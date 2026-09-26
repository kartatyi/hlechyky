namespace Hlechyky.Games.Impl;

/// <summary>
/// Поверхні клітинок траси: опір, зчеплення й розгін (spec §5.3). Коди — індекси в таблицях
/// <see cref="Drag"/>/<see cref="Grip"/>/<see cref="Acc"/>; клієнт (<c>rally.js</c>) має ті самі числа.
/// </summary>
public static class RallySurface
{
    public const int Road = 0, Puddle = 1, Ice = 2, Grass = 3, Corn = 4, Mud = 5, Oil = 6, Boost = 7, Ramp = 8, Hay = 9;
    public const int Fence = 10, Tree = 11, House = 12, Water = 13, Unknown = 255;

    //                                 =    ~    *    .    c    M    o    +    J    H    #    T    D    W
    public static readonly int[] Drag = [16, 16, 12, 32, 32, 64, 16, 16, 16, 16, 16, 16, 16, 16];
    public static readonly int[] Grip = [110, 40, 14, 70, 70, 90, 110, 110, 110, 110, 110, 110, 110, 110];
    public static readonly int[] Acc = [56, 56, 44, 28, 28, 28, 56, 56, 56, 56, 56, 56, 56, 56];

    public static bool IsWall(int code) => code is >= Fence and <= Water;

    public static int Of(char c) => c switch
    {
        '=' => Road, '~' => Puddle, '*' => Ice, '.' => Grass, 'c' => Corn, 'M' => Mud, 'o' => Oil,
        '+' => Boost, 'J' => Ramp, 'H' => Hay, '#' => Fence, 'T' => Tree, 'D' => House, 'W' => Water,
        _ => Unknown,
    };
}

/// <summary>Одна машина: стан симуляції плюс кола й таймери. Усе — цілі числа (spec §5.2).</summary>
public sealed class RallyCar
{
    public bool Present, Ghost;
    /// <summary>Центр (sub = 1/64 u), курс 0..1023, швидкість уперед і вбік у системі машини (sub/тик).</summary>
    public int X, Y, A, VF, VL;
    /// <summary>Чинна маска керування: 1 ліворуч, 2 праворуч, 4 газ, 8 гальмо, 16 ручник.</summary>
    public int Mask;
    /// <summary>Кільце запланованих масок за тиком (t % 16); −1 — нема.</summary>
    public readonly int[] Sched = new int[16];
    /// <summary>Запізнення останнього вводу в тиках (≤ 0 вчасно, &gt; 0 пізно) і тик, на який його просили.</summary>
    public int Lt, It;
    public int Cell, Lap, Next, Fin, FinishMs, LapStartMs, BestMs, LastMs;
    public int Air, Oil, Stall, BoostT, BoostCd, ResetCd, HornCd;
    /// <summary>Події тика (біти spec §4.1) і ті, що прийшли з дій між тиками (гудок, повернення).</summary>
    public int Ev, PendEv;
    public int PX, PY, Slot;
    public string Car = "traktor";
    public int VX, VY;
    public bool Hit;

    public RallyCar() => Array.Fill(Sched, -1);

    /// <summary>Таймери одним числом для кадру: air | oil&lt;&lt;4 | stall&lt;&lt;9 | boostT&lt;&lt;14 | boostCd&lt;&lt;19.</summary>
    public int Timers => Air | (Oil << 4) | (Stall << 9) | (BoostT << 14) | (BoostCd << 19);
}

