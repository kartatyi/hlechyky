using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hlechyky.Tests.Games;

/// <summary>Каса рулетки (specs/roulette.md §3.3, §3.5): відновлення, підмітання, справжній леджер, підписи й ачівки.</summary>
public class RouletteBookTests
{
    [Fact]
    public void Recover_skips_players_who_never_paid()
    {
        var stakes = new FakeStakes().Set("Оля", 1000);
        var store = new FakeStore();
        var book = new RouletteBook(stakes, store, defer: a => a());
        Assert.True(book.Open(new PendingSpin("room:abcd0123", 1, "roulette", 17, DateTimeOffset.UnixEpoch, [new SpinPay("Оля", 10, 360)])));
        Assert.Equal(1, book.Recover(DateTimeOffset.MaxValue));
        Assert.Empty(stakes.Calls);
        Assert.Empty(book.Pending());
    }

    [Fact]
    public void Periodic_recover_takes_only_records_older_than_2_minutes()
    {
        var clock = new FakeClock();
        var stakes = new FakeStakes().Set("Оля", 1000);
        var book = new RouletteBook(stakes, new FakeStore(), clock, defer: a => a());
        var old = new PendingSpin("room:00000001", 1, "roulette", 1, clock.UtcNow.AddMinutes(-3), [new SpinPay("Оля", 10, 20)]);
        var fresh = new PendingSpin("room:00000001", 2, "roulette", 1, clock.UtcNow.AddMinutes(-1), [new SpinPay("Оля", 10, 20)]);
        book.Open(old);
        book.Take("Оля", 10, "roulette-bet:roulette", "roulette-bet:room:00000001:1:оля");
        book.Open(fresh);
        book.Take("Оля", 10, "roulette-bet:roulette", "roulette-bet:room:00000001:2:оля");
        Assert.Equal(1, book.Sweep());
        Assert.Equal(["grant:Оля:20:roulette-win:room:00000001:1:оля"], stakes.Calls.Where(c => c.StartsWith("grant")));
        Assert.Equal(2, Assert.Single(book.Pending()).Spin);
        clock.Advance(TimeSpan.FromMinutes(1.5));
        Assert.Equal(1, book.Sweep());
        Assert.Empty(book.Pending());
    }

