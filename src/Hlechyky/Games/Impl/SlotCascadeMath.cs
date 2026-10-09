using System.Text.Json.Nodes;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Розбиті глеки» (slot-cascade): поле 6×5 без ліній — платить 8+ однакових будь-де (8–9 / 10–11 / 12+). Виграшний
/// посуд тріскається, решта падає, згори — нові (каскад) аж до спокою. Писанки-множники (×2…×100) не платять самі:
/// наприкінці каскаду з виграшем їхня сума множить увесь виграш каскаду. Горно 4+ будь-де (після каскаду) — виплата й
/// 10 вільних обертів; у вільних писанки накопичуються на весь бонус (кожен виграшний каскад із писанками множиться на
/// накопичене), 3+ горна — ще 5 обертів (до 50 разом). Увесь бонус рахується в тому ж оберті.
///
/// Порт моку <c>web/games/slots/slot-cascade.js</c> (makeScript / tumble / bonus) — сценарій тієї самої форми, поле за
/// полем (docs/games/specs/slots.md, розділ «Розбиті глеки»). Таблицю виплат і вагу горна підправлено під RTP бази ≈ 95 %
/// і бонус ≈ 1 з 176. Стеля — 5000× за оберт: досягли посеред бонусу — бонус закінчується, банер «Стеля».
/// </summary>
public sealed class SlotCascadeMath : ISlotMath
{
    public const int Cols = 6, Rows = 5, Cells = Cols * Rows;
    public const int MinCount = 8, FreeSpins = 10, Retrigger = 5, MaxFreeSpins = 50, MaxCascades = 30;
    public const int BonusFurnaces = 4, RetriggerFurnaces = 3;
    public const string Furnace = "furnace";

    /// <summary>Символи, що платять (дешеві черепки k1…k4, далі посуд до глека). Порядок — як PAYING у моку.</summary>
    public static readonly string[] Paying = ["k1", "k2", "k3", "k4", "bowl", "pot", "makitra", "kumanets", "glek"];

    /// <summary>Коди поля: 0…8 — <see cref="Paying"/>, 9 — горно, 10+i — писанка ×<see cref="Mults"/>[i].</summary>
    public const int FurnaceCode = 9, MultCode = 10;

    /// <summary>Множники писанок.</summary>
    public static readonly int[] Mults = [2, 3, 5, 10, 25, 50, 100];

    /// <summary>
    /// Виплати в ДЕСЯТИХ частках ставки за [8–9, 10–11, 12+] однакових будь-де. Ставки кратні 10 — виплати рівні.
    /// Від моку відрізняються лише k1 10–11 (0,9 → 0,8) і k2 8–9 (0,5 → 0,4): мок давав ≈ 96,8 %, з частішим горном —
    /// ≈ 99 %; так — RTP бази ≈ 95,0 % (розклад по групах і симуляції — specs/slots.md, «Розбиті глеки»).
    /// </summary>
    public static readonly int[][] Pay10 =
    [
        [3, 8, 25],      // k1
        [4, 10, 40],     // k2
        [6, 12, 50],     // k3
        [10, 15, 80],    // k4
        [12, 20, 100],   // bowl
        [15, 25, 120],   // pot
        [20, 50, 150],   // makitra
        [25, 100, 250],  // kumanets
        [100, 250, 500], // glek
    ];

    /// <summary>Горно в базі (після каскаду): 4 — 3×, 5 — 5×, 6+ — 100× ставки.</summary>
    public static int FurnacePay(int n) => n >= 6 ? 100 : n == 5 ? 5 : n == 4 ? 3 : 0;

    /// <summary>Ваги: 9 символів, що платять, горно, писанка; і ваги множника писанки (за <see cref="Mults"/>).</summary>
    public sealed class Weights(double[] sym, double[] mult)
    {
        public double[] Sym { get; } = sym;
        public double[] Mult { get; } = mult;
        public double SymTotal { get; } = sym.Sum();
        public double MultTotal { get; } = mult.Sum();
    }

    // Ваги — з моку (W_BASE / W_FS, M_BASE / M_FS), крім горна в базі: 2,3 → 2,4, щоб бонус випадав ≈ 1 з 176 (було ≈ 1 з 204).
    //                                             k1  k2  k3  k4 bowl pot mak kum glek furn  pys
    public static readonly Weights Base = new([20, 19, 18, 17, 12, 10, 8, 6, 4.5, 2.4, 0.45], [40, 26, 18, 10, 4, 1.5, 0.5]);
    public static readonly Weights Free = new([20, 19, 18, 17, 12, 10, 8, 6, 4.5, 1.9, 1.6], [34, 26, 20, 12, 5, 2, 1]);

    public int Cap => 5000;
    public bool Gamble => false;

