using Hlechyky.Games.Impl;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Байкарі»: перевірка тексту (specs/bluff.md §2.4) — найтестованіша частина гри. Сервер має відкинути правду,
/// написану випадково (відмінок, одруківка, у фразі, шматок), і не чіпати чесну брехню.
/// </summary>
public class BluffTextTests
{
    static readonly string[] Gas = ["гасі", "гас", "гасові лампи"];
    static readonly string[] Bug = ["Південний Буг", "пд буг", "буг", "південний буг"];
    static readonly string[] Pig = ["морську свинку", "морська свинка", "морських свинок"];
    static readonly string[] Meters = ["2 метрів", "2 м", "два метри", "двох метрів"];

    [Fact]
    public void Norm_folds_case_apostrophes_punctuation_and_spaces()
    {
        Assert.Equal("гасі", BluffText.Norm("  Гасі!!! "));
        Assert.Equal("пд буг", BluffText.Norm("Пд.   Буг"));
        Assert.Equal("олії з реп'яхів", BluffText.Norm("Олії з реп’яхів"));
        Assert.Equal("олії з реп'яхів", BluffText.Norm("олії з репʼяхів"));
        Assert.Equal("нью-йорку", BluffText.Norm("Нью-Йорку"));
        Assert.Equal("", BluffText.Norm("🙂 !!!"));
        Assert.Equal(["нью", "йорку"], BluffText.Tokens("Нью–Йорку"));
    }

    [Fact]
    public void The_exact_truth_is_a_truth()
    {
        Assert.True(BluffText.LooksTrue("гасі", Gas));
        Assert.True(BluffText.LooksTrue("Гасі!", Gas));
        Assert.True(BluffText.LooksTrue("гас", Gas));
    }

    [Fact]
    public void An_inflected_truth_is_a_truth()
    {
        Assert.True(BluffText.LooksTrue("гасу", Gas));
        Assert.True(BluffText.LooksTrue("гасом", Gas));
        Assert.True(BluffText.LooksTrue("гасових лампах", Gas));
        Assert.True(BluffText.LooksTrue("морських свинок", Pig));
    }

    [Fact]
    public void The_truth_hidden_inside_a_phrase_is_a_truth()
    {
        Assert.True(BluffText.LooksTrue("звісно ж, на гасі", Gas));
        Assert.True(BluffText.LooksTrue("на гасі", Gas));
        Assert.True(BluffText.LooksTrue("Південний Буг, звісно", Bug));
    }

    [Fact]
    public void A_piece_of_a_multiword_truth_is_a_truth()
    {
        Assert.True(BluffText.LooksTrue("Буг", Bug));
        Assert.True(BluffText.LooksTrue("Пд. Буг", Bug));
        Assert.True(BluffText.LooksTrue("свинку", Pig));
        // Шматок на одну-дві літери — не правда: «в» чи «2» ще нікого не видали.
        Assert.False(BluffText.LooksTrue("2", Meters));
    }

    [Fact]
    public void An_unrelated_lie_is_not_a_truth()
    {
        Assert.False(BluffText.LooksTrue("свинячому салі", Gas));
        Assert.False(BluffText.LooksTrue("нафті", Gas));
        Assert.False(BluffText.LooksTrue("спирті", Gas));
        Assert.False(BluffText.LooksTrue("Дніпро", Bug));
        Assert.False(BluffText.LooksTrue("Десна", Bug));
        Assert.False(BluffText.LooksTrue("Південний Дніпро", Bug));
        Assert.False(BluffText.LooksTrue("золоту рибку", Pig));
    }

    [Fact]
    public void A_typo_away_from_the_truth_is_still_the_truth()
    {
        Assert.True(BluffText.LooksTrue("газі", Gas));
        Assert.True(BluffText.LooksTrue("Уругавї", ["Уругваї"]));    // переставлені літери — дві правки на спільному корені
        Assert.True(BluffText.LooksTrue("Кульчицкий", ["Кульчицький"]));
        Assert.True(BluffText.LooksTrue("Нью Йорку", ["Нью-Йорку"]));
    }

