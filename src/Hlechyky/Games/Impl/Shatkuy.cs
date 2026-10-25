using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Спільне для «Шаткуй!» (docs/games/specs/shatkuy.md): Дядько Глек підкидає з воза плоди, людина ріже змахом. Що й коли
/// летить — розклад сервера (<see cref="ShatkuyPlan"/>); браузер шле лише «розрізав такі-то цим змахом», а сервер
/// перевіряє, що плід справді був у повітрі за його годинником, і сам рахує очки, комбо, промахи й особливі плоди.
/// </summary>
public abstract class ShatkuyGame : Game
{
    public const int TickMs = 50, MaxCuts = 64;
    public const string PhReady = "ready", PhGo = "go", PhOver = "over";
    public const string WhyMiss = "miss", WhyGlek = "glek", WhyTime = "time", WhyLeft = "left";
    /// <summary>Пороги ачівок (specs/shatkuy.md §6).</summary>
    public const int ComboAch = 6, ClassicAch = 250, BigAch = 25, CleanAch = 150;
    /// <summary>Черепків за кожні стільки очок (через денну стелю нагород), не більше <see cref="MaxShards"/> за партію.</summary>
    public const int PointsPerShard = 50, MaxShards = 10;

    protected abstract ShatkuyMode Mode { get; }
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected string _phase = PhReady;
    DateTimeOffset _t0;
    ShatkuyPlan? _plan;
    readonly Dictionary<int, ShatkuyFruit> _byId = [];
    /// <summary>Ще не розрізані й не впалі — їх тік перевіряє на промах.</summary>
    readonly List<ShatkuyFruit> _air = [];
    /// <summary>Відомі, але ще за обрієм виду (At &gt; g + LookMs): вид треба розіслати, щойно котрийсь увійде в обрій.</summary>
    readonly List<ShatkuyFruit> _soon = [];
    readonly HashSet<int> _cut = [], _missed = [], _bigWs = [];
    readonly Dictionary<int, int> _combo = [];
    readonly List<(int S, int E)> _frost = [], _double = [];
    int _planSeen, _showers, _bigAt = -1_000_000;
    protected int _score, _misses, _bestCombo, _gleks, _cuts, _bigHits, _endT;
    int _nextLife = ShatkuyCore.LifeEvery;
    protected string _why = "";
    protected int _best, _games;
    protected bool _record;
    bool _dirty;

    public string Phase => _phase;
    public int Score => _score;
    public int Misses => _misses;
    public int BestCombo => _bestCombo;
    public int BigHits => _bigHits;
    public string Why => _why;
    public int Best => _best;
    public IReadOnlyCollection<ShatkuyFruit> Fruits => _byId.Values;
    public bool IsCut(int id) => _cut.Contains(id);
    public IReadOnlyList<(int S, int E)> Frost => _frost;
    /// <summary>Ігрова мілісекунда зараз (0 — партія не йде).</summary>
    public int GameNow => _phase == PhGo ? G(T(Now)) : 0;
    DateTimeOffset Now => Ctx.Clock.UtcNow;

    public override string SeatName(int seat) => "шаткар";

    public override void Start() => Reset();

    protected void Reset()
    {
        _phase = PhReady;
        _plan = null;
        _byId.Clear(); _air.Clear(); _soon.Clear(); _cut.Clear(); _missed.Clear(); _bigWs.Clear(); _combo.Clear(); _frost.Clear(); _double.Clear();
        _planSeen = _showers = 0;
        _bigAt = -1_000_000;
        _score = _misses = _bestCombo = _gleks = _cuts = _bigHits = _endT = 0;
        _nextLife = ShatkuyCore.LifeEvery;
        _why = "";
        _record = false;
        _dirty = true;
    }

    /// <summary>Сід розкладу: звичайні — новий щоразу, щоденна — від дня.</summary>
    protected virtual ulong NewSeed() => (ulong)Ctx.Rng.NextInt64(1, long.MaxValue);

    protected virtual string? CanGo() => null;

    public override object? Frame() => null;