    public static string Key(int code) => code < FurnaceCode ? Paying[code] : code == FurnaceCode ? Furnace : "x" + Mults[code - MultCode];

    public static int Code(string key)
    {
        if (key == Furnace) return FurnaceCode;
        if (key.Length > 1 && key[0] == 'x' && int.TryParse(key.AsSpan(1), out var v))
        {
            var i = Array.IndexOf(Mults, v);
            if (i >= 0) return MultCode + i;
        }
        var p = Array.IndexOf(Paying, key);
        return p >= 0 ? p : throw new ArgumentException($"нема символу {key}");
    }

    /// <summary>Як SK.rnd.weighted: x = rnd × сума, віднімати ваги по черзі.</summary>
    static int Pick(double[] w, double total, ISlotRng rng)
    {
        var x = rng.NextDouble() * total;
        for (var i = 0; i < w.Length; i++)
        {
            x -= w[i];
            if (x < 0) return i;
        }
        return 0;
    }

    /// <summary>Один символ: писанка одразу отримує число.</summary>
    public static int Draw(Weights w, ISlotRng rng)
    {
        var k = Pick(w.Sym, w.SymTotal, rng);
        return k <= FurnaceCode ? k : MultCode + Pick(w.Mult, w.MultTotal, rng);
    }

    /// <summary>Поле по колонках: g[c × Rows + r], r = 0 — верх.</summary>
    static int[] RandGrid(Weights w, ISlotRng rng)
    {
        var g = new int[Cells];
        for (var i = 0; i < Cells; i++) g[i] = Draw(w, rng);
        return g;
    }

    /// <summary>Виграш групи в черепках: як Math.max(1, Math.round(u × ставка)) у моку.</summary>
    public static long Amount(int sym, int n, int bet)
    {
        var tier = n >= 12 ? 2 : n >= 10 ? 1 : 0;
        return Math.Max(1, (long)Math.Round(Pay10[sym][tier] * (double)bet / 10, MidpointRounding.AwayFromZero));
    }

    static int Count(int[] g, int code)
    {
        var n = 0;
        foreach (var k in g) if (k == code) n++;
        return n;
    }

    /// <summary>Скільки горен на полі та їхні клітинки (по колонках).</summary>
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

    /// <summary>
    /// Очікування — лише чесно: колонки, що падають, коли до бонусу бракує рівно одного горна (teaseFor у моку).
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

    /// <summary>Лічильники для симуляцій (без сценарію): скільки разів платила кожна група з урахуванням множника.</summary>
    public sealed class Probe
    {
        /// <summary>[символ × 3 + ярус] — сума множників, з якими платила група, у базі й у вільних.</summary>
        public double[] BaseCoef { get; } = new double[27];
        public double[] FreeCoef { get; } = new double[27];
        /// <summary>Скільки разів горно платило 4 / 5 / 6+.</summary>
        public long[] Furn { get; } = new long[3];
        public long Spins, Bonuses, FreeSpinsPlayed, Capped;
        /// <summary>Не грати бонус (рахується лише вхід і виплата горна) — для розшарованої оцінки RTP.</summary>
        public bool SkipBonus { get; init; }
    }

    readonly struct Tumble(long win, int casc, int multTo, int[] grid)
    {
        public long Win { get; } = win;
        public int Casc { get; } = casc;
        /// <summary>0 — кроку «mult» не було.</summary>
        public int MultTo { get; } = multTo;
        public int[] Grid { get; } = grid;
    }

