using System.Text.Json.Nodes;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Математика автоматів (specs/slots.md §3): точний RTP перебором стрічок, симуляції обертів і Скарбнички. Цифри — у
/// таблицю spec; тест друкує їх (dotnet test … --logger "console;verbosity=detailed").
/// </summary>
public class SlotsSimTests(ITestOutputHelper output)
{
    const int Bet = 100;

    /// <summary>Підсумок математики slot-glek: RTP і частоти — частки обертів, Max і Sd — у ставках.</summary>
    public sealed record GlekStats(double Rtp, double BaseRtp, double Hit, double Ge1, double Ge10, double Ge50, double Sneeze, double Max, double Sd)
    {
        /// <summary>RTP з округленням виплати лінії round(множник × ставка ÷ 5) для кожної ставки з <see cref="Bets"/>.</summary>
        public Dictionary<int, double> ByBet { get; init; } = [];
    }

    /// <summary>Ставки, на яких перевіряємо округлення: типовий набір і найменша дозволена.</summary>
    static readonly int[] Bets = [.. SlotsOptions.DefaultBets.Append(SlotsOptions.MinBet).Distinct()];

    /// <summary>
    /// Перебір усіх наслідків генератора: функцію ганяємо стільки разів, скільки є шляхів викликів Next, кожен шлях —
    /// з імовірністю Π 1/max. Так чих перевіряється рівно тим кодом, що грає на сервері (<see cref="SlotGlekMath.Sneeze"/>).
    /// </summary>
    public sealed class TreeRng : ISlotRng
    {
        readonly List<int> _path = [], _max = [];
        int _pos;

        public int Next(int max)
        {
            if (_pos == _path.Count) { _path.Add(0); _max.Add(max); }
            Assert.Equal(_max[_pos], max);
            return _path[_pos++];
        }

        public double NextDouble() => throw new InvalidOperationException("чих не бере NextDouble");

        public static List<(T Value, double P)> All<T>(Func<ISlotRng, T> f)
        {
            var rng = new TreeRng();
            var res = new List<(T, double)>();
            while (true)
            {
                rng._pos = 0;
                var v = f(rng);
                Assert.Equal(rng._path.Count, rng._pos);   // шлях не довший, ніж справді взяли
                var p = 1.0;
                for (var i = 0; i < rng._pos; i++) p /= rng._max[i];
                res.Add((v, p));
                while (rng._path.Count > 0 && rng._path[^1] + 1 == rng._max[^1]) { rng._path.RemoveAt(rng._path.Count - 1); rng._max.RemoveAt(rng._max.Count - 1); }
                if (rng._path.Count == 0) return res;
                rng._path[^1]++;
            }
        }
    }

    static string Key(IEnumerable<(int C, int R)> cells) => string.Join(" ", cells.Select(x => $"{x.C}{x.R}").Order());

    /// <summary>Точний розподіл чиху для поля: набір клітинок (ключ «cr cr …», порожній — не чхнув) → імовірність.</summary>
    public static Dictionary<string, (List<(int C, int R)> Cells, double P)> SneezeDist(string[,] f)
    {
        var d = new Dictionary<string, (List<(int, int)>, double)>();
        foreach (var (cells, p) in TreeRng.All(rng => SlotGlekMath.Sneeze(f, rng)))
        {
            var k = Key(cells);
            d[k] = d.TryGetValue(k, out var x) ? (x.Item1, x.Item2 + p) : (cells, p);
        }
        return d;
    }

    static readonly Lazy<GlekStats> ExactCache = new(ExactCore);

    /// <summary>Точно: усі 34³ зупинок рівноймовірні × усі наслідки чиху (з тими вагами, що дає сам код).</summary>
    public static GlekStats Exact() => ExactCache.Value;

