using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Спільний рушій пакета «runner» на голому <see cref="RunnerSim"/>: фізика стрибка, курс, перемотування,
/// парність із JS. Перешкоди тут ставляться руками на порожню кризу — так видно рівно те правило, що
/// перевіряємо, а не випадковий курс.
/// </summary>
[Collection(SerialPerf.Name)]
public class RunnerSimTests
{
    const int U = RunnerSim.Sub;

    // ---------- підмостки ----------

    static bool[] Seats(int n)
    {
        var a = new bool[RunnerSim.Seats];
        for (var i = 0; i < n; i++) a[i] = true;
        return a;
    }

    /// <summary>Порожня криза без відліку: крок = крок бігу, перешкоди ставить тест.</summary>
    static RunnerSim Bare(int players = 1, int seed = 7, RunnerMode mode = RunnerMode.Dino)
    {
        var sim = new RunnerSim(mode, seed, Seats(players), 0, 1000, snowOn: true);
        sim.ClearCourse();
        return sim;
    }

    /// <summary>Розігнати світ без гравців до кроку бігу run і прибрати курс.</summary>
    static RunnerSim Fast(int run, int players = 1)
    {
        var sim = new RunnerSim(RunnerMode.Dino, 7, Seats(players), 0, 1000, snowOn: true);
        for (var i = 0; i < players; i++) sim.P[i].Plays = false;
        while (sim.S < run) sim.Step();
        sim.ClearCourse();
        for (var i = 0; i < players; i++) sim.P[i].Plays = true;
        return sim;
    }

    static void Press(RunnerSim sim, int k, int seat = 0) => Assert.True(sim.Input(seat, sim.S, k));

    /// <summary>Лівий край динозавра після наступного кроку, якщо він не відстає.</summary>
    static int Ahead(RunnerSim sim, int px) => sim.PaceX(sim.S) + px * U;

    /// <summary>Стрибок: натиск і утримання hold кроків (1 — тап), далі до приземлення. Вершина й час у повітрі.</summary>
    static (int Peak, int Air) Jump(RunnerSim sim, int hold, int seat = 0)
    {
        Press(sim, 5, seat);
        var peak = 0;
        var steps = 0;
        do
        {
            if (steps == hold) Press(sim, 0, seat);
            sim.Step();
            steps++;
            peak = Math.Max(peak, sim.P[seat].Y);
        } while (sim.P[seat].Air && steps < 200);
        return (peak, steps);
    }

    static void Run(RunnerSim sim, int steps)
    {
        for (var i = 0; i < steps; i++) sim.Step();
    }

    /// <summary>Пройти задану кількість кроків, натискаючи стрибок (тап або утримання) на кроці press.</summary>
    static int Hits(RunnerSim sim, int total, int press = -1, int hold = 1, int seat = 0)
    {
        for (var i = 0; i < total; i++)
        {
            if (i == press) Press(sim, 5, seat);
            if (i == press + hold) Press(sim, 0, seat);
            sim.Step();
        }
        return sim.P[seat].Hits;
    }

    // ---------- генератор і темп ----------

    [Fact]
    public void Xorshift_matches_the_reference_sequence()
    {
        // ті самі числа видає RunnerRng у runner.js (і звичайний xorshift32 у Python)
        var rng = new RunnerRng(12345);
        uint[] want = [3336926330, 1697253807, 2816511904, 1955480042, 718842323, 3283620450, 4285686168, 3680911160];
        foreach (var w in want) Assert.Equal(w, rng.NextU32());
        var zero = new RunnerRng(0);
        Assert.NotEqual(0u, zero.NextU32());
    }

    [Theory]
    [InlineData(RunnerMode.Dino)]
    [InlineData(RunnerMode.Storks)]
    public void Pace_closed_form_equals_the_running_sum_for_every_step_up_to_ten_thousand(RunnerMode mode)
    {
        var rules = RunnerRules.For(mode);
        var sum = 0;
        for (var run = 0; run <= 10000; run++)
        {
            Assert.Equal(sum, rules.PaceX(run));
            sum += rules.Speed(run);
        }
    }

