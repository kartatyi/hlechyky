using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Хвилина гри» (10.10): заміри прод-збережень smaug і владіка показали, що година гри — це 10–15 тисяч годин пасиву, і
/// все, що платило «хвилинами пасиву» (купець, Око, гості, села, купці хати, віз, гостинці, кіт, сорока), важило копійки,
/// а 33 віхи-модифікатори (Гарт, Ніч за стелею доби, «видно довше», купець, Око) не давали нічого. Тут — нова мірка,
/// комора під полицею для віх Ночі й перероблені віхи.
/// </summary>
public class ClickerPlayMinuteTests
{
    static RoomHarness Wheel(string nick = "Оля", int seed = 1)
    {
        var h = new RoomHarness("clicker", seed: seed);
        h.Solo(nick);
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static double Pots(RoomHarness h) => View(h).GetProperty("pots").GetDouble();
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

    static RoomHarness Rich()
    {
        var h = Wheel();
        Levels(h, ("wheel", 100), ("apprentice", 100), ("kiln", 100), ("workshop", 300), ("basket", 10), ("flywheel", 8));
        return h;
    }

    static IEnumerable<(ClickerUpgrade Up, int I, ClickerMark M)> AllMarks =>
        Clicker.Shop.SelectMany(u => u.Steps.Select((m, i) => (u, i, m)));

    // ---------- сама мірка ----------

    [Fact]
    public void A_minute_of_play_is_shelf_pots_and_clicks_and_grows_with_the_streak()
    {
        var h = Rich();
        var play = ClickerPlay.Minute(h);
        var v = View(h);
        // Ні пасив, ні клік — більше: хвилина гри — це щонайменше п'ять кліків за секунду на повному розгоні.
        Assert.True(play >= 60 * Clicker.PlayCps * v.GetProperty("perClick").GetDouble(), $"хвилина гри {play}");
        Assert.True(play > 60 * v.GetProperty("perSecond").GetDouble());
        Patch(h, s => s["fallStreak"] = 300);
        Assert.True(ClickerPlay.Minute(h) > play * 2, "серія множить глек з полиці, а з ним — і хвилину гри");
    }

    [Fact]
    public void The_merchant_brings_minutes_of_play_and_his_marks_add_half_a_minute_each()
    {
        // Пізня гра в мініатюрі: пасиву мало, а глек з полиці з довгою серією й клік на розгоні — багато.
        var h = Wheel();
        Levels(h, ("wheel", 100), ("apprentice", 10), ("basket", 10), ("flywheel", 8));
        Patch(h, s => s["fallStreak"] = 300);
        var bare = ClickerPlay.Pay(h, Clicker.MerchantPlay);
        Marks(h, "fair:200", "chumaks:200");
        var play = ClickerPlay.Minute(h);
        Patch(h, s =>
        {
            s["golden"] = new JsonObject
            {
                ["at"] = h.Clock.UtcNow.ToString("O"), ["until"] = (h.Clock.UtcNow + Clicker.GoldenShown).ToString("O"),
                ["kind"] = (int)Clicker.GoldenKind.Merchant, ["x"] = 10, ["y"] = 10,
            };
        });
        var before = Pots(h);
        var r = Act(h, "catch");
        Assert.True(r.Ok, r.Message);
        Assert.Contains("купець", r.Message);
        var old = Math.Floor(View(h).GetProperty("baseSecond").GetDouble() * Clicker.MerchantSeconds) + 13;
        var pay = Math.Floor(play * (Clicker.MerchantPlay + 2 * Clicker.MarkMerchantMinutes));
        Assert.True(pay > old, $"хвилини гри {pay} мусять бути більші за чотири години пасиву {old}");
        Assert.Equal(before + pay, Pots(h));
        Assert.True(Pots(h) - before > bare);
    }

    // ---------- комора під полицею (віхи Ночі) ----------

    [Fact]
    public void A_night_mark_lets_the_store_catch_a_share_of_the_pots_that_fell_without_you()
    {
        var h = Rich();
        Marks(h, "kiln:100");                                  // «Піч на всю ніч»
        var shelf = View(h).GetProperty("fall").GetProperty("gain").GetDouble();
        var wait = (Clicker.FallMinSeconds + Clicker.FallMaxSeconds) / 2.0;
        var before = Pots(h);
        h.Clock.Advance(TimeSpan.FromHours(8));
        Assert.True(Act(h, "look").Ok);
        var store = Math.Floor(8 * 3600 / wait * shelf * Clicker.NightShelfShare);
        var notes = View(h).GetProperty("away").GetProperty("notes").EnumerateArray().Select(n => n.GetString()!).ToList();
        Assert.True(notes.Any(n => n.StartsWith("🫙 Комора під полицею") && n.Contains(Clicker.PotsShort(store))),
            $"чекали {Clicker.PotsShort(store)}: " + string.Join(" | ", notes));
        Assert.True(Pots(h) - before >= store);
    }

    [Fact]
    public void Without_night_marks_or_while_you_watch_the_store_catches_nothing()
    {
        var h = Rich();
        h.Clock.Advance(TimeSpan.FromHours(8));
        Assert.True(Act(h, "look").Ok);
        Assert.DoesNotContain(View(h).GetProperty("away").GetProperty("notes").EnumerateArray(),
            n => n.GetString()!.Contains("Комора"));

        // Біля кола (кліки щосекунди) комора мовчить: глеки ловить сам гончар.
        var g = Rich();
        Marks(g, "kiln:100");
        var passive = View(g).GetProperty("perSecond").GetDouble();
        var before = Pots(g);
        for (var i = 0; i < 30; i++)
        {
            g.Clock.Advance(1);
            Assert.True(Act(g, "look").Ok);
        }
        Assert.Equal(before + 30 * passive, Pots(g), passive * 2);
    }

    // ---------- перероблені віхи ----------

    [Fact]
    public void Reworked_marks_speak_of_minutes_of_play_and_sooner_pots_not_of_hours_of_passive()
    {
        foreach (var (up, i, m) in AllMarks)
        {
            var desc = (string)typeof(Clicker).GetMethod("MarkDesc", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [up, i])!;
            switch (m.Effect)
            {
                case MarkEffect.Merchant or MarkEffect.Eye:
                    Assert.Contains("гри", desc);
                    Assert.DoesNotContain("пасиву", desc);
                    break;
                case MarkEffect.GoldenOften or MarkEffect.FallOften:
                    Assert.Contains("частіше", desc);
                    break;
                case MarkEffect.Buff:
                    Assert.Contains("ярмарок і натхнення", desc);
                    break;
                case MarkEffect.Night:
                    Assert.Contains("комора", desc);
                    break;
            }
        }
        // Бюджет нових важелів: «частіше» не з'їдає чекання навіть разом.
        Assert.Equal(3 * Clicker.MarkGoldenOften, AllMarks.Where(x => x.M.Effect == MarkEffect.GoldenOften).Sum(x => x.M.Amount), 9);
        Assert.Equal(2 * Clicker.MarkFallOften, AllMarks.Where(x => x.M.Effect == MarkEffect.FallOften).Sum(x => x.M.Amount), 9);
        Assert.Equal(11 * Clicker.NightShelfShare, 0.33, 9);
    }

    [Fact]
    public void House_merchants_ask_at_least_half_a_minute_of_play()
    {
        var h = Rich();
        var play = ClickerPlay.Minute(h);
        Patch(h, s => s.Remove("boardUntil"));
        h.Clock.Advance(TimeSpan.FromHours(2));
        var board = View(h).GetProperty("house").GetProperty("orders").GetProperty("board");
        Assert.NotEmpty(board.EnumerateArray());
        foreach (var o in board.EnumerateArray())
        {
            var need = o.GetProperty("need").GetDouble();
            var floor = o.GetProperty("kind").GetString() == "style" ? Clicker.StylePlay : Clicker.InvestPlayMin;
            Assert.True(need >= play * floor * 0.95, $"купець просить {need}, а хвилина гри {play}");   // Nice: дві значущі цифри
        }
    }
}
