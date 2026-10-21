using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// v12, пакет D1 «Ремесло»: два поля виду для клієнта. kept у рядку комори — скільки штук чекають толока, замовлення
/// й майстерштук (🔒 «Толока 3 · замовлення 2»), той самий розподіл, що й у ладу (LadKept). lux в останньому горні —
/// шанс розкішного на виріб у тій партії («шанс розкішних ~14 %»).
/// </summary>
public class ClickerV12CraftViewTests
{
    static RoomHarness Wheel(double total = 1e9)
    {
        var h = new RoomHarness("clicker");
        h.Solo("Оля");
        Patch(h, s => { s["total"] = total; s["pots"] = 0; });
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
        h.Clock.Advance(0.1);                      // вид кешується на 50 мс
    }

    static void Items(RoomHarness h, params (string Key, int N)[] items) => Patch(h, s =>
    {
        var bag = new JsonObject();
        foreach (var (key, n) in items) bag[key] = n;
        s["craft"]!["items"] = bag;
    });

    /// <summary>Сільські й заморські замовники, що прийшли самі (Sync), — геть: перевіряємо одну вимогу за раз.</summary>
    static void NoOrders(RoomHarness h)
    {
        lock (h.Room.Sync)
        {
            foreach (var f in new[] { "_mktOrders", "_gOrders" })
                ((System.Collections.IList)typeof(Clicker).GetField(f, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(h.Room.Game)!).Clear();
        }
        h.Clock.Advance(0.1);
    }

    static JsonElement Item(RoomHarness h, string key) =>
        h.View(0).GetProperty("craft").GetProperty("items").EnumerateArray().First(x => x.GetProperty("key").GetString() == key);

    static int[]? Kept(RoomHarness h, string key)
    {
        var k = Item(h, key).GetProperty("kept");
        return k.ValueKind == JsonValueKind.Null ? null : k.EnumerateArray().Select(x => x.GetInt32()).ToArray();
    }

    [Fact]
    public void Toloka_holds_show_up_as_kept_in_the_store_row_and_free_wares_have_none()
    {
        var h = Wheel();
        // Криниця, етап 0: 15 горщиків і 15 мисок; наступний — 20 глечиків і 10 добрих макітр.
        Patch(h, s => { s["total"] = 1e16; s["toloka"] = new JsonObject { ["built"] = new JsonArray(), ["stage"] = 0, ["helpers"] = new JsonArray() }; });
        Items(h, ("pot||1", 20), ("jug||1", 30), ("barrel||3", 5));
        NoOrders(h);
        Assert.Equal([15, 0, 0], Kept(h, "pot||1"));
        Assert.Equal([20, 0, 0], Kept(h, "jug||1"));
        Assert.Null(Kept(h, "barrel||3"));
        Assert.Equal(20, Item(h, "pot||1").GetProperty("n").GetInt32());   // відкладене лишається в n — це лише позначка
    }

    [Fact]
    public void Guest_orders_are_kept_as_orders_not_as_toloka()
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
            s["upgrades"]!.AsObject()[Clicker.Guests.First(g => g.Key == "tsargrad").Tier] = 1;
            s["toloka"] = new JsonObject { ["built"] = new JsonArray(Clicker.Buildings.Select(b => (JsonNode)b.Key).ToArray()) };
        });
        Items(h, ("pot||1", 30));
        lock (h.Room.Sync)
            ((System.Collections.IList)typeof(Clicker).GetField("_mktOrders", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(h.Room.Game)!).Clear();
        h.Clock.Advance(0.1);
        var kept = Kept(h, "pot||1");
        Assert.NotNull(kept);
        Assert.Equal(0, kept![0]);
        Assert.Equal(7, kept[1]);
    }

    static readonly MethodInfo Finish = typeof(Clicker).GetMethod("KilnFinish", BindingFlags.NonPublic | BindingFlags.Instance)!;

    static double Lux(RoomHarness h, int beauty, double heat, bool manual)
    {
        Patch(h, s => s["kiln"] = new JsonObject { ["batch"] = new JsonArray("pot", "pot", "pot"), ["beauty"] = beauty });
        lock (h.Room.Sync) Finish.Invoke(h.Room.Game, [h.Clock.UtcNow, heat, 0.0, manual, 0.0]);
        h.Clock.Advance(0.1);
        return h.View(0).GetProperty("kiln").GetProperty("last").GetProperty("lux").GetDouble();
    }

    [Fact]
    public void The_last_kiln_tells_the_lux_chance_of_its_batch()
    {
        var h = Wheel();
        Assert.Equal(KilnHeat.LuxShare, Lux(h, 100, 1.0, manual: true), 3);   // ідеальний жар і краса 100 — чверть партії
        Assert.Equal(0.01, Lux(h, 50, 0.5, manual: true), 3);               // S = 0,4; p4 = 0,25 · 0,16 · 0,25
        Assert.Equal(0, Lux(h, 100, 1.0, manual: false));                     // підмайстер пече без розпису
    }

    [Fact]
    public void An_old_last_kiln_without_lux_still_loads()
    {
        var h = Wheel();
        Patch(h, s => s["kiln"] = new JsonObject
        {
            ["last"] = new JsonObject
            {
                ["at"] = h.Clock.UtcNow.ToString("O"), ["helper"] = false, ["heat"] = 80, ["over"] = 0, ["beauty"] = 40, ["style"] = "",
                ["straw"] = false, ["items"] = new JsonArray(new JsonObject { ["ware"] = "pot", ["q"] = 2 }), ["shards"] = 0, ["sold"] = 0,
            },
        });
        var last = h.View(0).GetProperty("kiln").GetProperty("last");
        Assert.Equal(0, last.GetProperty("lux").GetDouble());
        Assert.Equal(80, last.GetProperty("heat").GetInt32());
    }
}
