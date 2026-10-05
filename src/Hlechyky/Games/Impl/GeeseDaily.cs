using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Гуси дня»: той самий парад для всіх за київську добу (сід від дня), одна спроба, п'ять раундів (1–3 — питання до
/// параду, 4–5 — на пам'ять). Щоденний каркас (<see cref="IDailyGame"/>) дає «☀ Сьогодні», серію й щоденний глек;
/// таблиця дня за очками — своя (<see cref="GeeseDailyBoard"/>), як у «Скільки? дня».
/// </summary>
public sealed class GeeseDaily : GeeseBase, IDailyGame
{
    const string Puzzle = "geese";
    static readonly int[] DayLevels = [0, 2, 3, 5, 6];

    public override GameInfo Info { get; } = new(
        "geese-daily", "Гуси дня", "«Гуси дня»", GameGroup.Solo, 1, 1,
        TickMs: TickStep, Start: StartMode.Immediate, Private: true, Persistent: true, Hidden: true,
        Hint: "П'ять парадів дня — однакові для всіх, одна спроба. Хто сьогодні порахував точніше? Таблиця дня — у грі",
        Client: "geese");

    protected override int[] Levels => DayLevels;
    protected override bool BotsAllowed => false;
    protected override int Seed() => Days.Seed(Puzzle, _day);

    string _day = "";
    bool _reported;
    GeeseDailyBoard? _board;

    /// <summary>Найбільше очок дня: самому — лише за точність.</summary>
    public int Max => DayLevels.Length * Exact;

    public override string SoloKey(string nickKey, IClock clock) => $"daily:{Info.Id}:{Days.Today(clock)}:{nickKey}";

    public override void Start()
    {
        _board ??= Ctx.Services.GetService<GeeseDailyBoard>();
        var today = Days.Today(Ctx.Clock);
        // День уже зіграно («Ще раз» на дограному дні каркас пускає) — лишаємо підсумок, нової партії не даємо.
        if (_reported && _day == today) { _ph = PhDone; Ctx.Finish([0], ""); return; }
        _day = today;
        _reported = false;
        base.Start();
    }

    private protected override void Done()
    {
        _ph = PhDone;
        _winners = [0];
        var points = (int)_scores[0];
        if (_reported) { Ctx.Finish([0], ""); return; }
        _reported = true;
        var nick = Ctx.NickOf(0) ?? "";
        _board?.Record(_day, nick, points, Marks(), Ctx.Clock.UtcNow);
        // Спроба в щоденному каркасі одна; очки там не лягають (він рахує «менше — краще»), тож час — 0.
        Ctx.Score(0, 0, 1);
        Ctx.Award(0, 0, $"daily:{Info.Id}");
        Ctx.Finish([0], $"{Info.Title}: {nick} — {points} з {Max} {Marks()}");
    }

    /// <summary>Смужка по раундах: 🎯 точно, 🟨 мимо на одну, ⬛ мимо — без самих чисел, тож ділитись не шкода.</summary>
    public string Marks()
    {
        var sb = new StringBuilder();
        foreach (var h in _hist) sb.Append(h.Pts[0] >= Exact ? "🎯" : h.Pts[0] > 0 ? "🟨" : "⬛");
        for (var i = _hist.Count; i < DayLevels.Length; i++) sb.Append("⬛");
        return sb.ToString();
    }

    private protected override object? DailyView()
    {
        var top = _board?.Top(_day) ?? [];
        var nick = Ctx.NickOf(0);
        var mine = nick is null ? -1 : top.ToList().FindIndex(r => GeeseDailyBoard.Key(r.Nick) == GeeseDailyBoard.Key(nick));
        var done = _ph == PhDone;
        return new
        {
            day = _day,
            no = _day.Length == 0 ? 0 : Days.Number(_day),
            max = Max,
            // Таблицю дня — лише після своєї спроби: інакше це підказка «хтось набрав 15 — парад легкий».
            board = done ? top.Take(10).Select(r => new { nick = r.Nick, points = r.Points, marks = r.Marks }).ToArray() : null,
            place = done && mine >= 0 ? mine + 1 : (int?)null,
            players = top.Count,
            share = done ? $"Гуси дня №{Days.Number(_day)} — {(int)_scores[0]}/{Max}\n{Marks()}\nhttps://hlechyky.pp.ua/#games" : null,
        };
    }

    /// <summary><see cref="Ph"/> — фаза на мить збереження: чи парад раунду <see cref="Round"/> уже показували.</summary>
    sealed record Saved(string Day, int Round, long Score, bool Done, List<int>? Pts, string? Ph = null);
    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    public override string? Save() => _day.Length == 0 ? null
        : JsonSerializer.Serialize(new Saved(_day, _r, _scores[0], _reported, [.. _hist.Select(h => h.Pts[0])], _ph), Wire);

