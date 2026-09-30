using System.Diagnostics;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>Земля соло з ботами (spec territory.md, «Соло з ботом»): двоє ботів, рівні, без нагород.</summary>
public class TerritoryBotTests(ITestOutputHelper log)
{
    static RoomHarness Alone(string lvl = "normal", int seed = 42, string round = "90")
    {
        var h = new RoomHarness("territory", new { botlvl = lvl, round }, seed);
        h.Join("Оля");
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static Territory G(RoomHarness h) => (Territory)h.Room.Game;

    /// <summary>
    /// Грати до кінця. Людина — манекен (null: не повертає, їде за поле) або керована тим самим TerritoryBot
    /// заданого рівня через звичайний ввід <c>turn</c>. Повертає, скільки тиків тривала партія.
    /// </summary>
    static int PlayOut(RoomHarness h, LiveBots.Level? human, int maxTicks = 5000)
    {
        var brain = human is { } l ? new TerritoryBot(l) : null;
        var rng = new Random(7);
        var t = 0;
        for (; t < maxTicks && h.Room.Status == RoomStatus.Playing; t++)
        {
            if (brain is not null && brain.Think(G(h).Core, 0, rng) is { } d) h.Input(0, "turn", new { dir = d });
            h.Tick();
        }
        return t;
    }

    [Fact]
    public void Alone_the_field_asks_for_bots_and_two_plots_appear_in_the_lobby()
    {
        var h = new RoomHarness("territory");
        h.Join("Оля");
        Assert.False(h.Start().Ok);
        Assert.Equal(LiveBots.AloneText, h.Reply.Message);
        Assert.True(h.View(0).GetProperty("botOffer").GetBoolean());
        Assert.False(h.Act(0, "turn", new { dir = 1 }).Ok);
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        var v = h.View(0);
        Assert.Equal([1, 2], v.GetProperty("bot").EnumerateArray().Select(x => x.GetInt32()));
        Assert.True(v.GetProperty("area")[2].GetDouble() > 0);          // наділи ботів видно ще в лобі
        Assert.True(h.Start().Ok, h.Reply.Message);
        var g = G(h);
        Assert.Equal([1, 2], g.Bots);
        Assert.True(g.Core.Riders[1].On && g.Core.Riders[2].On && !g.Core.Riders[3].On);
        Assert.Equal("🤖 бот зелений", g.SeatBot(1));
        Assert.StartsWith(LiveBots.Name, g.SeatBot(2));
        Assert.Null(g.SeatBot(0));
        Assert.False(h.Act(0, LiveBots.Toggle, new { on = false }).Ok);
    }

    [Fact]
    public void Bots_really_grab_land()
    {
        var h = Alone();
        h.Tick(Territory.ReadyTicks + 300);
        var c = G(h).Core;
        foreach (var b in G(h).Bots)
            Assert.True(c.Area(b) > 9, $"бот {b} за 30 с мав би обвести більше за стартовий наділ: {c.Area(b)}");
    }

    [Fact]
    public void Bots_win_against_a_passive_human_without_rewards()
    {
        var h = Alone();
        PlayOut(h, null);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Empty(h.Finished.Single().Result.Winners);
        Assert.StartsWith("🤖 Землю взяв 🤖 бот", h.Room.Result!.Verdict);
        Assert.Contains("🤖 бот", h.Room.Result!.Text);
        Assert.DoesNotContain(h.Awards, a => a.Reason.StartsWith("ach:"));
        Assert.Empty(h.Scores);
    }

    [Fact]
    public void A_skilled_human_beats_easy_bots()
    {
        var won = 0;
        for (var seed = 1; seed <= 3; seed++)
        {
            var h = Alone("easy", seed);
            PlayOut(h, LiveBots.Level.Hard);
            Assert.Equal(RoomStatus.Finished, h.Room.Status);
            var w = h.Finished.Single().Result.Winners;
            log.WriteLine($"seed {seed}: переможці [{string.Join(",", w)}] — {h.Room.Result!.Text}");
            if (w.SequenceEqual([0]))
            {
                won++;
                Assert.StartsWith("🏆 Оля — перемога над легкими ботами", h.Room.Result!.Verdict);
            }
        }
        Assert.True(won >= 2, $"вправна людина мала б бити легких ботів: {won}/3");
    }

    /// <summary>
    /// Дуелі рівнів у чистому ядрі: скільки відсотків поля в середньому тримає кожен наприкінці раунду.
    /// Місця міняємо щораунду, щоб ніхто не мав кращого кута.
    /// </summary>
    static (double A, double B) Duel(LiveBots.Level a, LiveBots.Level b, int rounds, int seed = 5)
    {
        var rng = new Random(seed);
        var core = new TerritoryCore(rng);
        double sa = 0, sb = 0;
        for (var r = 0; r < rounds; r++)
        {
            core.Reset([true, true, false, false, false, false]);
            var (ia, ib) = r % 2 == 0 ? (0, 1) : (1, 0);
            var ba = new TerritoryBot(a);
            var bb = new TerritoryBot(b);
            while (core.TicksLeft > 0)
            {
                if (ba.Think(core, ia, rng) is { } x) core.Turn(ia, x);
                if (bb.Think(core, ib, rng) is { } y) core.Turn(ib, y);
                core.Step();
            }
            sa += core.Percent(ia);
            sb += core.Percent(ib);
        }
        return (sa / rounds, sb / rounds);
    }

    [Fact]
    public void Stronger_levels_grab_more_in_a_duel()
    {
        var ne = Duel(LiveBots.Level.Normal, LiveBots.Level.Easy, 10);
        var hn = Duel(LiveBots.Level.Hard, LiveBots.Level.Normal, 10);
        log.WriteLine($"звичайний {ne.A:0.0}% vs легкий {ne.B:0.0}%; сильний {hn.A:0.0}% vs звичайний {hn.B:0.0}%");
        Assert.True(ne.A > ne.B * 1.3, $"звичайний {ne.A:0.0} vs легкий {ne.B:0.0}");
        Assert.True(hn.A > hn.B, $"сильний {hn.A:0.0} vs звичайний {hn.B:0.0}");
    }

    [Fact]
    public void Hard_bot_against_easy_bots_takes_the_field()
    {
        // Людина-манекен на місці 0 не заважає: сильний (місце 1 у ядрі) проти легкого (2).
        var rng = new Random(11);
        var core = new TerritoryCore(rng);
        core.Reset([false, true, true, false, false, false]);
        var hard = new TerritoryBot(LiveBots.Level.Hard);
        var easy = new TerritoryBot(LiveBots.Level.Easy);
        while (core.TicksLeft > 0)
        {
            if (hard.Think(core, 1, rng) is { } a) core.Turn(1, a);
            if (easy.Think(core, 2, rng) is { } b) core.Turn(2, b);
            core.Step();
        }
        log.WriteLine($"сильний {core.Percent(1)}%, легкий {core.Percent(2)}%");
        Assert.True(core.Area(1) > core.Area(2));
    }

    [Fact]
    public void A_friend_sitting_down_sends_the_bots_away()
    {
        var h = new RoomHarness("territory");
        h.Join("Оля");
        h.Act(0, LiveBots.Toggle, new { on = true });
        h.Join("Петро");
        Assert.True(h.Start().Ok, h.Reply.Message);
        Assert.Empty(G(h).Bots);
        Assert.Null(G(h).SeatBot(2));
        Assert.False(G(h).Core.Riders[2].On);
    }

    [Fact]
    public void The_human_leaving_ends_the_game_with_no_winners()
    {
        var h = Alone();
        h.Tick(Territory.ReadyTicks + 20);
        h.Leave("Оля");
        Assert.Empty(h.Finished.Single().Result.Winners);
    }

    [Fact]
    public void Two_people_one_leaves_the_other_is_left_alone_and_wins()
    {
        // MinPlayers = 1 не має дограти людську партію «самому»: як і раніше, хто лишився — той і взяв.
        var h = new RoomHarness("territory");
        h.Join("Оля");
        h.Join("Петро");
        Assert.True(h.Start().Ok, h.Reply.Message);
        h.Tick(Territory.ReadyTicks + 10);
        h.Leave("Петро");
        Assert.Equal([0], h.Finished.Single().Result.Winners);
    }

    [Fact]
    public void Level_comes_from_the_table_option()
    {
        Assert.Equal("hard", Alone("hard").View(0).GetProperty("botLvl").GetString());
        Assert.Equal(LiveBots.Level.Easy, G(Alone("easy")).BotLevel);
    }

    [Fact]
    public void A_rematch_keeps_the_bots()
    {
        var h = Alone(round: "60");
        PlayOut(h, null);
        Assert.True(h.Rematch().Ok, h.Reply.Message);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal([1, 2], G(h).Bots);
        Assert.True(G(h).Core.Riders[1].On);
    }

    [Fact]
    public void Bot_thought_is_cheap()
    {
        // Цілий раунд утрьох сильних (полювання, пильність, пошуки додому) — середня й найдовша думка.
        var rng = new Random(9);
        var core = new TerritoryCore(rng);
        core.Reset([true, true, true, false, false, false]);
        var bots = Enumerable.Range(0, 3).Select(_ => new TerritoryBot(LiveBots.Level.Hard)).ToArray();
        var sw = new Stopwatch();
        double worst = 0;
        var n = 0;
        while (core.TicksLeft > 0)
        {
            for (var s = 0; s < 3; s++)
            {
                var t0 = sw.Elapsed.TotalMilliseconds;
                sw.Start();
                var d = bots[s].Think(core, s, rng);
                sw.Stop();
                worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds - t0);
                n++;
                if (d is { } x) core.Turn(s, x);
            }
            core.Step();
        }
        var per = sw.Elapsed.TotalMilliseconds / n;
        log.WriteLine($"думка сильного бота: середня {per * 1000:0} мкс, найдовша {worst * 1000:0} мкс");
        Assert.True(per < 0.2, $"думка {per:0.000} мс");
    }
}
