using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Десяте оновлення, пакет B «Швидкість» (docs/games/specs/clicker-v10-b.md): хата й ремесло у виді нового клієнта
/// (pv ≥ 10) — лише стан, незмінні тексти (назви, описи, ціни) — у каталозі, що їде раз; вкладка зі старим clicker.js
/// і далі бачить повний вид, як до v10; вид топ-гончаря вкладається в 25 КБ.
/// </summary>
public class ClickerSpeedTests
{
    static RoomHarness Wheel(string nick = "Оля", int seed = 1)
    {
        var h = new RoomHarness("clicker", seed: seed);
        h.Solo(nick);
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static ActResult Act(RoomHarness h, string action, object? payload = null) => h.Act(0, action, payload);
    static JsonElement House(RoomHarness h) => View(h).GetProperty("house");
    static JsonElement Craft(RoomHarness h) => View(h).GetProperty("craft");

    /// <summary>Клієнт каже «я v10» — далі вид худий (так робить clicker.js у кожному кліку й запиті каталогу).</summary>
    static void Slim(RoomHarness h) => Assert.True(Act(h, "look", new { pv = Clicker.ProtocolVersion }).Ok);

    /// <summary>Каталог наново, як його просить клієнт v10: він живе у виді до наступної дії.</summary>
    static JsonElement Catalog(RoomHarness h)
    {
        Assert.True(Act(h, "look", new { catalog = true, pv = Clicker.ProtocolVersion }).Ok);
        var c = View(h).GetProperty("catalog");
        Assert.Equal(JsonValueKind.Object, c.ValueKind);
        return c;
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

    static JsonArray Arr(IEnumerable<string> items)
    {
        var a = new JsonArray();
        foreach (var x in items) a.Add(x);
        return a;
    }

    static List<string> Strings(JsonElement arr) => arr.EnumerateArray().Select(x => x.GetString()!).ToList();

    /// <summary>Вид так, як він іде на дріт: SignalR пише кирилицю як є, UTF-8, без \uXXXX.</summary>
    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static int Bytes(JsonNode? node) => Encoding.UTF8.GetByteCount(node?.ToJsonString(Wire) ?? "");

    // ---------- хата ----------

    [Fact]
    public void The_slim_house_carries_only_state_and_the_catalog_carries_the_texts()
    {
        var h = Wheel();
        Patch(h, s =>
        {
            var house = s["house"]!.AsObject();
            house["clays"] = Arr(["red"]);
            house["clay"] = "red";
            house["tools"] = Arr(["lantern", "paddle"]);
            house["decor"] = Arr(["towel"]);
            house["looks"] = Arr(["roof:tile"]);
            house["look"] = new JsonObject { ["roof"] = "tile" };
            house["wonders"] = new JsonObject { ["singer"] = "2026-09-01T10:00:00+00:00" };
            s["stamps"] = 10;
        });
        Slim(h);
        var hs = House(h);
        // Жодних назв, описів і цін — лише куплене ключами, у порядку каталогу.
        foreach (var gone in new[] { "name", "nameMax", "clays", "tools", "decor", "looks" })
            Assert.False(hs.TryGetProperty(gone, out _), gone);
        var own = hs.GetProperty("own");
        Assert.Equal(["red"], Strings(own.GetProperty("clays")));
        Assert.Equal(["paddle", "lantern"], Strings(own.GetProperty("tools")));
        Assert.Equal(["towel"], Strings(own.GetProperty("decor")));
        Assert.Equal(["roof:tile"], Strings(own.GetProperty("looks")));
        Assert.Equal("tile", hs.GetProperty("look").GetProperty("roof").GetString());
        Assert.Equal("red", hs.GetProperty("clay").GetString());
        Assert.Equal(["singer"], hs.GetProperty("wonders").EnumerateObject().Select(p => p.Name).ToList());
        var text = hs.GetRawText();
        Assert.DoesNotContain("Дерев'яна лопатка", text);
        Assert.DoesNotContain("Глек, що співає", text);

        // Каталог — усе незмінне: глина, знаряддя, прикраси, оздоба з цінами в клеймах, дивовижі з підказкою.
        var cat = Catalog(h).GetProperty("house");
        Assert.Equal(Clicker.Clays.Length, cat.GetProperty("clays").GetArrayLength());
        Assert.Equal(Clicker.Tools.Select(t => t.Key), cat.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("key").GetString()));
        foreach (var t in cat.GetProperty("tools").EnumerateArray())
        {
            var def = Clicker.Tools.Single(x => x.Key == t.GetProperty("key").GetString());
            Assert.Equal(def.Name, t.GetProperty("name").GetString());
            Assert.Equal(def.Desc, t.GetProperty("desc").GetString());
            Assert.Equal(def.Price, t.GetProperty("price").GetDouble());
        }
        Assert.Equal(Clicker.Decor.Length, cat.GetProperty("decor").GetArrayLength());
        Assert.Equal(Clicker.Decor[6].Bonus, cat.GetProperty("decor")[6].GetProperty("bonus").GetDouble());
        var roof = cat.GetProperty("looks").EnumerateArray().Single(l => l.GetProperty("key").GetString() == "roof");
        Assert.Equal("Стріха", roof.GetProperty("name").GetString());
        Assert.Equal(40, roof.GetProperty("options").EnumerateArray().Single(o => o.GetProperty("value").GetString() == "tile").GetProperty("price").GetInt32());
        Assert.Equal(Clicker.HouseNameMax, cat.GetProperty("nameMax").GetInt32());
        Assert.Equal(Clicker.WonderBonus, cat.GetProperty("wonderBonus").GetDouble());
    }

    [Fact]
    public void A_wonder_keeps_its_tale_secret_until_it_is_found()
    {
        var h = Wheel();
        Slim(h);
        var list = Catalog(h).GetProperty("house").GetProperty("wonders");
        Assert.Equal(Clicker.Wonders.Length, list.GetArrayLength());
        foreach (var w in list.EnumerateArray())
        {
            Assert.False(w.TryGetProperty("name", out _));
            Assert.False(w.TryGetProperty("tale", out _));
            Assert.False(string.IsNullOrEmpty(w.GetProperty("from").GetString()));
        }
        Patch(h, s => s["house"]!["wonders"] = new JsonObject { ["moon"] = "2026-09-20T21:00:00+00:00" });
        Slim(h);
        Assert.Equal(["moon"], House(h).GetProperty("wonders").EnumerateObject().Select(p => p.Name).ToList());
        var moon = Catalog(h).GetProperty("house").GetProperty("wonders").EnumerateArray().Single(w => w.GetProperty("key").GetString() == "moon");
        var def = Clicker.Wonders.Single(w => w.Key == "moon");
        Assert.Equal(def.Name, moon.GetProperty("name").GetString());
        Assert.Equal(def.Tale, moon.GetProperty("tale").GetString());
    }

    [Fact]
    public void The_slim_merchant_board_names_the_style_by_key_only()
    {
        var h = Wheel();
        Patch(h, s => s["styles"] = Arr(["kosiv"]));
        Slim(h);
        var board = House(h).GetProperty("orders").GetProperty("board");
        Assert.True(board.GetArrayLength() > 0);
        foreach (var o in board.EnumerateArray()) Assert.False(o.TryGetProperty("styleName", out _));
    }

    [Fact]
    public void An_old_tab_still_gets_the_full_house_and_craft_as_before_v10()
    {
        // Вкладка зі старим clicker.js (радіо відкрите добами) про pv не знає — вид повний, як до v10.
        var h = Wheel();
        Patch(h, s => s["house"]!["tools"] = Arr(["paddle"]));
        Assert.True(Act(h, "spin", PotterHands.Human(1)).Ok);
        var hs = House(h);
        Assert.Equal("Хата гончаря", hs.GetProperty("name").GetString());
        var paddle = hs.GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("key").GetString() == "paddle");
        Assert.Equal("Дерев'яна лопатка", paddle.GetProperty("name").GetString());
        Assert.True(paddle.GetProperty("owned").GetBoolean());
        Assert.Equal(Clicker.Wonders.Length, hs.GetProperty("wonders").GetProperty("list").GetArrayLength());
        Assert.Equal(Clicker.Looks.Length, hs.GetProperty("looks").GetArrayLength());
        var c = Craft(h);
        Assert.Equal("Горщик", c.GetProperty("wares")[0].GetProperty("name").GetString());
        Assert.True(c.GetProperty("wares")[2].TryGetProperty("unlock", out _));
        Assert.Equal(Clicker.CraftUps[0].Name, c.GetProperty("ups")[0].GetProperty("name").GetString());
        Assert.Equal(Clicker.CraftUps[0].Desc, c.GetProperty("ups")[0].GetProperty("desc").GetString());

        // Щойно клієнт сказав «я v10» — худне; той самий гончар відкрив стару вкладку — знову повний.
        Slim(h);
        Assert.False(House(h).TryGetProperty("tools", out _));
        h.Clock.Advance(1);
        Assert.True(Act(h, "spin", PotterHands.Human(1)).Ok);
        Assert.True(House(h).TryGetProperty("tools", out _));
    }

