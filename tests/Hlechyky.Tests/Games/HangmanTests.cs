using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Словники для Віселиці: не бойові списки, а два маленькі власні. Один зі слова «корова» —
/// щоб у тесті було видно, яку саме літеру відкрито; другий із шістнадцяти слів — для детермінізму.
/// Бойовий data/words тут ні до чого: тест має читатись, а не залежати від того, що випало.
/// </summary>
public sealed class HangmanWords : IDisposable
{
    /// <summary>Слово єдиного списку: шість позицій, п'ять різних літер, «о» — двічі.</summary>
    public const string Word = "корова";

    /// <summary>Слово, у якому є і «і», і «и»: на ньому видно, що це різні літери, а не одна.</summary>
    public const string Tricky = "світлиця";

    public static readonly string[] Many =
    [
        "береза", "вишенька", "вулиця", "гарбуз", "глечик", "джерело", "дорога", "калина",
        "корова", "криниця", "полуниця", "смерека", "сонечко", "сопілка", "хатина", "яблуко",
    ];

    readonly string _one, _many, _empty, _tricky;

    public HangmanWords()
    {
        _one = Dir("one");
        _many = Dir("many");
        _empty = Dir("empty");
        _tricky = Dir("tricky");
        File.WriteAllLines(Path.Combine(_one, "uk-hangman.txt"), [Word]);
        File.WriteAllLines(Path.Combine(_many, "uk-hangman.txt"), Many);
        File.WriteAllLines(Path.Combine(_tricky, "uk-hangman.txt"), [Tricky]);
        One = new Words(_one);
        Lots = new Words(_many);
        None = new Words(_empty);
        Pair = new Words(_tricky);
    }

    /// <summary>Список з одного слова: яке слово випаде, знає й тест.</summary>
    public Words One { get; }
    /// <summary>Список зі «світлиці»: «і» й «и» стоять поруч в одному слові.</summary>
    public Words Pair { get; }
    /// <summary>Шістнадцять слів: те, на чому видно детермінізм сіду.</summary>
    public Words Lots { get; }
    /// <summary>Порожній каталог: словника нема взагалі.</summary>
    public Words None { get; }

    static string Dir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"hlechyky-hangman-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var w in new[] { One, Lots, None, Pair }) w.Dispose();
        foreach (var dir in new[] { _one, _many, _empty, _tricky })
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* хай лежить у temp */ }
    }
}

/// <summary>
/// Віселиця (specs/hangman.md): одне слово на всіх, вільна черга, спільна шибениця, п'ять слів у партії.
/// </summary>
public class HangmanTests(HangmanWords fx) : IClassFixture<HangmanWords>
{
    /// <summary>Скільки тиків триває пауза між словами.</summary>
    static readonly int PauseTicks = Hangman.PauseMs / Hangman.TickMs;

    /// <summary>Літери, яких у «корові» нема, — рівно вісім, тобто ціла шибениця.</summary>
    static readonly string[] Misses = ["б", "г", "ґ", "д", "е", "є", "ж", "з"];

    RoomHarness Table(params string[] nicks) => Table(fx.One, 42, nicks);

    static RoomHarness Table(Words words, int seed, params string[] nicks)
    {
        var h = new RoomHarness("hangman", seed: seed, services: RoomHarness.WithService(words));
        foreach (var nick in nicks) h.Join(nick);
        h.Start();
        return h;
    }

    /// <summary>Хід із паузою: без неї другий поспіль з'їв би ліміт «одна дія на секунду».</summary>
    static ActResult Guess(RoomHarness h, int seat, string letter)
    {
        h.Clock.Advance(1);
        return h.Act(seat, "guess", new { letter });
    }

    static ActResult Word(RoomHarness h, int seat, string text)
    {
        h.Clock.Advance(1);
        return h.Act(seat, "word", new { text });
    }

    /// <summary>Дочекатись наступного слова.</summary>
    static void NextWord(RoomHarness h) => h.Tick(PauseTicks);

