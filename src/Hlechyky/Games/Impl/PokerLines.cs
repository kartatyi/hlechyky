namespace Hlechyky.Games.Impl;

/// <summary>Глек-круп'є в балачці столу: коротко, не на кожну роздачу. {0}, {1}… — імена й суми.</summary>
public static class PokerLines
{
    static readonly string[] BigPot =
    [
        "{0} згрібає {1} — такий банк і в ярмарок не щодня 🏺",
        "Ого, {1} фішок поїхали до {0}. Хтось сьогодні вечеряє на два борщі",
        "{0} забирає {1}. Я б на вашому місці вже боявся {0}",
    ];
    static readonly string[] AllIn =
    [
        "Олл-ін і колл! Карти на стіл — дивимось, хто сьогодні з глечиком, а хто з черепками",
        "Усі фішки в центрі. Дихайте глибше, зараз буде дошка",
        "О, пішло-поїхало: олл-ін! Я навіть чай відставив",
    ];
    static readonly string[] BadBeat =
    [
        "{0} мав {2}% і програв — ось вам і покер. {1}, свічку поставиш?",
        "Бед-біт! У {0} було {2}%, а ріка принесла {1} диво",
        "{1} витягує з {2}% проти {0}. Карти — вони такі, без совісті",
    ];
    static readonly string[] Royal = ["Роял-флеш у {0}! Я таке бачив раз, і то уві сні 👑", "{0} збирає роял-флеш. Знімайте шапки 👑"];
    static readonly string[] Quads = ["Каре в {0} — {1}. Чотири, як ніжки в лави", "{0} кладе каре. Хтось тут грав нечесно — з долею"];
    static readonly string[] Bust =
    [
        "{0} вибуває — {1}-е місце. Не сумуй, чай ще гарячий",
        "{0} покидає турнір ({1}-е місце). Фішки пішли до добрих людей",
    ];
    static readonly string[] Back = ["{0} повернувся ☕ — фішки скучили", "О, {0} знову з нами. Чай допив?"];

    static string Pick(string[] lines, Random rng, params object[] args) => string.Format(lines[rng.Next(lines.Length)], args);

    public static string BigPotLine(Random rng, string who, int amount) => Pick(BigPot, rng, who, amount);
    public static string AllInLine(Random rng) => Pick(AllIn, rng);
    public static string BadBeatLine(Random rng, string loser, string winner, int pct) => Pick(BadBeat, rng, loser, winner, pct);
    public static string RoyalLine(Random rng, string who) => Pick(Royal, rng, who);
    public static string QuadsLine(Random rng, string who, string hand) => Pick(Quads, rng, who, hand);
    public static string BustLine(Random rng, string who, int place) => Pick(Bust, rng, who, place);
    public static string BackLine(Random rng, string who) => Pick(Back, rng, who);
}