    [Fact]
    public void Speed_reaches_its_maximum_at_step_3584_and_stays_there()
    {
        var d = RunnerRules.Dino;
        Assert.Equal(112, d.Speed(0));
        Assert.Equal(162, d.Speed(1600));
        Assert.Equal(223, d.Speed(3583));
        Assert.Equal(224, d.Speed(3584));
        Assert.Equal(224, d.Speed(100000));
        Assert.Equal(600320, d.PaceX(3584));
        Assert.Equal(1141504, d.PaceX(6000));
        Assert.Equal(192, RunnerRules.Storks.Speed(3840));
        Assert.Equal(191, RunnerRules.Storks.Speed(3839));
    }

    [Fact]
    public void The_avalanche_closes_at_two_and_a_half_pixels_per_second_and_stops_at_200()
    {
        Assert.Equal(8960, RunnerRules.AvD(0));
        Assert.Equal(5360, RunnerRules.AvD(4500));
        Assert.Equal(3200, RunnerRules.AvD(7200));
        Assert.Equal(3200, RunnerRules.AvD(20000));
        // 2,5 px/с = 40 суб за 50 кроків
        Assert.Equal(40, RunnerRules.AvD(0) - RunnerRules.AvD(50));
    }

    [Fact]
    public void RunAt_inverts_the_pace_line()
    {
        var d = RunnerRules.Dino;
        foreach (var x in new[] { 1, 112, 5000, 600320, 600321, 1141504, 5_000_000 })
        {
            var run = d.RunAt(x);
            Assert.True(d.PaceX(run) >= x);
            Assert.True(run == 0 || d.PaceX(run - 1) < x);
        }
    }

    // ---------- стрибок ----------

    [Fact]
    public void A_tap_jump_peaks_at_57_pixels_and_lands_in_23_steps()
    {
        var (peak, air) = Jump(Bare(), hold: 1);
        Assert.Equal(924, peak);            // 57,75 px
        Assert.Equal(23, air);              // 0,46 с
    }

    [Fact]
    public void A_held_jump_peaks_at_111_pixels_and_lands_in_35_steps()
    {
        // spec обіцяв 116 px і 36 кроків; закриті формули з тими самими сталими дають 111 і 35 — див. «Як реалізовано»
        var (peak, air) = Jump(Bare(), hold: 30);
        Assert.Equal(1780, peak);
        Assert.Equal(35, air);
    }

    [Fact]
    public void Releasing_early_gives_a_height_between_tap_and_full_hold()
    {
        var (tap, _) = Jump(Bare(), 1);
        var (mid, _) = Jump(Bare(), 6);
        var (full, _) = Jump(Bare(), 30);
        Assert.InRange(mid, tap + 1, full - 1);
    }

    [Fact]
    public void Fast_fall_lands_at_least_six_steps_sooner_than_a_plain_fall()
    {
        var (_, plain) = Jump(Bare(), 30);
        var sim = Bare();
        Press(sim, 5);
        var steps = 0;
        do
        {
            if (steps == 30) Press(sim, 0);
            if (steps == 16) Press(sim, 2);      // на вершині — ↓
            sim.Step();
            steps++;
        } while (sim.P[0].Air && steps < 200);
        Assert.True(plain - steps >= 6, $"звичайне {plain}, швидке {steps}");
        Assert.True(sim.P[0].Duck);              // приземлився вже пригнувшись
    }

    [Fact]
    public void Jump_pressed_three_steps_before_landing_fires_right_after_landing()
    {
        var (_, land) = Jump(Bare(), 1);          // тап приземляється на 23-му кроці
        // натиск на land−2: буфер живе land−2, land−1, land — і стрибок на першому кроці на землі
        var sim = Bare();
        Press(sim, 5);
        sim.Step();
        Press(sim, 0);
        for (var i = 2; i <= land; i++)
        {
            if (i == land - 2) Press(sim, 5);
            if (i == land - 1) Press(sim, 0);
            sim.Step();
        }
        Assert.False(sim.P[0].Air);
        sim.Step();
        Assert.True(sim.P[0].Air);
        Assert.True(sim.P[0].Vy > 0);

        // а на крок раніше — уже запізно: буфер вигорів у повітрі
        var late = Bare();
        Press(late, 5);
        late.Step();
        Press(late, 0);
        for (var i = 2; i <= land; i++)
        {
            if (i == land - 3) Press(late, 5);
            if (i == land - 2) Press(late, 0);
            late.Step();
        }
        late.Step();
        Assert.False(late.P[0].Air);
    }

