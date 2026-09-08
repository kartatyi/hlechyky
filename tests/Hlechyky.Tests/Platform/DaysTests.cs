using Hlechyky.Games;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

public class DaysTests
{
    [Fact]
    public void Day_switches_at_kyiv_midnight_not_utc()
    {
        // 9 вересня 20:59 UTC = 23:59 за Києвом (UTC+3 влітку); 21:01 UTC — уже 10 вересня
        Assert.Equal("2026-09-09", Days.Of(new DateTimeOffset(2026, 9, 9, 20, 59, 0, TimeSpan.Zero)));
        Assert.Equal("2026-09-10", Days.Of(new DateTimeOffset(2026, 9, 9, 21, 1, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Seed_is_stable_and_differs_by_puzzle_and_day()
    {
        Assert.Equal(Days.Seed("wordle", "2026-09-10"), Days.Seed("wordle", "2026-09-10"));
        Assert.NotEqual(Days.Seed("wordle", "2026-09-10"), Days.Seed("wordle", "2026-09-11"));
        Assert.NotEqual(Days.Seed("wordle", "2026-09-10"), Days.Seed("mines-daily", "2026-09-10"));
        Assert.True(Days.Seed("wordle", "2026-09-10") >= 0);
    }

    [Fact]
    public void Next_midnight_is_kyiv_midnight_in_utc()
    {
        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 9, 10, 21, 0, 0, TimeSpan.Zero), Days.NextMidnight(now));
    }

    [Fact]
    public void Day_number_starts_at_one_on_launch_day()
    {
        Assert.Equal(1, Days.Number("2026-09-10"));
        Assert.Equal(32, Days.Number("2026-10-11"));
    }

    [Fact]
    public void FakeClock_defaults_to_afternoon_in_kyiv()
    {
        var clock = new FakeClock();
        Assert.Equal("2026-09-10", Days.Today(clock));
        clock.Advance(TimeSpan.FromHours(9));   // 21:00 UTC = північ у Києві
        Assert.Equal("2026-09-11", Days.Today(clock));
    }
}
