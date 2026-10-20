using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>«Намисто»: жолоби трас і цілочислова симуляція разка (вставка, розсип, магніт, бонуси, кінці).</summary>
public class NamystoCoreTests(ITestOutputHelper output)
{
    const int Px = NamystoSim.Px, Dm = NamystoSim.Dm;

    static NamystoLevel L1 => NamystoTracks.Level(1);

    /// <summary>Сим без випадковості: разок задаємо руками, нових намистин не буде.</summary>
    static NamystoSim Bare(int[] colors, int headD, int speed = 6, NamystoLevel? lv = null)
    {
        lv ??= L1;
        var sim = new NamystoSim(lv.Track, 6, 1, speed, 100, 7) { ToSpawn = 0, Intro = false, BonusIn = 1_000_000 };
        for (var i = 0; i < colors.Length; i++) sim.Beads.Add(new Bead { C = colors[i], D = headD - i * Dm, Id = 100 + i });
        return sim;
    }

    /// <summary>Вистрілити так, щоб намистина влучила в намистину i з боку криниці (ahead) чи з боку хвоста.</summary>
    static void ShootAt(NamystoSim sim, int i, int color, bool ahead)
    {
        sim.Cur = color;
        var d = sim.Beads[i].D + (ahead ? Dm / 3 : -Dm / 3);
        var (x, y) = sim.Track.At(d);
        Assert.True(sim.Fire(x / Px, y / Px));
        for (var k = 0; k < 400 && sim.Shots.Count > 0; k++) sim.Step();
        Assert.Empty(sim.Shots);
    }

    // ---------- жолоби ----------

    [Fact]
    public void Twelve_tracks_enter_from_outside_end_inside_and_never_crowd_themselves_or_the_jug()
    {
        Assert.Equal(NamystoTracks.Count, NamystoTracks.Levels.Count);
        foreach (var lv in NamystoTracks.Levels)
        {
            var t = lv.Track;
            var tag = $"{lv.N} «{lv.Name}»";
            Assert.True(t.X[0] < -40 * Px || t.Y[0] < -40 * Px || t.X[0] > 760 * Px || t.Y[0] > 760 * Px, $"{tag}: вхід не за полем");
            Assert.InRange(t.X[^1], 40 * Px, 680 * Px);
            Assert.InRange(t.Y[^1], 40 * Px, 680 * Px);
            Assert.InRange(t.End, 2500 * Px, 6500 * Px);
            for (var i = 1; i < t.Count; i++) Assert.True(t.L[i] > t.L[i - 1], $"{tag}: відрізок нульової довжини на {i}");
            // жолоб, що вже зайшов у поле, не виходить за нього: намистина лишається видимою цілком
            var inside = false;
            for (var i = 0; i < t.Count; i++)
            {
                var deep = t.X[i] >= 24 * Px && t.Y[i] >= 24 * Px && t.X[i] <= 696 * Px && t.Y[i] <= 696 * Px;
                inside |= deep;
                if (!inside) continue;
                Assert.True(t.X[i] >= 16 * Px && t.Y[i] >= 16 * Px && t.X[i] <= 704 * Px && t.Y[i] <= 704 * Px,
                    $"{tag}: точка {i} ({t.X[i] / Px}, {t.Y[i] / Px}) біля самого краю");
            }
            // сусідні витки — не ближче за дві намистини; Глек — не ближче за 70 px від жолоба
            long min = long.MaxValue, jug = long.MaxValue;
            for (var i = 0; i < t.Count; i++)
            {
                long jx = t.X[i] - t.Sx, jy = t.Y[i] - t.Sy;
                jug = Math.Min(jug, jx * jx + jy * jy);
                for (var j = i + 1; j < t.Count; j++)
                {
                    if (t.L[j] - t.L[i] < 4 * Dm) continue;
                    long dx = t.X[i] - t.X[j], dy = t.Y[i] - t.Y[j];
                    min = Math.Min(min, dx * dx + dy * dy);
                }
            }
            output.WriteLine($"{tag}: {t.End / Px} px, {t.Count} точок, витки ≥ {Math.Sqrt(min) / Px:0} px, Глек ≥ {Math.Sqrt(jug) / Px:0} px");
            Assert.True(min >= 2L * Dm * 2 * Dm, $"{tag}: витки зійшлись на {Math.Sqrt(min) / Px:0} px");
            Assert.True(jug >= 70L * Px * 70 * Px, $"{tag}: жолоб за {Math.Sqrt(jug) / Px:0} px від Глека");
        }
    }

