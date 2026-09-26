using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Цеглини: 40 рядів» (docs/games/specs/bricks-sprint.md, bricks.md §2.10, §8.3): соло на час на тому ж рушії.
/// Правила стіни й журналу вже перевіряє <see cref="BricksTests"/>; тут — те, чим спринт відрізняється.
/// </summary>
public class BricksSprintTests
{
    static RoomHarness Solo(int seed = 7, string nick = "Оля")
    {
        var h = new RoomHarness("bricks-sprint", seed: seed);
        Assert.True(h.Solo(nick).Ok);
        return h;
    }

    static BricksSprint Game(RoomHarness h) => (BricksSprint)h.Room.Game;

    static BricksCore Wall(RoomHarness h) => Game(h).SeatState.Core;

    static void Go(RoomHarness h)
    {
        Assert.True(h.Act(0, "ready").Ok);
        for (var i = 0; i < 100 && Game(h).Phase != BricksSprint.PhaseGo; i++) h.Tick();
        Assert.Equal(BricksSprint.PhaseGo, Game(h).Phase);
    }

    static void Later(RoomHarness h, int wallTicks) => h.Clock.AdvanceMs((int)Math.Ceiling(wallTicks * 1000.0 / 60));

    static ActResult J(RoomHarness h, params int[] e) => h.Act(0, "j", new { q = Wall(h).Seq + 1, e });

    /// <summary>Двійка на тику <paramref name="t"/>: дві смуги з діркою 8–9 і O туди.</summary>
    static void Double(RoomHarness h, int t)
    {
        var c = Wall(h);
        c.LoadRows(["8888888800", "8888888800", "8000000000"]);
        c.Clearing = 0;
        c.Spawn(BricksCore.O);
        (c.Bx, c.By, c.LowY) = (7, 10, 10);
        c.Tick = Math.Max(c.Tick, t);                 // стіна вже на цьому тику — гравітація не встигне кинути O сама
        Assert.True(J(h, t, BricksCore.KHard).Ok);
    }

    [Fact]
    public void Sprint_opens_immediately_in_ready_and_ticks_do_nothing_until_ready()
    {
        var h = Solo();
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(BricksSprint.PhaseReady, Game(h).Phase);
        h.Tick();
        var frames = h.Outbox.OfType<RoomFrame>().Count();
        h.Tick(50);
        Assert.Equal(frames, h.Outbox.OfType<RoomFrame>().Count());
        Assert.Equal(0, Wall(h).Tick);
        Assert.Equal(19, Wall(h).By + (Wall(h).Type == BricksCore.I ? 1 : 0));   // фігурка на появі, не падає
        Assert.Equal("Партія ще не почалась", J(h, 0, 1).Message);
        Assert.Equal("Зараз нема чого звіряти", h.Act(0, "sync").Message);
        var v = h.View(0);
        Assert.Equal("ready", v.GetProperty("phase").GetString());
        Assert.Equal(1, v.GetProperty("boards").GetArrayLength());
        Assert.Equal(0, v.GetProperty("target").GetArrayLength());
        Assert.Equal(("none", 1, 0), (v.GetProperty("garbage").GetString(), v.GetProperty("need").GetInt32(), v.GetProperty("stage").GetInt32()));
        Assert.Equal(Views.Text(h.Room.Game.View(null)), Views.Text(h.Room.Game.View(0)));
    }

    [Fact]
    public void Ready_starts_a_75_tick_countdown_then_the_clock_runs_from_zero()
    {
        var h = Solo();
        Assert.True(h.Act(0, "ready").Ok);
        Assert.Equal("Уже почали", h.Act(0, "ready").Message);
        Assert.Equal("Зачекай, зараз почнемо", J(h, 0, 1).Message);
        h.Tick(74);
        Assert.Equal(BricksSprint.PhaseStart, Game(h).Phase);
        Assert.Equal(0, Game(h).WallTick);
        h.Tick();
        Assert.Equal(BricksSprint.PhaseGo, Game(h).Phase);
        Assert.Equal(0, Game(h).WallTick);
        Later(h, 120);
        Assert.InRange(Game(h).WallTick, 120, 121);
        Assert.True(J(h, 30, BricksCore.KLeft, 40, BricksCore.KLeftUp).Ok);
        Assert.Equal(BricksCore.SprintG, Wall(h).Gravity);
        Assert.Equal(0, Wall(h).Garbage == BricksCore.ModeNone ? 0 : 1);
    }

