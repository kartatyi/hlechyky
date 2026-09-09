using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Російські шашки: обов'язкове взяття, ланцюги, турецький удар, дамки. Позиції ставимо через
/// <see cref="Checkers.Load"/> — тим самим шляхом, яким стан підіймає сервер, тож тест не лізе у
/// приватні поля гри (TESTING.md §4).
/// </summary>
public class CheckersTests
{
    // ---------- обгортки ----------

    static RoomHarness Table()
    {
        var h = new RoomHarness("checkers");
        h.Join("Оля");
        h.Join("Петро");
        return h;
    }

    static Checkers Game(RoomHarness h) => (Checkers)h.Room.Game;

    /// <summary>Дошка з переліку «символ + поле»; решта темних полів порожня.</summary>
    static string Board(params (char P, string Sq)[] men)
    {
        var b = new char[64];
        for (var i = 0; i < 64; i++) b[i] = CheckersRules.Dark(i) ? '.' : ' ';
        foreach (var (p, sq) in men) b[CheckersRules.Parse(sq)!.Value] = p;
        return new string(b);
    }

    /// <summary>Стіл із наперед виставленою позицією.</summary>
    static RoomHarness Table(int turn, params (char P, string Sq)[] men) => Table(turn, 0, men);

    static RoomHarness Table(int turn, int quiet, params (char P, string Sq)[] men)
    {
        var h = Table();
        Game(h).Load(JsonSerializer.Serialize(
            new Checkers.Snapshot(Board(men), turn, null, null, false, null, null, quiet, [])));
        return h;
    }

    static ActResult Move(RoomHarness h, int seat, params string[] path) => h.Act(seat, "move", new { path });

    static string BoardOf(RoomHarness h, int? seat = 0) => h.View(seat).GetProperty("board").GetString()!;

    static char At(RoomHarness h, string square) => BoardOf(h)[CheckersRules.Parse(square)!.Value];

    static string[][] LegalOf(RoomHarness h) =>
        [.. h.View(0).GetProperty("legal").EnumerateArray()
            .Select(c => c.EnumerateArray().Select(x => x.GetString()!).ToArray())];

    static string Chain(string[] path) => string.Join("-", path);

    // ---------- початок ----------

    [Fact]
    public void The_start_position_is_twelve_against_twelve_and_white_moves_first()
    {
        var h = Table();
        var v = h.View(0);

        Assert.Equal(12, v.GetProperty("count").GetProperty("w").GetInt32());
        Assert.Equal(12, v.GetProperty("count").GetProperty("b").GetInt32());
        Assert.Equal(0, v.GetProperty("turn").GetInt32());
        Assert.Equal("w", v.GetProperty("toMove").GetString());
        Assert.False(v.GetProperty("mustCapture").GetBoolean());
    }

    [Fact]
    public void White_has_seven_moves_from_the_start()
    {
        var h = Table();
        var legal = LegalOf(h);

        Assert.Equal(7, legal.Length);
        Assert.All(legal, c => Assert.Equal(2, c.Length));
        Assert.Equal(["a3-b4", "c3-b4", "c3-d4", "e3-d4", "e3-f4", "g3-f4", "g3-h4"],
            legal.Select(Chain).Order());
    }

    [Fact]
    public void The_board_is_sixty_four_squares_and_light_ones_are_blank()
    {
        var board = BoardOf(Table());

        Assert.Equal(64, board.Length);
        Assert.Equal(32, board.Count(c => c == ' '));
        // a8 світле, a1 темне — з цього кута дошки й починається вся нумерація.
        Assert.Equal(' ', board[CheckersRules.Index(0, 0)]);
        Assert.Equal('w', board[CheckersRules.Index(7, 0)]);
        Assert.Equal('b', board[CheckersRules.Index(0, 1)]);
    }

    [Fact]
    public void Seats_are_white_and_black()
    {
        var h = Table();
        Assert.Equal(["білі", "чорні"], h.Room.Summary().SeatNames);
    }

    // ---------- прості ходи ----------

