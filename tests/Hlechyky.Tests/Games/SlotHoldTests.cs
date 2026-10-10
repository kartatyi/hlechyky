using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Цифри «Козацького скарбу»: точне (лінії, виграш, частота бонусу з дощем — перебір усіх 30⁵ полів) і бонус — лінійною
/// моделлю скрині, яку міряє той самий <see cref="SlotHoldMath.Hold"/>, що грає на сервері; симуляції — тим самим класом.
/// </summary>
public static class HoldStats
{
    public const int Bet = 100;

    /// <summary>Частка символу k (з диким) на барабані c.</summary>
    static double Q(int c, string k) =>
        SlotHoldMath.Reels[c].Count(x => x == k || x == SlotHoldMath.Wild) / (double)SlotHoldMath.Reels[c].Length;

    /// <summary>
    /// RTP ліній точно: усі 20 ліній мають однаковий розподіл (рядок на барабані не міняє частот, барабани незалежні),
    /// тож RTP ліній = очікування однієї лінії в ставках на лінію ÷ … × 20 ÷ 20. Бунчука на першому барабані нема — ряд
    /// завжди починається звичайним символом; дукат першим — лінія мертва. Дощ ліній не міняє (падає поза виграшними рядами).
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

    /// <summary>Розподіл кількості дукатів на полі барабанів (до дощу) точно: згортка вікон кожного барабана.</summary>
    public static double[] CoinDist()
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
        return dist;
    }

    /// <summary>Імовірність бонусу без дощу точно.</summary>
    public static double ExactHoldChance() => CoinDist().Skip(SlotHoldMath.HoldAt).Sum();

    /// <summary>Частки «скільки дукатів падає» з <see cref="SlotHoldMath.RainCounts"/>: [k] — шанс рівно k (1…4).</summary>
    public static double[] RainCountShare()
    {
        var p = new double[5];
        var prev = 0.0;
        foreach (var (below, n) in SlotHoldMath.RainCounts) { p[n] += below - prev; prev = below; }
        return p;
    }

    /// <summary>
    /// Бонус з дощем, якби вільних клітинок завжди вистачало (їх майже завжди ≥ 4): згортка + дощ. Точно — у <see cref="Exact"/>.
    /// </summary>
    public static double HoldChanceWithRainBound()
    {
        var d = CoinDist();
        var pk = RainCountShare();
        double rain = 0;
        for (var n = 0; n < 16; n++)
            for (var k = 1; k <= 4; k++) if (n + k >= SlotHoldMath.HoldAt) rain += d[n] * pk[k];
        return (1 - SlotHoldMath.RainChance) * d.Skip(SlotHoldMath.HoldAt).Sum() + SlotHoldMath.RainChance * rain;
    }

    /// <summary>
    /// Лінійна модель скрині. «Утримуй і вигравай» від стартових дукатів залежить лише через їх кількість n (гнізда
    /// рівноправні — що й де впаде, від номіналів не залежить), а сума — лінійна за номіналами: кожен звичайний стартовий
    /// дукат усі Пірначі подвоюють і всі Булави збирають однаково, джекпот лишається собою. Тож для неповного поля
    /// виплата = a·S + J + b (S — сума звичайних стартових, J — джекпотів, a і b — від шляху респінів), повне поле —
    /// стеля. A = E[a; неповне], B = E[b; неповне], D = P(неповне), F = P(повне). Міряємо справжнім
    /// <see cref="SlotHoldMath.Hold"/>: той самий шлях (той самий сід) з усіма номіналами ×1 і ×2 дає a·n і b.
    /// Var — дисперсія a·s̄n + b + 1000·повне на прогін (s̄ = 2,5 ставки на дукат) — для σ оцінки RTP.
    /// </summary>
    public sealed record Model(double[] A, double[] B, double[] D, double[] F, double[] Var, int[] Runs);

    public static Model HoldModel(int runs6, int seed)
    {
        double[] a = new double[16], b = new double[16], d = new double[16], f = new double[16], v = new double[16];
        var runs = new int[16];
        var ys = new double[16];
        var lk = new object();
        for (var n = SlotHoldMath.HoldAt; n <= 15; n++)
        {
            var r = n switch { 6 => runs6, 7 => runs6 * 2 / 3, 8 => runs6 / 3, _ => runs6 / 6 };
            runs[n] = r;
            var one = Enumerable.Range(0, n).Select(i => (i / 3, i % 3, "coin", 1)).ToList();
            var two = one.Select(x => (x.Item1, x.Item2, x.Item3, 2)).ToList();
            var nn = n;
            Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = 4 }, part =>
            {
                double sa = 0, sb = 0, sd = 0, sf = 0, sy = 0, sy2 = 0;
                for (var i = part; i < r; i += 4)
                {
                    var s = seed * 7_919 + nn * 1_000_003 + i;
                    var h1 = SlotHoldMath.Hold(one, 1, new SeededSlotRng(new Random(s)), 1L << 50);
                    double y;
                    if (h1.Full) { sf++; y = 1000; }
                    else
                    {
                        var h2 = SlotHoldMath.Hold(two, 1, new SeededSlotRng(new Random(s)), 1L << 50);
                        var an = (double)(h2.Total - h1.Total);
                        var bi = 2.0 * h1.Total - h2.Total;
                        sa += an / nn; sb += bi; sd++;
                        y = an / nn * 2.5 * nn + bi;
                    }
                    sy += y; sy2 += y * y;
                }
                lock (lk) { a[nn] += sa; b[nn] += sb; d[nn] += sd; f[nn] += sf; v[nn] += sy2; ys[nn] += sy; }
            });
            a[n] /= r; b[n] /= r; d[n] /= r; f[n] /= r;
            var mean = ys[n] / r;
            v[n] = v[n] / r - mean * mean;
        }
        return new Model(a, b, d, f, v, runs);
    }

    /// <summary>Очікування скрині в ставках: n стартових дукатів, сума звичайних S, джекпотів J, стеля room (ставок).</summary>
    public static double Ev(Model m, int n, double s, double j, double room) =>
        m.A[n] * s + m.D[n] * j + m.B[n] + m.F[n] * room;

    public sealed record ExactResult(double Rtp, double LineRtp, double Hit, double Bonus, double BonusBase, double RainOpens,
        double BonusAvg, double GrandPerSpin, double Sd);

    /// <summary>
    /// Перебір усіх 30⁵ полів з точною вагою: лінії, виграш (лінії або бонус), бонус без дощу й з дощем (1–4 дукати лише
    /// на вільні клітинки — <see cref="SlotHoldMath.RainCells"/>), очікування скрині — моделлю (дощ додає k дукатів; за
    /// лінійністю досить середнього номіналу дощу). Паралельно не більше 4 потоків.
    /// </summary>
    public static ExactResult Exact(Model m)
    {
        var R = SlotHoldMath.Reels;
        var win = R.Select(reel => Enumerable.Range(0, reel.Length).Select(s => new[] { reel[s], reel[(s + 1) % reel.Length], reel[(s + 2) % reel.Length] }).ToArray()).ToArray();
        var pk = RainCountShare();
        double tw = SlotHoldMath.RainCoins.Sum(x => x.W);
        double muS = SlotHoldMath.RainCoins.Where(x => SlotHoldMath.CoinKind(x.K) == "coin").Sum(x => x.W * SlotHoldMath.CoinValue[x.K]) / tw;
        double muJ = SlotHoldMath.RainCoins.Where(x => SlotHoldMath.CoinKind(x.K) != "coin").Sum(x => x.W * SlotHoldMath.CoinValue[x.K]) / tw;
        var p = SlotHoldMath.RainChance;
        double line = 0, hit = 0, bon = 0, bon0 = 0, opens = 0, ev = 0, grand = 0;
        var wn = new double[16];
        long all = 0;
        var lk = new object();
        Parallel.For(0, R[0].Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, s0 =>
        {
            double l = 0, h = 0, b = 0, b0 = 0, o = 0, e = 0, gr = 0;
            var w = new double[16];
            long cnt = 0;
            var g = new string[5][];
            g[0] = win[0][s0];
            for (var s1 = 0; s1 < R[1].Length; s1++)
            for (var s2 = 0; s2 < R[2].Length; s2++)
            for (var s3 = 0; s3 < R[3].Length; s3++)
            for (var s4 = 0; s4 < R[4].Length; s4++)
            {
                g[1] = win[1][s1]; g[2] = win[2][s2]; g[3] = win[3][s3]; g[4] = win[4][s4];
                cnt++;
                var wins = SlotHoldMath.Wins(g);
                var u = 0;
                foreach (var x in wins) u += x.Pay;
                int n = 0, sv = 0, jv = 0;
                foreach (var col in g)
                    foreach (var k in col)
                    {
                        if (!SlotHoldMath.IsCoin(k)) continue;
                        n++;
                        if (SlotHoldMath.CoinKind(k) == "coin") sv += SlotHoldMath.CoinValue[k]; else jv += SlotHoldMath.CoinValue[k];
                    }
                l += u / 20.0;
                var room = 1000 - u / 20.0;
                double pb = 0, pe = 0, pg = 0;
                if (n >= SlotHoldMath.HoldAt)
                {
                    b0++;
                    pb = 1 - p; pe = (1 - p) * Ev(m, n, sv, jv, room); pg = (1 - p) * m.F[n];
                    w[n] += 1 - p;
                }
                if (n + 4 >= SlotHoldMath.HoldAt)
                {
                    var free = SlotHoldMath.RainCells(g, wins).Count;
                    for (var k = 1; k <= 4; k++)
                    {
                        var kk = Math.Min(k, free);
                        var nn = n + kk;
                        if (nn < SlotHoldMath.HoldAt) continue;
                        var q = p * pk[k];
                        pb += q; pe += q * Ev(m, nn, sv + kk * muS, jv + kk * muJ, room); pg += q * m.F[nn];
                        w[nn] += q;
                        if (n < SlotHoldMath.HoldAt) o += q;
                    }
                }
                b += pb; e += pe; gr += pg;
                h += u > 0 ? 1 : pb;
            }
            lock (lk)
            {
                line += l; hit += h; bon += b; bon0 += b0; opens += o; ev += e; grand += gr; all += cnt;
                for (var i = 0; i < 16; i++) wn[i] += w[i];
            }
        });
        double sd2 = 0;
        for (var n = SlotHoldMath.HoldAt; n <= 15; n++)
            if (m.Runs[n] > 0) sd2 += wn[n] / all * (wn[n] / all) * m.Var[n] / m.Runs[n];
        return new ExactResult(line / all + ev / all, line / all, hit / all, bon / all, bon0 / all, opens / all, ev / bon,
            grand / all, Math.Sqrt(sd2));
    }

    public sealed record Sim(int N, double Rtp, double Hit, double Bonus, double BonusAvg, double Grand, double Max,
        double LineRtp, double Over100, long Capped, double Sd, double Rain, double RainOpens, double RainCoins);

    public static Sim Simulate(int n, int seed, int bet = Bet)
    {
        var m = new SlotHoldMath();
        var rng = new SeededSlotRng(new Random(seed));
        double sq = 0;
        long won = 0, lineWon = 0, hits = 0, holds = 0, holdWon = 0, grands = 0, max = 0, over100 = 0, capped = 0;
        long rains = 0, rainCoins = 0, opens = 0;
        var st = new JsonObject();
        for (var i = 0; i < n; i++)
        {
            var o = m.Spin(bet, rng, st);
            won += o.Win;
            sq += (double)o.Win / bet * o.Win / bet;
            if (o.Win > 0) hits++;
            if (o.Win > max) max = o.Win;
            if (o.Win >= 100L * bet) over100++;
            var s = o.Script;
            if (s["capped"] is not null) capped++;
            long lw = 0;
            int[]? stops = null;
            foreach (var step in s["steps"]!.AsArray())
            {
                var t = (string)step!["t"]!;
                if (t == "win") lw = (long)step["amount"]!;
                if (t == "spin") stops = [.. step["stops"]!.AsArray().Select(x => (int)x!)];
            }
            lineWon += lw;
            if (s["rain"] is { } rn) { rains++; rainCoins += (int)rn; }
            if ((bool)s["hold"]!)
            {
                holds++; holdWon += o.Win - lw;
                if (SlotHoldMath.Coins(stops!) < SlotHoldMath.HoldAt) opens++;
            }
            if ((bool)s["full"]!) grands++;
        }
        double total = (double)n * bet;
        return new Sim(n, won / total, (double)hits / n, (double)holds / n, holds > 0 ? holdWon / (double)holds / bet : 0,
            (double)grands / n, (double)max / bet, lineWon / total, (double)over100 / n, capped,
            Math.Sqrt(sq / n - won / total * (won / total)), (double)rains / n, (double)opens / n,
            rains > 0 ? (double)rainCoins / rains : 0);
    }

    public static string Line(string what, Sim s) =>
        $"{what}: RTP {s.Rtp:P2} (лінії {s.LineRtp:P2}), виграш {s.Hit:P2} (1 з {1 / s.Hit:F2}), бонус 1 з {1 / s.Bonus:F1}, " +
        $"середній бонус {s.BonusAvg:F1}×, дощ 1 з {1 / s.Rain:F1} (у середньому {s.RainCoins:F2} дуката), дощ відкрив бонус " +
        $"1 з {(s.RainOpens > 0 ? (1 / s.RainOpens).ToString("F0") : "—")}, Гетьманський 1 з {(s.Grand > 0 ? (1 / s.Grand).ToString("F0") : "—")}, " +
        $"≥100× 1 з {(s.Over100 > 0 ? (1 / s.Over100).ToString("F0") : "—")}, найбільше {s.Max:F1}×, обрізано стелею {s.Capped}, σ оберту {s.Sd:F2} ставки (σ RTP {s.Sd / Math.Sqrt(s.N):P2})";
}

