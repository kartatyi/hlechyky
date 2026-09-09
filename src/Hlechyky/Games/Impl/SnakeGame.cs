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

    /// <summary>Один крок обох змійок. Повертає, хто цього тика загинув.</summary>
    public (bool DeadA, bool DeadB) Step()
    {
        if (_turnsA.Count > 0) DirA = _turnsA.Dequeue();
        if (_turnsB.Count > 0) DirB = _turnsB.Dequeue();
        var (nextA, okA) = Ahead(A[0], DirA);
        var (nextB, okB) = Ahead(B[0], DirB);
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
        "snake", "Змійка", "змійку", GameGroup.Live, 2, 2, TickMs: SnakeCore.TickMs, Rated: true,
        Hint: "Дуель на двох: стрілки або WASD, поле зі стінами. Врізався в стіну, у себе чи в суперника — програв.");

    SnakeCore? _core;
    /// <summary>Хто сидів за столом на минулій партії — щоб знати, чи рахунок серії ще чийсь.</summary>
    string?[] _was = [];
    /// <summary>«x», «o», «draw» або null — те саме, що бачив старий фронт.</summary>
    string? _winner;

    SnakeCore Core => _core ??= new SnakeCore(Ctx.Rng);

    public override string SeatName(int seat) => seat == 0 ? "жовта" : "зелена";

    public override void Start()
    {
        var now = new[] { Ctx.NickOf(0), Ctx.NickOf(1) };
        // «Ще раз» обертає місця — разом з ними їде й рахунок; будь-яка інша зміна складу його обнуляє.
        if (Same(_was, now)) { }
        else if (_was.Length == 2 && Same([_was[1], _was[0]], now)) (Core.WinsA, Core.WinsB) = (Core.WinsB, Core.WinsA);
        else (Core.WinsA, Core.WinsB) = (0, 0);
        _was = now;
        _winner = null;
        Core.Reset();
    }

    static bool Same(string?[] a, string?[] b) =>
        a.Length == b.Length && a.Zip(b).All(p => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase));

    /// <summary>Реалтайм-ввід: повороти. Помилки нікого не цікавлять, наступний кадр усе перемалює.</summary>
    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action != "turn") return ActResult.Fail("Тут так не ходять");
        if (Dir(payload) is { } dir) Core.Turn(seat, dir);
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
        var (deadA, deadB) = Core.Step();
        if (!deadA && !deadB) return TickResult.FrameOnly;

        _winner = deadA && deadB ? "draw" : deadA ? "o" : "x";
        if (_winner == "x") Core.WinsA++;
        else if (_winner == "o") Core.WinsB++;

        if (_winner == "draw")
        {
            Ctx.Finish([], $"{Info.Title}: {Ctx.NickOf(0)} {SeatName(0)} і {Ctx.NickOf(1)} {SeatName(1)} врізались одночасно");
        }
        else
        {
            // Рахунок пишемо з боку переможця, щоб «2:1» читалось на його користь.
            var (won, lost) = _winner == "x" ? (0, 1) : (1, 0);
            var (score, other) = won == 0 ? (Core.WinsA, Core.WinsB) : (Core.WinsB, Core.WinsA);
            Ctx.Finish([won], $"{Info.Title}: {Ctx.NickOf(won)} {SeatName(won)} {score}:{other} {Ctx.NickOf(lost)} {SeatName(lost)}");
        }
        return TickResult.Both;
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
        height = SnakeCore.H,
        turn = (int?)null,
        a = Core.A.ToArray(),
        b = Core.B.ToArray(),
        apple = Core.Apple,
        winsA = Core.WinsA,
        winsB = Core.WinsB,
        startIn = Core.StartIn,
        winner = _winner,
    };
}
