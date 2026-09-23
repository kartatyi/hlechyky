using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>Що дає верстат: більше глеків за клік, глеки без тебе, множник до всього чи вправність (розгін, глеки з полиці).</summary>
public enum ClickerKind { Click, Idle, Mult, Skill }

/// <summary>Віха верстата: одноразове покращення, яке відкривається, коли верстат доріс до рівня.</summary>
public sealed record ClickerMark(int Level, string Name);

/// <summary>
/// Один верстат майстерні: скільки коштує перший рівень, що дає і чи є в нього стеля. Ціна росте в
/// <c>GrowNum/GrowDen</c> разів за кожен куплений рівень: перші верстати — у півтора раза (3/2), нові
/// дорогі — на п'ятнадцять відсотків (23/20), бо інакше в них не було б і двадцяти рівнів.
/// </summary>
/// <param name="Rate">Глеків за секунду з одного рівня (для <see cref="ClickerKind.Idle"/>).</param>
/// <param name="MaxLevel">0 — купуй скільки хочеш; більше нуля — далі цього рівня верстат не тягнеться.</param>
public sealed record ClickerUpgrade(string Key, string Name, string Desc, double Base, ClickerKind Kind,
    double Rate = 0, int MaxLevel = 0, int GrowNum = 3, int GrowDen = 2, ClickerMark[]? Marks = null)
{
    /// <summary>Віху купують за вісім цін того рівня, на якому вона відкривається.</summary>
    public const int MarkFactor = 8;

    /// <summary>Доки ціна влазить у ціле число, рахуємо її цілими; далі точних цілих у double однаково не буває.</summary>
    const double Exact = 9.0e18;

    public ClickerMark[] Steps => Marks ?? [];

    public bool Capped(int level) => MaxLevel > 0 && level >= MaxLevel;

    /// <summary>
    /// Ціна наступного рівня: <c>ceil(Base × (GrowNum/GrowDen)^level)</c>. Поки ціна ціла, рахуємо цілими, бо
    /// double на високих рівнях промахується на одиницю, а ціна в магазині мусить бути та сама і в тесті, і на
    /// екрані. Далі — просто степінь: стелі в ціни більше нема (дев'яте оновлення), і вище за 1e300 вона
    /// однаково означає «досить».
    /// </summary>
    public double Price(int level)
    {
        if (level < 0) level = 0;
        var rough = Base * Math.Pow((double)GrowNum / GrowDen, level);
        if (!double.IsFinite(rough)) return double.MaxValue;
        if (rough > Exact) return Math.Ceiling(rough);
        var num = BigInteger.Pow(GrowNum, level) * new BigInteger(Base);
        var den = BigInteger.Pow(GrowDen, level);
        return (double)((num + den - 1) / den);
    }

    /// <summary>Ціна i-ї віхи: вісім цін її рівня.</summary>
    public double MarkPrice(int i)
    {
        var price = Price(Steps[i].Level);
        return price > double.MaxValue / MarkFactor ? double.MaxValue : price * MarkFactor;
    }
}

/// <summary>
/// Родинний секрет: вічне покращення за клейма майстра, обпал його не забирає. <paramref name="Ring"/> — коло:
/// перше (родинні, з першого оновлення) чи друге (дідівські, дев'яте оновлення — сотні клейм).
/// </summary>
public sealed record ClickerSecret(string Key, string Name, string Desc, int Price, int Ring = 1);

/// <summary>Розпис для глека: колекція на всі обпали, кожен розпис — плюс п'ять відсотків до всього.</summary>
public sealed record ClickerStyle(string Key, string Name, double Price);

/// <summary>
/// Гончарне коло — соло-клікер на одного назавжди. Кімната приватна й persistent: закрив вкладку, прийшов
/// через тиждень — коло крутилось і без тебе (але не більше ніж вісім годин, інакше з відпустки повертались
/// би мільйонери). Валюта тут своя, глеки; у черепки вони переходять лише через прилавок, сотнями.
///
/// Понад кліки й верстати тут є ще кілька речей, заради яких варто повертатись: віхи верстатів (×2), розписний
/// глек, що з'являється на колі на кілька секунд, обпал (скинути майстерню за вічні клейма майстра) і колекція
/// розписів. А щоб і клацати було за що: розгін кола («Маховик» — швидкі кліки поспіль дають дедалі більше)
/// і глек, що раз на хвилину-дві падає з полиці: спіймав — кілька хвилин пасиву однією жменею, не спіймав —
/// черепки. Правила живуть тільки тут: клієнт батчить кліки й малює плавний долік, але кожен глек рахує
/// сервер за <see cref="IRoomContext.Clock"/>.
///
/// Від автоклікерів і скриптів коло стереже Око майстра (<see cref="ClickerGuard"/>): кліки приходять із почерком,
/// за робочий почерк і раз на кілька тисяч кліків (після підозри — сотень) треба торкнутись глечиків на картинці,
/// за три помилки — пауза.
/// </summary>
public sealed partial class Clicker : Game
{
    /// <summary>Скільки глеків іде за один черепок.</summary>
    public const int Rate = 100;
    /// <summary>Більше кліків за секунду — це вже не палець, а скрипт; зайве мовчки відкидаємо.</summary>
    public const int MaxClicksPerSecond = 12;
    /// <summary>Скільки черепків на день можна виміняти, коли економіки поруч нема (тести, гола гра).</summary>
    public const int DefaultDailyCap = 20;
    /// <summary>Скільки черепків до денної стелі додають клейма: одне місце на кожні <see cref="StampsPerCap"/>, не більше.</summary>
    public const int MaxStampCap = 20;
    public const int StampsPerCap = 10;
    /// <summary>За скільки офлайну коло ще платить. Далі — тиша: інакше відпустка коштувала б грі балансу.</summary>
    public static readonly TimeSpan OfflineCap = TimeSpan.FromHours(8);
    /// <summary>Те саме з родинним секретом «Довга ніч».</summary>
    public static readonly TimeSpan LongOfflineCap = TimeSpan.FromHours(12);
    /// <summary>Скільки рівнів можна купити одним натиском «макс».</summary>
    public const int MaxBuy = 1000;

    // ---------- розписний глек ----------

    /// <summary>Скільки розписний глек стоїть на колі.</summary>
    public static readonly TimeSpan GoldenShown = TimeSpan.FromSeconds(12);
    /// <summary>Запас на дорогу: клік, що вилетів в останню мить, приїде на сервер трохи пізніше.</summary>
    public static readonly TimeSpan CatchGrace = TimeSpan.FromSeconds(2);
    /// <summary>Годинник клієнта міряється серверним «зараз» із виду, але пінг лишається — рання мить теж рахується.</summary>
    public static readonly TimeSpan EarlyGrace = TimeSpan.FromSeconds(1);
    public const int GoldenMinSeconds = 180, GoldenMaxSeconds = 480;
    /// <summary>З «Прикметою» глеки частіші.</summary>
    public const int OmenMinSeconds = 120, OmenMaxSeconds = 360;
    public const double FairMult = 7;
    public static readonly TimeSpan FairFor = TimeSpan.FromSeconds(66);
    public const double InspireMult = 25;
    public static readonly TimeSpan InspireFor = TimeSpan.FromSeconds(20);
    /// <summary>
    /// Дев'яте оновлення: під натхненням кожен клік несе ще три відсотки пасиву — і вже потім усе це множиться
    /// на ×25 і на розгін. Без цього натхнення мовчало в того, хто ще не купив «Легку руку» з «Руками майстра».
    /// </summary>
    public const double InspireShare = 0.03;
    /// <summary>
    /// Щедрий купець (дев'яте оновлення): шість хвилин роботи як дно плюс десята частина того, що лежить, але
    /// теж не більше шести хвилин. Було «15 % кишені, не більше чверті години» — у пізній грі це майже нічого.
    /// </summary>
    public const double MerchantShare = 0.15;
    /// <summary>Стеля частки кишені — ще півтори «плоскі» частини: разом 360…900 с пасиву, як і до v9 у найкращому разі.</summary>
    public const double MerchantCapShare = 1.5;
    public const double MerchantSeconds = 360;
    public const int GoldenForAchievement = 50;

    /// <summary>Що буде в розписному глеку. Вирішується, коли глек з'являється, а гравцеві показується, лише коли впіймав.</summary>
    public enum GoldenKind { Merchant, Fair, Inspire }

    // ---------- дев'яте оновлення: Око платить, серія без стелі, випадковості на сцені ----------

    /// <summary>Платня за пройдену спокійну полицю: дві години пасиву й десять тисяч кліків.</summary>
    public const double EyeSeconds = 7200;
    public const int EyeClicks = 10_000;
    /// <summary>Скільки спокійних полиць треба пройти для ачівки «Майстер кивнув».</summary>
    public const int CalmShelvesForAchievement = 10;

    /// <summary>Серія глеків з полиці: перші десять по +10 %, далі +2 % за кожен — і без стелі.</summary>
    public const double StreakFarBonus = 0.02;
    /// <summary>Скільки спійманих поспіль варті дивовижі.</summary>
    public static readonly int[] StreakWonders = [10, 25, 50, 100];
    public const int LongStreakForAchievement = 50;

    /// <summary>Від якої ціни верстат вправності ховається, поки гончар до нього не доріс, і з якої частки ціни з'являється.</summary>
    public const double SkillHideFrom = 1_000_000, SkillShowShare = 20;

    /// <summary>Щасливий клік: за кожен рівень верстата один відсоток кліків приносить у п'ятдесят разів більше.</summary>
    public const double LuckyChance = 0.01;
    public const double LuckyMult = 50;
    public const int LuckyForAchievement = 100;

    /// <summary>Кіт-мандрівник: раз на 6–14 хвилин пробігає сценою за шість секунд.</summary>
    public const int CatMinWait = 360, CatMaxWait = 840;
    public static readonly TimeSpan CatShown = TimeSpan.FromSeconds(6);
    /// <summary>Скільки кліків «коштує» погладжений кіт для Ока майстра.</summary>
    public const int PetWeight = 150;
    public const int CatsForAchievement = 25;
    /// <summary>Гостинець від кота: п'ять хвилин пасиву, три в'язки соломи, дзвінкий виріб або плюс один до серії.</summary>
    public enum CatGift { Passive, Straw, Ware, Streak }
    public const double CatPassiveSeconds = 300;
    public const int CatStraw = 3;

    /// <summary>Зірка падає лише вночі за київським часом — сцена й так темна.</summary>
    public const int NightFrom = 21, NightTo = 5;
    public const int StarMinWait = 1200, StarMaxWait = 2400;
    public static readonly TimeSpan StarShown = TimeSpan.FromSeconds(8);
    /// <summary>Загадав бажання: наступний глек з полиці втричі щедріший, а розписний прийде за пів хвилини.</summary>
    public const double StarFallMult = 3;
    public const double StarGoldenSeconds = 30;

    /// <summary>
    /// Довший простій — і гончар уже не біля кола: випадковості на сцені його не чекають, а починаються наново.
    /// Дві хвилини — бо вид летить щопачки кліків, а вкладку відкритою лишають і без кліків.
    /// </summary>
    public static readonly TimeSpan EventGap = TimeSpan.FromMinutes(2);

    /// <summary>Вітер із поля: раз на 25–50 хвилин на три чверті хвилини потроює пасив.</summary>
    public const int WindMinWait = 1500, WindMaxWait = 3000;
    public static readonly TimeSpan WindShown = TimeSpan.FromSeconds(45);
    public const double WindMult = 3;

    /// <summary>
    /// Яку версію «Що нового» показуємо. Побачив — більше не показуємо ніколи й ні на якому пристрої. «v9.1» — клейма
    /// після тисячі й наука майстра (docs/games/specs/clicker-stamps.md): бачили «v9» — побачать і це.
    /// </summary>
    public const string NewsVersion = "v9.1";

    // ---------- розгін кола ----------

    /// <summary>За скільки секунд розгін спадає в e разів: перестав клацати — за кілька секунд коло стало.</summary>
    public const double HeatTau = 3.0;
    /// <summary>Стільки «гарячих» кліків — повний розгін: приблизно шість кліків за секунду протягом трьох секунд.</summary>
    public const double HeatFull = 18;
    /// <summary>Скільки до стелі розгону додає один рівень «Маховика». Без маховика коло не розганяється зовсім.</summary>
    public const double FlywheelStep = 0.5;

    // ---------- глек з полиці ----------

    /// <summary>Скільки глек летить з полиці до долівки: спіймати треба рукою, а не прочитати у виді.</summary>
    public static readonly TimeSpan FallShown = TimeSpan.FromSeconds(3.2);
    public const int FallMinSeconds = 50, FallMaxSeconds = 130;
    /// <summary>З «Котом на полиці» глеки падають частіше.</summary>
    public const int CatMinSeconds = 30, CatMaxSeconds = 80;
    /// <summary>Що в спійманому глеку: дві хвилини пасиву плюс сотня кліків — щоб і новачкові, і магнатові було за що ловити.</summary>
    public const double FallSeconds = 120;
    public const int FallClicks = 100;
    /// <summary>Дно: навіть на голому колі спійманий глек — це відчутно.</summary>
    public const int FallFloor = 20;
    /// <summary>Кожен рівень «Кошика» — плюс п'ята частина до глека з полиці.</summary>
    public const double BasketBonus = 0.2;
    /// <summary>Серія спійманих поспіль: +10 % за кожен, не більше десяти. Розбився — серія обірвалась.</summary>
    public const double StreakBonus = 0.1;
    public const int StreakMax = 10;
    public const int GrabsForAchievement = 100, StreakForAchievement = 10;
    /// <summary>Раз на стільки щасливих кліків — кидок дивовижі (v9).</summary>
    public const int LuckyWonderEvery = 25;

