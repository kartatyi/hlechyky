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
