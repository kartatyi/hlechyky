using Hlechyky.Games;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Заклики за стіл (lad-contract B2): особистий («📣 Покликати», /клич) і «покликати всіх ще раз». Хаб тут не
/// піднімається — уся логіка в <see cref="Calls"/>; кому саме летить готова розсилка, перевіряє BroadcasterTests.
/// </summary>
public class CallsTests
{
    sealed class Setup
    {
        public FakeClock Clock { get; } = new();
        public Presence Presence { get; } = new();
        public Rooms Rooms { get; }
        public Calls Calls { get; }

        public Setup(params string[] online)
        {
            Rooms = new Rooms(RoomHarness.NewRegistry(), Clock, new GameEvents(), new FakeStakes(), new FakeStore(), RoomHarness.Empty())
            {
                SeedOverride = 7,
            };
            Calls = new Calls(Rooms, Presence, Clock);
            foreach (var nick in online) Online(nick);
        }

        public void Online(string nick) => Presence.Set("c-" + nick, nick);
        public void Offline(string nick) => Presence.Remove("c-" + nick);

        /// <summary>Стіл гри <paramref name="game"/>: перший із <paramref name="crew"/> ставить, решта сідає.</summary>
        public string Table(string game, params string[] crew)
        {
            var id = Rooms.Create(crew[0], game, null).Reply.RoomId!;
            foreach (var nick in crew.Skip(1)) Assert.True(Rooms.Join(id, nick).Reply.Ok);
            return id;
        }

        public RoomOutcome Invite(string by, string room, string who) => Calls.Invite(by, room, who);
    }

    // ---------------------------------------------------------------- особистий заклик

    [Fact]
    public void A_personal_call_goes_to_the_one_who_is_called_and_nobody_else()
    {
        var s = new Setup("Влад", "Оля");
        var id = s.Table("t-party", "Влад");

        var call = s.Invite("Влад", id, "Оля");

        Assert.True(call.Reply.Ok);
        Assert.Equal("📣 Заклик полетів: Оля", call.Reply.Message);
        Assert.Equal(id, call.Reply.RoomId);
        var invite = Assert.Single(call.Out.OfType<Invite>());
        Assert.Equal(new Invite(id, "Влад", "Влад кличе тебе в тестову компанію", "Оля"), invite);
        var line = Assert.Single(call.Out.OfType<InviteLine>());
        Assert.Equal(new InviteLine(id, "Влад", "кличе тебе в тестову компанію", s.Clock.UtcNow, "Оля"), line);
        // Стіл від цього не змінився: ні лобі, ні видів, ні Журналу.
        Assert.Equal(2, call.Out.Count);
    }

    [Fact]
    public void Only_someone_at_the_table_may_call_to_it()
    {
        var s = new Setup("Влад", "Оля", "Ганна");
        var id = s.Table("t-party", "Влад");

        Assert.Equal("Ти тут не граєш", s.Invite("Ганна", id, "Оля").Reply.Message);
        Assert.Equal("Такого столу вже нема", s.Invite("Влад", "00000000", "Оля").Reply.Message);
        Assert.Equal("Спершу скажи, як тебе кликати", s.Invite("гість", id, "Оля").Reply.Message);
    }

    [Fact]
    public void A_solo_game_calls_nobody()
    {
        var s = new Setup("Влад", "Оля");
        var solo = s.Rooms.OpenSolo("Влад", "t-solo", null).Reply.RoomId!;

        var call = s.Invite("Влад", solo, "Оля");

        Assert.Equal("Тут гра на одного — гукати нема кого", call.Reply.Message);
        Assert.Empty(call.Out);
    }

    [Fact]
    public void Nobody_is_called_to_a_game_in_progress_or_to_a_full_table()
    {
        var s = new Setup("Влад", "Петро", "Ганна", "Іван", "Оля");
        var playing = s.Table("ttt", "Влад", "Петро");                         // на двох — уже грають
        Assert.Equal("Партія вже йде — сісти нема куди", s.Invite("Влад", playing, "Оля").Reply.Message);

        var full = s.Table("t-party", "Ганна", "Іван", "Оля", "Хома");          // ByHost: повний, а партії ще нема
        Assert.Equal(RoomStatus.Lobby, s.Rooms.Find(full)!.Status);
        s.Online("Марта");
        Assert.Equal("От халепа — місць уже нема", s.Invite("Ганна", full, "Марта").Reply.Message);
    }

