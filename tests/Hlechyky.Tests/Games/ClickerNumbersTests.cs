using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Великі числа Гончарного кола (docs/games/specs/clicker-v9.md §Z): глеки рахуються в <c>double</c>, а не
/// в <c>long</c>, ціни верстатів не впираються в стелю, а «1,09 млн» дочитується до децильйона й далі —
/// степенем. Правило просте: глек — штука (цілі числа), зіпсоване число (NaN, нескінченність) не рахується,
/// а старе збереження з проду читається так само, як читалось.
/// </summary>
public class ClickerNumbersTests
{
    static RoomHarness Wheel(string nick = "Оля")
    {
        var h = new RoomHarness("clicker");
        h.Solo(nick);
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static double Pots(RoomHarness h) => View(h).GetProperty("pots").GetDouble();
    static double Total(RoomHarness h) => View(h).GetProperty("total").GetDouble();
    static JsonElement Up(RoomHarness h, string key) => View(h).GetProperty("upgrades").GetProperty(key);

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

    static void Give(RoomHarness h, double pots, double? total = null) => Patch(h, s =>
    {
        s["pots"] = pots;
        s["total"] = Math.Max(pots, total ?? pots);
    });

    static ClickerUpgrade Tsar => Clicker.Shop.Single(u => u.Key == "tsar");

    // ---------- слова для великих чисел ----------

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1_000_000, "1 млн")]
    [InlineData(1_500_000, "1,5 млн")]
    [InlineData(999_000_000, "999 млн")]
    [InlineData(1e9, "1 млрд")]
    [InlineData(1e12, "1 трлн")]
    [InlineData(1e15, "1 квдрлн")]
    [InlineData(1e18, "1 квнтлн")]
    [InlineData(1e21, "1 скстлн")]
    [InlineData(1e24, "1 сптлн")]
    [InlineData(1e27, "1 октлн")]
    [InlineData(1e30, "1 нонлн")]
    [InlineData(1e33, "1 дцлн")]
    [InlineData(1.2e33, "1,2 дцлн")]
    public void Short_names_every_big_number_up_to_the_decillion(double n, string text) =>
        Assert.Equal(text, Clicker.Short(n));

    [Fact]
    public void Past_the_decillion_short_switches_to_a_power_of_ten()
    {
        // Слів далі нема — і вигадувати їх не варто: «1,2e36» читається однаково всюди.
        Assert.Equal("1e36", Clicker.Short(1e36));
        Assert.Equal("1,2e36", Clicker.Short(1.2e36));
        Assert.Equal("1e37", Clicker.Short(9.99e36));      // мантиса, що доросла до десятки, — це наступний степінь
        Assert.Equal("1e100", Clicker.Short(1e100));
        Assert.Equal("∞", Clicker.Short(double.PositiveInfinity));
        Assert.Equal("∞", Clicker.Short(double.NaN));
    }

    // ---------- ціни верстатів ----------

    [Fact]
    public void A_price_under_the_long_ceiling_is_the_very_same_whole_number_as_before()
    {
        // Дев'яте оновлення не має міняти цінника нікому: доки ціна ціла, вона рахується цілими, як і рахувалась.
        Assert.Equal(15d, Clicker.Shop[0].Price(0));
        Assert.Equal(23d, Clicker.Shop[0].Price(1));
        Assert.Equal(132_250d, Clicker.Shop.Single(u => u.Key == "workshop").Price(2));
        Assert.Equal(5_000_000_000_000_000d, Tsar.Price(0));
        Assert.Equal(Tsar.Price(25) * ClickerUpgrade.MarkFactor, Tsar.MarkPrice(0));
    }

    [Fact]
    public void A_price_over_the_long_ceiling_is_a_number_not_a_wall()
    {
        var wall = Tsar.Price(54);
        Assert.True(wall > long.MaxValue, $"{wall}");
        Assert.True(Tsar.Price(55) > wall);
        Assert.True(Tsar.Price(500) > Tsar.Price(499));
        // Навіть за межею глузду ціна лишається числом, а не NaN: магазин мусить щось намалювати.
        Assert.True(double.IsFinite(Tsar.Price(20_000)));
        Assert.True(double.IsFinite(Tsar.MarkPrice(2)));
    }

    [Fact]
    public void A_workbench_that_costs_more_than_long_can_hold_is_still_bought()
    {
        var h = Wheel();
        var price = Tsar.Price(54);
        Patch(h, s =>
        {
            s["upgrades"]!["tsar"] = 54;
            s["pots"] = 1e19;
            s["total"] = 1e19;
        });

        var r = Act(h, "buy", new { key = "tsar" });

        Assert.True(r.Ok, r.Message);
        Assert.Equal("Цар-глек — рівень 55", r.Message);
        Assert.Equal(55, Up(h, "tsar").GetProperty("level").GetInt32());
        Assert.Equal(1e19 - price, Pots(h));
    }

    [Fact]
    public void Max_at_a_mountain_of_pots_takes_the_levels_that_fit_and_says_so()
    {
        var h = Wheel();
        Give(h, 1e21);

        var r = Act(h, "buy", new { key = "tsar", n = Clicker.MaxBuy });

        Assert.True(r.Ok, r.Message);
        var level = Up(h, "tsar").GetProperty("level").GetInt32();
        Assert.InRange(level, 1, Clicker.MaxBuy);
        // Рівно стільки, скільки влізло: наступний уже не по кишені.
        Assert.True(Pots(h) >= 0);
        Assert.True(Pots(h) < Tsar.Price(level));
        Assert.DoesNotContain("NaN", r.Message);
        Assert.DoesNotContain("∞", r.Message);
    }

    [Fact]
    public void Not_enough_pots_for_an_astronomic_price_is_a_plain_refusal()
    {
        var h = Wheel();
        Patch(h, s =>
        {
            s["upgrades"]!["tsar"] = 54;
            s["pots"] = 1_000;
            s["total"] = 1e19;
        });

        var r = Act(h, "buy", new { key = "tsar" });

        Assert.False(r.Ok);
        Assert.StartsWith("Бракує глеків: треба ще ", r.Message);
        Assert.DoesNotContain("NaN", r.Message);
    }

    // ---------- глеки — цілі ----------

    [Fact]
    public void Pots_stay_whole_even_when_the_formula_is_not()
    {
        var h = Wheel();
        // Глина ×1,25 робить дробовим усе, що тільки можна, — а в лічильнику мусить лишитись ціле.
        Patch(h, s =>
        {
            s["upgrades"]!["clay"] = 3;
            s["upgrades"]!["apprentice"] = 7;
            s["pots"] = 1e15;
            s["total"] = 1e15;
        });
        h.Clock.Advance(TimeSpan.FromSeconds(97.5));
        Act(h, "look");

        var pots = Pots(h);
        Assert.Equal(Math.Floor(pots), pots);
        Assert.True(pots > 1e15);
    }

    [Fact]
    public void A_catch_from_the_shelf_is_a_whole_number_of_pots_at_any_scale()
    {
        var h = Wheel();
        Patch(h, s =>
        {
            s["upgrades"]!["tsar"] = 40;
            s["pots"] = 1e20;
            s["total"] = 1e20;
        });

        var gain = View(h).GetProperty("fall").GetProperty("gain").GetDouble();

        Assert.True(double.IsFinite(gain));
        Assert.Equal(Math.Floor(gain), gain);
        Assert.True(gain > 0);
    }

    // ---------- таблиця «Гончарі» ----------

    [Fact]
    public void The_table_gets_the_real_number_above_the_long_ceiling()
    {
        // Зі стелею 9,2·10¹⁸ Микола (3,6·10²¹) і Владік (4,2·10¹⁹) у таблиці зрівнялись, і обоє бачили «ти перший».
        var h = Wheel();
        Give(h, 1e21);
        Act(h, "spin", PotterHands.Human(3));

        Assert.NotEmpty(h.Scores);
        Assert.True(h.Scores.Last().Score >= 1e21);
    }

    // ---------- прилавок ----------

    [Fact]
    public void The_counter_still_trades_hundreds_when_the_pile_is_astronomic()
    {
        var h = Wheel();
        Give(h, 1e21);
        var cap = View(h).GetProperty("canSellToday").GetInt32();

        var r = Act(h, "sell", new { pots = 100 });

        Assert.True(r.Ok, r.Message);
        Assert.Equal("Обміняв 100 глеків на 1 черепок", r.Message);
        Assert.Equal(1, View(h).GetProperty("soldToday").GetInt32());
        Assert.Equal(cap - 1, View(h).GetProperty("canSellToday").GetInt32());
        // Сто глеків із секстильйона — це навіть не одиниця молодшого розряду double: купа стоїть, як стояла.
        Assert.Equal(1e21, Pots(h));
    }

    // ---------- клейма ----------

    [Fact]
    public void Stamps_are_counted_from_a_total_that_no_longer_fits_in_long()
    {
        var h = Wheel();
        Give(h, 0, 1e22);

        var v = View(h);

        Assert.Equal(Clicker.StampsFor(1e22), v.GetProperty("stampsReady").GetInt32());
        Assert.Equal(100_000_000, Clicker.StampsFor(1e25));
        Assert.Equal(1e22, v.GetProperty("total").GetDouble());
        Assert.True(double.IsFinite(v.GetProperty("nextStampAt").GetDouble()));
    }

    // ---------- збереження ----------

    [Fact]
    public void A_save_with_more_pots_than_long_can_hold_comes_back_as_it_was()
    {
        var h = Wheel();
        Give(h, 1.25e21, 3.5e21);

        var saved = Saved(h);

        Assert.Equal(1.25e21, saved["pots"]!.GetValue<double>());
        Assert.Equal(3.5e21, saved["total"]!.GetValue<double>());

        // І назад: перезавантажена кімната бачить ту саму купу.
        lock (h.Room.Sync) h.Room.Game.Load(saved.ToJsonString());
        Assert.Equal(1.25e21, Pots(h));
        Assert.Equal(3.5e21, Total(h));
    }

    [Fact]
    public void An_old_save_with_whole_long_pots_reads_exactly_as_before()
    {
        var h = Wheel();
        // Саме так виглядає збереження з проду до дев'ятого оновлення: цілі числа без крапки.
        Patch(h, s =>
        {
            s["pots"] = 1_600_000_000_000_000L;
            s["total"] = 4_000_000_000_000_000L;
        });

        Assert.Equal(1_600_000_000_000_000d, Pots(h));
        Assert.Equal(4_000_000_000_000_000d, Total(h));
        Assert.Equal(2_000, View(h).GetProperty("stampsReady").GetInt32());
        // І записується так само цілим — жодного «1.6E+15» у старому діапазоні.
        Assert.Equal("1600000000000000", Saved(h)["pots"]!.ToJsonString());
    }

    [Fact]
    public void A_save_with_a_nonsense_number_of_pots_starts_from_nothing_instead_of_breaking()
    {
        var h = Wheel();
        Patch(h, s =>
        {
            s["pots"] = -5;
            s["total"] = -5;
        });

        Assert.Equal(0d, Pots(h));
        Assert.Equal(0d, Total(h));
        Assert.True(Act(h, "spin", PotterHands.Human(2)).Ok);
    }

    // ---------- вид ----------

    [Fact]
    public void A_view_at_a_sextillion_has_no_broken_numbers_in_it()
    {
        var h = Wheel();
        Patch(h, s =>
        {
            s["upgrades"]!["tsar"] = 60;
            s["upgrades"]!["chumaks"] = 120;
            s["stamps"] = 1_500;
            s["pots"] = 1e21;
            s["total"] = 4e21;
        });

        var text = View(h).GetRawText();

        Assert.DoesNotContain("NaN", text);
        Assert.DoesNotContain("Infinity", text);
        foreach (var name in new[] { "pots", "total", "perClick", "clickBase", "perSecond", "baseSecond", "nextStampAt" })
            Assert.True(double.IsFinite(View(h).GetProperty(name).GetDouble()), name);
        Assert.True(double.IsFinite(Up(h, "tsar").GetProperty("price").GetDouble()));
    }

    // ---------- округлення купцям ----------

    [Fact]
    public void A_merchant_asks_for_a_round_number_even_at_a_sextillion()
    {
        Assert.Equal(12_000d, Clicker.Nice(11_873));
        Assert.Equal(1.2e21, Clicker.Nice(1.234e21));
        Assert.Equal(0d, Clicker.Nice(double.NaN));
    }
}
