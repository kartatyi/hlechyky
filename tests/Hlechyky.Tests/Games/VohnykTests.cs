using System.Text.Json;
using System.Text.RegularExpressions;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Вогник і Крапля» (specs/vohnyk.md §8): рівні й записані проходження, фізика на синтетичних майданчиках,
/// механізми, партія за столом, мережа (журнал вводу, перемотування), види й кадри, сховище, ачівки.
/// </summary>
public sealed class VohnykTests(ITestOutputHelper output)
{
    const int T = VohnykWorld.TileSu;
    const int R = VohnykWorld.KeyRight, L = VohnykWorld.KeyLeft, J = VohnykWorld.KeyJump;
    const int Go0 = Vohnyk.ReadySteps;   // крок гри перед першим кроком «go»

    // =============================================================================================
    // Допоміжне
    // =============================================================================================

    /// <summary>Мапа w×h: рамка й підлога (ряд h−2) з каменю, решта — повітря, потім заливки (c0, c1, r0, r1, символ).</summary>
    static string[] Map(int w, int h, params (int C0, int C1, int R0, int R1, char Ch)[] fills)
    {
        var g = new char[h][];
        for (var r = 0; r < h; r++)
        {
            g[r] = new char[w];
            for (var c = 0; c < w; c++)
                g[r][c] = r == 0 || r >= h - 2 || c == 0 || c == w - 1 ? '#' : '.';
        }
        foreach (var (c0, c1, r0, r1, ch) in fills)
            for (var r = r0; r <= r1; r++)
                for (var c = c0; c <= c1; c++)
                    g[r][c] = ch;
        return [.. g.Select(r => new string(r))];
    }

    static readonly string[] Flat = Map(24, 12);

    /// <summary>Синтетичний рівень: спавни на підлозі (ряд h−3), виходи — вгорі, де нікому не заважають.</summary>
    static VohnykLevel Lvl(string[] rows, int[]? fire = null, int[]? water = null, Action<VohnykLevelFile>? tweak = null)
    {
        var f = new VohnykLevelFile
        {
            N = 99, Name = "майданчик", Par = 60000, W = rows[0].Length, H = rows.Length, Rows = rows,
            Spawn = new VohnykSpawnFile { Fire = fire ?? [2, rows.Length - 3], Water = water ?? [4, rows.Length - 3] },
            Exits = new VohnykSpawnFile { Fire = [rows[0].Length - 4, 1], Water = [rows[0].Length - 3, 1] },
        };
        tweak?.Invoke(f);
        return VohnykLevels.Build(f);
    }

    static VohnykWorld W(string[] rows, int[]? fire = null, int[]? water = null, Action<VohnykLevelFile>? tweak = null) =>
        new(Lvl(rows, fire, water, tweak));

    static VohnykIdAtFile Btn(string id, int c, int r) => new() { Id = id, At = [c, r] };
    static VohnykDoorFile Door(string id, int c, int r, int h, string[] by, string mode = "any", bool inv = false) =>
        new() { Id = id, At = [c, r], H = h, By = by, Mode = mode, Inv = inv };

    static RoomHarness Table(VohnykStore? store = null, params string[] nicks)
    {
        var h = new RoomHarness("vohnyk", services: RoomHarness.WithService(store ?? new VohnykStore(null)));
        foreach (var n in nicks.Length == 0 ? ["Оля", "Петро"] : nicks) h.Join(n);
        return h;
    }

    static Vohnyk G(RoomHarness h) => (Vohnyk)h.Room.Game;

    static JsonElement Frame(RoomHarness h)
    {
        lock (h.Room.Sync) return Views.Json(h.Room.Game.Frame());
    }

    /// <summary>Тикати, доки гра не дійде до кроку step (кроки йдуть по два на тик).</summary>
    static void TickTo(RoomHarness h, int step)
    {
        while (G(h).StepNo < step && h.Room.Status == RoomStatus.Playing) h.Tick();
    }

    static void In(RoomHarness h, int seat, int n, int c, int k) => h.Input(seat, "in", new { n, c, k });

    /// <summary>
    /// Зіграти записаний журнал рівня за столом: кожен запис [крок go, герой, k] іде вводом рівно тоді, коли його крок
    /// ось-ось настане (як від клієнта з маленьким випередженням). soloSeat — усе з одного місця. Як і справжній
    /// клієнт, поки герой тримає клавішу, раз на 25 кроків нагадує про неї (інакше сервер вирішив би, що гравець зник).
    /// </summary>
    static void Play(RoomHarness h, int[][] log, int? soloSeat = null, int? until = null, Func<bool>? stop = null)
    {
        var entries = log.OrderBy(e => e[0]).ToList();
        var p = 0;
        int[] lastK = [0, 0], lastN = [0, 0], nudged = [0, 0];
        for (var guard = 0; guard < 20000 && h.Room.Status == RoomStatus.Playing; guard++)
        {
            var s = G(h).StepNo;
            if (until is { } u && s >= u) return;
            if (stop is not null && stop()) return;
            while (p < entries.Count && Go0 + entries[p][0] <= s + Vohnyk.StepsPerTick)
            {
                var e = entries[p++];
                In(h, soloSeat ?? e[1], Go0 + e[0], e[1], e[2]);
                (lastK[e[1]], lastN[e[1]], nudged[e[1]]) = (e[2], Go0 + e[0], s);
            }
            for (var c = 0; c < 2; c++)
                if (lastK[c] != 0 && s - nudged[c] >= 25)
                {
                    In(h, soloSeat ?? c, Math.Max(s + 1, lastN[c]), c, lastK[c]);
                    nudged[c] = s;
                }
            h.Tick();
        }
    }

    static VohnykStore Unlocked(int upTo, params string[] nicks)
    {
        var store = new VohnykStore(null);
        for (var n = 1; n < upTo; n++) store.Record(nicks.Length == 0 ? ["Оля"] : nicks, n, 99000, 0, 1, DateTimeOffset.UnixEpoch);
        return store;
    }

    // =============================================================================================
    // Рівні й дані
    // =============================================================================================

    [Fact]
    public void All_fifteen_levels_load_and_pass_validation()
    {
        var all = VohnykLevels.LoadDir(Paths.Resolve(VohnykLevels.Dir));
        Assert.Equal(15, all.Length);
        for (var i = 0; i < all.Length; i++)
        {
            var l = all[i];
            Assert.Equal(i + 1, l.N);
            Assert.Empty(VohnykLevels.Validate(l));
            Assert.NotEmpty(l.Solution);
            Assert.NotNull(l.Check);
            Assert.False(string.IsNullOrWhiteSpace(l.Name));
        }
    }

    [Fact]
    public void Levels_carry_the_gems_the_spec_promises_and_grow_in_size()
    {
        int[] perHero = [1, 1, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5];
        foreach (var l in VohnykLevels.All)
        {
            Assert.Equal(perHero[l.N - 1], l.GemsOf(0));
            Assert.Equal(perHero[l.N - 1], l.GemsOf(1));
            if (l.N >= 8) Assert.Equal((30, 17), (l.W, l.H));
        }
    }

    [Fact]
    public void Every_level_is_cleared_by_its_recorded_solution_with_all_gems()
    {
        foreach (var l in VohnykLevels.All)
        {
            var run = VohnykRecord.Replay(l, l.Solution);
            Assert.True(run.ClearedAt > 0, $"рівень {l.N}: журнал не проходить (смерть на {run.DiedAt})");
            Assert.Equal(l.AllGemsMask, run.Gems);
        }
    }

    [Fact]
    public void Every_level_solution_matches_its_recorded_check_hashes()
    {
        foreach (var l in VohnykLevels.All)
        {
            var run = VohnykRecord.Replay(l, l.Solution);
            var check = l.Check!;
            Assert.Equal(check.Steps, run.ClearedAt);
            Assert.Equal(check.Hash, run.Hash);
            Assert.Equal(check.Hashes, run.Hashes);
            Assert.Equal(VohnykRecord.Every, check.Every);
        }
    }

    [Fact]
    public void Level_four_is_cleared_alone_moving_one_hero_at_a_time_and_its_solo_run_matches_its_hashes()
    {
        var lv = VohnykLevels.Get(4);
        var so = lv.Solo!;
        Assert.NotNull(so.Check);
        // у кожен момент клавіші тримає лише один герой — як у людини з одною парою рук
        var k = new int[2];
        foreach (var grp in so.Solution.GroupBy(e => e[0]).OrderBy(g => g.Key))
        {
            foreach (var e in grp) k[e[1]] = e[2];
            Assert.False(k[0] != 0 && k[1] != 0, $"крок {grp.Key}: обидва герої тримають клавіші");
        }
        var run = VohnykRecord.Replay(lv, so.Solution, solo: true);
        Assert.Equal(so.Check!.Steps, run.ClearedAt);
        Assert.Equal(lv.AllGemsMask, run.Gems);
        Assert.Equal(so.Check.Hash, run.Hash);
        Assert.Equal(so.Check.Hashes, run.Hashes);
        // без «тримання» кнопки брами той самий журнал не проходить: удвох брами на рахунок «три»
        Assert.Equal(0, VohnykRecord.Replay(lv, so.Solution, maxSteps: 3000, solo: false).ClearedAt);
        // і за столом сам за двох — проходить
        var h = Table(Unlocked(4, "Оля"), "Оля");
        h.Act(0, "pick", new { level = 4 });
        h.Start();
        Play(h, so.Solution, soloSeat: 0);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(h.View(0).GetProperty("result").GetProperty("cleared").GetBoolean());
    }

    [Fact]
    public void Par_is_at_least_the_recorded_solution_time()
    {
        foreach (var l in VohnykLevels.All)
        {
            Assert.True(l.Par >= l.Check!.Steps * Vohnyk.StepMs, $"рівень {l.N}: par {l.Par} < {l.Check.Steps * 20}");
            Assert.Equal(0, l.Par % 5000);
        }
    }

    [Fact]
    public void Only_the_two_tutorial_levels_carry_hints()
    {
        foreach (var l in VohnykLevels.All)
            if (l.N <= 2) Assert.NotEmpty(l.Hints);
            else Assert.Empty(l.Hints);
    }

