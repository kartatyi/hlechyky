using System.Text.Json;
using Hlechyky;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Словники для Глек-слова: копія лише малих списків у temp. Бойовий data/words чіпати не можна —
/// у того, хто прогнав setup.ps1, там лежить uk-all.txt, і кожен прогін тестів мовчки будував би
/// стомегабайтну базу (та сама наука, що й у WordsFixture).
/// </summary>
public sealed class WordleWords : IDisposable
{
    public WordleWords()
    {
        Dir = Path.Combine(Path.GetTempPath(), "hlechyky-wordle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        var src = Paths.Resolve("data/words");
        foreach (var name in new[] { "uk-5.txt", "uk-guess.txt", "uk-hangman.txt" })
            File.Copy(Path.Combine(src, name), Path.Combine(Dir, name));
        Words = new Words(Dir);
        Five = [.. File.ReadAllLines(Path.Combine(Dir, "uk-5.txt")).Select(l => l.Trim().ToLowerInvariant()).Where(l => l.Length == 5)];
    }

    public string Dir { get; }
    public Words Words { get; }
    /// <summary>Список відповідей — звідси беремо свідомо валідні «неправильні» спроби.</summary>
    public string[] Five { get; }

    public void Dispose()
    {
        Words.Dispose();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { /* хай лежить у temp */ }
    }
}

/// <summary>
/// Глек-слово (specs/wordle.md) і його стик зі «Щоденним глеком» (specs/daily.md). Партія тут одна
/// на день, тому майже все крутиться навколо дня FakeClock: 2026-09-10, тобто день №1.
/// </summary>
public class WordleTests(WordleWords fx) : IClassFixture<WordleWords>
{
    const string Day = "2026-09-10";

    string Answer => fx.Words.Daily5ForDay(Day);

    /// <summary>Валідні слова, які точно не є відповіддю дня — стільки, скільки просить тест.</summary>
    string[] Wrong(int n) => [.. fx.Five.Where(w => w != Answer).Take(n)];

    RoomHarness Solo(string nick = "Оля")
    {
        var h = new RoomHarness("wordle", services: RoomHarness.WithService(fx.Words));
        h.Solo(nick);
        return h;
    }

    static ActResult Guess(RoomHarness h, string word) => h.Act(0, "guess", new { word });

    static JsonElement V(RoomHarness h) => h.View(0);

    static string[] Rows(JsonElement v) =>
        [.. v.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("word").GetString() + ":" + r.GetProperty("marks").GetString())];

    // ------------------------------------------------------------------------ оцінка спроби (чиста)

    [Fact]
    public void All_five_letters_in_place_is_all_green()
    {
        Assert.Equal("GGGGG", Wordle.Marks("казка", "казка"));
        Assert.Equal("GGGGG", Wordle.Marks("вітер", "вітер"));
    }

    [Fact]
    public void Nothing_in_common_is_all_black()
    {
        Assert.Equal("BBBBB", Wordle.Marks("казка", "вітер"));
        Assert.Equal("BBBBB", Wordle.Marks("вимір", "часто"));
    }

    [Fact]
    public void A_repeated_letter_in_the_guess_gets_only_as_many_yellows_as_the_answer_has()
    {
        // спробі «лілія» відповідь «ялина» дає рівно одну жовту «л» і одну жовту «я»: другої «л» і
        // третьої «і» у відповіді просто нема (той самий випадок, що «калина»/«лілія» зі spec, але на п'ять літер)
        Assert.Equal("YBBBY", Wordle.Marks("ялина", "лілія"));
        // «маска»: «м» і «а» на місці, зайві «м» лишаються чорними, а третя «а» бере жовту
        Assert.Equal("GGBYB", Wordle.Marks("маска", "мамам"));
    }

    [Fact]
    public void Green_takes_the_letter_before_yellow_does()
    {
        // «казка»: обидві «к» і «а» вже пораховані зеленими на своїх місцях
        Assert.Equal("GGYYB", Wordle.Marks("казка", "какао"));
        // у відповіді дві «к» і дві «о» — жовтих вистачає на обидві зайві літери
        Assert.Equal("YGYGB", Wordle.Marks("колок", "локон"));
    }

