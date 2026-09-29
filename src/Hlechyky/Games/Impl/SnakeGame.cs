using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Поле і рух змійок — без жодного слова про кімнати, ставки й чат. Партія живе не від кліку до кліку, а
/// від тика: сервер рухає обох, він же вирішує, хто в що врізався, тож підкрутити з консолі нічого не вийде.
/// Поле зі стінами: виїхав за край — програв.
/// </summary>
/// <param name="rng">Сідований генератор кімнати: та сама партія з тим самим сідом повторюється до клітинки.</param>
/// <param name="tailShrinks">Хвіст звільняє клітинку на кожному кроці. false — слід лишається назавжди (мотоцикли).</param>
/// <param name="apples">Чи є на полі яблуко. false — рости нема від чого.</param>
public sealed class SnakeCore(Random rng, bool tailShrinks = true, bool apples = true)
{
    public const int W = 26, H = 18;
    public const int TickMs = 120;
    /// <summary>Скільки тиків «готуйсь» перед стартом (десь три секунди).</summary>
    public const int StartTicks = 25;
    const int StartLen = 3;
    /// <summary>Далі вже не пам'ять гравця, а хвіст лагу.</summary>
    public const int MaxQueued = 2;

    /// <summary>0 праворуч, 1 вниз, 2 ліворуч, 3 вгору.</summary>
    static readonly (int Dx, int Dy)[] Deltas = [(1, 0), (0, 1), (-1, 0), (0, -1)];

    readonly Queue<int> _turnsA = new(), _turnsB = new();

    /// <summary>Клітинки змійок, голова перша.</summary>
    public List<int> A { get; } = [];
    public List<int> B { get; } = [];
    public int DirA { get; set; }
    public int DirB { get; set; }
    /// <summary>Клітинка яблука; -1, коли яблук у цьому режимі нема.</summary>
    public int Apple { get; set; } = -1;
    /// <summary>Рахунок серії; переживає «Ще раз», бо цікаво грати до трьох.</summary>
    public int WinsA { get; set; }
    public int WinsB { get; set; }
    public int StartIn { get; set; } = StartTicks;

    public bool TailShrinks => tailShrinks;
    /// <summary>Тор (прохід №3, №155): стін по краю нема — виповз праворуч, з'явився ліворуч.</summary>
    public bool Wrap { get; set; }
    public bool Apples => apples;

    public static int Cell(int x, int y) => y * W + x;

    /// <summary>
    /// Нова партія: змійки по різних краях і на різних рядах — щоб ті, хто задумався на старті,
    /// не влетіли одне в одного лоб у лоб через десять тиків. Яблуко посередині.
    /// </summary>
    public void Reset()
    {
        A.Clear();
        B.Clear();
        var (yA, yB) = (H / 2 - 3, H / 2 + 3);
        for (var i = 0; i < StartLen; i++) A.Add(Cell(StartLen - i, yA));          // голова праворуч від хвоста
        for (var i = 0; i < StartLen; i++) B.Add(Cell(W - 1 - StartLen + i, yB));  // дзеркально
        DirA = 0;
        DirB = 2;
        _turnsA.Clear();
        _turnsB.Clear();
        Apple = apples ? Cell(W / 2, H / 2) : -1;
        StartIn = StartTicks;
    }

    /// <summary>
    /// Гравець просить повернути. Розворот на 180° і повтор того самого ігноруємо. Без черги два швидкі
    /// натиски злипаються в один: після «вгору» встигає записатись «вліво», перевірене проти «вгору», і на
    /// тику змійка йде вліво — собі в бік. Тому кожен наступний поворот міряємо від останнього в черзі.
    /// </summary>
    public void Turn(int seat, int dir)
    {
        if (dir is < 0 or > 3 || seat is < 0 or > 1) return;
        var queue = seat == 0 ? _turnsA : _turnsB;
        if (queue.Count >= MaxQueued) return;
        var last = queue.Count > 0 ? queue.Last() : seat == 0 ? DirA : DirB;
        if (dir == last || (dir + 2) % 4 == last) return;
        queue.Enqueue(dir);
    }

    /// <summary>Чи чекає в черзі поворот цього місця — бот не підкладає другого, поки не відпрацював перший.</summary>
    public bool Queued(int seat) => (seat == 0 ? _turnsA : _turnsB).Count > 0;