    static GlekStats ExactCore()
    {
        var reels = SlotGlekMath.Reels;
        var total = (double)reels[0].Length * reels[1].Length * reels[2].Length;
        var byMask = new Dictionary<int, List<(List<(int C, int R)> Cells, double P)>>();
        double ev = 0, ev2 = 0, baseEv = 0, hit = 0, ge1 = 0, ge10 = 0, ge50 = 0, sneeze = 0, max = 0;
        var won = new double[Bets.Length];
        for (var a = 0; a < reels[0].Length; a++)
            for (var b = 0; b < reels[1].Length; b++)
                for (var c = 0; c < reels[2].Length; c++)
                {
                    var f = SlotGlekMath.Field([a, b, c]);
                    baseEv += SlotGlekMath.Units(f) / 5.0 / total;
                    var mask = 0;
                    for (var i = 0; i < 9; i++) if (f[i / 3, i % 3] == SlotGlekMath.Wild) mask |= 1 << i;
                    if (!byMask.TryGetValue(mask, out var dist)) byMask[mask] = dist = [.. SneezeDist(f).Values];
                    foreach (var (cells, p) in dist)
                    {
                        var g = (string[,])f.Clone();
                        foreach (var (cc, rr) in cells) g[cc, rr] = SlotGlekMath.Wild;
                        var w = p / total;
                        var units = 0;
                        foreach (var ln in SlotGlekMath.PayLines)
                            if (SlotGlekMath.Line(g[0, ln[0]], g[1, ln[1]], g[2, ln[2]]) is { } lw)
                            {
                                units += lw.Pay;
                                for (var j = 0; j < Bets.Length; j++)
                                    won[j] += w * Math.Round(lw.Pay * (double)Bets[j] / SlotGlekMath.Lines, MidpointRounding.AwayFromZero) / Bets[j];
                            }
                        var x = units / 5.0;
                        ev += w * x; ev2 += w * x * x;
                        if (x > 0) hit += w;
                        if (x >= 1) ge1 += w;
                        if (x >= 10) ge10 += w;
                        if (x >= 50) ge50 += w;
                        if (cells.Count > 0) sneeze += w;
                        max = Math.Max(max, x);
                    }
                }
        return new GlekStats(ev, baseEv, hit, ge1, ge10, ge50, sneeze, max, Math.Sqrt(ev2 - ev * ev))
        {
            ByBet = Bets.Select((b, j) => (b, won[j])).ToDictionary(x => x.b, x => x.Item2),
        };
    }

    static GlekStats Simulate(int spins, int seed)
    {
        var math = new SlotGlekMath();
        var rng = new SeededSlotRng(new Random(seed));
        var state = new JsonObject();
        long won = 0, hits = 0, max = 0, ge1 = 0, ge10 = 0, sneezes = 0;
        for (var i = 0; i < spins; i++)
        {
            var o = math.Spin(Bet, rng, state);
            won += o.Win;
            if (o.Win > 0) hits++;
            if (o.Win >= Bet) ge1++;
            if (o.Win >= 10 * Bet) ge10++;
            if (o.Flags.Contains("sneeze")) sneezes++;
            max = Math.Max(max, o.Win);
        }
        double n = spins;
        return new GlekStats(won / (n * Bet), 0, hits / n, ge1 / n, ge10 / n, 0, sneezes / n, (double)max / Bet, 0);
    }

    [Fact]
    public void Exact_rtp_of_one_armed_glek()
    {
        var s = Exact();
        output.WriteLine($"slot-glek точно: RTP {s.Rtp:P3} (без чиху {s.BaseRtp:P3}), виграш {s.Hit:P2} (1 з {1 / s.Hit:F2}), ≥ 1× {s.Ge1:P2}, " +
            $"≥ 10× {s.Ge10:P3}, ≥ 50× {s.Ge50:P4}, чих {s.Sneeze:P3} (1 з {1 / s.Sneeze:F2}), найбільше {s.Max}×, σ {s.Sd:F2}");
        Assert.InRange(s.Rtp, 0.968, 0.972);              // база 97 % (+ Скарбничка 1 % = 98 %)
        Assert.InRange(s.Hit, 0.45, 0.60);                // частіше призи: було 38,4 %
        Assert.InRange(s.Ge1, 0.26, 0.40);                // виграш ≥ 1× ставки: було 20,6 %
        Assert.Equal(1.0 / SlotGlekMath.SneezeOdds, s.Sneeze, 9);
        Assert.InRange(s.Sd, 0, 5);                       // волатильність низька (у кластера σ ≈ 7)
        Assert.True(s.Max <= new SlotGlekMath().Cap);
    }

