namespace Hlechyky.Games.Impl;

/// <summary>
/// Репліки Дядька Глека-круп'є за «Двадцять одно» (docs/games/specs/blackjack.md §7.1). Жодних викликів моделі в
/// Act/Tick — лише ці банки, вибір — <c>Ctx.Rng</c>. Ніки — лише в називному й без дієслів минулого часу (як у flair.md).
/// </summary>
public static class BlackjackLines
{
    public static readonly string[] Open =
        ["Ставки, панове! Глек уже тасує", "Черепки на сукно — карти чекають", "Хто до Глека на двадцять одно?"];

    public static readonly string[] Hurry =
        ["Ще п'ять секунд — і роздаю", "Хто не поставив — той дивиться"];

    public static readonly string[] Deal =
        ["Роздаю! Рахуйте до двадцяти одного", "Карти пішли — не перебирайте", "Тримайте карти — і без фокусів"];

    /// <summary>Туз відкритий — страховки нема.</summary>
    public static readonly string[] NoInsurance =
        ["Туз у Глека. Страхуватись від мене? Ображаєш", "Туз зверху. Страховки не продаю — Глекові вір на слово"];

    /// <summary>Глек заглянув під туза чи десятку — блекджеку нема.</summary>
    public const string Peeked = "Заглянув — двадцяти одного в мене нема. Грайте";

    public static readonly string[] DealerBj =
        ["Двадцять одно в Глека! Черепки — до мене", "Туз і десятка — вибачайте, панове", "Двадцять одно з двох — Глек сьогодні в ударі"];

    /// <summary>{0} — скільки набрав Глек.</summary>
    public static readonly string[] Bust =
        ["{0} — перебрав! Плачу всім, хто встояв", "Ох, {0}… Глек перебрав — забирайте своє"];

    /// <summary>{0} — скільки набрав Глек.</summary>
    public static readonly string[] Clap = ["{0} у Глека — ваша взяла, плачу!", "{0}. Каса плаче, а Глек платить"];

    /// <summary>{0} — скільки набрав Глек.</summary>
    public static readonly string[] Rake = ["{0} у Глека. Що на сукні — те моє", "{0}! Черепки — до глечика", "{0}. Глек рахує, Глек забирає"];

    /// <summary>{0} — нік, {1} — виграш.</summary>
    public static readonly string[] Dance =
        ["{0} бере {1}! Глек іде в гопак", "Отакої, {0} — плюс {1}! Тримайте мене семеро"];

    /// <summary>{0} — нік, {1} — скільки карт.</summary>
    public const string Five = "{0} — двадцять одно з {1} карт! Оце руки";

    public const string Natural = "Двадцять одно з двох! У Глека платять один до одного";

    /// <summary>Блекджек на блекджек — Глекові (§2.5).</summary>
    public const string BjOnBj = "Двадцять одно на двадцять одно — старшинство за Глеком";

    /// <summary>{0} — нік.</summary>
    public const string Timeout = "{0} задумується — стоїть";

    public static readonly string[] Sigh = ["Ех… ніхто не ставить. Потасую ще", "Карти без ставок — як вареники без сметани"];

    public static readonly string[] Doze = ["Задрімаю, поки хтось не поставить… 💤"];

    public const string BookStuck = "Каса заїла — цю роздачу не роздаю";
    public const string NobodyPaid = "Черепків ні в кого не стало — не роздаю";

    public static string Pick(string[] bank, Random rng) => bank[rng.Next(bank.Length)];
}
