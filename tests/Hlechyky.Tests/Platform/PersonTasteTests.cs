using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Етап 2 поліпшення порад Глека (25.10): смак тих, хто зараз слухає. З бази за 07.09–25.10: поки слухає владік, він
/// скіпає 1 % треків Глека, Smaug — 52 %, а смак кімнати на 58 % був владіковим; Smaug скіпала й третину чужих
/// замовлень — такі скіпи раніше ніде не лягали, крім рядка «нік скіпає …» у Журналі.
/// </summary>
public class PersonTasteTests
{
    static TrackInfo T(string id, string artist, string title) =>
        new(id, title, artist, 200, null, "https://music.youtube.com/watch?v=" + id, null);

    static void Request(TempDb db, TrackInfo t, string nick)
    {
        db.Db.UpsertTrack(t);
        db.Db.EndPlay(db.Db.StartPlay(t.Id, "user", nick, null, null), skipped: false);
    }

    static void SkipAuto(TempDb db, TrackInfo t, string nick)
    {
        db.Db.UpsertTrack(t);
        db.Db.EndPlay(db.Db.StartPlay(t.Id, "autodj", null, "схоже на щось", null), skipped: true, by: nick);
    }

    static readonly string[] VladRock = ["Linkin Park", "Nirvana", "Green Day", "AC/DC", "The Offspring", "Limp Bizkit",
        "System Of A Down", "Red Hot Chili Peppers", "The Doors", "Radiohead", "Deep Purple", "Eminem"];
    static readonly string[] SmaugPop = ["Sandykit", "Zwyntar", "Діти Інженерів", "ТІК", "Бумбокс", "KLER", "Wellboy",
        "DOROFEEVA", "Курган", "Tvorchi", "OTOY", "Мотор'Ролла"];

    /// <summary>Двоє з акаунтами й різним смаком: владік — латиничний рок, Smaug — своє, і вона двічі скіпнула Kalush.</summary>
    static RoomTaste Rig(TempDb db)
    {
        db.Db.AddAccount("владік", "h", "s");
        db.Db.AddAccount("Smaug", "h", "s");
        var n = 0;
        foreach (var a in VladRock) Request(db, T($"uV{n++:D9}", a, "Song " + n), "владік");
        foreach (var a in SmaugPop) Request(db, T($"uS{n++:D9}", a, "Пісня " + n), "Smaug");
        SkipAuto(db, T("uKalush0001", "Kalush", "Стефанія"), "Smaug");
        SkipAuto(db, T("uKalush0002", "Kalush", "Йо-йо"), "Smaug");
        return new RoomTaste(db.Db, new FixedOptions<AutoDjOptions>(new AutoDjOptions()));
    }

    [Fact]
    public void Each_person_has_own_artists_script_and_dislikes()
    {
        using var db = new TempDb();
        var taste = Rig(db);

        var vlad = taste.People["владік"];
        var smaug = taste.People["smaug"];
        Assert.True(vlad.HasTaste && smaug.HasTaste);
        Assert.Contains("linkin park", vlad.Artists);
        Assert.DoesNotContain("linkin park", smaug.Artists);
        Assert.Equal("lat", vlad.Script);
        Assert.Equal("cyr", smaug.Script);
        Assert.Contains("kalush", smaug.Disliked);
        Assert.Contains(AutoDj.KeyOf("Kalush", "Стефанія"), smaug.SkippedSongs);
        Assert.Empty(vlad.Disliked);
    }

    [Fact]
    public void Skipping_songs_of_an_artist_you_like_is_not_disliking_the_artist()
    {
        var now = DateTimeOffset.UtcNow;
        var jerry1 = T("uJerry00001", "Jerry Heil", "Охрана, отмєна");
        var jerry2 = T("uJerry00002", "Jerry Heil", "#ВІДЛІТАЮ");
        var jerry3 = T("uJerry00003", "Jerry Heil", "Ой, у гаю");
        var people = RoomTaste.BuildPeople([], [(jerry3, "Smaug", now)], [(jerry1, "Smaug", now), (jerry2, "Smaug", now), (jerry3, "Smaug", now)], now);

        var smaug = people["smaug"];
        Assert.DoesNotContain("jerry heil", smaug.Disliked);
        Assert.Contains(AutoDj.KeyOf("Jerry Heil", "Охрана, отмєна"), smaug.SkippedSongs);
        Assert.DoesNotContain(AutoDj.KeyOf("Jerry Heil", "Ой, у гаю"), smaug.SkippedSongs); // лайкнула — скіп був під настрій
    }

    [Fact]
    public void Old_skip_still_says_dislike_but_no_longer_bans_the_song()
    {
        var now = DateTimeOffset.UtcNow;
        var people = RoomTaste.BuildPeople([], [],
            [(T("uK000000001", "Kalush", "Стефанія"), "Smaug", now.AddDays(-45)), (T("uK000000002", "Kalush", "Йо-йо"), "Smaug", now.AddDays(-40))], now);
        Assert.Contains("kalush", people["smaug"].Disliked);
        Assert.Empty(people["smaug"].SkippedSongs);
    }

