using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Ультимативні хрестики (№214): куди сходив у малому полі — туди йде суперник; три поля в ряд беруть партію.</summary>
public class UltimateTicTacToeTests
{
    static RoomHarness Table()
    {
        var h = new RoomHarness("ttt9");
        h.Join("Оля");
        h.Join("Петро");
        return h;
    }

    static ActResult Move(RoomHarness h, int seat, int board, int cell) => h.Act(seat, "move", new { cell = board * 9 + cell });

    static int[] Legal(RoomHarness h) => [.. h.View(0).GetProperty("legal").EnumerateArray().Select(e => e.GetInt32())];

    /// <summary>
    /// ✕ бере малі поля 0, 1, 2 середнім рядом, ◯ тим часом бере поля 5 і 3 — і кожен хід суперника
    /// посилає туди, куди треба. Перші сім пар ходів (без останнього ходу ✕).
    /// </summary>
    static readonly (int seat, int board, int cell)[] Opening =
    [
        (0, 0, 3), (1, 3, 1), (0, 1, 4), (1, 4, 2), (0, 2, 5), (1, 5, 0),
        (0, 0, 4), (1, 4, 1), (0, 1, 5), (1, 5, 2), (0, 2, 3), (1, 3, 0),
        (0, 0, 5), (1, 5, 1),
    ];

    [Fact]
    public void First_move_goes_anywhere_and_sends_the_rival_to_the_matching_board()
    {
        var h = Table();
        Assert.Equal(81, Legal(h).Length);
        Assert.True(Move(h, 0, 0, 4).Ok);

        var v = h.View(1);
        Assert.Equal(4, v.GetProperty("next").GetInt32());
        Assert.All(Legal(h), c => Assert.Equal(4, c / 9));
        Assert.Equal(9, Legal(h).Length);
        Assert.Equal("Не те поле: ходити треба в підсвічене", Move(h, 1, 0, 0).Message);
        Assert.Equal("Не так швидко — зараз не твій хід", Move(h, 0, 4, 0).Message);
        Assert.True(Move(h, 1, 4, 0).Ok);
        Assert.Equal(0, h.View(0).GetProperty("next").GetInt32());
    }

    [Fact]
    public void Three_in_a_row_takes_a_small_board_and_a_closed_board_frees_the_next_move()
    {
        var h = Table();
        foreach (var (s, b, c) in Opening) Assert.True(Move(h, s, b, c).Ok, $"{s}:{b}/{c}");

        var boards = h.View(0).GetProperty("boards");
        Assert.Equal("x", boards[0].GetString());
        Assert.Equal("o", boards[5].GetString());
        Assert.Equal([3, 4, 5], h.View(0).GetProperty("small")[0].EnumerateArray().Select(e => e.GetInt32()));

        // ✕ посилає ◯ у поле 0, а воно вже взяте — ◯ ходить будь-куди, крім закритих полів.
        Assert.True(Move(h, 0, 1, 0).Ok);
        Assert.Equal(-1, h.View(1).GetProperty("next").GetInt32());
        var legal = Legal(h);
        Assert.DoesNotContain(legal, c => c / 9 is 0 or 5);
        Assert.Contains(legal, c => c / 9 == 8);
        Assert.Equal("Це поле вже закрите — ходи в інше", Move(h, 1, 0, 0).Message);
        Assert.True(Move(h, 1, 8, 8).Ok);
        Assert.Equal(8, h.View(0).GetProperty("next").GetInt32());
    }

    [Fact]
    public void Three_small_boards_in_a_row_win_the_game_and_the_journal_keeps_the_score()
    {
        var h = Table();
        foreach (var (s, b, c) in Opening) Move(h, s, b, c);
        Assert.True(Move(h, 0, 1, 3).Ok);   // ✕ бере поле 1, ◯ іде в поле 3
        Assert.True(Move(h, 1, 3, 2).Ok);   // ◯ бере поле 3, ✕ іде в поле 2
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("Твоя взяла! Три поля в ряд", Move(h, 0, 2, 4).Message);

        Assert.Equal([0], h.Room.Result!.Winners);
        var v = h.View(0);
        Assert.Equal("x", v.GetProperty("winner").GetString());
        Assert.Equal([0, 1, 2], v.GetProperty("line").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal("o", v.GetProperty("boards")[3].GetString());
        Assert.Empty(Legal(h));
        Assert.Equal("Ультимативні хрестики: Оля ✕ 1:0 Петро ◯", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Equal("Партію зіграно, тисни «Ану ще раз»", Move(h, 1, 8, 8).Message);
    }

    [Fact]
    public void Resign_gives_the_game_to_the_rival()
    {
        var h = Table();
        Move(h, 0, 4, 4);
        Assert.True(h.Act(0, "resign").Ok);
        Assert.Equal([1], h.Room.Result!.Winners);
        Assert.Equal("o", h.View(1).GetProperty("winner").GetString());
    }

    [Fact]
    public void Leaving_mid_game_is_a_forfeit()
    {
        var h = Table();
        Move(h, 0, 4, 4);
        h.Leave("Петро");
        Assert.Equal([0], h.Room.Result!.Winners);
    }

    [Fact]
    public void Garbage_is_refused_without_touching_the_board()
    {
        var h = Table();
        Assert.Equal("Не зрозумів, куди ходити", h.Act(0, "move", new { nope = 1 }).Message);
        Assert.Equal("Ця клітинка вже зайнята", h.Act(0, "move", new { cell = 81 }).Message);
        Assert.Equal("Тут так не ходять", h.Act(0, "jump").Message);
        Assert.True(Move(h, 0, 4, 4).Ok);
        Assert.Equal("Ця клітинка вже зайнята", Move(h, 1, 4, 4).Message);
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("series").ValueKind);
    }
}
