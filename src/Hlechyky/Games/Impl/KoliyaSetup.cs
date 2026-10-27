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
        app.MapGet(Root + "/{room}/log", (string room, int? from, Rooms rooms) => Log(rooms, room, from ?? 0));
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

    public static IResult Log(Rooms rooms, string room, int from)
    {
        if (rooms.Find(room) is not { } r || r.Game is not Koliya game) return Results.NotFound();
        object body;
        lock (r.Sync)
        {
            if (r.Status != RoomStatus.Playing) return Results.NotFound();
            body = game.Log(Math.Max(0, from));
        }
        return Results.Json(body);
    }
}
