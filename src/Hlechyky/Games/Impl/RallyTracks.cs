namespace Hlechyky.Games.Impl;

/// <summary>
/// Одна траса «Сільського ралі»: мапа 48×27 клітинок по 32 u, невидимі ворота-перетини, стартова решітка.
/// Усе похідне (поверхні клітинок, <see cref="GateAt"/>, центри й курси воріт, точки повернення, лінія
/// фінішу) рахується один раз у конструкторі — у тику симуляція лише читає готові масиви.
/// Легенда мапи — spec §5.3: <c>=</c> дорога, <c>~</c> калюжа, <c>*</c> лід, <c>.</c> трава, <c>c</c> кукурудза,
/// <c>M</c> багно, <c>o</c> мастило, <c>+</c> турбо, <c>J</c> трамплін, <c>H</c> копиця, <c>#TDW</c> — стіни.
/// </summary>
public sealed class RallyTrack
{
    public const int Cols = 48, Rows = 27, Cells = Cols * Rows;
    /// <summary>Клітинка в sub: 32 u × 64 = 2048 = 2¹¹.</summary>
    public const int CellSub = 2048, CellShift = 11;
    /// <summary>У <see cref="GateAt"/>: клітинка не належить жодним воротам.</summary>
    public const byte NoGate = 255;

    public string Id { get; }
    public string Title { get; }
    public IReadOnlyList<string> Map { get; }
    public int[][][] Gates { get; }
    public (int X, int Y)[] Slots { get; }
    /// <summary>Курс на старті (0..1023, 0 — праворуч). У всіх п'яти трасах — праворуч.</summary>
    public int Heading { get; }
    public bool Night { get; }
    public bool Ice { get; }
    public bool Corn { get; }

    /// <summary>Скільки воріт; ворота 0 — лінія старту й фінішу.</summary>
    public int K { get; }
    /// <summary>Поверхня кожної клітинки — код із <see cref="RallySurface"/>.</summary>
    public byte[] Tile { get; } = new byte[Cells];
    /// <summary>Номер воріт кожної клітинки або <see cref="NoGate"/>.</summary>
    public byte[] GateAt { get; } = new byte[Cells];
    /// <summary>Центр першого прямокутника воріт, sub — для ранжування й стрілки «не туди».</summary>
    public int[] GateCX { get; }
    public int[] GateCY { get; }
    /// <summary>Курс повернення на трасу біля воріт i: поперек воріт, у бік наступних.</summary>
    public int[] GateA { get; }
    /// <summary>Куди ставити машину, що повертається на трасу біля воріт i (sub): найближча до центру воріт
    /// клітинка дороги — не копиця й не калюжа, щоб повернення не кидало в перешкоду.</summary>
    public int[] ResetX { get; }
    public int[] ResetY { get; }
    /// <summary>Центри стартових слотів, sub.</summary>
    public int[] SlotX { get; }
    public int[] SlotY { get; }
    /// <summary>Лінія фінішу для частки тика: вісь (0 — x, 1 — y), грань у sub і напрямок перетину (+1/−1).</summary>
    public int LineAxis { get; }
    public int LineEdge { get; }
    public int LineDir { get; }

    /// <summary>
    /// Живі перешкоди (№89): кожна — <c>[вид, ax, ay, bx, by, хід, стоянка, гурт, зсув]</c>, координати в sub. Ходить
    /// A→B за «хід» тиків, стоїть у B «стоянку», вертається й стоїть в A. Гурт — ті, хто ходить разом (гуси), зсув — на
    /// скільки тиків відстає від гурту. Вид: 0 курка, 1 гуска, 2 віз, 3 весільний гість, 4 коза.
    /// </summary>
    public int[][] Critters { get; }

    /// <summary>Перешкода з клітинок: центр клітинки A → центр клітинки B.</summary>
    public static int[] Critter(int kind, int ax, int ay, int bx, int by, int move, int rest, int group = 0, int lag = 0) =>
        [kind, ax * CellSub + CellSub / 2, ay * CellSub + CellSub / 2, bx * CellSub + CellSub / 2, by * CellSub + CellSub / 2, move, rest, group, lag];

