using System.Security.Cryptography;
using System.Text;
using Hlechyky.Games.Impl;

namespace Hlechyky.Tests.Games;

/// <summary>Математика Горохової дошки: таблиці, точне повернення, шлях із seed, розподіл по глечиках.</summary>
public class PlinkoCoreTests
{
    public static TheoryData<string, int> Boards()
    {
        var d = new TheoryData<string, int>();
        foreach (var r in PlinkoCore.Risks)
            for (var n = PlinkoCore.MinRows; n <= PlinkoCore.MaxRows; n++) d.Add(r, n);
        return d;
    }

    [Theory]
    [MemberData(nameof(Boards))]
    public void Every_table_returns_98_percent(string risk, int rows)
    {
        // На 8 рядах найдрібніший крок таблиці зсуває повернення на 0,078 п.п. — ближче за 97,97 % не стати.
        var rtp = PlinkoCore.Rtp(rows, risk);
        Assert.InRange(rtp, 0.9795, 0.9805);
    }

    [Theory]
    [MemberData(nameof(Boards))]
    public void Tables_are_symmetric_growing_to_the_edge_and_pay_whole_shards(string risk, int rows)
    {
        var t = PlinkoCore.Table(rows, risk);
        Assert.Equal(rows + 1, t.Count);
        for (var k = 0; k <= rows; k++) Assert.Equal(t[k], t[rows - k]);
        for (var k = 0; k < rows / 2; k++) Assert.True(t[k] >= t[k + 1], $"{risk}/{rows}: глечик {k} менший за сусіда ближче до центру");
        foreach (var x in t)
        {
            Assert.True(x >= 1, "множник щонайменше ×0,1");
            // крок: 0,1 до ×10, 1 до ×100, 5 до ×300, далі 10
            var step = x < 100 ? 1 : x < 1000 ? 10 : x < 3000 ? 50 : 100;
            Assert.True(x % step == 0, $"{risk}/{rows}: ×{PlinkoCore.Fmt(x)} не на кроці");
            // будь-яка дозволена ставка (кратна 10) × множник — цілий черепок
            Assert.Equal(10L * x, 10L * PlinkoCore.Win(10, x));
        }
    }

    [Fact]
    public void Edges_are_the_promised_ones_and_low_risk_mostly_returns_the_bet()
    {
        Assert.Equal(150, PlinkoCore.Table(16, PlinkoCore.Low)[0]);
        Assert.Equal(1000, PlinkoCore.Table(16, PlinkoCore.Mid)[0]);
        Assert.Equal(10_000, PlinkoCore.Table(16, PlinkoCore.High)[16]);
        Assert.Equal(10_000, PlinkoCore.TopX);
        Assert.Equal(300, PlinkoCore.Table(8, PlinkoCore.High)[8]);
        for (var n = PlinkoCore.MinRows; n <= PlinkoCore.MaxRows; n++)
        {
            // на парній дошці програє лише центр; на непарній — два середні глечики, тож трохи частіше
            Assert.True(PlinkoCore.HitRate(n, PlinkoCore.Low) >= (n % 2 == 0 ? 0.7 : 0.5), $"низький/{n}");
            Assert.True(PlinkoCore.HitRate(n, PlinkoCore.High) < PlinkoCore.HitRate(n, PlinkoCore.Low), $"високий/{n}");
        }
    }

