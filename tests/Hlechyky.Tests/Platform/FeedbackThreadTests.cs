using Hlechyky.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Переписка в записках «💡 Розробнику»: хто куди може писати, скільки й як часто, що з того непрочитане для кожного
/// боку, і як стара одна відповідь розробника з проду стає першим повідомленням.
/// </summary>
public sealed class FeedbackThreadTests : IDisposable
{
    readonly TempDb _db = new();
    readonly FakeClock _clock = new();
    readonly FakeWire _wire = new();
    readonly Feedback _fb;

    public FeedbackThreadTests() => _fb = new Feedback(new FeedbackStore(_db.Db), _clock, _wire);

    public void Dispose() => _db.Dispose();

    /// <summary>Що полетіло б хабом: кому яке число.</summary>
    sealed class FakeWire : IFeedbackWire
    {
        public readonly List<(string Nick, int Unread)> Authors = [];
        public readonly List<FeedbackCount> Devs = [];
        public void Author(string nick, int unread) => Authors.Add((nick, unread));
        public void Dev(FeedbackCount count) => Devs.Add(count);
    }

    long Note(string nick = "Оля", string text = "Додайте темну тему") => _fb.Submit(nick, "idea", text, null, null, null).Id;

    IReadOnlyList<FeedbackMsg> Msgs(long id) => _fb.One(id)!.Msgs;

    // ---------- хто куди пише ----------

    [Fact]
    public void Author_and_developer_write_in_turns_as_long_as_they_like()
    {
        var id = Note();
        for (var i = 0; i < 4; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            Assert.True(_fb.Say(id, "Влад", true, "Відповідь " + i).Ok);
            _clock.Advance(TimeSpan.FromMinutes(1));
            Assert.True(_fb.Say(id, "оля", false, "Уточнення " + i).Ok);   // нік без урахування регістру
        }
        var msgs = Msgs(id);
        Assert.Equal(8, msgs.Count);
        Assert.Equal([true, false, true, false, true, false, true, false], msgs.Select(m => m.Dev));
        Assert.Equal("Відповідь 0", msgs[0].Text);
        Assert.Equal("Уточнення 3", msgs[^1].Text);
        Assert.All(msgs, m => Assert.Equal("text", m.Kind));
        Assert.True(msgs.Zip(msgs.Skip(1)).All(p => p.First.Id < p.Second.Id && p.First.At < p.Second.At));
    }

