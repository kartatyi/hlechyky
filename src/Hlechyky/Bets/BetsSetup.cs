using Hlechyky.Games;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Hlechyky.Bets;

/// <summary>
/// Через хаб радіо: людині — <c>betMine</c> { text } (і <c>toast</c>, коли треба), усім — <c>betEvents</c> {} (події
/// змінились), адмінам записок (<see cref="FeedbackDevGroup"/>) — <c>betSuggest</c> { count } і тост про нову пропозицію.
/// </summary>
public sealed class HubBetsWire(IHubContext<RadioHub> hub, Presence presence, ILogger<HubBetsWire> log) : IBetsWire
{
    public void Mine(string nick, string text, bool toast)
    {
        var ids = presence.ConnectionsOf(nick);
        if (ids.Count == 0) return;
        if (toast) _ = SendAsync(hub.Clients.Clients(ids), "toast", new { text, kind = "ok" });
        _ = SendAsync(hub.Clients.Clients(ids), "betMine", new { text });
    }

    public void Events() => _ = SendAsync(hub.Clients.All, "betEvents", new { });

    public void Suggestions(int pending, string? toast)
    {
        var admins = hub.Clients.Group(FeedbackDevGroup.Name);
        _ = SendAsync(admins, "betSuggest", new { count = pending });
        if (toast is not null) _ = SendAsync(admins, "toast", new { text = toast, kind = "ok" });
    }

    async Task SendAsync(IClientProxy to, string name, object payload)
    {
        try { await to.SendAsync(name, payload); }
        catch (Exception ex) { log.LogWarning(ex, "ставки не розіслали {Event}", name); }
    }
}

/// <summary>Підключення й маршрути <c>/api/bets…</c>. Кожен маршрут — статичний метод, щоб тести кликали його так само.</summary>
public static class BetsSetup
{
    public sealed record StatusRequest(string? Status);
    public sealed record SettleRequest(string? Winner);
    public sealed record ReasonRequest(string? Reason);
    public sealed record SuggestRequest(string? Text, string? PmSlug);
    public sealed record AddedRequest(long? EventId);

    public static IServiceCollection AddHlechykyBets(this IServiceCollection services)
    {
        services.AddOptions<BetsOptions>().BindConfiguration("Bets");
        services.AddSingleton<BetsStore>();
        services.AddSingleton<BetBook>();
        services.TryAddSingleton<IBetsWire, HubBetsWire>();
        services.TryAddSingleton(sp => new Polymarket(sp.GetRequiredService<IOptionsMonitor<BetsOptions>>(), sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<Polymarket>>()));
        services.AddSingleton<BetEvents>();
        // Ставки на столах: Rooms бере хук ліниво (сервіс сам залежить від Rooms); хост — звірка й розрахунок партій.
        services.TryAddSingleton<ITableBetsWire, HubTableBetsWire>();
        services.AddSingleton<TableBets>();
        services.AddSingleton<ITableBetsHook>(sp => sp.GetRequiredService<TableBets>());
        services.AddHostedService(sp => sp.GetRequiredService<TableBets>());
        return services;
    }

    public static WebApplication MapHlechykyBets(this WebApplication app)
    {
        var api = app.MapGroup("/api/bets");
        api.MapGet("", View);
        api.MapGet("/admin", Admin);
        api.MapGet("/mine", Mine);
        api.MapGet("/glek", Glek);
        api.MapPost("/events", Create);
        api.MapPut("/events/{id:long}", Edit);
        api.MapPost("/events/{id:long}/status", SetStatus);
        api.MapPost("/events/{id:long}/settle", Settle);
        api.MapPost("/events/{id:long}/cancel", Cancel);
        api.MapPost("/events/{id:long}/bet", Bet);
        api.MapGet("/events/{id:long}/pm", Prices);
        api.MapGet("/pm/feed", PmFeed);
        api.MapGet("/pm/event/{slug}", PmEvent);
        api.MapPost("/suggest", Suggest);
        api.MapPost("/suggest/{id:long}/add", SuggestionAdded);
        api.MapPost("/suggest/{id:long}/reject", SuggestionRejected);
        return app;
    }

    public static object View(HttpContext c, BetEvents bets) => bets.View(BetActor.Of(c));

    public static IResult Admin(HttpContext c, BetEvents bets) => Reply(bets.Admin(BetActor.Of(c)));

    public static IResult Mine(int? limit, HttpContext c, BetEvents bets) => Reply(bets.Mine(BetActor.Of(c), limit));

    public static object Glek(BetEvents bets) => bets.Glek();

    public static IResult Create(HttpContext c, BetEvents.EventRequest b, BetEvents bets) => Reply(bets.Create(BetActor.Of(c), b));

    public static IResult Edit(long id, HttpContext c, BetEvents.EventRequest b, BetEvents bets) => Reply(bets.Edit(BetActor.Of(c), id, b));

    public static IResult SetStatus(long id, HttpContext c, StatusRequest b, BetEvents bets) => Reply(bets.SetStatus(BetActor.Of(c), id, b.Status));

    public static IResult Settle(long id, HttpContext c, SettleRequest b, BetEvents bets) => Reply(bets.Settle(BetActor.Of(c), id, b.Winner));

    public static IResult Cancel(long id, HttpContext c, ReasonRequest? b, BetEvents bets) => Reply(bets.Cancel(BetActor.Of(c), id, b?.Reason));

    public static IResult Bet(long id, HttpContext c, BetEvents.BetRequest b, BetEvents bets) => Reply(bets.Bet(BetActor.Of(c), id, b));

    public static async Task<IResult> Prices(long id, string? market, HttpContext c, BetEvents bets) =>
        Reply(await bets.Prices(BetActor.Of(c), id, market, c.RequestAborted));

    public static async Task<IResult> PmFeed(string? cat, string? q, int? offset, HttpContext c, BetEvents bets) =>
        Reply(await bets.PmFeed(BetActor.Of(c), cat, q, offset, c.RequestAborted));

    public static async Task<IResult> PmEvent(string slug, string? market, HttpContext c, BetEvents bets) =>
        Reply(await bets.PmEvent(BetActor.Of(c), slug, market, c.RequestAborted));

    public static async Task<IResult> Suggest(HttpContext c, SuggestRequest b, BetEvents bets) =>
        Reply(await bets.Suggest(BetActor.Of(c), b.Text, b.PmSlug, c.RequestAborted));

    public static IResult SuggestionAdded(long id, HttpContext c, AddedRequest? b, BetEvents bets) => Reply(bets.SuggestionAdded(BetActor.Of(c), id, b?.EventId));

    public static IResult SuggestionRejected(long id, HttpContext c, ReasonRequest? b, BetEvents bets) => Reply(bets.SuggestionRejected(BetActor.Of(c), id, b?.Reason));

    /// <summary>{ ok, message, data } — як у черепків за гривні; відмова — 400 або свій код (403, 404, 409, 502).</summary>
    static IResult Reply(BetReply r)
    {
        var body = new { ok = r.Ok, message = r.Message, data = r.Data };
        return r.Ok ? Results.Ok(body) : Results.Json(body, statusCode: r.Status == 200 ? 400 : r.Status);
    }
}
