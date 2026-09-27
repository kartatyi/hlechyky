namespace Hlechyky.Games.Impl;

/// <summary>
/// Слова Дядька Глека в підсумку Клавоперегонів (spec §6.6) і звання за швидкістю. Репліка йде у вид
/// (<c>result.say</c>), а не в балачку столу: коментарі в чаті дратували ще в «Скільки?». Вибір — через
/// <c>Ctx.Rng</c> кімнати, тож той самий сід дає ту саму репліку.
/// </summary>
public static class TyperaceLines
{
    /// <summary>Звання за знаками на хвилину: від «Равлика» до «Ракети». Міняти разом із <c>typerace.js</c> (TITLES).</summary>
    public static string Title(int cpm) => cpm switch
    {
        < 120 => "Равлик",
        < 200 => "Пішохід",
        < 280 => "Велосипед",
        < 360 => "Трактор",
        < 450 => "Мотоцикл",
        _ => "Ракета",
    };

    static readonly string[] Winner =
    [
        "{nick} — {cpm} зн/хв. Клавіатура ще димить.",
        "{nick} доїхав першим. Решта ще шукає літеру «ґ».",
        "{cpm} зн/хв — {nick} друкує швидше, ніж Глек говорить.",
        "Перший — {nick}. Комусь варто протерти окуляри й клавіатуру.",
        "Трактор {nick} на фініші. Пахне бензином і перемогою.",
        "{nick} перший. Шевченко був би радий, якби мав клавіатуру.",
        "{nick} — {cpm}. Пальці окремо, голова окремо, і все одно перший.",
    ];

    const string Clean = "{nick}: {cpm} зн/хв і жодного червоного. Так не буває. Було.";
    const string AllDone = "Усі дописали — це вже саме по собі свято.";
    const string Alone = "{nick} їхав сам і виграв сам. Чесно, але сумно.";

    static readonly string[] Nobody =
    [
        "Ніхто не дописав. Уривок переміг. Реванш?",
        "Текст виявився сильнішим. Буває, тримайтеся.",
    ];

    public const string Idle = "Ніхто й пальцем не ворухнув. Клавіатури відпочивають, Глек теж.";
    public const string Robots = "Хтось доїхав, але не по-людськи. Глек перевірив журнал і знизав плечима.";

    static readonly string[] Solo =
    [
        "{cpm} зн/хв. {title} — так і запишемо.",
    ];

    const string Record = "Рекорд! {cpm} зн/хв. Учорашній ти нервово курить.";
    const string SoloShort = "Не доїхав: {pct} % тексту. Уривок цього разу переміг.";

    static string Fill(string line, string? nick, int cpm, string? title = null, int pct = 0) => line
        .Replace("{nick}", nick ?? "Хтось")
        .Replace("{cpm}", cpm.ToString())
        .Replace("{title}", title ?? Title(cpm))
        .Replace("{pct}", pct.ToString());

    /// <summary>Підсумок столу з переможцем. <paramref name="clean"/> — без жодної помилки; <paramref name="alone"/> — решта пішли.</summary>
    public static string ForWinner(Random rng, string nick, int cpm, bool clean, bool alone, bool allDone)
    {
        string line;
        if (alone) line = Fill(Alone, nick, cpm);
        else if (clean) line = Fill(Clean, nick, cpm);
        else line = Fill(Winner[rng.Next(Winner.Length)], nick, cpm);
        return allDone && !alone ? line + " " + AllDone : line;
    }

    public static string ForNobody(Random rng) => Nobody[rng.Next(Nobody.Length)];

    /// <summary>Підсумок тренування: рекорд, звичайний заїзд або «не доїхав».</summary>
    public static string ForSolo(Random rng, int cpm, bool record) =>
        record ? Fill(Record, null, cpm) : Fill(Solo[rng.Next(Solo.Length)], null, cpm);

    public static string ForSoloUnfinished(int pct) => Fill(SoloShort, null, 0, pct: pct);
}
