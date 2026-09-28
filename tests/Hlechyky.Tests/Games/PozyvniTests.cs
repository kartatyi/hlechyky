using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Позивні (specs/pozyvni.md): дві команди, два капітани, 25 слів. Розклад бачать лише капітани, тож
/// половина тестів — про те, чого у виді бути не повинно. Словник у тестах свій, щоб слова столу були
/// відомі наперед.
/// </summary>
public class PozyvniTests
{
    static readonly string[] Bank =
    [
        "кіт", "пес", "море", "ліс", "хата", "вода", "небо", "сонце", "ніч", "стіл",
        "вікно", "книга", "мед", "сир", "гора", "річка", "пісок", "вогонь", "сніг", "дощ",
        "кінь", "вовк", "риба", "птах", "дерево", "камінь", "зірка", "дорога", "хліб", "сіль",
    ];

    static readonly int SetupTicks = Pozyvni.SetupMs / Pozyvni.TickMs;

    static PictionaryWords Words(IEnumerable<string>? words = null) =>
        new((words ?? Bank).Select(w => ("animals", w)));

    /// <summary>Стіл, де партія вже почалась, але склад іще розбирають (фаза setup).</summary>
    static RoomHarness Setup(object? options = null, int nicks = 4, PictionaryWords? words = null)
    {
        var h = new RoomHarness("pozyvni", options: options, seed: 7, services: RoomHarness.WithService(words ?? Words()));
        foreach (var nick in new[] { "Оля", "Петро", "Ганна", "Влад", "Марта", "Богдан" }.Take(nicks)) h.Join(nick);
        h.Start();
        return h;
    }

    /// <summary>Стіл, де вже чекають на першу підказку.</summary>
    static RoomHarness Table(object? options = null, int nicks = 4, PictionaryWords? words = null)
    {
        var h = Setup(options, nicks, words);
        Assert.True(h.Act(0, "go").Ok);
        return h;
    }

    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;
    static string Side(RoomHarness h) => h.View(null).GetProperty("side").GetString()!;
    static string Foe(RoomHarness h) => Side(h) == "red" ? "blue" : "red";

    static int Boss(RoomHarness h, string side) =>
        h.View(null).GetProperty("teams").GetProperty(side).GetProperty("boss").GetInt32();

    static int[] Seats(RoomHarness h, string side) =>
        [.. h.View(null).GetProperty("teams").GetProperty(side).GetProperty("seats").EnumerateArray().Select(e => e.GetInt32())];

    /// <summary>Польовий гравець команди — той, хто тикає в слова.</summary>
    static int Field(RoomHarness h, string side) => Seats(h, side).First(s => s != Boss(h, side));

    /// <summary>Розклад очима капітана (єдиного, хто його бачить).</summary>
    static string[] Key(RoomHarness h) =>
        [.. h.View(Boss(h, Side(h))).GetProperty("key").EnumerateArray().Select(e => e.GetString()!)];

    static string[] Board(RoomHarness h) =>
        [.. h.View(null).GetProperty("board").EnumerateArray().Select(e => e.GetProperty("w").GetString()!)];

    static bool Open(RoomHarness h, int i) =>
        h.View(null).GetProperty("board")[i].GetProperty("open").ValueKind != JsonValueKind.Null;

    /// <summary>Перше ще не відкрите слово потрібного кольору.</summary>
    static int Card(RoomHarness h, string colour)
    {
        var key = Key(h);
        for (var i = 0; i < key.Length; i++) if (key[i] == colour && !Open(h, i)) return i;
        throw new InvalidOperationException($"нема закритого слова кольору {colour}");
    }

    static ActResult Clue(RoomHarness h, string word = "натяк", int count = 1) =>
        h.Act(Boss(h, Side(h)), "clue", new { word, count });

    static ActResult Pick(RoomHarness h, string colour) => h.Act(Field(h, Side(h)), "pick", new { i = Card(h, colour) });

    static int Left(RoomHarness h) => h.View(null).GetProperty("clue").GetProperty("left").GetInt32();

    // ---------------------------------------------------------------- стіл і розклад

    [Fact]
    public void The_table_is_twenty_five_words_nine_of_them_for_the_team_that_starts()
    {
        var h = Table();
        var key = Key(h);

        Assert.Equal(Pozyvni.Cards, key.Length);
        Assert.Equal(Pozyvni.Cards, Board(h).Distinct().Count());
        Assert.Equal(9, key.Count(k => k == Side(h)));
        Assert.Equal(8, key.Count(k => k == Foe(h)));
        Assert.Equal(7, key.Count(k => k == "grey"));
        Assert.Equal(1, key.Count(k => k == "black"));
    }

    [Fact]
    public void Two_black_words_take_their_place_from_the_neutral_ones()
    {
        var h = Table(new { black = "2" });
        var key = Key(h);

        Assert.Equal(2, key.Count(k => k == "black"));
        Assert.Equal(6, key.Count(k => k == "grey"));
        Assert.Equal(9, key.Count(k => k == Side(h)));
    }