    static string Mask(RoomHarness h) => h.View(0).GetProperty("mask").GetString()!;
    static string Phase(RoomHarness h) => h.View(0).GetProperty("phase").GetString()!;
    static int Score(RoomHarness h, int seat) => h.View(0).GetProperty("scores")[seat].GetInt32();

    // ---------------------------------------------------------------- слово і риски

    [Fact]
    public void The_word_starts_as_dashes_only()
    {
        var h = Table("Оля");
        var v = h.View(0);

        Assert.Equal("______", v.GetProperty("mask").GetString());
        Assert.Equal(1, v.GetProperty("round").GetInt32());
        Assert.Equal(Hangman.Rounds, v.GetProperty("of").GetInt32());
        Assert.Equal(0, v.GetProperty("errors").GetInt32());
        Assert.Equal(Hangman.MaxErrors, v.GetProperty("maxErrors").GetInt32());
        Assert.Equal("play", v.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("revealed").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("result").ValueKind);
        Assert.Empty(v.GetProperty("wrong").EnumerateArray());
    }

    [Fact]
    public void A_letter_opens_every_place_it_stands_on_and_pays_for_each()
    {
        var h = Table("Оля");
        Assert.True(Guess(h, 0, "о").Ok);

        Assert.Equal("_о_о__", Mask(h));
        Assert.Equal(2, Score(h, 0));            // «о» стоїть двічі — два очки
        Assert.Equal(new[] { "о" }, h.View(0).GetProperty("right").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(0, h.View(0).GetProperty("errors").GetInt32());
    }

    [Fact]
    public void The_same_letter_twice_is_refused_and_changes_nothing()
    {
        var h = Table("Оля");
        Guess(h, 0, "о");
        Guess(h, 0, "б");
        var before = Views.Text(h.View(0));

        Assert.Equal("Уже було", Guess(h, 0, "о").Message);
        Assert.Equal("Уже було", Guess(h, 0, "б").Message);
        Assert.Equal(before, Views.Text(h.View(0)));
    }

    [Theory]
    [InlineData("a")]        // латиниця
    [InlineData("1")]
    [InlineData("ко")]
    [InlineData("")]
    [InlineData(" ")]
    public void Only_a_single_ukrainian_letter_counts_as_a_guess(string raw)
    {
        var h = Table("Оля");
        var r = Guess(h, 0, raw);

        Assert.False(r.Ok);
        Assert.Equal("Це не українська літера", r.Message);
        Assert.Equal("______", Mask(h));
        Assert.Equal(0, h.View(0).GetProperty("errors").GetInt32());
    }

    [Fact]
    public void A_capital_letter_is_the_same_letter()
    {
        var h = Table("Оля");
        Assert.True(Guess(h, 0, "О").Ok);
        Assert.Equal("_о_о__", Mask(h));
    }

    [Fact]
    public void I_and_yi_are_letters_of_their_own()
    {
        // «світлиця»: «і» на третій позиції, «и» на шостій. Одна за одну не рахується — spec про це прямо каже.
        var h = Table(fx.Pair, 42, "Оля");
        Assert.True(Guess(h, 0, "и").Ok);
        Assert.Equal("_____и__", Mask(h));

        Assert.True(Guess(h, 0, "і").Ok);
        Assert.Equal("__і__и__", Mask(h));
        Assert.Equal(0, h.View(0).GetProperty("errors").GetInt32());

        Assert.True(Guess(h, 0, "ї").Ok);                   // «ї» — теж окрема літера, тут її нема
        Assert.Equal("__і__и__", Mask(h));
        Assert.Equal(1, h.View(0).GetProperty("errors").GetInt32());
    }

    // ---------------------------------------------------------------- шибениця

    [Fact]
    public void Eight_misses_lose_the_word_and_show_it_to_everyone()
    {
        var h = Table("Оля");
        for (var i = 0; i < Misses.Length - 1; i++) Assert.True(Guess(h, 0, Misses[i]).Ok);

        Assert.Equal(Hangman.MaxErrors - 1, h.View(0).GetProperty("errors").GetInt32());
        Assert.Equal("play", Phase(h));
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("revealed").ValueKind);

        var last = Guess(h, 0, Misses[^1]);
        Assert.True(last.Ok);
        Assert.Equal("", last.Message);                     // погана новина без зеленого тоста «усе гаразд»
        Assert.Equal(Hangman.MaxErrors, h.View(0).GetProperty("errors").GetInt32());
        Assert.Equal("between", Phase(h));
        Assert.Equal(HangmanWords.Word, h.View(0).GetProperty("revealed").GetString());
        Assert.Equal(0, Score(h, 0));
    }

