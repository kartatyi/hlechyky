using System.Text.Json;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// «Зберегти плейлистом» з посилання (POST /api/album/playlist): своя назва, лише відмічені треки, дописати в
/// наявний плейлист — і все як було, коли нічого з цього не передали.
/// </summary>
public class AlbumPlaylistTests
{
    static SearchResult Yt(string id, string title) => new(id, title, "Океан Ельзи", "Земля", 200, null);

    /// <summary>Альбом «Земля»: null — трек, якого не знайшлося в YouTube Music.</summary>
    static Album Earth(params SearchResult?[] matches) => new("ytmusic:album:X", "ytmusic", "album", "Земля", "Океан Ельзи", "2013", null,
        "https://music.youtube.com/browse/X", matches.Select((m, i) => new AlbumTrack(i + 1, m?.Title ?? "Не знайдена", "Океан Ельзи", 200, m)).ToList());

    static readonly Album Sample = Earth(Yt("a1", "Мить"), null, Yt("a3", "Обійми"));

    static Albums Service(TempDb db) => new(new YtMusicClient(NullLogger<YtMusicClient>.Instance), db.Db, NullLogger<Albums>.Instance);

    static string[] TrackIds(TempDb db, long playlist) => db.Db.PlaylistTracks(playlist).Select(x => x.Track.Id).ToArray();

    [Fact]
    public void Without_the_new_fields_it_saves_as_before()
    {
        using var db = new TempDb();
        var albums = Service(db);

        var r = albums.SaveAsPlaylist(Sample, "Оля");
        Assert.True(r.Ok);
        Assert.Equal("Океан Ельзи — Земля", db.Db.GetPlaylist(r.Id)!.Name);
        Assert.Equal(["a1", "a3"], TrackIds(db, r.Id));
        Assert.Equal("Плейлист «Океан Ельзи — Земля»: 2 треки", r.Message);

        // удруге — той самий плейлист, без двійника
        var again = albums.SaveAsPlaylist(Sample, "Петро");
        Assert.Equal(r.Id, again.Id);
        Assert.Equal("У плейлисті «Океан Ельзи — Земля» це все вже є", again.Message);
        Assert.Single(db.Db.Playlists());

        var nothing = albums.SaveAsPlaylist(Earth(null, null), "Оля");
        Assert.False(nothing.Ok);
        Assert.Equal("Жоден трек не знайшовся в YouTube Music — нема що зберегти", nothing.Message);
    }

    [Fact]
    public void Own_name_is_trimmed_and_cut_to_forty()
    {
        using var db = new TempDb();
        var albums = Service(db);

        var own = albums.SaveAsPlaylist(Sample, "Оля", "  Дорога додому  ");
        Assert.Equal("Дорога додому", db.Db.GetPlaylist(own.Id)!.Name);
        Assert.Equal("Плейлист «Дорога додому»: 2 треки", own.Message);

        var longName = albums.SaveAsPlaylist(Sample, "Оля", new string('я', 50));
        var name = db.Db.GetPlaylist(longName.Id)!.Name;
        Assert.True(name.Length <= 40);
        Assert.EndsWith("…", name);

        // порожня назва — як без неї
        var blank = albums.SaveAsPlaylist(Sample, "Оля", "   ");
        Assert.Equal("Океан Ельзи — Земля", db.Db.GetPlaylist(blank.Id)!.Name);
    }

    [Fact]
    public void Ids_keep_only_the_ticked_tracks()
    {
        using var db = new TempDb();
        var albums = Service(db);

        var r = albums.SaveAsPlaylist(Sample, "Оля", "Вибране", ["a3", "нема-такого"]);
        Assert.True(r.Ok);
        Assert.Equal(["a3"], TrackIds(db, r.Id));
        Assert.Equal("Плейлист «Вибране»: 1 трек", r.Message);

        var none = albums.SaveAsPlaylist(Sample, "Оля", "Порожнє", ["нема-такого"]);
        Assert.False(none.Ok);
        Assert.Equal("Не вибрано жодного треку", none.Message);
        Assert.DoesNotContain(db.Db.Playlists(), p => p.Name == "Порожнє");
    }

    [Fact]
    public void Playlist_id_appends_to_an_existing_playlist()
    {
        using var db = new TempDb();
        var albums = Service(db);
        var road = db.Db.CreatePlaylist("Дорога", "Петро");
        Radio.Track(db.Db, "old");
        db.Db.AddToPlaylist(road, "old", "Петро");

        var r = albums.SaveAsPlaylist(Sample, "Оля", "ця назва ні до чого", playlistId: road);
        Assert.True(r.Ok);
        Assert.Equal(road, r.Id);
        Assert.Equal(["old", "a1", "a3"], TrackIds(db, road));
        Assert.Equal("У плейлист «Дорога» додано ще 2 треки", r.Message);
        Assert.DoesNotContain(db.Db.Playlists(), p => p.Name == "ця назва ні до чого");

        var only = albums.SaveAsPlaylist(Earth(Yt("a1", "Мить"), Yt("a4", "Не питай")), "Оля", ids: ["a1", "a4"], playlistId: road);
        Assert.Equal("У плейлист «Дорога» додано ще 1 трек", only.Message);

        var missing = albums.SaveAsPlaylist(Sample, "Оля", playlistId: 999);
        Assert.False(missing.Ok);
        Assert.Equal("Нема такого плейлиста", missing.Message);
        Assert.Equal(0, missing.Id);
    }

    [Fact]
    public void Request_body_takes_the_new_fields_and_old_bodies_still_fit()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var full = JsonSerializer.Deserialize<Endpoints.AlbumRequest>(
            """{ "url": "https://music.youtube.com/browse/MPREb_x", "name": "Дорога", "ids": ["a1"], "playlistId": 3 }""", web)!;
        Assert.Equal("Дорога", full.Name);
        Assert.Equal(["a1"], full.Ids!);
        Assert.Equal(3, full.PlaylistId);

        var old = JsonSerializer.Deserialize<Endpoints.AlbumRequest>("""{ "url": "https://music.youtube.com/browse/MPREb_x", "shuffle": true }""", web)!;
        Assert.True(old.Shuffle);
        Assert.Null(old.Name);
        Assert.Null(old.Ids);
        Assert.Null(old.PlaylistId);
    }
}