    // ---------- дії ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (seat != 0) return ActResult.Fail("Ти тут не граєш");
        var now = Now;
        Advance(now);
        switch (action)
        {
            case "go":
                if (CanGo() is { } no) return ActResult.Fail(no);
                if (_phase != PhReady) return ActResult.Fail(_phase == PhGo ? "Уже ріжемо!" : "Партію скінчено — «Ще раз»");
                _plan = new ShatkuyPlan(Mode, NewSeed());
                _t0 = now.AddMilliseconds(ShatkuyCore.LeadMs);
                _phase = PhGo;
                Grow(0);
                _dirty = true;
                return ActResult.Done;
            case "cut":
                return CutMany(payload, now);
            default:
                return ActResult.Fail("Тут так не ходять");
        }
    }

    int T(DateTimeOffset now) => (int)Math.Clamp((now - _t0).TotalMilliseconds, -1e9, 1e9);
    int G(int t) => ShatkuyCore.Game(Math.Max(0, t), _frost);

    /// <summary>
    /// Розріз (реалтайм-ввід, пачкою): <c>{ c: [[id, w], …] }</c>, де w — номер змаху. Плід ріжеться, лише якщо вже вилетів
    /// і ще в повітрі за ігровим часом сервера (з запасом <see cref="ShatkuyCore.LagMs"/>); великий кавун — раз на змах.
    /// Майбутній, уже розрізаний, невідомий номер — мовчки ні.
    /// </summary>
    ActResult CutMany(JsonElement payload, DateTimeOffset now)
    {
        if (_phase != PhGo) return ActResult.Fail("Зараз нема чого різати");
        var t = T(now);
        if (t < -200) return ActResult.Fail("Ще не почали");
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("c", out var list) || list.ValueKind != JsonValueKind.Array)
            return ActResult.Fail("Що різати?");
        var g = G(t);
        if (Mode.Timed && g > Mode.LenMs + ShatkuyCore.LagMs) return ActResult.Fail("Час вийшов");
        Grow(g);
        var any = false;
        var n = 0;
        foreach (var e in list.EnumerateArray())
        {
            if (++n > MaxCuts || _phase != PhGo) break;
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() < 2) continue;
            if (!e[0].TryGetInt32(out var id) || !e[1].TryGetInt32(out var w)) continue;
            if (!_byId.TryGetValue(id, out var f) || g < f.At - 100 || g > f.Until) continue;
            if (Cut(f, w, t, g)) any = true;
        }
        if (any) _dirty = true;
        return any ? ActResult.Done : ActResult.Fail("Не влучив");
    }

    bool Cut(ShatkuyFruit f, int w, int t, int g)
    {
        var mult = Mode.Specials && ShatkuyCore.Inside(_double, t) ? 2 : 1;
        if (f.Big)
        {
            // удар — раз на змах і не частіше за BigGapMs (браузер дає кожному заходу леза свій номер)
            if (_bigHits >= ShatkuyCore.BigMaxHits || t - _bigAt < ShatkuyCore.BigGapMs || !_bigWs.Add(w)) return false;
            _bigAt = t;
            _bigHits++;
            _score += mult;
            return true;
        }
        if (!_cut.Add(f.Id)) return false;
        _air.Remove(f);
        if (f.Glek)
        {
            _gleks++;
            if (Mode.Lives) End(WhyGlek, t);
            else _score = Math.Max(0, _score - ShatkuyCore.GlekPenalty);
            return true;
        }
        _cuts++;
        var gain = 1;
        // комбо: третій плід одним змахом дає +3, кожен наступний +1 — разом комбо з n дає n очок зверху
        var c = _combo[w] = _combo.GetValueOrDefault(w) + 1;
        if (c == 3) gain += 3;
        else if (c > 3) gain += 1;
        _bestCombo = Math.Max(_bestCombo, c >= 3 ? c : 0);
        if (_combo.Count > 12) foreach (var old in _combo.Keys.Where(k => k < w - 8).ToList()) _combo.Remove(old);
        _score += gain * mult;
        switch (f.K)
        {
            case ShatkuyCore.Frost: ShatkuyCore.AddFrost(_frost, t); break;
            case ShatkuyCore.Double: ShatkuyCore.AddSpan(_double, t, ShatkuyCore.DoubleMs); break;
            case ShatkuyCore.Shower:
                foreach (var s in ShatkuyCore.ShowerOf(_plan!.Seed, ++_showers, g)) Put(s);
                break;
        }
        if (Mode.Lives)
            while (_score >= _nextLife)
            {
                if (_misses > 0) _misses--;
                _nextLife += ShatkuyCore.LifeEvery;
            }
        return true;
    }

    // ---------- час ----------

    public override TickResult Tick()
    {
        Advance(Now);
        if (!_dirty) return TickResult.None;
        _dirty = false;
        return new TickResult(false, true);
    }

    void Put(ShatkuyFruit f)
    {
        if (!_byId.TryAdd(f.Id, f)) return;
        if (!f.Big) _air.Add(f);
        _soon.Add(f);
        _dirty = true;
    }

    void Grow(int g)
    {
        if (_plan is null) return;
        _plan.Until(g + ShatkuyCore.LookMs);
        while (_planSeen < _plan.Fruits.Count) Put(_plan.Fruits[_planSeen++]);
        // плід хвилі, що вилітає пізніше за обрій, інакше доходив би лише з наступною хвилею — уже в польоті
        if (_soon.RemoveAll(f => f.At <= g + ShatkuyCore.LookMs) > 0) _dirty = true;
    }

    /// <summary>Прожити партію до <paramref name="now"/>: докинути розклад, порахувати впалі (у Класиці — промахи), кінець часу.</summary>
    void Advance(DateTimeOffset now)
    {
        if (_phase != PhGo) return;
        var t = T(now);
        var g = G(t);
        Grow(g);   // і під час відліку «Ану!» — перша хвиля теж приходить за обрій до вильоту
        if (t < 0) return;
        for (var i = _air.Count - 1; i >= 0 && _phase == PhGo; i--)
        {
            var f = _air[i];
            if (f.Until >= g) continue;
            _air.RemoveAt(i);
            if (!Mode.Lives || f.Glek) continue;
            _missed.Add(f.Id);
            _misses++;
            _dirty = true;
            if (_misses >= ShatkuyCore.Lives) End(WhyMiss, t);
        }
        if (_phase == PhGo && Mode.Timed && g >= Mode.LenMs + ShatkuyCore.LagMs) End(WhyTime, t);
    }

    /// <summary>Кінець партії: рекорд, ачівки, черепки, таблиця, Журнал.</summary>
    protected void End(string why, int t)
    {
        if (_phase != PhGo) return;
        Close(why, t);
        Report();
    }

    /// <summary>Кінець без подій каркаса (рекорд і підсумок) — для «пішов посеред партії», де Ctx нікуди не доносить.</summary>
    protected void Close(string why, int t)
    {
        _phase = PhOver;
        _why = why;
        _endT = t;
        _games++;
        _record = _score > _best;
        if (_record) _best = _score;
        _dirty = true;
    }

    protected void Achievements()
    {
        if (_bestCombo >= ComboAch) Ctx.Award(0, 0, "ach:shatkuy-combo");
        if (Mode == ShatkuyCore.Classic && _score >= ClassicAch) Ctx.Award(0, 0, "ach:shatkuy-classic");
        if (_bigHits >= BigAch) Ctx.Award(0, 0, "ach:shatkuy-big");
        if (Mode == ShatkuyCore.Fair && _gleks == 0 && _score >= CleanAch) Ctx.Award(0, 0, "ach:shatkuy-clean");
    }

    protected virtual void Report()
    {
        Achievements();
        Ctx.Score(0, _score);
        var shards = Math.Min(MaxShards, _score / PointsPerShard);
        if (shards > 0) Ctx.Award(0, shards, $"shatkuy:{Ctx.Round}");
        Ctx.Finish([0], $"{Info.Title}: {Ctx.NickOf(0)} — {_score} {Points(_score)}", new Dictionary<int, long> { [0] = _score });
    }

    public static string Points(int n) =>
        (n % 100) is >= 11 and <= 14 ? "очок" : (n % 10) switch { 1 => "очко", 2 or 3 or 4 => "очки", _ => "очок" };

    protected int NowT => T(Now);

    // ---------- вид ----------

    public override object View(int? seat) => BaseView();

    protected Dictionary<string, object?> BaseView()
    {
        var now = Now;
        var go = _phase == PhGo;
        var g = go ? G(T(now)) : 0;
        var fruits = go
            ? _byId.Values.Where(f => f.At <= g + ShatkuyCore.LookMs && f.Until + 400 >= g).OrderBy(f => f.Id).Select(f => new
            {
                id = f.Id, k = f.K.ToString(), at = f.At, d = f.D, x0 = R(f.X0), x1 = R(f.X1), h = R(f.H), sp = R(f.Spin),
                c = _cut.Contains(f.Id), m = _missed.Contains(f.Id),
            }).ToArray()
            : [];
        return new Dictionary<string, object?>
        {
            ["mode"] = Mode.Id,
            ["phase"] = _phase,
            ["turn"] = go ? 0 : null,
            ["now"] = now.ToString("o", CultureInfo.InvariantCulture),
            ["t0"] = _phase == PhReady ? null : _t0.ToString("o", CultureInfo.InvariantCulture),
            ["len"] = Mode.LenMs,
            ["lives"] = Mode.Lives ? ShatkuyCore.Lives : 0,
            ["misses"] = _misses,
            ["score"] = _score,
            ["best"] = _best,
            ["games"] = _games,
            ["combo"] = _bestCombo,
            ["gleks"] = _gleks,
            ["big"] = _bigHits,
            ["cuts"] = _cuts,
            ["frz"] = _frost.Select(s => new[] { s.S, s.E }).ToArray(),
            ["dbl"] = _double.Select(s => new[] { s.S, s.E }).ToArray(),
            ["fruits"] = fruits,
            ["end"] = _phase == PhOver ? new { why = _why, score = _score, best = _best, rec = _record, combo = _bestCombo, cuts = _cuts, big = _bigHits, gleks = _gleks, t = _endT } : null,
            ["k"] = new { lag = ShatkuyCore.LagMs, look = ShatkuyCore.LookMs, frost = ShatkuyCore.FrostRate, bigMax = ShatkuyCore.BigMaxHits, glek = Mode.Lives ? 0 : ShatkuyCore.GlekPenalty },
        };
    }

    static double R(double x) => Math.Round(x, 3);

    // ---------- збереження: рекорд і кількість партій ----------

    sealed record Saved(int Best, int Games);

    public override string? Save() => JsonSerializer.Serialize(new Saved(_best, _games), Json);

    public override void Load(string json)
    {
        try
        {
            if (JsonSerializer.Deserialize<Saved>(json, Json) is { } s) { _best = Math.Max(0, s.Best); _games = Math.Max(0, s.Games); }
        }
        catch (JsonException) { }
    }
}