    [Fact]
    public void Seats_are_split_every_other_one_and_the_first_of_each_team_is_the_captain()
    {
        var h = Setup(nicks: 5);

        Assert.Equal([0, 2, 4], Seats(h, "red"));
        Assert.Equal([1, 3], Seats(h, "blue"));
        Assert.Equal(0, Boss(h, "red"));
        Assert.Equal(1, Boss(h, "blue"));
        Assert.Equal("червоні", h.Room.Game.SeatName(2));
        Assert.Equal("сині", h.Room.Game.SeatName(3));
    }

    // ---------------------------------------------------------------- склад

    [Fact]
    public void In_setup_everyone_picks_a_team_and_a_captain()
    {
        var h = Setup();

        Assert.True(h.Act(2, "team", new { side = "blue" }).Ok);
        Assert.True(h.Act(2, "boss").Ok);
        Assert.Equal([1, 2, 3], Seats(h, "blue"));
        Assert.Equal(2, Boss(h, "blue"));
        Assert.Equal([0], Seats(h, "red"));
    }

    [Fact]
    public void A_team_left_without_a_captain_gets_a_new_one()
    {
        var h = Setup();

        Assert.True(h.Act(0, "team", new { side = "blue" }).Ok);   // капітан червоних пішов до синіх
        Assert.Null(h.View(null).GetProperty("teams").GetProperty("red").GetProperty("boss").GetString());
        Assert.True(h.Act(0, "go").Ok);
        Assert.Equal(2, Boss(h, "red"));
    }

    [Fact]
    public void The_line_up_is_balanced_before_the_first_clue()
    {
        var h = Setup();
        foreach (var seat in new[] { 1, 2, 3 }) Assert.True(h.Act(seat, "team", new { side = "red" }).Ok);

        Assert.True(h.Act(0, "go").Ok);

        Assert.Equal(2, Seats(h, "blue").Length);
        Assert.Equal(2, Seats(h, "red").Length);
        Assert.Contains(Boss(h, "red"), Seats(h, "red"));
        Assert.Contains(Boss(h, "blue"), Seats(h, "blue"));
        Assert.Equal("clue", Phase(h));
    }

    [Fact]
    public void A_table_that_lost_a_player_during_setup_has_no_two_teams()
    {
        var h = Setup();
        h.Leave(h.NickOf(3));

        Assert.True(h.Act(0, "go").Ok);

        Assert.Equal("done", Phase(h));
        var finished = Assert.Single(h.Finished);
        Assert.True(finished.Result.Draw);
        Assert.Contains("двох команд", finished.Result.Text);
    }

    [Fact]
    public void Nobody_is_left_alone_in_a_team_after_balancing()
    {
        var h = Setup(nicks: 5);
        foreach (var seat in new[] { 1, 2, 3, 4 }) Assert.True(h.Act(seat, "team", new { side = "red" }).Ok);

        Assert.True(h.Act(0, "go").Ok);

        Assert.True(Seats(h, "red").Length >= 2);
        Assert.True(Seats(h, "blue").Length >= 2);
        Assert.Equal(5, Seats(h, "red").Length + Seats(h, "blue").Length);
    }

    [Fact]
    public void Setup_runs_out_on_its_own()
    {
        var h = Setup();
        Assert.Equal("setup", Phase(h));

        h.Tick(SetupTicks);

        Assert.Equal("clue", Phase(h));
    }

    [Fact]
    public void The_line_up_is_settled_once_the_game_started()
    {
        var h = Table();

        Assert.False(h.Act(2, "team", new { side = "blue" }).Ok);
        Assert.False(h.Act(2, "boss").Ok);
    }

    // ---------------------------------------------------------------- підказка

    [Fact]
    public void Only_the_captain_of_the_moving_team_gives_the_clue()
    {
        var h = Table();
        var side = Side(h);

        Assert.False(h.Act(Field(h, side), "clue", new { word = "натяк", count = 1 }).Ok);
        Assert.False(h.Act(Boss(h, Foe(h)), "clue", new { word = "натяк", count = 1 }).Ok);
        Assert.True(Clue(h).Ok);
        Assert.Equal("guess", Phase(h));
    }

    [Fact]
    public void A_clue_is_one_word_of_letters()
    {
        var h = Table();

        Assert.False(Clue(h, "два слова").Ok);
        Assert.False(Clue(h, "хот-дог").Ok);
        Assert.False(Clue(h, "к3").Ok);
        Assert.False(Clue(h, "").Ok);
        Assert.False(Clue(h, new string('я', 30)).Ok);
        Assert.False(Clue(h, "я").Ok);
        Assert.True(Clue(h, "м'ясо").Ok);   // апостроф усередині — звичайне українське слово
    }

