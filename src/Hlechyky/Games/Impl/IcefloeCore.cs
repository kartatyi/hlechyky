namespace Hlechyky.Games.Impl;

/// <summary>Один кругляш у валянках на кризі: фізичне тіло, намір, перезарядки й особисті лічильники партії.</summary>
public sealed class IcefloeBody
{
    public ArenaBody B;
    /// <summary>Грає цю партію (сидів на «Почати» і не вставав).</summary>
    public bool Plays;
    /// <summary>Стоїть на кризі в цьому раунді; false — у воді (на березі з сніжками) або не грає.</summary>
    public bool Alive;
    /// <summary>Сектор, який тримають (0..15), або −1 — відпустив. У воді — лише прицілювання.</summary>
    public int Want = -1;
    /// <summary>Куди дивиться обличчя (0..15): напрямок ривка й сніжки.</summary>
    public int Face;
    /// <summary>Тиків до наступного ривка.</summary>
    public int Cd;
    /// <summary>Тиків «удару» після ривка: поки більше нуля, у зіткненнях маса подвоєна (з кулаком — утричі).</summary>
    public int Hit;
    /// <summary>Тиків до наступної сніжки.</summary>
    public int ThrowCd;
    /// <summary>Сніжки з підбирачки ❄ (на кризі, до трьох).</summary>
    public int Ammo;
    /// <summary>Сніжки з берега (чотири на раунд після падіння).</summary>
    public int BankAmmo;
    /// <summary>Тиків ефекту шипів, глека, ковзанів і кулака. Шипи й ковзани одне одного знімають.</summary>
    public int Spikes, Jug, Skates, Fist;
    /// <summary>Тиків інею: тяги й ривка нема, тіло лише ковзає.</summary>
    public int Frozen;
    /// <summary>Кут, під яким шубовснув: там на березі й стоїть.</summary>
    public double BankAngle;
    /// <summary>Хто зачепив останнім і коли (номер тика) — кому зарахувати «випхнув».</summary>
    public int LastBy = -1, LastAt = int.MinValue / 2;
    /// <summary>Виграні раунди й випхнуті за партію.</summary>
    public int Wins, Pushouts;
    /// <summary>Падав у воду під час гри хоч раз за партію (для «Сухих валянок»).</summary>
    public bool Wet;
    /// <summary>
    /// Відкол з берега (прохід №3, п. 183): уже колов у цьому раунді; скільки тиків до відколу (0 — нема) і з якої
    /// вершини дуга.
    /// </summary>
    public bool ChipUsed;
    public int ChipIn, ChipS;
    /// <summary>Команда (п. 182): 0 сині, 1 руді, −1 — кожен сам за себе.</summary>
    public int Team = -1;
    /// <summary>Службове: у цьому підкроці тіло штовхнули — після всіх ударів йому ще раз стеля швидкості.</summary>
    internal bool Touched;

    public double Mu => Spikes > 0 ? IcefloeCore.SpikeMu : Skates > 0 ? IcefloeCore.SkateMu : IcefloeCore.Mu;
    /// <summary>Тяга — таблицею, не множенням: браузер бере рівно ці числа, і передбачення сходиться до біта.</summary>
    public double Thrust => Spikes > 0 || Skates > 0
        ? (Jug > 0 ? IcefloeCore.JugFastThrust : IcefloeCore.FastThrust)
        : (Jug > 0 ? IcefloeCore.JugThrust : IcefloeCore.Thrust);
    public double Mass => Jug > 0 ? IcefloeCore.JugMass : IcefloeCore.Mass;
    public double DashImpulse => Fist > 0 ? IcefloeCore.FistDash : IcefloeCore.DashImpulse;
    /// <summary>У скільки разів важчий у зіткненні, поки триває «удар» ривка.</summary>
    public int HitMass => Fist > 0 ? 3 : 2;
}

/// <summary>Сніжка в польоті: прямо, без гальмування, 3 секунди або до першого влучання.</summary>
public struct IcefloeBall
{
    public bool On;
    public int Id, Owner, Ttl;
    public double X, Y, Vx, Vy;
}

/// <summary>Підбирачка на кризі: шипи, глек, сніжки, завірюха, ковзани, іній або кулак.</summary>
public struct IcefloePickup
{
    public bool On;
    public int Kind, Ttl;
    public double X, Y;
}

