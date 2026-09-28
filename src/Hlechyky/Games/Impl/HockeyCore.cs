namespace Hlechyky.Games.Impl;

/// <summary>
/// Стіл аерохокею без кімнат і рахунку партії: шайба, до чотирьох біт, борти, ворота, подача й застій.
/// Стіл 200 × 120 (x — уздовж, ворота на торцях), вісь y униз. Сині (команда 0) захищають ворота x = 0,
/// руді (1) — x = 200. П'ять підкроків по 8 мс: за підкрок шайба проходить ≤ 8, біта ≤ 3.36, а вікно
/// контакту біта–шайба 12.5 — повз біту не проскочить. Прохід №3 (29.09): десять підкроків по 4 мс — біта за
/// пальцем тепер швидка (900/с), а разом із шайбою за підкрок проходять ≤ 7.6 &lt; 12.5. Усі числа — зі spec <c>docs/games/specs/hockey.md</c> §2.
/// </summary>
public sealed class HockeyCore(Random rng)
{
    public const int Seats = 4;
    public const int TickMs = 40;
    public const int Sub = 10;
    public const double H = 0.004;

    public const double W = 200, TableH = 120, Mid = W / 2;
    public const double GoalLo = 40, GoalHi = 80;
    public const double PuckR = 4.5, PadR = 8;
    /// <summary>
    /// Біта за пальцем чи мишкою — 900/с: рука рідко рухається швидше, тож біта не відстає від курсора (на 420 вона
    /// відставала на пів стола при кожному замаху — «незручно»). Клавіші й пад лишились на 420: вісім напрямків —
    /// грубий інструмент, на 900 ним не прицілишся.
    /// </summary>
    public const double PadSpeed = 900, KeySpeed = 420;
    /// <summary>Скільки біта проходить за підкрок (3.6 за мишкою, 1.68 клавішами) — ті самі числа рахує браузер.</summary>
    public const double PadStep = PadSpeed * H, KeyStep = KeySpeed * H;
    /// <summary>
    /// Ціль біти їде далі швидкістю руки, що прийшла з наміром, ще до 32 мс (8 підкроків): намір летить 25 разів на
    /// секунду, і без цього біта ривками доганяла кожну нову ціль — удар то в повну силу, то нульовий.
    /// </summary>
    public const int AimLead = 8;
    public const double Mu = 0.6, VMax = 1000, EWall = 0.92, EPad = 0.90;

    public const int StartTicks = 75, ServeTicks = 30;
    /// <summary>Пауза після гола: «ГОЛ!» і дві секунди повільного повтору в браузері (п. 181), потім подача.</summary>
    public const int GoalServeTicks = 85;
    /// <summary>Шайба повільніша за 15 три секунди поспіль — сервер штовхає її до центру.</summary>
    public const int IdleTicks = 75;
    /// <summary>
    /// «Притримав»: шайбу три секунди затискають біля борта, у кутку чи між бітами — подача суперникам (у справжньому
    /// аерохокеї — сім секунд). Без цього дві біти, що тиснуть шайбу в кут, тримали б її там вічно.
    /// </summary>
    public const int HoldTicks = 75;
    public const double IdleSpeed = 15, NudgeSpeed = 150, NudgeAngle = 20;
    /// <summary>
    /// Стартовий розіграш — лагідний: 110 під кутом 15…35° у бік випадкової команди. Так шайба щоразу б'ється об
    /// борт чи торець повз ворота й сповзає в куток — перший гол мусить хтось забити, а не сам стартовий удар
    /// (на 220 і ±35° шайба часом залітала у ворота за пару секунд, поки ніхто й не торкнувся).
    /// </summary>
    public const double KickSpeed = 110, KickAngleMin = 15, KickAngle = 35;

    public ArenaBody Puck = new(Mid, TableH / 2, PuckR, 1);
    public ArenaBody[] Pads { get; } = new ArenaBody[Seats];
    public bool[] Plays { get; } = new bool[Seats];
    /// <summary>Команда місця: 0 — сині (ворота x = 0), 1 — руді.</summary>
    public int[] Team { get; } = new int[Seats];

