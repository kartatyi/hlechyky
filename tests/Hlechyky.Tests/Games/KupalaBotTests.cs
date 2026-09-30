using System.Diagnostics;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Купальська ніч» соло з 🤖 ботами: виклик, старт, боти справді пускають вінки й полюють на освітлених (сильний — помітно краще за легкого),
/// хто кого переміг, без нагород, друг за столом, вихід людини, заміри мозку.
/// </summary>
[Collection(SerialPerf.Name)]
public class KupalaBotTests(ITestOutputHelper output)
{
    static RoomHarness Solo(string lvl = "normal", int seed = 7, string rounds = "1")
    {
        var h = new RoomHarness("kupala", new { botlvl = lvl, rounds }, seed: seed);
        h.Join("Оля");
        Assert.True(h.Act(0, "bot", new { on = true }).Ok);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static Kupala G(RoomHarness h) => (Kupala)h.Room.Game;
    static KupalaSeat S(RoomHarness h, int seat) => G(h).SeatForTests(seat);
    static KupalaVillager V(RoomHarness h, int seat) => G(h).CoreForTests.V[S(h, seat).Me];

    static void Go(RoomHarness h)
    {
        for (var i = 0; i < 200 && G(h).Phase != Kupala.PhaseGo; i++) h.Tick();
        Assert.Equal(Kupala.PhaseGo, G(h).Phase);
    }

    /// <summary>До кінця раунду (розкриття чи кінця партії).</summary>
    static void Round(RoomHarness h, Action<int>? each = null)
    {
        for (var t = 0; t < 3000 && G(h).Phase == Kupala.PhaseGo; t++)
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
        var h = new RoomHarness("kupala");
        h.Join("Оля");
        var r = h.Start();
        Assert.False(r.Ok);
        Assert.Equal(LiveBots.AloneText, r.Message);
        Assert.True(h.View(0).GetProperty("botOffer").GetBoolean());
        Assert.True(h.Act(0, "bot", new { on = true }).Ok);
        Assert.Equal("[1,2]", h.View(0).GetProperty("bot").GetRawText());
        Assert.True(h.Start().Ok, h.Reply.Message);
        Assert.Equal([1, 2], G(h).BotsForTests);
        Assert.Equal(LiveBots.Name, h.Room.SafeSeatBot(1));
        Assert.Equal(LiveBots.Name, h.Room.SafeSeatBot(2));
        Assert.Null(h.Room.SafeSeatBot(3));
        Assert.True(S(h, 1).Plays && S(h, 1).Bot && S(h, 1).Me >= 0);
        Assert.Equal(1 + 2 + Kupala.BotsFor(3), G(h).CoreForTests.N);
        Assert.Equal("normal", h.View(0).GetProperty("botLvl").GetString());
    }

    [Fact]
    public void Level_comes_from_the_table_option()
    {
        var h = Solo("hard");
        Assert.Equal("hard", h.View(0).GetProperty("botLvl").GetString());
        Assert.Equal(LiveBots.Level.Hard, LiveBots.Read(new Dictionary<string, string> { ["botlvl"] = "hard" }));
    }

    [Fact]
    public void Round_with_one_human_does_not_end_at_once_and_bots_walk_and_shop()
    {
        var h = Solo("normal", seed: 11);
        Go(h);
        var start = (V(h, 1).X, V(h, 1).Y, V(h, 2).X, V(h, 2).Y);
        h.Tick(100);
        Assert.Equal(Kupala.PhaseGo, G(h).Phase);                       // «лишився один живий» не спрацював
        Assert.NotEqual(start, (V(h, 1).X, V(h, 1).Y, V(h, 2).X, V(h, 2).Y));
        var bought = 0;
        Round(h, _ => bought = S(h, 1).Wreaths + S(h, 2).Wreaths);
        Assert.True(bought >= 1, $"боти за раунд пустили лише {bought}");
    }

    [Fact]
    public void Bot_player_walks_like_the_crowd_never_turning_back_mid_step()
    {
        var h = Solo("easy", seed: 3);
        Go(h);
        // розворот на ходу — те, що видає людину; юрба робить це зрідка (вирівнювання на смугу біля межі клітинки),
        // бот мусить не частіше за пересічного селянина
        var core = G(h).CoreForTests;
        var turns = new int[core.N];
        var dir = new int[core.N];
        var mov = new bool[core.N];
        Round(h, _ =>
        {
            foreach (var v in core.V)
            {
                if (v.Moving && mov[v.Id] && v.Dir == (dir[v.Id] + 2) % 4) turns[v.Id]++;
                mov[v.Id] = v.Moving;
                dir[v.Id] = v.Dir;
            }
        });
        var npc = core.V.Where(v => v.Owner < 0).Average(v => turns[v.Id]);
        var bots = (turns[S(h, 1).Me] + turns[S(h, 2).Me]) / 2.0;
        output.WriteLine($"розворотів за раунд: юрба {npc:F2}, боти {bots:F2}");
        Assert.True(bots <= npc + 1.5, $"боти смикаються: {bots} проти {npc} у юрби");
        Assert.Equal(0, S(h, 1).Slaps + S(h, 2).Slaps);                  // легкий не полює
    }

    /// <summary>
    /// «Людина-манекен», що видає себе: смикається туди-сюди. Сильний бот вистежує її майже щоразу, легкий — ніколи
    /// (він і не стріляє).
    /// </summary>
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
                // смикається туди-сюди біля вогнища — у світлі, де його видно
                var fire = KupalaMap.FireRing[seed % KupalaMap.FireRing.Length][0];
                V(h, 0).X = KupalaMap.CenterX(fire); V(h, 0).Y = KupalaMap.CenterY(fire);
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
        // людина стоїть поруч із ботом і б'є першого-ліпшого NPC — камінець видно всім
        me.X = bot.X; me.Y = bot.Y;
        var fire = KupalaMap.FireRing[0][0];
        me.X = bot.X = KupalaMap.CenterX(fire); me.Y = bot.Y = KupalaMap.CenterY(fire);    // біля вогнища — видно
        var npc = G(h).CoreForTests.V.First(q => q.Owner < 0 && q.Upright);
        npc.X = me.X; npc.Y = me.Y;
        Assert.True(h.Act(0, "slap", new { id = npc.Id }).Ok, h.Reply.Message);
        h.Tick();                                                         // ляпас боти бачать у кадрі наступного тика
        Assert.True(G(h).EyeForTests.Sus(me.Id) >= CrowdEye.Shooter);
        Assert.Equal(0, G(h).EyeForTests.Sus(npc.Id));                   // упав — просто селянин
        for (var t = 0; t < 900 && S(h, 0).Alive; t++) h.Tick();
        Assert.False(S(h, 0).Alive);
    }