    [Fact]
    public void A_finished_table_with_a_free_seat_may_call_a_newcomer()
    {
        // Так, як це вирішує Rooms.Join: дограний стіл із вільним місцем новенький відкриває наново.
        var s = new Setup("Влад", "Петро", "Оля");
        var id = s.Table("ttt", "Влад", "Петро");
        s.Rooms.Leave(id, "Петро");                                            // техпоразка, місце ◯ вільне
        Assert.Equal(RoomStatus.Finished, s.Rooms.Find(id)!.Status);

        Assert.True(s.Invite("Влад", id, "Оля").Reply.Ok);
        Assert.True(s.Rooms.Join(id, "Оля").Reply.Ok);                         // і справді є куди сісти
    }

    [Fact]
    public void Only_someone_on_the_site_is_called()
    {
        var s = new Setup("Влад", "Оля");
        var id = s.Table("t-party", "Влад");
        s.Offline("Оля");

        Assert.Equal("Оля зараз не на сайті", s.Invite("Влад", id, "Оля").Reply.Message);
        Assert.Equal("Незнайомець зараз не на сайті", s.Invite("Влад", id, "Незнайомець").Reply.Message);
        Assert.Equal("Кого гукнути?", s.Invite("Влад", id, "  ").Reply.Message);
    }

    [Fact]
    public void Someone_already_at_the_table_and_you_yourself_are_not_called()
    {
        var s = new Setup("Влад", "Оля");
        var id = s.Table("t-party", "Влад", "Оля");

        Assert.Equal("Оля вже за цим столом", s.Invite("Влад", id, "оля").Reply.Message);
        Assert.Equal("Себе гукати не треба — ти вже тут", s.Invite("Влад", id, "@влад").Reply.Message);
    }

    [Fact]
    public void A_nameless_guest_cannot_be_called_to_a_table()
    {
        var s = new Setup("Влад", "гість");
        var id = s.Table("t-party", "Влад");

        Assert.Equal("Гостя без імені не покличеш — хай спершу назветься", s.Invite("Влад", id, "гість").Reply.Message);
    }

    [Theory]
    [InlineData("Оля", "Оля")]
    [InlineData("оля", "Оля")]
    [InlineData("@Оля", "Оля")]
    [InlineData("Оля, давай до нас", "Оля")]         // після ніка в /клич часто дописують слово-друге
    [InlineData("Оля Кава", "Оля Кава")]              // найдовший нік, яким рядок починається
    [InlineData("Петро", "гість Петро")]              // гість без акаунта — теж людина
    public void The_nick_is_found_the_way_people_type_it(string typed, string called)
    {
        var s = new Setup("Влад", "Оля", "Оля Кава", "гість Петро", "Олянка");
        var id = s.Table("t-party", "Влад");

        var call = s.Invite("Влад", id, typed);

        Assert.True(call.Reply.Ok, call.Reply.Message);
        Assert.Equal(called, Assert.Single(call.Out.OfType<Invite>()).To);
    }

    [Fact]
    public void The_same_person_is_called_by_the_same_one_once_in_thirty_seconds()
    {
        var s = new Setup("Влад", "Оля", "Петро", "Ганна");
        var id = s.Table("t-party", "Влад", "Ганна");
        Assert.True(s.Invite("Влад", id, "Оля").Reply.Ok);

        s.Clock.Advance(10);
        var again = s.Invite("Влад", id, "Оля");
        Assert.False(again.Reply.Ok);
        Assert.Equal("Оля вже має твій заклик — ще 20 с", again.Reply.Message);
        Assert.Empty(again.Out);
        Assert.True(s.Invite("Влад", id, "Петро").Reply.Ok);                  // пауза — на пару, а не на всіх
        Assert.True(s.Invite("Ганна", id, "Оля").Reply.Ok);                   // і не на всіх, хто кличе Олю

        s.Clock.Advance(20);
        Assert.True(s.Invite("Влад", id, "Оля").Reply.Ok);
    }

