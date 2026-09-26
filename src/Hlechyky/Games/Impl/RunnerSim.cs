namespace Hlechyky.Games.Impl;

// ============================================================================================
// Спільний рушій пакета «runner»: Стрибозаври (dino), Забіг дня (dino-daily), Лелеки (storks).
//
// Уся симуляція цілочисельна (суб-пікселі, Sub = 16) і ДОСЛІВНО повторена в web/games/runner.js
// (RunnerSim там): клієнт передбачає свого героя тим самим кодом, а сервер лишається суддею й
// перемотує запізнілий ввід. Будь-яка правка тут — така сама правка там, інакше хеш парності
// (RunnerSimTests.Simulation_hash_matches_the_javascript_reference_for_every_fixture) упаде.
//
// Правила для обох мов: лише int, + − ×, ділення невід'ємних (у JS — Math.floor), порівняння.
// Жодних double, % на від'ємних, округлень. Специфікація — docs/games/specs/dino.md §2, §5.
// ============================================================================================

/// <summary>Режим рушія: біг від лавини по кризі чи політ над селом.</summary>
public enum RunnerMode { Dino = 0, Storks = 1 }

/// <summary>xorshift32 — той самий генератор, що в JS: курс з одного зерна однаковий обабіч дроту.</summary>
public struct RunnerRng
{
    uint _x;

    public RunnerRng(int seed)
    {
        _x = unchecked((uint)seed);
        if (_x == 0) _x = 0x9E3779B9;
    }

    public uint NextU32()
    {
        var x = _x;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _x = x;
        return x;
    }

    public int Next(int n) => (int)(NextU32() % (uint)n);
}

/// <summary>
/// Скільки кроків по 20 мс зробити на цьому тику, щоб годинник гри йшов за стінним. Кімната тикає «не раніше
/// ніж за 40 мс», а таймер Windows грубий (15,6 мс), тож насправді тик приходить за 45–55 мс: із рівно двома
/// кроками на тик гра йшла б на ~80 % задуманої швидкості, а годинник клієнта (крок = 20 мс) весь час
/// утікав би вперед. Тому кроків стільки, скільки набіг час (2–4), залишок мілісекунд переходить на наступний
/// тик. Понад 4 кроки (сервер спав) не надолужуємо — як і каркас із пропущеними тиками.
/// У тестах годинник рухається рівно на 40 мс — там завжди два кроки.
/// </summary>
public struct RunnerPacer
{
    public const int MaxSteps = 4;
    DateTimeOffset? _last;
    double _carry;

    public void Reset() { _last = null; _carry = 0; }

    public int Due(DateTimeOffset now)
    {
        if (_last is not { } last)
        {
            _last = now;
            _carry = 0;
            return RunnerSim.StepsPerTick;
        }
        _last = now;
        var ms = (now - last).TotalMilliseconds + _carry;
        var n = (int)(ms / RunnerSim.StepMs);
        if (n < 1) n = 1;
        if (n > MaxSteps)
        {
            n = MaxSteps;
            _carry = 0;
        }
        else _carry = Math.Max(0, ms - n * RunnerSim.StepMs);
        return n;
    }
}

/// <summary>Коди перешкод, подій курсу й підбирачок (ті самі числа в JS і в кадрі).</summary>
public static class RunnerKind
{
    // перешкоди Стрибозаврів
    public const int Low = 1, Low2 = 2, High = 3, Wide = 4, Icicle = 5, Ptero = 6, Pit = 7, Hill = 8, Snow = 9;
    // перешкоди Лелек
    public const int Chimney = 20, Tree = 21, Pole = 22, Wire = 23, Nest = 24, Kite = 25;
    // події курсу, яких нема серед перешкод
    public const int Combo = 10, Gate = 26, Chimney2 = 27;
    // підбирачки
    public const int Egg = 1, Pepper = 2, SnowBall = 3;

    /// <summary>Об що можна зачепитись: яма й схил — це земля, а не перешкода.</summary>
    public static bool Solid(int kind) => kind != Pit && kind != Hill;
}

/// <summary>Сталі фізики Стрибозаврів (суб, кроки). Числа — dino.md §2.3–2.6.</summary>
public static class RunnerDino
{
    public const int JumpV = 160, G = 14, GHold = 6, HoldMax = 14, FastFall = -160, VyMin = -400;
    public const int CoyoteSteps = 2, BufferSteps = 3;
    public const int HitW = 30 * 16, HitH = 44 * 16, DuckW = 36 * 16, DuckH = 24 * 16, FootX = 15 * 16;
    public const int StunSteps = 30, PitStun = 50, PitDepth = 384, PitOut = 16 * 16;
    public const int StunMult = 40, BoostMult = 125, RecoverMult = 108, BoostSteps = 100;
    public const int LagMin = -1280;
    public const int AvD0 = 8960, AvDmin = 3200;
    public const int PteroV = 64, PteroWake = 480 * 16;
    public const int SnowAhead = 320 * 16, SnowW = 26 * 16, SnowH = 26 * 16, SnowRoom = 100 * 16, SnowShift = 40 * 16, SnowTries = 10;
    public const int PickBox = 24 * 16, AirEgg = 104 * 16, TopEgg = 66 * 16;
    public const int PickChance = 22;
    /// <summary>Скільки суб в одному метрі: 16 суб × 20 px.</summary>
    public const int SubPerMetre = 320;
}

/// <summary>Сталі польоту Лелек (storks.md §2.2).</summary>
public static class RunnerStorks
{
    public const int G = 8, Flap = 128, VyMin = -200;
    public const int StartY = 150 * 16, Ceiling = 268 * 16, Sky = 290;
    public const int HitW = 32 * 16, HitH = 22 * 16;
    public const int IFrames = 40, MinYAfterHit = 10 * 16;
    public const int GroundId = -2;
}

/// <summary>Темп, генератор і густина курсу — свої для кожного режиму.</summary>
public sealed class RunnerRules
{
    public required RunnerMode Mode { get; init; }
    public required int Speed0 { get; init; }
    public required int SpeedMax { get; init; }
    public required int Ramp { get; init; }
    /// <summary>Де перша подія від старту бігу (суб).</summary>
    public required int FirstAt { get; init; }
    public required int Lookahead { get; init; }
    /// <summary>Що позаду лінії темпу далі за це — викидаємо.</summary>
    public required int Behind { get; init; }
    public required int Gmin0 { get; init; }
    public required int GminDrop { get; init; }
    public required int Gmax0 { get; init; }
    public required int GmaxDrop { get; init; }
    /// <summary>Подія, після якої проміжок довший (яма / ворота), і на скільки кроків.</summary>
    public required int SpecialKind { get; init; }
    public required int SpecialGap { get; init; }
    /// <summary>Події в порядку ваг.</summary>
    public required int[] Events { get; init; }
    /// <summary>Ваги подій по смугах прогресу B0…B4.</summary>
    public required int[][] Weights { get; init; }