    [Fact]
    public void Jump_pressed_two_steps_after_walking_off_a_pit_edge_still_works()
    {
        var sim = Bare();
        sim.Put(RunnerKind.Pit, Ahead(sim, 20), 100 * U, 0, 0);
        var guard = 0;
        while (!sim.P[0].Air && guard++ < 50) sim.Step();
        Assert.True(sim.P[0].Air);
        Assert.Equal(0, sim.P[0].Vy);            // зійшов, а не стрибнув
        sim.Step();                               // перший крок койота
        Press(sim, 5);
        sim.Step();                               // другий — ще можна
        Assert.True(sim.P[0].Vy > 0);
        Assert.Equal(0, sim.P[0].Hits);

        var late = Bare();
        late.Put(RunnerKind.Pit, Ahead(late, 20), 100 * U, 0, 0);
        guard = 0;
        while (!late.P[0].Air && guard++ < 50) late.Step();
        late.Step();
        late.Step();
        Press(late, 5);                           // третій — уже падаємо
        late.Step();
        Assert.True(late.P[0].Vy < 0);
    }

    // ---------- перешкоди ----------

    [Fact]
    public void Ducking_shrinks_the_hitbox_and_clears_an_icicle()
    {
        var sim = Bare();
        sim.Put(RunnerKind.Icicle, Ahead(sim, 40), 30 * U, 30 * U, 270 * U);
        Press(sim, 2);
        Run(sim, 30);
        Assert.Equal(0, sim.P[0].Hits);
        Assert.Equal(2, sim.P[0].ModeOf(RunnerMode.Dino));
    }

    [Fact]
    public void Standing_under_an_icicle_is_a_stumble()
    {
        var sim = Bare();
        sim.Put(RunnerKind.Icicle, Ahead(sim, 40), 30 * U, 30 * U, 270 * U);
        Run(sim, 30);
        Assert.Equal(1, sim.P[0].Hits);
    }

    [Fact]
    public void A_low_block_needs_a_tap_and_a_high_block_needs_a_hold()
    {
        foreach (var (h, hold, hits) in new[] { (30, 1, 0), (30, 30, 0), (64, 1, 1), (64, 30, 0) })
        {
            var sim = Bare();
            sim.Put(RunnerKind.Low, Ahead(sim, 80), 34 * U, 0, h * U);
            Assert.Equal(hits, Hits(sim, 60, press: 0, hold: hold));
        }
        var still = Bare();
        still.Put(RunnerKind.Low, Ahead(still, 80), 34 * U, 0, 30 * U);
        Assert.Equal(1, Hits(still, 60));
    }

    [Fact]
    public void A_wide_block_at_start_speed_needs_a_hold_but_at_max_speed_a_tap_suffices()
    {
        var tap = Bare();
        tap.Put(RunnerKind.Wide, Ahead(tap, 60), 100 * U, 0, 30 * U);
        Assert.Equal(1, Hits(tap, 60, press: 0, hold: 1));

        var hold = Bare();
        hold.Put(RunnerKind.Wide, Ahead(hold, 60), 100 * U, 0, 30 * U);
        Assert.Equal(0, Hits(hold, 60, press: 0, hold: 30));

        var fast = Fast(3600);
        Assert.Equal(224, fast.Speed(fast.S));
        fast.Put(RunnerKind.Wide, Ahead(fast, 90), 100 * U, 0, 30 * U);
        Assert.Equal(0, Hits(fast, 60, press: 0, hold: 1));
    }

