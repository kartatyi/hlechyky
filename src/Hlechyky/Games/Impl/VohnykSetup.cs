namespace Hlechyky.Games.Impl;

/// <summary>
/// Підключення «Вогника і Краплі» (specs/vohnyk.md §7.5): сховище прогресу й рекордів пар і два ендпоінти —
/// таблиця рівня для лобі й усі рівні з записаними проходженнями для сценарію паритету (лише адмінові:
/// розв'язок головоломки гравцям не показуємо).
/// </summary>
public static class VohnykSetup
{
    public static IServiceCollection AddVohnyk(this IServiceCollection services)
    {
        services.AddSingleton(sp => new VohnykStore(sp.GetService<Db>(), sp.GetService<ILogger<VohnykStore>>()));
        // Фоновий злив у базу; заодно сховище створюється (і читає таблиці) ще на старті сервера, а не під першим столом.
        services.AddHostedService(sp => sp.GetRequiredService<VohnykStore>());
        return services;
    }

    public static WebApplication MapVohnyk(this WebApplication app)
    {
        // Таблиця рівня: десять найшвидших пар (і тих, хто сам за двох).
        app.MapGet("/api/games/vohnyk/best", (int? level, VohnykStore store) =>
        {
            var n = level >= 1 && level <= VohnykLevels.Count ? level.Value : 1;
            var rows = store.Top(n, 10).Select(b => new { nicks = b.Nicks, solo = b.Solo, ms = b.Ms, deaths = b.Deaths, stars = b.Stars, at = b.At });
            return Results.Json(new { level = n, rows });
        });

        // Привид найкращого записаного проходження пари на рівні: позиції обох героїв кожні 4 кроки (80 мс).
        // Клієнт бере його один раз на рівень (у виді — лише ms і ключ пари), тож вид лишається маленьким.
        app.MapGet("/api/games/vohnyk/ghost", (int? level, string? pair, VohnykStore store) =>
        {
            if (level is not { } n || string.IsNullOrEmpty(pair) || store.Ghost(pair, n) is not { } g) return Results.NotFound();
            return Results.Json(new { level = n, ms = g.Ms, every = Vohnyk.GhostEvery, data = g.Data });
        });

        // Усі рівні як лежать у файлах, разом із solution і check — для docs/games/dev/vohnyk-parity.js.
        app.MapGet("/api/games/vohnyk/levels", (HttpContext c) =>
        {
            if (!Auth.IsAdmin(c)) return Results.StatusCode(403);
            var dir = Paths.Resolve(VohnykLevels.Dir);
            var texts = new List<string>();
            for (var n = 1; n <= VohnykLevels.Count; n++)
            {
                var path = Path.Combine(dir, $"{n:00}.json");
                if (File.Exists(path)) texts.Add(File.ReadAllText(path));
            }
            return Results.Text("[" + string.Join(",", texts) + "]", "application/json; charset=utf-8");
        });
        return app;
    }
}
