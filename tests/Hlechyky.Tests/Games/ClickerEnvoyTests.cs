using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// 👑 Посольський корабель (v13 §В, docs/games/specs/clicker-v13.md): раз на 12 год, стоїть 12 год, окремо від трьох
/// звичайних; просить 60–120 дзвінких у розписі світу (чверть — розкішні); хвилини гри ×2, Слава ×3; повний — ще +50 % Слави
/// й +25 % плати.
/// </summary>
public class ClickerEnvoyTests
{
    static RoomHarness Wheel(string nick = "Оля", params string[] styles)
    {
        var h = new RoomHarness("clicker");
        h.Solo(nick);
        Patch(h, s =>
        {
            s["pots"] = 0;                                  // з нуля: різницю черепків видно (при 1e40 вона тоне)
            s["total"] = 1e40;
            var up = Clicker.Buildings.Select(b => b.Key).TakeWhile(k => k != "pier").Append("pier");
            s["toloka"] = new JsonObject { ["built"] = new JsonArray(up.Select(x => (JsonNode)x).ToArray()) };
            s["styles"] = new JsonArray(styles.Select(x => (JsonNode)x).ToArray());
        });
        return h;
    }

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static JsonElement Trade(RoomHarness h) => h.View(0).GetProperty("trade");
    static JsonElement? Envoy(RoomHarness h) =>
        Trade(h).TryGetProperty("envoy", out var e) && e.ValueKind == JsonValueKind.Object ? e : null;       // посла нема — ключа нема
    static int Count(JsonElement e) => e.GetProperty("n").GetInt32();
    static long Slava(RoomHarness h) => Trade(h).GetProperty("slava").GetInt64();
    static double Pots(RoomHarness h) => h.View(0).GetProperty("pots").GetDouble();
    static DateTimeOffset Until(JsonElement e) => DateTimeOffset.Parse(e.GetProperty("until").GetString()!);
    static ActResult Load(RoomHarness h, int id, string what = "loadAll", int n = 5) => h.Act(0, "trade", new { @do = what, id, n });

    static void Items(RoomHarness h, params (string Key, int N)[] items) => Patch(h, s =>
    {
        var bag = new JsonObject();
        foreach (var (key, n) in items) bag[key] = n;
        s["craft"]!["items"] = bag;
    });

    static JsonObject ShipRow(RoomHarness h, int id, string ware, int quality, int count, int lux, string style, bool envoy) => new()
    {
        ["id"] = id, ["port"] = "delft", ["ware"] = ware, ["style"] = style, ["quality"] = quality, ["count"] = count, ["lux"] = lux,
        ["at"] = h.Clock.UtcNow.ToString("O"), ["until"] = h.Clock.UtcNow.AddHours(envoy ? 12 : 8).ToString("O"), ["envoy"] = envoy,
    };

    /// <summary>Пристань порожня, перший корабель відплив; у посла — наперед задане замовлення (або жодного).</summary>
    static void Row(RoomHarness h, JsonObject? ship = null, JsonObject? envoy = null, int envoyFull = 0) => Patch(h, s =>
    {
        var now = h.Clock.UtcNow;
        s["trade"] = new JsonObject
        {
            ["slava"] = 0, ["firstDone"] = true, ["next"] = now.AddMinutes(40).ToString("O"), ["shipId"] = 5,
            ["ships"] = ship is null ? new JsonArray() : new JsonArray(ship),
            ["envoy"] = envoy,
            ["envoyNext"] = now.AddHours(12).ToString("O"),
            ["envoyFull"] = envoyFull,
        };
    });

    static string HomeStyle() =>
        Clicker.Styles.First(x => !Clicker.MasterTiers.Contains(x.Key) && !Clicker.OverseasTiers.Contains(x.Key)).Key;

    // ---------- прихід і відплив ----------

    [Fact]
    public void The_envoy_comes_on_the_first_sync_and_every_twelve_hours_stays_twelve()
    {
        var h = Wheel("Оля", "athens");
        var first = Envoy(h);
        Assert.NotNull(first);
        var id = first!.Value.GetProperty("id").GetInt32();
        Assert.True(first.Value.GetProperty("envoy").GetBoolean());
        Assert.Equal(h.Clock.UtcNow + Clicker.EnvoyStay, Until(first.Value));

        h.Clock.Advance(TimeSpan.FromHours(11));
        Assert.Equal(id, Envoy(h)!.Value.GetProperty("id").GetInt32());     // ще стоїть

        h.Clock.Advance(TimeSpan.FromHours(1.01));
        var next = Envoy(h);
        Assert.NotNull(next);                                              // старий відплив, новий уже тут
        Assert.NotEqual(id, next!.Value.GetProperty("id").GetInt32());
        Assert.True(Until(next.Value) > h.Clock.UtcNow && Until(next.Value) <= h.Clock.UtcNow + Clicker.EnvoyStay);
    }

