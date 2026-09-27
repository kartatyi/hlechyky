namespace Hlechyky.Games.Impl;

/// <summary>
/// Один відвідувач корчми. Бот і гравець — той самий клас і той самий <see cref="TavernCore.Step"/>: гравцеві
/// <see cref="Want"/> пише ввід, ботові — <see cref="TavernCore.Think"/>. Решта полів мозку в гравця просто лежать.
/// </summary>
public sealed class TavernGuest
{
    /// <summary>Номер у кадрі; за раунд не міняється, між раундами тасується.</summary>
    public int Id;
    /// <summary>Точка на долівці в одиницях світу (клітинка — 32).</summary>
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
    /// <summary>Гравець вибув (серця скінчились): лежить до кінця раунду.</summary>
    public bool Out;
    /// <summary>Лежить ще стільки тиків (збили з ніг або викинули за двері).</summary>
    public int Fallen;
    /// <summary>«Отетерів» після удару, що звалив когось: ще стільки тиків стоїть і кліпає.</summary>
    public int Dazed;
    /// <summary>Замах: за стільки тиків кулак долетить.</summary>
    public int Wind;
    /// <summary>Відкинуло ударом: ще стільки тиків летить назад у бік <see cref="KnockDir"/>.</summary>
    public int Knock, KnockDir;
    /// <summary>П'є: ще стільки тиків; з якого місця.</summary>
    public int Drink, DrinkPlace = -1;
    /// <summary>Сидить на лаві.</summary>
    public bool Sit;
    /// <summary>Встає з лави: ще стільки тиків (поки встає — сидить).</summary>
    public int Rise;
    /// <summary>Перезарядки кулака й кухля — однакові для всіх.</summary>
    public int PunchCool, DrinkCool;

    // ---- мозок бота ----
    /// <summary>Клітинка цілі; -1 — треба обрати.</summary>
    public int Target = -1;
    /// <summary>Точка цілі в клітинці (центр ± до 15) — її зсув від центру й «смуга», якою бот іде й повертає.</summary>
    public int Tx, Ty;
    /// <summary>Ціль — приступка цього місця; -1 — ні.</summary>
    public int TargetPlace = -1;
    /// <summary>Ціль — лава: дійде й сяде.</summary>
    public bool TargetBench;
    public int Stand;
    public int Wander;
    public bool WanderNext;
    /// <summary>П'яненький: хитається на ходу, частіше лізе в бійку й інколи плює на корчмаря.</summary>
    public bool Drunk;
    /// <summary>Кому дати здачі (id) і скільки ще тиків отямлюватись; -1 — нікому.</summary>
    public int Grudge = -1, GrudgeWait;

    // ---- вигляд на раунд ----
    public int Hat, HatColor, Shirt, Skin, Name;

    /// <summary>
    /// Стан у кадрі: 0 стоїть, 1 іде, 2 сидить, 3 замах, 4 п'є, 5 отетерів, 6 лежить, 7 вибув, 8 відкинуло.
    /// </summary>
    public int State => Out ? 7 : Fallen > 0 ? 6 : Knock > 0 ? 8 : Dazed > 0 ? 5 : Wind > 0 ? 3 : Drink > 0 ? 4 : Sit ? 2 : Moving ? 1 : 0;

    /// <summary>На ногах (чи на лаві): його можна вдарити.</summary>
    public bool Upright => !Out && Fallen == 0;

    /// <summary>Вільні руки й ноги: не лежить, не кліпає, не замахується, не п'є, не летить.</summary>
    public bool Free => !Out && Fallen == 0 && Dazed == 0 && Wind == 0 && Drink == 0 && Knock == 0;
}

/// <summary>
/// Корчмар за шинквасом — не відвідувач: ходить своїм закутком, а зрідка гримає «Хто тут б'ється?!» і кілька секунд
/// дивиться на залу. Хто в цей час махне кулаком — того за двері. У кадрі — окремими чотирма числами.
/// </summary>
public sealed class TavernBarman
{
    public int X = (TavernMap.BarmanMinX + TavernMap.BarmanMaxX) / 2, Y = TavernMap.BarmanY, Dir = 1;
    /// <summary>0 — порається, 1 — гримає (попередження), 2 — дивиться (б'єшся — за двері).</summary>
    public int Mode;
    /// <summary>Скільки ще тиків у поточному режимі (гримає чи дивиться).</summary>
    public int Left;
    /// <summary>Через скільки тиків гримне (лише коли порається).</summary>
    public int NextShout;
    public int Tx = (TavernMap.BarmanMinX + TavernMap.BarmanMaxX) / 2, Pause;

