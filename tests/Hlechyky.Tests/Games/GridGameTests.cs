using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Три покрокові гри на сітці: хрестики-нолики, зникаючі й чотири в ряд. Це регресійна база — вони мусять
/// грати рівно так, як грали до переїзду на кімнати (TESTING.md §4).
/// </summary>
public class GridGameTests
{
    static RoomHarness Table(string game = "ttt")
    {
        var h = new RoomHarness(game);
        h.Join("Оля");
        h.Join("Петро");
        return h;
    }

    static ActResult Move(RoomHarness h, int seat, int cell) => h.Act(seat, "move", new { cell });

    static string?[] Cells(RoomHarness h, int? seat = 0) =>
        [.. h.View(seat).GetProperty("cells").EnumerateArray().Select(c => c.ValueKind == JsonValueKind.Null ? null : c.GetString())];

    // ---------- спільні правила ----------

    [Fact]
    public void First_move_belongs_to_the_cross_and_lands_where_asked()
    {
        var h = Table();
        Assert.Equal(0, h.View(0).GetProperty("turn").GetInt32());
        Assert.True(Move(h, 0, 4).Ok);

        Assert.Equal("x", Cells(h)[4]);
        Assert.Equal(1, h.View(0).GetProperty("turn").GetInt32());
    }

    [Fact]
    public void Taken_cell_and_wrong_turn_are_refused_without_touching_the_board()
    {
        var h = Table();
        Move(h, 0, 4);
        var before = Views.Text(h.Room.Game.View(null));

        Assert.Equal("Зараз не твій хід", Move(h, 0, 0).Message);
        Assert.Equal("Ця клітинка вже зайнята", Move(h, 1, 4).Message);
        Assert.Equal("Не зрозумів, куди ходити", h.Act(1, "move", new { nope = 1 }).Message);
        Assert.Equal("Ця клітинка вже зайнята", Move(h, 1, 99).Message);
        Assert.Equal(before, Views.Text(h.Room.Game.View(null)));
    }

    [Fact]
    public void Unknown_action_is_refused()
    {
        var h = Table();
        Assert.Equal("Тут так не ходять", h.Act(0, "jump", new { cell = 0 }).Message);
    }

