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

    /// <summary>Точно: усі 32³ зупинок рівноймовірні.</summary>
    static (double Rtp, double Hit, double Max) Exact()
    {
        long units = 0, hits = 0, max = 0;
        var n = 0;
        for (var a = 0; a < 32; a++)
            for (var b = 0; b < 32; b++)
                for (var c = 0; c < 32; c++, n++)
                {
                    var u = SlotGlekMath.Units([a, b, c]);
                    units += u;
                    if (u > 0) hits++;
                    max = Math.Max(max, u);
                }
        return (units / 5.0 / n, (double)hits / n, max / 5.0);
    }

    static (double Rtp, double Hit, double Max) Simulate(int spins, int seed)
    {
        var math = new SlotGlekMath();
        var rng = new SeededSlotRng(new Random(seed));
        var state = new JsonObject();
        long won = 0, hits = 0, max = 0;
        for (var i = 0; i < spins; i++)
        {
            var o = math.Spin(Bet, rng, state);
            won += o.Win;
            if (o.Win > 0) hits++;
            max = Math.Max(max, o.Win);
        }
        return ((double)won / ((long)spins * Bet), (double)hits / spins, (double)max / Bet);
    }

    [Fact]
    public void Exact_rtp_of_one_armed_glek()
    {
        var (rtp, hit, max) = Exact();
        output.WriteLine($"slot-glek точно: RTP {rtp:P3}, виграш {hit:P2} (1 з {1 / hit:F2}), найбільше {max}×");
        Assert.InRange(rtp, 0.945, 0.965);
        Assert.InRange(hit, 0.30, 0.42);
        Assert.True(max <= new SlotGlekMath().Cap);
    }

    [Fact]
    public void Two_hundred_thousand_spins_land_near_the_exact_rtp()
    {
        var exact = Exact();
        var (rtp, hit, max) = Simulate(200_000, 1);
        output.WriteLine($"slot-glek 200 тис.: RTP {rtp:P3}, виграш {hit:P2}, найбільше {max}×");
        Assert.InRange(rtp, exact.Rtp - 0.02, exact.Rtp + 0.02);
        Assert.InRange(hit, exact.Hit - 0.01, exact.Hit + 0.01);
        Assert.True(max <= 1000);
    }

    [Fact, Trait("Category", "Perf")]
    public void Five_million_spins_within_half_a_percent()
    {
        var exact = Exact();
        var (rtp, hit, max) = Simulate(5_000_000, 2);
        output.WriteLine($"slot-glek 5 млн: RTP {rtp:P3}, виграш {hit:P2}, найбільше {max}×");
        Assert.InRange(rtp, exact.Rtp - 0.005, exact.Rtp + 0.005);
        Assert.InRange(hit, exact.Hit - 0.002, exact.Hit + 0.002);
    }

    [Fact]
    public void Jackpot_pays_back_what_was_put_in_on_average()
    {
        // Чесність шансу p = внесок ÷ сума: за тієї самої суми оберт у середньому віддає рівно внесок. Суму щоразу
        // повертаємо (Unfeed), інакше без кінця росте: час до падіння має важкий хвіст (див. specs/slots.md §4).
        var opts = new SlotsOptions { JackpotPct = 1, JackpotSeed = 200 };
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
        var opts = new SlotsOptions { JackpotPct = 1, JackpotSeed = 1000 };
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
        var opts = new SlotsOptions { JackpotPct = 1, JackpotSeed = 10_000 };
        var bank = new SlotsBank(new FakeStakes(), new FakeStore(), () => opts, new FakeClock());
        var never = new ScriptRng();
        for (var i = 0; i < 500; i++) Assert.Equal(0, bank.Feed(200, never));
        Assert.Equal(11_000, bank.Pot);
        bank.Unfeed(200, 0);
        Assert.Equal(10_998, bank.Pot);
    }
}
