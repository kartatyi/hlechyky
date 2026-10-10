using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Заморський гість Гостинного двору: хто, з якого щабля драбини приїжджає, що любить і що дає його шана.
/// </summary>
/// <param name="Tier">Щабель драбини, з першим рівнем якого гість прибуває (<see cref="Clicker.GuestTiers"/>).</param>
/// <param name="People">Хто саме приходить із замовленням — повним підписом, з місцем («Купець Мехмед із Царграда»).</param>
/// <param name="Likes">Улюблені вироби (ключі <see cref="Clicker.Wares"/>); порожньо — годиться будь-який відкритий.</param>
/// <param name="Quality">Мінімальна якість у замовленні: 2 добрий, 3 дзвінкий, 4 розкішний.</param>
/// <param name="Perk">Пільга за рівень шани — повним реченням.</param>
/// <param name="Step">Що саме додає наступний рівень — одним рядком на картку гостя.</param>
/// <param name="Kind">Яку пільгу дає: value, golden, pay, fair, offline, fall — з цього клієнт пише «зараз: +15 %».</param>
/// <param name="Per">Скільки пільги за рівень у своїх одиницях (частка, хвилини, додаток до множника).</param>
/// <param name="Arrive">Рядок у стрічку й у «поки тебе не було», коли гість прибув.</param>
/// <param name="Lines">Що гість каже, коли забирає замовлення.</param>
/// <param name="StylePct">Як часто просить розпис (відсотки; розпис — лише з куплених гравцем).</param>
/// <param name="PayMult">У скільки разів платить понад ціну виробу.</param>
public sealed record ClickerGuest(
    string Key, string Tier, string Emoji, string Name, string From, string[] People, string[] Likes, int Quality,
    string Perk, string Step, string Kind, double Per, string Arrive, string[] Lines,
    int CountMin = 1, int CountMax = 3, int StylePct = 15, double PayMult = 8);

/// <summary>
/// Заморські гості — «Гостинний двір» (десяте оновлення, docs/games/specs/clicker-v10.md §7; як зроблено —
/// clicker-v10-c.md). Шість щаблів нової драбини приводять своїх гостей — царградських і кантонських купців, діаспору
/// з Канади, лондонських торговців, паризьких колекціонерів і гончарів з усього світу. Гість прибуває з першим рівнем
/// свого щабля й лишається назавжди (обпал рівні палить, а гостей — ні), замовляє вироби з горна й має свою шану з
/// рівнями: кожен рівень будь-кого — +3 % до всього, а ще своя пільга.
///
/// Чому так: пізня гра живе на клеймах і пасиві, а вироби з горна там ідуть здебільшого на базар. Замовлення гостей
/// платять ×8 від ціни виробу (гончарі світу — ×12), але приходять раз на 20–40 хв і просять якісне, тож це не потік
/// глеків, а довга мета на тижні — як шана сіл, тільки для тих, хто дійшов до кінця драбини.
/// </summary>
public sealed partial class Clicker
{
    /// <summary>Щаблі драбини, що приводять гостей, — у порядку прибуття.</summary>
    public static readonly string[] GuestTiers = ["port", "voyage", "ocean", "exchange", "expo", "opishnia"];

    // ---------- числа ----------

