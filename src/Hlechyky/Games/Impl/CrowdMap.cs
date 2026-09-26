namespace Hlechyky.Games.Impl;

/// <summary>
/// Лоток ярмарку: корпус 2×1 клітинки (перешкода) і прилавок — дві стежкові клітинки перед ним. Точка
/// прилавка (<see cref="Fx"/>, <see cref="Fy"/>) — середина між центрами тих двох клітинок; купують із
/// відстані ≤ <see cref="CrowdCore.BuyRange"/> від неї. <see cref="Face"/> — куди дивиться той, хто стоїть
/// за прилавком (3 — корпус над прилавком, 1 — під ним).
/// </summary>
public sealed record CrowdStall(int I, char Letter, string Name, string What, string Emoji, int X, int Y, int Fx, int Fy, int Face, int C0, int C1);

/// <summary>
/// Ярмарок «Юрми»: одна ручна мапа 30×20 на всі партії, дванадцять лотків, імена селян і таблиця
/// «з клітинки до цілі — перший крок» для ботів. Мапа стала, тож усе це рахується раз на процес.
/// </summary>
public static class CrowdMap
{
    /// <summary>Клітинок по горизонталі й вертикалі; клітинка — 32 одиниці світу.</summary>
    public const int W = 30, H = 20, Cell = 32, Cells = W * H;
    public const int WorldW = W * Cell, WorldH = H * Cell;
    /// <summary>У таблиці <see cref="NextHop"/>: недосяжно або вже на місці.</summary>
    public const byte NoHop = 255;

    /// <summary>
    /// Легенда: <c>#</c> паркан, <c>.</c> трава, <c>=</c> стежка (прохідні лише ці дві), <c>T</c> дерево, <c>Y</c> копиця,
    /// <c>W</c> криниця, <c>O</c> карусель, <c>S</c> сцена, <c>A</c>…<c>L</c> корпуси лотків. Перевірено тестом: 446 прохідних
    /// клітинок, усі з'єднані між собою.
    /// </summary>
    public static readonly string[] Rows =
    [
        "##############################",
        "#T..AA....BB....CC....DD...T.#",
        "#...==....==....==....==.....#",
        "#.===========================#",
        "#.=.......T.........T......=.#",
        "#.=..LL........SSS.......EE=.#",
        "#.=..==........SSS.......==..#",
        "#.=.........OOOO.............#",
        "#.=...WW....OOOO.....Y.....=.#",
        "#.=...WW....OOOO...........=.#",
        "#.=..KK....................=.#",
        "#.=..==..............FF....=.#",
        "#.=.......Y..........==....=.#",
        "#.===========================#",
        "#.....==....==....==....==...#",
        "#.T...JJ....II....HH....GG.T.#",
        "#............................#",
        "#.T.....T........T.......T...#",
        "#............................#",
        "##############################",
    ];