/// <summary>Симуляції й перебір (spec «Козацький скарб» → таблиця). Друкують цифри: --logger "console;verbosity=detailed".</summary>
public class SlotHoldSimTests(ITestOutputHelper output)
{
    /// <summary>RTP бази 97 % (+ Скарбничка 1 % = 98 %): лінії точно + бонус перебором усіх полів з дощем (Perf нижче).</summary>
    const double Rtp = 0.970;

    [Fact]
    public void Exact_line_rtp_and_bonus_chance()
    {
        var line = HoldStats.ExactLineRtp();
        var ch = HoldStats.ExactHoldChance();
        var rain = HoldStats.HoldChanceWithRainBound();
        output.WriteLine($"точно: лінії {line:P3}, бонус без дощу 1 з {1 / ch:F2}, з дощем ≈ 1 з {1 / rain:F2}");
        Assert.InRange(line, 0.42, 0.43);
        Assert.InRange(1 / ch, 135, 150);
        Assert.InRange(1 / rain, 80, 90);
        // досяжні виплати парні: на будь-якій ставці, кратній 10, лінія платить рівно множник × ставка ÷ 20
        foreach (var (sym, pay) in SlotHoldMath.Pay)
            if (sym != SlotHoldMath.Wild) Assert.All(pay.Values, k => Assert.Equal(0, k % 2));
    }

