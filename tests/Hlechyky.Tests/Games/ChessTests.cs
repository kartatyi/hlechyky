using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Шахи: три варіанти на одних правилах. Головна перевірка правил — перфт (скільки листків у дереві на
/// задану глибину): якщо десь загубилась рокіровка, взяття на проході чи зв'язана фігура, число не зійдеться.
/// Решта тестів — про партію за столом: Журнал, здатись, нічия, вид, рематч (TESTING.md §4).
/// </summary>
public class ChessTests
{
    // ------------------------------------------------------------------------------------------
    // Обгортки
    // ------------------------------------------------------------------------------------------

    static RoomHarness Table(string variant = "classic", int seed = 42)
    {
        var h = new RoomHarness("chess", options: new { variant }, seed: seed);
        h.Join("Оля");
        h.Join("Петро");
        return h;
    }

    static ActResult Move(RoomHarness h, int seat, string from, string to, string? promo = null) =>
        h.Act(seat, "move", promo is null ? new { from, to } : new { from, to, promo });

    /// <summary>
    /// Поставити на дошку потрібну позицію. Партія за столом стану не зберігає, але <c>Save/Load</c> у неї
    /// є — і тест користується саме ним, а не чорним ходом у нутрощі гри.
    /// </summary>
    static void Position(RoomHarness h, string fen, ChessVariant variant = ChessVariant.Classic)
    {
        var json = JsonSerializer.Serialize(new
        {
            variant = variant.ToString(),
            fen,
            moves = Array.Empty<string>(),
            lostWhite = "",
            lostBlack = "",
            seen = new Dictionary<string, int>(),
            lastFrom = -1,
            lastTo = -1,
            drawOffer = (int?)null,
            winner = (int?)null,
            reason = (string?)null,
        });
        lock (h.Room.Sync) h.Room.Game.Load(json);
    }

    static ChessCore Core(string fen, ChessVariant variant = ChessVariant.Classic) => ChessCore.FromFen(fen, variant);

    /// <summary>Зробити хід на ядрі за координатами — щоб не тягати в тест внутрішні структури.</summary>
    static void Play(ChessCore c, string from, string to, char promo = ' ')
    {
        int f = ChessCore.Parse(from), t = ChessCore.Parse(to);
        var m = c.Legal().First(x => x.From == f && x.To == t && (promo == ' ' || " pnbrqk"[x.Promo] == promo));
        c.Make(m, out _);
    }

    static int Castles(ChessCore c) => c.Legal().Count(m => m.Kind == ChessMoveKind.Castle);

    static bool CanGo(ChessCore c, string from, string to) =>
        c.Legal().Any(m => m.From == ChessCore.Parse(from) && m.To == ChessCore.Parse(to));

    static string Journal(RoomHarness h) => h.Outbox.OfType<Journal>().Last().Text;

    static string Result(RoomHarness h, string field) => h.View(0).GetProperty("result").GetProperty(field).ToString();

    // ------------------------------------------------------------------------------------------
    // Перфт: головна перевірка правил
    // ------------------------------------------------------------------------------------------

    const string Kiwipete = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq -";

    [Fact]
    public void Perft_from_the_initial_position()
    {
        var c = ChessCore.Initial();
        Assert.Equal(20, c.Perft(1));
        Assert.Equal(400, c.Perft(2));
        Assert.Equal(8902, c.Perft(3));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Perft_depth_four_from_the_initial_position()
    {
        var sw = Stopwatch.StartNew();
        Assert.Equal(197281, ChessCore.Initial().Perft(4));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"перфт-4 має вкластись у 10 с, а зайняв {sw.Elapsed}");
    }

    [Fact]
    public void Perft_kiwipete_shallow()
    {
        var c = Core(Kiwipete);
        Assert.Equal(48, c.Perft(1));
        Assert.Equal(2039, c.Perft(2));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Perft_kiwipete_depth_three()
    {
        var sw = Stopwatch.StartNew();
        Assert.Equal(97862, Core(Kiwipete).Perft(3));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"Kiwipete-3 має вкластись у 10 с, а зайняв {sw.Elapsed}");
    }

    [Fact]
    public void Perft_of_the_endgame_position_with_en_passant_traps()
    {
        // Позиція №3 з класичного набору: майже самі пішаки, зате всі пастки зі взяттям на проході.
        var c = Core("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - -");
        Assert.Equal(14, c.Perft(1));
        Assert.Equal(191, c.Perft(2));
        Assert.Equal(2812, c.Perft(3));
    }

    [Fact]
    public void Perft_of_the_position_that_is_all_promotions()
    {
        var c = Core("r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq -");
        Assert.Equal(6, c.Perft(1));
        Assert.Equal(264, c.Perft(2));
        Assert.Equal(9467, c.Perft(3));
    }

    // ------------------------------------------------------------------------------------------
    // Рокіровка
    // ------------------------------------------------------------------------------------------

    const string EmptyRank = "4k3/8/8/8/8/8/8/R3K2R w KQ - 0 1";

    [Fact]
    public void Castling_both_ways_is_legal_on_an_empty_rank()
    {
        var c = Core(EmptyRank);
        Assert.Equal(2, Castles(c));
        Play(c, "e1", "h1");                       // «король бере туру» — той самий хід, що й O-O
        Assert.Equal("R....RK.", c.BoardString()[56..]);
    }

    [Fact]
    public void Castling_is_illegal_through_an_attacked_square()
    {
        // Чорна тура на f8 тримає f1 — коротка рокіровка веде короля через бите поле.
        var c = Core("4kr2/8/8/8/8/8/8/R3K2R w KQ - 0 1");
        Assert.Equal(1, Castles(c));
        Assert.True(CanGo(c, "e1", "a1"));
        Assert.False(CanGo(c, "e1", "h1"));
    }

    [Fact]
    public void Castling_is_illegal_out_of_check()
    {
        var c = Core("4r3/8/8/8/8/8/8/R3K2R w KQ - 0 1");
        Assert.True(c.InCheck());
        Assert.Equal(0, Castles(c));
    }

    [Fact]
    public void Castling_is_illegal_when_something_stands_between()
    {
        var c = Core("4k3/8/8/8/8/8/8/R3KB1R w KQ - 0 1");
        Assert.Equal(1, Castles(c));
        Assert.True(CanGo(c, "e1", "a1"));
    }

    [Fact]
    public void Castling_is_lost_once_the_king_has_moved()
    {
        var c = Core(EmptyRank);
        Play(c, "e1", "e2");
        Play(c, "e8", "e7");
        Play(c, "e2", "e1");
        Play(c, "e7", "e8");
        Assert.Equal(0, Castles(c));
    }

    [Fact]
    public void Castling_is_lost_on_the_side_whose_rook_has_moved()
    {
        var c = Core(EmptyRank);
        Play(c, "h1", "h2");
        Play(c, "e8", "e7");
        Play(c, "h2", "h1");
        Play(c, "e7", "e8");
        Assert.Equal(1, Castles(c));
        Assert.True(CanGo(c, "e1", "a1"));
    }

    [Fact]
    public void Taking_a_rook_on_its_home_square_takes_the_castling_right_with_it()
    {
        var c = Core("4k3/8/8/8/8/8/5n2/R3K2R b KQ - 0 1");
        Play(c, "f2", "h1");           // кінь з'їв туру h1 — коротка рокіровка пропала разом із нею
        Assert.Equal(1, Castles(c));
        Assert.True(CanGo(c, "e1", "a1"));
    }

    [Fact]
    public void Castling_moves_both_pieces_to_their_classic_squares()
    {
        var h = Table();
        Position(h, EmptyRank);
        Assert.True(Move(h, 0, "e1", "g1").Ok);
        Assert.Equal("R....RK.", h.View(0).GetProperty("board").GetString()![56..]);
        Assert.Equal("O-O", h.View(0).GetProperty("moves")[0].GetString());
    }

    // ------------------------------------------------------------------------------------------
    // Взяття на проході
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void En_passant_appears_right_after_a_double_step_and_takes_the_right_pawn()
    {
        var c = Core("4k3/8/8/8/4p3/8/3P4/4K3 w - - 0 1");
        Play(c, "d2", "d4");
        Assert.Equal("d3", ChessCore.Name(c.EnPassant));
        Assert.True(CanGo(c, "e4", "d3"));
        Play(c, "e4", "d3");
        Assert.Equal('.', c.BoardString()[ChessCore.Parse("d4")]);   // пішак, який пробіг, зник
        Assert.Equal('p', c.BoardString()[ChessCore.Parse("d3")]);
    }

    [Fact]
    public void En_passant_expires_after_one_move()
    {
        var c = Core("4k3/8/8/8/4p3/8/3P4/4K3 w - - 0 1");
        Play(c, "d2", "d4");
        Play(c, "e8", "e7");
        Play(c, "e1", "e2");
        Assert.Equal(-1, c.EnPassant);
        Assert.False(CanGo(c, "e4", "d3"));
    }

    // ------------------------------------------------------------------------------------------
    // Перетворення
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_pawn_promotes_into_any_of_the_four_pieces()
    {
        var c = Core("4k3/P7/8/8/8/8/8/4K3 w - - 0 1");
        var promos = c.Legal().Where(m => m.To == ChessCore.Parse("a8")).Select(m => " pnbrqk"[m.Promo]).Order().ToArray();
        Assert.Equal(['b', 'n', 'q', 'r'], promos);
    }

    [Fact]
    public void A_promotion_without_a_choice_becomes_a_queen()
    {
        var h = Table();
        Position(h, "4k3/P7/8/8/8/8/8/4K3 w - - 0 1");
        Assert.True(Move(h, 0, "a7", "a8").Ok);
        Assert.Equal('Q', h.View(0).GetProperty("board").GetString()![ChessCore.Parse("a8")]);
    }

    [Fact]
    public void A_promotion_takes_the_piece_the_player_asked_for()
    {
        var h = Table();
        Position(h, "4k3/P7/8/8/8/8/8/4K3 w - - 0 1");
        Assert.True(Move(h, 0, "a7", "a8", "n").Ok);
        Assert.Equal('N', h.View(0).GetProperty("board").GetString()![ChessCore.Parse("a8")]);
        Assert.Equal("a8=N", h.View(0).GetProperty("moves")[0].GetString());
    }

    [Fact]
    public void A_promotion_can_be_mate()
    {
        var h = Table();
        Position(h, "7k/P7/6K1/8/8/8/8/8 w - - 0 1");
        Assert.True(Move(h, 0, "a7", "a8", "q").Ok);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("a8=Q#", h.View(0).GetProperty("moves")[0].GetString());
        Assert.Equal("mate", Result(h, "reason"));
    }

    // ------------------------------------------------------------------------------------------
    // Кінець партії
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Mate_finishes_the_room_and_writes_the_score_into_the_journal()
    {
        var h = Table();
        Position(h, "6k1/5ppp/8/8/8/8/8/R5K1 w - - 0 1");
        Assert.True(Move(h, 0, "a1", "a8").Ok);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Equal("Шахи: Оля білі 1:0 Петро чорні (мат на 1-му ході)", Journal(h));
        Assert.Equal("0", Result(h, "winner"));
        Assert.Equal("Ra8#", h.View(0).GetProperty("moves")[0].GetString());
    }

    [Fact]
    public void Mate_still_marks_the_king_as_being_under_attack()
    {
        // Саме матовий кадр гравці й розглядають; якби check згасав разом із партією, мат у браузері
        // виглядав би як звичайний тихий хід — без червоного поля короля.
        var h = Table();
        Position(h, "6k1/5ppp/8/8/8/8/8/R5K1 w - - 0 1");
        Assert.True(Move(h, 0, "a1", "a8").Ok);

        var v = h.View(1);
        Assert.True(v.GetProperty("check").GetBoolean());
        Assert.Equal("b", v.GetProperty("toMove").GetString());   // король під боєм — той, кому ходити
        Assert.Empty(v.GetProperty("legal").EnumerateArray());    // але ходити нема чим
    }

    [Fact]
    public void Stalemate_is_a_draw()
    {
        var h = Table();
        Position(h, "7k/8/6K1/8/8/8/5Q2/8 w - - 0 1");
        Assert.True(Move(h, 0, "f2", "f7").Ok);

        Assert.True(h.Room.Result!.Draw);
        Assert.Equal("stalemate", Result(h, "reason"));
        Assert.Contains("зіграли внічию (пат)", Journal(h));
    }

    [Theory]
    [InlineData("8/8/4k3/8/8/4K3/8/8 w - -", true)]              // голі королі
    [InlineData("8/8/4k3/8/8/4K3/8/5B2 w - -", true)]            // король зі слоном
    [InlineData("8/8/4k3/8/8/4K3/8/5N2 w - -", true)]            // король із конем
    [InlineData("5b2/8/4k3/8/8/4K3/8/2B5 w - -", true)]          // однопольні слони
    [InlineData("5b2/8/4k3/8/8/4K3/8/5B2 w - -", false)]         // різнопольні — грати ще можна
    [InlineData("8/8/4k3/8/8/4K3/8/4NN2 w - -", false)]          // два коні правила нічиєю не звуть
    [InlineData("8/8/4k3/8/4P3/4K3/8/8 w - -", false)]           // є пішак
    public void Insufficient_material_knows_all_four_dead_positions(string fen, bool dead)
    {
        Assert.Equal(dead, Core(fen).InsufficientMaterial());
    }

    [Fact]
    public void The_last_capture_that_leaves_bare_kings_ends_the_game()
    {
        var h = Table();
        Position(h, "7k/8/8/8/8/8/1n6/K7 w - - 0 1");
        Assert.True(Move(h, 0, "a1", "b2").Ok);

        Assert.True(h.Room.Result!.Draw);
        Assert.Equal("material", Result(h, "reason"));
    }

    [Fact]
    public void Fifty_moves_without_a_capture_or_a_pawn_end_the_game()
    {
        var h = Table();
        Position(h, "4k3/8/8/8/8/8/8/R3K2R w KQ - 99 60");
        Assert.True(Move(h, 0, "a1", "a2").Ok);

        Assert.True(h.Room.Result!.Draw);
        Assert.Equal("fifty", Result(h, "reason"));
        Assert.Contains("50 ходів", Journal(h));
    }

    [Fact]
    public void The_same_position_three_times_is_a_draw()
    {
        var h = Table();
        // Коні туди-сюди: після другого повернення початкова позиція стоїть на дошці втретє.
        foreach (var (seat, from, to) in new[]
                 {
                     (0, "g1", "f3"), (1, "g8", "f6"), (0, "f3", "g1"), (1, "f6", "g8"),
                     (0, "g1", "f3"), (1, "g8", "f6"), (0, "f3", "g1"),
                 })
            Assert.True(Move(h, seat, from, to).Ok);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);

        Assert.True(Move(h, 1, "f6", "g8").Ok);
        Assert.True(h.Room.Result!.Draw);
        Assert.Equal("repetition", Result(h, "reason"));
    }

    // ------------------------------------------------------------------------------------------
    // Піддавки
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void In_antichess_a_capture_leaves_no_other_choice()
    {
        var c = Core("4k3/8/8/3p4/4P3/8/8/4K3 w - - 0 1", ChessVariant.Anti);
        Assert.Single(c.Legal());
        Assert.True(CanGo(c, "e4", "d5"));
    }

    [Fact]
    public void In_antichess_a_quiet_move_is_refused_while_a_capture_is_on_the_board()
    {
        var h = Table("anti");
        Position(h, "4k3/8/8/3p4/4P3/8/8/4K3 w - - 0 1", ChessVariant.Anti);
        var before = Views.Text(h.Room.Game.View(null));

        Assert.Equal("Тут взяття обов'язкове", Move(h, 0, "e1", "e2").Message);
        Assert.Equal(before, Views.Text(h.Room.Game.View(null)));
        Assert.True(Move(h, 0, "e4", "d5").Ok);
    }

    [Fact]
    public void In_antichess_the_player_left_without_pieces_wins()
    {
        var h = Table("anti");
        Position(h, "4k3/8/8/8/8/8/8/r3K3 b - - 0 1", ChessVariant.Anti);
        Assert.Equal(1, h.View(0).GetProperty("turn").GetInt32());

        Assert.True(Move(h, 1, "a1", "e1").Ok);   // чорні мусять узяти короля — і білі лишаються ні з чим
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Equal("anti-nopieces", Result(h, "reason"));
    }

    [Fact]
    public void In_antichess_the_player_with_no_moves_wins()
    {
        var h = Table("anti");
        Position(h, "4k3/7p/8/7P/8/8/8/8 b - - 0 1", ChessVariant.Anti);
        Assert.True(Move(h, 1, "h7", "h6").Ok);   // пішак білих замуровано, ходити їм більше нічим

        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Equal("anti-nomoves", Result(h, "reason"));
        Assert.Contains("ходити нічим", Journal(h));
    }

    [Fact]
    public void In_antichess_there_is_no_castling_and_no_check()
    {
        var castles = Core(EmptyRank, ChessVariant.Anti);
        Assert.Equal(0, Castles(castles));

        var attacked = Core("4k3/8/8/8/8/8/8/4K2r w - - 0 1", ChessVariant.Anti);
        Assert.False(attacked.InCheck());
        Assert.True(CanGo(attacked, "e1", "f1"));   // під бій ходити можна: короля тут не бережуть
    }

    [Fact]
    public void In_antichess_a_pawn_may_become_a_king()
    {
        var c = Core("4k3/P7/8/8/8/8/8/8 w - - 0 1", ChessVariant.Anti);
        Assert.Contains(c.Legal(), m => m.To == ChessCore.Parse("a8") && m.Promo == ChessCore.King);
    }

    // ------------------------------------------------------------------------------------------
    // Шахи Фішера
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Every_fischer_position_is_a_valid_one()
    {
        for (var seed = 1; seed <= 20; seed++)
        {
            var back = ChessCore.Fischer(new Random(seed)).BoardString()[56..];
            Assert.Equal("BBKNNQRR", new string([.. back.Order()]));
            var rooks = back.Select((ch, i) => (ch, i)).Where(p => p.ch == 'R').Select(p => p.i).ToArray();
            var king = back.IndexOf('K');
            Assert.True(rooks[0] < king && king < rooks[1], $"сід {seed}: король не між турами — {back}");

            var bishops = back.Select((ch, i) => (ch, i)).Where(p => p.ch == 'B').Select(p => p.i).ToArray();
            Assert.NotEqual(bishops[0] % 2, bishops[1] % 2);
            Assert.Equal(back.ToLowerInvariant(), ChessCore.Fischer(new Random(seed)).BoardString()[..8]);
        }
    }

    [Theory]
    [InlineData("4k3/8/8/8/8/8/8/2R1K1R1 w GC - 0 1", "e1", "g1", "..R..RK.")]   // звичайна пара
    [InlineData("4k3/8/8/8/8/8/8/6KR w H - 0 1", "g1", "h1", ".....RK.")]        // король лишається на місці
    [InlineData("4k3/8/8/8/8/8/8/1KR5 w C - 0 1", "b1", "c1", ".....RK.")]       // король їде через півдошки
    public void Fischer_castling_puts_both_pieces_on_the_classic_squares(string fen, string king, string rook, string after)
    {
        var c = Core(fen, ChessVariant.Fischer);
        Play(c, king, rook);
        Assert.Equal(after, c.BoardString()[56..]);
    }

    [Fact]
    public void Fischer_castling_is_accepted_as_king_takes_rook_and_as_king_to_g1()
    {
        var h = Table("960");
        Position(h, "4k3/8/8/8/8/8/8/2R1K1R1 w GC - 0 1", ChessVariant.Fischer);
        Assert.True(Move(h, 0, "e1", "g1").Ok);
        Assert.Equal("..R..RK.", h.View(0).GetProperty("board").GetString()![56..]);

        var other = Table("960");
        Position(other, "4k3/8/8/8/8/8/8/2R1K1R1 w GC - 0 1", ChessVariant.Fischer);
        Assert.True(Move(other, 0, "e1", "c1").Ok);
        Assert.Equal("..KR..R.", other.View(0).GetProperty("board").GetString()![56..]);
    }

    [Fact]
    public void Fischer_castling_is_accepted_by_the_castle_field_alone()
    {
        var h = Table("960");
        Position(h, "4k3/8/8/8/8/8/8/2R1K1R1 w GC - 0 1", ChessVariant.Fischer);
        Assert.True(h.Act(0, "move", new { castle = "K" }).Ok);
        Assert.Equal("..R..RK.", h.View(0).GetProperty("board").GetString()![56..]);
    }

    [Fact]
    public void The_same_seed_lays_out_the_same_fischer_position()
    {
        var a = Table("960", seed: 7).View(0).GetProperty("board").GetString();
        var b = Table("960", seed: 7).View(0).GetProperty("board").GetString();
        Assert.Equal(a, b);
        Assert.Equal("960", Table("960").View(0).GetProperty("variant").GetString());
    }

    // ------------------------------------------------------------------------------------------
    // Здатись, нічия, вихід
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Resign_hands_the_win_to_the_other_seat()
    {
        var h = Table();
        Assert.True(h.Act(1, "resign").Ok);

        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Equal("Шахи: Петро здався, Оля 1:0 Петро", Journal(h));
        Assert.Equal("resign", Result(h, "reason"));
    }

    [Fact]
    public void Two_draw_offers_make_a_draw()
    {
        var h = Table();
        Assert.Equal("Запропонував нічию", h.Act(0, "draw").Message);
        Assert.Equal(0, h.View(1).GetProperty("drawOffer").GetInt32());

        // Двічі поспіль пропонувати нічого не дає: чекай відповіді.
        Assert.Equal("Ти вже пропонував нічию", h.Act(0, "draw").Message);
        Assert.Equal(0, h.View(1).GetProperty("drawOffer").GetInt32());
        Assert.Equal(RoomStatus.Playing, h.Room.Status);

        Assert.True(h.Act(1, "draw").Ok);
        Assert.True(h.Room.Result!.Draw);
        Assert.Contains("за згодою", Journal(h));
    }

    [Fact]
    public void A_move_takes_the_draw_offer_back()
    {
        var h = Table();
        h.Act(0, "draw");
        Move(h, 0, "e2", "e4");
        Assert.Equal(JsonValueKind.Null, h.View(1).GetProperty("drawOffer").ValueKind);

        Assert.Equal("Запропонував нічию", h.Act(1, "draw").Message);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void Decline_clears_the_offer_and_needs_one_to_exist()
    {
        var h = Table();
        Assert.Equal("Нічиєї ніхто не пропонував", h.Act(1, "decline").Message);
        h.Act(0, "draw");
        Assert.True(h.Act(1, "decline").Ok);
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("drawOffer").ValueKind);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void Leaving_in_the_middle_of_the_game_is_a_loss()
    {
        var h = Table();
        Move(h, 0, "e2", "e4");
        h.Leave("Петро");

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Contains("встав з-за столу", Journal(h));
    }

    // ------------------------------------------------------------------------------------------
    // Відмови
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_move_out_of_turn_or_against_the_rules_changes_nothing()
    {
        var h = Table();
        var before = Views.Text(h.Room.Game.View(null));

        Assert.Equal("Зараз не твій хід", Move(h, 1, "e7", "e5").Message);
        Assert.Equal("Так не ходять", Move(h, 0, "e2", "e5").Message);
        Assert.Equal("Так не ходять", Move(h, 0, "d1", "d5").Message);
        Assert.Equal("Не зрозумів, куди ходити", h.Act(0, "move", new { from = "z9", to = "e4" }).Message);
        Assert.Equal("Не зрозумів, куди ходити", h.Act(0, "move", new { nope = 1 }).Message);
        Assert.Equal(before, Views.Text(h.Room.Game.View(null)));
    }

    [Fact]
    public void A_move_that_would_leave_the_king_in_check_is_refused()
    {
        var h = Table();
        // Пішак f2 зв'язаний: після f2-f3 ферзь h4 дає мат, а сам хід відкриває шах.
        Position(h, "rnb1kbnr/pppp1ppp/8/4p3/6Pq/5P2/PPPPP2P/RNBQKBNR w KQkq - 0 1");
        Assert.Equal("Так не ходять", Move(h, 0, "f3", "f4").Message);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void An_unknown_action_is_refused()
    {
        var h = Table();
        Assert.Equal("Тут так не ходять", h.Act(0, "jump", new { from = "e2", to = "e4" }).Message);
    }

    // ------------------------------------------------------------------------------------------
    // Вид
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void The_view_has_the_shape_the_spec_asks_for()
    {
        var v = Table().View(0);
        Assert.Equal("classic", v.GetProperty("variant").GetString());
        Assert.Equal(64, v.GetProperty("board").GetString()!.Length);
        Assert.Equal("rnbqkbnrpppppppp................................PPPPPPPPRNBQKBNR", v.GetProperty("board").GetString());
        Assert.StartsWith("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", v.GetProperty("fen").GetString());
        Assert.Equal(0, v.GetProperty("turn").GetInt32());
        Assert.Equal("w", v.GetProperty("toMove").GetString());
        Assert.Equal(20, v.GetProperty("legal").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("lastMove").ValueKind);
        Assert.False(v.GetProperty("check").GetBoolean());
        Assert.Equal("", v.GetProperty("captured").GetProperty("w").GetString());
        Assert.Empty(v.GetProperty("moves").EnumerateArray());
        Assert.Equal(0, v.GetProperty("halfmove").GetInt32());
        Assert.Equal(1, v.GetProperty("fullmove").GetInt32());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("drawOffer").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("result").ValueKind);
    }

    [Fact]
    public void The_spectator_sees_exactly_what_the_players_see()
    {
        var h = Table();
        Move(h, 0, "e2", "e4");
        Assert.Equal(Views.Text(h.Room.Game.View(0)), Views.Text(h.Room.Game.View(null)));
        Assert.Equal(Views.Text(h.Room.Game.View(1)), Views.Text(h.Room.Game.View(null)));
    }

    [Fact]
    public void Every_legal_move_from_the_view_is_a_move_the_server_accepts()
    {
        var h = Table();
        // Це і є перевірка «сервер приймає рівно те, що шле модуль»: беремо запис зі списку і шлемо як є.
        var first = h.View(0).GetProperty("legal")[0];
        var reply = h.Act(0, "move", new { from = first.GetProperty("from").GetString(), to = first.GetProperty("to").GetString() });
        Assert.True(reply.Ok, reply.Message);
        Assert.Equal(1, h.View(0).GetProperty("turn").GetInt32());
    }

    [Fact]
    public void The_legal_list_belongs_to_the_side_to_move_and_dries_up_at_the_end()
    {
        var h = Table();
        Assert.All(h.View(1).GetProperty("legal").EnumerateArray(),
            m => Assert.Contains(m.GetProperty("from").GetString()![1], "12"));   // ходять білі — і фігури білі

        h.Act(0, "resign");
        Assert.Empty(h.View(0).GetProperty("legal").EnumerateArray());
    }

    [Fact]
    public void Castling_shows_up_in_the_legal_list_twice_so_both_squares_are_clickable()
    {
        var h = Table();
        Position(h, EmptyRank);
        var castle = h.View(0).GetProperty("legal").EnumerateArray()
            .Where(m => m.GetProperty("castle").ValueKind != JsonValueKind.Null)
            .Select(m => (m.GetProperty("castle").GetString(), m.GetProperty("to").GetString())).ToArray();
        Assert.Contains(("K", "h1"), castle);
        Assert.Contains(("K", "g1"), castle);
        Assert.Contains(("Q", "a1"), castle);
        Assert.Contains(("Q", "c1"), castle);
    }

    [Fact]
    public void The_browser_names_the_castling_by_word_and_the_server_takes_it()
    {
        // chess.js шле запис зі списку як є: пару полів плюс castle. Обидва поля рокіровки мають спрацювати
        // однаково — і те, де стоїть тура, і звичне поле короля.
        foreach (var to in new[] { "h1", "g1" }) Assert.Equal("R....RK.", CastleByWord("K", to));
        foreach (var to in new[] { "a1", "c1" }) Assert.Equal("..KR...R", CastleByWord("Q", to));
    }

    static string CastleByWord(string side, string to)
    {
        var h = Table();
        Position(h, EmptyRank);
        Assert.True(h.Act(0, "move", new { from = "e1", to, castle = side }).Ok);
        return h.View(0).GetProperty("board").GetString()![56..];
    }

    [Fact]
    public void A_fischer_castle_record_goes_back_to_the_server_exactly_as_it_came()
    {
        // Той самий шлях, але в 960, де король нікуди не їде: єдиний запис зі списку повертаємо цілком.
        var h = Table("960");
        Position(h, "4k3/8/8/8/8/8/8/6KR w H - 0 1", ChessVariant.Fischer);
        var m = h.View(0).GetProperty("legal").EnumerateArray()
            .First(x => x.GetProperty("castle").ValueKind != JsonValueKind.Null);
        var reply = h.Act(0, "move", new
        {
            from = m.GetProperty("from").GetString(),
            to = m.GetProperty("to").GetString(),
            castle = m.GetProperty("castle").GetString(),
        });

        Assert.True(reply.Ok, reply.Message);
        Assert.Equal(".....RK.", h.View(0).GetProperty("board").GetString()![56..]);
    }

    [Fact]
    public void A_fischer_king_that_stays_put_is_offered_only_by_its_rook()
    {
        // Король уже на g1: «поле короля» тут збіглося б із полем, звідки він ходить, — такий запис зайвий.
        var h = Table("960");
        Position(h, "4k3/8/8/8/8/8/8/6KR w H - 0 1", ChessVariant.Fischer);
        var castle = h.View(0).GetProperty("legal").EnumerateArray()
            .Where(m => m.GetProperty("castle").ValueKind != JsonValueKind.Null)
            .Select(m => m.GetProperty("to").GetString() ?? "").ToArray();
        Assert.Equal(["h1"], castle);

        Assert.True(Move(h, 0, "g1", "h1").Ok);
        Assert.Equal(".....RK.", h.View(0).GetProperty("board").GetString()![56..]);
    }

    [Fact]
    public void A_promotion_comes_to_the_browser_as_four_moves_with_letters()
    {
        // Саме з цього модуль розуміє, що треба спитати «у кого перетворити», і що написати на кнопках.
        var h = Table();
        Position(h, "4k3/P7/8/8/8/8/8/4K3 w - - 0 1");
        var promos = h.View(0).GetProperty("legal").EnumerateArray()
            .Where(m => m.GetProperty("to").GetString() == "a8")
            .Select(m => m.GetProperty("promo").GetString() ?? "").Order().ToArray();
        Assert.Equal(["b", "n", "q", "r"], promos);

        var anti = Table("anti");
        Position(anti, "4k3/P7/8/8/8/8/8/8 w - - 0 1", ChessVariant.Anti);
        Assert.Contains("k", anti.View(0).GetProperty("legal").EnumerateArray()
            .Select(m => m.GetProperty("promo").GetString()));
    }

    [Fact]
    public void The_captured_pieces_and_the_move_list_grow_as_the_game_goes()
    {
        var h = Table();
        foreach (var (seat, from, to) in new[] { (0, "e2", "e4"), (1, "d7", "d5"), (0, "e4", "d5") })
            Assert.True(Move(h, seat, from, to).Ok);

        var v = h.View(0);
        Assert.Equal(["e4", "d5", "exd5"], v.GetProperty("moves").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal("p", v.GetProperty("captured").GetProperty("w").GetString());
        Assert.Equal("", v.GetProperty("captured").GetProperty("b").GetString());
        Assert.Equal("e4", v.GetProperty("lastMove").GetProperty("from").GetString());
        Assert.Equal("d5", v.GetProperty("lastMove").GetProperty("to").GetString());
        Assert.Equal(0, v.GetProperty("halfmove").GetInt32());
    }

    [Fact]
    public void A_check_has_to_be_answered()
    {
        var h = Table();
        Position(h, "4k3/7p/8/8/8/8/8/R3K3 w - - 0 1");
        Assert.True(Move(h, 0, "a1", "a8").Ok);
        Assert.True(h.View(1).GetProperty("check").GetBoolean());

        Assert.Equal("Так не ходять", Move(h, 1, "h7", "h6").Message);   // шах не можна ігнорувати
        Assert.True(Move(h, 1, "e8", "e7").Ok);
        Assert.False(h.View(0).GetProperty("check").GetBoolean());
    }

    [Fact]
    public void The_king_under_attack_is_marked_as_check()
    {
        var h = Table();
        Position(h, "4k3/8/8/8/8/8/8/R3K3 w - - 0 1");
        Assert.True(Move(h, 0, "a1", "a8").Ok);
        Assert.True(h.View(1).GetProperty("check").GetBoolean());
        Assert.Equal("Ra8+", h.View(0).GetProperty("moves")[0].GetString());
    }

    [Fact]
    public void San_writes_disambiguation_castling_and_captures_the_way_people_do()
    {
        var c = Core("4k3/8/8/8/8/8/8/R3K2R w KQ - 0 1");
        var legal = c.Legal();
        Assert.Equal("O-O", San(c, legal, "e1", "h1"));
        Assert.Equal("O-O-O", San(c, legal, "e1", "a1"));
        Assert.Equal("Kf1", San(c, legal, "e1", "f1"));

        // Уточнення дописуємо лише тоді, коли на це поле може піти й друга така сама фігура.
        var rooks = Core("4k3/8/4K3/8/8/8/8/R6R w - - 0 1");
        Assert.Equal("Rad1", San(rooks, rooks.Legal(), "a1", "d1"));
        Assert.Equal("Rhd1", San(rooks, rooks.Legal(), "h1", "d1"));

        var knights = Core("4k3/8/8/8/8/2N1N3/8/4K3 w - - 0 1");
        Assert.Equal("Ncd5", San(knights, knights.Legal(), "c3", "d5"));
    }

    static string San(ChessCore c, List<ChessMove> legal, string from, string to)
    {
        int f = ChessCore.Parse(from), t = ChessCore.Parse(to);
        return c.San(legal.First(m => m.From == f && m.To == t), legal);
    }

    // ------------------------------------------------------------------------------------------
    // Рематч, детермінізм, збереження
    // ------------------------------------------------------------------------------------------

    /// <summary>Іспанська партія на двадцять півходів — коротка, зате з обома рокіровками.</summary>
    static readonly (int Seat, string From, string To)[] Ruy =
    [
        (0, "e2", "e4"), (1, "e7", "e5"), (0, "g1", "f3"), (1, "b8", "c6"),
        (0, "f1", "b5"), (1, "a7", "a6"), (0, "b5", "a4"), (1, "g8", "f6"),
        (0, "e1", "g1"), (1, "f8", "e7"), (0, "f1", "e1"), (1, "b7", "b5"),
        (0, "a4", "b3"), (1, "d7", "d6"), (0, "c2", "c3"), (1, "e8", "g8"),
        (0, "h2", "h3"), (1, "c6", "a5"), (0, "b3", "c2"), (1, "c7", "c5"),
    ];

    static RoomHarness PlayRuy(int seed)
    {
        var h = Table(seed: seed);
        foreach (var (seat, from, to) in Ruy) Assert.True(Move(h, seat, from, to).Ok, $"{from}{to}");
        return h;
    }

    [Fact]
    public void The_same_moves_give_the_same_position_every_time()
    {
        Assert.Equal(PlayRuy(1).View(0).GetProperty("fen").GetString(), PlayRuy(999).View(0).GetProperty("fen").GetString());
        Assert.Equal("r1bq1rk1/4bppp/p2p1n2/npp1p3/4P3/2P2N1P/PPBP1PP1/RNBQR1K1 w - c6 0 11",
            PlayRuy(1).View(0).GetProperty("fen").GetString());
    }

    [Fact]
    public void Rematch_deals_a_fresh_board_and_swaps_the_colours()
    {
        var h = Table();
        h.Act(0, "resign");
        Assert.Equal("Оля", h.NickOf(0));

        h.Rematch();
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("Петро", h.NickOf(0));               // місця обернулись — тепер білими грає Петро
        Assert.Equal(0, h.View(0).GetProperty("turn").GetInt32());   // «turn» — це місце, а не колір
        Assert.Empty(h.View(0).GetProperty("moves").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("result").ValueKind);
        Assert.Equal("rnbqkbnrpppppppp................................PPPPPPPPRNBQKBNR",
            h.View(0).GetProperty("board").GetString());
    }

    [Fact]
    public void A_saved_game_comes_back_exactly_as_it_was()
    {
        var played = PlayRuy(3);
        var json = played.Room.Game.Save();
        Assert.NotNull(json);

        var fresh = Table();
        fresh.Room.Game.Load(json!);
        Assert.Equal(Views.Text(played.Room.Game.View(0)), Views.Text(fresh.Room.Game.View(0)));
    }

    [Fact]
    public void A_saved_antichess_game_stays_antichess()
    {
        var h = Table("anti");
        Position(h, "4k3/8/8/3p4/4P3/8/8/4K3 w - - 0 1", ChessVariant.Anti);
        var fresh = Table();
        fresh.Room.Game.Load(h.Room.Game.Save()!);
        Assert.Equal("anti", fresh.View(0).GetProperty("variant").GetString());
        Assert.Single(fresh.View(0).GetProperty("legal").EnumerateArray());
    }

    // ------------------------------------------------------------------------------------------
    // Паспорт гри
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void The_game_passport_says_what_the_lobby_needs()
    {
        var info = new Chess().Info;
        Assert.Equal("chess", info.Id);
        Assert.Equal(GameGroup.Board, info.Group);
        Assert.Equal(2, info.MinPlayers);
        Assert.Equal(2, info.MaxPlayers);
        Assert.True(info.Rated);                       // ставка можлива лише на двох і лише в рейтинговій
        Assert.Equal(0, info.TickMs);
        var option = Assert.Single(info.Options!);
        Assert.Equal("variant", option.Key);
        Assert.Equal("classic", option.Default);
        Assert.Equal(["classic", "960", "anti"], option.Values.Select(v => v.Value));
        Assert.Equal("білі", new Chess().SeatName(0));
        Assert.Equal("чорні", new Chess().SeatName(1));
    }

    [Fact]
    public void An_unknown_variant_falls_back_to_the_classic_one()
    {
        var h = new RoomHarness("chess", options: new { variant = "марсіанські" });
        h.Join("Оля");
        h.Join("Петро");
        Assert.Equal("classic", h.View(0).GetProperty("variant").GetString());
    }
}