    public static readonly RunnerRules Dino = new()
    {
        Mode = RunnerMode.Dino, Speed0 = 112, SpeedMax = 224, Ramp = 32,
        FirstAt = 900 * 16, Lookahead = 1600 * 16, Behind = RunnerDino.AvD0 + 1600 * 16,
        Gmin0 = 60, GminDrop = 28, Gmax0 = 100, GmaxDrop = 50,
        SpecialKind = RunnerKind.Pit, SpecialGap = 10,
        Events = [RunnerKind.Low, RunnerKind.Low2, RunnerKind.High, RunnerKind.Wide, RunnerKind.Icicle, RunnerKind.Ptero, RunnerKind.Pit, RunnerKind.Hill, RunnerKind.Combo],
        Weights =
        [
            [40, 10, 8, 6, 0, 0, 0, 6, 0],
            [32, 14, 12, 8, 10, 0, 8, 6, 0],
            [26, 16, 14, 10, 12, 8, 8, 6, 0],
            [22, 16, 16, 12, 12, 10, 8, 4, 10],
            [18, 16, 16, 12, 12, 12, 8, 4, 10],
        ],
    };

    public static readonly RunnerRules Storks = new()
    {
        Mode = RunnerMode.Storks, Speed0 = 96, SpeedMax = 192, Ramp = 40,
        FirstAt = 700 * 16, Lookahead = 1600 * 16, Behind = 1200 * 16,
        Gmin0 = 55, GminDrop = 25, Gmax0 = 90, GmaxDrop = 40,
        SpecialKind = RunnerKind.Gate, SpecialGap = 8,
        Events = [RunnerKind.Chimney, RunnerKind.Tree, RunnerKind.Gate, RunnerKind.Pole, RunnerKind.Nest, RunnerKind.Kite, RunnerKind.Chimney2],
        Weights =
        [
            [40, 20, 0, 14, 10, 0, 0],
            [34, 18, 14, 14, 10, 0, 10],
            [28, 16, 20, 14, 10, 12, 0],
            [24, 14, 24, 14, 10, 14, 0],
            [22, 14, 26, 12, 10, 16, 0],
        ],
    };

    public static RunnerRules For(RunnerMode mode) => mode == RunnerMode.Dino ? Dino : Storks;

    /// <summary>Темп на кроці бігу: +1 суб/крок кожні Ramp кроків, до стелі.</summary>
    public int Speed(int run) => Math.Min(SpeedMax, Speed0 + run / Ramp);

    /// <summary>Лінія темпу — Σ speed(i) для i &lt; run закритою формулою (dino.md §5.2).</summary>
    public int PaceX(int run)
    {
        if (run <= 0) return 0;
        var k = run / Ramp;
        var kMax = SpeedMax - Speed0;
        if (k <= kMax) return Ramp * (k * Speed0 + k * (k - 1) / 2) + (run - k * Ramp) * (Speed0 + k);
        return Ramp * (kMax * Speed0 + kMax * (kMax - 1) / 2) + (run - kMax * Ramp) * SpeedMax;
    }

    /// <summary>Перший крок бігу, на якому лінія темпу дійшла до x (двійковий пошук по закритій формулі).</summary>
    public int RunAt(int x)
    {
        if (x <= 0) return 0;
        int lo = 0, hi = 1 << 22;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (PaceX(mid) >= x) hi = mid; else lo = mid + 1;
        }
        return lo;
    }

    /// <summary>Лавина позаду лінії темпу (лише Стрибозаври).</summary>
    public static int AvD(int run) => Math.Max(RunnerDino.AvDmin, RunnerDino.AvD0 - run * 4 / 5);
}

/// <summary>Перешкода або частина рельєфу (схил, яма). Усе в суб.</summary>
public struct RunnerObstacle
{
    public int Id, Kind, X, W, Base, H, Since, By;

    /// <summary>Птеродактиль летить назустріч, щойно лінія темпу підійшла на 480 px.</summary>
    public readonly int XAt(int run) => Kind == RunnerKind.Ptero && run > Since ? X - RunnerDino.PteroV * (run - Since) : X;

    /// <summary>Повітряний змій гойдається трикутником ±16 px за 80 кроків.</summary>
    public readonly int BaseAt(int run)
    {
        if (Kind != RunnerKind.Kite) return Base;
        var ph = run % 80;
        return Base + ((ph < 40 ? ph : 80 - ph) - 20) * 13;
    }
}

/// <summary>Підбирачка (яйце, перчик, сніжка): коробка 24×24 px, у кожного гравця своя.</summary>
public struct RunnerPickup
{
    public int Id, Kind, X, Base;
}

/// <summary>Подія для ефектів кадру (ev): спотик, яма, підібрав, кинув, вибув.</summary>
public struct RunnerEvent
{
    public const int Hit = 1, Pit = 2, Egg = 3, Pepper = 4, SnowTake = 5, Throw = 6, Out = 7;
    public int Kind, Seat, A, B;
}

/// <summary>
/// Стан одного бігуна чи лелеки. Знімок для перемотування — копія цього ж класу (CopyFrom), без алокацій.
/// </summary>
public sealed class RunnerPlayer
{
    public bool Plays;
    /// <summary>Вибув і отримав місце (після звичайного кроку, не під час перемотування).</summary>
    public bool Out;
    /// <summary>Лелека зачепилась без пір'я — фізика стала; місце дасть найближчий звичайний крок.</summary>
    public bool Down;
    public int Lag, Y, Vy, Hold, Buffer, Coyote, Stun, Boost, Eggs, Snow, Place, Held, Hits, Ifr;
    /// <summary>Id підбирачки-сніжки, що зараз у руці (щоб перемотування не підняло кинуту вдруге).</summary>
    public int SnowId;
    public bool Air, Duck, HoldOn, Feather;
    /// <summary>Id останньої пройденої (зачепленої) перешкоди й останньої взятої підбирачки; −1 — ще нема.</summary>
    public int Lp = -1, Lt = -1;
    public readonly int[] Passed = [int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue];
    public readonly int[] Taken = [int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue];
    public int PassedN, TakenN;