    [Fact]
    public void Someone_else_cannot_write_in_my_note()
    {
        var id = Note();
        var r = _fb.Say(id, "Петро", false, "А я теж хочу");
        Assert.False(r.Ok);
        Assert.Contains("не твоя", r.Message);
        Assert.Empty(Msgs(id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("гість")]
    [InlineData(" ГІСТЬ ")]
    public void Nameless_guest_cannot_write_anywhere(string nick)
    {
        var id = Note();
        var r = _fb.Say(id, nick, false, "Привіт");
        Assert.False(r.Ok);
        Assert.Contains("назвись", r.Message);
    }

    [Fact]
    public void Named_guest_answers_in_own_note()
    {
        var id = Note("гість Вася");
        Assert.True(_fb.Say(id, "гість Вася", false, "так").Ok);
        Assert.False(_fb.Say(id, "Вася", false, "так, це я").Ok);   // зареєстрований «Вася» — вже інша людина
    }

    [Fact]
    public void Developer_writes_in_any_note_and_missing_note_is_refused()
    {
        var a = Note("Оля");
        var b = Note("Петро", "Скіп не працює");
        Assert.True(_fb.Say(a, "Влад", true, "Глянемо").Ok);
        Assert.True(_fb.Say(b, "Влад", true, "Глянемо").Ok);
        Assert.False(_fb.Say(b + 100, "Влад", true, "Глянемо").Ok);
        Assert.False(_fb.Say(b + 100, "Оля", false, "Глянемо").Ok);
    }

    // ---------- що можна написати ----------

    [Fact]
    public void A_single_letter_is_an_answer_but_empty_and_too_long_are_not()
    {
        var id = Note();
        Assert.True(_fb.Say(id, "Оля", false, "т").Ok);
        Assert.False(_fb.Say(id, "Оля", false, "").Ok);
        Assert.False(_fb.Say(id, "Оля", false, "   \n  ").Ok);
        Assert.False(_fb.Say(id, "Оля", false, null).Ok);
        var tooLong = _fb.Say(id, "Оля", false, new string('а', Feedback.MaxMsg + 1));
        Assert.False(tooLong.Ok);
        Assert.Contains(Feedback.MaxMsg.ToString(), tooLong.Message);
        Assert.True(_fb.Say(id, "Оля", false, new string('б', Feedback.MaxMsg)).Ok);
        Assert.Equal(2, Msgs(id).Count);
    }

    [Fact]
    public void Control_characters_go_but_line_breaks_stay()
    {
        var id = Note();
        Assert.True(_fb.Say(id, "Оля", false, "  так:\nще й оце\u0007\u0000  ").Ok);
        Assert.Equal("так:\nще й оце", Msgs(id)[0].Text);
    }

    [Fact]
    public void Same_text_twice_in_half_a_minute_is_a_double_enter()
    {
        var a = Note();
        var b = Note("Оля", "Ще одна думка");
        Assert.True(_fb.Say(a, "Оля", false, "так").Ok);
        var again = _fb.Say(a, "Оля", false, "так");
        Assert.False(again.Ok);
        Assert.Contains("вже тяпнуто", again.Message);
        Assert.True(_fb.Say(b, "Оля", false, "так").Ok);          // в іншу записку — окрема відповідь
        Assert.True(_fb.Say(a, "Влад", true, "так").Ok);          // розробник — окремий бік розмови
        Assert.False(_fb.Say(a, "Влад", true, "так").Ok);         // подвійний Enter у розробника теж ловимо

        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.True(_fb.Say(a, "Оля", false, "так").Ok);
        Assert.Equal(3, Msgs(a).Count);
    }

    [Fact]
    public void Thirty_messages_an_hour_then_wait_but_developer_is_not_limited()
    {
        var a = Note();
        var b = Note("Оля", "Ще одна думка");
        for (var i = 0; i < Feedback.MsgPerHour; i++)
            Assert.True(_fb.Say(i % 2 == 0 ? a : b, "Оля", false, "Думка " + i).Ok);   // ліміт — на ніка, по всіх записках
        var more = _fb.Say(a, "Оля", false, "Ще думка");
        Assert.False(more.Ok);
        Assert.Contains("годину", more.Message);

        var p = Note("Петро", "Скіп не працює");
        Assert.True(_fb.Say(p, "Петро", false, "А ще й черга").Ok);   // інший нік — свій ліміт

        for (var i = 0; i < Feedback.MsgPerHour + 10; i++)
            Assert.True(_fb.Say(a, "Влад", true, "Відповідь " + i).Ok);

        _clock.Advance(TimeSpan.FromMinutes(61));
        Assert.True(_fb.Say(a, "Оля", false, "Ще думка").Ok);
    }

    // ---------- стан ----------

    [Theory]
    [InlineData("done")]
    [InlineData("nope")]
    [InlineData("planned")]
    public void Author_answer_does_not_touch_the_status(string status)
    {
        var id = Note();
        Assert.True(_fb.Update(id, status, null).Ok);
        Assert.True(_fb.Say(id, "Оля", false, "Дякую!").Ok);
        Assert.Equal(status, _fb.Mine("Оля")[0].Status);
    }

    [Fact]
    public void First_developer_answer_marks_a_new_note_seen_but_keeps_other_states()
    {
        var a = Note();
        Assert.True(_fb.Say(a, "Оля", false, "Ще й шрифт більший").Ok);   // відповідь людини — не привід
        Assert.Equal("new", _fb.One(a)!.Item.Status);
        Assert.True(_fb.Say(a, "Влад", true, "Глянемо").Ok);
        Assert.Equal("seen", _fb.One(a)!.Item.Status);

        var b = Note("Оля", "Ще одна думка");
        _fb.Update(b, "planned", null);
        Assert.True(_fb.Say(b, "Влад", true, "Уже роблю").Ok);
        Assert.Equal("planned", _fb.One(b)!.Item.Status);
    }

    [Fact]
    public void Status_the_author_should_know_about_goes_into_the_thread()
    {
        var id = Note();
        _fb.Update(id, "seen", null);                       // «переглянуто» — без рядка
        Assert.Empty(Msgs(id));
        Assert.Equal(0, _fb.Unread("Оля"));

        _clock.Advance(TimeSpan.FromMinutes(1));
        _fb.Update(id, "done", null, "Влад");
        var m = Assert.Single(Msgs(id));
        Assert.Equal(("status", "done", true), (m.Kind, m.Text, m.Dev));
        Assert.Equal(1, _fb.Unread("Оля"));

        _fb.Update(id, "done", null);                       // той самий стан — нічого нового
        Assert.Single(Msgs(id));
        Assert.False(_fb.Update(id, "maybe", null).Ok);
        Assert.Single(Msgs(id));
    }

    // ---------- непрочитане ----------

    [Fact]
    public void Developer_answer_is_unread_for_the_author_until_she_opens_her_notes()
    {
        var a = Note();
        var b = Note("Оля", "Ще одна думка");
        Assert.Equal(0, _fb.Unread("Оля"));

        _fb.Say(a, "Влад", true, "Глянемо");
        _fb.Say(b, "Влад", true, "І це глянемо");
        _fb.Say(b, "Влад", true, "Уже глянув");
        Assert.Equal(2, _fb.Unread("оля"));                 // лічильник — записок, а не повідомлень
        Assert.Equal(0, _fb.Unread("Петро"));
        Assert.True(_fb.Mine("Оля").All(x => x.AuthorRead == 0));

        Assert.Equal(1, _fb.ReadMine("Оля", a));            // відкрила одну записку
        Assert.Equal(0, _fb.ReadMine("Оля", null));         // відкрила всі
        Assert.Equal(0, _fb.Unread("Оля"));

        _clock.Advance(TimeSpan.FromMinutes(1));
        _fb.Say(a, "Влад", true, "Зроблено");
        Assert.Equal(1, _fb.Unread("Оля"));
    }

    [Fact]
    public void Own_messages_are_never_unread_for_yourself()
    {
        var id = Note();
        _fb.Say(id, "Оля", false, "Ще й шрифт більший");
        Assert.Equal(0, _fb.Unread("Оля"));
        _fb.Say(id, "Влад", true, "Глянемо");
        Assert.Equal(0, _fb.DevCount().Replies);            // розробник відповів — отже, бачив і те, що вище
        Assert.Equal(1, _fb.Unread("Оля"));
        _fb.Say(id, "Оля", false, "Дякую");                 // людина відписала — розробникове теж прочитане
        Assert.Equal(0, _fb.Unread("Оля"));
    }

    [Fact]
    public void Author_answer_waits_for_the_developer_until_he_sees_it()
    {
        var a = Note();
        var b = Note("Петро", "Скіп не працює");
        Assert.Equal(new FeedbackCount(2, 2, 0), _fb.DevCount());

        _fb.Update(a, "planned", null);
        _fb.Update(b, "seen", null);
        Assert.Equal(new FeedbackCount(0, 0, 0), _fb.DevCount());

        _fb.Say(a, "Оля", false, "А коли?");
        _fb.Say(a, "Оля", false, "І ще шрифт");
        Assert.Equal(new FeedbackCount(1, 0, 1), _fb.DevCount());   // записок, а не повідомлень

        Assert.Equal(new FeedbackCount(0, 0, 0), _fb.ReadAsDev([a]));
        Assert.Equal(0, _fb.NewCount());

        // нова записка з відповіддю автора — одна записка, а не дві
        var c = Note("Петро", "Ще одне");
        _fb.Say(c, "Петро", false, "Уточню: на телефоні");
        Assert.Equal(new FeedbackCount(1, 1, 1), _fb.DevCount());
    }

    [Fact]
    public void Developer_reads_only_the_notes_he_opened()
    {
        var a = Note();
        var b = Note("Петро", "Скіп не працює");
        _fb.Say(a, "Оля", false, "Уточню");
        _fb.Say(b, "Петро", false, "Уточню");
        Assert.Equal(2, _fb.DevCount().Replies);
        Assert.Equal(1, _fb.ReadAsDev([b, b, 999]).Replies);
        Assert.Equal(1, _fb.ReadAsDev([]).Replies);
    }

    [Fact]
    public void Admin_list_keeps_a_note_with_an_unread_answer_whatever_the_filter()
    {
        var a = Note();
        var b = Note("Петро", "Скіп не працює");
        _fb.Update(a, "done", null);
        _fb.Say(a, "Оля", false, "Дякую, але ще одне");

        var (fresh, counts) = _fb.List("new");
        Assert.Equal([b, a], fresh.Select(x => x.Id));      // «зроблена» записка з відповіддю — теж тут
        Assert.Equal(1, counts["new"]);

        _fb.ReadAsDev([a]);
        Assert.Equal([b], _fb.List("new").Items.Select(x => x.Id));
    }

    [Fact]
    public void Answered_note_goes_up_in_my_list()
    {
        var old = Note();
        _clock.Advance(TimeSpan.FromMinutes(5));
        var fresh = Note("Оля", "Ще одна думка");
        Assert.Equal([fresh, old], _fb.Mine("Оля").Select(x => x.Id));
        _clock.Advance(TimeSpan.FromMinutes(5));
        _fb.Say(old, "Влад", true, "Глянемо");
        Assert.Equal([old, fresh], _fb.Mine("Оля").Select(x => x.Id));
    }

    [Fact]
    public void Threads_come_together_with_their_notes()
    {
        var a = Note();
        var b = Note("Оля", "Ще одна думка");
        _fb.Say(a, "Влад", true, "Раз");
        _fb.Say(b, "Влад", true, "Два");
        _fb.Say(a, "Оля", false, "Три");
        var threads = _fb.WithMsgs(_fb.Mine("Оля"));
        Assert.Equal(["Раз", "Три"], threads.Single(t => t.Item.Id == a).Msgs.Select(m => m.Text));
        Assert.Equal(["Два"], threads.Single(t => t.Item.Id == b).Msgs.Select(m => m.Text));
        Assert.Empty(_fb.WithMsgs([]));
    }

    // ---------- без F5 ----------

    [Fact]
    public void Both_sides_hear_about_it_at_once()
    {
        var id = Note();
        Assert.Equal(new FeedbackCount(1, 1, 0), _wire.Devs[^1]);   // нова записка — розробнику

        _fb.Say(id, "Влад", true, "Глянемо");
        Assert.Equal(("Оля", 1), _wire.Authors[^1]);
        Assert.Equal(new FeedbackCount(0, 0, 0), _wire.Devs[^1]);   // переглянуто — інші вкладки розробника теж знають

        _fb.Say(id, "Оля", false, "Дякую");
        Assert.Equal(new FeedbackCount(1, 0, 1), _wire.Devs[^1]);

        _wire.Authors.Clear();
        _fb.ReadMine("Оля", null);
        Assert.Empty(_wire.Authors);                        // уже все прочитане (сама ж відписала) — нема що гасити
        _fb.Say(id, "Влад", true, "Будь ласка");
        _fb.ReadMine("Оля", null);
        Assert.Equal(("Оля", 0), _wire.Authors[^1]);        // інші вкладки гасять кружечок

        var devs = _wire.Devs.Count;
        _fb.ReadAsDev([id]);
        Assert.Equal(devs, _wire.Devs.Count);               // нічого не мінялось — нічого й не шлемо

        _fb.Update(id, "done", null);
        Assert.Equal(("Оля", 1), _wire.Authors[^1]);
    }

    [Fact]
    public void Refused_messages_tell_nobody()
    {
        var id = Note();
        _wire.Authors.Clear();
        _wire.Devs.Clear();
        _fb.Say(id, "Петро", false, "Чуже");
        _fb.Say(id, "Оля", false, "");
        Assert.Empty(_wire.Authors);
        Assert.Empty(_wire.Devs);
    }

    [Theory]
    [InlineData("admin", true)]
    [InlineData("member", false)]
    [InlineData(null, false)]
    public async Task Only_the_developer_joins_the_feedback_group_on_connect(string? role, bool joins)
    {
        var http = new DefaultHttpContext();
        if (role is not null) http.Items["role"] = role;
        var groups = new Groups();
        var hub = new NoHub { Groups = groups };
        var called = false;
        var filter = new FeedbackDevGroup(Microsoft.Extensions.Logging.Abstractions.NullLogger<FeedbackDevGroup>.Instance);

        await filter.OnConnectedAsync(new HubLifetimeContext(new Caller(http), new ServiceCollection().BuildServiceProvider(), hub),
            _ => { called = true; return Task.CompletedTask; });

        Assert.True(called);                                // підключення йде далі за будь-якої ролі
        Assert.Equal(joins ? new List<(string, string)> { ("c1", FeedbackDevGroup.Name) } : [], groups.Added);
    }

    [Fact]
    public async Task A_broken_group_does_not_stop_the_connection()
    {
        var http = new DefaultHttpContext();
        http.Items["role"] = "admin";
        var hub = new NoHub { Groups = new Groups { Fail = true } };
        var called = false;
        await new FeedbackDevGroup(Microsoft.Extensions.Logging.Abstractions.NullLogger<FeedbackDevGroup>.Instance)
            .OnConnectedAsync(new HubLifetimeContext(new Caller(http), new ServiceCollection().BuildServiceProvider(), hub),
                _ => { called = true; return Task.CompletedTask; });
        Assert.True(called);
    }

    sealed class NoHub : Hub;

    sealed class Groups : IGroupManager
    {
        public bool Fail;
        public readonly List<(string, string)> Added = [];
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new InvalidOperationException("група впала");
            Added.Add((connectionId, groupName));
            return Task.CompletedTask;
        }
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>З'єднання хабу, за яким стоїть звичайний HTTP-запит (кука адміна вже розібрана в Items["role"]).</summary>
    sealed class Caller(HttpContext http) : HubCallerContext
    {
        readonly FeatureCollection _features = MakeFeatures(http);
        static FeatureCollection MakeFeatures(HttpContext http)
        {
            var f = new FeatureCollection();
            f.Set<IHttpContextFeature>(new HttpFeature { HttpContext = http });
            return f;
        }
        public override string ConnectionId => "c1";
        public override string? UserIdentifier => null;
        public override System.Security.Claims.ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features => _features;
        public override CancellationToken ConnectionAborted => default;
        public override void Abort() { }
    }

    sealed class HttpFeature : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; }
    }