    [Fact]
    public void One_nick_sends_at_most_six_personal_calls_a_minute()
    {
        var crowd = Enumerable.Range(1, Calls.PerMinute + 2).Select(i => $"друг{i}").ToArray();
        var s = new Setup(["Влад", .. crowd]);
        var id = s.Table("t-party", "Влад");
        var start = s.Clock.UtcNow;

        for (var i = 0; i < Calls.PerMinute; i++)
        {
            Assert.True(s.Invite("Влад", id, crowd[i]).Reply.Ok);
            s.Clock.Advance(1);
        }
        s.Clock.UtcNow = start.AddSeconds(10);
        Assert.Equal("Забагато закликів поспіль — ще 50 с", s.Invite("Влад", id, crowd[Calls.PerMinute]).Reply.Message);

        s.Clock.UtcNow = start.AddMinutes(1);                                  // найперший заклик випав із хвилини
        Assert.True(s.Invite("Влад", id, crowd[Calls.PerMinute]).Reply.Ok);
        Assert.False(s.Invite("Влад", id, crowd[Calls.PerMinute + 1]).Reply.Ok);   // а другий — ще ні
    }

    [Fact]
    public void A_call_that_did_not_happen_costs_nothing()
    {
        var s = new Setup("Влад");
        var id = s.Table("t-party", "Влад");
        for (var i = 0; i < Calls.PerMinute + 2; i++) Assert.False(s.Invite("Влад", id, "Оля").Reply.Ok);   // її ще нема

        s.Online("Оля");
        Assert.True(s.Invite("Влад", id, "Оля").Reply.Ok);                     // ні пауза пари, ні ліміт не зачеплені
    }

    [Theory]
    [InlineData("мафію", false, "в мафію")]
    [InlineData("«Вгадай мелодію»", false, "у «Вгадай мелодію»")]   // «в «Вгадай» — збіг двох в
    [InlineData("«Свою гру»", false, "у «Свою гру»")]
    [InlineData("ерудит", false, "в ерудит")]
    [InlineData("мафію", true, "у мафію")]                         // «заклик у мафію»: між приголосними — «у»
    [InlineData("ерудит", true, "в ерудит")]
    public void The_game_is_named_with_the_right_preposition(string accusative, bool afterConsonant, string expected)
    {
        Assert.Equal(expected, Calls.Into(accusative, afterConsonant));
    }

    // ---------------------------------------------------------------- «покликати всіх ще раз»

    [Fact]
    public void Calling_again_reaches_everyone_like_a_new_table()
    {
        var s = new Setup("Влад", "Оля");
        var id = s.Table("t-party", "Влад");
        s.Clock.Advance(Calls.AgainGap);

        var again = s.Calls.Again("Влад", id);

        Assert.True(again.Reply.Ok);
        Assert.Equal("📣 Заклик полетів усім", again.Reply.Message);
        Assert.Equal(id, again.Reply.RoomId);
        Assert.Equal(new Invite(id, "Влад", "Влад кличе в тестову компанію"), Assert.Single(again.Out.OfType<Invite>()));
        Assert.Equal(new InviteLine(id, "Влад", "кличе в тестову компанію", s.Clock.UtcNow), Assert.Single(again.Out.OfType<InviteLine>()));
        Assert.DoesNotContain(again.Out, m => m is Journal);                  // Журнал пише лише новий стіл
    }

    [Fact]
    public void A_table_calls_everyone_once_in_two_minutes_counting_from_its_creation()
    {
        var s = new Setup("Влад", "Петро");
        var id = s.Table("t-party", "Влад", "Петро");

        Assert.Equal("Щойно кликали — ще 120 с", s.Calls.Again("Влад", id).Reply.Message);
        s.Clock.Advance(100);
        Assert.Equal("Щойно кликали — ще 20 с", s.Calls.Again("Петро", id).Reply.Message);   // пауза — на стіл
        s.Clock.Advance(20);
        Assert.True(s.Calls.Again("Петро", id).Reply.Ok);
        Assert.Equal("Щойно кликали — ще 120 с", s.Calls.Again("Влад", id).Reply.Message);
    }

    [Fact]
    public void Only_those_at_a_table_that_waits_for_players_call_again()
    {
        var s = new Setup("Влад", "Петро", "Ганна", "Іван", "Оля", "Хома");
        var lobby = s.Table("t-party", "Влад");
        s.Clock.Advance(Calls.AgainGap);
        Assert.Equal("Ти тут не граєш", s.Calls.Again("Оля", lobby).Reply.Message);

        var playing = s.Table("ttt", "Петро", "Ганна");
        Assert.Equal("Партія вже йде — сісти нема куди", s.Calls.Again("Петро", playing).Reply.Message);
        s.Rooms.Leave(playing, "Ганна");                                       // дограний, місце вільне
        Assert.Equal("Партію зіграно, тисни «Ще раз»", s.Calls.Again("Петро", playing).Reply.Message);

        var full = s.Table("t-party", "Іван", "Оля", "Хома", "Ганна");
        s.Clock.Advance(Calls.AgainGap);
        Assert.Equal("От халепа — місць уже нема", s.Calls.Again("Іван", full).Reply.Message);

        var solo = s.Rooms.OpenSolo("Влад", "t-solo", null).Reply.RoomId!;
        Assert.Equal("Тут гра на одного — гукати нема кого", s.Calls.Again("Влад", solo).Reply.Message);
    }

