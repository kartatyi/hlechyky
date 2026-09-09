using System.Text;

namespace Hlechyky.Games.Impl;

/// <summary>Фішка, яку гравець кладе на дошку. Для порожньої (blank) літера — це та, яку гравець назвав.</summary>
public readonly record struct ScrabbleTile(int Cell, char Letter, bool Blank);

/// <summary>Слово, утворене ходом, і скільки воно принесло з усіма множниками.</summary>
public sealed record ScrabbleWord(string Text, int Score);

/// <summary>Прорахований хід: усі слова, підсумок (уже з бінго) і клітинки, куди лягли нові фішки.</summary>
public sealed record ScrabblePlay(IReadOnlyList<ScrabbleWord> Words, int Total, int[] Cells)
{
    /// <summary>Найдорожче слово ходу — з нього рахується ачівка «Ерудит» (30+ очок).</summary>
    public int Best => Words.Count == 0 ? 0 : Words.Max(w => w.Score);
}

/// <summary>
/// Мішок фішок. Розподіл — зі specs/scrabble.md, але приведений до обіцяних spec'ом 104 фішок:
/// у таблиці spec'а стовпчики сумуються в 111, а текст і підсумковий рядок кажуть 104, тому сім
/// найчастіших фішок (о, а, н, м, г, ч, ш) втратили по одній. Очки за літери — рівно ті, що в spec.
/// Разом виходить 104 фішки на 200 очок; апострофа й дефіса в грі нема взагалі.
/// </summary>
public static class ScrabbleBag
{
    /// <summary>Порожня фішка: на стійці вона '*', на дошці стає ВЕЛИКОЮ літерою і коштує нуль.</summary>
    public const char Blank = '*';

    /// <summary>Літера, скільки її в мішку, скільки вона коштує.</summary>
    public static readonly (char Letter, int Count, int Value)[] Table =
    [
        ('о', 9, 1), ('а', 7, 1), ('и', 7, 1), ('н', 6, 1), ('і', 6, 1),
        ('е', 5, 1), ('т', 5, 1), ('в', 5, 1), ('р', 5, 1), ('с', 5, 1),
        ('л', 4, 2), ('к', 4, 2), ('м', 3, 2), ('д', 3, 2), ('п', 3, 2), ('у', 3, 2),
        ('й', 2, 3), ('ь', 2, 3), ('з', 2, 3), ('б', 2, 3), ('я', 2, 3), ('г', 1, 3),
        ('ч', 1, 4), ('ш', 1, 4),
        ('ж', 1, 5), ('ц', 1, 5), ('х', 1, 5),
        ('є', 1, 6), ('ю', 1, 6), ('ї', 1, 6),
        ('щ', 1, 8), ('ф', 1, 8),
        ('ґ', 1, 10),
        (Blank, 2, 0),
    ];

    /// <summary>Скільки фішок у повному мішку.</summary>
    public static readonly int Total = Table.Sum(t => t.Count);

    static readonly Dictionary<char, int> Values = Table.ToDictionary(t => t.Letter, t => t.Value);

    /// <summary>
    /// Скільки коштує фішка. ВЕЛИКА літера на дошці — це порожня фішка, за яку очок не дають;
    /// невідомий символ теж нуль, бо дошку ми пишемо самі й сюрпризів там бути не має.
    /// </summary>
    public static int Value(char tile)
    {
        if (char.IsUpper(tile)) return 0;
        return Values.TryGetValue(tile, out var v) ? v : 0;
    }

    /// <summary>Повний перемішаний мішок. Випадковість — лише з переданого генератора кімнати.</summary>
    public static List<char> Fresh(Random rng)
    {
        var bag = new List<char>(Total);
        foreach (var (letter, count, _) in Table)
            for (var i = 0; i < count; i++) bag.Add(letter);
        Shuffle(bag, rng);
        return bag;
    }

    /// <summary>Перестановка Фішера–Єйтса на місці: тягнемо фішки з кінця списку.</summary>
    public static void Shuffle(List<char> bag, Random rng)
    {
        ArgumentNullException.ThrowIfNull(bag);
        ArgumentNullException.ThrowIfNull(rng);
        for (var i = bag.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (bag[i], bag[j]) = (bag[j], bag[i]);
        }
    }
}

