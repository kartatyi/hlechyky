using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Аерохокей соло з ботом (30.09): «🤖 + бот» для самотньої людини — бот-суперник навпроти, рівень опцією столу
/// (і для напарника утрьох), без нагород і серії. Сила рівнів — проти того самого «манекена» на голому ядрі.
/// </summary>
[Collection(SerialPerf.Name)]
public class HockeySoloTests(ITestOutputHelper output)
{
    static Hockey Game(RoomHarness h) => (Hockey)h.Room.Game;
    static HockeyCore Core(RoomHarness h) => Game(h).Core;

    static RoomHarness Solo(string lvl = "normal", string goals = "5", int seed = 42)
    {
        var h = new RoomHarness("hockey", new { goals, botlvl = lvl }, seed);
        h.Join("Оля");
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok, h.Reply.Message);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    /// <summary>
    /// «Людина-манекен»: бачить шайбу без запізнення, стоїть перед своїми воротами навпроти неї, а повільну шайбу на
    /// своїй половині б'є крізь неї в центр чужих воріт. Той самий для всіх рівнів — міряємо лише бота.
    /// </summary>
    static void Dummy(HockeyCore c, int seat)
    {
        var team = c.Team[seat];
        var gx = team == 0 ? 0.0 : HockeyCore.W;
        var dir = team == 0 ? 1 : -1;
        var p = c.Puck;
        var own = team == 0 ? p.X < HockeyCore.Mid : p.X > HockeyCore.Mid;
        double x = gx + dir * 20, y = Math.Clamp(p.Y, HockeyCore.GoalLo, HockeyCore.GoalHi);
        if (own && p.Speed < 300 && Math.Abs(p.X - gx) > HockeyCore.PadR + HockeyCore.PuckR)
        {
            double ux = HockeyCore.W - gx - p.X, uy = HockeyCore.TableH / 2 - p.Y;
            var l = Math.Sqrt(ux * ux + uy * uy);
            var near = (c.Pads[seat].X - p.X) * (c.Pads[seat].X - p.X) + (c.Pads[seat].Y - p.Y) * (c.Pads[seat].Y - p.Y) < 17 * 17;
            var reach = near ? 12 : -(HockeyCore.PadR + HockeyCore.PuckR + 3);
            (x, y) = (p.X + ux / l * reach, p.Y + uy / l * reach);
        }
        c.Aim(seat, x, y);
    }

    /// <summary>Голий стіл один на один: манекен на 0 (сині), бот на 1 (руді). Повертає (голи манекена, голи бота).</summary>
    static (int Dummy, int Bot) Duel(LiveBots.Level lvl, int seed, int ticks)
    {
        var c = new HockeyCore(new Random(seed)) { BotLevel = lvl };
        c.Reset([true, true, false, false]);
        for (var t = 0; t < ticks; t++)
        {
            Dummy(c, 0);
            c.BotThink(1);
            c.Step();
        }
        return (c.S[0], c.S[1]);
    }

    [Fact]
    public void Alone_without_a_bot_cannot_start_and_the_catalog_allows_one()
    {
        var h = new RoomHarness("hockey");
        h.Join("Оля");
        Assert.Equal(1, h.Room.Info.MinPlayers);
        Assert.Contains(h.Room.Info.Options!, o => o.Key == LiveBots.LevelOption.Key);
        var v = h.View(0);
        Assert.True(v.GetProperty("botOffer").GetBoolean());
        Assert.False(v.GetProperty("botWanted").GetBoolean());
        var r = h.Start();
        Assert.False(r.Ok);
        Assert.Equal(LiveBots.AloneText, r.Message);
    }

    [Fact]
    public void Host_calls_a_bot_it_sits_opposite_and_plays_the_other_team()
    {
        var h = new RoomHarness("hockey");
        h.Join("Оля");
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        var v = h.View(0);
        Assert.Equal(1, v.GetProperty("bot").GetInt32());                // у лобі видно, куди сяде бот
        Assert.Equal("normal", v.GetProperty("botLvl").GetString());
        Assert.True(h.Start().Ok, h.Reply.Message);
        var g = Game(h);
        var c = Core(h);
        Assert.Equal(1, g.Bot);
        Assert.True(c.Plays[0] && c.Plays[1] && !c.Plays[2] && !c.Plays[3]);
        Assert.NotEqual(c.Team[0], c.Team[1]);                          // суперник, а не напарник
        Assert.Equal(LiveBots.Name, h.Room.SafeSeatBot(1));
        Assert.Null(h.Room.SafeSeatBot(0));
        Assert.Equal(1, h.View(0).GetProperty("bot").GetInt32());
    }

