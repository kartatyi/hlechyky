using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Забіг дня, прохід №3: привиди друзів (журнал вводу найкращої спроби) і кубок тижня.</summary>
[Collection(SerialPerf.Name)]
public class DinoDailyGhostsTests
{
    const string Day = "2026-09-10";

    static (RoomHarness H, DinoGhosts G) Open(string nick, DinoGhosts? g = null)
    {
        g ??= new DinoGhosts(null);
        var h = new RoomHarness("dino-daily", seed: 3, services: RoomHarness.WithService(g));
        Assert.True(h.Solo(nick).Ok);
        return (h, g);
    }

    /// <summary>Чесний забіг «як людина»: стрибає раз на кілька кроків, ввід приходить трохи наперед і трохи запізно.</summary>
    static int Play(RoomHarness h, int seed)
    {
        var rng = new Random(seed);
        var sim = ((DinoDaily)h.Room.Game).World!;
        h.Input(0, "in", new { s = 0, k = 5 });
        var held = 1;
        for (var guard = 0; guard < 20000 && h.Room.Status != RoomStatus.Finished; guard++)
        {
            if (rng.Next(3) == 0)
            {
                held = rng.Next(4) == 0 ? 2 : held == 1 ? 0 : 1;
                var k = held | (held == 1 ? 4 : 0);
                var s = sim.S + rng.Next(-6, 9);   // хтось попереду, хтось запізнився (перемотування)
                h.Input(0, "in", new { s = Math.Max(0, s), k });
            }
            h.Tick();
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        return (int)h.Scores.Last().Score;
    }

    static DinoGhost? Wait(DinoGhosts g, string key)
    {
        for (var i = 0; i < 200; i++)
        {
            var (_, mine) = g.Pick(Day, key, 0);
            if (mine is not null) return mine;
            Thread.Sleep(20);
        }
        return null;
    }

    [Fact]
    public void Encode_and_decode_round_trip()
    {
        int[] log = [0, 5, 3, 1, 3, 0, 40, 6, 1300, 2];
        var s = DinoGhosts.Encode(log);
        Assert.Equal("05.31.00.116.z02", s);
        Assert.Equal(log, DinoGhosts.Decode(s));
        Assert.Empty(DinoGhosts.Decode(""));
    }

    [Fact]
    public void Honest_run_becomes_a_ghost_whose_replay_gives_the_same_metres()
    {
        var (h, g) = Open("Оля");
        var metres = Play(h, 7);
        Assert.True(metres > 0);
        var ghost = Wait(g, "оля");
        Assert.NotNull(ghost);
        Assert.Equal(metres, ghost!.Metres);
        var seed = Days.Seed("dino-daily", Day);
        Assert.Equal(metres, DinoGhosts.Replay(seed, DinoGhosts.Decode(ghost.Log)));
    }

    [Fact]
    public void Friend_sees_the_ghost_in_the_view_and_own_ghost_comes_back_as_mine()
    {
        var (h, g) = Open("Оля");
        var metres = Play(h, 11);
        Assert.NotNull(Wait(g, "оля"));

        var (p, _) = Open("Петро", g);
        var v = p.View(0);
        var gh = v.GetProperty("ghosts");
        Assert.Equal(1, gh.GetArrayLength());
        Assert.Equal("Оля", gh[0].GetProperty("n").GetString());
        Assert.Equal(metres, gh[0].GetProperty("m").GetInt32());
        Assert.False(string.IsNullOrEmpty(gh[0].GetProperty("g").GetString()));
        Assert.Equal(JsonValueKind.Null, v.GetProperty("mine").ValueKind);

        Assert.True(h.Rematch().Ok);
        var again = h.View(0);
        Assert.Equal(0, again.GetProperty("ghosts").GetArrayLength());
        Assert.Equal(metres, again.GetProperty("mine").GetProperty("m").GetInt32());
    }

    [Fact]
    public void Tampered_or_worse_log_is_not_stored()
    {
        var g = new DinoGhosts(null);
        var seed = Days.Seed("dino-daily", Day);
        Assert.False(g.Store(Day, seed, "оля", "Оля", 99999, [0, 5]));
        int[] idle = [0, 5];
        var m = DinoGhosts.Replay(seed, idle);
        Assert.True(m > 0);
        Assert.True(g.Store(Day, seed, "оля", "Оля", m, idle));
        Assert.False(g.Store(Day, seed, "оля", "Оля", m, idle));   // не краще — лишається старий
    }

    [Fact]
    public void Pick_prefers_friends_just_ahead_then_nearest_behind()
    {
        var g = new DinoGhosts(null);
        var seed = Days.Seed("dino-daily", Day);
        var m = DinoGhosts.Replay(seed, [0, 5]);
        foreach (var n in new[] { "а", "б", "в", "г", "ґ" }) Assert.True(g.Store(Day, seed, n, n, m, [0, 5]));
        var (friends, mine) = g.Pick(Day, "а", m - 1);
        Assert.Equal(3, friends.Count);
        Assert.NotNull(mine);
        Assert.DoesNotContain(friends, f => f.Key == "а");
    }

    // ---------- кубок тижня ----------

    [Fact]
    public void Week_starts_on_monday_by_kyiv_days()
    {
        Assert.Equal("2026-09-28", DinoCup.WeekStart("2026-09-28"));
        Assert.Equal("2026-09-28", DinoCup.WeekStart("2026-10-04"));
        Assert.Equal("2026-09-21", DinoCup.WeekStart("2026-09-27"));
    }

    [Fact]
    public void Cup_sums_three_best_days_best_run_of_each_day()
    {
        var rows = DinoCup.Of(
        [
            ("оля", "Оля", 900, "2026-09-28"), ("оля", "Оля", 1200, "2026-09-28"), ("оля", "Оля", 500, "2026-09-29"),
            ("оля", "Оля", 700, "2026-09-30"), ("оля", "Оля", 100, "2026-10-01"),
            ("петро", "Петро", 2000, "2026-09-28"),
        ], 10);
        Assert.Equal(2, rows.Count);
        Assert.Equal(("Оля", 2400, 4), (rows[0].Nick, rows[0].Total, rows[0].Days));
        Assert.Equal([1200, 700, 500], rows[0].Best);
        Assert.Equal(("Петро", 2000), (rows[1].Nick, rows[1].Total));
        var line = DinoCup.Line(rows)!;
        Assert.Contains("Оля", line);
        Assert.Contains("2 400 м", line);
        Assert.Contains("Петро 2 000 м", line);
        Assert.Null(DinoCup.Line([]));
    }

    [Fact]
    public void Poster_writes_journal_once_on_sunday_evening()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dino-cup-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        try
        {
            var db = new Db(Path.Combine(dir, "t.db"));
            db.With(c =>
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO game_results(room_id, game, round, nick_key, nick, outcome, score, created_at)
                    VALUES('r1', 'dino-daily', 1, 'оля', 'Оля', 'solo', 1500, '2026-09-29T10:00:00.0000000+00:00'),
                          ('r2', 'dino-daily', 1, 'петро', 'Петро', 'solo', 900, '2026-09-30T10:00:00.0000000+00:00'),
                          ('r3', 'dino-daily', 1, 'петро', 'Петро', 'solo', 5000, '2026-09-20T10:00:00.0000000+00:00')
                    """;
                cmd.ExecuteNonQuery();
            });
            var clock = new FakeClock { UtcNow = new DateTimeOffset(2026, 10, 4, 14, 0, 0, TimeSpan.Zero) };   // неділя, 17:00 Києва
            var outbox = new FakeOutbox();
            var poster = new DinoCupPoster(db, outbox, clock, null);
            Assert.False(poster.TryPost());
            clock.UtcNow = new DateTimeOffset(2026, 10, 4, 18, 0, 0, TimeSpan.Zero);                          // 21:00 Києва
            Assert.True(poster.TryPost());
            Assert.False(poster.TryPost());
            var line = Assert.Single(outbox.All.OfType<Journal>()).Text;
            Assert.Contains("Оля — 1 500 м", line);
            Assert.Contains("Петро 900 м", line);   // минулотижневі 5000 не рахуються
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
