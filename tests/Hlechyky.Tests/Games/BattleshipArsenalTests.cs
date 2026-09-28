using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Морський бій, режим «⚓ Арсенал»: крамниця за шеляги, штуки в бою, міни, ремонт і гаманець після бою.</summary>
public class BattleshipArsenalTests
{
    static readonly int[][] Blue =
        [[0, 1, 2, 3], [5, 6, 7], [20, 21, 22], [24, 25], [27, 28], [40, 41], [43], [45], [47], [49]];
    static readonly int[][] Red =
        [[0, 10, 20, 30], [2, 12, 22], [4, 14, 24], [6, 16], [8, 18], [50, 60], [52], [54], [56], [58]];

    static object Fleet(int[][] ships) => new { ships = ships.Select(s => new { cells = s }).ToArray() };
    static int[] Ints(JsonElement e) => [.. e.EnumerateArray().Select(x => x.GetInt32())];

    static RoomHarness Table(BattleshipPurse purse, string mode = "arsenal")
    {
        var h = new RoomHarness("battleship", options: new { mode }, seed: 3, services: RoomHarness.WithService(purse));
        h.Join("Оля");
        h.Join("Петро");
        h.Start();
        return h;
    }

    static RoomHarness Battle(BattleshipPurse purse, string[]? blue = null, string[]? red = null)
    {
        var h = Table(purse);
        foreach (var item in blue ?? []) Assert.True(h.Act(0, "buy", new { item }).Ok, item);
        foreach (var item in red ?? []) Assert.True(h.Act(1, "buy", new { item }).Ok, item);
        h.Act(0, "place", Fleet(Blue));
        h.Act(1, "place", Fleet(Red));
        h.Act(0, "ready");
        h.Act(1, "ready");
        return h;
    }

    static JsonElement Board(RoomHarness h, int of) => h.View(null).GetProperty("boards")[of];

