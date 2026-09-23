using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hlechyky;

/// <summary>
/// YouTube Music via its internal (InnerTube) API: song search, per-track radio ("Mix"),
/// and resolving an "artist + title" pair to a video id. Anonymous, no key needed.
/// Falls back are handled by callers (yt-dlp).
/// </summary>
public sealed partial class YtMusicClient(ILogger<YtMusicClient> log)
{
    static readonly HttpClient Http = CreateHttp();
    const string SongsFilter = "EgWKAQIIAWoKEAoQAxAEEAkQBQ%3D%3D";

    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        h.DefaultRequestHeaders.Add("Origin", "https://music.youtube.com");
        h.DefaultRequestHeaders.Add("X-Goog-Api-Format-Version", "1");
        return h;
    }

    static JsonObject Body() => new()
    {
        ["context"] = new JsonObject
        {
            ["client"] = new JsonObject
            {
                ["clientName"] = "WEB_REMIX",
                ["clientVersion"] = "1.20250901.01.00",
                ["hl"] = "uk",
                ["gl"] = "UA",
            },
        },
    };

    async Task<JsonNode?> PostAsync(string endpoint, JsonObject body, CancellationToken ct)
    {
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync($"https://music.youtube.com/youtubei/v1/{endpoint}?prettyPrint=false", content, ct);
        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonNode.ParseAsync(s, cancellationToken: ct);
    }

    public async Task<List<SearchResult>> SearchSongsAsync(string query, int limit, CancellationToken ct)
    {
        var body = Body();
        body["query"] = query;
        body["params"] = SongsFilter;
        var root = await PostAsync("search", body, ct);
        var list = new List<SearchResult>();
        var tabs = root?["contents"]?["tabbedSearchResultsRenderer"]?["tabs"]?.AsArray();
        var sections = tabs?.FirstOrDefault()?["tabRenderer"]?["content"]?["sectionListRenderer"]?["contents"]?.AsArray();
        if (sections is null) return list;
        foreach (var sec in sections)
        {
            var items = sec?["musicShelfRenderer"]?["contents"]?.AsArray();
            if (items is null) continue;
            foreach (var it in items)
            {
                var r = it?["musicResponsiveListItemRenderer"];
                if (r is null) continue;
                var id = r["playlistItemData"]?["videoId"]?.GetValue<string>();
                if (id is null) continue;
                var cols = r["flexColumns"]?.AsArray();
                if (cols is null || cols.Count < 2) continue;
                var title = RunsText(cols[0]?["musicResponsiveListItemFlexColumnRenderer"]?["text"]);
                var byline = RunsText(cols[1]?["musicResponsiveListItemFlexColumnRenderer"]?["text"]);
                var (artist, album, dur) = ParseByline(byline);
                var thumb = LastThumb(r["thumbnail"]?["musicThumbnailRenderer"]?["thumbnail"]?["thumbnails"]);
                if (string.IsNullOrWhiteSpace(title)) continue;
                list.Add(new SearchResult(id, title, artist, album, dur, thumb));
                if (list.Count >= limit) return list;
            }
        }
        return list;
    }

    /// <summary>YouTube Music "radio" for a track. The first entry is the seed itself.</summary>
    public async Task<List<SearchResult>> RadioAsync(string videoId, int limit, CancellationToken ct)
    {
        var body = Body();
        body["videoId"] = videoId;
        body["playlistId"] = "RDAMVM" + videoId;
        body["isAudioOnly"] = true;
        body["enablePersistentPlaylistPanel"] = true;
        body["tunerSettingValue"] = "AUTOMIX_SETTING_NORMAL";
        body["watchEndpointMusicSupportedConfigs"] = new JsonObject
        {
            ["watchEndpointMusicConfig"] = new JsonObject
            {
                ["hasPersistentPlaylistPanel"] = true,
                ["musicVideoType"] = "MUSIC_VIDEO_TYPE_ATV",
            },
        };
        var root = await PostAsync("next", body, ct);
        var tabs = root?["contents"]?["singleColumnMusicWatchNextResultsRenderer"]?["tabbedRenderer"]?["watchNextTabbedResultsRenderer"]?["tabs"]?.AsArray();
        var contents = tabs?.FirstOrDefault()?["tabRenderer"]?["content"]?["musicQueueRenderer"]?["content"]?["playlistPanelRenderer"]?["contents"]?.AsArray();
        var list = new List<SearchResult>();
        if (contents is null) return list;
        foreach (var it in contents)
        {
            var r = it?["playlistPanelVideoRenderer"] ?? it?["playlistPanelVideoWrapperRenderer"]?["primaryRenderer"]?["playlistPanelVideoRenderer"];
            if (r is null) continue;
            var id = r["videoId"]?.GetValue<string>();
            if (id is null) continue;
            var title = RunsText(r["title"]);
            var (artist, album, _) = ParseByline(RunsText(r["longBylineText"]));
            var dur = ParseDuration(RunsText(r["lengthText"]));
            var thumb = LastThumb(r["thumbnail"]?["thumbnails"]);
            if (string.IsNullOrWhiteSpace(title)) continue;
            list.Add(new SearchResult(id, title, artist, album, dur, thumb));
            if (list.Count >= limit) break;
        }
        return list;
    }

    /// <summary>Canonical metadata for a known video id (radio seed entry).</summary>
    public async Task<SearchResult?> LookupAsync(string videoId, CancellationToken ct)
    {
        var r = await RadioAsync(videoId, 2, ct);
        return r.FirstOrDefault(x => x.Id == videoId);
    }

    /// <summary>Find the YouTube Music song for a Last.fm-style artist + title pair.</summary>
    public async Task<SearchResult?> ResolveAsync(string artist, string title, CancellationToken ct)
    {
        var best = Pick(await SearchSongsAsync($"{artist} {title}", 8, ct), artist, title);
        if (best is null) log.LogDebug("YTM resolve miss: {Artist} - {Title}", artist, title);
        return best;
    }

    /// <summary>Та сама пісня серед результатів пошуку: назва й виконавець збігаються (без розділових знаків і регістру).</summary>
    public static SearchResult? Pick(IReadOnlyList<SearchResult> results, string artist, string title)
    {
        var nt = Norm(title);
        var na = Norm(FirstArtist(artist));
        if (nt.Length == 0) return null;
        return results.FirstOrDefault(r => Norm(r.Title).Contains(nt) && Norm(r.Artist).Contains(na))
               ?? results.FirstOrDefault(r => Norm(r.Title) == nt)
               ?? results.FirstOrDefault(r => Norm(r.Artist).Contains(na) && (Norm(r.Title).Contains(nt) || nt.Contains(Norm(r.Title))));
    }

    const string ArtistsFilter = "EgWKAQIgAWoKEAkQChAFEAMQBA==";

    /// <summary>Id сторінки артиста (UC…) за іменем; лише точний збіг імені, щоб не взяти однофамільця.</summary>
    public async Task<string?> ArtistIdAsync(string name, CancellationToken ct)
    {
        var want = Norm(FirstArtist(name));
        if (want.Length == 0) return null;
        var body = Body();
        body["query"] = FirstArtist(name);
        body["params"] = ArtistsFilter;
        var root = await PostAsync("search", body, ct);
        var sections = root?["contents"]?["tabbedSearchResultsRenderer"]?["tabs"]?.AsArray().FirstOrDefault()
            ?["tabRenderer"]?["content"]?["sectionListRenderer"]?["contents"]?.AsArray();
        foreach (var sec in sections ?? [])
            foreach (var it in sec?["musicShelfRenderer"]?["contents"]?.AsArray() ?? [])
            {
                var r = it?["musicResponsiveListItemRenderer"];
                var id = ArtistBrowseId(r?["navigationEndpoint"]);
                var title = RunsText(r?["flexColumns"]?.AsArray().FirstOrDefault()?["musicResponsiveListItemFlexColumnRenderer"]?["text"]);
                if (id is not null && Norm(title) == want) return id;
            }
        return null;
    }

    /// <summary>
    /// Сторінка артиста: «Найпопулярніші пісні» і «Схожі виконавці». Заголовки полиць локалізовані,
    /// тож розпізнаємо їх за вмістом: пісні — список із videoId, схожі — карусель, де картки ведуть на артистів.
    /// </summary>
    public async Task<YtArtist?> ArtistAsync(string browseId, CancellationToken ct)
    {
        var body = Body();
        body["browseId"] = browseId;
        return ParseArtist(browseId, await PostAsync("browse", body, ct));
    }

    public static YtArtist? ParseArtist(string browseId, JsonNode? root)
    {
        var name = RunsText(root?["header"]?["musicImmersiveHeaderRenderer"]?["title"] ?? root?["header"]?["musicVisualHeaderRenderer"]?["title"]);
        var sections = root?["contents"]?["singleColumnBrowseResultsRenderer"]?["tabs"]?.AsArray().FirstOrDefault()
            ?["tabRenderer"]?["content"]?["sectionListRenderer"]?["contents"]?.AsArray();
        if (sections is null) return null;
        var top = new List<SearchResult>();
        var related = new List<YtArtistRef>();
        foreach (var sec in sections)
        {
            if (sec?["musicShelfRenderer"] is { } shelf && top.Count == 0)
            {
                foreach (var it in shelf["contents"]?.AsArray() ?? [])
                {
                    var r = it?["musicResponsiveListItemRenderer"];
                    var id = r?["playlistItemData"]?["videoId"]?.GetValue<string>();
                    var cols = r?["flexColumns"]?.AsArray();
                    if (id is null || cols is null || cols.Count < 2) continue;
                    string Col(int i) => i < cols.Count ? RunsText(cols[i]?["musicResponsiveListItemFlexColumnRenderer"]?["text"]) : "";
                    var title = Col(0);
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    var album = Col(3);
                    top.Add(new SearchResult(id, title, Col(1), album.Length > 0 ? album : null, 0,
                        LastThumb(r!["thumbnail"]?["musicThumbnailRenderer"]?["thumbnail"]?["thumbnails"])));
                }
            }
            else if (sec?["musicCarouselShelfRenderer"]?["contents"]?.AsArray() is { } cards && related.Count == 0)
            {
                foreach (var card in cards)
                {
                    var r = card?["musicTwoRowItemRenderer"];
                    var id = ArtistBrowseId(r?["navigationEndpoint"]);
                    var title = RunsText(r?["title"]);
                    if (id is null || title.Length == 0) break; // не артисти — не та карусель
                    related.Add(new YtArtistRef(id, title));
                }
            }
        }
        return new YtArtist(browseId, name, top, related);
    }

    static string? ArtistBrowseId(JsonNode? nav) => BrowseId(nav, "MUSIC_PAGE_TYPE_ARTIST");

    static string? BrowseId(JsonNode? nav, string pageType)
    {
        var be = nav?["browseEndpoint"];
        var type = be?["browseEndpointContextSupportedConfigs"]?["browseEndpointContextMusicConfig"]?["pageType"]?.GetValue<string>();
        return type == pageType ? be?["browseId"]?.GetValue<string>() : null;
    }

    // ---------- альбоми й плейлисти ----------

    const string AlbumsFilter = "EgWKAQIYAWoKEAkQAxAEEAoQBQ%3D%3D";

    /// <summary>Альбоми за запитом «виконавець назва»: так Spotify-альбом знаходиться в YouTube Music цілим, а не трек за треком.</summary>
    public async Task<List<YtAlbumRef>> SearchAlbumsAsync(string query, int limit, CancellationToken ct)
    {
        var body = Body();
        body["query"] = query;
        body["params"] = AlbumsFilter;
        return ParseAlbumSearch(await PostAsync("search", body, ct), limit);
    }

    public static List<YtAlbumRef> ParseAlbumSearch(JsonNode? root, int limit)
    {
        var list = new List<YtAlbumRef>();
        var sections = root?["contents"]?["tabbedSearchResultsRenderer"]?["tabs"]?.AsArray().FirstOrDefault()
            ?["tabRenderer"]?["content"]?["sectionListRenderer"]?["contents"]?.AsArray();
        foreach (var sec in sections ?? [])
            foreach (var it in sec?["musicShelfRenderer"]?["contents"]?.AsArray() ?? [])
            {
                var r = it?["musicResponsiveListItemRenderer"];
                var id = BrowseId(r?["navigationEndpoint"], "MUSIC_PAGE_TYPE_ALBUM");
                var cols = r?["flexColumns"]?.AsArray();
                var title = Col(cols, 0);
                if (id is null || title.Length == 0) continue;
                // «Альбом • John Lennon і Yoko Ono • 1972»; у синглів і EP лише перше слово інше
                var segs = Col(cols, 1).Split(" • ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
                var year = segs.Count > 0 && YearRx().IsMatch(segs[^1]) ? segs[^1] : null;
                if (year is not null) segs.RemoveAt(segs.Count - 1);
                list.Add(new YtAlbumRef(id, title, segs.Count > 1 ? segs[1] : segs.FirstOrDefault() ?? "", year));
                if (list.Count >= limit) return list;
            }
        return list;
    }

    /// <summary>Сторінка альбому (MPREb_…): шапка й треки по порядку.</summary>
    public async Task<YtCollection?> AlbumAsync(string browseId, CancellationToken ct)
    {
        var body = Body();
        body["browseId"] = browseId;
        return ParseAlbum(browseId, await PostAsync("browse", body, ct));
    }

    /// <summary>
    /// Колонка виконавця в треку альбому порожня, коли це виконавець самого альбому; заповнена — у збірниках.
    /// Недоступні треки (без videoId) пропускаємо: качати там нічого.
    /// </summary>
    public static YtCollection? ParseAlbum(string browseId, JsonNode? root)
    {
        var header = Find(root?["contents"], "musicResponsiveHeaderRenderer") ?? root?["header"]?["musicDetailHeaderRenderer"];
        var shelf = Find(root?["contents"], "musicShelfRenderer");
        if (header is null || shelf is null) return null;
        var title = RunsText(header["title"]);
        var subtitle = RunsText(header["subtitle"]).Split(" • ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var artist = RunsText(header["straplineTextOne"]);
        if (artist.Length == 0 && subtitle.Length > 2) artist = subtitle[1];   // стара шапка: «Альбом • Виконавець • 2020»
        var year = subtitle.LastOrDefault(s => YearRx().IsMatch(s));
        var thumb = LastThumb(header["thumbnail"]?["musicThumbnailRenderer"]?["thumbnail"]?["thumbnails"]
                              ?? header["thumbnail"]?["croppedSquareThumbnailRenderer"]?["thumbnail"]?["thumbnails"]);
        var tracks = new List<SearchResult>();
        foreach (var it in shelf["contents"]?.AsArray() ?? [])
        {
            var r = it?["musicResponsiveListItemRenderer"];
            var id = r?["playlistItemData"]?["videoId"]?.GetValue<string>();
            var cols = r?["flexColumns"]?.AsArray();
            var name = Col(cols, 0);
            if (id is null || name.Length == 0) continue;
            var by = Col(cols, 1);
            tracks.Add(new SearchResult(id, name, by.Length > 0 ? by : artist, title, ParseDuration(FixedCol(r, 0)), thumb));
        }
        return new YtCollection(browseId, title, artist, year, thumb, tracks);
    }

    /// <summary>Плейлист (PL…, або OLAK5uy_… — так YouTube Music віддає альбом посиланням): перша сторінка, це до сотні треків.</summary>
    public async Task<YtCollection?> PlaylistAsync(string playlistId, CancellationToken ct)
    {
        var body = Body();
        body["browseId"] = "VL" + playlistId;
        return ParsePlaylist(playlistId, await PostAsync("browse", body, ct));
    }

    /// <summary>
    /// Шапки в альбомного плейлиста (OLAK5uy_…) нема — тоді назва й виконавець беруться з самих треків,
    /// які там усі з одного альбому.
    /// </summary>
    public static YtCollection? ParsePlaylist(string playlistId, JsonNode? root)
    {
        var shelf = Find(root?["contents"], "musicPlaylistShelfRenderer");
        if (shelf is null) return null;
        var tracks = new List<SearchResult>();
        foreach (var it in shelf["contents"]?.AsArray() ?? [])
        {
            var r = it?["musicResponsiveListItemRenderer"];
            var id = r?["playlistItemData"]?["videoId"]?.GetValue<string>();
            var cols = r?["flexColumns"]?.AsArray();
            var name = Col(cols, 0);
            if (id is null || name.Length == 0) continue;
            if (r!["musicItemRendererDisplayPolicy"]?.GetValue<string>() == "MUSIC_ITEM_RENDERER_DISPLAY_POLICY_GREY_OUT") continue;
            var from = Col(cols, 2);
            tracks.Add(new SearchResult(id, name, Col(cols, 1), from.Length > 0 ? from : null, ParseDuration(FixedCol(r, 0)),
                LastThumb(r["thumbnail"]?["musicThumbnailRenderer"]?["thumbnail"]?["thumbnails"])));
        }
        var header = Find(root?["contents"], "musicResponsiveHeaderRenderer") ?? root?["header"]?["musicDetailHeaderRenderer"];
        var title = RunsText(header?["title"]);
        var owner = RunsText(header?["straplineTextOne"]);
        if (owner.Length == 0) owner = header?["facepile"]?["avatarStackViewModel"]?["text"]?["content"]?.GetValue<string>() ?? "";
        var thumb = LastThumb(header?["thumbnail"]?["musicThumbnailRenderer"]?["thumbnail"]?["thumbnails"]);
        if (title.Length == 0 && tracks.Select(t => t.Album).Distinct().ToList() is [{ } album])
        {
            title = album;
            owner = tracks[0].Artist;
        }
        if (title.Length == 0) title = "Плейлист";
        return new YtCollection(playlistId, title, owner, null, thumb ?? tracks.FirstOrDefault()?.ThumbUrl, tracks);
    }

    static string Col(JsonArray? cols, int i) =>
        cols is not null && i < cols.Count ? RunsText(cols[i]?["musicResponsiveListItemFlexColumnRenderer"]?["text"]) : "";

    static string FixedCol(JsonNode? r, int i) =>
        r?["fixedColumns"] is JsonArray f && i < f.Count ? RunsText(f[i]?["musicResponsiveListItemFixedColumnRenderer"]?["text"]) : "";

    /// <summary>Перший вузол із таким ключем, углиб: розкладка сторінок YouTube Music час від часу переїжджає, а назви рендерерів лишаються.</summary>
    static JsonNode? Find(JsonNode? n, string key)
    {
        switch (n)
        {
            case JsonObject o:
                if (o[key] is { } hit) return hit;
                foreach (var kv in o)
                    if (Find(kv.Value, key) is { } deep) return deep;
                break;
            case JsonArray a:
                foreach (var x in a)
                    if (Find(x, key) is { } deep) return deep;
                break;
        }
        return null;
    }

    static string RunsText(JsonNode? textNode)
    {
        var runs = textNode?["runs"]?.AsArray();
        if (runs is null) return textNode?["simpleText"]?.GetValue<string>() ?? "";
        return string.Concat(runs.Select(r => r?["text"]?.GetValue<string>() ?? ""));
    }

    /// <summary>Byline looks like "Artist і Artist2 • Album • 3:21" (search) or "Artist • Album • 2024" (radio).</summary>
    static (string Artist, string? Album, int Duration) ParseByline(string byline)
    {
        var segs = byline.Split(" • ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        var dur = 0;
        if (segs.Count > 0 && DurationRx().IsMatch(segs[^1]))
        {
            dur = ParseDuration(segs[^1]);
            segs.RemoveAt(segs.Count - 1);
        }
        if (segs.Count > 0 && YearRx().IsMatch(segs[^1])) segs.RemoveAt(segs.Count - 1);
        if (segs.Count > 1 && segs[0] is "Пісня" or "Song" or "Відео" or "Video") segs.RemoveAt(0);
        var artist = segs.Count > 0 ? segs[0] : "";
        var album = segs.Count > 1 && !ViewsRx().IsMatch(segs[1]) ? segs[1] : null;
        return (artist, album, dur);
    }

    public static int ParseDuration(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var parts = s.Trim().Split(':');
        var total = 0;
        foreach (var p in parts)
        {
            if (!int.TryParse(p, out var v)) return 0;
            total = total * 60 + v;
        }
        return total;
    }

    static string? LastThumb(JsonNode? thumbs)
    {
        var url = thumbs?.AsArray().LastOrDefault()?["url"]?.GetValue<string>();
        return url is null ? null : ThumbSizeRx().Replace(url, "=w300-h300");
    }

    public static string Norm(string s)
    {
        var cleaned = NonWordRx().Replace(s.ToLowerInvariant(), " ");
        return SpacesRx().Replace(cleaned, " ").Trim();
    }

    public static string FirstArtist(string artist) =>
        artist.Split([" і ", " & ", ", ", " and ", " feat. ", " ft. ", " x "], StringSplitOptions.None)[0].Trim();

    [GeneratedRegex(@"^\d+:\d\d(:\d\d)?$")] private static partial Regex DurationRx();
    [GeneratedRegex(@"^\d{4}$")] private static partial Regex YearRx();
    [GeneratedRegex(@"\d.*(перегляд|views|тис\.|млн|K$|M$)", RegexOptions.IgnoreCase)] private static partial Regex ViewsRx();
    [GeneratedRegex(@"=w\d+-h\d+")] private static partial Regex ThumbSizeRx();
    [GeneratedRegex(@"[^\p{L}\p{Nd} ]")] private static partial Regex NonWordRx();
    [GeneratedRegex(@"\s+")] private static partial Regex SpacesRx();
}