    [Fact]
    public void Two_days_offline_leave_one_freshest_envoy()
    {
        var h = Wheel("Оля", "athens");
        var start = h.Clock.UtcNow;
        var id = Envoy(h)!.Value.GetProperty("id").GetInt32();
        h.Clock.Advance(TimeSpan.FromHours(50));
        var e = Envoy(h)!.Value;
        Assert.NotEqual(id, e.GetProperty("id").GetInt32());
        // Пропущено чотири прибуття (12, 24, 36, 48 год) — лишився найсвіжіший, що ще не відплив.
        Assert.Equal(start + TimeSpan.FromHours(48) + Clicker.EnvoyStay, Until(e));
    }

    [Fact]
    public void The_envoy_does_not_take_a_place_of_the_three_ordinary_ships()
    {
        var h = Wheel("Оля", "athens");
        Row(h, envoy: ShipRow(h, 1, "", 3, 80, 0, "athens", true));
        for (var i = 0; i < 3; i++) h.Clock.Advance(Clicker.TradeGap);
        var ships = Trade(h).GetProperty("ships").EnumerateArray().ToList();
        Assert.Equal(Clicker.TradeShipsMax, ships.Count);
        Assert.All(ships, s => Assert.False(s.Flag("envoy")));
        Assert.NotNull(Envoy(h));
    }

    // ---------- запит ----------

    [Fact]
    public void The_request_is_sixty_to_a_hundred_twenty_good_world_wares_a_quarter_luxury()
    {
        var h = Wheel("Оля", "athens", "delft", HomeStyle());
        var seen = new HashSet<int>();
        for (var i = 0; i < 40; i++)
        {
            var e = Envoy(h)!.Value;
            var n = Count(e);
            Assert.InRange(n, Clicker.EnvoyMin, Clicker.EnvoyMax);
            Assert.True(e.GetProperty("q").GetInt32() >= 3);
            Assert.Equal("", e.GetProperty("ware").GetString());           // будь-який виріб
            Assert.Contains(e.GetProperty("style").GetString(), new[] { "athens", "delft" });
            Assert.Equal(n / 4, e.GetProperty("lux").GetInt32());
            Assert.Equal(0, e.GetProperty("loaded").GetInt32());
            seen.Add(n);
            h.Clock.Advance(Clicker.EnvoyGap);
        }
        Assert.True(seen.Count > 5);                                       // рівномірно, а не одне число
    }

    [Fact]
    public void Half_of_the_envoys_ask_the_style_with_the_most_free_stock()
    {
        var h = Wheel("Оля", "athens", "delft");
        Items(h, ("jug|delft|3", 90), ("jug|athens|3", 5));
        var delft = 0;
        for (var i = 0; i < 40; i++)
        {
            h.Clock.Advance(Clicker.EnvoyGap);
            if (Envoy(h)!.Value.GetProperty("style").GetString() == "delft") delft++;
        }
        // 50 % «де найбільше» + половина решти випадкових = ~75 %; з запасом на жеребкування.
        Assert.InRange(delft, 20, 39);
    }

    [Fact]
    public void Without_a_world_style_there_is_no_envoy_until_one_appears()
    {
        var h = Wheel("Оля", HomeStyle());
        Assert.Null(Envoy(h));
        h.Clock.Advance(TimeSpan.FromHours(30));
        Assert.Null(Envoy(h));
        Patch(h, s => s["styles"] = new JsonArray("celadon"));
        var e = Envoy(h);
        Assert.NotNull(e);                                                 // прийшов одразу, на повний строк
        Assert.Equal("celadon", e!.Value.GetProperty("style").GetString());
        Assert.True(Until(e.Value) - h.Clock.UtcNow >= Clicker.EnvoyStay - TimeSpan.FromMinutes(1));
    }

    // ---------- плата ----------

    /// <summary>Те саме замовлення (4 дзвінких афінських) — звичайним кораблем чи послом; повертає прибуток черепків і Слави.</summary>
    static (double Pots, long Slava, ActResult Result) LoadFour(bool envoy)
    {
        var h = Wheel("Оля", "athens");
        Items(h, ("jug|athens|3", 10));
        var row = ShipRow(h, 1, envoy ? "" : "jug", 3, 40, 0, "athens", envoy);
        if (envoy) Row(h, envoy: row); else Row(h, ship: row);
        var p0 = Pots(h);
        var s0 = Slava(h);
        var r = Load(h, 1, "load", 4);
        Assert.True(r.Ok, r.Message);
        return (Pots(h) - p0, Slava(h) - s0, r);
    }

    [Fact]
    public void Pay_is_exactly_two_and_slava_three_times_the_ordinary_ship()
    {
        var (ordPots, ordSlava, _) = LoadFour(false);
        var (envPots, envSlava, _) = LoadFour(true);
        Assert.True(ordPots > 0 && ordSlava > 0);
        Assert.Equal(ordSlava * Clicker.EnvoySlavaMul, envSlava);
        Assert.Equal(ordPots * Clicker.EnvoyMinutesMul, envPots, ordPots * 1e-9);
        // Дзвінкий афінський виріб = 2 Слави +1 за розпис; чотири — 12, у посла — 36.
        Assert.Equal(12L, ordSlava);
        Assert.Equal(36L, envSlava);
    }

