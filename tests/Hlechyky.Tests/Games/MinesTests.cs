using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Сапер-дуель і Сапер дня. Поле в обох детерміноване (дуель — від сіду кімнати, щоденний — від сіду дня),
/// тому тест уміє зібрати таке саме поле в себе («дзеркало») і ходити свідомо, а не навмання (TESTING.md §4).
/// </summary>
public class MinesTests
{
    const string Day = "2026-09-10";   // FakeClock стартує саме з цього київського дня

    // ---------- дзеркала й хелпери ----------

    /// <summary>Таке саме поле, як збере гра: та сама генерація з того самого сіду і тієї самої першої клітинки.</summary>
    static MinesBoard Mirror(int seed, int first, int w = 9, int h = 9, int mines = 10)
    {
        var board = new MinesBoard(w, h, mines);
        board.Generate(new Random(seed), first);
        return board;
    }

    /// <summary>Стіл дуелі з уже зробленим першим ходом (саме він розставляє міни) і дзеркалом поля.</summary>
    static (RoomHarness H, MinesBoard Mirror) Duel(int seed = 7, int first = 40, string size = "9x9-10")
    {
        var h = new RoomHarness("mines", options: new { size }, seed: seed);
        h.Join("Оля");
        h.Join("Петро");
        h.Start();   // стіл на 2–4: «Почати» тисне господар
        var (w, height, mines) = size == "16x16-40" ? (16, 16, 40) : (9, 9, 10);
        var mirror = Mirror(seed, first, w, height, mines);
        h.Act(0, "open", new { cell = first });
        mirror.Open(first);   // дзеркало ходить слідом за грою, щоб тест не перечитував вид на кожну клітинку
        return (h, mirror);
    }

    static string Cells(RoomHarness h, int? seat = null) => h.View(seat).GetProperty("cells").GetString()!;

    static int Turn(RoomHarness h)
    {
        var t = h.View(null).GetProperty("turn");
        return t.ValueKind == JsonValueKind.Number ? t.GetInt32() : -1;
    }

    static int[] Scores(RoomHarness h) =>
        [.. h.View(null).GetProperty("scores").EnumerateArray().Select(e => e.GetInt32())];

    /// <summary>Перша закрита клітинка без міни (за дзеркалом) — щоб ходити свідомо.</summary>
    static int SafeCell(RoomHarness h, MinesBoard mirror, int from = 0)
    {
        var cells = Cells(h);
        for (var c = from; c < mirror.Cells; c++)
            if (!mirror.IsMine(c) && cells[c] == '#') return c;
        throw new InvalidOperationException("безпечних закритих клітинок уже нема");
    }

    static int MineCell(MinesBoard mirror, string cells)
    {
        for (var c = 0; c < mirror.Cells; c++)
            if (mirror.IsMine(c) && cells[c] == '#') return c;
        throw new InvalidOperationException("закритих мін уже нема");
    }

    /// <summary>Розмінувати поле дуелі по черзі: клітинки беремо підряд, міни обходимо.</summary>
    static void ClearDuel(RoomHarness h, MinesBoard mirror)
    {
        var turn = Turn(h);
        for (var c = 0; c < mirror.Cells; c++)
        {
            if (mirror.IsMine(c) || mirror.IsOpen(c)) continue;
            if (h.Room.Status != RoomStatus.Playing) return;
            h.Act(turn, "open", new { cell = c });
            mirror.Open(c);
            turn = 1 - turn;
        }
    }

    // ---------- поле: генерація і відкриття ----------

    [Fact]
    public void Generation_places_exactly_the_asked_number_of_mines()
    {
        foreach (var (w, h, n) in new[] { (9, 9, 10), (16, 16, 40) })
        {
            var board = Mirror(1, 0, w, h, n);
            var text = board.Text(reveal: true);
            Assert.Equal(w * h, text.Length);
            Assert.Equal(n, text.Count(c => c == '*'));
            Assert.Equal(w * h - n, board.Safe);
        }
    }

    [Fact]
    public void The_first_cell_and_its_whole_neighbourhood_stay_clean()
    {
        for (var seed = 1; seed <= 20; seed++)
            foreach (var first in new[] { 0, 8, 40, 80 })
            {
                var board = Mirror(seed, first);
                Assert.False(board.IsMine(first));
                foreach (var nb in board.Around(first)) Assert.False(board.IsMine(nb));
            }
    }

    [Fact]
    public void Neighbour_counts_match_the_mines_around()
    {
        var board = Mirror(3, 40);
        for (var c = 0; c < board.Cells; c++)
            Assert.Equal(board.Around(c).Count(board.IsMine), board.Near(c));
    }

    [Fact]
    public void Around_never_leaves_the_board()
    {
        var board = new MinesBoard(9, 9, 10);
        Assert.Equal(3, board.Around(0).Count());          // кут
        Assert.Equal(5, board.Around(4).Count());          // край
        Assert.Equal(8, board.Around(40).Count());         // середина
        Assert.All(board.Around(80), c => Assert.InRange(c, 0, 80));
    }

    [Fact]
    public void A_zero_opens_its_whole_patch_and_stops_on_the_numbers()
    {
        var board = Mirror(5, 40);
        var zero = Enumerable.Range(0, board.Cells).First(c => !board.IsMine(c) && board.Near(c) == 0);
        var opened = board.Open(zero);

        Assert.True(opened > 1);
        // жодної міни, і кожна відкрита клітинка — або сам нуль, або сусід відкритого нуля
        for (var c = 0; c < board.Cells; c++)
        {
            if (!board.IsOpen(c)) continue;
            Assert.False(board.IsMine(c));
            Assert.True(board.Near(c) == 0 || board.Around(c).Any(n => board.IsOpen(n) && board.Near(n) == 0));
        }
        // а кожен відкритий нуль привів за собою всіх своїх сусідів
        for (var c = 0; c < board.Cells; c++)
            if (board.IsOpen(c) && board.Near(c) == 0)
                Assert.All(board.Around(c), n => Assert.True(board.IsOpen(n)));
        Assert.Equal(board.Safe - board.Opened, board.Left);
    }