/// <summary>
/// Дошка 15×15 і всі правила викладки: чи можна так покласти, які слова з цього вийшли і скільки
/// вони коштують. Про мішок, стійки, черги й словник тут не знають нічого — саме тому цю частину
/// можна перевіряти тестами без кімнати, фішка за фішкою.
/// </summary>
public sealed class ScrabbleBoard
{
    public const int Size = 15;
    public const int Cells = Size * Size;
    /// <summary>Центр дошки: перше слово має його накрити.</summary>
    public const int Centre = 7 * Size + 7;
    /// <summary>Скільки фішок тримає стійка.</summary>
    public const int RackSize = 7;
    /// <summary>Премія за викладені за один хід усі сім фішок.</summary>
    public const int BingoBonus = 50;
    /// <summary>Порожня клітинка дошки.</summary>
    public const char Free = '.';

    /// <summary>
    /// Класична розкладка бонусів: 'd' — літера ×2, 't' — літера ×3, 'D' — слово ×2, 'T' — слово ×3,
    /// '*' — центр (він же слово ×2). Малюнок симетричний, тому його простіше прочитати очима, ніж
    /// відновити з формули.
    /// </summary>
    static readonly string[] Layout =
    [
        "T..d...T...d..T",
        ".D...t...t...D.",
        "..D...d.d...D..",
        "d..D...d...D..d",
        "....D.....D....",
        ".t...t...t...t.",
        "..d...d.d...d..",
        "T..d...*...d..T",
        "..d...d.d...d..",
        ".t...t...t...t.",
        "....D.....D....",
        "d..D...d...D..d",
        "..D...d.d...D..",
        ".D...t...t...D.",
        "T..d...T...d..T",
    ];

    /// <summary>225 символів бонусів у тому самому порядку, що й клітинки. Константа: клієнт бере її з виду.</summary>
    public static readonly string Bonuses = string.Concat(Layout);

    readonly char[] _cells;

    public ScrabbleBoard()
    {
        _cells = new char[Cells];
        Array.Fill(_cells, Free);
    }

    /// <summary>Дошка з рядка (відновлення збереженого стану). Кривий рядок — порожня дошка, а не виняток.</summary>
    public ScrabbleBoard(string? text) : this()
    {
        if (text is null || text.Length != Cells) return;
        for (var i = 0; i < Cells; i++) _cells[i] = text[i];
    }

    /// <summary>Дошка так, як вона летить у вид: '.' — порожньо, мала літера — фішка, ВЕЛИКА — порожня фішка.</summary>
    public string Text => new(_cells);

    public bool IsEmpty => Array.TrueForAll(_cells, c => c == Free);

    public char At(int cell) => cell >= 0 && cell < Cells ? _cells[cell] : Free;