    [Fact]
    public void Daily_track_comes_from_the_day_seed_with_middle_settings()
    {
        var a = NamystoTracks.Daily("2026-10-20");
        Assert.Equal(a, NamystoTracks.Daily("2026-10-20"));
        Assert.Equal((5, 120, 10, 140), (a.Colors, a.Beads, a.Speed, a.Par));
        Assert.Same(NamystoTracks.Level(a.N).Track, a.Track);
        var days = Enumerable.Range(0, 30).Select(i => NamystoTracks.Daily(new DateOnly(2026, 10, 1).AddDays(i).ToString("yyyy-MM-dd")).N).Distinct().Count();
        Assert.True(days >= 6, $"за місяць лише {days} різних трас");
    }

    [Fact]
    public void Track_position_is_linear_between_points_and_clamped_at_the_ends()
    {
        var t = new NamystoTrack([0, 0, 1000, 0, 1000, 500], 0, 0);
        Assert.Equal(1500, t.End);
        Assert.Equal((0, 0), t.At(-50));
        Assert.Equal((250, 0), t.At(250));
        Assert.Equal((1000, 0), t.At(1000));
        Assert.Equal((1000, 300), t.At(1300));
        Assert.Equal((1000, 500), t.At(9999));
        Assert.Equal((0, 500), t.Tangent(1200));
        Assert.Equal(46340L, NamystoSim.Isqrt(2147395600L));
        Assert.Equal(46339L, NamystoSim.Isqrt(2147395599L));
        Assert.Equal(0L, NamystoSim.Isqrt(-5));
    }

    [Fact]
    public void Rng_is_mulberry32()
    {
        var r = new NamystoRng(1);
        // ті самі числа дає JS-двійник (namysto-sim.js) — звірено паритетом
        var a = r.Next();
        var b = r.Next();
        Assert.NotEqual(a, b);
        var r2 = new NamystoRng(1);
        Assert.Equal(a, r2.Next());
        Assert.Equal(2581720956u, new NamystoRng(42).Next());
    }

    // ---------- разок ----------

    [Fact]
    public void Chain_rolls_in_fast_then_slow_and_falls_into_the_well_without_shots()
    {
        var lv = L1;
        var sim = new NamystoSim(lv.Track, lv.Colors, lv.Beads, lv.Speed, lv.Par, 5);
        Assert.Equal(lv.Beads, sim.ToSpawn);
        sim.Step();
        Assert.Equal(0, sim.Beads[0].D);
        sim.Step();
        Assert.Equal(NamystoSim.IntroSpeed, sim.Beads[0].D);
        while (sim.Intro) sim.Step();
        Assert.True(sim.Beads[0].D * 100L >= (long)sim.End * NamystoSim.IntroPct);
        var head = sim.Beads[0].D;
        sim.Step();
        Assert.Equal(head + lv.Speed, sim.Beads[0].D);
        // сусіди — рівно на діаметр
        for (var i = 1; i < sim.Beads.Count; i++) Assert.Equal(Dm, sim.Beads[i - 1].D - sim.Beads[i].D);
        while (sim.State == NamystoSim.Playing && sim.S < 60 * 60 * 10) sim.Step();
        Assert.Equal(NamystoSim.Lost, sim.State);
        Assert.True(sim.Beads[0].D >= sim.End);
        Assert.Equal(0, sim.Score);
    }

    [Fact]
    public void Same_seed_and_journal_give_the_same_game_to_the_bit()
    {
        var lv = NamystoTracks.Level(4);
        var (a, journal) = NamystoBot.Play(lv, 11);
        var b = NamystoBot.Replay(lv, 11, journal);
        Assert.Equal(a.Hash(), b.Hash());
        Assert.Equal(a.Score, b.Score);
        Assert.Equal(a.State, b.State);
        var c = NamystoBot.Replay(lv, 12, journal);
        Assert.NotEqual(a.Hash(), c.Hash());
    }