/// <summary>
/// Світ Крижини без слова про кімнати й раунди: ставок, 24-кутна крижина, що тріскається й тане, тіла, сніжки
/// й підбирачки. Одиниця — сантиметр, вісь y вниз. Усі числа — з <c>docs/games/specs/icefloe.md</c> §2; там,
/// де spec довелось підправити (ставок ширший за кригу, край — справжній багатокутник), — пояснення в
/// розділі «Як реалізовано». Жодної алокації в <see cref="Step"/>: масиви живуть із ядром.
/// </summary>
public sealed class IcefloeCore(Random rng)
{
    public const int Seats = 8;
    public const int TickMs = 40;
    /// <summary>Два підкроки по 20 мс: за підкрок тіло зсувається ≤ 22 см при вікні зіткнення 120.</summary>
    public const int Sub = 2;
    public const double H = 0.02;

    /// <summary>
    /// Ставок 2600 × 2600 (spec казав 2400, але найбільша крижина на вісьмох — 1100 × 1.08 = 1188 см — тоді лізла
    /// б на берег із радіусом 1150). Центр — (1300, 1300). Сніжки живуть у межах ставка.
    /// </summary>
    public const double Pond = 2600, Cx = Pond / 2, Cy = Pond / 2;
    /// <summary>Від найбільшої можливої вершини криги (R0 · 1.08) до берега — 40 см води; від берега до вибулих — 37.</summary>
    public const double ShoreGap = 40, BankGap = 37;

    /// <summary>
    /// Де закінчується вода й починається сніг. Залежить від криги партії: на двох (R0 = 800) берег ближчий
    /// (904), на вісьмох (1100) — 1228. Так браузер показує лише ставок навколо криги, і на двох тіла на екрані
    /// на третину більші, ніж якби камера завжди брала найбільший ставок.
    /// </summary>
    public double Shore { get; private set; } = ShoreOf(BaseRadius(2));
    /// <summary>Де стоять ті, хто шубовснув (на снігу, трохи за лінією берега).</summary>
    public double Bank => Shore + BankGap;

    public static double ShoreOf(int r0) => Math.Round(r0 * 1.08 + ShoreGap);
    public const int Vertices = 24;

    public const double BodyR = 60;
    public const double Mass = 1.0, JugMass = 2.5;
    /// <summary>Тяга: звичайна, з глеком, у шипах чи ковзанах (FastThrust) і глек разом із ними.</summary>
    public const double Thrust = 1800, JugThrust = 1600, FastThrust = 2700, JugFastThrust = 2400;
    /// <summary>Тертя: шипи тримають (гранична ≈ 400 см/с, удар відносить утричі менше), ковзани несуть далі.</summary>
    public const double Mu = 2.0, SpikeMu = 6.0, SkateMu = 1.2;
    public const double VMax = 1100;
    public const double E = 0.85;

    public const double DashImpulse = 520, FistDash = 832;
    public const int DashCd = 25, HitTicks = 6;
    /// <summary>Удар за останні дві секунди — і падіння зараховується тому, хто вдарив.</summary>
    public const int CreditTicks = 50;
    /// <summary>Від такого імпульсу подія зіткнення летить у кадр (скалки, звук).</summary>
    public const double LoudHit = 250;

    /// <summary>Відкол з берега: секунда тріщини, дуга з трьох вершин меншає на 22 % (уся крига — ні).</summary>
    public const int ChipWarn = 25, ChipLen = 3;
    public const int FirstWarn = 250, WarnEvery = 200, Breaks = 6, WarnTicks = 50;
    public const int MeltFrom = 1300, CapTicks = 1875;
    public const double ArcShrink = 0.78, AllShrink = 0.90;

    /// <summary>Сніжка штовхає на 600 см/с поділені на масу (глек тримає удар) — сильніше за ривок.</summary>
    public const double BallSpeed = 800, BallR = 14, BallPush = 600, BallMuzzle = 70;
    public const int BallTtl = 75, BankAmmoMax = 4, BankThrowCd = 50, IceThrowCd = 25, IceAmmoMax = 3, BallPickup = 2, BallSlots = 16;

    /// <summary>
    /// Підбирачки: з третьої секунди кожні 4 с, поки на кризі менше <see cref="PickupCap"/>; лежать 16 с (інакше
    /// четверта не встигала б з'явитись, поки лежить перша); ефект 10 с.
    /// </summary>
    public const int PickupFrom = 75, PickupEvery = 100, PickupMax = 4, PickupTtl = 400, EffectTicks = 250;
    public const double PickupReach = BodyR + 22;
    public const int KindSpikes = 0, KindJug = 1, KindBall = 2, KindGust = 3, KindSkates = 4, KindFrost = 5, KindFist = 6, Kinds = 7;
    /// <summary>Завірюха: суперників до 450 см (центр до центру) відкидає від того, хто підібрав, — 600 впритул, 300 на краю.</summary>
    public const double GustR = 450, GustPush = 600;
    /// <summary>Іній: суперники 1.2 с без тяги й ривка.</summary>
    public const int FrostTicks = 30;