    [Fact]
    public void A_guess_of_the_wrong_length_is_all_black_and_does_not_throw()
    {
        Assert.Equal("BBBBB", Wordle.Marks("казка", "кіт"));
        Assert.Equal("BBBBB", Wordle.Marks(null, "казка"));
        Assert.Equal("BBBBB", Wordle.Marks("казка", null));
    }

    // ------------------------------------------------------------------------------- слово дня

    [Fact]
    public void The_word_of_the_day_is_the_same_for_everyone_and_for_every_seed()
    {
        var a = new RoomHarness("wordle", seed: 1, services: RoomHarness.WithService(fx.Words));
        var b = new RoomHarness("wordle", seed: 99, services: RoomHarness.WithService(fx.Words));
        a.Solo("Оля");
        b.Solo("Петро");
        var word = Wrong(1)[0];
        Guess(a, word);
        Guess(b, word);
        Assert.Equal(Rows(V(a)), Rows(V(b)));
        Assert.Equal(Wordle.Marks(Answer, word), Rows(V(a))[0].Split(':')[1]);
    }

    [Fact]
    public void Thirty_days_in_a_row_give_almost_thirty_different_words()
    {
        var start = DateOnly.ParseExact(Day, "yyyy-MM-dd");
        var words = Enumerable.Range(0, 30)
            .Select(i => fx.Words.Daily5ForDay(start.AddDays(i).ToString("yyyy-MM-dd")))
            .ToList();
        Assert.All(words, w => Assert.Equal(5, w.Length));
        Assert.True(words.Distinct(StringComparer.Ordinal).Count() >= 25,
            $"на 30 днях різних слів має бути ≥ 25, а їх {words.Distinct(StringComparer.Ordinal).Count()}");
    }

    // ------------------------------------------------------------------------------- кімната дня

