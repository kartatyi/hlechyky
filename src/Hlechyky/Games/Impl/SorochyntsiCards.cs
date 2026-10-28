namespace Hlechyky.Games.Impl;

/// <summary>
/// Крамниця зі сталим id 0–89 (контракт §1.1): рівень 1–3, бонус-товар (0–4 буденні, <see cref="SorochyntsiCards.Fern"/>
/// — цвіт папороті), ⭐, козацька позначка ⚔ (0/1) і ціна в п'яти буденних товарах (цвіту в цінах не буває).
/// </summary>
public sealed record SoCard(int Id, int Tier, int Bonus, int Pts, int K, int[] Cost, string Name);

/// <summary>Гість ярмарку (§1.2): вимагає знижок (куплених крамниць), не жетонів; дає 3 ⭐.</summary>
public sealed record SoGuest(int Id, string Name, int[] Need);

/// <summary>
/// Сталі «Сорочинського ярмарку» без Ctx (контракт §1, §4): товари й відмінки, колода 90 крамниць з id, гості, банк за
/// кількістю гравців, відбиток колоди. Клієнт тримає те саме в <c>web/games/sorochyntsi/consts.js</c> — звіряють
/// <see cref="DeckSig"/> (тест).
/// </summary>
public static class SorochyntsiCards
{
    // ---------- товари ----------

    /// <summary>Буденні товари (жетони й знижки) у незмінному порядку §1.</summary>
    public const int Linen = 0, Fish = 1, Herbs = 2, Clay = 3, Wool = 4;
    /// <summary>Шостий жетон — дукат (джокер); шоста знижка — цвіт папороті. Обидва мають індекс 5 у своїх масивах.</summary>
    public const int Gold = 5, Fern = 5;
    public static readonly string[] Goods = ["linen", "fish", "herbs", "clay", "wool"];
    /// <summary>Жетони: товари + дукат.</summary>
    public static readonly string[] Tokens = ["linen", "fish", "herbs", "clay", "wool", "gold"];
    /// <summary>Знижки: товари + цвіт папороті.</summary>
    public static readonly string[] Bonuses = ["linen", "fish", "herbs", "clay", "wool", "fern"];

    public static readonly string[] GoodName = ["полотно", "риба", "зілля", "глина", "вовна", "цвіт папороті"];
    /// <summary>Родовий після числа — «бракує 2 риби», «бере 2 вовни».</summary>
    public static readonly string[] GoodGen = ["полотна", "риби", "зілля", "глини", "вовни", "цвіту папороті"];
    /// <summary>Знахідний — «бере рибу, зілля й глину».</summary>
    public static readonly string[] GoodAcc = ["полотно", "рибу", "зілля", "глину", "вовну", "цвіт папороті"];
    public static readonly string[] Roman = ["I", "II", "III"];

    public static int GoodIdx(string? name) => name is null ? -1 : Array.IndexOf(Goods, name);
    public static int TokenIdx(string? name) => name is null ? -1 : Array.IndexOf(Tokens, name);

    // ---------- банк і межі ----------

    public const int MaxSeats = 4, HandMax = 10, ReserveMax = 3, Row = 4, TwoMin = 4, GoldPile = 5, MaxRounds = 40;
    public const int GuestPoints = 3, StarshynaPoints = 3, StarshynaIcons = 3;
    public static readonly int[] TierSize = [40, 30, 20];
    public static readonly int[] Targets = [14, 16, 18];

    /// <summary>Жетонів кожного буденного товару в банку за кількістю гравців (§1.3).</summary>
    public static int Pile(int players) => players switch { <= 2 => 4, 3 => 5, _ => 7 };

    /// <summary>1 жетон, 2 жетони, 5 жетонів.</summary>
    public static string Plural(int n, string one, string few, string many)
    {
        var a = Math.Abs(n) % 100;
        var b = a % 10;
        if (a > 10 && a < 20) return many;
        if (b == 1) return one;
        if (b is >= 2 and <= 4) return few;
        return many;
    }

