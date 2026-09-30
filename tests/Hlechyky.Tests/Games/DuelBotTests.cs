using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Дуель соло з ботом (spec duel.md, «Соло з ботом»): виклик, як стріляє бот за рівнями, без рекордів і нагород.</summary>
public class DuelBotTests
{
    static RoomHarness Alone(string lvl = "normal", int seed = 42, IServiceProvider? services = null)
    {
        var h = new RoomHarness("duel", new { botlvl = lvl }, seed, services);
        h.Join("Оля");
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    /// <summary>
    /// Людина-манекен: на кожен «ВОГОНЬ!» тисне рівно за <paramref name="ms"/> мс (null — не стріляє зовсім). Грає до кінця
    /// партії; повертає реакції бота й причини раундів.
    /// </summary>
    static (List<long> botMs, List<string> reasons) PlayOut(RoomHarness h, int? ms, int maxTicks = 20000)
    {
        var bot = ((Duel)h.Room.Game).Bot;
        var botMs = new List<long>();
        var reasons = new List<string>();
        var shot = false;
        var inResult = false;
        for (var t = 0; t < maxTicks && h.Room.Status == RoomStatus.Playing; t++)
        {
            var v = h.View(0);
            var ph = v.GetProperty("phase").GetString();
            if (ph == "fire" && ms is { } m && !shot)
            {
                // Реакцію людини ставимо точно, як у DuelTests: годинник між тиками.
                h.Clock.AdvanceMs(m);
                h.Input(0, "shoot");
                shot = true;
            }
            if (ph != "fire") shot = false;
            var result = ph is "result" or "done";
            if (result && !inResult && v.GetProperty("last") is { ValueKind: JsonValueKind.Object } l)
            {
                reasons.Add(l.GetProperty("reason").GetString()!);
                if (l.GetProperty("ms")[bot] is { ValueKind: JsonValueKind.Number } b) botMs.Add(b.GetInt64());
            }
            inResult = result;
            h.Tick();
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        return (botMs, reasons);
    }

    [Fact]
    public void Alone_the_duel_asks_for_a_bot_and_the_bot_takes_the_free_seat()
    {
        var h = new RoomHarness("duel");
        h.Join("Оля");
        var r = h.Start();
        Assert.False(r.Ok);
        Assert.Equal(LiveBots.AloneText, r.Message);
        Assert.True(h.View(0).GetProperty("botOffer").GetBoolean());
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.Equal(1, h.View(0).GetProperty("bot").GetInt32());
        Assert.True(h.View(0).GetProperty("botWanted").GetBoolean());
        Assert.True(h.Start().Ok, h.Reply.Message);
        var g = (Duel)h.Room.Game;
        Assert.Equal(1, g.Bot);
        Assert.Equal(LiveBots.Name, g.SeatBot(1));
        Assert.Null(g.SeatBot(0));
        Assert.False(h.Act(0, LiveBots.Toggle, new { on = false }).Ok);   // посеред дуелі бота не проганяють
    }

    [Fact]
    public void The_bot_shoots_on_fire_and_wins_against_a_sleeping_human_without_rewards()
    {
        var h = Alone();
        var (botMs, _) = PlayOut(h, null);
        Assert.True(botMs.Count >= 3, $"бот вистрілив лише {botMs.Count} разів");
        Assert.All(botMs, ms => Assert.InRange(ms, 200, 800));
        var fin = h.Finished.Single();
        Assert.Empty(fin.Result.Winners);
        Assert.StartsWith("🤖 Бот переміг 3:", h.Room.Result!.Verdict);
        Assert.Contains("🤖 бот", h.Room.Result!.Text);
    }

    [Fact]
    public void A_quick_human_beats_the_easy_bot_and_the_verdict_names_its_level()
    {
        var h = Alone("easy");
        PlayOut(h, 200);
        var fin = h.Finished.Single();
        Assert.Equal([0], fin.Result.Winners);
        Assert.StartsWith("🏆 Оля — перемога над легким ботом 3:", h.Room.Result!.Verdict);
    }

    [Fact]
    public void Levels_differ_like_human_hands_hard_is_fast_but_not_a_script()
    {
        var rng = new Random(7);
        double Mean(LiveBots.Level lvl, out int min)
        {
            var b = new DuelBot();
            b.Reset(lvl);
            var xs = Enumerable.Range(0, 4000).Select(_ => b.Reaction(rng)).ToArray();
            min = xs.Min();
            return xs.Average();
        }
        var easy = Mean(LiveBots.Level.Easy, out var easyMin);
        var normal = Mean(LiveBots.Level.Normal, out _);
        var hard = Mean(LiveBots.Level.Hard, out var hardMin);
        Assert.InRange(easy, 420, 500);       // повільна людина
        Assert.InRange(normal, 290, 340);     // пересічна рука з мережею
        Assert.InRange(hard, 220, 260);       // дуже швидка людина…
        Assert.True(hardMin >= 180, $"сильний бот вистрілив за {hardMin} мс — це вже скрипт");   // …але не надлюдина
        Assert.True(easyMin >= 260);
    }

    [Fact]
    public void Against_the_same_dummy_the_hard_bot_takes_more_rounds_than_the_easy_one()
    {
        int BotRounds(string lvl)
        {
            var won = 0;
            for (var seed = 1; seed <= 12; seed++)
            {
                var h = Alone(lvl, seed);
                PlayOut(h, 300);
                won += h.View(0).GetProperty("wins")[1].GetInt32();
            }
            return won;
        }
        var easy = BotRounds("easy");
        var hard = BotRounds("hard");
        Assert.True(hard > easy + 10, $"сильний узяв {hard} раундів, легкий {easy}");
    }

    [Fact]
    public void The_easy_bot_sometimes_false_starts_and_the_hard_one_rarely_does()
    {
        int FalseStarts(string lvl)
        {
            var n = 0;
            for (var seed = 1; seed <= 25; seed++) n += PlayOut(Alone(lvl, seed), null).reasons.Count(r => r == "false");
            return n;
        }
        var easy = FalseStarts("easy");
        Assert.True(easy >= 3, $"легкий бот жодного разу не поспішив ({easy})");
        Assert.True(FalseStarts("hard") < easy);
    }

    [Fact]
    public void The_bot_flinches_at_decoys_more_often_on_easy()
    {
        int Flinches(string lvl)
        {
            var n = 0;
            for (var seed = 1; seed <= 25; seed++)
            {
                var h = new RoomHarness("duel", new { botlvl = lvl, bait = "on" }, seed);
                h.Join("Оля");
                h.Act(0, LiveBots.Toggle, new { on = true });
                h.Start();
                n += PlayOut(h, 250).reasons.Count(r => r == "false");
            }
            return n;
        }
        Assert.True(Flinches("easy") > Flinches("hard"));
    }

    [Fact]
    public void A_bot_duel_leaves_records_and_the_reaction_table_alone()
    {
        var clock = new FakeClock();
        var rec = new DuelRecords((_, _, _) => [], clock, inline: true);
        var h = Alone("easy", services: RoomHarness.WithService(rec));
        PlayOut(h, 150);
        Assert.Equal([0], h.Finished.Single().Result.Winners);
        Assert.Null(rec.Personal("оля"));        // 150 мс — рекорд був би, але не з ботом
        Assert.Empty(h.Scores);                   // таблиця реакцій (і ачівка «Швидка рука» з неї) — порожня
        Assert.DoesNotContain(h.Awards, a => a.Reason.StartsWith("ach:"));
    }

    [Fact]
    public void A_friend_sitting_down_sends_the_bot_away_and_the_duel_is_human()
    {
        var h = new RoomHarness("duel");
        h.Join("Оля");
        h.Act(0, LiveBots.Toggle, new { on = true });
        h.Join("Петро");                                   // стіл повний — дуель стартує сама, людська
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        var g = (Duel)h.Room.Game;
        Assert.Equal(-1, g.Bot);
        Assert.False(g.BotGame);
        Assert.Null(g.SeatBot(1));
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("bot").ValueKind);
    }

    [Fact]
    public void The_human_leaving_mid_duel_ends_it()
    {
        var h = Alone();
        h.Tick(40);
        h.Leave("Оля");
        // Порожній стіл каркас прибирає; партію закрито без переможців.
        Assert.Empty(h.Finished.Single().Result.Winners);
    }

    [Fact]
    public void The_bot_level_comes_from_the_table_option()
    {
        var h = Alone("hard");
        Assert.Equal("hard", h.View(0).GetProperty("botLvl").GetString());
        Assert.Contains(((Duel)h.Room.Game).Info.Options, o => o.Key == LiveBots.LevelOption.Key);
    }

    [Fact]
    public void The_bot_thinks_cheaply()
    {
        var h = Alone();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000 && h.Room.Status == RoomStatus.Playing; i++) h.Tick();
        sw.Stop();
        // Тик разом із каркасом харнеса; сама думка бота — копійки, але межа щедра на повільну машину.
        Assert.True(sw.Elapsed.TotalMilliseconds < 1000, $"1000 тиків — {sw.Elapsed.TotalMilliseconds:F0} мс");
    }
}
