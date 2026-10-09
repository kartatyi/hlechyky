using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Bets;

/// <summary>
/// Ставка. <see cref="Status"/>: <c>new</c> — рядок записано, гроші ще не списано (видно лише сховищу: пережила падіння
/// посередині — при старті або стає <c>open</c>, якщо списання таки є, або зникає); <c>open</c> — чекає розрахунку;
/// <c>won</c> — зіграла, <see cref="Payout"/> = ставка × кеф; <c>lost</c> — згоріла; <c>back</c> — повернуто
/// (<see cref="Payout"/> = ставка). <see cref="Ref"/> — id події або <c>roomId:round</c> столу.
/// </summary>
public sealed record Bet(long Id, string Source, string Ref, string Market, string Option, string Label, string Nick, int Stake,
    double Odds, string Status, int Payout, DateTimeOffset At, DateTimeOffset? SettledAt, string Note)
{
    /// <summary>Скільки Глек виграв на цій ставці (мінус — програв). Відкрита й повернута — нуль.</summary>
    public int Glek => Status switch { "lost" => Stake, "won" => Stake - Payout, _ => 0 };
    public bool IsOpen => Status == "open";
}

/// <summary>Подія. Статуси: draft → open ⇄ paused → closed → settled; cancelled — з будь-якого до розрахунку.</summary>
public sealed record BetEvent(long Id, string Title, string Description, string Source, string? PmSlug, string? PmUrl,
    DateTimeOffset? ClosesAt, string Status, string? Winner, string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset? SettledAt,
    string Note, IReadOnlyList<BetOption> Options)
{
    public string Ref => Id.ToString(CultureInfo.InvariantCulture);
    public bool Final => Status is "settled" or "cancelled";
    public BetOption? Option(string? key) => Options.FirstOrDefault(o => o.Key == key);
}

/// <summary>Варіант події: свій кеф і «світ думає» з Polymarket (null — подія своя або ціни не брали).</summary>
public sealed record BetOption(string Key, string Title, double Odds, double? WorldP);

/// <summary>Пропозиція гравця: своє питання текстом і/або slug події Polymarket. Статуси: new, added, rejected.</summary>
public sealed record BetSuggestion(long Id, string Nick, string Text, string? PmSlug, string Status, string Reason, long? EventId,
    DateTimeOffset At, DateTimeOffset? DoneAt);

