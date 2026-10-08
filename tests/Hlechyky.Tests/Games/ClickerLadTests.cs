using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Лад у коморі (08.10, clk12/lad): новий виріб, що не влазить у повну комору, міняється з найдешевшим незахищеним;
/// захищене — чого просять толока (поточний етап, лише чого бракує, і наступний) і замовлення гостей/сіл.
/// </summary>
public class ClickerLadTests
{
    static RoomHarness Wheel(double total = 1e9)
    {
        var h = new RoomHarness("clicker");
        h.Solo("Оля");
        Patch(h, s => { s["total"] = total; s["pots"] = 0; });
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static JsonElement Craft(RoomHarness h) => View(h).GetProperty("craft");

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
        var bag = new JsonObject();
        foreach (var (key, n) in items) bag[key] = n;
        s["craft"]!["items"] = bag;
    });

    static int N(RoomHarness h, string key) =>
        Craft(h).GetProperty("items").EnumerateArray()
            .Where(x => x.GetProperty("key").GetString() == key).Select(x => x.GetProperty("n").GetInt32()).FirstOrDefault();

    static int Total(RoomHarness h) => Craft(h).GetProperty("items").EnumerateArray().Sum(x => x.GetProperty("n").GetInt32());

    static double Value(RoomHarness h, string key) =>
        Craft(h).GetProperty("items").EnumerateArray().First(x => x.GetProperty("key").GetString() == key).GetProperty("value").GetDouble();

    static readonly MethodInfo PutItemsM = typeof(Clicker).GetMethod("PutItems", BindingFlags.NonPublic | BindingFlags.Instance)!;

    static System.Collections.IList Orders(RoomHarness h, string field) =>
        (System.Collections.IList)typeof(Clicker).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(h.Room.Game)!;

    /// <summary>Покласти в комору так, як кладуть горно, гості й толока (PutItems).</summary>
    static double Put(RoomHarness h, string ware, int q, int n, string style = "", bool guests = false)
    {
        double got;
        lock (h.Room.Sync)
        {
            // Пізня гра сама зве гостей і сільських замовників (Sync на кожному виді) — їхні замовлення теж захищені.
            // Тут перевіряємо одне правило за раз, тож чужі замовлення прибираємо.
            Orders(h, "_mktOrders").Clear();
            var g = Orders(h, "_gOrders");
            for (var i = g.Count - 1; i >= 0; i--)
                if (!guests || (int)g[i]!.GetType().GetProperty("Id")!.GetValue(g[i])! != 1) g.RemoveAt(i);
            got = (double)PutItemsM.Invoke(h.Room.Game, [ware, style, q, n])!;
        }
        h.Clock.Advance(0.1);                      // вид кешується на 50 мс, а дія повз каркас його не скидає
        return got;
    }

    [Fact]
    public void A_new_better_ware_takes_the_place_of_the_cheapest_and_the_cheapest_goes_to_the_bazaar()
    {
        var h = Wheel();
        Items(h, ("pot||1", 150), ("bowl||1", 50));
        var pot = Value(h, "pot||1");
        var sold = Put(h, "barrel", 3, 10);
        Assert.Equal(140, N(h, "pot||1"));
        Assert.Equal(50, N(h, "bowl||1"));
        Assert.Equal(10, N(h, "barrel||3"));
        Assert.Equal(200, Total(h));
        Assert.Equal(pot * 10, sold, 6);
    }

    [Fact]
    public void Nothing_cheaper_in_the_store_sends_the_new_ware_to_the_bazaar_as_before()
    {
        var h = Wheel();
        Items(h, ("barrel||3", 200));
        var sold = Put(h, "pot", 1, 5);
        Assert.True(sold > 0);
        Assert.Equal(200, N(h, "barrel||3"));
        Assert.Equal(0, N(h, "pot||1"));
        // Рівний за ціною — теж не міняємо: продати один горщик замість такого самого — те саме.
        Items(h, ("pot||1", 200));
        Put(h, "pot", 1, 5);
        Assert.Equal(200, N(h, "pot||1"));
    }

    [Fact]
    public void Only_what_is_cheaper_swaps_the_rest_of_the_pack_is_sold()
    {
        var h = Wheel();
        Items(h, ("pot||1", 3), ("barrel||4", 197));
        Put(h, "barrel", 3, 10);
        Assert.Equal(0, N(h, "pot||1"));
        Assert.Equal(3, N(h, "barrel||3"));
        Assert.Equal(197, N(h, "barrel||4"));
    }

    [Fact]
    public void Free_room_is_filled_first_and_cheapest_keys_go_first()
    {
        var h = Wheel();
        Items(h, ("bowl||1", 100), ("pot||1", 95));
        Put(h, "barrel", 3, 10);
        Assert.Equal(90, N(h, "pot||1"));          // 5 у вільне місце, 5 — замість горщиків (вони дешевші за миски)
        Assert.Equal(100, N(h, "bowl||1"));
        Assert.Equal(10, N(h, "barrel||3"));
    }

    /// <summary>Толока відкрита (гривня за весь час), криниця, етап 0: 15 горщиків і 15 мисок; наступний — 20 глечиків і 10 добрих макітр.</summary>
    static void Toloka(RoomHarness h, int stage = 0, bool laid = false, int gotPots = 0) => Patch(h, s =>
    {
        s["total"] = 1e16;
        var row = new JsonObject { ["built"] = new JsonArray(), ["stage"] = stage, ["helpers"] = new JsonArray() };
        if (laid) row["laidAt"] = h.Clock.UtcNow.ToString("O");
        if (gotPots > 0) row["got"] = new JsonObject { ["0"] = gotPots };
        s["toloka"] = row;
    });

    [Fact]
    public void Toloka_current_stage_and_next_stage_needs_are_kept()
    {
        var h = Wheel();
        Toloka(h);
        Items(h, ("pot||1", 20), ("bowl||1", 15), ("jug||1", 165));
        Put(h, "barrel", 3, 200);
        Assert.Equal(15, N(h, "pot||1"));          // етап 0: 15 горщиків
        Assert.Equal(15, N(h, "bowl||1"));         // і 15 мисок
        Assert.Equal(20, N(h, "jug||1"));          // наступний етап: 20 глечиків
        Assert.Equal(150, N(h, "barrel||3"));
    }

    [Fact]
    public void Toloka_keeps_only_what_is_still_missing_after_friends()
    {
        var h = Wheel();
        Toloka(h, gotPots: 10);
        Items(h, ("pot||1", 20), ("bowl||1", 180));
        Put(h, "barrel", 3, 200);
        Assert.Equal(5, N(h, "pot||1"));           // друзі піднесли 10 з 15 — тримаємо лише 5
        Assert.Equal(15, N(h, "bowl||1"));
    }

    [Fact]
    public void A_laid_stage_keeps_nothing_of_its_own_but_the_next_one_is_kept()
    {
        var h = Wheel();
        Toloka(h, laid: true);
        Items(h, ("pot||1", 20), ("bowl||1", 15), ("jug||1", 165));
        Put(h, "barrel", 3, 200);
        Assert.Equal(0, N(h, "pot||1"));
        Assert.Equal(0, N(h, "bowl||1"));
        Assert.Equal(20, N(h, "jug||1"));
    }

    [Fact]
    public void Toloka_keeps_the_worst_fitting_quality_better_ones_are_not_sold_for_it()
    {
        var h = Wheel();
        Toloka(h, stage: 2);                       // журавель: 10 добрих барил і 20 добрих глечиків
        Items(h, ("barrel||2", 4), ("barrel||3", 10), ("pot||1", 186));
        Put(h, "lion", 4, 200);
        Assert.Equal(4, N(h, "barrel||2"));
        Assert.Equal(6, N(h, "barrel||3"));        // 4 добрих + 6 дзвінких = 10 на етап; решта 4 дзвінких — дешевші за лева
        Assert.Equal(0, N(h, "pot||1"));
    }

    static string TierOf(string guest) => Clicker.Guests.First(g => g.Key == guest).Tier;

    [Fact]
    public void Guest_orders_are_kept_like_for_the_wagon()
    {
        var h = Wheel(1e23);
        Patch(h, s =>
        {
            s["guests"] = new JsonObject
            {
                ["met"] = new JsonObject { ["tsargrad"] = h.Clock.UtcNow.AddHours(-1).ToString("O") },
                ["rep"] = new JsonObject(),
                ["orders"] = new JsonArray(new JsonObject
                {
                    ["id"] = 1, ["guest"] = "tsargrad", ["who"] = 0, ["ware"] = "pot", ["style"] = "", ["quality"] = 1, ["count"] = 7,
                    ["at"] = h.Clock.UtcNow.ToString("O"), ["until"] = h.Clock.UtcNow.AddMinutes(200).ToString("O"),
                }),
                ["orderId"] = 100, ["next"] = h.Clock.UtcNow.AddMinutes(40).ToString("O"),
            };
            s["upgrades"]!.AsObject()[TierOf("tsargrad")] = 1;
            // Толока на 1e23 вже відкрита й теж тримала б горщики — тут усе збудовано, лишаються самі гості.
            s["toloka"] = new JsonObject { ["built"] = new JsonArray(Clicker.Buildings.Select(b => (JsonNode)b.Key).ToArray()) };
        });
        Items(h, ("pot||1", 200));
        Put(h, "barrel", 3, 300, guests: true);
        Assert.Equal(7, N(h, "pot||1"));
        Assert.Equal(100 + 193, N(h, "barrel||3"));   // кузня толоки дала комору на 300: 100 у вільне, 193 — замість горщиків
    }

    [Fact]
    public void A_store_full_of_kept_wares_sends_the_new_one_to_the_bazaar()
    {
        var h = Wheel();
        Toloka(h);
        Items(h, ("pot||1", 15), ("bowl||1", 15), ("jug||1", 20));
        Patch(h, s => s["craft"]!["items"]!["lion||4"] = 150);
        Put(h, "barrel", 3, 10);
        Assert.Equal(15, N(h, "pot||1"));
        Assert.Equal(20, N(h, "jug||1"));
        Assert.Equal(0, N(h, "barrel||3"));
    }

    [Fact]
    public void Big_packs_on_a_store_with_hundreds_of_keys_are_fast()
    {
        var h = Wheel(1e23);
        var bag = new List<(string, int)>();
        foreach (var w in Clicker.Wares.Take(14))
            foreach (var st in new[] { "", "gavarets", "kosiv", "opishnia", "petrykivka" })
                for (var q = 1; q <= 2; q++) bag.Add(($"{w.Key}|{st}|{q}", 1));
        Items(h, [.. bag]);
        Patch(h, s => s["craft"]!["items"]!["pot||1"] = 200 - bag.Count + 1);
        Assert.Equal(200, Total(h));
        Put(h, "pleskanets", 4, 10);              // прогрів
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++) Put(h, "pleskanets", 4, 100_000);
        sw.Stop();
        Assert.Equal(200, Total(h));
        Assert.True(N(h, "pleskanets||4") >= 150);   // решту тримають толока (вона вже відкрита) і майстерштук
        Assert.True(sw.ElapsedMilliseconds < 1000, $"{sw.ElapsedMilliseconds} мс на 20 пачок");
    }

    [Fact]
    public void The_helper_kiln_swaps_and_says_so_in_one_line()
    {
        var h = new RoomHarness("clicker");
        h.Solo("Оля");
        Patch(h, s =>
        {
            s["craft"]!["items"] = new JsonObject { ["pot||1"] = 200 };
            var rack = new JsonArray();
            for (var i = 0; i < 5; i++)
                rack.Add(new JsonObject { ["ware"] = "jug", ["clay"] = "", ["dryAt"] = h.Clock.UtcNow.AddMinutes(-1).ToString("O") });
            s["craft"]!["rack"] = rack;
        });
        var pot = Value(h, "pot||1");
        Assert.True(h.Act(0, "kiln", new { op = "light", helper = true }).Ok);
        h.Clock.Advance(30);
        var last = View(h).GetProperty("kiln").GetProperty("last");
        Assert.Equal(195, N(h, "pot||1"));
        Assert.Equal(200, Total(h));
        Assert.Equal(pot * 5, last.GetProperty("sold").GetDouble(), 6);
        Assert.StartsWith("звільнив місце: продав 5 дешевших — горщик звичайний ×5", last.GetProperty("freed").GetString());
    }

    [Fact]
    public void An_old_kiln_record_without_the_line_still_loads()
    {
        var h = Wheel();
        Patch(h, s => s["kiln"] = new JsonObject
        {
            ["last"] = new JsonObject { ["at"] = h.Clock.UtcNow.ToString("O"), ["helper"] = true, ["items"] = new JsonArray(), ["sold"] = 5 },
        });
        var last = View(h).GetProperty("kiln").GetProperty("last");
        Assert.Equal(JsonValueKind.Null, last.GetProperty("freed").ValueKind);
    }
}
