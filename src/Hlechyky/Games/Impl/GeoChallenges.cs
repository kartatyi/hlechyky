using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hlechyky.Games.Impl;

/// <summary>Раунд виклику: місце (id банку чи «u:…» фото друга), номер фото місця, км і очки автора (км null — без шпильки).</summary>
public sealed record GeoChRound(string Place, int Photo, double? Km, int Points);

/// <summary>Спроба друга: км і очки по вже зіграних раундах; <c>Done</c> — дограно до кінця.</summary>
public sealed record GeoChTry(string Nick, string Key, double?[] Km, int[] Points, bool Done, DateTimeOffset At)
{
    [JsonIgnore] public long Total => Points.Sum(p => (long)p);
}

/// <summary>Виклик «Зіграй мої 5 фото»: хто кинув, коли, ті самі місця й фото, його очки — і спроби друзів.</summary>
public sealed record GeoCh(string Id, string Nick, string Key, DateTimeOffset At, string? Day, GeoChRound[] Rounds, GeoChTry[] Tries)
{
    [JsonIgnore] public long Total => Rounds.Sum(r => (long)r.Points);
    public GeoChTry? TryOf(string key) => Tries.FirstOrDefault(t => t.Key == key);
}

/// <summary>
/// Виклики «Де це?» (пакет 4): після тренування кидаєш друзям свої п'ять фото, кожен грає їх раз, коли зручно, —
/// і наприкінці порівняння по раундах. Таблиця <c>geo_challenge</c> (id, час, JSON цілого виклику).
/// <para>
/// Гра під замком кімнати бачить лише пам'ять (<see cref="_all"/>): база читається фоном на старті, а кожна зміна
/// пишеться фоном (<see cref="Save"/>) — пише завжди <b>останній</b> стан з пам'яті, тож порядок фонових записів
/// не важить.
/// </para>
/// </summary>
public sealed class GeoChallenges
{
    /// <summary>Стільки днів виклик видно в списку (дограти почате можна й пізніше).</summary>
    public const int KeepDays = 14;
    /// <summary>Не більше стількох викликів від одного ніка за київську добу — щоб Журнал не засипало.</summary>
    public const int PerDay = 5;

    public const string Gone = "Цього виклику вже нема";
    public const string Own = "Це твій виклик — дивись, як грають друзі";
    public const string Played = "Цей виклик ти вже зіграв — спроба одна";
    public const string TooMany = "На сьогодні викликів досить — завтра ще";

    const string Schema = "CREATE TABLE IF NOT EXISTS geo_challenge(id TEXT PRIMARY KEY, at TEXT NOT NULL, data TEXT NOT NULL)";

    readonly Db? _db;
    readonly object _gate = new(), _write = new();
    readonly ConcurrentDictionary<string, GeoCh> _all = new(StringComparer.Ordinal);

    public GeoChallenges(Db? db)
    {
        _db = db;
        Loaded = db is null ? Task.CompletedTask : Task.Run(Load);
    }

    /// <summary>Фонове читання бази на старті (тести чекають на нього).</summary>
    public Task Loaded { get; }

    public static string KeyOf(string? nick) => Auth.NickKey(nick);

    public GeoCh? Find(string? id) => id is null ? null : _all.GetValueOrDefault(id);

    static bool Fresh(GeoCh ch, DateTimeOffset now) => now - ch.At <= TimeSpan.FromDays(KeepDays);

    /// <summary>Виклик з «Де це? дня» відкривається наступного дня — щоб не спойлерити день тим, хто ще не грав.</summary>
    public static bool Open(GeoCh ch, DateTimeOffset now) => ch.Day is null || string.CompareOrdinal(Days.Of(now), ch.Day) > 0;

    /// <summary>Чужі свіжі виклики для <paramref name="nick"/> — новіші першими.</summary>
    public List<GeoCh> For(string nick, DateTimeOffset now)
    {
        var key = KeyOf(nick);
        return [.. _all.Values.Where(c => c.Key != key && Fresh(c, now) && Open(c, now)).OrderByDescending(c => c.At)];
    }

    /// <summary>Свої свіжі виклики — новіші першими.</summary>
    public List<GeoCh> Mine(string nick, DateTimeOffset now)
    {
        var key = KeyOf(nick);
        return [.. _all.Values.Where(c => c.Key == key && Fresh(c, now)).OrderByDescending(c => c.At)];
    }

    /// <summary>Почата й не дограна спроба ніка (закрив вкладку посеред) — з неї гра й продовжить.</summary>
    public GeoCh? Unfinished(string? nick)
    {
        var key = KeyOf(nick);
        return key.Length == 0 ? null : _all.Values.Where(c => c.TryOf(key) is { Done: false }).OrderByDescending(c => c.At).FirstOrDefault();
    }

