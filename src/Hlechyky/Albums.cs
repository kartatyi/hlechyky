using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace Hlechyky;

/// <summary>
/// Альбоми й плейлисти з посилань: Spotify (альбом, плейлист) і YouTube Music / YouTube (альбом, плейлист).
/// Spotify-альбом шукаємо в YouTube Music цілим — тоді гратимуть саме альбомні версії, а не перший-ліпший
/// кавер чи кліп; що там не зійшлося (і всі треки плейлиста) шукаємо по одному. Розібране тримаємо пів години:
/// перегляд і «усе в чергу» — це два запити, а шукати двічі те саме не хочеться.
/// </summary>
public sealed partial class Albums(YtMusicClient ytm, Db db, ILogger<Albums> log)
{
    public sealed record Link(string Source, string Kind, string Id)
    {
        public string Key => $"{Source}:{Kind}:{Id}";
    }

    /// <summary>Скільки пошуків у YouTube Music іде нараз, коли треки доводиться шукати по одному.</summary>
    const int Parallel = 4;
    static readonly TimeSpan Keep = TimeSpan.FromMinutes(30);
    static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(90);
    readonly ConcurrentDictionary<string, (DateTime At, Lazy<Task<Album>> Load)> _cache = new();
    /// <summary>Сайт відкритий: хай хтось і накидає сотню плейлистів, у YouTube Music нараз підуть пошуки лише двох.</summary>
    readonly SemaphoreSlim _loads = new(2, 2);