    // ---------- обпал ----------

    /// <summary>Клейма рахуються від глеків за весь час: <c>⌊√(total / 1 млрд)⌋</c>.</summary>
    public const double StampUnit = 1e9;
    public const double StampBonus = 0.02, SealStampBonus = 0.03;
    /// <summary>
    /// Перша тисяча клейм дає бонус повністю, далі він росте як корінь: кожне нове клеймо важить дедалі менше, і
    /// вчетверо більше клейм дають лише приблизно вдвічі більший бонус. Без цього клейма (корінь із глеків за весь
    /// час) і драбина верстатів розганяли одне одного, і кожен обпал давав більше за попередній
    /// (docs/games/specs/clicker-stamps.md).
    /// </summary>
    public const int StampSoftFrom = 1000;
    /// <summary>
    /// Наука майстра: раз на <see cref="ScienceEvery"/> обпал дає ще <see cref="ScienceShare"/> різниці з клеймами
    /// найкращого гончаря округи, але не більше, ніж <see cref="ScienceCap"/> рази по власних клеймах за глеки —
    /// щоб відсталі наздоганяли, а новачок не перестрибнув пів гри за один день.
    /// </summary>
    public static readonly TimeSpan ScienceEvery = TimeSpan.FromHours(20);
    public const double ScienceShare = 0.25;
    public const int ScienceCap = 2;
    public const double StyleBonus = 0.05;
    /// <summary>Скільки рівнів дає «Родинний круг» після обпалу.</summary>
    public const int KinLevels = 10;

    /// <summary>Оголошено ДО магазину навмисно: статичні поля ініціалізуються згори вниз, а підписи верстатів
    /// уже форматують числа.</summary>
    static readonly CultureInfo Uk = CultureInfo.GetCultureInfo("uk-UA");

    /// <summary>
    /// Назви великих чисел — ті самі, що в клієнті (<c>web/games/clicker.js</c>, <c>BIG</c>): помилитись тут
    /// означає показати гравцеві два різні числа на одному екрані. Теж ДО магазину — з тієї самої причини.
    /// </summary>
    static readonly string[] BigNames = ["млн", "млрд", "трлн", "квдрлн", "квнтлн", "скстлн", "сптлн", "октлн", "нонлн", "дцлн"];

    /// <summary>
    /// Магазин. Порядок тут — це порядок кнопок на екрані. Перші чотири — ті, що були з першого дня (ціни ×1,5),
    /// далі драбина, прорахована симуляцією: новий верстат приблизно раз на кілька днів гри, останні — вже
    /// після обпалів.
    /// </summary>
    public static readonly ClickerUpgrade[] Shop =
    [
        new("wheel", "Швидше коло", "+1 глек за клік", 15, ClickerKind.Click,
            Marks: [new(10, "Ножний привід"), new(25, "Легка рука"), new(50, "Руки майстра")]),
        new("apprentice", "Підмайстер", "+0,5 глека за секунду", 100, ClickerKind.Idle, Rate: 0.5,
            Marks: [new(10, "Учні з Опішні"), new(25, "Кухоль узвару"), new(50, "Цехова грамота")]),
        new("kiln", "Піч", "+3 глеки за секунду", 1_000, ClickerKind.Idle, Rate: 3,
            Marks: [new(10, "Дубові дрова"), new(25, "Двоярусний горн"), new(50, "Вічний вогонь")]),
        new("clay", "Гарна глина", "×1,25 до всього", 10_000, ClickerKind.Mult, MaxLevel: 5),
        new("flywheel", "Маховик", "Швидкі кліки поспіль розкручують коло: +0,5 до стелі розгону", 250, ClickerKind.Skill, MaxLevel: 8),
        new("basket", "Кошик під полицею", "+20 % до глеків, що падають з полиці", 2_500, ClickerKind.Skill, MaxLevel: 10),
        // Дев'яте оновлення: три верстати, щоб клацати було варто й на квадрильйонах (docs/games/specs/clicker-v9.md §A.4).
        new("swing", "Замашна рука", "Клік бере ще +1 % пасиву за рівень", 50_000_000, ClickerKind.Skill, MaxLevel: 10),
        new("temper", "Гарт кола", "Розгін спадає повільніше: +1 с за рівень", 500_000_000, ClickerKind.Skill, MaxLevel: 5),
        new("lucky", "Щасливий клік", "1 % кліків за рівень б'є в ×50", 5_000_000_000, ClickerKind.Skill, MaxLevel: 5),
        Tier("workshop", "Гончарня", 100_000, 25, "Новий дах", "Полиці до стелі", "Вивіска на всю вулицю"),
        Tier("fair", "Ярмарок у Сорочинцях", 2_000_000, 150, "Намет із прапорцем", "Ярмаркові зазивали", "Гоголь приїхав"),
        Tier("artel", "Артіль в Опішні", 50_000_000, 900, "Спільна глина", "Артільний кошовий", "Знак Опішні"),
        Tier("chumaks", "Чумацький обоз", 1_000_000_000, 5_000, "Сіль у дорогу", "Круторогі воли", "Чумацький Шлях"),
        Tier("pit", "Глинище", 25_000_000_000, 32_000, "Голуба глина", "Кінний підйомник", "Глибокий пласт"),
        Tier("school", "Школа гончарів", 500_000_000_000, 200_000, "Підручник гончаря", "Майстер-клас", "Випускний у глині"),
        Tier("chaika", "Чайка до Царграда", 12_000_000_000_000, 1_200_000, "Козацька чайка", "Попутний вітер", "Царградський базар"),
        Tier("museum", "Музей гончарства", 250_000_000_000_000, 7_000_000, "Екскурсовод", "Вітрина скарбів", "Ніч у музеї"),
        Tier("tsar", "Цар-глек", 5_000_000_000_000_000, 45_000_000, "Глек на всю хату", "Глек на все село", "Глек видно з Місяця"),
        // Дев'яте оновлення: три щаблі після Цар-глека — ×22 ціни й ×6,5 доходу, щоб було заради чого грати далі.
        Tier("sloboda", "Гончарна слобода", 1e17, 3e8, "Своя вулиця", "Ярмарок під хатою", "Слобідський герб"),
        Tier("kontrakty", "Контрактовий ярмарок", 2.5e18, 2e9, "Контракт із Києвом", "Гостиний двір", "Лаврські купці"),
        Tier("sich", "Гончарня на Січі", 6e19, 1.3e10, "Курінь гончарів", "Козацька печатка", "Клейнод"),
    ];

    static ClickerUpgrade Tier(string key, string name, double price, double rate, string m25, string m50, string m100) =>
        new(key, name, $"+{Short(rate)} {Pots(rate)} за секунду", price, ClickerKind.Idle, Rate: rate,
            GrowNum: 23, GrowDen: 20, Marks: [new(25, m25), new(50, m50), new(100, m100)]);

    /// <summary>Родинні секрети — за клейма майстра, від дешевого до дорогого.</summary>
    public static readonly ClickerSecret[] Secrets =
    [
        new("night", "Довга ніч", "Коло крутиться без тебе 12 годин замість 8", 3),
        new("omen", "Прикмета", "Розписні глеки з'являються частіше", 5),
        new("cat", "Кіт на полиці", "Глеки падають з полиці частіше — котові нудно", 6),
        new("kin", "Родинний круг", $"Після обпалу коло, підмайстри й піч одразу на рівні {KinLevels}", 8),
        new("longfair", "Довгий ярмарок", "Бонуси розписних глеків тривають удвічі довше", 12),
        new("recipe", "Бабусин рецепт", "Гарна глина не згорає при обпалі", 20),
        new("memory", "Пам'ять рук", "Віхи верстатів не згорають при обпалі", 40),
        new("seal", "Родове клеймо", "Бонус клейм у півтора раза більший: +3 % замість +2 % за клеймо", 80),
        // Друге коло (дев'яте оновлення §A.5): дідівські секрети — на сотні клейм, кожен відмикає своє в майстерні.
        new("rack2", "Друга сушарня", "Під стріхою стає ще шість місць для сирцю", 150, Ring: 2),
        new("grandson", "Онук за колом", "Підмайстри ліплять до 0,8 роботи за секунду замість 0,5", 250, Ring: 2),
        new("caravan", "Великий обоз", "У дорозі буває п'ять купців, а на дошці — чотири", 400, Ring: 2),
        new("barn", "Ключ від клуні", "Клуня тримає 40 в'язок соломи, і в'язка вдвічі дешевша", 600, Ring: 2),
        new("bell", "Ярмарковий дзвін", "На дошці села до п'яти замовлень, нове — кожні 4–7 хвилин", 900, Ring: 2),
        new("ember", "Вогонь роду", "Палій пече якісніше: «блиск» його партії в півтора раза більший", 1_200, Ring: 2),
        new("ashes", "Дідова скриня", "Після обпалу в скрині лишається двадцята частина глеків", 2_000, Ring: 2),
    ];

    /// <summary>Розписи — від чорнодимленого до трипільського. Колекція лишається назавжди.</summary>
    public static readonly ClickerStyle[] Styles =
    [
        new("gavarets", "Гаварецька чорнодимлена", 1_000_000),
        new("vasylkiv", "Васильківська майоліка", 25_000_000),
        new("bubnivka", "Бубнівська", 500_000_000),
        new("kosiv", "Косівська", 10_000_000_000),
        new("opishnia", "Опішнянська", 250_000_000_000),
        new("mezhyhirya", "Межигірський фаянс", 5_000_000_000_000),
        new("petrykivka", "Петриківський розпис", 100_000_000_000_000),
        new("trypillia", "Трипільська", 2_000_000_000_000_000),
    ];

    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    public override GameInfo Info { get; } = new(
        "clicker", "Гончарне коло", "гончарне коло", GameGroup.Solo, 1, 1,
        Start: StartMode.Immediate, Private: true, Persistent: true, Score: ScoreOrder.HigherIsBetter,
        Hint: "Крути коло, ліпи глеки, лови розписні. Глеки — у черепки, майстерню — в обпал за клейма майстра. "
            + "Коло крутиться і без тебе, поки ти слухаєш радіо");

    readonly Dictionary<string, int> _levels = new(StringComparer.Ordinal);
    /// <summary>Куплені віхи, ключ — «верстат:рівень».</summary>
    readonly HashSet<string> _marks = new(StringComparer.Ordinal);
    readonly HashSet<string> _secrets = new(StringComparer.Ordinal);
    readonly HashSet<string> _styles = new(StringComparer.Ordinal);
    string _wear = "";

    /// <summary>
    /// Відро дозволів на кліки: дванадцять на дні, доливається дванадцять за секунду. Жорстке вікно «12 за
    /// останню секунду» тут не годиться — клієнт шле пачку раз на 700 мс, і дві сусідні пачки завжди падали б
    /// в одне вікно, з'їдаючи в чесного гравця кожен другий клік. Відро тримає ту саму швидкість, але не
    /// карає за те, що кліки приїхали купкою.
    /// </summary>
    double _tokens;
    DateTimeOffset _tokensAt;

    /// <summary>Око майстра: почерк кліків, перевірка картинкою й пауза кола (див. <see cref="ClickerGuard"/>).</summary>
    readonly ClickerGuard _guard = new();

    /// <summary>
    /// Глеки. З дев'ятого оновлення — <c>double</c>, а не <c>long</c>: у лідерів за тиждень набігає квадрильйон,
    /// і стеля <c>long</c> (9,2 квнтлн) була б місяцем-двома гри. Глеки завжди цілі (див. <see cref="Add"/>).
    /// </summary>
    double _pots;
    double _total;
    /// <summary>Недоліплений глек: пасив рідко дає ціле число, а губити півглека щосекунди — це половина доходу.</summary>
    double _carry;
    DateTimeOffset _lastSync;
    string _soldDay = "";
    int _soldShards;
    /// <summary>Останнє число, яке вже пішло в таблицю: те саме слати вдруге — марно смикати базу.</summary>
    double _scored = -1;
    DateTimeOffset _scoredAt;
    IOptionsMonitor<EconomyOptions>? _opts;

    GoldenRow _golden = new(default, default, GoldenKind.Merchant, 0, 0);
    DateTimeOffset _fairUntil;
    DateTimeOffset _inspireUntil;
    int _caught;
    /// <summary>Усі клейма: за глеки плюс <see cref="_stampsExtra"/>. Бонус, секрети й стеля черепків рахуються від них.</summary>
    int _stamps;
    /// <summary>
    /// Клейма понад ті, що за глеки: від Тавра майстра й науки майстра. Обпал рахує приріст від клейм за глеки, тож
    /// окремий лічильник потрібен, щоб наступний обпал їх не «з'їдав» (раніше саме так губилось клеймо від тавра).
    /// </summary>
    int _stampsExtra;
    /// <summary>Коли наука майстра дала клейма востаннє: наступна — через <see cref="ScienceEvery"/>.</summary>
    DateTimeOffset _scienceAt;
    int _firings;

