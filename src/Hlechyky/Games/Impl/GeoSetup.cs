using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Підключення «Де це?»: фоновий завантажувач фото (<see cref="GeoPhotos"/>, кеш <c>cache/geo</c>) і роздача
/// фото за токеном <c>GET /api/games/geo/&lt;24 hex&gt;.jpg</c>. Кличеться двома рядками з <see cref="GamesSetup"/>.
/// </summary>
public static class GeoSetup
{
    public static IServiceCollection AddGeo(IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var cache = sp.GetService<IOptionsMonitor<YtDlpOptions>>()?.CurrentValue.CacheDir ?? "cache";
            var log = sp.GetService<ILogger<GeoPhotos>>();
            return new GeoPhotos(GeoBank.Load(Paths.Resolve(GeoBank.FileName), log), Path.Combine(Paths.Resolve(cache), "geo"),
                sp.GetService<IClock>() ?? new SystemClock(), log);
        });
        services.AddHostedService(sp => sp.GetRequiredService<GeoPhotos>());
        return services;
    }

    public static WebApplication MapGeo(WebApplication app)
    {
        app.MapGet("/api/games/geo/{file}", (string file, GeoPhotos photos, HttpContext c) =>
        {
            // Лише «<токен>.jpg»: жодних списків теки, жодних імен файлів ззовні.
            if (!file.EndsWith(".jpg", StringComparison.Ordinal)) return Results.NotFound();
            if (photos.Resolve(file[..^4]) is not { } path || !File.Exists(path)) return Results.NotFound();
            GeoPhotos.Touch(path);
            c.Response.Headers.CacheControl = "private, max-age=1800";
            c.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(path, "image/jpeg");
        });
        return app;
    }
}
