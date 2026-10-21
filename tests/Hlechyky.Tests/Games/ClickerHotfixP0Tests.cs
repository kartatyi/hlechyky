using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Хотфікс P0 кола (плейтест 21.10.2026, docs/games/specs/clicker-v12-ux.md §1, B1–B4): горно відкриває вікно, де
/// палили; «Ремесло» не ховається під час першого горна; здача не забирає чужого відкладеного; обпал лишає глину й
/// каже, що згорить.
/// </summary>
public class ClickerHotfixP0Tests
{
    static readonly DateTimeOffset Thursday = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    static RoomHarness Wheel(int seed = 1)
    {
        var h = new RoomHarness("clicker", seed: seed);
        h.Clock.UtcNow = Thursday;
        h.Solo("Оля");
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static JsonElement Kiln(RoomHarness h) => View(h).GetProperty("kiln");
    static JsonElement Craft(RoomHarness h) => View(h).GetProperty("craft");
    static string State(RoomHarness h) => Kiln(h).GetProperty("state").GetString()!;
    static ActResult Act(RoomHarness h, string action, object? payload = null) => h.Act(0, action, payload);
    static ActResult K(RoomHarness h, object payload) => Act(h, "kiln", payload);
    static ActResult Fair(RoomHarness h, object payload) => Act(h, "fair", payload);

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static void Rack(RoomHarness h, int dry) => Patch(h, s =>
    {
        var rack = new JsonArray();
        for (var i = 0; i < dry; i++)
            rack.Add(new JsonObject { ["ware"] = "pot", ["clay"] = "", ["dryAt"] = h.Clock.UtcNow.AddMinutes(-1).ToString("O") });
        s["craft"]!["rack"] = rack;
    });

    static void Items(RoomHarness h, params (string Key, int N)[] items) => Patch(h, s =>
    {
        var bag = new JsonObject();
        foreach (var (key, n) in items) bag[key] = n;
        s["craft"]!["items"] = bag;
    });

    static int N(RoomHarness h, string key) =>
        Craft(h).GetProperty("items").EnumerateArray().Where(i => i.GetProperty("key").GetString() == key).Sum(i => i.GetProperty("n").GetInt32());

    static JsonObject Order(RoomHarness h, int id, string ware, int quality, int count, string style = "") => new()
    {
        ["id"] = id, ["village"] = "opishnia", ["who"] = 0, ["ware"] = ware, ["style"] = style, ["quality"] = quality,
        ["count"] = count, ["mult"] = 2, ["at"] = h.Clock.UtcNow.ToString("O"),
        ["until"] = (h.Clock.UtcNow + TimeSpan.FromMinutes(30)).ToString("O"), ["lord"] = false, ["sour"] = false,
    };

    static void Board(RoomHarness h, params JsonObject[] orders) => Patch(h, s =>
    {
        var fair = s["fair"]!.AsObject();
        fair["orders"] = new JsonArray(orders.Select(o => (JsonNode)o).ToArray());
        fair["orderNext"] = (h.Clock.UtcNow + TimeSpan.FromHours(5)).ToString("O");
    });

    static JsonElement? OrderView(RoomHarness h, int id) =>
        View(h).GetProperty("market").GetProperty("orders").EnumerateArray().Cast<JsonElement?>()
            .FirstOrDefault(o => o!.Value.GetProperty("id").GetInt32() == id);

    static object Open(IEnumerable<(int Ms, int Act)> acts, bool? own = null) => own is { } o
        ? new { op = "open", t = acts.Select(a => new[] { a.Ms, a.Act }).ToArray(), own = o }
        : new { op = "open", t = acts.Select(a => new[] { a.Ms, a.Act }).ToArray() };

    // ---------- B1: горно відкриває вікно, де палили ----------

    [Fact]
    public void B1_an_empty_record_from_another_window_waits_and_the_window_that_burned_counts()
    {
        var h = Wheel();
        Rack(h, 6);
        Assert.True(K(h, new { op = "light" }).Ok);
        var seed = Kiln(h).GetProperty("seed").GetInt32();
        h.Clock.Advance(Clicker.KilnBurn + TimeSpan.FromSeconds(0.4));

        // Ноутбук, де не грали, прийшов першим із порожнім записом — горно не відкривається, жар не губиться.
        var other = K(h, Open([]));
        Assert.False(other.Ok);
        Assert.StartsWith("⏳ Горно відкриє вікно, де ти палив", other.Message);
        Assert.Equal("burning", State(h));

        // Телефон, де палили, — зі своїм записом: рахується саме він.
        var acts = ClickerKilnTests.Stoker(seed);
        var mine = K(h, Open(acts));
        Assert.True(mine.Ok, mine.Message);
        var heat = Kiln(h).GetProperty("last").GetProperty("heat").GetInt32();
        Assert.Equal((int)Math.Round(KilnHeat.Run(seed, acts).Heat * 100), heat);
        Assert.True(heat > 50, $"умілий палій тримав жар у смузі, а вийшло {heat} %");
    }

    [Fact]
    public void B1_the_window_that_lit_the_kiln_waits_a_few_seconds_with_an_empty_record()
    {
        var h = Wheel();
        Rack(h, 6);
        Assert.True(K(h, new { op = "light" }).Ok);
        h.Clock.Advance(Clicker.KilnBurn + TimeSpan.FromSeconds(0.4));
        // v12, QA-2: жар могли тримати на іншому пристрої — його запис приходить на якісь 190 мс пізніше.
        var r = K(h, Open([], own: true));
        Assert.False(r.Ok);
        Assert.StartsWith("⏳ Чекаю, чи не тримав ти жар на іншому пристрої", r.Message);
        Assert.Equal("burning", State(h));
        h.Clock.Advance(Clicker.KilnOwnWait);
        r = K(h, Open([], own: true));
        Assert.True(r.Ok, r.Message);
        Assert.Equal("cooling", State(h));
    }

    [Fact]
    public void B1_if_the_window_that_burned_is_gone_the_other_one_opens_after_the_wait()
    {
        var h = Wheel();
        Rack(h, 6);
        Assert.True(K(h, new { op = "light" }).Ok);
        h.Clock.Advance(Clicker.KilnBurn + Clicker.KilnOtherWait - TimeSpan.FromSeconds(1));
        Assert.StartsWith("⏳", K(h, Open([])).Message);
        h.Clock.Advance(TimeSpan.FromSeconds(1.1));
        Assert.True(K(h, Open([])).Ok);                     // запис пропав разом із вікном — горно однаково не стоїть
    }

    [Fact]
    public void B1_the_client_marks_its_own_burn_and_waits_for_the_other_window()
    {
        var kiln = File.ReadAllText(Hlechyky.Paths.Resolve("web/games/clicker-kiln.js"));
        var craft = File.ReadAllText(Hlechyky.Paths.Resolve("web/games/clicker-craft.js"));
        Assert.Contains("own: kb.own", kiln);                     // open шле, чи розпалювали звідси
        Assert.Contains("otherWait(st)", kiln);                   // вікно без дій чекає на те, де палили
        Assert.Contains("bestActs(st, kb)", kiln);                // друга вкладка того ж пристрою бере запис із localStorage
        Assert.Contains("st.kLitPress = Date.now()", craft);      // «Обпалити» позначає своє вікно
        Assert.Equal(Clicker.KilnOtherWait.TotalMilliseconds,
            View(Wheel()).GetProperty("catalog").GetProperty("kiln").GetProperty("otherWaitMs").GetDouble());
    }

    // ---------- B2: «Ремесло» не ховається, поки в горні партія ----------

    [Fact]
    public void B2_the_first_kiln_takes_the_whole_rack_and_the_view_still_shows_a_batch()
    {
        var h = Wheel();
        Rack(h, 2);
        Assert.True(K(h, new { op = "light" }).Ok);
        // Те, з чого клієнт вирішує, чи є «Ремесло»: сушарня порожня, обпаленого й комори нема — лишається горно.
        Assert.Empty(Craft(h).GetProperty("rack").EnumerateArray());
        Assert.Equal(0, Craft(h).GetProperty("fired").GetInt32());
        Assert.Empty(Craft(h).GetProperty("items").EnumerateArray());
        Assert.Equal("burning", State(h));
        Assert.Equal(2, Kiln(h).GetProperty("batch").GetArrayLength());
    }

    [Fact]
    public void B2_the_craft_tab_gate_counts_the_kiln()
    {
        var craft = File.ReadAllText(Hlechyky.Paths.Resolve("web/games/clicker-craft.js"));
        var at = craft.IndexOf("api.showWhen(st, CRAFT_TAB", StringComparison.Ordinal);
        Assert.True(at > 0, "нема гейта вкладки «Ремесло»");
        var gate = craft[at..craft.IndexOf("});", at, StringComparison.Ordinal)];
        Assert.Contains("k.batch.length > 0", gate);
        Assert.Contains("k.state === 'burning'", gate);
    }

    // ---------- B3: здача спершу бере невідкладене ----------

    [Fact]
    public void B3_delivering_one_order_leaves_the_painted_ones_another_order_waits_for()
    {
        var h = Wheel();
        Patch(h, s => s["total"] = 1_000_000);
        // А — 5 бубнівських баранців; Б — 6 баранців «добрі й кращі». Бубнівські — добрі, прості — дзвінкі: раніше Б
        // брав найгірших, тобто бубнівські, і А лишалось «є 0 з 5».
        Board(h, Order(h, 100, "ram", 1, 5, "bubnivka"), Order(h, 101, "ram", 2, 6));
        Items(h, ("ram|bubnivka|2", 5), ("ram||3", 6));
        Assert.True(Fair(h, new { op = "deliver", id = 101 }).Ok);
        Assert.Equal(5, N(h, "ram|bubnivka|2"));
        Assert.Equal(0, N(h, "ram||3"));
        Assert.Equal(5, OrderView(h, 100)!.Value.GetProperty("have").GetInt32());
        Assert.True(Fair(h, new { op = "deliver", id = 100 }).Ok);
    }

    [Fact]
    public void B3_the_pay_is_for_what_really_went()
    {
        var h = Wheel();
        Patch(h, s => { s["total"] = 1_000_000; s["pots"] = 0; });
        Board(h, Order(h, 100, "ram", 1, 5, "bubnivka"), Order(h, 101, "ram", 2, 6));
        Items(h, ("ram|bubnivka|2", 5), ("ram||3", 6));
        var promised = OrderView(h, 101)!.Value.GetProperty("pay").GetDouble();
        Assert.True(Fair(h, new { op = "deliver", id = 101 }).Ok);
        // Віддали дзвінкі, а не добрі, — і заплатили за дзвінкі: не менше, ніж обіцяли за добрі.
        Assert.True(View(h).GetProperty("pots").GetDouble() >= promised);
    }

    [Fact]
    public void B3_what_is_kept_goes_only_when_nothing_else_is_left()
    {
        var h = Wheel();
        Patch(h, s => s["total"] = 1_000_000);
        Board(h, Order(h, 100, "ram", 1, 5, "bubnivka"), Order(h, 101, "ram", 2, 6));
        Items(h, ("ram|bubnivka|2", 5), ("ram||3", 2));
        Assert.True(Fair(h, new { op = "deliver", id = 101 }).Ok);
        // Простих лише два — решту чотири дали бубнівські: замовлення, яке здаєш, важливіше за те, що чекає.
        Assert.Equal(0, N(h, "ram||3"));
        Assert.Equal(1, N(h, "ram|bubnivka|2"));
    }

    [Fact]
    public void B3_laying_the_toloka_leaves_the_painted_pots_a_village_waits_for()
    {
        var h = Wheel();
        // Толока відкрита, криниця, етап 0: 15 горщиків і 15 мисок. Село чекає 5 бубнівських горщиків — і вони звичайні,
        // як і прості: «найгірші» забрали б їх на криницю, хоч добрих простих вистачає.
        Patch(h, s =>
        {
            s["pots"] = 3e15;
            s["total"] = 3e15;
            s["toloka"] = new JsonObject { ["built"] = new JsonArray(), ["stage"] = 0, ["helpers"] = new JsonArray() };
        });
        Board(h, Order(h, 100, "pot", 1, 5, "bubnivka"));
        Items(h, ("pot||1", 10), ("pot||2", 5), ("pot|bubnivka|1", 5), ("bowl||1", 15));
        var r = Act(h, "toloka", new { @do = "lay" });
        Assert.True(r.Ok, r.Message);
        Assert.Equal(5, N(h, "pot|bubnivka|1"));
        Assert.Equal(0, N(h, "pot||1"));
        Assert.Equal(0, N(h, "pot||2"));
    }

    [Fact]
    public void B3_when_nobody_loses_the_worst_go_first_as_before()
    {
        var h = Wheel();
        Patch(h, s => s["total"] = 1_000_000);
        // Обидва замовлення беруть будь-які баранці: хоч як розклади — кожне збереться. Тоді, як і раніше, віддаємо
        // найгірших, а дорожчі лишаються тому, хто за них заплатить.
        Board(h, Order(h, 100, "ram", 1, 5), Order(h, 101, "ram", 1, 5));
        Items(h, ("ram||1", 5), ("ram||3", 5));
        Assert.True(Fair(h, new { op = "deliver", id = 101 }).Ok);
        Assert.Equal(0, N(h, "ram||1"));
        Assert.Equal(5, N(h, "ram||3"));
    }

    // ---------- B4: обпал лишає глину й каже, що згорить ----------

    [Fact]
    public void B4_the_clay_on_the_wheel_survives_the_firing()
    {
        var h = Wheel();
        Patch(h, s => { s["pots"] = 10_000; s["total"] = Clicker.TotalFor(1); });
        Assert.True(Act(h, "knead", new { kind = "red" }).Ok);
        Assert.True(Act(h, "fire").Ok);
        Assert.Equal("red", View(h).GetProperty("house").GetProperty("clay").GetString());
        // Відлежування не скидається: обпал — не привід перемикати глину щохвилини.
        Assert.StartsWith("Глина ще відлежується", Act(h, "knead", new { kind = "" }).Message);
    }

    [Fact]
    public void B4_the_view_tells_how_many_pots_survive_and_whether_the_marks_burn()
    {
        var h = Wheel();
        Assert.Equal(0, View(h).GetProperty("fireKeep").GetDouble());
        Assert.True(View(h).GetProperty("fireMarks").GetBoolean());
        Patch(h, s => s["secrets"] = new JsonArray("ashes", "memory"));
        Assert.Equal(Clicker.AshesShare, View(h).GetProperty("fireKeep").GetDouble(), 6);
        Assert.False(View(h).GetProperty("fireMarks").GetBoolean());
    }

    [Fact]
    public void B4_the_second_press_lists_what_burns()
    {
        var js = File.ReadAllText(Hlechyky.Paths.Resolve("web/games/clicker.js")).Replace("\r\n", "\n");
        var at = js.IndexOf("function fireLoss(st)", StringComparison.Ordinal);
        Assert.True(at > 0, "нема переліку «що згорить»");
        var body = js[at..js.IndexOf("\n  }\n", at, StringComparison.Ordinal)];
        foreach (var what in new[] { "купці в дорозі", "не привезуть", "на сушарні", "у горні", "замовлення сіл", "комора", "Лишиться:" })
            Assert.Contains(what, body);
        Assert.Contains("swap(f._loss, armed ? fireLoss(st) : '')", js);
    }
}