    [Fact]
    public void The_last_letter_ends_the_round_with_the_word_open()
    {
        var h = Table("Оля");
        foreach (var ch in new[] { "к", "о", "р", "в" })
        {
            Assert.True(Guess(h, 0, ch).Ok);
            Assert.Equal("play", Phase(h));
        }
        Assert.True(Guess(h, 0, "а").Ok);

        Assert.Equal("between", Phase(h));
        Assert.Equal(HangmanWords.Word, Mask(h));
        Assert.Equal(HangmanWords.Word.Length, Score(h, 0));   // по очку за кожну відкриту позицію
    }

    // ---------------------------------------------------------------- ціле слово

    [Fact]
    public void Naming_the_whole_word_pays_three_plus_every_hidden_letter()
    {
        var h = Table("Оля");
        Guess(h, 0, "о");                                   // дві позиції вже відкриті
        Assert.True(Word(h, 0, HangmanWords.Word).Ok);

        Assert.Equal("between", Phase(h));
        Assert.Equal(2 + Hangman.WordBonus + 4, Score(h, 0));
    }

    [Fact]
    public void A_wrong_word_puts_the_player_out_of_this_round_and_costs_a_point()
    {
        var h = Table("Оля", "Петро");
        Guess(h, 0, "о");                                   // два очки, щоб було що втрачати
        var wrong = Word(h, 0, "будинок");
        Assert.True(wrong.Ok);
        Assert.Equal("", wrong.Message);                    // «не вгадав» зеленим тостом не показуємо

        Assert.Equal(new[] { 0 }, h.View(0).GetProperty("out").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        Assert.Equal(1, Score(h, 0));
        Assert.Equal("play", Phase(h));                     // Петро ще грає — раунд триває
        // Промах словом коштує очка і місця в раунді, але шибениці не додає: спільна кара — лише за літеру.
        Assert.Equal(0, h.View(0).GetProperty("errors").GetInt32());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("я")]        // одна літера — це вже guess, а не слово
    [InlineData("korova")]   // латиниця
    [InlineData("кор'ова")]  // апостроф: у словнику таких слів нема
    public void Only_a_real_word_counts_as_a_whole_word_call(string raw)
    {
        var h = Table("Оля");
        Guess(h, 0, "о");
        var before = Views.Text(h.View(0));

        var r = Word(h, 0, raw);
        Assert.False(r.Ok);
        Assert.Equal("Це не схоже на слово", r.Message);
        Assert.Equal(before, Views.Text(h.View(0)));
    }

    [Fact]
    public void Spaces_and_capitals_do_not_spoil_a_whole_word()
    {
        var h = Table("Оля");
        Assert.True(Word(h, 0, "  КОРОВА  ").Ok);

        Assert.Equal("between", Phase(h));
        Assert.Equal(Hangman.WordBonus + HangmanWords.Word.Length, Score(h, 0));
    }

    [Fact]
    public void The_one_who_is_out_cannot_guess_until_the_next_word()
    {
        var h = Table("Оля", "Петро");
        Word(h, 0, "будинок");

        var r = Guess(h, 0, "к");
        Assert.False(r.Ok);
        Assert.Equal("Це слово вже без тебе, чекай наступне", r.Message);
        Assert.Equal("______", Mask(h));

        Word(h, 1, "хатина");                               // Петро теж не вгадав — слово втекло
        NextWord(h);
        Assert.Empty(h.View(0).GetProperty("out").EnumerateArray());
        Assert.True(Guess(h, 0, "к").Ok);                   // нове слово — і він знову в грі
    }

    [Fact]
    public void Points_never_go_below_zero()
    {
        var h = Table("Оля", "Петро");
        Assert.True(Word(h, 0, "будинок").Ok);
        Assert.Equal(0, Score(h, 0));
    }

    [Fact]
    public void When_everyone_is_out_the_word_is_lost()
    {
        var h = Table("Оля", "Петро");
        Word(h, 0, "будинок");
        Word(h, 1, "хатина");

        Assert.Equal("between", Phase(h));
        Assert.Equal(HangmanWords.Word, h.View(0).GetProperty("revealed").GetString());
    }

    // ---------------------------------------------------------------- пауза і раунди

    [Fact]
    public void The_pause_between_words_lasts_four_seconds()
    {
        var h = Table("Оля");
        Word(h, 0, HangmanWords.Word);

        Assert.Equal(Hangman.PauseMs / 1000, h.View(0).GetProperty("nextIn").GetInt32());
        h.Tick(PauseTicks - 1);
        Assert.Equal("between", Phase(h));
        Assert.Equal(1, h.View(0).GetProperty("round").GetInt32());

        h.Tick(1);
        Assert.Equal("play", Phase(h));
        Assert.Equal(2, h.View(0).GetProperty("round").GetInt32());
        Assert.Equal("______", Mask(h));
        Assert.Equal(0, h.View(0).GetProperty("errors").GetInt32());
        Assert.Equal(0, h.View(0).GetProperty("nextIn").GetInt32());
    }

    [Fact]
    public void Five_words_end_the_party_with_scores_a_journal_line_and_a_table_row()
    {
        var h = Table("Оля", "Петро");
        for (var round = 1; round <= Hangman.Rounds; round++)
        {
            Assert.Equal(round, h.View(0).GetProperty("round").GetInt32());
            Word(h, 0, HangmanWords.Word);
            NextWord(h);
        }

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("done", Phase(h));
        var expected = Hangman.Rounds * (Hangman.WordBonus + HangmanWords.Word.Length);
        Assert.Equal(expected, Score(h, 0));
        Assert.Equal([0], h.Room.Result!.Winners);

        var finished = Assert.Single(h.Finished);
        Assert.Equal((long)expected, finished.Result.Scores![0]);
        Assert.Contains(h.Outbox.OfType<Journal>(), j => j.Text.StartsWith("Віселиця:") && j.Text.Contains("попереду Оля"));

        // таблиця «hangman» — за очками, по рядку на кожного, хто сидів
        Assert.Equal(2, h.Scores.Count);
        Assert.Equal(expected, h.Scores.Single(s => s.Nick == "Оля").Score);
        Assert.Equal(ScoreOrder.HigherIsBetter, h.Scores[0].Order);

        var result = h.View(0).GetProperty("result");
        Assert.Equal(new[] { 0 }, result.GetProperty("winners").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        Assert.Equal(expected, result.GetProperty("scores")[0].GetInt32());
    }

    [Fact]
    public void A_party_where_nobody_took_a_word_is_a_draw()
    {
        var h = Table("Оля", "Петро");
        for (var round = 1; round <= Hangman.Rounds; round++)
        {
            foreach (var miss in Misses) Guess(h, 0, miss);
            NextWord(h);
        }

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(h.Room.Result!.Draw);
        Assert.Empty(h.Room.Result.Winners);
        Assert.Contains("жодного слова", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void After_the_party_moves_are_refused()
    {
        var h = Table("Оля");
        for (var round = 1; round <= Hangman.Rounds; round++)
        {
            Word(h, 0, HangmanWords.Word);
            NextWord(h);
        }
        Assert.Equal("Партію зіграно, тисни «Ще раз»", Guess(h, 0, "к").Message);
    }

    [Fact]
    public void During_the_pause_nobody_guesses()
    {
        var h = Table("Оля");
        Word(h, 0, HangmanWords.Word);

        var r = Guess(h, 0, "к");
        Assert.False(r.Ok);
        Assert.Equal("Пауза. Зараз буде нове слово", r.Message);
    }

    // ---------------------------------------------------------------- ліміт, вид, тик

    [Fact]
    public void One_action_per_second_per_seat()
    {
        var h = Table("Оля", "Петро");
        h.Clock.Advance(1);
        Assert.True(h.Act(0, "guess", new { letter = "о" }).Ok);

        var fast = h.Act(0, "guess", new { letter = "к" });
        Assert.False(fast.Ok);
        Assert.Equal("Не так швидко", fast.Message);
        Assert.Equal("_о_о__", Mask(h));

        // сусіда чужий ліміт не стосується
        Assert.True(h.Act(1, "guess", new { letter = "к" }).Ok);

        h.Clock.Advance(1);
        Assert.True(h.Act(0, "guess", new { letter = "р" }).Ok);
    }

    [Fact]
    public void The_view_never_carries_the_word_while_the_round_runs()
    {
        var h = Table("Оля", "Петро");
        Guess(h, 0, "о");
        Guess(h, 1, "к");

        foreach (var seat in new int?[] { 0, 1, null })
        {
            var v = h.View(seat);
            Assert.All(Strings(v), s => Assert.DoesNotContain(HangmanWords.Word, s));
            Assert.Equal(JsonValueKind.Null, v.GetProperty("revealed").ValueKind);
        }

        Word(h, 0, HangmanWords.Word);
        Assert.Equal(HangmanWords.Word, h.View(null).GetProperty("revealed").GetString());
    }

    /// <summary>
    /// Усі рядки виду. Шукати слово треба саме в них: Views.Text екранує кирилицю в \uXXXX, і пошук
    /// підрядком по такому JSON мовчки проходив би завжди.
    /// </summary>
    static IEnumerable<string> Strings(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => new[] { e.GetString() ?? "" },
        JsonValueKind.Object => e.EnumerateObject().SelectMany(p => Strings(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().SelectMany(Strings),
        _ => Array.Empty<string>(),
    };

    [Fact]
    public void Everyone_at_the_table_sees_exactly_the_same_thing()
    {
        var h = Table("Оля", "Петро");
        Guess(h, 0, "о");

        var mine = Views.Text(h.View(0));
        Assert.Equal(mine, Views.Text(h.View(1)));
        Assert.Equal(mine, Views.Text(h.View(null)));       // прихованого тут нема, глядач бачить усе
    }

    [Fact]
    public void A_quiet_room_does_not_send_views_on_every_tick()
    {
        var h = Table("Оля");
        h.Tick(1);                                          // перший тик віддає те, що зробив Start
        var was = h.Outbox.OfType<RoomViews>().Count();

        h.Tick(20);
        Assert.Equal(was, h.Outbox.OfType<RoomViews>().Count());

        Guess(h, 0, "о");
        h.Tick(1);
        Assert.Equal(was + 1, h.Outbox.OfType<RoomViews>().Count());
    }

    // ---------------------------------------------------------------- стіл

    [Fact]
    public void One_player_plays_alone()
    {
        var h = new RoomHarness("hangman", seed: 42, services: RoomHarness.WithService(fx.One));
        h.Join("Оля");
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);      // ByHost: господар тисне «Почати» й сам

        Assert.True(h.Start().Ok);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.True(Guess(h, 0, "к").Ok);
    }

    [Fact]
    public void Somebody_leaving_does_not_break_the_party()
    {
        var h = Table("Оля", "Петро", "Ганна");
        Guess(h, 1, "о");
        h.Leave("Петро");

        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Contains(1, h.View(0).GetProperty("out").EnumerateArray().Select(x => x.GetInt32()));
        Assert.True(Guess(h, 0, "к").Ok);
    }

    [Fact]
    public void When_the_last_guesser_leaves_the_word_is_lost_on_the_next_tick()
    {
        var h = Table("Оля", "Петро");
        Word(h, 0, "будинок");                              // Оля вибула сама
        h.Leave("Петро");

        h.Tick(1);
        Assert.Equal("between", Phase(h));
        Assert.Equal(HangmanWords.Word, h.View(0).GetProperty("revealed").GetString());
    }

    [Fact]
    public void Rematch_starts_from_a_clean_score()
    {
        var h = Table("Оля", "Петро");
        for (var round = 1; round <= Hangman.Rounds; round++)
        {
            Word(h, 0, HangmanWords.Word);
            NextWord(h);
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);

        Assert.True(h.Rematch("Оля").Ok);
        Assert.Equal("Оля", h.Room.Seats[1]);               // місця обернулись
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(1, h.View(0).GetProperty("round").GetInt32());
        Assert.Equal("______", Mask(h));
        Assert.Equal(0, Score(h, 0));
        Assert.Equal(0, Score(h, 1));
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("result").ValueKind);
    }

    [Fact]
    public void There_is_no_table_without_a_dictionary()
    {
        var without = new RoomHarness("hangman", services: RoomHarness.WithService(fx.None));
        var reply = without.Join("Оля");
        Assert.False(reply.Ok);
        Assert.Equal("Нема словника, віселиця відпочиває", reply.Message);

        // і так само, коли сервісу Words у сервера взагалі нема
        var bare = new RoomHarness("hangman");
        Assert.Equal("Нема словника, віселиця відпочиває", bare.Join("Оля").Message);
    }

    [Fact]
    public void Unknown_actions_are_refused()
    {
        var h = Table("Оля");
        Assert.Equal("Тут так не ходять", h.Act(0, "hang", new { letter = "к" }).Message);
    }

    [Fact]
    public void A_payload_can_be_a_bare_string_too()
    {
        var h = Table("Оля");
        Assert.True(h.Act(0, "guess", "о").Ok);
        Assert.Equal("_о_о__", Mask(h));
        h.Clock.Advance(1);
        Assert.True(h.Act(0, "word", HangmanWords.Word).Ok);
    }

    [Fact]
    public void Hangman_is_in_the_catalog_as_a_party_game_started_by_the_host()
    {
        var game = Assert.Single(new Registry().Catalog, g => g.Id == "hangman");
        Assert.Equal("party", game.Group);
        Assert.Equal("byHost", game.Start);
        Assert.Equal(1, game.MinPlayers);
        Assert.Equal(6, game.MaxPlayers);
        Assert.False(game.Rated);                            // ставок і Ело тут нема
        Assert.False(game.Hidden);
        Assert.True(game.HasCss);
        Assert.Equal("hangman", game.Module);
    }

    // ---------------------------------------------------------------- детермінізм і швидкість

    [Fact]
    public void The_same_seed_gives_the_same_word_and_different_seeds_do_not()
    {
        // Сім літер — це щонайбільше сім промахів, тобто раунд гарантовано триває далі, хоч би яке
        // слово випало: порівнюємо повний вид після кожного ходу, а не самі лише риски.
        static string Play(Words words, int seed)
        {
            var h = Table(words, seed, "Оля");
            var steps = new List<string>();
            foreach (var ch in "абвгдеж")
            {
                Guess(h, 0, ch.ToString());
                steps.Add(Views.Text(h.View(0)));
            }
            return string.Join("|", steps);
        }

        Assert.Equal(Play(fx.Lots, 7), Play(fx.Lots, 7));
        var seeds = Enumerable.Range(1, 10).Select(s => Play(fx.Lots, s)).Distinct(StringComparer.Ordinal).Count();
        Assert.True(seeds > 1, "десять різних сідів дали те саме слово — це вже не випадковість");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void A_thousand_ticks_of_a_full_table_are_instant()
    {
        var h = Table(fx.Lots, 5, "Оля", "Петро", "Ганна", "Іван", "Марія", "Богдан");
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            if (i % 40 == 0) Guess(h, i / 40 % 6, Misses[i / 40 % Misses.Length]);
            h.Tick(1);
        }
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"1000 тиків зайняли {sw.Elapsed}");
    }
}