    [Fact]
    public void Numbers_match_by_digits_and_words_come_from_accept()
    {
        Assert.True(BluffText.LooksTrue("2 метри", Meters));      // «метри»/«метрів» — одна правка
        Assert.True(BluffText.LooksTrue("два метри", Meters));    // слово — лише з accept
        Assert.True(BluffText.LooksTrue("2 м", Meters));
        Assert.False(BluffText.LooksTrue("2 км", Meters));
        Assert.False(BluffText.LooksTrue("20 метрів", Meters));
        Assert.False(BluffText.LooksTrue("2 сантиметрів", Meters));
        // Числа — лише точно: рік поруч із правдою — чесна брехня, а не одруківка.
        Assert.False(BluffText.LooksTrue("1855", ["1854"]));
        Assert.True(BluffText.LooksTrue("1854 року", ["1854"]));
    }

    [Fact]
    public void LooksSame_ignores_case_and_punctuation_but_not_word_order()
    {
        Assert.True(BluffText.LooksSame("свинячому салі", "Свинячому салі!"));
        Assert.True(BluffText.LooksSame("свинячому салі", "свинячим салом"));
        Assert.False(BluffText.LooksSame("салі", "салі свинячому"));
        Assert.False(BluffText.LooksSame("салі свинячому", "свинячому салі"));
        Assert.False(BluffText.LooksSame("в Римі", "в Криму"));
        // Службові слова не роблять брехню новою: у питанні й так стоїть «в ___».
        Assert.True(BluffText.LooksSame("у хвості", "хвості"));
        Assert.True(BluffText.LooksSame("на гасі", "Гасі"));
        Assert.False(BluffText.LooksSame("кіт і пес", "пес і кіт"));
        Assert.False(BluffText.LooksSame("нахилитися до сонця", "нахилитися від сонця"));   // напрямок — не службове слово
        Assert.False(BluffText.LooksSame("1854", "1855"));
        // Самі смайлики нормалізуються в порожнечу — тоді порівнюємо як написано.
        Assert.True(BluffText.LooksSame("🙂", "🙂"));
        Assert.False(BluffText.LooksSame("🙂", "😀"));
    }

    [Fact]
    public void Latin_lookalikes_invisible_characters_and_stress_marks_do_not_hide_the_truth()
    {
        Assert.True(BluffText.LooksTrue("гaсi", Gas));                // латинські a та i
        Assert.True(BluffText.LooksTrue("га\u200bсі", Gas));          // нульовий пробіл посередині
        Assert.True(BluffText.LooksTrue("га\u00adсі", Gas));          // м'який перенос
        Assert.True(BluffText.LooksTrue("га\u0301сі", Gas));          // наголос
        Assert.True(BluffText.LooksTrue("Бyг", Bug));                 // латинська y
        Assert.Equal(BluffText.Norm("гасі"), BluffText.Norm("гaсi"));
    }

    [Fact]
    public void Capital_latin_and_greek_twins_do_not_hide_the_truth_on_uppercase_cards()
    {
        // Картки показано ВЕЛИКИМИ: латинські B, H, M, T (і грецькі) там не відрізнити від В, Н, М, Т.
        string[] hydrogen = ["водень", "гідроген"];
        Assert.True(BluffText.LooksTrue("BOДEHЬ", hydrogen));             // B, O, E, H — латиниця
        Assert.True(BluffText.LooksTrue("ВОД\u0395\u0397Ь", hydrogen));             // \u0395, \u0397 — грецькі
        Assert.True(BluffText.LooksTrue("TAKCI", ["таксі"]));             // уся брехня латиницею
        Assert.True(BluffText.LooksTrue("МАМА", ["mama"]));              // і навпаки: кирилиця під латинську правду
        Assert.True(BluffText.LooksTrue("В0ДЕНЬ", hydrogen));             // нуль посеред слова — це О
        Assert.Equal(BluffText.Norm("водень"), BluffText.Norm("BOДEHЬ"));
        // Чесні латинські слова лишаються собою: HOLLYWOODHILLS не став правдою HOLLYWOODLAND.
        Assert.False(BluffText.LooksTrue("HOLLYWOODHILLS", ["HOLLYWOODLAND"]));
        Assert.False(BluffText.LooksTrue("1930", ["1903"]));
    }

