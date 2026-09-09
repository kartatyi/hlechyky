using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>Ачівки: видача один раз, черепки один раз, рядок у Журналі, перевірки на подіях.</summary>
public class AchievementsTests
{
    static readonly GameInfo Ttt = EconomyRig.Info("ttt", "Хрестики-нолики", "хрестики-нолики");
    static readonly GameInfo Duel = EconomyRig.Info("duel", "Дуель", "дуель", GameGroup.Live);

    [Fact]
    public void Catalog_has_no_duplicate_keys_and_every_entry_is_filled_in()
    {
        Assert.Equal(AchievementCatalog.All.Count, AchievementCatalog.All.Select(a => a.Key).Distinct().Count());
        Assert.True(AchievementCatalog.All.Count >= 22);
        Assert.All(AchievementCatalog.All, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Title));
            Assert.False(string.IsNullOrWhiteSpace(a.Text));
            Assert.False(string.IsNullOrWhiteSpace(a.Icon));
            Assert.True(a.Reward >= 0);
        });
    }

    [Fact]
    public void Unlock_pays_once_and_writes_one_journal_line()
    {
        using var rig = new EconomyRig();
        Assert.True(rig.Achievements.Unlock("Оля", "first-win"));
        Assert.False(rig.Achievements.Unlock("Оля", "first-win"));

        Assert.Equal(10, rig.Economy.Balance("Оля"));
        var unlocked = rig.Outbox.Of<AchievementUnlocked>();
        Assert.Single(unlocked);
        Assert.Equal("Перша перемога", unlocked[0].Title);
        Assert.Equal(10, unlocked[0].Reward);

        var journal = rig.Outbox.Of<Journal>();
        Assert.Single(journal);
        Assert.Contains("Оля", journal[0].Text);
        Assert.Contains("Перша перемога", journal[0].Text);
        Assert.Contains("🏅", journal[0].Text);
    }

    [Fact]
    public void Unknown_key_is_ignored()
    {
        using var rig = new EconomyRig();
        Assert.False(rig.Achievements.Unlock("Оля", "не-існує"));
        Assert.Empty(rig.Outbox.Of<AchievementUnlocked>());
    }

    [Fact]
    public void First_game_and_first_win_come_with_the_first_finished_game()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(rig.Finished("r1", Ttt, ["Оля", "Петро"], [0]));

        Assert.True(rig.Achievements.Has("Оля", "first-game"));
        Assert.True(rig.Achievements.Has("Оля", "first-win"));
        Assert.True(rig.Achievements.Has("Петро", "first-game"));
        Assert.False(rig.Achievements.Has("Петро", "first-win"));
    }

    [Fact]
    public void Ten_wins_needs_ten_wins()
    {
        using var rig = new EconomyRig();
        for (var i = 0; i < 9; i++)
            rig.Events.Raise(rig.Finished($"r{i}", Ttt, ["Оля", "Петро"], [0]));
        Assert.False(rig.Achievements.Has("Оля", "ten-wins"));

        rig.Events.Raise(rig.Finished("r9", Ttt, ["Оля", "Петро"], [0]));
        Assert.True(rig.Achievements.Has("Оля", "ten-wins"));
    }

    [Fact]
    public void Three_wins_in_a_row_give_the_streak()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(rig.Finished("r1", Ttt, ["Оля", "Петро"], [0]));
        rig.Events.Raise(rig.Finished("r2", Ttt, ["Оля", "Петро"], [1], round: 2));
        rig.Events.Raise(rig.Finished("r3", Ttt, ["Оля", "Петро"], [0], round: 3));
        rig.Events.Raise(rig.Finished("r4", Ttt, ["Оля", "Петро"], [0], round: 4));
        Assert.False(rig.Achievements.Has("Оля", "streak-3"));

        rig.Events.Raise(rig.Finished("r5", Ttt, ["Оля", "Петро"], [0], round: 5));
        Assert.True(rig.Achievements.Has("Оля", "streak-3"));
    }

    [Fact]
    public void Winning_a_table_with_a_stake_of_twentyfive_is_a_high_roller()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(rig.Finished("r1", Ttt, ["Оля", "Петро"], [0], stake: 25));
        Assert.True(rig.Achievements.Has("Оля", "high-roller"));
        Assert.False(rig.Achievements.Has("Петро", "high-roller"));
    }

    [Fact]
    public void Ten_wins_in_the_duel_make_a_cowboy()
    {
        using var rig = new EconomyRig();
        for (var i = 0; i < 10; i++)
            rig.Events.Raise(rig.Finished($"d{i}", Duel, ["Оля", "Петро"], [0]));
        Assert.True(rig.Achievements.Has("Оля", "duel-10"));
    }

    [Fact]
    public void Hundred_shards_on_the_balance_is_noticed()
    {
        using var rig = new EconomyRig();
        rig.Economy.Grant("Оля", 99, "listen");
        Assert.False(rig.Achievements.Has("Оля", "rich-100"));

        rig.Economy.Grant("Оля", 1, "listen");
        Assert.True(rig.Achievements.Has("Оля", "rich-100"));
    }

    [Fact]
    public void Potter_achievements_come_from_solo_scores()
    {
        using var rig = new EconomyRig();
        var now = rig.Clock.UtcNow;
        rig.Events.Raise(new SoloScoreEvent("clicker", "Оля", 999, ScoreOrder.HigherIsBetter, "clicker:оля", now));
        Assert.False(rig.Achievements.Has("Оля", "potter-1k"));

        rig.Events.Raise(new SoloScoreEvent("clicker", "Оля", 1_000, ScoreOrder.HigherIsBetter, "clicker:оля", now));
        Assert.True(rig.Achievements.Has("Оля", "potter-1k"));
        Assert.False(rig.Achievements.Has("Оля", "potter-100k"));

        rig.Events.Raise(new SoloScoreEvent("clicker", "Оля", 100_000, ScoreOrder.HigherIsBetter, "clicker:оля", now));
        Assert.True(rig.Achievements.Has("Оля", "potter-100k"));
    }

    [Fact]
    public void Wordle_in_two_tries_and_a_fast_sapper_are_seen_from_daily_results()
    {
        using var rig = new EconomyRig();
        rig.Achievements.OnDaily("Оля", new DailyRow("2026-09-10", "wordle", "оля", "Оля", true, 2, 0), 1);
        rig.Achievements.OnDaily("Оля", new DailyRow("2026-09-10", "mines-daily", "оля", "Оля", true, 1, 45_000), 1);

        Assert.True(rig.Achievements.Has("Оля", "wordle-2"));
        Assert.True(rig.Achievements.Has("Оля", "mines-fast"));
        Assert.False(rig.Achievements.Has("Оля", "wordle-7"));
    }

    [Fact]
    public void Seven_days_of_wordle_in_a_row_is_a_week_of_words()
    {
        using var rig = new EconomyRig();
        var day = DateOnly.ParseExact(rig.Daily.Today(), "yyyy-MM-dd");
        for (var i = 6; i >= 0; i--)
            rig.Daily.Record("wordle", "Оля", solved: true, attempts: 4, ms: 0, day: day.AddDays(-i).ToString("yyyy-MM-dd"));

        var row = rig.Daily.MyResult("Оля", "wordle")!;
        rig.Achievements.OnDaily("Оля", row, rig.Daily.Streak("Оля", "wordle"));
        Assert.True(rig.Achievements.Has("Оля", "wordle-7"));
    }

    [Fact]
    public void Online_hours_make_a_listener()
    {
        using var rig = new EconomyRig();
        rig.Achievements.OnOnlineMinutes("Оля", 599);
        Assert.False(rig.Achievements.Has("Оля", "listener-10h"));

        rig.Achievements.OnOnlineMinutes("Оля", 600);
        Assert.True(rig.Achievements.Has("Оля", "listener-10h"));
        Assert.False(rig.Achievements.Has("Оля", "listener-100h"));

        rig.Achievements.OnOnlineMinutes("Оля", 6_000);
        Assert.True(rig.Achievements.Has("Оля", "listener-100h"));
    }

    [Fact]
    public void Profile_lists_what_was_unlocked()
    {
        using var rig = new EconomyRig();
        rig.Achievements.Unlock("Оля", "first-win");
        rig.Achievements.Unlock("Оля", "rich-100");

        var mine = rig.Achievements.Of("Оля");
        Assert.Equal(2, mine.Count);
        Assert.Contains(mine, x => x.Info.Key == "first-win");
    }

    [Fact]
    public void Achievement_shards_are_granted_once_even_after_a_rebuild()
    {
        using var rig = new EconomyRig();
        rig.Achievements.Unlock("Оля", "ten-wins");
        rig.Economy.Rebuild();
        Assert.Equal(25, rig.Economy.Balance("Оля"));
    }

    [Fact]
    public void A_shot_faster_than_two_hundred_milliseconds_is_a_quick_hand()
    {
        using var rig = new EconomyRig();
        // дуель кладе найкращу реакцію в соло-таблицю (specs/duel.md) — «Швидка рука» перевіряється тут
        rig.Events.Raise(new SoloScoreEvent("duel", "Оля", 180, ScoreOrder.LowerIsBetter, null, rig.Clock.UtcNow));
        Assert.True(rig.Achievements.Has("Оля", "duel-fast"));
    }

    [Fact]
    public void A_slower_shot_is_not_a_quick_hand()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(new SoloScoreEvent("duel", "Петро", 250, ScoreOrder.LowerIsBetter, null, rig.Clock.UtcNow));
        Assert.False(rig.Achievements.Has("Петро", "duel-fast"));
    }

    [Fact]
    public void Hidden_board_games_count_towards_the_board_player()
    {
        using var rig = new EconomyRig();
        // морський бій і доміно — теж настільні, хоч і з прихованими видами
        var chess = EconomyRig.Info("chess", "Шахи", "шахи");
        var ships = new GameInfo("battleship", "Морський бій", "морський бій", GameGroup.Board, 2, 2, Hidden: true);
        var domino = new GameInfo("domino", "Доміно", "доміно", GameGroup.Board, 2, 2, Hidden: true);
        foreach (var i in new[] { chess, ships, domino }) rig.Names.Learn(i);

        rig.Events.Raise(rig.Finished("r1", chess, ["Оля", "Петро"], [0]));
        rig.Events.Raise(rig.Finished("r2", ships, ["Оля", "Петро"], [0]));
        Assert.False(rig.Achievements.Has("Оля", "all-boards"));

        rig.Events.Raise(rig.Finished("r3", domino, ["Оля", "Петро"], [0]));
        Assert.True(rig.Achievements.Has("Оля", "all-boards"));
    }

    [Fact]
    public void An_achievement_already_granted_does_not_touch_the_database_again()
    {
        using var rig = new EconomyRig();
        Assert.True(rig.Achievements.Unlock("Оля", "rich-100"));
        // друга спроба відсікається в пам'яті, тож ані черепків, ані рядка в Журналі не додається
        Assert.False(rig.Achievements.Unlock("Оля", "rich-100"));
        Assert.Equal(10, rig.Economy.Balance("Оля"));
        Assert.Single(rig.Outbox.Of<AchievementUnlocked>());
    }
}