    [Fact]
    public void Fortieth_line_finishes_with_score_in_seconds_and_a_journal_line()
    {
        var h = Solo();
        Go(h);
        Wall(h).Lines = 38;
        Later(h, 600);
        Double(h, 600);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var score = Assert.Single(h.Scores);
        Assert.Equal(10.0, score.Score);
        Assert.Equal(ScoreOrder.LowerIsBetter, score.Order);
        Assert.Equal("Оля", score.Nick);
        var fin = Assert.Single(h.Finished);
        Assert.Equal([0], fin.Result.Winners);
        Assert.Equal("Цеглини: Оля — 40 рядів за 0:10,00", fin.Result.Text);
        Assert.Equal(600, fin.Result.Scores![0]);
        var v = h.View(0);
        Assert.Equal("over", v.GetProperty("phase").GetString());
        Assert.Equal(10.0, v.GetProperty("result").GetProperty("sec").GetDouble());
        Assert.Equal(40, v.GetProperty("result").GetProperty("lines")[0].GetInt32());
    }

    [Fact]
    public void Official_time_is_never_more_than_half_a_second_faster_than_the_wall_clock()
    {
        var h = Solo();
        Go(h);
        Wall(h).Lines = 38;
        Later(h, 3000);
        Double(h, 100);                                  // клієнт каже «тик 100», а годинник сервера — 3000
        var score = Assert.Single(h.Scores);
        Assert.Equal(Math.Round((3000 - BricksSprint.Slack) / 60.0, 2), score.Score);
        Assert.Equal(2970, h.Finished.Single().Result.Scores![0]);
    }

    [Fact]
    public void Under_two_minutes_asks_for_the_sprint_achievement()
    {
        var fast = Solo();
        Go(fast);
        Wall(fast).Lines = 38;
        Later(fast, 7100);
        Double(fast, 7100);
        Assert.Contains(fast.Awards, a => a.Reason == "ach:bricks-sprint-2m" && a.Nick == "Оля" && a.Shards == 0);

        var slow = Solo(seed: 8);
        Go(slow);
        Wall(slow).Lines = 38;
        Later(slow, 7300);
        Double(slow, 7300);
        Assert.Single(slow.Scores);
        Assert.DoesNotContain(slow.Awards, a => a.Reason == "ach:bricks-sprint-2m");
    }

    [Fact]
    public void Top_out_before_forty_finishes_without_a_score()
    {
        var h = Solo();
        Go(h);
        var c = Wall(h);
        c.Lines = 23;
        c.LoadRows(Enumerable.Repeat("8888888880", 20).ToArray());
        c.Spawn(BricksCore.T);
        Assert.True(J(h, 0, BricksCore.KHard).Ok);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Empty(h.Scores);
        var fin = Assert.Single(h.Finished);
        Assert.Empty(fin.Result.Winners);
        Assert.Equal("Цеглини: Оля — стіна впала, 23 ряди з 40", fin.Result.Text);
        Assert.Equal("Партію зіграно, тисни «Ану ще раз»", h.Act(0, "j", new { q = 2, e = new[] { 1, 1 } }).Message);
    }

    [Fact]
    public void Sixty_silent_seconds_abandon_the_sprint_without_a_score()
    {
        var h = Solo();
        Go(h);
        Later(h, 3000);
        Assert.True(J(h, 2990, BricksCore.KCw).Ok);        // справжній натиск — лічильник тиші з нуля
        (Wall(h).Type, Wall(h).Clearing) = (-1, 1_000_000); // стіна просто стоїть — інакше сервер-ведення завалило б її раніше
        Later(h, 3400);
        h.Tick();
        Assert.Equal(RoomStatus.Playing, h.Room.Status);   // пульсів і сервер-ведення мало: треба натискати
        Later(h, 300);
        h.Tick();
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Empty(h.Scores);
        Assert.Equal("Цеглини: Оля — стіну покинуто", h.Finished.Single().Result.Text);
    }

