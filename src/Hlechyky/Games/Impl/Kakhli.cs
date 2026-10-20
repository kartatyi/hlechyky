using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Спільне для трьох «Кахлів» (docs/games/specs/kakhli.md): поле, хід «поміняти дві сусідні», кроки розв'язку для
/// анімації й ачівки ходу. Браузер шле лише клітинку й напрямок — усе інше (чи можна, що зійшлось, що насипалось,
/// скільки очок) вирішує сервер. Зерно генератора в браузер не потрапляє, тож майбутніх кахлів не підглянути.
/// </summary>
public abstract class KakhliGame : Game
{
    public const int Size = 8;
    /// <summary>Каскад такої довжини одним ходом — ачівка «Каскад».</summary>
    public const int ChainAch = 6;

    private protected KakhliBoard? _board;
    /// <summary>Кроки останнього ходу (з «Глек доливає», якщо хід був останній) — браузер програє їх, коли <see cref="_mv"/> новий.</summary>
    private protected List<KakhliStep> _steps = [];
    private protected int _mv;
    private protected int[]? _swap;

    public KakhliBoard? Board => _board;

    /// <summary>«{c, d}» → дві клітинки; null — не зрозуміли.</summary>
    static (int A, int B)? Parse(KakhliBoard board, JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) return null;
        if (!p.TryGetProperty("c", out var c) || !c.TryGetInt32(out var a)) return null;
        if (!p.TryGetProperty("d", out var d) || !d.TryGetInt32(out var dir) || dir is < 0 or > 3) return null;
        if (!board.Valid(a)) return null;
        var b = board.Neighbor(a, dir);
        return b < 0 ? null : (a, b);
    }

    /// <summary>Хід: null — прийнято (кроки в <see cref="_steps"/>), інакше текст відмови. Стан при відмові не міняється.</summary>
    private protected string? Move(JsonElement payload)
    {
        if (_board is null) return "Поля ще нема";
        if (Parse(_board, payload) is not { } m) return "Не зрозумів, що з чим міняти";
        if (_board.RopeAt(m.A) || _board.RopeAt(m.B)) return "Зв'язану кахлю не посунеш — склади з нею лінію";
        if (_board.Swap(m.A, m.B) is not { } steps) return "Так нічого не складеться";
        _steps = steps;
        _swap = [m.A, m.B];
        _mv++;
        Achieve(steps);
        return null;
    }

    /// <summary>Ачівки, які видно лише в межах ходу: глек склали, каскад довгий.</summary>
    void Achieve(List<KakhliStep> steps)
    {
        // глек у Made з'являється лише зі збігу п'яти (перетворення дають рушники й жаринки); перемішування — не збіг
        if (steps.Exists(s => !s.Shuffle && s.Made.Exists(m => m[3] == KakhliKind.Glek)))
            Ctx.Award(0, 0, "ach:kakhli-glek");
        if (steps.Max(s => s.Chain) >= ChainAch) Ctx.Award(0, 0, "ach:kakhli-chain");
    }

    /// <summary>Наприкінці: «Глек доливає» — кроки дописуються до останнього ходу, щоб браузер програв їх одним махом.</summary>
    private protected void Finale(int convert)
    {
        if (_board is null) return;
        var fin = _board.Finale(convert);
        foreach (var s in fin) s.Final = true;
        if (fin.Count == 0) return;
        _steps = [.. _steps, .. fin];
    }

    private protected object? BoardView() => _board is null ? null : new
    {
        board = _board.Wire(),
        steps = _steps.Select(s => s.Wire()).ToArray(),
        mv = _mv,
        swap = _swap,
        score = _board.Score,
        made = new { stripes = _board.Stripes, embers = _board.Embers, gleks = _board.Gleks, chain = _board.MaxChain },
    };

    private protected static KakhliBoard NewBoard(ulong seed, int colors, string? map = null)
    {
        var b = new KakhliBoard(Size, Size, colors, seed, map);
        b.Fill();
        return b;
    }

    /// <summary>Смужка для «поділитись»: скільки глеків, жаринок і рушників та найдовший каскад — без самого поля.</summary>
    public static string Marks(KakhliBoard b) =>
        string.Create(CultureInfo.InvariantCulture, $"🏺×{b.Gleks} 🔥×{b.Embers} 🧣×{b.Stripes} · каскад ×{b.MaxChain}");

    /// <summary>12345 → «12 345».</summary>
    public static string Num(long n) => n.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', ' ');

    public override string SeatName(int seat) => "кахляр";

    private protected sealed record SavedBoard(KakhliBoardState? Board);
    private protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

