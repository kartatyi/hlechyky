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
    /// <summary>
    /// Підкрутка: ракетка, що в мить удару їде, додає до кута ще стільки градусів у свій бік (але не
    /// більше за <see cref="BounceAngle"/>). Без неї кут вирішувала лише точка попадання, і партія
    /// зводилась до «підстав центр» — з нею є чим обіграти суперника.
    /// </summary>
    public const double SpinAngle = 15;

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
    /// <summary>Куди ракетка справді зрушила за останній тик: −1, 0, +1 — для підкрутки.</summary>
    readonly int[] _moved = new int[2];
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
    /// <summary>Скільки ударів поспіль без гола — клієнт показує серію, коли розіграш затягнувся.</summary>
    public int Rally { get; private set; }
    /// <summary>Місце, що відбило м'яч останнім у цьому тику; клієнт по ньому спалахує ракеткою й клацає.</summary>
    public int? HitBy { get; private set; }

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
        _moved[0] = _moved[1] = 0;
        _next = 0;
        T = 0;
        Rally = 0;
        HitBy = null;
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
        HitBy = null;
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
            var was = P[seat];
            if (_aim[seat] is { } want) P[seat] += Math.Clamp(want - P[seat], -step, step);
            else P[seat] += _dir[seat] * step;
            P[seat] = Math.Clamp(P[seat], MinY, MaxY);
            // Підкрутку дає лише помітний рух, а не тремтіння пальця на пів одиниці біля цілі.
            var d = P[seat] - was;
            _moved[seat] = Math.Abs(d) >= step / 2 ? Math.Sign(d) : 0;
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
        var deg = Math.Clamp(rel * BounceAngle + _moved[seat] * SpinAngle, -BounceAngle, BounceAngle);
        var angle = deg * Math.PI / 180;
        var speed = Math.Min(MaxSpeed, Speed * SpeedUp);
        var away = seat == 0 ? 1 : -1;
        Vx = away * speed * Math.Cos(angle);
        Vy = speed * Math.Sin(angle);
        Bx = plane + away * Math.Abs(Bx - plane);
        By = y;
        Rally++;
        HitBy = seat;
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
        Rally = 0;
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
/// Арена на трьох-чотирьох: квадратне поле, кожен стереже свою стіну (0 — ліва, 1 — права, 2 — верхня,
/// 3 — нижня). Пропустив — мінус життя; життя скінчились — твоя стіна стає глухою, і м'яч від неї просто
/// відскакує. Хто лишився останнім, той і взяв арену. Кути поля — глухі квадрати: без них м'яч, що летить
/// у кут, не мав би чесного господаря, а ракетки двох сусідів лізли б одна на одну.
/// </summary>
public sealed class PongArena(Random rng)
{
    /// <summary>Поле S × S. Вісь y — вниз, як у канвасі.</summary>
    public const double S = 120;
    /// <summary>Сторона глухого квадрата в кожному куті.</summary>
    public const double Corner = 14;
    /// <summary>Центр ракетки від свого краю поля.</summary>
    public const double PaddleOff = 4;
    public const double PaddleL = 22, PaddleW = 2;
    public const double PaddleSpeed = 75;
    public const double BallR = 1.8;
    public const double StartSpeed = 55, MaxSpeed = 140, SpeedUp = 1.05;
    public const double ServeAngle = 25, BounceAngle = 60, SpinAngle = 15;
    /// <summary>
    /// Після відбою від глухої стіни чи кута м'яч мусить іти вздовж неї щонайменше з такою часткою
    /// швидкості. Інакше між двома глухими стінами навпроти він стрибав би туди-сюди вічно.
    /// </summary>
    public const double MinSlide = 0.25;
    public const int Seats = 4;
    public const int TickMs = PongCore.TickMs;
    public const double Dt = TickMs / 1000.0;
    public const int StartTicks = PongCore.StartTicks;
    /// <summary>Після гола трохи довша пауза, ніж на двох: треба встигнути побачити, в кого мінус.</summary>
    public const int ServeTicks = 30;
    /// <summary>Десять секунд м'яч не торкався жодної ракетки — перекидаємо подачу, без штрафу.</summary>
    public const int IdleTicks = 250;
    public const int DefaultLives = 5;
    /// <summary>Куди може заїхати центр ракетки: між кутовими квадратами.</summary>
    public const double MinP = Corner + PaddleL / 2, MaxP = S - Corner - PaddleL / 2;

