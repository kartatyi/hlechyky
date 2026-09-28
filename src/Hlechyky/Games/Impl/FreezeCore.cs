namespace Hlechyky.Games.Impl;

/// <summary>
/// Один селянин на лузі. Бот і гравець — той самий клас і той самий <see cref="FreezeCore.Step"/>: гравцеві
/// <see cref="Want"/> пише ввід, ботові — <see cref="FreezeCore.Think"/>. Поля мозку й вдачі є в кожного (і в гравця),
/// бо той, хто встав з-за столу, лишається на лузі ботом.
/// </summary>
public sealed class FreezeVillager
{
    /// <summary>Номер у кадрі; за раунд не міняється, між раундами тасується.</summary>
    public int Id;
    /// <summary>Точка на землі в одиницях світу (луг — 1200 × 360).</summary>
    public int X, Y;
    /// <summary>Куди дивиться: 0 праворуч (до Баби), 1 вниз, 2 ліворуч, 3 вгору.</summary>
    public int Dir;
    /// <summary>Куди хоче йти; -1 — стоїть.</summary>
    public int Want = -1;
    /// <summary>Цього тика зробив крок — саме це й бачить Баба, коли дивиться.</summary>
    public bool Moving;
    /// <summary>Хотів іти, але вперся в край лугу.</summary>
    public bool Blocked;
    /// <summary>Місце гравця; -1 — бот.</summary>
    public int Owner = -1;

    /// <summary>Лежить після штурхана (лише гравець — штурхан бота валить не бота, а того, хто штурхав).</summary>
    public int Down;
    /// <summary>«Отетерів»: штурхнув бота й стоїть, кліпає очима.</summary>
    public int Dazed;
    /// <summary>Баба впіймала: стоїть, поки вона не відвернеться (і щонайменше секунду), потім — на старт.</summary>
    public int Caught;
    /// <summary>Щойно повернули на старт: цього тика не ступає (і бот, і гравець), тож повернення — чистий стрибок.</summary>
    public bool JustBack;
    /// <summary>Щойно встав — не штурхають (інакше двоє мисливців тримали б одного на землі без кінця).</summary>
    public int Guard;
    /// <summary>Руки ще не відійшли після штурхана.</summary>
    public int PushCool;

    // ---- мозок бота ----
    /// <summary>Ще стільки тиків іти в бік <see cref="Want"/>.</summary>
    public int Run;
    /// <summary>Ще стільки тиків стояти (перепочити, роззирнутись).</summary>
    public int Rest;
    /// <summary>Баба знову співає — рушити через стільки тиків (реакція, як у людини).</summary>
    public int StartIn;
    /// <summary>Баба обертається (чи озирнулась) — ще стільки тиків іде за інерцією, тоді стає.</summary>
    public int StopIn;
    /// <summary>Стоїть, доки Баба знову не заспіває.</summary>
    public bool Hold;
    /// <summary>До якої точки йти, перш ніж розвернутись назад (боти глека не торкаються).</summary>
    public int GoalX;

    // ---- вдача на раунд (кидається всім однаково) ----
    /// <summary>0 ледачий, 1 звичайний, 2 бадьорий, 3 нетерплячий.</summary>
    public int Tempo;
    /// <summary>Відсоток, з яким спиняється, коли Баба лише озирнулась.</summary>
    public int Caution;

    // ---- вигляд на раунд ----
    public int Hat, HatColor, Shirt, Skin, Name;

    /// <summary>Стан у кадрі: 0 стоїть, 1 іде, 2 лежить, 3 впійманий, 4 отетерів.</summary>
    public int State => Caught > 0 ? 3 : Down > 0 ? 2 : Dazed > 0 ? 4 : Moving ? 1 : 0;

    /// <summary>Не ходить, хоч би що хотів: лежить, отетерів або впійманий.</summary>
    public bool Still => Down > 0 || Dazed > 0 || Caught > 0;
}

