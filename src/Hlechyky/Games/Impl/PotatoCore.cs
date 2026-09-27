namespace Hlechyky.Games.Impl;

/// <summary>
/// Один селянин толоки. Бот і гравець — той самий клас і той самий <see cref="PotatoCore.Step"/>, ті самі
/// <see cref="PotatoCore.TryPass"/> і <see cref="PotatoCore.TrySlap"/>: гравцеві <see cref="Want"/> і дії пише ввід,
/// ботові — <see cref="PotatoCore.Think"/>. Решта полів мозку в гравця просто лежать.
/// </summary>
public sealed class PotatoVillager
{
    /// <summary>Номер у кадрі; за раунд не міняється, між раундами тасується.</summary>
    public int Id;
    /// <summary>Точка на землі в одиницях світу (клітинка — 32).</summary>
    public int X, Y;
    /// <summary>Куди дивиться: 0 праворуч, 1 вниз, 2 ліворуч, 3 вгору.</summary>
    public int Dir;
    /// <summary>Куди хоче йти; -1 — стоїть.</summary>
    public int Want = -1;
    public bool Moving;
    /// <summary>Хотів іти, але коробка вперлась у перешкоду.</summary>
    public bool Blocked;
    /// <summary>Місце гравця; -1 — бот.</summary>
    public int Owner = -1;
    /// <summary>Гравець, у якого вибухнув горщик: лежить до кінця раунду.</summary>
    public bool Dead;
    /// <summary>Бот, у якого вибухнув горщик, лежить ще стільки тиків.</summary>
    public int Fallen;
    /// <summary>Оглушений (гравець після ляпаса) чи «отетерілий» (дав ляпаса ботові) — ще стільки тиків, не рухається й нічого не робить.</summary>
    public int Stun;
    /// <summary>Рука після ляпаса ще відходить стільки тиків.</summary>
    public int SlapCool;
    /// <summary>Номер горщика в руках; -1 — нема.</summary>
    public int Pot = -1;

    // ---- мозок бота ----
    public int Target = -1;
    public int Tx, Ty;
    public int Stand, Wander;
    public bool WanderNext;
    /// <summary>Не боїться горщика: не відходить, коли той поруч (частина юрми — щоб «хто відходить» не було вироком).</summary>
    public bool Brave;
    /// <summary>Відходить від горщика ще стільки тиків — від носія <see cref="ShyFrom"/>.</summary>
    public int Shy, ShyFrom = -1;
    /// <summary>Що робить бот із горщиком у руках: 0 — нема горщика, 1 шукає кому віддати, 2 панікує й біжить, 3 тримає.</summary>
    public int Mode;
    public int ModeLeft, Turn;
    /// <summary>Кого наздоганяє з горщиком; коли переобрати.</summary>
    public int Chase = -1, ChaseAt;
    /// <summary>Уже поруч із кимось — віддасть за стільки тиків; -1 — ще не вирішив.</summary>
    public int PassWait = -1;

    // ---- вигляд на раунд ----
    public int Hat, HatColor, Shirt, Skin, Name;

    /// <summary>Стан у кадрі: 0 стоїть, 1 іде, 2 лежить бот (вибух), 3 вибулий гравець, 4 оглушений / отетерів.</summary>
    public int State => Dead ? 3 : Fallen > 0 ? 2 : Stun > 0 ? 4 : Moving ? 1 : 0;

    /// <summary>На ногах і при тямі — такого можна ляснути.</summary>
    public bool Upright => !Dead && Fallen == 0 && Stun == 0;

    /// <summary>На ногах (хай і оглушений) — такому можна тицьнути горщик.</summary>
    public bool Standing => !Dead && Fallen == 0;
}

/// <summary>Горщик із жаром: у кого він, скільки ще до вибуху (таємниця) і пороги диму (свої на кожен горщик).</summary>
public sealed class PotatoPot
{
    public int Carrier = -1;
    /// <summary>Тиків до вибуху — таємниця; видно лише, як горщик димить (<see cref="PotatoCore.Heat"/>).</summary>
    public int Fuse;
    /// <summary>Пороги диму в тиках до вибуху: менше за H1 — дим, за H2 — іскри, за H3 — трусить.</summary>
    public int H1, H2, H3;
    /// <summary>Тик ядра, коли горщик опинився в теперішнього носія.</summary>
    public int Since;
    /// <summary>Від кого прийшов (id) — йому назад одразу не можна; і чиє це місце (-1 — бот): кому «+2», якщо згорить гравець.</summary>
    public int Giver = -1, GiverSeat = -1;
    /// <summary>Після вибуху — за скільки тиків з'явиться новий.</summary>
    public int Respawn;
}

