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
    /// <summary>Пороги ачівок (specs/shatkuy.md §6): v2 — ще серія 50 («Срібний серп») і 300 у Класиці («Вогняне лезо»).</summary>
    public const int ComboAch = 6, ClassicAch = 250, BigAch = 25, CleanAch = 150, StreakAch = 50, FireAch = 300;
    public const string AchStreak = "shatkuy-streak", AchFire = "shatkuy-fire";
    /// <summary>Черепків за кожні стільки очок (через денну стелю нагород), не більше <see cref="MaxShards"/> за партію.</summary>
    public const int PointsPerShard = 50, MaxShards = 10;

    protected abstract ShatkuyMode Mode { get; }
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected string _phase = PhReady;
    DateTimeOffset _t0;
    ShatkuyField? _field;
    protected ShatkuyField F => _field ??= new ShatkuyField(Mode);
    protected int _best, _games;
    protected bool _record;
    bool _dirty;

    public string Phase => _phase;
    public ShatkuyField Field => F;
    public int Score => F.Score;
    public int Misses => F.Misses;
    public int BestCombo => F.BestCombo;
    public int BigHits => F.BigHits;
    public int Streak => F.Streak;
    public int BestStreak => F.BestStreak;
    public int Precise => F.Precise;
    public string Why => F.Why;
    public int Best => _best;
    public IReadOnlyCollection<ShatkuyFruit> Fruits => F.Fruits;
    public bool IsCut(int id) => F.IsCut(id);
    public IReadOnlyList<(int S, int E)> Frost => F.Frost;
    /// <summary>Ігрова мілісекунда зараз (0 — партія не йде).</summary>
    public int GameNow => _phase == PhGo ? F.G(T(Now)) : 0;
    DateTimeOffset Now => Ctx.Clock.UtcNow;

    public override string SeatName(int seat) => "шаткар";

    public override void Start() => Reset();

    protected void Reset()
    {
        _phase = PhReady;
        F.Reset();
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
                _t0 = now.AddMilliseconds(ShatkuyCore.LeadMs);
                _phase = PhGo;
                F.Begin(NewSeed());
                _dirty = true;
                return ActResult.Done;
            case "cut":
                return CutMany(payload, now);
            default:
                return ActResult.Fail("Тут так не ходять");
        }
    }

    int T(DateTimeOffset now) => (int)Math.Clamp((now - _t0).TotalMilliseconds, -1e9, 1e9);

    /// <summary>
    /// Розбір розрізів <c>{ c: [[id, w], [id, w, 1], …] }</c>: w — номер змаху, третій елемент 1 — змах пройшов через
    /// центр (v2 §2). Зіпсовані елементи пропускаються. null — payload не той.
    /// </summary>
    public static List<ShatkuyCut>? ReadCuts(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("c", out var list) || list.ValueKind != JsonValueKind.Array)
            return null;
        var cuts = new List<ShatkuyCut>();
        foreach (var e in list.EnumerateArray())
        {
            if (cuts.Count >= MaxCuts) break;
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() < 2) continue;
            if (!e[0].TryGetInt32(out var id) || !e[1].TryGetInt32(out var w)) continue;
            var precise = e.GetArrayLength() >= 3 && e[2].ValueKind == JsonValueKind.Number && e[2].TryGetInt32(out var p) && p == 1;
            cuts.Add(new ShatkuyCut(id, w, precise));
        }
        return cuts;
    }

    /// <summary>
    /// Розріз (реалтайм-ввід, пачкою): див. <see cref="ReadCuts"/> і <see cref="ShatkuyField.CutMany"/>. Великий — раз на
    /// змах, гарбуз — двома різними змахами, точний удар — лише щойно розрізаному плоду.
    /// </summary>
    ActResult CutMany(JsonElement payload, DateTimeOffset now)
    {
        if (_phase != PhGo) return ActResult.Fail("Зараз нема чого різати");
        var t = T(now);
        if (t < -200) return ActResult.Fail("Ще не почали");
        if (ReadCuts(payload) is not { } cuts) return ActResult.Fail("Що різати?");
        if (!F.TimeLeft(t)) return ActResult.Fail("Час вийшов");
        var any = F.CutMany(cuts, t, MaxCuts) > 0;
        if (F.Over) End(F.Why, t);
        if (any) _dirty = true;
        return any ? ActResult.Done : ActResult.Fail("Не влучив");
    }

    // ---------- час ----------

    public override TickResult Tick()
    {
        Advance(Now);
        if (!_dirty && !F.Dirty) return TickResult.None;
        _dirty = false;
        F.Dirty = F.Urgent = false;
        return new TickResult(false, true);
    }

    /// <summary>Прожити партію до <paramref name="now"/>: докинути розклад, порахувати впалі (у Класиці — промахи), кінець часу.</summary>
    void Advance(DateTimeOffset now)
    {
        if (_phase != PhGo) return;
        var t = T(now);
        F.Advance(t);   // і під час відліку «Ану!» — перша хвиля теж приходить за обрій до вильоту
        if (F.Over) End(F.Why, t);
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
        F.Close(why, t);
        _phase = PhOver;
        _games++;
        _record = F.Score > _best;
        if (_record) _best = F.Score;
        _dirty = true;
    }

    /// <summary>Ачівки партії поля <paramref name="f"/> місця <paramref name="seat"/> (соло — 0; «разом» — кожному).</summary>
    public static void Achievements(IRoomContext ctx, int seat, ShatkuyField f)
    {
        if (f.BestCombo >= ComboAch) ctx.Award(seat, 0, "ach:shatkuy-combo");
        if (f.Mode == ShatkuyCore.Classic && f.Score >= ClassicAch) ctx.Award(seat, 0, "ach:shatkuy-classic");
        if (f.KavunHits >= BigAch) ctx.Award(seat, 0, "ach:shatkuy-big");
        if (f.Mode == ShatkuyCore.Fair && f.Gleks == 0 && f.Score >= CleanAch) ctx.Award(seat, 0, "ach:shatkuy-clean");
        // v2: леза за ачівки (Лавка, LavkaCatalog.EarnedBlades) — серп за серію 50 будь-де, вогняне — 300 у Класиці
        if (f.BestStreak >= StreakAch) ctx.Award(seat, 0, "ach:" + AchStreak);
        if (f.Mode == ShatkuyCore.Classic && f.Score >= FireAch) ctx.Award(seat, 0, "ach:" + AchFire);
    }

    protected void Achievements() => Achievements(Ctx, 0, F);

    protected virtual void Report()
    {
        Achievements();
        Ctx.Score(0, F.Score);
        var shards = Math.Min(MaxShards, F.Score / PointsPerShard);
        if (shards > 0) Ctx.Award(0, shards, $"shatkuy:{Ctx.Round}");
        Ctx.Finish([0], $"{Info.Title}: {Ctx.NickOf(0)} — {F.Score} {Points(F.Score)}", new Dictionary<int, long> { [0] = F.Score });
    }

    public static string Points(int n) =>
        (n % 100) is >= 11 and <= 14 ? "очок" : (n % 10) switch { 1 => "очко", 2 or 3 or 4 => "очки", _ => "очок" };

    protected int NowT => T(Now);

    // ---------- вид ----------

    public override object View(int? seat) => BaseView();

    /// <summary>Правила числами — однакові для всіх режимів (клієнт малює й передбачає за ними).</summary>
    public static object Rules(ShatkuyMode mode) => new
    {
        lag = ShatkuyCore.LagMs, look = ShatkuyCore.LookMs, frost = ShatkuyCore.FrostRate, bigMax = ShatkuyCore.BigMaxHits,
        glek = mode.Lives ? 0 : ShatkuyCore.GlekPenalty,
        // v2
        crackGap = ShatkuyCore.CrackGapMs, burn = ShatkuyCore.BurnMs, series = mode.Series ? ShatkuyCore.SeriesAt : [],
        giantEvery = mode.Giants ? ShatkuyCore.GiantEvery : 0, r = ShatkuyCore.NewRadii,
        pts = new { p = ShatkuyCore.Price(ShatkuyCore.Pumpkin), h = ShatkuyCore.Price(ShatkuyCore.Pepper), o = ShatkuyCore.Price(ShatkuyCore.Potato), c = 1, v = 1 },
    };

    /// <summary>
    /// Поля виду одного поля (соло й «разом»): рахунок, промахи, серія, множник, мороз/×2/печія, плоди в обрії, підсумок.
    /// <paramref name="go"/> — партія поля йде (плоди показуються).
    /// </summary>
    public static Dictionary<string, object?> FieldView(ShatkuyField f, bool go, int t, bool over)
    {
        var g = go ? f.G(t) : 0;
        return new Dictionary<string, object?>
        {
            ["misses"] = f.Misses,
            ["score"] = f.Score,
            ["combo"] = f.BestCombo,
            ["gleks"] = f.Gleks,
            ["big"] = f.BigHits,
            ["cuts"] = f.Cuts,
            ["streak"] = f.Streak,
            ["mult"] = f.Mult,
            ["bestStreak"] = f.BestStreak,
            ["precise"] = f.Precise,
            ["frz"] = ShatkuyField.Spans(f.Frost),
            ["dbl"] = f.DoubleSpans,
            ["burn"] = ShatkuyField.Spans(f.Burn),
            ["fruits"] = go ? f.FruitsView(g) : Array.Empty<object>(),
            ["end"] = over ? End(f) : null,
        };
    }

    public static object End(ShatkuyField f, string? why = null, int? t = null) => new
    {
        why = why ?? f.Why, score = f.Score, combo = f.BestCombo, cuts = f.Cuts, big = f.BigHits, gleks = f.Gleks, t = t ?? f.EndT,
        streak = f.BestStreak, precise = f.Precise,
    };

    protected Dictionary<string, object?> BaseView()
    {
        var now = Now;
        var go = _phase == PhGo;
        var over = _phase == PhOver;
        var v = new Dictionary<string, object?>
        {
            ["mode"] = Mode.Id,
            ["phase"] = _phase,
            ["turn"] = go ? 0 : null,
            ["now"] = now.ToString("o", CultureInfo.InvariantCulture),
            ["t0"] = _phase == PhReady ? null : _t0.ToString("o", CultureInfo.InvariantCulture),
            ["len"] = Mode.LenMs,
            ["lives"] = Mode.Lives ? ShatkuyCore.Lives : 0,
            ["best"] = _best,
            ["games"] = _games,
        };
        foreach (var (key, value) in FieldView(F, go, go ? T(now) : 0, false)) v[key] = value;
        v["end"] = over ? EndView() : null;
        v["k"] = Rules(Mode);
        return v;
    }

    /// <summary>Підсумок соло: поля поля + рекорд.</summary>
    protected object EndView(string? why = null, int? t = null, bool? rec = null) => new
    {
        why = why ?? F.Why, score = F.Score, best = _best, rec = rec ?? _record, combo = F.BestCombo, cuts = F.Cuts, big = F.BigHits,
        gleks = F.Gleks, t = t ?? F.EndT, streak = F.BestStreak, precise = F.Precise,
    };

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
        _table?.Record(_day, Ctx.NickOf(0) ?? "", F.Score, Marks(), Ctx.Clock.UtcNow);
    }

    void Credit()
    {
        Achievements();
        var nick = Ctx.NickOf(0) ?? "";
        // Спроба в щоденному каркасі одна; очки там не лягають (він рахує «менше — краще»), тож час — 0.
        Ctx.Score(0, 0, 1);
        Ctx.Award(0, 0, $"daily:{Info.Id}");
        Ctx.Finish([0], $"{Info.Title}: {nick} — {F.Score} {Points(F.Score)}", new Dictionary<int, long> { [0] = F.Score });
    }

    string Marks() => $"🔪 {F.Cuts} · комбо ×{Math.Max(F.BestCombo, 1)} · 🍉 {F.BigHits}" + (F.BestStreak >= 10 ? $" · серія {F.BestStreak}" : "")
        + (F.Gleks > 0 ? $" · 💥 {F.Gleks}" : "");

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
            share = _reported ? $"Шаткуй дня №{Days.Number(_day)} — {F.Score} {Points(F.Score)}\n{Marks()}\nhttps://hlechyky.pp.ua/#games" : null,
        };
        if (_reported && v["end"] is null)
            v["end"] = EndView(F.Why.Length > 0 ? F.Why : WhyTime, 0, false);
        return v;
    }

    sealed record Saved(string Day, bool Done, int Score, int Cuts, int Combo, int Big, int Gleks, string Why, int Best, bool Owed = false,
        int Streak = 0, int Precise = 0);

    public override string? Save() => _day.Length == 0 ? null
        : JsonSerializer.Serialize(new Saved(_day, _reported, F.Score, F.Cuts, F.BestCombo, F.BigHits, F.Gleks, F.Why, _best, _owed,
            F.BestStreak, F.Precise), Json);

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
        F.Restore(s.Score, s.Cuts, s.Combo, s.Big, s.Gleks, s.Why ?? "", s.Streak, s.Precise);
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
