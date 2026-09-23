using Hlechyky.Tests.Support;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Tests.Platform;

/// <summary>«Улюблене»: усі лайкнуті треки без стелі, при кожному — хто й коли поставив ❤.</summary>
public class LikesTests
{
    static readonly DateTimeOffset T0 = new(2026, 9, 7, 17, 0, 0, TimeSpan.Zero);

    /// <summary>Лайк із заданим часом: через ToggleLike час — «зараз», і два поспіль можуть збігтися.</summary>
    static void Like(TempDb db, string trackId, string nick, int minute)
    {
        using var c = new SqliteConnection($"Data Source={db.Path}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO likes(track_id, nick, created_at) VALUES($t, $n, $at)";
        cmd.Parameters.AddWithValue("$t", trackId);
        cmd.Parameters.AddWithValue("$n", nick);
        cmd.Parameters.AddWithValue("$at", T0.AddMinutes(minute).ToString("o"));
        cmd.ExecuteNonQuery();
    }

    static void Track(TempDb db, string id) =>
        db.Db.UpsertTrack(new TrackInfo(id, "Пісня " + id, "Гурт", 180, null, "https://music.youtube.com/watch?v=" + id, null));

    [Fact]
    public void Old_likes_survive_a_flood_of_new_ones()
    {
        using var db = new TempDb();
        Track(db, "kanives");
        Like(db, "kanives", "владік", 0);
        for (var i = 0; i < 250; i++)
        {
            Track(db, "s" + i);
            Like(db, "s" + i, "Smaug", 10 + i);
        }

        var all = db.Db.LikedTracksDetailed();

        Assert.Equal(251, all.Count);
        Assert.Equal("s249", all[0].Track.Id);
        Assert.Equal("kanives", all[^1].Track.Id);
        Assert.Equal([("владік", T0)], all[^1].Likes);
    }

    [Fact]
    public void Likers_come_oldest_first_and_track_order_follows_the_newest_like()
    {
        using var db = new TempDb();
        Track(db, "a");
        Track(db, "b");
        Like(db, "a", "Оля", 1);
        Like(db, "b", "Петро", 2);
        Like(db, "a", "Smaug", 3);

        var all = db.Db.LikedTracksDetailed();

        Assert.Equal(["a", "b"], all.Select(x => x.Track.Id));
        Assert.Equal(["Оля", "Smaug"], all[0].Likes.Select(x => x.Nick));
        Assert.Equal([T0.AddMinutes(1), T0.AddMinutes(3)], all[0].Likes.Select(x => x.At));
    }

    [Fact]
    public void Cap_limits_tracks_but_keeps_all_their_likers()
    {
        using var db = new TempDb();
        Track(db, "a");
        Track(db, "b");
        Like(db, "a", "Оля", 1);
        Like(db, "b", "Петро", 2);
        Like(db, "a", "Smaug", 3);

        var top = Assert.Single(db.Db.LikedTracksDetailed(1));

        Assert.Equal("a", top.Track.Id);
        Assert.Equal(["Оля", "Smaug"], top.Likes.Select(x => x.Nick));
    }
}