    public bool Watching => Mode == 2;
    public bool Calm => Mode == 0;
}

/// <summary>
/// Корчма без кімнат і очок: відвідувачі, один спільний крок, мозок ботів, досяжність кулака, корчмар, розстановка
/// раунду. Усе ціле, уся випадковість — з переданого <see cref="Random"/> у строгому порядку, тож той самий сід і той
/// самий ввід дають побайтно ті самі кадри. У гарячому циклі — жодної алокації, крім кадру.
/// </summary>
public sealed class TavernCore(Random rng)
{
    /// <summary>Крок за тик — однаковий для всіх: 75 од/с ≈ 2,3 клітинки/с. Бігу нема.</summary>
    public const int Speed = 3;
    /// <summary>Половина коробки відвідувача 16×16 (для перешкод; один крізь одного відвідувачі проходять).</summary>
    public const int Half = 8;
    /// <summary>Кулак дістає на стільки одиниць перед собою (трохи більше клітинки), у конусі ±45°.</summary>
    public const int PunchReach = 40, PunchCos2Milli = 500;
    /// <summary>Хто стоїть майже впритул (до 10 од.), того дістане з будь-якого боку.</summary>
    public const int PunchClose = 10;
    /// <summary>Замах 0,4 с — його видно всім, як в Unspottable; за ним — «бам!».</summary>
    public const int WindTicks = 10;
    /// <summary>Між ударами — секунда.</summary>
    public const int PunchCoolTicks = 25;
    /// <summary>Хто звалив відвідувача, той отетеріло кліпає 1,5 с — так забіяка себе й видає.</summary>
    public const int DazeTicks = 38;
    /// <summary>Збитий лежить 2 с (і викинутий за двері — теж).</summary>
    public const int FallTicks = 50;
    /// <summary>Відкинуло: 6 тиків по 6 од. — 36 одиниць, трохи більше клітинки.</summary>
    public const int KnockTicks = 6, KnockSpeed = 6;
    /// <summary>Кухоль — секунда біля шинквасу чи бочки; потім 2 с віддихатись.</summary>
    public const int DrinkTicks = 25, DrinkCoolTicks = 50;
    /// <summary>Підвестись із лави — мить (0,4 с): сидячий не втече одразу.</summary>
    public const int RiseTicks = 10;
    /// <summary>Скільки бот стоїть, коли дійшов: звичайно 0,5–3 с, а кожен четвертий задивляється довше.</summary>
    public const int StandMin = 12, StandMax = 75, LongStandMax = 500;
    /// <summary>Посеред дороги бот інколи зупиняється на 1–20 тиків — «завагався», як людина, що відпустила клавішу.</summary>
    public const int PauseMilli = 9, PauseMax = 20;
    /// <summary>Стоячи, бот інколи переступає: 1–3 тики кроку в випадковий бік.</summary>
    public const int FidgetMilli = 8;
    /// <summary>Відсоток «тиняння» після стояння і скільки воно триває.</summary>
    public const int WanderChance = 15, WanderMin = 8, WanderMax = 24;
    /// <summary>Цілі бота: приступка (30 %), лава (30 %), решта — будь-яка клітинка.</summary>
    public const int PlacePick = 30, BenchPick = 30;
    /// <summary>Бот, що дійшов до приступки, у стількох відсотках справді п'є — у корчмі ж.</summary>
    public const int BotDrinkPct = 55;
    /// <summary>
    /// Бійки: бот, перед яким хтось стоїть у досяжності кулака, замахується з імовірністю стільки на 10 000 за тик
    /// (п'яненький — утричі частіше). На 30 ботів — удар десь раз на 5–8 с: удар не вирок «це гравець».
    /// </summary>
    public const int BrawlPer10K = 20, DrunkBrawlPer10K = 60;
    /// <summary>Кого вдарили (бота), той у стількох відсотках дає здачі, щойно отямиться.</summary>
    public const int GrudgePct = 35;
    /// <summary>П'яненьких ботів — стільки відсотків; на ходу хитаються (з імовірністю в тисячних за тик).</summary>
    public const int DrunkPct = 12, SwayMilli = 12;
    /// <summary>П'яненький махає кулаком і тоді, як корчмар дивиться, — у стількох відсотках.</summary>
    public const int DrunkIgnorePct = 50;
    /// <summary>Де в клітинці стати чи якою смугою йти: до ±15 (уся клітинка), де тісно — до ±7.</summary>
    public const int SpotMax = 15, TightMax = 7;
    /// <summary>На старті раунду бот ще може простояти 0…6 с: гравець, що роздивлявся, не виділяється.</summary>
    public const int OpeningStandMax = 150;
    /// <summary>Хто на старті стоїть на лаві — сидить на ній (боти, у стількох відсотках).</summary>
    public const int OpeningSitPct = 50;

