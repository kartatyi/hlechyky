using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Ремесло (сьоме оновлення, ClickerCraft.cs): кліки й підмайстри ліплять виріб, він сохне на сушарні, комора й
/// базар, «поки тебе не було», каталоги у виді, збереження й обпал. Горно, альбом, ярмарок і цех — у своїх тестах.
/// </summary>
public class ClickerCraftTests
{
    static RoomHarness Wheel(string nick = "Оля")
    {
        var h = new RoomHarness("clicker");
        h.Solo(nick);
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static JsonElement Craft(RoomHarness h) => View(h).GetProperty("craft");
    static long Pots(RoomHarness h) => View(h).GetProperty("pots").GetInt64();
    static int RackCount(RoomHarness h) => Craft(h).GetProperty("rack").GetArrayLength();

    static ActResult Act(RoomHarness h, string action, object? payload = null) => h.Act(0, action, payload);

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

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static void Items(RoomHarness h, params (string Key, int N)[] items) => Patch(h, s =>
    {
        var craft = s["craft"]!.AsObject();
        var bag = new JsonObject();
        foreach (var (key, n) in items) bag[key] = n;
        craft["items"] = bag;
    });

    [Fact]
    public void Forty_clicks_make_a_pot_on_the_rack_and_not_a_single_extra_pot()
    {
        var h = Wheel();
        Click(h, 39);
        Assert.Equal(0, RackCount(h));
        Assert.Equal(39, Craft(h).GetProperty("work").GetDouble());
        Click(h, 1);

        var c = Craft(h);
        Assert.Equal(1, c.GetProperty("rack").GetArrayLength());
        Assert.Equal("pot", c.GetProperty("rack")[0].GetProperty("ware").GetString());
        Assert.Equal(0, c.GetProperty("work").GetDouble());
        Assert.Equal(1, c.GetProperty("formed").GetInt64());
        // Ремесло глеків не додає: сорок кліків — сорок глеків, як і було.
        Assert.Equal(40, Pots(h));
    }

    [Fact]
    public void The_first_ware_is_an_achievement()
    {
        var h = Wheel();
        Click(h, 40);
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-ware-1");
    }

    [Fact]
    public void A_full_rack_stops_the_wheel_at_the_last_bit_of_work()
    {
        var h = Wheel();
        Click(h, 40 * Clicker.RackBase + 45);
        var c = Craft(h);
        Assert.Equal(Clicker.RackBase, c.GetProperty("rack").GetArrayLength());
        Assert.True(c.GetProperty("rackFull").GetBoolean());
        Assert.Equal(40, c.GetProperty("work").GetDouble());
    }

    [Fact]
    public void Wares_dry_on_the_rack()
    {
        var h = Wheel();
        Click(h, 40);
        var dryAt = Craft(h).GetProperty("rack")[0].GetProperty("dryAt").GetDateTimeOffset();
        Assert.True(dryAt > h.Clock.UtcNow);
        Assert.True(dryAt <= h.Clock.UtcNow + Clicker.DryTime);
    }

    [Fact]
    public void Apprentices_form_on_their_own_but_slowly_and_only_into_free_rack_space()
    {
        var h = Wheel();
        Patch(h, s => s["upgrades"]!["apprentice"] = 25);           // 0,5 роботи за секунду — стеля
        Assert.Equal(Clicker.ApprenticeWorkMax, Craft(h).GetProperty("apprentice").GetDouble());
        h.Clock.Advance(79);
        Assert.Equal(0, RackCount(h));
        h.Clock.Advance(2);
        Assert.Equal(1, RackCount(h));

        h.Clock.Advance(TimeSpan.FromHours(8));
        var c = Craft(h);
        Assert.Equal(Clicker.RackBase, c.GetProperty("rack").GetArrayLength());
        // За довгий простій виліплене встигло висохнути.
        Assert.All(c.GetProperty("rack").EnumerateArray().Skip(1), r => Assert.True(r.GetProperty("dryAt").GetDateTimeOffset() <= h.Clock.UtcNow));
    }

    [Fact]
    public void A_bigger_workshop_means_a_bigger_rack()
    {
        var h = Wheel();
        Patch(h, s => s["upgrades"]!["workshop"] = 25);
        Assert.Equal(Clicker.RackBase + 5, Craft(h).GetProperty("rackSize").GetInt32());
        Patch(h, s => s["upgrades"]!["workshop"] = 500);
        Assert.Equal(Clicker.RackMax, Craft(h).GetProperty("rackSize").GetInt32());
    }

    [Fact]
    public void A_locked_ware_is_refused_and_an_open_one_goes_on_the_wheel()
    {
        var h = Wheel();
        Assert.StartsWith("Глечик відкриється на", Act(h, "form", new { ware = "jug" }).Message);
        Assert.Equal("Такого виробу гончарі не ліплять", Act(h, "form", new { ware = "vase" }).Message);

        Patch(h, s => { s["total"] = 1_000; s["pots"] = 0; });
        Click(h, 30);
        Assert.True(Act(h, "form", new { ware = "jug" }).Ok);
        var c = Craft(h);
        Assert.Equal("jug", c.GetProperty("ware").GetString());
        Assert.Equal(80, c.GetProperty("need").GetInt32());
        Assert.Equal(30, c.GetProperty("work").GetDouble());      // робота не губиться при зміні виробу
        Click(h, 50);
        Assert.Equal("jug", Craft(h).GetProperty("rack")[0].GetProperty("ware").GetString());
    }

    [Fact]
    public void Switching_to_a_smaller_ware_trims_the_work()
    {
        var h = Wheel();
        Patch(h, s => { s["total"] = 1_000; s["craft"]!["ware"] = "jug"; s["craft"]!["work"] = 70; });
        Assert.True(Act(h, "form", new { ware = "bowl" }).Ok);
        Assert.Equal(50, Craft(h).GetProperty("work").GetDouble());
    }

    [Fact]
    public void The_bazaar_pays_the_click_floor_on_a_bare_wheel()
    {
        var h = Wheel();
        Items(h, ("pot||1", 3), ("pot||3", 1));
        var before = Pots(h);
        // Голе коло: пасиву нема, тож ціна — половина кліків на виріб (40 × 0,5), дзвінкий — ×2,6.
        var r = Act(h, "bazaar", new { key = "pot||1", n = 2 });
        Assert.True(r.Ok, r.Message);
        Assert.Equal(before + 40, Pots(h));
        Assert.True(Act(h, "bazaar", new { key = "pot||3" }).Ok);
        Assert.Equal(before + 40 + 52, Pots(h));
        Assert.Equal(1, Craft(h).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public void The_bazaar_price_follows_the_passive_quality_and_style()
    {
        var h = Wheel();
        Patch(h, s => { s["upgrades"]!["kiln"] = 1000; s["styles"] = new JsonArray("gavarets"); });   // 3000 глеків/с × 1,05 за розпис — вище дна кліків
        Items(h, ("jug||1", 1), ("jug|gavarets|2", 1));
        var items = Craft(h).GetProperty("items").EnumerateArray().ToDictionary(x => x.GetProperty("key").GetString()!, x => x.GetProperty("value").GetInt64());
        var passive = View(h).GetProperty("baseSecond").GetDouble();
        Assert.Equal((long)(passive * 6), items["jug||1"]);
        Assert.Equal((long)(passive * 6 * 1.6 * 1.2), items["jug|gavarets|2"]);
    }

    [Fact]
    public void Selling_everything_asks_for_something_to_sell()
    {
        var h = Wheel();
        Assert.Equal("У коморі порожньо — нічого везти на базар", Act(h, "bazaar", new { all = true }).Message);
        Items(h, ("pot||1", 2), ("bowl||2", 1));
        var before = Pots(h);
        Assert.True(Act(h, "bazaar", new { all = true }).Ok);
        Assert.Equal(before + 2 * 20 + (long)(50 * 0.5 * 1.6), Pots(h));
        Assert.Equal(0, Craft(h).GetProperty("items").GetArrayLength());
        Assert.Equal("Такого виробу в коморі нема", Act(h, "bazaar", new { key = "pot||1" }).Message);
    }

    [Fact]
    public void Selling_up_to_a_quality_leaves_the_dearer_wares_in_the_store()
    {
        var h = Wheel();
        Items(h, ("pot||1", 2), ("pot||2", 1), ("pot||3", 1));
        var before = Pots(h);
        // «Лише звичайні» бере два горщики по 20 (дно кліків: 40 × 0,5) — добрий і дзвінкий лишаються.
        var r = Act(h, "bazaar", new { all = true, q = 1 });
        Assert.True(r.Ok, r.Message);
        Assert.Contains("(лише звичайні)", r.Message);
        Assert.Equal(before + 2 * 20, Pots(h));
        // Звичайних більше нема — кнопка про це й каже, а не про порожню комору.
        Assert.Equal("Звичайних у коморі нема", Act(h, "bazaar", new { all = true, q = 1 }).Message);
        // «Усе, крім дзвінких» забирає доброго (20 × 1,6) і лишає дзвінкого.
        var two = Act(h, "bazaar", new { all = true, q = 2 });
        Assert.True(two.Ok, two.Message);
        Assert.Contains("(крім дзвінких)", two.Message);
        Assert.Equal(before + 2 * 20 + 32, Pots(h));
        Assert.Equal("pot||3", Craft(h).GetProperty("items").EnumerateArray().Single().GetProperty("key").GetString());
        Assert.Equal("У коморі самі дзвінкі — їх базар не бере", Act(h, "bazaar", new { all = true, q = 2 }).Message);
    }

    [Fact]
    public void Broken_items_in_a_save_are_dropped()
    {
        var h = Wheel();
        Items(h, ("pot||1", 2), ("vase||1", 5), ("pot|nope|1", 5), ("pot||9", 5), ("pot||2", -3));
        var items = Craft(h).GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("pot||1", items[0].GetProperty("key").GetString());
    }

    [Fact]
    public void The_craft_survives_a_reload()
    {
        var h = Wheel();
        Click(h, 95);
        Items(h, ("bowl||2", 4));
        var before = Views.Text(Craft(h));
        Patch(h, _ => { });
        Assert.Equal(before, Views.Text(Craft(h)));
        Assert.Equal(2, RackCount(h));
    }

    [Fact]
    public void An_old_save_without_the_craft_starts_with_an_empty_rack()
    {
        var h = Wheel();
        Click(h, 50);
        Patch(h, s => s.Remove("craft"));
        var c = Craft(h);
        Assert.Equal("pot", c.GetProperty("ware").GetString());
        Assert.Equal(0, c.GetProperty("work").GetDouble());
        Assert.Equal(0, c.GetProperty("rack").GetArrayLength());
    }

    [Fact]
    public void Firing_the_workshop_burns_the_rack_and_the_store()
    {
        var h = Wheel();
        Click(h, 45);
        Patch(h, s =>
        {
            s["total"] = 2_000_000_000;
            s["craft"]!["items"] = new JsonObject { ["pot||1"] = 3 };
            s["craft"]!["firedBy"] = new JsonObject { ["pot"] = 17 };
        });
        Assert.True(Act(h, "fire").Ok);
        var c = Craft(h);
        Assert.Equal(0, c.GetProperty("rack").GetArrayLength());
        Assert.Equal(0, c.GetProperty("items").GetArrayLength());
        Assert.Equal(0, c.GetProperty("work").GetDouble());
        Assert.Equal(17, c.GetProperty("fired").GetInt64());         // майстерність рук не згорає
    }

    [Fact]
    public void Coming_back_after_a_while_shows_what_happened()
    {
        var h = Wheel();
        Patch(h, s => s["upgrades"]!["apprentice"] = 25);
        Assert.Equal(JsonValueKind.Null, View(h).GetProperty("away").ValueKind);
        h.Clock.Advance(TimeSpan.FromMinutes(30));
        var away = View(h).GetProperty("away");
        Assert.Equal(1800, away.GetProperty("seconds").GetInt64());
        Assert.True(away.GetProperty("pots").GetInt64() > 0);
        Assert.True(away.GetProperty("formed").GetInt32() > 0);

        // Коротка перерва не переписує запис про довгу.
        h.Clock.Advance(20);
        Assert.Equal(1800, View(h).GetProperty("away").GetProperty("seconds").GetInt64());
    }

    [Fact]
    public void Catalogs_ride_the_view_until_the_first_action_and_come_back_on_request()
    {
        var h = Wheel();
        var catalog = View(h).GetProperty("catalog");
        Assert.Equal(Clicker.Wares.Length, catalog.GetProperty("wares").GetArrayLength());
        Assert.Equal(Views.Text(View(h)), Views.Text(View(h)));    // вид — чиста функція стану

        Click(h, 1);
        Assert.Equal(JsonValueKind.Null, View(h).GetProperty("catalog").ValueKind);
        Assert.True(Act(h, "look", new { catalog = true }).Ok);
        Assert.Equal(JsonValueKind.Object, View(h).GetProperty("catalog").ValueKind);
        Click(h, 1);
        Assert.Equal(JsonValueKind.Null, View(h).GetProperty("catalog").ValueKind);
    }

    // ---------- прокачка ремесла (дев'яте оновлення, §B2.1) ----------

    /// <summary>Глеки в кишеню (і за весь час не менше, ніж у кишені, — інакше збереження саме підтягне total).</summary>
    static void Give(RoomHarness h, double pots, double? total = null) => Patch(h, s =>
    {
        s["pots"] = pots;
        s["total"] = Math.Max(pots, total ?? pots);
    });

    static JsonElement Up(RoomHarness h, string key) =>
        Craft(h).GetProperty("ups").EnumerateArray().First(u => u.GetProperty("key").GetString() == key);

    static ActResult BuyUp(RoomHarness h, string key) => Act(h, "craft", new { op = "up", key });

    static int RackSize(RoomHarness h) => Craft(h).GetProperty("rackSize").GetInt32();

    [Fact]
    public void The_rack_upgrade_adds_two_places_a_level_and_stops_at_ten()
    {
        var h = Wheel();
        Assert.Equal(Clicker.RackBase, RackSize(h));
        Give(h, 1e15);
        for (var i = 0; i < 10; i++) Assert.True(BuyUp(h, "rack").Ok);
        Assert.Equal(Clicker.RackBase + 20, RackSize(h));
        Assert.Equal(10, Up(h, "rack").GetProperty("level").GetInt32());
        Assert.Equal("Сушарня: більшої вже не буває", BuyUp(h, "rack").Message);
        // Стеля 20 місць стосується лише гончарні: прокачка кладеться зверху.
        Patch(h, s => s["upgrades"]!["workshop"] = 500);
        Assert.Equal(Clicker.RackMax + 20, RackSize(h));
    }

    [Fact]
    public void Every_level_of_an_upgrade_costs_four_times_the_last()
    {
        var h = Wheel();
        var up = Clicker.CraftUps.First(u => u.Key == "rack");
        Assert.Equal(5_000_000, Clicker.CraftUpPrice(up, 0));
        Assert.Equal(20_000_000, Clicker.CraftUpPrice(up, 1));
        Assert.Equal(5_000_000 * Math.Pow(4, 9), Clicker.CraftUpPrice(up, 9));

        Give(h, 25_000_000);
        Assert.Equal(5_000_000, Up(h, "rack").GetProperty("price").GetDouble());
        Assert.True(BuyUp(h, "rack").Ok);
        Assert.Equal(20_000_000, Pots(h));
        Assert.Equal(20_000_000, Up(h, "rack").GetProperty("price").GetDouble());
        Assert.True(BuyUp(h, "rack").Ok);
        Assert.Equal(0, Pots(h));
        Assert.Equal("Бракує глеків: треба ще 80 млн", BuyUp(h, "rack").Message);
        // Куплена стеля ціни не має: остання ціна лишається видимою, поки є що купувати.
        Assert.Equal(0, Up(h, "store").GetProperty("level").GetInt32());
        Assert.Equal("Такого в майстерні не прокачують", BuyUp(h, "veranda").Message);
        Assert.Equal("Тут так не ходять", Act(h, "craft", new { op = "down", key = "rack" }).Message);
    }

    [Fact]
    public void The_kiln_room_the_store_and_the_windy_rack_do_what_they_promise()
    {
        var h = Wheel();
        Give(h, 1e15);
        var slots = View(h).GetProperty("kiln").GetProperty("slots").GetInt32();
        Assert.True(BuyUp(h, "kilnroom").Ok);
        Assert.True(BuyUp(h, "kilnroom").Ok);
        Assert.Equal(slots + 2, View(h).GetProperty("kiln").GetProperty("slots").GetInt32());

        Assert.Equal(Clicker.StoreCap, Craft(h).GetProperty("storeCap").GetInt32());
        Assert.True(BuyUp(h, "store").Ok);
        Assert.Equal(Clicker.StoreCap + 50, Craft(h).GetProperty("storeCap").GetInt32());

        // Погода села теж множить сушіння, тож міряємо від того, що є зараз: два рівні — це рівно −10 %.
        var dry = Craft(h).GetProperty("dryMs").GetDouble();
        Assert.True(BuyUp(h, "dryer").Ok);
        Assert.True(BuyUp(h, "dryer").Ok);
        Assert.Equal(dry * 0.9, Craft(h).GetProperty("dryMs").GetDouble(), 6);
        Click(h, 40);
        var dryAt = Craft(h).GetProperty("rack")[0].GetProperty("dryAt").GetDateTimeOffset();
        // Сирець став на сушарню на секунду раніше за «зараз» (пачки кліків ідуть по секунді).
        Assert.InRange((dryAt - h.Clock.UtcNow).TotalSeconds, dry * 0.9 / 1000 - 2, dry * 0.9 / 1000);

        // Палій — це поки що лише рівень: якість і автогорно рахує горно (§B1.6).
        Assert.True(BuyUp(h, "stoker").Ok);
        Assert.Equal(1, Up(h, "stoker").GetProperty("level").GetInt32());
        Assert.Contains("палій", Up(h, "stoker").GetProperty("now").GetString());
    }

    [Fact]
    public void A_bigger_store_keeps_more_wares_before_the_bazaar_takes_them()
    {
        var h = Wheel();
        Give(h, 1e15);
        Assert.True(BuyUp(h, "store").Ok);
        Patch(h, s => s["craft"]!["items"] = new JsonObject { ["pot||1"] = 245 });
        Assert.Equal(245, Craft(h).GetProperty("items").EnumerateArray().Sum(x => x.GetProperty("n").GetInt32()));
        // 250 — стеля прокачаної комори: п'ять уліземо, шостий поїде на базар сам.
        Assert.True(Act(h, "bazaar", new { key = "pot||1", n = 1 }).Ok);
        Assert.Equal(244, Craft(h).GetProperty("items").EnumerateArray().Sum(x => x.GetProperty("n").GetInt32()));
    }

    [Fact]
    public void The_upgrades_are_walls_and_the_firing_does_not_burn_them()
    {
        var h = Wheel();
        Give(h, 1e15, 2e9);
        Assert.True(BuyUp(h, "rack").Ok);
        Assert.True(BuyUp(h, "store").Ok);
        Assert.True(Act(h, "fire").Ok);
        Assert.Equal(1, Up(h, "rack").GetProperty("level").GetInt32());
        Assert.Equal(1, Up(h, "store").GetProperty("level").GetInt32());
        Assert.Equal(Clicker.RackBase + 2, RackSize(h));
        Assert.Equal(Clicker.StoreCap + 50, Craft(h).GetProperty("storeCap").GetInt32());
    }

    [Fact]
    public void The_upgrades_survive_a_reload_and_an_old_save_simply_has_none()
    {
        var h = Wheel();
        Give(h, 1e15);
        var dry = Craft(h).GetProperty("dryMs").GetDouble();
        Assert.True(BuyUp(h, "dryer").Ok);
        Assert.True(BuyUp(h, "kilnroom").Ok);
        var before = Views.Text(Craft(h));
        Patch(h, _ => { });
        Assert.Equal(before, Views.Text(Craft(h)));

        // Збереження з проду про прокачку не знає — і це просто «цього ще не було».
        Patch(h, s => s["craft"]!.AsObject().Remove("ups"));
        Assert.All(Craft(h).GetProperty("ups").EnumerateArray(), u => Assert.Equal(0, u.GetProperty("level").GetInt32()));
        Assert.Equal(Clicker.RackBase, RackSize(h));
        Assert.Equal(dry, Craft(h).GetProperty("dryMs").GetDouble());
    }

    [Fact]
    public void A_made_up_upgrade_in_a_save_is_dropped_and_a_huge_one_is_trimmed()
    {
        var h = Wheel();
        Patch(h, s => s["craft"]!["ups"] = new JsonObject { ["rack"] = 99, ["veranda"] = 5, ["store"] = -3 });
        Assert.Equal(10, Up(h, "rack").GetProperty("level").GetInt32());
        Assert.Equal(0, Up(h, "store").GetProperty("level").GetInt32());
        Assert.Equal(Clicker.CraftUps.Length, Craft(h).GetProperty("ups").GetArrayLength());
        Assert.Equal(Clicker.RackBase + 20, RackSize(h));
    }

    [Fact]
    public void A_full_rack_of_a_maxed_workshop_is_read_back_whole()
    {
        var h = Wheel();
        Give(h, 1e15);
        Patch(h, s => s["upgrades"]!["workshop"] = 500);
        for (var i = 0; i < 10; i++) Assert.True(BuyUp(h, "rack").Ok);
        var size = RackSize(h);
        Assert.Equal(Clicker.RackMax + 20, size);
        Patch(h, s =>
        {
            var rack = new JsonArray();
            for (var i = 0; i < size; i++) rack.Add(new JsonObject { ["ware"] = "pot", ["clay"] = "", ["dryAt"] = h.Clock.UtcNow });
            s["craft"]!["rack"] = rack;
        });
        Assert.Equal(size, RackCount(h));
    }

    // ---------- три нові вироби (§B2.2) ----------

    [Fact]
    public void The_three_new_wares_close_the_catalog_of_fifteen()
    {
        Assert.Equal(15, Clicker.Wares.Length);
        var late = Clicker.Wares[^3..];
        Assert.Equal(new[] { "kukhol", "tykva", "pleskanets" }, late.Select(w => w.Key));
        Assert.Equal(new[] { 220, 380, 450 }, late.Select(w => w.Work));
        Assert.Equal(new[] { 22d, 40, 52 }, late.Select(w => w.Seconds));
        Assert.Equal(new[] { 5e12, 5e13, 5e14 }, late.Select(w => w.Unlock));
        // Каталог лише дописується в кінець: старі дванадцять стоять на своїх місцях (маски альбому — за ключами).
        Assert.Equal("pot", Clicker.Wares[0].Key);
        Assert.Equal("lion", Clicker.Wares[11].Key);
    }

    [Fact]
    public void A_mug_opens_on_five_trillion_and_asks_for_two_hundred_and_twenty()
    {
        var h = Wheel();
        Assert.Equal("Кухоль відкриється на 5 трлн глеків за весь час", Act(h, "form", new { ware = "kukhol" }).Message);
        Give(h, 0, 5e12);
        Assert.True(Act(h, "form", new { ware = "kukhol" }).Ok);
        Assert.Equal(220, Craft(h).GetProperty("need").GetInt32());
        Assert.Equal("Плесканець відкриється на 500 трлн глеків за весь час", Act(h, "form", new { ware = "pleskanets" }).Message);

        // Ціна виробу — секунди пасиву, як і в усієї драбини: тиква вдвічі дорожча за кухоль.
        Patch(h, s => s["upgrades"]!["kiln"] = 1000);
        var passive = View(h).GetProperty("baseSecond").GetDouble();
        var wares = Craft(h).GetProperty("wares").EnumerateArray().ToDictionary(x => x.GetProperty("key").GetString()!, x => x.GetProperty("value").GetDouble());
        Assert.Equal(Math.Floor(passive * 22), wares["kukhol"]);
        Assert.Equal(Math.Floor(passive * 40), wares["tykva"]);
        Assert.Equal(Math.Floor(passive * 52), wares["pleskanets"]);
    }

    /// <summary>Виліпити виріб, що стоїть на колі, скільки б роботи він не просив.</summary>
    static void FormOne(RoomHarness h, string ware)
    {
        Assert.True(Act(h, "form", new { ware }).Ok);
        Click(h, Craft(h).GetProperty("need").GetInt32());
    }

    [Fact]
    public void Forming_the_fifteenth_ware_is_an_achievement()
    {
        var h = Wheel();
        Give(h, 0, 1e16);
        Patch(h, s => s["craft"]!["formedBy"] =
            new JsonArray([.. Clicker.Wares.Take(Clicker.Wares.Length - 1).Select(w => (JsonNode)w.Key!)]));
        Click(h, 40);
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:potter-ware-15");
        FormOne(h, "pleskanets");
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-ware-15");
    }

    [Fact]
    public void An_old_save_gets_credit_for_everything_it_has_already_fired()
    {
        var h = Wheel();
        Give(h, 0, 1e16);
        // Збереження з проду про «хто що ліпив» не знає, зате знає, що обпалено: обпалений виріб хтось таки
        // виліпив. Ветеранові лишається виліпити три нові вироби, а не всі п'ятнадцять наново.
        Patch(h, s =>
        {
            var fired = new JsonObject();
            foreach (var w in Clicker.Wares[..12]) fired[w.Key] = 3;
            s["craft"]!["firedBy"] = fired;
            s["craft"]!.AsObject().Remove("formedBy");
        });
        FormOne(h, "kukhol");
        FormOne(h, "tykva");
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:potter-ware-15");
        FormOne(h, "pleskanets");
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-ware-15");
    }

    // ---------- секрети другого кола (§1) ----------

    [Fact]
    public void The_second_rack_adds_six_places_and_the_grandson_speeds_the_apprentices()
    {
        var h = Wheel();
        Patch(h, s => s["upgrades"]!["apprentice"] = 100);
        var rack = RackSize(h);
        Assert.Equal(Clicker.ApprenticeWorkMax, Craft(h).GetProperty("apprentice").GetDouble());
        Patch(h, s => s["secrets"] = new JsonArray("rack2", "grandson"));
        // Каталог секретів другого кола приносить пакет «Коло» (контракт §A.5). Поки його нема, ключ із збереження
        // відсівається й нічого не міняється, а щойно він з'явиться — ті самі +6 місць і 0,8 роботи за секунду.
        var known = Clicker.Secrets.Any(s => s.Key == "rack2");
        Assert.Equal(known ? rack + 6 : rack, RackSize(h));
        Assert.Equal(known ? Clicker.ApprenticeWorkGrandson : Clicker.ApprenticeWorkMax, Craft(h).GetProperty("apprentice").GetDouble());
    }

    [Fact]
    public void While_the_master_waits_the_wheel_forms_nothing()
    {
        var h = Wheel();
        Click(h, 10);
        Patch(h, s => s["guard"]!["left"] = 0);
        Click(h, 12);                                              // ця пачка кличе майстра
        var work = Craft(h).GetProperty("work").GetDouble();
        Click(h, 24);
        Assert.Equal(work, Craft(h).GetProperty("work").GetDouble());
    }
}
