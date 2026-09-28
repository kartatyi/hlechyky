using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hlechyky.Tests.Games;

/// <summary>Прохід №3 (29.09): «Земля росте» (коротші дуелі), рейтингова дуель 1×1 і «Цеглини дня».</summary>
public class BricksDailyDuelTests
{
    static void Later(RoomHarness h, int wallTicks) => h.Clock.AdvanceMs((int)Math.Ceiling(wallTicks * 1000.0 / 60));

    static RoomHarness Table(string id, object? options = null)
    {
        var h = new RoomHarness(id, options: options, seed: 5);
        Assert.True(h.Join("Оля").Ok);
        Assert.True(h.Join("Петро").Ok);
        if (h.Room.Status == RoomStatus.Lobby) Assert.True(h.Start().Ok);
        for (var i = 0; i < 200 && h.View(0).GetProperty("phase").GetString() != Bricks.PhaseGo; i++) h.Tick();
        Assert.Equal(Bricks.PhaseGo, h.View(0).GetProperty("phase").GetString());
        return h;
    }

    [Theory]
    [InlineData("bricks", null, 18_000)]
    [InlineData("bricks", "2", 9_000)]
    [InlineData("bricks-duel", null, 9_000)]
    [InlineData("bricks-duel", "5", 18_000)]
    public void Ground_rises_from_the_chosen_minute(string id, string? ground, int sudden)
    {
        var h = Table(id, ground is null ? null : new { ground });
        Assert.Equal(sudden, ((Bricks)h.Room.Game).SuddenAt);
        Assert.Equal(-1, h.View(0).GetProperty("sd").GetInt32());
    }

    [Fact]
    public void Round_cap_keeps_three_minutes_after_the_ground_starts()
    {
        Assert.Equal(BricksCore.Cap, Bricks.CapOf(Bricks.SuddenOf(Bricks.GroundLong)));
        Assert.Equal(9_000 + 10_800, Bricks.CapOf(Bricks.SuddenOf(Bricks.GroundShort)));
    }

    [Fact]
    public void Duel_is_a_rated_one_on_one_table_drawn_by_the_bricks_module()
    {
        var info = new Registry().Info("bricks-duel")!;
        Assert.True(info.Rated);
        Assert.Equal((2, 2), (info.MinPlayers, info.MaxPlayers));
        Assert.Equal("bricks", info.Client);
        Assert.Equal(Bricks.GroundShort, info.Options!.Single(o => o.Key == "ground").Default);
        Assert.DoesNotContain(info.Options!, o => o.Key == "garbage");
        Assert.False(new Registry().Info("bricks")!.Rated);
    }

    [Fact]
    public void Duel_leaver_gives_the_party_to_the_other_seat_so_elo_has_a_winner()
    {
        var h = Table("bricks-duel");
        Assert.True(h.Leave("Оля").Ok);
        var fin = Assert.Single(h.Finished);
        Assert.True(fin.Info.Rated);
        Assert.Equal([1], fin.Result.Winners);
    }

    [Fact]
    public void Duel_where_both_walls_fall_on_the_same_tick_is_a_draw_for_elo()
    {
        var h = Table("bricks-duel");   // обоє нічого не тиснуть, фігурки однакові — стіни падають разом
        for (var i = 0; i < 20_000 && h.Finished.Count == 0; i++) { Later(h, 6); h.Tick(); }
        var fin = Assert.Single(h.Finished);
        Assert.True(fin.Result.Draw);
        Assert.Empty(fin.Result.Winners);
    }

    // ------------------------------------------------------------------ Цеглини дня

    static RoomHarness Daily(DailyCard? card = null, string nick = "Оля", RoomHarness? reuse = null)
    {
        var sp = new ServiceCollection().AddSingleton(card ?? new DailyCard(null, new FakeClock())).BuildServiceProvider();
        var h = reuse ?? new RoomHarness("bricks-daily", seed: 3, services: sp);
        Assert.True(h.Solo(nick).Ok);
        return h;
    }

    static BricksSprint G(RoomHarness h) => (BricksSprint)h.Room.Game;

