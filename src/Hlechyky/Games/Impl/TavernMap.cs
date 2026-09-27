namespace Hlechyky.Games.Impl;

/// <summary>
/// Місце, де наливають: шинквас або бочка. Корпус — перешкода (<see cref="W"/> клітинок завширшки, один рядок), перед
/// ним — клітинки-«приступки» (<see cref="Cells"/>), з яких і п'ють. <see cref="Face"/> — куди дивиться той, хто п'є
/// (3 — корпус над приступками, 1 — під ними). (<see cref="Fx"/>, <see cref="Fy"/>) — середина приступок, для підписів.
/// </summary>
public sealed record TavernPlace(int I, string Name, string What, string Emoji, int X, int Y, int W, int Face, int[] Cells, int Fx, int Fy);

/// <summary>
/// Корчма: одна ручна мапа 30×20 на всі партії — шинквас із корчмарем, піч, чотири бочки, чотири довгі столи з
/// лавами, двері на ґанок. Імена відвідувачів і таблиця «з клітинки до цілі — перший крок» для ботів. Мапа стала,
/// тож усе це рахується раз на процес.
/// </summary>
public static class TavernMap
{
    /// <summary>Клітинок по горизонталі й вертикалі; клітинка — 32 одиниці світу.</summary>
    public const int W = 30, H = 20, Cell = 32, Cells = W * H;
    public const int WorldW = W * Cell, WorldH = H * Cell;
    /// <summary>У таблиці <see cref="NextHop"/>: недосяжно або вже на місці.</summary>
    public const byte NoHop = 255;

    /// <summary>
    /// Легенда: <c>#</c> стіна, <c>.</c> долівка, <c>b</c> лава (по ній ходять, на ній сидять), <c>:</c> приступка
    /// шинквасу чи бочки (звідси п'ють), <c>d</c> двері, <c>,</c> ґанок надворі — прохідні лише ці шість. <c>T</c> стіл,
    /// <c>K</c> шинквас, <c>k</c> місце корчмаря за шинквасом, <c>O</c> бочка, <c>P</c> піч — перешкоди.
    /// </summary>
    public static readonly string[] Rows =
    [
        "##############################",
        "#kkkkkkkk#....PPP......OO....#",
        "#kkkkkkkk#....PPP......::....#",
        "#KKKKKKKK#...................#",
        "#.::::::.....................#",
        "#............................#",
        "#OO..bbbbbbbb....bbbbbbbb....#",
        "#::..TTTTTTTT....TTTTTTTT....#",
        "#....bbbbbbbb....bbbbbbbb....#",
        "#............................#",
        "#....bbbbbbbb....bbbbbbbb..::#",
        "#....TTTTTTTT....TTTTTTTT..OO#",
        "#....bbbbbbbb....bbbbbbbb....#",
        "#............................#",
        "#::..........................#",
        "#OO..........................#",
        "#############dddd#############",
        "#,,,,,,,,,,,,,,,,,,,,,,,,,,,,#",
        "#,,,,,,,,,,,,,,,,,,,,,,,,,,,,#",
        "##############################",
    ];

    /// <summary>
    /// Де наливають: шинквас (0) і чотири бочки. Три кухлі в трьох різних місцях — тихий шлях до перемоги раунду.
    /// </summary>
    public static readonly TavernPlace[] Places =
    [
        Place(0, "Шинквас", "пива", "🍺", 1, 3, 8, 3, [(2, 4), (3, 4), (4, 4), (5, 4), (6, 4), (7, 4)]),
        Place(1, "Медовуха", "медовухи", "🍯", 23, 1, 2, 3, [(23, 2), (24, 2)]),
        Place(2, "Квас", "квасу", "🫗", 1, 6, 2, 3, [(1, 7), (2, 7)]),
        Place(3, "Сидр", "сидру", "🍏", 27, 11, 2, 1, [(27, 10), (28, 10)]),
        Place(4, "Слив'янка", "слив'янки", "🍷", 1, 15, 2, 1, [(1, 14), (2, 14)]),
    ];

