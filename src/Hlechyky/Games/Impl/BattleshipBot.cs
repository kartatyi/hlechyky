namespace Hlechyky.Games.Impl;

/// <summary>
/// «Глек підсідає» — бот на порожнє місце морського бою. Бачить рівно те, що й людина: влучання, промахи й
/// потоплені кораблі (чужого флоту не підглядає). Розумний настільки, наскільки грає досвідчений друг: підбитий
/// корабель добиває вздовж лінії, а шукає шаховим візерунком — найменший корабель, що лишився, у нього не
/// пролізе, тож половина поля відпадає сама.
/// </summary>
public static class BattleshipBot
{
    /// <summary>Імена ботів за столом — з 🤖, щоб ніхто не сплутав Глека з живою людиною.</summary>
    public static readonly string[] Names = ["Глек 🤖", "Макітра 🤖", "Горнятко 🤖"];

    /// <summary>Скільки бот «думає» над пострілом: миттєвий постріл читається як глюк, а довгий — нудить.</summary>
    public const int ThinkMs = 1000;

    /// <summary>Недобиті влучання поля: влучили, а корабель ще на плаву.</summary>
    public static List<int> Wounded(IReadOnlySet<int> hits, IEnumerable<int[]> sunk)
    {
        var dead = new HashSet<int>();
        foreach (var s in sunk) foreach (var c in s) dead.Add(c);
        var list = new List<int>();
        foreach (var c in hits) if (!dead.Contains(c)) list.Add(c);
        list.Sort();
        return list;
    }

    /// <summary>Куди стріляти по цьому полю; -1 — вільних клітинок нема.</summary>
    public static int Aim(Random rng, BattleshipSea sea, IReadOnlySet<int> hits, IReadOnlySet<int> misses, IEnumerable<int[]> sunk)
    {
        bool Free(int c) => c >= 0 && c < sea.Cells && !hits.Contains(c) && !misses.Contains(c);
        var wounded = Wounded(hits, sunk);
        if (wounded.Count > 0)
        {
            var cands = new List<int>();
            if (wounded.Count >= 2)
            {
                var (a, b) = (wounded[0], wounded[^1]);
                var row = wounded.TrueForAll(c => c / sea.W == a / sea.W);
                var col = wounded.TrueForAll(c => c % sea.W == a % sea.W);
                if (row)
                {
                    if (a % sea.W > 0 && Free(a - 1)) cands.Add(a - 1);
                    if (b % sea.W < sea.W - 1 && Free(b + 1)) cands.Add(b + 1);
                }
                else if (col)
                {
                    if (Free(a - sea.W)) cands.Add(a - sea.W);
                    if (Free(b + sea.W)) cands.Add(b + sea.W);
                }
            }
            if (cands.Count == 0)
                foreach (var c in wounded)
                {
                    var (x, y) = (c % sea.W, c / sea.W);
                    if (x > 0 && Free(c - 1)) cands.Add(c - 1);
                    if (x < sea.W - 1 && Free(c + 1)) cands.Add(c + 1);
                    if (y > 0 && Free(c - sea.W)) cands.Add(c - sea.W);
                    if (y < sea.H - 1 && Free(c + sea.W)) cands.Add(c + sea.W);
                }
            if (cands.Count > 0) return cands[rng.Next(cands.Count)];
        }
        // Шаховий візерунок: кожен корабель від двох клітинок неминуче стоїть хоч на одній «чорній».
        var hunt = new List<int>();
        for (var c = 0; c < sea.Cells; c++)
            if (Free(c) && ((c % sea.W + c / sea.W) & 1) == 0) hunt.Add(c);
        if (hunt.Count == 0)
            for (var c = 0; c < sea.Cells; c++)
                if (Free(c)) hunt.Add(c);
        return hunt.Count == 0 ? -1 : hunt[rng.Next(hunt.Count)];
    }
}

/// <summary>Слово Глека в стрічці: на потоплення й вибування (не на кожен постріл — стрічка не має галасувати).</summary>
public static class BattleshipLines
{
    static readonly string[] ShipName = ["Корабель", "Однопалубний", "Двопалубний", "Трипалубний", "Чотирипалубний"];

    /// <summary>{s} — «Трипалубний» (з великої, на початку речення).</summary>
    public static readonly string[] Sunk =
    [
        "{s} пішов пити чай з раками",
        "{s} тепер житло для карасів",
        "{s} ліг на дно подумати про життя",
        "{s} занурився без квитка назад",
        "{s} віддав якір — і все інше теж",
        "{s} пішов шукати Атлантиду",
        "{s} записався в підводний флот",
        "{s} тепер точно знає, яке тут дно",
        "{s} пірнув, як глек у криницю",
        "{s} відтепер рибальська легенда",
        "{s} — буль-буль, і тільки піна",
        "{s} взяв відпустку на дні. Безстрокову",
    ];

    /// <summary>{g} — нік у родовому («Олі»).</summary>
    public static readonly string[] Out =
    [
        "Флот {g} тепер годує рибу",
        "Від флоту {g} лишились самі бульбашки",
        "На мапі {g} тепер тільки хвилі",
        "Флот {g} поїхав у підводний санаторій",
        "Прощавай, флоте {g}! Раки вже накривають на стіл",
        "Флот {g} пішов на дно дружно, всім складом",
        "У {g} тепер не флот, а акваріум",
        "Капітанський кашкет {g} пливе сам",
    ];

    public static string ForSunk(Random rng, int size) =>
        Sunk[rng.Next(Sunk.Length)].Replace("{s}", size >= 1 && size < ShipName.Length ? ShipName[size] : ShipName[0]);

    public static string ForOut(Random rng, string nick) =>
        Out[rng.Next(Out.Length)].Replace("{g}", NickCases.Genitive(nick));
}
