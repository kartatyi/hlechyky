namespace Hlechyky.Games.Economy;

/// <summary>
/// Підключення словників (WP4): Words як синглтон, завантаження списків із data/words. Program.cs кличе;
/// вміст — власність WP4.
/// </summary>
public static class WordsSetup
{
    public static IServiceCollection AddHlechykyWords(this IServiceCollection services)
    {
        return services;
    }
}
