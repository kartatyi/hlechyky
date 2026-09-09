using System.Text;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Конкурс реклами. Кімнати тут нема, тож і RoomHarness ні до чого: сервіс збирається на тимчасовій базі
/// разом зі справжньою економікою (EconomyRig), а ffmpeg, Дядько Глек і ефір підмінені заглушками —
/// перевіряти треба правила й виплати, а не те, як браузер пише webm.
/// </summary>
public class AdContestTests
{
    // =============================================================================================
    // Заглушки
    // =============================================================================================

    /// <summary>Голосове, яке «вже готове»: id видається по порядку, тривалість задає тест.</summary>
    sealed class FakeVoice : IVoiceSaver
    {
        int _n;
        public bool Enabled { get; set; } = true;
        public long MaxUploadBytes => 10 * 1024 * 1024;
        /// <summary>Скільки секунд «записав» браузер наступного разу.</summary>
        public int Seconds { get; set; } = 18;
        public HashSet<string> Files { get; } = new(StringComparer.Ordinal);
        public List<string> Deleted { get; } = new();

        public Task<(TrackInfo Track, string FilePath)> SaveAsync(Stream body, string nick, CancellationToken ct)
        {
            var id = "voice-" + (++_n).ToString("D6");
            Files.Add(id);
            return Task.FromResult((new TrackInfo(id, "Голосове", nick, Seconds, null, $"/api/voice/{id}.mp3", null), id + ".mp3"));
        }

        public string? FilePath(string id) => Files.Contains(id) ? id + ".mp3" : null;

        public void Delete(string id)
        {
            Deleted.Add(id);
            Files.Remove(id);
        }
    }

    /// <summary>Дядько Глек, який або мовчить (null), або каже рівно те, що поклав тест.</summary>
    sealed class FakeWriter : IAdScriptWriter
    {
        public string? Text { get; set; }
        public int Calls { get; private set; }

