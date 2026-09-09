using System.Diagnostics;
using Hlechyky;
using Hlechyky.Games;
using Hlechyky.Games.Economy;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Словники читаються з файлів один раз на всі тести класу: 38 тисяч рядків — не те, що варто
/// перечитувати перед кожним фактом.
///
/// Words будуємо НЕ на бойовому data/words, а на тимчасовій копії трьох малих списків. Інакше в
/// того, хто прогнав setup.ps1 (а CONTRIBUTING велить його прогнати), у data/words лежав би
/// uk-all.txt — і тести або червоніли б на FullLoaded, або мовчки будували стомегабайтну базу
/// всередині репозиторію просто тому, що хтось запустив dotnet test.
/// </summary>
public sealed class WordsFixture : IDisposable
{
    /// <summary>Бойовий каталог — для тестів, які читають самі файли (кодування, сортування, дублікати).</summary>
    public string Dir { get; } = Paths.Resolve("data/words");

    /// <summary>Копія лише малих списків: uk-all.* сюди не потрапляє ніколи.</summary>
    public string SmallOnlyDir { get; }

    public Words Words { get; }
    public string[] Five { get; }
    public string[] Guess { get; }
    public string[] Hangman { get; }

    public WordsFixture()
    {
        SmallOnlyDir = Path.Combine(Path.GetTempPath(), "hlechyky-words-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(SmallOnlyDir);
        foreach (var name in new[] { "uk-5.txt", "uk-guess.txt", "uk-hangman.txt" })
            File.Copy(Path.Combine(Dir, name), Path.Combine(SmallOnlyDir, name));

        Words = new Words(SmallOnlyDir);
        Five = File.ReadAllLines(Path.Combine(Dir, "uk-5.txt"));
        Guess = File.ReadAllLines(Path.Combine(Dir, "uk-guess.txt"));
        Hangman = File.ReadAllLines(Path.Combine(Dir, "uk-hangman.txt"));
    }

    public void Dispose()
    {
        Words.Dispose();
        try { Directory.Delete(SmallOnlyDir, recursive: true); } catch (IOException) { /* хай лежить у temp */ }
    }
}

public class WordsTests(WordsFixture fx) : IClassFixture<WordsFixture>
{
    const string Alphabet = "абвгґдеєжзиіїйклмнопрстуфхцчшщьюя";

    static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hlechyky-words-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

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
            var path = Path.Combine(fx.Dir, name);
            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length > 2 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, $"{name}: BOM");
            Assert.DoesNotContain((byte)'\r', bytes);
            Assert.DoesNotContain((byte)' ', bytes);

