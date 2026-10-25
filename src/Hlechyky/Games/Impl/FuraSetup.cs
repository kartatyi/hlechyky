namespace Hlechyky.Games.Impl;

/// <summary>
/// HTTP «Чумаків-далекобійників»: <c>GET /api/games/fura/map/{id}</c> — мапа як є (браузер будує з неї світ). Лише id зі
/// списку <see cref="FuraMap.Known"/>: шлях до файлу з клієнта не будується. Кличеться рядком із <see cref="GamesSetup"/>.
/// </summary>
public static class FuraSetup
{
    public const string Root = "/api/games/fura";

    public static WebApplication MapFura(WebApplication app)
    {
        app.MapGet(Root + "/map/{id}", (string id, HttpContext c) => Map(id, c));
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
}
