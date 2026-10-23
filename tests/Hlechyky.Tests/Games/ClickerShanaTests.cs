using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Шана старійшини (ClickerShana.cs, docs/games/specs/clicker-shana.md): п'ять гілок від лічильників за весь час,
/// перки лише в старійшини, Журнал і звання, і кожен перк — у своєму пакеті (горно, вози, сусідство, ярмарок, кораблі).
/// </summary>
public class ClickerShanaTests
{
    static readonly DateTimeOffset Thursday = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    sealed class Tsekh
    {
        public FakeStore Store { get; } = new();
        public FakeClock Clock { get; } = new();
        public ClickerGuildService Svc { get; }
        public Tsekh() => Svc = new ClickerGuildService(Store, Clock);

        public RoomHarness Potter(string nick)
        {
            var h = new RoomHarness("clicker", services: RoomHarness.WithService(Svc));
            h.Solo(nick);
            Assert.True(h.Act(0, "look").Ok);
            Publish(h, nick);
            return h;
        }

        public void Publish(RoomHarness h, string nick)
        {
            lock (h.Room.Sync) Store.SaveState("clicker:" + ClickerGuildService.Key(nick), h.Room.Game.Save()!);
        }
    }

    /// <summary>Гончар зі своїм цехом — як на сайті (без сервісу вид цеху короткий і без шани).</summary>
    static RoomHarness Wheel(string nick = "Оля") => new Tsekh().Potter(nick);

    static JsonElement View(RoomHarness h) => h.View(0);
    static double Pots(RoomHarness h) => View(h).GetProperty("pots").GetDouble();
    static ActResult Guild(RoomHarness h, object payload) => h.Act(0, "guild", payload);
    static IEnumerable<string> Journal(RoomHarness h) => h.Outbox.OfType<Journal>().Select(j => j.Text);

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static JsonObject GuildRow(JsonObject s) => (s["guild"] as JsonObject) ?? (JsonObject)(s["guild"] = new JsonObject());

    /// <summary>Ранг і лічильники гілок просто в збереження: обпалено (горщиками), на вози, розкішних, підмайстрів, замовлень.</summary>
    static void Shana(RoomHarness h, int rank = Clicker.GuildElder, long fired = 0, long given = 0, long lux = 0, int lends = 0,
        int delivered = 0, int gifts = 0, int treats = 0) => Patch(h, s =>
    {
        var g = GuildRow(s);
        g["rank"] = rank;
        g["given"] = given;
        g["luxFired"] = lux;
        g["lendsSent"] = lends;
        g["giftsSent"] = gifts;
        g["treatsSent"] = treats;
        s["craft"]!["firedBy"] = new JsonObject { ["pot"] = fired };
        s["fair"]!["delivered"] = delivered;
    });

    static void Items(RoomHarness h, params (string Key, int N)[] items) => Patch(h, s =>
    {
        var bag = new JsonObject();
        foreach (var (key, n) in items) bag[key] = n;
        s["craft"]!["items"] = bag;
    });

    static JsonElement Track(RoomHarness h, string key) =>
        View(h).GetProperty("guild").GetProperty("shana").GetProperty("tracks").EnumerateArray().First(t => t.GetProperty("key").GetString() == key);

    // ---------- щаблі ----------

    [Fact]
    public void Levels_come_from_all_time_counters_but_perks_wait_for_the_elder()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        var slots = View(h).GetProperty("kiln").GetProperty("slots").GetInt32();

        // Цехмістр із горою обпаленого: щаблі вже набрано (вид їх показує), але перків нема — лише місця майстра.
        Shana(h, rank: Clicker.GuildMasterOfGuild, fired: 300_000);
        Assert.False(View(h).GetProperty("guild").GetProperty("shana").GetProperty("on").GetBoolean());
        Assert.Equal(5, Track(h, Clicker.ShanaKiln).GetProperty("level").GetInt32());
        Assert.Equal(slots + Clicker.MasterKilnSlots, View(h).GetProperty("kiln").GetProperty("slots").GetInt32());

        // Старійшина: ті самі лічильники — і ще +8 місць шани понад старійшинські.
        Shana(h, rank: Clicker.GuildElder, fired: 300_000);
        Assert.True(View(h).GetProperty("guild").GetProperty("shana").GetProperty("on").GetBoolean());
        Assert.Equal(slots + 2 * Clicker.ShanaKilnSlotsStep + Clicker.ElderKilnSlots,
            View(h).GetProperty("kiln").GetProperty("slots").GetInt32());

