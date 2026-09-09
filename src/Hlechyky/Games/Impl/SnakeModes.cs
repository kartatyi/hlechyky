using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Дрібниці, спільні для обох режимів змійки. Клієнт шле <c>{ dir: 2 }</c>, але голе число теж приймаємо —
/// так само, як це робить дуель у <see cref="SnakeGame"/>: одна форма payload на всю родину.
/// </summary>
static class SnakeModesTurns
{
    public static int? Dir(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.Number when payload.TryGetInt32(out var n) => n,
        JsonValueKind.Object when payload.TryGetProperty("dir", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var n) => n,
        _ => null,
    };

    /// <summary>Той самий склад за столом, що й минулого разу — від цього залежить, чи жити рахунку серії.</summary>
    public static bool Same(string?[] a, string?[] b) =>
        a.Length == b.Length && a.Zip(b).All(p => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Мотоцикли: та сама змійка, але хвіст не коротшає і яблук нема — за кожним тягнеться стіна, яка лишається
/// до кінця раунду. Ядро беремо готове (<see cref="SnakeCore"/> з <c>tailShrinks: false</c>), бо різниця між
/// дуеллю і мотоциклами — рівно два прапорці, а не нові правила.
/// </summary>
public sealed class TronGame : Game
{
    /// <summary>Швидше за змійку: слід росте щотика, і на 120 мс поле закінчувалось би надто мляво.</summary>
    public const int TickMs = 100;
    /// <summary>Три секунди «готуйсь». Ядро рахує їх під 120 мс дуелі, тож своє число ставимо самі.</summary>
    public const int StartTicks = 30;
    /// <summary>
    /// Хвилина на раунд. Двом слідам на полі в 468 клітинок стільки не протриматись — це страховка від
    /// вічного раунду, а не правило, з яким доведеться рахуватись гравцям. Саме тому межа не константа:
    /// звичайною грою її не дістати, а неперевіреної гілки в грі бути не має — тест опускає число і
    /// проходить нічию по-справжньому.
    /// </summary>
    public int MaxMoves { get; set; } = 600;

    public override GameInfo Info { get; } = new(
        "tron", "Мотоцикли", "мотоцикли", GameGroup.Live, 2, 2, TickMs: TickMs, Rated: true,
        Hint: "За тобою тягнеться стіна, яка не зникає. Хто врізався перший — програв. Стрілки або WASD",
        Client: "snake-modes");   // обидва режими малює один web/games/snake-modes.js

    SnakeCore? _core;
    /// <summary>Хто сидів за столом на минулій партії — щоб знати, чи рахунок серії ще чийсь.</summary>
    string?[] _was = [];
    /// <summary>«x», «o», «draw» або null — та сама мова, що й у дуелі.</summary>
    string? _winner;
    int _moves;

    /// <summary>Скільки кроків зробили мотоцикли в цьому раунді (відлік «готуйсь» сюди не рахується).</summary>
    public int Moves => _moves;

    /// <summary>Поле готове ще до старту: стіл, що чекає на суперника, має виглядати як поле, а не як порожнеча.</summary>
    SnakeCore Core
    {
        get
        {
            if (_core is not null) return _core;
            _core = new SnakeCore(Ctx.Rng, tailShrinks: false, apples: false);
            NewRound();
            return _core;
        }
    }

    void NewRound()
    {
        var core = Core;          // рекурсії тут нема: гетер спершу кладе ядро в поле, а вже потім кличе нас
        core.Reset();
        core.StartIn = StartTicks;
        _moves = 0;
    }

    public override string SeatName(int seat) => seat == 0 ? "жовтий" : "зелений";

    public override void Start()
    {
        var now = new[] { Ctx.NickOf(0), Ctx.NickOf(1) };
        // «Ще раз» обертає місця — разом з ними їде й рахунок; будь-яка інша зміна складу його обнуляє.
        if (SnakeModesTurns.Same(_was, now)) { }
        else if (_was.Length == 2 && SnakeModesTurns.Same([_was[1], _was[0]], now)) (Core.WinsA, Core.WinsB) = (Core.WinsB, Core.WinsA);
        else (Core.WinsA, Core.WinsB) = (0, 0);
        _was = now;
        _winner = null;
        NewRound();
    }

    /// <summary>
    /// Встав посеред раунду — техпоразка. Базовий <see cref="Game.OnLeave"/> сам порахує, кому дісталась
    /// перемога, а нам лишається закрити раунд і в самій грі: без цього <c>winner</c> лишався б null, і
    /// клієнт не притемнив би поле — воно застигло б яскравим, ніби партія ще триває.
    /// </summary>
    public override void OnLeave(int seat)
    {
        _winner = seat == 0 ? "o" : "x";
        base.OnLeave(seat);
    }

    /// <summary>Реалтайм-ввід: повороти. Помилки нікого не цікавлять, наступний кадр усе перемалює.</summary>
    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action != "turn") return ActResult.Fail("Тут так не ходять");
        if (SnakeModesTurns.Dir(payload) is { } dir) Core.Turn(seat, dir);
        return ActResult.Done;
    }