    [Fact]
    public void Exact_rtp_holds_on_every_bet_with_rounding()
    {
        // Виграш лінії — round(множник × ставка ÷ 5): для ставок, кратних 5, рівно (усі з набору), решта — не вище
        // 98 % разом зі Скарбничкою. Рахується тим самим перебором, що й Exact.
        var exact = Exact();
        foreach (var (bet, rtp) in exact.ByBet)
        {
            output.WriteLine($"slot-glek ставка {bet}: RTP бази {rtp:P3}");
            Assert.InRange(rtp, 0.968, 0.98);
            if (bet % SlotGlekMath.Lines == 0) Assert.Equal(exact.Rtp, rtp, 9);
        }
    }

    [Fact]
    public void Two_hundred_thousand_spins_land_near_the_exact_rtp()
    {
        // σ оберту ≈ 4,1 ставки → стандартна похибка RTP на 200 тис. ≈ 0,93 %; ±2,5 % — це 2,7σ.
        var exact = Exact();
        var s = Simulate(200_000, 1);
        output.WriteLine($"slot-glek 200 тис.: RTP {s.Rtp:P3}, виграш {s.Hit:P2}, ≥ 1× {s.Ge1:P2}, ≥ 10× {s.Ge10:P3}, чих {s.Sneeze:P3}, найбільше {s.Max}×");
        Assert.InRange(s.Rtp, exact.Rtp - 0.025, exact.Rtp + 0.025);
        Assert.InRange(s.Hit, exact.Hit - 0.01, exact.Hit + 0.01);
        Assert.InRange(s.Ge1, exact.Ge1 - 0.01, exact.Ge1 + 0.01);
        Assert.InRange(s.Sneeze, exact.Sneeze - 0.003, exact.Sneeze + 0.003);   // σ частки 1/12 на 200 тис. ≈ 0,06 %
        Assert.True(s.Max <= 1000);
    }

    [Fact, Trait("Category", "Perf")]
    public void Five_million_spins_within_half_a_percent()
    {
        // σ RTP на 5 млн ≈ 0,19 % → ±0,5 % — 2,7σ.
        var exact = Exact();
        var s = Simulate(5_000_000, 2);
        output.WriteLine($"slot-glek 5 млн: RTP {s.Rtp:P3}, виграш {s.Hit:P2}, ≥ 1× {s.Ge1:P2}, ≥ 10× {s.Ge10:P3}, чих {s.Sneeze:P3}, найбільше {s.Max}×");
        Assert.InRange(s.Rtp, exact.Rtp - 0.005, exact.Rtp + 0.005);
        Assert.InRange(s.Hit, exact.Hit - 0.002, exact.Hit + 0.002);
        Assert.InRange(s.Sneeze, exact.Sneeze - 0.0007, exact.Sneeze + 0.0007);
    }

    [Fact]
    public void Sneeze_distribution_is_exactly_as_written()
    {
        // Поле без Глеків: 11/12 — нічого; чих — 1, 2 чи 3 клітинки з вагами 14/5/1 (з 20), кожен набір рівноймовірний.
        int[] stops = [0, 0, 0];
        var f = SlotGlekMath.Field(stops);
        Assert.DoesNotContain(SlotGlekMath.Wild, f.Cast<string>());
        var d = SneezeDist(f);
        Assert.Equal(1 + 9 + 36 + 84, d.Count);
        Assert.Equal(11.0 / 12, d[""].P, 12);
        var w = SlotGlekMath.SneezeWeights;
        double[] per = [0, w[0] / 20.0 / 9, w[1] / 20.0 / 36, w[2] / 20.0 / 84];
        foreach (var (k, (cells, p)) in d.Where(x => x.Key != ""))
            Assert.Equal(per[cells.Count] / 12, p, 12);
        Assert.Equal(1.0, d.Values.Sum(x => x.P), 12);
    }

