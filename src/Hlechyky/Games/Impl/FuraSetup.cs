using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>
/// HTTP і сервіси «Чумаків-далекобійників»: <c>GET /api/games/fura/map/{id}</c> — мапа як є (браузер будує з неї світ;
/// лише id зі списку <see cref="FuraMap.Known"/>, шлях до файлу з клієнта не будується; gzip і ETag — Житомир важить
/// 180 КБ), <c>GET /api/games/fura/maps</c> — які мапи можна обрати столом (вимикач <c>Fura:Maps</c>), <c>GET /api/games/fura/voice</c> —
/// голос Глека-диспетчера (<see cref="FuraVoice"/>) і <c>GET /api/games/fura/garage</c> — книга гаража 0.0.6
/// (<see cref="FuraBook"/>). Кличеться рядками з <see cref="GamesSetup"/>.
/// </summary>
public static class FuraSetup
{
    public const string Root = "/api/games/fura";

    public static IServiceCollection AddFura(this IServiceCollection services)
    {
        services.AddSingleton<FuraVoice>();
        services.AddSingleton(sp => new FuraGarage(sp.GetRequiredService<IGameStore>(), log: sp.GetService<ILogger<FuraGarage>>()));   // фури гравців (етап 2)
        services.AddOptions<FuraOptions>().BindConfiguration("Fura");   // Fura:Garage — вимикач гаража 0.0.6
        // гараж 0.0.6: акаунт (купувати — лише акаунтам) і вигляд фури з Лавки; Лавку беремо ліниво — вона тягне пів сервера
        services.AddSingleton(sp => new FuraWardrobe(
            nick => sp.GetService<Db>()?.FindAccount(nick) is not null,
            nick => sp.GetService<Lavka>()?.FuraWorn(nick) ?? new Dictionary<string, string>()));
        services.AddHostedService<FuraWarmup>();
        return services;
    }

    public static WebApplication MapFura(WebApplication app)
    {
        app.MapGet(Root + "/map/{id}", (string id, HttpContext c) => Map(id, c));
        app.MapGet(Root + "/maps", (IOptionsMonitor<FuraOptions> opts, HttpContext c) =>
        {
            // вимикач міняється з конфігом без деплою — браузер перепитує
            c.Response.Headers.CacheControl = "no-cache";
            return Results.Json(Maps(opts.CurrentValue));
        });
        app.MapGet(Root + "/garage", (HttpContext c) =>
        {
            // числа гаража міняються лише з деплоєм — браузер перепитує, а не тримає старі
            c.Response.Headers.CacheControl = "no-cache";
            return Results.Text(FuraBook.Load().Raw, "application/json; charset=utf-8");
        });
        app.MapGet(Root + "/voice", (FuraVoice voice, HttpContext c) =>
        {
            // готові фрази додаються, поки воркер озвучує — браузер перепитує, а не тримає порожнє
            c.Response.Headers.CacheControl = "no-cache";
            return Results.Json(voice.Snapshot());
        });
        return app;
    }

    /// <summary>Мапи, які можна обрати опцією столу <see cref="Fura.MapKey"/>: <c>{ maps: [{id, title}], def }</c>.</summary>
    public static object Maps(FuraOptions opts) => new
    {
        maps = opts.AllowedMaps().Select(id => new { id, title = FuraMap.Raw(id) is null ? id : FuraMap.Get(id).Title }).ToArray(),
        def = FuraMap.DefaultId,
    };

    const string JsonType = "application/json; charset=utf-8";

    /// <summary>Мапа, готова до віддачі: текст, він же стиснутий, і ETag (слабкий — той самий для обох кодувань).</summary>
    sealed record Packed(string Json, byte[] Gzip, string ETag);

    static readonly ConcurrentDictionary<string, Lazy<Packed?>> Packs = new(StringComparer.Ordinal);

    static Packed? Pack(string id) => !FuraMap.Known.Contains(id) ? null : Packs.GetOrAdd(id, k => new Lazy<Packed?>(() =>
    {
        if (FuraMap.Raw(k) is not { } json) return null;
        var bytes = Encoding.UTF8.GetBytes(json);
        using var gz = new MemoryStream();
        using (var z = new GZipStream(gz, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(bytes);
        return new Packed(json, gz.ToArray(), $"W/\"{Convert.ToHexStringLower(SHA256.HashData(bytes))[..16]}\"");
    })).Value;

    /// <summary>
    /// Мапа за id: відомий — JSON як у файлі, інакше 404. З HTTP: <c>no-cache</c> + ETag (мапа міняється лише з деплоєм —
    /// браузер перепитує, а збіг дає 304 без тіла), gzip, коли браузер його бере.
    /// </summary>
    public static IResult Map(string id, HttpContext? c = null)
    {
        if (Pack(id) is not { } p) return Results.NotFound();
        if (c is null) return Results.Text(p.Json, JsonType);
        var h = c.Response.Headers;
        h.CacheControl = "no-cache";
        h.ETag = p.ETag;
        h.Vary = "Accept-Encoding";
        if (c.Request.Headers.IfNoneMatch.Any(v => v is not null && v.Split(',').Any(t => t.Trim() == p.ETag || t.Trim() == "*")))
            return Results.StatusCode(StatusCodes.Status304NotModified);
        if (c.Request.Headers.AcceptEncoding.Any(v => v?.Contains("gzip", StringComparison.OrdinalIgnoreCase) == true))
        {
            h.ContentEncoding = "gzip";
            return Results.Bytes(p.Gzip, JsonType);
        }
        return Results.Text(p.Json, JsonType);
    }

    /// <summary>
    /// Прогрів на старті: голос — перший рейс дня не має чекати, поки Глек «прочистить горло»; мапи — розбір і маршрути
    /// між місцями з пулу потоків, щоб перший стіл не чекав диска.
    /// </summary>
    sealed class FuraWarmup(FuraVoice voice, TtsService tts, ILogger<FuraWarmup> log) : IHostedService
    {
        public Task StartAsync(CancellationToken ct)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                foreach (var id in FuraMap.Known)
                    try { Pack(id); }
                    catch (Exception ex) { log.LogWarning(ex, "Чумаки: мапа {Map} не прогрілась", id); }
            });
            try { if (tts.Enabled) voice.Warm(); }
            catch (Exception ex) { log.LogWarning(ex, "Чумаки: прогрів голосу не вдався"); }
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
