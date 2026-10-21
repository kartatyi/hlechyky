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
    public void Clock_that_moved_on_the_recheck_thawed_by_itself()
    {
        // 21.10.2026 23:38: сторож побачив 2738.78 після 20 с стояння, а за секунду годинник уже доганяв — 2750.22
        Assert.True(AirClock.Thawed(2738.78, 2750.22, 1));
        Assert.True(AirClock.Thawed(2738.78, 2740.78, 2));     // іде звичайним темпом
        Assert.True(AirClock.Thawed(2738.78, 3.1, 2));         // з нуля: liquidsoap уже перезапустили
    }

    [Fact]
    public void Clock_still_frozen_or_silent_on_the_recheck_gets_restarted()
    {
        Assert.False(AirClock.Thawed(2738.78, 2738.78, 2));
        Assert.False(AirClock.Thawed(2738.78, 2738.98, 2));    // крихта руху — не рух
        Assert.False(AirClock.Thawed(2738.78, null, 2));       // telnet не відповів
        Assert.False(AirClock.Thawed(null, 2738.78, 2));
    }

    [Fact]
    public void After_a_thaw_the_clock_counts_from_the_recheck()
    {
        var c = new AirClock();
        c.Observe(2738.78, At(0));
        Assert.Equal(20, c.Observe(2738.78, At(20)));
        Assert.True(AirClock.Thawed(2738.78, 2750.22, 2));
        c.Reset();
        c.Observe(2750.22, At(22));                            // так робить сторож: відлік від другого показання
        Assert.Equal(0, c.Observe(2760.22, At(27)));           // доганяє — іде
        Assert.Equal(0, c.Observe(2765.22, At(32)));
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
