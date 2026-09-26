using System.Globalization;
using Hlechyky.Games;
using Microsoft.Data.Sqlite;

namespace Hlechyky;

/// <summary>
/// «💡 Розробнику»: що додати, що змінити, що зламалось — від тих, хто сайтом користується, тому, хто його робить.
/// Кожен бачить свої записки й відповідь на них («у планах», «зроблено», «не буде» + рядок від розробника), адмін —
/// усі разом, з фільтром за станом. Раніше пропозиції губились у Балачках між кубиками й «добраніч».
/// </summary>
public sealed record FeedbackItem(long Id, string Nick, string Kind, string Text, string? Place, string? Screen, string? Ua,
    string Status, string? Reply, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Таблиця записок. DDL і SQL живуть тут, від <see cref="Db"/> — лише з'єднання на одну коротку операцію.</summary>
public sealed class FeedbackStore
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS feedback(
            id INTEGER PRIMARY KEY AUTOINCREMENT, nick TEXT NOT NULL, nick_key TEXT NOT NULL, kind TEXT NOT NULL,
            text TEXT NOT NULL, place TEXT, screen TEXT, ua TEXT, status TEXT NOT NULL DEFAULT 'new', reply TEXT,
            created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_feedback_nick ON feedback(nick_key, id);
        CREATE INDEX IF NOT EXISTS ix_feedback_status ON feedback(status, id);
        """;
    const string Cols = "id, nick, kind, text, place, screen, ua, status, reply, created_at, updated_at";

    readonly Db _db;

    public FeedbackStore(Db db)
    {
        _db = db;
        _db.With(c => { Exec(c, Schema); return 0; });
    }

    public long Add(string nick, string kind, string text, string? place, string? screen, string? ua, DateTimeOffset now) => _db.With(c =>
    {
        using var cmd = Cmd(c, """
            INSERT INTO feedback(nick, nick_key, kind, text, place, screen, ua, status, created_at, updated_at)
            VALUES($n, $k, $kind, $t, $p, $s, $ua, 'new', $now, $now);
            SELECT last_insert_rowid();
            """, ("$n", nick), ("$k", Auth.NickKey(nick)), ("$kind", kind), ("$t", text), ("$p", place), ("$s", screen), ("$ua", ua), ("$now", Iso(now)));
        return (long)cmd.ExecuteScalar()!;
    });

    /// <summary>Скільки записок нік залишив, починаючи з <paramref name="since"/> — для обмеження частоти.</summary>
    public int CountSince(string nick, DateTimeOffset since) => _db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT COUNT(*) FROM feedback WHERE nick_key = $k AND created_at >= $since",
            ("$k", Auth.NickKey(nick)), ("$since", Iso(since)));
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    /// <summary>Та сама записка того самого ніка, залишена недавно (подвійний клік, «а чи дійшло?»).</summary>
    public bool Recent(string nick, string text, DateTimeOffset since) => _db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT 1 FROM feedback WHERE nick_key = $k AND text = $t AND created_at >= $since LIMIT 1",
            ("$k", Auth.NickKey(nick)), ("$t", text), ("$since", Iso(since)));
        return cmd.ExecuteScalar() is not null;
    });

    public List<FeedbackItem> Of(string nick, int limit) => _db.With(c =>
        Read(c, $"SELECT {Cols} FROM feedback WHERE nick_key = $k ORDER BY id DESC LIMIT $n", ("$k", Auth.NickKey(nick)), ("$n", limit)));

    public List<FeedbackItem> All(string? status, int limit) => _db.With(c => status is null
        ? Read(c, $"SELECT {Cols} FROM feedback ORDER BY id DESC LIMIT $n", ("$n", limit))
        : Read(c, $"SELECT {Cols} FROM feedback WHERE status = $s ORDER BY id DESC LIMIT $n", ("$s", status), ("$n", limit)));

    public FeedbackItem? Get(long id) => _db.With(c => Read(c, $"SELECT {Cols} FROM feedback WHERE id = $id", ("$id", id)).FirstOrDefault());

    public Dictionary<string, int> Counts() => _db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT status, COUNT(*) FROM feedback GROUP BY status");
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        while (r.Read()) map[r.GetString(0)] = r.GetInt32(1);
        return map;
    });

    public bool Update(long id, string status, string? reply, DateTimeOffset now) => _db.With(c =>
        Exec(c, "UPDATE feedback SET status = $s, reply = $r, updated_at = $now WHERE id = $id",
            ("$s", status), ("$r", reply), ("$now", Iso(now)), ("$id", id)) > 0);

    static List<FeedbackItem> Read(SqliteConnection c, string sql, params (string, object?)[] ps)
    {
        using var cmd = Cmd(c, sql, ps);
        using var r = cmd.ExecuteReader();
        var list = new List<FeedbackItem>();
        static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        while (r.Read())
            list.Add(new FeedbackItem(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), S(r, 4), S(r, 5), S(r, 6),
                r.GetString(7), S(r, 8), Ts(r.GetString(9)), Ts(r.GetString(10))));
        return list;
    }

    static string Iso(DateTimeOffset t) => t.ToString("O", CultureInfo.InvariantCulture);
    static DateTimeOffset Ts(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

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
}

/// <summary>Правила записок: що можна написати, як часто і хто що бачить.</summary>
public sealed class Feedback(FeedbackStore store, IClock clock)
{
    /// <summary>Що додати, що змінити, що зламалось.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["idea", "change", "bug"];
    /// <summary>нове → переглянуто → у планах → зроблено / не буде.</summary>
    public static readonly IReadOnlyList<string> Statuses = ["new", "seen", "planned", "done", "nope"];

    public const int MinText = 5;
    public const int MaxText = 2000;
    public const int MaxReply = 500;
    /// <summary>Записок на годину й на добу з одного ніка: досить, щоб висловитись, і мало, щоб засипати розробника.</summary>
    public const int PerHour = 5;
    public const int PerDay = 20;
    /// <summary>Скільки своїх записок показувати людині.</summary>
    public const int MineLimit = 30;
    public const int AdminLimit = 300;

    public sealed record Result(bool Ok, string Message, long Id = 0);

    public Result Submit(string nick, string? kind, string? text, string? place, string? screen, string? ua)
    {
        // Голий «гість» — це ще ніхто: відповідь розробника не буде кому показати.
        if (string.IsNullOrWhiteSpace(nick) || Auth.NickKey(nick) == Auth.Guest) return new(false, "Спершу назвись — тоді розробник знатиме, кому відповісти");
        kind = (kind ?? "").Trim().ToLowerInvariant();
        if (!Kinds.Contains(kind)) return new(false, "Не зрозумів, що це: пропозиція, зміна чи баг?");
        text = Clean(text, MaxText + 1);
        if (text.Length < MinText) return new(false, "Напиши трохи більше — хоч кілька слів");
        if (text.Length > MaxText) return new(false, $"Задовго: до {MaxText} символів. Розбий на дві записки");
        var now = clock.UtcNow;
        if (store.Recent(nick, text, now.AddMinutes(-30))) return new(false, "Це вже надіслано — розробник побачить");
        if (store.CountSince(nick, now.AddHours(-1)) >= PerHour) return new(false, "За годину досить записок — решту напиши трохи згодом");
        if (store.CountSince(nick, now.AddDays(-1)) >= PerDay) return new(false, "На сьогодні записок досить — завтра продовжимо");
        var id = store.Add(nick, kind, text, Short(place, 200), Short(screen, 40), Short(ua, 300), now);
        return new(true, kind == "bug" ? "Дякую! Баг записано — розробник погляне" : "Дякую! Розробник прочитає", id);
    }

    public List<FeedbackItem> Mine(string nick) => store.Of(nick, MineLimit);

    public (List<FeedbackItem> Items, Dictionary<string, int> Counts) List(string? status) =>
        (store.All(status is not null && Statuses.Contains(status) ? status : null, AdminLimit), store.Counts());

    public Result Update(long id, string? status, string? reply)
    {
        if (store.Get(id) is not { } item) return new(false, "Такої записки нема");
        var s = string.IsNullOrWhiteSpace(status) ? item.Status : status.Trim().ToLowerInvariant();
        if (!Statuses.Contains(s)) return new(false, "Невідомий стан");
        // null — відповідь не чіпаємо; порожній рядок — прибрати відповідь.
        var r = reply is null ? item.Reply : Clean(reply, MaxReply + 1) is { Length: > 0 } t ? Short(t, MaxReply) : null;
        store.Update(id, s, r, clock.UtcNow);
        return new(true, "Збережено", id);
    }

    public int NewCount() => store.Counts().GetValueOrDefault("new");

    /// <summary>Без керівних символів (крім переносу рядка) і без зайвих порожніх рядків по краях.</summary>
    static string Clean(string? s, int max)
    {
        var t = new string((s ?? "").Where(ch => ch == '\n' || !char.IsControl(ch)).ToArray()).Trim();
        return t.Length > max ? t[..max] : t;
    }

    static string? Short(string? s, int max)
    {
        var t = Clean(s, max);
        if (t.Length == 0) return null;
        return t.Length <= max ? t : t[..max];
    }
}

public static class FeedbackSetup
{
    public sealed record FeedbackBody(string? Kind, string? Text, string? Place, string? Screen, string? Ua);
    public sealed record FeedbackPatch(string? Status, string? Reply);

    public static IServiceCollection AddHlechykyFeedback(this IServiceCollection services)
    {
        services.AddSingleton<FeedbackStore>();
        services.AddSingleton<Feedback>();
        return services;
    }

    static object View(FeedbackItem x, bool admin) => new
    {
        id = x.Id, nick = x.Nick, kind = x.Kind, text = x.Text, status = x.Status, reply = x.Reply,
        at = x.CreatedAt, updatedAt = x.UpdatedAt,
        // де й на чому — лише адміну: людині своє «Mozilla/5.0…» ні до чого
        place = admin ? x.Place : null, screen = admin ? x.Screen : null, ua = admin ? x.Ua : null,
    };

    static IResult Fail(string message) => Results.BadRequest(new { ok = false, message });

    public static WebApplication MapHlechykyFeedback(this WebApplication app)
    {
        var api = app.MapGroup("/api/feedback");

        api.MapPost("", (HttpContext c, FeedbackBody b, Feedback fb) =>
        {
            var r = fb.Submit(Auth.Nick(c), b.Kind, b.Text, b.Place, b.Screen, b.Ua);
            return r.Ok ? Results.Ok(new { ok = true, id = r.Id, message = r.Message }) : Fail(r.Message);
        });

        api.MapGet("/mine", (HttpContext c, Feedback fb) => Results.Ok(new { items = fb.Mine(Auth.Nick(c)).Select(x => View(x, false)) }));

        api.MapGet("", (HttpContext c, string? status, Feedback fb) =>
        {
            if (!Auth.IsAdmin(c)) return Fail("Це бачить лише розробник");
            var (items, counts) = fb.List(status);
            return Results.Ok(new { items = items.Select(x => View(x, true)), counts });
        });

        api.MapGet("/new-count", (HttpContext c, Feedback fb) =>
            Auth.IsAdmin(c) ? Results.Ok(new { count = fb.NewCount() }) : Results.Ok(new { count = 0 }));

        api.MapPatch("/{id:long}", (HttpContext c, long id, FeedbackPatch b, Feedback fb) =>
        {
            if (!Auth.IsAdmin(c)) return Fail("Це вміє лише розробник");
            var r = fb.Update(id, b.Status, b.Reply);
            return r.Ok ? Results.Ok(new { ok = true, message = r.Message }) : Fail(r.Message);
        });
        return app;
    }
}
