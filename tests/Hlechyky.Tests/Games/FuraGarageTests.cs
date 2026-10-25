using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static Hlechyky.Tests.Games.FuraTests;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Чумаки», гараж 0.6 (specs/fura.md, «Гараж (0.6) — контракт»): книга <c>garage.json</c>, досвід водія й втома, рівні,
/// ретро, покупки через гаманець (ідемпотентні, лише акаунтам, з рівня й по порядку), стеля корисності, «⚖ Рівні фури»,
/// вимикач <c>Fura:Garage</c>, дошка під свої причепи й далеке замовлення. Косметика в Лавці — <c>LavkaTests</c>.
/// </summary>
public sealed class FuraGarageTests
{
    static readonly FuraBook Book = FuraBook.Load();

    sealed record Rig(RoomHarness H, FakeStakes Wallet);

    /// <summary>Сервіси стола: гараж поверх сховища харнеса, гаманець, хто акаунт і вимикач гаража.</summary>
    static IServiceProvider Services(Func<IGameStore> store, FakeStakes wallet, Func<string, bool>? account = null, bool garage = true,
        Func<string, IReadOnlyDictionary<string, string>>? worn = null) =>
        new ServiceCollection()
            .AddSingleton(_ => new FuraGarage(store(), a => a()))
            .AddSingleton<IStakes>(wallet)
            .AddSingleton(new FuraWardrobe(account, worn))
            .AddSingleton<IOptionsMonitor<FuraOptions>>(new FixedOptions<FuraOptions>(new FuraOptions { Garage = garage }))
            .BuildServiceProvider();

    /// <summary>
    /// Соло-стіл Олі (чи стіл з опціями — через Join). <paramref name="xp"/> — гараж, що вже є в базі (інакше — ретро від
    /// <paramref name="trips"/> рейсів у старій фурі).
    /// </summary>
    static Rig Table(long? xp = null, int balance = 100_000, Func<string, bool>? account = null, bool garage = true, object? options = null,
        string? career = null, int trips = 0, Func<string, IReadOnlyDictionary<string, string>>? worn = null)
    {
        var wallet = new FakeStakes().Set("Оля", balance);
        RoomHarness? h = null;
        h = new RoomHarness("fura", options, seed: 42, services: Services(() => h!.Store, wallet, account, garage, worn));
        if (career is not null) h.Store.SaveState(FuraCareer.Key("оля"), career);
        else if (xp is { } x) h.Store.SaveState(FuraCareer.Key("оля"), $$"""{"xp":{{x}},"lastTrip":{{trips}}}""");
        if (trips > 0) h.Store.SaveState(FuraGarage.Key("оля"), $$"""{"trips":{{trips}}}""");
        Assert.True((options is null ? h.Solo("Оля") : h.Join("Оля")).Ok, h.Reply.Message);
        return new Rig(h, wallet);
    }

    static JsonElement Career(RoomHarness h, int seat = 0) => h.View(seat).GetProperty("career");
    static JsonElement Params(RoomHarness h) => Career(h).GetProperty("params");

    /// <summary>Здати чесний рейс (ціле 100 %, у бокс, вчасно) і повернутись на дошку.</summary>
    static JsonElement Deliver(RoomHarness h, int k = 100)
    {
        Haul(h, k: k);
        var r = h.Act(0, "deliver");
        Assert.True(r.Ok, r.Message);
        var result = h.View(0).GetProperty("result");
        Assert.True(h.Act(0, "next").Ok, h.Reply.Message);
        return result;
    }

    // ---------- книга ----------

    [Fact]
    public void Book_levels_follow_the_curve_150_plus_50_per_level_to_11400()
    {
        Assert.Equal(0, Book.Total(1));
        Assert.Equal(150, Book.Total(2));
        Assert.Equal(350, Book.Total(3));
        Assert.Equal(600, Book.Total(4));
        Assert.Equal(2100, Book.Total(8));
        Assert.Equal(3150, Book.Total(10));
        Assert.Equal(11400, Book.Total(20));
        Assert.Equal(1, Book.LevelOf(149));
        Assert.Equal(2, Book.LevelOf(150));
        Assert.Equal(20, Book.LevelOf(1_000_000));
        Assert.Equal(0, Book.StarsOf(11400 + 2499));
        Assert.Equal(1, Book.StarsOf(11400 + 2500));
    }

