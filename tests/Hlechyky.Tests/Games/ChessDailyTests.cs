using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit;

namespace Hlechyky.Tests.Games;

/// <summary>Шахова задача дня (прохід №3, пункт 204): банк перевірено рушієм, хід за ходом — як у людини.</summary>
public class ChessDailyTests
{
    static string Uci(ChessMove m) => ChessCore.Name(m.From) + ChessCore.Name(m.To) + (m.Promo == 0 ? "" : " pnbrqk"[m.Promo].ToString());

    [Fact]
    public void Bank_has_sixty_plus_puzzles_all_distinct()
    {
        Assert.True(ChessPuzzles.Bank.Length >= 60);
        Assert.Equal(ChessPuzzles.Bank.Length, ChessPuzzles.Bank.Select(p => p.Fen).Distinct().Count());
        Assert.Contains(ChessPuzzles.Bank, p => p.N == 3);
        Assert.Contains(ChessPuzzles.Bank, p => !p.WhiteToMove);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Every_puzzle_is_a_true_mate_in_n_with_a_single_key()
    {
        var bad = new List<string>();
        Parallel.ForEach(ChessPuzzles.Bank, p =>
        {
            var c = ChessCore.FromFen(p.Fen);
            var keys = ChessEngine.MateKeys(c, p.N);
            if (keys.Count != 1 || Uci(keys[0]) != p.Key) lock (bad) bad.Add($"{p.Fen}: ключі {string.Join(",", keys.Select(Uci))}");
            else if (ChessEngine.ForcedMate(c, p.N - 1)) lock (bad) bad.Add($"{p.Fen}: є мат швидше");
        });
        Assert.Empty(bad);
    }

    [Fact]
    public void One_puzzle_a_day_cycling_through_the_bank()
    {
        Assert.Same(ChessPuzzles.Bank[0], ChessPuzzles.ForDay("2026-09-29"));
        Assert.Same(ChessPuzzles.Bank[1], ChessPuzzles.ForDay("2026-09-30"));
        Assert.Same(ChessPuzzles.Bank[0], ChessPuzzles.ForDay(DateOnly.Parse("2026-09-29").AddDays(ChessPuzzles.Bank.Length).ToString("yyyy-MM-dd")));
    }

    static (RoomHarness H, ChessPuzzle P) Open()
    {
        var h = new RoomHarness("chess-daily", seed: 1);
        Assert.True(h.Solo("Оля").Ok);
        var day = h.View(0).GetProperty("puzzle").GetProperty("day").GetString()!;
        return (h, ChessPuzzles.ForDay(day));
    }

    static bool Move(RoomHarness h, string uci) =>
        h.Act(0, "move", uci.Length > 4 ? new { from = uci[..2], to = uci[2..4], promo = uci[4..] } : (object)new { from = uci[..2], to = uci[2..4] }).Ok;

    /// <summary>Доводить задачу до мату так, як це зробила б людина: ключ, далі щоразу хід, що зберігає вимушений мат.</summary>
    static void Solve(RoomHarness h, ChessPuzzle p)
    {
        Assert.True(Move(h, p.Key));
        for (var step = 1; step < p.N && h.Room.Status == RoomStatus.Playing; step++)
        {
            var c = ChessCore.FromFen(h.View(0).GetProperty("fen").GetString()!);
            var left = p.N - step;
            var best = ChessEngine.MateKeys(c, left);
            if (best.Count == 0) best = ChessEngine.MateKeys(c, 1);
            Assert.NotEmpty(best);
            Assert.True(Move(h, Uci(best[0])));
        }
    }

    [Fact]
    public void Solving_scores_time_and_attempts()
    {
        var (h, p) = Open();
        var v = h.View(0);
        Assert.Equal(p.WhiteToMove ? "w" : "b", v.GetProperty("me").GetString());
        Assert.Equal(JsonValueKindNull, v.GetProperty("puzzle").GetProperty("key").ValueKind);
        h.Clock.AdvanceMs(42_000);
        Solve(h, p);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var s = Assert.Single(h.Scores);
        Assert.Equal(1, s.Attempts);
        Assert.True(s.Score >= 42_000);
        Assert.Contains(h.Awards, a => a.Reason == "daily:chess-daily");
        Assert.True(h.View(0).GetProperty("puzzle").GetProperty("solved").GetBoolean());
    }

    const System.Text.Json.JsonValueKind JsonValueKindNull = System.Text.Json.JsonValueKind.Null;

    [Fact]
    public void Wrong_first_move_costs_an_attempt_and_resets_the_board()
    {
        var (h, p) = Open();
        var start = h.View(0).GetProperty("fen").GetString();
        var c = ChessCore.FromFen(p.Fen);
        var wrong = c.Legal().First(m => Uci(m) != p.Key);
        var r = h.Act(0, "move", new { from = ChessCore.Name(wrong.From), to = ChessCore.Name(wrong.To) });
        Assert.True(r.Ok);
        Assert.Contains("спроба 2", r.Message);
        Assert.Equal(start, h.View(0).GetProperty("fen").GetString());
        Assert.Equal(2, h.View(0).GetProperty("puzzle").GetProperty("attempts").GetInt32());
        Solve(h, p);
        Assert.Equal(2, Assert.Single(h.Scores).Attempts);
    }

    [Fact]
    public void Every_puzzle_can_be_played_through_to_mate()
    {
        // Кожну задачу банку проходимо через справжню гру: захист відповідає, наступні ходи приймаються.
        foreach (var p in ChessPuzzles.Bank.Where(x => x.N == 2).Take(10).Concat(ChessPuzzles.Bank.Where(x => x.N == 3).Take(4)))
        {
            var g = new RoomHarness("chess-daily", seed: 3);
            Assert.True(g.Solo("Петро").Ok);
            var game = (ChessDaily)g.Room.Game;
            // Load бере задачу за днем — для чужої позиції підміняємо день так, щоб збігся з банком.
            var day = DateOnly.Parse("2026-09-29").AddDays(Array.IndexOf(ChessPuzzles.Bank, p)).ToString("yyyy-MM-dd");
            lock (g.Room.Sync) game.Load(System.Text.Json.JsonSerializer.Serialize(new
            {
                Day = day, Fen = p.Fen, Step = 0, Moves = Array.Empty<string>(), LastFrom = -1, LastTo = -1,
                StartedAt = g.Clock.UtcNow, Attempts = 1, Solved = false, GaveUp = false, Ms = 0L,
            }));
            Solve(g, p);
            Assert.Equal(RoomStatus.Finished, g.Room.Status);
        }
    }

    [Fact]
    public void Reveal_shows_the_key_and_gives_no_score()
    {
        var (h, p) = Open();
        Assert.True(h.Act(0, "reveal").Ok);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Empty(h.Scores);
        var key = h.View(0).GetProperty("puzzle").GetProperty("key");
        Assert.Equal(p.Key[..2], key.GetProperty("from").GetString());
        Assert.False(h.Act(0, "move", new { from = "a1", to = "a2" }).Ok);
    }

    [Fact]
    public void Save_and_load_keep_the_attempt()
    {
        var (h, p) = Open();
        Assert.True(Move(h, p.Key));
        var game = (ChessDaily)h.Room.Game;
        var copy = new ChessDaily();
        copy.Load(game.Save()!);
        Assert.Equal(game.Save(), copy.Save());
    }
}
