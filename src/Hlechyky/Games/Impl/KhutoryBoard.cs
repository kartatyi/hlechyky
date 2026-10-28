namespace Hlechyky.Games.Impl;

/// <summary>
/// Геометрія поля «Хуторів» (контракт §4): 19 гексів «вершиною догори», 54 вершини, 72 ребра, 30 берегових ребер.
/// Однакова для всіх партій, тож рахується раз. Алгоритм — рівно той, що в <c>web/games/khutory/dev/fixture.js</c>:
/// сервер і стенди мусять давати ті самі номери вершин і ребер.
/// </summary>
public sealed class KhutoryGeo
{
    public const int Hexes = 19, Verts = 54, EdgeCount = 72;

    public static readonly KhutoryGeo I = new();

    public readonly int[] Q = new int[Hexes], R = new int[Hexes];
    /// <summary>Координати вершин, 4 знаки після коми (як у фікстурі).</summary>
    public readonly double[][] Vx;
    /// <summary>6 вершин гекса в порядку кутів (0 — верх, далі за годинниковою).</summary>
    public readonly int[][] HexV = new int[Hexes][];
    /// <summary>Ребро — пара вершин, a &lt; b.</summary>
    public readonly int[][] Edges;
    /// <summary>Берегові ребра (належать одному гексу), відсортовані за кутом середини від верху за годинниковою.</summary>
    public readonly int[] Coast;
    /// <summary>Вершина → сусідні вершини / ребра при ній / гекси, яких торкається.</summary>
    public readonly int[][] VNb, VE, VH;
    /// <summary>Гекс → гекси зі спільним ребром.</summary>
    public readonly int[][] HNb;

    /// <summary>Номери берегових ребер, на яких стоять пристані.</summary>
    public static readonly int[] PortSlots = [0, 3, 7, 10, 13, 17, 20, 23, 27];

    KhutoryGeo()
    {
        var vx = new List<double[]>();
        var vkey = new Dictionary<(long, long), int>();
        var edges = new List<int[]>();
        var ekey = new Dictionary<(int, int), int>();
        var ecount = new List<int>();
        var edgeHexes = new List<List<int>>();
        var h = 0;
        for (var r = -2; r <= 2; r++)
            for (var q = Math.Max(-2, -2 - r); q <= Math.Min(2, 2 - r); q++)
            {
                Q[h] = q; R[h] = r;
                var cx = Math.Sqrt(3) * (q + r / 2.0);
                var cy = 1.5 * r;
                var hv = new int[6];
                for (var i = 0; i < 6; i++)
                {
                    var a = (-90 + 60 * i) * Math.PI / 180;
                    var x = cx + Math.Cos(a);
                    var y = cy + Math.Sin(a);
                    var k = (JsRound(x * 1000), JsRound(y * 1000));
                    if (!vkey.TryGetValue(k, out var id))
                    {
                        id = vx.Count;
                        vkey[k] = id;
                        vx.Add([Round4(x), Round4(y)]);
                    }
                    hv[i] = id;
                }
                HexV[h] = hv;
                for (var i = 0; i < 6; i++)
                {
                    var a = Math.Min(hv[i], hv[(i + 1) % 6]);
                    var b = Math.Max(hv[i], hv[(i + 1) % 6]);
                    if (!ekey.TryGetValue((a, b), out var e))
                    {
                        e = edges.Count;
                        ekey[(a, b)] = e;
                        edges.Add([a, b]);
                        ecount.Add(0);
                        edgeHexes.Add([]);
                    }
                    ecount[e]++;
                    edgeHexes[e].Add(h);
                }
                h++;
            }
        Vx = [.. vx];
        Edges = [.. edges];

        double Angle(int e)
        {
            var (a, b) = (Edges[e][0], Edges[e][1]);
            var mx = (Vx[a][0] + Vx[b][0]) / 2;
            var my = (Vx[a][1] + Vx[b][1]) / 2;
            return (Math.Atan2(my, mx) + Math.PI / 2 + 4 * Math.PI) % (2 * Math.PI);
        }
        Coast = [.. Enumerable.Range(0, Edges.Length).Where(e => ecount[e] == 1).OrderBy(Angle)];

        var nb = Enumerable.Range(0, Verts).Select(_ => new List<int>()).ToArray();
        var ve = Enumerable.Range(0, Verts).Select(_ => new List<int>()).ToArray();
        var vh = Enumerable.Range(0, Verts).Select(_ => new List<int>()).ToArray();
        for (var e = 0; e < Edges.Length; e++)
        {
            var (a, b) = (Edges[e][0], Edges[e][1]);
            nb[a].Add(b); nb[b].Add(a);
            ve[a].Add(e); ve[b].Add(e);
        }
        for (var x = 0; x < Hexes; x++)
            foreach (var v in HexV[x]) vh[v].Add(x);
        VNb = [.. nb.Select(l => l.ToArray())];
        VE = [.. ve.Select(l => l.ToArray())];
        VH = [.. vh.Select(l => l.ToArray())];
        var hn = Enumerable.Range(0, Hexes).Select(_ => new SortedSet<int>()).ToArray();
        foreach (var list in edgeHexes)
            if (list.Count == 2) { hn[list[0]].Add(list[1]); hn[list[1]].Add(list[0]); }
        HNb = [.. hn.Select(s => s.ToArray())];
    }

    /// <summary>Інший кінець ребра.</summary>
    public int Other(int e, int v) => Edges[e][0] == v ? Edges[e][1] : Edges[e][0];