    /// <summary>Каскад до спокою + писанки наприкінці (tumble у моку). acc — накопичений множник бонусу.</summary>
    static Tumble Run(int[] grid, int bet, Weights w, bool fs, int acc, ISlotRng rng, JsonArray? steps, Probe? probe)
    {
        long seq = 0;
        var n = 0;
        Span<int> counts = stackalloc int[Paying.Length];
        Span<int> paid = stackalloc int[Paying.Length * 3];
        paid.Clear();
        for (var guard = 0; guard < MaxCascades; guard++)
        {
            counts.Clear();
            foreach (var k in grid) if (k < FurnaceCode) counts[k]++;
            long amount = 0;
            JsonArray? items = steps is null ? null : new JsonArray();
            JsonArray? remove = steps is null ? null : new JsonArray();
            var any = false;
            for (var s = 0; s < Paying.Length; s++)
            {
                if (counts[s] < MinCount) continue;
                any = true;
                var a = Amount(s, counts[s], bet);
                amount += a;
                paid[s * 3 + (counts[s] >= 12 ? 2 : counts[s] >= 10 ? 1 : 0)]++;
                if (items is not null)
                {
                    var cells = CellsOf(grid, s);
                    items.Add(new JsonObject { ["sym"] = Paying[s], ["n"] = counts[s], ["cells"] = cells, ["amount"] = a });
                    foreach (var cell in cells) remove!.Add(cell!.DeepClone());
                }
            }
            if (!any) break;
            seq += amount;
            steps?.Add(new JsonObject { ["t"] = "win", ["items"] = items, ["amount"] = amount, ["hold"] = 260 });
            // падіння: у кожній колонці лишаються невиграшні (згори вниз), нові — згори
            var next = new int[Cells];
            for (var c = 0; c < Cols; c++)
            {
                var keep = 0;
                for (var r = 0; r < Rows; r++)
                {
                    var k = grid[c * Rows + r];
                    if (k >= FurnaceCode || counts[k] < MinCount) keep++;
                }
                var fresh = Rows - keep;
                for (var r = 0; r < fresh; r++) next[c * Rows + r] = Draw(w, rng);
                var at = fresh;
                for (var r = 0; r < Rows; r++)
                {
                    var k = grid[c * Rows + r];
                    if (k >= FurnaceCode || counts[k] < MinCount) next[c * Rows + at++] = k;
                }
            }
            n++;
            steps?.Add(new JsonObject { ["t"] = "cascade", ["remove"] = remove, ["grid"] = GridJson(next), ["n"] = n });
            grid = next;
        }

        var multTo = 0;
        long win = seq;
        if (seq > 0)
        {
            var sum = 0;
            foreach (var k in grid) if (k >= MultCode) sum += Mults[k - MultCode];
            if (sum > 0)
            {
                var from = fs ? acc : 0;
                multTo = from + sum;
                win = seq * multTo;
                if (steps is not null)
                {
                    var pys = new JsonArray();
                    for (var i = 0; i < Cells; i++)
                        if (grid[i] >= MultCode) pys.Add(new JsonArray(i / Rows, i % Rows, Mults[grid[i] - MultCode]));
                    steps.Add(new JsonObject
                    {
                        ["t"] = "mult", ["cells"] = pys, ["from"] = from, ["to"] = multTo, ["seq"] = seq, ["add"] = win - seq, ["fs"] = fs,
                    });
                }
            }
        }
        if (probe is not null)
        {
            var coef = fs ? probe.FreeCoef : probe.BaseCoef;
            var f = multTo > 0 ? multTo : 1;
            for (var i = 0; i < paid.Length; i++) if (paid[i] > 0) coef[i] += paid[i] * f;
        }
        return new Tumble(win, n, multTo, grid);
    }

    public SlotOutcome Spin(int bet, ISlotRng rng, JsonObject state) => Play(bet, rng, null, true, null).Outcome!;

    /// <summary>Лише виграш, без сценарію (симуляції). Ті самі виклики rng, що й <see cref="Spin"/>.</summary>
    public long Fast(int bet, ISlotRng rng, Probe? probe = null) => Play(bet, rng, null, false, probe).Win;

    /// <summary>Оберт із заданим першим полем (тести; [c][r] ключами моку). Далі падіння й бонус — з rng.</summary>
    public SlotOutcome ScriptFor(string[][] first, int bet, ISlotRng rng)
    {
        var g = new int[Cells];
        for (var c = 0; c < Cols; c++)
            for (var r = 0; r < Rows; r++) g[c * Rows + r] = Code(first[c][r]);
        return Play(bet, rng, g, true, null).Outcome!;
    }

    readonly record struct Result(long Win, SlotOutcome? Outcome);