    // ---- корчмар ----
    /// <summary>Гримає 1 с (попередження), дивиться 4 с.</summary>
    public const int ShoutTicks = 25, WatchTicks = 100;
    /// <summary>Перший окрик — за 6–15 с від початку раунду, далі — кожні 8–20 с.</summary>
    public const int FirstShoutMin = 150, FirstShoutMax = 375, ShoutGapMin = 200, ShoutGapMax = 500;
    /// <summary>Почув бійку: у стількох відсотках гримне вже за 0,4–1,6 с.</summary>
    public const int ProvokePct = 30, ProvokeMin = 10, ProvokeMax = 40;
    /// <summary>Крок корчмаря за шинквасом.</summary>
    public const int BarmanSpeed = 2;

    public static readonly int[] DX = [1, 0, -1, 0];
    public static readonly int[] DY = [0, 1, 0, -1];

    readonly Random _rng = rng;
    readonly int[] _nameOrder = new int[TavernMap.Names.Length];

    public TavernGuest[] V { get; private set; } = [];
    public int N => V.Length;
    public TavernBarman Barman { get; } = new();

    /// <summary>Корчма відчинена (фаза «go»): лише тоді боти п'ють і б'ються — гравцям на «роздивись» теж не можна.</summary>
    public bool Open { get; set; } = true;

    // ---------------------------------------------------------------------------------------------
    // Крок — один на всіх
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Крок відвідувача. Хто лежить, кліпає, замахується чи п'є — стоїть. Кого відкинуло — летить назад по 6 од., поки не
    /// вперся. Хто сидить і захотів іти — спершу встає <see cref="RiseTicks"/>. Інакше повертається в бік
    /// <see cref="TavernGuest.Want"/> (навіть коли далі стіна) і робить рівно <see cref="Speed"/> одиниць, якщо коробка там
    /// уміщається. Ця функція одна для ботів і гравців.
    /// </summary>
    public static void Step(TavernGuest v)
    {
        v.Moving = false;
        v.Blocked = false;
        if (v.Out || v.Fallen > 0 || v.Dazed > 0) return;
        if (v.Knock > 0)
        {
            v.Knock--;
            int kx = v.X + DX[v.KnockDir] * KnockSpeed, ky = v.Y + DY[v.KnockDir] * KnockSpeed;
            if (TavernMap.BoxFits(kx, ky))
            {
                v.X = kx;
                v.Y = ky;
            }
            else v.Knock = 0;            // уперся в стіл чи стіну — далі не летить
            return;
        }
        if (v.Wind > 0 || v.Drink > 0) return;
        if (v.Sit)
        {
            if (v.Rise > 0)
            {
                if (--v.Rise == 0) v.Sit = false;
            }
            else if (v.Want >= 0) v.Rise = RiseTicks - 1;
            return;
        }
        if (v.Want < 0) return;
        v.Dir = v.Want;
        int nx = v.X + DX[v.Dir] * Speed, ny = v.Y + DY[v.Dir] * Speed;
        if (TavernMap.BoxFits(nx, ny))
        {
            v.X = nx;
            v.Y = ny;
            v.Moving = true;
        }
        else v.Blocked = true;
    }

    /// <summary>Усі кроком, за зростанням id.</summary>
    public void StepAll()
    {
        var v = V;
        for (var i = 0; i < v.Length; i++) Step(v[i]);
    }

    int[] _struck = [], _drank = [];