    readonly int[] _dir = new int[Seats];
    readonly double?[] _aim = new double?[Seats];
    readonly int[] _moved = new int[Seats];
    /// <summary>У чий бік подавати: сторона, що щойно пропустила; −1 — будь-кому з живих.</summary>
    int _next = -1;
    int _idle;

    /// <summary>Центр ракетки вздовж своєї стіни: y для лівої й правої, x для верхньої й нижньої.</summary>
    public double[] P { get; } = [S / 2, S / 2, S / 2, S / 2];
    /// <summary>Життя кожного місця; 0 — вибув.</summary>
    public int[] L { get; } = new int[Seats];
    /// <summary>Хто взагалі грає цю партію (сидів на старті). Порожні місця — глухі стіни від першої секунди.</summary>
    public bool[] Plays { get; } = new bool[Seats];
    /// <summary>Скільки життів кожен вибив у суперників: чий був останній дотик перед чужим голом.</summary>
    public int[] Goals { get; } = new int[Seats];
    /// <summary>Порядок вибування: перший вибулий — перший у списку.</summary>
    public List<int> Out { get; } = [];

    public double Bx { get; set; } = S / 2;
    public double By { get; set; } = S / 2;
    public double Vx { get; set; }
    public double Vy { get; set; }
    public int StartIn { get; set; } = StartTicks;
    public int ServeIn { get; set; }
    public int T { get; set; }
    public int Rally { get; private set; }
    public int? HitBy { get; private set; }
    /// <summary>Хто торкався м'яча останнім — йому й зараховується чужий гол.</summary>
    public int? Touch { get; set; }
    /// <summary>Остання сторона, що пропустила, і хто їй забив (null — сам м'яч, без чийогось дотику).</summary>
    public int? LastLost { get; private set; }
    public int? LastBy { get; private set; }
    /// <summary>Номер подачі: росте щоразу, коли м'яч повертається в центр. Клієнт по ньому не тягне м'яч через поле.</summary>
    public int Serve { get; private set; }

    public double Speed => Math.Sqrt(Vx * Vx + Vy * Vy);
    public bool Alive(int seat) => seat is >= 0 and < Seats && Plays[seat] && L[seat] > 0;
    public int AliveCount => Enumerable.Range(0, Seats).Count(Alive);

    /// <summary>Нова партія: ті, хто сидить, отримують по <paramref name="lives"/> життів, решта стін глухі.</summary>
    public void Reset(bool[] plays, int lives = DefaultLives)
    {
        for (var s = 0; s < Seats; s++)
        {
            Plays[s] = s < plays.Length && plays[s];
            L[s] = Plays[s] ? lives : 0;
            P[s] = S / 2;
            _dir[s] = 0;
            _aim[s] = null;
            _moved[s] = 0;
            Goals[s] = 0;
        }
        Out.Clear();
        _next = -1;
        _idle = 0;
        T = 0;
        Rally = 0;
        HitBy = Touch = LastLost = LastBy = null;
        Serve = 0;
        StartIn = StartTicks;
        ServeIn = 0;
        Bx = By = S / 2;
        Vx = Vy = 0;
    }

    /// <summary>Гравець устав з-за столу: його стіна глухне, життя згорають.</summary>
    public void Knock(int seat)
    {
        if (!Alive(seat)) return;
        L[seat] = 0;
        Out.Add(seat);
        if (Touch == seat) Touch = null;
    }

    public void Move(int seat, int dir)
    {
        if (seat is < 0 or >= Seats) return;
        _dir[seat] = Math.Sign(dir);
        _aim[seat] = null;
    }

    public void Aim(int seat, double at)
    {
        if (seat is < 0 or >= Seats || !double.IsFinite(at)) return;
        _aim[seat] = Math.Clamp(at, MinP, MaxP);
        _dir[seat] = 0;
    }