    public RallyTrack(string id, string title, string[] Map, int[][][] Gates, (int X, int Y)[] Slots,
        int Heading = 0, bool Night = false, bool Ice = false, bool Corn = false, int[][]? Critters = null)
    {
        this.Critters = Critters ?? [];
        Id = id;
        Title = title;
        this.Map = Map;
        this.Gates = Gates;
        this.Slots = Slots;
        this.Heading = Heading;
        this.Night = Night;
        this.Ice = Ice;
        this.Corn = Corn;
        K = Gates.Length;

        if (Map.Length != Rows) throw Bad($"мапа має {Map.Length} рядків, а треба {Rows}");
        for (var y = 0; y < Rows; y++)
        {
            if (Map[y].Length != Cols) throw Bad($"рядок {y} має {Map[y].Length} символів, а треба {Cols}");
            for (var x = 0; x < Cols; x++)
            {
                var code = RallySurface.Of(Map[y][x]);
                if (code == RallySurface.Unknown) throw Bad($"невідомий символ «{Map[y][x]}» у ({x},{y})");
                if ((x == 0 || y == 0 || x == Cols - 1 || y == Rows - 1) && !RallySurface.IsWall(code))
                    throw Bad($"рамка мапи дірява в ({x},{y})");
                Tile[y * Cols + x] = (byte)code;
            }
        }
        if (K is < 3 or > 9) throw Bad($"воріт {K}, а треба 3..9");
        if (Slots.Length != RallyCore.Seats) throw Bad($"слотів {Slots.Length}, а треба {RallyCore.Seats}");

        Array.Fill(GateAt, NoGate);
        GateCX = new int[K];
        GateCY = new int[K];
        GateA = new int[K];
        ResetX = new int[K];
        ResetY = new int[K];
        for (var g = 0; g < K; g++)
        {
            if (Gates[g].Length == 0) throw Bad($"ворота {g} порожні");
            foreach (var r in Gates[g])
            {
                if (r.Length != 4 || r[2] < 1 || r[3] < 1 || r[0] < 0 || r[1] < 0 || r[0] + r[2] > Cols || r[1] + r[3] > Rows)
                    throw Bad($"кривий прямокутник воріт {g}");
                for (var y = r[1]; y < r[1] + r[3]; y++)
                    for (var x = r[0]; x < r[0] + r[2]; x++)
                    {
                        var cell = y * Cols + x;
                        if (GateAt[cell] != NoGate) throw Bad($"ворота {GateAt[cell]} і {g} перекриваються в ({x},{y})");
                        GateAt[cell] = (byte)g;
                    }
            }
            var f = Gates[g][0];
            GateCX[g] = (f[0] * 2 + f[2]) * (CellSub / 2);
            GateCY[g] = (f[1] * 2 + f[3]) * (CellSub / 2);
        }
        for (var g = 0; g < K; g++)
        {
            // Ворота — поперечний переріз дороги, тож повертаємо машину поперек них: стовпчик — ліворуч чи
            // праворуч, рядок — угору чи вниз, у той бік, де наступні ворота. Прямий курс «на центр наступних»
            // на поворотах дивився б у тин.
            var f = Gates[g][0];
            var next = (g + 1) % K;
            GateA[g] = f[2] <= f[3]
                ? (GateCX[next] >= GateCX[g] ? 0 : 512)
                : (GateCY[next] >= GateCY[g] ? 256 : 768);
            (ResetX[g], ResetY[g]) = ResetPoint(g);
        }

        var line = Gates[0];
        if (line.Length != 1 || (line[0][2] != 1 && line[0][3] != 1)) throw Bad("ворота 0 мають бути одним стовпчиком чи рядком");
        var l = line[0];
        var prev = K - 1;
        if (l[2] == 1)
        {
            LineAxis = 0;
            int left = l[0] * CellSub, right = (l[0] + 1) * CellSub;
            var fromLeft = Math.Abs(left - GateCX[prev]) <= Math.Abs(right - GateCX[prev]);
            LineEdge = fromLeft ? left : right;
            LineDir = fromLeft ? 1 : -1;
        }
        else
        {
            LineAxis = 1;
            int top = l[1] * CellSub, bottom = (l[1] + 1) * CellSub;
            var fromTop = Math.Abs(top - GateCY[prev]) <= Math.Abs(bottom - GateCY[prev]);
            LineEdge = fromTop ? top : bottom;
            LineDir = fromTop ? 1 : -1;
        }

        SlotX = new int[Slots.Length];
        SlotY = new int[Slots.Length];
        for (var i = 0; i < Slots.Length; i++)
        {
            var (x, y) = Slots[i];
            var code = Tile[y * Cols + x];
            if (RallySurface.IsWall(code) || code == RallySurface.Hay) throw Bad($"слот {i} стоїть не на дорозі");
            if (GateAt[y * Cols + x] != NoGate) throw Bad($"слот {i} стоїть у воротах");
            SlotX[i] = x * CellSub + CellSub / 2;
            SlotY[i] = y * CellSub + CellSub / 2;
        }
    }