    public void CopyFrom(RunnerPlayer o)
    {
        Plays = o.Plays; Out = o.Out; Down = o.Down;
        Lag = o.Lag; Y = o.Y; Vy = o.Vy; Hold = o.Hold; Buffer = o.Buffer; Coyote = o.Coyote; Stun = o.Stun; Boost = o.Boost;
        Eggs = o.Eggs; Snow = o.Snow; Place = o.Place; Held = o.Held; Hits = o.Hits; Ifr = o.Ifr; SnowId = o.SnowId;
        Air = o.Air; Duck = o.Duck; HoldOn = o.HoldOn; Feather = o.Feather;
        Lp = o.Lp; Lt = o.Lt;
        Array.Copy(o.Passed, Passed, 8);
        Array.Copy(o.Taken, Taken, 8);
        PassedN = o.PassedN; TakenN = o.TakenN;
    }

    public bool HasPassed(int id)
    {
        var a = Passed;
        for (var i = 0; i < 8; i++) if (a[i] == id) return true;
        return false;
    }

    public bool HasTaken(int id)
    {
        var a = Taken;
        for (var i = 0; i < 8; i++) if (a[i] == id) return true;
        return false;
    }

    public void AddPassed(int id)
    {
        Passed[PassedN & 7] = id;
        PassedN++;
    }

    public void AddTaken(int id)
    {
        Taken[TakenN & 7] = id;
        TakenN++;
    }

    /// <summary>Режим для кадру: 0 біг, 1 у повітрі, 2 пригнувся, 3 спотик, 4 вибув (у Лелек 1 летить, 3 зачеп, 4 на землі).</summary>
    public int ModeOf(RunnerMode mode)
    {
        if (mode == RunnerMode.Storks) return Down || Out ? 4 : Ifr > 0 ? 3 : 1;
        if (Out) return 4;
        if (Stun > 0) return 3;
        if (Air) return 1;
        return Duck ? 2 : 0;
    }

    /// <summary>Решта стану Стрибозавра одним числом — щоб звірка клієнта була точною, а не «майже».</summary>
    public int Hidden => (Air ? 1 : 0) | (Duck ? 2 : 0) | (HoldOn ? 4 : 0) | (Hold << 3) | (Buffer << 8) | (Coyote << 10);
}

/// <summary>
/// Світ одного раунду: курс від зерна, лінія темпу, до восьми гравців, журнали вводу й знімки для
/// перемотування. Кроки йдуть по 20 мс, кімната робить два на тик. Жодних алокацій у <see cref="Step"/>.
/// </summary>
public sealed class RunnerSim
{
    public const int Sub = 16, StepMs = 20, StepsPerTick = 2, TickMs = 40, Seats = 8;
    public const int FutureMax = 8, RewindMax = 15;
    public const int NoGround = int.MinValue;
    /// <summary>Кинуті сніжки мають свою нумерацію: клієнт не знає про кидки наперед, а курс мусить збігатись.</summary>
    public const int SnowIdBase = 1 << 20;
    const int SnapN = 16, LogN = 64, ObN = 64, PkN = 32, SnN = 16, EvN = 64;

    public readonly RunnerMode Mode;
    public readonly RunnerRules Rules;
    public readonly int Seed, ReadySteps, PmCap;
    public readonly bool SnowOn;
    public readonly RunnerPlayer[] P = new RunnerPlayer[Seats];

    /// <summary>Скільки кроків зроблено від початку раунду (з відліком); наступний крок має номер S.</summary>
    public int S { get; private set; }

    // курс
    readonly RunnerObstacle[] _ob = new RunnerObstacle[ObN];
    int _obHead, _obCount;
    readonly RunnerPickup[] _pk = new RunnerPickup[PkN];
    int _pkHead, _pkCount;
    readonly RunnerObstacle[] _sn = new RunnerObstacle[SnN];
    int _snCount, _snNext;
    RunnerRng _rng;
    int _cursor, _prev1 = -1, _prev2 = -1, _nextOb = 1, _nextPk = 1;
    int _pitIdx;

    // ввід і знімки
    readonly int[][] _held = new int[Seats][], _stamp = new int[Seats][];
    readonly bool[][] _edge = new bool[Seats][], _expl = new bool[Seats][];
    readonly RunnerPlayer[][] _snap = new RunnerPlayer[Seats][];
    readonly int[][] _snapAt = new int[Seats][];
    readonly bool[] _gone = new bool[Seats];

    // події для кадру
    readonly RunnerEvent[] _ev = new RunnerEvent[EvN];
    int _evCount;
    bool _rewinding;
    readonly int[] _prePassed = new int[8], _preTaken = new int[8];

    public bool Hashing;
    public uint Hash = 2166136261;

    public RunnerSim(RunnerMode mode, int seed, bool[] plays, int readySteps, int pmCap, bool snowOn, bool featherOn = true, bool hashing = false)
    {
        Mode = mode;
        Rules = RunnerRules.For(mode);
        Seed = seed;
        ReadySteps = readySteps;
        PmCap = pmCap;
        SnowOn = snowOn;
        Hashing = hashing;
        for (var i = 0; i < Seats; i++)
        {
            P[i] = new RunnerPlayer
            {
                Plays = i < plays.Length && plays[i],
                Feather = mode == RunnerMode.Storks && featherOn,
                Y = mode == RunnerMode.Storks ? RunnerStorks.StartY : 0,
            };
            _held[i] = new int[LogN];
            _stamp[i] = new int[LogN];
            _edge[i] = new bool[LogN];
            _expl[i] = new bool[LogN];
            Array.Fill(_stamp[i], -1);
            _snap[i] = new RunnerPlayer[SnapN];
            for (var k = 0; k < SnapN; k++) _snap[i][k] = new RunnerPlayer();
            _snapAt[i] = new int[SnapN];
            Array.Fill(_snapAt[i], -1);
        }
        _rng = new RunnerRng(seed);
        _cursor = Rules.FirstAt;
        Generate(0);
    }

    // ---------------------------------------------------------------------------------------------
    // світ
    // ---------------------------------------------------------------------------------------------

    /// <summary>Крок бігу для кроку раунду: мінус — ще відлік.</summary>
    public int RunOf(int step) => step - ReadySteps;

    public int Run => Math.Max(0, S - ReadySteps);

    public int Speed(int run) => Rules.Speed(run);

    public int PaceX(int run) => Rules.PaceX(run);

    /// <summary>Перешкоди у вікні (кільце, у порядку генерації).</summary>
    public int ObstacleCount => _obCount;

    public ref readonly RunnerObstacle Obstacle(int i) => ref _ob[(_obHead + i) & (ObN - 1)];

    public int PickupCount => _pkCount;