/// <summary>
/// Детермінована симуляція ралі: лише цілі числа, таблиці синусів, усічення й зсуви (spec §5). Той самий код
/// переписано в <c>web/games/rally.js</c> (<c>RallySim</c>) — рядок у рядок, щоб клієнт передбачав свою
/// машину рівно так, як її потім порахує сервер. Порядок операцій тут — частина контракту: не міняти
/// без клієнта й журналів паритету (<c>tests/…/RallyReplays</c>).
/// </summary>
public sealed class RallyCore
{
    public const int Seats = 6;
    public const int TickMs = 40, CountTicks = 75, TimeoutTicks = 500, MaxRaceTicks = 6000;
    public const int WorldW = RallyTrack.Cols * RallyTrack.CellSub, WorldH = RallyTrack.Rows * RallyTrack.CellSub;
    public const int RWall = 640, RCar = 768, RHay = 896;
    public const int Brake = 58, Rev = 19, MaxRev = 320, Turn = 28, VTurn = 256;
    /// <summary>Опір коченню: швидкість, меншу за StopV sub/тик (її вже не бере цілочисельний опір), без газу гасимо
    /// по Roll за тик — покинута машина зупиняється, а не повзе.</summary>
    public const int Roll = 2, StopV = 24;
    public const int BoostAdd = 384, BoostCap = 1408, BoostTicks = 25, BoostCdTicks = 40, BoostDrag = 4;
    public const int JumpMinVF = 512, AirTicks = 14, OilTicks = 30, OilGrip = 10, HbGrip = 30;
    public const int StallTicks = 25, ResetCdTicks = 75, HornCdTicks = 25;
    /// <summary>Пружність і тертя (spec §5.3): стіна віддає 90/256 нормальної швидкості, а тертя об неї забирає
    /// з дотичної 64/256 від зміни нормальної (кулонове: ковзом — трохи, у лоб — багато);
    /// копиця — чверть назад (256 + 64 = 320 знімає нормаль і додає 25 %) і гасить до 160/256 у лоб; машини
    /// рівної маси з пружністю ½ (імпульс (256 + 128)/2 від зустрічної швидкості).</summary>
    public const int EWall = 90, WallFriction = 64, HayBack = 320, HayDamp = 160, HayEvMin = 32, ECar = 384, HitEvMin = 128;
    /// <summary>Найдалі наперед, на скільки тиків сервер приймає ввід (400 мс).</summary>
    public const int MaxAhead = 10;

    public const int EvWall = 1, EvCar = 2, EvLap = 4, EvBoost = 8, EvPuddle = 16, EvOil = 32, EvHay = 64,
        EvJump = 128, EvHorn = 256, EvLand = 512, EvReset = 1024;

    /// <summary>sin/cos на 1024 кроки в масштабі 2¹⁴: floor(sin·16384 + 0.5) — не Math.Round, він банківський.</summary>
    public static readonly int[] Sin = new int[1024], Cos = new int[1024];

    static RallyCore()
    {
        for (var i = 0; i < 1024; i++)
        {
            Sin[i] = (int)Math.Floor(Math.Sin(2 * Math.PI * i / 1024) * 16384 + 0.5);
            Cos[i] = (int)Math.Floor(Math.Cos(2 * Math.PI * i / 1024) * 16384 + 0.5);
        }
    }

    public readonly RallyTrack Track;
    public readonly int Laps;
    public readonly RallyCar[] Cars = new RallyCar[Seats];
    /// <summary>Тиків від старту; 1..75 — світлофор, фізика — з тика 76.</summary>
    public int T;
    /// <summary>Скільки вже фінішувало.</summary>
    public int Finished;
    /// <summary>Порядок місць у гонці (перші — лідери) і місце кожного (1..6, 0 — нема машини).</summary>
    public readonly int[] Order = new int[Seats];
    public readonly int[] Place = new int[Seats];
    /// <summary>Хто щойно проїхав коло цього тика — гра віддає його в рекорди.</summary>
    public bool AnyLap;

    public RallyCore(RallyTrack track, int laps)
    {
        Track = track;
        Laps = laps;
        for (var i = 0; i < Seats; i++) Cars[i] = new RallyCar { Slot = i };
    }

    /// <summary>Машина на свій стартовий слот: курс траси, усе з нуля.</summary>
    public void Grid(int seat, string car)
    {
        var c = Cars[seat];
        Reset(c);
        c.Present = true;
        c.Car = car;
        c.X = Track.SlotX[seat];
        c.Y = Track.SlotY[seat];
        c.A = Track.Heading;
        c.Cell = CellOf(c.X, c.Y);
    }

    static void Reset(RallyCar c)
    {
        c.Present = c.Ghost = false;
        c.X = c.Y = c.A = c.VF = c.VL = c.Mask = c.Lt = c.It = 0;
        Array.Fill(c.Sched, -1);
        c.Lap = 0;
        c.Next = 1;
        c.Fin = c.FinishMs = c.LapStartMs = c.BestMs = c.LastMs = 0;
        c.Air = c.Oil = c.Stall = c.BoostT = c.BoostCd = c.ResetCd = c.HornCd = 0;
        c.Ev = c.PendEv = c.PX = c.PY = c.VX = c.VY = 0;
        c.Hit = false;
    }

