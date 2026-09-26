namespace Hlechyky.Games.Impl;

/// <summary>
/// Один селянин купальської ночі. Бот і гравець — той самий клас і той самий <see cref="KupalaCore.Step"/>: гравцеві
/// <see cref="Want"/> пише ввід, ботові — <see cref="KupalaCore.Think"/>.
/// </summary>
public sealed class KupalaVillager
{
    /// <summary>Номер у кадрі; за раунд не міняється, між раундами тасується.</summary>
    public int Id;
    public int X, Y;
    /// <summary>0 праворуч, 1 вниз, 2 ліворуч, 3 вгору.</summary>
    public int Dir;
    public int Want = -1;
    public bool Moving;
    public bool Blocked;
    /// <summary>Місце гравця; -1 — бот.</summary>
    public int Owner = -1;
    /// <summary>Вибитий гравець: лежить до кінця раунду.</summary>
    public bool Dead;
    /// <summary>Бот після ляпаса лежить ще стільки тиків.</summary>
    public int Fallen;
    /// <summary>«Отетерів» після ляпаса по ботові — стоїть стільки тиків (і гравець, і бот-жартівник).</summary>
    public int Stun;
    /// <summary>Пускає вінок чи зриває квітку ще стільки тиків (рух ігнорується).</summary>
    public int Busy;
    /// <summary>Що саме: номер кладки або <see cref="KupalaCore.FernWhat"/>; -1 — нічого.</summary>
    public int BusyWhat = -1;

    // ---- мозок бота ----
    public int Target = -1;
    public int Tx, Ty;
    /// <summary>Ціль — ця кладка (бот там інколи пускає вінок); -1 — ні.</summary>
    public int TargetSpot = -1;
    public int Stand;
    public int Wander;
    public bool WanderNext;

    // ---- вигляд на раунд ----
    public int Hat, HatColor, Shirt, Skin, Name;

    /// <summary>Стан у кадрі: 0 стоїть, 1 іде, 2 лежить бот, 3 вибитий гравець.</summary>
    public int State => Dead ? 3 : Fallen > 0 ? 2 : Moving ? 1 : 0;
    public bool Upright => !Dead && Fallen == 0;
}

/// <summary>
/// Купальська ніч без кімнат і очок: селяни, один спільний крок, мозок ботів, ляпас і головешка (однакові для ботів
/// і гравців), світло (вогнища, світлячки, головешки, свічки на вінках, папороть, зарниця) і хто в ньому стоїть.
/// Уся випадковість — з переданого <see cref="Random"/> у строгому порядку: той самий сід і той самий ввід дають
/// побайтно ті самі кадри.
/// </summary>
public sealed class KupalaCore(Random rng)
{
    public const int Speed = 3, Half = 8;

    // ---- ляпас ----
    /// <summary>Ляпас без цілі: найближчий у ±60° перед собою на стільки одиниць (центр до центру).</summary>
    public const int SlapReach = 40;
    /// <summary>Явна ціль (клік по селянину) — до стількох: клієнт бачить кадр на ~100 мс старший.</summary>
    public const int SlapReachMax = 56;
    /// <summary>cos²60° = 0,25 — у тисячних, щоб рахувати конус на цілих.</summary>
    public const int SlapCos2Milli = 250;
    /// <summary>Ляснув бота — «отетерів» на 2 с.</summary>
    public const int StunTicks = 50;
    /// <summary>Бот після ляпаса лежить 3 с.</summary>
    public const int FallTicks = 75;
    /// <summary>Махнув рукою в порожнечу — звук і місце чують усі, на такій відстані перед собою.</summary>
    public const int WhiffAhead = 20;

    // ---- вінок і квітка ----
    /// <summary>Пустити вінок чи зірвати квітку — секунда стояння, однаково в гравця й бота.</summary>
    public const int BusyTicks = 25;
    public const int FernWhat = 100;
    /// <summary>Вінок пливе за течією (праворуч) на стільки одиниць за тик.</summary>
    public const int WreathSpeed = 1;
    public const int WreathMax = 40;

