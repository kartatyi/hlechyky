namespace Hlechyky.Games.Impl;

/// <summary>Сапер дня, пакет 4: залік тижня і фоновий прогрів полів без вгадування (і завтрашнього — перед північчю).</summary>
public static class MinesSetup
{
    public static IServiceCollection AddMines(this IServiceCollection services)
    {
        services.AddSingleton(sp => new MinesWeek(sp.GetService<Db>(), sp.GetRequiredService<IClock>()));
        services.AddHostedService(sp => new MinesFieldsWarmUp(sp.GetRequiredService<IClock>(), sp.GetService<ILogger<MinesFieldsWarmUp>>()));
        return services;
    }
}

/// <summary>
/// Раз на хвилину: поле сьогодні й поле «через чверть години» (тобто завтрашнє, щойно до півночі лишилось мало) — у кеш
/// <see cref="MinesFields"/>. Перша кімната дня тоді не чекає на генератор навіть у Configure.
/// </summary>
public sealed class MinesFieldsWarmUp(IClock clock, ILogger<MinesFieldsWarmUp>? log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var today = Days.Today(clock);
                MinesFields.Prune(today);
                foreach (var day in new[] { today, Days.Of(clock.UtcNow.AddMinutes(15)) }.Distinct())
                {
                    if (MinesDaily.NoGuess(day)) MinesDaily.Field(day);
                    MinesDailyEasy.Field(day);
                }
            }
            catch (Exception ex) { log?.LogWarning(ex, "сапер дня: не прогрів поле"); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stop); }
            catch (OperationCanceledException) { break; }
        }
    }
}
