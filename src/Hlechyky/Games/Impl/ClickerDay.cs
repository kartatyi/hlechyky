using System.Globalization;
using Hlechyky.Games.Economy;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>Налаштування «Майстра дня» (<c>Clicker:Day</c>, наживо).</summary>
public sealed class ClickerDayOptions
{
    public bool Enabled { get; set; } = true;
    public int[]? Prizes { get; set; }
    /// <summary>Скільки людей мусить набрати <see cref="MinPoints"/>, щоб день платив.</summary>
    public int MinPlayers { get; set; } = 2;
    public int MinPoints { get; set; } = 30;

    /// <summary>Рішення користувача 21.10: 200 / 100 / 50 черепків.</summary>
    public static readonly int[] DefaultPrizes = [200, 100, 50];
    public int[] PrizeList => Prizes is { Length: > 0 } p ? p : DefaultPrizes;
}

/// <summary>Рядок табло дня.</summary>
public sealed record ClickerDayRow(int Place, string Nick, int Points);

/// <summary>
/// «Майстер дня» (docs/games/specs/clicker-v12.md §5): щоденне змагання гончарів за виробами з ручного горна — дзвінкий 1,
/// розкішний 3. Доба — з 23:30 до 23:30 за Києвом; о 23:30 Дядько Глек платить призи першим трьом (ключ
/// <c>clicker-day:{день}:{місце}</c> — повтор не платить двічі) і пише рядок у Балачки. Сервер лежав — підбиває на старті.
///
/// Чому вироби, а не глеки: глеки ростуть на порядки, і хто попереду за щаблями, той попереду назавжди. Вироби з ручного
/// горна — реальний час і руки, однакові для всіх (палій і підмайстри працюють без гравця — їх у залік не беремо).
/// Пам'ять — істина; база — запис фоном (раз на 5 с), як у турнірі слотів.
/// </summary>
public sealed class ClickerDayService : BackgroundService
{
    public const string Schema = """
        CREATE TABLE IF NOT EXISTS clicker_day(
            day TEXT NOT NULL, nick_key TEXT NOT NULL, nick TEXT NOT NULL, points INTEGER NOT NULL, at TEXT NOT NULL,
            PRIMARY KEY(day, nick_key));
        CREATE TABLE IF NOT EXISTS clicker_day_paid(day TEXT PRIMARY KEY, at TEXT NOT NULL, summary TEXT NOT NULL, winner TEXT);
        """;

    /// <summary>Межа доби — 23:30 за Києвом (слово користувача 21.10: «кожен день в 23:30»).</summary>
    public static readonly TimeSpan Close = new(23, 30, 0);
    public static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(5), SettleEvery = TimeSpan.FromSeconds(30);
    /// <summary>Скільки рядків табло шле вид.</summary>
    public const int BoardShown = 10;

    sealed class Entry
    {
        public string Day = "", Key = "", Nick = "";
        public int Points;
        public DateTimeOffset At;
    }

    readonly Db? _db;
    readonly IStakes? _stakes;
    readonly Func<ClickerDayOptions> _opts;
    readonly IClock _clock;
    readonly ISlotsWire? _wire;
    readonly Achievements? _ach;
    readonly ILogger<ClickerDayService>? _log;
    readonly Func<string, bool>? _person;
    readonly object _gate = new();
    readonly Dictionary<(string Day, string Key), Entry> _entries = [];
    readonly HashSet<(string Day, string Key)> _dirty = [];
    readonly HashSet<string> _paid = new(StringComparer.Ordinal);
    /// <summary>Скільки разів кожен був першим (ключ ніка) — на ачівку «Тиждень на вершині».</summary>
    readonly Dictionary<string, int> _wins = new(StringComparer.Ordinal);
    /// <summary>Останній підбитий день: рядок підсумку для виду («учора: 1. …»).</summary>
    (string Day, string Summary)? _last;
    volatile Bots.Set? _people;

