using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>«🤖 Бот у Цеглинах» (пакет 2, spec bricks.md «Пакет 2»): сам — троє ботів, удвох — двоє, утрьох — один.</summary>
public class BricksBotTests(ITestOutputHelper log)
{
    static RoomHarness Table(int humans, object? options = null, int seed = 42, bool bots = true)
    {
        var h = new RoomHarness("bricks", options, seed);
        foreach (var nick in new[] { "Оля", "Петро", "Ганна" }.Take(humans)) h.Join(nick);
        if (bots) Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        return h;
    }

    static Bricks G(RoomHarness h) => (Bricks)h.Room.Game;

    static void ToGo(RoomHarness h)
    {
        for (var i = 0; i < 200 && G(h).Phase != Bricks.PhaseGo; i++) h.Tick();
        Assert.Equal(Bricks.PhaseGo, G(h).Phase);
    }

    static int PlayOut(RoomHarness h, int max = 60000)
    {
        var t = 0;
        for (; t < max && h.Room.Status == RoomStatus.Playing; t++) h.Tick();
        return t;
    }

    [Fact]
    public void Alone_without_bot_cannot_start()
    {
        var h = Table(1, bots: false);
        Assert.Equal(1, h.Room.Info.MinPlayers);
        var r = h.Start();
        Assert.False(r.Ok);
        Assert.Equal(LiveBots.AloneText, r.Message);
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);
        Assert.True(h.View(0).GetProperty("botOffer").GetBoolean());
    }

    [Fact]
    public void Alone_with_bots_plays_a_round_bots_place_clear_and_send_garbage()
    {
        var h = Table(1, new { speed = "fast", ground = "2" });
        Assert.Equal([1, 2, 3], h.View(0).GetProperty("bot").EnumerateArray().Select(x => x.GetInt32()));
        Assert.True(h.Start().Ok, h.Reply.Message);
        Assert.Equal([1, 2, 3], G(h).Bots);
        Assert.Equal("🤖 бот бірюза", G(h).SeatBot(1));
        Assert.Null(G(h).SeatBot(0));
        ToGo(h);
        var view = h.View(0);
        Assert.Equal(4, view.GetProperty("boards").GetArrayLength());
        Assert.Equal("🤖 бот кобальт", view.GetProperty("boards")[3].GetProperty("nk").GetString());

        var ticks = PlayOut(h);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var g = G(h);
        var placed = new[] { 1, 2, 3 }.Select(s => g.SeatState(s).Core.Pi).ToArray();
        var lines = new[] { 1, 2, 3 }.Sum(s => g.SeatState(s).MatchLines);
        var sent = new[] { 1, 2, 3 }.Sum(s => g.SeatState(s).MatchSent);
        log.WriteLine($"{ticks} тиків; фігурок у ботів {string.Join(",", placed)}, рядів {lines}, сміття {sent}; {h.Room.Result!.Verdict}");
        Assert.All(placed, p => Assert.True(p > 20, $"бот поклав лише {p}"));
        Assert.True(lines > 10, $"боти закрили {lines} рядів");
        Assert.True(sent > 0, "боти не надіслали сміття");
        Assert.True(g.SeatState(0).MatchRecv > 0, "людині сміття не долетіло");
        // Сам із ботами — без нагород: winners лише люди, вердикт каже, хто взяв.
        var fin = h.Finished.Single();
        Assert.DoesNotContain(fin.Result.Winners, s => s > 0);
        Assert.Matches("^(🤖 Цеглини взяв 🤖 бот|🏆 Оля — перемога над звичайними ботами|🤝 Оля нарівні)", h.Room.Result!.Verdict);
    }

    [Fact]
    public void Two_people_get_two_bots_four_walls_and_a_bot_can_win_with_its_seat()
    {
        var h = Table(2, new { speed = "fast", ground = "2" }, seed: 7);
        var v = h.View(1);
        Assert.Equal(3 + 1, v.GetProperty("botFill").GetInt32());
        Assert.True(h.Start().Ok, h.Reply.Message);
        Assert.Equal([2, 3], G(h).Bots);
        ToGo(h);
        Assert.Equal(4, h.View(null).GetProperty("boards").GetArrayLength());
        // Люди мовчать — сервер веде їхні стіни, вони падають першими; раунд беруть боти своїми місцями.
        PlayOut(h);
        var fin = h.Finished.Single();
        Assert.Null(h.Room.Result!.Verdict);
        Assert.NotEmpty(fin.Result.Winners);
        Assert.All(fin.Result.Winners, s => Assert.Contains(s, new[] { 2, 3 }));
        Assert.Contains("🤖 бот", h.Room.Result!.Text);
    }

    [Fact]
    public void Three_people_get_one_bot_and_four_people_none()
    {
        var h = Table(3);
        Assert.True(h.Start().Ok, h.Reply.Message);
        Assert.Equal([3], G(h).Bots);

        var full = Table(3, bots: false);
        full.Join("Іван");
        Assert.False(full.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.False(full.View(0).GetProperty("botOffer").GetBoolean());
        Assert.True(full.Start().Ok);
        Assert.Empty(G(full).Bots);
    }

    [Fact]
    public void Person_leaving_a_table_with_bots_does_not_end_the_match()
    {
        var h = Table(2);
        Assert.True(h.Start().Ok);
        ToGo(h);
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        h.Tick(50);
        Assert.True(G(h).SeatState(2).Core.Pi > 1);
        // Пішла й остання людина — партію не дограли.
        h.Leave("Оля");
        Assert.Contains("партію не дограли", h.Finished.Single().Result.Text);
    }

    [Fact]
    public void Bot_toggle_only_between_matches()
    {
        var h = Table(1);
        Assert.True(h.Start().Ok);
        ToGo(h);
        var r = h.Act(0, LiveBots.Toggle, new { on = false });
        Assert.False(r.Ok);
    }

    [Fact]
    public void Levels_differ_in_pace()
    {
        var pieces = new Dictionary<string, double>();
        foreach (var lvl in new[] { "easy", "normal", "hard" })
        {
            var h = Table(1, new { botlvl = lvl, garbage = "none" }, seed: 3);
            Assert.True(h.Start().Ok);
            ToGo(h);
            h.Tick(1500);   // 60 с
            var g = G(h);
            pieces[lvl] = new[] { 1, 2, 3 }.Average(s => g.SeatState(s).Core.Pi);
        }
        log.WriteLine($"фігурок за 60 с: легкий {pieces["easy"]:F1}, звичайний {pieces["normal"]:F1}, сильний {pieces["hard"]:F1}");
        Assert.InRange(pieces["easy"], 30, 45);      // ≈ 1 / 1,6 с
        Assert.InRange(pieces["normal"], 55, 72);    // ≈ 1 / 0,9 с
        Assert.InRange(pieces["hard"], 105, 140);    // ≈ 1 / 0,45 с
    }

    [Fact]
    public void Easy_bot_misses_and_hard_bot_does_not()
    {
        // Той самий хід «найкращий» при pick = 0, і інший — коли бот помиляється.
        var c = new BricksCore(11, BricksCore.ModeNormal, 1800);
        var scratch = new BricksCore();
        while (c.Type < 0) c.Step();
        Assert.Equal(BricksBot.Plan(c, scratch), BricksBot.Plan(c, scratch, 0));
        Assert.NotEqual(BricksBot.Plan(c, scratch, 0), BricksBot.Plan(c, scratch, 3));
        Assert.Equal(0, BricksBotDriver.Hard.Miss);
        Assert.True(BricksBotDriver.Easy.Miss > BricksBotDriver.Normal.Miss);
    }

    [Fact]
    public void Same_seed_same_bot_game()
    {
        int[] Run()
        {
            var h = Table(1, seed: 9);
            Assert.True(h.Start().Ok);
            ToGo(h);
            h.Tick(800);
            return [.. new[] { 1, 2, 3 }.Select(s => (int)G(h).SeatState(s).Core.Hash())];
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Rated_duel_offers_no_bot()
    {
        var h = new RoomHarness("bricks-duel");
        h.Join("Оля");
        Assert.DoesNotContain(h.Room.Info.Options ?? [], o => o.Key == "botlvl");
        Assert.Equal(2, h.Room.Info.MinPlayers);
        Assert.False(h.View(0).GetProperty("botOffer").GetBoolean());
        Assert.False(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.False(h.Start().Ok);
    }
}

[Collection(SerialPerf.Name)]
public class BricksBotPerfTests(ITestOutputHelper output)
{
    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait("Category", "Perf")]
    public void Tick_on_four_walls_with_three_hard_bots_stays_in_budget()
    {
        var h = new RoomHarness("bricks", new { wins = "3", botlvl = "hard" }, seed: 5);
        h.Join("Оля");
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.True(h.Start().Ok);
        var game = (Bricks)h.Room.Game;
        int[] keys = [1, 2, 3, 4, 7, 0, 8, 5, 6, 3, 4, 11, 0, 10];
        var step = 0;
        var seen = 0;
        long bytes = 0, frames = 0;
        var tickTime = TimeSpan.Zero;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 3000; i++)
        {
            if (h.Room.Status == RoomStatus.Finished)
            {
                Assert.True(h.Rematch().Ok);
                game = (Bricks)h.Room.Game;
            }
            var me = Array.IndexOf([.. Enumerable.Range(0, 4).Select(s => h.Room.Seats[s] is not null)], true);
            if (game.Phase == Bricks.PhaseGo && i % 3 == 0 && me >= 0)
            {
                var c = game.SeatState(me).Core;
                if (c.Alive)
                {
                    var t = Math.Max(c.Tick, game.WallTick - 4);
                    h.Input(me, "j", new { q = c.Seq + 1, e = new[] { t, keys[step++ % keys.Length], t + 1, keys[step++ % keys.Length], t + 2, 0 }, g = c.Gseq });
                }
            }
            var t0 = Stopwatch.GetTimestamp();
            h.Tick();
            for (var all = h.Outbox; seen < all.Count; seen++)
            {
                if (all[seen] is not RoomFrame f) continue;
                bytes += JsonSerializer.SerializeToUtf8Bytes(f.Frame, Wire).Length;
                frames++;
            }
            tickTime += Stopwatch.GetElapsedTime(t0);
        }
        sw.Stop();
        var avg = tickTime.TotalMilliseconds / 3000;
        output.WriteLine($"3000 тиків (людина + 3 сильні боти): {sw.Elapsed.TotalMilliseconds:F0} мс; Tick() з кадром у середньому {avg:F4} мс; " +
                         $"кадрів {frames}, середній {(frames == 0 ? 0 : bytes / frames)} Б");
        Assert.True(avg <= 0.25, $"середній тик {avg:F4} мс");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"3000 тиків зайняли {sw.Elapsed}");
    }
}
