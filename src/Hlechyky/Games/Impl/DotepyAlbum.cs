using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Games.Impl;

/// <summary>Дотеп в альбомі: завдання, відповідь, хто написав, хто закинув, скільки очок узяв за столом, ❤ читачів.</summary>
public sealed record DotepyAlbumItem(long Id, string Prompt, string Text, string Author, string By, int Points, DateTimeOffset At, int Likes);

/// <summary>
/// «📖 Альбом дотепів»: найкращі дотепи, які гравці закинули після партії (📌). Гортати може будь-хто, ❤ ставить той,
/// хто назвався, прибирає — адмін. Рядки короткі (завдання ≤ 120 + нік, дотеп ≤ 80), тож альбом компактний; понад
/// <see cref="MaxItems"/> — найстаріші випадають. Без бази (тести) живе в пам'яті. Кличеться лише з HTTP, поза замком кімнати.
/// </summary>
public sealed class DotepyAlbum
{
    public const int MaxItems = 2_000;
    public const int PageSize = 30;

    const string Schema = """
        CREATE TABLE IF NOT EXISTS dotepy_album(id INTEGER PRIMARY KEY AUTOINCREMENT, prompt TEXT NOT NULL, text TEXT NOT NULL,
          author TEXT NOT NULL, by_nick TEXT NOT NULL, points INTEGER NOT NULL, at TEXT NOT NULL, likes INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS dotepy_album_likes(item INTEGER NOT NULL, nick_key TEXT NOT NULL, PRIMARY KEY(item, nick_key));
        """;

    readonly Db? _db;
    readonly object _gate = new();
    readonly List<DotepyAlbumItem> _mem = [];
    readonly HashSet<(long, string)> _memLikes = [];
    long _memId;
    int _count;

