using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Блефер сезону» (specs/dice.md «Прохід №3»): смішні звання кожної дограної партії «Під глеком» — у базу, а звідти
/// — таблиця за тиждень/місяць/весь час у «📊 Хто скільки» і на картці гри. Запис — фоном (гра кличе
/// <see cref="Record"/> з-під замка кімнати, тож там лише черга в пам'яті).
/// </summary>
public sealed class DiceSeason : BackgroundService
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS dice_titles(at TEXT NOT NULL, kind TEXT NOT NULL, nick TEXT NOT NULL, nick_key TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS dice_titles_at ON dice_titles(at);
        """;

    readonly Db? _db;
    readonly ILogger<DiceSeason>? _log;
    readonly object _gate = new();
    /// <summary>Усі звання в пам'яті: їх мало (≤ 5 на партію), а таблицю питають частіше, ніж пишуть.</summary>
    readonly List<Row> _rows = [];
    readonly Channel<Row> _queue = Channel.CreateUnbounded<Row>();

    public sealed record Row(DateTimeOffset At, string Kind, string Nick, string Key);

    public DiceSeason(Db? db, ILogger<DiceSeason>? log = null)
    {
        _db = db;
        _log = log;
        if (_db is null) return;
        try
        {
            _db.With(c =>
            {
                using (var cmd = c.CreateCommand()) { cmd.CommandText = Schema; cmd.ExecuteNonQuery(); }
                using var q = c.CreateCommand();
                q.CommandText = "SELECT at, kind, nick, nick_key FROM dice_titles";
                using var r = q.ExecuteReader();
                while (r.Read())
                    _rows.Add(new Row(DateTimeOffset.Parse(r.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                        r.GetString(1), r.GetString(2), r.GetString(3)));
            });
        }
        catch (Exception ex) { _log?.LogWarning(ex, "dice: не прочитав звання з бази"); }
    }

    /// <summary>Звання однієї партії. Не блокує: у пам'ять і в чергу на запис.</summary>
    public void Record(IEnumerable<(string Kind, string Nick)> titles, DateTimeOffset at)
    {
        lock (_gate)
            foreach (var (kind, nick) in titles)
            {
                var row = new Row(at, kind, nick, Auth.NickKey(nick));
                _rows.Add(row);
                if (_db is not null) _queue.Writer.TryWrite(row);
            }
    }

    /// <summary>Таблиця сезону: на кожне звання — хто скільки разів його брав від <paramref name="since"/>.</summary>
    public object Table(DateTimeOffset since, int top = 5)
    {
        List<Row> rows;
        lock (_gate) rows = [.. _rows.Where(r => r.At >= since)];
        return DiceTitles.All.Select(t => new
        {
            kind = t.Kind, icon = t.Icon, title = t.Title,
            rows = rows.Where(r => r.Kind == t.Kind).GroupBy(r => r.Key)
                .Select(g => new { nick = g.Last().Nick, n = g.Count(), last = g.Max(r => r.At) })
                .OrderByDescending(x => x.n).ThenByDescending(x => x.last).Take(top)
                .Select(x => new { x.nick, x.n }).ToArray(),
        }).Where(t => t.rows.Length > 0).ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_db is null) return;
        try { while (await _queue.Reader.WaitToReadAsync(ct)) Flush(); }
        catch (OperationCanceledException) { }
        Flush();
    }

    void Flush()
    {
        while (_queue.Reader.TryRead(out var row))
        {
            try
            {
                _db!.With(c =>
                {
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "INSERT INTO dice_titles(at, kind, nick, nick_key) VALUES($at, $k, $n, $key)";
                    cmd.Parameters.AddWithValue("$at", row.At.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                    cmd.Parameters.AddWithValue("$k", row.Kind);
                    cmd.Parameters.AddWithValue("$n", row.Nick);
                    cmd.Parameters.AddWithValue("$key", row.Key);
                    cmd.ExecuteNonQuery();
                });
            }
            catch (Exception ex) { _log?.LogWarning(ex, "dice: звання не записалось"); }
        }
    }
}

/// <summary>
/// Ставки вболівальників «Під глеком»: <c>Rooms.Act</c> пускає лише тих, хто сидить, тож глядачі (і вибулі — для
/// одноманітності клієнта) ставлять через HTTP, як голос публіки в «Дотепах». Лише тим, хто стіл справді відкрив або
/// за ним сидить, і не частіше за двічі на секунду з ніка.
/// </summary>
public sealed class DiceFans(Rooms rooms, Presence presence, IClock clock)
{
    public const int PerSecond = 2;
    readonly object _lock = new();
    readonly Dictionary<string, Queue<DateTimeOffset>> _tries = new(StringComparer.Ordinal);

    public ActResult Bet(string? nick, string? roomId, bool truth)
    {
        if (string.IsNullOrWhiteSpace(nick) || Auth.NickKey(nick) == Auth.Guest) return ActResult.Fail("Спершу скажи, як тебе кликати");
        if (rooms.Find(roomId) is not { } room || room.Game is not Dice game) return ActResult.Fail("Такого столу вже нема");
        int? seat;
        lock (room.Sync) seat = room.SeatOf(nick);
        if (seat is null)
        {
            var watching = false;
            foreach (var conn in presence.ConnectionsOf(nick))
                if (room.Watchers.ContainsKey(conn)) { watching = true; break; }
            if (!watching) return ActResult.Fail("Спершу відкрий цей стіл");
        }
        if (!Allow(Auth.NickKey(nick), clock.UtcNow)) return ActResult.Fail("Не так швидко");
        lock (room.Sync)
        {
            if (room.Status != RoomStatus.Playing) return ActResult.Fail("Зараз не ставлять");
            return game.Bet(nick, room.SeatOf(nick), truth);
        }
    }

    bool Allow(string key, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_tries.TryGetValue(key, out var q)) _tries[key] = q = new Queue<DateTimeOffset>(PerSecond + 1);
            while (q.Count > 0 && now - q.Peek() >= TimeSpan.FromSeconds(1)) q.Dequeue();
            if (q.Count >= PerSecond) return false;
            q.Enqueue(now);
            if (_tries.Count > 512)
                foreach (var k in _tries.Where(p => p.Value.Count == 0 || now - p.Value.Last() > TimeSpan.FromMinutes(1)).Select(p => p.Key).ToList())
                    _tries.Remove(k);
            return true;
        }
    }
}

public sealed record DiceBetBody(string? Room, bool Truth);

/// <summary>Підключення «Під глеком» у <see cref="GamesSetup"/>: сезон звань і ставки вболівальників.</summary>
public static class DiceSetup
{
    const int MaxBody = 512;

    public static IServiceCollection AddDice(this IServiceCollection services)
    {
        services.AddSingleton(sp => new DiceSeason(sp.GetService<Db>(), sp.GetService<ILogger<DiceSeason>>()));
        services.AddHostedService(sp => sp.GetRequiredService<DiceSeason>());
        services.AddSingleton<DiceFans>();
        return services;
    }

    public static WebApplication MapDice(this WebApplication app)
    {
        app.MapPost("/api/games/dice/bet", async (HttpContext c, DiceFans fans, CancellationToken ct) =>
        {
            DiceBetBody? body = null;
            if (c.Request.ContentLength is null or <= MaxBody)
            {
                try { body = await c.Request.ReadFromJsonAsync<DiceBetBody>(new JsonSerializerOptions(JsonSerializerDefaults.Web), ct); }
                catch (Exception e) when (e is JsonException or InvalidOperationException or BadHttpRequestException) { }
            }
            var r = body is null ? ActResult.Fail("Тут так не ставлять") : fans.Bet(Auth.Nick(c), body.Room, body.Truth);
            return Results.Json(new { ok = r.Ok, message = r.Message });
        });
        // Сезон: period = week (типово) | month | all.
        app.MapGet("/api/games/dice/season", (string? period, DiceSeason season, IClock clock) =>
        {
            var p = period is "month" or "all" ? period : "week";
            var since = p switch { "all" => DateTimeOffset.MinValue, "month" => clock.UtcNow.AddDays(-30), _ => clock.UtcNow.AddDays(-7) };
            return Results.Json(new { period = p, titles = season.Table(since) });
        });
        return app;
    }
}
