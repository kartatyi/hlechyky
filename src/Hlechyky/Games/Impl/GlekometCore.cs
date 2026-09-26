namespace Hlechyky.Games.Impl;

/// <summary>
/// Снаряд у коморі: скільки його на партію, як б'є і як сильно його зносить вітер. Порядок у
/// <see cref="GlekometCore.Weapons"/> сталий — клієнт знає снаряди за тим самим індексом.
/// </summary>
/// <param name="Stock">Запас на партію; -1 — без ліку (глек).</param>
/// <param name="Damage">Шкода при прямому влучанні (D).</param>
/// <param name="Crater">Радіус вирви (r); 0 — землю не рве.</param>
/// <param name="Reach">Радіус шкоди (R): далі — нуль, ближче — лінійно до D.</param>
/// <param name="WindK">Яку частку вітру відчуває в польоті.</param>
public sealed record GlekometWeapon(string Key, string Name, string Icon, int Stock, int Damage, int Crater, int Reach, double WindK);

/// <summary>
/// Снаряд у польоті. Структура в масиві на вісім слотів — щоб підкрок не народжував жодного об'єкта.
/// Вік рахуємо цілими підкроками (5 мс), а не сумою double: «вісім секунд» тоді рівно 1600, без хвостів.
/// </summary>
public struct GlekometShell
{
    public bool Alive;
    /// <summary>Індекс снаряда в <see cref="GlekometCore.Weapons"/>.</summary>
    public int Kind;
    public double X, Y, Vx, Vy;
    /// <summary>Скільки підкроків уже в повітрі.</summary>
    public int Steps;
    /// <summary>Скалка розсипного: вона вже не розколюється.</summary>
    public bool Split;
    /// <summary>Номер підкроку, у якому народився: скалки, що з'явились посеред підкроку, рушають із наступного.</summary>
    public long Born;
}

/// <summary>Хата на колесах одного місця. Координати цілі: x — центр, y — рівень підлоги (поверхня під центром).</summary>
public sealed class GlekometHut
{
    public int X, Y;
    public int Hp;
    public bool Alive;
    /// <summary>Хата брала участь у партії (за місцем хтось сидів на старті).</summary>
    public bool Plays;
    /// <summary>Команда: парність місця.</summary>
    public int Team;
    /// <summary>Скільки ще ходів дошкулятиме хрін.</summary>
    public int Poison;
    /// <summary>Хто отруїв (для статистики); -1 — ніхто.</summary>
    public int PoisonBy = -1;
    public int Fuel;
    /// <summary>Чому руїна: hit, fall, drown, afk, left, poison; порожньо — ціла або порожнє місце.</summary>
    public string Reason = "";
}

/// <summary>
/// Глекомети без кімнати: карта висот, хати, снаряди, вирви, копи, лелека, хрін, падіння й вода (spec
/// glekomet.md §2). Правила черги й кінця партії — у <see cref="Glekomet"/>, а тут лише фізика села, тож тести
/// ставлять хати куди треба й стріляють напряму. Гарячий цикл (<see cref="Step"/>) не алокує нічого:
/// снаряди — масив структур, вибухи й змінені колонки — масиви, що скидаються на початку тика.
/// </summary>
public sealed class GlekometCore
{
    public const int W = 1000, Hgt = 500, Cols = 250, ColW = 4, Seats = 6;
    /// <summary>Хата: півширини, висота, точка вильоту над підлогою.</summary>
    public const int HutHalf = 20, HutH = 34, Launch = 40;
    public const int MinX = 30, MaxX = 970;
    public const double G = 240, VPerPower = 4.8, WindAcc = 10, Dt = 0.005;
    public const int SubSteps = 8;
    /// <summary>Вісім секунд польоту — і снаряд «розчинився в хмарах».</summary>
    public const int MaxAgeSteps = 1600;
    /// <summary>Розсипний розколюється не раніше за чверть секунди (50 підкроків).</summary>
    public const int SplitSteps = 50;
    public const int FuelMax = 120, FuelCost = 8, MoveStep = 8, MaxClimb = 12, SafeDrop = 24;
    public const int WaterStart = 20, WaterRise = 15, WaterTop = 400;
    public const int HayW = 30, HayH = 26, PoisonTurns = 3, PoisonDmg = 8, StorkReach = 60, HutGap = 40;
    public const int MaxShells = 8, MaxEx = 16;
    public const int HMin = 60, HMax = 340, RampCols = 8;