    public override TickResult Tick()
    {
        if (_winner is not null) return TickResult.None;
        if (Core.StartIn > 0)
        {
            Core.StartIn--;
            return TickResult.FrameOnly;
        }

        var (deadA, deadB) = Core.Step();
        _moves++;
        if (!deadA && !deadB)
        {
            if (_moves < MaxMoves) return TickResult.FrameOnly;
            _winner = "draw";
            Ctx.Finish([], $"{Info.Title}: хвилина минула, {Ctx.NickOf(0)} і {Ctx.NickOf(1)} розійшлись внічию");
            return TickResult.Both;
        }

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

    /// <summary>
    /// Кадр — дельта: самі голови. Сліди ростуть до сотень клітинок, і слати їх двадцять разів на секунду
    /// означало б класти канал заради даних, які в клієнта вже є. Повний стан живе у <see cref="View"/>,
    /// і на кожну подію <c>room</c> клієнт перемальовує поле з нуля.
    /// </summary>
    public override object? Frame() => new
    {
        ha = Core.A[0],
        hb = Core.B[0],
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
        dirA = Core.DirA,
        dirB = Core.DirB,
        winsA = Core.WinsA,
        winsB = Core.WinsB,
        startIn = Core.StartIn,
        winner = _winner,
    };
}

/// <summary>
/// Одна змійка на двох. Ядро дуелі тут не годиться: воно завжди рухає дві змійки й тримає дві черги
/// поворотів, а нам потрібна одна змійка й одна спільна черга — інакше два натиски різних гравців злипались
/// би в один тик. Геометрію поля (розмір, крок через стіну) беремо з <see cref="SnakeCore"/>, щоб мотоцикли,
/// дуель і кооп грали на тому самому полі.
/// </summary>
/// <param name="rng">Сідований генератор кімнати: яблука з тим самим сідом лягають однаково.</param>
public sealed class CoopSnakeCore(Random rng)
{
    const int StartLen = 3;
    /// <summary>Далі вже не пам'ять гравця, а хвіст лагу — та сама межа, що й у дуелі.</summary>
    public const int MaxQueued = SnakeCore.MaxQueued;
    /// <summary>Скільки тиків «готуйсь»: 25 × 120 мс — рівно три секунди, як у дуелі.</summary>
    public const int StartTicks = SnakeCore.StartTicks;

    /// <summary>Спільна черга на двох: чий поворот прийшов першим, той і застосується першим тиком.</summary>
    readonly Queue<int> _turns = new();

    /// <summary>Клітинки змійки, голова перша.</summary>
    public List<int> S { get; } = [];
    public int Dir { get; set; }
    public int Apple { get; set; } = -1;
    public int StartIn { get; set; } = StartTicks;
    public int Len => S.Count;

    /// <summary>Чия це вісь: місце 0 крутить вгору-вниз (1 і 3), місце 1 — вліво-вправо (0 і 2).</summary>
    public static bool OwnAxis(int seat, int dir) => seat == 0 ? dir is 1 or 3 : seat == 1 && dir is 0 or 2;

    /// <summary>Нова партія: змійка посеред поля, дивиться праворуч; яблуко — куди лягло з генератора.</summary>
    public void Reset()
    {
        S.Clear();
        var y = SnakeCore.H / 2;
        for (var i = 0; i < StartLen; i++) S.Add(SnakeCore.Cell(SnakeCore.W / 2 - i, y));   // голова праворуч від хвоста
        Dir = 0;
        _turns.Clear();
        StartIn = StartTicks;
        Apple = -1;
        PlaceApple();
    }

    /// <summary>
    /// Гравець просить повернути. Чужа вісь — мовчки повз (це не помилка, а домовленість: один крутить
    /// вертикаль, другий горизонталь). Далі те саме, що в дуелі: розворот на 180° і повтор не беремо, а
    /// кожен наступний поворот міряємо від останнього в черзі, щоб два швидкі натиски не злипались.
    /// </summary>
    public bool Turn(int seat, int dir)
    {
        if (dir is < 0 or > 3 || !OwnAxis(seat, dir)) return false;
        if (_turns.Count >= MaxQueued) return false;
        var last = _turns.Count > 0 ? _turns.Last() : Dir;
        if (dir == last || (dir + 2) % 4 == last) return false;
        _turns.Enqueue(dir);
        return true;
    }

    /// <summary>Один крок змійки. Повертає true, якщо цим кроком вона врізалась.</summary>
    public bool Step()
    {
        if (_turns.Count > 0) Dir = _turns.Dequeue();
        var (next, ok) = SnakeCore.Ahead(S[0], Dir);
        if (!ok) return true;                       // стіна: голову за поле не пхаємо

        var ate = next == Apple;
        // Хвіст звільняє клітинку того ж тика, коли голова рушила — якщо змійка не росте.
        var body = S.Take(S.Count - (ate ? 0 : 1)).ToHashSet();
        var dead = body.Contains(next);

        S.Insert(0, next);
        if (!ate) S.RemoveAt(S.Count - 1);
        else PlaceApple();
        return dead;
    }