    /// <summary>Один крок обох змійок. Повертає, хто цього тика загинув.</summary>
    public (bool DeadA, bool DeadB) Step()
    {
        if (_turnsA.Count > 0) DirA = _turnsA.Dequeue();
        if (_turnsB.Count > 0) DirB = _turnsB.Dequeue();
        var (nextA, okA) = Next(A[0], DirA);
        var (nextB, okB) = Next(B[0], DirB);
        var ateA = apples && okA && nextA == Apple;
        var ateB = apples && okB && nextB == Apple;
        var growA = ateA || !tailShrinks;
        var growB = ateB || !tailShrinks;

        // Хвіст звільняє клітинку того ж тика, коли голова рушила — якщо змійка не росте.
        var bodyA = A.Take(A.Count - (growA ? 0 : 1)).ToHashSet();
        var bodyB = B.Take(B.Count - (growB ? 0 : 1)).ToHashSet();

        var deadA = !okA || bodyA.Contains(nextA) || bodyB.Contains(nextA) || (okB && nextA == nextB);
        var deadB = !okB || bodyB.Contains(nextB) || bodyA.Contains(nextB) || (okA && nextA == nextB);

        if (okA) { A.Insert(0, nextA); if (!growA) A.RemoveAt(A.Count - 1); }
        if (okB) { B.Insert(0, nextB); if (!growB) B.RemoveAt(B.Count - 1); }
        if (ateA || ateB) PlaceApple();
        return (deadA, deadB);
    }

    /// <summary>Наступна клітинка з урахуванням тору: на торі стіни нема ніколи.</summary>
    public (int Cell, bool Ok) Next(int head, int dir)
    {
        if (!Wrap) return Ahead(head, dir);
        var (dx, dy) = Deltas[dir];
        return (Cell((head % W + dx + W) % W, (head / W + dy + H) % H), true);
    }

    /// <summary>Куди дивиться голова; ok = false, якщо це вже стіна.</summary>
    public static (int Cell, bool Ok) Ahead(int head, int dir)
    {
        var (dx, dy) = Deltas[dir];
        var (x, y) = (head % W + dx, head / W + dy);
        return x < 0 || x >= W || y < 0 || y >= H ? (head, false) : (Cell(x, y), true);
    }

    /// <summary>Нове яблуко на вільній клітинці. Випадковість — тільки з переданого генератора.</summary>
    public void PlaceApple()
    {
        if (!apples) return;
        var busy = A.Concat(B).ToHashSet();
        if (busy.Count >= W * H) return;
        int cell;
        do { cell = rng.Next(W * H); } while (busy.Contains(cell));
        Apple = cell;
    }
}

/// <summary>
/// Змійка-дуель на двох. Порт старої гри: те саме поле, ті самі 120 мс, той самий рахунок серії, який
/// переживає «Ще раз», поки за столом ті самі двоє.
/// </summary>
public sealed class SnakeGame : Game
{
    public override GameInfo Info { get; } = new(
        "snake", "Змійка", "змійку", GameGroup.Live, 1, 2, TickMs: SnakeCore.TickMs, Rated: true,
        Options: [new("wrap", "Край поля", [("0", "стіни"), ("1", "🌀 тор: виповз праворуч — з'явився ліворуч")], "0"),
            LiveBots.LevelOption],
        Hint: "Дуель на двох: стрілки або WASD. Врізався в стіну, у себе чи в суперника — програв. Самому — з 🤖 ботом.");

