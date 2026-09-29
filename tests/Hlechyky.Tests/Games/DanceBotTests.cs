using System.Diagnostics;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Вечорниці» соло з 🤖 ботами: виклик, старт, боти справді танцюють (сильний — помітно краще за легкого) і ляпають
/// підозрілих, хто кого переміг, без нагород, друг за столом, вихід людини, заміри мозку.
/// </summary>
[Collection(SerialPerf.Name)]
public class DanceBotTests(ITestOutputHelper output)
{
    static RoomHarness Solo(string lvl = "normal", int seed = 7, string rounds = "1")
    {
        var h = new RoomHarness("dance", new { botlvl = lvl, rounds }, seed: seed);
        h.Join("Оля");
        Assert.True(h.Act(0, "bot", new { on = true }).Ok);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static Dance G(RoomHarness h) => (Dance)h.Room.Game;
    static DanceSeat S(RoomHarness h, int seat) => G(h).SeatForTests(seat);
    static DanceVillager V(RoomHarness h, int seat) => G(h).CoreForTests.V[S(h, seat).Me];

    static void Go(RoomHarness h)
    {
        for (var i = 0; i < 200 && G(h).Phase != Dance.PhaseGo; i++) h.Tick();
        Assert.Equal(Dance.PhaseGo, G(h).Phase);
    }

    static void Round(RoomHarness h, Action<int>? each = null)
    {
        for (var t = 0; t < 4000 && G(h).Phase == Dance.PhaseGo; t++)
        {
            each?.Invoke(t);
            h.Tick();
        }
    }

    static void ToEnd(RoomHarness h)
    {
        for (var t = 0; t < 20000 && h.Room.Status == RoomStatus.Playing; t++) h.Tick();
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
    }

    [Fact]
    public void Alone_needs_the_bot_then_two_bots_sit_on_free_seats()
    {
        var h = new RoomHarness("dance");
        h.Join("Оля");
        var r = h.Start();
        Assert.False(r.Ok);
        Assert.Equal(LiveBots.AloneText, r.Message);
        Assert.True(h.Act(0, "bot", new { on = true }).Ok);
        Assert.Equal("[1,2]", h.View(0).GetProperty("bot").GetRawText());
        Assert.True(h.Start().Ok, h.Reply.Message);
        Assert.Equal([1, 2], G(h).BotsForTests);
        Assert.Equal(LiveBots.Name, h.Room.SafeSeatBot(1));
        Assert.Equal(LiveBots.Name, h.Room.SafeSeatBot(2));
        Assert.True(S(h, 2).Plays && S(h, 2).Bot && S(h, 2).Me >= 0);
        Assert.Equal("normal", h.View(0).GetProperty("botLvl").GetString());
    }

    [Fact]
    public void Round_with_one_human_does_not_end_at_once_and_bots_dance()
    {
        var h = Solo("normal", seed: 11);
        Go(h);
        h.Tick(100);
        Assert.Equal(Dance.PhaseGo, G(h).Phase);
        var good = 0;
        Round(h, _ => good = S(h, 1).Good + S(h, 2).Good);
        Assert.True(good >= 10, $"боти вціляли лише {good} фігур");
    }

    /// <summary>
    /// Сильний бот танцює помітно краще за легкого — проти того самого манекена, що стоїть і не танцює: частіше вціляє
    /// і швидше витанцьовує стрічку (раунд кінчається раніше).
    /// </summary>
    [Fact]
    public void Hard_bot_dances_better_than_easy()
    {
        (int Good, int Bad, int Ribbons, int Ticks) Dance(string lvl)
        {
            int good = 0, bad = 0, ribbons = 0, ticks = 0;
            for (var seed = 1; seed <= 5; seed++)
            {
                var h = Solo(lvl, seed);
                Go(h);
                int g = 0, b = 0, t0 = G(h).T;
                bool rib = false;
                Round(h, _ =>
                {
                    g = S(h, 1).Good + S(h, 2).Good;
                    b = S(h, 1).Bad + S(h, 2).Bad;
                    rib |= S(h, 1).RibbonAt >= 0 || S(h, 2).RibbonAt >= 0;
                });
                good += g; bad += b; ticks += G(h).T - t0;
                if (rib) ribbons++;
            }
            return (good, bad, ribbons, ticks);
        }
        var hard = Dance("hard");
        var easy = Dance("easy");
        output.WriteLine($"сильний: {hard}, легкий: {easy} (вціляв, схибив, стрічок, тиків)");
        Assert.True(hard.Good * (easy.Good + easy.Bad) > easy.Good * (hard.Good + hard.Bad), "сильний влучає не краще");
    }

    /// <summary>Людина танцює бездоганно, але поза колом — боти (звичайні) рано чи пізно витанцьовують стрічку.</summary>
    [Fact]
    public void Normal_bot_wins_a_ribbon_against_a_clean_dancer_outside_the_circle()
    {
        var ribbons = 0;
        for (var seed = 1; seed <= 4; seed++)
        {
            var h = Solo("normal", seed);
            Go(h);
            Round(h, _ =>
            {
                var g = G(h);
                if (g.CallFig >= 0 && g.T + 1 == g.Beat && !V(h, 0).Danced) h.Act(0, "fig", new { f = g.CallFig });
            });
            if (S(h, 1).RibbonAt >= 0 || S(h, 2).RibbonAt >= 0) ribbons++;
        }
        output.WriteLine($"стрічок у ботів: {ribbons}/4");
        Assert.True(ribbons >= 1);
    }

    [Fact]
    public void Bot_pose_lasts_the_same_frames_as_the_crowd()
    {
        var h = Solo("hard", seed: 3);
        Go(h);
        var bot = V(h, 1);
        int frames = 0, max = 0;
        Round(h, _ =>
        {
            if (bot.Pose > 0) frames++;
            else { max = Math.Max(max, frames); frames = 0; }
        });
        Assert.Equal(DanceCore.PoseTicks - 1, max);
    }

    [Fact]
    public void Hard_bot_hunts_a_twitchy_human_easy_never_does()
    {
        int Kills(string lvl)
        {
            var dead = 0;
            for (var seed = 1; seed <= 6; seed++)
            {
                var h = Solo(lvl, seed);
                Go(h);
                Round(h, t => h.Act(0, "move", new { dir = t / 4 % 2 == 0 ? 0 : 2 }));
                if (!S(h, 0).Alive) dead++;
            }
            return dead;
        }
        int hard = Kills("hard"), easy = Kills("easy");
        output.WriteLine($"вистежили манекена: сильний {hard}/6, легкий {easy}/6");
        Assert.True(hard >= 4, $"сильний вистежив лише {hard}/6");
        Assert.Equal(0, easy);
    }

    [Fact]
    public void Slapper_is_marked_and_normal_bot_answers()
    {
        var h = Solo("normal", seed: 5);
        Go(h);
        var me = V(h, 0);
        var bot = V(h, 1);
        me.X = bot.X; me.Y = bot.Y;
        var npc = G(h).CoreForTests.V.First(q => q.Owner < 0 && q.Upright && DanceCore.Dist2(q, me.X, me.Y) < 150 * 150);
        npc.X = me.X; npc.Y = me.Y;
        Assert.True(h.Act(0, "slap", new { id = npc.Id }).Ok, h.Reply.Message);
        Assert.True(G(h).EyeForTests.Sus(me.Id) >= CrowdEye.Shooter);
        Assert.Equal(0, G(h).EyeForTests.Sus(npc.Id));
        for (var t = 0; t < 800 && S(h, 0).Alive; t++) h.Tick();
        Assert.False(S(h, 0).Alive);
    }

    [Fact]
    public void Passive_human_loses_to_the_bots_without_a_winner()
    {
        var h = Solo("normal", seed: 9);
        ToEnd(h);
        Assert.Empty(h.Finished.Single().Result.Winners);
        Assert.Matches("^(🤖|🤝)", h.Room.Result!.Verdict);
        Assert.Empty(h.Scores);
        Assert.Empty(h.Awards);
    }

    [Fact]
    public void Human_who_slaps_both_bots_wins_but_gets_no_achievements_or_scores()
    {
        var h = Solo("easy", seed: 4);
        Go(h);
        foreach (var b in new[] { 1, 2 })
        {
            var me = V(h, 0);
            var bot = V(h, b);
            for (var i = 0; i < 100 && (S(h, 0).SlapCool > 0 || me.Pose > 0); i++) h.Tick();
            me.X = bot.X; me.Y = bot.Y;
            Assert.True(h.Act(0, "slap", new { id = bot.Id }).Ok, h.Reply.Message);
            Assert.False(S(h, b).Alive);
        }
        ToEnd(h);
        Assert.Equal([0], h.Finished.Single().Result.Winners);
        Assert.StartsWith("🏆 Оля — перемога над легким", h.Room.Result!.Verdict);
        Assert.True(S(h, 0).Eye);
        Assert.Empty(h.Awards);
        Assert.Empty(h.Scores);
    }

    [Fact]
    public void Friend_at_the_table_sends_the_bots_away()
    {
        var h = new RoomHarness("dance");
        h.Join("Оля");
        Assert.True(h.Act(0, "bot", new { on = true }).Ok);
        h.Join("Петро");
        Assert.True(h.Start().Ok, h.Reply.Message);
        Assert.Empty(G(h).BotsForTests);
        Assert.Null(h.Room.SafeSeatBot(2));
    }

    [Fact]
    public void Human_leaving_mid_match_ends_it_without_winners()
    {
        var h = Solo();
        Go(h);
        h.Tick(50);
        h.Leave("Оля");
        Assert.Empty(h.Finished.Single().Result.Winners);
    }

    [Fact]
    public void Level_comes_from_the_table_option()
    {
        Assert.Equal("easy", Solo("easy").View(0).GetProperty("botLvl").GetString());
    }

    [Fact]
    public void Bot_think_is_cheap()
    {
        double Run(bool bots)
        {
            var h = bots ? Solo("hard", 2, "5") : new RoomHarness("dance", new { rounds = "5" }, seed: 2);
            if (!bots)
            {
                h.Join("Оля"); h.Join("Петро"); h.Join("Ганна");
                Assert.True(h.Start().Ok);
            }
            h.Tick(100);
            var sw = Stopwatch.StartNew();
            h.Tick(1000);
            return sw.Elapsed.TotalMilliseconds;
        }
        Run(true);
        double with = Run(true), without = Run(false);
        output.WriteLine($"1000 тиків: з ботами {with:F1} мс, без {without:F1} мс, мозок ≈ {(with - without):F1} мкс/тик");
        Assert.True(with < 1000, "1000 тиків з ботами довше за секунду");
    }
}
