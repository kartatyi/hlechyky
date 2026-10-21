using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Голуба глина, каолін і рівні глини (21.10, ClickerHouse.cs): голуба ліпиться швидше поверх підлоги майстерності,
/// каолін блищить у горні й дає тонкі сирці від підмайстрів, а кожна куплена глина росте від виліпленого з неї.
/// </summary>
public class ClickerClayTests
{
    static RoomHarness Wheel(int seed = 1)
    {
        var h = new RoomHarness("clicker", seed: seed);
        h.Solo("Оля");
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static JsonElement Craft(RoomHarness h) => View(h).GetProperty("craft");
    static JsonElement Kiln(RoomHarness h) => View(h).GetProperty("kiln");
    static double PerSecond(RoomHarness h) => View(h).GetProperty("perSecond").GetDouble();
    static ActResult Act(RoomHarness h, string action, object? payload = null) => h.Act(0, action, payload);

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static JsonNode SaveNode(RoomHarness h)
    {
        lock (h.Room.Sync) return JsonNode.Parse(h.Room.Game.Save()!)!;
    }

    static void Click(RoomHarness h, int times)
    {
        while (times > 0)
        {
            var n = Math.Min(Clicker.MaxClicksPerSecond, times);
            Assert.True(Act(h, "spin", PotterHands.Human(n)).Ok);
            times -= n;
            h.Clock.Advance(1);
        }
    }

    /// <summary>Купити глину й одразу поставити її на коло.</summary>
    static void OnWheel(RoomHarness h, string clay)
    {
        Patch(h, s => { s["pots"] = 1e9; s["total"] = 1e9; });
        Assert.True(Act(h, "knead", new { kind = clay }).Ok);
    }

    static void Formed(RoomHarness h, string clay, long n) => Patch(h, s => s["house"]!["clayFormed"] = new JsonObject { [clay] = n });

    static ClickerClay Clay(string key) => Clicker.Clays.Single(c => c.Key == key);

    // ---------- рівні: числа ----------

    [Fact]
    public void Levels_come_from_wares_formed()
    {
        Assert.Equal(0, Clicker.ClayLevel(0));
        Assert.Equal(0, Clicker.ClayLevel(149));
        Assert.Equal(1, Clicker.ClayLevel(150));
        Assert.Equal(4, Clicker.ClayLevel(9_999));
        Assert.Equal(5, Clicker.ClayLevel(10_000));
        Assert.Equal(5, Clicker.ClayLevel(long.MaxValue));
        Assert.Equal(Clicker.ClayLevelAt.Length + 1, Clicker.ClayLevelNames.Length);
    }

    [Fact]
    public void A_level_makes_the_plus_stronger_and_the_minus_softer()
    {
        var red = Clicker.ClayLevelled(Clay("red"), 5);
        Assert.Equal(2.5, red.Click, 9);
        Assert.Equal(0.85, red.Passive, 9);

        var white = Clicker.ClayLevelled(Clay("white"), 1);
        Assert.Equal(1.55, white.Passive, 9);
        Assert.Equal(0.55, white.Click, 9);

        var blue = Clicker.ClayLevelled(Clay("blue"), 5);
        Assert.Equal(0.475, blue.Work, 9);
        Assert.Equal(0.925, blue.Passive, 9);

        var kaolin = Clicker.ClayLevelled(Clay("kaolin"), 5);
        Assert.Equal(1.125, kaolin.Work, 9);
        Assert.Equal(0.3, kaolin.Shine, 9);
        Assert.Equal(0.045, kaolin.Thin, 9);

        // Чорна: здобич росте як усі плюси, а частота подій — лише на 5 % за рівень (рішення 21.10).
        var black = Clicker.ClayLevelled(Clay("black"), 5);
        Assert.Equal(1.75, black.Loot, 9);
        Assert.Equal(0.5, black.Events, 9);

        // Звичайна й нульовий рівень — без змін.
        Assert.Same(Clicker.Clays[0], Clicker.ClayLevelled(Clicker.Clays[0], 5));
        Assert.Same(Clay("red"), Clicker.ClayLevelled(Clay("red"), 0));
    }

    [Fact]
    public void Clay_texts_stay_what_they_were_and_follow_the_level()
    {
        Assert.Equal("Клік ×2, пасив ×0,7 — для того, хто клацає", Clicker.ClayText(Clay("red")));
        Assert.Equal("Пасив ×1,5, клік ×0,5 — для того, хто чекає", Clicker.ClayText(Clay("white")));
        Assert.Equal("Розписні й падаючі глеки на 40 % частіші, глек з полиці й купець ×1,5 — для ловця", Clicker.ClayText(Clay("black")));
        Assert.Equal("Робота на виріб −35 %, пасив ×0,85 — ліпиться, як масло: для того, хто ліпить", Clicker.ClayText(Clay("blue")));
        Assert.Equal("У горні блиск +20 %, 3 % виробів підмайстра — тонкі, з горна розкішні, робота на виріб +25 % — глухівська біла: для того, хто чекає розкішних",
            Clicker.ClayText(Clay("kaolin")));
        Assert.Equal("Клік ×2,5, пасив ×0,85 — для того, хто клацає", Clicker.ClayText(Clicker.ClayLevelled(Clay("red"), 5)));
        Assert.Equal("Як є: без сильних і слабких сторін", Clicker.ClayText(Clicker.Clays[0]));
    }

    // ---------- голуба ----------

    [Fact]
    public void Blue_clay_shortens_the_work_but_not_the_price_floor()
    {
        var h = Wheel();
        var valueBefore = Craft(h).GetProperty("wares")[0].GetProperty("value").GetDouble();
        Assert.Equal(40, Craft(h).GetProperty("need").GetInt32());
        OnWheel(h, "blue");
        Assert.Equal(26, Craft(h).GetProperty("need").GetInt32());             // ⌈40 × 0,65⌉
        Click(h, 26);
        Assert.Equal(1, Craft(h).GetProperty("rack").GetArrayLength());
        Assert.Equal("blue", Craft(h).GetProperty("rack")[0].GetProperty("clay").GetString());
        // Дно ціни виробу рахує роботу без глини: горщик у коморі не дешевшає від того, що на колі голуба.
        Assert.Equal(valueBefore, Craft(h).GetProperty("wares")[0].GetProperty("value").GetDouble());
    }

    [Fact]
    public void Blue_clay_works_above_the_mastery_floor()
    {
        var h = Wheel();
        // Майстерність 10 горщика: −60 % — рівно підлога 40 %.
        Patch(h, s => s["craft"]!["firedBy"] = new JsonObject { ["pot"] = 10_000 });
        Assert.Equal(16, Craft(h).GetProperty("need").GetInt32());
        OnWheel(h, "blue");
        Assert.Equal(11, Craft(h).GetProperty("need").GetInt32());             // ⌈40 × 0,4 × 0,65⌉ = ⌈10,4⌉
    }

    [Fact]
    public void Blue_clay_costs_a_bit_of_passive()
    {
        var h = Wheel();
        Patch(h, s => s["upgrades"]!["apprentice"] = 10);                  // 5 за секунду
        OnWheel(h, "blue");
        Assert.Equal(4.25, PerSecond(h), 6);
    }

    // ---------- рівні в грі ----------

    [Fact]
    public void Wares_formed_count_for_the_clay_on_the_wheel_and_raise_its_level()
    {
        var h = Wheel();
        Patch(h, s => s["upgrades"]!["apprentice"] = 10);
        OnWheel(h, "white");
        Assert.Equal(7.5, PerSecond(h), 6);
        Formed(h, "white", 149);
        Click(h, 40);                                                      // біла клік не чіпає: 40 роботи — горщик
        Assert.Equal(150, SaveNode(h)["house"]!["clayFormed"]!["white"]!.GetValue<long>());
        Assert.Equal(7.75, PerSecond(h), 6);                               // пасив ×1,55
        var white = House(h).GetProperty("clays").EnumerateArray().Single(c => c.GetProperty("key").GetString() == "white");
        Assert.StartsWith("Пасив ×1,55", white.GetProperty("desc").GetString());
    }

    static JsonElement House(RoomHarness h) => View(h).GetProperty("house");

    [Fact]
    public void Ordinary_clay_has_no_levels_and_a_firing_keeps_them()
    {
        var h = Wheel();
        Click(h, 40);
        Assert.Null(SaveNode(h)["house"]!["clayFormed"]);

        OnWheel(h, "red");
        Formed(h, "red", 600);
        var json = SaveNode(h).ToJsonString();
        var h2 = Wheel();
        lock (h2.Room.Sync) h2.Room.Game.Load(json);
        Assert.Equal(600, SaveNode(h2)["house"]!["clayFormed"]!["red"]!.GetValue<long>());
    }

    [Fact]
    public void Unknown_clays_in_the_level_counter_are_dropped_on_load()
    {
        var h = Wheel();
        Patch(h, s => s["house"]!["clayFormed"] = new JsonObject { ["golden"] = 50, ["red"] = 7, [""] = 9 });
        var saved = SaveNode(h)["house"]!["clayFormed"]!.AsObject();
        Assert.Single(saved);
        Assert.Equal(7, saved["red"]!.GetValue<long>());
    }

    // ---------- каолін і тонкі сирці ----------

    static void Rack(RoomHarness h, params (string Clay, bool Thin)[] rows) => Patch(h, s =>
    {
        var rack = new JsonArray();
        foreach (var (clay, thin) in rows)
            rack.Add(new JsonObject { ["ware"] = "pot", ["clay"] = clay, ["dryAt"] = h.Clock.UtcNow.AddMinutes(-1).ToString("O"), ["thin"] = thin });
        s["craft"]!["rack"] = rack;
    });

    [Fact]
    public void A_thin_kaolin_ware_comes_out_of_the_kiln_luxurious_without_a_paint()
    {
        var h = Wheel();
        Rack(h, ("kaolin", true), ("kaolin", true), ("", false));
        Assert.True(Act(h, "kiln", new { op = "load" }).Ok);
        Assert.Equal(2, Kiln(h).GetProperty("thin").GetInt32());
        Assert.True(Act(h, "kiln", new { op = "light", helper = true }).Ok);
        h.Clock.Advance(31);
        var q = Kiln(h).GetProperty("last").GetProperty("items").EnumerateArray().Select(i => i[1].GetInt32()).ToList();
        Assert.Equal(2, q.Count(x => x == Clicker.QualityMax));
        Assert.Contains(Craft(h).GetProperty("items").EnumerateArray(), i => i.GetProperty("q").GetInt32() == Clicker.QualityMax);
    }

    [Fact]
    public void Only_kaolin_can_be_thin_and_the_kiln_keeps_the_clay_across_a_reload()
    {
        var h = Wheel();
        Rack(h, ("red", true), ("kaolin", true));
        var rack = Craft(h).GetProperty("rack");
        Assert.False(rack[0].TryGetProperty("thin", out _));
        Assert.True(rack[1].GetProperty("thin").GetBoolean());

        Assert.True(Act(h, "kiln", new { op = "load" }).Ok);
        var json = SaveNode(h);
        Assert.Equal("[\"red\",\"kaolin*\"]", json["kiln"]!["clays"]!.ToJsonString());
        var h2 = Wheel();
        lock (h2.Room.Sync) h2.Room.Game.Load(json.ToJsonString());
        Assert.Equal(1, Kiln(h2).GetProperty("thin").GetInt32());
    }

    [Fact]
    public void A_kiln_saved_before_clays_reads_as_plain_clay()
    {
        var h = Wheel();
        Rack(h, ("kaolin", true));
        Assert.True(Act(h, "kiln", new { op = "load" }).Ok);
        Patch(h, s => s["kiln"]!.AsObject().Remove("clays"));
        Assert.Equal(0, Kiln(h).GetProperty("thin").GetInt32());
        Assert.Equal(1, Kiln(h).GetProperty("batch").GetArrayLength());
    }

    [Fact]
    public void Apprentices_on_kaolin_now_and_then_form_a_thin_ware_and_hands_never_do()
    {
        var h = Wheel();
        OnWheel(h, "kaolin");
        Assert.Equal(50, Craft(h).GetProperty("need").GetInt32());              // 40 × 1,25
        Click(h, 50 * Clicker.RackBase);
        Assert.All(Craft(h).GetProperty("rack").EnumerateArray(), r => Assert.False(r.TryGetProperty("thin", out _)));

        // Підмайстри ліплять сотні виробів: 3 % з них тонкі. Сушарня — велика, щоб не ставала.
        var thin = 0;
        var total = 0;
        Patch(h, s =>
        {
            s["upgrades"]!["apprentice"] = 25;
            s["craft"]!["rack"] = new JsonArray();
        });
        for (var i = 0; i < 400; i++)
        {
            h.Clock.Advance(100);                                          // 50 роботи — один виріб
            var r = Craft(h).GetProperty("rack");
            total += r.GetArrayLength();
            thin += r.EnumerateArray().Count(x => x.TryGetProperty("thin", out _));
            Patch(h, s => s["craft"]!["rack"] = new JsonArray());
        }
        Assert.InRange(total, 380, 420);
        Assert.InRange(thin, 2, 30);
    }
}