    // ---- світло ----
    /// <summary>Вогнище світить на 96 (три клітинки), мерехтить на ±6 за табличкою <see cref="Flick"/>.</summary>
    public const int FireR = 96;
    public static readonly int[] Flick = [0, 3, 5, 6, 4, 1, -2, -4, -6, -5, -3, 0, 2, 4, 1, -1];
    public const int FlyCount = 6, FlyR = 40, FlyTurn = 25;
    /// <summary>Світлячки літають над лугом і лісом (не над річкою): тут їхня коробка.</summary>
    public const int FlyMinX = 48, FlyMaxX = KupalaMap.WorldW - 48, FlyMinY = 48, FlyMaxY = 432;
    public const int TorchR = 80, TorchTicks = 150, TorchDist = 128, TorchFade = 25;
    public const int WreathR = 48;
    public const int FernR = 32;
    /// <summary>Зарниця: пів секунди видно всіх.</summary>
    public const int SkyTicks = 12, SkyFirstMin = 250, SkyFirstMax = 500, SkyEveryMin = 375, SkyEveryMax = 750;
    /// <summary>Папороть зацвітає раз за раунд, на 30–60 с ночі.</summary>
    public const int FernAtMin = 750, FernAtMax = 1500;

    /// <summary>Вид світла в кадрі.</summary>
    public const int LFire = 0, LFly = 1, LTorch = 2, LWreath = 3, LFern = 4;

    // ---- мозок бота (як у Юрмі — перевірено тестами на збіг розподілів) ----
    public const int StandMin = 12, StandMax = 75, LongStandMax = 500;
    public const int PauseMilli = 9, PauseMax = 20;
    public const int FidgetMilli = 8;
    public const int WanderChance = 15, WanderMin = 8, WanderMax = 24;
    public const int SpotMax = 15, TightMax = 7;
    public const int OpeningStandMax = 150;
    /// <summary>Куди йдуть боти: 45 % — погрітись біля вогнища, 20 % — на кладку, решта — навмання в темряву.</summary>
    public const int FirePick = 45, SpotPick = 20;
    /// <summary>Бот на кладці в стількох тисячних випадків і справді пускає вінок.</summary>
    public const int BotLaunchMilli = 120;
    /// <summary>
    /// Раз на стільки тиків (у середньому, на бота) парубок жартома ляскає сусіда-бота або махає рукою в темряві, і
    /// раз на <see cref="BotTorchOdds"/> — шпурляє головешку. На 30 ботах це раз на ~50 с і ~80 с: ні ляпас, ні
    /// головешка — не вирок «це гравець».
    /// </summary>
    public const int BotSlapOdds = 40000, BotTorchOdds = 60000;

    public static readonly int[] DX = [1, 0, -1, 0];
    public static readonly int[] DY = [0, 1, 0, -1];

    readonly Random _rng = rng;
    readonly int[] _nameOrder = new int[KupalaMap.Names.Length];

    public KupalaVillager[] V { get; private set; } = [];
    public int N => V.Length;
    /// <summary>Хто цього тика у світлі (за id). Лише їх і несе кадр.</summary>
    public bool[] Lit { get; private set; } = [];

    /// <summary>Ніч (фаза «go»): лише тоді світло вибіркове, а боти пускають вінки, ляскають і шпурляють головешки.</summary>
    public bool Night { get; set; }
    /// <summary>Тик раунду — від нього мерехтять вогнища.</summary>
    public int T { get; set; }
    /// <summary>Тиків ночі цього раунду.</summary>
    public int NightT { get; private set; }

    // світлячки
    public readonly int[] FlyX = new int[FlyCount], FlyY = new int[FlyCount], FlyDx = new int[FlyCount], FlyDy = new int[FlyCount], FlyLeft = new int[FlyCount];
    public readonly bool[] FlyOn = new bool[FlyCount];

    /// <summary>Головешка на землі: де й скільки ще горить.</summary>
    public readonly List<(int X, int Y, int Left)> Torches = [];
    /// <summary>Вінок на воді.</summary>
    public readonly List<(int X, int Y)> Wreaths = [];
    /// <summary>Клітинка, де цвіте папороть; -1 — ще не зацвіла або вже зірвали.</summary>
    public int FernCell { get; set; } = -1;
    public bool FernGone { get; private set; }
    public int FernAt { get; set; }
    public int Sky { get; set; }
    public int SkyNext { get; set; }

    /// <summary>Події, які породило саме ядро за тик (ляпаси й головешки ботів, вінки, папороть), — гра забирає їх.</summary>
    public readonly List<int[]> Events = [];

