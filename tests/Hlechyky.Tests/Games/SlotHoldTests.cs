using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>Цифри «Козацького скарбу»: точне (лінії, частота бонусу) і симуляції тим самим класом, що грає на сервері.</summary>
public static class HoldStats
{
    public const int Bet = 100;

    /// <summary>Частка символу k (з диким) на барабані c.</summary>
    static double Q(int c, string k) =>
        SlotHoldMath.Reels[c].Count(x => x == k || x == SlotHoldMath.Wild) / (double)SlotHoldMath.Reels[c].Length;

    /// <summary>
    /// RTP ліній точно: усі 20 ліній мають однаковий розподіл (рядок на барабані не міняє частот, барабани незалежні),
    /// тож RTP ліній = очікування однієї лінії в ставках на лінію ÷ … × 20 ÷ 20. Бунчука на першому барабані нема — ряд
    /// завжди починається звичайним символом; дукат першим — лінія мертва.
    /// </summary>
    public static double ExactLineRtp()
    {
        var r0 = SlotHoldMath.Reels[0];
        double e = 0;
        foreach (var (sym, pay) in SlotHoldMath.Pay)
        {
            if (sym == SlotHoldMath.Wild) continue;
            var p1 = r0.Count(x => x == sym) / (double)r0.Length;
            // P(ряд ≥ n) = p1 × Q(1) × … × Q(n−1); рівно n = P(≥ n) − P(≥ n+1)
            var atLeast = new double[7];
            atLeast[1] = p1;
            for (var n = 2; n <= 5; n++) atLeast[n] = atLeast[n - 1] * Q(n - 1, sym);
            for (var n = 3; n <= 5; n++) e += (atLeast[n] - atLeast[n + 1]) * pay.GetValueOrDefault(n);
        }
        return e;   // очікування в ставках на лінію = RTP ліній (20 ліній × ставка ÷ 20)
    }

    /// <summary>Імовірність бонусу точно: згортка кількості дукатів у вікні кожного барабана.</summary>
    public static double ExactHoldChance()
    {
        var dist = new double[16];
        dist[0] = 1;
        for (var c = 0; c < SlotHoldMath.Cols; c++)
        {
            var reel = SlotHoldMath.Reels[c];
            var per = new double[4];
            for (var s = 0; s < reel.Length; s++)
            {
                var k = 0;
                for (var r = 0; r < 3; r++) if (SlotHoldMath.IsCoin(reel[(s + r) % reel.Length])) k++;
                per[k] += 1.0 / reel.Length;
            }
            var next = new double[16];
            for (var a = 0; a < 16; a++)
                for (var k = 0; k < 4 && a + k < 16; k++) next[a + k] += dist[a] * per[k];
            dist = next;
        }
        return dist.Skip(SlotHoldMath.HoldAt).Sum();
    }

    /// <summary>
    /// Очікування бонусу в ставках (за умови бонусу): перебір усіх 30⁵ зупинок, для кожного поля з 6+ дукатами —
    /// <paramref name="per"/> прогонів «Утримуй і вигравай» (вага поля точна, випадковість — лише в респінах).
    /// </summary>
    public static (double Ev, double Grand, double Chance) BonusEv(int per, int seed)
    {
        var rng = new SeededSlotRng(new Random(seed));
        var len = SlotHoldMath.Reels.Select(r => r.Length).ToArray();
        int[] s = new int[5];
        double sum = 0, grands = 0;
        long trig = 0, all = 0;
        for (s[0] = 0; s[0] < len[0]; s[0]++)
            for (s[1] = 0; s[1] < len[1]; s[1]++)
                for (s[2] = 0; s[2] < len[2]; s[2]++)
                    for (s[3] = 0; s[3] < len[3]; s[3]++)
                        for (s[4] = 0; s[4] < len[4]; s[4]++)
                        {
                            all++;
                            if (SlotHoldMath.Coins(s) < SlotHoldMath.HoldAt) continue;
                            trig++;
                            var coins = new List<(int, int, string, int)>();
                            for (var c = 0; c < 5; c++)
                                for (var r = 0; r < 3; r++)
                                {
                                    var k = SlotHoldMath.At(s, c, r);
                                    if (SlotHoldMath.IsCoin(k)) coins.Add((c, r, SlotHoldMath.CoinKind(k), SlotHoldMath.CoinValue[k]));
                                }
                            var room = 1000L * Bet - SlotHoldMath.Units(s) * Bet / 20;
                            for (var i = 0; i < per; i++)
                            {
                                var h = SlotHoldMath.Hold(coins, Bet, rng, room);
                                sum += h.Pay;
                                if (h.Full) grands++;
                            }
                        }
        return (sum / Bet / (trig * (double)per), grands / (trig * (double)per), (double)trig / all);
    }