        public Task<string?> WriteAsync(string instruction, int maxChars, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Text);
        }
    }

    /// <summary>Ефір, який лише запам'ятовує, що йому дали.</summary>
    sealed class FakeAir : IAdAir
    {
        public List<TrackInfo> Played { get; } = new();
        /// <summary>Ефір відмовив («уже в черзі») — джингл має спробувати ще раз пізніше.</summary>
        public bool Refuse { get; set; }

        public (bool Ok, string Message) AddVoice(TrackInfo track, string filePath, string nick)
        {
            if (Refuse) return (false, "Уже в черзі");
            Played.Add(track);
            return (true, "Голосове в черзі");
        }
    }

    sealed class AdRig : IDisposable
    {
        public EconomyRig Eco { get; } = new();
        public FakeVoice Voice { get; } = new();
        public FakeWriter Writer { get; } = new();
        public FakeAir Air { get; } = new();
        public AdOptions Options { get; } = new();
        public AdContestStore Store { get; }
        public AdContest Ads { get; }
        public AdJingle Jingle { get; }

        public FakeClock Clock => Eco.Clock;
        public Presence Presence => Eco.Presence;

        public AdRig()
        {
            Store = new AdContestStore(Eco.Db);
            var opts = new FixedOptions<AdOptions>(Options);
            Ads = new AdContest(Store, Clock, Eco.Events, Eco.Outbox, Writer, Voice, opts, NullLogger<AdContest>.Instance);
            Jingle = new AdJingle(Ads, Air, Voice, Presence, Clock, opts, NullLogger<AdJingle>.Instance);
            Presence.Set("c1", "Оля");     // типово хтось на сайті є; тест, якому треба порожньо, чистить сам
        }

        /// <summary>Відкрити конкурс і віддати його id.</summary>
        public long Open()
        {
            var (ok, message) = Ads.OpenAsync().GetAwaiter().GetResult();
            Assert.True(ok, message);
            return Store.Active()!.Id;
        }

        /// <summary>Записати рекламу від імені ніка.</summary>
        public (bool Ok, string Message) Enter(long id, string nick, int seconds = 18)
        {
            Voice.Seconds = seconds;
            return Ads.EnterAsync(id, nick, new MemoryStream(Encoding.UTF8.GetBytes("webm")), default).GetAwaiter().GetResult();
        }

        public long EntryOf(long contest, string nick) => Store.Entry(contest, EconomyStore.Key(nick))!.Id;

        /// <summary>Трек, який заграв в ефірі, — джингл рахує саме такі події.</summary>
        public void TrackOnAir(string id = "yt-1") =>
            Jingle.OnTrackStarted(new TrackInfo(id, "Пісня", "Хтось", 180, null, "https://x", null));

        public JsonElement Snapshot(string nick) => Views.Json(Ads.Snapshot(nick));

        public void Dispose() => Eco.Dispose();
    }

    /// <summary>Понеділок, 12:30 за Києвом (у вересні Київ — UTC+3).</summary>
    static readonly DateTimeOffset MondayNoon = new(2026, 9, 14, 9, 30, 0, TimeSpan.Zero);

    // =============================================================================================
    // Сценарій
    // =============================================================================================

    [Fact]
    public void A_silent_dj_leaves_the_contest_with_a_built_in_script()
    {
        using var r = new AdRig();
        r.Writer.Text = null;

        r.Open();

        Assert.Equal(1, r.Writer.Calls);
        Assert.Contains(r.Store.Active()!.Script, AdContest.Templates);
    }

    [Fact]
    public void The_built_in_script_is_the_same_for_the_same_day_and_changes_with_the_day()
    {
        using var a = new AdRig();
        using var b = new AdRig();
        using var c = new AdRig();
        c.Clock.Advance(TimeSpan.FromDays(1));

        a.Open();
        b.Open();
        c.Open();

        Assert.Equal(a.Store.Active()!.Script, b.Store.Active()!.Script);
        Assert.NotEqual(a.Store.Active()!.Script, c.Store.Active()!.Script);
    }

    [Fact]
    public void A_script_written_by_the_dj_is_taken_as_it_is()
    {
        using var r = new AdRig();
        r.Writer.Text = "  Глек гуде, вода співає, а ти стоїш і слухаєш, як минає літо.  ";

        r.Open();

        Assert.Equal("Глек гуде, вода співає, а ти стоїш і слухаєш, як минає літо.", r.Store.Active()!.Script);
    }

    [Fact]
    public async Task A_second_contest_cannot_start_while_the_first_one_is_running()
    {
        using var r = new AdRig();
        r.Open();

        var (ok, message) = await r.Ads.OpenAsync();

        Assert.False(ok);
        Assert.Equal("Конкурс уже триває — спершу закрий той", message);
    }

    // =============================================================================================
    // Участь
    // =============================================================================================

    [Fact]
    public void A_new_recording_replaces_the_old_one_and_the_old_file_goes_away()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Оля");
        var first = r.Store.Entry(id, "оля")!.TrackId;

        var (ok, message) = r.Enter(id, "Оля");

        Assert.True(ok);
        Assert.Equal("Перезаписав — стара версія пішла в небуття", message);
        Assert.Single(r.Store.Entries(id));
        Assert.Equal([first], r.Voice.Deleted);
        Assert.NotEqual(first, r.Store.Entries(id)[0].TrackId);
    }

    [Fact]
    public void A_recording_longer_than_the_limit_is_refused_and_its_file_is_deleted()
    {
        using var r = new AdRig();
        var id = r.Open();

        var (ok, message) = r.Enter(id, "Оля", seconds: 41);

        Assert.False(ok);
        Assert.Equal("Задовга реклама: 41 с, а треба до 30", message);
        Assert.Empty(r.Store.Entries(id));
        Assert.Single(r.Voice.Deleted);
    }

    [Fact]
    public void A_recording_a_little_over_the_limit_still_gets_in()
    {
        using var r = new AdRig();
        var id = r.Open();

        // браузер спиняє мікрофон не миттєво, тож 33 с — це ще «тридцять», а не порушення
        Assert.True(r.Enter(id, "Оля", seconds: 33).Ok);
        Assert.Single(r.Store.Entries(id));
    }

    [Fact]
    public void Taking_your_recording_back_removes_it_with_its_votes_and_its_file()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Оля");
        r.Enter(id, "Петро");
        var olya = r.EntryOf(id, "Оля");
        r.Ads.Vote(id, "Петро", olya);
        var track = r.Store.Entry(id, "оля")!.TrackId;

        var (ok, _) = r.Ads.DropEntry(id, "Оля");

        Assert.True(ok);
        Assert.Single(r.Store.Entries(id));
        Assert.Equal(0, r.Store.Entries(id)[0].Votes);
        Assert.Contains(track, r.Voice.Deleted);
        Assert.Equal("Ти ще нічого не записував", r.Ads.DropEntry(id, "Оля").Message);
    }

    [Fact]
    public void A_guest_without_a_nick_neither_records_nor_votes()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Оля");
        var olya = r.EntryOf(id, "Оля");

        Assert.Equal("Спершу скажи, як тебе кликати", r.Enter(id, "гість").Message);
        Assert.Equal("Спершу скажи, як тебе кликати", r.Ads.Vote(id, "гість", olya).Message);
        Assert.Single(r.Store.Entries(id));
    }

    // =============================================================================================
    // Голоси
    // =============================================================================================

    [Fact]
    public void One_vote_per_nick_and_changing_it_moves_the_vote_instead_of_adding_one()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Оля");
        r.Enter(id, "Петро");
        r.Enter(id, "Маруся");
        var olya = r.EntryOf(id, "Оля");
        var petro = r.EntryOf(id, "Петро");

        Assert.True(r.Ads.Vote(id, "Маруся", olya).Ok);
        Assert.True(r.Ads.Vote(id, "Маруся", petro).Ok);

        var entries = r.Store.Entries(id);
        Assert.Equal(0, entries.First(e => e.Id == olya).Votes);
        Assert.Equal(1, entries.First(e => e.Id == petro).Votes);
        Assert.Equal(petro, r.Store.VoteOf(id, "маруся"));
    }

    [Fact]
    public void You_cannot_vote_for_yourself()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Оля");

        var (ok, message) = r.Ads.Vote(id, "оля", r.EntryOf(id, "Оля"));

        Assert.False(ok);
        Assert.Equal("За себе голосувати не можна", message);
        Assert.Equal(0, r.Store.Entries(id)[0].Votes);
    }

    [Fact]
    public void Voting_in_a_closed_contest_and_for_a_stranger_entry_is_refused()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Оля");
        var olya = r.EntryOf(id, "Оля");

        Assert.Equal("Такого запису нема", r.Ads.Vote(id, "Петро", olya + 999).Message);
        r.Ads.Vote(id, "Петро", olya);
        r.Ads.Close(id);
        Assert.Equal("Цей конкурс уже закрито", r.Ads.Vote(id, "Маруся", olya).Message);
        Assert.Equal("Цей конкурс уже закрито", r.Enter(id, "Маруся").Message);
    }

    // =============================================================================================
    // Закриття і виплати
    // =============================================================================================

    [Fact]
    public void The_entry_with_the_most_votes_wins()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Оля");
        r.Enter(id, "Петро");
        r.Ads.Vote(id, "Маруся", r.EntryOf(id, "Петро"));
        r.Ads.Vote(id, "Іван", r.EntryOf(id, "Петро"));
        r.Ads.Vote(id, "Ганна", r.EntryOf(id, "Оля"));

        var (ok, message) = r.Ads.Close(id);

        Assert.True(ok);
        Assert.Equal("Переміг Петро", message);
        Assert.Equal("петро", r.Store.Get(id)!.WinnerNickKey);
        Assert.Contains(r.Eco.Outbox.Of<Journal>(), j => j.Text.Contains("переміг Петро — 2 голоси"));
    }

    [Fact]
    public void On_a_tie_the_earlier_recording_wins()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Оля");
        r.Enter(id, "Петро");
        r.Ads.Vote(id, "Маруся", r.EntryOf(id, "Оля"));
        r.Ads.Vote(id, "Іван", r.EntryOf(id, "Петро"));

        r.Ads.Close(id);

        Assert.Equal("оля", r.Store.Get(id)!.WinnerNickKey);
    }

    [Fact]
    public void Nobody_voted_means_nobody_won_but_the_recordings_are_still_paid()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Оля");

        var (ok, _) = r.Ads.Close(id);

        Assert.True(ok);
        Assert.Null(r.Store.Get(id)!.WinnerNickKey);
        Assert.Null(r.Ads.Winner());
        Assert.Equal(3, r.Eco.Paid("Оля", "ad:entry"));
        Assert.Contains(r.Eco.Outbox.Of<Journal>(), j => j.Text.Contains("ніхто не проголосував"));
    }

    [Fact]
    public void Closing_pays_the_winner_the_entrants_and_the_voters()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Оля");
        r.Enter(id, "Петро");
        r.Ads.Vote(id, "Маруся", r.EntryOf(id, "Петро"));

        r.Ads.Close(id);

        Assert.Equal(25, r.Eco.Paid("Петро", "ad:winner"));
        Assert.Equal(3, r.Eco.Paid("Петро", "ad:entry"));
        Assert.Equal(3, r.Eco.Paid("Оля", "ad:entry"));
        Assert.Equal(1, r.Eco.Paid("Маруся", "ad:vote"));
        Assert.Equal(0, r.Eco.Paid("Оля", "ad:winner"));
        // ачівку «Голос села» платформа вішає сама, побачивши причину ad:winner
        Assert.Contains(r.Eco.Outbox.Of<AchievementUnlocked>(), a => a.Key == "ad-winner" && a.Nick == "Петро");
    }

    [Fact]
    public void Payouts_carry_the_refs_the_economy_expects_so_a_second_close_pays_nothing()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Оля");
        r.Ads.Vote(id, "Петро", r.EntryOf(id, "Оля"));
        r.Ads.Close(id);

        // Ref будує сама економіка з причини ad:*; повтор із тим самим ключем — Duplicate, а не друга виплата.
        Assert.Equal(GrantResult.Duplicate, r.Eco.Economy.Grant("Оля", 1, "ad:winner", $"ad:{id}:winner:оля"));
        Assert.Equal(GrantResult.Duplicate, r.Eco.Economy.Grant("Оля", 1, "ad:entry", $"ad:{id}:entry:оля"));
        Assert.Equal(GrantResult.Duplicate, r.Eco.Economy.Grant("Петро", 1, "ad:vote", $"ad:{id}:vote:петро"));

        var (ok, message) = r.Ads.Close(id);

        Assert.False(ok);
        Assert.Equal("Конкурс уже закрито", message);
        Assert.Equal(25, r.Eco.Paid("Оля", "ad:winner"));
        Assert.Equal(1, r.Eco.Paid("Петро", "ad:vote"));
    }

    // =============================================================================================
    // Тікер: понеділок і дотерміновані конкурси
    // =============================================================================================

    [Fact]
    public async Task A_contest_opens_by_itself_on_monday_noon_and_not_twice_the_same_day()
    {
        using var r = new AdRig();
        r.Clock.UtcNow = MondayNoon.AddHours(-1);            // понеділок, 11:30 за Києвом — ще рано

        await r.Ads.TickAsync();
        Assert.Null(r.Store.Active());

        r.Clock.UtcNow = MondayNoon;
        await r.Ads.TickAsync();
        var id = r.Store.Active()!.Id;

        // адмін закрив його по обіді — другий за той самий день тікер не відкриває
        r.Ads.Close(id);
        r.Clock.Advance(TimeSpan.FromHours(3));
        await r.Ads.TickAsync();
        Assert.Null(r.Store.Active());

        // а наступного понеділка — знову
        r.Clock.UtcNow = MondayNoon.AddDays(7);
        await r.Ads.TickAsync();
        Assert.NotNull(r.Store.Active());
        Assert.NotEqual(id, r.Store.Active()!.Id);
    }

    [Fact]
    public async Task On_any_other_day_the_ticker_opens_nothing()
    {
        using var r = new AdRig();
        r.Clock.UtcNow = MondayNoon.AddDays(1);

        await r.Ads.TickAsync();

        Assert.Null(r.Store.Active());
    }

    [Fact]
    public async Task When_the_time_is_up_the_ticker_closes_the_contest_itself()
    {
        using var r = new AdRig();
        r.Clock.UtcNow = MondayNoon;
        var id = r.Open();
        r.Enter(id, "Оля");
        r.Ads.Vote(id, "Петро", r.EntryOf(id, "Оля"));

        r.Clock.Advance(TimeSpan.FromDays(2));
        await r.Ads.TickAsync();
        Assert.False(r.Store.Get(id)!.Closed);

        r.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        await r.Ads.TickAsync();

        Assert.True(r.Store.Get(id)!.Closed);
        Assert.Equal(25, r.Eco.Paid("Оля", "ad:winner"));
    }

    // =============================================================================================
    // Ефір
    // =============================================================================================

    /// <summary>Конкурс із переможцем «Оля»: далі перевіряємо тільки те, коли її реклама йде в ефір.</summary>
    static long WinnerReady(AdRig r)
    {
        var id = r.Open();
        r.Enter(id, "Оля");
        r.Ads.Vote(id, "Петро", r.EntryOf(id, "Оля"));
        r.Ads.Close(id);
        return id;
    }

    [Fact]
    public void The_winning_ad_goes_on_air_after_six_tracks_and_not_before()
    {
        using var r = new AdRig();
        WinnerReady(r);

        for (var i = 0; i < 5; i++) r.TrackOnAir();
        Assert.Empty(r.Air.Played);

        r.TrackOnAir();

        var ad = Assert.Single(r.Air.Played);
        Assert.Equal("Реклама глека", ad.Title);
        Assert.Equal("Оля", ad.Artist);
    }

    [Fact]
    public void After_the_ad_it_waits_both_six_tracks_and_twenty_five_minutes()
    {
        using var r = new AdRig();
        WinnerReady(r);
        for (var i = 0; i < 6; i++) r.TrackOnAir();
        var ad = r.Air.Played[0];
        r.Jingle.OnTrackStarted(ad);                       // реклама заграла — відлік почався звідси

        for (var i = 0; i < 6; i++) r.TrackOnAir();        // треків уже досить…
        Assert.Single(r.Air.Played);                       // …а хвилин ще ні

        r.Clock.Advance(TimeSpan.FromMinutes(26));
        r.TrackOnAir();

        Assert.Equal(2, r.Air.Played.Count);
    }

    [Fact]
    public void The_ad_itself_does_not_count_as_one_of_the_six_tracks()
    {
        using var r = new AdRig();
        WinnerReady(r);
        var winner = r.Ads.Winner()!;

        for (var i = 0; i < 3; i++) r.TrackOnAir();
        r.Jingle.OnTrackStarted(new TrackInfo(winner.TrackId, "Реклама глека", "Оля", 18, null, "/x", null));
        for (var i = 0; i < 3; i++) r.TrackOnAir();

        Assert.Equal(3, r.Jingle.Since);
        Assert.Empty(r.Air.Played);
    }

    [Fact]
    public void With_nobody_on_the_site_the_ad_waits_for_the_first_listener()
    {
        using var r = new AdRig();
        WinnerReady(r);
        r.Presence.Remove("c1");

        for (var i = 0; i < 10; i++) r.TrackOnAir();
        Assert.Empty(r.Air.Played);

        r.Presence.Set("c2", "Петро");
        r.TrackOnAir();

        Assert.Single(r.Air.Played);
    }

    [Fact]
    public void Ad_Jingle_false_leaves_the_air_alone()
    {
        using var r = new AdRig();
        r.Options.Jingle = false;
        WinnerReady(r);

        for (var i = 0; i < 20; i++) r.TrackOnAir();

        Assert.Empty(r.Air.Played);
    }

    [Fact]
    public void A_winner_whose_file_vanished_from_the_cache_is_simply_not_played()
    {
        using var r = new AdRig();
        WinnerReady(r);
        r.Voice.Files.Clear();                             // кеш почистили — mp3 більше нема

        for (var i = 0; i < 10; i++) r.TrackOnAir();

        Assert.Empty(r.Air.Played);
    }

    [Fact]
    public void A_refused_queue_is_retried_on_the_next_track()
    {
        using var r = new AdRig();
        WinnerReady(r);
        r.Air.Refuse = true;

        for (var i = 0; i < 6; i++) r.TrackOnAir();
        Assert.Empty(r.Air.Played);

        r.Air.Refuse = false;
        r.TrackOnAir();

        Assert.Single(r.Air.Played);
    }

    // =============================================================================================
    // Що бачить браузер
    // =============================================================================================

    [Fact]
    public void The_snapshot_has_the_shape_the_panel_draws()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Оля");
        r.Enter(id, "Петро");
        r.Ads.Vote(id, "Петро", r.EntryOf(id, "Оля"));

        var v = r.Snapshot("Петро");
        var active = v.GetProperty("active");
        var entries = active.GetProperty("entries").EnumerateArray().ToList();
        var mine = entries.Single(e => e.GetProperty("mine").GetBoolean());

        Assert.Equal(id, active.GetProperty("id").GetInt64());
        Assert.False(string.IsNullOrWhiteSpace(active.GetProperty("script").GetString()));
        Assert.Equal(30, active.GetProperty("maxSeconds").GetInt32());
        Assert.Equal(mine.GetProperty("id").GetInt64(), active.GetProperty("myEntry").GetInt64());
        Assert.Equal("Петро", mine.GetProperty("nick").GetString());
        Assert.Equal(18, mine.GetProperty("seconds").GetInt32());
        Assert.Equal(1, entries.Single(e => e.GetProperty("nick").GetString() == "Оля").GetProperty("votes").GetInt32());
        Assert.Equal(entries.Single(e => e.GetProperty("nick").GetString() == "Оля").GetProperty("id").GetInt64(),
            active.GetProperty("myVote").GetInt64());
        Assert.Empty(v.GetProperty("past").EnumerateArray());
    }

    [Fact]
    public void A_closed_contest_leaves_no_active_one_but_shows_up_among_the_past_winners()
    {
        using var r = new AdRig();
        var id = WinnerReady(r);

        var v = r.Snapshot("Петро");
        var past = Assert.Single(v.GetProperty("past").EnumerateArray().ToList());

        Assert.Equal(JsonValueKind.Null, v.GetProperty("active").ValueKind);
        Assert.Equal(id, past.GetProperty("id").GetInt64());
        Assert.Equal("Оля", past.GetProperty("winner").GetString());
        Assert.Equal(1, past.GetProperty("votes").GetInt32());
        Assert.StartsWith("voice-", past.GetProperty("trackId").GetString());
    }

    [Fact]
    public async Task A_disabled_contest_neither_opens_nor_ticks()
    {
        using var r = new AdRig();
        r.Options.Enabled = false;
        r.Clock.UtcNow = MondayNoon;

        var (ok, message) = await r.Ads.OpenAsync();
        await r.Ads.TickAsync();

        Assert.False(ok);
        Assert.Equal("Конкурс реклами вимкнено", message);
        Assert.Null(r.Store.Active());
    }
}