    [Fact]
    public void The_room_key_is_the_one_the_daily_service_can_read()
    {
        var h = Solo("Оля");
        Assert.Equal($"daily:wordle:{Day}:оля", h.Room.Key);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void The_catalog_shows_the_game_as_a_daily_private_solo_puzzle()
    {
        var registry = RoomHarness.NewRegistry();
        var row = registry.Catalog.Single(c => c.Id == "wordle");
        Assert.True(row.Daily);
        Assert.True(row.Private);
        Assert.Equal("solo", row.Group);
        Assert.Equal(1, row.MaxPlayers);
        Assert.Equal("wordle", row.Module);
        Assert.True(row.HasCss, "web/games/wordle.css має лежати поруч із модулем");
    }

    // ------------------------------------------------------------------------------------ ходи

    [Fact]
    public void The_payload_is_the_object_the_browser_module_sends()
    {
        var h = Solo();
        // web/games/wordle.js шле рівно { word }; голий рядок теж приймаємо
        Assert.True(h.Act(0, "guess", new { word = Wrong(2)[0] }).Ok);
        Assert.True(h.Act(0, "guess", Wrong(2)[1]).Ok);
        Assert.Equal(2, V(h).GetProperty("attempts").GetInt32());
    }

    [Fact]
    public void An_unknown_word_costs_no_attempt_and_changes_nothing()
    {
        var h = Solo();
        var before = Views.Text(h.Room.Game.View(0));
        var r = Guess(h, "ххххх");
        Assert.False(r.Ok);
        Assert.Equal("Такого слова не знаю", r.Message);
        Assert.Equal(before, Views.Text(h.Room.Game.View(0)));
        Assert.Equal(0, V(h).GetProperty("attempts").GetInt32());
    }

    [Fact]
    public void A_word_that_is_not_five_ukrainian_letters_is_refused()
    {
        var h = Solo();
        Assert.Equal("Треба рівно п'ять українських літер", Guess(h, "кіт").Message);
        Assert.Equal("Треба рівно п'ять українських літер", Guess(h, "глечики").Message);
        Assert.Equal("Треба рівно п'ять українських літер", Guess(h, "house").Message);
        // слів з апострофом у наших списках нема свідомо — Normalize відкидає їх ще до словника
        Assert.Equal("Треба рівно п'ять українських літер", Guess(h, "м'ята").Message);
        Assert.Equal("Не зрозумів, що за слово", h.Act(0, "guess", new { nope = 1 }).Message);
        Assert.Equal(0, V(h).GetProperty("attempts").GetInt32());
    }

    [Fact]
    public void A_word_typed_in_capitals_or_with_spaces_is_still_the_same_word()
    {
        var h = Solo();
        var word = Wrong(1)[0];
        Assert.True(Guess(h, "  " + word.ToUpperInvariant() + " ").Ok);
        // у дошці лежить нормалізоване слово, а не те, що прийшло з дроту
        Assert.Equal(word, V(h).GetProperty("rows")[0].GetProperty("word").GetString());
    }

    [Fact]
    public void An_unknown_action_is_refused()
    {
        var h = Solo();
        Assert.Equal("Тут так не ходять", h.Act(0, "move", new { cell = 1 }).Message);
    }

    [Fact]
    public void A_letter_of_a_guess_is_counted_in_the_view_the_way_the_rules_say()
    {
        var h = Solo();
        var word = Wrong(1)[0];
        Assert.True(Guess(h, word).Ok);
        var v = V(h);
        Assert.Equal(1, v.GetProperty("attempts").GetInt32());
        Assert.Equal(6, v.GetProperty("max").GetInt32());
        Assert.Equal(word, v.GetProperty("rows")[0].GetProperty("word").GetString());
        Assert.Equal(Wordle.Marks(Answer, word), v.GetProperty("rows")[0].GetProperty("marks").GetString());
    }

    // ---------------------------------------------------------------------------------- перемога

    [Fact]
    public void Guessing_the_word_finishes_the_day_and_reports_it_once()
    {
        var h = Solo();
        var wrong = Wrong(2);
        Guess(h, wrong[0]);
        Assert.True(Guess(h, Answer).Ok);

        var v = V(h);
        Assert.True(v.GetProperty("solved").GetBoolean());
        Assert.False(v.GetProperty("failed").GetBoolean());
        Assert.Equal(2, v.GetProperty("attempts").GetInt32());
        Assert.Equal(RoomStatus.Finished, h.Room.Status);

        var score = Assert.Single(h.Scores);
        Assert.Equal(2, score.Score);
        Assert.Equal($"daily:wordle:{Day}:оля", score.Key);
        Assert.Equal(ScoreOrder.LowerIsBetter, score.Order);

        var award = Assert.Single(h.Awards);
        Assert.Equal("daily:wordle", award.Reason);
        Assert.Equal(0, award.Shards);   // нуль — «плати типову щоденну» (specs/daily.md)
    }

    [Fact]
    public void The_journal_tells_the_others_how_many_tries_it_took_but_not_the_word()
    {
        var h = Solo();
        Guess(h, Answer);
        var line = h.Outbox.OfType<Journal>().Last().Text;
        Assert.Contains("Оля", line, StringComparison.Ordinal);
        Assert.Contains("слово дня", line, StringComparison.Ordinal);
        Assert.DoesNotContain(Answer, line, StringComparison.Ordinal);
    }

    [Fact]
    public void After_the_word_is_taken_nothing_else_is_accepted()
    {
        var h = Solo();
        Guess(h, Answer);
        var again = Guess(h, Wrong(1)[0]);
        Assert.False(again.Ok);
        Assert.Equal(1, V(h).GetProperty("attempts").GetInt32());
    }

    [Fact]
    public void The_sixth_guess_can_still_win_the_day()
    {
        // межа, де сходяться обидві умови кінця: спроби вичерпані І слово вгадане. Перемога має бути
        // сильнішою за вичерпання, інакше остання правильна спроба рахувалась би поразкою.
        var h = Solo();
        foreach (var w in Wrong(5)) Assert.True(Guess(h, w).Ok);
        Assert.True(Guess(h, Answer).Ok);

        var v = V(h);
        Assert.True(v.GetProperty("solved").GetBoolean());
        Assert.False(v.GetProperty("failed").GetBoolean());
        Assert.Equal(6, v.GetProperty("attempts").GetInt32());
        Assert.StartsWith("Глек-слово #1 6/6", v.GetProperty("share").GetString(), StringComparison.Ordinal);
        Assert.Equal(6, Assert.Single(h.Scores).Score);
        Assert.Single(h.Awards);
    }

    // ---------------------------------------------------------------------------------- поразка

    [Fact]
    public void Six_misses_end_the_day_show_the_word_and_report_nothing()
    {
        var h = Solo();
        foreach (var w in Wrong(6)) Assert.True(Guess(h, w).Ok);

        var v = V(h);
        Assert.True(v.GetProperty("failed").GetBoolean());
        Assert.False(v.GetProperty("solved").GetBoolean());
        Assert.Equal(Answer, v.GetProperty("answer").GetString());
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Empty(h.Scores);
        Assert.Empty(h.Awards);
    }

    [Fact]
    public void A_seventh_guess_never_happens()
    {
        var h = Solo();
        foreach (var w in Wrong(6)) Guess(h, w);
        var seventh = Guess(h, Answer);
        Assert.False(seventh.Ok);
        Assert.Equal(6, V(h).GetProperty("attempts").GetInt32());
        Assert.False(V(h).GetProperty("solved").GetBoolean());
    }

    [Fact]
    public void Losing_the_day_is_not_announced_to_everyone()
    {
        var h = Solo();
        var before = h.Outbox.OfType<Journal>().Count();
        foreach (var w in Wrong(6)) Guess(h, w);
        Assert.Equal(before, h.Outbox.OfType<Journal>().Count());
    }

    // ------------------------------------------------------------------------------------- вид

    [Fact]
    public void The_answer_is_not_in_the_view_until_the_day_is_over()
    {
        var h = Solo();
        Assert.Equal(JsonValueKind.Null, V(h).GetProperty("answer").ValueKind);
        Guess(h, Wrong(1)[0]);
        var v = V(h);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("answer").ValueKind);
        Assert.DoesNotContain(Answer, Views.Text(h.Room.Game.View(0)), StringComparison.Ordinal);
        // і глядачеві теж нічого не світить
        Assert.DoesNotContain(Answer, Views.Text(h.Room.Game.View(null)), StringComparison.Ordinal);
    }

