using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Записки друзів (28.09), пакет «ui» Гончарного кола: Скалка з неба напевно з першою зіркою (і заднім числом тим, у
/// кого зірку вже спіймано), кіт і зірка не «гаснуть» від самої синхронізації, «🛒 Усе на віз» однією дією з
/// відкладеним під замовлення, віз пам'ятає розпис і якість покладеного, смужка бафів знає, скільки триває весь баф.
/// </summary>
public class ClickerFeedbackUiTests
{
    // Годинник RoomHarness стоїть на четвер 2026-09-10 12:00 UTC — це 15:00 у Києві, київський день 2026-09-10.
    static readonly DateTimeOffset Thursday = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    static RoomHarness Wheel(string nick = "Оля", int seed = 1)
    {
        var h = new RoomHarness("clicker", seed: seed);
        h.Solo(nick);
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static ActResult Act(RoomHarness h, string action, object? payload = null) => h.Act(0, action, payload);

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static JsonObject Saved(RoomHarness h)
    {
        lock (h.Room.Sync) return JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
    }

    /// <summary>Поставити подію сцени: <paramref name="field"/> — cat чи star, з'являється через <paramref name="after"/>.</summary>
    static void EventAt(RoomHarness h, string field, TimeSpan after, TimeSpan shown) => Patch(h, s =>
    {
        var at = h.Clock.UtcNow + after;
        s[field] = new JsonObject { ["at"] = at.ToString("O"), ["until"] = (at + shown).ToString("O"), ["a"] = 0, ["b"] = 0 };
    });

    static JsonElement WondersList(RoomHarness h) => View(h).GetProperty("house").GetProperty("wonders").GetProperty("list");

    static bool Found(RoomHarness h, string key) =>
        WondersList(h).EnumerateArray().Single(w => w.GetProperty("key").GetString() == key).GetProperty("found").GetBoolean();

    static int FoundCount(RoomHarness h) => View(h).GetProperty("house").GetProperty("wonders").GetProperty("found").GetInt32();

    // ---------- #3: Скалка з неба напевно ----------

    [Fact]
    public void The_first_caught_star_brings_the_sky_stone_for_sure()
    {
        // Кидок був 8 % (з люстром 16 %) — тепер жодне зерно не лишає гончаря без Скалки.
        for (var seed = 1; seed <= 25; seed++)
        {
            var h = Wheel(seed: seed);
            EventAt(h, "star", TimeSpan.Zero, Clicker.StarShown);
            var r = Act(h, "wish");
            Assert.True(r.Ok, r.Message);
            Assert.True(Found(h, "sky-stone"), $"зерно {seed}");
            Assert.Equal(1, FoundCount(h));
            Assert.Contains(h.Awards, a => a.Reason == "ach:potter-wonder");
        }
    }

    [Fact]
    public void After_the_sky_stone_a_star_only_rolls_for_the_moon_and_nothing_twice()
    {
        var h = Wheel();
        EventAt(h, "star", TimeSpan.Zero, Clicker.StarShown);
        Assert.True(Act(h, "wish").Ok);
        Assert.True(Found(h, "sky-stone"));
        // Далі зірка — звичайний кидок, і в пулі лишився тільки глечик із місяцем.
        ClickerWonder? moon = null;
        for (var i = 0; i < 400 && moon is null; i++)
        {
            lock (h.Room.Sync)
            {
                var w = ((Clicker)h.Room.Game).RollWonder("star");
                if (w is not null) { Assert.Equal("moon", w.Key); moon = w; }
            }
        }
        Assert.NotNull(moon);
        lock (h.Room.Sync) Assert.Null(((Clicker)h.Room.Game).RollWonder("star"));
        Assert.Equal(2, FoundCount(h));
    }

    [Fact]
    public void Caught_stars_are_counted_in_the_save_and_survive_a_reload()
    {
        var h = Wheel();
        for (var i = 0; i < 2; i++)
        {
            EventAt(h, "star", TimeSpan.Zero, Clicker.StarShown);
            Assert.True(Act(h, "wish").Ok);
        }
        Assert.Equal(2, Saved(h)["house"]!["stars"]!.GetValue<int>());
        Patch(h, _ => { });
        Assert.Equal(2, Saved(h)["house"]!["stars"]!.GetValue<int>());
        Assert.Equal(1, FoundCount(h));
    }

    [Fact]
    public void An_old_save_without_the_star_counter_reads_as_no_stars_and_gets_nothing_extra()
    {
        var h = Wheel();
        Patch(h, s => ((JsonObject)s["house"]!).Remove("stars"));
        Assert.False(Found(h, "sky-stone"));
        Assert.Equal(0, FoundCount(h));
        Assert.Equal(0, Saved(h)["house"]!["stars"]!.GetValue<int>());
        Assert.True(Act(h, "look").Ok);
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:potter-wonder");
    }

    [Fact]
    public void A_save_that_caught_a_star_without_the_sky_stone_gets_it_on_load_once()
    {
        // Заднім числом: зірку спіймано (лічильник є), а Скалки в хаті нема — хата віддає її на завантаженні.
        var h = Wheel();
        Patch(h, s => s["house"]!["stars"] = 3);
        Assert.True(Found(h, "sky-stone"));
        Assert.Equal(1, FoundCount(h));
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:potter-wonder");   // ачівка чекає дії — Load її не шле
        Assert.True(Act(h, "look").Ok);
        Assert.Single(h.Awards, a => a.Reason == "ach:potter-wonder");

        // Ще раз завантажили — нічого нового: знайдена вдруге не знаходиться, ачівка не дублюється.
        Patch(h, _ => { });
        Patch(h, _ => { });
        Assert.True(Act(h, "look").Ok);
        Assert.Equal(1, FoundCount(h));
        Assert.Single(h.Awards, a => a.Reason == "ach:potter-wonder");
    }

    [Fact]
    public void The_owed_sky_stone_survives_a_restart_before_the_first_action()
    {
        // Ачівка, поставлена в чергу на завантаженні, лежить у збереженні до першої дії — перезапуск її не губить.
        var h = Wheel();
        Patch(h, s => s["house"]!["stars"] = 1);
        var queued = Saved(h)["achievements"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Assert.Contains("potter-wonder", queued);
    }

    [Fact]
    public void Wonder_hints_say_may_and_only_the_sky_stone_promises()
    {
        var h = Wheel();
        foreach (var w in WondersList(h).EnumerateArray())
        {
            var from = w.GetProperty("from").GetString()!;
            if (w.GetProperty("key").GetString() == "sky-stone")
            {
                Assert.StartsWith("напевно", from);
                Assert.Contains("вночі", from);
            }
            else Assert.StartsWith("може знайтись", from);
        }
        Assert.Single(Clicker.Wonders, w => w.Sure is not null);
    }

    // ---------- кіт і зірка не гаснуть від самої синхронізації ----------

    [Fact]
    public void A_star_in_the_sky_waits_for_a_potter_who_only_watched()
    {
        // Гончар три хвилини дивився на сцену, нічого не клацаючи; зірка впала — перша ж дія (загадати) її не губить.
        var h = Wheel();
        EventAt(h, "star", TimeSpan.FromMinutes(3), Clicker.StarShown);
        h.Clock.Advance(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(2));
        Assert.True(Clicker.EventGap < TimeSpan.FromMinutes(3));
        var r = Act(h, "wish");
        Assert.True(r.Ok, r.Message);
        Assert.True(View(h).GetProperty("starWish").GetBoolean());
    }

    [Fact]
    public void A_wish_in_the_ping_grace_after_the_star_is_still_granted()
    {
        var h = Wheel();
        EventAt(h, "star", TimeSpan.Zero, Clicker.StarShown);
        h.Clock.Advance(Clicker.StarShown + Clicker.CatchGrace - TimeSpan.FromMilliseconds(300));
        var r = Act(h, "wish");
        Assert.True(r.Ok, r.Message);
    }

    [Fact]
    public void Past_the_grace_the_star_is_gone_and_the_next_is_scheduled()
    {
        var h = Wheel();
        EventAt(h, "star", TimeSpan.Zero, Clicker.StarShown);
        h.Clock.Advance(Clicker.StarShown + Clicker.CatchGrace + TimeSpan.FromSeconds(1));
        var r = Act(h, "wish");
        Assert.False(r.Ok);
        Assert.Contains("згасла", r.Message);
        var next = View(h).GetProperty("events").GetProperty("star").GetProperty("at").GetDateTimeOffset();
        Assert.True(next > h.Clock.UtcNow);
    }

    [Fact]
    public void A_cat_on_the_stage_can_be_petted_after_a_quiet_spell()
    {
        var h = Wheel();
        EventAt(h, "cat", TimeSpan.FromMinutes(4), Clicker.CatShown);
        h.Clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(1));
        var r = Act(h, "pet");
        Assert.True(r.Ok, r.Message);
    }

    [Fact]
    public void An_event_that_came_while_nobody_watched_is_still_rescheduled_from_now()
    {
        // Те, заради чого EventGap і є: зірка пройшла, поки гончаря не було, — наступна від «зараз», а не з минулого.
        var h = Wheel();
        EventAt(h, "star", TimeSpan.FromMinutes(1), Clicker.StarShown);
        h.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.True(Act(h, "look").Ok);
        var star = View(h).GetProperty("events").GetProperty("star");
        Assert.True(star.GetProperty("at").GetDateTimeOffset() > h.Clock.UtcNow);
    }

    // ---------- плашки бафів: скільки триває весь баф ----------

    [Fact]
    public void Fair_and_inspiration_tell_their_full_span_for_the_melting_bar()
    {
        var h = Wheel();
        var v = View(h);
        Assert.Equal(Clicker.FairFor.TotalSeconds, v.GetProperty("fair").GetProperty("span").GetDouble());
        Assert.Equal(Clicker.InspireFor.TotalSeconds, v.GetProperty("inspire").GetProperty("span").GetDouble());
        // «Довгий ярмарок» подовжує обидва вдвічі — і смужка мусить знати саме це.
        Patch(h, s => s["secrets"] = new JsonArray("longfair"));
        v = View(h);
        Assert.Equal(2 * Clicker.FairFor.TotalSeconds, v.GetProperty("fair").GetProperty("span").GetDouble());
        Assert.Equal(2 * Clicker.InspireFor.TotalSeconds, v.GetProperty("inspire").GetProperty("span").GetDouble());
    }

    // ---------- #4, #8: віз ----------

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
            return h;
        }
    }

    static JsonElement G(RoomHarness h) => View(h).GetProperty("guild");
    static ActResult Guild(RoomHarness h, object payload) => h.Act(0, "guild", payload);

    static void Items(RoomHarness h, params (string Key, int N)[] items) => Patch(h, s =>
    {
        var bag = new JsonObject();
        foreach (var (key, n) in items) bag[key] = n;
        s["craft"]!["items"] = bag;
    });

    static Dictionary<string, int> Store(RoomHarness h) =>
        (Saved(h)["craft"]!["items"] as JsonObject ?? new JsonObject()).ToDictionary(x => x.Key, x => x.Value!.GetValue<int>());

    /// <summary>Чисто: жодних замовлень сіл і гостей (свіжа гра могла вже вивісити дошку сіл), наступне — нескоро.</summary>
    static void NoOrders(RoomHarness h) => Patch(h, s =>
    {
        if (s["fair"] is JsonObject f) { f["orders"] = new JsonArray(); f["orderNext"] = (h.Clock.UtcNow + TimeSpan.FromHours(3)).ToString("O"); }
        if (s["guests"] is JsonObject g) g["orders"] = new JsonArray();
    });

    static void GuestOrder(RoomHarness h, string ware, string style, int quality, int count, TimeSpan life) => Patch(h, s =>
    {
        var g = (s["guests"] as JsonObject) ?? (JsonObject)(s["guests"] = new JsonObject());
        var list = (g["orders"] as JsonArray) ?? (JsonArray)(g["orders"] = new JsonArray());
        var now = h.Clock.UtcNow;
        list.Add(new JsonObject
        {
            ["id"] = 900 + list.Count, ["guest"] = "tsargrad", ["who"] = 0, ["ware"] = ware, ["style"] = style, ["quality"] = quality,
            ["count"] = count, ["at"] = now.ToString("O"), ["until"] = (now + life).ToString("O"),
        });
        var met = (g["met"] as JsonObject) ?? (JsonObject)(g["met"] = new JsonObject());
        met["tsargrad"] = now.ToString("O");
    });

    static void VillageOrder(RoomHarness h, string ware, int quality, int count) => Patch(h, s =>
    {
        var f = (s["fair"] as JsonObject) ?? (JsonObject)(s["fair"] = new JsonObject());
        var list = (f["orders"] as JsonArray) ?? (JsonArray)(f["orders"] = new JsonArray());
        var now = h.Clock.UtcNow;
        list.Add(new JsonObject
        {
            ["id"] = 700 + list.Count, ["village"] = "opishnia", ["who"] = 0, ["ware"] = ware, ["style"] = "", ["quality"] = quality,
            ["count"] = count, ["mult"] = 2.0, ["at"] = now.ToString("O"), ["until"] = (now + TimeSpan.FromMinutes(20)).ToString("O"),
            ["lord"] = false, ["sour"] = false,
        });
    });

    [Fact]
    public void Everything_goes_on_the_wagon_in_one_action_and_one_write()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        NoOrders(h);
        Items(h, ("pot||1", 10), ("bowl|kosiv|2", 3), ("tile||1", 5));
        Assert.Equal(18, G(h).GetProperty("all").GetProperty("n").GetInt32());
        Assert.Equal(0, G(h).GetProperty("all").GetProperty("keep").GetInt32());
        var writes = g.Store.Saves;

        var r = Guild(h, new { op = "giveAll" });

        Assert.True(r.Ok, r.Message);
        Assert.Contains("18 виробів", r.Message);
        Assert.Empty(Store(h));
        // Один запис воза (і, буває, звіт звань цехові в тій самій дії) — а не вісімнадцять, як від натисків «+1».
        Assert.InRange(g.Store.Saves - writes, 1, 2);
        var day = G(h).GetProperty("day");
        Assert.Equal(18, day.GetProperty("total").GetInt32());
        Assert.Equal(18, day.GetProperty("mine").GetInt32());
        Assert.Equal(18, G(h).GetProperty("given").GetInt64());
        Assert.Equal(0, G(h).GetProperty("all").GetProperty("n").GetInt32());
    }

    [Fact]
    public void Give_all_keeps_what_guests_villages_and_the_masterpiece_wait_for()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        NoOrders(h);
        // Майстерштук челядника — дзвінкий глечик чи макітра (від зерна ніка); кладемо обидва, лишитись має один.
        Items(h, ("tile||1", 5), ("tile||2", 2), ("pot||1", 4), ("jug||3", 1), ("makitra||3", 1));
        GuestOrder(h, "tile", "", 2, 3, TimeSpan.FromHours(2));   // хоче 3 добрі кахлі — є лише 2, їх і тримаємо
        VillageOrder(h, "pot", 1, 2);                              // 2 горщики

        var all = G(h).GetProperty("all");
        Assert.Equal(5, all.GetProperty("keep").GetInt32());
        Assert.Equal(8, all.GetProperty("n").GetInt32());

        var r = Guild(h, new { op = "giveAll" });
        Assert.True(r.Ok, r.Message);
        Assert.Contains("8 виробів", r.Message);
        Assert.Contains("5 виробів лишив під замовлення", r.Message);
        var left = Store(h);
        Assert.Equal(2, left["tile||2"]);
        Assert.Equal(2, left["pot||1"]);
        Assert.Equal(1, left.Where(x => x.Key is "jug||3" or "makitra||3").Sum(x => x.Value));
        Assert.False(left.ContainsKey("tile||1"));
    }

    [Fact]
    public void The_reserve_takes_the_worst_fitting_quality_like_the_delivery_would()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        NoOrders(h);
        Items(h, ("tile||2", 2), ("tile||3", 2), ("tile||4", 1));
        GuestOrder(h, "tile", "", 2, 2, TimeSpan.FromHours(2));
        Assert.True(Guild(h, new { op = "giveAll" }).Ok);
        Assert.Equal(new Dictionary<string, int> { ["tile||2"] = 2 }, Store(h));
    }

    [Fact]
    public void An_expired_order_holds_nothing()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        NoOrders(h);
        Items(h, ("tile||2", 3));
        GuestOrder(h, "tile", "", 2, 3, TimeSpan.FromMinutes(1));
        h.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(Guild(h, new { op = "giveAll" }).Ok);
        Assert.Empty(Store(h));
    }

    [Fact]
    public void Give_all_with_keep_false_takes_the_reserved_too()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        NoOrders(h);
        Items(h, ("tile||2", 2), ("pot||1", 4));
        GuestOrder(h, "tile", "", 2, 3, TimeSpan.FromHours(2));
        var r = Guild(h, new { op = "giveAll", keep = false });
        Assert.True(r.Ok, r.Message);
        Assert.Empty(Store(h));
        Assert.Equal(6, G(h).GetProperty("day").GetProperty("mine").GetInt32());
    }

    [Fact]
    public void Give_all_says_why_when_there_is_nothing_to_give()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        NoOrders(h);
        var empty = Guild(h, new { op = "giveAll" });
        Assert.False(empty.Ok);
        Assert.Contains("порожня", empty.Message);

        Items(h, ("tile||2", 2));
        GuestOrder(h, "tile", "", 2, 3, TimeSpan.FromHours(2));
        var held = Guild(h, new { op = "giveAll" });
        Assert.False(held.Ok);
        Assert.Contains("відкладено", held.Message);
        Assert.Equal(2, Store(h)["tile||2"]);
    }

    [Fact]
    public void Give_all_without_the_guild_is_refused_like_any_guild_act()
    {
        var h = Wheel();
        Items(h, ("pot||1", 5));
        Assert.False(Guild(h, new { op = "giveAll" }).Ok);
        Assert.Equal(5, Store(h)["pot||1"]);
    }

    [Fact]
    public void Give_all_that_fills_the_wagon_tells_the_journal_once()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        NoOrders(h);
        var goal = g.Svc.Summary("оля", Thursday).Today;
        var items = goal.Subs.Select(s => ($"{s.Ware}||1", s.Need)).ToList();
        items.Add(("tile||1", goal.Goal * 2));
        Items(h, [.. items]);
        var r = Guild(h, new { op = "giveAll" });
        Assert.True(r.Ok, r.Message);
        Assert.Equal(3, G(h).GetProperty("day").GetProperty("tier").GetInt32());
        Assert.Single(h.Outbox.OfType<Journal>(), j => j.Text.Contains("золото"));
    }

    [Fact]
    public void The_wagon_remembers_style_and_quality_of_what_lies_on_it()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        NoOrders(h);
        Items(h, ("bowl|kosiv|2", 3), ("kumanets|opishnia|3", 1), ("pot||1", 7));
        Assert.True(Guild(h, new { op = "give", key = "bowl|kosiv|2", n = 3 }).Ok);
        Assert.True(Guild(h, new { op = "giveAll" }).Ok);
        var items = G(h).GetProperty("day").GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal(3, G(h).GetProperty("day").GetProperty("kinds").GetInt32());
        // Найбільше — першим.
        Assert.Equal("pot", items[0].GetProperty("ware").GetString());
        Assert.Contains(items, x => x.GetProperty("ware").GetString() == "bowl" && x.GetProperty("style").GetString() == "kosiv"
            && x.GetProperty("q").GetInt32() == 2 && x.GetProperty("n").GetInt32() == 3);
        Assert.Contains(items, x => x.GetProperty("ware").GetString() == "kumanets" && x.GetProperty("q").GetInt32() == 3);
    }

    [Fact]
    public void An_old_wagon_that_knew_only_the_ware_shows_it_with_unknown_quality()
    {
        // Стан цеху до 28.09: у дні лише wares, без items. Сума мусить зійтись, а невідоме — якість 0.
        var store = new FakeStore();
        store.SaveState(ClickerGuildService.StoreKey, """
            {"days":{"2026-09-10":{"total":9,"wares":{"pot":6,"tile":3},"givers":{"оля":{"nick":"Оля","n":9}},"claimed":{},"logged":0}},
             "potters":{},"mail":{},"sent":{}}
            """);
        var svc = new ClickerGuildService(store, new FakeClock());
        var w = svc.Summary("оля", Thursday).Today;
        Assert.Equal(9, w.Total);
        Assert.Equal(9, w.Items!.Sum(x => x.N));
        Assert.All(w.Items!, x => Assert.Equal(0, x.Quality));
        Assert.Equal("pot", w.Items![0].Ware);

        // Докладене вже новим клієнтом — з розписом; старе лишається «невідомим», сума та сама.
        svc.Give("оля", "Оля", [("pot", (ItemInfo?)new ItemInfo("pot", "kosiv", 2), 2)], Thursday);
        w = svc.Summary("оля", Thursday).Today;
        Assert.Equal(11, w.Items!.Sum(x => x.N));
        Assert.Contains(w.Items!, x => x is { Ware: "pot", Style: "kosiv", Quality: 2, N: 2 });
        Assert.Contains(w.Items!, x => x is { Ware: "pot", Quality: 0, N: 6 });
    }

    [Fact]
    public void The_view_carries_at_most_eight_kinds_but_counts_them_all()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        NoOrders(h);
        var styles = new[] { "", "gavarets", "vasylkiv", "bubnivka", "kosiv", "opishnia", "mezhyhirya", "petrykivka", "trypillia" };
        Items(h, [.. styles.Select((s, i) => ($"pot|{s}|1", i + 1)), ("bowl||1", 2)]);
        Assert.True(Guild(h, new { op = "giveAll" }).Ok);
        var day = G(h).GetProperty("day");
        Assert.Equal(ClickerGuildService.WagonItemsShown, day.GetProperty("items").GetArrayLength());
        Assert.Equal(10, day.GetProperty("kinds").GetInt32());
        Assert.Equal(47, day.GetProperty("total").GetInt32());
    }
}
