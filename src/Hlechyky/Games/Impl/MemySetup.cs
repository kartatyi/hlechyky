using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Підключення «Мемології»: банк мемів, фоновий завантажувач картинок (<see cref="MemyImages"/>, кеш <c>cache/memes</c>)
/// і роздача кадрів за токеном <c>GET /api/games/memy/&lt;24 hex&gt;.jpg</c>. Кличеться двома рядками з <see cref="GamesSetup"/>.
/// </summary>
public static class MemySetup
{
    public static IServiceCollection AddMemy(IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var yt = sp.GetService<IOptionsMonitor<YtDlpOptions>>()?.CurrentValue;
            var log = sp.GetService<ILogger<MemyImages>>();
            var ffmpeg = yt is null ? null : Tool(yt, "ffmpeg");
            // кадри з відео — через той самий yt-dlp, що й у Мелодії (YtDlp:BinaryPath, куки для 18+)
            var frames = ffmpeg is not null && sp.GetService<YtDlpService>() is { } ytdlp ? new MemyVideoFrames(ytdlp, ffmpeg, log) : null;
            return new MemyImages(MemyBank.Load(Paths.Resolve(MemyBank.FileName), log),
                Path.Combine(Paths.Resolve(yt?.CacheDir ?? "cache"), "memes"), sp.GetService<IClock>() ?? new SystemClock(),
                ffmpeg, log, frames: frames);
        });
        services.AddHostedService(sp => sp.GetRequiredService<MemyImages>());
        return services;
    }

    /// <summary>ffmpeg з тієї ж теки, що й для ефіру (як <c>VoiceService.Tool</c>).</summary>
    static string Tool(YtDlpOptions yt, string name) =>
        Path.Combine(Paths.Resolve(yt.FfmpegDir), OperatingSystem.IsWindows() ? name + ".exe" : name);

    public static WebApplication MapMemy(WebApplication app)
    {
        // адміну — що з банку готове, а що не скачалось (і чому): список на перегляд посилань
        app.MapGet("/api/games/memy/status", (HttpContext c, MemyImages images) => Status(c, images));
        app.MapGet("/api/games/memy/{file}", (string file, MemyImages images, HttpContext c) => Serve(file, images, c));
        return app;
    }

    public static IResult Status(HttpContext c, MemyImages images)
    {
        if (!Auth.IsAdmin(c)) return Results.NotFound();
        c.Response.Headers.CacheControl = "no-store";
        return Results.Json(new
        {
            memes = images.Bank.Memes.Count,
            ready = images.ReadyCount,
            video = images.Bank.Memes.Count(m => m.IsVideo),
            rejected = images.Bank.Problems,
            // url — адреса картинки чи «відео&t=Ns»; asleep — до рестарту не пробуватиметься, false — ще спробує (мережа, 429)
            failed = images.Failed.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new { id = kv.Key, name = images.Bank.Find(kv.Key)?.Name, url = images.Bank.Find(kv.Key)?.Source, why = kv.Value, asleep = images.Gone(kv.Key) }),
        });
    }

    /// <summary>
    /// Кадр за токеном. Лише «&lt;24 hex&gt;.jpg» живого токена — інакше 404 (жодних списків теки, жодних імен файлів
    /// ззовні, жодного «..»); віддане — <c>private</c> і <c>nosniff</c>.
    /// </summary>
    public static IResult Serve(string file, MemyImages images, HttpContext c)
    {
        if (!file.EndsWith(".jpg", StringComparison.Ordinal)) return Results.NotFound();
        if (images.Resolve(file[..^4]) is not { } path || !File.Exists(path)) return Results.NotFound();
        GeoPhotos.Touch(path);
        c.Response.Headers.CacheControl = "private, max-age=1800";
        c.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.File(path, "image/jpeg");
    }
}
