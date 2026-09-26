namespace Hlechyky.Games.Impl;

/// <summary>
/// Кладка на березі — одна стежкова клітинка над водою, звідки пускають вінок. <see cref="Name"/> — для фішки
/// списку, <see cref="Where"/> — для новин («Хтось пустив вінок під вербою»).
/// </summary>
public sealed record KupalaSpot(int I, string Name, string Where, string Emoji, int X, int Y, int Cell);

/// <summary>
/// Галявина купальської ночі: одна ручна мапа 30×20 — ліс згори, луг із чотирма вогнищами, берег із вісьмома
/// кладками й річка знизу. Мапа стала, тож прохідність, кладки, вогнища й таблиця «з клітинки до цілі — перший
/// крок» для ботів рахуються раз на процес.
/// </summary>
public static class KupalaMap
{
    public const int W = 30, H = 20, Cell = 32, Cells = W * H;
    public const int WorldW = W * Cell, WorldH = H * Cell;
    /// <summary>У таблиці <see cref="NextHop"/>: недосяжно або вже на місці.</summary>
    public const byte NoHop = 255;
    /// <summary>Рядок берега з кладками; під ним — річка.</summary>
    public const int BankRow = 14;
    /// <summary>По цій висоті пливуть вінки: 12 одиниць нижче від краю берега.</summary>
    public const int WreathY = (BankRow + 1) * Cell + 12;
    /// <summary>Рядки лісу, де серед дерев зацвітає папороть.</summary>
    public const int ForestFrom = 1, ForestTo = 4;

    /// <summary>
    /// Легенда: <c>#</c> тин і лісова хаща по краю, <c>.</c> трава, <c>=</c> берег (прохідні — ці дві й кладки
    /// <c>0</c>…<c>7</c>), <c>T</c> дерево, <c>F</c> вогнище, <c>b</c> кущ, <c>Y</c> копиця, <c>M</c> Марена (опудало),
    /// <c>V</c> верба, <c>w</c> річка, <c>r</c> очерет на тому березі. Перевірено тестом: 343 прохідні клітинки, усі
    /// з'єднані.
    /// </summary>
    public static readonly string[] Rows =
    [
        "##############################",
        "#TT.TTT..TT.TTT..T.TTTT.TT.TT#",
        "#T...T....T...T.....T...T...T#",
        "#..T...T.....T...T.....T...T.#",
        "#.....T...........T..........#",
        "#..b......................b..#",
        "#..........M.................#",
        "#....F..............Y...F....#",
        "#............................#",
        "#..Y.........F..........b....#",
        "#.......b....................#",
        "#............................#",
        "#...................F........#",
        "#...V......V..........V....b.#",
        "#=0===1==2===3===4==5===6==7=#",
        "wwwwwwwwwwwwwwwwwwwwwwwwwwwwww",
        "wwwwwwwwwwwwwwwwwwwwwwwwwwwwww",
        "wwwwwwwwwwwwwwwwwwwwwwwwwwwwww",
        "wwwwwwwwwwwwwwwwwwwwwwwwwwwwww",
        "rrrrrrrrrrrrrrrrrrrrrrrrrrrrrr",
    ];

    static readonly (string Name, string Where, string Emoji)[] SpotNames =
    [
        ("Верба", "під вербою", "🌳"),
        ("Кладка", "з кладки", "🪵"),
        ("Човен", "біля човна", "🛶"),
        ("Брід", "біля броду", "🪨"),
        ("Круча", "з кручі", "⛰️"),
        ("Вогнище", "біля вогнища", "🔥"),
        ("Лоза", "з-під лози", "🌾"),
        ("Млинок", "біля млинка", "⚙️"),
    ];

    /// <summary>Імена селян: тасуються на кожен раунд, беруться перші N. Ім'я гравця — таке саме, як у бота.</summary>
    public static readonly string[] Names =
    [
        "Параска", "Микола", "Ганна", "Степан", "Одарка", "Тарас", "Марічка", "Гриць",
        "Оксана", "Панас", "Домаха", "Іванко", "Соломія", "Назар", "Мотря", "Богдан",
        "Ярина", "Остап", "Христя", "Данило", "Марта", "Максим", "Устя", "Юрко",
        "Килина", "Левко", "Наталка", "Роман", "Пріська", "Андрійко", "Гафія", "Василь",
        "Софійка", "Тиміш", "Орися", "Прокіп", "Настя", "Лесь", "Явдоха", "Захар",
        "Меланка", "Матвій", "Феся", "Сашко", "Текля", "Ігнат", "Зоряна", "Йосип",
        "Уляна", "Федь", "Люба", "Влас", "Дарина", "Кузьма", "Олеся", "Сидір",
        "Марійка", "Петрик", "Ганнуся", "Демко", "Варка", "Славко", "Катря", "Михась",
    ];

    /// <summary>Кладки за номером: ліві — 0…2, середні — 3…4, праві — 5…7.</summary>
    public static readonly KupalaSpot[] Spots;
    public static readonly int[] LeftSpots = [0, 1, 2], MidSpots = [3, 4], RightSpots = [5, 6, 7];

    /// <summary>Вогнища — центри клітинок <c>F</c>, у порядку рядків.</summary>
    public static readonly (int X, int Y)[] Fires;

