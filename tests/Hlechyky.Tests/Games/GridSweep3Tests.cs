using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Прохід №3: годинник ходу (№203), «Глек підсідає» в c4x (№228), пари 2×2 (№202).</summary>
public class GridSweep3Tests
{
    static int Turn(RoomHarness h) => h.View(0).GetProperty("turn").GetInt32();

    [Fact]
    public void Classic_has_no_clock_by_default()
    {
        var h = new RoomHarness("c4");
        h.Join("Оля"); h.Join("Петро");
        var v = h.View(0);
        Assert.Equal(0, v.GetProperty("clock").GetInt32());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("until").ValueKind);
        h.Clock.Advance(600);
        Assert.False(h.Act(0, "timeout").Ok);
        Assert.Equal(0, Turn(h));
    }

    [Fact]
    public void Clock_drops_a_piece_for_the_idle_player()
    {
        var h = new RoomHarness("c4", new { clock = "20" });
        h.Join("Оля"); h.Join("Петро");
        Assert.Equal(JsonValueKind.String, h.View(0).GetProperty("until").ValueKind);
        h.Clock.Advance(19);
        Assert.False(h.Act(1, "timeout").Ok);               // ще є час — ніхто за Олю не ходить
        h.Clock.Advance(1.5);
        Assert.True(h.Act(1, "timeout").Ok);                // штовхнув будь-хто з гравців
        var v = h.View(0);
        Assert.Equal(1, v.GetProperty("turn").GetInt32());
        Assert.True(v.GetProperty("auto").GetBoolean());
        Assert.Equal(1, v.GetProperty("cells").EnumerateArray().Count(c => c.ValueKind == JsonValueKind.String));
        // Годинник Петра пішов заново.
        Assert.False(h.Act(0, "timeout").Ok);
    }

    [Fact]
    public void Party_needs_three_unless_bots_come()
    {
        var h = new RoomHarness("c4x");
        h.Join("Оля"); h.Join("Петро");
        Assert.False(h.Start().Ok);

        var b = new RoomHarness("c4x", new { bots = "2" });
        b.Join("Оля");
        Assert.True(b.Start().Ok);
        var v = b.View(0);
        var bots = v.GetProperty("bots");
        Assert.Equal(JsonValueKind.Null, bots[0].ValueKind);
        Assert.Contains("🤖", bots[1].GetString());
        Assert.Contains("🤖", bots[2].GetString());
        Assert.Equal(9, v.GetProperty("width").GetInt32());   // утрьох — 9×7
    }

    [Fact]
    public void Bot_moves_only_after_thinking_and_the_game_reaches_an_end()
    {
        var h = new RoomHarness("c4x", new { bots = "3" }, seed: 7);
        h.Join("Оля");
        Assert.True(h.Start().Ok);
        var rng = new Random(3);
        for (var step = 0; step < 200 && h.Room.Status == RoomStatus.Playing; step++)
        {
            var turn = Turn(h);
            if (turn == 0)
            {
                var w = h.View(0).GetProperty("width").GetInt32();
                while (!h.Act(0, "move", new { cell = rng.Next(w) }).Ok) { }
                continue;
            }
            Assert.False(h.Act(0, BoardBots.Nudge).Ok);           // ще думає
            h.Clock.AdvanceMs(BoardBots.ThinkMs);
            Assert.True(h.Act(0, BoardBots.Nudge).Ok);
            Assert.NotEqual(turn, h.Room.Status == RoomStatus.Playing ? Turn(h) : -1);
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        // Глек у каркасі не сидить: нагород і серії він не бере.
        Assert.All(h.Finished.Single().Seats.Skip(1), s => Assert.Null(s));
    }

    [Fact]
    public void Human_cannot_move_for_a_bot()
    {
        var h = new RoomHarness("c4x", new { bots = "2" });
        h.Join("Оля");
        h.Start();
        h.Act(0, "move", new { cell = 4 });
        Assert.Equal(1, Turn(h));
        Assert.False(h.Act(0, "move", new { cell = 4 }).Ok);
    }

    [Fact]
    public void Bot_wins_when_it_can_and_blocks_the_next_player()
    {
        var rules = new GridRules(7, 6, 4, true);
        var cells = new string?[rules.Cells];
        // У «o» три в ряд унизу (колонки 0–2) — Глек «o» добирає четверту.
        for (var x = 0; x < 3; x++) cells[5 * 7 + x] = "o";
        Assert.Equal(3, GridBot.Pick(new Random(1), cells, rules, "o", "o", ["x"]));
        // Тепер «o» — суперник: Глек «x» мусить закрити колонку 3.
        Assert.Equal(3, GridBot.Pick(new Random(1), cells, rules, "x", "x", ["o"]));
    }

    [Fact]
    public void Pairs_count_the_partner_pieces_and_both_win()
    {
        var h = new RoomHarness("c4x", new { teams = "1" });
        foreach (var n in new[] { "Оля", "Петро", "Марко", "Ганна" }) h.Join(n);
        Assert.True(h.Start().Ok);
        Assert.True(h.View(0).GetProperty("pairs").GetBoolean());
        // Поле 10×8. Оля (0) і Марко (2) — пара; кладуть по черзі в колонки 0,1 (Оля) і 2,3 (Марко).
        int[][] moves = [[0, 9, 2, 8], [1, 9, 3]];
        foreach (var round in moves)
            for (var s = 0; s < round.Length; s++) Assert.True(h.Act(s, "move", new { cell = round[s] }).Ok);
        // Низ: Оля 0,1 · Марко 2,3 — четвірка з фішок обох напарників, зібрана ходом Марка.
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var won = h.Finished.Single().Result.Winners;
        Assert.Equal([0, 2], won);
    }

    [Fact]
    public void Pairs_need_exactly_four()
    {
        var h = new RoomHarness("c4x", new { teams = "1" });
        foreach (var n in new[] { "Оля", "Петро", "Марко" }) h.Join(n);
        Assert.False(h.Start().Ok);
        var b = new RoomHarness("c4x", new { teams = "1", bots = "1" });
        foreach (var n in new[] { "Оля", "Петро", "Марко" }) b.Join(n);
        Assert.True(b.Start().Ok);
        Assert.True(b.View(0).GetProperty("pairs").GetBoolean());
    }

    [Fact]
    public void Pair_survives_while_one_partner_stays()
    {
        var h = new RoomHarness("c4x", new { teams = "1" });
        foreach (var n in new[] { "Оля", "Петро", "Марко", "Ганна" }) h.Join(n);
        h.Start();
        Assert.True(h.Act(0, "resign").Ok);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);   // Марко грає за пару далі
        Assert.True(h.Act(2, "resign").Ok);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1, 3], h.Finished.Single().Result.Winners);
    }

    [Fact]
    public void Last_human_resigning_ends_the_bot_game()
    {
        var h = new RoomHarness("c4x", new { bots = "2" });
        h.Join("Оля");
        h.Start();
        Assert.True(h.Act(0, "resign").Ok);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
    }
}
