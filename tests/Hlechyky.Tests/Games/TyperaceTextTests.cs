using System.Text.RegularExpressions;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Клавоперегони: банк текстів (data/typerace/texts.json), нормалізація, рівність знаків при друці, нарізка уривків
/// потрібної довжини й вибір тексту без повторів (spec §7).
/// </summary>
public class TyperaceTextTests
{
    static readonly TyperaceBank Real = TyperaceBank.Load(Paths.Resolve(TyperaceBank.FileName));

    // ------------------------------------------------------------------------------------ банк

    [Fact]
    public void Bank_has_at_least_210_records_of_three_kinds_with_unique_ids()
    {
        Assert.True(Real.Entries.Count >= 210, $"записів {Real.Entries.Count}");
        Assert.True(Real.Of(TyperaceBank.Classic).Count >= 100);
        Assert.True(Real.Of(TyperaceBank.Proverbs).Count >= 80);
        Assert.True(Real.Of(TyperaceBank.Twisters).Count >= 30);
        Assert.Equal(Real.Entries.Count, Real.Entries.Select(e => e.Id).Distinct().Count());
        Assert.True(Real.Of(TyperaceBank.Classic).Select(e => e.Author).Distinct().Count() >= 10);
        // жоден запис не загубився на завантаженні: у файлі стільки ж, скільки прийняв банк
        var raw = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Paths.Resolve(TyperaceBank.FileName)));
        Assert.Equal(raw.RootElement.GetProperty("texts").GetArrayLength(), Real.Entries.Count);
    }

    [Fact]
    public void Every_classic_passage_names_an_author_a_title_and_a_wikisource_page()
    {
        var raw = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Paths.Resolve(TyperaceBank.FileName)));
        foreach (var t in raw.RootElement.GetProperty("texts").EnumerateArray())
        {
            if (t.GetProperty("kind").GetString() != "classic") continue;
            var id = t.GetProperty("id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(t.GetProperty("author").GetString()), id);
            Assert.False(string.IsNullOrWhiteSpace(t.GetProperty("title").GetString()), id);
            Assert.False(string.IsNullOrWhiteSpace(t.GetProperty("page").GetString()), id);
            Assert.True(t.GetProperty("rev").GetInt64() > 0, id);
            Assert.Equal("PD", t.GetProperty("license").GetString());
            if (t.TryGetProperty("year", out var y) && y.ValueKind == System.Text.Json.JsonValueKind.Number)
                Assert.True(y.GetInt32() < 1946, id);
        }
        // автори — лише ті, хто помер понад 70 років тому (список зі spec §7.2)
        string[] allowed = ["Тарас Шевченко", "Іван Котляревський", "Григорій Квітка-Основ’яненко", "Євген Гребінка", "Марко Вовчок",
            "Леонід Глібов", "Степан Руданський", "Іван Нечуй-Левицький", "Панас Мирний", "Михайло Старицький", "Іван Карпенко-Карий",
            "Іван Франко", "Леся Українка", "Михайло Коцюбинський", "Василь Стефаник", "Ольга Кобилянська", "Борис Грінченко",
            "Архип Тесленко", "Степан Васильченко", "Олександр Олесь", "Микола Вороний", "Григорій Сковорода"];
        foreach (var e in Real.Of(TyperaceBank.Classic)) Assert.Contains(e.Author, allowed);
        foreach (var e in Real.Entries.Where(e => e.Kind != TyperaceBank.Classic)) Assert.Null(e.Author);
    }

    [Fact]
    public void Every_text_uses_only_the_typeable_alphabet()
    {
        foreach (var e in Real.Entries)
        {
            Assert.True(TyperaceText.IsTypeable(e.Text), e.Id);
            Assert.DoesNotMatch("[ёъыэѣўЁЪЫЭ]|[A-Za-z]|  ", e.Text);
            Assert.DoesNotContain(e.Text, char.IsSurrogate);
            // нормалізація ідемпотентна: банк уже лежить у друкованому вигляді
            Assert.Equal(e.Text, TyperaceText.Normalize(e.Text));
        }
    }

    [Fact]
    public void Classic_passages_are_480_to_800_chars_and_end_with_a_sentence()
    {
        foreach (var e in Real.Of(TyperaceBank.Classic))
        {
            Assert.InRange(e.Text.Length, 480, 800);
            Assert.Matches("[.!?][»)]?$", e.Text);
            Assert.True(char.IsUpper(e.Text[0]) || e.Text[0] is '—' or '«', e.Id);
        }
    }

    [Fact]
    public void Proverbs_and_twisters_sit_in_their_own_length_windows()
    {
        foreach (var e in Real.Of(TyperaceBank.Proverbs))
        {
            Assert.InRange(e.Text.Length, 25, 90);
            Assert.Matches("[.!?]$", e.Text);
        }
        foreach (var e in Real.Of(TyperaceBank.Twisters))
        {
            Assert.InRange(e.Text.Length, 30, 120);
            Assert.Matches("[.!?]$", e.Text);
        }
    }

    [Fact]
    public void Missing_or_broken_bank_file_yields_an_empty_bank_not_an_exception()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typerace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(TyperaceBank.Load(Path.Combine(dir, "нема.json")).Empty);
            var broken = Path.Combine(dir, "texts.json");
            File.WriteAllText(broken, "{ \"texts\": [ { \"id\": ");
            Assert.True(TyperaceBank.Load(broken).Empty);
            File.WriteAllText(broken, "[1, 2, 3]");
            Assert.True(TyperaceBank.Load(broken).Empty);
            // криві записи відкидаються мовчки, добрі лишаються
            File.WriteAllText(broken, """
                { "texts": [
                  { "id": "a", "kind": "proverbs", "text": "Під лежачий камінь і вода не тече." },
                  { "id": "a", "kind": "proverbs", "text": "Дубль того самого id — геть." },
                  { "id": "b", "kind": "казка", "text": "Невідомий вид." },
                  { "id": "c", "kind": "twisters", "text": "Latin letters are not typeable here." },
                  { "kind": "twisters", "text": "Без id." },
                  { "id": "d", "kind": "twisters", "text": "Ёлка з російською літерою." }
                ] }
                """);
            var bank = TyperaceBank.Load(broken);
            Assert.Equal(["a"], bank.Entries.Select(e => e.Id));
        }
        finally { Directory.Delete(dir, true); }

        // стіл без текстів не створюється — з текстом відмови
        var h = new RoomHarness("typerace", services: RoomHarness.WithService(TyperaceBank.From([])));
        var reply = h.Join("Оля");
        Assert.False(reply.Ok);
        Assert.Equal(Typerace.NoTexts, reply.Message);
    }

    // ------------------------------------------------------------------------------------ текст

    [Theory]
    [InlineData("М'ята", "М’ята")]
    [InlineData("мʼята і м‘ята, м`ята, м´ята", "м’ята і м’ята, м’ята, м’ята")]
    [InlineData("Він - не я", "Він — не я")]
    [InlineData("Він – не я", "Він — не я")]
    [InlineData("де-не-де", "де-не-де")]
    [InlineData("так,— каже", "так, — каже")]
    [InlineData("- Ой, мамо", "— Ой, мамо")]
    [InlineData("\"Садок\" і „Хата“", "«Садок» і «Хата»")]
    [InlineData("Ну…", "Ну...")]
    [InlineData("  Садок \t вишневий коло   хати  ", "Садок вишневий коло хати")]
    [InlineData("рядок \r\n\r\n  другий", "рядок\nдругий")]
    [InlineData("що́, сестри́це", "що, сестрице")]
    [InlineData("Ї й", "Ї й")]
    public void Normalize_unifies_apostrophes_dashes_quotes_ellipsis_and_whitespace(string raw, string expected) =>
        Assert.Equal(expected, TyperaceText.Normalize(raw));

    [Fact]
    public void Normalize_composes_letters_and_never_throws_on_nothing()
    {
        Assert.Equal("", TyperaceText.Normalize(null));
        Assert.Equal("", TyperaceText.Normalize("   \n  "));
        Assert.Equal("й", TyperaceText.Normalize("й"));          // розкладене «й» — одним знаком
        Assert.Equal("ї", TyperaceText.Normalize("ї"));
    }

    [Fact]
    public void Same_accepts_every_typeable_variant_and_respects_case()
    {
        Assert.True(TyperaceText.Same('’', '\''));
        Assert.True(TyperaceText.Same('’', 'ʼ'));
        Assert.True(TyperaceText.Same('—', '-'));
        Assert.True(TyperaceText.Same('—', '–'));
        Assert.True(TyperaceText.Same('-', '—'));
        Assert.True(TyperaceText.Same('«', '"'));
        Assert.True(TyperaceText.Same('»', '"'));
        Assert.True(TyperaceText.Same('\n', ' '));
        Assert.True(TyperaceText.Same('а', 'а'));
        Assert.False(TyperaceText.Same('С', 'с'));
        Assert.False(TyperaceText.Same('с', 'c'));      // латинська «c» — не наша
        Assert.False(TyperaceText.Same('’', '"'));
        Assert.False(TyperaceText.Same(' ', '-'));
        Assert.False(TyperaceText.Same('.', ','));
    }

    // ------------------------------------------------------------------------------------ нарізка

    const string Poem =
        "Садок вишневий коло хати, хрущі над вишнями гудуть. Плугатарі з плугами йдуть, співають ідучи дівчата. " +
        "А матері вечерять ждуть! Сім’я вечеря коло хати, вечірня зіронька встає? Дочка вечерять подає, а мати хоче научати. " +
        "Так соловейко не дає... Поклала мати коло хати маленьких діточок своїх. Сама заснула коло їх. Затихло все, " +
        "тілько дівчата та соловейко не затих. Т. Шевченко написав це 1847 р. у Орській фортеці, і вірш став піснею. " +
        "Дівчата співають його й досі. Хлопці підтягують басом. Діти слухають і засинають. Над селом сходить місяць. " +
        "Вишні стоять у білому цвіту. Пахне медом і теплою землею. Хтось грає на сопілці за городами. Ніч тиха.";

    [Theory]
    [InlineData(150)]
    [InlineData(300)]
    [InlineData(600)]
    public void Cut_stops_at_a_sentence_end_inside_the_window(int target)
    {
        var cut = TyperaceBank.Cut(Poem, target);
        Assert.True(cut.Length <= Poem.Length);
        Assert.StartsWith(cut, Poem);
        Assert.Matches("[.!?]$", cut);
        if (cut.Length < Poem.Length)
        {
            Assert.InRange(cut.Length, (int)(0.8 * target), (int)(1.35 * target));
            // наступний знак у джерелі — пробіл: різали між реченнями, а не посеред
            Assert.Equal(' ', Poem[cut.Length]);
        }
        // крапка після ініціала «Т.» і після «1847 р.»-цифри не ріже
        Assert.DoesNotMatch(@"\bТ\.$", cut);
    }

    [Fact]
    public void Cut_keeps_initials_and_numbers_inside_a_sentence()
    {
        const string s = "Це написав Т. Шевченко. Рік 1847. Далі.";
        Assert.Equal("Це написав Т. Шевченко.".Length, TyperaceBank.SentenceEnd(s, 0));
        const string n = "Було 5.5 градуса. Далі.";
        Assert.Equal("Було 5.5 градуса.".Length, TyperaceBank.SentenceEnd(n, 0));
        // одне-єдине довге речення довше за вікно — беремо його ціле
        var one = string.Join(", ", Enumerable.Repeat("хрущі над вишнями гудуть", 20)) + ".";
        Assert.Equal(one, TyperaceBank.Cut(one, 150));
        // «!..» і «?»» — теж кінці
        Assert.Equal("Ой!.. «Хто?»".Length, TyperaceBank.SentenceEnd("Ой!.. «Хто?» Далі.", 6));
    }

    [Fact]
    public void Real_classic_passages_cut_into_every_length()
    {
        foreach (var target in new[] { 150, 300, 600 })
        {
            var fit = 0;
            foreach (var e in Real.Of(TyperaceBank.Classic))
            {
                var cut = TyperaceBank.Cut(e.Text, target);
                Assert.StartsWith(cut, e.Text);
                Assert.True(TyperaceText.IsTypeable(cut), e.Id);
                Assert.Matches("[.!?][»)]?$", cut);
                if (TyperaceBank.Fits(cut.Length, target)) fit++;
            }
            // на кожну довжину годиться переважна більшість уривків
            Assert.True(fit >= Real.Of(TyperaceBank.Classic).Count * 8 / 10, $"{target}: {fit}");
            // а вибір класики ніколи не дає нечесно короткого чи задовгого заїзду
            var used = new HashSet<string>();
            var rng = new Random(target);
            for (var i = 0; i < 200; i++)
            {
                var pick = Real.Pick(target switch { 150 => "short", 300 => "medium", _ => "long" }, TyperaceBank.Classic, rng, used)!;
                Assert.True(TyperaceBank.Fits(pick.Text.Length, target), $"{pick.Ids[0]} → {target}: {pick.Text.Length}");
            }
        }
    }

    // ------------------------------------------------------------------------------------ вибір

    static TyperaceBank Small() => TyperaceTestBank.Create();

    [Fact]
    public void A_string_of_proverbs_lands_in_the_window_and_never_repeats_an_item()
    {
        foreach (var (length, target) in new[] { ("short", 150), ("medium", 300), ("long", 600) })
            for (var seed = 1; seed <= 30; seed++)
            {
                var used = new HashSet<string>();
                var pick = Real.Pick(length, TyperaceBank.Proverbs, new Random(seed), used)!;
                Assert.InRange(pick.Text.Length, target - 20 - 90, target + 40);
                Assert.True(pick.Text.Length >= target - 20 || pick.Ids.Count == Real.Of(TyperaceBank.Proverbs).Count);
                Assert.Equal(pick.Ids.Count, pick.Ids.Distinct().Count());
                Assert.Equal("proverbs", pick.Src.Kind);
                Assert.Null(pick.Src.Author);
                Assert.Contains($"низка з {pick.Ids.Count}", pick.Src.Title);
                Assert.True(TyperaceText.IsTypeable(pick.Text));
            }
        var tw = Real.Pick("medium", TyperaceBank.Twisters, new Random(3), new HashSet<string>())!;
        Assert.Contains("поспіль", tw.Src.Title);
    }

    [Fact]
    public void Pick_with_the_same_seed_gives_the_same_text_and_another_seed_differs()
    {
        var a = Real.Pick("medium", TyperaceBank.All, new Random(7), new HashSet<string>())!;
        var b = Real.Pick("medium", TyperaceBank.All, new Random(7), new HashSet<string>())!;
        Assert.Equal(a.Text, b.Text);
        var differs = Enumerable.Range(8, 10).Any(s => Real.Pick("medium", TyperaceBank.All, new Random(s), new HashSet<string>())!.Text != a.Text);
        Assert.True(differs);
    }

    [Fact]
    public void Pick_never_repeats_a_text_in_one_room_until_the_pool_is_exhausted()
    {
        var bank = Small();
        var classics = bank.Of(TyperaceBank.Classic).Count;
        var used = new HashSet<string>();
        var rng = new Random(5);
        var seen = new List<string>();
        for (var i = 0; i < classics; i++) seen.Add(bank.Pick("long", TyperaceBank.Classic, rng, used)!.Ids[0]);
        Assert.Equal(classics, seen.Distinct().Count());
        // пул вичерпано — пам'ять чиститься, і тексти йдуть знову
        var again = bank.Pick("long", TyperaceBank.Classic, rng, used)!;
        Assert.Contains(again.Ids[0], seen);
        Assert.Single(used);

        // низки теж не повторюють складників, поки нових вистачає
        var pu = new HashSet<string>();
        var first = bank.Pick("short", TyperaceBank.Proverbs, rng, pu)!;
        var second = bank.Pick("short", TyperaceBank.Proverbs, rng, pu)!;
        Assert.Empty(first.Ids.Intersect(second.Ids));
    }

    [Fact]
    public void Source_all_mixes_kinds_roughly_fifty_thirty_twenty()
    {
        var counts = new Dictionary<string, int>();
        var rng = new Random(11);
        for (var i = 0; i < 2000; i++)
        {
            var k = Real.Pick("short", TyperaceBank.All, rng, new HashSet<string>())!.Src.Kind;
            counts[k] = counts.GetValueOrDefault(k) + 1;
        }
        Assert.InRange(counts["classic"], 900, 1100);
        Assert.InRange(counts["proverbs"], 500, 700);
        Assert.InRange(counts["twisters"], 300, 500);
    }

    [Fact]
    public void Unknown_length_or_source_falls_back_to_defaults()
    {
        var h = new RoomHarness("typerace", new { length = "дуже довго", source = "газети" }, services: RoomHarness.WithService(Small()));
        h.Join("Оля");
        var opts = h.View(0).GetProperty("opts");
        Assert.Equal("medium", opts.GetProperty("length").GetString());
        Assert.Equal("all", opts.GetProperty("source").GetString());

        var ok = new RoomHarness("typerace", new { length = "short", source = "twisters" }, services: RoomHarness.WithService(Small()));
        ok.Join("Оля");
        Assert.Equal("short", ok.View(0).GetProperty("opts").GetProperty("length").GetString());
        Assert.Equal("twisters", ok.View(0).GetProperty("opts").GetProperty("source").GetString());
    }

    [Fact]
    public void A_kind_missing_from_the_bank_falls_back_to_what_is_there()
    {
        var onlyProverbs = TyperaceBank.From(Small().Of(TyperaceBank.Proverbs));
        var pick = onlyProverbs.Pick("medium", TyperaceBank.Classic, new Random(1), new HashSet<string>())!;
        Assert.Equal("proverbs", pick.Src.Kind);
        Assert.Null(TyperaceBank.From([]).Pick("medium", TyperaceBank.All, new Random(1), new HashSet<string>()));
    }
}

