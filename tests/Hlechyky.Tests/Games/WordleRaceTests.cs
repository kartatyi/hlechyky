using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Глек-слово наввипередки: одне слово на всіх, шість спроб кожному, раунди, очки «7 мінус спроби» і
/// +1 першому. FakeClock стоїть на 2026-09-10 — день №1 «Щоденного глека».
/// </summary>
public class WordleRaceTests(WordleWords fx) : IClassFixture<WordleWords>
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Тарас", "Ірина", "Сашко"];

    RoomHarness Table(int players = 3, object? options = null, int seed = 1)
    {
        var h = new RoomHarness("wordle-race", options, seed, RoomHarness.WithService(fx.Words));
        for (var i = 0; i < players; i++) Assert.True(h.Join(Nicks[i]).Ok, h.Reply.Message);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static WordleRace Game(RoomHarness h) => (WordleRace)h.Room.Game;
    static string Answer(RoomHarness h) => Game(h).Answer;

    /// <summary>Валідні слова, які точно не відповідь цього раунду.</summary>
    string[] Wrong(RoomHarness h, int n) => [.. fx.Five.Where(w => w != Answer(h)).Take(n)];

    static ActResult Guess(RoomHarness h, int seat, string word) => h.Act(seat, "guess", new { word });

    static JsonElement Player(JsonElement view, int seat) =>
        view.GetProperty("players").EnumerateArray().Single(p => p.GetProperty("seat").GetInt32() == seat);

    static int Total(RoomHarness h, int seat) => Player(h.View(0), seat).GetProperty("total").GetInt32();

    /// <summary>Годинник уперед на стільки секунд, скільки треба, тиками гри.</summary>
    static void Wait(RoomHarness h, int seconds) => h.Tick(seconds * 1000 / h.Room.Info.TickMs + 1);

    // ------------------------------------------------------------------------------------ паспорт

    [Fact]
    public void Passport_is_a_party_table_for_two_to_six_sharing_the_wordle_module()
    {
        var info = new WordleRace().Info;
        Assert.Equal("wordle-race", info.Id);
        Assert.Equal(GameGroup.Party, info.Group);
        Assert.Equal((2, 6), (info.MinPlayers, info.MaxPlayers));
        Assert.Equal(StartMode.ByHost, info.Start);
        Assert.True(info.Hidden);
        Assert.False(info.Private);
        Assert.Equal("wordle", info.Module);
        Assert.False(new WordleRace() is IDailyGame);
    }

    [Fact]
    public void The_daily_wordle_stays_solo_and_daily()
    {
        var info = new Wordle().Info;
        Assert.Equal((1, 1), (info.MinPlayers, info.MaxPlayers));
        Assert.True(new Wordle() is IDailyGame);
    }

    // ------------------------------------------------------------------------------------ старт

    [Fact]
    public void Start_gives_everyone_an_empty_board_and_a_ticking_round()
    {
        var h = Table(3);
        var v = h.View(1);
        Assert.Equal("play", v.GetProperty("phase").GetString());
        Assert.Equal(1, v.GetProperty("round").GetInt32());
        Assert.Equal(WordleRace.DefaultRounds, v.GetProperty("rounds").GetInt32());
        Assert.Equal(3, v.GetProperty("players").GetArrayLength());
        Assert.Equal(0, v.GetProperty("me").GetProperty("attempts").GetInt32());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("answer").ValueKind);
        Assert.Equal(h.Clock.UtcNow.AddSeconds(WordleRace.DefaultSeconds), v.GetProperty("endsAt").GetDateTimeOffset());
        Assert.Equal(5, Answer(h).Length);
    }

    [Fact]
    public void One_player_cannot_start()
    {
        var h = new RoomHarness("wordle-race", services: RoomHarness.WithService(fx.Words));
        h.Join("Оля");
        Assert.False(h.Start().Ok);
    }

    [Fact]
    public void Six_players_fit_and_play()
    {
        var h = Table(6);
        Assert.Equal(6, h.View(5).GetProperty("players").GetArrayLength());
        Assert.True(Guess(h, 5, Answer(h)).Ok);
    }

    [Fact]
    public void Options_set_rounds_and_time_and_nonsense_falls_back()
    {
        var h = Table(2, new { rounds = "1", seconds = "90" });
        var v = h.View(0);
        Assert.Equal(1, v.GetProperty("rounds").GetInt32());
        Assert.Equal(90, v.GetProperty("seconds").GetInt32());

        var bad = Table(2, new { rounds = "100", seconds = "1" });
        Assert.Equal(WordleRace.DefaultRounds, bad.View(0).GetProperty("rounds").GetInt32());
        Assert.Equal(WordleRace.DefaultSeconds, bad.View(0).GetProperty("seconds").GetInt32());
    }

    [Fact]
    public void The_word_is_never_the_daily_word_nor_its_neighbours()
    {
        var near = Enumerable.Range(-7, 15)
            .Select(d => fx.Words.Daily5ForDay(new DateOnly(2026, 9, 10).AddDays(d).ToString("yyyy-MM-dd")))
            .ToHashSet();
        for (var seed = 1; seed <= 40; seed++)
        {
            var h = Table(2, new { rounds = "5" }, seed);
            for (var r = 0; r < 5; r++)
            {
                Assert.DoesNotContain(Answer(h), near);
                Guess(h, 0, Answer(h));
                Guess(h, 1, Answer(h));
                if (r < 4) Wait(h, WordleRace.RevealSeconds);
            }
        }
    }

    [Fact]
    public void Words_do_not_repeat_within_a_match()
    {
        var h = Table(2, new { rounds = "5" });
        var seen = new List<string>();
        for (var r = 0; r < 5; r++)
        {
            seen.Add(Answer(h));
            Guess(h, 0, Answer(h));
            Guess(h, 1, Answer(h));
            if (r < 4) Wait(h, WordleRace.RevealSeconds);
        }
        Assert.Equal(5, seen.Distinct().Count());
        Assert.Equal(seen, h.View(null).GetProperty("answers").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public void Same_seed_gives_the_same_words()
    {
        Assert.Equal(Answer(Table(2, seed: 7)), Answer(Table(2, seed: 7)));
    }

    // ------------------------------------------------------------------------------------ спроби

    [Fact]
    public void My_letters_are_mine_rivals_see_only_colours()
    {
        var h = Table(3);
        var w = Wrong(h, 1)[0];
        Assert.True(Guess(h, 0, w).Ok);

        var mine = h.View(0);
        Assert.Equal(w, mine.GetProperty("me").GetProperty("rows")[0].GetProperty("word").GetString());
        Assert.Equal(w, Player(mine, 0).GetProperty("words")[0].GetString());

        var rival = h.View(1);
        var olya = Player(rival, 0);
        Assert.Equal(Wordle.Marks(Answer(h), w), olya.GetProperty("marks")[0].GetString());
        Assert.Equal(JsonValueKind.Null, olya.GetProperty("words").ValueKind);
        Assert.DoesNotContain(w, rival.GetRawText());
    }

    [Fact]
    public void A_guess_reaches_everyone_on_the_next_tick()
    {
        // гра з тиком для каркаса — реалтайм: після Act він видів не шле, їх має попросити тик
        var h = Table(2);
        Guess(h, 0, Wrong(h, 1)[0]);
        var before = h.Outbox.OfType<RoomViews>().Count();
        h.Tick();
        Assert.Equal(before + 1, h.Outbox.OfType<RoomViews>().Count());
        h.Tick();
        Assert.Equal(before + 1, h.Outbox.OfType<RoomViews>().Count());   // нічого не змінилось — тиша
    }

    [Fact]
    public void A_spectator_sees_colours_but_no_letters_and_no_answer()
    {
        var h = Table(2);
        var w = Wrong(h, 1)[0];
        Guess(h, 0, w);
        var v = h.View(null);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("me").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("answer").ValueKind);
        Assert.DoesNotContain(w, v.GetRawText());
        Assert.DoesNotContain(Answer(h), v.GetRawText());
        Assert.Equal(1, Player(v, 0).GetProperty("attempts").GetInt32());
    }

    [Fact]
    public void Unknown_or_short_words_do_not_cost_a_try()
    {
        var h = Table(2);
        Assert.False(Guess(h, 0, "абвгд").Ok);
        Assert.False(Guess(h, 0, "кіт").Ok);
        Assert.False(h.Act(0, "dance").Ok);
        Assert.Equal(0, h.View(0).GetProperty("me").GetProperty("attempts").GetInt32());
    }

    [Fact]
    public void Solving_scores_seven_minus_tries_and_the_first_gets_a_bonus()
    {
        var h = Table(3);
        var wrong = Wrong(h, 2);
        Guess(h, 1, wrong[0]);
        Guess(h, 1, wrong[1]);
        var first = Guess(h, 1, Answer(h));        // третя спроба, перший: 4 + 1
        Assert.True(first.Ok);
        Assert.Contains("Перший", first.Message);
        var second = Guess(h, 0, Answer(h));       // перша спроба, але другий: 6
        Assert.Contains("+6", second.Message);

        var v = h.View(2);
        Assert.Equal(5, Player(v, 1).GetProperty("gained").GetInt32());
        Assert.True(Player(v, 1).GetProperty("first").GetBoolean());
        Assert.Equal(6, Player(v, 0).GetProperty("gained").GetInt32());
        Assert.False(Player(v, 0).GetProperty("first").GetBoolean());
    }

    [Fact]
    public void After_solving_or_six_misses_no_more_guesses()
    {
        var h = Table(3);
        Guess(h, 0, Answer(h));
        Assert.False(Guess(h, 0, Answer(h)).Ok);

        foreach (var w in Wrong(h, 6)) Assert.True(Guess(h, 1, w).Ok);
        Assert.True(h.View(1).GetProperty("me").GetProperty("failed").GetBoolean());
        Assert.False(Guess(h, 1, Answer(h)).Ok);
        Assert.Equal("play", h.View(2).GetProperty("phase").GetString());   // Ганна ще грає
    }

    [Fact]
    public void When_everyone_is_done_the_round_is_revealed_with_all_the_letters()
    {
        var h = Table(2);
        var w = Wrong(h, 1)[0];
        var answer = Answer(h);
        Guess(h, 0, w);
        Guess(h, 0, answer);
        Guess(h, 1, answer);

        var v = h.View(null);
        Assert.Equal("reveal", v.GetProperty("phase").GetString());
        Assert.Equal(answer, v.GetProperty("answer").GetString());
        Assert.Equal(w, Player(v, 0).GetProperty("words")[0].GetString());
        Assert.False(Guess(h, 0, answer).Ok);
    }

    [Fact]
    public void After_the_pause_a_new_word_comes_and_boards_are_clean()
    {
        var h = Table(2);
        var one = Answer(h);
        Guess(h, 0, one);
        Guess(h, 1, one);
        Wait(h, WordleRace.RevealSeconds);

        var v = h.View(0);
        Assert.Equal("play", v.GetProperty("phase").GetString());
        Assert.Equal(2, v.GetProperty("round").GetInt32());
        Assert.NotEqual(one, Answer(h));
        Assert.Equal(0, v.GetProperty("me").GetProperty("attempts").GetInt32());
        Assert.Equal(7, Player(v, 0).GetProperty("total").GetInt32());     // 6 + 1 першому
        Assert.Equal(6, Player(v, 1).GetProperty("total").GetInt32());
    }

    [Fact]
    public void Time_runs_out_and_the_slow_ones_get_nothing()
    {
        var h = Table(2, new { seconds = "90" });
        Guess(h, 0, Answer(h));
        Wait(h, 89);
        Assert.Equal("play", h.View(1).GetProperty("phase").GetString());
        Wait(h, 2);
        var v = h.View(1);
        Assert.Equal("reveal", v.GetProperty("phase").GetString());
        Assert.True(v.GetProperty("me").GetProperty("failed").GetBoolean());
        Assert.Equal(0, Player(v, 1).GetProperty("total").GetInt32());
    }

    // ------------------------------------------------------------------------------------ кінець

    [Fact]
    public void The_last_round_finishes_the_match_with_totals_as_scores()
    {
        var h = Table(3, new { rounds = "1" });
        Guess(h, 2, Answer(h));
        Guess(h, 0, Wrong(h, 1)[0]);
        Guess(h, 0, Answer(h));
        foreach (var w in Wrong(h, 6)) Guess(h, 1, w);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var e = Assert.Single(h.Finished);
        Assert.Equal([2], e.Result.Winners);
        Assert.Equal(7, e.Result.Scores![2]);
        Assert.Equal(5, e.Result.Scores[0]);
        Assert.Equal(0, e.Result.Scores[1]);
        Assert.Contains("Ганна", e.Result.Text);
        Assert.Equal("done", h.View(0).GetProperty("phase").GetString());
        Assert.Equal(Answer(h), h.View(1).GetProperty("answer").GetString());
    }

    [Fact]
    public void Nobody_solving_anything_is_a_draw()
    {
        var h = Table(2, new { rounds = "1", seconds = "90" });
        Wait(h, 91);
        Assert.Empty(Assert.Single(h.Finished).Result.Winners);
    }

    [Fact]
    public void Equal_totals_share_the_win()
    {
        var h = Table(2, new { rounds = "3" });
        // раунд 1: Оля перша з першої (7), Петро з першої (6); раунд 2 — навпаки; раунд 3 — ніхто
        Guess(h, 0, Answer(h)); Guess(h, 1, Answer(h));
        Wait(h, WordleRace.RevealSeconds);
        Guess(h, 1, Answer(h)); Guess(h, 0, Answer(h));
        Wait(h, WordleRace.RevealSeconds);
        Wait(h, WordleRace.DefaultSeconds);
        Assert.Equal([0, 1], Assert.Single(h.Finished).Result.Winners.Order());
    }

    [Fact]
    public void Someone_leaving_does_not_stop_the_others()
    {
        var h = Table(3);
        Guess(h, 0, Answer(h));
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.True(Player(h.View(0), 1).GetProperty("gone").GetBoolean());
        Assert.Equal("Петро", Player(h.View(0), 1).GetProperty("nick").GetString());   // не «—»: дошка лишається підписаною
        // решта дограла — раунд не чекає на того, хто пішов
        Guess(h, 2, Answer(h));
        Assert.Equal("reveal", h.View(0).GetProperty("phase").GetString());
    }

    [Fact]
    public void The_last_one_at_the_table_wins_with_the_points_so_far()
    {
        var h = Table(2);
        Guess(h, 1, Answer(h));
        h.Leave("Оля");
        var e = Assert.Single(h.Finished);
        Assert.Equal([1], e.Result.Winners);
        Assert.Equal(7, e.Result.Scores![1]);
    }

    [Fact]
    public void Rematch_starts_clean()
    {
        var h = Table(2, new { rounds = "1" });
        var old = Answer(h);
        Guess(h, 0, old);
        Guess(h, 1, old);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);

        Assert.True(h.Rematch().Ok, h.Reply.Message);
        var v = h.View(0);
        Assert.Equal("play", v.GetProperty("phase").GetString());
        Assert.Equal(1, v.GetProperty("round").GetInt32());
        Assert.All(v.GetProperty("players").EnumerateArray(), p => Assert.Equal(0, p.GetProperty("total").GetInt32()));
        Assert.Equal(1, v.GetProperty("answers").GetArrayLength() + 1);
    }

    [Fact]
    public void Keys_summary_prefers_green_over_yellow()
    {
        var keys = Wordle.KeysOf("казка", ["акула", "казка"]);
        Assert.Equal("G", keys["а"]);
        Assert.Equal("G", keys["к"]);
        Assert.Equal("B", keys["у"]);
    }
}