    [Fact]
    public void Unknown_boards_throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PlinkoCore.Table(7, PlinkoCore.Low));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlinkoCore.Table(17, PlinkoCore.Low));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlinkoCore.Table(12, "wild"));
        Assert.False(PlinkoCore.IsRisk(null));
    }

    [Fact]
    public void Path_is_the_bits_of_sha256_of_seed_and_number()
    {
        // золотий вектор: перевіряє й браузерна «ⓘ» (web/games/plinko.js, qa.path)
        var seed = new string('0', 64);
        Assert.Equal("60e05bd1b195af2f94112fa7197a5c88289058840ce7c6df9693756bc6250f55", PlinkoCore.Hash(seed));
        Assert.Equal("0000000000010100", PlinkoCore.Path(seed, 1, 16));
        Assert.Equal("1100100010011110", PlinkoCore.Path(seed, 2, 16));
        Assert.Equal(2, PlinkoCore.Slot("0000000000010100"));
        // біти не залежать від рядів: 8 рядів — початок тих самих 16
        Assert.Equal("11001000", PlinkoCore.Path(seed, 2, 8));
        // і рахуються так, як написано в документації
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(seed + ":2"));
        Assert.Equal(Convert.ToString(h[0], 2).PadLeft(8, '0') + Convert.ToString(h[1], 2).PadLeft(8, '0'), PlinkoCore.Path(seed, 2, 16));
    }

    [Fact]
    public void Drop_gives_each_ball_its_path_slot_multiplier_and_win()
    {
        var seed = PlinkoCore.NewSeed(new Random(5));
        var balls = PlinkoCore.Drop(seed, 10, 12, PlinkoCore.Mid, 50);
        Assert.Equal(10, balls.Count);
        var t = PlinkoCore.Table(12, PlinkoCore.Mid);
        for (var i = 0; i < balls.Count; i++)
        {
            var b = balls[i];
            Assert.Equal(PlinkoCore.Path(seed, i + 1, 12), b.P);
            Assert.Equal(PlinkoCore.Slot(b.P), b.S);
            Assert.Equal(t[b.S], b.X);
            Assert.Equal(50 * t[b.S] / 10, b.Win);
        }
        Assert.Equal(balls, PlinkoCore.Drop(seed, 10, 12, PlinkoCore.Mid, 50));   // той самий seed — ті самі горошини
    }

    [Fact]
    public void Balls_fall_into_pots_as_the_binomial_says()
    {
        // 16 рядів, 120 000 горошин із незалежних seed: хі-квадрат проти C(16, k) / 2¹⁶ (краї злито — там очікувано мало)
        const int n = 16, total = 120_000;
        var rng = new Random(2026);
        var counts = new int[n + 1];
        for (var i = 0; i < total; i++) counts[PlinkoCore.Slot(PlinkoCore.Path(PlinkoCore.NewSeed(rng), 1 + (i & 7), n))]++;
        var bins = new List<(double Exp, int Got)>();
        double e = 0;
        var g = 0;
        for (var k = 0; k <= n; k++)
        {
            e += total * (double)PlinkoCore.Choose(n, k) / (1 << n);
            g += counts[k];
            if (e >= 50 || k == n)
            {
                bins.Add((e, g));
                e = 0;
                g = 0;
            }
        }
        var chi = bins.Sum(b => (b.Got - b.Exp) * (b.Got - b.Exp) / b.Exp);
        // 14 кошиків — 13 ступенів свободи: 0,1 % хвіст — 34,5
        Assert.True(chi < 34.5,$"χ² = {chi:F1} на {bins.Count - 1} ступенях — горошини падають не за біноміальним розподілом");
    }

    [Theory]
    [InlineData(PlinkoCore.Low, 12)]
    [InlineData(PlinkoCore.Low, 9)]
    [InlineData(PlinkoCore.Mid, 10)]
    public void Simulated_return_matches_the_exact_one(string risk, int rows)
    {
        // справжній Drop зі ставкою 10: середнє повернення горошини збігається з точною формулою (σ ≈ 0,1–0,4 п.п.)
        var rng = new Random(rows * 7 + risk.Length);
        long staked = 0, won = 0;
        for (var i = 0; i < 25_000; i++)
            foreach (var b in PlinkoCore.Drop(PlinkoCore.NewSeed(rng), 10, rows, risk, 10))
            {
                staked += 10;
                won += b.Win;
            }
        Assert.InRange((double)won / staked, PlinkoCore.Rtp(rows, risk) - 0.02, PlinkoCore.Rtp(rows, risk) + 0.02);
    }

    [Fact]
    public void Formatting_is_ukrainian()
    {
        Assert.Equal("1,5", PlinkoCore.Fmt(15));
        Assert.Equal("0,2", PlinkoCore.Fmt(2));
        Assert.Equal("1000", PlinkoCore.Fmt(10_000));
    }
}