    /// <summary>Найближча до центру воріт клітинка воріт із дорогою (або льодом), без копиць і калюж.</summary>
    (int X, int Y) ResetPoint(int g)
    {
        int bestX = GateCX[g], bestY = GateCY[g];
        long best = long.MaxValue;
        for (var pass = 0; pass < 2 && best == long.MaxValue; pass++)
            foreach (var r in Gates[g])
                for (var y = r[1]; y < r[1] + r[3]; y++)
                    for (var x = r[0]; x < r[0] + r[2]; x++)
                    {
                        var code = Tile[y * Cols + x];
                        var fine = pass == 0
                            ? code is RallySurface.Road or RallySurface.Ice
                            : !RallySurface.IsWall(code) && code != RallySurface.Hay;
                        if (!fine) continue;
                        int cx = x * CellSub + CellSub / 2, cy = y * CellSub + CellSub / 2;
                        long dx = cx - GateCX[g], dy = cy - GateCY[g];
                        var d = dx * dx + dy * dy;
                        if (d >= best) continue;
                        best = d;
                        bestX = cx;
                        bestY = cy;
                    }
        return (bestX, bestY);
    }

    static InvalidOperationException Bad(string why) => new($"Сільське ралі: траса крива — {why}");

    /// <summary>Клітинка за координатами в клітинках; поза мапою — стіна.</summary>
    public int CodeAt(int x, int y) => x < 0 || y < 0 || x >= Cols || y >= Rows ? RallySurface.Fence : Tile[y * Cols + x];

    /// <summary>
    /// Перевірка з spec §7 (і тест <c>Every_track_passes_the_gate_cut_check</c>): з першого слоту досяжні всі
    /// ворота; для кожних g, якщо закрити ворота g і g+2, з воріт g+1 не досягти жодних інших — тобто кожні
    /// ворота справді перетинають дорогу від тину до тину, і їхній порядок правильний. null — усе гаразд.
    /// </summary>
    public string? CutCheck()
    {
        var all = Reach(Slots[0].X, Slots[0].Y, -1, -1);
        for (var g = 0; g < K; g++)
        {
            var seen = false;
            for (var c = 0; c < Cells && !seen; c++) seen = GateAt[c] == g && all[c];
            if (!seen) return $"ворота {g} недосяжні зі старту";
        }
        for (var g = 0; g < K; g++)
        {
            int a = g, b = (g + 1) % K, c = (g + 2) % K;
            for (var cell = 0; cell < Cells; cell++)
            {
                if (GateAt[cell] != b || RallySurface.IsWall(Tile[cell])) continue;
                var r = Reach(cell % Cols, cell / Cols, a, c);
                for (var i = 0; i < Cells; i++)
                    if (r[i] && GateAt[i] != NoGate && GateAt[i] != b)
                        return $"з воріт {b} можна проїхати до воріт {GateAt[i]} в обхід {a} і {c}";
            }
        }
        foreach (var (x, y) in Slots)
        {
            // слоти стоять між останніми воротами й лінією: закривши обидві, від слота не доїхати до жодних воріт
            var r = Reach(x, y, 0, K - 1);
            for (var i = 0; i < Cells; i++)
                if (r[i] && GateAt[i] != NoGate && GateAt[i] != 0 && GateAt[i] != K - 1)
                    return $"слот ({x},{y}) стоїть не перед лінією старту";
        }
        return null;
    }

