using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Серверні правки зручності v12 за плейтестом 21.10 (docs/games/specs/clicker-v12-ux.md §1).</summary>
public class ClickerV12UxTests
{
    static RoomHarness Wheel(string nick = "Оля")
    {
        var h = new RoomHarness("clicker");
        h.Solo(nick);
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    [Fact]
    public void The_away_card_once_seen_is_not_sent_again_on_any_device()
    {
        var h = Wheel();
        Patch(h, s =>
        {
            s["craft"]!["away"] = new JsonObject
            {
                ["at"] = h.Clock.UtcNow.ToString("O"), ["seconds"] = 3600, ["pots"] = 100, ["formed"] = 0, ["notes"] = new JsonArray(),
            };
        });
        var away = View(h).GetProperty("away");
        Assert.Equal(JsonValueKind.Object, away.ValueKind);
        // Чужа (новіша) мітка — не той запис: лишається.
        Assert.True(h.Act(0, "away", new { at = h.Clock.UtcNow.AddMinutes(5).ToString("O") }).Ok);
        Assert.Equal(JsonValueKind.Object, View(h).GetProperty("away").ValueKind);
        Assert.True(h.Act(0, "away", new { at = away.GetProperty("at").GetString() }).Ok);
        Assert.Equal(JsonValueKind.Null, View(h).GetProperty("away").ValueKind);
        // Переживає збереження: на другому пристрої картки теж нема.
        Patch(h, _ => { });
        Assert.Equal(JsonValueKind.Null, View(h).GetProperty("away").ValueKind);
    }
}