    [Fact]
    public void Book_finds_tractors_trailers_and_tune_steps()
    {
        Assert.Equal(new FuraBook.Buyable("vil", FuraBook.Tractor, "Віл", 6, 8000), Book.Find("vil"));
        Assert.Equal(new FuraBook.Buyable("izoterm", FuraBook.Trailer, "Ізотерм", 4, 4000), Book.Find("izoterm"));
        Assert.Equal(new FuraBook.Buyable("engine2", FuraBook.Tune, "Двигун, щабель 2", 5, 1000, "engine", 2), Book.Find("engine2"));
        Assert.Equal(3, Book.Find("gear1")!.Lvl);                // круїз — з третього, не з другого
        Assert.Equal(5500, Book.Find("susp5")!.Price);
        Assert.Equal(17, Book.Find("susp5")!.Lvl);
        Assert.Null(Book.Find("engine5"));                       // двигун — 4 щаблі
        Assert.Null(Book.Find("engine"));
        Assert.Null(Book.Find("kamaz"));
        Assert.Equal("Віл", FuraBook.Label("vil"));
    }

    [Fact]
    public void Base_params_are_todays_truck()
    {
        var p = Book.Params(FuraBook.Chumak, FuraBook.Tent, null);
        Assert.Equal(400000.0, p["power"]);
        Assert.Equal(64000.0, p["fmax"]);
        Assert.Equal(150.0, p["brake"]);
        Assert.Equal(330.0, p["trailerBrake"]);
        Assert.Equal(1.5, p["slip"]);
        Assert.Equal(12.0, p["trailerStiff"]);
        Assert.Equal(0.06, p["engineBrake"]);
        Assert.Equal(1.0, p["hurt"]);
        Assert.Equal(false, p["cruise"]);
    }

    [Fact]
    public void Tune_adds_up_and_the_usefulness_cap_cuts_it()
    {
        var full = Book.Branches.ToDictionary(b => b.Id, b => b.Steps);
        var vil = Book.Params("vil", "puh", full);
        Assert.Equal(560000.0, vil["power"]);                    // 470 × 1,2 = 564 → стеля 560
        Assert.Equal(74000 * 1.2, (double)vil["fmax"], 3);
        Assert.Equal(180.0, (double)vil["brake"], 3);
        Assert.Equal(396.0, (double)vil["trailerBrake"], 3);
        Assert.Equal(1.5 * 1.12, (double)vil["slip"], 3);
        Assert.Equal(0.1, vil["engineBrake"]);
        Assert.Equal(true, vil["cruise"]);
        Assert.Equal(true, vil["abs"]);
        Assert.Equal(true, vil["nav"]);
        Assert.Equal(0.5, vil["shake"]);
        Assert.Equal(10.0, vil["trailerStiff"]);
        // шкода: підвіска 0,96⁵ × «Пух» 0,8 ≈ 0,65 — у межах −40 %
        Assert.Equal(Math.Round(Math.Pow(0.96, 5) * 0.8, 4), vil["hurt"]);
        // «Ластівка» з повною підвіскою: пороги 1,1 × 1,25 = 1,375 → стеля 1,3
        Assert.Equal(1.3, Book.Params("lastivka", "puh", full)["limits"]);
        // одна підвіска без «Пуха» — пороги +25 %, шкода ×0,82
        var susp = Book.Params(FuraBook.Chumak, FuraBook.Tent, new Dictionary<string, int> { ["susp"] = 5 });
        Assert.Equal(1.25, susp["limits"]);
        Assert.Equal(Math.Round(Math.Pow(0.96, 5), 4), susp["hurt"]);
        // книга з жорсткішою стелею шкоди — сервер не дасть нижче за неї
        var strict = FuraBook.Parse(Book.Raw.Replace("\"hurt\": 0.6", "\"hurt\": 0.7"));
        Assert.Equal(0.7, strict.Params("vil", "puh", full)["hurt"]);
    }

