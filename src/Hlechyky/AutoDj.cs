using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Hlechyky;

/// <summary>
/// Builds suggestions from a few seeds: what is on air when a person put it there, otherwise the room's own
/// recent requests (anchors). Candidates come from three places:
/// 1) схожі артисти з YouTube Music (сторінка артиста сіда → «Схожі виконавці» → їхні найпопулярніші пісні) — основа;
/// 2) YouTube Music radio of the seed track — з меншою вагою: саме туди лізуть AI-контент-ферми;
/// 3) Last.fm similar tracks — agreement with the others is a strong signal.
/// Recently played, queued, banned and dismissed tracks are dropped, artists that just played or that the
/// room turned down are damped, the room's taste tilts the rest, and before anything is picked its artist
/// has to pass <see cref="ArtistQuality"/>: відомий Last.fm, є в MusicBrainz або вже звучав у кімнаті.
/// Без якорів це було б випадкове блукання: власний вибір Глека ставав би сідом для наступного, і за
/// ніч він доїжджав від «ДК Енергетик» до іспанських пісень про One Piece.
/// </summary>
public sealed class AutoDj(Db db, LastFmClient lastFm, YtMusicClient ytm, YtDlpService ytdlp, RoomTaste taste, ArtistQuality quality,
    IOptionsMonitor<AutoDjOptions> options, ILogger<AutoDj> log)
{
    /// <param name="SeedId">Трек-сід, від якого прийшла порада: якщо кімната її відкине, цей сід на якийсь час слабшає.</param>
    /// <param name="Source">
    /// Звідки кандидат: <c>related</c> (схожі артисти), <c>radio</c> (YT радіо), <c>lastfm</c> — через «+», якщо збіглися
    /// кілька; <c>archive</c> — свій архів, <c>seedquery</c> — стартовий пошук. Лягає в <c>plays.pick_source</c>.
    /// </param>
    public sealed record Pick(TrackInfo Track, string Reason, string? SeedId = null, string? Source = null);

    /// <param name="Weight">1 — повноцінний сід; менше — лише підмішується.</param>
    public sealed record Seed(TrackInfo Track, double Weight);

    /// <param name="List">Від чого шукати схоже.</param>
    /// <param name="Bias">Наскільки тиснути смаком кімнати: 1 — в ефірі вибір самого Глека, менше — трек поставила людина.</param>
    /// <param name="Listening">Хто зараз слухає на сайті: зважаємо на відмови саме цих людей (див. <see cref="CountStrikes"/>).</param>
    public sealed record Seeds(IReadOnlyList<Seed> List, double Bias = 1, IReadOnlyCollection<string>? Listening = null);

    /// <summary>
    /// Що кімнаті не зайшло. <paramref name="Artists"/> — дробові «штрафи» артистам (від 2 — артиста не беремо),
    /// <paramref name="Seeds"/> — скільки разів відкинули поради від сіда, <paramref name="Songs"/> і
    /// <paramref name="TrackIds"/> — пісні, яких поки не пропонувати зовсім.
    /// </summary>
    public sealed record Strikes(Dictionary<string, double> Artists, Dictionary<string, int> Seeds, HashSet<string> Songs, HashSet<string> TrackIds)
    {
        public bool Song(string key, string? trackId) => Songs.Contains(key) || (trackId is not null && TrackIds.Contains(trackId));
    }

    /// <summary>Скільки важить для артиста одна відмова від його пісні: щоб артиста прибрало, треба чотири різні пісні.</summary>
    public const double ArtistStrike = 0.5;

    sealed class Cand
    {
        public required string Key { get; init; }
        public required string Artist { get; init; }
        public required string Title { get; init; }
        /// <summary>Скільки кожен сід додав цьому кандидату (ключ — id треку-сіда).</summary>
        public Dictionary<string, double> BySeed { get; } = new();
        public double Mult = 1;
        public double Score => BySeed.Values.Sum() * Mult;
        public string TopSeed => BySeed.MaxBy(kv => kv.Value).Key;
        public SearchResult? Yt;
        public HashSet<string> Sources { get; } = new();
    }

    AutoDjOptions O => options.CurrentValue;
    readonly Random _rng = new();

    /// <summary>
    /// What to build on when nothing is on air: the last track that played, else a random liked one.
    /// Голосові пропускаємо — від чийогось «привіт усім» схожої музики не підбереш.
    /// </summary>
    public TrackInfo? FallbackSeed()
    {
        var recent = db.RecentDistinctTracks(5, excludeSkipped: false).FirstOrDefault(t => !VoiceService.IsVoice(t.Id));
        if (recent is not null) return recent;
        var liked = db.LikedTracks(50).Where(t => !VoiceService.IsVoice(t.Id)).ToList();
        return liked.Count > 0 ? liked[_rng.Next(liked.Count)] : null;
    }

    /// <summary>
    /// Своє, давно забуте: трек, який кімната вже ставила або лайкала, а в ефірі його не було вже
    /// добу. Нічого не коштує, зате гарантовано вертає Глека у свою колію.
    /// </summary>
    public Pick? ArchivePick(IReadOnlySet<string> excludeIds, IReadOnlyCollection<string>? listening = null)
    {
        var banned = db.BannedIds();
        var st = StrikesFor(listening);
        var pool = db.ArchiveTracks(DateTimeOffset.UtcNow.AddHours(-Math.Max(24, O.NoRepeatHours)), 40)
            .Where(t => !excludeIds.Contains(t.Id) && !banned.Contains(t.Id) && !st.Song(KeyOf(t.Artist, t.Title), t.Id)
                        && (t.DurationSec == 0 || t.DurationSec <= O.MaxDurationSeconds))
            .ToList();
        if (pool.Count == 0) return null;
        var t = pool[_rng.Next(pool.Count)];
        log.LogInformation("auto-DJ archive pick: {Label}", t.Label);
        return new Pick(t, "з нашого архіву", Source: "archive");
    }

    /// <summary>Що не зайшло тим, хто зараз слухає (нікого — тоді всім), за останні <see cref="AutoDjOptions.SongFeedbackDays"/>.</summary>
    /// <remarks>
    /// До пісень, відкинутих з порад, додаються й ті, що присутні скіпнули будь-де (і людські замовлення, і архів):
    /// Smaug скіпала й третину чужих замовлень, а архів потім пропонував їх знову.
    /// </remarks>
    public Strikes StrikesFor(IReadOnlyCollection<string>? listening)
    {
        var now = DateTimeOffset.UtcNow;
        var days = Math.Max(O.SongFeedbackDays, Math.Max(1, O.FeedbackHours) / 24.0);
        var st = CountStrikes(db.DjFeedbackSince(now.AddDays(-days)), listening, now, O);
        st.Songs.UnionWith(taste.SkippedBy(listening));
        return st;
    }

    /// <summary>
    /// Рахує відмови. Що змінилося 25.10 (дані з 13.09 по 25.10: 85 % відмов від однієї людини, 25 із 60 найчастіше
    /// відкинутих артистів вона ж сама лайкає, 321 її скіп — менш ніж за 40 с після попереднього):
    /// <list type="bullet">
    /// <item>Відмова — передусім про пісню: її не пропонуємо <see cref="AutoDjOptions.FeedbackHours"/>, а відкинуту двічі —
    /// <see cref="AutoDjOptions.SongFeedbackDays"/>. Артист отримує лише <see cref="ArtistStrike"/>.</item>
    /// <item>Серія скіпів однієї людини (кожен наступний не пізніше <see cref="AutoDjOptions.BurstSeconds"/>) — це «не той
    /// настрій»: артисти серії ділять один штраф на всіх, а сід (напрям) отримує не більше двох.</item>
    /// <item>Зважаємо на відмови лише тих, хто зараз слухає: не слухає — його смак ефіру не стосується. Нікого нема —
    /// рахуємо всіх. Записи без ніка рахуються завжди.</item>
    /// </list>
    /// </summary>
    public static Strikes CountStrikes(IEnumerable<Db.DjFeedbackRow> rows, IReadOnlyCollection<string>? listening, DateTimeOffset now, AutoDjOptions o)
    {
        var here = listening is { Count: > 0 } ? new HashSet<string>(listening.Select(Auth.NickKey)) : null;
        var all = rows.Where(r => here is null || r.Nick is null || here.Contains(Auth.NickKey(r.Nick))).ToList();
        var fresh = now.AddHours(-Math.Max(1, o.FeedbackHours));
        var recent = all.Where(r => r.At >= fresh).ToList();

        var artists = new Dictionary<string, double>();
        var seeds = new Dictionary<string, int>();
        foreach (var burst in Bursts(recent, TimeSpan.FromSeconds(Math.Max(0, o.BurstSeconds))))
        {
            foreach (var r in burst)
                artists[r.ArtistKey] = artists.GetValueOrDefault(r.ArtistKey) + ArtistStrike / burst.Count;
            foreach (var g in burst.Where(r => r.SeedId is not null).GroupBy(r => r.SeedId!))
                seeds[g.Key] = seeds.GetValueOrDefault(g.Key) + Math.Min(2, g.Count());
        }

        var songs = new HashSet<string>();
        var ids = new HashSet<string>();
        foreach (var r in recent)
        {
            if (r.Song is { Length: > 0 } s) songs.Add(s);
            if (r.TrackId is { Length: > 0 } id) ids.Add(id);
        }
        foreach (var g in all.Where(r => r.Song is { Length: > 0 }).GroupBy(r => r.Song!).Where(g => g.Count() >= 2)) songs.Add(g.Key);
        foreach (var g in all.Where(r => r.TrackId is { Length: > 0 }).GroupBy(r => r.TrackId!).Where(g => g.Count() >= 2)) ids.Add(g.Key);
        return new Strikes(artists, seeds, songs, ids);
    }

    /// <summary>Серії відмов однієї людини: наступна не пізніше <paramref name="gap"/> після попередньої. Без ніка — кожна окремо.</summary>
    static IEnumerable<List<Db.DjFeedbackRow>> Bursts(IEnumerable<Db.DjFeedbackRow> rows, TimeSpan gap)
    {
        foreach (var byNick in rows.GroupBy(r => r.Nick is null ? null : Auth.NickKey(r.Nick)))
        {
            if (byNick.Key is null || gap <= TimeSpan.Zero)
            {
                foreach (var r in byNick) yield return [r];
                continue;
            }
            List<Db.DjFeedbackRow>? cur = null;
            foreach (var r in byNick.OrderBy(r => r.At))
            {
                if (cur is not null && r.At - cur[^1].At <= gap) { cur.Add(r); continue; }
                if (cur is not null) yield return cur;
                cur = [r];
            }
            if (cur is not null) yield return cur;
        }
    }

    /// <summary>Сіди, від яких присутні вже двічі відкинули поради: якорем їх поки не беремо.</summary>
    public HashSet<string> WornOutSeeds(IReadOnlyCollection<string>? listening = null) =>
        StrikesFor(listening).Seeds.Where(kv => kv.Value >= 2).Select(kv => kv.Key).ToHashSet();

    /// <summary>Записати, що порада не зайшла: «Не те» або швидкий скіп.</summary>
    public void Reject(TrackInfo track, string? seedId, string kind, string? nick)
    {
        db.AddDjFeedback(ArtistKey(track.Artist), seedId, kind, nick, KeyOf(track.Artist, track.Title), track.Id);
        log.LogInformation("auto-DJ feedback: {Kind} {Label} (seed {Seed}) by {Nick}", kind, track.Label, seedId ?? "—", nick ?? "—");
    }

    public async Task<List<Pick>> PickManyAsync(Seeds s, IReadOnlySet<string> excludeIds, int count, CancellationToken ct)
    {
        var picks = new List<Pick>();
        if (count <= 0) return picks;

        var played = db.PlayedTrackIdsSince(DateTimeOffset.UtcNow.AddHours(-O.NoRepeatHours));
        var banned = db.BannedIds();
        var recentArtists = db.RecentArtists(3).Select(ArtistKey).ToHashSet();
        var strikes = StrikesFor(s.Listening);
        var (artistStrikes, seedStrikes) = (strikes.Artists, strikes.Seeds);
        bool Blocked(string id) => excludeIds.Contains(id) || played.Contains(id) || banned.Contains(id) || strikes.TrackIds.Contains(id);

        if (s.List.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(O.SeedQuery)) return picks;
            var found = await ytm.SearchSongsAsync(O.SeedQuery, 10, ct);
            foreach (var r in found.Where(r => !Blocked(r.Id) && Fits(r)).OrderBy(_ => _rng.Next()).Take(count))
                picks.Add(new Pick(ToTrack(r), $"стартовий сід «{O.SeedQuery}»", Source: "seedquery"));
            return picks;
        }

        var seedById = s.List.GroupBy(x => x.Track.Id).ToDictionary(g => g.Key, g => g.First().Track);
        var seedKeys = s.List.Select(x => KeyOf(x.Track.Artist, x.Track.Title)).ToHashSet();
        var seedArtists = s.List.Select(x => ArtistKey(x.Track.Artist)).ToHashSet();
        var cands = new Dictionary<string, Cand>();

        void Add(Seed seed, string artist, string title, double score, string source, SearchResult? yt)
        {
            var key = KeyOf(artist, title);
            if (seedKeys.Contains(key) || (yt is not null && seedById.ContainsKey(yt.Id))) return;
            if (!cands.TryGetValue(key, out var c)) cands[key] = c = new Cand { Key = key, Artist = artist, Title = title };
            c.BySeed[seed.Track.Id] = c.BySeed.GetValueOrDefault(seed.Track.Id) + score;
            c.Yt ??= yt;
            c.Sources.Add(source);
        }

        async Task CollectAsync(Seed seed)
        {
            // сід, від якого кімната вже відкидала поради, важить менше
            var w = seed.Weight * Math.Pow(0.5, seedStrikes.GetValueOrDefault(seed.Track.Id));
            var t = seed.Track;

            // 1) схожі артисти: справжні виконавці з їхніми хітами, а не випадкові треки з безодні радіо
            try
            {
                var page = await ArtistPageAsync(t.Artist, ct);
                if (page is not null)
                {
                    var related = await Task.WhenAll(page.Related.Take(6).Select(r => ArtistByIdAsync(r.Id, ct)));
                    for (var i = 0; i < related.Length; i++)
                    {
                        var songs = related[i]?.TopSongs ?? [];
                        for (var j = 0; j < Math.Min(5, songs.Count); j++)
                            Add(seed, songs[j].Artist, songs[j].Title, w * 0.9 * Math.Pow(0.92, i) * (1 - 0.1 * j), "related", songs[j]);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "YTM related artists failed for {Label}", t.Label); }

            // 2) YouTube Music radio of the seed: the order is YouTube's own relevance
            if (t.Id.Length == 11 && O.RadioWeight > 0)
            {
                var radio = new List<SearchResult>();
                try { radio = await ytm.RadioAsync(t.Id, 50, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "YTM radio failed for {Id}", t.Id); }
                if (radio.Count == 0)
                {
                    try { radio = await ytdlp.FlatPlaylistAsync($"https://music.youtube.com/watch?v={t.Id}&list=RDAMVM{t.Id}", 50, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "yt-dlp radio failed for {Id}", t.Id); }
                }
                var pos = 0;
                foreach (var r in radio.Where(r => r.Id != t.Id))
                    Add(seed, r.Artist, r.Title, w * O.RadioWeight * Math.Pow(0.97, pos++), "radio", r);
            }

            // 3) Last.fm similar tracks: agreement with the others is a strong signal, its own finds come with a lower weight
            if (!lastFm.Enabled) return;
            var sim = new List<SimilarTrack>();
            try
            {
                sim = await lastFm.SimilarTracksAsync(t.Artist, t.Title, 40, ct);
                if (sim.Count == 0)
                    foreach (var (ar, m) in await lastFm.SimilarArtistsAsync(YtMusicClient.FirstArtist(t.Artist), 5, ct))
                        foreach (var tt in await lastFm.ArtistTopTracksAsync(ar, 5, ct))
                            sim.Add(tt with { Match = tt.Match * m * 0.7 });
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Last.fm similar failed for {Label}", t.Label); }
            foreach (var si in sim)
            {
                var known = cands.ContainsKey(KeyOf(si.Artist, si.Title));
                Add(seed, si.Artist, si.Title, w * (known ? 0.8 : 0.45) * si.Match, "lastfm", null);
            }
        }

        foreach (var seed in s.List) await CollectAsync(seed);

        var pool = cands.Values
            .Where(c => c.Yt is null || (!Blocked(c.Yt.Id) && Fits(c.Yt)))
            .Where(c => !strikes.Songs.Contains(c.Key))
            .Where(c => artistStrikes.GetValueOrDefault(ArtistKey(c.Artist)) < 2)
            .ToList();
        foreach (var c in pool)
        {
            var a = ArtistKey(c.Artist);
            if (seedArtists.Contains(a)) c.Mult *= 0.5;      // same artist again is fine, just not all the time
            else if (recentArtists.Contains(a)) c.Mult *= 0.35;
            c.Mult *= Math.Pow(0.25, artistStrikes.GetValueOrDefault(a));
            c.Mult *= taste.Multiplier(c.Artist, c.Title, s.Bias, s.Listening);
        }
        // a little jitter only keeps two fills from being identical; the gate below throws some of the top out, so take plenty
        var top = pool.OrderByDescending(c => c.Score * (0.88 + 0.12 * _rng.NextDouble())).Take(40).ToList();
        var profile = taste.Current;
        log.LogInformation("auto-DJ: seeds {Seeds}, room {Script}/{Lean:F2}, for {Listening}, {Cands} candidates, top: {Top}",
            string.Join(" + ", s.List.Select(x => $"{x.Track.Label} ×{x.Weight:F2}")),
            profile.Script.Length > 0 ? profile.Script : "—", profile.Lean,
            s.Listening is { Count: > 0 } l ? string.Join(", ", l) : "кімнати", cands.Count,
            string.Join(" | ", top.Take(5).Select(c => $"{c.Artist} - {c.Title} ({c.Score:F2})")));

        var chosen = new HashSet<string>();
        var chosenArtists = new HashSet<string>();
        foreach (var c in top)
        {
            if (picks.Count >= count) break;
            if (count > 1 && chosenArtists.Contains(ArtistKey(c.Artist))) continue; // vary artists across a batch
            if (O.QualityGate && !ArtistQuality.Passes(await quality.JudgeAsync(c.Artist, c.Title, ct))) continue;
            var yt = c.Yt;
            if (yt is null || yt.DurationSec == 0)
            {
                // Last.fm дає лише назву, а сторінка артиста — без тривалості: пошук дає і те, й інше
                try
                {
                    var found = await ytm.ResolveAsync(c.Artist, c.Title, ct);
                    if (yt is null || found?.Id == yt.Id) yt = found ?? yt;
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "resolve failed for {A} - {T}", c.Artist, c.Title); }
            }
            if (yt is null || Blocked(yt.Id) || !Fits(yt) || strikes.Songs.Contains(KeyOf(yt.Artist, yt.Title)) || !chosen.Add(yt.Id)) continue;
            chosenArtists.Add(ArtistKey(c.Artist));
            var from = seedById[c.TopSeed];
            var sources = string.Join("+", c.Sources.Order(StringComparer.Ordinal));
            log.LogInformation("auto-DJ pick: {Label} ({Sources}; ×{Mult:F2}; від {Seed})", c.Artist + " - " + c.Title, sources, c.Mult, from.Label);
            picks.Add(new Pick(ToTrack(yt), $"схоже на {from.Label}", from.Id, sources));
        }
        return picks;
    }

    // ---- сторінки артистів YouTube Music, з кешем: вони міняються рідко, а запитів на один сід із десяток ----

    static readonly JsonSerializerOptions CacheJson = new(JsonSerializerDefaults.Web);

    async Task<YtArtist?> ArtistPageAsync(string artist, CancellationToken ct)
    {
        var idKey = "ytm:artist-id:" + ArtistKey(artist);
        var id = db.CacheGet(idKey, TimeSpan.FromDays(30));
        if (id is null)
        {
            id = await ytm.ArtistIdAsync(artist, ct) ?? "";
            db.CacheSet(idKey, id);
        }
        return id.Length == 0 ? null : await ArtistByIdAsync(id, ct);
    }

    async Task<YtArtist?> ArtistByIdAsync(string browseId, CancellationToken ct)
    {
        var key = "ytm:artist:" + browseId;
        if (db.CacheGet(key, TimeSpan.FromDays(3)) is { } cached)
            return JsonSerializer.Deserialize<YtArtist>(cached, CacheJson);
        try
        {
            var page = await ytm.ArtistAsync(browseId, ct);
            if (page is not null) db.CacheSet(key, JsonSerializer.Serialize(page, CacheJson));
            return page;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "YTM artist page failed for {Id}", browseId);
            return null;
        }
    }

    bool Fits(SearchResult r) => r.DurationSec == 0 || r.DurationSec <= O.MaxDurationSeconds;

    public static string ArtistKey(string artist) => YtMusicClient.Norm(YtMusicClient.FirstArtist(artist));
    /// <summary>Ключ пісні для порад і відмов (<c>dj_feedback.song</c>): перший артист і назва без розділових знаків.</summary>
    public static string KeyOf(string artist, string title) => ArtistKey(artist) + "|" + YtMusicClient.Norm(title);

    public static TrackInfo ToTrack(SearchResult r) =>
        new(r.Id, r.Title, r.Artist, r.DurationSec, r.ThumbUrl, $"https://music.youtube.com/watch?v={r.Id}", r.Album);
}
