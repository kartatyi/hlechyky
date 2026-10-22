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
    public void What_the_merchant_brought_while_away_is_its_own_line_not_the_wheel()
    {
        var h = Wheel();
        Patch(h, s => { s["pots"] = 1_000_000; s["total"] = 1_000_000; });
        var order = View(h).GetProperty("house").GetProperty("orders").GetProperty("board").EnumerateArray().First();
        var pay = order.GetProperty("pay").GetDouble();
        Assert.True(h.Act(0, "take", new { id = order.GetProperty("id").GetInt32() }).Ok);
        // Гончар пішов на довше, ніж їде купець, і довше за поріг простою.
        h.Clock.Advance(TimeSpan.FromMinutes(order.GetProperty("minutes").GetInt32() + 10));
        var away = View(h).GetProperty("away");
        Assert.Equal(JsonValueKind.Object, away.ValueKind);
        Assert.Contains(away.GetProperty("notes").EnumerateArray(), n => n.GetString()!.StartsWith("🐴 Купець привіз", StringComparison.Ordinal));
        // «Коло крутилось без тебе» — без купцевого: голе коло без пасиву, тож там нуль, а не плата купця.
        Assert.True(away.GetProperty("pots").GetDouble() < pay);
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

    [Fact]
    public void A_guests_nick_after_a_preposition_declines_the_word_guest_only()
    {
        Assert.Equal("гостя тест-микола", Clicker.NickGen("гість тест-микола"));
        Assert.Equal("Оля", Clicker.NickGen("Оля"));
        Assert.Equal("гостинна", Clicker.NickGen("гостинна"));
    }

    [Fact]
    public void Forming_from_an_album_cell_reminds_which_painting_it_is_for()
    {
        var h = Wheel();
        Patch(h, s => s["styles"] = new JsonArray("kosiv"));
        var r = h.Act(0, "form", new { ware = "pot", style = "kosiv" });
        Assert.True(r.Ok, r.Message);
        Assert.Contains("з розписом «", r.Message);
        Assert.DoesNotContain("ще треба відкрити", r.Message);
        // Уже на колі — однаково нагадуємо; розпис, якого нема, — так і кажемо.
        r = h.Act(0, "form", new { ware = "pot", style = "opishnia" });
        Assert.True(r.Ok, r.Message);
        Assert.Contains("уже на колі", r.Message);
        Assert.Contains("ще треба відкрити", r.Message);
        // Без розпису — як і було: на те саме мовчки.
        Assert.Equal(ActResult.Done, h.Act(0, "form", new { ware = "pot" }));
    }

    [Fact]
    public void A_look_from_a_hidden_tab_does_not_cut_the_time_away()
    {
        var h = Wheel();
        Assert.True(h.Act(0, "spin", PotterHands.Human(1)).Ok);
        Assert.True(h.Act(0, "sight", new { on = false }).Ok);
        // Схована вкладка сама питає свіжий вид (горно підмайстра, корабель…) — гончаря від цього не прибуває.
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        h.Act(0, "look");
        h.Clock.Advance(TimeSpan.FromMinutes(8));
        Assert.True(h.Act(0, "sight", new { on = true }).Ok);
        var away = View(h).GetProperty("away");
        Assert.InRange(away.GetProperty("seconds").GetInt64(), 13 * 60 - 2, 13 * 60 + 2);
    }
}