    public ref readonly RunnerPickup Pickup(int i) => ref _pk[(_pkHead + i) & (PkN - 1)];

    public int SnowCount => _snCount;

    public ref readonly RunnerObstacle SnowBlock(int i) => ref _sn[i];

    public int EventCount => _evCount;

    public ref readonly RunnerEvent Event(int i) => ref _ev[i];

    public void ClearEvents() => _evCount = 0;

    /// <summary>Висота землі під x (суб): 0 рівно, схил — лінійно, над ямою — <see cref="NoGround"/>.</summary>
    public int GroundAt(int x)
    {
        for (var i = 0; i < _obCount; i++)
        {
            ref var o = ref _ob[(_obHead + i) & (ObN - 1)];
            if (o.Kind == RunnerKind.Pit)
            {
                if (x >= o.X && x < o.X + o.W) { _pitIdx = (_obHead + i) & (ObN - 1); return NoGround; }
            }
            else if (o.Kind == RunnerKind.Hill && x >= o.X && x < o.X + o.W)
            {
                var d = x - o.X;
                if (d < 160 * 16) return 640 * d / 2560;
                if (d < 260 * 16) return 640;
                return 640 * (o.X + o.W - x) / 2560;
            }
        }
        return 0;
    }

    void Generate(int run)
    {
        var pace = Rules.PaceX(run);
        var lim = pace - Rules.Behind;
        while (_obCount > 0 && _ob[_obHead].X + _ob[_obHead].W < lim)
        {
            _obHead = (_obHead + 1) & (ObN - 1);
            _obCount--;
        }
        while (_pkCount > 0 && _pk[_pkHead].X + RunnerDino.PickBox < lim)
        {
            _pkHead = (_pkHead + 1) & (PkN - 1);
            _pkCount--;
        }
        if (_snCount > 0)
        {
            var w = 0;
            for (var r = 0; r < _snCount; r++)
                if (_sn[r].X + _sn[r].W >= lim) _sn[w++] = _sn[r];
            _snCount = w;
        }
        while (_cursor < pace + Rules.Lookahead) AddEvent(run);
    }

    void AddEvent(int run)
    {
        var pm = Math.Min(PmCap, run * 1000 / 4500);
        var sp = Rules.Speed(run);
        var kind = Pick(pm);
        EventLog?.Add(kind);
        var x = _cursor;
        var width = Place(kind, x, pm);
        var gmin = Rules.Gmin0 - Rules.GminDrop * pm / 1000;
        var gmax = Rules.Gmax0 - Rules.GmaxDrop * pm / 1000;
        var gs = gmin + _rng.Next(gmax - gmin + 1);
        if (kind == Rules.SpecialKind && gs < gmin + Rules.SpecialGap) gs = gmin + Rules.SpecialGap;
        GapLog?.Add(gs);
        var gap = gs * sp;
        var end = x + width;
        if (Mode == RunnerMode.Dino && _rng.Next(100) < RunnerDino.PickChance) AddPickup(kind, x, end, gap);
        _cursor = end + gap;
        _prev2 = _prev1;
        _prev1 = kind;
    }

    int Pick(int pm)
    {
        var band = pm < 150 ? 0 : pm < 300 ? 1 : pm < 500 ? 2 : pm < 800 ? 3 : 4;
        var w = Rules.Weights[band];
        var i = Roll(w);
        if (Bad(Rules.Events[i]))
        {
            i = Roll(w);
            if (Bad(Rules.Events[i]))
                for (var k = 0; k < w.Length; k++)
                    if (w[k] > 0 && !Bad(Rules.Events[k])) { i = k; break; }
        }
        return Rules.Events[i];
    }

    int Roll(int[] w)
    {
        var total = 0;
        for (var k = 0; k < w.Length; k++) total += w[k];
        var r = _rng.Next(total);
        for (var k = 0; k < w.Length; k++)
        {
            r -= w[k];
            if (r < 0) return k;
        }
        return w.Length - 1;
    }

    /// <summary>Та сама подія тричі поспіль, яма чи схил одразу за схилом, змій поруч із кроною — перебір.</summary>
    bool Bad(int kind)
    {
        if (kind == _prev1 && kind == _prev2) return true;
        if (Mode == RunnerMode.Dino) return _prev1 == RunnerKind.Hill && (kind == RunnerKind.Hill || kind == RunnerKind.Pit);
        return (_prev1 == RunnerKind.Tree && kind == RunnerKind.Kite) || (_prev1 == RunnerKind.Kite && kind == RunnerKind.Tree);
    }

    /// <summary>Ставить перешкоди події з лівим краєм x; повертає ширину події (суб).</summary>
    int Place(int kind, int x, int pm)
    {
        const int u = Sub;
        switch (kind)
        {
            // ---- Стрибозаври ----
            case RunnerKind.Low: Add(RunnerKind.Low, x, 34 * u, 0, 30 * u); return 34 * u;
            case RunnerKind.Low2: Add(RunnerKind.Low2, x, 68 * u, 0, 30 * u); return 68 * u;
            case RunnerKind.High: Add(RunnerKind.High, x, 34 * u, 0, 64 * u); return 34 * u;
            case RunnerKind.Wide: Add(RunnerKind.Wide, x, 100 * u, 0, 30 * u); return 100 * u;
            case RunnerKind.Icicle: Add(RunnerKind.Icicle, x, 30 * u, 30 * u, 270 * u); return 30 * u;
            case RunnerKind.Ptero:
            {
                var px = x + 160 * u;
                Add(RunnerKind.Ptero, px, 40 * u, 30 * u, 20 * u, Rules.RunAt(px - RunnerDino.PteroWake));
                return 200 * u;
            }
            case RunnerKind.Pit:
            {
                var w = (60 + 40 * Math.Min(pm, 1000) / 1000) * u;
                Add(RunnerKind.Pit, x, w, 0, 0);
                return w;
            }
            case RunnerKind.Hill:
                Add(RunnerKind.Hill, x, 420 * u, 0, 40 * u);
                if (pm >= 300) Add(RunnerKind.Low, x + 193 * u, 34 * u, 40 * u, 30 * u);
                return 420 * u;
            case RunnerKind.Combo:
                Add(RunnerKind.Low, x, 34 * u, 0, 30 * u);
                Add(RunnerKind.Icicle, x + 164 * u, 30 * u, 30 * u, 270 * u);
                return 194 * u;

            // ---- Лелеки ----
            case RunnerKind.Chimney:
            {
                var h = 80 + _rng.Next(91);
                Add(RunnerKind.Chimney, x, 40 * u, 0, h * u);
                return 40 * u;
            }
            case RunnerKind.Tree:
            {
                var h = 70 + _rng.Next(61);
                Add(RunnerKind.Tree, x, 60 * u, (RunnerStorks.Sky - h) * u, h * u);
                return 60 * u;
            }
            case RunnerKind.Gate:
            {
                var h1 = 60 + _rng.Next(71);
                var gapH = 150 - 50 * pm / 1000;
                var tb = h1 + gapH;
                Add(RunnerKind.Chimney, x, 44 * u, 0, h1 * u);
                Add(RunnerKind.Tree, x, 44 * u, tb * u, (RunnerStorks.Sky - tb) * u);
                return 44 * u;
            }
            case RunnerKind.Pole:
            {
                var h = 110 + _rng.Next(81);
                Add(RunnerKind.Wire, x, 120 * u, (h - 6) * u, 6 * u);
                Add(RunnerKind.Pole, x + 53 * u, 14 * u, 0, h * u);
                return 120 * u;
            }
            case RunnerKind.Nest:
            {
                var h = 100 + _rng.Next(61);
                Add(RunnerKind.Nest, x, 50 * u, h * u, 26 * u);
                Add(RunnerKind.Pole, x + 18 * u, 14 * u, 0, h * u);
                return 50 * u;
            }
            case RunnerKind.Kite:
            {
                var hk = 110 + _rng.Next(101);
                Add(RunnerKind.Kite, x, 36 * u, hk * u - 288, 36 * u);
                return 36 * u;
            }
            default: // Chimney2
            {
                var h1 = 80 + _rng.Next(71);
                var h2 = 80 + _rng.Next(71);
                Add(RunnerKind.Chimney, x, 40 * u, 0, h1 * u);
                Add(RunnerKind.Chimney, x + 90 * u, 40 * u, 0, h2 * u);
                return 130 * u;
            }
        }
    }