    [Fact]
    public void The_view_carries_the_fields_the_spec_promises()
    {
        var h = Solo();
        var v = V(h);
        foreach (var name in new[] { "day", "no", "rows", "attempts", "max", "solved", "failed", "answer", "keys", "share" })
            Assert.True(Views.Has(v, name), $"у виді нема поля {name}");
        Assert.Equal(Day, v.GetProperty("day").GetString());
        Assert.Equal(1, v.GetProperty("no").GetInt32());   // 10 вересня 2026 — день №1
        Assert.Equal(JsonValueKind.Null, v.GetProperty("share").ValueKind);
    }

    [Fact]
    public void The_keyboard_state_keeps_the_best_colour_of_each_letter()
    {
        var h = Solo();
        // беремо слово, у якому перша літера відповіді стоїть не на своєму місці, а потім — саму відповідь
        var word = fx.Five.First(w => w != Answer && w.Contains(Answer[0]) && w[0] != Answer[0]);
        Guess(h, word);
        var yellowish = V(h).GetProperty("keys").GetProperty(Answer[0].ToString()).GetString();
        Assert.Contains(yellowish, new[] { "Y", "G" });

        Guess(h, Answer);
        var keys = V(h).GetProperty("keys");
        foreach (var ch in Answer.Distinct())
            Assert.Equal("G", keys.GetProperty(ch.ToString()).GetString());
    }

    // ----------------------------------------------------------------------------------- share

