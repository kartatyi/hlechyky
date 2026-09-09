using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Доміно: роздача, прикладання, базар, підсумки раунду й партія до ста очок. Роздача випадкова, тому
/// правила ходів перевіряємо на точних розкладках — їх ставимо через <c>Save/Load</c>, а не ганяємо
/// сіди доти, доки випаде потрібна рука (TESTING.md §4).
/// </summary>
public class DominoTests
{
    static readonly string[] Nicks = ["Оля", "Петро", "Іван", "Ната"];

    static RoomHarness Table(int players = 2, int seed = 1)
    {
        var h = new RoomHarness("domino", seed: seed);
        for (var i = 0; i < players; i++) h.Join(Nicks[i]);
        h.Start();
        return h;
    }

    /// <summary>Точна позиція за столом: ланцюг, руки, базар, чия черга й рахунок партії.</summary>
    static void Position(RoomHarness h, int[][] line, int[][][] hands,
        int[][]? yard = null, int turn = 0, int[]? scores = null, int round = 1)
    {
        var state = new
        {
            Line = line,
            Yard = yard ?? [],
            Hands = hands,
            In = Enumerable.Range(0, 4).Select(s => h.Room.Seats[s] is not null).ToArray(),
            Scores = scores ?? new int[4],
            Turn = turn,
            Round = round,
            Winner = (int?)null,
            LastWinner = (int?)null,
            LastRoundWinner = (int?)null,
            LastPoints = 0,
            LastReason = "",
        };
        h.Room.Game.Load(JsonSerializer.Serialize(state));
    }

    static ActResult Play(RoomHarness h, int seat, int a, int b, string? end = null) =>
        h.Act(seat, "play", end is null ? new { tile = new[] { a, b } } : (object)new { tile = new[] { a, b }, end });

    static int[][] Hand(RoomHarness h, int seat) =>
        [.. h.View(seat).GetProperty("hand").EnumerateArray().Select(t => t.EnumerateArray().Select(x => x.GetInt32()).ToArray())];

    static int[][] Line(RoomHarness h, int? seat = 0) =>
        [.. h.View(seat).GetProperty("line").EnumerateArray()
            .Select(x => x.GetProperty("tile").EnumerateArray().Select(n => n.GetInt32()).ToArray())];

    static int[] Counts(RoomHarness h, int? seat = 0) =>
        [.. h.View(seat).GetProperty("counts").EnumerateArray().Select(x => x.GetInt32())];

    static int[] Scores(RoomHarness h, int? seat = 0) =>
        [.. h.View(seat).GetProperty("scores").EnumerateArray().Select(x => x.GetInt32())];

    static int Yard(RoomHarness h, int? seat = 0) => h.View(seat).GetProperty("boneyard").GetInt32();

    // ---------- роздача ----------

    [Fact]
    public void A_round_uses_all_twenty_eight_bones_and_never_repeats_one()
    {
        var h = Table();
        var save = JsonDocument.Parse(h.Room.Game.Save()!).RootElement;

        var all = new List<string>();
        void Take(JsonElement arr) => all.AddRange(arr.EnumerateArray()
            .Select(t => string.Join("-", t.EnumerateArray().Select(n => n.GetInt32()).Order())));
        Take(save.GetProperty("Line"));
        Take(save.GetProperty("Yard"));
        foreach (var hand in save.GetProperty("Hands").EnumerateArray()) Take(hand);

        Assert.Equal(28, all.Count);
        Assert.Equal(28, all.Distinct().Count());
    }

    [Fact]
    public void Two_players_take_seven_bones_each_and_the_rest_waits_in_the_boneyard()
    {
        var h = Table();
        Assert.Equal([7, 7, 0, 0], Counts(h));
        Assert.Equal(14, Yard(h));
        Assert.Equal(2, h.View(0).GetProperty("players").GetInt32());
    }

    [Fact]
    public void Three_and_four_players_take_five_bones_each()
    {
        var three = Table(3);
        Assert.Equal([5, 5, 5, 0], Counts(three));
        Assert.Equal(13, Yard(three));

        var four = Table(4);
        Assert.Equal([5, 5, 5, 5], Counts(four));
        Assert.Equal(8, Yard(four));
        Assert.Equal(4, four.View(0).GetProperty("players").GetInt32());
    }