    [Fact]
    public void Third_bead_of_a_colour_pops_the_run_and_pays_ten_each()
    {
        var sim = Bare([1, 2, 2, 3], 2000 * Px);
        ShootAt(sim, 2, 2, ahead: false);
        Assert.Equal(new[] { 1, 3 }, sim.Beads.Select(b => b.C));
        Assert.Equal(30, sim.Score);
        Assert.Equal(3, sim.Popped);
        Assert.Equal(1, sim.Streak);
        Assert.Equal(1, sim.Fired);
        Assert.Equal(0, sim.Misses);
    }

    [Fact]
    public void Shot_into_the_front_half_lands_ahead_and_the_front_is_pushed_smoothly()
    {
        var sim = Bare([1, 2, 3], 2000 * Px);
        var head = sim.Beads[0].D;
        ShootAt(sim, 1, 4, ahead: true);
        Assert.Equal(new[] { 1, 4, 2, 3 }, sim.Beads.Select(b => b.C));
        for (var k = 0; k < 20; k++) sim.Step();
        // голову розсунуло рівно на одну намистину (плюс те, що проїхав хвіст)
        Assert.Equal(Dm, sim.Beads[0].D - sim.Beads[1].D);
        Assert.Equal(Dm, sim.Beads[1].D - sim.Beads[2].D);
        Assert.True(sim.Beads[0].D >= head + Dm);
        Assert.Equal(0, sim.Score);
        Assert.Equal(0, sim.Streak);
    }

    [Fact]
    public void Gap_with_the_same_colour_on_both_edges_pulls_back_and_pops_as_a_combo()
    {
        // 5 · 2 2 · [1 1 1 ← постріл] · 2 · 4: після розсипу одиниць двійки зводить магніт — трійка, комбо ×2
        var sim = Bare([5, 2, 2, 1, 1, 2, 4], 2500 * Px);
        ShootAt(sim, 4, 1, ahead: false);
        Assert.Equal(new[] { 5, 2, 2, 2, 4 }, sim.Beads.Select(b => b.C));
        Assert.True(sim.Beads[2].D - sim.Beads[3].D > Dm);
        for (var k = 0; k < 200 && sim.Beads.Count > 2; k++) sim.Step();
        Assert.Equal(new[] { 5, 4 }, sim.Beads.Select(b => b.C));
        Assert.Equal(2, sim.MaxCombo);
        Assert.Equal(30 + 30 + NamystoSim.PtsCombo, sim.Score);
        // 5 і 4 різні — шматок спереду стоїть, хвіст доганяє
        for (var k = 0; k < 400; k++) sim.Step();
        Assert.Equal(Dm, sim.Beads[0].D - sim.Beads[1].D);
        Assert.Equal(0, sim.ChainCombo);
    }

    [Fact]
    public void Gap_with_different_edges_stands_still_until_the_tail_catches_up()
    {
        var sim = Bare([5, 1, 1, 4], 2500 * Px, speed: 8);
        ShootAt(sim, 2, 1, ahead: false);
        var head = sim.Beads[0].D;
        sim.Step();
        Assert.Equal(head, sim.Beads[0].D);
        Assert.Equal(new[] { 5, 4 }, sim.Beads.Select(b => b.C));
        var steps = 0;
        while (sim.Beads[0].D == head) { sim.Step(); steps++; }
        // розрив — три намистини, тож хвостові треба проїхати дві (1024 su) по 8 su за крок
        Assert.InRange(steps, 120, 135);
    }

    [Fact]
    public void Swap_trades_the_mouth_and_the_next_bead()
    {
        var sim = Bare([1, 2], 2000 * Px);
        (sim.Cur, sim.Next) = (1, 2);
        Assert.True(sim.Swap());
        Assert.Equal((2, 1), (sim.Cur, sim.Next));
    }

    [Fact]
    public void Fire_waits_for_the_cooldown_and_refuses_to_aim_at_the_jug_itself()
    {
        var sim = Bare([1, 2, 3, 4], 2000 * Px);
        Assert.False(sim.Fire(sim.Track.Sx / Px, sim.Track.Sy / Px));
        Assert.True(sim.Fire(0, 0));
        Assert.False(sim.Fire(0, 0));
        for (var k = 0; k < NamystoSim.Cooldown; k++) sim.Step();
        Assert.True(sim.Fire(0, 0));
        Assert.Equal(2, sim.Fired);
    }

