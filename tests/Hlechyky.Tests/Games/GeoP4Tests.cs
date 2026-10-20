using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>Пакет 4 «Щоденні» для «Де це?»: «Де це? дня» у «Щоденному глеку», таблиця дня, слабка область.</summary>
public sealed class GeoP4Tests
{
    static IServiceProvider With(GeoCache c, GeoDailyBoard? board = null)
    {
        var s = new ServiceCollection().AddSingleton(c.Photos);
        if (board is not null) s.AddSingleton(board);
        return s.BuildServiceProvider();
    }

    /// <summary>Зіграти «Де це? дня» до кінця: <paramref name="aim"/> — куди тицьнути (null — без шпильки).</summary>
    static void PlayDay(RoomHarness h, GeoCache c, Func<int, (int X, int Y), (int X, int Y)?> aim)
    {
        for (var r = 0; r < 5; r++)
        {
            GeoTests.Until(h, GeoMatch.PhaseGuess);
            if (aim(r, GeoTests.Truth(h, c)) is { } p)
            {
                h.Act(0, "guess", new { x = p.X, y = p.Y });
                h.Act(0, "ready");
            }
            GeoTests.Until(h, GeoMatch.PhaseReveal);
            h.Act(0, "next");
            h.Tick(1);
        }
        GeoTests.Until(h, GeoMatch.PhaseDone);
    }

    // ---------------------------------------------------------------- 1: «Де це? дня» у «Щоденному глеку»

    [Fact]
    public void Daily_is_in_the_daily_jug_with_a_daily_key()
    {
        Assert.Contains("geo-daily", new GameNames(RoomHarness.NewRegistry()).Daily);
        var clock = new FakeClock();
        var key = new GeoDaily().SoloKey("оля", clock);
        Assert.Equal($"daily:geo-daily:{Days.Today(clock)}:оля", key);
    }

    [Fact]
    public void Daily_writes_played_once_and_points_go_to_its_own_board_not_as_attempts()
    {
        using var c = new GeoCache();
        var board = new GeoDailyBoard(null);
        var h = new RoomHarness("geo-daily", null, 1, With(c, board));
        Assert.True(h.Solo("Оля").Ok, h.Reply.Message);
        PlayDay(h, c, (r, t) => (t.X + 30 * r, t.Y));

        // у щоденний каркас — рівно «зіграно, одна спроба», без очок (інакше 18 000 очок стали б «18 000 спроб»)
        var e = Assert.Single(h.Scores);
        Assert.Equal(0, e.Score);
        Assert.Equal(1, e.Attempts);
        Assert.False(e.Missed);
        Assert.StartsWith("daily:geo-daily:", e.Key);
        Assert.Contains(h.Awards, w => w.Reason == "daily:geo-daily");

        var day = Days.Today(h.Clock);
        var row = board.Of(day, "Оля")!;
        Assert.True(row.Points > 20_000);
        Assert.Equal(5, row.Marks.EnumerateRunes().Count());
        Assert.Equal(row.Points, ((IDailyPoints)board).Points(day, "оля"));

        // друга спроба того самого дня — ні
        Assert.False(h.Rematch().Ok);
        Assert.Equal(GeoDaily.Played, h.Reply.Message);
        Assert.Single(h.Scores);
    }

    [Fact]
    public void Daily_without_a_single_pin_is_a_miss_not_a_streak()
    {
        using var c = new GeoCache();
        var board = new GeoDailyBoard(null);
        var h = new RoomHarness("geo-daily", null, 1, With(c, board));
        h.Solo("Оля");
        PlayDay(h, c, (_, _) => null);
        var e = Assert.Single(h.Scores);
        Assert.True(e.Missed);
        Assert.Null(board.Of(Days.Today(h.Clock), "Оля"));
        Assert.DoesNotContain(h.Awards, w => w.Reason == "daily:geo-daily");
    }

    [Fact]
    public void Daily_points_reach_the_today_panel()
    {
        using var rig = new EconomyRig();
        var board = new GeoDailyBoard(null);
        board.Record(rig.Daily.Today(), "Оля", 18_450, "🟩🟩🟨⬛🟩", rig.Clock.UtcNow);
        var daily = new Daily(rig.Store, new GameNames(RoomHarness.NewRegistry()), rig.Clock, [board]);
        daily.Record("geo-daily", "Оля", true, 1, 0);
        var me = daily.Status("Оля").Puzzles.Single(p => p.Game == "geo-daily").Me!;
        Assert.True(me.Solved);
        Assert.Equal(18_450, me.Points);
    }

    [Fact]
    public void Daily_reopened_after_the_finish_does_not_report_again()
    {
        using var c = new GeoCache();
        var board = new GeoDailyBoard(null);
        var h = new RoomHarness("geo-daily", null, 1, With(c, board));
        h.Solo("Оля");
        PlayDay(h, c, (r, t) => t);
        var saved = ((GeoDaily)h.Room.Game).Save()!;

        var again = new RoomHarness("geo-daily", null, 1, With(c, board));
        again.Solo("Оля");
        ((GeoDaily)again.Room.Game).Load(saved);
        Assert.Equal(GeoMatch.PhaseDone, GeoTests.Phase(again));
        Assert.Empty(again.Scores);
    }

    // ---------------------------------------------------------------- 2: таблиця дня й серія

    [Fact]
    public void Board_orders_by_points_keeps_the_first_result_and_counts_the_streak()
    {
        var b = new GeoDailyBoard(null);
        var at = DateTimeOffset.UnixEpoch;
        b.Record("2026-10-18", "Оля", 9000, "🟩", at);
        b.Record("2026-10-19", "Оля", 9000, "🟩", at);
        b.Record("2026-10-20", "Оля", 12_000, "🟩🟨", at);
        b.Record("2026-10-20", "Петро", 15_000, "🟩🟩", at);
        b.Record("2026-10-20", "Оля", 25_000, "🟩🟩🟩", at);   // повтор — не переписує
        b.Record("2026-10-16", "Петро", 1, "⬛", at);

        Assert.Equal(["Петро", "Оля"], b.Top("2026-10-20").Select(r => r.Nick));
        Assert.Equal(12_000, b.Of("2026-10-20", "оля")!.Points);
        Assert.Equal(3, b.Streak("Оля", "2026-10-20"));
        Assert.Equal(1, b.Streak("Петро", "2026-10-20"));
        // сьогодні ще не грала — серія тримається вчорашнім днем
        Assert.Equal(3, b.Streak("Оля", "2026-10-21"));
        Assert.Equal(0, b.Streak("Оля", "2026-10-22"));
        Assert.Equal(0, b.Streak("Хтось", "2026-10-20"));
    }
}
