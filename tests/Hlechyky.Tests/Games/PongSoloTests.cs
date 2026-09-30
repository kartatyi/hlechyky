using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Понг соло з ботом (30.09): спільне «🤖 + бот» (SoloBot) і рівні бота опцією столу — реакція, похибка, відскоки.
/// Сила рівнів — проти тієї самої «людини-стіни».
/// </summary>
[Collection(SerialPerf.Name)]
public class PongSoloTests(ITestOutputHelper output)
{
    static RoomHarness Solo(string lvl, string len = "long", int seed = 5)
    {
        var h = new RoomHarness("pong", new { len, botlvl = lvl }, seed);
        h.Join("Оля");
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static JsonElement? LastFrameOrNull(RoomHarness h)
    {
        var f = h.Outbox.OfType<RoomFrame>().LastOrDefault();
        return f is null ? null : Views.Json(f.Frame);
    }

    /// <summary>Партія проти «стіни» (ракетка завжди під м'ячем, трохи нижче — б'є краєм): (очки людини, очки бота).</summary>
    static (int Human, int Bot) Match(string lvl, int seed)
    {
        var h = Solo(lvl, "long", seed);
        for (var t = 0; t < 60000 && h.Room.Status == RoomStatus.Playing; t++)
        {
            if (LastFrameOrNull(h) is { } f) h.Input(0, "to", new { y = f.GetProperty("by").GetDouble() + 6 });
            h.Tick();
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var s = h.View(0).GetProperty("scores");
        return (s[0].GetInt32(), s[1].GetInt32());
    }

    [Fact]
    public void Level_option_is_in_the_catalog_and_reaches_the_view()
    {
        var h = new RoomHarness("pong", new { botlvl = "hard" });
        h.Join("Оля");
        Assert.Contains(h.Room.Info.Options!, o => o.Key == LiveBots.LevelOption.Key);
        Assert.Equal("hard", h.View(0).GetProperty("botLvl").GetString());
        Assert.True(h.View(0).GetProperty("botOffer").GetBoolean());
        Assert.Equal(LiveBots.AloneText, h.Start().Message);
    }

    [Fact]
    public void Bot_seat_is_named_by_the_frame_and_friends_send_it_away()
    {
        var h = Solo("normal");
        Assert.Equal(LiveBots.Name, h.Room.SafeSeatBot(1));
        Assert.Null(h.Room.SafeSeatBot(0));

        var two = new RoomHarness("pong");
        two.Join("Оля");
        Assert.True(two.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        two.Join("Петро");
        Assert.True(two.Start().Ok);
        Assert.Equal(-1, ((Pong)two.Room.Game).Bot);
        Assert.False(two.Act(1, LiveBots.Toggle, new { on = true }).Ok);
    }

    [Fact]
    public void Hard_bot_concedes_noticeably_less_than_easy_against_the_same_wall()
    {
        var share = new Dictionary<string, double>();
        foreach (var lvl in new[] { "easy", "normal", "hard" })
        {
            int hu = 0, bo = 0;
            foreach (var seed in new[] { 1, 2, 3 })
            {
                var (a, b) = Match(lvl, seed);
                hu += a;
                bo += b;
            }
            share[lvl] = hu / (double)(hu + bo);
            output.WriteLine($"{lvl}: стіна {hu} — бот {bo}");
        }
        Assert.True(share["hard"] < share["easy"] * 0.7, $"сильний пропускає {share["hard"]:0.00}, легкий {share["easy"]:0.00}");
        Assert.True(share["normal"] < share["easy"]);
        Assert.True(share["hard"] <= share["normal"]);
        Assert.True(share["hard"] > 0, "сильний бот не мусить бути стіною");
    }

    [Fact]
    public void Human_beats_an_easy_bot_with_a_level_in_the_verdict()
    {
        var h = Solo("easy", "short", 2);
        for (var t = 0; t < 60000 && h.Room.Status == RoomStatus.Playing; t++)
        {
            if (LastFrameOrNull(h) is { } f) h.Input(0, "to", new { y = f.GetProperty("by").GetDouble() + 6 });
            h.Tick();
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var fin = h.Finished.Single();
        Assert.Equal([0], fin.Result.Winners);
        Assert.StartsWith("🏆 Оля — перемога над легким ботом 5:", h.Room.Result!.Verdict);
        Assert.Empty(h.Awards);
    }

    [Fact]
    public void Bot_thinks_fast()
    {
        var h = Solo("hard");
        h.Tick(100);
        var sw = Stopwatch.StartNew();
        h.Tick(1000);
        sw.Stop();
        output.WriteLine($"1000 тиків партії з ботом — {sw.Elapsed.TotalMilliseconds:0.0} мс");
        Assert.True(sw.Elapsed.TotalMilliseconds < 2000);
    }
}