    public static readonly bool[] Pass = new bool[Cells];
    public static readonly int[] Walkable;
    /// <summary>Клітинка → номер кладки; -1 — не кладка.</summary>
    public static readonly int[] SpotOf = new int[Cells];
    /// <summary>Для кожного вогнища — прохідні клітинки на відстані 1–2 (Чебишев): там боти гріються й танцюють.</summary>
    public static readonly int[][] FireRing;
    /// <summary>Прохідні клітинки лісу, куди не сягає жодне вогнище, — де може зацвісти папороть.</summary>
    public static readonly int[] FernCells;

    static readonly Lazy<byte[]> Hops = new(BuildHops, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>«З клітинки <c>cell</c> до цілі <c>target</c> — перший крок», індекс <c>target * Cells + cell</c>.</summary>
    public static byte[] NextHop => Hops.Value;

    static KupalaMap()
    {
        var walk = new List<int>();
        var fires = new List<(int, int)>();
        var spots = new KupalaSpot?[SpotNames.Length];
        var forest = new List<int>();
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var c = Rows[y][x];
                var i = y * W + x;
                SpotOf[i] = -1;
                Pass[i] = c is '.' or '=' or (>= '0' and <= '7');
                if (Pass[i]) walk.Add(i);
                if (c == 'F') fires.Add((x * Cell + Cell / 2, y * Cell + Cell / 2));
                if (c is >= '0' and <= '7')
                {
                    var k = c - '0';
                    SpotOf[i] = k;
                    var (name, where, emoji) = SpotNames[k];
                    spots[k] = new KupalaSpot(k, name, where, emoji, x, y, i);
                }
                if (Pass[i] && y >= ForestFrom && y <= ForestTo) forest.Add(i);
            }
        // папороть цвіте лише в справжній темряві: клітинки, куди сягає вогнище (з мерехтінням), — не для неї
        var reach = (long)(KupalaCore.FireR + 8) * (KupalaCore.FireR + 8);
        forest.RemoveAll(c => fires.Any(f =>
            (long)(CenterX(c) - f.Item1) * (CenterX(c) - f.Item1) + (long)(CenterY(c) - f.Item2) * (CenterY(c) - f.Item2) <= reach));
        Walkable = [.. walk];
        Fires = [.. fires];
        Spots = [.. spots.Select((s, k) => s ?? throw new InvalidOperationException($"на мапі нема кладки {k}"))];
        FernCells = [.. forest];
        FireRing = new int[Fires.Length][];
        for (var k = 0; k < Fires.Length; k++)
        {
            int fx = Fires[k].X / Cell, fy = Fires[k].Y / Cell;
            var ring = new List<int>();
            foreach (var cell in Walkable)
            {
                int d = Math.Max(Math.Abs(cell % W - fx), Math.Abs(cell / W - fy));
                if (d is >= 1 and <= 2 && SpotOf[cell] < 0) ring.Add(cell);
            }
            FireRing[k] = [.. ring];
        }
    }

    public static int CenterX(int cell) => cell % W * Cell + Cell / 2;
    public static int CenterY(int cell) => cell / W * Cell + Cell / 2;

    public static int CellOf(int x, int y) => Math.Clamp(y / Cell, 0, H - 1) * W + Math.Clamp(x / Cell, 0, W - 1);

    public static bool PassableAt(int x, int y) =>
        x >= 0 && y >= 0 && x < WorldW && y < WorldH && Pass[y / Cell * W + x / Cell];

    /// <summary>Вода — туди падає головешка й гасне.</summary>
    public static bool WaterAt(int x, int y) =>
        x >= 0 && y >= 0 && x < WorldW && y < WorldH && Rows[y / Cell][x / Cell] == 'w';

    /// <summary>Коробка селянина 16×16 із центром (x, y) — усі чотири кути в прохідних клітинках (як у Юрмі).</summary>
    public static bool BoxFits(int x, int y) =>
        PassableAt(x - KupalaCore.Half, y - KupalaCore.Half) && PassableAt(x + KupalaCore.Half - 1, y - KupalaCore.Half)
        && PassableAt(x - KupalaCore.Half, y + KupalaCore.Half - 1) && PassableAt(x + KupalaCore.Half - 1, y + KupalaCore.Half - 1);

    static byte[] BuildHops()
    {
        var hops = new byte[Cells * Cells];
        Array.Fill(hops, NoHop);
        var queue = new int[Cells];
        var seen = new bool[Cells];
        foreach (var target in Walkable)
        {
            Array.Clear(seen);
            int head = 0, tail = 0;
            queue[tail++] = target;
            seen[target] = true;
            var row = target * Cells;
            while (head < tail)
            {
                var u = queue[head++];
                int ux = u % W, uy = u / W;
                for (var d = 0; d < 4; d++)
                {
                    int nx = ux + KupalaCore.DX[d], ny = uy + KupalaCore.DY[d];
                    if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                    var n = ny * W + nx;
                    if (seen[n] || !Pass[n]) continue;
                    seen[n] = true;
                    hops[row + n] = (byte)((d + 2) & 3);
                    queue[tail++] = n;
                }
            }
        }
        return hops;
    }
}