    /// <summary>Один крок. Повертає сторону, що щойно пропустила (і втратила життя), або null.</summary>
    public int? Step()
    {
        T++;
        HitBy = null;
        MovePaddles();
        if (StartIn > 0)
        {
            StartIn--;
            if (StartIn == 0) Launch();
            return null;
        }
        if (ServeIn > 0)
        {
            ServeIn--;
            if (ServeIn == 0) Launch();
            return null;
        }
        if (++_idle > IdleTicks)
        {
            // М'яч забрів у глухий закуток і десять секунд ніхто його не торкався — подаємо наново.
            _next = -1;
            Center();
            return null;
        }

        // Два півкроки: у квадраті є кути й до чотирьох ракеток, і на повній швидкості м'яч за цілий тик
        // пролітає 5.6 одиниці — забагато, щоб чесно розібратись, об що саме він ударився першим.
        for (var half = 0; half < 2; half++)
        {
            var (x0, y0) = (Bx, By);
            Bx += Vx * Dt / 2;
            By += Vy * Dt / 2;
            for (var s = 0; s < Seats; s++)
                if (Alive(s)) Hit(s, x0, y0);
            Walls();
            Corners();
            if (Gone() is { } side) return Lose(side);
        }
        return null;
    }

    void MovePaddles()
    {
        var step = PaddleSpeed * Dt;
        for (var s = 0; s < Seats; s++)
        {
            if (!Alive(s)) { _moved[s] = 0; continue; }
            var was = P[s];
            if (_aim[s] is { } want) P[s] += Math.Clamp(want - P[s], -step, step);
            else P[s] += _dir[s] * step;
            P[s] = Math.Clamp(P[s], MinP, MaxP);
            var d = P[s] - was;
            _moved[s] = Math.Abs(d) >= step / 2 ? Math.Sign(d) : 0;
        }
    }

    /// <summary>Напрямок «усередину поля» від стіни: ліва й верхня — плюс, права й нижня — мінус.</summary>
    public static int Inward(int side) => side is 0 or 2 ? 1 : -1;

    /// <summary>Площина, від якої відбивається центр м'яча: край ракетки плюс радіус.</summary>
    public static double Plane(int side)
    {
        var near = PaddleOff + PaddleW / 2 + BallR;
        return Inward(side) > 0 ? near : S - near;
    }

    /// <summary>Відбій від ракетки — по відрізку руху, як і на двох (див. PongCore.Hit).</summary>
    void Hit(int side, double x0, double y0)
    {
        var vertical = side < 2;                      // ліва й права стоять вертикально, ходять по y
        var inward = Inward(side);
        var vn = vertical ? Vx : Vy;
        if (inward > 0 ? vn >= 0 : vn <= 0) return;   // летить не до цієї стіни
        var plane = Plane(side);
        var n0 = vertical ? x0 : y0;
        var n1 = vertical ? Bx : By;
        var crossed = inward > 0 ? n0 >= plane && n1 <= plane : n0 <= plane && n1 >= plane;
        if (!crossed) return;

        var dn = n1 - n0;
        var k = Math.Abs(dn) < 1e-9 ? 0 : (plane - n0) / dn;
        var t0 = vertical ? y0 : x0;
        var t1 = vertical ? By : Bx;
        var t = t0 + (t1 - t0) * k;
        var off = t - P[side];
        if (Math.Abs(off) > PaddleL / 2 + BallR) return;

        var rel = Math.Clamp(off / (PaddleL / 2), -1, 1);
        var deg = Math.Clamp(rel * BounceAngle + _moved[side] * SpinAngle, -BounceAngle, BounceAngle);
        var a = deg * Math.PI / 180;
        var speed = Math.Min(MaxSpeed, Speed * SpeedUp);
        var outN = inward * speed * Math.Cos(a);
        var outT = speed * Math.Sin(a);
        var n = plane + inward * Math.Abs(n1 - plane);
        if (vertical) { Vx = outN; Vy = outT; Bx = n; By = t; }
        else { Vy = outN; Vx = outT; By = n; Bx = t; }
        Touch = side;
        HitBy = side;
        Rally++;
        _idle = 0;
    }

    /// <summary>Стіни тих, хто не грає або вже вибув: дзеркало, як верх і низ на двох.</summary>
    void Walls()
    {
        if (!Alive(0) && Bx < BallR && Vx < 0) { Bx = 2 * BallR - Bx; Vx = -Vx; Slide(true); }
        if (!Alive(1) && Bx > S - BallR && Vx > 0) { Bx = 2 * (S - BallR) - Bx; Vx = -Vx; Slide(true); }
        if (!Alive(2) && By < BallR && Vy < 0) { By = 2 * BallR - By; Vy = -Vy; Slide(false); }
        if (!Alive(3) && By > S - BallR && Vy > 0) { By = 2 * (S - BallR) - By; Vy = -Vy; Slide(false); }
    }