    /// <summary>
    /// Повернувся посеред дня: зіграний день — лише підсумок; недограний — з того раунду, де зупинився
    /// (зіграні раунди лишаються зіграними: парад той самий, тож переграти вже бачений — нечесно й нецікаво).
    /// Пішов посеред параду чи відповіді — раунд продовжується з відповіді: інакше в раундах «на пам'ять» можна
    /// побачити питання, вийти й подивитись той самий парад уже цілеспрямовано.
    /// </summary>
    public override void Load(string json)
    {
        Saved? saved;
        try { saved = JsonSerializer.Deserialize<Saved>(json, Wire); }
        catch (JsonException) { return; }
        if (saved is null || saved.Day != _day || _rounds.Count == 0) return;
        _hist.Clear();
        foreach (var p in saved.Pts ?? [])
        {
            if (_hist.Count >= _rounds.Count) break;
            var pts = new int[MaxSeats];
            pts[0] = Math.Clamp(p, 0, Exact);
            _hist.Add((new int?[MaxSeats], pts, new bool[MaxSeats]));
        }
        _scores[0] = Math.Max(0, saved.Score);
        if (saved.Done)
        {
            _reported = true;
            _r = _rounds.Count - 1;
            _ph = PhDone;
            _winners = [0];
            Ctx.Finish([0], "");
            return;
        }
        _r = Math.Clamp(_hist.Count, 0, _rounds.Count - 1);
        Resume(answer: _hist.Count == saved.Round && saved.Ph is PhParade or PhAnswer);
    }
}

/// <summary>Рядок таблиці дня «Гусей дня».</summary>
public sealed record GeeseDayRow(string Nick, int Points, string Marks);

/// <summary>
/// Таблиця дня «Гусей дня» (<c>geese_daily</c>): очки «більше — краще», тож не каркасна (там «менше — краще»).
/// Гра звертається лише до пам'яті; запис і перше читання дня йдуть фоном (під замком кімнати — жодного SQLite).
/// </summary>
public sealed class GeeseDailyBoard(Db? db) : IDailyPoints
{
    string IDailyPoints.Game => "geese-daily";
    long? IDailyPoints.Points(string day, string nick) => Of(day, nick)?.Points;

    const string Schema = """
        CREATE TABLE IF NOT EXISTS geese_daily(
            day TEXT NOT NULL, nick_key TEXT NOT NULL, nick TEXT NOT NULL, points INTEGER NOT NULL,
            marks TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL, PRIMARY KEY(day, nick_key)) WITHOUT ROWID;
        """;

    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, GeeseDayRow>> _days = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, bool> _loading = new(StringComparer.Ordinal);
    bool _schema;

    public static string Key(string nick) => nick.Trim().ToLowerInvariant();

    /// <summary>Записати результат (перший за день — назавжди: «Ще раз» таблицю не переписує).</summary>
    public void Record(string day, string nick, int points, string marks, DateTimeOffset at)
    {
        var key = Key(nick);
        if (!Day(day).TryAdd(key, new GeeseDayRow(nick, points, marks)) || db is null) return;
        _ = Task.Run(() =>
        {
            try
            {
                db.With(c =>
                {
                    Ensure(c);
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "INSERT OR IGNORE INTO geese_daily(day, nick_key, nick, points, marks, created_at) VALUES($d, $k, $n, $p, $m, $t)";
                    cmd.Parameters.AddWithValue("$d", day);
                    cmd.Parameters.AddWithValue("$k", key);
                    cmd.Parameters.AddWithValue("$n", nick);
                    cmd.Parameters.AddWithValue("$p", points);
                    cmd.Parameters.AddWithValue("$m", marks);
                    cmd.Parameters.AddWithValue("$t", at.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                    cmd.ExecuteNonQuery();
                });
            }
            catch (Exception) { /* таблиця дня — радість, а не облік: без запису гра не ламається */ }
        });
    }

    /// <summary>Таблиця дня від більшого (рівні — за ніком). Не чекає на базу.</summary>
    public IReadOnlyList<GeeseDayRow> Top(string day) =>
        [.. Day(day).Values.OrderByDescending(r => r.Points).ThenBy(r => r.Nick, StringComparer.Ordinal)];

    public GeeseDayRow? Of(string day, string nick) => Day(day).GetValueOrDefault(Key(nick));

    ConcurrentDictionary<string, GeeseDayRow> Day(string day)
    {
        var rows = _days.GetOrAdd(day, _ => new ConcurrentDictionary<string, GeeseDayRow>(StringComparer.Ordinal));
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

    void Load(string day, ConcurrentDictionary<string, GeeseDayRow> rows)
    {
        if (db is null) return;
        try
        {
            db.With(c =>
            {
                Ensure(c);
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT nick_key, nick, points, marks FROM geese_daily WHERE day = $d";
                cmd.Parameters.AddWithValue("$d", day);
                using var r = cmd.ExecuteReader();
                while (r.Read()) rows.TryAdd(r.GetString(0), new GeeseDayRow(r.GetString(1), r.GetInt32(2), r.GetString(3)));
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

/// <summary>Сервіси «Порахуй гусей»: таблиця дня і її очки в «☀ Сьогодні».</summary>
public static class GeeseSetup
{
    public static IServiceCollection AddGeese(IServiceCollection services)
    {
        services.AddSingleton<GeeseDailyBoard>();
        services.AddSingleton<IDailyPoints>(sp => sp.GetRequiredService<GeeseDailyBoard>());
        return services;
    }
}
