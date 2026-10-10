using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Заморські гості — «Гостинний двір» (пакет C десятого оновлення, ClickerGuests.cs, docs/games/specs/clicker-v10.md §7):
/// прибуття з першим рівнем щабля, місця на дворі, розклад і «поки тебе нема», вибір гостя й виробу, віддати й
/// відпустити, шана з рівнями й пільги кожного гостя, збереження, обпал і ачівки.
/// </summary>
public class ClickerGuestsTests
{
    static readonly DateTimeOffset Thursday = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    static RoomHarness Wheel(int seed = 1)
    {
        var h = new RoomHarness("clicker", seed: seed);
        h.Clock.UtcNow = Thursday;
        h.Solo("Оля");
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static JsonElement Yard(RoomHarness h) => View(h).GetProperty("guests");
    static List<JsonElement> Orders(RoomHarness h) => Yard(h).GetProperty("orders").EnumerateArray().ToList();
    static double Pots(RoomHarness h) => View(h).GetProperty("pots").GetDouble();
    static ActResult Guests(RoomHarness h, object payload) => h.Act(0, "guests", payload);
    static ActResult Give(RoomHarness h, int id) => Guests(h, new { @do = "give", id });
    static ActResult Skip(RoomHarness h, int id) => Guests(h, new { @do = "skip", id });

    static string TierOf(string guest) => Clicker.Guests.First(g => g.Key == guest).Tier;

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    /// <summary>Пізня гра: за весь час наліплено на всі п'ятнадцять виробів (лев — з трильйона).</summary>
    static void Late(RoomHarness h, double total = 1e23) => Patch(h, s => s["total"] = total);

    /// <summary>
    /// Двір рівно такий: ці гості вже прибули (і їхні щаблі мають рівень — щоб ніхто не прибув зайвий), їхня шана,
    /// замовлення й коли наступне (0 — уже час).
    /// </summary>
    static void SetYard(RoomHarness h, string[] met, (string Key, int Pts)[]? rep = null, JsonObject[]? orders = null,
        double nextMinutes = 40, int delivered = 0) => Patch(h, s =>
    {
        var m = new JsonObject();
        foreach (var k in met) m[k] = h.Clock.UtcNow.AddHours(-1).ToString("O");
        var r = new JsonObject();
        foreach (var (k, p) in rep ?? []) r[k] = p;
        s["guests"] = new JsonObject
        {
            ["met"] = m, ["rep"] = r,
            ["orders"] = new JsonArray((orders ?? []).Select(o => (JsonNode)o).ToArray()),
            ["orderId"] = 100, ["next"] = h.Clock.UtcNow.AddMinutes(nextMinutes).ToString("O"), ["delivered"] = delivered,
        };
        var ups = s["upgrades"]!.AsObject();
        foreach (var k in met) ups[TierOf(k)] = 1;
    });

    static JsonObject Order(RoomHarness h, int id, string guest, string ware, int quality, int count, string style = "",
        int minutes = 200, int who = 0) => new()
    {
        ["id"] = id, ["guest"] = guest, ["who"] = who, ["ware"] = ware, ["style"] = style, ["quality"] = quality,
        ["count"] = count, ["at"] = h.Clock.UtcNow.ToString("O"), ["until"] = h.Clock.UtcNow.AddMinutes(minutes).ToString("O"),
    };

    static void Items(RoomHarness h, params (string Key, int N)[] items) => Patch(h, s =>
    {
        var bag = new JsonObject();
        foreach (var (key, n) in items) bag[key] = n;
        s["craft"]!["items"] = bag;
    });

    static double ItemValue(RoomHarness h, string key) =>
        View(h).GetProperty("craft").GetProperty("items").EnumerateArray()
            .First(x => x.GetProperty("key").GetString() == key).GetProperty("value").GetDouble();

    static int ItemN(RoomHarness h, string key) =>
        View(h).GetProperty("craft").GetProperty("items").EnumerateArray()
            .Where(x => x.GetProperty("key").GetString() == key).Select(x => x.GetProperty("n").GetInt32()).FirstOrDefault();

    static JsonElement GuestView(RoomHarness h, string key) =>
        Yard(h).GetProperty("list").EnumerateArray().First(g => g.GetProperty("key").GetString() == key);

    static int Rep(RoomHarness h, string key) => GuestView(h, key).GetProperty("pts").GetInt32();

    static List<string> AwayNotes(RoomHarness h) =>
        View(h).GetProperty("away") is { ValueKind: JsonValueKind.Object } a
            ? a.GetProperty("notes").EnumerateArray().Select(x => x.GetString()!).ToList()
            : [];

    static T Hook<T>(RoomHarness h, string name)
    {
        lock (h.Room.Sync)
            return (T)typeof(Clicker).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(h.Room.Game)!;
    }

    // ---------- прибуття ----------

    [Fact]
    public void A_new_wheel_has_no_guests_and_everything_they_give_is_neutral()
    {
        var h = Wheel();
        var v = View(h);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("guests").ValueKind);
        Assert.Equal(Clicker.FairMult, v.GetProperty("fair").GetProperty("mult").GetDouble());
        Assert.Equal(Clicker.OfflineCap.TotalHours, v.GetProperty("offlineHours").GetDouble());
        Assert.Equal(1.0, Hook<double>(h, "GuestsAllMult"));
        Assert.Equal(1.0, Hook<double>(h, "GuestsGoldenWait"));
        Assert.Null(JsonNode.Parse(h.Room.Game.Save()!)!["guests"]);
    }