    [Fact]
    public void Level_view_never_leaks_solution_or_check()
    {
        var store = Unlocked(16);
        foreach (var l in VohnykLevels.All)
        {
            var h = Table(store, "Оля");
            Assert.True(h.Act(0, "pick", new { level = l.N }).Ok);
            foreach (var text in new[] { Views.Text(h.Room.Game.View(0)), Views.Text(h.Room.Game.View(null)) })
            {
                Assert.DoesNotContain("solution", text);
                Assert.DoesNotContain("check", text);
                Assert.DoesNotContain("hashes", text);
            }
            h.Start();
            h.Tick(3);
            var play = Views.Text(h.Room.Game.View(null)) + Views.Text(h.Room.Game.Frame());
            Assert.DoesNotContain("solution", play);
        }
    }

    // =============================================================================================
    // Фізика
    // =============================================================================================

    static int Rise(int held)
    {
        var w = W(Flat);
        var y0 = w.Y[0];
        var top = y0;
        for (var s = 0; s < 60; s++)
        {
            w.Step(s < held ? J : 0, 0);
            top = Math.Min(top, w.Y[0]);
        }
        return y0 - top;
    }

    [Fact]
    public void A_full_jump_rises_two_and_a_half_tiles_and_a_tap_one_and_a_third()
    {
        Assert.Equal(1600, Rise(99));   // 100 px = 2,5 плитки
        Assert.Equal(848, Rise(1));     // 53 px ≈ 1,3 плитки
    }

    [Fact]
    public void Jump_cut_waits_for_the_fourth_step_then_trims_velocity_to_two_fifths()
    {
        // відпустив на кроці 1..4 — зріз усе одно на четвертому: той самий «тап»
        for (var r = 1; r <= 4; r++) Assert.Equal(848, Rise(r));
        // на п'ятому: vy = −130 → trunc(−130·2/5) = −52 → 830 + 52 + 40 + 28 + 16 + 4
        Assert.Equal(970, Rise(5));
    }

    [Fact]
    public void Running_reaches_max_speed_in_six_steps_and_friction_stops_in_five()
    {
        var w = W(Flat);
        var run = new List<int>();
        for (var s = 0; s < 8; s++) { w.Step(R, 0); run.Add(w.Vx[0]); }
        Assert.Equal([12, 24, 36, 48, 60, 72, 72, 72], run);
        var stop = new List<int>();
        for (var s = 0; s < 6; s++) { w.Step(0, 0); stop.Add(w.Vx[0]); }
        Assert.Equal([56, 40, 24, 8, 0, 0], stop);
        Assert.Equal(1, w.Facing[0]);
        w.Step(L, 0);
        Assert.Equal(0, w.Facing[0]);
    }

    [Fact]
    public void Walls_stop_the_hero_touching_them_and_zero_the_speed()
    {
        var w = W(Map(24, 12, (8, 8, 8, 9, '#')));
        for (var s = 0; s < 100; s++) w.Step(R, 0);
        Assert.Equal(8 * T - VohnykWorld.HeroW, w.X[0]);
        Assert.Equal(0, w.Vx[0]);
        for (var s = 0; s < 100; s++) w.Step(L, 0);
        Assert.Equal(T, w.X[0]);   // ліва рамка
    }

    [Fact]
    public void Ceilings_stop_the_jump_and_floors_land_the_fall_capped_at_fall_max()
    {
        // стеля — ряд 7: від підлоги (10·640) до низу стелі (8·640) лишається 1280 su, герой 576 — підскочить на 704
        var w = W(Map(24, 12, (1, 22, 7, 7, '#')));
        var y0 = w.Y[0];
        var top = y0;
        for (var s = 0; s < 40; s++) { w.Step(J, 0); top = Math.Min(top, w.Y[0]); }
        Assert.Equal(704, y0 - top);

        var fall = W(Map(24, 17));
        fall.Y[0] = T;
        fall.Grounded[0] = 0;
        var maxVy = 0;
        for (var s = 0; s < 200; s++) { fall.Step(0, 0); maxVy = Math.Max(maxVy, fall.Vy[0]); }
        Assert.Equal(VohnykWorld.FallMax, maxVy);
        Assert.Equal(15 * T - VohnykWorld.HeroH, fall.Y[0]);
        Assert.Equal(1, fall.Grounded[0]);
    }

    /// <summary>Біжимо з краю уступу й тиснемо стрибок через j кроків після того, як опора зникла.</summary>
    static bool CoyoteJump(int j)
    {
        var w = W(Map(24, 12, (9, 22, 10, 10, '.')), fire: [7, 9]);
        var s0 = -1;
        for (var s = 0; s < 200; s++)
        {
            var press = s0 >= 0 && s == s0 + j;
            w.Step(press ? R | J : R, 0);
            if (press) return w.Vy[0] < 0;
            if (s0 < 0 && w.Grounded[0] == 0) s0 = s;
        }
        return false;
    }

    [Fact]
    public void Coyote_time_allows_a_jump_four_steps_after_the_ledge_but_not_five()
    {
        for (var j = 1; j <= 4; j++) Assert.True(CoyoteJump(j), $"через {j} кроків ще можна");
        Assert.False(CoyoteJump(5));
    }

    [Fact]
    public void Jump_buffer_fires_on_landing_when_pressed_up_to_five_steps_early()
    {
        VohnykWorld Dropped()
        {
            var w = W(Flat);
            w.Y[0] = 5 * T;
            w.Grounded[0] = 0;
            return w;
        }
        var dry = Dropped();
        var land = -1;
        for (var s = 0; land < 0; s++) { dry.Step(0, 0); if (dry.Grounded[0] == 1) land = s; }

        bool Jumps(int press)
        {
            var w = Dropped();
            for (var s = 0; s <= land + 1; s++) w.Step(s >= press ? J : 0, 0);
            return w.Vy[0] < 0;
        }
        for (var p = land - 4; p <= land; p++) Assert.True(Jumps(p), $"натиснув на кроці {p}, приземлився на {land}");
        Assert.False(Jumps(land - 5));
    }

    [Fact]
    public void Fire_walks_on_lava_and_dies_in_water_and_water_the_other_way_round()
    {
        // своя рідина — підлога
        var onLava = W(Map(24, 12, (1, 6, 10, 10, 'L')), fire: [2, 9], water: [10, 9]);
        var y = onLava.Y[0];
        for (var s = 0; s < 40; s++) onLava.Step(0, 0);
        Assert.Equal(0, onLava.Died[0]);
        Assert.Equal(y, onLava.Y[0]);
        var onWater = W(Map(24, 12, (1, 6, 10, 10, 'W')), fire: [10, 9], water: [2, 9]);
        for (var s = 0; s < 40; s++) onWater.Step(0, 0);
        Assert.Equal(0, onWater.Died[1]);

        // чужа — смерть, щойно ноги в ній
        var intoWater = W(Map(24, 12, (6, 7, 10, 10, 'W')), fire: [2, 9], water: [15, 9]);
        for (var s = 0; s < 80 && intoWater.Died[0] == 0; s++) intoWater.Step(R, 0);
        Assert.Equal(1, intoWater.Died[0]);
        Assert.Equal(VohnykLevel.Water, intoWater.DeathTile(0));
        var intoLava = W(Map(24, 12, (6, 7, 10, 10, 'L')), fire: [15, 9], water: [2, 9]);
        for (var s = 0; s < 80 && intoLava.Died[1] == 0; s++) intoLava.Step(0, R);
        Assert.Equal(1, intoLava.Died[1]);
        Assert.Equal(VohnykLevel.Lava, intoLava.DeathTile(1));
    }

    [Fact]
    public void Mud_kills_both_and_a_box_sunk_in_mud_is_a_safe_bridge()
    {
        var rows = Map(24, 12, (8, 8, 10, 10, 'M'));
        var bare = W(rows, fire: [2, 9], water: [3, 9]);
        for (var s = 0; s < 120 && !bare.AnyDied; s++) bare.Step(R, R);
        Assert.True(bare.Died[0] == 1 || bare.Died[1] == 1);
        Assert.Equal(VohnykLevel.Mud, bare.DeathTile(bare.Died[0] == 1 ? 0 : 1));

        var bridged = W(rows, fire: [2, 9], water: [3, 9], tweak: f => f.Boxes = [new VohnykAtFile { At = [8, 6] }]);
        for (var s = 0; s < 40; s++) bridged.Step(0, 0);
        Assert.Equal(10 * T, bridged.BoxY[0]);   // скриня на дні болота: її верх — рівень підлоги
        for (var s = 0; s < 150; s++) bridged.Step(R, R);
        Assert.False(bridged.AnyDied);
        Assert.True(bridged.X[0] > 9 * T && bridged.X[1] > 9 * T);
    }

    [Fact]
    public void Heroes_pass_through_each_other()
    {
        var w = W(Flat, fire: [2, 9], water: [5, 9]);
        var water = w.X[1];
        for (var s = 0; s < 60; s++) w.Step(R, 0);
        Assert.True(w.X[0] > water + VohnykWorld.HeroW);
        Assert.Equal(water, w.X[1]);
    }

    [Fact]
    public void Fifteen_pixels_per_step_never_tunnel_through_a_one_tile_wall()
    {
        // висяча плита в одну плитку завтовшки; падаємо на неї з усіх фаз відносно сітки на граничній швидкості
        var rows = Map(24, 17, (5, 12, 8, 8, '#'));
        for (var off = 0; off < T; off += 16)
        {
            var w = W(rows, fire: [8, 14], water: [20, 14]);
            w.X[0] = 8 * T;
            w.Y[0] = 3 * T + off;
            w.Vy[0] = VohnykWorld.FallMax;
            w.Grounded[0] = 0;
            for (var s = 0; s < 60; s++) w.Step(0, 0);
            Assert.Equal(8 * T - VohnykWorld.HeroH, w.Y[0]);
        }
    }

    [Fact]
    public void A_box_is_pushed_two_pixels_per_step_and_a_wall_behind_it_blocks_the_pusher()
    {
        var w = W(Map(24, 12, (14, 14, 9, 9, '#')), fire: [2, 9], water: [20, 9],
            tweak: f => f.Boxes = [new VohnykAtFile { At = [6, 9] }]);
        var deltas = new List<int>();
        for (var s = 0; s < 60; s++)
        {
            var was = w.BoxX[0];
            w.Step(R, 0);
            if (w.BoxX[0] != was) deltas.Add(w.BoxX[0] - was);
        }
        Assert.True(deltas.Count > 20);
        Assert.All(deltas.Skip(3), d => Assert.Equal(VohnykWorld.BoxPush, d));   // після розгону — рівно 2 px за крок
        for (var s = 0; s < 400; s++) w.Step(R, 0);
        Assert.Equal(13 * T, w.BoxX[0]);                     // уперлась у стіну
        Assert.Equal(13 * T - VohnykWorld.HeroW, w.X[0]);    // і герой уперся в неї
        Assert.Equal(0, w.Vx[0]);
    }

