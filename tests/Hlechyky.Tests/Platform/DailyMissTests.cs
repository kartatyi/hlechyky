using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>Пакет 4 (20.10): невдалий день щоденної головоломки пишеться в daily_results і видно на табло картки.</summary>
public class DailyMissTests
{
    static SoloScoreEvent Miss(EconomyRig rig, string nick, int attempts, string game = "wordle") =>
        new(game, nick, 0, ScoreOrder.LowerIsBetter, $"daily:{game}:{rig.Daily.Today()}:{EconomyStore.Key(nick)}",
            rig.Clock.UtcNow, attempts, Missed: true);

    [Fact]
    public void Miss_writes_an_unsolved_row_without_solo_result_or_streak()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(Miss(rig, "Оля", 6));

        var row = rig.Daily.MyResult("Оля", "wordle")!;
        Assert.False(row.Solved);
        Assert.Equal(6, row.Attempts);
        Assert.Equal(0, rig.Daily.Streak("Оля", "wordle"));
        Assert.Empty(rig.Daily.Top("wordle"));
        Assert.Equal(0, rig.Store.DailySolvedCount(rig.Daily.Today(), "wordle"));
        Assert.Equal(0, rig.Economy.Balance("Оля"));
    }

    [Fact]
    public void Repeated_miss_raises_attempts_and_a_later_solve_wins()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(Miss(rig, "Оля", 1, "mines-daily"));
        rig.Events.Raise(Miss(rig, "Оля", 2, "mines-daily"));
        Assert.Equal(2, rig.Daily.MyResult("Оля", "mines-daily")!.Attempts);

        rig.Daily.Record("mines-daily", "Оля", solved: true, attempts: 3, ms: 90_000);
        var row = rig.Daily.MyResult("Оля", "mines-daily")!;
        Assert.True(row.Solved);
        Assert.Equal(3, row.Attempts);
        Assert.Equal(90_000, row.Ms);

        // промах після розв'язку (той самий день) нічого не псує
        rig.Events.Raise(Miss(rig, "Оля", 4, "mines-daily"));
        row = rig.Daily.MyResult("Оля", "mines-daily")!;
        Assert.True(row.Solved);
        Assert.Equal(3, row.Attempts);
    }

    [Fact]
    public void Miss_with_a_non_daily_key_is_ignored()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(new SoloScoreEvent("wordle", "Оля", 0, ScoreOrder.LowerIsBetter, "solo:wordle:оля", rig.Clock.UtcNow, 6, Missed: true));
        Assert.Null(rig.Daily.MyResult("Оля", "wordle"));
    }

    [Fact]
    public void Panel_shows_my_missed_day()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(Miss(rig, "Оля", 6));
        var p = Assert.Single(rig.Store.DailyPanel(rig.Daily.Today(), EconomyStore.Key("Оля"), ["wordle"], 10));
        Assert.False(p.Mine!.Solved);
        Assert.Equal(6, p.Mine.Attempts);
        Assert.Equal(0, p.SolvedCount);
        Assert.Empty(p.Top);
    }

    [Fact]
    public void Day_card_lists_misses_until_the_same_nick_solves()
    {
        var card = new DailyCard(null, new FakeClock());
        card.NoteMiss("mines-daily", "Оля", 1);
        card.NoteMiss("mines-daily", "Оля", 2);
        card.NoteMiss("mines-daily", "Петро", 1);
        var c = card.Get("mines-daily")!;
        Assert.Equal(["Оля", "Петро"], c.Missed!.Select(r => r.Nick));
        Assert.Equal(2, c.Missed![0].Attempts);
        Assert.Equal(0, card.StreakOf("mines-daily", "Оля"));

        card.Note("mines-daily", "Оля", 3, 80_000);
        c = card.Get("mines-daily")!;
        Assert.Equal(["Петро"], c.Missed!.Select(r => r.Nick));
        Assert.Equal("Оля", Assert.Single(c.Rows).Nick);

        card.NoteMiss("mines-daily", "Оля", 4);   // після розв'язку промах не повертає в «✗»
        Assert.Equal(["Петро"], card.Get("mines-daily")!.Missed!.Select(r => r.Nick));
    }

    [Fact]
    public void Day_card_reads_todays_misses_from_the_base()
    {
        using var rig = new EconomyRig();
        rig.Daily.Record("mines-daily", "Оля", solved: false, attempts: 2, ms: 0);
        rig.Daily.Record("mines-daily", "Петро", solved: true, attempts: 1, ms: 50_000);
        var card = new DailyCard(rig.Db, rig.Clock);
        card.LoadNow("mines-daily");
        var c = card.Get("mines-daily")!;
        Assert.Equal("Петро", Assert.Single(c.Rows).Nick);
        var miss = Assert.Single(c.Missed!);
        Assert.Equal(("Оля", 2), (miss.Nick, miss.Attempts));
    }
}