    // ---------- досвід ----------

    [Fact]
    public void Xp_formula_bonuses_broken_cargo_and_the_same_trip_twice()
    {
        var c = new FuraCareer();
        Assert.Equal(95, c.Gain(Book, 1, 3, 100, false, false, false, false, 1, false, "2026-10-25")!.Xp);
        Assert.Equal(115, c.Gain(Book, 2, 3, 100, true, true, false, false, 1, false, "2026-10-25")!.Xp);   // 95 × 1,21
        Assert.Equal(48, c.Gain(Book, 3, 3, 50, false, false, false, false, 1, false, "2026-10-25")!.Xp);   // round(47,5) = 48
        Assert.Equal(0, c.Gain(Book, 4, 3, 19, true, true, false, false, 1, false, "2026-10-25")!.Xp);     // «усе розбито»
        Assert.Equal(131, c.Gain(Book, 5, 3, 100, false, false, true, true, 1, false, "2026-10-25")!.Xp);    // 95 × 1,15 × 1,2 = 131,1
        Assert.Equal(Math.Round(95 * 1.3 * 1.1), c.Gain(Book, 6, 3, 100, false, false, false, false, 1.1, true, "2026-10-25")!.Xp);
        var xp = c.Xp;
        Assert.Null(c.Gain(Book, 6, 3, 100, true, true, true, true, 1, true, "2026-10-25"));   // той самий рейс — вдруге ні
        Assert.Null(c.Gain(Book, 2, 3, 100, true, true, true, true, 1, true, "2026-10-25"));
        Assert.Equal(xp, c.Xp);
        Assert.Equal(6, c.DayTrips);
    }

    [Fact]
    public void After_ten_trips_a_day_xp_halves_and_a_new_day_resets()
    {
        var c = new FuraCareer();
        for (var t = 1; t <= 10; t++) Assert.Equal(95, c.Gain(Book, t, 3, 100, false, false, false, false, 1, false, "2026-10-25")!.Xp);
        var tired = c.Gain(Book, 11, 3, 100, false, false, false, false, 1, false, "2026-10-25")!;
        Assert.True(tired.Tired);
        Assert.Equal(48, tired.Xp);
        Assert.Equal(48, c.Gain(Book, 12, 3, 100, false, false, false, false, 1, false, "2026-10-25")!.Xp);
        var fresh = c.Gain(Book, 13, 3, 100, false, false, false, false, 1, false, "2026-10-26")!;
        Assert.False(fresh.Tired);
        Assert.Equal(95, fresh.Xp);
        Assert.Equal(1, c.DayTrips);
    }

    [Fact]
    public void A_trip_gives_xp_in_the_result_and_the_career_and_it_is_stored_apart_from_the_rig()
    {
        var (h, _) = Table();
        var career = Career(h);
        Assert.Equal(1, career.GetProperty("lvl").GetInt32());
        Assert.Equal(0, career.GetProperty("xp").GetInt64());
        Assert.Equal(150, career.GetProperty("next").GetInt64());
        Assert.True(career.GetProperty("account").GetBoolean());
        Assert.True(career.GetProperty("on").GetBoolean());
        Assert.False(career.GetProperty("equal").GetBoolean());

        var order = h.View(0).GetProperty("board")[0];
        var result = Deliver(h);
        var km = order.GetProperty("km").GetDouble();
        var expected = (int)Math.Round(Math.Round(20 + 25 * km, MidpointRounding.AwayFromZero) * 1.1 * 1.1, MidpointRounding.AwayFromZero);
        Assert.Equal(expected, result.GetProperty("xp").GetInt32());
        career = Career(h);
        Assert.Equal(expected, career.GetProperty("xp").GetInt64());
        Assert.Equal(1, career.GetProperty("dayTrips").GetInt32());
        Assert.Equal(1, career.GetProperty("last").GetProperty("trip").GetInt32());
        Assert.Equal(expected, career.GetProperty("last").GetProperty("xp").GetInt32());

        var stored = JsonDocument.Parse(h.Store.LoadState(FuraCareer.Key("оля"))!).RootElement;
        Assert.Equal(expected, stored.GetProperty("xp").GetInt64());
        Assert.Equal(1, stored.GetProperty("lastTrip").GetInt32());
        Assert.DoesNotContain("\"xp\"", h.Store.LoadState(FuraGarage.Key("оля")));   // у фуру гараж не пишеться
    }

