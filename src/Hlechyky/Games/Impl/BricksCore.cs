using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Посилка сміття, що ще не влізла в стіну: ряди з однією діркою, дозрівають на тику <see cref="RipeAt"/>.</summary>
public struct BricksCredit
{
    public int G, Rows, Hole, RipeAt, From;
}

/// <summary>Одна фіксація з рядами — для події <c>c</c> у кадрі й для посилки цілі.</summary>
public struct BricksClear
{
    public int Lines, Kind, Combo, B2b, Attack, Sent, Tick;
}

/// <summary>
/// Рушій однієї стіни «Цеглин» (docs/games/specs/bricks.md §2, §5). Без кімнат, без годинника, без <c>Ctx</c>:
/// лише клітинки, фігурка й лічильники в цілих числах. Той самий рушій, побайтно, живе в <c>web/games/bricks.js</c>
/// (<c>window.BricksCore</c>): клієнт передбачає свою стіну, сервер переганяє той самий журнал натисків і звіряє
/// хеш. Тому тут жодних <c>double</c>, жодного <c>Random</c>, і кожна зміна правил — одразу в обох мовах.
/// У <see cref="Step"/>/<see cref="Lock"/> нічого не алокується: буфери — поля стіни.
/// </summary>
public sealed class BricksCore
{
    // ---------- сталі (дублюються в bricks.js і у виді, поле rules) ----------

    public const int W = 10, H = 24, Visible = 20;
    public const int Das = 10, Arr = 2, SoftG = 2, LockDelay = 30, MaxResets = 15, ClearTicks = 10, RipeTicks = 30;
    public const int MaxInsert = 8, Ahead = 15, Behind = 90;
    public const int SprintG = 48, SprintLines = 40;
    public const int Sudden = 18_000, SuddenEvery = 600, Cap = 28_800;
    public const int MaxCredits = 32;
    public const int FullRow = (1 << W) - 1;
    /// <summary>Гравітація за етапом темпу: тиків (60 Гц) на одну клітинку вниз. Далі — 1.</summary>
    public static readonly int[] Grav = [48, 36, 26, 18, 12, 8, 5, 3, 2, 1];

    public const int I = 0, O = 1, T = 2, S = 3, Z = 4, J = 5, L = 6;
    public const int ModeNormal = 0, ModeHard = 1, ModeNone = 2;
    /// <summary>Чому стіна впала: поява в зайняте, фіксація цілком над стіною, сміття вилізло вгору.</summary>
    public const int OutSpawn = 1, OutLock = 2, OutGarbage = 3, OutLeft = 4;

    /// <summary>Коди подій журналу (§3.1).</summary>
    public const int KPulse = 0, KLeft = 1, KLeftUp = 2, KRight = 3, KRightUp = 4, KSoft = 5, KSoftUp = 6,
        KCw = 7, KCcw = 8, KHard = 10, KHold = 11;

    public static bool KnownKey(int k) => k is >= 0 and <= 8 or 10 or 11;

    /// <summary>
    /// Клітинки фігурок у рамці: [тип][оберт][4 × (x, y)], y — вгору. Рівно таблиця spec §2.1; тест перевіряє,
    /// що стан r — це стан 0, повернутий r разів.
    /// </summary>
    public static readonly sbyte[] Shapes =
    [
        // I
        0,2, 1,2, 2,2, 3,2,   2,0, 2,1, 2,2, 2,3,   0,1, 1,1, 2,1, 3,1,   1,0, 1,1, 1,2, 1,3,
        // O
        1,1, 2,1, 1,2, 2,2,   1,1, 2,1, 1,2, 2,2,   1,1, 2,1, 1,2, 2,2,   1,1, 2,1, 1,2, 2,2,
        // T
        0,1, 1,1, 2,1, 1,2,   1,0, 1,1, 1,2, 2,1,   0,1, 1,1, 2,1, 1,0,   1,0, 1,1, 1,2, 0,1,
        // S
        0,1, 1,1, 1,2, 2,2,   1,1, 1,2, 2,0, 2,1,   0,0, 1,0, 1,1, 2,1,   0,1, 0,2, 1,0, 1,1,
        // Z
        0,2, 1,2, 1,1, 2,1,   1,0, 1,1, 2,1, 2,2,   0,1, 1,1, 1,0, 2,0,   0,0, 0,1, 1,1, 1,2,
        // J
        0,2, 0,1, 1,1, 2,1,   1,0, 1,1, 1,2, 2,2,   0,1, 1,1, 2,1, 2,0,   0,0, 1,0, 1,1, 1,2,
        // L
        2,2, 0,1, 1,1, 2,1,   1,0, 1,1, 1,2, 2,0,   0,0, 0,1, 1,1, 2,1,   0,2, 1,2, 1,1, 1,0,
    ];

