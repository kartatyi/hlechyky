using System.Diagnostics;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Секундомір тестів швидкодії: рахує роботу потоку, а не чекання, і не бреше вниз.
/// </summary>
public class CpuWatchTests
{
    [Fact]
    public void Busy_loop_counts_and_never_exceeds_the_wall_clock()
    {
        var wall = Stopwatch.StartNew();
        var cpu = CpuWatch.StartNew();
        var x = 0L;
        while (cpu.ElapsedMilliseconds < 60) x++;
        cpu.Stop();
        wall.Stop();
        Assert.True(x > 0);
        Assert.InRange(cpu.Elapsed.TotalMilliseconds, 60, wall.Elapsed.TotalMilliseconds + 1);
    }

    [Fact]
    public void Sleeping_is_not_work()
    {
        var cpu = CpuWatch.StartNew();
        Thread.Sleep(200);
        cpu.Stop();
        if (CpuWatch.IsThreadTime) Assert.True(cpu.ElapsedMilliseconds < 50, $"{cpu.ElapsedMilliseconds} мс");
        else Assert.True(cpu.ElapsedMilliseconds >= 190);
    }

    [Fact]
    public void Start_stop_accumulates_and_restart_clears()
    {
        var w = new CpuWatch();
        for (var i = 0; i < 3; i++)
        {
            w.Start();
            var t = CpuWatch.GetTimestamp();
            while (CpuWatch.GetElapsedTime(t).TotalMilliseconds < 10) { }
            w.Stop();
            Thread.Sleep(20);
        }
        Assert.False(w.IsRunning);
        Assert.InRange(w.Elapsed.TotalMilliseconds, 30, 60);
        Assert.Equal(w.ElapsedTicks * 1000 / CpuWatch.Frequency, w.ElapsedMilliseconds);
        w.Restart();
        Assert.True(w.IsRunning);
        Assert.True(w.ElapsedMilliseconds < 10);
    }

    [Fact]
    public async Task Interval_that_ends_on_another_thread_falls_back_to_the_wall_clock()
    {
        var before = Environment.CurrentManagedThreadId;
        var w = CpuWatch.StartNew();
        await Task.Delay(100).ConfigureAwait(false);
        await Task.Run(() => Thread.Sleep(1)).ConfigureAwait(false);
        var ms = w.Elapsed.TotalMilliseconds;
        // інший потік — такти чужі, тож чесний стінний час; той самий — лише трохи роботи
        if (Environment.CurrentManagedThreadId != before) Assert.InRange(ms, 95, 5000);
        else Assert.InRange(ms, 0, 50);
    }
}
