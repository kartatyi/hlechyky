using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>Сховище станів Persistent-ігор і лічильники економіки.</summary>
public class StoreTests
{
    [Fact]
    public void State_survives_save_and_load()
    {
        using var rig = new EconomyRig();
        IGameStore store = rig.GameStore;

        Assert.Null(store.LoadState("daily:wordle:2026-09-10:оля"));
        store.SaveState("daily:wordle:2026-09-10:оля", """{"guesses":["калина"]}""");
        Assert.Equal("""{"guesses":["калина"]}""", store.LoadState("daily:wordle:2026-09-10:оля"));
    }

    [Fact]
    public void Saving_the_same_key_overwrites()
    {
        using var rig = new EconomyRig();
        IGameStore store = rig.GameStore;
        store.SaveState("clicker:оля", """{"pots":1}""");
        store.SaveState("clicker:оля", """{"pots":2}""");
        Assert.Equal("""{"pots":2}""", store.LoadState("clicker:оля"));
    }

    [Fact]
    public void Delete_removes_the_state()
    {
        using var rig = new EconomyRig();
        IGameStore store = rig.GameStore;
        store.SaveState("clicker:оля", "{}");
        store.DeleteState("clicker:оля");
        Assert.Null(store.LoadState("clicker:оля"));
        store.DeleteState("clicker:оля");   // повторне видалення нікого не турбує
    }

    [Fact]
    public void Keys_of_different_players_do_not_mix()
    {
        using var rig = new EconomyRig();
        IGameStore store = rig.GameStore;
        store.SaveState("clicker:оля", "1");
        store.SaveState("clicker:петро", "2");
        Assert.Equal("1", store.LoadState("clicker:оля"));
        Assert.Equal("2", store.LoadState("clicker:петро"));
    }

    [Fact]
    public void Counters_add_up_per_day()
    {
        using var rig = new EconomyRig();
        Assert.Equal(1, rig.Store.Bump("оля", "online", "2026-09-10", 1));
        Assert.Equal(3, rig.Store.Bump("оля", "online", "2026-09-10", 2));
        Assert.Equal(1, rig.Store.Bump("оля", "online", "2026-09-11", 1));
        Assert.Equal(3, rig.Store.Counter("оля", "online", "2026-09-10"));
        Assert.Equal(0, rig.Store.Counter("петро", "online", "2026-09-10"));
    }

    [Fact]
    public void Ticker_pays_a_shard_for_every_ten_minutes_online()
    {
        using var rig = new EconomyRig();
        rig.Presence.Set("conn1", "Оля");

        for (var i = 0; i < 9; i++) rig.Ticker.Minute();
        Assert.Equal(0, rig.Economy.Balance("Оля"));

        rig.Ticker.Minute();
        Assert.Equal(1, rig.Economy.Balance("Оля"));

        for (var i = 0; i < 10; i++) rig.Ticker.Minute();
        Assert.Equal(2, rig.Economy.Balance("Оля"));
    }

    [Fact]
    public void Ticker_stops_at_the_daily_cap()
    {
        using var rig = new EconomyRig();
        rig.Presence.Set("conn1", "Оля");
        for (var i = 0; i < 10 * 20; i++) rig.Ticker.Minute();
        Assert.Equal(rig.Options.ListenDailyCap, rig.Economy.Balance("Оля"));
    }

    [Fact]
    public void Two_tabs_of_one_nick_are_one_listener()
    {
        using var rig = new EconomyRig();
        rig.Presence.Set("conn1", "Оля");
        rig.Presence.Set("conn2", "оля");
        for (var i = 0; i < 10; i++) rig.Ticker.Minute();
        Assert.Equal(1, rig.Economy.Balance("Оля"));
    }

    [Fact]
    public void Ticker_counts_lifetime_minutes_for_the_listener_achievements()
    {
        using var rig = new EconomyRig();
        rig.Presence.Set("conn1", "Оля");
        for (var i = 0; i < 5; i++) rig.Ticker.Minute();
        rig.Clock.Advance(TimeSpan.FromHours(24));
        for (var i = 0; i < 5; i++) rig.Ticker.Minute();

        Assert.Equal(10, rig.Store.Counter("оля", "online-total", EconomyTicker.AllDays));
        Assert.Equal(5, rig.Store.Counter("оля", "online", Days.Today(rig.Clock)));
    }

    [Fact]
    public void Nobody_online_costs_nothing()
    {
        using var rig = new EconomyRig();
        rig.Ticker.Minute();
        Assert.Empty(rig.Outbox.All);
    }
}