    // ---------- ремесло ----------

    [Fact]
    public void The_slim_craft_carries_levels_and_prices_and_the_catalog_carries_the_names()
    {
        var h = Wheel();
        Patch(h, s => s["craft"]!["ups"] = new JsonObject { ["rack"] = 3, ["store"] = 10 });
        Slim(h);
        var c = Craft(h);
        var pot = c.GetProperty("wares")[0];
        Assert.Equal("pot", pot.GetProperty("key").GetString());
        Assert.True(pot.GetProperty("open").GetBoolean());
        Assert.True(pot.GetProperty("need").GetInt32() > 0);
        Assert.False(pot.TryGetProperty("name", out _));
        Assert.False(pot.TryGetProperty("unlock", out _));
        var rack = c.GetProperty("ups").EnumerateArray().Single(u => u.GetProperty("key").GetString() == "rack");
        Assert.Equal(3, rack.GetProperty("level").GetInt32());
        Assert.Equal(Clicker.CraftUps.Single(u => u.Key == "rack").Max, rack.GetProperty("max").GetInt32());
        Assert.Equal(Clicker.CraftUpPrice(Clicker.CraftUps.Single(u => u.Key == "rack"), 3), rack.GetProperty("price").GetDouble());
        Assert.StartsWith("сушарня на", rack.GetProperty("now").GetString());
        Assert.False(rack.TryGetProperty("name", out _));
        Assert.False(rack.TryGetProperty("desc", out _));
        var store = c.GetProperty("ups").EnumerateArray().Single(u => u.GetProperty("key").GetString() == "store");
        Assert.Equal(0, store.GetProperty("price").GetDouble());          // стеля — купувати нічого

        var cat = Catalog(h);
        Assert.Equal(Clicker.Wares.Select(w => w.Name), cat.GetProperty("wares").EnumerateArray().Select(w => w.GetProperty("name").GetString()));
        Assert.Equal(Clicker.Wares.Select(w => w.Unlock), cat.GetProperty("wares").EnumerateArray().Select(w => w.GetProperty("unlock").GetDouble()));
        var ups = cat.GetProperty("craftUps").EnumerateArray().ToList();
        Assert.Equal(Clicker.CraftUps.Select(u => u.Key), ups.Select(u => u.GetProperty("key").GetString()));
        Assert.Equal(Clicker.CraftUps.Select(u => u.Name), ups.Select(u => u.GetProperty("name").GetString()));
        Assert.Equal(Clicker.CraftUps.Select(u => u.Desc), ups.Select(u => u.GetProperty("desc").GetString()));
    }