    bool _wrap;
    /// <summary>«🤖 + бот»: кликали чи ні і якого рівня (опція столу).</summary>
    readonly SoloBot _solo = new();
    readonly SnakeBrain _brain = new();
    /// <summary>Місце бота в цій партії; −1 — партія людська.</summary>
    int _bot = -1;

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _wrap = options.GetValueOrDefault("wrap") == "1";
        if (_core is not null) _core.Wrap = _wrap;
        _solo.Configure(options);
    }

    public override bool ActsInLobby => true;

    public override string? CanStart() => _solo.CanStart(Ctx, 2);

    /// <summary>Куди сяде бот, якщо почати зараз; −1 — не сяде (не кликали або за столом двоє).</summary>
    int BotSeat() => _solo.Active(Ctx, 2) ? (Ctx.Seated(0) ? 1 : 0) : -1;

    public override string? SeatBot(int seat) => seat == _bot && !Ctx.Seated(seat) ? LiveBots.Name : null;

    /// <summary>Нік або «🤖 бот» — для рядків Журналу й рахунку серії.</summary>
    string Nick(int seat) => Ctx.NickOf(seat) ?? (seat == _bot ? LiveBots.Name : SeatName(seat));

    SnakeCore? _core;
    /// <summary>Хто сидів за столом на минулій партії — щоб знати, чи рахунок серії ще чийсь.</summary>
    string?[] _was = [];
    /// <summary>«x», «o», «draw» або null — те саме, що бачив старий фронт.</summary>
    string? _winner;

    /// <summary>Ядро готове ще до старту: стіл, що чекає на суперника, має виглядати як поле, а не як порожнеча.</summary>
    SnakeCore Core
    {
        get
        {
            if (_core is not null) return _core;
            _core = new SnakeCore(Ctx.Rng) { Wrap = _wrap };
            _core.Reset();
            return _core;
        }
    }

    public override string SeatName(int seat) => seat == 0 ? "жовта" : "зелена";

    public override void Start()
    {
        _bot = BotSeat();
        var now = new[] { Nick(0), Nick(1) };
        // «Ще раз» обертає місця — разом з ними їде й рахунок; будь-яка інша зміна складу його обнуляє.
        if (Same(_was, now)) { }
        else if (_was.Length == 2 && Same([_was[1], _was[0]], now)) (Core.WinsA, Core.WinsB) = (Core.WinsB, Core.WinsA);
        else (Core.WinsA, Core.WinsB) = (0, 0);
        _was = now;
        _winner = null;
        Core.Wrap = _wrap;
        Core.Reset();
    }

    static bool Same(string?[] a, string?[] b) =>
        a.Length == b.Length && a.Zip(b).All(p => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase));

    /// <summary>Реалтайм-ввід: повороти. Помилки нікого не цікавлять, наступний кадр усе перемалює.</summary>
    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action == LiveBots.Toggle) return _solo.Switch(Ctx, seat, payload, 2);
        if (action != "turn") return ActResult.Fail("Тут так не ходять");
        if (Dir(payload) is { } dir && seat != _bot) Core.Turn(seat, dir);
        return ActResult.Done;
    }

    static int? Dir(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.Number when payload.TryGetInt32(out var n) => n,
        JsonValueKind.Object when payload.TryGetProperty("dir", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var n) => n,
        _ => null,
    };

    public override TickResult Tick()
    {
        if (_winner is not null) return TickResult.None;
        if (Core.StartIn > 0)
        {
            Core.StartIn--;
            return TickResult.FrameOnly;
        }
        if (_bot >= 0) BotThink();
        var (deadA, deadB) = Core.Step();
        if (!deadA && !deadB) return TickResult.FrameOnly;

        _winner = deadA && deadB ? "draw" : deadA ? "o" : "x";
        if (_winner == "x") Core.WinsA++;
        else if (_winner == "o") Core.WinsB++;

        // з ботом — без нагород: людина одна, тож Rewards нічого не дасть і з нею в переможцях, а бота в них нема
        var solo = _bot >= 0 ? " (з 🤖 — без нагород)" : "";
        if (_winner == "draw")
        {
            Ctx.Finish([], $"{Info.Title}: {Nick(0)} {SeatName(0)} і {Nick(1)} {SeatName(1)} врізались одночасно{solo}",
                verdict: _bot >= 0 ? "🤝 Нічия: лоб у лоб із ботом" : null);
        }
        else
        {
            // Рахунок пишемо з боку переможця, щоб «2:1» читалось на його користь.
            var (won, lost) = _winner == "x" ? (0, 1) : (1, 0);
            var (score, other) = won == 0 ? (Core.WinsA, Core.WinsB) : (Core.WinsB, Core.WinsA);
            var log = $"{Info.Title}: {Nick(won)} {SeatName(won)} {score}:{other} {Nick(lost)} {SeatName(lost)}{solo}";
            if (_bot < 0) Ctx.Finish([won], log);
            else if (won == _bot) Ctx.Finish([], log, verdict: $"🤖 Бот переміг, {score}:{other}");
            else Ctx.Finish([won], log, verdict: $"🏆 {Nick(won)} — перемога над {LiveBots.Of(_solo.Level)} ботом, {score}:{other}");
        }
        return TickResult.Both;
    }

    /// <summary>Бот кладе поворот у ту саму чергу, що й людина, — раз на крок і лише коли попередній уже відпрацював.</summary>
    void BotThink()
    {
        var core = Core;
        if (core.Queued(_bot)) return;
        var (me, him) = _bot == 0 ? (core.A, core.B) : (core.B, core.A);
        _brain.Begin(SnakeCore.W, SnakeCore.H, core.Wrap);
        _brain.Body(me, 0, rival: false);
        _brain.Body(him, 0, rival: true);
        if (core.Apple >= 0) _brain.Food(core.Apple);
        if (_brain.Decide(me[0], _bot == 0 ? core.DirA : core.DirB, me.Count, _solo.Level, Ctx.Rng) is { } dir) core.Turn(_bot, dir);
    }

    public override object? Frame() => new
    {
        a = Core.A.ToArray(),
        b = Core.B.ToArray(),
        apple = Core.Apple,
        winsA = Core.WinsA,
        winsB = Core.WinsB,
        startIn = Core.StartIn,
        winner = _winner,
    };

    public override object View(int? seat) => new
    {
        width = SnakeCore.W,
        wrap = _wrap,
        height = SnakeCore.H,
        turn = (int?)null,
        a = Core.A.ToArray(),
        b = Core.B.ToArray(),
        apple = Core.Apple,
        winsA = Core.WinsA,
        winsB = Core.WinsB,
        startIn = Core.StartIn,
        winner = _winner,
        botOffer = _solo.Offer(Ctx, 2),
        botWanted = _solo.Wanted,
        botLvl = _solo.LevelKey,
        bot = _bot >= 0 && !Ctx.Seated(_bot) ? _bot : BotSeat() is var b && b >= 0 ? b : (int?)null,
    };
}