    /// <summary>
    /// Пороги рівнів шани гостей. Замовлення дає 6–17 очок, тож п'ятий рівень — це два-три тижні з одним гостем:
    /// шана гостей має бути метою, а не нагородою за перший вечір. Верх (7–10) знижено 10.10: було 280/400/550/750,
    /// а лідер набирає з найкращим гостем ~30–35 очок на добу — десятий рівень був би за три тижні, тепер — за тиждень.
    /// </summary>
    public static readonly int[] GuestRepLevels = [0, 5, 15, 35, 70, 120, 190, 260, 330, 400, 480];
    /// <summary>Кожен рівень шани будь-якого гостя — плюс три відсотки до всього.</summary>
    public const double GuestRepAll = 0.03;
    /// <summary>Шана самого гостя додає до його плати: +10 % за рівень.</summary>
    public const double GuestPayPerLevel = 0.1;
    /// <summary>Місць на дворі — скільки гостей прибуло, але не більше трьох; «Заморська карта» — ще одне.</summary>
    public const int GuestSlotsMax = 3, GuestSeamapSlots = 1;
    /// <summary>Нове замовлення — за 20–40 хв реального часу; із «Заморською картою» — у 0,7 раза швидше.</summary>
    public const int GuestGapMinMinutes = 20, GuestGapMaxMinutes = 40;
    public const double GuestSeamapGap = 0.7;
    /// <summary>Скільки гість чекає на дворі: три-шість годин.</summary>
    public const int GuestLifeMinMinutes = 180, GuestLifeMaxMinutes = 360;
    /// <summary>Пільги за рівень: вироби (Царград), розписний глек (Кантон), купці й села (діаспора), ярмарок (Лондон),
    /// коло без тебе (Париж), глек з полиці (гончарі світу).</summary>
    public const double GuestPerkValue = 0.05, GuestPerkGolden = 0.03, GuestPerkPay = 0.05, GuestPerkFair = 0.3, GuestPerkFall = 0.05;
    public const int GuestPerkOfflineMinutes = 20;
    /// <summary>Рівні для ачівок: десятий в одного гостя — «Шанований у світі», п'ятий у всіх шести — «Свій у всьому світі».</summary>
    public const int GuestRepTop = 10, GuestRepAllFrom = 5;

    /// <summary>
    /// Ачівки гостей (контракт §12). Перший ключ — не «potter-guest», як у таблиці контракту: цей ключ із сьомого
    /// оновлення вже зайнятий «Гостинною хатою» (двадцять гостей села, ClickerFair.cs). Із ним гравці, що її мають,
    /// «Першого гостя» ніколи б не отримали, а решті за перше замовлення дісталась би чужа ачівка.
    /// </summary>
    public const string GuestAchFirst = "potter-guest-first", GuestAchMax = "potter-guest-max", GuestAchAll = "potter-guests-all";

