using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Рецензія v13, частина 1 (економіка): мірка плати «хвилина гри» не росте від тимчасових бафів («Свято», фестиваль
/// Толоки), а розпис посла жеребкується за запасом у коморі. Решта виправлень — у тестах своїх систем
/// (<see cref="ClickerCoopTests"/>, <see cref="ClickerChoresTests"/>, <see cref="ClickerCatchDayTests"/>, <see cref="ClickerWishTests"/>).
/// </summary>
public class ClickerV13FixTests
{
    static Clicker Game(RoomHarness h) => (Clicker)h.Room.Game;

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static RoomHarness Wheel(params string[] styles)
    {
        var h = new RoomHarness("clicker");
        h.Solo("Оля");
        Patch(h, s =>
        {
            s["pots"] = 0;
            s["total"] = 1e40;
            s["upgrades"] = new JsonObject { ["apprentice"] = 40, ["wheel"] = 30 };
            var built = Clicker.Buildings.Select(b => b.Key);                   // Толока добудована: нічого не відкладає
            s["toloka"] = new JsonObject { ["built"] = new JsonArray(built.Select(x => (JsonNode)x).ToArray()) };
            s["styles"] = new JsonArray(styles.Select(x => (JsonNode)x).ToArray());
        });
        return h;
    }

    static double PlayMinute(RoomHarness h) { lock (h.Room.Sync) return Game(h).PlayMinute; }
    static double PerSecond(RoomHarness h) => h.View(0).GetProperty("perSecond").GetDouble();
    static double Pots(RoomHarness h) => h.View(0).GetProperty("pots").GetDouble();

    [Fact]
    public void The_play_minute_does_not_grow_under_the_feast_while_the_passive_income_doubles()
    {
        var h = Wheel();
        var plainMinute = PlayMinute(h);
        var plain = PerSecond(h);
        Assert.True(plainMinute > 0);
        lock (h.Room.Sync) Game(h).BuffBank("feast", 60, "За тест");
        Assert.True(h.Act(0, "buff", new { kind = "feast" }).Ok);
        Assert.Equal(2.0, PerSecond(h) / plain, 6);                     // сам баф діє: заробіток ×2
        Assert.Equal(plainMinute, PlayMinute(h), plainMinute * 1e-9);   // а мірка плати — ні
        Assert.Equal(2.0, PerSecond(h) / plain, 6);                     // і після підрахунку баф нікуди не подівся
    }

    [Fact]
    public void The_play_minute_does_not_grow_under_the_toloka_festival_either()
    {
        var h = Wheel();
        var plainMinute = PlayMinute(h);
        var plain = PerSecond(h);
        Patch(h, s => s["toloka"]!["festivalUntil"] = h.Clock.UtcNow.AddMinutes(30).ToString("O"));
        Assert.Equal(2.0, PerSecond(h) / plain, 6);
        Assert.Equal(plainMinute, PlayMinute(h), plainMinute * 1e-9);
    }

    [Fact]
    public void A_ship_load_pays_the_same_with_and_without_the_feast()
    {
        double Run(bool feast)
        {
            var h = Wheel("athens");
            Patch(h, s =>
            {
                var now = h.Clock.UtcNow;
                s["craft"]!["items"] = new JsonObject { ["pot||3"] = 10 };
                s["trade"] = new JsonObject
                {
                    ["slava"] = 0, ["firstDone"] = true, ["next"] = now.AddMinutes(40).ToString("O"), ["shipId"] = 5, ["envoyNext"] = now.AddHours(12).ToString("O"),
                    ["ships"] = new JsonArray(new JsonObject
                    {
                        ["id"] = 1, ["port"] = "delft", ["ware"] = "", ["style"] = "", ["quality"] = 3, ["count"] = 40, ["lux"] = 0,
                        ["at"] = now.ToString("O"), ["until"] = now.AddHours(8).ToString("O"), ["envoy"] = false,
                    }),
                };
            });
            if (feast)
            {
                lock (h.Room.Sync) Game(h).BuffBank("feast", 60, "За тест");
                Assert.True(h.Act(0, "buff", new { kind = "feast" }).Ok);
            }
            var before = Pots(h);
            var r = h.Act(0, "trade", new { @do = "loadAll", id = 1 });
            Assert.True(r.Ok, r.Message);
            return Pots(h) - before;
        }

        var plain = Run(false);
        Assert.True(plain > 0);
        Assert.Equal(plain, Run(true), plain * 1e-9);
    }

    [Fact]
    public void The_envoy_asks_far_more_often_for_the_style_that_is_in_stock()
    {
        var h = Wheel("athens", "delft");
        Patch(h, s => s["craft"]!["items"] = new JsonObject { ["pot|athens|3"] = 400 });
        var athens = 0;
        const int rounds = 60;
        for (var i = 0; i < rounds; i++)
        {
            h.Clock.Advance(Clicker.EnvoyGap + TimeSpan.FromMinutes(1));
            var e = h.View(0).GetProperty("trade").GetProperty("envoy");
            Assert.Equal(JsonValueKind.Object, e.ValueKind);
            if (e.GetProperty("style").GetString() == "athens") athens++;
        }
        // Вага: 400 + 5 проти 0 + 5 — «афінський» майже завжди (очікувано ~98 %).
        Assert.InRange(athens, rounds * 8 / 10, rounds);
    }
}
