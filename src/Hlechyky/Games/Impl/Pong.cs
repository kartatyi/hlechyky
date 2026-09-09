using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Поле, дві ракетки й м'яч — без жодного слова про кімнати, ставки й чат. Усе в дробових одиницях і в
/// секундах: сітки тут нема, а 25 кадрів на секунду — це те, що встигає долетіти до браузера, а не крок
/// самої фізики. Клієнт малює лише те, що йому наказали кадром, тож підкрутити м'яч з консолі не вийде.
/// </summary>
/// <param name="rng">Сідований генератор кімнати: та сама партія з тим самим сідом подає під тим самим кутом.</param>
public sealed class PongCore(Random rng)
{
    /// <summary>Поле 160 × 100. Вісь y дивиться вниз, як у канвасі: 0 — верх.</summary>
    public const double W = 160, H = 100;
    /// <summary>Центр лівої ракетки по x; права стоїть дзеркально (W − PaddleX = 156).</summary>
    public const double PaddleX = 4;
    public const double PaddleH = 18, PaddleW = 2;
    /// <summary>Скільки одиниць на секунду проїжджає ракетка — і від клавіш, і від пальця.</summary>
    public const double PaddleSpeed = 60;
    public const double BallR = 1.5;
    public const double StartSpeed = 60, MaxSpeed = 160;
    /// <summary>Кожен відбій від ракетки додає 6% швидкості, поки не впремось у стелю.</summary>
    public const double SpeedUp = 1.06;
    /// <summary>Подача летить під випадковим кутом ±30°, відбій від ракетки — до ±60° від її краю.</summary>
    public const double ServeAngle = 30, BounceAngle = 60;

    public const int TickMs = 40;
    /// <summary>Крок симуляції в секундах: 40 мс — це 25 Гц.</summary>
    public const double Dt = TickMs / 1000.0;
    /// <summary>«Готуйсь» на старті партії — три секунди.</summary>
    public const int StartTicks = 75;
    /// <summary>Пауза після гола — секунда, щоб очима встигнути за рахунком.</summary>
    public const int ServeTicks = 25;
    public const int Target = 7;

    /// <summary>Куди можна заїхати центром ракетки, щоб вона лишалась у полі.</summary>
    public const double MinY = PaddleH / 2, MaxY = H - PaddleH / 2;

    readonly int[] _dir = new int[2];
    readonly double?[] _aim = new double?[2];
    /// <summary>Куди подавати наступного разу: −1 ліворуч, +1 праворуч, 0 — ще не знаємо (перша подача).</summary>
    int _next;

    /// <summary>Центри ракеток по y; місце 0 — ліва.</summary>
    public double[] P { get; } = [H / 2, H / 2];
    /// <summary>Рахунок партії.</summary>
    public int[] S { get; } = new int[2];
    public double Bx { get; set; } = W / 2;
    public double By { get; set; } = H / 2;
    public double Vx { get; set; }
    public double Vy { get; set; }
    /// <summary>Скільки тиків лишилось до першого удару; 0 — уже граємо.</summary>
    public int StartIn { get; set; } = StartTicks;
    /// <summary>Скільки тиків м'яч ще чекає в центрі після гола.</summary>
    public int ServeIn { get; set; }
    /// <summary>Номер тика — з нього клієнтський інтерполятор розуміє, які кадри сусідні.</summary>
    public int T { get; set; }

    public double Speed => Math.Sqrt(Vx * Vx + Vy * Vy);

    /// <summary>Площина, від якої відбивається центр м'яча: край ракетки плюс радіус.</summary>
    public static double Plane(int seat) => seat == 0 ? PaddleX + PaddleW / 2 + BallR : W - PaddleX - PaddleW / 2 - BallR;

    /// <summary>Нова партія: ракетки посередині, м'яч у центрі, три секунди «готуйсь».</summary>
    public void Reset()
    {
        P[0] = P[1] = H / 2;
        S[0] = S[1] = 0;
        _dir[0] = _dir[1] = 0;
        _aim[0] = _aim[1] = null;
        _next = 0;
        T = 0;
        StartIn = StartTicks;
        ServeIn = 0;   // на старті чекає «готуйсь», а не пауза після гола
        Bx = W / 2;
        By = H / 2;
        Vx = Vy = 0;
    }

    /// <summary>Клавіші: −1 вгору, 0 стоп, +1 вниз. Діє, поки не прийде наступний ввід.</summary>
    public void Move(int seat, int dir)
    {
        if (seat is < 0 or > 1) return;
        _dir[seat] = Math.Sign(dir);
        _aim[seat] = null;
    }

    /// <summary>Палець або миша: бажаний центр ракетки. Сервер сам довезе її туди зі своєю швидкістю.</summary>
    public void Aim(int seat, double y)
    {
        if (seat is < 0 or > 1 || !double.IsFinite(y)) return;
        _aim[seat] = Math.Clamp(y, MinY, MaxY);
        _dir[seat] = 0;
    }

