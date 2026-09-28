using System.Collections.Concurrent;
using System.Globalization;

namespace Hlechyky.Games.Impl;

/// <summary>Рядок таблиці дня «Скільки? дня»: хто, скільки очок і смужка емодзі по запитаннях.</summary>
public sealed record SkilkyDayRow(string Nick, int Points, string Marks);

/// <summary>
/// Таблиця дня «Скільки? дня». Щоденний каркас (<see cref="IDailyGame"/>, <c>daily_results</c>) рахує «менше —
/// краще» (спроби й мілісекунди), а тут очки — тож своя таблиця <c>skilky_daily</c>. Гра звертається лише до пам'яті:
/// запис і перше читання дня йдуть фоном (під замком кімнати — жодного SQLite).
/// </summary>
public sealed class SkilkyDailyBoard(Db? db) : IDailyPoints
{
    /// <summary>Панель «☀ Сьогодні» показує очки дня, а не «за 1 спробу».</summary>
    string IDailyPoints.Game => "skilky-daily";
    long? IDailyPoints.Points(string day, string nick) => Of(day, nick)?.Points;

    const string Schema = """
        CREATE TABLE IF NOT EXISTS skilky_daily(
            day TEXT NOT NULL, nick_key TEXT NOT NULL, nick TEXT NOT NULL, points INTEGER NOT NULL,
            marks TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL, PRIMARY KEY(day, nick_key)) WITHOUT ROWID;
        """;

    /// <summary>День → нік-ключ → рядок. Тримаємо кілька останніх днів: більше нікому не треба.</summary>
    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, SkilkyDayRow>> _days = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, bool> _loading = new(StringComparer.Ordinal);
    bool _schema;

    /// <summary>Записати результат (перший за день — назавжди: «Ще раз» таблицю не переписує).</summary>
    public void Record(string day, string nick, int points, string marks, DateTimeOffset at)
    {
        var key = SkilkySeen.NickKey(nick);
        var row = new SkilkyDayRow(nick, points, marks);
        if (!Day(day).TryAdd(key, row)) return;
        if (db is null) return;
        _ = Task.Run(() =>
        {
            try
            {
                db.With(c =>
                {
                    Ensure(c);
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "INSERT OR IGNORE INTO skilky_daily(day, nick_key, nick, points, marks, created_at) VALUES($d, $k, $n, $p, $m, $t)";
                    cmd.Parameters.AddWithValue("$d", day);
                    cmd.Parameters.AddWithValue("$k", key);
                    cmd.Parameters.AddWithValue("$n", nick);
                    cmd.Parameters.AddWithValue("$p", points);
                    cmd.Parameters.AddWithValue("$m", marks);
                    cmd.Parameters.AddWithValue("$t", at.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                    cmd.ExecuteNonQuery();
                });
            }
            catch (Exception) { /* таблиця дня — радість, а не облік: без запису гра не ламається */ }
        });
    }

    /// <summary>Уся таблиця дня від більшого (рівні — хто раніше записався, той вище; тут — за ніком). Не чекає на базу.</summary>
    public IReadOnlyList<SkilkyDayRow> Top(string day)
    {
        var rows = Day(day);
        return [.. rows.Values.OrderByDescending(r => r.Points).ThenBy(r => r.Nick, StringComparer.Ordinal)];
    }

    /// <summary>Мій рядок сьогодні (з пам'яті).</summary>
    public SkilkyDayRow? Of(string day, string nick) => Day(day).GetValueOrDefault(SkilkySeen.NickKey(nick));

    /// <summary>Прогріти день із бази — фоном; відповідь прийде з наступним видом.</summary>
    ConcurrentDictionary<string, SkilkyDayRow> Day(string day)
    {
        var rows = _days.GetOrAdd(day, _ => new ConcurrentDictionary<string, SkilkyDayRow>(StringComparer.Ordinal));
        if (db is not null && _loading.TryAdd(day, true))
        {
            // Старі дні з пам'яті геть: лишаємо тиждень.
            foreach (var old in _days.Keys.Where(d => string.CompareOrdinal(d, day) < 0).OrderByDescending(d => d).Skip(7).ToList())
            {
                _days.TryRemove(old, out _);
                _loading.TryRemove(old, out _);
            }
            _ = Task.Run(() => Load(day, rows));
        }
        return rows;
    }

    /// <summary>Лише для тестів і прогріву: дочитати день із бази зараз.</summary>
    public void LoadNow(string day)
    {
        _loading[day] = true;
        Load(day, _days.GetOrAdd(day, _ => new ConcurrentDictionary<string, SkilkyDayRow>(StringComparer.Ordinal)));
    }

    void Load(string day, ConcurrentDictionary<string, SkilkyDayRow> rows)
    {
        if (db is null) return;
        try
        {
            db.With(c =>
            {
                Ensure(c);
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT nick_key, nick, points, marks FROM skilky_daily WHERE day = $d";
                cmd.Parameters.AddWithValue("$d", day);
                using var r = cmd.ExecuteReader();
                while (r.Read()) rows.TryAdd(r.GetString(0), new SkilkyDayRow(r.GetString(1), r.GetInt32(2), r.GetString(3)));
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
