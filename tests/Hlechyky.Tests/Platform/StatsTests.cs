using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Tests.Support;
using static Hlechyky.Tests.Support.Radio;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// «Хто скільки» з однаковим перемикачем періоду: межі день · тиждень · місяць · весь час за київськими днями,
/// місяць у таблицях ігор і «Часі», топ закидальників без Глека серед людей, рейтинг треків (кеш — лише адміну),
/// ряд «Часто граємо» в лобі.
/// </summary>
public class StatsTests
{
    /// <summary>Субота, 12:00 за Києвом: день почався 25.09 о 21:00 UTC, тиждень — 19.09 о 21:00, місяць — 27.08 о 21:00.</summary>
    static readonly DateTimeOffset Now = new(2026, 9, 26, 9, 0, 0, TimeSpan.Zero);

    static EconomyRig Rig()
    {
        var rig = new EconomyRig();
        rig.Clock.UtcNow = Now;
        return rig;
    }

    static readonly GameInfo Ttt = EconomyRig.Info("ttt", "Хрестики-нолики", "хрестики-нолики");
    static readonly GameInfo Tron = EconomyRig.Info("tron", "Трон", "трон", max: 4);
    static readonly GameInfo Chess = EconomyRig.Info("chess", "Шахи", "шахи", rated: true);

    // ---------- періоди ----------

