using System.Text.Json.Nodes;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Сліди на полиці» (slot-slidy, docs/games/specs/slot-slidy.md): полиця 6×6 посуду, платить <b>кластер</b> — 5 і
/// більше однакових, що стикаються боками. Виграшний посуд б'ється, решта падає, згори — нові (каскад) аж до спокою.
/// На кожній клітинці, де розбився виграшний посуд, лишається <b>слід від глини</b>: перший — мокре коло (×1, ще не
/// множить), удруге там же — випалений слід ×2, далі ×4, ×8 … до ×128. Виграш кластера множиться на <b>суму</b> слідів
/// ×2+ під ним (мокрі не рахуються; жодного — ×1). Сліди лежать на полиці (не їздять із посудом) і в базі живуть лише
/// до кінця каскаду свого оберту. Горно — скатер: 3+ на полиці після каскаду → 10 вільних обертів, де сліди не стираються
/// весь бонус; 3+ у вільних — ще +10 (разом не більше 50). Стеля — 5000× ставки за оберт разом із бонусом.
///
/// «Купити бонус» (<see cref="Buy"/>) — ті самі 10 вільних обертів одразу за ціну з конфігу (вимикач <c>Slots:BuyBonus</c>).
/// </summary>
public sealed class SlotSlidyMath : ISlotMath, ISlotBuyBonus
{
    public const int Cols = 6, Rows = 6, Cells = Cols * Rows;
    public const int MinCluster = 5, Scatters = 3, FreeSpins = 10, Retrigger = 10, MaxFreeSpins = 50;
    public const int MaxTrace = 128, MaxCascades = 60;
    public const string Furnace = "furnace";

    /// <summary>
    /// Символи, що платять: дешеві кахлі k1…k3, далі посуд до глека (арт — з «Розбитих глеків»). Сім, а не дев'ять: на
    /// полиці 6×6 з дев'ятьма кластери 5+ випадали б лише на дешевих, а дорогий посуд був би декорацією (spec, рішення).
    /// </summary>
    public static readonly string[] Paying = ["k1", "k2", "k3", "pot", "makitra", "kumanets", "glek"];

    /// <summary>Коди поля: 0…6 — <see cref="Paying"/>, 7 — горно.</summary>
    public const int FurnaceCode = 7;
    public const int Kinds = 8;

    /// <summary>Яруси кластера: 5, 6, 7, 8, 9–10, 11–12, 13–15, 16+.</summary>
    public static readonly int[] TierFrom = [5, 6, 7, 8, 9, 11, 13, 16];
    public const int Tiers = 8;

    public static int Tier(int n) => n >= 16 ? 7 : n >= 13 ? 6 : n >= 11 ? 5 : n >= 9 ? 4 : n - 5;

    /// <summary>
    /// Виплати в ДЕСЯТИХ частках ставки за [5, 6, 7, 8, 9–10, 11–12, 13–15, 16+]. Ставки кратні 10 — виплати рівні, а
    /// множники слідів цілі, тож RTP однаковий на кожній ставці. Підібрано симуляцією під RTP бази 97 % (spec, §Математика).
    /// </summary>
    public static readonly int[][] Pay10 =
    [
        [5, 6, 8, 10, 15, 25, 50, 120],       // k1
        [6, 8, 10, 12, 20, 40, 80, 200],      // k2
        [8, 10, 12, 16, 25, 50, 100, 250],    // k3
        [12, 15, 20, 25, 40, 80, 150, 400],   // pot
        [15, 20, 25, 30, 50, 100, 200, 500],  // makitra
        [20, 25, 30, 40, 60, 120, 250, 800],  // kumanets
        [25, 40, 50, 60, 100, 200, 500, 2000], // glek
    ];

    /// <summary>Ваги падіння: 7 символів, що платять, і горно (останнє).</summary>
    public sealed class Weights(double[] sym)
    {
        public double[] Sym { get; } = sym;
        public double Total { get; } = sym.Sum();
    }

