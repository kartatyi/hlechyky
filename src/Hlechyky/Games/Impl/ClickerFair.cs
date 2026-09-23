using System.Globalization;
using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Гончарне село: де його шанують, чим воно пільгує і хто звідти приходить із замовленнями.</summary>
/// <param name="From">«з Опішні» — для підпису замовника.</param>
/// <param name="In">«в Опішні» — для «шана в селі».</param>
/// <param name="Style">Розпис, який це село любить найбільше (порожній — Сорочинцям годиться будь-який).</param>
/// <param name="Step">Що додає саме наступна зірка — одним рядком на картку села (v9).</param>
public sealed record FairVillage(string Key, string Name, string From, string In, string Emoji, string Style, string Perk, string[] People, string Step = "");

/// <summary>Гість біля вікна чи дверей: хто, що приніс і як часто заходить (вага в жеребку).</summary>
public sealed record FairGuest(string Key, string Name, string Emoji, string Desc, int Weight);

/// <summary>
/// Один наслідок події: <c>pots</c> — секунди пасиву (мінус — втрата, але не більше десятої частини глеків);
/// <c>work</c>/<c>dry</c>/<c>value</c> — тимчасовий множник ліплення, сушіння чи ціни на <paramref name="Seconds"/>;
/// <c>rep</c> — шана в селі (<paramref name="Village"/>, порожнє — у випадковому); <c>item</c> — виріб у комору;
/// <c>take</c> — виріб із комори; <c>guest</c> — гість от-от завітає.
/// </summary>
public sealed record FairFx(string Kind, double Amount = 0, int Seconds = 0, string Village = "");

/// <summary>Чим скінчився вибір: текст і наслідки.</summary>
public sealed record FairOutcome(string Text, FairFx[] Fx);

/// <summary>Варіант події. Два наслідки — жереб (кидається, коли подія з'являється: ворожка його бачить).</summary>
public sealed record FairChoice(string Label, FairOutcome[] Outcomes);

/// <summary>Подія з вибором: картка з двома варіантами.</summary>
public sealed record FairEvent(string Key, string Emoji, string Title, string Text, FairChoice A, FairChoice B);

/// <summary>Свято чи ярмарок за календарем: на що попит.</summary>
public sealed record FairHoliday(string Key, string Name, string Emoji, string Desc);

/// <summary>День за київським календарем: погода, пора року, свято, вихідний.</summary>
public sealed record FairDay(string Day, string Weather, string Season, string? Holiday, bool Weekend);

/// <summary>
/// Ярмарок і люди (пакет B4, docs/games/specs/clicker-v7-fair.md): замовлення з вимогами й шана сіл, торг, гості
/// біля вікна, події з вибором, пори року, свята й погода. Правила — лише тут і лише за <c>Ctx.Clock</c>/<c>Ctx.Rng</c>;
/// основа кличе гачки з §2.7 контракту. Дошка купців-інвесторів (<c>take</c>, ClickerHouse.cs) — окрема й лишається як є.
///
/// Чому так: вироби з горна мусять кудись іти не лише на базар. Замовлення платять ×2–2,75 від ціни виробу
/// (<see cref="ItemValue"/>), але з'являються раз на 6–10 хв і просять конкретне — тож беруть лише частину виробів,
/// і разом із базаром та горном активна гра лишається в межах контракту (~2–3 пасиви зверху). Шана сіл — довга мета
/// на тижні: кожен рівень +1 % до всього й пільга свого села.
/// </summary>
public sealed partial class Clicker
{
    // ---------- числа ----------

    /// <summary>Скільки замовлень на дошці: після повернення — не менше двох, за розкладом — до чотирьох (пан — понад).</summary>
    public const int FairBoardMin = 2, FairBoardMax = 4;
    public const int FairOrderGapMinSeconds = 360, FairOrderGapMaxSeconds = 600;
    public const int FairOrderLifeMinMinutes = 25, FairOrderLifeMaxMinutes = 40, FairLordLifeMinutes = 60;
    /// <summary>Оплата замовлення від ціни виробу: ×2, +0,25 за якість від доброї, ще +0,25 за дзвінку, +0,25 за розпис. Пан — ×4.</summary>
    public const double FairOrderMult = 2, FairOrderStep = 0.25, FairLordMult = 4;
    /// <summary>Торг: поступитись — −15 % і вдвічі більше шани; накинути — +30 %, якщо купець погодиться.</summary>
    public const double FairDownPay = 0.85, FairUpPay = 1.3, FairUpChance = 0.45, FairUpPerLevel = 0.07, FairUpMax = 0.9;
    /// <summary>
    /// Шана: пороги рівнів 0–10 (v9). Перші шість — ті самі, що були, тож наявні очки нікуди не діваються:
    /// у кого назбиралось 300, той одразу на шостому рівні, а не «втратив прогрес».
    /// </summary>
    public static readonly int[] FairRepLevels = [0, 8, 25, 60, 120, 220, 380, 620, 1000, 1600, 2500];
    /// <summary>Кожен рівень шани в будь-якому селі — +3 % до всього (v9: було +1 %, і шану ніхто не помічав).</summary>
    public const double FairRepAll = 0.03;
    /// <summary>Шана села додає до множника замовлення: +0,1 за рівень, а панові — +0,2.</summary>
    public const double FairPayPerLevel = 0.1, FairLordPayPerLevel = 0.2;
    /// <summary>Пільги сіл за рівень: робота Опішні, ціна Бубнівки й Гавареччини, сушіння Василькова, розпис Косова, гості й торг Сорочинців.</summary>
    public const double FairPerkWork = 0.04, FairPerkValue = 0.05, FairPerkDry = 0.04, FairPerkStyle = 0.04, FairPerkGuests = 0.05, FairPerkHaggle = 0.03;
    /// <summary>Рівень шани для ачівок: п'ятий — «Свій на ярмарку» (як було), десятий — «Шана на всю округу».</summary>
    public const int FairRepAch = 5, FairRepTop = 10;

    /// <summary>З якого рівня село шле гостинці й що саме в них: хвилини пасиву за рівень, в'язки соломи.</summary>
    public const int FairGiftFrom = 6, FairGiftStraw = 5;
    public const double FairGiftMinutes = 5;
    /// <summary>Скільки гостинець висить у стрічці (далі його видно лише лічильником на картці села).</summary>
    public static readonly TimeSpan FairGiftShown = TimeSpan.FromMinutes(10);

    /// <summary>Ярмарковий дзвін (секрет <c>bell</c>): замовлень на дошці до п'яти, нове — кожні 4–7 хв.</summary>
    public const int FairBellBoardMax = 5, FairBellGapMinSeconds = 240, FairBellGapMaxSeconds = 420;

    /// <summary>Базарний день: раз на 3–6 год десять хвилин усі замовлення платять у півтора раза більше.</summary>
    public const int FairBazaarMinSeconds = 3 * 3600, FairBazaarMaxSeconds = 6 * 3600;
    public static readonly TimeSpan FairBazaarFor = TimeSpan.FromMinutes(10);
    public const double FairBazaarMult = 1.5;

    /// <summary>Нові гості: дяк додає красу наступному розпису, мандрівний гончар — обпалені до майстерності, ведмідь грає навпіл.</summary>
    public const int FairDyakBeauty = 20, FairWanderFired = 25;
    public const double FairBearPay = 2, FairBearLoss = 0.05;

    public const int FairGuestMinSeconds = 8 * 60, FairGuestMaxSeconds = 20 * 60;
    public static readonly TimeSpan FairGuestShown = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan FairSaltFor = TimeSpan.FromSeconds(60);
    public const double FairSaltMult = 0.5;
    /// <summary>Сорока: три хвилини пасиву (на голому колі — шістдесят кліків), не менше 25 глеків.</summary>
    public const double FairMagpieSeconds = 180;
    public const int FairMagpieClicks = 60, FairMagpieFloor = 25;
    public static readonly TimeSpan FairSongFor = TimeSpan.FromSeconds(120);
    public const double FairSongMult = 1.25;
    public const int FairGuestsForAchievement = 20;

    public const int FairEventMinSeconds = 30 * 60, FairEventMaxSeconds = 60 * 60;
    /// <summary>Подія чекає гончаря не довше за десять хвилин.</summary>
    public static readonly TimeSpan FairEventWait = TimeSpan.FromMinutes(10);
    /// <summary>Межі наслідків: втрата — не більше десятої частини глеків і трьох хвилин пасиву; виграш — до п'яти хвилин.</summary>
    /// <summary>Стеля календарного попиту разом із бафом ціни (без пільг сіл).</summary>
    public const double FairCalendarCap = 2;
    public const double FairEventLoss = 0.1, FairEventPotsMin = -180, FairEventPotsMax = 300;
    public const int FairBuffMaxSeconds = 300;

    public const double FairDrySun = 0.7, FairDryCloud = 1, FairDryRain = 1.5, FairDryFrost = 2;
    public const double FairWeekend = 1.2, FairChristmas = 2, FairEaster = 1.5, FairPokrova = 2, FairSorochyntsi = 1.5, FairSorochyntsiGuests = 0.6;

    // ---------- каталоги ----------

