using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hlechyky.Games.Impl;

/// <summary>Раунд, що йде. Послідовність не зберігаємо — вона з <c>Seed</c> і <c>Cap</c> (<see cref="KavunyCore.Generate"/>).</summary>
public sealed class KavunyRound
{
    public int No { get; set; }
    /// <summary>«{кімната}:{epoch}» на мить старту — ключ каси й леджера.</summary>
    public string Table { get; set; } = "";
    public string Seed { get; set; } = "";
    public string Hash { get; set; } = "";
    public int Stake { get; set; }
    /// <summary>Автозабір, соті (null — нема).</summary>
    public int? Auto { get; set; }
    public bool AutoCut { get; set; }
    /// <summary>З якої мілісекунди (від t0) діє авторізання: ввімкнув посеред раунду — старше вже не ріже.</summary>
    public int AutoCutFrom { get; set; }
    /// <summary>Стеля, соті — з конфігу на мить старту.</summary>
    public int Cap { get; set; }
    /// <summary>Мить першого кидка (старт + <see cref="KavunyCore.LeadMs"/>).</summary>
    public DateTimeOffset T0 { get; set; }
    /// <summary>Розрізані овочі за порядком розрізу.</summary>
    public List<int> Cut { get; set; } = [];
    /// <summary>Множник гравця, соті: 100 + Σ приростів розрізаного.</summary>
    public int M { get; set; } = 100;
    /// <summary>Скільки овочів уже показано (вилетіли) — щоб вид ішов лише на новий кидок.</summary>
    public int Shown { get; set; }
    /// <summary>До якого овоча (індекс) авторізання вже дивилось.</summary>
    public int Scan { get; set; }
    public bool Glek { get; set; }

    KavunySeq? _seq;
    [JsonIgnore] public KavunySeq Seq => _seq ??= KavunyCore.Generate(Seed, Cap);
}

/// <summary>Чим скінчився раунд (для виду, перевірки й історії).</summary>
public sealed class KavunyLast
{
    public int No { get; set; }
    public string Hash { get; set; } = "";
    public string Seed { get; set; } = "";
    public int Stake { get; set; }
    /// <summary>Множник гравця наприкінці, соті.</summary>
    public int M { get; set; }
    public int Win { get; set; }
    /// <summary><c>cash</c> — забрав, <c>rot</c> — гнилий, <c>empty</c> — віз порожній, <c>void</c> — перервав перезапуск.</summary>
    public string Why { get; set; } = "";
    public int Cap { get; set; }
    /// <summary>Множник того, хто розрізав би все до кінця послідовності.</summary>
    public int Potential { get; set; }
    public string Codes { get; set; } = "";
    /// <summary>Скільки овочів уже вилетіло, коли раунд скінчився (решта — «що було б далі»).</summary>
    public int Seen { get; set; }
    public int Cuts { get; set; }
    public DateTimeOffset T0 { get; set; }
    /// <summary>Мс від t0, коли раунд скінчився.</summary>
    public int EndAt { get; set; }
    /// <summary>Гнилий, що скінчив раунд (клієнт докидає його на сцену й показує, як гепнувся).</summary>
    public KavunyFruit? Rot { get; set; }
}

public sealed class KavunyHist
{
    public double X { get; set; }
    public int Win { get; set; }
    public string Why { get; set; } = "";
}

public sealed class KavunyState
{
    public string Phase { get; set; } = Kavuny.Idle;
    public int No { get; set; }
    public string Epoch { get; set; } = "";
    /// <summary>Seed наступного раунду: таємниця, у виді лише його hash.</summary>
    public string NextSeed { get; set; } = "";
    public string NextHash { get; set; } = "";
    public KavunyRound? Round { get; set; }
    public KavunyLast? Last { get; set; }
    public List<KavunyHist> History { get; set; } = [];
    public int BestX { get; set; }
    public int BestWin { get; set; }
    public int? Wallet { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset? JournalAt { get; set; }
}

/// <summary>
/// «Кавуни на ярмарку» (docs/games/specs/kavuny.md) — соло-crash з ножем: Дядько Глек підкидає з воза овочі, ти ріжеш —
/// множник росте на приріст кожного розрізаного; гнилий гарбуз — кінець, «Забрати» — будь-коли до нього. Уся послідовність
/// вирішена на старті з seed (hash видно до ставки, seed — після). Розріз приймається, лише поки овоч у повітрі за розкладом
/// сервера (§5). Гроші — через <see cref="KavunyBook"/>: списання на старті, розрахунок — у черзі каси.
/// </summary>
public sealed class Kavuny : Game
{
    public const int TickMs = 50;
    public const int HistoryLen = 12, StripLen = 8, MaxCutIds = 64;
    public const int BigCents = 1_000;
    public static readonly TimeSpan JournalGap = TimeSpan.FromMinutes(5);

