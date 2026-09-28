using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Hlechyky.Games.Economy;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Забіг дня, прохід №3: «👻 привиди друзів» (найкраща спроба кожного за сьогодні — журнал вводу, який клієнт
/// проганяє на своїй копії траси) і «🏆 кубок тижня» (сума трьох найкращих днів тижня, рядок у Журнал у неділю ввечері).
/// </summary>
public static class DinoDailySetup
{
    public const string Game = "dino-daily";

    public static IServiceCollection AddDinoDaily(this IServiceCollection services)
    {
        services.AddSingleton(sp => new DinoGhosts(sp.GetService<Db>()));
        services.AddHostedService(sp => new DinoCupPoster(sp.GetService<Db>(), sp.GetRequiredService<IOutbox>(),
            sp.GetRequiredService<IClock>(), sp.GetService<ILogger<DinoCupPoster>>()));
        return services;
    }

    public static WebApplication MapDinoDaily(this WebApplication app)
    {
        // Кубок тижня: з понеділка по неділю за Києвом, сума трьох найкращих днів кожного.
        app.MapGet("/api/games/dino-daily/cup", (Db db, IClock clock) =>
        {
            var week = DinoCup.WeekStart(Days.Today(clock));
            return Results.Json(new { week, rows = DinoCup.Of(DinoCup.Read(db, week), 10) });
        });
        return app;
    }
}

// ---------------------------------------------------------------------------------------------
// Привиди друзів
// ---------------------------------------------------------------------------------------------

/// <summary>Найкраща спроба ніка за день: метри й журнал вводу «крок, клавіші» (стиснутий <see cref="DinoGhosts.Encode"/>).</summary>
public sealed record DinoGhost(string Key, string Nick, int Metres, string Log);

/// <summary>
/// Найкращі спроби дня кожного ніка. Кімната звертається лише до пам'яті; перевірка журналу (прогін траси з нуля —
/// кілька мілісекунд) і база — фоном. Журнал, що не дає тих самих метрів, привидом не стає.
/// </summary>
public sealed class DinoGhosts(Db? db)
{
    /// <summary>Скільки вводів журналу максимум (вдвічі більше чисел): довші спроби привидом не стають — на вид не влізуть.</summary>
    public const int MaxInputs = 3000;
    /// <summary>Скільки привидів друзів біжить поруч.</summary>
    public const int Near = 3;

    const string Schema = """
        CREATE TABLE IF NOT EXISTS dino_ghost(
            day TEXT NOT NULL, nick_key TEXT NOT NULL, nick TEXT NOT NULL, metres INTEGER NOT NULL,
            log TEXT NOT NULL, created_at TEXT NOT NULL, PRIMARY KEY(day, nick_key)) WITHOUT ROWID;
        """;

    sealed class DaySet
    {
        public readonly Dictionary<string, DinoGhost> Best = new(StringComparer.Ordinal);
        public int Ver;
    }

    readonly ConcurrentDictionary<string, DaySet> _days = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, bool> _loaded = new(StringComparer.Ordinal);
    bool _schema;

    DaySet Set(string day) => _days.GetOrAdd(day, _ => new DaySet());

    /// <summary>Прогріти день (не чекає на базу); вчорашні дні з пам'яті прибрати.</summary>
    public void Warm(string day)
    {
        if (!_loaded.TryAdd(day, true)) return;
        foreach (var old in _days.Keys.Where(d => string.CompareOrdinal(d, day) < 0).ToList()) _days.TryRemove(old, out _);
        if (db is not null) _ = Task.Run(() => Load(day));
    }

    /// <summary>Версія дня: міняється з кожним новим привидом (щоб кімната знала, коли перечитати).</summary>
    public int Version(string day) => _days.TryGetValue(day, out var s) ? s.Ver : 0;

    /// <summary>
    /// Хто біжить поруч: до <see cref="Near"/> чужих привидів — спершу ті, хто трохи попереду твого рекорду (їх і
    /// обганяти), далі найближчі позаду. Плюс свій (null — ще нема).
    /// </summary>
    public (List<DinoGhost> Friends, DinoGhost? Mine) Pick(string day, string myKey, int myBest)
    {
        Warm(day);
        var set = Set(day);
        var ahead = new List<DinoGhost>();
        var behind = new List<DinoGhost>();
        DinoGhost? mine = null;
        lock (set)
            foreach (var g in set.Best.Values)
            {
                if (g.Key == myKey) { mine = g; continue; }
                (g.Metres > myBest ? ahead : behind).Add(g);
            }
        ahead.Sort((a, b) => a.Metres.CompareTo(b.Metres));
        behind.Sort((a, b) => b.Metres.CompareTo(a.Metres));
        var list = new List<DinoGhost>(Near);
        foreach (var g in ahead) { if (list.Count == Near) break; list.Add(g); }
        foreach (var g in behind) { if (list.Count == Near) break; list.Add(g); }
        return (list, mine);
    }

    /// <summary>Кінець спроби: журнал перевіряємо фоном і, якщо це найкраще за день у ніка, запам'ятовуємо.</summary>
    public void Offer(string day, int seed, string key, string nick, int metres, int[] log, int n)
    {
        if (n == 0 || n > MaxInputs * 2 || metres <= 0) return;
        var set = Set(day);
        lock (set)
            if (set.Best.TryGetValue(key, out var was) && was.Metres >= metres) return;
        var copy = log[..n];
        _ = Task.Run(() => Store(day, seed, key, nick, metres, copy));
    }

    /// <summary>Перевірити й покласти (тести кличуть напряму, щоб не чекати фону).</summary>
    public bool Store(string day, int seed, string key, string nick, int metres, int[] log)
    {
        try
        {
            if (Replay(seed, log) != metres) return false;
            var g = new DinoGhost(key, nick, metres, Encode(log));
            var set = Set(day);
            lock (set)
            {
                if (set.Best.TryGetValue(key, out var was) && was.Metres >= metres) return false;
                set.Best[key] = g;
                set.Ver++;
            }
            if (db is not null) Save(day, g);
            return true;
        }
        catch (Exception) { return false; /* привид — забава: не вийшло, то й не біда */ }
    }

    /// <summary>Прогнати журнал на свіжій трасі дня: скільки метрів до лавини (−1 — не наздогнала, журнал не той).</summary>
    public static int Replay(int seed, int[] log)
    {
        var sim = new RunnerSim(RunnerMode.Dino, seed, [true], 0, DinoDaily.PmCapDaily, snowOn: false);
        var i = 0;
        for (var guard = 0; guard < 200_000 && !sim.P[0].Out; guard++)
        {
            var t = sim.S;
            while (i + 1 < log.Length && log[i] <= t) { if (log[i] == t) sim.Input(0, t, log[i + 1]); i += 2; }
            sim.Step();
            sim.ClearEvents();
        }
        return sim.P[0].Out ? Dino.Metres(sim, sim.S - 1) : -1;
    }

    /// <summary>
    /// Журнал [крок, клавіші, …] (кроки не спадають) → «Δкрок у base36 + цифра клавіш» через крапку: «04.1b1.a5».
    /// Клієнт розгортає назад (runner.js, ghostDecode).
    /// </summary>
    public static string Encode(int[] log)
    {
        var sb = new StringBuilder(log.Length * 2);
        var prev = 0;
        for (var i = 0; i + 1 < log.Length; i += 2)
        {
            if (i > 0) sb.Append('.');
            var d = log[i] - prev;
            prev = log[i];
            sb.Append(Base36(d)).Append((char)('0' + (log[i + 1] & 7)));
        }
        return sb.ToString();
    }

    public static int[] Decode(string s)
    {
        if (string.IsNullOrEmpty(s)) return [];
        var parts = s.Split('.');
        var log = new int[parts.Length * 2];
        var at = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            at += p.Length > 1 ? Convert.ToInt32(FromBase36(p[..^1])) : 0;
            log[i * 2] = at;
            log[i * 2 + 1] = p[^1] - '0';
        }
        return log;
    }

    static string Base36(int v)
    {
        if (v <= 0) return "0";
        Span<char> buf = stackalloc char[8];
        var n = 0;
        while (v > 0) { var d = v % 36; buf[n++] = (char)(d < 10 ? '0' + d : 'a' + d - 10); v /= 36; }
        var s = new char[n];
        for (var i = 0; i < n; i++) s[i] = buf[n - 1 - i];
        return new string(s);
    }

    static long FromBase36(string s)
    {
        long v = 0;
        foreach (var c in s) v = v * 36 + (c <= '9' ? c - '0' : c - 'a' + 10);
        return v;
    }

    void Save(string day, DinoGhost g)
    {
        db!.With(c =>
        {
            Ensure(c);
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO dino_ghost(day, nick_key, nick, metres, log, created_at) VALUES($d, $k, $n, $m, $l, $t)
                ON CONFLICT(day, nick_key) DO UPDATE SET nick = $n, metres = $m, log = $l, created_at = $t WHERE $m > metres
                """;
            cmd.Parameters.AddWithValue("$d", day);
            cmd.Parameters.AddWithValue("$k", g.Key);
            cmd.Parameters.AddWithValue("$n", g.Nick);
            cmd.Parameters.AddWithValue("$m", g.Metres);
            cmd.Parameters.AddWithValue("$l", g.Log);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Лише для тестів: дочитати день із бази зараз.</summary>
    public void LoadNow(string day) { _loaded[day] = true; Load(day); }

    void Load(string day)
    {
        try
        {
            var rows = new List<DinoGhost>();
            db!.With(c =>
            {
                Ensure(c);
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT nick_key, nick, metres, log FROM dino_ghost WHERE day = $d";
                cmd.Parameters.AddWithValue("$d", day);
                using var r = cmd.ExecuteReader();
                while (r.Read()) rows.Add(new DinoGhost(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3)));
            });
            var set = Set(day);
            lock (set)
            {
                foreach (var g in rows)
                    if (!set.Best.TryGetValue(g.Key, out var was) || was.Metres < g.Metres) set.Best[g.Key] = g;
                if (rows.Count > 0) set.Ver++;
            }
        }
        catch (Exception) { _loaded.TryRemove(day, out _); }
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

// ---------------------------------------------------------------------------------------------
// Кубок тижня
// ---------------------------------------------------------------------------------------------

/// <summary>Рядок кубка: нік, сума трьох найкращих днів, ці дні (метри, за спаданням), скільки днів бігав.</summary>
public sealed record DinoCupRow(string Nick, int Total, int[] Best, int Days);

public static class DinoCup
{
    public const int TopDays = 3;

    /// <summary>Понеділок київського тижня дня day ("2026-09-28").</summary>
    public static string WeekStart(string day)
    {
        var d = DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var back = ((int)d.DayOfWeek + 6) % 7;   // пн = 0 … нд = 6
        return d.AddDays(-back).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>Спроби тижня з таблиці результатів: (нік-ключ, нік, метри, київський день).</summary>
    public static List<(string Key, string Nick, int Metres, string Day)> Read(Db db, string week)
    {
        var since = Daily.StartOfDayUtc(week);
        var until = Daily.StartOfDayUtc(DateOnly.ParseExact(week, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(7)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return db.With(c =>
        {
            using var cmd = c.CreateCommand();
            // найкраща спроба кожного за кожен день — групуємо вже в C#: день київський, а created_at — UTC
            cmd.CommandText = """
                SELECT nick_key, nick, score, created_at FROM game_results
                WHERE game = $g AND outcome = 'solo' AND created_at >= $a AND created_at < $b AND score IS NOT NULL
                ORDER BY id LIMIT 20000
                """;
            cmd.Parameters.AddWithValue("$g", DinoDailySetup.Game);
            cmd.Parameters.AddWithValue("$a", since.ToUniversalTime().ToString("o"));
            cmd.Parameters.AddWithValue("$b", until.ToUniversalTime().ToString("o"));
            using var r = cmd.ExecuteReader();
            var list = new List<(string, string, int, string)>();
            while (r.Read())
            {
                var at = DateTimeOffset.Parse(r.GetString(3), CultureInfo.InvariantCulture);
                list.Add((r.GetString(0), r.GetString(1), (int)r.GetDouble(2), Hlechyky.Games.Days.Of(at)));
            }
            return list;
        });
    }

    /// <summary>Таблиця кубка: у кожного — найкраще за кожен день, сума трьох найкращих днів; більша сума вище.</summary>
    public static List<DinoCupRow> Of(IEnumerable<(string Key, string Nick, int Metres, string Day)> runs, int top)
    {
        var best = new Dictionary<string, (string Nick, Dictionary<string, int> Days)>(StringComparer.Ordinal);
        foreach (var (key, nick, m, day) in runs)
        {
            if (!best.TryGetValue(key, out var e)) best[key] = e = (nick, new Dictionary<string, int>(StringComparer.Ordinal));
            else best[key] = e = (nick, e.Days);   // нік — останній, яким бігав
            e.Days[day] = Math.Max(e.Days.GetValueOrDefault(day), m);
        }
        return best.Values
            .Select(e =>
            {
                var days = e.Days.Values.OrderByDescending(v => v).Take(TopDays).ToArray();
                return new DinoCupRow(e.Nick, days.Sum(), days, e.Days.Count);
            })
            .Where(r => r.Total > 0)
            .OrderByDescending(r => r.Total).ThenBy(r => r.Nick, StringComparer.Ordinal)
            .Take(top).ToList();
    }

    /// <summary>Рядок у Журнал: «🏆 Кубок тижня Забігу дня: Оля — 7 420 м (три найкращі дні) · далі Петро 6 100 м, Микола 5 900 м».</summary>
    public static string? Line(IReadOnlyList<DinoCupRow> rows)
    {
        if (rows.Count == 0) return null;
        var w = rows[0];
        var s = $"🏆 Кубок тижня Забігу дня бере {w.Nick} — {Num(w.Total)} м за {Plural(w.Best.Length)}";
        if (rows.Count > 1)
            s += " · далі " + string.Join(", ", rows.Skip(1).Take(2).Select(r => $"{r.Nick} {Num(r.Total)} м"));
        return s + ". Новий тиждень — нова криза!";
    }

    static string Plural(int n) => n switch { 1 => "один день", 2 => "два найкращі дні", _ => "три найкращі дні" };

    public static string Num(int n) => n.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', ' ');
}

/// <summary>
/// Раз на тиждень, у неділю після 20:00 за Києвом, пише в Журнал переможця кубка. Що вже писали — пам'ятає в
/// таблиці <c>dino_cup</c> (перезапуск сервера рядок не дублює).
/// </summary>
public sealed class DinoCupPoster(Db? db, IOutbox outbox, IClock clock, ILogger<DinoCupPoster>? log) : BackgroundService
{
    public const int Hour = 20;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (db is null) return;
        while (!stop.IsCancellationRequested)
        {
            try { TryPost(); }
            catch (Exception e) { log?.LogWarning(e, "Кубок тижня Забігу дня не записався"); }
            try { await Task.Delay(TimeSpan.FromMinutes(5), stop); } catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>Неділя, ≥ 20:00, цього тижня ще не писали — пише. true — рядок пішов.</summary>
    public bool TryPost()
    {
        var local = TimeZoneInfo.ConvertTime(clock.UtcNow, Hlechyky.Games.Days.Kyiv);
        if (local.DayOfWeek != DayOfWeek.Sunday || local.Hour < Hour) return false;
        var week = DinoCup.WeekStart(Hlechyky.Games.Days.Today(clock));
        var line = DinoCup.Line(DinoCup.Of(DinoCup.Read(db!, week), 3));
        if (line is null) return false;
        var fresh = db!.With(c =>
        {
            using (var ddl = c.CreateCommand())
            {
                // окремою командою: sqlite3_changes() після CREATE лишає число з попередньої вставки
                ddl.CommandText = "CREATE TABLE IF NOT EXISTS dino_cup(week TEXT PRIMARY KEY, line TEXT NOT NULL, created_at TEXT NOT NULL) WITHOUT ROWID";
                ddl.ExecuteNonQuery();
            }
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO dino_cup(week, line, created_at) VALUES($w, $l, $t);
                """;
            cmd.Parameters.AddWithValue("$w", week);
            cmd.Parameters.AddWithValue("$l", line);
            cmd.Parameters.AddWithValue("$t", clock.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            return cmd.ExecuteNonQuery() > 0;
        });
        if (fresh) outbox.Post(new Journal(line));
        return fresh;
    }
}