    readonly bool[] _aim = new bool[Seats];
    readonly double[] _tx = new double[Seats], _ty = new double[Seats];
    /// <summary>Швидкість руки з останнього наміру й скільки підкроків тому він прийшов.</summary>
    readonly double[] _tvx = new double[Seats], _tvy = new double[Seats];
    readonly int[] _age = new int[Seats];
    readonly int[] _dx = new int[Seats], _dy = new int[Seats];
    readonly double[] _x0 = new double[Seats], _y0 = new double[Seats];

    /// <summary>Рахунок команд.</summary>
    public int[] S { get; } = new int[2];
    /// <summary>Особисті голи й автоголи за місцями.</summary>
    public int[] Goals { get; } = new int[Seats];
    public int[] Own { get; } = new int[Seats];

    public int T { get; set; }
    public int StartIn { get; set; } = StartTicks;
    public int ServeIn { get; set; }
    /// <summary>Скільки тиків шайба вже повільніша за <see cref="IdleSpeed"/>.</summary>
    public int Idle { get; set; }
    /// <summary>Номер розіграшу: росте на кожен гол — клієнт по ньому не тягне шайбу через стіл.</summary>
    public int N { get; private set; }
    /// <summary>Удари без гола.</summary>
    public int Rally { get; private set; }
    /// <summary>Хто вдарив шайбу в цьому тику.</summary>
    public int? HitBy { get; private set; }
    /// <summary>
    /// Хто торкався шайби останнім. Сетер ще й запам'ятовує дотик за командою: гол записуємо останньому, хто
    /// грав шайбою за команду, що забила, навіть якщо захисник її потім зачепив (див. <see cref="Goal"/>).
    /// </summary>
    public int? LastTouch
    {
        get => _lastTouch;
        set
        {
            _lastTouch = value;
            if (value is { } s && s is >= 0 and < Seats) _teamTouch[Team[s]] = s;
        }
    }
    int? _lastTouch;
    /// <summary>Останній дотик кожної команди в цьому розіграші (−1 — ще не торкались).</summary>
    readonly int[] _teamTouch = [-1, -1];
    /// <summary>Команда, що забила в цьому тику.</summary>
    public int? GoalBy { get; private set; }
    /// <summary>Останній гол — автогол (забивна команда шайби не торкалась).</summary>
    public bool GoalOwn { get; private set; }
    /// <summary>Останній гол — «з-під борту»: після останнього удару біти шайба ще відбилась від борта чи торця.</summary>
    public bool GoalRail { get; private set; }
    /// <summary>Шайба відбилась від борта після останнього дотику біти.</summary>
    bool _rail;
    /// <summary>У цьому тику сервер штовхнув застиглу шайбу.</summary>
    public bool Nudged { get; private set; }
    /// <summary>У цьому тику нічого не зрушило: ні біти, ні шайба (тоді кадр можна не слати).</summary>
    public bool Still { get; private set; }
    /// <summary>Скільки тиків шайбу затискають (росте щотика затиску, на волі швидко спадає).</summary>
    public int Held { get; set; }
    /// <summary>У цьому тику шайбу забрали за притримування — подача цій команді.</summary>
    public int? FoulTo { get; private set; }
    /// <summary>У цьому тику шайба була затиснута хоч раз.</summary>
    bool _pinched;
    /// <summary>Біта торкалась шайби на минулому підкроці: довгий дотик (притиснув, веде) — один удар, а не п'ять на тик.</summary>
    readonly bool[] _contact = new bool[Seats], _contactNow = new bool[Seats];

    public static double MinX(int team) => team == 0 ? PadR : Mid + PadR;
    public static double MaxX(int team) => team == 0 ? Mid - PadR : W - PadR;

    /// <summary>
    /// Команда місця для складу <paramref name="plays"/>: на чотирьох — за парністю (0, 2 сині; 1, 3 руді), інакше —
    /// за порядком серед тих, хто грає (менше місце — сині). Так і пара на 0 і 2 грає одне проти одного.
    /// </summary>
    public static int TeamOf(int seat, bool[] plays)
    {
        var n = 0;
        for (var i = 0; i < plays.Length && i < Seats; i++) if (plays[i]) n++;
        if (n != 2) return seat % 2;
        for (var i = 0; i < seat && i < plays.Length; i++) if (plays[i]) return 1;
        return 0;
    }

