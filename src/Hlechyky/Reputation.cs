namespace Hlechyky;

/// <summary>
/// Репутація пісні й артиста з наших власних програвань: як часто їх скіпали, коли хтось слухав, і хто їх лайкнув.
/// <para>
/// Дані з 10.10 по 25.10 (лише «живі» програвання Глека — хтось із присутніх у ці ±20 хв щось робив): пісню, яку
/// лайкнув хтось із присутніх, скіпали 8 %, решту — 39 %; пісню з доброю репутацією — 12 %, ще не грану тут — 40 %.
/// А от свіжість не підтвердилась: що частіше пісня звучала й що недавніше, то рідше її скіпали (зіграна за добу
/// — 24 % проти 32 %), тож довшого відпочинку пісням тут нема — він прибрав би саме те, що заходить.
/// </para>
/// Кількість скіпів згладжена до загальної частки (<see cref="Prior"/>) так, ніби кожна пісня вже мала
/// <see cref="Smoothing"/> звичайних програвань: одне-два випадкові скіпи нічого не вирішують.
/// </summary>
public sealed class Reputation(Db db)
{
    /// <summary>Скільки «звичайних» програвань домішуємо до кожної пісні й артиста.</summary>
    public const double Smoothing = 5;
    /// <summary>За скільки днів рахуємо програвання.</summary>
    public const int Days = 90;

    /// <param name="Prior">Загальна частка скіпів серед почутих програвань.</param>
    /// <param name="Songs">Ключ пісні (<see cref="AutoDj.KeyOf"/>) → (почутих програвань, скіпів).</param>
    /// <param name="Artists">Ключ артиста → (почутих програвань, скіпів).</param>
    /// <param name="Likers">Ключ пісні → хто її лайкнув (ключі ніків).</param>
    public sealed record Table(double Prior, Dictionary<string, (int Plays, int Skips)> Songs,
        Dictionary<string, (int Plays, int Skips)> Artists, Dictionary<string, HashSet<string>> Likers)
    {
        public static readonly Table Empty = new(0, [], [], []);
    }

    static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
    readonly object _lock = new();
    Table? _cached;
    DateTime _builtAt = DateTime.MinValue;

    public Table Current
    {
        get
        {
            lock (_lock)
            {
                if (_cached is not null && DateTime.UtcNow - _builtAt < Ttl) return _cached;
                _builtAt = DateTime.UtcNow;
                return _cached = Build(db.HeardPlays(DateTimeOffset.UtcNow.AddDays(-Days)),
                    db.RecentLikes(int.MaxValue).Select(x => (x.Track.Artist, x.Track.Title, x.Nick)));
            }
        }
    }

    public static Table Build(IEnumerable<(string Artist, string Title, bool Skipped)> heard, IEnumerable<(string Artist, string Title, string Nick)> likes)
    {
        var songs = new Dictionary<string, (int, int)>();
        var artists = new Dictionary<string, (int, int)>();
        int plays = 0, skips = 0;
        foreach (var (artist, title, skipped) in heard)
        {
            var (a, t) = RoomTaste.CreditOf(artist, title);
            var s = skipped ? 1 : 0;
            plays++; skips += s;
            var sk = AutoDj.KeyOf(a, t);
            var (sp, ss) = songs.GetValueOrDefault(sk);
            songs[sk] = (sp + 1, ss + s);
            var ak = AutoDj.ArtistKey(a);
            var (ap, @as) = artists.GetValueOrDefault(ak);
            artists[ak] = (ap + 1, @as + s);
        }
        var likers = new Dictionary<string, HashSet<string>>();
        foreach (var (artist, title, nick) in likes)
        {
            var (a, t) = RoomTaste.CreditOf(artist, title);
            var key = AutoDj.KeyOf(a, t);
            if (!likers.TryGetValue(key, out var set)) likers[key] = set = [];
            set.Add(Auth.NickKey(nick));
        }
        return new Table(plays == 0 ? 0 : skips / (double)plays, songs, artists, likers);
    }

    /// <summary>
    /// Множник для кандидата: пісня з доброю репутацією — вгору (до ×1,6), з поганою — вниз (до ×0,6); артист — те саме,
    /// але слабше (×0,8…×1,25); лайкнута кимось із присутніх — ще ×1,5 (нікого нема — лайкнута будь-ким, ×1,25).
    /// Пісня й артист, яких тут ще не чули, — ×1: новизну не караємо, лише не підіймаємо.
    /// </summary>
    public static double Multiplier(Table r, string artist, string title, IReadOnlyCollection<string>? listening)
    {
        if (r.Prior <= 0) return 1;
        var song = AutoDj.KeyOf(artist, title);
        var m = 1.0;
        if (r.Songs.TryGetValue(song, out var s)) m *= Odds(r.Prior, s, 0.5, 0.6, 1.6);
        if (r.Artists.TryGetValue(AutoDj.ArtistKey(artist), out var a)) m *= Odds(r.Prior, a, 0.25, 0.8, 1.25);
        if (r.Likers.TryGetValue(song, out var likers))
        {
            var here = listening is { Count: > 0 } ? listening.Select(Auth.NickKey).ToHashSet() : null;
            if (here is null) m *= 1.25;
            else if (likers.Overlaps(here)) m *= 1.5;
        }
        return m;
    }

    public double Multiplier(string artist, string title, IReadOnlyCollection<string>? listening) => Multiplier(Current, artist, title, listening);

    /// <summary>(загальна частка ÷ згладжена частка скіпів)^<paramref name="power"/>, у межах.</summary>
    static double Odds(double prior, (int Plays, int Skips) x, double power, double min, double max)
    {
        var p = (x.Skips + Smoothing * prior) / (x.Plays + Smoothing);
        return Math.Clamp(Math.Pow(prior / Math.Max(p, 1e-3), power), min, max);
    }
}