    /// <summary>Кинути виклик: null й текст відмови, якщо не можна.</summary>
    public (GeoCh? Ch, string? Error) Add(string nick, IReadOnlyList<GeoChRound> rounds, string? day, DateTimeOffset now)
    {
        var key = KeyOf(nick);
        lock (_gate)
        {
            var today = Days.Of(now);
            if (_all.Values.Count(c => c.Key == key && Days.Of(c.At) == today) >= PerDay) return (null, TooMany);
            if (day is not null && _all.Values.Any(c => c.Key == key && c.Day == day)) return (null, "Виклик з цього дня вже кинуто");
            string id;
            do id = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
            while (_all.ContainsKey(id));
            var ch = new GeoCh(id, nick, key, now, day, [.. rounds], []);
            _all[id] = ch;
            Save(id);
            return (ch, null);
        }
    }

    /// <summary>
    /// Узятись за виклик: нова спроба (одна на нік), або вже почата — тоді її ж і продовжуємо. Автор свого не грає.
    /// </summary>
    public (GeoCh? Ch, GeoChTry? Try, string? Error) Begin(string? id, string nick, DateTimeOffset now)
    {
        var key = KeyOf(nick);
        lock (_gate)
        {
            if (Find(id) is not { } ch) return (null, null, Gone);
            if (ch.Key == key) return (null, null, Own);
            if (ch.TryOf(key) is { } had) return had.Done ? (null, null, Played) : (ch, had, null);
            if (!Fresh(ch, now) || !Open(ch, now)) return (null, null, Gone);
            var t = new GeoChTry(nick, key, [], [], false, now);
            _all[ch.Id] = ch = ch with { Tries = [.. ch.Tries, t] };
            Save(ch.Id);
            return (ch, t, null);
        }
    }

    /// <summary>Ще один зіграний раунд спроби.</summary>
    public void Progress(string id, string key, double? km, int points) =>
        Change(id, key, t => t.Done ? t : t with { Km = [.. t.Km, km], Points = [.. t.Points, points] });

    /// <summary>Спробу дограно — далі її не продовжити й не почати знову.</summary>
    public GeoCh? Finish(string id, string key) => Change(id, key, t => t with { Done = true });

    GeoCh? Change(string id, string key, Func<GeoChTry, GeoChTry> f)
    {
        lock (_gate)
        {
            if (Find(id) is not { } ch) return null;
            var i = Array.FindIndex(ch.Tries, t => t.Key == key);
            if (i < 0) return ch;
            var tries = (GeoChTry[])ch.Tries.Clone();
            tries[i] = f(tries[i]);
            _all[id] = ch = ch with { Tries = tries };
            Save(id);
            return ch;
        }
    }

    // ---------------------------------------------------------------------------------------
    // база — лише фоном
    // ---------------------------------------------------------------------------------------

    void Save(string id)
    {
        if (_db is null) return;
        _ = Task.Run(() => Write(id));
    }

    void Write(string id)
    {
        try
        {
            lock (_write)
            {
                // останній стан з пам'яті, а не той, що був у момент зміни: так пізніший запис не затре новіший
                if (Find(id) is not { } ch) return;
                var json = JsonSerializer.Serialize(ch);
                _db!.With(c =>
                {
                    using (var s = c.CreateCommand()) { s.CommandText = Schema; s.ExecuteNonQuery(); }
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "INSERT OR REPLACE INTO geo_challenge(id, at, data) VALUES($id, $at, $data)";
                    cmd.Parameters.AddWithValue("$id", ch.Id);
                    cmd.Parameters.AddWithValue("$at", ch.At.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                    cmd.Parameters.AddWithValue("$data", json);
                    cmd.ExecuteNonQuery();
                });
            }
        }
        catch (Exception)
        {
            // не записали — після рестарту виклику (чи останнього раунду спроби) не буде; гра від цього не стає
        }
    }

    void Load()
    {
        try
        {
            _db!.With(c =>
            {
                using (var s = c.CreateCommand()) { s.CommandText = Schema; s.ExecuteNonQuery(); }
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT data FROM geo_challenge";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    GeoCh? ch;
                    try { ch = JsonSerializer.Deserialize<GeoCh>(r.GetString(0)); }
                    catch (JsonException) { continue; }
                    // кинуте, поки читали базу, — новіше: не затираємо
                    if (ch is { Id: not null, Rounds: not null, Tries: not null }) _all.TryAdd(ch.Id, ch);
                }
            });
        }
        catch (Exception)
        {
            // без бази — просто без старих викликів
        }
    }
}