    /// <summary>Скільки підбирачок водночас на кризі: на шістьох і більше — чотири, інакше три.</summary>
    public static int PickupCap(int players) => players >= 6 ? 4 : 3;

    // Коди подій кадру (spec §4.3).
    public const int EvBump = 1, EvSplash = 2, EvDash = 3, EvPickup = 4, EvBall = 5, EvBreak = 6, EvRound = 7;
    const int EvWidth = 6, EvCap = 48;

    /// <summary>Стартовий радіус криги за кількістю гравців на «Почати».</summary>
    public static int BaseRadius(int n) => n <= 3 ? 800 : n <= 5 ? 950 : 1100;

    static readonly double Step15 = Math.PI / 12;
    static readonly double Sin15 = Math.Sin(Step15);
    /// <summary>Одиничні вектори вершин (вершина i — під кутом i·15°).</summary>
    static readonly double[] VCos = [.. Enumerable.Range(0, Vertices).Select(i => Math.Cos(i * Step15))];
    static readonly double[] VSin = [.. Enumerable.Range(0, Vertices).Select(i => Math.Sin(i * Step15))];

    public IcefloeBody[] Bodies { get; } = [.. Enumerable.Range(0, Seats).Select(_ => new IcefloeBody())];
    /// <summary>Радіуси вершин криги до танення.</summary>
    public double[] R { get; } = new double[Vertices];
    public int R0 { get; private set; } = BaseRadius(2);
    /// <summary>Скільки сантиметрів уже розтало з усіх боків.</summary>
    public int Melt { get; set; }
    /// <summary>Версія криги: росте на кожну нову крижину й кожен відкол — клієнт по ній перемальовує край.</summary>
    public int Iv { get; private set; }
    /// <summary>Попередження про відкол: перша вершина дуги й довжина; −1 — тріщини нема.</summary>
    public int CrackS { get; private set; } = -1;
    public int CrackL { get; private set; }

    public IcefloeBall[] Balls { get; } = new IcefloeBall[BallSlots];
    int _ballId;
    public IcefloePickup[] Pickups { get; } = new IcefloePickup[PickupMax];

    /// <summary>Номер тика партії (для кадрів і «хто кого коли зачепив»).</summary>
    public int T { get; set; }
    /// <summary>Тики від початку фази гри поточного раунду.</summary>
    public int Rt { get; set; }

    /// <summary>Порядок падінь у поточному раунді.</summary>
    public List<int> Out { get; } = new(Seats);
    /// <summary>Хто кого випхнув у цьому раунді: пари (впав, хто) — −1, якщо сам.</summary>
    public List<(int Fell, int By)> ByList { get; } = new(Seats);

    /// <summary>Події поточного тика плоским буфером (без алокацій): до 48 подій по 6 чисел.</summary>
    readonly int[] _ev = new int[EvCap * EvWidth];
    readonly int[] _evLen = new int[EvCap];
    public int EvCount { get; private set; }
    /// <summary>У цьому тику відкололась дуга — види треба розіслати одразу.</summary>
    public bool Broke { get; private set; }

    public int Playing
    {
        get
        {
            var n = 0;
            for (var i = 0; i < Seats; i++) if (Bodies[i].Plays) n++;
            return n;
        }
    }

    public int AliveCount
    {
        get
        {
            var n = 0;
            for (var i = 0; i < Seats; i++) if (Bodies[i].Plays && Bodies[i].Alive) n++;
            return n;
        }
    }

    /// <summary>
    /// Чи щось іще рухається (тіла ковзають швидше за 20 см/с, сніжки летять) — тоді кадр варто слати щотика.
    /// Повільніше — пів пікселя за 5 тиків на канвасі, інтерполяції вистачає й кадру раз на п'ять.
    /// </summary>
    public bool Moving
    {
        get
        {
            for (var i = 0; i < Seats; i++)
            {
                var b = Bodies[i];
                if (b.Plays && b.Alive && b.B.Vx * b.B.Vx + b.B.Vy * b.B.Vy > 400) return true;
            }
            for (var i = 0; i < BallSlots; i++) if (Balls[i].On) return true;
            return false;
        }
    }

    /// <summary>Тик без світу (відлік): події минулого тика більше не летять.</summary>
    public void Idle()
    {
        EvCount = 0;
        Broke = false;
    }

    /// <summary>Раунд скінчився — попередження про відкол уже ні до чого.</summary>
    public void ClearCrack()
    {
        CrackS = -1;
        CrackL = 0;
        for (var i = 0; i < Seats; i++) Bodies[i].ChipIn = 0;
    }

