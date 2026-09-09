using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>Таблиці й профіль: форма на дроті, періоди, порядок рядків.</summary>
public class LeaderboardsTests
{
    static readonly GameInfo Chess = EconomyRig.Info("chess", "Шахи", "шахи", rated: true);
    static readonly GameInfo Ttt = EconomyRig.Info("ttt", "Хрестики-нолики", "хрестики-нолики");
    static readonly GameInfo Clicker = EconomyRig.Info("clicker", "Гончарне коло", "гончарне коло",
        GameGroup.Solo, max: 1, score: ScoreOrder.HigherIsBetter);

    static JsonElement Json(object o) => Views.Json(o);
    static JsonElement Rows(object o) => Json(o).GetProperty("rows");

    [Fact]
    public void Shards_table_shows_balance_and_earnings()
    {
        using var rig = new EconomyRig();
        rig.Economy.Grant("Оля", 30, "listen");
        rig.Economy.Grant("Петро", 10, "listen");

        var e = Json(rig.Boards.Leaderboard("shards", "all", null));
        Assert.Equal("shards", e.GetProperty("kind").GetString());
        var rows = e.GetProperty("rows");
        Assert.Equal("Оля", rows[0].GetProperty("nick").GetString());
        Assert.Equal(30, rows[0].GetProperty("balance").GetInt32());
        Assert.Equal(30, rows[0].GetProperty("earned").GetInt32());
    }

    [Fact]
    public void Shards_are_the_default_table()
    {
        using var rig = new EconomyRig();
        Assert.Equal("shards", Json(rig.Boards.Leaderboard(null, null, null)).GetProperty("game").GetString());
    }

    [Fact]
    public void Period_day_does_not_see_yesterdays_earnings()
    {
        using var rig = new EconomyRig();
        rig.Economy.Grant("Оля", 7, "listen");
        rig.Clock.Advance(TimeSpan.FromHours(24));

        // за сьогодні вона не заробила нічого — і в таблиці за сьогодні її нема зовсім,
        // інакше топ дня забивали б учорашні багатії з нулем
        Assert.Empty(Rows(rig.Boards.Leaderboard("shards", "day", null)).EnumerateArray());

        var all = Rows(rig.Boards.Leaderboard("shards", "all", null));
        Assert.Equal(7, all[0].GetProperty("earned").GetInt32());
        Assert.Equal(7, all[0].GetProperty("balance").GetInt32());
    }

    [Fact]
    public void Todays_shard_table_keeps_whoever_earned_today()
    {
        using var rig = new EconomyRig();
        rig.Economy.Grant("Оля", 7, "listen", "listen:оля:вчора");
        rig.Clock.Advance(TimeSpan.FromHours(24));
        rig.Economy.Grant("Петро", 2, "listen", "listen:петро:сьогодні");

        var today = Rows(rig.Boards.Leaderboard("shards", "day", null));
        Assert.Equal(1, today.GetArrayLength());
        Assert.Equal("Петро", today[0].GetProperty("nick").GetString());
        Assert.Equal(2, today[0].GetProperty("earned").GetInt32());
    }

    [Fact]
    public void Solo_table_for_a_day_does_not_show_an_older_record()
    {
        using var rig = new EconomyRig();
        rig.Names.Learn(Clicker);
        rig.Events.Raise(new SoloScoreEvent("clicker", "Оля", 900, ScoreOrder.HigherIsBetter, "clicker:оля", rig.Clock.UtcNow));
        rig.Clock.Advance(TimeSpan.FromDays(30));
        rig.Events.Raise(new SoloScoreEvent("clicker", "Оля", 10, ScoreOrder.HigherIsBetter, "clicker:оля", rig.Clock.UtcNow));

        // сьогодні вона накрутила лише десять глеків — місячної давнини рекорд у сьогоднішній топ не лізе
        var today = Rows(rig.Boards.Leaderboard("clicker", "day", null));
        Assert.Equal(10, today[0].GetProperty("best").GetInt64());
        Assert.Equal(1, today[0].GetProperty("tries").GetInt32());

        var all = Rows(rig.Boards.Leaderboard("clicker", "all", null));
        Assert.Equal(900, all[0].GetProperty("best").GetInt64());
        Assert.Equal(2, all[0].GetProperty("tries").GetInt32());
        // а ачівки гончаря дивляться на найкраще за весь час
        Assert.Equal(900, rig.Store.BestSolo("оля", "clicker", higherIsBetter: true));
    }

    [Fact]
    public void Multiplayer_game_with_a_score_is_not_a_solo_table()
    {
        using var rig = new EconomyRig();
        // ерудет чи дурень цілком природно виставлять Score: очки за партію є, але таблиця в них — перемоги
        var scored = EconomyRig.Info("scrabble", "Ерудет", "ерудет", score: ScoreOrder.HigherIsBetter);
        rig.Names.Learn(scored);
        rig.Events.Raise(rig.Finished("r1", scored, ["Оля", "Петро"], [0],
            scores: new Dictionary<int, long> { [0] = 240, [1] = 180 }));

        var e = Json(rig.Boards.Leaderboard("scrabble", "all", null));
        Assert.Equal("wins", e.GetProperty("kind").GetString());
        Assert.Equal("Оля", e.GetProperty("rows")[0].GetProperty("nick").GetString());
        Assert.Equal(1, e.GetProperty("rows")[0].GetProperty("wins").GetInt32());
    }

    [Fact]
    public void Rated_game_table_shows_elo_and_streak()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(rig.Finished("r1", Chess, ["Оля", "Петро"], [0]));
        rig.Events.Raise(rig.Finished("r2", Chess, ["Оля", "Петро"], [0], round: 2));

