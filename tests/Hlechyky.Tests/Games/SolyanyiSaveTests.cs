using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Соляний шлях» переживає перезапуск сервера (контракт §8): Save → нова гра → Configure/Start/Load/Resumed посеред ходу, у фазах
/// setup, draw і keep, в останньому колі дає ті самі види всім місцям і глядачу (строк — зсунутий на паузу), і партію можна дограти.
/// </summary>
public class SolyanyiSaveTests
{
    static readonly TimeSpan Pause = TimeSpan.FromSeconds(47);

    /// <summary>Перезапуск «на місці»: знімок → пауза → нова гра в ту саму кімнату, як це робить RoomsFreeze.Resume.</summary>
    static Solyanyi Restart(RoomHarness h, TimeSpan pause)
    {
        var room = h.Room;
        lock (room.Sync)
        {
            var old = (Solyanyi)room.Game;
            var json = old.Save();
            Assert.False(string.IsNullOrEmpty(json));
            h.Clock.Advance(pause);
            var g = new Solyanyi { Ctx = old.Ctx };
            g.Configure(room.Options);
            g.Start();
            g.Load(json!);
            g.Resumed(pause);
            room.Game = g;
            return g;
        }
    }

    static (string[] Views, DateTimeOffset? Deadline) Look(RoomHarness h)
    {
        DateTimeOffset? deadline = null;
        var list = new List<string>();
        foreach (var seat in new int?[] { 0, 1, 2, 3, null })
        {
            var o = JsonNode.Parse(h.View(seat).GetRawText())!.AsObject();
            if (o["deadline"] is { } d) deadline = d.GetValue<DateTimeOffset>();
            o.Remove("deadline");
            list.Add(o.ToJsonString());
        }
        return ([.. list], deadline);
    }

    /// <summary>Перезапустити й звірити: види ті самі, строк — пізніший рівно на паузу.</summary>
    static void Same(RoomHarness h)
    {
        var (before, dl) = Look(h);
        var game = Restart(h, Pause);
        Assert.Same(game, h.Room.Game);
        var (after, dl2) = Look(h);
        for (var i = 0; i < before.Length; i++) Assert.Equal(before[i], after[i]);
        Assert.Equal(dl + (dl is null ? TimeSpan.Zero : Pause), dl2);
    }

    /// <summary>Двоє людей (0 і 1) і два Глеки (2 і 3).</summary>
    static RoomHarness Table(int seed = 5) => SlRig.Table(2, bots: "2", seed: seed);

    [Fact]
    public void Restart_in_setup_keeps_the_offers_and_who_chose()
    {
        var h = Table();
        var c = SlRig.CoreOf(h);
        var offer = c.Offer[1].ToArray();
        Assert.True(h.Act(0, "keep", new { ids = c.Offer[0].Take(2).ToArray() }).Ok);
        Same(h);
        var g = SlRig.CoreOf(h);
        Assert.NotSame(c, g);
        Assert.Equal(SlPhase.Setup, g.Phase);
        Assert.True(g.Ready[0]);
        Assert.Equal(offer, g.Offer[1]);
        Assert.True(h.Act(1, "keep", new { ids = offer }).Ok);
        Assert.Equal(SlPhase.Play, g.Phase);
    }

    [Fact]
    public void Mid_turn_restart_keeps_every_view_and_the_game_plays_on()
    {
        var h = Table();
        SlRig.Ready(h);
        var c = SlRig.CoreOf(h);
        for (var i = 0; i < 14; i++) SlRig.Step(h, 0);
        while (c.Turn is not (0 or 1) || c.Phase != SlPhase.Play) SlRig.Step(h, 0);
        var hand = c.Hand.Select(x => x.ToArray()).ToList();
        var deck = c.Deck.ToArray();
        Same(h);
        var core = SlRig.CoreOf(h);
        Assert.NotSame(c, core);
        Assert.Equal(hand, core.Hand.Select(x => x.ToArray()));
        Assert.Equal(deck, core.Deck);
        // І на ході Глека (botIn — той самий: строк Глека теж зсунувся).
        while (core.Turn is 0 or 1) SlRig.Step(h, 0);
        Same(h);
        core = SlRig.CoreOf(h);
        for (var i = 0; i < 3000 && h.Finished.Count == 0; i++) SlRig.Step(h, 0);
        Assert.Single(h.Finished);
        SolyanyiBotTests.Invariants(core);
    }

    [Fact]
    public void Restart_in_the_draw_phase_remembers_the_first_pick()
    {
        var h = SlRig.Play(2);
        var c = SlRig.CoreOf(h);
        var s = c.Turn;
        SlRig.Market(c, Hlechyky.Games.Impl.SolyanyiMap.Salt, 1, 2, 3, 4);
        Assert.True(h.Act(s, "draw", new { slot = 0 }).Ok);
        Assert.Equal(SlPhase.Draw, c.Phase);
        Same(h);
        var g = SlRig.CoreOf(h);
        Assert.Equal(SlPhase.Draw, g.Phase);
        Assert.Equal(s, g.Turn);
        Assert.True(h.Act(s, "draw", new { deck = true }).Ok);
        Assert.Contains($"{h.NickOf(s)} бере сіль і карту наосліп", g.Log);
    }