    [Fact]
    public void A_clue_cannot_be_a_word_that_lies_on_the_table()
    {
        var h = Table();
        var word = Board(h)[Card(h, Side(h))];

        Assert.False(Clue(h, word).Ok);
        Assert.Equal("Так не можна: це слово на столі", h.Act(Boss(h, Side(h)), "clue", new { word, count = 1 }).Message);
    }

    [Fact]
    public void Near_forms_of_a_table_word_are_refused_too()
    {
        Assert.True(Pozyvni.SameRoot("криниця", "криниці"));
        Assert.True(Pozyvni.SameRoot("криничний", "криниця"));   // спільні перші п'ять літер
        Assert.True(Pozyvni.SameRoot("хліб", "хліба"));          // слово столу — початок підказки
        Assert.True(Pozyvni.SameRoot("сонце", "сонцем"));
        Assert.False(Pozyvni.SameRoot("кіт", "кітель"));
        Assert.False(Pozyvni.SameRoot("море", "морський"));      // чергування в корені не ловимо свідомо
    }

    [Fact]
    public void An_opened_word_is_free_to_say()
    {
        var h = Table();
        var i = Card(h, "grey");
        var word = Board(h)[i];
        Assert.True(Clue(h).Ok);
        Assert.True(h.Act(Field(h, Side(h)), "pick", new { i }).Ok);   // нейтральне: хід перейшов

        Assert.True(Clue(h, word).Ok);
    }

    [Fact]
    public void The_count_stays_within_its_bounds()
    {
        var h = Table();

        Assert.False(Clue(h, count: -1).Ok);
        Assert.False(Clue(h, count: 10).Ok);
        Assert.True(Clue(h, count: 9).Ok);
    }

    [Fact]
    public void The_zero_clue_can_be_switched_off_at_the_table()
    {
        var off = Table(new { zero = "off" });
        Assert.False(Clue(off, count: 0).Ok);

        var on = Table();
        Assert.True(Clue(on, count: 0).Ok);
    }

    [Fact]
    public void A_second_clue_in_the_same_turn_is_refused()
    {
        var h = Table();
        Assert.True(Clue(h).Ok);

        Assert.False(Clue(h, "ще").Ok);
    }

    // ---------------------------------------------------------------- здогадки

    [Fact]
    public void An_own_word_keeps_the_turn_and_spends_one_guess()
    {
        var h = Table();
        var side = Side(h);
        Assert.True(Clue(h, count: 2).Ok);
        Assert.Equal(3, Left(h));

        Assert.True(Pick(h, side).Ok);

        Assert.Equal(side, Side(h));
        Assert.Equal("guess", Phase(h));
        Assert.Equal(2, Left(h));
        Assert.Equal(8, h.View(null).GetProperty("left").GetProperty(side).GetInt32());
    }