        var e = Json(rig.Boards.Leaderboard("chess", "all", null));
        Assert.Equal("rated", e.GetProperty("kind").GetString());
        var row = e.GetProperty("rows")[0];
        Assert.Equal("Оля", row.GetProperty("nick").GetString());
        Assert.Equal(2, row.GetProperty("wins").GetInt32());
        Assert.Equal(2, row.GetProperty("streak").GetInt32());
        Assert.True(row.GetProperty("elo").GetInt32() > 1000);
    }

    [Fact]
    public void Unrated_multiplayer_table_counts_wins()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(rig.Finished("r1", Ttt, ["Оля", "Петро"], [0]));
        rig.Events.Raise(rig.Finished("r2", Ttt, ["Оля", "Петро"], [1], round: 2));

        var e = Json(rig.Boards.Leaderboard("ttt", "all", null));
        Assert.Equal("wins", e.GetProperty("kind").GetString());
        Assert.Equal(2, e.GetProperty("rows").GetArrayLength());
        Assert.Equal(1, e.GetProperty("rows")[0].GetProperty("wins").GetInt32());
    }

    [Fact]
    public void Solo_table_keeps_the_best_result_and_counts_tries()
    {
        using var rig = new EconomyRig();
        rig.Names.Learn(Clicker);
        var now = rig.Clock.UtcNow;
        rig.Events.Raise(new SoloScoreEvent("clicker", "Оля", 120, ScoreOrder.HigherIsBetter, "clicker:оля", now));
        rig.Events.Raise(new SoloScoreEvent("clicker", "Оля", 900, ScoreOrder.HigherIsBetter, "clicker:оля", now));
        rig.Events.Raise(new SoloScoreEvent("clicker", "Петро", 300, ScoreOrder.HigherIsBetter, "clicker:петро", now));

        var e = Json(rig.Boards.Leaderboard("clicker", "all", null));
        Assert.Equal("solo", e.GetProperty("kind").GetString());
        Assert.Equal("higher", e.GetProperty("order").GetString());
        var rows = e.GetProperty("rows");
        Assert.Equal("Оля", rows[0].GetProperty("nick").GetString());
        Assert.Equal(900, rows[0].GetProperty("best").GetInt64());
        Assert.Equal(2, rows[0].GetProperty("tries").GetInt32());
    }

    [Fact]
    public void Daily_table_lists_who_solved_today()
    {
        using var rig = new EconomyRig();
        rig.Names.Learn(EconomyRig.Info("wordle", "Глек-слово", "Глек-слово", GameGroup.Solo, 1, score: ScoreOrder.LowerIsBetter));
        rig.Daily.Record("wordle", "Оля", solved: true, attempts: 3, ms: 0);
        rig.Daily.Record("wordle", "Петро", solved: true, attempts: 2, ms: 0);

        var e = Json(rig.Boards.Leaderboard("daily", "day", null));
        Assert.Equal("daily", e.GetProperty("kind").GetString());
        Assert.Equal(rig.Daily.Today(), e.GetProperty("day").GetString());
        var rows = e.GetProperty("rows");
        Assert.Equal("Петро", rows[0].GetProperty("nick").GetString());
        Assert.Equal("Глек-слово", rows[0].GetProperty("title").GetString());
    }

    [Fact]
    public void Profile_carries_wallet_ratings_achievements_and_history()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(rig.Finished("r1", Chess, ["Оля", "Петро"], [0]));

        var e = Json(rig.Boards.Profile("оля"));
        Assert.Equal("Оля", e.GetProperty("nick").GetString());
        Assert.True(e.GetProperty("wallet").GetProperty("balance").GetInt32() > 0);
        Assert.Equal("chess", e.GetProperty("ratings")[0].GetProperty("game").GetString());
        Assert.Equal("Шахи", e.GetProperty("ratings")[0].GetProperty("title").GetString());
        Assert.Equal(1, e.GetProperty("streak").GetProperty("current").GetInt32());
        Assert.Equal("win", e.GetProperty("recent")[0].GetProperty("outcome").GetString());
        Assert.Equal("Петро", e.GetProperty("recent")[0].GetProperty("opponents").GetString());
        Assert.Contains("first-win", e.GetProperty("achievements").EnumerateArray()
            .Select(a => a.GetProperty("key").GetString()));
    }

    [Fact]
    public void Wallet_of_an_unknown_nick_is_empty_but_well_formed()
    {
        using var rig = new EconomyRig();
        var e = Json(rig.Boards.Wallet("Ніхто"));
        Assert.Equal("Ніхто", e.GetProperty("nick").GetString());
        Assert.Equal(0, e.GetProperty("balance").GetInt32());
        Assert.Equal(0, e.GetProperty("earned").GetInt32());
        Assert.Equal(0, e.GetProperty("spent").GetInt32());
    }

    [Fact]
    public void Loss_breaks_the_streak_but_a_draw_does_not()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(rig.Finished("r1", Ttt, ["Оля", "Петро"], [0]));
        rig.Events.Raise(rig.Finished("r2", Ttt, ["Оля", "Петро"], [], draw: true, round: 2));
        rig.Events.Raise(rig.Finished("r3", Ttt, ["Оля", "Петро"], [0], round: 3));
        Assert.Equal(2, rig.Boards.WinStreak("оля"));

        rig.Events.Raise(rig.Finished("r4", Ttt, ["Оля", "Петро"], [1], round: 4));
        Assert.Equal(0, rig.Boards.WinStreak("оля"));
        Assert.Equal(2, rig.Boards.BestStreak("оля"));
    }

    [Fact]
    public void Unknown_game_gives_an_empty_table_not_an_error()
    {
        using var rig = new EconomyRig();
        var e = Json(rig.Boards.Leaderboard("немає-такої", "all", null));
        Assert.Equal(0, e.GetProperty("rows").GetArrayLength());
    }
}
