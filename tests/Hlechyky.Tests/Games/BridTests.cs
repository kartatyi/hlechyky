using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Брід (docs/games/specs/brid.md): брід, стрибки, вода, сліди, раунди й очки, боти, режим вечірки.</summary>
[Collection(SerialPerf.Name)]
public class BridTests
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марко", "Соня", "Тарас", "Леся"];

    // ---------- підмостки ----------

    static RoomHarness Table(int players = 2, int seed = 42)
    {
        var h = new RoomHarness("brid", null, seed);
        foreach (var nick in Nicks.Take(players)) h.Join(nick);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static RoomHarness Solo(string lvl, int seed = 5)
    {
        var h = new RoomHarness("brid", new { botlvl = lvl }, seed);
        h.Join("Оля");
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static Brid Game(RoomHarness h) => (Brid)h.Room.Game;
    static BridCore Core(RoomHarness h) => Game(h).Core;
    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString() ?? "";

    static void ToGo(RoomHarness h)
    {
        for (var i = 0; i < 200 && Game(h).Phase != Brid.PhGo && h.Room.Status == RoomStatus.Playing; i++) h.Tick();
        Assert.Equal(Brid.PhGo, Game(h).Phase);
    }

    /// <summary>Куди ступити, щоб іти рівно стежкою (знає брід — «людина, що грає добре»).</summary>
    static int PathDir(BridCore c, BridRunner r)
    {
        var path = c.Ford!.Path;
        if (r.Y < 0)
        {
            var t = path[0];
            return r.X < t.X ? 0 : r.X > t.X ? 2 : 3;
        }
        var k = PathIndex(c, r);
        if (k < 0 || k == path.Count - 1) return 3;
        var n = path[k + 1];
        for (var d = 0; d < 4; d++) if (BridCore.Dirs[d] == (n.X - r.X, n.Y - r.Y)) return d;
        throw new InvalidOperationException("стежка рветься");
    }

    static int PathIndex(BridCore c, BridRunner r)
    {
        var path = c.Ford!.Path;
        for (var i = 0; i < path.Count; i++) if (path[i] == (r.X, r.Y)) return i;
        return -1;
    }

    /// <summary>Зробити крок стежкою, якщо стоїть і ще не дійшов до каменя <paramref name="stopAt"/>.</summary>
    static void Walk(RoomHarness h, int seat, int stopAt = int.MaxValue)
    {
        var c = Core(h);
        var r = c.Runners[seat];
        if (!r.Standing || PathIndex(c, r) >= stopAt) return;
        Assert.True(h.Act(seat, "step", new { d = PathDir(c, r) }).Ok, h.Reply.Message);
    }

    /// <summary>Небезпечний камінь першого ряду (у ряду 0 безпечний рівно один).</summary>
    static int WrongCol(BridCore c) => c.Ford!.Path[0].X == 0 ? 1 : 0;

    /// <summary>Поставити бігуна на берег у колонку x (тест: без ходьби берегом).</summary>
    static void Place(BridCore c, int seat, int x)
    {
        var r = c.Runners[seat];
        r.X = r.Fx = x;
        r.Y = r.Fy = -1;
    }

    // ---------- брід ----------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Ford_is_a_thin_winding_path_with_dead_ends_and_one_stone_in_the_first_row(int round)
    {
        var (cols, rows) = BridFord.SizeOf(round);
        var sideways = 0;
        var branches = 0;
        for (var seed = 0; seed < 200; seed++)
        {
            var f = new BridFord(cols, rows, new Random(seed));
            var path = f.Path;
            Assert.Equal(0, path[0].Y);
            Assert.Equal(rows - 1, path[^1].Y);
            for (var i = 1; i < path.Count; i++)
            {
                var (dx, dy) = (path[i].X - path[i - 1].X, path[i].Y - path[i - 1].Y);
                Assert.Contains((dx, dy), new[] { (1, 0), (-1, 0), (0, 1) });   // назад стежка не йде
                if (dy == 0) sideways++;
            }
            // Тонка: камінь стежки торкається лише сусідів по стежці (і своїх тупиків).
            var dead = f.Branches.ToHashSet();
            for (var i = 0; i < path.Count; i++)
            {
                var near = BridCore.Dirs.Select(d => (path[i].X + d.Dx, path[i].Y + d.Dy)).Where(p => f.Safe(p.Item1, p.Item2) && !dead.Contains(p)).ToList();
                Assert.All(near, p => Assert.True((i > 0 && p == path[i - 1]) || (i + 1 < path.Count && p == path[i + 1])));
            }
            Assert.Single(Enumerable.Range(0, cols), x => f.Safe(x, 0));
            Assert.DoesNotContain(Enumerable.Range(0, cols), x => f.Safe(x, rows - 1) && path[^1] != (x, rows - 1));
            // Тупик нікуди не веде: з будь-якого каменя тупика до того берега — лише через точку відгалуження.
            foreach (var b in f.Branches) Assert.InRange(b.Y, 1, rows - 2);
            branches += f.Branches.Count;
        }
        Assert.True(sideways > 200 * rows / 5, $"мало звивин: {sideways}");
        Assert.True(branches > 200, $"мало тупиків: {branches}");
    }

    [Fact]
    public void Ford_is_deterministic_by_seed()
    {
        var a = new BridFord(6, 11, new Random(9));
        var b = new BridFord(6, 11, new Random(9));
        Assert.Equal(a.Path, b.Path);
        Assert.Equal(a.Branches, b.Branches);
    }

    // ---------- старт, вид і кадр ----------

    [Fact]
    public void Info_lobby_and_alone()
    {
        var h = new RoomHarness("brid");
        h.Join("Оля");
        Assert.Equal(GameGroup.Live, h.Room.Info.Group);
        Assert.Equal(1, h.Room.Info.MinPlayers);
        Assert.Equal(8, h.Room.Info.MaxPlayers);
        Assert.Contains(h.Room.Info.Options!, o => o.Key == LiveBots.LevelOption.Key);
        Assert.NotEmpty(h.Room.Info.Hint!);
        var v = h.View(0);
        Assert.Equal("lobby", v.GetProperty("phase").GetString());
        Assert.True(v.GetProperty("botOffer").GetBoolean());
        Assert.Equal(5, v.GetProperty("cols").GetInt32());
        Assert.Equal(9, v.GetProperty("rows").GetInt32());
        Assert.Equal(-1, v.GetProperty("frame").GetProperty("p")[0][1].GetInt32());   // уже стоїть на березі
        Assert.Equal(LiveBots.AloneText, h.Start().Message);
    }

    [Fact]
    public void Countdown_then_go_and_view_hides_the_ford()
    {
        var h = Table(2);
        Assert.Equal("ready", Phase(h));
        Assert.False(h.Act(0, "step", new { d = 3 }).Ok);
        ToGo(h);
        var v = h.View(1);
        Assert.Equal("go", v.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("reveal").ValueKind);
        var json = v.GetRawText();
        Assert.DoesNotContain("path", json);
        Assert.DoesNotContain("safe", json);
        Assert.Equal(h.View(null).GetRawText(), h.View(0).GetRawText());   // нічого свого прихованого нема
        var f = v.GetProperty("frame");
        Assert.Equal(9, f.GetProperty("p")[0].GetArrayLength());
        Assert.Equal(JsonValueKind.Null, f.GetProperty("p")[2].ValueKind);
        Assert.Equal(BridCore.RoundTicks, f.GetProperty("left").GetInt32());
    }

    // ---------- дії ----------

    [Fact]
    public void Illegal_steps_are_refused_and_do_not_change_the_view()
    {
        var h = Table(2);
        ToGo(h);
        var c = Core(h);
        Place(c, 0, 0);
        var before = h.View(null).GetRawText();
        Assert.False(h.Act(0, "step", new { d = 1 }).Ok);                 // позаду — берег
        Assert.False(h.Act(0, "step", new { d = 2 }).Ok);                 // лівіше берега нема
        Assert.False(h.Act(0, "step", new { d = 7 }).Ok);
        Assert.False(h.Act(0, "step", new { x = 3, y = 2 }).Ok);          // не сусідній
        Assert.False(h.Act(0, "step", "вперед").Ok);
        Assert.False(h.Act(0, "jump", new { d = 3 }).Ok);
        Assert.False(Game(h).Act(5, "step", JsonSerializer.SerializeToElement(new { d = 3 })).Ok);   // не грає
        Assert.Equal(before, h.View(null).GetRawText());
        Assert.True(h.Act(0, "step", new { x = 0, y = 0 }).Ok);           // тап по сусідньому каменю
        Assert.Equal(BridCore.JumpTicks, c.Runners[0].Jump);
    }

    [Fact]
    public void Wrong_stone_splashes_sinks_and_returns_to_the_bank()
    {
        var h = Table(2);
        ToGo(h);
        var c = Core(h);
        var x = WrongCol(c);
        Place(c, 0, x);
        Assert.True(h.Act(0, "step", new { d = 3 }).Ok);
        h.Tick(BridCore.JumpTicks);
        var r = c.Runners[0];
        Assert.Equal(1, r.Falls);
        Assert.True(r.Wet > 0);
        Assert.True(c.Sunk[c.Idx(x, 0)]);
        Assert.False(h.Act(0, "step", new { d = 0 }).Ok);                 // мокрий — чекай
        var f = h.View(null).GetProperty("frame");
        Assert.Equal([x, 0], f.GetProperty("sk")[0].EnumerateArray().Select(e => e.GetInt32()));
        h.Tick(BridCore.WetTicks);
        Assert.Equal(0, r.Wet);
        Assert.Equal(-1, r.Y);
        Assert.Equal(x, r.X);
        // На затоплений камінь не пускають — вода видно всім.
        Assert.False(h.Act(0, "step", new { d = 3 }).Ok);
        Assert.Equal(0, r.Jump);
    }

    [Fact]
    public void Safe_stone_leaves_a_trail_that_dries_in_five_seconds()
    {
        var h = Table(2);
        ToGo(h);
        var c = Core(h);
        var p0 = c.Ford!.Path[0];
        Place(c, 0, p0.X);
        Assert.True(h.Act(0, "step", new { d = 3 }).Ok);
        h.Tick(BridCore.JumpTicks);
        Assert.Equal(1, c.Runners[0].Best);
        Assert.Equal(0, c.FirstBy[c.Idx(p0.X, 0)]);
        Assert.True(h.Act(0, "step", new { d = 1 }).Ok);                  // назад на берег
        h.Tick(BridCore.JumpTicks);
        var tr = h.View(null).GetProperty("frame").GetProperty("tr");
        Assert.Equal(1, tr.GetArrayLength());
        Assert.Equal(p0.X, tr[0][0].GetInt32());
        Assert.InRange(tr[0][2].GetInt32(), BridCore.TrailTicks - BridCore.JumpTicks - 1, BridCore.TrailTicks);
        h.Tick(BridCore.TrailTicks);
        Assert.Equal(0, h.View(null).GetProperty("frame").GetProperty("tr").GetArrayLength());
    }

    [Fact]
    public void Two_can_stand_on_one_stone_and_a_key_held_in_the_air_is_queued()
    {
        var h = Table(2);
        ToGo(h);
        var c = Core(h);
        var p0 = c.Ford!.Path[0];
        Place(c, 0, p0.X);
        Place(c, 1, p0.X);
        Assert.True(h.Act(0, "step", new { d = 3 }).Ok);
        Assert.True(h.Act(1, "step", new { d = 3 }).Ok);
        Assert.True(h.Act(0, "step", new { d = 1 }).Ok);                  // у повітрі — у чергу
        h.Tick(BridCore.JumpTicks);
        Assert.Equal((p0.X, 0), (c.Runners[1].X, c.Runners[1].Y));
        Assert.Equal(-1, c.Runners[0].Y);                                // уже летить назад
        Assert.Equal(BridCore.JumpTicks, c.Runners[0].Jump);
    }

    // ---------- раунди й партія ----------

    [Fact]
    public void Three_rounds_wider_each_time_points_3_2_1_and_the_winner()
    {
        var h = Table(3, seed: 7);
        var sizes = new List<(int, int)>();
        for (var t = 0; t < 20000 && h.Room.Status == RoomStatus.Playing; t++)
        {
            var g = Game(h);
            if (g.Phase == Brid.PhGo)
            {
                if (sizes.Count < g.RoundNo) sizes.Add((Core(h).Cols, Core(h).Rows));
                // Оля йде одразу, Петро — коли Оля на півдорозі, Ганна — ще пізніше: місця 1, 2, 3.
                Walk(h, 0);
                if (Core(h).Runners[0].Best >= 3 || Core(h).Runners[0].Place > 0) Walk(h, 1);
                if (Core(h).Runners[1].Best >= 3 || Core(h).Runners[1].Place > 0) Walk(h, 2);
            }
            h.Tick();
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([(5, 9), (6, 10), (7, 11)], sizes);
        var res = h.Finished.Single().Result;
        Assert.Equal([0], res.Winners);
        Assert.Equal(9, res.Scores![0]);
        Assert.Equal(6, res.Scores[1]);
        Assert.Equal(3, res.Scores[2]);
        var v = h.View(null);
        Assert.Equal("over", v.GetProperty("phase").GetString());
        Assert.True(v.GetProperty("reveal").GetProperty("path").GetArrayLength() >= 11);
        Assert.Contains("Брід:", res.Text);
    }

    [Fact]
    public void Round_ends_by_time_and_nobody_crossing_is_a_draw()
    {
        var h = Table(2);
        ToGo(h);
        h.Tick(BridCore.RoundTicks);
        Assert.Equal(Brid.PhEnd, Game(h).Phase);
        for (var t = 0; t < 10000 && h.Room.Status == RoomStatus.Playing; t++) h.Tick();
        var res = h.Finished.Single().Result;
        Assert.Empty(res.Winners);
        Assert.Contains("ніхто не перейшов", res.Text);
    }

    [Fact]
    public void Leaving_three_play_on_two_ends_with_the_one_left()
    {
        var h = Table(3);
        ToGo(h);
        h.Leave("Ганна");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.False(Core(h).Runners[2].Plays);
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Finished.Single().Result.Winners);
    }

    [Fact]
    public void Rematch_starts_a_clean_party()
    {
        var h = Table(2);
        for (var t = 0; t < 10000 && h.Room.Status == RoomStatus.Playing; t++)
        {
            if (Game(h).Phase == Brid.PhGo) Walk(h, 1);
            h.Tick();
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal(9, Core(h).Runners[1].Points);
        Assert.True(h.Rematch().Ok, h.Reply.Message);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("ready", Phase(h));
        Assert.Equal(1, Game(h).RoundNo);
        Assert.All(Core(h).Runners.Where(r => r.Plays), r => Assert.Equal((0, -1), (r.Points, r.Y)));
    }

    // ---------- ачівки ----------

    [Fact]
    public void Brave_and_dry_for_the_lone_pioneer_sly_for_the_follower()
    {
        var h = Table(2, seed: 3);
        ToGo(h);
        // Раунд 1: Оля сама розвідує всю стежку й виходить першою.
        for (var t = 0; t < 2000 && Core(h).Runners[0].Place == 0; t++) { Walk(h, 0); h.Tick(); }
        var awards = h.Awards.Where(a => a.Nick == "Оля").Select(a => a.Reason).ToList();
        Assert.Contains("ach:brid-brave", awards);
        Assert.Contains("ach:brid-dry", awards);
        Assert.DoesNotContain("ach:brid-sly", awards);
        // Раунд 2: Оля доходить до останнього каменя й чекає, Петро йде її слідом і виходить першим.
        for (var t = 0; t < 2000 && Game(h).RoundNo < 2 || Game(h).Phase != Brid.PhGo; t++) h.Tick();
        var last = Core(h).Ford!.Path.Count - 1;
        for (var t = 0; t < 3000 && Core(h).Runners[1].Place == 0; t++)
        {
            Walk(h, 0, last);
            if (PathIndex(Core(h), Core(h).Runners[0]) == last) Walk(h, 1);
            h.Tick();
        }
        Assert.Equal(1, Core(h).Runners[1].Place);
        Assert.Contains(h.Awards, a => a.Nick == "Петро" && a.Reason == "ach:brid-sly");
        Assert.DoesNotContain(h.Awards, a => a.Nick == "Петро" && a.Reason == "ach:brid-brave");
        Assert.All(h.Awards, a => Assert.StartsWith("ach:brid-", a.Reason));
    }

    [Fact]
    public void Achievements_are_in_the_catalog()
    {
        foreach (var key in new[] { "brid-dry", "brid-brave", "brid-sly" })
            Assert.Contains(Hlechyky.Games.Economy.AchievementCatalog.All, a => a.Key == key);
    }

    [Fact]
    public void Fall_costs_dry_feet()
    {
        var h = Table(2, seed: 4);
        ToGo(h);
        var c = Core(h);
        Place(c, 0, WrongCol(c));
        Assert.True(h.Act(0, "step", new { d = 3 }).Ok);
        h.Tick(BridCore.JumpTicks + BridCore.WetTicks);
        for (var t = 0; t < 2000 && c.Runners[0].Place == 0; t++) { Walk(h, 0); h.Tick(); }
        Assert.Equal(1, c.Runners[0].Place);
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:brid-dry");
    }

    // ---------- соло з ботами ----------

    [Fact]
    public void Solo_with_bots_seats_two_named_bots_that_cross_the_ford()
    {
        var h = Solo("normal");
        var g = Game(h);
        Assert.Equal([1, 2], g.Bots);
        Assert.StartsWith(LiveBots.Name, g.SeatBot(1));
        Assert.Null(g.SeatBot(0));
        ToGo(h);
        for (var t = 0; t < BridCore.RoundTicks && g.Phase == Brid.PhGo; t++) h.Tick();
        Assert.True(Core(h).Order.Count >= 1 || Core(h).Runners.Skip(1).Take(2).Max(r => r.Best) >= 4, "боти стоять");
        Assert.True(Core(h).Runners[1].Falls + Core(h).Runners[2].Falls > 0 || Core(h).Order.Count > 0);
    }

    [Fact]
    public void Bots_win_against_an_idle_human_with_empty_winners_and_a_verdict_and_no_awards()
    {
        var h = Solo("hard", seed: 8);
        for (var t = 0; t < 20000 && h.Room.Status == RoomStatus.Playing; t++) h.Tick();
        var res = h.Finished.Single().Result;
        Assert.Empty(res.Winners);
        Assert.StartsWith("🤖", res.Verdict);
        Assert.Empty(h.Awards);
    }

    [Fact]
    public void Human_who_knows_the_ford_beats_easy_bots()
    {
        var h = Solo("easy", seed: 2);
        for (var t = 0; t < 20000 && h.Room.Status == RoomStatus.Playing; t++)
        {
            if (Game(h).Phase == Brid.PhGo) Walk(h, 0);
            h.Tick();
        }
        var res = h.Finished.Single().Result;
        Assert.Equal([0], res.Winners);
        Assert.Contains("легкими", res.Verdict);
        Assert.Empty(h.Awards);   // з ботами ачівок нема
    }

    /// <summary>Самі боти у вечірці: скільки тиків до кінця раунду (сума за сідами) — сильні мусять бути помітно швидші.</summary>
    static (long Ticks, int Falls) BotsOnly(LiveBots.Level level)
    {
        long ticks = 0;
        var falls = 0;
        for (var seed = 1; seed <= 8; seed++)
        {
            var h = new PartyHarness("brid", humans: 0, bots: 4, level: level, seed: seed);
            h.Start();
            var g = (Brid)h.Game;
            while (h.Result is null && g.Phase != Brid.PhEnd) h.Tick();
            ticks += g.Core.Rt;
            falls += g.Core.Runners.Sum(r => r.Falls);
            h.RunToEnd();
        }
        return (ticks, falls);
    }

    [Fact]
    public void Hard_bots_cross_noticeably_faster_than_easy()
    {
        var easy = BotsOnly(LiveBots.Level.Easy);
        var hard = BotsOnly(LiveBots.Level.Hard);
        Assert.True(hard.Ticks * 10 < easy.Ticks * 8, $"сильні {hard.Ticks}, легкі {easy.Ticks}");
    }

    [Fact]
    public void Friend_sitting_down_sends_the_bot_away()
    {
        var h = new RoomHarness("brid", null, 1);
        h.Join("Оля");
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        h.Join("Петро");
        Assert.True(h.Start().Ok, h.Reply.Message);
        Assert.Empty(Game(h).Bots);
        Assert.False(Game(h).BotGame);
    }

    // ---------- детермінізм і швидкість ----------

    [Fact]
    public void Same_seed_same_game()
    {
        string Run()
        {
            var h = Solo("normal", seed: 13);
            for (var t = 0; t < 1500; t++) h.Tick();
            return h.View(null).GetProperty("frame").GetRawText() + string.Join(",", Core(h).Ford!.Path);
        }
        Assert.Equal(Run(), Run());
    }

    [Fact, Trait("Category", "Perf")]
    public void Thousand_ticks_with_eight_hard_bots_under_2s()
    {
        var h = new PartyHarness("brid", humans: 0, bots: 8, level: LiveBots.Level.Hard, seed: 5);
        h.Start();
        var sw = Stopwatch.StartNew();
        h.TickSub(1000);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} мс");
    }

    [Fact, Trait("Category", "Perf")]
    public void Thousand_room_ticks_with_eight_humans_under_2s()
    {
        var h = Table(8);
        var sw = Stopwatch.StartNew();
        for (var t = 0; t < 1000 && h.Room.Status == RoomStatus.Playing; t++)
        {
            for (var s = 0; s < 8; s++) if ((t + s) % 7 == 0) h.Input(s, "step", new { d = (t / 7 + s) % 4 });
            h.Tick();
        }
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} мс");
    }

    // ---------- режим вечірки ----------

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(8)]
    public void Party_bots_only_finish_before_the_cap_with_scores_for_all(int bots)
    {
        var h = new PartyHarness("brid", humans: 0, bots: bots, seed: 7);
        h.Start();
        var r = h.RunToEnd();
        Assert.NotNull(r);
        Assert.Equal(MinigameEnd.Finished, r!.How);
        Assert.Equal(bots, r.Scores.Count);
        Assert.Equal(bots, r.Places.Length);
        Assert.NotEmpty(r.Winners);
        Assert.True(h.Clock.UtcNow - h.StartedAt <= TimeSpan.FromMilliseconds(h.Host.CapMs));
        Assert.Equal(0, h.Ctx.Muted);
        Assert.Equal(0, h.Parent.Leaked);
        Assert.Equal(0, h.Parent.Finishes);
        Assert.Equal(1, ((Brid)h.Game).RoundNo);
        Assert.Empty(h.Parent.Says);
    }

    [Fact]
    public void Party_human_who_knows_the_ford_is_on_top_and_idle_human_does_not_stall()
    {
        var h = new PartyHarness("brid", humans: 2, bots: 3, level: LiveBots.Level.Hard, seed: 3);
        h.Start();
        var g = (Brid)h.Game;
        Assert.Equal([2, 3, 4], g.Bots);
        Assert.Equal(5, g.Core.Cols);
        var r = h.RunToEnd(x =>
        {
            var me = g.Core.Runners[0];
            if (g.Phase == Brid.PhGo && me.Standing) x.Act(0, "step", new { d = PathDir(g.Core, me) });
        });
        Assert.NotNull(r);
        Assert.Equal(MinigameEnd.Finished, r!.How);
        Assert.Equal([0], r.Winners);
        Assert.Equal(1, r.Places[0]);
        Assert.Equal(400, r.Scores[0]);                // (5 − 1)·100
        Assert.True(r.Scores[1] < 100);               // людина стояла — лише найдальший ряд (0)
        Assert.Equal(0, h.Ctx.Muted);
    }

    [Fact]
    public void Party_scores_midway_cover_every_seat()
    {
        var h = new PartyHarness("brid", humans: 3, bots: 2, seed: 4);
        h.Start();
        h.TickSub(400);
        var s = ((IPartyMinigame)h.Game).PartyScores();
        Assert.Equal(5, s.Count);
        Assert.All(s.Values, v => Assert.True(v >= 0));
        Assert.Contains("Стрілки", h.Host.Howto);
        Assert.InRange(((IPartyMinigame)h.Game).PartyCapMs, 65_000, 120_000);
    }

    [Fact]
    public void Party_is_deterministic_by_seed()
    {
        string Run()
        {
            var h = new PartyHarness("brid", humans: 0, bots: 6, level: LiveBots.Level.Normal, seed: 11);
            h.Start();
            var r = h.RunToEnd()!;
            return string.Join(",", r.Places) + "|" + string.Join(",", r.Scores.Values) + "|" + h.Clock.UtcNow.ToUnixTimeMilliseconds();
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Party_keys_are_ignored_in_an_ordinary_table()
    {
        var room = new RoomHarness("brid", options: new { party = "1", bots = "1,2" });
        room.Join("Оля");
        Assert.False(((Brid)room.Room.Game).Party);
        Assert.True(PartyPool.Has("brid"));
    }
}
