using System.Text.Json.Nodes;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Козацький скарб» (slot-hold): 5 барабанів × 3 рядки, 20 ліній, Бунчук — дикий (нема на першому барабані).
/// Дукати на барабанах (номінал у ставках: ×1…×25, Міні ×20, Мажор ×100) ламають лінію; 6+ дукатів на полі —
/// «Утримуй і вигравай»: дукати стають у гнізда, 3 респіни-свічки, кожен новий дукат скидає свічки до 3, Булава
/// збирає суму всіх дукатів собі, Пірнач подвоює дукати й Булави, повне поле (15 гнізд) — Гетьманський скарб.
/// Сюрприз «Дукатний дощ»: після зупинки з шансом <see cref="RainChance"/> на поле падає 1–4 дукати — лише на клітинки,
/// що не дукати й не входять у виграшні лінії (тож виграш ліній не міняється), і може довести до 6+.
/// Увесь бонус рахується наперед у тому ж оберті. Математика 98 % (10.10.2026) — docs/games/specs/slots.md, розділ
/// «Козацький скарб»; до неї — порт моку <c>web/games/slots/slot-hold.js</c> (scriptFor/holdSim) поле за полем.
/// </summary>
public sealed class SlotHoldMath : ISlotMath
{
    public const string Wild = "wild";
    public const int Cols = 5, Rows = 3, Lines = 20;
    /// <summary>Скільки дукатів на полі відкривають «Утримуй і вигравай».</summary>
    public const int HoldAt = 6;
    /// <summary>Свічок (респінів) на старті й після кожного нового дуката.</summary>
    public const int Candles = 3;

    // Стрічки по 30 (98 %, 10.10.2026): дрібних символів більше, на третьому барабані два Бунчуки — ряди частіше;
    // дукати дрібніші (×1…×5 і Міні; ×10, ×25 і Мажор — лише в респінах), дві пари дукатів поруч (2-й і 3-й барабани).
    // Клієнт крутить ті самі стрічки (зупинки — з сервера): web/games/slots/slot-hold.js REELS мусить збігатися.
    public static readonly string[][] Reels =
    [
        "s1 s2 c1 s3 pipe s4 s1 mug s2 s3 horse s4 s1 c2 sabre s2 s3 s4 cossack s1 pipe c5 s2 s1 mug s4 sabre s2 s3 s1".Split(' '),
        "s2 s3 c2 c1 wild s4 mug s2 s1 s4 s3 pipe s1 horse s4 s2 sabre s1 s3 cossack s4 c5 s2 pipe s1 mug s3 c5 s4 s2".Split(' '),
        "s3 c1 s4 s1 horse s2 cmini c2 pipe s4 wild s1 s3 s2 mug s1 s3 sabre s4 c5 s1 cossack s2 s3 pipe wild s1 s3 s4 s2".Split(' '),
        "s4 s1 c3 s2 sabre s3 c1 c2 s4 pipe s1 wild s2 horse s3 mug s4 s1 c3 s2 cossack s3 s1 s4 pipe s2 s1 c5 mug s3".Split(' '),
        "s1 c2 s3 s2 mug s4 c1 s1 horse s3 wild s2 s4 pipe s1 sabre s2 s3 s2 cossack s4 c3 s1 mug s3 s1 s2 pipe s4 cmini".Split(' '),
    ];

    /// <summary>20 ліній: рядок кожного барабана (0 — верх). Як LINES у моку.</summary>
    public static readonly int[][] PayLines =
    [
        [1, 1, 1, 1, 1], [0, 0, 0, 0, 0], [2, 2, 2, 2, 2], [0, 1, 2, 1, 0], [2, 1, 0, 1, 2],
        [0, 0, 1, 2, 2], [2, 2, 1, 0, 0], [1, 0, 0, 0, 1], [1, 2, 2, 2, 1], [1, 0, 1, 2, 1],
        [1, 2, 1, 0, 1], [0, 1, 1, 1, 0], [2, 1, 1, 1, 2], [0, 1, 0, 1, 0], [2, 1, 2, 1, 2],
        [1, 1, 0, 1, 1], [1, 1, 2, 1, 1], [0, 0, 2, 0, 0], [2, 2, 0, 2, 2], [0, 2, 0, 2, 0],
    ];

