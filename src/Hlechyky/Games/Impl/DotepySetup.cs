using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Голос публіки «Дотепів» (specs/dotepy.md §3.2). Глядач не сидить за столом, а <c>Rooms.Act</c> пускає лише
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

/// <summary>
/// Підключення «Дотепів» одним рядком у <see cref="GamesSetup"/>: голос Глека поверх <see cref="TtsService"/>
/// (його реєструє «Своя гра»), голос публіки і прогрів сталих реплік на старті сервера.
/// </summary>
public static class DotepySetup
{
    public static IServiceCollection AddDotepy(this IServiceCollection services)
    {
        services.AddSingleton<IDotepyVoice, DotepyVoice>();
        services.AddSingleton<DotepyJury>();
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
        return app;
    }

    /// <summary>
    /// На старті сервера: сказати в лог, що з банком (порожній банк — партії не буде), і озвучити наперед усе, що
    /// Глек каже без підстановок, обома голосами — щоб перша ж партія дня не чекала на вступ.
    /// </summary>
    sealed class DotepyWarmup(IDotepyVoice voice, ILogger<DotepyWarmup> log) : IHostedService
    {
        public Task StartAsync(CancellationToken ct)
        {
            var count = DotepyBank.All.Count;
            if (DotepyBank.Problem is { } problem) log.LogWarning("Дотепи: банк завдань — {Problem}", problem);
            log.LogInformation("Дотепи: у банку {Count} завдань", count);
            try
            {
                if (voice.Enabled)
                {
                    voice.Prepare("ostap", DotepyLines.Pure());
                    voice.Prepare("polina", DotepyLines.Pure());
                }
            }
            catch (Exception ex) { log.LogWarning(ex, "Дотепи: прогрів голосу не вдався"); }
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