    // ---------- стара база ----------

    [Fact]
    public void Old_reply_becomes_the_first_developer_message_once()
    {
        var id = Note();
        var answered = new DateTimeOffset(2026, 9, 27, 1, 50, 4, TimeSpan.Zero);
        OldReply(id, "а ти шось бачиш тут?", answered);

        var store = new FeedbackStore(_db.Db);              // перший старт після деплою
        var fb = new Feedback(store, _clock);
        var m = Assert.Single(fb.One(id)!.Msgs);
        Assert.Equal(("а ти шось бачиш тут?", true, "text", answered), (m.Text, m.Dev, m.Kind, m.At));
        Assert.Equal(1, fb.Unread("Оля"));                  // 💡 покаже, що тепер можна відписати

        _ = new FeedbackStore(_db.Db);                      // повторний старт
        _ = new FeedbackStore(_db.Db);
        Assert.Single(fb.One(id)!.Msgs);
        Assert.Equal("а ти шось бачиш тут?", fb.One(id)!.Item.Reply);   // старе поле лишається як є

        // та сама відповідь, яку розробник уже написав у переписку сам, — теж не задвоюється
        var other = Note("Петро", "Скіп не працює");
        fb.Say(other, "Влад", true, "Глянемо");
        OldReply(other, "Глянемо", answered);
        _ = new FeedbackStore(_db.Db);
        Assert.Single(fb.One(other)!.Msgs);
    }

