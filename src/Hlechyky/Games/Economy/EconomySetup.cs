using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hlechyky.Games.Economy;

/// <summary>
/// Підключення сервісів (WP1): черепки, рейтинги, таблиці, ачівки, щоденний глек, словники, сховище станів.
/// Program.cs кличе обидва методи; вміст — власність WP1.
/// </summary>
public static class EconomySetup
{
    public static IServiceCollection AddHlechykyEconomy(this IServiceCollection services)
    {
        // Годинник, шину подій і поштову скриньку реєструє каркас (WP0) — і робить це раніше.
        // TryAdd тут лише для того, щоб сервіси піднімались і без нього (тести, ранні гілки).
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<GameEvents>();
        services.TryAddSingleton<IOutbox, NullOutbox>();

        // секція Economy — власність WP1, тому прив'язка живе тут, а не в Program.cs (спільному файлі)
        services.AddOptions<EconomyOptions>().BindConfiguration("Economy");

        // Паспорти беремо з реєстру каркаса, а не з власного скану збірки: реєстр уже все знайшов,
        // а без нього (ранні гілки, тести) лишається порожній список, який дозаповнять події партій.
        services.AddSingleton(sp => sp.GetService<Registry>() is { } r ? new GameNames(r) : new GameNames([]));
        services.AddSingleton<EconomyStore>();
        services.AddSingleton<Economy>();
        services.AddSingleton<Ratings>();
        services.AddSingleton<Achievements>();
        services.AddSingleton<Daily>();
        services.AddSingleton<Leaderboards>();

        services.AddSingleton<Rewards>();
        services.AddHostedService(sp => sp.GetRequiredService<Rewards>());
        services.AddSingleton<EconomyTicker>();
        services.AddHostedService(sp => sp.GetRequiredService<EconomyTicker>());

        // Каркас реєструє заглушки через TryAdd і робить це раніше — тому саме Replace, а не TryAdd.
        services.Replace(ServiceDescriptor.Singleton<IStakes>(sp => sp.GetRequiredService<Economy>()));
        services.Replace(ServiceDescriptor.Singleton<IGameStore, SqliteGameStore>());
        Impl.AdContestSetup.AddAdContest(services);   // конкурс реклами: сервіс, тікер, джингл і секція Ad
        return services;
    }

    public static WebApplication MapHlechykyEconomy(this WebApplication app)
    {
        var api = app.MapGroup("/api/games");

        api.MapGet("/leaderboard", (string? game, string? period, string? day, Leaderboards boards) =>
            boards.Leaderboard(game, period, day));

        api.MapGet("/profile", (string? nick, HttpContext c, Leaderboards boards) =>
            boards.Profile(string.IsNullOrWhiteSpace(nick) ? Auth.Nick(c) : nick.Trim()));

        api.MapGet("/wallet", (HttpContext c, Leaderboards boards) => boards.Wallet(Auth.Nick(c)));

        api.MapGet("/daily", (HttpContext c, Daily daily) => daily.Status(Auth.Nick(c)));

        Impl.AdContestSetup.MapAdContest(app);        // /api/ads — конкурс реклами
        return app;
    }
}
