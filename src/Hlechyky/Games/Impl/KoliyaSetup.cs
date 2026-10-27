using System.Collections.Concurrent;
using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Колія»: журнал команд партії окремим запитом — пізньому, після F5 чи коли в кадрах діра. Кадр SignalR великий
/// журнал не тягне, а тут це звичайна JSON-відповідь. Нічого прихованого в журналі нема (кадр і так бачить уся група).
/// </summary>
public static class KoliyaSetup
{
    public const string Root = "/api/games/koliya";

    public static void MapKoliya(WebApplication app)
    {
        app.MapGet(Root + "/{room}/log", (string room, int? from, int? snap, Rooms rooms) => Log(rooms, room, from ?? 0, snap == 1));
        app.MapPost(Root + "/{room}/snap", (string room, int e, int t, long h, int? z, HttpRequest req, Rooms rooms) => Snap(rooms, room, e, t, h, z == 1, req));
        app.MapPost(Root + "/{room}/watch", (string room, string? id, Rooms rooms) => Watch(rooms, room, id));
        var reports = new KoliyaReports(app.Services.GetRequiredService<DiagLog>());
        app.MapPost(Root + "/report", async (HttpContext c, IConfiguration config, string? id) =>
        {
            if (!config.GetValue("Diag:Koliya", true)) return Results.Ok(new { ok = false, off = true });
            var code = await reports.Accept(Auth.Nick(c), c.Connection.RemoteIpAddress?.ToString(), c.Request.ContentLength,
                c.Request.Body, Environment.TickCount64, c.RequestAborted, id);
            return code == 200 ? Results.Ok(new { ok = true }) : Results.StatusCode(code);
        });
    }

    /// <summary>
    /// K28: глядач (не грає: усі четверо вже за столом) каже «я тут» раз на 2 с — так стіл рахує «👀 N глядачів».
    /// id — випадковий номер вкладки (не нік: гість без ніка теж дивиться). Відповідь — і замір пінгу глядача.
    /// </summary>
    public static IResult Watch(Rooms rooms, string room, string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 40) return Results.BadRequest();
        if (rooms.Find(room) is not { } r || r.Game is not Koliya game) return Results.NotFound();
        int n;
        lock (r.Sync) n = game.Watch(id);
        return Results.Json(new { sp = n });
    }

    /// <summary>
    /// K44: знімок остаточного світу (стиснутий браузером, сервер не розбирає) — тілом запиту, бо в Input (≤ 8 КБ) не
    /// влазить. Хто прислав — неважливо: знімок стає «підтвердженим», лише коли хеші місць за столом збіглися з його h.
    /// </summary>
    public static async Task<IResult> Snap(Rooms rooms, string room, int e, int t, long h, bool z, HttpRequest req)
    {
        if (req.ContentLength is > Koliya.MaxSnap) return Results.StatusCode(413);
        if (rooms.Find(room) is not { } r || r.Game is not Koliya game) return Results.NotFound();
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        for (int n; (n = await req.Body.ReadAsync(buf)) > 0;)
        {
            if (ms.Length + n > Koliya.MaxSnap) return Results.StatusCode(413);
            ms.Write(buf, 0, n);
        }
        string? err;
        lock (r.Sync)
        {
            if (r.Status != RoomStatus.Playing) return Results.NotFound();
            err = game.PutSnap(e, t, h, z, ms.ToArray());
        }
        return err is null ? Results.Ok() : Results.BadRequest(err);
    }

    public static IResult Log(Rooms rooms, string room, int from, bool snap = false)
    {
        if (rooms.Find(room) is not { } r || r.Game is not Koliya game) return Results.NotFound();
        object body;
        lock (r.Sync)
        {
            if (r.Status != RoomStatus.Playing) return Results.NotFound();
            body = game.Log(Math.Max(0, from), snap);
        }
        return Results.Json(body);
    }
}

/// <summary>
/// Звіт партії «Колії» з браузера (web/games/koliya/report.js): чим скінчилась, скільки тривала, кадри, пінг, пристрій —
/// рядком у data/diag/koliya-&lt;дата&gt;.jsonl (зводить D:\or-wt\_tools\koliya\report.py). Не більше 8 КБ; з однієї вкладки
/// (нік, адреса й номер вкладки id — гості з однієї домівки мають той самий нік «гість») — не частіше ніж раз на 2 с;
/// за добу — не більше <see cref="MaxPerDay"/> рядків, хоч би що слали. Вимикач — Diag:Koliya.
/// </summary>
public sealed class KoliyaReports(DiagLog diag, int maxPerDay = KoliyaReports.MaxPerDay)
{
    public const int MaxBytes = 8 * 1024;
    public const int MinGapMs = 2000;
    public const int MaxPerDay = 20_000;   // ~1 КБ рядок — до ~20 МБ на добу, навіть якщо хтось засипає навмисно

    readonly ConcurrentDictionary<string, long> _last = new(StringComparer.Ordinal);
    DateOnly _day;
    int _today;

    /// <summary>HTTP-код відповіді: 200 — лягло в data/diag/koliya-&lt;дата&gt;.jsonl; 413, 429, 400 — ні.</summary>
    public async Task<int> Accept(string nick, string? ip, long? length, Stream body, long nowMs, CancellationToken ct = default, string? id = null)
    {
        if (length is not { } len || len > MaxBytes) return 413;
        var key = nick + "|" + ip + "|" + (id is { Length: <= 40 } ? id : "");
        if (_last.TryGetValue(key, out var was) && nowMs - was < MinGapMs) return 429;
        lock (_last)
        {
            var day = DateOnly.FromDateTime(DateTime.Now);
            if (day != _day) { _day = day; _today = 0; }
            if (_today >= maxPerDay) return 429;
            _today++;
        }
        _last[key] = nowMs;
        if (_last.Count > 500)
            foreach (var kv in _last)
                if (nowMs - kv.Value > 60_000) _last.TryRemove(kv.Key, out _);
        JsonDocument doc;
        try { doc = await JsonDocument.ParseAsync(body, cancellationToken: ct); }
        catch (JsonException) { return 400; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return 400;
            diag.Write("koliya", new { k = "koliya", at = DateTimeOffset.Now.ToString("O"), nick, m = doc.RootElement.Clone() });
        }
        return 200;
    }
}