    [Fact]
    public void Multiplier_follows_whoever_is_listening()
    {
        using var db = new TempDb();
        var taste = Rig(db);

        var rockForVlad = taste.Multiplier("Linkin Park", "Numb", 1, ["владік"]);
        var rockForSmaug = taste.Multiplier("Linkin Park", "Numb", 1, ["Smaug"]);
        var ownForSmaug = taste.Multiplier("Артем Пивоваров", "Очі", 1, ["Smaug"]);
        var ownForVlad = taste.Multiplier("Артем Пивоваров", "Очі", 1, ["владік"]);

        Assert.True(rockForVlad > 2, $"владікові його ж рок ×{rockForVlad:F2}");
        Assert.True(rockForSmaug < 0.6, $"Smaug латиничний рок не свій ×{rockForSmaug:F2}");
        Assert.True(ownForSmaug > ownForVlad * 2, $"своє для Smaug ×{ownForSmaug:F2} проти владікових ×{ownForVlad:F2}");
        var both = taste.Multiplier("Linkin Park", "Numb", 1, ["владік", "Smaug"]);
        Assert.InRange(both, rockForSmaug, rockForVlad);
    }

    [Fact]
    public void Artist_a_listener_keeps_skipping_is_damped_only_while_they_listen()
    {
        using var db = new TempDb();
        var taste = Rig(db);

        var kalushForSmaug = taste.Multiplier("Kalush", "Нова", 1, ["Smaug"]);
        var strangerForSmaug = taste.Multiplier("Артем Пивоваров", "Очі", 1, ["Smaug"]);
        Assert.Equal(strangerForSmaug * 0.5, kalushForSmaug, 3);

        var kalushForVlad = taste.Multiplier("Kalush", "Нова", 1, ["владік"]);
        var strangerForVlad = taste.Multiplier("Артем Пивоваров", "Очі", 1, ["владік"]);
        Assert.Equal(strangerForVlad, kalushForVlad, 3);
    }

    [Fact]
    public void Guests_and_nobody_mean_the_rooms_taste()
    {
        using var db = new TempDb();
        var taste = Rig(db);
        var room = taste.Multiplier("Linkin Park", "Numb", 1);
        Assert.Equal(room, taste.Multiplier("Linkin Park", "Numb", 1, []), 3);
        Assert.Equal(room, taste.Multiplier("Linkin Park", "Numb", 1, ["гість .", "Клодік"]), 3);
        // акаунт без жодних слідів «голосує» кімнатним смаком
        db.Db.AddAccount("Новенький", "h", "s");
        Assert.Equal(room, taste.Multiplier("Linkin Park", "Numb", 1, ["Новенький"]), 3);
    }

    [Fact]
    public void Songs_skipped_by_those_listening_are_not_offered()
    {
        using var db = new TempDb();
        var taste = Rig(db);
        Assert.Contains(AutoDj.KeyOf("Kalush", "Йо-йо"), taste.SkippedBy(["Smaug", "владік"]));
        Assert.Empty(taste.SkippedBy(["владік"]));
        Assert.Empty(taste.SkippedBy(null));
    }

    [Fact]
    public void Anchor_comes_from_those_listening()
    {
        using var db = new TempDb();
        var taste = Rig(db);
        for (var i = 0; i < 8; i++)
            Assert.Contains(taste.Anchor(new HashSet<string>(), ["Smaug"])!.Artist, SmaugPop);
        for (var i = 0; i < 8; i++)
            Assert.Contains(taste.Anchor(new HashSet<string>(), ["владік", "гість ."])!.Artist, VladRock);
    }

    // ---------- хто скіпнув ----------

    [Fact]
    public void Skip_remembers_who_and_is_read_back()
    {
        using var db = new TempDb();
        db.Db.AddAccount("Smaug", "h", "s");
        SkipAuto(db, T("uKalush0001", "Kalush", "Стефанія"), "Smaug");
        var skip = Assert.Single(db.Db.SkipsSince(DateTimeOffset.UtcNow.AddHours(-1)));
        Assert.Equal("Smaug", skip.Nick);
        Assert.Equal("uKalush0001", skip.Track.Id);
    }

    [Fact]
    public void Old_skips_get_their_skipper_from_the_journal()
    {
        using var tmp = new TempDb();
        var t0 = new DateTimeOffset(2026, 10, 20, 18, 0, 0, TimeSpan.Zero);
        string At(double s) => t0.AddSeconds(s).ToString("o");
        tmp.Db.UpsertTrack(T("uTrack00001", "Kalush", "Стефанія"));
        void PlayRow(double from, double to, int skipped) =>
            tmp.Db.Exec("INSERT INTO plays(track_id, source, started_at, ended_at, skipped) VALUES('uTrack00001', 'autodj', $a, $b, $s)",
                ("$a", At(from)), ("$b", At(to)), ("$s", skipped));
        void Journal(string text, double at) =>
            tmp.Db.Exec("INSERT INTO chat(nick, text, kind, created_at, topic) VALUES('Глечики', $t, 'system', $at, 'radio')", ("$t", text), ("$at", At(at)));
        PlayRow(0, 30, 1);      // Smaug скіпнула на 30-й секунді
        PlayRow(31, 100, 0);    // дограло
        PlayRow(101, 150, 1);   // владік скіпнув
        Journal("Smaug скіпає Kalush — Стефанія", 30.2);
        Journal("владік скіпає Kalush — Стефанія", 150.4);
        Journal("микола скіпає щось давнє", 500);   // програвання до нього нема — нікому не приписуємо
        tmp.Db.Exec("DELETE FROM migrations WHERE key = 'plays-skipped-by-2026-10'");

        var db = new Db(tmp.Path); // міграція біжить при відкритті бази

        var who = db.With(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT skipped_by FROM plays ORDER BY id";
            using var r = cmd.ExecuteReader();
            var list = new List<string?>();
            while (r.Read()) list.Add(r.IsDBNull(0) ? null : r.GetString(0));
            return list;
        });
        Assert.Equal(["Smaug", null, "владік"], who);
    }
}
