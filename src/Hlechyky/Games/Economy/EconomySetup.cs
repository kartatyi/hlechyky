using Microsoft.Extensions.Options;
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
        // 👁 у щоденну — лише після своєї (Rooms.PeekRefusal)
        services.AddSingleton<IDailyPeek>(sp => new DailyPeek(sp.GetRequiredService<Daily>()));
        services.AddSingleton<Leaderboards>();

        services.AddSingleton<Rewards>();
        services.AddHostedService(sp => sp.GetRequiredService<Rewards>());
        services.AddSingleton<EconomyTicker>();
        services.AddHostedService(sp => sp.GetRequiredService<EconomyTicker>());
        services.AddSingleton<PlayClock>();
        services.AddHostedService(sp => sp.GetRequiredService<PlayClock>());
        // сальдо азарту (пакет 5): поставив / повернув у слотах і Лелеці — сьогодні й за весь час, з пам'яті
        services.AddSingleton(sp => new AzartSaldo(sp.GetService<Db>(), sp.GetRequiredService<Economy>(),
            sp.GetRequiredService<IClock>(), sp.GetService<ILogger<AzartSaldo>>()));
        services.AddSingleton<IAzartSaldo>(sp => sp.GetRequiredService<AzartSaldo>());
        services.AddHostedService(sp => sp.GetRequiredService<AzartSaldo>());
        // разовий подарунок-перерахунок за ярликом Economy:RegiftId (15.10.2026, ×10): ачівки за новим курсом + рівний подарунок
        services.AddSingleton<Regift>();
        services.AddHostedService(sp => sp.GetRequiredService<Regift>());
        // нічний підсумок «Щоденного глека»: «за кращий результат» і призи топу за вчора
        services.AddSingleton<DailyPrizes>();
        services.AddHostedService(sp => sp.GetRequiredService<DailyPrizes>());
        // Січ щоденних: тижневий залік, вічний Ело зі званнями, підсумок у ніч на понеділок і Глек у Балачках
        services.AddSingleton<Sich>();
        services.AddHostedService(sp => sp.GetRequiredService<Sich>());

        // Каркас реєструє заглушки через TryAdd і робить це раніше — тому саме Replace, а не TryAdd.
        services.Replace(ServiceDescriptor.Singleton<IStakes>(sp => sp.GetRequiredService<Economy>()));
        services.Replace(ServiceDescriptor.Singleton<IGameStore, SqliteGameStore>());
        Impl.AdSetup.AddAds(services);   // реклама в ефірі: бібліотека, ротація, джингл, черепки за прослухане і секція Ad
        return services;
    }

    public static WebApplication MapHlechykyEconomy(this WebApplication app)
    {
        var api = app.MapGroup("/api/games");

        api.MapGet("/leaderboard", (string? game, string? period, string? day, string? sort, Leaderboards boards) =>
            boards.Leaderboard(game, period, day, sort));

        api.MapGet("/profile", (string? nick, HttpContext c, Leaderboards boards) =>
            boards.Profile(string.IsNullOrWhiteSpace(nick) ? Auth.Nick(c) : nick.Trim()));

        api.MapGet("/wallet", (HttpContext c, Leaderboards boards) => boards.Wallet(Auth.Nick(c)));

        // Перед відповіддю дописуємо хвилину, що ще в пам'яті: інакше щойно зіграні хвилини з'являлись би із запізненням.
        api.MapGet("/time", (string? period, Leaderboards boards, PlayClock clock) =>
        {
            clock.Flush();
            return boards.Time(period);
        });

        api.MapGet("/daily", (HttpContext c, Daily daily, Sich sich, IOptionsMonitor<EconomyOptions> o) =>
        {
            var s = daily.Status(Auth.Nick(c));
            return s with
            {
                Pay = DailyPay.Of(o.CurrentValue, s.Puzzles.Count, s.Puzzles.Count(p => p.Me?.Solved == true)),
                Money = daily.Money(Auth.Nick(c), s, o.CurrentValue),
                Sich = sich.Brief(Auth.Nick(c)),
            };
        });

        // Січ щоденних: тиждень (week — будь-який день потрібного тижня; без нього — поточний), вічний рейтинг, мої дуелі
        api.MapGet("/sich", (string? week, HttpContext c, Sich sich) => sich.View(Auth.Nick(c), week));
        // 👑 Гетьмана тижня й звання — для ніків по всьому сайту (картка людини, онлайн)
        api.MapGet("/sich/marks", (Sich sich) => new
        {
            crown = sich.Crown(),
            ranks = sich.Ranks().ToDictionary(r => r.Key, r => new { icon = r.Value.Icon, title = r.Value.Title, elo = r.Value.Elo }),
        });

        Impl.AdSetup.MapAds(app);        // /api/ads/library*, /api/ads/air* — реклама, лише господар
        return app;
    }
}