    [Fact]
    public void Invisible_letters_and_fillers_do_not_split_the_truth()
    {
        string[] hydrogen = ["водень"];
        Assert.True(BluffText.LooksTrue("в\u3164о\u3164день", hydrogen));   // HANGUL FILLER — «літера», якої не видно
        Assert.True(BluffText.LooksTrue("во\u115fдень", hydrogen));
        Assert.True(BluffText.LooksTrue("во\uffa0день", hydrogen));
        Assert.True(BluffText.LooksTrue("во\U000E0020день", hydrogen));    // тег-символ (дві половинки UTF-16)
        Assert.True(BluffText.LooksTrue("во\u2060день", hydrogen));
        Assert.True(BluffText.LooksTrue("во\u034fдень", hydrogen));
        Assert.Equal("водень", BluffText.Clean("в\u3164о\u3164день"));
        Assert.Equal("водень", BluffText.Clean("во\U000E0020день"));
        Assert.Equal("во день", BluffText.Clean("во\u2800день"));            // порожня клітинка Брайля — це пробіл
        Assert.Equal("", BluffText.Clean("\u3164\u3164 \u2800"));
        Assert.Equal("\u2764\ufe0f серце", BluffText.Clean("\u2764\ufe0f серце"));   // VS16 смайлика лишається
    }

    [Fact]
    public void A_word_mixing_alphabets_is_flagged()
    {
        Assert.True(BluffText.MixedScripts("BOДEHЬ"));
        Assert.True(BluffText.MixedScripts("гaсi"));
        Assert.True(BluffText.MixedScripts("вод\u0395нь"));
        Assert.False(BluffText.MixedScripts("водень"));
        Assert.False(BluffText.MixedScripts("HOLLYWOOD land"));
        Assert.False(BluffText.MixedScripts("iPhone-ом за 1000"));       // різні абетки в різних словах — можна
        Assert.False(BluffText.MixedScripts("🙂 водень!"));
        Assert.False(BluffText.MixedScripts("2м"));
    }

    [Fact]
    public void Service_words_in_and_with_do_not_turn_the_truth_into_a_lie()
    {
        // «зі сліз» — правда; «із сліз» і «з сліз» на картці — та сама правда, лише інше службове слово.
        string[] tears = ["зі сліз", "з сліз", "із сліз", "сльози"];
        Assert.True(BluffText.LooksTrue("із сліз", ["зі сліз"]));
        Assert.True(BluffText.LooksTrue("зо сліз", tears));
        Assert.True(BluffText.LooksTrue("в спальні", ["у спальні"]));
        Assert.True(BluffText.LooksTrue("У спальні!", ["в спальні"]));
        Assert.True(BluffText.LooksTrue("кіт й пес", ["кіт і пес"]));
        Assert.False(BluffText.LooksTrue("з війок", tears));
        Assert.False(BluffText.LooksTrue("у ванній", ["у спальні"]));
    }

    [Fact]
    public void Lies_merge_by_endings_but_different_words_stay_different_cards()
    {
        // Різні слова з однією одруківкою різниці — різні брехні, а не одна спільна картка.
        Assert.False(BluffText.LooksSame("кота", "кита"));
        Assert.False(BluffText.LooksSame("Іраку", "Ірану"));
        Assert.False(BluffText.LooksSame("Австрії", "Австралії"));
        Assert.False(BluffText.LooksSame("Ірландії", "Ісландії"));
        Assert.False(BluffText.LooksSame("салі", "салоні"));
        // А одне слово в різних відмінках — одна картка.
        Assert.True(BluffText.LooksSame("на Марсі", "Марсу"));
        Assert.True(BluffText.LooksSame("свинячому салі", "свинячим салом"));
        Assert.True(BluffText.LooksSame("кроликів", "кролики"));
        Assert.True(BluffText.LooksSame("лампами", "лампи"));
        Assert.True(BluffText.LooksSame("Ірак", "в Іраку"));
        Assert.True(BluffText.LooksSame("синій", "синя"));
        // Основа — без закінчення: «кита» (кит) і «Китаї» (Китай) — різні слова, хоч одне й починає інше.
        Assert.False(BluffText.LooksSame("кита", "Китаї"));
        Assert.True(BluffText.LooksSame("кит", "кита"));
        Assert.True(BluffText.LooksSame("Китай", "в Китаї"));
        Assert.True(BluffText.LooksSame("школа", "школу"));
        Assert.True(BluffText.LooksSame("школою", "школи"));
        Assert.True(BluffText.LooksSame("Уругвай", "Уругваї"));
        Assert.True(BluffText.LooksSame("заштовхують", "заштовхує"));
    }

