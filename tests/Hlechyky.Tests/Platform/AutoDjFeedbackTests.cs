using Hlechyky.Tests.Support;
using Row = Hlechyky.Db.DjFeedbackRow;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Етап 1 поліпшення порад Глека (25.10). Цифри — з бази за 07.09–25.10: 85 % відмов «Не те» і швидких скіпів дала
/// одна людина, 25 із 60 найчастіше відкинутих артистів вона ж сама лайкає, 321 її скіп — серією, а 24.10 гість за
/// вечір закинув 96 треків, і всі 15 якорів стали його роком 80-х від каналів на кшталт «jimmej955».
/// </summary>
public class AutoDjFeedbackTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    static readonly AutoDjOptions O = new() { FeedbackHours = 48, SongFeedbackDays = 30, BurstSeconds = 60 };

    static Row R(string artist, string title, string nick, double minutesAgo, string? seed = null, string? id = null) =>
        new(AutoDj.ArtistKey(artist), seed, AutoDj.KeyOf(artist, title), id, nick, Now.AddMinutes(-minutesAgo));

    // ---------- відмова — про пісню, а не вирок артисту ----------

    [Fact]
    public void Rejected_song_is_off_but_its_artist_only_dips()
    {
        var st = AutoDj.CountStrikes([R("Jerry Heil", "Охрана, отмєна", "Smaug", 30, id: "uJerry00001")], null, Now, O);

        Assert.True(st.Song(AutoDj.KeyOf("Jerry Heil", "Охрана, отмєна"), null));
        Assert.True(st.Song("будь-що", "uJerry00001"), "та сама пісня під своїм id");
        Assert.False(st.Song(AutoDj.KeyOf("Jerry Heil", "#ВІДЛІТАЮ"), null), "інша пісня того ж артиста — можна");
        Assert.Equal(AutoDj.ArtistStrike, st.Artists[AutoDj.ArtistKey("Jerry Heil")], 3);
        Assert.True(st.Artists[AutoDj.ArtistKey("Jerry Heil")] < 2, "однієї відмови замало, щоб прибрати артиста");
    }

    [Fact]
    public void Artist_goes_only_after_four_separate_rejections()
    {
        Row[] rows =
        [
            R("Monatik", "Вітаміни", "Smaug", 600), R("Monatik", "Кружляю", "Smaug", 400),
            R("Monatik", "Зорепад", "Smaug", 200), R("Monatik", "Love It Ритм", "Smaug", 10),
        ];
        Assert.Equal(1.5, AutoDj.CountStrikes(rows[..3], null, Now, O).Artists["monatik"], 3);
        Assert.Equal(2, AutoDj.CountStrikes(rows, null, Now, O).Artists["monatik"], 3);
    }

    [Fact]
    public void Song_rejected_twice_stays_off_for_a_month_once_only_for_two_days()
    {
        var twice = AutoDj.CountStrikes([R("ДК Енергетик", "Енергетик", "Smaug", 60 * 24 * 10), R("ДК Енергетик", "Енергетик", "владік", 60 * 24 * 5)], null, Now, O);
        var once = AutoDj.CountStrikes([R("Скрябін", "Сам собі країна", "Smaug", 60 * 24 * 5)], null, Now, O);

        Assert.Contains(AutoDj.KeyOf("ДК Енергетик", "Енергетик"), twice.Songs);
        Assert.Empty(once.Songs);
        Assert.False(twice.Artists.ContainsKey(AutoDj.ArtistKey("ДК Енергетик")), "артиста пам'ятаємо лише FeedbackHours");
    }

    [Fact]
    public void Burst_of_skips_is_one_mood_signal_not_five_verdicts()
    {
        // Smaug перебирає: п'ять скіпів за дві хвилини, усе від одного сіда
        Row[] burst =
        [
            R("Kalush", "Стефанія", "Smaug", 10.0, "uSeedAnchor"), R("Dzidzio", "Я і Сара", "Smaug", 9.6, "uSeedAnchor"),
            R("Alyona Alyona", "Рибки", "Smaug", 9.2, "uSeedAnchor"), R("Dorofeeva", "Хартбіт", "Smaug", 8.8, "uSeedAnchor"),
            R("Monatik", "Вітаміни", "Smaug", 8.4, "uSeedAnchor"),
        ];
        var st = AutoDj.CountStrikes(burst, null, Now, O);

        Assert.Equal(AutoDj.ArtistStrike, st.Artists.Values.Sum(), 3);
        Assert.All(st.Artists.Values, v => Assert.Equal(AutoDj.ArtistStrike / 5, v, 3));
        Assert.Equal(2, st.Seeds["uSeedAnchor"]);
        Assert.Equal(5, st.Songs.Count);
    }

    [Fact]
    public void Skips_far_apart_are_separate_signals()
    {
        Row[] rows = [R("Kalush", "Стефанія", "Smaug", 30, "uSeedA"), R("Dzidzio", "Я і Сара", "Smaug", 20, "uSeedA")];
        var st = AutoDj.CountStrikes(rows, null, Now, O);
        Assert.Equal(AutoDj.ArtistStrike, st.Artists["kalush"], 3);
        Assert.Equal(2, st.Seeds["uSeedA"]);
    }

    [Fact]
    public void Only_those_listening_now_shape_the_picks()
    {
        Row[] rows =
        [
            R("Jerry Heil", "Охрана, отмєна", "Smaug", 30, "uSeedS"),
            R("Linkin Park", "Numb", "владік", 120, "uSeedV"),
            new("legacy", "uSeedOld", null, null, null, Now.AddMinutes(-40)),
        ];

        var vladAlone = AutoDj.CountStrikes(rows, ["Владік"], Now, O);
        Assert.False(vladAlone.Artists.ContainsKey(AutoDj.ArtistKey("Jerry Heil")), "Smaug нема — її відмови не тиснуть");
        Assert.Contains("linkin park", vladAlone.Artists.Keys);
        Assert.Contains("legacy", vladAlone.Artists.Keys);
        Assert.DoesNotContain(AutoDj.KeyOf("Jerry Heil", "Охрана, отмєна"), vladAlone.Songs);

        var both = AutoDj.CountStrikes(rows, ["владік", "Smaug"], Now, O);
        Assert.Contains(AutoDj.KeyOf("Jerry Heil", "Охрана, отмєна"), both.Songs);

        var nobody = AutoDj.CountStrikes(rows, [], Now, O);
        Assert.Equal(3, nobody.Artists.Count);
    }

    [Fact]
    public void Rejection_is_stored_with_song_and_track_and_read_back()
    {
        using var db = new TempDb();
        db.Db.AddDjFeedback("jerry heil", "uSeed00001", "skip", "Smaug", AutoDj.KeyOf("Jerry Heil", "Охрана, отмєна"), "uJerry00001");

        var row = Assert.Single(db.Db.DjFeedbackSince(DateTimeOffset.UtcNow.AddHours(-1)));
        Assert.Equal("jerry heil|охрана отмєна", row.Song);
        Assert.Equal("uJerry00001", row.TrackId);
        Assert.Equal("Smaug", row.Nick);
        Assert.Equal("uSeed00001", row.SeedId);
    }

    // ---------- смак без гостей і ботів ----------

    static TrackInfo T(string id, string artist, string title) =>
        new(id, title, artist, 200, null, "https://www.youtube.com/watch?v=" + id, null);

    static void Request(TempDb db, TrackInfo t, string nick, bool skipped = false)
    {
        db.Db.UpsertTrack(t);
        db.Db.EndPlay(db.Db.StartPlay(t.Id, "user", nick, null, null), skipped);
    }

    static RoomTaste Taste(TempDb db) => new(db.Db, new FixedOptions<AutoDjOptions>(new AutoDjOptions()));

    [Fact]
    public void Guest_playlist_and_bots_do_not_become_the_rooms_taste()
    {
        using var db = new TempDb();
        db.Db.AddAccount("владік", "h", "s");
        Request(db, T("uVladik0001", "SadSvit", "Касета"), "владік");
        Request(db, T("uVladik0002", "Жадан і Собаки", "Мальви"), "владік");
        // гість закидає плейлист, Клодік (бот) — своє
        for (var i = 0; i < 20; i++) Request(db, T($"uGuest{i:D5}", "jimmej955", $"Bob Seger- Song {i}"), "гість .");
        Request(db, T("uClaude0001", "Bob Dominator", "Blood Runs Wild"), "Клодік");
        var taste = Taste(db);

        for (var i = 0; i < 6; i++)
            Assert.Contains(taste.Anchor(new HashSet<string>())!.Artist, new[] { "SadSvit", "Жадан і Собаки" });
        Assert.DoesNotContain("jimmej955", taste.Current.Artists);
        Assert.DoesNotContain("bob seger", taste.Current.Artists);
        Assert.DoesNotContain(AutoDj.ArtistKey("Bob Dominator"), taste.Current.Artists);
        Assert.True(taste.IsKnown("SadSvit"));
    }

    [Fact]
    public void Skipped_request_is_not_taste()
    {
        using var db = new TempDb();
        db.Db.AddAccount("владік", "h", "s");
        Request(db, T("uVladik0001", "SadSvit", "Касета"), "владік");
        Request(db, T("uVladik0002", "Alena Omargalieva", "Щось"), "владік", skipped: true);
        var taste = Taste(db);

        Assert.False(taste.IsKnown("Alena Omargalieva"));
        Assert.Equal("SadSvit", taste.Anchor(new HashSet<string>())!.Artist);
        Assert.Equal("SadSvit", taste.Anchor(new HashSet<string>())!.Artist);
    }

    [Fact]
    public void One_nick_takes_at_most_its_share_and_the_rest_comes_from_others()
    {
        var rows = Enumerable.Range(0, 20).Select(i => (T($"uV{i:D9}", "Linkin Park", $"Song {i}"), "владік"))
            .Append((T("uSmaug00001", "Zwyntar", "Потяг"), "Smaug"))
            .Append((T("uSmaug00002", "Sandykit", "Пісня"), "smaug"))
            .ToList();

        var fair = RoomTaste.Fair(rows, 10);

        Assert.Equal(3, fair.Count(t => t.Artist == "Linkin Park")); // ceil(10 × 0,3)
        Assert.Equal(2, fair.Count(t => t.Artist != "Linkin Park"));   // Smaug і smaug — той самий нік
        Assert.Equal("uV000000000", fair[0].Id);                      // свіжіше лишається вище
    }

    [Theory]
    [InlineData("jimmej955", "Bob Seger- Turn the Page", "Bob Seger", "Turn the Page")]
    [InlineData("ArhivariusRU", "Slade - My Oh My", "Slade", "My Oh My")]
    [InlineData("Pepita Alebrije", "Kiss- I Was Made For Lovin' You (Scooby-Doo)", "Kiss", "I Was Made For Lovin' You (Scooby-Doo)")]
    [InlineData("Moertler", "Frank Zappa- Bobby Brown  *Official Video*", "Frank Zappa", "Bobby Brown  *Official Video*")]
    // справжній артист — не чіпаємо
    [InlineData("Queen", "Queen - Bohemian Rhapsody", "Queen", "Queen - Bohemian Rhapsody")]
    [InlineData("Океан Ельзи", "Місто весни - Live", "Океан Ельзи", "Місто весни - Live")]
    [InlineData("The Beatles", "Let It Be - Remastered 2009", "The Beatles", "Let It Be - Remastered 2009")]
    [InlineData("Скрябін", "Старі фотографії - feat. Кузьма", "Скрябін", "Старі фотографії - feat. Кузьма")]
    [InlineData("Jay-Z", "Jay-Z - 99 Problems", "Jay-Z", "Jay-Z - 99 Problems")]
    [InlineData("Robin Moran", "AC/DC \"Let There Be Rock\": Retuned A-440 Version", "Robin Moran", "AC/DC \"Let There Be Rock\": Retuned A-440 Version")]
    [InlineData("SadSvit", "Касета", "SadSvit", "Касета")]
    public void Uploader_channel_is_replaced_by_the_artist_from_the_title(string artist, string title, string wantArtist, string wantTitle) =>
        Assert.Equal((wantArtist, wantTitle), RoomTaste.CreditOf(artist, title));

    // ---------- джерело поради ----------

    [Fact]
    public void Play_remembers_where_the_dj_found_the_track()
    {
        using var db = new TempDb();
        var t = T("uPick000001", "Zwyntar", "Потяг на Південь");
        db.Db.UpsertTrack(t);
        var pid = db.Db.StartPlay(t.Id, "autodj", null, "схоже на SadSvit — Касета", null, "lastfm+related", "uSeed00001");

        var (src, seed) = db.Db.With(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT pick_source, seed_id FROM plays WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", pid);
            using var r = cmd.ExecuteReader();
            r.Read();
            return (r.GetString(0), r.GetString(1));
        });
        Assert.Equal("lastfm+related", src);
        Assert.Equal("uSeed00001", seed);
    }
}