    public static readonly FairVillage[] FairVillages =
    [
        new("opishnia", "Опішня", "з Опішні", "в Опішні", "🏺", "opishnia",
            "Глечики й куманці ліпляться швидше: −4 % роботи за рівень",
            ["Баба Одарка", "Дід Панас", "Кума Параска", "Шинкар Мусій", "Гончар Юхим"],
            "глечики й куманці ще −4 % роботи"),
        new("kosiv", "Косів", "з Косова", "у Косові", "⛰", "kosiv",
            "Замовлення з розписом платять +4 % за рівень",
            ["Ґазда Василь", "Ґаздиня Марічка", "Вівчар Юра", "Бабця Олена", "Різьбяр Дмитро"],
            "замовлення з розписом ще +4 %"),
        new("vasylkiv", "Васильків", "з Василькова", "у Василькові", "🌼", "vasylkiv",
            "Сирці сохнуть швидше: −4 % часу за рівень",
            ["Писар Никифор", "Швачка Ганна", "Пекарка Люба", "Коваль Остап", "Вчителька Ніна"],
            "сирці сохнуть ще −4 % часу"),
        new("bubnivka", "Бубнівка", "з Бубнівки", "у Бубнівці", "🌀", "bubnivka",
            "Миски й полумиски дорожчі: +5 % ціни за рівень",
            ["Тітка Христя", "Мельник Семен", "Молодиця Оксана", "Дід Трохим", "Пасічник Гнат"],
            "миски й полумиски ще +5 % ціни"),
        new("gavarets", "Гавареччина", "з Гавареччини", "у Гавареччині", "⚫", "gavarets",
            "Горщики, макітри й барила дорожчі: +5 % ціни за рівень",
            ["Майстер Степан", "Ґаздиня Ірина", "Корчмар Лесь", "Бабця Настя", "Дударик Тарас"],
            "горщики, макітри й барила ще +5 % ціни"),
        new("sorochyntsi", "Сорочинці", "із Сорочинців", "у Сорочинцях", "🎪", "",
            "Гості заходять частіше (−5 % чекання) і «накинути» вдається частіше (+3 %) — за рівень",
            ["Солопій Черевик", "Хівря", "Параска", "Парубок Грицько", "Кум Цибуля"],
            "гості на 5 % швидше, «накинути» +3 %"),
    ];

    public static readonly FairGuest[] FairGuests =
    [
        new("chumak", "Чумак із сіллю", "🐂", "Пригостив кримською сіллю: ліплення вдвічі швидше 60 с", 25),
        new("magpie", "Сорока з монетою", "🐦", "Упустила на поріг блискучу монету: ~3 хв пасиву", 25),
        new("lord", "Пан із маєтку", "🎩", "Особливе замовлення ×4 на дошці", 15),
        new("kobzar", "Кобзар", "🎶", "Пісня під вікном: 2 хв вироби на базарі й у замовленнях +25 %", 20),
        new("fortune", "Ворожка з ярмарку", "🔮", "Нагадала долю: наслідки наступної події буде видно заздалегідь", 15),
        // v9: більше людей на подвір'ї. Ваги менші за старих — старі гості мусять лишитись найчастішими.
        new("dyak", "Дяк із книгою", "📖", "Показав візерунки зі старої книги: наступний розпис +20 краси", 12),
        new("wander", "Мандрівний гончар", "🧭", "Постояв за колом і показав хват: одному виробу +25 обпалених до майстерності", 10),
        new("bear", "Поводир із ведмедем", "🐻", "Ведмідь танцює: або наступне замовлення ×2, або перекине полицю", 10),
    ];

    public static readonly FairHoliday[] FairHolidays =
    [
        new("christmas", "Святвечір і Різдво", "🎄", "Макітри для куті й маку — ціна ×2"),
        new("easter", "Великдень", "🥚", "Миски й полумиски для кошиків — ціна ×1,5"),
        new("pokrova", "Покрова й весілля", "💐", "Весільна пора: куманці ×2"),
        new("sorochyntsi", "Сорочинський ярмарок", "🎪", "Усі вироби ×1,5, гості заходять частіше"),
    ];

    static FairOutcome FxOut(string text, params FairFx[] fx) => new(text, fx);
    static FairChoice FxPick(string label, params FairOutcome[] outcomes) => new(label, outcomes);
    static FairFx FxPots(double seconds) => new("pots", seconds);
    static FairFx FxWork(double mult, int seconds) => new("work", mult, seconds);
    static FairFx FxDry(double mult, int seconds) => new("dry", mult, seconds);
    static FairFx FxPrice(double mult, int seconds) => new("value", mult, seconds);
    static FairFx FxRep(int points, string village = "") => new("rep", points, 0, village);