    public sealed record Sim(int N, double Rtp, double Hit, double Bonus, double BonusAvg, double Grand, double Max,
        double LineRtp, double Over100, long Capped, double Sd);

    public static Sim Simulate(int n, int seed)
    {
        var m = new SlotHoldMath();
        var rng = new SeededSlotRng(new Random(seed));
        double sq = 0;
        long won = 0, lineWon = 0, hits = 0, holds = 0, holdWon = 0, grands = 0, max = 0, over100 = 0, capped = 0;
        var st = new JsonObject();
        for (var i = 0; i < n; i++)
        {
            var o = m.Spin(Bet, rng, st);
            won += o.Win;
            sq += (double)o.Win / Bet * o.Win / Bet;
            if (o.Win > 0) hits++;
            if (o.Win > max) max = o.Win;
            if (o.Win >= 100L * Bet) over100++;
            var s = o.Script;
            if (s["capped"] is not null) capped++;
            long lw = 0;
            foreach (var step in s["steps"]!.AsArray())
                if ((string)step!["t"]! == "win") lw = (long)step["amount"]!;
            lineWon += lw;
            if ((bool)s["hold"]!) { holds++; holdWon += o.Win - lw; }
            if ((bool)s["full"]!) grands++;
        }
        double bet = (double)n * Bet;
        return new Sim(n, won / bet, (double)hits / n, (double)holds / n, holds > 0 ? holdWon / (double)holds / Bet : 0,
            (double)grands / n, (double)max / Bet, lineWon / bet, (double)over100 / n, capped,
            Math.Sqrt(sq / n - won / bet * (won / bet)));
    }

    public static string Line(string what, Sim s) =>
        $"{what}: RTP {s.Rtp:P2} (лінії {s.LineRtp:P2}), виграш {s.Hit:P2} (1 з {1 / s.Hit:F2}), бонус 1 з {1 / s.Bonus:F1}, " +
        $"середній бонус {s.BonusAvg:F1}×, Гетьманський 1 з {(s.Grand > 0 ? (1 / s.Grand).ToString("F0") : "—")}, " +
        $"≥100× 1 з {(s.Over100 > 0 ? (1 / s.Over100).ToString("F0") : "—")}, найбільше {s.Max:F1}×, обрізано стелею {s.Capped}, σ оберту {s.Sd:F2} ставки (σ RTP {s.Sd / Math.Sqrt(s.N):P2})";
}

/// <summary>Симуляції (spec «Козацький скарб» → таблиця). Друкують цифри: --logger "console;verbosity=detailed".</summary>
public class SlotHoldSimTests(ITestOutputHelper output)
{
    /// <summary>RTP бази: лінії точно + бонус перебором усіх полів (Perf-тест нижче) ≈ 95,5 %.</summary>
    const double Rtp = 0.955;

    [Fact]
    public void Exact_line_rtp_and_bonus_chance()
    {
        var line = HoldStats.ExactLineRtp();
        var ch = HoldStats.ExactHoldChance();
        output.WriteLine($"точно: лінії {line:P3}, бонус 1 з {1 / ch:F2}");
        Assert.InRange(line, 0.43, 0.45);
        Assert.InRange(1 / ch, 100, 130);
    }

