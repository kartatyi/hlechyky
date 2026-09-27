using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Десяте оновлення «Глек на весь світ» (docs/games/specs/clicker-v10.md): дванадцять історичних щаблів після Січі,
/// віхи-модифікатори з тест-бюджетом, крива клейм із коліном на 4 млн, прадідівські секрети, гривня й червоні золоті
/// (вікно-церемонія), «Що нового» v10 з подарунком і ачівки.
/// </summary>
public class ClickerV10Tests
{
    static RoomHarness Wheel(string nick = "Оля", int seed = 1)
    {
        var h = new RoomHarness("clicker", seed: seed);
        h.Solo(nick);
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static double Num(RoomHarness h, string field) => View(h).GetProperty(field).GetDouble();
    static ActResult Act(RoomHarness h, string action, object? payload = null) => h.Act(0, action, payload);
    static JsonElement Up(RoomHarness h, string key) => View(h).GetProperty("upgrades").GetProperty(key);

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

    static void Strings(RoomHarness h, string field, params string[] values) => Patch(h, s =>
    {
        var arr = new JsonArray();
        foreach (var v in values) arr.Add(v);
        s[field] = arr;
    });

    static void Marks(RoomHarness h, params string[] keys) => Strings(h, "marks", keys);
    static void Secrets(RoomHarness h, params string[] keys) => Strings(h, "secrets", keys);

    static IEnumerable<ClickerMark> AllMarks => Clicker.Shop.SelectMany(u => u.Steps);
    static int CountOf(MarkEffect e) => AllMarks.Count(m => m.Effect == e);
    static double SumOf(MarkEffect e) => AllMarks.Where(m => m.Effect == e).Sum(m => m.Amount);

    // ---------- драбина ----------

    [Fact]
    public void Twelve_historical_rungs_follow_the_sich_each_ten_times_dearer()
    {
        var idle = Clicker.Shop.Where(u => u.Kind == ClickerKind.Idle).Select(u => u.Key).ToList();
        var sich = idle.IndexOf("sich");
        Assert.Equal(Clicker.WorldTiers, idle.Skip(sich + 1).Take(Clicker.WorldTiers.Length).ToArray());
        var prev = Clicker.Shop.Single(u => u.Key == "sich");
        foreach (var key in Clicker.WorldTiers)
        {
            var up = Clicker.Shop.Single(u => u.Key == key);
            Assert.Equal(prev.Base * 10, up.Base, prev.Base * 1e-9);
            Assert.InRange(up.Rate / prev.Rate, 5.3, 5.8);
            Assert.Equal(23, up.GrowNum);
            Assert.Equal(20, up.GrowDen);
            Assert.Equal([25, 50, 100, 150, 200], up.Steps.Select(s => s.Level).ToArray());
            Assert.Equal(3, up.Steps.Count(s => s.Effect == MarkEffect.Double));
            prev = up;
        }
        Assert.All(Clicker.GuestTiers, k => Assert.Contains(k, Clicker.WorldTiers));
    }

    [Fact]
    public void The_first_new_rung_opens_after_the_sich_and_the_next_after_it()
    {
        var h = Wheel();
        Levels(h, ("sich", 0), ("kontrakty", 1));
        Assert.False(Up(h, "baturyn").GetProperty("open").GetBoolean());
        Levels(h, ("sich", 1));
        Assert.True(Up(h, "baturyn").GetProperty("open").GetBoolean());
        Assert.False(Up(h, "korets").GetProperty("open").GetBoolean());
        Levels(h, ("baturyn", 1));
        Assert.True(Up(h, "korets").GetProperty("open").GetBoolean());
    }

    [Fact]
    public void A_new_rung_is_bought_for_hryvnias_and_earns_like_any_other()
    {
        var h = Wheel();
        Levels(h, ("sich", 1));
        Give(h, 7e20);
        var before = Num(h, "perSecond");
        var r = Act(h, "buy", new { key = "baturyn", n = 1 });
        Assert.True(r.Ok, r.Message);
        Assert.Equal(1, Up(h, "baturyn").GetProperty("level").GetInt32());
        Assert.True(Num(h, "perSecond") > before);
        Assert.Equal(7e20 - 6e20, Num(h, "pots"), 1e6);
    }

    // ---------- віхи: каталог і бюджет ----------

    [Fact]
    public void The_catalogue_holds_162_marks_and_every_one_past_a_hundred_is_a_modifier()
    {
        // v11 додав шість щаблів гончарів світу — лише з віхами ×2 на 25/50/100.
        Assert.Equal(180, Clicker.MarksAll);
        Assert.Equal(180, AllMarks.Count());
        foreach (var up in Clicker.Shop)
            Assert.All(up.Steps.Where(s => s.Level > 100 || (up.Key is "apprentice" or "kiln" && s.Level > 50)),
                s => Assert.NotEqual(MarkEffect.Double, s.Effect));
        // Ключ віхи — «верстат:рівень»: у верстата рівні віх не повторюються.
        foreach (var up in Clicker.Shop)
            Assert.Equal(up.Steps.Length, up.Steps.Select(s => s.Level).Distinct().Count());
        var names = AllMarks.Select(m => m.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_budget_of_every_lever_is_fixed_by_the_catalogue()
    {
        // Тест-бюджет (docs/games/specs/clicker-v10.md §4): ніхто не докине «ще одну +25 %» непомітно.
        Assert.Equal(15, CountOf(MarkEffect.Passive));
        Assert.Equal(10, CountOf(MarkEffect.Fall));
        Assert.Equal(6, CountOf(MarkEffect.Fair));
        Assert.Equal(7, CountOf(MarkEffect.Merchant));
        Assert.Equal(11, CountOf(MarkEffect.Night));
        Assert.Equal(3, CountOf(MarkEffect.GoldenShown));
        Assert.Equal(2, CountOf(MarkEffect.FallShown));
        Assert.Equal(3, CountOf(MarkEffect.Eye));
        Assert.Equal(1, CountOf(MarkEffect.ClickDouble));
        Assert.Equal(1, CountOf(MarkEffect.HandsDouble));
        Assert.Equal(3.75, SumOf(MarkEffect.Passive), 9);
        Assert.Equal(3, SumOf(MarkEffect.Momentum), 9);
        Assert.Equal(9, SumOf(MarkEffect.Temper), 9);
        Assert.Equal(0.05, SumOf(MarkEffect.Lucky), 9);
        Assert.Equal(0.12, SumOf(MarkEffect.Hand), 9);
        // 45 старих ×2 (разом із трьома «старими» віхами кола) + 57 плану віх + 60 нових щаблів.
        // v11: ще шість щаблів гончарів світу по три віхи ×2.
        Assert.Equal(12 * 3 + 2 * 3 + 12 * 3 + 6 * 3, CountOf(MarkEffect.Double));
    }

    [Fact]
    public void A_mark_price_is_eight_prices_of_its_level_for_new_rungs_too()
    {
        foreach (var key in Clicker.WorldTiers.Concat(["workshop", "sich"]))
        {
            var up = Clicker.Shop.Single(u => u.Key == key);
            for (var i = 0; i < up.Steps.Length; i++)
            {
                Assert.Equal(up.Price(up.Steps[i].Level) * ClickerUpgrade.MarkFactor, up.MarkPrice(i));
                Assert.False(Clicker.Short(up.MarkPrice(i)).Contains("NaN") || Clicker.Short(up.MarkPrice(i)) == "∞");
            }
        }
    }

    // ---------- віхи: кожен ефект через вид ----------

    static RoomHarness Rich(params (string Key, int Level)[] levels)
    {
        var h = Wheel();
        Levels(h, [("apprentice", 100), ("kiln", 100), ("workshop", 300), .. levels]);
        return h;
    }

    [Fact]
    public void A_passive_mark_adds_a_quarter_to_the_passive_and_does_not_double_its_workbench()
    {
        var h = Rich();
        var before = Num(h, "perSecond");
        var boost = Up(h, "workshop").GetProperty("boost").GetDouble();
        Marks(h, "workshop:150");
        Assert.Equal(before * 1.25, Num(h, "perSecond"), before * 1e-9);
        Assert.Equal(boost, Up(h, "workshop").GetProperty("boost").GetDouble());
    }

    [Fact]
    public void Buying_a_mark_past_a_hundred_says_what_it_gives()
    {
        var h = Rich();
        var workshop = Clicker.Shop.Single(u => u.Key == "workshop");
        Give(h, workshop.MarkPrice(3) * 2);
        var r = Act(h, "mark", new { key = "workshop:150" });
        Assert.True(r.Ok, r.Message);
        Assert.Equal("«Друга майстерня»: пасив +25 %", r.Message.Replace('\u00a0', ' '));
        Assert.False(Act(h, "mark", new { key = "workshop:150" }).Ok);
        Assert.Equal(1, View(h).GetProperty("marksOwned").GetInt32());
        Assert.Equal(Clicker.MarksAll, View(h).GetProperty("marksAll").GetInt32());
    }

    [Fact]
    public void The_view_tells_the_next_mark_of_a_workbench()
    {
        var h = Rich();
        Marks(h, "workshop:25", "workshop:50", "workshop:100");
        Assert.Equal(150, Up(h, "workshop").GetProperty("nextMark").GetInt32());
        // Назва й підпис — у каталозі магазину, що їде до першої дії й на look { catalog: true }.
        Assert.True(Act(h, "look", new { catalog = true }).Ok);
        var marks = View(h).GetProperty("shopCatalog").GetProperty("upgrades").GetProperty("workshop").GetProperty("marks");
        var m150 = Assert.Single(marks.EnumerateArray(), m => m.GetProperty("level").GetInt32() == 150);
        Assert.Equal("Друга майстерня", m150.GetProperty("name").GetString());
        Assert.Equal("пасив +25 %", m150.GetProperty("desc").GetString()!.Replace('\u00a0', ' '));
    }

    [Fact]
    public void Hand_marks_add_passive_to_the_click_and_the_old_wheel_marks_still_give_three_percent()
    {
        var h = Rich(("wheel", 200));
        Marks(h);
        var bare = Num(h, "clickBase");
        Marks(h, "wheel:25", "wheel:50");
        var old = Num(h, "clickBase");
        var passive = Num(h, "baseSecond");
        Assert.Equal(bare + passive * 0.03, old, Math.Max(2, old * 1e-9));
        Marks(h, "wheel:25", "wheel:50", "wheel:75");
        Assert.Equal(bare + passive * 0.06, Num(h, "clickBase"), Math.Max(2, old * 1e-9));
    }

    [Fact]
    public void Both_hands_double_the_whole_click_and_the_badge_shows_four()
    {
        var h = Rich(("wheel", 200));
        Marks(h, "wheel:10");
        var one = Num(h, "clickBase");
        Assert.Equal(2, Up(h, "wheel").GetProperty("boost").GetDouble());
        Marks(h, "wheel:10", "wheel:200");
        Assert.Equal(one * 2, Num(h, "clickBase"), 1);
        Assert.Equal(4, Up(h, "wheel").GetProperty("boost").GetDouble());
    }

    [Fact]
    public void Momentum_and_temper_marks_raise_the_ceiling_and_hold_the_spin_longer()
    {
        var h = Rich(("wheel", 200), ("flywheel", 8), ("school", 150));
        var max = Num(h, "momentumMax");
        var tau = Num(h, "heatTau");
        Marks(h, "wheel:100", "wheel:125", "school:150");
        Assert.Equal(max + 1.5, Num(h, "momentumMax"), 9);
        Assert.Equal(tau + 3, Num(h, "heatTau"), 9);
    }

    [Fact]
    public void A_lucky_mark_works_even_without_the_lucky_click_workbench()
    {
        var h = Rich(("wheel", 200));
        Marks(h, "wheel:150");
        var before = View(h).GetProperty("lucky").GetInt64();
        for (var i = 0; i < 40; i++)
        {
            h.Clock.Advance(1);
            Act(h, "spin", PotterHands.Human(10));
        }
        Assert.True(View(h).GetProperty("lucky").GetInt64() > before);
    }

    [Fact]
    public void A_fall_mark_adds_a_quarter_to_the_pot_from_the_shelf()
    {
        var h = Rich(("pit", 200));
        var gain = View(h).GetProperty("fall").GetProperty("gain").GetDouble();
        Marks(h, "pit:200");
        var after = View(h).GetProperty("fall").GetProperty("gain").GetDouble();
        // (1 + 0 кошика + 0,25) — на чверть більше без кошика; дно (20 глеків) лишається однаковим.
        Assert.Equal((gain - Clicker.FallFloor) * 1.25 + Clicker.FallFloor, after, Math.Max(2, gain * 1e-9));
    }

    [Fact]
    public void A_fair_mark_raises_the_fair_and_the_view_says_so()
    {
        var h = Rich(("fair", 150));
        Assert.Equal(Clicker.FairMult, View(h).GetProperty("fair").GetProperty("mult").GetDouble());
        Marks(h, "fair:150");
        Assert.Equal(Clicker.FairMult + 1, View(h).GetProperty("fair").GetProperty("mult").GetDouble());
    }

    [Fact]
    public void Night_marks_lengthen_the_night_but_never_past_a_day()
    {
        var h = Rich(("kiln", 100));
        var hours = Num(h, "offlineHours");
        Marks(h, "kiln:100");
        Assert.Equal(hours + 1, Num(h, "offlineHours"), 9);
        var all = Clicker.Shop.SelectMany(u => u.Steps.Select((m, i) => (u, m))).Where(x => x.m.Effect == MarkEffect.Night)
            .Select(x => $"{x.u.Key}:{x.m.Level}").ToArray();
        Levels(h, Clicker.Shop.Where(u => u.Kind == ClickerKind.Idle).Select(u => (u.Key, 300)).ToArray());
        Marks(h, all);
        Secrets(h, "night", "ennight");
        Assert.Equal(Clicker.OfflineMax.TotalHours, Num(h, "offlineHours"), 9);
    }

    [Fact]
    public void Shown_marks_keep_the_painted_pot_and_the_falling_pot_longer()
    {
        var h = Rich(("fair", 250), ("museum", 150));
        Marks(h, "fair:250", "museum:150");
        // Нові вікна рахуються з наступного розкладу: після спійманого/утеклого — тут беремо їх з виду наступних.
        Patch(h, s => { s.Remove("golden"); s.Remove("fall"); });
        var g = View(h).GetProperty("golden");
        var shown = (g.GetProperty("until").GetDateTimeOffset() - g.GetProperty("at").GetDateTimeOffset()).TotalSeconds;
        Assert.Equal(Clicker.GoldenShown.TotalSeconds + 3, shown, 3);
        var f = View(h).GetProperty("fall");
        var fall = (f.GetProperty("until").GetDateTimeOffset() - f.GetProperty("at").GetDateTimeOffset()).TotalSeconds;
        Assert.Equal(Clicker.FallShown.TotalSeconds + 0.5, fall, 3);
    }

    [Fact]
    public void Firing_burns_the_new_marks_unless_the_memory_of_hands_is_known()
    {
        var h = Rich();
        Marks(h, "workshop:150");
        Give(h, 1e12, Clicker.TotalFor(1000));
        Assert.True(Act(h, "fire").Ok);
        Assert.Equal(0, View(h).GetProperty("marksOwned").GetInt32());

        var k = Rich();
        Secrets(k, "memory");
        Marks(k, "workshop:150");
        Give(k, 1e12, Clicker.TotalFor(1000));
        Assert.True(Act(k, "fire").Ok);
        Assert.Equal(1, View(k).GetProperty("marksOwned").GetInt32());
    }

    [Fact]
    public void A_made_up_mark_from_the_base_is_dropped()
    {
        var h = Rich();
        Marks(h, "workshop:175", "workshop:150");
        Assert.Equal(1, View(h).GetProperty("marksOwned").GetInt32());
    }

    [Fact]
    public void Forty_marks_and_all_wheel_marks_are_achievements()
    {
        var h = Rich(("wheel", 200));
        var wheel = Clicker.Shop[0];
        Marks(h, wheel.Steps.Take(7).Select(m => $"wheel:{m.Level}").ToArray());
        Give(h, wheel.MarkPrice(7) * 2);
        Assert.True(Act(h, "mark", new { key = "wheel:200" }).Ok);
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-wheel-all");

        var k = Rich();
        var owned = Clicker.Shop.Where(u => u.Kind == ClickerKind.Idle)
            .SelectMany(u => u.Steps.Where(m => m.Effect == MarkEffect.Double).Select(m => $"{u.Key}:{m.Level}")).Take(39).ToArray();
        Levels(k, Clicker.Shop.Where(u => u.Kind == ClickerKind.Idle).Select(u => (u.Key, 300)).ToArray());
        Marks(k, owned);
        var workshop = Clicker.Shop.Single(u => u.Key == "workshop");
        Give(k, workshop.MarkPrice(3) * 2);
        Assert.True(Act(k, "mark", new { key = "workshop:150" }).Ok);
        Assert.Contains(k.Awards, a => a.Reason == "ach:potter-marks-40");
    }

    // ---------- клейма ----------

    [Fact]
    public void Below_the_knee_the_curve_is_the_one_of_september_23()
    {
        foreach (var n in new long[] { 0, 1000, 4000, 205_160, 2_436_264, Clicker.StampKnee })
        {
            var old = n <= 1000 ? n : 1000 * (2 * Math.Sqrt(n / 1000.0) - 1);
            Assert.Equal(old, Clicker.StampWeight(n), 6);
        }
    }

    [Fact]
    public void Past_the_knee_ten_times_more_stamps_add_the_same_weight_without_a_step()
    {
        var k = Clicker.StampKnee;
        Assert.InRange(Clicker.StampWeight(k + 1) - Clicker.StampWeight(k), Math.Sqrt(1000.0 / k) * 0.999, Math.Sqrt(1000.0 / k) * 1.001);
        var ten = Clicker.StampWeight(k * 10) - Clicker.StampWeight(k);
        var hundred = Clicker.StampWeight(k * 100) - Clicker.StampWeight(k * 10);
        Assert.Equal(ten, hundred, 3);
        Assert.InRange(ten, 145_600 - 100, 145_600 + 100);             // √(1000·4 млн)·ln 10 ≈ 145 629
        Assert.Equal(125_491.1, Clicker.StampWeight(k), 1);
    }

    [Fact]
    public void Stamps_no_longer_fit_in_an_int_and_do_not_have_to()
    {
        Assert.True(Clicker.StampsFor(1e40) > int.MaxValue);
        Assert.Equal(3_162_277_660_168L, Clicker.StampsFor(1e34));
        var h = Wheel();
        Patch(h, s => { s["stamps"] = 5_000_000_000L; s["total"] = Clicker.TotalFor(5_000_000_000L); });
        Assert.Equal(5_000_000_000L, View(h).GetProperty("stamps").GetInt64());
        Assert.Equal(Clicker.StampKnee, View(h).GetProperty("stampKnee").GetInt64());
    }

    [Fact]
    public void Nothing_changes_for_the_stamps_anyone_had_on_release_day()
    {
        // Найбільше клейм «з обпалом» у день релізу — 2,44 млн (Владік): це нижче коліна, тож множник той самий.
        var h = Wheel();
        Patch(h, s => { s["stamps"] = 2_436_264L; s["secrets"] = new JsonArray("seal"); });
        var mult = View(h).GetProperty("stampMult").GetDouble();
        Assert.Equal(1 + 0.03 * 1000 * (2 * Math.Sqrt(2436.264) - 1), mult, 6);
    }

    // ---------- прадідівські секрети ----------

    [Fact]
    public void The_third_ring_costs_millions_and_billions_of_stamps()
    {
        var third = Clicker.Secrets.Where(s => s.Ring == 3).ToList();
        Assert.Equal(["ennight", "seamap", "kin2", "basket2", "chest2", "wheel2"], third.Select(s => s.Key).ToArray());
        Assert.Equal(2_000_000_000L, third.Max(s => s.Price));
        Assert.True(Clicker.Secrets.Sum(s => s.Price) > int.MaxValue);
    }

    [Fact]
    public void Buying_a_billion_stamp_secret_works_and_the_bonus_stays()
    {
        var h = Wheel();
        Patch(h, s => s["stamps"] = 3_000_000_000L);
        var mult = View(h).GetProperty("stampMult").GetDouble();
        Assert.True(Act(h, "secret", new { key = "wheel2" }).Ok);
        Assert.Equal(1_000_000_000L, View(h).GetProperty("stampsFree").GetInt64());
        Assert.Equal(mult, View(h).GetProperty("stampMult").GetDouble());
    }

    [Fact]
    public void The_great_grandfather_night_adds_four_hours()
    {
        var h = Wheel();
        var hours = Num(h, "offlineHours");
        Secrets(h, "ennight");
        Assert.Equal(hours + 4, Num(h, "offlineHours"), 9);
    }

    [Fact]
    public void The_great_grandfather_circle_puts_the_old_ladder_on_ten_after_firing()
    {
        var h = Wheel();
        Secrets(h, "kin", "kin2");
        Levels(h, ("sich", 40), ("baturyn", 5));
        Give(h, 1e12, Clicker.TotalFor(100));
        Assert.True(Act(h, "fire").Ok);
        foreach (var key in Clicker.OldTiers) Assert.Equal(Clicker.KinLevels, Up(h, key).GetProperty("level").GetInt32());
        Assert.Equal(0, Up(h, "baturyn").GetProperty("level").GetInt32());
        Assert.Equal(Clicker.KinLevels, Up(h, "wheel").GetProperty("level").GetInt32());
    }

    [Fact]
    public void The_great_grandfather_basket_grows_to_fifteen_and_the_save_keeps_it()
    {
        var h = Wheel();
        Levels(h, ("basket", 10));
        Give(h, 1e30);
        Assert.False(Act(h, "buy", new { key = "basket", n = 1 }).Ok);
        Secrets(h, "basket2");
        Assert.Equal(15, Up(h, "basket").GetProperty("max").GetInt32());
        Assert.True(Act(h, "buy", new { key = "basket", n = 10 }).Ok);
        Assert.Equal(15, Up(h, "basket").GetProperty("level").GetInt32());
        Patch(h, _ => { });
        Assert.Equal(15, Up(h, "basket").GetProperty("level").GetInt32());
    }

    [Fact]
    public void The_great_grandfather_chest_keeps_another_twentieth_after_firing()
    {
        var h = Wheel();
        Secrets(h, "ashes", "chest2");
        Give(h, 1_000_000, Clicker.TotalFor(10));
        Assert.True(Act(h, "fire").Ok);
        Assert.InRange(Num(h, "pots"), 100_000, 110_000);
    }

    [Fact]
    public void The_great_grandfather_wheel_adds_five_percent_of_passive_to_a_click()
    {
        var h = Rich(("wheel", 50));
        var click = Num(h, "clickBase");
        var passive = Num(h, "baseSecond");
        Secrets(h, "wheel2");
        Assert.Equal(click + passive * 0.05, Num(h, "clickBase"), Math.Max(2, click * 1e-9));
    }

    // ---------- гривня й червоні золоті ----------

    [Fact]
    public void A_newcomer_has_no_coin_and_grows_into_hryvnias_then_gold()
    {
        var h = Wheel();
        Assert.Equal(0, View(h).GetProperty("coin").GetInt32());
        Give(h, 10, 2e15);
        Assert.Equal(1, View(h).GetProperty("coin").GetInt32());
        Assert.Equal(0, View(h).GetProperty("coinSeen").GetInt32());
        Assert.True(Act(h, "coin", new { v = 1 }).Ok);
        Assert.Equal(1, View(h).GetProperty("coinSeen").GetInt32());
        Assert.False(Act(h, "coin", new { v = 2 }).Ok);
        Give(h, 10, 3e27);
        Assert.Equal(2, View(h).GetProperty("coin").GetInt32());
        Assert.True(Act(h, "coin", new { v = 2 }).Ok);
        Assert.Equal(2, View(h).GetProperty("coinSeen").GetInt32());
        Patch(h, _ => { });
        Assert.Equal(2, View(h).GetProperty("coinSeen").GetInt32());
    }

    [Fact]
    public void The_view_speaks_hryvnias_in_the_workbench_descriptions()
    {
        var ocean = Clicker.Shop.Single(u => u.Key == "ocean");
        Assert.Equal("+2 ₴ за секунду", ocean.Desc);
        var workshop = Clicker.Shop.Single(u => u.Key == "workshop");
        Assert.Equal("+25 глеків за секунду", workshop.Desc);
    }

    // ---------- худий вид ----------

    [Fact]
    public void The_shop_texts_travel_once_and_the_view_carries_only_state()
    {
        var h = Rich(("sich", 50), ("baturyn", 5));
        Assert.True(Act(h, "look", new { pv = Clicker.ProtocolVersion }).Ok);   // після дії каталогів у виді нема
        var v = View(h);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("shopCatalog").ValueKind);
        var u = Up(h, "baturyn");
        Assert.False(u.TryGetProperty("name", out _));
        Assert.False(u.TryGetProperty("desc", out _));
        var shop = v.GetProperty("upgrades").GetRawText().Length + v.GetProperty("secrets").GetRawText().Length
            + v.GetProperty("styles").GetRawText().Length + v.GetProperty("marks").GetRawText().Length;
        Assert.True(shop < 6_000, $"магазин у виді важить {shop} Б");
        Assert.True(Act(h, "look", new { catalog = true }).Ok);
        Assert.Equal(JsonValueKind.Object, View(h).GetProperty("shopCatalog").ValueKind);
    }

    [Fact]
    public void A_tab_opened_before_the_update_gets_the_full_view_until_it_says_it_is_new()
    {
        // Вкладка з clicker.js до v10 (радіо відкрите добами, сайт сам не перезавантажується) доповнювати худий вид не
        // вміє: доки клієнт не сказав pv ≥ 10, вид повний, як до v10.
        var h = Rich(("sich", 50));
        var fat = Up(h, "sich");
        Assert.Equal("Гончарня на Січі", fat.GetProperty("name").GetString());
        Assert.Equal("idle", fat.GetProperty("kind").GetString());
        Assert.True(View(h).GetProperty("secrets")[0].TryGetProperty("desc", out _));
        Assert.True(View(h).GetProperty("marks")[0].TryGetProperty("price", out _));

        Assert.True(Act(h, "spin", PotterHands.Human(1)).Ok);                // старий клік без pv — лишається повний
        Assert.Equal("Гончарня на Січі", Up(h, "sich").GetProperty("name").GetString());

        Assert.True(Act(h, "look", new { pv = Clicker.ProtocolVersion }).Ok); // новий клієнт — худий
        Assert.False(Up(h, "sich").TryGetProperty("name", out _));
        Assert.False(View(h).GetProperty("secrets")[0].TryGetProperty("desc", out _));

        h.Clock.Advance(1);
        Assert.True(Act(h, "spin", PotterHands.Human(1)).Ok);                // той самий гончар відкрив стару вкладку
        Assert.Equal("Гончарня на Січі", Up(h, "sich").GetProperty("name").GetString());
    }

    // ---------- «Що нового» v10 і подарунок ----------

    [Fact]
    public void A_newcomer_sees_no_news_and_gets_no_gift()
    {
        var h = Wheel();
        Assert.Equal(JsonValueKind.Null, View(h).GetProperty("news").ValueKind);
    }

    [Fact]
    public void An_old_potter_sees_v10_once_and_gets_four_hours_once_and_the_coin_is_explained()
    {
        var h = Wheel();
        Levels(h, ("sich", 20));
        Patch(h, s => { s["news"] = "v9.2"; s["total"] = 5e21; s["titles"]!["gifts"] = new JsonArray("v9.2"); });
        Assert.Equal("v10", View(h).GetProperty("news").GetString());
        Assert.Equal("v9.2", View(h).GetProperty("newsSeen").GetString());
        var pots = Num(h, "pots");
        var passive = Num(h, "baseSecond");
        var r = Act(h, "news", new { v = "v10" });
        Assert.True(r.Ok, r.Message);
        Assert.Contains("чотири години", r.Message);
        Assert.Equal(pots + passive * 4 * 3600, Num(h, "pots"), Math.Max(10, passive * 60));
        Assert.Equal(JsonValueKind.Null, View(h).GetProperty("news").ValueKind);
        Assert.Equal(1, View(h).GetProperty("coinSeen").GetInt32());
        // Удруге — ні новин, ні подарунка.
        var again = Num(h, "pots");
        Assert.False(Act(h, "news", new { v = "v9.2" }).Ok);
        Assert.Equal(again, Num(h, "pots"));
    }

    [Fact]
    public void One_who_missed_the_titles_gets_both_gifts()
    {
        var h = Wheel();
        Levels(h, ("sich", 20));
        Patch(h, s => { s["news"] = "v9.1"; s["titles"]!["gifts"] = new JsonArray(); });
        var r = Act(h, "news", new { v = "v10" });
        Assert.True(r.Ok, r.Message);
        Assert.Contains("Подарунок округи", r.Message);
        Assert.Contains("Глек на весь світ", r.Message);
    }

    // ---------- ачівки щаблів ----------

    [Fact]
    public void The_first_voyage_the_paris_expo_and_opishnia_are_achievements()
    {
        var h = Wheel();
        Levels(h, ("mezhyhirya", 1), ("mirgorod", 1), ("exchange", 1), ("expo", 1));
        Give(h, 1e33);
        Assert.True(Act(h, "buy", new { key = "voyage", n = 1 }).Ok);
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-world");
        Assert.True(Act(h, "buy", new { key = "opishnia", n = 1 }).Ok);
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-opishnia");
        Assert.True(Act(h, "buy", new { key = "expo", n = 1 }).Ok);
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:potter-paris");     // уже був рівень — не перший
    }
}