    //                                            k1  k2  k3 pot mak kum glek furn
    public static readonly Weights Base = new([26, 24, 22, 16, 12, 9, 6, 1.1]);
    public static readonly Weights Free = new([33, 31, 28, 13, 10, 7, 5, 1.0]);

    public const int CapX = 5000;
    public int Cap => CapX;
    public bool Gamble => false;

    public static string Key(int code) => code < FurnaceCode ? Paying[code] : Furnace;

    public static int Code(string key)
    {
        if (key == Furnace) return FurnaceCode;
        var p = Array.IndexOf(Paying, key);
        return p >= 0 ? p : throw new ArgumentException($"нема символу {key}");
    }

    /// <summary>Як SK.rnd.weighted: x = rnd × сума, віднімати ваги по черзі.</summary>
    public static int Draw(Weights w, ISlotRng rng)
    {
        var x = rng.NextDouble() * w.Total;
        var s = w.Sym;
        for (var i = 0; i < s.Length; i++)
        {
            x -= s[i];
            if (x < 0) return i;
        }
        return 0;
    }

    /// <summary>Виграш кластера без слідів у черепках: max(1, round(u × ставка)); для ставок, кратних 10, — рівно.</summary>
    public static long PayOf(int sym, int n, int bet) =>
        Math.Max(1, (long)Math.Round(Pay10[sym][Tier(n)] * (double)bet / 10, MidpointRounding.AwayFromZero));

    /// <summary>Наступний слід: нема → мокре коло (1) → ×2 → ×4 … → ×128.</summary>
    public static int NextTrace(int t) => t <= 0 ? 1 : Math.Min(MaxTrace, t * 2);

    /// <summary>Множник кластера: сума слідів ×2+ під ним; мокрі (×1) не рахуються; жодного — 1.</summary>
    public static int MultOf(ReadOnlySpan<int> traces, ReadOnlySpan<int> cells)
    {
        var m = 0;
        foreach (var p in cells) if (traces[p] >= 2) m += traces[p];
        return m > 0 ? m : 1;
    }

    // ---------------------------------------------------------------------------------------------- кластери

    /// <summary>Кластер: символ і клітинки (індекс c × Rows + r).</summary>
    public readonly record struct Cluster(int Sym, int[] Cells);

    /// <summary>Усі кластери 5+ на полі (по символах, далі за першою клітинкою по колонках).</summary>
    public static void Scan(ReadOnlySpan<int> g, List<Cluster> into)
    {
        into.Clear();
        Span<int> count = stackalloc int[Kinds];
        count.Clear();
        for (var i = 0; i < Cells; i++) count[g[i]]++;
        Span<bool> seen = stackalloc bool[Cells];
        seen.Clear();
        Span<int> stack = stackalloc int[Cells];
        Span<int> comp = stackalloc int[Cells];
        for (var i0 = 0; i0 < Cells; i0++)
        {
            var s = g[i0];
            if (seen[i0] || s == FurnaceCode || count[s] < MinCluster) continue;
            int sp = 0, cn = 0;
            stack[sp++] = i0;
            seen[i0] = true;
            while (sp > 0)
            {
                var p = stack[--sp];
                comp[cn++] = p;
                int x = p / Rows, y = p % Rows;
                if (x > 0 && !seen[p - Rows] && g[p - Rows] == s) { seen[p - Rows] = true; stack[sp++] = p - Rows; }
                if (x < Cols - 1 && !seen[p + Rows] && g[p + Rows] == s) { seen[p + Rows] = true; stack[sp++] = p + Rows; }
                if (y > 0 && !seen[p - 1] && g[p - 1] == s) { seen[p - 1] = true; stack[sp++] = p - 1; }
                if (y < Rows - 1 && !seen[p + 1] && g[p + 1] == s) { seen[p + 1] = true; stack[sp++] = p + 1; }
            }
            if (cn < MinCluster) continue;
            var cells = comp[..cn].ToArray();
            Array.Sort(cells);
            into.Add(new Cluster(s, cells));
        }
        into.Sort((a, b) => a.Sym != b.Sym ? a.Sym.CompareTo(b.Sym) : a.Cells[0].CompareTo(b.Cells[0]));
    }

