namespace Hlechyky.Games.Economy;

/// <summary>
/// Підключення сервісів (WP1): черепки, рейтинги, таблиці, ачівки, щоденний глек, словники, сховище станів.
/// Program.cs кличе обидва методи; вміст — власність WP1.
/// </summary>
public static class EconomySetup
{
    public static IServiceCollection AddHlechykyEconomy(this IServiceCollection services)
    {
        return services;
    }

    public static WebApplication MapHlechykyEconomy(this WebApplication app)
    {
        return app;
    }
}