    /// <summary>
    /// Розгін кола: «гарячі» кліки, що спадають експоненційно (<see cref="HeatTau"/>). Рахуємо на мить
    /// <c>_heatAt</c>; що більше кліків підряд — то більший множник, аж до стелі маховика.
    /// </summary>
    double _heat;
    DateTimeOffset _heatAt;

    /// <summary>Наступний глек з полиці, серія спійманих поспіль і спіймані за весь час.</summary>
    FallRow _fall = new(default, default, 0);
    int _fallStreak;
    int _grabbed;

    // ---------- дев'яте оновлення ----------

    /// <summary>Скільки кліків уже влучило в ×50 — лічильник для клієнта («✨ ×50» над колом) і для ачівки.</summary>
    long _lucky;
    /// <summary>Кіт, зірка й вітер на сцені: коли приходять, доки видно і що з собою несуть (§A.6).</summary>
    EventRow _cat = new(default, default, 0, 0);
    EventRow _star = new(default, default, 0, 0);
    EventRow _wind = new(default, default, 0, 0);
    int _petted;
    /// <summary>Бажання на падаючу зірку: наступний спійманий глек з полиці втричі щедріший.</summary>
    bool _starWish;
    /// <summary>Розписний глек чи глек з полиці проспали під полицею Ока — після «кивнув» їх переплановують.</summary>
    bool _goldenSlept, _fallSlept;
    /// <summary>Яку версію «Що нового» гончар уже бачив.</summary>
    string _news = "";

    /// <summary>
    /// Як часто число з таблиці оновлюється під час клацання. Кожна пачка кліків — це запис у ту саму
    /// SQLite, у яку пише ефір, тож півхвилини затримки в таблиці «Гончарі» коштують дешевше, ніж
    /// півтора запису на секунду з кожного гончаря.
    /// </summary>
    static readonly TimeSpan ScoreEvery = TimeSpan.FromSeconds(30);

    /// <summary>Пороги ачівок: платформа бачить їх саме з таблиці, тож ці числа мусять летіти негайно,
    /// а не чекати своєї півхвилини.</summary>
    static readonly long[] Milestones = [1_000, 100_000, 1_000_000, 1_000_000_000, 1_000_000_000_000];

    // ---------- те, з чого складається дохід ----------

    int Level(string key) => _levels.TryGetValue(key, out var n) ? n : 0;

    bool Has(string secret) => _secrets.Contains(secret);

    static string MarkKey(ClickerUpgrade up, int i) => $"{up.Key}:{up.Steps[i].Level}";

    int MarksOf(ClickerUpgrade up)
    {
        var n = 0;
        for (var i = 0; i < up.Steps.Length; i++)
            if (_marks.Contains(MarkKey(up, i))) n++;
        return n;
    }

    /// <summary>
    /// Множник до всього: глина (1,25^n), розписи (+5 % кожен) і клейма (+2 % за кожне з першої тисячі, з «Родовим
    /// клеймом» +3 %, далі — див. <see cref="StampWeight"/>). Складаються множенням між собою, а всередині кожного —
    /// додаванням, як і обіцяє підпис.
    /// </summary>
    double AllMult => Math.Pow(1.25, Level("clay"))
        * (1 + StyleBonus * _styles.Count)
        * StampMult
        * HouseAllMult
        // Пакети сьомого оновлення: альбом, кахлі, репутація сіл, цех (docs/games/specs/clicker-v7.md).
        * KilnAllMult * AlbumAllMult * FairAllMult * GuildAllMult;

    /// <summary>Скільки глеків за секунду дає один наступний рівень верстата (без ярмарку).</summary>
    double GainOf(ClickerUpgrade up) => up.Rate * Math.Pow(2, MarksOf(up)) * AllMult;

    /// <summary>Глеків за секунду без тебе — без ярмарку розписного глека.</summary>
    double PassiveBase
    {
        get
        {
            var sum = 0.0;
            foreach (var up in Shop)
                if (up.Kind == ClickerKind.Idle) sum += Level(up.Key) * up.Rate * Math.Pow(2, MarksOf(up));
            // Біла глина — для того, хто чекає; відро з водою — трохи до всього пасиву.
            return sum * AllMult * ClayNow.Passive * (Tool("bucket") ? BucketPassive : 1);
        }
    }

    bool FairOn => Ctx.Clock.UtcNow < _fairUntil;
    bool InspireOn => Ctx.Clock.UtcNow < _inspireUntil;

    /// <summary>
    /// Скільки пасиву йде в кожен клік: «Легка рука» — один відсоток, «Руки майстра» — ще два, гончарський
    /// шнурок — ще один, «Замашна рука» — по одному за рівень (дев'яте оновлення: без неї клік у пізній грі
    /// нічого не важив).
    /// </summary>
    double ClickShare
    {
        get
        {
            var wheel = Shop[0];
            return (_marks.Contains(MarkKey(wheel, 1)) ? 0.01 : 0) + (_marks.Contains(MarkKey(wheel, 2)) ? 0.02 : 0)
                + (Tool("string") ? 0.01 : 0) + SwingShare * Level("swing");
        }
    }

    /// <summary>Скільки пасиву додає до кліка один рівень «Замашної руки».</summary>
    public const double SwingShare = 0.01;

    /// <summary>
    /// Глеків за один клік без бонусів розписного глека. Клік мусить бути цілим числом — «+1,25 глека» на
    /// екрані виглядало б як помилка, тож множник глини тут округлюємо, а дробову частину віддаємо пасиву,
    /// де вона рахується чесно.
    /// </summary>
    double ClickBase
    {
        get
        {
            var wheel = Shop[0];
            var hands = (1 + Level("wheel")) * (_marks.Contains(MarkKey(wheel, 0)) ? 2 : 1) * AllMult
                * ClayNow.Click * (Tool("ribs") ? RibsClick : 1);
            return Math.Max(1, ToPots(Math.Round(hands + PassiveBase * ClickShare, MidpointRounding.AwayFromZero)));
        }
    }

    /// <summary>
    /// Глеків за один клік просто зараз — з натхненням і ярмарком, якщо вони тривають. Натхнення понад ×25
    /// кладе в кожен клік ще три відсотки пасиву (дев'яте оновлення §A.7), і ці відсотки теж множаться на ×25:
    /// інакше на квадрильйонах «Натхнення!» було б порожнім словом у того, хто ще не взяв «Руки майстра».
    /// </summary>
    public double PerClick =>
        ToPots((ClickBase + (InspireOn ? PassiveBase * InspireShare : 0)) * (InspireOn ? InspireMult : 1) * (FairOn ? FairMult : 1));

    /// <summary>Глеків за секунду без тебе просто зараз — з ярмарком і вітром із поля, якщо вони тривають.</summary>
    public double PerSecond => PassiveBase * (FairOn ? FairMult : 1) * (WindOn ? WindMult : 1);

    /// <summary>Стеля розгону: ×1 без маховика (коло не розганяється), +0,5 за кожен його рівень — до ×5.</summary>
    public double MomentumMax => 1 + FlywheelStep * Level("flywheel");

    /// <summary>
    /// За скільки секунд розгін спадає в e разів: <see cref="HeatTau"/>, з лопаткою — удвічі довше, і ще по
    /// секунді за кожен рівень «Гарту кола» (дев'яте оновлення): коло тримає розгін, поки рука переводить подих.
    /// </summary>
    double Tau => (Tool("paddle") ? HeatTau * PaddleTau : HeatTau) + TemperTau * Level("temper");

    /// <summary>Скільки секунд до згасання розгону додає один рівень «Гарту кола».</summary>
    public const double TemperTau = 1;

    /// <summary>Розгін на мить <paramref name="now"/>: те, що було, спадає в e разів за <see cref="Tau"/> секунд.</summary>
    double HeatAt(DateTimeOffset now) =>
        _heatAt == default || !(_heat > 0) ? 0 : _heat * Math.Exp(-Math.Max(0, (now - _heatAt).TotalSeconds) / Tau);

    /// <summary>Множник кліка від розгону: лінійно від ×1 на холодному колі до стелі на <see cref="HeatFull"/> гарячих кліках.</summary>
    public double MomentumOf(double heat) => 1 + (MomentumMax - 1) * Math.Min(1, Math.Max(0, heat) / HeatFull);

    /// <summary>
    /// Що дасть глек з полиці, якщо спіймати його просто зараз: дві хвилини пасиву й сотня кліків, помножені на
    /// кошик, серію і ярмарок, плюс дно. Ця сама сума їде у вид — клієнт малює її над спійманим глеком, не чекаючи відповіді.
    /// </summary>
    double FallGain()
    {
        var raw = PassiveBase * FallSeconds + ClickBase * FallClicks;
        var mult = (1 + BasketBonus * Level("basket")) * (1 + StreakMult) * (FairOn ? FairMult : 1)
            * ClayNow.Loot * HouseFallMult * (_starWish ? StarFallMult : 1);
        return ToPots(raw * mult) + FallFloor;
    }

    /// <summary>
    /// Що додає серія: перші десять спійманих по +10 %, далі по +2 % за кожен — і стелі більше нема
    /// (дев'яте оновлення §A.3). Серія на 37 — це +154 %.
    /// </summary>
    double StreakMult => StreakBonus * Math.Min(_fallStreak, StreakMax) + StreakFarBonus * Math.Max(0, _fallStreak - StreakMax);

    TimeSpan OfflineNow => (Has("night") ? LongOfflineCap : OfflineCap) + (Tool("lantern") ? LanternHours : TimeSpan.Zero) + HouseOfflineExtra;

    /// <summary>
    /// Стеля обміну на сьогодні: з налаштувань економіки (без неї — типова) плюс те, що заробили клейма, але
    /// не вище за <see cref="EconomyOptions.ClickerDailyCapMax"/>. Рівно те саме число пропустить і економіка
    /// (Rewards.ClickerCap): покажи гра більше — глеки списались би, а черепки не прийшли. Нульова стеля —
    /// обмін вимкнено, і клейма його не вмикають.
    /// </summary>
    int BaseCap => Math.Max(0, _opts?.CurrentValue.ClickerDailyCap ?? DefaultDailyCap);
    int CapMax => Math.Max(BaseCap, _opts?.CurrentValue.ClickerDailyCapMax ?? DefaultDailyCap + MaxStampCap);
    int DailyCap => BaseCap == 0 ? 0 : Math.Min(BaseCap + Math.Min(MaxStampCap, _stamps / StampsPerCap), CapMax);
    /// <summary>Скільки черепків до стелі справді додали клейма (після всіх обмежень).</summary>
    int StampCap => DailyCap - BaseCap;

    /// <summary>Скільки вже виміняно САМЕ сьогодні: після півночі лічильник сам стає нулем.</summary>
    int SoldToday => _soldDay == Days.Today(Ctx.Clock) ? _soldShards : 0;

    /// <summary>Скільки клейм дають глеки за весь час — усього, а не «ще».</summary>
    public static int StampsFor(double total) =>
        !(total > 0) ? 0 : (int)Math.Min(int.MaxValue, Math.Floor(Math.Sqrt(total / StampUnit)));

    /// <summary>Скільки глеків за весь час треба для n клейм.</summary>
    public static double TotalFor(int stamps) => ToPots((double)stamps * stamps * StampUnit);

    /// <summary>
    /// Скільки «повних» клейм важать <paramref name="stamps"/> клейм: до <see cref="StampSoftFrom"/> — усі, далі —
    /// <c>1000·(2√(n/1000) − 1)</c>. На тисячі крива переходить у корінь без сходинки й без зламу: наступне клеймо
    /// важить рівно одне, а далі дедалі менше (на 4000 — пів клейма, на 16 000 — чверть).
    /// </summary>
    public static double StampWeight(int stamps) =>
        stamps <= StampSoftFrom ? Math.Max(0, stamps) : StampSoftFrom * (2 * Math.Sqrt((double)stamps / StampSoftFrom) - 1);

    /// <summary>Множник від клейм: <c>1 + бонус за клеймо × вага клейм</c>.</summary>
    double StampMult => 1 + (Has("seal") ? SealStampBonus : StampBonus) * StampWeight(_stamps);

    /// <summary>Клейма за глеки — від них рахується приріст на обпалі (Тавро й наука лежать окремо).</summary>
    int NaturalStamps => Math.Max(0, _stamps - _stampsExtra);

    /// <summary>
    /// Скільки дасть наука майстра на обпалі, якщо після нього клейм за глеки буде <paramref name="natural"/>, а
    /// зверху — <paramref name="extra"/> (Тавро й наука, що вже є): чверть різниці з найкращим гончарем округи, не
    /// більше ніж двічі по <paramref name="natural"/>. Нуль — коли ще не минуло 20 годин від минулої науки, коли
    /// гончар сам найкращий або коли цеху (а з ним і округи) нема.
    /// </summary>
    int ScienceFor(int natural, int extra, int top, DateTimeOffset now)
    {
        if (_scienceAt != default && now - _scienceAt < ScienceEvery) return 0;
        var gap = (double)top - natural - extra;
        if (!(gap > 0)) return 0;
        // У double і зі стелею: двічі по мільярду клейм уже не влазить в int.
        return (int)Math.Clamp(Math.Min(Math.Floor(gap * ScienceShare), (double)ScienceCap * natural), 0, int.MaxValue / 4);
    }