    [Fact]
    public void Clean_keeps_the_authors_case_and_punctuation_but_squeezes_spaces_and_drops_invisibles()
    {
        Assert.Equal("Свинячому салі!", BluffText.Clean("  Свинячому\u00a0\u00a0салі!\n"));
        Assert.Equal("гасі", BluffText.Clean("га\u200bсі"));
        Assert.Equal("a b", BluffText.Clean("a\tb\u0007"));
        Assert.Equal("", BluffText.Clean("   \u200b "));
        Assert.Equal("", BluffText.Clean(null));
    }

    [Fact]
    public void A_long_accept_phrase_does_not_turn_its_every_word_into_the_truth()
    {
        // Банк: «овоч» з формою «овоч, а не фрукт» — «фрукт» лишається чесною брехнею.
        string[] tomato = ["овоч", "овочі", "овоч, а не фрукт"];
        Assert.False(BluffText.LooksTrue("фрукт", tomato));
        Assert.True(BluffText.LooksTrue("овочем", tomato));
        // Слово, що лише починається з правди, — не відмінок: «hollywoodhills» ≠ «hollywood».
        string[] sign = ["HOLLYWOODLAND", "hollywood land"];
        Assert.False(BluffText.LooksTrue("HOLLYWOODHILLS", sign));
        Assert.True(BluffText.LooksTrue("Hollywood-Land", sign));
    }

    [Fact]
    public void Token_rules_catch_typos_and_endings_but_not_short_words()
    {
        Assert.True(BluffText.TokenMatch("гасі", "гасу"));          // одна правка на чотирьох літерах
        Assert.True(BluffText.TokenMatch("гасі", "гасом"));         // спільний корінь, дві правки
        Assert.True(BluffText.TokenMatch("кроликів", "кролики"));   // довгий відмінок
        Assert.True(BluffText.TokenMatch("лампи", "лампами"));
        Assert.False(BluffText.TokenMatch("кіт", "кит"));           // три літери — лише точно
        Assert.False(BluffText.TokenMatch("гасі", "салі"));
        Assert.False(BluffText.TokenMatch("hollywood", "hollywoodhills"));   // різниця довша за закінчення
        Assert.Equal(3, BluffText.Lev("kitten", "sitting"));
        Assert.Equal(0, BluffText.Lev("", ""));
        Assert.Equal(4, BluffText.Lev("", "гасі"));
    }

    // Літери інших письмен — лише escape-ами: у коді їх не відрізнити від наших, як і на картці.

    [Fact]
    public void Letters_of_other_scripts_are_foreign_however_much_they_look_like_ours()
    {
        // «МАШИНОЮ» з рецензії: М і А — черокі (U+13B7, U+13AA), решта — кирилиця; на картці — та сама «МАШИНОЮ».
        Assert.True(BluffText.ForeignLetters("\u13B7\u13AAШИНОЮ"));
        Assert.False(BluffText.MixedScripts("\u13B7\u13AAШИНОЮ"));      // стара перевірка черокі не бачила
        Assert.True(BluffText.ForeignLetters("\u13F4\u13AC\u13BB"));     // уся брехня черокі
        Assert.True(BluffText.ForeignLetters("\uAB7Aшина"));             // мала літера черокі (CSS зробить її великою)
        Assert.True(BluffText.ForeignLetters("\uA4DF\uA4EEШИНА"));       // лісу
        Assert.True(BluffText.ForeignLetters("\u1D0D\u1D00шина"));       // капітель
        Assert.True(BluffText.ForeignLetters("\u0182ІЛИЙ"));             // Ƃ — розширена латиниця, вдає Б
        Assert.True(BluffText.ForeignLetters("\u04AEТКА"));              // Ү — казахська, вдає У
        Assert.True(BluffText.ForeignLetters("\u04BAОРА"));              // Һ
        Assert.True(BluffText.ForeignLetters("В\u0555ДА"));              // вірменська
        Assert.True(BluffText.ForeignLetters("В\u2C9FДА"));              // коптська
        Assert.True(BluffText.ForeignLetters("В\u1C82ДА"));              // вузька о — розширена кирилиця
        Assert.True(BluffText.ForeignLetters("\u03F9ОК"));               // Ϲ: NFKC зробив би з неї Σ і сховав
        Assert.True(BluffText.ForeignLetters("\uFF21\uFF22"));           // широкі
        Assert.True(BluffText.ForeignLetters("\U0001D40C\U0001D400"));   // «математичні»
        Assert.True(BluffText.ForeignLetters("В\u0966ДА"));              // деванагарі нуль — цифра, але не наша
        Assert.True(BluffText.ForeignLetters("\u212AІТ"));               // знак Кельвіна
        // Наші: українська, сусідні кириличні й латинські літери Європи, основна грецька, апостроф-«літера», смайлики.
        Assert.False(BluffText.ForeignLetters("М'ЯЧ ґава ї є ы ё ў"));
        Assert.False(BluffText.ForeignLetters("pʼять Łódź straße café ș ț ñ ø œ"));
        Assert.False(BluffText.ForeignLetters("π ≈ 3,14 і Ω"));
        Assert.False(BluffText.ForeignLetters("\u2764\uFE0F 5 м² за 100 \u20AC"));
        Assert.False(BluffText.ForeignLetters("\u216F"));                // римська тисяча — NFKC робить з неї M
        Assert.False(BluffText.ForeignLetters(""));
        Assert.False(BluffText.ForeignLetters(null));
    }

