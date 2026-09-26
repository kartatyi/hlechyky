namespace Hlechyky.Games.Impl;

/// <summary>
/// Подвір'я «Вечорниць»: одна ручна мапа 30×20 на всі партії, коло для танцю посередині, поміст із музиками,
/// імена дівчат і парубків і таблиця «з клітинки до цілі — перший крок» для ботів. Мапа стала, тож усе це
/// рахується раз на процес.
/// </summary>
public static class DanceMap
{
    /// <summary>Клітинок по горизонталі й вертикалі; клітинка — 32 одиниці світу.</summary>
    public const int W = 30, H = 20, Cell = 32, Cells = W * H;
    public const int WorldW = W * Cell, WorldH = H * Cell;
    /// <summary>У таблиці <see cref="NextHop"/>: недосяжно або вже на місці.</summary>
    public const byte NoHop = 255;

    /// <summary>
    /// Коло для танцю: центр (480, 320) — просто під музиками, радіус 112 одиниць (3,5 клітинки). Хто стоїть у
    /// колі на такт і вціляє фігуру, тому й рахується «в колі». Усередині кола й на 16 довкола — жодної перешкоди.
    /// </summary>
    public const int CircleX = 480, CircleY = 320, CircleR = 112;

    /// <summary>
    /// Легенда: <c>#</c> тин, <c>.</c> спориш, <c>=</c> стежка (прохідні лише ці дві), <c>H</c> хата, <c>M</c> поміст
    /// із музиками, <c>T</c> дерево, <c>W</c> криниця, <c>L</c> лава, <c>S</c> стіл із наїдками, <c>Y</c> копиця.
    /// Перевірено тестом: 442 прохідні клітинки, усі з'єднані між собою.
    /// </summary>
    public static readonly string[] Rows =
    [
        "##############################",
        "#HHHHHHH.....MMMM..........TT#",
        "#HHHHHHH.....MMMM...........T#",
        "#HHHHHHH..............T......#",
        "#....=.......................#",
        "#....=.......................#",
        "#....=.................SSSS..#",
        "#....=.......................#",
        "#....=...L..........L........#",
        "#....=...L..........L........#",
        "#....=...L..........L........#",
        "#....=..T....................#",
        "#....=.................SSSS..#",
        "#.WW.=.......................#",
        "#.WW.=.......................#",
        "#....=.......LLLL.........Y..#",
        "#....=====================...#",
        "#...........................T#",
        "#TT....Y....................T#",
        "##############################",
    ];

    /// <summary>Імена дівчат (вигляд 0) — тасуються на кожен раунд. Ім'я гравчині таке саме, як у ботів.</summary>
    public static readonly string[] Girls =
    [
        "Параска", "Ганна", "Одарка", "Марічка", "Оксана", "Домаха", "Соломія", "Мотря",
        "Ярина", "Христя", "Марта", "Устя", "Килина", "Наталка", "Пріська", "Гафія",
        "Софійка", "Орися", "Настя", "Явдоха", "Меланка", "Феся", "Текля", "Зоряна",
        "Уляна", "Люба", "Дарина", "Олеся", "Марійка", "Ганнуся", "Варка", "Катря",
    ];

    /// <summary>Імена парубків (вигляд 1).</summary>
    public static readonly string[] Boys =
    [
        "Микола", "Степан", "Тарас", "Гриць", "Панас", "Іванко", "Назар", "Богдан",
        "Остап", "Данило", "Максим", "Юрко", "Левко", "Роман", "Андрійко", "Василь",
        "Тиміш", "Прокіп", "Лесь", "Захар", "Матвій", "Сашко", "Ігнат", "Йосип",
        "Федь", "Влас", "Кузьма", "Сидір", "Петрик", "Демко", "Славко", "Михась",
    ];

    /// <summary>Прохідність клітинки за індексом <c>y * W + x</c>.</summary>
    public static readonly bool[] Pass = new bool[Cells];
    /// <summary>Усі прохідні клітинки за зростанням індексу.</summary>
    public static readonly int[] Walkable;
    /// <summary>Клітинки кола: центр клітинки не далі <c>CircleR − 12</c> від центру кола (коробка цілком у колі).</summary>
    public static readonly int[] Ring;
    /// <summary>Клітинки перед помостом — «біля музик».</summary>
    public static readonly int[] Stage;

    static readonly Lazy<byte[]> Hops = new(BuildHops, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// «З клітинки <c>cell</c> до цілі <c>target</c> — перший крок» (0…3), індекс <c>target * Cells + cell</c>;
    /// <see cref="NoHop"/> — недосяжно або вже там. BFS від кожної прохідної клітинки, сусіди в порядку 0…3.
    /// 360 КБ на процес.
    /// </summary>
    public static byte[] NextHop => Hops.Value;

    static DanceMap()
    {
        var walk = new List<int>();
        var ring = new List<int>();
        var stage = new List<int>();
        const int inner = CircleR - 12;
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var c = Rows[y][x];
                var i = y * W + x;
                Pass[i] = c is '.' or '=';
                if (!Pass[i]) continue;
                walk.Add(i);
                long dx = CenterX(i) - CircleX, dy = CenterY(i) - CircleY;
                if (dx * dx + dy * dy <= inner * inner) ring.Add(i);
                if (y == 3 && x >= 12 && x <= 17) stage.Add(i);
            }
        Walkable = [.. walk];
        Ring = [.. ring];
        Stage = [.. stage];
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
    /// Чи вміщається коробка танцюриста 16×16 із центром (x, y): чотири кути (x − 8 … x + 7, y − 8 … y + 7) — у
    /// прохідних клітинках. Коробка напіввідкрита, тож центр клітинки ± 8 уміщається в одну клітинку рівно.
    /// </summary>
    public static bool BoxFits(int x, int y) =>
        PassableAt(x - DanceCore.Half, y - DanceCore.Half) && PassableAt(x + DanceCore.Half - 1, y - DanceCore.Half)
        && PassableAt(x - DanceCore.Half, y + DanceCore.Half - 1) && PassableAt(x + DanceCore.Half - 1, y + DanceCore.Half - 1);

    /// <summary>Чи стоїть точка в колі (центр танцюриста — не далі <see cref="CircleR"/> від центру кола).</summary>
    public static bool InCircle(int x, int y)
    {
        long dx = x - CircleX, dy = y - CircleY;
        return dx * dx + dy * dy <= (long)CircleR * CircleR;
    }

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
                    int nx = ux + DanceCore.DX[d], ny = uy + DanceCore.DY[d];
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