    [Fact]
    public void The_highest_double_opens_the_round()
    {
        // Роздача випадкова, тому дивимось не на конкретний сід, а на саме правило: у того, хто починає,
        // мусить бути найстарший дубль зі столу.
        for (var seed = 1; seed <= 12; seed++)
        {
            var h = Table(seed: seed);
            var doubles = new List<(int Seat, int Pip)>();
            for (var seat = 0; seat < 2; seat++)
                doubles.AddRange(Hand(h, seat).Where(t => t[0] == t[1]).Select(t => (seat, t[0])));
            if (doubles.Count == 0) continue;

            var top = doubles.MaxBy(d => d.Pip);
            Assert.Equal(top.Seat, h.View(0).GetProperty("turn").GetInt32());
        }
    }

    [Fact]
    public void Without_any_double_the_highest_bone_opens_the_round()
    {
        // Сід 30 роздає на двох чотирнадцять кісток без жодного дубля; найстарша з них — 6-3, і вона в Олі.
        var h = Table(seed: 30);
        var all = Hand(h, 0).Select(t => (Seat: 0, Tile: t)).Concat(Hand(h, 1).Select(t => (Seat: 1, Tile: t))).ToList();
        Assert.DoesNotContain(all, x => x.Tile[0] == x.Tile[1]);

        var top = all.MaxBy(x => x.Tile[0] + x.Tile[1] + x.Tile.Max() / 10.0);
        Assert.Equal([6, 3], top.Tile);
        Assert.Equal(top.Seat, h.View(0).GetProperty("turn").GetInt32());
    }

    // ---------- прикладання ----------

    [Fact]
    public void A_bone_joins_the_line_with_the_matching_half_forward()
    {
        var h = Table();
        Position(h, [[6, 3]], [[[3, 1], [6, 6]], [[6, 5]]], yard: [[0, 0]]);

        Assert.True(Play(h, 0, 1, 3, "right").Ok);
        Assert.Equal([[6, 3], [3, 1]], Line(h));
        Assert.Equal([6, 1], h.View(0).GetProperty("ends").EnumerateArray().Select(x => x.GetInt32()));
    }

    [Fact]
    public void The_end_decides_which_side_the_bone_goes_to()
    {
        var left = Table();
        Position(left, [[3, 3]], [[[3, 1], [6, 6]], [[6, 5]]], yard: [[0, 0]]);
        Assert.True(Play(left, 0, 3, 1, "left").Ok);
        Assert.Equal([[1, 3], [3, 3]], Line(left));

        var right = Table();
        Position(right, [[3, 3]], [[[3, 1], [6, 6]], [[6, 5]]], yard: [[0, 0]]);
        Assert.True(Play(right, 0, 3, 1, "right").Ok);
        Assert.Equal([[3, 3], [3, 1]], Line(right));
    }

    [Fact]
    public void A_bone_that_fits_neither_end_is_refused_and_the_table_stays_as_it_was()
    {
        var h = Table();
        Position(h, [[3, 2]], [[[6, 5], [4, 1]], [[6, 0]]], yard: [[0, 0]]);
        var before = Views.Text(h.Room.Game.View(0));

        Assert.Equal("Ця кістка сюди не підходить", Play(h, 0, 6, 5).Message);
        Assert.Equal(before, Views.Text(h.Room.Game.View(0)));
        Assert.Equal(0, h.Room.Moves);
    }

    [Fact]
    public void A_bone_you_do_not_have_is_refused()
    {
        var h = Table();
        Position(h, [[3, 2]], [[[6, 5]], [[3, 0]]], yard: [[0, 0]]);
        Assert.Equal("Такої кістки в тебе нема", Play(h, 0, 3, 0).Message);
    }

    [Fact]
    public void Playing_out_of_turn_is_refused()
    {
        var h = Table();
        Position(h, [[3, 2]], [[[6, 5]], [[3, 0]]], yard: [[0, 0]]);
        Assert.Equal("Зараз не твій хід", Play(h, 1, 3, 0).Message);
    }

