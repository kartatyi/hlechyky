using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hlechyky.Games.Impl;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Математика «Кавунів на ярмарку» (specs/kavuny.md §3): точний RTP перебором станів, симуляція 10+ млн раундів за ідеальної
/// гри (ріже все, забирає на цілі), розподіл довжини, стеля; чесність seed → послідовність. Цифри — у spec §3.4; тест їх друкує
/// (dotnet test --filter KavunyCore --logger "console;verbosity=detailed").
/// </summary>
public class KavunyCoreTests(ITestOutputHelper output)
{
    static readonly int[] Targets = [101, 110, 120, 150, 200, 300, 500, 1000, 2000, 5000, 10_000];

    /// <summary>
    /// Точно (динамікою по сотих): ідеальний різник із автозабором на <paramref name="target"/> при стелі <paramref name="cap"/>.
    /// Повертає RTP множником (без округлення черепків) і ймовірність забрати.
    /// </summary>
    static (double Rtp, double Hit) Exact(int target, int cap)
    {
        var top = cap + 10 * 100 + 1;
        var alive = new double[top + 1];
        alive[100] = 1;
        double rtp = 0, hit = 0;
        for (var p = 100; p <= top; p++)
        {
            var a = alive[p];
            if (a == 0) continue;
            if (p >= target || p >= cap)
            {
                rtp += a * Math.Min(p, cap) / 100.0;
                hit += a;
                continue;
            }
            var f = KavunyCore.Factor(p);
            var live = (double)KavunyCore.Q * p / ((double)KavunyCore.Q * p + f * KavunyCore.S);
            if (p == 100) live *= KavunyCore.Rtp / 100.0;
            foreach (var k in KavunyCore.Kinds) alive[p + f * k.Inc] += a * live * k.Weight / KavunyCore.Q;
        }
        return (rtp, hit);
    }

    /// <summary>Швидкий генератор 32-бітних кидків для симуляції (SplitMix64, старші біти): той самий Step, що й у грі, лише без sha256.</summary>
    sealed class Bits(ulong seed)
    {
        ulong _s = seed;
        public uint Next()
        {
            var z = _s += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return (uint)((z ^ (z >> 31)) >> 32);
        }
    }

    sealed record Sim(long Rounds, double[] Rtp, double[] Hit, double[] Money, long[] Len, double MeanLen, int MaxLen, int MaxP,
        long Capped, long FirstRot, long Glek);

    /// <summary>
    /// <paramref name="rounds"/> раундів: ідеальний різник ріже все й забирає на кожній цілі з <see cref="Targets"/> (одним
    /// проходом — ціль, якої досягли, фіксує свій виграш; гнилий забирає решту). Гроші — при найменшій ставці 10 з округленням донизу.
    /// </summary>
    static Sim Run(long rounds, int cap, ulong seed)
    {
        var bits = new Bits(seed);
        var n = Targets.Length;
        var got = new double[n];
        var hits = new long[n];
        var money = new long[n];
        var len = new long[8];
        long lenSum = 0, capped = 0, firstRot = 0, glek = 0;
        int maxLen = 0, maxP = 0;
        for (long r = 0; r < rounds; r++)
        {
            int p = 100, next = 0, fruits = 0;
            var first = true;
            while (true)
            {
                var (rot, kind, inc) = KavunyCore.Step(p, bits.Next(), bits.Next(), first ? bits.Next() : 0u, first);
                if (rot)
                {
                    if (first) firstRot++;
                    break;
                }
                first = false;
                fruits++;
                if (kind == KavunyCore.Kinds.Count - 1) glek++;
                p += inc;
                var pay = Math.Min(p, cap);
                while (next < n && (p >= Targets[next] || p >= cap))
                {
                    if (Targets[next] <= cap || p >= cap)
                    {
                        got[next] += pay / 100.0;
                        hits[next]++;
                        money[next] += KavunyCore.Win(10, pay);
                    }
                    next++;
                }
                if (p >= cap)
                {
                    capped++;
                    break;
                }
                if (next == n) break;
            }
            // Довжина й найбільший множник — для «ніколи не забирав»: рахуємо, доки не скінчились цілі; хвіст за останньою
            // ціллю (стеля) — уже сама стеля, тож довжина повна.
            lenSum += fruits;
            maxLen = Math.Max(maxLen, fruits);
            maxP = Math.Max(maxP, p);
            len[fruits == 0 ? 0 : fruits < 5 ? 1 : fruits < 10 ? 2 : fruits < 20 ? 3 : fruits < 50 ? 4 : fruits < 100 ? 5 : fruits < 200 ? 6 : 7]++;
        }
        return new Sim(rounds, [.. got.Select(g => g / rounds)], [.. hits.Select(h => (double)h / rounds)],
            [.. money.Select(m => m / (10.0 * rounds))], len, (double)lenSum / rounds, maxLen, maxP, capped, firstRot, glek);
    }