    [Fact]
    public void Hold_model_matches_direct_runs()
    {
        // Лінійна модель (a·S + J + b, повне поле — стеля) проти прямих прогонів скрині з тими самими шістьма дукатами,
        // що в тестах нижче (S = 21, Міні 20): різниця — у межах 4σ прямих прогонів.
        var m = HoldStats.HoldModel(60_000, 5);
        List<(int, int, string, int)> six = [(0, 0, "coin", 1), (0, 1, "coin", 2), (0, 2, "coin", 3), (1, 0, "coin", 5), (1, 1, "mini", 20), (1, 2, "coin", 10)];
        var rng = new SeededSlotRng(new Random(77));
        const int runs = 60_000;
        double sum = 0, sq = 0;
        for (var i = 0; i < runs; i++)
        {
            var x = SlotHoldMath.Hold(six, 1, rng, 1000).Pay;
            sum += x; sq += (double)x * x;
        }
        var mean = sum / runs;
        var sd = Math.Sqrt((sq / runs - mean * mean) / runs);
        var model = HoldStats.Ev(m, 6, 21, 20, 1000);
        output.WriteLine($"шість дукатів: прямо {mean:F2} ± {sd:F2}, модель {model:F2} (A {m.A[6]:F3}, B {m.B[6]:F2}, F 1 з {1 / m.F[6]:F0})");
        var tol = 4 * Math.Sqrt(sd * sd + m.Var[6] / m.Runs[6]);   // σ прямих прогонів і σ моделі
        Assert.InRange(model, mean - tol, mean + tol);
    }

