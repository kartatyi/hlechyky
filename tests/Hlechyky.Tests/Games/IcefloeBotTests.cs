using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Крижина соло з ботами (spec icefloe.md, «Соло з ботом»): двоє ботів, рівні, берег, без ачівок і серії.</summary>
public class IcefloeBotTests
{
    static RoomHarness Alone(string lvl = "normal", int seed = 42, string wins = "2")
    {
        var h = new RoomHarness("icefloe", new { botlvl = lvl, wins }, seed);
        h.Join("Оля");
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static Icefloe G(RoomHarness h) => (Icefloe)h.Room.Game;

    /// <summary>
    /// Грати до кінця. Людина — манекен (null: стоїть, де поставили) або керована тим самим IcefloeBot
    /// заданого рівня через звичайний ввід (move/dash/throw/chip). Повертає, скільки тиків тривала партія.
    /// </summary>
    static int PlayOut(RoomHarness h, LiveBots.Level? human, int maxTicks = 40000)
    {
        var brain = human is { } l ? new IcefloeBot(l, 1) : null;
        var rng = new Random(7);
        var t = 0;
        for (; t < maxTicks && h.Room.Status == RoomStatus.Playing; t++)
        {
            var c = G(h).Core;
            if (brain is not null && G(h).Phase == Icefloe.PhGo && brain.Due(c.T))
            {
                var m = brain.Think(c, 0, rng);
                if (m.Sector is { } a) h.Input(0, "move", new { a });
                if (m.Dash) h.Input(0, "dash");
                if (m.Throw) h.Input(0, "throw");
                if (m.Chip) h.Input(0, "chip");
            }
            h.Tick();
        }
        return t;
    }

    [Fact]
    public void Alone_the_floe_asks_for_bots_and_two_of_them_take_the_free_seats()
    {
        var h = new RoomHarness("icefloe");
        h.Join("Оля");
        Assert.False(h.Start().Ok);
        Assert.Equal(LiveBots.AloneText, h.Reply.Message);
        Assert.True(h.View(0).GetProperty("botOffer").GetBoolean());
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.Equal([1, 2], h.View(0).GetProperty("bot").EnumerateArray().Select(x => x.GetInt32()));
        Assert.True(h.Start().Ok, h.Reply.Message);
        var g = G(h);
        Assert.Equal([1, 2], g.Bots);
        Assert.True(g.Core.Bodies[1].Plays && g.Core.Bodies[2].Plays && !g.Core.Bodies[3].Plays);
        Assert.Equal("🤖 бот рудий", g.SeatBot(1));                 // двоє ботів — з кольором місця, щоб не плутались
        Assert.StartsWith(LiveBots.Name, g.SeatBot(2));
        Assert.Null(g.SeatBot(0));
        Assert.False(h.Act(0, LiveBots.Toggle, new { on = false }).Ok);
    }

    [Fact]
    public void Teams_are_for_people_only()
    {
        var h = new RoomHarness("icefloe", new { teams = "on" });
        h.Join("Оля");
        h.Act(0, LiveBots.Toggle, new { on = true });
        Assert.False(h.Start().Ok);
        Assert.Equal(Icefloe.TeamsText, h.Reply.Message);
    }

    [Fact]
    public void Bots_push_a_standing_human_off_and_win_without_rewards()
    {
        var h = Alone();
        PlayOut(h, null);
        var fin = h.Finished.Single();
        Assert.Empty(fin.Result.Winners);
        Assert.StartsWith("🤖 Крижину взяв 🤖 бот", h.Room.Result!.Verdict);
        Assert.True(G(h).Core.Bodies[0].Wet);                         // людину таки випхнули
        Assert.Contains("🤖 бот", h.Room.Result!.Text);
    }

    [Fact]
    public void Hard_bots_play_better_than_easy_ones_against_the_same_human()
    {
        // людина — звичайний «мозок»; рахуємо, скільки раундів узяла вона
        int HumanRounds(string lvl)
        {
            var n = 0;
            for (var seed = 1; seed <= 8; seed++)
            {
                var h = Alone(lvl, seed, "3");
                PlayOut(h, LiveBots.Level.Normal);
                n += G(h).Core.Bodies[0].Wins;
            }
            return n;
        }
        var easy = HumanRounds("easy");
        var hard = HumanRounds("hard");
        Assert.True(easy > hard, $"проти легких людина взяла {easy} раундів, проти сильних — {hard}");
    }

    [Fact]
    public void A_sharp_human_beats_easy_bots_and_gets_no_achievements_nor_series()
    {
        var wins = 0;
        for (var seed = 1; seed <= 6; seed++)
        {
            var h = Alone("easy", seed);
            PlayOut(h, LiveBots.Level.Hard);
            if (h.Finished.Single().Result.Winners.SequenceEqual([0]))
            {
                wins++;
                Assert.StartsWith("🏆 Оля — перемога над легкими ботами", h.Room.Result!.Verdict);
            }
            Assert.DoesNotContain(h.Awards, a => a.Reason.StartsWith("ach:"));
            Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("series").ValueKind);
        }
        Assert.True(wins >= 4, $"людина-майстер виграла в легких лише {wins} з 6");
    }