    /// <summary>
    /// Шість гостей — у порядку щаблів. Факти в текстах — лише перевірені (Галата й Скутарі — квартали Стамбула,
    /// шлюп «Нева» Лисянського заходив у Кантон, Вінніпег, Едмонтон і Саскатун — міста великої української громади
    /// Канади, Ейфелева вежа стояла й на виставці 1900-го, Делфт славиться синіми кахлями, Кіото — чашками, Талавера —
    /// керамікою); решта — жарти, що фактом не прикидаються.
    /// </summary>
    public static readonly ClickerGuest[] Guests =
    [
        new("tsargrad", "port", "🕌", "Царградські купці", "з Царграда",
            ["Купець Мехмед із Царграда", "Купець Селім із Галати", "Купець Юсуф зі Скутарі"],
            ["jug", "kumanets", "candle"], 2,
            "Вироби на базарі й у замовленнях дорожчі: +5 % за рівень", "вироби ще +5 %", "value", GuestPerkValue,
            "⚓ До Одеського порту зайшов корабель: царградські купці питають твої глечики",
            ["Такий куманець і в султана не стоїть!", "Повезу на Галату — хай увесь базар заздрить",
                "За такий глечик — і кава, і халва, і повага!", "Босфор широкий, а слава про твої глеки ширша",
                "Колись ваші чайки припливали до нас без запрошення, а тепер ми до вас — по глеки!"]),
        new("canton", "voyage", "🏮", "Кантонські купці", "з Кантона",
            ["Купець Лян із Кантона", "Купець Ву з Кантона", "Купець Хо з Кантона"],
            ["bowl", "dish", "tile"], 3,
            "Розписний глек з'являється частіше: −3 % чекання за рівень", "розписний глек ще −3 % чекання", "golden", GuestPerkGolden,
            "🏮 Слідом за кругосвітнім плаванням прибули кантонські купці — з батьківщини порцеляни, тож прискіпливі",
            ["Ми порцеляну віками робимо, а такої миски ще не бачили", "Спершу постукаю… Дзвенить! Беру",
                "Кахля — майже як із імператорського палацу. Майже", "Шлюп «Нева» в Кантоні вже бував — тепер черга твоїх мисок",
                "До кантонського чаю — саме такий полумисок"]),
        new("diaspora", "ocean", "🍁", "Діаспора з Канади", "з Канади",
            ["Тітка Марія з Вінніпега", "Дядько Василь з Едмонтона", "Баба Параска із Саскатуна"],
            ["makitra", "pot", "barrel"], 2,
            "Купці хати й замовлення сіл платять більше: +5 % за рівень", "купці й замовлення сіл ще +5 %", "pay", GuestPerkPay,
            "🍁 Пароплав привіз гостей з-за океану: діаспора з Канади хоче макітри й горщики — «як удома»",
            ["Як у мами в Галичині!", "Макітру — на мак, горщик — на борщ, а барило — на свята",
                "Прерія велика, а таких горщиків на всю провінцію нема", "Напишу мамі: горщики тут дорогі, а твої — найкращі",
                "Повезу через океан, щоб і онуки знали, як пахне дім"],
            CountMin: 2, CountMax: 4),
        new("london", "exchange", "🎩", "Лондонські торговці", "з Лондона",
            ["Містер Сміт із Лондона", "Місіс Браун із Лондона", "Містер Тейлор із Сіті"],
            ["tykva", "kukhol", "whistle"], 2,
            "Ярмарок розписного глека сильніший: +0,3 до множника за рівень", "ярмарок ще +0,3", "fair", GuestPerkFair,
            "🎩 На Одеській біржі з'явились лондонські торговці: питають тикви й кухлі",
            ["Непогано. Зовсім непогано", "Кухоль під чай — о п'ятій, як годиться",
                "На біржі кажуть: глек — тверда валюта. Купую, поки не подорожчав!",
                "Свищик-півник — і лондонський туман уже не такий сумний", "У Лондоні дощ, а в тебе тиква — як сонце"]),
        new("paris", "expo", "🗼", "Паризькі колекціонери", "з Парижа",
            ["Мсьє Анрі з Парижа", "Мадам Клер з Парижа", "Мсьє Лефевр з Монмартру"],
            ["lion", "ram", "pleskanets"], 3,
            "Коло крутиться без тебе довше: +20 хв за рівень (разом не більше доби)", "коло без тебе ще +20 хв", "offline",
            GuestPerkOfflineMinutes,
            "🗼 Після Всесвітньої виставки в Парижі приїхали колекціонери: полюють на левів і баранців",
            ["Magnifique! Цього лева — одразу у вітрину", "Баранець-свищик? Це ж мистецтво, мсьє!",
                "На виставці Ейфелева вежа, а черга — до твоїх глеків", "Розпис — як у найкращому салоні. Беру до колекції",
                "Плесканець — під скло, і нікому не торкатись!"],
            StylePct: 40),
        new("masters", "opishnia", "🌍", "Гончарі з усього світу", "з усього світу",
            ["Майстер із Делфта", "Майстриня з Кіото", "Гончар із Талавери"],
            [], 4,
            "Глек з полиці щедріший: +5 % за рівень", "глек з полиці ще +5 %", "fall", GuestPerkFall,
            "🌍 До Опішні з'їхались гончарі з усього світу: вчитися й купувати — лише розкішне",
            ["Приїхали вчитися, а ти ще й продаєш!", "Удома не повірять, що таке виліпили руками",
                "Глина в кожного своя, а таких рук — ні в кого", "У Делфті — сині кахлі, у Кіото — чашки, а таке — лише в Опішні",
                "Гончар гончара бачить здалеку"],
            PayMult: 12),
    ];

    // Номери гостей у Guests — для пільг, які ядро питає щокадру: без пошуку за ключем.
    const int GTsargrad = 0, GCanton = 1, GDiaspora = 2, GLondon = 3, GParis = 4, GMasters = 5;

    // ---------- стан ----------

    /// <summary>Замовлення гостя: хто (номер у <see cref="ClickerGuest.People"/>), що, якої якості й до коли чекає.</summary>
    sealed record GuestOrderRow(int Id, string Guest, int Who, string Ware, string Style, int Quality, int Count,
        DateTimeOffset At, DateTimeOffset Until);

    /// <summary>Стан пакета в збереженні. Усе необов'язкове: старе збереження — «гостей ще не було».</summary>
    /// <param name="Met">Хто прибув і коли (обпал цього не чіпає).</param>
    /// <param name="Rep">Очки шани кожного гостя.</param>
    /// <param name="Next">Коли на двір прийде наступне замовлення.</param>
    /// <param name="Delivered">Скільки замовлень гостей виконано за весь час.</param>
    sealed record GuestsRow(
        Dictionary<string, DateTimeOffset>? Met = null, Dictionary<string, int>? Rep = null, List<GuestOrderRow>? Orders = null,
        int OrderId = 0, DateTimeOffset Next = default, int Delivered = 0);

