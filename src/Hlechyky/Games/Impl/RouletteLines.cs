namespace Hlechyky.Games.Impl;

/// <summary>
/// Репліки Дядька Глека-круп'є (docs/games/specs/roulette.md §7.1). Жодних викликів моделі в Act/Tick — лише ці банки,
/// вибір — <c>Ctx.Rng</c>. Ніки — лише в називному й без дієслів минулого часу (як у flair.md).
/// </summary>
public static class RouletteLines
{
    public static readonly string[] Open =
        ["Робіть ваші ставки!", "Ставки, панове, ставки — колесо чекає", "Черепки на стіл — Глек крутить"];

    public static readonly string[] Hurry =
        ["Останні ставки! Хто не встиг — той дивиться", "П'ять секунд — і все, як у печі: що вліпив, те й випече"];

    public static readonly string[] Call =
        ["Ставки зроблено!", "Ставок більше нема — крутимо!", "Rien ne va plus! По-нашому — все, поїхали"];

    /// <summary>{0} — нік, {1} — число.</summary>
    public static readonly string[] Dance =
        ["Число в число — {0}! Глек іде в гопак", "{0} влучає просто в {1} — ну ти чаклун!", "Отакої, {0}! Тримайте мене семеро — танцюю"];

    public static readonly string[] Zero =
        ["Зеро! Усе зовнішнє — до Глека в глечик", "Нуль! Хе-хе, черепки до мене — на новий глечик", "Зеро, любі мої. Каса дякує"];

    /// <summary>Випало 00 (американське колесо) і хтось програв.</summary>
    public static readonly string[] ZeroZero =
        ["Подвійне зеро! Двічі нуль — двічі до Глека", "00! Американське колесо годує Глека", "Нуль і ще нуль — каса танцює"];

    public static readonly string[] Clap = ["Ну ви сьогодні й розійшлись — плачу!", "Каса плаче, а Глек платить"];

    public static readonly string[] Rake = ["Що впало, те пропало — до мене в глечик", "Червоне, чорне — а черепки мої"];

    public static readonly string[] Sigh = ["Ех… ніхто не ставить. Почекаю ще", "Колесо без ставок — як борщ без сметани"];

    public static readonly string[] Doze = ["Задрімаю, поки хтось не поставить… 💤"];

    /// <summary>{0} — нік.</summary>
    public const string AllInWon = "{0} ставить усе — і бере! Оце нерви";
    public const string AllInLost = "{0} ставить усе… а кулька каже «ні». Тримайся";

    /// <summary>Американське колесо: Глек чесно попереджає (стіл «🇺🇸 з 00» на старті, соло — на перемиканні).</summary>
    public const string UsWarning = "На американському колесі я забираю вдвічі більше. Любиш ризик — прошу";
    /// <summary>Соло: назад на європейське.</summary>
    public const string EuBack = "Європейське колесо — одне зеро. Розумний вибір, хоч Глекові й шкода";

    public const string BookStuck = "Каса заїла — це коло не крутимо";
    public const string NobodyPaid = "Черепків ні в кого не стало — не кручу";

    /// <summary>Число вголос — перше речення після розрахунку: «17, червоне!», «Зеро!», «Подвійне зеро!».</summary>
    public static string Number(int n) => n switch
    {
        0 => "Зеро!",
        RouletteCore.DoubleZero => "Подвійне зеро!",
        _ => $"{n}, {(RouletteCore.ColorOf(n) == "r" ? "червоне" : "чорне")}!",
    };

    public static string Pick(string[] bank, Random rng) => bank[rng.Next(bank.Length)];
}