// =============================================================================================
// Кахлі дня
// =============================================================================================

/// <summary>
/// «Кахлі дня»: поле й мішечок кахлів однакові для всіх за київську добу (сід від дня), 30 ходів, одна спроба. Щоденний
/// каркас (<see cref="IDailyGame"/>) дає «☀ Сьогодні», серію й щоденний глек; таблиця дня за очками — своя
/// (<see cref="KakhliDailyBoard"/>), як у «Гусей дня».
/// </summary>
public sealed class KakhliDaily : KakhliGame, IDailyGame
{
    public const int Moves = 30, Colors = 6;
    /// <summary>Ключ зерна дня — свідомо не Id: назва гри може мінятись, а поле дня лишатись.</summary>
    const string Puzzle = "kakhli";
    public const string DoneToday = "Кахлі дня вже складено — нове поле після півночі. А поки — «Шлях кахляра» чи «На час»";

    public override GameInfo Info { get; } = new(
        "kakhli-daily", "Кахлі дня", "кахлі дня", GameGroup.Solo, 1, 1,
        Start: StartMode.Immediate, Private: true, Persistent: true,
        Hint: "Одне поле на всіх за добу й тридцять ходів, одна спроба. Хто сьогодні склав найбільше? Таблиця дня — у грі",
        Client: "kakhli");

    string _day = "";
    int _left;
    bool _reported;
    KakhliDailyBoard? _table;

    public int Left => _left;
    public bool Reported => _reported;
    public string Day => _day;

    public override string SoloKey(string nickKey, IClock clock) => $"daily:{Info.Id}:{Days.Today(clock)}:{nickKey}";

    public override void Configure(IReadOnlyDictionary<string, string> options) => _table = Ctx.Services.GetService<KakhliDailyBoard>();

    public override string? CanStart() => _reported && _day == Days.Today(Ctx.Clock) ? DoneToday : null;

    public override void Start()
    {
        var today = Days.Today(Ctx.Clock);
        if (_reported && _day == today) return;   // «Ще раз» на дограному дні — лишаємо підсумок
        _day = today;
        _reported = false;
        _left = Moves;
        // Сід від дня, а не від кімнати: інакше в кожного було б своє поле, і порівнювати очки не було б сенсу.
        _board = NewBoard(DaySeed(_day), Colors);
        _steps = [];
        _swap = null;
        _mv = 0;
    }

    public static ulong DaySeed(string day) => (ulong)Days.Seed(Puzzle, day) * 0x9E3779B97F4A7C15UL + 1;

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (seat != 0) return ActResult.Fail("Ти тут не граєш");
        if (_reported) return ActResult.Fail(DoneToday);
        if (action != "swap") return ActResult.Fail("Тут так не ходять");
        if (Move(payload) is { } fail) return ActResult.Fail(fail);
        _left--;
        if (_left <= 0) Done();
        return ActResult.Done;
    }

    void Done()
    {
        Finale(0);
        _reported = true;
        var nick = Ctx.NickOf(0) ?? "";
        var points = (int)Math.Min(int.MaxValue, _board!.Score);
        _table?.Record(_day, nick, points, Marks(_board), Ctx.Clock.UtcNow);
        // Спроба в щоденному каркасі одна; очки там не лягають (він рахує «менше — краще»), тож час — 0.
        Ctx.Score(0, 0, 1);
        Ctx.Award(0, 0, $"daily:{Info.Id}");
        Ctx.Finish([0], $"{Info.Title}: {nick} — {Num(points)} очок", new Dictionary<int, long> { [0] = points });
    }

    public override object View(int? seat)
    {
        var top = _table?.Top(_day) ?? [];
        var nick = Ctx.NickOf(0);
        var mine = nick is null ? -1 : top.ToList().FindIndex(r => KakhliDailyBoard.Key(r.Nick) == KakhliDailyBoard.Key(nick));
        var score = _board?.Score ?? 0;
        return new
        {
            mode = "daily",
            phase = _reported ? "done" : "play",
            turn = _reported ? (int?)null : 0,
            left = _left,
            moves = Moves,
            game = BoardView(),
            daily = new
            {
                day = _day,
                no = _day.Length == 0 ? 0 : Days.Number(_day),
                // Таблицю дня — лише після своєї спроби: інакше це підказка «хтось набрав 9000 — поле щедре».
                board = _reported ? top.Take(10).Select(r => new { nick = r.Nick, points = r.Points, marks = r.Marks }).ToArray() : null,
                place = _reported && mine >= 0 ? mine + 1 : (int?)null,
                players = top.Count,
                share = _reported && _board is not null
                    ? $"Кахлі дня №{Days.Number(_day)} — {Num(score)}\n{Marks(_board)}\nhttps://hlechyky.pp.ua/#games" : null,
            },
        };
    }

    sealed record Saved(string Day, int Left, bool Done, int Mv, KakhliBoardState? Board);

    public override string? Save() => _day.Length == 0 ? null
        : JsonSerializer.Serialize(new Saved(_day, _left, _reported, _mv, _board?.Save()), Json);

    /// <summary>Повернувся посеред дня — з того самого ходу (і тим самим мішечком). Зіграний день лишається зіграним.</summary>
    public override void Load(string json)
    {
        Saved? s;
        try { s = JsonSerializer.Deserialize<Saved>(json, Json); }
        catch (JsonException) { return; }
        if (s is null || s.Day != _day || KakhliBoard.Load(s.Board) is not { } board) return;
        _board = board;
        _left = Math.Clamp(s.Left, 0, Moves);
        _reported = s.Done || _left == 0;
        _mv = Math.Max(0, s.Mv);
        _steps = [];
        _swap = null;
    }
}