    [Fact]
    public void The_first_level_of_the_port_brings_the_tsargrad_merchants_with_an_order()
    {
        var h = Wheel();
        Late(h);
        Patch(h, s => s["upgrades"]!["port"] = 1);
        var yard = Yard(h);
        var list = yard.GetProperty("list").EnumerateArray().ToList();
        Assert.Single(list);
        Assert.Equal("tsargrad", list[0].GetProperty("key").GetString());
        Assert.Equal(0, list[0].GetProperty("level").GetInt32());
        Assert.Equal(h.Clock.UtcNow, list[0].GetProperty("at").GetDateTimeOffset());
        // Корабель зайшов не просто так: одразу й замовлення, з улюблених царградських виробів.
        var order = Assert.Single(yard.GetProperty("orders").EnumerateArray());
        Assert.Equal("tsargrad", order.GetProperty("guest").GetString());
        Assert.Contains(order.GetProperty("ware").GetString(), new[] { "jug", "kumanets", "candle" });
        Assert.Equal(2, order.GetProperty("q").GetInt32());
        Assert.Equal(1, yard.GetProperty("slots").GetInt32());
        var next = yard.GetProperty("next").GetDateTimeOffset() - h.Clock.UtcNow;
        Assert.InRange(next.TotalMinutes, Clicker.GuestGapMinMinutes, Clicker.GuestGapMaxMinutes);
    }

    [Fact]
    public void Only_the_six_guest_tiers_bring_guests_each_its_own()
    {
        var h = Wheel();
        Late(h);
        Patch(h, s =>
        {
            foreach (var t in Clicker.WorldTiers.Except(Clicker.GuestTiers)) s["upgrades"]![t] = 3;
        });
        Assert.Equal(JsonValueKind.Null, View(h).GetProperty("guests").ValueKind);

        Patch(h, s =>
        {
            foreach (var t in Clicker.GuestTiers) s["upgrades"]![t] = 1;
        });
        var keys = Yard(h).GetProperty("list").EnumerateArray().Select(g => g.GetProperty("key").GetString()!).ToArray();
        Assert.Equal(new[] { "tsargrad", "canton", "diaspora", "london", "paris", "masters" }, keys);
        Assert.Equal(Clicker.GuestTiers, Clicker.Guests.Select(g => g.Tier).ToArray());
        // Шість кораблів разом — але нове замовлення за раз одне.
        Assert.Single(Orders(h));
    }

    [Fact]
    public void The_arrival_goes_into_the_away_notes_when_it_happened_while_you_were_away()
    {
        var h = Wheel();
        Late(h);
        Patch(h, s =>
        {
            s["upgrades"]!["port"] = 1;
            s["lastSync"] = h.Clock.UtcNow.AddHours(-1).ToString("O");
        });
        Assert.Contains(AwayNotes(h), n => n.StartsWith("⚓ До Одеського порту зайшов корабель"));
    }

    [Fact]
    public void Guests_stay_after_firing_and_do_not_arrive_twice()
    {
        var h = Wheel();
        Late(h);
        Patch(h, s => s["upgrades"]!["port"] = 1);
        var at = GuestView(h, "tsargrad").GetProperty("at").GetDateTimeOffset();
        Patch(h, s => s["guests"]!["rep"] = new JsonObject { ["tsargrad"] = 40 });
        var order = Orders(h).Single().GetProperty("id").GetInt32();

        h.Clock.Advance(60);
        Assert.True(h.Act(0, "fire").Ok);
        var v = View(h);
        Assert.Equal(0, v.GetProperty("upgrades").GetProperty("port").GetProperty("level").GetInt32());
        Assert.Equal(40, Rep(h, "tsargrad"));
        Assert.Equal(at, GuestView(h, "tsargrad").GetProperty("at").GetDateTimeOffset());
        Assert.Equal(order, Orders(h).Single().GetProperty("id").GetInt32());

        // Знову купив порт — купці вже тут, другого корабля й другого «привітального» замовлення нема.
        Patch(h, s => s["upgrades"]!["port"] = 1);
        Assert.Single(Yard(h).GetProperty("list").EnumerateArray());
        Assert.Single(Orders(h));
    }