/// <summary>Сховище ставок, подій і пропозицій. DDL і SQL — тут; міграції ідемпотентні.</summary>
public sealed class BetsStore
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS bets(
            id INTEGER PRIMARY KEY AUTOINCREMENT, source TEXT NOT NULL, ref TEXT NOT NULL, market TEXT NOT NULL,
            option TEXT NOT NULL, label TEXT NOT NULL DEFAULT '', nick_key TEXT NOT NULL, nick TEXT NOT NULL,
            stake INTEGER NOT NULL, odds REAL NOT NULL, status TEXT NOT NULL, payout INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL, settled_at TEXT, note TEXT NOT NULL DEFAULT '', idem TEXT);
        CREATE INDEX IF NOT EXISTS bets_ref ON bets(source, ref, status);
        CREATE INDEX IF NOT EXISTS bets_nick ON bets(nick_key, id);
        CREATE INDEX IF NOT EXISTS bets_settled ON bets(status, settled_at);
        CREATE UNIQUE INDEX IF NOT EXISTS bets_idem ON bets(nick_key, idem) WHERE idem IS NOT NULL;
        CREATE TABLE IF NOT EXISTS bet_events(
            id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL, description TEXT NOT NULL DEFAULT '',
            source TEXT NOT NULL, pm_slug TEXT, pm_url TEXT, closes_at TEXT, status TEXT NOT NULL, winner_option TEXT,
            created_by TEXT NOT NULL, created_at TEXT NOT NULL, settled_at TEXT, note TEXT NOT NULL DEFAULT '');
        CREATE INDEX IF NOT EXISTS bet_events_status ON bet_events(status, id);
        CREATE TABLE IF NOT EXISTS bet_options(
            event_id INTEGER NOT NULL, key TEXT NOT NULL, title TEXT NOT NULL, odds REAL NOT NULL, world_p REAL,
            ord INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(event_id, key));
        CREATE TABLE IF NOT EXISTS bet_suggestions(
            id INTEGER PRIMARY KEY AUTOINCREMENT, nick_key TEXT NOT NULL, nick TEXT NOT NULL, text TEXT NOT NULL DEFAULT '',
            pm_slug TEXT, status TEXT NOT NULL, reason TEXT NOT NULL DEFAULT '', event_id INTEGER, created_at TEXT NOT NULL,
            done_at TEXT);
        CREATE INDEX IF NOT EXISTS bet_suggestions_status ON bet_suggestions(status, id);
        CREATE INDEX IF NOT EXISTS bet_suggestions_nick ON bet_suggestions(nick_key, id);
        """;
    const string BetCols = "id, source, ref, market, option, label, nick, stake, odds, status, payout, created_at, settled_at, note";
    const string EventCols = "id, title, description, source, pm_slug, pm_url, closes_at, status, winner_option, created_by, created_at, settled_at, note";
    const string SuggestCols = "id, nick, text, pm_slug, status, reason, event_id, created_at, done_at";

    readonly Db _db;

    public BetsStore(Db db)
    {
        _db = db;
        _db.With(c =>
        {
            Exec(c, Schema);
            // Ставка, що пережила падіння між записом і списанням: списання є — вона справжня, нема — її й не було
            Exec(c, "UPDATE bets SET status='open' WHERE status='new' AND EXISTS(SELECT 1 FROM ledger WHERE ref = 'bet:' || bets.id)");
            Exec(c, "DELETE FROM bets WHERE status='new'");
        });
    }

    static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    static DateTimeOffset Ts(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    static DateTimeOffset? TsOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Ts(r.GetString(i));
    static string? StrOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    static SqliteCommand Cmd(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    static int Exec(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = Cmd(c, sql, ps);
        return cmd.ExecuteNonQuery();
    }

    long Scalar(string sql, params (string Name, object? Value)[] ps) => _db.With(c =>
    {
        using var cmd = Cmd(c, sql, ps);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    });

    // ---------------------------------------------------------------- ставки

    static Bet ReadBet(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.GetString(5), r.GetString(6), r.GetInt32(7), r.GetDouble(8), r.GetString(9), r.GetInt32(10), Ts(r.GetString(11)),
        TsOrNull(r, 12), r.GetString(13));

    List<Bet> Bets(string where, params (string Name, object? Value)[] ps) => _db.With(c =>
    {
        using var cmd = Cmd(c, $"SELECT {BetCols} FROM bets {where}", ps);
        using var r = cmd.ExecuteReader();
        var list = new List<Bet>();
        while (r.Read()) list.Add(ReadBet(r));
        return list;
    });

    /// <summary>Записати ставку в стані <c>new</c> (гроші ще не списано). null — ставка з цим ключем уже є.</summary>
    public long? AddNew(Bet b, string? idem) => _db.With(c =>
    {
        using var cmd = Cmd(c, """
            INSERT OR IGNORE INTO bets(source, ref, market, option, label, nick_key, nick, stake, odds, status, payout, created_at, note, idem)
            VALUES($s, $r, $m, $o, $l, $k, $n, $st, $od, 'new', 0, $at, $note, $i)
            """, ("$s", b.Source), ("$r", b.Ref), ("$m", b.Market), ("$o", b.Option), ("$l", b.Label), ("$k", Auth.NickKey(b.Nick)),
            ("$n", b.Nick), ("$st", b.Stake), ("$od", b.Odds), ("$at", Iso(b.At)), ("$note", b.Note), ("$i", idem));
        if (cmd.ExecuteNonQuery() == 0) return (long?)null;
        using var id = Cmd(c, "SELECT last_insert_rowid()");
        return (long)id.ExecuteScalar()!;
    });

    public void Opened(long id) => _db.With(c => Exec(c, "UPDATE bets SET status='open' WHERE id=$id AND status='new'", ("$id", id)));

    public void DropNew(long id) => _db.With(c => Exec(c, "DELETE FROM bets WHERE id=$id AND status='new'", ("$id", id)));

    public Bet? Bet(long id) => Bets("WHERE id=$id", ("$id", id)).FirstOrDefault();

    /// <summary>Ставка людини з тим самим ключем — повтор подвійного кліку.</summary>
    public Bet? ByIdem(string nick, string idem) =>
        Bets("WHERE nick_key=$k AND idem=$i AND status<>'new'", ("$k", Auth.NickKey(nick)), ("$i", idem)).FirstOrDefault();

    /// <summary>Усі ставки на ref (крім недописаних), старші згори.</summary>
    public List<Bet> ByRef(string source, string refKey) =>
        Bets("WHERE source=$s AND ref=$r AND status<>'new' ORDER BY id", ("$s", source), ("$r", refKey));

    public List<Bet> OpenByRef(string source, string refKey) =>
        Bets("WHERE source=$s AND ref=$r AND status='open' ORDER BY id", ("$s", source), ("$r", refKey));

    /// <summary>Усі відкриті ставки джерела — для звірки після перезапуску (стіл не відновився — повернути).</summary>
    public List<Bet> OpenOf(string source) => Bets("WHERE source=$s AND status='open' ORDER BY id", ("$s", source));

    /// <summary>Скільки людина вже поставила на ref (відкриті) — для стелі на людину.</summary>
    public int OpenStakeOf(string nick, string source, string refKey) => (int)Scalar(
        "SELECT COALESCE(SUM(stake), 0) FROM bets WHERE source=$s AND ref=$r AND nick_key=$k AND status IN ('new','open')",
        ("$s", source), ("$r", refKey), ("$k", Auth.NickKey(nick)));

    /// <summary>Мої ставки обох джерел, свіжі згори.</summary>
    public List<Bet> Of(string nick, int n) =>
        Bets("WHERE nick_key=$k AND status<>'new' ORDER BY id DESC LIMIT $n", ("$k", Auth.NickKey(nick)), ("$n", n));

    /// <summary>Закрити відкриту ставку; false — її вже розрахував хтось інший.</summary>
    public bool Close(long id, string status, int payout, DateTimeOffset at, string note) => _db.With(c =>
        Exec(c, "UPDATE bets SET status=$st, payout=$p, settled_at=$at, note=$n WHERE id=$id AND status='open'",
            ("$st", status), ("$p", payout), ("$at", Iso(at)), ("$n", note), ("$id", id)) > 0);

    /// <summary>Розраховані ставки з <paramref name="since"/> (за часом розрахунку) — сирий матеріал статистики Глека.</summary>
    public List<Bet> Settled(DateTimeOffset since) =>
        Bets("WHERE status IN ('won','lost') AND settled_at >= $s ORDER BY id", ("$s", Iso(since)));

    /// <summary>
    /// Сальдо Глека за період одним запитом по обох джерелах: програні ставки − (виплати − ставки виграних).
    /// </summary>
    public (int Events, int Tables) Saldo(DateTimeOffset since) => _db.With(c =>
    {
        using var cmd = Cmd(c, """
            SELECT source, COALESCE(SUM(CASE status WHEN 'lost' THEN stake WHEN 'won' THEN stake - payout ELSE 0 END), 0)
            FROM bets WHERE status IN ('won','lost') AND settled_at >= $s GROUP BY source
            """, ("$s", Iso(since)));
        using var r = cmd.ExecuteReader();
        int ev = 0, tb = 0;
        while (r.Read())
        {
            var v = (int)r.GetInt64(1);
            if (r.GetString(0) == BetSources.Table) tb += v; else ev += v;
        }
        return (ev, tb);
    });

    /// <summary>Люди за чистим результатом проти Глека (плюс — обіграли його): нік, чисте, скільки ставок.</summary>
    public List<(string Nick, int Net, int Count)> Net(DateTimeOffset since) => _db.With(c =>
    {
        using var cmd = Cmd(c, """
            SELECT MAX(nick), SUM(CASE status WHEN 'won' THEN payout - stake ELSE -stake END), COUNT(*)
            FROM bets WHERE status IN ('won','lost') AND settled_at >= $s GROUP BY nick_key
            """, ("$s", Iso(since)));
        using var r = cmd.ExecuteReader();
        var list = new List<(string, int, int)>();
        while (r.Read()) list.Add((r.GetString(0), (int)r.GetInt64(1), r.GetInt32(2)));
        return list;
    });

    /// <summary>Найбільші виграші (за чистим: виплата − ставка).</summary>
    public List<Bet> BiggestWins(int n) => Bets("WHERE status='won' ORDER BY payout - stake DESC, id DESC LIMIT $n", ("$n", n));

    /// <summary>Підсумок ставок кожної події: id → (ставок, поставлено, сальдо Глека).</summary>
    public Dictionary<string, (int Count, int Staked, int Glek)> PerEvent() => _db.With(c =>
    {
        using var cmd = Cmd(c, """
            SELECT ref, COUNT(*), COALESCE(SUM(stake), 0),
                   COALESCE(SUM(CASE status WHEN 'lost' THEN stake WHEN 'won' THEN stake - payout ELSE 0 END), 0)
            FROM bets WHERE source='event' AND status<>'new' GROUP BY ref
            """);
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, (int, int, int)>(StringComparer.Ordinal);
        while (r.Read()) map[r.GetString(0)] = (r.GetInt32(1), (int)r.GetInt64(2), (int)r.GetInt64(3));
        return map;
    });

    // ---------------------------------------------------------------- події

    List<BetEvent> Events(string where, params (string Name, object? Value)[] ps) => _db.With(c =>
    {
        var list = new List<(long Id, Func<IReadOnlyList<BetOption>, BetEvent> Make)>();
        using (var cmd = Cmd(c, $"SELECT {EventCols} FROM bet_events {where}", ps))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var e = new BetEvent(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), StrOrNull(r, 4), StrOrNull(r, 5),
                    TsOrNull(r, 6), r.GetString(7), StrOrNull(r, 8), r.GetString(9), Ts(r.GetString(10)), TsOrNull(r, 11), r.GetString(12), []);
                list.Add((e.Id, opts => e with { Options = opts }));
            }
        if (list.Count == 0) return [];
        var options = new Dictionary<long, List<BetOption>>();
        using (var cmd = Cmd(c, $"SELECT event_id, key, title, odds, world_p FROM bet_options WHERE event_id IN ({string.Join(",", list.Select(x => x.Id))}) ORDER BY event_id, ord"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var id = r.GetInt64(0);
                if (!options.TryGetValue(id, out var l)) options[id] = l = [];
                l.Add(new BetOption(r.GetString(1), r.GetString(2), r.GetDouble(3), r.IsDBNull(4) ? null : r.GetDouble(4)));
            }
        return list.Select(x => x.Make(options.TryGetValue(x.Id, out var o) ? o : [])).ToList();
    });

    public BetEvent? Event(long id) => Events("WHERE id=$id", ("$id", id)).FirstOrDefault();

    /// <summary>Усі події (адміну) — свіжі згори.</summary>
    public List<BetEvent> AllEvents(int n) => Events("ORDER BY id DESC LIMIT $n", ("$n", n));

    /// <summary>Видимі всім: відкриті, призупинені й закриті (чекають результату).</summary>
    public List<BetEvent> LiveEvents() => Events("WHERE status IN ('open','paused','closed') ORDER BY COALESCE(closes_at, '9999'), id");

    /// <summary>Нещодавно розраховані чи скасовані — свіжі згори.</summary>
    public List<BetEvent> DoneEvents(int n) =>
        Events("WHERE status IN ('settled','cancelled') ORDER BY COALESCE(settled_at, created_at) DESC LIMIT $n", ("$n", n));

    public long AddEvent(BetEvent e) => _db.With(c =>
    {
        using var tx = c.BeginTransaction();
        using var cmd = Cmd(c, """
            INSERT INTO bet_events(title, description, source, pm_slug, pm_url, closes_at, status, created_by, created_at, note)
            VALUES($t, $d, $s, $ps, $pu, $ca, $st, $by, $at, '') ; SELECT last_insert_rowid();
            """, ("$t", e.Title), ("$d", e.Description), ("$s", e.Source), ("$ps", e.PmSlug), ("$pu", e.PmUrl),
            ("$ca", e.ClosesAt is { } ca ? Iso(ca) : null), ("$st", e.Status), ("$by", e.CreatedBy), ("$at", Iso(e.CreatedAt)));
        cmd.Transaction = tx;
        var id = (long)cmd.ExecuteScalar()!;
        WriteOptions(c, tx, id, e.Options);
        tx.Commit();
        return id;
    });

    static void WriteOptions(SqliteConnection c, SqliteTransaction tx, long id, IReadOnlyList<BetOption> options)
    {
        using (var del = Cmd(c, "DELETE FROM bet_options WHERE event_id=$id", ("$id", id))) { del.Transaction = tx; del.ExecuteNonQuery(); }
        var ord = 0;
        foreach (var o in options)
        {
            using var ins = Cmd(c, "INSERT INTO bet_options(event_id, key, title, odds, world_p, ord) VALUES($e, $k, $t, $o, $p, $n)",
                ("$e", id), ("$k", o.Key), ("$t", o.Title), ("$o", o.Odds), ("$p", o.WorldP), ("$n", ord++));
            ins.Transaction = tx;
            ins.ExecuteNonQuery();
        }
    }

    /// <summary>Назва, опис, до коли й варіанти — разом. Не чіпає статус.</summary>
    public void UpdateEvent(long id, string title, string description, DateTimeOffset? closesAt, IReadOnlyList<BetOption> options) => _db.With(c =>
    {
        using var tx = c.BeginTransaction();
        using (var cmd = Cmd(c, "UPDATE bet_events SET title=$t, description=$d, closes_at=$ca WHERE id=$id",
                   ("$t", title), ("$d", description), ("$ca", closesAt is { } ca ? Iso(ca) : null), ("$id", id)))
        { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
        WriteOptions(c, tx, id, options);
        tx.Commit();
    });

    /// <summary>Перевести подію з одного зі станів <paramref name="from"/>; false — вона вже в іншому.</summary>
    public bool MoveEvent(long id, string[] from, string to, DateTimeOffset at, string? winner = null, string? note = null) => _db.With(c =>
    {
        var final = to is "settled" or "cancelled";
        var sql = $"UPDATE bet_events SET status=$to{(final ? ", settled_at=$at" : "")}{(winner is null ? "" : ", winner_option=$w")}{(note is null ? "" : ", note=$n")}"
            + $" WHERE id=$id AND status IN ({string.Join(",", from.Select((_, i) => "$f" + i))})";
        var ps = new List<(string, object?)> { ("$to", to), ("$at", Iso(at)), ("$w", winner), ("$n", note), ("$id", id) };
        ps.AddRange(from.Select((f, i) => ("$f" + i, (object?)f)));
        return Exec(c, sql, [.. ps]) > 0;
    });

    // ---------------------------------------------------------------- пропозиції

    List<BetSuggestion> Suggestions(string where, params (string Name, object? Value)[] ps) => _db.With(c =>
    {
        using var cmd = Cmd(c, $"SELECT {SuggestCols} FROM bet_suggestions {where}", ps);
        using var r = cmd.ExecuteReader();
        var list = new List<BetSuggestion>();
        while (r.Read())
            list.Add(new BetSuggestion(r.GetInt64(0), r.GetString(1), r.GetString(2), StrOrNull(r, 3), r.GetString(4), r.GetString(5),
                r.IsDBNull(6) ? null : r.GetInt64(6), Ts(r.GetString(7)), TsOrNull(r, 8)));
        return list;
    });

    public long AddSuggestion(string nick, string text, string? slug, DateTimeOffset at) => _db.With(c =>
    {
        using var cmd = Cmd(c, """
            INSERT INTO bet_suggestions(nick_key, nick, text, pm_slug, status, created_at) VALUES($k, $n, $t, $s, 'new', $at);
            SELECT last_insert_rowid();
            """, ("$k", Auth.NickKey(nick)), ("$n", nick), ("$t", text), ("$s", slug), ("$at", Iso(at)));
        return (long)cmd.ExecuteScalar()!;
    });

    public BetSuggestion? Suggestion(long id) => Suggestions("WHERE id=$id", ("$id", id)).FirstOrDefault();

    /// <summary>Нові — старші згори (першою розглянути ту, що чекає найдовше).</summary>
    public List<BetSuggestion> NewSuggestions() => Suggestions("WHERE status='new' ORDER BY id");

    public List<BetSuggestion> RecentSuggestions(int n) => Suggestions("WHERE status<>'new' ORDER BY id DESC LIMIT $n", ("$n", n));

    public List<BetSuggestion> SuggestionsOf(string nick, int n) =>
        Suggestions("WHERE nick_key=$k ORDER BY id DESC LIMIT $n", ("$k", Auth.NickKey(nick)), ("$n", n));

    public int PendingOf(string nick) =>
        (int)Scalar("SELECT COUNT(*) FROM bet_suggestions WHERE nick_key=$k AND status='new'", ("$k", Auth.NickKey(nick)));

    public int PendingAll() => (int)Scalar("SELECT COUNT(*) FROM bet_suggestions WHERE status='new'");

    /// <summary>Закрити нову пропозицію (додано чи відхилено); false — її вже закрили.</summary>
    public bool CloseSuggestion(long id, string status, string reason, long? eventId, DateTimeOffset at) => _db.With(c =>
        Exec(c, "UPDATE bet_suggestions SET status=$st, reason=$r, event_id=$e, done_at=$at WHERE id=$id AND status='new'",
            ("$st", status), ("$r", reason), ("$e", eventId), ("$at", Iso(at)), ("$id", id)) > 0);
}