/// <summary>«Шаткуй!» — Класика: три промахи — і кінець, глек — одразу кінець; кожні сто очок повертають життя.</summary>
public sealed class Shatkuy : ShatkuyGame
{
    protected override ShatkuyMode Mode => ShatkuyCore.Classic;

    public override GameInfo Info { get; } = new(
        "shatkuy", "Шаткуй!", "шаткуй", GameGroup.Solo, 1, 1, TickMs: TickMs, Start: StartMode.Immediate,
        Private: true, Persistent: true, Score: ScoreOrder.HigherIsBetter,
        Hint: "Дядько Глек підкидає з воза овочі — шаткуй змахом! Три пропущені — кінець, а глек не чіпай: розіб'єш — образиться");
}

/// <summary>«Шаткуй!» — Ярмарок: хвилина, без життів; мороз, ×2, злива й великий кавун наприкінці, глек — мінус очки.</summary>
public sealed class ShatkuyFair : ShatkuyGame
{
    protected override ShatkuyMode Mode => ShatkuyCore.Fair;

    public override GameInfo Info { get; } = new(
        "shatkuy-fair", "Шаткуй! Ярмарок", "шаткуй на ярмарку", GameGroup.Solo, 1, 1, TickMs: TickMs, Start: StartMode.Immediate,
        Private: true, Persistent: true, Score: ScoreOrder.HigherIsBetter, Client: "shatkuy",
        Hint: "Хвилина ярмарку: крижана слива сповільнює, золоте яблуко дає ×2, кукурудза кличе зливу, а наприкінці — великий кавун");
}