    public static string TokenWord(int n) => Plural(n, "жетон", "жетони", "жетонів");

    // ---------- колода (§1.1, те саме, що DECK у consts.js) ----------

    public static readonly SoCard[] Deck =
    [
        new(0, 1, Linen, 0, 0, [0, 1, 1, 1, 1], "Прядка"),
        new(1, 1, Linen, 0, 0, [0, 1, 1, 1, 2], "Мотки льону"),
        new(2, 1, Linen, 0, 0, [0, 1, 2, 0, 2], "Ятка ниток"),
        new(3, 1, Linen, 0, 1, [1, 0, 1, 3, 0], "Білильниця"),
        new(4, 1, Linen, 0, 0, [0, 1, 0, 2, 0], "Хустки"),
        new(5, 1, Linen, 0, 0, [0, 0, 2, 2, 0], "Ткаля з хутора"),
        new(6, 1, Linen, 0, 0, [0, 3, 0, 0, 0], "Рушничок"),
        new(7, 1, Linen, 1, 0, [0, 0, 0, 4, 0], "Полотняний рядок"),
        new(8, 1, Fish, 0, 0, [1, 0, 1, 1, 1], "Рибалка з Псла"),
        new(9, 1, Fish, 0, 0, [2, 0, 1, 1, 1], "Тараня"),
        new(10, 1, Fish, 0, 0, [2, 0, 1, 2, 0], "Карасі в кошику"),
        new(11, 1, Fish, 0, 1, [0, 1, 0, 1, 3], "Вудильник"),
        new(12, 1, Fish, 0, 0, [0, 0, 1, 0, 2], "В’ялена риба"),
        new(13, 1, Fish, 0, 0, [0, 0, 0, 2, 2], "Раки"),
        new(14, 1, Fish, 0, 0, [0, 0, 3, 0, 0], "Сітки й верші"),
        new(15, 1, Fish, 1, 0, [0, 0, 0, 0, 4], "Юшка в казанку"),
        new(16, 1, Herbs, 0, 0, [1, 1, 0, 1, 1], "Травниця"),
        new(17, 1, Herbs, 0, 0, [1, 2, 0, 1, 1], "Пучки м’яти"),
        new(18, 1, Herbs, 0, 0, [0, 2, 0, 1, 2], "Ромашка"),
        new(19, 1, Herbs, 0, 1, [3, 0, 1, 0, 1], "Ятка насіння"),
        new(20, 1, Herbs, 0, 0, [2, 0, 0, 1, 0], "Часник і цибуля"),
        new(21, 1, Herbs, 0, 0, [2, 0, 0, 0, 2], "Деревій"),
        new(22, 1, Herbs, 0, 0, [0, 0, 0, 3, 0], "Віночки"),
        new(23, 1, Herbs, 1, 0, [4, 0, 0, 0, 0], "Квітчаний ряд"),
        new(24, 1, Clay, 0, 0, [1, 1, 1, 0, 1], "Глечики"),
        new(25, 1, Clay, 0, 0, [1, 1, 2, 0, 1], "Макітри"),
        new(26, 1, Clay, 0, 0, [2, 0, 2, 0, 1], "Свищики-півники"),
        new(27, 1, Clay, 0, 1, [1, 3, 0, 1, 0], "Миски"),
        new(28, 1, Clay, 0, 0, [0, 2, 0, 0, 1], "Глиняні іграшки"),
        new(29, 1, Clay, 0, 0, [2, 2, 0, 0, 0], "Горнята"),
        new(30, 1, Clay, 0, 0, [0, 0, 0, 0, 3], "Ринки й покришки"),
        new(31, 1, Clay, 1, 0, [0, 4, 0, 0, 0], "Куманці"),
        new(32, 1, Wool, 0, 0, [1, 1, 1, 1, 0], "Вівчар"),
        new(33, 1, Wool, 0, 0, [1, 1, 1, 2, 0], "Мішок вовни"),
        new(34, 1, Wool, 0, 0, [1, 2, 0, 2, 0], "Смушкова шапка"),
        new(35, 1, Wool, 0, 1, [0, 1, 3, 0, 1], "Панчохи"),
        new(36, 1, Wool, 0, 0, [1, 0, 2, 0, 0], "Пастух з ягням"),
        new(37, 1, Wool, 0, 0, [0, 2, 2, 0, 0], "Ліжник"),
        new(38, 1, Wool, 0, 0, [3, 0, 0, 0, 0], "Валянки"),
        new(39, 1, Wool, 1, 0, [0, 0, 4, 0, 0], "Кептарик"),
        new(40, 2, Linen, 1, 1, [0, 2, 2, 0, 3], "Ткацький верстат"),
        new(41, 2, Linen, 1, 0, [2, 0, 3, 3, 0], "Вибійка"),
        new(42, 2, Linen, 2, 0, [0, 1, 0, 4, 2], "Сорочечниця"),
        new(43, 2, Linen, 2, 1, [0, 3, 5, 0, 0], "Кролевецькі рушники"),
        new(44, 2, Linen, 2, 0, [0, 0, 0, 0, 5], "Полотняний віз"),
        new(45, 2, Linen, 3, 0, [6, 0, 0, 0, 0], "Вишивальниці"),
        new(46, 2, Fish, 1, 1, [3, 0, 2, 2, 0], "Рибна лава"),
        new(47, 2, Fish, 1, 0, [0, 2, 0, 3, 3], "Чумацька тараня"),
        new(48, 2, Fish, 2, 0, [2, 0, 1, 0, 4], "Ставок сотника"),
        new(49, 2, Fish, 2, 1, [0, 0, 3, 5, 0], "Коптильня"),
        new(50, 2, Fish, 2, 0, [5, 0, 0, 0, 0], "Рибальська артіль"),
        new(51, 2, Fish, 3, 0, [0, 6, 0, 0, 0], "Осетер із Дніпра"),
        new(52, 2, Herbs, 1, 1, [0, 3, 0, 2, 2], "Знахарка"),
        new(53, 2, Herbs, 1, 0, [3, 0, 2, 0, 3], "Сушарня трав"),
        new(54, 2, Herbs, 2, 0, [4, 2, 0, 1, 0], "Травник-аптекар"),
        new(55, 2, Herbs, 2, 1, [0, 0, 0, 3, 5], "Город попаді"),
        new(56, 2, Herbs, 2, 0, [0, 5, 0, 0, 0], "Медова м’ята"),
        new(57, 2, Herbs, 3, 0, [0, 0, 6, 0, 0], "Зелений ряд"),
        new(58, 2, Clay, 1, 1, [2, 0, 3, 0, 2], "Гончарний круг"),
        new(59, 2, Clay, 1, 0, [3, 3, 0, 2, 0], "Гончарна піч"),
        new(60, 2, Clay, 2, 0, [0, 4, 2, 0, 1], "Розписані миски"),
        new(61, 2, Clay, 2, 1, [5, 0, 0, 0, 3], "Кахлярня"),
        new(62, 2, Clay, 2, 0, [0, 0, 5, 0, 0], "Глиняний ряд"),
        new(63, 2, Clay, 3, 0, [0, 0, 0, 6, 0], "Опішнянські майстри"),
        new(64, 2, Wool, 1, 1, [2, 2, 0, 3, 0], "Сукновальня"),
        new(65, 2, Wool, 1, 0, [0, 3, 3, 0, 2], "Кожухар"),
        new(66, 2, Wool, 2, 0, [1, 0, 4, 2, 0], "Килимарня"),
        new(67, 2, Wool, 2, 1, [3, 5, 0, 0, 0], "Отара з полонини"),
        new(68, 2, Wool, 2, 0, [0, 0, 0, 5, 0], "Шаповал"),
        new(69, 2, Wool, 3, 0, [0, 0, 0, 0, 6], "Свити на продаж"),
        new(70, 3, Linen, 4, 0, [0, 0, 7, 0, 0], "Рушникова світлиця"),
        new(71, 3, Linen, 4, 1, [3, 6, 0, 3, 0], "Вишиванки на весь ярмарок"),
        new(72, 3, Linen, 5, 0, [3, 0, 0, 0, 7], "Купець полотняний"),
        new(73, 3, Fish, 4, 0, [0, 0, 0, 7, 0], "Дніпровські рибалки"),
        new(74, 3, Fish, 4, 1, [0, 3, 6, 0, 3], "Рибний ряд"),
        new(75, 3, Fish, 5, 0, [7, 3, 0, 0, 0], "Обоз із Криму"),
        new(76, 3, Herbs, 4, 0, [0, 0, 0, 0, 7], "Знахарська хата"),
        new(77, 3, Herbs, 4, 1, [3, 0, 3, 6, 0], "Купальське зілля"),
        new(78, 3, Herbs, 5, 0, [0, 7, 3, 0, 0], "Монастирський сад"),
        new(79, 3, Clay, 4, 0, [7, 0, 0, 0, 0], "Гончарний кут"),
        new(80, 3, Clay, 4, 1, [0, 3, 0, 3, 6], "Майстер-кахляр"),
        new(81, 3, Clay, 5, 0, [0, 0, 7, 3, 0], "Цех гончарів"),
        new(82, 3, Wool, 4, 0, [0, 7, 0, 0, 0], "Полонинська отара"),
        new(83, 3, Wool, 4, 1, [6, 0, 3, 0, 3], "Решетилівські килими"),
        new(84, 3, Wool, 5, 0, [0, 0, 0, 7, 3], "Цех кравців"),
        new(85, 3, Fern, 3, 1, [3, 3, 3, 2, 0], "Червона свитка"),
        new(86, 3, Fern, 3, 0, [0, 3, 3, 3, 2], "Ворожка з Диканьки"),
        new(87, 3, Fern, 3, 0, [2, 0, 3, 3, 3], "Скарб під папороттю"),
        new(88, 3, Fern, 3, 1, [3, 2, 0, 3, 3], "Чорт у корчмі"),
        new(89, 3, Fern, 3, 0, [3, 3, 2, 0, 3], "Купальський вогонь"),
    ];