    /// <summary>
    /// Виплати в ставках на лінію (ставка ÷ 20): символ → (скільки в ряд → множник). Усі досяжні — парні: на ставці 10
    /// (лінія — пів черепка) і будь-якій кратній 10 виплата ціла, без округлення. Ряд Бунчуків недосяжний (на першому
    /// барабані Бунчука нема) — рядок лишився з моку.
    /// </summary>
    public static readonly Dictionary<string, Dictionary<int, int>> Pay = new(StringComparer.Ordinal)
    {
        ["wild"] = new() { [5] = 3000, [4] = 400, [3] = 75 },
        ["cossack"] = new() { [5] = 1000, [4] = 200, [3] = 50 },
        ["horse"] = new() { [5] = 500, [4] = 120, [3] = 40 },
        ["sabre"] = new() { [5] = 400, [4] = 100, [3] = 30 },
        ["mug"] = new() { [5] = 300, [4] = 80, [3] = 24 },
        ["pipe"] = new() { [5] = 240, [4] = 60, [3] = 22 },
        ["s1"] = new() { [5] = 90, [4] = 24, [3] = 10 },
        ["s2"] = new() { [5] = 90, [4] = 24, [3] = 10 },
        ["s3"] = new() { [5] = 80, [4] = 20, [3] = 8 },
        ["s4"] = new() { [5] = 80, [4] = 20, [3] = 8 },
    };

    /// <summary>Дукати на барабанах: ключ → номінал у ставках (Міні/Мажор — джекпоти). Як COINV у моку.</summary>
    public static readonly Dictionary<string, int> CoinValue = new(StringComparer.Ordinal)
    {
        ["c1"] = 1, ["c2"] = 2, ["c3"] = 3, ["c5"] = 5, ["c10"] = 10, ["c25"] = 25, ["cmini"] = Mini, ["cmajor"] = Major,
    };

    /// <summary>Джекпоти в ставках: Міні, Мажор; Гетьманський скарб = стеля оберту (<see cref="Cap"/>).</summary>
    public const int Mini = 20, Major = 100, Grand = 1000;

    // ---- респіни: шанс дуката в порожньому гнізді і що саме падає (bonusCoin у моку) ----
    /// <summary>Шанс, що в порожньому гнізді на респіні впаде щось (pLand моку).</summary>
    public const double PLand = 0.05;
    /// <summary>Межі для першого кидка: Булава, Пірнач, Міні, Мажор, далі — звичайний дукат.</summary>
    public const double PMace = 0.045, PPirnach = 0.085, PMini = 0.12, PMajor = 0.128;
    /// <summary>Номінали звичайного дуката на респіні і межі другого кидка (0,34 → ×1, 0,6 → ×2 …).</summary>
    public static readonly (double Below, int Value)[] RespinCoins = [(0.34, 1), (0.6, 2), (0.76, 3), (0.89, 5), (0.97, 10), (1.0, 25)];

    // ---- «Дукатний дощ» ----
    /// <summary>Шанс дощу в оберті (1 з 16). Не залежить ні від чого: один кидок на кожен оберт.</summary>
    public const double RainChance = 1.0 / 16;
    /// <summary>Скільки дукатів падає: межі кидка (0,70 → 1, 0,92 → 2, 0,98 → 3, далі 4).</summary>
    public static readonly (double Below, int Count)[] RainCounts = [(0.70, 1), (0.92, 2), (0.98, 3), (1.0, 4)];
    /// <summary>
    /// Номінали дощу — як у випадкового дуката зі стрічок: ключ → скільки таких дукатів на всіх п'яти барабанах
    /// (порядок — як у <see cref="CoinValue"/>; ключів, яких на стрічках нема, тут нема).
    /// </summary>
    public static readonly (string K, int W)[] RainCoins =
        [.. CoinValue.Keys.Select(k => (k, Reels.Sum(r => r.Count(x => x == k)))).Where(x => x.Item2 > 0)];

