using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Знімок «Хуторів» для перезапуску сервера (контракт §8 «Перезапуск сервера»). Плаский, з версією <see cref="V"/>:
/// нове поле — нова властивість (стара версія знімка дасть типове значення), несумісна зміна — <c>V</c> + 1.
/// Опцій тут нема: каркас кличе <see cref="Khutory.Configure"/> з опціями столу ще до Start/Load, а ціль і дружній
/// Кармелюк живуть у ядрі. Часи — абсолютні; <see cref="Khutory.Resumed"/> зсуває їх на паузу.
/// </summary>
public sealed class KhutorySnap
{
    public int V { get; set; } = 1;
    public KhCoreSnap Core { get; set; } = new();

    // Місця.
    public string?[] Bots { get; set; } = [];
    public string?[] Nicks { get; set; } = [];
    public bool[] Away { get; set; } = [];
    public int[] Missed { get; set; } = [];

    // Строки.
    public DateTimeOffset? Deadline { get; set; }
    public int DeadlineMs { get; set; }
    public string DeadlineKey { get; set; } = "";
    public string MainKey { get; set; } = "";
    public DateTimeOffset? MainDeadline { get; set; }
    public DateTimeOffset? BotAt { get; set; }
    public string GraceKey { get; set; } = "";
    public DateTimeOffset GraceUntil { get; set; }

    // Кінець.
    public bool Announced { get; set; }
    public string Reason { get; set; } = "points";

    // Пам'ять Глека-гравця: своя пропозиція, з якого часу чекає відповідей, у якому ході вже пропонував.
    public int[] BotPending { get; set; } = [];
    public DateTimeOffset[] BotSince { get; set; } = [];
    public long[] BotOffered { get; set; } = [];

    // Глек-ведучий: що вже казав за партію (запобіжник частоти — наново).
    public int[][] HandsWas { get; set; } = [];
    public bool SetupSaid { get; set; }
    public bool CityYet { get; set; }
    public bool LastSeven { get; set; }
    public int? LongestWas { get; set; }
}

public sealed partial class Khutory
{
    static readonly JsonSerializerOptions SnapJson = new(JsonSerializerDefaults.Web);

    /// <summary>Save тримає всю партію: ядро, місця з Глеками, строки, пам'ять Глеків — партія переживає деплой.</summary>
    public override bool Resumable => true;

    public override string? Save()
    {
        if (_core.Seats == 0) return null;   // стіл ще не грав
        var snap = new KhutorySnap
        {
            Core = _core.Snapshot(),
            Bots = [.. _bots],
            Nicks = [.. _nicks],
            Away = [.. _away],
            Missed = [.. _missed],
            Deadline = _deadline,
            DeadlineMs = _deadlineMs,
            DeadlineKey = _deadlineKey,
            MainKey = _mainKey,
            MainDeadline = _mainDeadline,
            BotAt = _botAt,
            GraceKey = _graceKey,
            GraceUntil = _graceUntil,
            Announced = _announced,
            Reason = _reason,
            HandsWas = [.. _handsWas.Select(h => (int[])h.Clone())],
            SetupSaid = _setupSaid,
            CityYet = _cityYet,
            LastSeven = _lastSeven,
            LongestWas = _longestWas,
        };
        _brain.SaveTo(snap);
        return JsonSerializer.Serialize(snap, SnapJson);
    }

    /// <summary>
    /// Партія зі знімка — поверх свіжого <see cref="Start"/> (каркас кличе Start + Load + Resumed). Голос Глека-ведучого
    /// починає наново, але події до знімка вважає почутими: історію вдруге не озвучує.
    /// </summary>
    public override void Load(string json)
    {
        var snap = JsonSerializer.Deserialize<KhutorySnap>(json, SnapJson)
            ?? throw new InvalidOperationException("порожній знімок «Хуторів»");
        if (snap.V != 1) throw new InvalidOperationException($"знімок «Хуторів» версії {snap.V} — ця збірка вміє лише 1");
        const int n = KhutoryCore.MaxSeats;
        _bots = Fit(snap.Bots, n);
        _nicks = Fit(snap.Nicks, n);
        Array.Copy(Fit(snap.Away, n), _away, n);
        Array.Copy(Fit(snap.Missed, n), _missed, n);
        _core = KhutoryCore.FromSnapshot(snap.Core, Ctx.Rng);
        _core.NameOf = Nick;
        _deadline = snap.Deadline;
        _deadlineMs = snap.DeadlineMs;
        _deadlineKey = snap.DeadlineKey;
        _mainKey = snap.MainKey;
        _mainDeadline = snap.MainDeadline;
        _botAt = snap.BotAt;
        _graceKey = snap.GraceKey;
        _graceUntil = snap.GraceUntil;
        _announced = snap.Announced;
        _reason = snap.Reason;
        _brain = new KhutoryBot();
        _brain.LoadFrom(snap);
        _glek.Reset();
        _heardSeq = _core.Seq;
        for (var s = 0; s < n; s++)
            _handsWas[s] = s < snap.HandsWas.Length ? Fit(snap.HandsWas[s], 5) : (int[])_core.Hand[s].Clone();
        _setupSaid = snap.SetupSaid;
        _cityYet = snap.CityYet;
        _lastSeven = snap.LastSeven;
        _longestWas = snap.LongestWas;
    }

    /// <summary>Сервер стояв <paramref name="pause"/>: строк ходу, Глеки й пільга задрімалому чекають рівно стільки ж.</summary>
    public override void Resumed(TimeSpan pause)
    {
        if (pause <= TimeSpan.Zero) return;
        if (_deadline is { } d) _deadline = d + pause;
        if (_mainDeadline is { } md) _mainDeadline = md + pause;
        if (_botAt is { } b) _botAt = b + pause;
        if (_graceKey != "") _graceUntil += pause;
        _brain.Shift(pause);
    }

    static T[] Fit<T>(T[]? a, int n)
    {
        var r = new T[n];
        if (a is not null) Array.Copy(a, r, Math.Min(n, a.Length));
        return r;
    }
}

public sealed partial class KhutoryBot
{
    /// <summary>Пам'ять пропозицій — у знімок партії (без неї Глек після перезапуску вдруге пропонував би той самий обмін).</summary>
    internal void SaveTo(KhutorySnap s)
    {
        s.BotPending = [.. _pending];
        s.BotSince = [.. _since];
        s.BotOffered = [.. _offered];
    }

    internal void LoadFrom(KhutorySnap s)
    {
        for (var i = 0; i < KhutoryCore.MaxSeats; i++)
        {
            _pending[i] = i < s.BotPending.Length ? s.BotPending[i] : -1;
            _since[i] = i < s.BotSince.Length ? s.BotSince[i] : default;
            _offered[i] = i < s.BotOffered.Length ? s.BotOffered[i] : -1;
        }
    }

    /// <summary>Перезапуск: 10 с очікування відповідей на пропозицію Глека не згоряють, поки сервер стояв.</summary>
    internal void Shift(TimeSpan pause)
    {
        for (var i = 0; i < KhutoryCore.MaxSeats; i++)
            if (_pending[i] >= 0) _since[i] += pause;
    }
}