    /// <summary>Нове яблуко на вільній клітинці. Випадковість — тільки з переданого генератора.</summary>
    public void PlaceApple()
    {
        var busy = S.ToHashSet();
        if (busy.Count >= SnakeCore.W * SnakeCore.H) { Apple = -1; return; }
        int cell;
        do { cell = rng.Next(SnakeCore.W * SnakeCore.H); } while (busy.Contains(cell));
        Apple = cell;
    }
}

/// <summary>
/// Змійка на двох: одна змійка, одне яблуко, двоє за кермом. Місце 0 відповідає за вертикаль, місце 1 — за
/// горизонталь, тож повернути наліво без напарника не вийде. Партія не рейтингова: тут нема з ким змагатись,
/// зате є спільний рекорд пари — довжина, з якою змійка врізалась.
/// </summary>
public sealed class SnakeCoopGame : Game
{
    public override GameInfo Info { get; } = new(
        "snake-coop", "Змійка на двох", "змійку на двох", GameGroup.Live, 2, 2,
        TickMs: SnakeCore.TickMs, Rated: false, Score: ScoreOrder.HigherIsBetter,
        Hint: "Одна змійка на двох: один крутить вгору-вниз, другий — вліво-вправо. Домовляйтесь!",
        Client: "snake-modes");

    CoopSnakeCore? _core;
    /// <summary>Раунд дограно: змійка врізалась. Далі тикати нема чого.</summary>
    bool _over;

    CoopSnakeCore Core
    {
        get
        {
            if (_core is not null) return _core;
            _core = new CoopSnakeCore(Ctx.Rng);
            _core.Reset();
            return _core;
        }
    }

    /// <summary>Підпис місця — це і є вся інструкція: людина бачить свою вісь у чіпі над полем.</summary>
    public override string SeatName(int seat) => seat == 0 ? "вгору-вниз" : "вліво-вправо";

    public override void Start()
    {
        _over = false;
        Core.Reset();
    }

    /// <summary>Напарник встав — раунд скінчився. Закриваємо його й тут, щоб клієнт притемнив поле.</summary>
    public override void OnLeave(int seat)
    {
        _over = true;
        base.OnLeave(seat);
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action != "turn") return ActResult.Fail("Тут так не ходять");
        if (SnakeModesTurns.Dir(payload) is not { } dir) return ActResult.Done;
        // Чужа вісь — не поламаний хід, а звичайне «це не твоя кнопка»: кажемо коротко й не міняємо стану.
        if (!CoopSnakeCore.OwnAxis(seat, dir))
            return ActResult.Fail(seat == 0 ? "Ти крутиш вгору-вниз" : "Ти крутиш вліво-вправо");
        Core.Turn(seat, dir);
        return ActResult.Done;
    }

    public override TickResult Tick()
    {
        if (_over) return TickResult.None;
        if (Core.StartIn > 0)
        {
            Core.StartIn--;
            return TickResult.FrameOnly;
        }
        if (!Core.Step()) return TickResult.FrameOnly;

        _over = true;
        var len = Core.Len;
        // Кооп: результатом партії йде довжина — з неї і збереться таблиця пар.
        Ctx.Score(0, len);
        Ctx.Score(1, len);
        // Переможців тут нема: у кооперативі програти одне одному неможливо, а «перемога» обом коштувала б
        // дорого — ачівки перемог («Перша перемога», «Серія», «Десять перемог») каркас видає повз стелю
        // черепків (Economy/Rewards.cs: цикл ачівок стоїть поза `if (rewarded)`), тож пара вибивала б їх
        // за кілька хвилин у грі, де не можна програти. Порожній список — це нічия: DrawReward обом і
        // жодних перемог. Довжина від цього не губиться, вона йде окремо, у Scores.
        Ctx.Finish([],
            $"{Info.Title}: {Ctx.NickOf(0)} і {Ctx.NickOf(1)} виростили змійку до {len}",
            new Dictionary<int, long> { [0] = len, [1] = len });
        return TickResult.Both;
    }

    /// <summary>Змійка одна й росте лише з яблук, тож повний стан у кадрі коштує дешево — дельта тут зайва.</summary>
    public override object? Frame() => new
    {
        s = Core.S.ToArray(),
        apple = Core.Apple,
        dir = Core.Dir,
        startIn = Core.StartIn,
        len = Core.Len,
        winner = Winner,
    };

    public override object View(int? seat) => new
    {
        width = SnakeCore.W,
        height = SnakeCore.H,
        turn = (int?)null,
        s = Core.S.ToArray(),
        apple = Core.Apple,
        dir = Core.Dir,
        startIn = Core.StartIn,
        len = Core.Len,
        winner = Winner,
    };

    /// <summary>Переможця тут нема — є кінець раунду. Клієнтові цього досить, щоб притемнити поле.</summary>
    string? Winner => _over ? "end" : null;
}