    public int Cap => Grand;
    public bool Gamble => false;

    public static bool IsCoin(string k) => CoinValue.ContainsKey(k);

    /// <summary>Вид дуката для сценарію: Міні/Мажор — свої, решта — 'coin'. Як COINK у моку.</summary>
    public static string CoinKind(string k) => k switch { "cmini" => "mini", "cmajor" => "major", _ => "coin" };

    /// <summary>Символ клітинки: барабан c, рядок r (0 — верх) при зупинці stops[c].</summary>
    public static string At(int[] stops, int c, int r) => Reels[c][(stops[c] + r) % Reels[c].Length];

    /// <summary>
    /// Виграш на лінії (evalLine моку): базовий символ — перший не дикий; дукат ламає лінію (тоді лишається хіба ряд
    /// Бунчуків); з двох прочитань — дорожче.
    /// </summary>
    public static (string Sym, int N, int Pay)? Line(ReadOnlySpan<string> keys)
    {
        var b = Wild;
        foreach (var k in keys) if (k != Wild) { b = k; break; }
        if (!Pay.ContainsKey(b)) b = Wild;
        int n = 0, w = 0;
        foreach (var k in keys) { if (k == b || k == Wild) n++; else break; }
        foreach (var k in keys) { if (k == Wild) w++; else break; }
        var p1 = Pay[b].GetValueOrDefault(n);
        var p2 = Pay[Wild].GetValueOrDefault(w);
        if (p2 > p1) return (Wild, w, p2);
        return p1 > 0 ? (b, n, p1) : null;
    }

    /// <summary>Поле [барабан][рядок] (0 — верх) при зупинках stops.</summary>
    public static string[][] Grid(int[] stops)
    {
        var g = new string[Cols][];
        for (var c = 0; c < Cols; c++)
        {
            g[c] = new string[Rows];
            for (var r = 0; r < Rows; r++) g[c][r] = At(stops, c, r);
        }
        return g;
    }

    /// <summary>Виграш однієї лінії: номер лінії, символ, скільки в ряд (клітинки — перші N барабанів), множник.</summary>
    public readonly record struct LineWin(int Line, string Sym, int N, int Pay);

    /// <summary>Усі лінії, що платять на полі (у порядку ліній).</summary>
    public static List<LineWin> Wins(string[][] g)
    {
        var list = new List<LineWin>();
        var keys = new string[Cols];
        for (var i = 0; i < PayLines.Length; i++)
        {
            var ln = PayLines[i];
            for (var c = 0; c < Cols; c++) keys[c] = g[c][ln[c]];
            if (Line(keys) is { } w) list.Add(new LineWin(i, w.Sym, w.N, w.Pay));
        }
        return list;
    }

    /// <summary>Сума виплат по всіх лініях у ставках на лінію.</summary>
    public static int Units(string[][] g)
    {
        var u = 0;
        foreach (var w in Wins(g)) u += w.Pay;
        return u;
    }

    /// <summary>Сума виплат по всіх лініях у ставках на лінію (поле барабанів, без дощу).</summary>
    public static int Units(int[] stops) => Units(Grid(stops));

    /// <summary>Скільки дукатів на полі.</summary>
    public static int Coins(string[][] g)
    {
        var n = 0;
        foreach (var col in g)
            foreach (var k in col) if (IsCoin(k)) n++;
        return n;
    }

    /// <summary>Скільки дукатів на полі барабанів (без дощу).</summary>
    public static int Coins(int[] stops) => Coins(Grid(stops));

