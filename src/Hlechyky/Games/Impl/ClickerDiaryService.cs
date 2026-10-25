using System.Globalization;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>Налаштування щоденника гончаря (<c>Clicker:Diary</c>, наживо).</summary>
public sealed class ClickerDiaryOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Як часто під час гри оновлювати знімок дня (хвилини). Виходячи з кола — одразу.</summary>
    public double SnapshotMinutes { get; set; } = 5;
    public TimeSpan SnapshotEvery => TimeSpan.FromMinutes(Math.Clamp(SnapshotMinutes, 0.5, 120));
}

/// <summary>Рядок щоденника: знімок гончаря на кінець дня гри (за Києвом).</summary>
public sealed record ClickerDiaryRow(string Day, string Nick, DateTimeOffset At, string Json, bool Restored);

/// <summary>Година гри: скільки секунд біля кола, скільки наліплено, спіймано з полиці й клацнуто.</summary>
public sealed record ClickerHourRow(string Day, int Hour, int Seconds, long Formed, long Grabbed, long Clicks);

/// <summary>
/// Щоденник гончаря (аналітика кола, план D:/or-wt/_tools/clicker-analytics/PLAN.md, етап 1). Сама гра історії не пише:
/// у збереженні лише «зараз», тож криві «за весь час» 25.10 довелось відновлювати з копій бази. Тепер кожен гончар має
/// <b>один знімок на київський день</b> (<c>clicker_diary</c>: оновлюється під час гри, у кінці дня в ньому стан на останню
/// гру) і <b>години</b> (<c>clicker_hours</c>: секунди біля кола, вироби, глеки з полиці, кліки — для «хто коли грає»).
///
/// Без гравця рядків нема: офлайн нараховується на першій синхронізації, тож дні без гри просто порожні. Як і «Майстер
/// дня»: пам'ять — лише те, що ще не записано; база — фоном раз на 5 с. Знімок замінює денний рядок цілком (і
/// відновлений, <c>restored</c>), години додаються.
/// </summary>
public sealed class ClickerDiaryService : BackgroundService
{
    public const string Schema = """
        CREATE TABLE IF NOT EXISTS clicker_diary(
            day TEXT NOT NULL, nick_key TEXT NOT NULL, nick TEXT NOT NULL, at TEXT NOT NULL, json TEXT NOT NULL,
            restored INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(day, nick_key));
        CREATE TABLE IF NOT EXISTS clicker_hours(
            nick_key TEXT NOT NULL, day TEXT NOT NULL, hour INTEGER NOT NULL,
            seconds INTEGER NOT NULL DEFAULT 0, formed INTEGER NOT NULL DEFAULT 0, grabbed INTEGER NOT NULL DEFAULT 0,
            clicks INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(nick_key, day, hour));
        """;

    public static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(5);
    static readonly TimeSpan PeopleEvery = TimeSpan.FromMinutes(5);

    sealed class Hour
    {
        public double Seconds;
        public long Formed, Grabbed, Clicks;
    }

    readonly Db? _db;
    readonly Func<ClickerDiaryOptions> _opts;
    readonly IClock _clock;
    readonly ILogger<ClickerDiaryService>? _log;
    readonly Func<string, bool>? _person;
    readonly object _gate = new();
    /// <summary>Незаписані знімки: (день, ключ) → рядок. Новіший того самого дня просто заміняє старий.</summary>
    readonly Dictionary<(string Day, string Key), ClickerDiaryRow> _snaps = [];
    /// <summary>Незаписані прирости годин: (ключ, день, година) → прирости. Дробові секунди чекають наступного запису.</summary>
    readonly Dictionary<(string Key, string Day, int Hour), Hour> _hours = [];
    volatile Bots.Set? _people;
    DateTimeOffset _peopleAt;

    /// <param name="person">Хто людина (тести). null — акаунти без ботів із бази; без бази — усі, крім гостей і «🤖».</param>
    public ClickerDiaryService(Db? db, Func<ClickerDiaryOptions> options, IClock? clock = null,
        ILogger<ClickerDiaryService>? log = null, Func<string, bool>? person = null)
    {
        _db = db;
        _opts = options;
        _clock = clock ?? new SystemClock();
        _log = log;
        _person = person;
        try { _db?.Exec(Schema); }
        catch (Exception ex) { _log?.LogWarning(ex, "щоденник гончаря: не створив таблиці"); }
    }

    public ClickerDiaryOptions O => _opts();

