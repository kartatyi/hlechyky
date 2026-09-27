namespace Hlechyky.Games.Impl;

/// <summary>
/// Один танцюрист на вечорницях. Бот і гравець — той самий клас, той самий <see cref="DanceCore.Step"/> і та сама
/// <see cref="DanceCore.Perform"/> (фігура): гравцеві <see cref="Want"/> і фігуру пише ввід, ботові — мозок. Решта
/// полів мозку в гравця просто лежать.
/// </summary>
public sealed class DanceVillager
{
    /// <summary>Номер у кадрі; за раунд не міняється, між раундами тасується.</summary>
    public int Id;
    /// <summary>Точка на землі в одиницях світу (клітинка — 32).</summary>
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
    /// <summary>Гравця вивели ляпасом: сидить до кінця раунду.</summary>
    public bool Dead;
    /// <summary>Бот, якому дали ляпаса, сидить ображений ще стільки тиків.</summary>
    public int Offended;
    /// <summary>Гравець «отетерів» після ляпаса по боту — ще стільки тиків ні кроку, ні фігури.</summary>
    public int Stun;
    /// <summary>Фігура триває ще стільки тиків (танцюрист стоїть, хоч би що хотів).</summary>
    public int Pose;
    /// <summary>Яку фігуру показує (0 плескай, 1 присядь, 2 крутись, 3 руки вгору) і чи це влучно.</summary>
    public int PoseFig = -1;
    public bool PoseOk;
    /// <summary>«Збився» — знак питання над головою ще стільки тиків (хто не вціляв фігуру, видно всім).</summary>
    public int Miss;

    // ---- танець поточного виклику ----
    /// <summary>Уже показав фігуру на цей виклик (будь-яку, будь-коли) — вдруге не можна.</summary>
    public bool Danced;
    /// <summary>Вціляв: та фігура й у вікні такту.</summary>
    public bool Hit;
    /// <summary>Вціляв, стоячи в колі.</summary>
    public bool HitIn;
    /// <summary>План бота на виклик: тик і фігура; -1 — не танцює (замріявся).</summary>
    public int PlanAt = -1, PlanFig = -1;

    // ---- мозок бота ----
    /// <summary>Клітинка цілі; -1 — треба обрати.</summary>
    public int Target = -1;
    /// <summary>Точка цілі в клітинці (центр ± до 15) — її зсув від центру й «смуга», якою бот іде.</summary>
    public int Tx, Ty;
    public int Stand;
    public int Wander;
    public bool WanderNext;

    // ---- вигляд на раунд ----
    /// <summary>0 — дівчина, 1 — парубок.</summary>
    public int Kind;
    /// <summary>Убір: дівчина — вінок / хустка / коса; парубок — смушева шапка / бриль / чуб.</summary>
    public int Head;
    /// <summary>Колір сорочки чи вишивки й колір стрічок чи пояса (палітра з 8).</summary>
    public int C1, C2;
    public int Skin, Name;

    /// <summary>
    /// Стан у кадрі: 0 стоїть, 1 іде, 2 ображений бот сидить, 3 вибулий гравець, 4 отетерів, 5…8 — фігура 0…3
    /// вціляна, 9…12 — фігура 0…3 не та чи не вчасно, 13 — збився без фігури («?»).
    /// </summary>
    public int State => Dead ? 3
        : Offended > 0 ? 2
        : Stun > 0 ? 4
        : Pose > 0 ? (PoseOk ? 5 : 9) + PoseFig
        : Miss > 0 ? 13
        : Moving ? 1 : 0;

    /// <summary>На ногах: не вибув і не сидить ображений.</summary>
    public bool Upright => !Dead && Offended == 0;
}

