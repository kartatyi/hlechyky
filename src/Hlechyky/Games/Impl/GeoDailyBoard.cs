using System.Collections.Concurrent;
using System.Globalization;

namespace Hlechyky.Games.Impl;

/// <summary>Рядок таблиці дня «Де це? дня»: хто, скільки очок і смужка 🟩🟨🟧⬛ по фото.</summary>
public sealed record GeoDayRow(string Nick, int Points, string Marks);

/// <summary>
/// Таблиця дня «Де це? дня» (очки — більше краще). Щоденний каркас (<see cref="IDailyGame"/>, <c>daily_results</c>)
/// рахує «менше — краще», тож очки — у своїй таблиці <c>geo_daily</c>, а панель «☀ Сьогодні» бере їх через
/// <see cref="IDailyPoints"/>. Той самий лад, що й у «Скільки? дня»: гра звертається лише до пам'яті, а запис і
/// читання бази — фоном (під замком кімнати — жодного SQLite). Таблиця маленька (≈8 людей × день), тож
/// піднімаємо її цілу за останній рік один раз на старті — з неї ж і серія 🔥 кожного в таблиці дня.
/// </summary>
public sealed class GeoDailyBoard : IDailyPoints
{
    string IDailyPoints.Game => GeoMatch.DailyPuzzle;
    long? IDailyPoints.Points(string day, string nick) => Of(day, nick)?.Points;

    const string Schema = """
        CREATE TABLE IF NOT EXISTS geo_daily(
            day TEXT NOT NULL, nick_key TEXT NOT NULL, nick TEXT NOT NULL, points INTEGER NOT NULL,
            marks TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL, PRIMARY KEY(day, nick_key)) WITHOUT ROWID;
        """;

    /// <summary>Скільки днів назад піднімаємо з бази (серія довша за рік — і так «🔥 365+»).</summary>
    const int Window = 400;

    readonly Db? _db;
    /// <summary>День → нік-ключ → рядок.</summary>
    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, GeoDayRow>> _days = new(StringComparer.Ordinal);
    /// <summary>Нік-ключ → дні, коли зіграно (для серії).</summary>
    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> _played = new(StringComparer.Ordinal);
    bool _schema;

    public GeoDailyBoard(Db? db, IClock? clock = null)
    {
        _db = db;
        if (db is not null) _ = Task.Run(() => Load(Days.Today(clock ?? new SystemClock())));
    }

    public static string NickKey(string nick) => GeoSeen.NickKey(nick);

    /// <summary>Записати результат дня. Перший за день — назавжди: повтор (чи відновлення) таблицю не переписує.</summary>
    public void Record(string day, string nick, int points, string marks, DateTimeOffset at)
    {
        var key = NickKey(nick);
        if (!Add(day, key, new GeoDayRow(nick, points, marks)) || _db is null) return;
        _ = Task.Run(() =>
        {
            try
            {
                _db.With(c =>
                {
                    Ensure(c);
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "INSERT OR IGNORE INTO geo_daily(day, nick_key, nick, points, marks, created_at) VALUES($d, $k, $n, $p, $m, $t)";
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

    bool Add(string day, string key, GeoDayRow row)
    {
        if (!_days.GetOrAdd(day, _ => new(StringComparer.Ordinal)).TryAdd(key, row)) return false;
        _played.GetOrAdd(key, _ => new(StringComparer.Ordinal))[day] = true;
        return true;
    }

    /// <summary>Уся таблиця дня від більшого (рівні — за ніком). Лише пам'ять.</summary>
    public IReadOnlyList<GeoDayRow> Top(string day) =>
        _days.TryGetValue(day, out var rows)
            ? [.. rows.Values.OrderByDescending(r => r.Points).ThenBy(r => r.Nick, StringComparer.Ordinal)]
            : [];

    public GeoDayRow? Of(string day, string nick) =>
        _days.TryGetValue(day, out var rows) ? rows.GetValueOrDefault(NickKey(nick)) : null;

    /// <summary>
    /// Серія 🔥: скільки днів поспіль до <paramref name="day"/> зіграно. Сьогодні ще не грав — серія тримається
    /// вчорашнім днем (як у «Щоденному глеку»). Лише пам'ять.
    /// </summary>
    public int Streak(string nick, string day)
    {
        if (!_played.TryGetValue(NickKey(nick), out var days)) return 0;
        var d = DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!days.ContainsKey(Iso(d))) d = d.AddDays(-1);
        var n = 0;
        while (n < Window && days.ContainsKey(Iso(d))) { n++; d = d.AddDays(-1); }
        return n;
    }

    static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Підняти рік таблиці з бази. Записи, що встигли раніше, не дублюються (TryAdd за ключем).</summary>
    public void Load(string today)
    {
        if (_db is null) return;
        try
        {
            var from = Iso(DateOnly.ParseExact(today, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(-Window));
            _db.With(c =>
            {
                Ensure(c);
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT day, nick_key, nick, points, marks FROM geo_daily WHERE day >= $f";
                cmd.Parameters.AddWithValue("$f", from);
                using var r = cmd.ExecuteReader();
                while (r.Read()) Add(r.GetString(0), r.GetString(1), new GeoDayRow(r.GetString(2), r.GetInt32(3), r.GetString(4)));
            });
        }
        catch (Exception) { /* без бази таблиця дня — лише з цього запуску */ }
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