    /// <summary>
    /// Чи можна так покласти. Повертає або прорахований хід, або коротку людську відмову — жодного
    /// винятку: нелегальний хід у нас не подія, а звичайна відповідь гравцеві.
    /// </summary>
    public (ScrabblePlay? Play, string? Error) Check(IReadOnlyList<ScrabbleTile> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        if (tiles.Count == 0) return (null, "Поклади хоч одну фішку");
        if (tiles.Count > RackSize) return (null, "За хід кладуть щонайбільше сім фішок");

        var fresh = new HashSet<int>();
        foreach (var t in tiles)
        {
            if (t.Cell < 0 || t.Cell >= Cells) return (null, "Такої клітинки на дошці нема");
            if (!fresh.Add(t.Cell)) return (null, "Дві фішки на одну клітинку не кладуть");
            if (_cells[t.Cell] != Free) return (null, "Ця клітинка вже зайнята");
        }

        var oneRow = tiles.Select(t => t.Cell / Size).Distinct().Count() == 1;
        var oneCol = tiles.Select(t => t.Cell % Size).Distinct().Count() == 1;
        if (!oneRow && !oneCol) return (null, "Фішки мають лягти в один рядок або стовпець");

        // Дошка з новими фішками: далі і дірки, і слова рахуємо вже по ній.
        var next = (char[])_cells.Clone();
        foreach (var t in tiles) next[t.Cell] = t.Blank ? char.ToUpperInvariant(t.Letter) : char.ToLowerInvariant(t.Letter);

        var horizontal = oneRow;
        var step = horizontal ? 1 : Size;
        var (from, to) = (tiles.Min(t => t.Cell), tiles.Max(t => t.Cell));
        for (var c = from; c <= to; c += step)
            if (next[c] == Free) return (null, "У слові дірка");

        if (IsEmpty)
        {
            if (!fresh.Contains(Centre)) return (null, "Перше слово кладуть через центр");
        }
        else if (!tiles.Any(t => Around(t.Cell).Any(n => _cells[n] != Free)))
        {
            return (null, "Слово має торкатись того, що вже на дошці");
        }

        var words = new List<ScrabbleWord>();
        if (tiles.Count == 1)
        {
            // Одна фішка не має власного напрямку: слово могло скластись і вздовж, і впоперек.
            Collect(words, next, tiles[0].Cell, horizontal: true, fresh);
            Collect(words, next, tiles[0].Cell, horizontal: false, fresh);
        }
        else
        {
            Collect(words, next, tiles[0].Cell, horizontal, fresh);
            foreach (var t in tiles) Collect(words, next, t.Cell, !horizontal, fresh);
        }
        if (words.Count == 0) return (null, "Слово має бути щонайменше з двох літер");

        var total = words.Sum(w => w.Score) + (tiles.Count == RackSize ? BingoBonus : 0);
        return (new ScrabblePlay(words, total, [.. fresh.Order()]), null);
    }

    /// <summary>Покласти перевірені фішки на дошку.</summary>
    public void Apply(IReadOnlyList<ScrabbleTile> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        foreach (var t in tiles)
            if (t.Cell >= 0 && t.Cell < Cells)
                _cells[t.Cell] = t.Blank ? char.ToUpperInvariant(t.Letter) : char.ToLowerInvariant(t.Letter);
    }

    /// <summary>Зняти фішки з клітинок (оскарження в режимі малого словника).</summary>
    public void Clear(IReadOnlyList<int> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        foreach (var c in cells)
            if (c >= 0 && c < Cells) _cells[c] = Free;
    }

    /// <summary>Сусіди клітинки по чотирьох боках, не вилазячи за край рядка.</summary>
    static IEnumerable<int> Around(int cell)
    {
        var (x, y) = (cell % Size, cell / Size);
        if (x > 0) yield return cell - 1;
        if (x < Size - 1) yield return cell + 1;
        if (y > 0) yield return cell - Size;
        if (y < Size - 1) yield return cell + Size;
    }

    /// <summary>Додає слово, що проходить через клітинку в заданому напрямку, якщо воно довше за одну літеру.</summary>
    static void Collect(List<ScrabbleWord> words, char[] cells, int cell, bool horizontal, HashSet<int> fresh)
    {
        var step = horizontal ? 1 : Size;
        var start = cell;
        while (true)
        {
            var back = start - step;
            if (back < 0) break;
            if (horizontal && back / Size != start / Size) break;
            if (cells[back] == Free) break;
            start = back;
        }

        var text = new StringBuilder(Size);
        var sum = 0;
        var multiplier = 1;
        for (var c = start; c < Cells; c += step)
        {
            if (horizontal && c / Size != start / Size) break;
            if (cells[c] == Free) break;
            text.Append(char.ToLowerInvariant(cells[c]));
            var value = ScrabbleBag.Value(cells[c]);
            // Бонус працює один раз — того ходу, коли на клітинку лягла фішка.
            if (fresh.Contains(c))
                switch (Bonuses[c])
                {
                    case 'd': value *= 2; break;
                    case 't': value *= 3; break;
                    case 'D' or '*': multiplier *= 2; break;
                    case 'T': multiplier *= 3; break;
                }
            sum += value;
        }

        if (text.Length < 2) return;
        words.Add(new ScrabbleWord(text.ToString(), sum * multiplier));
    }
}