    [Fact]
    public void An_enemy_word_hands_the_turn_over_at_once()
    {
        var h = Table();
        var side = Side(h);
        Assert.True(Clue(h, count: 3).Ok);

        Assert.True(Pick(h, Foe(h)).Ok);

        Assert.NotEqual(side, Side(h));
        Assert.Equal("clue", Phase(h));
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("clue").ValueKind);
    }

    [Fact]
    public void A_neutral_word_hands_the_turn_over_too()
    {
        var h = Table();
        var side = Side(h);
        Assert.True(Clue(h, count: 3).Ok);

        Assert.True(Pick(h, "grey").Ok);

        Assert.NotEqual(side, Side(h));
        Assert.Equal("clue", Phase(h));
    }

    [Fact]
    public void A_clue_gives_count_plus_one_guesses_and_no_more()
    {
        var h = Table();
        var side = Side(h);
        Assert.True(Clue(h, count: 1).Ok);

        Assert.True(Pick(h, side).Ok);
        Assert.True(Pick(h, side).Ok);   // друга, «на пам'ять»

        Assert.NotEqual(side, Side(h));
        Assert.Equal("clue", Phase(h));
    }

    [Fact]
    public void The_zero_clue_runs_until_the_first_miss()
    {
        var h = Table();
        var side = Side(h);
        Assert.True(Clue(h, count: 0).Ok);

        for (var i = 0; i < 5; i++) Assert.True(Pick(h, side).Ok);
        Assert.Equal(side, Side(h));

        Assert.True(Pick(h, "grey").Ok);
        Assert.NotEqual(side, Side(h));
    }

    [Fact]
    public void The_team_can_say_enough_but_the_captain_cannot()
    {
        var h = Table();
        var side = Side(h);
        Assert.True(Clue(h, count: 3).Ok);

        Assert.False(h.Act(Boss(h, side), "pass").Ok);
        Assert.True(h.Act(Field(h, side), "pass").Ok);

        Assert.NotEqual(side, Side(h));
    }

    [Fact]
    public void Who_cannot_touch_the_words()
    {
        var h = Table();
        var side = Side(h);
        var i = Card(h, side);

        Assert.False(h.Act(Field(h, side), "pick", new { i }).Ok);         // підказки ще нема
        Assert.True(Clue(h, count: 3).Ok);
        Assert.False(h.Act(Boss(h, side), "pick", new { i }).Ok);          // капітан свого розкладу не тикає
        Assert.False(h.Act(Field(h, Foe(h)), "pick", new { i }).Ok);       // чужа команда
        Assert.False(h.Act(Field(h, side), "pick", new { i = 99 }).Ok);    // нема такого слова
        Assert.True(h.Act(Field(h, side), "pick", new { i }).Ok);
        Assert.False(h.Act(Field(h, side), "pick", new { i }).Ok);         // вже відкрите
    }

    // ---------------------------------------------------------------- пальці

    /// <summary>Польові гравці команди, що ходить (їх двоє на столі з шістьох).</summary>
    static int[] Fields(RoomHarness h) => [.. Seats(h, Side(h)).Where(s => s != Boss(h, Side(h)))];

    static int[] Fingers(RoomHarness h, int card) =>
        h.View(null).GetProperty("fingers").TryGetProperty(card.ToString(), out var e)
            ? [.. e.EnumerateArray().Select(x => x.GetInt32())] : [];

    [Fact]
    public void One_finger_is_not_enough_when_the_team_has_two_field_players()
    {
        var h = Table(nicks: 6);
        var side = Side(h);
        Assert.True(Clue(h, count: 2).Ok);
        var i = Card(h, side);
        var mates = Fields(h);

        Assert.True(h.Act(mates[0], "pick", new { i }).Ok);

        Assert.False(Open(h, i));
        Assert.Equal([mates[0]], Fingers(h, i));
        Assert.Equal(3, Left(h));   // здогадку ще не витрачено
    }

    [Fact]
    public void The_word_opens_when_every_field_player_points_at_it()
    {
        var h = Table(nicks: 6);
        var side = Side(h);
        Assert.True(Clue(h, count: 2).Ok);
        var i = Card(h, side);
        var mates = Fields(h);

        Assert.True(h.Act(mates[0], "pick", new { i }).Ok);
        Assert.True(h.Act(mates[1], "pick", new { i }).Ok);

        Assert.True(Open(h, i));
        Assert.Empty(Fingers(h, i));
        Assert.Equal(2, Left(h));
    }

    [Fact]
    public void Fingers_on_different_words_open_nothing()
    {
        var h = Table(nicks: 6);
        var side = Side(h);
        Assert.True(Clue(h, count: 2).Ok);
        var mine = Card(h, side);
        var grey = Card(h, "grey");
        var mates = Fields(h);

        Assert.True(h.Act(mates[0], "pick", new { i = mine }).Ok);
        Assert.True(h.Act(mates[1], "pick", new { i = grey }).Ok);

        Assert.False(Open(h, mine));
        Assert.False(Open(h, grey));
        Assert.Equal([mates[0]], Fingers(h, mine));
        Assert.Equal([mates[1]], Fingers(h, grey));
    }

    [Fact]
    public void The_same_word_twice_takes_the_finger_back()
    {
        var h = Table(nicks: 6);
        Assert.True(Clue(h, count: 2).Ok);
        var i = Card(h, Side(h));
        var mate = Fields(h)[0];

        Assert.True(h.Act(mate, "pick", new { i }).Ok);
        Assert.True(h.Act(mate, "pick", new { i }).Ok);

        Assert.Empty(Fingers(h, i));
        Assert.False(Open(h, i));
    }

    [Fact]
    public void A_new_clue_wipes_the_fingers()
    {
        var h = Table(nicks: 6);
        var side = Side(h);
        Assert.True(Clue(h, count: 1).Ok);
        var i = Card(h, side);
        Assert.True(h.Act(Fields(h)[0], "pick", new { i }).Ok);

        Assert.True(h.Act(Fields(h)[0], "pass").Ok);

        Assert.Empty(Fingers(h, i));
        Assert.True(Clue(h, "інше", 1).Ok);
        Assert.Empty(Fingers(h, i));
    }

    [Fact]
    public void A_captain_points_at_nothing()
    {
        var h = Table(nicks: 6);
        Assert.True(Clue(h, count: 2).Ok);
        var i = Card(h, Side(h));

        Assert.False(h.Act(Boss(h, Side(h)), "pick", new { i }).Ok);

        Assert.Empty(Fingers(h, i));
    }

    [Fact]
    public void Alone_in_the_team_a_field_player_opens_at_once()
    {
        var h = Table();   // четверо: в кожній команді один польовий
        var side = Side(h);
        Assert.True(Clue(h, count: 2).Ok);
        var i = Card(h, side);

        Assert.True(h.Act(Field(h, side), "pick", new { i }).Ok);

        Assert.True(Open(h, i));
    }

    // ---------------------------------------------------------------- кінець партії

    [Fact]
    public void The_black_word_hands_the_win_to_the_other_team()
    {
        var h = Table();
        var side = Side(h);
        var foe = Foe(h);
        Assert.True(Clue(h, count: 3).Ok);

        Assert.True(Pick(h, "black").Ok);

        Assert.Equal("done", Phase(h));
        var finished = Assert.Single(h.Finished);
        Assert.Equal(Seats(h, foe), finished.Result.Winners.Order());
        Assert.Contains("чорне", finished.Result.Text);
        Assert.True(h.View(null).GetProperty("result").GetProperty("black").GetBoolean());
        Assert.NotEqual(side, h.View(null).GetProperty("result").GetProperty("side").GetString());
    }

    [Fact]
    public void All_own_words_win_and_the_captain_is_among_the_winners()
    {
        var h = Table();
        var side = Side(h);
        var winners = Seats(h, side);
        Assert.True(Clue(h, count: 0).Ok);

        for (var i = 0; i < 9; i++) Assert.True(Pick(h, side).Ok);

        Assert.Equal("done", Phase(h));
        var finished = Assert.Single(h.Finished);
        Assert.Equal(winners, finished.Result.Winners.Order());
        Assert.Contains(Boss(h, side), finished.Result.Winners);
    }

    [Fact]
    public void Opening_the_last_enemy_word_gives_the_game_away()
    {
        var h = Table();
        var foe = Foe(h);
        // Тикаємо лише в слова суперника: свої — коли ходить він, чужі — коли ходимо ми. Вісім слів,
        // і останнє з них закінчує партію на його користь, хоч відкрили його чужі руки.
        for (var n = 0; n < 20 && Phase(h) != "done"; n++)
        {
            if (Phase(h) == "clue") Assert.True(Clue(h, "натяк" + new string('о', n + 1), 1).Ok);
            Assert.True(h.Act(Field(h, Side(h)), "pick", new { i = Card(h, foe) }).Ok);
        }

        Assert.Equal("done", Phase(h));
        Assert.Equal(foe, h.View(null).GetProperty("result").GetProperty("side").GetString());
    }

    // ---------------------------------------------------------------- таємниця

    [Fact]
    public void The_key_is_for_captains_only()
    {
        var h = Table();
        var side = Side(h);

        Assert.Equal(Pozyvni.Cards, h.View(Boss(h, side)).GetProperty("key").GetArrayLength());
        Assert.Equal(Pozyvni.Cards, h.View(Boss(h, Foe(h))).GetProperty("key").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, h.View(Field(h, side)).GetProperty("key").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("key").ValueKind);
    }

    [Fact]
    public void When_the_game_is_over_everybody_sees_the_key()
    {
        var h = Table();
        var field = Field(h, Side(h));
        Assert.True(Clue(h, count: 1).Ok);
        Assert.True(Pick(h, "black").Ok);

        Assert.Equal(Pozyvni.Cards, h.View(field).GetProperty("key").GetArrayLength());
        Assert.Equal(Pozyvni.Cards, h.View(null).GetProperty("key").GetArrayLength());
    }

    [Fact]
    public void A_watcher_has_no_place_at_the_table()
    {
        var h = Table();

        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("me").ValueKind);
        Assert.Equal("red", h.View(Seats(h, "red")[1]).GetProperty("me").GetProperty("side").GetString());
        Assert.True(h.View(Boss(h, "red")).GetProperty("me").GetProperty("boss").GetBoolean());
        Assert.False(h.View(Field(h, "red")).GetProperty("me").GetProperty("boss").GetBoolean());
    }

    // ---------------------------------------------------------------- годинник

    [Fact]
    public void Without_a_clock_nothing_burns()
    {
        var h = Table();
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("endsAt").ValueKind);

        h.Tick(10_000 / Pozyvni.TickMs);

        Assert.Equal("clue", Phase(h));
    }

    [Fact]
    public void The_clock_burns_a_clue_that_never_came()
    {
        var h = Table(new { clock = "60" });
        var side = Side(h);

        h.Tick(60_000 / Pozyvni.TickMs);

        Assert.NotEqual(side, Side(h));
        Assert.Equal("clue", Phase(h));
    }

    [Fact]
    public void The_clock_burns_the_guesses_too()
    {
        var h = Table(new { clock = "60" });
        var side = Side(h);
        Assert.True(Clue(h, count: 3).Ok);

        h.Tick(60_000 / Pozyvni.TickMs);

        Assert.NotEqual(side, Side(h));
        Assert.Equal("clue", Phase(h));
    }

    // ---------------------------------------------------------------- швидкі 4×4 (прохід №3, п. 168)

    [Fact]
    public void A_small_table_has_sixteen_words_dealt_six_five_four_one()
    {
        var h = Table(new { size = "4" });
        var view = h.View(null);

        Assert.Equal(4, view.GetProperty("size").GetInt32());
        Assert.Equal(Pozyvni.SmallCards, Board(h).Length);
        var key = Key(h);
        Assert.Equal(16, key.Length);
        Assert.Equal(6, key.Count(k => k == Side(h)));
        Assert.Equal(5, key.Count(k => k == Foe(h)));
        Assert.Equal(4, key.Count(k => k == "grey"));
        Assert.Equal(1, key.Count(k => k == "black"));
        Assert.False(h.Act(Field(h, Side(h)), "pick", new { i = 16 }).Ok);
    }

    [Fact]
    public void A_small_table_ticks_a_minute_by_default_and_the_classic_one_does_not()
    {
        var small = Table(new { size = "4" });
        Assert.Equal(60_000, small.View(null).GetProperty("phaseMs").GetInt32());
        var side = Side(small);
        small.Tick(60_000 / Pozyvni.TickMs);
        Assert.NotEqual(side, Side(small));

        var classic = Table();
        Assert.Equal(25, Board(classic).Length);
        Assert.Equal(JsonValueKind.Null, classic.View(null).GetProperty("endsAt").ValueKind);

        var calm = Table(new { size = "4", clock = "off" });
        Assert.Equal(JsonValueKind.Null, calm.View(null).GetProperty("endsAt").ValueKind);
    }

    [Fact]
    public void A_small_table_can_be_won_by_finding_all_six()
    {
        var h = Table(new { size = "4" });
        var side = Side(h);
        Assert.True(Clue(h, count: 0).Ok);
        for (var i = 0; i < 6; i++) Assert.True(Pick(h, side).Ok);

        Assert.Equal("done", Phase(h));
    }

    // ---------------------------------------------------------------- виходи з-за столу

    [Fact]
    public void A_captain_who_left_is_replaced_by_a_team_mate()
    {
        var h = Table(nicks: 6);
        var side = Side(h);
        var boss = Boss(h, side);

        h.Leave(h.NickOf(boss));

        Assert.Equal("playing", h.Room.Status.ToString().ToLowerInvariant());
        Assert.NotEqual(boss, Boss(h, side));
        Assert.Contains(Boss(h, side), Seats(h, side));
    }

    [Fact]
    public void A_team_that_dropped_to_one_loses()
    {
        var h = Table();
        var side = Side(h);
        var foe = Foe(h);
        var winners = Seats(h, foe);

        h.Leave(h.NickOf(Field(h, side)));

        Assert.Equal("done", Phase(h));
        var finished = Assert.Single(h.Finished);
        Assert.Equal(winners, finished.Result.Winners.Order());
    }

    // ---------------------------------------------------------------- ще раз і словник

    [Fact]
    public void The_rematch_deals_new_words_and_the_losers_start()
    {
        var big = Words(Enumerable.Range(1, 60).Select(i => $"глечик{i:00}"));
        var h = Table(words: big);
        var first = Board(h).ToHashSet();
        var loser = Side(h);
        Assert.True(Clue(h, count: 1).Ok);
        Assert.True(Pick(h, "black").Ok);   // хто ходив, той і програв

        Assert.True(h.Rematch().Ok);
        Assert.True(h.Act(0, "go").Ok);

        Assert.DoesNotContain(Board(h), first.Contains);
        Assert.Equal(loser, Side(h));
        Assert.Equal(9, Key(h).Count(k => k == loser));
    }

    [Fact]
    public void A_table_without_words_is_not_created()
    {
        var thin = new RoomHarness("pozyvni", seed: 7, services: RoomHarness.WithService(Words(Bank.Take(10))));

        Assert.False(thin.Join("Оля").Ok);
        Assert.Contains("Замало слів", thin.Reply.Message);
    }

    // ---------------------------------------------------------------- ачівки

    [Fact]
    public void A_big_clue_taken_whole_is_an_achievement_for_the_captain()
    {
        var h = Table();
        var side = Side(h);
        var boss = Boss(h, side);
        Assert.True(Clue(h, count: 4).Ok);

        for (var i = 0; i < 4; i++) Assert.True(Pick(h, side).Ok);

        var award = Assert.Single(h.Awards, a => a.Reason == "ach:pozyvni-4");
        Assert.Equal(h.NickOf(boss), award.Nick);
    }

    [Fact]
    public void Winning_with_the_last_allowed_guess_is_an_achievement_for_the_team()
    {
        var h = Table();
        var side = Side(h);
        var team = Seats(h, side);

        // Беремо сім своїх і здаємо хід: лишається двоє. Суперник теж пасує — і повертаємось до своїх.
        Assert.True(Clue(h, count: 0).Ok);
        for (var i = 0; i < 7; i++) Assert.True(Pick(h, side).Ok);
        Assert.True(h.Act(Field(h, side), "pass").Ok);
        Assert.True(Clue(h, "чуже", 1).Ok);
        Assert.True(h.Act(Field(h, Side(h)), "pass").Ok);

        // Підказка на одне слово — дві здогадки, і друга з них виграє партію.
        Assert.True(Clue(h, "останнє", 1).Ok);
        Assert.True(Pick(h, side).Ok);
        Assert.Equal(1, Left(h));
        Assert.True(Pick(h, side).Ok);

        Assert.Equal("done", Phase(h));
        Assert.Equal(team.Length, h.Awards.Count(a => a.Reason == "ach:pozyvni-edge"));
    }

    // ---------------------------------------------------------------- разом проти столу (2–3 гравці)

    [Fact]
    public void Two_players_can_sit_and_start()
    {
        var h = Table(nicks: 2);

        Assert.Equal("clue", Phase(h));
        Assert.Equal("coop", h.View(null).GetProperty("mode").GetString());
        Assert.Equal([0, 1], Seats(h, "red"));
        Assert.Empty(Seats(h, "blue"));
        Assert.Equal(0, Boss(h, "red"));
        Assert.Equal("red", Side(h));
        Assert.Equal("команда", h.Room.Game.SeatName(1));
    }

    [Fact]
    public void Auto_mode_plays_teams_from_four()
    {
        var h = Table(nicks: 4);
        Assert.Equal("teams", h.View(null).GetProperty("mode").GetString());
        Assert.Equal(2, Seats(h, "red").Length);
        Assert.Equal(2, Seats(h, "blue").Length);
    }

    [Fact]
    public void Coop_can_be_chosen_for_a_big_table_too()
    {
        var h = Table(new { mode = "coop" }, nicks: 5);
        Assert.Equal("coop", h.View(null).GetProperty("mode").GetString());
        Assert.Equal(5, Seats(h, "red").Length);
    }

    [Fact]
    public void Teams_mode_refuses_to_start_with_three()
    {
        var h = new RoomHarness("pozyvni", options: new { mode = "teams" }, seed: 7, services: RoomHarness.WithService(Words()));
        foreach (var nick in new[] { "Оля", "Петро", "Ганна" }) h.Join(nick);

        Assert.False(h.Start().Ok);
        Assert.Contains("щонайменше 4", h.Reply.Message);
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);
    }

    [Fact]
    public void In_coop_nobody_switches_teams_but_the_captain_can_change()
    {
        var h = Setup(nicks: 3);

        Assert.False(h.Act(1, "team", new { side = "blue" }).Ok);
        Assert.True(h.Act(2, "boss").Ok);
        Assert.True(h.Act(0, "go").Ok);

        Assert.Equal(2, Boss(h, "red"));
        Assert.Equal(3, Seats(h, "red").Length);
    }

    [Fact]
    public void In_coop_the_table_takes_one_of_its_words_after_every_turn()
    {
        var h = Table(nicks: 2);
        Assert.Equal(8, h.View(null).GetProperty("left").GetProperty("blue").GetInt32());

        Assert.True(Clue(h, count: 1).Ok);
        Assert.True(Pick(h, "grey").Ok);                 // промах — хід закінчено, стіл бере своє

        Assert.Equal("clue", Phase(h));
        Assert.Equal("red", Side(h));                     // знову наша підказка
        Assert.Equal(7, h.View(null).GetProperty("left").GetProperty("blue").GetInt32());
        Assert.Contains(h.View(null).GetProperty("log").EnumerateArray(), l => l.GetString()!.StartsWith("стіл забирає"));
    }

    [Fact]
    public void In_coop_passing_also_lets_the_table_move()
    {
        var h = Table(nicks: 3);
        Assert.True(Clue(h, count: 2).Ok);
        Assert.True(Pick(h, "red").Ok);
        // двоє польових: один показав пальцем, слово ще не відкрилось — і команда каже «досить»
        Assert.True(h.Act(Field(h, "red"), "pass").Ok);

        Assert.Equal(7, h.View(null).GetProperty("left").GetProperty("blue").GetInt32());
    }

    [Fact]
    public void In_coop_finding_all_own_words_is_a_win_for_everybody()
    {
        var h = Table(nicks: 2);
        Assert.True(Clue(h, count: 0).Ok);
        for (var i = 0; i < 9; i++) Assert.True(Pick(h, "red").Ok);

        Assert.Equal("done", Phase(h));
        var finished = Assert.Single(h.Finished);
        Assert.Equal([0, 1], finished.Result.Winners.Order());
        Assert.Contains("1 підказку", finished.Result.Text);
        Assert.Equal(1, h.View(null).GetProperty("clues").GetInt32());
    }

    [Fact]
    public void In_coop_the_table_wins_when_it_takes_its_last_word()
    {
        var h = Table(nicks: 2);
        for (var turn = 0; turn < 8; turn++)
        {
            Assert.True(Clue(h, "натяк" + (char)('а' + turn), 1).Ok);
            Assert.True(h.Act(1, "pass").Ok);
        }

        Assert.Equal("done", Phase(h));
        var finished = Assert.Single(h.Finished);
        Assert.True(finished.Result.Draw);                // стіл нікого не садить — перемоги нема ні в кого
        Assert.Contains("стіл забрав", finished.Result.Text);
        Assert.Equal("blue", h.View(null).GetProperty("result").GetProperty("side").GetString());
    }

    [Fact]
    public void In_coop_the_black_word_loses_the_game()
    {
        var h = Table(nicks: 2);
        Assert.True(Clue(h, count: 1).Ok);
        Assert.True(Pick(h, "black").Ok);

        var finished = Assert.Single(h.Finished);
        Assert.True(finished.Result.Draw);
        Assert.Contains("стіл переміг", finished.Result.Text);
    }

    [Fact]
    public void In_coop_the_field_player_still_does_not_see_the_key()
    {
        var h = Table(nicks: 3);
        Assert.NotEqual(JsonValueKind.Null, h.View(0).GetProperty("key").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.View(1).GetProperty("key").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.View(2).GetProperty("key").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("key").ValueKind);
    }

    [Fact]
    public void In_coop_a_player_leaving_a_pair_ends_the_game()
    {
        var h = Table(nicks: 2);
        h.Leave(h.NickOf(1));

        Assert.Equal("done", Phase(h));
        Assert.Single(h.Finished);
    }

    [Fact]
    public void Coop_rematch_moves_the_captain_to_another_player()
    {
        var h = Table(nicks: 2);
        var captain = h.NickOf(Boss(h, "red"));
        Assert.True(Clue(h, count: 1).Ok);
        Assert.True(Pick(h, "black").Ok);

        Assert.True(h.Rematch().Ok);
        Assert.True(h.Act(0, "go").Ok);

        Assert.Equal("coop", h.View(null).GetProperty("mode").GetString());
        Assert.Equal("red", Side(h));
        Assert.NotEqual(captain, h.NickOf(Boss(h, "red")));
    }
}