    /// <summary>Нова партія: команди, біти на своїх місцях, шайба в центрі, три секунди «готуйсь».</summary>
    public void Reset(bool[] plays)
    {
        for (var i = 0; i < Seats; i++)
        {
            Plays[i] = i < plays.Length && plays[i];
            Team[i] = TeamOf(i, Plays);
            Goals[i] = Own[i] = 0;
            _aim[i] = false;
            _dx[i] = _dy[i] = 0;
            _tvx[i] = _tvy[i] = 0;
        }
        S[0] = S[1] = 0;
        T = 0;
        StartIn = StartTicks;
        ServeIn = 0;
        Idle = 0;
        N = 0;
        Rally = 0;
        HitBy = LastTouch = GoalBy = null;
        GoalOwn = GoalRail = _rail = false;
        _teamTouch[0] = _teamTouch[1] = -1;
        Nudged = false;
        Still = false;
        Held = 0;
        FoulTo = null;
        Array.Clear(_contact);
        Puck = new ArenaBody(Mid, TableH / 2, PuckR, 1);
        _seenN = 0;
        _botX = double.NaN;
        Place();
    }

    /// <summary>Біти на старт: сам — посеред своєї половини, удвох — перед воротами одна над одною.</summary>
    void Place()
    {
        for (var team = 0; team < 2; team++)
        {
            var k = 0;
            var members = 0;
            for (var i = 0; i < Seats; i++) if (Plays[i] && Team[i] == team) members++;
            for (var i = 0; i < Seats; i++)
            {
                if (!Plays[i] || Team[i] != team) continue;
                double x, y;
                if (members == 1) (x, y) = (25, TableH / 2);
                else (x, y) = (30, k == 0 ? 40 : 80);
                if (team == 1) (x, y) = (W - x, TableH - y);   // дзеркально через центр: руді бачать те саме
                Pads[i] = new ArenaBody(x, y, PadR, 0);
                _tx[i] = x;
                _ty[i] = y;
                k++;
            }
        }
    }

    /// <summary>Гравець устав: біта зникає, шайба її більше не бачить.</summary>
    public void Drop(int seat)
    {
        Plays[seat] = false;
        if (LastTouch == seat) LastTouch = null;
        for (var t = 0; t < 2; t++) if (_teamTouch[t] == seat) _teamTouch[t] = -1;
    }

    // ---------- ввід ----------

    /// <summary>Палець/миша: бажаний центр біти (світові координати). Ціль одразу підтягується у свою половину.</summary>
    public void Aim(int seat, double x, double y, double vx = 0, double vy = 0)
    {
        if (seat is < 0 or >= Seats || !double.IsFinite(x) || !double.IsFinite(y)) return;
        ClampAim(Team[seat], ref x, ref y);
        _tx[seat] = x;
        _ty[seat] = y;
        _aim[seat] = true;
        if (!double.IsFinite(vx) || !double.IsFinite(vy)) vx = vy = 0;
        var v = Math.Sqrt(vx * vx + vy * vy);
        var k = v > PadSpeed ? PadSpeed / v : 1;
        _tvx[seat] = vx * k;
        _tvy[seat] = vy * k;
        _age[seat] = 0;
    }

    /// <summary>Ціль біти зараз (для бота й тестів).</summary>
    public (double X, double Y) AimOf(int seat) => (_tx[seat], _ty[seat]);

    /// <summary>Ціль біти — у свою половину (те саме робить браузер перед передбаченням).</summary>
    public static void ClampAim(int team, ref double x, ref double y)
    {
        x = Math.Clamp(x, MinX(team), MaxX(team));
        y = Math.Clamp(y, PadR, TableH - PadR);
    }

    /// <summary>Клавіші/пад: напрямок уздовж світових осей (−1, 0, 1), діє до наступного вводу.</summary>
    public void Move(int seat, int dx, int dy)
    {
        if (seat is < 0 or >= Seats) return;
        _dx[seat] = Math.Sign(dx);
        _dy[seat] = Math.Sign(dy);
        _aim[seat] = false;
    }

