using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Звання округи й подарунок (docs/games/specs/clicker-titles.md): каталог, пам'ятні для старих збережень, подарунок
/// разом із новинами, рідкісні й таємні (лічильники, миті, обпали, покупки, дарунки), значки біля ніка, «перші в
/// окрузі» з перехопленням і тишею, звання дня, знайомство цеху зі збереженнями, список цеху, хата друга, збереження.
/// </summary>
public class ClickerTitlesTests
{
    // Годинник RoomHarness стоїть на четвер 2026-09-10 12:00 UTC — це 15:00 у Києві.
    static readonly DateTimeOffset Thursday = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    static RoomHarness Wheel(string nick = "Оля")
    {
        var h = new RoomHarness("clicker");
        h.Solo(nick);
        Assert.True(h.Act(0, "look").Ok);
        return h;
    }

    sealed class Okruha
    {
        public FakeStore Store { get; } = new();
        public FakeClock Clock { get; } = new();
        public ClickerGuildService Svc { get; }
        public Okruha() => Svc = new ClickerGuildService(Store, Clock);

        public RoomHarness Potter(string nick)
        {
            var h = new RoomHarness("clicker", services: RoomHarness.WithService(Svc));
            h.Clock.UtcNow = Clock.UtcNow;
            h.Solo(nick);
            Assert.True(h.Act(0, "look").Ok);
            Publish(h, nick);
            return h;
        }

        public void Publish(RoomHarness h, string nick)
        {
            lock (h.Room.Sync) Store.SaveState("clicker:" + ClickerGuildService.Key(nick), h.Room.Game.Save()!);
        }

        /// <summary>Усі годинники — і цеху, і кімнат — на ту саму мить.</summary>
        public void At(DateTimeOffset at, params RoomHarness[] rooms)
        {
            Clock.UtcNow = at;
            foreach (var h in rooms) h.Clock.UtcNow = at;
        }
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static JsonElement T(RoomHarness h) => View(h).GetProperty("titles");
    static List<string> Keys(JsonElement arr) => arr.EnumerateArray().Select(x => x.GetString()!).ToList();
    static List<string> Mine(RoomHarness h) => Keys(T(h).GetProperty("mine"));
    static List<string> Earned(RoomHarness h) => T(h).GetProperty("earned").EnumerateObject().Select(p => p.Name).ToList();
    static List<string> Badges(RoomHarness h) => Keys(T(h).GetProperty("badges"));
    static double Pots(RoomHarness h) => View(h).GetProperty("pots").GetDouble();
    static IEnumerable<string> Journal(RoomHarness h) => h.Outbox.OfType<Journal>().Select(j => j.Text);
    static ActResult Look(RoomHarness h) => h.Act(0, "look");

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static void PatchTitles(RoomHarness h, Action<JsonObject> edit) => Patch(h, s =>
    {
        var t = s["titles"] as JsonObject ?? new JsonObject();
        edit(t);
        s["titles"] = t;
    });

    static void Items(RoomHarness h, params (string Key, int N)[] items) => Patch(h, s =>
    {
        var bag = new JsonObject();
        foreach (var (key, n) in items) bag[key] = n;
        s["craft"]!["items"] = bag;
    });

