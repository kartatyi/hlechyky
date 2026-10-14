using Microsoft.AspNetCore.WebUtilities;

namespace Hlechyky;

/// <summary>
/// Посилання на трек → одна пісня YouTube Music. Спільний крок ефіру (<see cref="RadioEngine"/>) і пошуку
/// (<c>/api/search</c>, з нього бере пісню Лавка «Свій трек»): раніше посилання розумів лише ефір, а пошук слав його
/// в YT Music як текст і нічого не знаходив (записка #48).
/// Spotify-трек — назва зі Spotify, далі та сама пісня в YTM; YouTube / YT Music — id з адреси й метадані з YTM.
/// Звернення назовні — віртуальні, щоб тести підставляли свої відповіді.
/// </summary>
public class SongLinks(YtMusicClient ytm, Albums albums, YtDlpService ytdlp, ILogger<SongLinks> log)
{
    /// <summary>Скільки пісень віддає текстовий пошук.</summary>
    public const int SearchLimit = 12;

    /// <summary>Це посилання (Spotify чи будь-яка http/https-адреса), а не текст для пошуку.</summary>
    public static bool IsLink(string input) => SpotifyResolver.IsSpotify(input) || HttpUri(input) is not null;

    static Uri? HttpUri(string input) =>
        Uri.TryCreate(input.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u : null;

    /// <summary>Id відео з посилання YouTube чи YT Music (watch?v=, youtu.be/, shorts/, embed/, live/) або null.</summary>
    public static string? YouTubeId(string input) => HttpUri(input) is { } u ? YouTubeId(u) : null;

    public static string? YouTubeId(Uri u)
    {
        var host = u.Host.ToLowerInvariant();
        if (host.EndsWith("youtu.be", StringComparison.Ordinal))
        {
            var seg = u.AbsolutePath.Trim('/').Split('/').FirstOrDefault();
            return seg is { Length: 11 } ? seg : null;
        }
        if (!host.EndsWith("youtube.com", StringComparison.Ordinal)) return null;
        var q = QueryHelpers.ParseQuery(u.Query);
        if (q.TryGetValue("v", out var v) && v.ToString() is { Length: 11 } vid) return vid;
        var segs = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length >= 2 && segs[0] is "shorts" or "embed" or "live" && segs[1].Length == 11) return segs[1];
        return null;
    }

    /// <summary>
    /// Посилання → пісня. Spotify — або пісня, або виняток з людським поясненням (не трек, нема в YTM; null не буває).
    /// YouTube — пісня з YTM або null (тоді ефір бере yt-dlp). Інше посилання чи текст — null.
    /// </summary>
    public async Task<SearchResult?> ResolveLinkAsync(string input, CancellationToken ct)
    {
        input = input.Trim();
        if (SpotifyResolver.IsSpotify(input))
        {
            var sp = await SpotifyTrackAsync(input, ct);
            var label = string.IsNullOrWhiteSpace(sp.Artist) ? sp.Title : $"{sp.Artist} — {sp.Title}";
            var r = await MatchAsync(sp, ct);
            if (r is null) throw new InvalidOperationException($"не знайшов «{label}» на YouTube Music");
            log.LogInformation("spotify {Id} = {Label} -> YTM {Yt} ({YtLabel})", sp.Id, label, r.Id, $"{r.Artist} — {r.Title}");
            return r;
        }
        if (YouTubeId(input) is not { } vid) return null;
        try
        {
            var r = await LookupAsync(vid, ct);
            if (r is not null && r.DurationSec > 0) return r;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { log.LogDebug(ex, "YTM lookup failed for {Id}", vid); }
        return null;
    }

    /// <summary>
    /// Пошук пісні для <c>/api/search</c>: текст — у YT Music; посилання на трек — список з однієї пісні, як її взяв би ефір
    /// (YouTube, якого нема в YTM, — через yt-dlp). Альбом, плейлист, артист чи чуже посилання — порожньо.
    /// </summary>
    public async Task<List<SearchResult>> SearchAsync(string? q, CancellationToken ct)
    {
        q = q?.Trim();
        if (string.IsNullOrEmpty(q)) return [];
        if (!IsLink(q)) return await SearchTextAsync(q, SearchLimit, ct);
        try
        {
            if (await ResolveLinkAsync(q, ct) is { } r) return [r];
            if (YouTubeId(q) is { } vid && await FetchInfoAsync(q, ct) is { } t && t.Id == vid)
                return [new SearchResult(t.Id, t.Title, t.Artist, t.Album, t.DurationSec, t.ThumbUrl)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogInformation("пошук за посиланням {Link}: {Err}", q, ex.Message);
        }
        return [];
    }

    protected virtual Task<List<SearchResult>> SearchTextAsync(string q, int limit, CancellationToken ct) => ytm.SearchSongsAsync(q, limit, ct);
    protected virtual Task<SpotifyResolver.Track> SpotifyTrackAsync(string input, CancellationToken ct) => SpotifyResolver.ResolveAsync(input, ct);
    protected virtual Task<SearchResult?> MatchAsync(SpotifyResolver.Track sp, CancellationToken ct) => albums.MatchTrackAsync(sp, strict: false, ct);
    protected virtual Task<SearchResult?> LookupAsync(string videoId, CancellationToken ct) => ytm.LookupAsync(videoId, ct);
    protected virtual Task<TrackInfo> FetchInfoAsync(string url, CancellationToken ct) => ytdlp.FetchInfoAsync(url, ct);
}