    // ---------- двір і розклад ----------

    [Fact]
    public void The_yard_holds_one_order_per_guest_but_no_more_than_three()
    {
        foreach (var (met, max) in new[] { (1, 1), (2, 2), (4, 3), (6, 3) })
        {
            var h = Wheel();
            Late(h);
            SetYard(h, Clicker.Guests.Take(met).Select(g => g.Key).ToArray(), nextMinutes: 0);
            var seen = 0;
            for (var step = 0; step < 10; step++)
            {
                var n = Orders(h).Count;
                Assert.True(n <= max, $"{met} гостей: на дворі {n} замовлень, а місць {max}");
                seen = Math.Max(seen, n);
                Assert.Equal(max, Yard(h).GetProperty("slots").GetInt32());
                h.Clock.Advance(TimeSpan.FromMinutes(41));
            }
            Assert.Equal(max, seen);
        }
    }

    [Fact]
    public void A_new_order_comes_in_twenty_to_forty_minutes_and_waits_three_to_six_hours()
    {
        var h = Wheel();
        Late(h);
        SetYard(h, ["tsargrad", "canton"], nextMinutes: 25);
        h.Clock.Advance(TimeSpan.FromMinutes(24));
        Assert.Empty(Orders(h));
        h.Clock.Advance(TimeSpan.FromMinutes(2));
        var order = Assert.Single(Orders(h));
        var life = order.GetProperty("until").GetDateTimeOffset() - h.Clock.UtcNow;
        Assert.InRange(life.TotalMinutes, Clicker.GuestLifeMinMinutes, Clicker.GuestLifeMaxMinutes);
        var next = Yard(h).GetProperty("next").GetDateTimeOffset() - h.Clock.UtcNow;
        Assert.InRange(next.TotalMinutes, Clicker.GuestGapMinMinutes, Clicker.GuestGapMaxMinutes);
    }

    [Fact]
    public void Coming_back_after_a_day_finds_one_fresh_guest_not_a_queue()
    {
        var h = Wheel();
        Late(h);
        SetYard(h, ["tsargrad", "canton", "diaspora", "london"], nextMinutes: 20);
        h.Clock.Advance(TimeSpan.FromHours(24));
        Assert.Single(Orders(h));
        Assert.Contains(AwayNotes(h), n => n.StartsWith("🏛") && n.Contains("чекає в Гостинному дворі"));
        var next = Yard(h).GetProperty("next").GetDateTimeOffset() - h.Clock.UtcNow;
        Assert.InRange(next.TotalMinutes, Clicker.GuestGapMinMinutes, Clicker.GuestGapMaxMinutes);
    }

    [Fact]
    public void Orders_that_waited_too_long_leave_with_a_note()
    {
        var h = Wheel();
        Late(h);
        SetYard(h, ["tsargrad"], orders: [Order(h, 7, "tsargrad", "jug", 2, 1, minutes: 10)], nextMinutes: 40);
        h.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Empty(Orders(h));
        Assert.Contains(AwayNotes(h), n => n.Contains("не дочекався свого замовлення"));
    }

    // ---------- хто й що замовляє ----------

    [Fact]
    public void A_guest_without_an_order_on_the_yard_comes_first()
    {
        var h = Wheel();
        Late(h);
        SetYard(h, ["tsargrad", "canton"], rep: [("canton", 750)], orders: [Order(h, 7, "tsargrad", "jug", 2, 1)], nextMinutes: 0);
        var fresh = Orders(h).Single(o => o.GetProperty("id").GetInt32() != 7);
        Assert.Equal("canton", fresh.GetProperty("guest").GetString());
    }

    [Fact]
    public void Less_respected_guests_come_more_often()
    {
        var h = Wheel(seed: 11);
        Late(h);
        SetYard(h, ["tsargrad", "canton"], rep: [("tsargrad", 750)], nextMinutes: 0);
        var count = new Dictionary<string, int> { ["tsargrad"] = 0, ["canton"] = 0 };
        for (var i = 0; i < 60; i++)
        {
            var order = Assert.Single(Orders(h));
            count[order.GetProperty("guest").GetString()!]++;
            Assert.True(Skip(h, order.GetProperty("id").GetInt32()).Ok);
            h.Clock.Advance(TimeSpan.FromMinutes(41));
        }
        // Вага 11 − рівень: новенький гість приходить у одинадцять разів частіше за того, чия шана на вершині.
        Assert.True(count["canton"] >= 40, $"кантонських {count["canton"]} із 60");
        Assert.True(count["canton"] > 3 * count["tsargrad"], $"кантонських {count["canton"]}, царградських {count["tsargrad"]}");
    }