/// <summary>Що сталося в ядрі — гра рахує з цього очки й кладе в кадр.</summary>
public readonly record struct PotatoEvent(int Kind, int Pot, int A, int B, int C)
{
    public const int Pass = 1, Slap = 2, Boom = 3, Spawn = 4;
}

/// <summary>Чому ядро відмовило в дії.</summary>
public enum PotatoNo { None, NoPot, Stunned, TooSoon, Nobody, Far, Self, Down, HasPot, Back, NoSuch, Cool }

/// <summary>
/// Толока без кімнат і очок: селяни, горщики, один спільний крок, одні правила передачі й ляпаса, мозок ботів.
/// Усе ціле, уся випадковість — з переданого <see cref="Random"/> у строгому порядку, тож той самий сід і той самий
/// ввід дають побайтно ті самі кадри.
/// </summary>
public sealed class PotatoCore(Random rng)
{
    /// <summary>Крок за тик — однаковий для всіх: 75 од/с. Бігу нема ні в кого: і «паніка» бота — та сама хода.</summary>
    public const int Speed = 3;
    /// <summary>Половина коробки селянина 16×16 (для перешкод; один крізь одного селяни проходять).</summary>
    public const int Half = 8;
    /// <summary>«Впритул»: горщик передають найближчому на стільки одиниць (центр до центру).</summary>
    public const int PassRange = 36;
    /// <summary>Явна ціль передачі (клік/тап по селянину): клієнт бачить кадр на ~100 мс старший.</summary>
    public const int PassRangeMax = 48;
    /// <summary>Ляпас — найближчому в конусі ±45° перед собою на стільки одиниць.</summary>
    public const int SlapRange = 40;
    public const int SlapRangeMax = 52;
    /// <summary>cos²45° = 0,5 — у тисячних, щоб рахувати конус на цілих.</summary>
    public const int SlapCos2Milli = 500;
    /// <summary>Горщик пече: щойно отриманий передати можна не раніше, ніж за пів секунди. Інакше горщик скаче юрмою за тик.</summary>
    public const int HoldMin = 12;
    /// <summary>Назад тому, хто щойно дав, — не раніше, ніж за 2 с: без пінг-понгу вдвох.</summary>
    public const int NoBackTicks = 50;
    /// <summary>Ґніт горщика: 12–30 с, таємниця.</summary>
    public const int FuseMin = 300, FuseMax = 750;
    /// <summary>Пороги диму (у тиках до вибуху) — свої на кожен горщик, щоб дим підказував, але не рахував секунди.</summary>
    public const int H1Min = 200, H1Max = 300, H2Min = 100, H2Max = 150, H3Min = 38, H3Max = 63;
    /// <summary>Після вибуху новий горщик з'являється за 2 с — поки дим розвіється.</summary>
    public const int RespawnTicks = 50;
    /// <summary>Оглушений після ляпаса (чи отетерілий, ляснувши бота) — 3 с.</summary>
    public const int StunTicks = 75;
    /// <summary>Між ляпасами — 4 с.</summary>
    public const int SlapCoolTicks = 100;
    /// <summary>Бот, у якого рвонув горщик, лежить 3 с і встає закопчений.</summary>
    public const int FallTicks = 75;

    // ---- мозок бота (ходу й стояння — як у «Юрми», перевірені тестами на збіг із людьми) ----
    public const int StandMin = 12, StandMax = 75, LongStandMax = 500;
    public const int PauseMilli = 9, PauseMax = 20;
    public const int FidgetMilli = 8;
    public const int WanderChance = 15, WanderMin = 8, WanderMax = 24;
    /// <summary>Скільки відсотків цілей — «до людей» (клітинка випадкового селянина): юрма тримається купками, і горщику є кому переходити.</summary>
    public const int SocialPick = 35;
    public const int SpotMax = 15, TightMax = 7;
    public const int OpeningStandMax = 150;
    /// <summary>Відсоток ботів, що горщика не бояться взагалі.</summary>
    public const int BravePct = 35;
    /// <summary>Горщик ближче за стільки — боязкий бот щотика з імовірністю <see cref="ShyMilli"/>‰ (удвічі, коли трусить) відходить на 8–30 тиків.</summary>
    public const int ShyRange = 80, ShyMilli = 30, ShyMin = 8, ShyMax = 30;
    /// <summary>Бот, що отримав горщик: 60 % одразу шукає, кому віддати, 20 % панікує й біжить, 20 % тримає до пори.</summary>
    public const int SeekPct = 60, PanicPct = 20;
    public const int PanicMin = 20, PanicMax = 60, HoldOnMin = 40, HoldOnMax = 200;
    /// <summary>Отримав горщик — спершу «ой, гаряче!» стоїть 3–20 тиків: не одразу, як і людина.</summary>
    public const int CatchStandMin = 3, CatchStandMax = 20;
    /// <summary>Поруч є кому віддати — ще 0–8 тиків вагається.</summary>
    public const int PassWaitMax = 8;
    /// <summary>Ляпаси ботів: стільки на 10 000 тиків, коли перед ним хтось є, і всемеро частіше, коли це носій горщика (пре на нього).</summary>
    public const int BotSlapPer10k = 8, BotSlapCarrierPer10k = 60;

