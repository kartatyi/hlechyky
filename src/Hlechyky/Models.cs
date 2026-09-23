namespace Hlechyky;

public sealed record TrackInfo(string Id, string Title, string Artist, int DurationSec, string? ThumbUrl, string SourceUrl, string? Album)
{
    public string Label => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} — {Title}";
}

public enum ItemStatus { Queued, Downloading, Ready, Dispatched, Failed }

public sealed class QueueItem
{
    public string ItemId { get; init; } = Guid.NewGuid().ToString("N")[..10];
    public required TrackInfo Track { get; init; }
    public required string RequestedBy { get; init; }
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
    public ItemStatus Status { get; set; } = ItemStatus.Queued;
    public string? Error { get; set; }
    public string? FilePath { get; set; }
    public string? Rid { get; set; }
    /// <summary>user | autodj | suggestion</summary>
    public string Kind { get; init; } = "user";
    /// <summary>Why the auto-DJ picked it (also kept when a person takes a suggestion).</summary>
    public string? Reason { get; init; }
    /// <summary>"suggestion" when a person queued it from the DJ's suggestions, else null.</summary>
    public string? Via { get; init; }
    /// <summary>Трек-сід, від якого Глек це підібрав: «Не те» чи швидкий скіп послаблюють і його.</summary>
    public string? SeedId { get; init; }
}

public sealed class NowPlaying
{
    public TrackInfo? Track { get; set; }
    /// <summary>user | autodj | fallback | silence</summary>
    public string Source { get; set; } = "silence";
    public string? ItemId { get; set; }
    public string? RequestedBy { get; set; }
    public string? Reason { get; set; }
    public string? Via { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public int DurationSec { get; set; }
    public List<string> Likers { get; set; } = new();
    /// <summary>A skip was sent to liquidsoap and the next track has not started yet.</summary>
    public bool SkipPending { get; set; }
    public bool SpotifyLive { get; set; }
    public string? SpotifyTitle { get; set; }
    public long PlayId { get; set; }
    /// <summary>Від якого сіда Глек підібрав цей авто-трек (для швидкого скіпу); клієнту не потрібне.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? SeedId { get; set; }
}

/// <summary>
/// Рядок балачок. <paramref name="RoomId"/> — жива кімната, про яку цей рядок: фронт малює біля нього
/// кнопку «Сісти»/«Дивитись». null — звичайна репліка, ніякого столу за нею нема.
/// </summary>
/// <remarks>
/// <c>ReplyTo</c> — id повідомлення, на яке це відповідь; <c>ReplyNick</c>/<c>ReplyText</c> — хто й що там писав (уривок),
/// щоб браузер намалював цитату, навіть коли оригінал уже випав з історії. <c>Likes</c> — ніки тих, хто поставив ❤.
/// <c>Topic</c> — лише в рядків Журналу: 'radio' чи 'games'; за ним Журнал фільтрується.
/// </remarks>
public sealed record ChatMessage(long Id, string Nick, string Text, DateTimeOffset At, string Kind, string? RoomId = null,
    long? ReplyTo = null, string? ReplyNick = null, string? ReplyText = null, string[]? Likes = null, string? Topic = null);

public sealed record SearchResult(string Id, string Title, string Artist, string? Album, int DurationSec, string? ThumbUrl);

public sealed record YtArtistRef(string Id, string Name);

/// <summary>Сторінка артиста в YouTube Music: його найпопулярніші пісні і схожі виконавці.</summary>
public sealed record YtArtist(string Id, string Name, List<SearchResult> TopSongs, List<YtArtistRef> Related);

/// <summary>Альбом у видачі пошуку YouTube Music: browseId (MPREb_…), назва, виконавець, рік.</summary>
public sealed record YtAlbumRef(string BrowseId, string Title, string Artist, string? Year);

/// <summary>Альбом чи плейлист у YouTube Music: шапка й треки по порядку.</summary>
public sealed record YtCollection(string Id, string Title, string Artist, string? Year, string? ThumbUrl, List<SearchResult> Tracks);

/// <summary>
/// Трек альбому чи плейлиста, як його показати людині: назва й виконавець — як у джерелі (Spotify чи YouTube Music),
/// <paramref name="Match"/> — що саме з YouTube Music гратиме; null — не знайшлось.
/// </summary>
public sealed record AlbumTrack(int N, string Title, string Artist, int DurationSec, SearchResult? Match);

/// <summary>
/// Альбом чи плейлист, розібраний з посилання. <paramref name="Source"/> — spotify | ytmusic,
/// <paramref name="Kind"/> — album | playlist, <paramref name="Url"/> — сторінка в джерелі.
/// </summary>
public sealed record Album(string Key, string Source, string Kind, string Title, string Artist, string? Year, string? ThumbUrl, string Url, List<AlbumTrack> Tracks)
{
    public int Found => Tracks.Count(t => t.Match is not null);
    public int DurationSec => Tracks.Sum(t => t.Match?.DurationSec is > 0 ? t.Match.DurationSec : t.DurationSec);
}

public sealed record QueueItemDto(string ItemId, TrackInfo Track, string RequestedBy, string Status, string? Error, string Kind, string? Reason, string? Via, DateTimeOffset AddedAt);

public sealed record HistoryEntry(long PlayId, TrackInfo Track, string Source, string? RequestedBy, DateTimeOffset StartedAt, int Likes, string? Via, bool Skipped);

public sealed record PersistedQueueItem(string ItemId, TrackInfo Track, string RequestedBy, string Kind, string? Reason, string? Via, DateTimeOffset AddedAt);

public sealed record NickCount(string Nick, int Count);

public sealed class StateSnapshot
{
    public required NowPlaying Now { get; init; }
    public required List<QueueItemDto> Queue { get; init; }
    public QueueItemDto? AutoNext { get; init; }
    public List<QueueItemDto> Suggestions { get; init; } = new();
    /// <summary>The track the suggestions were built from (what is on air, the last thing that played, or an anchor from the room's own requests).</summary>
    public TrackInfo? SuggestSeed { get; init; }
    /// <summary>Звідки цей сід узявся — рядком, щоб панель не гадала.</summary>
    public string SuggestSeedNote { get; init; } = "";
    public required List<string> Online { get; init; }
    /// <summary>Ніки, у яких плеєр на сайті зараз грає.</summary>
    public List<string> ListeningNicks { get; init; } = new();
    /// <summary>Скільки вкладок сайту слухають (Icecast бачить кожну окремо).</summary>
    public int ListeningTabs { get; init; }
    public required bool LiquidsoapOk { get; init; }
    public required int Listeners { get; init; }
    public required int StreamDelaySeconds { get; init; }
    public required string SiteName { get; init; }
    public required string DjName { get; init; }
    public required string DjNameGen { get; init; }
    public required string StreamUrl { get; init; }
    public required bool LastFmEnabled { get; init; }
    /// <summary>Скільки секунд можна писати голосове; 0 — голосові вимкнені.</summary>
    public required int VoiceMaxSeconds { get; init; }
}

/// <summary>
/// Акаунт: нік як зареєстрували (з регістром), пароль як PBKDF2 (порожній — лише через Google), роль
/// (member чи admin), прив'язаний Google (<c>sub</c>) і пошта з нього — довідкова, листів не шлемо.
/// </summary>
public sealed record Account(string Nick, string PassHash, string PassSalt, string Role, string? GoogleSub = null, string? Email = null)
{
    public bool HasPassword => PassHash.Length > 0;
}
