using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>🎁 Безкоштовні прокрути: N на день акаунту, ставка з налаштувань, гаманець не рухається, Скарбничку не годує.</summary>
public class SlotFreeSpinsTests
{
    static SlotFreeSpins New(out FakeClock clock, out MemoryDayCounters counters, int spins = 20, Func<string, bool>? person = null)
    {
        clock = new FakeClock { UtcNow = new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero) };   // 12:00 Києва
        counters = new MemoryDayCounters();
        var o = new SlotFreeOptions { Spins = spins, Bet = 50 };
        return new SlotFreeSpins(counters, () => o, person ?? (n => !n.StartsWith("гість", StringComparison.Ordinal)), clock, defer: a => a());
    }

    [Fact]
    public void Twenty_a_day_per_person_and_new_ones_at_midnight()
    {
        var f = New(out var clock, out var counters, spins: 3);
        Assert.True(f.Take("Оля"));
        Assert.True(f.Take("оля"));   // ключ — без регістру
        Assert.True(f.Take("Оля"));
        Assert.False(f.Take("Оля"));
        Assert.Equal(0, f.Left("Оля"));
        Assert.Equal(3, f.Left("Петро"));
        Assert.Equal(3, counters.Counter("оля", SlotFreeSpins.CounterKey, "2026-10-25"));
        clock.UtcNow = new DateTimeOffset(2026, 10, 25, 22, 0, 0, TimeSpan.Zero);   // 00:00 Києва (25.10 уже зимовий час, UTC+2)
        Assert.Equal(3, f.Left("Оля"));
    }

    [Fact]
    public void Guests_get_none_and_return_gives_one_back()
    {
        var f = New(out var clock, out var counters, spins: 2);
        Assert.False(f.Take("гість Іван"));
        Assert.Equal(0, f.Left("гість Іван"));
        Assert.True(f.Take("Оля"));
        f.Return("Оля");
        Assert.Equal(2, f.Left("Оля"));
        Assert.True(f.Take("Оля"));
        var again = new SlotFreeSpins(counters, () => new SlotFreeOptions { Spins = 2 }, _ => true, clock, defer: a => a());
        Assert.Equal(1, again.Left("Оля"));   // перезапуск: використане — з бази
    }

    sealed class Kit
    {
        public FakeStakes Stakes { get; } = new();
        public SlotsOptions Options { get; } = new() { JackpotMustHit = 0 };
        public SlotsBank Bank { get; }
        public SlotFreeSpins Free { get; }
        public RoomHarness H { get; }

        public Kit(string nick = "Оля", int spins = 20, int wallet = 1000)
        {
            Stakes.Set(nick, wallet);
            Bank = new SlotsBank(Stakes, new FakeStore(), () => Options, new FakeClock(), new FakeSlotsWire(), defer: a => a());
            Free = new SlotFreeSpins(new MemoryDayCounters(), () => new SlotFreeOptions { Spins = spins, Bet = 50 },
                n => !n.StartsWith("гість", StringComparison.Ordinal), new FakeClock(), defer: a => a());
            var sp = new ServiceCollection().AddSingleton(Bank).AddSingleton(Free).BuildServiceProvider();
            H = new RoomHarness("slot-glek", services: sp, seed: 42);
            Assert.True(H.Solo(nick).Ok);
        }

        public SlotGame G => (SlotGame)H.Room.Game;
        public JsonElement View => H.View(0);
        public ScriptRng Rig() { var r = new ScriptRng(); G.Rng = r; return r; }
        public int Sum(string suffix) => Stakes.Ledger.Where(m => m.Ref.EndsWith(suffix, StringComparison.Ordinal)).Sum(m => m.Delta);
    }

    [Fact]
    public void Free_spin_takes_nothing_and_pays_the_win()
    {
        var k = new Kit();
        var pot = k.Bank.Pot;
        k.Rig().Stops(SlotsKit.StopsWhere((_, u) => u >= 2));
        Assert.True(k.H.Act(0, "spin", new { bet = 100, free = true }).Ok);
        var script = k.View.GetProperty("last").GetProperty("script");
        var win = script.GetProperty("win").GetInt32();
        Assert.True(win > 0);
        Assert.Equal(0, k.Sum(":bet"));
        Assert.Equal(win, k.Sum(":win"));
        Assert.Equal(1000 + win, k.Stakes.Balance("Оля"));
        Assert.Equal(pot, k.Bank.Pot);   // Скарбничку не годує
        Assert.Equal(19, script.GetProperty("free").GetProperty("left").GetInt32());
        Assert.Equal(50, script.GetProperty("free").GetProperty("bet").GetInt32());
        Assert.Equal(19, k.View.GetProperty("free").GetProperty("left").GetInt32());
        Assert.Empty(k.Bank.Pending());
    }

    [Fact]
    public void Free_spins_run_out_and_paid_spin_still_charges()
    {
        var k = new Kit(spins: 2);
        Assert.True(k.H.Act(0, "spin", new { free = true }).Ok);
        Assert.True(k.H.Act(0, "spin", new { free = true }).Ok);
        var r = k.H.Act(0, "spin", new { free = true });
        Assert.False(r.Ok);
        Assert.Contains("скінчились", r.Message);
        Assert.Equal(0, k.Sum(":bet"));
        Assert.True(k.H.Act(0, "spin", new { bet = 100 }).Ok);
        Assert.Equal(-100, k.Sum(":bet"));
    }

    [Fact]
    public void Guest_has_no_free_spins_even_with_empty_wallet()
    {
        var k = new Kit(nick: "гість Іван", wallet: 0);
        var r = k.H.Act(0, "spin", new { free = true });
        Assert.False(r.Ok);
        Assert.Contains("акаунт", r.Message);
        Assert.False(k.View.GetProperty("free").GetProperty("person").GetBoolean());
    }

    [Fact]
    public void Free_spin_works_with_empty_wallet()
    {
        var k = new Kit(wallet: 0);
        Assert.True(k.H.Act(0, "spin", new { free = true }).Ok);
        Assert.Equal(0, k.Sum(":bet"));
    }

    [Fact]
    public void Free_journal_entry_is_paid_on_recovery()
    {
        var stakes = new FakeStakes();
        var bank = new SlotsBank(stakes, new FakeStore(), () => new SlotsOptions { JackpotMustHit = 0 }, new FakeClock(), new FakeSlotsWire(), defer: a => a());
        var at = new FakeClock().UtcNow;
        Assert.True(bank.Open(new SlotPending("slot:r-e:1", "Оля", "slot-glek", 0, 240, 0, at)));
        Assert.Equal(1, bank.Recover(at.AddMinutes(5)));
        Assert.Equal(240, stakes.Balance("Оля"));
    }
}