    int StampsSpent => Secrets.Where(s => _secrets.Contains(s.Key)).Sum(s => s.Price);
    /// <summary>Клейма, витрачені не на секрети (оздоби хати тощо, v9): бонус клейм вони не гублять, як і секрети.</summary>
    int _stampsUsed;
    /// <summary>Вільні клейма: усі мінус секрети мінус інші покупки за клейма.</summary>
    internal int FreeStamps => _stamps - StampsSpent - _stampsUsed;

    /// <summary>Витратити клейма на щось, крім секретів (v9). null — вдалось; інакше готова відмова.</summary>
    internal ActResult? SpendStamps(int price)
    {
        if (price <= 0) return null;
        if (FreeStamps < price) return ActResult.Fail($"Бракує клейм: треба ще {price - FreeStamps}");
        _stampsUsed += price;
        return null;
    }

    // ---------- життя партії ----------

    public override string SeatName(int seat) => "гончар";

    public override void Start()
    {
        // Сервіси беремо тут: гру створює реєстр конструктором без параметрів, а в тестах економіки
        // взагалі нема — тоді стеля обміну лишається типовою, і гра від цього не ламається.
        _opts = Ctx.Services.GetService<IOptionsMonitor<EconomyOptions>>();

        _pots = 0;
        _total = 0;
        _carry = 0;
        _scored = -1;
        _scoredAt = default;
        _levels.Clear();
        foreach (var up in Shop) _levels[up.Key] = 0;
        _marks.Clear();
        _secrets.Clear();
        _styles.Clear();
        _wear = "";
        _soldDay = Days.Today(Ctx.Clock);
        _soldShards = 0;
        _lastSync = Ctx.Clock.UtcNow;
        _tokens = MaxClicksPerSecond;
        _tokensAt = _lastSync;
        _fairUntil = default;
        _inspireUntil = default;
        _caught = 0;
        _stamps = 0;
        _stampsExtra = 0;
        _scienceAt = default;
        _firings = 0;
        ScheduleGolden(_lastSync);
        _guard.Reset(Ctx.Rng);
        _heat = 0;
        _heatAt = _lastSync;
        _fallStreak = 0;
        _grabbed = 0;
        ScheduleFall(_lastSync);
        _lucky = 0;
        _petted = 0;
        _starWish = false;
        _goldenSlept = false;
        _fallSlept = false;
        // Новачкові «що нового» ні до чого — для нього нове все; новини бачить той, чиє збереження старше за випуск.
        _news = NewsVersion;
        ScheduleCat(_lastSync);
        ScheduleStar(_lastSync);
        ScheduleWind(_lastSync);
        ResetHouse(_lastSync);
        ResetCraft();
        ResetKiln(_lastSync);
        ResetAlbum(_lastSync);
        ResetFair(_lastSync);
        ResetGuild(_lastSync);
        _achQueue.Clear();
        _viewVersion++;
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        // Пасив дораховуємо перед кожною дією: і клік, і покупка мусять бачити однакове число глеків.
        _inAct = true;
        _viewVersion++;
        try { return ActInner(seat, action, payload); }
        finally { _inAct = false; }
    }

    ActResult ActInner(int seat, string action, JsonElement payload)
    {
        Sync();
        // Ачівки, що назбирались у видах (офлайн-прогрес рахується вже на відкритті), — тепер, коли каркас їх прийме.
        FlushAchievements();
        // Каталоги їдуть у вид лише до першої дії (Look знову попросить, якщо клієнтові їх бракує).
        _catalogWanted = false;
        var result = action switch
        {
            "spin" => Spin(payload),
            "buy" => Buy(payload),
            "mark" => BuyMark(payload),
            "sell" => Sell(payload),
            "catch" => Catch(),
            "grab" => Grab(),
            // Випадковості на сцені (v9): погладити кота, загадати бажання на зірку, «бачив, що нового».
            "pet" => Pet(),
            "wish" => Wish(),
            "news" => SeenNews(payload),
            // Клієнт питає свіжий вид, коли розписний глек утік чи глек з полиці розбився: наступний розклад знає лише сервер.
            "look" => LookHouse(payload) ?? Look(payload),
            "fire" => Fire(),
            "secret" => BuySecret(payload),
            "paint" => Paint(payload),
            "wear" => Wear(payload),
            "answer" => Answer(payload),
            // Хата: глина на коло, знаряддя на стіну, прикраса в хату, замовлення з дошки купців (ClickerHouse.cs).
            "knead" => Knead(payload),
            "tool" => BuyTool(payload),
            "adorn" => Adorn(payload),
            "take" => Take(payload),
            // Ремесло: що ліпити на колі й продаж виробів на базарі (ClickerCraft.cs).
            "form" => Form(payload),
            "bazaar" => Bazaar(payload),
            "craft" => ActCraft(payload),
            // Пакети сьомого оновлення — кожен зі своєю одною дією: kiln, album, fair, guild.
            _ => ActHouse(action, payload) ?? ActKiln(action, payload) ?? ActAlbum(action, payload) ?? ActFair(action, payload) ?? ActGuild(action, payload)
                ?? ActResult.Fail("Тут так не ходять"),
        };
        // Таблиця «Гончарі» — це глеки за весь час; те саме число вдруге їй нічого не додасть.
        if (result.Ok && WorthScoring())
        {
            _scored = _total;
            _scoredAt = Ctx.Clock.UtcNow;
            // Таблиця рахує в long, а глеків тепер буває й більше: вище за стелю long показуємо саму стелю.
            Ctx.Score(0, (long)Math.Min(_total, 9.2e18));
        }
        return result;
    }

    /// <summary>
    /// Чи варто зараз оновлювати рядок у таблиці. Клієнт шле пачку кліків раз на 700 мс, і <c>_total</c>
    /// росте від кожної — писати в базу півтора рази на секунду задорого. Тому під час клацання число
    /// оновлюється раз на <see cref="ScoreEvery"/>, а пороги ачівок ідуть одразу.
    /// </summary>
    bool WorthScoring()
    {
        if (_total == _scored) return false;
        if (_scored < 0) return true;                                    // перше число після Start/Load
        if (Ctx.Clock.UtcNow - _scoredAt >= ScoreEvery) return true;
        foreach (var mark in Milestones)
            if (_scored < mark && _total >= mark) return true;
        return false;
    }

    /// <summary>
    /// Пасив від останньої синхронізації. Стеля 8 год — не за сесію, а за один проміжок: хто заходить
    /// щодня, її й не помітить, а хто зник на тиждень, дістане рівно вісім годин роботи підмайстрів.
    /// Ярмарок множить лише ту частину проміжку, яку він справді тривав.
    /// </summary>
    void Sync()
    {
        RollDay();
        var now = Ctx.Clock.UtcNow;
        var from = _lastSync;
        _lastSync = now;
        var gap = now > from ? now - from : TimeSpan.Zero;
        var paid = gap > OfflineNow ? OfflineNow : gap;
        // Кіт, зірка й вітер — це те, що видно на сцені: поки гончаря не було, вони його не чекали (§A.6).
        var watching = gap <= EventGap;
        AwayBegin(gap);
        if (now > from)
        {
            // Ярмарок завжди починається з дії (а дія спершу синхронізує), тож він не може початись раніше
            // за from: досить обрізати його кінцем проміжку.
            var fair = _fairUntil > from ? (_fairUntil < now ? _fairUntil : now) - from : TimeSpan.Zero;
            if (fair > paid) fair = paid;
            // Вітер із поля потроює пасив рівно ті секунди, які справді віяв — і лише поки гончар був біля кола
            // (§A.6: це подія на сцені, а не погода в базі).
            var wind = watching ? Overlap(from, now, _wind.At, _wind.Until) : TimeSpan.Zero;
            if (wind > paid) wind = paid;
            Earn(PassiveBase * (paid.TotalSeconds + (FairMult - 1) * fair.TotalSeconds + (WindMult - 1) * wind.TotalSeconds));
        }
        // Утік — наступний. Від «зараз», а не від кінця старого: хто повернувся за добу, не мусить
        // перебирати пропущені глеки, щоб дійти до сьогоднішнього.
        var asked = _guard.Pending || _guard.Locked(now);
        if (now > _golden.Until + CatchGrace)
        {
            // Під полицею Ока глек однаково не ловився: після «кивнув» перепланувати від «зараз» (§A.2).
            if (asked) _goldenSlept = true;
            ScheduleGolden(now);
        }
        // Глек з полиці, якого ніхто не спіймав, — розбитий: серія обірвалась. Спійманий сюди не доходить —
        // Grab одразу ставить на полицю наступний.
        if (now > _fall.Until + CatchGrace)
        {
            // Під полицею Ока ловити було нічим: серію за це не рвемо й дамо новий глек, щойно майстер кивне.
            if (asked) _fallSlept = true;
            // Шкіряний фартух вибачає один розбитий у серії; другий поспіль — серія таки обірвалась.
            else if (_fallStreak > 0 && Tool("apron") && !_apronUsed) _apronUsed = true;
            else _fallStreak = 0;
            ScheduleFall(now);
        }
        // Пробігли — наступні від «зараз»; гончаря не було — теж від «зараз». Зірка ще й чекає ночі (§A.6).
        if (!watching || now > _cat.Until) ScheduleCat(now);
        if (!watching || now > _star.Until) ScheduleStar(now);
        if (!watching || now > _wind.Until) ScheduleWind(now);
        SyncOrders(now);
        // Ремесло й пакети — після пасиву й купців: підмайстри ліплять за той самий оплачений проміжок.
        // v9: ремесло й горно синхронізує майстерня (ClickerKiln.cs): за довгий простій — кроками, щоб палій устигав обпалювати.
        SyncWorkshop(now, paid);
        SyncAlbum(now, paid);
        SyncFair(now, paid);
        SyncGuild(now, paid);
        AwayEnd(now, gap);
    }