    [Fact]
    public void Hard_bots_rarely_slide_off_on_their_own()
    {
        int selfFalls = 0, falls = 0;
        for (var seed = 1; seed <= 8; seed++)
        {
            var h = Alone("hard", seed);
            var seen = 0;
            for (var t = 0; t < 40000 && h.Room.Status == RoomStatus.Playing; t++)
            {
                h.Tick();
                var by = G(h).Core.ByList;
                if (by.Count < seen) seen = 0;                        // новий раунд
                for (; seen < by.Count; seen++)
                {
                    if (by[seen].Fell == 0) continue;
                    falls++;
                    if (by[seen].By < 0) selfFalls++;
                }
            }
        }
        Assert.True(falls > 0);
        Assert.True(selfFalls * 3 <= falls, $"сильні боти самі шубовснули {selfFalls} з {falls}");
    }

    [Fact]
    public void A_bot_in_the_water_throws_snowballs_from_the_bank()
    {
        var h = Alone("hard", 5);
        for (var i = 0; i < 300 && G(h).Phase != Icefloe.PhGo; i++) h.Tick();
        var b = G(h).Core.Bodies[1];
        b.B.X = 40;
        b.B.Y = 40;
        b.B.Vx = b.B.Vy = 0;
        var thrown = false;
        for (var t = 0; t < 400 && !thrown && G(h).Phase == Icefloe.PhGo; t++)
        {
            h.Tick();
            thrown = G(h).Core.Balls.Any(x => x.On && x.Owner == 1);
        }
        Assert.False(G(h).Core.Bodies[1].Alive);
        Assert.True(thrown, "вибулий бот не кинув жодного сніжка");
    }

    [Fact]
    public void A_friend_sitting_down_sends_the_bots_away()
    {
        var h = new RoomHarness("icefloe");
        h.Join("Оля");
        h.Act(0, LiveBots.Toggle, new { on = true });
        h.Join("Петро");
        Assert.True(h.Start().Ok, h.Reply.Message);
        var g = G(h);
        Assert.Empty(g.Bots);
        Assert.False(g.BotGame);
        Assert.False(g.Core.Bodies[2].Plays);
    }

    [Fact]
    public void The_human_leaving_ends_the_bot_game_with_no_winner()
    {
        var h = Alone();
        h.Tick(120);
        h.Leave("Оля");
        Assert.Empty(h.Finished.Single().Result.Winners);
    }

    [Fact]
    public void The_bot_level_comes_from_the_table_option()
    {
        var h = Alone("easy");
        Assert.Equal("easy", h.View(0).GetProperty("botLvl").GetString());
    }

    [Fact]
    public void Bots_think_cheaply()
    {
        var h = Alone("hard");
        for (var i = 0; i < 300 && G(h).Phase != Icefloe.PhGo; i++) h.Tick();
        var c = G(h).Core;
        var bot = new IcefloeBot(LiveBots.Level.Hard, 0);
        var rng = new Random(1);
        bot.Think(c, 1, rng);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++) bot.Think(c, 1, rng);
        sw.Stop();
        Assert.True(sw.Elapsed.TotalMilliseconds < 200, $"1000 думок — {sw.Elapsed.TotalMilliseconds:F0} мс");
    }
}