/// <summary>Маленький банк для тестів гри: п'ять уривків класики, прислів'я й скоромовки — незалежно від data/.</summary>
public static class TyperaceTestBank
{
    public static TyperaceBank Create()
    {
        var list = new List<TyperaceEntry>();
        string[] sentences =
        [
            "Садок вишневий коло хати, хрущі над вишнями гудуть.", "Плугатарі з плугами йдуть, співають ідучи дівчата.",
            "А матері вечерять ждуть.", "Сім’я вечеря коло хати, вечірня зіронька встає.", "Дочка вечерять подає.",
            "Поклала мати коло хати маленьких діточок своїх.", "Сама заснула коло їх.", "Затихло все, тілько дівчата та соловейко не затих.",
            "Над ставом верби похилились.", "Пливе човен, води повен.", "Вітер з гаєм розмовляє.", "Сонце гріє, вітер віє.",
        ];
        var rng = new Random(3);
        for (var i = 0; i < 5; i++)
        {
            var parts = new List<string>();
            var len = 0;
            while (len < 560)
            {
                var s = sentences[rng.Next(sentences.Length)];
                parts.Add(s);
                len += s.Length + 1;
            }
            list.Add(new TyperaceEntry($"cl-{i}", TyperaceBank.Classic, string.Join(' ', parts), "Тарас Шевченко", $"Твір {i + 1}", 1840 + i));
        }
        for (var i = 0; i < 30; i++)
            list.Add(new TyperaceEntry($"pr-{i}", TyperaceBank.Proverbs, $"Прислів’я номер {Words(i)} каже щось мудре.", null, null, null));
        for (var i = 0; i < 12; i++)
            list.Add(new TyperaceEntry($"tw-{i}", TyperaceBank.Twisters, $"Скоромовка {Words(i)}: бук бундючився перед дубом.", null, null, null));
        return TyperaceBank.From(list);
    }

    static string Words(int i) => new[] { "один", "два", "три", "чотири", "п’ять", "шість", "сім", "вісім", "дев’ять", "десять" }[i % 10]
        + (i >= 10 ? " і " + new[] { "ще", "знов", "вкотре" }[i / 10 - 1] : "");
}
