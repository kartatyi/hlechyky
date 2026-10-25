using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>Доплата Глека: +N % до виграшу в слотах і Лелеці, стеля на людину за київський день.</summary>
public class GlekBonusTests(ITestOutputHelper output)
{
    static GlekBonus New(out FakeClock clock, out MemoryDayCounters counters, int pct = 6, int cap = 3000)
    {
        clock = new FakeClock { UtcNow = new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero) };   // 12:00 Києва
        counters = new MemoryDayCounters();
        var o = new AzartBonusOptions { BonusPct = pct, BonusDailyCap = cap };
        return new GlekBonus(counters, () => o, clock, defer: a => a());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(8, 0)]      // 0,48 → 0
    [InlineData(9, 1)]      // 0,54 → 1
    [InlineData(25, 2)]     // 1,5 → 2 (від нуля)
    [InlineData(75, 5)]     // 4,5 → 5
    [InlineData(100, 6)]
    [InlineData(1234, 74)]  // 74,04
    public void Rounds_away_from_zero(int win, int bonus) => Assert.Equal(bonus, GlekBonus.Of(win, 6));

    [Fact]
    public void Daily_cap_holds_and_resets_next_Kyiv_day()
    {
        var g = New(out var clock, out var counters, cap: 100);
        Assert.Equal(60, g.Take("Оля", 1000));
        Assert.Equal(40, g.Left("оля"));                  // ключ — без регістру
        Assert.Equal(40, g.Take("Оля", 1000));            // упирається в стелю
        Assert.Equal(0, g.Take("Оля", 1000));
        Assert.Equal(0, g.Left("Оля"));
        Assert.Equal(6, g.Take("Петро", 100));            // стеля — на людину
        Assert.Equal(100, counters.Counter("оля", GlekBonus.CounterKey, "2026-10-15"));
        clock.UtcNow = new DateTimeOffset(2026, 10, 15, 21, 0, 0, TimeSpan.Zero);   // 00:00 Києва — новий день
        Assert.Equal(100, g.Left("Оля"));
        Assert.Equal(60, g.Take("Оля", 1000));
        Assert.Equal(60, counters.Counter("оля", GlekBonus.CounterKey, "2026-10-16"));
    }

    [Fact]
    public void Return_gives_the_cap_back_and_restart_reads_the_counter()
    {
        var g = New(out var clock, out var counters, cap: 100);
        Assert.Equal(60, g.Take("Оля", 1000));
        g.Return("Оля", 60);
        Assert.Equal(100, g.Left("Оля"));
        Assert.Equal(0, counters.Counter("оля", GlekBonus.CounterKey, "2026-10-15"));
        Assert.Equal(60, g.Take("Оля", 1000));
        // перезапуск: видане за день — з бази
        var again = new GlekBonus(counters, () => new AzartBonusOptions { BonusPct = 6, BonusDailyCap = 100 }, clock, defer: a => a());
        Assert.Equal(40, again.Left("Оля"));
        Assert.Equal(40, again.Take("Оля", 5000));
    }

    [Fact]
    public void Zero_pct_or_zero_cap()
    {
        var off = New(out _, out _, pct: 0);
        Assert.Equal(0, off.Take("Оля", 1000));
        Assert.Null(off.View("Оля"));
        var free = New(out _, out _, cap: 0);
        for (var i = 0; i < 100; i++) Assert.Equal(600, free.Take("Оля", 10_000));
        Assert.Equal(int.MaxValue, free.Left("Оля"));
    }

    // ---------- слоти ----------

    sealed class Kit
    {
        public FakeStakes Stakes { get; } = new();
        public SlotsOptions Options { get; } = new() { JackpotMustHit = 0 };
        public AzartBonusOptions Bonus { get; }
        public SlotsBank Bank { get; }
        public GlekBonus Glek { get; }
        public RoomHarness H { get; }

        public Kit(int pct = 6, int cap = 3000, int wallet = 1_000_000)
        {
            Bonus = new AzartBonusOptions { BonusPct = pct, BonusDailyCap = cap };
            Stakes.Set("Оля", wallet);
            Bank = new SlotsBank(Stakes, new FakeStore(), () => Options, new FakeClock(), new FakeSlotsWire(), defer: a => a());
            Glek = new GlekBonus(new MemoryDayCounters(), () => Bonus, new FakeClock(), defer: a => a());
            var sp = new ServiceCollection().AddSingleton(Bank).AddSingleton(Glek).BuildServiceProvider();
            H = new RoomHarness("slot-glek", services: sp, seed: 42);
            Assert.True(H.Solo("Оля").Ok);
        }

        public SlotGame G => (SlotGame)H.Room.Game;
        public JsonElement Script => H.View(0).GetProperty("last").GetProperty("script");
        public ScriptRng Rig() { var r = new ScriptRng(); G.Rng = r; return r; }
        public int Sum(string suffix) => Stakes.Ledger.Where(m => m.Ref.EndsWith(suffix, StringComparison.Ordinal)).Sum(m => m.Delta);
    }

    static JsonElement? Step(JsonElement script, string t) =>
        script.GetProperty("steps").EnumerateArray().Where(s => s.GetProperty("t").GetString() == t).Select(s => (JsonElement?)s).FirstOrDefault();

    [Fact]
    public void Slot_win_gets_dobavka_step_before_jackpot_and_ledger_pays_it()
    {
        var k = new Kit();
        k.Rig().Stops(SlotsKit.StopsWhere((_, u) => u >= 2)).Jackpot();
        Assert.True(k.H.Act(0, "spin", new { bet = 100 }).Ok);
        var s = k.Script;
        var steps = s.GetProperty("steps").EnumerateArray().Select(x => x.GetProperty("t").GetString()).ToList();
        var dob = Step(s, "dobavka")!.Value;
        var baseWin = dob.GetProperty("base").GetInt32();
        var bonus = dob.GetProperty("amount").GetInt32();
        Assert.True(baseWin > 0);
        Assert.Equal(GlekBonus.Of(baseWin, 6), bonus);
        Assert.Equal(steps.Count - 2, steps.IndexOf("dobavka"));   // передостанній — перед jackpot
        Assert.Equal("jackpot", steps[^1]);
        Assert.Equal(baseWin + bonus, s.GetProperty("win").GetInt32());
        Assert.Equal(baseWin + bonus, k.Sum(":win"));
        Assert.Equal("slot-win:slot-glek", k.Stakes.Reasons.Single(x => x.Key.EndsWith(":win")).Value);
        Assert.Equal(baseWin + bonus, k.G.State.Gamble!.Amount);   // Ворожка — з доплатою
        Assert.Equal(k.Stakes.Balance("Оля"), k.H.View(0).GetProperty("balance").GetInt32());
        var view = k.H.View(0).GetProperty("dobavka");
        Assert.Equal(6, view.GetProperty("pct").GetInt32());
        Assert.Equal(3000 - bonus, view.GetProperty("left").GetInt32());
        Assert.Empty(k.Bank.Pending());
    }

    [Fact]
    public void Gamble_win_has_no_dobavka()
    {
        var k = new Kit();
        k.Rig().Stops(SlotsKit.StopsWhere((_, u) => u >= 2));
        Assert.True(k.H.Act(0, "spin", new { bet = 100 }).Ok);
        var amount = k.G.State.Gamble!.Amount;
        var left = k.Glek.Left("Оля");
        var rng = k.Rig();
        rng.Ints.Enqueue(0);   // карта «r»
        Assert.True(k.H.Act(0, "gamble", new { pick = "r" }).Ok);
        Assert.Equal(amount * 2, k.G.State.Gamble!.Amount);
        Assert.Equal(left, k.Glek.Left("Оля"));
    }

    [Fact]
    public void Spent_cap_means_no_step_and_zero_pct_changes_nothing()
    {
        var k = new Kit(cap: 1);
        k.Glek.Take("Оля", 1000);   // стелю вибрано
        k.Rig().Stops(SlotsKit.StopsWhere((_, u) => u >= 2));
        Assert.True(k.H.Act(0, "spin", new { bet = 100 }).Ok);
        Assert.Null(Step(k.Script, "dobavka"));
        Assert.Equal(k.Script.GetProperty("win").GetInt32(), k.Sum(":win"));

        var off = new Kit(pct: 0);
        off.Rig().Stops(SlotsKit.StopsWhere((_, u) => u >= 2));
        Assert.True(off.H.Act(0, "spin", new { bet = 100 }).Ok);
        Assert.Null(Step(off.Script, "dobavka"));
        Assert.False(off.Script.TryGetProperty("dobavka", out _));
        Assert.Equal(JsonValueKind.Null, off.H.View(0).GetProperty("dobavka").ValueKind);
    }

    /// <summary>Лічильники, що на першій доплаті щось роблять (тут — спорожняють гаманець між перевіркою і списанням).</summary>
    sealed class HookCounters(Action hook) : IDayCounters
    {
        readonly MemoryDayCounters _m = new();
        Action? _hook = hook;
        public int Bump(string nickKey, string key, string day, int by)
        {
            if (by > 0) { _hook?.Invoke(); _hook = null; }
            return _m.Bump(nickKey, key, day, by);
        }
        public int Counter(string nickKey, string key, string day) => _m.Counter(nickKey, key, day);
    }

    [Fact]
    public void Failed_charge_returns_the_dobavka()
    {
        var stakes = new FakeStakes().Set("Оля", 100);
        var bank = new SlotsBank(stakes, new FakeStore(), () => new SlotsOptions { JackpotMustHit = 0 }, new FakeClock(), new FakeSlotsWire(), defer: a => a());
        var counters = new HookCounters(() => stakes.Set("Оля", 0));
        var glek = new GlekBonus(counters, () => new AzartBonusOptions(), new FakeClock(), defer: a => a());
        var h = new RoomHarness("slot-glek", services: new ServiceCollection().AddSingleton(bank).AddSingleton(glek).BuildServiceProvider(), seed: 42);
        Assert.True(h.Solo("Оля").Ok);
        var rng = new ScriptRng();
        ((SlotGame)h.Room.Game).Rng = rng;
        rng.Stops(SlotsKit.StopsWhere((_, u) => u >= 2));
        Assert.False(h.Act(0, "spin", new { bet = 100 }).Ok);   // «бракує черепків»
        Assert.Equal(3000, glek.Left("Оля"));
        Assert.Equal(0, counters.Counter("оля", GlekBonus.CounterKey, Days.Today(new FakeClock())));
        Assert.Empty(bank.Pending());
        Assert.Empty(stakes.Ledger);
    }

    // ---------- Лелека ----------

    sealed class LKit
    {
        public FakeStakes Stakes { get; } = new();
        public LelkaBook Book { get; }
        public GlekBonus Glek { get; }
        public RoomHarness H { get; }

        public LKit()
        {
            Stakes.Set("Оля", 1000);
            Stakes.Set("Петро", 1000);
            H = null!;
            Book = new LelkaBook(Stakes, new FakeStore(), defer: a => a(), options: new FixedOptions<LelkaOptions>(new LelkaOptions()),
                held: (table, round) => LelkaBook.HeldBy(H.Rooms, table, round));
            Glek = new GlekBonus(new MemoryDayCounters(), () => new AzartBonusOptions(), new FakeClock(), defer: a => a());
            var sp = new ServiceCollection().AddSingleton(Book).AddSingleton(Glek).BuildServiceProvider();
            H = new RoomHarness("lelka", services: sp, seed: 11);
            Assert.True(H.Join("Оля").Ok);
            Assert.True(H.Join("Петро").Ok);
        }

        public Lelka G => (Lelka)H.Room.Game;
        public void To(string phase) { for (var i = 0; i < 3000 && G.State.Phase != phase; i++) H.Tick(); Assert.Equal(phase, G.State.Phase); }
    }

    [Fact]
    public void Lelka_cash_pays_win_plus_dobavka_and_settle_keeps_it()
    {
        var k = new LKit();
        k.G.Rig = () => 300;
        if (k.G.State.Phase == Lelka.Bets) k.G.State.Crash = 300;
        Assert.True(k.H.Act(0, "bet", new { amount = 100, auto = 2.0 }).Ok);
        Assert.True(k.H.Act(1, "bet", new { amount = 100, auto = 5.0 }).Ok);
        k.To(Lelka.Crashed);
        var olya = k.G.State.Bets.Single(b => b.Nick == "Оля");
        Assert.Equal(12, olya.Bonus);              // 6 % від 200
        Assert.Equal(212, olya.Win);
        Assert.Equal(900 + 212, k.Stakes.Balance("Оля"));   // розрахунок не доплатив удруге й не загубив доплату
        Assert.Equal(900, k.Stakes.Balance("Петро"));       // шубовсть — без доплати
        Assert.Equal(3000 - 12, k.Glek.Left("Оля"));
        Assert.Equal(3000, k.Glek.Left("Петро"));
    }

    [Fact]
    public void Lelka_bet_back_has_no_dobavka()
    {
        // ставка, на яку каса не встигла відповісти, і політ вище ×1 — «ставку назад» рівно, без доплати
        var stakes = new FakeStakes().Set("Оля", 1000);
        var book = new LelkaBook(stakes, new FakeStore(), defer: a => a(), options: new FixedOptions<LelkaOptions>(new LelkaOptions()));
        Assert.True(stakes.TrySpend("Оля", 100, "lelka-bet:lelka", "lelka-bet:t:1:оля"));
        book.Settle(new LelkaRound("t", 1, "lelka", 250, DateTimeOffset.UtcNow, [new LelkaPay("Оля", 100, null, null)]));
        Assert.Equal(1000, stakes.Balance("Оля"));
        Assert.Contains(stakes.Reasons, x => x.Value == "lelka-back:lelka");
    }

    // ---------- окупність ----------

    [Fact]
    public void Payback_with_dobavka_without_cap()
    {
        const int Bet = 100, Spins = 150_000;
        (string Id, ISlotMath Math)[] maths =
        [
            ("slot-glek", new SlotGlekMath()), ("slot-cascade", new SlotCascadeMath()), ("slot-hold", new SlotHoldMath()),
            ("slot-cluster", new SlotClusterMath()), ("slot-slidy", new SlotSlidyMath()),
        ];
        foreach (var (id, math) in maths)
        {
            var rng = new SeededSlotRng(new Random(7));
            var state = new JsonObject();
            long won = 0, bonus = 0;
            for (var i = 0; i < Spins; i++)
            {
                var o = math.Spin(Bet, rng, state);
                state = o.State;
                won += o.Win;
                bonus += GlekBonus.Of(o.Win, 6);
            }
            var staked = (double)Spins * Bet;
            output.WriteLine($"{id}: база {won / staked:P2}, з доплатою {(won + bonus) / staked:P2} (+1 % Скарбничка ≈ {(won + bonus) / staked + 0.01:P2})");
            Assert.InRange((double)bonus / Math.Max(1, won), 0.058, 0.062);
        }
        // Лелека: забирати на одному множнику
        var r = new Random(7);
        foreach (var target in new[] { 101, 150, 200, 1000 })
        {
            long staked = 0, back = 0, plus = 0;
            for (var i = 0; i < 200_000; i++)
            {
                var crash = LelkaCore.CrashCents(LelkaCore.NewSeed(r));
                staked += Bet;
                if (crash >= target)
                {
                    var w = LelkaCore.Win(Bet, target);
                    back += w;
                    plus += GlekBonus.Of(w, 6);
                }
            }
            output.WriteLine($"lelka ×{target / 100.0:0.00}: база {(double)back / staked:P2}, з доплатою {(double)(back + plus) / staked:P2}");
        }
    }
}
