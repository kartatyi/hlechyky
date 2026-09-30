using System.Diagnostics;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «🤖 + бот» у Корчмі: людина сама за столом кличе гравця-бота (<see cref="TavernPilot"/>), що ховається серед люду.
/// Друга половина <see cref="TavernTests"/> — з її підмостками (Put, Pin, Park, Calm, DrinkAt, Face).
/// </summary>
public partial class TavernTests
{
    static RoomHarness Solo(string level = "normal", int seed = 42, string rounds = "3")
    {
        var h = new RoomHarness("tavern", new { botlvl = level, rounds }, seed: seed);
        h.Join("Оля");
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static TavernGuest Pilot(RoomHarness h) => Core(h).V[S(h, G(h).Bot).Me];

    /// <summary>
    /// «Новачок-манекен»: щойно нема цілі — іде до найближчої приступки, де ще не пив, і п'є; кулаком не махає.
    /// Ходить мозком юрми. Однаковий для всіх рівнів бота — так і видно, хто сильніший.
    /// </summary>
    static void Rookie(RoomHarness h)
    {
        var g = G(h);
        var s = S(h, 0);
        if (g.Phase is not (Tavern.PhaseGo or Tavern.PhaseStart) || !s.Alive) return;
        var core = Core(h);
        var me = core.V[s.Me];
        me.PunchCool = Math.Max(me.PunchCool, 2);            // першим не б'є
        if (!me.Free) return;
        var p = TavernCore.PlaceAt(me);
        if (core.Open && p >= 0 && !s.Places[p] && !me.Sit && me.DrinkCool == 0 && g.Act(0, "drink", Views.Payload(new { })).Ok) return;
        if (me.Stand > 0) me.Stand = 0;
        if (core.Open && me.Target < 0 && me.Wander == 0)
        {
            int best = -1;
            long bestD = long.MaxValue;
            foreach (var pl in TavernMap.Places)
            {
                if (s.Places[pl.I]) continue;
                var cell = pl.Cells[0];
                long dx = TavernMap.CenterX(cell) - me.X, dy = TavernMap.CenterY(cell) - me.Y;
                if (dx * dx + dy * dy < bestD) { bestD = dx * dx + dy * dy; best = cell; }
            }
            if (best >= 0) core.Aim(me, best, TavernMap.PlaceOf[best]);
        }
        core.Think(me);
        g.Act(0, "move", Views.Payload(new { dir = me.Want }));
    }

    static void PlayOut(RoomHarness h, int maxTicks = 20_000)
    {
        for (var t = 0; t < maxTicks && h.Room.Status == RoomStatus.Playing; t++)
        {
            Rookie(h);
            h.Tick();
            if (t % 500 == 0) h.Outbox.Clear();
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
    }

    [Fact]
    public void Solo_cannot_start_until_the_host_calls_a_bot_then_the_bot_is_a_guest_with_three_hearts()
    {
        var h = new RoomHarness("tavern", seed: 3);
        h.Join("Оля");
        var r = h.Start();
        Assert.False(r.Ok);
        Assert.Equal(LiveBots.AloneText, r.Message);
        Assert.True(h.View(0).GetProperty("botOffer").GetBoolean());
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.Equal(1, h.View(0).GetProperty("bot").GetInt32());
        Assert.True(h.Start().Ok, h.Reply.Message);
        Assert.Equal(1, G(h).Bot);
        Assert.Equal(LiveBots.Name, h.Room.SafeSeatBot(1));
        Assert.Equal(LiveBots.Name, S(h, 1).Nick);
        Assert.Equal(2 + Tavern.BotsFor(2), Core(h).N);
        Assert.Equal(1, Pilot(h).Owner);
        Assert.Equal(Tavern.Hearts, S(h, 1).Hearts);
        Assert.False(G(h).Act(0, LiveBots.Toggle, Views.Payload(new { on = false })).Ok);
    }

    [Fact]
    public void Level_comes_from_the_table_option()
    {
        Assert.Contains(new Tavern().Info.Options!, o => o.Key == LiveBots.LevelOption.Key);
        Assert.Equal(LiveBots.Level.Easy, G(Solo("easy")).PilotForTests!.Level);
        Assert.Equal(LiveBots.Level.Hard, G(Solo("hard")).PilotForTests!.Level);
    }

    [Fact]
    public void A_friend_who_sits_down_plays_instead_of_the_bot()
    {
        var h = new RoomHarness("tavern", seed: 4);
        h.Join("Оля");
        h.Act(0, LiveBots.Toggle, new { on = true });
        h.Join("Петро");
        Assert.True(h.Start().Ok);
        Assert.Equal(-1, G(h).Bot);
        Assert.Null(G(h).PilotForTests);
        Assert.Null(h.Room.SafeSeatBot(1));
        Assert.All(Enumerable.Range(2, 6), i => Assert.False(S(h, i).Plays));
    }

    [Fact]
    public void With_one_human_the_round_does_not_end_at_once_and_the_bot_walks_and_drinks()
    {
        var h = Solo("hard", seed: 7);
        var pilot = Pilot(h);
        int moving = 0, ticks = 0;
        var cells = new HashSet<int>();
        for (var t = 0; t < 1400 && G(h).RoundNo == 1 && G(h).Phase is Tavern.PhaseStart or Tavern.PhaseGo; t++)
        {
            h.Tick();
            ticks++;
            if (pilot.Moving) moving++;
            cells.Add(TavernMap.CellOf(pilot.X, pilot.Y));
        }
        Assert.True(ticks > 300, $"раунд скінчився за {ticks} тиків");
        Assert.True(moving > ticks / 5, $"бот ходив лише {moving} з {ticks}");
        Assert.True(moving < ticks, "бот ніколи не стоїть");
        Assert.True(cells.Count >= 8, $"бот побував лише в {cells.Count} клітинках");
        Assert.True(S(h, 1).Mugs >= 1, "сильний бот за раунд не хильнув жодного свого кухля");
    }

    [Fact]
    public void Bot_drinks_through_the_same_drink_as_a_human()
    {
        var h = Solo("hard", seed: 9);
        Go(h);
        var pilot = Pilot(h);
        Park(h, pilot.Id);
        var place = TavernMap.Places[2];
        var (x, y) = CellXY(place.Cells[0]);
        Put(pilot, x, y);
        pilot.Stand = 0;
        for (var t = 0; t < 60 && S(h, 1).Mugs == 0; t++) h.Tick();
        Assert.Equal(1, S(h, 1).Mugs);
        Assert.True(S(h, 1).Places[2]);
    }

    [Fact]
    public void Bot_knocked_out_is_out_and_the_human_takes_the_round()
    {
        var h = Solo("hard", seed: 12);
        Go(h);
        var pilot = Pilot(h);
        var me = Me(h, 0);
        Park(h, me.Id, pilot.Id);
        Face(h, 0, pilot);
        pilot.Dazed = 40;                                       // стоїть, не тікає від кулака
        S(h, 1).Hearts = 1;
        Assert.True(h.Act(0, "punch", new { }).Ok, h.Reply.Message);
        h.Tick(TavernCore.WindTicks + 1);
        Assert.False(S(h, 1).Alive);
        Assert.Equal(Tavern.PhaseReveal, G(h).Phase);
        var reveal = h.View(0).GetProperty("reveal");
        Assert.Equal("last", reveal.GetProperty("why").GetString());
        Assert.Equal([0], reveal.GetProperty("winners").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void Bot_knows_the_human_who_took_its_heart_and_suspects_one_who_punches_villagers_easy_does_not()
    {
        foreach (var (level, marks) in new[] { ("hard", true), ("easy", false) })
        {
            var h = Solo(level, seed: 13);
            Go(h);
            var pilot = Pilot(h);
            var me = Me(h, 0);
            var npc = Core(h).V.First(v => v.Owner < 0);
            Park(h, me.Id, pilot.Id, npc.Id);
            Put(pilot, 20, 5);
            Pin(pilot);
            Face(h, 0, npc);
            Pin(npc);
            h.Act(0, "punch", new { });
            h.Tick(TavernCore.WindTicks + 1);
            Assert.True(npc.Fallen > 0);
            var pl = G(h).PilotForTests!;
            Assert.Equal(marks, pl.SuspicionOf(me.Id) > 0);
            // тепер Оля б'є самого бота: пропало серце — це людина, і це знає навіть легкий
            me.Dazed = 0;
            me.PunchCool = 0;
            Face(h, 0, pilot);
            pilot.Dazed = 40;
            h.Act(0, "punch", new { });
            h.Tick(TavernCore.WindTicks + 2);
            Assert.Equal(Tavern.Hearts - 1, S(h, 1).Hearts);
            Assert.Equal(TavernPilot.Sure, pl.SuspicionOf(me.Id));
        }
    }

    [Fact]
    public void Hard_bot_hits_back_a_known_human_but_never_while_the_barman_watches()
    {
        foreach (var watching in new[] { false, true })
        {
            var h = Solo("hard", seed: 21);
            Go(h);
            var pilot = Pilot(h);
            var me = Me(h, 0);
            Park(h, me.Id, pilot.Id);
            Face(h, 0, pilot);
            pilot.Dazed = 20;
            h.Act(0, "punch", new { });
            h.Tick(TavernCore.WindTicks + 1);                   // серце бота пропало — він знає, хто це
            me.PunchCool = 100_000;                             // Оля більше не б'є
            var b = Core(h).Barman;
            var hearts = S(h, 0).Hearts;
            var swung = false;
            for (var t = 0; t < 250; t++)
            {
                if (watching) { b.Mode = 2; b.Left = 1000; }
                h.Tick();
                swung |= pilot.Wind > 0;
            }
            if (watching)
            {
                Assert.False(swung, "махнув кулаком при корчмарі");
                Assert.Equal(hearts, S(h, 0).Hearts);
            }
            else Assert.True(S(h, 0).Hearts < hearts, "сильний бот не дав здачі людині");
        }
    }

    [Fact]
    public void Hard_bot_scores_more_than_easy_against_the_same_rookie_and_bot_gets_no_win_nor_rewards()
    {
        int easyBot = 0, hardBot = 0, rookieWinsVsEasy = 0;
        foreach (var level in new[] { "easy", "hard" })
            for (var seed = 1; seed <= 5; seed++)
            {
                var h = Solo(level, seed: seed);
                PlayOut(h);
                var g = G(h);
                int botPts = S(h, g.Bot).Total, humanPts = S(h, 0).Total;
                if (level == "easy") easyBot += botPts; else hardBot += botPts;
                var res = h.Room.Result!;
                Assert.DoesNotContain(g.Bot, res.Winners);
                Assert.Empty(h.Awards);
                Assert.Empty(h.Scores);
                if (humanPts > botPts)
                {
                    Assert.Equal([0], res.Winners);
                    Assert.StartsWith("🏆 Оля — перемога над " + (level == "easy" ? "легким" : "сильним") + " ботом", res.Verdict);
                    if (level == "easy") rookieWinsVsEasy++;
                }
                else if (botPts > humanPts)
                {
                    Assert.Empty(res.Winners);
                    Assert.StartsWith("🤖 Бот переміг", res.Verdict);
                }
                else Assert.StartsWith("🤝 Нічия з ботом", res.Verdict);
                output.WriteLine($"{level} seed {seed}: Оля {humanPts} — бот {botPts}");
            }
        output.WriteLine($"бот легкий Σ{easyBot}, сильний Σ{hardBot}; новачок переміг легкого {rookieWinsVsEasy} з 5");
        Assert.True(hardBot > easyBot, $"сильний {hardBot} не краще за легкого {easyBot}");
        Assert.True(rookieWinsVsEasy >= 3, $"новачок переміг легкого лише {rookieWinsVsEasy} з 5");
    }

    [Fact]
    public void Human_leaving_mid_match_ends_it_without_winners()
    {
        var h = Solo(seed: 14);
        Go(h);
        h.Tick(30);
        h.Leave("Оля");
        Assert.Empty(h.Finished.Single().Result.Winners);
        Assert.Empty(h.Scores);
    }

    [Fact]
    public void Bot_thinking_is_cheap()
    {
        var h = Solo("hard", seed: 15);
        Go(h);
        var pilot = G(h).PilotForTests!;
        var v = Pilot(h);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++) pilot.Think(v);
        sw.Stop();
        var per = sw.Elapsed.TotalMilliseconds / 1000;
        output.WriteLine($"думка бота: {per * 1000:F1} мкс на тик");
        Assert.True(per < 0.2, $"{per:F3} мс на тик");
    }
}