    [Fact]
    public void Wares_come_from_the_open_favourites_or_else_from_anything_open()
    {
        for (var seed = 1; seed <= 6; seed++)
        {
            // На двох тисячах глеків відкриті лише горщик, миска й глечик — жодної лондонської улюбленої.
            var h = Wheel(seed);
            Late(h, total: 2_000);
            Patch(h, s => s["upgrades"]!["exchange"] = 1);
            Assert.Contains(Orders(h).Single().GetProperty("ware").GetString(), new[] { "pot", "bowl", "jug" });

            var rich = Wheel(seed);
            Late(rich);
            Patch(rich, s => s["upgrades"]!["exchange"] = 1);
            Assert.Contains(Orders(rich).Single().GetProperty("ware").GetString(), new[] { "tykva", "kukhol", "whistle" });
        }
    }

    [Fact]
    public void Count_quality_and_painting_follow_each_guest()
    {
        foreach (var g in Clicker.Guests)
        {
            var h = Wheel(seed: 5);
            Late(h);
            Patch(h, s => s["styles"] = new JsonArray("kosiv", "opishnia"));
            SetYard(h, [g.Key], nextMinutes: 0);
            var counts = new HashSet<int>();
            var wares = new HashSet<string>();
            var styled = 0;
            const int Total = 60;
            for (var i = 0; i < Total; i++)
            {
                var o = Assert.Single(Orders(h));
                Assert.Equal(g.Key, o.GetProperty("guest").GetString());
                Assert.Equal(g.Quality, o.GetProperty("q").GetInt32());
                var n = o.GetProperty("n").GetInt32();
                Assert.InRange(n, g.CountMin, g.CountMax);
                counts.Add(n);
                var ware = o.GetProperty("ware").GetString()!;
                wares.Add(ware);
                if (g.Likes.Length > 0) Assert.Contains(ware, g.Likes);
                var style = o.GetProperty("style").GetString()!;
                if (style.Length > 0)
                {
                    styled++;
                    Assert.Contains(style, new[] { "kosiv", "opishnia" });
                }
                Assert.True(Skip(h, o.GetProperty("id").GetInt32()).Ok);
                h.Clock.Advance(TimeSpan.FromMinutes(41));
            }
            Assert.Equal(g.CountMax - g.CountMin + 1, counts.Count);
            if (g.Key == "masters") Assert.True(wares.Count >= 8, $"гончарі світу замовляли лише {wares.Count} виробів");
            // Розпис: паризьким — 40 %, решті — 15 %.
            if (g.Key == "paris") Assert.InRange(styled, 14, 36);
            else Assert.InRange(styled, 2, 18);
        }
    }

    [Fact]
    public void Without_bought_paintings_nobody_asks_for_one()
    {
        var h = Wheel(seed: 3);
        Late(h);
        SetYard(h, ["paris"], nextMinutes: 0);
        for (var i = 0; i < 25; i++)
        {
            var o = Assert.Single(Orders(h));
            Assert.Equal("", o.GetProperty("style").GetString());
            Assert.True(Skip(h, o.GetProperty("id").GetInt32()).Ok);
            h.Clock.Advance(TimeSpan.FromMinutes(41));
        }
    }

    // ---------- віддати й відпустити ----------

    [Fact]
    public void Giving_takes_the_wares_pays_eight_times_their_value_and_adds_respect()
    {
        var h = Wheel();
        Late(h);
        SetYard(h, ["tsargrad"], orders: [Order(h, 7, "tsargrad", "kumanets", 2, 2)]);
        Items(h, ("kumanets||2", 3), ("kumanets||1", 5));
        var value = ItemValue(h, "kumanets||2");
        var promised = Orders(h).Single().GetProperty("pay").GetDouble();
        Assert.Equal(Math.Floor(value * 2 * 8), promised);
        var before = Pots(h);

        var r = Give(h, 7);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(before + Math.Floor(value * 2 * 8), Pots(h));
        Assert.Equal(1, ItemN(h, "kumanets||2"));
        Assert.Equal(5, ItemN(h, "kumanets||1"));
        Assert.Empty(Orders(h));
        // Шана: 4 + 2·2 + 2·(2−2) = 8.
        Assert.Equal(8, Rep(h, "tsargrad"));
        Assert.StartsWith("🕌 Купець Мехмед із Царграда: «", r.Message);
        Assert.Contains("· шана +8", r.Message);
        Assert.Equal(1, Yard(h).GetProperty("delivered").GetInt32());
    }

