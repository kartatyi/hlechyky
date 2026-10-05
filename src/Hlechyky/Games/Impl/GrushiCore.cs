namespace Hlechyky.Games.Impl;

/// <summary>
/// Світ «Крадіїв груш»: квадратний сад 1000×1000 одиниць (y — донизу), дерева всередині, комори по краю. Тут лише
/// фізика й правила кроку — хода з ношею, підбирання, скидання в комору, крадіжка, штовхан, хвилі груш з дерев.
/// Фази, вид, кадр, боти й рахунок — у <see cref="Grushi"/>. Випадковість — лише з переданого <see cref="Random"/>
/// (це <c>Ctx.Rng</c>): партія за сідом повторюється до тика. Spec: <c>docs/games/specs/grushi.md</c>.
/// </summary>
public sealed class GrushiCore(Random rng)
{
    public const int Seats = 8;
    public const int TickMs = 40;
    public const int TicksPerSec = 1000 / TickMs;

    /// <summary>Сад — квадрат <see cref="Size"/>×<see cref="Size"/>; центр — <see cref="C"/>.</summary>
    public const double Size = 1000, C = Size / 2;
    /// <summary>Радіус тіла гравця й груші (для дотику).</summary>
    public const double BodyR = 28, PearR = 14;
    /// <summary>Комори стоять на квадраті з півстороною <see cref="LarderAt"/> від центру — по краю саду.</summary>
    public const double LarderAt = 430;
    /// <summary>Зона комори: ближче за це до її центру — скидаєш свої груші або крадеш чужі.</summary>
    public const double LarderR = 80;
    /// <summary>Дальність штовхана (від центру до центру).</summary>
    public const double PushR = 80;
    /// <summary>Швидкість порожнього, одиниць за тик (260 од/с); кожна груша в ноші — мінус 10 %.</summary>
    public const double Speed = 10.4, SlowPerPear = 0.1;
    public const int CarryMax = 5;
    /// <summary>Золота груша важить як одна, а в коморі — це три.</summary>
    public const int GoldValue = 3;
    /// <summary>Перезарядка штовхана 3 с; приголомшений 0,5 с не ходить і не підбирає; ще 1 с його не штовхнеш знов.</summary>
    public const int PushCd = 3 * TicksPerSec, StunTicks = 12, ImmuneTicks = 25;
    /// <summary>Відкидає на 8 тиків по 9 од — разом ~72 од від того, хто штовхнув. Комора під самою огорожею, тож
    /// злодія, якого господар штовхає ізсередини саду, огорожа однаково лишає в зоні — боронить комору не відліт,
    /// а те, що недоторканний не краде (див. Larders).</summary>
    public const int KnockTicks = 8;
    public const double KnockStep = 9;
    /// <summary>Стоїш у зоні чужої комори 1 с — береш грушу, далі ще по одній щосекунди.</summary>
    public const int StealTicks = TicksPerSec;
    /// <summary>Висипана груша 0,3 с «летить» — підібрати її ще не можна (видно, куди впала).</summary>
    public const int SpillLock = 8;
    /// <summary>Дерево трусить 1,2 с (видно в кадрі), потім сиплеться хвиля.</summary>
    public const int ShakeTicks = 30;
    /// <summary>Між хвилями — 3,6–5,6 с; на землі не більше <see cref="GroundMax"/> груш (решта хвилі не впаде).</summary>
    public const int WaveMin = 90, WaveMax = 140, GroundMax = 40;
    /// <summary>Шанс, що груша хвилі золота, у відсотках.</summary>
    public const int GoldPct = 7;

    /// <summary>Дерева: середнє й чотири навколо — комори стоять далі, по краю.</summary>
    public static readonly (double X, double Y)[] Trees = [(500, 500), (290, 290), (710, 290), (290, 710), (710, 710)];

