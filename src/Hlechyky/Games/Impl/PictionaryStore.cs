using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Games.Impl;

/// <summary>Малюнок у публічному Альбомі Піктіонарі: слово, художник, хто закинув, ❤ партії, коли, штрихи (<see cref="SketchWire"/>).</summary>
public sealed record PictionaryArt(long Id, string Word, string Author, string By, int Hearts, bool Home, DateTimeOffset At, string Z);

/// <summary>Рекорд пари в «Скільки встигнемо»: скільки слів за три хвилини.</summary>
public sealed record PictionaryPair(string PairKey, string Nicks, int Best, DateTimeOffset At);

/// <summary>
/// Сховище Піктіонарі (прохід №3): публічний альбом (п. 26) і рекорди пар «Скільки встигнемо» (п. 29).
/// <para>
/// Альбом пишеться й читається лише з HTTP (закинути, погортати, видалити) — не з-під замка кімнати, тож SQLite тут
/// можна напряму. Малюнок зберігається штрихами (base64, зазвичай 3–15 КБ), не картинкою; загальний розмір обмежений
/// (<see cref="MaxTotalChars"/>, <see cref="MaxItems"/>) — найстаріші випадають, диск тісний.
/// </para>
/// <para>
/// Рекорд пари пишеться з кінця партії, тобто під замком кімнати: пам'ять одразу, база — фоном через канал
/// (як <see cref="VohnykStore"/>). Без бази (тести) — усе в пам'яті.
/// </para>
/// </summary>
public sealed class PictionaryStore : BackgroundService
{
    /// <summary>Найбільший малюнок, що лягає в альбом (символів base64, ≈ 45 КБ байтів).</summary>
    public const int MaxArtChars = 60_000;
    /// <summary>Усі малюнки альбому разом — не більше (≈ 24 МБ); далі найстаріші випадають.</summary>
    public const long MaxTotalChars = 24_000_000;
    public const int MaxItems = 3_000;
    public const int PageSize = 24;

    const string Schema = """
        CREATE TABLE IF NOT EXISTS pictionary_album(id INTEGER PRIMARY KEY AUTOINCREMENT, word TEXT NOT NULL, author TEXT NOT NULL,
          by_nick TEXT NOT NULL, hearts INTEGER NOT NULL, home INTEGER NOT NULL, at TEXT NOT NULL, z TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS pictionary_pairs(pair_key TEXT PRIMARY KEY, nicks TEXT NOT NULL, best INTEGER NOT NULL, at TEXT NOT NULL);
        """;

    readonly Db? _db;
    readonly ILogger<PictionaryStore>? _log;
    readonly object _gate = new();
    /// <summary>Без бази альбом живе тут (тести).</summary>
    readonly List<PictionaryArt> _mem = [];
    long _memId;
    long _total;
    int _count;
    readonly ConcurrentDictionary<string, PictionaryPair> _pairs = new(StringComparer.Ordinal);
    readonly Channel<PictionaryPair> _rows = Channel.CreateUnbounded<PictionaryPair>();