    /// <summary>
    /// Жолоб-підкова для ручних перевірок: ряд y=200 зліва направо, вгору, ряд y=100 справа наліво; Глек — у (360, 400).
    /// На ряду y=200 відстань уздовж жолоба — це x + 100.
    /// </summary>
    static NamystoSim Lane(params (int C, int D)[] beads)
    {
        var t = new NamystoTrack([-100 * Px, 200 * Px, 700 * Px, 200 * Px, 700 * Px, 100 * Px, -100 * Px, 100 * Px], 360 * Px, 400 * Px);
        var sim = new NamystoSim(t, 6, 1, 1, 100, 7) { ToSpawn = 0, Intro = false, BonusIn = 1_000_000 };
        var id = 1;
        foreach (var (c, d) in beads) sim.Beads.Add(new Bead { C = c, D = d * Px, Id = id++ });
        return sim;
    }

    [Fact]
    public void Shot_that_leaves_the_field_is_a_miss_and_breaks_the_streak()
    {
        var sim = Lane((1, 500), (2, 468));
        sim.Streak = 4;
        Assert.True(sim.Fire(360, 900));
        for (var k = 0; k < 200 && sim.Shots.Count > 0; k++) sim.Step();
        Assert.Empty(sim.Shots);
        Assert.Equal(1, sim.Misses);
        Assert.Equal(0, sim.Streak);
        Assert.Equal(2, sim.Beads.Count);
    }

    [Fact]
    public void New_beads_come_only_in_colours_still_on_the_string()
    {
        var sim = Bare([3, 3, 1, 3], 2000 * Px);
        for (var k = 0; k < 50; k++)
        {
            Assert.True(sim.Fire(0, 0));
            Assert.Contains(sim.Next, new[] { 1, 3 });
            Assert.Contains(sim.Cur, new[] { 1, 3 });
            sim.Shots.Clear();
            sim.CooldownLeft = 0;
        }
    }

    [Fact]
    public void Jug_drops_colours_the_string_no_longer_has_even_after_it_ran_empty_and_refilled()
    {
        var sim = Bare([1, 2], 2000 * Px);
        (sim.Cur, sim.Next) = (0, 5);
        sim.Step();
        Assert.Contains(sim.Cur, new[] { 1, 2 });
        Assert.Contains(sim.Next, new[] { 1, 2 });
    }

    [Fact]
    public void Clearing_the_string_wins_with_clear_and_time_bonus()
    {
        var sim = Bare([2, 2], 2000 * Px);
        ShootAt(sim, 1, 2, ahead: false);
        Assert.Empty(sim.Beads);
        sim.Step();
        Assert.Equal(NamystoSim.Won, sim.State);
        Assert.Equal(NamystoSim.PtsClear, sim.ClearBonus);
        Assert.True(sim.TimeBonus > 0);
        Assert.Equal(30 + sim.ClearBonus + sim.TimeBonus, sim.Score);
        var after = sim.Hash();
        sim.Step();
        Assert.Equal(after, sim.Hash());
    }