    bool[] Reach(int sx, int sy, int blockA, int blockB)
    {
        var seen = new bool[Cells];
        var queue = new Queue<int>();
        var start = sy * Cols + sx;
        seen[start] = true;
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            int x = cell % Cols, y = cell / Cols;
            for (var d = 0; d < 4; d++)
            {
                int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0), ny = y + (d == 2 ? 1 : d == 3 ? -1 : 0);
                if (nx < 0 || ny < 0 || nx >= Cols || ny >= Rows) continue;
                var n = ny * Cols + nx;
                if (seen[n] || RallySurface.IsWall(Tile[n])) continue;
                var gate = GateAt[n];
                if (gate != NoGate && (gate == blockA || gate == blockB)) continue;
                seen[n] = true;
                queue.Enqueue(n);
            }
        }
        return seen;
    }

    /// <summary>Статична частина виду: мапа, ворота, решітка. Будується раз — види її лише посилають.</summary>
    public object Wire => _wire ??= new
    {
        id = Id,
        title = Title,
        cols = Cols,
        rows = Rows,
        cell = 32,
        night = Night,
        ice = Ice,
        corn = Corn,
        critters = Critters,
        map = Map,
        gates = Gates,
        // центри воріт і точки повернення — в u (1/64 від sub), курси — 0..1023
        gateA = GateA,
        gateC = Enumerable.Range(0, K).Select(g => new[] { GateCX[g] >> 6, GateCY[g] >> 6 }).ToArray(),
        slots = Slots.Select(s => new[] { s.X, s.Y }).ToArray(),
        heading = Heading,
        line = new[] { LineAxis, LineEdge >> 6, LineDir },
    };
    object? _wire;
}

