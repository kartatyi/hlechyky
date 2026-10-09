using System.Text.Json.Nodes;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Козацький скарб» (slot-hold): 5 барабанів × 3 рядки, 20 ліній, Бунчук — дикий (нема на першому барабані).
/// Дукати на барабанах (номінал у ставках: ×1…×25, Міні ×20, Мажор ×100) ламають лінію; 6+ дукатів на полі —
/// «Утримуй і вигравай»: дукати стають у гнізда, 3 респіни-свічки, кожен новий дукат скидає свічки до 3, Булава
/// збирає суму всіх дукатів собі, Пірнач подвоює дукати й Булави, повне поле (15 гнізд) — Гетьманський скарб.
/// Увесь бонус рахується наперед у тому ж оберті. Порт моку <c>web/games/slots/slot-hold.js</c> (scriptFor/holdSim)
/// поле за полем; розбіжності — у docs/games/specs/slots.md, розділ «Козацький скарб».
/// </summary>
public sealed class SlotHoldMath : ISlotMath
{
    public const string Wild = "wild";
    public const int Cols = 5, Rows = 3, Lines = 20;
    /// <summary>Скільки дукатів на полі відкривають «Утримуй і вигравай».</summary>
    public const int HoldAt = 6;
    /// <summary>Свічок (респінів) на старті й після кожного нового дуката.</summary>
    public const int Candles = 3;

    // Стрічки по 30, дослівно з моку (клієнт крутить ті самі — символи над і під лінією чесні).
    public static readonly string[][] Reels =
    [
        "s1 s2 c1 s3 pipe s4 s1 mug s2 s3 horse s4 s1 c5 sabre s2 s3 s4 cossack s1 pipe c2 s2 s3 mug s4 sabre s1 c3 horse".Split(' '),
        "s2 s3 c2 s1 wild s4 mug s2 c1 s4 s3 pipe s1 horse s4 s2 sabre s1 s3 cossack s4 c5 s2 pipe s1 mug s3 c10 s4 c1".Split(' '),
        "s3 c1 s4 s1 horse s2 cmini s3 pipe s4 wild s1 c2 s2 mug s1 s3 sabre s4 c5 s1 cossack s2 s3 pipe s4 s1 mug c25 s2".Split(' '),
        "s4 s1 c3 s2 sabre s3 c1 c2 s4 pipe s1 wild s2 horse s3 mug s4 s1 cmajor s2 cossack s3 c1 s4 pipe s2 s1 c5 mug s3".Split(' '),
        "s1 c2 s3 s2 mug s4 c1 s1 horse s3 wild s2 s4 pipe s1 sabre c1 s3 s2 cossack s4 c3 s1 mug s3 c25 s2 pipe s4 cmini".Split(' '),
    ];

    /// <summary>20 ліній: рядок кожного барабана (0 — верх). Як LINES у моку.</summary>
    public static readonly int[][] PayLines =
    [
        [1, 1, 1, 1, 1], [0, 0, 0, 0, 0], [2, 2, 2, 2, 2], [0, 1, 2, 1, 0], [2, 1, 0, 1, 2],
        [0, 0, 1, 2, 2], [2, 2, 1, 0, 0], [1, 0, 0, 0, 1], [1, 2, 2, 2, 1], [1, 0, 1, 2, 1],
        [1, 2, 1, 0, 1], [0, 1, 1, 1, 0], [2, 1, 1, 1, 2], [0, 1, 0, 1, 0], [2, 1, 2, 1, 2],
        [1, 1, 0, 1, 1], [1, 1, 2, 1, 1], [0, 0, 2, 0, 0], [2, 2, 0, 2, 2], [0, 2, 0, 2, 0],
    ];