    // Події кадру: [тип, a, b]. Кадр іде щотика, тож подія живе рівно один тик.
    public const int EvPick = 1;     // a — хто, b — id груші
    public const int EvDrop = 2;     // a — хто скинув у свою комору, b — скільки груш (золота — 3)
    public const int EvSteal = 3;    // a — злодій, b — чия комора
    public const int EvPush = 4;     // a — хто штовхнув, b — кого
    public const int EvSpill = 5;    // a — у кого висипалось, b — скільки груш з ноші
    public const int EvWhiff = 6;    // a — штовхнув повітря
    public const int EvShake = 7;    // a — яке дерево затрусилось
    public const int EvFall = 8;     // a — дерево, b — скільки груш упало

    public sealed class Body
    {
        public bool Plays;
        public double X, Y;
        public int Want = -1, Face;
        /// <summary>Ноша: скільки груш і скільки з них золотих.</summary>
        public int Carry, Gold;
        public int Cd, Stun, Immune, Knock;
        public double KnockX, KnockY;
        /// <summary>Скільки тиків стоїть у зоні чужої комори, і чиєї (−1 — ніде не краде).</summary>
        public int StealProg, StealFrom = -1;
        // Статистика партії — для ачівок, підсумку й ботів.
        public int Larder, Stole, Lost, Pushes, Spilled, Loads5, Delivered;
        public double LarderX, LarderY;
        /// <summary>Комора стоїть (гравець грав на старті; встав — комора лишається).</summary>
        public bool HasLarder;
    }

    public sealed class Pear
    {
        public int Id;
        public double X, Y;
        public bool Gold;
        public int Lock;
    }

    public readonly Body[] Bodies = [.. Enumerable.Range(0, Seats).Select(_ => new Body())];
    public readonly List<Pear> Ground = [];
    /// <summary>Тик світу (з початку партії, і на відліку теж) і тик гри (лише фаза гри).</summary>
    public int T, Rt;
    /// <summary>Яке дерево трусить (−1 — жодне) і скільки ще; через скільки тиків наступна хвиля.</summary>
    public int ShakeTree = -1, ShakeLeft, NextWave;
    int _pearId;
    int _playing;
    readonly List<int[]> _events = [];
    /// <summary>Події з Act між тиками (штовхан): ідуть у кадр наступного тика, а не губляться на його початку.</summary>
    readonly List<int[]> _pending = [];
    bool _inStep;

    static readonly double[] DirX = [.. Enumerable.Range(0, 16).Select(i => Math.Cos(i * Math.PI / 8))];
    static readonly double[] DirY = [.. Enumerable.Range(0, 16).Select(i => Math.Sin(i * Math.PI / 8))];

    public int Playing => _playing;

    /// <summary>
    /// Нова партія: комори рівно по краю квадрата для тих, хто грає (по колу від верху, за годинниковою), гравці —
    /// трохи всередину від своєї комори. Земля порожня, перша хвиля — за секунду гри.
    /// </summary>
    public void Reset(bool[] plays)
    {
        _playing = plays.Count(x => x);
        var k = 0;
        for (var s = 0; s < Seats; s++)
        {
            var b = Bodies[s];
            var on = s < plays.Length && plays[s];
            b.Plays = on;
            b.HasLarder = on;
            b.Want = -1;
            b.Carry = b.Gold = b.Cd = b.Stun = b.Immune = b.Knock = b.StealProg = 0;
            b.StealFrom = -1;
            b.Larder = b.Stole = b.Lost = b.Pushes = b.Spilled = b.Loads5 = b.Delivered = 0;
            if (!on) { b.X = b.Y = b.LarderX = b.LarderY = 0; continue; }
            var (lx, ly) = LarderPos(k++, Math.Max(1, _playing));
            b.LarderX = lx;
            b.LarderY = ly;
            var dx = C - lx;
            var dy = C - ly;
            var d = Math.Sqrt(dx * dx + dy * dy);
            b.X = lx + dx / d * (LarderR + 10);
            b.Y = ly + dy / d * (LarderR + 10);
            b.Face = SectorOf(Math.Atan2(dy, dx) * 180 / Math.PI);
        }
        Ground.Clear();
        _events.Clear();
        _pending.Clear();
        T = Rt = 0;
        ShakeTree = -1;
        ShakeLeft = 0;
        NextWave = TicksPerSec;
        _lastTree = -1;
        _pearId = 0;
    }