    // ---------------------------------------------------------------------------------------------
    // Партія і раунд
    // ---------------------------------------------------------------------------------------------

    /// <summary>Нова партія: хто грає, рахунки з нуля. Кригу й спавни ставить <see cref="NewRound"/>.</summary>
    public void ResetParty(bool[] plays)
    {
        T = 0;
        for (var i = 0; i < Seats; i++)
        {
            var b = Bodies[i];
            b.Plays = i < plays.Length && plays[i];
            b.Wins = 0;
            b.Pushouts = 0;
            b.Wet = false;
            b.Want = -1;
        }
    }

    /// <summary>Свіжа нерівна крижина ±8 % (з генератора кімнати) і всі, хто грає, — на спавнах.</summary>
    public void NewRound(int baseRadius)
    {
        R0 = baseRadius;
        Shore = ShoreOf(baseRadius);
        for (var i = 0; i < Vertices; i++) R[i] = R0 * (0.92 + 0.16 * rng.NextDouble());
        Iv++;
        Melt = 0;
        CrackS = -1;
        CrackL = 0;
        Rt = 0;
        Out.Clear();
        ByList.Clear();
        for (var i = 0; i < BallSlots; i++) Balls[i].On = false;
        for (var i = 0; i < PickupMax; i++) Pickups[i].On = false;
        EvCount = 0;
        Spawn();
    }

    /// <summary>Крижина для лобі: рівна, без генератора — сідована партія лишається тією самою.</summary>
    public void Flat(int baseRadius)
    {
        R0 = baseRadius;
        Shore = ShoreOf(baseRadius);
        for (var i = 0; i < Vertices; i++) R[i] = R0;
        Melt = 0;
        CrackS = -1;
        Spawn();
    }

    /// <summary>
    /// Спавни: рівномірно на колі 0.6·R0, перший (найменше місце) угорі, далі за годинниковою; обличчям до
    /// центру. Намір, який тримають, переживає новий раунд — людина ж не відпускала клавішу. Але в того, хто
    /// стояв на березі, «намір» — це приціл сніжки, а не рух: його гасимо, інакше раунд почався б із ковзання
    /// туди, куди він востаннє цілився.
    /// </summary>
    public void Spawn()
    {
        for (var i = 0; i < Seats; i++) { Bodies[i].ChipUsed = false; Bodies[i].ChipIn = 0; }
        var n = Playing;
        var k = 0;
        for (var i = 0; i < Seats; i++)
        {
            var b = Bodies[i];
            if (b.Plays && !b.Alive) b.Want = -1;
            b.Alive = b.Plays;
            b.Cd = b.Hit = b.ThrowCd = b.Ammo = b.BankAmmo = b.Spikes = b.Jug = b.Skates = b.Fist = b.Frozen = 0;
            b.LastBy = -1;
            b.LastAt = int.MinValue / 2;
            b.B = new ArenaBody(Cx, Cy, BodyR, 1 / Mass);
            if (!b.Plays) continue;
            var deg = -90 + k * 360.0 / Math.Max(1, n);
            k++;
            var rad = deg * Math.PI / 180;
            b.B.X = Cx + 0.6 * R0 * Math.Cos(rad);
            b.B.Y = Cy + 0.6 * R0 * Math.Sin(rad);
            b.Face = b.Want >= 0 ? b.Want : SectorOf(deg + 180);
        }
    }

    /// <summary>Кут у градусах → найближчий сектор 0..15.</summary>
    public static int SectorOf(double deg)
    {
        var s = (int)Math.Round(deg / 22.5, MidpointRounding.AwayFromZero) % 16;
        return s < 0 ? s + 16 : s;
    }

    // ---------------------------------------------------------------------------------------------
    // Край криги
    // ---------------------------------------------------------------------------------------------

    /// <summary>Радіус вершини після танення.</summary>
    public double Vertex(int i) => Math.Max(0, R[i % Vertices] - Melt);

    /// <summary>
    /// Відстань від центру до краю криги в напрямку θ (радіани, y униз). Край — справжній багатокутник, рівно
    /// такий, як його малює браузер: пряма між сусідніми вершинами, а не дуга (на 15° різниця до 1 % радіуса —
    /// тіло «висіло б у повітрі» над намальованою водою).
    /// </summary>
    public double EdgeAt(double theta)
    {
        var a = theta % (2 * Math.PI);
        if (a < 0) a += 2 * Math.PI;
        var f = a / Step15;
        var i = (int)f;
        if (i >= Vertices) i = 0;
        var phi = a - i * Step15;
        var ri = Vertex(i);
        if (phi <= 0) return ri;                                  // рівно у вершині — без похибки ділення
        var rj = Vertex(i + 1);
        var den = ri * Math.Sin(phi) + rj * Math.Sin(Step15 - phi);
        return den <= 1e-9 ? 0 : ri * rj * Sin15 / den;
    }