    [Fact]
    public void Two_hundred_thousand_spins_land_near_the_target()
    {
        // Висока волатильність: σ RTP на 200 тис. обертів ≈ 1,75 %, тож ±3,5 % (2σ) — грубий запобіжник на фіксованому
        // сіді; точність — у Perf-тестах нижче (5 млн і перебір полів).
        var s = HoldStats.Simulate(200_000, 3);
        output.WriteLine(HoldStats.Line("200 тис.", s));
        Assert.InRange(s.Rtp, Rtp - 0.035, Rtp + 0.035);
        Assert.InRange(s.LineRtp, HoldStats.ExactLineRtp() - 0.01, HoldStats.ExactLineRtp() + 0.01);
        Assert.InRange(1 / s.Bonus, 77, 92);                                     // точно 1 з 84,45; ±4σ
        Assert.InRange(s.Hit, 0.401, 0.411);                                     // точно 40,63 %; ±4σ
        Assert.InRange(s.Rain, SlotHoldMath.RainChance - 0.0025, SlotHoldMath.RainChance + 0.0025);   // ±4,6σ
        Assert.InRange(s.RainCoins, 1.33, 1.47);                                                   // 1,40 ± 4σ
        Assert.True(s.RainOpens > 0);
        Assert.True(s.Max <= 1000);
    }

