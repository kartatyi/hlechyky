using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Тіло реакції глядача: <c>{ room, e }</c>.</summary>
public sealed record PictionaryReactBody(string? Room, int E);

/// <summary>Тіло «📌 в альбом»: <c>{ room, t }</c> — хід партії, чий малюнок закинути.</summary>
public sealed record PictionaryPinBody(string? Room, int T);

/// <summary>Тіло адмінського «прибрати з альбому»: <c>{ id }</c>.</summary>
public sealed record PictionaryDeleteBody(long Id);

/// <summary>
/// Піктіонарі поза столом (прохід №3): сховище альбому й рекордів пар, реакції глядачів (глядач не сидить за
/// столом, а <c>Rooms.Act</c> пускає лише тих, хто сидить — як голос публіки в «Дотепах»), «📌 в альбом» після партії
/// (після кінця партії <c>Act</c> уже не ходить), і публічний Альбом Піктіонарі — сторінка web/pictionary-album/,
/// яку може гортати будь-хто, навіть гість без ніка. Відповіді дій — завжди 200 з <c>{ ok, message }</c>.
/// </summary>
public static class PictionarySetup
{
    const int MaxBody = 1024;
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static IServiceCollection AddPictionary(this IServiceCollection services)
    {
        services.AddSingleton(sp => new PictionaryStore(sp.GetService<Db>(), sp.GetService<ILogger<PictionaryStore>>()));
        services.AddHostedService(sp => sp.GetRequiredService<PictionaryStore>());
        return services;
    }

    public static WebApplication MapPictionary(this WebApplication app)
    {
        // 😂🔥🤯 глядача: лише той, хто цей стіл справді відкрив і за ним не сидить.
        app.MapPost("/api/games/pictionary/react", async (HttpContext c, Rooms rooms, Presence presence, CancellationToken ct) =>
        {
            var body = await Read<PictionaryReactBody>(c, ct);
            var nick = Auth.Nick(c);
            if (body is null) return Reply(ActResult.Fail("Тут так не реагують"));
            if (Auth.NickKey(nick) == Auth.Guest) return Reply(ActResult.Fail("Спершу скажи, як тебе кликати"));
            if (rooms.Find(body.Room) is not { } room || room.Game is not Pictionary game) return Reply(ActResult.Fail("Такого столу вже нема"));
            var watching = false;
            foreach (var conn in presence.ConnectionsOf(nick))
                if (room.Watchers.ContainsKey(conn)) { watching = true; break; }
            if (!watching) return Reply(ActResult.Fail("Спершу відкрий цей стіл"));
            lock (room.Sync)
            {
                if (room.Has(nick)) return Reply(ActResult.Fail("Ти за столом — реагуй на картці"));
                if (room.Status != RoomStatus.Playing) return Reply(ActResult.Fail("Зараз нема на що реагувати"));
                return Reply(game.FanReact(Auth.NickKey(nick), body.E));
            }
        });

        // Малюнок ходу для галереї тим, хто цей хід не застав (штрихами, SketchWire).
        app.MapGet("/api/games/pictionary/art", (string? room, int? t, Rooms rooms) =>
        {
            if (rooms.Find(room) is not { } r || r.Game is not Pictionary game || t is null) return Results.NotFound();
            string? z;
            lock (r.Sync) z = game.ArtZ(t.Value);
            return z is null ? Results.NotFound() : Results.Json(new { t, z });
        });

        // «📌 в Альбом Піктіонарі» після партії.
        app.MapPost("/api/games/pictionary/pin", async (HttpContext c, Rooms rooms, PictionaryStore store, CancellationToken ct) =>
        {
            var body = await Read<PictionaryPinBody>(c, ct);
            var nick = Auth.Nick(c);
            if (body is null) return Reply(ActResult.Fail("Тут так не закидають"));
            if (Auth.NickKey(nick) == Auth.Guest) return Reply(ActResult.Fail("Спершу скажи, як тебе кликати"));
            if (rooms.Find(body.Room) is not { } room || room.Game is not Pictionary game) return Reply(ActResult.Fail("Такого столу вже нема"));
            (PictionaryArt? Art, string? Error) got;
            lock (room.Sync) got = game.Pin(nick, body.T);
            if (got.Art is not { } a) return Reply(ActResult.Fail(got.Error ?? "Не вийшло"));
            // база — вже поза замком кімнати
            var id = store.Add(a.Word, a.Author, a.By, a.Hearts, a.Home, a.At, a.Z);
            return Results.Json(new { ok = true, message = "📌 Малюнок у публічному альбомі", id });
        });

        // Публічний альбом: сторінками, новіші першими. Гортати може будь-хто.
        app.MapGet("/api/games/pictionary/album", (HttpContext c, long? before, int? n, PictionaryStore store) =>
        {
            var (items, more) = store.Page(before ?? 0, n ?? PictionaryStore.PageSize);
            return Results.Json(new
            {
                items = items.Select(a => new { id = a.Id, word = a.Word, author = a.Author, by = a.By, hearts = a.Hearts, home = a.Home, at = a.At, z = a.Z }),
                more,
                total = store.Count,
                admin = Auth.IsAdmin(c),
            });
        });

        // Адмін прибирає малюнок з альбому.
        app.MapPost("/api/games/pictionary/album/delete", async (HttpContext c, PictionaryStore store, CancellationToken ct) =>
        {
            if (!Auth.IsAdmin(c)) return Results.StatusCode(403);
            var body = await Read<PictionaryDeleteBody>(c, ct);
            if (body is null) return Reply(ActResult.Fail("Нема що прибирати"));
            return Reply(store.Delete(body.Id) ? ActResult.Accept("Прибрано з альбому") : ActResult.Fail("Такого малюнка вже нема"));
        });

        // Найкращі пари «Скільки встигнемо».
        app.MapGet("/api/games/pictionary/pairs", (PictionaryStore store) =>
            Results.Json(new { rows = store.TopPairs(10).Select(p => new { nicks = p.Nicks, best = p.Best, at = p.At }) }));
        return app;
    }

    static async Task<T?> Read<T>(HttpContext c, CancellationToken ct) where T : class
    {
        if (c.Request.ContentLength is > MaxBody) return null;
        // Без Content-Length (chunked) перевірка вище не спрацює — тоді Kestrel сам обірве тіло на MaxBody.
        if (c.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = MaxBody;
        try { return await c.Request.ReadFromJsonAsync<T>(Web, ct); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or BadHttpRequestException or IOException) { return null; }
    }

    static IResult Reply(ActResult r) => Results.Json(new { ok = r.Ok, message = r.Message });
}