    /// <summary>
    /// Крок біти на один підкрок: до цілі не далі ніж <see cref="PadStep"/> або за напрямком клавіш, потім — у свою
    /// половину. Рівно ця функція (і в тому самому порядку операцій) живе в браузері — передбачення своєї біти.
    /// </summary>
    public static void StepPad(ref double x, ref double y, int team, bool aim, double tx, double ty, int dx, int dy)
    {
        if (aim)
        {
            var ex = tx - x;
            var ey = ty - y;
            var d2 = ex * ex + ey * ey;
            if (d2 <= PadStep * PadStep)
            {
                x = tx;
                y = ty;
            }
            else
            {
                var d = Math.Sqrt(d2);
                x += ex / d * PadStep;
                y += ey / d * PadStep;
            }
        }
        else if (dx != 0 || dy != 0)
        {
            var k = dx != 0 && dy != 0 ? KeyStep * ArenaPhysics.D : KeyStep;
            x += dx * k;
            y += dy * k;
        }
        x = Math.Clamp(x, MinX(team), MaxX(team));
        y = Math.Clamp(y, PadR, TableH - PadR);
    }

    // ---------- крок ----------

    /// <summary>Один тик. Повертає команду, що забила (0/1), або −1.</summary>
    public int Step()
    {
        T++;
        HitBy = null;
        GoalBy = null;
        Nudged = false;
        FoulTo = null;
        _pinched = false;
        if (StartIn > 0)
        {
            StartIn--;
            if (StartIn == 0) Kickoff();
        }
        else if (ServeIn > 0)
        {
            ServeIn--;
        }
        var live = StartIn == 0 && ServeIn == 0;
        var moved = false;
        var scored = -1;
        for (var s = 0; s < Sub; s++)
        {
            moved |= MovePads();
            if (live && scored < 0) scored = PuckSub();
            else if (!live) Rest();
        }
        if (live && scored < 0)
        {
            Held = _pinched ? Held + 1 : Math.Max(0, Held - 3);
            if (Held >= HoldTicks) Foul();
            else Stall();
        }
        Still = !moved && scored < 0 && FoulTo is null && Puck.Vx == 0 && Puck.Vy == 0;
        return scored;
    }

    /// <summary>Біти на підкрок: крок до цілі, розведення напарників, швидкість біти для удару.</summary>
    bool MovePads()
    {
        var moved = false;
        for (var i = 0; i < Seats; i++)
        {
            if (!Plays[i]) continue;
            ref var p = ref Pads[i];
            _x0[i] = p.X;
            _y0[i] = p.Y;
            double tx = _tx[i], ty = _ty[i];
            if (_aim[i] && (_tvx[i] != 0 || _tvy[i] != 0))
            {
                var lead = Math.Min(_age[i], AimLead) * H;
                tx += _tvx[i] * lead;
                ty += _tvy[i] * lead;
                ClampAim(Team[i], ref tx, ref ty);
            }
            if (_age[i] < AimLead) _age[i]++;
            StepPad(ref p.X, ref p.Y, Team[i], _aim[i], tx, ty, _dx[i], _dy[i]);
        }
        // Напарники на одній половині не б'ються, а розходяться навпіл — швидкості не міняються.
        for (var i = 0; i < Seats; i++)
        {
            if (!Plays[i]) continue;
            for (var j = i + 1; j < Seats; j++)
            {
                if (!Plays[j] || Team[j] != Team[i]) continue;
                ref var a = ref Pads[i];
                ref var b = ref Pads[j];
                var dx = b.X - a.X;
                var dy = b.Y - a.Y;
                var d2 = dx * dx + dy * dy;
                if (d2 >= 4 * PadR * PadR) continue;
                var d = Math.Sqrt(d2);
                var (nx, ny) = d > 1e-9 ? (dx / d, dy / d) : (0.0, 1.0);
                var half = (2 * PadR - d) / 2;
                a.X -= nx * half;
                a.Y -= ny * half;
                b.X += nx * half;
                b.Y += ny * half;
                var team = Team[i];
                a.X = Math.Clamp(a.X, MinX(team), MaxX(team));
                a.Y = Math.Clamp(a.Y, PadR, TableH - PadR);
                b.X = Math.Clamp(b.X, MinX(team), MaxX(team));
                b.Y = Math.Clamp(b.Y, PadR, TableH - PadR);
            }
        }
        for (var i = 0; i < Seats; i++)
        {
            if (!Plays[i]) continue;
            ref var p = ref Pads[i];
            p.Vx = (p.X - _x0[i]) / H;
            p.Vy = (p.Y - _y0[i]) / H;
            if (p.X != _x0[i] || p.Y != _y0[i]) moved = true;
        }
        return moved;
    }