    readonly Dictionary<string, DateTimeOffset> _gMet = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _gRep = new(StringComparer.Ordinal);
    readonly List<GuestOrderRow> _gOrders = [];
    int _gOrderId;
    DateTimeOffset _gNext;
    int _gDelivered;
    /// <summary>Рівні шани кожного гостя та їхня сума: множник до всього ядро питає дуже часто, тож тримаємо готовими.</summary>
    readonly int[] _gLevel = new int[GuestTiers.Length];
    int _gLevelSum;

    // ---------- що гості дають ядру ----------

    /// <summary>+3 % до всього за кожен рівень шани будь-яких гостей.</summary>
    double GuestsAllMult => 1 + GuestRepAll * _gLevelSum;
    /// <summary>Гончарі з усього світу: додаток до глека з полиці (поруч із кошиком).</summary>
    double GuestsFallBonus => GuestPerkFall * _gLevel[GMasters];
    /// <summary>Кантонські купці: множник чекання розписного глека (≤ 1).</summary>
    double GuestsGoldenWait => Math.Max(0.5, 1 - GuestPerkGolden * _gLevel[GCanton]);
    /// <summary>Діаспора: додаток до плати купців хати й замовлень сіл. До замовлень самих гостей не йде — там своя шана.</summary>
    double GuestsPayBonus => GuestPerkPay * _gLevel[GDiaspora];
    /// <summary>Паризькі колекціонери: коло крутиться без тебе довше (загальна стеля — 24 год, її ставить ядро).</summary>
    TimeSpan GuestsOfflineExtra => TimeSpan.FromMinutes(GuestPerkOfflineMinutes * _gLevel[GParis]);
    /// <summary>Царградські купці: додаток до ціни виробів на базарі й у замовленнях.</summary>
    double GuestsValueBonus => GuestPerkValue * _gLevel[GTsargrad];
    /// <summary>Лондонські торговці: додаток до множника ярмарку розписного глека.</summary>
    double GuestsFairBonus => GuestPerkFair * _gLevel[GLondon];

    // ---------- шана ----------

    public static int GuestLevelOf(int points)
    {
        var level = 0;
        for (var i = 1; i < GuestRepLevels.Length; i++)
            if (points >= GuestRepLevels[i]) level = i;
        return level;
    }

    static int GuestIndex(string key) => Array.FindIndex(Guests, g => g.Key == key);

    static ClickerGuest GuestOf(string key) => Guests.FirstOrDefault(g => g.Key == key) ?? Guests[0];

    int GuestPoints(string key) => _gRep.TryGetValue(key, out var n) ? n : 0;

    int GuestLevel(string key) => GuestIndex(key) is var i and >= 0 ? _gLevel[i] : 0;

    void GuestsRecount()
    {
        _gLevelSum = 0;
        for (var i = 0; i < Guests.Length; i++)
        {
            _gLevel[i] = GuestLevelOf(GuestPoints(Guests[i].Key));
            _gLevelSum += _gLevel[i];
        }
    }

    /// <summary>Додати шану гостеві. Повертає, на скільки рівнів вона виросла; ачівки — на переході через поріг.</summary>
    int GuestRepAdd(string key, int delta)
    {
        if (GuestIndex(key) < 0 || delta <= 0) return 0;
        var before = GuestLevel(key);
        // Виставкова зала Толоки й «Прадідова печатка» (v11) множать шану.
        delta = (int)Math.Ceiling(delta * TolokaGuestRepMult * (1 + Relic("seal2") / 2));
        _gRep[key] = (int)Math.Min(1_000_000L, (long)GuestPoints(key) + delta);
        GuestsRecount();
        var after = GuestLevel(key);
        if (before < GuestRepTop && after >= GuestRepTop) Achieve(GuestAchMax);
        if (before < GuestRepAllFrom && after >= GuestRepAllFrom && _gLevel.All(l => l >= GuestRepAllFrom)) Achieve(GuestAchAll);
        return after - before;
    }

    // ---------- двір ----------

    /// <summary>Скільки замовлень уміщає двір: по місцю на гостя, але не більше трьох; «Заморська карта» — ще одне.</summary>
    int GuestSlots => Math.Min(_gMet.Count, GuestSlotsMax) + (Has("seamap") ? GuestSeamapSlots : 0) + (_gMet.Count > 0 ? TolokaGuestSlots : 0);