    [Fact]
    public void A_row_of_three_wins_and_writes_the_score_into_the_journal()
    {
        var h = Table();
        foreach (var (seat, cell) in new[] { (0, 0), (1, 3), (0, 1), (1, 4), (0, 2) }) Move(h, seat, cell);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Equal([0, 1, 2], h.View(0).GetProperty("line").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal("Хрестики-нолики: Оля ✕ 1:0 Петро ◯", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Equal("x", h.View(0).GetProperty("winner").GetString());
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("turn").ValueKind);
    }

    [Fact]
    public void A_full_board_is_a_draw()
    {
        var h = Table();
        // ✕ ◯ ✕ / ✕ ◯ ◯ / ◯ ✕ ✕ — жодного ряду
        foreach (var (seat, cell) in new[] { (0, 0), (1, 1), (0, 2), (1, 4), (0, 3), (1, 6), (0, 7), (1, 5), (0, 8) })
            Move(h, seat, cell);

        Assert.True(h.Room.Result!.Draw);
        Assert.Equal("draw", h.View(0).GetProperty("winner").GetString());
        Assert.Contains("зіграли внічию", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void Every_seat_can_win()
    {
        var h = Table();
        foreach (var (seat, cell) in new[] { (0, 0), (1, 3), (0, 1), (1, 4), (0, 8), (1, 5) }) Move(h, seat, cell);

        Assert.Equal([1], h.Room.Result!.Winners);
        Assert.Equal("o", h.View(1).GetProperty("winner").GetString());
    }

    [Fact]
    public void Diagonals_count_too()
    {
        var h = Table();
        foreach (var (seat, cell) in new[] { (0, 2), (1, 1), (0, 4), (1, 3), (0, 6) }) Move(h, seat, cell);
        Assert.Equal([2, 4, 6], h.View(0).GetProperty("line").EnumerateArray().Select(e => e.GetInt32()));
    }

    // ---------- вид ----------

    [Fact]
    public void View_matches_the_shape_the_client_expects()
    {
        var h = Table();
        var v = h.View(0);

        Assert.Equal(3, v.GetProperty("width").GetInt32());
        Assert.Equal(3, v.GetProperty("height").GetInt32());
        Assert.Equal(9, v.GetProperty("cells").GetArrayLength());
        Assert.Equal(["✕", "◯"], v.GetProperty("marks").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(JsonValueKind.Null, v.GetProperty("line").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("fading").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("winner").ValueKind);
        // Гра відкрита: глядач бачить те саме, що й гравці.
        Assert.Equal(Views.Text(h.Room.Game.View(0)), Views.Text(h.Room.Game.View(null)));
    }

    [Fact]
    public void Views_are_copies_not_the_live_board()
    {
        var h = Table();
        var first = (object)h.Room.Game.View(0);
        Move(h, 0, 0);
        Assert.NotEqual(Views.Text(first), Views.Text(h.Room.Game.View(0)));
    }

    // ---------- зникаючі ----------

    [Fact]
    public void Fading_mode_removes_the_oldest_mark_and_never_ends_in_a_draw()
    {
        var h = Table("ttt3");
        // ✕ ставить 0,1 і 6 (не в ряд), ◯ — 3,4,5 і виграє середнім рядом? ні: перевіряємо саме зникання
        foreach (var (seat, cell) in new[] { (0, 0), (1, 3), (0, 1), (1, 5), (0, 6), (1, 7) }) Move(h, seat, cell);

        Assert.Equal(0, h.View(0).GetProperty("fading").GetInt32());   // черга ✕: щезне його найстарша, клітинка 0
        Move(h, 0, 2);
        var cells = Cells(h);
        Assert.Null(cells[0]);
        Assert.Equal(3, cells.Count(c => c == "x"));
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void Fading_mode_cannot_win_with_a_mark_that_just_vanished()
    {
        var h = Table("ttt3");
        // ✕ по черзі 0,1,2 — але перед третім ходом щезає 0, тож ряду 0-1-2 не буде
        foreach (var (seat, cell) in new[] { (0, 0), (1, 3), (0, 1), (1, 4), (0, 6), (1, 7), (0, 2) }) Move(h, seat, cell);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Null(Cells(h)[0]);
    }

    [Fact]
    public void Fading_mode_still_has_a_winner_when_three_live_marks_line_up()
    {
        var h = Table("ttt3");
        foreach (var (seat, cell) in new[] { (0, 0), (1, 3), (0, 1), (1, 4), (0, 2) }) Move(h, seat, cell);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
    }

    // ---------- чотири в ряд ----------

    [Fact]
    public void Discs_fall_to_the_bottom_of_the_column()
    {
        var h = Table("c4");
        Move(h, 0, 3);
        Move(h, 1, 3);

        var cells = Cells(h);
        Assert.Equal("x", cells[5 * 7 + 3]);
        Assert.Equal("o", cells[4 * 7 + 3]);
        Assert.Equal(42, cells.Length);
    }

    [Fact]
    public void A_full_column_is_refused()
    {
        var h = Table("c4");
        for (var i = 0; i < 3; i++) { Move(h, 0, 0); Move(h, 1, 0); }   // шість фішок навперемін — переможця нема
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("Ця колонка вже повна", Move(h, 0, 0).Message);
    }

    [Fact]
    public void Four_in_a_row_wins_and_the_journal_names_the_colours()
    {
        var h = Table("c4");
        foreach (var (seat, cell) in new[] { (0, 1), (1, 6), (0, 2), (1, 6), (0, 3), (1, 6), (0, 4) }) Move(h, seat, cell);

        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Equal("Чотири в ряд: Оля жовті 1:0 Петро зелені", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Equal(4, h.View(0).GetProperty("line").GetArrayLength());
    }

    [Fact]
    public void The_move_payload_is_always_cell_even_when_it_means_a_column()
    {
        // Модуль c4 колись слав { col } — сервер такого поля не знає, і хід просто зникав.
        // Поле завжди зветься cell; у грі з гравітацією його значення — номер колонки.
        var h = Table("c4");
        Assert.Equal("Не зрозумів, куди ходити", h.Act(0, "move", new { col = 3 }).Message);
        Assert.True(h.Act(0, "move", new { cell = 3 }).Ok);
        Assert.True(h.Act(1, "move", 3).Ok);      // голе число теж приймаємо
    }

    [Fact]
    public void Column_outside_the_board_is_refused()
    {
        var h = Table("c4");
        Assert.False(Move(h, 0, 7).Ok);
        Assert.False(Move(h, 0, -1).Ok);
        Assert.Equal(0, h.Room.Moves);
    }

    // ---------- рематч і детермінізм ----------

    [Fact]
    public void Rematch_gives_a_clean_board_and_swaps_the_seats()
    {
        var h = Table();
        foreach (var (seat, cell) in new[] { (0, 0), (1, 3), (0, 1), (1, 4), (0, 2) }) Move(h, seat, cell);
        Assert.True(h.Rematch("Оля").Ok);

        Assert.Equal("Петро", h.Room.Seats[0]);
        Assert.All(Cells(h), c => Assert.Null(c));
        Assert.Equal(0, h.View(0).GetProperty("turn").GetInt32());
        Assert.Equal(0, h.Room.Moves);
        Assert.True(Move(h, 0, 0).Ok);   // тепер починає Петро
    }

    [Fact]
    public void Same_moves_give_the_same_view()
    {
        static string Play(int seed)
        {
            var h = new RoomHarness("c4", seed: seed);
            h.Join("Оля");
            h.Join("Петро");
            foreach (var (seat, cell) in new[] { (0, 3), (1, 3), (0, 2), (1, 4), (0, 1) }) Move(h, seat, cell);
            return Views.Text(h.Room.Game.View(null));
        }
        Assert.Equal(Play(1), Play(999));   // тут випадковості немає взагалі
    }

    [Fact]
    public void All_three_grid_games_are_in_the_catalog()
    {
        var catalog = new Registry().Catalog;
        foreach (var id in new[] { "ttt", "ttt3", "c4" })
        {
            var game = Assert.Single(catalog, g => g.Id == id);
            Assert.Equal("board", game.Group);
            Assert.Equal("whenFull", game.Start);
            Assert.True(game.Rated);
            Assert.Equal(2, game.MinPlayers);
            Assert.Equal(2, game.MaxPlayers);
            Assert.NotEmpty(game.Hint);
        }
    }
}
