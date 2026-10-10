namespace Hlechyky.Tests.Platform;

/// <summary>
/// Сторож годинника ефіру (<see cref="LiquidsoapGuard"/>): liquidsoap на Windows часом «замерзає» — telnet відповідає,
/// а годинник стоїть і потік мовчить. Перезапускати треба саме тоді, а не коли машина просто пригальмувала чи
/// liquidsoap щойно перезапустили.
/// </summary>
public class AirClockTests
{
    static readonly DateTime T0 = new(2026, 10, 9, 1, 10, 0, DateTimeKind.Utc);
    static DateTime At(double s) => T0.AddSeconds(s);

    [Fact]
    public void Running_clock_is_never_stuck()
    {
        var c = new AirClock();
        for (var s = 0; s <= 60; s += 5) Assert.Equal(0, c.Observe(1000 + s, At(s)));
    }

    [Fact]
    public void Frozen_clock_counts_seconds_since_it_last_moved()
    {
        var c = new AirClock();
        c.Observe(1000, At(0));
        c.Observe(1005, At(5));
        Assert.Equal(5, c.Observe(1005, At(10)));
        Assert.Equal(15, c.Observe(1005, At(20)));
        Assert.Equal(20, c.Observe(1005.2, At(25)));   // крихта руху — не рух
    }

    [Fact]
    public void Slow_but_moving_clock_is_not_frozen()
    {
        var c = new AirClock();
        c.Observe(1000, At(0));
        for (var s = 5; s <= 60; s += 5) Assert.Equal(0, c.Observe(1000 + s * 0.6, At(s)));   // відстає, але йде
    }

    [Fact]
    public void Clock_starts_over_after_a_restart_or_a_missed_answer()
    {
        var c = new AirClock();
        c.Observe(1000, At(0));
        Assert.Equal(10, c.Observe(1000, At(10)));
        Assert.Equal(0, c.Observe(2, At(15)));        // свіжий liquidsoap: годинник з нуля
        Assert.Equal(0, c.Observe(null, At(20)));     // telnet не відповів — не знаємо, і не рахуємо
        Assert.Equal(0, c.Observe(12, At(25)));
        Assert.Equal(5, c.Observe(12, At(30)));
    }

    [Fact]
    public void Reset_forgets_the_last_reading()
    {
        var c = new AirClock();
        c.Observe(1000, At(0));
        c.Reset();
        Assert.Equal(0, c.Observe(1000, At(30)));
    }

    [Fact]
    public void Parses_clock_dump()
    {
        const string dump = "· radio (ticks: 3982739, time: 79654.78s, self_sync: false)\n  ├── outputs: radio [radio]\n";
        Assert.Equal(79654.78, AirClock.Parse(dump));
        Assert.Null(AirClock.Parse("ERROR: unknown command"));
        Assert.Null(AirClock.Parse(null));
    }
}