    void Add(int kind, int x, int w, int bas, int h, int since = 0)
    {
        if (_obCount == ObN)
        {
            _obHead = (_obHead + 1) & (ObN - 1);
            _obCount--;
        }
        ref var o = ref _ob[(_obHead + _obCount) & (ObN - 1)];
        o = new RunnerObstacle { Id = _nextOb++, Kind = kind, X = x, W = w, Base = bas, H = h, Since = since, By = -1 };
        _obCount++;
        if (Hashing) { H(0x0B); H(o.Id); H(kind); H(x); H(bas); H(h); }
    }

    void AddPickup(int eventKind, int x, int end, int gap)
    {
        var roll = _rng.Next(100);
        var kind = roll < 50 ? RunnerKind.Egg : roll < 80 ? RunnerKind.Pepper : RunnerKind.SnowBall;
        if (kind == RunnerKind.SnowBall && !SnowOn) kind = RunnerKind.Egg;
        int px, pb;
        if (kind == RunnerKind.Egg && eventKind == RunnerKind.High)
        {
            px = x + 5 * Sub;
            pb = RunnerDino.TopEgg;
        }
        else
        {
            px = end + gap / 2 - 12 * Sub;
            pb = kind == RunnerKind.Egg && _rng.Next(2) == 0 ? RunnerDino.AirEgg : 0;
        }
        if (_pkCount == PkN)
        {
            _pkHead = (_pkHead + 1) & (PkN - 1);
            _pkCount--;
        }
        ref var k = ref _pk[(_pkHead + _pkCount) & (PkN - 1)];
        k = new RunnerPickup { Id = _nextPk++, Kind = kind, X = px, Base = pb };
        _pkCount++;
        if (Hashing) { H(0x0C); H(k.Id); H(kind); H(px); H(pb); }
    }

    /// <summary>
    /// Кинута сніжка: брила лягає в x, а якщо поруч (ближче 100 px) інша перешкода, яма чи схил — зсувається
    /// вперед по 40 px, до десяти разів. Повертає id брили.
    /// </summary>
    public int PlaceSnow(int x, int by, int run)
    {
        for (var tries = 0; tries < RunnerDino.SnowTries && Crowded(x, run); tries++) x += RunnerDino.SnowShift;
        if (_snCount == SnN)
        {
            Array.Copy(_sn, 1, _sn, 0, SnN - 1);
            _snCount--;
        }
        var id = SnowIdBase + _snNext++;
        _sn[_snCount++] = new RunnerObstacle
        {
            Id = id, Kind = RunnerKind.Snow, X = x, W = RunnerDino.SnowW, Base = 0, H = RunnerDino.SnowH, Since = run, By = by,
        };
        if (Hashing) { H(0x0D); H(id); H(x); H(by); H(run); }
        Emit(RunnerEvent.Throw, by, x, id);
        return id;
    }

    bool Crowded(int x, int run)
    {
        var a = x - RunnerDino.SnowRoom;
        var b = x + RunnerDino.SnowW + RunnerDino.SnowRoom;
        for (var i = 0; i < _obCount; i++)
        {
            ref var o = ref _ob[(_obHead + i) & (ObN - 1)];
            var ox = o.XAt(run);
            if (a < ox + o.W && b > ox) return true;
        }
        for (var i = 0; i < _snCount; i++)
            if (a < _sn[i].X + _sn[i].W && b > _sn[i].X) return true;
        return false;
    }

    // ---------- для тестів і стенда: порожня криза й перешкоди руками ----------

    /// <summary>Якщо не null — сюди пишуться події курсу по черзі (тести правил генерації).</summary>
    public List<int>? EventLog;

    /// <summary>Якщо не null — проміжки після подій у кроках (тести густини).</summary>
    public List<int>? GapLog;

    /// <summary>Прибрати весь курс і більше нічого не генерувати: тест ставить перешкоди сам.</summary>
    public void ClearCourse()
    {
        _obCount = 0;
        _pkCount = 0;
        _snCount = 0;
        _cursor = int.MaxValue / 2;
    }

    /// <summary>Поставити перешкоду (суб); повертає її id.</summary>
    public int Put(int kind, int x, int w, int bas, int h, int since = 0)
    {
        Add(kind, x, w, bas, h, since);
        return _nextOb - 1;
    }

    public int PutPickup(int kind, int x, int bas)
    {
        if (_pkCount == PkN)
        {
            _pkHead = (_pkHead + 1) & (PkN - 1);
            _pkCount--;
        }
        _pk[(_pkHead + _pkCount) & (PkN - 1)] = new RunnerPickup { Id = _nextPk++, Kind = kind, X = x, Base = bas };
        _pkCount++;
        return _nextPk - 1;
    }

    // ---------------------------------------------------------------------------------------------
    // крок
    // ---------------------------------------------------------------------------------------------

