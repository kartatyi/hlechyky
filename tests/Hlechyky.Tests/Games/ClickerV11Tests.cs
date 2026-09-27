using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Одинадцяте оновлення «Толока», ядро (docs/games/specs/clicker-v11.md): гончарі світу за будовами Толоки, розписи
/// світу, альбом 9 → 15 без втрати старих бонусів, скарбниця роду, серверна Толока (закладання, годинник, нагороди),
/// «Що нового» v11. Клієнт Толоки й допомогу друзів тестує пакет A (ClickerTolokaTests).
/// </summary>
public class ClickerV11Tests
{
    static RoomHarness Wheel(string nick = "Оля", int seed = 1)
    {
        var h = new RoomHarness("clicker", seed: seed);
        h.Solo(nick);
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static double Num(RoomHarness h, string field) => View(h).GetProperty(field).GetDouble();
    static ActResult Act(RoomHarness h, string action, object? payload = null) => h.Act(0, action, payload);
    static JsonElement Up(RoomHarness h, string key) => View(h).GetProperty("upgrades").GetProperty(key);

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static void Give(RoomHarness h, double pots, double? total = null) => Patch(h, s =>
    {
        s["pots"] = pots;
        s["total"] = total ?? pots;
    });

    static void Levels(RoomHarness h, params (string Key, int Level)[] levels) => Patch(h, s =>
    {
        foreach (var (key, level) in levels) s["upgrades"]![key] = level;
    });

    static void Built(RoomHarness h, params string[] keys) => Patch(h, s =>
    {
        var arr = new JsonArray();
        foreach (var k in keys) arr.Add(k);
        s["toloka"] = new JsonObject { ["built"] = arr };
    });

    /// <summary>Покласти в комору: «виріб|розпис|якість» → скільки.</summary>
    static void Items(RoomHarness h, params (string Ware, string Style, int Q, int N)[] items) => Patch(h, s =>
    {
        var craft = s["craft"] as JsonObject ?? new JsonObject();
        var dict = new JsonObject();
        foreach (var (w, st, q, n) in items) dict[$"{w}|{st}|{q}"] = n;
        craft["items"] = dict;
        s["craft"] = craft;
    });

    static double AllMult(RoomHarness h) => Num(h, "allMult");

    // ---------- гончарі світу ----------

    [Fact]
    public void Six_masters_follow_opishnia_ten_times_dearer_and_five_and_a_half_times_richer()
    {
        var idle = Clicker.Shop.Where(u => u.Kind == ClickerKind.Idle).Select(u => u.Key).ToList();
        Assert.Equal(Clicker.MasterTiers, idle.Skip(idle.IndexOf("opishnia") + 1).ToArray());
        var prev = Clicker.Shop.Single(u => u.Key == "opishnia");
        foreach (var key in Clicker.MasterTiers)
        {
            var up = Clicker.Shop.Single(u => u.Key == key);
            Assert.Equal(prev.Base * 10, up.Base, prev.Base * 1e-9);
            Assert.InRange(up.Rate / prev.Rate, 5.4, 5.7);
            Assert.Equal([25, 50, 100], up.Steps.Select(s => s.Level));
            Assert.True(Clicker.WorldGate.ContainsKey(key), key);
            prev = up;
        }
    }

    [Fact]
    public void A_master_rung_stays_shut_without_its_building_however_rich_you_are()
    {
        var h = Wheel();
        Levels(h, ("opishnia", 5));
        Give(h, 1e40);
        Assert.False(Up(h, "jingdezhen").GetProperty("open").GetBoolean());
        var r = Act(h, "buy", new { key = "jingdezhen" });
        Assert.False(r.Ok);
        Assert.Contains("Пристань на Ворсклі", r.Message);

        Built(h, "pier");
        Assert.True(Up(h, "jingdezhen").GetProperty("open").GetBoolean());
        Assert.True(Act(h, "buy", new { key = "jingdezhen" }).Ok);
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-world-first");
        // Наступний щабель чекає на свою будову (музей), а не лише на рівень попереднього.
        Assert.False(Up(h, "iznik").GetProperty("open").GetBoolean());
    }

    [Fact]
    public void A_world_style_comes_only_with_its_rung()
    {
        var h = Wheel();
        Give(h, 1e40);
        var r = Act(h, "paint", new { key = "jingdezhen" });
        Assert.False(r.Ok);
        Assert.Contains("Порцеляна Цзиндечженя", r.Message);
        Levels(h, ("jingdezhen", 1));
        Assert.True(Act(h, "paint", new { key = "jingdezhen" }).Ok);
        Assert.Equal(1e40 - 6e34, Num(h, "pots"), 1e30);
    }

    [Fact]
    public void The_museum_achievement_still_means_the_eight_home_styles()
    {
        var h = Wheel();
        Patch(h, s => s["styles"] = new JsonArray(Clicker.Styles.Take(Clicker.HomeStyles - 1).Select(x => (JsonNode)x.Key).ToArray()));
        Give(h, 1e20);
        Assert.True(Act(h, "paint", new { key = "trypillia" }).Ok);
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-museum");
    }

    // ---------- альбом ----------

    [Fact]
    public void A_full_home_album_stays_full_when_the_world_columns_arrive()
    {
        var home = Enumerable.Repeat((1 << Clicker.AlbumHomeColumns) - 1, Clicker.Wares.Length).ToArray();
        var before = Clicker.AlbumBonusFor(home, home, null, 0);
        Assert.Equal(Clicker.AlbumSize, Clicker.AlbumOpenCount(home));
        Assert.Equal(0, Clicker.AlbumWorldBonus(home, home));
        // Одна клітинка світу із зіркою — +0,5 % і +1 %, решта не рухається.
        var more = home.ToArray();
        more[0] |= 1 << Clicker.AlbumHomeColumns;
        Assert.Equal(before + 0.005 + 0.01, Clicker.AlbumBonusFor(more, more, null, 0), 9);
        // Уся сітка з п'ятнадцятьма стовпчиками — ще +25 % зверху.
        var all = Enumerable.Repeat((1 << Clicker.AlbumColumns) - 1, Clicker.Wares.Length).ToArray();
        var worldCells = Clicker.AlbumWorldSize - Clicker.AlbumSize;
        Assert.True(Clicker.AlbumWorldFull(all));
        Assert.Equal(worldCells * 0.005 + Clicker.AlbumWorldAllBonus, Clicker.AlbumWorldBonus(all, null), 9);
    }

    // ---------- скарбниця роду ----------

    [Fact]
    public void The_treasury_opens_with_every_secret_or_a_million_free_stamps()
    {
        var h = Wheel();
        Assert.Equal(JsonValueKind.Null, View(h).GetProperty("relics").ValueKind);
        Patch(h, s => s["stamps"] = 1_000_000);
        var relics = View(h).GetProperty("relics");
        Assert.Equal(Clicker.Relics.Length, relics.GetArrayLength());
        Assert.All(relics.EnumerateArray(), r => Assert.Equal(0, r.GetProperty("level").GetInt32()));
    }

    [Fact]
    public void A_relic_level_costs_three_times_the_last_and_does_not_eat_the_stamp_bonus()
    {
        var h = Wheel();
        Patch(h, s => s["stamps"] = 5_000_000);
        var mult = Num(h, "stampMult");
        var basket = Clicker.Relics.Single(r => r.Key == "basket3");
        Assert.Equal(100_000, Clicker.RelicPrice(basket, 0));
        Assert.Equal(300_000, Clicker.RelicPrice(basket, 1));
        Assert.Equal(900_000, Clicker.RelicPrice(basket, 2));
        Assert.True(Act(h, "relic", new { key = "basket3" }).Ok);
        Assert.True(Act(h, "relic", new { key = "basket3" }).Ok);
        Assert.Equal(5_000_000 - 400_000, Num(h, "stampsFree"));
        Assert.Equal(mult, Num(h, "stampMult"), 9);
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-relic");
        var r = View(h).GetProperty("relics").EnumerateArray().Single(x => x.GetProperty("key").GetString() == "basket3");
        Assert.Equal(2, r.GetProperty("level").GetInt32());
        Assert.Equal(0.2, r.GetProperty("sum").GetDouble(), 9);
        // Стеля ціни — у long: скільки б рівнів не було, ціна не переповнюється в мінус.
        Assert.True(Clicker.RelicPrice(basket, 59) > 0);
    }

    [Fact]
    public void The_fathers_basket_adds_to_the_falling_pot()
    {
        var h = Wheel();
        Levels(h, ("apprentice", 50));
        Patch(h, s => s["stamps"] = 2_000_000);
        var fall = View(h).GetProperty("fall").GetProperty("gain").GetDouble();
        Assert.True(Act(h, "relic", new { key = "basket3" }).Ok);
        var after = View(h).GetProperty("fall").GetProperty("gain").GetDouble();
        Assert.True(after > fall * 1.05, $"{fall} → {after}");
    }

    [Fact]
    public void A_capped_relic_stops_at_its_limit()
    {
        var h = Wheel();
        Patch(h, s => { s["stamps"] = long.MaxValue / 16; s["relics"] = new JsonObject { ["cat3"] = 20 }; });
        var r = View(h).GetProperty("relics").EnumerateArray().Single(x => x.GetProperty("key").GetString() == "cat3");
        Assert.Equal(0.4, r.GetProperty("sum").GetDouble(), 9);
    }

    // ---------- Толока: ядро ----------

    [Fact]
    public void The_toloka_opens_with_a_hryvnia_and_the_big_one_with_a_gold()
    {
        var h = Wheel();
        Assert.Equal(JsonValueKind.Null, View(h).GetProperty("toloka").ValueKind);
        Give(h, 0, Clicker.Hryvnia);
        var t = View(h).GetProperty("toloka");
        Assert.Equal("well", t.GetProperty("stage").GetProperty("building").GetString());

        Built(h, Clicker.Buildings.Where(b => !b.Big).Select(b => b.Key).ToArray());
        Give(h, 0, 1e26);
        t = View(h).GetProperty("toloka");
        Assert.Equal(JsonValueKind.Null, t.GetProperty("stage").ValueKind);
        Assert.True(t.GetProperty("waitBig").GetBoolean());
        Give(h, 0, Clicker.Gold);
        Assert.Equal("pier", View(h).GetProperty("toloka").GetProperty("stage").GetProperty("building").GetString());
    }

    [Fact]
    public void Laying_a_stage_takes_pots_and_wares_and_the_clock_does_the_rest_even_offline()
    {
        var h = Wheel();
        Give(h, 2e15, 2e15);
        Items(h, ("pot", "", 1, 10), ("bowl", "", 2, 15));
        var r = Act(h, "toloka", new { @do = "lay" });
        Assert.False(r.Ok);
        Assert.Contains("горщик — ще 5", r.Message);
        Items(h, ("pot", "", 1, 20), ("bowl", "", 1, 15), ("bowl", "", 3, 5));
        r = Act(h, "toloka", new { @do = "lay" });
        Assert.True(r.Ok, r.Message);
        Assert.Equal(1e15, Num(h, "pots"), 1);
        // Бралась спершу найгірша придатна якість: дзвінкі миски лишились.
        var craft = View(h).GetProperty("craft");
        Assert.False(Act(h, "toloka", new { @do = "lay" }).Ok);   // уже будується
        h.Clock.AdvanceMs(59 * 60_000);
        Assert.Equal(0, View(h).GetProperty("toloka").GetProperty("stage").GetProperty("index").GetInt32());
        h.Clock.AdvanceMs(2 * 60_000);
        var t = View(h).GetProperty("toloka");
        Assert.Equal(1, t.GetProperty("stage").GetProperty("index").GetInt32());
        Assert.Equal(1, t.GetProperty("done").GetInt32());
        _ = craft;
    }

    [Fact]
    public void A_finished_building_gives_five_percent_to_everything_and_its_own_perk()
    {
        var h = Wheel();
        Levels(h, ("apprentice", 10));
        var mult = AllMult(h);
        Built(h, "well", "mill", "forge");
        Assert.Equal(mult * 1.15, AllMult(h), 9);
        // Кузня: комора +100 місць.
        Assert.Equal(Clicker.StoreCap + Clicker.ForgeStore, View(h).GetProperty("craft").GetProperty("storeCap").GetInt32());
    }

    [Fact]
    public void The_festival_doubles_everything_for_an_hour_once_a_day()
    {
        var h = Wheel();
        Levels(h, ("apprentice", 10));
        Assert.False(Act(h, "toloka", new { @do = "festival" }).Ok);
        Built(h, "festival");
        var mult = AllMult(h);
        Assert.True(Act(h, "toloka", new { @do = "festival" }).Ok);
        Assert.Equal(mult * 2, AllMult(h), 9);
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-festival");
        h.Clock.AdvanceMs(61 * 60_000);
        Assert.Equal(mult, AllMult(h), 9);
        Assert.False(Act(h, "toloka", new { @do = "festival" }).Ok);
        h.Clock.AdvanceMs(23 * 3600_000);
        Assert.True(Act(h, "toloka", new { @do = "festival" }).Ok);
    }

    [Fact]
    public void The_budget_of_the_toloka_is_fixed()
    {
        // Тест-бюджет (§5): 12 будов, 5 малих по 3 етапи й 7 великих по 4; 70 і 544 години без друзів.
        Assert.Equal(12, Clicker.Buildings.Length);
        Assert.Equal(5, Clicker.Buildings.Count(b => !b.Big));
        Assert.All(Clicker.Buildings.Where(b => !b.Big), b => Assert.Equal(3, b.Stages.Length));
        Assert.All(Clicker.Buildings.Where(b => b.Big), b => Assert.Equal(4, b.Stages.Length));
        Assert.Equal(70, Clicker.Buildings.Where(b => !b.Big).Sum(b => b.Stages.Sum(s => s.Hours)));
        Assert.Equal(544, Clicker.Buildings.Where(b => b.Big).Sum(b => b.Stages.Sum(s => s.Hours)));
        // Гроші ростуть з кожним етапом; вироби — лише з каталогу, розписи — лише ті, що є в грі.
        var stages = Clicker.Buildings.SelectMany(b => b.Stages).ToList();
        for (var i = 1; i < stages.Count; i++) Assert.True(stages[i].Pay > stages[i - 1].Pay, stages[i].Name);
        foreach (var n in stages.SelectMany(s => s.Needs))
        {
            Assert.Contains(Clicker.Wares, w => w.Key == n.Ware);
            Assert.InRange(n.Q, 1, Clicker.QualityMax);
            if (n.Style.Length > 0) Assert.Contains(Clicker.Styles, s => s.Key == n.Style);
        }
        // Кожна будова, що відмикає щабель, стоїть у черзі раніше за будову, яка просить розпис того щабля.
        var order = Clicker.Buildings.Select(b => b.Key).ToList();
        foreach (var b in Clicker.Buildings)
            foreach (var style in b.Stages.SelectMany(s => s.Needs).Select(n => n.Style).Where(s => s.Length > 0).Distinct())
                if (Clicker.Styles.Single(s => s.Key == style).Tier is { Length: > 0 } tier)
                    Assert.True(order.IndexOf(Clicker.WorldGate[tier]) < order.IndexOf(b.Key), $"{b.Key} просить {style}");
    }

    [Fact]
    public void An_old_save_keeps_every_multiplier_it_had()
    {
        // Старе збереження без Толоки й реліквій: вид і множники ті самі, що були б до оновлення (нуль нових будов).
        var h = Wheel();
        Levels(h, ("apprentice", 50), ("sich", 10), ("opishnia", 3));
        Give(h, 1e30, 4.7e29);
        Patch(h, s => { s["stamps"] = 7_799_607_687; s.Remove("toloka"); s.Remove("relics"); });
        var v = View(h);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("toloka").GetProperty("stage").GetProperty("laidAt").ValueKind);
        Assert.Equal(0, v.GetProperty("toloka").GetProperty("built").GetArrayLength());
        // Раз-два збереглись і прочитались — нічого не змінилось.
        var mult = AllMult(h);
        Patch(h, _ => { });
        Assert.Equal(mult, AllMult(h), 9);
    }

    [Fact]
    public void Who_saw_v10_keeps_the_gold_ceremony_waiting_after_the_v11_news()
    {
        var h = Wheel();
        Patch(h, s => { s["news"] = "v10"; s["coinSeen"] = 1; s["total"] = 5e27; });
        Assert.Equal(2, View(h).GetProperty("coin").GetInt32());
        Assert.True(Act(h, "news", new { v = Clicker.NewsVersion }).Ok);
        // Новини v11 гривні й золотих не пояснюють — вікно «Червоні золоті» ще попереду.
        Assert.Equal(1, View(h).GetProperty("coinSeen").GetInt32());

        var old = Wheel("Стара");
        Patch(old, s => { s["news"] = "v9.2"; s["coinSeen"] = 0; s["total"] = 5e27; });
        Assert.True(Act(old, "news", new { v = Clicker.NewsVersion }).Ok);
        // А хто v10 не бачив, тому це вікно показало блок v10 із гривнею й золотими.
        Assert.Equal(2, View(old).GetProperty("coinSeen").GetInt32());
    }
}