    [Fact]
    public void The_world_s_potters_pay_twelve_times()
    {
        var h = Wheel();
        Late(h);
        SetYard(h, ["masters"], orders: [Order(h, 7, "masters", "lion", 4, 1, style: "kosiv")]);
        Items(h, ("lion|kosiv|4", 1));
        var value = ItemValue(h, "lion|kosiv|4");
        var before = Pots(h);
        var r = Give(h, 7);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(before + Math.Floor(value * 12), Pots(h));
        // 4 + 2·1 + 2·(4−2) + 3 за розпис = 13.
        Assert.Equal(13, Rep(h, "masters"));
    }

    [Fact]
    public void A_better_ware_than_asked_is_paid_as_what_it_is()
    {
        var h = Wheel();
        Late(h);
        SetYard(h, ["tsargrad"], orders: [Order(h, 7, "tsargrad", "jug", 2, 2)]);
        Items(h, ("jug||3", 2));
        var value = ItemValue(h, "jug||3");
        var before = Pots(h);
        // З 10.10 гість платить більше з двох: ціну виробів чи хвилини гри (GuestPlayPerWare за виріб на одиницю множника).
        var play = ClickerPlay.Pay(h, Clicker.GuestPlayPerWare * 2 * 8);
        Assert.True(Give(h, 7).Ok);
        Assert.Equal(before + Math.Max(Math.Floor(value * 2 * 8), play), Pots(h));
    }

    [Fact]
    public void Respect_raises_the_pay_by_a_tenth_a_level()
    {
        var h = Wheel();
        Late(h);
        SetYard(h, ["tsargrad"], rep: [("tsargrad", 70)], orders: [Order(h, 7, "tsargrad", "candle", 2, 2)]);
        Items(h, ("candle||2", 2));
        Assert.Equal(4, GuestView(h, "tsargrad").GetProperty("level").GetInt32());
        var mult = 8 * (1 + 0.1 * 4);
        Assert.Equal(mult, Orders(h).Single().GetProperty("mult").GetDouble(), 9);
        var value = ItemValue(h, "candle||2");
        var before = Pots(h);
        var play = ClickerPlay.Pay(h, Clicker.GuestPlayPerWare * 2 * mult);
        Assert.True(Give(h, 7).Ok);
        Assert.Equal(before + Math.Max(Math.Floor(value * 2 * mult), play), Pots(h));
    }

    [Fact]
    public void Not_enough_wares_is_a_clear_refusal_and_takes_nothing()
    {
        var h = Wheel();
        Late(h);
        SetYard(h, ["tsargrad", "london", "masters"], orders:
        [
            Order(h, 7, "tsargrad", "kumanets", 3, 3, style: "kosiv"),
            Order(h, 8, "london", "tykva", 2, 2),
            Order(h, 9, "masters", "lion", 4, 1),
        ]);
        // Добрий розписний і дзвінкий без розпису — не те, що просили; дзвінкий косівський — один.
        Items(h, ("kumanets|kosiv|3", 1), ("kumanets||3", 5), ("kumanets|kosiv|2", 4), ("lion||3", 2));
        var before = Pots(h);

        var r = Give(h, 7);
        Assert.False(r.Ok);
        Assert.Equal("Треба ще 2 × куманець (дзвінкий чи кращий, «Косівська») — є 1 з 3", r.Message);
        Assert.Equal("Треба ще 2 × тиква (добра чи краща) — є 0 з 2", Give(h, 8).Message);
        Assert.Equal("Треба ще 1 × лев-посудина (лише розкішний) — є 0 з 1", Give(h, 9).Message);

        Assert.Equal(before, Pots(h));
        Assert.Equal(1, ItemN(h, "kumanets|kosiv|3"));
        Assert.Equal(5, ItemN(h, "kumanets||3"));
        Assert.Equal(4, ItemN(h, "kumanets|kosiv|2"));
        Assert.Equal(3, Orders(h).Count);
        Assert.Equal(0, Rep(h, "tsargrad"));
    }

    [Fact]
    public void Letting_a_guest_go_costs_nothing()
    {
        var h = Wheel();
        Late(h);
        SetYard(h, ["tsargrad"], rep: [("tsargrad", 20)], orders: [Order(h, 7, "tsargrad", "jug", 2, 1)]);
        Items(h, ("jug||2", 1));
        var before = Pots(h);
        var r = Skip(h, 7);
        Assert.True(r.Ok);
        Assert.Contains("Іншим разом", r.Message);
        Assert.Empty(Orders(h));
        Assert.Equal(20, Rep(h, "tsargrad"));
        Assert.Equal(before, Pots(h));
        Assert.Equal(1, ItemN(h, "jug||2"));
    }

    [Fact]
    public void Unknown_or_gone_orders_are_refused()
    {
        var h = Wheel();
        Assert.False(Give(h, 1).Ok);                            // гостей ще нема зовсім
        Late(h);
        SetYard(h, ["tsargrad"], orders: [Order(h, 7, "tsargrad", "jug", 2, 1, minutes: 5)]);
        Items(h, ("jug||2", 1));
        Assert.False(Give(h, 99).Ok);
        Assert.False(Skip(h, 99).Ok);
        Assert.False(Guests(h, new { @do = "dance", id = 7 }).Ok);
        h.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.False(Give(h, 7).Ok);
        Assert.Equal(1, ItemN(h, "jug||2"));
    }

