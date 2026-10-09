using System.Text.Json.Nodes;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Цвіт папороті» (slot-cluster): поле 7×7, кластери від 5 однакових, що стикаються боками (листок папороті — дикий,
/// входить у будь-який кластер), каскади, шкала папороті з трьома рівнями (раз за оберт кожен): 1 — світлячки (3–6
/// клітинок стають дикими), 2 — русалка (один вид квітів стає іншим), 3 — цвіт папороті (дикий 3×3 посередині, вибух
/// 5×5, далі 5 вільних обертів, де шкала не скидається; знову до цвіту — +3, до 15). Усе — в одному оберті.
/// Повний порт моку <c>web/games/slots/slot-cluster.js</c> (simulate/resolve/clusters/planFlies/planMermaid) — сценарій
/// поле за полем тієї самої форми; розбіжності — у specs/slots.md §9.
/// </summary>
public sealed class SlotClusterMath : ISlotMath
{
    public const int C = 7, R = 7, N = C * R;
    public const int MinCluster = 5;

    /// <summary>Символи: 0–3 квіти (дешеві), 4–7 дорогі, 8 — листок папороті (дикий), 9 — цвіт (дикий 3×3).</summary>
    public static readonly string[] Keys = ["f1", "f2", "f3", "f4", "wreath", "candle", "fire", "comb", "fern", "bloom"];
    public const byte Fern = 8, Bloom = 9;
    const int PayCount = 8;
    static readonly byte[] Low = [0, 1, 2, 3];

    /// <summary>Ваги падіння (звичайна гра) — дослівно W8 з моку.</summary>
    public static readonly double[] Weights = [19, 18, 18, 17, 10, 9, 7, 5, 1.3];
    /// <summary>У вільних листків папороті більше: вага листка × це (FS_FERN моку).</summary>
    public const double FsFern = 2.2;

    /// <summary>Розміри кластера → сходинка виплати (SIZES моку).</summary>
    public static readonly int[] Sizes = [5, 6, 7, 8, 9, 11, 13, 16];

    /// <summary>
    /// Виплати у ставках за сходинками <see cref="Sizes"/>. Мок (RTP ≈ 98 % на його симуляції) мав: f1 0,2…5 · f2 0,2…8 ·
    /// f3 0,3…10 · f4 0,3…12 · wreath 0,6…20 · candle 0,8…30 · fire 1…50 · comb 1,5…100 (див. specs/slots.md §1.2).
    /// </summary>
    public static readonly double[][] Pay =
    [
        [0.2, 0.25, 0.3, 0.4, 0.6, 1, 2, 5],          // f1
        [0.2, 0.3, 0.4, 0.6, 0.8, 1.5, 3, 8],         // f2
        [0.3, 0.4, 0.5, 0.7, 1, 2, 4, 10],            // f3
        [0.3, 0.4, 0.6, 0.8, 1.2, 2.5, 5, 12],        // f4
        [0.6, 0.8, 1, 1.5, 2.5, 4, 8, 20],            // wreath
        [0.8, 1, 1.5, 2, 3, 5, 10, 30],               // candle
        [1, 1.5, 2, 3, 4, 8, 15, 50],                 // fire
        [1.5, 2, 3, 5, 8, 15, 30, 100],               // comb
    ];

    /// <summary>Рівні шкали (крапельки): світлячки, русалка, цвіт. Шкала — до останнього.</summary>
    public static readonly int[] Levels = [11, 30, 50];
    public static int MeterMax => Levels[^1];
    public const int FsCount = 5, FsAdd = 3, FsMax = 15;
    public const int Guard = 80;
    /// <summary>Світлячки: скільки спроб розкласти, з них беремо найкращу; і з яким шансом — просто випадкову.</summary>
    public const int FlyTries = 6;
    public const double FlyPlain = 0.35;
    /// <summary>Русалка: з цим шансом — найвигідніша заміна, інакше — будь-яка.</summary>
    public const double MermaidBest = 0.7;

    static readonly double[] CumBase = Cum(Weights, 1);
    static readonly double[] CumFs = Cum(Weights, FsFern);
    static readonly int[] BloomArea = Area(2, 4);
    static readonly int[] BoomArea = Area(1, 5);

    static double[] Cum(double[] w, double fernMul)
    {
        var c = new double[w.Length];
        double t = 0;
        for (var i = 0; i < w.Length; i++) { t += i == Fern ? w[i] * fernMul : w[i]; c[i] = t; }
        return c;
    }

    static int[] Area(int from, int to)
    {
        var l = new List<int>();
        for (var c = from; c <= to; c++) for (var r = from; r <= to; r++) l.Add(c * R + r);
        return [.. l];
    }

    /// <param name="cap">Стеля за оберт у ставках (тести стелі ставлять малу).</param>
    public SlotClusterMath(int cap = 5000) => Cap = cap;

    public int Cap { get; }
    public bool Gamble => false;

    static bool IsWild(byte k) => k >= Fern;

    /// <summary>Виплата кластера: остання сходинка, до якої дотягує розмір; не менше 1 черепка (як payOf у моку).</summary>
    public static long PayOf(int sym, int n, int bet)
    {
        var i = -1;
        for (var j = 0; j < Sizes.Length; j++) if (n >= Sizes[j]) i = j;
        return i < 0 ? 0 : Math.Max(1, (long)Math.Round(Pay[sym][i] * bet, MidpointRounding.AwayFromZero));
    }

    static byte Pick(double[] cum, ISlotRng rng)
    {
        var x = rng.NextDouble() * cum[^1];
        for (var i = 0; i < cum.Length; i++) if (x < cum[i]) return (byte)i;
        return (byte)(cum.Length - 1);
    }

    public readonly record struct Cluster(byte Sym, int[] Cells);

    /// <summary>
    /// Кластери (як clusters у моку): для кожного платного символу — зв'язні області «символ або дикий» від 5 клітинок.
    /// Дикий може входити в кілька кластерів різних символів. <paramref name="into"/> = null — лише сума виплат.
    /// </summary>
    public static long Scan(byte[] g, int bet, List<Cluster>? into)
    {
        Span<int> count = stackalloc int[Keys.Length];
        count.Clear();
        for (var i = 0; i < N; i++) count[g[i]]++;
        var wilds = count[Fern] + count[Bloom];
        Span<bool> seen = stackalloc bool[N];
        Span<int> vis = stackalloc int[N];   // мітка компоненти замість очищення на кожну
        vis.Clear();
        var stamp = 0;
        Span<int> stack = stackalloc int[N];
        Span<int> comp = stackalloc int[N];
        long sum = 0;
        for (byte s = 0; s < PayCount; s++)
        {
            if (count[s] == 0 || count[s] + wilds < MinCluster) continue;   // кластера цього символу не буде
            seen.Clear();
            for (var i0 = 0; i0 < N; i0++)
            {
                if (g[i0] != s || seen[i0]) continue;
                stamp++;
                int sp = 0, cn = 0;
                stack[sp++] = i0;
                vis[i0] = stamp;
                while (sp > 0)
                {
                    var p = stack[--sp];
                    comp[cn++] = p;
                    if (g[p] == s) seen[p] = true;
                    int x = p / R, y = p % R;
                    // сусіди в порядку моку: (1,0), (-1,0), (0,1), (0,-1)
                    for (var d = 0; d < 4; d++)
                    {
                        int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0), ny = y + (d == 2 ? 1 : d == 3 ? -1 : 0);
                        if (nx < 0 || ny < 0 || nx >= C || ny >= R) continue;
                        var q = nx * R + ny;
                        if (vis[q] == stamp) continue;
                        var k = g[q];
                        if (k == s || IsWild(k)) { vis[q] = stamp; stack[sp++] = q; }
                    }
                }
                if (cn < MinCluster) continue;
                sum += PayOf(s, cn, bet);
                into?.Add(new Cluster(s, comp[..cn].ToArray()));
            }
        }
        return sum;
    }

    /// <summary>Підсумок оберту для симуляцій (без JSON) і для сценарію.</summary>
    public sealed class Result
    {
        public long Win;
        public bool Bonus, Capped;
        public int Chain, MaxCl, FreeSpins;
        public long FsWon;
        public readonly int[] Lv = new int[3];
        public JsonObject? Script;
    }

    sealed class Run(int bet, ISlotRng rng, bool script, long capAmount)
    {
        public readonly int Bet = bet;
        public readonly ISlotRng Rng = rng;
        public readonly JsonArray? Steps = script ? [] : null;
        public readonly long CapAmount = capAmount;
        public readonly Result Info = new();
        public readonly bool[] Done = new bool[3];
        public double[] Cum = CumBase;
        public byte[] Grid = new byte[N];
        public int Meter;
        public bool Fs, Bloom;
        public int FsLeft, FsTotal;
        public long Total;
    }

    public SlotOutcome Spin(int bet, ISlotRng rng, JsonObject state)
    {
        var r = Play(bet, rng, true);
        var flags = new List<string>();
        if (r.Bonus) flags.Add("bonus");
        if (r.Capped) flags.Add("cap");
        return new SlotOutcome(r.Script!, (int)r.Win, flags, new JsonObject());
    }

    /// <summary>Оберт (simulate моку). <paramref name="script"/> = false — без сценарію, для мільйонних симуляцій.</summary>
    public Result Play(int bet, ISlotRng rng, bool script)
    {
        var S = new Run(bet, rng, script, (long)Cap * bet);
        Fill(S);
        S.Steps?.Add(new JsonObject { ["t"] = "spin", ["grid"] = GridJson(S.Grid) });
        var total = Resolve(S);
        var info = S.Info;
        if (S.Bloom && !info.Capped)
        {
            info.Bonus = true;
            S.Steps?.Add(new JsonObject { ["t"] = "bonusIn", ["count"] = FsCount, ["title"] = "Цвіт папороті", ["sub"] = "вільних обертів · шкала не скидається" });
            S.Steps?.Add(new JsonObject { ["t"] = "fern", ["to"] = 0 });
            S.Fs = true;
            S.FsLeft = FsCount;
            S.FsTotal = FsCount;
            S.Meter = 0;
            Array.Clear(S.Done);
            S.Cum = CumFs;
            long fsWon = 0;
            while (S.FsLeft > 0 && !info.Capped)
            {
                S.FsLeft--;
                info.FreeSpins++;
                S.Steps?.Add(new JsonObject { ["t"] = "fs", ["left"] = S.FsLeft });
                Fill(S);
                S.Bloom = false;
                S.Steps?.Add(new JsonObject { ["t"] = "spin", ["grid"] = GridJson(S.Grid) });
                fsWon += Resolve(S);
            }
            S.Steps?.Add(new JsonObject { ["t"] = "bonusOut", ["total"] = fsWon, ["title"] = "Цвіт папороті приніс" });
            info.FsWon = fsWon;
            total += fsWon;
        }
        S.Steps?.Add(new JsonObject { ["t"] = "fern", ["to"] = 0, ["end"] = true });
        info.Win = total;
        if (script)
        {
            var inf = new JsonObject
            {
                ["chain"] = info.Chain,
                ["lv"] = new JsonArray(info.Lv[0], info.Lv[1], info.Lv[2]),
                ["maxCl"] = info.MaxCl,
                ["bonus"] = info.Bonus,
            };
            if (info.Bonus) inf["fsWon"] = info.FsWon;
            info.Script = new JsonObject
            {
                ["bet"] = bet,
                ["steps"] = S.Steps,
                ["win"] = total,
                ["state"] = new JsonObject(),
                ["info"] = inf,
            };
            if (info.Capped) info.Script["cap"] = true;
        }
        return info;
    }

    void Fill(Run S)
    {
        for (var i = 0; i < N; i++) S.Grid[i] = Pick(S.Cum, S.Rng);   // по колонках, згори вниз — як fresh() у моку
    }

    /// <summary>Розіграш поля до кінця: каскади, рівні шкали (resolve моку). Повертає виграш цього поля.</summary>
    long Resolve(Run S)
    {
        long won = 0;
        int chain = 0;
        var boom = false;
        var info = S.Info;
        var cl = new List<Cluster>();
        Span<bool> mask = stackalloc bool[N];
        var rem = new List<int>(N);
        for (var guard = 0; guard < Guard; guard++)
        {
            cl.Clear();
            Scan(S.Grid, S.Bet, cl);
            if (cl.Count > 0 || boom)
            {
                mask.Clear();
                rem.Clear();
                if (cl.Count > 0)
                {
                    long amount = 0;
                    var items = S.Steps is null ? null : new JsonArray();
                    foreach (var k in cl)
                    {
                        var a = PayOf(k.Sym, k.Cells.Length, S.Bet);
                        amount += a;
                        info.MaxCl = Math.Max(info.MaxCl, k.Cells.Length);
                        foreach (var p in k.Cells) if (!mask[p]) { mask[p] = true; rem.Add(p); }
                        items?.Add(new JsonObject { ["cells"] = CellsJson(k.Cells), ["sym"] = Keys[k.Sym], ["n"] = k.Cells.Length, ["amount"] = a });
                    }
                    chain++;
                    // Стеля 5000× за оберт: виграш, що до неї дотягнув, урізається, і на цьому оберт закінчується.
                    if (S.Total + amount >= S.CapAmount)
                    {
                        amount = S.CapAmount - S.Total;
                        info.Capped = true;
                    }
                    won += amount;
                    S.Total += amount;
                    S.Steps?.Add(new JsonObject { ["t"] = "win", ["items"] = items, ["amount"] = amount, ["hold"] = 250 });
                    if (info.Capped) break;
                }
                if (boom) foreach (var p in BoomArea) if (!mask[p]) { mask[p] = true; rem.Add(p); }
                var before = S.Meter;
                S.Meter = Math.Min(MeterMax, S.Meter + (boom ? 0 : rem.Count));
                Fall(S, mask);
                if (S.Steps is not null)
                {
                    var st = new JsonObject
                    {
                        ["t"] = "cascade", ["remove"] = CellsJson(rem), ["grid"] = GridJson(S.Grid), ["n"] = chain,
                        ["fern"] = S.Meter, ["from"] = before,
                    };
                    if (boom) st["boom"] = true;
                    S.Steps.Add(st);
                }
                if (boom)
                {
                    boom = false;
                    if (S.Fs)
                    {
                        // у вільних шкала доходить до цвіту — +3 оберти (до 15 разом) і знову з нуля
                        var add = Math.Min(FsAdd, FsMax - S.FsTotal);
                        if (add > 0)
                        {
                            S.FsTotal += add;
                            S.FsLeft += add;
                            S.Steps?.Add(new JsonObject { ["t"] = "fs", ["add"] = add });
                        }
                        S.Meter = 0;
                        Array.Clear(S.Done);
                        S.Steps?.Add(new JsonObject { ["t"] = "fern", ["to"] = 0 });
                    }
                }
                continue;
            }
            // виграшів нема — чи досягнуто рівня шкали?
            var L = -1;
            for (var i = 0; i < 3; i++) if (!S.Done[i] && S.Meter >= Levels[i]) { L = i; break; }
            if (L < 0) break;
            S.Done[L] = true;
            info.Lv[L]++;
            if (L == 0)
            {
                var cells = PlanFlies(S);
                foreach (var p in cells) S.Grid[p] = Fern;
                S.Steps?.Add(new JsonObject { ["t"] = "lvl", ["n"] = 1, ["cells"] = MorphJson(cells, _ => Fern) });
            }
            else if (L == 1)
            {
                if (PlanMermaid(S) is not { } m) continue;
                foreach (var p in m.Cells) S.Grid[p] = m.To;
                S.Steps?.Add(new JsonObject
                {
                    ["t"] = "lvl", ["n"] = 2, ["from"] = Keys[m.From], ["to"] = Keys[m.To], ["cells"] = MorphJson(m.Cells, _ => m.To),
                });
            }
            else
            {
                foreach (var p in BloomArea) S.Grid[p] = Bloom;
                S.Steps?.Add(new JsonObject { ["t"] = "lvl", ["n"] = 3, ["cells"] = MorphJson(BloomArea, _ => Bloom) });
                boom = true;
                S.Bloom = true;
            }
        }
        info.Chain = Math.Max(info.Chain, chain);
        return won;
    }

    /// <summary>Падіння (fall моку): у колонці лишаються незгаслі (порядок той самий), згори — нові.</summary>
    static void Fall(Run S, ReadOnlySpan<bool> mask)
    {
        Span<byte> keep = stackalloc byte[R];
        for (var c = 0; c < C; c++)
        {
            var kn = 0;
            for (var r = 0; r < R; r++) if (!mask[c * R + r]) keep[kn++] = S.Grid[c * R + r];
            var fresh = R - kn;
            for (var r = 0; r < fresh; r++) S.Grid[c * R + r] = Pick(S.Cum, S.Rng);
            for (var r = 0; r < kn; r++) S.Grid[c * R + fresh + r] = keep[r];
        }
    }

    /// <summary>Випадкові n різних клітинок зі списку (рівномірно, як shuffle(...).slice(0, n) у моку).</summary>
    static int[] Sample(List<int> from, int n, ISlotRng rng)
    {
        var a = from.ToArray();
        n = Math.Min(n, a.Length);
        for (var i = 0; i < n; i++)
        {
            var j = i + rng.Next(a.Length - i);
            (a[i], a[j]) = (a[j], a[i]);
        }
        return a[..n];
    }

    /// <summary>Світлячки: 3–6 клітинок стають дикими; з 6 розкладів — найвигідніший, але з шансом 35 % — просто випадковий.</summary>
    static int[] PlanFlies(Run S)
    {
        var free = new List<int>(N);
        for (var i = 0; i < N; i++) if (!IsWild(S.Grid[i])) free.Add(i);
        var n = 3 + S.Rng.Next(4);
        int[] best = [];
        long bw = -1;
        var h = new byte[N];
        for (var t = 0; t < FlyTries; t++)
        {
            var set = Sample(free, n, S.Rng);
            Array.Copy(S.Grid, h, N);
            foreach (var p in set) h[p] = Fern;
            var w = Scan(h, S.Bet, null);
            if (w > bw) { bw = w; best = set; }
        }
        if (S.Rng.NextDouble() < FlyPlain) best = Sample(free, n, S.Rng);
        return best;
    }

    readonly record struct Mermaid(byte From, byte To, int[] Cells, long W);

    /// <summary>Русалка: один вид квітів → інший; з шансом 70 % — найвигідніша заміна, інакше будь-яка.</summary>
    static Mermaid? PlanMermaid(Run S)
    {
        var opts = new List<Mermaid>(12);
        var h = new byte[N];
        foreach (var a in Low)
            foreach (var b in Low)
            {
                if (a == b) continue;
                var cells = new List<int>();
                for (var i = 0; i < N; i++) if (S.Grid[i] == a) cells.Add(i);
                if (cells.Count == 0) continue;
                Array.Copy(S.Grid, h, N);
                foreach (var p in cells) h[p] = b;
                opts.Add(new Mermaid(a, b, [.. cells], Scan(h, S.Bet, null)));
            }
        if (opts.Count == 0) return null;
        var sorted = opts.OrderByDescending(o => o.W).ToList();   // стабільно, як sort у моку
        return S.Rng.NextDouble() < MermaidBest ? sorted[0] : sorted[S.Rng.Next(sorted.Count)];
    }

    // ---------- JSON ----------

    static JsonArray GridJson(byte[] g)
    {
        var cols = new JsonArray();
        for (var c = 0; c < C; c++)
        {
            var col = new JsonArray();
            for (var r = 0; r < R; r++) col.Add(Keys[g[c * R + r]]);
            cols.Add(col);
        }
        return cols;
    }

    static JsonArray CellsJson(IEnumerable<int> cells)
    {
        var a = new JsonArray();
        foreach (var p in cells) a.Add(new JsonArray(p / R, p % R));
        return a;
    }

    static JsonArray MorphJson(IEnumerable<int> cells, Func<int, byte> key)
    {
        var a = new JsonArray();
        foreach (var p in cells) a.Add(new JsonArray(p / R, p % R, Keys[key(p)]));
        return a;
    }

    public JsonObject Table()
    {
        var pay = new JsonObject();
        for (var s = 0; s < PayCount; s++)
        {
            var o = new JsonObject();
            for (var j = 0; j < Sizes.Length; j++) o[Sizes[j].ToString()] = Pay[s][j];
            pay[Keys[s]] = o;
        }
        var w = new JsonObject();
        for (var i = 0; i < Weights.Length; i++) w[Keys[i]] = Weights[i];
        return new JsonObject
        {
            ["cols"] = C, ["rows"] = R, ["min"] = MinCluster,
            ["sizes"] = new JsonArray([.. Sizes.Select(x => (JsonNode)x)]),
            ["pay"] = pay, ["payUnit"] = 1, ["wild"] = "fern", ["weights"] = w, ["fsFern"] = FsFern,
            ["levels"] = new JsonArray([.. Levels.Select(x => (JsonNode)x)]),
            ["fs"] = new JsonObject { ["count"] = FsCount, ["add"] = FsAdd, ["max"] = FsMax },
            ["cap"] = Cap,
        };
    }
}