    /// <summary>
    /// Зсуви SRS (§2.2): [перехід][5 × (dx, dy)]. Переходи: 0→1, 1→0, 1→2, 2→1, 2→3, 3→2, 3→0, 0→3 —
    /// за годинниковою з r це індекс 2r, проти годинникової в b — індекс 2b+1.
    /// </summary>
    public static readonly sbyte[] KicksJlstz =
    [
        0,0, -1,0, -1,1, 0,-2, -1,-2,   // 0→1
        0,0, 1,0, 1,-1, 0,2, 1,2,       // 1→0
        0,0, 1,0, 1,-1, 0,2, 1,2,       // 1→2
        0,0, -1,0, -1,1, 0,-2, -1,-2,   // 2→1
        0,0, 1,0, 1,1, 0,-2, 1,-2,      // 2→3
        0,0, -1,0, -1,-1, 0,2, -1,2,    // 3→2
        0,0, -1,0, -1,-1, 0,2, -1,2,    // 3→0
        0,0, 1,0, 1,1, 0,-2, 1,-2,      // 0→3
    ];

    public static readonly sbyte[] KicksI =
    [
        0,0, -2,0, 1,0, -2,-1, 1,2,     // 0→1
        0,0, 2,0, -1,0, 2,1, -1,-2,     // 1→0
        0,0, -1,0, 2,0, -1,2, 2,-1,     // 1→2
        0,0, 1,0, -2,0, 1,-2, -2,1,     // 2→1
        0,0, 2,0, -1,0, 2,1, -1,-2,     // 2→3
        0,0, -2,0, 1,0, -2,-1, 1,2,     // 3→2
        0,0, 1,0, -2,0, 1,-2, -2,1,     // 3→0
        0,0, -1,0, 2,0, -1,2, 2,-1,     // 0→3
    ];

    static readonly int[] AttackNormal = [0, 0, 1, 2, 4];
    static readonly int[] AttackHard = [0, 1, 1, 2, 4];
    static readonly int[] AttackTspin = [0, 2, 4, 6, 6];

    // ---------- стан (§5.2) ----------

    public readonly byte[] Cells = new byte[W * H];
    public readonly ushort[] Mask = new ushort[H];
    /// <summary>Фігурка: тип (−1 — нема), лівий нижній кут рамки, оберт.</summary>
    public int Type = -1, Bx, By, Rot;
    public uint Seed = 1;
    /// <summary>Скільки фігурок уже видано з черги — номер наступної.</summary>
    public int Pi;
    public int Hold = -1;
    public bool HoldUsed;
    /// <summary>Маска затиснутого: 1 ←, 2 →, 4 ↓.</summary>
    public int Keys, Dir, DasT, GravT, LockT, Resets, LowY, Clearing;
    public bool LastRot;
    public int Combo = -1, B2b = -1, Lines, Sent, Recv;
    /// <summary>Годинник стіни (60 Гц від «go»), номер останньої застосованої події журналу.</summary>
    public int Tick, Seq;
    public readonly BricksCredit[] Credits = new BricksCredit[MaxCredits];
    public int CreditCount, Gseq;
    public bool Alive = true;
    public int OutTick = -1, OutWhy;
    public int Garbage;
    /// <summary>Тиків на етап темпу; 0 — гравітація стала (<see cref="SprintG"/>), як у спринті.</summary>
    public int StageTicks;
    /// <summary>Росте на кожну зміну клітинок стіни — кімната шле ряди, лише коли вона змінилась.</summary>
    public int CellsVer;
    /// <summary>Росте на будь-яку видиму зміну (фігурка, кишеня, клітинки) — клієнт малює лише тоді.</summary>
    public int Ver;