/// <summary>
/// «Замри!» без кімнат і очок: селяни, один спільний крок, Баба Параска (співає, озирається, обертається, дивиться),
/// мозок ботів, штурхан, розстановка раунду. Усе ціле, уся випадковість — із переданого <see cref="Random"/> у строгому
/// порядку, тож той самий сід і той самий ввід дають побайтно ті самі кадри.
/// </summary>
public sealed class FreezeCore(Random rng)
{
    // ---- луг ----
    /// <summary>Світ 1200 × 360 одиниць: довгий луг зліва направо, вгорі — тин і соняхи, праворуч — хата Баби.</summary>
    public const int WorldW = 1200, WorldH = 360;
    /// <summary>Де можна ходити: x від тину до двору, y — смуга лугу.</summary>
    public const int MinX = 24, MaxX = 1100, MinY = 100, MaxY = 340;
    /// <summary>Стартова смуга біля тину: тут стають на початку раунду й сюди вертають упійманих.</summary>
    public const int StartMinX = 32, StartMaxX = 72;
    /// <summary>Фінішна смуга — поріг двору з глеком: перетнув (X ≥ 1080) — торкнувся глека.</summary>
    public const int FinishX = 1080;
    /// <summary>Ближче до фінішу боти не підходять (розвертаються й ідуть назад): прихід бота — не перемога.</summary>
    public const int BotMaxX = FinishX - 16;
    /// <summary>Куди бот іде, перш ніж розвернутись: десь під самою хатою.</summary>
    public const int GoalMin = FinishX - 220, GoalMax = BotMaxX;

    /// <summary>Крок за тик — однаковий для всіх: 50 од/с, від тину до глека ≈ 20 с чистої ходи. Бігу нема.</summary>
    public const int Speed = 2;

    // ---- Баба ----
    public const int Sing = 0, Turn = 1, Watch = 2, Glance = 3;
    /// <summary>Співає 2–6 с; першого разу в раунді — щонайменше 3 с (щоб усі встигли рушити).</summary>
    public const int SingMin = 50, SingMax = 150, FirstSingMin = 75;
    /// <summary>Після обманки співає коротше: 1–4 с.</summary>
    public const int ShortSingMin = 25, ShortSingMax = 100;
    /// <summary>У стількох відсотках кінець пісні — не поворот, а «озирнулась через плече» (0,5–1 с).</summary>
    public const int GlanceChance = 30, GlanceMin = 12, GlanceMax = 24;
    /// <summary>Після того, як озирнулась, справді обертається у стількох відсотках; решта — обманка, співає далі.</summary>
    public const int GlanceTurn = 45;
    /// <summary>
    /// Благодать після «Замри!»: 11 тиків = 440 мс. Бюджет: тик, на якому Баба обернулась, іде кадром одразу; кадр
    /// летить до гравця (≈ 30–80 мс, Wi-Fi/4G), банер малюється з найсвіжішого кадру без інтерполяції (≤ 16 мс), людина
    /// відпускає клавішу за ≈ 220–260 мс (різкий банер і звук), відповідь летить назад (≈ 30–80 мс), а сервер застосовує
    /// її до кроку наступного тика (≤ 40 мс квантування). 40 + 16 + 2·60 + 250 ≈ 426 мс ≤ 440. Довша благодать зробила б
    /// «замри» безпечним для неуважних, коротша карала б за пінг, а не за неувагу.
    /// </summary>
    public const int Grace = 11;
    /// <summary>Дивиться 2–4 с. Хто зрушив хоч на крок — впійманий.</summary>
    public const int WatchMin = 50, WatchMax = 100;

    // ---- впіймали, штурхан ----
    /// <summary>Впійманий стоїть щонайменше секунду (Баба тицяє пальцем), і доки вона дивиться, — потім на старт.</summary>
    public const int CaughtTicks = 25;
    /// <summary>Штурхан: найближчий попереду на 32 од.; явна ціль (клік) — до 44 (клієнт бачить кадр на ~100 мс старший).</summary>
    public const int PushRange = 32, PushRangeMax = 44;
    /// <summary>Руки відходять 3 с.</summary>
    public const int PushCoolTicks = 75;
    /// <summary>Збитий гравець лежить 1,6 с, а встав — 2 с його не штурхають.</summary>
    public const int DownTicks = 40, GuardTicks = 50;
    /// <summary>Штурхнув бота — отетерів на 1,5 с (публічно: над тобою зірочки).</summary>
    public const int DazeTicks = 38;

