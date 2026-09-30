using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Реверсі удвох: правила ядра (perft, 8 напрямків, край і порожнє поле), пас, кінці, відмови, годинник, «Ще раз».
/// Позиції ставимо через <see cref="Reversi.Load"/> — як у шашках, без лазіння в приватні поля.
/// </summary>
public class ReversiTests
{
    static RoomHarness Table(string clock = "none")
    {
        var h = new RoomHarness("reversi", options: new { clock });
        h.Join("Оля");
        h.Join("Петро");
        return h;
    }

    /// <summary>Дошка з переліку полів: 'b'/'w' + назва поля; решта порожня.</summary>
    static string Board(params (char P, string Sq)[] discs)
    {
        var b = Enumerable.Repeat('.', 64).ToArray();
        foreach (var (p, sq) in discs) b[ReversiCore.Parse(sq)!.Value] = p;
        return new string(b);
    }

    static RoomHarness Table(int side, string board)
    {
        var h = Table();
        lock (h.Room.Sync) h.Room.Game.Load(JsonSerializer.Serialize(new Reversi.Position(board, side)));
        return h;
    }

    static int Cell(string sq) => ReversiCore.Parse(sq)!.Value;
    static ActResult Move(RoomHarness h, int seat, string sq) => h.Act(seat, "move", new { cell = Cell(sq) });
    static string BoardOf(RoomHarness h) => h.View(0).GetProperty("board").GetString()!;
    static int[] Ints(JsonElement e) => [.. e.EnumerateArray().Select(x => x.GetInt32())];
    static string Reason(RoomHarness h) => h.View(0).GetProperty("result").GetProperty("reason").GetString()!;

    // ---------- ядро ----------

    [Fact]
    public void Notation_goes_top_down_like_othello()
    {
        Assert.Equal("a1", ReversiCore.Name(0));
        Assert.Equal("h8", ReversiCore.Name(63));
        Assert.Equal(37, ReversiCore.Parse("f5"));
        Assert.Null(ReversiCore.Parse("i1"));
        Assert.Null(ReversiCore.Parse("a9"));
        Assert.Null(ReversiCore.Parse("a10"));
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(2, 12)]
    [InlineData(3, 56)]
    [InlineData(4, 244)]
    [InlineData(5, 1396)]
    [InlineData(6, 8200)]
    [InlineData(7, 55092)]
    public void Perft_from_the_start_matches_oeis(int depth, long nodes)
    {
        var c = ReversiCore.Start();
        Assert.Equal(nodes, ReversiCore.Perft(c.Discs[0], c.Discs[1], depth));
    }