    /// <summary>k-та комора з n: промінь від центру під кутом (верх — перша), спроєктований на квадрат <see cref="LarderAt"/>.</summary>
    public static (double X, double Y) LarderPos(int k, int n)
    {
        var a = -Math.PI / 2 + 2 * Math.PI * k / n;
        // На двох — навскоси по кутах, а не верх і низ: так обидві комори однаково далеко від усіх дерев.
        if (n == 2) a += Math.PI / 4;
        var cx = Math.Cos(a);
        var cy = Math.Sin(a);
        var m = Math.Max(Math.Abs(cx), Math.Abs(cy));
        return (Math.Round(C + cx / m * LarderAt), Math.Round(C + cy / m * LarderAt));
    }

    /// <summary>Кут у градусах → найближчий сектор 0..15 (0 — праворуч, 4 — донизу).</summary>
    public static int SectorOf(double deg)
    {
        var s = (int)Math.Round(deg / 22.5, MidpointRounding.AwayFromZero) % 16;
        return s < 0 ? s + 16 : s;
    }

    public void Move(int seat, int sector)
    {
        var b = Bodies[seat];
        b.Want = sector;
        if (sector >= 0) b.Face = sector;
    }

    /// <summary>Скільки одиниць за тик іде гравець з такою ношею.</summary>
    public static double SpeedOf(int carry) => Speed * (1 - SlowPerPear * carry);

    static double Dist2(double ax, double ay, double bx, double by) => (ax - bx) * (ax - bx) + (ay - by) * (ay - by);