    public const int Pot = 0, Shards = 1, Varenyk = 2, Hay = 3, Stork = 4, Khrin = 5;
    public const int ExSplash = 6, ExOut = 7, ExCloud = 8;

    /// <summary>Комора. Порядок і ключі — ті самі, що в клієнті (glekomet.js WEAPONS).</summary>
    public static readonly GlekometWeapon[] Weapons =
    [
        new("pot", "Глек", "🏺", -1, 35, 22, 36, 1.0),
        new("shards", "Розсипний глек", "💥", 2, 16, 14, 24, 1.0),
        new("varenyk", "Вареник-бомба", "🥟", 1, 55, 40, 56, 0.4),
        new("hay", "Копа сіна", "🌾", 2, 0, 0, 0, 1.0),
        new("stork", "Лелека", "🕊", 1, 0, 0, 0, 1.0),
        new("khrin", "Хрін", "🌿", 2, 12, 12, 40, 1.0),
    ];

    /// <summary>Імена подій у кадрі (<c>ex</c>): шість снарядів, бризки, виліт за край, хмари.</summary>
    public static readonly string[] ExNames = ["pot", "shards", "varenyk", "hay", "stork", "khrin", "splash", "out", "cloud"];

    /// <summary>Розліт скалок від верхівки: додатки до швидкості розсипного глека.</summary>
    static readonly int[] ShardVx = [-90, -30, 30, 90], ShardVy = [30, 50, 50, 30];

    readonly Random _rng;

    /// <summary>Висоти поверхні по колонках (по 4 u): колонка c накриває x ∈ [4c, 4c+4).</summary>
    public readonly int[] H = new int[Cols];
    public readonly GlekometHut[] Huts = new GlekometHut[Seats];
    public readonly GlekometShell[] Shells = new GlekometShell[MaxShells];
    public int Water = WaterStart;
    /// <summary>Вітер цього ходу: -5..5, додатний — праворуч.</summary>
    public int Wind;
    /// <summary>Чи граємо командами — тоді «своя» шкода — ще й по хаті союзника.</summary>
    public bool Teams;
    long _sub;

    // ---------- позначки тика: що змінилось (для кадру); скидає ClearMarks ----------
    public int ExCount;
    public readonly int[] ExX = new int[MaxEx], ExY = new int[MaxEx], ExR = new int[MaxEx], ExK = new int[MaxEx];
    public readonly bool[] Dirty = new bool[Cols];
    public int DirtyLo = Cols, DirtyHi = -1;
    /// <summary>Хати, що посунулись (бітова маска місць).</summary>
    public int MovedMask;
    public bool HpChanged;

    // ---------- підсумок пострілу; скидає Fire ----------
    public int ShotBy = -1, ShotW, ShotFromX;
    /// <summary>Шкода від вибухів цього пострілу по хатах.</summary>
    public readonly int[] ShotDmg = new int[Seats];
    /// <summary>Шкода від падіння за цей постріл (нікому не зараховується).</summary>
    public readonly int[] ShotFall = new int[Seats];
    /// <summary>Маски: пряме влучання, отруєні, вибули.</summary>
    public int ShotDirect, ShotPoisoned, ShotDied;
    /// <summary>Скільки снарядів цього пострілу впало в ставок / вилетіло за край / розчинилось / вибухнуло.</summary>
    public int ShotSplash, ShotOut, ShotCloud, ShotBooms;
    /// <summary>Скільки кіп виросло.</summary>
    public int ShotHay;
    /// <summary>Лелека: 1 — переніс хату, -1 — змарновано, 0 — не летіла.</summary>
    public int ShotStork;

    /// <summary>Порядок вибування (для рядка Журналу: останній вибулий — першим після переможця).</summary>
    public readonly int[] OutOrder = new int[Seats];
    public int OutCount;

    public GlekometCore(Random rng)
    {
        _rng = rng;
        for (var i = 0; i < Seats; i++) Huts[i] = new GlekometHut { Team = i % 2 };
    }

    public static int Col(int x) => x < 0 ? 0 : x >= W ? Cols - 1 : x / ColW;

    public static int Col(double x) => x < 0 ? 0 : x >= W ? Cols - 1 : (int)(x / ColW);