    [Fact]
    public void Two_hundred_thousand_spins_land_near_the_target()
    {
        // Висока волатильність: σ RTP на 200 тис. обертів ≈ 1,7 % (сіди 1–8 дають 92,4…99,7 %), тож ±2 % тут — грубий
        // запобіжник на фіксованому сіді; точність — у Perf-тестах нижче (5 млн і перебір полів).
        var s = HoldStats.Simulate(200_000, 3);
        output.WriteLine(HoldStats.Line("200 тис.", s));
        Assert.InRange(s.Rtp, Rtp - 0.02, Rtp + 0.02);
        Assert.InRange(s.LineRtp, HoldStats.ExactLineRtp() - 0.01, HoldStats.ExactLineRtp() + 0.01);
        Assert.InRange(1 / s.Bonus, 100, 135);
        Assert.InRange(s.Hit, 0.30, 0.33);
        Assert.True(s.Max <= 1000);
    }

    [Fact, Trait("Category", "Perf")]
    public void Five_million_spins_within_half_a_percent()
    {
        // σ RTP на 5 млн ≈ 0,35 %: сіди 2–5 дали 94,88 / 95,36 / 95,03 / 95,83 % (у середньому 95,28 % на 20 млн).
        var s = HoldStats.Simulate(5_000_000, 3);
        output.WriteLine(HoldStats.Line("5 млн", s));
        Assert.InRange(s.Rtp, Rtp - 0.005, Rtp + 0.005);
        Assert.InRange(1 / s.Bonus, 110, 124);
        Assert.True(s.Max <= 1000);
    }

    [Fact, Trait("Category", "Perf")]
    public void Bonus_expectation_over_every_field()
    {
        // Усі 30⁵ полів з точною вагою, по 20 бонусів на кожне поле з 6+ дукатами (≈ 4,2 млн бонусів).
        var (ev, gr, ch) = HoldStats.BonusEv(20, 11);
        var rtp = HoldStats.ExactLineRtp() + ch * ev;
        output.WriteLine($"бонус 1 з {1 / ch:F2}, середній {ev:F2}×, Гетьманський за умови бонусу 1 з {1 / gr:F0}; RTP ≈ {rtp:P3}");
        Assert.InRange(rtp, Rtp - 0.003, Rtp + 0.003);
    }
}

/// <summary>Обв'язка: каса автоматів і кімната «Козацького скарбу» через справжній каркас.</summary>
public sealed class HoldKit
{
    public FakeStakes Stakes { get; } = new();
    public SlotsOptions Options { get; } = new();
    public SlotsBank Bank { get; }
    public RoomHarness H { get; }

    public HoldKit(int wallet = 100_000, int seed = 42)
    {
        Stakes.Set("Оля", wallet);
        Bank = new SlotsBank(Stakes, new FakeStore(), () => Options, new FakeClock(), new FakeSlotsWire(), defer: a => a());
        H = new RoomHarness("slot-hold", services: RoomHarness.WithService(Bank), seed: seed);
        var r = H.Solo("Оля");
        Assert.True(r.Ok, r.Message);
    }

    public SlotGame G => (SlotGame)H.Room.Game;
    public ActResult Spin(int bet = 20) => H.Act(0, "spin", new { bet });
    public JsonElement View => H.View(0);
}

public class SlotHoldTests
{
    const int Bet = 20;
    static readonly SlotHoldMath M = new();

    /// <summary>Перші випадкові зупинки, що задовольняють умову.</summary>
    static int[] StopsWhere(Func<int[], bool> pred, int seed = 1)
    {
        var rnd = new Random(seed);
        for (var i = 0; i < 5_000_000; i++)
        {
            int[] s = [.. SlotHoldMath.Reels.Select(r => rnd.Next(r.Length))];
            if (pred(s)) return s;
        }
        throw new InvalidOperationException("таких зупинок нема");
    }

