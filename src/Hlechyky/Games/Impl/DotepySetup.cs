using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Голос публіки «Додепів» (specs/dotepy.md §3.2). Глядач не сидить за столом, а <c>Rooms.Act</c> пускає лише
/// тих, хто сидить, тому глядачі голосують через HTTP. Один голос на нік на картку (повторний замінює), лише
/// тим, хто цей стіл справді відкрив, і не частіше за двічі на секунду з ніка.
/// </summary>
public sealed class DotepyJury(Rooms rooms, Presence presence, IClock clock)
{
    /// <summary>Скільки голосів публіки за секунду з одного ніка.</summary>
    public const int PerSecond = 2;

    readonly object _lock = new();
    /// <summary>Останні спроби ніка (ключ — нижній регістр): щоб третя за секунду отримала «Не так швидко».</summary>
    readonly Dictionary<string, Queue<DateTimeOffset>> _tries = new(StringComparer.Ordinal);

    public ActResult Vote(string? nick, string? roomId, int card, int pick) =>
        Pass(nick, roomId, "Ти за столом — голосуй на картці", "Зараз не голосують", game => game.JuryVote(nick!, card, pick));

    /// <summary>«😂» глядача на розкритті — ті самі перевірки й та сама квота, що в голосу публіки.</summary>
    public ActResult Laugh(string? nick, string? roomId, int card, int i) =>
        Pass(nick, roomId, "Ти за столом — смійся на картці", "Зараз не смішно", game => game.JuryLaugh(Auth.NickKey(nick), card, i));

    /// <summary>Спільні перевірки публіки (specs/dotepy.md §3.2) і сам виклик гри під замком кімнати.</summary>
    ActResult Pass(string? nick, string? roomId, string seated, string idle, Func<Dotepy, ActResult> act)
    {
        if (string.IsNullOrWhiteSpace(nick) || Auth.NickKey(nick) == Auth.Guest) return ActResult.Fail("Спершу скажи, як тебе кликати");
        if (rooms.Find(roomId) is not { } room || room.Game is not Dotepy game) return ActResult.Fail("Такого столу вже нема");
        lock (room.Sync)
        {
            if (room.Has(nick)) return ActResult.Fail(seated);
        }
        var watching = false;
        foreach (var conn in presence.ConnectionsOf(nick))
            if (room.Watchers.ContainsKey(conn)) { watching = true; break; }
        if (!watching) return ActResult.Fail("Спершу відкрий цей стіл");
        if (!Allow(Auth.NickKey(nick), clock.UtcNow)) return ActResult.Fail("Не так швидко");
        // Лише замок кімнати (без Rooms._lock) — той самий порядок вкладення, що в Rooms: дедлоку нема.
        lock (room.Sync)
        {
            if (room.Status != RoomStatus.Playing) return ActResult.Fail(idle);
            return act(game);
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
            // словник не росте вічно: раз на якийсь час викидаємо тих, хто давно мовчить
            if (_tries.Count > 512)
                foreach (var k in _tries.Where(p => p.Value.Count == 0 || now - p.Value.Last() > TimeSpan.FromMinutes(1)).Select(p => p.Key).ToList())
                    _tries.Remove(k);
            return true;
        }
    }
}

/// <summary>Тіло запиту голосу публіки: <c>{ room, card, pick }</c>.</summary>
public sealed record DotepyJuryBody(string? Room, int Card, int Pick);

/// <summary>Тіло «😂» глядача: <c>{ room, card, i }</c>.</summary>
public sealed record DotepyLaughBody(string? Room, int Card, int I);

/// <summary>«📌 В альбом»: <c>{ room, i }</c> — номер додепу в трійці найкращих партії.</summary>
public sealed record DotepyPinBody(string? Room, int I);

/// <summary>❤ чи прибирання в альбомі: <c>{ id }</c>.</summary>
public sealed record DotepyAlbumIdBody(long Id);

/// <summary>
/// Підключення «Додепів» одним рядком у <see cref="GamesSetup"/>: голос Глека поверх <see cref="TtsService"/>
/// (його реєструє «Своя гра»), голос публіки і прогрів сталих реплік на старті сервера.
/// </summary>
public static class DotepySetup
{
    public static IServiceCollection AddDotepy(this IServiceCollection services)
    {
        services.AddSingleton<IDotepyVoice, DotepyVoice>();
        services.AddSingleton<DotepyJury>();
        services.AddSingleton(sp => new DotepyAlbum(sp.GetService<Db>(), sp.GetService<ILogger<DotepyAlbum>>()));
        services.AddHostedService<DotepyWarmup>();
        return services;
    }

    /// <summary>Скільки байтів тіла голосу публіки ще приймаємо: там три поля.</summary>
    const int MaxBody = 1024;

