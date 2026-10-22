using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// 21.10, серія владіка 1 861 через кілька хвилин після v12: відкрита картка сама шле <c>look</c> (підмайстер відкрив
/// горно, купець вернувся, корабель прийшов, глек розбився) — і сервер вважав гончаря біля кола, хоч той мовчав; а вікна
/// кола, крім «Що нового» й «Поки тебе не було», глеків не тримали, і ті летіли в смугу справа, за край картки.
/// </summary>
public class ClickerPollPresenceTests
{
    static RoomHarness Wheel()
    {
        var h = new RoomHarness("clicker");
        h.Solo("Оля");
        return h;
    }

    static JsonElement Fall(RoomHarness h) => h.View(0).GetProperty("fall");
    static DateTimeOffset FallAt(RoomHarness h) => Fall(h).GetProperty("at").GetDateTimeOffset();
    static int Streak(RoomHarness h) => Fall(h).GetProperty("streak").GetInt32();
    static ActResult Act(RoomHarness h, string action, object? payload = null) => h.Act(0, action, payload);
    static ActResult Hold(RoomHarness h, bool on) => Act(h, "hold", new { on });

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    /// <summary>Глек з полиці злетить за <paramref name="seconds"/> с від «зараз».</summary>
    static void FallIn(RoomHarness h, double seconds) => Patch(h, s =>
    {
        var at = h.Clock.UtcNow.AddSeconds(seconds);
        s["fall"] = new JsonObject { ["at"] = at.ToString("O"), ["until"] = (at + Clicker.FallShown).ToString("O"), ["x"] = 40 };
    });

    [Fact]
    public void Timer_looks_of_an_open_card_do_not_keep_a_silent_potter_at_the_wheel()
    {
        var h = Wheel();
        Patch(h, s => s["fallStreak"] = 9);
        Assert.True(Act(h, "spin", PotterHands.Human(5)).Ok);   // останнє, що гончар зробив сам
        var hands = h.Clock.UtcNow;
        FallIn(h, 150);
        var jug = FallAt(h);
        // Картка видна, гончар мовчить, а її таймери щопівхвилини питають свіжий вид.
        for (var i = 1; i <= 6; i++)
        {
            h.Clock.AdvanceMs(30_000);
            Assert.True(Act(h, "look").Ok);
            // Затих довше за дві хвилини ще до зльоту — вид так і каже: розіб'ється, то без нього.
            if (i == 5) Assert.Equal("away", Fall(h).GetProperty("miss").GetString());
        }
        Assert.True(FallAt(h) > jug + Clicker.FallShown);       // той глек розбився, на полиці вже наступний

        Assert.Equal(9, Streak(h));
        Assert.Equal(hands, Fall(h).GetProperty("seen").GetDateTimeOffset());
    }

    [Fact]
    public void An_open_window_holds_jugs_for_as_long_as_the_card_confirms_it()
    {
        var h = Wheel();
        Patch(h, s => s["fallStreak"] = 9);
        FallIn(h, 5);
        Assert.True(Hold(h, true).Ok);
        // Хвилину читає вікно: клієнт підтверджує його щодесять секунд — стеля в пів хвилини не настає.
        for (var i = 0; i < 6; i++)
        {
            h.Clock.AdvanceMs(10_000);
            Assert.True(Hold(h, true).Ok);
            Assert.True(FallAt(h) > h.Clock.UtcNow);
        }
        Assert.True(Hold(h, false).Ok);

        Assert.True(FallAt(h) >= h.Clock.UtcNow + Clicker.MinigameAfter);
        Assert.Equal(9, Streak(h));
    }

    [Fact]
    public void Every_wheel_window_holds_jugs_and_the_card_confirms_it_within_the_cap()
    {
        var js = File.ReadAllText(Hlechyky.Paths.Resolve("web/games/clicker.js")).Replace("\r\n", "\n");
        // Тримають усі вікна, а не лише «Що нового» й «Поки тебе не було» (HOLD_CLS).
        Assert.DoesNotContain("HOLD_CLS", js);
        Assert.Equal(2, Regex.Matches(js, @"st\.ov\.hold = true;\n\s+holdJugs\(st, true\);").Count);   // overlay і nextQueued
        var renew = Regex.Match(js, @"const HOLD_RENEW_MS = (\d+) \* 1000;");
        Assert.True(renew.Success);
        Assert.True(TimeSpan.FromSeconds(int.Parse(renew.Groups[1].Value)) * 2 < Clicker.ResultCap);
        Assert.Contains("if (holdOpen(st) && visible(st)) holdSay(st, true); }, HOLD_RENEW_MS)", js);
    }
}