    /// <param name="person">Хто людина (тести). null — акаунти без ботів із бази; без бази — усі, крім гостей і «🤖».</param>
    public ClickerDayService(Db? db, IStakes? stakes, Func<ClickerDayOptions> options, IClock? clock = null, ISlotsWire? wire = null,
        Achievements? achievements = null, ILogger<ClickerDayService>? log = null, Func<string, bool>? person = null)
    {
        _db = db;
        _stakes = stakes;
        _opts = options;
        _clock = clock ?? new SystemClock();
        _wire = wire;
        _ach = achievements;
        _log = log;
        _person = person;
        try { Load(); }
        catch (Exception ex) { _log?.LogWarning(ex, "майстер дня: не прочитав очки — починаю з чистого"); }
    }

    public ClickerDayOptions O => _opts();

    // ---------- доба ----------

    /// <summary>День, що закінчується найближчої 23:30 за Києвом: після 23:30 — уже завтрашній.</summary>
    public static string DayOf(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, Days.Kyiv);
        var date = DateOnly.FromDateTime(local.DateTime);
        if (local.TimeOfDay >= Close) date = date.AddDays(1);
        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>Коли день закінчується (UTC): 23:30 його дати за Києвом.</summary>
    public static DateTimeOffset Ends(string day)
    {
        var date = DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var local = date.ToDateTime(TimeOnly.FromTimeSpan(Close));
        if (Days.Kyiv.IsInvalidTime(local)) local = local.AddHours(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, Days.Kyiv), TimeSpan.Zero);
    }

    // ---------- люди ----------

    bool IsPerson(string nick)
    {
        if (_person is not null) return _person(nick);
        if (_people is { } set) return !set.Has(nick);
        return !Bots.Looks(nick) && !Auth.IsGuestNick(nick) && Auth.NickKey(nick) != Auth.Guest;
    }

    void RefreshPeople()
    {
        if (_db is null || _person is not null) return;
        try { _people = Bots.Load(_db); }
        catch (Exception ex) { _log?.LogWarning(ex, "майстер дня: не прочитав акаунти й ботів"); }
    }

    // ---------- очки (під замком кімнати гончаря: лише пам'ять) ----------

    /// <summary>Очки за партію з ручного горна. Гості й боти — не в табло (гратимуть, але очок не мають).</summary>
    public void Add(string nick, int points, DateTimeOffset now)
    {
        if (points <= 0 || !O.Enabled || !IsPerson(nick)) return;
        var key = Auth.NickKey(nick);
        if (key.Length == 0) return;
        var day = DayOf(now);
        lock (_gate)
        {
            if (_paid.Contains(day)) return;
            if (!_entries.TryGetValue((day, key), out var e)) _entries[(day, key)] = e = new Entry { Day = day, Key = key };
            e.Nick = nick;
            e.Points += points;
            // Рівні — хто раніше набрав своє: час останнього приросту.
            e.At = now;
            _dirty.Add((day, key));
        }
    }

    List<ClickerDayRow> BoardLocked(string day) =>
        [.. _entries.Values.Where(e => e.Day == day && e.Points > 0).OrderByDescending(e => e.Points).ThenBy(e => e.At)
            .ThenBy(e => e.Key, StringComparer.Ordinal).Select((e, i) => new ClickerDayRow(i + 1, e.Nick, e.Points))];

    public List<ClickerDayRow> Board(string day) { lock (_gate) return BoardLocked(day); }

    public int WinsOf(string nick) { lock (_gate) return _wins.GetValueOrDefault(Auth.NickKey(nick)); }

    /// <summary>Вид для кола: день, до коли, призи, моє місце й очки, табло (перші десять), учорашній підсумок.</summary>
    public object? Wire(string nick, DateTimeOffset now)
    {
        var o = O;
        if (!o.Enabled) return null;
        var day = DayOf(now);
        var key = Auth.NickKey(nick);
        lock (_gate)
        {
            var board = BoardLocked(day);
            var mine = board.FirstOrDefault(r => Auth.NickKey(r.Nick) == key);
            return new
            {
                day,
                ends = Ends(day),
                prizes = o.PrizeList,
                minPoints = o.MinPoints,
                minPlayers = o.MinPlayers,
                person = IsPerson(nick),
                mine = mine is null ? null : new { place = mine.Place, points = mine.Points },
                board = board.Take(BoardShown).Select(r => new { place = r.Place, nick = r.Nick, points = r.Points, me = Auth.NickKey(r.Nick) == key }),
                players = board.Count,
                last = _last is { } l ? new { day = l.Day, line = l.Summary } : null,
            };
        }
    }

    // ---------- підбиття ----------

    /// <summary>
    /// Підбити всі дні, що вже закінчились і ще не оплачені: призи першим (якщо людей з порогом очок досить), рядок у
    /// Балачки, ачівки переможцеві. Повертає, скільки днів підбито.
    /// </summary>
    public int Settle(DateTimeOffset now)
    {
        List<string> days;
        lock (_gate)
            days = [.. _entries.Values.Select(e => e.Day).Distinct().Where(d => !_paid.Contains(d) && Ends(d) <= now).Order(StringComparer.Ordinal)];
        var done = 0;
        foreach (var day in days)
        {
            var o = O;
            List<ClickerDayRow> board;
            lock (_gate) board = BoardLocked(day);
            var eligible = board.Where(r => r.Points >= o.MinPoints).ToList();
            var prizes = o.PrizeList;
            var winners = eligible.Count >= Math.Max(1, o.MinPlayers) ? eligible.Take(prizes.Length).ToList() : [];
            try
            {
                for (var i = 0; i < winners.Count; i++)
                    if (prizes[i] > 0 && _stakes is not null)
                        _stakes.Grant(winners[i].Nick, prizes[i], $"clicker-day:{day}", $"clicker-day:{day}:{i + 1}");
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "майстер дня: призи дня {Day} не пройшли — спробую згодом", day);
                continue;
            }
            var summary = winners.Count > 0 ? Line(winners) : board.Count > 0 ? $"🏆 Майстер дня: без призів — порогу {o.MinPoints} очок досягли {eligible.Count}" : "";
            var winner = winners.Count > 0 ? winners[0].Nick : null;
            lock (_gate)
            {
                _paid.Add(day);
                if (winner is not null) _wins[Auth.NickKey(winner)] = _wins.GetValueOrDefault(Auth.NickKey(winner)) + 1;
                if (summary.Length > 0) _last = (day, summary);
            }
            try
            {
                _db?.Exec("INSERT OR IGNORE INTO clicker_day_paid(day, at, summary, winner) VALUES($d, $t, $s, $w)",
                    ("$d", day), ("$t", now.UtcDateTime.ToString("o")), ("$s", summary), ("$w", winner));
            }
            catch (Exception ex) { _log?.LogWarning(ex, "майстер дня: не записав оплату дня {Day}", day); }
            done++;
            if (winner is not null)
            {
                try
                {
                    _ach?.Unlock(winner, "potter-day-win");
                    if (WinsOf(winner) >= 7) _ach?.Unlock(winner, "potter-day-7");
                }
                catch (Exception ex) { _log?.LogWarning(ex, "майстер дня: ачівки не пройшли"); }
            }
            if (winners.Count > 0 && _wire is not null)
            {
                try { _wire.Chat(summary); }
                catch (Exception ex) { _log?.LogWarning(ex, "майстер дня: рядок у Балачки не пішов"); }
            }
            _log?.LogInformation("майстер дня: {Day} підбито — {Summary}", day, summary);
        }
        if (done > 0) Prune(now);
        return done;
    }

    static string Line(List<ClickerDayRow> winners) =>
        "🏆 Майстер дня в Гончарному колі: " + string.Join(" · ", winners.Select(w => $"{w.Place}. {w.Nick} {w.Points}"))
        + " — призи від Дядька Глека";

    /// <summary>Оплачені минулі дні з пам'яті геть (у базі лишаються). Незаписане спершу пишемо.</summary>
    void Prune(DateTimeOffset now)
    {
        FlushNow();
        var today = DayOf(now);
        lock (_gate)
            foreach (var k in _entries.Keys.Where(k => _paid.Contains(k.Day) && string.CompareOrdinal(k.Day, today) < 0).ToList())
                _entries.Remove(k);
    }

    // ---------- база ----------

    void Load()
    {
        if (_db is null) return;
        _db.Exec(Schema);
        _db.With(c =>
        {
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT day, summary, winner FROM clicker_day_paid ORDER BY day";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var day = r.GetString(0);
                    _paid.Add(day);
                    if (!r.IsDBNull(1) && r.GetString(1).Length > 0) _last = (day, r.GetString(1));
                    if (!r.IsDBNull(2)) { var k = Auth.NickKey(r.GetString(2)); if (k.Length > 0) _wins[k] = _wins.GetValueOrDefault(k) + 1; }
                }
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT day, nick_key, nick, points, at FROM clicker_day WHERE day NOT IN (SELECT day FROM clicker_day_paid)";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var e = new Entry
                    {
                        Day = r.GetString(0), Key = r.GetString(1), Nick = r.GetString(2), Points = r.GetInt32(3),
                        At = DateTimeOffset.TryParse(r.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : default,
                    };
                    _entries[(e.Day, e.Key)] = e;
                }
            }
        });
    }

    public void FlushNow()
    {
        if (_db is null) { lock (_gate) _dirty.Clear(); return; }
        List<(string Day, string Key, string Nick, int Points, string At)> snap;
        lock (_gate)
        {
            if (_dirty.Count == 0) return;
            snap = [.. _dirty.Where(_entries.ContainsKey).Select(k => _entries[k]).Select(e => (e.Day, e.Key, e.Nick, e.Points, e.At.UtcDateTime.ToString("o")))];
            _dirty.Clear();
        }
        try
        {
            _db.With(c =>
            {
                using var tx = c.BeginTransaction();
                foreach (var e in snap)
                {
                    using var cmd = c.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT OR REPLACE INTO clicker_day(day, nick_key, nick, points, at) VALUES($d, $k, $n, $p, $t)";
                    cmd.Parameters.AddWithValue("$d", e.Day);
                    cmd.Parameters.AddWithValue("$k", e.Key);
                    cmd.Parameters.AddWithValue("$n", e.Nick);
                    cmd.Parameters.AddWithValue("$p", e.Points);
                    cmd.Parameters.AddWithValue("$t", e.At);
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            });
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "майстер дня: не записав очки — повторю");
            lock (_gate) foreach (var e in snap) _dirty.Add((e.Day, e.Key));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        RefreshPeople();
        try { Settle(_clock.UtcNow); }
        catch (Exception ex) { _log?.LogWarning(ex, "майстер дня: підбиття на старті не вдалось"); }
        var last = _clock.UtcNow;
        using var timer = new PeriodicTimer(FlushEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                FlushNow();
                if (_clock.UtcNow - last < SettleEvery) continue;
                last = _clock.UtcNow;
                RefreshPeople();
                try { Settle(last); }
                catch (Exception ex) { _log?.LogWarning(ex, "майстер дня: підбиття не вдалось"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        FlushNow();
    }
}

/// <summary>Підключення: опції <c>Clicker:Day</c> і сервіс.</summary>
public static class ClickerDaySetup
{
    public static IServiceCollection Add(IServiceCollection services)
    {
        services.AddOptions<ClickerDayOptions>().BindConfiguration("Clicker:Day");
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptionsMonitor<ClickerDayOptions>>();
            return new ClickerDayService(sp.GetService<Db>(), sp.GetService<IStakes>(), () => opts.CurrentValue, sp.GetService<IClock>(),
                sp.GetService<ISlotsWire>(), sp.GetService<Achievements>(), sp.GetService<ILogger<ClickerDayService>>());
        });
        services.AddHostedService(sp => sp.GetRequiredService<ClickerDayService>());
        return services;
    }
}