    /// <summary>Двадцять одна пригода. Наслідки навмисно дрібні: кілька хвилин пасиву, короткі бафи, трохи шани.</summary>
    public static readonly FairEvent[] FairEvents =
    [
        new("rain-pit", "🌧", "Злива розмила глинище", "Копати зараз по коліна в болоті чи чекати, поки підсохне?",
            FxPick("Копати в болоті", FxOut("Накопав голубої глини — ліпиться, як масло: робота −25 % на 4 хв", FxWork(0.75, 240))),
            FxPick("Чекати на печі", FxOut("Виспався, а сирці тим часом підсохли під вітром: сушіння −30 % на 5 хв", FxDry(0.7, 300)))),
        new("kum-makitra", "🍯", "Кум просить макітру на весілля", "«Позич, куме, макітру — на весіллі без неї ніяк». Позичити?",
            FxPick("Позичити",
                FxOut("Кум повернув макітру з пирогами й розхвалив тебе на все село: шана +3", FxRep(3)),
                FxOut("Макітру на весіллі розбили «на щастя». Зате пам'ятають: шана +2, з комори зник один виріб", FxRep(2), new("take", 1))),
            FxPick("Відмовити", FxOut("Кум надувся, а ти за той час виліпив ще: робота −15 % на 3 хв", FxWork(0.85, 180)))),
        new("cat-shelf", "🐈", "Кіт скинув полицю", "Гуркіт, черепки, кіт на шафі робить вигляд, що це не він.",
            FxPick("Ганятися за котом", FxOut("Кота не впіймав, зате розігрівся: робота −20 % на 2 хв", FxWork(0.8, 120))),
            FxPick("Позбирати черепки",
                FxOut("Серед черепків знайшлась загублена монета: +2 хв пасиву", FxPots(120)),
                FxOut("Черепки підмів, нову полицю збив — трохи глеків на цвяхи", FxPots(-60)))),
        new("chumaky", "🐂", "Чумаки кличуть у дорогу", "Обоз іде на Крим по сіль. Передати з ними слово про свої глеки чи сидіти біля кола?",
            FxPick("Передати слово", FxOut("Чумаки хвалили тебе на кожному привалі: шана +3 у Сорочинцях", FxRep(3, "sorochyntsi"))),
            FxPick("Лишитися біля кола", FxOut("Поки інші базікали, ти ліпив: робота −20 % на 3 хв", FxWork(0.8, 180)))),
        new("wedding", "💐", "Сусіди кличуть на весілля", "Гуляють три дні. Піти чи послати дарунок?",
            FxPick("Піти гуляти", FxOut("Погуляв і вернувся з гостинцями: +4 хв пасиву; руки ще тиждень пританцьовують — робота +15 % на 2 хв", FxPots(240), FxWork(1.15, 120))),
            FxPick("Послати виріб у дарунок", FxOut("Молоді в захваті від дарунка: шана +3, з комори поїхав один виріб", FxRep(3), new("take", 1)))),
        new("cold-night", "❄", "Холодна ніч", "Сирці на сушарні мерзнуть. Топити піч до ранку чи накрити рядном?",
            FxPick("Топити піч", FxOut("У теплі сирці висохли рівно: сушіння −40 % на 5 хв, але дрова — не задарма", FxDry(0.6, 300), FxPots(-45))),
            FxPick("Накрити рядном", FxOut("Рядно вберегло, сохне довше: сушіння +20 % на 3 хв; зате дрова цілі — +1 хв пасиву", FxDry(1.2, 180), FxPots(60)))),
        new("scribe", "📜", "Писар із волості рахує гончарів", "Ходить із книгою й чорнильницею. Показати вироби чи сховатися?",
            FxPick("Показати все",
                FxOut("Писар замилувався й записав тебе в книгу майстрів: шана +3", FxRep(3)),
                FxOut("Писар узяв один виріб «на пробу» і забув повернути. Хоч записав: шана +1", FxRep(1), new("take", 1))),
            FxPick("Сховатися на горищі", FxOut("Пересидів і підслухав, як сусіди хвалять твої глеки: +2 хв пасиву", FxPots(120)))),
        new("goat", "🐐", "Сусідська коза в майстерні", "Жує рядно й дивиться просто в душу.",
            FxPick("Вигнати", FxOut("Коза пішла, а сусід на радощах приніс сиру: +2 хв пасиву", FxPots(120))),
            FxPick("Подоїти й лишити", FxOut("Молоко смачне, коза спокійна, гончар — теж: робота −10 % на 5 хв", FxWork(0.9, 300)))),
        new("fair-trip", "🎪", "До Сорочинців на ярмарок?", "Кажуть, цього року там пів повіту. Їхати з возом чи лишитися?",
            FxPick("Поїхати з возом", FxOut("Наторгувався й навчився торгуватися: ціни +20 % на 3 хв", FxPrice(1.2, 180))),
            FxPick("Лишитися вдома", FxOut("Ярмарок сам прийшов до тебе: зараз хтось завітає", new FairFx("guest")))),
        new("storm", "⛈", "Гроза над хатою", "Блискавка вдарила в грушу під вікном. Рятувати грушу чи перечекати в льоху?",
            FxPick("Перечекати в льоху", FxOut("Отямився — а коло крутилося саме: +3 хв пасиву", FxPots(180))),
            FxPick("Рятувати грушу", FxOut("Грушу врятував, тепер сушиш груші поруч із сирцями: сушіння −25 % на 4 хв", FxDry(0.75, 240)))),
        new("sleepy", "😴", "Підмайстер проспав", "Сонце вже високо, а він хропе на лаві.",
            FxPick("Будити відром води",
                FxOut("Прокинувся й ліпить, як навіжений: робота −25 % на 3 хв", FxWork(0.75, 180)),
                FxOut("Образився й пів ранку дувся: робота +15 % на 2 хв", FxWork(1.15, 120))),
            FxPick("Хай спить", FxOut("Виспаний підмайстер — добрий підмайстер: +2 хв пасиву", FxPots(120)))),
        new("clay-seller", "🧺", "Мандрівний торговець глиною", "«Найкраща глина в світі, пане майстре, лише для вас!»",
            FxPick("Купити жменю",
                FxOut("Глина справді добра: робота −30 % на 3 хв (і трохи глеків торговцеві)", FxWork(0.7, 180), FxPots(-60)),
                FxOut("Звичайнісінька глина з сусіднього яру. Трохи глеків — за науку", FxPots(-90))),
            FxPick("Прогнати", FxOut("Торговець пішов до сусіда, а ти зберіг глеки й гідність: +1 хв пасиву", FxPots(60)))),
        new("kids", "🧒", "Діти просять навчити ліпити", "Стоять під тином, носи в глині, очі горять.",
            FxPick("Навчити", FxOut("Разом виліпили виріб — лежить у коморі; батьки дякують: шана +2", new FairFx("item", 1), FxRep(2))),
            FxPick("Відправити гратися", FxOut("Тиша й спокій: робота −15 % на 3 хв", FxWork(0.85, 180)))),
        new("blessing", "🔔", "Батюшка кличе посвятити майстерню", "Іде з кропилом повз хату. Покликати?",
            FxPick("Посвятити", FxOut("Майстерня посвячена, руки легкі: робота й сушіння −10 % на 5 хв", FxWork(0.9, 300), FxDry(0.9, 300))),
            FxPick("Згодом, роботи повно", FxOut("Встиг виліпити ще кілька: +2 хв пасиву", FxPots(120)))),
        new("geese", "🦆", "Гуси обсіли сушарню", "Ціла зграя з річки, і ватажок сичить.",
            FxPick("Прогнати хворостиною", FxOut("Гуси втекли, сирці цілі, ти засапався: +1 хв пасиву", FxPots(60))),
            FxPick("Нагодувати й подружитися", FxOut("Тепер гуси стережуть подвір'я й гукають гостей: зараз хтось завітає", new FairFx("guest")))),
        new("kobzar-night", "🎶", "Кобзар проситься переночувати", "Під вечір стукає старий із кобзою.",
            FxPick("Прийняти", FxOut("Співав до ранку, про твої глеки теж: ціни +25 % на 2 хв", FxPrice(1.25, 120))),
            FxPick("Нема місця", FxOut("Виспався як слід: робота −15 % на 3 хв", FxWork(0.85, 180)))),
        new("rivalry", "⚔", "Суперечка майстрів", "Косівські й опішнянські гончарі сперечаються, чиї вироби кращі. Кличуть тебе суддею.",
            FxPick("Стати за Опішню", FxOut("Опішня тобі вдячна: шана +3 в Опішні; Косів трохи насупився", FxRep(3, "opishnia"), FxRep(-1, "kosiv"))),
            FxPick("Стати за Косів", FxOut("Косів тобі вдячний: шана +3 у Косові; Опішня трохи насупилась", FxRep(3, "kosiv"), FxRep(-1, "opishnia")))),
        new("zigzag", "🌀", "Бабця з Бубнівки показує «кривульки»", "Малює пензликом хвилі так швидко, що аж в очах мерехтить.",
            FxPick("Повчитися", FxOut("Рука набила ритм: робота −20 % на 3 хв і шана +1 у Бубнівці", FxWork(0.8, 180), FxRep(1, "bubnivka"))),
            FxPick("Пригостити узваром", FxOut("Бабця розказала всім, що в тебе гостинно: шана +3 у Бубнівці", FxRep(3, "bubnivka")))),
        new("heat", "☀", "Спека така, що глина тріскається", "Сирці на сонці аж дзвенять. Що робити?",
            FxPick("Поливати сирці", FxOut("Сирці вціліли, а ти змок до нитки: +2 хв пасиву", FxPots(120))),
            FxPick("Сушити в тіні під грушею", FxOut("У тіні сохнуть рівно й швидко: сушіння −30 % на 4 хв", FxDry(0.7, 240)))),
        new("borsch", "🍲", "Кличуть обідати", "Борщ на столі, а виріб на колі ще мокрий.",
            FxPick("Спершу доліпити", FxOut("Борщ вистиг, зате виріб вийшов: робота −20 % на 2 хв", FxWork(0.8, 120))),
            FxPick("Бігти до борщу",
                FxOut("Ситий гончар ліпить краще: робота −30 % на 2 хв", FxWork(0.7, 120)),
                FxOut("Після пампушок потягнуло на сон, а коло крутилося саме: +1 хв пасиву", FxPots(60)))),
        new("rustle", "🌙", "Хтось шарудить біля комори", "Ніч, місяць, і щось тупцяє під дверима.",
            FxPick("Вийти з ціпком", FxOut("То був їжак. Тепер він живе під ґанком і ловить мишей: +2 хв пасиву", FxPots(120))),
            FxPick("Гукнути собаку", FxOut("Собака прогнав… кота. Кіт образився й скинув з полиці один виріб", new FairFx("take", 1)))),
        // v9: ще шість пригод — у тому самому стилі й у тих самих межах наслідків.
        new("bridge", "🌉", "Міст через річку підмило", "За мостом — глинище, а об'їзд на пів дня. Кидати кладку чи возити в об'їзд?",
            FxPick("Кинути кладку",
                FxOut("Кладка вийшла міцна, сусіди носять по ній глину й тобі: робота −20 % на 4 хв", FxWork(0.8, 240)),
                FxOut("Кладку зробив, чоботи втопив. Зате глина є: робота −15 % на 3 хв", FxWork(0.85, 180), FxPots(-45))),
            FxPick("Возити в об'їзд", FxOut("Дорогою наслухався базарних цін і підняв свої: ціни +20 % на 3 хв", FxPrice(1.2, 180)))),
        new("fiddler", "🎻", "Скрипаль на вигоні розучує нову", "Грає те саме коліно вже годину. Кортить послухати.",
            FxPick("Послухати з тину", FxOut("Під ритм коло крутиться саме собою: робота −25 % на 3 хв", FxWork(0.75, 180))),
            FxPick("Кликати до хати",
                FxOut("Грав цілий вечір, а вранці все село прийшло дивитись, у кого він ночував: шана +3", FxRep(3)),
                FxOut("Грав цілий вечір і з'їв усю вечерю. Зате весело: +1 хв пасиву", FxPots(60)))),
        new("horseshoe", "🧲", "Підкова в глині", "Копнув — дзенькнуло. У грудці глини стара підкова.",
            FxPick("Прибити над дверима", FxOut("Над дверима — на щастя, і щастя вже пішло: +3 хв пасиву", FxPots(180))),
            FxPick("Однести ковалеві", FxOut("Коваль викував гачок для кола й подякував на все село: шана +2 і робота −10 % на 4 хв", FxRep(2), FxWork(0.9, 240)))),
        new("neighbour-kiln", "🧱", "Сусід кличе мурувати горно", "Своє горно в нього тріснуло. Просить руки на день.",
            FxPick("Піти помогти",
                FxOut("Змурували вдвох, а він віддячив в'язанкою хмизу й добрим словом: шана +3", FxRep(3)),
                FxOut("Мурували до ночі, спину не розігнути. Сусід удячний: шана +2", FxRep(2), FxWork(1.1, 120))),
            FxPick("Показати, як мурувати", FxOut("Пояснив на пальцях, сам тим часом ліпив: +2 хв пасиву", FxPots(120)))),
        new("apprentice-girl", "👧", "Дівчина з сусіднього села проситься в науку", "Каже, що бачила твої глеки на ярмарку й відтоді спати не може.",
            FxPick("Узяти в науку", FxOut("Учениця вже крутить коло — разом виліпили виріб, і про тебе говорять: шана +2", new FairFx("item", 1), FxRep(2))),
            FxPick("Порадити свого майстра", FxOut("Відправив до кума в Опішню — там теж запам'ятали, хто порадив: шана +3 в Опішні", FxRep(3, "opishnia")))),
        new("fog", "🌫", "Туман сів на село", "З хати не видно тину. Пекти в такому — все одно що наосліп.",
            FxPick("Перечекати біля кола", FxOut("У тиші й тумані наліпив більше, ніж за пів дня: +3 хв пасиву", FxPots(180))),
            FxPick("Винести сирці на вітер",
                FxOut("На горбі вітер розігнав туман, сирці підсохли: сушіння −30 % на 4 хв", FxDry(0.7, 240)),
                FxOut("Туман осів росою просто на сирцях: сушіння +30 % на 3 хв", FxDry(1.3, 180)))),
    ];