/// <summary>Рядок таблиці дня «Кахлів дня».</summary>
public sealed record KakhliDayRow(string Nick, int Points, string Marks);

/// <summary>
/// Таблиця дня «Кахлів дня» (<c>kakhli_daily</c>): очки «більше — краще», тож не каркасна (там «менше — краще»). Гра
/// звертається лише до пам'яті; запис і перше читання дня йдуть фоном (під замком кімнати — жодного SQLite).
/// </summary>
public sealed class KakhliDailyBoard(Db? db) : IDailyPoints
{
    string IDailyPoints.Game => "kakhli-daily";
    long? IDailyPoints.Points(string day, string nick) => Of(day, nick)?.Points;

    const string Schema = """
        CREATE TABLE IF NOT EXISTS kakhli_daily(
            day TEXT NOT NULL, nick_key TEXT NOT NULL, nick TEXT NOT NULL, points INTEGER NOT NULL,
            marks TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL, PRIMARY KEY(day, nick_key)) WITHOUT ROWID;
        """;

    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, KakhliDayRow>> _days = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, bool> _loading = new(StringComparer.Ordinal);
    bool _schema;

    public static string Key(string nick) => nick.Trim().ToLowerInvariant();

    /// <summary>Записати результат (перший за день — назавжди).</summary>
    public void Record(string day, string nick, int points, string marks, DateTimeOffset at)
    {
        if (!Day(day).TryAdd(Key(nick), new KakhliDayRow(nick, points, marks)) || db is null) return;
        _ = Task.Run(() =>
        {
            try
            {
                db.With(c =>
                {
                    Ensure(c);
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "INSERT OR IGNORE INTO kakhli_daily(day, nick_key, nick, points, marks, created_at) VALUES($d, $k, $n, $p, $m, $t)";
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
    public IReadOnlyList<KakhliDayRow> Top(string day) =>
        [.. Day(day).Values.OrderByDescending(r => r.Points).ThenBy(r => r.Nick, StringComparer.Ordinal)];

    public KakhliDayRow? Of(string day, string nick) => Day(day).GetValueOrDefault(Key(nick));

    ConcurrentDictionary<string, KakhliDayRow> Day(string day)
    {
        var rows = _days.GetOrAdd(day, _ => new ConcurrentDictionary<string, KakhliDayRow>(StringComparer.Ordinal));
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

    void Load(string day, ConcurrentDictionary<string, KakhliDayRow> rows)
    {
        if (db is null) return;
        try
        {
            db.With(c =>
            {
                Ensure(c);
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT nick_key, nick, points, marks FROM kakhli_daily WHERE day = $d";
                cmd.Parameters.AddWithValue("$d", day);
                using var r = cmd.ExecuteReader();
                while (r.Read()) rows.TryAdd(r.GetString(0), new KakhliDayRow(r.GetString(1), r.GetInt32(2), r.GetString(3)));
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

/// <summary>Сервіси «Кахлів»: таблиця «Кахлів дня» і її очки в «☀ Сьогодні».</summary>
public static class KakhliSetup
{
    public static IServiceCollection AddKakhli(IServiceCollection services)
    {
        services.AddSingleton<KakhliDailyBoard>();
        services.AddSingleton<IDailyPoints>(sp => sp.GetRequiredService<KakhliDailyBoard>());
        return services;
    }
}

// =============================================================================================
// На час
// =============================================================================================

/// <summary>
/// «Кахлі на час» («Поки піч гаряча»): дві хвилини на новому полі щоразу. «Готовий?» → три секунди відліку → годинник
/// сервера. Хід, що прийшов пізніше дзвоника й секунди на дорогу, не рахується. Таблиця — найкращий рахунок.
/// </summary>
public sealed class KakhliBlitz : KakhliGame
{
    public const int Colors = 6;
    public static readonly TimeSpan Length = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan Countdown = TimeSpan.FromSeconds(3);
    /// <summary>Скільки після дзвоника ще пускаємо хід, що був у дорозі.</summary>
    public static readonly TimeSpan Slack = TimeSpan.FromSeconds(1);
    /// <summary>Скільки очок за дві хвилини — ачівка «Гаряча піч».</summary>
    public const int HotScore = 9000;

    public const string PhReady = "ready", PhGo = "go", PhOver = "over";

    public override GameInfo Info { get; } = new(
        "kakhli-blitz", "Кахлі на час", "кахлі на час", GameGroup.Solo, 1, 1,
        Start: StartMode.Immediate, Private: true, Score: ScoreOrder.HigherIsBetter,
        Hint: "Дві хвилини, поки піч гаряча: склади якнайбільше. Таблиця — найкращий рахунок",
        Client: "kakhli");

    string _phase = PhReady;
    DateTimeOffset _goAt, _until;

    public string Phase => _phase;

    public override void Start()
    {
        _phase = PhReady;
        _board = NewBoard((ulong)Ctx.Rng.NextInt64(1, long.MaxValue), Colors);
        _steps = [];
        _swap = null;
        _mv = 0;
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (seat != 0) return ActResult.Fail("Ти тут не граєш");
        var now = Ctx.Clock.UtcNow;
        switch (action)
        {
            case "ready":
                if (_phase != PhReady) return ActResult.Fail("Уже почали");
                _goAt = now + Countdown;
                _until = _goAt + Length;
                _phase = PhGo;
                return ActResult.Done;
            case "swap":
                if (_phase != PhGo) return ActResult.Fail(_phase == PhReady ? "Спершу «Поїхали»" : "Піч уже згасла");
                if (now < _goAt - TimeSpan.FromMilliseconds(200)) return ActResult.Fail("Зачекай, зараз почнемо");
                if (now > _until + Slack) { End(); return ActResult.Fail("Час вийшов"); }
                return Move(payload) is { } fail ? ActResult.Fail(fail) : ActResult.Done;
            case "end":
                if (_phase != PhGo) return ActResult.Fail("Зараз нема чого кінчати");
                if (now < _until - TimeSpan.FromMilliseconds(500)) return ActResult.Fail("Ще є час");
                End();
                return ActResult.Done;
            default:
                return ActResult.Fail("Тут так не ходять");
        }
    }

    void End()
    {
        _steps = [];
        _mv++;
        Finale(0);
        _phase = PhOver;
        var score = _board!.Score;
        Ctx.Score(0, score);
        if (score >= HotScore) Ctx.Award(0, 0, "ach:kakhli-hot");
        Ctx.Finish([0], $"{Info.Title}: {Ctx.NickOf(0)} — {Num(score)} очок за дві хвилини",
            new Dictionary<int, long> { [0] = score });
    }

    public override object View(int? seat)
    {
        var now = Ctx.Clock.UtcNow;
        return new
        {
            mode = "blitz",
            phase = _phase,
            turn = _phase == PhGo ? 0 : (int?)null,
            // до «Поїхали» поле не показуємо: інакше перший хід шукали б без годинника
            game = _phase == PhReady ? null : BoardView(),
            inMs = _phase == PhGo ? Math.Max(0, (long)(_goAt - now).TotalMilliseconds) : 0,
            leftMs = _phase == PhGo ? Math.Max(0, (long)(_until - now).TotalMilliseconds)
                : _phase == PhReady ? (long)Length.TotalMilliseconds : 0,
            seconds = (int)Length.TotalSeconds,
            hot = HotScore,
        };
    }
}

// =============================================================================================
// Шлях кахляра
// =============================================================================================

/// <summary>
/// «Шлях кахляра»: тридцять рівнів (<see cref="KakhliLevels"/>) із цілями, глиною й мотузками. Прогрес — назавжди
/// (Persistent, ключ на ніка): зірки кожного рівня й рівень, що зараз на полі. За кожну НОВУ зірку — черепок
/// (через денну стелю нагород), таблиця гри — скільки зірок разом.
/// </summary>
public sealed class Kakhli : KakhliGame
{
    public const string PhMap = "map", PhPlay = "play", PhWon = "won", PhLost = "lost";
    /// <summary>Черепків за кожну нову зірку (базово; економіка множить сама).</summary>
    public const int StarShards = 1;

    public override GameInfo Info { get; } = new(
        "kakhli", "Кахлі: шлях кахляра", "кахлі", GameGroup.Solo, 1, 1,
        Start: StartMode.Immediate, Private: true, Persistent: true, Score: ScoreOrder.HigherIsBetter,
        Hint: "Тридцять рівнів у трьох печах: цілі, сира глина й мотузки, до трьох зірок за рівень. Таблиця — скільки зірок",
        Client: "kakhli");

    readonly int[] _stars = new int[KakhliLevels.All.Count];
    string _phase = PhMap;
    int _level = -1;
    int _left;
    int _clay0, _rope0;
    /// <summary>Підсумок останнього рівня: скільки зірок і скільки з них нових.</summary>
    int _got, _fresh;

    public string Phase => _phase;
    public int Level => _level;
    public int Left => _left;
    public IReadOnlyList<int> Stars => _stars;
    public int Total => _stars.Sum();

    bool Open(int n) => n == 0 || (n > 0 && n < _stars.Length && _stars[n - 1] > 0);

    public override void Start()
    {
        _phase = PhMap;
        _level = -1;
        _board = null;
        _steps = [];
        _swap = null;
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (seat != 0) return ActResult.Fail("Ти тут не граєш");
        switch (action)
        {
            case "level":
                if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("n", out var n) || !n.TryGetInt32(out var level)
                    || level < 0 || level >= _stars.Length) return ActResult.Fail("Нема такого рівня");
                if (!Open(level)) return ActResult.Fail("Цей рівень ще замкнено — спершу попередній");
                Begin(level);
                return ActResult.Done;
            case "retry":
                if (_level < 0) return ActResult.Fail("Спершу обери рівень");
                Begin(_level);
                return ActResult.Done;
            case "next":
                if (_phase != PhWon || !Open(_level + 1)) return ActResult.Fail("Далі поки нікуди");
                Begin(_level + 1);
                return ActResult.Done;
            case "map":
                _phase = PhMap;
                _board = null;
                _steps = [];
                _swap = null;
                return ActResult.Done;
            case "swap":
                if (_phase != PhPlay) return ActResult.Fail("Спершу обери рівень");
                if (Move(payload) is { } fail) return ActResult.Fail(fail);
                _left--;
                Judge();
                return ActResult.Done;
            default:
                return ActResult.Fail("Тут так не ходять");
        }
    }

    void Begin(int level)
    {
        var def = KakhliLevels.All[level];
        _level = level;
        _left = def.Moves;
        _board = NewBoard((ulong)Ctx.Rng.NextInt64(1, long.MaxValue), def.Colors, def.Map);
        _clay0 = _board.ClayLeft;
        _rope0 = _board.RopesLeft;
        _steps = [];
        _swap = null;
        _mv++;
        _got = _fresh = 0;
        _phase = PhPlay;
    }

    /// <summary>Чи виконано цілі (очки — лише для рівня на очки).</summary>
    bool GoalsMet(KakhliLevel def) => def.Goals.All(g => Have(g) >= Need(g));

    int Need(KakhliGoal g) => g.Kind switch { 'l' => _clay0, 'r' => _rope0, _ => g.Count };

    int Have(KakhliGoal g) => _board is null ? 0 : g.Kind switch
    {
        's' => (int)Math.Min(int.MaxValue, _board.Score),
        'c' => g.Color is >= 0 and < KakhliBoard.MaxColors ? _board.Collected[g.Color] : 0,
        'l' => _clay0 - _board.ClayLeft,
        'r' => _rope0 - _board.RopesLeft,
        _ => 0,
    };

    /// <summary>Після ходу: цілі виконано — перемога (лишок ходів Глек доливає рушниками); ходи скінчились — підсумок.</summary>
    void Judge()
    {
        var def = KakhliLevels.All[_level];
        if (!def.ScoreOnly && GoalsMet(def)) { Win(def, _left); return; }
        if (_left > 0) return;
        if (def.ScoreOnly)
        {
            Finale(0);   // що лишилось спецового — спрацьовує, а тоді рахуємо
            if (GoalsMet(def)) { Win(def, 0); return; }
        }
        _phase = PhLost;
        _got = _fresh = 0;
    }

    void Win(KakhliLevel def, int spare)
    {
        if (spare > 0) Finale(spare);
        var stars = def.Stars(_board!.Score, true);
        var was = _stars[_level];
        _got = stars;
        _fresh = Math.Max(0, stars - was);
        _phase = PhWon;
        if (_fresh == 0) return;
        _stars[_level] = stars;
        // по черепку за кожну нову зірку — причина з номером рівня й зірки, тож та сама зірка вдруге не платиться
        for (var s = was + 1; s <= stars; s++) Ctx.Award(0, StarShards, $"kakhli-star:{_level + 1}:{s}");
        Ctx.Score(0, Total);
        if (Total >= KakhliLevels.MaxStars) Ctx.Award(0, 0, "ach:kakhli-master");
    }

    public override object View(int? seat)
    {
        var def = _level >= 0 ? KakhliLevels.All[_level] : null;
        return new
        {
            mode = "path",
            phase = _phase,
            turn = _phase == PhPlay ? 0 : (int?)null,
            total = Total,
            max = KakhliLevels.MaxStars,
            kilns = KakhliLevels.Kilns,
            levels = KakhliLevels.All.Select((l, i) => new { n = i, name = l.Name, stars = _stars[i], open = Open(i) }).ToArray(),
            level = def is null ? null : new
            {
                n = _level,
                name = def.Name,
                moves = def.Moves,
                left = _left,
                tip = def.Tip,
                two = def.Two,
                three = def.Three,
                scoreOnly = def.ScoreOnly,
                goals = def.Goals.Select(g => new { k = g.Kind.ToString(), color = g.Color, need = Need(g), have = Math.Min(Need(g), Have(g)) }).ToArray(),
                best = _stars[_level],
                got = _got,
                fresh = _fresh,
                next = _phase == PhWon && Open(_level + 1) ? _level + 1 : (int?)null,
            },
            game = _phase == PhMap ? null : BoardView(),
        };
    }

    sealed record Saved(int[] Stars, string Phase, int Level, int Left, int Clay0, int Rope0, int Got, int Fresh, int Mv, KakhliBoardState? Board);

    public override string? Save() => JsonSerializer.Serialize(
        new Saved([.. _stars], _phase, _level, _left, _clay0, _rope0, _got, _fresh, _mv, _board?.Save()), Json);

    /// <summary>Зірки — завжди; рівень на полі — якщо поле живе (інакше назад до мапи, зірки не губляться).</summary>
    public override void Load(string json)
    {
        Saved? s;
        try { s = JsonSerializer.Deserialize<Saved>(json, Json); }
        catch (JsonException) { return; }
        if (s is null) return;
        for (var i = 0; i < _stars.Length && i < (s.Stars?.Length ?? 0); i++) _stars[i] = Math.Clamp(s.Stars![i], 0, 3);
        _mv = Math.Max(0, s.Mv);
        if (s.Level < 0 || s.Level >= _stars.Length || s.Phase is not (PhPlay or PhWon or PhLost)
            || KakhliBoard.Load(s.Board) is not { } board)
        {
            _phase = PhMap;
            return;
        }
        _board = board;
        _phase = s.Phase;
        _level = s.Level;
        _left = Math.Clamp(s.Left, 0, KakhliLevels.All[s.Level].Moves);
        _clay0 = Math.Max(0, s.Clay0);
        _rope0 = Math.Max(0, s.Rope0);
        _got = Math.Clamp(s.Got, 0, 3);
        _fresh = Math.Clamp(s.Fresh, 0, 3);
    }
}
