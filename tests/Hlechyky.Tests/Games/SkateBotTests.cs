using System.Diagnostics;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «🤖 + бот» у Ковзанці: людина сама за столом кличе гравця-бота (<see cref="SkatePilot"/>), що ховається серед
/// ковзанярів. Друга половина <see cref="SkateTests"/> — з її підмостками (Put, Park, Feed, Go).
/// </summary>
public partial class SkateTests
{
    static RoomHarness Solo(string level = "normal", int seed = 42, string rounds = "3")
    {
        var h = new RoomHarness("skate", new { botlvl = level, rounds }, seed: seed);
        h.Join("Оля");
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static SkateVillager Pilot(RoomHarness h) => Core(h).V[S(h, G(h).Bot).Me];

    /// <summary>
    /// «Новачок-манекен»: щойно нема заняття — їде по найближче своє ласощі, клавіші тисне мозком юрми (ті самі
    /// поштовхи й гальма). Нікого не таранить. Однаковий для всіх рівнів бота — так і видно, хто сильніший.
    /// </summary>
    static void Rookie(RoomHarness h)
    {
        var g = G(h);
        var s = S(h, 0);
        if (g.Phase is not (Skate.PhaseGo or Skate.PhaseStart) || !s.Alive) return;
        var core = Core(h);
        var me = core.V[s.Me];
        if (!me.OnIce) return;
        if (me.Fallen == 0 && me.Alarm == 0 && me.Hold == 0 && me.Mode == SkateCore.ModeStand && core.Trading)
        {
            me.Stand = 0;
            int best = -1;
            long bestD = long.MaxValue;
            for (var k = 0; k < core.Slots.Length; k++)
            {
                var it = core.Slots[k];
                var want = false;
                for (var j = 0; j < 4; j++) want |= s.List[j] == it.Kind && !s.Got[j];
                if (!it.Here || !want || !SkateMap.Clear(me.X / Fp, me.Y / Fp, it.X, it.Y)) continue;
                long dx = (long)it.X * Fp - me.X, dy = (long)it.Y * Fp - me.Y;
                if (dx * dx + dy * dy < bestD) { bestD = dx * dx + dy * dy; best = k; }
            }
            if (best >= 0)
            {
                me.Mode = SkateCore.ModeItem;
                me.ModeLeft = 40;
                me.Item = best;
                me.Tx = core.Slots[best].X * Fp;
                me.Ty = core.Slots[best].Y * Fp;
                me.Cruise = SkateCore.Vmax * 2 / 3;
            }
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
    public void Solo_cannot_start_until_the_host_calls_a_bot_then_the_bot_skates_as_a_player()
    {
        var h = new RoomHarness("skate", seed: 3);
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
        Assert.Equal(2 + Skate.BotsFor(2), Core(h).N);
        Assert.Equal(1, Pilot(h).Owner);
        Assert.Equal(4, S(h, 1).List.Distinct().Count());           // свій кошик, як у людини
        Assert.False(G(h).Act(0, LiveBots.Toggle, Views.Payload(new { on = false })).Ok);
    }

    [Fact]
    public void Level_comes_from_the_table_option()
    {
        Assert.Contains(new Skate().Info.Options!, o => o.Key == LiveBots.LevelOption.Key);
        Assert.Equal(LiveBots.Level.Easy, G(Solo("easy")).PilotForTests!.Level);
        Assert.Equal(LiveBots.Level.Hard, G(Solo("hard")).PilotForTests!.Level);
    }

    [Fact]
    public void A_friend_who_sits_down_plays_instead_of_the_bot()
    {
        var h = new RoomHarness("skate", seed: 4);
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
    public void With_one_human_the_round_does_not_end_at_once_and_the_bot_skates_and_collects()
    {
        var h = Solo("hard", seed: 7);
        var pilot = Pilot(h);
        int moving = 0, ticks = 0;
        for (var t = 0; t < 1500 && G(h).RoundNo == 1 && G(h).Phase is Skate.PhaseStart or Skate.PhaseGo; t++)
        {
            h.Tick();
            ticks++;
            if (SkateCore.Speed2(pilot) > 100L * 100) moving++;
        }
        Assert.True(ticks > 400, $"раунд скінчився за {ticks} тиків");
        Assert.True(moving > ticks / 4, $"бот котився лише {moving} з {ticks}");
        Assert.True(moving < ticks, "бот ніколи не стоїть");
        Assert.True(S(h, 1).Items >= 1 || G(h).Phase == Skate.PhaseReveal, "сильний бот за хвилину не взяв жодного свого ласощі");
    }

    [Fact]
    public void Bot_in_the_water_is_out_and_the_human_takes_the_round()
    {
        var h = Solo("hard", seed: 12);
        Go(h);
        var core = Core(h);
        var pilot = Pilot(h);
        var hole = core.Holes.First(x => x.Open);
        pilot.X = hole.X * Fp;
        pilot.Y = hole.Y * Fp;
        pilot.Vx = pilot.Vy = 0;
        h.Tick();
        Assert.False(S(h, 1).Alive);
        Assert.Equal(Skate.PhaseReveal, G(h).Phase);
        var reveal = h.View(0).GetProperty("reveal");
        Assert.Equal("last", reveal.GetProperty("why").GetString());
        Assert.Equal([0], reveal.GetProperty("winners").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void Bot_picks_its_own_treats_through_the_same_slow_roll_as_a_human()
    {
        var h = Solo("hard", seed: 9);
        Go(h);
        var pilot = Pilot(h);
        var s = S(h, 1);
        // «Feed» бота: його ласощі під ноги — підхоплює за тими ж правилами, що й людина
        var core = Core(h);
        var slot = core.Slots.First(x => x.Kind == s.List[0]);
        slot.X = pilot.X / Fp;
        slot.Y = pilot.Y / Fp;
        slot.Wait = 0;
        pilot.Vx = pilot.Vy = 0;
        h.Tick();
        Assert.True(s.Got[0]);
        Assert.Equal(1, s.Items);
    }

    [Fact]
    public void Hard_bot_suspects_the_one_who_keeps_picking_treats_easy_does_not()
    {
        foreach (var (level, marks) in new[] { ("hard", true), ("easy", false) })
        {
            var h = Solo(level, seed: 13);
            Go(h);
            Feed(h, 0, S(h, 0).List[0]);
            Feed(h, 0, S(h, 0).List[1]);
            var sus = G(h).PilotForTests!.SuspicionOf(Me(h, 0).Id);
            Assert.Equal(marks, sus > 0);
        }
    }

    /// <summary>Ставок без юрми: ополонка й точка льоду за 60 од. від її краю, звідки видно ще 200 од. льоду назад.</summary>
    static (SkateHole Hole, int Hx, int Hy, int Px, int Py) Rink(RoomHarness h)
    {
        var core = Core(h);
        for (var tries = 0; tries < 400; tries++)
        {
            var (x, y) = core.FreeSpot(SkateCore.R, 60);
            for (var d = 0; d < 8; d++)
            {
                int ux = SkateCore.DX8[d], uy = SkateCore.DY8[d];
                int hx = x + ux * 60 / 256, hy = y + uy * 60 / 256, px = x + ux * 200 / 256, py = y + uy * 200 / 256;
                int wx = x - ux * 90 / 256, wy = y - uy * 90 / 256;
                if (!SkateMap.Clear(px, py, wx, wy)) continue;
                return (new SkateHole { X = wx, Y = wy, R = 26 }, x, y, px, py);
            }
        }
        throw new InvalidOperationException("нема льоду для тарана");
    }

    [Fact]
    public void Hard_bot_rams_a_suspect_standing_by_the_water_easy_one_never_does()
    {
        var rams = new Dictionary<string, int>();
        foreach (var level in new[] { "hard", "easy" })
        {
            var n = 0;
            for (var seed = 20; seed < 24; seed++)
            {
                var h = Solo(level, seed: seed);
                Go(h);
                var core = Core(h);
                var pilot = Pilot(h);
                var me = Me(h, 0);
                Park(h, pilot.Id, me.Id);
                var (hole, hx, hy, px, py) = Rink(h);
                core.Holes.Add(hole);
                Put(me, hx, hy);
                Feed(h, 0, S(h, 0).List[0], tick: false);
                Feed(h, 0, S(h, 0).List[1], tick: false);
                Feed(h, 0, S(h, 0).List[2]);                    // тричі підхопила — для сильного вище порога
                Put(me, hx, hy);
                Put(pilot, px, py);
                pilot.Hold = 0;
                var hit = false;
                for (var t = 0; t < 400 && !hit && G(h).Phase == Skate.PhaseGo; t++)
                {
                    h.Tick();
                    hit = Events(h).Any(e => e[0].GetInt32() == SkateCore.EvKnock && e[1].GetInt32() == pilot.Id && e[2].GetInt32() == me.Id)
                        || !S(h, 0).Alive;
                }
                if (hit) n++;
            }
            rams[level] = n;
        }
        Assert.True(rams["hard"] >= 2, $"сильний таранив лише {rams["hard"]} з 4");
        Assert.Equal(0, rams["easy"]);
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
