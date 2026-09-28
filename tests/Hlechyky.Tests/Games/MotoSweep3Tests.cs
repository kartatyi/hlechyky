using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Мотоцикли, прохід №3: серія раундів без «Ще раз», «хто кого підрізав», турбо, мапи, звужене поле, боти й
/// команди 2×2. Усе вмикається опціями столу — типова партія та сама, що й була (її стережуть старі тести).
/// </summary>
public class MotoSweep3Tests(ITestOutputHelper output)
{
    static readonly string[] Nicks = ["Оля", "Петро", "Іра", "Сашко"];
    const int W = SnakeCore.W, RowA = SnakeCore.H / 2 - 3;

    static RoomHarness Duel(object? options = null)
    {
        var h = new RoomHarness("tron", options, seed: 7);
        h.Join("Оля");
        h.Join("Петро");
        return h;
    }

    static RoomHarness Party(int players, object? options = null)
    {
        var h = new RoomHarness("tron-party", options, seed: 7);
        for (var i = 0; i < players; i++) h.Join(Nicks[i]);
        h.Start();
        return h;
    }

    static int Cell(int x, int y, int w = W) => y * w + x;
    static string LastLog(RoomHarness h) => h.Outbox.OfType<Journal>().Last().Text;
    static int[] Ints(JsonElement v, string name) => [.. v.GetProperty(name).EnumerateArray().Select(e => e.GetInt32())];
    static JsonElement LastFrame(RoomHarness h) => Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);

    /// <summary>Петро звертає вгору одразу — і за 13 кроків б'ється в стелю: раунд Олин.</summary>
    static void PetroHitsTheCeiling(RoomHarness h)
    {
        h.Input(1, "turn", new { dir = 3 });
        h.Tick(13);
    }

    // ------------------------------------------------------------------ 156 серія раундів

    [Fact]
    public void A_duel_to_three_runs_rounds_by_itself_and_finishes_once()
    {
        var h = Duel(new { series = "3" });
        h.Tick(TronGame.StartTicks);
        PetroHitsTheCeiling(h);

        Assert.Equal(RoomStatus.Playing, h.Room.Status);   // раунд — ще не партія
        Assert.Empty(h.Finished);
        var v = h.View(null);
        Assert.Equal("x", v.GetProperty("winner").GetString());
        Assert.Equal([1, 0], Ints(v, "wins"));
        Assert.Equal(3, v.GetProperty("ser").GetInt32());
        Assert.True(v.GetProperty("nx").GetInt32() > 0);

        h.Tick(ArenaGame.PauseTicks);                      // 2 с табло — і новий раунд сам
        v = h.View(null);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("winner").ValueKind);
        Assert.Equal(ArenaGame.NextStartTicks, v.GetProperty("startIn").GetInt32());
        Assert.Equal(2, v.GetProperty("round").GetInt32());
        Assert.Equal(ArenaCore.StartLen, v.GetProperty("t")[0].GetArrayLength());

        for (var r = 2; r <= 3; r++)
        {
            h.Tick(ArenaGame.NextStartTicks);
            PetroHitsTheCeiling(h);
            if (r < 3) h.Tick(ArenaGame.PauseTicks);
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var done = Assert.Single(h.Finished);
        Assert.Equal([0], done.Result.Winners);            // одна партія — одна ставка й одне Ело
        Assert.Equal("Мотоцикли: серія до 3 — Оля жовтий 3:0 Петро зелений", LastLog(h));
    }

    [Fact]
    public void One_round_is_still_the_default_party()
    {
        var h = Duel();
        h.Tick(TronGame.StartTicks);
        PetroHitsTheCeiling(h);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("Мотоцикли: Оля жовтий 1:0 Петро зелений", LastLog(h));
        Assert.False(Views.Has(h.View(null), "ser"));
    }

    [Fact]
    public void Leaving_between_rounds_of_a_series_hands_the_party_over_while_the_seat_is_still_taken()
    {
        var h = Duel(new { series = "5" });
        h.Tick(TronGame.StartTicks);
        PetroHitsTheCeiling(h);
        h.Leave("Оля");                                    // веде 1:0 — і встає

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var done = Assert.Single(h.Finished);
        Assert.Equal([1], done.Result.Winners);
        Assert.Equal("Оля", done.Seats[0]);                // у результаті — серед тих, хто програв
    }

    [Fact]
    public void A_party_series_counts_rounds_per_rider_and_keeps_the_evening_separately()
    {
        var h = Party(2, new { series = "3" });
        h.Tick(TronGame.StartTicks);
        PetroHitsTheCeiling(h);
        Assert.Equal([1, 0, 0, 0], Ints(h.View(null), "wins"));
        Assert.Empty(h.Finished);
    }

    // ------------------------------------------------------------------ 159 хто кого підрізав

    [Fact]
    public void Crashing_into_someone_elses_trail_is_a_cut_with_a_line_in_the_journal()
    {
        var h = Duel();
        h.Tick(TronGame.StartTicks);
        h.Tick(7);                                         // Петро їде ліворуч до колонки 15
        h.Input(1, "turn", new { dir = 3 });
        h.Tick(6);                                         // …угору — у слід Олі, що проїхала тут кроком раніше

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var v = h.View(null);
        Assert.Equal([1, 0], Ints(v, "cuts"));
        var ko = v.GetProperty("ko")[0];
        Assert.Equal([1, 0, ArenaCore.CauseTrail], ko.EnumerateArray().Select(e => e.GetInt32()).ToArray());
        Assert.Contains("✂ Петро влітає у слід Олі", LastLog(h));
    }

    [Fact]
    public void A_wall_is_not_a_cut()
    {
        var h = Duel();
        h.Tick(TronGame.StartTicks);
        PetroHitsTheCeiling(h);
        Assert.Equal([0, 0], Ints(h.View(null), "cuts"));
        Assert.DoesNotContain("✂", LastLog(h));
    }

    // ------------------------------------------------------------------ 157 турбо

    [Fact]
    public void Turbo_makes_two_steps_a_tick_for_a_second_then_recharges_for_five()
    {
        var h = Duel(new { turbo = "1" });
        h.Tick(TronGame.StartTicks);
        h.Act(0, "turbo");
        h.Tick();

        var f = LastFrame(h);
        Assert.Equal(Cell(5, RowA), f.GetProperty("h")[0].GetInt32());
        Assert.Equal(Cell(4, RowA), f.GetProperty("h2")[0].GetInt32());   // клітинку посередині клієнт домалює з кадру
        Assert.Equal(-1, f.GetProperty("h2")[1].GetInt32());
        Assert.Equal(ArenaCore.TurboTicks - 1, f.GetProperty("tb")[0].GetInt32());

        h.Tick(ArenaCore.TurboTicks - 1);
        f = LastFrame(h);
        Assert.Equal(Cell(3 + 2 * ArenaCore.TurboTicks, RowA), f.GetProperty("h")[0].GetInt32());
        Assert.Equal(-ArenaCore.TurboCool, f.GetProperty("tb")[0].GetInt32());
        h.Act(0, "turbo");                                 // ще не перезарядилось
        h.Tick();
        Assert.Equal(Cell(4 + 2 * ArenaCore.TurboTicks, RowA), LastFrame(h).GetProperty("h")[0].GetInt32());
    }

    [Fact]
    public void Turbo_is_ignored_on_a_table_without_it()
    {
        var h = Duel();
        h.Tick(TronGame.StartTicks);
        h.Act(0, "turbo");
        h.Tick();
        var f = LastFrame(h);
        Assert.Equal(Cell(4, RowA), f.GetProperty("h")[0].GetInt32());
        Assert.False(Views.Has(f, "tb"));
        Assert.False(Views.Has(f, "h2"));
    }

    // ------------------------------------------------------------------ 158 мапи

    [Theory]
    [InlineData(26, 18)]
    [InlineData(34, 24)]
    public void Every_map_leaves_every_start_and_six_cells_ahead_free(int w, int hgt)
    {
        foreach (var map in new[] { ArenaMaps.Columns, ArenaMaps.Cross })
            for (var n = 2; n <= 4; n++)
            {
                var core = new ArenaCore(new Random(1), w, hgt, 4, tailShrinks: false, apples: 0);
                core.Reset([.. Enumerable.Range(0, n)], 0, ArenaMaps.Blocks(map, w, hgt));
                Assert.NotEmpty(core.Walls);
                (int, int)[] d = [(1, 0), (0, 1), (-1, 0), (0, -1)];
                foreach (var (x, y, dir) in core.Starts(n).Take(n))
                    for (var k = 1; k <= ArenaCore.SafeAhead; k++)
                        Assert.False(core.Walls.Contains(core.Cell(x + d[dir].Item1 * k, y + d[dir].Item2 * k)), $"{map} {w}×{hgt} на {n}: стіна перед стартом");
                // дзеркально відносно центру — ніхто не стартує ближче до перешкоди, ніж суперник
                foreach (var c in core.Walls)
                    Assert.Contains(core.Cell(w - 1 - c % w, hgt - 1 - c / w), core.Walls);
            }
    }

    [Fact]
    public void Columns_on_the_duel_field_keep_all_nine_pillars()
    {
        var h = Duel(new { map = "columns" });
        var v = h.View(null);
        Assert.Equal("columns", v.GetProperty("map").GetString());
        Assert.Equal(9 * 4, v.GetProperty("walls").GetArrayLength());
    }

    [Fact]
    public void A_pillar_stops_a_rider_like_a_wall()
    {
        var core = new ArenaCore(new Random(1), W, SnakeCore.H, 2, tailShrinks: false, apples: 0);
        core.Reset([0, 1], 0, ArenaMaps.Blocks(ArenaMaps.Columns, W, SnakeCore.H));
        // колони 26×18: x 5–6, 12–13, 19–20 на рядках 3–4, 8–9, 13–14; Оля з (3, 6) — униз і праворуч
        core.Turn(0, 1);
        core.Step();                                       // (3, 7)
        core.Turn(0, 0);
        core.Step();                                       // (4, 7)
        core.Turn(0, 1);
        Assert.Empty(core.Step());                         // (4, 8)
        core.Turn(0, 0);
        var died = core.Step();                            // (5, 8) — колона
        Assert.Equal([0], died);
        Assert.Equal(ArenaCore.CauseWall, core.Cause[0]);
    }

    [Fact]
    public void On_the_torus_the_edge_is_not_a_wall()
    {
        var h = Duel(new { map = "torus" });
        h.Tick(TronGame.StartTicks);
        h.Tick(W - 3);                                     // Оля — через правий край на лівий
        var v = h.View(null);
        Assert.True(v.GetProperty("wrap").GetBoolean());
        Assert.Equal(0b11, v.GetProperty("al").GetInt32());
        Assert.Equal(Cell(0, RowA), v.GetProperty("t")[0][0].GetInt32());
        h.Tick();                                          // (1, 6) — її ж стартовий слід
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
    }

    [Fact]
    public void A_random_map_changes_every_round()
    {
        var h = Duel(new { map = "random", series = "5" });
        var seen = new List<string>();
        JsonElement V() => h.View(null);
        for (var r = 0; r < 4; r++)
        {
            while (V().GetProperty("startIn").GetInt32() > 0) h.Tick();
            seen.Add(V().GetProperty("map").GetString()!);
            // прямо: на будь-якій мапі обидва в стіну чи свій слід разом — нічия, і серія їде далі
            for (var i = 0; i < 30 && V().GetProperty("winner").ValueKind == JsonValueKind.Null; i++) h.Tick();
            Assert.Equal("draw", V().GetProperty("winner").GetString());
            while (V().GetProperty("winner").ValueKind != JsonValueKind.Null) h.Tick();
        }
        Assert.All(seen, m => Assert.Contains(m, ArenaMaps.Pool));
        for (var i = 1; i < seen.Count; i++) Assert.NotEqual(seen[i - 1], seen[i]);
    }

    // ------------------------------------------------------------------ 162 звужене поле

    [Fact]
    public void The_edge_grows_ring_by_ring_and_catches_a_head_in_it()
    {
        var core = new ArenaCore(new Random(1), W, SnakeCore.H, 2, tailShrinks: false, apples: 0);
        core.Reset([0, 1], 0);
        for (var k = 0; k < 3; k++) Assert.Empty(core.Squeeze());
        Assert.Equal(-1, core.Occ[core.Cell(2, 10)]);
        Assert.Equal(0, core.Occ[core.Cell(3, 10)]);
        var died = core.Squeeze();                         // кільце 3 — там голова Олі (3, 6)
        Assert.Contains(0, died);
        Assert.Equal(ArenaCore.CauseSqueeze, core.Cause[0]);
        for (var k = 0; k < 10; k++) core.Squeeze();
        Assert.Equal(core.MaxRing, core.Ring);             // посередині лишається смуга
        Assert.Equal(0, core.Occ[core.Cell(W / 2, SnakeCore.H / 2)]);
    }

    [Fact]
    public void The_frame_counts_down_to_the_first_ring()
    {
        var h = Party(2, new { squeeze = "1" });
        h.Tick(TronGame.StartTicks);
        h.Tick(5);
        var f = LastFrame(h);
        Assert.Equal(ArenaGame.SqueezeAt - 5, f.GetProperty("sq").GetInt32());
        Assert.Equal(0, f.GetProperty("rg").GetInt32());
    }

    // ------------------------------------------------------------------ 160 боти

    [Fact]
    public void Alone_at_the_table_you_ride_against_a_bot_and_nobody_gets_paid()
    {
        var h = Party(1);
        var v = h.View(null);
        Assert.Equal([true, true, false, false], v.GetProperty("present").EnumerateArray().Select(e => e.GetBoolean()).ToArray());
        Assert.Equal(ArenaGame.BotNames[0], v.GetProperty("bots")[1].GetString());

        h.Tick(TronGame.StartTicks + 600);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var done = Assert.Single(h.Finished);
        Assert.Empty(done.Result.Winners);                 // з ботами — без нагород і таблиць
        Assert.Contains("🤖", LastLog(h));
    }

    [Fact]
    public void Bots_fill_empty_seats_and_steer_around_trails()
    {
        var h = Party(1, new { bots = "3", field = "big" });
        h.Tick(TronGame.StartTicks);
        h.Tick(40);                                        // людина мовчить і врізається; боти мають пережити її
        var v = h.View(null);
        Assert.Equal(4, v.GetProperty("present").EnumerateArray().Count(e => e.GetBoolean()));
        Assert.True((v.GetProperty("al").GetInt32() & 0b1110) != 0, "боти мали б об'їхати стіни");
    }

    [Fact]
    public void Two_people_and_two_bots_still_play_for_nothing()
    {
        var h = Party(2, new { bots = "2" });
        h.Tick(TronGame.StartTicks + 1200);
        var done = Assert.Single(h.Finished);
        Assert.Empty(done.Result.Winners);
    }

    // ------------------------------------------------------------------ 161 команди

    [Fact]
    public void A_teammates_trail_is_not_deadly_but_an_enemys_is()
    {
        var teams = Party(4, new { teams = "1" });
        var ffa = Party(4);
        foreach (var h in new[] { teams, ffa }) h.Tick(TronGame.StartTicks + 13);

        var v = teams.View(null);
        Assert.Equal([0, 1, 1, 0], Ints(v, "teams"));
        Assert.Equal(0b1111, v.GetProperty("al").GetInt32());   // синій і рожевий проїхали по сліду своїх
        Assert.Equal(0b0011, ffa.View(null).GetProperty("al").GetInt32());
    }

    [Fact]
    public void A_team_wins_together_even_with_one_of_them_out()
    {
        var h = Party(4, new { teams = "1" });
        h.Tick(TronGame.StartTicks + 1200);
        var done = Assert.Single(h.Finished);
        var w = done.Result.Winners;
        Assert.True(w.Length == 0 || (w.Length == 2 && (w.Order().SequenceEqual([0, 3]) || w.Order().SequenceEqual([1, 2]))), string.Join(",", w));
    }

    // ------------------------------------------------------------------ швидкодія

    [Fact]
    public void Four_bots_with_every_option_keep_the_tick_cheap_and_the_frame_small()
    {
        var h = Party(1, new { bots = "3", field = "big", turbo = "1", squeeze = "1", map = "columns", series = "5" });
        var game = (ArenaGame)h.Room.Game;
        var sw = new Stopwatch();
        var ticks = 0;
        var maxFrame = 0;
        for (var i = 0; i < 6000 && h.Room.Status == RoomStatus.Playing; i++)
        {
            sw.Start();
            h.Tick();
            sw.Stop();
            ticks++;
            if (h.Outbox.LastOrDefault() is RoomFrame rf) maxFrame = Math.Max(maxFrame, Views.WireBytes(rf.Frame));
        }
        var perTick = sw.Elapsed.TotalMilliseconds / ticks;
        output.WriteLine($"тиків {ticks}, раундів {game.RoundNo}, {perTick:F4} мс на тик кімнати, кадр до {maxFrame} Б");
        Assert.True(perTick < 0.25, $"{perTick:F4} мс");
        Assert.True(maxFrame < 1500, $"{maxFrame} Б");
    }
}