    [Fact]
    public void Lone_human_on_seat_two_gets_the_bot_on_seat_three()
    {
        var h = new RoomHarness("hockey");
        h.Join("Оля");
        h.Join("Петро");
        h.Join("Ганна");
        h.Leave("Оля");
        h.Leave("Петро");
        Assert.True(h.Act(2, LiveBots.Toggle, new { on = true }).Ok, h.Reply.Message);
        Assert.True(h.Start().Ok, h.Reply.Message);
        Assert.Equal(3, Game(h).Bot);
        Assert.Equal(0, Core(h).Team[2]);
        Assert.Equal(1, Core(h).Team[3]);
    }

    [Fact]
    public void Only_the_host_alone_can_call_the_bot()
    {
        var h = new RoomHarness("hockey");
        h.Join("Оля");
        h.Join("Петро");
        Assert.False(h.Act(1, LiveBots.Toggle, new { on = true }).Ok);
        Assert.False(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);  // двоє — бот не потрібен
        Assert.True(h.Start().Ok);
        Assert.Equal(-1, Game(h).Bot);
    }

    [Fact]
    public void A_friend_sitting_down_sends_the_bot_away()
    {
        var h = new RoomHarness("hockey");
        h.Join("Оля");
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        h.Join("Петро");
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("bot").ValueKind);
        Assert.True(h.Start().Ok, h.Reply.Message);
        Assert.Equal(-1, Game(h).Bot);
        Assert.True(Core(h).Plays[0] && Core(h).Plays[1]);
        Assert.Null(h.Room.SafeSeatBot(2));
    }

    [Fact]
    public void Bot_opponent_defends_its_own_goal_and_beats_an_idle_human_without_awards()
    {
        var h = Solo("normal", "5", 7);
        var c = Core(h);
        var hits = 0;
        for (var t = 0; t < 20000 && h.Room.Status == RoomStatus.Playing; t++)
        {
            h.Tick();
            if (c.HitBy == 1) hits++;
        }
        output.WriteLine($"ударів бота {hits}, рахунок {c.S[0]}:{c.S[1]}");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal(5, c.S[1]);
        var fin = h.Finished.Single();
        Assert.Empty(fin.Result.Winners);
        Assert.StartsWith("🤖 Бот переміг 5:", h.Room.Result!.Verdict ?? "");
        Assert.Empty(h.Awards);
    }

    [Fact]
    public void Bot_opponent_saves_shots_at_its_own_goal()
    {
        // шайба з центру летить у праві ворота — там стереже бот-суперник (руді, місце 1)
        foreach (var lvl in new[] { LiveBots.Level.Easy, LiveBots.Level.Normal, LiveBots.Level.Hard })
        {
            var saved = 0;
            for (var k = 0; k < 20; k++)
            {
                var c = new HockeyCore(new Random(1)) { BotLevel = lvl };
                c.Reset([true, true, false, false]);
                c.StartIn = 0;
                c.Pads[0].X = 30;
                c.Aim(0, 30, 60);
                var y0 = 20 + k * 4;
                c.Puck = new ArenaBody(90, y0, HockeyCore.PuckR, 1) { Vx = 380, Vy = (60 + (k % 5 - 2) * 6 - y0) * 380 / 110.0 };
                var goal = false;
                for (var t = 0; t < 40 && !goal; t++)
                {
                    c.BotThink(1);
                    goal = c.Step() >= 0;
                }
                if (!goal) saved++;
            }
            output.WriteLine($"{lvl}: відбив {saved} з 20");
            Assert.True(saved >= (lvl == LiveBots.Level.Easy ? 6 : 12), $"{lvl}: відбив лише {saved} з 20");
        }
    }

    [Fact]
    public void Hard_bot_concedes_noticeably_less_than_easy_against_the_same_dummy()
    {
        var res = new Dictionary<LiveBots.Level, (int D, int B)>();
        foreach (var lvl in new[] { LiveBots.Level.Easy, LiveBots.Level.Normal, LiveBots.Level.Hard })
        {
            int d = 0, b = 0;
            foreach (var seed in new[] { 1, 2, 3 })
            {
                var (x, y) = Duel(lvl, seed, 15000);
                d += x;
                b += y;
            }
            res[lvl] = (d, b);
            output.WriteLine($"{lvl}: манекен {d} — бот {b}");
        }
        var e = res[LiveBots.Level.Easy];
        var n = res[LiveBots.Level.Normal];
        var hd = res[LiveBots.Level.Hard];
        double Share((int D, int B) r) => r.D / (double)Math.Max(1, r.D + r.B);
        Assert.True(Share(hd) < Share(e) * 0.7, $"сильний пропускає частку {Share(hd):0.00}, легкий {Share(e):0.00}");
        Assert.True(Share(n) < Share(e), "звичайний мусить бути сильнішим за легкого");
        Assert.True(hd.D > 0, "сильний бот не мусить бути стіною");
        Assert.True(e.D > e.B, "легкого манекен мусить обігравати");
    }

    [Fact]
    public void Human_beats_an_easy_bot_and_gets_a_verdict_but_no_awards()
    {
        var h = Solo("easy", "5", 3);
        var c = Core(h);
        for (var t = 0; t < 40000 && h.Room.Status == RoomStatus.Playing; t++)
        {
            if (c.Plays[0]) Dummy(c, 0);
            h.Tick();
        }
        output.WriteLine($"рахунок {c.S[0]}:{c.S[1]}");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal(5, c.S[0]);
        Assert.Equal([0], h.Finished.Single().Result.Winners);
        Assert.Equal($"🏆 Оля — перемога над легким ботом 5:{c.S[1]}", h.Room.Result!.Verdict);
        // «суха» перемога й камбек не видаються: партія з ботом — без ачівок
        Assert.DoesNotContain(h.Awards, a => a.Reason.StartsWith("ach:hockey"));
    }

    [Fact]
    public void Level_comes_from_the_table_option_for_the_opponent_and_the_trio_partner()
    {
        Assert.Equal(LiveBots.Level.Hard, Core(Solo("hard")).BotLevel);
        Assert.Equal(LiveBots.Level.Easy, Core(Solo("easy")).BotLevel);
        var h = new RoomHarness("hockey", new { botlvl = "hard" });
        foreach (var n in new[] { "Оля", "Петро", "Ганна" }) h.Join(n);
        Assert.True(h.Start().Ok);
        Assert.Equal(3, Game(h).Bot);
        Assert.Equal(LiveBots.Level.Hard, Core(h).BotLevel);
        Assert.Equal(LiveBots.Name, h.Room.SafeSeatBot(3));
    }

    [Fact]
    public void Human_leaving_mid_match_ends_it_without_winners()
    {
        var h = Solo();
        h.Tick(100);
        h.Leave("Оля");                                                   // кімната без людей зникає, але партію закрито
        Assert.Empty(h.Finished.Single().Result.Winners);
    }

    [Fact]
    public void Bot_can_be_sent_away_and_the_table_is_alone_again()
    {
        var h = new RoomHarness("hockey");
        h.Join("Оля");
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = false }).Ok);
        Assert.False(h.View(0).GetProperty("botWanted").GetBoolean());
        Assert.Equal(LiveBots.AloneText, h.Start().Message);
    }

    [Fact]
    public void Bot_thinks_fast()
    {
        foreach (var lvl in new[] { LiveBots.Level.Easy, LiveBots.Level.Hard })
        {
            var c = new HockeyCore(new Random(5)) { BotLevel = lvl };
            c.Reset([true, true, false, false]);
            for (var t = 0; t < 200; t++) { c.BotThink(1); c.Step(); }
            var sw = Stopwatch.StartNew();
            for (var t = 0; t < 1000; t++) c.BotThink(1);
            sw.Stop();
            output.WriteLine($"{lvl}: 1000 думок бота — {sw.Elapsed.TotalMilliseconds:0.00} мс");
            Assert.True(sw.Elapsed.TotalMilliseconds < 200, $"{sw.Elapsed.TotalMilliseconds} мс");
        }
    }
}
