using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Хата гончаря: глина як стратегія, знаряддя на стіні, прикраси й дошка купців (ClickerHouse.cs). Основа — у
/// <see cref="ClickerTests"/>, драбина й обпал — у <see cref="ClickerProgressTests"/>, розгін і полиця — у <see cref="ClickerLiveTests"/>.
/// </summary>
public class ClickerHouseTests
{
    static RoomHarness Wheel(string nick = "Оля")
    {
        var h = new RoomHarness("clicker");
        h.Solo(nick);
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static JsonElement House(RoomHarness h) => View(h).GetProperty("house");
    static JsonElement Orders(RoomHarness h) => House(h).GetProperty("orders");
    static long Pots(RoomHarness h) => View(h).GetProperty("pots").GetInt64();
    static long ClickBase(RoomHarness h) => View(h).GetProperty("clickBase").GetInt64();
    static double PerSecond(RoomHarness h) => View(h).GetProperty("perSecond").GetDouble();
    static JsonElement Fall(RoomHarness h) => View(h).GetProperty("fall");
    static int Streak(RoomHarness h) => Fall(h).GetProperty("streak").GetInt32();
    static int Stamps(RoomHarness h) => View(h).GetProperty("stamps").GetInt32();

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

    static void Give(RoomHarness h, double pots, double? total = null) => Patch(h, s =>
    {
        s["pots"] = pots;
        s["total"] = Math.Max(pots, total ?? pots);
    });

    static void Levels(RoomHarness h, params (string Key, int Level)[] levels) => Patch(h, s =>
    {
        foreach (var (key, level) in levels) s["upgrades"]![key] = level;
    });

    static void FallNow(RoomHarness h) => Patch(h, s =>
    {
        var now = h.Clock.UtcNow;
        s["fall"] = new JsonObject
        {
            ["at"] = now.ToString("O"),
            ["until"] = (now + Clicker.FallShown).ToString("O"),
            ["x"] = 40,
        };
    });

    static void GoldenNow(RoomHarness h) => Patch(h, s =>
    {
        var now = h.Clock.UtcNow;
        s["golden"] = new JsonObject
        {
            ["at"] = now.ToString("O"),
            ["until"] = (now + Clicker.GoldenShown).ToString("O"),
            ["kind"] = (int)Clicker.GoldenKind.Merchant,
            ["x"] = 10,
            ["y"] = 10,
        };
    });

    static JsonElement Owned(JsonElement list, string key) => list.EnumerateArray().Single(x => x.GetProperty("key").GetString() == key);

    // ---------- глина ----------

    [Fact]
    public void Buying_a_clay_puts_it_on_the_wheel_and_it_has_to_rest_before_the_next()
    {
        var h = Wheel();
        Give(h, 10_000);
        var r = Act(h, "knead", new { kind = "red" });
        Assert.True(r.Ok);
        Assert.Equal(7_000, Pots(h));
        Assert.Equal("red", House(h).GetProperty("clay").GetString());
        Assert.Equal(2, ClickBase(h));                          // клік ×2

        // Замісити назад звичайну одразу не можна — глина відлежується.
        var back = Act(h, "knead", new { kind = "" });
        Assert.False(back.Ok);
        Assert.Contains("відлежується", back.Message);

        h.Clock.AdvanceMs((int)Clicker.ClayRest.TotalMilliseconds + 1000);
        Assert.True(Act(h, "knead", new { kind = "" }).Ok);
        Assert.Equal("", House(h).GetProperty("clay").GetString());
        Assert.Equal(1, ClickBase(h));
        // Куплена лишилась: вдруге платити не треба.
        h.Clock.AdvanceMs((int)Clicker.ClayRest.TotalMilliseconds + 1000);
        Assert.True(Act(h, "knead", new { kind = "red" }).Ok);
        Assert.Equal(7_000, Pots(h));
    }

    [Fact]
    public void White_clay_trades_the_click_for_passive()
    {
        var h = Wheel();
        Levels(h, ("apprentice", 10));                          // 5 за секунду
        Give(h, 100_000);
        Assert.True(Act(h, "knead", new { kind = "white" }).Ok);
        Assert.Equal(7.5, PerSecond(h), 6);
        Assert.Equal(1, ClickBase(h));                          // 0,5 округлюється до мінімуму — одного глека
        Assert.True(Owned(House(h).GetProperty("clays"), "white").GetProperty("on").GetBoolean());
    }

    [Fact]
    public void Black_clay_makes_jugs_fall_sooner_and_pay_more()
    {
        var h = Wheel();
        Give(h, 1_000_000);
        Assert.True(Act(h, "knead", new { kind = "black" }).Ok);
        FallNow(h);
        Assert.Equal(170, Fall(h).GetProperty("gain").GetInt64());     // (100 кліків) × 1,5 + 20
        Assert.True(Act(h, "grab").Ok);
        var wait = (Fall(h).GetProperty("at").GetDateTimeOffset() - h.Clock.UtcNow).TotalSeconds;
        Assert.InRange(wait, Clicker.FallMinSeconds * 0.6 - 0.01, Clicker.FallMaxSeconds * 0.6 + 0.01);
    }

    [Fact]
    public void Unknown_or_unpaid_clay_is_refused()
    {
        var h = Wheel();
        Assert.False(Act(h, "knead", new { kind = "golden" }).Ok);
        var poor = Act(h, "knead", new { kind = "red" });
        Assert.False(poor.Ok);
        Assert.Contains("Бракує", poor.Message);
        Assert.False(Act(h, "knead", new { kind = "" }).Ok);            // звичайна вже на колі
    }

    [Fact]
    public void The_kiln_puts_plain_clay_back_but_keeps_the_bought_ones()
    {
        var h = Wheel();
        Give(h, 10_000, Clicker.TotalFor(1));
        Assert.True(Act(h, "knead", new { kind = "red" }).Ok);
        Assert.True(Act(h, "fire").Ok);
        Assert.Equal("", House(h).GetProperty("clay").GetString());
        Assert.True(Owned(House(h).GetProperty("clays"), "red").GetProperty("owned").GetBoolean());
    }

    // ---------- знаряддя ----------

    [Fact]
    public void A_tool_is_bought_once_and_the_paddle_slows_the_cooling()
    {
        var h = Wheel();
        Give(h, 10_000);
        Assert.True(Act(h, "tool", new { key = "paddle" }).Ok);
        Assert.Equal(5_000, Pots(h));
        Assert.False(Act(h, "tool", new { key = "paddle" }).Ok);
        Assert.True(Owned(House(h).GetProperty("tools"), "paddle").GetProperty("owned").GetBoolean());
        Assert.Equal(Clicker.HeatTau * Clicker.PaddleTau, View(h).GetProperty("heatTau").GetDouble());
        Assert.False(Act(h, "tool", new { key = "hammer" }).Ok);
    }

    [Fact]
    public void The_string_and_the_ribs_feed_the_click()
    {
        var h = Wheel();
        Levels(h, ("wheel", 9), ("apprentice", 200));           // рука 10, пасив 100
        Give(h, 200_000);
        Assert.Equal(10, ClickBase(h));
        Assert.True(Act(h, "tool", new { key = "string" }).Ok);
        Assert.Equal(11, ClickBase(h));                         // +1 % пасиву
        Assert.True(Act(h, "tool", new { key = "ribs" }).Ok);
        Assert.Equal(14, ClickBase(h));                         // 10 × 1,25 + 1 = 13,5 → 14
    }

    [Fact]
    public void The_bucket_and_the_lantern()
    {
        var h = Wheel();
        Levels(h, ("apprentice", 10));
        Give(h, 2_000_000);
        Assert.True(Act(h, "tool", new { key = "bucket" }).Ok);
        Assert.Equal(5.5, PerSecond(h), 6);
        Assert.Equal(8, View(h).GetProperty("offlineHours").GetDouble());
        Assert.True(Act(h, "tool", new { key = "lantern" }).Ok);
        Assert.Equal(10, View(h).GetProperty("offlineHours").GetDouble());
    }

    [Fact]
    public void The_sponge_gives_the_falling_jug_more_air()
    {
        var h = Wheel();
        Give(h, 100_000);
        Assert.True(Act(h, "tool", new { key = "sponge" }).Ok);
        FallNow(h);
        Assert.True(Act(h, "grab").Ok);
        var f = Fall(h);
        Assert.Equal(Clicker.FallShownLong, f.GetProperty("until").GetDateTimeOffset() - f.GetProperty("at").GetDateTimeOffset());
    }

    [Fact]
    public void The_apron_forgives_one_broken_jug_but_not_two_in_a_row()
    {
        var h = Wheel();
        Give(h, 1_000_000);
        Assert.True(Act(h, "tool", new { key = "apron" }).Ok);
        Patch(h, s => s["fallStreak"] = 5);
        var past = (int)(Clicker.FallShown + Clicker.CatchGrace).TotalMilliseconds + 500;

        FallNow(h);
        h.Clock.AdvanceMs(past);
        Assert.False(Act(h, "grab").Ok);
        Assert.Equal(5, Streak(h));                             // фартух вибачив

        FallNow(h);
        h.Clock.AdvanceMs(past);
        Assert.False(Act(h, "grab").Ok);
        Assert.Equal(0, Streak(h));                             // другий поспіль — ні

        // Спійманий глек повертає фартухові силу.
        FallNow(h);
        Assert.True(Act(h, "grab").Ok);
        Assert.Equal(1, Streak(h));
        FallNow(h);
        h.Clock.AdvanceMs(past);
        Assert.False(Act(h, "grab").Ok);
        Assert.Equal(1, Streak(h));
    }

    [Fact]
    public void The_whistle_hurries_the_golden_jug()
    {
        var h = Wheel();
        Give(h, 3_000_000);
        Assert.True(Act(h, "tool", new { key = "whistle" }).Ok);
        for (var i = 0; i < 3; i++)
        {
            GoldenNow(h);
            Assert.True(Act(h, "catch").Ok);
            var wait = (View(h).GetProperty("golden").GetProperty("at").GetDateTimeOffset() - h.Clock.UtcNow).TotalSeconds;
            Assert.InRange(wait, Clicker.GoldenMinSeconds * Clicker.WhistleEvents - 0.01, Clicker.GoldenMaxSeconds * Clicker.WhistleEvents + 0.01);
        }
    }

    [Fact]
    public void The_iron_adds_a_stamp_to_every_kiln_but_never_makes_one_out_of_nothing()
    {
        var h = Wheel();
        Give(h, 100_000_000, Clicker.TotalFor(2));
        Assert.True(Act(h, "tool", new { key = "iron" }).Ok);
        Assert.True(Act(h, "fire").Ok);
        Assert.Equal(3, Stamps(h));                             // 2 за глеки + 1 від тавра
        Assert.False(Act(h, "fire").Ok);                        // без нового клейма тавро не спрацює
        Assert.Equal(3, Stamps(h));
        Assert.True(Owned(House(h).GetProperty("tools"), "iron").GetProperty("owned").GetBoolean());
    }

    // ---------- прикраси ----------

    [Fact]
    public void Decor_adds_two_percent_each_and_stays_after_the_kiln()
    {
        var h = Wheel();
        Give(h, 200_000, Clicker.TotalFor(1));
        Assert.True(Act(h, "adorn", new { key = "towel" }).Ok);
        Assert.True(Act(h, "adorn", new { key = "icon" }).Ok);
        Assert.False(Act(h, "adorn", new { key = "icon" }).Ok);
        Assert.Equal(1.04, View(h).GetProperty("allMult").GetDouble(), 6);

        Assert.True(Act(h, "fire").Ok);
        Assert.Equal(1.04 * 1.02, View(h).GetProperty("allMult").GetDouble(), 6);
        Assert.True(Owned(House(h).GetProperty("decor"), "towel").GetProperty("owned").GetBoolean());
    }

    [Fact]
    public void The_dog_keeps_the_golden_jug_on_the_stage_longer()
    {
        var h = Wheel();
        Give(h, 200_000_000);
        Assert.True(Act(h, "adorn", new { key = "dog" }).Ok);
        GoldenNow(h);
        Assert.True(Act(h, "catch").Ok);
        var g = View(h).GetProperty("golden");
        Assert.Equal(Clicker.GoldenShown + Clicker.DogGuard, g.GetProperty("until").GetDateTimeOffset() - g.GetProperty("at").GetDateTimeOffset());
    }

    // ---------- купці ----------

    [Fact]
    public void The_board_has_three_merchants_and_refreshes_every_four_minutes()
    {
        var h = Wheel();
        var board = Orders(h).GetProperty("board").EnumerateArray().ToList();
        Assert.Equal(Clicker.BoardSize, board.Count);
        Assert.All(board, o => Assert.Equal("invest", o.GetProperty("kind").GetString()));   // розписів ще нема
        Assert.All(board, o => Assert.True(o.GetProperty("pay").GetInt64() > o.GetProperty("need").GetInt64()));
        Assert.Equal(3, board.Select(o => o.GetProperty("id").GetInt32()).Distinct().Count());
        Assert.Equal(h.Clock.UtcNow + Clicker.BoardEvery, Orders(h).GetProperty("refreshAt").GetDateTimeOffset());

        var ids = board.Select(o => o.GetProperty("id").GetInt32()).ToHashSet();
        h.Clock.AdvanceMs((int)Clicker.BoardEvery.TotalMilliseconds + 1000);
        var fresh = Orders(h).GetProperty("board").EnumerateArray().Select(o => o.GetProperty("id").GetInt32()).ToList();
        Assert.Equal(Clicker.BoardSize, fresh.Count);
        Assert.Empty(fresh.Intersect(ids));
    }

    [Fact]
    public void A_merchant_takes_the_pots_and_returns_with_more()
    {
        var h = Wheel();
        Give(h, 1_000_000);
        var order = Orders(h).GetProperty("board").EnumerateArray().First();
        var id = order.GetProperty("id").GetInt32();
        var need = order.GetProperty("need").GetInt64();
        var pay = order.GetProperty("pay").GetInt64();
        var minutes = order.GetProperty("minutes").GetInt32();
        Assert.Contains(minutes, Clicker.OrderMinutes);
        Assert.Equal((long)(need * (Clicker.InvestBase + minutes / 30.0 * Clicker.InvestPerHalfHour)), pay);

        var r = Act(h, "take", new { id });
        Assert.True(r.Ok);
        Assert.Contains("повернеться", r.Message);
        Assert.Equal(1_000_000 - need, Pots(h));
        Assert.Single(Orders(h).GetProperty("taken").EnumerateArray());
        Assert.Equal(Clicker.BoardSize - 1, Orders(h).GetProperty("board").GetArrayLength());

        // Ще не час — купець у дорозі.
        h.Clock.AdvanceMs(minutes * 60_000 - 5_000);
        Assert.Equal(1_000_000 - need, Pots(h));
        h.Clock.AdvanceMs(10_000);
        Assert.Equal(1_000_000 - need + pay, Pots(h));
        Assert.Empty(Orders(h).GetProperty("taken").EnumerateArray());
        var paid = Orders(h).GetProperty("paid").EnumerateArray().Single();
        Assert.Equal(pay, paid.GetProperty("pay").GetInt64());
        Assert.Equal(id, paid.GetProperty("id").GetInt32());
    }

    [Fact]
    public void A_style_order_pays_at_once_and_only_for_a_style_you_own()
    {
        var h = Wheel();
        Patch(h, s => s["styles"] = new JsonArray("kosiv"));
        h.Clock.AdvanceMs((int)Clicker.BoardEvery.TotalMilliseconds + 1000);   // нова дошка вже бачить розпис
        var order = Orders(h).GetProperty("board").EnumerateArray().Single(o => o.GetProperty("kind").GetString() == "style");
        Assert.Equal("kosiv", order.GetProperty("style").GetString());
        Assert.Equal("Косівська", order.GetProperty("styleName").GetString());
        Assert.True(order.GetProperty("can").GetBoolean());
        var need = order.GetProperty("need").GetInt64();
        var pay = order.GetProperty("pay").GetInt64();
        Assert.Equal((long)(need * Clicker.StyleOrderPay), pay);

        Give(h, need);
        Assert.True(Act(h, "take", new { id = order.GetProperty("id").GetInt32() }).Ok);
        Assert.Equal(pay, Pots(h));
        Assert.Empty(Orders(h).GetProperty("taken").EnumerateArray());

        // Той самий купець, але розпис продали з колекції (правка бази) — не візьме.
        h.Clock.AdvanceMs((int)Clicker.BoardEvery.TotalMilliseconds + 1000);
        var again = Orders(h).GetProperty("board").EnumerateArray().Single(o => o.GetProperty("kind").GetString() == "style");
        Patch(h, s => s["styles"] = new JsonArray());
        Give(h, 10_000_000);
        var r = Act(h, "take", new { id = again.GetProperty("id").GetInt32() });
        Assert.False(r.Ok);
        Assert.Contains("розпису", r.Message);
    }

    [Fact]
    public void No_more_than_three_merchants_on_the_road()
    {
        var h = Wheel();
        Give(h, 100_000_000);
        foreach (var o in Orders(h).GetProperty("board").EnumerateArray().ToList())
            Assert.True(Act(h, "take", new { id = o.GetProperty("id").GetInt32() }).Ok);
        Assert.Equal(Clicker.MaxTaken, Orders(h).GetProperty("taken").GetArrayLength());
        Assert.Equal(0, Orders(h).GetProperty("board").GetArrayLength());

        h.Clock.AdvanceMs((int)Clicker.BoardEvery.TotalMilliseconds + 1000);
        var next = Orders(h).GetProperty("board").EnumerateArray().First();
        var r = Act(h, "take", new { id = next.GetProperty("id").GetInt32() });
        Assert.False(r.Ok);
        Assert.Contains("Уже в дорозі 3 купці", r.Message);
    }

    [Fact]
    public void A_merchant_who_left_cannot_be_hired_and_you_need_the_pots()
    {
        var h = Wheel();
        var order = Orders(h).GetProperty("board").EnumerateArray().First();
        var id = order.GetProperty("id").GetInt32();
        var poor = Act(h, "take", new { id });
        Assert.False(poor.Ok);
        Assert.Contains("Бракує", poor.Message);

        h.Clock.AdvanceMs((int)Clicker.BoardEvery.TotalMilliseconds + 1000);
        Give(h, 100_000_000);
        var gone = Act(h, "take", new { id });
        Assert.False(gone.Ok);
        Assert.Contains("поїхав", gone.Message);
        Assert.False(Act(h, "take", new { id = 9999 }).Ok);
    }

    [Fact]
    public void The_scales_make_every_merchant_pay_a_fifth_more()
    {
        var h = Wheel();
        Give(h, 100_000_000);
        var before = Orders(h).GetProperty("board").EnumerateArray().First();
        var id = before.GetProperty("id").GetInt32();
        var pay = before.GetProperty("pay").GetInt64();
        Assert.True(Act(h, "tool", new { key = "scales" }).Ok);
        var after = Orders(h).GetProperty("board").EnumerateArray().Single(o => o.GetProperty("id").GetInt32() == id);
        Assert.Equal((long)(pay * (1 + Clicker.ScalesBonus)), after.GetProperty("pay").GetInt64());

        var potsBefore = Pots(h);
        Assert.True(Act(h, "take", new { id }).Ok);
        h.Clock.AdvanceMs(31 * 60_000);
        Assert.Equal(potsBefore - after.GetProperty("need").GetInt64() + after.GetProperty("pay").GetInt64(), Pots(h));
    }

    [Fact]
    public void The_kiln_burns_the_merchants_on_the_road_and_hangs_a_new_board()
    {
        var h = Wheel();
        Give(h, 100_000_000, Clicker.TotalFor(1));
        var ids = Orders(h).GetProperty("board").EnumerateArray().Select(o => o.GetProperty("id").GetInt32()).ToList();
        Assert.True(Act(h, "take", new { id = ids[0] }).Ok);
        Assert.True(Act(h, "fire").Ok);
        Assert.Empty(Orders(h).GetProperty("taken").EnumerateArray());
        var fresh = Orders(h).GetProperty("board").EnumerateArray().Select(o => o.GetProperty("id").GetInt32()).ToList();
        Assert.Equal(Clicker.BoardSize, fresh.Count);
        Assert.Empty(fresh.Intersect(ids));
        Assert.Equal(0, Pots(h));
    }

    [Fact]
    public void The_house_survives_save_and_load()
    {
        var h = Wheel();
        Give(h, 100_000_000);
        Assert.True(Act(h, "knead", new { kind = "red" }).Ok);
        Assert.True(Act(h, "tool", new { key = "paddle" }).Ok);
        Assert.True(Act(h, "adorn", new { key = "towel" }).Ok);
        var order = Orders(h).GetProperty("board").EnumerateArray().First();
        Assert.True(Act(h, "take", new { id = order.GetProperty("id").GetInt32() }).Ok);
        var boardIds = Orders(h).GetProperty("board").EnumerateArray().Select(o => o.GetProperty("id").GetInt32()).ToList();
        var payAt = Orders(h).GetProperty("taken").EnumerateArray().Single().GetProperty("payAt").GetDateTimeOffset();

        Patch(h, _ => { });

        Assert.Equal("red", House(h).GetProperty("clay").GetString());
        Assert.True(Owned(House(h).GetProperty("tools"), "paddle").GetProperty("owned").GetBoolean());
        Assert.True(Owned(House(h).GetProperty("decor"), "towel").GetProperty("owned").GetBoolean());
        Assert.Equal(boardIds, Orders(h).GetProperty("board").EnumerateArray().Select(o => o.GetProperty("id").GetInt32()).ToList());
        Assert.Equal(payAt, Orders(h).GetProperty("taken").EnumerateArray().Single().GetProperty("payAt").GetDateTimeOffset());
    }

    [Fact]
    public void Old_saves_without_a_house_get_plain_clay_and_a_fresh_board()
    {
        var h = Wheel();
        lock (h.Room.Sync)
            h.Room.Game.Load("""{"pots":5,"total":5,"upgrades":{"wheel":2},"soldToday":null}""");
        Assert.Equal("", House(h).GetProperty("clay").GetString());
        Assert.Equal(Clicker.BoardSize, Orders(h).GetProperty("board").GetArrayLength());
        Assert.Empty(Orders(h).GetProperty("taken").EnumerateArray());
        Assert.Equal(5, Pots(h));
    }

    [Fact]
    public void Nice_rounds_to_two_significant_digits()
    {
        Assert.Equal(12_000, Clicker.Nice(11_873));
        Assert.Equal(57, Clicker.Nice(56.2));
        Assert.Equal(1_200_000, Clicker.Nice(1_234_567));
        Assert.Equal(0, Clicker.Nice(-3));
    }

    // ---------- дев'яте оновлення: другий ряд знарядь ----------

    /// Купити все, що просить тест, не рахуючи глеків: ціни другого ряду — мільярди й трильйони.
    static void Rich(RoomHarness h) => Give(h, 2e14);

    static void Tools(RoomHarness h, params string[] keys) => Patch(h, s =>
        s["house"]!["tools"] = new JsonArray([.. keys.Select(k => (JsonNode)k!)]));

    static void Secrets(RoomHarness h, params string[] keys) => Patch(h, s =>
        s["secrets"] = new JsonArray([.. keys.Select(k => (JsonNode)k!)]));

    /// Поставити на дорогу стільки купців, скільки треба: повернуться за годину, тобто не посеред перевірки.
    static void Road(RoomHarness h, int n) => Patch(h, s =>
    {
        var road = new JsonArray();
        for (var i = 0; i < n; i++)
            road.Add(new JsonObject
            {
                ["id"] = 900 + i, ["merchant"] = "Чумак Іван", ["pay"] = 1_000,
                ["payAt"] = h.Clock.UtcNow.AddHours(1).ToString("O"),
            });
        s["house"]!["taken"] = road;
    });

    static JsonElement Looks(RoomHarness h) => House(h).GetProperty("looks");
    static JsonElement WondersView(RoomHarness h) => House(h).GetProperty("wonders");
    static int StampsFree(RoomHarness h) => View(h).GetProperty("stampsFree").GetInt32();
    static double AllMult(RoomHarness h) => View(h).GetProperty("allMult").GetDouble();

    static string LookNow(RoomHarness h, string key) =>
        Owned(Looks(h), key).GetProperty("value").GetString()!;

    static JsonElement Option(JsonElement looks, string group, string value) =>
        Owned(looks, group).GetProperty("options").EnumerateArray().Single(o => o.GetProperty("value").GetString() == value);

    /// Кинути тригер дивовижі напряму: у грі це роблять пакети через Wonder(trigger) з місця події.
    static ClickerWonder? Roll(RoomHarness h, string trigger)
    {
        lock (h.Room.Sync) return ((Clicker)h.Room.Game).RollWonder(trigger);
    }

    /// Скільки кидків знадобилось, щоб із цього тригера знайшлось усе, що з нього буває (−1 — не знайшлось).
    static int CallsToFindAll(RoomHarness h, string trigger)
    {
        var want = Clicker.Wonders.Count(w => w.Triggers.Contains(trigger));
        var found = 0;
        for (var i = 1; i <= 5_000; i++)
        {
            if (Roll(h, trigger) is not null) found++;
            if (found == want) return i;
        }
        return -1;
    }

    [Fact]
    public void The_abacus_and_the_scales_add_up_on_the_merchant_pay()
    {
        var h = Wheel();
        Rich(h);
        var bare = Orders(h).GetProperty("board").EnumerateArray().First();
        var id = bare.GetProperty("id").GetInt32();
        var pay = bare.GetProperty("pay").GetDouble();
        Assert.True(Act(h, "tool", new { key = "scales" }).Ok);
        Assert.True(Act(h, "tool", new { key = "abacus" }).Ok);
        var now = Orders(h).GetProperty("board").EnumerateArray().Single(o => o.GetProperty("id").GetInt32() == id);
        // Ваги +20 % і рахівниця +20 % складаються, а не множаться: ×1,4.
        Assert.Equal(Math.Floor(pay * 1.4), now.GetProperty("pay").GetDouble());
    }

    [Fact]
    public void The_cart_makes_the_bazaar_pay_more()
    {
        var h = Wheel();
        Patch(h, s => s["craft"]!["items"] = new JsonObject { ["pot||1"] = 10 });
        var before = Pots(h);
        Assert.True(Act(h, "bazaar", new { key = "pot||1", n = 5 }).Ok);
        var plain = Pots(h) - before;
        Rich(h);
        Assert.True(Act(h, "tool", new { key = "cart" }).Ok);
        before = Pots(h);
        Assert.True(Act(h, "bazaar", new { key = "pot||1", n = 5 }).Ok);
        Assert.Equal(Math.Floor(plain * (1 + Clicker.CartBazaar)), Pots(h) - before);
    }

    [Fact]
    public void The_kerosene_lamp_adds_two_more_hours_without_you()
    {
        var h = Wheel();
        var was = View(h).GetProperty("offlineHours").GetDouble();
        Rich(h);
        Assert.True(Act(h, "tool", new { key = "lamp" }).Ok);
        Assert.Equal(was + 2, View(h).GetProperty("offlineHours").GetDouble(), 6);
        // І ліхтар, і лампа — разом чотири години зверху.
        Assert.True(Act(h, "tool", new { key = "lantern" }).Ok);
        Assert.Equal(was + 4, View(h).GetProperty("offlineHours").GetDouble(), 6);
    }

    [Fact]
    public void The_cuckoo_clock_halves_the_clay_rest()
    {
        var h = Wheel();
        Rich(h);
        Assert.True(Act(h, "tool", new { key = "clock" }).Ok);
        Assert.True(Act(h, "knead", new { kind = "red" }).Ok);
        var rest = House(h).GetProperty("clayRestUntil").GetDateTimeOffset() - h.Clock.UtcNow;
        Assert.Equal(Clicker.ClayRestQuick.TotalMinutes, rest.TotalMinutes, 1);
        h.Clock.AdvanceMs((int)Clicker.ClayRestQuick.TotalMilliseconds + 1000);
        Assert.True(Act(h, "knead", new { kind = "" }).Ok);
    }

    [Fact]
    public void The_locked_chest_keeps_five_percent_of_the_pots_through_the_firing()
    {
        var h = Wheel();
        Give(h, 200_000_000_000, Clicker.TotalFor(1));
        Assert.True(Act(h, "tool", new { key = "lock" }).Ok);
        var left = Pots(h);
        Assert.True(Act(h, "fire").Ok);
        Assert.Equal(Math.Floor(left * Clicker.LockKeep), Pots(h));
        // Без скрині обпал забирає все до останнього глека.
        var bare = Wheel();
        Give(bare, 200_000_000_000, Clicker.TotalFor(1));
        Assert.True(Act(bare, "fire").Ok);
        Assert.Equal(0, Pots(bare));
    }

    [Fact]
    public void The_net_under_the_shelf_catches_thirty_percent_more()
    {
        var h = Wheel();
        Levels(h, ("apprentice", 100));
        FallNow(h);
        var plain = Fall(h).GetProperty("gain").GetDouble();
        Rich(h);
        Assert.True(Act(h, "tool", new { key = "net" }).Ok);
        FallNow(h);
        var with = Fall(h).GetProperty("gain").GetDouble();
        // Дно глека (FallFloor) множник не бере, тож порівнюємо те, що над ним.
        Assert.Equal(Math.Floor((plain - Clicker.FallFloor) * (1 + Clicker.NetFall)), with - Clicker.FallFloor);
    }

    [Fact]
    public void The_doorbell_keeps_the_golden_jug_five_seconds_longer()
    {
        var h = Wheel();
        Rich(h);
        Assert.True(Act(h, "tool", new { key = "bell2" }).Ok);
        Patch(h, x => x["golden"] = null);                       // збереження без розписного — розклад складається наново
        var g = View(h).GetProperty("golden");
        var shown = g.GetProperty("until").GetDateTimeOffset() - g.GetProperty("at").GetDateTimeOffset();
        Assert.Equal((Clicker.GoldenShown + Clicker.BellGolden).TotalSeconds, shown.TotalSeconds, 1);
    }

    [Fact]
    public void Every_tool_is_bought_once_and_shows_on_the_wall()
    {
        var h = Wheel();
        Rich(h);
        foreach (var tool in Clicker.Tools)
        {
            Assert.True(Act(h, "tool", new { key = tool.Key }).Ok, tool.Key);
            Assert.False(Act(h, "tool", new { key = tool.Key }).Ok);
            Assert.True(Owned(House(h).GetProperty("tools"), tool.Key).GetProperty("owned").GetBoolean(), tool.Key);
        }
        Assert.Equal(18, House(h).GetProperty("tools").GetArrayLength());
    }

    // ---------- другий ряд прикрас ----------

    [Fact]
    public void The_second_row_of_decorations_gives_three_percent_each()
    {
        var h = Wheel();
        Rich(h);
        var was = AllMult(h);
        Assert.True(Act(h, "adorn", new { key = "towel" }).Ok);
        Assert.Equal(was * (1 + Clicker.DecorBonus) / 1, AllMult(h), 9);
        var two = AllMult(h);
        Assert.True(Act(h, "adorn", new { key = "plakhta" }).Ok);
        // Другий ряд додається до першого всередині гурту хати: 1 + 2 % + 3 %.
        Assert.Equal(was * (1 + Clicker.DecorBonus + Clicker.Decor2Bonus), AllMult(h), 9);
        Assert.True(AllMult(h) > two);
        Assert.Contains("+3 %", Act(h, "adorn", new { key = "didukh" }).Message);
    }

    [Fact]
    public void Every_decoration_can_be_bought_once_and_the_firing_keeps_it()
    {
        var h = Wheel();
        Give(h, 2e15, Clicker.TotalFor(1));
        foreach (var d in Clicker.Decor)
        {
            Assert.True(Act(h, "adorn", new { key = d.Key }).Ok, d.Key);
            Assert.False(Act(h, "adorn", new { key = d.Key }).Ok);
        }
        Assert.Equal(12, House(h).GetProperty("decor").GetArrayLength());
        Assert.True(Act(h, "fire").Ok);
        foreach (var d in Clicker.Decor)
            Assert.True(Owned(House(h).GetProperty("decor"), d.Key).GetProperty("owned").GetBoolean(), d.Key);
    }

    // ---------- оздоба за клейма ----------

    [Fact]
    public void A_look_is_paid_with_stamps_once_and_then_worn_for_free()
    {
        var h = Wheel();
        Patch(h, s => s["stamps"] = 100);
        Assert.Equal("straw", LookNow(h, "roof"));
        var r = Act(h, "look", new { key = "roof", value = "tile" });
        Assert.True(r.Ok, r.Message);
        Assert.Contains("черепиця", r.Message);
        Assert.Equal("tile", LookNow(h, "roof"));
        Assert.Equal(60, StampsFree(h));                         // 100 − 40 за черепицю
        // Назад на солому — безплатно, і черепиця лишається купленою.
        Assert.True(Act(h, "look", new { key = "roof", value = "straw" }).Ok);
        Assert.Equal(60, StampsFree(h));
        Assert.True(Act(h, "look", new { key = "roof", value = "tile" }).Ok);
        Assert.Equal(60, StampsFree(h));
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-look");
    }

    [Fact]
    public void A_look_without_stamps_is_refused_and_changes_nothing()
    {
        var h = Wheel();
        Patch(h, s => s["stamps"] = 10);
        var r = Act(h, "look", new { key = "wheel", value = "painted" });
        Assert.False(r.Ok);
        Assert.Contains("Бракує клейм", r.Message);
        Assert.Equal("oakwood", LookNow(h, "wheel"));
        Assert.Equal(10, StampsFree(h));
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:potter-look");
    }

    [Fact]
    public void The_first_variant_of_a_look_is_free_and_nonsense_is_refused()
    {
        var h = Wheel();
        Assert.Equal(0, StampsFree(h));
        Assert.False(Act(h, "look", new { key = "wall", value = "white" }).Ok);   // уже така
        Assert.False(Act(h, "look", new { key = "moon", value = "white" }).Ok);
        Assert.False(Act(h, "look", new { key = "wall", value = "рожева" }).Ok);
        // Безплатний варіант іншого гурту вдягається й без клейм.
        Patch(h, s => s["house"]!["look"] = new JsonObject { ["wall"] = "blue" });
        Assert.Equal("white", LookNow(h, "wall"));               // некуплене з бази не вдягаємо
    }

    [Fact]
    public void Stamps_spent_on_a_look_keep_their_bonus_and_the_firing_keeps_the_look()
    {
        var h = Wheel();
        Patch(h, s => { s["stamps"] = 200; s["total"] = Clicker.TotalFor(250); s["pots"] = 1_000; });
        var mult = AllMult(h);
        Assert.True(Act(h, "look", new { key = "tree", value = "oak" }).Ok);
        Assert.Equal(mult, AllMult(h), 9);                       // клейма на оздобі бонусу не гублять
        Assert.Equal(170, StampsFree(h));
        Assert.True(Act(h, "fire").Ok);
        Assert.Equal("oak", LookNow(h, "tree"));                 // хата не горить
        Assert.True(Option(Looks(h), "tree", "oak").GetProperty("owned").GetBoolean());
    }

    [Fact]
    public void A_look_lives_through_save_and_load_and_an_old_save_is_a_plain_house()
    {
        var h = Wheel();
        Patch(h, s => s["stamps"] = 300);
        Assert.True(Act(h, "look", new { key = "fence", value = "hedge" }).Ok);
        Assert.True(Act(h, "look", new { key = "pet", value = "ginger" }).Ok);
        Patch(h, _ => { });                                      // Save → Load без правок
        Assert.Equal("hedge", LookNow(h, "fence"));
        Assert.Equal("ginger", LookNow(h, "pet"));
        Assert.Equal(300 - 35 - 15, StampsFree(h));

        var old = Wheel();
        lock (old.Room.Sync)
            old.Room.Game.Load("""{"pots":5,"total":5,"upgrades":{"wheel":2},"soldToday":null}""");
        foreach (var group in Clicker.Looks)
            Assert.Equal(group.Options[0].Value, LookNow(old, group.Key));
        Assert.Equal("Хата гончаря", House(old).GetProperty("name").GetString());
        Assert.Equal("", House(old).GetProperty("named").GetString());
    }

    // ---------- ім'я хати ----------

    [Fact]
    public void The_house_gets_a_name_and_it_is_cleaned_up()
    {
        var h = Wheel();
        Assert.True(Act(h, "name", new { text = "  Хата над ставом  " }).Ok);
        Assert.Equal("Хата над ставом", House(h).GetProperty("name").GetString());
        Assert.Equal("Хата над ставом", House(h).GetProperty("named").GetString());
        // Розмітки, лапок і подвійних пробілів на вивісці не буває.
        Assert.True(Act(h, "name", new { text = "<b>Глек</b>  &  \"кум\"" }).Ok);
        Assert.Equal("bГлек/b кум", House(h).GetProperty("name").GetString());
        // Довше 24 знаків не влізе.
        Assert.True(Act(h, "name", new { text = new string('я', 40) }).Ok);
        Assert.Equal(24, House(h).GetProperty("name").GetString()!.Length);
        // Порожнє — назад на типову вивіску; те саме вдруге не приймається.
        Assert.True(Act(h, "name", new { text = "   " }).Ok);
        Assert.Equal("Хата гончаря", House(h).GetProperty("name").GetString());
        Assert.False(Act(h, "name", new { text = "" }).Ok);
        Assert.Equal(24, Clicker.HouseNameMax);
        Assert.Equal("", Clicker.CleanName("\u0007\u0001"));
    }

    [Fact]
    public void The_name_lives_through_save_and_the_firing()
    {
        var h = Wheel();
        Give(h, 1_000, Clicker.TotalFor(1));
        Assert.True(Act(h, "name", new { text = "Хата Оленчина" }).Ok);
        Assert.True(Act(h, "fire").Ok);
        Patch(h, _ => { });
        Assert.Equal("Хата Оленчина", House(h).GetProperty("name").GetString());
    }

    // ---------- дивовижі ----------

    [Fact]
    public void Every_wonder_has_triggers_from_the_contract_and_is_reachable()
    {
        string[] known =
        [
            "eye", "streak", "lucky", "cat", "star", "fire", "kiln-perfect", "paint-90",
            "album-row-stars", "stove-full", "mastery-10", "lord-order", "holiday-guest", "rep-10", "wagon-gold", "treat",
        ];
        Assert.Equal(16, Clicker.Wonders.Length);
        Assert.Equal(16, Clicker.Wonders.Select(w => w.Key).Distinct().Count());
        foreach (var w in Clicker.Wonders)
        {
            Assert.InRange(w.Triggers.Length, 1, 3);
            Assert.All(w.Triggers, t => Assert.Contains(t, known));
            Assert.False(string.IsNullOrWhiteSpace(w.Name), w.Key);
            Assert.True(w.Tale.Length > 40, w.Key);
        }
        // Жоден тригер не висить ні до чого, і кожна дивовижа з якогось таки приходить.
        Assert.All(known, t => Assert.Contains(Clicker.Wonders, w => w.Triggers.Contains(t)));

        // Насправді знаходиться все: ходимо по всіх тригерах, поки хата не повна.
        var h = Wheel();
        for (var round = 0; round < 60; round++)
            foreach (var t in known) Roll(h, t);
        Assert.Equal(16, WondersView(h).GetProperty("found").GetInt32());
    }

    [Fact]
    public void A_trigger_finds_only_its_own_wonders_and_never_twice()
    {
        var h = Wheel();
        var want = Clicker.Wonders.Where(w => w.Triggers.Contains("eye")).Select(w => w.Key).Order().ToList();
        Assert.NotEmpty(want);
        var calls = CallsToFindAll(h, "eye");
        Assert.True(calls > want.Count, $"кидок не мусить щастити щоразу, а знадобилось лише {calls}");
        // Більше з цього тригера нічого: пул порожній, кидок навіть не робиться.
        Assert.Null(Roll(h, "eye"));
        var found = WondersView(h).GetProperty("list").EnumerateArray()
            .Where(w => w.GetProperty("found").GetBoolean()).Select(w => w.GetProperty("key").GetString()!).Order().ToList();
        Assert.Equal(want, found);
        Assert.Null(Roll(h, ""));
        Assert.Null(Roll(h, "невідомо-що"));
    }

    [Fact]
    public void The_mirror_finds_wonders_twice_as_often()
    {
        var plain = Wheel();
        var lucky = Wheel();
        Rich(lucky);
        Assert.True(Act(lucky, "tool", new { key = "mirror" }).Ok);
        Assert.True(CallsToFindAll(lucky, "streak") < CallsToFindAll(plain, "streak"));
        Assert.Equal(2, Clicker.MirrorWonder);
    }

    [Fact]
    public void Each_wonder_adds_one_percent_to_everything_and_lives_through_the_firing()
    {
        var h = Wheel();
        Give(h, 1_000, Clicker.TotalFor(1));
        var was = AllMult(h);
        while (WondersView(h).GetProperty("found").GetInt32() < 2) Roll(h, "fire");
        Assert.Equal(was * (1 + 2 * Clicker.WonderBonus), AllMult(h), 9);
        var found = WondersView(h).GetProperty("found").GetInt32();
        Assert.True(Act(h, "fire").Ok);
        Assert.Equal(found, WondersView(h).GetProperty("found").GetInt32());
        Patch(h, _ => { });
        Assert.Equal(found, WondersView(h).GetProperty("found").GetInt32());
        // У виді є і час знахідки (клієнт малює картку раз), і байка, і «звідки» для ще не знайдених.
        foreach (var w in WondersView(h).GetProperty("list").EnumerateArray())
        {
            Assert.False(string.IsNullOrEmpty(w.GetProperty("from").GetString()));
            if (w.GetProperty("found").GetBoolean())
            {
                Assert.False(string.IsNullOrEmpty(w.GetProperty("name").GetString()));
                Assert.NotEqual(JsonValueKind.Null, w.GetProperty("at").ValueKind);
            }
            else
            {
                Assert.Equal("", w.GetProperty("name").GetString());
                Assert.Equal("", w.GetProperty("tale").GetString());
            }
        }
    }

    [Fact]
    public void The_first_wonder_and_the_full_sixteen_give_their_achievements()
    {
        var h = Wheel();
        while (WondersView(h).GetProperty("found").GetInt32() < 1) Roll(h, "cat");
        Act(h, "look", new { catalog = true });                  // черга ачівок висипається найближчою дією
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-wonder");
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:potter-wonders");
        for (var round = 0; round < 60 && WondersView(h).GetProperty("found").GetInt32() < 16; round++)
            foreach (var w in Clicker.Wonders) foreach (var t in w.Triggers) Roll(h, t);
        Act(h, "look", new { catalog = true });
        Assert.Equal(16, WondersView(h).GetProperty("found").GetInt32());
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-wonders");
    }

    // ---------- великий обоз ----------

    [Fact]
    public void The_great_caravan_widens_the_board_and_the_road()
    {
        var h = Wheel();
        Assert.Equal(Clicker.BoardSize, Orders(h).GetProperty("board").GetArrayLength());
        Assert.Equal(Clicker.MaxTaken, Orders(h).GetProperty("maxTaken").GetInt32());
        Secrets(h, "caravan");
        h.Clock.AdvanceMs((int)Clicker.BoardEvery.TotalMilliseconds + 1000);
        Give(h, 10_000_000);
        Assert.Equal(Clicker.CaravanBoard, Orders(h).GetProperty("board").GetArrayLength());
        Assert.Equal(Clicker.CaravanTaken, Orders(h).GetProperty("maxTaken").GetInt32());
        // Чотири купці вже в дорозі (далеко, щоб не поверталися посеред перевірки) — п'ятий ще влазить, шостий уже ні.
        Road(h, Clicker.CaravanTaken - 1);
        var invest = Orders(h).GetProperty("board").EnumerateArray().First(o => o.GetProperty("kind").GetString() == "invest");
        Assert.True(Act(h, "take", new { id = invest.GetProperty("id").GetInt32() }).Ok);
        Assert.Equal(Clicker.CaravanTaken, Orders(h).GetProperty("taken").GetArrayLength());
        var more = Orders(h).GetProperty("board").EnumerateArray().First(o => o.GetProperty("kind").GetString() == "invest");
        var no = Act(h, "take", new { id = more.GetProperty("id").GetInt32() });
        Assert.False(no.Ok);
        Assert.Contains("Уже в дорозі 5 купців", no.Message);
        // Без обозу на дошці три замовлення, а в дорозі — троє.
        Secrets(h);
        Road(h, Clicker.MaxTaken);
        var third = Orders(h).GetProperty("board").EnumerateArray().First(o => o.GetProperty("kind").GetString() == "invest");
        Assert.Contains("Уже в дорозі 3 купці", Act(h, "take", new { id = third.GetProperty("id").GetInt32() }).Message);
    }
}