    /// <summary>
    /// Чи це посилання на альбом чи плейлист — без мережі. Короткого spotify.link так не впізнати:
    /// його розгортає <see cref="ResolveAsync"/>.
    /// </summary>
    public static Link? Detect(string input)
    {
        input = input.Trim();
        if (SpotifyResolver.Entity(input) is { Kind: "album" or "playlist" } sp) return new Link("spotify", sp.Kind, sp.Id);
        if (!Uri.TryCreate(input, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return null;
        var host = u.Host.ToLowerInvariant();
        if (!host.EndsWith("youtube.com", StringComparison.Ordinal)) return null;
        var segs = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs is ["browse", var browse] && browse.StartsWith("MPREb_", StringComparison.Ordinal)) return new Link("ytmusic", "album", browse);
        var q = QueryHelpers.ParseQuery(u.Query);
        // watch?v=…&list=… — людина кидає один трек, який слухала з плейлиста, а не весь плейлист
        if (q.ContainsKey("v") || !q.TryGetValue("list", out var list)) return null;
        var id = list.ToString();
        if (!PlaylistIdRx().IsMatch(id)) return null;
        // RD… — нескінченні мікси «радіо від треку», LL/WL/LM — чиїсь «вподобане» й «на потім»: їх не відкрити
        if (id.StartsWith("RD", StringComparison.Ordinal) || id is "LL" or "WL" or "LM") return null;
        return new Link("ytmusic", id.StartsWith("OLAK5uy_", StringComparison.Ordinal) ? "album" : "playlist", id);
    }

    /// <summary>Розібраний альбом чи плейлист; null — посилання не на альбом і не на плейлист.</summary>
    public async Task<Album?> ResolveAsync(string input, CancellationToken ct)
    {
        var link = Detect(input);
        if (link is null && input.Contains("spotify.link", StringComparison.OrdinalIgnoreCase))
            link = Detect(await SpotifyResolver.ExpandAsync(input, ct));
        return link is null ? null : await GetAsync(link).WaitAsync(ct);
    }

    /// <summary>
    /// Один пошук на альбом, хоч скільки людей його відкрили: подвійний клік чи «усе в чергу» під час перегляду
    /// чекають той самий розбір. Вдалий пам'ятаємо <see cref="Keep"/>, невдалий — ні, наступна спроба шукає знову.
    /// Розбір не прив'язаний до запиту, який його почав: закрита вкладка не вбиває пошук для решти.
    /// </summary>
    Task<Album> GetAsync(Link link)
    {
        var now = DateTime.UtcNow;
        foreach (var (key, old) in _cache)
            if (now - old.At > Keep) _cache.TryRemove(new KeyValuePair<string, (DateTime, Lazy<Task<Album>>)>(key, old));
        var entry = _cache.GetOrAdd(link.Key, _ => (now, new Lazy<Task<Album>>(() => LoadAsync(link))));
        var task = entry.Load.Value;
        _ = task.ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) _cache.TryRemove(new KeyValuePair<string, (DateTime, Lazy<Task<Album>>)>(link.Key, entry));
        }, TaskScheduler.Default);
        return task;
    }

    async Task<Album> LoadAsync(Link link)
    {
        using var cts = new CancellationTokenSource(LoadTimeout);
        await _loads.WaitAsync(cts.Token);
        try { return await LoadCoreAsync(link, cts.Token); }
        finally { _loads.Release(); }
    }

    async Task<Album> LoadCoreAsync(Link link, CancellationToken ct)
    {
        if (link.Source == "spotify")
        {
            var sp = await SpotifyResolver.ListAsync(link.Kind, link.Id, ct);
            if (sp.Tracks.Count == 0) throw new InvalidOperationException(link.Kind == "album" ? "у цьому альбомі нема треків" : "у цьому плейлисті нема треків");
            var matches = new SearchResult?[sp.Tracks.Count];
            var whole = link.Kind == "album" ? await FindAlbumAsync(sp, ct) : null;
            if (whole is { } w) w.Map.CopyTo(matches, 0);
            var rest = Enumerable.Range(0, matches.Length).Where(i => matches[i] is null).ToList();
            await System.Threading.Tasks.Parallel.ForEachAsync(rest, new ParallelOptions { MaxDegreeOfParallelism = Parallel, CancellationToken = ct }, async (i, token) =>
            {
                // пошук іноді падає, коли запитів багато: одна повторна спроба, а тоді вже «не знайшлось»
                for (var attempt = 1; attempt <= 2 && matches[i] is null; attempt++)
                    try { matches[i] = await MatchTrackAsync(sp.Tracks[i], strict: true, token); break; }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.LogWarning("пошук «{Artist} — {Title}» у YouTube Music впав (спроба {Attempt}): {Error}", sp.Tracks[i].Artist, sp.Tracks[i].Title, attempt, ex.Message);
                        if (attempt == 1) await Task.Delay(400, token);
                    }
            });
            var album = new Album(link.Key, "spotify", link.Kind, sp.Name, sp.Owner, sp.Year, sp.ThumbUrl,
                $"https://open.spotify.com/{link.Kind}/{link.Id}",
                sp.Tracks.Select((t, i) => new AlbumTrack(i + 1, t.Title, t.Artist, t.DurationSec, matches[i])).ToList());
            log.LogInformation("spotify {Kind} {Id} «{Name}»: {Found}/{Total} у YouTube Music ({How}){Missing}", link.Kind, link.Id, sp.Name,
                album.Found, album.Tracks.Count, whole is { } hit ? $"альбом {hit.Album.Id} цілим, {rest.Count} по одному" : "по одному",
                album.Found == album.Tracks.Count ? "" : "; не знайшлось: " + string.Join("; ", album.Tracks.Where(t => t.Match is null).Select(t => $"{t.Artist} — {t.Title}")));
            return album;
        }
        var c = link.Id.StartsWith("MPREb_", StringComparison.Ordinal) ? await ytm.AlbumAsync(link.Id, ct) : await ytm.PlaylistAsync(link.Id, ct);
        if (c is null || c.Tracks.Count == 0)
            throw new InvalidOperationException(link.Kind == "album" ? "YouTube Music не віддав треків цього альбому" : "YouTube Music не віддав треків (плейлист приватний чи порожній?)");
        var url = link.Id.StartsWith("MPREb_", StringComparison.Ordinal) ? $"https://music.youtube.com/browse/{link.Id}" : $"https://music.youtube.com/playlist?list={link.Id}";
        return new Album(link.Key, "ytmusic", link.Kind, c.Title, c.Artist, c.Year, c.ThumbUrl, url,
            c.Tracks.Select((t, i) => new AlbumTrack(i + 1, t.Title, t.Artist, t.DurationSec, t)).ToList());
    }

    /// <summary>
    /// Той самий альбом у YouTube Music: назва й виконавець збігаються, а з його треків складається більшість
    /// Spotify-треклісту. Інше видання (делюкс, ремастер з бонусами) теж годиться — зайве просто не візьмемо.
    /// </summary>
    async Task<(YtCollection Album, SearchResult?[] Map)?> FindAlbumAsync(SpotifyResolver.TrackList sp, CancellationToken ct)
    {
        var artist = YtMusicClient.FirstArtist(sp.Owner);
        List<YtAlbumRef> hits;
        try { hits = await ytm.SearchAlbumsAsync($"{artist} {sp.Name}", 8, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogDebug(ex, "album search failed for {Artist} — {Name}", artist, sp.Name);
            return null;
        }
        var candidates = hits
            .Select((h, i) => (Hit: h, Score: TitleScore(sp.Name, h.Title), Order: i))
            .Where(x => x.Score > 0 && SameArtist(artist, x.Hit.Artist))
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Hit.Year == sp.Year).ThenBy(x => x.Order)
            .Take(3);
        foreach (var (hit, _, _) in candidates)
        {
            YtCollection? album;
            try { album = await ytm.AlbumAsync(hit.BrowseId, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogDebug(ex, "album {Id} failed", hit.BrowseId);
                continue;
            }
            if (album is null) continue;
            var map = Map(sp.Tracks, album.Tracks);
            if (map.Count(m => m is not null) * 2 >= sp.Tracks.Count) return (album, map);
        }
        return null;
    }

    /// <summary>2 — назви однакові, 1 — одна містить іншу («Abbey Road» і «Abbey Road (Remastered)»), 0 — інший альбом.</summary>
    static int TitleScore(string a, string b)
    {
        var (x, y) = (YtMusicClient.Norm(a), YtMusicClient.Norm(b));
        if (x.Length == 0 || y.Length == 0) return 0;
        return x == y ? 2 : x.Contains(y) || y.Contains(x) ? 1 : 0;
    }

    static bool SameArtist(string a, string b)
    {
        var (x, y) = (YtMusicClient.Norm(YtMusicClient.FirstArtist(a)), YtMusicClient.Norm(YtMusicClient.FirstArtist(b)));
        return x.Length > 0 && y.Length > 0 && (x.Contains(y) || y.Contains(x));
    }

    /// <summary>
    /// Треки Spotify-альбому ↔ треки того самого альбому в YouTube Music. Назви там пишуть по-різному
    /// («New York City - Ultimate Mix» і «New York City (Ultimate Mix)»), тож порівнюємо без розділових знаків;
    /// тривалість не дає сплутати дві версії однієї пісні. Три проходи від певного до ризикованого:
    /// та сама назва; назва без приписок («- 2011 Remaster», «(feat. …)»); те саме місце в треклісті й та сама довжина.
    /// </summary>
    public static SearchResult?[] Map(IReadOnlyList<SpotifyResolver.Track> sp, IReadOnlyList<SearchResult> yt)
    {
        var result = new SearchResult?[sp.Count];
        var used = new bool[yt.Count];
        for (var pass = 0; pass < 3; pass++)
            for (var i = 0; i < sp.Count; i++)
            {
                if (result[i] is not null) continue;
                var best = -1;
                var bestScore = double.MaxValue;
                for (var j = 0; j < yt.Count; j++)
                {
                    if (used[j] || !Fits(pass, i, j, sp[i], yt[j])) continue;
                    var score = Gap(sp[i].DurationSec, yt[j].DurationSec) + Math.Abs(i - j) * 0.5;
                    if (score < bestScore) (best, bestScore) = (j, score);
                }
                if (best < 0) continue;
                used[best] = true;
                result[i] = yt[best];
            }
        return result;
    }

    static bool Fits(int pass, int i, int j, SpotifyResolver.Track s, SearchResult y)
    {
        var gap = Gap(s.DurationSec, y.DurationSec);
        return pass switch
        {
            0 => YtMusicClient.Norm(s.Title) == YtMusicClient.Norm(y.Title) && gap <= 15,
            1 => Core(s.Title) is { Length: > 0 } core && core == Core(y.Title) && gap <= 10,
            _ => i == j && s.DurationSec > 0 && y.DurationSec > 0 && gap <= 3,
        };
    }

    /// <summary>Різниця в секундах; коли одна з тривалостей невідома — нуль, бо й сперечатись нема з чим.</summary>
    static int Gap(int a, int b) => a > 0 && b > 0 ? Math.Abs(a - b) : 0;

    /// <summary>Назва без приписок: без усього в дужках і після « - » («Heroes - 2017 Remaster» → «heroes»).</summary>
    static string Core(string title)
    {
        var t = BracketsRx().Replace(title, " ");
        var dash = t.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) t = t[..dash];
        return YtMusicClient.Norm(t);
    }

    /// <summary>
    /// Spotify-трек → пісня в YouTube Music (<see cref="Best"/>). Один трек за посиланням, коли певного збігу нема,
    /// бере далі те, що брав завжди: збіг за старими правилами, перший схожої довжини, перший узагалі.
    /// <paramref name="strict"/> — для альбомів і плейлистів: там «перший-ліпший» гірший за «не знайшов»,
    /// бо з-поміж двадцяти рядків ніхто не помітить, що замість пісні гратиме зовсім інша.
    /// </summary>
    public async Task<SearchResult?> MatchTrackAsync(SpotifyResolver.Track sp, bool strict, CancellationToken ct)
    {
        var query = string.IsNullOrWhiteSpace(sp.Artist) ? sp.Title : $"{sp.Artist} {sp.Title}";
        var hits = await ytm.SearchSongsAsync(query, 8, ct);
        if (hits.Count == 0)
        {
            // коли пошуків багато, YouTube Music зрідка віддає порожню видачу там, де пісня точно є: перепитуємо раз
            await Task.Delay(300, ct);
            hits = await ytm.SearchSongsAsync(query, 8, ct);
        }
        var best = Best(hits, sp);
        if (best is not null || strict) return best;
        return YtMusicClient.Pick(hits, sp.Artist, sp.Title)
               ?? hits.FirstOrDefault(h => sp.DurationSec == 0 || h.DurationSec == 0 || Math.Abs(h.DurationSec - sp.DurationSec) <= 20)
               ?? hits.FirstOrDefault();
    }

    /// <summary>
    /// Найкраща пара Spotify-треку серед результатів пошуку. Та сама назва (чи та сама без приписок на кшталт
    /// «(with Olivia Dean)») і хоч один спільний виконавець; з кількох — найближча за тривалістю. Ремікс, live,
    /// sped up та інші версії, яких нема в назві оригіналу, — уже не він. Та сама назва без спільного виконавця
    /// годиться лише секунда в секунду: так буває, коли ім'я записане іншою абеткою. null — нічого певного.
    /// </summary>
    public static SearchResult? Best(IReadOnlyList<SearchResult> hits, SpotifyResolver.Track sp)
    {
        SearchResult? best = null;
        var bestScore = int.MaxValue;
        var artists = ArtistSet(sp.Artist);
        var title = YtMusicClient.Norm(sp.Title);
        var core = Core(sp.Title);
        if (title.Length == 0) return null;
        foreach (var h in hits)
        {
            var gap = Gap(sp.DurationSec, h.DurationSec);
            if (gap > 20 || OtherVersion(sp.Title, h.Title)) continue;
            var other = YtMusicClient.Norm(h.Title);
            var same = other == title ? 0
                : other.Length > 0 && ((core.Length > 0 && core == Core(h.Title)) || other.Contains(title) || title.Contains(other)) ? 1
                : 2;
            var score = (same, artists.Overlaps(ArtistSet(h.Artist))) switch
            {
                (0, true) => gap,
                (1, true) => 30 + gap,
                (0, false) when gap <= 3 => 60 + gap,
                _ => -1,
            };
            if (score >= 0 && score < bestScore) (best, bestScore) = (h, score);
        }
        return best;
    }

    /// <summary>Усі виконавці з рядка «A, B і C feat. D» — нормалізовані, щоб «ROSÉ» і «Rosé» були одним.</summary>
    static HashSet<string> ArtistSet(string artists) =>
        artists.Replace('\u00a0', ' ').Split([", ", " & ", " і ", " и ", " and ", " feat. ", " ft. ", " x ", " + ", " with "], StringSplitOptions.None)
            .Select(YtMusicClient.Norm).Where(a => a.Length > 0).ToHashSet();

    /// <summary>Слова, якими позначають іншу версію пісні: є в знайденому, нема в оригіналі — отже, не той трек.</summary>
    static readonly string[] VersionWords =
        ["remix", "ремікс", "live", "наживо", "sped up", "speed", "slowed", "reverb", "nightcore", "acoustic", "instrumental",
         "karaoke", "cover", "кавер", "8d", "phonk", "mashup", "version", "versión", "demo"];

    static bool OtherVersion(string original, string found)
    {
        var a = $" {YtMusicClient.Norm(original)} ";
        var b = $" {YtMusicClient.Norm(found)} ";
        return VersionWords.Any(w => b.Contains($" {w} ") && !a.Contains($" {w} "));
    }

    /// <summary>
    /// «Зберегти плейлистом»: спільний плейлист сайту з назвою «Виконавець — Альбом» і треками по порядку.
    /// Плейлист з такою назвою вже є — докидаємо в нього, чого там бракує, а не плодимо двійників.
    /// </summary>
    public (bool Ok, string Message, long Id) SaveAsPlaylist(Album album, string nick)
    {
        var found = album.Tracks.Where(t => t.Match is not null).Select(t => t.Match!).ToList();
        if (found.Count == 0) return (false, "Жоден трек не знайшовся в YouTube Music — нема що зберегти", 0);
        var name = PlaylistName(album);
        var existing = db.Playlists().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var id = existing?.Id ?? db.CreatePlaylist(name, nick);
        var added = 0;
        foreach (var m in found)
        {
            db.UpsertTrack(AutoDj.ToTrack(m));
            if (db.AddToPlaylist(id, m.Id, nick)) added++;
        }
        return existing is null
            ? (true, $"Плейлист «{name}»: {Tracks(added)}", id)
            : (true, added == 0 ? $"У плейлисті «{name}» це все вже є" : $"У плейлист «{name}» додано ще {Tracks(added)}", id);
    }

    /// <summary>«1 трек», «3 треки», «12 треків».</summary>
    public static string Tracks(int n) =>
        $"{n} " + (n % 10 == 1 && n % 100 != 11 ? "трек" : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? "треки" : "треків");

    /// <summary>Назва плейлиста сайту — до 40 символів, як і в тих, що створюють руками.</summary>
    public static string PlaylistName(Album a)
    {
        var name = a.Kind == "album" && a.Artist.Length > 0 ? $"{YtMusicClient.FirstArtist(a.Artist)} — {a.Title}" : a.Title;
        if (name.Length <= 40) return name;
        var cut = char.IsHighSurrogate(name[38]) ? 38 : 39;
        return name[..cut].TrimEnd() + "…";
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]{2,64}$")] private static partial Regex PlaylistIdRx();
    [GeneratedRegex(@"[\(\[][^\)\]]*[\)\]]")] private static partial Regex BracketsRx();
}
