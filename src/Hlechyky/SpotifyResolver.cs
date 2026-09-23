using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hlechyky;

/// <summary>
/// Turns a Spotify track link into "artist + title (+ length)" without an API key. The public
/// track page no longer carries Open Graph tags for bots, but the embed page still ships the
/// track as JSON (__NEXT_DATA__); the oEmbed endpoint is the last resort (title only).
/// Accepts open.spotify.com/track/…, the intl-xx/ variants, spotify:track:… URIs and spotify.link short links.
/// Альбом і плейлист беруться з тієї самої embed-сторінки: там увесь трекліст (плейлист — перші 100).
/// </summary>
public static partial class SpotifyResolver
{
    public sealed record Track(string Id, string Title, string Artist, int DurationSec, string? ThumbUrl);

    /// <summary>Альбом чи плейлист: <paramref name="Kind"/> — album | playlist, <paramref name="Owner"/> — виконавець альбому або хто склав плейлист.</summary>
    public sealed record TrackList(string Kind, string Id, string Name, string Owner, string? ThumbUrl, string? Year, List<Track> Tracks);

    /// <summary>Що за сутність у посиланні: (track|album|playlist|artist|…, id) або null. Короткі spotify.link спершу розгорни <see cref="ExpandAsync"/>.</summary>
    public static (string Kind, string Id)? Entity(string input)
    {
        var m = EntityRx().Match(input.Trim());
        return m.Success ? (m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value) : null;
    }

