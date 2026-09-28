using System.Runtime.InteropServices;

namespace Hlechyky.Games;

/// <summary>
/// Один годинник на всі реалтайм-кімнати (ARCHITECTURE §4.6). Прокидається кожні 10 мс, тикає тим, кому
/// час, раз на секунду прибирає засиджені кімнати й тих, хто не повернувся після grace, і зливає все
/// зібране через Broadcaster — уже поза замками кімнат.
/// </summary>
public sealed class TickEngine(Rooms rooms, Broadcaster broadcaster, IClock clock, ILogger<TickEngine> log) : BackgroundService
{
    /// <summary>
    /// Крок циклу. Ділить кожен TickMs ігор (40, 50, 60, 100, 120, 200, 250 …), тож тик прокидається точно у свій час.
    /// Було 20 — і 50 мс Дуелі чи 250 мс партійних ігор чергувались 40/60 і 240/260.
    /// </summary>
    public const int StepMs = 10;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Системний таймер Windows типово цокає раз на 15,6 мс, і цикл прокидався кожні ~31 мс: кадри Танчиків (40 мс)
        // приходили то за 31, то за 62 мс (заміри 28.09: σ 13,6 мс) — рух у браузері смикався. Просимо точність 1 мс
        // на час роботи сервера (з Windows 10 2004 — лише для нашого процесу).
        var fine = FineTimer.Begin();
        try { await Loop(ct); }
        finally { if (fine) FineTimer.End(); }
    }

    async Task Loop(CancellationToken ct)
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

/// <summary>Точний (1 мс) системний таймер Windows на час роботи циклу тика; деінде — нічого не робить.</summary>
static class FineTimer
{
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] static extern uint TimeBeginPeriod(uint ms);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] static extern uint TimeEndPeriod(uint ms);

    public static bool Begin()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return TimeBeginPeriod(1) == 0; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    public static void End()
    {
        try { TimeEndPeriod(1); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }
}
