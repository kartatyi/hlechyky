using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Games.Impl;

/// <summary>Конкурс: сценарій, коли відкрився, коли закривається і хто переміг.</summary>
public sealed record AdContestRow(
    long Id, string Script, DateTimeOffset CreatedAt, DateTimeOffset ClosesAt,
    bool Closed, string? WinnerNickKey, DateTimeOffset? ClosedAt);

/// <summary>Один запис у конкурсі разом із уже порахованими голосами.</summary>
public sealed record AdEntryRow(
    long Id, long ContestId, string NickKey, string Nick, string TrackId, int Seconds, DateTimeOffset CreatedAt, int Votes);

/// <summary>
/// Три таблиці конкурсу реклами живуть окремо від <see cref="Db"/>: DDL і весь SQL — тут, а від Db
/// береться лише з'єднання на час однієї короткої операції (<see cref="Db.With{T}"/>). Так спільний
/// тонкий файл не збирає на собі схему кожної нової витівки, а конкурс носить свою базу з собою.
/// </summary>
public sealed class AdContestStore
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS ad_contests(
            id INTEGER PRIMARY KEY AUTOINCREMENT, script TEXT NOT NULL, created_at TEXT NOT NULL,
            closes_at TEXT NOT NULL, closed INTEGER NOT NULL DEFAULT 0,
            winner_nick_key TEXT, closed_at TEXT);
        CREATE TABLE IF NOT EXISTS ad_entries(
            id INTEGER PRIMARY KEY AUTOINCREMENT, contest_id INTEGER NOT NULL, nick_key TEXT NOT NULL,
            nick TEXT NOT NULL, track_id TEXT NOT NULL, dur_sec INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL);
        -- один запис на ніка на конкурс: новий просто замінює старий (ON CONFLICT нижче)
        CREATE UNIQUE INDEX IF NOT EXISTS ux_ad_entries ON ad_entries(contest_id, nick_key);
        CREATE TABLE IF NOT EXISTS ad_votes(
            contest_id INTEGER NOT NULL, voter_key TEXT NOT NULL, voter_nick TEXT NOT NULL,
            entry_id INTEGER NOT NULL, created_at TEXT NOT NULL,
            PRIMARY KEY(contest_id, voter_key));
        """;

    readonly Db _db;

    public AdContestStore(Db db)
    {
        _db = db;
        _db.With(c => Exec(c, Schema));
    }

    // ---------- конкурси ----------

    /// <summary>Конкурс, який зараз триває (закритих не буває більше одного відкритого).</summary>
    public AdContestRow? Active() => _db.With(c => One(c, "closed = 0"));

    /// <summary>Найсвіжіший конкурс, хай навіть закритий — щоб не відкрити двох за один день.</summary>
    public AdContestRow? Latest() => _db.With(c => One(c, "1 = 1"));

    public AdContestRow? Get(long id) => _db.With(c =>
    {
        using var cmd = Cmd(c, Sql("id = $id"), ("$id", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadContest(r) : null;
    });

    public long Open(string script, DateTimeOffset now, DateTimeOffset closesAt) => _db.With(c =>
    {
        using var cmd = Cmd(c, """
            INSERT INTO ad_contests(script, created_at, closes_at) VALUES($s, $now, $till);
            SELECT last_insert_rowid();
            """, ("$s", script), ("$now", Iso(now)), ("$till", Iso(closesAt)));
        return (long)cmd.ExecuteScalar()!;
    });

    /// <summary>
    /// Закрити конкурс. Повертає false, якщо його вже закрили: саме на цьому тримається «двічі
    /// закрити — не подвоїти виплати», бо викликати закриття можуть і адмін, і тікер одночасно.
    /// </summary>
    public bool Close(long id, string? winnerKey, DateTimeOffset now) => _db.With(c =>
    {
        using var cmd = Cmd(c, """
            UPDATE ad_contests SET closed = 1, winner_nick_key = $w, closed_at = $now
            WHERE id = $id AND closed = 0
            """, ("$w", winnerKey), ("$now", Iso(now)), ("$id", id));
        return cmd.ExecuteNonQuery() > 0;
    });

    /// <summary>Останні закриті конкурси — для рядка «минулі переможці».</summary>
    public List<AdContestRow> Past(int n) => _db.With(c =>
    {
        using var cmd = Cmd(c, Sql("closed = 1", n), ("$n", n));
        using var r = cmd.ExecuteReader();
        var list = new List<AdContestRow>();
        while (r.Read()) list.Add(ReadContest(r));
        return list;
    });

    // ---------- записи ----------

    public List<AdEntryRow> Entries(long contestId) => _db.With(c => EntriesOn(c, contestId));

    public AdEntryRow? Entry(long contestId, string nickKey) =>
        Entries(contestId).FirstOrDefault(e => e.NickKey == nickKey);

    public AdEntryRow? EntryById(long contestId, long entryId) =>
        Entries(contestId).FirstOrDefault(e => e.Id == entryId);

    /// <summary>
    /// Покласти (або замінити) запис ніка. Повертає track_id старого запису — його файл кличучий
    /// має прибрати з кеша, інакше кожна перезапись лишала б по mp3.
    /// </summary>
    public string? PutEntry(long contestId, string nickKey, string nick, string trackId, int seconds, DateTimeOffset now) => _db.With(c =>
    {
        var old = OldTrack(c, contestId, nickKey);
        Exec(c, """
            INSERT INTO ad_entries(contest_id, nick_key, nick, track_id, dur_sec, created_at)
            VALUES($c, $k, $n, $t, $d, $now)
            ON CONFLICT(contest_id, nick_key) DO UPDATE SET
                nick = excluded.nick, track_id = excluded.track_id, dur_sec = excluded.dur_sec
            """, ("$c", contestId), ("$k", nickKey), ("$n", nick), ("$t", trackId), ("$d", seconds), ("$now", Iso(now)));
        return old == trackId ? null : old;
    });

    /// <summary>Забрати свій запис. Повертає track_id, щоб файл не лишався в кеші сиротою.</summary>
    public string? DropEntry(long contestId, string nickKey) => _db.With(c =>
    {
        var old = OldTrack(c, contestId, nickKey);
        if (old is null) return null;
        // голоси за викинутий запис теж ідуть: інакше в підрахунку висіли б голоси в нікуди
        Exec(c, "DELETE FROM ad_votes WHERE contest_id = $c AND entry_id IN (SELECT id FROM ad_entries WHERE contest_id = $c AND nick_key = $k)",
            ("$c", contestId), ("$k", nickKey));
        Exec(c, "DELETE FROM ad_entries WHERE contest_id = $c AND nick_key = $k", ("$c", contestId), ("$k", nickKey));
        return old;
    });

    // ---------- голоси ----------

    /// <summary>Голос ніка в цьому конкурсі: id запису або null.</summary>
    public long? VoteOf(long contestId, string voterKey) => _db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT entry_id FROM ad_votes WHERE contest_id = $c AND voter_key = $k",
            ("$c", contestId), ("$k", voterKey));
        return cmd.ExecuteScalar() is long id ? id : (long?)null;
    });

    /// <summary>Проголосувати (або перекласти свій голос на інший запис — PK не дає другого).</summary>
    public void Vote(long contestId, string voterKey, string voterNick, long entryId, DateTimeOffset now) => _db.With(c =>
        Exec(c, """
            INSERT INTO ad_votes(contest_id, voter_key, voter_nick, entry_id, created_at)
            VALUES($c, $k, $n, $e, $now)
            ON CONFLICT(contest_id, voter_key) DO UPDATE SET entry_id = excluded.entry_id, created_at = excluded.created_at
            """, ("$c", contestId), ("$k", voterKey), ("$n", voterNick), ("$e", entryId), ("$now", Iso(now))));

    /// <summary>Ніки тих, хто проголосував — їм платять по черепку.</summary>
    public List<string> Voters(long contestId) => _db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT voter_nick FROM ad_votes WHERE contest_id = $c ORDER BY created_at", ("$c", contestId));
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    });

    // ---------- дрібне ----------

    static string Sql(string where, int limit = 1) =>
        $"SELECT id, script, created_at, closes_at, closed, winner_nick_key, closed_at FROM ad_contests WHERE {where} ORDER BY id DESC LIMIT {(limit == 1 ? "1" : "$n")}";

    static AdContestRow? One(SqliteConnection c, string where)
    {
        using var cmd = Cmd(c, Sql(where));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadContest(r) : null;
    }

    static AdContestRow ReadContest(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), Ts(r.GetString(2)), Ts(r.GetString(3)),
        r.GetInt32(4) == 1, r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : Ts(r.GetString(6)));

    static List<AdEntryRow> EntriesOn(SqliteConnection c, long contestId)
    {
        using var cmd = Cmd(c, """
            SELECT e.id, e.contest_id, e.nick_key, e.nick, e.track_id, e.dur_sec, e.created_at,
                   (SELECT COUNT(*) FROM ad_votes v WHERE v.entry_id = e.id)
            FROM ad_entries e WHERE e.contest_id = $c ORDER BY e.id
            """, ("$c", contestId));
        using var r = cmd.ExecuteReader();
        var list = new List<AdEntryRow>();
        while (r.Read())
            list.Add(new AdEntryRow(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetInt32(5), Ts(r.GetString(6)), r.GetInt32(7)));
        return list;
    }

    static string? OldTrack(SqliteConnection c, long contestId, string nickKey)
    {
        using var cmd = Cmd(c, "SELECT track_id FROM ad_entries WHERE contest_id = $c AND nick_key = $k",
            ("$c", contestId), ("$k", nickKey));
        return cmd.ExecuteScalar() as string;
    }

    static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("o");
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
