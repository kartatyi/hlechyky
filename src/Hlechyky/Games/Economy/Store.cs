using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Games.Economy;

/// <summary>
/// Чим скінчилось нарахування: пройшло, вже було з таким ref, вперлось у стелю дня, або нічого й не
/// збиралось рухати (нуль черепків, порожній нік) — <see cref="Skipped"/> саме для того, щоб той, хто
/// кликав, не порахував такий виклик за виплату.
/// </summary>
public enum GrantResult { Applied, Duplicate, Capped, Skipped }

/// <summary>Гаманець одного ніка.</summary>
public sealed record WalletRow(string NickKey, string Nick, int Balance, int Earned, int Spent);

/// <summary>Рядок історії партій (одна на кожного учасника). Для соло — один рядок на ключ кімнати, з лічильником спроб.</summary>
public sealed record ResultRow(string RoomId, string Game, int Round, string NickKey, string Nick,
    string Outcome, long? Score, string? Opponents, int Stake, DateTimeOffset At, int Tries = 1);

/// <summary>Рейтинг ніка в одній грі.</summary>
public sealed record RatingRow(string NickKey, string Nick, string Game, int Elo, int Games, int Wins, int Losses, int Draws);

/// <summary>Результат щоденної головоломки.</summary>
public sealed record DailyRow(string Day, string Game, string NickKey, string Nick, bool Solved, int Attempts, int Ms);

/// <summary>Рядок панелі «Щоденного глека» для однієї головоломки: мій результат, скільки розв'язало, топ дня.</summary>
public sealed record DailyPanelRow(string Game, DailyRow? Mine, int SolvedCount, List<DailyRow> Top);

/// <summary>Здобута ачівка.</summary>
public sealed record UnlockedRow(string Key, DateTimeOffset At);

/// <summary>Скільки заробив за період (для таблиці «черепки»).</summary>
public sealed record ShardRow(string Nick, int Balance, int Earned);

/// <summary>
/// Увесь SQL економіки в одному місці. Db тримає лише DDL і гачок <see cref="Db.With{T}"/>: так нові таблиці
/// не тягнуть за собою десятки методів у спільний файл, який правлять усі.
/// </summary>
public sealed class EconomyStore(Db db)
{
    // ---------- дрібні помічники ----------

    /// <summary>Ключ гаманця: нік у нижньому регістрі (InvariantCulture), обрізаний. «Оля» і «оля» — один нік.</summary>
    public static string Key(string? nick) => (nick ?? "").Trim().ToLowerInvariant();

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

    static long Scalar(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = Cmd(c, sql, ps);
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }

    static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("o");
    static DateTimeOffset Ts(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    /// <summary>
    /// Коротка транзакція на запис. BEGIN IMMEDIATE бере блокування одразу — інакше два паралельні списання
    /// піднімали б читальну транзакцію до писальної і одне з них падало б із SQLITE_BUSY замість чекати.
    /// </summary>
    static T Write<T>(SqliteConnection c, Func<T> body)
    {
        Exec(c, "BEGIN IMMEDIATE");
        try
        {
            var result = body();
            Exec(c, "COMMIT");
            return result;
        }
        catch
        {
            try { Exec(c, "ROLLBACK"); } catch (SqliteException) { /* транзакції вже нема */ }
            throw;
        }
    }

    // ---------- гаманці й леджер ----------

    public int Balance(string nickKey) =>
        (int)db.With(c => Scalar(c, "SELECT balance FROM wallets WHERE nick_key=$n", ("$n", nickKey)));

    public WalletRow? Wallet(string nickKey) => db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT nick_key, nick, balance, earned, spent FROM wallets WHERE nick_key=$n", ("$n", nickKey));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new WalletRow(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4)) : null;
    });

    static int BalanceIn(SqliteConnection c, string nickKey) =>
        (int)Scalar(c, "SELECT balance FROM wallets WHERE nick_key=$n", ("$n", nickKey));

    static int CounterIn(SqliteConnection c, string nickKey, string key, string day) =>
        (int)Scalar(c, "SELECT n FROM economy_counters WHERE nick_key=$n AND key=$k AND day=$d",
            ("$n", nickKey), ("$k", key), ("$d", day));

    /// <summary>
    /// Нарахування. <paramref name="capKey"/> — лічильник стелі на день (null — без стелі);
    /// <paramref name="refForSeq"/> будує ref із поточного значення лічильника (так «десята хвилина слухання»
    /// і «третій продаж глеків за день» отримують свій унікальний ref без окремої нумерації).
    /// </summary>
    public (GrantResult Result, int Balance) Grant(string nickKey, string nick, int amount, string reason,
        string? refKey, Func<int, string>? refForSeq, string? capKey, string day, int capLimit, int capUnits,
        DateTimeOffset now) => db.With(c => Write(c, () =>
    {
        var seq = capKey is null ? 0 : CounterIn(c, nickKey, capKey, day);
        if (refForSeq is not null) refKey = refForSeq(seq);
        if (capKey is not null && seq + capUnits > capLimit)
            return (GrantResult.Capped, BalanceIn(c, nickKey));

        var sql = refKey is null
            ? "INSERT INTO ledger(nick_key, delta, reason, ref, created_at) VALUES($n, $d, $r, NULL, $t)"
            : "INSERT OR IGNORE INTO ledger(nick_key, delta, reason, ref, created_at) VALUES($n, $d, $r, $f, $t)";
        if (Exec(c, sql, ("$n", nickKey), ("$d", amount), ("$r", reason), ("$f", refKey), ("$t", Iso(now))) == 0)
            return (GrantResult.Duplicate, BalanceIn(c, nickKey));

        if (capKey is not null)
            Exec(c, """
                INSERT INTO economy_counters(nick_key, key, day, n) VALUES($n, $k, $d, $u)
                ON CONFLICT(nick_key, key, day) DO UPDATE SET n = n + $u
                """, ("$n", nickKey), ("$k", capKey), ("$d", day), ("$u", capUnits));

        Exec(c, """
            INSERT INTO wallets(nick_key, nick, balance, earned, spent, updated_at)
            VALUES($n, $nk, $d, $e, 0, $t)
            ON CONFLICT(nick_key) DO UPDATE SET balance = balance + $d, earned = earned + $e,
                nick = $nk, updated_at = $t
            """, ("$n", nickKey), ("$nk", nick), ("$d", amount), ("$e", Math.Max(0, amount)), ("$t", Iso(now)));

        return (GrantResult.Applied, BalanceIn(c, nickKey));
    }));

    /// <summary>
    /// Списання. Атомарне: <c>UPDATE … WHERE balance &gt;= $a</c> — двадцять паралельних спроб при балансі 10
    /// дадуть рівно десять успіхів. Повторне списання з тим самим ref — успіх без другого руху грошей.
    /// </summary>
    public (bool Ok, bool Duplicate, int Balance) Spend(string nickKey, string nick, int amount, string reason,
        string? refKey, DateTimeOffset now) => db.With(c => Write(c, () =>
    {
        // спершу перевірка ref, потім гроші, і лише тоді запис у леджер: якщо грошей не вистачило,
        // ref не «згорає» і чесна спроба після поповнення пройде
        if (refKey is not null && Scalar(c, "SELECT COUNT(*) FROM ledger WHERE ref=$f", ("$f", refKey)) > 0)
            return (true, true, BalanceIn(c, nickKey));

        var changed = Exec(c, """
            UPDATE wallets SET balance = balance - $a, spent = spent + $a, nick = $nk, updated_at = $t
            WHERE nick_key = $n AND balance >= $a
            """, ("$a", amount), ("$nk", nick), ("$t", Iso(now)), ("$n", nickKey));
        if (changed == 0) return (false, false, BalanceIn(c, nickKey));

        Exec(c, "INSERT INTO ledger(nick_key, delta, reason, ref, created_at) VALUES($n, $d, $r, $f, $t)",
            ("$n", nickKey), ("$d", -amount), ("$r", reason), ("$f", refKey), ("$t", Iso(now)));
        return (true, false, BalanceIn(c, nickKey));
    }));

    /// <summary>Перерахувати гаманці з леджера: леджер — істина, wallets — кеш.</summary>
    public void Rebuild(DateTimeOffset now) => db.With(c => Write<object?>(c, () =>
    {
        Exec(c, """
            INSERT OR IGNORE INTO wallets(nick_key, nick, balance, earned, spent, updated_at)
            SELECT nick_key, nick_key, 0, 0, 0, $t FROM ledger GROUP BY nick_key
            """, ("$t", Iso(now)));
        Exec(c, """
            UPDATE wallets SET
                balance = (SELECT COALESCE(SUM(delta), 0) FROM ledger l WHERE l.nick_key = wallets.nick_key),
                earned  = (SELECT COALESCE(SUM(CASE WHEN delta > 0 THEN delta ELSE 0 END), 0) FROM ledger l WHERE l.nick_key = wallets.nick_key),
                spent   = (SELECT COALESCE(SUM(CASE WHEN delta < 0 THEN -delta ELSE 0 END), 0) FROM ledger l WHERE l.nick_key = wallets.nick_key),
                updated_at = $t
            """, ("$t", Iso(now)));
        return null;
    }));

    /// <summary>Лічильник дня (онлайн-хвилини, стелі). Повертає нове значення.</summary>
    public int Bump(string nickKey, string key, string day, int by) => db.With(c => Write(c, () =>
    {
        Exec(c, """
            INSERT INTO economy_counters(nick_key, key, day, n) VALUES($n, $k, $d, $u)
            ON CONFLICT(nick_key, key, day) DO UPDATE SET n = n + $u
            """, ("$n", nickKey), ("$k", key), ("$d", day), ("$u", by));
        return CounterIn(c, nickKey, key, day);
    }));

    public int Counter(string nickKey, string key, string day) =>
        db.With(c => CounterIn(c, nickKey, key, day));

    /// <summary>
    /// Таблиця «черепки»: баланс і скільки набігло за період. У таблиці за день чи тиждень рядки з нулем
    /// не показуємо: інакше «сьогоднішній» топ забивають старі багатії, які сьогодні й не заходили.
    /// </summary>
    public List<ShardRow> TopShards(int n, DateTimeOffset since, bool byBalance) => db.With(c =>
    {
        var period = since > DateTimeOffset.MinValue;
        using var cmd = Cmd(c, $"""
            SELECT nick, balance, got FROM (
                SELECT w.nick AS nick, w.balance AS balance,
                       (SELECT COALESCE(SUM(CASE WHEN l.delta > 0 THEN l.delta ELSE 0 END), 0)
                        FROM ledger l WHERE l.nick_key = w.nick_key AND l.created_at >= $s) AS got
                FROM wallets w)
            WHERE {(period ? "got > 0" : "1 = 1")}
            ORDER BY {(byBalance ? "balance DESC, got DESC" : "got DESC, balance DESC")}
            LIMIT $n
            """, ("$s", Iso(since)), ("$n", n));
        using var r = cmd.ExecuteReader();
        var list = new List<ShardRow>();
        while (r.Read()) list.Add(new ShardRow(r.GetString(0), r.GetInt32(1), r.GetInt32(2)));
        return list;
    });

    // ---------- результати партій ----------

    const string InsertResult = """
        INSERT OR IGNORE INTO game_results(room_id, game, round, nick_key, nick, outcome, score, opponents, stake, tries, created_at)
        VALUES($room, $g, $rd, $n, $nk, $o, $sc, $opp, $st, 1, $t)
        """;

    static bool AddResultIn(SqliteConnection c, ResultRow row) => Exec(c, InsertResult,
        ("$room", row.RoomId), ("$g", row.Game), ("$rd", row.Round), ("$n", row.NickKey), ("$nk", row.Nick),
        ("$o", row.Outcome), ("$sc", row.Score), ("$opp", row.Opponents), ("$st", row.Stake), ("$t", Iso(row.At))) > 0;

    /// <summary>Записати результат партії. false — такий рядок уже був (подвійний Finish не подвоює нічого).</summary>
    public bool AddResult(ResultRow row) => db.With(c => AddResultIn(c, row));

    /// <summary>
    /// Записати результати всіх учасників партії однією транзакцією: або нова партія цілком, або нічого.
    /// Інакше збій між учасниками лишив би партію напівзаписаною, і повторна подія порахувала б Ело вдруге.
    /// Повертає на кожен рядок «свіжий?» (false — такий уже був).
    /// </summary>
    public List<bool> AddResults(IReadOnlyList<ResultRow> rows) => db.With(c => Write(c, () =>
        rows.Select(r => AddResultIn(c, r)).ToList()));

    /// <summary>
    /// Соло-результат: один рядок на ключ кімнати і день (<c>room_id</c> уже містить день — див.
    /// <c>Rewards.Solo</c>). Гірший результат наявний не псує, спроби рахуються. Гончарне коло шле Score
    /// після кожної дії — тому саме upsert, а не рядок на подію; а день у ключі дає таблицям чесний
    /// «найкращий результат за період»: місячної давнини рекорд у сьогоднішній топ не лізе.
    /// </summary>
    public void AddSolo(ResultRow row, bool higherIsBetter) => db.With(c => Write<object?>(c, () =>
    {
        Exec(c, $"""
            INSERT INTO game_results(room_id, game, round, nick_key, nick, outcome, score, opponents, stake, tries, created_at)
            VALUES($room, $g, 0, $n, $nk, 'solo', $sc, NULL, 0, 1, $t)
            ON CONFLICT(room_id, round, nick_key) DO UPDATE SET
                nick = $nk, tries = tries + 1, created_at = $t,
                score = CASE WHEN score IS NULL THEN $sc
                             WHEN $sc IS NULL THEN score
                             WHEN {(higherIsBetter ? "$sc > score" : "$sc < score")} THEN $sc ELSE score END
            """, ("$room", row.RoomId), ("$g", row.Game), ("$n", row.NickKey), ("$nk", row.Nick),
            ("$sc", row.Score), ("$t", Iso(row.At)));
        return null;
    }));

    public int CountResults(string nickKey, string? game, string? outcome, DateTimeOffset since) => db.With(c =>
        (int)Scalar(c, """
            SELECT COUNT(*) FROM game_results
            WHERE nick_key = $n AND ($g IS NULL OR game = $g) AND ($o IS NULL OR outcome = $o) AND created_at >= $s
            """, ("$n", nickKey), ("$g", game), ("$o", outcome), ("$s", Iso(since))));

    /// <summary>Ігри, у яких нік уже вигравав (для «Настільний»).</summary>
    public HashSet<string> GamesWon(string nickKey) => db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT DISTINCT game FROM game_results WHERE nick_key=$n AND outcome='win'", ("$n", nickKey));
        using var r = cmd.ExecuteReader();
        var set = new HashSet<string>(StringComparer.Ordinal);
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    });

    /// <summary>Останні мультиплеєрні результати ніка (для серії й профілю).</summary>
    public List<ResultRow> RecentResults(string nickKey, int n, bool multiplayerOnly = false) => db.With(c =>
    {
        using var cmd = Cmd(c, $"""
            SELECT room_id, game, round, nick_key, nick, outcome, score, opponents, stake, created_at, tries
            FROM game_results WHERE nick_key = $n {(multiplayerOnly ? "AND outcome <> 'solo'" : "")}
            ORDER BY id DESC LIMIT $k
            """, ("$n", nickKey), ("$k", n));
        using var r = cmd.ExecuteReader();
        var list = new List<ResultRow>();
        while (r.Read())
            list.Add(new ResultRow(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4),
                r.GetString(5), r.IsDBNull(6) ? null : r.GetInt64(6), Str(r, 7), r.GetInt32(8), Ts(r.GetString(9)), r.GetInt32(10)));
        return list;
    });

    /// <summary>
    /// Серії перемог одразу для набору ніків (таблиця рейтингової гри — це двадцять рядків, і по запиту
    /// на кожен було б двадцять з'єднань). Нічия серію не рве, поразка — рве.
    /// </summary>
    public Dictionary<string, int> WinStreaks(IReadOnlyList<string> nickKeys, int lookback)
    {
        var streaks = new Dictionary<string, int>(StringComparer.Ordinal);
        if (nickKeys.Count == 0) return streaks;
        var ps = new List<(string, object?)>();
        for (var i = 0; i < nickKeys.Count; i++) ps.Add(($"$k{i}", nickKeys[i]));
        ps.Add(("$n", lookback));
        var names = string.Join(", ", nickKeys.Select((_, i) => $"$k{i}"));
        return db.With(c =>
        {
            using var cmd = Cmd(c, $"""
                SELECT nick_key, outcome FROM (
                    SELECT nick_key, outcome, ROW_NUMBER() OVER (PARTITION BY nick_key ORDER BY id DESC) AS rn
                    FROM game_results WHERE nick_key IN ({names}) AND outcome <> 'solo')
                WHERE rn <= $n ORDER BY nick_key, rn
                """, ps.ToArray());
            using var r = cmd.ExecuteReader();
            var stopped = new HashSet<string>(StringComparer.Ordinal);
            while (r.Read())
            {
                var key = r.GetString(0);
                if (stopped.Contains(key)) continue;
                var outcome = r.GetString(1);
                if (outcome == "win") streaks[key] = streaks.GetValueOrDefault(key) + 1;
                else if (outcome == "loss") stopped.Add(key);
            }
            foreach (var k in nickKeys) streaks.TryAdd(k, 0);
            return streaks;
        });
    }

    /// <summary>Таблиця не-рейтингової гри: скільки перемог/нічиїх/поразок за період.</summary>
    public List<(string Nick, int Wins, int Draws, int Losses)> TopWins(string game, DateTimeOffset since, int n) => db.With(c =>
    {
        using var cmd = Cmd(c, """
            SELECT nick,
                   SUM(CASE WHEN outcome = 'win' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN outcome = 'draw' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN outcome = 'loss' THEN 1 ELSE 0 END)
            FROM game_results WHERE game = $g AND outcome <> 'solo' AND created_at >= $s
            GROUP BY nick_key ORDER BY 2 DESC, 3 DESC LIMIT $n
            """, ("$g", game), ("$s", Iso(since)), ("$n", n));
        using var r = cmd.ExecuteReader();
        var list = new List<(string, int, int, int)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3)));
        return list;
    });

    /// <summary>Таблиця соло-гри: найкращий результат за період і скільки було спроб.</summary>
    public List<(string Nick, long Best, int Tries)> TopSolo(string game, DateTimeOffset since, bool higherIsBetter, int n) => db.With(c =>
    {
        using var cmd = Cmd(c, $"""
            SELECT nick, {(higherIsBetter ? "MAX(score)" : "MIN(score)")}, SUM(tries)
            FROM game_results WHERE game = $g AND outcome = 'solo' AND score IS NOT NULL AND created_at >= $s
            GROUP BY nick_key ORDER BY 2 {(higherIsBetter ? "DESC" : "ASC")} LIMIT $n
            """, ("$g", game), ("$s", Iso(since)), ("$n", n));
        using var r = cmd.ExecuteReader();
        var list = new List<(string, long, int)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetInt64(1), r.GetInt32(2)));
        return list;
    });

    /// <summary>Найкращий соло-результат ніка (для ачівок гончаря).</summary>
    public long? BestSolo(string nickKey, string game, bool higherIsBetter) => db.With<long?>(c =>
    {
        using var cmd = Cmd(c, $"""
            SELECT {(higherIsBetter ? "MAX(score)" : "MIN(score)")} FROM game_results
            WHERE nick_key = $n AND game = $g AND outcome = 'solo' AND score IS NOT NULL
            """, ("$n", nickKey), ("$g", game));
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : Convert.ToInt64(v, CultureInfo.InvariantCulture);
    });

    // ---------- рейтинги ----------

    public RatingRow? Rating(string nickKey, string game) => db.With(c => ReadRating(c, nickKey, game));

    static RatingRow? ReadRating(SqliteConnection c, string nickKey, string game)
    {
        using var cmd = Cmd(c, "SELECT nick_key, nick, game, elo, games, wins, losses, draws FROM ratings WHERE nick_key=$n AND game=$g",
            ("$n", nickKey), ("$g", game));
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new RatingRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7))
            : null;
    }

    /// <summary>Обидва рейтинги і запис нових значень — в одній транзакції, щоб Ело не поїхало від паралельної партії.</summary>
    public (RatingRow A, RatingRow B) UpdatePair(string gameId, (string Key, string Nick) a, (string Key, string Nick) b,
        Func<RatingRow, RatingRow, (RatingRow, RatingRow)> compute, DateTimeOffset now) => db.With(c => Write(c, () =>
    {
        var ra = ReadRating(c, a.Key, gameId) ?? new RatingRow(a.Key, a.Nick, gameId, 1000, 0, 0, 0, 0);
        var rb = ReadRating(c, b.Key, gameId) ?? new RatingRow(b.Key, b.Nick, gameId, 1000, 0, 0, 0, 0);
        var (na, nb) = compute(ra with { Nick = a.Nick }, rb with { Nick = b.Nick });
        foreach (var x in new[] { na, nb })
            Exec(c, """
                INSERT INTO ratings(nick_key, game, nick, elo, games, wins, losses, draws, updated_at)
                VALUES($n, $g, $nk, $e, $ga, $w, $l, $d, $t)
                ON CONFLICT(nick_key, game) DO UPDATE SET nick=$nk, elo=$e, games=$ga, wins=$w, losses=$l, draws=$d, updated_at=$t
                """, ("$n", x.NickKey), ("$g", gameId), ("$nk", x.Nick), ("$e", x.Elo), ("$ga", x.Games),
                ("$w", x.Wins), ("$l", x.Losses), ("$d", x.Draws), ("$t", Iso(now)));
        return (na, nb);
    }));

    public List<RatingRow> TopRatings(string game, int n) => db.With(c =>
    {
        using var cmd = Cmd(c, """
            SELECT nick_key, nick, game, elo, games, wins, losses, draws FROM ratings
            WHERE game = $g AND games > 0 ORDER BY elo DESC, wins DESC LIMIT $n
            """, ("$g", game), ("$n", n));
        using var r = cmd.ExecuteReader();
        var list = new List<RatingRow>();
        while (r.Read())
            list.Add(new RatingRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7)));
        return list;
    });

    public List<RatingRow> RatingsOf(string nickKey) => db.With(c =>
    {
        using var cmd = Cmd(c, """
            SELECT nick_key, nick, game, elo, games, wins, losses, draws FROM ratings
            WHERE nick_key = $n AND games > 0 ORDER BY elo DESC
            """, ("$n", nickKey));
        using var r = cmd.ExecuteReader();
        var list = new List<RatingRow>();
        while (r.Read())
            list.Add(new RatingRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7)));
        return list;
    });

    // ---------- ачівки ----------

    /// <summary>true — щойно видали; false — вона в нього вже була.</summary>
    public bool Unlock(string nickKey, string nick, string key, DateTimeOffset now) => db.With(c => Exec(c,
        "INSERT OR IGNORE INTO achievements(nick_key, key, nick, unlocked_at) VALUES($n, $k, $nk, $t)",
        ("$n", nickKey), ("$k", key), ("$nk", nick), ("$t", Iso(now))) > 0);

    public bool HasAchievement(string nickKey, string key) => db.With(c =>
        Scalar(c, "SELECT COUNT(*) FROM achievements WHERE nick_key=$n AND key=$k", ("$n", nickKey), ("$k", key)) > 0);

    public List<UnlockedRow> AchievementsOf(string nickKey) => db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT key, unlocked_at FROM achievements WHERE nick_key=$n ORDER BY unlocked_at", ("$n", nickKey));
        using var r = cmd.ExecuteReader();
        var list = new List<UnlockedRow>();
        while (r.Read()) list.Add(new UnlockedRow(r.GetString(0), Ts(r.GetString(1))));
        return list;
    });

    // ---------- щоденне ----------

    /// <summary>Upsert результату дня: краще (менше спроб, потім менший час) перекриває гірше, гірше — ні.</summary>
    public void SaveDaily(DailyRow row, DateTimeOffset now) => db.With(c => Write<object?>(c, () =>
    {
        Exec(c, """
            INSERT INTO daily_results(day, game, nick_key, nick, solved, attempts, ms, created_at)
            VALUES($d, $g, $n, $nk, $s, $a, $m, $t)
            ON CONFLICT(day, game, nick_key) DO UPDATE SET
                nick = $nk,
                solved = MAX(solved, $s),
                attempts = CASE WHEN solved = 0 AND $s = 1 THEN $a
                                WHEN $s = 1 AND $a < attempts THEN $a ELSE attempts END,
                ms = CASE WHEN solved = 0 AND $s = 1 THEN $m
                          WHEN $s = 1 AND ($a < attempts OR ($a = attempts AND $m < ms)) THEN $m ELSE ms END
            """, ("$d", row.Day), ("$g", row.Game), ("$n", row.NickKey), ("$nk", row.Nick),
            ("$s", row.Solved ? 1 : 0), ("$a", row.Attempts), ("$m", row.Ms), ("$t", Iso(now)));
        return null;
    }));

    static DailyRow ReadDailyRow(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2),
        r.GetString(3), r.GetInt32(4) == 1, r.GetInt32(5), r.GetInt32(6));

    public DailyRow? Daily(string day, string game, string nickKey) => db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT day, game, nick_key, nick, solved, attempts, ms FROM daily_results WHERE day=$d AND game=$g AND nick_key=$n",
            ("$d", day), ("$g", game), ("$n", nickKey));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new DailyRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4) == 1, r.GetInt32(5), r.GetInt32(6)) : null;
    });

    public List<DailyRow> DailyTop(string day, string game, int n) => db.With(c =>
    {
        using var cmd = Cmd(c, """
            SELECT day, game, nick_key, nick, solved, attempts, ms FROM daily_results
            WHERE day = $d AND game = $g AND solved = 1 ORDER BY attempts ASC, ms ASC, created_at ASC LIMIT $n
            """, ("$d", day), ("$g", game), ("$n", n));
        using var r = cmd.ExecuteReader();
        var list = new List<DailyRow>();
        while (r.Read()) list.Add(new DailyRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4) == 1, r.GetInt32(5), r.GetInt32(6)));
        return list;
    });

    public int DailySolvedCount(string day, string game) => db.With(c =>
        (int)Scalar(c, "SELECT COUNT(*) FROM daily_results WHERE day=$d AND game=$g AND solved=1", ("$d", day), ("$g", game)));

    /// <summary>Дні, коли нік розв'язав цю головоломку, найновіші спершу — з них рахується серія.</summary>
    public List<string> SolvedDays(string nickKey, string game, int limit) => db.With(c =>
    {
        using var cmd = Cmd(c, """
            SELECT day FROM daily_results WHERE nick_key = $n AND game = $g AND solved = 1
            ORDER BY day DESC LIMIT $k
            """, ("$n", nickKey), ("$g", game), ("$k", limit));
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    });

    /// <summary>
    /// Дні всіх головоломок ніка одним запитом: панель «Щоденного глека» і профіль рахують серії
    /// для кожної гри, і окремий запит на кожну — це рівно те, чого не варто робити на HTTP-запиті.
    /// </summary>
    public Dictionary<string, HashSet<string>> SolvedDaysAll(string nickKey, int limitPerGame) => db.With(c =>
    {
        using var cmd = Cmd(c, """
            SELECT game, day FROM (
                SELECT game, day, ROW_NUMBER() OVER (PARTITION BY game ORDER BY day DESC) AS rn
                FROM daily_results WHERE nick_key = $n AND solved = 1)
            WHERE rn <= $k
            """, ("$n", nickKey), ("$k", limitPerGame));
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        while (r.Read())
        {
            var game = r.GetString(0);
            if (!map.TryGetValue(game, out var days)) map[game] = days = new HashSet<string>(StringComparer.Ordinal);
            days.Add(r.GetString(1));
        }
        return map;
    });

    /// <summary>
    /// Усе, що треба панелі «Щоденного глека», на одному з'єднанні: мій результат, скільки розв'язало
    /// і топ дня — для всіх головоломок разом.
    /// </summary>
    public List<DailyPanelRow> DailyPanel(string day, string nickKey, IReadOnlyList<string> games, int topN) => db.With(c =>
    {
        var mine = new Dictionary<string, DailyRow>(StringComparer.Ordinal);
        using (var cmd = Cmd(c, "SELECT day, game, nick_key, nick, solved, attempts, ms FROM daily_results WHERE day=$d AND nick_key=$n",
                   ("$d", day), ("$n", nickKey)))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) { var row = ReadDailyRow(r); mine[row.Game] = row; }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var cmd = Cmd(c, "SELECT game, COUNT(*) FROM daily_results WHERE day=$d AND solved=1 GROUP BY game", ("$d", day)))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) counts[r.GetString(0)] = r.GetInt32(1);

        var tops = new Dictionary<string, List<DailyRow>>(StringComparer.Ordinal);
        using (var cmd = Cmd(c, """
                   SELECT day, game, nick_key, nick, solved, attempts, ms FROM daily_results
                   WHERE day = $d AND solved = 1 ORDER BY game, attempts ASC, ms ASC, created_at ASC
                   """, ("$d", day)))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var row = ReadDailyRow(r);
                if (!tops.TryGetValue(row.Game, out var list)) tops[row.Game] = list = new List<DailyRow>();
                if (list.Count < topN) list.Add(row);
            }

        return games.Select(g => new DailyPanelRow(g, mine.GetValueOrDefault(g), counts.GetValueOrDefault(g),
            tops.TryGetValue(g, out var t) ? t : new List<DailyRow>())).ToList();
    });

    /// <summary>Усе розв'язане за день (для таблиці «щоденне» без розбивки за іграми).</summary>
    public List<DailyRow> DailyOfDay(string day, int n) => db.With(c =>
    {
        using var cmd = Cmd(c, """
            SELECT day, game, nick_key, nick, solved, attempts, ms FROM daily_results
            WHERE day = $d AND solved = 1 ORDER BY game, attempts ASC, ms ASC LIMIT $n
            """, ("$d", day), ("$n", n));
        using var r = cmd.ExecuteReader();
        var list = new List<DailyRow>();
        while (r.Read()) list.Add(new DailyRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4) == 1, r.GetInt32(5), r.GetInt32(6)));
        return list;
    });
}

/// <summary>
/// Стан Persistent-ігор (щоденне, гончарне коло) у таблиці <c>game_state</c>. Ключ будує сама гра
/// (<see cref="Game.SoloKey"/>), тут — лише зберегти/прочитати/викинути.
/// </summary>
public sealed class SqliteGameStore(Db db, IClock clock) : IGameStore
{
    public void SaveState(string key, string json) => db.Exec("""
        INSERT INTO game_state(key, json, updated_at) VALUES($k, $j, $t)
        ON CONFLICT(key) DO UPDATE SET json = excluded.json, updated_at = excluded.updated_at
        """, ("$k", key), ("$j", json), ("$t", clock.UtcNow.ToString("o")));

    public string? LoadState(string key) => db.With(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM game_state WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    });

    public void DeleteState(string key) => db.Exec("DELETE FROM game_state WHERE key=$k", ("$k", key));
}
