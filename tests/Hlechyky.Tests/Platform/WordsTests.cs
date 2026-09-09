using System.Diagnostics;
using Hlechyky;
using Hlechyky.Games;
using Hlechyky.Games.Economy;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Словники читаються з файлів один раз на всі тести класу: 38 тисяч рядків — не те, що варто
/// перечитувати перед кожним фактом.
/// </summary>
public sealed class WordsFixture
{
    public string Dir { get; } = Paths.Resolve("data/words");
    public Words Words { get; }
    public string[] Five { get; }
    public string[] Guess { get; }
    public string[] Hangman { get; }

    public WordsFixture()
    {
        Words = new Words(Dir);
        Five = File.ReadAllLines(Path.Combine(Dir, "uk-5.txt"));
        Guess = File.ReadAllLines(Path.Combine(Dir, "uk-guess.txt"));
        Hangman = File.ReadAllLines(Path.Combine(Dir, "uk-hangman.txt"));
    }
}

public class WordsTests(WordsFixture fx) : IClassFixture<WordsFixture>
{
    const string Alphabet = "абвгґдеєжзиіїйклмнопрстуфхцчшщьюя";

    // ---------------------------------------------------------------------------- списки на диску

    [Fact]
    public void Lists_are_loaded()
    {
        Assert.True(fx.Words.Loaded);
        var s = fx.Words.Stats;
        Assert.InRange(s.Small5, 1500, 3000);
        Assert.True(s.Guess5 >= 20_000, $"допустимих спроб має бути ≥ 20000, а їх {s.Guess5}");
        Assert.True(s.Hangman >= 3000, $"слів для віселиці має бути ≥ 3000, а їх {s.Hangman}");
    }

    [Fact]
    public void Answers_are_exactly_five_ukrainian_letters()
    {
        foreach (var w in fx.Five)
        {
            Assert.Equal(5, w.Length);
            Assert.All(w, ch => Assert.Contains(ch, Alphabet));
        }
    }

    [Fact]
    public void Guess_words_are_exactly_five_ukrainian_letters()
    {
        foreach (var w in fx.Guess)
        {
            Assert.Equal(5, w.Length);
            Assert.All(w, ch => Assert.Contains(ch, Alphabet));
        }
    }

    [Fact]
    public void Hangman_words_are_five_to_twelve_ukrainian_letters()
    {
        foreach (var w in fx.Hangman)
        {
            Assert.InRange(w.Length, 5, 12);
            Assert.All(w, ch => Assert.Contains(ch, Alphabet));
        }
    }

    [Fact]
    public void Lists_have_no_duplicates()
    {
        Assert.Equal(fx.Five.Length, fx.Five.Distinct().Count());
        Assert.Equal(fx.Guess.Length, fx.Guess.Distinct().Count());
        Assert.Equal(fx.Hangman.Length, fx.Hangman.Distinct().Count());
    }

    [Fact]
    public void Answers_are_a_subset_of_guesses()
    {
        var guess = fx.Guess.ToHashSet(StringComparer.Ordinal);
        var missing = fx.Five.Where(w => !guess.Contains(w)).ToArray();
        Assert.Empty(missing);
    }

    [Fact]
    public void Lists_are_sorted_lf_files_without_bom_or_spaces()
    {
        foreach (var name in new[] { "uk-5.txt", "uk-guess.txt", "uk-hangman.txt" })
        {
            var bytes = File.ReadAllBytes(Path.Combine(fx.Dir, name));
            Assert.False(bytes.Length > 2 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, $"{name}: BOM");
            Assert.DoesNotContain((byte)'\r', bytes);
            Assert.DoesNotContain((byte)' ', bytes);
        }
    }

    // ---------------------------------------------------------------------------- IsValid5

    [Fact]
    public void Answer_word_is_a_valid_guess()
    {
        Assert.True(fx.Words.IsValid5(fx.Five[0]));
        Assert.True(fx.Words.IsValid5("книга"));
        Assert.True(fx.Words.IsValid5("зброя"));
    }

    [Theory]
    [InlineData("м'ята")]       // прямий апостроф
    [InlineData("м’ята")]       // типографський
    [InlineData("мʼята")]       // модифікатор
    public void Words_with_apostrophe_are_not_accepted(string word)
    {
        Assert.False(fx.Words.IsValid5(word));
        Assert.Null(Words.Normalize(word));
    }

    [Theory]
    [InlineData("книгa")]       // остання «a» — латинська
    [InlineData("ёлка!")]
    [InlineData("хата-")]
    [InlineData("книг1")]
    public void Non_ukrainian_letters_are_not_accepted(string word)
    {
        Assert.False(fx.Words.IsValid5(word));
        Assert.Null(Words.Normalize(word));
    }

    [Fact]
    public void Case_is_normalized_including_ukrainian_specific_letters()
    {
        Assert.Equal("їжа", Words.Normalize("ЇЖА"));
        Assert.Equal("ґанок", Words.Normalize("Ґанок"));
        Assert.Equal("єресь", Words.Normalize(" ЄРЕСЬ "));
        Assert.True(fx.Words.IsValid5("ҐАНОК"));
        Assert.True(fx.Words.IsValid5(" Книга "));
    }

