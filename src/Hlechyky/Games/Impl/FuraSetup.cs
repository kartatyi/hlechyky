namespace Hlechyky.Games.Impl;

/// <summary>
/// HTTP і сервіси «Чумаків-далекобійників»: <c>GET /api/games/fura/map/{id}</c> — мапа як є (браузер будує з неї світ;
/// лише id зі списку <see cref="FuraMap.Known"/>, шлях до файлу з клієнта не будується) і <c>GET /api/games/fura/voice</c> —
/// голос Глека-диспетчера (<see cref="FuraVoice"/>). Кличеться рядками з <see cref="GamesSetup"/>.
/// </summary>
public static class FuraSetup
{
    public const string Root = "/api/games/fura";

    public static IServiceCollection AddFura(this IServiceCollection services)
    {
        services.AddSingleton<FuraVoice>();
        services.AddSingleton(sp => new FuraGarage(sp.GetRequiredService<IGameStore>(), log: sp.GetService<ILogger<FuraGarage>>()));   // фури гравців (етап 2)
        services.AddHostedService<FuraWarmup>();
        return services;
    }

    public static WebApplication MapFura(WebApplication app)
    {
        app.MapGet(Root + "/map/{id}", (string id, HttpContext c) => Map(id, c));
        app.MapGet(Root + "/voice", (FuraVoice voice, HttpContext c) =>
        {
            // готові фрази додаються, поки воркер озвучує — браузер перепитує, а не тримає порожнє
            c.Response.Headers.CacheControl = "no-cache";
            return Results.Json(voice.Snapshot());
        });
        return app;
    }

    /// <summary>Мапа за id: відомий — JSON як у файлі, інакше 404.</summary>
    public static IResult Map(string id, HttpContext? c = null)
    {
        if (FuraMap.Raw(id) is not { } json) return Results.NotFound();
        // мапа міняється лише з деплоєм — браузер перепитує, а не тримає стару
        if (c is not null) c.Response.Headers.CacheControl = "no-cache";
        return Results.Text(json, "application/json; charset=utf-8");
    }

    /// <summary>Прогрів голосу на старті: перший рейс дня не має чекати, поки Глек «прочистить горло».</summary>
    sealed class FuraWarmup(FuraVoice voice, TtsService tts, ILogger<FuraWarmup> log) : IHostedService
    {
        public Task StartAsync(CancellationToken ct)
        {
            try { if (tts.Enabled) voice.Warm(); }
            catch (Exception ex) { log.LogWarning(ex, "Чумаки: прогрів голосу не вдався"); }
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