    [Fact]
    public void Sneeze_never_touches_a_glek_and_takes_one_to_three_cells()
    {
        // Поле з Глеком на кожному барабані — чих лише серед шести інших.
        var stops = Enumerable.Range(0, 3).Select(c => (Array.IndexOf(SlotGlekMath.Reels[c], SlotGlekMath.Wild) + 33) % 34).ToArray();
        var f = SlotGlekMath.Field(stops);
        Assert.Equal(3, f.Cast<string>().Count(x => x == SlotGlekMath.Wild));
        var d = SneezeDist(f);
        Assert.Equal(1 + 6 + 15 + 20, d.Count);
        foreach (var (cells, _) in d.Values)
        {
            Assert.InRange(cells.Count, 0, 3);
            Assert.Equal(cells.Count, cells.Distinct().Count());
            Assert.All(cells, x => Assert.NotEqual(SlotGlekMath.Wild, f[x.C, x.R]));
        }
        Assert.Equal(1.0 / 12, d.Values.Where(x => x.Cells.Count > 0).Sum(x => x.P), 12);
    }

    [Fact]
    public void Jackpot_pays_back_what_was_put_in_on_average()
    {
        // Чесність шансу p = внесок ÷ сума: за тієї самої суми оберт у середньому віддає рівно внесок. Суму щоразу
        // повертаємо (Unfeed), інакше без кінця росте: час до падіння має важкий хвіст (див. specs/slots.md §4).
        var opts = new SlotsOptions { JackpotPct = 1, JackpotSeed = 200, JackpotMustHit = 0 };
        var bank = new SlotsBank(new FakeStakes(), new FakeStore(), () => opts, new FakeClock());
        var rng = new SeededSlotRng(new Random(3));
        long paid = 0, hits = 0;
        const int spins = 1_000_000;
        for (var i = 0; i < spins; i++)
        {
            var jp = bank.Feed(Bet, rng);
            if (jp > 0) { paid += jp; hits++; }
            bank.Unfeed(Bet, jp);
        }
        var put = spins * (Bet * opts.JackpotPct / 100);
        output.WriteLine($"Скарбничка на сумі {bank.Pot}: внесено {put}, виплачено {paid} ({(double)paid / put:P2}), падінь {hits}");
        Assert.Equal(200, bank.Pot);
        Assert.InRange((double)paid / put, 0.95, 1.05);
    }

    [Fact]
    public void Jackpot_money_is_conserved_on_a_long_run()
    {
        // Усе, що внесли, або виплачено, або лежить у Скарбничці; дім доклав лише початкову суму після кожного падіння.
        var opts = new SlotsOptions { JackpotPct = 1, JackpotSeed = 1000, JackpotMustHit = 0 };
        var bank = new SlotsBank(new FakeStakes(), new FakeStore(), () => opts, new FakeClock());
        var rng = new SeededSlotRng(new Random(4));
        long paid = 0, hits = 0;
        const int spins = 1_000_000;
        for (var i = 0; i < spins; i++)
        {
            var jp = bank.Feed(Bet, rng);
            if (jp > 0) { paid += jp; hits++; }
        }
        var put = spins * (Bet * opts.JackpotPct / 100);
        output.WriteLine($"Скарбничка за 1 млн: внесено {put}, виплачено {paid}, падінь {hits}, лишилось {bank.Pot}");
        Assert.True(hits > 0);
        Assert.InRange(put + opts.JackpotSeed * (hits + 1) - paid - bank.Pot, 0, hits + 1);   // копійки — від округлення виплат униз
    }

    [Fact]
    public void Jackpot_contributions_add_up()
    {
        var opts = new SlotsOptions { JackpotPct = 1, JackpotSeed = 10_000, JackpotMustHit = 0 };
        var bank = new SlotsBank(new FakeStakes(), new FakeStore(), () => opts, new FakeClock());
        var never = new ScriptRng();
        for (var i = 0; i < 500; i++) Assert.Equal(0, bank.Feed(200, never));
        Assert.Equal(11_000, bank.Pot);
        bank.Unfeed(200, 0);
        Assert.Equal(10_998, bank.Pot);
    }
    // ---------- «має впасти до» ----------