    static List<(int C, int R, string K, int V)> CoinsOf(int[] s)
    {
        var l = new List<(int, int, string, int)>();
        for (var c = 0; c < 5; c++)
            for (var r = 0; r < 3; r++)
                if (SlotHoldMath.At(s, c, r) is var k && SlotHoldMath.IsCoin(k)) l.Add((c, r, SlotHoldMath.CoinKind(k), SlotHoldMath.CoinValue[k]));
        return l;
    }

    static int CoinsBefore(int[] s, int cols)
    {
        var b = 0;
        for (var c = 0; c < cols; c++)
            for (var r = 0; r < 3; r++) if (SlotHoldMath.IsCoin(SlotHoldMath.At(s, c, r))) b++;
        return b;
    }

    /// <summary>Шість дукатів у перших двох стовпчиках: порожні гнізда — (2,0)…(4,2), дев'ять, у цьому порядку.</summary>
    static readonly List<(int C, int R, string K, int V)> Six =
        [(0, 0, "coin", 1), (0, 1, "coin", 2), (0, 2, "coin", 3), (1, 0, "coin", 5), (1, 1, "mini", 20), (1, 2, "coin", 10)];

    static string[] Types(IEnumerable<JsonObject> steps) => [.. steps.Select(x => (string)x["t"]!)];
    static JsonObject Step(SlotHoldMath.HoldRun h, string t) => h.Steps.Single(x => (string)x["t"]! == t);
    static List<JsonObject> Steps(SlotOutcome o) => [.. o.Script["steps"]!.AsArray().Select(x => x!.AsObject())];

    /// <summary>Респін: нічого не впало в жодне з n порожніх гнізд.</summary>
    static ScriptRng Miss(ScriptRng r, int n) { for (var i = 0; i < n; i++) r.Doubles.Enqueue(0.999); return r; }

    /// <summary>У порожньому гнізді впало: x — перший кидок (Булава/Пірнач/Міні/Мажор/дукат), y — номінал дуката.</summary>
    static ScriptRng Land(ScriptRng r, double x, double? y = null)
    {
        r.Doubles.Enqueue(0.0);
        r.Doubles.Enqueue(x);
        if (y is { } v) r.Doubles.Enqueue(v);
        return r;
    }

    [Theory]
    [InlineData("s1 wild s1 s2 s3", "s1", 3, 15)]
    [InlineData("cossack wild wild wild wild", "cossack", 5, 1250)]
    [InlineData("s4 wild wild wild wild", "s4", 5, 125)]
    [InlineData("pipe pipe pipe pipe s1", "pipe", 4, 75)]
    [InlineData("horse wild c5 horse horse", null, 0, 0)]   // дукат ламає ряд на третьому
    [InlineData("c5 wild wild wild wild", null, 0, 0)]      // дукат першим — лишається хіба ряд Бунчуків з першого
    [InlineData("s1 s1 s2 s1 s1", null, 0, 0)]
    public void Lines_read_left_to_right_with_the_wild(string keys, string? sym, int n, int pay)
    {
        var w = SlotHoldMath.Line(keys.Split(' '));
        if (sym is null) { Assert.Null(w); return; }
        Assert.Equal((sym, n, pay), w!.Value);
    }

    [Fact]
    public void A_row_of_wilds_pays_as_wilds_when_dearer()
    {
        // Ряд Бунчуків з першого барабана — окреме прочитання (Бунчука на першому нема, тож у грі недосяжне — як у моку).
        Assert.Equal(("wild", 4, 400), SlotHoldMath.Line(["wild", "wild", "wild", "wild", "s4"]));
        Assert.Equal(("s4", 5, 125), SlotHoldMath.Line(["wild", "wild", "wild", "s4", "s4"]));
        Assert.DoesNotContain("wild", SlotHoldMath.Reels[0]);
    }