            // порядок — не дрібниця: саме він робить diff між версіями словника читабельним
            var lines = File.ReadAllLines(path);
            var sorted = lines.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var first = lines.Zip(sorted).FirstOrDefault(p => p.First != p.Second);
            Assert.True(lines.SequenceEqual(sorted), $"{name}: рядки не за порядком — «{first.First}» стоїть там, де мало б «{first.Second}»");
        }
    }

    [Fact]
    public void Words_curated_out_never_come_back_as_answers()
    {
        // curation-drop.txt — це те, що людина свідомо викинула (бренди, наркотики, скорочення).
        // Відповідь Глек-слова і слово Віселиці звідти взятись не має, хоч спробою воно бути може.
        var drop = File.ReadAllLines(Path.Combine(fx.Dir, "curation-drop.txt"))
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(drop);
        var inAnswers = fx.Five.Where(drop.Contains).ToArray();
        var inHangman = fx.Hangman.Where(drop.Contains).ToArray();
        Assert.True(inAnswers.Length == 0, "у відповідях лишились викинуті слова: " + string.Join(", ", inAnswers));
        Assert.True(inHangman.Length == 0, "у віселиці лишились викинуті слова: " + string.Join(", ", inHangman));
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
    public void Huge_junk_guess_is_rejected_without_work()
    {
        // з дроту приходить до 8 КБ; такі спроби мають відпадати по довжині, без копії рядка
        var junk = new string('я', 8192);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 10_000; i++) Assert.False(fx.Words.IsValid5(junk));
        Assert.True(sw.ElapsedMilliseconds < 100, $"10 000 сміттєвих спроб зайняли {sw.ElapsedMilliseconds} мс");
        Assert.False(fx.Words.IsValid5("   книга   ще слова   "));
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
#pragma warning disable CS0618 // Daily5(seed) лишений для сумісності — перевіряємо, що він принаймні детермінований
        var seed = Days.Seed("wordle", "2026-09-10");
        Assert.Equal(fx.Words.Daily5(seed), fx.Words.Daily5(seed));
#pragma warning restore CS0618
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
#pragma warning disable CS0618
        Assert.Contains(fx.Words.Daily5(Days.Seed("wordle", "2026-12-31")), answers);
#pragma warning restore CS0618
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

    [Theory]
    [InlineData("")]
    [InlineData("не-дата")]
    [InlineData("2026-9-1")]        // без нулів формат не той
    [InlineData("2026-02-30")]      // такого дня нема
    [InlineData(null)]
    public void Daily_word_for_a_broken_day_is_empty_not_an_exception(string? day)
    {
        // день може приїхати зі збереженого стану Persistent-гри, тобто з диска
        Assert.Equal("", fx.Words.Daily5ForDay(day));
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
        // фікстура зібрана на копії лише малих списків, тому це чесний інваріант, а не збіг обставин
        Assert.False(fx.Words.FullLoaded);
        Assert.Equal(0, fx.Words.Stats.Full);
        Assert.True(fx.Words.IsWord("книга"));
        Assert.True(fx.Words.IsWord(fx.Hangman[0]));
        Assert.False(fx.Words.IsWord("жжжжжж"));
        Assert.False(fx.Words.IsWord("м'ята"));
        Assert.False(fx.Words.IsWord(null));
    }

    [Fact]
    public async Task Big_dictionary_is_built_from_text_and_answers_lookups()
    {
        var dir = TempDir();
        try
        {
            // маленька копія великого словника: тими самими шляхами, тільки не 3.4 млн слів
            File.Copy(Path.Combine(fx.Dir, "uk-5.txt"), Path.Combine(dir, "uk-hangman.txt"));
            File.WriteAllLines(Path.Combine(dir, "uk-all.txt"), ["ковбаса", "ковбасою", "ЗБРОЯ", "м'ята", "у"]);

            using (var words = new Words(dir))
            {
                await words.FullReady;          // шов замість сну: фонова збірка сама каже, коли скінчила

                Assert.True(words.FullLoaded);
                Assert.Equal(3, words.Stats.Full);             // «м'ята» відкинуто, «у» закоротке
                Assert.True(words.IsWord("ковбасою"));
                Assert.True(words.IsWord("Ковбаса"));
                Assert.True(words.IsWord("зброя"));            // регістр із файлу нормалізовано при збиранні
                Assert.False(words.IsWord("ковбасина"));
                Assert.False(words.IsWord("м'ята"));

                // Stats читають і з лога при старті, і потенційно з API — вона не має ходити в базу
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < 10_000; i++) Assert.Equal(3, words.Stats.Full);
                Assert.True(sw.ElapsedMilliseconds < 100, $"10 000 читань Stats зайняли {sw.ElapsedMilliseconds} мс — схоже, там знову COUNT(*)");
            }

            // після Dispose база закрита: файл видаляється, а не висить зайнятим до кінця процесу
            File.Delete(Path.Combine(dir, "uk-all.db"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Existing_database_is_opened_on_start_and_closed_on_dispose()
    {
        var dir = TempDir();
        try
        {
            File.Copy(Path.Combine(fx.Dir, "uk-5.txt"), Path.Combine(dir, "uk-hangman.txt"));
            File.WriteAllLines(Path.Combine(dir, "uk-all.txt"), ["ковбаса", "ковбасою"]);
            using (var first = new Words(dir)) await first.FullReady;

            // другий Words бере вже готовий uk-all.db — без збирання і без uk-all.txt у пам'яті
            using (var second = new Words(dir))
            {
                await second.FullReady;
                Assert.True(second.FullLoaded);
                Assert.Equal(2, second.Stats.Full);
                Assert.True(second.IsWord("ковбасою"));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Broken_database_leaves_the_small_lists_working()
    {
        var dir = TempDir();
        try
        {
            File.Copy(Path.Combine(fx.Dir, "uk-5.txt"), Path.Combine(dir, "uk-5.txt"));
            File.Copy(Path.Combine(fx.Dir, "uk-hangman.txt"), Path.Combine(dir, "uk-hangman.txt"));
            File.WriteAllText(Path.Combine(dir, "uk-all.db"), "це не база даних, це просто текст");

            using var words = new Words(dir);
            await words.FullReady;

            Assert.False(words.FullLoaded);
            Assert.Equal(0, words.Stats.Full);
            Assert.True(words.Loaded);
            Assert.True(words.IsWord("книга"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------- нема словника

    [Fact]
    public async Task Missing_dictionary_does_not_throw()
    {
        var dir = TempDir();
        try
        {
            using var words = new Words(dir);
            await words.FullReady;

            Assert.False(words.Loaded);
            Assert.False(words.FullLoaded);
            Assert.Equal(new WordsStats(0, 0, 0, 0), words.Stats);
            Assert.False(words.IsValid5("книга"));
            Assert.False(words.IsWord("книга"));
#pragma warning disable CS0618
            Assert.Equal("", words.Daily5(12345));
#pragma warning restore CS0618
            Assert.Equal("", words.Daily5ForDay("2026-09-10"));
            Assert.Equal("", words.Daily5ForDay("не-дата"));
            Assert.Equal("", words.RandomHangman(new Random(1)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