    // ---------- що сталось (кімната забирає після пачки й скидає лічильники) ----------

    public readonly BricksClear[] Clears = new BricksClear[64];
    public int ClearCount;
    /// <summary>Скільки рядів сміття влізло з минулого забору (для ефекту «гуп» і тестів).</summary>
    public int Inserted;
    /// <summary>Скільки разів закрито четвірку (ачівка).</summary>
    public int Fours;

    readonly int[] _bag = new int[7];
    int _bagN = -1;
    uint _bagSeed;
    readonly int[] _takeRows = new int[MaxCredits];

    public BricksCore() { }

    public BricksCore(uint seed, int garbage = ModeNormal, int stageTicks = 0) => Reset(seed, garbage, stageTicks);

    /// <summary>Чиста стіна з першою фігуркою на появі; годинник — 0.</summary>
    public void Reset(uint seed, int garbage, int stageTicks)
    {
        Array.Clear(Cells);
        Array.Clear(Mask);
        Seed = seed == 0 ? 1 : seed;
        Garbage = garbage;
        StageTicks = stageTicks;
        Pi = 0;
        Hold = -1;
        HoldUsed = false;
        Keys = Dir = DasT = GravT = LockT = Resets = Clearing = 0;
        LastRot = false;
        Combo = B2b = -1;
        Lines = Sent = Recv = 0;
        Tick = Seq = 0;
        CreditCount = Gseq = 0;
        Alive = true;
        OutTick = -1;
        OutWhy = 0;
        CellsVer = Ver = 0;
        ClearCount = Inserted = Fours = 0;
        _bagN = -1;
        SpawnNext();
    }

    // ---------- черга: мішок із семи як функція від зерна й номера (§2.3) ----------

    public static uint NextRand(ref uint x)
    {
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        return x;
    }

    /// <summary>Мішок номер <paramref name="n"/>: перестановка 0..6 Фішером-Єйтсом на xorshift32.</summary>
    public static void Bag(uint seed, int n, int[] into)
    {
        var s = unchecked(seed + (uint)n * 0x9E3779B9u);
        if (s == 0) s = 1;
        for (var i = 0; i < 7; i++) into[i] = i;
        for (var k = 6; k >= 1; k--)
        {
            var j = (int)(NextRand(ref s) % (uint)(k + 1));
            (into[k], into[j]) = (into[j], into[k]);
        }
    }

    /// <summary>Фігурка номер <paramref name="i"/> у черзі цього зерна.</summary>
    public int PieceAt(int i)
    {
        var n = i / 7;
        if (n != _bagN || _bagSeed != Seed)
        {
            Bag(Seed, n, _bag);
            _bagN = n;
            _bagSeed = Seed;
        }
        return _bag[i % 7];
    }

    // ---------- зіткнення ----------

    /// <summary>Чи може фігурка <paramref name="type"/> стати рамкою в (bx, by) з обертом rot.</summary>
    public bool Fits(int type, int bx, int by, int rot)
    {
        var o = (type * 4 + rot) * 8;
        for (var i = 0; i < 8; i += 2)
        {
            var x = bx + Shapes[o + i];
            var y = by + Shapes[o + i + 1];
            if ((uint)x >= W || (uint)y >= H) return false;
            if ((Mask[y] & (1 << x)) != 0) return false;
        }
        return true;
    }

    bool Fits(int bx, int by, int rot) => Fits(Type, bx, by, rot);

    bool Grounded => !Fits(Bx, By - 1, Rot);

    /// <summary>Етап темпу й гравітація — від годинника стіни, тож клієнт і сервер рахують їх однаково.</summary>
    public int Stage => StageTicks <= 0 ? 0 : Tick / StageTicks;