    [Fact]
    public void Every_letter_of_the_lookalike_scripts_is_foreign()
    {
        // Цілі блоки, де живуть двійники: черокі, лісу, канадські склади, вірменська, коптська, тифінаг, IPA й капітель,
        // розширена латиниця й кирилиця, широкі. Жодна їхня літера не пройде — хоч який двійник знайдеться завтра.
        (int From, int To)[] blocks =
        [
            (0x13A0, 0x13FF), (0xAB70, 0xABBF), (0xA4D0, 0xA4FF), (0x1400, 0x167F), (0x0530, 0x058F), (0x2C80, 0x2CFF),
            (0x2D30, 0x2D7F), (0x0250, 0x02AF), (0x1D00, 0x1DBF), (0x0180, 0x0217), (0x021C, 0x024F), (0x1E00, 0x1EFF),
            (0x2C60, 0x2C7F), (0xA720, 0xA7FF), (0xAB30, 0xAB6F), (0x0460, 0x048F), (0x0492, 0x052F), (0x1C80, 0x1C8F),
            (0xA640, 0xA69F), (0x0370, 0x0385), (0x03CF, 0x03FF), (0xFF21, 0xFF5A),
        ];
        var checkedLetters = 0;
        foreach (var (from, to) in blocks)
            for (var c = from; c <= to; c++)
            {
                if (!System.Text.Rune.IsValid(c) || !System.Text.Rune.IsLetter(new System.Text.Rune(c))) continue;
                var s = char.ConvertFromUtf32(c);
                Assert.True(BluffText.ForeignLetters(s), $"U+{c:X4} {s}");
                checkedLetters++;
            }
        Assert.True(checkedLetters > 1500, $"перевірено лише {checkedLetters} літер");
    }

    [Fact]
    public void Dotless_i_kra_thorn_and_greek_yi_fold_to_their_twins()
    {
        // «KıT» — уся латиниця, а CSS uppercase покаже «KIT»: той самий «КІТ».
        Assert.True(BluffText.LooksTrue("K\u0131T", ["кіт"]));
        Assert.Equal("кіт", BluffText.Norm("K\u0131T"));
        Assert.Equal("кіт", BluffText.Norm("\u0138\u0131T"));           // ĸ — латинська «кра»
        Assert.Equal("рік", BluffText.Norm("\u00FEIK"));                // þ на картці — Þ
        Assert.Equal("їжак", BluffText.Norm("\u03AAЖАК"));              // Ϊ — грецька йота з двома крапками
        Assert.False(BluffText.MixedScripts("K\u0131T"));
    }