    TimeSpan GuestGap() => TimeSpan.FromMinutes(
        (GuestGapMinMinutes + Ctx.Rng.NextDouble() * (GuestGapMaxMinutes - GuestGapMinMinutes)) * (Has("seamap") ? GuestSeamapGap : 1));

    static Func<ItemInfo, bool> GuestMatch(GuestOrderRow o) =>
        it => it.Ware == o.Ware && it.Quality >= o.Quality && (o.Style.Length == 0 || it.Style == o.Style);

    static string GuestWho(GuestOrderRow o)
    {
        var g = GuestOf(o.Guest);
        return g.People[Math.Clamp(o.Who, 0, g.People.Length - 1)];
    }

    /// <summary>Шана за замовлення: 4 + 2 за кожен виріб + 2 за кожен ступінь якості понад добрий + 3 за розпис.</summary>
    static int GuestRepFor(GuestOrderRow o) => Math.Max(1, 4 + 2 * o.Count + 2 * (o.Quality - 2) + (o.Style.Length > 0 ? 3 : 0));

    /// <summary>У скільки разів гість платить понад ціну виробу: ×8 (гончарі світу — ×12) і +10 % за рівень його шани.</summary>
    double GuestPayMult(GuestOrderRow o) => GuestOf(o.Guest).PayMult * (1 + GuestPayPerLevel * GuestLevel(o.Guest)) * (1 + Relic("seal2"));

    /// <summary>Скільки замовлення заплатить, якщо віддати рівно те, що просять (у виді — як обіцянка).</summary>
    double GuestPay(GuestOrderRow o) => Math.Max(1, ToPots(ItemValue(o.Ware, o.Style, o.Quality) * o.Count * GuestPayMult(o)));

    /// <summary>
    /// Хто прийде з наступним замовленням. Спершу ті, чийого замовлення на дворі ще нема; серед них — зважено на користь
    /// меншої шани (вага — 11 мінус рівень): новенький гість приходить в одинадцять разів частіше за того, чия шана вже на
    /// вершині, тож «усі шість на п'ятому» не впирається в одного забутого гостя.
    /// </summary>
    ClickerGuest GuestPick()
    {
        var met = Guests.Where(g => _gMet.ContainsKey(g.Key)).ToList();
        var free = met.Where(g => _gOrders.All(o => o.Guest != g.Key)).ToList();
        var pool = free.Count > 0 ? free : met;
        var weights = pool.Select(g => Math.Max(1, GuestRepLevels.Length - GuestLevel(g.Key))).ToList();
        var roll = Ctx.Rng.Next(weights.Sum());
        for (var i = 0; i < pool.Count; i++)
        {
            roll -= weights[i];
            if (roll < 0) return pool[i];
        }
        return pool[^1];
    }

    /// <summary>
    /// Нове замовлення: виріб — з улюблених, що вже відкриті (жодного — будь-який відкритий), кількість 1–3 (діаспора
    /// 2–4), мінімальна якість — своя в кожного гостя, розпис — лише з куплених гравцем (паризьким 40 %, решті 15 %).
    /// </summary>
    GuestOrderRow GuestsAddOrder(DateTimeOffset now, ClickerGuest? who = null)
    {
        var g = who ?? GuestPick();
        var open = Wares.Where(w => WareOpen(w.Key)).ToList();
        if (open.Count == 0) open.Add(Wares[0]);
        var liked = g.Likes.Length == 0 ? open : open.Where(w => g.Likes.Contains(w.Key)).ToList();
        if (liked.Count == 0) liked = open;
        var ware = liked[Ctx.Rng.Next(liked.Count)];
        var count = g.CountMin + Ctx.Rng.Next(g.CountMax - g.CountMin + 1);
        var styles = _styles.Order(StringComparer.Ordinal).ToList();
        var style = styles.Count > 0 && Ctx.Rng.Next(100) < g.StylePct ? styles[Ctx.Rng.Next(styles.Count)] : "";
        var person = Ctx.Rng.Next(g.People.Length);
        var life = TimeSpan.FromMinutes(GuestLifeMinMinutes + Ctx.Rng.Next(GuestLifeMaxMinutes - GuestLifeMinMinutes + 1));
        var order = new GuestOrderRow(++_gOrderId, g.Key, person, ware.Key, style, g.Quality, count, now, now + life);
        _gOrders.Add(order);
        return order;
    }

