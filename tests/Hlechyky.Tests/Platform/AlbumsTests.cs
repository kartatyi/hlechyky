using System.Text.Json.Nodes;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Альбоми з посилань: що вважаємо альбомом чи плейлистом, як Spotify-треки лягають на треки того самого
/// альбому в YouTube Music і як із видачі пошуку вибирається та сама пісня, а не ремікс чи кавер.
/// Фікстури — справжні відповіді Spotify і YouTube Music (вересень 2026), обрізані до того, що ми читаємо.
/// </summary>
public class AlbumsTests
{
    static SpotifyResolver.Track Sp(string title, string artist, int sec) => new("sp" + title.GetHashCode(), title, artist, sec, null);
    static SearchResult Yt(string id, string title, string artist, int sec) => new(id, title, artist, null, sec, null);
    static string Fixture(string name) => File.ReadAllText(Paths.Resolve($"tests/Hlechyky.Tests/Fixtures/albums/{name}"));
    static JsonNode Json(string name) => JsonNode.Parse(Fixture(name))!;

    [Fact]
    public void Spotify_album_embed_gives_the_tracklist_without_podcasts()
    {
        var list = SpotifyResolver.ParseList(Fixture("spotify-embed-album.html"), "album", "7FepjkQJLHjbf0Tt9cooe1")!;
        Assert.Equal("Power To The People (New York City - The Ultimate Mixes)", list.Name);
        Assert.Equal("John Lennon", list.Owner);
        Assert.Equal("2025", list.Year);
        Assert.Contains("ab67616d00001e02", list.ThumbUrl);   // 300 px, а не 64 чи 640
        Assert.Equal(["New York City - Ultimate Mix", "Sisters, O Sisters - Ultimate Mix", "Attica State - Ultimate Mix"], list.Tracks.Select(t => t.Title));
        Assert.Equal(280, list.Tracks[0].DurationSec);
        Assert.Equal("6UWIAESo4iDnQD2eXEVzZB", list.Tracks[0].Id);
        // Spotify ставить між виконавцями нерозривний пробіл; без заміни «A, B» не ділилось би на двох
        Assert.StartsWith("John Lennon, Yoko Ono", list.Tracks[0].Artist);
    }

    [Fact]
    public void Youtube_album_page_gives_header_and_playable_tracks()
    {
        var album = YtMusicClient.ParseAlbum("MPREb_XHt1jYJt0h9", Json("ytm-album.json"))!;
        Assert.Equal("Power To The People (New York City - The Ultimate Mixes)", album.Title);
        Assert.StartsWith("John Lennon", album.Artist);
        Assert.Equal("2025", album.Year);
        Assert.Equal(["ddBFNKBvKFM", "b8tPK7-l7b4", "nBHdAFZd6sc"], album.Tracks.Select(t => t.Id));   // недоступного нема
        Assert.Equal("New York City (Ultimate Mix)", album.Tracks[0].Title);
        Assert.Equal(281, album.Tracks[0].DurationSec);
        Assert.Equal(album.Artist, album.Tracks[0].Artist);   // порожня колонка — це виконавець альбому
        Assert.Equal(album.Title, album.Tracks[0].Album);
        Assert.Contains("=w300-h300", album.ThumbUrl);
    }

    [Fact]
    public void Youtube_playlist_page_skips_greyed_out_tracks()
    {
        var pl = YtMusicClient.ParsePlaylist("PLCiVjAiT9q1KL5pExT_sTW0oAbbLeqmwv", Json("ytm-playlist.json"))!;
        Assert.StartsWith("Украинская Музыка 2025", pl.Title);
        Assert.Equal("Black Beats", pl.Artist);
        Assert.Equal(["h28H0Ho9Sk8", "0mBqM53W0Mk"], pl.Tracks.Select(t => t.Id));
        Assert.Equal("Jerry Heil", pl.Tracks[0].Artist);
        Assert.Equal(188, pl.Tracks[0].DurationSec);
        Assert.NotNull(pl.Tracks[0].ThumbUrl);
    }

    [Fact]
    public void Album_playlist_without_a_header_is_named_after_its_album()
    {
        var pl = YtMusicClient.ParsePlaylist("OLAK5uy_nlivzrrTVO-n4scmqo7d9lDfzH6qbCQSk", Json("ytm-olak.json"))!;
        Assert.Equal("Power To The People (New York City - The Ultimate Mixes)", pl.Title);
        Assert.StartsWith("John Lennon", pl.Artist);
        Assert.Equal(2, pl.Tracks.Count);
    }

