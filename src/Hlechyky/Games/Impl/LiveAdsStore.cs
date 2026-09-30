using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Рядок журналу живих роликів. Статуси: <c>queued</c> (замовлено, ще не спечено) → <c>ready</c> (файл готовий) →
/// <c>sent</c> (стоїть у черзі ефіру) → <c>aired</c> (заграв). Вбік: <c>failed</c> (не спеклось), <c>expired</c>
/// (протухло, поки чекало), <c>refunded</c> (замовлення не вийшло — черепки повернуто).
/// </summary>
public sealed record LiveAdRow(long Id, string Kind, string? Target, string? Buyer, bool Anon, int Price, string Facts, string Text,
    string? File, int Seconds, string Status, DateTimeOffset CreatedAt, DateTimeOffset? ReadyAt, DateTimeOffset? SentAt,
    DateTimeOffset? PlayedAt, string Day)
{
    public IEnumerable<string> FactKinds => Facts.Split(',', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// Таблиці живої реклами: журнал роликів <c>live_ads</c> (кулдауни, ліміти замовлень, вкладка адміна), дрібні
/// налаштування господаря <c>live_ads_state</c> і прапорець відмови <c>accounts.no_roast</c>. Міграції — лише
/// «якщо нема», тож стара база відкривається як є, а старий акаунт читається як «не відмовлявся».
/// </summary>
public sealed class LiveAdsStore
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS live_ads(
            id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, target TEXT, target_key TEXT, buyer TEXT, buyer_key TEXT,
            anon INTEGER NOT NULL DEFAULT 0, price INTEGER NOT NULL DEFAULT 0, facts TEXT NOT NULL DEFAULT '',
            text TEXT NOT NULL DEFAULT '', file TEXT, seconds INTEGER NOT NULL DEFAULT 0, status TEXT NOT NULL,
            created_at TEXT NOT NULL, ready_at TEXT, sent_at TEXT, played_at TEXT, day TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_live_ads_target ON live_ads(target_key, id);
        CREATE INDEX IF NOT EXISTS ix_live_ads_status ON live_ads(status, id);
        CREATE INDEX IF NOT EXISTS ix_live_ads_buyer ON live_ads(buyer_key, day);
        CREATE TABLE IF NOT EXISTS live_ads_state(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """;
    const string Cols = "id, kind, target, buyer, anon, price, facts, text, file, seconds, status, created_at, ready_at, sent_at, played_at, day";

    readonly Db _db;

    public LiveAdsStore(Db db)
    {
        _db = db;
        _db.With(c =>
        {
            Exec(c, Schema);
            // Відмова від прожарок — прапорець акаунта. ALTER лише коли стовпця ще нема: повторний старт нічого не чіпає.
            if (!Columns(c, "accounts").Contains("no_roast"))
                Exec(c, "ALTER TABLE accounts ADD COLUMN no_roast INTEGER NOT NULL DEFAULT 0");
        });
    }

    // ---------- відмова ----------

    public bool OptedOut(string nick) => _db.With(c =>
        Scalar(c, "SELECT COALESCE(MAX(no_roast), 0) FROM accounts WHERE nick_key = $k", ("$k", Auth.NickKey(nick))) == 1);

    public HashSet<string> OptedOutKeys() => _db.With(c =>
        Read(c, "SELECT nick_key FROM accounts WHERE no_roast = 1", r => r.GetString(0)).ToHashSet(StringComparer.Ordinal));

    public bool SetOptOut(string nick, bool off) => _db.With(c =>
        Exec(c, "UPDATE accounts SET no_roast = $v WHERE nick_key = $k", ("$v", off ? 1 : 0), ("$k", Auth.NickKey(nick))) > 0);

    /// <summary>Акаунти, яких можна прожарити на замовлення: заходили за <paramref name="since"/> і не відмовились.</summary>
    public List<string> Targets(DateTimeOffset since) => _db.With(c =>
        Read(c, "SELECT nick FROM accounts WHERE no_roast = 0 AND seen_at >= $s ORDER BY seen_at DESC", r => r.GetString(0), ("$s", Iso(since))));

    public (string Nick, DateTimeOffset Seen)? Account(string nick) => _db.With(c =>
        Read(c, "SELECT nick, seen_at FROM accounts WHERE nick_key = $k", r => (r.GetString(0), Ts(r.GetString(1))), ("$k", Auth.NickKey(nick)))
            .Select(x => ((string, DateTimeOffset)?)x).FirstOrDefault());

    // ---------- налаштування господаря ----------

    public string? State(string key) => _db.With(c =>
        Read(c, "SELECT value FROM live_ads_state WHERE key = $k", r => r.GetString(0), ("$k", key)).FirstOrDefault());

    public void SetState(string key, string value) => _db.With(c =>
        Exec(c, "INSERT INTO live_ads_state(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$k", key), ("$v", value)));

    // ---------- журнал роликів ----------

    public long Add(string kind, string? target, string? buyer, bool anon, int price, string status, DateTimeOffset now) => _db.With(c =>
    {
        using var cmd = Cmd(c, """
            INSERT INTO live_ads(kind, target, target_key, buyer, buyer_key, anon, price, status, created_at, day)
            VALUES($kind, $t, $tk, $b, $bk, $a, $p, $s, $now, $day);
            SELECT last_insert_rowid();
            """, ("$kind", kind), ("$t", target), ("$tk", target is null ? null : Auth.NickKey(target)), ("$b", buyer),
            ("$bk", buyer is null ? null : Auth.NickKey(buyer)), ("$a", anon ? 1 : 0), ("$p", price), ("$s", status),
            ("$now", Iso(now)), ("$day", Days.Of(now)));
        return (long)cmd.ExecuteScalar()!;
    });

    public LiveAdRow? Get(long id) => _db.With(c => Rows(c, $"SELECT {Cols} FROM live_ads WHERE id = $id", ("$id", id)).FirstOrDefault());

    public LiveAdRow? ByFile(string file) => _db.With(c => Rows(c, $"SELECT {Cols} FROM live_ads WHERE file = $f", ("$f", file)).FirstOrDefault());

    public List<LiveAdRow> Recent(int n) => _db.With(c => Rows(c, $"SELECT {Cols} FROM live_ads ORDER BY id DESC LIMIT $n", ("$n", n)));

    public List<LiveAdRow> WithStatus(string status) => _db.With(c => Rows(c, $"SELECT {Cols} FROM live_ads WHERE status = $s ORDER BY id", ("$s", status)));

    public List<LiveAdRow> OrdersOf(string buyer, int n) => _db.With(c =>
        Rows(c, $"SELECT {Cols} FROM live_ads WHERE kind = 'order' AND buyer_key = $k ORDER BY id DESC LIMIT $n", ("$k", Auth.NickKey(buyer)), ("$n", n)));

    public List<LiveAdRow> AboutSince(string target, DateTimeOffset since) => _db.With(c =>
        Rows(c, $"SELECT {Cols} FROM live_ads WHERE target_key = $k AND created_at >= $s ORDER BY id", ("$k", Auth.NickKey(target)), ("$s", Iso(since))));

    public List<LiveAdRow> Since(DateTimeOffset since) => _db.With(c =>
        Rows(c, $"SELECT {Cols} FROM live_ads WHERE created_at >= $s ORDER BY id", ("$s", Iso(since))));

    /// <summary>Замовлення за день (Київ), що не впали: для лімітів «на замовника» і «на ціль».</summary>
    public (int ByBuyer, int OnTarget) OrdersToday(string buyer, string target, DateTimeOffset now) => _db.With(c => (
        (int)Scalar(c, "SELECT COUNT(*) FROM live_ads WHERE kind = 'order' AND buyer_key = $k AND day = $d AND status NOT IN ('failed', 'refunded')",
            ("$k", Auth.NickKey(buyer)), ("$d", Days.Of(now))),
        (int)Scalar(c, "SELECT COUNT(*) FROM live_ads WHERE kind = 'order' AND target_key = $k AND day = $d AND status NOT IN ('failed', 'refunded')",
            ("$k", Auth.NickKey(target)), ("$d", Days.Of(now)))));

    /// <summary>Коли кого востаннє крутили (для черги «хто давно не смажився»).</summary>
    public Dictionary<string, DateTimeOffset> LastSent() => _db.With(c =>
        Read(c, "SELECT target_key, MAX(sent_at) FROM live_ads WHERE target_key IS NOT NULL AND sent_at IS NOT NULL GROUP BY target_key",
            r => (r.GetString(0), Ts(r.GetString(1)))).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal));

    public void Ready(long id, string text, string facts, string file, int seconds, DateTimeOffset now) => _db.With(c =>
        Exec(c, "UPDATE live_ads SET status = 'ready', text = $t, facts = $f, file = $file, seconds = $s, ready_at = $now WHERE id = $id",
            ("$t", text), ("$f", facts), ("$file", file), ("$s", seconds), ("$now", Iso(now)), ("$id", id)));

    public void SetText(long id, string text, string facts) => _db.With(c =>
        Exec(c, "UPDATE live_ads SET text = $t, facts = $f WHERE id = $id", ("$t", text), ("$f", facts), ("$id", id)));

    /// <summary>Перевести зі стану в стан; false — рядок уже не в <paramref name="from"/> (хтось встиг першим).</summary>
    public bool Move(long id, string from, string to, DateTimeOffset now)
    {
        var col = to switch { "sent" => "sent_at", "aired" => "played_at", _ => null };
        return _db.With(c => Exec(c, $"UPDATE live_ads SET status = $to{(col is null ? "" : $", {col} = $now")} WHERE id = $id AND status = $from",
            ("$to", to), ("$now", Iso(now)), ("$id", id), ("$from", from)) > 0);
    }

    /// <summary>Файли, які вже можна прибрати: не останні <paramref name="keep"/> і відіграні/мертві раніше за <paramref name="before"/>.</summary>
    public List<(long Id, string File)> Stale(int keep, DateTimeOffset before) => _db.With(c =>
        Read(c, """
            SELECT id, file FROM live_ads WHERE file IS NOT NULL AND id NOT IN (SELECT id FROM live_ads WHERE file IS NOT NULL ORDER BY id DESC LIMIT $keep)
            AND ((status = 'aired' AND played_at < $b) OR (status IN ('failed', 'expired', 'refunded') AND created_at < $b))
            """, r => (r.GetInt64(0), r.GetString(1)), ("$keep", keep), ("$b", Iso(before))));

    public void DropFile(long id) => _db.With(c => Exec(c, "UPDATE live_ads SET file = NULL WHERE id = $id", ("$id", id)));

    // ---------- довільне читання (водяні знаки подій) ----------

    public long Scalar(string sql, params (string Name, object? Value)[] ps) => _db.With(c => Scalar(c, sql, ps));

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] ps) => _db.With(c => Read(c, sql, map, ps));

    // ---------- SQL ----------

    static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    static DateTimeOffset Ts(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    static DateTimeOffset? TsN(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Ts(r.GetString(i));
    static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    static List<LiveAdRow> Rows(SqliteConnection c, string sql, params (string, object?)[] ps) =>
        Read(c, sql, r => new LiveAdRow(r.GetInt64(0), r.GetString(1), S(r, 2), S(r, 3), r.GetInt32(4) == 1, r.GetInt32(5),
            r.GetString(6), r.GetString(7), S(r, 8), r.GetInt32(9), r.GetString(10), Ts(r.GetString(11)), TsN(r, 12), TsN(r, 13),
            TsN(r, 14), r.GetString(15)), ps);

    static HashSet<string> Columns(SqliteConnection c, string table) =>
        Read(c, $"PRAGMA table_info({table})", r => r.GetString(1)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    static SqliteCommand Cmd(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    static int Exec(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = Cmd(c, sql, ps);
        return cmd.ExecuteNonQuery();
    }

    static long Scalar(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = Cmd(c, sql, ps);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    static List<T> Read<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] ps)
    {
        using var cmd = Cmd(c, sql, ps);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }
}