    [Fact]
    public void Win_step_lists_every_line_with_its_cells_and_amount()
    {
        var s = StopsWhere(x => SlotHoldMath.Units(x) >= 60 && SlotHoldMath.Coins(x) < 6);
        var o = M.ScriptFor(s, Bet, new JsonObject(), new ScriptRng());
        var win = Steps(o).Single(x => (string)x["t"]! == "win");
        long sum = 0;
        foreach (var it in win["items"]!.AsArray())
        {
            var ln = SlotHoldMath.PayLines[(int)it!["line"]!];
            var cells = it["cells"]!.AsArray();
            var w = SlotHoldMath.Line(ln.Select((r, c) => SlotHoldMath.At(s, c, r)).ToArray())!.Value;
            Assert.Equal(w.Sym, (string)it["sym"]!);
            Assert.Equal(w.N, cells.Count);
            for (var c = 0; c < cells.Count; c++) Assert.Equal([c, ln[c]], cells[c]!.AsArray().Select(x => (int)x!));
            Assert.Equal(w.Pay * Bet / 20, (long)it["amount"]!);
            sum += (long)it["amount"]!;
        }
        Assert.Equal(SlotHoldMath.Units(s) * Bet / 20, sum);
        Assert.Equal(sum, (long)win["amount"]!);
        Assert.Equal(sum, o.Win);
        Assert.False((bool)o.Script["hold"]!);
    }

    [Fact]
    public void Six_coins_open_hold_and_win_in_the_same_spin()
    {
        var s = StopsWhere(x => SlotHoldMath.Coins(x) == 6);
        var o = M.ScriptFor(s, Bet, new JsonObject(), new ScriptRng());
        var steps = Steps(o);
        var holdIn = steps.Single(x => (string)x["t"]! == "holdIn");
        Assert.Equal(CoinsOf(s).Select(x => $"[{x.C},{x.R},\"{x.K}\",{x.V}]"), holdIn["coins"]!.AsArray().Select(x => x!.ToJsonString()));
        // нічого не впало: три порожні респіни 3→2→1→0
        Assert.Equal([3, 2, 1], steps.Where(x => (string)x["t"]! == "respin").Select(x => (int)x["left"]!));
        var coins = CoinsOf(s).Sum(x => (long)x.V * Bet);
        Assert.Equal(coins, (long)steps.Single(x => (string)x["t"]! == "holdCount")["total"]!);
        Assert.Equal("holdOut", (string)steps[^1]["t"]!);
        Assert.Equal(coins, (long)steps[^1]["total"]!);
        Assert.True((bool)o.Script["hold"]!);
        Assert.Contains("hold", o.Flags);
        var line = steps.Where(x => (string)x["t"]! == "win").Sum(x => (long)x["amount"]!);
        Assert.Equal(line + coins, o.Win);

        var five = StopsWhere(x => SlotHoldMath.Coins(x) == 5);
        Assert.False((bool)M.ScriptFor(five, Bet, new JsonObject(), new ScriptRng()).Script["hold"]!);
    }

    [Fact]
    public void Tease_only_when_exactly_five_coins_are_already_down()
    {
        for (var seed = 1; seed <= 40; seed++)
        {
            var s = StopsWhere(x => SlotHoldMath.Coins(x) >= 4, seed);
            var tease = Steps(M.ScriptFor(s, Bet, new JsonObject(), new ScriptRng()))[0]["tease"]!.AsArray().Select(x => (int)x!);
            Assert.Equal(Enumerable.Range(0, 5).Where(c => CoinsBefore(s, c) == 5), tease);
        }
        var t = StopsWhere(x => CoinsBefore(x, 4) == 5);
        Assert.Contains(4, Steps(M.ScriptFor(t, Bet, new JsonObject(), new ScriptRng()))[0]["tease"]!.AsArray().Select(x => (int)x!));
    }