    static readonly string[] FairDownLines = ["Оце по-сусідськи!", "Дай тобі Боже здоров'я!", "Ну ти й добра душа!", "Усім розкажу, який ти щедрий!"];
    static readonly string[] FairAsLines = ["Добрий товар — добра ціна", "По руках!", "Як домовлялись, так і плачу", "Беру, не торгуюсь"];
    static readonly string[] FairUpLines = ["Ех, здираєш… але варто!", "Та бери вже, бо таких ніде нема", "Ну й торгуєшся ти, як на ярмарку!", "За таку роботу не шкода"];
    static readonly string[] FairNoLines = ["Та це ж грабунок серед білого дня!", "За такі гроші я сам виліплю!", "Ти що, з Царграда ціни привіз?", "Ой, не смішіть мої чоботи!"];

    // ---------- стан ----------

    sealed record FairOrderRow(int Id, string Village, int Who, string Ware, string Style, int Quality, int Count, double Mult,
        DateTimeOffset At, DateTimeOffset Until, bool Lord = false, bool Sour = false);

    sealed record FairGuestRow(string Kind, DateTimeOffset At, DateTimeOffset Until, int X, int Y);

    sealed record FairEventRow(int Id, string Key, DateTimeOffset At, DateTimeOffset Until, int RollA, int RollB, bool Foresight);

    sealed record FairBuffRow(string Kind, double Mult, DateTimeOffset Until, string Src);

    /// <summary>Гостинець із села: що прислали й коли (щоб рядок устиг побути в стрічці).</summary>
    sealed record FairGiftRow(string Village, string Text, DateTimeOffset At);

    /// <summary>Збереження пакета: усе необов'язкове — старе збереження читається як «ярмарку ще не було».</summary>
    sealed record FairRow(
        List<FairOrderRow>? Orders = null, int OrderId = 0, DateTimeOffset OrderNext = default,
        Dictionary<string, int>? Rep = null, FairGuestRow? Guest = null, int Guests = 0,
        FairEventRow? Event = null, DateTimeOffset EventAt = default, int EventId = 0, bool Foresight = false,
        List<FairBuffRow>? Buffs = null, int Delivered = 0,
        // v9 «Село»: гостинці, дяк із книгою, ведмідь і базарний день. Усе нове — необов'язкове.
        string? GiftDay = null, Dictionary<string, int>? Gifts = null, List<FairGiftRow>? GiftLog = null,
        int Beauty = 0, bool Bear = false, DateTimeOffset BazaarUntil = default, DateTimeOffset BazaarNext = default);

    readonly List<FairOrderRow> _mktOrders = [];
    int _mktOrderId;
    DateTimeOffset _mktOrderNext;
    readonly Dictionary<string, int> _mktRep = new(StringComparer.Ordinal);
    /// <summary>Сума рівнів шани всіх сіл — множник до всього питають дуже часто, тож тримаємо готовою.</summary>
    int _mktRepLevels;
    FairGuestRow _mktGuest = new("", default, default, 0, 0);
    int _mktGuests;
    FairEventRow? _mktEvent;
    DateTimeOffset _mktEventAt;
    int _mktEventId;
    bool _mktForesight;
    readonly List<FairBuffRow> _mktBuffs = [];
    int _mktDelivered;
    FairDay? _mktDay;
    long _mktDayMinute = -1;
    /// <summary>Київський день, за який гостинці вже роздано: «раз на день» — це перша синхронізація нового дня.</summary>
    string _mktGiftDay = "";
    /// <summary>Скільки гостинців прийшло з кожного села — на картці села.</summary>
    readonly Dictionary<string, int> _mktGifts = new(StringComparer.Ordinal);
    readonly List<FairGiftRow> _mktGiftLog = [];
    /// <summary>Дяк із книгою: скільки краси він додасть наступному розпису (0 — дяка не було).</summary>
    int _mktBeauty;
    /// <summary>Ведмідь витанцював удачу: наступне здане замовлення платить удвічі.</summary>
    bool _mktBear;
    DateTimeOffset _mktBazaarUntil, _mktBazaarNext;

    // ---------- календар ----------

    /// <summary>
    /// Православний Великдень: алгоритм Меєуса для юліанського календаря плюс 13 днів (вірно для 1900–2099).
    /// 2027 → 2 травня.
    /// </summary>
    public static DateOnly OrthodoxEaster(int year)
    {
        var a = year % 4;
        var b = year % 7;
        var c = year % 19;
        var d = (19 * c + 15) % 30;
        var e = (2 * a + 4 * b - d + 34) % 7;
        var month = (d + e + 114) / 31;
        var day = (d + e + 114) % 31 + 1;
        return new DateOnly(year, month, day).AddDays(13);
    }

    /// <summary>
    /// Свято дня: Святвечір і Різдво — 24 грудня…7 січня; Великдень — тиждень до й після; Сорочинський ярмарок —
    /// передостанній тиждень серпня (18–24: останній — 25–31); Покрова й весілля — 14 жовтня…30 листопада.
    /// </summary>
    public static string? FairHolidayOf(DateOnly d)
    {
        if ((d.Month == 12 && d.Day >= 24) || (d.Month == 1 && d.Day <= 7)) return "christmas";
        if (Math.Abs(d.DayNumber - OrthodoxEaster(d.Year).DayNumber) <= 7) return "easter";
        if (d.Month == 8 && d.Day is >= 18 and <= 24) return "sorochyntsi";
        if ((d.Month == 10 && d.Day >= 14) || d.Month == 11) return "pokrova";
        return null;
    }

    /// <summary>
    /// День за Києвом. Погода — детермінована від <c>Days.Seed("clicker-weather", день)</c>: у теплі місяці (квітень–жовтень)
    /// сонце 40 %, хмарно 35 %, дощ 25 %; у холодні — сонце 20 %, хмарно 30 %, дощ 20 %, мороз 30 %.
    /// </summary>
    public static FairDay FairCalendar(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, Days.Kyiv);
        var date = DateOnly.FromDateTime(local.DateTime);
        var day = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var m = date.Month;
        var r = Days.Seed("clicker-weather", day) % 100;
        var weather = m is 11 or 12 or 1 or 2 or 3
            ? (r < 20 ? "sun" : r < 50 ? "cloud" : r < 70 ? "rain" : "frost")
            : (r < 40 ? "sun" : r < 75 ? "cloud" : "rain");
        var season = m switch { 12 or 1 or 2 => "winter", 3 or 4 or 5 => "spring", 6 or 7 or 8 => "summer", _ => "autumn" };
        return new FairDay(day, weather, season, FairHolidayOf(date), date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
    }

    /// <summary>Попит свята на виріб.</summary>
    public static double FairDemand(string? holiday, string ware) => holiday switch
    {
        "christmas" => ware == "makitra" ? FairChristmas : 1,
        "easter" => ware is "bowl" or "dish" ? FairEaster : 1,
        "pokrova" => ware == "kumanets" ? FairPokrova : 1,
        "sorochyntsi" => FairSorochyntsi,
        _ => 1,
    };

    public static double FairWeatherDry(string weather) => weather switch
    {
        "sun" => FairDrySun, "rain" => FairDryRain, "frost" => FairDryFrost, _ => FairDryCloud,
    };

    /// <summary>Календар на цю хвилину: ціну виробу питають десятки разів на вид, а день міняється раз на добу.</summary>
    FairDay MktToday
    {
        get
        {
            var now = Ctx.Clock.UtcNow;
            var minute = now.UtcTicks / TimeSpan.TicksPerMinute;
            if (_mktDay is null || minute != _mktDayMinute)
            {
                _mktDay = FairCalendar(now);
                _mktDayMinute = minute;
            }
            return _mktDay;
        }
    }

    // ---------- шана ----------

    public static int FairLevelOf(int points)
    {
        var level = 0;
        for (var i = 1; i < FairRepLevels.Length; i++)
            if (points >= FairRepLevels[i]) level = i;
        return level;
    }

    int MktPoints(string village) => _mktRep.TryGetValue(village, out var n) ? n : 0;

    int MktLevel(string village) => FairLevelOf(MktPoints(village));

    void MktRecountLevels() => _mktRepLevels = FairVillages.Sum(v => MktLevel(v.Key));