    public DotepyAlbum(Db? db, ILogger<DotepyAlbum>? log = null)
    {
        _db = db;
        if (_db is null) return;
        try
        {
            _db.With(c =>
            {
                Exec(c, Schema);
                using var cmd = Cmd(c, "SELECT COUNT(*) FROM dotepy_album");
                _count = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            });
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "dotepy: не відкрив альбом дотепів у базі");
        }
    }

    public int Count { get { lock (_gate) return _db is null ? _mem.Count : _count; } }

    public long Add(string prompt, string text, string author, string by, int points, DateTimeOffset at)
    {
        lock (_gate)
        {
            long id;
            if (_db is null)
            {
                id = ++_memId;
                _mem.Add(new DotepyAlbumItem(id, prompt, text, author, by, points, at, 0));
                while (_mem.Count > MaxItems) _mem.RemoveAt(0);
                return id;
            }
            id = _db.With(c =>
            {
                Exec(c, "INSERT INTO dotepy_album(prompt, text, author, by_nick, points, at) VALUES($p, $t, $a, $b, $pts, $at)",
                    ("$p", prompt), ("$t", text), ("$a", author), ("$b", by), ("$pts", points), ("$at", Iso(at)));
                using var cmd = Cmd(c, "SELECT last_insert_rowid()");
                return (long)cmd.ExecuteScalar()!;
            });
            _count++;
            if (_count > MaxItems)
            {
                _db.With(c =>
                {
                    Exec(c, "DELETE FROM dotepy_album_likes WHERE item IN (SELECT id FROM dotepy_album ORDER BY id LIMIT $n)", ("$n", _count - MaxItems));
                    Exec(c, "DELETE FROM dotepy_album WHERE id IN (SELECT id FROM dotepy_album ORDER BY id LIMIT $n)", ("$n", _count - MaxItems));
                });
                _count = MaxItems;
            }
            return id;
        }
    }

    /// <summary>Сторінка: новіші першими, старші за <paramref name="before"/> (0 — з початку). <c>Liked</c> — ❤ цього читача.</summary>
    public (List<(DotepyAlbumItem Item, bool Liked)> Items, bool More) Page(long before, int n, string? nickKey)
    {
        n = Math.Clamp(n, 1, PageSize);
        lock (_gate)
        {
            List<(DotepyAlbumItem, bool)> items;
            if (_db is null)
            {
                items = [.. _mem.Where(a => before <= 0 || a.Id < before).OrderByDescending(a => a.Id).Take(n + 1)
                    .Select(a => (a, nickKey is not null && _memLikes.Contains((a.Id, nickKey))))];
            }
            else
            {
                items = _db.With(c =>
                {
                    var list = new List<(DotepyAlbumItem, bool)>();
                    using var cmd = Cmd(c, """
                        SELECT a.id, a.prompt, a.text, a.author, a.by_nick, a.points, a.at, a.likes,
                          EXISTS(SELECT 1 FROM dotepy_album_likes l WHERE l.item = a.id AND l.nick_key = $k)
                        FROM dotepy_album a WHERE $b <= 0 OR a.id < $b ORDER BY a.id DESC LIMIT $n
                        """, ("$b", before), ("$n", n + 1), ("$k", nickKey ?? ""));
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        list.Add((new DotepyAlbumItem(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                            r.GetInt32(5), Ts(r.GetString(6)), r.GetInt32(7)), r.GetInt64(8) != 0));
                    return list;
                });
            }
            var more = items.Count > n;
            if (more) items.RemoveAt(items.Count - 1);
            return (items, more);
        }
    }

    /// <summary>❤ читача: ставить або знімає. null — такого дотепу нема.</summary>
    public (int Likes, bool Liked)? Like(long id, string nickKey)
    {
        lock (_gate)
        {
            if (_db is null)
            {
                var i = _mem.FindIndex(a => a.Id == id);
                if (i < 0) return null;
                var liked = _memLikes.Add((id, nickKey));
                if (!liked) _memLikes.Remove((id, nickKey));
                _mem[i] = _mem[i] with { Likes = Math.Max(0, _mem[i].Likes + (liked ? 1 : -1)) };
                return (_mem[i].Likes, liked);
            }
            return _db.With<(int, bool)?>(c =>
            {
                using (var probe = Cmd(c, "SELECT 1 FROM dotepy_album WHERE id = $id", ("$id", id)))
                    if (probe.ExecuteScalar() is null) return null;
                using var tx = c.BeginTransaction();
                bool liked;
                using (var ins = Cmd(c, "INSERT OR IGNORE INTO dotepy_album_likes(item, nick_key) VALUES($id, $k)", ("$id", id), ("$k", nickKey)))
                {
                    ins.Transaction = tx;
                    liked = ins.ExecuteNonQuery() > 0;
                }
                if (!liked)
                {
                    using var del = Cmd(c, "DELETE FROM dotepy_album_likes WHERE item = $id AND nick_key = $k", ("$id", id), ("$k", nickKey));
                    del.Transaction = tx;
                    del.ExecuteNonQuery();
                }
                using (var upd = Cmd(c, "UPDATE dotepy_album SET likes = (SELECT COUNT(*) FROM dotepy_album_likes WHERE item = $id) WHERE id = $id", ("$id", id)))
                {
                    upd.Transaction = tx;
                    upd.ExecuteNonQuery();
                }
                int likes;
                using (var get = Cmd(c, "SELECT likes FROM dotepy_album WHERE id = $id", ("$id", id)))
                {
                    get.Transaction = tx;
                    likes = Convert.ToInt32(get.ExecuteScalar(), CultureInfo.InvariantCulture);
                }
                tx.Commit();
                return (likes, liked);
            });
        }
    }

    /// <summary>Адмін прибирає дотеп. false — такого нема.</summary>
    public bool Delete(long id)
    {
        lock (_gate)
        {
            if (_db is null)
            {
                _memLikes.RemoveWhere(l => l.Item1 == id);
                return _mem.RemoveAll(a => a.Id == id) > 0;
            }
            var gone = _db.With(c =>
            {
                Exec(c, "DELETE FROM dotepy_album_likes WHERE item = $id", ("$id", id));
                using var cmd = Cmd(c, "DELETE FROM dotepy_album WHERE id = $id", ("$id", id));
                return cmd.ExecuteNonQuery() > 0;
            });
            if (gone) _count--;
            return gone;
        }
    }

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
