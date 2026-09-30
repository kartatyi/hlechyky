using System.Globalization;
using System.Text.RegularExpressions;
using Hlechyky.Games.Economy;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Факти для живої реклами — лише з ігрової й сайтової статистики, лише читання бази. Жодних цитат із балачок чи
/// пошти: з балачок береться хіба що кількість рядків і системні «X скіпає …» (їх пише сам сайт).
/// <para>
/// «Сьогодні» — з 06:00 за Києвом: ніч до шостої ще «вчора», і нічна гра рахується окремим фактом. Лічильники
/// часу (<c>economy_counters</c>) живуть за календарним днем Києва — там «сьогодні» з опівночі, інакше не буває.
/// </para>
/// </summary>
public sealed class LiveFacts(Db db, GameNames names, IClock clock)
{
    /// <summary>Вимова ніка (з банку фраз); без неї — нік як є.</summary>
    public Func<string, string> Say { get; set; } = n => n;

    public const int MinDayHour = 6;

    /// <summary>Початок «сьогодні»: 06:00 за Києвом (до шостої — вчорашні 06:00).</summary>
    public static DateTimeOffset DayStart(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, Days.Kyiv);
        var day = local.Hour >= MinDayHour ? local.Date : local.Date.AddDays(-1);
        var start = day.AddHours(MinDayHour);
        return new DateTimeOffset(start, Days.Kyiv.GetUtcOffset(start)).ToUniversalTime();
    }

    static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    // Сумісні з «O»-рядками бази: вони всі в UTC з +00:00, тож рядкове порівняння — це порівняння часу.
    string Today => Iso(DayStart(clock.UtcNow));
    string Week => Iso(clock.UtcNow.AddDays(-7));

    /// <summary>Родини фактів: з однієї родини в ролик іде лише один — «програв 9 разів» і «9 поразок підряд» — це повтор.</summary>
    public static string Family(string kind) => kind switch
    {
        "losses_today" or "lose_streak" or "stakes_lost" => "lose",
        "wins_today" or "win_streak" => "win",
        "rich" or "poor" or "hoarder" or "wallet" => "wallet",
        "site_time" or "listen_time" => "time",
        "skips_self" or "ne_te" => "picky",
        _ => kind,
    };

    // =================================================================================================================
    // Факти про одного гравця
    // =================================================================================================================

    /// <summary>
    /// Усе, що вдалося знайти про гравця, від найсоковитішого. <paramref name="present"/> — хто зараз на сайті:
    /// з ним порівнюється гаманець і шукається суперник.
    /// </summary>
    public List<LiveFact> For(string nick, IReadOnlyCollection<string>? present = null)
    {
        var key = Auth.NickKey(nick);
        var say = Say(nick);
        var list = new List<LiveFact>();
        db.With(c =>
        {
            Results(c, key, say, list);
            Streak(c, key, say, list);
            Time(c, key, say, list);
            Night(c, nick, say, list);
            Radio(c, nick, key, say, list);
            Wallet(c, key, say, present ?? [], list);
            Shop(c, key, say, list);
            Stakes(c, key, say, list);
            Achievements(c, key, say, list);
            DailyTries(c, key, say, list);
            Rival(c, key, say, present ?? [], list);
            First(c, key, say, list);
        });
        return list.OrderByDescending(f => f.Juice).ToList();
    }

    void Results(SqliteConnection c, string key, string say, List<LiveFact> list)
    {
        var rows = Rows(c, """
            SELECT game, SUM(outcome = 'win'), SUM(outcome = 'loss'), COUNT(*) FROM game_results
            WHERE nick_key = $k AND created_at >= $d GROUP BY game
            """, r => (Game: r.GetString(0), Wins: r.GetInt32(1), Losses: r.GetInt32(2), All: r.GetInt32(3)), ("$k", key), ("$d", Today));
        var wins = rows.Sum(r => r.Wins);
        var losses = rows.Sum(r => r.Losses);
        if (losses >= 3)
        {
            var worst = rows.OrderByDescending(r => r.Losses).First();
            list.Add(LiveFact.Of("losses_today", 0.3 + 0.07 * losses, ("nick", say), ("n", losses), ("game", Title(worst.Game)),
                ("m", worst.Losses), ("w", wins)));
        }
        if (wins >= 3)
        {
            var best = rows.OrderByDescending(r => r.Wins).First();
            list.Add(LiveFact.Of("wins_today", 0.25 + 0.06 * wins, ("nick", say), ("n", wins), ("game", Title(best.Game)), ("m", best.Wins)));
        }
        // Клік по колу — одна «партія» на день, тож гра дня рахується без нього
        var top = rows.Where(r => r.Game != "clicker").OrderByDescending(r => r.All).FirstOrDefault();
        if (top.All >= 5)
            list.Add(LiveFact.Of("game_of_day", Math.Min(0.8, 0.2 + 0.03 * top.All), ("nick", say), ("n", top.All), ("game", Title(top.Game))));
    }

    /// <summary>Поточна серія сьогодні: однакові результати підряд від останньої партії (нічиї й соло серію не рвуть і не множать).</summary>
    void Streak(SqliteConnection c, string key, string say, List<LiveFact> list)
    {
        var (outcome, n, game) = CurrentStreak(c, key, null);
        if (n < 3) return;
        var values = new List<(string, object)> { ("nick", say), ("n", n) };
        if (game is not null) values.Add(("game", Title(game)));
        list.Add(outcome == "loss"
            ? LiveFact.Of("lose_streak", 0.4 + 0.1 * n, [.. values])
            : LiveFact.Of("win_streak", 0.35 + 0.08 * n, [.. values]));
    }

    /// <summary>Серія гравця сьогодні (в одній грі, коли <paramref name="onlyGame"/> задано): результат, довжина, гра (якщо вся серія в одній).</summary>
    public (string Outcome, int N, string? Game) CurrentStreak(string nickKey, string? onlyGame) =>
        db.With(c => CurrentStreak(c, nickKey, onlyGame));

    (string Outcome, int N, string? Game) CurrentStreak(SqliteConnection c, string key, string? onlyGame)
    {
        var rows = Rows(c, $"""
            SELECT outcome, game FROM game_results WHERE nick_key = $k AND created_at >= $d AND outcome IN ('win', 'loss')
            {(onlyGame is null ? "" : "AND game = $g")} ORDER BY id DESC LIMIT 60
            """, r => (Outcome: r.GetString(0), Game: r.GetString(1)), ("$k", key), ("$d", Today), ("$g", onlyGame));
        if (rows.Count == 0) return ("", 0, null);
        var first = rows[0].Outcome;
        var run = rows.TakeWhile(r => r.Outcome == first).ToList();
        var games = run.Select(r => r.Game).Distinct().ToList();
        return (first, run.Count, games.Count == 1 ? games[0] : null);
    }

    void Time(SqliteConnection c, string key, string say, List<LiveFact> list)
    {
        var day = Days.Of(clock.UtcNow);
        var times = Rows(c, "SELECT key, n FROM economy_counters WHERE nick_key = $k AND day = $d AND key LIKE 'time:%'",
            r => (Key: r.GetString(0), Sec: r.GetInt64(1)), ("$k", key), ("$d", day)).ToDictionary(t => t.Key, t => t.Sec);
        var site = times.GetValueOrDefault("time:site");
        if (site >= 2 * 3600)
            list.Add(LiveFact.Of("site_time", 0.3 + 0.08 * (site / 3600), ("nick", say), ("h", site / 3600), ("min", site / 60)));
        var clicker = times.GetValueOrDefault("time:game:clicker");
        // Коло тиждень — бо соло-коло хтось мішає за день по годині, а хтось сидить по п'ять
        var week = Scalar(c, "SELECT COALESCE(SUM(n), 0) FROM economy_counters WHERE nick_key = $k AND key = 'time:game:clicker' AND day >= $w",
            ("$k", key), ("$w", Days.Of(clock.UtcNow.AddDays(-6))));
        if (clicker >= 3600)
            list.Add(LiveFact.Of("clicker_time", 0.35 + 0.1 * (clicker / 3600), ("nick", say), ("h", clicker / 3600), ("wh", week / 3600)));
        else if (week >= 5 * 3600)
            list.Add(LiveFact.Of("clicker_week", 0.3 + 0.03 * (week / 3600), ("nick", say), ("h", week / 3600)));
        var listen = times.GetValueOrDefault("time:listen");
        if (listen >= 3600)
            list.Add(LiveFact.Of("listen_time", 0.15 + 0.03 * (listen / 3600), ("nick", say), ("h", listen / 3600)));
    }

    /// <summary>Минула ніч (00:00–06:00 за Києвом перед «сьогодні»): замовлення й рядки в балачках — лише кількість.</summary>
    void Night(SqliteConnection c, string nick, string say, List<LiveFact> list)
    {
        var end = DayStart(clock.UtcNow);
        var (from, to) = (Iso(end.AddHours(-MinDayHour)), Iso(end));
        var orders = Scalar(c, "SELECT COUNT(*) FROM plays WHERE source = 'user' AND requested_by = $n AND started_at >= $f AND started_at < $t",
            ("$n", nick), ("$f", from), ("$t", to));
        var chat = Scalar(c, "SELECT COUNT(*) FROM chat WHERE kind = 'chat' AND nick = $n AND created_at >= $f AND created_at < $t",
            ("$n", nick), ("$f", from), ("$t", to));
        var games = Scalar(c, "SELECT COUNT(*) FROM game_results WHERE nick_key = $k AND created_at >= $f AND created_at < $t",
            ("$k", Auth.NickKey(nick)), ("$f", from), ("$t", to));
        var all = orders + chat + games;
        if (all < 3) return;
        list.Add(LiveFact.Of("night_owl", 0.5 + 0.03 * all, ("nick", say), ("n", orders), ("m", chat), ("g", games), ("all", all)));
    }

    void Radio(SqliteConnection c, string nick, string key, string say, List<LiveFact> list)
    {
        // Той самий трек за тиждень
        var same = Rows(c, """
            SELECT t.title, t.artist, COUNT(*) FROM plays p JOIN tracks t ON t.id = p.track_id
            WHERE p.source = 'user' AND p.requested_by = $n AND p.started_at >= $w
            GROUP BY p.track_id ORDER BY 3 DESC LIMIT 1
            """, r => (Title: r.GetString(0), Artist: r.GetString(1), N: r.GetInt32(2)), ("$n", nick), ("$w", Week)).FirstOrDefault();
        if (same.N >= 3)
            list.Add(LiveFact.Of("same_track", 0.4 + 0.08 * same.N, ("nick", say), ("n", same.N), ("track", Track(same.Title)), ("artist", Track(same.Artist))));

        var skipsSelf = Scalar(c, "SELECT COUNT(*) FROM chat WHERE kind = 'system' AND text LIKE $p AND created_at >= $d",
            ("$p", nick + " скіпає %"), ("$d", Today));
        if (skipsSelf >= 3)
            list.Add(LiveFact.Of("skips_self", 0.3 + 0.05 * skipsSelf, ("nick", say), ("n", skipsSelf)));

        // Скіпнуте замовлення, біля кінця якого нема рядка «нік скіпає» від самого замовника, — скіпнув хтось інший
        var skippedByOthers = Scalar(c, """
            SELECT COUNT(*) FROM plays p WHERE p.source = 'user' AND p.requested_by = $n AND p.skipped = 1 AND p.started_at >= $d
            AND NOT EXISTS (SELECT 1 FROM chat ch WHERE ch.kind = 'system' AND ch.text LIKE $p
                AND ch.created_at >= p.started_at AND ch.created_at <= COALESCE(p.ended_at, p.started_at))
            """, ("$n", nick), ("$d", Today), ("$p", nick + " скіпає %"));
        if (skippedByOthers >= 2)
            list.Add(LiveFact.Of("skipped_by_others", 0.45 + 0.08 * skippedByOthers, ("nick", say), ("n", skippedByOthers)));

        var neTe = Scalar(c, "SELECT COUNT(*) FROM dj_feedback WHERE kind = 'dismiss' AND nick = $n AND created_at >= $d", ("$n", nick), ("$d", Today));
        if (neTe >= 3)
            list.Add(LiveFact.Of("ne_te", 0.35 + 0.04 * neTe, ("nick", say), ("n", neTe)));

        var likes = Scalar(c, "SELECT COUNT(*) FROM likes WHERE nick = $n AND created_at >= $d", ("$n", nick), ("$d", Today));
        if (likes >= 5)
            list.Add(LiveFact.Of("likes", 0.25 + 0.02 * likes, ("nick", say), ("n", likes)));
    }

    void Wallet(SqliteConnection c, string key, string say, IReadOnlyCollection<string> present, List<LiveFact> list)
    {
        var me = Rows(c, "SELECT balance, earned, spent FROM wallets WHERE nick_key = $k",
            r => (Balance: r.GetInt64(0), Earned: r.GetInt64(1), Spent: r.GetInt64(2)), ("$k", key)).FirstOrDefault();
        if (me.Earned >= 300 && me.Spent * 50 <= me.Earned)
            list.Add(LiveFact.Of("hoarder", 0.55, ("nick", say), ("n", me.Earned), ("m", me.Spent), ("b", me.Balance)));
        var others = present.Select(Auth.NickKey).Where(k => k != key).Distinct().ToList();
        if (others.Count > 0 && me.Balance > 0)
        {
            var balances = others.Select(k => Scalar(c, "SELECT COALESCE(MAX(balance), 0) FROM wallets WHERE nick_key = $k", ("$k", k))).ToList();
            if (me.Balance > balances.Max())
                list.Add(LiveFact.Of("rich", 0.45, ("nick", say), ("n", me.Balance)));
            else if (me.Balance < balances.Min())
                list.Add(LiveFact.Of("poor", 0.45, ("nick", say), ("n", me.Balance)));
        }
        if (me.Balance > 0)
            list.Add(LiveFact.Of("wallet", 0.1, ("nick", say), ("n", me.Balance)));
    }

    void Shop(SqliteConnection c, string key, string say, List<LiveFact> list)
    {
        var (n, sum) = Rows(c, "SELECT COUNT(*), COALESCE(SUM(price), 0) FROM lavka_owned WHERE nick_key = $k AND source = 'buy' AND at >= $w",
            r => (r.GetInt32(0), r.GetInt64(1)), ("$k", key), ("$w", Week)).FirstOrDefault();
        if (n >= 1 && sum >= 300)
            list.Add(LiveFact.Of("lavka_buys", 0.35 + sum / 5000.0, ("nick", say), ("n", n), ("m", sum)));
    }

    void Stakes(SqliteConnection c, string key, string say, List<LiveFact> list)
    {
        var (bets, spent) = Rows(c, "SELECT COUNT(*), COALESCE(-SUM(delta), 0) FROM ledger WHERE nick_key = $k AND reason = 'stake' AND created_at >= $d",
            r => (r.GetInt32(0), r.GetInt64(1)), ("$k", key), ("$d", Today)).FirstOrDefault();
        var (won, back) = Rows(c, "SELECT COUNT(*), COALESCE(SUM(delta), 0) FROM ledger WHERE nick_key = $k AND reason IN ('stake-win', 'stake-refund') AND created_at >= $d",
            r => (r.GetInt32(0), r.GetInt64(1)), ("$k", key), ("$d", Today)).FirstOrDefault();
        var lost = bets - won;
        if (lost >= 2 && spent > back)
            list.Add(LiveFact.Of("stakes_lost", 0.4 + 0.07 * lost, ("nick", say), ("n", lost), ("m", spent - back)));
    }

    void Achievements(SqliteConnection c, string key, string say, List<LiveFact> list)
    {
        var keys = Rows(c, "SELECT key FROM achievements WHERE nick_key = $k AND unlocked_at >= $d ORDER BY unlocked_at DESC",
            r => r.GetString(0), ("$k", key), ("$d", Today));
        if (keys.Count >= 2)
            list.Add(LiveFact.Of("ach_today", 0.3 + 0.05 * keys.Count, ("nick", say), ("n", keys.Count),
                ("ach", AchievementCatalog.Get(keys[0])?.Title ?? "якась ачівка")));
    }

    void DailyTries(SqliteConnection c, string key, string say, List<LiveFact> list)
    {
        var rows = Rows(c, "SELECT game, solved, attempts FROM daily_results WHERE nick_key = $k AND day = $d",
            r => (Game: r.GetString(0), Solved: r.GetInt32(1) == 1, Tries: r.GetInt32(2)), ("$k", key), ("$d", Days.Of(clock.UtcNow)));
        var bad = rows.Where(r => !r.Solved || r.Tries >= 5).OrderByDescending(r => r.Tries).FirstOrDefault();
        if (bad.Game is null) return;
        list.Add(bad.Solved
            ? LiveFact.Of("daily_tries", 0.3 + 0.04 * bad.Tries, ("nick", say), ("n", bad.Tries), ("game", Title(bad.Game)))
            : LiveFact.Of("daily_fail", 0.45, ("nick", say), ("n", bad.Tries), ("game", Title(bad.Game))));
    }

    /// <summary>Суперник серед присутніх: з ким найбільше спільних партій за тиждень і який рахунок.</summary>
    void Rival(SqliteConnection c, string key, string say, IReadOnlyCollection<string> present, List<LiveFact> list)
    {
        var best = (Nick: "", Games: 0, Wins: 0, Losses: 0, Game: "");
        foreach (var other in present.Where(p => Auth.NickKey(p) != key && !Auth.IsGuestNick(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var rows = Rows(c, """
                SELECT a.game, a.outcome, b.outcome FROM game_results a
                JOIN game_results b ON b.room_id = a.room_id AND b.round = a.round AND b.nick_key = $o
                WHERE a.nick_key = $k AND a.created_at >= $w
                """, r => (Game: r.GetString(0), Me: r.GetString(1), Him: r.GetString(2)), ("$k", key), ("$o", Auth.NickKey(other)), ("$w", Week));
            if (rows.Count <= best.Games) continue;
            var game = rows.GroupBy(r => r.Game).OrderByDescending(g => g.Count()).First().Key;
            best = (other, rows.Count, rows.Count(r => r.Me == "win"), rows.Count(r => r.Me == "loss"), game);
        }
        if (best.Games < 3) return;
        var lopsided = Math.Abs(best.Wins - best.Losses) >= 3;
        list.Add(LiveFact.Of(best.Wins >= best.Losses ? "rival_ahead" : "rival_behind", 0.5 + (lopsided ? 0.2 : 0) + 0.01 * best.Games,
            ("nick", say), ("rival", Say(best.Nick)), ("n", Math.Max(best.Wins, best.Losses)), ("m", Math.Min(best.Wins, best.Losses)),
            ("g", best.Games), ("game", Title(best.Game))));
    }

    /// <summary>Хто перший прийшов сьогодні: найраніший запис гаманця (слухання, онлайн, гра) з 06:00.</summary>
    void First(SqliteConnection c, string key, string say, List<LiveFact> list)
    {
        var first = Rows(c, """
            SELECT l.nick_key FROM ledger l JOIN accounts a ON a.nick_key = l.nick_key
            WHERE l.created_at >= $d ORDER BY l.created_at LIMIT 1
            """, r => r.GetString(0), ("$d", Today)).FirstOrDefault();
        if (first == key)
        {
            var at = Rows(c, "SELECT MIN(created_at) FROM ledger WHERE nick_key = $k AND created_at >= $d", r => r.GetString(0), ("$k", key), ("$d", Today)).First();
            var local = TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(at, CultureInfo.InvariantCulture), Days.Kyiv);
            list.Add(LiveFact.Of("first_today", 0.35, ("nick", say), ("hh", local.Hour), ("mm", local.Minute.ToString("00"))));
        }
    }

    // =================================================================================================================
    // Новини дня
    // =================================================================================================================

    /// <summary>
    /// Рядки дайджесту дня. Ті, хто відмовився від прожарок (<paramref name="hidden"/>), поіменно не згадуються:
    /// рядок бере наступного в списку або пропускається. <c>Sport</c> — факти для спортивного блоку.
    /// </summary>
    public List<(LiveFact Fact, bool Sport)> News(ISet<string> hidden)
    {
        var d = Today;
        var list = new List<(LiveFact, bool)>();
        bool Ok(string k) => !hidden.Contains(Auth.NickKey(k)) && !Auth.IsGuestNick(k);
        db.With(c =>
        {
            var winners = Rows(c, """
                SELECT nick_key, MAX(nick), COUNT(*) FROM game_results WHERE outcome = 'win' AND created_at >= $d
                GROUP BY nick_key ORDER BY 3 DESC LIMIT 5
                """, r => (Key: r.GetString(0), Nick: r.GetString(1), N: r.GetInt32(2)), ("$d", d));
            if (winners.FirstOrDefault(w => Ok(w.Key)) is { N: >= 2 } top)
                list.Add((LiveFact.Of("top_winner", 0.6, ("nick", Say(top.Nick)), ("n", top.N)), true));

            // Найдовша серія поразок сьогодні — серед тих, хто грав
            var players = Rows(c, "SELECT DISTINCT nick_key, nick FROM game_results WHERE created_at >= $d AND outcome IN ('win', 'loss')",
                r => (Key: r.GetString(0), Nick: r.GetString(1)), ("$d", d));
            var worst = players.Where(p => Ok(p.Key)).Select(p => (p.Nick, S: MaxLoseRun(c, p.Key, d))).OrderByDescending(p => p.S.N).FirstOrDefault();
            if (worst.S.N >= 3)
                list.Add((LiveFact.Of("lose_streak", 0.6, ("nick", Say(worst.Nick)), ("n", worst.S.N), ("game", Title(worst.S.Game))), true));

            var game = Rows(c, """
                SELECT game, COUNT(DISTINCT room_id || ':' || round) FROM game_results WHERE created_at >= $d AND game <> 'clicker'
                GROUP BY game ORDER BY 2 DESC LIMIT 1
                """, r => (Game: r.GetString(0), N: r.GetInt32(1)), ("$d", d)).FirstOrDefault();
            if (game.N >= 3)
                list.Add((LiveFact.Of("game_of_day", 0.5, ("game", Title(game.Game)), ("n", game.N)), true));

            var skipped = Rows(c, """
                SELECT t.title, t.artist, COUNT(*) FROM plays p JOIN tracks t ON t.id = p.track_id
                WHERE p.skipped = 1 AND p.started_at >= $d GROUP BY p.track_id ORDER BY 3 DESC LIMIT 1
                """, r => (Title: r.GetString(0), Artist: r.GetString(1), N: r.GetInt32(2)), ("$d", d)).FirstOrDefault();
            if (skipped.N >= 2)
                list.Add((LiveFact.Of("most_skipped", 0.5, ("track", Track(skipped.Title)), ("artist", Track(skipped.Artist)), ("n", skipped.N)), false));

            var orderers = Rows(c, """
                SELECT requested_by, COUNT(*) FROM plays WHERE source = 'user' AND started_at >= $d AND requested_by IS NOT NULL
                AND requested_by <> 'Дядько Глек' GROUP BY requested_by ORDER BY 2 DESC LIMIT 5
                """, r => (Nick: r.GetString(0), N: r.GetInt32(1)), ("$d", d));
            if (orderers.FirstOrDefault(o => Ok(o.Nick) && Account(c, o.Nick)) is { N: >= 3 } dj)
                list.Add((LiveFact.Of("top_orderer", 0.5, ("nick", Say(dj.Nick)), ("n", dj.N)), false));

            var newbies = Rows(c, "SELECT nick FROM accounts WHERE created_at >= $d ORDER BY created_at", r => r.GetString(0), ("$d", d))
                .Where(Ok).ToList();
            if (newbies.Count == 1)
                list.Add((LiveFact.Of("newbie", 0.5, ("nick", Say(newbies[0]))), false));
            else if (newbies.Count > 1)
                list.Add((LiveFact.Of("newbies", 0.5, ("n", newbies.Count), ("nicks", string.Join(", ", newbies.Take(4).Select(Say)))), false));

            var achs = Rows(c, """
                SELECT nick_key, MAX(nick), COUNT(*) FROM achievements WHERE unlocked_at >= $d GROUP BY nick_key ORDER BY 3 DESC LIMIT 5
                """, r => (Key: r.GetString(0), Nick: r.GetString(1), N: r.GetInt32(2)), ("$d", d));
            if (achs.FirstOrDefault(a => Ok(a.Key)) is { N: >= 2 } ach)
                list.Add((LiveFact.Of("record_ach", 0.4, ("nick", Say(ach.Nick)), ("n", ach.N)), true));

            var site = Rows(c, """
                SELECT e.nick_key, COALESCE(a.nick, e.nick_key), e.n FROM economy_counters e LEFT JOIN accounts a ON a.nick_key = e.nick_key
                WHERE e.key = 'time:site' AND e.day = $day ORDER BY e.n DESC LIMIT 5
                """, r => (Key: r.GetString(0), Nick: r.GetString(1), Sec: r.GetInt64(2)), ("$day", Days.Of(clock.UtcNow)));
            if (site.FirstOrDefault(s => Ok(s.Key)) is { Sec: >= 3 * 3600 } longest)
                list.Add((LiveFact.Of("record_time", 0.4, ("nick", Say(longest.Nick)), ("h", longest.Sec / 3600)), false));
        });
        return list;
    }

    (int N, string Game) MaxLoseRun(SqliteConnection c, string key, string day)
    {
        var rows = Rows(c, "SELECT outcome, game FROM game_results WHERE nick_key = $k AND created_at >= $d AND outcome IN ('win', 'loss') ORDER BY id",
            r => (Outcome: r.GetString(0), Game: r.GetString(1)), ("$k", key), ("$d", day));
        int best = 0, run = 0;
        string bestGame = "", game = "";
        foreach (var r in rows)
        {
            if (r.Outcome == "loss")
            {
                run = r.Game == game ? run + 1 : 1;
                game = r.Game;
            }
            else { run = 0; game = ""; }
            if (run > best) (best, bestGame) = (run, game);
        }
        return (best, bestGame);
    }

    // =================================================================================================================
    // Помічники
    // =================================================================================================================

    string Title(string game) => names.Title(game);

    /// <summary>Назва гри як у лобі («Мотоцикли», не «tron»).</summary>
    public string GameTitle(string game) => names.Title(game);

    static readonly Regex Noise = new(@"\s*[\(\[][^\)\]]*(official|video|audio|lyric|remaster|visualizer|mv|кліп|prod)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Назва треку вголос: без «(Official Video)» і лапок, щоб Глек не зачитував ютубівські хвости.</summary>
    public static string Track(string title)
    {
        var t = Noise.Replace(title, "").Replace("\"", "").Replace("«", "").Replace("»", "").Trim();
        return t.Length > 60 ? t[..60].TrimEnd() : t;
    }

    static bool Account(SqliteConnection c, string nick) =>
        Scalar(c, "SELECT COUNT(*) FROM accounts WHERE nick_key = $k", ("$k", Auth.NickKey(nick))) > 0;

    static SqliteCommand Cmd(SqliteConnection c, string sql, (string Name, object? Value)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    static long Scalar(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = Cmd(c, sql, ps);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    static List<T> Rows<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] ps)
    {
        using var cmd = Cmd(c, sql, ps);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }
}