/// <summary>«Шаткуй!» — Дзен: півтори хвилини самих плодів, без глеків і промахів.</summary>
public sealed class ShatkuyZen : ShatkuyGame
{
    protected override ShatkuyMode Mode => ShatkuyCore.Zen;

    public override GameInfo Info { get; } = new(
        "shatkuy-zen", "Шаткуй! Дзен", "шаткуй у дзені", GameGroup.Solo, 1, 1, TickMs: TickMs, Start: StartMode.Immediate,
        Private: true, Persistent: true, Score: ScoreOrder.HigherIsBetter, Client: "shatkuy",
        Hint: "Півтори хвилини спокою: самі плоди, ні глеків, ні промахів — просто шаткуй");
}

// =============================================================================================
// Шаткуй дня
// =============================================================================================

/// <summary>
/// «Шаткуй дня»: 45 секунд, той самий розклад для всіх за київську добу (сід від дня), одна спроба. Щоденний каркас
/// (<see cref="IDailyGame"/>) дає «☀ Сьогодні», серію й щоденний глек; таблиця дня за очками — своя
/// (<see cref="ShatkuyDailyBoard"/>), як у «Кахлів дня».
/// </summary>
public sealed class ShatkuyDaily : ShatkuyGame, IDailyGame
{
    const string Puzzle = "shatkuy";
    public const string DoneToday = "Сьогодні вже нашаткували — новий віз після півночі. А поки — Класика чи Ярмарок";

