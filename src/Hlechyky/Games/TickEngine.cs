namespace Hlechyky.Games;

/// <summary>
/// Один годинник на всі реалтайм-кімнати (ARCHITECTURE §4.6). Прокидається кожні 20 мс, тикає тим, кому
/// час, раз на секунду прибирає засиджені кімнати й тих, хто не повернувся після grace, і зливає все
/// зібране через Broadcaster — уже поза замками кімнат.
/// </summary>
public sealed class TickEngine(Rooms rooms, Broadcaster broadcaster, IClock clock, ILogger<TickEngine> log) : BackgroundService
{
    /// <summary>Крок циклу. Найшвидша гра тикає раз на 40 мс, тож 20 дає запас і не мажеться по фазі.</summary>
    public const int StepMs = 20;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(StepMs));
        var nextChores = clock.UtcNow;
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var now = clock.UtcNow;
                var outbox = new Outbox();
                foreach (var room in rooms.TickDue(now)) outbox.Adopt(rooms.Tick(room));
                if (now >= nextChores)
                {
                    nextChores = now.AddSeconds(1);
                    outbox.Adopt(rooms.DropIfGone(now));
                    outbox.Adopt(rooms.Housekeeping(now));
                }
                await broadcaster.FlushAsync(outbox, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log.LogWarning(ex, "коло тика впало"); }
        }
    }
}