    // ---------- шана й пільги ----------

    [Fact]
    public void Respect_levels_follow_the_thresholds()
    {
        Assert.Equal(0, Clicker.GuestLevelOf(0));
        Assert.Equal(0, Clicker.GuestLevelOf(4));
        Assert.Equal(1, Clicker.GuestLevelOf(5));
        Assert.Equal(5, Clicker.GuestLevelOf(120));
        Assert.Equal(9, Clicker.GuestLevelOf(479));
        Assert.Equal(10, Clicker.GuestLevelOf(480));
        Assert.Equal(10, Clicker.GuestLevelOf(1_000_000));
        Assert.Equal(new[] { 0, 5, 15, 35, 70, 120, 190, 260, 330, 400, 480 }, Clicker.GuestRepLevels);
    }

    [Fact]
    public void Every_level_of_anyone_is_three_percent_to_everything()
    {
        var h = Wheel();
        Late(h);
        var all = Clicker.Guests.Select(g => g.Key).ToArray();
        SetYard(h, all);
        var before = View(h).GetProperty("allMult").GetDouble();
        SetYard(h, all, rep: [("tsargrad", 120), ("canton", 35), ("masters", 5)]);
        var after = View(h).GetProperty("allMult").GetDouble();
        Assert.Equal(1 + 0.03 * (5 + 3 + 1), after / before, 9);
        Assert.Equal(1.27, Yard(h).GetProperty("allMult").GetDouble(), 9);

        // Усі шість на десятому — ×2,8.
        SetYard(h, all, rep: all.Select(k => (k, 750)).ToArray());
        Assert.Equal(2.8, View(h).GetProperty("allMult").GetDouble() / before, 9);
    }

    [Fact]
    public void The_hooks_follow_each_guest_s_level()
    {
        var h = Wheel();
        Late(h);
        SetYard(h, Clicker.Guests.Select(g => g.Key).ToArray(),
            rep: [("tsargrad", 750), ("canton", 750), ("diaspora", 120), ("london", 120), ("paris", 35), ("masters", 5)]);
        Assert.Equal(0.5, Hook<double>(h, "GuestsValueBonus"), 12);
        Assert.Equal(0.7, Hook<double>(h, "GuestsGoldenWait"), 12);
        Assert.Equal(0.25, Hook<double>(h, "GuestsPayBonus"), 12);
        Assert.Equal(1.5, Hook<double>(h, "GuestsFairBonus"), 12);
        Assert.Equal(TimeSpan.FromMinutes(60), Hook<TimeSpan>(h, "GuestsOfflineExtra"));
        Assert.Equal(0.05, Hook<double>(h, "GuestsFallBonus"), 12);
        Assert.Equal(1 + 0.03 * (10 + 10 + 5 + 5 + 3 + 1), Hook<double>(h, "GuestsAllMult"), 12);
    }

    [Fact]
    public void Tsargrad_merchants_make_wares_dearer()
    {
        var h = Wheel();
        Late(h);
        SetYard(h, ["tsargrad"]);
        Items(h, ("jug||1", 1));
        var before = ItemValue(h, "jug||1");
        SetYard(h, ["tsargrad"], rep: [("tsargrad", 750)]);
        // Сама пільга ×1,5 — і ще ×1,3 від десяти рівнів шани до всього.
        Assert.Equal(1.5 * 1.3, ItemValue(h, "jug||1") / before, 9);
    }

    [Fact]
    public void Canton_merchants_bring_the_painted_jug_sooner()
    {
        double Wait(int cantonPoints)
        {
            var h = Wheel(seed: 3);
            Late(h);
            SetYard(h, ["tsargrad", "canton"], rep: [("canton", cantonPoints)]);
            var until = View(h).GetProperty("golden").GetProperty("until").GetDateTimeOffset();
            h.Clock.UtcNow = until + TimeSpan.FromSeconds(3);
            return (View(h).GetProperty("golden").GetProperty("at").GetDateTimeOffset() - h.Clock.UtcNow).TotalSeconds;
        }
        Assert.Equal(0.7, Wait(750) / Wait(0), 3);
    }

    [Fact]
    public void The_diaspora_makes_village_orders_pay_more()
    {
        var h = Wheel();
        Late(h);
        SetYard(h, ["diaspora"]);
        var order = View(h).GetProperty("market").GetProperty("orders").EnumerateArray().First();
        var id = order.GetProperty("id").GetInt32();
        var before = order.GetProperty("mult").GetDouble();
        SetYard(h, ["diaspora"], rep: [("diaspora", 750)]);
        var after = View(h).GetProperty("market").GetProperty("orders").EnumerateArray()
            .First(o => o.GetProperty("id").GetInt32() == id).GetProperty("mult").GetDouble();
        Assert.Equal(1.5, after / before, 9);
    }