    static JsonElement Json(object o) => JsonSerializer.SerializeToElement(o, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    // ---------- каталог ----------

    [Fact]
    public void The_catalog_has_every_kind_and_secrets_give_nothing_away()
    {
        var kinds = Clicker.Titles.GroupBy(t => t.Kind).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(13, kinds[Clicker.TitleTop]);
        Assert.Equal(5, kinds[Clicker.TitleDay]);
        Assert.Equal(9, kinds[Clicker.TitleRare]);
        Assert.Equal(10, kinds[Clicker.TitleSecret]);
        Assert.Equal(2, kinds[Clicker.TitleMemory]);
        Assert.Equal(Clicker.Titles.Length, Clicker.Titles.Select(t => t.Key).Distinct().Count());
        // Ключ таємного нічого не каже: каталог їде до кожного клієнта.
        Assert.All(Clicker.Titles.Where(t => t.Kind == Clicker.TitleSecret), t => Assert.Matches("^x[0-9]+$", t.Key));

        var h = Wheel();
        Assert.True(h.Act(0, "look", new { catalog = true }).Ok);
        var list = View(h).GetProperty("catalog").GetProperty("titles").GetProperty("list").EnumerateArray().ToList();
        Assert.Equal(Clicker.Titles.Length, list.Count);
        foreach (var t in list.Where(x => x.GetProperty("kind").GetString() == Clicker.TitleSecret))
        {
            Assert.False(t.TryGetProperty("name", out _));
            Assert.False(t.TryGetProperty("desc", out _));
        }
    }

    // ---------- пам'ятні й подарунок ----------

    [Fact]
    public void An_old_save_gets_the_memory_titles_once_and_a_new_wheel_does_not()
    {
        var h = Wheel();
        Assert.Empty(Earned(h));
        Patch(h, s => { s.Remove("titles"); s["total"] = 5e6; s["stamps"] = 2510; });
        Assert.Equal(["firstclay", "veteran"], Earned(h).Order().ToList());
        var at = T(h).GetProperty("earned").GetProperty("veteran").GetDateTimeOffset();

        h.Clock.Advance(TimeSpan.FromDays(1));
        Patch(h, _ => { });
        Assert.Equal(2, Earned(h).Count);
        Assert.Equal(at, T(h).GetProperty("earned").GetProperty("veteran").GetDateTimeOffset());
    }

    [Fact]
    public void A_thousand_stamps_or_less_is_no_veteran()
    {
        var h = Wheel();
        Patch(h, s => { s.Remove("titles"); s["total"] = 5e6; s["stamps"] = 999; });
        Assert.Equal(["firstclay"], Earned(h));
    }

    [Fact]
    public void The_gift_is_three_hours_of_your_own_passive_and_comes_once_with_the_news()
    {
        var h = Wheel();
        Patch(h, s => { s.Remove("titles"); s["news"] = "v9.1"; s["total"] = 1000; s["upgrades"]!["apprentice"] = 10; });
        var v = View(h);
        Assert.Equal(Clicker.NewsVersion, v.GetProperty("news").GetString());
        Assert.Equal("v9.1", v.GetProperty("newsSeen").GetString());
        Assert.False(T(h).GetProperty("gift").GetBoolean());

        var before = Pots(h);
        var perSecond = v.GetProperty("baseSecond").GetDouble();
        var r = h.Act(0, "news", new { v = Clicker.NewsVersion });
        Assert.True(r.Ok, r.Message);
        Assert.Contains("Подарунок округи", r.Message);
        Assert.Equal(Math.Floor(perSecond * Clicker.GiftMinutes * 60), Pots(h) - before);
        Assert.True(T(h).GetProperty("gift").GetBoolean());

        // Удруге нічого: новини вже бачив, подарунок уже забрав.
        before = Pots(h);
        Assert.True(h.Act(0, "news", new { v = Clicker.NewsVersion }).Ok);
        Assert.Equal(before, Pots(h));
    }

    [Fact]
    public void A_new_potter_has_no_news_and_no_gift()
    {
        var h = Wheel();
        Assert.Equal(JsonValueKind.Null, View(h).GetProperty("news").ValueKind);
        var before = Pots(h);
        var r = h.Act(0, "news", new { v = Clicker.NewsVersion });
        Assert.True(r.Ok);
        Assert.Equal(before, Pots(h));
    }

    // ---------- рідкісні ----------

    [Fact]
    public void Ten_thousand_stamps_is_a_rare_title_and_the_journal_hears_of_it()
    {
        var h = Wheel();
        Patch(h, s => s["stamps"] = Clicker.TenThousand);
        Assert.True(Look(h).Ok);
        Assert.Contains("tenk", Earned(h));
        Assert.Contains("tenk", Mine(h));
        Assert.Contains(Journal(h), t => t.Contains("Оля") && t.Contains("«Десять тисяч»"));
        // Раз: друга дія нічого вже не каже.
        var said = Journal(h).Count();
        Assert.True(Look(h).Ok);
        Assert.Equal(said, Journal(h).Count());
    }

    [Fact]
    public void A_streak_of_a_hundred_and_the_sich_are_rare_titles_too()
    {
        var h = Wheel();
        Patch(h, s => { s["fallStreak"] = Clicker.HundredStreak; s["upgrades"]!["sich"] = 1; });
        Assert.True(Look(h).Ok);
        Assert.Contains("hundred", Earned(h));
        Assert.Contains("sich", Earned(h));
    }

    [Fact]
    public void A_golden_jug_caught_in_its_last_half_second_is_the_last_moment()
    {
        var h = Wheel();
        var now = h.Clock.UtcNow;
        Patch(h, s => s["golden"] = new JsonObject { ["at"] = now.AddSeconds(-5), ["until"] = now.AddSeconds(5), ["kind"] = 0, ["x"] = 10, ["y"] = 10 });
        Assert.True(h.Act(0, "catch").Ok);
        Assert.DoesNotContain("moment", Earned(h));

        now = h.Clock.UtcNow;
        Patch(h, s => s["golden"] = new JsonObject { ["at"] = now.AddSeconds(-11.8), ["until"] = now.AddSeconds(0.3), ["kind"] = 0, ["x"] = 10, ["y"] = 10 });
        Assert.True(h.Act(0, "catch").Ok);
        Assert.Contains("moment", Earned(h));
    }

    [Fact]
    public void Twenty_flawless_firings_in_a_row_and_the_kiln_counters_become_titles()
    {
        var h = Wheel();
        PatchTitles(h, t => { t["perfectRun"] = Clicker.FlawlessRun; t["cracked"] = Clicker.OverburnCracks; t["catKnocks"] = Clicker.CatKnockTimes; });
        Assert.True(Look(h).Ok);
        Assert.Contains("flawless", Earned(h));
        Assert.Contains("x4", Earned(h));
        Assert.Contains("x3", Earned(h));
    }

    // ---------- таємні ----------

    [Fact]
    public void Three_hundred_escaped_golden_jugs_make_the_sleepyhead()
    {
        var h = Wheel();
        var now = h.Clock.UtcNow;
        PatchTitles(h, t => t["goldenMissed"] = Clicker.SleepyMissed - 1);
        Patch(h, s => s["golden"] = new JsonObject { ["at"] = now.AddSeconds(-20), ["until"] = now.AddSeconds(-5), ["kind"] = 0, ["x"] = 10, ["y"] = 10 });
        Assert.True(Look(h).Ok);
        Assert.Contains("x1", Earned(h));
        Assert.Contains(Journal(h), t => t.StartsWith("🙈") && t.Contains("«Соня»"));
        // Своє таємне клієнт бачить із назвою: вивіска мусить знати значок.
        Assert.Equal("Соня", T(h).GetProperty("secrets").GetProperty("x1").GetProperty("name").GetString());
    }

    [Fact]
    public void A_jug_that_breaks_while_you_click_counts_for_butterfingers_and_a_quiet_one_does_not()
    {
        var h = Wheel();
        var now = h.Clock.UtcNow;
        PatchTitles(h, t => t["brokenBusy"] = Clicker.HooksBroken - 1);
        // Глек летів і розбився, а останній клік — поза його польотом: не рахується.
        Patch(h, s =>
        {
            s["fall"] = new JsonObject { ["at"] = now.AddSeconds(-10), ["until"] = now.AddSeconds(-6), ["x"] = 20 };
            s["heatAt"] = now.AddSeconds(-30);
        });
        Assert.True(Look(h).Ok);
        Assert.DoesNotContain("x2", Earned(h));

        now = h.Clock.UtcNow;
        Patch(h, s =>
        {
            s["fall"] = new JsonObject { ["at"] = now.AddSeconds(-10), ["until"] = now.AddSeconds(-6), ["x"] = 20 };
            s["heatAt"] = now.AddSeconds(-8);
        });
        Assert.True(Look(h).Ok);
        Assert.Contains("x2", Earned(h));
    }

    [Fact]
    public void A_firing_just_after_midnight_and_two_within_an_hour_are_secret_titles()
    {
        var h = Wheel();
        // 00:02 у Києві — це 21:02 UTC напередодні.
        h.Clock.UtcNow = new DateTimeOffset(2026, 9, 10, 21, 2, 0, TimeSpan.Zero);
        Patch(h, s => s["total"] = Clicker.TotalFor(2));
        Assert.True(h.Act(0, "fire").Ok);
        Assert.Contains("x5", Earned(h));
        Assert.DoesNotContain("x6", Earned(h));

        h.Clock.Advance(TimeSpan.FromMinutes(40));
        Patch(h, s => s["total"] = Clicker.TotalFor(5));
        Assert.True(h.Act(0, "fire").Ok);
        Assert.Contains("x6", Earned(h));
    }

    [Fact]
    public void Buying_so_close_that_less_than_a_second_is_left_is_the_last_penny()
    {
        var h = Wheel();
        Patch(h, s => s["upgrades"]!["apprentice"] = 10);
        var up = View(h).GetProperty("upgrades").EnumerateObject()
            .First(p => p.Value.GetProperty("kind").GetString() == "idle" && p.Value.GetProperty("price").GetDouble() >= Clicker.PennyFrom);
        var price = up.Value.GetProperty("price").GetDouble();

        // Лишилось хвилину пасиву — ще ні.
        Patch(h, s => s["pots"] = price + 100);
        Assert.True(h.Act(0, "buy", new { key = up.Name, n = 1 }).Ok);
        Assert.DoesNotContain("x7", Earned(h));

        price = View(h).GetProperty("upgrades").GetProperty(up.Name).GetProperty("price").GetDouble();
        Patch(h, s => { s["pots"] = price; s["total"] = price * 2; });
        Assert.True(h.Act(0, "buy", new { key = up.Name, n = 1 }).Ok);
        Assert.Contains("x7", Earned(h));
    }

    [Fact]
    public void The_eternal_apprentice_lives_only_while_you_are_an_apprentice()
    {
        var h = Wheel();
        Patch(h, s => s["craft"]!["firedBy"] = new JsonObject { ["pot"] = Clicker.ApprenticeFired });
        Assert.True(Look(h).Ok);
        Assert.Contains(Clicker.TitleApprentice, Mine(h));
        Assert.Contains(Journal(h), t => t.Contains("«Вічний учень»"));

        Patch(h, s => s["guild"]!["rank"] = 1);
        Assert.True(Look(h).Ok);
        Assert.DoesNotContain(Clicker.TitleApprentice, Mine(h));
        Assert.DoesNotContain(Clicker.TitleApprentice, Earned(h));
        // І назад не оголошуємо: позначка «вже казали» лишилась.
        Assert.Single(Journal(h), t => t.Contains("«Вічний учень»"));
    }

    // ---------- значки ----------

    [Fact]
    public void Badges_are_your_choice_of_what_you_have_or_else_the_rarest()
    {
        var h = Wheel();
        Patch(h, s => { s.Remove("titles"); s["total"] = 10; s["stamps"] = 1500; });
        Assert.Equal(["firstclay", "veteran"], Badges(h));

        Assert.True(h.Act(0, "titles", new { op = "show", keys = new[] { "veteran" } }).Ok);
        Assert.Equal(["veteran"], Badges(h));
        Assert.False(h.Act(0, "titles", new { op = "show", keys = new[] { "first" } }).Ok);

        PatchTitles(h, t => t["earned"] = new JsonObject
        {
            ["tenk"] = Thursday, ["hundred"] = Thursday, ["sich"] = Thursday, ["moment"] = Thursday,
        });
        Assert.False(h.Act(0, "titles", new { op = "show", keys = new[] { "tenk", "hundred", "sich", "moment" } }).Ok);
        // Порожньо — найрідкісніші: рідкісні раніше за пам'ятні, всередині — порядок каталогу.
        Assert.True(h.Act(0, "titles", new { op = "show", keys = Array.Empty<string>() }).Ok);
        Assert.Equal(["sich", "tenk", "hundred"], Badges(h));
    }

    // ---------- «перші в окрузі» ----------

    [Fact]
    public void The_first_potter_is_the_one_with_most_stamps_and_the_journal_hears_only_a_real_takeover()
    {
        var g = new Okruha();
        var m = g.Potter("Микола");
        var v = g.Potter("Владік");
        Patch(m, s => s["stamps"] = 5783);
        Patch(v, s => s["stamps"] = 2510);
        Assert.True(Look(m).Ok);
        Assert.True(Look(v).Ok);
        Assert.Contains("first", Mine(m));
        Assert.DoesNotContain("first", Mine(v));
        // Перше призначення — тихе.
        Assert.DoesNotContain(Journal(m).Concat(Journal(v)), t => t.Contains("забирає"));

        Patch(v, s => s["stamps"] = 6000);
        Assert.True(Look(v).Ok);
        Assert.Contains("first", Mine(v));
        Assert.Contains("👑 Владік забирає в Микола звання «Перший гончар округи»", Journal(v));

        // Одразу назад — звання переходить, а Журнал мовчить: двоє не засиплють його перехопленнями.
        Patch(m, s => s["stamps"] = 7000);
        Assert.True(Look(m).Ok);
        Assert.Contains("first", Mine(m));
        Assert.DoesNotContain(Journal(m), t => t.Contains("забирає"));

        g.At(Thursday.AddMinutes(31), m, v);
        Patch(v, s => s["stamps"] = 8000);
        Assert.True(Look(v).Ok);
        Assert.Equal(2, Journal(v).Count(t => t.Contains("забирає")));
    }

    [Fact]
    public void An_equal_number_leaves_the_title_where_it_was()
    {
        var g = new Okruha();
        var a = g.Potter("Оля");
        var b = g.Potter("Антон");
        Patch(a, s => s["caught"] = 50);
        Assert.True(Look(a).Ok);
        Patch(b, s => s["caught"] = 50);
        Assert.True(Look(b).Ok);
        Assert.Contains("hunter", Mine(a));
        Assert.DoesNotContain("hunter", Mine(b));
    }

    [Fact]
    public void Who_has_not_played_for_two_weeks_quietly_loses_the_title()
    {
        var g = new Okruha();
        var a = g.Potter("Оля");
        var b = g.Potter("Антон");
        Patch(a, s => s["petted"] = 9);
        Assert.True(Look(a).Ok);
        Patch(b, s => s["petted"] = 2);
        Assert.True(Look(b).Ok);
        Assert.Contains("catlover", Mine(a));

        g.At(Thursday.AddDays(ClickerGuildService.TitleActiveDays + 1), b);
        Assert.True(Look(b).Ok);
        Assert.Contains("catlover", Mine(b));
        Assert.DoesNotContain(Journal(b), t => t.Contains("забирає"));
    }

    [Fact]
    public void The_guild_reads_the_saves_of_those_who_have_not_come_since_the_update()
    {
        var g = new Okruha();
        g.Svc.Hello("микола", "Микола", 0, Thursday);
        g.Svc.Hello("владік", "Владік", 0, Thursday);
        g.Store.SaveState("clicker:микола", """{"stamps":5783,"caught":436,"titles":{"earned":{"veteran":"2026-09-10T12:00:00+00:00"},"show":["veteran"]}}""");
        g.Store.SaveState("clicker:владік", """{"stamps":2510,"guild":{"rank":0,"given":363},"craft":{"firedBy":{"pot":2518}}}""");

        var n = g.Potter("Назар");
        Patch(n, s => s["stamps"] = 3000);
        Assert.True(Look(n).Ok);
        Assert.Contains("first", g.Svc.TitlesHeld("микола", Thursday));
        Assert.Contains("pillar", g.Svc.TitlesHeld("владік", Thursday));
        Assert.DoesNotContain(Journal(n), t => t.Contains("забирає"));

        var roster = Json(g.Svc.Roster("Назар"));
        var potters = roster.GetProperty("potters").EnumerateArray().ToDictionary(p => p.GetProperty("nick").GetString()!);
        Assert.True(potters["Микола"].GetProperty("first").GetBoolean());
        Assert.False(potters["Назар"].GetProperty("first").GetBoolean());
        // Микола обрав ветерана — значок саме він.
        Assert.Equal(["veteran"], potters["Микола"].GetProperty("badges").EnumerateArray().Select(b => b.GetProperty("key").GetString()).ToList());
        // Владік — учень із двома з половиною тисячами обпалених: «Вічний учень» і в списку.
        Assert.Contains(potters["Владік"].GetProperty("badges").EnumerateArray(), b => b.GetProperty("key").GetString() == Clicker.TitleApprentice);
        Assert.Equal("Микола", roster.GetProperty("titles").GetProperty("tops").GetProperty("first").GetProperty("nick").GetString());
    }

    // ---------- звання дня ----------

    [Fact]
    public void Yesterdays_winners_hold_the_day_titles_all_day_today()
    {
        var g = new Okruha();
        const string day = "2026-09-10";
        var none = new Dictionary<string, double>();
        g.Svc.TitlesReport("а", "А", Thursday, new TitleReport(none, [], [], day, new TitleDayReport(150, 60, 6, Thursday.AddHours(-5), 0.5)));
        g.Svc.TitlesReport("б", "Б", Thursday, new TitleReport(none, [], [], day, new TitleDayReport(120, 10, 9, Thursday.AddHours(-6), 2.0)));
        Assert.Empty(g.Svc.TitlesHeld("а", Thursday));

        var next = Thursday.AddDays(1);
        Assert.Equal(["bee", "night"], g.Svc.TitlesHeld("а", next).Order().ToList());
        Assert.Equal(["catch", "rising", "rooster"], g.Svc.TitlesHeld("б", next).Order().ToList());
        // Через день — уже нічого: тримають лише вчорашні.
        Assert.Empty(g.Svc.TitlesHeld("а", Thursday.AddDays(2)));
    }

    [Fact]
    public void Below_the_thresholds_there_is_no_day_title()
    {
        var g = new Okruha();
        var none = new Dictionary<string, double>();
        g.Svc.TitlesReport("а", "А", Thursday, new TitleReport(none, [], [], "2026-09-10",
            new TitleDayReport(ClickerGuildService.BeeMin - 1, ClickerGuildService.NightMin - 1, ClickerGuildService.CatchMin - 1, null, 0)));
        Assert.Empty(g.Svc.TitlesHeld("а", Thursday.AddDays(1)));
    }

    [Fact]
    public void Night_clicks_count_for_the_night_watch_and_the_first_click_after_five_is_the_rooster()
    {
        var h = Wheel();
        // 03:30 у Києві наступного дня.
        h.Clock.UtcNow = new DateTimeOffset(2026, 9, 11, 0, 30, 0, TimeSpan.Zero);
        Assert.True(h.Act(0, "spin", PotterHands.Human(12)).Ok);
        var v = T(h).GetProperty("values");
        Assert.Equal(12, v.GetProperty("night").GetDouble());
        Assert.Equal(12, v.GetProperty("bee").GetDouble());
        Assert.Equal(0, v.GetProperty("rooster").GetDouble());

        // 06:00 у Києві.
        h.Clock.UtcNow = new DateTimeOffset(2026, 9, 11, 3, 0, 0, TimeSpan.Zero);
        Assert.True(h.Act(0, "spin", PotterHands.Human(12)).Ok);
        v = T(h).GetProperty("values");
        Assert.Equal(12, v.GetProperty("night").GetDouble());
        Assert.Equal(24, v.GetProperty("bee").GetDouble());
        Assert.Equal(h.Clock.UtcNow.ToUnixTimeMilliseconds(), v.GetProperty("rooster").GetDouble());
    }

    [Fact]
    public void The_rising_star_is_growth_since_the_first_act_of_the_day()
    {
        var h = Wheel();
        Patch(h, s => s["total"] = 2e6);
        h.Clock.UtcNow = new DateTimeOffset(2026, 9, 11, 6, 0, 0, TimeSpan.Zero);
        Assert.True(Look(h).Ok);
        Patch(h, s => s["total"] = 3e6);
        Assert.Equal(0.5, T(h).GetProperty("values").GetProperty("rising").GetDouble(), 9);
    }

    // ---------- дарунки ----------

    [Fact]
    public void Gifts_from_everyone_around_make_the_mutual_guarantee()
    {
        var g = new Okruha();
        var a = g.Potter("Оля");
        var b = g.Potter("Антон");
        var c = g.Potter("Марта");
        foreach (var from in new[] { b, c })
        {
            Items(from, ("pot||1", 1));
            Assert.True(from.Act(0, "guild", new { op = "gift", key = "pot||1", nick = "Оля" }).Ok);
        }
        Assert.True(Look(a).Ok);
        Assert.Contains("x8", Earned(a));
        Assert.Contains(Journal(a), t => t.Contains("перший(а) в окрузі") && t.Contains("«Кругова порука»"));
    }

    [Fact]
    public void Three_gifts_to_three_potters_in_one_day_is_saint_nicholas()
    {
        var g = new Okruha();
        var a = g.Potter("Микола");
        foreach (var nick in new[] { "Антон", "Марта", "Назар" }) g.Potter(nick);
        Items(a, ("pot||1", 3));
        foreach (var nick in new[] { "Антон", "Марта" })
            Assert.True(a.Act(0, "guild", new { op = "gift", key = "pot||1", nick }).Ok);
        Assert.DoesNotContain("x9", Earned(a));
        Assert.True(a.Act(0, "guild", new { op = "gift", key = "pot||1", nick = "Назар" }).Ok);
        Assert.Contains("x9", Earned(a));
    }

    [Fact]
    public void A_secret_title_is_revealed_to_everyone_once_somebody_has_it()
    {
        var g = new Okruha();
        var a = g.Potter("Оля");
        var b = g.Potter("Антон");
        Assert.False(Json(g.Svc.Roster("Оля")).GetProperty("titles").GetProperty("secrets").TryGetProperty("x1", out _));

        var now = a.Clock.UtcNow;
        PatchTitles(a, t => t["goldenMissed"] = Clicker.SleepyMissed);
        Assert.True(Look(a).Ok);
        var board = Json(g.Svc.Roster("Антон")).GetProperty("titles");
        Assert.Equal("Соня", board.GetProperty("secrets").GetProperty("x1").GetProperty("name").GetString());
        Assert.Equal("Оля", board.GetProperty("firsts").GetProperty("x1").GetProperty("nick").GetString());

        // Другий — уже не перший.
        PatchTitles(b, t => t["goldenMissed"] = Clicker.SleepyMissed);
        Assert.True(Look(b).Ok);
        Assert.Contains(Journal(b), t => t.Contains("вибив(ла) таємне звання") && !t.Contains("перший"));
    }

    // ---------- хата друга й збереження ----------

    [Fact]
    public void A_friends_house_shows_the_wall_of_titles_and_the_keepsake()
    {
        var g = new Okruha();
        var a = g.Potter("Оля");
        Patch(a, s => { s.Remove("titles"); s["news"] = "v9.1"; s["total"] = 5e6; s["stamps"] = 1200; });
        Assert.True(a.Act(0, "news", new { v = Clicker.NewsVersion }).Ok);
        g.Publish(a, "Оля");

        var house = Json(g.Svc.House("Оля")!);
        Assert.True(house.GetProperty("keepsake").GetBoolean());
        var wall = house.GetProperty("titles").EnumerateArray().Select(t => t.GetProperty("key").GetString()).ToList();
        Assert.Contains("veteran", wall);
        Assert.Contains("firstclay", wall);
        Assert.Contains("first", wall);
    }

    [Fact]
    public void A_friends_house_names_the_wonders_it_has_not_just_counts_them()
    {
        var g = new Okruha();
        var key = Clicker.Wonders[0].Key;
        // Сире збереження: хата друга читає його сама, і вигадану дивовижу серед ключів не показує.
        g.Store.SaveState("clicker:оля", "{\"house\":{\"wonders\":{\"" + key + "\":\"2026-09-10T12:00:00+00:00\",\"вигадка\":\"2026-09-10T12:00:00+00:00\"}}}");
        var house = Json(g.Svc.House("Оля")!);
        Assert.Equal(2, house.GetProperty("wonders").GetInt32());
        Assert.Equal([key], house.GetProperty("wonderKeys").EnumerateArray().Select(x => x.GetString()).ToList());
    }

    [Fact]
    public void Titles_counters_and_badges_survive_a_save()
    {
        var h = Wheel();
        PatchTitles(h, t =>
        {
            t["goldenMissed"] = 17; t["brokenBusy"] = 3; t["perfectRun"] = 4;
            t["earned"] = new JsonObject { ["tenk"] = Thursday };
            t["show"] = new JsonArray("tenk");
            t["gifts"] = new JsonArray(Clicker.GiftKey);
        });
        Patch(h, _ => { });
        var t = T(h);
        Assert.Equal(17, t.GetProperty("progress").GetProperty("x1")[0].GetDouble());
        Assert.Equal(3, t.GetProperty("progress").GetProperty("x2")[0].GetDouble());
        Assert.Equal(4, t.GetProperty("progress").GetProperty("flawless")[0].GetDouble());
        Assert.Equal(["tenk"], Badges(h));
        Assert.True(t.GetProperty("gift").GetBoolean());
    }

    [Fact]
    public void Top_and_day_titles_never_come_from_a_save()
    {
        var h = Wheel();
        PatchTitles(h, t => t["earned"] = new JsonObject { ["first"] = Thursday, ["bee"] = Thursday, ["nonsense"] = Thursday });
        Assert.Empty(Earned(h));
        Assert.Empty(Mine(h));
    }
}