    /// <summary>
    /// Штовхан: найближчий у межах <see cref="PushR"/>, кого ще можна штовхнути, — відлітає й висипає ношу. Нікого
    /// поруч — штовхнув повітря (перезарядка однаково: інакше штовхан тиснули б навмання). null — вдалося, інакше відмова.
    /// </summary>
    public string? Push(int seat)
    {
        var b = Bodies[seat];
        if (b.Cd > 0) return "Ще не відсапався після штовхана";
        if (b.Stun > 0) return "Тебе самого штовхнули";
        b.Cd = PushCd;
        var best = -1;
        var bestD = PushR * PushR;
        for (var s = 0; s < Seats; s++)
        {
            var o = Bodies[s];
            if (s == seat || !o.Plays || o.Immune > 0) continue;
            var d = Dist2(b.X, b.Y, o.X, o.Y);
            if (d <= bestD) { bestD = d; best = s; }
        }
        if (best < 0) { Event(EvWhiff, seat, 0); return null; }
        var t = Bodies[best];
        b.Pushes++;
        var dx = t.X - b.X;
        var dy = t.Y - b.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) { dx = DirX[b.Face]; dy = DirY[b.Face]; len = 1; }
        t.KnockX = dx / len * KnockStep;
        t.KnockY = dy / len * KnockStep;
        t.Knock = KnockTicks;
        t.Stun = StunTicks;
        t.Immune = StunTicks + ImmuneTicks;
        t.StealProg = 0;
        t.StealFrom = -1;
        Event(EvPush, seat, best);
        Spill(best);
        return null;
    }

    /// <summary>Ноша висипається навколо: груші розлітаються на 40–100 од і 0,3 с «летять».</summary>
    void Spill(int seat)
    {
        var t = Bodies[seat];
        var n = t.Carry;
        if (n == 0) return;
        for (var i = 0; i < n; i++)
        {
            var a = rng.NextDouble() * 2 * Math.PI;
            var r = 40 + rng.NextDouble() * 60;
            Drop(t.X + Math.Cos(a) * r, t.Y + Math.Sin(a) * r, i < t.Gold, SpillLock);
        }
        t.Spilled += n;
        t.Carry = t.Gold = 0;
        Event(EvSpill, seat, n);
    }

    void Drop(double x, double y, bool gold, int lockTicks)
    {
        x = Math.Clamp(x, PearR + 4, Size - PearR - 4);
        y = Math.Clamp(y, PearR + 4, Size - PearR - 4);
        Ground.Add(new Pear { Id = ++_pearId, X = x, Y = y, Gold = gold, Lock = lockTicks });
    }

    /// <summary>Гравця не стало посеред партії: ноша висипається, тіло зникає; комора лишається — її можна красти.</summary>
    public void Drop(int seat)
    {
        Spill(seat);
        var b = Bodies[seat];
        b.Plays = false;
        b.Want = -1;
    }

    /// <summary>Відлік: світ стоїть, лише годинник іде.</summary>
    public void Idle() { T++; NewEvents(); }

    void NewEvents()
    {
        _events.Clear();
        _events.AddRange(_pending);
        _pending.Clear();
    }

    /// <summary>Один тик гри: хвилі, хода, штовхани (вже в Act), підбирання, комори.</summary>
    public void Step()
    {
        T++;
        Rt++;
        NewEvents();
        _inStep = true;
        Waves();
        for (var s = 0; s < Seats; s++)
        {
            var b = Bodies[s];
            if (!b.Plays) continue;
            if (b.Cd > 0) b.Cd--;
            if (b.Immune > 0) b.Immune--;
            if (b.Knock > 0)
            {
                b.Knock--;
                b.X += b.KnockX;
                b.Y += b.KnockY;
            }
            if (b.Stun > 0) b.Stun--;
            else if (b.Want >= 0)
            {
                var v = SpeedOf(b.Carry);
                b.X += DirX[b.Want] * v;
                b.Y += DirY[b.Want] * v;
            }
            b.X = Math.Clamp(b.X, BodyR, Size - BodyR);
            b.Y = Math.Clamp(b.Y, BodyR, Size - BodyR);
        }
        Separate();
        foreach (var p in Ground) if (p.Lock > 0) p.Lock--;
        // Спірну грушу (чи останню грушу комори) на тому самому тику бере перший в обході — тож обхід щотику
        // зсуваємо, щоб менший номер місця не вигравав такі суперечки систематично.
        for (var i = 0; i < Seats; i++)
        {
            var s = (i + T) % Seats;
            var b = Bodies[s];
            if (!b.Plays || b.Stun > 0) continue;
            Pick(s, b);
            Larders(s, b);
        }
        _inStep = false;
    }

    /// <summary>Тіла не налазять одне на одне: розводимо пари порівну (на 8 гравців — 28 пар, дешево).</summary>
    void Separate()
    {
        const double min = 2 * BodyR;
        for (var i = 0; i < Seats; i++)
        {
            var a = Bodies[i];
            if (!a.Plays) continue;
            for (var j = i + 1; j < Seats; j++)
            {
                var b = Bodies[j];
                if (!b.Plays) continue;
                var dx = b.X - a.X;
                var dy = b.Y - a.Y;
                var d2 = dx * dx + dy * dy;
                if (d2 >= min * min) continue;
                var d = Math.Sqrt(d2);
                if (d < 1e-6) { dx = 1; dy = 0; d = 1; }
                var push = (min - d) / 2;
                a.X = Math.Clamp(a.X - dx / d * push, BodyR, Size - BodyR);
                a.Y = Math.Clamp(a.Y - dy / d * push, BodyR, Size - BodyR);
                b.X = Math.Clamp(b.X + dx / d * push, BodyR, Size - BodyR);
                b.Y = Math.Clamp(b.Y + dy / d * push, BodyR, Size - BodyR);
            }
        }
    }

    void Pick(int s, Body b)
    {
        const double reach = (BodyR + PearR) * (BodyR + PearR);
        for (var i = 0; i < Ground.Count && b.Carry < CarryMax; i++)
        {
            var p = Ground[i];
            if (p.Lock > 0 || Dist2(b.X, b.Y, p.X, p.Y) > reach) continue;
            b.Carry++;
            if (p.Gold) b.Gold++;
            Event(EvPick, s, p.Id);
            Ground.RemoveAt(i--);
        }
    }

    /// <summary>Своя комора — скинути все разом; чужа — стояти секунду за кожну грушу.</summary>
    void Larders(int s, Body b)
    {
        const double zone = LarderR * LarderR;
        if (Dist2(b.X, b.Y, b.LarderX, b.LarderY) <= zone)
        {
            if (b.Carry > 0)
            {
                var value = b.Carry + b.Gold * (GoldValue - 1);
                if (b.Carry >= CarryMax) b.Loads5++;
                b.Larder += value;
                b.Delivered += value;
                Event(EvDrop, s, value);
                b.Carry = b.Gold = 0;
            }
            b.StealProg = 0;
            b.StealFrom = -1;
            return;
        }
        var from = -1;
        for (var o = 0; o < Seats; o++)
        {
            if (o == s) continue;
            var ob = Bodies[o];
            // Комора того, хто встав, лишається на місці — її теж можна обчистити.
            if (!ob.HasLarder) continue;
            if (Dist2(b.X, b.Y, ob.LarderX, ob.LarderY) <= zone) { from = o; break; }
        }
        // Штовхнутий не краде, поки недоторканний (ще 1 с після приголомшення): інакше штовхан, що перезаряджається 3 с,
        // не боронив би комору — злодій за ці 3 с устигав узяти дві груші.
        if (from < 0 || b.Carry >= CarryMax || Bodies[from].Larder <= 0 || b.Immune > 0)
        {
            b.StealProg = 0;
            b.StealFrom = from >= 0 && b.Carry < CarryMax ? from : -1;
            return;
        }
        if (b.StealFrom != from) b.StealProg = 0;
        b.StealFrom = from;
        if (++b.StealProg < StealTicks) return;
        b.StealProg = 0;
        var victim = Bodies[from];
        victim.Larder--;
        victim.Lost++;
        b.Carry++;
        b.Stole++;
        Event(EvSteal, s, from);
    }

    /// <summary>
    /// Хвилі: дерево трусить <see cref="ShakeTicks"/>, потім сипле 3 + n/2 + (0..2) груш на 40–140 од навколо (не
    /// більше, ніж влазить під <see cref="GroundMax"/>). Дерево — випадкове, але не те саме двічі поспіль.
    /// </summary>
    void Waves()
    {
        if (ShakeTree >= 0)
        {
            if (--ShakeLeft > 0) return;
            var (tx, ty) = Trees[ShakeTree];
            var n = Math.Min(3 + _playing / 2 + rng.Next(3), GroundMax - Ground.Count);
            for (var i = 0; i < n; i++)
            {
                var a = rng.NextDouble() * 2 * Math.PI;
                var r = 40 + rng.NextDouble() * 100;
                Drop(tx + Math.Cos(a) * r, ty + Math.Sin(a) * r, rng.Next(100) < GoldPct, 0);
            }
            Event(EvFall, ShakeTree, Math.Max(0, n));
            ShakeTree = -1;
            NextWave = WaveMin + rng.Next(WaveMax - WaveMin + 1);
            return;
        }
        if (--NextWave > 0) return;
        if (Ground.Count >= GroundMax) { NextWave = TicksPerSec; return; }
        var last = _lastTree;
        var t = rng.Next(Trees.Length - (last >= 0 ? 1 : 0));
        if (last >= 0 && t >= last) t++;
        _lastTree = t;
        ShakeTree = t;
        ShakeLeft = ShakeTicks;
        Event(EvShake, t, 0);
    }

    int _lastTree = -1;

    public void Event(int type, int a, int b) => (_inStep ? _events : _pending).Add([type, a, b]);

    /// <summary>Події цього тика — новий масив (кадр серіалізують уже поза замком кімнати).</summary>
    public int[][] Events() => [.. _events.Select(e => (int[])e.Clone())];

    public static double DirXOf(int sector) => DirX[sector];
    public static double DirYOf(int sector) => DirY[sector];
}