    /// <summary>
    /// Куди може впасти дощ: клітинки без дуката, що не входять у жоден виграшний ряд (стовпчик за стовпчиком, згори вниз).
    /// Дукат ряду не складає (він лише ламає лінію), а поза виграшними рядами ламати нічого: виграш ліній після дощу
    /// рівно той самий.
    /// </summary>
    public static List<(int C, int R)> RainCells(string[][] g, IReadOnlyList<LineWin> wins)
    {
        var busy = new bool[Cols, Rows];
        foreach (var w in wins)
            for (var c = 0; c < w.N; c++) busy[c, PayLines[w.Line][c]] = true;
        var free = new List<(int, int)>();
        for (var c = 0; c < Cols; c++)
            for (var r = 0; r < Rows; r++)
                if (!busy[c, r] && !IsCoin(g[c][r])) free.Add((c, r));
        return free;
    }

    /// <summary>
    /// «Дукатний дощ»: кидок «чи йде» — на кожен оберт (шанс <see cref="RainChance"/>, без жодної пам'яті), далі — скільки
    /// (<see cref="RainCounts"/>), потім для кожного дуката — вільна клітинка (<see cref="RainCells"/>, рівноймовірно) і
    /// номінал (вага — скільки таких дукатів на стрічках, <see cref="RainCoins"/>). Вільних менше — падає, скільки влізло.
    /// Повертає в порядку падіння.
    /// </summary>
    public static List<(int C, int R, string K)> Rain(string[][] g, IReadOnlyList<LineWin> wins, ISlotRng rng)
    {
        var drops = new List<(int, int, string)>();
        if (rng.NextDouble() >= RainChance) return drops;
        var x = rng.NextDouble();
        var count = RainCounts[^1].Count;
        foreach (var (below, n) in RainCounts) if (x < below) { count = n; break; }
        var free = RainCells(g, wins);
        var total = 0;
        foreach (var (_, w) in RainCoins) total += w;
        for (var i = 0; i < count && free.Count > 0; i++)
        {
            var j = rng.Next(free.Count);
            var (c, r) = free[j];
            free.RemoveAt(j);
            var y = rng.Next(total);
            var k = RainCoins[^1].K;
            foreach (var (key, w) in RainCoins)
            {
                if (y < w) { k = key; break; }
                y -= w;
            }
            drops.Add((c, r, k));
        }
        return drops;
    }

    public SlotOutcome Spin(int bet, ISlotRng rng, JsonObject state)
    {
        var stops = new int[Cols];
        for (var c = 0; c < Cols; c++) stops[c] = rng.Next(Reels[c].Length);
        return ScriptFor(stops, bet, state, rng);
    }