    [Fact]
    public void A_new_coin_resets_the_candles_to_three()
    {
        var rng = Miss(new ScriptRng(), 9);                         // 3 → 2
        Land(rng, 0.5, 0.5);                                         // (2,0): дукат ×2 → знову 3
        Miss(rng, 8);
        var h = SlotHoldMath.Hold(Six, Bet, rng, 1000L * Bet);
        var res = h.Steps.Where(x => (string)x["t"]! == "respin").ToList();
        Assert.Equal([(3, 2), (2, 3), (3, 2), (2, 1), (1, 0)], res.Select(x => ((int)x["left"]!, (int)x["after"]!)));
        Assert.Equal("[[2,0,\"coin\",2]]", res[1]["land"]!.ToJsonString());
        Assert.Equal([false, false, false, false, true], res.Select(x => (bool)x["slow"]!));
        Assert.Equal(5, h.Respins);
        Assert.Equal((1 + 2 + 3 + 5 + 20 + 10 + 2) * Bet, h.Total);
        Assert.Equal(h.Total, h.Pay);
        Assert.False(h.Full);
        Assert.Equal(["holdIn", "respin", "respin", "respin", "respin", "respin", "holdCount", "holdOut"], Types(h.Steps));
    }

    [Fact]
    public void Mace_collects_every_coin_and_keeps_them()
    {
        var h = SlotHoldMath.Hold(Six, Bet, Land(new ScriptRng(), 0.01), 1000L * Bet);   // (2,0): Булава
        var col = Step(h, "collect");
        Assert.Equal("[2,0]", col["at"]!.ToJsonString());
        Assert.Equal(5, col["from"]!.AsArray().Count);               // Міні — джекпот, Булава його не бере
        Assert.Equal(1 + 2 + 3 + 5 + 10, (long)col["total"]!);
        Assert.Equal((21 + 20 + 21) * Bet, h.Total);                 // дукати лишились + Міні + Булава
        Assert.Contains("[2,0," + 21 * Bet + ",\"mace\"]", Step(h, "holdCount")["cells"]!.ToJsonString());
    }

    [Fact]
    public void Pirnach_doubles_coins_and_maces_but_not_jackpots()
    {
        var rng = Land(new ScriptRng(), 0.01);                       // (2,0): Булава
        Land(rng, 0.06);                                             // (2,1): Пірнач — у тому ж респіні
        var h = SlotHoldMath.Hold(Six, Bet, rng, 1000L * Bet);
        // спершу Пірнач (Булава ще порожня — не множиться), потім Булава збирає подвоєне
        Assert.Equal(["holdIn", "respin", "double", "collect"], Types(h.Steps).Take(4));
        var dbl = Step(h, "double");
        Assert.Equal("[2,1]", dbl["at"]!.ToJsonString());
        Assert.Equal("[[0,0,2],[0,1,4],[0,2,6],[1,0,10],[1,2,20]]", dbl["cells"]!.ToJsonString());
        Assert.Equal(42, (long)Step(h, "collect")["total"]!);
        Assert.Equal((42 + 20 + 42) * Bet, h.Total);                 // Пірнач сам нічого не платить
        Assert.DoesNotContain("pirnach", Step(h, "holdCount")["cells"]!.ToJsonString());

        // Пірнач пізніше подвоює й Булаву, що вже зібрала
        var later = Land(new ScriptRng(), 0.01);
        Miss(later, 8);
        Land(later, 0.06);
        var h2 = SlotHoldMath.Hold(Six, Bet, later, 1000L * Bet);
        Assert.Contains("[2,0,42]", Step(h2, "double")["cells"]!.ToJsonString());
        Assert.Equal((42 + 20 + 42) * Bet, h2.Total);
    }

    [Fact]
    public void Mini_and_major_land_with_their_jackpots()
    {
        var rng = Land(new ScriptRng(), 0.10);                       // (2,0): Міні
        Land(rng, 0.125);                                            // (2,1): Мажор
        var h = SlotHoldMath.Hold(Six, Bet, rng, 1000L * Bet);
        Assert.Equal("[[2,0,\"mini\",20],[2,1,\"major\",100]]", h.Steps[1]["land"]!.ToJsonString());
        var cells = Step(h, "holdCount")["cells"]!.ToJsonString();
        Assert.Contains($"[2,0,{20 * Bet},\"mini\"]", cells);
        Assert.Contains($"[2,1,{100 * Bet},\"major\"]", cells);
        Assert.Equal((21 + 20 + 20 + 100) * Bet, h.Total);
    }