    [Fact]
    public void A_box_falls_off_a_ledge_stacks_on_another_box_and_ignores_heroes()
    {
        // уступ cols 3..6 висотою два; скриня A на ньому, скриня B під ним праворуч
        var w = W(Map(24, 12, (3, 6, 8, 9, '#')), fire: [3, 7], water: [12, 9],
            tweak: f => f.Boxes = [new VohnykAtFile { At = [5, 7] }, new VohnykAtFile { At = [7, 9] }]);
        for (var s = 0; s < 300 && w.BoxY[0] == 7 * T; s++) w.Step(R, 0);
        for (var s = 0; s < 40; s++) w.Step(0, 0);
        Assert.Equal(7 * T, w.BoxX[0]);   // перекинулась рівно в клітинку
        Assert.Equal(8 * T, w.BoxY[0]);   // і лежить на скрині B
        Assert.Equal(9 * T, w.BoxY[1]);
    }

    [Fact]
    public void A_hero_caught_inside_a_box_is_pushed_up_onto_it()
    {
        var w = W(Flat, fire: [10, 9], water: [4, 9], tweak: f => f.Boxes = [new VohnykAtFile { At = [4, 3] }]);
        for (var s = 0; s < 80; s++) w.Step(0, 0);
        Assert.Equal(9 * T, w.BoxY[0]);                         // скриня впала крізь Краплю на підлогу
        Assert.Equal(9 * T - VohnykWorld.HeroH, w.Y[1]);        // а Крапля стоїть на ній
        Assert.Equal(1, w.Grounded[1]);
    }

    [Fact]
    public void A_box_hanging_more_than_half_over_a_pit_tips_into_it()
    {
        // яма в одну плитку з підлогою по обидва боки: скриню штовхають кроками по 2 px — рівно в яму вона не стане ніколи
        var w = W(Map(24, 12, (10, 10, 10, 10, 'M')), fire: [2, 9], water: [20, 9],
            tweak: f => f.Boxes = [new VohnykAtFile { At = [6, 9] }]);
        for (var s = 0; s < 200 && w.BoxY[0] < 10 * T; s++) w.Step(R, 0);
        Assert.Equal(10 * T, w.BoxX[0]);
        Assert.Equal(10 * T, w.BoxY[0]);
        for (var s = 0; s < 100; s++) w.Step(R, 0);
        Assert.False(w.AnyDied);                                 // місток
        Assert.True(w.X[0] > 11 * T);
    }

    // =============================================================================================
    // Механізми
    // =============================================================================================

    [Fact]
    public void A_button_is_pressed_by_a_hero_or_a_box_and_releases_when_they_leave()
    {
        var w = W(Flat, fire: [6, 9], water: [15, 9], tweak: f =>
        {
            f.Buttons = [Btn("b1", 6, 9), Btn("b2", 10, 9)];
            f.Boxes = [new VohnykAtFile { At = [10, 9] }];
        });
        Assert.Equal(1, w.Button[0]);   // герой стоїть на ній уже на старті
        Assert.Equal(1, w.Button[1]);   // і скриня теж
        for (var s = 0; s < 30; s++) w.Step(L, 0);
        Assert.Equal(0, w.Button[0]);
        Assert.Equal(1, w.Button[1]);
    }

    [Fact]
    public void An_any_door_opens_while_one_trigger_holds_and_closes_at_six_pixels_per_step()
    {
        var w = W(Flat, fire: [6, 9], water: [15, 9], tweak: f =>
        {
            f.Buttons = [Btn("b1", 6, 9), Btn("b2", 3, 9)];
            f.Doors = [Door("d1", 12, 8, 2, ["b1", "b2"])];
        });
        w.Step(0, 0);
        Assert.Equal(VohnykWorld.DoorSpeed, w.DoorO[0]);
        for (var s = 0; s < 20; s++) w.Step(0, 0);
        Assert.Equal(2 * T, w.DoorO[0]);
        while (w.Button[0] != 0) w.Step(R, 0);      // зійшов з кнопки (цей крок двері ще бачили натиснутою)
        Assert.Equal(2 * T, w.DoorO[0]);
        var was = w.DoorO[0];
        w.Step(0, 0);
        Assert.Equal(was - VohnykWorld.DoorSpeed, w.DoorO[0]);
        for (var s = 0; s < 30; s++) w.Step(0, 0);
        Assert.Equal(0, w.DoorO[0]);
    }

    [Fact]
    public void An_all_door_needs_every_trigger_and_inv_flips_the_logic()
    {
        var w = W(Flat, fire: [6, 9], water: [15, 9], tweak: f =>
        {
            f.Buttons = [Btn("b1", 6, 9), Btn("b2", 15, 9)];
            f.Doors = [Door("all", 12, 8, 2, ["b1", "b2"], "all"), Door("inv", 18, 8, 2, ["b1"], inv: true)];
        });
        for (var s = 0; s < 30; s++) w.Step(0, 0);
        Assert.Equal(2 * T, w.DoorO[0]);   // обидві кнопки натиснуті
        Assert.Equal(0, w.DoorO[1]);       // inv: b1 натиснута — зачинено
        for (var s = 0; s < 40; s++) w.Step(L, 0);
        Assert.Equal(0, w.DoorO[0]);
        Assert.Equal(2 * T, w.DoorO[1]);   // b1 вільна — inv відчинені
    }

    [Fact]
    public void A_closing_door_waits_instead_of_crushing_a_hero_or_a_box()
    {
        var w = W(Flat, fire: [6, 9], water: [12, 9], tweak: f =>
        {
            f.Buttons = [Btn("b1", 6, 9)];
            f.Doors = [Door("d1", 12, 8, 2, ["b1"])];
        });
        for (var s = 0; s < 20; s++) w.Step(0, 0);
        Assert.Equal(2 * T, w.DoorO[0]);
        for (var s = 0; s < 60; s++) w.Step(L, 0);   // Вогник пішов, Крапля стоїть у проході
        var head = w.Y[1];
        Assert.True(w.DoorO[0] > 0);
        Assert.True(8 * T + 2 * T - w.DoorO[0] <= head);   // низ дверей не нижче за голову Краплі
        Assert.Equal(10 * T - VohnykWorld.HeroH, w.Y[1]);  // її не зсунуло
    }

    [Fact]
    public void A_lever_flips_to_the_side_the_hero_leaves_on_and_ignores_standing_still()
    {
        var w = W(Flat, fire: [6, 9], water: [20, 9], tweak: f => f.Levers = [new VohnykLeverFile { Id = "l1", At = [8, 9], Init = 0 }]);
        while (w.X[0] < 8 * T) w.Step(R, 0);
        for (var s = 0; s < 20; s++) w.Step(0, 0);   // стоїть у клітинці важеля — нічого
        Assert.Equal(0, w.Lever[0]);
        for (var s = 0; s < 30; s++) w.Step(R, 0);
        Assert.Equal(1, w.Lever[0]);                 // вийшов праворуч
        for (var s = 0; s < 70; s++) w.Step(L, 0);
        Assert.Equal(0, w.Lever[0]);                 // назад крізь нього ліворуч
    }

    [Fact]
    public void A_lift_moves_three_pixels_per_step_and_carries_heroes_and_boxes()
    {
        var w = W(Flat, fire: [6, 9], water: [10, 9], tweak: f =>
        {
            f.Buttons = [Btn("b1", 6, 9)];
            f.Lifts = [new VohnykLiftFile { Id = "f1", At = [10, 10], W = 2, To = [10, 5], By = ["b1"] }];
            f.Boxes = [new VohnykAtFile { At = [11, 9] }];
        });
        var ly = w.LiftY[0];
        var hy = w.Y[1];
        var by = w.BoxY[0];
        w.Step(0, 0);
        Assert.Equal(ly - VohnykWorld.LiftSpeed, w.LiftY[0]);
        Assert.Equal(hy - VohnykWorld.LiftSpeed, w.Y[1]);
        Assert.Equal(by - VohnykWorld.LiftSpeed, w.BoxY[0]);
        for (var s = 0; s < 200; s++) w.Step(0, 0);
        Assert.Equal(5 * T, w.LiftY[0]);
        Assert.Equal(5 * T - VohnykWorld.HeroH, w.Y[1]);
        Assert.Equal(5 * T - VohnykWorld.BoxSize, w.BoxY[0]);
    }

    [Fact]
    public void A_rider_pressed_into_a_wall_stops_the_lift_for_that_step()
    {
        // стеля над шахтою в ряду 5: Крапля вдариться головою раніше, ніж ліфт доїде до ряду 3
        var w = W(Map(24, 12, (9, 12, 5, 5, '#')), fire: [6, 9], water: [10, 9], tweak: f =>
        {
            f.Buttons = [Btn("b1", 6, 9)];
            f.Lifts = [new VohnykLiftFile { Id = "f1", At = [10, 10], W = 2, To = [10, 3], By = ["b1"] }];
        });
        for (var s = 0; s < 200; s++) w.Step(0, 0);
        Assert.True(w.Y[1] >= 6 * T);                          // голова не в стелі
        Assert.Equal(w.Y[1] + VohnykWorld.HeroH, w.LiftY[0]);  // стоїть на ліфті
        Assert.True(w.LiftY[0] > 3 * T);                       // ліфт не доїхав
    }

    [Fact]
    public void Gems_are_picked_only_by_the_matching_hero_and_survive_death()
    {
        var w = W(Flat, fire: [2, 9], water: [3, 9], tweak: f => f.Gems =
        [
            new VohnykGemFile { Who = "water", At = [6, 9] },
            new VohnykGemFile { Who = "fire", At = [12, 9] },
        ]);
        for (var s = 0; s < 50; s++) w.Step(R, 0);   // Вогник пробіг крізь самоцвіт Краплі
        Assert.True(w.X[0] > 7 * T);
        Assert.Equal(0, w.Gems & 1);
        for (var s = 0; s < 60; s++) w.Step(R, 0);
        Assert.Equal(2, w.Gems & 2);
        w.Reset(keepGems: true);
        Assert.Equal(2, w.Gems);
        Assert.Equal(2 * T + 8 * VohnykWorld.Px, w.X[0]);
    }