    [Fact]
    public void A_stumble_slows_to_forty_percent_for_thirty_steps_and_then_recovers_at_108_percent()
    {
        var sim = Bare();
        sim.Put(RunnerKind.Low, Ahead(sim, 40), 34 * U, 0, 30 * U);
        while (sim.P[0].Hits == 0) sim.Step();
        Assert.Equal(RunnerDino.StunSteps, sim.P[0].Stun);
        var lag0 = sim.P[0].Lag;
        var sp = sim.Speed(sim.S);
        sim.Step();
        Assert.Equal(sp - sp * 40 / 100, sim.P[0].Lag - lag0);   // 40 % темпу
        Run(sim, RunnerDino.StunSteps - 1);
        Assert.Equal(0, sim.P[0].Stun);
        var lost = sim.P[0].Lag;
        Assert.InRange(lost, 1900, 2150);                          // ≈ 126 px на старті
        var steps = 0;
        while (sim.P[0].Lag > 0 && steps < 1000) { sim.Step(); steps++; }
        Assert.InRange(steps, 200, 300);                           // відіграв за ≈ 5 с
        Assert.Equal(1, sim.P[0].Hits);
    }

    [Fact]
    public void The_same_obstacle_never_hits_twice()
    {
        var sim = Bare();
        var id = sim.Put(RunnerKind.Wide, Ahead(sim, 30), 100 * U, 0, 30 * U);
        Run(sim, 120);
        Assert.Equal(1, sim.P[0].Hits);
        Assert.Equal(id, sim.P[0].Lp);
        Assert.True(sim.P[0].HasPassed(id));
    }

    [Fact]
    public void Falling_into_a_pit_costs_a_second_of_stun_and_pops_out_at_the_far_edge()
    {
        var sim = Bare();
        var x = Ahead(sim, 20);
        var id = sim.Put(RunnerKind.Pit, x, 60 * U, 0, 0);
        var guard = 0;
        while (sim.P[0].Stun == 0 && guard++ < 100) sim.Step();
        var p = sim.P[0];
        Assert.Equal(RunnerDino.PitStun, p.Stun);
        Assert.Equal(0, p.Y);
        Assert.False(p.Air);
        Assert.Equal(id, p.Lp);
        Assert.Equal(sim.PaceX(sim.S) - (x + 60 * U + 16 * U), p.Lag);
        Run(sim, 60);
        Assert.Equal(1, p.Hits);                 // вилізла й біжить далі
    }

    [Fact]
    public void A_hill_lifts_the_feet_linearly_and_a_block_on_top_sits_at_forty_pixels()
    {
        var sim = Bare();
        var x = Ahead(sim, 100);
        sim.Put(RunnerKind.Hill, x, 420 * U, 0, 40 * U);
        Assert.Equal(0, sim.GroundAt(x - 1));
        Assert.Equal(20 * U, sim.GroundAt(x + 80 * U));
        Assert.Equal(40 * U, sim.GroundAt(x + 200 * U));
        Assert.Equal(20 * U, sim.GroundAt(x + 340 * U));
        Assert.Equal(0, sim.GroundAt(x + 420 * U));
        var peak = 0;
        for (var i = 0; i < 80; i++) { sim.Step(); peak = Math.Max(peak, sim.P[0].Y); Assert.False(sim.P[0].Air); }
        Assert.Equal(40 * U, peak);              // ноги йшли за схилом, без стрибків

        // генератор ставить брилу на верхівку з прогресу 300 ‰
        var gen = new RunnerSim(RunnerMode.Dino, 11, Seats(0), 0, 1000, true);
        var found = false;
        for (var i = 0; i < 6000 && !found; i++)
        {
            gen.Step();
            for (var k = 0; k + 1 < gen.ObstacleCount; k++)
            {
                ref readonly var a = ref gen.Obstacle(k);
                ref readonly var b = ref gen.Obstacle(k + 1);
                if (a.Kind != RunnerKind.Hill || b.Kind != RunnerKind.Low || b.X != a.X + 193 * U) continue;
                Assert.Equal(40 * U, b.Base);
                found = true;
            }
        }
        Assert.True(found);
    }

