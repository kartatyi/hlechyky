using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Заморська торгівля (дванадцяте оновлення, docs/games/specs/clicker-v12.md §3–4): кораблі біля Пристані, Слава у світі
/// й заморські майстерні, які вона відмикає. Перший корабель відкриває першу майстерню одразу; вантаж не бере чужого
/// відкладеного; розкішні — лише в свої місця; плата й Слава — одразу, повний — з бонусом.
/// </summary>
public class ClickerTradeTests
{
    static RoomHarness Wheel(string nick = "Оля")
    {
        var h = new RoomHarness("clicker");
        h.Solo(nick);
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static JsonElement Trade(RoomHarness h) => View(h).GetProperty("trade");
    static List<JsonElement> Ships(RoomHarness h) => Trade(h).GetProperty("ships").EnumerateArray().ToList();
    static long Slava(RoomHarness h) => Trade(h).GetProperty("slava").GetInt64();
    static double Pots(RoomHarness h) => View(h).GetProperty("pots").GetDouble();
    static ActResult Load(RoomHarness h, int id, string what = "loadAll", int n = 1) => h.Act(0, "trade", new { @do = what, id, n });

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static string[] Upto(string key) => Clicker.Buildings.Select(b => b.Key).TakeWhile(k => k != key).Append(key).ToArray();

    /// <summary>Пристань стоїть, гроші й «за весь час» — з запасом (велика толока відкрита).</summary>
    static void Pier(RoomHarness h, double pots = 1e40) => Patch(h, s =>
    {
        s["pots"] = pots;
        s["total"] = Math.Max(pots, 1e40);
        s["toloka"] = new JsonObject { ["built"] = new JsonArray(Upto("pier").Select(x => (JsonNode)x).ToArray()) };
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

    /// <summary>Стан торгівлі просто в збереження: перший корабель уже був, Слава й кораблі — які треба.</summary>
    static void TradeRow(RoomHarness h, long slava = 0, params JsonObject[] ships) => Patch(h, s =>
    {
        var now = h.Clock.UtcNow;
        s["trade"] = new JsonObject
        {
            ["slava"] = slava,
            ["firstDone"] = true,
            ["next"] = now.AddMinutes(40).ToString("O"),
            ["shipId"] = ships.Length,
            ["ships"] = new JsonArray(ships.Select(x => (JsonNode)x).ToArray()),
        };
    });

    static JsonObject Ship(RoomHarness h, int id, string ware, int quality, int count, int lux = 0, string style = "") => new()
    {
        ["id"] = id, ["port"] = "delft", ["ware"] = ware, ["style"] = style, ["quality"] = quality, ["count"] = count, ["lux"] = lux,
        ["at"] = h.Clock.UtcNow.ToString("O"), ["until"] = h.Clock.UtcNow.AddHours(8).ToString("O"),
    };

    // ---------- відкриття ----------

    [Fact]
    public void Without_the_pier_there_is_no_trade()
    {
        var h = Wheel();
        Assert.Equal(JsonValueKind.Null, View(h).GetProperty("trade").ValueKind);
        var r = Load(h, 1);
        Assert.False(r.Ok);
        Assert.Contains("Пристань", r.Message);
    }

    [Fact]
    public void The_first_ship_waits_at_the_pier_and_opens_the_first_workshop_at_once()
    {
        var h = Wheel();
        Pier(h);
        // Глечики: горщиків і кахель чекає поточний етап Толоки (Горн-велетень) — корабель їх не бере.
        Items(h, ("jug||2", 30), ("bowl||1", 50));
        var ships = Ships(h);
        var first = Assert.Single(ships);
        Assert.True(first.GetProperty("first").GetBoolean());
        Assert.Equal(Clicker.TradeFirstCount, first.GetProperty("n").GetInt32());
        Assert.Equal("", first.GetProperty("ware").GetString());

        // До Слави майстерня закрита.
        var shut = h.Act(0, "buy", new { key = "athens" });
        Assert.False(shut.Ok);
        Assert.Contains("Слава", shut.Message);

        var r = Load(h, first.GetProperty("id").GetInt32());
        Assert.True(r.Ok, r.Message);
        Assert.Contains("відкрито", r.Message);
        Assert.DoesNotContain(Ships(h), s => s.GetProperty("first").GetBoolean());
        Assert.True(Slava(h) >= Clicker.SlavaGate["athens"]);
        Assert.Equal(10, Item(h, "jug||2"));          // звичайні (Q1) кораблеві не годяться
        Assert.Equal(50, Item(h, "bowl||1"));

        var bought = h.Act(0, "buy", new { key = "athens" });
        Assert.True(bought.Ok, bought.Message);
        Assert.True(h.Act(0, "look").Ok);
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-ship-first");
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-overseas-first");
    }

    [Fact]
    public void The_first_ship_asks_only_what_there_is_but_never_fewer_than_five()
    {
        var h = Wheel();
        Pier(h);
        Items(h, ("jug||2", 7));
        Assert.Equal(7, Ships(h).Single().GetProperty("n").GetInt32());
        var k = Wheel("Іра");
        Pier(k);
        Items(k, ("jug||2", 1));
        Assert.Equal(Clicker.TradeFirstMin, Ships(k).Single().GetProperty("n").GetInt32());
    }

    [Fact]
    public void An_overseas_workshop_opens_by_slava_not_by_the_tier_before_it()
    {
        var h = Wheel();
        Pier(h);
        TradeRow(h, slava: Clicker.SlavaGate["faenza"]);
        // Раку (за Фестивалем) нема — заморським це не заважає.
        Assert.True(h.Act(0, "buy", new { key = "athens" }).Ok);
        Assert.True(h.Act(0, "buy", new { key = "faenza" }).Ok);
        var r = h.Act(0, "buy", new { key = "azulejo" });
        Assert.False(r.Ok);
        Assert.Contains("Слава", r.Message);
        Assert.Equal("azulejo", Trade(h).GetProperty("gate").GetProperty("key").GetString());
    }

    // ---------- розклад ----------

    [Fact]
    public void Ships_come_every_forty_minutes_up_to_three_and_leave_after_eight_hours()
    {
        var h = Wheel();
        Pier(h);
        TradeRow(h);
        Assert.Empty(Ships(h));
        h.Clock.Advance(Clicker.TradeGap);
        Assert.Single(Ships(h));
        // Офлайн теж: за дві години — до трьох, не більше.
        h.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(Clicker.TradeShipsMax, Ships(h).Count);
        h.Clock.Advance(TimeSpan.FromHours(3));
        Assert.Equal(Clicker.TradeShipsMax, Ships(h).Count);
        // Кожен стоїть від своєї години прибуття: за добу всі старі відпливли, а нові — свіжі.
        h.Clock.Advance(TimeSpan.FromHours(24));
        var now = h.Clock.UtcNow;
        Assert.InRange(Ships(h).Count, 1, Clicker.TradeShipsMax);
        Assert.All(Ships(h), s => Assert.True(s.GetProperty("until").GetDateTimeOffset() > now));
    }

    // ---------- вантаж ----------

    [Fact]
    public void Loading_takes_the_worst_fitting_first_and_never_what_waits_for_the_toloka()
    {
        var h = Wheel();
        Pier(h);
        // Поточний етап Толоки (Горн-велетень, фундамент) чекає 100 дзвінких кахель — корабель їх не чіпає.
        TradeRow(h, 0, Ship(h, 1, "tile", 2, 30));
        Items(h, ("tile||3", 100), ("tile||2", 10));
        var r = Load(h, 1);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(0, Item(h, "tile||2"));
        Assert.Equal(100, Item(h, "tile||3"));
        Assert.Equal(10, Ships(h).Single().GetProperty("loaded").GetInt32());
        var again = Load(h, 1);
        Assert.False(again.Ok);
        Assert.Contains("відкладено", again.Message);
    }

    [Fact]
    public void Luxury_seats_take_only_luxurious_and_wait_for_them()
    {
        var h = Wheel();
        Pier(h);
        TradeRow(h, 0, Ship(h, 1, "jug", 3, 10, lux: 3));
        Items(h, ("jug||3", 20), ("jug||4", 2));
        Assert.True(Load(h, 1).Ok);
        var s = Ships(h).Single();
        Assert.Equal(9, s.GetProperty("loaded").GetInt32());
        Assert.Equal(2, s.GetProperty("loadedLux").GetInt32());
        Assert.Equal(13, Item(h, "jug||3"));
        var wait = Load(h, 1);
        Assert.False(wait.Ok);
        Assert.Contains("розкішних", wait.Message);
        Items(h, ("jug||3", 13), ("jug||4", 1));
        Assert.True(Load(h, 1).Ok);
        Assert.Empty(Ships(h));
    }

    [Fact]
    public void Each_load_pays_at_once_and_a_full_ship_adds_a_bonus()
    {
        var h = Wheel();
        Pier(h, pots: 0);
        TradeRow(h, 0, Ship(h, 1, "jug", 2, 10));
        Items(h, ("jug||2", 10));
        var start = Pots(h);
        Assert.True(Load(h, 1, "load", 4).Ok);
        var part = Pots(h) - start;
        Assert.True(part > 0);
        Assert.Equal(4 * Clicker.SlavaQ2, Slava(h));
        Assert.True(Load(h, 1).Ok);
        var all = Pots(h) - start;
        // Шість виробів за ту саму плату за штуку плюс 10 % за повний корабель — більше, ніж 6/4 частини.
        Assert.True(all > part * 10 / 4 * 1.05, $"{all} проти {part}");
        Assert.Equal(10 * Clicker.SlavaQ2 + (10 * Clicker.SlavaQ2 * Clicker.SlavaFullPct + 99) / 100, Slava(h));
    }

    [Fact]
    public void A_world_style_earns_more_slava_and_pay()
    {
        Assert.Equal(Clicker.SlavaQ3 + Clicker.SlavaStyle, SlavaOf(3, "delft"));
        Assert.Equal(Clicker.SlavaQ3, SlavaOf(3, "kosiv"));
        Assert.Equal(Clicker.SlavaQ4 + Clicker.SlavaStyle, SlavaOf(4, "athens"));
    }

    static int SlavaOf(int q, string style) =>
        (q >= 4 ? Clicker.SlavaQ4 : q >= 3 ? Clicker.SlavaQ3 : Clicker.SlavaQ2)
        + (Clicker.MasterTiers.Contains(style) || Clicker.OverseasTiers.Contains(style) ? Clicker.SlavaStyle : 0);

    // ---------- збереження ----------

    [Fact]
    public void Ships_and_slava_survive_save_and_load_and_the_firing()
    {
        var h = Wheel();
        Pier(h);
        TradeRow(h, 123, Ship(h, 5, "jug", 2, 10));
        Items(h, ("jug||2", 3));
        Assert.True(Load(h, 5).Ok);
        Patch(h, _ => { });
        Assert.Equal(123 + 3 * Clicker.SlavaQ2, Slava(h));
        var s = Ships(h).Single();
        Assert.Equal(5, s.GetProperty("id").GetInt32());
        Assert.Equal(3, s.GetProperty("loaded").GetInt32());
    }

    // ---------- «Що нового» v12 ----------

    [Fact]
    public void The_v12_news_gives_half_an_hour_of_play_once_and_a_newcomer_has_it_taken()
    {
        var h = Wheel();
        Pier(h, pots: 0);
        Patch(h, s => { s["news"] = "v11.2"; s["titles"] = new JsonObject { ["gifts"] = new JsonArray("v9.2", Clicker.GiftV10Key, Clicker.GiftV11Key) }; });
        Assert.Equal("v12", View(h).GetProperty("news").GetString());
        var before = Pots(h);
        var r = h.Act(0, "news", new { v = Clicker.NewsVersion });
        Assert.True(r.Ok, r.Message);
        Assert.Contains("Заморська торгівля", r.Message);
        Assert.True(Pots(h) > before);
        Assert.Equal(JsonValueKind.Null, View(h).GetProperty("news").ValueKind);

        // Новачкові подарунок v12 «уже забраний» — він грає вже з кораблями.
        var k = Wheel("Новачок");
        Assert.Contains(Clicker.GiftV12Key, JsonNode.Parse(k.Room.Game.Save()!)!["titles"]!["gifts"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    // ---------- розписи ----------

    [Fact]
    public void Overseas_styles_stay_out_of_the_album_and_need_their_workshop()
    {
        foreach (var k in Clicker.OverseasTiers) Assert.Equal(-1, Clicker.AlbumStyleIndex(k));
        Assert.Equal(Clicker.AlbumStyles, Clicker.AlbumStyleIndex("raku"));
        Assert.All(Clicker.OverseasTiers, k => Assert.Equal(k, Clicker.Styles.Single(s => s.Key == k).Tier));
    }
}