/// <summary>
/// Вечорниці без кімнат і очок: танцюристи, один спільний крок, одна спільна фігура, мозок ботів, ляпас,
/// розстановка раунду. Усе ціле, уся випадковість — з переданого <see cref="Random"/> у строгому порядку, тож той
/// самий сід і той самий ввід дають побайтно ті самі кадри. У гарячому циклі — жодної алокації, крім кадру.
/// </summary>
public sealed class DanceCore(Random rng)
{
    /// <summary>Крок за тик — однаковий для всіх: 75 од/с ≈ 2,3 клітинки/с. Бігу нема.</summary>
    public const int Speed = 3;
    /// <summary>Половина коробки 16×16 (для перешкод; один крізь одного танцюристи проходять — тіснява ж).</summary>
    public const int Half = 8;

    // ---- фігури ----
    public const int Figures = 4;
    /// <summary>Фігуру показують 15 тиків (0,6 с) — і гравець, і бот; увесь цей час стоять.</summary>
    public const int PoseTicks = 15;
    /// <summary>Вікно такту: від 5 тиків до (−200 мс) до 10 після (+400 мс; клієнт бачить кадр на ~80 мс пізніше).</summary>
    public const int Early = 5, Late = 10;
    /// <summary>Спізнену фігуру ще приймають до +25 тиків (1 с) — вона видна, але вже «не в такт».</summary>
    public const int LateMax = 25;
    /// <summary>«?» над тим, хто збився, — 1 с.</summary>
    public const int MissTicks = 25;
    /// <summary>
    /// Боти теж не ідеальні — у тисячних на виклик: замріявся й не станцював нічого, спізнився, переплутав фігуру.
    /// Разом ≈ 9 %: на 30 ботів двоє-троє збиваються щоразу, тож одна помилка — не вирок «це гравець».
    /// </summary>
    public const int BotNoneMilli = 15, BotLateMilli = 40, BotWrongMilli = 35;

    // ---- ляпас ----
    /// <summary>Ляпас без цілі — найближчому в ±45° перед собою на 36 одиниць (трохи більше клітинки).</summary>
    public const int SlapRange = 36;
    /// <summary>Явна ціль (клік чи тап по танцюристу) — до 52 одиниць у будь-який бік: клієнт бачить кадр трохи старший.</summary>
    public const int SlapRangeMax = 52;
    /// <summary>cos²45° = 0,5 — у тисячних.</summary>
    public const int SlapCos2Milli = 500;
    /// <summary>Ображений бот сидить 3 с.</summary>
    public const int OffendTicks = 75;

    // ---- мозок (як у Юрмі: цілі, паузи, стояння, переступання) ----
    /// <summary>Скільки бот стоїть, коли дійшов: 74 % — 0,5–3 с, 14 % — 3–6 с, 8 % — 6–12 с, 4 % — 12–20 с.</summary>
    public const int StandMin = 12, StandMax = 75, LongStandMax = 500;
    /// <summary>Посеред дороги бот інколи зупиняється на 1–20 тиків — «завагався», як людина, що відпустила клавішу.</summary>
    public const int PauseMilli = 9, PauseMax = 20;
    /// <summary>Стоячи, бот інколи переступає: 1–3 тики кроку в випадковий бік.</summary>
    public const int FidgetMilli = 8;
    /// <summary>Відсоток «тиняння» після стояння і скільки воно триває.</summary>
    public const int WanderChance = 15, WanderMin = 8, WanderMax = 24;
    /// <summary>
    /// Куди йде бот: у коло — 35 %, до музик — 10 %, решта — будь-куди. Хто вже в колі, той у 65 % випадків і далі
    /// танцює в колі: так у колі завжди є ті, хто стоїть там кілька фігур поспіль, — і гравець, що витанцьовує
    /// стрічку, серед них не виділяється.
    /// </summary>
    public const int RingPick = 35, RingStay = 65, StagePick = 10;
    /// <summary>Де в клітинці стати чи якою смугою йти: зсув до ±15, де коробка влазить; де тісно — до ±7.</summary>
    public const int SpotMax = 15, TightMax = 7;
    /// <summary>На старті раунду бот ще стоїть 0…6 с: гравець, що рушає лише на сигнал, не виділяється.</summary>
    public const int OpeningStandMax = 150;