    // ---------------------------------------------------------------- /клич

    [Fact]
    public void Klych_calls_to_the_table_the_author_sits_at()
    {
        var s = new Setup("Влад", "Оля");
        var id = s.Table("t-party", "Влад");

        var r = s.Calls.Command("Влад", "Оля");

        Assert.Null(r.Error);
        Assert.Equal("note", r.Kind);
        Assert.Equal("📣 Заклик у тестову компанію полетів: Оля", r.Text);
        Assert.Equal(new Invite(id, "Влад", "Влад кличе тебе в тестову компанію", "Оля"), Assert.Single(r.Out!.OfType<Invite>()));
        Assert.Single(r.Out!.OfType<InviteLine>(), l => l.To == "Оля");
    }

    [Fact]
    public void Klych_without_a_table_says_where_to_sit_first()
    {
        var s = new Setup("Влад", "Оля");
        s.Rooms.OpenSolo("Влад", "t-solo", null);                             // соло — не стіл, куди кличуть

        var r = s.Calls.Command("Влад", "Оля");

        Assert.Equal("Спершу сідай за стіл — тоді буде куди гукати", r.Error);
        Assert.Null(r.Out);
    }

    [Fact]
    public void Klych_explains_why_the_table_the_author_sits_at_calls_nobody()
    {
        var s = new Setup("Влад", "Петро", "Оля");
        s.Table("ttt", "Влад", "Петро");

        Assert.Equal("Партія вже йде — сісти нема куди", s.Calls.Command("Влад", "Оля").Error);
        s.Offline("Оля");
        Assert.Equal("Партія вже йде — сісти нема куди", s.Calls.Command("Влад", "Оля").Error);   // стіл — спершу
    }

    [Fact]
    public void Klych_in_someone_elses_table_talk_still_calls_to_your_own_table()
    {
        // Глядач пише /клич у балачці чужого столу: кличе туди, де сидить сам, — за чужий стіл кликати не йому.
        var s = new Setup("Влад", "Петро", "Оля");
        var mine = s.Table("t-party", "Влад");
        var theirs = s.Table("t-party", "Петро");

        Assert.Equal(mine, s.Calls.TableFor("Влад", theirs));
        Assert.Equal(mine, s.Calls.TableFor("Влад", mine));
        Assert.Equal(theirs, s.Calls.TableFor("петро"));
        Assert.Null(s.Calls.TableFor("Оля"));
        Assert.Equal(mine, Assert.Single(s.Calls.Command("Влад", "Оля", theirs).Out!.OfType<Invite>()).RoomId);
    }

    [Theory]
    [InlineData("/клич Оля")]
    [InlineData("/клич @Оля")]
    [InlineData("/поклич оля")]
    [InlineData("/invite @оля")]
    [InlineData("/КЛИЧ Оля")]
    public void Klych_through_the_chat_command_is_the_same_call(string text)
    {
        var s = new Setup("Влад", "Оля");
        s.Table("t-party", "Влад");

        var r = ChatCommands.Run(text, null, who => s.Calls.Command("Влад", who));

        Assert.Null(r.Error);
        Assert.Equal("📣 Заклик у тестову компанію полетів: Оля", r.Text);
        Assert.Equal("Оля", Assert.Single(r.Out!.OfType<Invite>()).To);
    }

    [Fact]
    public void Klych_with_a_dot_works_from_the_ukrainian_layout()
    {
        var s = new Setup("Влад", "Оля");
        s.Table("t-party", "Влад");

        var r = ChatCommands.Run(ChatCommands.FromDot(".клич Оля")!, null, who => s.Calls.Command("Влад", who));

        Assert.Equal("note", r.Kind);
        Assert.Single(r.Out!.OfType<Invite>());
    }
}