    [Fact]
    public void A_man_steps_one_square_forward_and_hands_the_turn_over()
    {
        var h = Table();
        Assert.True(Move(h, 0, "c3", "d4").Ok);

        Assert.Equal('.', At(h, "c3"));
        Assert.Equal('w', At(h, "d4"));
        Assert.Equal(1, h.View(0).GetProperty("turn").GetInt32());
        Assert.Equal("b", h.View(0).GetProperty("toMove").GetString());
    }

    [Fact]
    public void A_man_never_steps_backwards()
    {
        var h = Table(0, ('w', "d4"), ('b', "a7"));
        Assert.Equal("Так не ходять", Move(h, 0, "d4", "c3").Message);
        Assert.Equal("Так не ходять", Move(h, 0, "d4", "e3").Message);
        Assert.True(Move(h, 0, "d4", "c5").Ok);
    }

    [Fact]
    public void Moving_out_of_turn_is_refused_without_touching_the_board()
    {
        var h = Table();
        var before = Views.Text(Game(h).View(null));

        Assert.Equal("Зараз не твій хід", Move(h, 1, "d6", "c5").Message);
        Assert.Equal(before, Views.Text(Game(h).View(null)));
        Assert.Equal(0, h.Room.Moves);
    }

    [Fact]
    public void Moving_someone_elses_piece_is_refused()
    {
        var h = Table();
        Assert.Equal("Це не твоя шашка", Move(h, 0, "d6", "c5").Message);
        Assert.Equal("Це не твоя шашка", Move(h, 0, "d4", "c5").Message);   // порожнє поле — теж не твоя
    }

    [Fact]
    public void An_unknown_action_is_refused()
    {
        var h = Table();
        Assert.Equal("Тут так не ходять", h.Act(0, "jump", new { path = new[] { "c3", "d4" } }).Message);
    }

    [Fact]
    public void A_broken_payload_is_refused_and_nothing_moves()
    {
        var h = Table();
        var before = Views.Text(Game(h).View(null));

        Assert.Equal("Не зрозумів, куди ходити", h.Act(0, "move", new { cell = 4 }).Message);
        Assert.Equal("Не зрозумів, куди ходити", Move(h, 0, "c3").Message);
        Assert.Equal("Не зрозумів, куди ходити", Move(h, 0, "c3", "c4").Message);   // світле поле
        Assert.Equal("Не зрозумів, куди ходити", Move(h, 0, "c3", "z9").Message);
        Assert.Equal(before, Views.Text(Game(h).View(null)));
    }

    [Fact]
    public void The_move_payload_is_always_a_path_of_square_names()
    {
        // Клієнт шле { path: ["c3","d4"] } і нічого іншого: перевіряємо саме те, що надсилає модуль.
        var h = Table();
        Assert.Equal("Не зрозумів, куди ходити", h.Act(0, "move", new { from = "c3", to = "d4" }).Message);
        Assert.True(h.Act(0, "move", new { path = new[] { "c3", "d4" } }).Ok);
    }

    // ---------- обов'язкове взяття ----------

    [Fact]
    public void A_quiet_move_is_refused_while_a_capture_is_on_the_board()
    {
        var h = Table(0, ('w', "c3"), ('b', "d4"), ('b', "h8"));
        var before = Views.Text(Game(h).View(null));

        Assert.True(h.View(0).GetProperty("mustCapture").GetBoolean());
        Assert.Equal("Бити обов'язково", Move(h, 0, "c3", "b4").Message);
        Assert.Equal(before, Views.Text(Game(h).View(null)));
    }

    [Fact]
    public void A_capture_takes_the_piece_off_the_board()
    {
        var h = Table(0, ('w', "c3"), ('b', "d4"), ('b', "h8"));
        Assert.True(Move(h, 0, "c3", "e5").Ok);

        Assert.Equal('.', At(h, "c3"));
        Assert.Equal('.', At(h, "d4"));
        Assert.Equal('w', At(h, "e5"));
        Assert.Equal(1, h.View(0).GetProperty("count").GetProperty("b").GetInt32());
    }

    [Fact]
    public void Any_capture_may_be_chosen_not_only_the_longest()
    {
        // Обидві шашки б'ються однією; правило «бий будь-що» дозволяє взяти хоч ту, хоч ту.
        var h = Table(0, ('w', "c3"), ('b', "d4"), ('b', "b4"), ('b', "h8"));
        Assert.Equal(["c3-a5", "c3-e5"], LegalOf(h).Select(Chain).Order());
        Assert.True(Move(h, 0, "c3", "a5").Ok);
    }