    [Fact]
    public void The_pterodactyl_flies_toward_the_runner_at_64_sub_per_step()
    {
        var o = new RunnerObstacle { Kind = RunnerKind.Ptero, X = 100000, W = 640, Base = 480, H = 320, Since = 50 };
        Assert.Equal(100000, o.XAt(10));
        Assert.Equal(100000, o.XAt(50));
        Assert.Equal(100000 - 64 * 30, o.XAt(80));

        // згенерований птеродактиль рушає, коли лінія темпу за 480 px від нього
        var gen = new RunnerSim(RunnerMode.Dino, 5, Seats(0), 0, 1000, true);
        var seen = 0;
        for (var i = 0; i < 6000; i++)
        {
            gen.Step();
            for (var k = 0; k < gen.ObstacleCount; k++)
            {
                ref readonly var p = ref gen.Obstacle(k);
                if (p.Kind != RunnerKind.Ptero || p.Since != gen.S - 1) continue;
                seen++;
                Assert.True(gen.PaceX(p.Since) >= p.X - RunnerDino.PteroWake);
                Assert.True(gen.PaceX(p.Since - 1) < p.X - RunnerDino.PteroWake);
            }
        }
        Assert.True(seen > 0);
    }

    [Fact]
    public void A_standing_runner_stumbles_on_a_pterodactyl_and_a_ducking_one_does_not()
    {
        foreach (var (duck, hits) in new[] { (false, 1), (true, 0) })
        {
            var sim = Bare();
            sim.Put(RunnerKind.Ptero, Ahead(sim, 120), 40 * U, 30 * U, 20 * U, since: 0);
            if (duck) Press(sim, 2);
            Run(sim, 40);
            Assert.Equal(hits, sim.P[0].Hits);
        }
    }

    // ---------- підбирачки ----------

    [Fact]
    public void Pepper_boosts_to_125_percent_for_100_steps_and_lag_never_goes_below_minus_1280()
    {
        var sim = Bare();
        sim.PutPickup(RunnerKind.Pepper, Ahead(sim, 30), 0);
        var min = 0;
        var took = -1;
        for (var i = 0; i < 160; i++)
        {
            sim.Step();
            if (took < 0 && sim.P[0].Boost > 0) took = i;
            min = Math.Min(min, sim.P[0].Lag);
        }
        Assert.True(took >= 0);
        Assert.Equal(RunnerDino.LagMin, min);
        Assert.Equal(0, sim.P[0].Boost);
        Assert.Equal(RunnerDino.LagMin, sim.P[0].Lag);   // відірвався — і тримає відрив, поки не спіткнеться
    }