    /// <summary>Шайба на підкрок: рух із тертям → біти → борти → ворота. Повертає команду, що забила, або −1.</summary>
    int PuckSub()
    {
        ArenaPhysics.Integrate(ref Puck, H, Mu, VMax);
        for (var i = 0; i < Seats; i++)
        {
            if (!Plays[i]) continue;
            // Біта кінематична (InvM = 0): шайба забирає весь імпульс, біта не зрушує.
            var j = ArenaPhysics.Collide(ref Pads[i], ref Puck, EPad);
            if (j <= 0) continue;
            LastTouch = i;
            _rail = false;
            _contactNow[i] = true;
            // удар — новий дотик; біта, що тисне чи веде шайбу, б'є один раз, а не щопідкроку
            if (!_contact[i])
            {
                HitBy = i;
                Rally++;
            }
            // Застій дотиком не скидається — лише швидкістю: інакше шайбу можна «пасти» біля борта вічно.
        }
        ArenaPhysics.Cap(ref Puck, VMax);
        var bx = Puck.X;
        var by = Puck.Y;
        ArenaPhysics.ReflectY(ref Puck, PuckR, TableH - PuckR, EWall);
        // Торці — стіна скрізь, крім прорізу воріт: центр шайби в прорізі торця не бачить.
        if (Puck.Y < GoalLo || Puck.Y > GoalHi) ArenaPhysics.ReflectX(ref Puck, PuckR, W - PuckR, EWall);
        if (Puck.X != bx || Puck.Y != by) _rail = true;
        Pinch(bx, by);
        for (var i = 0; i < Seats; i++)
        {
            _contact[i] = _contactNow[i];
            _contactNow[i] = false;
        }
        if (Puck.X <= 0) return Goal(1);
        if (Puck.X >= W) return Goal(0);
        return -1;
    }