    void Print(Sim s, int cap)
    {
        output.WriteLine($"Раундів: {s.Rounds:N0}, стеля ×{cap / 100}");
        output.WriteLine("ціль      | RTP (множник) | RTP (ставка 10) | забрав    | точно");
        for (var i = 0; i < Targets.Length; i++)
        {
            if (Targets[i] > cap) continue;
            var ex = Exact(Targets[i], cap);
            output.WriteLine($"×{KavunyCore.Fmt(Targets[i]),-8} | {s.Rtp[i],12:P3} | {s.Money[i],15:P3} | {s.Hit[i],8:P3} | {ex.Rtp:P3} / {ex.Hit:P3}");
        }
        string[] names = ["0 (гнилий перший)", "1–4", "5–9", "10–19", "20–49", "50–99", "100–199", "200+"];
        output.WriteLine("Скільки цілих овочів до кінця (гнилий чи стеля):");
        for (var i = 0; i < names.Length; i++) output.WriteLine($"  {names[i],-18} {(double)s.Len[i] / s.Rounds:P3}");
        output.WriteLine($"Середня довжина {s.MeanLen:F2}, найдовша {s.MaxLen}, найбільший множник ×{KavunyCore.Fmt(s.MaxP)}, " +
            $"віз порожній (стеля) {(double)s.Capped / s.Rounds:P3}, гнилий першим {(double)s.FirstRot / s.Rounds:P3}, " +
            $"Глеків кавун на раунд {(double)s.Glek / s.Rounds:F4}");
    }

    [Fact]
    public void Table_weights_sum_to_q_and_codes_are_unique()
    {
        Assert.Equal(KavunyCore.Q, KavunyCore.Kinds.Sum(k => k.Weight));
        Assert.Equal(6850, KavunyCore.S);
        Assert.Equal(KavunyCore.Kinds.Count, KavunyCore.Kinds.Select(k => k.Code).Distinct().Count());
        Assert.DoesNotContain(KavunyCore.Kinds, k => k.Code == KavunyCore.Rot);
    }

    [Fact]
    public void Exact_rtp_is_97_percent_for_every_target_under_the_default_cap()
    {
        foreach (var t in Targets)
        {
            var (rtp, hit) = Exact(t, KavunyCore.DefaultCap);
            output.WriteLine($"×{KavunyCore.Fmt(t)}: RTP {rtp:P4}, забрати {hit:P3}");
            // нижче стелі — рівно 97 % (мартингал); на самій стелі трохи менше: що понад стелю, не платиться
            if (t < KavunyCore.DefaultCap) Assert.InRange(rtp, 0.97 - 1e-9, 0.97 + 1e-9);
            else Assert.InRange(rtp, 0.95, 0.97);
        }
        // «Ніколи не забирати» = ціль на стелі: теж ≤ 97 %.
        Assert.InRange(Exact(int.MaxValue / 2, KavunyCore.DefaultCap).Rtp, 0.9, 0.97 + 1e-9);
        // і за найвищої дозволеної стелі
        Assert.InRange(Exact(200, KavunyCore.MaxCap).Rtp, 0.965, 0.97 + 1e-9);
    }

    [Fact]
    public void First_fruit_is_rotten_about_9_percent_of_rounds()
    {
        var expect = 1 - 0.97 * KavunyCore.Q * 100.0 / (KavunyCore.Q * 100.0 + KavunyCore.S);
        var s = Run(400_000, KavunyCore.DefaultCap, 5);
        Assert.InRange((double)s.FirstRot / s.Rounds, expect - 0.003, expect + 0.003);
    }

    [Fact]
    public void Half_a_million_rounds_land_near_the_exact_rtp()
    {
        var s = Run(500_000, KavunyCore.DefaultCap, 1);
        Print(s, KavunyCore.DefaultCap);
        for (var i = 0; i < Targets.Length; i++)
        {
            var ex = Exact(Targets[i], KavunyCore.DefaultCap);
            var tol = 0.97 * 4 * Math.Sqrt(Math.Max(1, Targets[i] / 100.0) / s.Rounds) + 0.004;
            Assert.InRange(s.Rtp[i], ex.Rtp - tol, ex.Rtp + tol);
        }
    }

    [Fact, Trait("Category", "Perf")]
    public void Ten_million_rounds_of_perfect_play_return_at_most_97_percent()
    {
        var sw = Stopwatch.StartNew();
        var cap = new KavunyOptions().MaxX * 100;
        var s = Run(10_000_000, cap, 2);
        output.WriteLine($"за {sw.Elapsed.TotalSeconds:F1} с");
        Print(s, cap);
        for (var i = 0; i < Targets.Length; i++)
        {
            if (Targets[i] > cap) continue;
            var ex = Exact(Targets[i], cap);
            Assert.True(ex.Rtp <= 0.97 + 1e-9, $"×{KavunyCore.Fmt(Targets[i])}: точний RTP {ex.Rtp:P4}");
            // 4σ: дисперсія виграшу на цілі X ≈ 0,97·X
            var tol = 4 * Math.Sqrt(0.97 * Targets[i] / 100.0 / s.Rounds) + 0.0005;
            Assert.InRange(s.Rtp[i], ex.Rtp - tol, ex.Rtp + tol);
            Assert.True(s.Money[i] <= s.Rtp[i] + 1e-12);
        }
        Assert.True(s.MaxP < cap + 10 * 100 + 1, "множник «ідеального різника» не тікає далеко за стелю");
        Assert.InRange((double)s.Capped / s.Rounds, 0.97 / (cap / 100.0) * 0.9, 0.97 / (cap / 100.0) * 1.1);
    }