        // Пороги: 29 999 — перший щабель, 30 000 — другий; наступний поріг у виді.
        Shana(h, fired: 29_999);
        Assert.Equal(1, Track(h, Clicker.ShanaKiln).GetProperty("level").GetInt32());
        Assert.Equal(30_000, Track(h, Clicker.ShanaKiln).GetProperty("next").GetInt64());
        Shana(h, fired: 30_000);
        Assert.Equal(2, Track(h, Clicker.ShanaKiln).GetProperty("level").GetInt32());
        Assert.Equal(Clicker.ShanaKilnCool.TotalMilliseconds, View(h).GetProperty("kiln").GetProperty("coolMs").GetDouble());

        // Сусідство рахує дарунки, гостинці й підмайстрів разом.
        Shana(h, gifts: 20, treats: 20, lends: 10);
        Assert.Equal(50, Track(h, Clicker.ShanaKind).GetProperty("have").GetInt64());
        Assert.Equal(1, Track(h, Clicker.ShanaKind).GetProperty("level").GetInt32());
    }

    [Fact]
    public void New_levels_go_to_the_journal_once_with_a_toast()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        Shana(h, fired: 31_000, given: 6_500, delivered: 320);
        var r = h.Act(0, "look");
        Assert.True(r.Ok);
        var line = Assert.Single(Journal(h), j => j.Contains("шана старійшини"));
        Assert.Contains("🔥 Горно II", line);
        Assert.Contains("🛒 Вози II", line);
        Assert.Contains("🎪 Ярмарок I", line);
        Assert.Contains("Шана старійшини", r.Message);

        // Удруге — тиша; новий щабель — новий рядок і тост про нього самого.
        Assert.True(h.Act(0, "look").Ok);
        Assert.Single(Journal(h), j => j.Contains("шана старійшини"));
        Patch(h, s => GuildRow(s)["given"] = 12_000);
        r = h.Act(0, "look");
        Assert.Contains("Вози III", r.Message);
        Assert.Equal(2, Journal(h).Count(j => j.Contains("шана старійшини")));

        // Збережене «вже сказано» переживає перезапуск.
        Patch(h, _ => { });
        Assert.True(h.Act(0, "look").Ok);
        Assert.Equal(2, Journal(h).Count(j => j.Contains("шана старійшини")));
    }

    [Fact]
    public void Not_an_elder_hears_nothing_yet()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        Shana(h, rank: Clicker.GuildMasterOfGuild, fired: 300_000, given: 60_000);
        Assert.True(h.Act(0, "look").Ok);
        Assert.DoesNotContain(Journal(h), j => j.Contains("шана старійшини"));
    }

    [Fact]
    public void Old_save_starts_the_luxury_counter_from_what_lies_in_the_store()
    {
        var h = new Tsekh().Potter("Оля");
        Patch(h, s =>
        {
            s["craft"]!["items"] = new JsonObject { ["pot|kosiv|4"] = 7, ["jug|kosiv|4"] = 3, ["jug|kosiv|3"] = 40 };
            GuildRow(s).Remove("luxFired");
            GuildRow(s)["rank"] = Clicker.GuildElder;
        });
        Assert.Equal(10, Track(h, Clicker.ShanaLux).GetProperty("have").GetInt64());
        // А збережений лічильник комора вже не чіпає.
        Patch(h, s => GuildRow(s)["luxFired"] = 2);
        Assert.Equal(2, Track(h, Clicker.ShanaLux).GetProperty("have").GetInt64());
    }

    // ---------- горно й розкіш ----------

    [Fact]
    public void Luxury_bonus_takes_from_good_and_plain_but_keeps_the_ringing_share()
    {
        // Ідеальна ручна партія: 25 % розкішних, 5 % дзвінких.
        Assert.Equal(0.25, KilnHeat.LuxChance(1, 1), 6);
        Assert.Equal(0.2875, KilnHeat.LuxChance(1, 1, 1.15), 6);
        static double Share(double luxMult, int q)
        {
            var n = 0;
            for (var i = 0; i < 100_000; i++) if (KilnHeat.QualityOf(1, 1, (i + 0.5) / 100_000, luxMult) == q) n++;
            return n / 100_000.0;
        }
        Assert.Equal(0.25 * 1.45, Share(1.45, 4), 3);
        Assert.Equal(Share(1, 3), Share(1.45, 3), 3);
        Assert.Equal(0.4, Share(1.45, 2), 3);
        // Без розпису розкішних нема, хоч який множник.
        Assert.Equal(0, KilnHeat.LuxChance(1, 0, 1.45));
        // Палій з блиском ~0,78 і красою 15 — ~0,34 %, з красою 30 — ~1,4 % (калібрування 21.10).
        Assert.InRange(KilnHeat.LuxChance(0.783, Clicker.ShanaAutoPaint1), 0.0030, 0.0040);
        Assert.InRange(KilnHeat.LuxChance(0.783, Clicker.ShanaAutoPaint4), 0.0130, 0.0145);
    }

    // ---------- вози ----------

    [Fact]
    public void Wagon_pays_for_items_and_counts_them_one_and_a_half()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");

        // Без перку віз за вироби не платить.
        Shana(h, given: 0);
        Items(h, ("pot||1", 10));
        var before = Pots(h);
        var r = Guild(h, new { op = "give", key = "pot||1", n = 10, keep = false });
        Assert.True(r.Ok, r.Message);
        Assert.DoesNotContain("за вироби", r.Message);
        Assert.Equal(10, g.Svc.Summary("оля", h.Clock.UtcNow).Today.Mine);

        // Вози III: плата за вироби і вага ×1,5 — десять виробів кладуться як п'ятнадцять; непарні не губляться.
        Shana(h, given: 12_000);
        Items(h, ("pot||1", 10));
        before = Pots(h);
        r = Guild(h, new { op = "give", key = "pot||1", n = 10, keep = false });
        Assert.True(r.Ok, r.Message);
        Assert.Contains("за вироби", r.Message);
        Assert.True(Pots(h) > before);
        Assert.Equal(25, g.Svc.Summary("оля", h.Clock.UtcNow).Today.Mine);
        Items(h, ("pot||1", 1));
        Assert.True(Guild(h, new { op = "give", key = "pot||1", n = 1, keep = false }).Ok);
        Assert.Equal(26, g.Svc.Summary("оля", h.Clock.UtcNow).Today.Mine);
        Items(h, ("pot||1", 1));
        Assert.True(Guild(h, new { op = "give", key = "pot||1", n = 1, keep = false }).Ok);
        Assert.Equal(28, g.Svc.Summary("оля", h.Clock.UtcNow).Today.Mine);
        // Підцілі видів — справжні вироби, без ваги.
        var day = g.Svc.Summary("оля", h.Clock.UtcNow).Today;
        Assert.Equal(22, day.Items!.Sum(x => x.N));

        // Вози V: нагорода воза ×1,5 понад старійшинські ×2.
        Shana(h, given: 50_000);
        Assert.Equal(Clicker.ElderWagon * Clicker.ShanaWagonMult, View(h).GetProperty("guild").GetProperty("wagonMult").GetDouble(), 6);
    }

    [Fact]
    public void An_oboz_wagon_gives_every_giver_ten_more_minutes_once_a_day()
    {
        var g = new Tsekh();
        var a = g.Potter("Оля");
        var b = g.Potter("Петро");
        // Оля — обозна (Вози IV); Петро — звичайний гончар.
        Shana(a, given: 25_000);
        Items(a, ("pot||1", 30));
        Assert.True(Guild(a, new { op = "give", key = "pot||1", n = 30, keep = false }).Ok);
        Assert.True(View(b).GetProperty("guild").GetProperty("oboz").GetBoolean());
        // Петро докладає до бронзи й забирає — з обозною доплатою; удруге того дня її нема.
        Items(b, ("pot||1", 40));
        Assert.True(Guild(b, new { op = "give", key = "pot||1", n = 40, keep = false }).Ok);
        var r = Guild(b, new { op = "claim" });
        Assert.True(r.Ok, r.Message);
        Assert.Contains("віз обозного", r.Message);
        Assert.Equal((true, true), g.Svc.Oboz("петро", ClickerGuildService.DayOf(b.Clock.UtcNow)));
        Assert.Equal((true, false), g.Svc.Oboz("оля", ClickerGuildService.DayOf(b.Clock.UtcNow)));
    }

    // ---------- сусідство ----------

    [Fact]
    public void Kind_neighbour_sends_two_apprentices_and_more_generous_help()
    {
        var g = new Tsekh();
        var a = g.Potter("Оля");
        g.Potter("Петро");
        g.Potter("Ганна");
        g.Potter("Іван");

        // Без шани — один підмайстер на день.
        Assert.True(Guild(a, new { op = "lend", to = "Петро" }).Ok);
        Assert.False(Guild(a, new { op = "lend", to = "Ганна" }).Ok);

        // Сусідство IV: другий підмайстер сьогодні ще можна, третій — ні.
        Shana(a, gifts: 200, treats: 200);
        Assert.True(View(a).GetProperty("guild").GetProperty("help").GetProperty("lendLeft").GetBoolean());
        Assert.True(Guild(a, new { op = "lend", to = "Ганна" }).Ok);
        Assert.False(Guild(a, new { op = "lend", to = "Іван" }).Ok);
        Assert.Equal(1, Track(a, Clicker.ShanaKind).GetProperty("have").GetInt64() - 400);

        // Гостинець удвічі щедріший, похвала на дві години, дарунок — із гостинцем.
        Patch(a, s => s["pots"] = 1e30);
        Assert.True(Guild(a, new { op = "treat", to = "Іван", minutes = 10 }).Ok);
        Assert.True(Guild(a, new { op = "cheer", to = "Іван" }).Ok);
        Items(a, ("pot||1", 1));
        var gift = Guild(a, new { op = "gift", nick = "Іван", key = "pot||1", keep = false });
        Assert.True(gift.Ok, gift.Message);
        var box = g.Svc.TakeBoosts("іван")!;
        Assert.Contains(box, x => x.Kind == "treat" && x.Minutes == 10 * ClickerGuildService.TreatBack * 2);
        Assert.Contains(box, x => x.Kind == "cheer" && x.Minutes == 2 * ClickerGuildService.CheerMinutes);
        Assert.Contains(box, x => x.Kind == "treat" && x.Minutes == Clicker.ShanaGiftTreatMinutes * ClickerGuildService.TreatBack);
        // Гостинець із дарунком у денну стелю Івана не йде: туди пішло лише звичайне подвоєння гостинця.
        Assert.Equal(ClickerGuildService.TreatCapMinutes - 10 * ClickerGuildService.TreatBack, g.Svc.Help("іван", a.Clock.UtcNow).TreatLeft);
    }

    // ---------- ярмарок ----------

    static JsonObject Order(RoomHarness h, int id, string ware, int quality, int count, double mult = 2, int minutes = 30, bool lord = false) => new()
    {
        ["id"] = id, ["village"] = "opishnia", ["who"] = 0, ["ware"] = ware, ["style"] = "", ["quality"] = quality,
        ["count"] = count, ["mult"] = mult, ["at"] = h.Clock.UtcNow.ToString("O"),
        ["until"] = (h.Clock.UtcNow + TimeSpan.FromMinutes(minutes)).ToString("O"), ["lord"] = lord, ["sour"] = false,
    };

    static void Board(RoomHarness h, params JsonObject[] orders) => Patch(h, s =>
    {
        var fair = s["fair"]!.AsObject();
        fair["orders"] = new JsonArray(orders.Select(o => (JsonNode)o).ToArray());
        fair["orderNext"] = (h.Clock.UtcNow + TimeSpan.FromHours(5)).ToString("O");
    });

    static List<int> OrderIds(RoomHarness h) =>
        View(h).GetProperty("market").GetProperty("orders").EnumerateArray().Select(o => o.GetProperty("id").GetInt32()).ToList();

    [Fact]
    public void The_clerk_delivers_plain_orders_with_free_stock_but_leaves_lords_to_you()
    {
        var h = Wheel();
        var board = View(h).GetProperty("market").GetProperty("boardMax").GetInt32();
        Shana(h, delivered: 1_200);
        Assert.Equal(board + 1, View(h).GetProperty("market").GetProperty("boardMax").GetInt32());
        // Вид теж синхронізує (і прикажчик працює вже в ньому): глеки міряємо раніше, а дошку й комору кладемо без видів.
        var before = Pots(h);
        Items(h, ("pot||1", 10), ("bowl||1", 2));
        Board(h, Order(h, 1, "pot", 1, 3), Order(h, 2, "pot", 1, 15), Order(h, 3, "pot", 1, 2, mult: 4, lord: true),
            // Замовлення, що вже минуло, поки гончаря не було, — прикажчик теж устиг здати.
            Order(h, 4, "bowl", 1, 2, minutes: -10));
        Assert.True(h.Act(0, "look").Ok);
        var ids = OrderIds(h);
        Assert.DoesNotContain(1, ids);
        Assert.Contains(2, ids);     // товару бракує
        Assert.Contains(3, ids);     // панське — тобі
        Assert.DoesNotContain(4, ids);
        Assert.True(Pots(h) > before);
        Assert.Equal(1_202, Track(h, Clicker.ShanaFair).GetProperty("have").GetInt64());
        var clerk = View(h).GetProperty("market").GetProperty("clerk");
        Assert.Equal(2, clerk.GetProperty("n").GetInt32());

        // Нижче третього щабля прикажчика нема.
        var h2 = Wheel();
        Shana(h2, delivered: 600);
        Board(h2, Order(h2, 1, "pot", 1, 3));
        Items(h2, ("pot||1", 10));
        Assert.True(h2.Act(0, "look").Ok);
        Assert.Contains(1, OrderIds(h2));
    }

    [Fact]
    public void Fair_pays_more_and_a_luxury_lord_order_survives_a_reload()
    {
        var h = Wheel();
        Board(h, Order(h, 1, "pot", 1, 3, minutes: 60));
        var plain = View(h).GetProperty("market").GetProperty("orders")[0].GetProperty("mult").GetDouble();
        Shana(h, delivered: 2_500);
        Board(h, Order(h, 1, "pot", 1, 3, minutes: 60), Order(h, 2, "pot", 4, 2, mult: Clicker.ShanaLuxLordMult, minutes: 60, lord: true));
        var orders = View(h).GetProperty("market").GetProperty("orders").EnumerateArray().ToList();
        Assert.Equal(plain * Clicker.ShanaFairPay, orders[0].GetProperty("mult").GetDouble(), 6);
        Assert.Equal(2, orders.Count);
        Assert.Equal(4, orders[1].GetProperty("q").GetInt32());
    }

    // ---------- кораблі ----------

    static string[] Upto(string key) => Clicker.Buildings.Select(b => b.Key).TakeWhile(k => k != key).Append(key).ToArray();

    [Fact]
    public void A_ship_takes_a_luxury_piece_for_two_places()
    {
        var h = Wheel();
        Patch(h, s =>
        {
            s["pots"] = 1e40;
            s["total"] = 1e40;
            s["toloka"] = new JsonObject { ["built"] = new JsonArray(Upto("pier").Select(x => (JsonNode)x).ToArray()) };
            var now = h.Clock.UtcNow;
            s["trade"] = new JsonObject
            {
                ["slava"] = 0, ["firstDone"] = true, ["next"] = now.AddMinutes(40).ToString("O"), ["shipId"] = 1,
                ["ships"] = new JsonArray(new JsonObject
                {
                    ["id"] = 1, ["port"] = "delft", ["ware"] = "jug", ["style"] = "", ["quality"] = 3, ["count"] = 10, ["lux"] = 4,
                    ["at"] = now.ToString("O"), ["until"] = now.AddHours(8).ToString("O"),
                }),
            };
        });
        Shana(h, lux: 300);
        Items(h, ("jug||4", 3), ("jug||3", 3));
        Patch(h, s => s["fair"]!["orders"] = new JsonArray());
        var r = h.Act(0, "trade", new { @do = "loadAll", id = 1, n = 1 });
        Assert.True(r.Ok, r.Message);
        // Два розкішні закрили чотири розкішні місця, третій розкішний — ще два звичайні, і три дзвінкі — решта:
        // 2·2 + 2 + 3 = 9 з 10 за шість виробів.
        var ship = View(h).GetProperty("trade").GetProperty("ships").EnumerateArray().First(x => x.GetProperty("id").GetInt32() == 1);
        Assert.Equal(4, ship.GetProperty("loadedLux").GetInt32());
        Assert.Equal(9, ship.GetProperty("loaded").GetInt32());
    }

    // ---------- звання ----------

    [Fact]
    public void Fifth_levels_bring_titles_and_all_five_make_a_legend()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        Shana(h, fired: 250_000, given: 50_000, lux: 10_000, gifts: 800, delivered: 5_000);
        Assert.True(h.Act(0, "look").Ok);
        var titles = View(h).GetProperty("titles").GetProperty("earned").EnumerateObject().Select(x => x.Name).ToList();
        Assert.Contains("oboz", titles);
        Assert.Contains("kindsoul", titles);
        Assert.Contains("legend", titles);
        Assert.Contains(Journal(h), j => j.Contains("Легенда Опішні"));
    }
}