    /// <summary>
    /// Глухі квадрати в кутах. М'яч, що заліз у квадрат (з урахуванням радіуса), виштовхуємо по тій осі,
    /// по якій він заліз менше, і відбиваємо — так він відскакує від тієї грані, в яку справді влучив.
    /// </summary>
    void Corners()
    {
        var edge = Corner + BallR;
        for (var c = 0; c < 4; c++)
        {
            var lowX = c is 0 or 2;
            var lowY = c is 0 or 1;
            var penX = lowX ? edge - Bx : Bx - (S - edge);
            var penY = lowY ? edge - By : By - (S - edge);
            if (penX <= 0 || penY <= 0) continue;
            if (penX < penY)
            {
                Bx = lowX ? edge + penX : S - edge - penX;
                Vx = lowX ? Math.Abs(Vx) : -Math.Abs(Vx);
                Slide(true);
            }
            else
            {
                By = lowY ? edge + penY : S - edge - penY;
                Vy = lowY ? Math.Abs(Vy) : -Math.Abs(Vy);
                Slide(false);
            }
        }
    }

    /// <summary>
    /// Після відбою від глухого: не дати м'ячеві піти майже перпендикулярно (див. <see cref="MinSlide"/>).
    /// <paramref name="normalIsX"/> — стіна стоїть вертикально, тож «вздовж» — це y.
    /// </summary>
    void Slide(bool normalIsX)
    {
        var sp = Speed;
        if (sp < 1e-9) return;
        var along = normalIsX ? Vy : Vx;
        var across = normalIsX ? Vx : Vy;
        if (Math.Abs(along) >= MinSlide * sp) return;
        var sign = along < 0 ? -1 : 1;
        along = sign * MinSlide * sp;
        across = Math.Sign(across) * Math.Sqrt(sp * sp - along * along);
        if (normalIsX) { Vy = along; Vx = across; }
        else { Vx = along; Vy = across; }
    }

    /// <summary>М'яч вилетів за край — чия це сторона.</summary>
    int? Gone() =>
        Bx < 0 ? 0 : Bx > S ? 1 : By < 0 ? 2 : By > S ? 3 : null;

    int? Lose(int side)
    {
        if (!Alive(side))
        {
            // Сюди м'яч дійти не мав би (глухі стіни відбивають раніше) — але якщо таки дійшов, не
            // карати того, хто вже вибув: просто подаємо наново.
            _next = -1;
            Center();
            return null;
        }
        L[side]--;
        LastLost = side;
        LastBy = Touch is { } by && by != side ? by : null;
        if (LastBy is { } scorer) Goals[scorer]++;
        if (L[side] == 0) Out.Add(side);
        _next = L[side] > 0 ? side : -1;
        Center();
        return side;
    }

    void Center()
    {
        Bx = By = S / 2;
        Vx = Vy = 0;
        ServeIn = ServeTicks;
        Rally = 0;
        Touch = null;
        _idle = 0;
        Serve++;
    }

    /// <summary>Подача з центру в бік <c>_next</c> (або будь-кого з живих) під випадковим кутом ±25°.</summary>
    void Launch()
    {
        var alive = Enumerable.Range(0, Seats).Where(Alive).ToArray();
        if (alive.Length == 0) return;
        var side = Alive(_next) ? _next : alive[rng.Next(alive.Length)];
        var a = (rng.NextDouble() * 2 - 1) * ServeAngle * Math.PI / 180;
        var toward = -Inward(side);                   // до лівої — мінус по x, до нижньої — плюс по y
        var n = toward * StartSpeed * Math.Cos(a);
        var t = StartSpeed * Math.Sin(a);
        Bx = By = S / 2;
        if (side < 2) { Vx = n; Vy = t; }
        else { Vy = n; Vx = t; }
        _idle = 0;
    }
}

/// <summary>
/// Понг. На двох — класика: ліва й права ракетки, до семи. На трьох-чотирьох — арена
/// (<see cref="PongArena"/>): кожен стереже свою стіну, у кожного життя, останній на полі виграв.
/// Правила й стан живуть тільки тут: браузер шле наміри («тримаю вгору», «палець отут»), а назад
/// отримує кадр із дробовими координатами, який домальовує інтерполятором.
/// </summary>
public sealed class Pong : Game
{
    static readonly GameOption Length = new("len", "Партія",
        [("short", "Коротка"), ("normal", "Звичайна"), ("long", "Довга")], "normal");

