using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Надбавки «Щоденного глека» (25.10.2026): база × Boost, серія +5 % за день до +50 %, «повний глек» за 8 різних,
/// уночі — «за місце» й призи топу. Лише людям (акаунт, не бот).
/// </summary>
public class DailyPayTests
{
    static readonly string[] Games = ["g1", "g2", "g3", "g4", "g5", "g6", "g7", "g8", "g9", "g10"];

    sealed class Rig : IDisposable
    {
        public EconomyRig E { get; } = new(people: true);
        public Daily Daily { get; }
        public Rewards Rewards { get; }
        public DailyPrizes Prizes { get; }

        public Rig(IDailyPoints? points = null)
        {
            var o = E.Options;
            o.Boost = 10;
            o.DailyReward = 20;
            o.DailyStreakPct = 5;
            o.DailyStreakMaxPct = 50;
            o.DailyFullNeed = 8;
            o.DailyFullBonus = 50;
            o.DailyBestMax = 10;
            o.DailyTopPrizes = [30, 15, 8];
            o.DailyTopMinSolvers = 3;
            var names = new GameNames([], Games);
            var opts = new FixedOptions<EconomyOptions>(o);
            Daily = new Daily(E.Store, names, E.Clock, points is null ? null : [points]);
            E.Rewards.Detach();
            Rewards = new Rewards(E.Events, E.Economy, E.Store, E.Ratings, E.Achievements, Daily, names, E.Clock, opts,
                NullLogger<Rewards>.Instance);
            Rewards.Attach();
            Prizes = new DailyPrizes(Daily, E.Store, E.Economy, names, E.Clock, opts, E.Outbox, NullLogger<DailyPrizes>.Instance);
        }

        public string Day(int shift = 0) =>
            DateOnly.ParseExact(Daily.Today(), "yyyy-MM-dd").AddDays(shift).ToString("yyyy-MM-dd");

        /// <summary>Розв'язав головоломку: результат, тоді нагорода — як роблять ігри.</summary>
        public void Solve(string game, string nick, int attempts = 3, int ms = 0, string? day = null)
        {
            var d = day ?? Daily.Today();
            if (day is null)
            {
                E.Events.Raise(new SoloScoreEvent(game, nick, attempts, ScoreOrder.LowerIsBetter, $"daily:{game}:{d}:{Economy.Key(nick)}",
                    E.Clock.UtcNow, Attempts: attempts));
                E.Events.Raise(new AwardEvent(game, "r-" + game, nick, 0, "daily:" + game));
            }
            else Daily.Record(game, nick, solved: true, attempts, ms, d);
        }

        public void Dispose() => E.Dispose();
    }

    [Fact]
    public void Base_is_boosted_and_first_day_of_streak_adds_five_percent()
    {
        using var rig = new Rig();
        rig.Solve("g1", "Оля");
        Assert.Equal(200, rig.E.Paid("Оля", "daily:"));
        Assert.Equal(10, rig.E.Paid("Оля", "daily-streak:"));   // серія 1 → +5 %
    }

    [Fact]
    public void Streak_bonus_grows_and_stops_at_fifty_percent()
    {
        using var rig = new Rig();
        for (var i = 15; i >= 1; i--) rig.Solve("g1", "Оля", day: rig.Day(-i));
        rig.Solve("g1", "Оля");
        Assert.Equal(100, rig.E.Paid("Оля", "daily-streak:"));   // 16 днів — стеля +50 %
    }

    [Fact]
    public void Paying_twice_the_same_day_pays_nothing_more()
    {
        using var rig = new Rig();
        rig.Solve("g1", "Оля");
        rig.Solve("g1", "Оля");
        Assert.Equal(200, rig.E.Paid("Оля", "daily:"));
        Assert.Equal(10, rig.E.Paid("Оля", "daily-streak:"));
    }

    [Fact]
    public void Full_jug_pays_once_at_eight_of_ten()
    {
        using var rig = new Rig();
        for (var i = 0; i < 7; i++) rig.Solve(Games[i], "Оля");
        Assert.Equal(0, rig.E.Paid("Оля", "daily-full"));
        rig.Solve(Games[7], "Оля");
        Assert.Equal(500, rig.E.Paid("Оля", "daily-full"));
        rig.Solve(Games[8], "Оля");
        Assert.Equal(500, rig.E.Paid("Оля", "daily-full"));
    }

    [Fact]
    public void Guests_get_only_the_base()
    {
        using var rig = new Rig();
        for (var i = 0; i < 8; i++) rig.Solve(Games[i], "гість Іван");
        Assert.Equal(1600, rig.E.Paid("гість Іван", "daily:"));
        Assert.Equal(0, rig.E.Paid("гість Іван", "daily-streak:"));
        Assert.Equal(0, rig.E.Paid("гість Іван", "daily-full"));
    }

