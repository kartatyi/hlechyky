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
        Assert.False(BluffText.LooksSame("1854", "1855"));
        // Самі смайлики нормалізуються в порожнечу — тоді порівнюємо як написано.
        Assert.True(BluffText.LooksSame("🙂", "🙂"));
        Assert.False(BluffText.LooksSame("🙂", "😀"));
    }

    [Fact]
    public void Latin_lookalikes_invisible_characters_and_stress_marks_do_not_hide_the_truth()
    {
        Assert.True(BluffText.LooksTrue("гaсi", Gas));                // латинські a та i
        Assert.True(BluffText.LooksTrue("га​сі", Gas));          // нульовий пробіл посередині
        Assert.True(BluffText.LooksTrue("га­сі", Gas));          // м'який перенос
        Assert.True(BluffText.LooksTrue("га́сі", Gas));          // наголос
        Assert.True(BluffText.LooksTrue("Бyг", Bug));                 // латинська y
        Assert.Equal(BluffText.Norm("гасі"), BluffText.Norm("гaсi"));
    }

    [Fact]
    public void Clean_keeps_the_authors_case_and_punctuation_but_squeezes_spaces_and_drops_invisibles()
    {
        Assert.Equal("Свинячому салі!", BluffText.Clean("  Свинячому  салі!\n"));
        Assert.Equal("гасі", BluffText.Clean("га​сі"));
        Assert.Equal("a b", BluffText.Clean("a\tb\u0007"));
        Assert.Equal("", BluffText.Clean("   ​ "));
        Assert.Equal("", BluffText.Clean(null));
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
        Assert.Equal(3, BluffText.Lev("kitten", "sitting"));
        Assert.Equal(0, BluffText.Lev("", ""));
        Assert.Equal(4, BluffText.Lev("", "гасі"));
    }
}