    [Fact]
    public void London_traders_make_the_fair_stronger()
    {
        var h = Wheel();
        Late(h);
        SetYard(h, ["london"], rep: [("london", 120)]);
        Assert.Equal(Clicker.FairMult + 0.3 * 5, View(h).GetProperty("fair").GetProperty("mult").GetDouble(), 9);
    }

    [Fact]
    public void Paris_collectors_keep_the_wheel_turning_longer()
    {
        var h = Wheel();
        Late(h);
        SetYard(h, ["paris"], rep: [("paris", 750)]);
        Assert.Equal(Clicker.OfflineCap.TotalHours + 200 / 60.0, View(h).GetProperty("offlineHours").GetDouble(), 9);
    }

    [Fact]
    public void The_world_s_potters_make_the_falling_jug_richer()
    {
        var h = Wheel();
        Late(h);
        SetYard(h, ["masters"]);
        var before = View(h).GetProperty("fall").GetProperty("gain").GetDouble() - Clicker.FallFloor;
        SetYard(h, ["masters"], rep: [("masters", 750)]);
        var after = View(h).GetProperty("fall").GetProperty("gain").GetDouble() - Clicker.FallFloor;
        // Кошик-пільга +50 % — і ×1,3 від десяти рівнів шани до всього.
        Assert.Equal(1.5 * 1.3, after / before, 6);
    }

    [Fact]
    public void The_sea_map_adds_a_place_and_brings_orders_sooner()
    {
        var h = Wheel();
        Late(h);
        SetYard(h, ["tsargrad"], nextMinutes: 0);
        Patch(h, s => s["secrets"] = new JsonArray("seamap"));
        Assert.True(Yard(h).GetProperty("map").GetBoolean());
        Assert.Equal(2, Yard(h).GetProperty("slots").GetInt32());
        var seen = 0;
        for (var step = 0; step < 6; step++)
        {
            seen = Math.Max(seen, Orders(h).Count);
            var next = Yard(h).GetProperty("next").GetDateTimeOffset() - h.Clock.UtcNow;
            Assert.InRange(next.TotalMinutes, Clicker.GuestGapMinMinutes * Clicker.GuestSeamapGap, Clicker.GuestGapMaxMinutes * Clicker.GuestSeamapGap);
            h.Clock.Advance(TimeSpan.FromMinutes(29));
        }
        Assert.Equal(2, seen);
    }

    // ---------- ачівки ----------

    [Fact]
    public void The_first_order_is_an_achievement_once()
    {
        var h = Wheel();
        Late(h);
        SetYard(h, ["tsargrad"], orders: [Order(h, 7, "tsargrad", "jug", 2, 1), Order(h, 8, "tsargrad", "jug", 2, 1)]);
        Items(h, ("jug||2", 2));
        Assert.True(Give(h, 7).Ok);
        Assert.True(Give(h, 8).Ok);
        Assert.Single(h.Awards, a => a.Reason == "ach:" + Clicker.GuestAchFirst);
        // «potter-guest» — «Гостинна хата» сьомого оновлення (гості села): замовлення гостей її не видають.
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:potter-guest");
    }

    [Fact]
    public void The_tenth_level_of_one_guest_is_an_achievement_and_is_said_aloud()
    {
        var h = Wheel();
        Late(h);
        SetYard(h, ["tsargrad"], rep: [("tsargrad", 475)], orders: [Order(h, 7, "tsargrad", "jug", 2, 1)]);
        Items(h, ("jug||2", 1));
        var r = Give(h, 7);
        Assert.True(r.Ok);
        Assert.EndsWith("· ⭐ Царградські купці: шана 10", r.Message);
        Assert.Contains(h.Awards, a => a.Reason == "ach:" + Clicker.GuestAchMax);
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:" + Clicker.GuestAchAll);
    }

    [Fact]
    public void All_six_guests_on_the_fifth_level_is_an_achievement()
    {
        var h = Wheel();
        Late(h);
        var rep = Clicker.Guests.Select(g => (g.Key, g.Key == "diaspora" ? 118 : 120)).ToArray();
        SetYard(h, Clicker.Guests.Select(g => g.Key).ToArray(), rep: rep, orders: [Order(h, 7, "diaspora", "pot", 2, 2)]);
        Items(h, ("pot||2", 2));
        Assert.True(Give(h, 7).Ok);
        Assert.Equal(5, GuestView(h, "diaspora").GetProperty("level").GetInt32());
        Assert.Contains(h.Awards, a => a.Reason == "ach:" + Clicker.GuestAchAll);
    }

