using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Hlechyky;

/// <summary>
/// Смак кімнати: артисти, яких тут уже ставили або лайкали, і яким письмом підписане те, що тут
/// слухають. Рахується тільки з людських замовлень — на власних виборах Дядько Глек учився б на
/// своєму ж дрейфі. Звідси ж беруться якорі: свіжі замовлення, від яких можна відштовхнутись,
/// коли він уже задовго крутить своє.
/// <para>
/// «Людські» — це від акаунтів (як у «Хто скільки»): 24.10 гість за вечір закинув 96 треків (71 скіпнули), і всі
/// 15 якорів та половина смаку стали його роком 80-х. Тому гості й боти не рахуються, скіпнуте замовлення — теж
/// (кімнаті не зайшло), а один нік дає не більше <see cref="MaxShare"/> смаку і якорів: решту добирають старші
/// замовлення інших.
/// </para>
/// </summary>
public sealed partial class RoomTaste(Db db, IOptionsMonitor<AutoDjOptions> options)
{
    /// <param name="Artists">Нормалізовані ключі артистів, яких кімната знає.</param>
    /// <param name="Script">Переважне письмо — <c>cyr</c>, <c>lat</c> або порожньо, якщо ще нема з чого судити.</param>
    /// <param name="Lean">0 — кімната мішана, 1 — слухає виключно одне. Сила всіх множників нижче.</param>
    public sealed record Profile(HashSet<string> Artists, string Script, double Lean)
    {
        public static readonly Profile Empty = new([], "", 0);
    }

    /// <summary>Менше замовлень — і будь-який нахил це просто випадковість, а не смак.</summary>
    const int MinTracksForLean = 12;

    /// <summary>Найбільша частка одного ніка в смаку й серед якорів.</summary>
    public const double MaxShare = 0.3;

    /// <summary>Скільки замовлень дивимось назад: з запасом, бо гості, скіпи й стеля на нік частину відсіють.</summary>
    const int Lookback = 600;
    const int ProfileTracks = 200;
    const int AnchorPool = 15;

    static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    readonly object _lock = new();
    readonly Random _rng = new();
    readonly Queue<string> _lastAnchors = new();
    Profile _cached = Profile.Empty;
    DateTime _builtAt = DateTime.MinValue;

    public Profile Current
    {
        get
        {
            lock (_lock)
            {
                if (DateTime.UtcNow - _builtAt < Ttl) return _cached;
                _builtAt = DateTime.UtcNow;
                return _cached = Build();
            }
        }
    }

    /// <summary>Замовлення людей (акаунти, не боти), яких не скіпнули, — найсвіжіші перші, з виправленим артистом.</summary>
    List<(TrackInfo Track, string Nick)> Requests(Bots.Set notPeople) =>
        db.RecentRequests(Lookback)
            .Where(x => x.Nick is not null && !notPeople.Has(x.Nick) && !x.Skipped)
            .Select(x => (Credit(x.Track), x.Nick!))
            .ToList();

    List<(TrackInfo Track, string Nick)> Likes(Bots.Set notPeople) =>
        db.RecentLikes(Lookback).Where(x => !notPeople.Has(x.Nick)).Select(x => (Credit(x.Track), x.Nick)).ToList();

    Profile Build()
    {
        var notPeople = Bots.Load(db);
        var requests = Requests(notPeople);
        var likes = Likes(notPeople);
        var artists = requests.Concat(likes).Select(x => AutoDj.ArtistKey(x.Track.Artist)).Where(a => a.Length > 0).ToHashSet();
        var recent = Fair(requests, ProfileTracks);
        var counts = recent.Select(t => ScriptOf(t.Artist, t.Title)).Where(s => s.Length > 0)
            .GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        var forced = options.CurrentValue.PreferScript?.Trim().ToLowerInvariant() ?? "";
        if (forced is "cyr" or "lat") return new Profile(artists, forced, 1);
        var total = counts.Values.Sum();
        if (total < MinTracksForLean) return new Profile(artists, "", 0);
        var top = counts.MaxBy(kv => kv.Value);
        // рівно навпіл — нахилу нема; що далі від половини, то впевненіше
        return new Profile(artists, top.Key, Math.Clamp((top.Value / (double)total - 0.5) * 2, 0, 1));
    }

    /// <summary>
    /// Перші <paramref name="take"/> треків, але від кожного ніка не більше <see cref="MaxShare"/> з них (хоч один):
    /// хто закинув цілий плейлист, не перекриває решту кімнати. Порядок (свіжіше — вище) зберігається.
    /// </summary>
    public static List<TrackInfo> Fair(IEnumerable<(TrackInfo Track, string Nick)> rows, int take)
    {
        var cap = Math.Max(1, (int)Math.Ceiling(take * MaxShare));
        var per = new Dictionary<string, int>();
        var list = new List<TrackInfo>();
        foreach (var (t, nick) in rows)
        {
            if (list.Count >= take) break;
            var key = Auth.NickKey(nick);
            var n = per.GetValueOrDefault(key);
            if (n >= cap) continue;
            per[key] = n + 1;
            list.Add(t);
        }
        return list;
    }

    // «Bob Seger- Turn the Page» від каналу «jimmej955»: справжній артист — у назві, перед тире
    [GeneratedRegex(@"^\s*(?<a>[^-–—|]{2,60}?)\s*[-–—]\s+(?<t>\S.*)$")]
    private static partial Regex DashTitle();

