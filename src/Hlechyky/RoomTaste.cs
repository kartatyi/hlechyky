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
/// <para>
/// А ще в кожного свій смак (<see cref="Person"/>): поки слухає владік, він скіпає 1 % треків Глека, Smaug — 52 %, а
/// смак кімнати на 58 % був владіковим. Тож коли на сайті хтось слухає, множник і якорі рахуються за тими, хто тут:
/// «своє» — що свої для когось із присутніх, письмо — за їхніми нахилами, а артист, якого хтось із присутніх скіпає,
/// притискається (хто часто скіпає, того «не те» і важить більше). Нікого нема — як раніше, смаком кімнати.
/// </para>
/// </summary>
public sealed partial class RoomTaste(Db db, IOptionsMonitor<AutoDjOptions> options)
{
    /// <summary>Смак однієї людини.</summary>
    /// <param name="Key">Ключ ніка (<see cref="Auth.NickKey"/>).</param>
    /// <param name="Artists">Артисти з її замовлень (не скіпнутих) і лайків.</param>
    /// <param name="Script">Її переважне письмо (як у <see cref="Profile"/>).</param>
    /// <param name="Disliked">Артисти, яких вона скіпнула щонайменше двічі за <see cref="SkipDays"/> днів і сама не ставила й не лайкала.</param>
    /// <param name="SkippedSongs">Пісні (<see cref="AutoDj.KeyOf"/>), які вона скіпнула за <see cref="SkippedSongDays"/> днів і не лайкнула.</param>
    /// <param name="Tracks">Скільки в неї своїх треків: менше <see cref="MinTracksForPerson"/> — свого смаку ще не видно.</param>
    public sealed record Person(string Key, HashSet<string> Artists, string Script, double Lean, HashSet<string> Disliked,
        HashSet<string> SkippedSongs, int Tracks)
    {
        public bool HasTaste => Tracks >= MinTracksForPerson;
    }

    /// <summary>Від скількох своїх треків у людини вже є смак, на який можна спертися замість кімнатного.</summary>
    public const int MinTracksForPerson = 8;
    /// <summary>За скільки днів скіпи кажуть, що людині не заходить артист.</summary>
    public const int SkipDays = 60;
    /// <summary>Скільки днів не пропонувати пісню, яку хтось із присутніх скіпнув.</summary>
    public const int SkippedSongDays = 30;
    /// <summary>Сила «не заходить» одного присутнього: ×(1 − <see cref="DislikeDamp"/>·k).</summary>
    const double DislikeDamp = 0.5;
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

    /// <summary>Скільки замовлень дивимось назад: з запасом — і на людей, що ставлять рідко, і бо гості, скіпи й стеля на нік частину відсіють.</summary>
    const int Lookback = 5000;
    const int ProfileTracks = 200;
    const int AnchorPool = 15;

    static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    readonly object _lock = new();
    readonly Random _rng = new();
    readonly Queue<string> _lastAnchors = new();
    sealed record Snapshot(Profile Room, Dictionary<string, Person> People, Bots.Set NotPeople);
    Snapshot? _cached;
    DateTime _builtAt = DateTime.MinValue;

    Snapshot Snap
    {
        get
        {
            lock (_lock)
            {
                if (_cached is not null && DateTime.UtcNow - _builtAt < Ttl) return _cached;
                _builtAt = DateTime.UtcNow;
                return _cached = Build();
            }
        }
    }

    public Profile Current => Snap.Room;

    /// <summary>Смак кожної людини (ключ — <see cref="Auth.NickKey"/>), у кого є хоч щось: замовлення, лайки чи скіпи.</summary>
    public IReadOnlyDictionary<string, Person> People => Snap.People;

    /// <summary>Замовлення людей (акаунти, не боти), яких не скіпнули, — найсвіжіші перші, з виправленим артистом.</summary>
    List<(TrackInfo Track, string Nick, DateTimeOffset At)> Requests(Bots.Set notPeople) =>
        db.RecentRequests(Lookback)
            .Where(x => x.Nick is not null && !notPeople.Has(x.Nick) && !x.Skipped)
            .Select(x => (Credit(x.Track), x.Nick!, x.At))
            .ToList();

    List<(TrackInfo Track, string Nick, DateTimeOffset At)> Likes(Bots.Set notPeople) =>
        db.RecentLikes(Lookback).Where(x => !notPeople.Has(x.Nick)).Select(x => (Credit(x.Track), x.Nick, x.At)).ToList();