    [Fact]
    public void Must_hit_share_follows_the_formula()
    {
        Assert.Equal(1 / (1 + Math.Log(10)), new SlotsOptions().SeedShare, 12);           // 10 000 → 100 000: 30,28 %
        Assert.Equal(0, new SlotsOptions { JackpotMustHit = 0 }.SeedShare);
        Assert.False(new SlotsOptions { JackpotMustHit = 5000 }.MustHitOn);                // межа нижче старту — вимкнено
    }

    [Fact]
    public void Must_hit_limit_drops_the_jackpot_on_the_spin_that_reaches_it()
    {
        var opts = new SlotsOptions { JackpotPct = 1, JackpotSeed = 1000, JackpotMustHit = 2000 };
        var bank = new SlotsBank(new FakeStakes(), new FakeStore(), () => opts, new FakeClock());
        var never = new ScriptRng();   // 0,999 — таємничий шанс не спрацьовує ніколи
        var add = 5 * (1 - opts.SeedShare);
        var need = (int)Math.Ceiling(1000 / add);
        for (var i = 1; i < need; i++) Assert.Equal(0, bank.Feed(500, never));
        Assert.True(bank.Pot < 2000);
        var won = bank.Feed(500, never);                       // цей оберт доклав останній внесок
        Assert.Equal(2000, won);
        Assert.InRange(bank.Pot, 1000, 1000 + add);            // знову з початкової (надлишок понад межу — у нову суму)
        // гроші: усе внесене = виплачене + сума + запас (запас уже віддав старт новій сумі)
        Assert.InRange(need * 5.0 - (won + bank.Pot - 1000 + bank.Reserve), 0, 1);   // Pot — ціла частина
        bank.Unfeed(500, won);                                 // ставку не списано — усе назад, як до оберту
        Assert.True(bank.Pot < 2000 && bank.Pot >= 2000 - add);
    }

    [Fact]
    public void Must_hit_jackpot_pays_back_what_was_put_in_on_a_long_run()
    {
        // З межею старт оплачують самі внески (запас), тож за довгий час виплачено ≈ внесене — разом зі стартами.
        var opts = new SlotsOptions { JackpotPct = 1, JackpotSeed = 1000, JackpotMustHit = 10_000 };
        var bank = new SlotsBank(new FakeStakes(), new FakeStore(), () => opts, new FakeClock());
        var rng = new SeededSlotRng(new Random(5));
        long paid = 0, hits = 0, atLimit = 0;
        const int spins = 3_000_000;
        for (var i = 0; i < spins; i++)
        {
            var jp = bank.Feed(500, rng);
            if (jp <= 0) continue;
            paid += jp;
            hits++;
            if (jp >= 10_000) atLimit++;
        }
        var put = spins * 5.0;
        output.WriteLine($"Скарбничка 1000→10 000 за 3 млн: внесено {put}, виплачено {paid} ({paid / put:P2}), падінь {hits}, на межі {atLimit} ({(double)atLimit / hits:P1}), середня {(double)paid / hits:F0}, запас {bank.Reserve:F0}");
        Assert.InRange(paid / put, 0.97, 1.03);
        Assert.InRange((double)atLimit / hits, 0.08, 0.12);    // до межі доживає старт ÷ межа = 10 %
        Assert.InRange((double)paid / hits, 3300 * 0.95, 3300 * 1.05);   // старт × (1 + ln 10) ≈ 3303
        Assert.InRange(put - (paid + bank.Pot - 1000 + bank.Reserve), 0, 1);   // нічого не губиться (Pot — ціла частина)
    }

    [Fact]
    public void Feed_shows_must_hit_only_when_it_is_on()
    {
        var opts = new SlotsOptions();
        var bank = new SlotsBank(new FakeStakes(), new FakeStore(), () => opts, new FakeClock());
        var on = System.Text.Json.JsonSerializer.Serialize(bank.FeedView(), SlotsSetup.FeedJson);
        Assert.Contains("\"mustHit\":100000", on);
        opts.JackpotMustHit = 0;
        var off = System.Text.Json.JsonSerializer.Serialize(bank.FeedView(), SlotsSetup.FeedJson);
        Assert.DoesNotContain("mustHit", off);
        Assert.Contains("\"jackpot\":10000", off);
    }
}