    // ---- боти ----
    /// <summary>
    /// Реакція бота на «Замри!»: <c>StopIn</c> 3–12 — останній крок на 1–10-му тику після повороту (як у людини, що
    /// відпустила клавішу за 250 мс із пінгом 20–90 мс); незграба (4,5 %) — 13–19, тобто ще крокує, коли Баба вже дивиться.
    /// </summary>
    public const int ReactMin = 3, ReactMax = Grace + 1, ClumsyMilli = 45;
    /// <summary>Баба знову співає — бот рушає за 0–30 тиків (дехто стоїть напоготові й рушає одразу, як і людина).</summary>
    public const int StartMax = 30;
    /// <summary>Штурхає сусіда в стількох випадках із 10 000 тиків (якщо є кого): рідко, але штурхан — не вирок «гравець».</summary>
    public const int BotPushTenK = 4;
    /// <summary>Шанс перепочити, найдовший перепочинок і відрізок ходи — за вдачею (ледачий … нетерплячий).</summary>
    static readonly int[] RestChance = [45, 30, 18, 8], RestMax = [90, 60, 40, 25], RunMin = [10, 15, 20, 40], RunMax = [40, 70, 100, 160];

    public static readonly int[] DX = [1, 0, -1, 0];
    public static readonly int[] DY = [0, 1, 0, -1];

    /// <summary>Імена селян: тасуються на кожен раунд, беруться перші N. Ім'я гравця — таке саме, як у бота.</summary>
    public static readonly string[] Names =
    [
        "Параска", "Микола", "Ганна", "Степан", "Одарка", "Тарас", "Марічка", "Гриць",
        "Оксана", "Панас", "Домаха", "Іванко", "Соломія", "Назар", "Мотря", "Богдан",
        "Ярина", "Остап", "Христя", "Данило", "Марта", "Максим", "Устя", "Юрко",
        "Килина", "Левко", "Наталка", "Роман", "Пріська", "Андрійко", "Гафія", "Василь",
        "Софійка", "Тиміш", "Орися", "Прокіп", "Настя", "Лесь", "Явдоха", "Захар",
        "Меланка", "Матвій", "Феся", "Сашко", "Текля", "Ігнат", "Зоряна", "Йосип",
        "Уляна", "Федь", "Люба", "Влас", "Дарина", "Кузьма", "Олеся", "Сидір",
    ];

    readonly Random _rng = rng;
    readonly int[] _nameOrder = new int[Names.Length];

    public FreezeVillager[] V { get; private set; } = [];
    public int N => V.Length;

    /// <summary>Що зараз робить Баба (<see cref="Sing"/>, <see cref="Turn"/>, <see cref="Watch"/>, <see cref="Glance"/>).</summary>
    public int Baba { get; private set; } = Watch;
    /// <summary>Скільки тиків ще триватиме поточний стан Баби.</summary>
    public int BabaLeft { get; private set; }
    /// <summary>Скільки дивитиметься після благодаті — кидається на повороті.</summary>
    int _watch;

    /// <summary>
    /// Скільки лишилось, доки Баба знову відвернеться, — лише коли вона обертається чи дивиться (відлік «замри»).
    /// Поки співає чи озирається — 0: скільки ще співатиме, не знає ніхто.
    /// </summary>
    public int Countdown => Baba switch
    {
        Turn => BabaLeft + _watch,
        Watch => BabaLeft,
        _ => 0,
    };

    /// <summary>Події тика: [1, хто, кого, упав(1)|хитнувся(0)] · [2, id] впіймали · [3, id] повернувся на старт.</summary>
    public List<int[]> Ev { get; } = [];