    /// <summary>
    /// Сценарій для заданих зупинок (дощ і бонус — з rng; порядок кидків: дощ, потім респіни). Кроки: spin → morph (дощ,
    /// why: 'rain') → win → holdIn… Лінії рахуються на полі після дощу — і дорівнюють лініям барабанів, бо дощ не чіпає
    /// виграшних рядів (<see cref="RainCells"/>).
    /// </summary>
    public SlotOutcome ScriptFor(int[] stops, int bet, JsonObject state, ISlotRng rng)
    {
        var g = Grid(stops);

        // Чесне очікування — за полем барабанів (дощ іде вже після зупинки): барабан c тягнеться, лише коли на попередніх
        // уже рівно 5 дукатів (бракує одного до бонусу).
        var tease = new JsonArray();
        var before = 0;
        for (var c = 0; c < Cols; c++)
        {
            if (before == HoldAt - 1) tease.Add(c);
            for (var r = 0; r < Rows; r++) if (IsCoin(g[c][r])) before++;
        }

        var rain = Rain(g, Wins(g), rng);
        foreach (var (c, r, k) in rain) g[c][r] = k;

        var items = new JsonArray();
        long lineWin = 0;
        foreach (var w in Wins(g))
        {
            var ln = PayLines[w.Line];
            // як Math.round(u × ставка ÷ 20) у моку; ставки кратні 10, досяжні множники парні — рівно
            var amount = (long)Math.Round(w.Pay * (double)bet / Lines, MidpointRounding.AwayFromZero);
            var cells = new JsonArray();
            for (var c = 0; c < w.N; c++) cells.Add(new JsonArray(c, ln[c]));
            items.Add(new JsonObject { ["line"] = w.Line, ["cells"] = cells, ["sym"] = w.Sym, ["amount"] = amount });
            lineWin += amount;
        }

        // Дукати поля після дощу (стовпчик за стовпчиком, згори вниз).
        var coins = new List<(int C, int R, string K, int V)>();
        for (var c = 0; c < Cols; c++)
            for (var r = 0; r < Rows; r++)
                if (IsCoin(g[c][r])) coins.Add((c, r, CoinKind(g[c][r]), CoinValue[g[c][r]]));

        var capWin = (long)Cap * bet;
        var steps = new JsonArray { new JsonObject { ["t"] = "spin", ["stops"] = new JsonArray([.. stops.Select(s => (JsonNode)s)]), ["tease"] = tease } };
        if (rain.Count > 0)
        {
            // morph кіт уже вміє на барабанах: [c, r, ключ] — ключ стрічки (c1…c5, cmini), тож старий клієнт малює
            // справжній дукат з номіналом; четверте — номінал у ставках (для анімації дощу).
            var cells = new JsonArray();
            foreach (var (c, r, k) in rain) cells.Add(new JsonArray(c, r, k, CoinValue[k]));
            steps.Add(new JsonObject { ["t"] = "morph", ["cells"] = cells, ["why"] = "rain" });
        }
        if (items.Count > 0) steps.Add(new JsonObject { ["t"] = "win", ["items"] = items, ["amount"] = lineWin });
        var win = Math.Min(lineWin, capWin);
        HoldRun? hold = null;
        var capped = lineWin > capWin;
        if (coins.Count >= HoldAt)
        {
            hold = Hold(coins, bet, rng, Math.Max(0, capWin - lineWin));
            foreach (var s in hold.Steps) steps.Add(s);
            win = lineWin + hold.Pay;
            capped |= hold.Capped;
        }

        var script = new JsonObject
        {
            ["bet"] = bet,
            ["steps"] = steps,
            ["win"] = win,
            ["state"] = new JsonObject(),
            ["hold"] = hold is not null,
            ["full"] = hold?.Full == true,
        };
        if (rain.Count > 0) script["rain"] = rain.Count;
        if (capped) script["capped"] = true;
        List<string> flags = [];
        if (rain.Count > 0) flags.Add("rain");
        if (hold is not null) flags.Add("hold");
        if (hold?.Full == true) flags.Add("grand");
        return new SlotOutcome(script, (int)win, flags, new JsonObject());
    }

    /// <summary>Підсумок «Утримуй і вигравай»: кроки, сума дукатів (holdCount), Гетьманський, що віддала скриня.</summary>
    public sealed record HoldRun(List<JsonObject> Steps, long Total, long GrandAmount, long Pay, bool Full, int Respins, bool Capped);

    sealed class Slot(string k, long v) { public string K = k; public long V = v; }