    [Fact]
    public void Sequence_follows_from_the_seed_alone()
    {
        var seed = new string('7', 64);
        var a = KavunyCore.Generate(seed, 10_000);
        var b = KavunyCore.Generate(seed, 10_000);
        Assert.Equal(JsonSerializer.Serialize(a), JsonSerializer.Serialize(b));
        var c = KavunyCore.Generate(new string('8', 64), 10_000);
        Assert.NotEqual(a.Codes, c.Codes);
    }

    [Fact]
    public void Sequence_matches_a_hand_rolled_replay_of_the_formula()
    {
        var rng = new Random(42);
        for (var round = 0; round < 300; round++)
        {
            var seed = KavunyCore.NewSeed(rng);
            var seq = KavunyCore.Generate(seed, 10_000);
            var p = 100;
            for (var i = 0; i < seq.Fruits.Count; i++)
            {
                var f = seq.Fruits[i];
                Assert.Equal(i + 1, f.Id);
                var hex = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{f.Id}")));
                var roll = Convert.ToUInt32(hex[..8], 16);
                var kind = Convert.ToUInt32(hex[8..16], 16);
                var edge = Convert.ToUInt32(hex[16..24], 16);
                var factor = p < 300 ? 1 : p < 1000 ? 2 : p < 3000 ? 5 : 10;
                var rot = (f.Id == 1 && (System.Numerics.BigInteger)edge * 100 < (System.Numerics.BigInteger)3 << 32)
                    || (System.Numerics.BigInteger)roll * (1000L * p + factor * 6850) >= ((System.Numerics.BigInteger)p * 1000 << 32);
                Assert.Equal(rot, f.Rotten);
                if (rot)
                {
                    Assert.Equal(seq.Fruits.Count - 1, i);
                    break;
                }
                var pick = (int)((kind * 1000UL) >> 32);
                var at = 0;
                while (pick >= KavunyCore.Kinds[at].Weight) pick -= KavunyCore.Kinds[at++].Weight;
                Assert.Equal(KavunyCore.Kinds[at].Code, f.Code);
                Assert.Equal(factor * KavunyCore.Kinds[at].Inc, f.Inc);
                p += f.Inc;
            }
            Assert.Equal(p, seq.Potential);
            Assert.Equal(seq.Rotten, seq.Fruits[^1].Rotten);
            if (!seq.Rotten) Assert.True(p >= 10_000);
        }
    }

    [Fact]
    public void Schedule_is_waves_of_growing_size()
    {
        var seq = KavunyCore.Generate(Enumerable.Range(0, 2000).Select(i => KavunyCore.NewSeed(new Random(i)))
            .First(s => KavunyCore.Generate(s, 10_000).Fruits.Count > 60), 10_000);
        var f = seq.Fruits;
        Assert.Equal(0, f[0].At);
        Assert.Equal(KavunyCore.WaveMs, f[1].At);          // кидок 1 — один овоч
        Assert.Equal(2 * KavunyCore.WaveMs, f[2].At);      // кидок 2 — два
        Assert.Equal(2 * KavunyCore.WaveMs + KavunyCore.StaggerMs, f[3].At);
        for (var i = 1; i < f.Count; i++)
        {
            Assert.True(f[i].At > f[i - 1].At);
            Assert.True(f[i].At - f[i - 1].At >= KavunyCore.StaggerMs);
            Assert.InRange(f[i].X0, 0.06, 0.94);
            Assert.InRange(f[i].X1, 0.06, 0.94);
            Assert.InRange(f[i].H, 0.58, 0.88);
        }
        // авторізання встигає раніше за наступний овоч
        Assert.True(KavunyCore.AutoCutMs < KavunyCore.StaggerMs);
    }

    [Fact]
    public void Cart_empties_at_the_cap()
    {
        var rng = new Random(9);
        for (var i = 0; i < 20_000; i++)
        {
            var seq = KavunyCore.Generate(KavunyCore.NewSeed(rng), 200);
            if (seq.Rotten) continue;
            Assert.True(seq.Potential >= 200);
            Assert.Equal(seq.Fruits[^1].At + KavunyCore.FlyMs + KavunyCore.LagMs, seq.EndAt);
            return;
        }
        Assert.Fail("за 20 тис. раундів ніхто не доріс до ×2");
    }
}