    [Fact]
    public void Rematch_gives_a_fresh_wall_and_seed_in_ready()
    {
        var h = Solo();
        Go(h);
        var seed = h.View(0).GetProperty("seed").GetUInt32();
        Wall(h).Lines = 38;
        Later(h, 900);
        Double(h, 900);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(h.Rematch().Ok);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(BricksSprint.PhaseReady, Game(h).Phase);
        var v = h.View(0);
        Assert.NotEqual(seed, v.GetProperty("seed").GetUInt32());
        Assert.Equal(0, Wall(h).Lines);
        Assert.Equal(0, Wall(h).Tick);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, v.GetProperty("result").ValueKind);
        Go(h);
        Assert.Equal(0, Game(h).WallTick);
    }

    [Fact]
    public void Sprint_is_listed_as_solo_lower_is_better_and_shares_the_bricks_module()
    {
        var info = new Registry().Info("bricks-sprint")!;
        Assert.Equal((GameGroup.Solo, 1, 1, 40), (info.Group, info.MinPlayers, info.MaxPlayers, info.TickMs));
        Assert.Equal(StartMode.Immediate, info.Start);
        Assert.Equal(ScoreOrder.LowerIsBetter, info.Score);
        Assert.False(info.Private);
        Assert.False(info.Persistent);
        Assert.Equal("bricks", info.Module);
        var cat = Assert.Single(new Registry().Catalog, g => g.Id == "bricks-sprint");
        Assert.True(cat.HasCss);
        Assert.False(cat.Daily);
        Assert.DoesNotContain("Тетріс", info.Hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Clock_and_row_words_read_like_people_write_them()
    {
        Assert.Equal("1:37,35", BricksSprint.Clock(97.35));
        Assert.Equal("0:05,00", BricksSprint.Clock(5));
        Assert.Equal("12:00,01", BricksSprint.Clock(720.01));
        Assert.Equal(97.35, BricksSprint.Seconds(5841));
        Assert.Equal(["1 ряд", "2 ряди", "5 рядів", "11 рядів", "21 ряд", "23 ряди", "40 рядів"],
            new[] { 1, 2, 5, 11, 21, 23, 40 }.Select(BricksSprint.Rows));
    }

    [Fact]
    public void Journal_and_frames_work_the_same_as_in_the_duel()
    {
        var h = Solo();
        Go(h);
        h.Tick(2);
        var was = h.Outbox.OfType<RoomFrame>().Count();
        h.Tick(3);
        Assert.Equal(was, h.Outbox.OfType<RoomFrame>().Count());       // нічого не змінилось — кадру нема
        var local = new BricksCore(h.View(0).GetProperty("seed").GetUInt32(), BricksCore.ModeNone, 0);
        local.AdvanceTo(5); local.Apply(BricksCore.KCw); local.AdvanceTo(9); local.Apply(BricksCore.KHard);
        Assert.True(h.Act(0, "j", new { q = 1, e = new[] { 5, 7, 9, 10 }, h = local.Hash(), g = 0, f = 0 }).Ok);
        Assert.Equal(local.Hash(), Wall(h).Hash());
        Assert.False(Game(h).SeatState.NeedFix);
        h.Tick();
        var f = Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);
        var b = Assert.Single(f.GetProperty("b").EnumerateArray());
        Assert.Equal(local.Hash(), b.GetProperty("x").GetUInt32());
        Assert.True(b.TryGetProperty("r", out _));
        Assert.Equal("Журнал із майбутнього", h.Act(0, "j", new { q = 3, e = new[] { 500, 0 } }).Message);
        Assert.Equal("Тут так не ходять", h.Act(0, "move").Message);
    }

    [Fact]
    public void Leaving_the_sprint_just_closes_it()
    {
        var h = Solo();
        Go(h);
        Assert.True(h.Leave("Оля").Ok);
        Assert.Null(h.Rooms.Find(h.RoomId));
        Assert.Empty(h.Scores);
    }

    [Fact]
    public void Lost_fix_in_the_sprint_is_sent_again_too()
    {
        var h = Solo();
        Go(h);
        Assert.True(h.Act(0, "sync").Ok);
        h.Tick();
        var st = Game(h).SeatState;
        Assert.Equal(1, st.Epoch);
        h.Tick(2 * BricksJournal.FixEvery);
        // клієнт і далі шле пачки зі старою епохою — кадр із fix не дійшов; сервер шле fix іще раз
        Assert.True(h.Act(0, "j", new { q = 1, e = new[] { 3, 1 }, f = 0 }).Ok);
        Assert.True(st.NeedFix);
        var was = h.Outbox.OfType<RoomFrame>().Count();
        h.Tick();
        var fix = h.Outbox.OfType<RoomFrame>().Skip(was).Select(f => Views.Json(f.Frame))
            .SelectMany(f => f.GetProperty("ev").EnumerateArray()).Single(e => e[0].GetString() == "f");
        Assert.Equal(2, fix[2].GetProperty("fx").GetInt32());
    }
}