    [Fact]
    public void Classic_tables_have_no_shop()
    {
        var h = Table(new BattleshipPurse(null), "classic");
        Assert.Equal("Арсенал — лише в режимі «⚓ Арсенал»", h.Act(0, "buy", new { item = "plane" }).Message);
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("arsenal").ValueKind);
    }

    [Fact]
    public void The_shop_checks_the_purse_the_hold_and_the_limits()
    {
        var h = Table(new BattleshipPurse(null));
        Assert.Equal(BattleshipArsenal.StartPurse, h.View(0).GetProperty("arsenal").GetProperty("purse").GetInt32());
        Assert.True(h.Act(0, "buy", new { item = "plane" }).Ok);
        Assert.Equal("Літак — не більше 1 на бій", h.Act(0, "buy", new { item = "plane" }).Message);
        Assert.True(h.Act(0, "buy", new { item = "torpedo" }).Ok);                        // 36 з 40
        Assert.StartsWith("Не вистачає шелягів", h.Act(0, "buy", new { item = "bomb" }).Message);
        Assert.True(h.Act(0, "buy", new { item = "radar" }).Ok is false);                  // 42 > 40
        Assert.True(h.Act(0, "sell", new { item = "torpedo" }).Ok);
        Assert.True(h.Act(0, "buy", new { item = "radar" }).Ok);
        Assert.True(h.Act(0, "buy", new { item = "radar" }).Ok);                           // 32, трюм повний
        Assert.StartsWith("У трюм влазить лише", h.Act(0, "buy", new { item = "mine" }).Message);
        Assert.Equal("Такого в арсеналі нема", h.Act(0, "buy", new { item = "кулемет" }).Message);
        h.Act(0, "random");
        h.Act(0, "ready");
        Assert.StartsWith("«Готово» вже сказано", h.Act(0, "sell", new { item = "radar" }).Message);
        // чужий трюм не видно: лише скільки штук
        var other = h.View(1).GetProperty("arsenal");
        Assert.Equal(JsonValueKind.Object, other.GetProperty("bought").ValueKind);
        Assert.Empty(other.GetProperty("bought").EnumerateObject());
        Assert.Equal(3, Ints(other.GetProperty("left"))[0]);
    }

    [Fact]
    public void A_plane_flies_the_row_from_the_nearer_edge_and_stops_at_the_first_ship()
    {
        var h = Battle(new BattleshipPurse(null), blue: ["plane"]);
        var r = h.Act(0, "use", new { item = "plane", cell = 5 });                         // рядок 1, права половина → справа
        Assert.True(r.Ok);
        Assert.Equal([9], Ints(Board(h, 1).GetProperty("misses")));
        Assert.Equal([8], Ints(Board(h, 1).GetProperty("hits")));
        Assert.Equal(0, h.View(0).GetProperty("turn").GetInt32());                          // влучив — стріляє ще
        Assert.Equal("Літак в трюмі скінчився", h.Act(0, "use", new { item = "plane", cell = 95 }).Message);
        var feed = h.View(1).GetProperty("feed");
        var last = feed[feed.GetArrayLength() - 1];
        Assert.Equal("plane", last.GetProperty("tool").GetString());
        Assert.Equal([9, 8], Ints(last.GetProperty("cells")));
    }

    [Fact]
    public void A_plane_over_empty_water_hands_the_turn_over()
    {
        var h = Battle(new BattleshipPurse(null), blue: ["plane"]);
        Assert.True(h.Act(0, "use", new { item = "plane", cell = 90 }).Ok);
        Assert.Equal(10, Ints(Board(h, 1).GetProperty("misses")).Length);
        Assert.Equal(1, h.View(0).GetProperty("turn").GetInt32());
    }

    [Fact]
    public void A_torpedo_runs_the_column_and_a_bomb_hits_a_cross()
    {
        var h = Battle(new BattleshipPurse(null), blue: ["torpedo", "bomb"]);
        Assert.True(h.Act(0, "use", new { item = "torpedo", cell = 4 }).Ok);                // стовпець e зверху: одразу корабель
        Assert.Equal([4], Ints(Board(h, 1).GetProperty("hits")));
        Assert.True(h.Act(0, "use", new { item = "bomb", cell = 11 }).Ok);                 // 11 + 1, 10, 12, 21
        Assert.Equal([4, 10, 12], Ints(Board(h, 1).GetProperty("hits")));
        Assert.Equal([1, 11, 21], Ints(Board(h, 1).GetProperty("misses")));
        Assert.Equal(0, h.View(0).GetProperty("turn").GetInt32());
    }

    [Fact]
    public void The_radar_counts_decks_for_its_owner_only()
    {
        var h = Battle(new BattleshipPurse(null), blue: ["radar"]);
        Assert.Equal("📡 У квадраті 3×3 — цілих палуб: 6", h.Act(0, "use", new { item = "radar", cell = 11 }).Message);
        Assert.Equal(1, h.View(0).GetProperty("turn").GetInt32());                          // радар — замість пострілу
        Assert.Equal([1, 11, 6], Ints(h.View(0).GetProperty("arsenal").GetProperty("intel")[0]));
        var feed = h.View(1).GetProperty("feed");
        Assert.Equal(JsonValueKind.Null, feed[feed.GetArrayLength() - 1].GetProperty("n").ValueKind);
        Assert.Empty(Ints(Board(h, 1).GetProperty("misses")));
    }

    [Fact]
    public void A_repair_patches_a_wounded_deck()
    {
        var h = Battle(new BattleshipPurse(null), blue: ["repair"]);
        h.Act(0, "shoot", new { cell = 99 });                                              // мимо → хід червоного
        h.Act(1, "shoot", new { cell = 0 });                                               // влучив у синій чотирипалубний
        h.Act(1, "shoot", new { cell = 99 });
        Assert.Equal("Латати можна лише підбиту палубу свого корабля", h.Act(0, "use", new { item = "repair", cell = 1 }).Message);
        Assert.True(h.Act(0, "use", new { item = "repair", cell = 0 }).Ok);
        Assert.Empty(Ints(Board(h, 0).GetProperty("hits")));
        Assert.Equal([0], Ints(Board(h, 0).GetProperty("patched")));
        Assert.Equal(1, h.View(0).GetProperty("turn").GetInt32());
        Assert.True(h.Act(1, "shoot", new { cell = 0 }).Ok);                               // латку можна пробити знову
        Assert.Empty(Ints(Board(h, 0).GetProperty("patched")));
    }

    [Fact]
    public void A_mine_blows_back_into_the_shooter()
    {
        var h = Battle(new BattleshipPurse(null), blue: ["mine"]);
        var mines = Ints(h.View(0).GetProperty("me").GetProperty("mines"));
        Assert.Single(mines);
        Assert.DoesNotContain("mines\":[" + mines[0], h.View(1).GetRawText());             // суперник міни не бачить
        h.Act(0, "shoot", new { cell = 99 == mines[0] ? 98 : 99 });
        var r = h.Act(1, "shoot", new { cell = mines[0] });
        Assert.StartsWith("Бабах!", r.Message);
        Assert.Equal([mines[0]], Ints(Board(h, 0).GetProperty("boom")));
        Assert.Single(Ints(Board(h, 1).GetProperty("hits")));                              // рикошет — у червоний флот
        Assert.Equal(0, h.View(0).GetProperty("turn").GetInt32());
    }

    [Fact]
    public void The_purse_pays_more_for_a_thrifty_win_and_charges_only_what_was_used()
    {
        var purse = new BattleshipPurse(null);
        var h = Battle(purse, blue: ["plane", "radar"], red: ["radar"]);
        Assert.True(h.Act(0, "use", new { item = "plane", cell = 90 }).Ok);                 // усе мимо → хід червоного
        Assert.True(h.Act(1, "use", new { item = "radar", cell = 55 }).Ok);                 // → хід синього
        foreach (var cell in Red.SelectMany(s => s)) Assert.True(h.Act(0, "shoot", new { cell }).Ok);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        // синій: витратив 20 (радар не пускав — не платить), отримав 10 + 10 + (30 − 20) = 30 → 40 + 10
        Assert.Equal(50, purse.Balance("Оля"));
        // червоний: витратив 6, отримав базу 10 → 44
        Assert.Equal(44, purse.Balance("Петро"));
        var ledger = h.View(0).GetProperty("arsenal").GetProperty("ledger");
        Assert.Equal([20, 30, 50], Ints(ledger[0]));
        Assert.Contains("шеляги: Оля +10, Петро +4", h.Room.Result!.Text);
    }

    [Fact]
    public void A_bot_with_an_arsenal_plays_to_the_end()
    {
        var h = new RoomHarness("battleship", options: new { mode = "arsenal", bots = "2", fleet = "quick" }, seed: 11,
            services: RoomHarness.WithService(new BattleshipPurse(null)));
        h.Join("Оля");
        h.Start();
        h.Act(0, "random");
        h.Act(0, "ready");
        for (var i = 0; i < 5000 && h.Room.Status == RoomStatus.Playing; i++) h.Tick(4);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
    }

    [Fact]
    public void Save_and_load_keep_the_arsenal()
    {
        var h = Battle(new BattleshipPurse(null), blue: ["mine", "radar"]);
        h.Act(0, "use", new { item = "radar", cell = 11 });
        var json = ((Battleship)h.Room.Game).Save()!;
        var copy = new Battleship();
        copy.Load(json);
        Assert.Equal(json, copy.Save());
        Assert.Contains("\"On\":true", JsonDocument.Parse(json).RootElement.GetProperty("Arsenal").GetString());
    }
}