    /// <summary>Чий кулак долетів цього тика (id за зростанням) — після <see cref="TimersAll"/>.</summary>
    public ReadOnlySpan<int> Struck => _struck.AsSpan(0, StruckCount);
    public int StruckCount { get; private set; }
    /// <summary>Хто допив кухоль цього тика (id за зростанням).</summary>
    public ReadOnlySpan<int> Drank => _drank.AsSpan(0, DrankCount);
    public int DrankCount { get; private set; }

    /// <summary>
    /// Годинники відвідувачів: перезарядки, замах, кухоль, «отетерів», лежання. Чий кулак долетів — у <see cref="Struck"/>,
    /// хто допив — у <see cref="Drank"/> (наслідки рахує гра); хто відлежав — встає й думає з чистого аркуша.
    /// </summary>
    public void TimersAll()
    {
        var v = V;
        if (_struck.Length < v.Length) { _struck = new int[v.Length]; _drank = new int[v.Length]; }
        StruckCount = DrankCount = 0;
        for (var i = 0; i < v.Length; i++)
        {
            var q = v[i];
            if (q.PunchCool > 0) q.PunchCool--;
            if (q.DrinkCool > 0) q.DrinkCool--;
            if (q.Wind > 0 && --q.Wind == 0) _struck[StruckCount++] = i;
            if (q.Drink > 0 && --q.Drink == 0) _drank[DrankCount++] = i;
            if (q.Dazed > 0) q.Dazed--;
            if (q.Fallen > 0 && --q.Fallen == 0 && q.Owner < 0) ForgetPath(q);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Кулак
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Кого дістане кулак: найближчий на ногах (чи на лаві) у конусі ±45° на <see cref="PunchReach"/> перед бійцем, або
    /// впритул (до <see cref="PunchClose"/>) з будь-якого боку; при рівній відстані — менший id. -1 — повітря.
    /// </summary>
    public int Reach(TavernGuest p)
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
            if (d2 > (long)PunchReach * PunchReach) continue;
            if (d2 > (long)PunchClose * PunchClose)
            {
                var dot = dx * fx + dy * fy;
                if (dot <= 0 || dot * dot * 1000 < PunchCos2Milli * d2) continue;
            }
            if (d2 < bestD)
            {
                bestD = d2;
                best = i;
            }
        }
        return best;
    }

    /// <summary>Замах: обертається в бік удару (якщо заданий) і стоїть. Гравцеві — з Act між тиками, ботові — з Think.</summary>
    public static void Swing(TavernGuest v, int dir, int ticks)
    {
        if (dir >= 0) v.Dir = dir;
        v.Wind = ticks;
        v.Moving = false;
    }

    /// <summary>
    /// Кулак влучив: <paramref name="fall"/> — ціль падає, а боєць отетеріло кліпає; інакше ціль відкидає назад. Бот,
    /// якого вдарили, інколи дає здачі, щойно отямиться (хоч би хто його вдарив — бот чи гравець).
    /// </summary>
    public void Hit(TavernGuest a, TavernGuest t, bool fall)
    {
        if (fall)
        {
            Fall(t);
            a.Dazed = DazeTicks;
            a.Moving = false;
        }
        else Knock(t, a.Dir);
        if (t.Owner < 0) ForgetPath(t);       // збитий з лави чи з дороги бот далі думає з того місця, куди впав
        if (t.Owner < 0 && _rng.Next(100) < GrudgePct)
        {
            t.Grudge = a.Id;
            t.GrudgeWait = _rng.Next(0, 16);
        }
    }

    /// <summary>Чи впаде ціль від кулака бота: навпіл — упав чи відкинуло (для будь-якої цілі однаково).</summary>
    public bool CoinFall() => _rng.Next(2) == 0;

    static void Drop(TavernGuest t)
    {
        t.Wind = 0;
        t.Drink = 0;
        t.DrinkPlace = -1;
        t.Sit = false;
        t.Rise = 0;
        t.Moving = false;
        t.Blocked = false;
    }

    public static void Fall(TavernGuest t)
    {
        Drop(t);
        t.Knock = 0;
        t.Dazed = 0;
        t.Fallen = FallTicks;
    }

    public static void Knock(TavernGuest t, int dir)
    {
        Drop(t);
        t.Dazed = 0;
        t.Knock = KnockTicks;
        t.KnockDir = dir;
    }