    public PictionaryStore(Db? db, ILogger<PictionaryStore>? log = null)
    {
        _db = db;
        _log = log;
        if (_db is null) return;
        try
        {
            _db.With(c =>
            {
                Exec(c, Schema);
                using (var cmd = Cmd(c, "SELECT COUNT(*), COALESCE(SUM(LENGTH(z)), 0) FROM pictionary_album"))
                using (var r = cmd.ExecuteReader())
                    if (r.Read()) { _count = r.GetInt32(0); _total = r.GetInt64(1); }
                using (var cmd = Cmd(c, "SELECT pair_key, nicks, best, at FROM pictionary_pairs"))
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) _pairs[r.GetString(0)] = new PictionaryPair(r.GetString(0), r.GetString(1), r.GetInt32(2), Ts(r.GetString(3)));
            });
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "pictionary: не прочитав альбом і рекорди пар із бази");
        }
    }

    // =========================================================================================
    // Пари «Скільки встигнемо»
    // =========================================================================================

    public static string PairKey(IEnumerable<string> nicks) =>
        string.Join('+', nicks.Select(Auth.NickKey).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    public PictionaryPair? Pair(string pairKey) => _pairs.TryGetValue(pairKey, out var p) ? p : null;

    public List<PictionaryPair> TopPairs(int n)
    {
        var list = _pairs.Values.ToList();
        list.Sort((a, b) => a.Best != b.Best ? b.Best.CompareTo(a.Best) : a.At.CompareTo(b.At));
        return list.Count > n ? list.GetRange(0, n) : list;
    }

    /// <summary>Пара встигла <paramref name="count"/> слів. true — це новий рекорд пари. Кличеться під замком кімнати: лише пам'ять і канал.</summary>
    public bool RecordPair(IReadOnlyList<string> nicks, int count, DateTimeOffset at)
    {
        if (nicks.Count == 0 || count <= 0) return false;
        var key = PairKey(nicks);
        lock (_gate)
        {
            if (Pair(key) is { } prev && prev.Best >= count) return false;
            var row = new PictionaryPair(key, string.Join(" і ", nicks), count, at);
            _pairs[key] = row;
            if (_db is not null) _rows.Writer.TryWrite(row);
            return true;
        }
    }

    public void Flush()
    {
        if (_db is null) return;
        while (_rows.Reader.TryRead(out var p))
        {
            try
            {
                _db.With(c => Exec(c, """
                    INSERT INTO pictionary_pairs(pair_key, nicks, best, at) VALUES($k, $n, $b, $at)
                    ON CONFLICT(pair_key) DO UPDATE SET nicks = excluded.nicks, best = excluded.best, at = excluded.at
                      WHERE excluded.best > pictionary_pairs.best
                    """, ("$k", p.PairKey), ("$n", p.Nicks), ("$b", p.Best), ("$at", Iso(p.At))));
            }
            catch (Exception ex) { _log?.LogWarning(ex, "pictionary: рекорд пари не записався"); }
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

    // =========================================================================================
    // Публічний альбом
    // =========================================================================================

    public int Count { get { lock (_gate) return _db is null ? _mem.Count : _count; } }
    public long TotalChars { get { lock (_gate) return _total; } }

    /// <summary>Закинути малюнок в альбом. Повертає його номер; найстаріші випадають, коли альбом переріс межі.</summary>
    public long Add(string word, string author, string by, int hearts, bool home, DateTimeOffset at, string z)
    {
        lock (_gate)
        {
            long id;
            if (_db is null)
            {
                id = ++_memId;
                _mem.Add(new PictionaryArt(id, word, author, by, hearts, home, at, z));
            }
            else
            {
                id = _db.With(c =>
                {
                    Exec(c, "INSERT INTO pictionary_album(word, author, by_nick, hearts, home, at, z) VALUES($w, $a, $b, $h, $home, $at, $z)",
                        ("$w", word), ("$a", author), ("$b", by), ("$h", hearts), ("$home", home ? 1 : 0), ("$at", Iso(at)), ("$z", z));
                    using var cmd = Cmd(c, "SELECT last_insert_rowid()");
                    return (long)cmd.ExecuteScalar()!;
                });
                _count++;
            }
            _total += z.Length;
            Trim();
            return id;
        }
    }

    /// <summary>Найстаріші — геть, поки альбом більший за межі (під _gate).</summary>
    void Trim()
    {
        while ((_total > MaxTotalChars || Count0() > MaxItems) && Count0() > 1)
        {
            if (_db is null)
            {
                _total -= _mem[0].Z.Length;
                _mem.RemoveAt(0);
                continue;
            }
            var gone = _db.With(c =>
            {
                using var cmd = Cmd(c, "SELECT id, LENGTH(z) FROM pictionary_album ORDER BY id LIMIT 1");
                using var r = cmd.ExecuteReader();
                return r.Read() ? (r.GetInt64(0), r.GetInt64(1)) : (0L, 0L);
            });
            if (gone.Item1 == 0) { _total = 0; _count = 0; return; }
            _db.Exec("DELETE FROM pictionary_album WHERE id = $id", ("$id", gone.Item1));
            _total -= gone.Item2;
            _count--;
        }
    }

    int Count0() => _db is null ? _mem.Count : _count;

    /// <summary>Сторінка альбому: новіші першими, старші за <paramref name="before"/> (0 — з початку).</summary>
    public (List<PictionaryArt> Items, bool More) Page(long before, int n)
    {
        n = Math.Clamp(n, 1, PageSize);
        lock (_gate)
        {
            List<PictionaryArt> items;
            if (_db is null)
            {
                items = [.. _mem.Where(a => before <= 0 || a.Id < before).OrderByDescending(a => a.Id).Take(n + 1)];
            }
            else
            {
                items = _db.With(c =>
                {
                    var list = new List<PictionaryArt>();
                    using var cmd = Cmd(c, """
                        SELECT id, word, author, by_nick, hearts, home, at, z FROM pictionary_album
                        WHERE $b <= 0 OR id < $b ORDER BY id DESC LIMIT $n
                        """, ("$b", before), ("$n", n + 1));
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        list.Add(new PictionaryArt(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4),
                            r.GetInt32(5) != 0, Ts(r.GetString(6)), r.GetString(7)));
                    return list;
                });
            }
            var more = items.Count > n;
            if (more) items.RemoveAt(items.Count - 1);
            return (items, more);
        }
    }

    /// <summary>Адмін прибирає малюнок. false — такого нема.</summary>
    public bool Delete(long id)
    {
        lock (_gate)
        {
            if (_db is null)
            {
                var i = _mem.FindIndex(a => a.Id == id);
                if (i < 0) return false;
                _total -= _mem[i].Z.Length;
                _mem.RemoveAt(i);
                return true;
            }
            var len = _db.With(c =>
            {
                using var cmd = Cmd(c, "SELECT LENGTH(z) FROM pictionary_album WHERE id = $id", ("$id", id));
                return cmd.ExecuteScalar() is long l ? l : -1;
            });
            if (len < 0) return false;
            _db.Exec("DELETE FROM pictionary_album WHERE id = $id", ("$id", id));
            _total -= len;
            _count--;
            return true;
        }
    }

    // =========================================================================================

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
