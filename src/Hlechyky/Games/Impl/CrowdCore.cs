namespace Hlechyky.Games.Impl;

/// <summary>
/// Один селянин ярмарку. Бот і гравець — той самий клас і той самий <see cref="CrowdCore.Step"/>: гравцеві
/// <see cref="Want"/> пише ввід, ботові — <see cref="CrowdCore.Think"/>. Решта полів мозку в гравця просто лежать.
/// </summary>
public sealed class CrowdVillager
{
    /// <summary>Номер у кадрі; за раунд не міняється, між раундами тасується.</summary>
    public int Id;
    /// <summary>Центр у одиницях світу (клітинка — 32).</summary>
    public int X, Y;
    /// <summary>Куди дивиться: 0 праворуч, 1 вниз, 2 ліворуч, 3 вгору.</summary>
    public int Dir;
    /// <summary>Куди хоче йти; -1 — стоїть.</summary>
    public int Want = -1;
    /// <summary>Цього тика зробив крок.</summary>
    public bool Moving;
    /// <summary>Хотів іти, але коробка вперлась у перешкоду.</summary>
    public bool Blocked;
    /// <summary>Місце гравця; -1 — бот.</summary>
    public int Owner = -1;
    /// <summary>Збитий гравець: лежить до кінця раунду.</summary>
    public bool Dead;
    /// <summary>Збитий бот лежить ще стільки тиків.</summary>
    public int Fallen;
    /// <summary>Гравець торгується ще стільки тиків (рух ігнорується).</summary>
    public int Haggle;
    public int HaggleStall = -1;

    // ---- мозок бота ----
    /// <summary>Клітинка цілі; -1 — треба обрати.</summary>
    public int Target = -1;
    /// <summary>Точка цілі в клітинці (центр ± до 15) — її зсув від центру й «смуга», якою бот іде й повертає.</summary>
    public int Tx, Ty;
    /// <summary>Ціль — прилавок цього лотка; -1 — просто кудись.</summary>
    public int TargetStall = -1;
    public int Stand;
    public int Wander;
    public bool WanderNext;

    // ---- вигляд на раунд ----
    public int Hat, HatColor, Shirt, Skin, Name;

    /// <summary>Стан у кадрі: 0 стоїть, 1 іде, 2 лежить бот, 3 мертвий гравець.</summary>
    public int State => Dead ? 3 : Fallen > 0 ? 2 : Moving ? 1 : 0;

    public bool Upright => !Dead && Fallen == 0;
}

/// <summary>
/// Юрма без кімнат і очок: селяни, один спільний крок, мозок ботів, конус рогатки, розстановка раунду.
/// Усе ціле, уся випадковість — з переданого <see cref="Random"/> у строгому порядку, тож той самий сід і
/// той самий ввід дають побайтно ті самі кадри. У гарячому циклі — жодної алокації, крім кадру.
/// </summary>
public sealed class CrowdCore(Random rng)
{
    /// <summary>Крок за тик — однаковий для всіх: 75 од/с ≈ 2,3 клітинки/с. Бігу нема.</summary>
    public const int Speed = 3;
    /// <summary>Половина коробки селянина 16×16 (для перешкод; один крізь одного селяни проходять).</summary>
    public const int Half = 8;
    /// <summary>Купують з такої відстані до точки прилавка.</summary>
    public const int BuyRange = 40;
    /// <summary>Конус рогатки: найближчий у ±35° на стільки одиниць.</summary>
    public const int ShotRange = 160;
    /// <summary>Явна ціль (клік по селянину) приймається до стількох: клієнт бачить кадр на ~100 мс старший.</summary>
    public const int ShotRangeMax = 190;
    /// <summary>cos²35° ≈ 0,671 — у тисячних, щоб рахувати конус на цілих.</summary>
    public const int ConeCos2Milli = 671;
    /// <summary>
    /// Скільки бот стоїть, коли дійшов: звичайно 0,5–3 с, але кожен четвертий задивляється довше — до 6 с,
    /// до 12 с, а зрідка й до 20 («роззяви»). Людина, що завмерла роздивитись юрму, так не виділяється.
    /// </summary>
    public const int StandMin = 12, StandMax = 75, LongStandMax = 500;
    /// <summary>Посеред дороги бот інколи зупиняється на 1–20 тиків — «завагався», як людина, що відпустила клавішу.</summary>
    public const int PauseMilli = 9, PauseMax = 20;
    /// <summary>Стоячи, бот інколи переступає: 1–3 тики кроку в випадковий бік (біля перешкоди — лише обертається).</summary>
    public const int FidgetMilli = 8;
    /// <summary>Відсоток «тиняння» після стояння і скільки воно триває.</summary>
    public const int WanderChance = 15, WanderMin = 8, WanderMax = 24;
    /// <summary>Відсоток цілей-прилавків (решта — випадкова клітинка).</summary>
    public const int StallPick = 60;
    /// <summary>
    /// Бот, що дійшов до прилавка, у стількох тисячних випадків і справді купує: секунду торгується, і лоток
    /// спалахує, як від гравця. Тож спалах — не вирок «тут живий», а лише привід придивитись.
    /// </summary>
    public const int BotBuyMilli = 25;
    /// <summary>Торг триває секунду — однаково в гравця й бота.</summary>
    public const int HaggleTicks = 25;
    /// <summary>
    /// Де в клітинці селянин стає чи якою смугою йде: зсув від центру до ±15 (уся клітинка), коли коробка
    /// влазить; де тісно — до ±7. Так само, як людина, що зупиняється будь-де.
    /// </summary>
    public const int SpotMax = 15, TightMax = 7;
    /// <summary>Збитий бот лежить 3 с.</summary>
    public const int FallTicks = 75;
    /// <summary>
    /// Скільки бот може простояти на самому старті раунду: 0…6 с, тобто й перші 3 с після «роздивись». Гравець,
    /// що роздивлявся й рушає лише на сигнал, так не виділяється: на старті «go» стоїть ще половина юрми.
    /// </summary>
    public const int OpeningStandMax = 150;