    Result Play(int bet, ISlotRng rng, int[]? first, bool rec, Probe? probe)
    {
        var capAmt = (long)Cap * bet;
        var grid = first ?? RandGrid(Base, rng);
        var tease = Tease(grid, BonusFurnaces);
        var steps = rec ? new JsonArray { new JsonObject { ["t"] = "spin", ["grid"] = GridJson(grid), ["tease"] = Ints(tease) } } : null;
        if (probe is not null) probe.Spins++;

        var t = Run(grid, bet, Base, false, 0, rng, steps, probe);
        var win = t.Win;
        var mults = new JsonArray();
        if (t.MultTo > 0) mults.Add(t.MultTo);
        var meta = new JsonObject { ["casc"] = t.Casc, ["mults"] = mults, ["bonus"] = false, ["tease"] = tease.Count > 0, ["fsMults"] = 0, ["acc"] = 0 };
        var capped = false;
        var bonus = false;

        var furn = Count(t.Grid, FurnaceCode);
        if (furn >= BonusFurnaces)
        {
            bonus = true;
            var pay = (long)FurnacePay(furn) * bet;
            steps?.Add(new JsonObject { ["t"] = "furn", ["cells"] = CellsOf(t.Grid, FurnaceCode), ["amount"] = pay });
            win += pay;
            if (probe is not null)
            {
                probe.Bonuses++;
                probe.Furn[Math.Min(furn, 6) - 4]++;
                // розшарована оцінка RTP (тести): база окремо, бонуси — окремим прогоном FastBonus
                if (probe.SkipBonus) return new Result(Math.Min(win, capAmt), null);
            }

            var (fsWin, spins, acc, nm) = FreeRound(bet, rng, steps, probe, capAmt - win);
            if (win + fsWin >= capAmt) capped = true;
            var fsShown = capped ? Math.Max(0, capAmt - win) : fsWin;
            if (capped) steps?.Add(CapBanner());
            steps?.Add(new JsonObject { ["t"] = "bonusOut", ["total"] = fsShown, ["spins"] = spins, ["acc"] = acc });
            win += fsWin;
            meta["bonus"] = true;
            meta["fsMults"] = nm;
            meta["acc"] = acc;
            meta["fsWin"] = fsShown;
            meta["spins"] = spins;
        }
        else if (win >= capAmt)
        {
            capped = true;
            steps?.Add(CapBanner());
        }
        if (win > capAmt) win = capAmt;
        if (capped && probe is not null) probe.Capped++;
        if (!rec) return new Result(win, null);

        var script = new JsonObject
        {
            ["bet"] = bet,
            ["steps"] = steps,
            ["win"] = win,
            ["state"] = new JsonObject(),
            ["meta"] = meta,
        };
        if (capped) script["capped"] = true;
        List<string> flags = [];
        if (bonus) flags.Add("bonus");
        if (capped) flags.Add("cap");
        return new Result(win, new SlotOutcome(script, (int)win, flags, new JsonObject()));
    }

    /// <summary>
    /// Вільні оберти (bonus у моку): 10 обертів на вагах <see cref="Free"/>, писанки накопичуються на весь бонус, 3+ горна
    /// після каскаду — ще 5 (поки всього менше 50). Досягли <paramref name="room"/> (залишок до стелі) — бонус закінчується.
    /// </summary>
    static (long Win, int Spins, int Acc, int Mults) FreeRound(int bet, ISlotRng rng, JsonArray? steps, Probe? probe, long room)
    {
        steps?.Add(new JsonObject { ["t"] = "bonusIn", ["count"] = FreeSpins });
        int left = FreeSpins, total = FreeSpins, acc = 0, nm = 0, spins = 0;
        long fsWin = 0;
        while (left > 0 && spins < 60 && fsWin < room)
        {
            left--; spins++;
            steps?.Add(new JsonObject { ["t"] = "fs", ["left"] = left });
            var g = RandGrid(Free, rng);
            steps?.Add(new JsonObject { ["t"] = "spin", ["grid"] = GridJson(g), ["tease"] = Ints(total < MaxFreeSpins ? Tease(g, RetriggerFurnaces) : []) });
            var ft = Run(g, bet, Free, true, acc, rng, steps, probe);
            fsWin += ft.Win;
            if (ft.MultTo > 0) { nm++; acc = ft.MultTo; }
            var fc = Count(ft.Grid, FurnaceCode);
            if (fc >= RetriggerFurnaces && total < MaxFreeSpins)
            {
                steps?.Add(new JsonObject { ["t"] = "furn", ["cells"] = CellsOf(ft.Grid, FurnaceCode), ["amount"] = 0, ["fs"] = true });
                steps?.Add(new JsonObject { ["t"] = "fs", ["add"] = Retrigger });
                left += Retrigger;
                total += Retrigger;
            }
        }
        if (probe is not null) probe.FreeSpinsPlayed += spins;
        return (fsWin, spins, acc, nm);
    }

    /// <summary>Лише вільні оберти (без стелі й без сценарію) — для розшарованої оцінки RTP у тестах.</summary>
    public long FastBonus(int bet, ISlotRng rng, Probe? probe = null) => FreeRound(bet, rng, null, probe, long.MaxValue).Win;

    JsonObject CapBanner() => new() { ["t"] = "banner", ["text"] = $"Стеля {Cap}×!", ["sub"] = "більше один оберт не дає", ["ms"] = 2200 };

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
            pay[Paying[s]] = new JsonObject { ["12"] = Pay10[s][2] / 10.0, ["10"] = Pay10[s][1] / 10.0, ["8"] = Pay10[s][0] / 10.0 };
        pay[Furnace] = new JsonObject { ["6"] = FurnacePay(6), ["5"] = FurnacePay(5), ["4"] = FurnacePay(4) };
        var mults = new JsonArray();
        foreach (var m in Mults) mults.Add(m);
        return new JsonObject
        {
            ["cols"] = Cols, ["rows"] = Rows, ["min"] = MinCount, ["pay"] = pay, ["payUnit"] = 1, ["mults"] = mults,
            ["free"] = FreeSpins, ["retrigger"] = Retrigger, ["maxFree"] = MaxFreeSpins, ["cap"] = Cap,
        };
    }
}
