using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Тир дня»: ті самі три стенди, але мішені однакові для всіх на цілий київський день (сід дня), і спроба одна —
/// «Ще раз» показує підсумок, а не нову партію (інакше вдруге вже знаєш, де вискочить золотий). Щоденний каркас
/// (<see cref="IDailyGame"/>) дає «☀ Сьогодні», серію й щоденний глек; таблиця дня за очками — своя
/// (<see cref="TyrDailyBoard"/>): каркасна рахує «менше — краще».
/// </summary>
public sealed class TyrDaily : TyrBase, IDailyGame
{
    const string Puzzle = "tyr";

    public override GameInfo Info { get; } = new(
        "tyr-daily", "Тир дня", "«Тир дня»", GameGroup.Solo, 1, 1, TickMs: 50,
        Start: StartMode.Immediate, Private: true, Persistent: true,
        Hint: "Однакові мішені для всіх на цілий день і одна спроба: три стенди по 45 секунд. Хто сьогодні найвлучніший?",
        Client: "tyr");

    string _day = "";
    /// <summary>Результат уже пішов у таблицю дня — «Ще раз» і відновлення не платять удруге.</summary>
    bool _reported;
    TyrDailyBoard? _board;

    public string Day => _day;
    public bool Reported => _reported;

    public override string SoloKey(string nickKey, IClock clock) => $"daily:{Info.Id}:{Days.Today(clock)}:{nickKey}";

    protected override Random ScheduleRng() => new(Days.Seed(Puzzle, _day));

    protected override bool InPlay(int seat) => seat == 0;

    public override void Start()
    {
        _board ??= Ctx.Services.GetService<TyrDailyBoard>();
        var today = Days.Today(Ctx.Clock);
        // День уже зіграно — лишаємо підсумок, нову партію не даємо.
        if (_reported && _day == today)
        {
            _started = _over = true;
            Ctx.Finish([0], "");
            return;
        }
        _day = today;
        _reported = false;
        Begin([true]);
    }

    protected override void End() => Report();

    /// <summary>Спроба одна: хто пішов посеред стенду, той і здав — рахунок, який був, іде в таблицю дня.</summary>
    public override void OnLeave(int seat)
    {
        if (!_started || Over) return;
        _over = true;
        Report();
    }

    void Report()
    {
        _winners = [0];
        var me = Shooter(0);
        if (_reported) { Ctx.Finish([0], ""); return; }
        _reported = true;
        var nick = Ctx.NickOf(0) ?? "";
        _board?.Record(_day, nick, (int)me.Score, me.Hits, me.Shots, Ctx.Clock.UtcNow);
        // Спроба в щоденному каркасі одна; очки там не лягають (він рахує «менше — краще»), тож час — 0.
        Ctx.Score(0, 0, 1);
        Ctx.Award(0, 0, $"daily:{Info.Id}");
        Ctx.Finish([0], $"{Info.Title}: {nick} — {me.Score} {Tyr.Ochok(me.Score)}, влучив {me.Hits} з {me.Shots}");
    }

    public override object View(int? seat)
    {
        var v = Common();
        v["mode"] = "daily";
        var top = _board?.Top(_day) ?? [];
        var nick = Ctx.NickOf(0);
        var mine = nick is null ? -1 : top.ToList().FindIndex(r => TyrDailyBoard.NickKey(r.Nick) == TyrDailyBoard.NickKey(nick));
        var done = Over;
        v["daily"] = new
        {
            day = _day,
            no = _day.Length == 0 ? 0 : Days.Number(_day),
            // Таблицю дня показуємо лише після своєї спроби: інакше це підказка, скільки там узагалі можна набрати.
            board = !done ? null : top.Take(10).Select(r => new { nick = r.Nick, points = r.Points, hits = r.Hits, shots = r.Shots }).ToArray(),
            place = !done || mine < 0 ? (int?)null : mine + 1,
            players = top.Count,
            share = !done ? null : $"Тир дня №{(_day.Length == 0 ? 0 : Days.Number(_day))} — {Shooter(0).Score} {Tyr.Ochok(Shooter(0).Score)}, {Shooter(0).Hits}/{Shooter(0).Shots} влучних\nhttps://hlechyky.pp.ua/#games",
        };
        return v;
    }

    sealed record Saved(string Day, bool Reported, long Score, long[] Stands, int Shots, int Hits, int Misses, int Kegs, int Pots, int Golds);

    /// <summary>Зберігаємо лише день і підсумок: недограна спроба після перезапуску сервера починається заново.</summary>
    public override string? Save() => _day.Length == 0 ? null : JsonSerializer.Serialize(new Saved(_day, _reported, Shooter(0).Score,
        [.. Shooter(0).StandScore], Shooter(0).Shots, Shooter(0).Hits, Shooter(0).Misses, Shooter(0).Kegs, Shooter(0).Pots, Shooter(0).Golds));