    // ---------- розмір ----------

    /// <summary>
    /// Топ-гончар пізньої гри: уся драбина на 200-х рівнях, половина віх, усі секрети й розписи, повна хата (глина,
    /// знаряддя, прикраси, оздоба, шістнадцять дивовиж), повна сушарня й комора на два десятки різних виробів.
    /// </summary>
    static RoomHarness TopPotter()
    {
        var h = Wheel("Владік");
        var now = h.Clock.UtcNow;
        Patch(h, s =>
        {
            s["pots"] = 2.7e21;
            s["total"] = 6e21;
            s["stamps"] = 2_000_000;
            var ups = new JsonObject();
            foreach (var u in Clicker.Shop) ups[u.Key] = u.MaxLevel > 0 ? Math.Min(200, u.MaxLevel) : 200;
            s["upgrades"] = ups;
            s["marks"] = Arr(Clicker.Shop.SelectMany(u => u.Steps.Where(m => m.Level <= 100).Select(m => u.Key + ":" + m.Level)));
            s["secrets"] = Arr(Clicker.Secrets.Select(x => x.Key));
            s["styles"] = Arr(Clicker.Styles.Select(x => x.Key));
            var house = s["house"]!.AsObject();
            house["clays"] = Arr(Clicker.Clays.Where(c => c.Key.Length > 0).Select(c => c.Key));
            house["clay"] = "black";
            house["tools"] = Arr(Clicker.Tools.Select(t => t.Key));
            house["decor"] = Arr(Clicker.Decor.Select(d => d.Key));
            house["looks"] = Arr(Clicker.Looks.SelectMany(l => l.Options.Where(o => o.Price > 0).Select(o => l.Key + ":" + o.Value)));
            var look = new JsonObject();
            foreach (var l in Clicker.Looks) look[l.Key] = l.Options[^1].Value;
            house["look"] = look;
            var wonders = new JsonObject();
            foreach (var w in Clicker.Wonders) wonders[w.Key] = now.AddDays(-3).ToString("O");
            house["wonders"] = wonders;
            house["name"] = "Хата над ставом";
            var craft = s["craft"]!.AsObject();
            var rack = new JsonArray();
            for (var i = 0; i < 20; i++) rack.Add(new JsonObject { ["ware"] = Clicker.Wares[i % 5].Key, ["clay"] = "black", ["dryAt"] = now.AddSeconds(i * 5).ToString("O") });
            craft["rack"] = rack;
            var items = new JsonObject();
            foreach (var w in Clicker.Wares.Take(6))
                foreach (var st in Clicker.Styles.Take(4))
                    items[w.Key + "|" + st.Key + "|" + (1 + items.Count % 4)] = 3;
            craft["items"] = items;
            var fired = new JsonObject();
            foreach (var w in Clicker.Wares) fired[w.Key] = 1_000;
            craft["firedBy"] = fired;
            var cups = new JsonObject();
            foreach (var u in Clicker.CraftUps) cups[u.Key] = u.Max;
            craft["ups"] = cups;
        });
        return h;
    }