    /// <summary>
    /// Прибуття: перший рівень свого щабля — і гість на дворі назавжди. Прибулий одразу приносить замовлення, якщо на
    /// дворі є місце (корабель зайшов не просто так). Повертає, чи з'явилось нове замовлення.
    /// </summary>
    bool GuestsArrive(DateTimeOffset now)
    {
        if (_gMet.Count >= Guests.Length) return false;
        var added = false;
        foreach (var g in Guests)
        {
            if (_gMet.ContainsKey(g.Key) || Level(g.Tier) <= 0) continue;
            _gMet[g.Key] = now;
            AwayNote(g.Arrive);
            Wonder("guests");
            if (!added && _gOrders.Count < GuestSlots)
            {
                GuestsAddOrder(now, g);
                added = true;
            }
        }
        // Перший гість заводить розклад: далі нові замовлення — кожні 20–40 хв.
        if (_gMet.Count > 0 && _gNext == default) _gNext = now + GuestGap();
        return added;
    }

    // ---------- гачки життя партії ----------

    void ResetGuests(DateTimeOffset now)
    {
        _gMet.Clear();
        _gRep.Clear();
        _gOrders.Clear();
        _gOrderId = 0;
        _gNext = default;
        _gDelivered = 0;
        GuestsRecount();
    }

    /// <summary>
    /// Той самий оплачений проміжок, що й у пасиву: прибуття, прострочені замовлення й розклад. Розклад не накопичується:
    /// за один проміжок — не більше одного нового замовлення, а наступне — від «зараз». Тож хто зник на добу, застає на
    /// дворі одного свіжого гостя, а не чергу з восьми (так само дошка сіл після повернення).
    /// </summary>
    void SyncGuests(DateTimeOffset now, TimeSpan paid)
    {
        var expired = _gOrders.RemoveAll(o => o.Until <= now);
        if (expired > 0)
            AwayNote(expired == 1
                ? "🏛 Один гість не дочекався свого замовлення — відплив додому"
                : $"🏛 {expired} {Plural(expired, "гість", "гості", "гостей")} не дочекались своїх замовлень — відпливли додому");
        var added = GuestsArrive(now);
        if (_gMet.Count == 0 || now < _gNext) return;
        if (!added && _gOrders.Count < GuestSlots)
        {
            var o = GuestsAddOrder(now);
            AwayNote($"🏛 {GuestOf(o.Guest).Emoji} {GuestWho(o)} чекає в Гостинному дворі: {WareOf(o.Ware)!.Name.ToLowerInvariant()} ×{o.Count}");
        }
        _gNext = now + GuestGap();
    }

    /// <summary>Дія «guests»: <c>{ do: "give", id }</c> — віддати замовлення з комори, <c>{ do: "skip", id }</c> — відпустити гостя.</summary>
    ActResult? ActGuests(string action, JsonElement payload)
    {
        if (action != "guests") return null;
        var now = Ctx.Clock.UtcNow;
        return Str(payload, "do") switch
        {
            "give" => GuestsGive(payload, now),
            "skip" => GuestsSkip(payload, now),
            _ => ActResult.Fail("У Гостинному дворі так не торгуються"),
        };
    }

    ActResult GuestsGive(JsonElement payload, DateTimeOffset now)
    {
        var id = Num(payload, "id") ?? -1;
        var i = _gOrders.FindIndex(o => o.Id == id);
        if (i < 0 || _gOrders[i].Until <= now) return ActResult.Fail("Цей гість уже відплив додому");
        var o = _gOrders[i];
        var match = GuestMatch(o);
        var have = ItemCount(match);
        if (have < o.Count) return ActResult.Fail(GuestsLack(o, have));

        // Ціна — від того, що справді піде: TakeItems бере спершу найгіршу придатну якість, тож рахуємо в тому самому
        // порядку. Хто віддав дзвінкий замість доброго, отримує й за дзвінкий.
        double sum = 0;
        var left = o.Count;
        foreach (var (item, n) in AllItems().Where(x => match(x.Item)).OrderBy(x => x.Item.Quality).ToList())
        {
            var take = Math.Min(n, left);
            sum += ItemValue(item.Ware, item.Style, item.Quality) * take;
            left -= take;
            if (left <= 0) break;
        }
        TakeItems(match, o.Count);
        var pay = Math.Max(1, ToPots(sum * GuestPayMult(o)));
        Add(pay);
        _gOrders.RemoveAt(i);
        _gDelivered++;
        if (_gDelivered == 1) Achieve(GuestAchFirst);
        var g = GuestOf(o.Guest);
        var rep = GuestRepFor(o);
        var up = GuestRepAdd(g.Key, rep);
        var text = $"{g.Emoji} {GuestWho(o)}: «{g.Lines[Ctx.Rng.Next(g.Lines.Length)]}» +{PotsShort(pay)} · шана +{rep}";
        if (up > 0) text += $" · ⭐ {g.Name}: шана {GuestLevel(g.Key)}";
        return ActResult.Accept(text);
    }