    int[] _lights = new int[64];
    public int LightCount { get; private set; }

    // ---------------------------------------------------------------------------------------------
    // Крок — один на всіх
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Крок селянина. Хто зайнятий вінком, лежить, отетерів, вибитий чи нікуди не хоче — стоїть. Інакше повертається
    /// в бік <see cref="KupalaVillager.Want"/> і робить рівно <see cref="Speed"/>, якщо коробка там уміщається. Ця
    /// функція одна для ботів і гравців.
    /// </summary>
    public static void Step(KupalaVillager v)
    {
        if (v.Busy > 0 || v.Fallen > 0 || v.Stun > 0 || v.Dead || v.Want < 0)
        {
            v.Moving = false;
            v.Blocked = false;
            return;
        }
        v.Dir = v.Want;
        int nx = v.X + DX[v.Dir] * Speed, ny = v.Y + DY[v.Dir] * Speed;
        if (KupalaMap.BoxFits(nx, ny))
        {
            v.X = nx;
            v.Y = ny;
            v.Moving = true;
            v.Blocked = false;
        }
        else
        {
            v.Moving = false;
            v.Blocked = true;
        }
    }

    public void StepAll()
    {
        var v = V;
        for (var i = 0; i < v.Length; i++) Step(v[i]);
    }

    int[] _done = [];
    /// <summary>Кому цього тика скінчилось «пускаю вінок» / «зриваю квітку» (id за зростанням).</summary>
    public ReadOnlySpan<int> Done => _done.AsSpan(0, DoneCount);
    public int DoneCount { get; private set; }

