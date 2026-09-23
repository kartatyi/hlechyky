using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Село дев'ятого оновлення (пакет D, ClickerFair.cs, docs/games/specs/clicker-v9.md §D): шана до десятої зірки й
/// її вага в оплаті, гостинці раз на день, нові гості й пригоди, базарний день, ярмарковий дзвін.
/// </summary>
public class ClickerFairVillageTests
{
    static readonly DateTimeOffset Thursday = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    static RoomHarness Wheel(int seed = 1, DateTimeOffset? at = null)
    {
        var h = new RoomHarness("clicker", seed: seed);
        h.Clock.UtcNow = at ?? Thursday;
        h.Solo("Оля");
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static JsonElement Market(RoomHarness h) => View(h).GetProperty("market");
    static double Pots(RoomHarness h) => View(h).GetProperty("pots").GetDouble();
    static ActResult Fair(RoomHarness h, object payload) => h.Act(0, "fair", payload);

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static void Rep(RoomHarness h, string village, int points) => Patch(h, s =>
    {
        var fair = s["fair"]!.AsObject();
        var rep = fair["rep"] as JsonObject ?? new JsonObject();
        rep[village] = points;
        fair["rep"] = rep;
    });

    static void Items(RoomHarness h, params (string Key, int N)[] items) => Patch(h, s =>
    {
        var bag = new JsonObject();
        foreach (var (key, n) in items) bag[key] = n;
        s["craft"]!["items"] = bag;
    });

    static JsonObject Order(RoomHarness h, int id, string village, string ware, int quality, int count, string style = "",
        double mult = 2, int minutes = 30, bool lord = false) => new()
    {
        ["id"] = id, ["village"] = village, ["who"] = 0, ["ware"] = ware, ["style"] = style, ["quality"] = quality,
        ["count"] = count, ["mult"] = mult, ["at"] = h.Clock.UtcNow.ToString("O"),
        ["until"] = (h.Clock.UtcNow + TimeSpan.FromMinutes(minutes)).ToString("O"), ["lord"] = lord, ["sour"] = false,
    };

    static void Board(RoomHarness h, params JsonObject[] orders) => Patch(h, s =>
    {
        var fair = s["fair"]!.AsObject();
        fair["orders"] = new JsonArray(orders.Select(o => (JsonNode)o).ToArray());
        fair["orderNext"] = (h.Clock.UtcNow + TimeSpan.FromHours(5)).ToString("O");
    });

    static void Guest(RoomHarness h, string kind, double inSeconds = 0) => Patch(h, s =>
    {
        var at = h.Clock.UtcNow + TimeSpan.FromSeconds(inSeconds);
        s["fair"]!["guest"] = new JsonObject
        {
            ["kind"] = kind, ["at"] = at.ToString("O"), ["until"] = (at + Clicker.FairGuestShown).ToString("O"), ["x"] = 66, ["y"] = 12,
        };
    });

    /// <summary>Гостинці ще не роздавали сьогодні — щоб не чекати київської півночі.</summary>
    static void GiftDayReady(RoomHarness h) => Patch(h, s => s["fair"]!["giftDay"] = "2020-01-01");

    /// <summary>Базарного дня ще не було й скоро не буде (або навпаки — от-от).</summary>
    static void BazaarAt(RoomHarness h, double inSeconds) => Patch(h, s =>
        s["fair"]!["bazaarNext"] = (h.Clock.UtcNow + TimeSpan.FromSeconds(inSeconds)).ToString("O"));

    static JsonElement? OrderView(RoomHarness h, int id) =>
        Market(h).GetProperty("orders").EnumerateArray().Cast<JsonElement?>().FirstOrDefault(o => o!.Value.GetProperty("id").GetInt32() == id);

    static JsonElement RepView(RoomHarness h, string village) =>
        Market(h).GetProperty("rep").EnumerateArray().First(r => r.GetProperty("key").GetString() == village);

    static List<string> GiftTexts(RoomHarness h) =>
        Market(h).GetProperty("gifts").EnumerateArray().Select(g => g.GetProperty("text").GetString()!).ToList();

    static int Straw(RoomHarness h) => View(h).GetProperty("kiln").GetProperty("straw").GetInt32();

    static JsonElement Ware(RoomHarness h, string key) =>
        View(h).GetProperty("craft").GetProperty("wares").EnumerateArray().First(x => x.GetProperty("key").GetString() == key);

    static int ItemsTotal(RoomHarness h) =>
        View(h).GetProperty("craft").GetProperty("items").EnumerateArray().Sum(x => x.GetProperty("n").GetInt32());

    // ---------- D.1 шана до десятої зірки ----------

    [Fact]
    public void Respect_goes_on_past_the_fifth_star_to_the_tenth()
    {
        Assert.Equal(11, Clicker.FairRepLevels.Length);
        Assert.Equal([0, 8, 25, 60, 120, 220, 380, 620, 1000, 1600, 2500], Clicker.FairRepLevels);
        // Пороги ростуть, і кожен наступний — більший за попередній (інакше рівень «перескочив би»).
        for (var i = 1; i < Clicker.FairRepLevels.Length; i++)
            Assert.True(Clicker.FairRepLevels[i] > Clicker.FairRepLevels[i - 1]);
    }

    [Fact]
    public void Points_earned_before_the_update_are_not_lost_they_just_mean_more_stars()
    {
        var h = Wheel();
        // Старе збереження, де шана давно вперлась у стелю v7: усе, що понад 220, тоді нікуди не йшло,
        // а тепер це просто нові зірки — і жодне очко не згоріло.
        Rep(h, "opishnia", 400);
        Assert.Equal(6, RepView(h, "opishnia").GetProperty("level").GetInt32());
        Assert.Equal(400, RepView(h, "opishnia").GetProperty("pts").GetInt32());
        Rep(h, "kosiv", 300);
        Assert.Equal(5, RepView(h, "kosiv").GetProperty("level").GetInt32());
        Assert.Equal(300, RepView(h, "kosiv").GetProperty("pts").GetInt32());
    }

    [Fact]
    public void Respect_pays_a_tenth_more_per_star_and_twice_that_to_the_lord()
    {
        var h = Wheel();
        Board(h, Order(h, 100, "opishnia", "pot", 1, 2), Order(h, 101, "opishnia", "pot", 1, 2, mult: Clicker.FairLordMult, lord: true));
        Assert.Equal(2.0, OrderView(h, 100)!.Value.GetProperty("mult").GetDouble(), 9);
        Assert.Equal(4.0, OrderView(h, 101)!.Value.GetProperty("mult").GetDouble(), 9);
        Rep(h, "opishnia", 2_500);                                   // десята зірка
        Assert.Equal(2 + 1.0, OrderView(h, 100)!.Value.GetProperty("mult").GetDouble(), 9);
        Assert.Equal(4 + 2.0, OrderView(h, 101)!.Value.GetProperty("mult").GetDouble(), 9);
    }

    [Fact]
    public void The_promised_pay_grows_with_respect_and_so_does_the_real_one()
    {
        double Paid(int points)
        {
            var h = Wheel();
            Patch(h, s => { s["pots"] = 0; s["total"] = 1_000_000; });
            Board(h, Order(h, 100, "opishnia", "pot", 1, 2));
            Rep(h, "opishnia", points);
            Items(h, ("pot||1", 2));
            var promised = OrderView(h, 100)!.Value.GetProperty("pay").GetDouble();
            Assert.True(Fair(h, new { op = "deliver", id = 100 }).Ok);
            var got = Pots(h);
            Assert.Equal(promised, got, 0);                          // обіцянка у виді = те, що справді заплатили
            return got;
        }
        var plain = Paid(0);
        var top = Paid(2_500);
        // ×2 → ×3: половину зверху. Шана більше не «+1 % до всього й нічого більше».
        Assert.Equal(plain * 1.5, top, plain * 0.02);
    }

    [Fact]
    public void The_tenth_star_is_an_achievement_of_its_own()
    {
        var h = Wheel();
        Board(h, Order(h, 100, "opishnia", "pot", 1, 2));
        Rep(h, "opishnia", 2_498);
        Items(h, ("pot||1", 2));
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:potter-rep10");
        var r = Fair(h, new { op = "deliver", id = 100 });
        Assert.Contains("⭐ Опішня: шана 10", r.Message);
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-rep10");
    }

    [Fact]
    public void The_fifth_star_achievement_still_comes_at_the_fifth_star()
    {
        var h = Wheel();
        Board(h, Order(h, 100, "kosiv", "pot", 1, 2));
        Rep(h, "kosiv", 218);
        Items(h, ("pot||1", 2));
        var r = Fair(h, new { op = "deliver", id = 100 });
        Assert.True(r.Ok);
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-rep");
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:potter-rep10");
    }

    [Fact]
    public void Village_perks_keep_the_same_rate_all_the_way_to_the_tenth_star()
    {
        var h = Wheel(at: Thursday);
        Patch(h, s => { s["total"] = 10_000_000_000; s["upgrades"]!["kiln"] = 100; });
        var dry = Market(h).GetProperty("dry").GetDouble();
        Rep(h, "vasylkiv", 2_500);                                   // 10 × −4 % = −40 % сушіння
        Assert.Equal(dry * 0.6, Market(h).GetProperty("dry").GetDouble(), 9);

        var h2 = Wheel(at: Thursday);
        Patch(h2, s => { s["total"] = 10_000_000_000; s["upgrades"]!["kiln"] = 100; });
        var jug0 = Ware(h2, "jug").GetProperty("need").GetInt32();
        Rep(h2, "opishnia", 2_500);                                  // 10 × −4 % = −40 % роботи (підлоги 40 % не зачеплено)
        Assert.Equal((int)Math.Ceiling(jug0 * 0.6), Ware(h2, "jug").GetProperty("need").GetInt32());
        Assert.True(Ware(h2, "jug").GetProperty("need").GetInt32() >= (int)Math.Ceiling(jug0 * Clicker.MinWorkShare));
    }

    // ---------- D.2 гостинець із села ----------

    [Fact]
    public void A_village_that_respects_you_sends_a_gift_once_a_day()
    {
        var h = Wheel(seed: 3);
        Rep(h, "opishnia", 380);                                     // шоста зірка — рівно поріг гостинців
        GiftDayReady(h);
        var gifts = GiftTexts(h);
        Assert.Single(gifts);
        Assert.StartsWith("🎁 Гостинець з Опішні:", gifts[0]);
        // Удруге того самого дня — нічого нового.
        h.Clock.Advance(60);
        Assert.Equal(gifts, GiftTexts(h));
        Assert.Equal(1, RepView(h, "opishnia").GetProperty("gifts").GetInt32());
    }

    [Fact]
    public void A_new_day_brings_a_new_gift()
    {
        var h = Wheel(seed: 3);
        Rep(h, "opishnia", 380);
        GiftDayReady(h);
        Assert.Single(GiftTexts(h));
        h.Clock.Advance(TimeSpan.FromDays(1));
        View(h);
        Assert.Equal(2, RepView(h, "opishnia").GetProperty("gifts").GetInt32());
    }

    [Fact]
    public void Villages_below_the_sixth_star_send_nothing()
    {
        var h = Wheel();
        Rep(h, "opishnia", 379);                                     // одне очко до шостої зірки
        GiftDayReady(h);
        Assert.Empty(GiftTexts(h));
        Assert.Equal(0, RepView(h, "opishnia").GetProperty("gifts").GetInt32());
    }

    [Fact]
    public void Every_respected_village_sends_its_own_gift()
    {
        var h = Wheel(seed: 5);
        foreach (var v in Clicker.FairVillages) Rep(h, v.Key, 2_500);
        GiftDayReady(h);
        var gifts = GiftTexts(h);
        Assert.Equal(Clicker.FairVillages.Length, gifts.Count);
        Assert.Equal(Clicker.FairVillages.Length, gifts.Distinct().Count());
        foreach (var v in Clicker.FairVillages)
            Assert.Contains(gifts, g => g.StartsWith($"🎁 Гостинець {v.From}:"));
    }

    [Fact]
    public void A_gift_is_pots_straw_or_a_ringing_ware_and_nothing_else()
    {
        var pots = false;
        var straw = false;
        var ware = false;
        for (var seed = 1; seed <= 12 && !(pots && straw && ware); seed++)
        {
            var h = Wheel(seed);
            Patch(h, s => { s["pots"] = 0; s["total"] = 10_000_000_000; s["upgrades"]!["kiln"] = 50; });
            Patch(h, s => s["kiln"]!["strawStock"] = 0);
            Items(h);
            Rep(h, "opishnia", 2_500);
            GiftDayReady(h);
            var text = Assert.Single(GiftTexts(h));
            if (Pots(h) > 0) { pots = true; Assert.Contains("за поміч селу", text); }
            else if (Straw(h) > 0) { straw = true; Assert.Equal(Clicker.FairGiftStraw, Straw(h)); Assert.Contains("соломи", text); }
            else { ware = true; Assert.Equal(1, ItemsTotal(h)); Assert.Contains("дзвінкий", text); }
        }
        Assert.True(pots && straw && ware, "за дюжину сідів мусили трапитись усі три гостинці");
    }

    [Fact]
    public void The_ringing_gift_comes_painted_in_the_style_of_its_village()
    {
        for (var seed = 1; seed <= 20; seed++)
        {
            var h = Wheel(seed);
            Patch(h, s => s["total"] = 10_000_000_000);
            Items(h);
            Rep(h, "kosiv", 2_500);
            GiftDayReady(h);
            if (ItemsTotal(h) == 0) continue;
            var item = View(h).GetProperty("craft").GetProperty("items").EnumerateArray().First();
            Assert.Equal(3, item.GetProperty("q").GetInt32());
            Assert.Equal("kosiv", item.GetProperty("style").GetString());
            return;
        }
        Assert.Fail("за двадцять сідів мусив трапитись виріб у гостинці");
    }

    [Fact]
    public void Gifts_missed_while_away_do_not_pile_up()
    {
        var h = Wheel(seed: 3);
        Rep(h, "opishnia", 2_500);
        GiftDayReady(h);
        Assert.Single(GiftTexts(h));
        // Тиждень без гри — і рівно один гостинець, а не сім.
        h.Clock.Advance(TimeSpan.FromDays(7));
        View(h);
        Assert.Equal(2, RepView(h, "opishnia").GetProperty("gifts").GetInt32());
    }

    [Fact]
    public void An_old_save_without_gifts_gets_todays_one_and_keeps_the_count()
    {
        var h = Wheel(seed: 3);
        Rep(h, "opishnia", 2_500);
        // Збереження з проду: поля гостинців ще не існувало.
        Patch(h, s =>
        {
            var fair = s["fair"]!.AsObject();
            fair.Remove("giftDay");
            fair.Remove("gifts");
            fair.Remove("giftLog");
        });
        Assert.Single(GiftTexts(h));
        Assert.Equal(1, RepView(h, "opishnia").GetProperty("gifts").GetInt32());
        // І пережило перезавантаження.
        Patch(h, _ => { });
        Assert.Equal(1, RepView(h, "opishnia").GetProperty("gifts").GetInt32());
    }

    [Fact]
    public void A_gift_line_leaves_the_feed_after_ten_minutes()
    {
        var h = Wheel(seed: 3);
        Rep(h, "opishnia", 2_500);
        GiftDayReady(h);
        Assert.Single(GiftTexts(h));
        h.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Empty(GiftTexts(h));
        Assert.Equal(1, RepView(h, "opishnia").GetProperty("gifts").GetInt32());
    }

    // ---------- D.4 нові гості ----------

    [Fact]
    public void There_are_three_new_guests_and_the_old_ones_are_still_the_common_ones()
    {
        Assert.Equal(8, Clicker.FairGuests.Length);
        foreach (var key in new[] { "dyak", "wander", "bear" })
            Assert.Contains(Clicker.FairGuests, g => g.Key == key);
        var old = Clicker.FairGuests.Take(5).Sum(g => g.Weight);
        var @new = Clicker.FairGuests.Skip(5).Sum(g => g.Weight);
        Assert.True(old > @new, "старі гості мусять лишитись частішими за нових");
    }

    [Fact]
    public void The_scribe_adds_beauty_to_the_next_painting_and_only_to_it()
    {
        var h = Wheel();
        Guest(h, "dyak");
        Assert.Equal(0, Market(h).GetProperty("beauty").GetInt32());
        var r = Fair(h, new { op = "guest" });
        Assert.True(r.Ok, r.Message);
        Assert.Contains("розпис", r.Message);
        Assert.Equal(Clicker.FairDyakBeauty, Market(h).GetProperty("beauty").GetInt32());
        // Пережило перезавантаження…
        Patch(h, _ => { });
        Assert.Equal(Clicker.FairDyakBeauty, Market(h).GetProperty("beauty").GetInt32());
        // …і забирається рівно один раз (горно кличе це, коли рахує красу).
        var game = (Clicker)h.Room.Game;
        Assert.Equal(Clicker.FairDyakBeauty, game.FairTakeBeauty());
        Assert.Equal(0, game.FairTakeBeauty());
        h.Clock.Advance(1);                                          // вид кешується на 50 мс
        Assert.Equal(0, Market(h).GetProperty("beauty").GetInt32());
    }

    [Fact]
    public void The_wandering_potter_adds_fired_wares_to_the_mastery_of_one_ware()
    {
        var h = Wheel();
        Patch(h, s => s["craft"]!["firedBy"] = new JsonObject { ["pot"] = 40 });
        Guest(h, "wander");
        var r = Fair(h, new { op = "guest" });
        Assert.True(r.Ok, r.Message);
        Assert.Contains("обпалених", r.Message);
        var fired = View(h).GetProperty("craft").GetProperty("wares").EnumerateArray()
            .First(x => x.GetProperty("key").GetString() == "pot").GetProperty("fired").GetInt64();
        Assert.Equal(40 + Clicker.FairWanderFired, fired);
    }

    [Fact]
    public void The_bear_either_doubles_the_next_order_or_knocks_a_shelf_over()
    {
        var doubled = false;
        var knocked = false;
        for (var seed = 1; seed <= 12 && !(doubled && knocked); seed++)
        {
            var h = Wheel(seed);
            Patch(h, s => { s["pots"] = 1_000_000; s["total"] = 10_000_000; });
            Guest(h, "bear");
            var r = Fair(h, new { op = "guest" });
            Assert.True(r.Ok, r.Message);
            if (Market(h).GetProperty("bear").GetBoolean())
            {
                doubled = true;
                Assert.Contains("удвічі", r.Message);
                // Наступне замовлення справді платить удвічі — і лише воно.
                Board(h, Order(h, 100, "opishnia", "pot", 1, 2), Order(h, 101, "opishnia", "pot", 1, 2));
                Items(h, ("pot||1", 4));
                var promised = OrderView(h, 100)!.Value.GetProperty("pay").GetDouble();
                Patch(h, s => s["pots"] = 0);
                Assert.True(Fair(h, new { op = "deliver", id = 100 }).Ok);
                Assert.Equal(promised * 2, Pots(h), promised * 0.02);
                Assert.False(Market(h).GetProperty("bear").GetBoolean());
                Patch(h, s => s["pots"] = 0);
                Assert.True(Fair(h, new { op = "deliver", id = 101 }).Ok);
                Assert.Equal(promised, Pots(h), promised * 0.02);
            }
            else
            {
                knocked = true;
                Assert.Contains("перекинув", r.Message);
                // Рівно двадцята частина — і ні глеком більше.
                Assert.Equal(1_000_000 * (1 - Clicker.FairBearLoss), Pots(h), 1);
            }
        }
        Assert.True(doubled && knocked, "за дюжину сідів ведмідь мусив зіграти в обидва боки");
    }

    [Fact]
    public void The_new_guests_are_caught_like_the_old_ones_and_cost_the_masters_patience()
    {
        foreach (var kind in new[] { "dyak", "wander", "bear" })
        {
            var h = Wheel();
            Guest(h, kind, inSeconds: 600);                          // ще не прийшов
            Assert.False(Fair(h, new { op = "guest" }).Ok);
            Guest(h, kind);
            Assert.Equal(0, Market(h).GetProperty("guests").GetInt32());
            Assert.True(Fair(h, new { op = "guest" }).Ok);
            Assert.Equal(1, Market(h).GetProperty("guests").GetInt32());
            // Спійманий гість іде далі селом, а наступного чекаємо за розкладом.
            Assert.True(Market(h).GetProperty("guest").GetProperty("at").GetDateTimeOffset() > h.Clock.UtcNow, kind);
        }
    }

    // ---------- D.4 нові пригоди ----------

    [Fact]
    public void Six_new_adventures_joined_the_old_ones()
    {
        Assert.Equal(27, Clicker.FairEvents.Length);
        foreach (var key in new[] { "bridge", "fiddler", "horseshoe", "neighbour-kiln", "apprentice-girl", "fog" })
            Assert.Contains(Clicker.FairEvents, e => e.Key == key);
        Assert.Equal(Clicker.FairEvents.Length, Clicker.FairEvents.Select(e => e.Key).Distinct().Count());
        // Кожна пригода — два варіанти, у кожного 1–2 наслідки, і в кожного є текст.
        foreach (var e in Clicker.FairEvents)
        {
            Assert.NotEmpty(e.Title);
            Assert.NotEmpty(e.Text);
            foreach (var c in new[] { e.A, e.B })
            {
                Assert.NotEmpty(c.Label);
                Assert.InRange(c.Outcomes.Length, 1, 2);
                Assert.All(c.Outcomes, o => Assert.NotEmpty(o.Text));
            }
        }
    }

    [Fact]
    public void The_new_adventures_can_be_chosen_and_stay_within_the_old_limits()
    {
        foreach (var key in new[] { "bridge", "fiddler", "horseshoe", "neighbour-kiln", "apprentice-girl", "fog" })
            for (var pick = 0; pick < 2; pick++)
                for (var roll = 0; roll < 2; roll++)
                {
                    var h = Wheel();
                    Patch(h, s => { s["pots"] = 1_000; s["total"] = 1_000; s["upgrades"]!["tsar"] = 5; });
                    Items(h, ("pot||1", 3));
                    Patch(h, s => s["fair"]!["event"] = new JsonObject
                    {
                        ["id"] = 7, ["key"] = key, ["at"] = h.Clock.UtcNow.ToString("O"),
                        ["until"] = (h.Clock.UtcNow + Clicker.FairEventWait).ToString("O"),
                        ["rollA"] = roll, ["rollB"] = roll, ["foresight"] = false,
                    });
                    var r = Fair(h, new { op = "choose", id = 7, pick });
                    Assert.True(r.Ok, key + ": " + r.Message);
                    Assert.True(Pots(h) >= 900, $"{key}/{pick}/{roll}: лишилось {Pots(h)}");
                    Assert.InRange(ItemsTotal(h), 2, 5);
                    foreach (var b in Market(h).GetProperty("buffs").EnumerateArray())
                    {
                        Assert.True(b.GetProperty("until").GetDateTimeOffset() <= h.Clock.UtcNow + TimeSpan.FromSeconds(Clicker.FairBuffMaxSeconds));
                        Assert.InRange(b.GetProperty("mult").GetDouble(), 0.5, 1.5);
                    }
                    Assert.All(Market(h).GetProperty("rep").EnumerateArray(), x => Assert.InRange(x.GetProperty("pts").GetInt32(), 0, 3));
                }
    }

    // ---------- D.4 базарний день ----------

    [Fact]
    public void A_market_day_comes_every_three_to_six_hours_and_lasts_ten_minutes()
    {
        for (var seed = 1; seed <= 6; seed++)
        {
            var h = Wheel(seed);
            var next = View(h).GetProperty("market");
            Assert.True(next.GetProperty("bazaar").ValueKind == JsonValueKind.Null, "на старті базарного дня нема");
            BazaarAt(h, 1);
            h.Clock.Advance(2);
            var b = Market(h).GetProperty("bazaar");
            Assert.Equal(Clicker.FairBazaarMult, b.GetProperty("mult").GetDouble());
            var left = b.GetProperty("until").GetDateTimeOffset() - h.Clock.UtcNow;
            Assert.Equal(Clicker.FairBazaarFor.TotalMinutes, left.TotalMinutes, 1);
            // Скінчився — і наступний не раніше ніж за три години.
            h.Clock.Advance(Clicker.FairBazaarFor + TimeSpan.FromSeconds(1));
            Assert.Equal(JsonValueKind.Null, Market(h).GetProperty("bazaar").ValueKind);
        }
    }

    [Fact]
    public void On_a_market_day_every_order_pays_half_as_much_again()
    {
        var h = Wheel();
        Patch(h, s => { s["pots"] = 0; s["total"] = 1_000_000; });
        Board(h, Order(h, 100, "opishnia", "pot", 1, 2));
        Items(h, ("pot||1", 2));
        var plain = OrderView(h, 100)!.Value.GetProperty("pay").GetDouble();
        BazaarAt(h, 1);
        h.Clock.Advance(2);
        Assert.Equal(plain * Clicker.FairBazaarMult, OrderView(h, 100)!.Value.GetProperty("pay").GetDouble(), plain * 0.02);
        var r = Fair(h, new { op = "deliver", id = 100 });
        Assert.True(r.Ok);
        Assert.Contains("базарний день", r.Message);
        Assert.Equal(plain * Clicker.FairBazaarMult, Pots(h), plain * 0.02);
    }

    // ---------- D.5 ярмарковий дзвін ----------

    /// <summary>
    /// Каталог секретів другого кола додає пакет «Коло» (контракт v9 §1), тож до злиття гілок ключ «bell»
    /// у збереження просто не лягає. Щоб тест не брехав ні до, ні після злиття, він спершу звіряє, чи ключ
    /// уже відомий грі, і чекає рівно тієї дошки, яка з цього виходить.
    /// </summary>
    static bool Bell(RoomHarness h)
    {
        Patch(h, s => s["secrets"] = new JsonArray("bell"));
        var on = Market(h).GetProperty("bell").GetBoolean();
        Assert.Equal(Clicker.Secrets.Any(x => x.Key == "bell"), on);
        return on;
    }

    [Fact]
    public void The_fair_bell_keeps_five_orders_on_the_board_instead_of_four()
    {
        var h = Wheel();
        Assert.Equal(Clicker.FairBoardMax, Market(h).GetProperty("boardMax").GetInt32());
        Assert.False(Market(h).GetProperty("bell").GetBoolean());
        var want = Bell(h) ? Clicker.FairBellBoardMax : Clicker.FairBoardMax;
        Assert.Equal(want, Market(h).GetProperty("boardMax").GetInt32());
        var max = 0;
        for (var minute = 1; minute <= 40; minute++)
        {
            h.Clock.Advance(60);
            max = Math.Max(max, Market(h).GetProperty("orders").GetArrayLength());
        }
        Assert.Equal(want, max);
    }

    [Fact]
    public void With_the_bell_a_new_order_comes_every_four_to_seven_minutes()
    {
        for (var seed = 1; seed <= 8; seed++)
        {
            var h = Wheel(seed);
            var (min, max) = Bell(h)
                ? (Clicker.FairBellGapMinSeconds, Clicker.FairBellGapMaxSeconds)
                : (Clicker.FairOrderGapMinSeconds, Clicker.FairOrderGapMaxSeconds);
            // Розклад перераховується, коли надходить черговий замовник.
            var at = Market(h).GetProperty("nextOrderAt").GetDateTimeOffset();
            h.Clock.UtcNow = at + TimeSpan.FromSeconds(1);
            var wait = Market(h).GetProperty("nextOrderAt").GetDateTimeOffset() - h.Clock.UtcNow;
            Assert.InRange(wait.TotalSeconds, min - 2, max);
        }
    }

    // ---------- збереження ----------

    [Fact]
    public void Everything_new_survives_a_reload()
    {
        var h = Wheel(seed: 4);
        Rep(h, "opishnia", 2_500);
        Guest(h, "dyak");
        Assert.True(Fair(h, new { op = "guest" }).Ok);
        GiftDayReady(h);
        BazaarAt(h, 1);
        h.Clock.Advance(2);
        var before = Views.Text(Market(h));
        Patch(h, _ => { });
        Assert.Equal(before, Views.Text(Market(h)));
    }

    [Fact]
    public void A_save_from_before_the_village_update_reads_as_nothing_new_yet()
    {
        var h = Wheel();
        Patch(h, s =>
        {
            var fair = s["fair"]!.AsObject();
            foreach (var key in new[] { "giftDay", "gifts", "giftLog", "beauty", "bear", "bazaarUntil", "bazaarNext" })
                fair.Remove(key);
        });
        var m = Market(h);
        Assert.Equal(0, m.GetProperty("beauty").GetInt32());
        Assert.False(m.GetProperty("bear").GetBoolean());
        Assert.Equal(JsonValueKind.Null, m.GetProperty("bazaar").ValueKind);
        Assert.Empty(m.GetProperty("gifts").EnumerateArray());
        Assert.All(m.GetProperty("rep").EnumerateArray(), r => Assert.Equal(0, r.GetProperty("gifts").GetInt32()));
        // Базарний день таки прийде — просто за новим розкладом від «зараз».
        h.Clock.Advance(TimeSpan.FromHours(7));
        Assert.NotEqual(JsonValueKind.Null, Market(h).GetProperty("bazaar").ValueKind);
    }

    [Fact]
    public void Broken_new_rows_in_a_save_are_dropped()
    {
        var h = Wheel();
        Patch(h, s =>
        {
            var fair = s["fair"]!.AsObject();
            fair["giftDay"] = "не-день";
            fair["gifts"] = new JsonObject { ["опішня"] = 3, ["opishnia"] = -5, ["kosiv"] = 2 };
            fair["giftLog"] = new JsonArray(new JsonObject
            {
                ["village"] = "atlantida", ["text"] = "🎁 Гостинець зі дна морського", ["at"] = h.Clock.UtcNow.ToString("O"),
            });
            fair["beauty"] = 9_000;
            fair["bazaarUntil"] = (h.Clock.UtcNow + TimeSpan.FromDays(3)).ToString("O");
        });
        var m = Market(h);
        Assert.Equal(Clicker.FairDyakBeauty, m.GetProperty("beauty").GetInt32());
        Assert.Equal(0, RepView(h, "opishnia").GetProperty("gifts").GetInt32());
        Assert.Equal(2, RepView(h, "kosiv").GetProperty("gifts").GetInt32());
        Assert.DoesNotContain(GiftTexts(h), t => t.Contains("дна морського"));
        var b = m.GetProperty("bazaar");
        if (b.ValueKind != JsonValueKind.Null)
            Assert.True(b.GetProperty("until").GetDateTimeOffset() <= h.Clock.UtcNow + Clicker.FairBazaarFor);
    }

    [Fact]
    public void Firing_burns_the_orders_but_not_the_gifts_or_the_stars()
    {
        var h = Wheel(seed: 3);
        Rep(h, "opishnia", 2_500);
        GiftDayReady(h);
        Assert.Single(GiftTexts(h));
        Patch(h, s => { s["total"] = 1e18; s["pots"] = 1e18; });
        Assert.True(h.Act(0, "fire", new { }).Ok, h.Reply.Message);
        Assert.Equal(10, RepView(h, "opishnia").GetProperty("level").GetInt32());
        Assert.Equal(1, RepView(h, "opishnia").GetProperty("gifts").GetInt32());
        // І другого гостинця того самого дня обпал не приносить.
        Assert.Equal(1, RepView(h, "opishnia").GetProperty("gifts").GetInt32());
    }

    [Fact]
    public void The_village_catalog_tells_what_the_next_star_gives()
    {
        var h = Wheel();
        var fair = View(h).GetProperty("catalog").GetProperty("fair");
        Assert.Equal(11, fair.GetProperty("levels").GetArrayLength());
        Assert.Equal(Clicker.FairGiftFrom, fair.GetProperty("giftFrom").GetInt32());
        Assert.Equal(Clicker.FairPayPerLevel, fair.GetProperty("payPerLevel").GetDouble());
        Assert.All(fair.GetProperty("villages").EnumerateArray(), v => Assert.NotEmpty(v.GetProperty("step").GetString()!));
    }
}
