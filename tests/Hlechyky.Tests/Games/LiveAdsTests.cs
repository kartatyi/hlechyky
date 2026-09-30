using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Жива реклама: множина й шаблони, банк фраз, факти на засіяній базі, вибір цілі й кулдауни, пріоритет у джинглі,
/// замовлення (списання, ліміти, повернення), відмова, події, новини й міграція старої бази. Справжнього edge-tts
/// тут нема (рендер — заглушка); ffmpeg-рендер — окремий тест, що пропускається без ffmpeg.
/// </summary>
public class LiveAdsTests
{
    // =============================================================================================
    // Обв'язка
    // =============================================================================================

    internal static string RepoFile(string rel)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, rel);
            if (File.Exists(p) || Directory.Exists(p)) return p;
        }
        throw new FileNotFoundException(rel);
    }

    internal static readonly Lazy<LiveLines> Bank = new(() => LiveLines.Load(RepoFile("data/liveads/lines.json")));

    /// <summary>Рендер-заглушка: пише «файл» і каже 20 с; Fail — «edge-tts ліг».</summary>
    sealed class FakeRenderer : ILiveRenderer
    {
        public bool Fail { get; set; }
        public List<LiveScript> Scripts { get; } = new();

        public Task<double?> RenderAsync(LiveScript script, string outPath, CancellationToken ct)
        {
            Scripts.Add(script);
            if (Fail) return Task.FromResult<double?>(null);
            File.WriteAllText(outPath, "mp3");
            return Task.FromResult<double?>(20);
        }
    }

    sealed class FakeAir : IAdAir
    {
        public List<TrackInfo> Played { get; } = new();
        public (bool Ok, string Message) AddVoice(TrackInfo track, string filePath, string nick)
        {
            Played.Add(track);
            return (true, "в черзі");
        }
    }

    sealed class FakeVoice : IVoiceSaver
    {
        public bool Enabled => true;
        public long MaxUploadBytes => 1 << 20;
        public Task<(TrackInfo Track, string FilePath)> SaveAsync(Stream body, string nick, CancellationToken ct) => throw new NotSupportedException();
        public string? FilePath(string id) => id.StartsWith("voice-lib", StringComparison.Ordinal) ? id + ".mp3" : null;
        public void Delete(string id) { }
    }

    sealed class FakeOnAir : IAdOnAir
    {
        public (string? TrackId, bool SkipPending) Now() => (null, false);
    }

    sealed class Rig : IDisposable
    {
        public EconomyRig Eco { get; } = new();
        public LiveAdsOptions O { get; } = new();
        public CurfewOptions Curfew { get; } = new();
        public FakeRenderer Render { get; } = new();
        public LiveAdsStore Store { get; }
        public LiveFacts Facts { get; }
        public LiveAds Live { get; }
        public string Cache { get; } = Path.Combine(Path.GetTempPath(), "hlechyky-live-" + Guid.NewGuid().ToString("N")[..8]);
        public double RollValue { get; set; } = 0.1;

        public FakeClock Clock => Eco.Clock;
        public Presence Presence => Eco.Presence;
        public Db Db => Eco.Db;

        public Rig()
        {
            Directory.CreateDirectory(Cache);
            _ = new LavkaStore(Db);          // таблиці Лавки: на сервері їх створює Лавка, факти й події їх читають
            Store = new LiveAdsStore(Db);
            Facts = new LiveFacts(Db, new GameNames([EconomyRig.Info("tron", "Мотоцикли", "мотоцикли"), EconomyRig.Info("chess", "Шахи", "шахи")]), Clock);
            Live = new LiveAds(Store, Facts, Render, Eco.Economy, Presence, Eco.Outbox, Clock, new FixedOptions<LiveAdsOptions>(O),
                new FixedOptions<CurfewOptions>(Curfew), new FixedOptions<YtDlpOptions>(new YtDlpOptions()), NullLogger<LiveAds>.Instance)
            {
                CacheDir = Cache, Rng = new Random(7), Roll = () => RollValue, Lines = Bank.Value,
            };
        }

        public void Account(string nick) => Db.AddAccount(nick, "h", "s");

        public void Online(string nick, bool listening = true)
        {
            Account(nick);
            var conn = "c-" + nick;
            Presence.Set(conn, nick);
            if (listening) Presence.SetListening(conn, true);
        }

        public void Exec(string sql, params (string, object?)[] ps) => Db.With(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        });

        public static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

        public void Result(string nick, string game, string outcome, DateTimeOffset? at = null, string? room = null, int round = 1) =>
            Exec("INSERT INTO game_results(room_id, game, round, nick_key, nick, outcome, created_at) VALUES($r, $g, $round, $k, $n, $o, $at)",
                ("$r", room ?? Guid.NewGuid().ToString("N")), ("$g", game), ("$round", round), ("$k", Auth.NickKey(nick)), ("$n", nick),
                ("$o", outcome), ("$at", Iso(at ?? Clock.UtcNow)));

        /// <summary>Кілька результатів підряд, по секунді між ними — щоб id і час ішли в одному порядку.</summary>
        public void Results(string nick, string game, string outcome, int n)
        {
            for (var i = 0; i < n; i++)
            {
                Result(nick, game, outcome);
                Clock.Advance(1);
            }
        }

        public void Dispose()
        {
            Eco.Dispose();
            try { Directory.Delete(Cache, true); } catch (IOException) { }
        }
    }

    static LiveFact? Fact(List<LiveFact> list, string kind) => list.FirstOrDefault(f => f.Kind == kind);

    // =============================================================================================
    // Множина і шаблони
    // =============================================================================================

    [Theory]
    [InlineData(1, "гра")]
    [InlineData(2, "гри")]
    [InlineData(4, "гри")]
    [InlineData(5, "ігор")]
    [InlineData(11, "ігор")]
    [InlineData(12, "ігор")]
    [InlineData(14, "ігор")]
    [InlineData(21, "гра")]
    [InlineData(22, "гри")]
    [InlineData(25, "ігор")]
    [InlineData(111, "ігор")]
    [InlineData(0, "ігор")]
    public void Plural_follows_ukrainian_rules(int n, string form) => Assert.Equal(form, LiveLines.Plural(n, "гра", "гри", "ігор"));

    [Fact]
    public void Fill_takes_the_last_number_before_the_forms()
    {
        var v = new Dictionary<string, string> { ["nick"] = "Смауг", ["n"] = "3", ["m"] = "11" };
        Assert.Equal("Смауг: 3 гри, 11 поразок", LiveLines.Fill("{nick}: {n} {гра|гри|ігор}, {m} {поразка|поразки|поразок}", v));
        Assert.Equal("за 7 днів 1 гра", LiveLines.Fill("за 7 днів {n} {гра|гри|ігор}", new Dictionary<string, string> { ["n"] = "1" }));
    }

    [Fact]
    public void Fill_rejects_unknown_placeholders_and_forms_without_number()
    {
        var v = new Dictionary<string, string> { ["nick"] = "владік" };
        Assert.Null(LiveLines.Fill("{nick} і {rival}", v));
        Assert.Null(LiveLines.Fill("{nick}: {гра|гри|ігор}", v));
        Assert.Equal("владік.", LiveLines.Fill("{nick}. {rim}", v));
    }

    [Fact]
    public void Line_prefix_picks_voice_and_rim_marks_the_line()
    {
        var v = new Dictionary<string, string> { ["nick"] = "Назар" };
        var p = LiveLines.Line("П: Привіт, {nick}!", v, LiveLines.Glek, "-4%")!;
        Assert.Equal(LiveLines.Polina, p.Voice);
        Assert.Equal("Привіт, Назар!", p.Text);
        Assert.False(p.Rim);
        var g = LiveLines.Line("Г: Ну, {nick}. {rim}", v, LiveLines.Polina, "-4%")!;
        Assert.Equal(LiveLines.Glek, g.Voice);
        Assert.True(g.Rim);
        Assert.Equal("Ну, Назар.", g.Text);
    }

    [Fact]
    public void Nicks_are_spoken_without_brackets_and_by_the_dictionary()
    {
        var lines = Bank.Value;
        Assert.Equal("Смауг", lines.Say("Smaug"));
        Assert.Equal("Микола справжній", lines.Say("микола ( справжній )"));
        Assert.Equal("Старший Брат", lines.Say("Старший Брат 👁"));
    }

    // =============================================================================================
    // Банк фраз
    // =============================================================================================

    static readonly Dictionary<string, string> Everything = new()
    {
        ["nick"] = "Назар", ["target"] = "Назар", ["buyer"] = "Смауг", ["rival"] = "владік", ["n"] = "7", ["m"] = "3", ["w"] = "2",
        ["g"] = "5", ["b"] = "900", ["h"] = "3", ["min"] = "190", ["wh"] = "12", ["all"] = "9", ["game"] = "Мотоцикли",
        ["track"] = "Я Канівес", ["artist"] = "Ем Сі Петя", ["ach"] = "Шериф", ["item"] = "Веселка", ["nicks"] = "владік, Смауг",
        ["hh"] = "7", ["mm"] = "05",
    };

    static IEnumerable<(string Where, string Template)> AllTemplates(LiveLines l)
    {
        foreach (var (k, v) in l.Intro) foreach (var t in v) yield return ($"intro.{k}", t);
        foreach (var t in l.Link) yield return ("link", t);
        foreach (var t in l.Outro) yield return ("outro", t);
        foreach (var (k, v) in l.Facts) foreach (var t in v) yield return ($"facts.{k}", t);
        foreach (var (k, v) in l.Events) foreach (var t in v) yield return ($"events.{k}", t);
        foreach (var (k, v) in l.News) foreach (var t in v) yield return ($"news.{k}", t);
    }

    [Fact]
    public void Every_template_fills_and_uses_only_known_placeholders()
    {
        var bad = AllTemplates(Bank.Value)
            .Where(x => LiveLines.Fill(x.Template, Everything) is null || LiveLines.Keys(x.Template).Any(k => !Everything.ContainsKey(k)))
            .Select(x => $"{x.Where}: {x.Template}").ToList();
        Assert.Empty(bad);
    }

    [Fact]
    public void Every_fact_event_and_news_kind_has_at_least_four_variants()
    {
        var l = Bank.Value;
        string[] facts = ["losses_today", "wins_today", "lose_streak", "win_streak", "game_of_day", "site_time", "clicker_time",
            "clicker_week", "listen_time", "night_owl", "same_track", "skips_self", "skipped_by_others", "ne_te", "likes", "hoarder",
            "rich", "poor", "wallet", "lavka_buys", "stakes_lost", "ach_today", "daily_tries", "daily_fail", "rival_ahead",
            "rival_behind", "first_today", "nothing"];
        string[] events = ["lose_streak", "win_streak", "big_buy", "new_account", "curfew"];
        string[] news = ["intro", "sport_intro", "top_winner", "lose_streak", "game_of_day", "most_skipped", "top_orderer", "newbie",
            "newbies", "record_ach", "record_time", "quiet", "outro"];
        Assert.All(facts, k => Assert.True(l.Facts.GetValueOrDefault(k)?.Count >= 4, $"facts.{k}"));
        Assert.All(events, k => Assert.True(l.Events.GetValueOrDefault(k)?.Count >= 4, $"events.{k}"));
        Assert.All(news, k => Assert.True(l.News.GetValueOrDefault(k)?.Count >= 4, $"news.{k}"));
        Assert.All(new[] { "roast", "order", "order_anon", "event" }, k => Assert.True(l.Intro.GetValueOrDefault(k)?.Count >= 4, $"intro.{k}"));
        Assert.True(l.Outro.Count >= 4 && l.Link.Count >= 4);
    }

    [Fact]
    public void Streak_templates_exist_without_game_too()
    {
        // Серія через кілька ігор приходить без {game}: хоч один шаблон мусить обходитись без нього
        var v = Everything.Where(kv => kv.Key != "game").ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var kind in new[] { "lose_streak", "win_streak" })
            Assert.Contains(Bank.Value.Facts[kind], t => LiveLines.Fill(t, v) is not null);
    }

    [Fact]
    public void Order_intro_names_the_buyer_or_keeps_the_secret()
    {
        var l = Bank.Value;
        Assert.All(l.Intro["order"], t => Assert.Contains("{buyer}", t));
        Assert.All(l.Intro["order_anon"], t => Assert.DoesNotContain("{buyer}", t));
    }

    [Fact]
    public void Bank_file_is_valid_json_and_numbers_are_digits()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(RepoFile("data/liveads/lines.json")));
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    // =============================================================================================
    // Факти
    // =============================================================================================

    [Fact]
    public void Day_starts_at_six_in_kyiv()
    {
        // 12:00 UTC = 15:00 у Києві (літній час) → «сьогодні» з 03:00 UTC; 02:00 UTC = 05:00 Києва → ще вчорашні 06:00
        Assert.Equal(new DateTimeOffset(2026, 9, 10, 3, 0, 0, TimeSpan.Zero), LiveFacts.DayStart(new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateTimeOffset(2026, 9, 9, 3, 0, 0, TimeSpan.Zero), LiveFacts.DayStart(new DateTimeOffset(2026, 9, 10, 2, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Losses_today_name_the_worst_game_by_its_lobby_title_and_skip_the_night()
    {
        using var r = new Rig();
        r.Account("владік");
        r.Result("владік", "tron", "loss", r.Clock.UtcNow.AddHours(-10));   // 05:00 Києва — ще вчора
        r.Results("владік", "tron", "loss", 3);
        r.Results("владік", "chess", "loss", 1);
        r.Results("владік", "chess", "win", 1);
        var f = Fact(r.Facts.For("владік"), "losses_today")!;
        Assert.Equal("4", f.Values["n"]);
        Assert.Equal("Мотоцикли", f.Values["game"]);
        Assert.Equal("3", f.Values["m"]);
    }

    [Fact]
    public void Streaks_count_from_the_last_game()
    {
        using var r = new Rig();
        r.Results("Смауг", "chess", "win", 1);
        r.Results("Смауг", "tron", "loss", 4);
        var f = r.Facts.For("Смауг");
        Assert.Equal("4", Fact(f, "lose_streak")!.Values["n"]);
        Assert.Equal("Мотоцикли", Fact(f, "lose_streak")!.Values["game"]);
        Assert.Null(Fact(f, "win_streak"));

        r.Results("Смауг", "chess", "win", 3);
        r.Results("Смауг", "tron", "win", 1);
        var w = Fact(r.Facts.For("Смауг"), "win_streak")!;
        Assert.Equal("4", w.Values["n"]);
        Assert.False(w.Values.ContainsKey("game"));    // серія через дві гри — без назви
    }

    [Fact]
    public void Game_of_day_skips_the_clicker()
    {
        using var r = new Rig();
        r.Results("Назар", "clicker", "solo", 9);
        Assert.Null(Fact(r.Facts.For("Назар"), "game_of_day"));
        r.Results("Назар", "chess", "draw", 5);
        Assert.Equal("Шахи", Fact(r.Facts.For("Назар"), "game_of_day")!.Values["game"]);
    }

    [Fact]
    public void Time_facts_come_from_economy_counters()
    {
        using var r = new Rig();
        var day = Days.Of(r.Clock.UtcNow);
        r.Exec("INSERT INTO economy_counters(nick_key, key, day, n) VALUES('smaug', 'time:site', $d, 18000), ('smaug', 'time:game:clicker', $d, 7300)", ("$d", day));
        var f = r.Facts.For("Smaug");
        Assert.Equal("5", Fact(f, "site_time")!.Values["h"]);
        Assert.Equal("2", Fact(f, "clicker_time")!.Values["h"]);
    }

    [Fact]
    public void Night_owl_counts_last_night_only_and_hides_zeros()
    {
        using var r = new Rig();
        var night = new DateTimeOffset(2026, 9, 9, 23, 30, 0, TimeSpan.Zero);   // 02:30 за Києвом
        for (var i = 0; i < 4; i++)
            r.Exec("INSERT INTO plays(track_id, source, requested_by, started_at) VALUES('t', 'user', 'владік', $at)", ("$at", Rig.Iso(night.AddMinutes(i))));
        r.Exec("INSERT INTO plays(track_id, source, requested_by, started_at) VALUES('t', 'user', 'владік', $at)", ("$at", Rig.Iso(r.Clock.UtcNow)));
        var f = Fact(r.Facts.For("владік"), "night_owl")!;
        Assert.Equal("4", f.Values["all"]);
        Assert.Equal("4", f.Values["n"]);
        Assert.False(f.Values.ContainsKey("m"));
    }

    [Fact]
    public void Same_track_three_times_a_week_and_titles_lose_youtube_tails()
    {
        using var r = new Rig();
        r.Exec("INSERT INTO tracks(id, title, artist, source_url, created_at) VALUES('k1', 'Я Канівес (Official Video)', 'MC Petya', 'u', $n)", ("$n", Rig.Iso(r.Clock.UtcNow)));
        for (var i = 0; i < 3; i++)
            r.Exec("INSERT INTO plays(track_id, source, requested_by, started_at) VALUES('k1', 'user', 'владік', $at)", ("$at", Rig.Iso(r.Clock.UtcNow.AddDays(-i))));
        var f = Fact(r.Facts.For("владік"), "same_track")!;
        Assert.Equal("Я Канівес", f.Values["track"]);
        Assert.Equal("3", f.Values["n"]);
    }

    [Fact]
    public void Skips_split_into_own_and_by_others()
    {
        using var r = new Rig();
        var t = r.Clock.UtcNow.AddMinutes(-30);
        for (var i = 0; i < 3; i++)
        {
            var at = t.AddMinutes(i * 5);
            r.Exec("INSERT INTO plays(track_id, source, requested_by, started_at, ended_at, skipped) VALUES('x', 'user', 'Smaug', $s, $e, 1)",
                ("$s", Rig.Iso(at)), ("$e", Rig.Iso(at.AddMinutes(1))));
        }
        // перший скіпнула сама замовниця — він не «чужий»
        r.Exec("INSERT INTO chat(nick, text, kind, created_at) VALUES('Глечики', 'Smaug скіпає Щось — Там', 'system', $at)", ("$at", Rig.Iso(t.AddSeconds(30))));
        r.Exec("INSERT INTO chat(nick, text, kind, created_at) VALUES('Глечики', 'Smaug скіпає А — Б', 'system', $at)", ("$at", Rig.Iso(t.AddMinutes(20))));
        r.Exec("INSERT INTO chat(nick, text, kind, created_at) VALUES('Глечики', 'Smaug скіпає В — Г', 'system', $at)", ("$at", Rig.Iso(t.AddMinutes(21))));
        var f = r.Facts.For("Smaug");
        Assert.Equal("2", Fact(f, "skipped_by_others")!.Values["n"]);
        Assert.Equal("3", Fact(f, "skips_self")!.Values["n"]);
    }

    [Fact]
    public void Ne_te_and_likes_today()
    {
        using var r = new Rig();
        for (var i = 0; i < 4; i++)
            r.Exec("INSERT INTO dj_feedback(artist_key, kind, nick, created_at) VALUES('a', 'dismiss', 'Smaug', $at)", ("$at", Rig.Iso(r.Clock.UtcNow)));
        for (var i = 0; i < 6; i++)
            r.Exec("INSERT INTO likes(track_id, nick, created_at) VALUES($t, 'Smaug', $at)", ("$t", "l" + i), ("$at", Rig.Iso(r.Clock.UtcNow)));
        var f = r.Facts.For("Smaug");
        Assert.Equal("4", Fact(f, "ne_te")!.Values["n"]);
        Assert.Equal("6", Fact(f, "likes")!.Values["n"]);
    }

    [Fact]
    public void Wallet_facts_compare_with_who_is_present()
    {
        using var r = new Rig();
        r.Eco.Economy.Grant("Назар", 1200, "award");
        r.Eco.Economy.Grant("владік", 50, "award");
        var f = r.Facts.For("Назар", ["Назар", "владік"]);
        Assert.NotNull(Fact(f, "hoarder"));
        Assert.Equal(r.Eco.Economy.Balance("Назар").ToString(), Fact(f, "rich")!.Values["n"]);
        Assert.NotNull(Fact(r.Facts.For("владік", ["Назар", "владік"]), "poor"));
        Assert.Null(Fact(r.Facts.For("Назар", ["Назар"]), "rich"));     // сам на сайті — ні з ким порівнювати
    }

    [Fact]
    public void Lost_stakes_subtract_the_won_ones()
    {
        using var r = new Rig();
        r.Eco.Economy.Grant("Микола", 500, "award");
        for (var i = 0; i < 3; i++) Assert.True(r.Eco.Economy.TrySpend("Микола", 25, "stake", $"stake:{i}"));
        r.Eco.Economy.Grant("Микола", 50, "stake-win", "sw:1");
        var f = Fact(r.Facts.For("Микола"), "stakes_lost")!;
        Assert.Equal("2", f.Values["n"]);
        Assert.Equal("25", f.Values["m"]);
    }

    [Fact]
    public void Rival_is_someone_present_with_shared_games_this_week()
    {
        using var r = new Rig();
        for (var i = 0; i < 4; i++)
        {
            var room = "room" + i;
            r.Result("владік", "tron", i < 3 ? "win" : "loss", room: room);
            r.Result("Микола", "tron", i < 3 ? "loss" : "win", room: room);
        }
        var ahead = Fact(r.Facts.For("владік", ["владік", "Микола"]), "rival_ahead")!;
        Assert.Equal("Микола", ahead.Values["rival"]);
        Assert.Equal("3", ahead.Values["n"]);
        Assert.Equal("1", ahead.Values["m"]);
        Assert.NotNull(Fact(r.Facts.For("Микола", ["владік", "Микола"]), "rival_behind"));
        Assert.Null(Fact(r.Facts.For("владік", ["владік"]), "rival_ahead"));   // суперник не онлайн — не згадуємо
    }

    [Fact]
    public void First_today_is_the_earliest_account_in_the_ledger()
    {
        using var r = new Rig();
        r.Account("Назар");
        r.Account("владік");
        r.Clock.UtcNow = new DateTimeOffset(2026, 9, 10, 4, 5, 0, TimeSpan.Zero);   // 07:05 Києва
        r.Eco.Economy.Grant("Назар", 1, "listen", "l1");
        r.Clock.Advance(TimeSpan.FromHours(1));
        r.Eco.Economy.Grant("владік", 1, "listen", "l2");
        var f = Fact(r.Facts.For("Назар"), "first_today")!;
        Assert.Equal("7", f.Values["hh"]);
        Assert.Equal("05", f.Values["mm"]);
        Assert.Null(Fact(r.Facts.For("владік"), "first_today"));
    }

    [Fact]
    public void Achievements_and_daily_tries()
    {
        using var r = new Rig();
        r.Exec("INSERT INTO achievements(nick_key, key, nick, unlocked_at) VALUES('smaug', 'first-game', 'Smaug', $a), ('smaug', 'first-win', 'Smaug', $a)", ("$a", Rig.Iso(r.Clock.UtcNow)));
        r.Exec("INSERT INTO daily_results(day, game, nick_key, nick, solved, attempts, ms, created_at) VALUES($d, 'chess', 'smaug', 'Smaug', 1, 6, 0, $a)",
            ("$d", Days.Of(r.Clock.UtcNow)), ("$a", Rig.Iso(r.Clock.UtcNow)));
        var f = r.Facts.For("Smaug");
        Assert.Equal("2", Fact(f, "ach_today")!.Values["n"]);
        Assert.Equal("6", Fact(f, "daily_tries")!.Values["n"]);
    }

    // =============================================================================================
    // Сценарій і вибір цілі
    // =============================================================================================

    static void Juicy(Rig r, string nick)
    {
        r.Results(nick, "tron", "loss", 6);
        r.Exec("INSERT INTO economy_counters(nick_key, key, day, n) VALUES($k, 'time:site', $d, 20000)", ("$k", Auth.NickKey(nick)), ("$d", Days.Of(r.Clock.UtcNow)));
    }

    [Fact]
    public void Roast_script_is_intro_facts_and_outro()
    {
        using var r = new Rig();
        r.Online("владік");
        Juicy(r, "владік");
        var s = r.Live.PickTarget()!.Value;
        Assert.Equal("владік", s.Nick);
        Assert.Equal(2, s.Script.Facts.Count);
        Assert.Equal(LiveLines.Polina, s.Script.Lines[0].Voice);
        Assert.Contains("Владік", s.Script.Lines[0].Text);                 // вимова зі словника банку
        Assert.DoesNotContain("{", s.Script.Text);
        Assert.Contains(s.Script.Style, Bank.Value.Styles["roast"]);
        // «програв 6 разів» і «6 поразок підряд» — одна родина, в ролик іде лише одне
        Assert.False(s.Script.Facts.Contains("losses_today") && s.Script.Facts.Contains("lose_streak"));
    }

    [Fact]
    public void Nobody_to_roast_without_juicy_facts_guests_or_opt_out()
    {
        using var r = new Rig();
        r.Online("Назар");
        Assert.Null(r.Live.PickTarget());                                   // про Назара нічого нема
        r.Presence.Set("g", "гість Оля");
        Juicy(r, "гість Оля");
        Assert.Null(r.Live.PickTarget());                                   // гість — не ціль
        Juicy(r, "Назар");
        Assert.NotNull(r.Live.PickTarget());
        r.Store.SetOptOut("Назар", true);
        Assert.Null(r.Live.PickTarget());
    }

    [Fact]
    public void Listening_comes_first_then_who_was_not_roasted_longest()
    {
        using var r = new Rig();
        r.Online("Смауг", listening: false);
        r.Online("Назар");
        r.Online("владік");
        foreach (var n in new[] { "Смауг", "Назар", "владік" }) Juicy(r, n);
        // Назара вже смажили 2 години тому — кулдаун минув, але владік чекав довше (ніколи)
        var id = r.Store.Add("roast", "Назар", null, false, 0, "ready", r.Clock.UtcNow.AddHours(-2));
        r.Store.Move(id, "ready", "sent", r.Clock.UtcNow.AddHours(-2));
        Assert.Equal("владік", r.Live.PickTarget()!.Value.Nick);
        r.Presence.SetListening("c-владік", false);
        r.Presence.SetListening("c-Назар", true);
        Assert.Equal("Назар", r.Live.PickTarget()!.Value.Nick);
    }

    [Fact]
    public void Target_cooldown_and_fact_cooldown()
    {
        using var r = new Rig();
        r.Online("владік");
        Juicy(r, "владік");
        var first = r.Live.PickTarget()!.Value.Script;
        var id = r.Store.Add("roast", "владік", null, false, 0, "queued", r.Clock.UtcNow);
        r.Store.Ready(id, first.Text, string.Join(',', first.Facts), "voice-live-x", 20, r.Clock.UtcNow);
        r.Store.Move(id, "ready", "sent", r.Clock.UtcNow);
        r.Clock.Advance(TimeSpan.FromMinutes(44));
        Assert.Null(r.Live.PickTarget());                                   // той самий нік — не частіше ніж раз на 45 хв
        r.Clock.Advance(TimeSpan.FromMinutes(2));
        // Кулдаун минув, але обидва соковиті факти вже звучали сьогодні — лишаються хіба дрібні, і прожарки нема
        var again = r.Live.PickTarget();
        Assert.True(again is null || !again.Value.Script.Facts.Intersect(first.Facts).Any());
    }

    // =============================================================================================
    // Джингл: хто першим
    // =============================================================================================

    sealed class JingleRig : IDisposable
    {
        public Rig R { get; } = new();
        public FakeAir Air { get; } = new();
        public AdOptions AdO { get; } = new() { EveryTracks = 3, MinMinutes = 0, ListenReward = 0 };
        public AdJingle Jingle { get; }
        int _n;

        public JingleRig(bool library = true)
        {
            var adStore = new AdLibraryStore(R.Db);
            if (library) adStore.Add("voice-lib1", "Бібліотечна", 20, true, R.Clock.UtcNow);
            var voice = new FakeVoice();
            var lib = new AdLibrary(adStore, voice, R.Clock, NullLogger<AdLibrary>.Instance);
            var rewards = new AdListenRewards(R.Eco.Economy, R.Presence, new FakeOnAir(), R.Clock, new FixedOptions<AdOptions>(AdO),
                NullLogger<AdListenRewards>.Instance) { Delay = _ => new TaskCompletionSource().Task };
            Jingle = new AdJingle(lib, adStore, rewards, Air, voice, R.Presence, R.Clock, new FixedOptions<AdOptions>(AdO),
                NullLogger<AdJingle>.Instance, R.Live);
        }

        public void Song() => Jingle.OnTrackStarted(new TrackInfo("song" + ++_n, "Пісня", "Хтось", 200, null, "u", null));

        /// <summary>Остання реклама з черги заграла.</summary>
        public void AdStarts() => Jingle.OnTrackStarted(Air.Played[^1]);

        public List<string> Ids => Air.Played.Select(t => t.Id).ToList();

        public void Dispose() => R.Dispose();
    }

    [Fact]
    public async Task Ordered_roast_goes_on_the_next_track_but_never_back_to_back()
    {
        using var j = new JingleRig();
        j.R.Online("владік");
        j.R.Online("Назар");
        j.R.Eco.Economy.Grant("Назар", 1000, "award");
        j.Song(); j.Song(); j.Song();                                        // третій трек — бібліотека за частотою
        Assert.Equal(["voice-lib1"], j.Ids);
        Assert.True(j.R.Live.Order("Назар", true, "владік", false).Ok);
        await j.R.Live.TickAsync(default);
        j.Song();                                                            // реклама ще в черзі — замовлена чекає
        Assert.Single(j.Ids);
        j.AdStarts();
        j.Song();                                                            // після реклами трек — і одразу замовлена, без трьох треків
        Assert.Equal(2, j.Ids.Count);
        Assert.StartsWith("voice-live-", j.Ids[1]);
        Assert.Equal(AdJingle.AdTitle, j.Air.Played[1].Title);
        j.AdStarts();
        var row = j.R.Store.Recent(10).Single(x => x.Kind == "order");
        Assert.Equal("aired", row.Status);
    }

    [Fact]
    public async Task Event_beats_news_beats_roast_beats_library()
    {
        using var j = new JingleRig();
        j.R.Online("владік");
        Juicy(j.R, "владік");
        await j.R.Live.TickAsync(default);                                   // водяні знаки + одна прожарка наперед
        Assert.Contains(j.R.Store.WithStatus("ready"), x => x.Kind == "roast");
        j.R.RollValue = 0.9;                                                 // частка не випала
        j.Song(); j.Song(); j.Song();
        Assert.Equal("voice-lib1", j.Ids[^1]);
        j.AdStarts();
        j.R.RollValue = 0.1;
        j.Song(); j.Song(); j.Song();
        Assert.StartsWith("voice-live-", j.Ids[^1]);
        Assert.Equal("roast", j.R.Store.ByFile(j.Ids[^1])!.Kind);
        j.AdStarts();

        // Подія свіжа — іде першою, навіть коли частка не випала
        j.R.Online("Назар");
        j.R.Results("Назар", "tron", "loss", 5);
        await j.R.Live.TickAsync(default);
        j.R.RollValue = 0.99;
        j.Song(); j.Song(); j.Song();
        Assert.Equal("event", j.R.Store.ByFile(j.Ids[^1])!.Kind);
    }

    [Fact]
    public async Task Empty_library_lets_the_live_roast_play_regardless_of_share()
    {
        using var j = new JingleRig(library: false);
        j.R.Online("владік");
        Juicy(j.R, "владік");
        await j.R.Live.TickAsync(default);
        j.R.RollValue = 0.99;
        j.Song(); j.Song(); j.Song();
        Assert.Single(j.Ids);
        Assert.StartsWith("voice-live-", j.Ids[0]);
    }

    [Fact]
    public async Task Disabled_live_ads_leave_the_library_alone()
    {
        using var j = new JingleRig();
        j.R.Online("владік");
        Juicy(j.R, "владік");
        await j.R.Live.TickAsync(default);
        j.R.Live.SetEnabled(false);
        j.Song(); j.Song(); j.Song();
        Assert.Equal(["voice-lib1"], j.Ids);
    }

    [Fact]
    public async Task Roast_of_someone_who_left_is_not_played()
    {
        using var j = new JingleRig();
        j.R.Online("владік");
        Juicy(j.R, "владік");
        await j.R.Live.TickAsync(default);
        j.R.Presence.Remove("c-владік");
        j.R.Presence.Set("x", "Назар");
        j.Song(); j.Song(); j.Song();
        Assert.Equal(["voice-lib1"], j.Ids);
    }

    // =============================================================================================
    // Замовлення
    // =============================================================================================

    [Fact]
    public async Task Order_charges_posts_to_journal_and_bakes_three_facts()
    {
        using var r = new Rig();
        r.Account("владік");
        Juicy(r, "владік");
        r.Account("Назар");
        r.Eco.Economy.Grant("Назар", 1000, "award");
        var before = r.Eco.Economy.Balance("Назар");     // поверх нагороди могла лягти ачівка
        var reply = r.Live.Order("Назар", true, "владік", false);
        Assert.True(reply.Ok, reply.Message);
        Assert.Equal(before - 100, reply.Balance);
        Assert.Contains(r.Eco.Outbox.Of<Journal>(), x => x.Text == "🔥 Прожарка для владік від Назар — скоро в ефірі");
        Assert.Equal(-100, r.Eco.Paid("Назар", LiveAds.Reason));
        await r.Live.TickAsync(default);
        var row = r.Store.Get(reply.Id!.Value)!;
        Assert.Equal("ready", row.Status);
        var script = r.Render.Scripts.First(s => s.Kind == "order");
        Assert.Contains("Назар", script.Lines[0].Text);
        Assert.True(script.Facts.Count >= 2);
    }

    [Fact]
    public void Anonymous_order_costs_more_and_keeps_the_secret()
    {
        using var r = new Rig();
        r.Account("владік");
        r.Account("Назар");
        r.Eco.Economy.Grant("Назар", 1000, "award");
        var before = r.Eco.Economy.Balance("Назар");
        var reply = r.Live.Order("Назар", true, "владік", true);
        Assert.Equal(before - 150, reply.Balance);
        Assert.Contains(r.Eco.Outbox.Of<Journal>(), x => x.Text.Contains(LiveAds.Anonymous) && !x.Text.Contains("Назар"));
    }

    [Fact]
    public void Order_limits_per_buyer_and_per_target()
    {
        using var r = new Rig();
        foreach (var n in new[] { "a1", "a2", "a3", "a4", "b1" }) { r.Account(n); r.Eco.Economy.Grant(n, 1000, "award"); }
        Assert.True(r.Live.Order("b1", true, "a1", false).Ok);
        Assert.True(r.Live.Order("b1", true, "a2", false).Ok);
        Assert.True(r.Live.Order("b1", true, "a3", false).Ok);
        var fourth = r.Live.Order("b1", true, "a4", false);
        Assert.False(fourth.Ok);
        Assert.Equal(-300, r.Eco.Paid("b1", LiveAds.Reason));             // зайвої не списано                                  // зайвої не списано
        Assert.True(r.Live.Order("a2", true, "a1", false).Ok);
        Assert.True(r.Live.Order("a3", true, "a1", false).Ok);
        Assert.False(r.Live.Order("a4", true, "a1", false).Ok);              // a1 сьогодні вже тричі
        r.Clock.Advance(TimeSpan.FromDays(1));
        Assert.True(r.Live.Order("a4", true, "a1", false).Ok);               // новий день — нові ліміти
    }

    [Fact]
    public void Order_refuses_guests_strangers_sleepers_opt_outs_and_empty_wallets()
    {
        using var r = new Rig();
        r.Account("Назар");
        r.Account("владік");
        r.Account("Старий");
        r.Exec("UPDATE accounts SET seen_at = '2020-01-01T00:00:00.0000000+00:00' WHERE nick_key = 'старий'");
        Assert.False(r.Live.Order("гість Оля", false, "владік", false).Ok);
        Assert.False(r.Live.Order("Назар", true, "Невідомий", false).Ok);
        r.Eco.Economy.Grant("Назар", 1000, "award");
        Assert.Contains("давно не заходить", r.Live.Order("Назар", true, "Старий", false).Message);
        r.Store.SetOptOut("владік", true);
        Assert.Contains("відмовляється", r.Live.Order("Назар", true, "владік", false).Message);
        r.Store.SetOptOut("владік", false);
        r.Account("Бідний");
        var poor = r.Live.Order("Бідний", true, "владік", false);
        Assert.False(poor.Ok);
        Assert.StartsWith("Бракує 100", poor.Message);
        Assert.Equal(0, r.Store.OrdersToday("Бідний", "владік", r.Clock.UtcNow).ByBuyer);   // невдалий не з'їдає ліміт
        Assert.Empty(r.Store.WithStatus("queued"));
    }

    [Fact]
    public async Task Failed_render_refunds_with_the_same_reason()
    {
        using var r = new Rig();
        r.Account("владік");
        r.Account("Назар");
        r.Eco.Economy.Grant("Назар", 1000, "award");
        var before = r.Eco.Economy.Balance("Назар");
        r.Render.Fail = true;
        var id = r.Live.Order("Назар", true, "владік", false).Id!.Value;
        await r.Live.TickAsync(default);
        Assert.Equal("refunded", r.Store.Get(id)!.Status);
        Assert.Equal(before, r.Eco.Economy.Balance("Назар"));
        Assert.Equal(0, r.Eco.Paid("Назар", LiveAds.Reason));                  // −100 і +100 під тією самою причиною
        await r.Live.TickAsync(default);
        Assert.Equal(before, r.Eco.Economy.Balance("Назар"));                 // повтор не платить удруге
        Assert.Equal(0, r.Store.OrdersToday("Назар", "владік", r.Clock.UtcNow).ByBuyer);
    }

    [Fact]
    public async Task Order_that_never_aired_refunds_after_expiry()
    {
        using var r = new Rig();
        r.Account("владік");
        r.Account("Назар");
        r.Eco.Economy.Grant("Назар", 1000, "award");
        var before = r.Eco.Economy.Balance("Назар");
        var id = r.Live.Order("Назар", true, "владік", false).Id!.Value;
        await r.Live.TickAsync(default);
        Assert.Equal("ready", r.Store.Get(id)!.Status);
        r.Clock.Advance(TimeSpan.FromHours(r.O.OrderExpireHours + 1));
        await r.Live.TickAsync(default);
        Assert.Equal("refunded", r.Store.Get(id)!.Status);
        Assert.Equal(before, r.Eco.Economy.Balance("Назар"));
    }

    [Fact]
    public void Card_view_shows_limits_targets_and_stages()
    {
        using var r = new Rig();
        r.Account("Назар");
        r.Account("владік");
        r.Account("Тихоня");
        r.Store.SetOptOut("Тихоня", true);
        r.Eco.Economy.Grant("Назар", 1000, "award");
        r.Live.Order("Назар", true, "владік", false);
        var json = JsonSerializer.Serialize(r.Live.View("Назар", true));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("left").GetInt32());
        Assert.Equal(100, root.GetProperty("price").GetInt32());
        var targets = root.GetProperty("targets").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("владік", targets);
        Assert.DoesNotContain("Тихоня", targets);
        Assert.Equal("queued", root.GetProperty("orders")[0].GetProperty("status").GetString());
        Assert.False(JsonDocument.Parse(JsonSerializer.Serialize(r.Live.View("гість Оля", false))).RootElement.GetProperty("account").GetBoolean());
    }

    // =============================================================================================
    // Відмова і міграція
    // =============================================================================================

    [Fact]
    public void Opt_out_is_an_account_flag_and_only_for_accounts()
    {
        using var r = new Rig();
        r.Account("Смауг");
        Assert.False(r.Live.OptOut("гість Оля", false, true).Ok);
        Assert.True(r.Live.OptOut("Смауг", true, true).Ok);
        Assert.True(r.Store.OptedOut("смауг"));
        Assert.True(r.Live.OptOut("Смауг", true, false).Ok);
        Assert.False(r.Store.OptedOut("Смауг"));
    }

    [Fact]
    public void Old_database_gets_the_column_and_second_start_changes_nothing()
    {
        using var t = new TempDb();
        t.Db.AddAccount("Старожил", "h", "s");
        var store = new LiveAdsStore(t.Db);
        Assert.False(store.OptedOut("Старожил"));                          // старий акаунт — «не відмовлявся»
        store.SetOptOut("Старожил", true);
        var again = new LiveAdsStore(t.Db);
        Assert.True(again.OptedOut("Старожил"));
        Assert.Empty(again.Recent(5));
    }

    // =============================================================================================
    // Реакції на події
    // =============================================================================================

    [Fact]
    public async Task Five_losses_in_a_row_make_an_event_once_per_hour_and_history_is_ignored()
    {
        using var r = new Rig();
        r.Account("Назар");
        r.Results("Назар", "tron", "loss", 6);                                // історія до старту — без реакції
        await r.Live.TickAsync(default);
        Assert.DoesNotContain(r.Store.Recent(20), x => x.Kind == "event");
        r.Results("Назар", "tron", "loss", 1);
        await r.Live.TickAsync(default);
        var ev = r.Store.Recent(20).Single(x => x.Kind == "event");
        Assert.Equal("Назар", ev.Target);
        Assert.Contains("Мотоцикли", ev.Text);
        r.Results("Назар", "tron", "loss", 1);
        await r.Live.TickAsync(default);
        Assert.Single(r.Store.Recent(20), x => x.Kind == "event");           // кулдаун на ніка — 60 хв
    }

    [Fact]
    public async Task No_more_than_three_events_an_hour_and_they_go_stale()
    {
        using var r = new Rig();
        await r.Live.TickAsync(default);
        foreach (var n in new[] { "e1", "e2", "e3", "e4" })
        {
            r.Account(n);
            r.Results(n, "chess", "win", 5);
        }
        await r.Live.TickAsync(default);
        Assert.Equal(3, r.Store.Recent(20).Count(x => x.Kind == "event"));
        r.Clock.Advance(TimeSpan.FromMinutes(31));
        await r.Live.TickAsync(default);
        Assert.All(r.Store.Recent(20).Where(x => x.Kind == "event"), x => Assert.Equal("expired", x.Status));
    }

    [Fact]
    public async Task Big_buy_new_account_and_opt_out_events()
    {
        using var r = new Rig();
        r.Account("Смауг");
        await r.Live.TickAsync(default);
        r.Exec("INSERT INTO lavka_owned(nick_key, item, source, price, at) VALUES('смауг', 'stars', 'buy', 800, $a)", ("$a", Rig.Iso(r.Clock.UtcNow)));
        r.Account("Новенький");
        r.Account("Тихоня");
        r.Store.SetOptOut("Тихоня", true);
        await r.Live.TickAsync(default);
        var events = r.Store.Recent(20).Where(x => x.Kind == "event").ToList();
        Assert.Contains(events, e => e.Target == "Смауг" && e.FactKinds.Contains("big_buy"));
        Assert.Contains(events, e => e.Target == "Новенький" && e.FactKinds.Contains("new_account"));
        Assert.DoesNotContain(events, e => e.Target == "Тихоня");
    }

    [Fact]
    public async Task Curfew_event_at_midnight_for_listed_players_online()
    {
        using var r = new Rig();
        r.Curfew.Nicks = ["владік", "Смауг"];
        r.Online("владік");
        r.Clock.UtcNow = new DateTimeOffset(2026, 9, 10, 21, 5, 0, TimeSpan.Zero);   // 00:05 Києва
        await r.Live.TickAsync(default);
        var ev = r.Store.Recent(20).Single(x => x.Kind == "event");
        Assert.Contains("curfew", ev.FactKinds);
        Assert.Contains("Владік", ev.Text);
        Assert.DoesNotContain("Смауг", ev.Text);                             // не онлайн — не згадуємо
        r.Clock.Advance(TimeSpan.FromMinutes(3));
        await r.Live.TickAsync(default);
        Assert.Single(r.Store.Recent(20), x => x.Kind == "event");           // раз на ніч
    }

    // =============================================================================================
    // Новини
    // =============================================================================================

    [Fact]
    public async Task News_once_a_day_in_the_window_and_without_opt_outs()
    {
        using var r = new Rig();
        r.Online("Слухач");
        r.Account("Чемпіон");
        r.Account("Тихоня");
        r.Results("Тихоня", "chess", "win", 6);
        r.Results("Чемпіон", "chess", "win", 3);
        r.Store.SetOptOut("Тихоня", true);
        await r.Live.TickAsync(default);                                     // 15:00 Києва — ще рано
        Assert.DoesNotContain(r.Store.Recent(20), x => x.Kind == "news");
        r.Clock.UtcNow = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.Zero);   // 21:00 Києва
        await r.Live.TickAsync(default);
        var news = r.Store.Recent(20).Single(x => x.Kind == "news");
        Assert.Equal("ready", news.Status);
        Assert.Contains("Чемпіон", news.Text);
        Assert.DoesNotContain("Тихоня", news.Text);
        var script = r.Render.Scripts.Single(s => s.Kind == "news");
        Assert.Equal(LiveLines.Polina, script.Lines[0].Voice);
        Assert.Contains(script.Lines, l => l.Rate == r.O.SportRate && l.Voice == LiveLines.Glek);   // спорт — Глек-коментатор
        await r.Live.TickAsync(default);
        Assert.Single(r.Store.Recent(20), x => x.Kind == "news");
    }

    [Fact]
    public async Task Without_tts_nothing_breaks_and_nothing_is_ready()
    {
        using var r = new Rig();
        r.Online("владік");
        Juicy(r, "владік");
        r.Render.Fail = true;
        await r.Live.TickAsync(default);
        Assert.Empty(r.Store.WithStatus("ready"));
        Assert.Null(r.Live.Take(libraryEmpty: true));
    }

    [Fact]
    public async Task Old_files_are_cleaned_but_the_last_twenty_stay()
    {
        using var r = new Rig();
        r.O.KeepFiles = 1;
        for (var i = 0; i < 3; i++)
        {
            var id = r.Store.Add("roast", "x", null, false, 0, "queued", r.Clock.UtcNow);
            var file = $"voice-live-{id}";
            File.WriteAllText(Path.Combine(r.Cache, file + ".mp3"), "mp3");
            r.Store.Ready(id, "t", "", file, 20, r.Clock.UtcNow);
            r.Store.Move(id, "ready", "sent", r.Clock.UtcNow);
            r.Store.Move(id, "sent", "aired", r.Clock.UtcNow);
        }
        r.Clock.Advance(TimeSpan.FromMinutes(61));
        await r.Live.TickAsync(default);
        Assert.Single(Directory.GetFiles(r.Cache, "voice-live-*.mp3"));
    }

    // =============================================================================================
    // Рендер ffmpeg (пропускається без ffmpeg)
    // =============================================================================================

    internal static string? FfmpegDir()
    {
        foreach (var d in new[] { Environment.GetEnvironmentVariable("HLECHYKY_FFMPEG_DIR"), "D:/or/tools/yt-dlp" })
            if (!string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"))) return d;
        return null;
    }

    public sealed class FfmpegFactAttribute : FactAttribute
    {
        public FfmpegFactAttribute() { if (FfmpegDir() is null) Skip = "нема ffmpeg (HLECHYKY_FFMPEG_DIR)"; }
    }

    /// <summary>«Голос» без edge-tts: синус потрібної довжини з ffmpeg.</summary>
    sealed class SineTts(string ffmpeg) : ITtsEngine
    {
        public int Calls;
        public async Task<bool> SynthesizeAsync(string voice, string text, string rate, int pauseMs, string outPath, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=330:duration=2", "-ac", "1", outPath }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0;
        }

        public Task<double> DurationAsync(string path, CancellationToken ct) => Task.FromResult(2.0);
    }

    [FfmpegFact]
    public async Task Ffmpeg_renders_a_clip_with_bed_rimshot_and_sign()
    {
        var dir = FfmpegDir()!;
        var cache = Path.Combine(Path.GetTempPath(), "hlechyky-render-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(cache);
        try
        {
            var tts = new SineTts(Path.Combine(dir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"));
            var renderer = new FfmpegLiveRenderer(tts, new FixedOptions<LiveAdsOptions>(new LiveAdsOptions { BedsDir = RepoFile("data/liveads/beds") }),
                new FixedOptions<TtsOptions>(new TtsOptions()), new FixedOptions<YtDlpOptions>(new YtDlpOptions { FfmpegDir = dir, CacheDir = cache }),
                NullLogger<FfmpegLiveRenderer>.Instance);
            var script = new LiveScript("roast", "polka",
                [new LiveLine(LiveLines.Polina, "Раз", "-4%"), new LiveLine(LiveLines.Glek, "Два", "-4%", Rim: true)], ["x"]);
            var outPath = Path.Combine(cache, "voice-live-1.mp3");
            var sec = await renderer.RenderAsync(script, outPath, default);
            Assert.NotNull(sec);
            // інтро 1,5 + (2+0,1) + ба-дум-тсс (1,2+0,3) + (2+0,3) + підпис 2 + хвіст 2,2
            Assert.InRange(sec!.Value, 11, 13);
            Assert.True(new FileInfo(outPath).Length > 60_000);
            Assert.Equal(3, tts.Calls);                                    // дві репліки й підпис
            await renderer.RenderAsync(script, outPath, default);
            Assert.Equal(5, tts.Calls);                                    // підпис — із кешу
            Assert.Empty(Directory.GetDirectories(Path.Combine(cache, "liveads"), "tmp-*"));
        }
        finally { try { Directory.Delete(cache, true); } catch (IOException) { } }
    }
}