    protected override ShatkuyMode Mode => ShatkuyCore.Daily;

    public override GameInfo Info { get; } = new(
        "shatkuy-daily", "Шаткуй дня", "шаткуй дня", GameGroup.Solo, 1, 1, TickMs: TickMs, Start: StartMode.Immediate,
        Private: true, Persistent: true, Client: "shatkuy",
        Hint: "Сорок п'ять секунд і один віз на всіх за добу, одна спроба. Хто сьогодні нашаткував найбільше? Таблиця дня — у грі");

    string _day = "";
    bool _reported;
    ShatkuyDailyBoard? _table;

    public bool Reported => _reported;
    public string Day => _day;

    public override string SoloKey(string nickKey, IClock clock) => $"daily:{Info.Id}:{Days.Today(clock)}:{nickKey}";

    public override void Configure(IReadOnlyDictionary<string, string> options) => _table = Ctx.Services.GetService<ShatkuyDailyBoard>();

    public override string? CanStart() => _reported && _day == Days.Today(Ctx.Clock) ? DoneToday : null;

    protected override string? CanGo() => _reported ? DoneToday : null;

    /// <summary>Пішов посеред партії — спроба зарахована з тим, що встиг; щоденний глек видамо з першого тіку нової кімнати.</summary>
    bool _owed;

    public override void Unwatched()
    {
        if (_phase != PhGo) return;
        Close(WhyLeft, NowT);
        Record();
        _owed = true;
    }

    public override TickResult Tick()
    {
        if (_owed && _reported)
        {
            _owed = false;
            Credit();
            return TickResult.Both;
        }
        return base.Tick();
    }

    public override void Start()
    {
        var today = Days.Today(Ctx.Clock);
        if (_reported && _day == today) return;   // «Ще раз» на дограному дні — лишаємо підсумок
        Reset();
        _day = today;
        _reported = false;
    }

    public static ulong DaySeed(string day) => (ulong)Days.Seed(Puzzle, day) * 0x9E3779B97F4A7C15UL + 7;

    protected override ulong NewSeed() => DaySeed(_day);

    protected override void Report()
    {
        Record();
        Credit();
    }