    /// <summary>
    /// Шайба чекає подачі чи свистка й стоїть: біти ходять, але на неї не наїжджають — упираються на дотик. Інакше
    /// біта, що стояла на місці подачі, накривала шайбу всю паузу, а зі свистком виштовхувала її куди прийдеться.
    /// </summary>
    void Rest()
    {
        const double rr = PadR + PuckR;
        for (var i = 0; i < Seats; i++)
        {
            if (!Plays[i]) continue;
            ref var p = ref Pads[i];
            var dx = p.X - Puck.X;
            var dy = p.Y - Puck.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 >= rr * rr - 1e-9) continue;
            var d = Math.Sqrt(d2);
            // біта рівно в центрі шайби — відступає до своїх воріт
            var (nx, ny) = d > 1e-9 ? (dx / d, dy / d) : (Team[i] == 0 ? -1.0 : 1.0, 0.0);
            Yield(ref p, Team[i], nx, ny, rr);
            p.Vx = (p.X - _x0[i]) / H;
            p.Vy = (p.Y - _y0[i]) / H;
        }
    }

    /// <summary>
    /// Шайба затиснута: біта впхнула її в борт (чи на біту суперника), і відбій повернув шайбу знову в біту — місця
    /// між ними нема. Без цього наступний підкрок бачив би нормаль уже з іншого боку центра біти й вистрілював шайбу
    /// навиворіт крізь біту. Тож: шайба лишається біля борта (без дзеркала), швидкість у бік біти гасне (вздовж борта
    /// лишається — затиснута шайба вислизає вбік, як справжня), а біта впирається й відступає на дотик: вона
    /// кінематична, але крізь затиснуту шайбу не проходить. Кілька проходів — на кут і на дві біти одразу.
    /// <paramref name="bx"/>, <paramref name="by"/> — де була шайба до відбою від бортів.
    /// </summary>
    void Pinch(double bx, double by)
    {
        const double rr = PadR + PuckR;
        for (var pass = 0; pass < 4; pass++)
        {
            var any = false;
            for (var i = 0; i < Seats; i++)
            {
                if (!Plays[i]) continue;
                ref var p = ref Pads[i];
                var dx = p.X - Puck.X;
                var dy = p.Y - Puck.Y;
                var d2 = dx * dx + dy * dy;
                if (d2 >= rr * rr - 1e-9) continue;
                any = true;
                _pinched = true;
                _contactNow[i] = true;
                if (pass == 0)
                {
                    // замість дзеркала — упритул до борта (не було борта — позиція та сама: між двома бітами)
                    by = Math.Clamp(by, PuckR, TableH - PuckR);
                    if (by < GoalLo || by > GoalHi) bx = Math.Clamp(bx, PuckR, W - PuckR);
                    Puck.X = bx;
                    Puck.Y = by;
                    dx = p.X - Puck.X;
                    dy = p.Y - Puck.Y;
                    d2 = dx * dx + dy * dy;
                    if (d2 >= rr * rr - 1e-9) continue;
                }
                var d = Math.Sqrt(d2);
                var (nx, ny) = d > 1e-9 ? (dx / d, dy / d) : (0.0, Puck.Y < TableH / 2 ? 1.0 : -1.0);
                // швидкість у бік біти гасне: туди шайбі нема куди
                var vn = Puck.Vx * nx + Puck.Vy * ny;
                if (vn > 0)
                {
                    Puck.Vx -= vn * nx;
                    Puck.Vy -= vn * ny;
                }
                // біта відступає рівно на дотик (у своїй половині) — і її швидкість для ударів теж чесна
                Yield(ref p, Team[i], nx, ny, rr);
                p.Vx = (p.X - _x0[i]) / H;
                p.Vy = (p.Y - _y0[i]) / H;
                // Біта в самому кутку своєї половини й відступити не може — тоді вже шайба відходить від неї
                // (упритул до борта); якщо наступить на іншу біту, розберемось на наступному проході.
                dx = p.X - Puck.X;
                dy = p.Y - Puck.Y;
                d2 = dx * dx + dy * dy;
                if (d2 >= rr * rr - 1e-9) continue;
                d = Math.Sqrt(d2);
                if (d > 1e-9) (nx, ny) = (dx / d, dy / d);
                Puck.X = p.X - nx * rr;
                Puck.Y = Math.Clamp(p.Y - ny * rr, PuckR, TableH - PuckR);
                if (Puck.Y < GoalLo || Puck.Y > GoalHi) Puck.X = Math.Clamp(Puck.X, PuckR, W - PuckR);
            }
            if (!any) return;
        }
    }

    /// <summary>
    /// Біта відступає від шайби вздовж нормалі на відстань <paramref name="rr"/>. Якщо одна вісь уперлась у межу
    /// своєї половини (біта біля борта чи центральної лінії), дотик шукаємо вздовж тієї межі — інакше біта в куті
    /// повзла б до дотику кілька підкроків, а шайба тим часом сиділа б у ній.
    /// </summary>
    void Yield(ref ArenaBody p, int team, double nx, double ny, double rr)
    {
        double lo = MinX(team), hi = MaxX(team);
        var tx = Puck.X + nx * rr;
        var ty = Puck.Y + ny * rr;
        var cx = Math.Clamp(tx, lo, hi);
        var cy = Math.Clamp(ty, PadR, TableH - PadR);
        if (cx != tx && cy == ty)
        {
            var e = cx - Puck.X;
            var rem = rr * rr - e * e;
            if (rem > 0) cy = Math.Clamp(Puck.Y + (ny < 0 ? -1 : 1) * Math.Sqrt(rem), PadR, TableH - PadR);
        }
        else if (cy != ty && cx == tx)
        {
            var e = cy - Puck.Y;
            var rem = rr * rr - e * e;
            if (rem > 0) cx = Math.Clamp(Puck.X + (nx < 0 ? -1 : 1) * Math.Sqrt(rem), lo, hi);
        }
        p.X = cx;
        p.Y = cy;
    }

    /// <summary>
    /// Гол команді <paramref name="team"/>, шайба — тим, хто пропустив. Особистий гол — останньому з команди, що
    /// забила, хто торкався шайби в цьому розіграші: удар, який захисник лише зачепив по дорозі у свої ворота, —
    /// гол нападника, а не автогол. Автогол — коли забивна команда шайби не торкалась зовсім, а останнім був
    /// хтось із тих, хто пропустив (сам заштовхав у свої).
    /// </summary>
    int Goal(int team)
    {
        S[team]++;
        var scorer = _teamTouch[team];
        GoalOwn = false;
        GoalRail = _rail;
        _rail = false;
        if (scorer >= 0 && Plays[scorer]) Goals[scorer]++;
        else if (LastTouch is { } lt && Plays[lt] && Team[lt] != team)
        {
            Own[lt]++;
            GoalOwn = true;
        }
        LastTouch = null;
        _teamTouch[0] = _teamTouch[1] = -1;
        Rally = 0;
        GoalBy = team;
        ServeIn = GoalServeTicks;
        N++;
        Idle = 0;
        Held = 0;
        Array.Clear(_contact);
        Puck = new ArenaBody(team == 1 ? Mid / 2 : Mid + Mid / 2, TableH / 2, PuckR, 1);
        return team;
    }

    /// <summary>
    /// Притримав: шайбу три секунди затискали на одній половині — подача команді з іншої (як після гола, але без
    /// очка). Особистих дотиків розіграшу більше нема, лічильник ударів з нуля.
    /// </summary>
    void Foul()
    {
        var to = Puck.X < Mid ? 1 : 0;
        FoulTo = to;
        Held = 0;
        Idle = 0;
        Rally = 0;
        LastTouch = null;
        _teamTouch[0] = _teamTouch[1] = -1;
        _rail = false;
        Array.Clear(_contact);
        ServeIn = ServeTicks;
        N++;
        Puck = new ArenaBody(to == 0 ? Mid / 2 : Mid + Mid / 2, TableH / 2, PuckR, 1);
    }

    /// <summary>Застій: три секунди шайба ледь повзе — поштовх 150 до центру стола з відхиленням ±20°.</summary>
    void Stall()
    {
        if (Puck.Speed >= IdleSpeed)
        {
            Idle = 0;
            return;
        }
        if (++Idle < IdleTicks) return;
        var dx = Mid - Puck.X;
        var dy = TableH / 2 - Puck.Y;
        var baseAngle = dx * dx + dy * dy < 1 ? (rng.Next(2) == 0 ? 0 : Math.PI) : Math.Atan2(dy, dx);
        var a = baseAngle + (rng.NextDouble() * 2 - 1) * NudgeAngle * Math.PI / 180;
        Puck.Vx = NudgeSpeed * Math.Cos(a);
        Puck.Vy = NudgeSpeed * Math.Sin(a);
        Idle = 0;
        Nudged = true;
    }

    /// <summary>Стартовий розіграш: 110 під кутом 15…35° (угору чи вниз) у бік випадкової команди.</summary>
    void Kickoff()
    {
        var team = rng.Next(2);
        var deg = KickAngleMin + rng.NextDouble() * (KickAngle - KickAngleMin);
        var a = (rng.Next(2) == 0 ? -deg : deg) * Math.PI / 180;
        var dir = team == 0 ? -1 : 1;
        Puck = new ArenaBody(Mid, TableH / 2, PuckR, 1) { Vx = dir * KickSpeed * Math.Cos(a), Vy = KickSpeed * Math.Sin(a) };
        Idle = 0;
    }
    // ---------- бот-напарник (прохід №3, п. 180) ----------

    /// <summary>Бот бачить шайбу з запізненням на три тики (120 мс) — як людина, а не як сервер.</summary>
    public const int BotDelay = 3;
    /// <summary>Як швидко бот переставляє свою ціль: 520/с у воротах, 760/с на замаху.</summary>
    public const double BotSpeed = 520, BotStrike = 760;
    readonly double[] _seenX = new double[BotDelay + 2], _seenY = new double[BotDelay + 2];
    int _seenN;
    double _botX = double.NaN, _botY;

    /// <summary>
    /// Один тик бота на місці <paramref name="seat"/>: простий, але не дурний воротар. Стоїть перед своїми воротами
    /// навпроти шайби; шайба летить у ворота — стає на її шлях (з відбоями від бортів); повільна шайба на своїй половині,
    /// а людина-напарник не ближча — замах у бік чужих воріт. Бачить із запізненням і трохи маже — йому можна забити.
    /// </summary>
    public void BotThink(int seat)
    {
        if (seat is < 0 or >= Seats || !Plays[seat]) return;
        var n = _seenX.Length;
        _seenX[_seenN % n] = Puck.X;
        _seenY[_seenN % n] = Puck.Y;
        _seenN++;
        var back = Math.Min(_seenN - 1, BotDelay);
        var i0 = (_seenN - 1 - back) % n;
        var i1 = (_seenN - back) % n;
        const double dt = TickMs / 1000.0;
        double px = _seenX[i0], py = _seenY[i0];
        double vx = back > 0 ? (_seenX[i1] - px) / dt : 0, vy = back > 0 ? (_seenY[i1] - py) / dt : 0;
        var team = Team[seat];
        var dir = team == 0 ? 1 : -1;
        var gx = team == 0 ? 0.0 : W;
        var line = gx + dir * 20;
        ref var me = ref Pads[seat];
        if (double.IsNaN(_botX)) (_botX, _botY) = (me.X, me.Y);
        // за замовчуванням — воротар: між шайбою й центром воріт
        double wx = line, wy = Math.Clamp(TableH / 2 + (py - TableH / 2) * 0.6, GoalLo - 6, GoalHi + 6);
        var speed = BotSpeed;
        var live = StartIn == 0 && ServeIn == 0;
        var ownHalf = team == 0 ? px < Mid : px > Mid;
        if (live && vx * dir < -40)
        {
            // шайба летить до нас — де вона перетне лінію воріт (з відбоями від бортів)
            var t = (line - px) / vx;
            if (t is > 0 and < 1.2)
            {
                var y = py + vy * t;
                const double span = TableH - 2 * PuckR;
                var m = ((y - PuckR) % (2 * span) + 2 * span) % (2 * span);
                y = PuckR + (m <= span ? m : 2 * span - m);
                wy = Math.Clamp(y, GoalLo - 10, GoalHi + 10);
            }
        }
        else if (live && ownHalf && Math.Abs(px - gx) > PadR + PuckR && Puck.Speed < 320)
        {
            // напарник-людина ближче до шайби — хай б'є він, бот стереже ворота
            var mine = Dist2(me.X, me.Y, px, py);
            var mate = double.MaxValue;
            for (var j = 0; j < Seats; j++)
                if (j != seat && Plays[j] && Team[j] == team) mate = Math.Min(mate, Dist2(Pads[j].X, Pads[j].Y, px, py));
            if (mine <= mate * 1.2)
            {
                // замах: ціль — трохи за шайбою на лінії до чужих воріт, а коли вже поруч — крізь шайбу
                var ox = team == 0 ? W : 0.0;
                var aimY = TableH / 2 + (rng.NextDouble() * 2 - 1) * 14;
                double ux = ox - px, uy = aimY - py;
                var ul = Math.Sqrt(ux * ux + uy * uy);
                ux /= ul;
                uy /= ul;
                const double rr = PadR + PuckR + 5;
                var reach = mine < rr * rr ? 12 : -(PadR + PuckR + 3);
                wx = px + ux * reach;
                wy = py + uy * reach;
                speed = BotStrike;
            }
        }
        // ціль бота повзе до бажаної не швидше за speed — без ривків і телепортів
        var step = speed * dt;
        double ex = wx - _botX, ey = wy - _botY;
        var d = Math.Sqrt(ex * ex + ey * ey);
        double nx = wx, ny = wy;
        if (d > step)
        {
            nx = _botX + ex / d * step;
            ny = _botY + ey / d * step;
        }
        ClampAim(team, ref nx, ref ny);
        var bvx = (nx - _botX) / dt;
        var bvy = (ny - _botY) / dt;
        (_botX, _botY) = (nx, ny);
        Aim(seat, nx, ny, bvx, bvy);
    }

    static double Dist2(double ax, double ay, double bx, double by) => (ax - bx) * (ax - bx) + (ay - by) * (ay - by);
}