    public int Gravity => StageTicks <= 0 ? SprintG : Grav[Math.Min(Grav.Length - 1, Tick / StageTicks)];

    // ---------- кроки ----------

    /// <summary>Один тик стіни (§2.5).</summary>
    public void Step()
    {
        if (!Alive) return;
        Tick++;
        if (Clearing > 0)
        {
            Clearing--;
            if (Clearing == 0)
            {
                RemoveFullRows();
                SpawnNext();
            }
            return;
        }
        if (Type < 0) return;
        if (Dir != 0)
        {
            DasT++;
            if (DasT >= Das && (DasT - Das) % Arr == 0) TryMove(Dir);
        }
        var g = Gravity;
        if ((Keys & 4) != 0 && g > SoftG) g = SoftG;
        GravT++;
        while (GravT >= g)
        {
            GravT -= g;
            if (!Fits(Bx, By - 1, Rot)) break;
            By--;
            LastRot = false;
            Ver++;
            if (By < LowY)
            {
                LowY = By;
                Resets = 0;
                LockT = 0;
            }
        }
        if (!Grounded) LockT = 0;
        else if (++LockT >= LockDelay) Lock();
    }

    /// <summary>Виконати тики, поки годинник стіни не дійде до <paramref name="t"/>.</summary>
    public void AdvanceTo(int t)
    {
        while (Tick < t && Alive) Step();
    }

    /// <summary>Подія журналу на поточному тику (§2.4). Невідомий код — нічого.</summary>
    public void Apply(int k)
    {
        if (!Alive) return;
        switch (k)
        {
            case KLeft:
                Keys |= 1; Dir = -1; DasT = 0; TryMove(-1);
                break;
            case KLeftUp:
                Keys &= ~1;
                if (Dir == -1)
                {
                    if ((Keys & 2) != 0) { Dir = 1; DasT = 0; TryMove(1); }
                    else Dir = 0;
                }
                break;
            case KRight:
                Keys |= 2; Dir = 1; DasT = 0; TryMove(1);
                break;
            case KRightUp:
                Keys &= ~2;
                if (Dir == 1)
                {
                    if ((Keys & 1) != 0) { Dir = -1; DasT = 0; TryMove(-1); }
                    else Dir = 0;
                }
                break;
            case KSoft:
                Keys |= 4;
                // Інакше накопичене за повільної гравітації кинуло б фігурку на кілька клітинок за один тик.
                if (GravT > SoftG - 1) GravT = SoftG - 1;
                break;
            case KSoftUp:
                Keys &= ~4;
                break;
            case KCw:
                TryRotate(1);
                break;
            case KCcw:
                TryRotate(3);
                break;
            case KHard:
                HardDrop();
                break;
            case KHold:
                DoHold();
                break;
        }
    }

    bool TryMove(int dx)
    {
        if (Type < 0 || !Fits(Bx + dx, By, Rot)) return false;
        Bx += dx;
        LastRot = false;
        Ver++;
        Moved();
        return true;
    }

    /// <summary>Вдалий рух чи оберт скидає затримку фіксації, поки не вичерпано скидань на цій висоті.</summary>
    void Moved()
    {
        if (Resets < MaxResets)
        {
            Resets++;
            LockT = 0;
        }
    }

    /// <summary>Оберт на <paramref name="turn"/> чвертей (1 — за годинниковою, 3 — проти) з п'ятьма зсувами SRS.</summary>
    void TryRotate(int turn)
    {
        if (Type < 0 || Type == O) return;
        var to = (Rot + turn) & 3;
        var idx = turn == 1 ? Rot * 2 : to * 2 + 1;
        var kicks = Type == I ? KicksI : KicksJlstz;
        var o = idx * 10;
        for (var i = 0; i < 10; i += 2)
        {
            var nx = Bx + kicks[o + i];
            var ny = By + kicks[o + i + 1];
            if (!Fits(nx, ny, to)) continue;
            Bx = nx;
            By = ny;
            Rot = to;
            LastRot = true;
            Ver++;
            Moved();
            if (By < LowY)
            {
                LowY = By;
                Resets = 0;
                LockT = 0;
            }
            return;
        }
    }