    Snapshot Build()
    {
        var notPeople = Bots.Load(db);
        var requests = Requests(notPeople);
        var likes = Likes(notPeople);
        var skips = db.SkipsSince(DateTimeOffset.UtcNow.AddDays(-SkipDays)).Where(x => !notPeople.Has(x.Nick))
            .Select(x => (Track: Credit(x.Track), x.Nick, x.At)).ToList();
        var artists = requests.Select(x => x.Track).Concat(likes.Select(x => x.Track))
            .Select(t => AutoDj.ArtistKey(t.Artist)).Where(a => a.Length > 0).ToHashSet();
        var forced = options.CurrentValue.PreferScript?.Trim().ToLowerInvariant() ?? "";
        var pinned = forced is "cyr" or "lat";
        var (script, lean) = pinned ? (forced, 1.0) : LeanOf(Fair(requests.Select(x => (x.Track, x.Nick)), ProfileTracks));
        var people = BuildPeople(requests, likes, skips, DateTimeOffset.UtcNow);
        // письмо, перебите вручну, — для всіх, а не лише для кімнати
        if (pinned) people = people.ToDictionary(kv => kv.Key, kv => kv.Value with { Script = forced, Lean = 1 });
        return new Snapshot(new Profile(artists, script, lean), people, notPeople);
    }

    /// <summary>Переважне письмо і наскільки воно переважає. Менше <see cref="MinTracksForLean"/> підписаних треків — нахилу нема.</summary>
    static (string Script, double Lean) LeanOf(IEnumerable<TrackInfo> tracks)
    {
        var counts = tracks.Select(t => ScriptOf(t.Artist, t.Title)).Where(s => s.Length > 0)
            .GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        var total = counts.Values.Sum();
        if (total < MinTracksForLean) return ("", 0);
        var top = counts.MaxBy(kv => kv.Value);
        // рівно навпіл — нахилу нема; що далі від половини, то впевненіше
        return (top.Key, Math.Clamp((top.Value / (double)total - 0.5) * 2, 0, 1));
    }

    /// <summary>
    /// Смак кожної людини: свої треки — замовлення (не скіпнуті) і лайки; «не заходить» — артисти, яких вона скіпнула
    /// двічі й більше, але сама не ставила й не лайкала (Smaug лайкає Jerry Heil і скіпає окремі її пісні — це про
    /// пісні, не про артистку); пісні, скіпнуті за <see cref="SkippedSongDays"/> днів, — крім лайкнутих.
    /// </summary>
    public static Dictionary<string, Person> BuildPeople(
        IEnumerable<(TrackInfo Track, string Nick, DateTimeOffset At)> requests,
        IEnumerable<(TrackInfo Track, string Nick, DateTimeOffset At)> likes,
        IEnumerable<(TrackInfo Track, string Nick, DateTimeOffset At)> skips,
        DateTimeOffset now)
    {
        var own = requests.Concat(likes).GroupBy(x => Auth.NickKey(x.Nick))
            .ToDictionary(g => g.Key, g => g.Select(x => x.Track).DistinctBy(t => t.Id).ToList());
        var liked = likes.GroupBy(x => Auth.NickKey(x.Nick))
            .ToDictionary(g => g.Key, g => g.Select(x => AutoDj.KeyOf(x.Track.Artist, x.Track.Title)).ToHashSet());
        var skipped = skips.GroupBy(x => Auth.NickKey(x.Nick)).ToDictionary(g => g.Key, g => g.ToList());

        var people = new Dictionary<string, Person>();
        foreach (var key in own.Keys.Union(skipped.Keys))
        {
            var tracks = own.GetValueOrDefault(key) ?? [];
            var artists = tracks.Select(t => AutoDj.ArtistKey(t.Artist)).Where(a => a.Length > 0).ToHashSet();
            var mine = skipped.GetValueOrDefault(key) ?? [];
            var likedSongs = liked.GetValueOrDefault(key) ?? [];
            var disliked = mine.GroupBy(x => AutoDj.ArtistKey(x.Track.Artist))
                .Where(g => g.Key.Length > 0 && g.Count() >= 2 && !artists.Contains(g.Key)).Select(g => g.Key).ToHashSet();
            var songs = mine.Where(x => x.At >= now.AddDays(-SkippedSongDays))
                .Select(x => AutoDj.KeyOf(x.Track.Artist, x.Track.Title)).Where(s => !likedSongs.Contains(s)).ToHashSet();
            var (script, lean) = LeanOf(tracks);
            people[key] = new Person(key, artists, script, lean, disliked, songs, tracks.Count);
        }
        return people;
    }

