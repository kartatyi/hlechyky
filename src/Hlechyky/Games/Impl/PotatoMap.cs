namespace Hlechyky.Games.Impl;

/// <summary>
/// Толока «Гарячого горщика»: одна ручна мапа 24×16 на всі партії, імена селян і таблиця «з клітинки до цілі —
/// перший крок» для ботів. Мапа стала, тож усе це рахується раз на процес. Своя, а не ярмаркова з «Юрми»:
/// тут не торгують, а ганяють горщик, тож поле відкрите й тісніше — горщик має до когось доходити.
/// </summary>
public static class PotatoMap
{
    /// <summary>Клітинок по горизонталі й вертикалі; клітинка — 32 одиниці світу.</summary>
    public const int W = 24, H = 16, Cell = 32, Cells = W * H;
    public const int WorldW = W * Cell, WorldH = H * Cell;
    /// <summary>У таблиці <see cref="NextHop"/>: недосяжно або вже на місці.</summary>
    public const byte NoHop = 255;

    /// <summary>
    /// Легенда: <c>#</c> тин, <c>.</c> трава, <c>=</c> утоптана земля (прохідні лише ці дві), <c>T</c> дерево (верба),
    /// <c>Y</c> копиця, <c>W</c> криниця 2×2, <c>B</c> лава 2×1, <c>V</c> віз 2×1. Перевірено тестом: 283 прохідні клітинки,
    /// усі з'єднані між собою; тупиків, де горщик загнав би когось у кут назавжди, нема.
    /// </summary>
    public static readonly string[] Rows =
    [
        "########################",
        "#T.........==.........T#",
        "#....BB....==....VV....#",
        "#..........==..........#",
        "#..Y...============....#",
        "#......=..........=..T.#",
        "#.T....=...WW.....=....#",
        "#==========WW==========#",
        "#......=..........=....#",
        "#..VV..=...BB.....=.Y..#",
        "#......============....#",
        "#.T........==......BB..#",
        "#....Y.....==..........#",
        "#..........==....T.....#",
        "#T.........==.........T#",
        "########################",
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
    ];

    /// <summary>Прохідність клітинки за індексом <c>y * W + x</c>.</summary>
    public static readonly bool[] Pass = new bool[Cells];
    /// <summary>Усі прохідні клітинки за зростанням індексу.</summary>
    public static readonly int[] Walkable;

    static readonly Lazy<byte[]> Hops = new(BuildHops, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// «З клітинки <c>cell</c> до цілі <c>target</c> — перший крок» (0…3), індекс <c>target * Cells + cell</c>;
    /// <see cref="NoHop"/> — недосяжно або вже там. BFS від кожної прохідної клітинки, сусіди в порядку 0…3. 147 КБ.
    /// </summary>
    public static byte[] NextHop => Hops.Value;

    static PotatoMap()
    {
        var walk = new List<int>();
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var i = y * W + x;
                Pass[i] = Rows[y][x] is '.' or '=';
                if (Pass[i]) walk.Add(i);
            }
        Walkable = [.. walk];
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
        PassableAt(x - PotatoCore.Half, y - PotatoCore.Half) && PassableAt(x + PotatoCore.Half - 1, y - PotatoCore.Half)
        && PassableAt(x - PotatoCore.Half, y + PotatoCore.Half - 1) && PassableAt(x + PotatoCore.Half - 1, y + PotatoCore.Half - 1);

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
                    int nx = ux + PotatoCore.DX[d], ny = uy + PotatoCore.DY[d];
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