    void HardDrop()
    {
        if (Type < 0) return;
        var d = 0;
        while (Fits(Bx, By - 1, Rot)) { By--; d++; }
        if (d > 0) LastRot = false;
        Lock();
    }

    void DoHold()
    {
        if (Type < 0 || HoldUsed) return;
        var was = Type;
        if (Hold < 0)
        {
            Hold = was;
            SpawnNext();
        }
        else
        {
            var t = Hold;
            Hold = was;
            Spawn(t);
        }
        HoldUsed = true;
        Ver++;
    }

    void SpawnNext()
    {
        var t = PieceAt(Pi);
        Pi++;
        Spawn(t);
    }

    /// <summary>Нова фігурка на місці появи (§2.1); зайняте — завал. Публічне для тестів і стенда.</summary>
    public void Spawn(int type)
    {
        Type = type;
        Rot = 0;
        Bx = 3;
        By = type == I ? 18 : 19;
        HoldUsed = false;
        LockT = 0;
        Resets = 0;
        GravT = 0;
        LastRot = false;
        LowY = By;
        Ver++;
        if (!Fits(Bx, By, Rot)) TopOut(OutSpawn);
    }

    /// <summary>Фіксація фігурки (§2.5): клітинки в стіну, ряди, напад, сміття або наступна фігурка.</summary>
    void Lock()
    {
        var o = (Type * 4 + Rot) * 8;
        var above = 0;
        var color = (byte)(Type + 1);
        for (var i = 0; i < 8; i += 2)
        {
            var x = Bx + Shapes[o + i];
            var y = By + Shapes[o + i + 1];
            Cells[y * W + x] = color;
            Mask[y] |= (ushort)(1 << x);
            if (y >= Visible) above++;
        }
        CellsVer++;
        Ver++;
        if (above == 4)
        {
            TopOut(OutLock);
            return;
        }

        var n = 0;
        var left = 0;   // чи лишиться в стіні щось, крім повних рядів (чиста стіна — ні)
        for (var y = 0; y < H; y++)
        {
            if (Mask[y] == FullRow) n++;
            else if (Mask[y] != 0) left++;
        }

        var tspin = false;
        if (Type == T && LastRot)
        {
            var corners = Taken(Bx, By) + Taken(Bx + 2, By) + Taken(Bx, By + 2) + Taken(Bx + 2, By + 2);
            tspin = corners >= 3;
        }
        Type = -1;

        if (n == 0)
        {
            Combo = -1;
            InsertGarbage();
            if (Alive) SpawnNext();
            return;
        }

        var perfect = left == 0;
        Combo++;
        var strong = n == 4 || tspin;
        var atk = tspin ? AttackTspin[n] : Garbage == ModeHard ? AttackHard[n] : AttackNormal[n];
        if (strong)
        {
            if (B2b >= 0) atk++;
            B2b++;
        }
        else B2b = -1;
        if (Combo >= 1) atk += Math.Min(3, (Combo + 1) / 2);
        if (perfect) atk += 4;
        if (Garbage == ModeHard && atk > 0) atk++;
        if (Garbage == ModeNone) atk = 0;
        if (n == 4) Fours++;
        Lines += n;

        // Свій напад спершу гасить сміття, що ще летить сюди (найстаріше першим), решта — цілі.
        var rest = atk;
        var w = 0;
        for (var i = 0; i < CreditCount; i++)
        {
            var c = Credits[i];
            if (rest > 0)
            {
                var take = Math.Min(rest, c.Rows);
                c.Rows -= take;
                rest -= take;
            }
            if (c.Rows > 0) Credits[w++] = c;
        }
        CreditCount = w;
        Sent += rest;

        if (ClearCount < Clears.Length)
        {
            ref var e = ref Clears[ClearCount++];
            e.Lines = n;
            e.Kind = (tspin ? 1 : 0) + (perfect ? 2 : 0);
            e.Combo = Combo;
            e.B2b = B2b;
            e.Attack = atk;
            e.Sent = rest;
            e.Tick = Tick;
        }
        Clearing = ClearTicks;
    }