    void Record()
    {
        _reported = true;
        _table?.Record(_day, Ctx.NickOf(0) ?? "", _score, Marks(), Ctx.Clock.UtcNow);
    }

    void Credit()
    {
        Achievements();
        var nick = Ctx.NickOf(0) ?? "";
        // Спроба в щоденному каркасі одна; очки там не лягають (він рахує «менше — краще»), тож час — 0.
        Ctx.Score(0, 0, 1);
        Ctx.Award(0, 0, $"daily:{Info.Id}");
        Ctx.Finish([0], $"{Info.Title}: {nick} — {_score} {Points(_score)}", new Dictionary<int, long> { [0] = _score });
    }

    string Marks() => $"🔪 {_cuts} · комбо ×{Math.Max(_bestCombo, 1)} · 🍉 {_bigHits}" + (_gleks > 0 ? $" · 💥 {_gleks}" : "");

    public override object View(int? seat)
    {
        var v = BaseView();
        var top = _table?.Top(_day) ?? [];
        var nick = Ctx.NickOf(0);
        var mine = nick is null ? -1 : top.ToList().FindIndex(r => ShatkuyDailyBoard.Key(r.Nick) == ShatkuyDailyBoard.Key(nick));
        if (_reported && _phase != PhOver) v["phase"] = PhOver;
        v["daily"] = new
        {
            day = _day,
            no = _day.Length == 0 ? 0 : Days.Number(_day),
            done = _reported,
            // Таблицю дня — лише після своєї спроби: інакше це підказка «хтось набрав 300 — віз щедрий».
            board = _reported ? top.Take(10).Select(r => new { nick = r.Nick, points = r.Points, marks = r.Marks }).ToArray() : null,
            place = _reported && mine >= 0 ? mine + 1 : (int?)null,
            players = top.Count,
            share = _reported ? $"Шаткуй дня №{Days.Number(_day)} — {_score} {Points(_score)}\n{Marks()}\nhttps://hlechyky.pp.ua/#games" : null,
        };
        if (_reported && v["end"] is null)
            v["end"] = new { why = _why.Length > 0 ? _why : WhyTime, score = _score, best = _best, rec = false, combo = _bestCombo, cuts = _cuts, big = _bigHits, gleks = _gleks, t = 0 };
        return v;
    }

    sealed record Saved(string Day, bool Done, int Score, int Cuts, int Combo, int Big, int Gleks, string Why, int Best, bool Owed = false);

    public override string? Save() => _day.Length == 0 ? null
        : JsonSerializer.Serialize(new Saved(_day, _reported, _score, _cuts, _bestCombo, _bigHits, _gleks, _why, _best, _owed), Json);

    /// <summary>Зіграний день лишається зіграним. Партія, яку перервав перезапуск сайту, не рахується — можна ще раз.</summary>
    public override void Load(string json)
    {
        Saved? s;
        try { s = JsonSerializer.Deserialize<Saved>(json, Json); }
        catch (JsonException) { return; }
        if (s is null) return;
        _best = Math.Max(0, s.Best);
        if (s.Day != _day || !s.Done) return;
        _reported = true;
        _phase = PhOver;
        _score = s.Score; _cuts = s.Cuts; _bestCombo = s.Combo; _bigHits = s.Big; _gleks = s.Gleks; _why = s.Why ?? "";
        _owed = s.Owed;
    }
}

/// <summary>Рядок таблиці дня «Шаткуй дня».</summary>
public sealed record ShatkuyDayRow(string Nick, int Points, string Marks);

/// <summary>
/// Таблиця дня «Шаткуй дня» (<c>shatkuy_daily</c>): очки «більше — краще», тож не каркасна (там «менше — краще»). Гра
/// звертається лише до пам'яті; запис і перше читання дня йдуть фоном (під замком кімнати — жодного SQLite).
/// </summary>
public sealed class ShatkuyDailyBoard(Db? db) : IDailyPoints
{
    string IDailyPoints.Game => "shatkuy-daily";
    long? IDailyPoints.Points(string day, string nick) => Of(day, nick)?.Points;