    [Fact]
    public void Album_search_gives_browse_ids_artists_and_years()
    {
        var hits = YtMusicClient.ParseAlbumSearch(Json("ytm-album-search.json"), 5);
        Assert.Equal(3, hits.Count);
        Assert.Equal(new YtAlbumRef("MPREb_9NgGUhkV6rh", "Sometime In New York City", "John Lennon і Yoko Ono", "1972"), hits[0]);
        Assert.Equal("MPREb_XHt1jYJt0h9", hits[1].BrowseId);
        Assert.Equal("2025", hits[1].Year);
    }

    [Fact]
    public void Same_title_with_a_featuring_note_and_a_shared_artist_is_the_song()
    {
        var hits = new[]
        {
            Yt("a", "Talk To You (feat. 54 Ultra)", "ANOTR", 192),
            Yt("b", "Talk To You (Extended Mix) (feat. 54 Ultra)", "ANOTR", 307),
        };
        Assert.Equal("a", Albums.Best(hits, Sp("Talk To You (ft. 54 Ultra)", "ANOTR, 54 Ultra", 191))?.Id);
    }

    [Fact]
    public void Title_without_the_with_note_still_matches()
    {
        var hits = new[]
        {
            Yt("a", "Rein Me In", "Sam Fender і Olivia Dean", 340),
            Yt("live", "Rein Me In (Live At London Stadium)", "Sam Fender і Olivia Dean", 366),
        };
        Assert.Equal("a", Albums.Best(hits, Sp("Rein Me In (with Olivia Dean)", "Sam Fender, Olivia Dean", 339))?.Id);
    }

    [Fact]
    public void Remix_loses_to_the_original_and_is_not_taken_alone()
    {
        var original = Yt("orig", "Dai Dai", "Shakira і Burna Boy", 224);
        var remix = Yt("remix", "Dai Dai (SPINALL Remix)", "Shakira, Burna Boy і SPINALL", 236);
        var sp = Sp("Dai Dai", "Shakira, Burna Boy", 223);
        Assert.Equal("orig", Albums.Best([remix, original], sp)?.Id);
        Assert.Null(Albums.Best([remix], sp));
    }

    [Fact]
    public void Same_title_by_a_stranger_is_taken_only_second_for_second()
    {
        var sp = Sp("Stateside + Zara Larsson", "PinkPantheress, Zara Larsson", 185);
        Assert.Null(Albums.Best([Yt("cover", "STATESIDE + ZARA LARSSON", "MIDNIGHT HAN", 199)], sp));
        Assert.Equal("real", Albums.Best([Yt("cover", "STATESIDE + ZARA LARSSON", "MIDNIGHT HAN", 199),
            Yt("real", "Stateside + Zara Larsson (feat. Zara Larsson)", "PinkPantheress", 185)], sp)?.Id);
        // та сама пісня, лише виконавця записано кирилицею
        Assert.Equal("cyr", Albums.Best([Yt("cyr", "Без бою", "Океан Ельзи", 262)], Sp("Без бою", "Okean Elzy", 262))?.Id);
    }

    [Fact]
    public void Spotify_album_tracks_land_on_the_youtube_album_by_title_and_length()
    {
        var sp = new[]
        {
            Sp("New York City - Ultimate Mix", "John Lennon, Yoko Ono", 280),
            Sp("Sisters, O Sisters - Ultimate Mix", "John Lennon, Yoko Ono", 226),
            Sp("We're All Water - Ultimate Mix", "John Lennon, Yoko Ono", 432),
            Sp("Bonus Only On Spotify", "John Lennon", 100),
        };
        var yt = new[]
        {
            Yt("nyc", "New York City (Ultimate Mix)", "John Lennon", 281),
            Yt("water", "We’re All Water (Ultimate Mix)", "John Lennon", 433),
            Yt("sisters", "Sisters, O Sisters (Ultimate Mix)", "John Lennon", 226),
        };
        Assert.Equal(["nyc", "sisters", "water", null], Albums.Map(sp, yt).Select(m => m?.Id));
    }

