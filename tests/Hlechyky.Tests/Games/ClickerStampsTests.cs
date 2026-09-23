using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Клейма після тисячі й наука майстра (docs/games/specs/clicker-stamps.md): крива бонусу й «Родове клеймо» на ній,
/// Тавро майстра, якого наступний обпал більше не «з'їдає», старе збереження, вид для клієнта, наука з цехом —
/// частка, стеля, раз на 20 годин, найкращий гончар і клейма, які цех дочитує зі збережень.
/// </summary>
public class ClickerStampsTests
{
    static RoomHarness Wheel(string nick = "Оля", ClickerGuildService? guild = null)
    {
        var h = new RoomHarness("clicker", services: guild is null ? null : RoomHarness.WithService(guild));
        h.Solo(nick);
        Assert.True(h.Act(0, "look").Ok);
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static ActResult Act(RoomHarness h, string action, object? payload = null) => h.Act(0, action, payload);
    static double AllMult(RoomHarness h) => View(h).GetProperty("allMult").GetDouble();
    static int Stamps(RoomHarness h) => View(h).GetProperty("stamps").GetInt32();
    static int Extra(RoomHarness h) => View(h).GetProperty("stampsExtra").GetInt32();
    static JsonElement Science(RoomHarness h) => View(h).GetProperty("science");

    /// <summary>Переписати збережений стан і завантажити назад — набивати мільярди кліками нема коли.</summary>
    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    /// <summary>Глеків за весь час рівно на n клейм.</summary>
    static void Earned(RoomHarness h, int stamps) => Patch(h, s => s["total"] = Clicker.TotalFor(stamps));

    static void Strings(RoomHarness h, string field, params string[] values) => Patch(h, s =>
    {
        var arr = new JsonArray();
        foreach (var v in values) arr.Add(v);
        s[field] = arr;
    });

    /// <summary>Округа: один цех на кількох гончарів, як на сервері.</summary>
    sealed class Okruha
    {
        public FakeStore Store { get; } = new();
        public FakeClock Clock { get; } = new();
        public ClickerGuildService Svc { get; }
        public Okruha() => Svc = new ClickerGuildService(Store, Clock);

        /// <summary>Гончар, якого цех уже знає з клеймами (без власної кімнати в тесті).</summary>
        public void Known(string nick, int stamps) => Svc.Hello(ClickerGuildService.Key(nick), nick, 0, Clock.UtcNow, stamps);
    }

    // ---------- крива ----------

    [Fact]
    public void The_first_thousand_counts_in_full_and_then_every_stamp_weighs_less()
    {
        Assert.Equal(0, Clicker.StampWeight(0));
        Assert.Equal(0, Clicker.StampWeight(-5));
        Assert.Equal(1, Clicker.StampWeight(1));
        Assert.Equal(1000, Clicker.StampWeight(1000));
        Assert.Equal(3000, Clicker.StampWeight(4000), 9);
        Assert.Equal(7000, Clicker.StampWeight(16_000), 9);
        // На тисячі — без сходинки: наступне клеймо важить майже рівно одне…
        Assert.InRange(Clicker.StampWeight(1001) - Clicker.StampWeight(1000), 0.999, 1.0);
        // …на 4000 — половину, на 16 000 — чверть.
        Assert.InRange(Clicker.StampWeight(4001) - Clicker.StampWeight(4000), 0.49, 0.51);
        Assert.InRange(Clicker.StampWeight(16_001) - Clicker.StampWeight(16_000), 0.24, 0.26);
    }

    [Fact]
    public void Up_to_a_thousand_stamps_the_bonus_is_what_it_always_was()
    {
        var h = Wheel();
        Patch(h, s => s["stamps"] = 1000);
        Assert.Equal(21, AllMult(h), 9);                             // 1 + 2 % × 1000
    }

    [Fact]
    public void Past_a_thousand_the_bonus_grows_as_a_root()
    {
        var h = Wheel();
        Patch(h, s => s["stamps"] = 4000);
        Assert.Equal(61, AllMult(h), 9);                             // 1 + 2 % × 3000, а не × 4000
    }

    [Fact]
    public void The_seal_makes_the_whole_curve_half_as_big_again()
    {
        var h = Wheel();
        Patch(h, s => s["stamps"] = 4000);
        Strings(h, "secrets", "seal");
        Assert.Equal(91, AllMult(h), 9);                             // 1 + 3 % × 3000
    }

    [Fact]
    public void A_leader_with_thousands_of_stamps_keeps_only_the_curve()
    {
        // Микола з проду 23.09: 5783 клейма з Родовим клеймом — було ×174,5, стало ×115,3.
        var h = Wheel();
        Patch(h, s => s["stamps"] = 5783);
        Strings(h, "secrets", "seal");
        Assert.Equal(1 + 0.03 * 1000 * (2 * Math.Sqrt(5.783) - 1), AllMult(h), 9);
        Assert.InRange(AllMult(h), 115.2, 115.4);
    }

    // ---------- обпал: клейма за глеки й зверху ----------

    [Fact]
    public void The_iron_stamp_is_kept_by_every_next_firing()
    {
        var h = Wheel();
        Patch(h, s => { s["pots"] = 100_000_000; s["total"] = Clicker.TotalFor(2); });
        Assert.True(Act(h, "tool", new { key = "iron" }).Ok);
        Assert.True(Act(h, "fire").Ok);
        Assert.Equal(3, Stamps(h));                                   // 2 за глеки + 1 від тавра
        Assert.Equal(1, Extra(h));

        Earned(h, 3);
        Assert.Equal(1, View(h).GetProperty("stampsReady").GetInt32());
        // Раніше тавро «з'їдалось»: приріст рахувався від усіх клейм, і тут обпал відмовляв.
        Assert.Equal("🔥 Обпал! +2 клейма — тепер +10 % до всього", Act(h, "fire").Message);
        Assert.Equal(5, Stamps(h));
        Assert.Equal(2, Extra(h));
    }

    [Fact]
    public void An_old_save_counts_every_stamp_as_earned_with_pots()
    {
        var h = Wheel();
        Patch(h, s =>
        {
            s["stamps"] = 10;
            s["total"] = Clicker.TotalFor(12);
            s.Remove("stampsExtra");
            s.Remove("scienceAt");
        });

        Assert.Equal(0, Extra(h));
        Assert.Equal(2, View(h).GetProperty("stampsReady").GetInt32());
        Assert.Equal(JsonValueKind.Null, Science(h).GetProperty("readyAt").ValueKind);
    }

    [Fact]
    public void Extra_stamps_from_a_hand_edited_save_never_outnumber_the_stamps()
    {
        var h = Wheel();
        Patch(h, s => { s["stamps"] = 10; s["stampsExtra"] = 50; });
        Assert.Equal(10, Extra(h));
    }

    [Fact]
    public void The_view_carries_the_curve_and_the_iron_for_the_client()
    {
        var h = Wheel();
        var v = View(h);
        Assert.Equal(Clicker.StampSoftFrom, v.GetProperty("stampSoft").GetInt32());
        Assert.Equal(1.0, v.GetProperty("stampMult").GetDouble(), 9);
        Assert.Equal(0, v.GetProperty("stampIron").GetInt32());
        Assert.Equal(Clicker.ScienceShare, v.GetProperty("science").GetProperty("share").GetDouble());
        Assert.Equal(Clicker.ScienceCap, v.GetProperty("science").GetProperty("cap").GetInt32());

        Patch(h, s => s["pots"] = 100_000_000);
        Assert.True(Act(h, "tool", new { key = "iron" }).Ok);
        Assert.Equal(1, View(h).GetProperty("stampIron").GetInt32());
    }

    // ---------- наука майстра ----------

    [Fact]
    public void Without_the_guild_there_is_nobody_to_learn_from()
    {
        var h = Wheel();
        Earned(h, 100);

        var sc = Science(h);
        Assert.Equal(0, sc.GetProperty("top").GetInt32());
        Assert.Equal("", sc.GetProperty("who").GetString());
        Assert.Equal("🔥 Обпал! +100 клейм — тепер +200 % до всього", Act(h, "fire").Message);
        Assert.Equal(0, Extra(h));
    }

    [Fact]
    public void A_potter_behind_learns_a_quarter_of_the_gap_on_the_firing()
    {
        var o = new Okruha();
        o.Known("Микола", 3000);
        var h = Wheel("Назар", o.Svc);
        Earned(h, 1000);

        var sc = Science(h);
        Assert.Equal(3000, sc.GetProperty("top").GetInt32());
        Assert.Equal("Микола", sc.GetProperty("who").GetString());
        Assert.Equal(JsonValueKind.Null, sc.GetProperty("readyAt").ValueKind);

        // Різниця 3000 − 1000 = 2000, чверть — 500 (стеля 2 × 1000 далеко).
        Assert.StartsWith("🔥 Обпал! +1000 клейм і ще +500 від науки майстра — тепер +", Act(h, "fire").Message);
        Assert.Equal(1500, Stamps(h));
        Assert.Equal(500, Extra(h));
        Assert.Equal(h.Clock.UtcNow + Clicker.ScienceEvery, Science(h).GetProperty("readyAt").GetDateTimeOffset());
    }

    [Fact]
    public void The_science_is_capped_at_twice_the_stamps_earned_with_pots()
    {
        // Назар із проду 23.09: 67 клейм за глеки проти 5783 у Миколи — чверть різниці була б 1429, стеля — 134.
        var o = new Okruha();
        o.Known("Микола", 5783);
        var h = Wheel("Назар", o.Svc);
        Earned(h, 67);

        Assert.StartsWith("🔥 Обпал! +67 клейм і ще +134 від науки майстра", Act(h, "fire").Message);
        Assert.Equal(201, Stamps(h));
    }

    [Fact]
    public void The_science_comes_once_in_twenty_hours()
    {
        var o = new Okruha();
        o.Known("Микола", 3000);
        var h = Wheel("Назар", o.Svc);
        Earned(h, 1000);
        Assert.True(Act(h, "fire").Ok);                               // 1000 + 500 науки
        Assert.Equal(1500, Stamps(h));

        h.Clock.Advance(TimeSpan.FromHours(10));
        Earned(h, 1100);
        var r = Act(h, "fire");
        Assert.True(r.Ok);
        Assert.DoesNotContain("науки", r.Message);
        Assert.Equal(1600, Stamps(h));

        h.Clock.Advance(TimeSpan.FromHours(10));                      // 20 годин від минулої науки
        Earned(h, 1200);
        // Різниця — від усіх клейм, разом із минулою наукою: 3000 − 1200 − 500 = 1300, чверть — 325.
        Assert.StartsWith("🔥 Обпал! +100 клейм і ще +325 від науки майстра", Act(h, "fire").Message);
        Assert.Equal(2025, Stamps(h));
        Assert.Equal(825, Extra(h));
    }

    [Fact]
    public void A_gap_too_small_for_a_whole_stamp_does_not_spend_the_science()
    {
        var o = new Okruha();
        o.Known("Микола", 1003);
        var h = Wheel("Назар", o.Svc);
        Earned(h, 1000);

        Assert.DoesNotContain("науки", Act(h, "fire").Message);
        Assert.Equal(JsonValueKind.Null, Science(h).GetProperty("readyAt").ValueKind);
    }

    [Fact]
    public void The_best_potter_has_nobody_to_learn_from()
    {
        var o = new Okruha();
        o.Known("Назар", 500);
        var h = Wheel("Микола", o.Svc);
        Earned(h, 3000);

        var r = Act(h, "fire");
        Assert.True(r.Ok);
        Assert.DoesNotContain("науки", r.Message);
        Assert.Equal(0, Extra(h));
        Assert.Equal(JsonValueKind.Null, Science(h).GetProperty("readyAt").ValueKind);
    }

    [Fact]
    public void Two_potters_of_one_district_learn_through_the_guild()
    {
        var o = new Okruha();
        var mykola = Wheel("Микола", o.Svc);
        Earned(mykola, 3000);
        Assert.True(Act(mykola, "fire").Ok);
        // Обпал сказав цеху нові клейма; себе цех найкращим для себе ж не рахує.
        Assert.Equal(("Микола", 3000), o.Svc.TopStamps(ClickerGuildService.Key("Назар")));
        Assert.Equal(("", 0), o.Svc.TopStamps(ClickerGuildService.Key("Микола")));

        var nazar = Wheel("Назар", o.Svc);
        Earned(nazar, 1000);
        Assert.Contains("і ще +500 від науки майстра", Act(nazar, "fire").Message);
    }

    // ---------- цех знає клейма ----------

    [Fact]
    public void A_hello_without_stamps_keeps_the_stamps_the_guild_already_knows()
    {
        var o = new Okruha();
        o.Svc.Hello("микола", "Микола", 0, o.Clock.UtcNow, 3000);
        o.Svc.Hello("микола", "Микола", 1, o.Clock.UtcNow);              // новий ранг, клейм не сказав
        Assert.Equal(("Микола", 3000), o.Svc.TopStamps("назар"));
    }

    [Fact]
    public void The_guild_reads_the_stamps_it_does_not_know_from_the_save_once()
    {
        var store = new FakeStore();
        // Стан цеху з часів до науки: гончар у списку є, а клейм при ньому нема.
        store.SaveState(ClickerGuildService.StoreKey,
            "{\"potters\":{\"микола\":{\"nick\":\"Микола\",\"rank\":2,\"seen\":\"2026-09-09T10:00:00+00:00\"}}}");
        store.SaveState("clicker:микола", "{\"stamps\":5783,\"total\":3.4e16}");
        var svc = new ClickerGuildService(store, new FakeClock());

        Assert.Equal(("Микола", 5783), svc.TopStamps("назар"));
        // Прочитане лягло в список цеху: друге питання вже не йде в збереження.
        store.SaveState("clicker:микола", "{\"stamps\":1}");
        Assert.Equal(("Микола", 5783), svc.TopStamps("назар"));
        Assert.Contains("\"stamps\":5783", store.LoadState(ClickerGuildService.StoreKey));
    }

    [Fact]
    public void A_potter_without_a_save_counts_as_nobody()
    {
        var store = new FakeStore();
        store.SaveState(ClickerGuildService.StoreKey,
            "{\"potters\":{\"оля\":{\"nick\":\"Оля\",\"rank\":0,\"seen\":\"2026-09-09T10:00:00+00:00\"}}}");
        var svc = new ClickerGuildService(store, new FakeClock());

        Assert.Equal(("", 0), svc.TopStamps("назар"));
    }
}