    /// <summary>Імена відвідувачів: тасуються на кожен раунд, беруться перші N. Ім'я гравця — таке саме, як у бота.</summary>
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
    /// <summary>Клітинки лав (на них сідають) за зростанням індексу.</summary>
    public static readonly int[] Benches;
    /// <summary>Клітинки ґанку біля дверей — туди корчмар викидає забіяк.</summary>
    public static readonly int[] Porch;
    /// <summary>Клітинка → місце, чия це приступка; -1 — не приступка.</summary>
    public static readonly int[] PlaceOf = new int[Cells];
    /// <summary>Клітинка → куди дивиться той, хто сидить тут на лаві (1 — стіл під лавою, 3 — над); -1 — не лава.</summary>
    public static readonly int[] BenchFace = new int[Cells];

    /// <summary>Де ходить корчмар за шинквасом: від лівого до правого краю свого закутка, у рядку біля шинквасу.</summary>
    public const int BarmanMinX = 1 * Cell + Cell / 2, BarmanMaxX = 8 * Cell + Cell / 2, BarmanY = 2 * Cell + Cell / 2;

    static readonly Lazy<byte[]> Hops = new(BuildHops, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// «З клітинки <c>cell</c> до цілі <c>target</c> — перший крок» (0…3), індекс <c>target * Cells + cell</c>;
    /// <see cref="NoHop"/> — недосяжно або вже там. BFS від кожної прохідної клітинки, сусіди в порядку 0…3. 360 КБ.
    /// </summary>
    public static byte[] NextHop => Hops.Value;

    static TavernPlace Place(int i, string name, string what, string emoji, int x, int y, int w, int face, (int X, int Y)[] cells)
    {
        int sx = 0, sy = 0;
        foreach (var (cx, cy) in cells)
        {
            sx += cx * Cell + Cell / 2;
            sy += cy * Cell + Cell / 2;
        }
        return new TavernPlace(i, name, what, emoji, x, y, w, face, [.. cells.Select(c => c.Y * W + c.X)], sx / cells.Length, sy / cells.Length);
    }

    static TavernMap()
    {
        var walk = new List<int>();
        var benches = new List<int>();
        var porch = new List<int>();
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var c = Rows[y][x];
                var i = y * W + x;
                Pass[i] = c is '.' or 'b' or ':' or 'd' or ',';
                if (Pass[i]) walk.Add(i);
                PlaceOf[i] = -1;
                BenchFace[i] = -1;
                if (c == 'b')
                {
                    benches.Add(i);
                    BenchFace[i] = y + 1 < H && Rows[y + 1][x] == 'T' ? 1 : 3;
                }
                // ґанок біля дверей: два ряди надворі, на шість клітинок в обидва боки від середини дверей
                if (c == ',' && x >= 9 && x <= 20) porch.Add(i);
            }
        Walkable = [.. walk];
        Benches = [.. benches];
        Porch = [.. porch];
        foreach (var p in Places)
            foreach (var cell in p.Cells)
                PlaceOf[cell] = p.I;
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
    /// Чи вміщається коробка відвідувача 16×16 із центром (x, y): чотири кути (x − 8 … x + 7, y − 8 … y + 7) — у прохідних
    /// клітинках. Коробка напіввідкрита, тож центр клітинки ± 8 уміщається в одну клітинку рівно.
    /// </summary>
    public static bool BoxFits(int x, int y) =>
        PassableAt(x - TavernCore.Half, y - TavernCore.Half) && PassableAt(x + TavernCore.Half - 1, y - TavernCore.Half)
        && PassableAt(x - TavernCore.Half, y + TavernCore.Half - 1) && PassableAt(x + TavernCore.Half - 1, y + TavernCore.Half - 1);

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
                    int nx = ux + TavernCore.DX[d], ny = uy + TavernCore.DY[d];
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