    /// <summary>Один крок симуляції. Повертає місце, яке щойно забило, або null.</summary>
    public int? Step()
    {
        T++;
        MovePaddles();
        // Поки йде «готуйсь» чи пауза після гола, м'яч висить у центрі, а ракетки вже можна розім'яти.
        if (StartIn > 0)
        {
            StartIn--;
            if (StartIn == 0) Launch(_next);
            return null;
        }
        if (ServeIn > 0)
        {
            ServeIn--;
            if (ServeIn == 0) Launch(_next);
            return null;
        }

        var (x0, y0) = (Bx, By);
        Bx += Vx * Dt;
        By += Vy * Dt;
        Hit(0, x0, y0);
        Hit(1, x0, y0);
        Walls();

        if (Bx >= 0 && Bx <= W) return null;
        var scorer = Bx < 0 ? 1 : 0;          // за лівий край вилетів — пропустила ліва
        S[scorer]++;
        _next = scorer == 1 ? -1 : 1;         // подача в бік того, хто пропустив
        Center();
        return scorer;
    }

    void MovePaddles()
    {
        var step = PaddleSpeed * Dt;
        for (var seat = 0; seat < 2; seat++)
        {
            // Палець може смикнути через усе поле, але ракетка все одно їде зі своєю швидкістю —
            // інакше «to» був би телепортом, а миша — читом проти клавіатури.
            if (_aim[seat] is { } want) P[seat] += Math.Clamp(want - P[seat], -step, step);
            else P[seat] += _dir[seat] * step;
            P[seat] = Math.Clamp(P[seat], MinY, MaxY);
        }
    }

    /// <summary>Верх і низ — дзеркало: скільки м'яч заліз за край, стільки й відскочив назад.</summary>
    void Walls()
    {
        if (By < BallR && Vy < 0)
        {
            By = 2 * BallR - By;
            Vy = -Vy;
        }
        else if (By > H - BallR && Vy > 0)
        {
            By = 2 * (H - BallR) - By;
            Vy = -Vy;
        }
    }

    /// <summary>
    /// Відбій від ракетки. Дивимось не на кінцеву точку, а на весь відрізок руху: на 160 од/с м'яч
    /// проходить за тик 6.4 одиниці й міг би прошити ракетку наскрізь, ніде її не «торкнувшись».
    /// </summary>
    void Hit(int seat, double x0, double y0)
    {
        var toward = seat == 0 ? Vx < 0 : Vx > 0;
        if (!toward) return;
        var plane = Plane(seat);
        var crossed = seat == 0 ? x0 >= plane && Bx <= plane : x0 <= plane && Bx >= plane;
        if (!crossed) return;

        // Де саме м'яч перетнув площину ракетки — там і вирішується, куди він полетить.
        var dx = Bx - x0;
        var k = Math.Abs(dx) < 1e-9 ? 0 : (plane - x0) / dx;
        // Стіни рахуємо вже після ракетки, тож пряма з y0 у By могла за той самий тик вилізти за поле:
        // м'яч, що відскочив від стелі просто в ракетку, інакше «промазував» би повз неї — і в гол.
        var y = Fold(y0 + (By - y0) * k);
        var off = y - P[seat];
        if (Math.Abs(off) > PaddleH / 2 + BallR) return;   // повз ракетку — це вже гол, не відбій

        // Кут задає точка попадання: центр віддає м'яч горизонтально, край — під 60°.
        var rel = Math.Clamp(off / (PaddleH / 2), -1, 1);
        var angle = rel * BounceAngle * Math.PI / 180;
        var speed = Math.Min(MaxSpeed, Speed * SpeedUp);
        var away = seat == 0 ? 1 : -1;
        Vx = away * speed * Math.Cos(angle);
        Vy = speed * Math.Sin(angle);
        Bx = plane + away * Math.Abs(Bx - plane);
        By = y;
    }

    /// <summary>
    /// Скласти точку назад у поле: за тик м'яч долає щонайбільше 5.6 одиниці по y, тож одного дзеркала
    /// досить, а спрацьовує воно лише тоді, коли стіна трапилась раніше за площину ракетки.
    /// </summary>
    static double Fold(double y) =>
        y < BallR ? 2 * BallR - y : y > H - BallR ? 2 * (H - BallR) - y : y;

    /// <summary>М'яч у центр і без руху: далі його підніме Launch, коли добіжить відлік.</summary>
    void Center()
    {
        Bx = W / 2;
        By = H / 2;
        Vx = Vy = 0;
        ServeIn = ServeTicks;
    }

    /// <summary>Подача: 60 од/с під випадковим кутом ±30°. dir = 0 — перша подача, бік теж випадковий.</summary>
    public void Launch(int dir)
    {
        if (dir == 0) dir = rng.Next(2) == 0 ? -1 : 1;
        var angle = (rng.NextDouble() * 2 - 1) * ServeAngle * Math.PI / 180;
        Bx = W / 2;
        By = H / 2;
        Vx = Math.Sign(dir) * StartSpeed * Math.Cos(angle);
        Vy = StartSpeed * Math.Sin(angle);
    }
}