    // ---------------------------------------------------------------------------------------------- лічильники

    /// <summary>Лічильники для симуляцій: скільки разів платила кожна група з урахуванням множника слідів.</summary>
    public sealed class Probe
    {
        /// <summary>[символ × 8 + ярус] — сума множників, з якими платила група, у базі й у вільних.</summary>
        public double[] BaseCoef { get; } = new double[Paying.Length * Tiers];
        public double[] FreeCoef { get; } = new double[Paying.Length * Tiers];
        public long Spins, Bonuses, Retriggers, FreeSpinsPlayed, Capped, BaseHits;
        /// <summary>Скільки виграно (у черепках, зі стелею) у базових каскадах і у вільних обертах.</summary>
        public long BaseWon, FreeWon;
        /// <summary>Найбільший множник кластера (сума слідів), що платив.</summary>
        public int TopMult;
        /// <summary>Не грати бонус (рахується лише вхід) — для розшарованої оцінки RTP.</summary>
        public bool SkipBonus { get; init; }
    }

    // ---------------------------------------------------------------------------------------------- оберт

    /// <summary>Стан одного оберту (з бонусом): виграш, стеля, сценарій.</summary>
    sealed class Run(int bet, long capAmt, JsonArray? steps, Probe? probe)
    {
        public readonly int Bet = bet;
        public readonly long CapAmt = capAmt;
        public readonly JsonArray? Steps = steps;
        public readonly Probe? Probe = probe;
        public long Total;
        public bool Capped;
        public int TopMult;
        public int MaxCasc;
        public readonly List<Cluster> Found = new(8);
    }

    /// <summary>
    /// Каскад до спокою. <paramref name="traces"/> — сліди на полиці (у базі — свіжі на кожен оберт, у вільних — спільні
    /// на весь бонус). Кожна ланка: усі кластери платять (кожен × сума слідів ×2+ під ним), далі сліди під ними
    /// ростуть, посуд б'ється й падає. Досягли стелі — виграш урізається, і на цьому все.
    /// </summary>
    static int[] Tumble(int[] grid, int[] traces, Weights w, bool fs, ISlotRng rng, Run S)
    {
        var steps = S.Steps;
        Span<bool> rem = stackalloc bool[Cells];
        var n = 0;
        for (var guard = 0; guard < MaxCascades; guard++)
        {
            Scan(grid, S.Found);
            if (S.Found.Count == 0) break;
            long amount = 0;
            JsonArray? items = steps is null ? null : new JsonArray();
            rem.Clear();
            foreach (var k in S.Found)
            {
                var m = MultOf(traces, k.Cells);
                var pay = PayOf(k.Sym, k.Cells.Length, S.Bet);
                var a = pay * m;
                amount += a;
                if (m > S.TopMult) S.TopMult = m;
                if (S.Probe is { } pr)
                {
                    (fs ? pr.FreeCoef : pr.BaseCoef)[k.Sym * Tiers + Tier(k.Cells.Length)] += m;
                    if (m > pr.TopMult) pr.TopMult = m;
                }
                foreach (var p in k.Cells) rem[p] = true;
                items?.Add(new JsonObject
                {
                    ["sym"] = Paying[k.Sym], ["n"] = k.Cells.Length, ["cells"] = CellsJson(k.Cells), ["pay"] = pay, ["mult"] = m, ["amount"] = a,
                });
            }
            n++;
            var capped = S.Total + amount >= S.CapAmt;
            if (capped)
            {
                amount = S.CapAmt - S.Total;
                S.Capped = true;
            }
            S.Total += amount;
            steps?.Add(new JsonObject { ["t"] = "win", ["items"] = items, ["amount"] = amount, ["hold"] = 240 });
            if (capped) break;

            // сліди під розбитим посудом ростуть (і мокрі стають ×2)
            JsonArray? up = steps is null ? null : new JsonArray();
            JsonArray? remove = steps is null ? null : new JsonArray();
            for (var i = 0; i < Cells; i++)
            {
                if (!rem[i]) continue;
                traces[i] = NextTrace(traces[i]);
                up?.Add(new JsonArray(i / Rows, i % Rows, traces[i]));
                remove?.Add(new JsonArray(i / Rows, i % Rows));
            }
            // падіння: у колонці лишаються нерозбиті (згори вниз), нові — згори
            var next = new int[Cells];
            for (var c = 0; c < Cols; c++)
            {
                var keep = 0;
                for (var r = 0; r < Rows; r++) if (!rem[c * Rows + r]) keep++;
                var fresh = Rows - keep;
                for (var r = 0; r < fresh; r++) next[c * Rows + r] = Draw(w, rng);
                var at = fresh;
                for (var r = 0; r < Rows; r++) if (!rem[c * Rows + r]) next[c * Rows + at++] = grid[c * Rows + r];
            }
            grid = next;
            steps?.Add(new JsonObject { ["t"] = "cascade", ["remove"] = remove, ["grid"] = GridJson(grid), ["n"] = n, ["up"] = up });
        }
        if (n > S.MaxCasc) S.MaxCasc = n;
        return grid;
    }