    /// <summary>Один крок 20 мс: курс, кожен гравець зі свого журналу вводу, вибування.</summary>
    public void Step()
    {
        var t = S;
        var run = t - ReadySteps;
        if (run >= 0) Generate(run);
        for (var i = 0; i < Seats; i++)
        {
            var p = P[i];
            if (!p.Plays) continue;
            Entry(i, t);
            var snap = _snap[i][t & (SnapN - 1)];
            snap.CopyFrom(p);
            _snapAt[i][t & (SnapN - 1)] = t;
            StepPlayer(i, p, t, run);
            p.Held = _held[i][t & (LogN - 1)];
        }
        S = t + 1;
        if (run >= 0) Eliminate(run);
        if (Hashing) HashStep(run);
    }

    void StepPlayer(int seat, RunnerPlayer p, int t, int run)
    {
        if (run < 0) return;   // відлік: світ стоїть
        var j = t & (LogN - 1);
        if (Mode == RunnerMode.Dino) StepDino(seat, p, run, _held[seat][j], _edge[seat][j]);
        else StepStork(seat, p, run, _edge[seat][j]);
    }

    void StepDino(int seat, RunnerPlayer p, int run, int held, bool edge)
    {
        var sp = Rules.Speed(run);
        if (p.Out)
        {
            p.Lag += sp;
            return;
        }
        var mult = p.Stun > 0 ? RunnerDino.StunMult : p.Boost > 0 ? RunnerDino.BoostMult : p.Lag > 0 ? RunnerDino.RecoverMult : 100;
        var fwd = sp * mult / 100;
        p.Lag += sp - fwd;
        if (p.Lag < RunnerDino.LagMin) p.Lag = RunnerDino.LagMin;
        var worldX = Rules.PaceX(run + 1) - p.Lag;
        if (p.Stun > 0) p.Stun--;
        if (p.Boost > 0) p.Boost--;
        var gx = worldX + RunnerDino.FootX;

        if (p.Stun == 0)
        {
            if (!p.Air)
            {
                if (edge || p.Buffer > 0) Jump(p, held);
                else p.Duck = (held & 2) != 0;
            }
            else
            {
                if (p.Buffer > 0) p.Buffer--;
                if (edge)
                {
                    if (p.Coyote > 0) Jump(p, held);
                    else p.Buffer = RunnerDino.BufferSteps;
                }
                if (p.Coyote > 0) p.Coyote--;
            }
            if (p.Air)
            {
                var heldUp = p.HoldOn && (held & 1) != 0 && p.Hold < RunnerDino.HoldMax && p.Vy > 0;
                if (heldUp) p.Hold++;
                var g = heldUp ? RunnerDino.GHold : RunnerDino.G;
                if ((held & 2) != 0)
                {
                    // ↓ у повітрі: швидко вниз і вже пригнувшись — під бурульку за брилою (подія combo)
                    if (p.Vy > RunnerDino.FastFall) p.Vy = RunnerDino.FastFall;
                    g = RunnerDino.G * 2;
                    p.HoldOn = false;
                    p.Duck = true;
                }
                else p.Duck = false;
                Fly(seat, p, run, gx, held, g);
            }
            else Walk(p, gx);
        }
        else
        {
            p.Duck = false;
            p.HoldOn = false;
            if (p.Air) Fly(seat, p, run, gx, 0, RunnerDino.G);
            else Walk(p, gx);
        }

        if (p.Stun == 0) CollideDino(seat, p, run, worldX);
    }

    static void Jump(RunnerPlayer p, int held)
    {
        p.Air = true;
        p.Vy = RunnerDino.JumpV;
        p.Hold = 0;
        p.HoldOn = (held & 1) != 0;
        p.Buffer = 0;
        p.Coyote = 0;
        p.Duck = false;
    }

    void Fly(int seat, RunnerPlayer p, int run, int gx, int held, int g)
    {
        p.Vy -= g;
        if (p.Vy < RunnerDino.VyMin) p.Vy = RunnerDino.VyMin;
        p.Y += p.Vy;
        var ground = GroundAt(gx);
        if (ground != NoGround && p.Y <= ground && p.Vy <= 0)
        {
            p.Y = ground;
            p.Air = false;
            p.Vy = 0;
            p.Duck = (held & 2) != 0;
            p.Coyote = 0;
        }
        else if (ground == NoGround && p.Y < -RunnerDino.PitDepth) Fall(seat, p, run);
    }

    void Walk(RunnerPlayer p, int gx)
    {
        var ground = GroundAt(gx);
        if (ground == NoGround)
        {
            p.Air = true;
            p.Vy = 0;
            p.Coyote = RunnerDino.CoyoteSteps;
        }
        else p.Y = ground;
    }

    /// <summary>Провалився в яму: секунда стуну, вилазить на дальньому краю.</summary>
    void Fall(int seat, RunnerPlayer p, int run)
    {
        ref var pit = ref _ob[_pitIdx];
        p.Stun = RunnerDino.PitStun;
        p.Y = 0;
        p.Air = false;
        p.Vy = 0;
        p.Duck = false;
        p.HoldOn = false;
        p.Buffer = 0;
        p.Coyote = 0;
        p.Lag = Rules.PaceX(run + 1) - (pit.X + pit.W + RunnerDino.PitOut);
        if (p.Lag < RunnerDino.LagMin) p.Lag = RunnerDino.LagMin;
        p.Hits++;
        var fresh = !(_rewinding && Was(_prePassed, pit.Id));
        p.AddPassed(pit.Id);
        p.Lp = pit.Id;
        if (fresh) Emit(RunnerEvent.Pit, seat, run, pit.Id);
    }

    void CollideDino(int seat, RunnerPlayer p, int run, int worldX)
    {
        var x0 = worldX;
        var x1 = worldX + (p.Duck ? RunnerDino.DuckW : RunnerDino.HitW);
        var y0 = p.Y;
        var y1 = p.Y + (p.Duck ? RunnerDino.DuckH : RunnerDino.HitH);
        var hit = false;
        for (var i = 0; i < _obCount && !hit; i++)
        {
            ref var o = ref _ob[(_obHead + i) & (ObN - 1)];
            if (!RunnerKind.Solid(o.Kind)) continue;
            var ox = o.XAt(run);
            if (ox >= x1 || ox + o.W <= x0) continue;
            if (o.Base >= y1 || o.Base + o.H <= y0) continue;
            if (p.HasPassed(o.Id)) continue;
            HitDino(seat, p, run, o.Id);
            hit = true;
        }
        for (var i = 0; i < _snCount && !hit; i++)
        {
            ref var o = ref _sn[i];
            if (o.Since > run) continue;
            if (o.X >= x1 || o.X + o.W <= x0) continue;
            if (o.Base >= y1 || o.Base + o.H <= y0) continue;
            if (p.HasPassed(o.Id)) continue;
            HitDino(seat, p, run, o.Id);
            hit = true;
        }
        for (var i = 0; i < _pkCount; i++)
        {
            ref var k = ref _pk[(_pkHead + i) & (PkN - 1)];
            if (k.X >= x1 || k.X + RunnerDino.PickBox <= x0) continue;
            if (k.Base >= y1 || k.Base + RunnerDino.PickBox <= y0) continue;
            if (p.HasTaken(k.Id)) continue;
            Take(seat, p, k.Id, k.Kind);
        }
    }