    [Fact]
    public void Old_production_database_opens_and_migrates()
    {
        // База з проду до переписки: таблиця feedback без author_read/dev_read, без feedback_msg і без позначки.
        using var old = new TempDbFile();
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = old.Path }.ToString()))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE feedback(
                    id INTEGER PRIMARY KEY AUTOINCREMENT, nick TEXT NOT NULL, nick_key TEXT NOT NULL, kind TEXT NOT NULL,
                    text TEXT NOT NULL, place TEXT, screen TEXT, ua TEXT, status TEXT NOT NULL DEFAULT 'new', reply TEXT,
                    created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
                INSERT INTO feedback(nick, nick_key, kind, text, status, reply, created_at, updated_at) VALUES
                    ('Назар', 'назар', 'idea', 'Додати можливість ставити аватарку', 'seen', 'уже є, в профілі вкладка "лавка"',
                        '2026-09-26T20:33:31.7311109+00:00', '2026-09-27T01:50:04.6745779+00:00'),
                    ('Smaug', 'smaug', 'bug', 'обпал глеків залагує', 'seen', 'а ти шось бачиш тут?',
                        '2026-09-27T01:51:04.5748672+00:00', '2026-09-27T02:36:16.0394385+00:00'),
                    ('Smaug', 'smaug', 'idea', 'я тут щось бачиш але не можеш відповісти', 'new', NULL,
                        '2026-09-27T02:40:19.1322265+00:00', '2026-09-27T02:40:19.1322265+00:00'),
                    ('Smaug', 'smaug', 'bug', 'тре пофіксити розпис пензликом', 'planned', '   ',
                        '2026-09-27T18:34:42.7167279+00:00', '2026-09-27T21:22:16.4011238+00:00');
                """;
            cmd.ExecuteNonQuery();
        }

        var fb = new Feedback(new FeedbackStore(new Db(old.Path)), _clock);
        var all = fb.WithMsgs(fb.List(null).Items).ToDictionary(t => t.Item.Id);
        Assert.Equal(4, all.Count);
        Assert.Equal("уже є, в профілі вкладка \"лавка\"", Assert.Single(all[1].Msgs).Text);
        var smaug = Assert.Single(all[2].Msgs);
        Assert.Equal(("а ти шось бачиш тут?", true), (smaug.Text, smaug.Dev));
        Assert.Equal(DateTimeOffset.Parse("2026-09-27T02:36:16.0394385+00:00"), smaug.At);
        Assert.Empty(all[3].Msgs);
        Assert.Empty(all[4].Msgs);                         // порожня відповідь — не повідомлення
        Assert.True(all[1].Msgs[0].Id < all[2].Msgs[0].Id);
        Assert.Equal(1, fb.Unread("Smaug"));
        Assert.Equal(1, fb.Unread("Назар"));
        Assert.Equal(new FeedbackCount(1, 1, 0), fb.DevCount());

        // і далі живе як нова: Smaug відписує в стару записку
        Assert.True(fb.Say(2, "Smaug", false, "так, бачу").Ok);
        Assert.Equal(0, fb.Unread("Smaug"));
        Assert.Equal(new FeedbackCount(2, 1, 1), fb.DevCount());

        // другий старт на тій самій базі — нічого не задвоєно
        var again = new Feedback(new FeedbackStore(new Db(old.Path)), _clock);
        var after = again.WithMsgs(again.List(null).Items).ToDictionary(t => t.Item.Id, t => t.Msgs.Count);
        Assert.Equal(new Dictionary<long, int> { [1] = 1, [2] = 2, [3] = 0, [4] = 0 }, after);
        Assert.Equal(1, again.Unread("Назар"));
        Assert.Equal(new FeedbackCount(2, 1, 1), again.DevCount());
    }

    void OldReply(long id, string reply, DateTimeOffset at) => _db.Db.Exec(
        "UPDATE feedback SET reply = $r, updated_at = $at WHERE id = $id",
        ("$r", reply), ("$at", at.ToString("O", System.Globalization.CultureInfo.InvariantCulture)), ("$id", id));

    /// <summary>Порожній файл бази, на якому тест сам будує стару схему, а потім відкриває його як <see cref="Db"/>.</summary>
    sealed class TempDbFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hlechyky-old-" + Guid.NewGuid().ToString("N") + ".db");

        public void Dispose()
        {
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path }.ToString()))
                SqliteConnection.ClearPool(c);
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(Path + suffix); } catch (IOException) { /* хай лежить у temp */ }
        }
    }
}