    public static SoCard CardOf(int id) => Deck[id];
    public static bool IsCard(int id) => id >= 0 && id < Deck.Length;

    /// <summary>
    /// Відбиток колоди (§1.1): FNV-1a 32 над ASCII-рядком <c>id:tier:bonus:pts:k:c0,c1,c2,c3,c4</c> для кожної карти за id,
    /// через «;». Клієнт рахує те саме (<c>deckSig()</c> у consts.js).
    /// </summary>
    public static string DeckSig() => Sig(Deck);

    public static string Sig(IEnumerable<SoCard> deck)
    {
        var s = string.Join(";", deck.Select(c => $"{c.Id}:{c.Tier}:{Bonuses[c.Bonus]}:{c.Pts}:{c.K}:{string.Join(",", c.Cost)}"));
        var h = 0x811c9dc5u;
        foreach (var ch in s)
        {
            h ^= ch;
            h = unchecked(h * 0x01000193u);
        }
        return h.ToString("x8");
    }

    // ---------- гості (§1.2) ----------

    public static readonly SoGuest[] Guests =
    [
        new(0, "Солопій Черевик", [4, 4, 0, 0, 0]),
        new(1, "Хівря", [0, 4, 4, 0, 0]),
        new(2, "Параска", [0, 0, 4, 4, 0]),
        new(3, "Грицько Голопупенко", [0, 0, 0, 4, 4]),
        new(4, "Кум Цибуля", [4, 0, 0, 0, 4]),
        new(5, "Попович Афанасій Іванович", [3, 3, 3, 0, 0]),
        new(6, "Циган-міняйло", [0, 3, 3, 3, 0]),
        new(7, "Пан сотник", [0, 0, 3, 3, 3]),
        new(8, "Кобзар", [3, 0, 0, 3, 3]),
        new(9, "Писар із волості", [3, 3, 0, 0, 3]),
    ];
}