    /// <summary>Додати шану. Мінус ніколи не опускає нижче порогу вже здобутого рівня — образа, а не вигнання з села.</summary>
    int MktRepAdd(string village, int delta)
    {
        if (FairVillages.All(v => v.Key != village) || delta == 0) return 0;
        var before = MktPoints(village);
        var level = FairLevelOf(before);
        var after = delta > 0 ? before + delta : Math.Max(FairRepLevels[level], before + delta);
        _mktRep[village] = Math.Max(0, after);
        var now = FairLevelOf(_mktRep[village]);
        MktRecountLevels();
        if (level < FairRepAch && now >= FairRepAch) Achieve("potter-rep");
        if (level < FairRepTop && now >= FairRepTop)
        {
            Achieve("potter-rep10");
            // Дивовижа — лише на першому селі, що дійшло до десятої зірки (друге вже не диво, а звичка).
            if (FairVillages.Count(v => MktLevel(v.Key) >= FairRepTop) == 1) Wonder("rep-10");
        }
        return now - level;
    }

    // ---------- гачки основи ----------

    double FairAllMult => 1 + FairRepAll * _mktRepLevels;

    /// <summary>Чи триває якийсь баф ярмарку (сіль, пісня, наслідок пригоди): Око майстра під ним не перебиває (v9).</summary>
    internal bool FairBuffOn(DateTimeOffset now) => _mktBuffs.Any(b => b.Until > now);

    double MktBuff(string kind)
    {
        var now = Ctx.Clock.UtcNow;
        var m = 1.0;
        foreach (var b in _mktBuffs)
            if (b.Kind == kind && b.Until > now) m *= b.Mult;
        return m;
    }

    double FairWorkMult(string ware)
    {
        var m = ware is "jug" or "kumanets" ? 1 - FairPerkWork * MktLevel("opishnia") : 1;
        return Math.Clamp(m * MktBuff("work"), MinWorkShare, 2);
    }

    double FairValueMult(string ware)
    {
        var d = MktToday;
        // Свято × вихідні × кобзар разом — не більше ×2 (свято саме по собі вже ×2): інакше Різдво у вихідний із кобзарем
        // давало ×3,6 до ціни, а з замовленнями — вдесятеро понад ціль. Пільга села — окремо, це заслужене тижнями.
        var m = Math.Min(FairCalendarCap, FairDemand(d.Holiday, ware) * (d.Weekend ? FairWeekend : 1) * Math.Clamp(MktBuff("value"), 1, 1.5));
        if (ware is "bowl" or "dish") m *= 1 + FairPerkValue * MktLevel("bubnivka");
        if (ware is "pot" or "makitra" or "barrel") m *= 1 + FairPerkValue * MktLevel("gavarets");
        return m;
    }

    double FairDryMult() =>
        Math.Clamp(FairWeatherDry(MktToday.Weather) * (1 - FairPerkDry * MktLevel("vasylkiv")) * MktBuff("dry"), 0.3, 3);

    // ---------- замовлення ----------

    static Func<ItemInfo, bool> MktMatch(FairOrderRow o) =>
        it => it.Ware == o.Ware && it.Quality >= o.Quality && (o.Style.Length == 0 || it.Style == o.Style);

    static FairVillage MktVillage(string key) => FairVillages.FirstOrDefault(v => v.Key == key) ?? FairVillages[0];

    static string MktWho(FairOrderRow o)
    {
        var v = MktVillage(o.Village);
        return o.Lord || o.Who < 0 ? "Пан із маєтку" : v.People[Math.Clamp(o.Who, 0, v.People.Length - 1)];
    }

    /// <summary>Складність від прогресу: скільки виробів уже відкрито.</summary>
    int MktDifficulty(int open) => open <= 2 ? 0 : open <= 4 ? 1 : open <= 7 ? 2 : 3;

    /// <summary>Косів доплачує за розпис.</summary>
    double MktStylePerk(FairOrderRow o) => o.Style.Length > 0 ? 1 + FairPerkStyle * MktLevel("kosiv") : 1;

    /// <summary>
    /// Шана села додає до множника замовлення (v9): +0,1 за рівень, а панові — +0,2. На десятій зірці звичайне
    /// замовлення платить ×3…×3,75 замість ×2…×2,75, панське — ×6. Це і є «шана має вагу».
    /// </summary>
    double MktRepPay(FairOrderRow o) => (o.Lord ? FairLordPayPerLevel : FairPayPerLevel) * MktLevel(o.Village);

    /// <summary>Базарний день: десять хвилин, коли всі замовлення платять у півтора раза більше.</summary>
    bool MktBazaarOn(DateTimeOffset now) => now < _mktBazaarUntil;

    /// <summary>Повний множник замовлення: базовий плюс шана села, ×Косів за розпис, ×базарний день.</summary>
    double MktMult(FairOrderRow o) =>
        (o.Mult + MktRepPay(o)) * MktStylePerk(o) * (MktBazaarOn(Ctx.Clock.UtcNow) ? FairBazaarMult : 1);

    double MktChance(FairOrderRow o) =>
        Math.Min(FairUpMax, FairUpChance + FairUpPerLevel * MktLevel(o.Village) + FairPerkHaggle * MktLevel("sorochyntsi"));

    static int MktRepFor(FairOrderRow o) => (2 + o.Count + 2 * (o.Quality - 1) + (o.Style.Length > 0 ? 2 : 0)) * (o.Lord ? 2 : 1);

    /// <summary>Скільки замовлення заплатить «як є», якщо здати рівно те, що просять (у виді — як обіцянка).</summary>
    double MktPay(FairOrderRow o) =>
        Math.Max(1, ToPots(ItemValue(o.Ware, o.Style, o.Quality) * o.Count * MktMult(o)));

    void MktAddOrder(DateTimeOffset now, bool lord = false)
    {
        var open = Wares.Where(w => WareOpen(w.Key)).ToList();
        if (open.Count == 0) open.Add(Wares[0]);
        var d = MktDifficulty(open.Count);
        var village = FairVillages[Ctx.Rng.Next(FairVillages.Length)];
        // Частіше просять нове: один із п'яти (пан — трьох) останніх відкритих виробів.
        var back = Math.Min(open.Count, lord ? 3 : 5);
        var ware = open[open.Count - 1 - Ctx.Rng.Next(back)];
        var styles = _styles.Order(StringComparer.Ordinal).ToList();
        var style = "";
        var roll = Ctx.Rng.Next(100);
        if (village.Style.Length > 0 && _styles.Contains(village.Style) && roll < 40) style = village.Style;
        else if (styles.Count > 0 && roll >= (lord ? 50 : 80)) style = styles[Ctx.Rng.Next(styles.Count)];
        var q = Ctx.Rng.Next(100);
        int quality = lord
            ? (q < 30 ? 3 : 2)
            : d switch
            {
                0 => 1,
                1 => q < 30 ? 2 : 1,
                2 => q < 10 ? 3 : q < 50 ? 2 : 1,
                _ => q < 20 ? 3 : q < 70 ? 2 : 1,
            };
        var count = lord ? 3 + Ctx.Rng.Next(3) : 2 + Ctx.Rng.Next(2 + d);
        if (quality == 3) count = Math.Max(lord ? 2 : 1, (count + 1) / 2);
        var mult = lord ? FairLordMult : FairOrderMult + FairOrderStep * ((quality >= 2 ? 1 : 0) + (quality == 3 ? 1 : 0) + (style.Length > 0 ? 1 : 0));
        var life = lord
            ? TimeSpan.FromMinutes(FairLordLifeMinutes)
            : TimeSpan.FromMinutes(FairOrderLifeMinMinutes + Ctx.Rng.Next(FairOrderLifeMaxMinutes - FairOrderLifeMinMinutes + 1));
        var who = lord ? -1 : Ctx.Rng.Next(village.People.Length);
        _mktOrders.Add(new(++_mktOrderId, village.Key, who, ware.Key, style, quality, count, mult, now, now + life, lord));
    }

    /// <summary>Скільки замовлень уміщає дошка: з ярмарковим дзвоном — п'ять замість чотирьох.</summary>
    int MktBoardMax => Has("bell") ? FairBellBoardMax : FairBoardMax;

    TimeSpan MktOrderGap()
    {
        var (min, max) = Has("bell")
            ? (FairBellGapMinSeconds, FairBellGapMaxSeconds)
            : (FairOrderGapMinSeconds, FairOrderGapMaxSeconds);
        return TimeSpan.FromSeconds(min + Ctx.Rng.NextDouble() * (max - min));
    }