    ActResult GuestsSkip(JsonElement payload, DateTimeOffset now)
    {
        var id = Num(payload, "id") ?? -1;
        var i = _gOrders.FindIndex(o => o.Id == id);
        if (i < 0 || _gOrders[i].Until <= now) return ActResult.Fail("Цього гостя вже нема на дворі");
        var o = _gOrders[i];
        _gOrders.RemoveAt(i);
        return ActResult.Accept($"{GuestOf(o.Guest).Emoji} {GuestWho(o)}: «Іншим разом, то й іншим разом!» — місце на дворі вільне");
    }

    /// <summary>Рід виробу для «добрий чи кращий» / «добра чи краща» / «добре чи краще».</summary>
    static readonly Dictionary<string, char> GuestWareGender = new(StringComparer.Ordinal)
    {
        ["bowl"] = 'f', ["makitra"] = 'f', ["tile"] = 'f', ["tykva"] = 'f', ["barrel"] = 'n',
    };

    /// <summary>«дзвінкий чи кращий», «лише розкішна» — якість, якої просять, узгоджена з родом виробу.</summary>
    static string GuestQualityNeed(string ware, int quality)
    {
        var gender = GuestWareGender.GetValueOrDefault(ware, 'm');
        string[] words = gender switch
        {
            'f' => ["", "звичайна", "добра", "дзвінка", "розкішна"],
            'n' => ["", "звичайне", "добре", "дзвінке", "розкішне"],
            _ => ["", "звичайний", "добрий", "дзвінкий", "розкішний"],
        };
        var better = gender switch { 'f' => "краща", 'n' => "краще", _ => "кращий" };
        var q = Math.Clamp(quality, 1, QualityMax);
        return q <= 1 ? "будь-якої якості" : q >= QualityMax ? $"лише {words[q]}" : $"{words[q]} чи {better}";
    }

    /// <summary>«Треба ще 2 × куманець (дзвінкий чи кращий, «Косівська») — є 1 з 3».</summary>
    static string GuestsLack(GuestOrderRow o, int have)
    {
        var style = o.Style.Length > 0 ? $", «{Styles.FirstOrDefault(s => s.Key == o.Style)?.Name ?? o.Style}»" : "";
        return $"Треба ще {o.Count - have} × {WareOf(o.Ware)!.Name.ToLowerInvariant()} ({GuestQualityNeed(o.Ware, o.Quality)}{style})"
            + $" — є {have} з {o.Count}";
    }

    /// <summary>Вид «guests»: null, поки гостей нема. Лише стан — тексти гостей їдуть у каталог.</summary>
    object? ViewGuests(DateTimeOffset now)
    {
        if (_gMet.Count == 0) return null;
        return new
        {
            list = Guests.Where(g => _gMet.ContainsKey(g.Key)).Select(g =>
            {
                var level = GuestLevel(g.Key);
                var pts = GuestPoints(g.Key);
                return new
                {
                    key = g.Key, level, pts,
                    // Скільки очок до наступного рівня (0 — вище нема куди).
                    need = level >= GuestRepLevels.Length - 1 ? 0 : GuestRepLevels[level + 1] - pts,
                    at = _gMet[g.Key],
                };
            }),
            orders = _gOrders.Select(o => new
            {
                id = o.Id, guest = o.Guest, who = o.Who, ware = o.Ware, style = o.Style, q = o.Quality, n = o.Count,
                have = ItemCount(GuestMatch(o)), pay = GuestPay(o), mult = GuestPayMult(o), rep = GuestRepFor(o), until = o.Until,
            }),
            next = _gNext,
            slots = GuestSlots,
            allMult = GuestsAllMult,
            delivered = _gDelivered,
            // «Заморська карта» (секрет третього кола): клієнт так і пише, звідки четверте місце.
            map = Has("seamap"),
        };
    }

