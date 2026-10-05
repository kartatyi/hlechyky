namespace Hlechyky.Games.Impl;

/// <summary>
/// Підключення «Склей глек» (specs/sklei.md §6): колода (свої картинки, малюнки Піктіонарі, фото «Де це?») і ендпоінти —
/// колода й «мої», завантаження своєї картинки за черепки, сховати свою, адмін знімає, віддача файлів і малюнків.
/// </summary>
public static class SkleiSetup
{
    public const string Dir = "data/sklei";

    public static IServiceCollection AddSklei(IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var economy = sp.GetService<Hlechyky.Games.Economy.Economy>();
            var achievements = sp.GetService<Hlechyky.Games.Economy.Achievements>();
            return new SkleiDeck(sp.GetService<Db>(), Paths.Resolve(Dir), sp.GetService<IClock>() ?? new SystemClock(),
                sp.GetService<PictionaryStore>(), sp.GetService<ILogger<SkleiDeck>>())
            {
                Geo = sp.GetService<GeoPhotos>(),
                Spend = economy is null ? null : (nick, n, reason, refKey) => economy.TrySpend(nick, n, reason, refKey),
                Grant = economy is null ? null : (nick, n, reason, refKey) =>
                    economy.GrantCapped(nick, n, reason, refKey, "sklei-author", SkleiDeck.AuthorDailyCap),
                Unlock = achievements is null ? null : (nick, key) => achievements.Unlock(nick, key),
            };
        });
        // Фон: +1 автору й лічильники ачівок з партій, раз на 5 хв — які малюнки є в альбомі.
        services.AddHostedService(sp => sp.GetRequiredService<SkleiDeck>());
        return services;
    }

    public static WebApplication MapSklei(WebApplication app)
    {
        var api = app.MapGroup("/api/games/sklei");
        api.MapGet("/deck", Deck);
        api.MapGet("/mine", Mine);
        api.MapPost("/pic", Upload);
        api.MapPost("/pic/hide", Hide);
        api.MapPost("/pic/remove", Remove);
        api.MapGet("/pic/{file}", (string file, SkleiDeck deck, HttpContext c) => File(file, deck, c));
        api.MapGet("/art/{id:long}", (long id, SkleiDeck deck) => Art(id, deck));
        return app;
    }

    static object Own(SkleiOwn o) => new { id = o.Id, url = o.Url, by = o.Author, at = o.At, hidden = o.Hidden, solved = o.Solved };

    /// <summary>
    /// GET /api/games/sklei/deck — що є в колоді: скільки вбудованих, фото, малюнків, і всі свої картинки (свіжі згори).
    /// <c>admin</c> — показати «🗑» біля кожної, <c>price</c>/<c>balance</c>/<c>account</c> — для кнопки «Своя картинка».
    /// </summary>
    public static IResult Deck(HttpContext c, SkleiDeck deck, IServiceProvider sp)
    {
        var nick = Auth.Nick(c);
        var account = Auth.IsUser(c);
        var balance = account && sp.GetService<Hlechyky.Games.Economy.Economy>() is { } eco ? eco.Balance(nick) : 0;
        return Results.Json(new
        {
            builtin = SkleiBuiltin.All.Select(b => new { id = b.Id, title = b.Title, url = SkleiBuiltin.Picture(b.Id, b.Title).Url }),
            photos = deck.PhotoPlaces,
            arts = deck.Arts.Count,
            own = deck.Own.OrderByDescending(o => o.Id).Select(Own),
            price = SkleiDeck.Price,
            account,
            balance,
            admin = Auth.IsAdmin(c),
        });
    }

    /// <summary>GET /api/games/sklei/mine — мої картинки, і сховані теж.</summary>
    public static IResult Mine(HttpContext c, SkleiDeck deck) => Results.Json(new
    {
        account = Auth.IsUser(c),
        items = Auth.IsUser(c) ? deck.Mine(Auth.Nick(c)).Select(Own) : [],
    });

    /// <summary>
    /// POST /api/games/sklei/pic — тіло запиту й є картинка (JPEG/PNG/WebP, квадрат ≤ 768, ≤ 300 КБ), обрізана браузером.
    /// Коштує <see cref="SkleiDeck.Price"/> черепків, лише акаунт. → { ok, message, pic? }.
    /// </summary>
    public static async Task<IResult> Upload(HttpContext c, SkleiDeck deck)
    {
        var bytes = await ReadCapped(c.Request.Body, SkleiDeck.MaxBytes + 1, c.RequestAborted);
        return Reply(deck.Upload(Auth.Nick(c), Auth.IsUser(c), bytes));
    }

    public sealed record HideRequest(long Id, bool Hidden);
    public sealed record RemoveRequest(long Id);

    /// <summary>POST /api/games/sklei/pic/hide { id, hidden } — сховати свою картинку з пулу (чи повернути).</summary>
    public static IResult Hide(HttpContext c, HideRequest b, SkleiDeck deck) =>
        Reply(deck.Hide(Auth.Nick(c), Auth.IsUser(c), b.Id, b.Hidden));

    /// <summary>POST /api/games/sklei/pic/remove { id } — розробник знімає будь-яку картинку (премодерації нема).</summary>
    public static IResult Remove(HttpContext c, RemoveRequest b, SkleiDeck deck) => !Auth.IsAdmin(c)
        ? Results.BadRequest(new { ok = false, message = "Знімає лише розробник" })
        : Reply(deck.Remove(b.Id));

    /// <summary>GET /api/games/sklei/pic/&lt;хеш&gt;.&lt;jpg|png|webp&gt; — сам файл; тип за магічними байтами, кеш на рік (нова картинка — нове ім'я).</summary>
    public static IResult File(string file, SkleiDeck deck, HttpContext c)
    {
        if (deck.Resolve(file) is not { } path) return Results.NotFound();
        Span<byte> head = stackalloc byte[16];
        int n;
        try
        {
            using var f = System.IO.File.OpenRead(path);
            n = f.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }
        catch (IOException) { return Results.NotFound(); }
        if (LavkaImage.MimeOf(head[..n]) is not { } mime) return Results.NotFound();
        c.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        c.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.File(path, mime);
    }

    /// <summary>GET /api/games/sklei/art/&lt;id&gt; — малюнок з альбому Піктіонарі: { id, word, by, z } (z — як в альбомі).</summary>
    public static IResult Art(long id, SkleiDeck deck) => deck.Art(id) is { } a
        ? Results.Json(new { id, word = a.Word, by = a.Author, z = a.Z })
        : Results.NotFound();

    static IResult Reply(SkleiReply r)
    {
        var body = new { ok = r.Ok, message = r.Message, pic = r.Pic is { } p ? Own(p) : null };
        return r.Ok ? Results.Ok(body) : Results.BadRequest(body);
    }

    static async Task<byte[]> ReadCapped(Stream body, int max, CancellationToken ct)
    {
        var buf = new byte[max];
        var n = 0;
        while (n < max)
        {
            var k = await body.ReadAsync(buf.AsMemory(n), ct);
            if (k == 0) break;
            n += k;
        }
        return buf[..n];
    }
}