    [Fact]
    public void Remaster_suffix_and_brackets_do_not_break_the_album_map()
    {
        var sp = new[] { Sp("Heroes - 2017 Remaster", "David Bowie", 371), Sp("Heroes", "David Bowie", 211) };
        var yt = new[] { Yt("single", "Heroes (Single Version)", "David Bowie", 212), Yt("album", "Heroes (2017 Remaster)", "David Bowie", 372) };
        Assert.Equal(["album", "single"], Albums.Map(sp, yt).Select(m => m?.Id));
    }

    [Theory]
    [InlineData("https://open.spotify.com/album/7FepjkQJLHjbf0Tt9cooe1?si=7bc56798405f439d", "spotify:album:7FepjkQJLHjbf0Tt9cooe1")]
    [InlineData("https://open.spotify.com/intl-uk/album/7FepjkQJLHjbf0Tt9cooe1", "spotify:album:7FepjkQJLHjbf0Tt9cooe1")]
    [InlineData("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", "spotify:playlist:37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("https://music.youtube.com/browse/MPREb_XHt1jYJt0h9", "ytmusic:album:MPREb_XHt1jYJt0h9")]
    [InlineData("https://music.youtube.com/playlist?list=OLAK5uy_nlivzrrTVO-n4scmqo7d9lDfzH6qbCQSk", "ytmusic:album:OLAK5uy_nlivzrrTVO-n4scmqo7d9lDfzH6qbCQSk")]
    [InlineData("https://www.youtube.com/playlist?list=PLCiVjAiT9q1KL5pExT_sTW0oAbbLeqmwv", "ytmusic:playlist:PLCiVjAiT9q1KL5pExT_sTW0oAbbLeqmwv")]
    public void Album_and_playlist_links_are_recognised(string url, string key) =>
        Assert.Equal(key, Albums.Detect(url)?.Key);

    [Theory]
    [InlineData("https://open.spotify.com/track/6UWIAESo4iDnQD2eXEVzZB")]
    [InlineData("https://www.youtube.com/watch?v=ddBFNKBvKFM&list=PLCiVjAiT9q1KL5pExT_sTW0oAbbLeqmwv")]
    [InlineData("https://music.youtube.com/watch?v=ddBFNKBvKFM&list=RDAMVMddBFNKBvKFM")]
    [InlineData("https://www.youtube.com/playlist?list=RDCLAK5uy_kmPRjHDECIcuVwnKsx2Ng7fyNgFKWNJFs")]
    [InlineData("https://youtu.be/ddBFNKBvKFM")]
    [InlineData("океан ельзи без бою")]
    public void Single_tracks_mixes_and_text_are_not_albums(string input) =>
        Assert.Null(Albums.Detect(input));

    [Fact]
    public void Several_links_in_one_paste_come_apart_in_order()
    {
        Assert.Equal(["https://youtu.be/aaaaaaaaaaa", "https://open.spotify.com/track/6UWIAESo4iDnQD2eXEVzZB?si=1"],
            Links.Split("https://youtu.be/aaaaaaaaaaahttps://open.spotify.com/track/6UWIAESo4iDnQD2eXEVzZB?si=1"));
        Assert.Equal(["https://youtu.be/a1", "spotify:track:6UWIAESo4iDnQD2eXEVzZB", "https://youtu.be/b2"],
            Links.Split("https://youtu.be/a1\nspotify:track:6UWIAESo4iDnQD2eXEVzZB  дивись https://youtu.be/b2!"));
        Assert.Empty(Links.Split("просто назва пісні"));
    }

    [Theory]
    [InlineData("album", "John Lennon, Yoko Ono", "Imagine", "John Lennon — Imagine")]
    [InlineData("playlist", "Spotify", "Today’s Top Hits", "Today’s Top Hits")]
    [InlineData("album", "Pink Floyd", "The Dark Side of the Moon (50th Anniversary Remaster)", "Pink Floyd — The Dark Side of the Moon…")]
    public void Playlist_name_fits_the_site_limit(string kind, string artist, string title, string name)
    {
        var album = new Album("k", "spotify", kind, title, artist, null, null, "", []);
        Assert.Equal(name, Albums.PlaylistName(album));
        Assert.True(Albums.PlaylistName(album).Length <= 40);
    }

    [Theory]
    [InlineData(1, "1 трек")]
    [InlineData(3, "3 треки")]
    [InlineData(11, "11 треків")]
    [InlineData(21, "21 трек")]
    [InlineData(14, "14 треків")]
    [InlineData(22, "22 треки")]
    public void Track_count_reads_right(int n, string text) => Assert.Equal(text, Albums.Tracks(n));
}