    int Taken(int x, int y) => (uint)x >= W || (uint)y >= H || (Mask[y] & (1 << x)) != 0 ? 1 : 0;

    void RemoveFullRows()
    {
        var dst = 0;
        for (var y = 0; y < H; y++)
        {
            if (Mask[y] == FullRow) continue;
            if (dst != y)
            {
                Array.Copy(Cells, y * W, Cells, dst * W, W);
                Mask[dst] = Mask[y];
            }
            dst++;
        }
        for (var y = dst; y < H; y++)
        {
            Array.Clear(Cells, y * W, W);
            Mask[y] = 0;
        }
        CellsVer++;
        Ver++;
    }

    /// <summary>Скільки рядів сміття ще летить (усі посилки) і скільки з них уже дозріло.</summary>
    public int PendingRows
    {
        get
        {
            var sum = 0;
            for (var i = 0; i < CreditCount; i++) sum += Credits[i].Rows;
            return sum;
        }
    }

    public int RipeRows
    {
        get
        {
            var sum = 0;
            for (var i = 0; i < CreditCount; i++) if (Credits[i].RipeAt <= Tick) sum += Credits[i].Rows;
            return sum;
        }
    }

    /// <summary>
    /// Вставка дозрілого сміття (§2.6): найстаріші посилки першими, разом не більше <see cref="MaxInsert"/>;
    /// стіна зсувається вгору, найстаріші ряди — зверху нового шару (ніби влізли першими).
    /// </summary>
    void InsertGarbage()
    {
        var k = 0;
        for (var i = 0; i < CreditCount; i++)
        {
            _takeRows[i] = 0;
            if (Credits[i].RipeAt > Tick || k >= MaxInsert) continue;
            var take = Math.Min(MaxInsert - k, Credits[i].Rows);
            _takeRows[i] = take;
            k += take;
        }
        if (k == 0) return;

        var top = -1;
        for (var y = H - 1; y >= 0; y--) if (Mask[y] != 0) { top = y; break; }
        var over = top >= 0 && top + k >= H - 2;

        for (var y = H - 1; y >= k; y--)
        {
            Array.Copy(Cells, (y - k) * W, Cells, y * W, W);
            Mask[y] = Mask[y - k];
        }
        var row = k - 1;
        var w = 0;
        for (var i = 0; i < CreditCount; i++)
        {
            var c = Credits[i];
            for (var r = 0; r < _takeRows[i]; r++, row--)
            {
                var b = row * W;
                for (var x = 0; x < W; x++) Cells[b + x] = x == c.Hole ? (byte)0 : (byte)8;
                Mask[row] = (ushort)(FullRow & ~(1 << c.Hole));
            }
            c.Rows -= _takeRows[i];
            if (c.Rows > 0) Credits[w++] = c;
        }
        CreditCount = w;
        Recv += k;
        Inserted += k;
        CellsVer++;
        Ver++;
        if (over) TopOut(OutGarbage);
    }

    void TopOut(int why)
    {
        Alive = false;
        OutTick = Tick;
        OutWhy = why;
        Type = -1;
        Clearing = 0;
        Ver++;
    }

    /// <summary>Вибув не сам (встав з-за столу, кінець раунду без нього) — стіна сіріє.</summary>
    public void Retire(int why)
    {
        if (Alive) TopOut(why);
    }

    /// <summary>Посилка сміття в чергу стіни. Понад <see cref="MaxCredits"/> — ряди доливаються в останню.</summary>
    public void AddCredit(int g, int rows, int hole, int ripeAt, int from)
    {
        if (rows <= 0) return;
        Gseq = g;
        if (CreditCount == MaxCredits)
        {
            Credits[MaxCredits - 1].Rows += rows;
            Ver++;
            return;
        }
        ref var c = ref Credits[CreditCount++];
        c.G = g;
        c.Rows = rows;
        c.Hole = hole;
        c.RipeAt = ripeAt;
        c.From = from;
        Ver++;
    }