    /// <summary>Висота поверхні під точкою x.</summary>
    public int Ground(int x) => H[Col(x)];

    /// <summary>Цілочисельний корінь без похибок double: найбільше s, для якого s² ≤ n.</summary>
    public static int Isqrt(int n)
    {
        if (n <= 0) return 0;
        var s = (int)Math.Sqrt(n);
        while (s * s > n) s--;
        while ((s + 1) * (s + 1) <= n) s++;
        return s;
    }

    public int LiveShells
    {
        get
        {
            var n = 0;
            for (var i = 0; i < MaxShells; i++) if (Shells[i].Alive) n++;
            return n;
        }
    }

    public int AliveCount
    {
        get
        {
            var n = 0;
            for (var i = 0; i < Seats; i++) if (Huts[i].Alive) n++;
            return n;
        }
    }

    // =============================================================================================
    // Мапа
    // =============================================================================================

    /// <summary>
    /// Нове село (spec §2.2), лише з <c>Rng</c> і рівно в такому порядку викликів: база, три довгі хвилі, дві
    /// короткі, пагорб-акцент, зсуви слотів, перемішування. Під кожною хатою — рівний майданчик ±24 u.
    /// </summary>
    public void Generate(bool[] plays)
    {
        var n = 0;
        for (var i = 0; i < Seats; i++) if (plays[i]) n++;

        // Хвилі лагідніші, ніж у першому задумі spec (там схили виходили 2–6 u на u — скелі, а не пагорби):
        // медіана схилу ≈ 0,5, дев'яносто відсотків — до 1,25, тож хата здебільшого може з'їхати й виїхати.
        var bottom = 150 + _rng.Next(51);
        Span<double> ph = stackalloc double[5], per = stackalloc double[5], amp = stackalloc double[5];
        for (var k = 0; k < 3; k++)
        {
            ph[k] = _rng.NextDouble() * 2 * Math.PI;
            per[k] = 300 + _rng.NextDouble() * 250;
            amp[k] = 22 + _rng.NextDouble() * 28;
        }
        for (var k = 3; k < 5; k++)
        {
            ph[k] = _rng.NextDouble() * 2 * Math.PI;
            per[k] = 70 + _rng.NextDouble() * 60;
            amp[k] = 3 + _rng.NextDouble() * 4;
        }
        var cx = 200 + _rng.NextDouble() * 600;
        var hamp = 40 + _rng.NextDouble() * 50;
        var wid = 100 + _rng.NextDouble() * 80;
        for (var c = 0; c < Cols; c++)
        {
            double x = c * ColW + 2, h = bottom;
            for (var k = 0; k < 5; k++) h += amp[k] * Math.Sin(2 * Math.PI * x / per[k] + ph[k]);
            var d = (x - cx) / wid;
            h += hamp * Math.Exp(-d * d);
            H[c] = (int)Math.Clamp(h, HMin, HMax);
        }

        Span<int> slots = stackalloc int[Math.Max(1, n)];
        for (var i = 0; i < n; i++)
        {
            var at = n == 1 ? 500 : (int)Math.Round(90 + i * (820.0 / (n - 1)), MidpointRounding.AwayFromZero);
            slots[i] = Math.Clamp(at + _rng.Next(51) - 25, MinX, MaxX);
        }
        for (var i = n - 1; i >= 1; i--)
        {
            var j = _rng.Next(i + 1);
            (slots[i], slots[j]) = (slots[j], slots[i]);
        }

        Water = WaterStart;
        Wind = 0;
        OutCount = 0;
        ClearShells();
        var next = 0;
        Span<bool> pad = stackalloc bool[Cols];
        for (var s = 0; s < Seats; s++)
        {
            var hut = Huts[s];
            Reset(hut, s);
            if (!plays[s]) continue;
            hut.Plays = true;
            hut.Alive = true;
            hut.Hp = 100;
            hut.Fuel = FuelMax;
            hut.X = slots[next++];
            Level(hut.X, pad);
        }
        // Майданчик на схилі давав урвища по боках (до 75 u в одній колонці) — з'їжджаємо до нього пандусом.
        for (var s = 0; s < Seats; s++)
            if (Huts[s].Plays) Ramp(Huts[s].X, pad);
        for (var s = 0; s < Seats; s++)
            if (Huts[s].Plays) Huts[s].Y = Ground(Huts[s].X);
    }