    // ---------------------------------------------------------------------------------------------
    // Крок — один на всіх
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Крок селянина. Хто лежить, отетерів, впійманий чи нікуди не хоче — стоїть. Інакше повертається в бік
    /// <see cref="FreezeVillager.Want"/> (навіть коли там край лугу) і робить рівно <see cref="Speed"/> одиниць, якщо не
    /// виходить за луг. Ця функція одна для ботів і гравців.
    /// </summary>
    public static void Step(FreezeVillager v)
    {
        if (v.Still || v.Want < 0 || v.JustBack)
        {
            v.Moving = false;
            v.Blocked = false;
            v.JustBack = false;
            return;
        }
        v.Dir = v.Want;
        int nx = v.X + DX[v.Dir] * Speed, ny = v.Y + DY[v.Dir] * Speed;
        if (nx >= MinX && nx <= MaxX && ny >= MinY && ny <= MaxY)
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

    // ---------------------------------------------------------------------------------------------
    // Тик фази «іди»
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Тик гри: годинники селян → Баба → мозок ботів → крок усіх → хто зрушив, поки Баба дивиться, — впійманий.
    /// Події — у <see cref="Ev"/> (гра чистить його сама на початку тика й докидає події з дій між тиками).
    /// </summary>
    public void TickGo()
    {
        TimersAll();
        BabaTick();
        ThinkAll();
        StepAll();
        if (Baba == Watch) CatchMovers();
    }

    /// <summary>Годинники: руки, «не штурхати», лежання, отетеріння; впійманий іде на старт, коли Баба відвернулась.</summary>
    public void TimersAll()
    {
        var v = V;
        for (var i = 0; i < v.Length; i++)
        {
            var q = v[i];
            if (q.PushCool > 0) q.PushCool--;
            if (q.Guard > 0) q.Guard--;
            if (q.Dazed > 0) q.Dazed--;
            if (q.Down > 0 && --q.Down == 0) q.Guard = GuardTicks;
            if (q.Caught > 0)
            {
                // доки Баба дивиться, впійманий стоїть під її пальцем; відвернулась — іде на старт
                if (q.Caught > 1 || Baba != Watch) q.Caught--;
                if (q.Caught == 0) SendBack(q);
            }
        }
    }

    /// <summary>Хто зрушив цього тика, поки Баба дивиться, — впійманий. Бот і гравець однаково.</summary>
    void CatchMovers()
    {
        var v = V;
        for (var i = 0; i < v.Length; i++)
        {
            var q = v[i];
            if (!q.Moving) continue;
            q.Moving = false;
            q.Caught = CaughtTicks;
            if (q.Owner < 0) Forget(q);
            Ev.Add([2, q.Id]);
        }
    }

    /// <summary>Естафета: торкнувся глека — назад до тину, як упійманий, а в кадр — подія [4, id, команда].</summary>
    public void RelayBack(FreezeVillager v, int team)
    {
        SendBack(v);
        Ev.Add([4, v.Id, team]);
    }

    /// <summary>На старт: біля тину, на тій самій висоті лугу. Бот думає з чистого аркуша; гравець тримає, що тримав.</summary>
    void SendBack(FreezeVillager v)
    {
        v.X = _rng.Next(StartMinX, StartMaxX + 1);
        v.Dir = 0;
        v.Moving = false;
        v.Blocked = false;
        v.JustBack = true;
        if (v.Owner < 0) Forget(v);
        Ev.Add([3, v.Id]);
    }

    // ---------------------------------------------------------------------------------------------
    // Баба Параска
    // ---------------------------------------------------------------------------------------------

    /// <summary>Роздивились — Баба відвертається й починає співати; боти рушають хто одразу, хто трохи згодом.</summary>
    public void Open()
    {
        Baba = Sing;
        BabaLeft = _rng.Next(FirstSingMin, SingMax + 1);
        _watch = 0;
        foreach (var v in V)
        {
            if (v.Owner >= 0) continue;
            Forget(v);
            v.StartIn = _rng.Next(0, StartMax + 1);
        }
    }

    /// <summary>
    /// Співає → (озирнулась → співає далі | обертається) → «Замри!» (благодать) → дивиться → співає. Тривалості
    /// випадкові; скільки ще співатиме, не знає ніхто, а скільки дивитиметься — видно (відлік).
    /// </summary>
    void BabaTick()
    {
        if (--BabaLeft > 0) return;
        switch (Baba)
        {
            case Sing:
                if (_rng.Next(100) < GlanceChance)
                {
                    Baba = Glance;
                    BabaLeft = _rng.Next(GlanceMin, GlanceMax + 1);
                    BotsGlance();
                }
                else StartTurn();
                break;
            case Glance:
                if (_rng.Next(100) < GlanceTurn) StartTurn();
                else
                {
                    Baba = Sing;
                    BabaLeft = _rng.Next(ShortSingMin, ShortSingMax + 1);
                    BotsResume(onlyHeld: true);
                }
                break;
            case Turn:
                Baba = Watch;
                BabaLeft = _watch;
                break;
            default:
                Baba = Sing;
                BabaLeft = _rng.Next(SingMin, SingMax + 1);
                BotsResume(onlyHeld: false);
                break;
        }
    }

    void StartTurn()
    {
        Baba = Turn;
        BabaLeft = Grace;
        _watch = _rng.Next(WatchMin, WatchMax + 1);
        BotsTurn();
    }

    /// <summary>
    /// «Замри!»: хто з ботів іде — стане за 3–12 тиків (як людина: побачив, відпустив, пінг), а незграба (4,5 %) — за
    /// 13–19, уже коли Баба дивиться, і його впіймають, як і гравця, що загавився.
    /// </summary>
    void BotsTurn()
    {
        foreach (var v in V)
        {
            if (v.Owner >= 0 || v.Still) continue;
            v.Hold = true;
            if (v.Want < 0) continue;
            if (v.StopIn > 0) continue;           // уже спиняється (озирнулась) — хай так і буде
            v.StopIn = _rng.Next(1000) < ClumsyMilli ? _rng.Next(ReactMax + 1, ReactMax + 8) : _rng.Next(ReactMin, ReactMax + 1);
        }
    }

    /// <summary>Озирнулась: обачні боти про всяк випадок стають (за 3–12 тиків) і чекають, решта йде далі.</summary>
    void BotsGlance()
    {
        foreach (var v in V)
        {
            if (v.Owner >= 0 || v.Still || v.Want < 0) continue;
            if (_rng.Next(100) >= v.Caution) continue;
            v.Hold = true;
            v.StopIn = _rng.Next(ReactMin, ReactMax + 1);
        }
    }

    /// <summary>Знову співає: хто стояв, рушить за 0–30 тиків; після «дивиться» — усі з чистого аркуша.</summary>
    void BotsResume(bool onlyHeld)
    {
        foreach (var v in V)
        {
            if (v.Owner >= 0 || v.Still) continue;
            if (onlyHeld && !v.Hold) continue;
            v.Hold = false;
            v.StopIn = 0;
            v.Run = 0;
            v.Rest = 0;
            v.Want = -1;
            v.StartIn = _rng.Next(0, StartMax + 1);
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
    /// Бот виставляє <see cref="FreezeVillager.Want"/> так, щоб у кадрах його не відрізнити від людини: іде до хати
    /// відрізками, перепочиває, ступає вбік і назад, реагує на Бабу з людською затримкою (а зрідка — запізно), біля хати
    /// розвертається й іде назад (глека не торкається), зрідка штурхає сусіда.
    /// </summary>
    public void Think(FreezeVillager v)
    {
        if (v.Still) return;                               // Step і так не рушить
        if (v.Want == 0 && v.X + Speed > BotMaxX)
        {
            // до глека бот не ступає ніколи — навіть за інерцією після «Замри!»
            v.Want = -1;
            v.Run = 0;
        }
        if (v.StopIn > 0)
        {
            // побачив Бабу — ще кілька тиків іде за інерцією (реакція), тоді стає
            if (--v.StopIn == 0)
            {
                v.Want = -1;
                v.Run = 0;
            }
            return;
        }
        if (v.Hold || Baba is Turn or Watch)
        {
            v.Want = -1;
            return;
        }
        if (v.StartIn > 0)
        {
            v.StartIn--;
            v.Want = -1;
            return;
        }
        MaybePush(v);
        if (v.Dazed > 0) return;
        if (v.Run > 0)
        {
            v.Run--;
            if (v.Blocked || (v.Want == 0 && v.X + Speed > Math.Min(v.GoalX, BotMaxX))) v.Run = 0;
            else return;
        }
        if (v.Rest > 0)
        {
            v.Rest--;
            v.Want = -1;
            return;
        }
        Decide(v);
    }

    /// <summary>Наступний відрізок: під хатою — назад; інакше перепочити, ступити вбік, назад або йти вперед.</summary>
    void Decide(FreezeVillager v)
    {
        if (v.X + Speed > Math.Min(v.GoalX, BotMaxX))
        {
            // дійшов під хату — розвертається й іде назад по своїх (глек — не для нього), потім знову сюди
            v.Want = 2;
            v.Run = _rng.Next(30, 141);
            v.GoalX = _rng.Next(GoalMin, GoalMax + 1);
            return;
        }
        var t = v.Tempo;
        var r = _rng.Next(100);
        if (r < RestChance[t])
        {
            v.Want = -1;
            v.Rest = _rng.Next(4, RestMax[t] + 1);
            return;
        }
        r -= RestChance[t];
        if (r < 12)
        {
            v.Want = _rng.Next(2) == 0 ? 1 : 3;           // ступив убік
            v.Run = _rng.Next(4, 27);
            return;
        }
        if (r < 16)
        {
            v.Want = 2;                                    // крок-другий назад
            v.Run = _rng.Next(4, 19);
            return;
        }
        v.Want = 0;
        v.Run = _rng.Next(RunMin[t], RunMax[t] + 1);
    }

    /// <summary>Бот зрідка штурхає того, хто перед ним, — тим самим штурханом, що й гравець.</summary>
    void MaybePush(FreezeVillager v)
    {
        if (v.PushCool > 0 || _rng.Next(10_000) >= BotPushTenK) return;
        var t = Nearest(v);
        if (t < 0) return;
        Ev.Add(Push(v, V[t]));
    }

    /// <summary>Бот устав, повернувся на старт чи став ботом після виходу гравця: думає з чистого аркуша.</summary>
    public static void Forget(FreezeVillager v)
    {
        v.Want = -1;
        v.Run = 0;
        v.Rest = 0;
        v.StartIn = 0;
        v.StopIn = 0;
        v.Hold = false;
        v.Blocked = false;
    }

    // ---------------------------------------------------------------------------------------------
    // Штурхан
    // ---------------------------------------------------------------------------------------------

    /// <summary>Баба не дивиться: штурхати можна, поки співає чи лише озирається.</summary>
    public bool PushAllowed => Baba is Sing or Glance;

    /// <summary>Кого можна штурхнути: не себе, не лежачого, не впійманого, не того, хто щойно встав.</summary>
    public static bool Pushable(FreezeVillager p, FreezeVillager q) => q != p && q.Down == 0 && q.Caught == 0 && q.Guard == 0;

    /// <summary>
    /// Найближчий, кого можна штурхнути, попереду (у півплощині погляду) на <see cref="PushRange"/>; при рівній
    /// відстані — менший id. -1 — нікого. Той самий алгоритм підсвічує ціль на клієнті.
    /// </summary>
    public int Nearest(FreezeVillager p)
    {
        int best = -1;
        long bestD = long.MaxValue;
        int fx = DX[p.Dir], fy = DY[p.Dir];
        var v = V;
        for (var i = 0; i < v.Length; i++)
        {
            var q = v[i];
            if (!Pushable(p, q)) continue;
            long dx = q.X - p.X, dy = q.Y - p.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 > (long)PushRange * PushRange || dx * fx + dy * fy <= 0) continue;
            if (d2 < bestD)
            {
                bestD = d2;
                best = i;
            }
        }
        return best;
    }

    public static long Dist2(FreezeVillager a, FreezeVillager b)
    {
        long dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    /// <summary>
    /// Штурхан (перевірки — у того, хто кличе). Гравця — валить на 1,6 с. Бота — ні: той лише хитнувся й постояв, а
    /// штурхач отетерів на 1,5 с — і це бачать усі. Однаково, хто штурхає: бот чи гравець.
    /// </summary>
    public int[] Push(FreezeVillager p, FreezeVillager q)
    {
        p.PushCool = PushCoolTicks;
        if (q.Owner >= 0)
        {
            q.Down = DownTicks;
            q.Moving = false;
            return [1, p.Id, q.Id, 1];
        }
        p.Dazed = DazeTicks;
        p.Moving = false;
        q.Run = 0;
        q.Want = -1;
        q.Rest = Math.Max(q.Rest, 12);
        q.Moving = false;
        return [1, p.Id, q.Id, 0];
    }

    // ---------------------------------------------------------------------------------------------
    // Раунд
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Нова юрма біля тину: <paramref name="owners"/> — місця гравців (ідуть першими), далі <paramref name="bots"/> ботів.
    /// Порядок випадковості: розстановка → тасування id → вигляд → імена → вдача. Усім однаково: і ботам, і гравцям.
    /// Баба дивиться на луг (роздивись), доки гра не скаже <see cref="Open"/>.
    /// </summary>
    public void Deal(IReadOnlyList<int> owners, int bots)
    {
        var n = owners.Count + bots;
        var list = new FreezeVillager[n];
        for (var i = 0; i < n; i++)
        {
            list[i] = new FreezeVillager
            {
                Owner = i < owners.Count ? owners[i] : -1,
                X = _rng.Next(StartMinX, StartMaxX + 1),
                Y = _rng.Next(MinY, MaxY + 1),
                Dir = 0,
            };
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
            var r = _rng.Next(100);
            v.Tempo = r < 20 ? 0 : r < 55 ? 1 : r < 80 ? 2 : 3;
            v.Caution = _rng.Next(0, 81);
            v.GoalX = _rng.Next(GoalMin, GoalMax + 1);
        }
        V = list;
        Baba = Watch;
        BabaLeft = 0;
        _watch = 0;
        Ev.Clear();
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
        for (var i = 0; i < v.Length; i++) a[i] = Names[v[i].Name];
        return a;
    }

    /// <summary>Для тестів: поставити Бабу в потрібний стан на потрібну кількість тиків.</summary>
    public void SetBabaForTests(int state, int left, int watch = WatchMin)
    {
        Baba = state;
        BabaLeft = left;
        _watch = watch;
    }
}
