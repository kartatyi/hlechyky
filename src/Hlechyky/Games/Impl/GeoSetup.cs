using System.Globalization;
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
            var yt = sp.GetService<IOptionsMonitor<YtDlpOptions>>()?.CurrentValue;
            var cache = yt?.CacheDir ?? "cache";
            var log = sp.GetService<ILogger<GeoPhotos>>();
            return new GeoPhotos(GeoBank.Load(Paths.Resolve(GeoBank.FileName), log), Path.Combine(Paths.Resolve(cache), "geo"),
                sp.GetService<IClock>() ?? new SystemClock(), log)
            {
                Ffmpeg = yt is null ? null : Path.Combine(Paths.Resolve(yt.FfmpegDir), OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"),
            };
        });
        services.AddSingleton(sp => new GeoSeen(sp.GetService<Db>()));
        services.AddSingleton(sp =>
        {
            var yt = sp.GetService<IOptionsMonitor<YtDlpOptions>>()?.CurrentValue;
            return new GeoMine(sp.GetService<Db>(), Paths.Resolve(MineDir),
                yt is null ? null : Path.Combine(Paths.Resolve(yt.FfmpegDir), OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"),
                sp.GetService<ILogger<GeoMine>>());
        });
        services.AddHostedService(sp => sp.GetRequiredService<GeoPhotos>());
        return services;
    }

    public static WebApplication MapGeo(WebApplication app)
    {
        app.MapGet("/api/games/geo/{file}", (string file, GeoPhotos photos, HttpContext c) => Serve(file, photos, c));
        // «📷 Мої фото» (п. 52)
        app.MapGet("/api/games/geo/mine", (HttpContext c, GeoMine mine) => MineList(c, mine));
        app.MapGet("/api/games/geo/mine/{file}", (string file, HttpContext c, GeoMine mine) => MineFile(file, c, mine));
        app.MapPost("/api/games/geo/mine", (HttpContext c, GeoMine mine, IClock clock) => MineAdd(c, mine, clock));
        app.MapPost("/api/games/geo/mine/delete", (HttpContext c, GeoMine mine, string? id) =>
            MineReply(mine.Delete(id, Auth.Nick(c), Auth.IsAdmin(c))));
        return app;
    }

    /// <summary>Тека фото друзів (під <c>data/*</c> — у гіт не йде).</summary>
    public const string MineDir = "data/geo-mine";

    /// <summary>Гостям — ні: фото лишається за ніком, і прибрати його має змогу лише той самий нік.</summary>
    public static bool CanUpload(HttpContext c) => !Auth.IsGuestNick(Auth.Nick(c));

    /// <summary>
    /// GET /api/games/geo/mine — свої фото (адміну — усі) з лічильниками лімітів. Жодних чужих координат і назв
    /// нікому, крім адміна: це відповіді до гри.
    /// </summary>
    public static IResult MineList(HttpContext c, GeoMine mine)
    {
        var admin = Auth.IsAdmin(c);
        var nick = Auth.Nick(c);
        var list = admin ? mine.All.ToList() : CanUpload(c) ? mine.Of(nick) : [];
        list.Reverse();
        c.Response.Headers.CacheControl = "no-store";
        return Results.Json(new
        {
            can = CanUpload(c),
            admin,
            max = GeoMine.MaxPerNick,
            have = CanUpload(c) ? mine.Of(nick).Count : 0,
            full = mine.TotalBytes + GeoMine.MaxFile > mine.TotalCap,
            items = list.Select(p => new
            {
                id = p.Id,
                url = $"/api/games/geo/mine/{p.Id}.jpg",
                nick = p.Nick,
                own = p.NickKey == Auth.NickKey(nick),
                title = p.Title,
                story = p.Story,
                region = p.Region,
                x = p.X,
                y = p.Y,
                at = p.At,
                kb = (p.Bytes + 512) / 1024,
            }),
        });
    }

    /// <summary>GET /api/games/geo/mine/&lt;12 hex&gt;.jpg — лише автору чи адміну: іншим це спойлер гри (у грі — за токеном).</summary>
    public static IResult MineFile(string file, HttpContext c, GeoMine mine)
    {
        if (!file.EndsWith(".jpg", StringComparison.Ordinal) || mine.Resolve(file[..^4]) is not { } path) return Results.NotFound();
        var p = mine.Find(file[..^4])!;
        if (!Auth.IsAdmin(c) && (!CanUpload(c) || p.NickKey != Auth.NickKey(Auth.Nick(c)))) return Results.NotFound();
        if (!File.Exists(path)) return Results.NotFound();
        c.Response.Headers.CacheControl = "private, max-age=3600";
        c.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.File(path, "image/jpeg");
    }

    /// <summary>
    /// POST /api/games/geo/mine?x=&amp;y=&amp;title=&amp;story= — тіло запиту й є фото. Шпилька — точка сітки мапи
    /// (координати з EXIF не беремо ніколи). Тіло довше за <see cref="GeoMine.MaxBody"/> не дочитуємо.
    /// </summary>
    public static async Task<IResult> MineAdd(HttpContext c, GeoMine mine, IClock clock)
    {
        if (!CanUpload(c)) return MineReply(new(false, "Спершу скажи, як тебе кликати, — фото лишається за ніком"));
        if (c.Request.ContentLength is > GeoMine.MaxBody) return MineReply(new(false, $"Завелике фото: до {GeoMine.MaxBody / 1024} КБ"));
        var q = c.Request.Query;
        if (!int.TryParse(q["x"], NumberStyles.None, CultureInfo.InvariantCulture, out var x)
            || !int.TryParse(q["y"], NumberStyles.None, CultureInfo.InvariantCulture, out var y))
            return MineReply(new(false, GeoMine.NotLand));
        var body = await ReadCapped(c.Request.Body, GeoMine.MaxBody + 1, c.RequestAborted);
        var r = await mine.AddAsync(Auth.Nick(c), body, x, y, q["title"], q["story"], clock.UtcNow, c.RequestAborted);
        return MineReply(r);
    }

    static IResult MineReply(GeoMineReply r) => Results.Json(new { ok = r.Ok, message = r.Message, id = r.Photo?.Id });

    static async Task<byte[]> ReadCapped(Stream body, int max, CancellationToken ct)
    {
        var buf = new byte[max];
        var n = 0;
        while (n < max)
        {
            var got = await body.ReadAsync(buf.AsMemory(n, max - n), ct);
            if (got == 0) break;
            n += got;
        }
        return buf[..n];
    }

    /// <summary>
    /// Фото за токеном. Лише «&lt;24 hex&gt;.jpg» живого токена — інакше 404 (жодних списків теки, жодних імен
    /// файлів ззовні, жодного «..»); віддане — <c>private</c> (токен особистий, спільним кешам ні до чого) і
    /// <c>nosniff</c>.
    /// </summary>
    public static IResult Serve(string file, GeoPhotos photos, HttpContext c)
    {
        if (file.EndsWith(".bin", StringComparison.Ordinal)) return Sealed(file[..^4], photos, c);
        if (!file.EndsWith(".jpg", StringComparison.Ordinal)) return Results.NotFound();
        if (photos.Resolve(file[..^4]) is not { } path || !File.Exists(path)) return Results.NotFound();
        GeoPhotos.Touch(path);
        c.Response.Headers.CacheControl = "private, max-age=1800";
        c.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.File(path, "image/jpeg");
    }

    /// <summary>
    /// Запечатане фото наступного раунду (<see cref="GeoPhotos.IssueSealed"/>): шифр AES-GCM, ключ до якого
    /// приходить лише у «Готуйсь». Шифруємо тут, у запиті, а не під замком кімнати.
    /// </summary>
    static IResult Sealed(string token, GeoPhotos photos, HttpContext c)
    {
        if (photos.ResolveSealed(token) is not { } hit || !File.Exists(hit.Path)) return Results.NotFound();
        byte[] plain;
        try { plain = File.ReadAllBytes(hit.Path); }
        catch (IOException) { return Results.NotFound(); }
        c.Response.Headers.CacheControl = "private, max-age=1800";
        c.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.Bytes(GeoPhotos.Seal(plain, hit.Key), "application/octet-stream");
    }
}
