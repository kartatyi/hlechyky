using System.Diagnostics;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Замри!» соло з 🤖 ботами: виклик, старт (двоє, в естафеті — один), боти справді йдуть до глека й стають на
/// «Замри!» (сильний — швидше й рідше попадається за легкого), штурхають викритих, хто кого переміг, без нагород,
/// друг за столом, вихід людини, заміри мозку.
/// </summary>
[Collection(SerialPerf.Name)]
public class FreezeBotTests(ITestOutputHelper output)
{
    static RoomHarness Solo(string lvl = "normal", int seed = 7, string rounds = "1", string mode = "solo")
    {
        var h = new RoomHarness("freeze", new { botlvl = lvl, rounds, mode }, seed: seed);
        h.Join("Оля");
        Assert.True(h.Act(0, "bot", new { on = true }).Ok);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static Freeze G(RoomHarness h) => (Freeze)h.Room.Game;
    static FreezeSeat S(RoomHarness h, int seat) => G(h).SeatForTests(seat);
    static FreezeVillager V(RoomHarness h, int seat) => G(h).CoreForTests.V[S(h, seat).Me];

    static void Go(RoomHarness h)
    {
        for (var i = 0; i < 200 && G(h).Phase != Freeze.PhaseGo; i++) h.Tick();
        Assert.Equal(Freeze.PhaseGo, G(h).Phase);
    }

    /// <summary>До кінця раунду; повертає, скільки тиків він ішов.</summary>
    static int Round(RoomHarness h, Action<int>? each = null)
    {
        var t = 0;
        for (; t < 3000 && G(h).Phase == Freeze.PhaseGo; t++)
        {
            each?.Invoke(t);
            h.Tick();
        }
        return t;
    }

    static int BotX(RoomHarness h) => Math.Max(V(h, 1).X, V(h, 2).X);

    static void ToEnd(RoomHarness h)
    {
        for (var t = 0; t < 20000 && h.Room.Status == RoomStatus.Playing; t++) h.Tick();
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
    }

    [Fact]
    public void Alone_needs_the_bot_two_bots_in_solo_one_in_relay()
    {
        var h = new RoomHarness("freeze");
        h.Join("Оля");
        var r = h.Start();
        Assert.False(r.Ok);
        Assert.Equal(LiveBots.AloneText, r.Message);
        Assert.True(h.Act(0, "bot", new { on = true }).Ok);
        Assert.Equal("[1,2]", h.View(0).GetProperty("bot").GetRawText());
        Assert.True(h.Start().Ok, h.Reply.Message);
        Assert.Equal([1, 2], G(h).BotsForTests);
        Assert.Equal(LiveBots.Name, h.Room.SafeSeatBot(2));

        var relay = Solo(mode: "relay");
        Assert.Equal([1], G(relay).BotsForTests);
        Assert.NotEqual(S(relay, 0).Team, S(relay, 1).Team);          // команди один на один
    }

    [Fact]
    public void Bots_walk_to_the_jug_and_passive_human_loses_without_a_winner()
    {
        var h = Solo("normal", seed: 9);
        Go(h);
        h.Tick(100);
        Assert.Equal(Freeze.PhaseGo, G(h).Phase);                   // з однією людиною раунд не кінчається одразу
        var ticks = Round(h);
        Assert.True(BotX(h) >= FreezeCore.FinishX, $"боти дійшли лише до {BotX(h)}");
        output.WriteLine($"бот торкнувся глека за {ticks * Freeze.TickMs / 1000} с");
        ToEnd(h);
        Assert.Empty(h.Finished.Single().Result.Winners);
        Assert.StartsWith("🤖", h.Room.Result!.Verdict);
        Assert.Empty(h.Scores);
        Assert.Empty(h.Awards);
    }

    [Fact]
    public void Hard_bot_reaches_the_jug_faster_and_is_caught_less_than_easy()
    {
        (int Ticks, int Caught) Race(string lvl)
        {
            int ticks = 0, caught = 0;
            for (var seed = 1; seed <= 6; seed++)
            {
                var h = Solo(lvl, seed);
                Go(h);
                ticks += Round(h);
                caught += S(h, 1).Caught + S(h, 2).Caught;
            }
            return (ticks, caught);
        }
        var hard = Race("hard");
        var easy = Race("easy");
        output.WriteLine($"за 6 раундів: сильний {hard.Ticks} тиків / впіймали {hard.Caught}; легкий {easy.Ticks} / {easy.Caught}");
        Assert.True(hard.Ticks < easy.Ticks, $"сильний {hard} проти легкого {easy}");
        Assert.True(hard.Caught < easy.Caught, $"сильний {hard} проти легкого {easy}");
    }

    [Fact]
    public void Bot_freezes_like_the_crowd_on_normal()
    {
        var h = Solo("normal", seed: 3, rounds: "3");
        var caught = 0;
        for (var r = 0; r < 3 && h.Room.Status == RoomStatus.Playing; r++)
        {
            Go(h);
            Round(h);
            caught += S(h, 1).Caught + S(h, 2).Caught;
            for (var i = 0; i < 200 && G(h).Phase == Freeze.PhaseReveal; i++) h.Tick();
        }
        output.WriteLine($"звичайних ботів впіймали за 3 раунди: {caught}");
        Assert.True(caught <= 6, $"боти попадаються надто часто: {caught}");
    }

    [Fact]
    public void Pusher_is_marked_and_a_bot_that_merely_wobbled_is_cleared()
    {
        var h = Solo("normal", seed: 5);
        Go(h);
        for (var i = 0; i < 400 && !G(h).CoreForTests.PushAllowed; i++) h.Tick();
        var me = V(h, 0);
        var npc = G(h).CoreForTests.V.First(q => q.Owner < 0 && !q.Still && q.Guard == 0);
        npc.X = me.X + 10; npc.Y = me.Y;
        Assert.True(h.Act(0, "push", new { id = npc.Id }).Ok, h.Reply.Message);
        h.Tick();
        Assert.True(G(h).EyeForTests.Sus(me.Id) >= 40);
        Assert.Equal(0, G(h).EyeForTests.Sus(npc.Id));
    }

    [Fact]
    public void Human_past_the_crowd_line_is_pushed_by_a_hard_bot()
    {
        var pushed = 0;
        for (var seed = 1; seed <= 4; seed++)
        {
            var h = Solo("hard", seed);
            Go(h);
            for (var i = 0; i < 400 && G(h).CoreForTests.Baba != FreezeCore.Sing; i++) h.Tick();
            var me = V(h, 0);
            var bot = V(h, 1);
            me.X = FreezeCore.BotMaxX + 4; me.Y = 200;           // стоїть за межею юрби — так може лише гравець
            bot.X = me.X - 24; bot.Y = 200;
            for (var t = 0; t < 200 && V(h, 0).Down == 0 && G(h).Phase == Freeze.PhaseGo; t++) h.Tick();
            if (V(h, 0).Down > 0) pushed++;
        }
        output.WriteLine($"штурхнули людину під хатою: {pushed}/4");
        Assert.True(pushed >= 2);
    }

    [Fact]
    public void Human_who_touches_the_jug_first_wins_without_achievements()
    {
        var h = Solo("easy", seed: 4);
        Go(h);
        for (var i = 0; i < 400 && G(h).CoreForTests.Baba != FreezeCore.Sing; i++) h.Tick();
        V(h, 0).X = FreezeCore.FinishX - 6;
        h.Act(0, "move", new { dir = 0 });
        for (var i = 0; i < 20 && G(h).Phase == Freeze.PhaseGo; i++) h.Tick();
        ToEnd(h);
        Assert.Equal([0], h.Finished.Single().Result.Winners);
        Assert.StartsWith("🏆 Оля — перемога над легким", h.Room.Result!.Verdict);
        Assert.Empty(h.Awards);                                      // «Чистий хід» заслужила б — але з ботами ачівок нема
        Assert.Empty(h.Scores);
    }

    [Fact]
    public void Relay_with_a_bot_finishes_with_a_verdict()
    {
        var h = Solo("normal", seed: 2, mode: "relay");
        ToEnd(h);
        Assert.NotNull(h.Room.Result!.Verdict);
        Assert.DoesNotContain(1, h.Finished.Single().Result.Winners);
        Assert.Empty(h.Scores);
    }

    [Fact]
    public void Friend_at_the_table_sends_the_bots_away()
    {
        var h = new RoomHarness("freeze");
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
        Assert.Equal("hard", Solo("hard").View(0).GetProperty("botLvl").GetString());
    }

    [Fact]
    public void Bot_think_is_cheap()
    {
        double Run(bool bots)
        {
            var h = bots ? Solo("hard", 2, "5") : new RoomHarness("freeze", new { rounds = "5" }, seed: 2);
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