    /// <summary>Рівний майданчик під хатою: колонки x±24 отримують цілочисельне середнє своїх висот.</summary>
    void Level(int x, Span<bool> pad)
    {
        int lo = Col(x - 24), hi = Col(x + 24), sum = 0;
        for (var c = lo; c <= hi; c++) sum += H[c];
        var avg = sum / (hi - lo + 1);
        for (var c = lo; c <= hi; c++)
        {
            H[c] = avg;
            pad[c] = true;
        }
    }

    /// <summary>
    /// Пандус по 8 колонок (32 u) з обох боків майданчика: висота плавно переходить від рівня хати до природної.
    /// Чужі майданчики не чіпаємо — хата має стояти рівно.
    /// </summary>
    void Ramp(int x, Span<bool> pad)
    {
        int lo = Col(x - 24), hi = Col(x + 24), level = H[lo];
        for (var d = 1; d <= RampCols; d++)
        {
            for (var side = 0; side < 2; side++)
            {
                var c = side == 0 ? lo - d : hi + d;
                if (c < 0 || c >= Cols || pad[c]) continue;
                H[c] = level + (int)Math.Round((H[c] - level) * d / (double)(RampCols + 1), MidpointRounding.AwayFromZero);
            }
        }
    }

    static void Reset(GlekometHut hut, int seat)
    {
        hut.X = 0;
        hut.Y = 0;
        hut.Hp = 0;
        hut.Alive = false;
        hut.Plays = false;
        hut.Team = seat % 2;
        hut.Poison = 0;
        hut.PoisonBy = -1;
        hut.Fuel = 0;
        hut.Reason = "";
    }

    /// <summary>Для тестів: рівне поле заданої висоти, без жодної хати.</summary>
    public void Flat(int height)
    {
        Array.Fill(H, height);
        Water = WaterStart;
        Wind = 0;
        OutCount = 0;
        ClearShells();
        for (var s = 0; s < Seats; s++) Reset(Huts[s], s);
    }

    /// <summary>Для тестів і лобі: поставити хату місця на x (на поверхню).</summary>
    public GlekometHut Place(int seat, int x)
    {
        var hut = Huts[seat];
        hut.Plays = true;
        hut.Alive = true;
        hut.Hp = 100;
        hut.Fuel = FuelMax;
        hut.Reason = "";
        hut.Poison = 0;
        hut.PoisonBy = -1;
        hut.X = x;
        hut.Y = Ground(x);
        return hut;
    }

    void ClearShells()
    {
        for (var i = 0; i < MaxShells; i++) Shells[i] = default;
    }

    // =============================================================================================
    // Позначки тика
    // =============================================================================================

    /// <summary>Початок тика: забути вибухи, змінені колонки й рухи минулого кадру.</summary>
    public void ClearMarks()
    {
        ExCount = 0;
        if (DirtyHi >= 0)
            for (var c = DirtyLo; c <= DirtyHi; c++) Dirty[c] = false;
        DirtyLo = Cols;
        DirtyHi = -1;
        MovedMask = 0;
        HpChanged = false;
    }

    void Mark(int c)
    {
        Dirty[c] = true;
        if (c < DirtyLo) DirtyLo = c;
        if (c > DirtyHi) DirtyHi = c;
    }

    void Event(int x, int y, int r, int kind)
    {
        if (ExCount >= MaxEx) return;
        ExX[ExCount] = x;
        ExY[ExCount] = y;
        ExR[ExCount] = r;
        ExK[ExCount] = kind;
        ExCount++;
    }

    // =============================================================================================
    // Постріл і політ
    // =============================================================================================

    /// <summary>Постріл із хати місця: кут у градусах (0 — праворуч, 90 — угору), сила 5..100, снаряд w.</summary>
    public void Fire(int seat, int a, int p, int w)
    {
        var hut = Huts[seat];
        BeginShot(seat, w);
        var v = VPerPower * p;
        var rad = a * Math.PI / 180;
        Spawn(w, hut.X, hut.Y + Launch, v * Math.Cos(rad), v * Math.Sin(rad), false);
    }