    /// <summary>Центр точки на кризі (рівно на краю — ще на кризі).</summary>
    public bool OnIce(double x, double y)
    {
        var dx = x - Cx;
        var dy = y - Cy;
        return Math.Sqrt(dx * dx + dy * dy) <= EdgeAt(Math.Atan2(dy, dx)) + 1e-9;
    }

    // ---------------------------------------------------------------------------------------------
    // Ввід
    // ---------------------------------------------------------------------------------------------

    public void Move(int seat, int a)
    {
        var b = Bodies[seat];
        b.Want = a;
        if (a >= 0) b.Face = a;
    }

    /// <summary>Ривок. Повертає текст відмови або null. Фазу перевіряє гра.</summary>
    public string? Dash(int seat)
    {
        var b = Bodies[seat];
        if (!b.Plays) return "Ти тут не граєш";
        if (!b.Alive) return "Ти у воді — кидай сніжки";
        if (b.Frozen > 0) return "Замерз — ще мить";
        if (b.Cd > 0) return "Ще не готово";
        b.B.Vx += b.DashImpulse * ArenaPhysics.Cos(b.Face);
        b.B.Vy += b.DashImpulse * ArenaPhysics.Sin(b.Face);
        ArenaPhysics.Cap(ref b.B, VMax);
        b.Cd = DashCd;
        b.Hit = HitTicks;
        Event(EvDash, seat);
        return null;
    }

    /// <summary>Сніжка з криги (з підбирачки) або з берега. Повертає текст відмови або null.</summary>
    public string? Throw(int seat)
    {
        var b = Bodies[seat];
        if (!b.Plays) return "Ти тут не граєш";
        if (b.Alive ? b.Ammo <= 0 : b.BankAmmo <= 0) return "Сніжок нема";
        if (b.ThrowCd > 0) return "Не так швидко";
        var slot = -1;
        for (var i = 0; i < BallSlots; i++) if (!Balls[i].On) { slot = i; break; }
        if (slot < 0) return "Забагато сніжок у повітрі";

        var cx = ArenaPhysics.Cos(b.Face);
        var sy = ArenaPhysics.Sin(b.Face);
        double x, y;
        if (b.Alive)
        {
            x = b.B.X + BallMuzzle * cx;
            y = b.B.Y + BallMuzzle * sy;
            b.Ammo--;
            b.ThrowCd = IceThrowCd;
        }
        else
        {
            (x, y) = BankPoint(b.BankAngle);
            b.BankAmmo--;
            b.ThrowCd = BankThrowCd;
        }
        Balls[slot] = new IcefloeBall { On = true, Id = ++_ballId, Owner = seat, Ttl = BallTtl, X = x, Y = y, Vx = BallSpeed * cx, Vy = BallSpeed * sy };
        return null;
    }

    /// <summary>Точка на березі під кутом падіння.</summary>
    public (double X, double Y) BankPoint(double angle) => (Cx + Bank * Math.Cos(angle), Cy + Bank * Math.Sin(angle));

    /// <summary>Гравець устав із-за столу: тіло зникає без «шубовсь» і без заліку будь-кому.</summary>
    public void Drop(int seat)
    {
        var b = Bodies[seat];
        b.Plays = false;
        b.Alive = false;
        b.Want = -1;
        b.ChipIn = 0;
        for (var i = 0; i < Seats; i++) if (Bodies[i].LastBy == seat) Bodies[i].LastBy = -1;
    }

    // ---------------------------------------------------------------------------------------------
    // Крок
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Один тик (40 мс). <paramref name="play"/> — фаза гри: крига тріскається й тане, з'являються підбирачки,
    /// падіння зараховуються. Поза грою (кінець раунду) світ лише доковзує: тіла їдуть, сніжки летять, але
    /// крига застигла й рахунок не міняється.
    /// </summary>
    public void Step(bool play)
    {
        T++;
        EvCount = 0;
        Broke = false;
        if (play) Rt++;

        for (var i = 0; i < Seats; i++)
        {
            var b = Bodies[i];
            if (b.Cd > 0) b.Cd--;
            if (b.ThrowCd > 0) b.ThrowCd--;
            if (b.Frozen > 0) b.Frozen--;
            if (play)
            {
                if (b.Spikes > 0) b.Spikes--;
                if (b.Jug > 0) b.Jug--;
                if (b.Skates > 0) b.Skates--;
                if (b.Fist > 0) b.Fist--;
            }
        }
        for (var i = 0; i < BallSlots; i++)
            if (Balls[i].On && --Balls[i].Ttl <= 0) Balls[i].On = false;

        if (play)
        {
            Schedule();
            for (var i = 0; i < PickupMax; i++)
            {
                ref var p = ref Pickups[i];
                if (!p.On) continue;
                if (--p.Ttl <= 0 || !OnIce(p.X, p.Y)) p.On = false;   // пролежала своє або пішла під воду
            }
            SpawnPickup();
        }

        for (var s = 0; s < Sub; s++) Substep(play);
        if (play) Grab();
        // «Удар» гасне після підкроків: ривок між тиками б'є подвоєною масою рівно HitTicks тиків.
        for (var i = 0; i < Seats; i++) if (Bodies[i].Hit > 0) Bodies[i].Hit--;
    }

