using Hlechyky.Games.Impl;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Математика «Гончарного колеса» (specs/kolo.md §2): точно з розкладки й симуляцією мільйонів обертів через той самий
/// шлях, що й на проді (seed → сегмент → повернення). Цифри друкуються в spec
/// (dotnet test --filter KoloSimTests --logger "console;verbosity=detailed").
/// </summary>
public class KoloSimTests(ITestOutputHelper output)
{
    const int Spins = 5_000_000;

    [Fact]
    public void Exact_rtp_from_the_layout_is_96_percent_for_every_pick()
    {
        Assert.Equal(125, KoloCore.Size);
        foreach (var (x, n) in KoloCore.Counts) Assert.Equal(n, KoloCore.Wheel.Count(w => w == x));
        foreach (var pick in KoloCore.Picks)
        {
            var rtp = (double)pick * KoloCore.Counts[pick] / KoloCore.Size;
            output.WriteLine($"×{pick}: {KoloCore.Counts[pick]} сегм., шанс {KoloCore.Counts[pick] / (double)KoloCore.Size:P1}, RTP {rtp:P2}");
            Assert.Equal(0.96, rtp, 12);
            Assert.Equal(rtp, KoloCore.Rtp(pick), 12);
        }
        // «Тріснув!» — 1 сегмент з 125: решта RTP до 100 % — саме ця 0,8 % і ще 3,2 % «не той множник»
        Assert.Equal(1, KoloCore.Counts[KoloCore.Crack]);
        Assert.Equal(0.008, KoloCore.Counts[KoloCore.Crack] / (double)KoloCore.Size, 12);
        // ставка на всі чотири однаковими сумами: повертається 96 % (кожна — окремо 96 %)
        long back = 0;
        foreach (var x in KoloCore.Wheel) back += KoloCore.Return(KoloCore.Picks.Select(p => (p, 100)), x);
        Assert.Equal(0.96, back / (400.0 * KoloCore.Size), 12);
    }

    [Fact]
    [Trait("Category", "Sim")]
    public void Five_million_spins_show_every_pick_near_96_and_never_above_97()
    {
        var rng = new Random(2026);
        var count = new Dictionary<int, long>();
        foreach (var x in KoloCore.Counts.Keys) count[x] = 0;
        var segHits = new long[KoloCore.Size];
        for (var i = 0; i < Spins; i++)
        {
            var seg = KoloCore.Seg(KoloCore.NewSeed(rng));
            segHits[seg]++;
            count[KoloCore.X(seg)]++;
        }
        var rtps = new List<double>();
        foreach (var pick in KoloCore.Picks)
        {
            // ставка 100 на один множник щоразу: повернення = 100·pick, коли випав він
            var paid = count[pick] * 100L * pick;
            var rtp = paid / (100.0 * Spins);
            var p = KoloCore.Counts[pick] / (double)KoloCore.Size;
            var sigma = pick * Math.Sqrt(p * (1 - p) / Spins);
            output.WriteLine($"×{pick}: випало {count[pick]} ({count[pick] / (double)Spins:P3}, очікувано {p:P3}), "
                + $"RTP {rtp:P3} (±{4 * sigma:P3}), частота виграшу 1 з {Spins / (double)count[pick]:F1}");
            Assert.InRange(rtp, 0.96 - 4 * sigma, 0.96 + 4 * sigma);
            Assert.True(rtp <= 0.97, $"×{pick}: RTP {rtp:P3} вище за 97 %");
            rtps.Add(rtp);
        }
        output.WriteLine($"Тріснув!: {count[KoloCore.Crack]} ({count[KoloCore.Crack] / (double)Spins:P3}, очікувано 0,800 %)");
        output.WriteLine($"розкид RTP між множниками: {rtps.Max() - rtps.Min():P3}");
        Assert.InRange(count[KoloCore.Crack] / (double)Spins, 0.008 - 0.0004, 0.008 + 0.0004);
        Assert.True(rtps.Max() - rtps.Min() < 0.01);
        // кожен із 125 сегментів — рівноймовірний: 40 000 ± 5σ (σ ≈ 199)
        var e = Spins / (double)KoloCore.Size;
        Assert.All(segHits, h => Assert.InRange(h, e - 1000, e + 1000));
        output.WriteLine($"сегменти: найменше {segHits.Min()}, найбільше {segHits.Max()} (очікувано {e:F0})");
    }

    [Fact]
    [Trait("Category", "Sim")]
    public void Mixed_bettors_get_96_percent_and_the_ceiling_is_30x_the_max_bet()
    {
        // гравець ставить на 1–4 випадкові множники випадкові суми від 10 до 2000 (межа конфігу): RTP, частота, найбільше
        var rng = new Random(77);
        var opts = new KoloOptions();
        const int rounds = 2_000_000;
        long staked = 0, paid = 0, wins = 0, maxWin = 0;
        for (var i = 0; i < rounds; i++)
        {
            var x = KoloCore.X(KoloCore.Seg(KoloCore.NewSeed(rng)));
            var picks = KoloCore.Picks.Where(_ => rng.Next(2) == 0).ToList();
            if (picks.Count == 0) picks.Add(KoloCore.Picks[rng.Next(4)]);
            var stakes = picks.Select(p => (p, rng.Next(opts.MinBet, opts.MaxBet + 1))).ToList();
            var s = stakes.Sum(z => (long)z.Item2);
            var r = KoloCore.Return(stakes, x);
            staked += s;
            paid += r;
            if (r > s) wins++;
            maxWin = Math.Max(maxWin, r);
        }
        var rtp = paid / (double)staked;
        output.WriteLine($"змішані ставки: RTP {rtp:P3}, у плюсі {wins / (double)rounds:P2} раундів, найбільше повернення {maxWin} 🏺");
        Assert.InRange(rtp, 0.95, 0.97);
        // стеля: ×30 на найбільшій ставці одного множника — 60 000, і вона влазить в int із запасом
        Assert.True(maxWin <= (long)opts.MaxBet * KoloCore.Top);
        Assert.True((long)KoloCore.MaxPerPick * KoloCore.Top <= int.MaxValue);
    }
}
