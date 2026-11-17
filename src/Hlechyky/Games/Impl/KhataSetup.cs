using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Підключення «Здам хату» (specs/khata.md): банк квартир (<see cref="KhataStore"/>) і його ендпоінти — що в банку й
/// «мої», додати квартиру з фото, розробник схвалює / виправляє / знімає, віддача фото.
/// </summary>
public static class KhataSetup
{
    /// <summary>Тіло «додати квартиру»: п'ять фото в base64 (× 4/3) і трохи JSON навколо.</summary>
    public const int MaxBody = KhataStore.MaxPhotos * KhataStore.MaxBytes * 4 / 3 + 64 * 1024;

    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static IServiceCollection AddKhata(IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var economy = sp.GetService<Hlechyky.Games.Economy.Economy>();
            return new KhataStore(sp.GetService<Db>(), Paths.Resolve(KhataStore.Dir), sp.GetService<IClock>() ?? new SystemClock(),
                sp.GetService<ILogger<KhataStore>>())
            {
                // ref на квартиру: схвалили, зняли й додали знову — це вже інша квартира (інший id), а та сама двічі не платить
                Reward = economy is null ? null : (nick, n, refKey) =>
                    economy.Grant(nick, economy.Earn(n), "Здам хату — квартиру схвалено", refKey),
            };
        });
        return services;
    }

    public static WebApplication MapKhata(WebApplication app)
    {
        var api = app.MapGroup("/api/games/khata");
        api.MapGet("/bank", Bank);
        api.MapPost("/flat", Add);
        api.MapPost("/flat/approve", Approve);
        api.MapPost("/flat/edit", Edit);
        api.MapPost("/flat/remove", Remove);
        api.MapGet("/pic/{file}", (string file, KhataStore store, HttpContext c) => Pic(file, store, c));
        return app;
    }

    static object Flat(KhataFlat f) => new
    {
        id = f.Id, region = f.Region, city = f.City, price = f.Price, unit = f.Unit, photos = f.Urls.ToArray(),
        by = f.Author, at = f.At, status = f.Status,
    };

    /// <summary>
    /// GET /api/games/khata/bank — скільки квартир у грі, мої (з цінами — вони й так мої), а розробнику — всі, спершу ті,
    /// що чекають. Чужих цін звичайний гравець тут не бачить: це ж відповіді.
    /// </summary>
    public static IResult Bank(HttpContext c, KhataStore store, IServiceProvider sp)
    {
        var account = Auth.IsUser(c);
        var admin = Auth.IsAdmin(c);
        var economy = sp.GetService<Hlechyky.Games.Economy.Economy>();
        c.Response.Headers.CacheControl = "no-store";
        return Results.Json(new
        {
            total = store.Live.Count,
            byRegion = new
            {
                ua = store.Live.Count(f => f.Region == KhataRegions.Ua),
                eu = store.Live.Count(f => f.Region == KhataRegions.Eu),
                world = store.Live.Count(f => f.Region == KhataRegions.World),
            },
            account,
            admin,
            reward = economy?.Earn(KhataStore.AuthorReward) ?? KhataStore.AuthorReward,
            limits = new { photos = KhataStore.MaxPhotos, bytes = KhataStore.MaxBytes, side = KhataStore.MaxSide, city = KhataStore.MaxCity },
            mine = account ? store.Mine(Auth.Nick(c)).Select(Flat) : [],
            pending = admin ? store.PendingCount : 0,
            all = admin ? store.Everything().Select(Flat) : [],
        });
    }

    public sealed record AddRequest(string? Region, string? City, long Price, string[]? Photos);
    public sealed record IdRequest(long Id);
    public sealed record EditRequest(long Id, string? Region, string? City, long Price);

    /// <summary>
    /// POST /api/games/khata/flat { region, city, price, photos: [base64…] } — фото вже стиснуті браузером. Лише акаунт.
    /// → { ok, message, flat? }.
    /// </summary>
    public static async Task<IResult> Add(HttpContext c, KhataStore store)
    {
        var body = await ReadCapped(c.Request.Body, MaxBody + 1, c.RequestAborted);
        if (body.Length > MaxBody) return Reply(new(false, "Забагато даних: до п'яти фото, кожне до 400 КБ"));
        AddRequest? req;
        try { req = JsonSerializer.Deserialize<AddRequest>(body, Web); }
        catch (JsonException) { req = null; }
        if (req is null) return Reply(new(false, "Запит не розібрався — онови сторінку й спробуй ще раз"));
        var photos = new List<byte[]>();
        foreach (var p in req.Photos ?? [])
        {
            try { photos.Add(Convert.FromBase64String(p ?? "")); }
            catch (FormatException) { return Reply(new(false, "Одне з фото зіпсувалось дорогою — обери ще раз")); }
        }
        return Reply(store.Add(Auth.Nick(c), Auth.IsUser(c), Auth.IsAdmin(c), req.Region, req.City, req.Price, photos));
    }

    const string AdminOnly = "Це робить лише розробник";

    /// <summary>POST /api/games/khata/flat/approve { id } — розробник пускає квартиру в гру.</summary>
    public static IResult Approve(HttpContext c, IdRequest b, KhataStore store) =>
        Reply(Auth.IsAdmin(c) ? store.Approve(b.Id) : new(false, AdminOnly));

    /// <summary>POST /api/games/khata/flat/edit { id, region, city, price } — розробник виправляє одруківку.</summary>
    public static IResult Edit(HttpContext c, EditRequest b, KhataStore store) =>
        Reply(Auth.IsAdmin(c) ? store.Edit(b.Id, b.Region, b.City, b.Price) : new(false, AdminOnly));

    /// <summary>POST /api/games/khata/flat/remove { id } — автор знімає свою, розробник — будь-яку.</summary>
    public static IResult Remove(HttpContext c, IdRequest b, KhataStore store) =>
        Reply(store.Remove(b.Id, Auth.Nick(c), Auth.IsUser(c), Auth.IsAdmin(c)));

    /// <summary>GET /api/games/khata/pic/&lt;хеш&gt;.&lt;jpg|png|webp&gt; — фото; тип за магічними байтами, кеш на рік (нове фото — нове ім'я).</summary>
    public static IResult Pic(string file, KhataStore store, HttpContext c)
    {
        if (store.Resolve(file) is not { } path) return Results.NotFound();
        Span<byte> head = stackalloc byte[16];
        int n;
        try
        {
            using var f = File.OpenRead(path);
            n = f.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }
        catch (IOException) { return Results.NotFound(); }
        if (LavkaImage.MimeOf(head[..n]) is not { } mime) return Results.NotFound();
        c.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        c.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.File(path, mime);
    }

    static IResult Reply(KhataReply r)
    {
        var body = new { ok = r.Ok, message = r.Message, flat = r.Flat is { } f ? Flat(f) : null };
        return r.Ok ? Results.Ok(body) : Results.BadRequest(body);
    }

    static async Task<byte[]> ReadCapped(Stream body, int max, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        while (ms.Length < max)
        {
            var k = await body.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, max - ms.Length)), ct);
            if (k == 0) break;
            ms.Write(buf, 0, k);
        }
        return ms.ToArray();
    }
}