    /// <summary>Новий постріл місця: підсумок попереднього забуваємо.</summary>
    public void BeginShot(int seat, int w)
    {
        ShotBy = seat;
        ShotW = w;
        ShotFromX = Huts[seat].X;
        Array.Clear(ShotDmg);
        Array.Clear(ShotFall);
        ShotDirect = ShotPoisoned = ShotDied = 0;
        ShotSplash = ShotOut = ShotCloud = ShotBooms = ShotHay = ShotStork = 0;
    }

    /// <summary>Те саме з іншим вітром (тести).</summary>
    public void Fire(int seat, int a, int p, int w, int wind)
    {
        Wind = wind;
        Fire(seat, a, p, w);
    }

    /// <summary>Снаряд у вільний слот. Повертає індекс або -1 (слотів вісім — більше чотирьох скалок не буває).</summary>
    public int Spawn(int kind, double x, double y, double vx, double vy, bool split)
    {
        for (var i = 0; i < MaxShells; i++)
        {
            if (Shells[i].Alive) continue;
            Shells[i] = new GlekometShell { Alive = true, Kind = kind, X = x, Y = y, Vx = vx, Vy = vy, Split = split, Born = _sub };
            return i;
        }
        return -1;
    }

    /// <summary>
    /// Один тик: вісім підкроків по 5 мс для всіх живих снарядів (вибухи — одразу, наступна скалка бачить уже
    /// нову землю), потім осідання хат, падіння й вода. Нуль алокацій.
    /// </summary>
    public void Step()
    {
        for (var k = 0; k < SubSteps; k++)
        {
            _sub++;
            for (var i = 0; i < MaxShells; i++)
                if (Shells[i].Alive && Shells[i].Born != _sub) Advance(ref Shells[i]);
        }
        Settle();
    }

    void Advance(ref GlekometShell s)
    {
        var ax = Wind * WindAcc * Weapons[s.Kind].WindK;
        s.Vx += ax * Dt;
        s.Vy -= G * Dt;
        s.X += s.Vx * Dt;
        s.Y += s.Vy * Dt;
        s.Steps++;

        if (s.X < 0 || s.X > W)
        {
            s.Alive = false;
            ShotOut++;
            Event(s.X < 0 ? 0 : W, Round(s.Y), 0, ExOut);
            return;
        }
        if (s.Y < Water)
        {
            s.Alive = false;
            ShotSplash++;
            Event(Round(s.X), Water, 0, ExSplash);
            return;
        }
        var hit = HutAt(s.X, s.Y);
        if (hit >= 0)
        {
            s.Alive = false;
            Explode(s.Kind, Round(s.X), Round(s.Y), hit);
            return;
        }
        var ground = H[Col(s.X)];
        if (s.Y <= ground)
        {
            s.Alive = false;
            Explode(s.Kind, Round(s.X), ground, -1);
            return;
        }
        if (s.Kind == Shards && !s.Split && s.Steps >= SplitSteps && s.Vy <= 0)
        {
            s.Alive = false;
            double x = s.X, y = s.Y, vx = s.Vx, vy = s.Vy;
            for (var j = 0; j < 4; j++) Spawn(Shards, x, y, vx + ShardVx[j], vy + ShardVy[j], true);
            return;
        }
        if (s.Steps >= MaxAgeSteps)
        {
            s.Alive = false;
            ShotCloud++;
            Event(Round(s.X), Math.Min(Hgt, Round(s.Y)), 0, ExCloud);
        }
    }

    static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    /// <summary>Жива хата, у хітбокс якої влетіла точка; -1 — жодна.</summary>
    int HutAt(double x, double y)
    {
        for (var i = 0; i < Seats; i++)
        {
            var h = Huts[i];
            if (!h.Alive) continue;
            if (x >= h.X - HutHalf && x <= h.X + HutHalf && y >= h.Y && y <= h.Y + HutH) return i;
        }
        return -1;
    }