    /// <summary>Клієнт питає свіжий вид (глек утік, купець повернувся) або каталоги наново (<c>{ catalog: true }</c>).</summary>
    ActResult Look(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("catalog", out var c) && c.ValueKind == JsonValueKind.True)
            _catalogWanted = true;
        return ActResult.Done;
    }

    /// <summary>Нарахувати пасив разом із недоліпленим залишком.</summary>
    void Earn(double amount)
    {
        if (!(amount > 0)) return;
        _carry += amount;
        if (_carry < 1) return;
        var whole = Math.Floor(_carry);
        _carry -= whole;
        if (!double.IsFinite(_carry) || _carry < 0 || _carry >= 1) _carry = 0;   // на трильйонах double уже не тримає дробів
        Add(whole);
    }

    /// <summary>
    /// Додати глеків. Глек — штука: половини не буває, тож округлюємо вниз. Що прийшло зіпсованим (NaN чи
    /// нескінченність від чужої формули) — мовчки не рахуємо: краще нічого, ніж «NaN глеків» у лічильнику.
    /// </summary>
    void Add(double pots)
    {
        if (!double.IsFinite(pots) || !(pots >= 1)) return;
        pots = Math.Floor(pots);
        _pots += pots;
        _total += pots;
    }

    /// <summary>
    /// Перекинути лічильник обміну на новий день. Робиться одним місцем навмисно: доти, доки день чистили
    /// «по дорозі», вчорашня сума встигала переїхати в сьогодні й з'їдала гравцеві цілу денну стелю.
    /// </summary>
    void RollDay()
    {
        var today = Days.Today(Ctx.Clock);
        if (_soldDay == today) return;
        _soldDay = today;
        _soldShards = 0;
    }

    /// <summary><paramref name="seconds"/> — чекати рівно стільки (бажання на зірку кличе глек за пів хвилини).</summary>
    void ScheduleGolden(DateTimeOffset from, double? seconds = null)
    {
        var (min, max) = Has("omen") ? (OmenMinSeconds, OmenMaxSeconds) : (GoldenMinSeconds, GoldenMaxSeconds);
        // Чорна глина й глиняний свисток скорочують чекання; собака під лавою стереже глек трохи довше.
        var wait = seconds ?? (min + Ctx.Rng.NextDouble() * (max - min)) * ClayNow.Events * (Tool("whistle") ? WhistleEvents : 1);
        var at = from + TimeSpan.FromSeconds(wait);
        // Дев'яте оновлення §A.7: кидок 40/30/30 — купець уже не переважає, бо тепер він платить по-справжньому.
        var roll = Ctx.Rng.Next(100);
        var kind = roll < 40 ? GoldenKind.Merchant : roll < 70 ? GoldenKind.Fair : GoldenKind.Inspire;
        // Де саме на сцені: лівий верхній кут у відсотках. Глек завширшки ~58 px, сцена на телефоні ~300 px —
        // тож праворуч лишаємо чверть, щоб він не вилазив за картку.
        var shown = GoldenShown + (Adorned("dog") ? DogGuard : TimeSpan.Zero) + HouseGoldenExtra;
        _golden = new GoldenRow(at, at + shown, kind, Ctx.Rng.Next(4, 77), Ctx.Rng.Next(2, 70));
    }

    /// <summary>Коли й де впаде наступний глек з полиці: за хвилину-дві (з котом — частіше), x — у відсотках сцени.</summary>
    void ScheduleFall(DateTimeOffset from)
    {
        var (min, max) = Has("cat") ? (CatMinSeconds, CatMaxSeconds) : (FallMinSeconds, FallMaxSeconds);
        var at = from + TimeSpan.FromSeconds((min + Ctx.Rng.NextDouble() * (max - min)) * ClayNow.Events);
        _fall = new FallRow(at, at + (Tool("sponge") ? FallShownLong : FallShown), Ctx.Rng.Next(8, 80));
    }

    // ---------- випадковості на сцені: кіт, зірка, вітер (§A.6) ----------

    /// <summary>Подія на сцені: коли приходить, доки видно і двоє чисел на свій розсуд (гостинець і напрямок, місце в небі).</summary>
    sealed record EventRow(DateTimeOffset At, DateTimeOffset Until, int A, int B);

    static bool During(EventRow row, DateTimeOffset now) => now >= row.At && now <= row.Until;

    bool CatOn(DateTimeOffset now) => During(_cat, now);
    bool StarOn(DateTimeOffset now) => During(_star, now);
    bool WindOn => During(_wind, Ctx.Clock.UtcNow);

    /// <summary>Кіт-мандрівник: раз на 6–14 хв, шість секунд сценою. Гостинець і напрямок вирішуються тут, при появі.</summary>
    void ScheduleCat(DateTimeOffset from)
    {
        var at = from + TimeSpan.FromSeconds(CatMinWait + Ctx.Rng.NextDouble() * (CatMaxWait - CatMinWait));
        _cat = new EventRow(at, at + CatShown, Ctx.Rng.Next(0, 4), Ctx.Rng.Next(0, 2));
    }

    /// <summary>
    /// Зірка падає лише вночі за київським часом (21:00–05:00) — на світлій сцені її однаково не видно. Випала
    /// денна мить — чекаємо вечора й трохи згодом: рівно о дев'ятій зірки не падають.
    /// </summary>
    void ScheduleStar(DateTimeOffset from)
    {
        var at = from + TimeSpan.FromSeconds(StarMinWait + Ctx.Rng.NextDouble() * (StarMaxWait - StarMinWait));
        if (!NightIn(at)) at = NextNight(at) + TimeSpan.FromSeconds(Ctx.Rng.NextDouble() * (StarMaxWait - StarMinWait));
        _star = new EventRow(at, at + StarShown, Ctx.Rng.Next(8, 80), Ctx.Rng.Next(6, 34));
    }

    void ScheduleWind(DateTimeOffset from)
    {
        var at = from + TimeSpan.FromSeconds(WindMinWait + Ctx.Rng.NextDouble() * (WindMaxWait - WindMinWait));
        _wind = new EventRow(at, at + WindShown, 0, 0);
    }

    /// <summary>Ніч за київським часом: 21:00–05:00.</summary>
    static bool NightIn(DateTimeOffset now)
    {
        var hour = TimeZoneInfo.ConvertTime(now, Days.Kyiv).Hour;
        return hour >= NightFrom || hour < NightTo;
    }

    /// <summary>Найближча мить, коли над хатою вже ніч: сама <paramref name="now"/> уночі, інакше сьогоднішні 21:00.</summary>
    static DateTimeOffset NextNight(DateTimeOffset now)
    {
        if (NightIn(now)) return now;
        var local = TimeZoneInfo.ConvertTime(now, Days.Kyiv);
        var at = new DateTimeOffset(local.Date.AddHours(NightFrom), local.Offset);
        return at > now ? at : now;
    }

    /// <summary>
    /// Що на сцені зараз коштує гравцеві секунд: ярмарок, натхнення, розписний глек, глек у польоті, бафи села
    /// й три випадковості дев'ятого оновлення. Поки триває хоч одне — Око майстра не перебиває (§A.2): полиця
    /// з'їдала б саме ті секунди, заради яких гравець і сидить біля кола.
    /// </summary>
    bool BonusOn(DateTimeOffset now) =>
        FairOn || InspireOn || FairBuffOn(now)
        || (now >= _golden.At - EarlyGrace && now <= _golden.Until + CatchGrace)
        || (now >= _fall.At - EarlyGrace && now <= _fall.Until + CatchGrace)
        || CatOn(now) || StarOn(now) || During(_wind, now);

    /// <summary>Скільки з проміжку [from, to] припало на вікно події.</summary>
    static TimeSpan Overlap(DateTimeOffset from, DateTimeOffset to, DateTimeOffset at, DateTimeOffset until)
    {
        var start = from > at ? from : at;
        var end = to < until ? to : until;
        return end > start ? end - start : TimeSpan.Zero;
    }

    // ---------- дії ----------

    /// <summary>
    /// Клік. Клієнт батчить: замість дванадцяти повідомлень за секунду шле одне з пачкою відбитків
    /// <c>c: [[dt, press, x, y, src], …]</c> — по одному на кожен справжній натиск (див. <see cref="ClickerGuard"/>).
    /// Понад дванадцять кліків на секунду не рахуємо, але й не сваримось — чесний гравець із лагом не має
    /// бачити червоних тостів через власний інтернет. Так само мовчки не рахуємо, поки коло стоїть чи майстер
    /// чекає відповіді: чому — покаже вид.
    /// </summary>
    ActResult Spin(JsonElement payload)
    {
        // Кліки без почерку не приймаємо зовсім: так клацав би кожен скрипт. Стара вкладка (до Ока майстра) теж
        // тут — їй досить перезавантажитись.
        if (ClickerGuard.Parse(payload) is not { } hands)
            return ActResult.Fail("Коло оновилось — перезавантаж сторінку");
        var now = Ctx.Clock.UtcNow;
        if (_guard.Locked(now) || _guard.Pending) return ActResult.Done;

        if (_guard.Judge(hands) is { } why)
        {
            // Пачка, на якій почерк видав робота, не рахується, і далі — жодного кліка, доки не пройде полицю.
            // Глеків, зароблених раніше, не забираємо: так само виглядають тачпад і рівна рука на грубому таймері.
            _guard.Suspect(why);
            return ActResult.Done;
        }

        var taken = Math.Min(hands.Count, Allowance());
        _tokens -= taken;
        Add(ClickGain(taken));
        // Кліки ще й ліплять виріб на колі (глеків це не додає — лише роботу, див. ClickerCraft.cs).
        if (taken > 0) FormBy(taken, now);
        _guard.SpendClicks(taken);
        // Поки на сцені хоч щось діється, не перебиваємо: бонус тікає секундами, а перевірка почекає (§A.2).
        if (_guard.Due && !BonusOn(now)) _guard.Check();
        return ActResult.Done;
    }

    /// <summary>
    /// Скільки кліків із пачки влучило в ×50. Кидаємо на сервері й одразу на всю пачку (біноміально, по кліку):
    /// клієнт малює «✨ ×50» уже за приростом лічильника, а не вгадує наперед.
    /// </summary>
    int LuckyIn(int taken)
    {
        var level = Level("lucky");
        if (level <= 0 || taken <= 0) return 0;
        var chance = LuckyChance * level;
        var hits = 0;
        for (var i = 0; i < taken; i++)
            if (Ctx.Rng.NextDouble() < chance) hits++;
        return hits;
    }

    /// <summary>
    /// Відповідь майстрові: торкання полиці <c>taps: [[x, y], …]</c> у пікселях картинки. Влучив — коло крутиться
    /// далі; не влучив — нова полиця; три помилки поспіль — коло стоїть десять хвилин. Невдача — теж успішна дія
    /// (Done): стан змінився (нова полиця, лічильник спроб), і вид мусить до гравця долетіти.
    /// </summary>
    ActResult Answer(JsonElement payload)
    {
        var now = Ctx.Clock.UtcNow;
        if (!_guard.Pending) return ActResult.Fail("Майстер ні про що не питає — крути коло");
        if (_guard.Locked(now)) return ActResult.Fail($"Коло стоїть ще {Wait(_guard.LockUntil - now)}");
        if (ClickerGuard.Taps(payload) is not { } taps) return ActResult.Fail("Торкання прийшли зіпсовані");
        // Платить лише спокійна полиця, і лише поки її ще не пройдено: беремо це до відповіді.
        var calm = _guard.Calm;
        var missed = _guard.Misses > 0;
        // Платня — за зараховані кліки від минулої полиці (Share), а не за спійманих котів і глеків: інакше ловець
        // із чорною глиною доїв би майстра, не торкаючись кола (рецензія v9).
        var gain = calm ? ToPots(EyeGain * _guard.Share * (missed ? ClickerGuard.MissedShare : 1)) : 0;
        if (_guard.Answer(taps, now, Ctx.Rng) != ClickerGuard.Verdict.Passed) return ActResult.Done;

        // Проспаний під полицею глек чи розписний вертаємо: гравець не мусить платити за чесність (§A.2).
        if (_goldenSlept) { _goldenSlept = false; ScheduleGolden(now); }
        if (_fallSlept) { _fallSlept = false; ScheduleFall(now); }
        if (gain <= 0) return ActResult.Accept("👁 Майстер кивнув — крути далі");

        Add(gain);
        if (_guard.CalmPassed >= CalmShelvesForAchievement && _guard.CalmPassed - 1 < CalmShelvesForAchievement)
            Achieve("potter-eye");
        Wonder("eye");
        return ActResult.Accept($"👁 Майстер кивнув і відсипав {PotsShort(gain)}"
            + (missed ? " — половину: рука таки вагалась" : " за чесну руку"));
    }

    /// <summary>
    /// Скільки платить майстер за пройдену спокійну полицю: дві години пасиву й десять тисяч кліків. Це не
    /// подарунок, а плата за руку: полиця, що прийшла через підозру, пильний крок чи паузу, не платить нічого,
    /// інакше автоклікер із господарем при ньому доїв би майстра щодві хвилини.
    /// </summary>
    double EyeGain => PassiveBase * EyeSeconds + ClickBase * EyeClicks;

    /// <summary>Серія стала довшою на один — спійманим глеком чи котом: ачівки й дивовижі одні на обох.</summary>
    void StreakUp()
    {
        _fallStreak++;
        if (_fallStreak == StreakForAchievement) Ctx.Award(0, 0, "ach:potter-streak");
        if (_fallStreak == LongStreakForAchievement) Achieve("potter-streak-50");
        if (Array.IndexOf(StreakWonders, _fallStreak) >= 0) Wonder("streak");
    }

    /// <summary>«9:05» — скільки ще стояти колу.</summary>
    static string Wait(TimeSpan left)
    {
        var s = (int)Math.Ceiling(Math.Max(0, left.TotalSeconds));
        return $"{s / 60}:{s % 60:00}";
    }

    /// <summary>Скільки кліків відро готове віддати просто зараз; заразом і доливає його.</summary>
    int Allowance()
    {
        var now = Ctx.Clock.UtcNow;
        var seconds = Math.Max(0, (now - _tokensAt).TotalSeconds);
        _tokens = Math.Min(MaxClicksPerSecond, _tokens + seconds * MaxClicksPerSecond);
        _tokensAt = now;
        return (int)Math.Floor(_tokens);
    }

    /// <summary>
    /// Купити рівні верстата: <c>n</c> — скільки хочеться (кнопки «×10» і «макс»), купується стільки, скільки
    /// влазить у глеки. Хоч один — уже покупка; жодного — відмова з тим, чого бракує.
    /// </summary>
    ActResult Buy(JsonElement payload)
    {
        if (Shop.FirstOrDefault(u => u.Key == Str(payload, "key")) is not { } up)
            return ActResult.Fail("Такого верстата в майстерні нема");
        var raw = Num(payload, "n");
        if (raw is <= 0) return ActResult.Fail("Скільки купити — хоч один");
        var want = (int)Math.Clamp(raw ?? 1, 1, MaxBuy);

        var level = Level(up.Key);
        if (up.Capped(level)) return ActResult.Fail($"{up.Name}: кращої вже не буває");

        var bought = 0;
        while (bought < want && !up.Capped(level))
        {
            var price = up.Price(level);
            if (_pots < price)
            {
                if (bought == 0) return ActResult.Fail($"Бракує глеків: треба ще {Short(price - _pots)}");
                break;
            }
            _pots -= price;
            level++;
            bought++;
        }
        _levels[up.Key] = level;
        return ActResult.Accept(bought == 1 ? $"{up.Name} — рівень {level}" : $"{up.Name} +{bought} — рівень {level}");
    }

    /// <summary>Віха: відкривається рівнем верстата, купується один раз.</summary>
    ActResult BuyMark(JsonElement payload)
    {
        var key = Str(payload, "key");
        foreach (var up in Shop)
            for (var i = 0; i < up.Steps.Length; i++)
            {
                if (MarkKey(up, i) != key) continue;
                var step = up.Steps[i];
                if (_marks.Contains(key)) return ActResult.Fail($"«{step.Name}» уже є");
                if (Level(up.Key) < step.Level) return ActResult.Fail($"Спершу {up.Name} до рівня {step.Level}");
                var price = up.MarkPrice(i);
                if (_pots < price) return ActResult.Fail($"Бракує глеків: треба ще {Short(price - _pots)}");
                _pots -= price;
                _marks.Add(key);
                return ActResult.Accept($"«{step.Name}»: {MarkDesc(up, i)}");
            }
        return ActResult.Fail("Такої віхи нема");
    }

    static string MarkDesc(ClickerUpgrade up, int i) => up.Kind != ClickerKind.Click
        ? $"{up.Name} ×2"
        : i switch { 0 => "клік ×2", 1 => "клік +1 % пасиву", _ => "клік ще +2 % пасиву" };

    /// <summary>Прилавок: сотня глеків за черепок, не більше <see cref="DailyCap"/> черепків на день.</summary>
    ActResult Sell(JsonElement payload)
    {
        RollDay();   // після київської півночі лічильник дня чистий — і перевірка, і запис бачать нуль
        var pots = Num(payload, "pots") ?? 0;
        if (pots <= 0 || pots % Rate != 0) return ActResult.Fail($"Міняю сотнями: {Rate} глеків — один черепок");
        if (pots > _pots) return ActResult.Fail("Стільки глеків ще не наліплено");

        var left = Math.Max(0, DailyCap - _soldShards);
        if (left <= 0) return ActResult.Fail("Сьогодні черепки скінчились, приходь завтра");
        // Рахуємо в long і ріжемо стелею ДО приведення: глеків у стані може лежати скільки завгодно
        // (стан — це JSON у базі), а (int) від такої частки мовчки загорнувся б у мінус.
        var want = pots / Rate;
        if (want > left) return ActResult.Fail($"Сьогодні лишилось {left} — більше не візьму");
        var shards = (int)want;

        _pots -= pots;
        _soldShards += shards;
        // Стеля дня в економіці своя (Economy:ClickerDailyCap). Коли клейма її підняли, кажемо економіці
        // власне число — вона однаково не пустить вище за Economy:ClickerDailyCapMax.
        Ctx.Award(0, shards, StampCap > 0 ? $"clicker:{DailyCap}" : "clicker");
        return ActResult.Accept($"Обміняв {pots} глеків на {shards} {Shards(shards)}");
    }

    /// <summary>Розписний глек: впіймав у вікні — бонус, запізнився — він уже втік.</summary>
    ActResult Catch()
    {
        var now = Ctx.Clock.UtcNow;
        if (now < _golden.At - EarlyGrace || now > _golden.Until + CatchGrace)
            return ActResult.Fail("Розписний глек уже втік");
        // Глек ловиться скриптом так само легко, як клацається коло (вид знає, де й коли він стоїть), тож і тут
        // Око майстра: поки коло стоїть чи чекає відповіді, глеки не ловляться, а кожен спійманий наближає
        // перевірку. Хто лише ловить глеки й не клацає, зустріне майстра тут, а не в Spin.
        if (_guard.Locked(now) || _guard.Pending)
            return ActResult.Fail("Спершу Око майстра: покажи, що ти не автоклікер");

        var longer = Has("longfair") ? 2 : 1;
        string text;
        switch (_golden.Kind)
        {
            case GoldenKind.Fair:
                _fairUntil = now + FairFor * longer;
                text = $"🎪 Ярмарок! Усе ×{FairMult:0} на {(FairFor * longer).TotalSeconds:0} с";
                break;
            case GoldenKind.Inspire:
                _inspireUntil = now + InspireFor * longer;
                text = $"✨ Натхнення! Клік ×{InspireMult:0} на {(InspireFor * longer).TotalSeconds:0} с";
                break;
            default:
                // Шість хвилин роботи як дно плюс десята частина кишені (теж не більше шести хвилин): купець
                // мусить щось важити і на голому колі, і на квадрильйонах.
                var flat = PassiveBase * MerchantSeconds;
                var gain = ToPots((flat + Math.Min(_pots * MerchantShare, flat * MerchantCapShare)) * ClayNow.Loot) + 13;
                Add(gain);
                text = $"🧺 Щедрий купець: +{PotsShort(gain)}";
                break;
        }
        _caught++;
        _guard.Spend(ClickerGuard.CatchWeight);
        if (_caught == GoldenForAchievement) Ctx.Award(0, 0, "ach:potter-golden");
        ScheduleGolden(now);
        return ActResult.Accept(text + AskAfter(now));
    }

    /// <summary>
    /// Майстер питає ПІСЛЯ спійманого, а не замість нього (дев'яте оновлення §A.2): перевірка, що коштує глека
    /// в польоті, — це покарання за чесність. Бот нічого не виграв: наступного глека він уже не спіймає, доки
    /// не пройде полицю. І не питаємо, поки на сцені триває бонус: ярмарок і натхнення біжать секундами.
    /// </summary>
    string AskAfter(DateTimeOffset now)
    {
        if (!_guard.Due || _guard.Pending || _guard.Locked(now) || BonusOn(now)) return "";
        _guard.Check();
        return " · 👁 майстер хоче глянути на твої руки";
    }

    /// <summary>
    /// Глеків за пачку кліків з розгоном. Розгін спадає від останньої пачки, а множник беремо на середині цієї:
    /// перші її кліки холодніші за останні. Пачка, з якої відро нічого не віддало, кола не гріє. Без маховика
    /// множник рівно один — тоді просто множимо, без зайвого округлення.
    /// </summary>
    double ClickGain(int taken)
    {
        var now = Ctx.Clock.UtcNow;
        var heat = HeatAt(now);
        var mult = MomentumOf(heat + taken / 2.0);
        if (taken > 0)
        {
            _heat = heat + taken;
            _heatAt = now;
        }
        // «Щасливий клік»: кожен влучений іде не за один, а за п'ятдесят (v9 §A.4).
        var lucky = LuckyIn(taken);
        if (lucky > 0)
        {
            _lucky += lucky;
            if (_lucky >= LuckyForAchievement && _lucky - lucky < LuckyForAchievement) Achieve("potter-lucky");
            // Дивовижу за щасливий клік кидаємо не на кожен (їх бувають десятки за хвилину), а раз на 25 — інакше
            // «щасливі» дивовижі вичерпались би за перший вечір (v9, зауваження пакета «Хата»).
            if (_lucky / LuckyWonderEvery != (_lucky - lucky) / LuckyWonderEvery) Wonder("lucky");
        }
        var clicks = taken + lucky * (LuckyMult - 1);
        return mult <= 1 ? ToPots(PerClick * clicks) : ToPots(PerClick * clicks * mult);
    }

    /// <summary>
    /// Глек з полиці: спіймав, поки летів, — жменя глеків (див. <see cref="FallGain"/>) і плюс один до серії;
    /// не встиг — він уже черепки. Те саме Око майстра, що й у розписного: скрипт бачить у виді, коли й де
    /// впаде, тож поки коло стоїть чи майстер чекає — не ловиться, а кожен спійманий наближає перевірку.
    /// </summary>
    ActResult Grab()
    {
        var now = Ctx.Clock.UtcNow;
        if (now < _fall.At - EarlyGrace || now > _fall.Until + CatchGrace)
            return ActResult.Fail("Глек уже розбився");
        if (_guard.Locked(now) || _guard.Pending)
            return ActResult.Fail("Спершу Око майстра: покажи, що ти не автоклікер");

        var wished = _starWish;
        var gain = FallGain();
        Add(gain);
        _starWish = false;                 // бажання тримається, доки глек не спіймано, і згоряє на ньому
        StreakUp();
        _apronUsed = false;
        _grabbed++;
        _guard.Spend(ClickerGuard.CatchWeight);
        if (_grabbed == GrabsForAchievement) Ctx.Award(0, 0, "ach:potter-grab");
        ScheduleFall(now);
        return ActResult.Accept($"🤲 Спіймав! +{PotsShort(gain)}"
            + (wished ? " · зірка не збрехала (×3)" : "")
            + (_fallStreak > 1 ? $" · серія {_fallStreak}" : "") + AskAfter(now));
    }

    /// <summary>
    /// Обпал: глеки, верстати й віхи згорають, а клейма за глеки за весь час лишаються назавжди. Клейма
    /// рахуються від усього наліпленого, тож ранній обпал нічого не губить: пізніше дорахується решта.
    /// Зверху — Тавро майстра (+1) і раз на 20 годин наука майстра (<see cref="ScienceFor"/>); обидва лягають в
    /// <see cref="_stampsExtra"/>, тож наступний обпал рахує приріст лише від клейм за глеки й нічого з них не забирає.
    /// </summary>
    ActResult Fire()
    {
        var natural = StampsFor(_total);
        var gain = natural - NaturalStamps;
        if (gain < 1)
            return ActResult.Fail($"Ще рано: наступне клеймо — на {Short(TotalFor(Math.Max(natural, NaturalStamps) + 1))} глеків за весь час");

        var now = Ctx.Clock.UtcNow;
        // Тавро майстра — ще одне клеймо зверху, але лише коли обпал і так щось дає: інакше палили б щохвилини.
        var iron = Tool("iron") ? IronStamps : 0;
        var science = ScienceFor(natural, _stampsExtra + iron, GuildTopStamps().Stamps, now);
        _stamps += gain + iron + science;
        _stampsExtra += iron + science;
        if (science > 0) _scienceAt = now;
        _firings++;
        // v9: частина глеків переживає обпал — хата (гачок KeepShare) і дідова скриня зверху.
        _pots = Math.Floor(_pots * Math.Clamp(KeepShare, 0, 0.5));
        _carry = 0;
        foreach (var up in Shop)
            if (!(up.Key == "clay" && Has("recipe"))) _levels[up.Key] = 0;
        if (Has("kin"))
            foreach (var key in new[] { "wheel", "apprentice", "kiln" }) _levels[key] = KinLevels;
        if (!Has("memory")) _marks.Clear();
        FireHouse(Ctx.Clock.UtcNow);
        FireCraft();
        FireKiln(Ctx.Clock.UtcNow);
        FireAlbum(Ctx.Clock.UtcNow);
        FireFair(Ctx.Clock.UtcNow);
        FireGuild(Ctx.Clock.UtcNow);
        // Цех мусить знати нові клейма: з них рахується наука майстра для решти округи.
        GuildStampsChanged(now);

        if (_firings == 1) Ctx.Award(0, 0, "ach:potter-fire");
        Wonder("fire");
        var bonus = (StampMult - 1) * 100;
        return ActResult.Accept($"🔥 Обпал! +{gain + iron} {Stamps(gain + iron)}"
            + (science > 0 ? $" і ще +{science} від науки майстра" : "")
            + $" — тепер +{bonus.ToString("#,0.#", Uk)} % до всього");
    }

    /// <summary>Яка частка глеків переживає обпал: хата (v9) плюс дідова скриня — двадцята частина.</summary>
    double KeepShare => HouseKeepShare + (Has("ashes") ? AshesShare : 0);

    /// <summary>«Дідова скриня»: після обпалу лишається п'ять відсотків глеків.</summary>
    public const double AshesShare = 0.05;

    // ---------- випадковості на сцені (§A.6) ----------

    /// <summary>
    /// Погладити кота. Гостинець вирішився ще при появі (щоб кидок не залежав від миті натиску), а гравцеві
    /// кажеться аж тепер. Око майстра рахує кота як півтори сотні кліків: бот, що ловить самих котів, теж
    /// зустріне майстра.
    /// </summary>
    ActResult Pet()
    {
        var now = Ctx.Clock.UtcNow;
        if (now < _cat.At - EarlyGrace || now > _cat.Until + CatchGrace) return ActResult.Fail("Кіт уже побіг своєю дорогою");
        if (_guard.Locked(now) || _guard.Pending)
            return ActResult.Fail("Спершу Око майстра: покажи, що ти не автоклікер");

        string text;
        switch ((CatGift)Math.Clamp(_cat.A, 0, 3))
        {
            case CatGift.Straw:
                StrawAdd(CatStraw);
                text = $"🐈 Кіт приволік {CatStraw} в'язки соломи — і дивиться, наче так і треба";
                break;
            case CatGift.Ware:
                var open = Wares.Where(w => WareOpen(w.Key)).ToList();
                var ware = open.Count > 0 ? open[Ctx.Rng.Next(open.Count)] : Wares[0];
                PutItems(ware.Key, "", 3, 1);
                text = $"🐈 Кіт приніс {QualityWord(ware.Key, 3)} {ware.Name.ToLowerInvariant()} — де взяв, не каже";
                break;
            case CatGift.Streak:
                StreakUp();
                text = $"🐈 Кіт збив глек із полиці й сам його спіймав — серія {_fallStreak}";
                break;
            default:
                var gain = ToPots(PassiveBase * CatPassiveSeconds);
                Add(gain);
                text = gain > 0
                    ? $"🐈 Кіт намуркотів на {PotsShort(gain)} — п'ять хвилин роботи задарма"
                    : "🐈 Кіт намуркотів на цілих п'ять хвилин роботи — шкода, що працювати ще нікому";
                break;
        }
        _petted++;
        _guard.Spend(PetWeight);
        if (_petted == CatsForAchievement) Achieve("potter-cat");
        Wonder("cat");
        ScheduleCat(now);
        return ActResult.Accept(text + AskAfter(now));
    }

    /// <summary>Загадати бажання на падаючу зірку: наступний глек з полиці втричі щедріший, а розписний прийде за пів хвилини.</summary>
    ActResult Wish()
    {
        var now = Ctx.Clock.UtcNow;
        if (now < _star.At - EarlyGrace || now > _star.Until + CatchGrace) return ActResult.Fail("Зірка вже згасла");
        if (_guard.Locked(now) || _guard.Pending)
            return ActResult.Fail("Спершу Око майстра: покажи, що ти не автоклікер");

        _starWish = true;
        ScheduleGolden(now, StarGoldenSeconds);
        _guard.Spend(ClickerGuard.CatchWeight);
        Wonder("star");
        ScheduleStar(now);
        return ActResult.Accept($"🌠 Загадав! Наступний глек з полиці ×{StarFallMult:0}, а розписний — за {StarGoldenSeconds:0} с"
            + AskAfter(now));
    }

    /// <summary>«Бачив, що нового»: показ керує сервер, тож вікно відкриється рівно раз на гончаря, з будь-якого пристрою.</summary>
    ActResult SeenNews(JsonElement payload)
    {
        if (Str(payload, "v") != NewsVersion) return ActResult.Fail("Це новини з іншого оновлення");
        _news = NewsVersion;
        return ActResult.Done;
    }

    ActResult BuySecret(JsonElement payload)
    {
        if (Secrets.FirstOrDefault(s => s.Key == Str(payload, "key")) is not { } secret)
            return ActResult.Fail("Такого секрету в родині нема");
        if (_secrets.Contains(secret.Key)) return ActResult.Fail($"«{secret.Name}» уже знаєш");
        var free = FreeStamps;
        if (free < secret.Price) return ActResult.Fail($"Бракує клейм: треба ще {secret.Price - free}");
        _secrets.Add(secret.Key);
        return ActResult.Accept($"🤫 {secret.Name}: {secret.Desc.ToLowerInvariant()}");
    }

    ActResult Paint(JsonElement payload)
    {
        if (Styles.FirstOrDefault(s => s.Key == Str(payload, "key")) is not { } style)
            return ActResult.Fail("Такого розпису нема");
        if (_styles.Contains(style.Key)) return ActResult.Fail($"{style.Name} уже в колекції");
        if (_pots < style.Price) return ActResult.Fail($"Бракує глеків: треба ще {Short(style.Price - _pots)}");
        _pots -= style.Price;
        _styles.Add(style.Key);
        _wear = style.Key;                     // новий розпис хочеться одразу побачити на колі
        if (_styles.Count == Styles.Length) Ctx.Award(0, 0, "ach:potter-museum");
        return ActResult.Accept($"🎨 {style.Name} — +5 % до всього");
    }

    /// <summary>Який розпис стоїть на колі. Порожній ключ — простий глиняний глек.</summary>
    ActResult Wear(JsonElement payload)
    {
        var key = Str(payload, "key");
        if (key.Length > 0 && !_styles.Contains(key)) return ActResult.Fail("Цього розпису ще нема в колекції");
        _wear = key;
        return ActResult.Done;
    }

    // ---------- слова ----------

    /// <summary>Черепок / черепки / черепків — «2 черепків» ріже око так само, як і в гаманці.</summary>
    static string Shards(long n) => Plural(n, "черепок", "черепки", "черепків");
    static string Stamps(long n) => Plural(n, "клеймо", "клейма", "клейм");

    /// <summary>Глек / глеки / глеків; дробове число — «глека» («0,5 глека»).</summary>
    static string Pots(double n) => n % 1 != 0 ? "глека" : Plural(n, "глек", "глеки", "глеків");

    /// <summary>
    /// Число зі словом: «14,5 квдрлн глеків», а не «14,5 квдрлн глеки». Після скорочення слово узгоджується
    /// з «млн», а не з останньою цифрою — її на екрані однаково не видно. Те саме робить клієнтський potsShort.
    /// </summary>
    static string PotsShort(double n) => $"{Short(n)} {(Math.Abs(n) >= 1_000_000 ? "глеків" : Pots(n))}";

    static string Plural(double n, string one, string few, string many)
    {
        if (!double.IsFinite(n)) return many;
        if (n % 100 is >= 11 and <= 14) return many;
        var d = n % 10;
        return d == 1 ? one : d is >= 2 and <= 4 ? few : many;
    }

    /// <summary>«1,09 млн» замість «1 093 232»: мільярди цифрами не читаються. До мільйона — повне число,
    /// за децильйоном слів уже нема — там «1,2e36».</summary>
    public static string Short(double n)
    {
        if (!double.IsFinite(n)) return "∞";
        if (Math.Abs(n) < 1_000_000) return n % 1 == 0 ? n.ToString("#,0", Uk) : n.ToString("#,0.#", Uk);
        var i = (int)Math.Floor(Math.Log10(Math.Abs(n)) / 3) - 2;
        if (i >= BigNames.Length) return n.ToString("0.#e0", Uk);
        var v = n / Math.Pow(1000, i + 2);
        return $"{v.ToString(v < 10 ? "0.##" : v < 100 ? "0.#" : "0", Uk)} {BigNames[i]}";
    }

    // ---------- вид ----------

    public override object View(int? seat)
    {
        // Каркас будує вид двічі поспіль (місце й глядач) і розсилає його вже поза замком кімнати. Тому вид — готовий
        // JsonElement: жодних лінивих Select по живих колекціях, які змінить наступна пачка кліків під час серіалізації
        // («Collection was modified»), — і в межах 50 мс без дії той самий вид не складається вдруге.
        var at = Ctx.Clock.UtcNow;
        if (_viewCache is { } cached && _viewCacheVersion == _viewVersion && at >= _viewCacheAt && at - _viewCacheAt < ViewReuse)
            return cached;
        _memoOn = false;
        JsonElement built;
        try { built = JsonSerializer.SerializeToElement(BuildView(), Wire); }
        finally { _memoOn = false; }
        _viewCache = built;
        _viewCacheAt = at;
        _viewCacheVersion = _viewVersion;
        return built;
    }

    static readonly TimeSpan ViewReuse = TimeSpan.FromMilliseconds(50);
    JsonElement? _viewCache;
    DateTimeOffset _viewCacheAt;
    long _viewVersion, _viewCacheVersion = -1;

    object BuildView()
    {
        // Пасив рахуємо і на відкритті, а не лише при дії (так каже spec): гончар, який повернувся й
        // просто дивиться на коло, мусить одразу бачити зароблене, а не чекати першого кліка. View
        // каркас кличе під замком кімнати (Rooms.ViewsFor), тож синхронізувати тут безпечно.
        Sync();
        var all = AllMult;
        var passive = PassiveBase;
        _memoPassive = passive;
        _memoClick = ClickBase;
        _memoOn = true;
        var spent = StampsSpent;
        var stampsAll = StampsFor(_total);
        var heat = HeatAt(Ctx.Clock.UtcNow);
        return new
        {
            pots = _pots,
            total = _total,
            perClick = PerClick,
            // Клік без натхнення й ярмарку: бонуси кінчаються між видами, і клієнт мусить знати, до чого вертатись.
            clickBase = ClickBase,
            perSecond = PerSecond,
            // Без ярмарку: клієнт доліковує сам і сам вимикає ярмарок, коли той скінчиться.
            baseSecond = passive,
            upgrades = Shop.Select((u, index) => (u, index)).ToDictionary(x => x.u.Key, x => (object)new
            {
                level = Level(x.u.Key),
                price = x.u.Price(Level(x.u.Key)),
                name = x.u.Name,
                desc = x.u.Desc,
                max = x.u.MaxLevel,
                kind = x.u.Kind.ToString().ToLowerInvariant(),
                // Скільки глеків за секунду додасть наступний рівень — для підказки «окупиться за».
                gain = x.u.Kind switch
                {
                    ClickerKind.Idle => GainOf(x.u),
                    ClickerKind.Mult when !x.u.Capped(Level(x.u.Key)) => passive * 0.25,
                    _ => 0,
                },
                growth = (double)x.u.GrowNum / x.u.GrowDen,
                marks = MarksOf(x.u),
                // Справжній множник від віх: у пасивних ×2 за кожну, а в колі ×2 дає лише перша (решта — відсоток пасиву).
                boost = x.u.Kind == ClickerKind.Click
                    ? (_marks.Contains(MarkKey(x.u, 0)) ? 2 : 1)
                    : Math.Pow(2, MarksOf(x.u)),
                open = Opened(x.index),
            }, StringComparer.Ordinal),
            // Лише відкриті й ще не куплені віхи: решта клієнту ні до чого, а вид летить щопачки кліків.
            marks = Shop.SelectMany(u => u.Steps.Select((m, i) => (u, m, i)))
                .Where(x => !_marks.Contains(MarkKey(x.u, x.i)) && Level(x.u.Key) >= x.m.Level)
                .Select(x => new
                {
                    key = MarkKey(x.u, x.i), on = x.u.Key, level = x.m.Level, name = x.m.Name,
                    desc = MarkDesc(x.u, x.i), price = x.u.MarkPrice(x.i),
                })
                .ToList(),
            canSellToday = Math.Max(0, DailyCap - SoldToday),
            soldToday = SoldToday,
            cap = DailyCap,
            rate = Rate,
            // Клієнт доліковує глеки від цієї мітки — тому вона мусить бути на дроті, а не лише в пам'яті.
            lastSync = _lastSync,
            // І серверне «зараз» поруч: інакше клієнт міряв би серверну мітку своїм годинником, а збитий
            // на кілька хвилин годинник малював би сотні глеків, яких на сервері нема.
            now = Ctx.Clock.UtcNow,
            offlineHours = OfflineNow.TotalHours,
            // Наступний розписний глек. Що в ньому — секрет до першого кліка.
            golden = new { at = _golden.At, until = _golden.Until, x = _golden.X, y = _golden.Y },
            caught = _caught,
            fair = new { until = _fairUntil, mult = FairMult },
            // share — ті самі три відсотки пасиву, що натхнення кладе в кожен клік: клієнт мусить рахувати так само.
            inspire = new { until = _inspireUntil, mult = InspireMult, share = InspireShare },
            allMult = all,
            stamps = _stamps,
            stampsFree = FreeStamps,
            // Приріст — від клейм за глеки: Тавро й наука (stampsExtra) лежать окремо, і обпал їх не забирає.
            stampsReady = Math.Max(0, stampsAll - NaturalStamps),
            stampsExtra = _stampsExtra,
            nextStampAt = TotalFor(stampsAll + 1),
            stampBonus = Has("seal") ? SealStampBonus : StampBonus,
            // З якого клейма бонус росте як корінь (StampWeight): клієнт рахує «після обпалу» тією самою кривою.
            stampSoft = StampSoftFrom,
            stampMult = StampMult,
            stampIron = Tool("iron") ? IronStamps : 0,
            science = ScienceView(Ctx.Clock.UtcNow),
            stampCap = StampCap,
            firings = _firings,
            secrets = Secrets.Select(s => new { key = s.Key, name = s.Name, desc = s.Desc, price = s.Price, ring = s.Ring, owned = _secrets.Contains(s.Key) }),
            styles = Styles.Select(s => new { key = s.Key, name = s.Name, price = s.Price, owned = _styles.Contains(s.Key) }),
            wear = _wear,
            // Розгін: скільки гарячих кліків зараз і що з них виходить. Клієнт веде той самий рахунок між видами.
            heat,
            heatFull = HeatFull,
            heatTau = Tau,
            momentum = MomentumOf(heat),
            momentumMax = MomentumMax,
            // Наступний глек з полиці і скільки він дасть, якщо спіймати просто зараз.
            fall = new { at = _fall.At, until = _fall.Until, x = _fall.X, streak = _fallStreak, gain = FallGain(), bonus = StreakMult },
            grabbed = _grabbed,
            // Дев'яте оновлення: щасливі кліки за весь час (клієнт малює «✨ ×50» за приростом) і бажання на зірку.
            lucky = _lucky,
            starWish = _starWish,
            // Три випадковості на сцені: кіт, зірка й вітер із поля (§A.6). A/B — гостинець і напрямок, місце в небі.
            events = new
            {
                cat = new { at = _cat.At, until = _cat.Until, dir = _cat.B },
                star = new { at = _star.At, until = _star.Until, x = _star.A, y = _star.B },
                wind = new { at = _wind.At, until = _wind.Until, mult = WindMult },
                petted = _petted,
            },
            // «Що нового»: поки гончар цього оновлення не бачив — версія, інакше нічого.
            news = _news == NewsVersion ? null : NewsVersion,
            // Хата: глина, знаряддя, прикраси й дошка купців (ClickerHouse.cs).
            house = HouseView(Ctx.Clock.UtcNow),
            // Сьоме оновлення: ремесло й пакети (docs/games/specs/clicker-v7.md). Каталоги — лише коли просили.
            craft = CraftView(Ctx.Clock.UtcNow),
            kiln = ViewKiln(Ctx.Clock.UtcNow),
            album = ViewAlbum(Ctx.Clock.UtcNow),
            // «fair» у виді вже зайняте ярмарком розписного глека — пакет ярмарку й людей їде як «market».
            market = ViewFair(Ctx.Clock.UtcNow),
            guild = ViewGuild(Ctx.Clock.UtcNow),
            away = AwayView(),
            catalog = CatalogView(),
            // Око майстра: null, поки коло крутиться вільно; інакше полиця-картинка (без зерна), пауза й платня.
            guard = _guard.View(Ctx.Clock.UtcNow, EyeGain),
        };
    }

    /// <summary>
    /// Наука майстра для виду: найкращий гончар округи й скільки в нього клейм, частка, стеля і з якої миті наука
    /// знову готова (null — готова вже). Скільки саме дасть обпал, клієнт рахує від живих глеків тією ж формулою,
    /// що й <see cref="ScienceFor"/>.
    /// </summary>
    object ScienceView(DateTimeOffset now)
    {
        var top = GuildTopStamps();
        return new
        {
            top = top.Stamps,
            who = top.Nick,
            share = ScienceShare,
            cap = ScienceCap,
            readyAt = _scienceAt != default && now - _scienceAt < ScienceEvery ? _scienceAt + ScienceEvery : (DateTimeOffset?)null,
        };
    }

    /// <summary>
    /// Чи показувати верстат. Драбину відкриваємо поступово: куплений, не «пасивний» або наступний після
    /// вже купленого пасивного — інакше полиця з тринадцяти карток із трильйонними цінами лякала б новачка.
    /// </summary>
    bool Opened(int index)
    {
        var up = Shop[index];
        // Вправність за мільйони (дев'яте оновлення: Замашна рука, Гарт кола, Щасливий клік) — не для першого
        // дня: поки гончар не наліпив і двадцятої частини ціни, картка лише лякала б мільярдами поруч із
        // «+1 глек за клік». Маховик і кошик (сотні глеків) видно, як і було.
        if (up.Kind == ClickerKind.Skill)
            return Level(up.Key) > 0 || up.Base < SkillHideFrom || _total * SkillShowShare >= up.Base;
        if (up.Kind != ClickerKind.Idle || Level(up.Key) > 0) return true;
        for (var i = index - 1; i >= 0; i--)
            if (Shop[i].Kind == ClickerKind.Idle) return Level(Shop[i].Key) > 0;
        return true;                                           // перший пасивний верстат видно завжди
    }

    // ---------- збереження ----------

    /// <summary>
    /// Стан у сховищі. Форма — зі spec, з дописками: <c>carry</c>, щоб недоліплений глек не губився на F5,
    /// відро дозволів замість списку кліків (див. <see cref="Allowance"/>) і все, що додав обпал: віхи,
    /// розписний глек із бонусами, клейма, секрети й розписи. Нові поля необов'язкові — старі збереження
    /// читаються як «цього ще не було».
    /// </summary>
    sealed record SoldRow(string Day, int Shards);

    sealed record BucketRow(double Tokens, DateTimeOffset At);

    sealed record GoldenRow(DateTimeOffset At, DateTimeOffset Until, GoldenKind Kind, int X, int Y);

    sealed record FallRow(DateTimeOffset At, DateTimeOffset Until, int X);

    sealed record Snapshot(
        double Pots, double Total, double Carry, DateTimeOffset LastSync,
        Dictionary<string, int> Upgrades, SoldRow SoldToday, BucketRow Clicks,
        List<string>? Marks = null, GoldenRow? Golden = null,
        DateTimeOffset FairUntil = default, DateTimeOffset InspireUntil = default, int Caught = 0,
        int Stamps = 0, int Firings = 0, List<string>? Secrets = null, List<string>? Styles = null, string? Wear = null,
        ClickerGuard.Row? Guard = null,
        FallRow? Fall = null, int FallStreak = 0, int Grabbed = 0, double Heat = 0, DateTimeOffset HeatAt = default,
        HouseRow? House = null,
        CraftRow? Craft = null, KilnRow? Kiln = null, AlbumRow? Album = null, FairRow? Fair = null, GuildRow? Guild = null,
        List<string>? Achievements = null, int StampsUsed = 0,
        // Дев'яте оновлення: щасливі кліки, випадковості на сцені, бажання на зірку, проспані під полицею глеки
        // й побачене «Що нового». Усе необов'язкове — старе збереження читається як «цього ще не було».
        long Lucky = 0, EventRow? Cat = null, EventRow? Star = null, EventRow? Wind = null,
        int Petted = 0, bool StarWish = false, bool GoldenSlept = false, bool FallSlept = false, string? News = null,
        // Клейма понад ті, що за глеки (Тавро й наука майстра), і остання наука. Старе збереження — «ще не було».
        int StampsExtra = 0, DateTimeOffset ScienceAt = default);

    public override string? Save() => JsonSerializer.Serialize(
        new Snapshot(_pots, _total, _carry, _lastSync,
            new Dictionary<string, int>(_levels, StringComparer.Ordinal),
            new SoldRow(_soldDay, _soldShards), new BucketRow(_tokens, _tokensAt),
            _marks.Order(StringComparer.Ordinal).ToList(), _golden, _fairUntil, _inspireUntil, _caught,
            _stamps, _firings, _secrets.Order(StringComparer.Ordinal).ToList(),
            _styles.Order(StringComparer.Ordinal).ToList(), _wear, _guard.Save(),
            _fall, _fallStreak, _grabbed, _heat, _heatAt, SaveHouse(),
            SaveCraft(), SaveKiln(), SaveAlbum(), SaveFair(), SaveGuild(), _achQueue.Count > 0 ? [.. _achQueue] : null, _stampsUsed,
            _lucky, _cat, _star, _wind, _petted, _starWish, _goldenSlept, _fallSlept, _news,
            _stampsExtra, _scienceAt),
        Wire);

    public override void Load(string json)
    {
        // Стан лежить у базі й міг застати попередню версію гри або чиюсь правку руками: чого не зрозуміли —
        // лишаємо чистим, але партію через це не ламаємо.
        if (JsonSerializer.Deserialize<Snapshot>(json, Wire) is not { } s) return;

        // Глеки в базі — число: старі збереження писали ціле (long), нові пишуть double. Читаються однаково;
        // зіпсоване (NaN, нескінченність) — нуль, а не «∞ глеків» на екрані.
        _pots = ToPots(s.Pots);
        _total = Math.Max(_pots, ToPots(s.Total));
        _carry = double.IsFinite(s.Carry) ? Math.Clamp(s.Carry, 0, 1) : 0;
        _lastSync = s.LastSync == default ? Ctx.Clock.UtcNow : s.LastSync;
        _scored = -1;
        _scoredAt = default;

        _levels.Clear();
        foreach (var up in Shop)
        {
            var level = s.Upgrades is not null && s.Upgrades.TryGetValue(up.Key, out var n) ? n : 0;
            _levels[up.Key] = up.MaxLevel > 0 ? Math.Clamp(level, 0, up.MaxLevel) : Math.Max(0, level);
        }

        _soldDay = s.SoldToday?.Day ?? "";
        _soldShards = Math.Max(0, s.SoldToday?.Shards ?? 0);

        // Відро переживає F5 навмисно: інакше перезавантаження сторінки скидало б захист від автоклікера.
        _tokens = s.Clicks is { } b && double.IsFinite(b.Tokens) ? Math.Clamp(b.Tokens, 0, MaxClicksPerSecond) : MaxClicksPerSecond;
        _tokensAt = s.Clicks is { At: var at } && at != default ? at : _lastSync;

        // Лише ті ключі, які гра знає: вигадана віха з бази не має множити дохід.
        var marks = Shop.SelectMany(u => u.Steps.Select((_, i) => MarkKey(u, i))).ToHashSet(StringComparer.Ordinal);
        Fill(_marks, s.Marks, marks.Contains);
        Fill(_secrets, s.Secrets, k => Secrets.Any(x => x.Key == k));
        Fill(_styles, s.Styles, k => Styles.Any(x => x.Key == k));
        _wear = s.Wear is { } w && _styles.Contains(w) ? w : "";

        _stamps = Math.Max(0, s.Stamps);
        // Старе збереження окремого лічильника не має — усі клейма вважаємо за глеки (тавро, що вже «з'їдене», не
        // вертаємо). Наука з майбутнього (правлена база, збитий годинник) чекає не довше, ніж від «зараз».
        _stampsExtra = Math.Clamp(s.StampsExtra, 0, _stamps);
        _scienceAt = s.ScienceAt > Ctx.Clock.UtcNow ? Ctx.Clock.UtcNow : s.ScienceAt;
        _firings = Math.Max(0, s.Firings);
        _stampsUsed = Math.Max(0, s.StampsUsed);
        _caught = Math.Max(0, s.Caught);
        _fairUntil = s.FairUntil;
        _inspireUntil = s.InspireUntil;
        if (s.Golden is { } g && g.Until > g.At && Enum.IsDefined(g.Kind))
            _golden = g with { X = Math.Clamp(g.X, 0, 90), Y = Math.Clamp(g.Y, 0, 90) };
        else
            ScheduleGolden(Ctx.Clock.UtcNow);    // збереження з часів до розписних глеків
        // Пауза кола й недороблена перевірка переживають F5 — інакше перезавантаження знімало б і те, і те.
        _guard.Load(s.Guard, Ctx.Rng);

        // Розгін переживає F5 теж (він однаково спаде за кілька секунд), а глек з полиці — або той, що вже
        // на розкладі, або, для збережень із часів до полиці, новий.
        _heat = double.IsFinite(s.Heat) ? Math.Clamp(s.Heat, 0, 10 * HeatFull) : 0;
        _heatAt = s.HeatAt;
        _fallStreak = Math.Max(0, s.FallStreak);
        _grabbed = Math.Max(0, s.Grabbed);
        if (s.Fall is { } f && f.Until > f.At)
            _fall = f with { X = Math.Clamp(f.X, 0, 90) };
        else
            ScheduleFall(Ctx.Clock.UtcNow);

        // Дев'яте оновлення. Старе збереження цих полів не знає — тоді розклад подій від «зараз», а «Що нового»
        // гончар ще не бачив (і мусить побачити).
        _lucky = Math.Max(0, s.Lucky);
        _petted = Math.Max(0, s.Petted);
        _starWish = s.StarWish;
        _goldenSlept = s.GoldenSlept;
        _fallSlept = s.FallSlept;
        _news = s.News is { Length: <= 16 } news ? news : "";
        if (Sane(s.Cat) is { } cat) _cat = cat; else ScheduleCat(Ctx.Clock.UtcNow);
        if (Sane(s.Star) is { } star) _star = star; else ScheduleStar(Ctx.Clock.UtcNow);
        if (Sane(s.Wind) is { } wind) _wind = wind; else ScheduleWind(Ctx.Clock.UtcNow);
        // Хата — після розписів (замовлення на розпис мусять бачити колекцію) і після рівнів (дошка рахується від пасиву).
        LoadHouse(s.House);
        // Ремесло й пакети — наприкінці: їм потрібні рівні, розписи й глина.
        LoadCraft(s.Craft);
        LoadKiln(s.Kiln);
        LoadAlbum(s.Album);
        LoadFair(s.Fair);
        LoadGuild(s.Guild);
        _achQueue.Clear();
        foreach (var key in s.Achievements ?? []) if (key is { Length: > 0 and < 64 } && !_achQueue.Contains(key)) _achQueue.Add(key);
        _viewVersion++;
        // Після Load каталогів у виді нема (вид до збереження й після мусить збігатись): клієнт без них сам попросить look { catalog: true }.
        _catalogWanted = false;
    }

    /// <summary>Подія зі збереження: або ціле вікно з числами в межах сцени, або null — і тоді розклад від «зараз».</summary>
    static EventRow? Sane(EventRow? row) =>
        row is { } r && r.Until > r.At ? r with { A = Math.Clamp(r.A, 0, 100), B = Math.Clamp(r.B, 0, 100) } : null;

    static void Fill(HashSet<string> set, List<string>? from, Func<string, bool> known)
    {
        set.Clear();
        foreach (var k in from ?? [])
            if (k is not null && known(k)) set.Add(k);
    }

    // ---------- гачки дев'ятого оновлення (docs/games/specs/clicker-v9.md) ----------

    /// <summary>
    /// «Дивовижа»: щось рідкісне сталось (ідеальний обпал, серія з полиці, золотий віз, замовлення пана, гість на свято…).
    /// Реалізує пакет «Хата» (ClickerHouse.cs): кидок і колекція дивовиж. Без реалізації виклики просто зникають.
    /// Тригери: див. контракт v9 §Дивовижі.
    /// </summary>
    partial void Wonder(string trigger);

    // ---------- дрібниці ----------

    /// <summary>Додати без переповнення: глеків за місяці обпалів стає більше, ніж влазить у long.</summary>
    internal static long Sum(long a, long b) => b > 0 && a > long.MaxValue - b ? long.MaxValue : a + b;

    internal static long Mul(long a, long b) =>
        a <= 0 || b <= 0 ? 0 : a > long.MaxValue / b ? long.MaxValue : a * b;

    /// <summary>double → long зі стелею: (long) від 1e19 у C# дає сміття, а не максимум.</summary>
    internal static long ToLong(double v) =>
        !(v > 0) ? 0 : v >= 9.2e18 ? long.MaxValue : (long)v;

    /// <summary>
    /// Число → глеки: цілі (глек — штука), невід'ємні, без NaN і нескінченності. Те саме, що робив
    /// <see cref="ToLong"/> до дев'ятого оновлення, тільки без стелі long.
    /// </summary>
    internal static double ToPots(double v) => double.IsFinite(v) && v > 0 ? Math.Floor(v) : 0;

    static long? Num(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    static string Str(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