/// <summary>
/// Понг на двох. Правила й стан живуть тільки тут: браузер шле наміри («тримаю вгору», «палець отут»),
/// а назад отримує кадр із дробовими координатами, який домальовує інтерполятором.
/// </summary>
public sealed class Pong : Game
{
    public override GameInfo Info { get; } = new(
        "pong", "Понг", "понг", GameGroup.Live, 2, 2, TickMs: PongCore.TickMs, Rated: true,
        Hint: "Дві ракетки, м'ячик. До семи. Стрілки ↑↓ або W/S, на телефоні — тягни пальцем");

    PongCore? _core;
    /// <summary>Місце переможця; null — ще граємо (або партія скінчилась без переможця).</summary>
    int? _winner;
    /// <summary>Партію закрито — сьомим очком чи тим, що хтось встав з-за столу.</summary>
    bool _over;

    /// <summary>Ядро готове ще до старту: стіл, що чекає на суперника, має виглядати як поле, а не як порожнеча.</summary>
    PongCore Core
    {
        get
        {
            if (_core is not null) return _core;
            _core = new PongCore(Ctx.Rng);
            _core.Reset();
            return _core;
        }
    }

    public override string SeatName(int seat) => seat == 0 ? "ліва" : "права";

    public override void Start()
    {
        _winner = null;
        _over = false;
        Core.Reset();
    }

    /// <summary>
    /// Техпоразка теж закриває партію: інакше вид і кадр лишились би у фазі «граємо», і клієнт не
    /// намалював би підсумку — на полі просто застигла б картинка.
    /// </summary>
    public override void OnLeave(int seat)
    {
        var other = 1 - seat;
        if (Ctx.Seated(other)) _winner = other;   // суперника вже нема — це не перемога, а обірвана партія
        _over = true;
        base.OnLeave(seat);
    }

    /// <summary>
    /// Реалтайм-ввід. Відповіді на нього ніхто не чекає (Rooms.Input ковтає і Fail, і GameError), тож
    /// незрозумілий payload просто ігноруємо: наступний кадр однаково покаже правду.
    /// </summary>
    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        switch (action)
        {
            case "move":
                if (Num(payload, "dir") is { } dir) Core.Move(seat, Math.Sign(dir));
                return ActResult.Done;
            case "to":
                if (Num(payload, "y") is { } y) Core.Aim(seat, y);
                return ActResult.Done;
            default:
                return ActResult.Fail("Тут так не ходять");
        }
    }

    /// <summary>Приймаємо і голе число, і <c>{ dir: -1 }</c> — клієнтам так простіше.</summary>
    static double? Num(JsonElement payload, string name) => payload.ValueKind switch
    {
        JsonValueKind.Number when payload.TryGetDouble(out var n) => n,
        JsonValueKind.Object when payload.TryGetProperty(name, out var p)
            && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var n) => n,
        _ => null,
    };

    public override TickResult Tick()
    {
        if (_over) return TickResult.None;

        var wasReady = Core.StartIn > 0;
        var scorer = Core.Step();
        // Кадр летить щотика, а повний вид — лише коли змінилось те, що в кадрі не видно:
        // фаза «готуйсь» → «граємо», рахунок і кінець партії.
        if (scorer is null) return wasReady && Core.StartIn == 0 ? TickResult.Both : TickResult.FrameOnly;
        if (Core.S[scorer.Value] < PongCore.Target) return TickResult.Both;

        _winner = scorer;
        _over = true;
        var lost = 1 - scorer.Value;
        // Ніки чужі, відмінювати їх нема як, тому рахунок замість речення з відмінками.
        Ctx.Finish([scorer.Value],
            $"{Info.Title}: {Ctx.NickOf(scorer.Value)} {SeatName(scorer.Value)} {Core.S[scorer.Value]}:{Core.S[lost]} {Ctx.NickOf(lost)} {SeatName(lost)}");
        return TickResult.Both;
    }

    public override object? Frame() => Shot();

    public override object View(int? seat) => new
    {
        scores = new[] { Core.S[0], Core.S[1] },
        phase = _over ? "done" : Core.StartIn > 0 ? "ready" : "play",
        startIn = Core.StartIn,
        winner = _winner,
        target = PongCore.Target,
        turn = (int?)null,   // ходів тут нема, але каркас питає це поле в кожної гри
        frame = Shot(),      // щоб картка намалювала поле ще до першого кадру
    };

    /// <summary>
    /// Кадр: усе, що потрібно для малювання, і нічого зайвого. Округлення до 0.1 — не про точність, а про
    /// розмір: 25 таких повідомлень на секунду на кожного глядача.
    /// </summary>
    object Shot() => new
    {
        t = Core.T,
        bx = R(Core.Bx),
        by = R(Core.By),
        vx = R(Core.Vx),
        vy = R(Core.Vy),
        p = new[] { R(Core.P[0]), R(Core.P[1]) },
        s = new[] { Core.S[0], Core.S[1] },
        serveIn = Core.ServeIn,
        // Відлік «готуйсь» іде в кадрі, а не лише у виді: види летять раз на фазу, а цифру на полі
        // треба міняти щосекунди — і статус картки каркас теж бере з кадру (INTEGRATION-NOTES §3).
        startIn = Core.StartIn,
        winner = _winner,
    };

    static double R(double v) => Math.Round(v, 1);
}