    /// <summary>Виплати в ставках на лінію (ставка ÷ 20): символ → (скільки в ряд → множник). Як PAY у моку.</summary>
    public static readonly Dictionary<string, Dictionary<int, int>> Pay = new(StringComparer.Ordinal)
    {
        ["wild"] = new() { [5] = 3000, [4] = 400, [3] = 75 },
        ["cossack"] = new() { [5] = 1250, [4] = 250, [3] = 60 },
        ["horse"] = new() { [5] = 625, [4] = 150, [3] = 50 },
        ["sabre"] = new() { [5] = 500, [4] = 125, [3] = 40 },
        ["mug"] = new() { [5] = 375, [4] = 100, [3] = 30 },
        ["pipe"] = new() { [5] = 300, [4] = 75, [3] = 25 },
        ["s1"] = new() { [5] = 150, [4] = 40, [3] = 15 },
        ["s2"] = new() { [5] = 150, [4] = 40, [3] = 15 },
        ["s3"] = new() { [5] = 125, [4] = 30, [3] = 12 },
        ["s4"] = new() { [5] = 125, [4] = 30, [3] = 12 },
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

    /// <summary>Сума виплат по всіх лініях у ставках на лінію.</summary>
    public static int Units(int[] stops)
    {
        var u = 0;
        Span<string> keys = new string[Cols];
        foreach (var ln in PayLines)
        {
            for (var c = 0; c < Cols; c++) keys[c] = At(stops, c, ln[c]);
            if (Line(keys) is { } w) u += w.Pay;
        }
        return u;
    }

    /// <summary>Скільки дукатів на полі.</summary>
    public static int Coins(int[] stops)
    {
        var n = 0;
        for (var c = 0; c < Cols; c++)
            for (var r = 0; r < Rows; r++) if (IsCoin(At(stops, c, r))) n++;
        return n;
    }

    public SlotOutcome Spin(int bet, ISlotRng rng, JsonObject state)
    {
        var stops = new int[Cols];
        for (var c = 0; c < Cols; c++) stops[c] = rng.Next(Reels[c].Length);
        return ScriptFor(stops, bet, state, rng);
    }

    /// <summary>Сценарій для заданих зупинок (бонус — з rng): поле за полем як scriptFor у моку.</summary>
    public SlotOutcome ScriptFor(int[] stops, int bet, JsonObject state, ISlotRng rng)
    {
        var items = new JsonArray();
        long lineWin = 0;
        var keys = new string[Cols];
        for (var i = 0; i < PayLines.Length; i++)
        {
            var ln = PayLines[i];
            for (var c = 0; c < Cols; c++) keys[c] = At(stops, c, ln[c]);
            if (Line(keys) is not { } w) continue;
            // як Math.round(u × ставка ÷ 20) у моку; для ставок, кратних 20, — рівно
            var amount = (long)Math.Round(w.Pay * (double)bet / Lines, MidpointRounding.AwayFromZero);
            var cells = new JsonArray();
            for (var c = 0; c < w.N; c++) cells.Add(new JsonArray(c, ln[c]));
            items.Add(new JsonObject { ["line"] = i, ["cells"] = cells, ["sym"] = w.Sym, ["amount"] = amount });
            lineWin += amount;
        }

        // Дукати поля (стовпчик за стовпчиком, згори вниз) і чесне очікування: барабан c тягнеться, лише коли на
        // попередніх уже рівно 5 дукатів (бракує одного до бонусу).
        var coins = new List<(int C, int R, string K, int V)>();
        var tease = new JsonArray();
        var before = 0;
        for (var c = 0; c < Cols; c++)
        {
            if (before == HoldAt - 1) tease.Add(c);
            for (var r = 0; r < Rows; r++)
            {
                var k = At(stops, c, r);
                if (!IsCoin(k)) continue;
                coins.Add((c, r, CoinKind(k), CoinValue[k]));
                before++;
            }
        }

        var capWin = (long)Cap * bet;
        var steps = new JsonArray { new JsonObject { ["t"] = "spin", ["stops"] = new JsonArray([.. stops.Select(s => (JsonNode)s)]), ["tease"] = tease } };
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
        if (capped) script["capped"] = true;
        List<string> flags = [];
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
        return new JsonObject
        {
            ["reels"] = reels, ["lines"] = lines, ["pay"] = pay, ["payUnit"] = 1.0 / Lines, ["wild"] = Wild, ["cap"] = Cap,
            ["coins"] = coins, ["jackpots"] = new JsonObject { ["mini"] = Mini, ["major"] = Major, ["grand"] = Grand },
            ["holdAt"] = HoldAt, ["candles"] = Candles,
        };
    }
}