    [Fact]
    public void A_level_up_shows_in_the_reply_and_the_career_and_lvl_10_gives_the_title_achievement()
    {
        var (h, _) = Table(xp: Book.Total(10) - 10);
        Assert.Equal(9, Career(h).GetProperty("lvl").GetInt32());
        Haul(h);
        var r = h.Act(0, "deliver");
        Assert.True(r.Ok, r.Message);
        Assert.Contains("рівень 10", r.Message);
        Assert.Equal(10, Career(h).GetProperty("last").GetProperty("up").GetInt32());
        Assert.Contains(h.Awards, a => a.Reason == "ach:fura-chumak" && a.Shards == 0);
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:fura-otaman");
    }

    [Fact]
    public void Retro_xp_on_first_open_is_trips_times_80_capped_at_level_8_and_only_once()
    {
        var (h, _) = Table(trips: 5);
        Assert.Equal(400, Career(h).GetProperty("xp").GetInt64());
        Assert.Contains("\"xp\":400", h.Store.LoadState(FuraCareer.Key("оля")));

        var (big, _) = Table(trips: 300);
        Assert.Equal(2100, Career(big).GetProperty("xp").GetInt64());
        Assert.Equal(8, Career(big).GetProperty("lvl").GetInt32());

        // запис уже є — ретро вдруге не дається, а старі рейси ДВ не дають (номер наступного — 301)
        Deliver(big);
        var xp = Career(big).GetProperty("xp").GetInt64();
        Assert.True(xp > 2100);
        Assert.True(big.Leave("Оля").Ok);
        Assert.True(big.Solo("Оля").Ok, big.Reply.Message);
        Assert.Equal(xp, Career(big).GetProperty("xp").GetInt64());
    }

    // ---------- покупки ----------