    [Fact]
    public void Passive_human_loses_to_the_bots_without_a_winner_and_with_a_bot_verdict()
    {
        var h = Solo("normal", seed: 9);
        ToEnd(h);
        var fin = h.Finished.Single();
        Assert.Empty(fin.Result.Winners);
        Assert.Matches("^(🤖|🤝)", h.Room.Result!.Verdict);
        Assert.Contains(LiveBots.Name, h.Room.Result!.Text);
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
            me.X = bot.X; me.Y = bot.Y;       // підкрався впритул (тестові ноги)
            for (var i = 0; i < 100 && (S(h, 0).SlapCool > 0 || me.Stun > 0); i++) h.Tick();
            me.X = bot.X; me.Y = bot.Y;
            Assert.True(h.Act(0, "slap", new { id = bot.Id }).Ok, h.Reply.Message);
            Assert.False(S(h, b).Alive);
        }
        ToEnd(h);
        Assert.Equal([0], h.Finished.Single().Result.Winners);
        Assert.StartsWith("🏆 Оля — перемога над легким", h.Room.Result!.Verdict);
        Assert.Empty(h.Awards);                                           // з ботами ачівок нема
        Assert.Empty(h.Scores);
    }

    [Fact]
    public void Friend_at_the_table_sends_the_bots_away()
    {
        var h = new RoomHarness("kupala");
        h.Join("Оля");
        Assert.True(h.Act(0, "bot", new { on = true }).Ok);
        h.Join("Петро");
        Assert.True(h.Start().Ok, h.Reply.Message);
        Assert.Empty(G(h).BotsForTests);
        Assert.Null(h.Room.SafeSeatBot(2));
        Assert.False(h.Act(0, "bot", new { on = false }).Ok);           // посеред партії бота не женуть
    }

    [Fact]
    public void Human_leaving_mid_match_ends_it_without_winners()
    {
        var h = Solo();
        Go(h);
        h.Tick(50);
        h.Leave("Оля");                                                   // стіл без людей каркас прибирає одразу
        Assert.Empty(h.Finished.Single().Result.Winners);
    }

    [Fact]
    public void Bot_think_is_cheap()
    {
        long Run(bool bots)
        {
            var h = bots ? Solo("hard", 2, "5") : new RoomHarness("kupala", new { rounds = "5" }, seed: 2);
            if (!bots)
            {
                h.Join("Оля"); h.Join("Петро"); h.Join("Ганна");
                Assert.True(h.Start().Ok);
            }
            h.Tick(100);
            var sw = Stopwatch.StartNew();
            h.Tick(1000);
            return sw.ElapsedTicks;
        }
        Run(true);
        var with = Run(true);
        var without = Run(false);
        var perTick = (with - without) * 1000.0 / Stopwatch.Frequency;   // мс на 1000 тиків → мкс на тик
        output.WriteLine($"1000 тиків: з ботами {with * 1000.0 / Stopwatch.Frequency:F1} мс, без {without * 1000.0 / Stopwatch.Frequency:F1} мс, мозок ≈ {perTick:F1} мкс/тик");
        Assert.True(with * 1000.0 / Stopwatch.Frequency < 1000, "1000 тиків з ботами довше за секунду");
    }
}