    [Fact]
    public void Unknown_action_is_refused()
    {
        var h = Table();
        Assert.Equal("Тут так не ходять", h.Act(0, "jump", new { tile = new[] { 1, 1 } }).Message);
    }

    [Fact]
    public void A_broken_payload_is_refused_without_touching_the_table()
    {
        var h = Table();
        Position(h, [[3, 2]], [[[6, 5]], [[3, 0]]], yard: [[0, 0]]);
        var before = Views.Text(h.Room.Game.View(0));

        Assert.Equal("Не зрозумів, яку кістку класти", h.Act(0, "play", new { nope = 1 }).Message);
        Assert.Equal("Не зрозумів, яку кістку класти", h.Act(0, "play", new { tile = new[] { 9, 9 } }).Message);
        Assert.Equal("Не зрозумів, яку кістку класти", h.Act(0, "play", new { tile = new[] { 1 } }).Message);
        Assert.Equal(before, Views.Text(h.Room.Game.View(0)));
    }

    [Fact]
    public void A_bone_that_fits_both_ends_may_come_without_a_side()
    {
        var h = Table();
        Position(h, [[3, 3]], [[[3, 1], [6, 6]], [[6, 5]]], yard: [[0, 0]]);
        Assert.True(Play(h, 0, 3, 1).Ok);
        Assert.Equal([[3, 3], [3, 1]], Line(h));
    }

    // ---------- базар і пас ----------

    [Fact]
    public void Drawing_takes_one_bone_and_keeps_the_turn()
    {
        var h = Table();
        Position(h, [[0, 3]], [[[6, 6]], [[6, 5]]], yard: [[3, 1], [2, 2]]);

        Assert.True(h.Act(0, "draw").Ok);
        Assert.Equal([2, 1, 0, 0], Counts(h));
        Assert.Equal(1, Yard(h));
        Assert.Equal(0, h.View(0).GetProperty("turn").GetInt32());
        Assert.True(h.View(0).GetProperty("mustDraw").GetBoolean());

        Assert.True(h.Act(0, "draw").Ok);
        Assert.Equal(0, Yard(h));
        Assert.True(h.View(0).GetProperty("canPlay").GetBoolean());
        Assert.False(h.View(0).GetProperty("mustDraw").GetBoolean());
    }

    [Fact]
    public void Drawing_while_you_have_a_move_is_refused()
    {
        var h = Table();
        Position(h, [[0, 3]], [[[3, 1]], [[6, 5]]], yard: [[2, 2]]);
        Assert.Equal("Є чим ходити", h.Act(0, "draw").Message);
        Assert.Equal(1, Yard(h));
    }

    [Fact]
    public void Drawing_from_an_empty_boneyard_is_refused()
    {
        var h = Table();
        Position(h, [[0, 3]], [[[6, 6]], [[6, 5], [5, 4]]]);
        Assert.Equal("Базар порожній, лишається пас", h.Act(0, "draw").Message);
    }

    [Fact]
    public void Passing_while_the_boneyard_is_not_empty_is_refused()
    {
        var h = Table();
        Position(h, [[0, 3]], [[[6, 6]], [[6, 5]]], yard: [[2, 2]]);
        Assert.Equal("У базарі ще є кістки — тягни", h.Act(0, "pass").Message);
    }

    [Fact]
    public void Passing_while_you_still_have_a_move_is_refused()
    {
        var h = Table();
        Position(h, [[0, 3]], [[[3, 1]], [[6, 5]]]);
        Assert.Equal("Є чим ходити", h.Act(0, "pass").Message);
    }

    [Fact]
    public void Passing_hands_the_turn_over_without_spamming_the_journal()
    {
        var h = Table();
        Position(h, [[0, 3]], [[[6, 6]], [[3, 2]]]);
        var before = h.Outbox.OfType<Journal>().Count();

        Assert.Equal("Пас: ходити нема чим", h.Act(0, "pass").Message);
        Assert.Equal(1, h.View(0).GetProperty("turn").GetInt32());
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        // Пас буває по три поспіль, а Журнал спільний на весь сайт — туди він не йде.
        Assert.Equal(before, h.Outbox.OfType<Journal>().Count());
    }

    // ---------- підсумок раунду ----------