    [Fact]
    public void The_view_of_a_top_potter_fits_in_25_kb_and_the_catalog_has_every_text()
    {
        var h = TopPotter();
        Slim(h);
        var view = JsonNode.Parse(View(h).GetRawText())!.AsObject();
        Assert.Null(view["catalog"]);
        Assert.Null(view["shopCatalog"]);
        var bytes = Bytes(view);
        Assert.True(bytes < 25_000, $"вид топ-гончаря важить {bytes} Б, а мусить < 25 000");
        // Хата й ремесло — лише стан: кілобайт-два, а не двадцять чотири, як до v10.
        Assert.True(Bytes(view["house"]) < 3_000, $"хата — {Bytes(view["house"])} Б");
        Assert.True(Bytes(view["craft"]) < 6_000, $"ремесло — {Bytes(view["craft"])} Б");

        // Каталог (раз після відкриття) — з усіма текстами, яких у виді більше нема.
        Assert.True(Act(h, "look", new { catalog = true, pv = Clicker.ProtocolVersion }).Ok);
        var full = View(h).GetRawText();
        var catalog = View(h).GetProperty("catalog").GetRawText();
        foreach (var t in Clicker.Tools) { Assert.Contains(Json(t.Name), catalog); Assert.Contains(Json(t.Desc), catalog); }
        foreach (var d in Clicker.Decor) Assert.Contains(Json(d.Name), catalog);
        foreach (var c in Clicker.Clays) Assert.Contains(Json(c.Desc), catalog);
        foreach (var l in Clicker.Looks) foreach (var o in l.Options) Assert.Contains(Json(o.Name), catalog);
        foreach (var w in Clicker.Wonders) { Assert.Contains(Json(w.Name), catalog); Assert.Contains(Json(w.Tale), catalog); }
        foreach (var w in Clicker.Wares) Assert.Contains(Json(w.Name), catalog);
        foreach (var u in Clicker.CraftUps) { Assert.Contains(Json(u.Name), catalog); Assert.Contains(Json(u.Desc), catalog); }
        Assert.Contains("\"shopCatalog\":{", full);
    }

    /// <summary>Рядок так, як його пише System.Text.Json у GetRawText (кирилиця — \uXXXX, апостроф теж екранований).</summary>
    static string Json(string s) => JsonSerializer.Serialize(s)[1..^1];
}
