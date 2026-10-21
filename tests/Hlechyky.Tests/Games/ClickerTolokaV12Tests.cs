using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Толока у v12 (docs/games/specs/clicker-v12.md §2): склад наперед — вироби на будь-який незакладений етап поза
/// коморою, «усе, що підходить» спершу в найсуворіші рядки, забрати назад, закладка зі складу; і своя праця — ручні
/// кліки під час будови скорочують етап не більше ніж на 15 %.
/// </summary>
public class ClickerTolokaV12Tests
{
    static RoomHarness Wheel(string nick = "Оля")
    {
        var h = new RoomHarness("clicker");
        h.Solo(nick);
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static JsonElement T(RoomHarness h) => View(h).GetProperty("toloka");
    static ActResult Toloka(RoomHarness h, object payload) => h.Act(0, "toloka", payload);

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

    static void Items(RoomHarness h, params (string Key, int N)[] items) => Patch(h, s =>
    {
        var bag = new JsonObject();
        foreach (var (key, n) in items) bag[key] = n;
        s["craft"]!["items"] = bag;
    });

    static int Item(RoomHarness h, string key)
    {
        foreach (var it in View(h).GetProperty("craft").GetProperty("items").EnumerateArray())
            if (it.GetProperty("key").GetString() == key) return it.GetProperty("n").GetInt32();
        return 0;
    }

    static void Built(RoomHarness h, params string[] built) => Patch(h, s =>
        s["toloka"] = new JsonObject { ["built"] = new JsonArray(built.Select(x => (JsonNode)x).ToArray()) });

    static string[] Upto(string key) => Clicker.Buildings.Select(b => b.Key).TakeWhile(k => k != key).Append(key).ToArray();

    static int Depot(RoomHarness h, string b, int s, int i)
    {
        foreach (var st in T(h).GetProperty("plan").EnumerateArray())
            if (st.GetProperty("building").GetString() == b && st.GetProperty("index").GetInt32() == s)
                return st.GetProperty("needs")[i].GetProperty("depot").GetInt32();
        return -1;
    }

    static Dictionary<string, int> DepotBag(RoomHarness h, string key)
    {
        var bag = new Dictionary<string, int>();
        var node = JsonNode.Parse(h.Room.Game.Save()!)!["toloka"]?["depot"]?[key]?.AsObject();
        if (node is not null) foreach (var (k, v) in node) bag[k] = v!.GetValue<int>();
        return bag;
    }

    // ---------- склад ----------

    [Fact]
    public void Stocking_a_future_stage_takes_the_wares_out_of_the_store_and_the_lay_counts_them()
    {
        var h = Wheel();
        Give(h, 1e17);
        Items(h, ("pot||1", 15), ("bowl||1", 15), ("jug||1", 20), ("makitra||2", 10));
        // Криниця, етап 2 «Дубовий зруб»: глечик ×20 і макітра ×10 (добра й краща).
        var r = Toloka(h, new { @do = "stock", b = "well", s = 1, i = 0, n = 99 });
        Assert.True(r.Ok, r.Message);
        Assert.Equal(0, Item(h, "jug||1"));
        Assert.Equal(20, Depot(h, "well", 1, 0));
        Assert.False(Toloka(h, new { @do = "stock", b = "well", s = 1, i = 0, n = 1 }).Ok);   // рядок повний

        Assert.True(Toloka(h, new { @do = "lay" }).Ok);
        h.Clock.Advance(TimeSpan.FromHours(1));
        // Глечиків у коморі нема, але склад їх дав — етап закладається.
        var lay = Toloka(h, new { @do = "lay" });
        Assert.True(lay.Ok, lay.Message);
        Assert.Equal(0, Item(h, "makitra||2"));
        Assert.Empty(DepotBag(h, "well|1|0"));
    }

    [Fact]
    public void Stock_all_fills_the_strictest_rows_first_so_luxury_goes_where_only_luxury_fits()
    {
        var h = Wheel();
        Give(h, 1e40);
        Built(h, Upto("square").Append("school").ToArray());
        // Пристань: «Поміст» — глечик ×50 дзвінкий і кращий; «Ліхтар» — глечик ×20 лише розкішний.
        Items(h, ("jug||3", 30), ("jug||4", 20));
        var r = Toloka(h, new { @do = "stockAll" });
        Assert.True(r.Ok, r.Message);
        Assert.Contains("розкішних 20", r.Message);
        Assert.Equal(new Dictionary<string, int> { ["jug||4"] = 20 }, DepotBag(h, "pier|3|2"));
        // Дзвінкі — у «Поміст»; що лишилось у коморі — відкладене під майстерштук цеху, склад його не бере.
        var floor = DepotBag(h, "pier|1|1");
        Assert.Equal(["jug||3"], floor.Keys);
        Assert.Equal(30, floor["jug||3"] + Item(h, "jug||3"));
        Assert.InRange(floor["jug||3"], 25, 30);
        Assert.Equal(0, Item(h, "jug||4"));
    }

    [Fact]
    public void Stocking_from_an_empty_store_says_what_is_missing()
    {
        var h = Wheel();
        Give(h, 1e17);
        Items(h);
        var r = Toloka(h, new { @do = "stock", b = "well", s = 1, i = 0, n = 5 });
        Assert.False(r.Ok);
        Assert.Contains("нема", r.Message);
    }

    [Fact]
    public void Unstock_returns_the_very_same_wares_while_there_is_room()
    {
        var h = Wheel();
        Give(h, 1e17);
        Items(h, ("jug||1", 12), ("jug||2", 8));
        Assert.True(Toloka(h, new { @do = "stock", b = "well", s = 1, i = 0, n = 20 }).Ok);
        Assert.Equal(new Dictionary<string, int> { ["jug||1"] = 12, ["jug||2"] = 8 }, DepotBag(h, "well|1|0"));
        var r = Toloka(h, new { @do = "unstock", b = "well", s = 1, i = 0 });
        Assert.True(r.Ok, r.Message);
        Assert.Equal(12, Item(h, "jug||1"));
        Assert.Equal(8, Item(h, "jug||2"));
        Assert.False(Toloka(h, new { @do = "unstock", b = "well", s = 1, i = 0 }).Ok);
    }

    [Fact]
    public void The_depot_and_the_work_survive_save_and_load()
    {
        var h = Wheel();
        Give(h, 1e17);
        Items(h, ("pot||1", 15), ("bowl||1", 15), ("jug||1", 20));
        Assert.True(Toloka(h, new { @do = "stock", b = "well", s = 1, i = 0, n = 20 }).Ok);
        Assert.True(Toloka(h, new { @do = "lay" }).Ok);
        Patch(h, s => s["toloka"]!["work"] = 700);
        Patch(h, _ => { });
        Assert.Equal(20, Depot(h, "well", 1, 0));
        Assert.Equal(700, T(h).GetProperty("stage").GetProperty("work").GetProperty("n").GetInt64());
    }

    [Fact]
    public void Selling_everything_keeps_what_the_toloka_waits_for_unless_told_otherwise()
    {
        var h = Wheel();
        Give(h, 1e17);
        // Криниця, «Копаємо яму»: горщик ×15 і миска ×15 — етап ще не закладено, тож комора їх тримає.
        // Горщиків із запасом: свіжа дошка сіл теж може чекати кількох.
        Items(h, ("pot||1", 200), ("bowl||1", 15));
        var r = h.Act(0, "bazaar", new { all = true });
        Assert.True(r.Ok, r.Message);
        Assert.Contains("лишив під замовлення й Толоку", r.Message);
        Assert.InRange(Item(h, "pot||1"), 15, 199);
        Assert.Equal(15, Item(h, "bowl||1"));
        Assert.False(h.Act(0, "bazaar", new { all = true }).Ok);
        Assert.True(h.Act(0, "bazaar", new { all = true, keep = false }).Ok);
        Assert.Equal(0, Item(h, "pot||1"));
    }

    // ---------- своя праця ----------

    [Fact]
    public void Hand_clicks_during_the_build_shorten_the_stage_by_at_most_fifteen_percent()
    {
        var h = Wheel();
        Give(h, 1e17);
        Items(h, ("pot||1", 15), ("bowl||1", 15));
        // До закладки кліки не рахуються.
        Assert.True(h.Act(0, "spin", PotterHands.Human(5)).Ok);
        Assert.True(Toloka(h, new { @do = "lay" }).Ok);
        var work = T(h).GetProperty("stage").GetProperty("work");
        Assert.Equal(0, work.GetProperty("n").GetInt64());
        Assert.Equal((long)(Clicker.TolokaWorkPerHour * 1), work.GetProperty("of").GetInt64());

        Assert.True(h.Act(0, "spin", PotterHands.Human(5)).Ok);
        Assert.InRange(T(h).GetProperty("stage").GetProperty("work").GetProperty("n").GetInt64(), 1, 5);

        // Смуга добивається ручним кліком — і ачівка; повна смуга — рівно −15 %: година стає 51 хвилиною.
        Patch(h, s => s["toloka"]!["work"] = Clicker.TolokaWorkPerHour - 1);
        Assert.True(h.Act(0, "spin", PotterHands.Human(2)).Ok);
        Assert.Equal((long)Clicker.TolokaWorkPerHour, T(h).GetProperty("stage").GetProperty("work").GetProperty("n").GetInt64());
        var laid = T(h).GetProperty("stage").GetProperty("laidAt").GetDateTimeOffset();
        var ends = T(h).GetProperty("stage").GetProperty("endsAt").GetDateTimeOffset();
        Assert.Equal(TimeSpan.FromMinutes(51), ends - laid);
        h.Clock.Advance(TimeSpan.FromMinutes(51));
        Assert.Equal(1, T(h).GetProperty("stage").GetProperty("index").GetInt32());
        Assert.True(h.Act(0, "look").Ok);
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-toloka-work");
    }
}