    [Fact]
    public void Buy_needs_the_level_and_the_shards_and_pays_once_per_ref()
    {
        var (low, lw) = Table(xp: Book.Total(5));
        var no = low.Act(0, "buy", new { id = "vil" });
        Assert.False(no.Ok);
        Assert.Equal("Це відкриється з рівня 6", no.Message);
        Assert.Empty(lw.Calls);

        var (h, w) = Table(xp: Book.Total(6), balance: 9000);
        var r = h.Act(0, "buy", new { id = "vil" });
        Assert.True(r.Ok, r.Message);
        Assert.Equal(["spend:Оля:8000:fura-buy:оля:vil"], w.Calls);
        Assert.Equal("fura-buy:vil", w.Reasons["fura-buy:оля:vil"]);
        Assert.Equal(1000, w.Balance("Оля"));
        var career = Career(h);
        Assert.Contains("vil", career.GetProperty("owned").GetProperty("tractors").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("vil", career.GetProperty("pick").GetProperty("tractor").GetString());   // купив — одразу їде ним
        Assert.Equal(470000.0, Params(h).GetProperty("power").GetDouble());
        Assert.Contains("\"vil\"", h.Store.LoadState(FuraCareer.Key("оля")));

        // подвійний клік — «уже твоє», другого списання нема
        var again = h.Act(0, "buy", new { id = "vil" });
        Assert.False(again.Ok);
        Assert.Equal("Уже твоє", again.Message);
        Assert.Single(w.Calls);

        // бракує черепків
        var poor = h.Act(0, "buy", new { id = "izoterm" });
        Assert.False(poor.Ok);
        Assert.Equal("Бракує 3000 🏺", poor.Message);
    }

    [Fact]
    public void A_purchase_lost_before_the_garage_was_saved_does_not_pay_twice()
    {
        var (h, w) = Table(xp: Book.Total(6), balance: 9000);
        Assert.True(w.TrySpend("Оля", 8000, "fura-buy:vil", "fura-buy:оля:vil"));   // списано, а гараж не записався
        Assert.True(h.Act(0, "buy", new { id = "vil" }).Ok, h.Reply.Message);
        Assert.Equal(1000, w.Balance("Оля"));
    }

    [Fact]
    public void Tune_steps_go_in_order_and_from_their_level()
    {
        var (h, w) = Table(xp: Book.Total(9));
        Assert.Equal("Спершу попередній щабель", h.Act(0, "buy", new { id = "engine2" }).Message);
        Assert.True(h.Act(0, "buy", new { id = "engine1" }).Ok, h.Reply.Message);
        Assert.True(h.Act(0, "buy", new { id = "engine2" }).Ok, h.Reply.Message);
        Assert.True(h.Act(0, "buy", new { id = "engine3" }).Ok, h.Reply.Message);
        Assert.Equal("Це відкриється з рівня 13", h.Act(0, "buy", new { id = "engine4" }).Message);
        Assert.Equal("Уже твоє", h.Act(0, "buy", new { id = "engine1" }).Message);
        Assert.Equal(3, Career(h).GetProperty("tune").GetProperty("engine").GetInt32());
        Assert.Equal(0, Career(h).GetProperty("tune").GetProperty("susp").GetInt32());
        Assert.Equal(460000.0, Params(h).GetProperty("power").GetDouble());
        Assert.Equal(["spend:Оля:500:fura-buy:оля:engine1", "spend:Оля:1000:fura-buy:оля:engine2", "spend:Оля:2000:fura-buy:оля:engine3"], w.Calls);
        Assert.Equal("Такого в гаражі нема", h.Act(0, "buy", new { id = "turbo1" }).Message);
    }

    [Fact]
    public void Guests_drive_the_plain_chumak_and_cannot_buy_but_their_xp_is_kept()
    {
        var (h, w) = Table(xp: Book.Total(12), account: _ => false,
            career: $$$"""{"xp":{{{Book.Total(12)}}},"owned":{"tractors":["chumak","lastivka"]},"tune":{"engine":2},"pick":{"tractor":"lastivka"}}""");
        Assert.False(Career(h).GetProperty("account").GetBoolean());
        Assert.Equal(Fura.NotAccount, h.Act(0, "buy", new { id = "izoterm" }).Message);
        Assert.Equal(Fura.NotAccount, h.Act(0, "pick", new { tractor = "chumak" }).Message);
        Assert.Empty(w.Calls);
        Assert.Equal("chumak", Params(h).GetProperty("tractor").GetString());
        Assert.Equal(400000.0, Params(h).GetProperty("power").GetDouble());
        Assert.Equal(5, h.View(0).GetProperty("board").GetArrayLength());   // без далекого й без нових причепів
        var xp = Career(h).GetProperty("xp").GetInt64();
        Deliver(h);
        Assert.True(Career(h).GetProperty("xp").GetInt64() > xp);
    }

    [Fact]
    public void Garage_acts_only_between_trips()
    {
        var (h, _) = Table(xp: Book.Total(6));
        Assert.True(h.Act(0, "take", new { id = 1 }).Ok, h.Reply.Message);
        Assert.Equal("Гараж — між рейсами", h.Act(0, "buy", new { id = "vil" }).Message);
        Assert.Equal("Гараж — між рейсами", h.Act(0, "pick", new { tractor = "chumak" }).Message);
        Assert.True(h.Act(0, "garage").Ok);   // перечитати вигляд можна будь-коли
    }

    [Fact]
    public void Pick_needs_an_owned_thing_and_special_trailers_hitch_themselves()
    {
        var (h, _) = Table(career: """{"xp":99999,"owned":{"tractors":["chumak","vil"],"trailers":["tent","puh","izoterm"]}}""");
        Assert.Equal("Спершу купи", h.Act(0, "pick", new { tractor = "lastivka" }).Message);
        Assert.Equal("Цей причіп чіпляється сам під свій вантаж", h.Act(0, "pick", new { trailer = "izoterm" }).Message);
        Assert.Equal("Що обрати?", h.Act(0, "pick", new { }).Message);
        Assert.True(h.Act(0, "pick", new { trailer = "puh" }).Ok, h.Reply.Message);
        Assert.True(h.Act(0, "pick", new { tractor = "vil" }).Ok, h.Reply.Message);
        Assert.Equal("puh", Params(h).GetProperty("trailer").GetString());
        Assert.Equal(0.8, Params(h).GetProperty("hurt").GetDouble());
        Assert.Equal("vil", h.View(0).GetProperty("convoy")[0].GetProperty("car").GetProperty("tractor").GetString());
        Assert.Contains("\"puh\"", h.Store.LoadState(FuraCareer.Key("оля")));
    }

    // ---------- «⚖ Рівні фури» і вимикач ----------

    const string Tuned = """{"xp":99999,"owned":{"tractors":["chumak","vil"],"trailers":["tent","puh","izoterm","low"]},"tune":{"engine":4,"susp":5},"pick":{"tractor":"vil","trailer":"puh"}}""";

    [Fact]
    public void Equal_trucks_table_gives_everyone_the_plain_chumak_and_a_tent_board()
    {
        var (own, _) = Table(career: Tuned);
        Assert.Equal(560000.0, Params(own).GetProperty("power").GetDouble());
        Assert.Equal(6, own.View(0).GetProperty("board").GetArrayLength());   // рівень ≥ 10 — далеке шостим

        var (h, _) = Table(career: Tuned, options: new { equal = "on" });
        var career = Career(h);
        Assert.True(career.GetProperty("equal").GetBoolean());
        var p = career.GetProperty("params");
        Assert.Equal("chumak", p.GetProperty("tractor").GetString());
        Assert.Equal("tent", p.GetProperty("trailer").GetString());
        Assert.Equal(400000.0, p.GetProperty("power").GetDouble());
        Assert.Equal(1.0, p.GetProperty("hurt").GetDouble());
        Assert.Equal("chumak", h.View(0).GetProperty("convoy")[0].GetProperty("car").GetProperty("tractor").GetString());
        var board = h.View(0).GetProperty("board").EnumerateArray().ToList();
        Assert.Equal(5, board.Count);
        Assert.All(board, o => Assert.Equal("tent", Map.CargoOf(o.GetProperty("cargo").GetString()!)!.Trailer));
        Assert.True(h.Act(0, "pick", new { tractor = "chumak" }).Ok, h.Reply.Message);   // свій гараж міняти можна, їде однаково
    }

    [Fact]
    public void Garage_switch_off_closes_buying_and_flattens_params_but_xp_still_counts()
    {
        var (h, w) = Table(career: Tuned, garage: false);
        Assert.False(Career(h).GetProperty("on").GetBoolean());
        Assert.Equal("Гараж зачинено", h.Act(0, "buy", new { id = "lastivka" }).Message);
        Assert.Equal("Гараж зачинено", h.Act(0, "pick", new { tractor = "chumak" }).Message);
        Assert.Empty(w.Calls);
        Assert.Equal(400000.0, Params(h).GetProperty("power").GetDouble());
        var xp = Career(h).GetProperty("xp").GetInt64();
        Deliver(h);
        Assert.True(Career(h).GetProperty("xp").GetInt64() > xp);
    }

    // ---------- дошка ----------

    [Fact]
    public void Board_carries_only_cargo_for_owned_trailers_and_the_convoy_order_is_always_tent()
    {
        var seen = new HashSet<string>();
        for (var trips = 0; trips < 60; trips++)
        {
            foreach (var o in Fura.MakeBoard(Map, "2026-10-25", "оля", trips, ["tent", "izoterm"]))
            {
                var t = Map.CargoOf(o.Cargo)!.Trailer;
                Assert.NotEqual("low", t);
                seen.Add(t);
            }
            Assert.All(Fura.MakeBoard(Map, "2026-10-25", "оля", trips), o => Assert.Equal("tent", Map.CargoOf(o.Cargo)!.Trailer));
            Assert.Equal("tent", Map.CargoOf(Fura.MakeColOrder(Map, "2026-10-25", "room" + trips, 1)!.Cargo)!.Trailer);
        }
        Assert.Contains("izoterm", seen);
        // без причепів — та сама дошка, що й до гаража (null = тент)
        Assert.Equal(Fura.MakeBoard(Map, "2026-10-25", "оля", 3), Fura.MakeBoard(Map, "2026-10-25", "оля", 3, ["tent"]));
    }

    [Fact]
    public void Ice_cream_has_a_shorter_term()
    {
        var ice = Map.CargoOf("icecream")!;
        Assert.Equal("izoterm", ice.Trailer);
        Assert.Equal(0.8, ice.Term);
        Assert.Equal("low", Map.CargoOf("bell")!.Trailer);
        Assert.Equal("low", Map.CargoOf("tractor")!.Trailer);
        Assert.Equal("izoterm", Map.CargoOf("milk")!.Trailer);
        Assert.Equal(1.0, Map.CargoOf("jugs")!.Term);
        var o = Enumerable.Range(0, 200).SelectMany(t => Fura.MakeBoard(Map, "2026-10-25", "оля", t, ["tent", "izoterm"]))
            .First(x => x.Cargo == "icecream");
        Assert.Equal((int)Math.Ceiling(FuraJudge.Minutes(o.Km) * 0.8 - 1e-9), o.Minutes);
    }

    [Fact]
    public void Far_order_from_level_10_is_the_longest_of_the_rest_and_gives_more_xp()
    {
        var board = Fura.MakeBoard(Map, "2026-10-25", "оля", 7, ["tent"], far: true);
        Assert.Equal(6, board.Count);
        var far = board[^1];
        Assert.True(far.Far);
        Assert.Equal(6, far.Id);
        Assert.Equal(Fura.MakeBoard(Map, "2026-10-25", "оля", 7), board.Take(5));   // п'ять своїх — ті самі
        Assert.All(board.Take(5), o => Assert.False(o.Far));

        var (h, _) = Table(xp: Book.Total(10));
        var view = h.View(0).GetProperty("board");
        Assert.Equal(6, view.GetArrayLength());
        Assert.True(view[5].GetProperty("far").GetBoolean());
        Assert.False(view[0].GetProperty("far").GetBoolean());
        Haul(h, id: 6);
        Assert.True(h.Act(0, "deliver").Ok, h.Reply.Message);
        var km = view[5].GetProperty("km").GetDouble();
        var expected = (int)Math.Round(Math.Round(20 + 25 * km, MidpointRounding.AwayFromZero) * 1.1 * 1.1 * 1.3, MidpointRounding.AwayFromZero);
        Assert.Equal(expected, h.View(0).GetProperty("result").GetProperty("xp").GetInt32());
    }

    [Fact]
    public void Buying_a_trailer_rebuilds_the_board_for_it()
    {
        var (h, _) = Table(xp: Book.Total(4));
        Assert.True(h.Act(0, "buy", new { id = "izoterm" }).Ok, h.Reply.Message);
        var board = h.View(0).GetProperty("board").EnumerateArray().Select(o => o.GetProperty("cargo").GetString()!).ToList();
        var expected = Fura.MakeBoard(Map, Days.Today(h.Clock), "оля", 0, ["tent", "izoterm"]).Select(o => o.Cargo).ToList();
        Assert.Equal(expected, board);
    }

    // ---------- колона: наставник ----------

    [Fact]
    public void A_mentor_and_a_newcomer_in_one_convoy_both_get_x1_2()
    {
        var wallet = new FakeStakes();
        RoomHarness? h = null;
        h = new RoomHarness("fura", seed: 42, services: Services(() => h!.Store, wallet));
        h.Store.SaveState(FuraCareer.Key("оля"), $$"""{"xp":{{Book.Total(8)}}}""");
        Assert.True(h.Solo("Оля").Ok, h.Reply.Message);
        Assert.True(h.Join("Петро").Ok, h.Reply.Message);
        Assert.True(h.Act(0, "take", new { id = "k" }).Ok, h.Reply.Message);
        Assert.True(h.Act(1, "take", new { id = "k" }).Ok, h.Reply.Message);
        var o0 = Haul(h, seat: 0, take: false);
        var o1 = Haul(h, seat: 1, take: false);
        foreach (var (o, s) in new[] { (o0, 0), (o1, 1) })
        {
            var t = To(o).Docks[0];
            h.Tick();
            Pose(h, t.X, t.Z, ry: t.Yaw, seat: s);
            Assert.True(h.Act(s, "deliver").Ok, h.Reply.Message);
        }
        foreach (var s in new[] { 0, 1 })
        {
            var last = Career(h, s).GetProperty("last");
            Assert.True(last.GetProperty("mentor").GetBoolean());
            var km = o0.GetProperty("km").GetDouble();
            var res = h.View(s).GetProperty("result");
            var x = Math.Round(20 + 25 * km, MidpointRounding.AwayFromZero) * 1.15 * 1.2;
            if (res.GetProperty("onTime").GetBoolean()) x *= 1.1;
            if (res.GetProperty("exact").GetBoolean()) x *= 1.1;
            Assert.Equal((int)Math.Round(x, MidpointRounding.AwayFromZero), last.GetProperty("xp").GetInt32());
        }
    }

    // ---------- вигляд з Лавки ----------

    [Fact]
    public void Worn_cosmetics_show_in_the_career_and_the_convoy_tag_and_garage_rereads_them()
    {
        var worn = new Dictionary<string, string> { ["fura-paint"] = "paintsky", ["fura-sticker2"] = "stickmama" };
        var (h, _) = Table(worn: _ => new Dictionary<string, string>(worn));
        var look = Career(h).GetProperty("look");
        Assert.Equal("paintsky", look.GetProperty("paint").GetString());
        Assert.Equal(["stickmama"], look.GetProperty("stickers").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(JsonValueKind.Null, look.GetProperty("horn").ValueKind);
        var tag = h.View(0).GetProperty("convoy")[0].GetProperty("car");
        Assert.Equal(1, tag.GetProperty("lvl").GetInt32());
        Assert.Equal("paintsky", tag.GetProperty("look").GetProperty("paint").GetString());

        worn["fura-horn"] = "horngoose";
        Assert.Equal(JsonValueKind.Null, Career(h).GetProperty("look").GetProperty("horn").ValueKind);   // ще старе
        Assert.True(h.Act(0, "garage").Ok);
        Assert.Equal("horngoose", Career(h).GetProperty("look").GetProperty("horn").GetString());
    }

    [Fact]
    public void Garbage_career_starts_over_with_retro_and_unknown_things_are_dropped()
    {
        var (h, _) = Table(career: "не json", trips: 3);
        Assert.Equal(240, Career(h).GetProperty("xp").GetInt64());

        var (x, _) = Table(career: """{"xp":-5,"owned":{"tractors":["kamaz"],"trailers":null},"tune":{"engine":9,"turbo":2},"pick":{"tractor":"kamaz","trailer":"low"}}""");
        var c = Career(x);
        Assert.Equal(0, c.GetProperty("xp").GetInt64());
        Assert.Equal(["chumak"], c.GetProperty("owned").GetProperty("tractors").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(["tent"], c.GetProperty("owned").GetProperty("trailers").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(4, c.GetProperty("tune").GetProperty("engine").GetInt32());
        Assert.False(c.GetProperty("tune").TryGetProperty("turbo", out _));
        Assert.Equal("chumak", c.GetProperty("pick").GetProperty("tractor").GetString());
        Assert.Equal("tent", c.GetProperty("pick").GetProperty("trailer").GetString());
    }

    [Fact]
    public void Garage_endpoint_and_voice_events_exist()
    {
        Assert.Equal(1, JsonDocument.Parse(Book.Raw).RootElement.GetProperty("v").GetInt32());
        var voice = FuraVoice.Load();
        Assert.InRange(voice.Lines["level_up"].Count, 2, 4);
        Assert.InRange(voice.Lines["buy"].Count, 2, 4);
        Assert.All(voice.Lines["level_up"].Concat(voice.Lines["buy"]), l => Assert.DoesNotMatch("[0-9]", l));
    }
}
