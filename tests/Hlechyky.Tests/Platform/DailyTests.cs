using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>Щоденний глек: день за Києвом, серії, топ дня, форма відповіді (specs/daily.md).</summary>
public class DailyTests
{
    static string Day(EconomyRig rig, int shift) =>
        DateOnly.ParseExact(rig.Daily.Today(), "yyyy-MM-dd").AddDays(shift).ToString("yyyy-MM-dd");

    [Fact]
    public void Today_follows_kyiv_midnight()
    {
        using var rig = new EconomyRig();
        rig.Clock.UtcNow = new DateTimeOffset(2026, 9, 9, 20, 59, 0, TimeSpan.Zero);
        Assert.Equal("2026-09-09", rig.Daily.Today());

        rig.Clock.UtcNow = new DateTimeOffset(2026, 9, 9, 21, 1, 0, TimeSpan.Zero);
        Assert.Equal("2026-09-10", rig.Daily.Today());
    }

    [Fact]
    public void Start_of_day_is_kyiv_midnight_in_utc()
    {
        Assert.Equal(new DateTimeOffset(2026, 9, 9, 21, 0, 0, TimeSpan.Zero), Daily.StartOfDayUtc("2026-09-10"));
    }

    [Fact]
    public void Record_upserts_instead_of_duplicating()
    {
        using var rig = new EconomyRig();
        rig.Daily.Record("wordle", "Оля", solved: false, attempts: 0, ms: 0);
        rig.Daily.Record("wordle", "Оля", solved: true, attempts: 4, ms: 0);
        rig.Daily.Record("wordle", "Оля", solved: true, attempts: 6, ms: 0);

        var row = rig.Daily.MyResult("Оля", "wordle")!;
        Assert.True(row.Solved);
        Assert.Equal(4, row.Attempts);      // гірший результат кращий не псує
        Assert.Equal(1, rig.Store.DailySolvedCount(rig.Daily.Today(), "wordle"));
    }

    [Fact]
    public void Nick_case_does_not_split_the_daily_row()
    {
        using var rig = new EconomyRig();
        rig.Daily.Record("wordle", "Оля", solved: true, attempts: 3, ms: 0);
        rig.Daily.Record("wordle", "оля", solved: true, attempts: 2, ms: 0);
        Assert.Equal(1, rig.Store.DailySolvedCount(rig.Daily.Today(), "wordle"));
        Assert.Equal(2, rig.Daily.MyResult("ОЛЯ", "wordle")!.Attempts);
    }

    [Fact]
    public void Top_is_sorted_by_attempts_then_time()
    {
        using var rig = new EconomyRig();
        rig.Daily.Record("wordle", "Оля", true, 3, 5_000);
        rig.Daily.Record("wordle", "Петро", true, 2, 40_000);
        rig.Daily.Record("wordle", "Гриць", true, 2, 10_000);
        rig.Daily.Record("wordle", "Ніна", false, 0, 0);

        var top = rig.Daily.Top("wordle");
        Assert.Equal(["Гриць", "Петро", "Оля"], top.Select(t => t.Nick));
    }

    [Fact]
    public void Three_days_in_a_row_make_a_streak_of_three()
    {
        using var rig = new EconomyRig();
        foreach (var shift in new[] { -2, -1, 0 })
            rig.Daily.Record("wordle", "Оля", true, 3, 0, Day(rig, shift));

        Assert.Equal(3, rig.Daily.Streak("Оля", "wordle"));
    }

    [Fact]
    public void A_gap_yesterday_resets_the_streak()
    {
        using var rig = new EconomyRig();
        rig.Daily.Record("wordle", "Оля", true, 3, 0, Day(rig, -3));
        rig.Daily.Record("wordle", "Оля", true, 3, 0, Day(rig, -2));

        Assert.Equal(0, rig.Daily.Streak("Оля", "wordle"));
    }

    [Fact]
    public void Solving_today_after_a_gap_leaves_a_streak_of_one()
    {
        using var rig = new EconomyRig();
        rig.Daily.Record("wordle", "Оля", true, 3, 0, Day(rig, -3));
        rig.Daily.Record("wordle", "Оля", true, 3, 0, Day(rig, 0));

        Assert.Equal(1, rig.Daily.Streak("Оля", "wordle"));
    }

    [Fact]
    public void Yesterdays_streak_survives_a_morning_without_a_game()
    {
        using var rig = new EconomyRig();
        rig.Daily.Record("wordle", "Оля", true, 3, 0, Day(rig, -2));
        rig.Daily.Record("wordle", "Оля", true, 3, 0, Day(rig, -1));

        Assert.Equal(2, rig.Daily.Streak("Оля", "wordle"));
    }

    [Fact]
    public void An_unsolved_day_does_not_count_towards_the_streak()
    {
        using var rig = new EconomyRig();
        rig.Daily.Record("wordle", "Оля", true, 3, 0, Day(rig, -1));
        rig.Daily.Record("wordle", "Оля", false, 0, 0, Day(rig, 0));

        Assert.Equal(1, rig.Daily.Streak("Оля", "wordle"));
    }

    [Fact]
    public void Status_has_the_shape_the_panel_expects()
    {
        using var rig = new EconomyRig();
        rig.Names.Learn(EconomyRig.Info("wordle", "Глек-слово", "Глек-слово", GameGroup.Solo, 1, score: ScoreOrder.LowerIsBetter));
        rig.Daily.Record("wordle", "Оля", true, 3, 0);

        var e = Views.Json(rig.Daily.Status("Оля"));
        Assert.Equal(rig.Daily.Today(), e.GetProperty("day").GetString());
        Assert.True(e.GetProperty("no").GetInt32() >= 1);
        Assert.True(Views.Has(e, "nextMidnight"));
        Assert.True(Views.Has(e, "puzzles"));
    }

    [Fact]
    public void Status_lists_every_registered_daily_puzzle()
    {
        using var rig = new EconomyRig();
        // у збірці щоденних ігор поки нема — панель має бути порожньою, а не падати
        Assert.Empty(rig.Daily.Puzzles);
        Assert.Empty(rig.Daily.Status("Оля").Puzzles);
    }

    [Fact]
    public void Days_of_different_puzzles_do_not_mix()
    {
        using var rig = new EconomyRig();
        rig.Daily.Record("wordle", "Оля", true, 2, 0);
        rig.Daily.Record("mines-daily", "Оля", true, 1, 30_000);

        Assert.Equal(2, rig.Daily.MyResult("Оля", "wordle")!.Attempts);
        Assert.Equal(30_000, rig.Daily.MyResult("Оля", "mines-daily")!.Ms);
        Assert.Equal(1, rig.Store.DailySolvedCount(rig.Daily.Today(), "wordle"));
    }

    [Fact]
    public void Streaks_of_all_puzzles_match_the_single_game_answer()
    {
        using var rig = new EconomyRig();
        var today = rig.Daily.Today();
        var yesterday = DateOnly.ParseExact(today, "yyyy-MM-dd").AddDays(-1).ToString("yyyy-MM-dd");
        rig.Daily.Record("wordle", "Оля", solved: true, 3, 0, yesterday);
        rig.Daily.Record("wordle", "Оля", solved: true, 2, 0, today);
        rig.Daily.Record("mines", "Оля", solved: true, 1, 40_000, today);

        var all = rig.Daily.Streaks("Оля");
        Assert.Equal(rig.Daily.Streak("Оля", "wordle"), all["wordle"]);
        Assert.Equal(2, all["wordle"]);
        Assert.Equal(1, all["mines"]);
        Assert.Equal(0, all.GetValueOrDefault("skilky"));
    }
}