    [Fact]
    public void A_full_envoy_adds_fifty_percent_slava_and_twenty_five_percent_pay_and_counts()
    {
        var h = Wheel("Оля", "athens");
        Items(h, ("jug|athens|3", 10));
        Row(h, envoy: ShipRow(h, 1, "", 3, 4, 0, "athens", true));
        var p0 = Pots(h);
        var part = Load(h, 1, "load");                                     // +5 за раз, але просить лише 4: це й є повний
        Assert.True(part.Ok, part.Message);
        // 4 виробів × 3 Слави × 3 = 36, повний +50 % = 18.
        Assert.Equal(36 + 18, Slava(h));
        Assert.Null(Envoy(h));
        Assert.Equal(1, Trade(h).GetProperty("envoysFull").GetInt32());
        Assert.Equal(JsonValueKind.String, Trade(h).GetProperty("envoyFullAt").ValueKind);
        Assert.Contains("Посольський корабель", part.Message);

        // Плата: 4 × (3 × 1,5) = 18 хв гри, ×2 = 36 хв, +25 % = 45 хв.
        var ordinary = LoadFour(false).Pots;                               // 4 виробів за звичайною ціною
        Assert.Equal(ordinary * 2 * 1.25, Pots(h) - p0, ordinary * 1e-9);
    }

    [Fact]
    public void Load_all_takes_more_than_sixty_and_pays_per_item_before_the_end()
    {
        var h = Wheel("Оля", "athens");
        Items(h, ("jug|athens|3", 110));       // з запасом: дещо тримають замовлення
        Row(h, envoy: ShipRow(h, 1, "", 3, 100, 0, "athens", true));
        var r = Load(h, 1, "load");                                        // +5: платить одразу, корабель ще стоїть
        Assert.True(r.Ok, r.Message);
        Assert.Equal(5L * 3 * 3, Slava(h));
        Assert.Equal(5, Envoy(h)!.Value.GetProperty("loaded").GetInt32());
        var all = Load(h, 1);
        Assert.True(all.Ok, all.Message);
        Assert.Null(Envoy(h));                                             // 95 решти за одну дію (стеля 120, а не 60)
        Assert.Equal(1, Trade(h).GetProperty("envoysFull").GetInt32());
    }

    [Fact]
    public void Ten_full_envoys_give_the_achievement()
    {
        var h = Wheel("Оля", "athens");
        Items(h, ("jug|athens|3", 10));
        Row(h, envoy: ShipRow(h, 1, "", 3, 2, 0, "athens", true), envoyFull: 9);
        Assert.True(Load(h, 1).Ok);
        Assert.Equal(10, Trade(h).GetProperty("envoysFull").GetInt32());
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-envoy-10");
    }

    [Fact]
    public void The_envoy_cannot_be_released_and_does_not_load_after_it_has_left()
    {
        var h = Wheel("Оля", "athens");
        var id = Envoy(h)!.Value.GetProperty("id").GetInt32();
        Assert.False(h.Act(0, "trade", new { @do = "release", id }).Ok);
        Assert.NotNull(Envoy(h));
        h.Clock.Advance(Clicker.EnvoyStay + TimeSpan.FromMinutes(1));
        Items(h, ("jug|athens|3", 10));
        Assert.False(Load(h, id).Ok);
    }

    // ---------- збереження ----------

    [Fact]
    public void The_envoy_and_counters_survive_save_and_load()
    {
        var h = Wheel("Оля", "athens");
        Items(h, ("jug|athens|3", 30));
        var id = Envoy(h)!.Value.GetProperty("id").GetInt32();
        Assert.True(Load(h, id, "load").Ok);
        var loaded = Envoy(h)!.Value.GetProperty("loaded").GetInt32();
        Assert.True(loaded > 0);
        var slava = Slava(h);
        var until = Until(Envoy(h)!.Value);

        Patch(h, _ => { });                                                // Save → Load без змін
        var e = Envoy(h)!.Value;
        Assert.Equal(id, e.GetProperty("id").GetInt32());
        Assert.Equal(loaded, e.GetProperty("loaded").GetInt32());
        Assert.Equal(until, Until(e));
        Assert.Equal(slava, Slava(h));

        Patch(h, s => { s["trade"]!["envoyFull"] = 7; s["trade"]!["envoyFullAt"] = h.Clock.UtcNow.ToString("O"); });
        Assert.Equal(7, Trade(h).GetProperty("envoysFull").GetInt32());
        Assert.Equal(JsonValueKind.String, Trade(h).GetProperty("envoyFullAt").ValueKind);
        Assert.Equal(id, Envoy(h)!.Value.GetProperty("id").GetInt32());    // посол лишився, нового не прийшло
    }
}
