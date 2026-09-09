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

    [Fact]
    public void All_players_of_a_game_are_written_or_none_are()
    {
        using var temp = new TempDb();
        var store = new EconomyStore(temp.Db);
        var at = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        ResultRow Row(string nick, string outcome) =>
            new("r1", "chess", 1, EconomyStore.Key(nick), nick, outcome, null, null, 0, at);

        Assert.Equal([true, true], store.AddResults([Row("Оля", "win"), Row("Петро", "loss")]));
        // друга та сама подія — жодного свіжого рядка, тож і Ело нема від чого рухати
        Assert.Equal([false, false], store.AddResults([Row("Оля", "win"), Row("Петро", "loss")]));
        Assert.Equal(1, store.CountResults("оля", "chess", null, DateTimeOffset.MinValue));
    }

    [Fact]
    public void Win_streaks_of_many_nicks_come_in_one_query()
    {
        using var temp = new TempDb();
        var store = new EconomyStore(temp.Db);
        var at = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        void Add(string room, string nick, string outcome) => store.AddResult(
            new ResultRow(room, "chess", 1, EconomyStore.Key(nick), nick, outcome, null, null, 0, at));

        Add("r1", "Оля", "win");
        Add("r2", "Оля", "loss");
        Add("r3", "Оля", "win");
        Add("r4", "Оля", "draw");
        Add("r5", "Оля", "win");
        Add("r1", "Петро", "loss");

        var streaks = store.WinStreaks(["оля", "петро", "марта"], 50);
        // нічия серію не рве, поразка — рве
        Assert.Equal(2, streaks["оля"]);
        Assert.Equal(0, streaks["петро"]);
        Assert.Equal(0, streaks["марта"]);
    }
}