    [Fact]
    public void Periods_are_kyiv_days()
    {
        var clock = new FakeClock { UtcNow = Now };
        Assert.Equal("2026-09-26", Periods.FirstDay("day", clock));
        Assert.Equal("2026-09-20", Periods.FirstDay("week", clock));
        Assert.Equal("2026-08-28", Periods.FirstDay("month", clock));
        Assert.Null(Periods.FirstDay("all", clock));
        Assert.Null(Periods.FirstDay("year", clock));
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 21, 0, 0, TimeSpan.Zero), Periods.Since("day", clock));
        Assert.Equal(new DateTimeOffset(2026, 9, 19, 21, 0, 0, TimeSpan.Zero), Periods.Since("week", clock));
        Assert.Equal(new DateTimeOffset(2026, 8, 27, 21, 0, 0, TimeSpan.Zero), Periods.Since("month", clock));
        Assert.Equal(DateTimeOffset.MinValue, Periods.Since("all", clock));
        Assert.Equal(DateTimeOffset.MinValue, Periods.Since(null, clock));
        Assert.True(Periods.Known("month"));
        Assert.True(Periods.Known("all"));
        Assert.False(Periods.Known("year"));
        Assert.False(Periods.Known(""));
        Assert.False(Periods.Known(null));

        // пів на першу ночі за Києвом — уже новий день, хоч в UTC ще вчора
        clock.UtcNow = new DateTimeOffset(2026, 9, 25, 21, 30, 0, TimeSpan.Zero);
        Assert.Equal("2026-09-26", Periods.FirstDay("day", clock));
    }

    // ---------- місяць у таблицях ігор і «Часі» ----------

    [Fact]
    public void Game_table_knows_the_month()
    {
        using var rig = Rig();
        rig.Clock.UtcNow = new DateTimeOffset(2026, 8, 27, 20, 0, 0, TimeSpan.Zero);   // 27.08, 23:00 за Києвом — за день до місяця
        rig.Events.Raise(rig.Finished("old", Ttt, ["Оля", "Петро"], [0]));
        rig.Clock.UtcNow = new DateTimeOffset(2026, 8, 27, 21, 30, 0, TimeSpan.Zero);  // 28.08, 00:30 — перший день місяця
        rig.Events.Raise(rig.Finished("edge", Ttt, ["Оля", "Петро"], [0]));
        rig.Clock.UtcNow = Now.AddDays(-1);
        rig.Events.Raise(rig.Finished("fresh", Ttt, ["Оля", "Петро"], [1]));
        rig.Clock.UtcNow = Now;

        Dictionary<string, int> Wins(string period)
        {
            var e = Views.Json(rig.Boards.Leaderboard("ttt", period, null));
            Assert.Equal(period, e.GetProperty("period").GetString());
            return e.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("nick").GetString()!, r => r.GetProperty("wins").GetInt32());
        }

        Assert.Equal(new Dictionary<string, int> { ["Оля"] = 2, ["Петро"] = 1 }, Wins("all"));
        Assert.Equal(new Dictionary<string, int> { ["Оля"] = 1, ["Петро"] = 1 }, Wins("month"));
        Assert.Equal(new Dictionary<string, int> { ["Оля"] = 0, ["Петро"] = 1 }, Wins("week"));
        // невідомий період — як було: за весь час
        Assert.Equal("all", Views.Json(rig.Boards.Leaderboard("ttt", "year", null)).GetProperty("period").GetString());
    }

    [Fact]
    public void Time_page_knows_the_month()
    {
        using var rig = Rig();
        rig.Store.AddTime([
            new TimeRow("оля", PlayClock.Site, "2026-08-27", 100),   // за день до місяця
            new TimeRow("оля", PlayClock.Site, "2026-08-28", 30),
            new TimeRow("оля", PlayClock.Site, "2026-09-26", 5),
        ]);

        int Site(string period)
        {
            var e = Views.Json(rig.Boards.Time(period));
            return e.GetProperty("people")[0].GetProperty("site").GetInt32();
        }

        Assert.Equal("month", Views.Json(rig.Boards.Time("month")).GetProperty("period").GetString());
        Assert.Equal(35, Site("month"));
        Assert.Equal(5, Site("week"));
        Assert.Equal(5, Site("day"));
        Assert.Equal(135, Site("all"));
        Assert.Equal("all", Views.Json(rig.Boards.Time("year")).GetProperty("period").GetString());
    }

    // ---------- топ закидальників ----------

    static JsonElement Top(EconomyRig rig, string? period, int? days = null) =>
        Views.Json(PeopleEndpoints.Top(period, days, rig.Db, rig.Clock, new FixedOptions<SiteOptions>(new SiteOptions())));

    static (string Nick, int Count)[] People(JsonElement top) => top.GetProperty("requesters").EnumerateArray()
        .Select(x => (x.GetProperty("nick").GetString()!, x.GetProperty("count").GetInt32())).ToArray();

    [Fact]
    public void Top_keeps_people_and_the_dj_apart()
    {
        using var rig = Rig();
        Track(rig.Db, "a");
        Track(rig.Db, "voice-ad", "Дядько Глек", "Реклама глека");
        Play(rig.Db, "a", "user", "Оля", Now.AddHours(-3));
        Play(rig.Db, "a", "user", "оля", Now.AddHours(-2));      // та сама людина, інший регістр
        Play(rig.Db, "a", "user", "Петро", Now.AddHours(-1));
        Play(rig.Db, "a", "autodj", null, Now.AddMinutes(-50));   // Глек сам
        Play(rig.Db, "a", "autodj", null, Now.AddMinutes(-40));
        Play(rig.Db, "voice-ad", "user", "Дядько Глек", Now.AddMinutes(-30));   // реклама від джингла — від його імені

        var e = Top(rig, "day");

        Assert.Equal("day", e.GetProperty("period").GetString());
        Assert.Equal([("оля", 2), ("Петро", 1)], People(e));   // найсвіжіше написання
        Assert.Equal("Дядько Глек", e.GetProperty("dj").GetProperty("nick").GetString());
        Assert.Equal(3, e.GetProperty("dj").GetProperty("count").GetInt32());
    }

    [Fact]
    public void Top_has_no_dj_when_he_played_nothing()
    {
        using var rig = Rig();
        Track(rig.Db, "a");
        Play(rig.Db, "a", "user", "Оля", Now.AddHours(-1));
        Play(rig.Db, "a", "autodj", null, Now.AddDays(-2));   // учора й раніше — не сьогодні

        var e = Top(rig, "day");

        Assert.Equal([("Оля", 1)], People(e));
        Assert.Equal(JsonValueKind.Null, e.GetProperty("dj").ValueKind);
        Assert.Equal(1, Top(rig, "week").GetProperty("dj").GetProperty("count").GetInt32());
    }

    [Fact]
    public void Top_periods_are_kyiv_days_and_old_days_still_work()
    {
        using var rig = Rig();
        Track(rig.Db, "a");
        Play(rig.Db, "a", "user", "Оля", Now.AddDays(-40));
        Play(rig.Db, "a", "user", "Оля", new DateTimeOffset(2026, 8, 27, 21, 30, 0, TimeSpan.Zero));    // перший день місяця
        Play(rig.Db, "a", "user", "Петро", new DateTimeOffset(2026, 9, 19, 20, 30, 0, TimeSpan.Zero));  // 23:30 напередодні тижня
        Play(rig.Db, "a", "user", "Петро", Now.AddHours(-1));

        Assert.Equal([("Петро", 1)], People(Top(rig, "day")));
        Assert.Equal([("Петро", 1)], People(Top(rig, "week")));
        Assert.Equal([("Петро", 2), ("Оля", 1)], People(Top(rig, "month")));
        Assert.Equal([("Петро", 2), ("Оля", 2)], People(Top(rig, "all")));   // порівну — свіжіший вище

        // старі клієнти: ?days= — стільки разів по 24 години назад від зараз, і period у відповіді нема
        var legacy = Top(rig, null, 7);
        Assert.Equal(JsonValueKind.Null, legacy.GetProperty("period").ValueKind);
        Assert.Equal([("Петро", 2)], People(legacy));
        Assert.Equal([("Петро", 2)], People(Top(rig, "year")));   // невідомий період — як без нього (7 днів)
        Assert.Equal([("Петро", 1)], People(Top(rig, "day", 30)));  // period головніший за days
    }

    // ---------- рейтинг треків ----------

    [Fact]
    public void Rating_shows_the_cache_to_the_admin_only()
    {
        using var rig = Rig();
        Track(rig.Db, "a");
        Play(rig.Db, "a", "user", "Оля", Now.AddHours(-1));
        var asked = 0;
        Func<((long, int), long)> disk = () =>
        {
            asked++;
            return ((1024L, 3), 5_000L);
        };

        var member = Views.Json(PeopleEndpoints.RatingOf(As("Оля"), "week", null, null, rig.Db, rig.Clock, disk));
        Assert.Equal(JsonValueKind.Null, member.GetProperty("cache").ValueKind);
        Assert.Equal(0, asked);   // іншим навіть теку не обходимо
        Assert.Equal("a", member.GetProperty("tracks")[0].GetProperty("track").GetProperty("id").GetString());

        var admin = Views.Json(PeopleEndpoints.RatingOf(As("Влад", admin: true), "week", null, null, rig.Db, rig.Clock, disk));
        var cache = admin.GetProperty("cache");
        Assert.Equal(1024, cache.GetProperty("bytes").GetInt64());
        Assert.Equal(3, cache.GetProperty("files").GetInt32());
        Assert.Equal(5_000, cache.GetProperty("limitBytes").GetInt64());
        Assert.Equal(1, asked);
    }

    [Fact]
    public void Rating_period_beats_days()
    {
        using var rig = Rig();
        Track(rig.Db, "a");
        Track(rig.Db, "b");
        Play(rig.Db, "a", "user", "Оля", Now.AddHours(-1));
        Play(rig.Db, "b", "user", "Оля", Now.AddDays(-3));

        string[] Ids(string? period, int? days) => Views.Json(PeopleEndpoints.RatingOf(As("Оля"), period, days, "plays", rig.Db, rig.Clock, () => ((0, 0), 0)))
            .GetProperty("tracks").EnumerateArray().Select(t => t.GetProperty("track").GetProperty("id").GetString()!).Order().ToArray();

        Assert.Equal(["a"], Ids("day", null));
        Assert.Equal(["a"], Ids("day", 30));
        Assert.Equal(["a", "b"], Ids("week", null));
        Assert.Equal(["a", "b"], Ids(null, 30));
        Assert.Equal(["a"], Ids(null, 1));
        Assert.Equal(["a", "b"], Ids("all", null));
    }

    // ---------- «Часто граємо» ----------

    [Fact]
    public void Popular_counts_tables_and_people_in_the_window()
    {
        using var rig = Rig();
        rig.Names.Learn(EconomyRig.Info("clicker", "Гончарне коло", "гончарне коло", GameGroup.Solo, max: 1, score: ScoreOrder.HigherIsBetter));
        rig.Clock.UtcNow = Now.AddDays(-40);
        rig.Events.Raise(rig.Finished("old", Chess, ["Оля", "Петро"], [0]));            // поза вікном
        rig.Clock.UtcNow = Now.AddDays(-3);
        rig.Events.Raise(rig.Finished("t1", Tron, ["Оля", "Петро"], [0]));
        rig.Events.Raise(rig.Finished("t1", Tron, ["Оля", "Петро"], [1], round: 2));   // той самий стіл, друга партія
        rig.Events.Raise(rig.Finished("t2", Tron, ["Оля", "Марко"], [0]));
        rig.Events.Raise(new SoloScoreEvent("clicker", "Оля", 10, ScoreOrder.HigherIsBetter, "clicker:оля", Now.AddDays(-3)));
        rig.Events.Raise(new SoloScoreEvent("clicker", "Оля", 20, ScoreOrder.HigherIsBetter, "clicker:оля", Now.AddDays(-3).AddHours(1)));
        rig.Events.Raise(new SoloScoreEvent("clicker", "Оля", 30, ScoreOrder.HigherIsBetter, "clicker:оля", Now.AddDays(-2)));
        rig.Clock.UtcNow = Now.AddDays(-1);
        rig.Events.Raise(rig.Finished("c1", Chess, ["Оля", "Петро"], [1]));
        rig.Clock.UtcNow = Now;

        var e = Views.Json(PeopleEndpoints.Popular(null, rig.Store, rig.Clock));

        Assert.Equal(30, e.GetProperty("days").GetInt32());
        var games = e.GetProperty("games").EnumerateArray().ToList();
        Assert.Equal(["tron", "clicker", "chess"], games.Select(g => g.GetProperty("game").GetString()));
        Assert.Equal(2, games[0].GetProperty("rooms").GetInt32());
        Assert.Equal(3, games[0].GetProperty("players").GetInt32());
        Assert.Equal(Now.AddDays(-3).AddSeconds(120), games[0].GetProperty("last").GetDateTimeOffset());
        Assert.Equal(2, games[1].GetProperty("rooms").GetInt32());      // соло: людина за день — один «стіл»
        Assert.Equal(1, games[1].GetProperty("players").GetInt32());
        Assert.Equal(1, games[2].GetProperty("rooms").GetInt32());      // старої партії у вікні нема
        Assert.Equal(2, games[2].GetProperty("players").GetInt32());

        // два дні — це вчора й сьогодні за Києвом
        var two = Views.Json(PeopleEndpoints.Popular(2, rig.Store, rig.Clock));
        Assert.Equal(["chess"], two.GetProperty("games").EnumerateArray().Select(g => g.GetProperty("game").GetString()));
        Assert.Equal(365, Views.Json(PeopleEndpoints.Popular(10_000, rig.Store, rig.Clock)).GetProperty("days").GetInt32());
        Assert.Equal(1, Views.Json(PeopleEndpoints.Popular(0, rig.Store, rig.Clock)).GetProperty("days").GetInt32());
    }
}