    /// <summary>Назва, «що купили» (для «Хтось купив меду»), емодзі — у порядку літер A…L.</summary>
    static readonly (string Name, string What, string Emoji)[] Goods =
    [
        ("Глеки", "глека", "🏺"),
        ("Рушники", "рушник", "🧵"),
        ("Мед", "меду", "🍯"),
        ("Хліб", "хліба", "🍞"),
        ("Яблука", "яблук", "🍎"),
        ("Сир", "сиру", "🧀"),
        ("Пряники", "пряників", "🍪"),
        ("Вишиванки", "вишиванку", "👗"),
        ("Ложки", "ложку", "🥄"),
        ("Писанки", "писанку", "🥚"),
        ("Ковбаси", "ковбасу", "🌭"),
        ("Квіти", "квітів", "🌻"),
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

    /// <summary>Лотки: верхній ряд — 0…3 (A–D), бічні — 4, 5, 10, 11 (E, F, K, L), нижній — 6…9 (G–J).</summary>
    public static readonly CrowdStall[] Stalls;
    public static readonly int[] TopStalls = [0, 1, 2, 3], BottomStalls = [6, 7, 8, 9], SideStalls = [4, 5, 10, 11];

    /// <summary>Прохідність клітинки за індексом <c>y * W + x</c>.</summary>
    public static readonly bool[] Pass = new bool[Cells];
    /// <summary>Усі прохідні клітинки за зростанням індексу.</summary>
    public static readonly int[] Walkable;
    /// <summary>Клітинка → лоток, чий це прилавок; -1 — не прилавок.</summary>
    public static readonly int[] CounterOf = new int[Cells];

    static readonly Lazy<byte[]> Hops = new(BuildHops, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// «З клітинки <c>cell</c> до цілі <c>target</c> — перший крок» (0…3), індекс <c>target * Cells + cell</c>;
    /// <see cref="NoHop"/> — недосяжно або вже там. BFS від кожної прохідної клітинки, сусіди в порядку 0…3.
    /// 360 КБ на процес.
    /// </summary>
    public static byte[] NextHop => Hops.Value;

    static CrowdMap()
    {
        var walk = new List<int>();
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var c = Rows[y][x];
                var i = y * W + x;
                Pass[i] = c is '.' or '=';
                if (Pass[i]) walk.Add(i);
                CounterOf[i] = -1;
            }
        Walkable = [.. walk];

        var stalls = new CrowdStall[Goods.Length];
        for (var k = 0; k < Goods.Length; k++)
        {
            var letter = (char)('A' + k);
            var (bx, by) = Find(letter);
            // Прилавок — під корпусом (верхні й бічні лотки) або над ним (нижній ряд).
            var below = by + 1 < H && Rows[by + 1][bx] == '=' && Rows[by + 1][bx + 1] == '=';
            var cy = below ? by + 1 : by - 1;
            var c0 = cy * W + bx;
            var c1 = cy * W + bx + 1;
            CounterOf[c0] = k;
            CounterOf[c1] = k;
            var (name, what, emoji) = Goods[k];
            stalls[k] = new CrowdStall(k, letter, name, what, emoji, bx, by,
                Fx: bx * Cell + Cell, Fy: cy * Cell + Cell / 2, Face: below ? 3 : 1, C0: c0, C1: c1);
        }
        Stalls = stalls;
    }

    static (int X, int Y) Find(char letter)
    {
        for (var y = 0; y < H; y++)
        {
            var x = Rows[y].IndexOf(letter);
            if (x >= 0) return (x, y);
        }
        throw new InvalidOperationException($"на мапі нема лотка {letter}");
    }

    /// <summary>Центр клітинки в одиницях світу.</summary>
    public static int CenterX(int cell) => cell % W * Cell + Cell / 2;
    public static int CenterY(int cell) => cell / W * Cell + Cell / 2;

    /// <summary>Клітинка, у якій лежить точка (у межах мапи).</summary>
    public static int CellOf(int x, int y) => Math.Clamp(y / Cell, 0, H - 1) * W + Math.Clamp(x / Cell, 0, W - 1);

    /// <summary>Чи прохідна точка світу; усе поза мапою — ні.</summary>
    public static bool PassableAt(int x, int y) =>
        x >= 0 && y >= 0 && x < WorldW && y < WorldH && Pass[y / Cell * W + x / Cell];

    /// <summary>
    /// Чи вміщається коробка селянина 16×16 із центром (x, y): чотири кути (x − 8 … x + 7, y − 8 … y + 7) — у прохідних
    /// клітинках. Коробка напіввідкрита, тож центр клітинки ± 8 уміщається в одну клітинку рівно.
    /// </summary>
    public static bool BoxFits(int x, int y) =>
        PassableAt(x - CrowdCore.Half, y - CrowdCore.Half) && PassableAt(x + CrowdCore.Half - 1, y - CrowdCore.Half)
        && PassableAt(x - CrowdCore.Half, y + CrowdCore.Half - 1) && PassableAt(x + CrowdCore.Half - 1, y + CrowdCore.Half - 1);

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
                    int nx = ux + CrowdCore.DX[d], ny = uy + CrowdCore.DY[d];
                    if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                    var n = ny * W + nx;
                    if (seen[n] || !Pass[n]) continue;
                    seen[n] = true;
                    // із n до u — протилежний напрямок до того, яким ми прийшли з u в n
                    hops[row + n] = (byte)((d + 2) & 3);
                    queue[tail++] = n;
                }
            }
        }
        return hops;
    }
}
