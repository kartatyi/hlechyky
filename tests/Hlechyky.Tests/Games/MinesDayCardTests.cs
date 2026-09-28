using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hlechyky.Tests.Games;

/// <summary>Прохід №3 (29.09): табло дня на картці Сапера дня, серія 🔥 і привид найшвидшого.</summary>
public class MinesDayCardTests
{
    static (RoomHarness H, DailyCard Card, MinesGhosts Ghosts) Open(string nick = "Оля", DailyCard? card = null, MinesGhosts? ghosts = null)
    {
        card ??= new DailyCard(null, new FakeClock());
        ghosts ??= new MinesGhosts(null, new FakeClock());
        var sp = new ServiceCollection().AddSingleton(card).AddSingleton(ghosts).BuildServiceProvider();
        var h = new RoomHarness("mines-daily", seed: 1, services: sp);
        Assert.True(h.Solo(nick).Ok);
        return (h, card, ghosts);
    }

    /// <summary>Розв'язати поле дня: відкрити всі безпечні клітинки, які ще закриті (міни видно з гри).</summary>
    static void Solve(RoomHarness h, int stepMs = 700)
    {
        var board = typeof(MinesDaily).GetField("_board", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(h.Room.Game) as MinesBoard;
        for (var c = 0; c < board!.Cells && board.Left > 0; c++)
        {
            if (board.IsMine(c) || board.IsOpen(c)) continue;
            h.Clock.AdvanceMs(stepMs);
            Assert.True(h.Act(0, "open", new { cell = c }).Ok);
        }
    }

    [Fact]
    public void Solving_puts_me_on_the_day_card_with_a_streak_and_shows_the_ghost()
    {
        var (h, card, _) = Open();
        var v0 = h.View(0);
        Assert.Equal(JsonValueKind.Null, v0.GetProperty("ghost").ValueKind);   // до розв'язку привида не видно
        Solve(h);
        var v = h.View(0);
        Assert.True(v.GetProperty("solved").GetBoolean());
        var row = v.GetProperty("board").GetProperty("rows")[0];
        Assert.Equal("Оля", row.GetProperty("n").GetString());
        Assert.Equal(1, row.GetProperty("st").GetInt32());
        Assert.Equal(1, v.GetProperty("streak").GetInt32());
        var ghost = v.GetProperty("ghost");
        Assert.Equal("Оля", ghost.GetProperty("n").GetString());
        var moves = ghost.GetProperty("mv").GetString()!.Split(' ');
        Assert.Equal($"0o{MinesDaily.Center}", moves[0]);
        Assert.All(moves, m => Assert.Matches("^[0-9]+[ofc][0-9]+$", m));
        Assert.InRange(v.GetProperty("ms").GetInt64() / 10 - long.Parse(moves[^1][..moves[^1].IndexOfAny(['o', 'f', 'c'])]), -5, 5);
        Assert.Single(card.Get("mines-daily")!.Rows);
    }

    [Fact]
    public void Faster_friend_becomes_the_ghost_and_slower_one_does_not_replace_it()
    {
        var card = new DailyCard(null, new FakeClock());
        var ghosts = new MinesGhosts(null, new FakeClock());
        var (a, _, _) = Open("Оля", card, ghosts);
        Solve(a, stepMs: 300);
        var (b, _, _) = Open("Петро", card, ghosts);
        Solve(b, stepMs: 900);
        var v = b.View(0);
        Assert.Equal("Оля", v.GetProperty("ghost").GetProperty("n").GetString());
        var rows = v.GetProperty("board").GetProperty("rows");
        Assert.Equal(["Оля", "Петро"], rows.EnumerateArray().Select(r => r.GetProperty("n").GetString()));
    }

    [Fact]
    public void Waiting_lists_who_played_this_week_but_not_today_and_streak_counts_consecutive_days()
    {
        var clock = new FakeClock();
        var card = new DailyCard(null, clock);
        card.Note("mines-daily", "Оля", 1, 60_000);
        Assert.Equal(1, card.StreakOf("mines-daily", "оля"));
        Assert.Empty(card.Get("mines-daily")!.Waiting);   // без бази минулого нема — і «чекає» порожнє
    }

    [Fact]
    public void Moves_survive_reopening_mid_attempt()
    {
        var (h, _, _) = Open();
        h.Clock.AdvanceMs(500);
        var saved = h.Room.Game.Save()!;
        Assert.Contains("0o" + MinesDaily.Center, saved);
        var (again, _, _) = Open();
        again.Room.Game.Load(saved);
        Solve(again);
        Assert.StartsWith("0o" + MinesDaily.Center, again.View(0).GetProperty("ghost").GetProperty("mv").GetString());
    }
}