    [Fact]
    public void Start_position_and_the_four_first_moves()
    {
        var h = Table();
        var v = h.View(0);
        Assert.Equal(Board(('w', "d4"), ('b', "e4"), ('b', "d5"), ('w', "e5")), v.GetProperty("board").GetString());
        Assert.Equal(0, v.GetProperty("turn").GetInt32());
        Assert.Equal("b", v.GetProperty("toMove").GetString());
        Assert.Equal(new[] { "d3", "c4", "f5", "e6" }.Select(Cell).Order(), Ints(v.GetProperty("legal")).Order());
        Assert.Equal(2, v.GetProperty("count").GetProperty("b").GetInt32());
        Assert.Equal(2, v.GetProperty("count").GetProperty("w").GetInt32());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("last").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("result").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("clock").ValueKind);
        // Глядач бачить те саме — прихованого нема.
        Assert.Equal(v.GetRawText(), h.View(null).GetRawText());
    }

    [Fact]
    public void A_move_flips_and_passes_the_turn()
    {
        var h = Table();
        Assert.True(Move(h, 0, "f5").Ok);
        var v = h.View(1);
        Assert.Equal('b', BoardOf(h)[Cell("e5")]);
        Assert.Equal(Cell("f5"), v.GetProperty("last").GetInt32());
        Assert.Equal([Cell("e5")], Ints(v.GetProperty("flipped")));
        Assert.Equal(1, v.GetProperty("turn").GetInt32());
        Assert.Equal("w", v.GetProperty("toMove").GetString());
        Assert.Equal(4, v.GetProperty("count").GetProperty("b").GetInt32());
        Assert.Equal(1, v.GetProperty("count").GetProperty("w").GetInt32());
        Assert.Equal(1, v.GetProperty("moves").GetInt32());
        Assert.True(Move(h, 1, "f6").Ok);   // f6 бере e5 по діагоналі (d4 біла)
        Assert.Equal('w', BoardOf(h)[Cell("e5")]);
    }

    [Fact]
    public void One_move_flips_in_all_eight_directions()
    {
        string[] near = ["c3", "d3", "e3", "c4", "e4", "c5", "d5", "e5"];
        string[] far = ["b2", "d2", "f2", "b4", "f4", "b6", "d6", "f6"];
        var h = Table(0, Board([.. near.Select(s => ('w', s)), .. far.Select(s => ('b', s))]));
        Assert.True(Move(h, 0, "d4").Ok);
        Assert.Equal(near.Select(Cell).Order(), Ints(h.View(0).GetProperty("flipped")).Order());
        Assert.All(near, s => Assert.Equal('b', BoardOf(h)[Cell(s)]));
    }

    [Fact]
    public void Several_discs_in_a_row_and_several_directions_at_once()
    {
        // Праворуч два білих до чорної, донизу — один; далека біла h1 не чіпається.
        var h = Table(0, Board(('w', "b1"), ('w', "c1"), ('b', "d1"), ('w', "a2"), ('b', "a3"), ('w', "h1")));
        var c = ReversiCore.FromString(BoardOf(h), 0)!;
        Assert.Equal(3, ReversiCore.Pop(c.Flips(0, Cell("a1"))));
        Assert.True(Move(h, 0, "a1").Ok);
        Assert.Equal("bbbb...w", BoardOf(h)[..8]);
        Assert.Equal('b', BoardOf(h)[Cell("a2")]);
    }

    [Fact]
    public void An_empty_square_or_the_edge_breaks_the_row()
    {
        // Порожнє b4 між білою c4 і чорною a4.
        var gap = ReversiCore.FromString(Board(('b', "a4"), ('w', "c4")), 0)!;
        Assert.Equal(0UL, gap.Flips(0, Cell("d4")));
        // Ряд білих упирається в край, чорної за ним нема.
        var edge = ReversiCore.FromString(Board(('w', "a4"), ('w', "b4"), ('w', "c4")), 0)!;
        Assert.Equal(0UL, edge.Flips(0, Cell("d4")));
        // Край не загортається на сусідній рядок: за a4 «ліворуч» не h3.
        var wrap = ReversiCore.FromString(Board(('b', "h3"), ('w', "a4")), 0)!;
        Assert.Equal(0UL, wrap.Flips(0, Cell("b4")));
        var wrapDown = ReversiCore.FromString(Board(('b', "a5"), ('w', "h4")), 0)!;
        Assert.Equal(0UL, wrapDown.Flips(0, Cell("g4")));
    }

    // ---------- пас і кінець ----------

    [Fact]
    public void A_player_with_no_move_passes_automatically()
    {
        // Після c1 у білих лишається b8, а ходу нема; у чорних є c8.
        var h = Table(0, Board(('b', "a1"), ('w', "b1"), ('b', "a8"), ('w', "b8")));
        Assert.True(Move(h, 0, "c1").Ok);
        var v = h.View(0);
        Assert.Equal(0, v.GetProperty("turn").GetInt32());
        Assert.Equal(1, v.GetProperty("pass").GetInt32());
        Assert.Equal([Cell("c8")], Ints(v.GetProperty("legal")));
        Assert.Contains(h.Outbox.OfType<Journal>(), j => j.Text == "⚪ білі пасують — ходити нікуди");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.False(Move(h, 1, "c8").Ok);   // білі пропущені
    }

    [Fact]
    public void Wiping_the_board_wins_and_asks_for_the_achievement()
    {
        var h = Table(0, Board(('b', "a1"), ('w', "b1"), ('b', "a8"), ('w', "b8")));
        Assert.True(Move(h, 0, "c1").Ok);
        Assert.True(Move(h, 0, "c8").Ok);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        var r = h.View(0).GetProperty("result");
        Assert.Equal("wipe", r.GetProperty("reason").GetString());
        Assert.Equal(6, r.GetProperty("b").GetInt32());
        Assert.Equal(0, r.GetProperty("w").GetInt32());
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("pass").ValueKind);
        var award = Assert.Single(h.Awards);
        Assert.Equal(("Оля", 0, "ach:reversi-wipe"), (award.Nick, award.Shards, award.Reason));
        Assert.Equal("Реверсі: Оля ⚫ 6 : 0 ⚪ Петро — дошку витерто", h.Outbox.OfType<Journal>().Last().Text);
        Assert.NotNull(AchievementCatalog.Get("reversi-wipe"));
        Assert.NotNull(AchievementCatalog.Get("reversi-5"));
    }

    [Fact]
    public void Game_ends_when_nobody_can_move_and_discs_are_counted()
    {
        // Після c1 біла h8 одна в кутку: ходу нема ні в кого, хоч дошка й порожня.
        var h = Table(0, Board(('b', "a1"), ('w', "b1"), ('w', "h8")));
        Assert.True(Move(h, 0, "c1").Ok);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Equal("count", Reason(h));
        var v = h.View(1);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("turn").ValueKind);
        Assert.Empty(Ints(v.GetProperty("legal")));
        Assert.Equal("Реверсі: Оля ⚫ 3 : 1 ⚪ Петро", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Empty(h.Awards);
    }

    [Fact]
    public void White_can_win_too()
    {
        var h = Table(1, Board(('w', "a1"), ('b', "b1"), ('b', "h8"), ('w', "a8"), ('w', "b8"), ('w', "c8")));
        Assert.True(Move(h, 1, "c1").Ok);
        Assert.Equal([1], h.Room.Result!.Winners);
        Assert.Equal(1, h.View(0).GetProperty("result").GetProperty("winner").GetInt32());
    }

    [Fact]
    public void Full_board_32_32_is_a_draw()
    {
        // a1 порожнє; b1 біла, c1 чорна (переверне лише b1), a2 і b2 чорні — вниз і по діагоналі нема чого брати.
        var b = new char[64];
        b[Cell("a1")] = '.';
        b[Cell("b1")] = 'w';
        b[Cell("c1")] = b[Cell("a2")] = b[Cell("b2")] = 'b';
        var blacks = 27;   // 30 чорних разом із трьома вище, 33 білі
        for (var i = 0; i < 64; i++)
        {
            if (b[i] != '\0') continue;
            b[i] = blacks-- > 0 ? 'b' : 'w';
        }
        var h = Table(0, new string(b));
        Assert.True(Move(h, 0, "a1").Ok);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(h.Room.Result!.Draw);
        var r = h.View(0).GetProperty("result");
        Assert.Equal(JsonValueKind.Null, r.GetProperty("winner").ValueKind);
        Assert.Equal((32, 32), (r.GetProperty("b").GetInt32(), r.GetProperty("w").GetInt32()));
        Assert.EndsWith("— нічия", h.Outbox.OfType<Journal>().Last().Text);
    }

    // ---------- відмови: вид не міняється ----------

    [Fact]
    public void Every_refusal_leaves_the_view_untouched()
    {
        var h = Table();
        var before = h.View(0).GetRawText();
        (int Seat, string Action, object? Payload, string Msg)[] bad =
        [
            (1, "move", new { cell = Cell("d3") }, "Не так швидко — зараз не твій хід"),
            (0, "move", new { cell = Cell("d4") }, "Поле вже зайняте"),
            (0, "move", new { cell = Cell("a1") }, "Так не можна — нічого не перевертається"),
            (0, "move", new { cell = 64 }, "Не зрозумів, куди ставити"),
            (0, "move", new { cell = -1 }, "Не зрозумів, куди ставити"),
            (0, "move", new { cell = "z9" }, "Не зрозумів, куди ставити"),
            (0, "move", new { cell = 3.5 }, "Не зрозумів, куди ставити"),
            (0, "move", new { col = 3 }, "Не зрозумів, куди ставити"),
            (0, "move", null, "Не зрозумів, куди ставити"),
            (0, "move", new[] { 19 }, "Не зрозумів, куди ставити"),
            (0, "flag", null, "Час ще є"),
            (0, "draw", null, "Тут так не ходять"),
        ];
        foreach (var (seat, action, payload, msg) in bad)
        {
            var r = h.Act(seat, action, payload);
            Assert.False(r.Ok, $"{action} {JsonSerializer.Serialize(payload)}");
            Assert.Equal(msg, r.Message);
            Assert.Equal(before, h.View(0).GetRawText());
        }
        // Назвою поля теж можна.
        Assert.True(h.Act(0, "move", new { cell = "d3" }).Ok);
    }

    [Fact]
    public void Nothing_goes_after_the_game_is_over()
    {
        var h = Table();
        Assert.True(h.Act(0, "resign").Ok);
        var before = h.View(0).GetRawText();
        Assert.False(Move(h, 1, "d3").Ok);
        Assert.False(h.Act(1, "resign").Ok);
        Assert.False(h.Act(1, "flag").Ok);
        Assert.Equal(before, h.View(0).GetRawText());
    }

    // ---------- інші кінці ----------

    [Fact]
    public void Resigning_gives_the_game_away()
    {
        var h = Table();
        Assert.True(Move(h, 0, "d3").Ok);
        Assert.True(h.Act(0, "resign").Ok);
        Assert.Equal([1], h.Room.Result!.Winners);
        Assert.Equal("resign", Reason(h));
        Assert.Equal("Реверсі: Оля здається — Петро білі 1:0 Оля чорні", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void Leaving_the_table_loses_the_game()
    {
        var h = Table();
        Assert.True(Move(h, 0, "d3").Ok);
        Assert.True(h.Leave("Петро").Ok);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Equal("left", Views.Json(h.Room.Game.View(0)).GetProperty("result").GetProperty("reason").GetString());
        Assert.Contains(h.Outbox.OfType<Journal>(), j => j.Text.Contains("встає з-за столу"));
    }

    [Fact]
    public void A_fallen_flag_loses_on_time()
    {
        var h = Table("3");
        Assert.True(Move(h, 0, "d3").Ok);
        Assert.True(Move(h, 1, "c3").Ok);   // тепер іде час чорних
        Assert.Equal(0, h.View(0).GetProperty("clock").GetProperty("running").GetInt32());
        h.Clock.Advance(TimeSpan.FromMinutes(4));
        var r = h.Act(1, "flag");
        Assert.True(r.Ok);
        Assert.Equal("Овва! У суперника впав прапорець", r.Message);
        Assert.Equal([1], h.Room.Result!.Winners);
        Assert.Equal("time", Reason(h));
    }

    [Fact]
    public void Pass_hands_the_clock_back_to_the_mover()
    {
        var h = new RoomHarness("reversi", options: new { clock = "3" });
        h.Join("Оля");
        h.Join("Петро");
        Assert.True(Move(h, 0, "d3").Ok);
        Assert.True(Move(h, 1, "c3").Ok);
        lock (h.Room.Sync) h.Room.Game.Load(JsonSerializer.Serialize(new Reversi.Position(
            Board(('b', "a1"), ('w', "b1"), ('b', "a8"), ('w', "b8")), 0)));
        Assert.True(Move(h, 0, "c1").Ok);   // білі пасують
        Assert.Equal(0, h.View(0).GetProperty("clock").GetProperty("running").GetInt32());
    }

    [Fact]
    public void Rematch_swaps_colours_and_clears_the_board()
    {
        var h = Table();
        Assert.True(Move(h, 0, "d3").Ok);
        Assert.True(h.Act(1, "resign").Ok);
        Assert.True(h.Rematch("Оля").Ok);
        Assert.Equal("Петро", h.NickOf(0));   // тепер Петро чорними
        Assert.Equal("Оля", h.NickOf(1));
        var v = h.View(0);
        Assert.Equal(ReversiCore.Start().BoardString(), v.GetProperty("board").GetString());
        Assert.Equal(0, v.GetProperty("moves").GetInt32());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("result").ValueKind);
        Assert.Equal(0, v.GetProperty("turn").GetInt32());
        Assert.True(Move(h, 0, "d3").Ok);
    }

    [Fact]
    public void Series_follows_the_nick_after_the_rematch()
    {
        var h = Table();
        Assert.True(Move(h, 0, "d3").Ok);
        Assert.True(h.Act(1, "resign").Ok);   // Петро (білі) здався — Оля виграла
        Assert.True(h.Rematch("Оля").Ok);     // тепер Петро — місце 0, Оля — 1
        var s = h.View(0).GetProperty("series");
        Assert.Equal(1, s.GetProperty("games").GetInt32());
        Assert.Equal([0, 1], Ints(s.GetProperty("wins")));
        Assert.Equal(0, s.GetProperty("draws").GetInt32());
    }

    [Fact]
    public void A_whole_random_game_keeps_the_invariants()
    {
        var rng = new Random(7);
        var h = Table();
        for (var guard = 0; guard < 80 && h.Room.Status == RoomStatus.Playing; guard++)
        {
            var v = h.View(0);
            var legal = Ints(v.GetProperty("legal"));
            var seat = v.GetProperty("turn").GetInt32();
            Assert.NotEmpty(legal);
            Assert.True(h.Act(seat, "move", new { cell = legal[rng.Next(legal.Length)] }).Ok);
            var c = h.View(0).GetProperty("count");
            Assert.Equal(BoardOf(h).Count(ch => ch == 'b'), c.GetProperty("b").GetInt32());
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var r = h.View(0).GetProperty("result");
        Assert.Equal(BoardOf(h).Count(ch => ch == 'w'), r.GetProperty("w").GetInt32());
    }
}
