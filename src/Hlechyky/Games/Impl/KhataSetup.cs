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
        services.AddOptions<KhataOptions>().BindConfiguration("Khata");   // Khata:RiaKey — ключ DIM.RIA (лише в appsettings.Local.json)
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
        // Фон: імпорт світу з Inside Airbnb на прохання розробника й неспішна перевірка, чи фото сторонніх оголошень ще живі.
        services.AddSingleton(sp =>
        {
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible)");
            http.DefaultRequestHeaders.Accept.ParseAdd("*/*");
            // Ключ DIM.RIA читаємо щоразу наново: вписав у appsettings.Local.json — і набір України пішов без перезапуску.
            var options = sp.GetService<Microsoft.Extensions.Options.IOptionsMonitor<KhataOptions>>();
            return new KhataImport(sp.GetRequiredService<KhataStore>(), http, sp.GetService<IClock>() ?? new SystemClock(),
                sp.GetService<ILogger<KhataImport>>(), () => options?.CurrentValue ?? new KhataOptions());
        });
        services.AddHostedService(sp => sp.GetRequiredService<KhataImport>());
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
        api.MapPost("/import", Import);
        api.MapPost("/import/olx", ImportOlx);
        api.MapPost("/broken", Broken);
        api.MapGet("/pic/{file}", (string file, KhataStore store, HttpContext c) => Pic(file, store, c));
        return app;
    }

    static object Flat(KhataFlat f) => new
    {
        id = f.Id, region = f.Region, city = f.City, price = f.Price, unit = f.Unit, per = f.Per, photos = f.Urls.ToArray(),
        by = f.Author, at = f.At, status = f.Status, source = f.Source, link = f.Link, about = f.About,
    };

    /// <summary>POST /api/games/khata/import — розробник просить набрати (чи донабрати) світ з Inside Airbnb. Іде фоном; поступ — у /bank.</summary>
    public static IResult Import(HttpContext c, KhataImport import)
    {
        if (!Auth.IsAdmin(c)) return Results.BadRequest(new { ok = false, message = AdminOnly });
        return import.Start()
            ? Results.Ok(new { ok = true, message = "Набираю світ — це кілька хвилин, поступ видно тут" })
            : Results.BadRequest(new { ok = false, message = "Імпорт уже йде" });
    }

    /// <summary>
    /// POST /api/games/khata/import/olx — розробник набирає оренду з OLX на тиждень: кількасот оголошень, з паузами; старші
    /// за тиждень при цьому зникають. Лише по кнопці — сам сервер на OLX не ходить ніколи.
    /// </summary>
    public static IResult ImportOlx(HttpContext c, KhataImport import)
    {
        if (!Auth.IsAdmin(c)) return Results.BadRequest(new { ok = false, message = AdminOnly });
        return import.StartOlx()
            ? Results.Ok(new { ok = true, message = "Набираю оголошення OLX — хвилини дві-три, поступ видно тут" })
            : Results.BadRequest(new { ok = false, message = "Набір уже йде" });
    }

    public sealed record BrokenRequest(string? Url);

    /// <summary>
    /// POST /api/games/khata/broken { url } — у гравця не відкрилось фото стороннього оголошення. Сервер не вірить на слово:
    /// лише ставить те оголошення в чергу на перевірку (черга коротка, тож заспамити її нема чим).
    /// </summary>
    public static IResult Broken(BrokenRequest b, KhataImport import)
    {
        import.Suspect(b.Url);
        return Results.Ok(new { ok = true });
    }

    /// <summary>
    /// GET /api/games/khata/bank — скільки квартир у грі, мої (з цінами — вони й так мої), а розробнику — всі, спершу ті,
    /// що чекають. Чужих цін звичайний гравець тут не бачить: це ж відповіді.
    /// </summary>
    public static IResult Bank(HttpContext c, KhataStore store, IServiceProvider sp)
    {
        var account = Auth.IsUser(c);
        var admin = Auth.IsAdmin(c);
        var economy = sp.GetService<Hlechyky.Games.Economy.Economy>();
        var import = sp.GetService<KhataImport>();
        var state = import?.State;
        var ria = import?.Ria;
        c.Response.Headers.CacheControl = "no-store";
        return Results.Json(new
        {
            total = store.Live.Count,
            byRegion = new
            {
                ua = store.ByRegion.GetValueOrDefault(KhataRegions.Ua),
                eu = store.ByRegion.GetValueOrDefault(KhataRegions.Eu),
                world = store.ByRegion.GetValueOrDefault(KhataRegions.World),
            },
            // скільки з них «хат мрії» (замки, біля моря, вілли з басейном) — для опції столу «Добірка»
            dreamByRegion = new
            {
                ua = store.DreamByRegion.GetValueOrDefault(KhataRegions.Ua),
                eu = store.DreamByRegion.GetValueOrDefault(KhataRegions.Eu),
                world = store.DreamByRegion.GetValueOrDefault(KhataRegions.World),
            },
            // звідки квартири: від гравців і зі сторонніх джерел
            bySource = new
            {
                own = store.BySource.GetValueOrDefault(KhataSources.Own),
                airbnb = store.BySource.GetValueOrDefault(KhataSources.Airbnb),
                ria = store.BySource.GetValueOrDefault(KhataSources.Ria),
                olx = store.BySource.GetValueOrDefault(KhataSources.Olx),
            },
            // набір OLX на тиждень — лише розробнику
            olx = !admin || import is null ? null : new
            {
                running = import.Olx.Running, done = import.Olx.Done, total = import.Olx.Total, added = import.Olx.Added,
                message = import.Olx.Message, finishedAt = import.Olx.FinishedAt, days = (int)KhataOlx.Life.TotalDays,
            },
            // набір України з DIM.RIA — лише розробнику: чи є ключ і що зараз відбувається
            ria = !admin || ria is null ? null : new { key = ria.HasKey, added = ria.Added, message = ria.Message, at = ria.At },
            // поступ імпорту світу — лише розробнику (він його й запускає)
            import = !admin || state is null ? null : new
            {
                running = state.Running, done = state.Done, total = state.Total, added = state.Added, updated = state.Updated,
                message = state.Message, finishedAt = state.FinishedAt,
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
