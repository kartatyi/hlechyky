using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Майстер дня» (docs/games/specs/clicker-v12.md §5): доба з 23:30 до 23:30 за Києвом, очки з ручного горна, табло без
/// гостей і ботів, о 23:30 — призи першим трьом (якщо людей з порогом очок досить), повтор підбиття не платить двічі.
/// </summary>
public class ClickerDayTests
{
    static readonly TimeZoneInfo Kyiv = Days.Kyiv;

    static DateTimeOffset Local(int y, int m, int d, int h, int min) =>
        new(TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, m, d, h, min, 0, DateTimeKind.Unspecified), Kyiv), TimeSpan.Zero);

    static ClickerDayService Svc(FakeStakes stakes, FakeClock clock, ClickerDayOptions? o = null) =>
        new(null, stakes, () => o ?? new ClickerDayOptions(), clock, person: nick => !nick.StartsWith("гість", StringComparison.Ordinal));

    [Fact]
    public void The_day_turns_at_half_past_eleven_kyiv_time()
    {
        Assert.Equal("2026-10-21", ClickerDayService.DayOf(Local(2026, 10, 21, 23, 29)));
        Assert.Equal("2026-10-22", ClickerDayService.DayOf(Local(2026, 10, 21, 23, 30)));
        Assert.Equal("2026-10-22", ClickerDayService.DayOf(Local(2026, 10, 22, 0, 10)));
        Assert.Equal(Local(2026, 10, 22, 23, 30), ClickerDayService.Ends("2026-10-22"));
        // Перехід на зимовий час (25.10.2026): межа однаково о 23:30 за Києвом.
        Assert.Equal(Local(2026, 10, 25, 23, 30), ClickerDayService.Ends("2026-10-25"));
    }

    [Fact]
    public void At_half_past_eleven_the_top_three_get_prizes_once()
    {
        var stakes = new FakeStakes();
        var clock = new FakeClock { UtcNow = Local(2026, 10, 21, 12, 0) };
        var svc = Svc(stakes, clock);
        svc.Add("Smaug", 112, clock.UtcNow);
        svc.Add("Назар", 80, clock.UtcNow.AddMinutes(1));
        svc.Add("Владік", 80, clock.UtcNow.AddMinutes(2));   // рівні з Назаром — хто раніше
        svc.Add("Микола", 31, clock.UtcNow);
        svc.Add("гість Оля", 500, clock.UtcNow);             // гості — не в табло
        var board = svc.Board("2026-10-21");
        Assert.Equal(["Smaug", "Назар", "Владік", "Микола"], board.Select(r => r.Nick));

        Assert.Equal(0, svc.Settle(Local(2026, 10, 21, 23, 29)));
        Assert.Equal(1, svc.Settle(Local(2026, 10, 21, 23, 30)));
        Assert.Equal(0, svc.Settle(Local(2026, 10, 21, 23, 45)));
        var paid = stakes.Ledger.Select(m => (m.Nick, m.Delta)).ToList();
        Assert.Equal([("Smaug", 200), ("Назар", 100), ("Владік", 50)], paid.Select(x => (x.Nick, (int)x.Delta)));
        Assert.Equal(1, svc.WinsOf("Smaug"));
        // Після 23:30 очки йдуть уже в завтрашній день.
        svc.Add("Smaug", 3, Local(2026, 10, 21, 23, 40));
        Assert.Equal(3, svc.Board("2026-10-22").Single().Points);
    }

    [Fact]
    public void A_day_pays_only_when_enough_people_reached_the_threshold()
    {
        var stakes = new FakeStakes();
        var clock = new FakeClock { UtcNow = Local(2026, 10, 21, 12, 0) };
        var svc = Svc(stakes, clock);
        svc.Add("Smaug", 300, clock.UtcNow);
        svc.Add("Назар", 29, clock.UtcNow);       // нижче порогу 30
        Assert.Equal(1, svc.Settle(Local(2026, 10, 22, 0, 0)));
        Assert.Empty(stakes.Ledger);
        Assert.Equal(0, svc.WinsOf("Smaug"));
    }

    [Fact]
    public void Prizes_and_thresholds_come_from_the_options_live()
    {
        var stakes = new FakeStakes();
        var clock = new FakeClock { UtcNow = Local(2026, 10, 21, 12, 0) };
        var o = new ClickerDayOptions { Prizes = [500], MinPlayers = 1, MinPoints = 1 };
        var svc = Svc(stakes, clock, o);
        svc.Add("Smaug", 5, clock.UtcNow);
        svc.Settle(Local(2026, 10, 21, 23, 31));
        Assert.Equal(500, (int)stakes.Ledger.Single().Delta);
        Assert.Contains(stakes.Reasons, kv => kv.Value == "clicker-day:2026-10-21");
    }

    [Fact]
    public void Switched_off_it_counts_nothing_and_shows_nothing()
    {
        var clock = new FakeClock { UtcNow = Local(2026, 10, 21, 12, 0) };
        var svc = Svc(new FakeStakes(), clock, new ClickerDayOptions { Enabled = false });
        svc.Add("Smaug", 50, clock.UtcNow);
        Assert.Empty(svc.Board("2026-10-21"));
        Assert.Null(svc.Wire("Smaug", clock.UtcNow));
    }
}
