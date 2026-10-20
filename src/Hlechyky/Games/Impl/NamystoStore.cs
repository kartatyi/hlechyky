using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Найкраще ніка на трасі «Намиста»: очки (і пройдена чи ні).</summary>
public sealed record NamystoBest(string Nick, int Level, long Best, bool Cleared);

/// <summary>
/// Кампанія «Намиста»: які траси нік пройшов і найкращі очки на кожній. Гра звертається лише до пам'яті (під замком
/// кімнати — жодного SQLite): таблиця читається раз у конструкторі (вона крихітна), запис іде фоном.
/// </summary>
public sealed class NamystoStore
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS namysto_best(nick_key TEXT NOT NULL, level INTEGER NOT NULL, nick TEXT NOT NULL,
          best INTEGER NOT NULL, cleared INTEGER NOT NULL, at TEXT NOT NULL, PRIMARY KEY(nick_key, level)) WITHOUT ROWID;
        """;

    readonly Db? _db;
    readonly ConcurrentDictionary<string, ConcurrentDictionary<int, NamystoBest>> _rows = new(StringComparer.Ordinal);

    public NamystoStore(Db? db, ILogger<NamystoStore>? log = null)
    {
        _db = db;
        if (db is null) return;
        try
        {
            db.With(c =>
            {
                using (var cmd = c.CreateCommand()) { cmd.CommandText = Schema; cmd.ExecuteNonQuery(); }
                using var q = c.CreateCommand();
                q.CommandText = "SELECT nick_key, level, nick, best, cleared FROM namysto_best";
                using var r = q.ExecuteReader();
                while (r.Read())
                    Of(r.GetString(0))[r.GetInt32(1)] = new NamystoBest(r.GetString(2), r.GetInt32(1), r.GetInt64(3), r.GetInt32(4) != 0);
            });
        }
        catch (Exception ex) { log?.LogWarning(ex, "не прочитав namysto_best — кампанія з чистого аркуша"); }
    }

    public static string Key(string nick) => Economy.Economy.Key(nick);

    ConcurrentDictionary<int, NamystoBest> Of(string nickKey) => _rows.GetOrAdd(nickKey, _ => new ConcurrentDictionary<int, NamystoBest>());

    public NamystoBest? Best(string nick, int level) => Of(Key(nick)).GetValueOrDefault(level);

    /// <summary>Найдальша відкрита траса: перша — завжди, кожна наступна — коли пройдено попередню.</summary>
    public int Open(string nick)
    {
        var mine = Of(Key(nick));
        var n = 1;
        while (n < NamystoTracks.Count && mine.TryGetValue(n, out var b) && b.Cleared) n++;
        return n;
    }

    public bool AllCleared(string nick)
    {
        var mine = Of(Key(nick));
        for (var n = 1; n <= NamystoTracks.Count; n++)
            if (!mine.TryGetValue(n, out var b) || !b.Cleared) return false;
        return true;
    }

    /// <summary>Записати партію: (уперше пройдено, новий особистий рекорд).</summary>
    public (bool FirstClear, bool Record) Record(string nick, int level, long score, bool cleared, DateTimeOffset at)
    {
        var key = Key(nick);
        var mine = Of(key);
        var old = mine.GetValueOrDefault(level);
        var first = cleared && old is not { Cleared: true };
        var record = old is null || score > old.Best;
        if (!first && !record) return (false, false);
        var row = new NamystoBest(nick, level, Math.Max(score, old?.Best ?? 0), cleared || old?.Cleared == true);
        mine[level] = row;
        if (_db is { } db)
            _ = Task.Run(() =>
            {
                try
                {
                    db.With(c =>
                    {
                        using var cmd = c.CreateCommand();
                        cmd.CommandText = """
                            INSERT INTO namysto_best(nick_key, level, nick, best, cleared, at) VALUES($k, $l, $n, $b, $c, $t)
                            ON CONFLICT(nick_key, level) DO UPDATE SET nick = $n, best = MAX(best, $b), cleared = MAX(cleared, $c), at = $t
                            """;
                        cmd.Parameters.AddWithValue("$k", key);
                        cmd.Parameters.AddWithValue("$l", level);
                        cmd.Parameters.AddWithValue("$n", nick);
                        cmd.Parameters.AddWithValue("$b", row.Best);
                        cmd.Parameters.AddWithValue("$c", row.Cleared ? 1 : 0);
                        cmd.Parameters.AddWithValue("$t", at.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                        cmd.ExecuteNonQuery();
                    });
                }
                catch (Exception) { /* рекорди — радість, а не облік: без запису гра не ламається */ }
            });
        return (first, record);
    }

    /// <summary>Найкращі на трасі серед усіх (від більшого).</summary>
    public IReadOnlyList<NamystoBest> Top(int level, int take) =>
        [.. _rows.Values.Select(m => m.GetValueOrDefault(level)).OfType<NamystoBest>()
            .OrderByDescending(b => b.Best).ThenBy(b => b.Nick, StringComparer.Ordinal).Take(take)];
}

/// <summary>Рядок таблиці дня «Намиста дня».</summary>
public sealed record NamystoDayRow(string Nick, int Points, int Popped, bool Won);

/// <summary>
/// Таблиця дня «Намиста дня» (очки — більше краще; перша спроба — назавжди). Той самий лад, що й у «Тиру дня»:
/// гра звертається лише до пам'яті, запис і перше читання дня — фоном.
/// </summary>
public sealed class NamystoDailyBoard(Db? db) : IDailyPoints
{
    string IDailyPoints.Game => "namysto-daily";
    long? IDailyPoints.Points(string day, string nick) => Of(day, nick)?.Points;

    const string Schema = """
        CREATE TABLE IF NOT EXISTS namysto_daily(
            day TEXT NOT NULL, nick_key TEXT NOT NULL, nick TEXT NOT NULL, points INTEGER NOT NULL,
            popped INTEGER NOT NULL, won INTEGER NOT NULL, created_at TEXT NOT NULL, PRIMARY KEY(day, nick_key)) WITHOUT ROWID;
        """;

    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, NamystoDayRow>> _days = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, bool> _loading = new(StringComparer.Ordinal);
    bool _schema;

    public void Record(string day, string nick, int points, int popped, bool won, DateTimeOffset at)
    {
        var key = NamystoStore.Key(nick);
        if (!Day(day).TryAdd(key, new NamystoDayRow(nick, points, popped, won)) || db is null) return;
        _ = Task.Run(() =>
        {
            try
            {
                db.With(c =>
                {
                    Ensure(c);
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "INSERT OR IGNORE INTO namysto_daily(day, nick_key, nick, points, popped, won, created_at) VALUES($d, $k, $n, $p, $o, $w, $t)";
                    cmd.Parameters.AddWithValue("$d", day);
                    cmd.Parameters.AddWithValue("$k", key);
                    cmd.Parameters.AddWithValue("$n", nick);
                    cmd.Parameters.AddWithValue("$p", points);
                    cmd.Parameters.AddWithValue("$o", popped);
                    cmd.Parameters.AddWithValue("$w", won ? 1 : 0);
                    cmd.Parameters.AddWithValue("$t", at.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                    cmd.ExecuteNonQuery();
                });
            }
            catch (Exception) { /* таблиця дня — радість, а не облік */ }
        });
    }

    /// <summary>Уся таблиця дня від більшого (рівні — хто зібрав увесь разок, далі за ніком).</summary>
    public IReadOnlyList<NamystoDayRow> Top(string day) =>
        [.. Day(day).Values.OrderByDescending(r => r.Points).ThenByDescending(r => r.Won).ThenBy(r => r.Nick, StringComparer.Ordinal)];

    public NamystoDayRow? Of(string day, string nick) => Day(day).GetValueOrDefault(NamystoStore.Key(nick));

    ConcurrentDictionary<string, NamystoDayRow> Day(string day)
    {
        var rows = _days.GetOrAdd(day, _ => new ConcurrentDictionary<string, NamystoDayRow>(StringComparer.Ordinal));
        if (db is not null && _loading.TryAdd(day, true))
        {
            foreach (var old in _days.Keys.Where(d => string.CompareOrdinal(d, day) < 0).OrderByDescending(d => d).Skip(7).ToList())
            {
                _days.TryRemove(old, out _);
                _loading.TryRemove(old, out _);
            }
            _ = Task.Run(() => Load(day, rows));
        }
        return rows;
    }

    void Load(string day, ConcurrentDictionary<string, NamystoDayRow> rows)
    {
        if (db is null) return;
        try
        {
            db.With(c =>
            {
                Ensure(c);
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT nick_key, nick, points, popped, won FROM namysto_daily WHERE day = $d";
                cmd.Parameters.AddWithValue("$d", day);
                using var r = cmd.ExecuteReader();
                while (r.Read()) rows.TryAdd(r.GetString(0), new NamystoDayRow(r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4) != 0));
            });
        }
        catch (Exception) { _loading.TryRemove(day, out _); }
    }

    void Ensure(Microsoft.Data.Sqlite.SqliteConnection c)
    {
        if (_schema) return;
        using var cmd = c.CreateCommand();
        cmd.CommandText = Schema;
        cmd.ExecuteNonQuery();
        _schema = true;
    }
}

public static class NamystoSetup
{
    public static IServiceCollection AddNamysto(IServiceCollection services)
    {
        services.AddSingleton(sp => new NamystoStore(sp.GetService<Db>(), sp.GetService<ILogger<NamystoStore>>()));
        services.AddSingleton(sp => new NamystoDailyBoard(sp.GetService<Db>()));
        services.AddSingleton<IDailyPoints>(sp => sp.GetRequiredService<NamystoDailyBoard>());
        return services;
    }

    /// <summary>
    /// Усі траси одним разом (≈40 КБ, браузер бере раз): точки жолоба різницями від попередньої (su) — ті самі, з яких
    /// рахує сервер, тож браузерові не треба тригонометрії. Вид кімнати несе лише номер траси.
    /// </summary>
    public static WebApplication MapNamysto(WebApplication app)
    {
        app.MapGet("/api/games/namysto/tracks", () => Results.Text(TracksJson.Value, "application/json; charset=utf-8"));
        return app;
    }

    /// <summary>Відбиток усіх трас: браузер, що тримає старі точки, знає, що їх треба перебрати.</summary>
    public static readonly Lazy<uint> TracksVersion = new(() =>
    {
        var h = 2166136261u;
        foreach (var l in NamystoTracks.Levels)
            foreach (var v in l.Wire()) h = (h ^ (uint)v) * 16777619u;
        return h;
    });

    public static readonly Lazy<string> TracksJson = new(() => JsonSerializer.Serialize(new
    {
        v = TracksVersion.Value,
        places = NamystoTracks.Places,
        tracks = NamystoTracks.Levels.Select(l => new
        {
            n = l.N, name = l.Name, place = l.Place, colors = l.Colors, beads = l.Beads, speed = l.Speed, par = l.Par,
            sx = l.Track.Sx, sy = l.Track.Sy, pts = Delta(l.Wire()),
        }),
    }));

    static int[] Delta(int[] xy)
    {
        var d = new int[xy.Length];
        for (var i = 0; i < xy.Length; i++) d[i] = i < 2 ? xy[i] : xy[i] - xy[i - 2];
        return d;
    }
}
