using System.Diagnostics;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «🤖 + бот» у Гарячому горщику: людина сама за столом кличе гравця-бота (<see cref="PotatoPilot"/>), що ховається в
/// юрмі. Друга половина <see cref="PotatoTests"/> — з її підмостками (Put, Park, Hand, Go).
/// </summary>
public partial class PotatoTests
{
    static RoomHarness Solo(string level = "normal", int seed = 42, string rounds = "3")
    {
        var h = new RoomHarness("potato", new { botlvl = level, rounds }, seed: seed);
        h.Join("Оля");
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static PotatoVillager Pilot(RoomHarness h) => Core(h).V[S(h, G(h).Bot).Me];

    /// <summary>
    /// «Людина-манекен»: без горщика стоїть; з горщиком іде до найближчого на ногах і передає, щойно можна. Однакова
    /// для всіх рівнів бота — так і видно, хто сильніший.
    /// </summary>
    static void Dummy(RoomHarness h)
    {
        var g = G(h);
        var s = S(h, 0);
        if (g.Phase != Potato.PhaseGo || !s.Alive) return;
        var core = Core(h);
        var me = core.V[s.Me];
        if (me.Pot < 0) { me.Want = -1; return; }
        if (core.PassTarget(me) >= 0 && g.Act(0, "pass", Views.Payload(new { })).Ok) return;
        var t = core.Nearest(me, core.Pots[me.Pot]);
        if (t < 0) return;
        int dx = core.V[t].X - me.X, dy = core.V[t].Y - me.Y;
        var dir = Math.Abs(dx) > Math.Abs(dy) ? (dx > 0 ? 0 : 2) : (dy > 0 ? 1 : 3);
        g.Act(0, "move", Views.Payload(new { dir }));
    }

    /// <summary>Партія до кінця з манекеном; Outbox чистимо, щоб тисячі кадрів не лежали в пам'яті.</summary>
    static void PlayOut(RoomHarness h, int maxTicks = 20_000)
    {
        for (var t = 0; t < maxTicks && h.Room.Status == RoomStatus.Playing; t++)
        {
            Dummy(h);
            h.Tick();
            if (t % 500 == 0) h.Outbox.Clear();
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
    }

    [Fact]
    public void Solo_cannot_start_until_the_host_calls_a_bot_then_the_bot_takes_a_free_seat()
    {
        var h = new RoomHarness("potato", seed: 3);
        h.Join("Оля");
        var r = h.Start();
        Assert.False(r.Ok);
        Assert.Equal(LiveBots.AloneText, r.Message);
        Assert.True(h.View(0).GetProperty("botOffer").GetBoolean());
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.Equal(1, h.View(0).GetProperty("bot").GetInt32());
        Assert.True(h.Start().Ok, h.Reply.Message);
        var g = G(h);
        Assert.Equal(1, g.Bot);
        Assert.Equal(LiveBots.Name, h.Room.SafeSeatBot(1));
        Assert.True(S(h, 1).Plays);
        Assert.Equal(LiveBots.Name, S(h, 1).Nick);
        // бот — такий самий гравець: свій селянин (owner = його місце), юрма «як на двох»
        Assert.Equal(2 + Potato.BotsFor(2), Core(h).N);
        Assert.Equal(1, Pilot(h).Owner);
        Assert.Equal("normal", h.View(0).GetProperty("botLvl").GetString());
        Assert.False(g.Act(0, LiveBots.Toggle, Views.Payload(new { on = false })).Ok);   // посеред партії не проганяють
    }

    [Fact]
    public void Level_comes_from_the_table_option()
    {
        Assert.Contains(new Potato().Info.Options!, o => o.Key == LiveBots.LevelOption.Key);
        Assert.Equal(LiveBots.Level.Easy, G(Solo("easy")).PilotForTests!.Level);
        Assert.Equal(LiveBots.Level.Hard, G(Solo("hard")).PilotForTests!.Level);
        Assert.Equal(LiveBots.Level.Normal, G(Solo("nonsense")).PilotForTests!.Level);
    }

    [Fact]
    public void A_friend_who_sits_down_plays_instead_of_the_bot()
    {
        var h = new RoomHarness("potato", seed: 4);
        h.Join("Оля");
        h.Act(0, LiveBots.Toggle, new { on = true });
        h.Join("Петро");
        Assert.True(h.Start().Ok);
        Assert.Equal(-1, G(h).Bot);
        Assert.Null(G(h).PilotForTests);
        Assert.Null(h.Room.SafeSeatBot(1));
        Assert.Equal(2 + Potato.BotsFor(2), Core(h).N);
        Assert.All(Enumerable.Range(2, 6), i => Assert.False(S(h, i).Plays));
    }

    [Fact]
    public void With_one_human_the_round_does_not_end_at_once_and_the_bot_walks_the_crowd()
    {
        var h = Solo(seed: 7);
        var pilot = Pilot(h);
        var moves = 0;
        var seen = new HashSet<(int, int)>();
        for (var t = 0; t < 1000; t++)
        {
            h.Tick();
            if (G(h).Phase != Potato.PhaseStart && G(h).Phase != Potato.PhaseGo) break;
            if (pilot.Moving) moves++;
            seen.Add((pilot.X / PotatoMap.Cell, pilot.Y / PotatoMap.Cell));
        }
        // «лишився один живий» не спрацював одразу: гравців двоє (людина й бот)
        Assert.True(G(h).Phase == Potato.PhaseGo || G(h).RoundNo > 1 || G(h).Phase == Potato.PhaseReveal);
        Assert.True(G(h).Left < Potato.RoundTicks - 500 || G(h).Phase != Potato.PhaseGo);
        Assert.True(moves > 100, $"бот ходив лише {moves} тиків");
        Assert.True(seen.Count >= 4, $"бот побував лише в {seen.Count} клітинках");
        Assert.True(moves < 950, "бот ніколи не стоїть — так ходять лише люди");
    }

    [Fact]
    public void Bot_passes_the_pot_on_through_the_same_pass_as_a_human()
    {
        var h = Solo("normal", seed: 11);
        Go(h);
        var core = Core(h);
        var pilot = Pilot(h);
        Park(h, pilot.Id);
        foreach (var p in core.Pots) p.Fuse = 100_000;
        Put(pilot, 5, 7);
        var npc = core.V.First(v => v.Owner < 0);
        Put(npc, 6, 7);
        Hand(h, 0, pilot, 100_000);
        for (var t = 0; t < 120 && pilot.Pot >= 0; t++) h.Tick();
        Assert.Equal(-1, pilot.Pot);
        Assert.Equal(npc.Id, core.Pots[0].Carrier);
    }

    [Fact]
    public void Bot_blown_up_by_the_pot_is_out_and_the_human_takes_the_round()
    {
        var h = Solo("hard", seed: 12);
        Go(h);
        var core = Core(h);
        var pilot = Pilot(h);
        Park(h, pilot.Id);
        Hand(h, 0, pilot, 1);
        h.Tick();
        Assert.True(pilot.Dead);
        Assert.False(S(h, G(h).Bot).Alive);
        Assert.Equal(Potato.PhaseReveal, G(h).Phase);
        var reveal = h.View(0).GetProperty("reveal");
        Assert.Equal("last", reveal.GetProperty("why").GetString());
        Assert.Equal([0], reveal.GetProperty("winners").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(RoomStatus.Playing, h.Room.Status);           // партія йде далі — ще два раунди
    }

    [Fact]
    public void Hard_bot_marks_the_human_who_slaps_a_villager_and_the_one_stunned_by_a_slap_easy_does_not_hunt()
    {
        foreach (var (level, expectMark) in new[] { ("hard", true), ("easy", false) })
        {
            var h = Solo(level, seed: 13);
            Go(h);
            var core = Core(h);
            var pilot = Pilot(h);
            Park(h, pilot.Id);
            foreach (var p in core.Pots) p.Fuse = 100_000;
            var me = Me(h, 0);
            var npc = core.V.First(v => v.Owner < 0);
            Put(me, 5, 7, dir: 0);
            Put(npc, 6, 7);
            Put(pilot, 12, 7);
            Assert.True(h.Act(0, "slap", new { }).Ok);
            Assert.True(me.Stun > 0);                              // ляснула селянина — сама отетеріла
            var pl = G(h).PilotForTests!;
            Assert.Equal(expectMark, pl.SuspicionOf(me.Id) > 0);
            // селянин ляснув Олю — вона оглухла: це гравець, тобто людина (легкий теж бачить, але не полює)
            me.Stun = 0;
            Put(npc, 6, 7, dir: 2);
            npc.SlapCool = 0;
            core.TrySlap(npc, me.Id);
            h.Tick();
            Assert.Equal(PotatoPilot.Sure, pl.SuspicionOf(me.Id));
        }
    }

    [Fact]
    public void Hard_bot_hunts_a_marked_human_and_slaps_her_while_easy_one_just_walks()
    {
        var stunned = new Dictionary<string, int>();
        foreach (var level in new[] { "hard", "easy" })
        {
            var hits = 0;
            for (var seed = 20; seed < 24; seed++)
            {
                var h = Solo(level, seed: seed);
                Go(h);
                var core = Core(h);
                var pilot = Pilot(h);
                Park(h, pilot.Id);
                foreach (var p in core.Pots) p.Fuse = 100_000;
                var me = Me(h, 0);
                Put(me, 4, 7);
                Put(pilot, 10, 7);
                // Оля ляснула селянина: для сильного — підозра вище порога
                var npc = core.V.First(v => v.Owner < 0);
                Put(npc, 5, 7);
                me.Dir = 0;
                h.Act(0, "slap", new { });
                me.Stun = 0;
                Put(npc, 1, 14);
                for (var t = 0; t < 300 && me.Stun == 0; t++) h.Tick();
                if (me.Stun > 0) hits++;
            }
            stunned[level] = hits;
        }
        Assert.True(stunned["hard"] >= 3, $"сильний оглушив Олю в {stunned["hard"]} з 4");
        Assert.Equal(0, stunned["easy"]);
    }

    [Fact]
    public void Hard_bot_beats_the_same_dummy_more_often_than_easy_and_bot_gets_no_win_nor_rewards()
    {
        int easyBot = 0, hardBot = 0, humanWinsVsEasy = 0;
        foreach (var level in new[] { "easy", "hard" })
            for (var seed = 1; seed <= 5; seed++)
            {
                var h = Solo(level, seed: seed);
                PlayOut(h);
                var g = G(h);
                var botPts = S(h, g.Bot).Total;
                var humanPts = S(h, 0).Total;
                if (level == "easy") easyBot += botPts; else hardBot += botPts;
                var res = h.Room.Result!;
                Assert.DoesNotContain(g.Bot, res.Winners);
                Assert.Empty(h.Awards);
                Assert.Empty(h.Scores);
                Assert.Contains(LiveBots.Name, res.Text);
                if (humanPts > botPts)
                {
                    Assert.Equal([0], res.Winners);
                    Assert.StartsWith("🏆 Оля — перемога над " + (level == "easy" ? "легким" : "сильним") + " ботом", res.Verdict);
                    if (level == "easy") humanWinsVsEasy++;
                }
                else if (botPts > humanPts)
                {
                    Assert.Empty(res.Winners);
                    Assert.StartsWith("🤖 Бот переміг", res.Verdict);
                }
                else Assert.StartsWith("🤝 Нічия з ботом", res.Verdict);
                output.WriteLine($"{level} seed {seed}: Оля {humanPts} — бот {botPts}");
            }
        output.WriteLine($"бот легкий Σ{easyBot}, сильний Σ{hardBot}; Оля перемогла легкого {humanWinsVsEasy} з 5");
        Assert.True(hardBot > easyBot, $"сильний {hardBot} не краще за легкого {easyBot}");
        Assert.True(humanWinsVsEasy >= 2, $"манекен переміг легкого лише {humanWinsVsEasy} з 5");
    }

    [Fact]
    public void Human_leaving_mid_match_ends_it_without_winners()
    {
        var h = Solo(seed: 14);
        Go(h);
        h.Tick(30);
        h.Leave("Оля");                                           // стіл без людей закривається
        Assert.Empty(h.Finished.Single().Result.Winners);
        Assert.Empty(h.Scores);
    }

    [Fact]
    public void Bot_can_be_called_again_after_the_match_and_dismissed_in_the_lobby()
    {
        var h = new RoomHarness("potato", seed: 5);
        h.Join("Оля");
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = false }).Ok);
        Assert.False(h.View(0).GetProperty("botWanted").GetBoolean());
        Assert.Equal(LiveBots.AloneText, h.Start().Message);
    }

    [Fact]
    public void Bot_thinking_is_cheap()
    {
        var h = Solo("hard", seed: 15);
        Go(h);
        var core = Core(h);
        var pilot = G(h).PilotForTests!;
        var v = Pilot(h);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            core.TimersAll();
            pilot.Think(v);
            PotatoCore.Step(v);
            core.Log.Clear();
        }
        sw.Stop();
        var per = sw.Elapsed.TotalMilliseconds / 1000;
        output.WriteLine($"думка бота: {per * 1000:F1} мкс на тик");
        Assert.True(per < 0.2, $"{per:F3} мс на тик");
    }
}