    [Fact]
    public void A_man_captures_backwards_too()
    {
        var h = Table(0, ('w', "e5"), ('b', "d4"), ('b', "h8"));
        Assert.True(Move(h, 0, "e5", "c3").Ok);

        Assert.Equal('w', At(h, "c3"));
        Assert.Equal('.', At(h, "d4"));
    }

    [Fact]
    public void A_piece_without_a_capture_may_not_move_while_another_one_must_take()
    {
        var h = Table(0, ('w', "c3"), ('w', "g3"), ('b', "d4"), ('b', "h8"));
        Assert.Equal("Бити обов'язково", Move(h, 0, "g3", "f4").Message);
        Assert.Equal(["c3-e5"], LegalOf(h).Select(Chain));
    }

    // ---------- ланцюги ----------

    [Fact]
    public void A_chain_of_three_captures_goes_in_one_move()
    {
        var h = Table(0, ('w', "c3"), ('b', "d4"), ('b', "f4"), ('b', "f2"), ('b', "a7"));
        Assert.Equal(["c3-e5-g3-e1"], LegalOf(h).Select(Chain));
        Assert.True(Move(h, 0, "c3", "e5", "g3", "e1").Ok);

        Assert.Equal('w', At(h, "e1"));
        Assert.All(new[] { "c3", "d4", "f4", "f2" }, sq => Assert.Equal('.', At(h, sq)));
        Assert.Equal(1, h.View(0).GetProperty("count").GetProperty("b").GetInt32());
    }

    [Fact]
    public void Stopping_in_the_middle_of_a_chain_is_refused()
    {
        var h = Table(0, ('w', "c3"), ('b', "d4"), ('b', "f4"), ('b', "f2"), ('b', "a7"));
        var before = Views.Text(Game(h).View(null));

        Assert.Equal("Треба дібрати до кінця", Move(h, 0, "c3", "e5").Message);
        Assert.Equal("Треба дібрати до кінця", Move(h, 0, "c3", "e5", "g3").Message);
        Assert.Equal(before, Views.Text(Game(h).View(null)));
    }

    [Fact]
    public void A_chain_that_wanders_off_the_legal_path_is_refused()
    {
        var h = Table(0, ('w', "c3"), ('b', "d4"), ('b', "f4"), ('b', "f2"), ('b', "a7"));
        Assert.Equal("Так не ходять", Move(h, 0, "c3", "e5", "c7").Message);
    }

    [Fact]
    public void The_last_path_shows_the_whole_chain()
    {
        var h = Table(0, ('w', "c3"), ('b', "d4"), ('b', "f4"), ('b', "f2"), ('b', "a7"));
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("lastPath").ValueKind);
        Move(h, 0, "c3", "e5", "g3", "e1");