    [Fact]
    public void Both_heroes_in_their_own_exits_for_ten_steps_clear_the_level_and_the_wrong_door_does_nothing()
    {
        VohnykWorld At(int[] fire, int[] water) => W(Flat, fire, water, f => f.Exits = new VohnykSpawnFile { Fire = [5, 8], Water = [9, 8] });
        var right = At([5, 9], [9, 9]);
        for (var s = 0; s < 9; s++) right.Step(0, 0);
        Assert.Equal(0, right.Cleared);
        Assert.Equal(9, right.Hold);
        right.Step(0, 0);
        Assert.Equal(1, right.Cleared);

        var swapped = At([9, 9], [5, 9]);
        for (var s = 0; s < 40; s++) swapped.Step(0, 0);
        Assert.Equal(0, swapped.Cleared);
        Assert.Equal(0, swapped.InExit[0]);
    }

    [Fact]
    public void Alone_a_button_of_an_all_gate_holds_for_two_seconds_after_stepping_off_but_not_in_a_duo()
    {
        foreach (var solo in new[] { false, true })
        {
            var w = W(Flat, fire: [2, 9], water: [4, 9], tweak: f =>
            {
                f.Buttons = [Btn("b1", 2, 9), Btn("b2", 4, 9), Btn("b3", 8, 9)];
                f.Doors = [Door("d1", 12, 8, 2, ["b1", "b2"], "all"), Door("d2", 16, 8, 2, ["b3"])];
            });
            w.Solo = solo;
            for (var i = 0; i < 20; i++) w.Step(0, 0);
            Assert.Equal(2 * T, w.DoorO[0]);                          // обоє на кнопках — брама відчинена
            var guard = 0;
            while (w.Button[0] == 1 && guard++ < 60) w.Step(L, 0);     // Вогник зійшов з b1
            w.Step(0, 0);
            if (!solo)
            {
                Assert.Equal(0, w.Button[0]);
                Assert.True(w.DoorO[0] < 2 * T);                       // удвох брама зачиняється одразу
                continue;
            }
            for (var i = 0; i < VohnykWorld.SoloLatch - 3; i++) w.Step(0, 0);
            Assert.NotEqual(0, w.Button[0]);                           // сам — кнопка ще тримається
            Assert.Equal(2 * T, w.DoorO[0]);
            for (var i = 0; i < 4; i++) w.Step(0, 0);
            Assert.Equal(0, w.Button[0]);                              // а за 2 с відпускається
            Assert.True(w.DoorO[0] < 2 * T);
            // кнопка звичайних дверей (any) не тримається й сам за двох
            guard = 0;
            while (w.Button[2] == 0 && guard++ < 200) w.Step(0, R);
            while (w.Button[2] != 0 && guard++ < 400) w.Step(0, R);
            Assert.True(guard < 400);
            Assert.Equal(0, w.Button[2]);
        }
    }

    [Fact]
    public void Triggers_seen_by_mechanisms_are_those_of_the_previous_step()
    {
        var w = W(Flat, fire: [4, 9], water: [15, 9], tweak: f =>
        {
            f.Buttons = [Btn("b1", 6, 9)];
            f.Doors = [Door("d1", 12, 8, 2, ["b1"])];
        });
        var s = 0;
        while (w.Button[0] == 0) { w.Step(R, 0); s++; Assert.True(s < 100); }
        Assert.Equal(0, w.DoorO[0]);      // крок, на якому натиснули: двері ще не бачать
        w.Step(0, 0);
        Assert.Equal(VohnykWorld.DoorSpeed, w.DoorO[0]);
    }

    [Fact]
    public void Save_and_load_round_trip_the_whole_world_and_the_hash_follows_it()
    {
        var level = VohnykLevels.Get(15);
        var w = new VohnykWorld(level);
        var run = level.Solution.OrderBy(e => e[0]).ToList();
        var k = new int[2];
        var p = 0;
        for (var s = 1; s <= 700; s++)
        {
            while (p < run.Count && run[p][0] <= s) { k[run[p][1]] = run[p][2]; p++; }
            w.Step(k[0], k[1]);
        }
        var snap = new int[w.StateLength];
        w.Save(snap);
        var h = w.Hash();
        var copy = new VohnykWorld(level);
        copy.Load(snap);
        Assert.Equal(h, copy.Hash());
        Assert.Equal(h, VohnykWorld.Hash(snap, snap.Length));
        w.Step(R, R);
        Assert.NotEqual(h, w.Hash());
    }

    // =============================================================================================
    // Партія за столом
    // =============================================================================================

    [Fact]
    public void The_table_opens_in_pick_phase_with_level_one_chosen_and_only_the_first_level_unlocked()
    {
        var h = Table(null, "Оля");
        var v = h.View(0);
        Assert.Equal("pick", v.GetProperty("phase").GetString());
        Assert.Equal(1, v.GetProperty("picked").GetInt32());
        var levels = v.GetProperty("levels");
        Assert.Equal(15, levels.GetArrayLength());
        Assert.True(levels[0].GetProperty("unlocked").GetBoolean());
        Assert.False(levels[1].GetProperty("unlocked").GetBoolean());
        Assert.Equal("Перші кроки", v.GetProperty("level").GetProperty("name").GetString());
        Assert.Null(h.Room.Game.CanStart());
    }

    [Fact]
    public void The_host_picks_a_level_and_a_guest_is_refused_with_a_reason()
    {
        var h = Table(Unlocked(4));
        Assert.True(h.Act(0, "pick", new { level = 3 }).Ok);
        Assert.Equal(3, h.View(1).GetProperty("picked").GetInt32());
        var no = h.Act(1, "pick", new { level = 2 });
        Assert.False(no.Ok);
        Assert.Equal("Рівень обирає господар столу", no.Message);
        Assert.Equal(3, h.View(0).GetProperty("picked").GetInt32());
        h.Start();
        h.Tick();
        Assert.Equal(3, G(h).LevelNo);
    }

    [Fact]
    public void A_locked_level_and_a_nonexistent_one_are_refused_and_nothing_changes()
    {
        var h = Table();
        var locked = h.Act(0, "pick", new { level = 2 });
        Assert.Equal("Рівень 2 ще зачинений: спершу пройдіть 1", locked.Message);
        Assert.Equal("Такого рівня нема", h.Act(0, "pick", new { level = 16 }).Message);
        Assert.Equal("Такого рівня нема", h.Act(0, "pick", new { level = "три" }).Message);
        Assert.Equal("Такого рівня нема", h.Act(0, "pick", null).Message);
        Assert.Equal(1, h.View(0).GetProperty("picked").GetInt32());
    }

    [Fact]
    public void The_default_pick_is_the_lowest_unlocked_level_the_host_has_not_cleared()
    {
        var store = Unlocked(4, "Оля");                 // Оля пройшла 1–3
        var h = Table(store, "Оля");
        Assert.Equal(4, h.View(0).GetProperty("picked").GetInt32());
        var all = Unlocked(16, "Оля");
        Assert.Equal(15, Table(all, "Оля").View(0).GetProperty("picked").GetInt32());
    }

    [Fact]
    public void Ready_lasts_a_hundred_steps_and_inputs_during_it_apply_from_the_first_go_step()
    {
        var h = Table();
        h.Start();
        var x0 = G(h).World!.X[0];
        h.Tick(30);
        In(h, 0, G(h).StepNo + 1, 0, R);                  // натиснув «праворуч» ще на відліку (за 0,8 с до старту)
        TickTo(h, Go0);
        Assert.Equal(Vohnyk.PhGo, G(h).PhaseNo);
        Assert.Equal(x0, G(h).World!.X[0]);               // світ стояв увесь відлік
        Assert.Equal(0, G(h).ClockSteps);
        h.Tick();
        Assert.Equal(x0 + 12 + 24, G(h).World!.X[0]);     // два кроки розгону з першого ж кроку go
        Assert.Equal(2, G(h).ClockSteps);
    }

    [Fact]
    public void Death_freezes_thirty_steps_then_resets_all_but_gems_and_the_clock_and_counts()
    {
        var h = Table(Unlocked(2));
        h.Act(0, "pick", new { level = 2 });
        h.Start();
        // Крапля водою по свій самоцвіт до зачинених дверей, Вогник трохи пізніше біжить у воду
        Play(h, [[1, 1, R], [40, 0, R]], until: Go0 + 45);
        var g = G(h);
        while (g.PhaseNo == Vohnyk.PhGo && g.StepNo < 600) h.Tick();
        Assert.Equal(Vohnyk.PhDead, g.PhaseNo);
        Assert.Equal(1, g.Deaths);
        Assert.Equal(Vohnyk.CauseFireWater, Frame(h).GetProperty("dc").GetInt32());
        Assert.Equal(1, g.World!.Gems & 1);                // самоцвіт Краплі вже зібрано
        var frozen = g.World.Hash();
        var died = g.StepNo;
        h.Tick(10);
        Assert.Equal(frozen, g.World.Hash());
        TickTo(h, died + Vohnyk.DeadSteps + 2);
        Assert.Equal(Vohnyk.PhGo, g.PhaseNo);
        Assert.Equal(0, g.World.Died[0]);                  // знову живий на старті
        Assert.Equal(1, g.World.Gems & 1);                 // самоцвіт лишився
        Assert.Equal(g.StepNo - Go0, g.ClockSteps);        // годинник ішов і під час смерті
    }

    [Fact]
    public void After_a_death_the_level_is_back_at_its_start()
    {
        var h = Table(Unlocked(2));
        h.Act(0, "pick", new { level = 2 });
        h.Start();
        TickTo(h, Go0);
        In(h, 0, Go0 + 1, 0, R);
        var g = G(h);
        while (g.PhaseNo == Vohnyk.PhGo) h.Tick();
        In(h, 0, g.StepNo + 1, 0, 0);     // відпустив клавішу, поки «вмирав»
        while (g.PhaseNo != Vohnyk.PhGo) h.Tick();
        var fresh = new VohnykWorld(VohnykLevels.Get(2));
        Assert.Equal(fresh.X[0], g.World!.X[0]);
        Assert.Equal(fresh.Y[0], g.World.Y[0]);
        Assert.Equal(fresh.X[1], g.World.X[1]);
        Assert.Equal(0, g.World.DoorO[0]);
    }

    /// <summary>На якому кроці гине герой, що біжить праворуч сам від старту рівня.</summary>
    static int DeathStep(VohnykLevel lv, int hero)
    {
        var w = new VohnykWorld(lv);
        for (var s = 1; s < 500; s++)
        {
            w.Step(hero == 0 ? R : 0, hero == 1 ? R : 0);
            if (w.Died[hero] != 0) return s;
        }
        throw new InvalidOperationException("не загинув");
    }