    /// <summary>
    /// Перші <paramref name="take"/> треків, але від кожного ніка не більше <paramref name="share"/> з них (хоч один;
    /// за замовчуванням <see cref="MaxShare"/>): хто закинув цілий плейлист, не перекриває решту кімнати. Порядок
    /// (свіжіше — вище) зберігається.
    /// </summary>
    public static List<TrackInfo> Fair(IEnumerable<(TrackInfo Track, string Nick)> rows, int take, double share = MaxShare)
    {
        var cap = Math.Max(1, (int)Math.Ceiling(take * share));
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
    /// <param name="listening">
    /// Хто зараз слухає на сайті. Порожньо (чи лише гості) — тиснемо смаком кімнати. Інакше — смаком присутніх:
    /// «свій» артист і письмо — середнє за ними (у кого свого смаку ще нема, той «голосує» кімнатним), а
    /// «не заходить» кожного присутнього притискає окремо.
    /// </param>
    public double Multiplier(string artist, string title, double bias, IReadOnlyCollection<string>? listening = null)
    {
        var k = Math.Clamp(bias, 0, 1) * Math.Clamp(options.CurrentValue.TasteBias, 0, 2);
        if (k <= 0) return 1;
        var snap = Snap;
        var room = snap.Room;
        var a = AutoDj.ArtistKey(artist);
        var script = ScriptOf(artist, title);
        var here = Present(snap, listening);
        if (here.Count == 0) return Lift(room.Artists, room.Script, room.Lean, a, script, k);

        double lift = 0, damp = 1;
        foreach (var p in here)
        {
            lift += p is { HasTaste: true } ? Lift(p.Artists, p.Script, p.Lean, a, script, k) : Lift(room.Artists, room.Script, room.Lean, a, script, k);
            if (p is not null && p.Disliked.Contains(a)) damp *= Math.Max(0.1, 1 - DislikeDamp * k);
        }
        return lift / here.Count * damp;
    }

    /// <summary>Підйом за «свого» артиста і звичне письмо (той самий, що й був для кімнати).</summary>
    static double Lift(HashSet<string> artists, string prefer, double lean, string artistKey, string script, double k)
    {
        var m = 1.0;
        if (artists.Contains(artistKey)) m *= 1 + 0.7 * k;
        if (prefer.Length > 0 && lean > 0)
        {
            if (script == prefer) m *= 1 + 0.35 * lean * k;
            else if (script.Length > 0) m *= 1 - 0.6 * lean * k;
        }
        return m;
    }

    /// <summary>
    /// Присутні люди: для кожного акаунта серед <paramref name="listening"/> — його смак або null (акаунт без жодних
    /// слідів). Гості й боти не рахуються. Порожньо — нікого з людей.
    /// </summary>
    static List<Person?> Present(Snapshot snap, IReadOnlyCollection<string>? listening) =>
        listening is null ? [] :
        [.. listening.Where(n => !snap.NotPeople.Has(n)).Select(Auth.NickKey).Distinct().Select(k => snap.People.GetValueOrDefault(k))];

    /// <summary>Пісні, які хтось із присутніх скіпнув за <see cref="SkippedSongDays"/> днів (ключі <see cref="AutoDj.KeyOf"/>).</summary>
    public HashSet<string> SkippedBy(IReadOnlyCollection<string>? listening)
    {
        var set = new HashSet<string>();
        foreach (var p in Present(Snap, listening)) if (p is not null) set.UnionWith(p.SkippedSongs);
        return set;
    }

    /// <summary>
    /// Якір проти дрейфу: свіже людське замовлення (а як таких нема — щось лайкнуте), від якого
    /// Глек будує поради, коли в ефірі вже його власний вибір. Свіжіше важить більше, але й старіше
    /// має шанс, і кілька останніх якорів поспіль не повторюються. Від одного ніка — не більше
    /// <see cref="MaxShare"/> пулу.
    /// <para>
    /// Слухають люди — якір з їхніх замовлень і лайків (кожному рівна частка пулу): сам владік — від владікового, удвох зі
    /// Smaug — навпіл. Нічого свого в присутніх нема — з кімнатного, як раніше.
    /// </para>
    /// </summary>
    public TrackInfo? Anchor(IReadOnlySet<string> exclude, IReadOnlyCollection<string>? listening = null)
    {
        bool Ok(TrackInfo t) => !exclude.Contains(t.Id) && !VoiceService.IsVoice(t.Id);
        var notPeople = Bots.Load(db);
        var requests = Requests(notPeople);
        var likes = Likes(notPeople);
        var pool = new List<TrackInfo>();
        var here = listening?.Where(n => !notPeople.Has(n)).Select(Auth.NickKey).ToHashSet() ?? [];
        if (here.Count > 0)
        {
            var theirs = requests.Concat(likes).Where(x => here.Contains(Auth.NickKey(x.Nick)) && Ok(x.Track))
                .OrderByDescending(x => x.At).DistinctBy(x => x.Track.Id).Select(x => (x.Track, x.Nick));
            pool = Fair(theirs, AnchorPool, Math.Max(MaxShare, 1.0 / here.Count));
        }
        if (pool.Count == 0) pool = Fair(requests.Where(x => Ok(x.Track)).Select(x => (x.Track, x.Nick)), AnchorPool);
        if (pool.Count == 0) pool = Fair(likes.Where(x => Ok(x.Track)).DistinctBy(x => x.Track.Id).Select(x => (x.Track, x.Nick)), AnchorPool);
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
