using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Kit = Hlechyky.Tests.Games.ClickerWeekGrowTests.Kit;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «🤝 Спільне замовлення округи» (v13 Г5, ClickerWeek.cs + ClickerWeekGame.cs): сервіс щотижня обирає виріб (відкритий у
/// більшості активних гончарів) якості 3+ і обсяг 15 % обпаленого людьми минулого тижня (500…5000); гончар везе зі своєї
/// комори дією <c>guild { op: "coop" }</c> лише вільне, платить як село; виконали до неділі 23:30 — +10 % до всього на 7 діб
/// тим, хто довіз від 5 %; не виконали — нічого; рядок у Балачки; лише люди.
/// </summary>
public class ClickerCoopTests
{
    static DateTimeOffset Local(int y, int m, int d, int h, int min) => ClickerWeekGrowTests.Local(y, m, d, h, min);

    const string Week = "2026-11-08";
    static readonly DateTimeOffset Wed = Local(2026, 11, 4, 12, 0);
    static readonly DateTimeOffset Settled = Local(2026, 11, 8, 23, 31);
    static DateTimeOffset Ends() => ClickerWeekService.Ends(Week);

    /// <summary>Знімок щоденника з обпаленими горщиками/глечиками/мисками (на день, 10 хв гри).</summary>
    static void Fired(Kit k, string nick, string day, long pot, long jug = 0, long bowl = 0, int minutes = 10)
    {
        var at = Local(int.Parse(day[..4]), int.Parse(day[5..7]), int.Parse(day[8..10]), 12, 0);
        k.Diary.Put(nick, day, at, string.Create(CultureInfo.InvariantCulture,
            $"{{\"total\":1e20,\"dayStart\":1e19,\"fired\":{{\"pot\":{pot},\"jug\":{jug},\"bowl\":{bowl}}}}}"));
        if (minutes > 0) k.Diary.AddHour(nick, at, minutes * 60, 0, 0, 0);
    }

    /// <summary>Минулий тиждень (26.10–01.11): Оля обпалила +3000, Тарас +1500 (разом 4500 → 15 % = 675).</summary>
    static void LastWeek(Kit k)
    {
        Fired(k, "Оля", "2026-10-20", 1000, 500, minutes: 0);
        Fired(k, "Оля", "2026-10-31", 3000, 1500);
        Fired(k, "Тарас", "2026-10-20", 500, 0, minutes: 0);
        Fired(k, "Тарас", "2026-10-30", 1500, 500);
    }

    static void Chosen(Kit k, out string ware, out int quality, out int volume)
    {
        k.Svc.Settle(Wed);
        var order = k.Svc.CoopNow(Wed)!;
        (ware, quality, volume) = (order.Ware, order.Quality, order.Volume);
    }

    // ---------- вибір замовлення ----------

    [Fact]
    public void The_order_is_fifteen_percent_of_what_people_fired_last_week_in_a_ware_open_for_most_potters()
    {
        var k = new Kit();
        LastWeek(k);
        Chosen(k, out var ware, out var quality, out var volume);
        Assert.Equal(675, volume);                                                      // +3000 і +1500 обпалених: 15 % від 4500
        Assert.Equal(3, quality);
        Assert.Contains(ware, new[] { "pot", "jug" });                                  // обидва відкриті в обох
        Assert.Equal(Ends(), k.Svc.CoopNow(Wed)!.Ends);
    }

    [Fact]
    public void A_ware_open_for_less_than_half_the_active_potters_is_not_chosen()
    {
        var k = new Kit();
        LastWeek(k);                                                                    // Оля й Тарас мають pot і jug
        Fired(k, "Зоя", "2026-10-30", 100);                                             // лише горщики
        Fired(k, "Ганна", "2026-10-30", 100);
        Assert.Equal(["pot"], Enumerable.Range(0, 20).Select(i =>
        {
            var one = new Kit();
            LastWeek(one);
            Fired(one, "Зоя", "2026-10-30", 100);
            Fired(one, "Ганна", "2026-10-30", 100);
            one.Svc.Settle(Wed);
            return one.Svc.CoopNow(Wed)!.Ware;
        }).Distinct());
    }

    [Fact]
    public void The_volume_is_never_below_500_nor_above_5000()
    {
        var little = new Kit();
        Fired(little, "Оля", "2026-10-20", 10, minutes: 0);
        Fired(little, "Оля", "2026-10-31", 40);                                         // 15 % від 30 — це 5
        Chosen(little, out _, out _, out var small);
        Assert.Equal(500, small);

        var none = new Kit();                                                           // щоденника нема зовсім
        Chosen(none, out var ware, out _, out var fallback);
        Assert.Equal(500, fallback);
        Assert.Equal(Clicker.Wares[0].Key, ware);

        var big = new Kit();
        Fired(big, "Оля", "2026-10-20", 1000, minutes: 0);
        Fired(big, "Оля", "2026-10-31", 1_000_000);
        Chosen(big, out _, out _, out var huge);
        Assert.Equal(5000, huge);
    }

    [Fact]
    public void Bots_do_not_count_in_the_volume()
    {
        var k = new Kit();
        LastWeek(k);
        Fired(k, "бот Вася", "2026-10-20", 0, minutes: 0);
        Fired(k, "бот Вася", "2026-10-31", 900_000);
        Chosen(k, out _, out _, out var volume);
        Assert.Equal(675, volume);
    }

    // ---------- прогрес у сервісі ----------

    [Fact]
    public void Deliveries_are_capped_by_what_is_left_and_the_last_one_closes_the_order_with_a_line_in_the_chat()
    {
        var k = new Kit();
        Chosen(k, out _, out _, out var volume);
        Assert.Equal(500, volume);
        Assert.Equal(125, k.Svc.CoopAdd("Оля", 300, Wed));                              // стеля одного — 25 % обсягу
        Assert.Equal(125, k.Svc.CoopAdd("Тарас", 400, Wed));
        Assert.Equal(125, k.Svc.CoopAdd("Іван", 125, Wed));
        Assert.Equal(100, k.Svc.CoopAdd("Марко", 100, Wed));                            // лишилось 25
        Assert.Equal(25, k.Svc.CoopAdd("Ігор", 400, Wed));                              // і це — решта: більше не візьме
        Assert.Equal(0, k.Svc.CoopAdd("Богдан", 10, Wed));                              // виконано
        var order = k.Svc.CoopNow(Wed)!;
        Assert.True(order.Fulfilled);
        Assert.Equal(500, order.Done);
        Assert.Equal(125, k.Svc.CoopMine("Оля", Wed));
        var line = Assert.Single(k.Wire.Chats, c => c.Contains("Округа виконала спільне замовлення"));
        Assert.Contains("500", line);
    }

    [Fact]
    public void Bots_and_late_deliveries_are_refused()
    {
        var k = new Kit();
        Chosen(k, out _, out _, out _);
        Assert.Equal(0, k.Svc.CoopAdd("бот Вася", 50, Wed));
        Assert.Equal(0, k.Svc.CoopAdd("Оля", 0, Wed));
        Assert.Equal(0, k.Svc.CoopAdd("Оля", -3, Wed));
        Assert.Equal(0, k.Svc.CoopAdd("Оля", 50, Local(2026, 11, 8, 23, 30)));          // тиждень уже скінчився
        Assert.Equal(0, k.Svc.CoopNow(Wed)!.Done);
        Assert.False(JsonSerializer.SerializeToElement(k.Svc.Wire("бот Вася", Wed)).GetProperty("person").GetBoolean());
    }

    [Fact]
    public void Progress_survives_a_restart_through_the_database()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clk-coop-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        try
        {
            var db = new Db(Path.Combine(dir, "t.db"));
            var k = new Kit(db: db);
            Chosen(k, out var ware, out _, out var volume);
            k.Svc.CoopAdd("Оля", 120, Wed);
            k.Svc.CoopAdd("Тарас", 30, Wed);
            k.Svc.FlushNow();

            var again = k.Make(db);
            var order = again.CoopNow(Wed)!;
            Assert.Equal((ware, volume, 150), (order.Ware, order.Volume, order.Done));
            Assert.Equal(120, again.CoopMine("Оля", Wed));
            again.Settle(Wed);                                                          // не заводить друге замовлення на тиждень
            Assert.Equal(ware, again.CoopNow(Wed)!.Ware);
            Assert.Equal(150, again.CoopNow(Wed)!.Done);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // ---------- підбиття: бонус +10 % ----------

    [Fact]
    public void A_fulfilled_order_gives_plus_ten_percent_for_seven_days_only_to_those_who_brought_five_percent_and_only_after_the_settle()
    {
        var k = new Kit();
        Chosen(k, out _, out _, out _);                                                 // 500 → поріг 25
        k.Svc.CoopAdd("Іван", 24, Wed);                                                 // на одиницю менше
        k.Svc.CoopAdd("Оля", 276, Wed);                                                 // стеля 125
        k.Svc.CoopAdd("Тарас", 200, Wed);                                               // стеля 125
        k.Svc.CoopAdd("Марко", 200, Wed);                                               // стеля 125
        k.Svc.CoopAdd("Ігор", 200, Wed);                                                // лишилось 101
        Assert.True(k.Svc.CoopNow(Wed)!.Fulfilled);
        Assert.Empty(k.Svc.PrizesFor("Оля", Wed));                                      // виконали, але до підбиття нагород нема

        k.Svc.Settle(Settled);
        var olya = Assert.Single(k.Svc.PrizesFor("Оля", Settled));
        Assert.Equal(($"week:{Week}:coop", "coop", 10.0), (olya.Key, olya.Kind, olya.Pct));
        Assert.Equal(Ends().AddDays(7), olya.Until);
        Assert.Single(k.Svc.PrizesFor("Тарас", Settled));
        Assert.Single(k.Svc.PrizesFor("Ігор", Settled));
        Assert.Empty(k.Svc.PrizesFor("Іван", Settled));
        Assert.Empty(k.Svc.PrizesFor("Богдан", Settled));
        Assert.Contains(k.Wire.Chats, c => c.Contains("Спільне замовлення округи підбито") && c.Contains("4 гончарів"));
        Assert.Empty(k.Svc.PrizesFor("Оля", Ends().AddDays(7).AddMinutes(1)));
    }

    [Fact]
    public void An_unfulfilled_order_brings_nothing_and_next_week_gets_a_new_one()
    {
        var k = new Kit();
        Chosen(k, out _, out _, out _);
        k.Svc.CoopAdd("Оля", 125, Wed);
        k.Svc.CoopAdd("Тарас", 125, Wed);
        k.Svc.CoopAdd("Іван", 125, Wed);
        k.Svc.CoopAdd("Марко", 25, Wed);
        k.Svc.Settle(Settled);
        Assert.Empty(k.Svc.PrizesFor("Оля", Settled));
        Assert.Contains(k.Wire.Chats, c => c.Contains("не довезли: 400 з 500"));
        var next = k.Svc.CoopNow(Settled)!;
        Assert.Equal("2026-11-15", next.Week);
        Assert.Equal(0, next.Done);
        Assert.NotEqual(Week, next.Week);
        Assert.Equal(0, k.Svc.CoopAdd("Оля", 10, Wed));                                 // стара тиждень — не приймає
    }

    // ---------- у грі ----------

    static (RoomHarness H, Kit K) Wheel(string nick = "Оля", int seed = 1)
    {
        var k = new Kit();
        var sp = new ServiceCollection().AddSingleton(k.Svc).BuildServiceProvider();
        var h = new RoomHarness("clicker", seed: seed, services: sp);
        h.Clock.UtcNow = Wed;
        h.Solo(nick);
        k.Svc.Settle(Wed);                                                              // замовлення: горщик ×500, якість 3+
        Board(h);                                                                       // на дошці сіл порожньо: випадкове замовлення відклало б вироби
        return (h, k);
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
        var bag = new JsonObject();
        foreach (var (key, n) in items) bag[key] = n;
        s["craft"]!["items"] = bag;
    });

    static JsonObject VillageOrder(RoomHarness h, int id, string ware, int quality, int count, double mult = 2.5) => new()
    {
        ["id"] = id, ["village"] = Clicker.FairVillages[0].Key, ["who"] = 0, ["ware"] = ware, ["style"] = "", ["quality"] = quality,
        ["count"] = count, ["mult"] = mult, ["at"] = h.Clock.UtcNow.ToString("O"),
        ["until"] = (h.Clock.UtcNow + TimeSpan.FromMinutes(30)).ToString("O"), ["lord"] = false, ["sour"] = false,
    };

    static void Board(RoomHarness h, params JsonObject[] orders) => Patch(h, s =>
    {
        var fair = s["fair"]!.AsObject();
        fair["orders"] = new JsonArray(orders.Select(o => (JsonNode)o).ToArray());
        fair["orderNext"] = (h.Clock.UtcNow + TimeSpan.FromHours(5)).ToString("O");
    });

    static ActResult Coop(RoomHarness h, int? n = null) => h.Act(0, "guild", n is { } c ? new { op = "coop", n = c } : new { op = "coop" });
    static ActResult Spin(RoomHarness h) => h.Act(0, "spin", PotterHands.Human(5));
    static long Pots(RoomHarness h) => h.View(0).GetProperty("pots").GetInt64();
    static double PerSecond(RoomHarness h) => h.View(0).GetProperty("perSecond").GetDouble();
    static void Rich(RoomHarness h) => Patch(h, s => s["upgrades"] = new JsonObject { ["apprentice"] = 40, ["wheel"] = 30 });

    static int Have(RoomHarness h, string key) =>
        h.View(0).GetProperty("craft").GetProperty("items").EnumerateArray().Where(i => i.GetProperty("key").GetString() == key)
            .Select(i => i.GetProperty("n").GetInt32()).FirstOrDefault();

    [Fact]
    public void The_potter_brings_good_wares_from_his_store_and_the_service_counts_them()
    {
        var (h, k) = Wheel();
        Items(h, ("pot||3", 20), ("pot||2", 30), ("jug||3", 5));
        var r = Coop(h, 50);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(20, k.Svc.CoopMine("Оля", Wed));                                   // лише якість 3+, лише потрібний виріб
        Assert.Equal(0, Have(h, "pot||3"));
        Assert.Equal(30, Have(h, "pot||2"));
        Assert.Equal(5, Have(h, "jug||3"));
        Assert.Contains("20 × горщик", r.Message);
        Assert.Contains("округа 20 з 500", r.Message);
        Assert.Contains("до бонусу ще 5", r.Message);                                   // поріг 25
        Assert.Equal(20, h.View(0).GetProperty("week").GetProperty("coop").GetProperty("mine").GetInt32());
    }

    [Fact]
    public void Wares_set_aside_for_a_village_order_are_not_taken()
    {
        var (h, k) = Wheel();
        Items(h, ("pot||3", 20));
        Board(h, VillageOrder(h, 100, "pot", 3, 15));
        var r = Coop(h);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(5, k.Svc.CoopMine("Оля", Wed));                                    // 20 − 15 відкладено для села
        Assert.Equal(15, Have(h, "pot||3"));
        var again = Coop(h);
        Assert.False(again.Ok);
        Assert.Contains("Нема вільних", again.Message);
    }

    [Fact]
    public void Delivering_nothing_or_too_much_or_with_no_order_fails_politely()
    {
        var (h, k) = Wheel();
        Assert.False(Coop(h, 5).Ok);                                                    // комора порожня
        Items(h, ("pot||3", 300));
        Assert.False(Coop(h, 0).Ok);
        Assert.Equal(0, k.Svc.CoopMine("Оля", Wed));
        var big = Coop(h, 600);                                                         // просить більше, ніж є: везе все вільне, але не вище стелі 25 %
        Assert.True(big.Ok, big.Message);
        Assert.Equal(125, k.Svc.CoopMine("Оля", Wed));
        Assert.Equal(175, Have(h, "pot||3"));
        var capped = Coop(h, 10);                                                       // стеля вичерпана — відмова, нічого не списано
        Assert.False(capped.Ok);
        Assert.Contains("стеля", capped.Message);
        Assert.Equal(175, Have(h, "pot||3"));
        k.Svc.CoopAdd("Тарас", 125, Wed);
        k.Svc.CoopAdd("Іван", 125, Wed);
        k.Svc.CoopAdd("Марко", 125, Wed);
        var done = Coop(h, 10);
        Assert.False(done.Ok);
        Assert.Contains("виконала", done.Message);

        var bare = new RoomHarness("clicker");                                          // без сервісу
        bare.Solo("Оля");
        Assert.False(Coop(bare, 5).Ok);
        var h2 = new RoomHarness("clicker", services: new ServiceCollection().AddSingleton(new Kit().Svc).BuildServiceProvider());
        h2.Clock.UtcNow = Wed;
        h2.Solo("Оля");
        Assert.Contains("ще нема", Coop(h2, 5).Message);                                // замовлення на тиждень не заведено
    }

    [Fact]
    public void The_coop_pays_by_the_ware_price_without_the_village_play_minutes_floor()
    {
        var (a, _) = Wheel();
        Items(a, ("pot||3", 3));
        Board(a, VillageOrder(a, 100, "pot", 3, 3));
        var before = Pots(a);
        Assert.True(a.Act(0, "fair", new { op = "deliver", id = 100 }).Ok);
        var village = Pots(a) - before;

        var (b, _) = Wheel();
        Items(b, ("pot||3", 3));
        var beforeB = Pots(b);
        var cr = Coop(b, 3);
        Assert.True(cr.Ok, cr.Message);
        var coop = Pots(b) - beforeB;

        Assert.True(village > 0);
        Assert.True(coop > 0);
        // Село має підлогу за хвилинами гри, довіз — лише ціна виробу (рецензія v13): свіжому гончареві, де хвилина гри
        // вагоміша за ціну горщика, село платить більше.
        Assert.True(village > coop);
    }

    [Fact]
    public void Nine_percent_is_not_enough_five_is_and_the_bonus_reaches_the_potter_only_after_the_settle()
    {
        var (h, k) = Wheel("Оля");
        Rich(h);
        Spin(h);
        var plain = PerSecond(h);
        Items(h, ("pot||3", 400));
        var nine = Coop(h, 24);
        Assert.True(nine.Ok, nine.Message);
        Assert.Contains("до бонусу ще 1", nine.Message);
        var r = Coop(h, 1);
        Assert.Contains("на бонус вистачає", r.Message);
        k.Svc.CoopAdd("Тарас", 125, Wed);                                               // решту довозять друзі
        k.Svc.CoopAdd("Іван", 125, Wed);
        k.Svc.CoopAdd("Марко", 125, Wed);
        k.Svc.CoopAdd("Ігор", 100, Wed);
        Assert.True(k.Svc.CoopNow(Wed)!.Fulfilled);

        Assert.DoesNotContain("Спільне замовлення округи", Spin(h).Message);            // до підбиття нагороди нема
        Assert.Equal(plain, PerSecond(h), plain * 1e-9 + 1e-6);

        h.Clock.UtcNow = Settled;
        k.Svc.Settle(Settled);
        var got = Spin(h);
        Assert.Contains("Спільне замовлення округи: +10 % до всього до неділі", got.Message);
        Assert.Equal(plain * 1.1, PerSecond(h), plain * 1e-9 + 1e-6);
        Assert.Equal(10, h.View(0).GetProperty("buffs").GetProperty("week").GetProperty("coop").GetProperty("pct").GetDouble(), 6);
        Assert.DoesNotContain("Спільне замовлення округи", Spin(h).Message);            // забрано раз

        h.Clock.UtcNow = Ends().AddDays(7).AddMinutes(1);                               // за сім діб згасло
        Spin(h);
        Assert.Equal(plain, PerSecond(h), plain * 1e-9 + 1e-6);
    }

    [Fact]
    public void Sails_and_the_coop_bonus_stack_as_two_multipliers()
    {
        var k = new Kit();
        k.Season();
        var sp = new ServiceCollection().AddSingleton(k.Svc).BuildServiceProvider();
        var h = new RoomHarness("clicker", services: sp);
        h.Clock.UtcNow = Wed;
        h.Solo("Оля");
        k.Svc.Settle(Wed);
        Rich(h);
        Spin(h);
        var plain = PerSecond(h);
        k.Svc.CoopAdd("Оля", 500, Wed);
        k.Svc.CoopAdd("Тарас", 125, Wed);
        k.Svc.CoopAdd("Іван", 125, Wed);
        k.Svc.CoopAdd("Марко", 125, Wed);
        h.Clock.UtcNow = Settled;
        k.Svc.Settle(Settled);
        Spin(h);
        Assert.Equal(plain * 1.2 * 1.1, PerSecond(h), plain * 1e-9 + 1e-6);
    }

    // ---------- рецензія v13 ----------

    [Fact]
    public void One_potter_cannot_close_the_order_alone_his_ceiling_is_a_quarter_of_the_volume()
    {
        var k = new Kit();
        Chosen(k, out _, out _, out var volume);
        Assert.Equal(125, ClickerWeekService.MaxPart(volume, k.Svc.O.CoopMaxShare, k.Svc.O.CoopMinShare));
        Assert.Equal(125, k.Svc.CoopAdd("Оля", 500, Wed));
        Assert.Equal(0, k.Svc.CoopAdd("Оля", 1, Wed));                                  // стеля вичерпана
        Assert.Equal(0, k.Svc.CoopRoom("Оля", Wed));
        Assert.Equal(125, k.Svc.CoopRoom("Тарас", Wed));
        Assert.False(k.Svc.CoopNow(Wed)!.Fulfilled);
        Assert.Equal(125, k.Svc.CoopMine("Оля", Wed));
    }
}
