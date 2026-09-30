using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Підключення живої реклами й HTTP (<c>/api/liveads…</c>). Кличеться з <see cref="AdSetup"/>: жива реклама — частина
/// реклами, і без неї джингл працює як раніше (бібліотека).
/// </summary>
public static class LiveAdsSetup
{
    public sealed record OrderRequest(string? Target, bool Anon);
    public sealed record OptOutRequest(bool Off);
    public sealed record RoastNowRequest(string? Nick);
    public sealed record EnabledRequest(bool Enabled);
    public sealed record ShareRequest(double Share);

    public static IServiceCollection AddLiveAds(this IServiceCollection services)
    {
        services.AddOptions<LiveAdsOptions>().BindConfiguration("LiveAds");
        services.AddOptions<CurfewOptions>();
        services.AddOptions<YtDlpOptions>();
        services.AddOptions<TtsOptions>();
        services.TryAddSingleton<ITtsEngine, EdgeTtsEngine>();
        services.TryAddSingleton<ILiveRenderer, FfmpegLiveRenderer>();
        services.AddSingleton<LiveAdsStore>();
        services.AddSingleton<LiveFacts>();
        services.AddSingleton<LiveAds>();
        services.AddSingleton<ILiveAdSource>(sp => sp.GetRequiredService<LiveAds>());
        services.AddHostedService(sp => sp.GetRequiredService<LiveAds>());
        return services;
    }

    static IResult Reply((bool Ok, string Message) r) =>
        r.Ok ? Results.Ok(new { ok = true, message = r.Message }) : Results.BadRequest(new { ok = false, message = r.Message });

    static IResult Deny() => Results.BadRequest(new { ok = false, message = "Це вміє тільки господар" });

    // ---------- маршрути окремими методами: тести кличуть їх так само, як сервер ----------

    public static IResult Card(HttpContext c, LiveAds live) => Results.Ok(live.View(Auth.Nick(c), Auth.IsUser(c)));

    public static IResult Order(HttpContext c, OrderRequest req, LiveAds live)
    {
        var r = live.Order(Auth.Nick(c), Auth.IsUser(c), req.Target, req.Anon);
        var body = new { ok = r.Ok, message = r.Message, balance = r.Balance, id = r.Id };
        return r.Ok ? Results.Ok(body) : Results.BadRequest(body);
    }

    public static IResult OptOut(HttpContext c, OptOutRequest req, LiveAds live) => Reply(live.OptOut(Auth.Nick(c), Auth.IsUser(c), req.Off));

    public static async Task<IResult> RoastNow(HttpContext c, RoastNowRequest req, LiveAds live, AdJingle jingle, CancellationToken ct)
    {
        if (!Auth.IsAdmin(c)) return Deny();
        var (clip, message) = await live.RoastNowAsync(req.Nick ?? "", ct);
        return Reply(clip is null ? (false, message) : jingle.PlayLive(clip));
    }

    public static async Task<IResult> NewsNow(HttpContext c, LiveAds live, AdJingle jingle, CancellationToken ct)
    {
        if (!Auth.IsAdmin(c)) return Deny();
        var (clip, message) = await live.NewsNowAsync(ct);
        return Reply(clip is null ? (false, message) : jingle.PlayLive(clip));
    }

    public static WebApplication MapLiveAds(this WebApplication app)
    {
        // ---- картка «Замов прожарку» в Лавці: будь-хто бачить, замовляє й відмовляється — лише акаунт ----
        app.MapGet("/api/liveads", Card);
        app.MapPost("/api/liveads/order", Order);
        app.MapPost("/api/liveads/optout", OptOut);

        // ---- блок «Жива реклама» у вкладці «📣 Реклама»: тільки господар ----
        app.MapGet("/api/liveads/admin", (HttpContext c, LiveAds live) => Auth.IsAdmin(c) ? Results.Ok(live.AdminView()) : Deny());
        app.MapPost("/api/liveads/admin/enabled", (HttpContext c, EnabledRequest req, LiveAds live) =>
            Auth.IsAdmin(c) ? Reply(live.SetEnabled(req.Enabled)) : Deny());
        app.MapPost("/api/liveads/admin/share", (HttpContext c, ShareRequest req, LiveAds live) =>
            Auth.IsAdmin(c) ? Reply(live.SetShare(req.Share)) : Deny());
        app.MapPost("/api/liveads/admin/roast", RoastNow);
        app.MapPost("/api/liveads/admin/news", NewsNow);
        return app;
    }
}