    // ---------- хеш і знімки ----------

    /// <summary>FNV-1a 32 біт (§5.3): 240 байтів клітинок, потім поля по 4 байти little-endian.</summary>
    public uint Hash()
    {
        var h = 2166136261u;
        for (var i = 0; i < Cells.Length; i++) h = unchecked((h ^ Cells[i]) * 16777619u);
        h = Mix(h, Type + 1);
        h = Mix(h, Bx + 8);
        h = Mix(h, By + 8);
        h = Mix(h, Rot);
        h = Mix(h, Pi);
        h = Mix(h, Hold + 1);
        h = Mix(h, HoldUsed ? 1 : 0);
        h = Mix(h, Lines);
        h = Mix(h, PendingRows);
        h = Mix(h, CreditCount);
        h = Mix(h, Combo + 1);
        h = Mix(h, B2b + 1);
        h = Mix(h, Tick);
        h = Mix(h, Clearing);
        h = Mix(h, Keys);
        h = Mix(h, DasT);
        h = Mix(h, LockT);
        h = Mix(h, Resets);
        h = Mix(h, GravT);
        h = Mix(h, LastRot ? 1 : 0);
        h = Mix(h, Dir + 1);
        h = Mix(h, LowY + 8);
        return h;
    }

    static uint Mix(uint h, int v)
    {
        var u = (uint)v;
        h = unchecked((h ^ (u & 0xFF)) * 16777619u);
        h = unchecked((h ^ ((u >> 8) & 0xFF)) * 16777619u);
        h = unchecked((h ^ ((u >> 16) & 0xFF)) * 16777619u);
        h = unchecked((h ^ (u >> 24)) * 16777619u);
        return h;
    }

    /// <summary>Знімок: копія всього стану (клітинки, фігурка, лічильники, посилки).</summary>
    public void CopyFrom(BricksCore o)
    {
        Array.Copy(o.Cells, Cells, Cells.Length);
        Array.Copy(o.Mask, Mask, Mask.Length);
        Type = o.Type; Bx = o.Bx; By = o.By; Rot = o.Rot;
        Seed = o.Seed; Pi = o.Pi; Hold = o.Hold; HoldUsed = o.HoldUsed;
        Keys = o.Keys; Dir = o.Dir; DasT = o.DasT; GravT = o.GravT; LockT = o.LockT; Resets = o.Resets; LowY = o.LowY;
        Clearing = o.Clearing; LastRot = o.LastRot;
        Combo = o.Combo; B2b = o.B2b; Lines = o.Lines; Sent = o.Sent; Recv = o.Recv;
        Tick = o.Tick; Seq = o.Seq;
        Array.Copy(o.Credits, Credits, o.CreditCount);
        CreditCount = o.CreditCount; Gseq = o.Gseq;
        Alive = o.Alive; OutTick = o.OutTick; OutWhy = o.OutWhy;
        Garbage = o.Garbage; StageTicks = o.StageTicks;
        CellsVer = o.CellsVer; Ver = o.Ver;
        Fours = o.Fours;
    }

    public BricksCore Clone()
    {
        var c = new BricksCore();
        c.CopyFrom(this);
        return c;
    }

    // ---------- на дріт ----------

    /// <summary>Ряди знизу вгору рядками з десяти цифр, без порожніх зверху; порожня стіна — [].</summary>
    public string[] WireRows()
    {
        var top = -1;
        for (var y = H - 1; y >= 0; y--) if (Mask[y] != 0) { top = y; break; }
        if (top < 0) return [];
        var rows = new string[top + 1];
        Span<char> line = stackalloc char[W];
        for (var y = 0; y <= top; y++)
        {
            for (var x = 0; x < W; x++) line[x] = (char)('0' + Cells[y * W + x]);
            rows[y] = new string(line);
        }
        return rows;
    }

    public int[]? WirePiece() => Type < 0 ? null : [Type, Bx, By, Rot];

    /// <summary>Три наступні фігурки черги.</summary>
    public int[] Next3() => [PieceAt(Pi), PieceAt(Pi + 1), PieceAt(Pi + 2)];