    /// <summary>
    /// «Утримуй і вигравай» (holdSim моку без демо-підкруток plan/saves/fill/rich): 3 свічки; кожен респін у кожному
    /// порожньому гнізді з шансом <see cref="PLand"/> щось падає; впало — свічки знову 3, ні — мінус одна. Порядок у
    /// респіні: поставити все, що впало → кожен Пірнач подвоює дукати й Булави (джекпоти — ні) → кожна Булава збирає
    /// собі суму всіх інших дукатів і Булав (вони лишаються). Повне поле — Гетьманський скарб: скриня віддає стелю
    /// оберту. <paramref name="room"/> — скільки черепків ще можна віддати до стелі (стеля мінус виграш ліній).
    /// </summary>
    public static HoldRun Hold(IReadOnlyList<(int C, int R, string K, int V)> coins, int bet, ISlotRng rng, long room)
    {
        var g = new Slot?[Cols, Rows];
        var holdIn = new JsonArray();
        foreach (var x in coins)
        {
            g[x.C, x.R] = new Slot(x.K, x.V);
            holdIn.Add(new JsonArray(x.C, x.R, x.K, x.V));
        }
        var steps = new List<JsonObject> { new() { ["t"] = "holdIn", ["coins"] = holdIn } };

        List<(int C, int R)> Empties()
        {
            var a = new List<(int, int)>();
            for (var c = 0; c < Cols; c++)
                for (var r = 0; r < Rows; r++) if (g[c, r] is null) a.Add((c, r));
            return a;
        }

        int left = Candles, n = 0, guard = 0;
        while (left > 0 && guard++ < 80)
        {
            var em = Empties();
            if (em.Count == 0) break;
            var land = new List<(int C, int R, string K, long V)>();
            foreach (var (c, r) in em)
                if (rng.NextDouble() < PLand)
                {
                    var (k, v) = RespinCoin(rng);
                    land.Add((c, r, k, v));
                }
            var was = left;
            foreach (var x in land) g[x.C, x.R] = new Slot(x.K, x.V);
            left = land.Count > 0 ? Candles : left - 1;
            var landJson = new JsonArray();
            foreach (var x in land) landJson.Add(new JsonArray(x.C, x.R, x.K, x.V));
            steps.Add(new JsonObject
            {
                ["t"] = "respin", ["left"] = was, ["after"] = left, ["land"] = landJson, ["slow"] = was == 1 || em.Count <= 2,
            });
            foreach (var p in land.Where(x => x.K == "pirnach"))
            {
                var cells = new JsonArray();
                for (var c = 0; c < Cols; c++)
                    for (var r = 0; r < Rows; r++)
                        if (g[c, r] is { } it && it.K is "coin" or "mace" && it.V > 0)
                        {
                            it.V *= 2;
                            cells.Add(new JsonArray(c, r, it.V));
                        }
                steps.Add(new JsonObject { ["t"] = "double", ["at"] = new JsonArray(p.C, p.R), ["cells"] = cells });
            }
            foreach (var m in land.Where(x => x.K == "mace"))
            {
                var from = new JsonArray();
                long sum = 0;
                for (var c = 0; c < Cols; c++)
                    for (var r = 0; r < Rows; r++)
                    {
                        if (g[c, r] is not { } it || (c == m.C && r == m.R) || it.K is not ("coin" or "mace") || it.V <= 0) continue;
                        from.Add(new JsonArray(c, r, it.V));
                        sum += it.V;
                    }
                g[m.C, m.R]!.V = Math.Max(1, sum);
                steps.Add(new JsonObject { ["t"] = "collect", ["at"] = new JsonArray(m.C, m.R), ["from"] = from, ["total"] = g[m.C, m.R]!.V });
            }
            n++;
            if (em.Count == land.Count) break;
        }

        var full = Empties().Count == 0;
        var count = new JsonArray();
        long total = 0;
        for (var c = 0; c < Cols; c++)
            for (var r = 0; r < Rows; r++)
            {
                if (g[c, r] is not { } it || it.V <= 0) continue;
                var amt = it.V * bet;
                count.Add(new JsonArray(c, r, amt, it.K));
                total += amt;
            }
        steps.Add(new JsonObject { ["t"] = "holdCount", ["cells"] = count, ["total"] = total });
        // Гетьманський скарб — стеля оберту: скриня доплачує до 1000× (разом із лініями). Мок клав 1000× зверху.
        long grand = full ? Math.Max(0, room - total) : 0;
        if (full) steps.Add(new JsonObject { ["t"] = "grand", ["amount"] = grand });
        var pay = Math.Min(total + grand, room);
        steps.Add(new JsonObject { ["t"] = "holdOut", ["total"] = pay });
        return new HoldRun(steps, total, grand, pay, full, n, total > room);
    }