    [Fact]
    public void Bonuses_slow_the_tail_roll_it_back_sharpen_the_eye_and_the_pot_blows_its_neighbours()
    {
        // сповільнення
        var sim = Bare([1, 1, 4, 3, 3, 5, 2], 2000 * Px, speed: 8);
        sim.Beads[0].Bonus = NamystoSim.BonusSlow; sim.Beads[0].BonusLeft = 500;
        ShootAt(sim, 1, 1, ahead: false);
        Assert.True(sim.SlowLeft > 0);
        var tail = sim.Beads[^1].D;
        sim.Step();
        Assert.Equal(tail + 4, sim.Beads[^1].D);

        // назад
        sim = Bare([1, 1, 4, 5, 2], 2000 * Px);
        sim.Beads[1].Bonus = NamystoSim.BonusBack; sim.Beads[1].BonusLeft = 500;
        ShootAt(sim, 1, 1, ahead: false);
        Assert.True(sim.BackLeft > 0);
        tail = sim.Beads[^1].D;
        sim.Step();
        Assert.Equal(tail - NamystoSim.BackSpeed, sim.Beads[^1].D);

        // око: швидші постріли
        sim = Bare([1, 1, 4, 5, 2], 2000 * Px);
        sim.Beads[0].Bonus = NamystoSim.BonusEye; sim.Beads[0].BonusLeft = 500;
        ShootAt(sim, 1, 1, ahead: false);
        Assert.True(sim.EyeLeft > 0);
        Assert.True(sim.Fire(0, 0));
        Assert.Equal(NamystoSim.SubsEye, sim.Shots[^1].Subs);

        // горщик із жаром: усе в радіусі 112 px від місця, де лежав (рахуємо геометрією жолоба)
        sim = Bare([4, 5, 0, 2, 1, 1, 3, 5, 4, 2, 3], 2000 * Px);
        sim.Beads[4].Bonus = NamystoSim.BonusBomb; sim.Beads[4].BonusLeft = 500;
        var (px, py) = sim.Track.At(sim.Beads[4].D);
        var near = sim.Beads.Where((b, i) => i is not (4 or 5)).Count(b =>
        {
            var (x, y) = sim.Track.At(b.D);
            return (long)(x - px) * (x - px) + (long)(y - py) * (y - py) <= (long)NamystoSim.BombR * NamystoSim.BombR;
        });
        Assert.InRange(near, 4, 6);
        ShootAt(sim, 5, 1, ahead: false);
        Assert.Equal(11 - 2 - near, sim.Beads.Count);
        Assert.Equal(30 + near * NamystoSim.PtsBead, sim.Score);
    }

    [Fact]
    public void Bonus_appears_on_a_visible_bead_and_fades_out()
    {
        var sim = Bare([1, 2, 3, 4, 5, 1, 2, 3], 2000 * Px);
        sim.BonusIn = 0;
        sim.Step();
        sim.BonusIn = 1_000_000;
        var b = sim.Beads.Single(x => x.Bonus > 0);
        Assert.InRange(b.Bonus, 1, NamystoSim.BonusKinds);
        Assert.Equal(NamystoSim.BonusLife, b.BonusLeft);
        for (var k = 0; k < NamystoSim.BonusLife; k++) sim.Step();
        Assert.DoesNotContain(sim.Beads, x => x.Bonus > 0);
    }

    [Fact]
    public void Shot_through_a_narrow_gap_pays_the_gap_bonus()
    {
        // ряд y=100: пара двійок над Глеком (x 344 і 376); ряд y=200: 5 (x 400) і 4 (x 320) — розрив 80 px, постріл
        // угору по x=360 проходить між ними (до кожної 40 px) і влучає в пару
        var sim = Lane((2, 900 + 700 - 344), (2, 900 + 700 - 376), (5, 400 + 100), (4, 320 + 100));
        sim.Cur = 2;
        Assert.True(sim.Fire(360, 0));
        for (var k = 0; k < 200 && sim.Shots.Count > 0; k++) sim.Step();
        Assert.Equal(new[] { 5, 4 }, sim.Beads.Select(b => b.C));
        Assert.Equal(30 + NamystoSim.PtsGap, sim.Score);
    }

    // ---------- баланс (діагностика, не правило) ----------

    [Fact]
    [Trait("Category", "Perf")]
    public void Bot_can_clear_the_first_tracks_and_a_full_game_runs_fast()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long steps = 0;
        foreach (var (every, err) in new[] { (12, 0), (36, 0), (36, 14) })
            foreach (var lv in NamystoTracks.Levels)
            {
                int wins = 0;
                long sum = 0, secs = 0;
                for (uint seed = 1; seed <= 6; seed++)
                {
                    var (sim, _) = NamystoBot.Play(lv, seed, every: every, err: err);
                    steps += sim.S;
                    secs += sim.S / 60;
                    if (sim.State == NamystoSim.Won) wins++;
                    sum += sim.Score;
                }
                output.WriteLine($"кожні {every}, ±{err} px: {lv.N} «{lv.Name}»: бот {wins}/6, в середньому {sum / 6} очок, {secs / 6} с");
                if (lv.N == 1 && err == 0) Assert.True(wins >= 1, "перша траса не по зубах навіть боту");
            }
        output.WriteLine($"{steps} кроків за {sw.ElapsedMilliseconds} мс");
        Assert.True(sw.ElapsedMilliseconds < 20_000);
    }
}