/// <summary>
/// Швидкодія Позивних на дванадцятьох (прохід 28.09): дві команди по шість, капітани підказують, польові показують
/// пальцем на свої слова, поки партія не скінчиться. Тик, хід і вид — копійчані.
/// </summary>
[Collection(SerialPerf.Name)]
public class PozyvniPerfTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Perf")]
    public void Twelve_players_a_whole_game_stay_cheap()
    {
        var words = new PictionaryWords(Enumerable.Range(0, 40).Select(i => ("animals", "слово" + (char)('а' + i % 30) + (char)('а' + i / 30))));
        var h = new RoomHarness("pozyvni", options: new { clock = "90" }, seed: 7, services: RoomHarness.WithService(words));
        foreach (var n in new[] { "Оля", "Петро", "Ганна", "Влад", "Марта", "Богдан", "Софія", "Тарас", "Мар'яна", "Остап", "Іра", "Денис" }) h.Join(n);
        h.Start();
        Assert.True(h.Act(0, "go").Ok);

        var ticks = new System.Diagnostics.Stopwatch();
        var acts = new System.Diagnostics.Stopwatch();
        int tickCount = 0, actCount = 0;
        long viewMax = 0;
        for (var guard = 0; guard < 2000 && h.Room.Status == RoomStatus.Playing; guard++)
        {
            var v = h.View(null);
            var side = v.GetProperty("side").GetString()!;
            var team = v.GetProperty("teams").GetProperty(side);
            var boss = team.GetProperty("boss").GetInt32();
            var field = team.GetProperty("seats").EnumerateArray().Select(e => e.GetInt32()).Where(s => s != boss).ToArray();
            acts.Start();
            if (v.GetProperty("phase").GetString() == "clue") h.Act(boss, "clue", new { word = "натяк", count = 2 });
            else
            {
                var key = h.View(boss).GetProperty("key").EnumerateArray().Select(e => e.GetString()).ToArray();
                var board = v.GetProperty("board");
                // своє закрите слово, а через раз — перше-ліпше закрите (промахи теж бувають)
                var card = Enumerable.Range(0, Pozyvni.Cards).First(i => board[i].GetProperty("open").ValueKind == JsonValueKind.Null
                    && (guard % 3 == 0 || key[i] == side));
                foreach (var s in field) h.Act(s, "pick", new { i = card });
            }
            acts.Stop();
            actCount++;
            viewMax = Math.Max(viewMax, System.Text.Encoding.UTF8.GetByteCount(Views.Text(h.Room.Game.View(boss))));
            ticks.Start(); h.Tick(); ticks.Stop(); tickCount++;
        }
        output.WriteLine($"тиків {tickCount}: {ticks.Elapsed.TotalMilliseconds * 1000 / tickCount:F1} мкс на тик; ходів {actCount}: {acts.Elapsed.TotalMilliseconds * 1000 / actCount:F0} мкс на хід разом із видами тесту; вид капітана макс {viewMax} Б");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(ticks.Elapsed.TotalMilliseconds * 1000 / tickCount < 250);
    }
}
