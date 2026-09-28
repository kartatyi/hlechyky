using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Пам'ять «Де це?»: хто яке місце вже бачив і коли — за будь-яким столом, у тренуванні й у «Де це? дня».
/// У партію спершу йдуть місця, яких не бачив ніхто за столом, далі — бачені найдавніше. Як у «Скільки?»
/// (<see cref="SkilkySeen"/>), але гра до бази під замком кімнати не ходить: усе читається з пам'яті, яку
/// один раз фоном підняли з таблиці <c>geo_seen</c>, а записи летять у базу теж фоном.
/// <para>
/// Ніколи не кидає. Нема бази (тести з порожнім провайдером) — пам'ять живе до рестарту, і все.
/// </para>
/// </summary>
public sealed class GeoSeen
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS geo_seen(
            nick_key TEXT NOT NULL, place_id TEXT NOT NULL, seen_at TEXT NOT NULL,
            PRIMARY KEY(nick_key, place_id)) WITHOUT ROWID;
        """;

    readonly Db? _db;
    /// <summary>нік → (місце → коли бачив востаннє).</summary>
    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTimeOffset>> _mem = new(StringComparer.Ordinal);

    public GeoSeen(Db? db)
    {
        _db = db;
        if (db is not null) _ = Task.Run(Load);
    }

    /// <summary>Ключ ніка — той самий, що в економіці.</summary>
    public static string NickKey(string nick) => SkilkySeen.NickKey(nick);

    /// <summary>Для кожного місця, яке бачив хоч хтось із цих гравців, — найсвіжіший раз. Лише пам'ять.</summary>
    public Dictionary<string, DateTimeOffset> LastSeen(IEnumerable<string> nickKeys)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var key in nickKeys)
        {
            if (!_mem.TryGetValue(key, out var mine)) continue;
            foreach (var (place, at) in mine)
                if (!result.TryGetValue(place, out var had) || at > had) result[place] = at;
        }
        return result;
    }

    /// <summary>Ці гравці щойно побачили це місце: пам'ять — одразу, база — фоном.</summary>
    public void Mark(IReadOnlyCollection<string> nickKeys, string placeId, DateTimeOffset now)
    {
        if (nickKeys.Count == 0) return;
        foreach (var key in nickKeys)
            _mem.GetOrAdd(key, _ => new(StringComparer.Ordinal))[placeId] = now;
        if (_db is null) return;
        var keys = nickKeys.ToArray();
        _ = Task.Run(() => Write(keys, placeId, now));
    }

    void Load()
    {
        try
        {
            _db!.With(c =>
            {
                Ensure(c);
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT nick_key, place_id, seen_at FROM geo_seen";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var at = DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                    var mine = _mem.GetOrAdd(r.GetString(0), _ => new(StringComparer.Ordinal));
                    // свіже з пам'яті (партія встигла до кінця читання) не затираємо старим з бази
                    mine.AddOrUpdate(r.GetString(1), at, (_, had) => had > at ? had : at);
                }
                return 0;
            });
        }
        catch (Exception)
        {
            // пам'ять без бази — повтори трохи раніше, ніж могли б; партія від цього не страждає
        }
    }

    void Write(string[] keys, string placeId, DateTimeOffset now)
    {
        try
        {
            _db!.With(c =>
            {
                Ensure(c);
                using var tx = c.BeginTransaction();
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO geo_seen(nick_key, place_id, seen_at) VALUES($n, $p, $at)
                    ON CONFLICT(nick_key, place_id) DO UPDATE SET seen_at = excluded.seen_at
                    """;
                var n = cmd.Parameters.Add("$n", SqliteType.Text);
                cmd.Parameters.AddWithValue("$p", placeId);
                cmd.Parameters.AddWithValue("$at", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                foreach (var key in keys)
                {
                    n.Value = key;
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
                return 0;
            });
        }
        catch (Exception)
        {
            // не записали — після рестарту місце здасться свіжішим, ніж є; не біда
        }
    }

    bool _ready;

    void Ensure(SqliteConnection c)
    {
        if (_ready) return;
        using var cmd = c.CreateCommand();
        cmd.CommandText = Schema;
        cmd.ExecuteNonQuery();
        _ready = true;
    }
}
