namespace Hlechyky.Games.Impl;

/// <summary>
/// Слова Дядька Глека в підсумку Клавоперегонів (spec §6.6) і звання за швидкістю. Репліка йде у вид
/// (<c>result.say</c>), а не в балачку столу: коментарі в чаті дратували ще в «Скільки?». Вибір — через
/// <c>Ctx.Rng</c> кімнати, тож той самий сід дає ту саму репліку. Нік — підметом у називному, а дієслова без роду:
/// за столом і Оля, і Петро («доїхав» Олі не личить).
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

    /// <summary>Переможець; <c>true</c> у парі — репліка каже, що решта ще їде, тож коли дописали всі, її не беремо.</summary>
    static readonly (string Line, bool OthersStillTyping)[] Winner =
    [
        ("{nick} — {cpm} зн/хв. Клавіатура ще димить.", false),
        ("Перше місце — {nick}. Решта ще шукає літеру «ґ».", true),
        ("{cpm} зн/хв — {nick} друкує швидше, ніж Глек говорить.", false),
        ("Перше місце — {nick}. Комусь варто протерти окуляри й клавіатуру.", false),
        ("Трактор {nick} на фініші. Пахне бензином і перемогою.", false),
        ("Перше місце — {nick}. Шевченко був би радий, якби мав клавіатуру.", false),
        ("{nick} — {cpm}. Пальці окремо, голова окремо, і все одно перше місце.", false),
        ("{nick} на фініші, а в решти ще гарячі клавіші.", true),
    ];

    const string Clean = "{nick}: {cpm} зн/хв і жодного червоного. Так не буває. Було.";
    const string AllDone = "Усі дописали — це вже саме по собі свято.";
    const string Alone = "{nick} — один на трасі й перше місце на фініші. Чесно, але сумно.";

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
        "{cpm} зн/хв. Пальці розім’яті, можна й за стіл.",
        "{title}: {cpm} зн/хв. Глек записав олівцем — ще переб’єш.",
        "{cpm} зн/хв. Клавіатура ціла, текст дописаний — день удався.",
        "{cpm}. Не рекорд, зате чесно й по літері.",
    ];

    static readonly string[] Record =
    [
        "Рекорд! {cpm} зн/хв. Учорашній ти мовчки заздрить.",
        "Рекорд! {cpm} зн/хв. Глек аж окуляри протер.",
        "Новий рекорд — {cpm} зн/хв. Клавіші просять пощади.",
    ];

    const string SoloShort = "До фінішу — {pct} % тексту. Уривок цього разу переміг.";

    static string Fill(string line, string? nick, int cpm, string? title = null, int pct = 0) => line
        .Replace("{nick}", nick ?? "Хтось")
        .Replace("{cpm}", cpm.ToString())
        .Replace("{title}", title ?? Title(cpm))
        .Replace("{pct}", pct.ToString());

    /// <summary>
    /// Підсумок столу з переможцем. <paramref name="clean"/> — без жодної помилки; <paramref name="alone"/> — решта пішли;
    /// <paramref name="allDone"/> — дописали всі (тоді без реплік «решта ще їде», зате з «це вже свято»).
    /// </summary>
    public static string ForWinner(Random rng, string nick, int cpm, bool clean, bool alone, bool allDone)
    {
        string line;
        if (alone) line = Fill(Alone, nick, cpm);
        else if (clean) line = Fill(Clean, nick, cpm);
        else
        {
            var n = 0;
            foreach (var w in Winner) if (!(allDone && w.OthersStillTyping)) n++;
            var pick = rng.Next(n);
            line = "";
            foreach (var w in Winner)
            {
                if (allDone && w.OthersStillTyping) continue;
                if (pick-- == 0) { line = Fill(w.Line, nick, cpm); break; }
            }
        }
        return allDone && !alone ? line + " " + AllDone : line;
    }

    public static string ForNobody(Random rng) => Nobody[rng.Next(Nobody.Length)];

    /// <summary>Підсумок тренування: рекорд або звичайний заїзд.</summary>
    public static string ForSolo(Random rng, int cpm, bool record) =>
        record ? Fill(Record[rng.Next(Record.Length)], null, cpm) : Fill(Solo[rng.Next(Solo.Length)], null, cpm);

    public static string ForSoloUnfinished(int pct) => Fill(SoloShort, null, 0, pct: pct);
}