    /// <summary>Годинники селян: вінок, лежання, «отетерів». Хто відлежав — встає й думає з чистого аркуша.</summary>
    public void TimersAll()
    {
        var v = V;
        if (_done.Length < v.Length) _done = new int[v.Length];
        DoneCount = 0;
        for (var i = 0; i < v.Length; i++)
        {
            var q = v[i];
            if (q.Busy > 0 && --q.Busy == 0) _done[DoneCount++] = i;
            if (q.Fallen > 0 && --q.Fallen == 0) Forget(q);
            if (q.Stun > 0) q.Stun--;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Мозок бота
    // ---------------------------------------------------------------------------------------------

    public void ThinkAll()
    {
        var v = V;
        for (var i = 0; i < v.Length; i++)
            if (v[i].Owner < 0) Think(v[i]);
    }

    /// <summary>
    /// Бот виставляє <see cref="KupalaVillager.Want"/> так, щоб у кадрах його не відрізнити від людини (мозок Юрми):
    /// іде до цілі — вогнище, кладка чи будь-куди в темряву — своєю смугою, інколи вагається, стоїть будь-де в
    /// клітинці, переступає, тиняється. Уночі зрідка ще й ляскає сусіда, шпурляє головешку чи пускає вінок.
    /// </summary>
    public void Think(KupalaVillager v)
    {
        if (v.Dead || v.Fallen > 0 || v.Busy > 0 || v.Stun > 0) return;
        if (Night)
        {
            if (_rng.Next(BotSlapOdds) == 0)
            {
                BotSlap(v);
                return;
            }
            if (_rng.Next(BotTorchOdds) == 0) Throw(v);
        }
        if (v.Wander > 0)
        {
            v.Wander--;
            return;
        }
        if (v.Stand > 0)
        {
            v.Stand--;
            v.Want = -1;
            if (v.Stand > 3 && _rng.Next(1000) < FidgetMilli)
            {
                v.Wander = _rng.Next(0, 3);
                v.Want = _rng.Next(4);
                return;
            }
            if (v.Stand == 0 && v.WanderNext)
            {
                v.WanderNext = false;
                v.Wander = _rng.Next(WanderMin, WanderMax + 1);
                v.Want = _rng.Next(4);
            }
            return;
        }
        if (v.Blocked)
        {
            v.Blocked = false;
            v.Target = -1;
            v.TargetSpot = -1;
            v.Want = -1;
            return;
        }
        if (v.Target < 0) PickTarget(v);
        if (_rng.Next(1000) < PauseMilli)
        {
            v.Stand = _rng.Next(0, PauseMax);
            v.Want = -1;
            return;
        }

        var cell = KupalaMap.CellOf(v.X, v.Y);
        if (cell == v.Target)
        {
            Approach(v);
            return;
        }
        var hop = KupalaMap.NextHop[v.Target * KupalaMap.Cells + cell];
        if (hop == KupalaMap.NoHop)
        {
            v.Target = -1;
            v.TargetSpot = -1;
            v.Want = -1;
            return;
        }
        if (hop is 0 or 2)
        {
            int mid = KupalaMap.CenterY(cell), off = v.Ty - KupalaMap.CenterY(v.Target), next = KupalaMap.CenterX(cell) + DX[hop] * KupalaMap.Cell;
            var lane = mid + off;
            if (!Fits4(v.X, next, lane, true)) lane = mid + Math.Clamp(off, -TightMax, TightMax);
            if (v.Y < lane - 1) { v.Want = 1; return; }
            if (v.Y > lane + 1) { v.Want = 3; return; }
        }
        else
        {
            int mid = KupalaMap.CenterX(cell), off = v.Tx - KupalaMap.CenterX(v.Target), next = KupalaMap.CenterY(cell) + DY[hop] * KupalaMap.Cell;
            var lane = mid + off;
            if (!Fits4(v.Y, next, lane, false)) lane = mid + Math.Clamp(off, -TightMax, TightMax);
            if (v.X < lane - 1) { v.Want = 0; return; }
            if (v.X > lane + 1) { v.Want = 2; return; }
        }
        v.Want = hop;
    }

    static bool Fits4(int at, int next, int lane, bool horizontal) => horizontal
        ? KupalaMap.BoxFits(at, lane - 1) && KupalaMap.BoxFits(at, lane + 1) && KupalaMap.BoxFits(next, lane - 1) && KupalaMap.BoxFits(next, lane + 1)
        : KupalaMap.BoxFits(lane - 1, at) && KupalaMap.BoxFits(lane + 1, at) && KupalaMap.BoxFits(lane - 1, next) && KupalaMap.BoxFits(lane + 1, next);

    static bool CanStep(KupalaVillager v, int d) => KupalaMap.BoxFits(v.X + DX[d] * Speed, v.Y + DY[d] * Speed);

    void Approach(KupalaVillager v)
    {
        int dx = v.Tx - v.X, dy = v.Ty - v.Y;
        if (Math.Abs(dx) <= 2 && Math.Abs(dy) <= 2)
        {
            Arrive(v);
            return;
        }
        int hx = dx > 2 ? 0 : dx < -2 ? 2 : -1, hy = dy > 2 ? 1 : dy < -2 ? 3 : -1;
        int first = Math.Abs(dx) > Math.Abs(dy) ? hx : hy, second = first == hx ? hy : hx;
        if (first >= 0 && CanStep(v, first)) v.Want = first;
        else if (second >= 0 && CanStep(v, second)) v.Want = second;
        else Arrive(v);
    }

    /// <summary>
    /// Дійшов. На кладці обертається до води (як гравець, що пускає вінок) і вночі в 12 % випадків справді пускає:
    /// секунда стояння, як у гравця, — від першого тика стояння до вінка на воді 24 тики. Деінде обличчя не міняє.
    /// </summary>
    void Arrive(KupalaVillager v)
    {
        var k = v.TargetSpot;
        v.Target = -1;
        v.TargetSpot = -1;
        v.Want = -1;
        v.WanderNext = false;
        if (k >= 0 && SpotAt(v) == k)
        {
            v.Dir = 1;
            if (_rng.Next(1000) < BotLaunchMilli && Night)
            {
                v.Busy = BusyTicks - 1;     // гравець: Act між тиками, таймер тикає вже в першому тику стояння
                v.BusyWhat = k;
                v.Stand = _rng.Next(0, 41);
                return;
            }
        }
        v.Stand = StandTicks();
        v.WanderNext = _rng.Next(100) < WanderChance;
    }

    int StandTicks()
    {
        var r = _rng.Next(100);
        return r < 74 ? _rng.Next(StandMin, StandMax + 1)
            : r < 88 ? _rng.Next(StandMax + 1, 151)
            : r < 96 ? _rng.Next(151, 301)
            : _rng.Next(301, LongStandMax + 1);
    }

    void PickTarget(KupalaVillager v)
    {
        int cell;
        var r = _rng.Next(100);
        v.TargetSpot = -1;
        if (r < FirePick)
        {
            var ring = KupalaMap.FireRing[_rng.Next(KupalaMap.Fires.Length)];
            cell = ring[_rng.Next(ring.Length)];
        }
        else if (r < FirePick + SpotPick)
        {
            var k = _rng.Next(KupalaMap.Spots.Length);
            cell = KupalaMap.Spots[k].Cell;
            v.TargetSpot = k;
        }
        else cell = KupalaMap.Walkable[_rng.Next(KupalaMap.Walkable.Length)];
        v.Target = cell;
        (v.Tx, v.Ty) = Spot(cell);
    }

    (int X, int Y) Spot(int cell)
    {
        int cx = KupalaMap.CenterX(cell), cy = KupalaMap.CenterY(cell);
        for (var i = 0; i < 3; i++)
        {
            int x = cx + _rng.Next(-SpotMax, SpotMax), y = cy + _rng.Next(-SpotMax, SpotMax);
            if (KupalaMap.BoxFits(x, y)) return (x, y);
        }
        return (cx + _rng.Next(-TightMax, TightMax + 1), cy + _rng.Next(-TightMax, TightMax + 1));
    }

    public static void Forget(KupalaVillager v)
    {
        v.Target = -1;
        v.TargetSpot = -1;
        v.Want = -1;
        v.Stand = 0;
        v.Wander = 0;
        v.WanderNext = false;
        v.Blocked = false;
        v.Busy = 0;
        v.BusyWhat = -1;
    }

    // ---------------------------------------------------------------------------------------------
    // Ляпас, головешка, вінок — однаково для ботів і гравців
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Найближчий стоячий селянин у конусі ±60° на <see cref="SlapReach"/> перед <paramref name="p"/> — у світлі чи в
    /// темряві, однаково; при рівній відстані — менший id. <paramref name="botsOnly"/> — для бота-жартівника: гравців
    /// він не чіпає. -1 — нікого.
    /// </summary>
    public int Cone(KupalaVillager p, bool botsOnly = false)
    {
        int best = -1;
        long bestD = long.MaxValue;
        int fx = DX[p.Dir], fy = DY[p.Dir];
        var v = V;
        for (var i = 0; i < v.Length; i++)
        {
            var q = v[i];
            if (q == p || !q.Upright || (botsOnly && q.Owner >= 0)) continue;
            long dx = q.X - p.X, dy = q.Y - p.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 > (long)SlapReach * SlapReach) continue;
            var dot = dx * fx + dy * fy;
            if (dot <= 0 || dot * dot * 1000 < SlapCos2Milli * d2) continue;
            if (d2 < bestD)
            {
                bestD = d2;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// Ляпас від <paramref name="p"/> по <paramref name="target"/> (-1 — махнув у порожнечу). Бот падає на 3 с, а той,
    /// хто ляснув, «отетерів» на 2 с; гравець вибуває. Подія <c>[1, хто, кого, вид, місце, x, y]</c> — вид 0 бот,
    /// 1 гравець, 2 махнув повз; ids у кадрі потім сховає гра, якщо ті в темряві. Повертає, в кого влучив.
    /// </summary>
    public KupalaVillager? Slap(KupalaVillager p, int target)
    {
        if (target >= 0 && target != p.Id)
        {
            var hit = V[target];
            // обертається до того, кого б'є (клік міг бути збоку)
            int dx = hit.X - p.X, dy = hit.Y - p.Y;
            if (dx != 0 || dy != 0) p.Dir = Math.Abs(dx) >= Math.Abs(dy) ? (dx > 0 ? 0 : 2) : (dy > 0 ? 1 : 3);
            if (hit.Owner >= 0)
            {
                hit.Dead = true;
                hit.Busy = 0;
                hit.BusyWhat = -1;
                hit.Stun = 0;
                hit.Want = -1;
                hit.Moving = false;
                Events.Add([1, p.Id, hit.Id, 1, hit.Owner, hit.X, hit.Y]);
            }
            else
            {
                hit.Fallen = FallTicks;
                hit.Moving = false;
                hit.Stun = 0;
                Forget(hit);
                p.Stun = StunTicks;
                p.Moving = false;
                Events.Add([1, p.Id, hit.Id, 0, -1, hit.X, hit.Y]);
            }
            return hit;
        }
        Events.Add([1, p.Id, -1, 2, -1, p.X + DX[p.Dir] * WhiffAhead, p.Y + DY[p.Dir] * WhiffAhead]);
        return null;
    }

    void BotSlap(KupalaVillager v)
    {
        var t = Cone(v, botsOnly: true);
        Slap(v, t);
        v.Want = -1;
        v.Moving = false;
        v.Stand = Math.Max(v.Stand, _rng.Next(12, 40));      // після ляпаса ще постоїть, як людина
    }

    /// <summary>
    /// Головешка летить на <see cref="TorchDist"/> вперед і горить там 6 с. У воді — шипить і гасне. Подія
    /// <c>[3, хто, x0, y0, x1, y1, мокро]</c>: звідки летіла, бачать усі — це й ціна.
    /// </summary>
    public void Throw(KupalaVillager p)
    {
        int x1 = Math.Clamp(p.X + DX[p.Dir] * TorchDist, 8, KupalaMap.WorldW - 8);
        int y1 = Math.Clamp(p.Y + DY[p.Dir] * TorchDist, 8, KupalaMap.WorldH - 8);
        var wet = KupalaMap.WaterAt(x1, y1);
        if (!wet)
        {
            if (Torches.Count >= 16) Torches.RemoveAt(0);
            Torches.Add((x1, y1, TorchTicks));
        }
        Events.Add([3, p.Id, p.X, p.Y, x1, y1, wet ? 1 : 0]);
    }

    /// <summary>Вінок пішов на воду з кладки <paramref name="spot"/>: пливе від її середини праворуч. Подія <c>[2, кладка]</c>.</summary>
    public void Launch(int spot)
    {
        if (Wreaths.Count >= WreathMax) Wreaths.RemoveAt(0);
        Wreaths.Add((KupalaMap.CenterX(KupalaMap.Spots[spot].Cell), KupalaMap.WreathY));
        Events.Add([2, spot]);
    }

    /// <summary>Зірвати папороть: є, і селянин у її клітинці. Подія <c>[4, хто]</c>.</summary>
    public bool PickFern(KupalaVillager v)
    {
        if (FernCell < 0 || KupalaMap.CellOf(v.X, v.Y) != FernCell) return false;
        FernCell = -1;
        FernGone = true;
        Events.Add([4, v.Id]);
        return true;
    }

    public static int SpotAt(KupalaVillager v) =>
        v.X < 0 || v.Y < 0 || v.X >= KupalaMap.WorldW || v.Y >= KupalaMap.WorldH ? -1 : KupalaMap.SpotOf[KupalaMap.CellOf(v.X, v.Y)];

    public static long Dist2(KupalaVillager a, int x, int y)
    {
        long dx = a.X - x, dy = a.Y - y;
        return dx * dx + dy * dy;
    }

    // ---------------------------------------------------------------------------------------------
    // Світло
    // ---------------------------------------------------------------------------------------------

    /// <summary>Радіус вогнища <paramref name="k"/> на тику <paramref name="t"/>: мерехтить без жодної випадковості.</summary>
    public static int FireRadius(int k, int t) => FireR + Flick[(t / 3 + k * 5) & 15];

    /// <summary>
    /// Годинник світла на тик: світлячки летять і блимають, головешки догорають, вінки пливуть, а вночі — зарниця й
    /// папороть. Потім збирає всі джерела світла в список (<see cref="Lights"/>).
    /// </summary>
    public void LightsTick()
    {
        for (var i = 0; i < FlyCount; i++)
        {
            if (--FlyLeft[i] <= 0)
            {
                FlyOn[i] = !FlyOn[i];
                FlyLeft[i] = FlyOn[i] ? _rng.Next(100, 251) : _rng.Next(25, 126);
            }
            if (_rng.Next(FlyTurn) == 0) TurnFly(i);
            FlyX[i] += FlyDx[i];
            FlyY[i] += FlyDy[i];
            if (FlyX[i] < FlyMinX || FlyX[i] > FlyMaxX) { FlyDx[i] = -FlyDx[i]; FlyX[i] = Math.Clamp(FlyX[i], FlyMinX, FlyMaxX); }
            if (FlyY[i] < FlyMinY || FlyY[i] > FlyMaxY) { FlyDy[i] = -FlyDy[i]; FlyY[i] = Math.Clamp(FlyY[i], FlyMinY, FlyMaxY); }
        }
        for (var i = Torches.Count - 1; i >= 0; i--)
        {
            var t = Torches[i];
            if (t.Left <= 1) Torches.RemoveAt(i);
            else Torches[i] = (t.X, t.Y, t.Left - 1);
        }
        for (var i = Wreaths.Count - 1; i >= 0; i--)
        {
            var w = Wreaths[i];
            if (w.X + WreathSpeed > KupalaMap.WorldW + 24) Wreaths.RemoveAt(i);
            else Wreaths[i] = (w.X + WreathSpeed, w.Y);
        }
        if (Night)
        {
            NightT++;
            if (Sky > 0) Sky--;
            else if (NightT >= SkyNext)
            {
                Sky = SkyTicks;
                SkyNext = NightT + _rng.Next(SkyEveryMin, SkyEveryMax + 1);
            }
            if (FernCell < 0 && !FernGone && NightT == FernAt)
            {
                FernCell = KupalaMap.FernCells[_rng.Next(KupalaMap.FernCells.Length)];
                Events.Add([5]);
            }
        }
        BuildLights();
    }

    void TurnFly(int i)
    {
        FlyDx[i] = _rng.Next(-1, 2);
        FlyDy[i] = _rng.Next(-1, 2);
        if (FlyDx[i] == 0 && FlyDy[i] == 0) FlyDx[i] = 1;
    }

    /// <summary>Усі джерела світла цього тика: по 4 числа — вид, x, y, радіус.</summary>
    public void BuildLights()
    {
        LightCount = 0;
        for (var k = 0; k < KupalaMap.Fires.Length; k++) AddLight(LFire, KupalaMap.Fires[k].X, KupalaMap.Fires[k].Y, FireRadius(k, T));
        for (var i = 0; i < FlyCount; i++)
            if (FlyOn[i]) AddLight(LFly, FlyX[i], FlyY[i], FlyR);
        foreach (var t in Torches) AddLight(LTorch, t.X, t.Y, t.Left >= TorchFade ? TorchR : TorchR / 2 + TorchR / 2 * t.Left / TorchFade);
        foreach (var w in Wreaths) AddLight(LWreath, w.X, w.Y, WreathR);
        if (FernCell >= 0) AddLight(LFern, KupalaMap.CenterX(FernCell), KupalaMap.CenterY(FernCell), FernR);
    }

    void AddLight(int kind, int x, int y, int r)
    {
        if ((LightCount + 1) * 4 > _lights.Length) Array.Resize(ref _lights, _lights.Length * 2);
        var j = LightCount * 4;
        _lights[j] = kind;
        _lights[j + 1] = x;
        _lights[j + 2] = y;
        _lights[j + 3] = r;
        LightCount++;
    }

    public ReadOnlySpan<int> Lights => _lights.AsSpan(0, LightCount * 4);

    /// <summary>Чи світло на точці: у колі хоч одного джерела.</summary>
    public bool LitAt(int x, int y)
    {
        var l = _lights;
        for (var i = 0; i < LightCount; i++)
        {
            var j = i * 4;
            long dx = x - l[j + 1], dy = y - l[j + 2], r = l[j + 3];
            if (dx * dx + dy * dy <= r * r) return true;
        }
        return false;
    }

    /// <summary>
    /// Хто у світлі. <paramref name="all"/> — сутінки «роздивись», зарниця, розкриття: видно всіх. Точка селянина —
    /// земля між ногами, та сама, що в кадрі.
    /// </summary>
    public void ComputeLit(bool all)
    {
        var v = V;
        if (Lit.Length != v.Length) Lit = new bool[v.Length];
        for (var i = 0; i < v.Length; i++) Lit[i] = all || LitAt(v[i].X, v[i].Y);
    }

    // ---------------------------------------------------------------------------------------------
    // Раунд
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Нова галявина: <paramref name="owners"/> — місця гравців (ідуть першими), далі <paramref name="bots"/> ботів.
    /// Порядок випадковості: розстановка → тасування id → вигляд → імена → перше стояння ботів → світлячки →
    /// розклад зарниці й папороті.
    /// </summary>
    public void Deal(IReadOnlyList<int> owners, int bots)
    {
        var n = owners.Count + bots;
        var list = new KupalaVillager[n];
        var walk = KupalaMap.Walkable;
        for (var i = 0; i < n; i++)
        {
            var v = new KupalaVillager { Owner = i < owners.Count ? owners[i] : -1 };
            int cell = 0;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                cell = walk[_rng.Next(walk.Length)];
                if (KupalaMap.SpotOf[cell] >= 0) continue;
                if (v.Owner >= 0 && TooClose(list, i, cell)) continue;
                break;
            }
            (v.X, v.Y) = Spot(cell);
            v.Dir = _rng.Next(4);
            list[i] = v;
        }
        for (var i = n - 1; i > 0; i--)
        {
            var j = _rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        for (var i = 0; i < n; i++)
        {
            var v = list[i];
            v.Id = i;
            v.Hat = _rng.Next(4);
            v.HatColor = _rng.Next(8);
            v.Shirt = _rng.Next(8);
            v.Skin = _rng.Next(4);
        }
        for (var i = 0; i < _nameOrder.Length; i++) _nameOrder[i] = i;
        for (var i = _nameOrder.Length - 1; i > 0; i--)
        {
            var j = _rng.Next(i + 1);
            (_nameOrder[i], _nameOrder[j]) = (_nameOrder[j], _nameOrder[i]);
        }
        for (var i = 0; i < n; i++) list[i].Name = _nameOrder[i % _nameOrder.Length];
        for (var i = 0; i < n; i++)
            if (list[i].Owner < 0) list[i].Stand = _rng.Next(0, OpeningStandMax + 1);
        V = list;
        Lit = new bool[n];

        for (var i = 0; i < FlyCount; i++)
        {
            FlyX[i] = _rng.Next(FlyMinX, FlyMaxX + 1);
            FlyY[i] = _rng.Next(FlyMinY, FlyMaxY + 1);
            TurnFly(i);
            FlyOn[i] = _rng.Next(2) == 0;
            FlyLeft[i] = _rng.Next(25, 251);
        }
        Torches.Clear();
        Wreaths.Clear();
        FernCell = -1;
        FernGone = false;
        FernAt = _rng.Next(FernAtMin, FernAtMax + 1);
        Sky = 0;
        SkyNext = _rng.Next(SkyFirstMin, SkyFirstMax + 1);
        NightT = 0;
        Night = false;
        T = 0;
        Events.Clear();
        BuildLights();
        ComputeLit(true);
    }

    static bool TooClose(KupalaVillager[] placed, int count, int cell)
    {
        int cx = cell % KupalaMap.W, cy = cell / KupalaMap.W;
        for (var i = 0; i < count; i++)
        {
            var p = placed[i];
            if (p.Owner < 0) continue;
            int px = p.X / KupalaMap.Cell, py = p.Y / KupalaMap.Cell;
            if (Math.Max(Math.Abs(px - cx), Math.Abs(py - cy)) < 3) return true;
        }
        return false;
    }

    /// <summary>
    /// Кадр: лише ті, хто у світлі, — по 5 чисел (id, x, y, d, s) за зростанням id. Хто в темряві, того в кадрі нема
    /// зовсім. Новий масив щоразу: розсилка серіалізує його вже поза замком.
    /// </summary>
    public int[] Pack()
    {
        var v = V;
        var count = 0;
        for (var i = 0; i < v.Length; i++)
            if (Lit[i]) count++;
        var a = new int[count * 5];
        var j = 0;
        for (var i = 0; i < v.Length; i++)
        {
            if (!Lit[i]) continue;
            var q = v[i];
            a[j] = q.Id;
            a[j + 1] = q.X;
            a[j + 2] = q.Y;
            a[j + 3] = q.Dir;
            a[j + 4] = q.State;
            j += 5;
        }
        return a;
    }

    public int[] PackLights() => Lights.ToArray();

    public int[] Looks()
    {
        var v = V;
        var a = new int[v.Length * 4];
        for (var i = 0; i < v.Length; i++)
        {
            a[i * 4] = v[i].Hat;
            a[i * 4 + 1] = v[i].HatColor;
            a[i * 4 + 2] = v[i].Shirt;
            a[i * 4 + 3] = v[i].Skin;
        }
        return a;
    }

    public string[] NamesNow()
    {
        var v = V;
        var a = new string[v.Length];
        for (var i = 0; i < v.Length; i++) a[i] = KupalaMap.Names[v[i].Name];
        return a;
    }
}
