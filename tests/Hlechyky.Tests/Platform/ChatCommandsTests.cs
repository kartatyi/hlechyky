using Hlechyky;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Команди чату (specs/chat-commands.md). Випадковість тут — Random.Shared, тому перевіряємо не
/// конкретний результат, а множину можливих: сто кидків мають лягти всередині того, що обіцяно.
/// </summary>
public class ChatCommandsTests
{
    /// <summary>Скільки разів крутити команду, щоб побачити обидва боки монети й усі варіанти вибору.</summary>
    const int Spins = 200;

    // ---------------------------------------------------------------------------- /coin

    [Fact]
    public void Coin_falls_on_one_of_two_sides_and_shows_both_over_time()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Spins; i++)
        {
            var r = ChatCommands.Run("/coin");
            Assert.Null(r.Error);
            Assert.Equal("coin", r.Kind);
            Assert.Contains(r.Text!, new[] { "🪙 Орел", "🪙 Решка" });
            seen.Add(r.Text!);
        }
        Assert.Equal(2, seen.Count);
    }

    [Theory]
    [InlineData("/coin")]
    [InlineData("/COIN")]
    [InlineData("/монетка")]
    [InlineData("/Монетка")]
    public void Coin_answers_to_its_aliases_in_any_case(string text)
    {
        var r = ChatCommands.Run(text);
        Assert.Null(r.Error);
        Assert.Equal("coin", r.Kind);
    }

    [Fact]
    public void Coin_ignores_whatever_was_typed_after_it()
    {
        var r = ChatCommands.Run("/coin на пиво");
        Assert.Null(r.Error);
        Assert.StartsWith("🪙 ", r.Text);
    }

    // ---------------------------------------------------------------------------- /choose

    [Fact]
    public void Choose_splits_by_pipe_and_trims_the_spaces()
    {
        var r = ChatCommands.Run("/choose  чай |   кава  | компот ");
        Assert.Null(r.Error);
        Assert.Equal("choose", r.Kind);
        Assert.Contains("(з: чай, кава, компот)", r.Text);
    }

    [Fact]
    public void Choose_falls_back_to_a_comma_when_there_is_no_pipe()
    {
        var r = ChatCommands.Run("/choose чай, кава");
        Assert.Null(r.Error);
        Assert.Contains("(з: чай, кава)", r.Text);
    }

    [Fact]
    public void Choose_keeps_a_comma_inside_an_option_when_the_pipe_is_there()
    {
        // «чай, міцний» — один варіант: роздільник обрано скісною, кома вже просто кома
        var r = ChatCommands.Run("/choose чай, міцний | кава");
        Assert.Contains("(з: чай, міцний, кава)", r.Text);
        Assert.Equal(2, ChatCommands.SplitChoices("чай, міцний | кава").Count);
    }

    [Theory]
    [InlineData("чай або кава", new[] { "чай", "кава" })]
    [InlineData("чай чи кава", new[] { "чай", "кава" })]
    [InlineData("чай, кава або компот", new[] { "чай", "кава", "компот" })]
    [InlineData("піца суші", new[] { "піца", "суші" })]
    [InlineData("чипси чи горішки", new[] { "чипси", "горішки" })]
    [InlineData("Червоне АБО біле вино", new[] { "Червоне", "біле вино" })]
    public void Choose_understands_or_and_plain_spaces(string args, string[] expected)
    {
        Assert.Equal(expected, ChatCommands.SplitChoices(args));
    }

    [Fact]
    public void Choose_picks_only_from_what_it_was_given()
    {
        var options = new[] { "чай", "кава", "компот" };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Spins; i++)
        {
            var r = ChatCommands.Run("/choose " + string.Join(" | ", options));
            var pick = r.Text!["🤔 Обираю: ".Length..r.Text!.IndexOf(" (з:", StringComparison.Ordinal)];
            Assert.Contains(pick, options);
            seen.Add(pick);
        }
        Assert.Equal(options.Length, seen.Count);
    }

    [Theory]
    [InlineData("/choose")]
    [InlineData("/choose чай")]
    [InlineData("/choose чай |")]
    [InlineData("/choose  |  |  ")]
    public void Choose_needs_at_least_two_options(string text)
    {
        var r = ChatCommands.Run(text);
        Assert.Equal("Дай хоч два варіанти: /обери чай або кава", r.Error);
        Assert.Null(r.Text);
    }

    [Fact]
    public void Choose_refuses_a_crowd_and_a_novel()
    {
        var many = string.Join(" | ", Enumerable.Range(1, ChatCommands.MaxChoices + 1).Select(i => "в" + i));
        Assert.Contains("Забагато варіантів", ChatCommands.Run("/choose " + many).Error);

        var longOne = new string('я', ChatCommands.MaxChoiceLength + 1);
        Assert.Contains("Варіант задовгий", ChatCommands.Run($"/choose чай | {longOne}").Error);

        // рівно на межі — ще можна
        var edge = new string('я', ChatCommands.MaxChoiceLength);
        Assert.Null(ChatCommands.Run($"/choose чай | {edge}").Error);
    }

    [Theory]
    [InlineData("/обери чай | кава")]
    [InlineData("/ВИБЕРИ чай | кава")]
    [InlineData("/Choose чай | кава")]
    public void Choose_answers_to_its_aliases_in_any_case(string text)
    {
        var r = ChatCommands.Run(text);
        Assert.Null(r.Error);
        Assert.Equal("choose", r.Kind);
    }

    // ---------------------------------------------------------------------------- /8ball

    [Theory]
    [InlineData("/8ball")]
    [InlineData("/8ball    ")]
    [InlineData("/8ball чи")]
    public void Ball_wants_a_real_question(string text)
    {
        var r = ChatCommands.Run(text);
        Assert.Equal("Спитай щось довше: /куля чи буде дощ?", r.Error);
    }

    [Fact]
    public void Ball_keeps_the_question_next_to_the_answer()
    {
        var r = ChatCommands.Run("/8ball чи піде дощ?");
        Assert.Null(r.Error);
        Assert.Equal("8ball", r.Kind);
        Assert.StartsWith("чи піде дощ? — 🔮 Дядько Глек каже: „", r.Text);
        Assert.EndsWith("“", r.Text);
        var said = r.Text!["чи піде дощ? — 🔮 Дядько Глек каже: „".Length..^1];
        Assert.Contains(ChatCommands.BallAnswers, a => a.Text == said);
    }

    [Fact]
    public void Ball_cuts_a_question_that_does_not_fit_the_line()
    {
        var huge = new string('я', 400);
        var r = ChatCommands.Run("/8ball " + huge);
        Assert.Null(r.Error);
        Assert.StartsWith(new string('я', ChatCommands.MaxQuestion) + "…", r.Text);
    }

    [Fact]
    public void Ball_bank_has_twenty_distinct_answers_in_all_three_moods()
    {
        var bank = ChatCommands.BallAnswers;
        Assert.True(bank.Count >= 20, $"відповідей має бути щонайменше 20, а їх {bank.Count}");
        Assert.Equal(bank.Count, bank.Select(a => a.Text).Distinct(StringComparer.Ordinal).Count());
        foreach (var mood in Enum.GetValues<ChatCommands.BallMood>())
            Assert.Contains(bank, a => a.Mood == mood);
        Assert.All(bank, a => Assert.False(string.IsNullOrWhiteSpace(a.Text)));
    }

    [Fact]
    public void Ball_uses_the_whole_bank()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // 2000 кидків на 21 відповідь: пропустити хоч одну — це вже не випадковість, а баг у виборі
        for (var i = 0; i < 2000; i++) seen.Add(ChatCommands.Run("/8ball чи буде дощ").Text!);
        Assert.Equal(ChatCommands.BallAnswers.Count, seen.Count);
    }

    [Theory]
    [InlineData("/куля чи буде дощ")]
    [InlineData("/ГЛЕК чи буде дощ")]
    [InlineData("/8BALL чи буде дощ")]
    public void Ball_answers_to_its_aliases_in_any_case(string text)
    {
        var r = ChatCommands.Run(text);
        Assert.Null(r.Error);
        Assert.Equal("8ball", r.Kind);
    }

    // ---------------------------------------------------------------------------- /столи

    [Fact]
    public void Tables_answers_with_the_ids_the_lobby_gave()
    {
        var r = ChatCommands.Run("/столи", () => ["aa11", "bb22"]);

        Assert.Null(r.Error);
        Assert.Equal("Живих столів: 2", r.Text);
        Assert.Equal("tables", r.Kind);
        Assert.Equal(["aa11", "bb22"], r.Rooms);
    }

    [Fact]
    public void One_table_is_said_in_the_singular()
    {
        var r = ChatCommands.Run("/tables", () => ["aa11"]);

        Assert.Equal("Живий стіл", r.Text);
        Assert.Equal(["aa11"], r.Rooms);
    }

    [Fact]
    public void An_empty_lobby_answers_only_the_one_who_asked()
    {
        var r = ChatCommands.Run("/столи", () => []);

        Assert.Equal("За столами ні душі. Постав свій у розділі «Ігри»", r.Error);
        Assert.Null(r.Rooms);
    }

    [Fact]
    public void Without_a_lobby_the_command_says_so_instead_of_lying_about_empty()
    {
        // Так /столи виглядає для агента: столи в нього свої, через list_rooms.
        var r = ChatCommands.Run("/столи");

        Assert.Equal("Звідси столів не видно", r.Error);
    }

    [Fact]
    public void A_crowded_lobby_is_cut_to_what_fits_one_card()
    {
        var many = Enumerable.Range(0, ChatCommands.MaxTables + 5).Select(i => "id" + i).ToList();
        var r = ChatCommands.Run("/столи", () => many);

        Assert.Equal(ChatCommands.MaxTables, r.Rooms!.Count);
        Assert.Equal($"Живих столів: {many.Count}", r.Text);   // рахуємо всі, показуємо скільки влізло
    }

    // ---------------------------------------------------------------------------- /клич

    [Theory]
    [InlineData("/клич Оля")]
    [InlineData("/клич @Оля")]
    [InlineData("/клич   @Оля  ")]
    [InlineData("/поклич Оля")]
    [InlineData("/invite @Оля")]
    [InlineData("/Клич Оля")]
    public void Klych_hands_the_nick_without_the_at_sign_to_whoever_calls(string text)
    {
        string? asked = null;
        var r = ChatCommands.Run(text, null, who => { asked = who; return new(Text: "📣 гаразд", Kind: "note"); });

        Assert.Equal("Оля", asked);
        Assert.Null(r.Error);
        Assert.Equal("note", r.Kind);
    }

    [Theory]
    [InlineData("/клич")]
    [InlineData("/клич   ")]
    [InlineData("/клич @")]
    public void Klych_needs_a_nick(string text)
    {
        var called = false;
        var r = ChatCommands.Run(text, null, _ => { called = true; return new(); });

        Assert.Equal("Кого гукнути? Так: /клич Оля", r.Error);
        Assert.False(called);
    }

    [Fact]
    public void Klych_passes_the_callers_refusal_on_as_an_ordinary_command_error()
    {
        var r = ChatCommands.Run("/клич Оля", null, _ => new(Error: "Оля зараз не на сайті"));

        Assert.Equal("Оля зараз не на сайті", r.Error);
        Assert.Null(r.Text);
    }

    [Fact]
    public void Without_anyone_to_call_the_command_says_so()
    {
        // Так /клич виглядає для агента: у нього нема ні з'єднання, ні кнопки «📣 Покликати».
        Assert.Equal("Звідси кликати не вийде", ChatCommands.Run("/клич Оля").Error);
    }

    // ---------------------------------------------------------------------------- крапка замість скісної

    [Theory]
    [InlineData(".кубик 20", "/кубик 20")]
    [InlineData(".обери чай або кава", "/обери чай або кава")]
    [InlineData(".монетка", "/монетка")]
    [InlineData(".Кубик", "/Кубик")]
    [InlineData(".вибери а | б", "/вибери а | б")]
    [InlineData(".куля чи буде дощ", "/куля чи буде дощ")]
    [InlineData(".глек чи буде дощ", "/глек чи буде дощ")]
    [InlineData(".столи", "/столи")]
    [InlineData(".стіл", "/стіл")]
    [InlineData(".клич Оля", "/клич Оля")]
    [InlineData(".поклич @Оля", "/поклич @Оля")]
    [InlineData(".пароль Оля нове123", "/пароль Оля нове123")]   // інакше новий пароль ліг би в Балачки реплікою
    public void A_dot_before_a_ukrainian_command_name_is_the_same_command(string typed, string command)
    {
        Assert.Equal(command, ChatCommands.FromDot(typed));
    }

    [Theory]
    [InlineData("...")]
    [InlineData(".ну")]
    [InlineData(".ну що, граємо?")]
    [InlineData(". кубик")]
    [InlineData(".кубики")]
    [InlineData(".кубик,")]
    [InlineData(".roll")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("кубик")]
    [InlineData("/кубик")]
    public void Any_other_dot_is_just_a_line(string typed)
    {
        Assert.Null(ChatCommands.FromDot(typed));
    }

    [Fact]
    public void A_dotted_command_rolls_and_chooses_like_the_slashed_one()
    {
        Assert.Equal("dice", ChatCommands.Run(ChatCommands.FromDot(".кубик 20")!).Kind);
        var pick = ChatCommands.Run(ChatCommands.FromDot(".обери чай або кава")!);
        Assert.Equal("choose", pick.Kind);
        Assert.Contains("(з: чай, кава)", pick.Text);
    }

    [Fact]
    public void A_dotted_command_twice_is_no_repeat_for_the_flood_guard()
    {
        // Хаб перетворює крапку на скісну ще до лічильника флуду: команди на повтор не перевіряються, і кинути
        // «.кубик» двічі поспіль — так само нормально, як і /кубик. Без цього друга спроба впиралась би в «Це вже тяпнуто».
        var flood = new ChatFlood();
        var now = DateTimeOffset.UtcNow;
        var text = ChatCommands.FromDot(".кубик")!;

        Assert.Null(flood.Check("Оля", text, now));
        Assert.Null(flood.Check("Оля", text, now.AddSeconds(1)));

        var raw = new ChatFlood();                                    // а сира крапка — звичайний текст, і повтор ловиться
        Assert.Null(raw.Check("Оля", ".кубик", now));
        Assert.Equal(ChatFlood.Repeat, raw.Check("Оля", ".кубик", now.AddSeconds(1)));
    }

    // ---------------------------------------------------------------------------- решта

    [Fact]
    public void An_unknown_command_names_the_ones_that_exist()
    {
        var r = ChatCommands.Run("/фігня");
        Assert.Equal("Команди /фігня нема. Є /кубик, /монетка, /обери, /куля, /столи і /клич", r.Error);
        Assert.Null(r.Text);
    }

    [Fact]
    public void The_dice_still_rolls()
    {
        var r = ChatCommands.Run("/roll 2-2");
        Assert.Equal("З таких меж кубик нецікавий", r.Error);
        Assert.Equal("dice", ChatCommands.Run("/roll").Kind);
    }
}