    [Fact]
    public void A_lone_zero_or_three_reads_as_the_letter_on_the_card()
    {
        // b0118: правда «о четвертій ранку»; «0 четвертій ранку» на великій картці майже не відрізнити.
        string[] four = ["о четвертій ранку", "четвертій ранку", "о четвертій"];
        Assert.True(BluffText.LooksTrue("0 четвертій ранку", four));
        Assert.True(BluffText.LooksTrue("0 ЧЕТВЕРТІЙ РАНКУ", ["о четвертій ранку"]));
        Assert.True(BluffText.LooksTrue("3 друзями", ["з друзями"]));
        Assert.True(BluffText.LooksTrue("з дні", ["3 дні"]));           // і навпаки: цифра в правді
        // Число лишається числом: інша цифра — чесна брехня, а в злитті «3 коти» й «коти» — різні картки.
        Assert.False(BluffText.LooksTrue("4 дні", ["3 дні"]));
        Assert.False(BluffText.LooksTrue("10 четвертій ранку", ["о четвертій ранку"]));
        Assert.False(BluffText.LooksTrue("30 друзями", ["з друзями"]));
        Assert.False(BluffText.LooksSame("3 коти", "коти"));
        Assert.Equal("0 котів", BluffText.Norm("0 котів"));
    }

    [Fact]
    public void A_symbol_inside_a_word_is_the_letter_it_mimics_but_alone_it_stays_a_symbol()
    {
        Assert.True(BluffText.LooksTrue("МА\u00D7ОРКА", ["махорка"]));  // × посеред слова — Х
        Assert.True(BluffText.LooksTrue("К|Т", ["кіт"]));               // | — І
        Assert.True(BluffText.LooksTrue("\u20ACВА", ["Єва"]));          // € — Є
        Assert.True(BluffText.LooksTrue("\u220F\u212EТРО", ["Петро"]));  // ∏ біля ℮, а той — біля літери
        Assert.Equal("махорка", BluffText.Norm("МА\u00D7ОРКА"));
        Assert.Equal("2 2", BluffText.Norm("2\u00D72"));                 // між цифрами — знак множення
        Assert.Equal("5", BluffText.Norm("5 \u20AC"));                   // окремо — валюта
        Assert.Equal("кіт пес", BluffText.Norm("кіт | пес"));
    }

    [Fact]
    public void A_glued_or_split_truth_is_still_the_truth()
    {
        // Рецензія: звичайною клавіатурою, без жодних особливих знаків, правду склеювали чи розбивали — і вона проходила.
        Assert.True(BluffText.LooksTrue("кістокмамонта", ["кісток мамонта"]));
        Assert.True(BluffText.LooksTrue("яєчнібілки", ["яєчні білки"]));
        Assert.True(BluffText.LooksTrue("35разів", ["35 разів"]));            // «0 і 3 — літери» тут не читаємо
        Assert.True(BluffText.LooksTrue("РічардаЛевовеСерце", ["Річарда Левове Серце"]));
        string[] disney = ["Діснейленд"];
        foreach (var lie in new[] { "ДІСНЕЙ ЛЕНД", "Діс-ней-ленд", "ДІСНЕЙ.ЛЕНД", "Д'ІСНЕЙ'ЛЕНД", "ді с ней лен д", "ДІСНЕЙ_ЛЕНД" })
            Assert.True(BluffText.LooksTrue(lie, disney), lie);
        Assert.True(BluffText.LooksTrue("майдан-чики", ["майданчики"]));
        Assert.True(BluffText.LooksTrue("ші-сть", ["шість"]));
        // Жива партія рецензента (b0191): обидві брехні лягли на стіл поруч із правдою.
        string[] swap = ["перейшла на правосторонній рух"];
        Assert.True(BluffText.LooksTrue("ПЕРЕЙШЛА НАПРАВОСТОРОННІЙ РУХ", swap));
        Assert.True(BluffText.LooksTrue("перейшла на право сторонній рух", swap));
        Assert.True(BluffText.LooksTrue("перейшла направо сторонній рух", swap));     // межа посунулась з обох боків
        // Той самий допуск на одруківки, що й для слів, правда у фразі й шматок правди — теж склеєні.
        Assert.True(BluffText.LooksTrue("ДІЗНЕЙ ЛЕНД", disney));
        Assert.True(BluffText.LooksTrue("кістокмамонту", ["кісток мамонта"]));
        Assert.True(BluffText.LooksTrue("звісно ж, Діс-ней-ленд", disney));
        Assert.True(BluffText.LooksTrue("ЛевовеСерце", ["Річарда Левове Серце"]));
        Assert.True(BluffText.LooksTrue("мор ську", Pig));
        Assert.True(BluffText.LooksTrue("НаПравостороннійРух", swap));
        // Цифри в склеєному — точно, як і окремо; самотній нуль, приклеєний до слова, — усе ще О.
        Assert.True(BluffText.LooksTrue("0четвертійранку", ["о четвертій ранку"]));
        Assert.True(BluffText.LooksTrue("2метрів", Meters));
    }

