using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Палій і готовий розпис (14.11, записки #81/#83): краса партії більше не тримає палія назавжди (у владіка 4 дні,
/// сушарня 46/46), а палій більше не стирає її. Розпис чекає ручного обпалу; поки гончар біля горна (KilnIdle від
/// дотику) — горно його навіть із повною сушарнею, далі палій пече свої партії без краси.
/// </summary>
public class ClickerStokerPaintTests
{
    static RoomHarness Wheel(string mode = "always")
    {
        var h = new RoomHarness("clicker", seed: 1);
        h.Solo("Оля");
        Patch(h, s => s["guild"] = new JsonObject { ["rank"] = 1, ["autoMode"] = mode });
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

    /// <summary>Розпис з красою на горні; <paramref name="touchAgo"/> — коли гончар востаннє чіпав горно.</summary>
    static void Painted(RoomHarness h, int beauty, TimeSpan touchAgo) => Patch(h, s =>
    {
        s["kiln"]!["tech"] = "rizh";
        s["kiln"]!["beauty"] = beauty;
        s["kiln"]!["paintAt"] = (h.Clock.UtcNow - touchAgo).ToString("O");
        s["kiln"]!["touch"] = (h.Clock.UtcNow - touchAgo).ToString("O");
    });

    static void Rack(RoomHarness h, int dry, int wet = 0) => Patch(h, s =>
    {
        var rack = new JsonArray();
        var now = h.Clock.UtcNow;
        for (var i = 0; i < dry; i++) rack.Add(new JsonObject { ["ware"] = "pot", ["clay"] = "", ["dryAt"] = now.AddMinutes(-1).ToString("O") });
        for (var i = 0; i < wet; i++) rack.Add(new JsonObject { ["ware"] = "pot", ["clay"] = "", ["dryAt"] = now.AddMinutes(1).ToString("O") });
        s["craft"]!["rack"] = rack;
    });

    static JsonElement Kiln(RoomHarness h) => h.View(0).GetProperty("kiln");
    static string State(RoomHarness h) => Kiln(h).GetProperty("state").GetString()!;
    static int Beauty(RoomHarness h) => Kiln(h).GetProperty("beauty").GetInt32();
    static int RackSize(RoomHarness h) => h.View(0).GetProperty("craft").GetProperty("rackSize").GetInt32();
    static ActResult K(RoomHarness h, object payload) => h.Act(0, "kiln", payload);
    static void BurnOut(RoomHarness h) => h.Clock.Advance(Clicker.KilnBurn + TimeSpan.FromSeconds(1));

    [Fact]
    public void An_old_painting_no_longer_stops_the_stoker_and_stays_for_the_hand()
    {
        // Як у владіка: розпис з красою 100 лежить чотири дні, сушарня повна.
        var h = Wheel();
        Painted(h, 100, TimeSpan.FromDays(4));
        Rack(h, dry: RackSize(h));
        Assert.Equal("burning", State(h));
        Assert.True(Kiln(h).GetProperty("helper").GetBoolean());
        BurnOut(h);
        var last = Kiln(h).GetProperty("last");
        Assert.True(last.GetProperty("helper").GetBoolean());
        Assert.Equal(0, last.GetProperty("beauty").GetInt32());                  // палій пік без краси
        Assert.Equal(100, Beauty(h));                                             // а розпис лишився рукам
        Assert.Equal("rizh", Kiln(h).GetProperty("tech").GetString());
    }

    [Fact]
    public void While_the_potter_is_at_the_kiln_the_painted_batch_is_his_even_with_a_full_rack()
    {
        var h = Wheel();
        Painted(h, 90, TimeSpan.Zero);
        Rack(h, dry: RackSize(h));
        h.Clock.Advance(Clicker.KilnFullGrace + TimeSpan.FromSeconds(5));
        Assert.Equal("cold", State(h));                                           // без розпису тут палій уже взявся б
        h.Clock.Advance(Clicker.KilnIdle - Clicker.KilnFullGrace);
        Assert.Equal("burning", State(h));
        Assert.Equal(90, Beauty(h));
    }

    [Fact]
    public void Fired_by_hand_the_painting_finally_goes_into_the_batch()
    {
        var h = Wheel("hands");
        Painted(h, 80, TimeSpan.FromHours(1));
        Rack(h, dry: 6);
        Assert.True(K(h, new { op = "light", helper = true }).Ok);              // підмайстер — розпис не чіпає
        BurnOut(h);
        Assert.Equal(80, Beauty(h));
        h.Clock.Advance(Clicker.KilnCool);
        Rack(h, dry: 6);
        Assert.True(K(h, new { op = "light" }).Ok);
        h.Clock.Advance(Clicker.KilnBurn + Clicker.KilnOwnWait + TimeSpan.FromSeconds(1));
        var open = K(h, new { op = "open", t = Array.Empty<int[]>(), own = true });
        Assert.True(open.Ok, open.Message);
        Assert.Equal(80, Kiln(h).GetProperty("last").GetProperty("beauty").GetInt32());
        Assert.Equal(0, Beauty(h));
    }

    [Fact]
    public void An_abandoned_hand_fired_kiln_burns_its_painting()
    {
        var h = Wheel("hands");
        Painted(h, 70, TimeSpan.FromHours(1));
        Rack(h, dry: 6);
        Assert.True(K(h, new { op = "light" }).Ok);
        h.Clock.Advance(Clicker.KilnBurn + Clicker.KilnAbandon + TimeSpan.FromSeconds(1));
        Assert.True(Kiln(h).GetProperty("last").GetProperty("helper").GetBoolean());
        Assert.Equal(0, Beauty(h));
    }

    [Fact]
    public void An_away_night_with_a_painting_on_the_kiln_still_fires()
    {
        // Як у Smaug: «коли мене нема», розписала й пішла.
        var h = Wheel("away");
        Patch(h, s => s["upgrades"]!["apprentice"] = 25);
        Painted(h, 100, TimeSpan.Zero);
        h.Clock.Advance(TimeSpan.FromHours(8));
        var k = Kiln(h);
        Assert.True(k.GetProperty("autoBatches").GetInt32() > 20, $"партій: {k.GetProperty("autoBatches").GetInt32()}");
        Assert.Equal(100, Beauty(h));
    }

    [Fact]
    public void Firing_by_hand_while_the_stoker_burns_claims_the_next_batch()
    {
        var h = Wheel();
        Painted(h, 100, TimeSpan.FromHours(1));
        Rack(h, dry: 6);
        Assert.Equal("burning", State(h));
        var claim = K(h, new { op = "light" });
        Assert.True(claim.Ok, claim.Message);
        Assert.Contains("Наступна — твоя", claim.Message);
        BurnOut(h);
        h.Clock.Advance(Clicker.KilnCool);
        Rack(h, dry: 6);
        Assert.Equal("cold", State(h));                                           // палій чекає гончаревих рук
        Assert.True(K(h, new { op = "light" }).Ok);                               // і руками — справді його
        Assert.False(Kiln(h).GetProperty("helper").GetBoolean());
    }

    [Fact]
    public void A_cooling_kiln_can_be_claimed_for_the_painting_too()
    {
        var h = Wheel();
        Painted(h, 100, TimeSpan.FromHours(1));
        Rack(h, dry: 6);
        Assert.Equal("burning", State(h));
        BurnOut(h);
        Assert.Equal("cooling", State(h));
        var claim = K(h, new { op = "light" });
        Assert.True(claim.Ok, claim.Message);
        Assert.Contains("наступну партію палиш ти", claim.Message);
        h.Clock.Advance(Clicker.KilnCool);
        Rack(h, dry: 6);
        Assert.Equal("cold", State(h));
        // Без розпису гаряче горно — як і було, відмова.
        var plain = Wheel();
        Rack(plain, dry: 6);
        Assert.Equal("burning", State(plain));
        BurnOut(plain);
        Assert.False(K(plain, new { op = "light" }).Ok);
    }

    [Fact]
    public void A_loaded_but_unlit_kiln_no_longer_stops_the_stoker()
    {
        var h = Wheel();
        Rack(h, dry: 3);
        Patch(h, s => s["kiln"]!["touch"] = h.Clock.UtcNow.ToString("O"));
        Assert.True(K(h, new { op = "load" }).Ok);
        Rack(h, dry: 4);
        h.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("loaded", State(h));                                         // гончар щойно склав — його
        h.Clock.Advance(Clicker.KilnIdle - TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(5));
        Assert.Equal("burning", State(h));
        Assert.True(Kiln(h).GetProperty("helper").GetBoolean());
        // І доклав сухі з сушарні до повного горна (свіжий гончар — шість місць).
        Assert.Equal(Kiln(h).GetProperty("slots").GetInt32(), Kiln(h).GetProperty("batch").GetArrayLength());
    }

    [Fact]
    public void Without_a_painting_the_stoker_behaves_as_before()
    {
        var h = Wheel();
        Rack(h, dry: 6);
        Assert.Equal("burning", State(h));
        // «Хай підмайстер палить» — не дотик-претензія: наступну палій бере сам, як і було.
        var claim = K(h, new { op = "light", helper = true });
        Assert.True(claim.Ok, claim.Message);
        Assert.DoesNotContain("Наступна", claim.Message);
    }
}