    [Fact]
    public void The_share_line_is_a_grid_of_emoji_without_a_single_letter()
    {
        var h = Solo();
        var wrong = Wrong(2);
        Guess(h, wrong[0]);
        Guess(h, wrong[1]);
        Guess(h, Answer);

        var share = V(h).GetProperty("share").GetString()!;
        var lines = share.Split('\n');
        Assert.Equal("Глек-слово #1 3/6", lines[0]);
        Assert.Equal(4, lines.Length);
        Assert.Equal(Emoji(Wordle.Marks(Answer, wrong[0])), lines[1]);
        Assert.Equal(Emoji("GGGGG"), lines[3]);
        Assert.DoesNotContain(Answer, share, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lost_day_shares_an_X_instead_of_the_number_of_tries()
    {
        var h = Solo();
        foreach (var w in Wrong(6)) Guess(h, w);
        var share = V(h).GetProperty("share").GetString()!;
        Assert.StartsWith("Глек-слово #1 X/6", share, StringComparison.Ordinal);
        Assert.Equal(7, share.Split('\n').Length);
    }

    static string Emoji(string marks) =>
        string.Concat(marks.Select(m => m switch { 'G' => "🟩", 'Y' => "🟨", _ => "⬛" }));

    // ------------------------------------------------------------------------------- Save/Load

    [Fact]
    public void Load_of_what_Save_wrote_gives_the_same_view()
    {
        var h = Solo();
        Guess(h, Wrong(2)[0]);
        Guess(h, Wrong(2)[1]);
        var json = h.Room.Game.Save()!;
        var before = Views.Text(h.Room.Game.View(0));

        var fresh = Solo("Петро");
        fresh.Room.Game.Load(json);
        Assert.Equal(before, Views.Text(fresh.Room.Game.View(0)));
    }

    [Fact]
    public void Yesterdays_state_does_not_leak_into_todays_board()
    {
        var yesterday = JsonSerializer.Serialize(
            new { day = "2026-09-09", guesses = new[] { Wrong(1)[0] }, solved = false, failed = false });
        var h = Solo();
        h.Room.Game.Load(yesterday);
        Assert.Equal(0, V(h).GetProperty("attempts").GetInt32());
        Assert.Equal(Day, V(h).GetProperty("day").GetString());
    }

    [Fact]
    public void A_broken_saved_state_leaves_the_board_clean_instead_of_breaking_the_room()
    {
        var h = Solo();
        h.Room.Game.Load("{ отаке");
        h.Room.Game.Load("null");
        Assert.Equal(0, V(h).GetProperty("attempts").GetInt32());
        Assert.True(Guess(h, Wrong(1)[0]).Ok);
    }

    [Fact]
    public void A_saved_state_with_the_answer_in_the_middle_comes_back_as_a_solved_day()
    {
        // прапорці зі сховища ми не читаємо, а рахуємо зі спроб; спроб після вгаданого слова у грі
        // статись не могло, тому хвіст такого запису відрізаємо, а день піднімаємо розв'язаним
        var h = Solo();
        h.Room.Game.Load(JsonSerializer.Serialize(
            new { day = Day, guesses = new[] { Answer, Wrong(1)[0] }, solved = false, failed = false }));

        var v = V(h);
        Assert.Equal(1, v.GetProperty("attempts").GetInt32());
        Assert.True(v.GetProperty("solved").GetBoolean());
        Assert.False(v.GetProperty("failed").GetBoolean());
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.False(Guess(h, Wrong(2)[1]).Ok);   // «вгадати» ще раз уже не вийде
    }

    [Fact]
    public void The_day_survives_closing_the_tab()
    {
        var h = Solo();
        var word = Wrong(1)[0];
        Guess(h, word);
        var key = h.Room.Key!;
        Assert.True(h.Store.States.ContainsKey(key));

        h.Leave("Оля");                 // соло-кімнату без людей каркас прибирає одразу
        h.Solo("Оля");                  // …а тут вона відкривається наново — уже зі сховища
        var rows = Rows(V(h));
        Assert.Single(rows);
        Assert.StartsWith(word, rows[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_solved_day_comes_back_solved_and_is_not_paid_for_twice()
    {
        var h = Solo();
        Guess(h, Answer);
        var key = h.Room.Key!;
        h.Leave("Оля");
        h.Solo("Оля");

        Assert.True(V(h).GetProperty("solved").GetBoolean());
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Single(h.Scores);
        Assert.Single(h.Awards);
        Assert.Equal(key, h.Room.Key);
    }

    // ----------------------------------------------------------------------------------- рематч

    [Fact]
    public void Another_go_at_the_same_day_gives_the_same_board_not_six_new_tries()
    {
        var h = Solo();
        Guess(h, Wrong(1)[0]);
        Guess(h, Answer);
        Assert.True(h.Rematch("Оля").Ok);

        var v = V(h);
        Assert.True(v.GetProperty("solved").GetBoolean());
        Assert.Equal(2, v.GetProperty("attempts").GetInt32());
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Single(h.Scores);
        Assert.Single(h.Awards);
    }

    [Fact]
    public void Another_go_does_not_shout_into_the_shared_journal_a_second_time()
    {
        // кнопки «Ще раз» у щоденних на картці нема, але метод хаба відкритий кожному, хто сидить у
        // кімнаті: без прапорця одна людина з консолі залила б спільний Журнал скільки завгодно разів
        var h = Solo();
        Guess(h, Answer);
        var announced = h.Outbox.OfType<Journal>().Count(j => j.Text.Contains("слово дня", StringComparison.Ordinal));
        Assert.Equal(1, announced);

        for (var i = 0; i < 3; i++) Assert.True(h.Rematch("Оля").Ok);

        Assert.Equal(1, h.Outbox.OfType<Journal>().Count(j => j.Text.Contains("слово дня", StringComparison.Ordinal)));
        Assert.Single(h.Scores);
        Assert.Single(h.Awards);
        Assert.True(V(h).GetProperty("solved").GetBoolean());
    }

    [Fact]
    public void A_day_lifted_from_the_store_is_not_announced_all_over_again()
    {
        var h = Solo();
        Guess(h, Answer);
        var announced = h.Outbox.OfType<Journal>().Count(j => j.Text.Contains("слово дня", StringComparison.Ordinal));

        h.Leave("Оля");
        h.Solo("Оля");
        Assert.True(h.Rematch("Оля").Ok);

        Assert.Equal(announced, h.Outbox.OfType<Journal>().Count(j => j.Text.Contains("слово дня", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_new_day_is_a_new_room_with_a_clean_board()
    {
        var h = Solo();
        Guess(h, Wrong(1)[0]);
        Guess(h, Answer);

        h.Clock.Advance(TimeSpan.FromDays(1));
        Assert.True(h.Solo("Оля").Ok);   // панель тисне «Грати» — ключ уже з новим днем

        var v = V(h);
        Assert.Equal("2026-09-11", v.GetProperty("day").GetString());
        Assert.Equal(2, v.GetProperty("no").GetInt32());
        Assert.Equal(0, v.GetProperty("attempts").GetInt32());
        Assert.False(v.GetProperty("solved").GetBoolean());
        Assert.Equal("daily:wordle:2026-09-11:оля", h.Room.Key);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void A_room_opened_yesterday_stays_yesterdays_puzzle()
    {
        var h = Solo();
        Guess(h, Wrong(1)[0]);
        // посеред партії «Ще раз» не проходить узагалі — це каркас, а не гра
        Assert.False(h.Rematch("Оля").Ok);

        Guess(h, Answer);
        h.Clock.Advance(TimeSpan.FromDays(1));
        Assert.True(h.Rematch("Оля").Ok);   // тепер рематч законний — і Start() справді виконується

        // день кімнати стоїть у її ключі, і саме за ним запишеться результат — підміняти його
        // посеред життя кімнати не можна навіть після півночі
        var v = V(h);
        Assert.Equal(Day, v.GetProperty("day").GetString());
        Assert.Equal(1, v.GetProperty("no").GetInt32());
        Assert.Equal(2, v.GetProperty("attempts").GetInt32());
        Assert.Equal($"daily:wordle:{Day}:оля", h.Room.Key);
    }

    // ------------------------------------------------------------------------------ нема словника

    [Fact]
    public void Without_a_dictionary_the_game_says_so_instead_of_falling_over()
    {
        var h = new RoomHarness("wordle");   // порожній провайдер — Words у ньому нема взагалі
        Assert.True(h.Solo("Оля").Ok);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);

        var v = V(h);
        Assert.True(v.GetProperty("noWords").GetBoolean());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("answer").ValueKind);
        Assert.Equal("Словника нема — сьогодні без слова", Guess(h, "казка").Message);
    }

    [Fact]
    public void An_empty_word_folder_is_the_same_kind_of_quiet_refusal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hlechyky-wordle-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var words = new Words(dir);
            var h = new RoomHarness("wordle", services: RoomHarness.WithService(words));
            h.Solo("Оля");
            Assert.True(V(h).GetProperty("noWords").GetBoolean());
            Assert.False(Guess(h, "казка").Ok);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* хай лежить */ } }
    }
}
