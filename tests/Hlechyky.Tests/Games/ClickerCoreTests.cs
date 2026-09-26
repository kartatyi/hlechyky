using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Дев'яте оновлення Гончарного кола, пакет «Коло» (docs/games/specs/clicker-v9.md §A): Око майстра платить
/// за чесну руку й не перебиває бонусів, серія без стелі, три верстати для кліків, секрети другого кола,
/// кіт-зірка-вітер на сцені, щедріші розписні глеки, драбина після Цар-глека й «Що нового» раз на гончаря.
/// </summary>
public class ClickerCoreTests
{
    static RoomHarness Wheel(string nick = "Оля", int seed = 1)
    {
        var h = new RoomHarness("clicker", seed: seed);
        h.Solo(nick);
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static double Pots(RoomHarness h) => View(h).GetProperty("pots").GetDouble();
    static double PerSecond(RoomHarness h) => View(h).GetProperty("perSecond").GetDouble();
    static double PerClick(RoomHarness h) => View(h).GetProperty("perClick").GetDouble();
    static JsonElement Guard(RoomHarness h) => View(h).GetProperty("guard");
    static bool Free(RoomHarness h) => Guard(h).ValueKind == JsonValueKind.Null;
    static JsonElement Events(RoomHarness h) => View(h).GetProperty("events");
    static ActResult Act(RoomHarness h, string action, object? payload = null) => h.Act(0, action, payload);
    static ActResult Human(RoomHarness h, int n = 1) => h.Act(0, "spin", PotterHands.Human(n));

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static void Give(RoomHarness h, double pots, double? total = null) => Patch(h, s =>
    {
        s["pots"] = pots;
        s["total"] = total ?? pots;
    });

    static void Levels(RoomHarness h, params (string Key, int Level)[] levels) => Patch(h, s =>
    {
        foreach (var (key, level) in levels) s["upgrades"]![key] = level;
    });

    static void Marks(RoomHarness h, params string[] keys) => Patch(h, s =>
    {
        var arr = new JsonArray();
        foreach (var k in keys) arr.Add(k);
        s["marks"] = arr;
    });

    /// <summary>Полиця от-от: наступний зарахований клік покличе майстра.</summary>
    static void ShelfDue(RoomHarness h) => Patch(h, s => { s["guard"]!["left"] = 0; s["guard"]!["clicks"] = ClickerGuard.CalmMin; });

    /// <summary>Поставити подію сцени просто зараз: <paramref name="field"/> — cat, star чи wind.</summary>
    static void EventNow(RoomHarness h, string field, TimeSpan shown, int a = 0, int b = 0) => Patch(h, s =>
    {
        var now = h.Clock.UtcNow;
        s[field] = new JsonObject
        {
            ["at"] = now.ToString("O"),
            ["until"] = (now + shown).ToString("O"),
            ["a"] = a,
            ["b"] = b,
        };
    });

    static void BonusFor(RoomHarness h, double seconds) =>
        Patch(h, s => s["fairUntil"] = (h.Clock.UtcNow + TimeSpan.FromSeconds(seconds)).ToString("O"));

    // ---------- A.1 Око майстра платить ----------

    [Fact]
    public void A_calm_shelf_passed_is_paid_for_two_hours_of_work_and_ten_thousand_clicks()
    {
        var h = Wheel();
        Levels(h, ("apprentice", 10));                 // 5 глеків за секунду
        ShelfDue(h);
        Human(h);
        Assert.False(Free(h));
        Assert.True(Guard(h).GetProperty("pays").GetBoolean());
        Assert.Equal(5 * 7200 + 1 * 10_000, Guard(h).GetProperty("gain").GetDouble());

        var before = Pots(h);
        var r = PotterHands.Pass(h);

        Assert.True(r.Ok);
        Assert.Contains("відсипав", r.Message);
        Assert.Equal(before + 46_000, Pots(h));
        Assert.True(Free(h));
    }

    [Fact]
    public void The_pay_is_told_in_short_numbers_with_the_right_word()
    {
        // «14,5 квдрлн глеки» різало б око: після скорочення слово узгоджується з «квдрлн», а не з останньою цифрою.
        var h = Wheel();
        Levels(h, ("sich", 77));
        ShelfDue(h);
        Human(h);
        var r = PotterHands.Pass(h);

        Assert.Contains("квдрлн глеків", r.Message);
        Assert.DoesNotContain("квдрлн глеки ", r.Message);
    }

    [Fact]
    public void A_shelf_after_a_suspicion_pays_nothing_because_an_autoclicker_owner_passes_it_too()
    {
        var h = Wheel();
        Levels(h, ("apprentice", 10));
        for (var i = 0; i < 3; i++) { h.Act(0, "spin", PotterHands.Robot(12)); h.Clock.Advance(1); }
        Assert.False(Free(h));
        Assert.Equal("rhythm", Guard(h).GetProperty("why").GetString());
        Assert.False(Guard(h).GetProperty("pays").GetBoolean());
        Assert.Equal(0d, Guard(h).GetProperty("gain").GetDouble());

        var before = Pots(h);
        var r = PotterHands.Pass(h);

        Assert.Equal("👁 Майстер кивнув — крути далі", r.Message);
        Assert.Equal(before, Pots(h));
    }

    [Fact]
    public void A_wary_shelf_right_after_a_suspicion_pays_nothing_either()
    {
        var h = Wheel();
        Levels(h, ("apprentice", 10));
        for (var i = 0; i < 3; i++) { h.Act(0, "spin", PotterHands.Robot(12)); h.Clock.Advance(1); }
        PotterHands.Pass(h);                           // підозру знято, але майстер ще пильнує
        Patch(h, s => s["guard"]!["left"] = 0);
        Human(h);

        Assert.False(Free(h));
        Assert.False(Guard(h).GetProperty("pays").GetBoolean());
        var before = Pots(h);
        PotterHands.Pass(h);
        Assert.Equal(before, Pots(h));
    }

    [Fact]
    public void A_miss_on_the_shelf_halves_the_pay()
    {
        var h = Wheel();
        Levels(h, ("apprentice", 10));
        ShelfDue(h);
        Human(h);
        PotterHands.Miss(h);
        Assert.Equal(1, Guard(h).GetProperty("misses").GetInt32());
        Assert.True(Guard(h).GetProperty("pays").GetBoolean());
        Assert.Equal(23_000, Guard(h).GetProperty("gain").GetDouble());

        var before = Pots(h);
        var r = PotterHands.Pass(h);
        Assert.Contains("половину", r.Message);
        Assert.Equal(before + 23_000, Pots(h));
    }

    [Fact]
    public void A_pause_for_three_misses_does_not_pay_even_after_it_ends()
    {
        var h = Wheel();
        Levels(h, ("apprentice", 10));
        ShelfDue(h);
        Human(h);
        for (var i = 0; i < ClickerGuard.MaxMisses; i++) PotterHands.Miss(h);
        h.Clock.Advance(ClickerGuard.LockFor);

        Assert.False(Guard(h).GetProperty("pays").GetBoolean());
        var before = Pots(h);
        PotterHands.Pass(h);
        Assert.Equal(before, Pots(h));
    }

    [Fact]
    public void Ten_calm_shelves_are_an_achievement()
    {
        var h = Wheel();
        Patch(h, s => s["guard"]!["calmPassed"] = 9);
        ShelfDue(h);
        Human(h);
        PotterHands.Pass(h);

        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-eye");
        Assert.Equal(10, Passed(h));
    }

    static int Passed(RoomHarness h)
    {
        lock (h.Room.Sync) return JsonNode.Parse(h.Room.Game.Save()!)!["guard"]!["calmPassed"]!.GetValue<int>();
    }

    [Fact]
    public void The_calm_flag_survives_save_and_load()
    {
        var h = Wheel();
        Levels(h, ("apprentice", 10));
        ShelfDue(h);
        Human(h);
        Assert.True(Guard(h).GetProperty("pays").GetBoolean());

        Patch(h, _ => { });                            // Save → Load без правок
        Assert.True(Guard(h).GetProperty("pays").GetBoolean());
        Assert.Equal(46_000, Guard(h).GetProperty("gain").GetDouble());
    }

    [Fact]
    public void An_old_save_without_the_calm_flag_does_not_pay_for_the_shelf_it_already_had()
    {
        var h = Wheel();
        Levels(h, ("apprentice", 10));
        ShelfDue(h);
        Human(h);
        Patch(h, s => ((JsonObject)s["guard"]!).Remove("calm"));

        Assert.False(Guard(h).GetProperty("pays").GetBoolean());
        var before = Pots(h);
        PotterHands.Pass(h);
        Assert.Equal(before, Pots(h));
    }

    // ---------- A.2 Око не перебиває ----------

    [Fact]
    public void The_master_waits_out_the_fair_before_asking()
    {
        var h = Wheel();
        BonusFor(h, 60);
        ShelfDue(h);
        Human(h);
        Assert.True(Free(h));                          // бонус тікає секундами — полиця почекає

        h.Clock.Advance(61);
        Human(h);
        Assert.False(Free(h));
    }

    [Fact]
    public void The_master_waits_out_a_jug_in_flight()
    {
        var h = Wheel();
        Patch(h, s => s["fall"] = new JsonObject
        {
            ["at"] = h.Clock.UtcNow.ToString("O"),
            ["until"] = (h.Clock.UtcNow + Clicker.FallShown).ToString("O"),
            ["x"] = 40,
        });
        ShelfDue(h);
        Human(h);
        Assert.True(Free(h));
    }

    [Fact]
    public void The_master_waits_out_the_cat_on_the_scene()
    {
        var h = Wheel();
        EventNow(h, "cat", Clicker.CatShown);
        ShelfDue(h);
        Human(h);
        Assert.True(Free(h));
    }

    [Fact]
    public void A_jug_slept_through_under_the_shelf_comes_back_after_the_nod()
    {
        var h = Wheel();
        Patch(h, s => s["fallStreak"] = 4);
        ShelfDue(h);
        Human(h);
        Assert.False(Free(h));

        h.Clock.Advance(600);                          // і глек з полиці, і розписний минули, поки майстер чекав
        Act(h, "look");
        Assert.Equal(4, View(h).GetProperty("fall").GetProperty("streak").GetInt32());

        PotterHands.Pass(h);
        var fall = View(h).GetProperty("fall");
        var wait = (fall.GetProperty("at").GetDateTimeOffset() - h.Clock.UtcNow).TotalSeconds;
        Assert.InRange(wait, Clicker.FallMinSeconds, Clicker.FallMaxSeconds);
        var golden = (View(h).GetProperty("golden").GetProperty("at").GetDateTimeOffset() - h.Clock.UtcNow).TotalSeconds;
        Assert.InRange(golden, Clicker.GoldenMinSeconds, Clicker.GoldenMaxSeconds);
    }

    // ---------- A.3 серія без стелі ----------

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 0.5)]
    [InlineData(10, 1.0)]
    [InlineData(25, 1.3)]
    [InlineData(37, 1.54)]
    [InlineData(100, 2.8)]
    public void The_streak_adds_ten_percent_up_to_ten_and_two_after(int streak, double bonus)
    {
        var h = Wheel();
        Patch(h, s => s["fallStreak"] = streak);
        Assert.Equal(bonus, View(h).GetProperty("fall").GetProperty("bonus").GetDouble(), 6);
    }

    // ---------- A.4 кліки в пізній грі ----------

    [Fact]
    public void The_swing_puts_another_percent_of_the_passive_into_every_click()
    {
        var h = Wheel();
        Levels(h, ("kiln", 100));                      // 300 глеків за секунду
        Assert.Equal(1, PerClick(h));

        Levels(h, ("swing", 10));                      // +10 % пасиву в клік
        Assert.Equal(31, PerClick(h));                 // 1 + 30
    }

    [Fact]
    public void The_million_clicking_workbenches_do_not_scare_a_beginner()
    {
        // Новачок із двадцятьма дев'ятьма глеками не мусить дивитись на «5 млрд»: маховик і кошик видно, як і було,
        // а нові три з'являються, коли гончар наліпив хоч двадцяту частину їхньої ціни.
        var h = Wheel();
        bool Open(string key) => View(h).GetProperty("upgrades").GetProperty(key).GetProperty("open").GetBoolean();
        Assert.True(Open("flywheel"));
        Assert.True(Open("basket"));
        Assert.False(Open("swing"));
        Assert.False(Open("temper"));
        Assert.False(Open("lucky"));

        Give(h, 100, 2_500_000);
        Assert.True(Open("swing"));
        Assert.False(Open("temper"));

        Give(h, 100, 25_000_000);
        Assert.True(Open("temper"));

        // А куплений верстат видно завжди — навіть після обпалу, поки рівень не згорів.
        Levels(h, ("lucky", 1));
        Assert.True(Open("lucky"));
    }

    [Fact]
    public void The_temper_keeps_the_wheel_hot_a_second_longer_per_level()
    {
        var h = Wheel();
        Assert.Equal(Clicker.HeatTau, View(h).GetProperty("heatTau").GetDouble());

        Levels(h, ("temper", 5));
        Assert.Equal(Clicker.HeatTau + 5, View(h).GetProperty("heatTau").GetDouble());
    }

    [Fact]
    public void A_lucky_click_hits_fifty_times_about_once_in_twenty()
    {
        var h = Wheel(seed: 7);
        Levels(h, ("kiln", 100), ("lucky", 5));        // 5 % кліків
        var clicks = 0;
        for (var i = 0; i < 100; i++)
        {
            Human(h, 12);
            clicks += 12;
            h.Clock.Advance(1);
            if (!Free(h)) PotterHands.Pass(h);
        }

        var lucky = View(h).GetProperty("lucky").GetInt64();
        // 1200 кліків по 5 % — близько шістдесяти; межі широкі навмисно: це кидок, а не таблиця.
        Assert.InRange(lucky, 25, 110);
        Assert.Equal(1200, clicks);
    }

    [Fact]
    public void Without_the_workbench_no_click_is_ever_lucky()
    {
        var h = Wheel(seed: 7);
        Levels(h, ("kiln", 100));
        for (var i = 0; i < 40; i++) { Human(h, 12); h.Clock.Advance(1); if (!Free(h)) PotterHands.Pass(h); }
        Assert.Equal(0, View(h).GetProperty("lucky").GetInt64());
    }

    [Fact]
    public void A_lucky_click_really_brings_fifty_clicks_worth()
    {
        // Один рівень «Щасливого кліка» — один відсоток; ганяємо, доки не влучить, і дивимось на різницю.
        var h = Wheel(seed: 3);
        Levels(h, ("kiln", 100), ("lucky", 5));
        double lucky = 0, pots = Pots(h);
        for (var i = 0; i < 200 && lucky == 0; i++)
        {
            var before = Pots(h);
            var had = View(h).GetProperty("lucky").GetInt64();
            Human(h, 1);
            h.Clock.Advance(3);                        // розгін спадає — множник кола не плутає рахунок
            lucky = View(h).GetProperty("lucky").GetInt64() - had;
            pots = Pots(h) - before;
            if (!Free(h)) { PotterHands.Pass(h); lucky = 0; }
        }
        Assert.Equal(1, lucky);
        // Клік коштує 1 глек (пасив у клік не йде без «Замашної руки»), щасливий — п'ятдесят;
        // пасив за три секунди між кліками теж у різниці, тож дивимось «не менше».
        Assert.True(pots >= 50, $"щасливий клік дав {pots}");
    }

    [Fact]
    public void A_hundred_lucky_clicks_are_an_achievement()
    {
        var h = Wheel(seed: 5);
        Patch(h, s => s["lucky"] = 99);
        Levels(h, ("lucky", 5));
        for (var i = 0; i < 60 && !h.Awards.Any(a => a.Reason == "ach:potter-lucky"); i++)
        {
            Human(h, 12);
            h.Clock.Advance(1);
            if (!Free(h)) PotterHands.Pass(h);
        }
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-lucky");
    }

    [Fact]
    public void The_firing_burns_the_new_skill_workbenches_like_the_old_ones()
    {
        var h = Wheel();
        Levels(h, ("swing", 10), ("temper", 5), ("lucky", 5));
        Give(h, 1_000, 4_000_000_000);
        Assert.True(Act(h, "fire").Ok);

        foreach (var key in new[] { "swing", "temper", "lucky" })
            Assert.Equal(0, View(h).GetProperty("upgrades").GetProperty(key).GetProperty("level").GetInt32());
    }

    [Fact]
    public void Six_clicks_a_second_with_the_swing_and_the_flywheel_beat_four_passives()
    {
        // Ціль контракту §A.4: з повними «Замашною рукою» й «Маховиком» рука дає ~4 пасиви (було ~0,9).
        var h = Wheel();
        Levels(h, ("kiln", 1_000), ("swing", 10), ("flywheel", 8));
        Marks(h, "wheel:25", "wheel:50");              // «Легка рука» й «Руки майстра» — ще 3 % пасиву в клік
        var v = View(h);
        var perClick = v.GetProperty("perClick").GetDouble();
        var perSecond = v.GetProperty("perSecond").GetDouble();
        var momentum = v.GetProperty("momentumMax").GetDouble();

        var byHand = perClick * 6 * momentum;
        Assert.InRange(byHand / perSecond, 3.5, 4.5);
    }

    // ---------- A.5 секрети другого кола ----------

    [Theory]
    [InlineData("rack2", 150)]
    [InlineData("grandson", 250)]
    [InlineData("caravan", 400)]
    [InlineData("barn", 600)]
    [InlineData("bell", 900)]
    [InlineData("ember", 1_200)]
    [InlineData("ashes", 2_000)]
    public void The_second_ring_of_secrets_is_in_the_catalog_at_its_price(string key, int price)
    {
        var h = Wheel();
        var secret = Assert.Single(View(h).GetProperty("secrets").EnumerateArray(),
            s => s.GetProperty("key").GetString() == key);
        Assert.Equal(price, secret.GetProperty("price").GetInt32());
        Assert.Equal(2, secret.GetProperty("ring").GetInt32());
        Assert.NotEmpty(secret.GetProperty("desc").GetString()!);
    }

    [Fact]
    public void The_first_ring_of_secrets_did_not_move()
    {
        var h = Wheel();
        var ring1 = View(h).GetProperty("secrets").EnumerateArray()
            .Where(s => s.GetProperty("ring").GetInt32() == 1).Select(s => s.GetProperty("key").GetString() ?? "").ToArray();
        Assert.Equal(new[] { "night", "omen", "cat", "kin", "longfair", "recipe", "memory", "seal" }, ring1);
    }

    [Fact]
    public void The_grandfathers_chest_keeps_a_twentieth_of_the_pots_through_the_firing()
    {
        var h = Wheel();
        Give(h, 1_000_000, 4e18);
        Patch(h, s => s["stamps"] = 2_000);
        Assert.True(Act(h, "secret", new { key = "ashes" }).Ok);

        Assert.True(Act(h, "fire").Ok);
        Assert.Equal(50_000, Pots(h));
    }

    [Fact]
    public void Without_the_chest_the_firing_takes_everything_as_before()
    {
        var h = Wheel();
        Give(h, 1_000_000, 4e18);
        Assert.True(Act(h, "fire").Ok);
        Assert.Equal(0, Pots(h));
    }

    // ---------- A.6 кіт, зірка, вітер ----------

    [Fact]
    public void The_cat_walks_the_scene_and_a_stroke_brings_five_minutes_of_work()
    {
        var h = Wheel();
        Levels(h, ("kiln", 100));                      // 300 глеків за секунду
        EventNow(h, "cat", Clicker.CatShown, a: (int)Clicker.CatGift.Passive);
        var before = Pots(h);

        var r = Act(h, "pet");

        Assert.True(r.Ok);
        Assert.Contains("🐈", r.Message);
        Assert.Equal(before + 300 * 300, Pots(h));
    }

    [Fact]
    public void The_cat_can_bring_straw_a_ringing_ware_or_a_jug_for_the_streak()
    {
        var h = Wheel();
        EventNow(h, "cat", Clicker.CatShown, a: (int)Clicker.CatGift.Straw);
        Assert.True(Act(h, "pet").Ok);
        Assert.Equal(3, View(h).GetProperty("kiln").GetProperty("straw").GetInt32());

        EventNow(h, "cat", Clicker.CatShown, a: (int)Clicker.CatGift.Ware);
        Assert.True(Act(h, "pet").Ok);
        var item = Assert.Single(View(h).GetProperty("craft").GetProperty("items").EnumerateArray());
        Assert.Equal(3, item.GetProperty("q").GetInt32());
        Assert.Equal("", item.GetProperty("style").GetString());

        EventNow(h, "cat", Clicker.CatShown, a: (int)Clicker.CatGift.Streak);
        Assert.True(Act(h, "pet").Ok);
        Assert.Equal(1, View(h).GetProperty("fall").GetProperty("streak").GetInt32());
    }

    [Fact]
    public void A_cat_that_already_ran_past_is_not_stroked()
    {
        var h = Wheel();
        EventNow(h, "cat", Clicker.CatShown);
        h.Clock.Advance(Clicker.CatShown.TotalSeconds + Clicker.CatchGrace.TotalSeconds + 1);

        var r = Act(h, "pet");
        Assert.False(r.Ok);
        Assert.Contains("побіг", r.Message);
    }

    [Fact]
    public void The_stroked_cat_is_replaced_by_the_next_one_in_six_to_fourteen_minutes()
    {
        var h = Wheel();
        EventNow(h, "cat", Clicker.CatShown);
        Assert.True(Act(h, "pet").Ok);

        var at = Events(h).GetProperty("cat").GetProperty("at").GetDateTimeOffset();
        Assert.InRange((at - h.Clock.UtcNow).TotalSeconds, Clicker.CatMinWait, Clicker.CatMaxWait);
        Assert.Equal(1, Events(h).GetProperty("petted").GetInt32());
    }

    [Fact]
    public void Twenty_five_cats_are_an_achievement()
    {
        var h = Wheel();
        Patch(h, s => s["petted"] = 24);
        EventNow(h, "cat", Clicker.CatShown);
        Assert.True(Act(h, "pet").Ok);
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-cat");
    }

    [Fact]
    public void A_wish_on_a_falling_star_triples_the_next_jug_from_the_shelf()
    {
        var h = Wheel();
        Levels(h, ("apprentice", 10));
        EventNow(h, "star", Clicker.StarShown);
        var plain = View(h).GetProperty("fall").GetProperty("gain").GetDouble();

        var r = Act(h, "wish");

        Assert.True(r.Ok);
        Assert.True(View(h).GetProperty("starWish").GetBoolean());
        Assert.Equal((plain - Clicker.FallFloor) * 3 + Clicker.FallFloor, View(h).GetProperty("fall").GetProperty("gain").GetDouble());

        // І розписний глек приходить за пів хвилини, а не за кілька.
        var golden = View(h).GetProperty("golden").GetProperty("at").GetDateTimeOffset();
        Assert.Equal(Clicker.StarGoldenSeconds, (golden - h.Clock.UtcNow).TotalSeconds, 1);
    }

    [Fact]
    public void The_wish_burns_on_the_jug_it_tripled()
    {
        var h = Wheel();
        Levels(h, ("apprentice", 10));
        EventNow(h, "star", Clicker.StarShown);
        Assert.True(Act(h, "wish").Ok);

        Patch(h, s => s["fall"] = new JsonObject
        {
            ["at"] = h.Clock.UtcNow.ToString("O"),
            ["until"] = (h.Clock.UtcNow + Clicker.FallShown).ToString("O"),
            ["x"] = 40,
        });
        var r = Act(h, "grab");
        Assert.Contains("зірка не збрехала", r.Message);
        Assert.False(View(h).GetProperty("starWish").GetBoolean());
    }

    [Fact]
    public void A_star_that_already_faded_grants_no_wish()
    {
        var h = Wheel();
        EventNow(h, "star", Clicker.StarShown);
        h.Clock.Advance(Clicker.StarShown.TotalSeconds + Clicker.CatchGrace.TotalSeconds + 1);
        Assert.False(Act(h, "wish").Ok);
    }

    [Fact]
    public void Stars_fall_only_at_night_over_kyiv()
    {
        // Годинник стоїть на 12:00 UTC — це 15:00 у Києві, і зірка чекає вечора.
        var h = Wheel();
        for (var i = 0; i < 12; i++)
        {
            var at = Events(h).GetProperty("star").GetProperty("at").GetDateTimeOffset();
            var hour = TimeZoneInfo.ConvertTime(at, Days.Kyiv).Hour;
            Assert.True(hour >= Clicker.NightFrom || hour < Clicker.NightTo, $"зірка на {hour} годину");
            h.Clock.UtcNow = at + Clicker.StarShown + TimeSpan.FromSeconds(1);
            Act(h, "look");
        }
    }

    [Fact]
    public void The_wind_from_the_field_triples_the_passive_for_three_quarters_of_a_minute()
    {
        var h = Wheel();
        Levels(h, ("kiln", 1));                        // 3 глеки за секунду
        EventNow(h, "wind", Clicker.WindShown);
        Assert.Equal(9, PerSecond(h));

        var before = Pots(h);
        h.Clock.Advance(60);
        Act(h, "look");
        // 45 секунд утричі плюс 15 звичайних.
        Assert.Equal(before + 45 * 3 * 3 + 15 * 3, Pots(h));
        Assert.Equal(3, PerSecond(h));
    }

    [Fact]
    public void The_wind_does_not_blow_for_those_who_were_not_at_the_wheel()
    {
        var h = Wheel();
        Levels(h, ("kiln", 1));
        EventNow(h, "wind", Clicker.WindShown);
        var before = Pots(h);

        h.Clock.Advance(TimeSpan.FromHours(1));        // гончар пішов — подія на сцені його не чекала
        Act(h, "look");

        Assert.Equal(before + 3600 * 3, Pots(h));
        var at = Events(h).GetProperty("wind").GetProperty("at").GetDateTimeOffset();
        Assert.InRange((at - h.Clock.UtcNow).TotalSeconds, Clicker.WindMinWait, Clicker.WindMaxWait);
    }

    [Fact]
    public void The_events_are_scheduled_within_their_windows()
    {
        var h = Wheel();
        var ev = Events(h);
        var cat = (ev.GetProperty("cat").GetProperty("at").GetDateTimeOffset() - h.Clock.UtcNow).TotalSeconds;
        var wind = (ev.GetProperty("wind").GetProperty("at").GetDateTimeOffset() - h.Clock.UtcNow).TotalSeconds;
        Assert.InRange(cat, Clicker.CatMinWait, Clicker.CatMaxWait);
        Assert.InRange(wind, Clicker.WindMinWait, Clicker.WindMaxWait);
        Assert.Equal(Clicker.CatShown,
            ev.GetProperty("cat").GetProperty("until").GetDateTimeOffset() - ev.GetProperty("cat").GetProperty("at").GetDateTimeOffset());
    }

    // ---------- A.7 розписні глеки ----------

    [Fact]
    public void The_three_golden_jugs_are_worth_the_same_order_of_magnitude_late_in_the_game()
    {
        // Контракт §A.7: на пасиві ~1e12 і кишені 1e13 усі три дають 4–8 хвилин роботи (з кліками 6/с і
        // «Маховиком»; «Замашна рука» — верстат §A.4, вона піднімає натхнення разом із самим кліком).
        var h = Wheel();
        Levels(h, ("sich", 77), ("flywheel", 8));      // 77 × 1,3e10 ≈ 1e12 глеків за секунду
        Patch(h, s => { s["pots"] = 1e13; s["total"] = 1e13; });
        var passive = PerSecond(h);
        Assert.InRange(passive, 9e11, 1.1e12);

        var flat = passive * Clicker.MerchantSeconds;
        var merchant = flat + Math.Min(1e13 * Clicker.MerchantShare, flat * Clicker.MerchantCapShare);
        // Ярмарок: 66 секунд усе ×7 — чистий приріст пасиву (кліки зверху).
        var fair = Clicker.FairFor.TotalSeconds * passive * (Clicker.FairMult - 1);
        // Натхнення: 20 секунд по 6 кліків із розгоном ×5, і кожен клік несе ще три відсотки пасиву — теж ×25.
        var perClick = PerClick(h) + passive * Clicker.InspireShare;
        var inspire = Clicker.InspireFor.TotalSeconds * 6 * perClick * Clicker.InspireMult * 5;

        foreach (var value in new[] { merchant, fair, inspire })
            Assert.InRange(value / passive, 240, 600);   // від чотирьох до десяти хвилин роботи
    }

    [Fact]
    public void The_golden_jug_rolls_fair_forty_merchant_and_inspire_thirty()
    {
        var h = Wheel(seed: 11);
        var kinds = new int[3];
        for (var i = 0; i < 400; i++)
        {
            h.Clock.UtcNow = View(h).GetProperty("golden").GetProperty("until").GetDateTimeOffset() + TimeSpan.FromSeconds(3);
            Act(h, "look");
            lock (h.Room.Sync) kinds[JsonNode.Parse(h.Room.Game.Save()!)!["golden"]!["kind"]!.GetValue<int>()]++;
        }
        Assert.InRange(kinds[(int)Clicker.GoldenKind.Fair] / 400.0, 0.30, 0.50);
        Assert.InRange(kinds[(int)Clicker.GoldenKind.Merchant] / 400.0, 0.21, 0.39);
        Assert.InRange(kinds[(int)Clicker.GoldenKind.Inspire] / 400.0, 0.21, 0.39);
    }

    // ---------- A.8 драбина після Цар-глека ----------

    [Theory]
    [InlineData("sloboda", 1e17, 3e8)]
    [InlineData("kontrakty", 2.5e18, 2e9)]
    [InlineData("sich", 6e19, 1.3e10)]
    public void The_ladder_goes_three_rungs_past_the_tsar_jug(string key, double price, double rate)
    {
        var up = Assert.Single(Clicker.Shop, u => u.Key == key);
        Assert.Equal(ClickerKind.Idle, up.Kind);
        Assert.Equal(price, up.Price(0));
        Assert.Equal(rate, up.Rate);
        Assert.Equal([25, 50, 100], up.Steps.Select(s => s.Level).ToArray());
        Assert.All(up.Steps, s => Assert.NotEmpty(s.Name));
    }

    [Fact]
    public void The_new_rungs_open_one_after_another_behind_the_tsar_jug()
    {
        var h = Wheel();
        Levels(h, ("tsar", 1));
        Assert.True(View(h).GetProperty("upgrades").GetProperty("sloboda").GetProperty("open").GetBoolean());
        Assert.False(View(h).GetProperty("upgrades").GetProperty("kontrakty").GetProperty("open").GetBoolean());

        Levels(h, ("sloboda", 1));
        Assert.True(View(h).GetProperty("upgrades").GetProperty("kontrakty").GetProperty("open").GetBoolean());
    }

    [Fact]
    public void A_rung_of_the_new_ladder_really_pays_its_rate()
    {
        var h = Wheel();
        Give(h, 2e17);
        Assert.True(Act(h, "buy", new { key = "sloboda", n = 1 }).Ok);
        Assert.Equal(3e8, PerSecond(h));
    }

    // ---------- A.9 «Що нового» ----------

    [Fact]
    public void A_potter_is_told_what_is_new_exactly_once()
    {
        var h = Wheel();
        // Новачок новин не бачить (для нього все нове); бачить той, чиє збереження старше за випуск — поле news у ньому порожнє.
        Assert.Equal(JsonValueKind.Null, View(h).GetProperty("news").ValueKind);
        Patch(h, s => s["news"] = "");
        Assert.Equal(Clicker.NewsVersion, View(h).GetProperty("news").GetString());

        Assert.True(Act(h, "news", new { v = Clicker.NewsVersion }).Ok);
        Assert.Equal(JsonValueKind.Null, View(h).GetProperty("news").ValueKind);

        Patch(h, _ => { });                            // і після перезавантаження сторінки теж
        Assert.Equal(JsonValueKind.Null, View(h).GetProperty("news").ValueKind);
    }

    [Fact]
    public void News_from_another_update_are_refused()
    {
        var h = Wheel();
        Patch(h, s => s["news"] = "");                 // збереження з часів до випуску
        Assert.False(Act(h, "news", new { v = "v8" }).Ok);
        Assert.Equal(Clicker.NewsVersion, View(h).GetProperty("news").GetString());
    }

    // ---------- старе збереження ----------

    [Fact]
    public void A_save_from_before_the_ninth_update_reads_as_this_was_not_there_yet()
    {
        var h = Wheel();
        Patch(h, s =>
        {
            foreach (var key in new[] { "lucky", "cat", "star", "wind", "petted", "starWish", "goldenSlept", "fallSlept", "news" })
                s.Remove(key);
        });

        var v = View(h);
        Assert.Equal(0, v.GetProperty("lucky").GetInt64());
        Assert.False(v.GetProperty("starWish").GetBoolean());
        Assert.Equal(Clicker.NewsVersion, v.GetProperty("news").GetString());
        Assert.Equal(0, v.GetProperty("events").GetProperty("petted").GetInt32());
        // Розклад подій — від «зараз»: старе збереження про них нічого не знало.
        var cat = (Events(h).GetProperty("cat").GetProperty("at").GetDateTimeOffset() - h.Clock.UtcNow).TotalSeconds;
        Assert.InRange(cat, Clicker.CatMinWait, Clicker.CatMaxWait);
    }

    [Fact]
    public void Everything_new_survives_save_and_load()
    {
        var h = Wheel();
        Levels(h, ("swing", 3), ("temper", 2), ("lucky", 1));
        Patch(h, s => { s["lucky"] = 42; s["petted"] = 7; s["starWish"] = true; });

        string Text() => Views.Text(View(h));
        var before = Text();
        Patch(h, _ => { });
        Assert.Equal(before, Text());
        Assert.Equal(42, View(h).GetProperty("lucky").GetInt64());
        Assert.Equal(7, Events(h).GetProperty("petted").GetInt32());
        Assert.True(View(h).GetProperty("starWish").GetBoolean());
    }
}