    /// <summary>Відстань від точки до найближчої точки хітбокса хати.</summary>
    public static double Distance(GlekometHut h, double x, double y)
    {
        var dx = x < h.X - HutHalf ? h.X - HutHalf - x : x > h.X + HutHalf ? x - (h.X + HutHalf) : 0;
        var dy = y < h.Y ? h.Y - y : y > h.Y + HutH ? y - (h.Y + HutH) : 0;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Шкода від вибуху з D і R на відстані d (spec §2.7): лінійно до нуля на R.</summary>
    public static int Falloff(int damage, int reach, double d) =>
        d >= reach ? 0 : (int)Math.Round(damage * (1 - d / reach), MidpointRounding.AwayFromZero);

    /// <summary>Вибух снаряда kind у точці (ex, ey); direct — хата, у яку влетіли (-1 — земля).</summary>
    public void Explode(int kind, int ex, int ey, int direct)
    {
        var wpn = Weapons[kind];
        switch (kind)
        {
            case Hay:
                Mound(ex);
                ShotHay++;
                Event(ex, ey, HayW, Hay);
                return;
            case Stork:
                Land(ex);
                Event(ex, ey, 0, Stork);
                return;
        }
        ShotBooms++;
        Crater(ex, ey, wpn.Crater);
        for (var i = 0; i < Seats; i++)
        {
            var h = Huts[i];
            if (!h.Alive) continue;
            var d = i == direct ? 0 : Distance(h, ex, ey);
            var dmg = i == direct ? wpn.Damage : Falloff(wpn.Damage, wpn.Reach, d);
            if (i == direct) ShotDirect |= 1 << i;
            if (kind == Khrin && d < wpn.Reach)
            {
                h.Poison = PoisonTurns;
                h.PoisonBy = ShotBy;
                ShotPoisoned |= 1 << i;
            }
            if (dmg > 0) Hurt(i, dmg);
        }
        Event(ex, ey, wpn.Crater, kind);
    }

    /// <summary>Шкода рахується справжня: хаті з 10 здоров'я глек знімає 10, а не 35.</summary>
    void Hurt(int seat, int dmg)
    {
        var h = Huts[seat];
        dmg = Math.Min(dmg, h.Hp);
        h.Hp -= dmg;
        ShotDmg[seat] += dmg;
        HpChanged = true;
        if (h.Hp == 0) Kill(seat, "hit");
    }

    /// <summary>Хата стає руїною з причиною; стоїть на місці до кінця партії.</summary>
    public void Kill(int seat, string reason)
    {
        var h = Huts[seat];
        if (!h.Alive) return;
        h.Alive = false;
        h.Reason = reason;
        h.Poison = 0;
        HpChanged = true;
        ShotDied |= 1 << seat;
        if (OutCount < Seats) OutOrder[OutCount++] = seat;
    }

    /// <summary>Вирва радіуса r по хорді з цілочисельним коренем: колонка втрачає те, що між ey−dy і ey+dy.</summary>
    public void Crater(int ex, int ey, int r)
    {
        if (r <= 0) return;
        int lo = Col(ex - r), hi = Col(ex + r);
        for (var c = lo; c <= hi; c++)
        {
            var dx = c * ColW + 2 - ex;
            if (dx >= r || dx <= -r) continue;
            var dy = Isqrt(r * r - dx * dx);
            var top = Math.Min(H[c], ey + dy);
            var bot = Math.Max(0, ey - dy);
            var removed = top - bot;
            if (removed <= 0) continue;
            H[c] -= removed;
            Mark(c);
        }
    }

    /// <summary>Копа сіна: півеліпс 60 × 26 над поверхнею в точці падіння; земля лише росте.</summary>
    public void Mound(int mx)
    {
        var bottom = Ground(mx);
        int lo = Col(mx - HayW), hi = Col(mx + HayW);
        for (var c = lo; c <= hi; c++)
        {
            var dx = c * ColW + 2 - mx;
            if (dx >= HayW || dx <= -HayW) continue;
            var dy = Isqrt(HayH * HayH - dx * dx * HayH * HayH / (HayW * HayW));
            var top = bottom + dy;
            if (top <= H[c]) continue;
            H[c] = Math.Min(top, Hgt);
            Mark(c);
        }
    }

    /// <summary>Лелека несе хату того, хто стріляв, у найближчу суху й вільну точку біля падіння (±60 u).</summary>
    void Land(int sx)
    {
        var seat = ShotBy;
        if (seat < 0 || !Huts[seat].Alive) { ShotStork = -1; return; }
        var tx = StorkSpot(seat, sx);
        if (tx < 0) { ShotStork = -1; return; }
        var hut = Huts[seat];
        hut.X = tx;
        hut.Y = Ground(tx);
        MovedMask |= 1 << seat;
        ShotStork = 1;
    }

    /// <summary>Де лелека може сісти: спершу sx, далі ±4, ±8… до ±60; -1 — ніде.</summary>
    public int StorkSpot(int seat, int sx)
    {
        for (var off = 0; off <= StorkReach; off += ColW)
        {
            for (var sign = -1; sign <= 1; sign += 2)
            {
                if (off == 0 && sign > 0) continue;
                var tx = sx + sign * off;
                if (tx < MinX || tx > MaxX) continue;
                if (Ground(tx) <= Water) continue;
                if (Near(seat, tx)) continue;
                return tx;
            }
        }
        return -1;
    }

    /// <summary>Чи стоїть інша жива хата ближче за 40 u від x.</summary>
    bool Near(int seat, int x)
    {
        for (var i = 0; i < Seats; i++)
            if (i != seat && Huts[i].Alive && Math.Abs(Huts[i].X - x) < HutGap) return true;
        return false;
    }

    // =============================================================================================
    // Осідання, падіння, вода, рух
    // =============================================================================================

    /// <summary>Кожна жива хата — на поверхню під собою; падіння понад 24 u б'є (drop−24)/2; нижче води — тоне.</summary>
    public void Settle()
    {
        for (var i = 0; i < Seats; i++)
        {
            var h = Huts[i];
            if (!h.Alive)
            {
                // Руїна не висить над вирвою: осідає разом із землею, але вже без шкоди.
                if (h.Plays && h.Y != Ground(h.X)) { h.Y = Ground(h.X); MovedMask |= 1 << i; }
                continue;
            }
            var ground = Ground(h.X);
            if (ground != h.Y)
            {
                var drop = h.Y - ground;
                h.Y = ground;
                MovedMask |= 1 << i;
                if (drop > SafeDrop) Fall(i, (drop - SafeDrop) / 2);
            }
            if (h.Alive && h.Y < Water) Kill(i, "drown");
        }
    }

    void Fall(int seat, int dmg)
    {
        var h = Huts[seat];
        dmg = Math.Min(dmg, h.Hp);
        if (dmg <= 0) return;
        h.Hp -= dmg;
        ShotFall[seat] += dmg;
        HpChanged = true;
        if (h.Hp == 0) Kill(seat, "fall");
    }

    /// <summary>
    /// На скільки підступає вода за коло, коли живих хат <paramref name="alive"/>: 15 на шістьох і швидше, що менше
    /// лишилось (4 → 22, 3 → 30, 2 → 45). Кожну хату за коло б'ють приблизно раз, хоч скільки за столом, тож дуель
    /// тяглась би стільки ж кіл, як базар на шістьох, — у плейтесті двоє грали 16 кіл. Так кінцівка стискається.
    /// </summary>
    public static int WaterRiseFor(int alive) => WaterRise * Seats / Math.Max(2, alive);

    /// <summary>Вода на початку кола: +<paramref name="rise"/> (стеля 400), і всі, хто нижче, тонуть.</summary>
    public void RaiseWater(int rise = WaterRise)
    {
        Water = Math.Min(WaterTop, Water + rise);
        ShotDied = 0;
        for (var i = 0; i < Seats; i++)
            if (Huts[i].Alive && Huts[i].Y < Water) Kill(i, "drown");
    }

    /// <summary>
    /// Крок хати на 8 u за 8 пального. null — посунулась (можливо, впала чи втопилась — див. <see cref="ShotFall"/>,
    /// <see cref="ShotDied"/>); інакше — текст відмови (spec §3).
    /// </summary>
    public string? Move(int seat, int dir)
    {
        var h = Huts[seat];
        if (dir is not (-1 or 1)) return "Такого напрямку нема";
        if (h.Fuel < FuelCost) return "Пальне скінчилось";
        var nx = h.X + MoveStep * dir;
        if (nx < MinX || nx > MaxX) return "Далі — край села";
        if (Ground(nx) - Ground(h.X) > MaxClimb) return "Туди не заїхати — крутий схил";
        if (Near(seat, nx)) return "Там уже стоїть хата";
        Array.Clear(ShotFall);
        ShotDied = 0;
        h.Fuel -= FuelCost;
        h.X = nx;
        MovedMask |= 1 << seat;
        var ground = Ground(nx);
        var drop = h.Y - ground;
        h.Y = ground;
        if (drop > SafeDrop) Fall(seat, (drop - SafeDrop) / 2);
        if (h.Alive && h.Y < Water) Kill(seat, "drown");
        return null;
    }
}