    [Fact]
    public void Full_field_is_the_hetman_treasure_and_the_cap()
    {
        var rng = new ScriptRng();
        for (var i = 0; i < 9; i++) Land(rng, 0.5, 0.0);            // усі дев'ять — дукати ×1
        var room = 1000L * Bet - 3 * Bet;                            // лінії вже дали 3 ставки
        var h = SlotHoldMath.Hold(Six, Bet, rng, room);
        Assert.True(h.Full);
        Assert.Equal(1, h.Respins);
        Assert.Equal((41 + 9) * Bet, h.Total);
        Assert.Equal(room - h.Total, h.GrandAmount);
        Assert.Equal(room, h.Pay);
        Assert.Equal(["holdIn", "respin", "holdCount", "grand", "holdOut"], Types(h.Steps));
        Assert.Equal(room - h.Total, (long)Step(h, "grand")["amount"]!);
        Assert.Equal(room, (long)Step(h, "holdOut")["total"]!);
        Assert.False(h.Capped);
    }

    [Fact]
    public void Nothing_pays_above_a_thousand_bets()
    {
        var h = SlotHoldMath.Hold(Six, Bet, Land(new ScriptRng(), 0.125), 50L * Bet);   // Мажор, до стелі — 50 ставок
        Assert.True(h.Capped);
        Assert.Equal(50L * Bet, h.Pay);
        Assert.Equal(50L * Bet, (long)h.Steps[^1]["total"]!);

        // увесь оберт: лінії + скриня ≤ 1000×, позначка capped у сценарії
        var s = StopsWhere(x => SlotHoldMath.Coins(x) == 6);
        var big = new ScriptRng();
        for (var i = 0; i < 9; i++) Land(big, 0.125);               // усе — Мажори: 900 ставок + дукати
        var o = M.ScriptFor(s, Bet, new JsonObject(), big);
        Assert.Equal(1000 * Bet, o.Win);
        Assert.True((bool)o.Script["full"]!);
        Assert.Contains("grand", o.Flags);
        var line = Steps(o).Where(x => (string)x["t"]! == "win").Sum(x => (long)x["amount"]!);
        var total = (long)Steps(o).Single(x => (string)x["t"]! == "holdCount")["total"]!;
        Assert.Equal(Math.Max(0, 1000 * Bet - line - total), (long)Steps(o).Single(x => (string)x["t"]! == "grand")["amount"]!);
        Assert.Equal(total > 1000 * Bet - line, o.Script["capped"] is not null);
    }

    [Fact]
    public void Same_seed_same_spins()
    {
        string Run(int seed)
        {
            var rng = new SeededSlotRng(new Random(seed));
            return string.Join('\n', Enumerable.Range(0, 3000).Select(_ => M.Spin(Bet, rng, new JsonObject()).Script.ToJsonString()));
        }
        Assert.Equal(Run(5), Run(5));
        Assert.NotEqual(Run(5), Run(6));
    }