    /// <summary>Для тестів і журналів: поставити машину куди завгодно.</summary>
    public RallyCar Put(int seat, int x, int y, int a = 0, int vf = 0)
    {
        var c = Cars[seat];
        if (!c.Present) Grid(seat, c.Car);
        c.X = x;
        c.Y = y;
        c.A = a & 1023;
        c.VF = vf;
        c.VL = 0;
        c.Cell = CellOf(x, y);
        return c;
    }

    /// <summary>Машина зникає з траси (вийшов посеред гонки).</summary>
    public void Drop(int seat)
    {
        var c = Cars[seat];
        c.Present = false;
        c.Ev = c.PendEv = 0;
    }

    public static int CellOf(int x, int y) => (y >> RallyTrack.CellShift) * RallyTrack.Cols + (x >> RallyTrack.CellShift);

    // ---------------------------------------------------------------------------------------------
    // Ввід
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Маска <paramref name="k"/>, чинна з тика <paramref name="t"/>. Наперед — у кільце (далі ніж на 10 тиків не
    /// пускаємо), запізніла — одразу. Повертає запізнення в тиках: (T+1) − t, ≤ 0 — прийшла вчасно.
    /// </summary>
    public int Schedule(int seat, int t, int k)
    {
        var c = Cars[seat];
        if (t > T + MaxAhead) t = T + MaxAhead;
        if (t >= T + 1) c.Sched[t & 15] = k;
        else c.Mask = k;
        c.Lt = T + 1 - t;
        c.It = t;
        return c.Lt;
    }

    /// <summary>Повернення на трасу (spec §5.5): до останніх пройдених воріт або на свій слот, секунда без керма.</summary>
    public void Respawn(int seat)
    {
        var c = Cars[seat];
        var k = Track.K;
        var i = (c.Next - 1 + k) % k;
        if (c.Lap == 0 && c.Next == 1)
        {
            c.X = Track.SlotX[seat];
            c.Y = Track.SlotY[seat];
            c.A = Track.Heading;
        }
        else
        {
            c.X = Track.ResetX[i];
            c.Y = Track.ResetY[i];
            c.A = Track.GateA[i];
        }
        c.VF = c.VL = 0;
        c.Air = c.Oil = c.BoostT = 0;
        c.Stall = StallTicks;
        c.ResetCd = ResetCdTicks;
        c.PendEv |= EvReset;
        c.Cell = CellOf(c.X, c.Y);
    }

    public void Honk(int seat)
    {
        var c = Cars[seat];
        c.HornCd = HornCdTicks;
        c.PendEv |= EvHorn;
    }

    // ---------------------------------------------------------------------------------------------
    // Тик
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Один тик: T++, заплановані маски стають чинними, і — якщо світлофор уже зелений (T &gt; 75) — фізика.
    /// </summary>
    public void Tick()
    {
        T++;
        var slot = T & 15;
        AnyLap = false;
        for (var i = 0; i < Seats; i++)
        {
            var c = Cars[i];
            var s = c.Sched[slot];
            if (s >= 0)
            {
                c.Mask = s;
                c.Sched[slot] = -1;
            }
            c.Ev = c.PendEv;
            c.PendEv = 0;
            c.PX = c.X;
            c.PY = c.Y;
            c.Hit = false;
        }
        if (T <= CountTicks)
        {
            // на світлофорі машини стоять, лише гудок перезаряджається
            for (var i = 0; i < Seats; i++)
                if (Cars[i].HornCd > 0) Cars[i].HornCd--;
            Rank();
            return;
        }

        for (var i = 0; i < Seats; i++)
            if (Cars[i].Present) Controls(Cars[i]);
        for (var h = 0; h < 2; h++)
        {
            for (var i = 0; i < Seats; i++)
            {
                var c = Cars[i];
                if (!c.Present) continue;
                c.X += h == 0 ? c.VX / 2 : c.VX - c.VX / 2;
                c.Y += h == 0 ? c.VY / 2 : c.VY - c.VY / 2;
                Clamp(c);
                Walls(c);
                Clamp(c);
            }
            for (var a = 0; a < Seats; a++)
                for (var b = a + 1; b < Seats; b++)
                    Bump(Cars[a], Cars[b]);
        }
        for (var i = 0; i < Seats; i++)
        {
            var c = Cars[i];
            if (!c.Present) continue;
            if (c.Hit)
            {
                c.VF = (c.VX * Cos[c.A] + c.VY * Sin[c.A]) >> 14;
                c.VL = (-c.VX * Sin[c.A] + c.VY * Cos[c.A]) >> 14;
            }
            Cells(c);
            Timers(c);
        }
        Rank();
    }

    void Controls(RallyCar c)
    {
        int drag, grip, acc, gas, steer;
        bool hb;
        if (c.Air > 0)
        {
            // у повітрі колеса землі не торкаються: ні газу, ні керма, ні опору
            drag = grip = acc = gas = steer = 0;
            hb = false;
        }
        else
        {
            var code = Track.Tile[c.Cell];
            drag = RallySurface.Drag[code];
            grip = RallySurface.Grip[code];
            acc = RallySurface.Acc[code];
            gas = (c.Mask & 8) != 0 ? -1 : (c.Mask & 4) != 0 ? 1 : 0;
            steer = ((c.Mask & 2) != 0 ? 1 : 0) - ((c.Mask & 1) != 0 ? 1 : 0);
            hb = (c.Mask & 16) != 0;
            if (c.Stall > 0)
            {
                gas = steer = 0;
                hb = false;
            }
            if (c.BoostT > 0) drag = BoostDrag;
            if (c.Oil > 0) grip = OilGrip;
            if (hb && grip > HbGrip) grip = HbGrip;
        }

        if (gas > 0) c.VF += acc;
        else if (gas < 0) c.VF = c.VF > 0 ? Math.Max(0, c.VF - Brake) : Math.Max(-MaxRev, c.VF - Rev);
        c.VF -= c.VF * drag / 256;
        c.VL -= c.VL * grip / 256;
        // опір коченню: усічення вище не гасить швидкостей, менших за 256/опір (на дорозі — 15 sub/тик, на льоду —
        // 21), і покинута машина повзла б сама хоч до кінця гонки. На землі малу швидкість гасимо кроками до нуля
        // (уперед — без газу й гальма); на ходу, понад StopV, фізика та сама, що в spec §5.4
        if (c.Air == 0)
        {
            if (gas == 0 && c.VF != 0 && c.VF > -StopV && c.VF < StopV) c.VF += c.VF > 0 ? -Math.Min(Roll, c.VF) : Math.Min(Roll, -c.VF);
            if (c.VL != 0 && c.VL > -StopV && c.VL < StopV) c.VL += c.VL > 0 ? -1 : 1;
        }
        if (c.BoostT > 0 && c.VF > BoostCap) c.VF = BoostCap;

        var tr = Turn * Math.Min(Math.Abs(c.VF), VTurn) / 256;
        if (hb) tr = tr * 3 / 2;
        if (c.VF < 0) tr = -tr;
        var dA = steer * tr;
        if (dA != 0)
        {
            int co = Cos[dA & 1023], si = Sin[dA & 1023];
            var vf = (c.VF * co + c.VL * si) >> 14;
            var vl = (-c.VF * si + c.VL * co) >> 14;
            c.VF = vf;
            c.VL = vl;
            c.A = (c.A + dA) & 1023;
        }
        c.VX = (c.VF * Cos[c.A] - c.VL * Sin[c.A]) >> 14;
        c.VY = (c.VF * Sin[c.A] + c.VL * Cos[c.A]) >> 14;
    }

    static void Clamp(RallyCar c)
    {
        if (c.X < RWall) c.X = RWall;
        else if (c.X > WorldW - RWall) c.X = WorldW - RWall;
        if (c.Y < RWall) c.Y = RWall;
        else if (c.Y > WorldH - RWall) c.Y = WorldH - RWall;
    }

    bool WallAt(int cx, int cy) => RallySurface.IsWall(Track.CodeAt(cx, cy));

    /// <summary>Стіни й копиці, що перекриває квадрат машини (≤ 2×2 клітинки), рядок за рядком.</summary>
    void Walls(RallyCar c)
    {
        int x0 = (c.X - RWall) >> RallyTrack.CellShift, x1 = (c.X + RWall) >> RallyTrack.CellShift;
        int y0 = (c.Y - RWall) >> RallyTrack.CellShift, y1 = (c.Y + RWall) >> RallyTrack.CellShift;
        for (var cy = y0; cy <= y1; cy++)
            for (var cx = x0; cx <= x1; cx++)
            {
                var code = Track.CodeAt(cx, cy);
                if (RallySurface.IsWall(code)) Wall(c, cx, cy);
                else if (code == RallySurface.Hay) HayBale(c, cx, cy);
            }
    }

    /// <summary>
    /// Коло проти квадрата клітинки-стіни. Грань, за якою теж стіна (шов між двома клітинками тину), не
    /// штовхає: інакше машина, що ковзає вздовж тину, чіплялась би за кожен шов.
    /// </summary>
    void Wall(RallyCar c, int cx, int cy)
    {
        int rx = cx << RallyTrack.CellShift, ry = cy << RallyTrack.CellShift;
        const int s = RallyTrack.CellSub;
        int px = c.X < rx ? rx : c.X > rx + s ? rx + s : c.X;
        int py = c.Y < ry ? ry : c.Y > ry + s ? ry + s : c.Y;
        int dx = c.X - px, dy = c.Y - py;
        int axis, sign;
        if (dx == 0 && dy == 0)
        {
            // центр у стіні: до найближчої відкритої грані (закриті — ті, за якими теж стіна)
            int dl = c.X - rx + (WallAt(cx - 1, cy) ? 4 * s : 0);
            int dr = rx + s - c.X + (WallAt(cx + 1, cy) ? 4 * s : 0);
            int du = c.Y - ry + (WallAt(cx, cy - 1) ? 4 * s : 0);
            int dd = ry + s - c.Y + (WallAt(cx, cy + 1) ? 4 * s : 0);
            axis = 0;
            sign = -1;
            var best = dl;
            if (dr < best) { best = dr; sign = 1; }
            if (du < best) { best = du; axis = 1; sign = -1; }
            if (dd < best) { axis = 1; sign = 1; }
            if (axis == 0) c.X = sign < 0 ? rx - RWall : rx + s + RWall;
            else c.Y = sign < 0 ? ry - RWall : ry + s + RWall;
        }
        else
        {
            if (dx * dx + dy * dy >= RWall * RWall) return;
            var ax = Math.Abs(dx);
            var ay = Math.Abs(dy);
            axis = ax >= ay ? 0 : 1;
            // шов: за цією гранню ще стіна — штовхаємо по іншій осі, якщо є куди
            if (axis == 0 && ay > 0 && WallAt(cx + (dx > 0 ? 1 : -1), cy)) axis = 1;
            else if (axis == 1 && ax > 0 && WallAt(cx, cy + (dy > 0 ? 1 : -1))) axis = 0;
            if (axis == 0)
            {
                sign = dx > 0 ? 1 : -1;
                c.X += sign * (RWall - ax);
            }
            else
            {
                sign = dy > 0 ? 1 : -1;
                c.Y += sign * (RWall - ay);
            }
        }

        var vn = axis == 0 ? sign * c.VX : sign * c.VY;
        if (vn >= 0) return;
        // відскок — 90/256 нормальної; тертя об тин — чверть від зміни нормальної швидкості, не більше за дотичну:
        // у лоб гасить сильно, а ковзом уздовж тину лише пригальмовує
        var back = -vn * EWall / 256;
        var fr = (back - vn) * WallFriction / 256;
        if (axis == 0)
        {
            c.VX = sign * back;
            c.VY = c.VY > 0 ? Math.Max(0, c.VY - fr) : Math.Min(0, c.VY + fr);
        }
        else
        {
            c.VY = sign * back;
            c.VX = c.VX > 0 ? Math.Max(0, c.VX - fr) : Math.Min(0, c.VX + fr);
        }
        c.Hit = true;
        if (-vn > HitEvMin) c.Ev |= EvWall;
    }

    void HayBale(RallyCar c, int cx, int cy)
    {
        const int r = RWall + RHay;
        int hx = (cx << RallyTrack.CellShift) + RallyTrack.CellSub / 2, hy = (cy << RallyTrack.CellShift) + RallyTrack.CellSub / 2;
        int dx = c.X - hx, dy = c.Y - hy;
        var d2 = dx * dx + dy * dy;
        if (d2 >= r * r) return;
        var d = Isqrt(d2);
        if (d == 0) { dx = 1; d = 1; }
        var nx = dx * 16384 / d;
        var ny = dy * 16384 / d;
        var push = r - d;
        c.X += (nx * push) >> 14;
        c.Y += (ny * push) >> 14;
        var vn = (c.VX * nx + c.VY * ny) >> 14;
        if (vn >= 0) return;
        // у лоб — назад чверть і сіно гасить до 62,5 %; по дотичній — лише трохи пригальмовує
        var j = vn * HayBack / 256;
        c.VX -= (j * nx) >> 14;
        c.VY -= (j * ny) >> 14;
        var damp = 256 - Math.Min(256 - HayDamp, -vn * (256 - HayDamp) / 512);
        c.VX = c.VX * damp / 256;
        c.VY = c.VY * damp / 256;
        c.Hit = true;
        if (-vn > HayEvMin) c.Ev |= EvHay;
    }

    /// <summary>Дві машини рівної маси: розсунути навпіл і обмінятись половиною зустрічної швидкості.</summary>
    static void Bump(RallyCar a, RallyCar b)
    {
        if (!a.Present || !b.Present || a.Ghost || b.Ghost || (a.Air > 0) != (b.Air > 0)) return;
        const int r = RCar * 2;
        int dx = b.X - a.X, dy = b.Y - a.Y;
        // спершу рамка: квадрат відстані далеких машин не влазить в int32 (і в JS дав би інше число)
        if (dx >= r || dx <= -r || dy >= r || dy <= -r) return;
        var d2 = dx * dx + dy * dy;
        if (d2 >= r * r) return;
        var d = Isqrt(d2);
        if (d == 0) { dx = 1; d = 1; }
        var nx = dx * 16384 / d;
        var ny = dy * 16384 / d;
        var push = (r - d) / 2;
        int px = (nx * push) >> 14, py = (ny * push) >> 14;
        a.X -= px;
        a.Y -= py;
        b.X += px;
        b.Y += py;
        Clamp(a);
        Clamp(b);
        var vrel = ((b.VX - a.VX) * nx + (b.VY - a.VY) * ny) >> 14;
        if (vrel >= 0) return;
        var j = -vrel * ECar / 2 / 256;
        int jx = (j * nx) >> 14, jy = (j * ny) >> 14;
        a.VX -= jx;
        a.VY -= jy;
        b.VX += jx;
        b.VY += jy;
        a.Hit = b.Hit = true;
        if (-vrel > HitEvMin)
        {
            a.Ev |= EvCar;
            b.Ev |= EvCar;
        }
    }

    public static int Isqrt(int n)
    {
        if (n <= 0) return 0;
        var x = (int)Math.Sqrt(n);
        while ((long)x * x > n) x--;
        while ((long)(x + 1) * (x + 1) <= n) x++;
        return x;
    }

    /// <summary>Нова клітинка: поверхні «на в'їзді» (лише на землі) і ворота.</summary>
    void Cells(RallyCar c)
    {
        var cell = CellOf(c.X, c.Y);
        if (cell == c.Cell) return;
        c.Cell = cell;
        if (c.Air == 0)
            switch (Track.Tile[cell])
            {
                case RallySurface.Puddle:
                    c.Ev |= EvPuddle;
                    break;
                case RallySurface.Oil:
                    c.Oil = OilTicks;
                    c.Ev |= EvOil;
                    break;
                case RallySurface.Boost when c.BoostCd == 0:
                    c.VF = Math.Min(BoostCap, Math.Max(c.VF, 0) + BoostAdd);
                    c.BoostT = BoostTicks;
                    c.BoostCd = BoostCdTicks;
                    c.Ev |= EvBoost;
                    break;
                case RallySurface.Ramp when c.VF >= JumpMinVF:
                    c.Air = AirTicks;
                    c.Ev |= EvJump;
                    break;
            }
        var g = Track.GateAt[cell];
        if (!c.Ghost && g == c.Next) PassGate(c);
    }

    void PassGate(RallyCar c)
    {
        if (c.Next != 0)
        {
            c.Next = (c.Next + 1) % Track.K;
            return;
        }
        c.Next = 1;
        c.Lap++;
        c.Ev |= EvLap;
        AnyLap = true;
        var ms = LapCrossMs(c);
        c.LastMs = ms - c.LapStartMs;
        c.LapStartMs = ms;
        if (c.BestMs == 0 || c.LastMs < c.BestMs) c.BestMs = c.LastMs;
        if (c.Lap < Laps) return;
        c.Fin = ++Finished;
        c.FinishMs = ms;
        c.Ghost = true;
    }

    /// <summary>Час перетину лінії з часткою тика: лінійно між позицією на початку й наприкінці тика.</summary>
    int LapCrossMs(RallyCar c)
    {
        int num, den;
        if (Track.LineAxis == 0)
        {
            num = (Track.LineEdge - c.PX) * Track.LineDir;
            den = (c.X - c.PX) * Track.LineDir;
        }
        else
        {
            num = (Track.LineEdge - c.PY) * Track.LineDir;
            den = (c.Y - c.PY) * Track.LineDir;
        }
        var part = den <= 0 || num < 0 ? 0 : num > den ? TickMs : TickMs * num / den;
        return (T - CountTicks - 1) * TickMs + part;
    }

    static void Timers(RallyCar c)
    {
        if (c.Air > 0 && --c.Air == 0) c.Ev |= EvLand;
        if (c.Oil > 0) c.Oil--;
        if (c.Stall > 0) c.Stall--;
        if (c.BoostT > 0) c.BoostT--;
        if (c.BoostCd > 0) c.BoostCd--;
        if (c.ResetCd > 0) c.ResetCd--;
        if (c.HornCd > 0) c.HornCd--;
    }

    // ---------------------------------------------------------------------------------------------
    // Позиції
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Порядок у гонці вставним сортуванням шести індексів (без LINQ і без алокацій): фінішери за місцем,
    /// далі за пройденими воротами, далі за відстанню до наступних; вільні — у кінці з місцем 0.
    /// </summary>
    public void Rank()
    {
        for (var i = 0; i < Seats; i++)
        {
            var j = i;
            while (j > 0 && Ahead(i, Order[j - 1]))
            {
                Order[j] = Order[j - 1];
                j--;
            }
            Order[j] = i;
        }
        for (var i = 0; i < Seats; i++)
        {
            var s = Order[i];
            Place[s] = Cars[s].Present ? i + 1 : 0;
        }
    }

    /// <summary>Чи стоїть місце a в гонці вище за b (при рівності — нижчий номер місця вище).</summary>
    bool Ahead(int a, int b)
    {
        RallyCar ca = Cars[a], cb = Cars[b];
        if (ca.Present != cb.Present) return ca.Present;
        if (!ca.Present) return a < b;
        if ((ca.Fin > 0) != (cb.Fin > 0)) return ca.Fin > 0;
        if (ca.Fin > 0) return ca.Fin < cb.Fin;
        int pa = Passed(ca), pb = Passed(cb);
        if (pa != pb) return pa > pb;
        long da = GateDist(ca), db = GateDist(cb);
        if (da != db) return da < db;
        return a < b;
    }

    /// <summary>Скільки воріт пройдено від старту: Lap·K + Next − 1 (Next = 0 — перед лінією, тобто K−1 воріт кола).</summary>
    public int Passed(RallyCar c) => c.Lap * Track.K + (c.Next == 0 ? Track.K : c.Next) - 1;

    public long GateDist(RallyCar c)
    {
        long dx = c.X - Track.GateCX[c.Next], dy = c.Y - Track.GateCY[c.Next];
        return dx * dx + dy * dy;
    }

    // ---------------------------------------------------------------------------------------------
    // Хеш стану (журнали паритету C# ↔ JS)
    // ---------------------------------------------------------------------------------------------

    /// <summary>FNV-1a-32 над X, Y, A, VF, VL кожної присутньої машини (int32 little-endian), продовжуючи h.</summary>
    public uint Hash(uint h)
    {
        for (var i = 0; i < Seats; i++)
        {
            var c = Cars[i];
            if (!c.Present) continue;
            h = Mix(h, c.X);
            h = Mix(h, c.Y);
            h = Mix(h, c.A);
            h = Mix(h, c.VF);
            h = Mix(h, c.VL);
        }
        return h;
    }

    public const uint FnvStart = 0x811c9dc5, FnvPrime = 0x01000193;

    public static uint Mix(uint h, int v)
    {
        for (var b = 0; b < 4; b++)
        {
            h ^= (uint)(v >> (8 * b)) & 0xff;
            h *= FnvPrime;
        }
        return h;
    }
}