    ActResult MktDeliver(JsonElement payload, DateTimeOffset now)
    {
        var id = Num(payload, "id") ?? -1;
        var i = _mktOrders.FindIndex(o => o.Id == id);
        if (i < 0 || _mktOrders[i].Until <= now) return ActResult.Fail("Цей замовник уже не чекає — поїхав додому");
        var o = _mktOrders[i];
        var bid = Str(payload, "bid");
        if (bid.Length == 0) bid = "as";
        if (bid is not ("down" or "as" or "up")) return ActResult.Fail("Так на ярмарку не торгуються");
        if (bid == "up" && o.Sour) return ActResult.Fail("Замовник уже ображений — удруге накидати не вийде");
        var match = MktMatch(o);
        var have = ItemCount(match);
        var w = WareOf(o.Ware)!;
        if (have < o.Count) return ActResult.Fail($"Бракує: є {have} з {o.Count} — {w.Name.ToLowerInvariant()}");
        var who = MktWho(o);
        var village = MktVillage(o.Village);

        if (bid == "up" && Ctx.Rng.NextDouble() >= MktChance(o))
        {
            // Відмова не болить: вироби лишаються в коморі, замовлення чекає далі (як є чи поступитись), шани — мінус один.
            _mktOrders[i] = o with { Sour = true };
            MktRepAdd(o.Village, -1);
            return ActResult.Accept($"😤 {who} {village.From}: «{FairNoLines[Ctx.Rng.Next(FairNoLines.Length)]}» — торг не вдався, але замовлення ще чекає");
        }

        // Ціна — від того, що справді піде: TakeItems бере спершу найгіршу придатну якість, тож рахуємо в тому самому порядку.
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
        var haggle = bid switch { "down" => FairDownPay, "up" => FairUpPay, _ => 1 };
        // Ведмідь витанцював удачу — і згорає на першому ж замовленні, скільки б його не берегли.
        var bear = _mktBear;
        _mktBear = false;
        var pay = Math.Max(1, ToPots(sum * MktMult(o) * haggle * (bear ? FairBearPay : 1)));
        Add(pay);
        _mktOrders.RemoveAt(i);
        _mktDelivered++;
        if (o.Lord) Wonder("lord-order");
        var rep = MktRepFor(o) * (bid == "down" ? 2 : 1);
        var up = MktRepAdd(o.Village, rep);
        var (emoji, lines) = bid switch { "down" => ("🥰", FairDownLines), "up" => ("💰", FairUpLines), _ => ("🤝", FairAsLines) };
        var text = $"{emoji} {who} {village.From}: «{lines[Ctx.Rng.Next(lines.Length)]}» +{Short(pay)} {Pots(pay)} · шана +{rep}";
        if (bear) text += " · 🐻 ведмідь наворожив подвійну плату";
        if (MktBazaarOn(now)) text += " · 🛒 базарний день";
        if (up > 0) text += $" · ⭐ {village.Name}: шана {MktLevel(o.Village)}";
        return ActResult.Accept(text);
    }

    // ---------- гості ----------

    void MktScheduleGuest(DateTimeOffset from)
    {
        var wait = FairGuestMinSeconds + Ctx.Rng.NextDouble() * (FairGuestMaxSeconds - FairGuestMinSeconds);
        wait *= (MktToday.Holiday == "sorochyntsi" ? FairSorochyntsiGuests : 1) * (1 - FairPerkGuests * MktLevel("sorochyntsi"));
        var at = from + TimeSpan.FromSeconds(wait);
        var roll = Ctx.Rng.Next(FairGuests.Sum(g => g.Weight));
        var kind = FairGuests[^1].Key;
        foreach (var g in FairGuests)
        {
            if (roll < g.Weight) { kind = g.Key; break; }
            roll -= g.Weight;
        }
        // Біля вікна хати чи біля хвіртки в тину — лівий верхній кут фігурки у відсотках сцени (жива хата, 360×450):
        // вікно x 226…280 / y 190…236, хвіртка x 96…120 / y 100…128; фігурка ~70 px, тож кут — трохи лівіше й вище.
        var window = Ctx.Rng.Next(2) == 0;
        var x = window ? Ctx.Rng.Next(58, 65) : Ctx.Rng.Next(20, 26);
        var y = window ? Ctx.Rng.Next(35, 41) : Ctx.Rng.Next(11, 17);
        _mktGuest = new FairGuestRow(kind, at, at + FairGuestShown, x, y);
    }

    double MktMagpie() =>
        Math.Max(FairMagpieFloor, ToPots(Math.Max(PassiveBase * FairMagpieSeconds, ClickBase * FairMagpieClicks)));

    void MktBuffAdd(string kind, double mult, TimeSpan span, string src, DateTimeOffset now)
    {
        _mktBuffs.RemoveAll(b => b.Kind == kind && b.Src == src);
        _mktBuffs.Add(new FairBuffRow(kind, mult, now + span, src));
    }

    ActResult MktGuestCatch(DateTimeOffset now)
    {
        var g = _mktGuest;
        if (g.Kind.Length == 0 || now < g.At - EarlyGrace || now > g.Until + CatchGrace)
            return ActResult.Fail("Гість уже пішов далі селом");
        // Гість ловиться скриптом так само легко, як розписний глек: те саме Око майстра.
        if (GuardGate(now) is { } gate) return gate;
        string text;
        switch (g.Kind)
        {
            case "chumak":
                MktBuffAdd("work", FairSaltMult, FairSaltFor, "chumak", now);
                text = "🐂 Чумак пригостив сіллю: ліплення вдвічі швидше 60 с";
                break;
            case "lord" when _mktOrders.All(o => !o.Lord):
                MktAddOrder(now, lord: true);
                text = "🎩 Пан із маєтку лишив на дошці особливе замовлення ×4";
                break;
            case "kobzar":
                MktBuffAdd("value", FairSongMult, FairSongFor, "kobzar", now);
                text = "🎶 Кобзар заспівав під вікном: 2 хв вироби на базарі й у замовленнях +25 %";
                break;
            case "fortune":
                _mktForesight = true;
                text = "🔮 Ворожка глянула на долоню: наслідки наступної пригоди буде видно заздалегідь";
                break;
            case "dyak":
                _mktBeauty = FairDyakBeauty;
                text = $"📖 Дяк розгорнув книгу з візерунками: наступний розпис +{FairDyakBeauty} краси";
                break;
            case "wander":
            {
                // Хват показують на тому, що гончар уже пік: майстерність росте, а нову клітинку альбому
                // гість за гончаря не відкриває.
                var known = Wares.Where(w => WareOpen(w.Key) && FiredOf(w.Key) > 0).ToList();
                if (known.Count == 0) known = Wares.Where(w => WareOpen(w.Key)).ToList();
                if (known.Count == 0) known = [Wares[0]];
                var w2 = known[Ctx.Rng.Next(known.Count)];
                AddFired(new ItemInfo(w2.Key, "", 1), FairWanderFired);
                text = $"🧭 Мандрівний гончар став за коло: {w2.Name.ToLowerInvariant()} — ще {FairWanderFired} обпалених до майстерності";
                break;
            }
            case "bear":
                if (Ctx.Rng.Next(2) == 0)
                {
                    _mktBear = true;
                    text = "🐻 Ведмідь витанцював удачу: наступне замовлення заплатить удвічі";
                }
                else
                {
                    var loss = ToPots(_pots * FairBearLoss);
                    if (loss > 0) _pots -= loss;
                    text = loss > 0
                        ? $"🐻 Ведмідь перекинув полицю: −{Short(loss)} {Pots(loss)}"
                        : "🐻 Ведмідь перекинув полицю — добре, що на ній нічого не стояло";
                }
                break;
            default:
                // Сорока — і пан, чиє замовлення вже на дошці: той лишає на чай.
                var gain = MktMagpie();
                Add(gain);
                text = g.Kind == "lord"
                    ? $"🎩 Пан ще чекає свого замовлення — лишив на чай +{Short(gain)} {Pots(gain)}"
                    : $"🐦 Сорока впустила монету: +{Short(gain)} {Pots(gain)}";
                break;
        }
        _mktGuests++;
        GuardSpend(ClickerGuard.CatchWeight);
        if (_mktGuests == FairGuestsForAchievement) Achieve("potter-guest");
        // Гість на свято — рідкісний збіг: у такі дні дивовижі й трапляються.
        if (MktToday.Holiday is not null) Wonder("holiday-guest");
        MktScheduleGuest(now);
        return ActResult.Accept(text);
    }

    // ---------- гостинці з сіл (v9) ----------

    /// <summary>
    /// Дяк із книгою: скільки краси він додає наступному розпису. Забирається один раз — горно кличе це рівно
    /// там, де рахує красу (див. звіт, §правки чужих файлів).
    /// </summary>
    public int FairTakeBeauty()
    {
        var n = _mktBeauty;
        _mktBeauty = 0;
        return n;
    }

    /// <summary>
    /// Гостинець із села, де шана вже ≥ 6: раз на київський день кожне таке село шле щось своє — гроші за поміч,
    /// солому в клуню або дзвінкий виріб у своєму розписі. Без гравця нічого не накопичується: за пропущені дні
    /// гостинець приходить один.
    /// </summary>
    void MktGifts(DateTimeOffset now)
    {
        foreach (var v in FairVillages)
        {
            var level = MktLevel(v.Key);
            if (level < FairGiftFrom) continue;
            string what;
            switch (Ctx.Rng.Next(3))
            {
                case 0:
                {
                    // П'ять хвилин пасиву за рівень; на голому колі (пасиву ще нема) — кліками, як у сороки.
                    var sec = FairGiftMinutes * 60 * level;
                    var gain = Math.Max(FairMagpieFloor, ToPots(Math.Max(PassiveBase * sec, ClickBase * sec / 5)));
                    Add(gain);
                    what = $"+{Short(gain)} {Pots(gain)} за поміч селу";
                    break;
                }
                case 1:
                    StrawAdd(FairGiftStraw);
                    what = $"{FairGiftStraw} {Plural(FairGiftStraw, "в'язка", "в'язки", "в'язок")} соломи в клуню";
                    break;
                default:
                {
                    var open = Wares.Where(w => WareOpen(w.Key)).ToList();
                    if (open.Count == 0) open = [Wares[0]];
                    var ware = open[open.Count - 1 - Ctx.Rng.Next(Math.Min(open.Count, 5))];
                    var over = PutItems(ware.Key, v.Style, 3, 1);
                    what = $"дзвінкий {ware.Name.ToLowerInvariant()}, {(v.Style.Length == 0 ? "простий" : StyleWord(v.Style))}"
                        + (over > 0 ? " (комора повна — одразу на базар)" : "");
                    break;
                }
            }
            var text = $"🎁 Гостинець {v.From}: {what}";
            _mktGifts[v.Key] = (_mktGifts.TryGetValue(v.Key, out var c) ? c : 0) + 1;
            _mktGiftLog.Add(new FairGiftRow(v.Key, text, now));
            AwayNote(text);
        }
        while (_mktGiftLog.Count > FairVillages.Length) _mktGiftLog.RemoveAt(0);
    }

