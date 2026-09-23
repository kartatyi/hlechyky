using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Правки за рецензією сервера дев'ятого оновлення (docs/games/specs/clicker.md, «Дев'яте оновлення»): Око майстра платить
/// за кліки, а не за ловлю; щедрий купець не просів проти восьмого оновлення; серія від кота — тим самим шляхом, що й
/// спійманий глек; базарний день не гарантований після ночі; підмайстер друга діє й у ветерана.
/// </summary>
public class ClickerReviewTests
{
    static RoomHarness Wheel(string nick = "Оля", int seed = 1)
    {
        var h = new RoomHarness("clicker", seed: seed);
        h.Solo(nick);
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static double Pots(RoomHarness h) => View(h).GetProperty("pots").GetDouble();
    static JsonElement Guard(RoomHarness h) => View(h).GetProperty("guard");
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

    static void Levels(RoomHarness h, params (string Key, int Level)[] levels) => Patch(h, s =>
    {
        foreach (var (key, level) in levels) s["upgrades"]![key] = level;
    });

    // ---------- Око платить за кліки ----------

    [Fact]
    public void A_shelf_earned_by_catching_alone_pays_next_to_nothing()
    {
        var h = Wheel();
        Levels(h, ("apprentice", 10));
        // Полиця назріла з ваги спійманих глеків, а кліків від минулої полиці не було зовсім.
        Patch(h, s => { s["guard"]!["left"] = 0; s["guard"]!["clicks"] = 0; });
        Human(h);                                       // єдиний клік — він і покликав майстра
        Assert.NotEqual(JsonValueKind.Null, Guard(h).ValueKind);
        // Повна платня — 46 000; за один клік із шести тисяч майстер дає його частку, тобто копійки.
        var gain = Guard(h).GetProperty("gain").GetDouble();
        Assert.InRange(gain, 0, 46_000.0 / ClickerGuard.CalmMin + 1);

        var before = Pots(h);
        var r = PotterHands.Pass(h);
        Assert.True(r.Ok);
        Assert.InRange(Pots(h) - before, 0, 46_000.0 / ClickerGuard.CalmMin + 1);
    }

    [Fact]
    public void Half_the_clicks_is_half_the_pay_and_a_full_share_is_the_whole_two_hours()
    {
        var h = Wheel();
        Levels(h, ("apprentice", 10));                 // 5 глеків за секунду, клік = 1
        Patch(h, s => { s["guard"]!["left"] = 0; s["guard"]!["clicks"] = ClickerGuard.CalmMin / 2 - 1; });
        Human(h);                                       // цей клік — останній із половини
        Assert.True(Guard(h).GetProperty("pays").GetBoolean());
        Assert.Equal((5 * 7200 + 10_000) * 0.5, Guard(h).GetProperty("gain").GetDouble(), 0);

        var g = Wheel("Галя");
        Levels(g, ("apprentice", 10));
        Patch(g, s => { s["guard"]!["left"] = 0; s["guard"]!["clicks"] = ClickerGuard.CalmMax; });
        Human(g);
        Assert.Equal(5 * 7200 + 10_000, Guard(g).GetProperty("gain").GetDouble());
        var before = Pots(g);
        Assert.Contains("відсипав", PotterHands.Pass(g).Message);
        Assert.Equal(before + 46_000, Pots(g));
    }

    [Fact]
    public void The_click_count_toward_the_pay_survives_save_and_load_and_an_old_save_starts_from_zero()
    {
        var h = Wheel();
        Human(h, 12);
        JsonObject Save() { lock (h.Room.Sync) return JsonNode.Parse(h.Room.Game.Save()!)!.AsObject(); }
        Assert.Equal(12, Save()["guard"]!["clicks"]!.GetValue<int>());
        Patch(h, s => { });
        Assert.Equal(12, Save()["guard"]!["clicks"]!.GetValue<int>());
        Patch(h, s => { ((JsonObject)s["guard"]!).Remove("clicks"); ((JsonObject)s["guard"]!).Remove("share"); });
        Assert.Equal(0, Save()["guard"]!["clicks"]!.GetValue<int>());
    }

    // ---------- Щедрий купець ----------

    [Fact]
    public void The_merchant_never_gives_less_than_before_the_ninth_update()
    {
        // До дев'ятого оновлення купець давав min(15 % кишені, 15 хв пасиву); тепер — не менше й з дном у шість хвилин.
        var flat = 100.0 * Clicker.MerchantSeconds;
        foreach (var pots in new[] { 0.0, 1e4, 1e6, 1e9 })
        {
            var old = Math.Min(pots * 0.15, 100.0 * 900);
            var now = flat + Math.Min(pots * Clicker.MerchantShare, flat * Clicker.MerchantCapShare);
            Assert.True(now >= old, $"кишеня {pots}: було {old}, стало {now}");
        }
        Assert.Equal(100.0 * 900, flat + Math.Min(1e9 * Clicker.MerchantShare, flat * Clicker.MerchantCapShare));
    }

    // ---------- Серія від кота ----------

    [Fact]
    public void A_streak_the_cat_extends_counts_for_the_achievement_like_a_caught_jug()
    {
        var h = Wheel();
        Patch(h, s =>
        {
            s["fallStreak"] = Clicker.StreakForAchievement - 1;
            var now = h.Clock.UtcNow;
            // Кіт на сцені просто зараз і з гостинцем «серія» (a = 3, див. CatGift).
            s["cat"] = new JsonObject { ["at"] = now.ToString("O"), ["until"] = now.AddSeconds(6).ToString("O"), ["a"] = 3, ["b"] = 0 };
        });
        var r = Act(h, "pet", new { x = 50 });
        Assert.True(r.Ok, r.Message);
        Assert.Contains("серія", r.Message);
        Assert.Equal(Clicker.StreakForAchievement, View(h).GetProperty("fall").GetProperty("streak").GetInt32());
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-streak");
    }

    // ---------- Підмайстер друга у ветерана ----------

    [Fact]
    public void A_friends_apprentice_works_even_below_the_mastery_floor()
    {
        var h = Wheel();
        Patch(h, s => s["craft"]!["firedBy"] = new JsonObject { ["pot"] = 25_000 });   // майстерність 10 — робота вже на підлозі 40 %
        int Need() => View(h).GetProperty("craft").GetProperty("wares").EnumerateArray()
            .First(w => w.GetProperty("key").GetString() == "pot").GetProperty("need").GetInt32();
        var floor = Need();
        Assert.Equal((int)Math.Ceiling(40 * Clicker.MinWorkShare), floor);
        Patch(h, s => s["guild"]!["buffs"] = new JsonObject
        {
            ["lend"] = h.Clock.UtcNow.AddHours(20).ToString("O"), ["lendFrom"] = "Микола",
        });
        Assert.True(Need() < floor, $"з підмайстром друга {Need()} проти підлоги {floor}");
    }
}
