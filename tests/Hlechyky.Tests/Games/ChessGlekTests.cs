using System.Diagnostics;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit;

namespace Hlechyky.Tests.Games;

/// <summary>Глек-бот у шахах (прохід №3, спільне 228) і сам рушій.</summary>
public class ChessGlekTests
{
    static RoomHarness Open(int seed = 42)
    {
        var h = new RoomHarness("chess-glek", seed: seed);
        Assert.True(h.Solo("Оля").Ok);
        return h;
    }

    [Fact]
    public void Solo_table_is_not_rated_and_not_in_the_lobby()
    {
        var info = new ChessGlek().Info;
        Assert.True(info.Solo);
        Assert.True(info.Private);
        Assert.False(info.Rated);
        Assert.Equal(0, info.TickMs % 10);
        Assert.Equal("chess", info.Client);
    }

    [Fact]
    public void First_game_human_is_white_and_glek_answers_after_a_pause()
    {
        var h = Open();
        var v = h.View(0);
        Assert.Equal("w", v.GetProperty("me").GetString());
        Assert.Equal(0, v.GetProperty("turn").GetInt32());
        Assert.True(h.Act(0, "move", new { from = "e2", to = "e4" }).Ok);
        Assert.False(h.Act(0, "move", new { from = "d2", to = "d4" }).Ok);   // Глек ще думає
        Assert.Equal(1, h.View(0).GetProperty("turn").GetInt32());
        Assert.True(h.View(0).GetProperty("glek").GetProperty("thinking").GetBoolean());
        h.Tick(2);
        Assert.Equal(1, h.View(0).GetProperty("moves").GetArrayLength());   // ще «думає» — пауза
        h.Tick(15);
        v = h.View(0);
        Assert.Equal(2, v.GetProperty("moves").GetArrayLength());
        Assert.Equal(0, v.GetProperty("turn").GetInt32());
        Assert.True(v.GetProperty("legal").GetArrayLength() > 0);
    }

    [Fact]
    public void Choosing_black_restarts_and_glek_opens()
    {
        var h = Open();
        Assert.True(h.Act(0, "set", new { color = "black", level = "medium" }).Ok);
        var v = h.View(0);
        Assert.Equal("b", v.GetProperty("me").GetString());
        Assert.Equal(1, v.GetProperty("turn").GetInt32());
        Assert.Equal("medium", v.GetProperty("glek").GetProperty("level").GetString());
        Assert.Equal(0, v.GetProperty("legal").GetArrayLength());   // не твій хід — ходів не підсвічуємо
        h.Tick(15);
        Assert.Equal(1, h.View(0).GetProperty("moves").GetArrayLength());
        Assert.Equal("b", h.View(0).GetProperty("toMove").GetString());
    }

    [Fact]
    public void Resign_finishes_without_rewards()
    {
        var h = Open();
        Assert.True(h.Act(0, "resign").Ok);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        // Місця Глека в кімнаті нема — каркас переможця-не-людину відкидає; хто взяв гору, каже вид і Журнал.
        Assert.Equal(1, h.View(0).GetProperty("result").GetProperty("winner").GetInt32());
        Assert.Contains("Глек", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Empty(h.Awards);
    }

    [Fact]
    public void Rematch_swaps_colours_when_by_turn()
    {
        var h = Open();
        Assert.True(h.Act(0, "resign").Ok);
        Assert.True(h.Rematch().Ok);
        Assert.Equal("b", h.View(0).GetProperty("me").GetString());
    }

    [Fact]
    public void Glek_takes_a_hanging_queen()
    {
        // Білий ферзь на d5 під пішаком e6: середній Глек (чорні) бере не думаючи.
        var c = ChessCore.FromFen("4k3/8/4p3/3Q4/8/8/8/4K3 b - - 0 1");
        var m = ChessEngine.Pick(c, ChessEngine.Medium, new Random(1))!.Value;
        Assert.Equal("e6d5", ChessCore.Name(m.From) + ChessCore.Name(m.To));
    }

    [Fact]
    public void Glek_mates_in_one_when_he_can()
    {
        var c = ChessCore.FromFen("6k1/5ppp/8/8/8/8/8/R5K1 w - - 0 1");
        foreach (var lvl in new[] { ChessEngine.Easy, ChessEngine.Medium })
        {
            var m = ChessEngine.Pick(c, lvl, new Random(3))!.Value;
            Assert.Equal("a1a8", ChessCore.Name(m.From) + ChessCore.Name(m.To));
        }
    }

    [Fact]
    public void Same_seed_same_moves()
    {
        string Play(int seed)
        {
            var h = Open(seed);
            for (var i = 0; i < 6 && h.Room.Status == RoomStatus.Playing; i++)
            {
                var legal = h.View(0).GetProperty("legal");
                if (legal.GetArrayLength() == 0) { h.Tick(15); continue; }
                var m = legal[0];
                h.Act(0, "move", new { from = m.GetProperty("from").GetString(), to = m.GetProperty("to").GetString() });
                h.Tick(15);
            }
            return h.View(0).GetProperty("fen").GetString()!;
        }
        Assert.Equal(Play(7), Play(7));
    }

    [Fact]
    public void A_full_game_against_glek_ends()
    {
        // Людину грає легкий Глек, чорними — середній: партія має дійти до кінця (мат, нічия) за розумний час.
        var h = Open(5);
        var rng = new Random(9);
        for (var i = 0; i < 400 && h.Room.Status == RoomStatus.Playing; i++)
        {
            var legal = h.View(0).GetProperty("legal");
            if (legal.GetArrayLength() == 0) { h.Tick(3); continue; }
            var fen = h.View(0).GetProperty("fen").GetString()!;
            var m = ChessEngine.Pick(ChessCore.FromFen(fen), ChessEngine.Easy, rng)!.Value;
            Assert.True(h.Act(0, "move", new { from = ChessCore.Name(m.From), to = ChessCore.Name(m.To) }).Ok);
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Medium_glek_thinks_well_under_200_ms_in_a_busy_middlegame()
    {
        var c = ChessCore.FromFen("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1");
        ChessEngine.Pick(c, ChessEngine.Medium, new Random(1));   // прогрів JIT
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 3; i++) ChessEngine.Pick(c, ChessEngine.Medium, new Random(i));
        Assert.True(sw.ElapsedMilliseconds / 3 < 200, $"середній Глек думав {sw.ElapsedMilliseconds / 3} мс");
    }
}
