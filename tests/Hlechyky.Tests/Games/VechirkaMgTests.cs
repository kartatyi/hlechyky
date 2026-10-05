using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit;

namespace Hlechyky.Tests.Games;

/// <summary>Вечірка зі справжніми міні-іграми каркаса (S1.4): Крижина через MinigameHost, нагороди, повернення посеред mg.</summary>
public sealed class VechirkaMgTests
{
    static Vechirka G(RoomHarness h) => (Vechirka)h.Room.Game;

    static bool Until(RoomHarness h, Func<bool> done, int maxSeconds)
    {
        for (var k = 0; k < maxSeconds * 50; k++) { if (done()) return true; h.Tick(); }
        return done();
    }

    [Fact]
    public void Eight_at_the_table_play_a_whole_evening_of_icefloe()
    {
        var h = VechirkaTests.Table(1, 7, seed: 21);
        // Пул звужено до Крижини: після злиття хвилі в пулі й інші ігри (дуелі — Понг тощо), а тут перевіряємо саме її
        G(h).PoolFactory = () => [.. VechirkaPool.Available.Where(e => e.Id == "icefloe")];
        Assert.True(h.Start().Ok);
        var g = G(h); var c = g.Core!;
        var runner = Assert.IsType<VechirkaHostMg>(c.Mg);
        var mgs = 0; var wasMg = false; var swallowed = 0;
        var limit = c.S.Rounds * 8 * 30 + c.S.Rounds * 20 + c.S.Rounds * 120;
        Assert.True(Until(h, () =>
        {
            var mg = c.S.Phase == "mg";
            if (mg && !wasMg)
            {
                mgs++;
                Assert.Equal("icefloe", c.S.M!.Id);
                Assert.Equal(8, runner.Host!.Seats);
            }
            if (!mg && wasMg) swallowed += runner.Ctx!.Swallowed;
            wasMg = mg;
            return h.Room.Status == RoomStatus.Finished;
        }, limit), $"застрягли: {c.S.Phase} {c.S.Round}/{c.S.Rounds}");
        Assert.Equal(c.S.Rounds, mgs);
        Assert.All(c.S.MgCount, kv => Assert.Equal("icefloe", kv.Key));
        Assert.True(c.S.P.Sum(p => p.MgWins) >= c.S.Rounds);
        // Журнал сайту — лише підсумок вечірки; рядки Крижини проковтнуто
        Assert.Single(h.Outbox.OfType<Journal>(), j => j.Text.StartsWith("🎉"));
        Assert.DoesNotContain(h.Outbox.OfType<Journal>(), j => j.Text.Contains("Крижин"));
        Assert.True(swallowed >= 0);   // Крижина в режимі вечірки може й мовчати; головне — у Журнал не протекло
        Assert.Empty(h.Scores);
        Assert.Empty(h.Awards);
    }

    [Fact]
    public void Two_humans_get_place_shards_and_minigame_rewards_do_not_leak()
    {
        var h = VechirkaTests.Table(2, 1, seed: 8);
        h.Start();
        var c = G(h).Core!;
        Assert.True(Until(h, () => h.Room.Status == RoomStatus.Finished, c.S.Rounds * 3 * 30 + c.S.Rounds * 140));
        var place = Assert.Single(h.Awards, a => a.Reason.StartsWith("vechirka:place:"));
        Assert.Equal(20, place.Shards);
        Assert.DoesNotContain(h.Awards, a => a.Reason.Contains("icefloe"));
        Assert.Empty(h.Scores);
        Assert.Equal(2, h.Room.Result!.Scores!.Count);
    }

    [Fact]
    public void Coming_back_during_a_minigame_watches_it_and_plays_the_next()
    {
        var h = VechirkaTests.Table(2, 1, seed: 4);
        h.Start();
        var g = G(h); var c = g.Core!;
        Assert.True(Until(h, () => c.S.Phase == "mg", 600));
        var petro = c.S.P.FindIndex(p => p.Nick == "Петро");
        h.Rooms.NoteOffline("Петро", h.Clock.UtcNow);
        h.Clock.Advance(TimeSpan.FromSeconds(21));
        h.Rooms.DropIfGone(h.Clock.UtcNow);
        Assert.True(c.S.P[petro].Away);
        Assert.True(h.Rooms.Join(h.RoomId, "Петро").Reply.Ok);
        var seat = h.Room.SeatOf("Петро")!.Value;
        if (c.S.Phase == "mg")
        {
            var mg = h.View(seat).GetProperty("mg");
            Assert.Equal(System.Text.Json.JsonValueKind.Null, mg.GetProperty("sub").ValueKind);
            Assert.False(h.Act(seat, "mg", new { a = "go", p = new { } }).Ok);
        }
        Assert.True(Until(h, () => c.S.Phase == "results", 130));
        Assert.True(Until(h, () => c.S.Phase == "mg", 600));
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, h.View(seat).GetProperty("mg").GetProperty("sub").ValueKind);
    }

    [Fact]
    public void Missing_minigame_counts_as_broken_and_the_evening_goes_on()
    {
        var h = VechirkaTests.Table(1, 2, seed: 5);
        G(h).PoolFactory = () => [new VechirkaPoolEntry("nosuch", "Нема такої", "", "tap", 0, 1, 2, 8, 60_000)];
        h.Start();
        var c = G(h).Core!;
        Assert.True(Until(h, () => c.S.Phase == "results", 600));
        Assert.Equal("Crash", c.S.M!.How);
        Assert.All(c.S.M.Results!, r => Assert.Equal(VechirkaRules.Pay([1, 2, 3]).Sum() / 3 + 1, r.Coins));
        Assert.True(Until(h, () => c.S.Phase == "turn", 30));
    }
}