    /// <summary>Тріщини, відколи й танення за розкладом §2.6.</summary>
    void Schedule()
    {
        if (Rt >= MeltFrom) Melt++;
        for (var i = 0; i < Seats; i++)
        {
            var b = Bodies[i];
            if (b.ChipIn > 0 && --b.ChipIn == 0) BreakArc(b.ChipS, ChipLen);
        }
        for (var k = 0; k < Breaks; k++)
        {
            var warn = FirstWarn + k * WarnEvery;
            if (Rt == warn)
            {
                CrackS = rng.Next(Vertices);
                CrackL = 5 + rng.Next(3);
            }
            else if (Rt == warn + WarnTicks && CrackS >= 0)
            {
                Break(CrackS, CrackL);
            }
        }
    }

    /// <summary>Відкол: дуга меншає на 22 %, уся крига — на 10 %.</summary>
    public void Break(int s, int l)
    {
        for (var k = 0; k < l; k++) R[(s + k) % Vertices] *= ArcShrink;
        for (var i = 0; i < Vertices; i++) R[i] *= AllShrink;
        Iv++;
        CrackS = -1;
        CrackL = 0;
        Broke = true;
        Event(EvBreak, s, l);
    }

    /// <summary>Відкол з берега: лише дуга меншає на 22 %, решта криги ціла.</summary>
    public void BreakArc(int s, int l)
    {
        for (var k = 0; k < l; k++) R[(s + k) % Vertices] *= ArcShrink;
        Iv++;
        Broke = true;
        Event(EvBreak, s, l);
    }

    /// <summary>
    /// Вибулий раз за раунд відколює шматок краю (п. 183): туди, куди цілиться з берега, — перша крига на промені.
    /// Секунду світиться тріщина (встигнеш відскочити), потім дуга з трьох вершин меншає. Повертає текст відмови.
    /// </summary>
    public string? Chip(int seat)
    {
        var b = Bodies[seat];
        if (!b.Plays) return "Ти тут не граєш";
        if (b.Alive) return "Лід колють із берега — спершу шубовсни";
        if (b.ChipUsed) return "Лід уже колов — раз за раунд";
        var (x0, y0) = BankPoint(b.BankAngle);
        var cx = ArenaPhysics.Cos(b.Face);
        var sy = ArenaPhysics.Sin(b.Face);
        for (var d = 0.0; d <= 2 * Bank; d += 20)
        {
            var x = x0 + d * cx;
            var y = y0 + d * sy;
            if (!OnIce(x, y)) continue;
            var v = (int)Math.Round(Math.Atan2(y - Cy, x - Cx) / (Math.PI / 12));
            b.ChipS = ((v - ChipLen / 2) % Vertices + Vertices) % Vertices;
            b.ChipIn = ChipWarn;
            b.ChipUsed = true;
            return null;
        }
        return "Цілься в кригу — туди лід не дістати";
    }

    void SpawnPickup()
    {
        if (Rt < PickupFrom || (Rt - PickupFrom) % PickupEvery != 0) return;
        var slot = -1;
        var on = 0;
        for (var i = 0; i < PickupMax; i++)
        {
            if (Pickups[i].On) on++;
            else if (slot < 0) slot = i;
        }
        if (slot < 0 || on >= PickupCap(Playing)) return;
        var phi = rng.NextDouble() * 2 * Math.PI;
        var rho = (0.2 + 0.5 * rng.NextDouble()) * EdgeAt(phi);
        var kind = rng.Next(Kinds);
        Pickups[slot] = new IcefloePickup { On = true, Kind = kind, Ttl = PickupTtl, X = Cx + rho * Math.Cos(phi), Y = Cy + rho * Math.Sin(phi) };
    }