    [Fact]
    public void Two_deaths_in_one_step_count_once()
    {
        var lv = VohnykLevels.Get(3);
        var df = DeathStep(lv, 0);
        var dw = DeathStep(lv, 1);
        var h = Table(Unlocked(3));
        h.Act(0, "pick", new { level = 3 });
        h.Start();
        Play(h, [[1 + Math.Max(0, dw - df), 0, R], [1 + Math.Max(0, df - dw), 1, R]], until: Go0 + 3 + Math.Abs(df - dw));
        var g = G(h);
        while (g.PhaseNo != Vohnyk.PhDead && g.StepNo < 800) h.Tick();
        Assert.Equal(1, g.Deaths);
        Assert.Equal(1, g.World!.Died[0]);
        Assert.Equal(1, g.World.Died[1]);
        Assert.Equal(Vohnyk.CauseBoth, Frame(h).GetProperty("dc").GetInt32());
        Assert.True(h.Act(0, "giveup").Ok);                          // підсумок каже, хто скільки разів
        var by = h.View(null).GetProperty("result").GetProperty("deathsBy");
        Assert.Equal(1, by[0].GetInt32());
        Assert.Equal(1, by[1].GetInt32());
    }

    [Fact]
    public void Clearing_finishes_as_a_draw_with_scores_a_journal_line_and_no_winners()
    {
        var h = Table();
        h.Start();
        Play(h, VohnykLevels.Get(1).Solution);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var result = h.Room.Result!;
        Assert.Empty(result.Winners);
        Assert.True(result.Draw);
        var ms = VohnykLevels.Get(1).Check!.Steps * Vohnyk.StepMs;
        Assert.Equal(ms, result.Scores![0]);
        Assert.Equal(ms, result.Scores[1]);
        var line = h.Outbox.OfType<Journal>().Last().Text;
        Assert.Equal("Вогник і Крапля: Оля і Петро пройшли рівень 1 «Перші кроки» за 0:03 ★★★", line);
        var v = h.View(null);
        Assert.Equal("over", v.GetProperty("phase").GetString());
        var r = v.GetProperty("result");
        Assert.True(r.GetProperty("cleared").GetBoolean());
        Assert.Equal(3, r.GetProperty("stars").GetInt32());
        Assert.Equal(2, r.GetProperty("next").GetInt32());
    }

    [Fact]
    public void Stars_are_one_for_clearing_plus_all_gems_plus_par()
    {
        // ті самі ходи, але пів хвилини спершу стояли: усі самоцвіти, а час уже за par (25 с)
        var h = Table();
        h.Start();
        var late = VohnykLevels.Get(1).Solution.Select(e => new[] { e[0] + 1400, e[1], e[2] }).ToArray();
        Play(h, late);
        var r = h.View(0).GetProperty("result");
        Assert.Equal(2, r.GetProperty("stars").GetInt32());
        Assert.Equal(2, G(h).StoreService.Stars("оля", 1));

        // без самоцвітів — одна зірка: Крапля не бере свій (стрибає через калюжу), Вогник обходить свій
        var solo = new VohnykStore(null);
        solo.Record(["Оля"], 1, 3000, 0, 1, DateTimeOffset.UnixEpoch);
        Assert.Equal(1, solo.Stars("оля", 1));
        solo.Record(["Оля"], 1, 3000, 0, 3, DateTimeOffset.UnixEpoch);
        solo.Record(["Оля"], 1, 3000, 0, 2, DateTimeOffset.UnixEpoch);
        Assert.Equal(3, solo.Stars("оля", 1));   // зберігається максимум
    }

    [Fact]
    public void Rematch_after_a_clear_starts_the_next_level_after_a_give_up_the_same_one_and_fifteen_stays_fifteen()
    {
        var h = Table();
        h.Start();
        Play(h, VohnykLevels.Get(1).Solution);
        Assert.True(h.Rematch().Ok);
        h.Tick();
        Assert.Equal(2, G(h).LevelNo);
        TickTo(h, Go0 + 4);
        Assert.True(h.Act(0, "giveup").Ok);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(h.Rematch().Ok);
        h.Tick();
        Assert.Equal(2, G(h).LevelNo);

        var last = Table(Unlocked(15, "Оля", "Петро"));
        last.Act(0, "pick", new { level = 15 });
        last.Start();
        Play(last, VohnykLevels.Get(15).Solution);
        Assert.Equal(RoomStatus.Finished, last.Room.Status);
        Assert.Equal(15, last.View(0).GetProperty("result").GetProperty("next").GetInt32());
        last.Rematch();
        last.Tick();
        Assert.Equal(15, G(last).LevelNo);
    }

    [Fact]
    public void Rematch_swaps_heroes_with_the_seats()
    {
        var h = Table();
        h.Start();
        Play(h, VohnykLevels.Get(1).Solution);
        h.Rematch();
        Assert.Equal("Петро", h.NickOf(0));
        Assert.Equal("Оля", h.NickOf(1));
        Assert.Equal("Вогник", h.Room.Game.SeatName(0));
    }