/// <summary>Усі траси. Мапи — spec §7; перевірку <see cref="RallyTrack.CutCheck"/> женуть тести.</summary>
public static class RallyTracks
{
    public static readonly RallyTrack[] All =
    [
        new("selo", "Село", Night: false, Ice: false, Corn: false,
            Map:
            [
                "################################################",
                "##...........................................###",
                "##.=========================================.###",
                "##.=========================================.###",
                "##.=========================================.###",
                "##.=========================================.###",
                "##.====.................................H===.###",
                "##.====.#T###T###T###T###T###T###T###T#.====.###",
                "##.====.T###DDDDD###T###T#DDDDD#T###T##.====.###",
                "##.=~~=.###TDDDDD##T###T##DDDDDT###T###.====.###",
                "##.=~~=.##T#DDDDD#T###T###DDDDD###T###T.====.###",
                "##.====.#T##DDDDDT###T###T###T###T###T#.====.###",
                "##.====.T###T###T###T###T###T#WWWWWWW##.===H.###",
                "##.====.###T###T###T###T###T##WWWWWWW##.====.###",
                "##.====.##T###T###T###T###T###WWWWWWW#T.====.###",
                "##.====....................##TWWWWWWWT#.====.###",
                "##.=======================.#T###T###T##.====.###",
                "##.=========H=============.T###T###T###.====.###",
                "##.=======================.###T###T###T.====.###",
                "##.=======================..............====.###",
                "##....................==o==========~~=======.###",
                "#####################.========~~===~~=======.###",
                "#####################.========~~============.###",
                "#####################.======================.###",
                "#####################........................###",
                "################################################",
                "################################################",
            ],
            Gates: [[[20, 1, 1, 6]], [[34, 1, 1, 6]], [[39, 9, 6, 1]], [[39, 15, 6, 1]], [[30, 19, 1, 6]], [[18, 15, 1, 6]], [[2, 13, 6, 1]], [[2, 8, 6, 1]]],
            Slots: [(19, 2), (19, 4), (17, 3), (17, 5), (15, 2), (15, 4)],
            // живі перешкоди (№89), лише з опцією «Живність»
            Critters:
            [
                RallyTrack.Critter(0, 28, 1, 28, 6, 50, 150),
                RallyTrack.Critter(1, 2, 11, 7, 11, 90, 200, group: 1, lag: 0),
                RallyTrack.Critter(1, 2, 12, 7, 12, 90, 200, group: 1, lag: 12),
                RallyTrack.Critter(1, 2, 13, 7, 13, 90, 200, group: 1, lag: 24),
            ]),
        new("ozero", "Крижане озеро", Night: false, Ice: true, Corn: false,
            Map:
            [
                "################################################",
                "###..........................................###",
                "###.************======**********************.###",
                "###.************======**********************.###",
                "###.************======**********************.###",
                "###.************======**********************.###",
                "###.************======**********************.###",
                "###.*****..............................*****.###",
                "###.*****.TTWWWWWWWWWWWWWWWWWWWWWWWWTT.*****.###",
                "###.*****.TTWWWWWWWWWWWWWWWWWWWWWWWWTT.*****.###",
                "###.*****.TTWWWWWWWWDDDDDDDDWWWWWWWWTT.*****.###",
                "###.*****.TTWWWWWWWWDDDDDDDDWWWWWWWWTT.*****.###",
                "###.*****.TTWWWWWWWWDDDDDDDDWWWWWWWWTT.*****.###",
                "###.*****.TTWWWWWWWWDDDDDDDDWWWWWWWWTT.*****.###",
                "###.*****.TTWWWWWWWWDDDDDDDDWWWWWWWWTT.*****.###",
                "###.*****.TTWWWWWWWWWWWWWWWWWWWWWWWWTT.*****.###",
                "###.*****.TTWWWWWWWWWWWWWWWWWWWWWWWWTT.*****.###",
                "###.*****.TTWWWWWWWWWWWWWWWWWWWWWWWWTT.*****.###",
                "###.*****..............T...............*****.###",
                "###.******************###*******************.###",
                "###.******************###*******************.###",
                "###.****************************************.###",
                "###.****************************************.###",
                "###.****************************************.###",
                "###..........................................###",
                "################################################",
                "################################################",
            ],
            Gates: [[[21, 1, 1, 7]], [[33, 1, 1, 7]], [[38, 9, 7, 1]], [[38, 15, 7, 1]], [[30, 18, 1, 7]], [[14, 18, 1, 7]], [[3, 14, 7, 1]], [[3, 8, 7, 1]]],
            Slots: [(20, 3), (20, 5), (18, 2), (18, 4), (18, 6), (16, 3)]),
        new("nich", "Нічна", Night: true, Ice: false, Corn: false,
            Map:
            [
                "################################################",
                "##................................##############",
                "##.==============================.##############",
                "##.==============================.##############",
                "##.==============================.##############",
                "##.==============================.##############",
                "##.====......................====.##############",
                "##.====.###T####T####T####T#.====............###",
                "##.====.#TDDDDDDDDDDD###T###.===============.###",
                "##.====.##DDDDDDDDDDD#T####T.==o============.###",
                "##.====.##T####T####T####T##.===============.###",
                "##.====............####T####.===============.###",
                "##.===============.##T####T#............====.###",
                "##.=====~~========.T####T####T####T####.====.###",
                "##.=====~~========.###T####T####T####T#.====.###",
                "##.===============.#T####T####T####T###.====.###",
                "##............====.####T####T####T####T.====.###",
                "#############.====.##T####T####T####T##.====.###",
                "#############.====.T####T####T####T####.====.###",
                "#############.====......................====.###",
                "#############.==============================.###",
                "#############.======================~~======.###",
                "#############.======H===============~~======.###",
                "#############.==============================.###",
                "#############................................###",
                "################################################",
                "################################################",
            ],
            Gates: [[[16, 1, 1, 6]], [[28, 1, 1, 6]], [[36, 7, 1, 6]], [[39, 15, 6, 1]], [[32, 19, 1, 6]], [[20, 19, 1, 6]], [[13, 16, 6, 1]], [[9, 11, 1, 6]], [[2, 8, 6, 1]]],
            Slots: [(15, 2), (15, 4), (13, 3), (13, 5), (11, 2), (11, 4)]),
        new("kukurudza", "Кукурудзяне поле", Night: false, Ice: false, Corn: true,
            Map:
            [
                "################################################",
                "#cccccccccccccccccccccccccccccc#################",
                "#cccccccccccccccccccccccccccccc#################",
                "#cc==========================cc#################",
                "#cc==========================cc#################",
                "#cc===================H======ccccccccccccccccc##",
                "#cc==========================ccccccccccccccccc##",
                "#cc====cccccccccccccccccc===================cc##",
                "#cc====cccccccccccccccccc===================cc##",
                "#cc====cc##T####TDDDDDDcc===================cc##",
                "#cc====ccccccccc#DDDDDDcc===================cc##",
                "#cc====ccccccccc#DDDDDDccccccccccccccccc====cc##",
                "#cc===========cc#DDDDDDccccccccccccccccc====cc##",
                "#cc===========cc#DDDDDD####T##WWWWWWWTcc====cc##",
                "#cc===========ccT####T####T###WWWWWWW#cc====cc##",
                "#cc===========cc####T####T####WWWWWWW#cc====cc##",
                "#ccccccccc====cc###T####T####T####T###cc====cc##",
                "#ccccccccc====cccccccccccccccccccccccccc====cc##",
                "########cc====cccccccccccccccccccccccccc====cc##",
                "########cc====================MM============cc##",
                "########cc======MM============MM============cc##",
                "########cc======MM==================H=======cc##",
                "########cc==================================cc##",
                "########cccccccccccccccccccccccccccccccccccccc##",
                "########cccccccccccccccccccccccccccccccccccccc##",
                "################################################",
                "################################################",
            ],
            Gates: [[[15, 1, 1, 8]], [[20, 1, 1, 8]], [[34, 1, 1, 12]], [[38, 16, 8, 1]], [[33, 16, 1, 10]], [[20, 16, 1, 10]], [[8, 18, 8, 1], [16, 17, 1, 2]], [[1, 9, 8, 1]]],
            Slots: [(14, 3), (14, 5), (12, 4), (12, 6), (10, 3), (10, 5)],
            // живі перешкоди (№89), лише з опцією «Живність»
            Critters:
            [
                RallyTrack.Critter(0, 24, 18, 24, 23, 55, 160),
            ]),
        new("yarmarok", "Ярмарок", Night: false, Ice: false, Corn: false,
            Map:
            [
                "################################################",
                "##...........................................###",
                "##.======+====JMMM==========================.###",
                "##.======+====JMMM==========================.###",
                "##.======+====JMMM==========================.###",
                "##.======+====JMMM====================o=====.###",
                "##.====.................................====.###",
                "##.====.###T#####T#####T#####T#####T###.====.###",
                "##.====.##T#####T#####T#####T#####T####.====.###",
                "##.====.#TDDDDDT##DDDDD###DDDDD##T#####.====.###",
                "##.====.T#DDDDD###DDDDD###DDDDD#T#####T.====.###",
                "##.====.##DDDDD###DDDDD##TDDDDDT#####T#.====.###",
                "##.====.##DDDDD###DDDDD#T#DDDDD#####T##.=~~=.###",
                "##.====.###T#####T#####T#####T##WWWWW##.=~~=.###",
                "##.====.##T#####T#####T#####T###WWWWW##.====.###",
                "##.====.#T#####T#####T#####T####WWWWW##.====.###",
                "##.====.T#####T#####T#####T#####WWWWW#T.====.###",
                "##.====.#####T#####T#####T#####T#####T#.====.###",
                "##.====.####T#####T#####T#####T#####T##.====.###",
                "##.====.................................====.###",
                "##.==============================+==========.###",
                "##.===========H===========H======+==========.###",
                "##.=================H============+==========.###",
                "##.==============================+==========.###",
                "##...........................................###",
                "################################################",
                "################################################",
            ],
            Gates: [[[24, 1, 1, 6]], [[36, 1, 1, 6]], [[39, 9, 6, 1]], [[39, 16, 6, 1]], [[30, 19, 1, 6]], [[10, 19, 1, 6]], [[2, 14, 6, 1]], [[2, 8, 6, 1]]],
            Slots: [(23, 2), (23, 4), (21, 3), (21, 5), (19, 2), (19, 4)],
            // живі перешкоди (№89), лише з опцією «Живність»
            Critters:
            [
                RallyTrack.Critter(2, 7, 23, 40, 23, 300, 125),
                RallyTrack.Critter(0, 30, 1, 30, 6, 50, 170, group: 1),
            ]),
    ];

    public static readonly string[] Ids = [.. All.Select(t => t.Id)];

    /// <summary>Траса за id; невідомий — Село.</summary>
    public static RallyTrack Get(string id)
    {
        foreach (var t in All)
            if (t.Id == id) return t;
        return All[0];
    }
}