    void Grab()
    {
        for (var i = 0; i < PickupMax; i++)
        {
            ref var p = ref Pickups[i];
            if (!p.On) continue;
            for (var s = 0; s < Seats; s++)
            {
                var b = Bodies[s];
                if (!b.Plays || !b.Alive) continue;
                var dx = b.B.X - p.X;
                var dy = b.B.Y - p.Y;
                if (dx * dx + dy * dy >= PickupReach * PickupReach) continue;
                switch (p.Kind)
                {
                    case KindSpikes: b.Spikes = EffectTicks; b.Skates = 0; break;
                    case KindJug: b.Jug = EffectTicks; break;
                    case KindSkates: b.Skates = EffectTicks; b.Spikes = 0; break;
                    case KindFist: b.Fist = EffectTicks; break;
                    case KindGust: Gust(s); break;
                    case KindFrost: Frost(s); break;
                    default: b.Ammo = Math.Min(IceAmmoMax, b.Ammo + BallPickup); break;
                }
                p.On = false;
                Event(EvPickup, s, p.Kind);
                break;
            }
        }
    }

    /// <summary>Суперник для завірюхи й інею: живий, не той, хто підібрав, і не з його команди.</summary>
    bool Rival(int seat, int of)
    {
        if (seat == of) return false;
        var b = Bodies[seat];
        return b.Plays && b.Alive && (b.Team < 0 || b.Team != Bodies[of].Team);
    }

    /// <summary>
    /// Завірюха: суперників поруч відкидає від того, хто підібрав, — ближчих сильніше (600 впритул, 300 на
    /// <see cref="GustR"/>), глек тримає за масою. Упав за дві секунди — «випхнув» тому, хто підібрав.
    /// </summary>
    public void Gust(int seat)
    {
        var src = Bodies[seat];
        for (var i = 0; i < Seats; i++)
        {
            if (!Rival(i, seat)) continue;
            var b = Bodies[i];
            var dx = b.B.X - src.B.X;
            var dy = b.B.Y - src.B.Y;
            var d = Math.Sqrt(dx * dx + dy * dy);
            if (d > GustR) continue;
            var (nx, ny) = d > 1e-9 ? (dx / d, dy / d) : (1.0, 0.0);
            var near = Math.Clamp((d - 2 * BodyR) / (GustR - 2 * BodyR), 0, 1);
            var dv = GustPush * (1 - 0.5 * near) / b.Mass;
            b.B.Vx += dv * nx;
            b.B.Vy += dv * ny;
            ArenaPhysics.Cap(ref b.B, VMax);
            b.LastBy = seat;
            b.LastAt = T;
        }
    }

    /// <summary>Іній: усі суперники на <see cref="FrostTicks"/> без тяги й ривка — лише ковзають.</summary>
    public void Frost(int seat)
    {
        for (var i = 0; i < Seats; i++)
            if (Rival(i, seat)) Bodies[i].Frozen = FrostTicks;
    }

    /// <summary>
    /// Тяга й крок з тертям одного тіла на один підкрок. Рівно ця функція (і в тому самому порядку операцій)
    /// живе в браузері — передбачення свого тіла збігається з сервером до біта, поки ніхто не штовхається.
    /// </summary>
    public static void Glide(ref ArenaBody b, int want, double mu, double thrust)
    {
        if (want >= 0)
        {
            var k = thrust * H;
            b.Vx += k * ArenaPhysics.Cos(want);
            b.Vy += k * ArenaPhysics.Sin(want);
        }
        ArenaPhysics.Integrate(ref b, H, mu, VMax);
    }