    [Fact]
    public void Start_recovery_leaves_spins_opened_after_the_book_was_born()
    {
        var clock = new FakeClock();
        var stakes = new FakeStakes().Set("Оля", 1000);
        var book = new RouletteBook(stakes, new FakeStore(), clock, defer: a => a());
        // лишилось від старого процесу
        Assert.True(book.Open(new PendingSpin("room0001:aaaa0000", 1, "roulette", 1, clock.UtcNow.AddSeconds(-30), [new SpinPay("Оля", 10, 20)])));
        Assert.True(book.Take("Оля", 10, "roulette-bet:roulette", "roulette-bet:room0001:aaaa0000:1:оля"));
        // відновлений стіл закрив коло вже в цьому процесі, раніше, ніж каса взялась відновлювати
        Assert.True(book.Open(new PendingSpin("room0002:bbbb0000", 1, "roulette", 1, clock.UtcNow, [new SpinPay("Оля", 10, 20)])));
        Assert.True(book.Take("Оля", 10, "roulette-bet:roulette", "roulette-bet:room0002:bbbb0000:1:оля"));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, book.RecoverAtStart());
        Assert.Equal(["grant:Оля:20:roulette-win:room0001:aaaa0000:1:оля"], stakes.Calls.Where(c => c.StartsWith("grant")));
        Assert.Equal("room0002:bbbb0000", Assert.Single(book.Pending()).Table);
        Assert.True(book.Holds("room0002:bbbb0000", 1));
        Assert.False(book.Holds("room0001:aaaa0000", 1));
    }

    [Fact]
    public void Corrupt_record_refunds_what_was_actually_taken()
    {
        var stakes = new FakeStakes().Set("Оля", 1000);
        var book = new RouletteBook(stakes, new FakeStore(), defer: a => a());
        book.Open(new PendingSpin("room:0000000a", 3, "roulette-solo", 17, DateTimeOffset.UnixEpoch, [new SpinPay("Оля", 10, 360)]));
        Assert.True(book.Take("Оля", 15, "roulette-bet:roulette-solo", "roulette-bet:room:0000000a:3:оля"));
        Assert.Equal(1, book.Recover(DateTimeOffset.MaxValue));
        Assert.Equal(["grant:Оля:15:roulette-back:room:0000000a:3:оля"], stakes.Calls.Where(c => c.StartsWith("grant")));
        Assert.Equal("roulette-back:roulette-solo", stakes.Reasons["roulette-back:room:0000000a:3:оля"]);
        Assert.Equal(1000, stakes.Balance("Оля"));
    }

    [Fact]
    public void Without_a_ledger_nobody_is_paid_and_the_record_goes()
    {
        var book = new RouletteBook(new NoLedger(), new FakeStore(), defer: a => a());
        book.Open(new PendingSpin("room:0000000b", 1, "roulette", 1, DateTimeOffset.UnixEpoch, [new SpinPay("Оля", 10, 20)]));
        Assert.Equal(1, book.Recover(DateTimeOffset.MaxValue));
        Assert.Empty(book.Pending());
    }

    [Fact]
    public void Real_economy_ledger_pays_once_by_ref()
    {
        using var rig = new EconomyRig();
        rig.Economy.Grant("Оля", 1000, "test", "seed:оля");
        var start = rig.Economy.Balance("Оля");   // поверх тисячі могла лягти ачівка «Сотня»
        var book = new RouletteBook(rig.Economy, rig.GameStore, rig.Clock, defer: a => a());
        var spin = new PendingSpin("room1234:cafebabe", 7, "roulette", 17, rig.Clock.UtcNow, [new SpinPay("Оля", 10, 360), new SpinPay("Петро", 5, 0)]);
        Assert.True(book.Open(spin));
        Assert.Single(book.Pending());
        Assert.True(book.Take("Оля", 10, "roulette-bet:roulette", "roulette-bet:room1234:cafebabe:7:оля"));
        Assert.False(book.Take("Петро", 5, "roulette-bet:roulette", "roulette-bet:room1234:cafebabe:7:петро"));
        Assert.Equal(start - 10, rig.Economy.Balance("Оля"));
        book.Settle(spin);
        book.Settle(spin);
        Assert.Equal(0, book.Recover(DateTimeOffset.MaxValue));   // запис уже прибрано
        Assert.Equal(start + 350, rig.Economy.Balance("Оля"));
        Assert.Equal(0, rig.Economy.Balance("Петро"));
        Assert.Empty(book.Pending());
        var toasts = rig.Outbox.Of<WalletChanged>().Where(w => w.Nick == "Оля").Select(w => w.Text).ToList();
        Assert.Contains("−10 черепків: Рулетка — ставка", toasts);
        Assert.Contains("Лови +360 черепків: Рулетка — виграш", toasts);
        Assert.Single(toasts, t => t.Contains("виграш", StringComparison.Ordinal));
    }

    [Fact]
    public void Wallet_labels_say_roulette_for_both_games()
    {
        using var rig = new EconomyRig();
        Assert.Equal("Рулетка — ставка", rig.Economy.Reason("roulette-bet:roulette"));
        Assert.Equal("Рулетка — ставка", rig.Economy.Reason("roulette-bet:roulette-solo"));
        Assert.Equal("Рулетка — виграш", rig.Economy.Reason("roulette-win:roulette"));
        Assert.Equal("Рулетка — ставку повернуто", rig.Economy.Reason("roulette-back:roulette-solo"));
    }

    [Fact]
    public void Catalog_has_the_five_roulette_achievements()
    {
        var keys = AchievementCatalog.All.Where(a => a.Key.StartsWith("roulette-", StringComparison.Ordinal))
            .ToDictionary(a => a.Key, a => a.Reward);
        Assert.Equal(new Dictionary<string, int>
        {
            ["roulette-straight"] = 15, ["roulette-zero"] = 25, ["roulette-allin"] = 30, ["roulette-red5"] = 20, ["roulette-hopak"] = 20,
        }, keys);
    }

    [Fact]
    public void Both_games_are_in_the_registry_with_their_passports()
    {
        var registry = RoomHarness.NewRegistry();
        var table = registry.Info("roulette")!;
        Assert.Equal(GameGroup.Party, table.Group);
        Assert.Equal((1, 8, 250), (table.MinPlayers, table.MaxPlayers, table.TickMs));
        Assert.True(table.Hidden && table.Coop && !table.Private && !table.Persistent && !table.Rated);
        Assert.Equal(StartMode.Immediate, table.Start);
        Assert.Equal("roulette", table.Module);
        var solo = registry.Info("roulette-solo")!;
        Assert.Equal(GameGroup.Solo, solo.Group);
        Assert.True(solo.Solo && solo.Private && solo.Persistent && !solo.Hidden);
        Assert.Equal(ScoreOrder.HigherIsBetter, solo.Score);
        Assert.Equal("roulette", solo.Module);
        Assert.Equal("Рулетка: сам на сам", solo.Title);
    }

    [Fact]
    public void Setup_registers_the_book_as_a_hosted_service()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IStakes>(new FakeStakes());
        services.AddSingleton<IGameStore>(new FakeStore());
        services.AddSingleton<IClock>(new FakeClock());
        RouletteSetup.AddRoulette(services);
        using var sp = services.BuildServiceProvider();
        var book = sp.GetRequiredService<RouletteBook>();
        Assert.Contains(sp.GetServices<IHostedService>(), s => ReferenceEquals(s, book));
    }

    /// <summary>Ставки без леджера (економіки нема): Moves — null.</summary>
    sealed class NoLedger : IStakes
    {
        public int Balance(string nick) => 0;
        public bool TrySpend(string nick, int amount, string reason, string refKey) => false;
        public void Grant(string nick, int amount, string reason, string refKey) => throw new InvalidOperationException("не мало б платити");
    }
}