    [Fact]
    public void Alone_the_host_drives_both_heroes_and_the_c_field_switches_the_active_one()
    {
        var h = Table(null, "Оля");
        Assert.True(h.Start().Ok);
        var g = G(h);
        Assert.True(g.SoloMode);
        Assert.True(h.View(0).GetProperty("solo").GetBoolean());
        TickTo(h, Go0);
        In(h, 0, g.StepNo + 1, 1, R);
        Assert.Equal(1, g.ActiveHero);
        var water = g.World!.X[1];
        h.Tick(5);
        Assert.True(g.World.X[1] > water);
        In(h, 0, g.StepNo + 1, 1, 0);
        In(h, 0, g.StepNo + 1, 0, R);
        Assert.Equal(0, g.ActiveHero);
        Assert.Equal(0, Frame(h).GetProperty("a").GetInt32());

        // сам за двох проходить рівень записаним журналом
        var solo = Table(null, "Оля");
        solo.Start();
        Play(solo, VohnykLevels.Get(1).Solution, soloSeat: 0);
        Assert.Equal(RoomStatus.Finished, solo.Room.Status);
        Assert.Equal("Вогник і Крапля: Оля за двох — рівень 1 «Перші кроки» пройдено за 0:03 ★★★", solo.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void In_a_duo_input_for_the_partners_hero_is_ignored()
    {
        var h = Table();
        h.Start();
        TickTo(h, Go0);
        var g = G(h);
        In(h, 0, g.StepNo + 1, 1, R);      // Оля — Вогник — шле за Краплю
        h.Tick(10);
        Assert.Equal(0, g.KeysAt(1, g.StepNo));
        Assert.Equal(new VohnykWorld(VohnykLevels.Get(1)).X[1], g.World!.X[1]);
        lock (h.Room.Sync)
            Assert.Equal("Це не твій герой", Assert.Throws<GameError>(() => g.Act(0, "in", Views.Payload(new { n = 1, c = 1, k = 2 }))).Message);
    }

    [Fact]
    public void A_partner_leaving_mid_level_hands_both_heroes_to_the_other_and_the_match_goes_on()
    {
        var h = Table();
        h.Start();
        TickTo(h, Go0);
        In(h, 1, G(h).StepNo + 1, 1, R);          // Петро тримав «праворуч»
        h.Tick(3);
        var views = h.Outbox.OfType<RoomViews>().Count();
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        var g = G(h);
        Assert.True(g.SoloMode);
        Assert.Equal(0, g.ActiveHero);
        Assert.Contains(h.Outbox.OfType<Journal>(), j => j.Text == "Вогник і Крапля: Петро встав з-за столу — Оля веде обох");
        Assert.True(h.Outbox.OfType<RoomViews>().Count() > views);
        Assert.True(h.View(0).GetProperty("solo").GetBoolean());
        h.Tick(5);
        Assert.Equal(0, g.KeysAt(1, g.StepNo));    // Крапля відпустила клавіші
        In(h, 0, g.StepNo + 1, 1, L);              // тепер Оля веде й Краплю
        Assert.Equal(1, g.ActiveHero);
        h.Tick(2);
        Assert.Equal(L, g.KeysAt(1, g.StepNo));
    }

    [Fact]
    public void The_last_player_leaving_ends_the_match_unfinished()
    {
        var h = Table(null, "Оля");
        h.Start();
        h.Tick(60);
        h.Leave("Оля");
        Assert.Contains(h.Finished, f => f.Result.Text == "Вогник і Крапля: Оля встав з-за столу, партію не дограли");
    }

    [Fact]
    public void Reset_counts_as_a_death_and_give_up_ends_without_a_clear()
    {
        var h = Table();
        h.Start();
        TickTo(h, Go0 + 2);
        Assert.True(h.Act(1, "reset").Ok);
        var g = G(h);
        Assert.Equal(Vohnyk.PhDead, g.PhaseNo);
        Assert.Equal(1, g.Deaths);
        h.Tick();
        Assert.Equal(Vohnyk.CauseReset, Frame(h).GetProperty("dc").GetInt32());
        Assert.True(h.Act(0, "giveup").Ok);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var r = h.View(null).GetProperty("result");
        Assert.False(r.GetProperty("cleared").GetBoolean());
        Assert.Equal(0, r.GetProperty("stars").GetInt32());
        Assert.Equal("Вогник і Крапля: Оля і Петро здались на рівні 1 «Перші кроки»", h.Room.Result!.Text);
        Assert.Equal(0, g.StoreService.Stars("оля", 1));
        Assert.Equal(0, r.GetProperty("deathsBy")[0].GetInt32() + r.GetProperty("deathsBy")[1].GetInt32());   // «заново» — нічия смерть
        Assert.Equal(1, r.GetProperty("deaths").GetInt32());
    }

    [Fact]
    public void Acts_outside_their_phase_are_refused_with_the_documented_texts()
    {
        var h = Table();
        Assert.Equal("Зараз не можна", h.Act(0, "reset").Message);    // лобі
        Assert.Equal("Зараз не можна", h.Act(0, "giveup").Message);
        Assert.Equal("Тут так не ходять", h.Act(0, "dance").Message);
        h.Start();
        Assert.Equal("Зараз не можна", h.Act(0, "reset").Message);    // відлік
        Assert.True(h.Act(1, "pick", new { level = 1 }).Ok);             // рівень на відліку ще можна змінити (тут — той самий)
        Assert.True(h.Act(0, "giveup").Ok);                              // здатись можна й на відліку
        var go = Table();
        go.Start();
        TickTo(go, Go0 + 2);
        Assert.Equal("Партія вже йде", go.Act(0, "pick", new { level = 1 }).Message);
        Assert.Equal(1, G(go).GameNo);                                   // і нічого не перезапустилось
    }

    [Fact]
    public void A_reopened_table_goes_back_to_the_level_map_with_the_next_level_picked()
    {
        var h = Table();
        h.Start();
        Play(h, VohnykLevels.Get(1).Solution);
        h.Leave("Петро");
        h.Join("Ганна");                                  // вільне місце відкриває стіл наново
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);
        var v = h.View(0);
        Assert.Equal("pick", v.GetProperty("phase").GetString());                 // лобі, а не підсумок минулої партії
        Assert.Equal(JsonValueKind.Null, v.GetProperty("result").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("f").ValueKind);
        Assert.Equal(2, v.GetProperty("picked").GetInt32());
        Assert.Equal(2, v.GetProperty("level").GetProperty("n").GetInt32());      // прев'ю — обраний рівень
        Assert.True(h.Act(0, "pick", new { level = 1 }).Ok);
        v = h.View(1);
        Assert.Equal("pick", v.GetProperty("phase").GetString());
        Assert.Equal(1, v.GetProperty("picked").GetInt32());
        Assert.Equal(1, v.GetProperty("level").GetProperty("n").GetInt32());
        Assert.True(h.Start().Ok);
        h.Tick();
        Assert.Equal(1, G(h).LevelNo);
        Assert.False(G(h).SoloMode);
    }

    [Fact]
    public void Standing_up_on_together_still_credits_both_with_the_pairs_record()
    {
        // «Разом!» уже грає — рівень пройдено; Петро тисне «Встати»: зараховуємо зараз, обом
        var store = new VohnykStore(null);
        var h2 = Table(store);
        h2.Start();
        var g2 = G(h2);
        Play(h2, VohnykLevels.Get(1).Solution, stop: () => g2.PhaseNo == Vohnyk.PhClear);
        Assert.Equal(Vohnyk.PhClear, g2.PhaseNo);
        h2.Leave("Петро");
        Assert.Equal(RoomStatus.Finished, h2.Room.Status);
        Assert.Equal(3, store.Stars("петро", 1));
        Assert.Equal(3, store.Stars("оля", 1));
        Assert.NotNull(store.Best("оля+петро", 1));
        Assert.Null(store.Best("оля", 1));                                        // не соло-рекорд Олі
        Assert.Contains(h2.Awards, a => a.Nick == "Петро" && a.Reason == "ach:vohnyk-duo");
        Assert.Equal("Вогник і Крапля: Оля і Петро пройшли рівень 1 «Перші кроки» за 0:03 ★★★", h2.Room.Result!.Text);
    }

    [Fact]
    public void A_crew_that_changed_mid_level_gets_the_stars_but_no_record()
    {
        var store = new VohnykStore(null);
        var h = Table(store);
        h.Start();
        TickTo(h, Go0 + 10);
        h.Leave("Петро");                                                           // Оля догравує за двох
        Play(h, VohnykLevels.Get(1).Solution.Select(e => new[] { e[0] + 10, e[1], e[2] }).ToArray(), soloSeat: 0);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(store.Stars("оля", 1) > 0);
        Assert.Equal(0, store.Stars("петро", 1));
        Assert.Null(store.Best("оля", 1));                                          // час пари — не соло-рекорд
        Assert.Null(store.Best("оля+петро", 1));                                    // і не рекорд пари: догравала сама
        Assert.Empty(store.Top(1, 10));
    }

    [Fact]
    public void A_vanished_players_held_key_is_released_at_the_first_reset_instead_of_looping_deaths()
    {
        // Оля (Вогник) тримає «праворуч» і пропадає: Вогник біжить у воду. Без відпускання — смерть за смертю 20 с grace.
        var h = Table();
        h.Start();
        TickTo(h, Go0);
        In(h, 0, Go0 + 1, 0, R);
        var g = G(h);
        TickTo(h, Go0 + 1000);
        Assert.Equal(1, g.Deaths);                                                  // одна смерть, а не п'ятнадцять
        Assert.Equal(0, g.KeysAt(0, g.StepNo));
        Assert.Equal(Vohnyk.PhGo, g.PhaseNo);
        Assert.True(g.World!.X[0] < 4 * T, $"x {g.World.X[0]}");                    // стоїть біля старту, до води не дійшов
    }

    [Fact]
    public void A_held_key_the_client_keeps_reminding_about_survives_the_reset()
    {
        // живий клієнт, поки тримає клавішу, нагадує про неї раз на 25 кроків — і сервер її не відпускає
        var h = Table();
        h.Start();
        TickTo(h, Go0);
        In(h, 0, Go0 + 1, 0, R);
        var g = G(h);
        while (g.Deaths < 2 && g.StepNo < Go0 + 1000)
        {
            if (g.StepNo % 25 < Vohnyk.StepsPerTick) In(h, 0, g.StepNo + 3, 0, R);
            h.Tick();
        }
        Assert.Equal(2, g.Deaths);                                                  // побіг у воду й удруге — він же тримає
        Assert.Equal(R, g.KeysAt(0, g.StepNo));
    }

    [Fact]
    public void After_a_game_a_seated_player_switches_the_level_during_the_countdown_of_the_next()
    {
        var h = Table();
        h.Start();
        Play(h, VohnykLevels.Get(1).Solution);
        var gi = G(h).GameNo;
        Assert.True(h.Rematch().Ok);                                                 // «Ще раз» — наступний, 2-й
        h.Tick(3);
        Assert.Equal(2, G(h).LevelNo);
        Assert.Equal(gi + 1, Frame(h).GetProperty("gi").GetInt32());
        var views = h.Outbox.OfType<RoomViews>().Count();
        var guest = h.Room.SeatOf("Петро")!.Value;
        Assert.Equal("Рівень 3 ще зачинений: спершу пройдіть 2", h.Act(guest, "pick", new { level = 3 }).Message);
        Assert.True(h.Act(guest, "pick", new { level = 1 }).Ok);                     // «Ще раз цей» — будь-хто з сидячих
        var g = G(h);
        Assert.Equal(1, g.LevelNo);
        Assert.Equal(Vohnyk.PhReady, g.PhaseNo);
        Assert.True(g.StepNo < 3);                                                   // відлік з нуля
        h.Tick();
        Assert.True(h.Outbox.OfType<RoomViews>().Count() > views);                   // вид із новим рівнем — з тика
        var v = h.View(null);
        Assert.Equal(1, v.GetProperty("level").GetProperty("n").GetInt32());
        Assert.Equal(gi + 2, v.GetProperty("gi").GetInt32());
        Assert.Equal(gi + 2, Frame(h).GetProperty("gi").GetInt32());
        TickTo(h, Go0 + 2);
        Assert.Equal("Партія вже йде", h.Act(0, "pick", new { level = 2 }).Message);
    }

    [Fact]
    public void Steps_follow_the_real_clock_not_the_tick_count()
    {
        // таймер Windows тикає раз на ~48 мс замість 40 — гра однаково йде 50 кроків за секунду
        var h = Table();
        h.Start();
        for (var i = 0; i < 25; i++)
        {
            h.Clock.AdvanceMs(8);
            h.Tick();                                                                // + 40 мс
        }
        Assert.Equal(60, G(h).StepNo);                                              // 25 × 48 мс = 1,2 с
        h.Clock.AdvanceMs(3000);                                                     // сервер завмер на 3 с
        h.Tick();
        Assert.Equal(60 + Vohnyk.MaxCatchUp, G(h).StepNo);                          // не надолужує все махом
        h.Tick();
        Assert.Equal(60 + Vohnyk.MaxCatchUp + 2, G(h).StepNo);                      // і далі — рівно
    }

    [Fact]
    public void A_level_you_passed_yourself_is_never_shown_locked()
    {
        // Тарас пройшов 2-й і 3-й гостем у ветерана, а 1-го — ні
        var store = new VohnykStore(null);
        store.Record(["Тарас", "Ганна"], 2, 30000, 0, 3, DateTimeOffset.UnixEpoch);
        store.Record(["Тарас", "Ганна"], 3, 30000, 0, 3, DateTimeOffset.UnixEpoch);
        Assert.True(store.Unlocked(["тарас"], 2));
        Assert.True(store.Unlocked(["тарас"], 4));
        Assert.False(store.Unlocked(["тарас"], 5));
        var h = Table(store, "Тарас");
        Assert.True(h.Act(0, "pick", new { level = 2 }).Ok);
        Assert.True(h.View(0).GetProperty("levels")[1].GetProperty("unlocked").GetBoolean());
    }

    // =============================================================================================
    // Мережа: журнал вводу й перемотування
    // =============================================================================================

    [Fact]
    public void A_late_input_rewinds_and_replays_to_exactly_the_state_of_an_on_time_input()
    {
        var onTime = Table();
        var late = Table();
        foreach (var h in new[] { onTime, late }) { h.Start(); TickTo(h, Go0 + 20); }
        In(onTime, 0, Go0 + 21, 0, R | J);
        TickTo(onTime, Go0 + 40);
        TickTo(late, Go0 + 30);
        In(late, 0, Go0 + 21, 0, R | J);                 // той самий натиск, але дійшов на 10 кроків пізно
        TickTo(late, Go0 + 40);
        Assert.Equal(G(onTime).World!.Hash(), G(late).World!.Hash());
        Assert.Equal(Frame(onTime).GetProperty("h").GetInt32(), Frame(late).GetProperty("h").GetInt32());
    }

    [Fact]
    public void Inputs_older_than_the_window_are_clamped_and_newer_ones_wait_for_their_step()
    {
        var h = Table();
        h.Start();
        TickTo(h, Go0 + 50);
        var g = G(h);
        var s = g.StepNo;
        In(h, 0, 3, 0, R);                                 // стародавній — лягає на S−24
        Assert.Equal(R, g.KeysAt(0, s - Vohnyk.Rewind + 1));
        Assert.Equal(0, g.KeysAt(0, s - Vohnyk.Rewind));
        In(h, 0, s + 500, 0, L);                           // із далекого майбутнього — на S+6
        Assert.Equal(R, g.KeysAt(0, s + Vohnyk.Future - 1));
        Assert.Equal(L, g.KeysAt(0, s + Vohnyk.Future));
    }

    [Fact]
    public void Duplicate_and_malformed_inputs_change_nothing()
    {
        var h = Table();
        h.Start();
        TickTo(h, Go0 + 10);
        var g = G(h);
        In(h, 0, g.StepNo - 3, 0, R);
        var hash = g.World!.Hash();
        var keys = g.KeysAt(0, g.StepNo);
        In(h, 0, g.StepNo - 3, 0, R);                      // дубль
        h.Input(0, "in", null);
        h.Input(0, "in", new { n = "x", c = 0, k = 2 });
        h.Input(0, "in", new { n = 1, c = 2, k = 2 });
        h.Input(0, "in", new { n = 1, c = 0, k = 9 });
        h.Input(0, "in", new { n = 1, c = 0 });
        h.Input(0, "in", 7);
        Assert.Equal(hash, g.World.Hash());
        Assert.Equal(keys, g.KeysAt(0, g.StepNo));
        lock (h.Room.Sync)
            Assert.Equal("Кривий ввід", Assert.Throws<GameError>(() => g.Act(0, "in", Views.Payload(new { n = 1, c = 0, k = 8 }))).Message);
    }

    [Fact]
    public void A_probe_into_the_past_never_collects_a_gem_the_hero_did_not_reach()
    {
        // «Зонд» модифікованого клієнта (рецензія): Крапля стоїть на старті, клієнт шле «праворуч» на 24 кроки в минуле —
        // у повторі вона добігає до самоцвіта; тут же шле «нічого» на той самий крок — у повторі стоїть. Самоцвіт — за
        // останньою гілкою: його нема, бо героїня туди так і не ходила.
        var h = Table();
        h.Start();
        TickTo(h, Go0 + 40);
        var g = G(h);
        var spawn = g.World!.X[1];
        var s = g.StepNo;
        In(h, 1, s - 24, 1, R);
        Assert.Equal(1, g.World.Gems & 1);                 // гілка «біжить» — самоцвіт у руках
        In(h, 1, s - 24, 1, 0);
        Assert.Equal(spawn, g.World.X[1]);                 // гілка «стоїть» — на старті
        Assert.Equal(0, g.World.Gems & 1);                 // і самоцвіта нема
        h.Tick(30);
        Assert.Equal(0, g.World.Gems & 1);
        Assert.Equal(0, Frame(h).GetProperty("w")[g.World.StateLength - 3].GetInt32() & 1);

        // чесна гра: побігла вчасно — самоцвіт її, і пізній ввід партнера цього не міняє
        var fair = Table();
        fair.Start();
        TickTo(fair, Go0);
        In(fair, 1, Go0 + 1, 1, R);
        var gf = G(fair);
        while ((gf.World!.Gems & 1) == 0) fair.Tick();
        In(fair, 0, gf.StepNo - 10, 0, L);
        Assert.Equal(1, gf.World.Gems & 1);
    }

    [Fact]
    public void A_hero_input_never_rewrites_its_own_past_before_the_last_one_sent()
    {
        var h = Table();
        h.Start();
        TickTo(h, Go0 + 60);
        var g = G(h);
        var s = g.StepNo;
        In(h, 0, s - 5, 0, R);
        In(h, 0, s - 20, 0, L);                            // раніше за вже надіслане — лягає не раніше за нього
        Assert.Equal(0, g.KeysAt(0, s - 20));
        Assert.Equal(0, g.KeysAt(0, s - 6));
        Assert.Equal(L, g.KeysAt(0, s - 5));
        In(h, 1, s - 20, 1, R);                            // межа — своя в кожного героя
        Assert.Equal(R, g.KeysAt(1, s - 20));
    }

    [Fact]
    public void A_replayed_death_lands_on_the_current_step()
    {
        // Вогник стрибав через воду вчасно; пізній ввід «без стрибка» — у повторі він падає у воду, а смерть — зараз
        var d = Table();
        d.Start();
        Play(d, [[1, 0, R], [31, 0, R | J]], until: Go0 + 40);
        var gd = G(d);
        Assert.Equal(Vohnyk.PhGo, gd.PhaseNo);
        var now = gd.StepNo;
        In(d, 0, Go0 + 31, 0, R);
        Assert.Equal(Vohnyk.PhDead, gd.PhaseNo);
        Assert.Equal(1, gd.Deaths);
        Assert.Equal(now, gd.StepNo);
        Assert.Equal(Vohnyk.DeadSteps, Frame(d).GetProperty("pt").GetInt32());
    }

    [Fact]
    public void Acks_report_the_last_applied_input_step_per_hero()
    {
        var h = Table();
        h.Start();
        TickTo(h, Go0 + 10);
        var g = G(h);
        var s = g.StepNo;
        In(h, 0, s - 2, 0, R);
        In(h, 1, s + 3, 1, L);
        var ack = Frame(h).GetProperty("ack");
        Assert.Equal(s - 2, ack[0].GetInt32());
        Assert.Equal(0, ack[1].GetInt32());                 // її крок ще не настав
        TickTo(h, s + 3);
        Assert.Equal(s + 3, Frame(h).GetProperty("ack")[1].GetInt32());
    }

    [Fact]
    public void The_frame_hash_matches_a_fresh_world_fed_the_same_inputs()
    {
        var h = Table();
        h.Start();
        TickTo(h, Go0 + 4);
        In(h, 0, Go0 + 5, 0, L);          // Вогник — до стіни (праворуч у нього вода)
        In(h, 1, Go0 + 9, 1, R | J);      // Крапля — стрибком праворуч
        TickTo(h, Go0 + 60);
        Assert.Equal(Vohnyk.PhGo, G(h).PhaseNo);
        var steps = G(h).StepNo - Go0;
        var w = new VohnykWorld(VohnykLevels.Get(1));
        for (var s = 1; s <= steps; s++) w.Step(s >= 5 ? L : 0, s >= 9 ? R | J : 0);
        Assert.Equal(w.Hash(), (uint)Frame(h).GetProperty("h").GetInt32());
        var state = Frame(h).GetProperty("w");
        Assert.Equal(w.StateLength, state.GetArrayLength());
        Assert.Equal(w.X[0], state[0].GetInt32());
    }

    // =============================================================================================
    // Види, кадри, каталог
    // =============================================================================================

    [Fact]
    public void Views_for_both_seats_and_the_watcher_are_identical_and_carry_the_static_level()
    {
        var h = Table();
        h.Start();
        h.Tick(60);
        var a = Views.Text(h.Room.Game.View(0));
        Assert.Equal(a, Views.Text(h.Room.Game.View(1)));
        Assert.Equal(a, Views.Text(h.Room.Game.View(null)));
        var v = h.View(null);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("turn").ValueKind);
        var level = v.GetProperty("level");
        Assert.Equal(14, level.GetProperty("rows").GetArrayLength());
        Assert.Equal(24, level.GetProperty("w").GetInt32());
        Assert.Equal("fire", level.GetProperty("gems")[1].GetProperty("who").GetString());
        Assert.Equal(1, level.GetProperty("spawn").GetProperty("fire")[0].GetInt32());
        Assert.Equal(3, level.GetProperty("hints").GetArrayLength());
        foreach (var name in new[] { "phase", "solo", "active", "picked", "levels", "run", "result", "f" })
            Assert.True(v.TryGetProperty(name, out _), name);
        Assert.Equal(JsonValueKind.Object, v.GetProperty("f").ValueKind);
        var l2 = VohnykLevels.Get(2);
        Assert.Equal(l2.Doors.Length, Static(2).GetProperty("doors").GetArrayLength());
        Assert.Equal("any", Static(2).GetProperty("doors")[0].GetProperty("mode").GetString());
    }

