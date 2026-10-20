using System.Collections.Concurrent;
using System.Globalization;

namespace Hlechyky.Games.Impl;

/// <summary>Найслабша область гравця: де в середньому найдалі від правди.</summary>
public sealed record GeoWeak(string Region, double AvgKm, int Rounds);

/// <summary>Що відомо про гравця: <see cref="Weak"/> — null, поки даних мало (див. <see cref="GeoRounds.MinRounds"/>).</summary>
public sealed record GeoWeakInfo(GeoWeak? Weak, int Rounds, int Regions);

/// <summary>
/// Історія раундів «Де це?» (пакет 4): кожна шпилька з будь-якого режиму — стіл, тренування, «Де це? дня» — з
/// відстанню й областю правди (таблиця <c>geo_rounds</c>). З неї тренування пропонує «🎯 Моя слабка: &lt;область&gt;».
/// До пакета 4 відстаней ніде не писали (лише <c>geo_seen</c> — що бачив, і підсумкова сума), тож історія
/// починається з деплою. Гра звертається лише до пам'яті (нік → область → сума км і кількість), база — фоном:
/// під замком кімнати — жодного SQLite. Фото друзів не пишемо: їх «область» — не з банку.
/// </summary>
public sealed class GeoRounds
{
    /// <summary>Скільки раундів із шпилькою треба, щоб казати «слабка область».</summary>
    public const int MinRounds = 10;
    /// <summary>І зі скількох різних областей.</summary>
    public const int MinRegions = 3;
    /// <summary>Область із одним раундом — випадковість: беремо ті, де їх хоч стільки (якщо такі є).</summary>
    public const int MinPerRegion = 2;

    const string Schema = """
        CREATE TABLE IF NOT EXISTS geo_rounds(
            id INTEGER PRIMARY KEY, nick_key TEXT NOT NULL, nick TEXT NOT NULL, place_id TEXT NOT NULL,
            region TEXT NOT NULL, km REAL NOT NULL, points INTEGER NOT NULL, mode TEXT NOT NULL, at TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS geo_rounds_nick ON geo_rounds(nick_key);
        """;

    readonly Db? _db;
    /// <summary>Записи, новіші за цю мить, уже в пам'яті з <see cref="Note"/> — старт їх із бази не дочитує вдруге.</summary>
    readonly string _since;
    /// <summary>нік-ключ → область → (сума км, раундів).</summary>
    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, (double Km, int N)>> _mem = new(StringComparer.Ordinal);
    bool _schema;

    public GeoRounds(Db? db, IClock? clock = null)
    {
        _db = db;
        _since = Iso((clock ?? new SystemClock()).UtcNow);
        if (db is not null) _ = Task.Run(Load);
    }

    static string Iso(DateTimeOffset at) => at.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    /// <summary>Одна шпилька: пам'ять — одразу, база — фоном. Ніколи не кидає.</summary>
    public void Note(string nick, string placeId, string region, double km, int points, string mode, DateTimeOffset at)
    {
        if (string.IsNullOrEmpty(region) || double.IsNaN(km)) return;
        var key = GeoSeen.NickKey(nick);
        Add(key, region, km);
        if (_db is null) return;
        _ = Task.Run(() =>
        {
            try
            {
                _db.With(c =>
                {
                    Ensure(c);
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "INSERT INTO geo_rounds(nick_key, nick, place_id, region, km, points, mode, at) VALUES($k, $n, $p, $r, $km, $pt, $m, $t)";
                    cmd.Parameters.AddWithValue("$k", key);
                    cmd.Parameters.AddWithValue("$n", nick);
                    cmd.Parameters.AddWithValue("$p", placeId);
                    cmd.Parameters.AddWithValue("$r", region);
                    cmd.Parameters.AddWithValue("$km", Math.Round(km, 1));
                    cmd.Parameters.AddWithValue("$pt", points);
                    cmd.Parameters.AddWithValue("$m", mode);
                    cmd.Parameters.AddWithValue("$t", Iso(at));
                    cmd.ExecuteNonQuery();
                });
            }
            catch (Exception) { /* історія — для порад, а не облік */ }
        });
    }

    void Add(string key, string region, double km)
    {
        var regions = _mem.GetOrAdd(key, _ => new(StringComparer.Ordinal));
        regions.AddOrUpdate(region, (km, 1), (_, was) => (was.Km + km, was.N + 1));
    }

    /// <summary>Що відомо про гравця (лише пам'ять): скільки раундів і областей і найслабша, якщо даних досить.</summary>
    public GeoWeakInfo Info(string nick)
    {
        if (!_mem.TryGetValue(GeoSeen.NickKey(nick), out var regions)) return new(null, 0, 0);
        var list = regions.Select(kv => (Region: kv.Key, kv.Value.Km, kv.Value.N)).Where(r => r.N > 0).ToList();
        var rounds = list.Sum(r => r.N);
        if (rounds < MinRounds || list.Count < MinRegions) return new(null, rounds, list.Count);
        var solid = list.Where(r => r.N >= MinPerRegion).ToList();
        if (solid.Count == 0) solid = list;
        var w = solid.OrderByDescending(r => r.Km / r.N).ThenBy(r => r.Region, StringComparer.Ordinal).First();
        return new(new GeoWeak(w.Region, w.Km / w.N, w.N), rounds, list.Count);
    }

    void Load()
    {
        try
        {
            _db!.With(c =>
            {
                Ensure(c);
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT nick_key, region, km FROM geo_rounds WHERE at < $s";
                cmd.Parameters.AddWithValue("$s", _since);
                using var r = cmd.ExecuteReader();
                while (r.Read()) Add(r.GetString(0), r.GetString(1), r.GetDouble(2));
            });
        }
        catch (Exception) { /* без бази — історія лише з цього запуску */ }
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