    [Fact]
    public void Script_has_the_shape_of_the_mock()
    {
        var rng = new SeededSlotRng(new Random(9));
        int holds = 0, specials = 0;
        for (var i = 0; i < 40_000; i++)
        {
            var o = M.Spin(Bet, rng, new JsonObject());
            var s = o.Script;
            Assert.Equal(["bet", "steps", "win", "state", "hold", "full"], s.Select(x => x.Key).Where(k => k != "capped"));
            var steps = Steps(o);
            Assert.Equal(["t", "stops", "tease"], steps[0].Select(x => x.Key));
            Assert.Equal(5, steps[0]["stops"]!.AsArray().Count);
            long line = 0, chest = 0;
            var phase = 0;   // 0 — база, 1 — респіни, 2 — підрахунок, 3 — кінець
            foreach (var st in steps.Skip(1))
            {
                var t = (string)st["t"]!;
                var keys = st.Select(x => x.Key).ToArray();
                switch (t)
                {
                    case "win": Assert.Equal(0, phase); line = (long)st["amount"]!; Assert.Equal(["t", "items", "amount"], keys); break;
                    case "holdIn": Assert.Equal(0, phase); phase = 1; Assert.True(st["coins"]!.AsArray().Count >= 6); break;
                    case "respin": Assert.Equal(1, phase); Assert.Equal(["t", "left", "after", "land", "slow"], keys); break;
                    case "double": Assert.Equal(1, phase); specials++; Assert.Equal(["t", "at", "cells"], keys); break;
                    case "collect": Assert.Equal(1, phase); specials++; Assert.Equal(["t", "at", "from", "total"], keys); break;
                    case "holdCount": Assert.Equal(1, phase); phase = 2; Assert.Equal(["t", "cells", "total"], keys); break;
                    case "grand": Assert.Equal(2, phase); Assert.True((bool)s["full"]!); break;
                    case "holdOut": Assert.Equal(2, phase); phase = 3; chest = (long)st["total"]!; holds++; break;
                    default: Assert.Fail("невідомий крок " + t); break;
                }
            }
            Assert.Equal((bool)s["hold"]!, phase == 3);
            Assert.Equal(line + chest, (long)s["win"]!);
            Assert.Equal(o.Win, (long)s["win"]!);
        }
        Assert.InRange(holds, 250, 450);
        Assert.True(specials > 0);
    }

    [Fact]
    public void Spin_takes_the_bet_and_pays_the_win_exactly_once()
    {
        static int Sum(FakeStakes s, string suffix) => s.Ledger.Where(m => m.Ref.EndsWith(suffix, StringComparison.Ordinal)).Sum(m => m.Delta);
        var k = new HoldKit();
        long won = 0;
        for (var i = 1; i <= 400; i++)
        {
            var r = k.Spin(20);
            Assert.True(r.Ok, r.Message);
            var last = k.View.GetProperty("last");
            Assert.Equal(i, last.GetProperty("seq").GetInt32());
            var sc = last.GetProperty("script");
            won += sc.GetProperty("win").GetInt32();
            won += sc.TryGetProperty("jackpot", out var jp) ? jp.GetInt32() : 0;
            Assert.Equal(JsonValueKind.Null, k.View.GetProperty("gamble").ValueKind);   // Ворожки тут нема
        }
        Assert.Equal(400, k.Stakes.Ledger.Count(m => m.Ref.EndsWith(":bet", StringComparison.Ordinal)));
        Assert.Equal(-400 * 20, Sum(k.Stakes, ":bet"));
        Assert.Equal(won, Sum(k.Stakes, ":win") + Sum(k.Stakes, ":jp"));
        Assert.Equal(100_000 - 8000 + won, k.Stakes.Balance("Оля"));
        Assert.All(k.Stakes.Reasons.Where(x => x.Key.EndsWith(":bet")), x => Assert.Equal("slot-bet:slot-hold", x.Value));
        Assert.Empty(k.Bank.Pending());
        Assert.False(k.H.Act(0, "gamble", new { pick = "r" }).Ok);
        Assert.Equal(20, k.View.GetProperty("table").GetProperty("lines").GetArrayLength());
    }

    [Fact]
    public void Rigged_hold_spin_pays_once_through_the_room()
    {
        var k = new HoldKit();
        var s = StopsWhere(x => SlotHoldMath.Coins(x) == 6);
        k.G.Rng = Land(new ScriptRng().Stops(s), 0.125);             // Мажор у першому порожньому гнізді
        Assert.True(k.Spin(20).Ok);
        var sc = k.View.GetProperty("last").GetProperty("script");
        Assert.True(sc.GetProperty("hold").GetBoolean());
        var win = sc.GetProperty("win").GetInt32();
        Assert.True(win >= 100 * 20);
        Assert.Equal(100_000 - 20 + win, k.Stakes.Balance("Оля"));
        Assert.Single(k.Stakes.Ledger, m => m.Ref.EndsWith(":win", StringComparison.Ordinal));
    }
}