    static int Count(int[] g, int code)
    {
        var n = 0;
        foreach (var k in g) if (k == code) n++;
        return n;
    }

    static int[] RandGrid(Weights w, ISlotRng rng)
    {
        var g = new int[Cells];
        for (var i = 0; i < Cells; i++) g[i] = Draw(w, rng);
        return g;
    }

    /// <summary>
    /// Очікування — лише чесно: колонки, що падають, коли до скатера бракує рівно одного горна (як teaseFor у «Розбитих
    /// глеках»): горна в попередніх колонках уже лежать, і ця колонка справді вирішує.
    /// </summary>
    public static List<int> Tease(int[] g, int need)
    {
        var t = new List<int>();
        var n = 0;
        for (var c = 0; c < Cols; c++)
        {
            if (n == need - 1) t.Add(c);
            for (var r = 0; r < Rows; r++) if (g[c * Rows + r] == FurnaceCode) n++;
            if (n >= need) break;
        }
        return t;
    }

    public SlotOutcome Spin(int bet, ISlotRng rng, JsonObject state) => Play(bet, rng, null, true, null).Outcome!;

    /// <summary>Лише виграш, без сценарію (симуляції). Ті самі виклики rng, що й <see cref="Spin"/>.</summary>
    public long Fast(int bet, ISlotRng rng, Probe? probe = null) => Play(bet, rng, null, false, probe).Win;

    /// <summary>Оберт із заданим першим полем (тести; [c][r] ключами). Далі падіння й бонус — з rng.</summary>
    public SlotOutcome ScriptFor(string[][] first, int bet, ISlotRng rng) => Play(bet, rng, Parse(first), true, null).Outcome!;

    /// <summary>
    /// Один вільний оберт із заданим полем і слідами (тести правил слідів у бонусі). <paramref name="traces"/> міняється
    /// на місці, як у справжньому бонусі.
    /// </summary>
    public (long Win, JsonArray Steps) FreeSpinFor(string[][] first, int[] traces, int bet, ISlotRng rng)
    {
        var steps = new JsonArray();
        var S = new Run(bet, (long)Cap * bet, steps, null);
        Tumble(Parse(first), traces, Free, true, rng, S);
        return (S.Total, steps);
    }

    static int[] Parse(string[][] first)
    {
        var g = new int[Cells];
        for (var c = 0; c < Cols; c++)
            for (var r = 0; r < Rows; r++) g[c * Rows + r] = Code(first[c][r]);
        return g;
    }

    readonly record struct Result(long Win, SlotOutcome? Outcome);