    public static readonly int[] DX = [1, 0, -1, 0];
    public static readonly int[] DY = [0, 1, 0, -1];

    readonly Random _rng = rng;
    readonly int[] _girls = new int[DanceMap.Girls.Length];
    readonly int[] _boys = new int[DanceMap.Boys.Length];

    public DanceVillager[] V { get; private set; } = [];
    public int N => V.Length;

    /// <summary>Тик раунду — його пише гра перед мозком ботів (план бота — на конкретний тик).</summary>
    public int T { get; set; }
    /// <summary>Фігура, яку зараз кличуть (-1 — ніякої), і тик її такту.</summary>
    public int CallFig { get; private set; } = -1;
    public int CallBeat { get; private set; }

    // ---------------------------------------------------------------------------------------------
    // Крок — один на всіх
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Крок танцюриста. Хто танцює фігуру, отетерів, сидить чи нікуди не хоче — стоїть. Інакше повертається в бік
    /// <see cref="DanceVillager.Want"/> (навіть коли далі стіна) і робить рівно <see cref="Speed"/> одиниць, якщо
    /// коробка там уміщається. Ця функція одна для ботів і гравців.
    /// </summary>
    public static void Step(DanceVillager v)
    {
        if (v.Pose > 0 || v.Offended > 0 || v.Stun > 0 || v.Dead || v.Want < 0)
        {
            v.Moving = false;
            v.Blocked = false;
            return;
        }
        v.Dir = v.Want;
        int nx = v.X + DX[v.Dir] * Speed, ny = v.Y + DY[v.Dir] * Speed;
        if (DanceMap.BoxFits(nx, ny))
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

    /// <summary>Годинники: фігура, «?», отетеріння, образа (хто відсидівся — встає й думає з чистого аркуша).</summary>
    public void TimersAll()
    {
        var v = V;
        for (var i = 0; i < v.Length; i++)
        {
            var q = v[i];
            if (q.Pose > 0) q.Pose--;
            if (q.Miss > 0) q.Miss--;
            if (q.Stun > 0) q.Stun--;
            if (q.Offended > 0 && --q.Offended == 0) Forget(q);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Фігури — одна функція на всіх
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Музики вигукнули фігуру з тактом на тику <paramref name="beat"/>. Кожен бот (на ногах) одразу вирішує, коли й що
    /// станцює: здебільшого ту саму фігуру в межах −5…+10 тиків від такту (як людина, що бачить відлік і тисне),
    /// а зрідка — замріється, спізниться чи переплутає.
    /// </summary>
    public void Announce(int fig, int beat)
    {
        CallFig = fig;
        CallBeat = beat;
        var v = V;
        for (var i = 0; i < v.Length; i++)
        {
            var q = v[i];
            q.Danced = q.Hit = q.HitIn = false;
            q.PlanAt = q.PlanFig = -1;
            if (q.Owner >= 0 || !q.Upright) continue;
            var r = _rng.Next(1000);
            if (r < BotNoneMilli) continue;                                          // замріявся
            if (r < BotNoneMilli + BotLateMilli)
            {
                q.PlanAt = beat + _rng.Next(Late + 1, LateMax + 1);                  // спізнився
                q.PlanFig = fig;
                continue;
            }
            q.PlanAt = beat + BotOffset();
            q.PlanFig = r < BotNoneMilli + BotLateMilli + BotWrongMilli ? (fig + _rng.Next(1, Figures)) % Figures : fig;
        }
    }

    /// <summary>Коли бот «тисне» відносно такту: −5…+10 тиків, найчастіше +1…+4 — як людина з реакцією й мережею.</summary>
    int BotOffset() => _rng.Next(-Early, 4) + _rng.Next(0, 8);

    /// <summary>Виклик скінчився (минуло <see cref="LateMax"/>): спізнених фігур більше не приймають.</summary>
    public void EndCall()
    {
        CallFig = -1;
        foreach (var q in V) q.PlanAt = q.PlanFig = -1;
    }

    /// <summary>
    /// Показати фігуру — однаково для гравця й бота. <paramref name="tick"/> — тик, у якому фігуру вперше видно в
    /// кадрі: для гравця (дія між тиками) це наступний тик, для бота — поточний. Тому гравцеві й таймер на тик довший
    /// (<paramref name="between"/>): в обох фігура стоїть у кадрах рівно <see cref="PoseTicks"/> − 1 тиків.
    /// Вціляв — коли фігура та сама, що кличуть, і в межах вікна такту; «в колі» — якщо стоїть у колі.
    /// </summary>
    public bool Perform(DanceVillager v, int fig, int tick, bool between)
    {
        if (CallFig < 0 || v.Danced || !v.Upright || v.Stun > 0 || v.Pose > 0 || fig < 0 || fig >= Figures) return false;
        var d = tick - CallBeat;
        if (d < -Early || d > LateMax) return false;
        v.Danced = true;
        v.PoseFig = fig;
        v.Pose = between ? PoseTicks : PoseTicks - 1;
        v.Moving = false;
        v.PoseOk = fig == CallFig && d <= Late;
        v.Hit = v.PoseOk;
        v.HitIn = v.Hit && DanceMap.InCircle(v.X, v.Y);
        return true;
    }

    /// <summary>
    /// Вікно такту закрилось: кожен на ногах, хто не вціляв (не станцював, спізнився, переплутав чи отетерів), дістає
    /// «?» над головою — бот так само, як гравець.
    /// </summary>
    public void Judge()
    {
        var v = V;
        for (var i = 0; i < v.Length; i++)
        {
            var q = v[i];
            if (q.Upright && !q.Hit) q.Miss = MissTicks;
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
    /// Бот: на свій тик плану — фігура (тією ж <see cref="Perform"/>, що й у гравця); поки танцює — стоїть; решту часу
    /// ходить так, щоб у кадрах його не відрізнити від людини: до цілі своєю смугою, посеред дороги інколи завагається,
    /// дійшов — стоїть де завгодно в клітинці, стоячи інколи переступає, після стояння інколи тиняється.
    /// </summary>
    public void Think(DanceVillager v)
    {
        if (v.Dead || v.Offended > 0 || v.Stun > 0) return;
        if (v.PlanAt == T && v.PlanFig >= 0)
        {
            var fig = v.PlanFig;
            v.PlanAt = v.PlanFig = -1;
            if (Perform(v, fig, T, false)) return;
        }
        if (v.Pose > 0) return;                              // танцює — мозок чекає; Step і так не рушить
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
            v.Blocked = false;
            v.Target = -1;
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

        var cell = DanceMap.CellOf(v.X, v.Y);
        if (cell == v.Target)
        {
            Approach(v);
            return;
        }
        var hop = DanceMap.NextHop[v.Target * DanceMap.Cells + cell];
        if (hop == DanceMap.NoHop)
        {
            v.Target = -1;
            v.Want = -1;
            return;
        }
        // Вирівнятись на смугу поперек кроку, потім іти; де з таким зсувом коробка не пролізе — ближче до центру.
        if (hop is 0 or 2)
        {
            int mid = DanceMap.CenterY(cell), off = v.Ty - DanceMap.CenterY(v.Target), next = DanceMap.CenterX(cell) + DX[hop] * DanceMap.Cell;
            var lane = mid + off;
            if (!Fits4(v.X, next, lane, true)) lane = mid + Math.Clamp(off, -TightMax, TightMax);
            if (v.Y < lane - 1) { v.Want = 1; return; }
            if (v.Y > lane + 1) { v.Want = 3; return; }
        }
        else
        {
            int mid = DanceMap.CenterX(cell), off = v.Tx - DanceMap.CenterX(v.Target), next = DanceMap.CenterY(cell) + DY[hop] * DanceMap.Cell;
            var lane = mid + off;
            if (!Fits4(v.Y, next, lane, false)) lane = mid + Math.Clamp(off, -TightMax, TightMax);
            if (v.X < lane - 1) { v.Want = 0; return; }
            if (v.X > lane + 1) { v.Want = 2; return; }
        }
        v.Want = hop;
    }

    static bool Fits4(int at, int next, int lane, bool horizontal) => horizontal
        ? DanceMap.BoxFits(at, lane - 1) && DanceMap.BoxFits(at, lane + 1) && DanceMap.BoxFits(next, lane - 1) && DanceMap.BoxFits(next, lane + 1)
        : DanceMap.BoxFits(lane - 1, at) && DanceMap.BoxFits(lane + 1, at) && DanceMap.BoxFits(lane - 1, next) && DanceMap.BoxFits(lane + 1, next);

    static bool CanStep(DanceVillager v, int d) => DanceMap.BoxFits(v.X + DX[d] * Speed, v.Y + DY[d] * Speed);

    /// <summary>У клітинці цілі — до своєї точки: спершу по довшій осі, де не пролізти — по іншій; ніяк — стає тут.</summary>
    void Approach(DanceVillager v)
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

    /// <summary>Дійшов: стоїть <see cref="StandTicks"/>, обличчя не міняє (на місці повернутись людина не може).</summary>
    void Arrive(DanceVillager v)
    {
        v.Target = -1;
        v.Want = -1;
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

    void PickTarget(DanceVillager v)
    {
        var ring = DanceMap.InCircle(v.X, v.Y) ? RingStay : RingPick;
        var r = _rng.Next(100);
        var cell = r < ring ? DanceMap.Ring[_rng.Next(DanceMap.Ring.Length)]
            : r < ring + StagePick ? DanceMap.Stage[_rng.Next(DanceMap.Stage.Length)]
            : DanceMap.Walkable[_rng.Next(DanceMap.Walkable.Length)];
        v.Target = cell;
        (v.Tx, v.Ty) = Spot(cell);
    }

    /// <summary>Точка в клітинці: зсув до ±15, якщо коробка влазить (три спроби), далі — до ±7 (влазить завжди).</summary>
    (int X, int Y) Spot(int cell)
    {
        int cx = DanceMap.CenterX(cell), cy = DanceMap.CenterY(cell);
        for (var i = 0; i < 3; i++)
        {
            int x = cx + _rng.Next(-SpotMax, SpotMax), y = cy + _rng.Next(-SpotMax, SpotMax);
            if (DanceMap.BoxFits(x, y)) return (x, y);
        }
        return (cx + _rng.Next(-TightMax, TightMax + 1), cy + _rng.Next(-TightMax, TightMax + 1));
    }

    /// <summary>Бот устав після образи чи став ботом після виходу гравця: думає з чистого аркуша.</summary>
    public static void Forget(DanceVillager v)
    {
        v.Target = -1;
        v.Want = -1;
        v.Stand = 0;
        v.Wander = 0;
        v.WanderNext = false;
        v.Blocked = false;
    }

    // ---------------------------------------------------------------------------------------------
    // Ляпас
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Найближчий на ногах у ±45° на <see cref="SlapRange"/> перед танцюристом; при рівній відстані — менший id.
    /// -1 — нікого. Той самий алгоритм підсвічує ціль на клієнті.
    /// </summary>
    public int Reach(DanceVillager p)
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
            if (d2 > (long)SlapRange * SlapRange) continue;
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

    public static long Dist2(DanceVillager a, int x, int y)
    {
        long dx = a.X - x, dy = a.Y - y;
        return dx * dx + dy * dy;
    }

    // ---------------------------------------------------------------------------------------------
    // Раунд
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Нові вечорниці: <paramref name="owners"/> — місця гравців (ідуть першими), далі <paramref name="bots"/> ботів.
    /// Порядок випадковості: розстановка (гравці — не ближче 3 клітинок один до одного) → тасування id → вигляд →
    /// імена → перше стояння ботів.
    /// </summary>
    public void Deal(IReadOnlyList<int> owners, int bots)
    {
        var n = owners.Count + bots;
        var list = new DanceVillager[n];
        var walk = DanceMap.Walkable;
        for (var i = 0; i < n; i++)
        {
            var v = new DanceVillager { Owner = i < owners.Count ? owners[i] : -1 };
            var cell = 0;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                cell = walk[_rng.Next(walk.Length)];
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
        int girls = 0, boys = 0;
        for (var i = 0; i < n; i++)
        {
            var v = list[i];
            v.Id = i;
            v.Kind = _rng.Next(2);
            // імен по 32: щоб жодне не повторилось, перебір однієї статі віддаємо іншій
            if (v.Kind == 0 && girls >= _girls.Length) v.Kind = 1;
            else if (v.Kind == 1 && boys >= _boys.Length) v.Kind = 0;
            if (v.Kind == 0) girls++;
            else boys++;
            v.Head = _rng.Next(3);
            v.C1 = _rng.Next(8);
            v.C2 = _rng.Next(8);
            v.Skin = _rng.Next(4);
        }
        Shuffle(_girls);
        Shuffle(_boys);
        int g = 0, b = 0;
        for (var i = 0; i < n; i++)
            list[i].Name = list[i].Kind == 0 ? _girls[g++ % _girls.Length] : _boys[b++ % _boys.Length];
        for (var i = 0; i < n; i++)
            if (list[i].Owner < 0) list[i].Stand = _rng.Next(0, OpeningStandMax + 1);
        V = list;
        CallFig = -1;
    }

    void Shuffle(int[] a)
    {
        for (var i = 0; i < a.Length; i++) a[i] = i;
        for (var i = a.Length - 1; i > 0; i--)
        {
            var j = _rng.Next(i + 1);
            (a[i], a[j]) = (a[j], a[i]);
        }
    }

    /// <summary>Гравця не ставимо ближче 3 клітинок (Чебишев) до вже поставлених гравців.</summary>
    static bool TooClose(DanceVillager[] placed, int count, int cell)
    {
        int cx = cell % DanceMap.W, cy = cell / DanceMap.W;
        for (var i = 0; i < count; i++)
        {
            var p = placed[i];
            if (p.Owner < 0) continue;
            int px = p.X / DanceMap.Cell, py = p.Y / DanceMap.Cell;
            if (Math.Max(Math.Abs(px - cx), Math.Abs(py - cy)) < 3) return true;
        }
        return false;
    }

    /// <summary>Кадр: 4 числа на танцюриста за id — x, y, d, s. Новий масив щоразу: розсилка серіалізує його вже поза замком.</summary>
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

    /// <summary>Вигляд за id: kind, head, c1, c2, skin.</summary>
    public int[] Looks()
    {
        var v = V;
        var a = new int[v.Length * 5];
        for (var i = 0; i < v.Length; i++)
        {
            a[i * 5] = v[i].Kind;
            a[i * 5 + 1] = v[i].Head;
            a[i * 5 + 2] = v[i].C1;
            a[i * 5 + 3] = v[i].C2;
            a[i * 5 + 4] = v[i].Skin;
        }
        return a;
    }

    public string[] NamesNow()
    {
        var v = V;
        var a = new string[v.Length];
        for (var i = 0; i < v.Length; i++) a[i] = v[i].Kind == 0 ? DanceMap.Girls[v[i].Name] : DanceMap.Boys[v[i].Name];
        return a;
    }
}
