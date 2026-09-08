namespace Hlechyky.Games;

/// <summary>
/// Підключення ігрового каркаса (WP0): кімнати, реєстр, розсилка, тик. Program.cs кличе обидва методи;
/// їхній вміст — власність WP0. Сервіси економіки підключає EconomySetup.
/// </summary>
public static class GamesSetup
{
    public static IServiceCollection AddHlechykyGames(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<GameEvents>();
        return services;
    }

    public static WebApplication MapHlechykyGames(this WebApplication app)
    {
        return app;
    }
}