    [Fact]
    public void Every_player_can_take_the_same_egg()
    {
        var sim = Bare(players: 3);
        var id = sim.PutPickup(RunnerKind.Egg, Ahead(sim, 40), 0);
        Run(sim, 20);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(1, sim.P[i].Eggs);
            Assert.Equal(id, sim.P[i].Lt);
        }
    }

    [Fact]
    public void An_air_egg_needs_a_held_jump()
    {
        foreach (var (hold, eggs) in new[] { (1, 0), (30, 1) })
        {
            var sim = Bare();
            sim.PutPickup(RunnerKind.Egg, Ahead(sim, 100), RunnerDino.AirEgg);
            Hits(sim, 60, press: 0, hold: hold);
            Assert.Equal(eggs, sim.P[0].Eggs);
        }
    }

    [Fact]
    public void A_second_snowball_just_vanishes()
    {
        var sim = Bare();
        sim.PutPickup(RunnerKind.SnowBall, Ahead(sim, 20), 0);
        sim.PutPickup(RunnerKind.SnowBall, Ahead(sim, 80), 0);
        Run(sim, 30);
        Assert.Equal(1, sim.P[0].Snow);
    }

    // ---------- курс ----------

    [Fact]
    public void Course_gaps_shrink_from_60_to_32_steps_as_progress_reaches_1000()
    {
        var sim = new RunnerSim(RunnerMode.Dino, 3, Seats(0), 0, 1000, true) { GapLog = [] };
        Run(sim, 200);
        Assert.True(sim.GapLog!.Count > 0);
        Assert.All(sim.GapLog, g => Assert.InRange(g, 58, 110));
        Run(sim, 4500 - 200);
        var from = sim.GapLog.Count;
        Run(sim, 1500);
        var late = sim.GapLog.Skip(from).ToList();
        Assert.True(late.Count > 20);
        Assert.All(late, g => Assert.InRange(g, 32, 50));
        Assert.Contains(late, g => g < 40);
    }

    [Fact]
    public void The_daily_cap_of_1250_squeezes_gaps_to_25_steps()
    {
        var sim = new RunnerSim(RunnerMode.Dino, 3, Seats(0), 0, 1250, false) { GapLog = [] };
        Run(sim, 5700);
        var from = sim.GapLog!.Count;
        Run(sim, 2000);
        var late = sim.GapLog.Skip(from).ToList();
        Assert.All(late, g => Assert.InRange(g, 25, 45));
        Assert.Contains(late, g => g <= 27);
    }

    [Fact]
    public void Course_generation_depends_only_on_seed_and_step_not_on_players()
    {
        var a = new RunnerSim(RunnerMode.Dino, 99, Seats(4), 150, 1000, true);
        var b = new RunnerSim(RunnerMode.Dino, 99, Seats(0), 150, 1000, true);
        var rng = new RunnerRng(5);
        for (var i = 0; i < 3000; i++)
        {
            for (var s = 0; s < 4; s++) a.Input(s, a.S, rng.Next(8));
            a.Step();
            b.Step();
        }
        Assert.Equal(b.ObstacleCount, a.ObstacleCount);
        for (var k = 0; k < a.ObstacleCount; k++)
        {
            ref readonly var x = ref a.Obstacle(k);
            ref readonly var y = ref b.Obstacle(k);
            Assert.Equal((y.Id, y.Kind, y.X, y.Base, y.H, y.Since), (x.Id, x.Kind, x.X, x.Base, x.H, x.Since));
        }
        Assert.Equal(b.PickupCount, a.PickupCount);
    }

    [Fact]
    public void The_same_event_never_comes_three_times_in_a_row_and_no_hill_or_pit_right_after_a_hill()
    {
        foreach (var seed in new[] { 1, 2, 3, 4, 5, 6, 7, 8 })
        {
            var sim = new RunnerSim(RunnerMode.Dino, seed, Seats(0), 0, 1250, true) { EventLog = [] };
            Run(sim, 8000);
            var e = sim.EventLog!;
            Assert.True(e.Count > 100, $"подій {e.Count}");
            for (var i = 2; i < e.Count; i++) Assert.False(e[i] == e[i - 1] && e[i] == e[i - 2], $"зерно {seed}, подія {i}");
            for (var i = 1; i < e.Count; i++)
                if (e[i - 1] == RunnerKind.Hill) Assert.True(e[i] != RunnerKind.Hill && e[i] != RunnerKind.Pit);
        }
    }

    [Fact]
    public void Early_progress_has_no_icicles_or_pterodactyls_and_late_progress_has_combos()
    {
        var sim = new RunnerSim(RunnerMode.Dino, 21, Seats(0), 0, 1000, true) { EventLog = [] };
        Run(sim, 600);    // pm < 150 — лише брили й схили
        Assert.DoesNotContain(sim.EventLog!, k => k is RunnerKind.Icicle or RunnerKind.Ptero or RunnerKind.Pit or RunnerKind.Combo);
        Run(sim, 5400);
        Assert.Contains(sim.EventLog!, k => k == RunnerKind.Combo);
        Assert.Contains(sim.EventLog!, k => k == RunnerKind.Ptero);
    }

    // ---------- сніжки ----------

    [Fact]
    public void A_thrown_snow_block_shifts_forward_off_other_obstacles()
    {
        var sim = Bare();
        var x = Ahead(sim, 300);
        var free = sim.PlaceSnow(x, 0, 0);
        Assert.Equal(x, sim.SnowBlock(0).X);
        Assert.Equal(RunnerSim.SnowIdBase, free);

        var busy = Bare();
        busy.Put(RunnerKind.Low, x + 50 * U, 34 * U, 0, 30 * U);
        busy.PlaceSnow(x, 0, 0);
        // [x, x+26] мусить відійти від брили [x+50, x+84] на 100 px — п'ять зсувів по 40
        Assert.Equal(x + 200 * U, busy.SnowBlock(0).X);

        var crowded = Bare();
        for (var i = 0; i < 20; i++) crowded.Put(RunnerKind.Low, x + i * 60 * U, 34 * U, 0, 30 * U);
        crowded.PlaceSnow(x, 0, 0);
        Assert.Equal(x + RunnerDino.SnowTries * RunnerDino.SnowShift, crowded.SnowBlock(0).X);   // на десятому — лягає як є
    }

    [Fact]
    public void A_snow_block_does_not_hit_anyone_before_its_since_step()
    {
        var early = Bare();
        early.PlaceSnow(Ahead(early, 40), 1, 100);
        Run(early, 40);
        Assert.Equal(0, early.P[0].Hits);

        var now = Bare();
        var id = now.PlaceSnow(Ahead(now, 40), 1, 0);
        Run(now, 40);
        Assert.Equal(1, now.P[0].Hits);
        Assert.Equal(id, now.P[0].Lp);
    }

    // ---------- перемотування ----------

    static int[] Keys(int n, int seed)
    {
        var rng = new RunnerRng(seed);
        var held = 0;
        var k = new int[n];
        for (var i = 0; i < n; i++)
        {
            var u = rng.Next(100);
            var edge = false;
            if (u < 6) { held ^= 1; edge = (held & 1) != 0; }
            else if (u < 9) held ^= 2;
            k[i] = held | (edge ? 4 : 0);
        }
        return k;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(15)]
    public void Rewinding_one_player_reproduces_the_straight_simulation(int late)
    {
        const int n = 1500;
        var keys = Keys(n, 77);
        var straight = new RunnerSim(RunnerMode.Dino, 4242, Seats(1), 150, 1000, true);
        var rewound = new RunnerSim(RunnerMode.Dino, 4242, Seats(1), 150, 1000, true);
        for (var t = 0; t < n; t++)
        {
            straight.Input(0, t, keys[t]);
            straight.Step();
        }
        for (var t = 0; t < n + late; t++)
        {
            if (t >= late) Assert.True(rewound.Input(0, t - late, keys[t - late]) || rewound.P[0].Out);
            if (t < n) rewound.Step();
        }
        Assert.Equal(straight.Wire(0), rewound.Wire(0));
        Assert.True(straight.P[0].Hits > 0);   // прогін не порожній
    }

    [Fact]
    public void Inputs_older_than_fifteen_steps_are_dropped_and_future_ones_clamped_to_plus_eight()
    {
        var sim = Bare();
        Run(sim, 40);
        Assert.False(sim.Input(0, sim.S - 16, 5));
        Assert.True(sim.Input(0, sim.S - 15, 0));
        Assert.True(sim.Input(0, sim.S + 50, 5));   // ляже на S+8: клієнт іде попереду на ~rtt/20 + 3 кроки
        for (var i = 0; i < 8; i++)
        {
            sim.Step();
            Assert.False(sim.P[0].Air);
        }
        sim.Step();
        Assert.True(sim.P[0].Air);
        Assert.False(sim.Input(0, 5, 8));            // біти поза 0..7
        Assert.False(sim.Input(0, -1, 0));
    }

    [Fact]
    public void Rewinding_one_player_leaves_the_others_untouched()
    {
        var keys = Keys(900, 3);
        var a = new RunnerSim(RunnerMode.Dino, 17, Seats(2), 150, 1000, true);
        var b = new RunnerSim(RunnerMode.Dino, 17, Seats(2), 150, 1000, true);
        for (var t = 0; t < 900; t++)
        {
            a.Input(1, t, keys[t]);
            b.Input(1, t, keys[t]);
            a.Input(0, t, keys[(t * 7) % 900]);
            if (t >= 10) b.Input(0, t - 10, keys[((t - 10) * 7) % 900]);
            a.Step();
            b.Step();
        }
        Assert.Equal(a.Wire(1), b.Wire(1));
    }

    [Fact]
    public void A_late_jump_saves_the_runner_from_a_block_he_had_already_hit_on_the_server()
    {
        var sim = Bare();
        sim.Put(RunnerKind.Low, Ahead(sim, 80), 34 * U, 0, 30 * U);
        var start = sim.S;
        Run(sim, 14);
        Assert.Equal(1, sim.P[0].Hits);                   // на сервері спіткнувся: стрибка ще не було
        Assert.True(sim.Input(0, start, 5));              // стрибок з міткою кроку, 14 кроків тому
        Assert.True(sim.Input(0, start + 1, 0));
        Assert.Equal(0, sim.P[0].Hits);
        Assert.True(sim.P[0].Air);
        Run(sim, 40);
        Assert.Equal(0, sim.P[0].Hits);
    }

    // ---------- годинник кроків ----------

    [Fact]
    public void The_pacer_makes_two_steps_per_forty_milliseconds()
    {
        var pacer = new RunnerPacer();
        var t = DateTimeOffset.Parse("2026-09-27T10:00:00Z");
        Assert.Equal(2, pacer.Due(t));   // перший тик — два кроки
        for (var i = 0; i < 100; i++)
        {
            t = t.AddMilliseconds(40);
            Assert.Equal(2, pacer.Due(t));
        }
    }

    [Fact]
    public void A_coarse_timer_still_gives_fifty_steps_per_second()
    {
        // Таймер Windows: тик приходить за 46,875 мс (3 × 15,625) — гра не має від цього сповільнитись.
        var pacer = new RunnerPacer();
        var t = DateTimeOffset.Parse("2026-09-27T10:00:00Z");
        pacer.Due(t);
        var steps = 0;
        for (var i = 0; i < 640; i++)
        {
            t = t.AddTicks(468750);
            var n = pacer.Due(t);
            Assert.InRange(n, 2, 3);
            steps += n;
        }
        Assert.InRange(steps, 1499, 1500);   // 640 × 46,875 мс = 30 с = 1500 кроків
    }

    [Fact]
    public void A_sleeping_server_does_not_catch_up_more_than_four_steps()
    {
        var pacer = new RunnerPacer();
        var t = DateTimeOffset.Parse("2026-09-27T10:00:00Z");
        pacer.Due(t);
        Assert.Equal(RunnerPacer.MaxSteps, pacer.Due(t.AddSeconds(2)));
        Assert.Equal(2, pacer.Due(t.AddSeconds(2).AddMilliseconds(40)));   // борг не тягнеться далі
        pacer.Reset();
        Assert.Equal(2, pacer.Due(t.AddSeconds(9)));
    }

    // ---------- парність із JS ----------

    /// <summary>
    /// Фікстури dino.md §8.3. Очікувані хеші порахував runner.js у headless Chrome
    /// (docs/games/dev/runner-check.html → runnerCheck()); C# мусить дати рівно ті самі.
    /// </summary>
    public static readonly (string Name, Func<RunnerSim> Make, uint Js)[] Fixtures =
    [
        ("dino 1×3000", () => RunnerSim.Fixture(RunnerMode.Dino, 1693571063, 1, 11, 3000, 150, 1000, false), 1063127438u),
        ("dino 8×6000", () => RunnerSim.Fixture(RunnerMode.Dino, 424242, 8, 12, 6000, 150, 1000, true), 2442894996u),
        ("dino 8×3000 сніжки", () => RunnerSim.Fixture(RunnerMode.Dino, 777, 8, 13, 3000, 150, 1000, true, [(600, 0), (1500, 3), (2400, 5)]), 550162003u),
        ("storks 4×4000", () => RunnerSim.Fixture(RunnerMode.Storks, 402881377, 4, 14, 4000, 150, 1000, false), 3833156659u),
        ("dino-daily 1×5000", () => RunnerSim.Fixture(RunnerMode.Dino, Days.Seed("dino-daily", "2026-09-27"), 1, 15, 5000, 0, 1250, false), 1039721497u),
    ];

    [Fact]
    public void Simulation_hash_matches_the_javascript_reference_for_every_fixture()
    {
        foreach (var (name, make, js) in Fixtures)
        {
            var sim = make();
            Assert.True(js == sim.Hash, $"{name}: C# {sim.Hash}, JS {js}");
            var p = sim.P[0];
            if (sim.Mode == RunnerMode.Dino) Assert.True(p.Hits > 0, $"{name}: жодного спотику — фікстура нічого не доводить");
            else Assert.True(p.Hits > 0 || p.Out, name);
        }
        // хоч одна фікстура має і взяті яйця
        Assert.Contains(Fixtures, f => { var s = f.Make(); return s.P.Any(p => p.Eggs > 0); });
    }
}