    static JsonElement Static(int n)
    {
        var h = Table(Unlocked(n), "Оля");
        h.Act(0, "pick", new { level = n });
        return h.View(0).GetProperty("level");
    }

    [Fact]
    public void The_frame_is_flat_int_arrays_under_seven_hundred_bytes_on_the_busiest_level()
    {
        var store = Unlocked(16, "Оля", "Петро");
        var biggest = 0;
        for (var n = 1; n <= VohnykLevels.Count; n++)
        {
            var h = Table(store);
            h.Act(0, "pick", new { level = n });
            h.Start();
            TickTo(h, Go0 + 10);
            In(h, 0, G(h).StepNo + 1, 0, R | J);
            In(h, 1, G(h).StepNo + 1, 1, L | J);
            h.Tick(3);
            var text = Views.Text(h.Room.Game.Frame());
            var f = Frame(h);
            foreach (var p in f.EnumerateObject())
                Assert.True(p.Value.ValueKind is JsonValueKind.Number or JsonValueKind.Array, p.Name);
            foreach (var x in f.GetProperty("w").EnumerateArray()) Assert.Equal(JsonValueKind.Number, x.ValueKind);
            biggest = Math.Max(biggest, text.Length);
        }
        output.WriteLine($"найбільший кадр: {biggest} Б");
        Assert.True(biggest < 700, $"кадр {biggest} Б");
    }

    [Fact]
    public void Idle_ticks_send_no_frame_except_a_keepalive_every_second()
    {
        var h = Table();
        h.Start();
        h.Tick(60);
        var before = h.Outbox.OfType<RoomFrame>().Count();
        h.Tick(50);                                          // усі стоять — нічого не рухається
        Assert.Equal(2, h.Outbox.OfType<RoomFrame>().Count() - before);
        In(h, 0, G(h).StepNo + 1, 0, R);
        before = h.Outbox.OfType<RoomFrame>().Count();
        h.Tick(5);
        Assert.Equal(5, h.Outbox.OfType<RoomFrame>().Count() - before);
    }

    [Fact]
    public void The_catalog_lists_vohnyk_as_live_by_host_one_to_two_players_with_css()
    {
        var c = RoomHarness.NewRegistry().Catalog.Single(g => g.Id == "vohnyk");
        Assert.Equal("Вогник і Крапля", c.Title);
        Assert.Equal("live", c.Group);
        Assert.Equal("byHost", c.Start);
        Assert.Equal((1, 2), (c.MinPlayers, c.MaxPlayers));
        Assert.Equal(40, c.TickMs);
        Assert.True(c.HasCss);
        Assert.False(c.Rated);
        Assert.Empty(c.Options);
        Assert.True(File.Exists(Paths.Resolve("web/games/vohnyk.js")));
        Assert.True(File.Exists(Paths.Resolve("web/games/vohnyk-sim.js")));
    }

