using Hlechyky.Games;

namespace Hlechyky.Tests.Support;

/// <summary>Годинник, який іде лише тоді, коли тест його штовхає. Стартує з 2026-09-10 12:00 UTC (15:00 за Києвом).</summary>
public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;
    public void Advance(double seconds) => UtcNow += TimeSpan.FromSeconds(seconds);
    public void AdvanceMs(int ms) => UtcNow += TimeSpan.FromMilliseconds(ms);
}
