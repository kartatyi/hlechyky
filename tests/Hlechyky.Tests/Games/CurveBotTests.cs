using System.Diagnostics;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>Кривуля соло з ботами (spec curve.md, «Соло з ботом»): троє ботів, рівні, без нагород.</summary>
public class CurveBotTests(ITestOutputHelper log)
{
    static RoomHarness Alone(string lvl = "normal", int seed = 42, object? more = null)
    {
        var h = new RoomHarness("curve", more ?? new { botlvl = lvl }, seed);
        h.Join("Оля");
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static CurveGame G(RoomHarness h) => (CurveGame)h.Room.Game;

    /// <summary>
    /// Грати до кінця. Людина — манекен (null: не кермує, їде прямо в стіну) або керована тим самим CurveBot
    /// заданого рівня через звичайний ввід <c>turn</c>. Повертає, скільки тиків тривала партія.
    /// </summary>
    static int PlayOut(RoomHarness h, LiveBots.Level? human, int maxTicks = 80000)
    {
        var brain = human is { } l ? new CurveBot(l, 1) : null;
        var rng = new Random(7);
        var t = 0;
        for (; t < maxTicks && h.Room.Status == RoomStatus.Playing; t++)
        {
            var f = G(h).Field;
            if (brain is not null && f.Heads[0].Alive && brain.Due(f.RoundTicks))
                h.Input(0, "turn", new { d = brain.Think(f, 0, rng) });
            h.Tick();
        }
        return t;
    }

    [Fact]
    public void Alone_the_field_asks_for_bots_and_three_of_them_take_the_free_seats()
    {
        var h = new RoomHarness("curve");
        h.Join("Оля");
        Assert.False(h.Start().Ok);
        Assert.Equal(LiveBots.AloneText, h.Reply.Message);
        Assert.True(h.View(0).GetProperty("botOffer").GetBoolean());
        Assert.False(h.Act(0, "turn", new { d = 1 }).Ok);            // у лобі кермувати ще нічим
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.Equal([1, 2, 3], h.View(0).GetProperty("bot").EnumerateArray().Select(x => x.GetInt32()));
        Assert.True(h.Start().Ok, h.Reply.Message);
        var g = G(h);
        Assert.Equal([1, 2, 3], g.Bots);
        Assert.True(g.Field.Heads[1].Present && g.Field.Heads[3].Present && !g.Field.Heads[4].Present);
        Assert.Equal("🤖 бот зелений", g.SeatBot(1));
        Assert.StartsWith(LiveBots.Name, g.SeatBot(3));
        Assert.Null(g.SeatBot(0));
        Assert.Null(g.SeatBot(4));
        Assert.Equal(30, h.View(0).GetProperty("target").GetInt32());   // «10 на суперника» — і з ботами
        Assert.False(h.Act(0, LiveBots.Toggle, new { on = false }).Ok);  // посеред партії бота не проженеш
    }

    [Fact]
    public void Bots_really_drive_and_outlive_a_human_who_does_not_steer()
    {
        var h = Alone();
        var f = G(h).Field;
        h.Tick(CurveCore.ReadyTicks + 5);
        var start = Enumerable.Range(1, 3).Select(s => (f.Heads[s].X, f.Heads[s].Y, f.Heads[s].A)).ToArray();
        for (var i = 0; i < 400 && f.Heads[0].Alive; i++) h.Tick();
        Assert.False(f.Heads[0].Alive);                                // манекен доїхав до стіни (чи до чийогось сліду)
        var alive = Enumerable.Range(1, 3).Count(s => f.Heads[s].Alive);
        Assert.True(alive >= 1, "боти мали б пережити 6 секунд на порожньому полі");
        // рухаються і кермують — не лише прямо
        for (var i = 0; i < 3; i++)
            Assert.True(Math.Abs(f.Heads[i + 1].X - start[i].X) + Math.Abs(f.Heads[i + 1].Y - start[i].Y) > 10);
    }

    [Fact]
    public void Bots_win_against_a_passive_human_without_rewards()
    {
        var h = Alone();
        var ticks = PlayOut(h, null);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var fin = h.Finished.Single();
        Assert.Empty(fin.Result.Winners);
        Assert.StartsWith("🤖 Кривулю взяв 🤖 бот", h.Room.Result!.Verdict);
        Assert.Contains("🤖 бот", h.Room.Result!.Text);
        Assert.DoesNotContain(h.Awards, a => a.Reason.StartsWith("ach:"));
        Assert.Empty(h.Scores);
        log.WriteLine($"боти проти манекена: {ticks} тиків");
    }

    [Fact]
    public void A_skilled_human_beats_easy_bots()
    {
        var won = 0;
        for (var seed = 1; seed <= 3; seed++)
        {
            var h = Alone("easy", seed);
            var ticks = PlayOut(h, LiveBots.Level.Hard);
            Assert.Equal(RoomStatus.Finished, h.Room.Status);
            if (h.Finished.Single().Result.Winners.SequenceEqual([0]))
            {
                won++;
                Assert.StartsWith("🏆 Оля — перемога над легкими ботами", h.Room.Result!.Verdict);
            }
            log.WriteLine($"seed {seed}: {ticks} тиків, переможці [{string.Join(",", h.Finished.Single().Result.Winners)}]");
            Assert.True(ticks < 60000, "партія мала б скінчитись за розумний час");
        }
        Assert.True(won >= 2, $"вправна людина мала б бити легких ботів: {won}/3");
    }

    /// <summary>Середнє життя бота в раунді учотирьох — боти того самого рівня, чисте ядро без кімнати.</summary>
    static double MeanLife(LiveBots.Level lvl, int rounds, int seed = 5)
    {
        var rng = new Random(seed);
        var core = new CurveCore(rng);
        var total = 0.0;
        for (var r = 0; r < rounds; r++)
        {
            core.Reset([true, true, true, true, false, false, false, false]);
            var bots = Enumerable.Range(0, 4).Select(i => new CurveBot(lvl, i)).ToArray();
            var died = new int[4];
            while (core.AliveCount > 0 && core.RoundTicks < CurveCore.MaxRoundTicks)
            {
                for (var s = 0; s < 4; s++)
                    if (core.Heads[s].Alive && bots[s].Due(core.RoundTicks)) core.Turn(s, bots[s].Think(core, s, rng));
                foreach (var d in core.Step()) died[d] = core.RoundTicks;
            }
            for (var s = 0; s < 4; s++) total += died[s] == 0 ? core.RoundTicks : died[s];
        }
        return total / rounds / 4;
    }

    [Fact]
    public void Hard_bots_live_much_longer_than_easy_ones()
    {
        var easy = MeanLife(LiveBots.Level.Easy, 20);
        var normal = MeanLife(LiveBots.Level.Normal, 20);
        var hard = MeanLife(LiveBots.Level.Hard, 20);
        log.WriteLine($"середнє життя, тиків: легкий {easy:0}, звичайний {normal:0}, сильний {hard:0}");
        Assert.True(normal > easy * 1.3, $"звичайний {normal:0} vs легкий {easy:0}");
        Assert.True(hard > normal, $"сильний {hard:0} vs звичайний {normal:0}");
    }

    [Fact]
    public void A_friend_sitting_down_sends_the_bots_away()
    {
        var h = new RoomHarness("curve");
        h.Join("Оля");
        h.Act(0, LiveBots.Toggle, new { on = true });
        h.Join("Петро");
        Assert.True(h.Start().Ok, h.Reply.Message);
        var g = G(h);
        Assert.Empty(g.Bots);
        Assert.Null(g.SeatBot(2));
        Assert.False(g.Field.Heads[2].Present);
        Assert.Equal(10, h.View(0).GetProperty("target").GetInt32());
    }

    [Fact]
    public void The_human_leaving_ends_the_game_with_no_winners()
    {
        var h = Alone();
        h.Tick(CurveCore.ReadyTicks + 20);
        h.Leave("Оля");
        Assert.Empty(h.Finished.Single().Result.Winners);
    }

    [Fact]
    public void Level_comes_from_the_table_option_and_teams_stay_for_people()
    {
        var h = Alone(more: new { botlvl = "hard", teams = "1" });
        Assert.Equal("hard", h.View(0).GetProperty("botLvl").GetString());
        Assert.Equal(LiveBots.Level.Hard, G(h).BotLevel);
        Assert.Null(G(h).Teams);
        Assert.Contains("ботами", h.View(0).GetProperty("note").GetString());
        Assert.Equal("easy", Alone("easy").View(0).GetProperty("botLvl").GetString());
    }

    [Fact]
    public void A_rematch_keeps_the_bots_at_the_table()
    {
        var h = Alone();
        PlayOut(h, null);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        // «Ще раз» одразу стартує нову партію — боти лишаються за столом, рахунок з нуля.
        Assert.True(h.Rematch().Ok, h.Reply.Message);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal([1, 2, 3], G(h).Bots);
        Assert.All(h.View(0).GetProperty("scores").EnumerateArray(), x => Assert.Equal(0, x.GetInt32()));
    }

    [Fact]
    public void Bot_thought_is_cheap()
    {
        var h = Alone("hard");
        h.Tick(CurveCore.ReadyTicks + 30);
        var f = G(h).Field;
        var s = G(h).Bots.First(b => f.Heads[b].Alive);
        var bot = new CurveBot(LiveBots.Level.Hard);
        var rng = new Random(3);
        bot.Think(f, s, rng);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++) bot.Think(f, s, rng);
        sw.Stop();
        var per = sw.Elapsed.TotalMilliseconds / 1000;
        log.WriteLine($"думка сильного бота: {per * 1000:0} мкс");
        Assert.True(per < 0.2, $"думка {per:0.000} мс");
    }
}