    void Substep(bool play)
    {
        // тяга й рух
        for (var i = 0; i < Seats; i++)
        {
            var b = Bodies[i];
            if (!b.Plays || !b.Alive) continue;
            Glide(ref b.B, b.Frozen > 0 ? -1 : b.Want, b.Mu, b.Thrust);
            b.B.InvM = 1 / (b.Mass * (b.Hit > 0 ? b.HitMass : 1));
            b.Touched = false;
        }

        // зіткнення пар у порядку місць
        const double reach = 2 * BodyR;
        for (var i = 0; i < Seats; i++)
        {
            var a = Bodies[i];
            if (!a.Plays || !a.Alive) continue;
            for (var j = i + 1; j < Seats; j++)
            {
                var c = Bodies[j];
                if (!c.Plays || !c.Alive) continue;
                var dx = c.B.X - a.B.X;
                var dy = c.B.Y - a.B.Y;
                if (dx * dx + dy * dy >= reach * reach) continue;
                var jm = ArenaPhysics.Collide(ref a.B, ref c.B, E);
                a.Touched = c.Touched = true;
                a.LastBy = j;
                a.LastAt = T;
                c.LastBy = i;
                c.LastAt = T;
                if (jm >= LoudHit)
                {
                    var d = Math.Sqrt(dx * dx + dy * dy);
                    var (nx, ny) = d > 1e-9 ? (dx / d, dy / d) : (1.0, 0.0);
                    Event(EvBump, i, j, (int)Math.Round(a.B.X + nx * BodyR), (int)Math.Round(a.B.Y + ny * BodyR), (int)Math.Round(jm));
                }
            }
        }

        // сніжки
        for (var k = 0; k < BallSlots; k++)
        {
            ref var ball = ref Balls[k];
            if (!ball.On) continue;
            ball.X += ball.Vx * H;
            ball.Y += ball.Vy * H;
            if (ball.X < 0 || ball.X > Pond || ball.Y < 0 || ball.Y > Pond) { ball.On = false; continue; }
            const double hit = BodyR + BallR;
            for (var s = 0; s < Seats; s++)
            {
                var b = Bodies[s];
                if (s == ball.Owner || !b.Plays || !b.Alive) continue;
                var dx = b.B.X - ball.X;
                var dy = b.B.Y - ball.Y;
                if (dx * dx + dy * dy >= hit * hit) continue;
                var push = BallPush / BallSpeed / b.Mass;
                b.B.Vx += push * ball.Vx;
                b.B.Vy += push * ball.Vy;
                b.Touched = true;
                if (Bodies[ball.Owner].Plays)
                {
                    b.LastBy = ball.Owner;
                    b.LastAt = T;
                }
                Event(EvBall, s, ball.Owner, (int)Math.Round(ball.X), (int)Math.Round(ball.Y));
                ball.On = false;
                break;
            }
        }

        // стеля швидкості після всіх поштовхів; падіння
        for (var i = 0; i < Seats; i++)
        {
            var b = Bodies[i];
            if (!b.Plays || !b.Alive) continue;
            if (b.Touched) ArenaPhysics.Cap(ref b.B, VMax);
            if (b.B.X < 0 || b.B.X > Pond) b.B.X = Math.Clamp(b.B.X, 0, Pond);
            if (b.B.Y < 0 || b.B.Y > Pond) b.B.Y = Math.Clamp(b.B.Y, 0, Pond);
            if (!OnIce(b.B.X, b.B.Y)) Fall(i, play);
        }
    }

    /// <summary>Шубовсь: тіло йде на берег під кутом падіння з трьома сніжками; хто штовхнув за 2 с — тому «випхнув».</summary>
    void Fall(int seat, bool play)
    {
        var b = Bodies[seat];
        var (x, y) = ((int)Math.Round(b.B.X), (int)Math.Round(b.B.Y));
        b.Alive = false;
        b.BankAngle = Math.Atan2(b.B.Y - Cy, b.B.X - Cx);
        var (bx, by) = BankPoint(b.BankAngle);
        b.B.X = bx;
        b.B.Y = by;
        b.B.Vx = b.B.Vy = 0;
        b.BankAmmo = BankAmmoMax;
        b.ThrowCd = 0;
        b.Ammo = 0;
        b.Cd = b.Hit = 0;
        b.Spikes = b.Jug = b.Skates = b.Fist = b.Frozen = 0;
        var who = -1;
        if (play)
        {
            b.Wet = true;
            Out.Add(seat);
            if (b.LastBy >= 0 && b.LastBy != seat && T - b.LastAt <= CreditTicks && Bodies[b.LastBy].Plays
                && (b.Team < 0 || Bodies[b.LastBy].Team != b.Team))
            {
                who = b.LastBy;
                Bodies[who].Pushouts++;
            }
            ByList.Add((seat, who));
        }
        Event(EvSplash, seat, who, x, y);
    }

    // ---------------------------------------------------------------------------------------------
    // Події кадру
    // ---------------------------------------------------------------------------------------------

    public void Event(int code, int a = 0, int b = 0, int c = int.MinValue, int d = int.MinValue, int e = int.MinValue)
    {
        if (EvCount >= EvCap) return;
        var o = EvCount * EvWidth;
        _ev[o] = code;
        _ev[o + 1] = a;
        var n = 2;
        if (code is EvBump or EvSplash or EvBall or EvPickup or EvBreak) _ev[o + n++] = b;
        if (c != int.MinValue) _ev[o + n++] = c;
        if (d != int.MinValue) _ev[o + n++] = d;
        if (e != int.MinValue) _ev[o + n++] = e;
        _evLen[EvCount] = n;
        EvCount++;
    }

    /// <summary>Події тика для кадру — нові масиви (кадр серіалізують поза замком кімнати).</summary>
    public int[][] Events()
    {
        var list = new int[EvCount][];
        for (var i = 0; i < EvCount; i++) list[i] = _ev.AsSpan(i * EvWidth, _evLen[i]).ToArray();
        return list;
    }
}