    /// <summary>Базарний день: раз на 3–6 год десять хвилин усі замовлення платять у півтора раза більше.</summary>
    TimeSpan MktBazaarGap() =>
        TimeSpan.FromSeconds(FairBazaarMinSeconds + Ctx.Rng.NextDouble() * (FairBazaarMaxSeconds - FairBazaarMinSeconds));

    // ---------- події ----------

    TimeSpan MktEventGap() => TimeSpan.FromSeconds(FairEventMinSeconds + Ctx.Rng.NextDouble() * (FairEventMaxSeconds - FairEventMinSeconds));

    void MktNewEvent(DateTimeOffset now)
    {
        var last = _mktEvent?.Key;
        var e = FairEvents[Ctx.Rng.Next(FairEvents.Length)];
        if (e.Key == last) e = FairEvents[(Array.IndexOf(FairEvents, e) + 1) % FairEvents.Length];
        // Жереб кидаємо одразу: ворожка «бачить» саме те, що станеться.
        var a = Ctx.Rng.Next(e.A.Outcomes.Length);
        var b = Ctx.Rng.Next(e.B.Outcomes.Length);
        _mktEvent = new FairEventRow(++_mktEventId, e.Key, now, now + FairEventWait, a, b, _mktForesight);
        _mktForesight = false;
    }

    ActResult MktChoose(JsonElement payload, DateTimeOffset now)
    {
        if (_mktEvent is not { } e || now > e.Until || Num(payload, "id") != e.Id)
            return ActResult.Fail("Ця пригода вже минула");
        var pick = Num(payload, "pick");
        if (pick is not (0 or 1)) return ActResult.Fail("Обери один із двох варіантів");
        if (FairEvents.FirstOrDefault(x => x.Key == e.Key) is not { } ev) { _mktEvent = null; return ActResult.Fail("Ця пригода вже минула"); }
        var choice = pick == 0 ? ev.A : ev.B;
        var outcome = choice.Outcomes[Math.Clamp(pick == 0 ? e.RollA : e.RollB, 0, choice.Outcomes.Length - 1)];
        var extra = MktApply(outcome.Fx, now);
        _mktEvent = null;
        _mktEventAt = now + MktEventGap();
        return ActResult.Accept($"{ev.Emoji} {outcome.Text}{extra}");
    }

    /// <summary>Наслідки події — з жорсткими межами. Повертає дописку з точними числами («+1,2 тис глеків»).</summary>
    string MktApply(FairFx[] fx, DateTimeOffset now)
    {
        var notes = new List<string>();
        foreach (var f in fx)
        {
            switch (f.Kind)
            {
                case "pots":
                {
                    var sec = Math.Clamp(f.Amount, FairEventPotsMin, FairEventPotsMax);
                    if (sec > 0)
                    {
                        // На голому колі пасиву нема — тоді кліками: хвилина ≈ дванадцять.
                        var gain = Math.Max(5, ToPots(Math.Max(PassiveBase * sec, ClickBase * sec / 5)));
                        Add(gain);
                        notes.Add($"+{Short(gain)} {Pots(gain)}");
                    }
                    else if (sec < 0)
                    {
                        var loss = Math.Min(ToPots(PassiveBase * -sec), ToPots(_pots * FairEventLoss));
                        if (loss > 0)
                        {
                            _pots -= loss;
                            notes.Add($"−{Short(loss)} {Pots(loss)}");
                        }
                    }
                    break;
                }
                case "work":
                    MktBuffAdd("work", Math.Clamp(f.Amount, 0.5, 1.3), TimeSpan.FromSeconds(Math.Clamp(f.Seconds, 1, FairBuffMaxSeconds)), "event", now);
                    break;
                case "dry":
                    MktBuffAdd("dry", Math.Clamp(f.Amount, 0.5, 1.5), TimeSpan.FromSeconds(Math.Clamp(f.Seconds, 1, FairBuffMaxSeconds)), "event", now);
                    break;
                case "value":
                    MktBuffAdd("value", Math.Clamp(f.Amount, 1, 1.3), TimeSpan.FromSeconds(Math.Clamp(f.Seconds, 1, FairBuffMaxSeconds)), "event", now);
                    break;
                case "rep":
                {
                    var village = f.Village.Length > 0 ? f.Village : FairVillages[Ctx.Rng.Next(FairVillages.Length)].Key;
                    var delta = (int)Math.Clamp(f.Amount, -3, 3);
                    var up = MktRepAdd(village, delta);
                    if (f.Village.Length == 0 && delta > 0) notes.Add($"шана — {MktVillage(village).In}");
                    if (up > 0) notes.Add($"⭐ {MktVillage(village).Name}: шана {MktLevel(village)}");
                    break;
                }
                case "item":
                {
                    var ware = WareOpen(_formWare) ? _formWare : Wares[0].Key;
                    var n = (int)Math.Clamp(f.Amount, 1, 2);
                    var over = PutItems(ware, "", 1, n);
                    if (over > 0) notes.Add($"комора повна — на базар +{Short(over)}");
                    break;
                }
                case "take":
                    if (TakeItems(_ => true, (int)Math.Clamp(f.Amount, 0, 1)) == 0) notes.Add("але комора й так порожня");
                    break;
                case "guest":
                    if (_mktGuest.At > now + TimeSpan.FromSeconds(20))
                    {
                        MktScheduleGuest(now);
                        _mktGuest = _mktGuest with { At = now + TimeSpan.FromSeconds(5), Until = now + TimeSpan.FromSeconds(5) + FairGuestShown };
                    }
                    break;
            }
        }
        return notes.Count > 0 ? " (" + string.Join(", ", notes) + ")" : "";
    }

    // ---------- гачки ----------

    FairRow? SaveFair() => new(
        _mktOrders.ToList(), _mktOrderId, _mktOrderNext,
        new Dictionary<string, int>(_mktRep, StringComparer.Ordinal), _mktGuest, _mktGuests,
        _mktEvent, _mktEventAt, _mktEventId, _mktForesight, _mktBuffs.ToList(), _mktDelivered,
        _mktGiftDay, new Dictionary<string, int>(_mktGifts, StringComparer.Ordinal), _mktGiftLog.ToList(),
        _mktBeauty, _mktBear, _mktBazaarUntil, _mktBazaarNext);

    void LoadFair(FairRow? row)
    {
        var now = Ctx.Clock.UtcNow;
        if (row is null)
        {
            // Старе збереження: шани ще нема, дошка свіжа, гість і пригода — за розкладом від «зараз».
            ResetFair(now);
            return;
        }
        _mktOrders.Clear();
        foreach (var o in row.Orders ?? [])
            if (o is not null && o.Style is not null && WareOf(o.Ware) is not null && FairVillages.Any(v => v.Key == o.Village) && o.Count is > 0 and <= 20
                && o.Quality is >= 1 and <= 3 && (o.Style.Length == 0 || Styles.Any(s => s.Key == o.Style))
                && double.IsFinite(o.Mult) && o.Mult is > 0 and <= FairLordMult && _mktOrders.Count < FairBellBoardMax + 2)
                _mktOrders.Add(o);
        _mktOrderId = Math.Max(row.OrderId, _mktOrders.Count == 0 ? 0 : _mktOrders.Max(o => o.Id));
        _mktOrderNext = row.OrderNext;
        _mktRep.Clear();
        foreach (var (key, n) in row.Rep ?? [])
            if (n > 0 && FairVillages.Any(v => v.Key == key)) _mktRep[key] = Math.Min(n, 1_000_000);
        MktRecountLevels();
        _mktGuests = Math.Max(0, row.Guests);
        if (row.Guest is { } g && g.Until > g.At && FairGuests.Any(x => x.Key == g.Kind))
            _mktGuest = g with { X = Math.Clamp(g.X, 0, 80), Y = Math.Clamp(g.Y, 0, 80) };
        else
            MktScheduleGuest(now);
        _mktEvent = row.Event is { } e && FairEvents.Any(x => x.Key == e.Key) ? e : null;
        _mktEventId = Math.Max(row.EventId, _mktEvent?.Id ?? 0);
        _mktEventAt = row.EventAt == default ? now + MktEventGap() : row.EventAt;
        _mktForesight = row.Foresight;
        _mktBuffs.Clear();
        foreach (var b in row.Buffs ?? [])
            if (b is not null && b.Kind is "work" or "dry" or "value" && double.IsFinite(b.Mult) && b.Mult is > 0.3 and < 2
                && b.Until <= now + TimeSpan.FromSeconds(FairBuffMaxSeconds) && _mktBuffs.Count < 8)
                _mktBuffs.Add(b);
        _mktDelivered = Math.Max(0, row.Delivered);
        // v9. Старе збереження: гостинців ще не було — перша ж синхронізація віддасть те, що селу винне за сьогодні.
        _mktGiftDay = row.GiftDay is { Length: 10 } d && DateOnly.TryParse(d, out _) ? d : "";
        _mktGifts.Clear();
        foreach (var (key, n) in row.Gifts ?? [])
            if (n > 0 && FairVillages.Any(v => v.Key == key)) _mktGifts[key] = Math.Min(n, 1_000_000);
        _mktGiftLog.Clear();
        foreach (var gift in row.GiftLog ?? [])
            if (gift is not null && gift.Text is { Length: > 0 and <= 200 } && FairVillages.Any(v => v.Key == gift.Village)
                && gift.At <= now && _mktGiftLog.Count < FairVillages.Length)
                _mktGiftLog.Add(gift);
        _mktBeauty = Math.Clamp(row.Beauty, 0, FairDyakBeauty);
        _mktBear = row.Bear;
        _mktBazaarUntil = row.BazaarUntil > now + FairBazaarFor ? now + FairBazaarFor : row.BazaarUntil;
        _mktBazaarNext = row.BazaarNext == default || row.BazaarNext > now + TimeSpan.FromSeconds(FairBazaarMaxSeconds)
            ? now + MktBazaarGap()
            : row.BazaarNext;
    }