    // ---------- збереження й каталог ----------

    [Fact]
    public void An_old_save_without_guests_reads_as_no_guests_yet()
    {
        var h = Wheel();
        Late(h);
        Patch(h, s => s.Remove("guests"));
        Assert.Equal(JsonValueKind.Null, View(h).GetProperty("guests").ValueKind);
        Patch(h, s =>
        {
            s.Remove("guests");
            s["upgrades"]!["voyage"] = 1;
        });
        Assert.Equal("canton", Yard(h).GetProperty("list")[0].GetProperty("key").GetString());
    }

    [Fact]
    public void Guests_survive_a_save_round_trip()
    {
        var h = Wheel();
        Late(h);
        SetYard(h, ["tsargrad", "paris"], rep: [("tsargrad", 77), ("paris", 5)],
            orders: [Order(h, 7, "tsargrad", "jug", 2, 3, who: 2), Order(h, 8, "paris", "lion", 3, 1, style: "kosiv")], delivered: 4);
        var before = Views.Text(Yard(h));
        string json;
        lock (h.Room.Sync) json = h.Room.Game.Save()!;

        var other = Wheel();
        lock (other.Room.Sync) other.Room.Game.Load(json);
        Assert.Equal(before, Views.Text(Yard(other)));
        Assert.Equal(4, Yard(other).GetProperty("delivered").GetInt32());
        Assert.Equal(2, Orders(other).Single(o => o.GetProperty("id").GetInt32() == 7).GetProperty("who").GetInt32());
    }

    [Fact]
    public void Broken_rows_in_a_save_are_dropped_not_the_whole_game()
    {
        var h = Wheel();
        Late(h);
        SetYard(h, ["tsargrad"], orders:
        [
            Order(h, 7, "tsargrad", "jug", 2, 1),
            Order(h, 8, "martians", "jug", 2, 1),
            Order(h, 9, "tsargrad", "spaceship", 2, 1),
            Order(h, 10, "tsargrad", "jug", 9, 1),
            Order(h, 11, "tsargrad", "jug", 2, 1, style: "neon"),
            Order(h, 12, "tsargrad", "jug", 2, 99),
        ]);
        Patch(h, s =>
        {
            s["guests"]!["met"]!["martians"] = h.Clock.UtcNow.ToString("O");
            s["guests"]!["rep"] = new JsonObject { ["tsargrad"] = 10, ["martians"] = 999, ["paris"] = -5 };
            s["guests"]!["orders"]![0]!["who"] = 42;
        });
        var yard = Yard(h);
        Assert.Equal(new[] { "tsargrad" }, yard.GetProperty("list").EnumerateArray().Select(g => g.GetProperty("key").GetString()!).ToArray());
        var order = Assert.Single(yard.GetProperty("orders").EnumerateArray());
        Assert.Equal(7, order.GetProperty("id").GetInt32());
        Assert.Equal(2, order.GetProperty("who").GetInt32());                // підпис — у межах списку людей гостя
        Assert.Equal(10, Rep(h, "tsargrad"));
        Assert.Equal(1.0 + 0.03, yard.GetProperty("allMult").GetDouble(), 9);
    }

    [Fact]
    public void The_catalog_tells_about_six_guests()
    {
        var h = Wheel();
        var c = View(h).GetProperty("catalog").GetProperty("guests");
        var list = c.GetProperty("list").EnumerateArray().ToList();
        Assert.Equal(Clicker.GuestTiers, list.Select(g => g.GetProperty("tier").GetString()!).ToArray());
        var wares = Clicker.Wares.Select(w => w.Key).ToHashSet();
        foreach (var g in list)
        {
            Assert.Equal(Clicker.Shop.First(u => u.Key == g.GetProperty("tier").GetString()).Name, g.GetProperty("tierName").GetString());
            Assert.InRange(g.GetProperty("lines").GetArrayLength(), 4, 6);
            Assert.Equal(3, g.GetProperty("people").GetArrayLength());
            Assert.False(string.IsNullOrWhiteSpace(g.GetProperty("perk").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(g.GetProperty("arrive").GetString()));
            Assert.All(g.GetProperty("likes").EnumerateArray(), w => Assert.Contains(w.GetString()!, wares));
        }
        Assert.Equal(Clicker.GuestRepLevels.Length, c.GetProperty("levels").GetArrayLength());
        Assert.Equal("глечик, куманець, свічник", list[0].GetProperty("likesText").GetString());
        // Каталог — лише до першої дії: вид щопачки кліків текстів гостей не везе.
        h.Act(0, "look");
        Assert.Equal(JsonValueKind.Null, View(h).GetProperty("catalog").ValueKind);
    }
}