    [Fact]
    public void Gluing_does_not_turn_an_honest_lie_into_the_truth()
    {
        // Кілька слів проти кількох — лише точно: короткі слова й досі не мають допуску на одруківку.
        Assert.False(BluffText.LooksTrue("кит і пес", ["кіт і пес"]));
        Assert.False(BluffText.LooksTrue("Південний Бог", Bug));
        Assert.False(BluffText.LooksTrue("в Криму", ["в Римі"]));
        // Числа — точно: інша цифра, склеєна чи ні, — чесна брехня.
        Assert.False(BluffText.LooksTrue("4дні", ["3 дні"]));
        Assert.False(BluffText.LooksTrue("20метрів", Meters));
        Assert.False(BluffText.LooksTrue("1855року", ["1854 року"]));
        Assert.False(BluffText.LooksTrue("30 друзями", ["з друзями"]));
        // Шматок — не менше половини змістовних слів, хоч склеєний, хоч розбитий.
        Assert.False(BluffText.LooksTrue("фру кт", ["овоч", "овоч, а не фрукт"]));
        Assert.False(BluffText.LooksTrue("Левове", ["Річарда Левове Серце"]));
        // Слово, що лише починається з правди, — як і раніше, не правда.
        Assert.False(BluffText.LooksTrue("HOLLYWOOD HILLS", ["HOLLYWOODLAND", "hollywood land"]));
        Assert.False(BluffText.LooksTrue("Дісней", ["Діснейленд"]));
        Assert.False(BluffText.LooksTrue("Юрського періоду", ["Діснейленд"]));
        Assert.False(BluffText.LooksTrue("на пляжі", ["на піску"]));
    }

    [Fact]
    public void Digits_inside_a_word_match_exactly_and_the_letters_with_the_usual_slack()
    {
        Assert.True(BluffText.TokenMatch("35разів", "35разів"));
        Assert.True(BluffText.TokenMatch("35разив", "35разів"));
        Assert.False(BluffText.TokenMatch("36разів", "35разів"));
        Assert.False(BluffText.TokenMatch("2км", "2м"));
        Assert.False(BluffText.TokenMatch("1855", "1854"));
        Assert.False(BluffText.TokenMatch("з5разів", "35разів"));   // літера проти цифри — інше
    }

    [Fact]
    public void A_mark_inside_a_word_is_flagged_but_punctuation_and_emoji_beside_words_are_fine()
    {
        // Рецензія: знаки-двійники поза старим списком (рамки, геометрія, математика, чужа пунктуація).
        foreach (var lie in new[]
        {
            "Д\u2502СНЕЙЛЕНД", "Д\u23D0СНЕЙЛЕНД", "Д\u05C0СНЕЙЛЕНД", "МА\u2715ОРКА", "МА\u2573ОРКА", "ЛЬВ\u25EFВІ",
            "ЛЬВ\u25CBВІ", "Ап\u00B0ллон", "п\u2205льку", "МА\u00D7ОРКА", "К|Т", "К!Т", "Д\u2502\u2502СНЕЙЛЕНД",
            "кіт\U0001F642пес", "Д\uFF5CСНЕЙЛЕНД",
        })
            Assert.True(BluffText.MarkInWord(lie), lie);
        // Звичайне письмо: дефіс, апостроф, тире, розділові знаки посеред слова; знаки й смайлики біля пробілу й на краях.
        foreach (var lie in new[]
        {
            "Нью-Йорк", "м'яч", "м’яч", "Пд.Буг", "кіт/пес", "rock&roll", "кіт,пес", "Нью—Йорк", "так…ні", "«Бітлз»",
            "гасі!", "(гас)", "2\u00D72", "5 \u20AC", "100%", "м\u00B2", "кіт \U0001F642 пес", "\u2764\uFE0F серце",
            "га\u0301сі", "iPhone-ом", "20\u00B0C", "В0\u25EFДЕНЬ", "\u20ACВА", "домен .tv", "35 %", "кіт | пес", "", null,
        })
            Assert.False(BluffText.MarkInWord(lie), lie);
    }
}