    const string Schema = """
        CREATE TABLE IF NOT EXISTS shatkuy_daily(
            day TEXT NOT NULL, nick_key TEXT NOT NULL, nick TEXT NOT NULL, points INTEGER NOT NULL,
            marks TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL, PRIMARY KEY(day, nick_key)) WITHOUT ROWID;
        """;

    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ShatkuyDayRow>> _days = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, bool> _loading = new(StringComparer.Ordinal);
    bool _schema;

    public static string Key(string nick) => nick.Trim().ToLowerInvariant();

    /// <summary>Записати результат (перший за день — назавжди).</summary>
    public void Record(string day, string nick, int points, string marks, DateTimeOffset at)
    {
        if (!Day(day).TryAdd(Key(nick), new ShatkuyDayRow(nick, points, marks)) || db is null) return;
        _ = Task.Run(() =>
        {
            try
            {
                db.With(c =>
                {
                    Ensure(c);
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "INSERT OR IGNORE INTO shatkuy_daily(day, nick_key, nick, points, marks, created_at) VALUES($d, $k, $n, $p, $m, $t)";
                    cmd.Parameters.AddWithValue("$d", day);
                    cmd.Parameters.AddWithValue("$k", Key(nick));
                    cmd.Parameters.AddWithValue("$n", nick);
                    cmd.Parameters.AddWithValue("$p", points);
                    cmd.Parameters.AddWithValue("$m", marks);
                    cmd.Parameters.AddWithValue("$t", at.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                    cmd.ExecuteNonQuery();
                });
            }
            catch (Exception) { /* таблиця дня — радість, а не облік: без запису гра не ламається */ }
        });
    }

    /// <summary>Таблиця дня від більшого (рівні — за ніком). Не чекає на базу.</summary>
    public IReadOnlyList<ShatkuyDayRow> Top(string day) =>
        [.. Day(day).Values.OrderByDescending(r => r.Points).ThenBy(r => r.Nick, StringComparer.Ordinal)];

    public ShatkuyDayRow? Of(string day, string nick) => Day(day).GetValueOrDefault(Key(nick));

    ConcurrentDictionary<string, ShatkuyDayRow> Day(string day)
    {
        var rows = _days.GetOrAdd(day, _ => new ConcurrentDictionary<string, ShatkuyDayRow>(StringComparer.Ordinal));
        if (db is not null && day.Length > 0 && _loading.TryAdd(day, true))
        {
            foreach (var old in _days.Keys.Where(d => string.CompareOrdinal(d, day) < 0).OrderByDescending(d => d).Skip(7).ToList())
            {
                _days.TryRemove(old, out _);
                _loading.TryRemove(old, out _);
            }
            _ = Task.Run(() => Load(day, rows));
        }
        return rows;
    }

    void Load(string day, ConcurrentDictionary<string, ShatkuyDayRow> rows)
    {
        if (db is null) return;
        try
        {
            db.With(c =>
            {
                Ensure(c);
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT nick_key, nick, points, marks FROM shatkuy_daily WHERE day = $d";
                cmd.Parameters.AddWithValue("$d", day);
                using var r = cmd.ExecuteReader();
                while (r.Read()) rows.TryAdd(r.GetString(0), new ShatkuyDayRow(r.GetString(1), r.GetInt32(2), r.GetString(3)));
            });
        }
        catch (Exception) { _loading.TryRemove(day, out _); }
    }

    void Ensure(Microsoft.Data.Sqlite.SqliteConnection c)
    {
        if (_schema) return;
        using var cmd = c.CreateCommand();
        cmd.CommandText = Schema;
        cmd.ExecuteNonQuery();
        _schema = true;
    }
}

/// <summary>Сервіси «Шаткуй!»: таблиця «Шаткуй дня» і її очки в «☀ Сьогодні».</summary>
public static class ShatkuySetup
{
    public static IServiceCollection AddShatkuy(IServiceCollection services)
    {
        services.AddSingleton<ShatkuyDailyBoard>();
        services.AddSingleton<IDailyPoints>(sp => sp.GetRequiredService<ShatkuyDailyBoard>());
        return services;
    }
}