    /// <summary>Тексти гостей (хто, звідки, що любить, пільги, репліки) — у каталог, а не щопачки.</summary>
    object? CatalogGuests() => new
    {
        list = Guests.Select(g => new
        {
            key = g.Key, tier = g.Tier, tierName = Shop.FirstOrDefault(u => u.Key == g.Tier)?.Name ?? g.Tier,
            emoji = g.Emoji, name = g.Name, from = g.From, people = g.People, likes = g.Likes,
            likesText = g.Likes.Length == 0
                ? "будь-що відкрите"
                : string.Join(", ", g.Likes.Select(k => WareOf(k)?.Name.ToLowerInvariant() ?? k)),
            quality = g.Quality, perk = g.Perk, step = g.Step, kind = g.Kind, per = g.Per, arrive = g.Arrive, lines = g.Lines,
            countMin = g.CountMin, countMax = g.CountMax, pay = g.PayMult,
        }),
        levels = GuestRepLevels,
        repAll = GuestRepAll,
        payPerLevel = GuestPayPerLevel,
        slotsMax = GuestSlotsMax,
        gap = new[] { GuestGapMinMinutes, GuestGapMaxMinutes },
        life = new[] { GuestLifeMinMinutes / 60, GuestLifeMaxMinutes / 60 },
    };

    GuestsRow? SaveGuests() => _gMet.Count == 0 && _gRep.Count == 0 && _gOrders.Count == 0 && _gDelivered == 0
        ? null
        : new(new Dictionary<string, DateTimeOffset>(_gMet, StringComparer.Ordinal), new Dictionary<string, int>(_gRep, StringComparer.Ordinal),
            _gOrders.ToList(), _gOrderId, _gNext, _gDelivered);

    /// <summary>
    /// Збереження гостей. Чого не зрозуміли (невідомий гість, виріб, розпис, зіпсована якість) — відкидаємо рядок, а не
    /// всю партію. Час прибуття з майбутнього (збитий годинник, правлена база) — «зараз».
    /// </summary>
    void LoadGuests(GuestsRow? row)
    {
        var now = Ctx.Clock.UtcNow;
        ResetGuests(now);
        if (row is null) return;
        foreach (var (key, at) in row.Met ?? [])
            if (key is not null && GuestIndex(key) >= 0) _gMet[key] = at == default || at > now ? now : at;
        foreach (var (key, n) in row.Rep ?? [])
            if (key is not null && n > 0 && GuestIndex(key) >= 0) _gRep[key] = Math.Min(n, 1_000_000);
        GuestsRecount();
        foreach (var o in row.Orders ?? [])
        {
            if (o is null || o.Id <= 0 || o.Guest is null || !_gMet.ContainsKey(o.Guest) || o.Ware is null || WareOf(o.Ware) is null
                || o.Style is null || (o.Style.Length > 0 && Styles.All(s => s.Key != o.Style))
                || o.Quality is < 1 or > QualityMax || o.Count is < 1 or > 10 || o.Until <= o.At
                || _gOrders.Count >= GuestSlotsMax + GuestSeamapSlots + TolokaGuestSlotsMax + 2)
                continue;
            _gOrders.Add(o with { Who = Math.Clamp(o.Who, 0, GuestOf(o.Guest).People.Length - 1) });
        }
        _gOrderId = Math.Max(Math.Max(0, row.OrderId), _gOrders.Count == 0 ? 0 : _gOrders.Max(o => o.Id));
        _gDelivered = Math.Max(0, row.Delivered);
        _gNext = _gMet.Count == 0 ? default
            : row.Next == default || row.Next > now + TimeSpan.FromMinutes(GuestGapMaxMinutes) ? now + GuestGap()
            : row.Next;
    }

    /// <summary>
    /// Обпал гостей не проганяє: шана й замовлення лишаються (контракт §7). Комору обпал спалює — вироби на замовлення
    /// доведеться виліпити наново, але гості почекають.
    /// </summary>
    void FireGuests(DateTimeOffset now) { }
}
