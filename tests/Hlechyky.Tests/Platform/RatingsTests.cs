using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>Ело: очікування, K, нічия, і те, що рахується воно лише для рейтингових ігор на двох.</summary>
public class RatingsTests
{
    static readonly GameInfo Chess = EconomyRig.Info("chess", "Шахи", "шахи", rated: true);
    static readonly GameInfo Ttt = EconomyRig.Info("ttt", "Хрестики-нолики", "хрестики-нолики");

    /// <summary>
    /// Виставити гравцеві рейтинг і кількість зіграних партій. «Награти» десять партій не годиться:
    /// туди-сюди перемоги з K=48 не повертають рівно тисячу, і тест перевіряв би не те, що треба.
    /// </summary>
    static void Seed(EconomyRig rig, string nick, int elo, int games) => rig.Db.Exec("""
        INSERT INTO ratings(nick_key, game, nick, elo, games, wins, losses, draws, updated_at)
        VALUES($n, 'chess', $nk, $e, $g, 0, 0, 0, $t)
        ON CONFLICT(nick_key, game) DO UPDATE SET elo = $e, games = $g
        """, ("$n", Economy.Key(nick)), ("$nk", nick), ("$e", elo), ("$g", games),
        ("$t", rig.Clock.UtcNow.ToString("o")));

    static void WarmUp(EconomyRig rig, string a, string b)
    {
        Seed(rig, a, 1000, 10);
        Seed(rig, b, 1000, 10);
    }

    [Fact]
    public void Equal_players_start_at_a_thousand()
    {
        using var rig = new EconomyRig();
        Assert.Equal(1000, rig.Ratings.Of("Оля", "chess").Elo);
        Assert.Equal(0, rig.Ratings.Of("Оля", "chess").Games);
    }

    [Fact]
    public void Expected_score_is_a_half_between_equals()
    {
        Assert.Equal(0.5, Ratings.Expected(1000, 1000), 6);
        Assert.True(Ratings.Expected(1200, 1000) > 0.75);
    }

    [Fact]
    public void First_ten_games_move_the_rating_by_fortyeight()
    {
        using var rig = new EconomyRig();
        var (a, b) = rig.Ratings.Apply("chess", ("оля", "Оля"), ("петро", "Петро"), 1);
        Assert.Equal(1024, a.Elo);
        Assert.Equal(976, b.Elo);
    }

    [Fact]
    public void After_ten_games_a_win_between_equals_is_sixteen()
    {
        using var rig = new EconomyRig();
        WarmUp(rig, "Оля", "Петро");
        Assert.Equal(1000, rig.Ratings.Of("Оля", "chess").Elo);
        Assert.Equal(10, rig.Ratings.Of("Оля", "chess").Games);

        var (a, b) = rig.Ratings.Apply("chess", ("оля", "Оля"), ("петро", "Петро"), 1);
        Assert.Equal(1016, a.Elo);
        Assert.Equal(984, b.Elo);
    }

    [Fact]
    public void Draw_between_equals_changes_nothing()
    {
        using var rig = new EconomyRig();
        WarmUp(rig, "Оля", "Петро");
        var (a, b) = rig.Ratings.Apply("chess", ("оля", "Оля"), ("петро", "Петро"), 0.5);
        Assert.Equal(1000, a.Elo);
        Assert.Equal(1000, b.Elo);
        Assert.Equal(1, a.Draws);
    }

    [Fact]
    public void Wins_losses_and_draws_are_counted()
    {
        using var rig = new EconomyRig();
        rig.Ratings.Apply("chess", ("оля", "Оля"), ("петро", "Петро"), 1);
        rig.Ratings.Apply("chess", ("оля", "Оля"), ("петро", "Петро"), 0.5);
        rig.Ratings.Apply("chess", ("оля", "Оля"), ("петро", "Петро"), 0);

        var olya = rig.Ratings.Of("Оля", "chess");
        Assert.Equal(3, olya.Games);
        Assert.Equal(1, olya.Wins);
        Assert.Equal(1, olya.Draws);
        Assert.Equal(1, olya.Losses);
    }

    [Fact]
    public void Finished_rated_game_updates_both_ratings()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(rig.Finished("r1", Chess, ["Оля", "Петро"], [0]));

        Assert.Equal(1024, rig.Ratings.Of("Оля", "chess").Elo);
        Assert.Equal(976, rig.Ratings.Of("Петро", "chess").Elo);
    }

    [Fact]
    public void Unrated_game_leaves_ratings_alone()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(rig.Finished("r1", Ttt, ["Оля", "Петро"], [0]));
        Assert.Equal(0, rig.Ratings.Of("Оля", "ttt").Games);
    }

    [Fact]
    public void Repeated_finish_does_not_move_the_rating_twice()
    {
        using var rig = new EconomyRig();
        var e = rig.Finished("r1", Chess, ["Оля", "Петро"], [0]);
        rig.Events.Raise(e);
        rig.Events.Raise(e);
        Assert.Equal(1, rig.Ratings.Of("Оля", "chess").Games);
    }

    [Fact]
    public void Leaving_the_table_is_an_ordinary_loss()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(rig.Finished("r1", Chess, ["Оля", "Петро"], [1], moves: 2, seconds: 3));
        Assert.Equal(1, rig.Ratings.Of("Петро", "chess").Wins);
        Assert.Equal(1, rig.Ratings.Of("Оля", "chess").Losses);
    }

    [Fact]
    public void Top_of_the_table_is_sorted_by_elo()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(rig.Finished("r1", Chess, ["Оля", "Петро"], [0]));
        var top = rig.Ratings.Top("chess");
        Assert.Equal("Оля", top[0].Nick);
        Assert.Equal("Петро", top[1].Nick);
    }
}