    [Fact]
    public void Night_pays_places_and_top_for_yesterday()
    {
        using var rig = new Rig();
        var y = rig.Day(-1);
        rig.Solve("g1", "Оля", attempts: 2, day: y);
        rig.Solve("g1", "Петро", attempts: 3, day: y);
        rig.Solve("g1", "Яся", attempts: 4, day: y);
        rig.Solve("g1", "Сем", attempts: 5, day: y);

        rig.Prizes.Settle(y);

        Assert.Equal(100, rig.E.Paid("Оля", "daily-best:"));
        Assert.Equal(67, rig.E.Paid("Петро", "daily-best:"));
        Assert.Equal(33, rig.E.Paid("Яся", "daily-best:"));
        Assert.Equal(0, rig.E.Paid("Сем", "daily-best:"));
        Assert.Equal(300, rig.E.Paid("Оля", "daily-top:"));
        Assert.Equal(150, rig.E.Paid("Петро", "daily-top:"));
        Assert.Equal(80, rig.E.Paid("Яся", "daily-top:"));
        Assert.Equal(0, rig.E.Paid("Сем", "daily-top:"));
        Assert.Single(rig.E.Outbox.Of<Journal>(), j => j.Text.StartsWith("☀ Щоденний глек") && j.Text.Contains("Оля"));

        rig.Prizes.Settle(y);   // повтор — нічого нового
        Assert.Equal(100, rig.E.Paid("Оля", "daily-best:"));
        Assert.Equal(300, rig.E.Paid("Оля", "daily-top:"));
    }

    [Fact]
    public void Night_without_enough_solvers_pays_places_but_no_top()
    {
        using var rig = new Rig();
        var y = rig.Day(-1);
        rig.Solve("g2", "Оля", attempts: 2, day: y);
        rig.Solve("g2", "Петро", attempts: 2, day: y);   // нічия — обоє перші
        rig.Prizes.Settle(y);
        Assert.Equal(100, rig.E.Paid("Оля", "daily-best:"));
        Assert.Equal(100, rig.E.Paid("Петро", "daily-best:"));
        Assert.Equal(0, rig.E.Paid("Оля", "daily-top:"));
    }

    sealed class Board(string game) : IDailyPoints
    {
        public Dictionary<string, long> Map { get; } = [];
        public string Game => game;
        public long? Points(string day, string nick) => Map.TryGetValue(nick, out var p) ? p : null;
    }

    [Fact]
    public void Points_games_rank_by_points_and_wait_until_the_board_is_read()
    {
        var pts = new Board("g3");
        using var rig = new Rig(pts);
        var y = rig.Day(-1);
        rig.Solve("g3", "Оля", attempts: 1, day: y);
        rig.Solve("g3", "Петро", attempts: 1, day: y);
        pts.Map["Петро"] = 4700;   // Олі на дошці ще нема — дошка дочитує базу

        Assert.Equal(-1, rig.Prizes.Settle(y, final: false));
        Assert.Equal(0, rig.E.Paid("Петро", "daily-best:"));

        pts.Map["Оля"] = 4350;
        Assert.True(rig.Prizes.Settle(y, final: false) > 0);
        Assert.Equal(100, rig.E.Paid("Петро", "daily-best:"));
        Assert.Equal(0, rig.E.Paid("Оля", "daily-best:"));
    }

    [Fact]
    public void Money_counts_today_left_and_last_night()
    {
        using var rig = new Rig();
        var y = rig.Day(-1);
        rig.Solve("g1", "Оля", attempts: 2, day: y);
        rig.Solve("g1", "Петро", attempts: 3, day: y);
        rig.Solve("g1", "Яся", attempts: 4, day: y);
        rig.Prizes.Settle(y);
        rig.Solve("g2", "Оля");

        var s = rig.Daily.Status("Оля");
        var m = rig.Daily.Money("Оля", s, rig.E.Options)!;
        Assert.Equal(210, m.Today);              // 200 + серія 1 (+5 %)
        Assert.Equal(210, m.ByGame["g2"]);
        // 9 нерозв'язаних: g1 із серією 1 → завтра 2 (+10 %), решта +5 %; плюс «повний глек»
        Assert.Equal(220 + 8 * 210 + 500, m.Left);
        Assert.Equal(y, m.NightDay);
        Assert.Equal(400, m.Night);              // 100 за місце + 300 за перше
        Assert.Equal("top", m.NightItems[0].Kind);

        Assert.Null(rig.Daily.Money("гість Іван", rig.Daily.Status("гість Іван"), rig.E.Options));
    }

    [Fact]
    public void Lone_solver_gets_half_for_place()
    {
        Assert.Equal(50, DailyPrizes.Best(100, 1, 1));
        Assert.Equal(0, DailyPrizes.Best(0, 1, 5));
    }

    [Fact]
    public void Pay_view_reports_boosted_numbers()
    {
        using var rig = new Rig();
        var p = DailyPay.Of(rig.E.Options, puzzles: 10, solved: 3);
        Assert.Equal(200, p.Reward);
        Assert.Equal(8, p.FullNeed);
        Assert.Equal(500, p.FullBonus);
        Assert.Equal(100, p.BestMax);
        Assert.Equal([300, 150, 80], p.Top);
    }
}
