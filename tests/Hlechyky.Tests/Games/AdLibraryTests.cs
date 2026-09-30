using System.Text;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Реклама в ефірі: бібліотека господаря, колода ротації, джингл, частота і черепки за прослуховування.
/// Кімнати тут нема, тож і RoomHarness ні до чого: сервіси збираються на тимчасовій базі разом зі справжньою
/// економікою (EconomyRig), а ffmpeg і ефір підмінені заглушками — перевіряти треба правила, а не те, як
/// браузер пише webm. Наприкінці — що лишилось від конкурсу реклами (прибрано 26.09.2026): частота в старому
/// рядку бази, форма відповідей, записи гаманця й ачівка.
/// </summary>
public class AdLibraryTests
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
        /// <summary>Скільки секунд триває наступний «залитий» файл.</summary>
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

    /// <summary>Ефір, який лише запам'ятовує, що йому дали.</summary>
    sealed class FakeAir : IAdAir
    {
        public List<TrackInfo> Played { get; } = new();
        /// <summary>Ефір відмовив («уже в черзі») — джингл має спробувати ще раз пізніше.</summary>
        public bool Refuse { get; set; }
        /// <summary>У черзі ще стоїть реклама, що не заграла.</summary>
        public bool Waiting { get; set; }
        public bool AdWaiting() => Waiting;

        public (bool Ok, string Message) AddVoice(TrackInfo track, string filePath, string nick)
        {
            if (Refuse) return (false, "Уже в черзі");
            Played.Add(track);
            return (true, "Голосове в черзі");
        }
    }

    /// <summary>Що «зараз грає» для нагороди за рекламу.</summary>
    sealed class FakeOnAir : IAdOnAir
    {
        public string? TrackId { get; set; }
        public bool SkipPending { get; set; }
        /// <summary>Грає запаска (нове не вантажиться) — у джингла своя частота.</summary>
        public bool Spare { get; set; }
        public (string? TrackId, bool SkipPending) Now() => (TrackId, SkipPending);
        public bool OnSpare() => Spare;
    }

    sealed class AdRig : IDisposable
    {
        public EconomyRig Eco { get; } = new();
        public FakeVoice Voice { get; } = new();
        public FakeAir Air { get; } = new();
        public FakeOnAir OnAir { get; } = new();
        public AdOptions Options { get; } = new();
        public AdLibrary Library { get; }
        public AdListenRewards Rewards { get; }
        public AdJingle Jingle { get; }

        public FakeClock Clock => Eco.Clock;
        public Presence Presence => Eco.Presence;

        /// <param name="before">Що лежало в базі ще до старту сервера (наприклад, рядок ефіру часів конкурсу).</param>
        public AdRig(Action<Db>? before = null)
        {
            before?.Invoke(Eco.Db);
            Library = new AdLibrary(new AdLibraryStore(Eco.Db), Voice, Clock, NullLogger<AdLibrary>.Instance);
            Rewards = new AdListenRewards(Eco.Economy, Presence, OnAir, Clock, new FixedOptions<AdOptions>(Options),
                NullLogger<AdListenRewards>.Instance)
            {
                Delay = _ => new TaskCompletionSource().Task,     // перевірку під кінець реклами тести кличуть самі
            };
            Jingle = Cold();
            Presence.Set("c1", "Оля");     // типово хтось на сайті є; тест, якому треба порожньо, чистить сам
        }

        /// <summary>Джингл на тій самій базі, але з порожньою пам'яттю — як після перезапуску сервера.</summary>
        public AdJingle Cold() => new(Library, new AdLibraryStore(Eco.Db), Rewards, Air, Voice, Presence, Clock,
            new FixedOptions<AdOptions>(Options), NullLogger<AdJingle>.Instance, onAir: OnAir);

        /// <summary>Залити рекламу в бібліотеку — вона одразу в ротації.</summary>
        public AdClip Add(string title, int seconds = 20)
        {
            Voice.Seconds = seconds;
            var (ok, message) = Library.AddAsync(new MemoryStream(Encoding.UTF8.GetBytes("wav")), title, "владік", default)
                .GetAwaiter().GetResult();
            Assert.True(ok, message);
            return Library.All()[^1];
        }

        /// <summary>Трек, який заграв в ефірі, — джингл рахує саме такі події.</summary>
        public void TrackOnAir(string id = "yt-1") =>
            Jingle.OnTrackStarted(new TrackInfo(id, "Пісня", "Хтось", 180, null, "https://x", null));

        /// <summary>Заграла сама реклама — так її бачить джингл.</summary>
        public void AdOnAir(AdClip clip) =>
            Jingle.OnTrackStarted(new TrackInfo(clip.TrackId, AdJingle.AdTitle, clip.Title, clip.Seconds, null, "/x", null));

        public void Dispose() => Eco.Dispose();
    }

    // =============================================================================================
    // Джингл: коли реклама йде в ефір
    // =============================================================================================

    [Fact]
    public void A_clip_goes_on_air_after_six_tracks_and_not_before()
    {
        using var r = new AdRig();
        r.Add("Глекминатор", 18);

        for (var i = 0; i < 5; i++) r.TrackOnAir();
        Assert.Empty(r.Air.Played);

        r.TrackOnAir();

        var ad = Assert.Single(r.Air.Played);
        Assert.Equal(AdJingle.AdTitle, ad.Title);
        Assert.Equal("Глекминатор", ad.Artist);
        Assert.Equal(18, ad.DurationSec);
    }

    [Fact]
    public void While_an_ad_still_waits_in_the_queue_no_second_one_joins_it()
    {
        using var r = new AdRig();
        r.Add("Глекминатор", 18);
        r.Air.Waiting = true;          // попередню господар посунув у кінець — вона ще не грала

        for (var i = 0; i < 12; i++) r.TrackOnAir();
        Assert.Empty(r.Air.Played);

        r.Air.Waiting = false;
        r.TrackOnAir();
        Assert.Single(r.Air.Played);
    }

    [Fact]
    public void After_the_ad_it_waits_both_six_tracks_and_twenty_five_minutes()
    {
        using var r = new AdRig();
        r.Add("Глекминатор");
        for (var i = 0; i < 6; i++) r.TrackOnAir();
        r.Jingle.OnTrackStarted(r.Air.Played[0]);          // реклама заграла — відлік почався звідси

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
        var clip = r.Add("Глекминатор");

        for (var i = 0; i < 3; i++) r.TrackOnAir();
        r.AdOnAir(clip);
        for (var i = 0; i < 3; i++) r.TrackOnAir();

        Assert.Equal(3, r.Jingle.Since);
        Assert.Empty(r.Air.Played);
    }

    [Fact]
    public void An_ad_that_left_the_library_while_queued_still_does_not_count_as_a_track()
    {
        using var r = new AdRig();
        r.Add("Глекминатор");
        r.TrackOnAir();
        r.TrackOnAir();

        // У бібліотеці такої вже нема (видалили, поки стояла в черзі), але грає все одно реклама, а не пісня.
        r.Jingle.OnTrackStarted(new TrackInfo("voice-000777", AdJingle.AdTitle, "Стара", 18, null, "/x", null));

        Assert.Equal(0, r.Jingle.Since);
    }

    [Fact]
    public void With_nobody_on_the_site_the_ad_waits_for_the_first_listener()
    {
        using var r = new AdRig();
        r.Add("Глекминатор");
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
        r.Add("Глекминатор");

        for (var i = 0; i < 20; i++) r.TrackOnAir();

        Assert.Empty(r.Air.Played);
    }

    [Fact]
    public void A_clip_whose_file_vanished_from_the_cache_is_simply_not_played()
    {
        using var r = new AdRig();
        r.Add("Глекминатор");
        r.Voice.Files.Clear();                             // кеш почистили — mp3 більше нема

        for (var i = 0; i < 10; i++) r.TrackOnAir();

        Assert.Empty(r.Air.Played);
    }

    [Fact]
    public void A_refused_queue_is_retried_on_the_next_track()
    {
        using var r = new AdRig();
        r.Add("Глекминатор");
        r.Air.Refuse = true;

        for (var i = 0; i < 6; i++) r.TrackOnAir();
        Assert.Empty(r.Air.Played);

        r.Air.Refuse = false;
        r.TrackOnAir();

        Assert.Single(r.Air.Played);
    }

    [Fact]
    public void With_the_rotation_switched_off_nothing_takes_its_place()
    {
        using var r = new AdRig();
        r.Options.EveryTracks = 1;
        r.Options.MinMinutes = 0;
        r.Add("Глекминатор", 22);

        r.TrackOnAir("yt-1");
        var queued = Assert.Single(r.Air.Played);
        Assert.Equal("Глекминатор", queued.Artist);
        Assert.Equal(AdJingle.AdTitle, queued.Title);

        // колись тут підхоплювала реклама господаря чи переможець конкурсу; тепер порожня ротація — це тиша
        r.Library.SetAll(false);
        for (var i = 2; i < 8; i++) r.TrackOnAir($"yt-{i}");

        Assert.Single(r.Air.Played);
    }

    // =============================================================================================
    // Частота й «в ефір зараз»
    // =============================================================================================

    [Fact]
    public void Frequency_from_the_admin_overrides_the_config_and_survives_a_restart()
    {
        using var r = new AdRig();
        r.Add("Глекминатор");

        Assert.Equal((true, "Реклама — раз на 3 тр."), r.Jingle.SetFrequency(3, 0));
        Assert.Equal((3, 0), r.Cold().Frequency());

        for (var i = 0; i < 3; i++) r.TrackOnAir();
        Assert.Single(r.Air.Played);
        r.Jingle.OnTrackStarted(r.Air.Played[0]);
        for (var i = 0; i < 3; i++) r.TrackOnAir();       // жодних 25 хвилин: господар поставив нуль
        Assert.Equal(2, r.Air.Played.Count);
    }

    // =============================================================================================
    // Запаска: нове не вантажиться, крутиться знайоме з кешу — у реклами своя частота
    // =============================================================================================

    [Fact]
    public void On_the_spare_an_ad_goes_every_three_tracks_but_not_more_often_than_every_ten_minutes()
    {
        using var r = new AdRig();
        r.OnAir.Spare = true;
        var clip = r.Add("Глекминатор");

        r.TrackOnAir("s-1");
        r.TrackOnAir("s-2");
        Assert.Empty(r.Air.Played);
        r.TrackOnAir("s-3");
        Assert.Single(r.Air.Played);                      // три треки запаски — і реклама, хоч звичайних шести не було

        r.AdOnAir(clip);
        for (var i = 4; i < 10; i++) r.TrackOnAir($"s-{i}");
        Assert.Single(r.Air.Played);                      // треків досить, а десяти хвилин ще нема

        r.Clock.Advance(TimeSpan.FromMinutes(11));
        r.TrackOnAir("s-10");
        Assert.Equal(2, r.Air.Played.Count);
    }

    [Fact]
    public void Back_from_the_spare_the_usual_frequency_rules_again()
    {
        using var r = new AdRig();
        r.Add("Глекминатор");
        r.OnAir.Spare = true;
        r.TrackOnAir("s-1");
        r.TrackOnAir("s-2");

        r.OnAir.Spare = false;                            // нове знову вантажиться
        for (var i = 0; i < 3; i++) r.TrackOnAir();
        Assert.Empty(r.Air.Played);                       // 5 треків — до звичайних шести ще один
        r.TrackOnAir();
        Assert.Single(r.Air.Played);
    }

    [Fact]
    public void Spare_frequency_from_the_admin_is_its_own_and_survives_a_restart()
    {
        using var r = new AdRig();
        Assert.Equal((3, 10), r.Jingle.SpareFrequency());   // типове: раз на 3 треки, не частіше ніж раз на 10 хв

        Assert.Equal((true, "У запасці реклама — раз на 2 тр., але не частіше ніж раз на 5 хв"), r.Jingle.SetSpareFrequency(2, 5));
        Assert.Equal((2, 5), r.Cold().SpareFrequency());
        Assert.Equal((6, 25), r.Cold().Frequency());         // звичайна частота не зачеплена

        r.Jingle.SetFrequency(4, 0);
        Assert.Equal((2, 5), r.Cold().SpareFrequency());     // і навпаки
        Assert.False(r.Jingle.SetSpareFrequency(0, 5).Ok);
        Assert.False(r.Jingle.SetSpareFrequency(3, 601).Ok);
        Assert.Equal((2, 5), r.Jingle.SpareFrequency());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(101, 0)]
    [InlineData(3, -1)]
    [InlineData(3, 601)]
    public void Nonsense_frequency_is_refused(int every, int minutes)
    {
        using var r = new AdRig();
        Assert.False(r.Jingle.SetFrequency(every, minutes).Ok);
        Assert.Equal((6, 25), r.Jingle.Frequency());
    }

    [Fact]
    public void Without_a_saved_frequency_the_config_decides()
    {
        using var r = new AdRig();
        Assert.Equal((6, 25), r.Jingle.Frequency());

        r.Options.EveryTracks = 3;
        r.Options.MinMinutes = 0;
        Assert.Equal((3, 0), r.Jingle.Frequency());

        r.Options.EveryTracks = 0;                        // нуль у конфізі — не «щомиті», а щотреку
        r.Options.MinMinutes = -5;
        Assert.Equal((1, 0), r.Jingle.Frequency());
    }

    [Fact]
    public void Play_now_queues_the_next_clip_at_once_and_restarts_the_count()
    {
        using var r = new AdRig();
        r.Add("Глекминатор");
        r.TrackOnAir();

        Assert.Equal((true, "«Глекминатор» стала в чергу"), r.Jingle.PlayNow());

        Assert.Single(r.Air.Played);
        Assert.Equal(0, r.Jingle.Since);
    }

    [Fact]
    public void Play_now_with_an_empty_rotation_says_so_but_a_chosen_clip_still_plays()
    {
        using var r = new AdRig();
        Assert.Equal((false, "Нема що крутити: у ротації порожньо"), r.Jingle.PlayNow());

        var clip = r.Add("Вимкнена");
        r.Library.SetEnabled(clip.Id, false);
        Assert.Equal((false, "Нема що крутити: у ротації порожньо"), r.Jingle.PlayNow());

        // «саме цю в чергу» грає й ту, що вимкнена в ротації
        Assert.True(r.Jingle.PlayClip(clip.Id).Ok);
        Assert.Equal("Вимкнена", Assert.Single(r.Air.Played).Artist);
    }

    // =============================================================================================
    // Бібліотека й ротація
    // =============================================================================================

    [Fact]
    public void Rotation_plays_every_clip_once_before_any_repeats_and_never_twice_in_a_row()
    {
        using var r = new AdRig();
        foreach (var t in new[] { "А", "Б", "В", "Г", "Ґ" }) r.Add(t);

        var played = new List<string>();
        for (var i = 0; i < 50; i++) played.Add(r.Library.Take()!.Title);

        for (var deck = 0; deck < 10; deck++)
            Assert.Equal(5, played.Skip(deck * 5).Take(5).Distinct().Count());
        for (var i = 1; i < played.Count; i++) Assert.NotEqual(played[i - 1], played[i]);
    }

    [Fact]
    public void Rotation_skips_switched_off_clips_and_clips_whose_file_is_gone()
    {
        using var r = new AdRig();
        r.Add("Живий");
        var off = r.Add("Вимкнений");
        var gone = r.Add("Без файлу");
        r.Library.SetEnabled(off.Id, false);
        r.Voice.Files.Remove(gone.TrackId);

        for (var i = 0; i < 10; i++) Assert.Equal("Живий", r.Library.Take()!.Title);

        r.Library.SetAll(false);
        Assert.Null(r.Library.Take());
        Assert.False(r.Library.HasLive());
    }

    [Fact]
    public void A_clip_playing_counts_as_a_play_and_resets_the_track_counter()
    {
        using var r = new AdRig();
        r.Options.EveryTracks = 3;
        r.Options.MinMinutes = 0;
        var clip = r.Add("Прожарка");

        r.TrackOnAir("yt-1");
        r.TrackOnAir("yt-2");
        r.AdOnAir(clip);

        Assert.Equal(0, r.Jingle.Since);
        Assert.Equal(1, r.Library.All()[0].Plays);
        Assert.Empty(r.Air.Played);
    }

    [Fact]
    public void Deleting_a_clip_removes_its_file_rename_validates_the_title()
    {
        using var r = new AdRig();
        var clip = r.Add("Стара назва");

        Assert.False(r.Library.Rename(clip.Id, "   ").Ok);
        Assert.True(r.Library.Rename(clip.Id, "Нова назва").Ok);
        Assert.Equal("Нова назва", r.Library.Get(clip.Id)!.Title);

        Assert.True(r.Library.Delete(clip.Id).Ok);
        Assert.Contains(clip.TrackId, r.Voice.Deleted);
        Assert.Empty(r.Library.All());
        Assert.False(r.Library.IsAd(clip.TrackId));
    }

    // =============================================================================================
    // Черепки за прослухану рекламу
    // =============================================================================================

    [Fact]
    public void Listeners_who_heard_the_ad_to_the_end_get_shards_up_to_the_daily_cap()
    {
        using var r = new AdRig();
        r.Options.ListenReward = 2;
        r.Options.ListenDailyCap = 4;
        var clip = r.Add("Каламбур");
        var track = new TrackInfo(clip.TrackId, AdJingle.AdTitle, clip.Title, 20, null, "x", null);
        r.Presence.Set("c2", "Петро");
        r.Presence.SetListening("c2", true);
        r.OnAir.TrackId = clip.TrackId;

        for (var i = 0; i < 3; i++)
        {
            var p = r.Rewards.Start(track)!;
            r.Clock.Advance(TimeSpan.FromSeconds(30));
            r.Rewards.Settle(p);
        }

        Assert.Equal(4, r.Eco.Paid("Петро", "ad:listen"));       // стеля 4 = дві реклами по 2
        Assert.Equal(0, r.Eco.Paid("Оля", "ad:listen"));          // Оля на сайті, але плеєр вимкнений
    }

    [Fact]
    public void No_shards_for_an_ad_that_was_skipped_or_for_someone_who_switched_the_player_off()
    {
        using var r = new AdRig();
        var clip = r.Add("Каламбур");
        var track = new TrackInfo(clip.TrackId, AdJingle.AdTitle, clip.Title, 20, null, "x", null);
        r.Presence.Set("c2", "Петро");
        r.Presence.SetListening("c2", true);
        r.Presence.Set("c3", "Марта");
        r.Presence.SetListening("c3", true);

        r.OnAir.TrackId = clip.TrackId;
        r.OnAir.SkipPending = true;
        Assert.Equal(0, r.Rewards.Settle(r.Rewards.Start(track)!));

        r.OnAir.SkipPending = false;
        var p = r.Rewards.Start(track)!;
        r.Presence.SetListening("c3", false);
        Assert.Equal(1, r.Rewards.Settle(p));
        Assert.Equal(0, r.Eco.Paid("Марта", "ad:listen"));
        Assert.Equal(r.Options.ListenReward, r.Eco.Paid("Петро", "ad:listen"));

        // реклама вже скінчилась і грає пісня — запізніла перевірка нічого не платить
        var late = r.Rewards.Start(track)!;
        r.OnAir.TrackId = "yt-9";
        Assert.Equal(0, r.Rewards.Settle(late));
    }

    // =============================================================================================
    // Що лишилось від конкурсу реклами (прибрано 26.09.2026)
    // =============================================================================================

    const string OldHouseTrack = "voice-0000house0";

    /// <summary>
    /// Рядок ефіру таким, яким його лишив сервер часів конкурсу: таблиця зі стовпцями реклами господаря, у ній
    /// своя реклама Владіка і частота, яку господар поставив (null — не ставив).
    /// </summary>
    static Action<Db> OldAir(int? every, int? minutes) => db =>
    {
        db.Exec("""
            CREATE TABLE ad_air(
                id INTEGER PRIMARY KEY CHECK(id = 1), track_id TEXT, nick TEXT, dur_sec INTEGER NOT NULL DEFAULT 0,
                own INTEGER NOT NULL DEFAULT 0, every_tracks INTEGER, min_minutes INTEGER, updated_at TEXT)
            """);
        db.Exec("""
            INSERT INTO ad_air(id, track_id, nick, dur_sec, own, every_tracks, min_minutes, updated_at)
            VALUES(1, $t, 'Владік', 12, 1, $e, $m, '2026-09-15T10:00:00.0000000Z')
            """, ("$t", OldHouseTrack), ("$e", every), ("$m", minutes));
    };

    /// <summary>Скільки рядків в ad_air і що лежить у стовпцях реклами господаря.</summary>
    static (long Rows, string? Track, string? Nick) OldRow(Db db) => db.With<(long, string?, string?)>(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(*) FROM ad_air), track_id, nick FROM ad_air WHERE id = 1";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        return (r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
    });

    [Fact]
    public void Frequency_saved_while_the_contest_was_alive_is_read_and_written_as_before()
    {
        using var r = new AdRig(OldAir(every: 4, minutes: 10));

        Assert.Equal((4, 10), r.Jingle.Frequency());
        Assert.Equal((4, 10), r.Cold().Frequency());                 // і після перезапуску

        Assert.True(r.Jingle.SetFrequency(7, 0).Ok);
        Assert.Equal((7, 0), r.Cold().Frequency());

        // той самий рядок: стовпці реклами господаря лежать як лежали — їх просто ніхто не читає
        Assert.Equal((1L, OldHouseTrack, "Владік"), OldRow(r.Eco.Db));
    }

    [Fact]
    public void An_old_row_without_a_frequency_leaves_it_to_the_config()
    {
        using var r = new AdRig(OldAir(every: null, minutes: null));

        Assert.Equal((6, 25), r.Jingle.Frequency());

        Assert.True(r.Jingle.SetFrequency(2, 5).Ok);
        Assert.Equal((2, 5), r.Cold().Frequency());
    }

    [Fact]
    public void The_old_house_ad_left_in_the_base_never_goes_on_air()
    {
        using var r = new AdRig(OldAir(every: 1, minutes: 0));
        r.Voice.Files.Add(OldHouseTrack);                            // і файл її ще живий

        for (var i = 0; i < 10; i++) r.TrackOnAir($"yt-{i}");

        Assert.Empty(r.Air.Played);
        Assert.Equal((false, "Нема що крутити: у ротації порожньо"), r.Jingle.PlayNow());
    }

    [Fact]
    public void The_library_view_keeps_its_shape_and_the_fallback_is_always_null()
    {
        using var r = new AdRig(OldAir(every: 4, minutes: 10));
        r.Voice.Files.Add(OldHouseTrack);                            // колись саме вона й була б «fallback»
        r.Add("Глекминатор", 22);

        var v = Views.Json(AdSetup.LibraryView(r.Library, r.Jingle, r.Voice, r.Options));

        var item = Assert.Single(v.GetProperty("items").EnumerateArray().ToList());
        Assert.True(Views.Has(item, "id"));
        Assert.StartsWith("voice-", item.GetProperty("trackId").GetString());
        Assert.Equal("Глекминатор", item.GetProperty("title").GetString());
        Assert.Equal(22, item.GetProperty("seconds").GetInt32());
        Assert.True(item.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, item.GetProperty("plays").GetInt32());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("lastPlayedAt").ValueKind);
        Assert.True(Views.Has(item, "createdAt"));
        Assert.False(item.GetProperty("missing").GetBoolean());

        Assert.Equal(4, v.GetProperty("everyTracks").GetInt32());
        Assert.Equal(10, v.GetProperty("minMinutes").GetInt32());
        Assert.Equal(0, v.GetProperty("since").GetInt32());
        Assert.True(v.GetProperty("jingle").GetBoolean());
        Assert.Equal(2, v.GetProperty("reward").GetProperty("amount").GetInt32());
        Assert.Equal(40, v.GetProperty("reward").GetProperty("dailyCap").GetInt32());
        Assert.Equal(10, v.GetProperty("maxMb").GetInt32());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("fallback").ValueKind);
    }

    [Fact]
    public void The_air_view_keeps_its_fields_but_has_no_house_ad_any_more()
    {
        using var r = new AdRig(OldAir(every: 4, minutes: 10));
        r.Voice.Files.Add(OldHouseTrack);
        r.Options.Jingle = false;

        var v = Views.Json(AdSetup.AirView(r.Jingle, r.Options));

        Assert.Equal(JsonValueKind.Null, v.GetProperty("on").ValueKind);
        Assert.False(v.GetProperty("houseMissing").GetBoolean());
        Assert.Equal(4, v.GetProperty("everyTracks").GetInt32());
        Assert.Equal(10, v.GetProperty("minMinutes").GetInt32());
        Assert.Equal(0, v.GetProperty("since").GetInt32());
        Assert.False(v.GetProperty("jingle").GetBoolean());
    }

    [Fact]
    public void The_contest_is_gone_from_the_games_catalog()
    {
        Assert.DoesNotContain(new Registry().Catalog, g => g.Id == "ad-contest");
    }

    [Fact]
    public void Leftover_contest_keys_in_the_config_break_nothing()
    {
        // так могла б виглядати секція Ad у чиємусь appsettings.Local.json, списаному ще за часів конкурсу
        const string json = """
            { "Ad": { "Enabled": true, "Days": 3, "MaxSeconds": 30, "GraceSeconds": 5, "AutoOpen": true, "OpenHour": 12,
                      "Jingle": false, "EveryTracks": 4, "WinnerReward": 25, "EntryReward": 3, "VoteReward": 1,
                      "ListenReward": 5 } }
            """;
        var cfg = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(cfg);
        services.AddHlechykyEconomy();

        var o = services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<AdOptions>>().CurrentValue;

        Assert.True(o.Enabled);
        Assert.False(o.Jingle);
        Assert.Equal(4, o.EveryTracks);
        Assert.Equal(25, o.MinMinutes);               // чого в конфізі нема — типове
        Assert.Equal(5, o.ListenReward);
        Assert.Equal(40, o.ListenDailyCap);
    }

    [Fact]
    public void Old_contest_lines_in_the_wallet_still_read_as_before()
    {
        using var eco = new EconomyRig();

        Assert.Equal("перемога в конкурсі реклами", eco.Economy.Reason("ad:winner"));
        Assert.Equal("за голос у конкурсі реклами", eco.Economy.Reason("ad:vote"));
        Assert.Equal("за конкурс реклами", eco.Economy.Reason("ad:entry"));
        Assert.Equal("за прослухану рекламу", eco.Economy.Reason("ad:listen"));
        Assert.Equal("+25 черепків: перемога в конкурсі реклами", eco.Economy.Text(25, "ad:winner"));
    }

    [Fact]
    public void A_voice_of_the_village_already_won_stays_in_the_profile()
    {
        using var eco = new EconomyRig();
        Assert.True(eco.Achievements.Unlock("Оля", "ad-winner"));     // так її видавав конкурс — так вона й лежить у базі

        Assert.Contains(eco.Achievements.Of("Оля"), a => a.Info.Key == "ad-winner" && a.Info.Title == "Голос села");
        Assert.True(AchievementCatalog.Get("ad-winner")!.Hidden);    // нових уже не буде, тож і не обіцяємо
    }
}