    public bool Enabled => O.Enabled;

    // ---------- люди ----------

    public bool IsPerson(string nick)
    {
        if (_person is not null) return _person(nick);
        if (_people is { } set) return !set.Has(nick);
        return !Bots.Looks(nick) && !Auth.IsGuestNick(nick) && Auth.NickKey(nick) != Auth.Guest;
    }

    void RefreshPeople(DateTimeOffset now)
    {
        if (_db is null || _person is not null || now - _peopleAt < PeopleEvery) return;
        _peopleAt = now;
        try { _people = Bots.Load(_db); }
        catch (Exception ex) { _log?.LogWarning(ex, "щоденник гончаря: не прочитав акаунти й ботів"); }
    }

    // ---------- запис (під замком кімнати гончаря: лише пам'ять) ----------

    /// <summary>Знімок дня <paramref name="day"/>: замінює попередній того самого дня (і відновлений).</summary>
    public void Put(string nick, string day, DateTimeOffset at, string json)
    {
        if (!O.Enabled || !IsPerson(nick)) return;
        var key = Auth.NickKey(nick);
        if (key.Length == 0 || day.Length == 0) return;
        lock (_gate) _snaps[(day, key)] = new ClickerDiaryRow(day, nick, at, json, false);
    }

    /// <summary>Приріст години гри: <paramref name="at"/> — коли (година за Києвом береться з нього).</summary>
    public void AddHour(string nick, DateTimeOffset at, double seconds, long formed, long grabbed, long clicks)
    {
        if (!O.Enabled || !IsPerson(nick)) return;
        if (!(seconds > 0) && formed <= 0 && grabbed <= 0 && clicks <= 0) return;
        var key = Auth.NickKey(nick);
        if (key.Length == 0) return;
        var local = TimeZoneInfo.ConvertTime(at, Days.Kyiv);
        var slot = (key, local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), local.Hour);
        lock (_gate)
        {
            if (!_hours.TryGetValue(slot, out var h)) _hours[slot] = h = new Hour();
            h.Seconds += Math.Max(0, seconds);
            h.Formed += Math.Max(0, formed);
            h.Grabbed += Math.Max(0, grabbed);
            h.Clicks += Math.Max(0, clicks);
        }
    }

    // ---------- читання ----------

    /// <summary>Щоденник гончаря від старих днів до нових (і ще не записане з пам'яті).</summary>
    public List<ClickerDiaryRow> Diary(string nick)
    {
        var key = Auth.NickKey(nick);
        var rows = new Dictionary<string, ClickerDiaryRow>(StringComparer.Ordinal);
        _db?.With(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT day, nick, at, json, restored FROM clicker_diary WHERE nick_key = $k ORDER BY day";
            cmd.Parameters.AddWithValue("$k", key);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows[r.GetString(0)] = new ClickerDiaryRow(r.GetString(0), r.GetString(1), ParseAt(r.GetString(2)), r.GetString(3), r.GetInt64(4) != 0);
        });
        lock (_gate)
            foreach (var s in _snaps.Values.Where(s => Auth.NickKey(s.Nick) == key)) rows[s.Day] = s;
        return [.. rows.Values.OrderBy(r => r.Day, StringComparer.Ordinal)];
    }

    /// <summary>Години гончаря (записані плюс ще не записані), від старих до нових.</summary>
    public List<ClickerHourRow> Hours(string nick)
    {
        var key = Auth.NickKey(nick);
        var sum = new Dictionary<(string Day, int Hour), (double S, long F, long G, long C)>();
        _db?.With(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT day, hour, seconds, formed, grabbed, clicks FROM clicker_hours WHERE nick_key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            using var r = cmd.ExecuteReader();
            while (r.Read()) sum[(r.GetString(0), r.GetInt32(1))] = (r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5));
        });
        lock (_gate)
            foreach (var (k, h) in _hours.Where(x => x.Key.Key == key))
            {
                var o = sum.GetValueOrDefault((k.Day, k.Hour));
                sum[(k.Day, k.Hour)] = (o.S + Math.Floor(h.Seconds), o.F + h.Formed, o.G + h.Grabbed, o.C + h.Clicks);
            }
        return [.. sum.OrderBy(x => x.Key.Day, StringComparer.Ordinal).ThenBy(x => x.Key.Hour)
            .Select(x => new ClickerHourRow(x.Key.Day, x.Key.Hour, (int)x.Value.S, x.Value.F, x.Value.G, x.Value.C))];
    }

    static DateTimeOffset ParseAt(string s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : default;

    // ---------- база ----------

    public void FlushNow()
    {
        List<ClickerDiaryRow> snaps;
        List<((string Key, string Day, int Hour) Slot, long Seconds, long Formed, long Grabbed, long Clicks)> hours;
        lock (_gate)
        {
            if (_db is null) { _snaps.Clear(); _hours.Clear(); return; }
            snaps = [.. _snaps.Values];
            _snaps.Clear();
            hours = [];
            foreach (var (slot, h) in _hours.ToList())
            {
                var whole = (long)Math.Floor(h.Seconds);
                if (whole == 0 && h.Formed == 0 && h.Grabbed == 0 && h.Clicks == 0) continue;
                hours.Add((slot, whole, h.Formed, h.Grabbed, h.Clicks));
                h.Seconds -= whole;
                h.Formed = h.Grabbed = h.Clicks = 0;
                if (h.Seconds <= 0) _hours.Remove(slot);
            }
        }
        if (snaps.Count == 0 && hours.Count == 0) return;
        try
        {
            _db.With(c =>
            {
                using var tx = c.BeginTransaction();
                foreach (var s in snaps)
                {
                    using var cmd = c.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT OR REPLACE INTO clicker_diary(day, nick_key, nick, at, json, restored) VALUES($d, $k, $n, $t, $j, 0)";
                    cmd.Parameters.AddWithValue("$d", s.Day);
                    cmd.Parameters.AddWithValue("$k", Auth.NickKey(s.Nick));
                    cmd.Parameters.AddWithValue("$n", s.Nick);
                    cmd.Parameters.AddWithValue("$t", s.At.UtcDateTime.ToString("o"));
                    cmd.Parameters.AddWithValue("$j", s.Json);
                    cmd.ExecuteNonQuery();
                }
                foreach (var h in hours)
                {
                    using var cmd = c.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT INTO clicker_hours(nick_key, day, hour, seconds, formed, grabbed, clicks) VALUES($k, $d, $h, $s, $f, $g, $c)
                        ON CONFLICT(nick_key, day, hour) DO UPDATE SET seconds = seconds + excluded.seconds,
                            formed = formed + excluded.formed, grabbed = grabbed + excluded.grabbed, clicks = clicks + excluded.clicks
                        """;
                    cmd.Parameters.AddWithValue("$k", h.Slot.Key);
                    cmd.Parameters.AddWithValue("$d", h.Slot.Day);
                    cmd.Parameters.AddWithValue("$h", h.Slot.Hour);
                    cmd.Parameters.AddWithValue("$s", h.Seconds);
                    cmd.Parameters.AddWithValue("$f", h.Formed);
                    cmd.Parameters.AddWithValue("$g", h.Grabbed);
                    cmd.Parameters.AddWithValue("$c", h.Clicks);
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            });
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "щоденник гончаря: не записав — повторю");
            lock (_gate)
            {
                // Новіший знімок, що встиг прийти, лишається; старий вертається лише туди, де порожньо.
                foreach (var s in snaps) _snaps.TryAdd((s.Day, Auth.NickKey(s.Nick)), s);
                foreach (var h in hours)
                {
                    if (!_hours.TryGetValue(h.Slot, out var cur)) _hours[h.Slot] = cur = new Hour();
                    cur.Seconds += h.Seconds;
                    cur.Formed += h.Formed;
                    cur.Grabbed += h.Grabbed;
                    cur.Clicks += h.Clicks;
                }
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        RefreshPeople(_clock.UtcNow);
        using var timer = new PeriodicTimer(FlushEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                FlushNow();
                RefreshPeople(_clock.UtcNow);
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

/// <summary>Підключення: опції <c>Clicker:Diary</c> і сервіс.</summary>
public static class ClickerDiarySetup
{
    public static IServiceCollection Add(IServiceCollection services)
    {
        services.AddOptions<ClickerDiaryOptions>().BindConfiguration("Clicker:Diary");
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptionsMonitor<ClickerDiaryOptions>>();
            return new ClickerDiaryService(sp.GetService<Db>(), () => opts.CurrentValue, sp.GetService<IClock>(),
                sp.GetService<ILogger<ClickerDiaryService>>());
        });
        services.AddHostedService(sp => sp.GetRequiredService<ClickerDiaryService>());
        return services;
    }
}