    Result Play(int bet, ISlotRng rng, int[]? first, bool rec, Probe? probe)
    {
        var steps = rec ? new JsonArray() : null;
        var S = new Run(bet, (long)Cap * bet, steps, probe);
        var grid = first ?? RandGrid(Base, rng);
        var tease = Tease(grid, Scatters);
        steps?.Add(new JsonObject { ["t"] = "spin", ["grid"] = GridJson(grid), ["tease"] = Ints(tease) });
        if (probe is not null) probe.Spins++;

        var traces = new int[Cells];   // у базі полиця чиста на кожен оберт
        grid = Tumble(grid, traces, Base, false, rng, S);
        if (probe is not null)
        {
            if (S.Total > 0) probe.BaseHits++;
            probe.BaseWon += S.Total;
        }
        var meta = new JsonObject { ["casc"] = S.MaxCasc, ["bonus"] = false, ["tease"] = tease.Count > 0 };
        var bonus = false;
        var fsInfo = default((long Win, int Spins, int Top));

        if (!S.Capped && Count(grid, FurnaceCode) >= Scatters)
        {
            bonus = true;
            if (probe is not null)
            {
                probe.Bonuses++;
                if (probe.SkipBonus) return new Result(S.Total, null);
            }
            steps?.Add(new JsonObject { ["t"] = "furn", ["cells"] = CellsOf(grid, FurnaceCode) });
            fsInfo = FreeRound(rng, S);
        }
        else if (S.Capped) steps?.Add(CapBanner());

        if (probe is not null && S.Capped) probe.Capped++;
        if (!rec) return new Result(S.Total, null);
        meta["casc"] = S.MaxCasc;
        meta["top"] = S.TopMult;
        if (bonus)
        {
            meta["bonus"] = true;
            meta["fsWin"] = fsInfo.Win;
            meta["spins"] = fsInfo.Spins;
            meta["traceTop"] = fsInfo.Top;
        }
        return new Result(S.Total, Outcome(bet, S, meta, bonus, false));
    }

    SlotOutcome Outcome(int bet, Run S, JsonObject meta, bool bonus, bool bought)
    {
        var script = new JsonObject
        {
            ["bet"] = bet,
            ["steps"] = S.Steps,
            ["win"] = S.Total,
            ["state"] = new JsonObject(),
            ["bonus"] = bonus,
            ["meta"] = meta,
        };
        if (bought) script["bought"] = true;
        if (S.Capped) script["capped"] = true;
        List<string> flags = [];
        if (bonus) flags.Add("bonus");
        if (bought) flags.Add("bought");
        if (S.Capped) flags.Add("cap");
        return new SlotOutcome(script, (int)S.Total, flags, new JsonObject());
    }

    /// <summary>
    /// Вільні оберти: 10 обертів на вагах <see cref="Free"/>, сліди спільні на весь бонус (з чистої полиці), 3+ горна після
    /// каскаду — ще 10 (разом не більше 50). Дійшли до стелі — бонус закінчується. Повертає виграш вільних, скільки обертів
    /// зіграно й найбільший слід.
    /// </summary>
    static (long Win, int Spins, int Top) FreeRound(ISlotRng rng, Run S)
    {
        var steps = S.Steps;
        steps?.Add(new JsonObject { ["t"] = "bonusIn", ["count"] = FreeSpins });
        var start = S.Total;
        var traces = new int[Cells];
        int left = FreeSpins, total = FreeSpins, spins = 0;
        while (left > 0 && !S.Capped)
        {
            left--; spins++;
            steps?.Add(new JsonObject { ["t"] = "fs", ["left"] = left });
            var g = RandGrid(Free, rng);
            steps?.Add(new JsonObject { ["t"] = "spin", ["grid"] = GridJson(g), ["tease"] = Ints(total < MaxFreeSpins ? Tease(g, Scatters) : []) });
            g = Tumble(g, traces, Free, true, rng, S);
            if (S.Capped) break;
            if (total < MaxFreeSpins && Count(g, FurnaceCode) >= Scatters)
            {
                var add = Math.Min(Retrigger, MaxFreeSpins - total);
                steps?.Add(new JsonObject { ["t"] = "furn", ["cells"] = CellsOf(g, FurnaceCode), ["fs"] = true });
                steps?.Add(new JsonObject { ["t"] = "fs", ["add"] = add });
                left += add;
                total += add;
                if (S.Probe is not null) S.Probe.Retriggers++;
            }
        }
        if (S.Probe is not null)
        {
            S.Probe.FreeSpinsPlayed += spins;
            S.Probe.FreeWon += S.Total - start;
        }
        var top = 0;
        foreach (var t in traces) top = Math.Max(top, t);
        if (S.Capped) steps?.Add(CapBanner());
        steps?.Add(new JsonObject { ["t"] = "bonusOut", ["total"] = S.Total - start, ["spins"] = spins, ["top"] = top });
        return (S.Total - start, spins, top);
    }

