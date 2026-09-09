using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Economy;

/// <summary>
/// Хвилинний тікер економіки: рахує, скільки хто просидів на сайті, і час від часу кидає за це черепок.
/// Онлайн — це наявність з'єднання з хабом (<see cref="Presence"/>), а не гучність у навушниках; чесніше
/// ми все одно не дізнаємось, а стеля дня не дає нафармити двома вкладками.
/// </summary>
public sealed class EconomyTicker(Presence presence, Economy economy, EconomyStore store,
    Achievements achievements, IClock clock, IOptionsMonitor<EconomyOptions> opts, ILogger<EconomyTicker> log)
    : BackgroundService
{
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(1);

    /// <summary>Лічильник хвилин за весь час: день у нього не входить, звідси зірочка.</summary>
    public const string AllDays = "*";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Period);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { Minute(); }
            catch (Exception ex) { log.LogWarning(ex, "хвилинний підрахунок економіки спіткнувся"); }
        }
    }

    /// <summary>Один крок. Публічний, бо в тестах його зручніше смикати руками, ніж чекати хвилину.</summary>
    public void Minute()
    {
        var o = opts.CurrentValue;
        var day = Days.Today(clock);
        foreach (var nick in presence.Online)
        {
            var key = Economy.Key(nick);
            if (key.Length == 0) continue;

            // спотикання на одному нікові не має забирати хвилину в усіх інших
            try
            {
                var minutes = store.Bump(key, "online", day, 1);
                var total = store.Bump(key, "online-total", AllDays, 1);

                if (o.ListenEveryMinutes > 0 && minutes % o.ListenEveryMinutes == 0)
                    economy.GrantSequenced(nick, o.ListenReward, "listen",
                        n => $"listen:{key}:{day}:{n}", "listen", o.ListenDailyCap);

                achievements.OnOnlineMinutes(nick, total);
            }
            catch (Exception ex) { log.LogWarning(ex, "хвилина для {Nick} не порахувалась", nick); }
        }
    }
}