    void HitDino(int seat, RunnerPlayer p, int run, int id)
    {
        p.Stun = RunnerDino.StunSteps;
        p.Duck = false;
        p.HoldOn = false;
        p.Buffer = 0;
        p.Hits++;
        var fresh = !(_rewinding && Was(_prePassed, id));
        p.AddPassed(id);
        p.Lp = id;
        if (fresh) Emit(RunnerEvent.Hit, seat, run, id);
    }

    void Take(int seat, RunnerPlayer p, int id, int kind)
    {
        var fresh = !(_rewinding && Was(_preTaken, id));
        p.AddTaken(id);
        p.Lt = id;
        switch (kind)
        {
            case RunnerKind.Egg: p.Eggs++; break;
            case RunnerKind.Pepper: p.Boost = RunnerDino.BoostSteps; break;
            default: p.Snow = 1; p.SnowId = id; break;
        }
        if (fresh) Emit(kind == RunnerKind.Egg ? RunnerEvent.Egg : kind == RunnerKind.Pepper ? RunnerEvent.Pepper : RunnerEvent.SnowTake, seat, id, 0);
    }

    void StepStork(int seat, RunnerPlayer p, int run, bool edge)
    {
        if (p.Down) return;
        if (p.Ifr > 0) p.Ifr--;
        if (edge) p.Vy = RunnerStorks.Flap;
        p.Vy -= RunnerStorks.G;
        if (p.Vy < RunnerStorks.VyMin) p.Vy = RunnerStorks.VyMin;
        p.Y += p.Vy;
        if (p.Y > RunnerStorks.Ceiling)
        {
            p.Y = RunnerStorks.Ceiling;
            p.Vy = 0;
        }
        if (p.Ifr > 0) return;
        if (p.Y <= 0)
        {
            HitStork(seat, p, run, RunnerStorks.GroundId);
            return;
        }
        var x0 = Rules.PaceX(run + 1);
        var x1 = x0 + RunnerStorks.HitW;
        var y0 = p.Y;
        var y1 = p.Y + RunnerStorks.HitH;
        for (var i = 0; i < _obCount; i++)
        {
            ref var o = ref _ob[(_obHead + i) & (ObN - 1)];
            if (o.Since > run) continue;
            if (o.X >= x1 || o.X + o.W <= x0) continue;
            var ob = o.BaseAt(run);
            if (ob >= y1 || ob + o.H <= y0) continue;
            if (p.HasPassed(o.Id)) continue;
            HitStork(seat, p, run, o.Id);
            return;
        }
    }

    void HitStork(int seat, RunnerPlayer p, int run, int id)
    {
        p.Hits++;
        var fresh = !(_rewinding && id != RunnerStorks.GroundId && Was(_prePassed, id));
        if (id != RunnerStorks.GroundId)
        {
            p.AddPassed(id);
            p.Lp = id;
        }
        if (p.Feather)
        {
            p.Feather = false;
            p.Ifr = RunnerStorks.IFrames;
            p.Vy = RunnerStorks.Flap;
            if (p.Y < RunnerStorks.MinYAfterHit) p.Y = RunnerStorks.MinYAfterHit;
            if (fresh) Emit(RunnerEvent.Hit, seat, run, id);
        }
        else p.Down = true;
    }

