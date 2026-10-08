using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Записка №29: глек чекає мінігру (розпис технікою, ручний обпал) і чесне «трісь» — вид наперед каже, що станеться з
/// серією, якщо глек розіб'ється (ClickerFallHold.cs).
/// </summary>
public class ClickerFallHoldTests
{
    static RoomHarness Wheel()
    {
        var h = new RoomHarness("clicker");
        h.Solo("Оля");
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static JsonElement Fall(RoomHarness h) => View(h).GetProperty("fall");
    static DateTimeOffset FallAt(RoomHarness h) => Fall(h).GetProperty("at").GetDateTimeOffset();
    static DateTimeOffset FallUntil(RoomHarness h) => Fall(h).GetProperty("until").GetDateTimeOffset();
    static DateTimeOffset GoldenAt(RoomHarness h) => View(h).GetProperty("golden").GetProperty("at").GetDateTimeOffset();
    static int Streak(RoomHarness h) => Fall(h).GetProperty("streak").GetInt32();
    static string Miss(RoomHarness h) => Fall(h).GetProperty("miss").GetString()!;
    static ActResult Act(RoomHarness h, string action, object? payload = null) => h.Act(0, action, payload);
    static ActResult K(RoomHarness h, object payload) => Act(h, "kiln", payload);

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    /// <summary>Глек з полиці злетить за <paramref name="seconds"/> с від «зараз».</summary>
    static void FallIn(RoomHarness h, double seconds) => Patch(h, s =>
    {
        var at = h.Clock.UtcNow.AddSeconds(seconds);
        s["fall"] = new JsonObject { ["at"] = at.ToString("O"), ["until"] = (at + Clicker.FallShown).ToString("O"), ["x"] = 40 };
    });

    static void GoldenIn(RoomHarness h, double seconds) => Patch(h, s =>
    {
        var at = h.Clock.UtcNow.AddSeconds(seconds);
        s["golden"] = new JsonObject
        {
            ["at"] = at.ToString("O"), ["until"] = (at + Clicker.GoldenShown).ToString("O"),
            ["kind"] = (int)Clicker.GoldenKind.Merchant, ["x"] = 10, ["y"] = 10,
        };
    });

    static void DryRack(RoomHarness h, int n) => Patch(h, s =>
    {
        var rack = new JsonArray();
        for (var i = 0; i < n; i++)
            rack.Add(new JsonObject { ["ware"] = "pot", ["clay"] = "", ["dryAt"] = h.Clock.UtcNow.AddMinutes(-1).ToString("O") });
        s["craft"]!["rack"] = rack;
    });

    static void Near(DateTimeOffset want, DateTimeOffset got) => Assert.InRange((got - want).TotalMilliseconds, -1, 1);

    [Fact]
    public void Painting_holds_the_fall_and_the_golden_jug_until_it_ends()
    {
        var h = Wheel();
        FallIn(h, 10);
        GoldenIn(h, 20);
        var shown = FallUntil(h) - FallAt(h);
        var t0 = h.Clock.UtcNow;
        Assert.True(K(h, new { op = "paint", style = "", tech = "rizh" }).Ok);
        // Розпис іще не здано — глеки чекають до стелі мінігри й ще півтори секунди.
        Near(t0 + Clicker.MinigameCap + Clicker.MinigameAfter, FallAt(h));
        Assert.Equal(shown, FallUntil(h) - FallAt(h));          // летить стільки ж, скільки й мав
        Near(t0 + Clicker.MinigameCap + Clicker.MinigameAfter, GoldenAt(h));

        // Розпис скінчився (обрали розпис без техніки): обидва вертаються — глек за півтори секунди, бо його час минув,
        // а розписний — до свого часу, що ще не настав.
        h.Clock.Advance(15);
        Assert.True(K(h, new { op = "paint", style = "" }).Ok);
        Near(h.Clock.UtcNow + Clicker.MinigameAfter, FallAt(h));
        Near(t0.AddSeconds(20), GoldenAt(h));
    }

    [Fact]
    public void A_fall_planned_after_the_minigame_keeps_its_time()
    {
        var h = Wheel();
        FallIn(h, 40);
        var planned = FallAt(h);
        Assert.True(K(h, new { op = "paint", style = "", tech = "rizh" }).Ok);
        h.Clock.Advance(5);
        Assert.True(K(h, new { op = "paint", style = "" }).Ok);
        Near(planned, FallAt(h));
    }

    [Fact]
    public void An_unfinished_painting_holds_jugs_no_longer_than_the_cap()
    {
        var h = Wheel();
        FallIn(h, 10);
        var t0 = h.Clock.UtcNow;
        Assert.True(K(h, new { op = "paint", style = "", tech = "rizh" }).Ok);
        // Закрив вікно й пішов клацати коло: візерунок живе п'ять хвилин, але глеки — лише до стелі.
        h.Clock.Advance(Clicker.MinigameCap + Clicker.MinigameAfter + TimeSpan.FromSeconds(0.5));
        Act(h, "look");                                          // будь-яка дія синхронізує
        Near(t0 + Clicker.MinigameCap + Clicker.MinigameAfter, FallAt(h));
        Assert.True(Act(h, "grab").Ok);
        // Новий розпис після стелі знову тримає — але знову лише свої 90 с.
        var t1 = h.Clock.UtcNow;
        FallIn(h, 3);
        Assert.True(K(h, new { op = "paint", style = "", tech = "rizh" }).Ok);
        Near(t1 + Clicker.MinigameCap + Clicker.MinigameAfter, FallAt(h));
    }

    [Fact]
    public void A_fall_already_flying_when_the_minigame_starts_keeps_flying()
    {
        var h = Wheel();
        FallIn(h, 0.5);                                          // у запасі на пінг — клієнт уже міг його намалювати
        var at = FallAt(h);
        Assert.True(K(h, new { op = "paint", style = "", tech = "rizh" }).Ok);
        Near(at, FallAt(h));
        h.Clock.Advance(1);
        Assert.True(Act(h, "grab").Ok);                          // ловиться поверх мінігри, як і раніше
        Assert.Equal(1, Streak(h));
    }

    [Fact]
    public void Manual_firing_holds_the_fall_but_the_stoker_does_not()
    {
        var h = Wheel();
        DryRack(h, 3);
        FallIn(h, 5);
        var lit = h.Clock.UtcNow;
        Assert.True(K(h, new { op = "light" }).Ok);
        Near(lit + Clicker.KilnBurn + Clicker.MinigameAfter, FallAt(h));
        // Обпал скінчився сам (горно ще не відкрили) — глек летить рівно тоді, як обіцяв.
        h.Clock.Advance(Clicker.KilnBurn + TimeSpan.FromSeconds(1.6));
        Assert.True(Act(h, "grab").Ok);

        var h2 = Wheel();
        DryRack(h2, 3);
        FallIn(h2, 5);
        var at = FallAt(h2);
        Assert.True(K(h2, new { op = "light", helper = true }).Ok);
        Near(at, FallAt(h2));
    }

    [Fact]
    public void Opening_the_kiln_early_does_not_pull_the_fall_before_its_time()
    {
        var h = Wheel();
        DryRack(h, 3);
        FallIn(h, 5);
        var lit = h.Clock.UtcNow;
        Assert.True(K(h, new { op = "light" }).Ok);
        h.Clock.Advance(Clicker.KilnBurn + TimeSpan.FromSeconds(0.2));
        var r = K(h, new { op = "open", t = Array.Empty<int[]>() });
        Assert.True(r.Ok, r.Message);
        // Відкладений глек не стрибає ні раніше за «зараз + 1,5 с», ні назад до свого старого часу.
        Assert.True(FallAt(h) >= lit + Clicker.KilnBurn + Clicker.MinigameAfter - TimeSpan.FromMilliseconds(1));
        Assert.True(FallAt(h) >= h.Clock.UtcNow + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Restart_in_the_middle_of_a_minigame_still_holds_from_the_saved_kiln()
    {
        var h = Wheel();
        Assert.True(K(h, new { op = "paint", style = "", tech = "rizh" }).Ok);
        var save = h.Room.Game.Save()!;

        // Новий сервер: те саме збереження, «пам'яті» про відкладення нема — розклад відкладається знову з горна.
        var h2 = Wheel();
        h2.Clock.Advance(h.Clock.UtcNow - h2.Clock.UtcNow);
        lock (h2.Room.Sync) h2.Room.Game.Load(save);
        FallIn(h2, 3);
        var t = h2.Clock.UtcNow;
        Act(h2, "look");
        Assert.True(FallAt(h2) >= t + TimeSpan.FromSeconds(80));
    }

    [Fact]
    public void An_old_save_without_a_painting_or_fire_loads_and_drops_jugs_as_before()
    {
        var h = Wheel();
        Patch(h, s => { s.Remove("kiln"); s.Remove("fall"); s.Remove("golden"); });
        var at = FallAt(h);
        Assert.InRange((at - h.Clock.UtcNow).TotalSeconds, Clicker.CatMinSeconds * 0.5, Clicker.FallMaxSeconds * 2);
        Assert.Equal("none", Miss(h));
    }

    // ---------- чесне «трісь» ----------

    [Fact]
    public void The_view_says_what_a_broken_jug_will_cost_and_it_does()
    {
        var past = Clicker.FallShown + Clicker.CatchGrace + TimeSpan.FromSeconds(0.5);

        // Серії нема — просто «трісь».
        var h = Wheel();
        Assert.Equal("none", Miss(h));

        // Серія без фартуха — обірветься.
        Patch(h, s => s["fallStreak"] = 4);
        Assert.Equal("streak", Miss(h));
        FallIn(h, 0);
        h.Clock.Advance(past);
        Assert.False(Act(h, "grab").Ok);
        Assert.Equal(0, Streak(h));
        Assert.Equal("none", Miss(h));

        // Фартух: перший раз уберігає, другий поспіль — уже ні, і вид це каже заздалегідь.
        Patch(h, s => { s["pots"] = 1e7; s["total"] = 1e7; });
        Assert.True(Act(h, "tool", new { key = "apron" }).Ok);
        Patch(h, s => s["fallStreak"] = 4);
        Assert.Equal("apron", Miss(h));
        FallIn(h, 0);
        h.Clock.Advance(past);
        Assert.False(Act(h, "grab").Ok);
        Assert.Equal(4, Streak(h));
        Assert.Equal("streak", Miss(h));
        FallIn(h, 0);
        h.Clock.Advance(past);
        Assert.False(Act(h, "grab").Ok);
        Assert.Equal(0, Streak(h));
    }
}