    [Fact]
    public void The_server_accepts_exactly_the_payloads_the_module_sends()
    {
        var js = File.ReadAllText(Paths.Resolve("web/games/vohnyk.js"));
        Assert.Matches(new Regex(@"input\('in', \{ n: [^,]+, c: [^,]+, k: [^}]+\}\)"), js);
        Assert.Matches(new Regex(@"act\('pick', \{ level: [^}]+\}\)"), js);
        Assert.Contains("act('reset')", js);
        Assert.Contains("act('giveup')", js);

        var h = Table(Unlocked(3));
        Assert.True(h.Act(0, "pick", JsonSerializer.Deserialize<JsonElement>("{\"level\":2}")).Ok);
        h.Start();
        TickTo(h, Go0);
        h.Input(0, "in", JsonSerializer.Deserialize<JsonElement>("{\"n\":" + (Go0 + 3) + ",\"c\":0,\"k\":6}"));
        Assert.Equal(6, G(h).KeysAt(0, Go0 + 3));
        Assert.True(h.Act(0, "reset", null).Ok);
        Assert.True(h.Act(0, "giveup", null).Ok);
    }

    // =============================================================================================
    // Сховище й ачівки
    // =============================================================================================

    [Fact]
    public void Clearing_records_done_for_every_seated_nick_and_the_pairs_best_time()
    {
        var store = new VohnykStore(null);
        var h = Table(store);
        h.Start();
        Play(h, VohnykLevels.Get(1).Solution);
        Assert.Equal(3, store.Stars("оля", 1));
        Assert.Equal(3, store.Stars("петро", 1));
        var best = store.Best(VohnykStore.PairKey(["Петро", "Оля"]), 1)!;
        Assert.Equal("оля+петро", best.PairKey);
        Assert.Equal("Оля і Петро", best.Nicks);
        Assert.Equal(VohnykLevels.Get(1).Check!.Steps * 20, best.Ms);
        Assert.False(best.Solo);
        Assert.True(store.Unlocked(["оля"], 2));
        var best2 = h.View(0).GetProperty("levels")[0].GetProperty("best");
        Assert.Equal(best.Ms, best2.GetProperty("ms").GetInt32());
    }

    [Fact]
    public void A_slower_run_keeps_the_pairs_best_and_a_faster_one_replaces_it()
    {
        var store = new VohnykStore(null);
        var at = DateTimeOffset.UnixEpoch;
        store.Record(["Оля", "Петро"], 3, 50000, 2, 2, at);
        store.Record(["Петро", "Оля"], 3, 70000, 0, 3, at);
        Assert.Equal(50000, store.Best("оля+петро", 3)!.Ms);
        store.Record(["Оля", "Петро"], 3, 41000, 1, 3, at);
        Assert.Equal(41000, store.Best("оля+петро", 3)!.Ms);
        store.Record(["Оля"], 3, 30000, 0, 3, at);
        Assert.Equal(["Оля", "Оля і Петро"], store.Top(3, 10).Select(b => b.Nicks));
        Assert.True(store.Top(3, 10)[0].Solo);
    }

    [Fact]
    public void Unlocking_follows_any_seated_nick_so_a_veteran_carries_a_newcomer()
    {
        var store = Unlocked(6, "Оля");
        var h = Table(store, "Петро", "Оля");            // господар — новачок Петро
        Assert.True(h.Act(0, "pick", new { level = 6 }).Ok);
        Assert.False(h.Act(0, "pick", new { level = 7 }).Ok);
        var levels = h.View(1).GetProperty("levels");
        Assert.True(levels[5].GetProperty("unlocked").GetBoolean());
        Assert.Equal(0, levels[4].GetProperty("stars")[0].GetInt32());   // Петро — нічого
        Assert.Equal(1, levels[4].GetProperty("stars")[1].GetInt32());   // Оля — зірка
        var alone = Table(store, "Петро");
        Assert.False(alone.Act(0, "pick", new { level = 2 }).Ok);
    }

    [Fact]
    public void Without_a_database_the_store_works_in_memory_and_the_game_still_plays()
    {
        var store = new VohnykStore(null);
        store.Record(["Оля"], 1, 5000, 0, 3, DateTimeOffset.UnixEpoch);
        Assert.Equal(0, store.Pending);
        Assert.Equal(3, store.Stars("оля", 1));
        store.Flush();

        // сервісу взагалі нема — гра бере свій, у пам'яті
        var h = new RoomHarness("vohnyk");
        h.Join("Оля");
        h.Start();
        Play(h, VohnykLevels.Get(1).Solution, soloSeat: 0);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
    }

    [Fact]
    public void Duo_achievement_is_asked_on_a_two_nick_clear_and_cave_after_fifteen_three_star_levels()
    {
        var store = new VohnykStore(null);
        for (var n = 2; n <= 15; n++) store.Record(["Оля"], n, 1000, 0, 3, DateTimeOffset.UnixEpoch);
        var h = Table(store);
        h.Start();
        Play(h, VohnykLevels.Get(1).Solution);
        var got = h.Awards.Select(a => (a.Nick, a.Reason, a.Shards)).ToList();
        Assert.Contains(("Оля", "ach:vohnyk-duo", 0), got);
        Assert.Contains(("Петро", "ach:vohnyk-duo", 0), got);
        Assert.Contains(("Оля", "ach:vohnyk-cave", 0), got);
        Assert.DoesNotContain(("Петро", "ach:vohnyk-cave", 0), got);
        Assert.Contains(Hlechyky.Games.Economy.AchievementCatalog.All, a => a.Key == "vohnyk-duo");
        Assert.Contains(Hlechyky.Games.Economy.AchievementCatalog.All, a => a.Key == "vohnyk-cave");

        var solo = Table(new VohnykStore(null), "Оля");
        solo.Start();
        Play(solo, VohnykLevels.Get(1).Solution, soloSeat: 0);
        Assert.DoesNotContain(solo.Awards, a => a.Reason == "ach:vohnyk-duo");
    }

    [Fact]
    public void The_store_never_touches_sqlite_under_the_room_lock()
    {
        using var db = new TempDb();
        var store = new VohnykStore(db.Db);
        int Rows(string table) => db.Db.With(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            return Convert.ToInt32(cmd.ExecuteScalar());
        });
        store.Record(["Оля", "Петро"], 1, 4000, 0, 3, DateTimeOffset.UnixEpoch);
        Assert.Equal(3, store.Pending);                 // два «пройшов» і рекорд пари — у черзі
        Assert.Equal(0, Rows("vohnyk_done"));
        Assert.Equal(0, Rows("vohnyk_best"));
        Assert.Equal(3, store.Stars("оля", 1));         // а пам'ять уже знає
        store.Flush();
        Assert.Equal(2, Rows("vohnyk_done"));
        Assert.Equal(1, Rows("vohnyk_best"));

        // перезапуск сервера: нове сховище піднімає прогрес із бази
        var again = new VohnykStore(db.Db);
        Assert.Equal(3, again.Stars("петро", 1));
        Assert.Equal(4000, again.Best("оля+петро", 1)!.Ms);
        again.Record(["Оля", "Петро"], 1, 9000, 0, 1, DateTimeOffset.UnixEpoch);   // повільніше й гірше — нічого
        Assert.Equal(0, again.Pending);
    }
}

/// <summary>Швидкодія «Вогника і Краплі» — окремою колекцією, щоб годинник не брехав у паралельному прогоні.</summary>
[Collection(SerialPerf.Name)]
public sealed class VohnykPerfTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Perf")]
    public void Three_thousand_ticks_of_two_running_jumping_heroes_with_late_inputs_take_under_a_second()
    {
        var store = new VohnykStore(null);
        for (var n = 1; n < 15; n++) store.Record(["Оля", "Петро"], n, 1000, 0, 1, DateTimeOffset.UnixEpoch);
        var h = new RoomHarness("vohnyk", services: RoomHarness.WithService(store));
        h.Join("Оля");
        h.Join("Петро");
        h.Act(0, "pick", new { level = 15 });
        h.Start();
        var game = (Vohnyk)h.Room.Game;
        int[] pattern = [VohnykWorld.KeyRight, VohnykWorld.KeyRight | VohnykWorld.KeyJump, VohnykWorld.KeyLeft, VohnykWorld.KeyLeft | VohnykWorld.KeyJump, 0];
        var payloads = new JsonElement[2][];
        var sw = new System.Diagnostics.Stopwatch();
        var frames = 0;
        var bytes = 0L;
        lock (h.Room.Sync)
        {
            for (var t = 0; t < 60; t++) { h.Clock.AdvanceMs(40); game.Tick(); }   // відлік
            var rewinds = 0;
            for (var t = 0; t < 3000; t++)
            {
                var s = game.StepNo;
                // по вводу на героя щотика (зміна кожні 3 кроки, у середньому), і кожен — на 5 кроків пізно: перемотування
                var p0 = Views.Payload(new { n = s - 5, c = 0, k = pattern[(t / 2) % pattern.Length] });
                var p1 = Views.Payload(new { n = s - 4, c = 1, k = pattern[(t / 3 + 2) % pattern.Length] });
                h.Clock.AdvanceMs(40);
                sw.Start();
                game.Act(0, "in", p0);
                game.Act(1, "in", p1);
                var r = game.Tick();
                if (r.Frame)
                {
                    var f = game.Frame();
                    frames++;
                    sw.Stop();
                    bytes += Views.Text(f).Length;
                    sw.Start();
                }
                sw.Stop();
                if (game.PhaseNo == Vohnyk.PhGo) rewinds++;
                if (h.Room.Status != RoomStatus.Playing) break;
            }
            output.WriteLine($"3000 тиків (з перемотуванням на кожен ввід, тиків у go: {rewinds}): {sw.Elapsed.TotalMilliseconds:0.0} мс, " +
                $"середній тик {sw.Elapsed.TotalMilliseconds / 3000:0.0000} мс, кадрів {frames}, середній кадр {(frames > 0 ? bytes / frames : 0)} Б");
        }
        Assert.True(sw.ElapsedMilliseconds < 1000, $"{sw.ElapsedMilliseconds} мс");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void A_level_start_allocates_its_buffers_once_and_ticks_allocate_nothing_without_frames()
    {
        var h = new RoomHarness("vohnyk", services: RoomHarness.WithService(new VohnykStore(null)));
        h.Join("Оля");
        h.Join("Петро");
        h.Start();
        var game = (Vohnyk)h.Room.Game;
        lock (h.Room.Sync)
        {
            for (var t = 0; t < 60; t++) { h.Clock.AdvanceMs(40); game.Tick(); }
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var t = 0; t < 1000; t++) { h.Clock.AdvanceMs(40); game.Tick(); }
            Assert.True(game.StepNo > 2000, $"крок {game.StepNo}");
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            output.WriteLine($"1000 тиків без кадрів: {allocated} Б");
            Assert.True(allocated < 1024, $"{allocated} Б");
        }
    }
}