    static void Go(RoomHarness h)
    {
        Assert.True(h.Act(0, "ready").Ok);
        for (var i = 0; i < 100 && G(h).Phase != BricksSprint.PhaseGo; i++) h.Tick();
        Assert.Equal(BricksSprint.PhaseGo, G(h).Phase);
    }

    /// <summary>Двійка, що добиває 40 рядів (як у BricksSprintTests).</summary>
    static void Finish(RoomHarness h, int t)
    {
        var c = G(h).SeatState.Core;
        c.Lines = 38;
        c.LoadRows(["8888888800", "8888888800", "8000000000"]);
        c.Clearing = 0;
        c.Spawn(BricksCore.O);
        (c.Bx, c.By, c.LowY) = (7, 10, 10);
        c.Tick = Math.Max(c.Tick, t);
        Assert.True(h.Act(0, "j", new { q = c.Seq + 1, e = new[] { t, BricksCore.KHard } }).Ok);
    }

    [Fact]
    public void Daily_seed_is_the_same_for_everyone_on_the_same_day()
    {
        var a = Daily(nick: "Оля");
        var b = new RoomHarness("bricks-daily", seed: 99);
        Assert.True(b.Solo("Петро").Ok);
        Assert.Equal(a.View(0).GetProperty("seed").GetUInt32(), b.View(0).GetProperty("seed").GetUInt32());
        var sprint = new RoomHarness("bricks-sprint", seed: 3);
        Assert.True(sprint.Solo("Оля").Ok);
        Assert.NotEqual(a.View(0).GetProperty("seed").GetUInt32(), sprint.View(0).GetProperty("seed").GetUInt32());
        Assert.IsAssignableFrom<IDailyGame>(a.Room.Game);
        Assert.StartsWith("daily:bricks-daily:", a.Room.Key);
    }

    [Fact]
    public void Daily_scores_ms_and_attempts_notes_the_card_and_refuses_a_second_run()
    {
        var card = new DailyCard(null, new FakeClock());
        var h = Daily(card);
        Go(h);
        Later(h, BricksSprint.IdleTicks + 60);           // перша спроба — стіну покинуто
        h.Tick();
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Empty(h.Scores);
        Assert.True(h.Rematch("Оля").Ok);
        Go(h);
        Later(h, 6000);
        Finish(h, 6000);
        var score = Assert.Single(h.Scores);
        Assert.Equal(2, score.Attempts);
        Assert.InRange(score.Score, 99_000, 101_000);
        Assert.Contains(h.Awards, a => a.Reason == "daily:bricks-daily");
        var row = Assert.Single(card.Get("bricks-daily")!.Rows);
        Assert.Equal(("Оля", 2, 1), (row.Nick, row.Attempts, row.Streak));
        Assert.Equal(BricksSprint.DoneToday, h.Rematch("Оля").Message);
        var daily = h.View(0).GetProperty("daily");
        Assert.True(daily.GetProperty("solved").GetBoolean());
        Assert.Equal("Оля", daily.GetProperty("board").GetProperty("rows")[0].GetProperty("n").GetString());
    }

    [Fact]
    public void Daily_state_survives_reopening_the_card()
    {
        var h = Daily();
        Go(h);
        Later(h, 5000);
        Finish(h, 5000);
        var saved = h.Room.Game.Save()!;
        var again = new RoomHarness("bricks-daily", seed: 11);
        Assert.True(again.Solo("Оля").Ok);
        again.Room.Game.Load(saved);
        Assert.Equal(BricksSprint.PhaseOver, G(again).Phase);
        Assert.Equal(BricksSprint.DoneToday, again.Act(0, "ready").Message);
        Assert.Equal(JsonValueKind.Number, again.View(0).GetProperty("result").GetProperty("sec").ValueKind);
    }

    [Fact]
    public void Plain_sprint_is_untouched_no_daily_block_and_no_saved_state()
    {
        var h = new RoomHarness("bricks-sprint", seed: 3);
        Assert.True(h.Solo("Оля").Ok);
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("daily").ValueKind);
        Assert.Null(h.Room.Game.Save());
    }
}