        Assert.Equal(["c3", "e5", "g3", "e1"],
            h.View(0).GetProperty("lastPath").EnumerateArray().Select(x => x.GetString()));
    }

    // ---------- турецький удар ----------

    [Fact]
    public void A_captured_piece_is_never_jumped_twice()
    {
        // Дамка обходить чотири шашки по колу й вертається на своє поле: далі бити нема кого,
        // бо всі побиті ще стоять на дошці й через них не стрибають.
        var h = Table(0, ('W', "d2"), ('b', "e3"), ('b', "e5"), ('b', "c5"), ('b', "c3"), ('b', "h8"));
        Assert.Contains("d2-f4-d6-b4-d2", LegalOf(h).Select(Chain));
        Assert.True(Move(h, 0, "d2", "f4", "d6", "b4", "d2").Ok);

        Assert.Equal('W', At(h, "d2"));
        Assert.All(new[] { "e3", "e5", "c5", "c3" }, sq => Assert.Equal('.', At(h, sq)));
        Assert.Equal(1, h.View(0).GetProperty("count").GetProperty("b").GetInt32());
    }

    [Fact]
    public void A_man_walking_a_ring_never_jumps_the_same_piece_twice()
    {
        // Турецький удар не лише для дамок: проста обходить чотири шашки по колу й вертається на своє
        // поле. Побиті стоять до кінця ланцюга, тож із c3 бити вже нема кого. Дамкою вона теж не стає —
        // e1 не її остання лінія.
        var h = Table(0, ('w', "c3"), ('b', "d4"), ('b', "f4"), ('b', "f2"), ('b', "d2"), ('b', "a7"));
        Assert.Contains("c3-e5-g3-e1-c3", LegalOf(h).Select(Chain));
        Assert.True(Move(h, 0, "c3", "e5", "g3", "e1", "c3").Ok);

        Assert.Equal('w', At(h, "c3"));
        Assert.All(new[] { "d4", "f4", "f2", "d2" }, sq => Assert.Equal('.', At(h, sq)));
        Assert.Equal(1, h.View(0).GetProperty("count").GetProperty("b").GetInt32());
    }

    [Fact]
    public void A_piece_captured_earlier_in_the_chain_still_blocks_the_way()
    {
        // e1-b4-d6-f4 могло б тривати взяттям e3 із приземленням на c1 — якби побита d2 щезала одразу.
        // Вона стоїть до кінця ланцюга, тож за e3 приземлитись нема куди, і ланцюг на f4 закінчується.
        var h = Table(0, ('W', "e1"), ('b', "d2"), ('b', "c5"), ('b', "e5"), ('b', "e3"), ('b', "h8"));
        var chains = LegalOf(h).Select(Chain).ToArray();

        Assert.Contains("e1-b4-d6-f4", chains);
        Assert.DoesNotContain(chains, c => c.StartsWith("e1-b4-d6-f4-"));
        Assert.True(Move(h, 0, "e1", "b4", "d6", "f4").Ok);
        Assert.Equal('b', At(h, "e3"));         // e3 вціліла: до неї ланцюг не дістав
        Assert.Equal('W', At(h, "f4"));
    }

    // ---------- дамки ----------

    [Fact]
    public void A_king_slides_along_the_whole_diagonal()
    {
        var h = Table(0, ('W', "a1"), ('B', "h6"));
        Assert.Equal(["a1-b2", "a1-c3", "a1-d4", "a1-e5", "a1-f6", "a1-g7", "a1-h8"],
            LegalOf(h).Select(Chain).Order());
        Assert.True(Move(h, 0, "a1", "g7").Ok);
        Assert.Equal('W', At(h, "g7"));
    }

    [Fact]
    public void A_king_captures_from_a_distance_and_may_land_on_any_free_square_behind()
    {
        var h = Table(0, ('W', "a1"), ('b', "c3"), ('b', "h2"));
        Assert.Equal(["a1-d4", "a1-e5", "a1-f6", "a1-g7", "a1-h8"], LegalOf(h).Select(Chain).Order());
        Assert.True(Move(h, 0, "a1", "h8").Ok);

        Assert.Equal('W', At(h, "h8"));
        Assert.Equal('.', At(h, "c3"));
    }

    [Fact]
    public void A_king_must_carry_on_the_chain_while_it_can()
    {
        var h = Table(0, ('W', "a1"), ('b', "c3"), ('b', "e5"), ('b', "g7"), ('b', "h2"));
        Assert.Equal(["a1-d4-f6-h8"], LegalOf(h).Select(Chain));
        Assert.Equal("Треба дібрати до кінця", Move(h, 0, "a1", "d4").Message);
        Assert.True(Move(h, 0, "a1", "d4", "f6", "h8").Ok);
    }

    [Fact]
    public void A_king_does_not_jump_two_pieces_standing_side_by_side()
    {
        var h = Table(0, ('W', "a1"), ('b', "c3"), ('b', "d4"));
        Assert.False(h.View(0).GetProperty("mustCapture").GetBoolean());
        Assert.Equal(["a1-b2"], LegalOf(h).Select(Chain));
    }

    [Fact]
    public void A_king_does_not_jump_its_own_piece()
    {
        var h = Table(0, ('W', "a1"), ('w', "c3"), ('b', "d4"), ('b', "h8"));
        // Дамка стоїть на тій самій діагоналі, що й ворожа d4, але між ними своя шашка.
        Assert.Equal(["c3-e5"], LegalOf(h).Select(Chain));
    }

    // ---------- дамки з простих ----------

    [Fact]
    public void A_man_that_reaches_the_last_row_becomes_a_king()
    {
        var h = Table(0, ('w', "c7"), ('b', "a3"));
        Assert.True(Move(h, 0, "c7", "d8").Ok);
        Assert.Equal('W', At(h, "d8"));
    }

    [Fact]
    public void A_black_man_is_crowned_on_the_first_row()
    {
        var h = Table(1, ('b', "c3"), ('w', "h6"));
        Assert.True(Move(h, 1, "c3", "b2").Ok);
        Assert.True(Move(h, 0, "h6", "g7").Ok);
        Assert.True(Move(h, 1, "b2", "a1").Ok);
        Assert.Equal('B', At(h, "a1"));
    }

    [Fact]
    public void A_man_crowned_in_the_middle_of_a_chain_carries_on_as_a_king()
    {
        // f6 б'є e7, стає дамкою на d8 — і вже дамкою дістає b6 через порожнє c7.
        // Простій це не під силу: вона б'є лише через сусіднє поле.
        var h = Table(0, ('w', "f6"), ('b', "e7"), ('b', "b6"), ('b', "h2"));
        Assert.Equal(["f6-d8-a5"], LegalOf(h).Select(Chain));
        Assert.True(Move(h, 0, "f6", "d8", "a5").Ok);

        Assert.Equal('W', At(h, "a5"));
        Assert.Equal('.', At(h, "e7"));
        Assert.Equal('.', At(h, "b6"));
    }

    [Fact]
    public void A_capture_that_ends_on_the_last_row_crowns_the_man()
    {
        // Третій випадок перетворення: ланцюг не проходить крізь останню лінію, а закінчується на ній.
        var h = Table(0, ('w', "f6"), ('b', "g7"), ('b', "a3"));
        Assert.Equal(["f6-h8"], LegalOf(h).Select(Chain));
        Assert.True(Move(h, 0, "f6", "h8").Ok);

        Assert.Equal('W', At(h, "h8"));
        Assert.Equal('.', At(h, "g7"));
    }

    // ---------- кінець партії ----------

    [Fact]
    public void Taking_the_last_piece_wins_the_game()
    {
        var h = Table(0, ('w', "c3"), ('b', "d4"));
        Assert.True(Move(h, 0, "c3", "e5").Ok);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Equal("nopieces", h.View(0).GetProperty("result").GetProperty("reason").GetString());
        Assert.Equal("Шашки: Оля білі 1:0 Петро чорні", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void A_player_with_nowhere_to_go_loses()
    {
        // Чорна h8 замкнена своїми ж стінами: g7 зайнята білою, а за нею стоїть ще одна.
        var h = Table(0, ('w', "g7"), ('w', "f6"), ('w', "a3"), ('b', "h8"));
        Assert.True(Move(h, 0, "a3", "b4").Ok);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Equal("nomoves", h.View(0).GetProperty("result").GetProperty("reason").GetString());
    }

    [Fact]
    public void Every_seat_can_win()
    {
        var h = Table(1, ('b', "f6"), ('w', "e5"));
        Assert.True(Move(h, 1, "f6", "d4").Ok);

        Assert.Equal([1], h.Room.Result!.Winners);
        Assert.Equal(1, h.View(1).GetProperty("result").GetProperty("winner").GetInt32());
        Assert.Equal("Шашки: Петро чорні 1:0 Оля білі", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void Fifteen_king_moves_without_a_capture_are_a_draw()
    {
        var h = Table(0, quiet: Checkers.QuietLimit * 2 - 1, men: [('W', "a1"), ('B', "h6")]);
        Assert.True(Move(h, 0, "a1", "b2").Ok);

        Assert.True(h.Room.Result!.Draw);
        Assert.Equal("kings15", h.View(0).GetProperty("result").GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("result").GetProperty("winner").ValueKind);
        Assert.Contains("зіграли внічию", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void Fourteen_full_moves_by_kings_are_not_a_draw_but_the_fifteenth_is()
    {
        // «15 ходів» — це 15 ходів кожного, а не 15 півходів: виграш «дамка проти дамки» саме стільки
        // й маневрує. Дамки ходять по паралельних діагоналях (a1-c3 і e3-h6), тож бити одна одну не
        // можуть; періоди маршрутів різні (3 і 4), тож позиція не встигає повторитись утретє.
        string[][] white = [["a1", "c3"], ["c3", "b2"], ["b2", "a1"]];
        string[][] black = [["h6", "f4"], ["f4", "g5"], ["g5", "e3"], ["e3", "h6"]];
        var h = Table(0, ('W', "a1"), ('B', "h6"));

        for (var k = 0; k < Checkers.QuietLimit; k++)
        {
            Assert.Equal(RoomStatus.Playing, h.Room.Status);   // до 15-го повного ходу партія триває
            Assert.True(Move(h, 0, white[k % 3]).Ok);
            Assert.True(Move(h, 1, black[k % 4]).Ok);
        }

        Assert.True(h.Room.Result!.Draw);
        Assert.Equal("kings15", h.View(0).GetProperty("result").GetProperty("reason").GetString());
        Assert.Contains("15 ходів дамками", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void A_man_move_resets_the_quiet_counter()
    {
        var h = Table(0, quiet: Checkers.QuietLimit * 2 - 1, men: [('W', "a1"), ('w', "a3"), ('B', "h6")]);
        Assert.True(Move(h, 0, "a3", "b4").Ok);

        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.True(Move(h, 1, "h6", "g5").Ok);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void A_capture_resets_the_quiet_counter()
    {
        var h = Table(0, quiet: Checkers.QuietLimit * 2 - 1, men: [('W', "a1"), ('b', "c3"), ('b', "h2")]);
        Assert.True(Move(h, 0, "a1", "d4").Ok);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void The_same_position_three_times_is_a_draw()
    {
        var h = Table(0, ('W', "a1"), ('B', "h6"));
        for (var i = 0; i < 2; i++)
        {
            Assert.True(Move(h, 0, "a1", "b2").Ok);
            Assert.True(Move(h, 1, "h6", "g5").Ok);
            Assert.True(Move(h, 0, "b2", "a1").Ok);
            Assert.True(Move(h, 1, "g5", "h6").Ok);
        }
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.True(Move(h, 0, "a1", "b2").Ok);        // ця позиція трапилась утретє

        Assert.True(h.Room.Result!.Draw);
        Assert.Equal("repetition", h.View(0).GetProperty("result").GetProperty("reason").GetString());
    }

    [Fact]
    public void Resigning_hands_the_win_to_the_other_seat()
    {
        var h = Table();
        Assert.True(h.Act(1, "resign").Ok);

        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Equal("resign", h.View(0).GetProperty("result").GetProperty("reason").GetString());
        Assert.Contains("Петро здається", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void Two_draw_offers_make_a_draw()
    {
        var h = Table();
        Assert.Equal("Запропонував нічию", h.Act(0, "draw").Message);
        Assert.Equal(0, h.View(1).GetProperty("drawOffer").GetInt32());
        Assert.Equal("Нічия", h.Act(1, "draw").Message);

        Assert.True(h.Room.Result!.Draw);
        Assert.Equal("agreed", h.View(0).GetProperty("result").GetProperty("reason").GetString());
    }

    [Fact]
    public void Offering_a_draw_twice_is_refused()
    {
        var h = Table();
        Assert.True(h.Act(0, "draw").Ok);
        Assert.Equal("Пропозиція вже висить", h.Act(0, "draw").Message);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void Declining_clears_the_offer()
    {
        var h = Table();
        Assert.True(h.Act(0, "draw").Ok);
        Assert.Equal("Пропозицію знято", h.Act(1, "decline").Message);
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("drawOffer").ValueKind);
        Assert.Equal("Нічиєї ніхто не пропонував", h.Act(1, "decline").Message);
    }

    [Fact]
    public void A_move_takes_the_draw_offer_off_the_table()
    {
        var h = Table();
        Assert.True(h.Act(0, "draw").Ok);
        Assert.True(Move(h, 0, "c3", "d4").Ok);
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("drawOffer").ValueKind);
    }

    [Fact]
    public void Leaving_in_the_middle_is_a_technical_loss()
    {
        var h = Table();
        Move(h, 0, "c3", "d4");
        Assert.True(h.Leave("Петро").Ok);

        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Equal("left", Views.Json(Game(h).View(0)).GetProperty("result").GetProperty("reason").GetString());
        Assert.Contains("встав з-за столу", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void Nothing_can_be_played_after_the_game_is_over()
    {
        var h = Table(0, ('w', "c3"), ('b', "d4"));
        Assert.True(Move(h, 0, "c3", "e5").Ok);

        Assert.False(Move(h, 1, "h8", "g7").Ok);
        Assert.False(h.Act(0, "resign").Ok);
        Assert.False(h.Act(0, "draw").Ok);
    }

    // ---------- вид ----------

    [Fact]
    public void The_view_matches_the_shape_the_client_expects()
    {
        var h = Table();
        var v = h.View(0);

        Assert.Equal(JsonValueKind.String, v.GetProperty("board").ValueKind);
        Assert.Equal(JsonValueKind.Array, v.GetProperty("legal").ValueKind);
        Assert.Equal(JsonValueKind.False, v.GetProperty("mustCapture").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("lastPath").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("drawOffer").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("result").ValueKind);
        Assert.True(Views.Has(v, "toMove"));
        Assert.True(Views.Has(v, "count"));
    }

    [Fact]
    public void The_game_is_open_so_the_spectator_sees_the_same_board()
    {
        var h = Table();
        Move(h, 0, "c3", "d4");
        Assert.Equal(Views.Text(Game(h).View(0)), Views.Text(Game(h).View(null)));
        Assert.Equal(Views.Text(Game(h).View(0)), Views.Text(Game(h).View(1)));
    }

    [Fact]
    public void Legal_is_empty_and_the_turn_is_null_once_the_game_is_over()
    {
        var h = Table(0, ('w', "c3"), ('b', "d4"));
        Move(h, 0, "c3", "e5");

        Assert.Empty(LegalOf(h));
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("turn").ValueKind);
        Assert.False(h.View(0).GetProperty("mustCapture").GetBoolean());
    }

    [Fact]
    public void Legal_shows_only_captures_when_a_capture_is_on_the_board()
    {
        var h = Table(0, ('w', "c3"), ('w', "g3"), ('b', "d4"), ('b', "h8"));
        Assert.True(h.View(0).GetProperty("mustCapture").GetBoolean());
        Assert.All(LegalOf(h), c => Assert.Equal(2, c.Length));
        Assert.Equal(["c3-e5"], LegalOf(h).Select(Chain));
    }

    [Fact]
    public void Views_are_copies_not_the_live_board()
    {
        var h = Table();
        var first = (object)Game(h).View(0);
        Move(h, 0, "c3", "d4");
        Assert.NotEqual(Views.Text(first), Views.Text(Game(h).View(0)));
    }

    // ---------- детермінізм, рематч, збереження ----------

    [Fact]
    public void The_same_moves_give_the_same_view()
    {
        static string Play(int seed)
        {
            var h = new RoomHarness("checkers", seed: seed);
            h.Join("Оля");
            h.Join("Петро");
            foreach (var (seat, path) in new (int, string[])[]
                     {
                         (0, ["c3", "d4"]), (1, ["f6", "e5"]), (0, ["d4", "f6"]), (1, ["g7", "e5"]),
                     })
                Assert.True(h.Act(seat, "move", new { path }).Ok);
            return Views.Text(h.Room.Game.View(null));
        }
        Assert.Equal(Play(1), Play(999));      // випадковості в шашках нема взагалі
    }

    [Fact]
    public void Rematch_gives_a_clean_board_and_swaps_the_seats()
    {
        var h = Table(0, ('w', "c3"), ('b', "d4"));
        Move(h, 0, "c3", "e5");
        Assert.True(h.Rematch("Оля").Ok);

        Assert.Equal("Петро", h.Room.Seats[0]);
        Assert.Equal(new string(CheckersRules.Start()), BoardOf(h));
        Assert.Equal(12, h.View(0).GetProperty("count").GetProperty("w").GetInt32());
        Assert.Equal(0, h.View(0).GetProperty("turn").GetInt32());
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("result").ValueKind);
        Assert.Equal(7, LegalOf(h).Length);
        Assert.True(Move(h, 0, "c3", "d4").Ok);        // тепер білими грає Петро
    }

    [Fact]
    public void A_saved_game_loads_back_into_the_very_same_view()
    {
        var h = Table(0, ('w', "c3"), ('b', "d4"), ('b', "f4"), ('b', "f2"), ('b', "a7"));
        Move(h, 0, "c3", "e5", "g3", "e1");
        h.Act(1, "draw");

        var copy = new Checkers();
        copy.Load(Game(h).Save()!);
        Assert.Equal(Views.Text(Game(h).View(0)), Views.Text(copy.View(0)));
    }

    [Fact]
    public void A_finished_game_survives_the_save_and_load_round_trip()
    {
        var h = Table(0, ('w', "c3"), ('b', "d4"));
        Move(h, 0, "c3", "e5");

        var copy = new Checkers();
        copy.Load(Game(h).Save()!);
        var v = Views.Json(copy.View(0));
        Assert.Equal("nopieces", v.GetProperty("result").GetProperty("reason").GetString());
        Assert.Empty(v.GetProperty("legal").EnumerateArray());
    }

    // ---------- каталог ----------

    [Fact]
    public void Checkers_is_in_the_catalog_as_a_rated_board_game_for_two()
    {
        var game = Assert.Single(new Registry().Catalog, g => g.Id == "checkers");

        Assert.Equal("Шашки", game.Title);
        Assert.Equal("board", game.Group);
        Assert.Equal("whenFull", game.Start);
        Assert.True(game.Rated);
        Assert.Equal(2, game.MinPlayers);
        Assert.Equal(2, game.MaxPlayers);
        Assert.Equal(0, game.TickMs);
        Assert.False(game.Hidden);
        Assert.False(game.Private);
        Assert.Equal("checkers", game.Module);
        Assert.NotEmpty(game.Hint);
    }

    // ---------- правила окремо від кімнати ----------

    [Fact]
    public void Square_names_and_dark_squares_line_up_with_the_board_string()
    {
        Assert.Equal("a8", CheckersRules.Name(0));
        Assert.Equal("h1", CheckersRules.Name(63));
        Assert.Equal("c3", CheckersRules.Name(CheckersRules.Parse("c3")!.Value));
        Assert.True(CheckersRules.Dark(CheckersRules.Parse("a1")!.Value));
        Assert.Null(CheckersRules.Parse("a2"));      // світле поле
        Assert.Null(CheckersRules.Parse("j1"));
        Assert.Null(CheckersRules.Parse("c9"));
        Assert.Null(CheckersRules.Parse("c"));
        Assert.Null(CheckersRules.Parse(null));
    }

    [Fact]
    public void The_chain_cap_is_per_piece_so_no_piece_falls_out_of_the_view()
    {
        // Дошка з чотирма дамками проти чотирьох шашок дає купу приземлень; вид мусить лишатись
        // скінченним — але стеля на ОДНУ шашку, інакше при спрацюванні в переліку були б усі ланцюги
        // перших шашок і жодного від решти, і тими рештою гравець не походив би з UI.
        var b = Board([('W', "a1"), ('W', "c1"), ('W', "e1"), ('W', "g1"),
            ('b', "b4"), ('b', "d4"), ('b', "f4"), ('b', "h4")]).ToCharArray();

        var all = CheckersRules.Legal(b, 0);
        Assert.NotEmpty(all);
        Assert.InRange(all.Count, 4, 4 * CheckersRules.ViewChains);

        // Стеля в один ланцюг на шашку: з кожної, що вміє бити, лишається рівно один — і жодна не зникає.
        var tight = CheckersRules.Legal(b, 0, cap: 1);
        var startsOf = (List<CheckersMove> ms) => ms.Select(m => CheckersRules.Name(m.Path[0])).ToHashSet();
        Assert.Equal(["a1", "c1", "e1", "g1"], startsOf(all).Order());
        Assert.Equal(startsOf(all), startsOf(tight));
        Assert.Equal(4, tight.Count);
    }
}
