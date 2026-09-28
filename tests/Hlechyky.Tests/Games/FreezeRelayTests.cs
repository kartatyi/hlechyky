using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>«Замри!», прохід №3: естафета до глека (п. 195, опція mode=relay).</summary>
public class FreezeRelayTests
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван"];

    static RoomHarness Table(int players, int seed, string mode = "relay", string rounds = "1")
    {
        var h = new RoomHarness("freeze", new { mode, rounds }, seed: seed);
        foreach (var nick in Nicks.Take(players)) h.Join(nick);
        h.Start();
        for (var i = 0; i < 200 && G(h).Phase != Freeze.PhaseGo; i++) h.Tick();
        Assert.Equal(Freeze.PhaseGo, G(h).Phase);
        G(h).CoreForTests.SetBabaForTests(FreezeCore.Sing, 1_000_000);
        return h;
    }

    static Freeze G(RoomHarness h) => (Freeze)h.Room.Game;
    static FreezeSeat S(RoomHarness h, int seat) => G(h).SeatForTests(seat);
    static FreezeVillager Me(RoomHarness h, int seat) => G(h).CoreForTests.V[S(h, seat).Me];

    /// <summary>Місце доходить до глека: ставимо за крок до порогу й тиснемо «вперед».</summary>
    static void Touch(RoomHarness h, int seat)
    {
        var v = Me(h, seat);
        v.X = FreezeCore.FinishX - 1;
        v.Y = 200;
        v.Moving = false;
        v.JustBack = false;
        h.Input(seat, "move", new { dir = 0 });
        h.Tick();
        h.Input(seat, "move", new { dir = -1 });
    }

    [Fact]
    public void Default_game_is_every_man_for_himself_without_teams()
    {
        var h = Table(2, seed: 3, mode: "solo");
        Assert.False(G(h).Relay);
        Assert.Equal(-1, S(h, 0).Team);
        Touch(h, 0);
        Assert.Equal(Freeze.PhaseReveal, G(h).Phase);   // як і було: перший біля глека бере раунд
    }

    [Fact]
    public void Teams_alternate_by_seat_and_a_touch_scores_for_the_team_and_sends_you_back()
    {
        var h = Table(4, seed: 5);
        Assert.Equal([0, 1, 0, 1], Enumerable.Range(0, 4).Select(i => S(h, i).Team));
        Touch(h, 2);
        Assert.Equal(Freeze.PhaseGo, G(h).Phase);                  // раунд іде далі
        Assert.Equal(1, G(h).TeamPtsForTests(0));
        Assert.Equal(1, S(h, 2).Jugs);
        Assert.True(Me(h, 2).X <= FreezeCore.StartMaxX + FreezeCore.Speed);   // назад до тину
        Assert.Equal(0, S(h, 2).Total);                              // своє +1 — лише на розкритті
        var ev = G(h).CoreForTests.Ev;
        Assert.Contains(ev, e => e[0] == 4 && e[1] == Me(h, 2).Id && e[2] == 0);

        // свої знають одне одного в лице, суперники — ні
        var mates = h.View(0).GetProperty("me").GetProperty("mates").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        Assert.Equal([2, Me(h, 2).Id], mates);
        var relay = h.View(null).GetProperty("relay");
        Assert.Equal(Freeze.RelayGoal, relay.GetProperty("goal").GetInt32());
        Assert.Equal(1, relay.GetProperty("teams")[0].GetProperty("pts").GetInt32());
    }

    [Fact]
    public void First_team_to_the_goal_takes_the_round_and_the_match()
    {
        var h = Table(4, seed: 7);
        for (var k = 0; k < Freeze.RelayGoal - 1; k++) Touch(h, 1);
        Touch(h, 0);
        Assert.Equal(Freeze.PhaseGo, G(h).Phase);
        Touch(h, 3);
        Assert.Equal(Freeze.PhaseReveal, G(h).Phase);
        var r = h.View(null).GetProperty("reveal");
        Assert.Equal("relay", r.GetProperty("why").GetString());
        Assert.Equal([1, 3], r.GetProperty("winners").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(Freeze.RelayGoal - 1 + Freeze.PtRelayRound, S(h, 1).Total);
        Assert.Equal(1 + Freeze.PtRelayRound, S(h, 3).Total);
        Assert.Equal(1, S(h, 0).Total);
        h.Tick(Freeze.RevealTicks + 1);
        Assert.Equal(Freeze.PhaseOver, G(h).Phase);
        var fin = Assert.Single(h.Finished).Result;
        Assert.Contains("Волошки", fin.Text);
        Assert.Equal([1, 3], fin.Winners);
    }

    [Fact]
    public void Two_against_one_the_lone_player_touch_counts_double()
    {
        var h = Table(3, seed: 9);
        Assert.Equal(1, S(h, 1).Team);
        Touch(h, 1);
        Assert.Equal(2, G(h).TeamPtsForTests(1));
        Touch(h, 0);
        Assert.Equal(1, G(h).TeamPtsForTests(0));
    }

    [Fact]
    public void Caught_at_the_jug_is_no_point()
    {
        var h = Table(2, seed: 11);
        var v = Me(h, 0);
        v.X = FreezeCore.FinishX - 1;
        v.Y = 200;
        G(h).CoreForTests.SetBabaForTests(FreezeCore.Watch, 100);
        h.Input(0, "move", new { dir = 0 });
        h.Tick();
        Assert.Equal(0, G(h).TeamPtsForTests(0));
    }
}