    public static WebApplication MapDotepy(this WebApplication app)
    {
        // Відповідь завжди 200 з { ok, message } — браузер показує текст тостом, як відмову хаба.
        app.MapPost("/api/games/dotepy/jury", async (HttpContext c, DotepyJury jury, CancellationToken ct) =>
        {
            DotepyJuryBody? body = null;
            if (c.Request.ContentLength is null or <= MaxBody)
            {
                try { body = await c.Request.ReadFromJsonAsync<DotepyJuryBody>(new JsonSerializerOptions(JsonSerializerDefaults.Web), ct); }
                catch (Exception e) when (e is JsonException or InvalidOperationException or BadHttpRequestException) { }
            }
            var r = body is null ? ActResult.Fail("Тут так не голосують") : jury.Vote(Auth.Nick(c), body.Room, body.Card, body.Pick);
            return Results.Json(new { ok = r.Ok, message = r.Message });
        });
        app.MapPost("/api/games/dotepy/laugh", async (HttpContext c, DotepyJury jury, CancellationToken ct) =>
        {
            DotepyLaughBody? body = null;
            if (c.Request.ContentLength is null or <= MaxBody)
            {
                try { body = await c.Request.ReadFromJsonAsync<DotepyLaughBody>(new JsonSerializerOptions(JsonSerializerDefaults.Web), ct); }
                catch (Exception e) when (e is JsonException or InvalidOperationException or BadHttpRequestException) { }
            }
            var r = body is null ? ActResult.Fail("Тут так не сміються") : jury.Laugh(Auth.Nick(c), body.Room, body.Card, body.I);
            return Results.Json(new { ok = r.Ok, message = r.Message });
        });
        // «📌 В альбом» на підсумку партії: гра під замком лише видає додеп, база — вже поза замком кімнати.
        app.MapPost("/api/games/dotepy/pin", async (HttpContext c, Rooms rooms, DotepyAlbum album, CancellationToken ct) =>
        {
            var body = await Body<DotepyPinBody>(c, ct);
            var nick = Auth.Nick(c);
            if (body is null) return Reply(ActResult.Fail("Тут так не закидають"));
            if (Auth.NickKey(nick) == Auth.Guest) return Reply(ActResult.Fail("Спершу скажи, як тебе кликати"));
            if (rooms.Find(body.Room) is not { } room || room.Game is not Dotepy game) return Reply(ActResult.Fail("Такого столу вже нема"));
            (DotepyAlbumItem? Item, string? Error) got;
            lock (room.Sync) got = game.Pin(nick, body.I);
            if (got.Item is not { } a) return Reply(ActResult.Fail(got.Error ?? "Не вийшло"));
            var id = album.Add(a.Prompt, a.Text, a.Author, a.By, a.Points, a.At);
            return Results.Json(new { ok = true, message = "📌 Додеп в альбомі", id });
        });

        // «📖 Альбом додепів»: гортати може будь-хто (і гість), новіші першими.
        app.MapGet("/api/games/dotepy/album", (HttpContext c, long? before, int? n, DotepyAlbum album) =>
        {
            var key = Auth.NickKey(Auth.Nick(c));
            var (items, more) = album.Page(before ?? 0, n ?? DotepyAlbum.PageSize, key == Auth.Guest ? null : key);
            return Results.Json(new
            {
                items = items.Select(x => new
                {
                    id = x.Item.Id, prompt = x.Item.Prompt, text = x.Item.Text, author = x.Item.Author, by = x.Item.By,
                    points = x.Item.Points, at = x.Item.At, likes = x.Item.Likes, liked = x.Liked,
                }),
                more,
                total = album.Count,
                admin = Auth.IsAdmin(c),
            });
        });

        app.MapPost("/api/games/dotepy/album/like", async (HttpContext c, DotepyAlbum album, CancellationToken ct) =>
        {
            var body = await Body<DotepyAlbumIdBody>(c, ct);
            var key = Auth.NickKey(Auth.Nick(c));
            if (body is null) return Reply(ActResult.Fail("Тут так не лайкають"));
            if (key == Auth.Guest) return Reply(ActResult.Fail("Спершу скажи, як тебе кликати"));
            if (album.Like(body.Id, key) is not { } r) return Reply(ActResult.Fail("Такого додепу вже нема"));
            return Results.Json(new { ok = true, likes = r.Likes, liked = r.Liked });
        });

        // Адмін прибирає додеп з альбому.
        app.MapPost("/api/games/dotepy/album/delete", async (HttpContext c, DotepyAlbum album, CancellationToken ct) =>
        {
            if (!Auth.IsAdmin(c)) return Results.StatusCode(403);
            var body = await Body<DotepyAlbumIdBody>(c, ct);
            if (body is null) return Reply(ActResult.Fail("Нема що прибирати"));
            return Reply(album.Delete(body.Id) ? ActResult.Accept("Прибрано з альбому") : ActResult.Fail("Такого додепу вже нема"));
        });
        return app;
    }

    static async Task<T?> Body<T>(HttpContext c, CancellationToken ct) where T : class
    {
        if (c.Request.ContentLength is > MaxBody) return null;
        // Без Content-Length (chunked) перевірка вище не спрацює — тоді Kestrel сам обірве тіло на MaxBody.
        if (c.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = MaxBody;
        try { return await c.Request.ReadFromJsonAsync<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web), ct); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or BadHttpRequestException or IOException) { return null; }
    }

    static IResult Reply(ActResult r) => Results.Json(new { ok = r.Ok, message = r.Message });

    /// <summary>
    /// На старті сервера: сказати в лог, що з банком (порожній банк — партії не буде), і озвучити наперед усе, що
    /// Глек каже без підстановок, обома голосами — щоб перша ж партія дня не чекала на вступ.
    /// </summary>
    sealed class DotepyWarmup(IDotepyVoice voice, ILogger<DotepyWarmup> log) : IHostedService
    {
        public Task StartAsync(CancellationToken ct)
        {
            var count = DotepyBank.All.Count;
            if (DotepyBank.Problem is { } problem) log.LogWarning("Додепи: банк завдань — {Problem}", problem);
            log.LogInformation("Додепи: у банку {Count} завдань", count);
            try
            {
                if (voice.Enabled)
                {
                    voice.Prepare("ostap", DotepyLines.Pure());
                    voice.Prepare("polina", DotepyLines.Pure());
                }
            }
            catch (Exception ex) { log.LogWarning(ex, "Додепи: прогрів голосу не вдався"); }
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