    public override GameInfo Info { get; } = new(
        "pong", "Понг", "понг", GameGroup.Live, 2, PongArena.Seats, TickMs: PongCore.TickMs,
        Start: StartMode.ByHost, Options: [Length],
        Hint: "На двох — класика до семи, на трьох-чотирьох — арена: кожен стереже свою стіну. Стрілки або тягни пальцем");

    PongCore? _core;
    PongArena? _arena;
    /// <summary>Партія йде на арені (на старті сиділо троє-четверо).</summary>
    bool _isArena;
    bool _started;
    /// <summary>Хто в класиці ліворуч і хто праворуч: місця, що сиділи на старті, по порядку.</summary>
    int[] _duo = [0, 1];
    string _len = "normal";
    int? _winner;
    bool _over;

    /// <summary>До скількох грають на двох.</summary>
    public int Target => _len switch { "short" => 5, "long" => 11, _ => PongCore.Target };
    /// <summary>Скільки життів на арені.</summary>
    public int Lives => _len switch { "short" => 3, "long" => 7, _ => PongArena.DefaultLives };

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

    bool[] Seated() => [.. Enumerable.Range(0, PongArena.Seats).Select(Ctx.Seated)];

    /// <summary>
    /// Арена чи класика. До старту — за тим, скільки зараз сидить (стіл, що чекає, показує саме те поле,
    /// на якому гратимуть); після старту — за тим, скільки сиділо на старті.
    /// </summary>
    bool IsArena => Lobby ? Seated().Count(x => x) > 2 : _isArena;

    /// <summary>
    /// Стіл чекає на старт: або партії ще не було, або дограний стіл хтось відкрив наново, сівши на вільне
    /// місце (за столом з'явився хтось, кого не було на старті). Тоді показуємо не старий підсумок, а свіже
    /// поле для нового складу. Той, хто лише встав, стола не відкриває — підсумок лишається.
    /// </summary>
    bool Lobby => !_started || (_over && Enumerable.Range(0, PongArena.Seats)
        .Any(s => Ctx.Seated(s) && !_startNicks.Contains(Ctx.NickOf(s) ?? "", StringComparer.OrdinalIgnoreCase)));
    string[] _startNicks = [];

    /// <summary>Арена для малювання: справжня після старту, а в лобі — свіжий макет з тих, хто вже сів.</summary>
    PongArena Arena
    {
        get
        {
            if (!Lobby && _arena is not null) return _arena;
            // Макет генератора не чіпає (Reset випадковості не питає): сідована партія лишається тією самою.
            var preview = new PongArena(Ctx.Rng);
            preview.Reset(Seated(), Lives);
            return preview;
        }
    }

    public override string SeatName(int seat)
    {
        if (!IsArena)
        {
            var duo = Lobby ? Duo() : _duo;
            if (seat == duo[0]) return "ліва";
            if (seat == duo[1]) return "права";
        }
        // На арені кожен бачить свою стіну внизу, тож «ліва» чи «верхня» кожному значила б своє.
        // Кажемо кольором ракетки — він однаковий для всіх (і для чіпів місць у картці).
        return seat switch { 0 => "жовта", 1 => "зелена", 2 => "руда", _ => "синя" };
    }