    /// <summary>Math.round з JS: половина — вгору (а не до парного, як у .NET).</summary>
    static long JsRound(double x) => (long)Math.Floor(x + 0.5);

    /// <summary>4 знаки, як у фікстурі; «-0» стає нулем.</summary>
    static double Round4(double x) => Math.Floor(x * 10000 + 0.5) / 10000 + 0.0;
}

/// <summary>
/// Карта однієї партії: земля й число кожного гекса, пристані. Земля з номером 0..4 родить ресурс із тим самим номером
/// (ліс → дерево, глинище → глина, пасовище → вовна, лан → зерно, гори → залізо); 5 — Дике поле.
/// </summary>
public sealed class KhutoryBoard
{
    public static readonly string[] Terrains = ["forest", "pit", "pasture", "field", "mountain", "steppe"];
    public static readonly string[] Res = ["wood", "clay", "wool", "grain", "ore"];
    public const int Steppe = 5;
    /// <summary>Вид пристані «3:1 будь-чим».</summary>
    public const int AnyPort = -1;

    public string Id = "";
    /// <summary>Земля гекса (індекс у <see cref="Terrains"/>).</summary>
    public int[] T = new int[KhutoryGeo.Hexes];
    /// <summary>Число гекса; 0 — Дике поле.</summary>
    public int[] Num = new int[KhutoryGeo.Hexes];
    /// <summary>Ребро кожної з 9 пристаней.</summary>
    public int[] PortEdge = new int[9];
    /// <summary>Вид пристані: <see cref="AnyPort"/> або ресурс 0..4.</summary>
    public int[] PortKind = new int[9];

    /// <summary>Ресурс, який родить гекс (-1 — Дике поле).</summary>
    public int ResOf(int h) => T[h] == Steppe ? -1 : T[h];

    /// <summary>Скільки «крапок» (шансів із 36) у числа.</summary>
    public static int Pips(int n) => n is < 2 or > 12 or 7 ? 0 : 6 - Math.Abs(7 - n);

    public int SteppeHex => Array.IndexOf(T, Steppe);

    static readonly int[] StarterT = [0, 2, 3, 3, 1, 2, 4, 4, 3, 5, 0, 1, 0, 4, 2, 3, 1, 0, 2];
    static readonly int[] StarterN = [10, 2, 9, 12, 6, 4, 10, 9, 11, 0, 3, 8, 8, 3, 4, 5, 5, 6, 11];
    static readonly int[] StarterPorts = [AnyPort, 2, AnyPort, 4, 3, AnyPort, 1, 0, AnyPort];

    /// <summary>Стартова карта для новачків — завжди та сама (контракт §4).</summary>
    public static KhutoryBoard Starter()
    {
        var b = new KhutoryBoard { Id = "starter", T = (int[])StarterT.Clone(), Num = (int[])StarterN.Clone() };
        b.SetPorts(StarterPorts);
        return b;
    }

    /// <summary>
    /// Випадкова рівна карта: тасуємо землі й числа, доки жодні два «червоні» (6, 8) і жодні два однакові числа
    /// не сусідять (до 2000 спроб; далі досить першої умови).
    /// </summary>
    public static KhutoryBoard Random(Random rng)
    {
        var lands = new List<int>();
        foreach (var (t, n) in new[] { (0, 4), (2, 4), (3, 4), (1, 3), (4, 3), (Steppe, 1) })
            for (var i = 0; i < n; i++) lands.Add(t);
        int[] nums = [2, 3, 3, 4, 4, 5, 5, 6, 6, 8, 8, 9, 9, 10, 10, 11, 11, 12];
        var b = new KhutoryBoard();
        for (var attempt = 0; ; attempt++)
        {
            var t = lands.ToArray();
            var n = (int[])nums.Clone();
            Shuffle(t, rng);
            Shuffle(n, rng);
            var k = 0;
            for (var h = 0; h < KhutoryGeo.Hexes; h++) b.Num[h] = t[h] == Steppe ? 0 : n[k++];
            b.T = t;
            if (b.RedsApart() && (attempt >= 2000 || b.SameApart())) break;
            if (attempt > 100_000) break;   // теоретично недосяжно; не висіти ж вічно
        }
        int[] kinds = [AnyPort, AnyPort, AnyPort, AnyPort, 0, 1, 2, 3, 4];
        Shuffle(kinds, rng);
        b.SetPorts(kinds);
        b.Id = rng.Next(0x1000000).ToString("x6");
        return b;
    }

    void SetPorts(int[] kinds)
    {
        var g = KhutoryGeo.I;
        for (var i = 0; i < 9; i++)
        {
            PortEdge[i] = g.Coast[KhutoryGeo.PortSlots[i]];
            PortKind[i] = kinds[i];
        }
    }

    /// <summary>Жодні два гекси з 6 чи 8 не мають спільного ребра.</summary>
    public bool RedsApart()
    {
        var g = KhutoryGeo.I;
        for (var h = 0; h < KhutoryGeo.Hexes; h++)
            if (Num[h] is 6 or 8)
                foreach (var o in g.HNb[h])
                    if (Num[o] is 6 or 8) return false;
        return true;
    }

    /// <summary>Жодні два сусіди не мають однакового числа.</summary>
    public bool SameApart()
    {
        var g = KhutoryGeo.I;
        for (var h = 0; h < KhutoryGeo.Hexes; h++)
            if (Num[h] != 0)
                foreach (var o in g.HNb[h])
                    if (Num[o] == Num[h]) return false;
        return true;
    }

    static void Shuffle(int[] a, Random rng)
    {
        for (var i = a.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (a[i], a[j]) = (a[j], a[i]);
        }
    }
}