    [Fact]
    public void Opening_the_same_cell_twice_changes_nothing()
    {
        var board = Mirror(5, 40);
        var first = board.Open(40);
        Assert.Equal(0, board.Open(40));
        Assert.Equal(first, board.Opened);
    }

    [Fact]
    public void A_flag_goes_on_and_off_but_never_onto_an_open_cell()
    {
        var board = Mirror(5, 40);
        Assert.True(board.Toggle(0));
        Assert.True(board.IsFlag(0));
        Assert.Equal(1, board.Flags);
        Assert.True(board.Toggle(0));
        Assert.False(board.IsFlag(0));
        Assert.Equal(0, board.Flags);

        board.Open(40);
        Assert.False(board.Toggle(40));
    }

    [Fact]
    public void A_cascade_sweeps_the_flags_it_runs_over()
    {
        var board = Mirror(5, 40);
        var zero = Enumerable.Range(0, board.Cells).First(c => !board.IsMine(c) && board.Near(c) == 0);
        var neighbour = board.Around(zero).First();
        board.Toggle(neighbour);
        board.Open(zero);

        Assert.False(board.IsFlag(neighbour));
        Assert.Equal(0, board.Flags);
    }

    [Fact]
    public void An_opened_mine_shows_up_even_when_the_field_is_not_revealed()
    {
        var board = Mirror(5, 40);
        var mine = Enumerable.Range(0, board.Cells).First(board.IsMine);
        board.Open(mine);

        // Каскад мін не відкриває, тож відкрита міна рівно одна — та, на яку наступили. Її й видно,
        // а решта поля лишається закритою: інакше один клік роздавав би всю розкладку.
        var text = board.Text(reveal: false);
        Assert.Equal('*', text[mine]);
        Assert.Equal(1, text.Count(c => c == '*'));
        Assert.Equal(10, board.Text(reveal: true).Count(c => c == '*'));
    }

    [Fact]
    public void The_same_seed_gives_the_same_field_and_a_different_one_does_not()
    {
        Assert.Equal(Mirror(42, 40).Text(true), Mirror(42, 40).Text(true));
        Assert.NotEqual(Mirror(42, 40).Text(true), Mirror(43, 40).Text(true));
    }

    [Fact]
    public void The_mask_survives_a_round_trip()
    {
        var board = Mirror(11, 40);
        board.Open(40);
        board.Toggle(0);
        var text = board.Text(false);

        var copy = Mirror(11, 40);
        copy.Restore(board.Mask());
        Assert.Equal(text, copy.Text(false));
        Assert.Equal(board.Opened, copy.Opened);
        Assert.Equal(board.Flags, copy.Flags);
    }

    // ---------- дуель: правила ----------

    [Fact]
    public void Opening_a_cell_scores_it_and_passes_the_turn()
    {
        var (h, mirror) = Duel();
        var opened = Cells(h).Count(c => c != '#');

        Assert.Equal(opened, Scores(h)[0]);
        Assert.Equal(0, Scores(h)[1]);
        Assert.Equal(1, Turn(h));
        Assert.False(mirror.IsMine(40));
    }