    public const string Idle = "idle", Fly = "fly";
    public const string ActStart = "start", ActCut = "cut", ActCash = "cash", ActAuto = "auto", ActAutoCut = "autocut";
    public const string Cash = "cash", Rotten = "rot", Empty = "empty", Void = "void";

    public const string ClosedText = "Каса зачинена — спробуй трохи згодом";
    public const string OffText = "Ярмарок зачинено — Глек поїхав по кавуни";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override GameInfo Info { get; } = new(
        "kavuny", "Кавуни на ярмарку", "кавуни на ярмарку", GameGroup.Solo, 1, 1, TickMs: TickMs, Start: StartMode.Immediate,
        Private: true, Persistent: true, Score: ScoreOrder.HigherIsBetter,
        Hint: "Глек підкидає з воза кавуни й гарбузи — ріж серпом, множник росте. Гнилий гарбуз — усе пропало, тож забирай вчасно");

    KavunyState S = new();
    KavunyBook? _book;
    bool _dirty;

    public KavunyState State => S;
    DateTimeOffset Now => Ctx.Clock.UtcNow;
    KavunyOptions Opts => _book?.Options ?? new KavunyOptions();

    public override string SeatName(int seat) => "ярмарок";

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        try { _book = Ctx?.Services?.GetService<KavunyBook>(); }
        catch { _book = null; }
    }

    /// <summary>
    /// Чистий прилавок. Історія, рекорди й номер раунду переживають «Ще раз»; раунд, що «йшов» (людина встала посеред
    /// нього — каркас закрив кімнату), — анулюємо через касу.
    /// </summary>
    public override void Start()
    {
        var keep = S;
        if (keep.Phase == Fly && keep.Round is { } r)
            _book?.Settle(new KavunyRec(r.Table, r.No, Ctx.NickOf(0) ?? "", r.Stake, r.Seed, Now, null));
        S = new KavunyState
        {
            No = keep.No, History = keep.History, BestX = keep.BestX, BestWin = keep.BestWin, Last = keep.Last,
            JournalAt = keep.JournalAt, Epoch = NewEpoch(),
        };
        NextSeed();
        _dirty = true;
    }

    public override object? Frame() => null;