    /// <summary>Що впало в гніздо на респіні (bonusCoin моку): Булава, Пірнач, Міні, Мажор або дукат ×1…×25.</summary>
    public static (string K, long V) RespinCoin(ISlotRng rng)
    {
        var x = rng.NextDouble();
        if (x < PMace) return ("mace", 0);
        if (x < PPirnach) return ("pirnach", 0);
        if (x < PMini) return ("mini", Mini);
        if (x < PMajor) return ("major", Major);
        var y = rng.NextDouble();
        foreach (var (below, v) in RespinCoins) if (y < below) return ("coin", v);
        return ("coin", RespinCoins[^1].Value);
    }

    /// <summary>Правило дощу для ⓘ.</summary>
    public const string RainText = "Дукатний дощ: після зупинки в 1 з 16 обертів на поле падає 1–4 дукати (номінали — як на " +
        "барабанах) — лише на клітинки поза виграшними лініями, тож виграш ліній не меншає. Шість дукатів разом із дощем — " +
        "«Утримуй і вигравай».";

    public JsonObject Table()
    {
        var pay = new JsonObject();
        foreach (var (sym, p) in Pay)
        {
            var o = new JsonObject();
            foreach (var (n, k) in p.OrderByDescending(x => x.Key)) o[n.ToString()] = k;
            pay[sym] = o;
        }
        var reels = new JsonArray();
        foreach (var r in Reels) reels.Add(new JsonArray([.. r.Select(s => (JsonNode)JsonValue.Create(s)!)]));
        var lines = new JsonArray();
        foreach (var l in PayLines) lines.Add(new JsonArray([.. l.Select(x => (JsonNode)x)]));
        var coins = new JsonObject();
        foreach (var (k, v) in CoinValue) coins[k] = v;
        // номінал дуката на респіні: частка серед усього, що впало (звичайний дукат — решта після Булави/Пірнача/джекпотів)
        var respinCoins = new JsonObject();
        var lo = 0.0;
        foreach (var (below, v) in RespinCoins) { respinCoins[v.ToString()] = Math.Round((1 - PMajor) * (below - lo), 6); lo = below; }
        var rainCounts = new JsonObject();
        var prev = 0.0;
        foreach (var (below, n) in RainCounts) { rainCounts[n.ToString()] = Math.Round(below - prev, 6); prev = below; }
        var rainCoins = new JsonObject();
        foreach (var (k, w) in RainCoins) rainCoins[k] = w;
        return new JsonObject
        {
            ["reels"] = reels, ["lines"] = lines, ["pay"] = pay, ["payUnit"] = 1.0 / Lines, ["wild"] = Wild, ["cap"] = Cap,
            ["coins"] = coins, ["jackpots"] = new JsonObject { ["mini"] = Mini, ["major"] = Major, ["grand"] = Grand },
            ["holdAt"] = HoldAt, ["candles"] = Candles,
            // ⓘ: шанси респіну (у порожньому гнізді щось падає; що саме — частки від того, що впало)
            ["respin"] = new JsonObject
            {
                ["land"] = PLand, ["mace"] = PMace, ["pirnach"] = Math.Round(PPirnach - PMace, 6),
                ["mini"] = Math.Round(PMini - PPirnach, 6), ["major"] = Math.Round(PMajor - PMini, 6),
                ["coins"] = respinCoins,
            },
            // ⓘ: «Дукатний дощ» — шанс на оберт, скільки дукатів (частки), номінали (вага — дукатів цього виду на стрічках)
            ["rain"] = new JsonObject { ["chance"] = RainChance, ["counts"] = rainCounts, ["coins"] = rainCoins, ["text"] = RainText },
        };
    }
}