    [Fact]
    public void The_first_click_never_blows_up()
    {
        for (var seed = 1; seed <= 25; seed++)
        {
            var (h, _) = Duel(seed);
            Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("result").ValueKind);
            Assert.Equal(RoomStatus.Playing, h.Room.Status);
        }
    }

    [Fact]
    public void A_flag_does_not_pass_the_turn_and_is_seen_by_both()
    {
        var (h, mirror) = Duel();
        var cell = SafeCell(h, mirror);

        Assert.True(h.Act(1, "flag", new { cell }).Ok);
        Assert.Equal(1, Turn(h));                        // хід лишився в того, хто ставив
        Assert.Equal('F', Cells(h, 0)[cell]);
        Assert.Equal('F', Cells(h, 1)[cell]);
        Assert.True(h.Act(1, "flag", new { cell }).Ok);  // зняли
        Assert.Equal('#', Cells(h, 1)[cell]);
    }

    [Fact]
    public void A_flag_guards_the_cell_from_a_careless_click()
    {
        var (h, mirror) = Duel();
        var cell = SafeCell(h, mirror);
        h.Act(1, "flag", new { cell });

        Assert.Equal("Тут прапорець — спершу зніми його", h.Act(1, "open", new { cell }).Message);
        Assert.Equal('F', Cells(h)[cell]);
    }

    [Fact]
    public void Wrong_turn_open_cell_and_nonsense_are_refused_without_touching_the_board()
    {
        var (h, mirror) = Duel();
        var before = Cells(h);
        var safe = SafeCell(h, mirror);

        Assert.Equal("Зараз не твій хід", h.Act(0, "open", new { cell = safe }).Message);
        Assert.Equal("Тут уже відкрито", h.Act(1, "open", new { cell = 40 }).Message);
        Assert.Equal("Не зрозумів, куди тиснути", h.Act(1, "open", new { nope = 1 }).Message);
        Assert.Equal("Не зрозумів, куди тиснути", h.Act(1, "open", new { cell = 999 }).Message);
        Assert.Equal("Не зрозумів, куди тиснути", h.Act(1, "open", new { cell = -1 }).Message);
        Assert.Equal("Тут так не ходять", h.Act(1, "jump", new { cell = 1 }).Message);

        Assert.Equal(before, Cells(h));
        Assert.Equal(1, Turn(h));
        Assert.Equal(1, h.Room.Moves);   // нелегальний хід ходом не був
    }

    [Fact]
    public void A_bare_number_works_as_a_cell_too()
    {
        var (h, mirror) = Duel();
        var cell = SafeCell(h, mirror);
        Assert.True(h.Act(1, "open", cell).Ok);
        Assert.NotEqual('#', Cells(h)[cell]);
    }

    [Fact]
    public void Stepping_on_a_mine_loses_the_game_at_once()
    {
        var (h, mirror) = Duel();
        var mine = MineCell(mirror, Cells(h));
        var reply = h.Act(1, "open", new { cell = mine });

        Assert.True(reply.Ok);
        Assert.Equal("Бабах. Це була міна", reply.Message);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        var result = h.View(null).GetProperty("result");
        Assert.Equal("boom", result.GetProperty("reason").GetString());
        Assert.Equal(0, result.GetProperty("winner").GetInt32());
        Assert.Contains("наступив на міну", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void The_finished_game_refuses_any_further_move()
    {
        var (h, mirror) = Duel();
        h.Act(1, "open", new { cell = MineCell(mirror, Cells(h)) });
        Assert.Equal("Партію зіграно, тисни «Ще раз»", h.Act(0, "open", new { cell = 0 }).Message);
    }

    [Fact]
    public void Clearing_the_field_compares_the_scores()
    {
        var (h, mirror) = Duel();
        ClearDuel(h, mirror);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var view = h.View(null);
        Assert.Equal(0, view.GetProperty("left").GetInt32());
        Assert.Equal("cleared", view.GetProperty("result").GetProperty("reason").GetString());

        var scores = Scores(h);
        Assert.Equal(mirror.Safe, scores[0] + scores[1]);
        var winner = view.GetProperty("result").GetProperty("winner");
        if (scores[0] == scores[1])
        {
            Assert.True(h.Room.Result!.Draw);
            Assert.Equal(JsonValueKind.Null, winner.ValueKind);
        }
        else
        {
            Assert.Equal(scores[0] > scores[1] ? 0 : 1, winner.GetInt32());
            Assert.Equal([winner.GetInt32()], h.Room.Result!.Winners);
        }
    }

    [Fact]
    public void Equal_scores_end_in_a_draw()
    {
        // Нічия можлива лише на великому полі: на маленькому безпечних клітинок 71, а непарне число
        // порівну не ділиться. Порядок розмінування тут сталий (клітинки підряд), тож лишається знайти
        // сід, на якому рахунок зійшовся — пошук детермінований, не випадковий.
        for (var seed = 1; seed <= 300; seed++)
        {
            var (h, mirror) = Duel(seed, first: 8 * 16 + 8, size: "16x16-40");
            ClearDuel(h, mirror);
            if (h.Room.Status != RoomStatus.Finished) continue;
            var scores = Scores(h);
            if (scores[0] != scores[1]) continue;

            Assert.True(h.Room.Result!.Draw);
            Assert.Empty(h.Room.Result!.Winners);
            Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("result").GetProperty("winner").ValueKind);
            Assert.Contains("порівну", h.Outbox.OfType<Journal>().Last().Text);
            return;
        }
        Assert.Fail("на перших 300 сідах нічия не трапилась — перевірте порядок розмінування");
    }

    [Fact]
    public void Resign_hands_the_win_to_the_other_seat_even_out_of_turn()
    {
        var (h, _) = Duel();
        Assert.Equal(1, Turn(h));
        Assert.True(h.Act(0, "resign").Ok);   // здаємось не в свою чергу

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Room.Result!.Winners);
        Assert.Equal("resign", h.View(null).GetProperty("result").GetProperty("reason").GetString());
        Assert.Contains("здався", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void Leaving_mid_game_is_a_technical_loss()
    {
        var (h, _) = Duel();
        h.Leave("Петро");

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        var result = h.View(null).GetProperty("result");
        Assert.Equal("left", result.GetProperty("reason").GetString());
        Assert.Equal(0, result.GetProperty("winner").GetInt32());
    }

    // ---------- дуель: вид, опції, рематч ----------

    [Fact]
    public void View_matches_the_shape_the_client_expects()
    {
        var (h, _) = Duel();
        var v = h.View(0);

        Assert.Equal(9, v.GetProperty("w").GetInt32());
        Assert.Equal(9, v.GetProperty("h").GetInt32());
        Assert.Equal(10, v.GetProperty("mines").GetInt32());
        Assert.Equal(81, v.GetProperty("cells").GetString()!.Length);
        Assert.Equal(Mines.Seats, v.GetProperty("scores").GetArrayLength());
        Assert.Equal(40, v.GetProperty("lastOpen").GetInt32());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("result").ValueKind);
        Assert.Equal(1, v.GetProperty("turn").GetInt32());
        Assert.True(v.GetProperty("left").GetInt32() > 0);
        // поле спільне — обидва бачать те саме, що й глядач
        Assert.Equal(Views.Text(h.Room.Game.View(0)), Views.Text(h.Room.Game.View(null)));
        Assert.Equal(Views.Text(h.Room.Game.View(0)), Views.Text(h.Room.Game.View(1)));
    }

    [Fact]
    public void The_view_hides_the_mines_until_the_game_ends()
    {
        var (h, mirror) = Duel();
        Assert.DoesNotContain('*', Cells(h, 0));
        Assert.DoesNotContain('*', Cells(h, 1));
        Assert.DoesNotContain('*', Cells(h, null));
        // і в жодному полі виду мін теж нема — вони живуть тільки на сервері
        Assert.DoesNotContain("mine", Views.Text(h.Room.Game.View(0)).Replace("\"mines\":", ""));

        h.Act(1, "open", new { cell = MineCell(mirror, Cells(h)) });
        Assert.Equal(10, Cells(h).Count(c => c == '*'));
    }

    [Fact]
    public void The_big_field_option_gives_sixteen_by_sixteen()
    {
        var (h, _) = Duel(size: "16x16-40", first: 8 * 16 + 8);
        var v = h.View(0);
        Assert.Equal(16, v.GetProperty("w").GetInt32());
        Assert.Equal(16, v.GetProperty("h").GetInt32());
        Assert.Equal(40, v.GetProperty("mines").GetInt32());
        Assert.Equal(256, v.GetProperty("cells").GetString()!.Length);
    }

    [Fact]
    public void A_table_waiting_for_the_second_player_already_shows_the_chosen_field()
    {
        var h = new RoomHarness("mines", options: new { size = "16x16-40" });
        h.Join("Оля");   // суперника ще нема, партія не почалась

        Assert.Equal(RoomStatus.Lobby, h.Room.Status);
        Assert.Equal(16, h.View(0).GetProperty("w").GetInt32());
        Assert.Equal(new string('#', 256), Cells(h, 0));
    }

    [Fact]
    public void An_unknown_size_falls_back_to_the_small_field()
    {
        var h = new RoomHarness("mines", options: new { size = "1000x1000-999" });
        h.Join("Оля");
        h.Join("Петро");
        Assert.Equal(9, h.View(0).GetProperty("w").GetInt32());
        Assert.Equal(10, h.View(0).GetProperty("mines").GetInt32());
    }

    [Fact]
    public void Rematch_gives_a_clean_field_and_swaps_the_seats()
    {
        var (h, mirror) = Duel();
        h.Act(1, "open", new { cell = MineCell(mirror, Cells(h)) });
        Assert.True(h.Rematch("Оля").Ok);

        Assert.Equal("Петро", h.Room.Seats[0]);
        Assert.Equal(new string('#', 81), Cells(h));
        Assert.Equal([0, 0, 0, 0], Scores(h));
        Assert.Equal(0, Turn(h));
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("lastOpen").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("result").ValueKind);
    }

    [Fact]
    public void The_same_seed_and_the_same_moves_give_the_same_view()
    {
        static string Play(int seed)
        {
            var (h, mirror) = Duel(seed);
            for (var i = 0; i < 6 && h.Room.Status == RoomStatus.Playing; i++)
                h.Act(Turn(h), "open", new { cell = SafeCell(h, mirror) });
            return Views.Text(h.Room.Game.View(null));
        }
        Assert.Equal(Play(77), Play(77));
        Assert.NotEqual(Play(77), Play(78));
    }

    [Fact]
    public void Both_sappers_are_in_the_catalog_and_share_one_client_module()
    {
        var catalog = new Registry().Catalog;

        var duel = Assert.Single(catalog, g => g.Id == "mines");
        Assert.Equal("board", duel.Group);
        // на 2–4 Ело не рахується (Rewards.Elo — лише MaxPlayers == 2), тож і рейтинговою її не звемо
        Assert.False(duel.Rated);
        Assert.Equal(2, duel.MinPlayers);
        Assert.Equal(4, duel.MaxPlayers);
        Assert.Equal("byHost", duel.Start);
        Assert.Equal("mines", duel.Module);
        Assert.False(duel.Daily);
        Assert.Equal(3, duel.Options.Single(o => o.Key == "size").Values.Count);
        Assert.Equal("boom", duel.Options.Single(o => o.Key == "mode").Default);

        var daily = Assert.Single(catalog, g => g.Id == "mines-daily");
        Assert.Equal("solo", daily.Group);
        Assert.Equal("immediate", daily.Start);
        Assert.True(daily.Private);
        Assert.True(daily.Daily);
        Assert.Equal(1, daily.MaxPlayers);
        Assert.Equal("mines", daily.Module);   // окремого mines-daily.js не існує
    }

    // ---------- сапер дня ----------

    static RoomHarness DailyRoom(string nick = "Оля")
    {
        var h = new RoomHarness("mines-daily");
        h.Solo(nick);
        return h;
    }

    /// <summary>Таке саме поле дня, як збере гра: сід від дня, центр уже відкритий.</summary>
    static MinesBoard DailyMirror(string day = Day)
    {
        var board = new MinesBoard(MinesDaily.W, MinesDaily.H, MinesDaily.MineCount);
        board.Generate(new Random(Days.Seed("mines", day)), MinesDaily.Center);
        board.Open(MinesDaily.Center);
        return board;
    }

    static void SolveDaily(RoomHarness h, MinesBoard mirror)
    {
        for (var c = 0; c < mirror.Cells && h.Room.Status == RoomStatus.Playing; c++)
            if (!mirror.IsMine(c)) h.Act(0, "open", new { cell = c });
    }

    [Fact]
    public void The_daily_field_comes_open_in_the_centre()
    {
        var h = DailyRoom();
        var v = h.View(0);

        Assert.Equal(16, v.GetProperty("w").GetInt32());
        Assert.Equal(40, v.GetProperty("mines").GetInt32());
        Assert.Equal(Day, v.GetProperty("day").GetString());
        Assert.Equal(1, v.GetProperty("attempts").GetInt32());
        Assert.False(v.GetProperty("solved").GetBoolean());
        Assert.Equal(MinesDaily.Center, v.GetProperty("lastOpen").GetInt32());
        Assert.NotEqual('#', Cells(h, 0)[MinesDaily.Center]);
        Assert.Equal(DailyMirror().Text(false), Cells(h, 0));
    }

    [Fact]
    public void The_solo_key_carries_the_game_and_the_day()
    {
        var h = DailyRoom();
        Assert.Equal($"daily:mines-daily:{Day}:оля", h.Room.Key);
    }

    [Fact]
    public void Everybody_gets_the_same_field_on_the_same_day_and_a_new_one_tomorrow()
    {
        var one = DailyRoom("Оля");
        var two = DailyRoom("Петро");
        Assert.Equal(Cells(one, 0), Cells(two, 0));

        var tomorrow = new RoomHarness("mines-daily");
        tomorrow.Clock.Advance(TimeSpan.FromDays(1));
        tomorrow.Solo("Оля");
        Assert.Equal("2026-09-11", tomorrow.View(0).GetProperty("day").GetString());
        Assert.NotEqual(Cells(one, 0), Cells(tomorrow, 0));
    }

    [Fact]
    public void A_boom_shows_only_the_mine_that_was_stepped_on()
    {
        var h = DailyRoom();
        Assert.DoesNotContain('*', Cells(h, 0));

        var mirror = DailyMirror();
        var mine = MineCell(mirror, Cells(h, 0));
        h.Act(0, "open", new { cell = mine });

        // Розкрити всі сорок означало б подарувати поле дня: «Спробувати ще» дає те саме поле, і
        // навмисний клік по міні перетворив би головоломку на «розмінуй за десять секунд».
        var cells = Cells(h, 0);
        Assert.Equal('*', cells[mine]);
        Assert.Equal(1, cells.Count(c => c == '*'));
    }

    [Fact]
    public void A_retry_gives_the_day_back_unspoiled()
    {
        var h = DailyRoom();
        var mirror = DailyMirror();
        h.Act(0, "open", new { cell = MineCell(mirror, Cells(h, 0)) });
        Assert.True(h.Act(0, "restart").Ok);

        Assert.DoesNotContain('*', Cells(h, 0));
        Assert.Equal(DailyMirror().Text(false), Cells(h, 0));
    }

    [Fact]
    public void The_solved_field_finally_shows_every_mine()
    {
        var h = DailyRoom();
        SolveDaily(h, DailyMirror());
        Assert.Equal(40, Cells(h, 0).Count(c => c == '*'));
    }

    [Fact]
    public void A_boom_keeps_the_room_open_so_the_day_can_be_retried()
    {
        var h = DailyRoom();
        var mirror = DailyMirror();
        var reply = h.Act(0, "open", new { cell = MineCell(mirror, Cells(h, 0)) });

        Assert.True(reply.Ok);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);   // кімнату не закриваємо
        Assert.Equal("boom", h.View(0).GetProperty("result").GetProperty("reason").GetString());
        Assert.Equal("Підірвався. Тисни «Спробувати ще»", h.Act(0, "open", new { cell = 0 }).Message);
        Assert.Empty(h.Scores);
    }

    [Fact]
    public void Restart_counts_the_attempt_and_starts_the_clock_over()
    {
        var h = DailyRoom();
        var mirror = DailyMirror();
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        h.Act(0, "open", new { cell = MineCell(mirror, Cells(h, 0)) });

        Assert.True(h.Act(0, "restart").Ok);
        var v = h.View(0);
        Assert.Equal(2, v.GetProperty("attempts").GetInt32());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("result").ValueKind);
        Assert.True(v.GetProperty("elapsedMs").GetInt64() < 1000);
        // поле те саме: сід від дня, а не від спроби
        Assert.Equal(DailyMirror().Text(false), Cells(h, 0));
    }

    [Fact]
    public void Solving_the_day_records_the_time_and_asks_for_the_daily_reward()
    {
        var h = DailyRoom();
        h.Clock.Advance(TimeSpan.FromSeconds(42));
        SolveDaily(h, DailyMirror());

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var v = h.View(0);
        Assert.True(v.GetProperty("solved").GetBoolean());
        Assert.Equal(0, v.GetProperty("left").GetInt32());
        Assert.Equal(42_000, v.GetProperty("ms").GetInt64());
        Assert.Equal("cleared", v.GetProperty("result").GetProperty("reason").GetString());

        var score = Assert.Single(h.Scores);
        Assert.Equal("mines-daily", score.GameId);
        Assert.Equal(42_000, score.Score);
        Assert.Equal(ScoreOrder.LowerIsBetter, score.Order);
        Assert.Equal($"daily:mines-daily:{Day}:оля", score.Key);

        var award = Assert.Single(h.Awards);
        Assert.Equal("daily:mines-daily", award.Reason);
        Assert.Equal(0, award.Shards);   // нуль — «плати типову щоденну», не «нічого»
        Assert.Contains("розмінував поле дня", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void Solving_after_booms_reports_the_real_attempt_count()
    {
        var h = DailyRoom();
        var mirror = DailyMirror();
        h.Act(0, "open", new { cell = MineCell(mirror, Cells(h, 0)) });
        h.Act(0, "restart");
        h.Act(0, "open", new { cell = MineCell(mirror, Cells(h, 0)) });
        h.Act(0, "restart");
        h.Clock.Advance(TimeSpan.FromSeconds(42));
        SolveDaily(h, DailyMirror());

        var score = Assert.Single(h.Scores);
        Assert.Equal(3, score.Attempts);
        Assert.Equal(42_000, score.Score);   // час — лише вдалої спроби
    }

    [Fact]
    public void A_run_faster_than_a_second_is_still_written_as_milliseconds()
    {
        var h = DailyRoom();
        h.Clock.Advance(TimeSpan.FromMilliseconds(200));
        SolveDaily(h, DailyMirror());

        // значення менше за тисячу щоденні читають як «спроби» (specs/daily.md), тож нижче не падаємо
        Assert.Equal(1000, Assert.Single(h.Scores).Score);
        Assert.Equal(1000, h.View(0).GetProperty("ms").GetInt64());
    }

    [Fact]
    public void A_solved_day_refuses_a_second_run()
    {
        var h = DailyRoom();
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        SolveDaily(h, DailyMirror());

        // кімната вже дограна, тож відмову пише сам каркас
        Assert.False(h.Act(0, "restart").Ok);
        Assert.Single(h.Scores);
    }

    [Fact]
    public void Flags_work_on_the_daily_field_too()
    {
        var h = DailyRoom();
        var closed = Cells(h, 0).IndexOf('#');
        Assert.True(h.Act(0, "flag", new { cell = closed }).Ok);
        Assert.Equal('F', Cells(h, 0)[closed]);
        Assert.Equal("Тут прапорець — спершу зніми його", h.Act(0, "open", new { cell = closed }).Message);
        Assert.True(h.Act(0, "flag", new { cell = closed }).Ok);
        Assert.Equal('#', Cells(h, 0)[closed]);
    }

    [Fact]
    public void Nonsense_on_the_daily_field_is_refused()
    {
        var h = DailyRoom();
        Assert.Equal("Тут так не ходять", h.Act(0, "jump", new { cell = 0 }).Message);
        Assert.Equal("Не зрозумів, куди тиснути", h.Act(0, "open", new { cell = 999 }).Message);
        Assert.Equal("Тут уже відкрито", h.Act(0, "open", new { cell = MinesDaily.Center }).Message);
    }

    [Fact]
    public void Load_of_Save_gives_an_equivalent_view()
    {
        var h = DailyRoom();
        var mirror = DailyMirror();
        h.Clock.Advance(TimeSpan.FromSeconds(9));
        for (int c = 0, done = 0; c < mirror.Cells && done < 5; c++)
            if (!mirror.IsMine(c) && Cells(h, 0)[c] == '#' && h.Act(0, "open", new { cell = c }).Ok) done++;
        h.Act(0, "flag", new { cell = MineCell(mirror, Cells(h, 0)) });

        var saved = h.Room.Game.Save()!;
        var fresh = new MinesDaily { Ctx = h.Room.Game.Ctx };
        fresh.Start();
        fresh.Load(saved);

        Assert.Equal(Views.Text(h.Room.Game.View(0)), Views.Text(fresh.View(0)));
    }

    [Fact]
    public void The_framework_saves_after_every_move_and_brings_the_field_back()
    {
        var h = DailyRoom();
        var mirror = DailyMirror();
        h.Act(0, "open", new { cell = SafeCell(h, mirror) });
        h.Act(0, "flag", new { cell = MineCell(mirror, Cells(h, 0)) });
        var was = Cells(h, 0);
        var key = h.Room.Key!;
        Assert.True(h.Store.States.ContainsKey(key));

        h.Leave("Оля");                 // закрив вкладку — соло-кімнати вже нема
        h.Solo("Оля");                  // відкрив знову того самого дня
        Assert.Equal(was, Cells(h, 0));
        Assert.Equal(1, h.View(0).GetProperty("attempts").GetInt32());
    }

    [Fact]
    public void A_state_from_another_day_is_ignored()
    {
        var h = DailyRoom();
        var game = (MinesDaily)h.Room.Game;
        var clean = Views.Text(game.View(0));

        game.Load("""{"Day":"2020-01-01","Mask":"","StartedAt":"2020-01-01T00:00:00+00:00","Attempts":9,"Solved":true,"Dead":false,"Ms":1,"Last":null}""");
        Assert.Equal(clean, Views.Text(game.View(0)));

        game.Load("це не json");
        Assert.Equal(clean, Views.Text(game.View(0)));
    }

    // ---------- компанія: 3–4 сапери ----------

    static readonly string[] Crew = ["Оля", "Петро", "Іра", "Марко"];

    /// <summary>Стіл на n із першим ходом місця 0 і дзеркалом поля; mode — «boom» або «hunt».</summary>
    static (RoomHarness H, MinesBoard Mirror) Company(int n, int seed = 7, int first = 40, string mode = "boom", string size = "9x9-10")
    {
        var h = new RoomHarness("mines", options: new { size, mode }, seed: seed);
        foreach (var nick in Crew.Take(n)) h.Join(nick);
        h.Start();
        var (w, height, careful, hunt) = size switch
        {
            "16x16-40" => (16, 16, 40, 51),
            "12x12-22" => (12, 12, 22, 29),
            _ => (9, 9, 10, 15),
        };
        var mirror = Mirror(seed, first, w, height, mode == "hunt" ? hunt : careful);
        h.Act(0, "open", new { cell = first });
        mirror.Open(first);
        return (h, mirror);
    }

    static string? OutOf(RoomHarness h, int seat)
    {
        var e = h.View(null).GetProperty("out")[seat];
        return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }

    [Fact]
    public void Four_sappers_take_turns_round_the_table()
    {
        var (h, mirror) = Company(4);
        Assert.Equal([0, 1, 2, 3], h.View(null).GetProperty("players").EnumerateArray().Select(e => e.GetInt32()));
        foreach (var expected in new[] { 1, 2, 3, 0, 1 })
        {
            Assert.Equal(expected, Turn(h));
            var cell = SafeCell(h, mirror);
            Assert.True(h.Act(expected, "open", new { cell }).Ok);
            mirror.Open(cell);
            if (h.Room.Status != RoomStatus.Playing) return;
        }
    }

    [Fact]
    public void A_boom_in_a_company_drops_only_the_one_who_stepped()
    {
        var (h, mirror) = Company(3);
        var reply = h.Act(1, "open", new { cell = MineCell(mirror, Cells(h)) });

        Assert.Equal("Бабах. Ти вибув — дивись, хто кого", reply.Message);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("boom", OutOf(h, 1));
        Assert.Equal(2, Turn(h));
        Assert.Single(Cells(h), c => c == '*');            // видно лише ту міну, на яку наступили
        Assert.Equal("Ти вже вибув — лишається дивитись", h.Act(1, "flag", new { cell = 0 }).Message);

        var cell = SafeCell(h, mirror);
        h.Act(2, "open", new { cell });
        Assert.Equal(0, Turn(h));                             // вибулого черга оминає
    }

    [Fact]
    public void The_last_survivor_wins_the_careful_game()
    {
        var (h, mirror) = Company(3);
        h.Act(1, "open", new { cell = MineCell(mirror, Cells(h)) });
        var mine = MineCell(mirror, Cells(h));
        Assert.Equal("Бабах. Це була міна", h.Act(2, "open", new { cell = mine }).Message);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        var result = h.View(null).GetProperty("result");
        Assert.Equal("boom", result.GetProperty("reason").GetString());
        Assert.Equal([0, 2, 1], result.GetProperty("places").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Contains("наступив на міну", h.Room.Result.Text);
    }

    [Fact]
    public void A_cleared_field_ranks_only_those_still_standing()
    {
        var (h, mirror) = Company(3, seed: 11);
        h.Act(1, "open", new { cell = MineCell(mirror, Cells(h)) });   // зелений вибув одразу
        var turn = Turn(h);
        for (var c = 0; c < mirror.Cells && h.Room.Status == RoomStatus.Playing; c++)
        {
            if (mirror.IsMine(c) || mirror.IsOpen(c)) continue;
            h.Act(turn, "open", new { cell = c });
            mirror.Open(c);
            turn = Turn(h);
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("cleared", h.View(null).GetProperty("result").GetProperty("reason").GetString());
        Assert.DoesNotContain(1, h.Room.Result!.Winners);
        var e = Assert.Single(h.Finished);
        Assert.Equal(3, e.Result.Scores!.Count);
    }

    [Fact]
    public void Resigning_in_a_company_lets_the_rest_play_on()
    {
        var (h, _) = Company(3);
        Assert.Equal(1, Turn(h));
        Assert.Equal("Здався. Дивись, хто кого", h.Act(1, "resign").Message);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("resign", OutOf(h, 1));
        Assert.Equal(2, Turn(h));
        Assert.Equal("Ти вже вибув — лишається дивитись", h.Act(1, "resign").Message);

        h.Act(2, "resign");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
    }

    [Fact]
    public void Leaving_a_company_does_not_break_the_game()
    {
        var (h, _) = Company(4);
        Assert.Equal(1, Turn(h));
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("left", OutOf(h, 1));
        Assert.Equal(2, Turn(h));
        Assert.Contains(h.Outbox.OfType<Journal>(), j => j.Text.Contains("встав з-за столу, решта грають далі"));
    }

    [Fact]
    public void A_sapper_already_out_can_leave_quietly()
    {
        var (h, mirror) = Company(3);
        h.Act(1, "open", new { cell = MineCell(mirror, Cells(h)) });
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("boom", OutOf(h, 1));
    }

    [Fact]
    public void A_company_rematch_gives_everybody_a_clean_start()
    {
        var (h, mirror) = Company(3);
        h.Act(1, "open", new { cell = MineCell(mirror, Cells(h)) });
        h.Act(2, "open", new { cell = MineCell(mirror, Cells(h)) });
        Assert.True(h.Rematch("Іра").Ok);

        Assert.Equal(new string('#', 81), Cells(h));
        Assert.Equal([0, 0, 0, 0], Scores(h));
        for (var s = 0; s < 3; s++) Assert.Null(OutOf(h, s));
        Assert.Equal(0, Turn(h));
    }

    [Fact]
    public void Two_sappers_on_a_four_seat_table_play_the_old_duel()
    {
        var h = new RoomHarness("mines", seed: 7);
        h.Join("Оля"); h.Join("Петро"); h.Join("Іра");
        h.Leave("Петро");
        h.Start();
        var mirror = Mirror(7, 40);
        h.Act(0, "open", new { cell = 40 });
        mirror.Open(40);
        Assert.Equal(2, Turn(h));
        h.Act(2, "open", new { cell = MineCell(mirror, Cells(h)) });
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Contains("Іра глиняний наступив на міну, Оля жовтий виграв", h.Room.Result.Text);
    }

    [Fact]
    public void The_middle_field_is_twelve_by_twelve()
    {
        var (h, _) = Company(2, first: 6 * 12 + 6, size: "12x12-22");
        var v = h.View(0);
        Assert.Equal(12, v.GetProperty("w").GetInt32());
        Assert.Equal(22, v.GetProperty("mines").GetInt32());
        Assert.Equal(144, Cells(h).Length);
    }

    // ---------- мисливці ----------

    [Fact]
    public void Hunters_play_on_a_denser_field()
    {
        foreach (var (size, first, mines) in new[] { ("9x9-10", 40, 15), ("12x12-22", 78, 29), ("16x16-40", 136, 51) })
        {
            var (h, _) = Company(2, first: first, mode: "hunt", size: size);
            Assert.Equal(mines, h.View(0).GetProperty("mines").GetInt32());
            Assert.Equal("hunt", h.View(0).GetProperty("mode").GetString());
        }
    }

    [Fact]
    public void A_hunter_scores_a_mine_and_shoots_again()
    {
        var (h, mirror) = Company(2, mode: "hunt");
        Assert.Equal([0, 0, 0, 0], Scores(h));             // відкриті клітинки мисливцям очок не дають
        Assert.Equal(1, Turn(h));

        var mine = MineCell(mirror, Cells(h));
        var reply = h.Act(1, "open", new { cell = mine });
        Assert.StartsWith("Міна твоя!", reply.Message);
        Assert.Equal(1, Scores(h)[1]);
        Assert.Equal(1, Turn(h));                           // знайшов міну — ходиш ще
        Assert.Equal('*', Cells(h)[mine]);
        Assert.Equal('1', h.View(null).GetProperty("owners").GetString()![mine]);
        Assert.Equal(14, h.View(null).GetProperty("unclaimed").GetInt32());

        var safe = SafeCell(h, mirror);
        h.Act(1, "open", new { cell = safe });
        Assert.Equal(0, Turn(h));                           // порожня клітинка — хід далі
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void The_hunt_ends_as_soon_as_nobody_can_catch_the_leader()
    {
        var (h, mirror) = Company(2, mode: "hunt");
        h.Act(1, "open", new { cell = SafeCell(h, mirror) });   // хід переходить до жовтого
        for (var i = 0; i < 7; i++)
        {
            h.Act(0, "open", new { cell = MineCell(mirror, Cells(h)) });
            Assert.Equal(RoomStatus.Playing, h.Room.Status);   // 7 проти 0 при восьми, що лишились, — ще не вирішено
        }
        h.Act(0, "open", new { cell = MineCell(mirror, Cells(h)) });   // 8:0, а лишилось 7 — усе

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        var result = h.View(null).GetProperty("result");
        Assert.Equal("hunted", result.GetProperty("reason").GetString());
        Assert.Contains("полювання скінчено", h.Room.Result.Text);
        Assert.Equal(15, Cells(h).Count(c => c == '*'));   // після кінця видно всі міни
    }

    [Fact]
    public void The_hunt_view_is_the_same_for_every_seat_and_hides_unclaimed_mines()
    {
        var (h, mirror) = Company(3, mode: "hunt");
        h.Act(1, "open", new { cell = MineCell(mirror, Cells(h)) });
        Assert.Single(Cells(h), c => c == '*');
        Assert.Equal(Views.Text(h.Room.Game.View(0)), Views.Text(h.Room.Game.View(null)));
        Assert.Equal(Views.Text(h.Room.Game.View(2)), Views.Text(h.Room.Game.View(null)));
    }

    [Fact]
    public void The_careful_view_has_no_owners()
    {
        var (h, _) = Company(2);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("owners").ValueKind);
        Assert.Equal("boom", h.View(null).GetProperty("mode").GetString());
    }

    // ---------- сапер дня: тиск по числу ----------

    /// <summary>Відкрите число поля дня, довкола якого є і закриті міни, і закриті безпечні клітинки.</summary>
    static (int Cell, int[] Mines, int[] Safe) ChordSpot(MinesBoard mirror, Func<int, int[], int[], bool> want)
    {
        for (var c = 0; c < mirror.Cells; c++)
        {
            if (!mirror.IsOpen(c) || mirror.Near(c) == 0) continue;
            var closed = mirror.Around(c).Where(n => !mirror.IsOpen(n)).ToArray();
            var mines = closed.Where(mirror.IsMine).ToArray();
            var safe = closed.Where(n => !mirror.IsMine(n)).ToArray();
            if (want(mirror.Near(c), mines, safe)) return (c, mines, safe);
        }
        throw new InvalidOperationException("на полі дня нема потрібного числа");
    }

    [Fact]
    public void Pressing_a_number_with_its_flags_in_place_opens_the_rest_around_it()
    {
        var h = DailyRoom();
        var (cell, mines, safe) = ChordSpot(DailyMirror(), (_, m, s) => m.Length > 0 && s.Length > 0);
        foreach (var m in mines) h.Act(0, "flag", new { cell = m });

        Assert.True(h.Act(0, "open", new { cell }).Ok);
        var cells = Cells(h, 0);
        Assert.All(safe, c => Assert.NotEqual('#', cells[c]));
        Assert.All(mines, c => Assert.Equal('F', cells[c]));
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("result").ValueKind);
    }

    [Fact]
    public void Pressing_a_number_without_enough_flags_is_refused()
    {
        var h = DailyRoom();
        var (cell, _, _) = ChordSpot(DailyMirror(), (_, m, s) => m.Length > 0 && s.Length > 0);
        var before = Cells(h, 0);

        var r = h.Act(0, "open", new { cell });
        Assert.False(r.Ok);
        Assert.StartsWith("Довкола має стояти прапорців", r.Message);
        Assert.Equal(before, Cells(h, 0));
        Assert.Equal("Тут уже відкрито", h.Act(0, "open", new { cell = MinesDaily.Center }).Message);   // нуль тиснути нема чого
    }

    [Fact]
    public void A_wrong_flag_under_a_pressed_number_blows_up()
    {
        var h = DailyRoom();
        var mirror = DailyMirror();
        var (cell, mines, safe) = ChordSpot(mirror, (n, m, s) => m.Length > 0 && s.Length >= n);
        var n = mirror.Near(cell);
        foreach (var c in safe.Take(n)) h.Act(0, "flag", new { cell = c });

        var r = h.Act(0, "open", new { cell });
        Assert.Equal("Бабах. Один прапорець стояв не там", r.Message);
        Assert.Equal("boom", h.View(0).GetProperty("result").GetProperty("reason").GetString());
        Assert.Contains(mines, m => Cells(h, 0)[m] == '*');
        Assert.Equal(RoomStatus.Playing, h.Room.Status);   // день не зіпсовано — «Спробувати ще» працює
    }

    [Fact]
    public void In_the_duel_a_pressed_number_is_still_just_an_open_cell()
    {
        var (h, _) = Duel();
        var cells = Cells(h);
        var number = Enumerable.Range(0, cells.Length).First(c => cells[c] is >= '1' and <= '8');
        Assert.Equal("Тут уже відкрито", h.Act(1, "open", new { cell = number }).Message);
    }
}
