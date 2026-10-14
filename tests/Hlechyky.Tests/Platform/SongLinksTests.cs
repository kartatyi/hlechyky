using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Посилання на трек у пошуку (записка #48): Лавка «Свій трек» шле в /api/search усе, що вписали, — і посилання теж.
/// Посилання мусить давати ту саму пісню, що й в ефірі, а не текстовий пошук YT Music за адресою. Назовні не ходимо:
/// Spotify, YTM і yt-dlp — підставні.
/// </summary>
public class SongLinksTests
{
    static readonly SearchResult Song = new("dQw4w9WgXcQ", "Never Gonna Give You Up", "Rick Astley", "Whenever You Need Somebody", 213, "https://i/x.jpg");

    sealed class Fake() : SongLinks(null!, null!, null!, NullLogger<SongLinks>.Instance)
    {
        public readonly List<string> Calls = [];
        public SearchResult? Match = Song;
        public SearchResult? Lookup = Song;
        public TrackInfo? Info;

        protected override Task<List<SearchResult>> SearchTextAsync(string q, int limit, CancellationToken ct)
        {
            Calls.Add("text:" + q);
            return Task.FromResult(new List<SearchResult> { Song, Song with { Id = "zzzzzzzzzzz" } });
        }
        protected override Task<SpotifyResolver.Track> SpotifyTrackAsync(string input, CancellationToken ct)
        {
            Calls.Add("spotify:" + input);
            if (!input.Contains("/track/")) throw new InvalidOperationException("зі Spotify беру посилання на трек, альбом чи плейлист");
            return Task.FromResult(new SpotifyResolver.Track("4cOdK2wGLETKBW3PvgPWqT", "Never Gonna Give You Up", "Rick Astley", 213, null));
        }
        protected override Task<SearchResult?> MatchAsync(SpotifyResolver.Track sp, CancellationToken ct)
        {
            Calls.Add("match:" + sp.Title);
            return Task.FromResult(Match);
        }
        protected override Task<SearchResult?> LookupAsync(string videoId, CancellationToken ct)
        {
            Calls.Add("lookup:" + videoId);
            return Task.FromResult(Lookup is null ? null : Lookup with { Id = videoId });
        }
        protected override Task<TrackInfo> FetchInfoAsync(string url, CancellationToken ct)
        {
            Calls.Add("ytdlp:" + url);
            return Info is null ? throw new InvalidOperationException("yt-dlp не взяв") : Task.FromResult(Info);
        }
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=RDdQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ&si=abc", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc", "dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/shorts/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/playlist?list=OLAK5uy_abc", null)]
    [InlineData("https://soundcloud.com/a/b", null)]
    [InlineData("Rick Astley Never Gonna", null)]
    public void Youtube_id_comes_from_any_youtube_track_link(string input, string? id) =>
        Assert.Equal(id, SongLinks.YouTubeId(input));

    [Theory]
    [InlineData("https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT?si=1", true)]
    [InlineData("spotify:track:4cOdK2wGLETKBW3PvgPWqT", true)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", true)]
    [InlineData("Океан Ельзи Обійми", false)]
    [InlineData("youtube.com/watch?v=dQw4w9WgXcQ", false)]   // без https — це текст, як і в ефірі
    public void Link_or_text(string input, bool link) => Assert.Equal(link, SongLinks.IsLink(input));

    [Fact]
    public async Task Search_by_youtube_music_link_gives_that_one_song_not_a_text_search()
    {
        var f = new Fake();
        var list = await f.SearchAsync("  https://music.youtube.com/watch?v=dQw4w9WgXcQ&si=abc ", default);
        Assert.Equal(["dQw4w9WgXcQ"], list.Select(r => r.Id));
        Assert.Equal(213, list[0].DurationSec);                 // Лавка бере id, назву, виконавця, тривалість
        Assert.Equal(["lookup:dQw4w9WgXcQ"], f.Calls);
    }

    [Fact]
    public async Task Search_by_spotify_track_link_gives_the_ytm_match()
    {
        var f = new Fake();
        var list = await f.SearchAsync("https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT?si=1", default);
        Assert.Equal([Song], list);
        Assert.DoesNotContain(f.Calls, c => c.StartsWith("text:"));
    }

    [Fact]
    public async Task Spotify_without_a_ytm_pair_is_empty_in_search_but_explained_on_air()
    {
        var f = new Fake { Match = null };
        Assert.Empty(await f.SearchAsync("https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT", default));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => f.ResolveLinkAsync("https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT", default));
        Assert.Equal("не знайшов «Rick Astley — Never Gonna Give You Up» на YouTube Music", ex.Message);
    }

    [Fact]
    public async Task Album_playlist_and_foreign_links_are_empty_and_never_text_searched()
    {
        var f = new Fake();
        Assert.Empty(await f.SearchAsync("https://open.spotify.com/album/7FepjkQJLHjbf0Tt9cooe1", default));
        Assert.Empty(await f.SearchAsync("https://music.youtube.com/playlist?list=OLAK5uy_abc", default));
        Assert.Empty(await f.SearchAsync("https://soundcloud.com/a/b", default));
        Assert.DoesNotContain(f.Calls, c => c.StartsWith("text:") || c.StartsWith("ytdlp:"));
    }

    [Fact]
    public async Task Youtube_video_missing_in_ytm_falls_back_to_ytdlp_like_on_air()
    {
        var f = new Fake { Lookup = null, Info = new TrackInfo("dQw4w9WgXcQ", "Пісня", "Хтось", 180, null, "https://www.youtube.com/watch?v=dQw4w9WgXcQ", null) };
        Assert.Null(await f.ResolveLinkAsync("https://youtu.be/dQw4w9WgXcQ", default));   // ефір далі сам іде в yt-dlp
        var list = await f.SearchAsync("https://youtu.be/dQw4w9WgXcQ", default);
        Assert.Equal([new SearchResult("dQw4w9WgXcQ", "Пісня", "Хтось", null, 180, null)], list);

        Assert.Empty(await new Fake { Lookup = null }.SearchAsync("https://youtu.be/dQw4w9WgXcQ", default));   // і yt-dlp упав — порожньо, без 500
    }

    [Fact]
    public async Task Plain_text_still_goes_to_ytm_search()
    {
        var f = new Fake();
        Assert.Equal(2, (await f.SearchAsync(" Rick Astley ", default)).Count);
        Assert.Equal(["text:Rick Astley"], f.Calls);
        Assert.Empty(await f.SearchAsync("   ", default));
        Assert.Null(await f.ResolveLinkAsync("Rick Astley", default));
    }
}