    /// <summary>Посилки на дріт: [g, rows, hole, ripeAt, from].</summary>
    public int[][] WireCredits()
    {
        var list = new int[CreditCount][];
        for (var i = 0; i < CreditCount; i++)
        {
            var c = Credits[i];
            list[i] = [c.G, c.Rows, c.Hole, c.RipeAt, c.From];
        }
        return list;
    }

    /// <summary>Ряди стіни з рядків «0033100777» знизу вгору (тести й стенд); решта стану не чіпається.</summary>
    public void LoadRows(IReadOnlyList<string> rows)
    {
        Array.Clear(Cells);
        Array.Clear(Mask);
        for (var y = 0; y < rows.Count && y < H; y++)
        {
            var s = rows[y];
            for (var x = 0; x < W && x < s.Length; x++)
            {
                var v = (byte)(s[x] - '0');
                Cells[y * W + x] = v;
                if (v != 0) Mask[y] |= (ushort)(1 << x);
            }
        }
        CellsVer++;
        Ver++;
    }

    /// <summary>
    /// Стіна з <c>BoardWire</c> (§4.1) — те, що робить клієнт після виправлення чи F5. На сервері не потрібне
    /// (сервер свою стіну ніколи не підганяє), але тест перевіряє, що дріт несе повний стан: хеш той самий.
    /// </summary>
    public void LoadWire(JsonElement w, uint seed, int garbage, int stageTicks)
    {
        Array.Clear(Cells);
        Array.Clear(Mask);
        Seed = seed == 0 ? 1 : seed;
        Garbage = garbage;
        StageTicks = stageTicks;
        _bagN = -1;
        var y = 0;
        foreach (var row in w.GetProperty("r").EnumerateArray())
        {
            var s = row.GetString() ?? "";
            for (var x = 0; x < W && x < s.Length; x++)
            {
                var v = (byte)(s[x] - '0');
                Cells[y * W + x] = v;
                if (v != 0) Mask[y] |= (ushort)(1 << x);
            }
            y++;
        }
        if (w.GetProperty("p") is { ValueKind: JsonValueKind.Array } p)
        {
            Type = p[0].GetInt32(); Bx = p[1].GetInt32(); By = p[2].GetInt32(); Rot = p[3].GetInt32();
        }
        else Type = -1;
        Alive = w.GetProperty("a").GetInt32() == 1;
        Lines = w.GetProperty("l").GetInt32();
        Sent = w.GetProperty("sn").GetInt32();
        Recv = w.GetProperty("rc").GetInt32();
        Tick = w.GetProperty("k").GetInt32();
        Seq = w.GetProperty("q").GetInt32();
        Gseq = w.GetProperty("g").GetInt32();
        Hold = w.GetProperty("hd").GetInt32();
        HoldUsed = w.GetProperty("hu").GetInt32() == 1;
        Pi = w.GetProperty("pi").GetInt32();
        Combo = w.GetProperty("cb").GetInt32();
        B2b = w.GetProperty("bb").GetInt32();
        Keys = w.GetProperty("ky").GetInt32();
        Dir = w.GetProperty("dr").GetInt32();
        DasT = w.GetProperty("ds").GetInt32();
        GravT = w.GetProperty("gt").GetInt32();
        LockT = w.GetProperty("lt").GetInt32();
        Resets = w.GetProperty("rs").GetInt32();
        LowY = w.GetProperty("ly").GetInt32();
        Clearing = w.GetProperty("cl").GetInt32();
        LastRot = w.GetProperty("lr").GetInt32() == 1;
        CreditCount = 0;
        foreach (var c in w.GetProperty("cr").EnumerateArray())
        {
            if (CreditCount == MaxCredits) break;
            ref var cr = ref Credits[CreditCount++];
            cr.G = c[0].GetInt32(); cr.Rows = c[1].GetInt32(); cr.Hole = c[2].GetInt32(); cr.RipeAt = c[3].GetInt32(); cr.From = c[4].GetInt32();
        }
        OutTick = Alive ? -1 : Tick;
    }
}