    // ---------- дії ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (_book is null) return ActResult.Fail(ClosedText);
        if (action is not (ActStart or ActCut or ActCash or ActAuto or ActAutoCut)) return ActResult.Fail("Тут так не ходять");
        if (Ctx.NickOf(seat) is not { } nick) return ActResult.Fail("Тут так не ходять");
        var now = Now;
        Advance(now);
        var r = action switch
        {
            ActStart => StartRound(nick, payload, now),
            ActCut => CutMany(payload, now),
            ActCash => CashNow(now),
            ActAuto => SetAuto(payload),
            _ => SetAutoCut(payload, now),
        };
        if (r.Ok) _dirty = true;
        return r;
    }

    ActResult StartRound(string nick, JsonElement payload, DateTimeOffset now)
    {
        if (S.Phase == Fly) return ActResult.Fail("Раунд іще йде — ріж або забирай");
        var o = Opts;
        if (!o.Enabled) return ActResult.Fail(OffText);
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("amount", out var a) || a.ValueKind != JsonValueKind.Number
            || !a.TryGetInt32(out var amount)) return ActResult.Fail("Ставка — ціле число черепків");
        if (amount < o.MinBet) return ActResult.Fail($"Найменша ставка — {o.MinBet} 🏺");
        if (o.MaxBet > 0 && amount > o.MaxBet) return ActResult.Fail($"Найбільша ставка — {o.MaxBet} 🏺");
        var cap = KavunyCore.CapOf(o.MaxX * 100);
        int? auto = null;
        if (payload.TryGetProperty("auto", out var ae) && ae.ValueKind != JsonValueKind.Null)
        {
            if (ReadAuto(ae, cap, out auto) is { } bad) return ActResult.Fail(bad);
        }
        var autoCut = payload.TryGetProperty("autocut", out var ce) && ce.ValueKind == JsonValueKind.True;
        var wallet = _book!.Balance(nick);
        S.Wallet = wallet;
        if (amount > wallet) return ActResult.Fail($"Бракує черепків: у гаманці {Math.Max(0, wallet)}");

        if (string.IsNullOrEmpty(S.NextSeed)) NextSeed();
        var no = S.No + 1;
        var table = $"{Ctx.RoomId}:{S.Epoch}";
        if (_book.Start(new KavunyRec(table, no, nick, amount, S.NextSeed, now, null)) is { } refused)
        {
            S.Wallet = _book.Balance(nick);
            return ActResult.Fail(refused);
        }
        S.No = no;
        S.Round = new KavunyRound
        {
            No = no, Table = table, Seed = S.NextSeed, Hash = S.NextHash, Stake = amount, Auto = auto, AutoCut = autoCut,
            Cap = cap, T0 = now.AddMilliseconds(KavunyCore.LeadMs),
        };
        S.Phase = Fly;
        S.Wallet = wallet - amount;
        S.Note = null;
        NextSeed();   // наступний раунд — новий seed; його hash людина побачить, коли цей скінчиться
        return ActResult.Done;
    }

    /// <summary>
    /// Розріз (реалтайм-ввід, пачкою): <c>{ ids: [3, 4] }</c>. Овоч ріжеться, лише якщо вже вилетів і ще в повітрі за
    /// розкладом сервера з запасом на мережу: <c>at ≤ t ≤ at + FlyMs + LagMs</c>, де t — мить, коли сервер отримав намір
    /// (§5). Гнилий не ріжеться; майбутній, уже розрізаний, чужий номер — мовчки ні.
    /// </summary>
    ActResult CutMany(JsonElement payload, DateTimeOffset now)
    {
        if (S.Phase != Fly || S.Round is not { } r) return ActResult.Fail("Нема чого різати");
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("ids", out var ids) || ids.ValueKind != JsonValueKind.Array)
            return ActResult.Fail("Що різати?");
        var t = Ms(r, now);
        var any = false;
        var n = 0;
        foreach (var e in ids.EnumerateArray())
        {
            if (++n > MaxCutIds) break;
            if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out var id)) continue;
            if (CanCut(r, id, t))
            {
                CutOne(r, id, t, now);
                any = true;
                if (S.Phase != Fly) break;   // автозабір спрацював
            }
        }
        return any ? ActResult.Done : ActResult.Fail("Не влучив");
    }

    static bool CanCut(KavunyRound r, int id, int t)
    {
        var seq = r.Seq;
        if (id < 1 || id > seq.Fruits.Count) return false;
        var f = seq.Fruits[id - 1];
        if (f.Rotten || t < f.At || t > f.At + KavunyCore.FlyMs + KavunyCore.LagMs || t >= seq.EndAt) return false;
        return !r.Cut.Contains(id);
    }

    void CutOne(KavunyRound r, int id, int t, DateTimeOffset now)
    {
        var f = r.Seq.Fruits[id - 1];
        r.Cut.Add(id);
        r.M += f.Inc;
        if (f.Code == 'K') r.Glek = true;
        _dirty = true;
        if (r.Auto is { } a && r.M >= a) EndRound(Cash, now, t);
    }

    ActResult CashNow(DateTimeOffset now)
    {
        if (S.Phase != Fly || S.Round is not { } r)
            return ActResult.Fail(S.Last is { Why: Rotten } ? "Пізно — гнилий уже летить" : "Раунд не йде");
        if (r.M <= 100) return ActResult.Fail("Спершу розріж хоч щось");
        var win = KavunyCore.Win(r.Stake, Math.Min(r.M, r.Cap));
        EndRound(Cash, now, Ms(r, now));
        return ActResult.Accept($"Забрав ×{KavunyCore.Fmt(Math.Min(r.M, r.Cap))}: +{win} 🏺");
    }

    ActResult SetAuto(JsonElement payload)
    {
        if (S.Phase != Fly || S.Round is not { } r) return ActResult.Fail("Раунд не йде");
        int? auto = null;
        var x = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("x", out var xe) ? xe : default;
        if (x.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) && ReadAuto(x, r.Cap, out auto) is { } bad) return ActResult.Fail(bad);
        r.Auto = auto;
        // Поставив нижче, ніж уже є, — це «забрати зараз».
        if (auto is { } a && r.M >= a && r.M > 100) EndRound(Cash, Now, Ms(r, Now));
        return ActResult.Done;
    }

    ActResult SetAutoCut(JsonElement payload, DateTimeOffset now)
    {
        if (S.Phase != Fly || S.Round is not { } r) return ActResult.Fail("Раунд не йде");
        var on = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("on", out var oe) && oe.ValueKind == JsonValueKind.True;
        if (on == r.AutoCut) return ActResult.Done;
        r.AutoCut = on;
        if (!on) return ActResult.Done;
        var t = Ms(r, now);
        r.AutoCutFrom = t;
        // Те, що вже в повітрі, — під ніж одразу.
        for (var i = 0; i < r.Seq.Fruits.Count && S.Phase == Fly && r.Seq.Fruits[i].At <= t; i++)
            if (CanCut(r, i + 1, t)) CutOne(r, i + 1, t, now);
        return ActResult.Done;
    }

    static string? ReadAuto(JsonElement e, int cap, out int? cents)
    {
        cents = null;
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out var x) || KavunyCore.Cents(x) is not { } c)
            return "Автозабір — число, як 2 чи 1.5";
        if (c < KavunyCore.MinAuto) return "Автозабір — від ×1,01";
        if (c > cap) return $"Автозабір — до ×{cap / 100}";
        cents = c;
        return null;
    }

    // ---------- час ----------

    public override TickResult Tick()
    {
        Advance(Now);
        if (!_dirty) return TickResult.None;
        _dirty = false;
        return new TickResult(false, true);
    }

    static int Ms(KavunyRound r, DateTimeOffset now) => (int)Math.Clamp((now - r.T0).TotalMilliseconds, -1e9, 1e9);

    /// <summary>
    /// Прожити раунд до <paramref name="now"/> за розкладом: показати вилетілі овочі, авторізання (кожен — у свою мить
    /// <c>at + AutoCutMs</c>, з автозабором на ній), і кінець — виліт гнилого чи «віз порожній». Події — у порядку часу,
    /// тож рідкий тік чи пізня дія дають той самий підсумок, що й частий.
    /// </summary>
    void Advance(DateTimeOffset now)
    {
        if (S.Phase != Fly || S.Round is not { } r) return;
        var seq = r.Seq;
        var t = Ms(r, now);
        if (r.AutoCut)
        {
            while (r.Scan < seq.Fruits.Count && S.Phase == Fly)
            {
                var f = seq.Fruits[r.Scan];
                var at = f.At + KavunyCore.AutoCutMs;
                if (at > t || at >= seq.EndAt) break;
                r.Scan++;
                if (at >= r.AutoCutFrom && CanCut(r, f.Id, at)) CutOne(r, f.Id, at, r.T0.AddMilliseconds(at));
            }
            if (S.Phase != Fly) return;
        }
        var shown = 0;
        while (shown < seq.Fruits.Count && seq.Fruits[shown].At <= t) shown++;
        if (shown != r.Shown)
        {
            r.Shown = shown;
            _dirty = true;
        }
        if (t >= seq.EndAt) EndRound(seq.Rotten ? Rotten : Empty, r.T0.AddMilliseconds(seq.EndAt), seq.EndAt);
    }

    /// <summary>
    /// Кінець раунду: забрав / гнилий / віз порожній. Каса — у черзі (поза замком), стан — у сховище теж з черги
    /// (тік сам не пише), ачівки, таблиця, Журнал.
    /// </summary>
    void EndRound(string why, DateTimeOffset now, int t)
    {
        if (S.Round is not { } r) return;
        var m = Math.Min(r.M, r.Cap);
        var win = why == Rotten ? 0 : KavunyCore.Win(r.Stake, m);
        var seq = r.Seq;
        var seen = 0;
        while (seen < seq.Fruits.Count && seq.Fruits[seen].At <= t) seen++;
        S.Last = new KavunyLast
        {
            No = r.No, Hash = r.Hash, Seed = r.Seed, Stake = r.Stake, M = why == Rotten ? 0 : m, Win = win, Why = why, Cap = r.Cap,
            Potential = seq.Potential, Codes = seq.Codes, Seen = why == Rotten ? seq.Fruits.Count : seen, Cuts = r.Cut.Count,
            T0 = r.T0, EndAt = t, Rot = why == Rotten ? seq.Fruits[^1] : null,
        };
        S.Round = null;
        S.Phase = Idle;
        S.Wallet = S.Wallet is { } w ? (int)Math.Min(int.MaxValue, (long)w + win) : null;
        S.History.Insert(0, new KavunyHist { X = KavunyCore.X(S.Last.M), Win = win, Why = why });
        if (S.History.Count > HistoryLen) S.History.RemoveRange(HistoryLen, S.History.Count - HistoryLen);
        _dirty = true;

        _book?.Settle(new KavunyRec(r.Table, r.No, Ctx.NickOf(0) ?? "", r.Stake, r.Seed, now, win));
        if (r.Glek) Ctx.Award(0, 0, "ach:kavuny-glek");
        if (why != Rotten && win > r.Stake)
        {
            if (m > S.BestX) S.BestX = m;
            if (win > S.BestWin) S.BestWin = win;
            Ctx.Score(0, win - r.Stake);
            if (m >= BigCents)
            {
                Ctx.Award(0, 0, "ach:kavuny-10");
                if (S.JournalAt is not { } at || now - at >= JournalGap)
                {
                    S.JournalAt = now;
                    Ctx.Log($"🍉 Кавуни на ярмарку: {Ctx.NickOf(0)} — ×{KavunyCore.Fmt(m)}, +{win} 🏺");
                }
            }
        }
        if (_book is not null && Ctx.NickOf(0) is { } nick) _book.Keep(SoloKey(Rooms.NickKey(nick), Ctx.Clock), Save()!);
    }

    // ---------- вид ----------

    public override object View(int? seat)
    {
        var now = Now;
        var o = Opts;
        var r = S.Round;
        object? round = null;
        if (S.Phase == Fly && r is not null)
        {
            var t = Ms(r, now);
            var seq = r.Seq;
            var fruits = new List<object>();
            var from = t - KavunyCore.FlyMs - KavunyCore.LagMs - 300;
            for (var i = 0; i < seq.Fruits.Count && seq.Fruits[i].At <= t; i++)
            {
                var f = seq.Fruits[i];
                if (f.At < from) continue;
                fruits.Add(new { id = f.Id, k = f.Code.ToString(), inc = f.Inc, at = f.At, x0 = f.X0, x1 = f.X1, h = f.H, spin = f.Spin, cut = r.Cut.Contains(f.Id) });
            }
            var strip = r.Cut.Skip(Math.Max(0, r.Cut.Count - StripLen)).Select(id => seq.Fruits[id - 1].Inc).ToList();
            round = new
            {
                no = r.No, stake = r.Stake, auto = r.Auto is { } a ? KavunyCore.X(a) : (double?)null, autocut = r.AutoCut,
                t0 = r.T0, m = KavunyCore.X(r.M), win = KavunyCore.Win(r.Stake, Math.Min(r.M, r.Cap)), cap = KavunyCore.X(r.Cap),
                cuts = r.Cut.Count, strip, fruits, tier = KavunyCore.Tier(Potential(r, t)),
            };
        }
        var l = S.Last;
        object? last = null;
        if (l is not null)
        {
            var rot = l.Rot;
            last = new
            {
                no = l.No, hash = l.Hash, seed = l.Seed, stake = l.Stake, m = KavunyCore.X(l.M), win = l.Win, why = l.Why,
                cap = KavunyCore.X(l.Cap), potential = KavunyCore.X(l.Potential), codes = l.Codes, seen = l.Seen, cuts = l.Cuts,
                t0 = l.T0, endAt = l.EndAt,
                rot = rot is null ? null : new { id = rot.Id, at = rot.At, x0 = rot.X0, x1 = rot.X1, h = rot.H, spin = rot.Spin },
            };
        }
        return new
        {
            phase = S.Phase,
            now,
            on = o.Enabled && _book is not null,
            hash = S.Phase == Fly && r is not null ? r.Hash : S.NextHash,
            limits = new { min = o.MinBet, max = o.MaxBet },
            cap = KavunyCore.X(KavunyCore.CapOf(o.MaxX * 100)),
            wallet = S.Wallet is { } w ? Math.Max(0, w) : (int?)null,
            note = S.Note,
            round,
            last,
            history = S.History.Select(h => new { x = h.X, win = h.Win, why = h.Why }).ToList(),
            best = new { x = KavunyCore.X(S.BestX), win = S.BestWin },
            k = new
            {
                lead = KavunyCore.LeadMs, wave = KavunyCore.WaveMs, stagger = KavunyCore.StaggerMs, fly = KavunyCore.FlyMs,
                lag = KavunyCore.LagMs, autoCut = KavunyCore.AutoCutMs,
            },
        };
    }

    /// <summary>Множник «ідеального різника» на мить t — для ярусу на виді (те, що вже вилетіло, гравець і так бачив).</summary>
    static int Potential(KavunyRound r, int t)
    {
        var p = 100;
        foreach (var f in r.Seq.Fruits)
        {
            if (f.At > t || f.Rotten) break;
            p += f.Inc;
        }
        return p;
    }

    // ---------- збереження ----------

    public override string? Save() => JsonSerializer.Serialize(S, Json);

    /// <summary>
    /// Відновлення (OpenSolo після прибирання кімнати чи перезапуску). Новий epoch і новий seed наступного раунду (старий
    /// міг уже засвітитись). Раунд, що «йшов», доводимо через касу: запис ще лежить — анулюємо (каса поверне ставку, а якщо
    /// підсумок у записі вже є — заплатить за ним); запису нема — його вже розраховано, лише закриваємо.
    /// </summary>
    public override void Load(string json)
    {
        var s = JsonSerializer.Deserialize<KavunyState>(json, Json) ?? throw new InvalidOperationException("порожній стан кавунів");
        if (s.Phase is not (Idle or Fly) || (s.Phase == Fly && s.Round is null)) throw new InvalidOperationException($"невідома фаза кавунів: {s.Phase}");
        s.Epoch = NewEpoch();
        S = s;
        NextSeed();
        if (S.Phase == Fly && S.Round is { } r)
        {
            var held = _book?.Holds(r.Table, r.No);
            int? due = null;
            if (held != false && _book is not null)
            {
                try { due = _book.Pending().FirstOrDefault(p => p.Table == r.Table && p.No == r.No)?.Return; }
                catch { due = null; }
                _book.Settle(new KavunyRec(r.Table, r.No, Ctx.NickOf(0) ?? "", r.Stake, r.Seed, Now, null));
            }
            S.Last = new KavunyLast
            {
                No = r.No, Hash = r.Hash, Seed = r.Seed, Stake = r.Stake, M = r.M, Win = 0, Why = Void, Cap = r.Cap,
                Potential = r.Seq.Potential, Codes = r.Seq.Codes, Seen = 0, Cuts = r.Cut.Count, T0 = r.T0, EndAt = 0,
            };
            S.Round = null;
            S.Phase = Idle;
            S.Note = held == false ? $"Раунд №{r.No} уже розраховано, поки тебе не було, — глянь історію гаманця"
                : due is null ? $"Раунд №{r.No} перервав перезапуск — ставку ({r.Stake} 🏺) повернуто"
                : due > 0 ? $"Раунд №{r.No} скінчився, поки сайт перезапускався: +{due} 🏺"
                : $"Раунд №{r.No} скінчився гнилим, поки сайт перезапускався";
        }
        if (_book is not null && Ctx?.NickOf(0) is { } nick) S.Wallet = _book.Balance(nick);
        _dirty = true;
    }

    /// <summary>Чи кімната чекає саме на цей раунд — тоді каса його не чіпає.</summary>
    public bool Holds(string table, int no) =>
        S.Phase == Fly && S.Round is { } r && r.No == no && string.Equals(r.Table, table, StringComparison.Ordinal);

    // ---------- дрібниці ----------

    void NextSeed()
    {
        S.NextSeed = KavunyCore.NewSeed(Ctx.Seeded ? Ctx.Rng : null);
        S.NextHash = KavunyCore.Hash(S.NextSeed);
    }

    string NewEpoch() => Ctx.Seeded
        ? ((uint)Ctx.Rng.Next(int.MinValue, int.MaxValue)).ToString("x8")
        : System.Security.Cryptography.RandomNumberGenerator.GetHexString(8, lowercase: true);
}