    // «Місто весни - Live», «Song - 2011 Remaster», «… - From "Film"»: після тире — версія, а не пісня
    [GeneratedRegex(@"^(\(|\[)?(live|remaster|remastered|version|mix|edit|remix|radio|mono|stereo|demo|acoustic|bonus|instrumental|from|single|original|extended|feat|ft|prod|\d{4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex VersionTail();

    /// <summary>
    /// Справжній артист треку, якщо його завантажив не виконавець, а випадковий канал: тоді в полі «артист» сидить
    /// назва каналу, а виконавець — у назві «Артист - Пісня». Без цього «jimmej955» ставав «своїм артистом» кімнати
    /// і проходив суддю якості без питань, а від такого сіда ні схожих артистів, ні Last.fm не знайдеш.
    /// </summary>
    public static (string Artist, string Title) CreditOf(string artist, string title)
    {
        var m = DashTitle().Match(title);
        if (!m.Success) return (artist, title);
        var a = m.Groups["a"].Value.Trim();
        var t = m.Groups["t"].Value.Trim();
        if (VersionTail().IsMatch(t)) return (artist, title);
        var na = YtMusicClient.Norm(a);
        var own = YtMusicClient.Norm(YtMusicClient.FirstArtist(artist));
        // артист і так той самий («Queen - Bohemian Rhapsody» від Queen) або назва починається з нього
        if (na.Length == 0 || own.Length == 0 || na == own || na.Contains(own) || own.Contains(na)) return (artist, title);
        return (a, t);
    }

    public static TrackInfo Credit(TrackInfo t)
    {
        var (a, ti) = CreditOf(t.Artist, t.Title);
        return ReferenceEquals(a, t.Artist) ? t : t with { Artist = a, Title = ti };
    }

    /// <summary>
    /// Яким письмом підписаний трек. Дивимось на слова від двох літер і лише на першого виконавця:
    /// YouTube Music з українською локаллю склеює їх через кириличне «і», і без цього будь-який
    /// «true viking, fijorddd і moonlighttt» рахувався б своїм.
    /// </summary>
    public static string ScriptOf(string artist, string title)
    {
        bool cyr = false, lat = false;
        foreach (var word in (YtMusicClient.FirstArtist(artist) + " " + title).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var letters = word.Where(char.IsLetter).ToArray();
            if (letters.Length < 2) continue;
            if (letters.Any(c => c is >= 'Ѐ' and <= 'ӿ')) cyr = true;
            else if (letters.Any(char.IsAsciiLetter)) lat = true;
        }
        return cyr ? "cyr" : lat ? "lat" : "";
    }

    /// <summary>Артист уже звучав у кімнаті з людського замовлення або є в лайках.</summary>
    public bool IsKnown(string artist) => Current.Artists.Contains(AutoDj.ArtistKey(artist));

    /// <summary>
    /// Наскільки підняти чи притиснути кандидата під смак кімнати.
    /// </summary>
    /// <param name="bias">
    /// Сила тиску: 1 — коли в ефірі вибір самого Глека (тоді й тягнути назад найпотрібніше),
    /// менше — коли трек поставила людина: її вибір сам по собі теж заява про смак.
    /// </param>
    public double Multiplier(string artist, string title, double bias)
    {
        var k = Math.Clamp(bias, 0, 1) * Math.Clamp(options.CurrentValue.TasteBias, 0, 2);
        if (k <= 0) return 1;
        var p = Current;
        var m = 1.0;
        if (p.Artists.Contains(AutoDj.ArtistKey(artist))) m *= 1 + 0.7 * k;
        if (p.Script.Length > 0 && p.Lean > 0)
        {
            var script = ScriptOf(artist, title);
            if (script == p.Script) m *= 1 + 0.35 * p.Lean * k;
            else if (script.Length > 0) m *= 1 - 0.6 * p.Lean * k;
        }
        return m;
    }

    /// <summary>
    /// Якір проти дрейфу: свіже людське замовлення (а як таких нема — щось лайкнуте), від якого
    /// Глек будує поради, коли в ефірі вже його власний вибір. Свіжіше важить більше, але й старіше
    /// має шанс, і кілька останніх якорів поспіль не повторюються. Від одного ніка — не більше
    /// <see cref="MaxShare"/> пулу.
    /// </summary>
    public TrackInfo? Anchor(IReadOnlySet<string> exclude)
    {
        bool Ok(TrackInfo t) => !exclude.Contains(t.Id) && !VoiceService.IsVoice(t.Id);
        var notPeople = Bots.Load(db);
        var pool = Fair(Requests(notPeople).Where(x => Ok(x.Track)), AnchorPool);
        if (pool.Count == 0) pool = Fair(Likes(notPeople).Where(x => Ok(x.Track)).DistinctBy(x => x.Track.Id), AnchorPool);
        if (pool.Count == 0) return null;
        lock (_lock)
        {
            var fresh = pool.Where(t => !_lastAnchors.Contains(t.Id)).ToList();
            if (fresh.Count > 0) pool = fresh;
            var weights = pool.Select((_, i) => 1.0 / (i + 2)).ToList();
            var roll = _rng.NextDouble() * weights.Sum();
            var pick = pool[^1];
            for (var i = 0; i < pool.Count; i++)
            {
                roll -= weights[i];
                if (roll > 0) continue;
                pick = pool[i];
                break;
            }
            _lastAnchors.Enqueue(pick.Id);
            while (_lastAnchors.Count > 4) _lastAnchors.Dequeue();
            return pick;
        }
    }
}