    /// <summary>
    /// Хто вибув на цьому кроці: у Стрибозаврів — кого наздогнала лавина, у Лелек — хто зачепився без пір'я.
    /// Вибулі одним кроком ділять місце «живих після + 1».
    /// </summary>
    void Eliminate(int run)
    {
        var newly = 0;
        for (var i = 0; i < Seats; i++)
        {
            var p = P[i];
            _gone[i] = false;
            if (!p.Plays || p.Out) continue;
            if (Mode == RunnerMode.Dino ? p.Lag >= RunnerRules.AvD(run) : p.Down)
            {
                _gone[i] = true;
                newly++;
            }
        }
        if (newly == 0) return;
        var alive = 0;
        for (var i = 0; i < Seats; i++)
            if (P[i].Plays && !P[i].Out && !_gone[i]) alive++;
        for (var i = 0; i < Seats; i++)
        {
            if (!_gone[i]) continue;
            var p = P[i];
            p.Out = true;
            p.Down = true;
            p.Place = alive + 1;
            Emit(RunnerEvent.Out, i, run, 0);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // ввід і перемотування
    // ---------------------------------------------------------------------------------------------

    /// <summary>Готує запис журналу на крок t: утримання тягнеться з попереднього кроку, ребра нема.</summary>
    void Entry(int seat, int t)
    {
        var j = t & (LogN - 1);
        if (_stamp[seat][j] == t) return;
        var prev = (t - 1) & (LogN - 1);
        _held[seat][j] = _stamp[seat][prev] == t - 1 ? _held[seat][prev] : P[seat].Held;
        _edge[seat][j] = false;
        _expl[seat][j] = false;
        _stamp[seat][j] = t;
    }

    /// <summary>
    /// Ввід гравця <c>{ s, k }</c>: k — біти (1 стрибок тримають, 2 ↓ тримають, 4 ребро натиску), s — крок,
    /// на якому клієнт його застосував. Майбутнє ріжемо до +8 (клієнт свідомо йде попереду сервера, щоб ввід приходив «вчасно»), старше за 15 кроків відкидаємо, минуле —
    /// перемотуємо: гравця відновлюємо зі знімка й проганяємо ще раз із виправленим журналом.
    /// </summary>
    public bool Input(int seat, int s, int k)
    {
        if (seat < 0 || seat >= Seats || k < 0 || k > 7) return false;
        var p = P[seat];
        if (!p.Plays || p.Out || p.Down) return false;
        if (s > S + FutureMax) s = S + FutureMax;
        if (s < S - RewindMax || s < 0) return false;
        Record(seat, s, k);
        if (s < S) Rewind(seat, s);
        return true;
    }

    void Record(int seat, int s, int k)
    {
        var j = s & (LogN - 1);
        var edge = (k & 4) != 0;
        if (_stamp[seat][j] == s && _expl[seat][j]) edge |= _edge[seat][j];   // два вводи на один крок — натиск не губимо
        _held[seat][j] = k & 3;
        _edge[seat][j] = edge;
        _expl[seat][j] = true;
        _stamp[seat][j] = s;
        if (s >= S) return;
        var t = s + 1;
        for (; t < S; t++)
        {
            var jj = t & (LogN - 1);
            if (_stamp[seat][jj] == t && _expl[seat][jj]) break;
            _held[seat][jj] = k & 3;
            _stamp[seat][jj] = t;
        }
        if (t == S) P[seat].Held = k & 3;
    }

    void Rewind(int seat, int s)
    {
        if (_snapAt[seat][s & (SnapN - 1)] != s) return;
        var p = P[seat];
        Array.Copy(p.Passed, _prePassed, 8);
        Array.Copy(p.Taken, _preTaken, 8);
        _rewinding = true;
        p.CopyFrom(_snap[seat][s & (SnapN - 1)]);
        for (var t = s; t < S; t++)
        {
            _snap[seat][t & (SnapN - 1)].CopyFrom(p);
            _snapAt[seat][t & (SnapN - 1)] = t;
            StepPlayer(seat, p, t, t - ReadySteps);
        }
        _rewinding = false;
    }

    /// <summary>
    /// Сніжку кинули: її нема ні зараз, ні в знімках — інакше перемотування цього гравця повернуло б її в руку,
    /// а підбирачку, з якої вона взялась, дозволило б підняти вдруге.
    /// </summary>
    public void ConsumeSnow(int seat)
    {
        var p = P[seat];
        p.Snow = 0;
        for (var k = 0; k < SnapN; k++)
        {
            var s = _snap[seat][k];
            s.Snow = 0;
            if (p.SnowId != 0 && !s.HasTaken(p.SnowId)) s.AddTaken(p.SnowId);
        }
    }

    static bool Was(int[] ring, int id)
    {
        for (var i = 0; i < 8; i++) if (ring[i] == id) return true;
        return false;
    }

    void Emit(int kind, int seat, int a, int b)
    {
        if (_evCount == EvN) return;
        ref var e = ref _ev[_evCount++];
        e.Kind = kind;
        e.Seat = seat;
        e.A = a;
        e.B = b;
    }

    // ---------------------------------------------------------------------------------------------
    // кадр і хеш
    // ---------------------------------------------------------------------------------------------

    /// <summary>Стан місця для кадру: свіжий масив (кадр серіалізують поза замком кімнати).</summary>
    public int[]? Wire(int seat)
    {
        var p = P[seat];
        if (!p.Plays) return null;
        return Mode == RunnerMode.Dino
            ? [p.Lag, p.Y, p.Vy, p.ModeOf(Mode), p.Stun, p.Boost, p.Eggs, p.Snow, p.Lp, p.Lt, p.Hidden]
            : [p.Y, p.Vy, p.ModeOf(Mode), p.Feather ? 1 : 0, p.Ifr, p.Lp];
    }

    public int[]?[] WirePlayers()
    {
        var a = new int[]?[Seats];
        for (var i = 0; i < Seats; i++) a[i] = Wire(i);
        return a;
    }

    /// <summary>Живі кинуті брили: [x, хто кинув, id, з якого кроку б'є].</summary>
    public int[][] WireSnow()
    {
        var a = new int[_snCount][];
        for (var i = 0; i < _snCount; i++) a[i] = [_sn[i].X, _sn[i].By, _sn[i].Id, _sn[i].Since];
        return a;
    }

    void HashStep(int run)
    {
        H(run);
        H(Rules.PaceX(run < 0 ? 0 : run + 1));
        for (var i = 0; i < Seats; i++)
        {
            var p = P[i];
            if (!p.Plays) continue;
            H(i);
            if (Mode == RunnerMode.Dino)
            {
                H(p.Lag); H(p.Y); H(p.Vy); H(p.ModeOf(Mode)); H(p.Stun); H(p.Boost); H(p.Eggs); H(p.Snow); H(p.Hidden);
            }
            else
            {
                H(p.Y); H(p.Vy); H(p.ModeOf(Mode)); H(p.Feather ? 1 : 0); H(p.Ifr); H(p.Lp);
            }
        }
    }

    /// <summary>FNV-1a над int32 little-endian — те саме в JS (Math.imul).</summary>
    void H(int v)
    {
        unchecked
        {
            var u = (uint)v;
            var h = Hash;
            h = (h ^ (u & 255)) * 16777619;
            h = (h ^ ((u >> 8) & 255)) * 16777619;
            h = (h ^ ((u >> 16) & 255)) * 16777619;
            h = (h ^ (u >> 24)) * 16777619;
            Hash = h;
        }
    }

    /// <summary>
    /// Прогін для парності з JS (dino.md §8.3): випадковий ввід від inputSeed, ті самі правила обабіч.
    /// throws — (крок, місце): на цьому кроці місце кидає брилу на paceX + 400 px.
    /// </summary>
    public static RunnerSim Fixture(RunnerMode mode, int seed, int players, int inputSeed, int steps, int readySteps, int pmCap,
        bool snowOn, (int Step, int Seat)[]? throws = null)
    {
        var plays = new bool[Seats];
        for (var i = 0; i < players; i++) plays[i] = true;
        var sim = new RunnerSim(mode, seed, plays, readySteps, pmCap, snowOn, featherOn: true, hashing: true);
        var rng = new RunnerRng(inputSeed);
        var held = new int[Seats];
        for (var step = 0; step < steps; step++)
        {
            for (var seat = 0; seat < players; seat++)
            {
                var u = rng.Next(100);
                var edge = false;
                if (u < 6)
                {
                    held[seat] ^= 1;
                    edge = (held[seat] & 1) != 0;
                }
                else if (u < 9) held[seat] ^= 2;
                sim.Input(seat, sim.S, held[seat] | (edge ? 4 : 0));
            }
            if (throws is not null)
                foreach (var (at, by) in throws)
                    if (at == step)
                    {
                        var run = Math.Max(0, sim.S - sim.ReadySteps);
                        sim.PlaceSnow(sim.PaceX(run) + 400 * Sub, by, run);
                    }
            sim.Step();
            sim.ClearEvents();
        }
        return sim;
    }
}