    /// <summary>Двоє, що сидять, по порядку місць; поки сидить менше — добиваємо звичними 0 і 1.</summary>
    int[] Duo()
    {
        var s = Enumerable.Range(0, PongArena.Seats).Where(Ctx.Seated).Take(2).ToList();
        for (var d = 0; s.Count < 2; d++)
            if (!s.Contains(d)) s.Add(d);
        s.Sort();
        return [.. s];
    }

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        var len = options.TryGetValue("len", out var l) ? l : Length.Default;
        if (!Length.Values.Any(v => v.Value == len)) throw new GameError("Такої довжини партії нема");
        _len = len;
    }

    public override void Start()
    {
        _winner = null;
        _over = false;
        _started = true;
        var seated = Seated();
        _startNicks = [.. Enumerable.Range(0, PongArena.Seats).Where(Ctx.Seated).Select(x => Ctx.NickOf(x) ?? "")];
        _isArena = seated.Count(x => x) > 2;
        if (_isArena)
        {
            _arena = new PongArena(Ctx.Rng);
            _arena.Reset(seated, Lives);
        }
        else
        {
            _duo = Duo();
            Core.Reset();
        }
    }

    /// <summary>
    /// Хтось устав. На двох — техпоразка, як і була (вид і кадр теж мусять дізнатись, що партію закрито:
    /// інакше на полі застигла б картинка без підсумку). На арені решту партії не ламаємо: стіна того,
    /// хто пішов, глухне, а коли живим лишився один — арена його.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (!_isArena)
        {
            var other = seat == _duo[0] ? _duo[1] : _duo[0];
            if (Ctx.Seated(other) && other != seat) _winner = other;
            _over = true;
            base.OnLeave(seat);
            return;
        }
        var a = _arena!;
        a.Knock(seat);
        var alive = Enumerable.Range(0, PongArena.Seats).Where(s => s != seat && a.Alive(s) && Ctx.Seated(s)).ToArray();
        if (alive.Length > 1)
        {
            Ctx.Log($"{Info.Title}: {Ctx.NickOf(seat)} встав з-за столу — його стіна тепер глуха, решта грає далі");
            return;
        }
        _over = true;
        _winner = alive.Length == 1 ? alive[0] : null;
        Ctx.Finish(alive, $"{Info.Title}: {Ctx.NickOf(seat)} встав з-за столу, партію не дограли");
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        switch (action)
        {
            case "move":
                if (Num(payload, "dir") is { } dir)
                {
                    if (_isArena) _arena?.Move(seat, Math.Sign(dir));
                    else if (Index(seat) is { } i) Core.Move(i, Math.Sign(dir));
                }
                return ActResult.Done;
            case "to":
                // На арені «y» — це позиція вздовж своєї стіни (для верхньої й нижньої — по x). Ім'я
                // лишилось від класики: клієнт і так перераховує палець у координату своєї ракетки.
                if (Num(payload, "y") is { } y)
                {
                    if (_isArena) _arena?.Aim(seat, y);
                    else if (Index(seat) is { } i) Core.Aim(i, y);
                }
                return ActResult.Done;
            default:
                return ActResult.Fail("Тут так не ходять");
        }
    }

    /// <summary>Місце → ракетка в класиці (0 ліва, 1 права); null — це місце не грає.</summary>
    int? Index(int seat) => seat == _duo[0] ? 0 : seat == _duo[1] ? 1 : null;

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
        return _isArena ? TickArena() : TickDuo();
    }

    /// <summary>
    /// Класика. Кадр летить щотика, а повний вид — лише коли змінилось те, що в кадрі не видно:
    /// фаза «готуйсь» → «граємо», рахунок і кінець партії.
    /// </summary>
    TickResult TickDuo()
    {
        var wasReady = Core.StartIn > 0;
        var scorer = Core.Step();
        if (scorer is null) return wasReady && Core.StartIn == 0 ? TickResult.Both : TickResult.FrameOnly;
        if (Core.S[scorer.Value] < Target) return TickResult.Both;

        var won = _duo[scorer.Value];
        var lost = _duo[1 - scorer.Value];
        _winner = won;
        _over = true;
        // Ніки чужі, відмінювати їх нема як, тому рахунок замість речення з відмінками.
        Ctx.Finish([won],
            $"{Info.Title}: {Ctx.NickOf(won)} {SeatName(won)} {Core.S[scorer.Value]}:{Core.S[1 - scorer.Value]} {Ctx.NickOf(lost)} {SeatName(lost)}");
        return TickResult.Both;
    }

    TickResult TickArena()
    {
        var a = _arena!;
        var wasReady = a.StartIn > 0;
        var lost = a.Step();
        if (lost is null) return wasReady && a.StartIn == 0 ? TickResult.Both : TickResult.FrameOnly;
        if (a.AliveCount > 1) return TickResult.Both;

        var alive = Enumerable.Range(0, PongArena.Seats).Where(a.Alive).ToArray();
        _winner = alive.Length == 1 ? alive[0] : null;
        _over = true;
        // Підсумок: переможець із життями, що лишились, далі — хто вилетів пізніше, той вище.
        var rest = Enumerable.Reverse(a.Out).Where(s => s != _winner).Select(s => Ctx.NickOf(s));
        var head = _winner is { } w ? $"{Ctx.NickOf(w)} ({a.L[w]} ♥)" : "нікого не лишилось";
        Ctx.Finish(alive, $"{Info.Title}, арена: {head} · {string.Join(" · ", rest)}",
            Enumerable.Range(0, PongArena.Seats).Where(s => a.Plays[s]).ToDictionary(s => s, s => (long)a.Goals[s]));
        return TickResult.Both;
    }

    public override object? Frame() => IsArena ? ArenaShot() : Shot();

    public override object View(int? seat)
    {
        if (IsArena)
        {
            var a = Arena;
            return new
            {
                mode = "arena",
                lives = LivesOf(a),
                goals = (int[])a.Goals.Clone(),
                phase = _over && !Lobby ? "done" : a.StartIn > 0 ? "ready" : "play",
                startIn = a.StartIn,
                winner = Lobby ? null : _winner,
                target = Lives,
                turn = (int?)null,
                frame = ArenaShot(),
            };
        }
        var d = DuoField;
        var lobby = Lobby;
        return new
        {
            mode = "duo",
            scores = new[] { d.S[0], d.S[1] },
            phase = _over && !lobby ? "done" : d.StartIn > 0 ? "ready" : "play",
            startIn = d.StartIn,
            winner = lobby ? null : _winner,
            target = Target,
            turn = (int?)null,   // ходів тут нема, але каркас питає це поле в кожної гри
            frame = Shot(),      // щоб картка намалювала поле ще до першого кадру
        };
    }

    /// <summary>Життя на дроті: null для порожнього місця (там глуха стіна, а не «0 життів»).</summary>
    static int?[] LivesOf(PongArena a) => [.. Enumerable.Range(0, PongArena.Seats).Select(s => a.Plays[s] ? a.L[s] : (int?)null)];

    /// <summary>
    /// Кадр класики. Округлення до 0.1 — не про точність, а про розмір: 25 таких повідомлень на секунду на
    /// кожного глядача. <c>seats</c> — які місця ліва й права; <c>hit</c> — хто відбив у цьому тику
    /// (клієнт спалахує ракеткою і клацає), <c>rally</c> — удари поспіль без гола.
    /// </summary>
    object Shot()
    {
        var d = DuoField;
        var lobby = Lobby;
        return new
        {
            t = d.T,
            bx = R(d.Bx),
            by = R(d.By),
            vx = R(d.Vx),
            vy = R(d.Vy),
            p = new[] { R(d.P[0]), R(d.P[1]) },
            s = new[] { d.S[0], d.S[1] },
            serveIn = d.ServeIn,
            // Відлік «готуйсь» іде в кадрі, а не лише у виді: види летять раз на фазу, а цифру на полі
            // треба міняти щосекунди — і статус картки каркас теж бере з кадру (INTEGRATION-NOTES §3).
            startIn = d.StartIn,
            winner = lobby ? null : _winner,
            seats = lobby ? Duo() : (int[])_duo.Clone(),
            hit = d.HitBy,
            rally = d.Rally,
        };
    }

    /// <summary>Класичне поле для малювання: справжнє в партії, а в лобі — свіже, без старого рахунку.</summary>
    PongCore DuoField
    {
        get
        {
            if (!_started || !Lobby) return Core;
            var fresh = new PongCore(Ctx.Rng);
            fresh.Reset();
            return fresh;
        }
    }

    /// <summary>
    /// Кадр арени: <c>p</c> — ракетки вздовж своїх стін (null — стіна без гравця), <c>l</c> — життя,
    /// <c>lost</c>/<c>from</c> — хто пропустив останнім і хто йому забив, <c>n</c> — номер подачі.
    /// </summary>
    object ArenaShot()
    {
        var a = Arena;
        return new
        {
            mode = "arena",
            t = a.T,
            bx = R(a.Bx),
            by = R(a.By),
            vx = R(a.Vx),
            vy = R(a.Vy),
            p = Enumerable.Range(0, PongArena.Seats).Select(s => a.Plays[s] ? R(a.P[s]) : (double?)null).ToArray(),
            l = LivesOf(a),
            serveIn = a.ServeIn,
            startIn = a.StartIn,
            winner = Lobby ? null : _winner,
            hit = a.HitBy,
            rally = a.Rally,
            lost = a.LastLost,
            from = a.LastBy,
            n = a.Serve,
        };
    }

    static double R(double v) => Math.Round(v, 1);
}
