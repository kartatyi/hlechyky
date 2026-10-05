using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Спільне господарство столів родини «Дуель» (Дуель, Перестрілка, Турнір стрільців): опції обманок, сигналів і
/// пінгу, рекорди «найшвидшої руки», середня реакція кожного за партію, заміри пінгу. Кожна гра тримає свій екземпляр.
/// </summary>
public sealed class DuelKit(Game game, int seats)
{
    public static readonly GameOption BaitOption = new("bait", "Обманки",
        [("off", "без обманок"), ("on", "інколи кричать «ВОДА!», «ВОРОН!»")], "off");
    public static readonly GameOption SignalOption = new("signal", "Сигнал",
        [("word", "напис «ВОГОНЬ!»"), ("mix", "різний: дзвін, сонце, небо")], "word");
    public static readonly GameOption PingOption = new("ping", "Пінг",
        [("off", "без поправки"), ("on", "з поправкою (до 80 мс)")], "off");

    /// <summary>Як часто в «Готуйсь…» і паузі між раундами слати кадр-замір пінгу.</summary>
    public const int PingEveryMs = 300;

    IRoomContext Ctx => game.Ctx;

    public bool Decoys { get; private set; }
    public bool MixSignals { get; private set; }
    public PingMeter? Ping { get; private set; }
    DuelRecords? _rec;

    readonly long?[] _best = new long?[seats];
    readonly long[] _sum = new long[seats];
    readonly int[] _cnt = new int[seats];
    long _fid;
    DateTimeOffset _pingNext;

    public void Configure(IReadOnlyDictionary<string, string> options, bool pingAllowed = true)
    {
        Decoys = options.TryGetValue("bait", out var b) && b == "on";
        MixSignals = options.TryGetValue("signal", out var s) && s == "mix";
        Ping = pingAllowed && options.TryGetValue("ping", out var p) && p == "on" ? new PingMeter(seats) : null;
        // Сервіс беремо тут, поза замком кімнати.
        _rec = Ctx.Services.GetService(typeof(DuelRecords)) as DuelRecords;
    }

    /// <summary>Нова партія: чиста статистика, пінг міряємо наново (місця після «Ще раз» обертаються).</summary>
    public void Reset()
    {
        Array.Clear(_best);
        Array.Clear(_sum);
        Array.Clear(_cnt);
        Ping?.Clear();
    }

    /// <summary>Підключити поєдинок до опцій, статистики й рекордів.</summary>
    public void Wire(DuelBout bout)
    {
        bout.Decoys = Decoys;
        bout.MixSignals = MixSignals;
        bout.Comp = Ping is null ? null : Ping.Comp;
        bout.Scored = (side, raw) => Scored(bout.Seats[side], raw);
        bout.Counted = (side, ms) => Count(bout.Seats[side], ms);
    }

    /// <summary>Найшвидша зарахована реакція місця за партію (мс з поправкою); null — ще не стріляв.</summary>
    public long? BestOf(int seat) => seat >= 0 && seat < seats ? _best[seat] : null;

    /// <summary>Постріл зараховано в партію: найшвидша й середня реакція місця.</summary>
    public void Count(int seat, long ms)
    {
        if (seat < 0 || seat >= seats) return;
        if (_best[seat] is not { } b || ms < b) _best[seat] = ms;
        _sum[seat] += ms;
        _cnt[seat]++;
    }

    /// <summary>Людський постріл (≥ <see cref="Duel.HumanFloorMs"/>): у таблицю реакцій і в рекорди.</summary>
    public string? Scored(int seat, long raw)
    {
        Ctx.Score(seat, raw);
        if (_rec is null) return null;
        var nick = Ctx.NickOf(seat) ?? "";
        var flag = _rec.Post(nick, raw, Ctx.Clock.UtcNow);
        if (flag == "week") Ctx.Log($"🏆 Найшвидша рука тижня — {nick}: {raw} мс ({game.Info.Title})");
        return flag;
    }

    /// <summary>Тик повернув кадр — занотувати, коли він пішов (для заміру пінгу).</summary>
    public TickResult Mark(TickResult r, DateTimeOffset now)
    {
        if (r == TickResult.None || Ping is null) return r;
        _fid++;
        Ping.Sent(_fid, now);
        _pingNext = now.AddMilliseconds(PingEveryMs);
        return r;
    }

    /// <summary>
    /// Кадр-замір: лише в «Готуйсь…» і в паузі між раундами — у «Цілься…» зайвий кадр підказав би, що «ВОГОНЬ!» ще не скоро.
    /// </summary>
    public TickResult PingTick(DuelPhase phase, DateTimeOffset now) =>
        Ping is not null && phase is DuelPhase.Ready or DuelPhase.Result && now >= _pingNext
            ? Mark(TickResult.FrameOnly, now) : TickResult.None;

    /// <summary>Відлуння кадру: <c>{ f: номер }</c>.</summary>
    public void Pong(int seat, JsonElement payload)
    {
        if (Ping is null || payload.ValueKind != JsonValueKind.Object) return;
        if (payload.TryGetProperty("f", out var f) && f.ValueKind == JsonValueKind.Number && f.TryGetInt64(out var fid))
            Ping.Pong(seat, fid, Ctx.Clock.UtcNow);
    }

    /// <summary>Поля виду: best, avg, рекорди, номер кадру й поправки пінгу.</summary>
    public void Fill(Dictionary<string, object?> o, bool full, IReadOnlyList<int> recSeats)
    {
        if (full) o["best"] = (long?[])_best.Clone();
        var avg = new long?[seats];
        for (var i = 0; i < seats; i++) avg[i] = _cnt[i] > 0 ? (long)Math.Round((double)_sum[i] / _cnt[i]) : null;
        o["avg"] = avg;
        if (full && _rec is not null)
        {
            var nicks = new string?[recSeats.Count];
            for (var i = 0; i < nicks.Length; i++) nicks[i] = Ctx.Seated(recSeats[i]) ? Ctx.NickOf(recSeats[i]) : null;
            o["rec"] = _rec.View(Ctx.Clock.UtcNow, nicks);
        }
        if (Ping is not null)
        {
            o["fid"] = _fid;
            var c = new int[seats];
            for (var i = 0; i < seats; i++) c[i] = Ping.Comp(i);
            o["ping"] = c;
        }
    }
}