    /// <summary>Гравець вибув: лежить до кінця раунду.</summary>
    public static void KnockOut(TavernGuest t)
    {
        Drop(t);
        t.Knock = 0;
        t.Dazed = 0;
        t.Fallen = 0;
        t.Want = -1;
        t.Out = true;
    }

    /// <summary>
    /// За двері: корчмар виносить забіяку на ґанок, той лежить 2 с і (бот) плентається назад. Однаково для ботів і
    /// гравців; що гравець при цьому втрачає серце — справа гри.
    /// </summary>
    public void ThrowOut(TavernGuest t)
    {
        Drop(t);
        t.Knock = 0;
        t.Dazed = 0;
        var cell = TavernMap.Porch[_rng.Next(TavernMap.Porch.Length)];
        (t.X, t.Y) = Spot(cell);
        t.Dir = 3;
        t.Fallen = FallTicks;
        if (t.Owner < 0) Forget(t);
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

    /// <summary>Чи наважиться бот махнути кулаком зараз: корчмар спокійний, або п'яненькому байдуже.</summary>
    bool Dares(TavernGuest v) => Open && v.PunchCool == 0 && !v.Sit
        && (Barman.Calm || (v.Drunk && _rng.Next(100) < DrunkIgnorePct));

    /// <summary>
    /// Бот виставляє <see cref="TavernGuest.Want"/> так, щоб у кадрах його не відрізнити від людини. Іде до цілі
    /// (приступка, лава чи будь-куди) своєю «смугою»; посеред дороги інколи завагається; дійшов — стоїть де завгодно в
    /// клітинці, на лаві сідає, біля бочки часто п'є; стоячи, інколи переступає. Зрідка б'є того, хто перед ним, а кого
    /// вдарили — дає здачі. П'яненький на ходу хитається.
    /// </summary>
    public void Think(TavernGuest v)
    {
        if (!v.Free) return;                          // лежить, кліпає, замахується, п'є чи летить — Step і так не рушить
        if (v.Grudge >= 0)
        {
            if (v.GrudgeWait > 0) v.GrudgeWait--;
            else
            {
                var g = v.Grudge;
                v.Grudge = -1;
                if (Payback(v, g)) return;
            }
        }
        if (Dares(v) && _rng.Next(10_000) < (v.Drunk ? DrunkBrawlPer10K : BrawlPer10K) && Reach(v) >= 0)
        {
            // бійка: той, хто перед носом, отримує кулаком — як від гравця (той самий замах, той самий «бам»)
            Swing(v, -1, WindTicks - 1);
            v.Want = -1;
            v.Stand = 0;
            v.Wander = 0;
            return;
        }
        if (v.Wander > 0)
        {
            v.Wander--;          // Want уже стоїть; біля стіни Step сам зупинить (лише обернеться)
            return;
        }
        if (v.Stand > 0)
        {
            v.Stand--;
            v.Want = -1;
            if (v.Sit) return;                        // на лаві не переступають
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
            // уперся (після тиняння, хитання чи колишній гравець, що стояв криво) — ціль геть, наступного тика нова
            ForgetPath(v);
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
        if (v.Drunk && !v.Sit && _rng.Next(1000) < SwayMilli)
        {
            // хитнуло: кілька кроків абикуди, потім знову до цілі
            v.Wander = _rng.Next(2, 8);
            v.Want = _rng.Next(4);
            return;
        }

        var cell = TavernMap.CellOf(v.X, v.Y);
        if (cell == v.Target)
        {
            Approach(v);
            return;
        }
        var hop = TavernMap.NextHop[v.Target * TavernMap.Cells + cell];
        if (hop == TavernMap.NoHop)
        {
            ForgetPath(v);
            return;
        }
        // Вирівнятись на смугу поперек напрямку кроку, потім іти. Смуга — зсув цілі від центру; де з таким зсувом
        // коробка не пролізе (тут або в наступній клітинці), — ближче до центру, ±7 пролазить завжди.
        if (hop is 0 or 2)
        {
            int mid = TavernMap.CenterY(cell), off = v.Ty - TavernMap.CenterY(v.Target), next = TavernMap.CenterX(cell) + DX[hop] * TavernMap.Cell;
            var lane = mid + off;
            if (!Fits4(v.X, next, lane, true)) lane = mid + Math.Clamp(off, -TightMax, TightMax);
            if (v.Y < lane - 1) { v.Want = 1; return; }
            if (v.Y > lane + 1) { v.Want = 3; return; }
        }
        else
        {
            int mid = TavernMap.CenterX(cell), off = v.Tx - TavernMap.CenterX(v.Target), next = TavernMap.CenterY(cell) + DY[hop] * TavernMap.Cell;
            var lane = mid + off;
            if (!Fits4(v.Y, next, lane, false)) lane = mid + Math.Clamp(off, -TightMax, TightMax);
            if (v.X < lane - 1) { v.Want = 0; return; }
            if (v.X > lane + 1) { v.Want = 2; return; }
        }
        v.Want = hop;
    }

    /// <summary>
    /// Дати здачі: кривдник поруч і на ногах — обертається до нього (по довшій осі) і замахується, як гравець із
    /// <c>punch {dir}</c>. Далеко чи вже лежить — махнув рукою й пішов далі.
    /// </summary>
    bool Payback(TavernGuest v, int id)
    {
        if (id < 0 || id >= V.Length || !Dares(v)) return false;
        var q = V[id];
        if (!q.Upright) return false;
        int dx = q.X - v.X, dy = q.Y - v.Y;
        if ((long)dx * dx + (long)dy * dy > (long)PunchReach * PunchReach) return false;
        var dir = Math.Abs(dx) >= Math.Abs(dy) ? (dx >= 0 ? 0 : 2) : (dy >= 0 ? 1 : 3);
        Swing(v, dir, WindTicks - 1);
        v.Want = -1;
        v.Stand = 0;
        v.Wander = 0;
        return true;
    }

    /// <summary>
    /// Чи пролізе коробка смугою <paramref name="lane"/> (±1 — стільки лишає крок 3) і тут (<paramref name="at"/> уздовж
    /// руху), і в центрі наступної клітинки (<paramref name="next"/>).
    /// </summary>
    static bool Fits4(int at, int next, int lane, bool horizontal) => horizontal
        ? TavernMap.BoxFits(at, lane - 1) && TavernMap.BoxFits(at, lane + 1) && TavernMap.BoxFits(next, lane - 1) && TavernMap.BoxFits(next, lane + 1)
        : TavernMap.BoxFits(lane - 1, at) && TavernMap.BoxFits(lane + 1, at) && TavernMap.BoxFits(lane - 1, next) && TavernMap.BoxFits(lane + 1, next);

    static bool CanStep(TavernGuest v, int d) => TavernMap.BoxFits(v.X + DX[d] * Speed, v.Y + DY[d] * Speed);

    /// <summary>У клітинці цілі — до своєї точки: спершу по довшій осі, де не пролізти — по іншій; ніяк — стає тут.</summary>
    void Approach(TavernGuest v)
    {
        if (v.Sit) { Arrive(v); return; }
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
    /// Дійшов. На своїй приступці обертається до бочки (як гравець, що п'є) і часто п'є: від першого тика стояння до
    /// «хильнув» — 24 тики, як у гравця. На лаві — сідає обличчям до столу й сидить довше. Деінде обличчя не міняє.
    /// </summary>
    void Arrive(TavernGuest v)
    {
        var k = v.TargetPlace;
        var bench = v.TargetBench;
        ForgetPath(v);
        v.WanderNext = false;
        if (k >= 0 && PlaceAt(v) == k && !v.Sit)
        {
            v.Dir = TavernMap.Places[k].Face;
            if (Open && v.DrinkCool == 0 && _rng.Next(100) < BotDrinkPct)
            {
                v.Drink = DrinkTicks - 1;     // гравець: Act між тиками, і таймер тикає вже в першому тику стояння
                v.DrinkPlace = k;
                v.Stand = _rng.Next(0, 41);
                return;
            }
        }
        if (bench && !v.Sit && BenchAt(v) is var face and >= 0)
        {
            v.Sit = true;
            v.Dir = face;
            v.Stand = SitTicks();
            return;
        }
        v.Stand = StandTicks();
        v.WanderNext = !v.Sit && _rng.Next(100) < WanderChance;
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

    /// <summary>Скільки сидіти: 60 % — 2–6 с, 30 % — 6–15 с, 10 % — 15–30 с (корчма ж, не ярмарок).</summary>
    int SitTicks()
    {
        var r = _rng.Next(100);
        return r < 60 ? _rng.Next(50, 151) : r < 90 ? _rng.Next(151, 376) : _rng.Next(376, 751);
    }

    void PickTarget(TavernGuest v)
    {
        int cell;
        var r = _rng.Next(100);
        v.TargetPlace = -1;
        v.TargetBench = false;
        if (r < PlacePick)
        {
            var p = TavernMap.Places[_rng.Next(TavernMap.Places.Length)];
            cell = p.Cells[_rng.Next(p.Cells.Length)];
            v.TargetPlace = p.I;
        }
        else if (r < PlacePick + BenchPick)
        {
            cell = TavernMap.Benches[_rng.Next(TavernMap.Benches.Length)];
            v.TargetBench = true;
        }
        else cell = TavernMap.Walkable[_rng.Next(TavernMap.Walkable.Length)];
        v.Target = cell;
        (v.Tx, v.Ty) = Spot(cell);
    }

    /// <summary>
    /// Точка в клітинці, де стати: зсув до ±15 по обох осях, якщо коробка там влазить; три спроби, далі — до ±7
    /// (влазить завжди). Нею ж і ходять (смуга), і так само ставлять на старті раунду й ботів, і гравців.
    /// </summary>
    (int X, int Y) Spot(int cell)
    {
        int cx = TavernMap.CenterX(cell), cy = TavernMap.CenterY(cell);
        for (var i = 0; i < 3; i++)
        {
            int x = cx + _rng.Next(-SpotMax, SpotMax), y = cy + _rng.Next(-SpotMax, SpotMax);
            if (TavernMap.BoxFits(x, y)) return (x, y);
        }
        return (cx + _rng.Next(-TightMax, TightMax + 1), cy + _rng.Next(-TightMax, TightMax + 1));
    }

    /// <summary>Мозок — з чистого аркуша: ціль, стояння, тиняння. Образа (кому дати здачі) лишається.</summary>
    static void ForgetPath(TavernGuest v)
    {
        v.Target = -1;
        v.TargetPlace = -1;
        v.TargetBench = false;
        v.Want = -1;
        v.Stand = 0;
        v.Wander = 0;
        v.WanderNext = false;
        v.Blocked = false;
    }

    /// <summary>Бот устав після падіння чи став ботом після виходу гравця: думає з чистого аркуша, образ не тримає.</summary>
    public static void Forget(TavernGuest v)
    {
        ForgetPath(v);
        v.Grudge = -1;
        v.GrudgeWait = 0;
    }

    /// <summary>Приступка, на якій стоїть відвідувач (центр у її клітинці); -1 — ні на якій.</summary>
    public static int PlaceAt(TavernGuest v) =>
        v.X < 0 || v.Y < 0 || v.X >= TavernMap.WorldW || v.Y >= TavernMap.WorldH ? -1 : TavernMap.PlaceOf[TavernMap.CellOf(v.X, v.Y)];

    /// <summary>Куди дивитись, сівши тут на лаву (1 — стіл унизу, 3 — угорі); -1 — тут не лава.</summary>
    public static int BenchAt(TavernGuest v) =>
        v.X < 0 || v.Y < 0 || v.X >= TavernMap.WorldW || v.Y >= TavernMap.WorldH ? -1 : TavernMap.BenchFace[TavernMap.CellOf(v.X, v.Y)];

    public static long Dist2(TavernGuest a, int x, int y)
    {
        long dx = a.X - x, dy = a.Y - y;
        return dx * dx + dy * dy;
    }

    // ---------------------------------------------------------------------------------------------
    // Корчмар
    // ---------------------------------------------------------------------------------------------

    /// <summary>Новий раунд: корчмар посередині, спокійний, перший окрик — за 6–15 с від відчинення.</summary>
    public void ResetBarman()
    {
        var b = Barman;
        b.X = b.Tx = (TavernMap.BarmanMinX + TavernMap.BarmanMaxX) / 2;
        b.Y = TavernMap.BarmanY;
        b.Dir = 1;
        b.Mode = 0;
        b.Left = 0;
        b.Pause = 0;
        b.NextShout = _rng.Next(FirstShoutMin, FirstShoutMax + 1);
    }

    /// <summary>
    /// Тик корчмаря. Коли корчма відчинена, лічить до окрику; гримає 1 с, дивиться 4 с, далі знову порається. Поки
    /// порається — походжає за шинквасом. Повертає true того тика, коли гримнув.
    /// </summary>
    public bool BarmanTick()
    {
        var b = Barman;
        var shouted = false;
        switch (b.Mode)
        {
            case 0 when Open && --b.NextShout <= 0:
                b.Mode = 1;
                b.Left = ShoutTicks;
                b.Dir = 1;
                shouted = true;
                break;
            case 1 when --b.Left <= 0:
                b.Mode = 2;
                b.Left = WatchTicks;
                break;
            case 2 when --b.Left <= 0:
                b.Mode = 0;
                b.NextShout = _rng.Next(ShoutGapMin, ShoutGapMax + 1);
                break;
        }
        if (b.Mode != 0) return shouted;
        if (b.Pause > 0) b.Pause--;
        else if (Math.Abs(b.X - b.Tx) <= BarmanSpeed)
        {
            b.X = b.Tx;
            b.Dir = 1;
            b.Pause = _rng.Next(20, 101);
            b.Tx = _rng.Next(TavernMap.BarmanMinX, TavernMap.BarmanMaxX + 1);
        }
        else
        {
            b.Dir = b.Tx > b.X ? 0 : 2;
            b.X += b.Tx > b.X ? BarmanSpeed : -BarmanSpeed;
        }
        return shouted;
    }

    /// <summary>Корчмар почув бійку: інколи гримне зовсім скоро.</summary>
    public void Provoke()
    {
        var b = Barman;
        if (b.Mode != 0 || b.NextShout <= ProvokeMax) return;
        if (_rng.Next(100) < ProvokePct) b.NextShout = _rng.Next(ProvokeMin, ProvokeMax + 1);
    }

    /// <summary>Корчмар у кадрі: x, y, d, режим (0 порається, 1 гримає, 2 дивиться).</summary>
    public int[] PackBarman() => [Barman.X, Barman.Y, Barman.Dir, Barman.Mode];

    // ---------------------------------------------------------------------------------------------
    // Раунд
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Нова корчма: <paramref name="owners"/> — місця гравців (ідуть першими), далі <paramref name="bots"/> ботів.
    /// Порядок випадковості: розстановка (гравці — не ближче 3 клітинок один до одного; ніхто не на приступці й не
    /// надворі) → тасування id → вигляд → імена → п'яненькі, перше стояння й сидіння ботів → корчмар.
    /// </summary>
    public void Deal(IReadOnlyList<int> owners, int bots)
    {
        var n = owners.Count + bots;
        var list = new TavernGuest[n];
        var walk = TavernMap.Walkable;
        for (var i = 0; i < n; i++)
        {
            var v = new TavernGuest { Owner = i < owners.Count ? owners[i] : -1 };
            int cell = 0;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                cell = walk[_rng.Next(walk.Length)];
                if (TavernMap.PlaceOf[cell] >= 0 || cell / TavernMap.W >= 16) continue;
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
        {
            var v = list[i];
            if (v.Owner >= 0) continue;
            v.Drunk = _rng.Next(100) < DrunkPct;
            if (BenchAt(v) is var face and >= 0 && _rng.Next(100) < OpeningSitPct)
            {
                v.Sit = true;
                v.Dir = face;
                v.Stand = SitTicks();
            }
            else v.Stand = _rng.Next(0, OpeningStandMax + 1);
        }
        V = list;
        ResetBarman();
    }

    /// <summary>Гравця не ставимо ближче 3 клітинок (Чебишев) до вже поставлених гравців.</summary>
    static bool TooClose(TavernGuest[] placed, int count, int cell)
    {
        int cx = cell % TavernMap.W, cy = cell / TavernMap.W;
        for (var i = 0; i < count; i++)
        {
            var p = placed[i];
            if (p.Owner < 0) continue;
            int px = p.X / TavernMap.Cell, py = p.Y / TavernMap.Cell;
            if (Math.Max(Math.Abs(px - cx), Math.Abs(py - cy)) < 3) return true;
        }
        return false;
    }

    /// <summary>Кадр: 4 числа на відвідувача за id — x, y, d, s. Новий масив щоразу: розсилка серіалізує його поза замком.</summary>
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
        for (var i = 0; i < v.Length; i++) a[i] = TavernMap.Names[v[i].Name];
        return a;
    }
}
