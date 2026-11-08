using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Палій «коли мене нема» (v13 Б): три режими — завжди / коли мене нема / лише руками. У режимі «нема» палій мовчить, поки
/// гончар біля кола (руки менш як п'ять хвилин тому), навіть коли сушарня повна; палить, коли руки старі чи натиснуто
/// «Закрити»; офлайн-ніч палить сама. Збереження зі знятою галочкою (autoOff) переїздить у «коли мене нема».
/// </summary>
public class ClickerStokerAwayTests
{
    static RoomHarness Wheel(string? mode, bool full = true)
    {
        var h = new RoomHarness("clicker", seed: 1);
        h.Solo("Оля");
        Patch(h, s =>
        {
            var g = new JsonObject { ["rank"] = 1 };
            if (mode is not null) g["autoMode"] = mode;
            s["guild"] = g;
            if (!full) return;
            var rack = new JsonArray();
            var now = h.Clock.UtcNow;
            for (var i = 0; i < Clicker.RackBase; i++)
                rack.Add(new JsonObject { ["ware"] = "pot", ["clay"] = "", ["dryAt"] = now.AddMinutes(-1).ToString("O") });
            s["craft"]!["rack"] = rack;
        });
        return h;
    }

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static JsonElement Kiln(RoomHarness h) => h.View(0).GetProperty("kiln");

    /// <summary>Палій брався за горно: партія палає зараз або вже обпалена (autoBatches рахує лише закінчені).</summary>
    static bool Fired(RoomHarness h) => Kiln(h).GetProperty("state").GetString() != "cold" || Kiln(h).GetProperty("batches").GetInt32() > 0;
    static string Mode(RoomHarness h) => Kiln(h).GetProperty("autoMode").GetString()!;

    /// <summary>Справжня дія гончаря (оновлює «руки»): «погладити кота» може й відмовити, але руки вже позначено.</summary>
    static void Hands(RoomHarness h) => h.Act(0, "pet");

    [Fact]
    public void Away_stays_silent_while_the_potter_is_here_even_with_a_full_rack()
    {
        var h = Wheel("away");
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        Hands(h);
        h.Clock.Advance(TimeSpan.FromMinutes(4));
        Assert.False(Fired(h));
        Assert.Equal(Clicker.RackBase, h.View(0).GetProperty("craft").GetProperty("rack").GetArrayLength());
    }

    [Fact]
    public void Away_fires_five_minutes_after_the_last_touch()
    {
        var h = Wheel("away");
        Hands(h);
        h.Clock.Advance(TimeSpan.FromMinutes(4));
        Assert.False(Fired(h));
        h.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(Fired(h));
    }

    [Fact]
    public void Away_fires_after_close_even_with_fresh_hands()
    {
        var h = Wheel("away");
        Hands(h);
        h.Clock.Advance(TimeSpan.FromSeconds(40));
        Assert.False(Fired(h));
        Assert.True(h.Act(0, "sight", new { on = false }).Ok);      // «Закрити»: коло зникло з очей
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(Fired(h));
    }

    [Fact]
    public void Always_fires_with_the_potter_here_and_hands_never_does()
    {
        var always = Wheel("always");
        Hands(always);
        always.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(Fired(always));
        var hands = Wheel("hands");
        Hands(hands);
        hands.Clock.Advance(TimeSpan.FromHours(2));
        Assert.False(Fired(hands));
    }

    [Fact]
    public void A_save_with_the_switch_off_becomes_away_with_one_toast_and_the_rest_always()
    {
        var old = Wheel(null, full: false);
        Patch(old, s => s["guild"] = new JsonObject { ["rank"] = 1, ["autoOff"] = true });
        Assert.Equal("away", Mode(old));
        Assert.Contains("Палій тепер палить, коли тебе нема", old.Act(0, "look").Message);
        Assert.DoesNotContain("Палій тепер", old.Act(0, "look").Message);                 // раз
        Assert.Equal("away", JsonNode.Parse(old.Room.Game.Save()!)!["guild"]!["autoMode"]!.GetValue<string>());

        var fresh = Wheel(null, full: false);
        Patch(fresh, s => s["guild"] = new JsonObject { ["rank"] = 1 });
        Assert.Equal("always", Mode(fresh));
        Assert.DoesNotContain("Палій тепер", fresh.Act(0, "look").Message);

        // Нове поле важить більше за старе: autoOff лишається для відкату, але режим береться з autoMode.
        var both = Wheel(null, full: false);
        Patch(both, s => s["guild"] = new JsonObject { ["rank"] = 1, ["autoOff"] = true, ["autoMode"] = "always" });
        Assert.Equal("always", Mode(both));
        Assert.DoesNotContain("Палій тепер", both.Act(0, "look").Message);
    }

    [Fact]
    public void The_old_on_flag_still_works_and_mode_is_validated()
    {
        var h = Wheel("away", full: false);
        Assert.True(h.Act(0, "guild", new { op = "auto", on = false }).Ok);
        Assert.Equal("hands", Mode(h));
        Assert.False(Kiln(h).GetProperty("auto").GetBoolean());
        Assert.True(h.Act(0, "guild", new { op = "auto", on = true }).Ok);
        Assert.Equal("always", Mode(h));
        Assert.True(Kiln(h).GetProperty("auto").GetBoolean());
        Assert.True(h.Act(0, "guild", new { op = "auto", mode = "away" }).Ok);
        Assert.Equal("away", Mode(h));
        Assert.True(Kiln(h).GetProperty("auto").GetBoolean());
        Assert.False(h.Act(0, "guild", new { op = "auto", mode = "sometimes" }).Ok);
        Assert.Equal("away", Mode(h));
        // Режим переживає збереження.
        Patch(h, _ => { });
        Assert.Equal("away", Mode(h));
    }

    [Fact]
    public void An_offline_night_in_away_mode_fires_many_batches()
    {
        var h = Wheel("away", full: false);
        Patch(h, s => s["upgrades"]!["apprentice"] = 25);
        h.Clock.Advance(TimeSpan.FromHours(8));
        var k = Kiln(h);
        Assert.True(k.GetProperty("autoBatches").GetInt32() > 50, $"партій: {k.GetProperty("autoBatches").GetInt32()}");
        Assert.True(h.View(0).GetProperty("craft").GetProperty("fired").GetInt64() > 100);
    }

    [Fact]
    public void Glek_notices_a_sleeping_stoker_only_in_hands_mode_after_half_an_hour()
    {
        var h = Wheel("hands");
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        Hands(h);
        Assert.DoesNotContain(Advise(h).Tips, t => t.Kind == "stoker-asleep");
        h.Clock.Advance(TimeSpan.FromMinutes(31));
        Hands(h);
        var tip = Assert.Single(Advise(h).Tips, t => t.Kind == "stoker-asleep");
        Assert.Equal("kiln", tip.Where);
        Assert.True(ClickerGlekBook.Allowed.ContainsKey("stoker-asleep"));

        // Режим «коли мене нема» не потребує поради: палій сам візьме, коли гончар піде.
        Assert.True(h.Act(0, "guild", new { op = "auto", mode = "away" }).Ok);
        Assert.DoesNotContain(Advise(h).Tips, t => t.Kind == "stoker-asleep");
    }

    static ClickerAdvice Advise(RoomHarness h)
    {
        lock (h.Room.Sync) return ((Clicker)h.Room.Game).Advise();
    }
}