    /// <summary>
    /// Куплений бонус: одразу 10 вільних обертів (ті самі правила й ваги, що й у виграному), без базового оберту. Ціну
    /// (у ставках) списує гра; тут лише сценарій. Стеля та сама — 5000× ставки.
    /// </summary>
    public SlotOutcome Buy(int bet, ISlotRng rng)
    {
        var steps = new JsonArray();
        var S = new Run(bet, (long)Cap * bet, steps, null);
        var fs = FreeRound(rng, S);
        var meta = new JsonObject { ["casc"] = S.MaxCasc, ["bonus"] = true, ["tease"] = false, ["top"] = S.TopMult, ["fsWin"] = fs.Win, ["spins"] = fs.Spins, ["traceTop"] = fs.Top };
        return Outcome(bet, S, meta, true, true);
    }

    /// <summary>Лише вільні оберти (без сценарію) — для розшарованої оцінки RTP і RTP купленого бонусу.</summary>
    public long FastBonus(int bet, ISlotRng rng, Probe? probe = null)
    {
        var S = new Run(bet, (long)Cap * bet, null, probe);
        FreeRound(rng, S);
        if (probe is not null && S.Capped) probe.Capped++;
        return S.Total;
    }

    /// <summary>
    /// Найменша ціна купленого бонусу в ставках: з нею RTP купівлі не вищий за RTP бази (середній бонус ≈ 87,7× ставки
    /// → 87,7 ÷ 92 = 95,3 %; симуляція — spec, §Математика). Конфіг, що просить дешевше, підтягується до неї.
    /// </summary>
    public const int MinBuyPrice = 92;

    public int BuyMinPrice => MinBuyPrice;

    static JsonObject CapBanner() => new() { ["t"] = "banner", ["text"] = $"Стеля {CapX}×!", ["sub"] = "більше один оберт не дає", ["ms"] = 2200 };

    // ---------------------------------------------------------------------------------------------- JSON

    static JsonArray CellsJson(int[] cells)
    {
        var a = new JsonArray();
        foreach (var p in cells) a.Add(new JsonArray(p / Rows, p % Rows));
        return a;
    }

    static JsonArray CellsOf(int[] g, int code)
    {
        var a = new JsonArray();
        for (var i = 0; i < Cells; i++) if (g[i] == code) a.Add(new JsonArray(i / Rows, i % Rows));
        return a;
    }

    static JsonArray GridJson(int[] g)
    {
        var cols = new JsonArray();
        for (var c = 0; c < Cols; c++)
        {
            var col = new JsonArray();
            for (var r = 0; r < Rows; r++) col.Add(Key(g[c * Rows + r]));
            cols.Add(col);
        }
        return cols;
    }

    static JsonArray Ints(List<int> a)
    {
        var j = new JsonArray();
        foreach (var x in a) j.Add(x);
        return j;
    }

    public JsonObject Table()
    {
        var pay = new JsonObject();
        for (var s = Paying.Length - 1; s >= 0; s--)
        {
            var o = new JsonObject();
            for (var t = Tiers - 1; t >= 0; t--) o[TierFrom[t].ToString()] = Pay10[s][t] / 10.0;
            pay[Paying[s]] = o;
        }
        var sizes = new JsonArray();
        foreach (var f in TierFrom) sizes.Add(f);
        return new JsonObject
        {
            ["cols"] = Cols, ["rows"] = Rows, ["min"] = MinCluster, ["sizes"] = sizes, ["pay"] = pay, ["payUnit"] = 1,
            ["scatters"] = Scatters, ["free"] = FreeSpins, ["retrigger"] = Retrigger, ["maxFree"] = MaxFreeSpins,
            ["maxTrace"] = MaxTrace, ["cap"] = Cap,
        };
    }
}
