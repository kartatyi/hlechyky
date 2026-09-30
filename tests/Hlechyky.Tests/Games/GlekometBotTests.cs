using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Глекомети соло з ботом (spec glekomet.md, «Соло з ботом»): виклик, пристрілка, рівні, без ачівок і ★ серії.</summary>
public class GlekometBotTests
{
    static RoomHarness Alone(string lvl = "normal", int seed = 42, object? more = null)
    {
        var opts = new Dictionary<string, string> { ["botlvl"] = lvl };
        if (more is not null)
            foreach (var p in more.GetType().GetProperties()) opts[p.Name] = p.GetValue(more)!.ToString()!;
        var h = new RoomHarness("glekomet", opts, seed);
        h.Join("Оля");
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static Glekomet G(RoomHarness h) => (Glekomet)h.Room.Game;

    /// <summary>
    /// Грати до кінця: людина на своєму ході або пропускає (<paramref name="human"/> = null), або стріляє, як підкаже її
    /// «мозок» (той самий GlekometBot — манекен із пристрілкою). Повертає, скільки тиків тривала партія.
    /// </summary>
    static int PlayOut(RoomHarness h, GlekometBot? human, int maxTicks = 60000)
    {
        var flying = false;
        double lastX = 0;
        var t = 0;
        for (; t < maxTicks && h.Room.Status == RoomStatus.Playing; t++)
        {
            var g = G(h);
            var core = g.Core!;
            if (g.Phase == Glekomet.PhaseAim && g.Turn == 0)
            {
                if (human is null) h.Act(0, "skip");
                else if (human.Plan(core, 0, 1, [-1, 0, 0, 0, 0, 0], 6, new Random(t)) is { } s)
                {
                    Assert.True(h.Act(0, "fire", new { a = s.A, p = s.P, w = 0 }).Ok, h.Reply.Message);
                    flying = true;
                }
            }
            h.Tick();
            if (!flying || h.Room.Status != RoomStatus.Playing) continue;
            var any = false;
            foreach (var sh in G(h).Core!.Shells)
                if (sh.Alive && sh.Owner == 0) { any = true; lastX = sh.X; }
            if (!any) { human!.Landed(lastX); flying = false; }
        }
        return t;
    }

    [Fact]
    public void Alone_glekomet_asks_for_a_bot_whose_hut_shows_up_in_the_lobby()
    {
        var h = new RoomHarness("glekomet");
        h.Join("Оля");
        Assert.False(h.Start().Ok);
        Assert.Equal(LiveBots.AloneText, h.Reply.Message);
        Assert.True(h.View(0).GetProperty("botOffer").GetBoolean());
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        var v = h.View(0);
        Assert.Equal(1, v.GetProperty("bot").GetInt32());
        Assert.Equal(LiveBots.Name, v.GetProperty("huts")[1].GetProperty("nick").GetString());
        Assert.True(v.GetProperty("huts")[1].GetProperty("alive").GetBoolean());
        Assert.True(h.Start().Ok, h.Reply.Message);
        var g = G(h);
        Assert.Equal(1, g.Bot);
        Assert.Equal(LiveBots.Name, g.SeatBot(1));
        Assert.True(g.Core!.Huts[1].Plays);
        Assert.False(g.Core!.Huts[2].Plays);                                    // бот один
        Assert.False(h.Act(0, LiveBots.Toggle, new { on = false }).Ok);         // посеред партії не проганяють
    }

    [Fact]
    public void The_bot_shoots_on_its_turn_zeroes_in_and_wins_against_a_skipping_human_without_rewards()
    {
        var h = Alone();
        PlayOut(h, null);
        Assert.True(G(h).Brain.Shots >= 3, $"бот стрельнув лише {G(h).Brain.Shots} разів");
        var fin = h.Finished.Single();
        Assert.Empty(fin.Result.Winners);
        Assert.StartsWith("🤖 Бот переміг", h.Room.Result!.Verdict);
        Assert.Contains("🤖 бот", h.Room.Result!.Text);
    }

    /// <summary>
    /// Пристрілка на рівному полі: бот стріляє справжнім ядром, ми кажемо, де впало, — і так кілька ходів. Вітер — або
    /// тихо, або щоходу інший: промахи з третього ходу мусять бути меншими за перший («на око»).
    /// </summary>
    static (double first, double later) Zeroing(LiveBots.Level lvl, bool windy)
    {
        double first = 0, later = 0;
        int nl = 0;
        const int Seeds = 40;
        for (var seed = 1; seed <= Seeds; seed++)
        {
            var rng = new Random(seed);
            var core = new GlekometCore(new Random(seed));
            core.Flat(100);
            core.Place(0, 150 + rng.Next(200));
            core.Place(1, 700 + rng.Next(200));
            var bot = new GlekometBot();
            bot.Reset(lvl);
            for (var shot = 0; shot < 6; shot++)
            {
                core.Flat(100);
                core.Place(0, core.Huts[0].X);
                core.Place(1, core.Huts[1].X);
                core.Huts[0].Hp = 1000;
                core.Wind = windy ? rng.Next(11) - 5 : 0;
                var s = bot.Plan(core, 1, 0, [-1, 0, 0, 0, 0, 0], 6, rng)!.Value;
                core.Fire(1, s.A, s.P, s.W);
                double x = 0;
                for (var k = 0; k < 400 && core.LiveShells > 0; k++)
                {
                    core.ClearMarks();
                    core.Step();
                    foreach (var sh in core.Shells) if (sh.Alive) x = sh.X;
                    if (core.ExCount > 0) x = core.ExX[0];
                }
                bot.Landed(x);
                var miss = Math.Abs(bot.LastMiss);
                if (shot == 0) first += miss;
                else if (shot >= 2) { later += miss; nl++; }
            }
        }
        return (first / Seeds, later / nl);
    }

    [Theory]
    [InlineData(LiveBots.Level.Easy, false)]
    [InlineData(LiveBots.Level.Normal, false)]
    [InlineData(LiveBots.Level.Normal, true)]
    [InlineData(LiveBots.Level.Hard, true)]
    public void Zeroing_in_the_bot_misses_less_after_the_first_shot(LiveBots.Level lvl, bool windy)
    {
        var (first, later) = Zeroing(lvl, windy);
        Assert.True(later < first * 0.8, $"перший у середньому {first:F0} u, з третього — {later:F0} u");
    }

    [Fact]
    public void On_the_wind_the_hard_bot_zeroes_in_tighter_than_the_easy_one()
    {
        var easy = Zeroing(LiveBots.Level.Easy, true).later;
        var hard = Zeroing(LiveBots.Level.Hard, true).later;
        Assert.True(hard < easy * 0.6, $"сильний мимо на {hard:F0} u, легкий — на {easy:F0} u");
    }

    [Fact]
    public void The_hard_bot_razes_a_skipping_human_faster_than_the_easy_one()
    {
        long Ticks(string lvl)
        {
            long sum = 0;
            for (var seed = 1; seed <= 8; seed++) sum += PlayOut(Alone(lvl, seed, new { water = "0" }), null);
            return sum;
        }
        var easy = Ticks("easy");
        var hard = Ticks("hard");
        Assert.True(hard < easy * 0.8, $"сильний — {hard} тиків, легкий — {easy}");
    }

    [Fact]
    public void A_sharp_human_beats_the_easy_bot_and_gets_no_achievements()
    {
        var wins = 0;
        for (var seed = 1; seed <= 6; seed++)
        {
            var h = Alone("easy", seed, new { water = "0" });
            var human = new GlekometBot();
            human.Reset(LiveBots.Level.Hard);
            PlayOut(h, human);
            var fin = h.Finished.Single();
            if (fin.Result.Winners.SequenceEqual([0]))
            {
                wins++;
                Assert.StartsWith("🏆 Оля — перемога над легким ботом", h.Room.Result!.Verdict);
            }
            Assert.DoesNotContain(h.Awards, a => a.Reason.StartsWith("ach:"));
            Assert.Equal(0, h.View(0).GetProperty("wins")[0].GetInt32());      // ★ серії — не за бота
        }
        Assert.True(wins >= 4, $"людина-снайпер виграла в легкого лише {wins} з 6");
    }

    [Fact]
    public void In_a_volley_the_bot_loads_its_shot_like_everyone()
    {
        var h = Alone("normal", 7, new { mode = "volley" });
        var ready = false;
        for (var t = 0; t < 3000 && !ready; t++)
        {
            h.Tick();
            ready = G(h).Phase == Glekomet.PhaseAim && h.View(0).GetProperty("ready")[1].GetBoolean();
        }
        Assert.True(ready, "бот так і не зарядив залп");
    }

    [Fact]
    public void A_friend_sitting_down_sends_the_bot_away()
    {
        var h = new RoomHarness("glekomet");
        h.Join("Оля");
        h.Act(0, LiveBots.Toggle, new { on = true });
        h.Join("Петро");
        Assert.True(h.Start().Ok, h.Reply.Message);
        var g = G(h);
        Assert.Equal(-1, g.Bot);
        Assert.False(g.BotGame);
        Assert.Null(g.SeatBot(1));
        Assert.Equal("Петро", h.View(0).GetProperty("huts")[1].GetProperty("nick").GetString());
    }

    [Fact]
    public void The_human_leaving_ends_the_bot_game_with_no_winner()
    {
        var h = Alone();
        h.Tick(80);
        h.Leave("Оля");
        Assert.Empty(h.Finished.Single().Result.Winners);
    }

    [Fact]
    public void The_bot_level_comes_from_the_table_option()
    {
        var h = Alone("hard");
        Assert.Equal("hard", h.View(0).GetProperty("botLvl").GetString());
    }

    [Fact]
    public void The_bot_plans_a_shot_cheaply()
    {
        var h = Alone("hard");
        h.Tick(60);
        var core = G(h).Core!;
        var b = new GlekometBot();
        b.Reset(LiveBots.Level.Hard);
        var rng = new Random(1);
        var inv = new[] { -1, 2, 1, 2, 1, 2 };
        b.Plan(core, 1, 0, inv, 6, rng);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++) b.Plan(core, 1, 0, inv, 6, rng);
        sw.Stop();
        // план — раз на хід; навіть так він має бути копійчаним (≲ 0,2 мс)
        Assert.True(sw.Elapsed.TotalMilliseconds < 200, $"1000 планів — {sw.Elapsed.TotalMilliseconds:F0} мс");
    }
}
