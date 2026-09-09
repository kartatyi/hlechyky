namespace Hlechyky.Games.Economy;

/// <summary>
/// Підключення словників (WP4): Words як синглтон, завантаження списків із data/words. Program.cs кличе;
/// вміст — власність WP4.
/// </summary>
public static class WordsSetup
{
    public static IServiceCollection AddHlechykyWords(this IServiceCollection services)
    {
        services.AddSingleton<Words>();
        // синглтон з DI створюється лише коли його вперше попросять — а нам треба, щоб списки читались
        // і статистика лягала в лог на старті сервера, а не посеред першої партії
        services.AddHostedService<WordsWarmup>();
        return services;
    }
}

/// <summary>Смикає Words при старті, щоб файли прочитались одразу і великий словник почав будуватись.</summary>
sealed class WordsWarmup(Words words) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        _ = words.Stats;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