    public override void Load(string json)
    {
        var s = JsonSerializer.Deserialize<Saved>(json);
        if (s is null || !s.Reported) return;
        _day = s.Day;
        _reported = true;
        _started = _over = true;
        _winners = [0];
        _si = _stands.Length - 1;
        var me = Shooter(0);
        me.Plays = true;
        me.Score = s.Score;
        for (var i = 0; i < me.StandScore.Length && i < s.Stands.Length; i++) me.StandScore[i] = s.Stands[i];
        (me.Shots, me.Hits, me.Misses, me.Kegs, me.Pots, me.Golds) = (s.Shots, s.Hits, s.Misses, s.Kegs, s.Pots, s.Golds);
    }
}

/// <summary>Рядок таблиці дня «Тиру дня».</summary>
public sealed record TyrDayRow(string Nick, int Points, int Hits, int Shots);

/// <summary>
/// Таблиця дня «Тиру дня» (очки — більше краще). Гра звертається лише до пам'яті: запис і перше читання дня йдуть фоном
/// (під замком кімнати — жодного SQLite). Той самий лад, що й у «Скільки? дня».
/// </summary>
public sealed class TyrDailyBoard(Db? db) : IDailyPoints
{
    string IDailyPoints.Game => "tyr-daily";
    long? IDailyPoints.Points(string day, string nick) => Of(day, nick)?.Points;

    const string Schema = """
        CREATE TABLE IF NOT EXISTS tyr_daily(
            day TEXT NOT NULL, nick_key TEXT NOT NULL, nick TEXT NOT NULL, points INTEGER NOT NULL,
            hits INTEGER NOT NULL, shots INTEGER NOT NULL, created_at TEXT NOT NULL, PRIMARY KEY(day, nick_key)) WITHOUT ROWID;
        """;

    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TyrDayRow>> _days = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, bool> _loading = new(StringComparer.Ordinal);
    bool _schema;

    public static string NickKey(string nick) => Economy.Economy.Key(nick);

    /// <summary>Записати результат (перший за день — назавжди).</summary>
    public void Record(string day, string nick, int points, int hits, int shots, DateTimeOffset at)
    {
        var key = NickKey(nick);
        if (!Day(day).TryAdd(key, new TyrDayRow(nick, points, hits, shots))) return;
        if (db is null) return;
        _ = Task.Run(() =>
        {
            try
            {
                db.With(c =>
                {
                    Ensure(c);
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "INSERT OR IGNORE INTO tyr_daily(day, nick_key, nick, points, hits, shots, created_at) VALUES($d, $k, $n, $p, $h, $s, $t)";
                    cmd.Parameters.AddWithValue("$d", day);
                    cmd.Parameters.AddWithValue("$k", key);
                    cmd.Parameters.AddWithValue("$n", nick);
                    cmd.Parameters.AddWithValue("$p", points);
                    cmd.Parameters.AddWithValue("$h", hits);
                    cmd.Parameters.AddWithValue("$s", shots);
                    cmd.Parameters.AddWithValue("$t", at.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                    cmd.ExecuteNonQuery();
                });
            }
            catch (Exception) { /* таблиця дня — радість, а не облік: без запису гра не ламається */ }
        });
    }

    /// <summary>Уся таблиця дня від більшого (рівні — за влучністю, далі за ніком).</summary>
    public IReadOnlyList<TyrDayRow> Top(string day) =>
        [.. Day(day).Values.OrderByDescending(r => r.Points).ThenByDescending(r => r.Hits * 1000 / Math.Max(1, r.Shots))
            .ThenBy(r => r.Nick, StringComparer.Ordinal)];

    public TyrDayRow? Of(string day, string nick) => Day(day).GetValueOrDefault(NickKey(nick));

    ConcurrentDictionary<string, TyrDayRow> Day(string day)
    {
        var rows = _days.GetOrAdd(day, _ => new ConcurrentDictionary<string, TyrDayRow>(StringComparer.Ordinal));
        if (db is not null && _loading.TryAdd(day, true))
        {
            foreach (var old in _days.Keys.Where(d => string.CompareOrdinal(d, day) < 0).OrderByDescending(d => d).Skip(7).ToList())
            {
                _days.TryRemove(old, out _);
                _loading.TryRemove(old, out _);
            }
            _ = Task.Run(() => Load(day, rows));
        }
        return rows;
    }

    void Load(string day, ConcurrentDictionary<string, TyrDayRow> rows)
    {
        if (db is null) return;
        try
        {
            db.With(c =>
            {
                Ensure(c);
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT nick_key, nick, points, hits, shots FROM tyr_daily WHERE day = $d";
                cmd.Parameters.AddWithValue("$d", day);
                using var r = cmd.ExecuteReader();
                while (r.Read()) rows.TryAdd(r.GetString(0), new TyrDayRow(r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4)));
            });
        }
        catch (Exception) { _loading.TryRemove(day, out _); }
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

public static class TyrSetup
{
    /// <summary>Таблиця дня «Тиру дня» і її очки в «☀ Сьогодні».</summary>
    public static IServiceCollection AddTyr(IServiceCollection services)
    {
        services.AddSingleton<TyrDailyBoard>();
        services.AddSingleton<IDailyPoints>(sp => sp.GetRequiredService<TyrDailyBoard>());
        return services;
    }
}
