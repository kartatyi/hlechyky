namespace Hlechyky.Games.Economy;

/// <summary>
/// Таблиці й профіль (ARCHITECTURE §7). Форма відповідей — анонімні об'єкти: на дроті вони стають
/// camelCase, як і все інше на цьому сайті, а клієнтський каркас читає їх як є.
/// </summary>
public sealed class Leaderboards(EconomyStore store, Ratings ratings, Achievements achievements,
    Daily daily, GameNames names, Economy economy, IClock clock)
{
    public const int Rows = 20;

    /// <summary>Межа періоду: «сьогодні» і «тиждень» — за київськими днями, а не за 24 годинами назад.</summary>
    DateTimeOffset Since(string period) => period switch
    {
        "day" => Daily.StartOfDayUtc(Days.Today(clock)),
        "week" => Daily.StartOfDayUtc(DateOnly.ParseExact(Days.Today(clock), "yyyy-MM-dd").AddDays(-6).ToString("yyyy-MM-dd")),
        _ => DateTimeOffset.MinValue,
    };

    static string Norm(string? period) => period is "day" or "week" ? period : "all";

    /// <summary>Скільки перемог поспіль у ніка просто зараз (нічия серію не рве, поразка — рве).</summary>
    public int WinStreak(string nickKey)
    {
        var n = 0;
        foreach (var r in store.RecentResults(nickKey, 50, multiplayerOnly: true))
        {
            if (r.Outcome == "win") n++;
            else if (r.Outcome == "loss") break;
        }
        return n;
    }

    /// <summary>Найдовша серія перемог за останні партії — для профілю.</summary>
    public int BestStreak(string nickKey)
    {
        int best = 0, run = 0;
        foreach (var r in store.RecentResults(nickKey, 300, multiplayerOnly: true))
        {
            if (r.Outcome == "win") { run++; best = Math.Max(best, run); }
            else if (r.Outcome == "loss") run = 0;
        }
        return best;
    }

    /// <summary>GET /api/games/leaderboard?game=&amp;period=day|week|all[&amp;day=]</summary>
    public object Leaderboard(string? game, string? period, string? day)
    {
        var p = Norm(period);
        var since = Since(p);
        game = string.IsNullOrWhiteSpace(game) ? "shards" : game.Trim();

        if (game == "shards")
            return new
            {
                game, title = "Черепки", kind = "shards", period = p,
                rows = store.TopShards(Rows, since, byBalance: p == "all")
                    .Select(r => new { nick = r.Nick, balance = r.Balance, earned = r.Earned }),
            };

        if (game == "daily")
        {
            var d = string.IsNullOrWhiteSpace(day) ? daily.Today() : day.Trim();
            return new
            {
                game, title = "Щоденний глек", kind = "daily", period = p, day = d,
                rows = store.DailyOfDay(d, 100).Select(r => new
                {
                    game = r.Game, title = names.Title(r.Game), nick = r.Nick, attempts = r.Attempts, ms = r.Ms,
                }),
            };
        }

        var info = names.Get(game);
        var title = names.Title(game);

        if (info is { Solo: true } || info?.Score is ScoreOrder.HigherIsBetter or ScoreOrder.LowerIsBetter)
        {
            var higher = info!.Score != ScoreOrder.LowerIsBetter;
            return new
            {
                game, title, kind = "solo", period = p, order = higher ? "higher" : "lower",
                rows = store.TopSolo(game, since, higher, Rows)
                    .Select(r => new { nick = r.Nick, best = r.Best, tries = r.Tries }),
            };
        }

        if (info is { Rated: true, MaxPlayers: 2 })
            return new
            {
                game, title, kind = "rated", period = p,
                rows = ratings.Top(game, Rows).Select(r => new
                {
                    nick = r.Nick, elo = r.Elo, games = r.Games, wins = r.Wins, losses = r.Losses,
                    draws = r.Draws, streak = WinStreak(r.NickKey),
                }),
            };

        return new
        {
            game, title, kind = "wins", period = p,
            rows = store.TopWins(game, since, Rows).Select(r => new
            {
                nick = r.Nick, wins = r.Wins, draws = r.Draws, losses = r.Losses,
            }),
        };
    }

    /// <summary>GET /api/games/profile?nick=</summary>
    public object Profile(string nick)
    {
        var key = Economy.Key(nick);
        var w = economy.Wallet(nick);
        return new
        {
            nick = w.Nick,
            wallet = new { balance = w.Balance, earned = w.Earned, spent = w.Spent },
            streak = new { current = WinStreak(key), best = BestStreak(key) },
            ratings = ratings.AllOf(nick).Select(r => new
            {
                game = r.Game, title = names.Title(r.Game), elo = r.Elo, games = r.Games,
                wins = r.Wins, losses = r.Losses, draws = r.Draws,
            }),
            achievements = achievements.Of(nick).Select(a => new
            {
                key = a.Info.Key, title = a.Info.Title, text = a.Info.Text, icon = a.Info.Icon,
                reward = a.Info.Reward, at = a.At,
            }),
            recent = store.RecentResults(key, 15).Select(r => new
            {
                game = r.Game, title = names.Title(r.Game), outcome = r.Outcome, score = r.Score,
                opponents = r.Opponents, stake = r.Stake, at = r.At,
            }),
            daily = daily.Puzzles.Select(g => new { game = g, title = names.Title(g), streak = daily.Streak(nick, g) }),
        };
    }

    /// <summary>GET /api/games/wallet — свій гаманець.</summary>
    public object Wallet(string nick)
    {
        var w = economy.Wallet(nick);
        return new { nick = w.Nick, balance = w.Balance, earned = w.Earned, spent = w.Spent };
    }
}