    void ResetFair(DateTimeOffset now)
    {
        _mktOrders.Clear();
        _mktOrderId = 0;
        _mktRep.Clear();
        _mktRepLevels = 0;
        _mktGuests = 0;
        _mktEvent = null;
        _mktEventId = 0;
        _mktForesight = false;
        _mktBuffs.Clear();
        _mktDelivered = 0;
        _mktDay = null;
        _mktGiftDay = "";
        _mktGifts.Clear();
        _mktGiftLog.Clear();
        _mktBeauty = 0;
        _mktBear = false;
        _mktBazaarUntil = default;
        _mktBazaarNext = now + MktBazaarGap();
        for (var i = 0; i < FairBoardMin; i++) MktAddOrder(now);
        _mktOrderNext = now + MktOrderGap();
        MktScheduleGuest(now);
        _mktEventAt = now + MktEventGap();
    }

    /// <summary>Обпал: замовлення в роботі згорають (нова дошка від нового прогресу). Шана, гості й пригоди лишаються.</summary>
    void FireFair(DateTimeOffset now)
    {
        _mktOrders.Clear();
        for (var i = 0; i < FairBoardMin; i++) MktAddOrder(now);
        _mktOrderNext = now + MktOrderGap();
    }

    void SyncFair(DateTimeOffset now, TimeSpan paid)
    {
        var expired = _mktOrders.RemoveAll(o => o.Until <= now);
        if (expired > 0) AwayNote($"📜 {expired} {Plural(expired, "замовлення", "замовлення", "замовлень")} не дочекалось — замовники поїхали");
        _mktBuffs.RemoveAll(b => b.Until <= now);
        // Повернувся після простою — на дошці вже хтось чекає; здане під час гри нове не з'являється миттєво.
        if (paid >= AwayFrom)
            while (_mktOrders.Count(o => !o.Lord) < FairBoardMin) MktAddOrder(now);
        if (now >= _mktOrderNext)
        {
            if (_mktOrders.Count(o => !o.Lord) < MktBoardMax) MktAddOrder(now);
            _mktOrderNext = now + MktOrderGap();
        }
        // Гостинці — перша синхронізація нового київського дня.
        _mktGiftLog.RemoveAll(g => now - g.At > FairGiftShown);
        var day = Days.Of(now);
        if (_mktGiftDay != day)
        {
            _mktGiftDay = day;
            MktGifts(now);
        }
        // Базарний день: коротко й зрідка, щоб лишався святом, а не тлом.
        if (now >= _mktBazaarNext)
        {
            _mktBazaarUntil = now + FairBazaarFor;
            _mktBazaarNext = now + MktBazaarGap();
            AwayNote("🛒 Базарний день: десять хвилин замовлення платили в півтора раза більше");
        }
        if (now > _mktGuest.Until + CatchGrace) MktScheduleGuest(now);
        if (_mktEvent is { } e && now > e.Until)
        {
            _mktEvent = null;
            _mktEventAt = now + MktEventGap();
        }
        if (_mktEvent is null && now >= _mktEventAt) MktNewEvent(now);
    }

    ActResult? ActFair(string action, JsonElement payload)
    {
        if (action != "fair") return null;
        var now = Ctx.Clock.UtcNow;
        return Str(payload, "op") switch
        {
            "deliver" => MktDeliver(payload, now),
            "guest" => MktGuestCatch(now),
            "choose" => MktChoose(payload, now),
            _ => ActResult.Fail("На ярмарку так не роблять"),
        };
    }

    object? ViewFair(DateTimeOffset now)
    {
        var d = MktToday;
        return new
        {
            weather = d.Weather,
            season = d.Season,
            holiday = d.Holiday is { } h ? new { key = h, name = FairHolidays.First(x => x.Key == h).Name } : null,
            weekend = d.Weekend,
            dry = FairDryMult(),
            orders = _mktOrders.Select(o => new
            {
                id = o.Id, village = o.Village, who = o.Who, ware = o.Ware, style = o.Style, q = o.Quality, n = o.Count,
                have = ItemCount(MktMatch(o)), until = o.Until, mult = MktMult(o), pay = MktPay(o),
                lord = o.Lord, sour = o.Sour, chance = MktChance(o), rep = MktRepFor(o),
            }),
            nextOrderAt = _mktOrderNext,
            boardMax = MktBoardMax,
            bell = Has("bell"),
            rep = FairVillages.Select(v => new
            {
                key = v.Key, pts = MktPoints(v.Key), level = MktLevel(v.Key),
                gifts = _mktGifts.TryGetValue(v.Key, out var got) ? got : 0,
            }),
            gifts = _mktGiftLog.Select(g => new { village = g.Village, text = g.Text, at = g.At }),
            giftFrom = FairGiftFrom,
            bazaar = _mktBazaarUntil > now ? new { until = _mktBazaarUntil, mult = FairBazaarMult } : null,
            beauty = _mktBeauty,
            bear = _mktBear,
            allMult = FairAllMult,
            guest = _mktGuest.Kind.Length > 0
                ? new { kind = _mktGuest.Kind, at = _mktGuest.At, until = _mktGuest.Until, x = _mktGuest.X, y = _mktGuest.Y }
                : null,
            guests = _mktGuests,
            eventAt = _mktEventAt,
            @event = _mktEvent is { } e
                ? new { id = e.Id, key = e.Key, until = e.Until, sure = e.Foresight ? new[] { e.RollA, e.RollB } : null }
                : null,
            foresight = _mktForesight,
            buffs = _mktBuffs.Where(b => b.Until > now).Select(b => new { kind = b.Kind, src = b.Src, mult = b.Mult, until = b.Until }),
            delivered = _mktDelivered,
        };
    }

    object? CatalogFair() => new
    {
        villages = FairVillages.Select(v => new { key = v.Key, name = v.Name, from = v.From, @in = v.In, emoji = v.Emoji, style = v.Style, perk = v.Perk, step = v.Step, people = v.People }),
        levels = FairRepLevels,
        payPerLevel = FairPayPerLevel,
        lordPayPerLevel = FairLordPayPerLevel,
        giftFrom = FairGiftFrom,
        guests = FairGuests.Select(g => new { key = g.Key, name = g.Name, emoji = g.Emoji, desc = g.Desc }),
        events = FairEvents.Select(e => new
        {
            key = e.Key, emoji = e.Emoji, title = e.Title, text = e.Text,
            a = new { label = e.A.Label, outcomes = e.A.Outcomes.Select(x => x.Text) },
            b = new { label = e.B.Label, outcomes = e.B.Outcomes.Select(x => x.Text) },
        }),
        holidays = FairHolidays.Select(x => new { key = x.Key, name = x.Name, emoji = x.Emoji, desc = x.Desc }),
        weather = new[]
        {
            new { key = "sun", name = "Сонце", emoji = "☀", desc = "сирці сохнуть швидше", dry = FairDrySun },
            new { key = "cloud", name = "Хмарно", emoji = "☁", desc = "сохне як завжди", dry = FairDryCloud },
            new { key = "rain", name = "Дощ", emoji = "🌧", desc = "сохне повільніше", dry = FairDryRain },
            new { key = "frost", name = "Мороз", emoji = "❄", desc = "сохне вдвічі довше", dry = FairDryFrost },
        },
        bids = new { down = FairDownPay, up = FairUpPay },
        weekend = FairWeekend,
        repAll = FairRepAll,
    };
}