    /// <summary>Коротке посилання з телефона (spotify.link/…) веде редиректом на справжню сторінку; решту повертає як є.</summary>
    public static async Task<string> ExpandAsync(string input, CancellationToken ct)
    {
        input = input.Trim();
        if (!Uri.TryCreate(input, UriKind.Absolute, out var u) || !u.Host.Equals("spotify.link", StringComparison.OrdinalIgnoreCase)) return input;
        using var resp = await Http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, ct);
        return resp.RequestMessage?.RequestUri?.ToString() ?? input;
    }

    public static async Task<TrackList> ListAsync(string kind, string id, CancellationToken ct)
    {
        var html = await Http.GetStringAsync($"https://open.spotify.com/embed/{kind}/{id}", ct);
        return ParseList(html, kind, id)
               ?? throw new InvalidOperationException(kind == "album" ? "Spotify не віддав треків цього альбому" : "Spotify не віддав треків цього плейлиста");
    }

    /// <summary>Embed-сторінка альбому чи плейлиста → трекліст. Подкасти й локальні файли з плейлиста пропускаємо: на YouTube Music їх нема.</summary>
    public static TrackList? ParseList(string html, string kind, string id)
    {
        var m = NextDataRx().Match(html);
        if (!m.Success) return null;
        var entity = JsonNode.Parse(WebUtility.HtmlDecode(m.Groups[1].Value))?["props"]?["pageProps"]?["state"]?["data"]?["entity"];
        var name = Clean(Str(entity?["name"]) ?? Str(entity?["title"]));
        if (entity is null || name.Length == 0) return null;
        var tracks = new List<Track>();
        foreach (var t in entity["trackList"] as JsonArray ?? [])
        {
            var uri = Str(t?["uri"]) ?? "";
            var title = Clean(Str(t?["title"]));
            if (!uri.StartsWith("spotify:track:", StringComparison.Ordinal) || title.Length == 0) continue;
            var ms = t?["duration"] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
            tracks.Add(new Track(uri["spotify:track:".Length..], title, Clean(Str(t?["subtitle"])), (int)Math.Round(ms / 1000), null));
        }
        // обкладинок кілька розмірів: беремо ту, що ближча до 300 px, як у списках сайту
        var thumb = (entity["visualIdentity"]?["image"] as JsonArray ?? [])
            .Select(i => (Url: Str(i?["url"]), W: i?["maxWidth"] is JsonValue w && w.TryGetValue<int>(out var px) ? px : 0))
            .Where(i => i.Url is not null)
            .OrderBy(i => Math.Abs(i.W - 300)).Select(i => i.Url).FirstOrDefault();
        var date = Str(entity["releaseDate"]?["isoString"]);
        var year = date is { Length: >= 4 } && char.IsDigit(date[0]) ? date[..4] : null;
        return new TrackList(kind, id, name, Clean(Str(entity["subtitle"])), thumb, year, tracks);
    }

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Виконавців у треклісті Spotify розділяє «,» з нерозривним пробілом — без заміни «A, B» не ділиться на двох.</summary>
    static string Clean(string? s) => (s ?? "").Replace('\u00a0', ' ').Trim();

    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        h.DefaultRequestHeaders.AcceptLanguage.ParseAdd("uk,en;q=0.8");
        return h;
    }

    public static bool IsSpotify(string input)
    {
        input = input.Trim();
        if (input.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase)) return true;
        return Uri.TryCreate(input, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
               && (u.Host.EndsWith("spotify.com", StringComparison.OrdinalIgnoreCase) || u.Host.Equals("spotify.link", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<Track> ResolveAsync(string input, CancellationToken ct)
    {
        input = await ExpandAsync(input, ct);
        var m = TrackIdRx().Match(input);
        if (!m.Success)
        {
            var what = input.Contains("/artist/") ? "сторінка артиста" : input.Contains("/episode/") || input.Contains("/show/") ? "подкаст" : null;
            throw new InvalidOperationException(what is null ? "зі Spotify беру посилання на трек, альбом чи плейлист" : $"це {what}, а зі Spotify беру трек, альбом чи плейлист");
        }
        var id = m.Groups[1].Value;

        try
        {
            var t = await FromEmbedAsync(id, ct);
            if (t is not null) return t;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // fall through to oEmbed
            _ = ex;
        }
        var title = await FromOEmbedAsync(id, ct);
        if (title is null) throw new InvalidOperationException("Spotify не віддав назву треку");
        return title;
    }

    /// <summary>open.spotify.com/embed/track/{id}: Next.js page with props.pageProps.state.data.entity {name, artists[], duration}.</summary>
    static async Task<Track?> FromEmbedAsync(string id, CancellationToken ct)
    {
        var html = await Http.GetStringAsync($"https://open.spotify.com/embed/track/{id}", ct);
        var m = NextDataRx().Match(html);
        if (!m.Success) return null;
        var root = JsonNode.Parse(WebUtility.HtmlDecode(m.Groups[1].Value));
        var entity = root?["props"]?["pageProps"]?["state"]?["data"]?["entity"] ?? FindEntity(root, "spotify:track:" + id);
        if (entity is null) return null;
        var name = entity["name"]?.GetValue<string>() ?? entity["title"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name)) return null;
        var artists = entity["artists"]?.AsArray().Select(a => a?["name"]?.GetValue<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).ToList() ?? new();
        var artist = string.Join(", ", artists);
        if (string.IsNullOrWhiteSpace(artist)) artist = entity["subtitle"]?.GetValue<string>() ?? "";
        var durMs = 0d;
        try { durMs = entity["duration"]?.GetValue<double>() ?? 0; } catch { /* not a number */ }
        var thumb = entity["visualIdentity"]?["image"]?.AsArray().LastOrDefault()?["url"]?.GetValue<string>()
                    ?? entity["coverArt"]?["sources"]?.AsArray().FirstOrDefault()?["url"]?.GetValue<string>();
        return new Track(id, name.Trim(), artist.Trim(), (int)Math.Round(durMs / 1000), thumb);
    }

    static JsonNode? FindEntity(JsonNode? n, string uri)
    {
        switch (n)
        {
            case JsonObject o:
                if (o["uri"]?.ToString() == uri && o["name"] is not null) return o;
                foreach (var kv in o)
                    if (FindEntity(kv.Value, uri) is { } hit) return hit;
                break;
            case JsonArray a:
                foreach (var x in a)
                    if (FindEntity(x, uri) is { } hit) return hit;
                break;
        }
        return null;
    }

    static async Task<Track?> FromOEmbedAsync(string id, CancellationToken ct)
    {
        var json = await Http.GetStringAsync($"https://open.spotify.com/oembed?url=https://open.spotify.com/track/{id}", ct);
        var n = JsonNode.Parse(json);
        var title = n?["title"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(title) ? null : new Track(id, title.Trim(), "", 0, n?["thumbnail_url"]?.GetValue<string>());
    }

    [GeneratedRegex(@"(?:/track/|spotify:track:)([A-Za-z0-9]{22})")] private static partial Regex TrackIdRx();
    [GeneratedRegex(@"(?:open\.spotify\.com/(?:intl-[a-z-]+/)?(?:embed/)?|spotify:)(track|album|playlist|artist|episode|show)[/:]([A-Za-z0-9]{22})", RegexOptions.IgnoreCase)] private static partial Regex EntityRx();
    [GeneratedRegex(@"<script[^>]*id=""__NEXT_DATA__""[^>]*>(.*?)</script>", RegexOptions.Singleline)] private static partial Regex NextDataRx();
}