    public static readonly int[] DX = [1, 0, -1, 0];
    public static readonly int[] DY = [0, 1, 0, -1];

    readonly Random _rng = rng;
    readonly int[] _nameOrder = new int[CrowdMap.Names.Length];

    public CrowdVillager[] V { get; private set; } = [];
    public int N => V.Length;

    /// <summary>Ярмарок відкрито (фаза «go»): лише тоді й боти купують — гравцям на «роздивись» теж не можна.</summary>
    public bool Trading { get; set; } = true;

    // ---------------------------------------------------------------------------------------------
    // Крок — один на всіх
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Крок селянина. Хто торгується, лежить, мертвий чи нікуди не хоче — стоїть. Інакше повертається в бік
    /// <see cref="CrowdVillager.Want"/> (навіть коли далі стіна) і робить рівно <see cref="Speed"/> одиниць, якщо
    /// коробка там уміщається. Ця функція одна для ботів і гравців.
    /// </summary>
    public static void Step(CrowdVillager v)
    {
        if (v.Haggle > 0 || v.Fallen > 0 || v.Dead || v.Want < 0)
        {
            v.Moving = false;
            v.Blocked = false;
            return;
        }
        v.Dir = v.Want;
        int nx = v.X + DX[v.Dir] * Speed, ny = v.Y + DY[v.Dir] * Speed;
        if (CrowdMap.BoxFits(nx, ny))
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

    /// <summary>Усі кроком, за зростанням id.</summary>
    public void StepAll()
    {
        var v = V;
        for (var i = 0; i < v.Length; i++) Step(v[i]);
    }

    int[] _haggled = [];

    /// <summary>Кому цього тика скінчився торг (id за зростанням) — після <see cref="TimersAll"/>.</summary>
    public ReadOnlySpan<int> Haggled => _haggled.AsSpan(0, HaggledCount);
    public int HaggledCount { get; private set; }

    /// <summary>
    /// Годинники селян: торг і лежання. Кому торг скінчився — у <see cref="Haggled"/> (спалах і покупку рахує гра);
    /// хто відлежав — встає й думає з чистого аркуша.
    /// </summary>
    public void TimersAll()
    {
        var v = V;
        if (_haggled.Length < v.Length) _haggled = new int[v.Length];
        HaggledCount = 0;
        for (var i = 0; i < v.Length; i++)
        {
            var q = v[i];
            if (q.Haggle > 0 && --q.Haggle == 0) _haggled[HaggledCount++] = i;
            if (q.Fallen > 0 && --q.Fallen == 0) Forget(q);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Мозок бота
    // ---------------------------------------------------------------------------------------------

    /// <summary>Думають лише боти, за зростанням id.</summary>
    public void ThinkAll()
    {
        var v = V;
        for (var i = 0; i < v.Length; i++)
            if (v[i].Owner < 0) Think(v[i]);
    }

    /// <summary>
    /// Бот виставляє <see cref="CrowdVillager.Want"/> так, щоб у кадрах його не відрізнити від людини. Іде до цілі
    /// (прилавок чи випадкова клітинка) своєю «смугою» — зсувом цілі від центру клітинки, аж до краю клітинки, де
    /// коробка влазить; посеред дороги інколи завагається на мить; дійшов — стоїть де завгодно в клітинці, звичайно
    /// 0,5–3 с, а кожен четвертий задивляється довше; стоячи, інколи переступає; після стояння інколи тиняється.
    /// Біля прилавка обертається до лотка й зрідка справді купує (спалах, як від гравця).
    /// </summary>
    public void Think(CrowdVillager v)
    {
        if (v.Dead || v.Fallen > 0 || v.Haggle > 0) return;     // лежить чи торгується — Step і так не рушить
        if (v.Wander > 0)
        {
            v.Wander--;          // Want уже стоїть; біля стіни Step сам зупинить (лише обернеться)
            return;
        }
        if (v.Stand > 0)
        {
            v.Stand--;
            v.Want = -1;
            if (v.Stand > 3 && _rng.Next(1000) < FidgetMilli)
            {
                // переступив з ноги на ногу — і стоїть далі
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
            // уперся (після тиняння чи колишній гравець, що стояв криво) — ціль геть, наступного тика нова
            v.Blocked = false;
            v.Target = -1;
            v.TargetStall = -1;
            v.Want = -1;
            return;
        }
        if (v.Target < 0) PickTarget(v);
        if (_rng.Next(1000) < PauseMilli)
        {
            // завагався: зупинка на 1–20 тиків, ціль та сама
            v.Stand = _rng.Next(0, PauseMax);
            v.Want = -1;
            return;
        }

        var cell = CrowdMap.CellOf(v.X, v.Y);
        if (cell == v.Target)
        {
            Approach(v);
            return;
        }
        var hop = CrowdMap.NextHop[v.Target * CrowdMap.Cells + cell];
        if (hop == CrowdMap.NoHop)
        {
            v.Target = -1;
            v.TargetStall = -1;
            v.Want = -1;
            return;
        }
        // Вирівнятись на смугу поперек напрямку кроку, потім іти. Смуга — зсув цілі від центру; де з таким зсувом
        // коробка не пролізе (тут або в наступній клітинці), — ближче до центру, ±7 пролазить завжди.
        if (hop is 0 or 2)
        {
            int mid = CrowdMap.CenterY(cell), off = v.Ty - CrowdMap.CenterY(v.Target), next = CrowdMap.CenterX(cell) + DX[hop] * CrowdMap.Cell;
            var lane = mid + off;
            if (!Fits4(v.X, next, lane, true)) lane = mid + Math.Clamp(off, -TightMax, TightMax);
            if (v.Y < lane - 1) { v.Want = 1; return; }
            if (v.Y > lane + 1) { v.Want = 3; return; }
        }
        else
        {
            int mid = CrowdMap.CenterX(cell), off = v.Tx - CrowdMap.CenterX(v.Target), next = CrowdMap.CenterY(cell) + DY[hop] * CrowdMap.Cell;
            var lane = mid + off;
            if (!Fits4(v.Y, next, lane, false)) lane = mid + Math.Clamp(off, -TightMax, TightMax);
            if (v.X < lane - 1) { v.Want = 0; return; }
            if (v.X > lane + 1) { v.Want = 2; return; }
        }
        v.Want = hop;
    }

    /// <summary>
    /// Чи пролізе коробка смугою <paramref name="lane"/> (±1 — стільки лишає крок 3) і тут (<paramref name="at"/> уздовж
    /// руху), і в центрі наступної клітинки (<paramref name="next"/>). Тоді вільне й усе між ними: коробка на півдорозі
    /// накриває лише ті клітинки, що вже накривали ці.
    /// </summary>
    static bool Fits4(int at, int next, int lane, bool horizontal) => horizontal
        ? CrowdMap.BoxFits(at, lane - 1) && CrowdMap.BoxFits(at, lane + 1) && CrowdMap.BoxFits(next, lane - 1) && CrowdMap.BoxFits(next, lane + 1)
        : CrowdMap.BoxFits(lane - 1, at) && CrowdMap.BoxFits(lane + 1, at) && CrowdMap.BoxFits(lane - 1, next) && CrowdMap.BoxFits(lane + 1, next);

    static bool CanStep(CrowdVillager v, int d) => CrowdMap.BoxFits(v.X + DX[d] * Speed, v.Y + DY[d] * Speed);

    /// <summary>У клітинці цілі — до своєї точки: спершу по довшій осі, де не пролізти — по іншій; ніяк — стає тут.</summary>
    void Approach(CrowdVillager v)
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
    /// Дійшов. Біля свого прилавка обертається до лотка (як гравець, що торгується) і зрідка купує: торг рівно такий,
    /// як у гравця, — від першого тика стояння до спалаху 24 тики. Деінде обличчя не міняє: на місці повернутись
    /// людина не може. Стоїть <see cref="StandTicks"/>.
    /// </summary>
    void Arrive(CrowdVillager v)
    {
        var k = v.TargetStall;
        v.Target = -1;
        v.TargetStall = -1;
        v.Want = -1;
        v.WanderNext = false;
        if (k >= 0 && CounterAt(v) == k)
        {
            v.Dir = CrowdMap.Stalls[k].Face;
            // гравець-бот (🤖, Owner ≥ 0) сам вирішує, чи торгуватись, — через ту саму дію, що й людина
            if (v.Owner < 0 && _rng.Next(1000) < BotBuyMilli && Trading)
            {
                v.Haggle = HaggleTicks - 1;     // гравець: Act між тиками, і таймер тикає вже в першому тику стояння
                v.HaggleStall = k;
                v.Stand = _rng.Next(0, 41);
                return;
            }
        }
        v.Stand = StandTicks();
        v.WanderNext = _rng.Next(100) < WanderChance;
    }

    /// <summary>Скільки стояти: 74 % — 0,5–3 с, 14 % — 3–6 с, 8 % — 6–12 с, 4 % — 12–20 с.</summary>
    int StandTicks()
    {
        var r = _rng.Next(100);
        return r < 74 ? _rng.Next(StandMin, StandMax + 1)
            : r < 88 ? _rng.Next(StandMax + 1, 151)
            : r < 96 ? _rng.Next(151, 301)
            : _rng.Next(301, LongStandMax + 1);
    }

    void PickTarget(CrowdVillager v)
    {
        int cell;
        if (_rng.Next(100) < StallPick)
        {
            var k = _rng.Next(CrowdMap.Stalls.Length);
            var s = CrowdMap.Stalls[k];
            cell = _rng.Next(2) == 0 ? s.C0 : s.C1;
            v.TargetStall = k;
        }
        else
        {
            cell = CrowdMap.Walkable[_rng.Next(CrowdMap.Walkable.Length)];
            v.TargetStall = -1;
        }
        v.Target = cell;
        (v.Tx, v.Ty) = Spot(cell);
    }

    /// <summary>
    /// Точка в клітинці, де стати: зсув до ±15 по обох осях (уся клітинка), якщо коробка там влазить; три спроби, далі
    /// — до ±7 (влазить завжди). Нею ж і ходять (смуга), і так само ставлять на старті раунду й ботів, і гравців.
    /// </summary>
    (int X, int Y) Spot(int cell)
    {
        int cx = CrowdMap.CenterX(cell), cy = CrowdMap.CenterY(cell);
        for (var i = 0; i < 3; i++)
        {
            int x = cx + _rng.Next(-SpotMax, SpotMax), y = cy + _rng.Next(-SpotMax, SpotMax);
            if (CrowdMap.BoxFits(x, y)) return (x, y);
        }
        return (cx + _rng.Next(-TightMax, TightMax + 1), cy + _rng.Next(-TightMax, TightMax + 1));
    }

    /// <summary>
    /// Гравець-бот (🤖) обрав, куди йти: ціль — клітинка <paramref name="cell"/> (прилавок <paramref name="stall"/>
    /// чи -1), точка в ній — та сама <see cref="Spot"/>, що в юрми, тож і смуга, і стояння лишаються «селянськими».
    /// </summary>
    public void Aim(CrowdVillager v, int cell, int stall)
    {
        v.Target = cell;
        v.TargetStall = stall;
        (v.Tx, v.Ty) = Spot(cell);
        v.Stand = 0;
        v.Wander = 0;
        v.WanderNext = false;
    }

    /// <summary>Гравець-бот іде просто до точки (за підозрілим): точка селянина завжди прохідна для коробки.</summary>
    public static void AimAt(CrowdVillager v, int x, int y)
    {
        v.Target = CrowdMap.CellOf(x, y);
        v.TargetStall = -1;
        (v.Tx, v.Ty) = (x, y);
        v.Stand = 0;
        v.Wander = 0;
        v.WanderNext = false;
    }

    /// <summary>Бот устав після падіння чи став ботом після виходу гравця: думає з чистого аркуша, недоторгованого не купує.</summary>
    public static void Forget(CrowdVillager v)
    {
        v.Target = -1;
        v.TargetStall = -1;
        v.Want = -1;
        v.Stand = 0;
        v.Wander = 0;
        v.WanderNext = false;
        v.Blocked = false;
        v.Haggle = 0;
        v.HaggleStall = -1;
    }

    // ---------------------------------------------------------------------------------------------
    // Рогатка
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Найближчий до стрільця селянин у конусі ±35° на <see cref="ShotRange"/> перед ним (стоїть чи йде); при
    /// рівній відстані — менший id. -1 — нікого. Той самий алгоритм малює підсвітку на клієнті.
    /// </summary>
    public int Cone(CrowdVillager p)
    {
        int best = -1;
        long bestD = long.MaxValue;
        int fx = DX[p.Dir], fy = DY[p.Dir];
        var v = V;
        for (var i = 0; i < v.Length; i++)
        {
            var q = v[i];
            if (q == p || !q.Upright) continue;
            long dx = q.X - p.X, dy = q.Y - p.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 > (long)ShotRange * ShotRange) continue;
            var dot = dx * fx + dy * fy;
            if (dot <= 0 || dot * dot * 1000 < ConeCos2Milli * d2) continue;
            if (d2 < bestD)
            {
                bestD = d2;
                best = i;
            }
        }
        return best;
    }

    public static long Dist2(CrowdVillager a, int x, int y)
    {
        long dx = a.X - x, dy = a.Y - y;
        return dx * dx + dy * dy;
    }

    /// <summary>
    /// Прилавок, на якому стоїть селянин (його центр — у одній із двох клітинок стежки перед корпусом); -1 — ні на
    /// якому. Звідси й торгуються: обидві клітинки лежать у межах <see cref="BuyRange"/> від точки прилавка.
    /// </summary>
    public static int CounterAt(CrowdVillager v) =>
        v.X < 0 || v.Y < 0 || v.X >= CrowdMap.WorldW || v.Y >= CrowdMap.WorldH ? -1 : CrowdMap.CounterOf[CrowdMap.CellOf(v.X, v.Y)];

    // ---------------------------------------------------------------------------------------------
    // Раунд
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Нова юрма: <paramref name="owners"/> — місця гравців (ідуть першими), далі <paramref name="bots"/> ботів.
    /// Порядок випадковості: розстановка (гравці — не ближче 3 клітинок один до одного) → тасування id →
    /// вигляд → імена → перше стояння ботів. Прилавки на старті вільні для всіх однаково.
    /// </summary>
    public void Deal(IReadOnlyList<int> owners, int bots)
    {
        var n = owners.Count + bots;
        var list = new CrowdVillager[n];
        var walk = CrowdMap.Walkable;
        for (var i = 0; i < n; i++)
        {
            var v = new CrowdVillager { Owner = i < owners.Count ? owners[i] : -1 };
            int cell = 0;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                cell = walk[_rng.Next(walk.Length)];
                if (CrowdMap.CounterOf[cell] >= 0) continue;
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
    }

    /// <summary>Гравця не ставимо ближче 3 клітинок (Чебишев) до вже поставлених гравців.</summary>
    static bool TooClose(CrowdVillager[] placed, int count, int cell)
    {
        int cx = cell % CrowdMap.W, cy = cell / CrowdMap.W;
        for (var i = 0; i < count; i++)
        {
            var p = placed[i];
            if (p.Owner < 0) continue;
            int px = p.X / CrowdMap.Cell, py = p.Y / CrowdMap.Cell;
            if (Math.Max(Math.Abs(px - cx), Math.Abs(py - cy)) < 3) return true;
        }
        return false;
    }

    /// <summary>Кадр: 4 числа на селянина за id — x, y, d, s. Новий масив щоразу: розсилка серіалізує його вже поза замком.</summary>
    public int[] Pack()
    {
        var v = V;
        var a = new int[v.Length * 4];
        for (var i = 0; i < v.Length; i++)
        {
            var q = v[i];
            a[i * 4] = q.X;
            a[i * 4 + 1] = q.Y;
            a[i * 4 + 2] = q.Dir;
            a[i * 4 + 3] = q.State;
        }
        return a;
    }

    /// <summary>Вигляд за id: hat, hatColor, shirt, skin.</summary>
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
        for (var i = 0; i < v.Length; i++) a[i] = CrowdMap.Names[v[i].Name];
        return a;
    }
}