    [Fact]
    public void Going_out_takes_the_pips_left_in_every_other_hand()
    {
        var h = Table();
        Position(h, [[6, 3]], [[[3, 3]], [[5, 4], [2, 1]]], yard: [[0, 0]]);

        Assert.True(Play(h, 0, 3, 3, "right").Ok);
        Assert.Equal([12, 0, 0, 0], Scores(h));

        var last = h.View(0).GetProperty("lastRound");
        Assert.Equal(0, last.GetProperty("winner").GetInt32());
        Assert.Equal(12, last.GetProperty("points").GetInt32());
        Assert.Equal("out", last.GetProperty("reason").GetString());
        Assert.Contains("рука порожня, +12", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void A_blocked_round_gives_the_difference_to_the_lighter_hand()
    {
        var h = Table();
        // Кінці 1 і 2: ходити не може ніхто, базар порожній. Оля тримає 12, Петро — 18.
        Position(h, [[1, 0], [0, 2]], [[[6, 6]], [[5, 5], [4, 4]]]);

        Assert.True(h.Act(0, "pass").Ok);
        Assert.Equal([6, 0, 0, 0], Scores(h));

        var last = h.View(0).GetProperty("lastRound");
        Assert.Equal(0, last.GetProperty("winner").GetInt32());
        Assert.Equal("fish", last.GetProperty("reason").GetString());
        Assert.Equal(2, h.View(0).GetProperty("round").GetInt32());
    }

    [Fact]
    public void A_blocked_round_with_equal_hands_gives_nothing_to_anybody()
    {
        var h = Table();
        Position(h, [[0, 3]], [[[6, 6]], [[5, 5], [1, 1]]]);

        Assert.True(h.Act(0, "pass").Ok);
        Assert.Equal([0, 0, 0, 0], Scores(h));

        var last = h.View(0).GetProperty("lastRound");
        Assert.Equal(JsonValueKind.Null, last.GetProperty("winner").ValueKind);
        Assert.Equal(0, last.GetProperty("points").GetInt32());
        Assert.Contains("очки нікому", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void The_next_round_is_dealt_at_once_and_opened_by_the_winner()
    {
        var h = Table();
        Position(h, [[6, 3]], [[[3, 3]], [[5, 4], [2, 1]]], yard: [[0, 0]]);
        Assert.True(Play(h, 0, 3, 3, "right").Ok);

        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(2, h.View(0).GetProperty("round").GetInt32());
        Assert.Equal([7, 7, 0, 0], Counts(h));
        Assert.Empty(Line(h));
        Assert.Equal(0, h.View(0).GetProperty("turn").GetInt32());
    }

    [Fact]
    public void A_hundred_points_ends_the_match_and_writes_the_score_into_the_journal()
    {
        var h = Table();
        Position(h, [[6, 3]], [[[3, 3]], [[5, 4], [2, 1]]], yard: [[0, 0]], scores: [95, 0, 0, 0], round: 4);

        Assert.True(Play(h, 0, 3, 3, "right").Ok);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Equal("Доміно: Оля 107 : Петро 0", h.Outbox.OfType<Journal>().Last().Text);

        var v = h.View(0);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("turn").ValueKind);
        Assert.Equal(0, v.GetProperty("result").GetProperty("winner").GetInt32());
        Assert.Equal([107, 0, 0, 0], v.GetProperty("result").GetProperty("scores").EnumerateArray().Select(x => x.GetInt32()));
    }

    [Fact]
    public void Under_a_hundred_the_match_goes_on()
    {
        var h = Table();
        Position(h, [[6, 3]], [[[3, 3]], [[5, 4], [2, 1]]], yard: [[0, 0]], scores: [80, 0, 0, 0]);

        Assert.True(Play(h, 0, 3, 3, "right").Ok);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal([92, 0, 0, 0], Scores(h));
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("result").ValueKind);
    }

    // ---------- вид і приховане ----------

    [Fact]
    public void View_matches_the_shape_the_client_expects()
    {
        var h = Table();
        // Хто починає — вирішує роздача, тож питаємо вид у того, чия черга: саме йому цікаві canPlay/mustDraw.
        var turn = h.View(0).GetProperty("turn").GetInt32();
        var v = h.View(turn);

        Assert.Equal(2, v.GetProperty("players").GetInt32());
        Assert.Equal(1, v.GetProperty("round").GetInt32());
        Assert.Equal(JsonValueKind.Array, v.GetProperty("line").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("ends").ValueKind);
        Assert.Equal(7, v.GetProperty("hand").GetArrayLength());
        Assert.Equal(4, v.GetProperty("counts").GetArrayLength());
        Assert.Equal(JsonValueKind.Number, v.GetProperty("boneyard").ValueKind);
        Assert.Equal(4, v.GetProperty("scores").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("lastRound").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("result").ValueKind);
        // Ланцюг порожній — перша кістка лягає будь-яка.
        Assert.True(v.GetProperty("canPlay").GetBoolean());
        Assert.False(v.GetProperty("mustDraw").GetBoolean());
        Assert.False(h.View(1 - turn).GetProperty("canPlay").GetBoolean());
    }

    [Fact]
    public void The_line_carries_the_double_flag_the_client_draws_sideways()
    {
        var h = Table();
        Position(h, [[3, 3]], [[[3, 1], [6, 6]], [[6, 5]]], yard: [[0, 0]]);
        Assert.True(Play(h, 0, 3, 1, "right").Ok);

        var line = h.View(0).GetProperty("line");
        Assert.True(line[0].GetProperty("double").GetBoolean());
        Assert.False(line[1].GetProperty("double").GetBoolean());
    }

    [Fact]
    public void Each_player_sees_only_their_own_hand()
    {
        var h = Table(4);
        var hands = Enumerable.Range(0, 4).Select(s => Views.Text(Hand(h, s))).ToList();

        Assert.Equal(4, hands.Distinct().Count());
        for (var seat = 0; seat < 4; seat++)
        {
            var v = h.View(seat);
            Assert.Equal(5, v.GetProperty("hand").GetArrayLength());
            // Чужі кістки не показуємо навіть числом — лише скільки їх.
            Assert.Equal([5, 5, 5, 5], v.GetProperty("counts").EnumerateArray().Select(x => x.GetInt32()));
        }
    }

    [Fact]
    public void A_watcher_sees_no_hand_at_all_and_no_boneyard()
    {
        var h = Table();
        var v = h.View(null);

        Assert.Equal(JsonValueKind.Null, v.GetProperty("hand").ValueKind);
        Assert.Equal(JsonValueKind.Number, v.GetProperty("boneyard").ValueKind);
        Assert.False(v.GetProperty("canPlay").GetBoolean());
        Assert.False(v.GetProperty("mustDraw").GetBoolean());

        // Жодна кістка з чиєїсь руки не має потрапити у вид глядача.
        var text = Views.Text(h.Room.Game.View(null));
        foreach (var tile in Hand(h, 0)) Assert.DoesNotContain($"[{tile[0]},{tile[1]}]", text);
    }

    [Fact]
    public void CanPlay_and_mustDraw_describe_the_seat_that_asks()
    {
        var h = Table();
        Position(h, [[0, 3]], [[[6, 6]], [[3, 2]]], yard: [[2, 2]]);

        Assert.False(h.View(0).GetProperty("canPlay").GetBoolean());
        Assert.True(h.View(0).GetProperty("mustDraw").GetBoolean());
        // Петро ходити зараз не може взагалі — байдуже, що кістка в нього підходяща.
        Assert.False(h.View(1).GetProperty("canPlay").GetBoolean());
        Assert.False(h.View(1).GetProperty("mustDraw").GetBoolean());
    }

    [Fact]
    public void Views_are_copies_not_the_live_table()
    {
        var h = Table();
        Position(h, [[6, 3]], [[[3, 1], [6, 6]], [[6, 5]]], yard: [[0, 0]]);
        var first = (object)h.Room.Game.View(0);
        Play(h, 0, 3, 1, "right");
        Assert.NotEqual(Views.Text(first), Views.Text(h.Room.Game.View(0)));
    }

    // ---------- вихід гравця ----------

    [Fact]
    public void A_player_who_leaves_drops_their_bones_into_the_boneyard_and_the_rest_play_on()
    {
        var h = Table(3);
        Assert.True(h.Leave("Петро").Ok);

        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(0, Counts(h)[1]);
        Assert.Equal(18, Yard(h));                         // 13 у базарі + 5 із руки
        Assert.Equal(2, h.View(0).GetProperty("players").GetInt32());
        Assert.Contains(h.Outbox.OfType<Journal>(), j => j.Text.Contains("Петро встав з-за столу"));
    }

    [Fact]
    public void The_leaver_never_gets_the_turn_again()
    {
        var h = Table(3);
        var turn = h.View(0).GetProperty("turn").GetInt32();
        h.Leave(Nicks[turn]);

        Assert.NotEqual(turn, h.View(0).GetProperty("turn").GetInt32());
        // Місце вже вільне, тож питаємо саму гру: каркас до неї такий хід і не донесе.
        Assert.Equal("Ти вже не в цій партії", h.Room.Game.Act(turn, "draw", Views.Payload(null)).Message);
    }

    [Fact]
    public void When_only_one_player_is_left_the_match_is_theirs()
    {
        var h = Table();
        Assert.True(h.Leave("Петро").Ok);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Contains("перемога — Оля", h.Outbox.OfType<Journal>().Last().Text);
    }

    // ---------- детермінізм, рематч, збереження ----------

    [Fact]
    public void The_same_seed_deals_the_same_bones()
    {
        static string Deal(int seed) => Views.Text(Table(seed: seed).Room.Game.View(0));
        Assert.Equal(Deal(77), Deal(77));
        Assert.NotEqual(Deal(77), Deal(78));
    }

    [Fact]
    public void Rematch_starts_the_match_from_zero_and_swaps_the_seats()
    {
        var h = Table();
        Position(h, [[6, 3]], [[[3, 3]], [[5, 4], [2, 1]]], yard: [[0, 0]], scores: [95, 0, 0, 0], round: 6);
        Play(h, 0, 3, 3, "right");
        Assert.True(h.Rematch("Оля").Ok);

        Assert.Equal("Петро", h.Room.Seats[0]);
        Assert.Equal([0, 0, 0, 0], Scores(h));
        Assert.Equal(1, h.View(0).GetProperty("round").GetInt32());
        Assert.Equal([7, 7, 0, 0], Counts(h));
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("lastRound").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("result").ValueKind);
    }

    [Fact]
    public void Load_of_Save_gives_the_very_same_table()
    {
        var h = Table();
        Position(h, [[6, 3]], [[[3, 1], [4, 4]], [[6, 5], [2, 0]]], yard: [[0, 0]], scores: [40, 12, 0, 0], round: 3);
        Play(h, 0, 3, 1, "right");

        var copy = Table();
        copy.Room.Game.Load(h.Room.Game.Save()!);
        Assert.Equal(Views.Text(h.Room.Game.View(0)), Views.Text(copy.Room.Game.View(0)));
        Assert.Equal(Views.Text(h.Room.Game.View(1)), Views.Text(copy.Room.Game.View(1)));
    }

    [Fact]
    public void Domino_is_in_the_catalog_as_a_hidden_board_game_for_two_to_four()
    {
        var game = Assert.Single(new Registry().Catalog, g => g.Id == "domino");

        Assert.Equal("Доміно", game.Title);
        Assert.Equal("board", game.Group);
        Assert.Equal(2, game.MinPlayers);
        Assert.Equal(4, game.MaxPlayers);
        Assert.Equal("byHost", game.Start);
        Assert.True(game.Hidden);
        Assert.False(game.Rated);
        Assert.Equal(0, game.TickMs);
        Assert.Equal("domino", game.Module);
    }

    [Fact]
    public void The_bone_knows_its_own_rank_and_which_half_is_free()
    {
        Assert.True(new DominoBone(4, 4).Rank > new DominoBone(6, 5).Rank);   // дубль старший за будь-що
        Assert.True(new DominoBone(6, 0).Rank > new DominoBone(5, 1).Rank);   // сума та сама — вирішує половинка
        Assert.Equal(2, new DominoBone(6, 2).Other(6));
        Assert.Equal(6, new DominoBone(6, 2).Other(2));
        Assert.True(new DominoBone(6, 2).Same(new DominoBone(2, 6)));
        Assert.Equal(100, Domino.Target);
    }
}