    [Fact]
    public void Bet_ten_pays_exactly_a_tenth_of_bet_hundred()
    {
        // Досяжні множники парні, дукати — цілі ставки, стеля — 1000 ставок: на ставці 10 жодного округлення, той самий сід
        // дає рівно ту саму гру (RTP ставки 10 = RTP ставки 100, не > 98 %).
        var a = new SeededSlotRng(new Random(21));
        var b = new SeededSlotRng(new Random(21));
        var m = new SlotHoldMath();
        for (var i = 0; i < 30_000; i++)
        {
            var w10 = m.Spin(10, a, new JsonObject()).Win;
            var w100 = m.Spin(100, b, new JsonObject()).Win;
            Assert.Equal(w100, w10 * 10L);
        }
        foreach (var bet in SlotsOptions.DefaultBets)
            foreach (var (sym, pay) in SlotHoldMath.Pay)
                if (sym != SlotHoldMath.Wild) Assert.All(pay.Values, k => Assert.Equal(0, k * bet % 20));
    }

    [Fact, Trait("Category", "Perf")]
    public void Five_million_spins_within_half_a_percent()
    {
        // σ RTP на 5 млн ≈ 0,32 %: ±0,5 % — ≈ 1,6σ, тож це запобіжник на фіксованому сіді (сід 3 — 96,77 %), а точно —
        // перебір полів нижче.
        var s = HoldStats.Simulate(5_000_000, 3);
        output.WriteLine(HoldStats.Line("5 млн", s));
        Assert.InRange(s.Rtp, Rtp - 0.005, Rtp + 0.005);
        Assert.InRange(1 / s.Bonus, 83, 86);                                     // ±4σ
        Assert.InRange(s.Hit, 0.405, 0.4075);                                    // ±4σ
        Assert.True(s.Max <= 1000);
    }

