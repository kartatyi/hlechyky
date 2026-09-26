using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Лелеки: політ на голому <see cref="RunnerSim"/> у режимі Storks (перешкоди ставимо руками, висоту
/// тримаємо рукою — Vy = G перед кроком), партія — через справжню кімнату.
/// </summary>
[Collection(SerialPerf.Name)]
public class StorksTests
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марта", "Богдан", "Ліна", "Тарас"];
    const int U = RunnerSim.Sub;

    static bool[] Seats(int n)
    {
        var a = new bool[RunnerSim.Seats];
        for (var i = 0; i < n; i++) a[i] = true;
        return a;
    }

    static RunnerSim Bare(int players = 1, bool feather = true)
    {
        var sim = new RunnerSim(RunnerMode.Storks, 5, Seats(players), 0, 1000, false, featherOn: feather);
        sim.ClearCourse();
        return sim;
    }

    /// <summary>Тримати висоту: гравітація цього кроку з'їсть рівно Vy.</summary>
    static void Hover(RunnerSim sim, int steps, int seat = 0)
    {
        for (var i = 0; i < steps; i++)
        {
            if (!sim.P[seat].Down) sim.P[seat].Vy = RunnerStorks.G;
            sim.Step();
        }
    }

    static int Ahead(RunnerSim sim, int px) => sim.PaceX(sim.S) + px * U;

    static void Flap(RunnerSim sim, int seat = 0)
    {
        Assert.True(sim.Input(seat, sim.S, 5));
        sim.Step();
        sim.Input(seat, sim.S, 0);
    }

    static RoomHarness Table(int players = 2, int seed = 42, object? options = null)
    {
        var h = new RoomHarness("storks", options, seed);
        foreach (var nick in Nicks.Take(players)) h.Join(nick);
        Assert.True(h.Start().Ok);
        return h;
    }

    static RunnerSim Sim(RoomHarness h) => ((Storks)h.Room.Game).World!;

    static string Ph(RoomHarness h) => h.View(null).GetProperty("ph").GetString()!;

    static void ToRun(RoomHarness h)
    {
        for (var i = 0; i < 200 && Ph(h) == "ready"; i++) h.Tick();
        Assert.Equal("run", Ph(h));
    }

    /// <summary>Тримати всіх у повітрі на 150 px без перешкод (щоб раунд жив, поки тест не скаже).</summary>
    static void Keep(RoomHarness h)
    {
        var sim = Sim(h);
        foreach (var p in sim.P)
            if (p.Plays && !p.Down) { p.Y = 150 * U; p.Vy = 0; }
    }

    static void Down(RoomHarness h, params int[] seats)
    {
        var sim = Sim(h);
        Keep(h);
        foreach (var s in seats) { sim.P[s].Feather = false; sim.P[s].Ifr = 0; sim.P[s].Y = 0; sim.P[s].Vy = -8; }
        h.Tick();
    }

    static void ThroughOver(RoomHarness h)
    {
        for (var i = 0; i < 200 && Ph(h) == "over"; i++) h.Tick();
    }

    static int[] Ints(JsonElement e, string name) => e.GetProperty(name).EnumerateArray().Select(x => x.GetInt32()).ToArray();

    // ---------- паспорт ----------

    [Fact]
    public void Passport_is_live_one_to_eight_by_host_with_client_runner_and_feather_option()
    {
        var info = new Storks().Info;
        Assert.Equal(("storks", "Лелеки", GameGroup.Live), (info.Id, info.Title, info.Group));
        Assert.Equal((1, 8), (info.MinPlayers, info.MaxPlayers));
        Assert.Equal(StartMode.ByHost, info.Start);
        Assert.Equal(40, info.TickMs);
        Assert.Equal("runner", info.Module);
        Assert.Equal(["rounds", "feather"], info.Options!.Select(o => o.Key));
        Assert.Equal("on", info.Options![1].Default);
    }

    [Fact]
    public void Seat_names_are_feminine_and_cover_eight_seats()
    {
        var g = new Storks();
        var names = Enumerable.Range(0, 8).Select(g.SeatName).ToList();
        Assert.Equal(8, names.Distinct().Count());
        Assert.All(names, n => Assert.True(n.EndsWith('а') || n.EndsWith('я'), n));
    }

    // ---------- політ ----------

    [Fact]
    public void Storks_hang_at_150_pixels_during_ready_and_start_falling_on_the_first_run_step()
    {
        var h = Table(2);
        h.Tick(40);
        Assert.Equal("ready", Ph(h));
        h.Input(0, "in", new { s = Sim(h).S, k = 5 });     // змах на відліку нічого не робить
        h.Tick(10);
        Assert.Equal(RunnerStorks.StartY, Sim(h).P[0].Y);
        Assert.Equal(0, Sim(h).P[0].Vy);
        ToRun(h);
        h.Tick();
        Assert.True(Sim(h).P[0].Y < RunnerStorks.StartY);
    }

    [Fact]
    public void A_stork_that_never_flaps_touches_the_ground_in_about_25_steps()
    {
        var sim = Bare();
        var steps = 0;
        while (sim.P[0].Hits == 0 && steps < 100) { sim.Step(); steps++; }
        Assert.InRange(steps, 24, 25);
        Assert.False(sim.P[0].Down);          // пір'я врятувало
    }

    [Fact]
    public void One_flap_lifts_60_pixels_in_15_steps()
    {
        // spec рахував неперервно (64 px за 16); у кроках: змах ставить 128, і гравітація діє в тому ж кроці
        var sim = Bare();
        sim.P[0].Y = 100 * U;
        Flap(sim);
        var top = sim.P[0].Y;
        var at = 1;
        for (var step = 2; step < 40; step++)
        {
            sim.Step();
            if (sim.P[0].Y > top) { top = sim.P[0].Y; at = step; }
        }
        Assert.Equal(100 * U + 960, top);
        Assert.Equal(15, at);
    }

    [Fact]
    public void The_ceiling_stops_the_stork_without_a_hit()
    {
        var sim = Bare();
        var top = 0;
        for (var i = 0; i < 200; i++)
        {
            if (i % 4 == 0) sim.Input(0, sim.S, 5); else if (i % 4 == 1) sim.Input(0, sim.S, 0);
            sim.Step();
            top = Math.Max(top, sim.P[0].Y);
            if (sim.P[0].Y == RunnerStorks.Ceiling) Assert.Equal(0, sim.P[0].Vy);
        }
        Assert.Equal(RunnerStorks.Ceiling, top);
        Assert.Equal(0, sim.P[0].Hits);
        Assert.True(sim.P[0].Feather);
    }

    [Fact]
    public void Falling_speed_is_capped_at_200()
    {
        var sim = Bare();
        sim.P[0].Y = RunnerStorks.Ceiling;
        sim.P[0].Ifr = 100;
        var min = 0;
        for (var i = 0; i < 40; i++) { sim.Step(); min = Math.Min(min, sim.P[0].Vy); }
        Assert.Equal(RunnerStorks.VyMin, min);
    }

    [Fact]
    public void The_first_hit_spends_the_feather_gives_40_iframes_and_tosses_the_stork_up()
    {
        var sim = Bare();
        var id = sim.Put(RunnerKind.Chimney, Ahead(sim, 20), 40 * U, 0, 200 * U);
        Hover(sim, 1);
        var p = sim.P[0];
        Assert.Equal(1, p.Hits);
        Assert.False(p.Feather);
        Assert.Equal(RunnerStorks.IFrames, p.Ifr);
        Assert.Equal(RunnerStorks.Flap, p.Vy);
        Assert.Equal(id, p.Lp);
        Assert.Equal(3, p.ModeOf(RunnerMode.Storks));
        Assert.False(p.Down);
    }

    [Fact]
    public void The_second_hit_is_out()
    {
        var sim = Bare(players: 2);
        sim.Put(RunnerKind.Chimney, Ahead(sim, 20), 40 * U, 0, 200 * U);
        sim.Put(RunnerKind.Chimney, Ahead(sim, 400), 40 * U, 0, 200 * U);
        sim.P[1].Y = 250 * U;
        for (var i = 0; i < 100 && !sim.P[0].Out; i++)
        {
            sim.P[0].Vy = RunnerStorks.G;
            sim.P[1].Vy = RunnerStorks.G;
            sim.Step();
        }
        Assert.True(sim.P[0].Out);
        Assert.Equal(2, sim.P[0].Hits);
        Assert.Equal(2, sim.P[0].Place);
        Assert.Equal(4, sim.P[0].ModeOf(RunnerMode.Storks));
    }

    [Fact]
    public void Iframes_ignore_obstacles_and_the_ground()
    {
        var sim = Bare();
        sim.P[0].Ifr = 30;
        sim.P[0].Y = 5 * U;
        sim.Put(RunnerKind.Chimney, Ahead(sim, 10), 40 * U, 0, 200 * U);
        for (var i = 0; i < 20; i++) { sim.P[0].Vy = 0; sim.P[0].Y = 0; sim.Step(); }
        Assert.Equal(0, sim.P[0].Hits);
        Assert.True(sim.P[0].Feather);
    }

    [Fact]
    public void Ground_hit_lifts_to_at_least_ten_pixels()
    {
        var sim = Bare();
        sim.P[0].Y = 16;
        sim.P[0].Vy = -200;
        sim.Step();
        Assert.Equal(1, sim.P[0].Hits);
        Assert.Equal(RunnerStorks.MinYAfterHit, sim.P[0].Y);
        Assert.Equal(-1, sim.P[0].Lp);          // земля в passed не заноситься
    }

    [Fact]
    public void Feather_off_option_makes_the_first_hit_fatal()
    {
        var sim = Bare(feather: false);
        sim.Put(RunnerKind.Chimney, Ahead(sim, 20), 40 * U, 0, 200 * U);
        Hover(sim, 3);
        Assert.True(sim.P[0].Out);
        Assert.Equal(1, sim.P[0].Hits);

        var h = Table(2, options: new { feather = "off" });
        Assert.False(h.View(null).GetProperty("featherOpt").GetBoolean());
        Assert.False(Sim(h).P[0].Feather);
        Assert.Equal(0, h.View(null).GetProperty("p")[0][3].GetInt32());
    }

    [Fact]
    public void The_same_obstacle_never_hits_twice()
    {
        var sim = Bare();
        sim.Put(RunnerKind.Wire, Ahead(sim, 10), 400 * U, 140 * U, 20 * U);   // довгий дріт на висоті
        sim.P[0].Y = 140 * U;
        for (var i = 0; i < 80; i++)
        {
            sim.P[0].Vy = RunnerStorks.G;
            sim.P[0].Ifr = 0;                                  // навіть без невразливості — той самий дріт не б'є
            sim.Step();
        }
        Assert.Equal(1, sim.P[0].Hits);
        Assert.False(sim.P[0].Down);
    }

    [Fact]
    public void A_chimney_is_passed_above_and_a_tree_below()
    {
        foreach (var (y, hits) in new[] { (140, 0), (100, 1) })
        {
            var sim = Bare();
            sim.Put(RunnerKind.Chimney, Ahead(sim, 30), 40 * U, 0, 120 * U);
            sim.P[0].Y = y * U;
            Hover(sim, 30);
            Assert.Equal(hits, sim.P[0].Hits);
        }
        foreach (var (y, hits) in new[] { (140, 0), (200, 1) })
        {
            var sim = Bare();
            sim.Put(RunnerKind.Tree, Ahead(sim, 30), 60 * U, (RunnerStorks.Sky - 110) * U, 110 * U);   // крона 180..290
            sim.P[0].Y = y * U;
            Hover(sim, 30);
            Assert.Equal(hits, sim.P[0].Hits);
        }
    }

    static IEnumerable<(RunnerObstacle A, RunnerObstacle B, int Run)> Pairs(int seed, int steps)
    {
        var sim = new RunnerSim(RunnerMode.Storks, seed, Seats(0), 0, 1000, false);
        var seen = new HashSet<int>();
        var list = new List<(RunnerObstacle, RunnerObstacle, int)>();
        for (var i = 0; i < steps; i++)
        {
            sim.Step();
            for (var k = 0; k + 1 < sim.ObstacleCount; k++)
            {
                var a = sim.Obstacle(k);
                if (!seen.Add(a.Id)) continue;
                list.Add((a, sim.Obstacle(k + 1), sim.S - 1));
            }
        }
        return list;
    }

    [Fact]
    public void A_gate_gap_shrinks_from_150_to_100_pixels_with_progress()
    {
        var gates = Pairs(8, 7000).Where(p => p.A.Kind == RunnerKind.Chimney && p.B.Kind == RunnerKind.Tree && p.A.X == p.B.X).ToList();
        Assert.True(gates.Count > 10);
        foreach (var (a, b, run) in gates)
        {
            var gap = (b.Base - a.H) / U;
            Assert.InRange(gap, 100, 150);
            Assert.Equal(RunnerStorks.Sky * U, b.Base + b.H);
            Assert.Equal(44 * U, a.W);
        }
        Assert.Contains(gates, g => (g.B.Base - g.A.H) / U == 100);
        Assert.Contains(gates, g => (g.B.Base - g.A.H) / U >= 135);
    }

    [Fact]
    public void A_wire_can_be_flown_under_but_the_pole_cannot()
    {
        // дріт на висоті 150: під ним на 100 px вільно, але стовп від землі — не пролетиш
        var under = Bare();
        var x = Ahead(under, 30);
        under.Put(RunnerKind.Wire, x, 120 * U, 144 * U, 6 * U);
        under.P[0].Y = 100 * U;
        Hover(under, 12);
        Assert.Equal(0, under.P[0].Hits);           // ще до стовпа — під дротом
        var pole = Bare();
        pole.Put(RunnerKind.Wire, x, 120 * U, 144 * U, 6 * U);
        pole.Put(RunnerKind.Pole, x + 53 * U, 14 * U, 0, 150 * U);
        pole.P[0].Y = 100 * U;
        Hover(pole, 40);
        Assert.Equal(1, pole.P[0].Hits);
        var over = Bare();
        over.Put(RunnerKind.Wire, x, 120 * U, 144 * U, 6 * U);
        over.Put(RunnerKind.Pole, x + 53 * U, 14 * U, 0, 150 * U);
        over.P[0].Y = 160 * U;
        Hover(over, 40);
        Assert.Equal(0, over.P[0].Hits);
    }

    [Fact]
    public void A_nest_sits_on_top_of_its_pole()
    {
        var nests = Pairs(4, 6000).Where(p => p.A.Kind == RunnerKind.Nest).ToList();
        Assert.NotEmpty(nests);
        foreach (var (nest, pole, _) in nests)
        {
            Assert.Equal(RunnerKind.Pole, pole.Kind);
            Assert.Equal(nest.Base, pole.H);
            Assert.Equal(nest.X + 18 * U, pole.X);
            Assert.Equal((50 * U, 26 * U), (nest.W, nest.H));
            Assert.InRange(pole.H / U, 100, 160);
        }
    }

    [Fact]
    public void The_kite_bobs_sixteen_pixels_in_an_eighty_step_triangle()
    {
        var k = new RunnerObstacle { Kind = RunnerKind.Kite, Base = 3000, W = 576, H = 576 };
        var offs = Enumerable.Range(0, 160).Select(r => k.BaseAt(r) - 3000).ToList();
        Assert.Equal(-260, offs.Min());
        Assert.Equal(260, offs.Max());
        Assert.Equal(offs.Take(80), offs.Skip(80));
        for (var r = 1; r < 160; r++) Assert.Equal(13, Math.Abs(offs[r] - offs[r - 1]));
    }

    [Fact]
    public void Course_gaps_shrink_from_55_to_30_steps()
    {
        var sim = new RunnerSim(RunnerMode.Storks, 3, Seats(0), 0, 1000, false) { GapLog = [] };
        for (var i = 0; i < 200; i++) sim.Step();
        Assert.All(sim.GapLog!, g => Assert.InRange(g, 54, 98));
        for (var i = 0; i < 4300; i++) sim.Step();
        var from = sim.GapLog!.Count;
        for (var i = 0; i < 1500; i++) sim.Step();
        var late = sim.GapLog.Skip(from).ToList();
        Assert.True(late.Count > 20);
        Assert.All(late, g => Assert.InRange(g, 30, 50));
        Assert.Contains(late, g => g < 36);
    }

    [Fact]
    public void Course_rules_hold_no_kite_by_a_tree_no_triples_and_twin_chimneys_only_early()
    {
        foreach (var seed in new[] { 1, 2, 3, 4, 5 })
        {
            var sim = new RunnerSim(RunnerMode.Storks, seed, Seats(0), 0, 1000, false) { EventLog = [] };
            for (var i = 0; i < 7000; i++) sim.Step();
            var e = sim.EventLog!;
            for (var i = 1; i < e.Count; i++)
            {
                Assert.False(e[i - 1] == RunnerKind.Tree && e[i] == RunnerKind.Kite);
                Assert.False(e[i - 1] == RunnerKind.Kite && e[i] == RunnerKind.Tree);
                if (i >= 2) Assert.False(e[i] == e[i - 1] && e[i] == e[i - 2]);
            }
            Assert.DoesNotContain(e.Skip(e.Count / 2), k => k == RunnerKind.Chimney2);
        }
    }

    [Fact]
    public void Course_generation_depends_only_on_seed_and_step()
    {
        var a = new RunnerSim(RunnerMode.Storks, 77, Seats(3), 150, 1000, false);
        var b = new RunnerSim(RunnerMode.Storks, 77, Seats(0), 150, 1000, false);
        var rng = new RunnerRng(1);
        for (var i = 0; i < 2500; i++)
        {
            for (var s = 0; s < 3; s++) a.Input(s, a.S, rng.Next(8));
            a.Step();
            b.Step();
        }
        Assert.Equal(b.ObstacleCount, a.ObstacleCount);
        for (var k = 0; k < a.ObstacleCount; k++) Assert.Equal(b.Obstacle(k), a.Obstacle(k));
        Assert.Equal(0, a.PickupCount);          // у Лелек підбирачок нема
    }

    // ---------- партія ----------

    [Fact]
    public void Round_ends_when_one_stork_is_left_and_the_cap_shares_first_place()
    {
        var h = Table(3);
        ToRun(h);
        Down(h, 2);
        Assert.Equal("run", Ph(h));
        Down(h, 0);
        Assert.Equal("over", Ph(h));
        Assert.Equal([2, 1, 3], Ints(h.View(null), "place").Take(3));

        var cap = Table(3, options: new { rounds = "1" });
        ToRun(cap);
        Sim(cap).ClearCourse();
        for (var i = 0; i < 3010 && Ph(cap) == "run"; i++) { Keep(cap); cap.Tick(); }
        Assert.Equal("over", Ph(cap));
        Assert.Equal([1, 1, 1], Ints(cap.View(null), "place").Take(3));
        Assert.Equal([9, 9, 9], Ints(cap.View(null), "points").Take(3));
    }

    [Fact]
    public void Points_are_three_per_place_rank_and_there_are_no_eggs()
    {
        var h = Table(3);
        ToRun(h);
        Down(h, 1);
        Down(h, 0);
        Assert.Equal([6, 3, 9], Ints(h.View(null), "points").Take(3));
        Assert.False(Views.Has(h.View(null), "eggs"));
    }

    [Fact]
    public void Three_rounds_then_finish_with_the_journal_line()
    {
        var h = Table(3);
        for (var r = 0; r < 3; r++)
        {
            ToRun(h);
            Down(h, 0);                 // Оля падає першою, Ганна — другою, Петро бере раунд
            Down(h, 2);
            ThroughOver(h);
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("Лелеки: Петро 27 · Ганна 18 · Оля 9", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Equal([1], h.Finished.Single().Result.Winners);
    }

    [Fact]
    public void Storks_clean_is_awarded_for_a_minute_without_a_hit_and_only_once()
    {
        var h = Table(2, options: new { rounds = "3" });
        ToRun(h);
        Sim(h).ClearCourse();
        for (var i = 0; i < 1520; i++) { Keep(h); h.Tick(); }
        Assert.Equal(2, h.Awards.Count(a => a.Reason == "ach:storks-clean"));
        for (var i = 0; i < 400; i++) { Keep(h); h.Tick(); }
        Assert.Equal(2, h.Awards.Count(a => a.Reason == "ach:storks-clean"));
    }

    [Fact]
    public void Storks_clean_counts_the_minute_from_the_last_forgiven_hit()
    {
        var h = Table(2);
        ToRun(h);
        var sim = Sim(h);
        sim.ClearCourse();
        for (var i = 0; i < 250; i++) { Keep(h); h.Tick(); }      // 500 кроків чистого польоту
        Keep(h);
        sim.P[0].Y = 0;                // Оля торкнулась землі — пір'я пропало, але вона летить далі
        sim.P[0].Vy = -8;
        h.Tick();
        Assert.Equal(1, sim.P[0].Hits);
        Assert.False(sim.P[0].Down);
        for (var i = 0; i < 1300; i++) { Keep(h); h.Tick(); }     // ≈ 3100 кроків від старту, ≈ 2600 від зачепу
        Assert.Contains(h.Awards, a => a.Reason == "ach:storks-clean" && a.Nick == "Петро");
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:storks-clean" && a.Nick == "Оля");
        for (var i = 0; i < 300; i++) { Keep(h); h.Tick(); }      // хвилина від зачепу минула — небо чисте й для Олі
        Assert.Single(h.Awards, a => a.Reason == "ach:storks-clean" && a.Nick == "Оля");
        Assert.Single(h.Awards, a => a.Reason == "ach:storks-clean" && a.Nick == "Петро");
    }

    [Fact]
    public void Storks_clean_is_not_lost_when_the_round_ends_inside_the_rewind_window()
    {
        var h = Table(2);
        ToRun(h);
        var sim = Sim(h);
        sim.ClearCourse();
        while (sim.S - 1 - RunnerParty.ReadySteps < 3002) { Keep(h); h.Tick(); }
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:storks-clean");   // ще чекаємо на вікно перемотування
        Down(h, 1);                                                               // Петро падає — раунд Олі
        Assert.Equal("over", Ph(h));
        Assert.Single(h.Awards, a => a.Reason == "ach:storks-clean" && a.Nick == "Оля");
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:storks-clean" && a.Nick == "Петро");
    }

    [Fact]
    public void Everyone_down_on_the_same_step_is_a_draw_not_a_win_for_all()
    {
        var h = Table(2, options: new { rounds = "1" });
        ToRun(h);
        Down(h, 0, 1);
        ThroughOver(h);
        var fin = h.Finished.Single().Result;
        Assert.True(fin.Draw);
        Assert.Empty(fin.Winners);
        Assert.Equal("Лелеки: нічия — Оля 6 · Петро 6", h.Outbox.OfType<Journal>().Last().Text);
        Assert.True(h.View(null).GetProperty("result").GetProperty("draw").GetBoolean());
    }

    [Fact]
    public void A_single_player_plays_one_training_round_quietly()
    {
        var h = Table(1, options: new { rounds = "5" });
        Assert.Equal(1, h.View(null).GetProperty("rounds").GetInt32());
        ToRun(h);
        h.Tick(100);                      // без змахів — земля, пір'я, знову земля
        Assert.Equal("over", Ph(h));
        ThroughOver(h);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Finished.Single().Result.Winners);
        Assert.DoesNotContain(h.Outbox.OfType<Journal>(), j => j.Text.StartsWith("Лелеки:"));
    }

    [Fact]
    public void Leaving_keeps_the_party_with_three_and_ends_it_with_two()
    {
        var h = Table(3);
        ToRun(h);
        h.Leave("Ганна");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Contains(h.Outbox.OfType<Journal>(), j => j.Text == "Лелеки: Ганна встав з-за столу");
        h.Leave("Оля");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Finished.Single().Result.Winners);
        Assert.Equal("Лелеки: Оля встав з-за столу, Петро лишився сам", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void Rematch_gives_a_new_seed_and_zero_points()
    {
        var h = Table(2, options: new { rounds = "1" });
        var seed = h.View(null).GetProperty("seed").GetInt32();
        ToRun(h);
        Down(h, 1);
        ThroughOver(h);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(h.Rematch().Ok);
        var v = h.View(null);
        Assert.NotEqual(seed, v.GetProperty("seed").GetInt32());
        Assert.All(Ints(v, "points"), p => Assert.Equal(0, p));
        Assert.Equal("ready", v.GetProperty("ph").GetString());
        Assert.Equal(1, v.GetProperty("p")[0][3].GetInt32());   // пір'я знову є
    }

    [Fact]
    public void Input_payload_is_exactly_what_the_module_sends_and_throw_is_refused()
    {
        var h = Table(2);
        ToRun(h);
        var sim = Sim(h);
        sim.ClearCourse();
        Keep(h);
        foreach (var junk in new object?[] { null, 5, new { k = 5 }, new { s = sim.S, k = 8 }, new { s = true, k = 5 } })
        {
            sim.P[0].Vy = 0;
            h.Input(0, "in", junk);
            h.Tick();
            Assert.True(sim.P[0].Vy <= 0);
            Keep(h);
        }
        h.Input(0, "in", new { s = sim.S, k = 5 });
        h.Tick();
        Assert.True(sim.P[0].Vy > 0);
        Assert.Equal("Тут так не ходять", h.Act(0, "throw").Message);
        Assert.Equal("Тут так не ходять", h.Act(0, "fly").Message);
    }

    [Fact]
    public void A_flap_input_fifteen_steps_late_is_rewound_and_matches_the_straight_run()
    {
        // курс прибрано, лишено один комин: у перемотаному прогоні лелека 15 кроків летить «без змаху», і
        // справжні перешкоди встигли б її збити остаточно — а тут перевіряємо саме перемотування
        var straight = new RunnerSim(RunnerMode.Storks, 31, Seats(1), 150, 1000, false);
        var late = new RunnerSim(RunnerMode.Storks, 31, Seats(1), 150, 1000, false);
        foreach (var sim in new[] { straight, late })
        {
            sim.ClearCourse();
            sim.Put(RunnerKind.Chimney, sim.PaceX(300) + 200 * U, 40 * U, 0, 60 * U);
        }
        const int n = 700;
        var keys = new int[n];
        for (var t = 0; t < n; t++) keys[t] = t % 30 == 0 ? 5 : t % 30 == 1 ? 0 : -1;
        for (var t = 0; t < n; t++)
        {
            if (keys[t] >= 0) straight.Input(0, t, keys[t]);
            straight.Step();
        }
        for (var t = 0; t < n + 15; t++)
        {
            if (t >= 15 && keys[t - 15] >= 0) late.Input(0, t - 15, keys[t - 15]);
            if (t < n) late.Step();
        }
        Assert.Equal(straight.Wire(0), late.Wire(0));
        Assert.False(straight.P[0].Out);
    }

    [Fact]
    public void Views_of_every_seat_and_of_the_watcher_are_identical()
    {
        var h = Table(4);
        ToRun(h);
        h.Tick(30);
        var w = h.View(null).GetRawText();
        for (var s = 0; s < 4; s++) Assert.Equal(w, h.View(s).GetRawText());
    }

    [Fact]
    public void Frame_of_eight_storks_stays_under_one_kilobyte()
    {
        var h = Table(8);
        ToRun(h);
        int max = 0, sum = 0;
        for (var i = 0; i < 300; i++)
        {
            Keep(h);
            h.Tick();
            var n = System.Text.Encoding.UTF8.GetByteCount(Views.Text(h.Outbox.OfType<RoomFrame>().Last().Frame));
            max = Math.Max(max, n);
            sum += n;
        }
        Console.WriteLine($"[size] storks 8 гравців: кадр у середньому {sum / 300} Б, найбільший {max} Б");
        Assert.True(max < 1024, $"кадр {max} Б");
        var f = Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);
        Assert.Equal(6, f.GetProperty("p")[0].GetArrayLength());
        Assert.False(Views.Has(f, "d"));
    }

    [Fact]
    public void The_same_seed_and_inputs_give_the_same_frames()
    {
        static List<string> Play(int seed)
        {
            var h = Table(3, seed);
            var list = new List<string>();
            for (var i = 0; i < 500 && h.Room.Status == RoomStatus.Playing; i++)
            {
                var sim = Sim(h);
                if (i % 7 == 0) h.Input(i % 3, "in", new { s = sim.S, k = 5 });
                if (i % 7 == 1) h.Input((i - 1) % 3, "in", new { s = sim.S, k = 0 });
                var before = h.Outbox.Count;
                h.Tick();
                list.AddRange(h.Outbox.Skip(before).OfType<RoomFrame>().Select(f => Views.Text(f.Frame)));
            }
            return list;
        }
        Assert.Equal(Play(8), Play(8));
    }

    [Fact]
    public void Simulation_hash_matches_the_javascript_reference_for_the_storks_fixture()
    {
        var (name, make, js) = RunnerSimTests.Fixtures.Single(f => f.Name.StartsWith("storks"));
        var sim = make();
        Assert.True(js == sim.Hash, $"{name}: C# {sim.Hash}, JS {js}");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Three_thousand_ticks_of_eight_storks_take_under_a_second()
    {
        var h = Table(8, seed: 9, options: new { rounds = "5" });
        ToRun(h);
        var room = h.Room;
        for (var i = 0; i < 50; i++) h.Rooms.Tick(room);
        var sw = new Stopwatch();
        var measured = 0;
        for (var guard = 0; measured < 3000 && guard < 20000; guard++)
        {
            var game = (Storks)h.Room.Game;
            var sim = game.World!;
            if (game.PhaseName != "run") { h.Rooms.Tick(room); continue; }
            for (var s = 0; s < 8; s++)
            {
                var p = sim.P[s];
                if (p.Y < 60 * U || p.Hits > 0) { p.Hits = 0; p.Feather = true; }
                if ((guard + s) % 8 == 0 && p.Y < 180 * U)
                    h.Rooms.Input(room.Id, room.Seats[s]!, "in", Views.Payload(new { s = sim.S, k = 5 }));
            }
            sw.Start();
            h.Rooms.Tick(room);
            sw.Stop();
            measured++;
        }
        Assert.Equal(3000, measured);
        var ms = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"[perf] storks 8 гравців: 3000 тиків за {ms:F1} мс, {ms / 3000:F4} мс на тик");
        Assert.True(ms < 1000, $"3000 тиків за {ms} мс");
    }
}
