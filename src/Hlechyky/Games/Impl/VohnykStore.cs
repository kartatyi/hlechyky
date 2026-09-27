using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Games.Impl;

/// <summary>Найкращий час пари на рівні: хто (ключ і як показати), скільки, зі скількома смертями й зірками.</summary>
public sealed record VohnykBest(string PairKey, int Level, int Ms, int Deaths, int Stars, string Nicks, DateTimeOffset At)
{
    /// <summary>Сам за двох — у ключі один нік.</summary>
    public bool Solo => !PairKey.Contains('+');
}

/// <summary>
/// «Вогник і Крапля»: прогрес рівнів на нік і найкращі часи пар (specs/vohnyk.md §7.4). Усе, що читає гра, —
/// з пам'яті (під замком кімнати SQLite не чіпаємо): таблиці піднімаються раз у конструкторі, а нові рядки
/// лягають у канал, який фоновий цикл зливає в базу. Без бази (тести) — лише пам'ять, гра від цього не страждає.
/// </summary>
public sealed class VohnykStore : BackgroundService
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS vohnyk_done(nick_key TEXT NOT NULL, level INTEGER NOT NULL, stars INTEGER NOT NULL,
          at TEXT NOT NULL, PRIMARY KEY(nick_key, level));
        CREATE TABLE IF NOT EXISTS vohnyk_best(pair_key TEXT NOT NULL, level INTEGER NOT NULL, ms INTEGER NOT NULL,
          deaths INTEGER NOT NULL, stars INTEGER NOT NULL, nicks TEXT NOT NULL, at TEXT NOT NULL, PRIMARY KEY(pair_key, level));
        """;

    readonly Db? _db;
    readonly ILogger<VohnykStore>? _log;
    readonly ConcurrentDictionary<(string Key, int Level), int> _done = new();
    readonly ConcurrentDictionary<(string Key, int Level), VohnykBest> _best = new();
    readonly Channel<Row> _rows = Channel.CreateUnbounded<Row>();
    readonly object _gate = new();

    /// <summary>Рядок для бази: або «пройшов» (done), або рекорд пари (best).</summary>
    sealed record Row(string Key, int Level, int Stars, VohnykBest? Best, DateTimeOffset At);

    public VohnykStore(Db? db, ILogger<VohnykStore>? log = null)
    {
        _db = db;
        _log = log;
        if (_db is null) return;
        try
        {
            _db.With(c =>
            {
                Exec(c, Schema);
                using (var cmd = Cmd(c, "SELECT nick_key, level, stars FROM vohnyk_done"))
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) _done[(r.GetString(0), r.GetInt32(1))] = r.GetInt32(2);
                using (var cmd = Cmd(c, "SELECT pair_key, level, ms, deaths, stars, nicks, at FROM vohnyk_best"))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        var b = new VohnykBest(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetString(5), Ts(r.GetString(6)));
                        _best[(b.PairKey, b.Level)] = b;
                    }
            });
        }
        catch (Exception ex)
        {
            // База впала — гра грається далі з пам'яті, просто прогрес не переживе рестарту.
            _log?.LogWarning(ex, "vohnyk: не прочитав прогрес із бази");
        }
    }

    /// <summary>Ключ ніка — як у гаманців: «Оля» і «оля» — одна людина.</summary>
    public static string Key(string nick) => Rooms.NickKey(nick);

    /// <summary>Ключ пари: ключі ніків за абеткою через «+» (сам — один ключ).</summary>
    public static string PairKey(IEnumerable<string> nicks) =>
        string.Join('+', nicks.Select(Key).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    /// <summary>«Оля» / «Оля і Петро» — для Журналу й таблиці.</summary>
    public static string Names(IReadOnlyList<string> nicks) => nicks.Count switch
    {
        0 => "",
        1 => nicks[0],
        _ => string.Join(", ", nicks.Take(nicks.Count - 1)) + " і " + nicks[^1],
    };

    /// <summary>Зірки ніка на рівні (0 — ще не пройшов).</summary>
    public int Stars(string nickKey, int level) => _done.TryGetValue((nickKey, level), out var s) ? s : 0;

    /// <summary>
    /// Рівень відчинений, якщо це перший або хоч хтось із цих ніків пройшов попередній — чи вже сам цей рівень
    /// (гостем у ветерана пройшов 3-й, не пройшовши 2-го: свій пройдений рівень не буває зачиненим).
    /// </summary>
    public bool Unlocked(IEnumerable<string> nickKeys, int level)
    {
        if (level <= 1) return true;
        foreach (var k in nickKeys)
            if (Stars(k, level - 1) > 0 || Stars(k, level) > 0) return true;
        return false;
    }

    public VohnykBest? Best(string pairKey, int level) => _best.TryGetValue((pairKey, level), out var b) ? b : null;

    /// <summary>Найкращі пари рівня, швидші першими.</summary>
    public List<VohnykBest> Top(int level, int n)
    {
        var list = new List<VohnykBest>();
        foreach (var (key, b) in _best)
            if (key.Level == level) list.Add(b);
        list.Sort((a, b) => a.Ms != b.Ms ? a.Ms.CompareTo(b.Ms) : a.At.CompareTo(b.At));
        return list.Count > n ? list.GetRange(0, n) : list;
    }

    /// <summary>Усі п'ятнадцять рівнів на три зірки — умова «Кришталевої печери».</summary>
    public bool AllThreeStars(string nickKey)
    {
        for (var n = 1; n <= VohnykLevels.Count; n++)
            if (Stars(nickKey, n) < 3) return false;
        return true;
    }

    /// <summary>
    /// Пройшли рівень: кожному ніку — максимум зірок, парі — рекорд, якщо швидше (<paramref name="best"/> = false —
    /// лише зірки: склад мінявся посеред рівня, і час не належить жодній парі). Пам'ять одразу, база — потім,
    /// через канал (кличеться під замком кімнати, тож жодного SQLite тут).
    /// </summary>
    public void Record(IReadOnlyList<string> nicks, int level, int ms, int deaths, int stars, DateTimeOffset at, bool best = true)
    {
        if (nicks.Count == 0) return;
        lock (_gate)
        {
            foreach (var nick in nicks)
            {
                var key = Key(nick);
                var was = Stars(key, level);
                if (stars <= was) continue;
                _done[(key, level)] = stars;
                if (_db is not null) _rows.Writer.TryWrite(new Row(key, level, stars, null, at));
            }
            if (!best) return;
            var pair = PairKey(nicks);
            var prev = Best(pair, level);
            if (prev is null || ms < prev.Ms)
            {
                var row = new VohnykBest(pair, level, ms, deaths, stars, Names(nicks), at);
                _best[(pair, level)] = row;
                if (_db is not null) _rows.Writer.TryWrite(new Row(pair, level, stars, row, at));
            }
        }
    }

    /// <summary>Скільки рядків ще не дійшло до бази (для тестів).</summary>
    public int Pending => _rows.Reader.CanCount ? _rows.Reader.Count : 0;

    /// <summary>Злити все, що накопичилось, у базу — синхронно (фоновий цикл і тести).</summary>
    public void Flush()
    {
        if (_db is null) return;
        while (_rows.Reader.TryRead(out var row))
        {
            try { Write(row); }
            catch (Exception ex) { _log?.LogWarning(ex, "vohnyk: рядок прогресу не записався"); }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_db is null) return;
        try
        {
            while (await _rows.Reader.WaitToReadAsync(ct)) Flush();
        }
        catch (OperationCanceledException) { }
        Flush();
    }

    void Write(Row row) => _db!.With(c =>
    {
        if (row.Best is { } b)
            Exec(c, """
                INSERT INTO vohnyk_best(pair_key, level, ms, deaths, stars, nicks, at) VALUES($k, $l, $ms, $d, $s, $n, $at)
                ON CONFLICT(pair_key, level) DO UPDATE SET ms = excluded.ms, deaths = excluded.deaths, stars = excluded.stars,
                  nicks = excluded.nicks, at = excluded.at WHERE excluded.ms < vohnyk_best.ms
                """, ("$k", b.PairKey), ("$l", b.Level), ("$ms", b.Ms), ("$d", b.Deaths), ("$s", b.Stars), ("$n", b.Nicks), ("$at", Iso(b.At)));
        else
            Exec(c, """
                INSERT INTO vohnyk_done(nick_key, level, stars, at) VALUES($k, $l, $s, $at)
                ON CONFLICT(nick_key, level) DO UPDATE SET stars = excluded.stars, at = excluded.at WHERE excluded.stars > vohnyk_done.stars
                """, ("$k", row.Key), ("$l", row.Level), ("$s", row.Stars), ("$at", Iso(row.At)));
    });

    static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
    static DateTimeOffset Ts(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    static SqliteCommand Cmd(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    static void Exec(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = Cmd(c, sql, ps);
        cmd.ExecuteNonQuery();
    }
}