    [Fact]
    public void Unknown_and_wrong_length_words_are_rejected()
    {
        Assert.False(fx.Words.IsValid5("жжжжж"));
        Assert.False(fx.Words.IsValid5("сон"));
        Assert.False(fx.Words.IsValid5("книгарня"));
        Assert.False(fx.Words.IsValid5(""));
        Assert.False(fx.Words.IsValid5(null));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Ten_thousand_lookups_take_less_than_a_hundred_milliseconds()
    {
        var probes = fx.Five.Take(100).Concat(Enumerable.Repeat("жжжжж", 100)).ToArray();
        var sw = Stopwatch.StartNew();
        var hits = 0;
        for (var i = 0; i < 10_000; i++)
            if (fx.Words.IsValid5(probes[i % probes.Length])) hits++;
        sw.Stop();
        Assert.Equal(5000, hits);
        Assert.True(sw.ElapsedMilliseconds < 100, $"10 000 перевірок зайняли {sw.ElapsedMilliseconds} мс");
    }

    // ---------------------------------------------------------------------------- слово дня

    [Fact]
    public void Daily_word_is_deterministic()
    {
        var seed = Days.Seed("wordle", "2026-09-10");
        Assert.Equal(fx.Words.Daily5(seed), fx.Words.Daily5(seed));
        Assert.Equal(fx.Words.Daily5ForDay("2026-09-10"), fx.Words.Daily5ForDay("2026-09-10"));
        Assert.NotEqual(fx.Words.Daily5ForDay("2026-09-10"), fx.Words.Daily5ForDay("2026-09-11"));
    }

    [Fact]
    public void Daily_word_is_always_a_valid_answer()
    {
        var answers = fx.Five.ToHashSet(StringComparer.Ordinal);
        var day = new DateOnly(2026, 9, 10);
        for (var i = 0; i < 365; i++)
            Assert.Contains(fx.Words.Daily5ForDay(day.AddDays(i).ToString("yyyy-MM-dd")), answers);
        Assert.Contains(fx.Words.Daily5(Days.Seed("wordle", "2026-12-31")), answers);
    }

    [Fact]
    public void Daily_word_does_not_repeat_within_a_year()
    {
        var day = new DateOnly(2026, 9, 10);
        var seen = new List<string>(365);
        for (var i = 0; i < 365; i++)
            seen.Add(fx.Words.Daily5ForDay(day.AddDays(i).ToString("yyyy-MM-dd")));
        Assert.Equal(365, seen.Distinct().Count());
    }

    [Fact]
    public void Daily_word_survives_days_before_the_launch_day()
    {
        // Days.Number для дня до 10 вересня 2026 від'ємний — слово все одно має знайтись
        var w = fx.Words.Daily5ForDay("2026-01-01");
        Assert.Equal(5, w.Length);
        Assert.Contains(w, fx.Five);
    }

    // ---------------------------------------------------------------------------- віселиця

    [Fact]
    public void Hangman_word_respects_length_limits()
    {
        var rng = new Random(42);
        for (var i = 0; i < 500; i++)
        {
            var w = fx.Words.RandomHangman(rng, 5, 12);
            Assert.InRange(w.Length, 5, 12);
        }
        for (var i = 0; i < 200; i++)
        {
            var w = fx.Words.RandomHangman(rng, 6, 7);
            Assert.InRange(w.Length, 6, 7);
        }
    }

    [Fact]
    public void Hangman_word_is_deterministic_for_a_given_seed()
    {
        Assert.Equal(fx.Words.RandomHangman(new Random(7)), fx.Words.RandomHangman(new Random(7)));
    }

    [Fact]
    public void Hangman_returns_empty_when_no_word_fits()
    {
        Assert.Equal("", fx.Words.RandomHangman(new Random(1), 20, 25));
    }

    // ---------------------------------------------------------------------------- Ерудит

    [Fact]
    public void Without_the_big_dictionary_only_small_lists_are_known()
    {
        Assert.False(fx.Words.FullLoaded);
        Assert.True(fx.Words.IsWord("книга"));
        Assert.True(fx.Words.IsWord(fx.Hangman[0]));
        Assert.False(fx.Words.IsWord("жжжжжж"));
        Assert.False(fx.Words.IsWord("м'ята"));
        Assert.False(fx.Words.IsWord(null));
    }

    [Fact]
    public void Big_dictionary_is_built_from_text_and_answers_lookups()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hlechyky-words-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // маленька копія великого словника: тими самими шляхами, тільки не 3.4 млн слів
            File.Copy(Path.Combine(fx.Dir, "uk-5.txt"), Path.Combine(dir, "uk-hangman.txt"));
            File.WriteAllLines(Path.Combine(dir, "uk-all.txt"), ["ковбаса", "ковбасою", "ЗБРОЯ", "м'ята", "у"]);

            var words = new Words(dir);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!words.FullLoaded && DateTime.UtcNow < deadline) Thread.Sleep(20);

            Assert.True(words.FullLoaded, "великий словник не зібрався за 30 с");
            Assert.Equal(3, words.Stats.Full);             // «м'ята» відкинуто, «у» закоротке
            Assert.True(words.IsWord("ковбасою"));
            Assert.True(words.IsWord("Ковбаса"));
            Assert.True(words.IsWord("зброя"));            // регістр із файлу нормалізовано при збиранні
            Assert.False(words.IsWord("ковбасина"));
            Assert.False(words.IsWord("м'ята"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* хай лежить у temp */ }
        }
    }

    // ---------------------------------------------------------------------------- нема словника

    [Fact]
    public void Missing_dictionary_does_not_throw()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hlechyky-words-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var words = new Words(dir);

            Assert.False(words.Loaded);
            Assert.False(words.FullLoaded);
            Assert.Equal(new WordsStats(0, 0, 0, 0), words.Stats);
            Assert.False(words.IsValid5("книга"));
            Assert.False(words.IsWord("книга"));
            Assert.Equal("", words.Daily5(12345));
            Assert.Equal("", words.Daily5ForDay("2026-09-10"));
            Assert.Equal("", words.RandomHangman(new Random(1)));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* хай лежить у temp */ }
        }
    }
}