    public static readonly int[] DX = [1, 0, -1, 0];
    public static readonly int[] DY = [0, 1, 0, -1];

    readonly Random _rng = rng;
    readonly int[] _nameOrder = new int[PotatoMap.Names.Length];
    int[] _pick = [];

    public PotatoVillager[] V { get; private set; } = [];
    public int N => V.Length;
    public PotatoPot[] Pots { get; private set; } = [];
    /// <summary>Тик ядра — годинник для «щойно отримав» і «щойно віддав».</summary>
    public int Now { get; private set; }
    /// <summary>Гра йде (фаза «go»): горщики горять, боти ляскають.</summary>
    public bool Live { get; set; }
    /// <summary>Що сталося з останнього зчитування — гра забирає й чистить.</summary>
    public List<PotatoEvent> Log { get; } = [];

    // ---------------------------------------------------------------------------------------------
    // Крок — один на всіх
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Крок селянина. Хто лежить, вибув, оглушений чи нікуди не хоче — стоїть. Інакше повертається в бік
    /// <see cref="PotatoVillager.Want"/> (навіть коли далі перешкода) і робить рівно <see cref="Speed"/> одиниць, якщо
    /// коробка там уміщається. Ця функція одна для ботів і гравців.
    /// </summary>
    public static void Step(PotatoVillager v)
    {
        if (v.Stun > 0 || v.Fallen > 0 || v.Dead || v.Want < 0)
        {
            v.Moving = false;
            v.Blocked = false;
            return;
        }
        v.Dir = v.Want;
        int nx = v.X + DX[v.Dir] * Speed, ny = v.Y + DY[v.Dir] * Speed;
        if (PotatoMap.BoxFits(nx, ny))
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

    /// <summary>
    /// Годинники: тик ядра, оглушення, лежання, рука після ляпаса; у грі — ще й ґноти горщиків (вибух) і новий горщик
    /// після вибуху.
    /// </summary>
    public void TimersAll()
    {
        Now++;
        var v = V;
        for (var i = 0; i < v.Length; i++)
        {
            var q = v[i];
            if (q.Stun > 0 && --q.Stun == 0 && q.Owner < 0) ForgetWalk(q);
            if (q.Fallen > 0 && --q.Fallen == 0) Forget(q);
            if (q.SlapCool > 0) q.SlapCool--;
        }
        if (!Live) return;
        for (var k = 0; k < Pots.Length; k++)
        {
            var p = Pots[k];
            if (p.Carrier >= 0)
            {
                if (--p.Fuse <= 0) Explode(k);
            }
            else if (p.Respawn > 0 && --p.Respawn == 0) Spawn(k);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Горщики
    // ---------------------------------------------------------------------------------------------

    /// <summary>Роздати <paramref name="count"/> горщиків випадковим селянам — на початку «go».</summary>
    public void StartPots(int count)
    {
        Pots = [.. Enumerable.Range(0, count).Select(_ => new PotatoPot())];
        for (var k = 0; k < count; k++) Spawn(k);
    }

    /// <summary>Кінець раунду: горщики зникають.</summary>
    public void ClearPots()
    {
        foreach (var v in V) v.Pot = -1;
        Pots = [];
    }

    /// <summary>Новий горщик — випадковому селянинові на ногах без горщика (хоч боту, хоч гравцю; однаково).</summary>
    void Spawn(int k)
    {
        var v = V;
        if (_pick.Length < v.Length) _pick = new int[v.Length];
        var m = 0;
        for (var i = 0; i < v.Length; i++)
            if (v[i].Standing && v[i].Pot < 0) _pick[m++] = i;
        var p = Pots[k];
        if (m == 0) { p.Respawn = RespawnTicks; return; }
        var who = v[_pick[_rng.Next(m)]];
        p.Carrier = who.Id;
        p.Fuse = _rng.Next(FuseMin, FuseMax + 1);
        p.H1 = _rng.Next(H1Min, H1Max + 1);
        p.H2 = _rng.Next(H2Min, H2Max + 1);
        p.H3 = _rng.Next(H3Min, H3Max + 1);
        p.Since = Now;
        p.Giver = p.GiverSeat = -1;
        p.Respawn = 0;
        who.Pot = k;
        if (who.Owner < 0) Caught(who);
        Log.Add(new(PotatoEvent.Spawn, k, who.Id, 0, 0));
    }

    /// <summary>Горщик рвонув: бот падає на 3 с, гравець вибуває на раунд. У подію — хто підкинув (місце, -1 — бот).</summary>
    void Explode(int k)
    {
        var p = Pots[k];
        var v = V[p.Carrier];
        v.Pot = -1;
        p.Carrier = -1;
        p.Respawn = RespawnTicks;
        v.Stun = 0;
        v.Moving = false;
        if (v.Owner >= 0)
        {
            v.Dead = true;
            v.Want = -1;
        }
        else
        {
            v.Fallen = FallTicks;
            Forget(v);
        }
        Log.Add(new(PotatoEvent.Boom, k, v.Id, p.GiverSeat, 0));
    }

    /// <summary>Як сильно димить горщик: 0 ледь курить, 1 дим, 2 іскри, 3 трусить. Однаково для всіх — таймера не видно.</summary>
    public int Heat(int k)
    {
        if (k < 0 || k >= Pots.Length) return 0;
        var p = Pots[k];
        if (p.Carrier < 0) return 0;
        return p.Fuse <= p.H3 ? 3 : p.Fuse <= p.H2 ? 2 : p.Fuse <= p.H1 ? 1 : 0;
    }

    /// <summary>Чи можна тицьнути горщик цьому селянинові від <paramref name="g"/>.</summary>
    bool Takes(PotatoVillager g, PotatoVillager q, PotatoPot p) =>
        q != g && q.Standing && q.Pot < 0 && !(q.Id == p.Giver && Now - p.Since < NoBackTicks);

    /// <summary>Найближчий «впритул» (≤ <see cref="PassRange"/>), кому можна передати; при рівній відстані — менший id. -1 — нікого.</summary>
    public int PassTarget(PotatoVillager g)
    {
        if (g.Pot < 0) return -1;
        var p = Pots[g.Pot];
        int best = -1;
        long bestD = (long)PassRange * PassRange + 1;
        var v = V;
        for (var i = 0; i < v.Length; i++)
        {
            var q = v[i];
            if (!Takes(g, q, p)) continue;
            var d2 = Dist2(q, g.X, g.Y);
            if (d2 < bestD) { bestD = d2; best = i; }
        }
        return best;
    }

    /// <summary>
    /// Передати горщик: найближчому впритул або явному <paramref name="id"/> (до <see cref="PassRangeMax"/>). Та сама
    /// функція для гравця (з Act) і бота (з Think).
    /// </summary>
    public PotatoNo TryPass(PotatoVillager g, int? id)
    {
        if (g.Pot < 0) return PotatoNo.NoPot;
        if (g.Stun > 0) return PotatoNo.Stunned;
        var p = Pots[g.Pot];
        if (Now - p.Since < HoldMin) return PotatoNo.TooSoon;
        int t;
        if (id is { } want)
        {
            if (want < 0 || want >= N) return PotatoNo.NoSuch;
            if (want == g.Id) return PotatoNo.Self;
            var q = V[want];
            if (!q.Standing) return PotatoNo.Down;
            if (q.Pot >= 0) return PotatoNo.HasPot;
            if (q.Id == p.Giver && Now - p.Since < NoBackTicks) return PotatoNo.Back;
            if (Dist2(q, g.X, g.Y) > (long)PassRangeMax * PassRangeMax) return PotatoNo.Far;
            t = want;
        }
        else
        {
            t = PassTarget(g);
            if (t < 0) return PotatoNo.Nobody;
        }
        var to = V[t];
        var k = g.Pot;
        g.Pot = -1;
        to.Pot = k;
        p.Carrier = to.Id;
        p.Since = Now;
        p.Giver = g.Id;
        p.GiverSeat = g.Owner;
        if (g.Owner < 0) Passed(g, to);
        if (to.Owner < 0) Caught(to);
        Log.Add(new(PotatoEvent.Pass, k, g.Id, to.Id, 0));
        return PotatoNo.None;
    }

    // ---------------------------------------------------------------------------------------------
    // Ляпас
    // ---------------------------------------------------------------------------------------------

    /// <summary>Найближчий на ногах у конусі ±45° на <see cref="SlapRange"/> перед <paramref name="a"/>; -1 — нікого.</summary>
    public int SlapTarget(PotatoVillager a)
    {
        int best = -1;
        long bestD = long.MaxValue;
        int fx = DX[a.Dir], fy = DY[a.Dir];
        var v = V;
        for (var i = 0; i < v.Length; i++)
        {
            var q = v[i];
            if (q == a || !q.Upright) continue;
            long dx = q.X - a.X, dy = q.Y - a.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 > (long)SlapRange * SlapRange) continue;
            var dot = dx * fx + dy * fy;
            if (dot <= 0 || dot * dot * 1000 < SlapCos2Milli * d2) continue;
            if (d2 < bestD) { bestD = d2; best = i; }
        }
        return best;
    }

    /// <summary>
    /// Ляпас: влучив у гравця — той оглушений на 3 с (не тікає, не передає); у бота — сам отетерів на 3 с. Та сама
    /// функція для гравця й бота; хто ляснув — видно всім, а чи то був гравець — лише з того, хто закліпав.
    /// </summary>
    public PotatoNo TrySlap(PotatoVillager a, int? id)
    {
        if (a.Stun > 0) return PotatoNo.Stunned;
        if (a.SlapCool > 0) return PotatoNo.Cool;
        int t;
        if (id is { } want)
        {
            if (want < 0 || want >= N) return PotatoNo.NoSuch;
            if (want == a.Id) return PotatoNo.Self;
            var q = V[want];
            if (!q.Upright) return PotatoNo.Down;
            if (Dist2(q, a.X, a.Y) > (long)SlapRangeMax * SlapRangeMax) return PotatoNo.Far;
            t = want;
        }
        else
        {
            t = SlapTarget(a);
            if (t < 0) return PotatoNo.Nobody;
        }
        var b = V[t];
        // Замахнувся — обернувся до того, кого б'є (для явної цілі за спиною теж): так видно, хто кого.
        int dx = b.X - a.X, dy = b.Y - a.Y;
        if (dx != 0 || dy != 0) a.Dir = Math.Abs(dx) >= Math.Abs(dy) ? (dx > 0 ? 0 : 2) : (dy > 0 ? 1 : 3);
        a.SlapCool = SlapCoolTicks;
        PotatoVillager dazed;
        if (b.Owner >= 0)
        {
            dazed = b;
            b.Stun = StunTicks;
            b.Moving = false;
        }
        else
        {
            dazed = a;
            a.Stun = StunTicks;
            a.Moving = false;
            if (a.Owner < 0) ForgetWalk(a);
        }
        Log.Add(new(PotatoEvent.Slap, -1, a.Id, b.Id, dazed.Id));
        return PotatoNo.None;
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
    /// Бот: з горщиком — шукає, кому віддати (чи панікує, чи тримає до пори); без — гуляє, як на «Юрмі», зрідка
    /// ляскає того, хто перед ним (частіше — носія, що пре на нього), а боязкий відходить від горщика, що підійшов.
    /// Так гравець, що тікає від горщика чи кидається його віддати, не виділяється з юрми.
    /// </summary>
    public void Think(PotatoVillager v)
    {
        if (v.Dead || v.Fallen > 0) return;
        if (v.Stun > 0) { v.Want = -1; return; }
        if (v.Pot >= 0) { Carry(v); return; }
        if (Live)
        {
            if (v.SlapCool == 0)
            {
                var t = SlapTarget(v);
                if (t >= 0 && _rng.Next(10_000) < (V[t].Pot >= 0 ? BotSlapCarrierPer10k : BotSlapPer10k))
                {
                    TrySlap(v, null);
                    return;
                }
            }
            if (v.Shy > 0)
            {
                if (--v.Shy == 0 || v.ShyFrom < 0 || V[v.ShyFrom].Pot < 0) { ForgetWalk(v); return; }
                Away(v, V[v.ShyFrom]);
                return;
            }
            if (!v.Brave)
            {
                var c = NearCarrier(v, out var heat);
                if (c >= 0 && _rng.Next(1000) < ShyMilli * (heat >= 3 ? 2 : 1))
                {
                    v.Shy = _rng.Next(ShyMin, ShyMax + 1);
                    v.ShyFrom = c;
                    v.Stand = v.Wander = 0;
                    Away(v, V[c]);
                    return;
                }
            }
        }
        Walk(v);
    }

    /// <summary>Найближчий носій горщика в межах <see cref="ShyRange"/> (id) і як той горщик димить.</summary>
    int NearCarrier(PotatoVillager v, out int heat)
    {
        heat = 0;
        int best = -1;
        long bestD = (long)ShyRange * ShyRange + 1;
        for (var k = 0; k < Pots.Length; k++)
        {
            var c = Pots[k].Carrier;
            if (c < 0 || c == v.Id) continue;
            var d2 = Dist2(V[c], v.X, v.Y);
            if (d2 < bestD) { bestD = d2; best = c; heat = Heat(k); }
        }
        return best;
    }

    /// <summary>Крок геть від <paramref name="from"/>: по довшій осі, а де не пролізти — по іншій.</summary>
    static void Away(PotatoVillager v, PotatoVillager from)
    {
        int dx = v.X - from.X, dy = v.Y - from.Y;
        int hx = dx >= 0 ? 0 : 2, hy = dy >= 0 ? 1 : 3;
        int first = Math.Abs(dx) >= Math.Abs(dy) ? hx : hy, second = first == hx ? hy : hx;
        v.Want = CanStep(v, first) ? first : CanStep(v, second) ? second : -1;
    }

    /// <summary>Бот із горщиком. Спершу «ой, гаряче» стоїть мить, далі — за своїм норовом; коли трусить — віддає будь-що.</summary>
    void Carry(PotatoVillager v)
    {
        var heat = Heat(v.Pot);
        if (v.Stand > 0)
        {
            v.Stand--;
            v.Want = -1;
            return;
        }
        if (heat >= 3 && v.Mode != 1) v.Mode = 1;
        if (v.Mode == 3)
        {
            if (--v.ModeLeft <= 0 || heat >= 2) v.Mode = 1;
            else { Walk(v); return; }
        }
        if (v.Mode == 2)
        {
            if (--v.ModeLeft <= 0) v.Mode = 1;
            else
            {
                if (v.Blocked || --v.Turn <= 0 || v.Want < 0)
                {
                    v.Want = _rng.Next(4);
                    v.Turn = _rng.Next(8, 21);
                }
                return;
            }
        }
        // шукає, кому віддати
        var p = Pots[v.Pot];
        if (Now - p.Since >= HoldMin && PassTarget(v) >= 0)
        {
            if (v.PassWait < 0) v.PassWait = _rng.Next(0, PassWaitMax + 1);
            if (v.PassWait-- == 0)
            {
                TryPass(v, null);
                return;
            }
        }
        if (v.Chase < 0 || --v.ChaseAt <= 0 || !Takes(v, V[v.Chase], p))
        {
            v.Chase = Nearest(v, p);
            v.ChaseAt = _rng.Next(10, 26);
        }
        if (v.Chase < 0) { Walk(v); return; }
        Toward(v, V[v.Chase].X, V[v.Chase].Y);
    }

    /// <summary>Найближчий, кому можна віддати (хоч би як далеко); -1 — нікого.</summary>
    int Nearest(PotatoVillager g, PotatoPot p)
    {
        int best = -1;
        long bestD = long.MaxValue;
        var v = V;
        for (var i = 0; i < v.Length; i++)
        {
            if (!Takes(g, v[i], p)) continue;
            var d2 = Dist2(v[i], g.X, g.Y);
            if (d2 < bestD) { bestD = d2; best = i; }
        }
        return best;
    }

    /// <summary>До точки (x, y): поруч — навпростець по довшій осі, далеко — першим кроком BFS, тримаючи свою смугу.</summary>
    void Toward(PotatoVillager v, int x, int y)
    {
        int dx = x - v.X, dy = y - v.Y;
        var cell = PotatoMap.CellOf(v.X, v.Y);
        var target = PotatoMap.CellOf(x, y);
        if (cell == target || Math.Abs(dx) + Math.Abs(dy) <= 40)
        {
            int hx = dx > 2 ? 0 : dx < -2 ? 2 : -1, hy = dy > 2 ? 1 : dy < -2 ? 3 : -1;
            int first = Math.Abs(dx) > Math.Abs(dy) ? hx : hy, second = first == hx ? hy : hx;
            v.Want = first >= 0 && CanStep(v, first) ? first : second >= 0 && CanStep(v, second) ? second : -1;
            return;
        }
        var hop = PotatoMap.NextHop[target * PotatoMap.Cells + cell];
        if (hop == PotatoMap.NoHop) { v.Want = -1; return; }
        Lane(v, cell, hop, hop is 0 or 2 ? v.Y - PotatoMap.CenterY(cell) : v.X - PotatoMap.CenterX(cell));
    }

    /// <summary>Бот устав після вибуху чи став ботом після виходу гравця: думає з чистого аркуша.</summary>
    public static void Forget(PotatoVillager v)
    {
        ForgetWalk(v);
        v.Want = -1;
        v.Stand = 0;
        v.Mode = 0;
        v.Chase = -1;
        v.PassWait = -1;
    }

    /// <summary>Скинути прогулянку й «відходжу від горщика»: наступного разу — нова ціль.</summary>
    static void ForgetWalk(PotatoVillager v)
    {
        v.Target = -1;
        v.Wander = 0;
        v.WanderNext = false;
        v.Blocked = false;
        v.Shy = 0;
        v.ShyFrom = -1;
    }

    /// <summary>Гравець устав посеред раунду — його селянин стає ботом (і з горщиком теж: шукатиме, кому віддати).</summary>
    public void BecomeBot(PotatoVillager v)
    {
        v.Owner = -1;
        Forget(v);
        if (v.Pot >= 0) v.Mode = 1;
    }

    /// <summary>Бот отримав горщик: мить стоїть, далі — норов (шукає / панікує / тримає).</summary>
    void Caught(PotatoVillager v)
    {
        ForgetWalk(v);
        v.Stand = _rng.Next(CatchStandMin, CatchStandMax + 1);
        v.Chase = -1;
        v.PassWait = -1;
        var r = _rng.Next(100);
        if (r < SeekPct) v.Mode = 1;
        else if (r < SeekPct + PanicPct)
        {
            v.Mode = 2;
            v.ModeLeft = _rng.Next(PanicMin, PanicMax + 1);
            v.Turn = 0;
        }
        else
        {
            v.Mode = 3;
            v.ModeLeft = _rng.Next(HoldOnMin, HoldOnMax + 1);
        }
    }

    /// <summary>Бот віддав горщик: норов скинуто; щойно віддав — часто ще й відходить від нового носія, як людина.</summary>
    void Passed(PotatoVillager g, PotatoVillager to)
    {
        g.Mode = 0;
        g.Chase = -1;
        g.PassWait = -1;
        g.Stand = 0;
        ForgetWalk(g);
        if (_rng.Next(2) == 0)
        {
            g.Shy = _rng.Next(ShyMin, ShyMax + 1);
            g.ShyFrom = to.Id;
        }
    }

    /// <summary>
    /// Прогулянка, як у «Юрмі»: до цілі своєю смугою, посеред дороги інколи вагається, дійшовши — стоїть будь-де в
    /// клітинці (звичайно 0,5–3 с, кожен четвертий — довше), стоячи інколи переступає, після стояння інколи тиняється.
    /// </summary>
    void Walk(PotatoVillager v)
    {
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
        var cell = PotatoMap.CellOf(v.X, v.Y);
        if (cell == v.Target)
        {
            Approach(v);
            return;
        }
        var hop = PotatoMap.NextHop[v.Target * PotatoMap.Cells + cell];
        if (hop == PotatoMap.NoHop)
        {
            v.Target = -1;
            v.Want = -1;
            return;
        }
        Lane(v, cell, hop, hop is 0 or 2 ? v.Ty - PotatoMap.CenterY(v.Target) : v.Tx - PotatoMap.CenterX(v.Target));
    }

    /// <summary>
    /// Іти в бік <paramref name="hop"/> смугою — зсувом <paramref name="off"/> від центру клітинки поперек руху; де з
    /// таким зсувом коробка не пролізе (тут або в наступній клітинці) — ближче до центру, ±7 пролазить завжди.
    /// </summary>
    static void Lane(PotatoVillager v, int cell, int hop, int off)
    {
        if (hop is 0 or 2)
        {
            int mid = PotatoMap.CenterY(cell), next = PotatoMap.CenterX(cell) + DX[hop] * PotatoMap.Cell;
            var lane = mid + off;
            if (!Fits4(v.X, next, lane, true)) lane = mid + Math.Clamp(off, -TightMax, TightMax);
            if (v.Y < lane - 1) { v.Want = 1; return; }
            if (v.Y > lane + 1) { v.Want = 3; return; }
        }
        else
        {
            int mid = PotatoMap.CenterX(cell), next = PotatoMap.CenterY(cell) + DY[hop] * PotatoMap.Cell;
            var lane = mid + off;
            if (!Fits4(v.Y, next, lane, false)) lane = mid + Math.Clamp(off, -TightMax, TightMax);
            if (v.X < lane - 1) { v.Want = 0; return; }
            if (v.X > lane + 1) { v.Want = 2; return; }
        }
        v.Want = hop;
    }

    static bool Fits4(int at, int next, int lane, bool horizontal) => horizontal
        ? PotatoMap.BoxFits(at, lane - 1) && PotatoMap.BoxFits(at, lane + 1) && PotatoMap.BoxFits(next, lane - 1) && PotatoMap.BoxFits(next, lane + 1)
        : PotatoMap.BoxFits(lane - 1, at) && PotatoMap.BoxFits(lane + 1, at) && PotatoMap.BoxFits(lane - 1, next) && PotatoMap.BoxFits(lane + 1, next);

    static bool CanStep(PotatoVillager v, int d) => PotatoMap.BoxFits(v.X + DX[d] * Speed, v.Y + DY[d] * Speed);

    void Approach(PotatoVillager v)
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

    void Arrive(PotatoVillager v)
    {
        v.Target = -1;
        v.Want = -1;
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

    /// <summary>Ціль: 35 % — туди, де хтось стоїть (юрма збивається в купки), решта — будь-яка прохідна клітинка.</summary>
    void PickTarget(PotatoVillager v)
    {
        int cell;
        if (_rng.Next(100) < SocialPick && V.Length > 1)
        {
            var q = V[_rng.Next(V.Length)];
            cell = PotatoMap.CellOf(q.X, q.Y);
            if (!PotatoMap.Pass[cell]) cell = PotatoMap.Walkable[_rng.Next(PotatoMap.Walkable.Length)];
        }
        else cell = PotatoMap.Walkable[_rng.Next(PotatoMap.Walkable.Length)];
        v.Target = cell;
        (v.Tx, v.Ty) = Spot(cell);
    }

    /// <summary>Точка в клітинці: зсув до ±15 по обох осях, якщо коробка влазить (три спроби), далі — до ±7.</summary>
    (int X, int Y) Spot(int cell)
    {
        int cx = PotatoMap.CenterX(cell), cy = PotatoMap.CenterY(cell);
        for (var i = 0; i < 3; i++)
        {
            int x = cx + _rng.Next(-SpotMax, SpotMax), y = cy + _rng.Next(-SpotMax, SpotMax);
            if (PotatoMap.BoxFits(x, y)) return (x, y);
        }
        return (cx + _rng.Next(-TightMax, TightMax + 1), cy + _rng.Next(-TightMax, TightMax + 1));
    }

    public static long Dist2(PotatoVillager a, int x, int y)
    {
        long dx = a.X - x, dy = a.Y - y;
        return dx * dx + dy * dy;
    }

    // ---------------------------------------------------------------------------------------------
    // Раунд
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Нова толока: <paramref name="owners"/> — місця гравців (ідуть першими), далі <paramref name="bots"/> ботів. Порядок
    /// випадковості: розстановка (гравці — не ближче 3 клітинок один до одного) → тасування id → вигляд → імена →
    /// норов і перше стояння ботів. Горщиків ще нема — вони з'являються на початку «go».
    /// </summary>
    public void Deal(IReadOnlyList<int> owners, int bots)
    {
        var n = owners.Count + bots;
        var list = new PotatoVillager[n];
        var walk = PotatoMap.Walkable;
        for (var i = 0; i < n; i++)
        {
            var v = new PotatoVillager { Owner = i < owners.Count ? owners[i] : -1 };
            int cell = 0;
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
            if (list[i].Owner < 0)
            {
                list[i].Brave = _rng.Next(100) < BravePct;
                list[i].Stand = _rng.Next(0, OpeningStandMax + 1);
            }
        V = list;
        Pots = [];
        Live = false;
        Log.Clear();
    }

    static bool TooClose(PotatoVillager[] placed, int count, int cell)
    {
        int cx = cell % PotatoMap.W, cy = cell / PotatoMap.W;
        for (var i = 0; i < count; i++)
        {
            var p = placed[i];
            if (p.Owner < 0) continue;
            int px = p.X / PotatoMap.Cell, py = p.Y / PotatoMap.Cell;
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

    /// <summary>Горщики в кадрі: пара «у кого (-1 — ніде), як димить» на кожен.</summary>
    public int[] PackPots()
    {
        var a = new int[Pots.Length * 2];
        for (var k = 0; k < Pots.Length; k++)
        {
            a[k * 2] = Pots[k].Carrier;
            a[k * 2 + 1] = Heat(k);
        }
        return a;
    }

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
        for (var i = 0; i < v.Length; i++) a[i] = PotatoMap.Names[v[i].Name];
        return a;
    }
}
