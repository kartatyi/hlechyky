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
    static IServiceProvider With(GeoCache c, GeoDailyBoard? board = null, GeoRounds? rounds = null)
    {
        var s = new ServiceCollection().AddSingleton(c.Photos);
        if (board is not null) s.AddSingleton(board);
        if (rounds is not null) s.AddSingleton(rounds);
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

    [Fact]
    public void Day_view_shows_who_played_before_and_the_board_with_streak_after()
    {
        using var c = new GeoCache();
        var board = new GeoDailyBoard(null);
        var h = new RoomHarness("geo-daily", null, 1, With(c, board));
        var day = Days.Today(h.Clock);
        var yesterday = DateOnly.ParseExact(day, "yyyy-MM-dd").AddDays(-1).ToString("yyyy-MM-dd");
        board.Record(day, "Петро", 24_000, "🟩🟩🟩🟩🟩", h.Clock.UtcNow);
        board.Record(yesterday, "Оля", 5000, "🟩", h.Clock.UtcNow);
        h.Solo("Оля");

        var before = h.View(0).GetProperty("extra");
        Assert.Equal(["Петро"], before.GetProperty("played").EnumerateArray().Select(e => e.GetString()));
        Assert.False(before.TryGetProperty("board", out _));      // жодних очок до своєї спроби

        PlayDay(h, c, (r, t) => (t.X + 30 * r, t.Y));
        var after = h.View(0).GetProperty("extra");
        var rows = after.GetProperty("board").EnumerateArray().ToList();
        Assert.Equal(["Петро", "Оля"], rows.Select(r => r.GetProperty("nick").GetString()));
        Assert.True(rows[1].GetProperty("me").GetBoolean());
        Assert.Equal(5, rows[1].GetProperty("marks").GetString()!.EnumerateRunes().Count());
        Assert.Equal(2, rows[1].GetProperty("streak").GetInt32());
        Assert.Equal(2, after.GetProperty("place").GetInt32());
        Assert.Equal(2, after.GetProperty("streak").GetInt32());
    }

    // ---------------------------------------------------------------- 3: «🎯 Моя слабка»

    static void Note(GeoRounds h, string region, params double[] kms)
    {
        foreach (var km in kms) h.Note("Оля", "x", region, km, 1000, "solo", DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void Weak_region_is_the_worst_average_once_there_is_enough_data()
    {
        var h = new GeoRounds(null);
        Assert.Null(h.Info("Оля").Weak);
        Note(h, "Київ", 5, 15);
        Note(h, "Львівська область", 100, 120, 110);
        Note(h, "Сумська область", 300);                      // одна шпилька — випадковість, не «слабка»
        Assert.Null(h.Info("Оля").Weak);                       // 6 раундів < 10
        Assert.Equal(6, h.Info("оля").Rounds);
        Assert.Equal(3, h.Info("оля").Regions);
        Note(h, "Київ", 10, 10, 10, 10);
        var w = h.Info("Оля").Weak!;
        Assert.Equal("Львівська область", w.Region);
        Assert.Equal(110, w.AvgKm, 3);
        Assert.Equal(3, w.Rounds);
        Note(h, "Сумська область", 400);
        Assert.Equal("Сумська область", h.Info("Оля").Weak!.Region);
    }

    [Fact]
    public void Weak_region_needs_three_regions()
    {
        var h = new GeoRounds(null);
        Note(h, "Київ", 1, 2, 3, 4, 5, 6);
        Note(h, "Львівська область", 100, 100, 100, 100);
        Assert.Null(h.Info("Оля").Weak);
        Assert.Equal(10, h.Info("Оля").Rounds);
    }

    static GeoPlace At(string id, string region, double lat, double lon) => GeoCache.Place(id, lat: lat, lon: lon) with { Region = region };

    [Fact]
    public void Every_pin_lands_in_the_history_and_weak_training_deals_from_that_region_then_neighbours()
    {
        var bank = GeoCache.BankOf(
            At("w1", "Волинська область", 51.0, 25.0), At("w2", "Волинська область", 51.2, 25.3), At("w3", "Волинська область", 50.9, 25.6),
            At("n1", "Рівненська область", 50.8, 26.2), At("n2", "Рівненська область", 51.0, 26.5),
            At("f1", "Харківська область", 49.9, 36.2), At("f2", "Одеська область", 46.5, 30.7), At("f3", "Київ", 50.45, 30.5),
            At("f4", "Сумська область", 50.9, 34.8), At("f5", "Полтавська область", 49.6, 34.5));
        using var c = new GeoCache(bank);
        var rounds = new GeoRounds(null);
        var h = new RoomHarness("geo-solo", seed: 7, services: With(c, rounds: rounds));
        Assert.True(h.Solo("Оля").Ok, h.Reply.Message);

        // ще мало даних — чесна відмова, а не порожня партія
        var view = h.View(0).GetProperty("extra");
        Assert.Equal(JsonValueKind.Null, view.GetProperty("weak").ValueKind);
        PlayDay(h, c, (r, t) => (t.X + 40 * r, t.Y));
        Assert.Equal(5, rounds.Info("Оля").Rounds);             // кожна шпилька — у історію
        Assert.False(h.Act(0, GeoSolo.Weak).Ok);

        Note(rounds, "Волинська область", 400, 500);
        Note(rounds, "Київ", 1, 1, 1);
        var weak = rounds.Info("Оля").Weak!;
        Assert.Equal("Волинська область", weak.Region);
        Assert.Equal("Волинська область", h.View(0).GetProperty("extra").GetProperty("weak").GetProperty("region").GetString());

        Assert.True(h.Act(0, GeoSolo.Weak).Ok);
        Assert.True(h.Rematch().Ok, h.Reply.Message);
        var ids = new List<string>();
        for (var r = 0; r < 5; r++)
        {
            GeoTests.Until(h, GeoMatch.PhaseGuess);
            ids.Add(GeoTests.Current(h, c).Id);
            h.Act(0, "ready");
            GeoTests.Until(h, GeoMatch.PhaseReveal);
            h.Act(0, "next");
            h.Tick(1);
        }
        // усі три волинські + два найближчі рівненські, жодного далекого
        Assert.Equal(["n1", "n2", "w1", "w2", "w3"], ids.Order());
        Assert.Equal("Волинська область", h.View(0).GetProperty("extra").GetProperty("focus").GetString());

        // «Ще раз» після — знову з усієї України
        GeoTests.Until(h, GeoMatch.PhaseDone);
        Assert.True(h.Rematch().Ok, h.Reply.Message);
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("extra").GetProperty("focus").ValueKind);
    }
}