    [Fact]
    public void Restart_in_the_keep_phase_keeps_the_offer()
    {
        var h = SlRig.Play(2);
        var c = SlRig.CoreOf(h);
        var s = c.Turn;
        Assert.True(h.Act(s, "tickets").Ok);
        var offer = c.Offer[s].ToArray();
        Same(h);
        var g = SlRig.CoreOf(h);
        Assert.Equal(SlPhase.Keep, g.Phase);
        Assert.Equal(offer, g.Offer[s]);
        Assert.True(h.Act(s, "keep", new { ids = new[] { offer[0] } }).Ok);
        Assert.Contains(offer[0], g.Tickets[s]);
    }

    [Fact]
    public void Restart_in_the_last_round_keeps_who_started_it_and_how_many_turns_are_left()
    {
        var h = SlRig.Play(3);
        var c = SlRig.CoreOf(h);
        var s = c.Turn;
        c.Wagons[s] = 3;
        SlRig.Give(c, s, (Hlechyky.Games.Impl.SolyanyiMap.Wild, 1));
        Assert.True(h.Act(s, "claim", new { route = 2, c = "wild", w = 1 }).Ok);
        Tip(h);
        Same(h);
        var g = SlRig.CoreOf(h);
        Assert.True(g.Last);
        Assert.Equal(2, g.Left);
        Assert.Equal(s, g.LastBy);
        Tip(h);
        Tip(h);
        Assert.True(g.Over);
        Assert.Equal("wagons", g.Reason);
        Assert.Single(h.Finished);
    }

    static void Tip(RoomHarness h)
    {
        var c = SlRig.CoreOf(h);
        var turns = c.Turns;
        while (c.Turns == turns && !c.Over) Assert.True(SlRig.PlayTip(h, c.Turn, h.View(c.Turn).GetProperty("me").GetProperty("tip")).Ok);
    }

    [Fact]
    public void Resumed_shifts_the_bot_and_the_away_grace()
    {
        var h = Table();
        SlRig.Ready(h);
        var c = SlRig.CoreOf(h);
        while (c.Turn is 0 or 1) SlRig.Step(h, 0);
        var botIn = h.View(0).GetProperty("botIn").GetInt32();
        Restart(h, Pause);
        Assert.Equal(botIn, h.View(0).GetProperty("botIn").GetInt32());
        Assert.Equal("Глек ще думає", h.Act(0, BoardBots.Nudge).Message);
    }

    [Fact]
    public void Glek_host_does_not_replay_history_after_a_restart()
    {
        var h = Table();
        SlRig.Ready(h);
        for (var i = 0; i < 20; i++) SlRig.Step(h, 0);
        var said = h.Outbox.OfType<TableSaid>().Count();
        var room = h.Room;
        lock (room.Sync)
        {
            var old = (Solyanyi)room.Game;
            var json = old.Save()!;
            var g = new Solyanyi { Ctx = old.Ctx };
            g.Configure(room.Options);
            g.Load(json);
            g.Resumed(Pause);
            room.Game = g;
        }
        // Сам Load нічого не каже — події до знімка вже почуті.
        Assert.Equal(said, h.Outbox.OfType<TableSaid>().Count());
    }

    [Fact]
    public void Restored_core_is_identical_including_hidden_owner_only_events()
    {
        var c = SolyanyiBotTests.Playing(4, 12);
        for (var i = 0; i < 120 && !c.Over; i++) SolyanyiBot.Step(c, c.Turn);
        var json = JsonSerializer.Serialize(c.Snapshot());
        var back = SolyanyiCore.FromSnapshot(JsonSerializer.Deserialize<SlCoreSnap>(json)!);
        Assert.Equal(json, JsonSerializer.Serialize(back.Snapshot()));
        SolyanyiBotTests.Invariants(back);
        var wire = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach (var seat in new int?[] { 0, 1, 2, 3, null })
            Assert.Equal(
                JsonSerializer.Serialize(c.Events.Select(e => e.For(seat)).Where(e => e is not null), wire),
                JsonSerializer.Serialize(back.Events.Select(e => e.For(seat)).Where(e => e is not null), wire));
        Assert.Null(new Solyanyi().Save());
        // Дограти можна — з тим самим результатом правил (тасує Rng, тож лише інваріанти й кінець).
        back.Rng = new Random(1);
        back.NameOf = c.NameOf;
        var run = SolyanyiBotTests.Play(back);
        Assert.True(back.Over);
        Assert.True(run.Turns >= c.Turns);
    }

    [Fact]
    public void A_finished_game_round_trips_with_its_longest_roads()
    {
        var c = SolyanyiBotTests.Playing(3, 2);
        SolyanyiBotTests.Play(c, check: false);
        var back = SolyanyiCore.FromSnapshot(JsonSerializer.Deserialize<SlCoreSnap>(JsonSerializer.Serialize(c.Snapshot()))!);
        Assert.True(back.Over);
        Assert.Equal(c.Winners, back.Winners);
        Assert.Equal(c.LongestSeats, back.LongestSeats);
        Assert.Equal(c.LongestLen, back.LongestLen);
        Assert.Equal(c.Order.Select(c.Total), back.Order.Select(back.Total));
        foreach (var s in c.LongestSeats) Assert.Equal(c.LongestRoutes[s], back.LongestRoutes[s]);
    }
}