    [Fact, Trait("Category", "Perf")]
    public void Rtp_over_every_field_with_rain()
    {
        // Усі 30⁵ полів з точною вагою (лінії, виграш, бонус з дощем — точно), скриня — моделлю з ≈ 1,7 млн прогонів
        // справжнього Hold (σ оцінки друкується; ≈ 0,05 %).
        var m = HoldStats.HoldModel(400_000, 11);
        var e = HoldStats.Exact(m);
        output.WriteLine($"перебір: RTP {e.Rtp:P3} ± {e.Sd:P3} (лінії {e.LineRtp:P3}), виграш {e.Hit:P3} (1 з {1 / e.Hit:F3}), " +
            $"бонус 1 з {1 / e.Bonus:F2} (без дощу 1 з {1 / e.BonusBase:F1}, дощ відкрив 1 з {1 / e.RainOpens:F0}), середній бонус {e.BonusAvg:F2}×, " +
            $"Гетьманський 1 з {1 / e.GrandPerSpin:F0} обертів (1 з {e.Bonus / e.GrandPerSpin:F0} бонусів)");
        for (var n = 6; n <= 9; n++) output.WriteLine($"  n={n}: A {m.A[n]:F4} B {m.B[n]:F3} D {m.D[n]:F5} F {m.F[n]:F5}");
        Assert.InRange(e.LineRtp, HoldStats.ExactLineRtp() - 1e-9, HoldStats.ExactLineRtp() + 1e-9);
        Assert.InRange(e.Rtp, Rtp - 0.002, Rtp + 0.002);
        Assert.True(e.Sd < 0.002);
        Assert.True(e.Hit >= 0.37);
        Assert.InRange(1 / e.Bonus, 80, 90);
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
    [InlineData("s1 wild s1 s2 s3", "s1", 3, 10)]
    [InlineData("cossack wild wild wild wild", "cossack", 5, 1000)]
    [InlineData("s4 wild wild wild wild", "s4", 5, 80)]
    [InlineData("pipe pipe pipe pipe s1", "pipe", 4, 60)]
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
        Assert.Equal(("s4", 5, 80), SlotHoldMath.Line(["wild", "wild", "wild", "s4", "s4"]));
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
        var big = NoRain(new ScriptRng());
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
        int holds = 0, specials = 0, rains = 0;
        for (var i = 0; i < 40_000; i++)
        {
            var o = M.Spin(Bet, rng, new JsonObject());
            var s = o.Script;
            Assert.Equal(["bet", "steps", "win", "state", "hold", "full"], s.Select(x => x.Key).Where(k => k is not ("capped" or "rain")));
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
                    case "morph": Assert.Equal(0, phase); Assert.Equal(1, steps.IndexOf(st)); rains++; Assert.Equal(["t", "cells", "why"], keys); break;
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
        Assert.InRange(holds, 380, 580);       // 1 з 84,5: ≈ 473 ± 4,5σ
        Assert.InRange(rains, 2250, 2750);     // 1 з 16: 2500 ± 5σ
        Assert.True(specials > 0);
    }


    // ---- «Дукатний дощ» ----

    /// <summary>Дощ іде: перший кидок оберту — «так», другий — скільки (x), далі для кожного — клітинка й номінал з Ints.</summary>
    static ScriptRng Rain(ScriptRng r, double count, params int[] cellAndCoin)
    {
        r.Doubles.Enqueue(0.0);
        r.Doubles.Enqueue(count);
        foreach (var x in cellAndCoin) r.Ints.Enqueue(x);
        return r;
    }

    /// <summary>Дощу нема: перший кидок оберту — «ні».</summary>
    static ScriptRng NoRain(ScriptRng r) { r.Doubles.Enqueue(0.999); return r; }

    [Fact]
    public void Rain_chance_count_and_coins_follow_the_table()
    {
        // Сам кидок дощу на порожньому полі (усі 15 клітинок вільні): частота, скільки падає, номінали — до 4σ.
        string[][] g = [.. Enumerable.Range(0, 5).Select(_ => new[] { "s1", "s2", "s3" })];
        var rng = new SeededSlotRng(new Random(4));
        const int n = 400_000;
        var counts = new int[5];
        var keys = new Dictionary<string, int>();
        var rains = 0;
        for (var i = 0; i < n; i++)
        {
            var d = SlotHoldMath.Rain(g, [], rng);
            if (d.Count == 0) continue;
            rains++;
            counts[d.Count]++;
            Assert.Equal(d.Count, d.Select(x => (x.C, x.R)).Distinct().Count());
            foreach (var x in d) keys[x.K] = keys.GetValueOrDefault(x.K) + 1;
        }
        var p = SlotHoldMath.RainChance;
        Assert.InRange(rains, n * p - 4 * Math.Sqrt(n * p * (1 - p)), n * p + 4 * Math.Sqrt(n * p * (1 - p)));
        var share = HoldStats.RainCountShare();
        for (var k = 1; k <= 4; k++)
        {
            var sd = Math.Sqrt(rains * share[k] * (1 - share[k]));
            Assert.InRange(counts[k], rains * share[k] - 4 * sd, rains * share[k] + 4 * sd);
        }
        Assert.Equal(0, counts[0]);
        var all = keys.Values.Sum();
        double tw = SlotHoldMath.RainCoins.Sum(x => x.W);
        Assert.Equal(SlotHoldMath.RainCoins.Select(x => x.K).Order(), keys.Keys.Order());
        foreach (var (k, w) in SlotHoldMath.RainCoins)
        {
            var q = w / tw;
            var sd = Math.Sqrt(all * q * (1 - q));
            Assert.InRange(keys[k], all * q - 4 * sd, all * q + 4 * sd);
        }
        // номінали дощу — як на стрічках: стільки ваги, скільки таких дукатів на п'яти барабанах
        foreach (var (k, w) in SlotHoldMath.RainCoins) Assert.Equal(SlotHoldMath.Reels.Sum(r => r.Count(x => x == k)), w);
    }

    [Fact]
    public void Rain_lands_only_on_free_cells_and_never_changes_the_lines()
    {
        // Сценарій програється як клієнт: поле зі стрічок за stops → morph дощу → лінії й дукати рахуються на ньому.
        var rng = new SeededSlotRng(new Random(12));
        int rains = 0, opened = 0, withLines = 0;
        for (var i = 0; i < 60_000; i++)
        {
            var o = M.Spin(Bet, rng, new JsonObject());
            var steps = Steps(o);
            int[] stops = [.. steps[0]["stops"]!.AsArray().Select(x => (int)x!)];
            var before = SlotHoldMath.Grid(stops);
            var g = SlotHoldMath.Grid(stops);
            var morph = steps.Where(x => (string)x["t"]! == "morph").ToList();
            var lines = SlotHoldMath.Wins(before);
            if (morph.Count == 1)
            {
                rains++;
                Assert.Equal(1, steps.IndexOf(morph[0]));                         // одразу після spin, до win
                Assert.Equal("rain", (string)morph[0]["why"]!);
                var cells = morph[0]["cells"]!.AsArray();
                Assert.InRange(cells.Count, 1, 4);
                Assert.Equal(cells.Count, (int)o.Script["rain"]!);
                Assert.Contains("rain", o.Flags);
                var free = SlotHoldMath.RainCells(before, lines);
                foreach (var cell in cells)
                {
                    var a = cell!.AsArray();
                    int c = (int)a[0]!, r = (int)a[1]!;
                    var k = (string)a[2]!;
                    Assert.Contains((c, r), free);                                   // не дукат і не виграшний ряд
                    Assert.False(SlotHoldMath.IsCoin(g[c][r]));
                    Assert.True(SlotHoldMath.IsCoin(k));
                    Assert.Equal(SlotHoldMath.CoinValue[k], (int)a[3]!);
                    g[c][r] = k;
                }
                if (lines.Count > 0) withLines++;
                if (SlotHoldMath.Coins(before) < SlotHoldMath.HoldAt && SlotHoldMath.Coins(g) >= SlotHoldMath.HoldAt) opened++;
            }
            else
            {
                Assert.Empty(morph);
                Assert.Null(o.Script["rain"]);
            }
            // лінії на полі після дощу — ті самі, що на барабанах, і рівно ті, що в кроці win
            Assert.Equal(lines, SlotHoldMath.Wins(g));
            var win = steps.SingleOrDefault(x => (string)x["t"]! == "win");
            Assert.Equal(lines.Count > 0, win is not null);
            if (win is not null)
            {
                var items = win["items"]!.AsArray();
                Assert.Equal(lines.Select(x => x.Line), items.Select(x => (int)x!["line"]!));
                Assert.Equal(lines.Select(x => x.Sym), items.Select(x => (string)x!["sym"]!));
                Assert.Equal(SlotHoldMath.Units(before) * Bet / 20, (long)win["amount"]!);
            }
            // бонус — на полі після дощу: усі його дукати, у тому ж порядку
            var holdIn = steps.SingleOrDefault(x => (string)x["t"]! == "holdIn");
            var coins = new List<string>();
            for (var c = 0; c < 5; c++)
                for (var r = 0; r < 3; r++)
                    if (SlotHoldMath.IsCoin(g[c][r])) coins.Add($"[{c},{r},\"{SlotHoldMath.CoinKind(g[c][r])}\",{SlotHoldMath.CoinValue[g[c][r]]}]");
            Assert.Equal(coins.Count >= SlotHoldMath.HoldAt, holdIn is not null);
            if (holdIn is not null) Assert.Equal(coins, holdIn["coins"]!.AsArray().Select(x => x!.ToJsonString()));
            Assert.True(o.Win >= SlotHoldMath.Units(before) * Bet / 20);              // дощ виграшу не зменшує
        }
        Assert.InRange(rains, 3400, 4100);                                          // 1 з 16: 3750 ± 5σ
        Assert.True(opened > 0);
        Assert.True(withLines > 0);
    }

    [Fact]
    public void Rain_can_open_hold_and_still_reach_the_hetman_treasure()
    {
        // П'ять дукатів на барабанах, дощ дає шостий (перша вільна клітинка, номінал — перший у RainCoins) → бонус;
        // далі всі дев'ять гнізд — Мажори: повне поле, оберт — рівно стеля.
        var s = StopsWhere(x => SlotHoldMath.Coins(x) == 5);
        var g = SlotHoldMath.Grid(s);
        var (c0, r0) = SlotHoldMath.RainCells(g, SlotHoldMath.Wins(g))[0];
        var rng = Rain(new ScriptRng(), 0.5, 0, 0);                                  // 1 дукат
        var o = M.ScriptFor(s, Bet, new JsonObject(), rng);
        var steps = Steps(o);
        Assert.Equal(["spin", "morph"], Types(steps).Take(2));
        var k = SlotHoldMath.RainCoins[0].K;
        Assert.Equal($"[[{c0},{r0},\"{k}\",{SlotHoldMath.CoinValue[k]}]]", steps[1]["cells"]!.ToJsonString());
        Assert.True((bool)o.Script["hold"]!);
        var holdIn = steps.Single(x => (string)x["t"]! == "holdIn");
        Assert.Equal(6, holdIn["coins"]!.AsArray().Count);
        Assert.Contains($"[{c0},{r0},\"{SlotHoldMath.CoinKind(k)}\",{SlotHoldMath.CoinValue[k]}]", holdIn["coins"]!.ToJsonString());
        Assert.Equal(Enumerable.Range(0, 5).Where(c => CoinsBefore(s, c) == 5), steps[0]["tease"]!.AsArray().Select(x => (int)x!));

        var big = Rain(new ScriptRng(), 0.5, 0, 0);
        for (var i = 0; i < 9; i++) Land(big, 0.125);
        var full = M.ScriptFor(s, Bet, new JsonObject(), big);
        Assert.True((bool)full.Script["full"]!);
        Assert.Equal(1000 * Bet, full.Win);
        Assert.Contains("grand", full.Flags);
        Assert.Contains("rain", full.Flags);

        // дощ 4 дукати, а вільних клітинок менше — падає скільки влізло; поле без вільних — дощу нема
        string[][] packed = [.. Enumerable.Range(0, 5).Select(_ => new[] { "c1", "c2", "c3" })];
        packed[4][2] = "s1";
        var d = SlotHoldMath.Rain(packed, [], Rain(new ScriptRng(), 0.99, 0, 0, 0, 0));
        Assert.Equal([(4, 2, SlotHoldMath.RainCoins[0].K)], d);
        packed[4][2] = "c5";
        Assert.Empty(SlotHoldMath.Rain(packed, [], Rain(new ScriptRng(), 0.99)));
    }

    [Fact]
    public void Rain_keeps_off_winning_rows()
    {
        // Поле з виграшем у верхньому ряду: дощ на 4 дукати не падає на клітинки ряду, навіть коли вільних мало.
        var s = StopsWhere(x => SlotHoldMath.Units(x) > 0 && SlotHoldMath.Coins(x) <= 3, 3);
        var g = SlotHoldMath.Grid(s);
        var wins = SlotHoldMath.Wins(g);
        var busy = wins.SelectMany(w => Enumerable.Range(0, w.N).Select(c => (c, SlotHoldMath.PayLines[w.Line][c]))).ToHashSet();
        Assert.NotEmpty(busy);
        for (var seed = 0; seed < 300; seed++)
        {
            var r = new ScriptRng();
            r.Doubles.Enqueue(0.0); r.Doubles.Enqueue(0.99);
            var rnd = new Random(seed);
            for (var i = 0; i < 8; i++) r.Ints.Enqueue(rnd.Next(1000));
            var d = SlotHoldMath.Rain(g, wins, r);
            Assert.Equal(Math.Min(4, SlotHoldMath.RainCells(g, wins).Count), d.Count);
            Assert.All(d, x => Assert.DoesNotContain((x.C, x.R), busy));
            Assert.All(d, x => Assert.False(SlotHoldMath.IsCoin(g[x.C][x.R])));
        }
    }

    [Fact]
    public void Client_reels_are_the_server_reels()
    {
        // Сервер шле лише зупинки, клієнт крутить свої стрічки: REELS у slot-hold.js мусять збігатися з SlotHoldMath.Reels.
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "web", "games"))) dir = Path.GetDirectoryName(dir);
        var js = File.ReadAllText(Path.Combine(dir!, "web", "games", "slots", "slot-hold.js"));
        var block = js[js.IndexOf("const REELS = [", StringComparison.Ordinal)..];
        block = block[..block.IndexOf("].map", StringComparison.Ordinal)];
        var strips = System.Text.RegularExpressions.Regex.Matches(block, "'([^']+)'").Select(x => x.Groups[1].Value).ToArray();
        Assert.Equal(SlotHoldMath.Reels.Select(r => string.Join(' ', r)), strips);
    }

    [Fact]
    public void Table_tells_the_rain_and_the_respins()
    {
        var t = M.Table();
        var rain = t["rain"]!.AsObject();
        Assert.Equal(SlotHoldMath.RainChance, (double)rain["chance"]!);
        Assert.Equal(1.0, rain["counts"]!.AsObject().Sum(x => (double)x.Value!), 6);
        Assert.Equal(["1", "2", "3", "4"], rain["counts"]!.AsObject().Select(x => x.Key));
        Assert.Equal(SlotHoldMath.RainCoins.Select(x => x.K), rain["coins"]!.AsObject().Select(x => x.Key));
        Assert.Contains("1 з 16", (string)rain["text"]!);
        var rs = t["respin"]!.AsObject();
        Assert.Equal(SlotHoldMath.PLand, (double)rs["land"]!);
        var parts = (double)rs["mace"]! + (double)rs["pirnach"]! + (double)rs["mini"]! + (double)rs["major"]! + rs["coins"]!.AsObject().Sum(x => (double)x.Value!);
        Assert.Equal(1.0, parts, 6);
        Assert.Equal(SlotHoldMath.Reels.Length, t["reels"]!.AsArray().Count);
        Assert.Equal(SlotHoldMath.Reels[2], t["reels"]![2]!.AsArray().Select(x => (string)x!));
        Assert.Equal(1000, (int)t["pay"]!["cossack"]!["5"]!);
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
        k.G.Rng = Land(NoRain(new ScriptRng().Stops(s)), 0.125);     // без дощу; Мажор у першому порожньому гнізді
        Assert.True(k.Spin(20).Ok);
        var sc = k.View.GetProperty("last").GetProperty("script");
        Assert.True(sc.GetProperty("hold").GetBoolean());
        var win = sc.GetProperty("win").GetInt32();
        Assert.True(win >= 100 * 20);
        Assert.Equal(100_000 - 20 + win, k.Stakes.Balance("Оля"));
        Assert.Single(k.Stakes.Ledger, m => m.Ref.EndsWith(":win", StringComparison.Ordinal));
    }
}
