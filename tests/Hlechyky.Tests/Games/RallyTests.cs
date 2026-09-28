using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Сільське ралі»: фізику, ворота й кола перевіряємо на голому <see cref="RallyCore"/> (машину можна
/// поставити рівно туди, куди треба, а для фізики є «арена» — рівне поле з рамкою), партію, ввід із
/// міткою тика, вихід, рематч і вид — через кімнату. Паритет із браузером — журнали в <c>RallyReplays/</c>.
/// </summary>
[Collection(SerialPerf.Name)]
public partial class RallyTests(ITestOutputHelper output)
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марко", "Зоя"];
    const int S = RallyTrack.CellSub;

    // ---------- підмостки ----------

    static RoomHarness Table(int players = 2, object? options = null, int seed = 42, IServiceProvider? services = null, bool start = true)
    {
        var h = new RoomHarness("rally", options ?? new { track = "selo", laps = "3" }, seed, services);
        foreach (var nick in Nicks.Take(players)) h.Join(nick);
        if (start) h.Start();
        return h;
    }

    static Rally Game(RoomHarness h) => (Rally)h.Room.Game;
    static RallyCore Core(RoomHarness h) => Game(h).Core!;

    /// <summary>Тикати до зеленого світла.</summary>
    static void Green(RoomHarness h)
    {
        for (var i = 0; i < 100 && Game(h).Phase == Rally.PhCount; i++) h.Tick();
        Assert.Equal(Rally.PhRace, Game(h).Phase);
    }

    static void Ctl(RoomHarness h, int seat, int k, int? t = null) => h.Input(seat, "ctl", new { t = t ?? Core(h).T + 1, k });

    /// <summary>Голе ядро одразу після світлофора (фізика з наступного тика).</summary>
    static RallyCore Bare(RallyTrack? track = null, int laps = 3)
    {
        var core = new RallyCore(track ?? RallyTracks.Get("selo"), laps);
        core.T = RallyCore.CountTicks;
        return core;
    }

    /// <summary>Центр клітинки в sub (можна з половинками: 3.5 → межа рядків 3 і 4).</summary>
    static int At(double cells) => (int)Math.Round(cells * S);

    /// <summary>Рівне поле 48×27 з рамкою: для фізики, де траса лише заважала б. Ворота — далеко праворуч.</summary>
    static RallyTrack Arena(char fill = '=', Action<char[][]>? edit = null)
    {
        var rows = new char[RallyTrack.Rows][];
        for (var y = 0; y < RallyTrack.Rows; y++)
        {
            rows[y] = new char[RallyTrack.Cols];
            for (var x = 0; x < RallyTrack.Cols; x++)
                rows[y][x] = x == 0 || y == 0 || x == RallyTrack.Cols - 1 || y == RallyTrack.Rows - 1 ? '#' : fill;
        }
        edit?.Invoke(rows);
        return new RallyTrack("arena", "Арена", [.. rows.Select(r => new string(r))],
            [[[46, 1, 1, 25]], [[45, 1, 1, 25]], [[44, 1, 1, 25]]],
            [(2, 2), (2, 4), (2, 6), (2, 8), (2, 16), (2, 18)]);
    }

    static void Run(RallyCore core, int ticks)
    {
        for (var i = 0; i < ticks; i++) core.Tick();
    }

    static bool InWall(RallyCore core, RallyCar c) => RallySurface.IsWall(core.Track.Tile[RallyCore.CellOf(c.X, c.Y)]);

    static long Dist2(RallyCar a, RallyCar b)
    {
        long dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    /// <summary>Машину місця seat — перед лінією з останнім колом у кишені, щоб наступний перетин був фінішем.</summary>
    static void ReadyToFinish(RallyCore core, int seat, int lapsDone, double x = 19.3)
    {
        var c = core.Put(seat, At(x), At(3.5 + seat % 2), 0, 800);
        c.Lap = lapsDone;
        c.Next = 0;
    }

    /// <summary>Ведемо місце seat автопілотом через Input кімнати, поки не станеться until() або вийде час.</summary>
    static void Pilot(RoomHarness h, RallyPilot pilot, int[] seats, Func<bool> until, int max = 6000)
    {
        var last = new int[RallyCore.Seats];
        Array.Fill(last, -1);
        for (var i = 0; i < max && !until(); i++)
        {
            foreach (var s in seats)
            {
                var m = pilot.Mask(Core(h), s);
                if (m == last[s]) continue;
                last[s] = m;
                Ctl(h, s, m);
            }
            h.Tick();
        }
    }

    static string LastJournal(RoomHarness h) => h.Outbox.OfType<Journal>().Last().Text;

    // =============================================================================================
    // Траси й таблиці
    // =============================================================================================

    [Fact]
    public void Every_track_is_48_by_27_with_a_wall_border()
    {
        Assert.Equal(["selo", "ozero", "nich", "kukurudza", "yarmarok"], RallyTracks.Ids);
        foreach (var t in RallyTracks.All)
        {
            Assert.Equal(RallyTrack.Rows, t.Map.Count);
            Assert.All(t.Map, row => Assert.Equal(RallyTrack.Cols, row.Length));
            Assert.All(t.Map, row => Assert.All(row, ch => Assert.NotEqual(RallySurface.Unknown, RallySurface.Of(ch))));
            for (var x = 0; x < RallyTrack.Cols; x++)
            {
                Assert.True(RallySurface.IsWall(t.CodeAt(x, 0)));
                Assert.True(RallySurface.IsWall(t.CodeAt(x, RallyTrack.Rows - 1)));
            }
            for (var y = 0; y < RallyTrack.Rows; y++)
            {
                Assert.True(RallySurface.IsWall(t.CodeAt(0, y)));
                Assert.True(RallySurface.IsWall(t.CodeAt(RallyTrack.Cols - 1, y)));
            }
            Assert.InRange(t.K, 7, 9);
            Assert.Equal(0, t.Heading);
        }
        Assert.True(RallyTracks.Get("ozero").Ice);
        Assert.True(RallyTracks.Get("nich").Night);
        Assert.True(RallyTracks.Get("kukurudza").Corn);
        Assert.Equal("selo", RallyTracks.Get("нема такої").Id);
    }

    [Fact]
    public void Every_track_passes_the_gate_cut_check()
    {
        foreach (var t in RallyTracks.All)
        {
            Assert.Null(t.CutCheck());
            // ворота 0 — один стовпчик, решітка перед ним і на дорозі
            Assert.Single(t.Gates[0]);
            Assert.Equal(1, t.Gates[0][0][2]);
            Assert.Equal(0, t.LineAxis);
            Assert.Equal(1, t.LineDir);
            foreach (var (x, y) in t.Slots)
            {
                Assert.True(x < t.Gates[0][0][0], $"{t.Id}: слот ({x},{y}) за лінією");
                Assert.Equal(RallySurface.Road, t.CodeAt(x, y));
            }
            // точки повернення — на дорозі чи льоду, у своїх воротах
            for (var g = 0; g < t.K; g++)
            {
                var cell = RallyCore.CellOf(t.ResetX[g], t.ResetY[g]);
                Assert.Equal(g, t.GateAt[cell]);
                Assert.Contains(t.Tile[cell], new[] { (byte)RallySurface.Road, (byte)RallySurface.Ice });
            }
        }
        // зламана мапа не проходить: ворота 2 Села без узбіччя — повз них можна проїхати
        var selo = RallyTracks.Get("selo");
        var gates = selo.Gates.Select(g => g.Select(r => r.ToArray()).ToArray()).ToArray();
        gates[2] = [[40, 9, 3, 1]];
        var broken = new RallyTrack("broken", "Криве", [.. selo.Map], gates, selo.Slots);
        Assert.NotNull(broken.CutCheck());
        // і крива мапа кидає ще в конструкторі
        Assert.Throws<InvalidOperationException>(() => new RallyTrack("x", "x", [.. selo.Map.Take(26)], selo.Gates, selo.Slots));
        Assert.Throws<InvalidOperationException>(() => new RallyTrack("x", "x", [.. selo.Map.Select((r, i) => i == 5 ? r.Replace('=', 'Q') : r)], selo.Gates, selo.Slots));
    }

    [Fact]
    public void Gate_headings_point_across_the_gate_toward_the_next_one()
    {
        foreach (var t in RallyTracks.All)
            for (var g = 0; g < t.K; g++)
            {
                var r = t.Gates[g][0];
                var n = (g + 1) % t.K;
                var column = r[2] <= r[3];
                var expected = column
                    ? (t.GateCX[n] >= t.GateCX[g] ? 0 : 512)
                    : (t.GateCY[n] >= t.GateCY[g] ? 256 : 768);
                Assert.Equal(expected, t.GateA[g]);
                // і справді «туди»: крок від точки повернення курсом gateA не впирається в стіну
                int x = t.ResetX[g] + RallyCore.Cos[t.GateA[g]] * 2 / 16, y = t.ResetY[g] + RallyCore.Sin[t.GateA[g]] * 2 / 16;
                Assert.False(RallySurface.IsWall(t.Tile[RallyCore.CellOf(x, y)]), $"{t.Id}: ворота {g}");
            }
        var selo = RallyTracks.Get("selo");
        Assert.Equal([0, 0, 256, 256, 512, 512, 768, 768], selo.GateA);
    }

    [Fact]
    public void Sin_table_checksum_matches_the_client()
    {
        Assert.Equal(101, RallyCore.Sin[1]);
        Assert.Equal(9434, RallyCore.Sin[100]);
        Assert.Equal(13395, RallyCore.Cos[100]);
        Assert.Equal(16384, RallyCore.Sin[256]);
        Assert.Equal(16384, RallyCore.Cos[0]);
        Assert.Equal(-16384, RallyCore.Cos[512]);
        uint Fnv(int[] a)
        {
            var h = RallyCore.FnvStart;
            foreach (var v in a) h = RallyCore.Mix(h, v);
            return h;
        }
        Assert.Equal("c53c97bf", Fnv(RallyCore.Sin).ToString("x8"));
        Assert.Equal("4c534e0f", Fnv(RallyCore.Cos).ToString("x8"));
    }

    [Fact]
    public void No_formula_leaves_int32()
    {
        // найгірші добутки: швидкість (з запасом на відскоки) × 2¹⁴ удвічі, копиця, зіткнення, частка тика
        const long vmax = 4000;
        Assert.True(vmax * 16384 * 2 < int.MaxValue);
        Assert.True((long)(RallyCore.RWall + RallyCore.RHay) * 16384 < int.MaxValue);
        Assert.True((long)RallyCore.RCar * 2 * 16384 < int.MaxValue);
        Assert.True(vmax * RallyCore.ECar < int.MaxValue);
        Assert.True(vmax * RallyCore.HayBack < int.MaxValue);
        Assert.True((long)RallyCore.TickMs * vmax < int.MaxValue);
        long reach = RallyTrack.CellSub + RallyCore.RWall + RallyCore.RHay;
        Assert.True(reach * reach * 2 < int.MaxValue);
        Assert.True((long)RallyCore.WorldW * RallyCore.WorldW < long.MaxValue);
        // корінь цілий і точний на межах
        foreach (var n in new[] { 0, 1, 2, 3, 4, 15, 16, 17, 1535 * 1535, 1536 * 1536 - 1, 1536 * 1536, int.MaxValue / 2 })
        {
            var r = RallyCore.Isqrt(n);
            Assert.True((long)r * r <= n && (long)(r + 1) * (r + 1) > n, $"Isqrt({n}) = {r}");
        }
    }

    // =============================================================================================
    // Фізика (голе ядро)
    // =============================================================================================

    [Fact]
    public void Full_throttle_reaches_840_and_ninety_percent_within_35_ticks()
    {
        var core = Bare(Arena());
        var c = core.Put(0, At(4), At(13.5));
        c.Mask = 4;
        var ninety = 0;
        var top = 0;
        for (var i = 1; i <= 80; i++)
        {
            core.Tick();
            if (ninety == 0 && c.VF >= 756) ninety = i;
            top = Math.Max(top, c.VF);
        }
        Assert.InRange(ninety, 30, 35);
        Assert.Equal(840, top);
        Assert.Equal(840, c.VF);
        Assert.Equal(0, c.A);
        Assert.Equal(0, c.VL);
    }

    [Fact]
    public void Braking_from_top_speed_stops_within_12_ticks_and_then_reverses_but_never_past_320()
    {
        var core = Bare(Arena());
        var c = core.Put(0, At(30), At(13.5), 0, 840);
        c.Mask = 8;
        var stop = 0;
        var slowest = 0;
        for (var i = 1; i <= 120; i++)
        {
            core.Tick();
            if (stop == 0 && c.VF <= 0) stop = i;
            slowest = Math.Min(slowest, c.VF);
        }
        Assert.InRange(stop, 9, 12);
        Assert.True(slowest >= -RallyCore.MaxRev);
        Assert.InRange(c.VF, -310, -280);
        Assert.True(c.X < At(30));
    }

    [Fact]
    public void Steering_at_speed_turns_28_per_tick_and_keeps_world_velocity()
    {
        var core = Bare(Arena());
        var c = core.Put(0, At(20), At(13.5), 0, 840);
        c.Mask = 2;
        core.Tick();
        Assert.Equal(28, c.A);
        // опір забрав своє (840 → 788), а напрямок руху в світі не змінився — змінився лише розклад на VF/VL
        Assert.InRange(c.VX, 786, 790);
        Assert.InRange(c.VY, -2, 2);
        Assert.True(c.VL < 0);
        core.Tick();
        Assert.Equal(56, c.A);
        c.Mask = 1;
        core.Tick();
        Assert.Equal(28, c.A);
    }

    [Fact]
    public void An_abandoned_car_stops_instead_of_creeping()
    {
        // цілочисельний опір не бере швидкостей, менших за 256/опір, — без опору коченню машина, яку покинули
        // (F5, вийшов на хвилинку), повзла б сама: на дорозі 15 sub/тик, на льоду 21 і ще й боком
        foreach (var fill in new[] { '=', '*', '.' })
        {
            var core = Bare(Arena(fill));
            var c = core.Put(0, At(20), At(13.5), 0, 700);
            c.VL = 300;
            Run(core, 150);
            Assert.True(c.VF == 0 && c.VL == 0, $"{fill}: VF {c.VF}, VL {c.VL}");
            var (x, y) = (c.X, c.Y);
            Run(core, 50);
            Assert.Equal((x, y), (c.X, c.Y));
        }
        // а на ходу опору коченню нема: накатом понад StopV швидкість гасить лише опір дороги, як у spec §5.4
        var run = Bare(Arena());
        var r = run.Put(0, At(5), At(13.5), 0, 700);
        var vf = 700;
        for (var i = 0; i < 20; i++)
        {
            run.Tick();
            vf -= vf * 16 / 256;
            Assert.Equal(vf, r.VF);
        }
    }

    [Fact]
    public void Steering_while_stopped_does_nothing()
    {
        var core = Bare(Arena());
        var c = core.Put(0, At(20), At(13.5), 100, 0);
        c.Mask = 2;
        Run(core, 10);
        Assert.Equal(100, c.A);
        c.Mask = 1 | 2;
        c.VF = 800;
        core.Tick();
        Assert.Equal(100, c.A);
    }

    [Fact]
    public void Reverse_gear_steers_the_other_way()
    {
        var core = Bare(Arena());
        var c = core.Put(0, At(20), At(13.5), 0, -300);
        c.Mask = 2;
        core.Tick();
        Assert.Equal(1024 - 28, c.A);
        // повільно — кермо слабше: 28 · 128 / 256 = 14
        var d = core.Put(1, At(20), At(20.5), 0, 144);
        d.Mask = 2;
        core.Tick();
        Assert.Equal(14, d.A);
    }

    [Fact]
    public void Handbrake_turns_faster_and_slides_longer()
    {
        (int Ticks, int Slide) Turn90(int mask)
        {
            var core = Bare(Arena());
            var c = core.Put(0, At(20), At(13.5), 0, 840);
            c.Mask = mask;
            var n = 0;
            while (c.A < 256 && n < 40)
            {
                core.Tick();
                n++;
            }
            return (n, Math.Abs(c.VL));
        }
        var plain = Turn90(4 | 2);
        var hand = Turn90(4 | 2 | 16);
        Assert.Equal(10, plain.Ticks);
        Assert.Equal(7, hand.Ticks);
        Assert.True(hand.Slide > plain.Slide * 2, $"ручник {hand.Slide}, без нього {plain.Slide}");
    }

    [Fact]
    public void Grass_halves_speed_within_6_ticks_and_settles_near_200()
    {
        var core = Bare(Arena('.'));
        var c = core.Put(0, At(4), At(13.5), 0, 840);
        var half = 0;
        for (var i = 1; i <= 20 && half == 0; i++)
        {
            core.Tick();
            if (c.VF <= 420) half = i;
        }
        Assert.InRange(half, 5, 6);
        c.Mask = 4;
        Run(core, 60);
        Assert.InRange(c.VF, 190, 210);
        // багно — ще гірше: повзеш
        var mud = Bare(Arena('M'));
        var m = mud.Put(0, At(4), At(13.5));
        m.Mask = 4;
        Run(mud, 60);
        Assert.InRange(m.VF, 70, 95);
    }

    [Fact]
    public void Ice_keeps_sliding_after_a_turn()
    {
        (int After, int Later) Slide(char fill)
        {
            var core = Bare(Arena(fill));
            var c = core.Put(0, At(10), At(8.5), 0, 840);
            c.Mask = 2;
            // без газу машина крутиться, поки має швидкість уздовж себе: на льоду це майже рівно чверть оберту
            for (var n = 0; c.A < 240 && n < 100; n++) core.Tick();
            Assert.True(c.A >= 240, $"{fill}: повернули лише на {c.A}");
            var after = Math.Abs(c.VL);
            c.Mask = 0;
            Run(core, 10);
            return (after, Math.Abs(c.VL));
        }
        var ice = Slide('*');
        var road = Slide('=');
        Assert.True(ice.After > 300, $"лід: {ice.After}");
        Assert.True(ice.Later > 150, $"лід за 10 тиків: {ice.Later}");
        Assert.True(road.Later < 20, $"дорога за 10 тиків: {road.Later}");
        Assert.True(ice.After > road.After * 2);
    }

    [Fact]
    public void Oil_slick_cuts_grip_for_30_ticks_then_restores()
    {
        var core = Bare(Arena(edit: m => m[13][10] = 'o'));
        var c = core.Put(0, At(9.8), At(13.5), 0, 500);
        core.Tick();
        Assert.Equal(10, c.X >> 11);
        Assert.NotEqual(0, c.Ev & RallyCore.EvOil);
        Assert.Equal(RallyCore.OilTicks - 1, c.Oil);
        c.VL = 500;
        core.Tick();
        // на мастилі бік тримає лише 10/256
        Assert.Equal(500 - 500 * RallyCore.OilGrip / 256, c.VL);
        Run(core, RallyCore.OilTicks - 2);
        Assert.Equal(0, c.Oil);
        c.VL = 500;
        core.Tick();
        Assert.Equal(500 - 500 * 110 / 256, c.VL);
    }

    [Fact]
    public void Puddle_sets_the_splash_event_once_on_entry()
    {
        var core = Bare(Arena(edit: m => m[13][10] = '~'));
        var c = core.Put(0, At(9.9), At(13.5), 0, 300);
        core.Tick();
        Assert.Equal(10, c.X >> 11);
        Assert.NotEqual(0, c.Ev & RallyCore.EvPuddle);
        core.Tick();
        Assert.Equal(10, c.X >> 11);
        Assert.Equal(0, c.Ev & RallyCore.EvPuddle);
        // калюжа веде: зчеплення 40 замість 110
        c.VL = 400;
        core.Tick();
        Assert.Equal(400 - 400 * 40 / 256, c.VL);
    }

    [Fact]
    public void Boost_strip_adds_384_caps_at_1408_holds_25_ticks_and_has_a_cooldown()
    {
        var core = Bare(Arena(edit: m => { for (var y = 1; y < 26; y++) { m[y][10] = '+'; m[y][30] = '+'; } }));
        var c = core.Put(0, At(9.8), At(13.5), 0, 500);
        core.Tick();
        Assert.NotEqual(0, c.Ev & RallyCore.EvBoost);
        // 500 − опір 31 = 469, +384 = 853
        Assert.Equal(469 + RallyCore.BoostAdd, c.VF);
        Assert.Equal(RallyCore.BoostTicks - 1, c.BoostT);
        c.Mask = 4;
        var top = 0;
        for (var i = 0; i < RallyCore.BoostTicks - 1; i++)
        {
            core.Tick();
            top = Math.Max(top, c.VF);
        }
        Assert.Equal(0, c.BoostT);
        Assert.True(top > 1000 && top <= RallyCore.BoostCap, $"турбо: {top}");
        // кулдаун: друга смуга одразу — нічого
        var d = core.Put(1, At(29.8), At(20.5), 0, 900);
        d.BoostCd = 5;
        core.Tick();
        Assert.Equal(30, d.X >> 11);
        Assert.Equal(0, d.Ev & RallyCore.EvBoost);
        // а зі швидкості понад стелю турбо не розганяє далі
        var e = core.Put(2, At(29.8), At(6.5), 0, 1400);
        core.Tick();
        Assert.Equal(RallyCore.BoostCap, e.VF);
    }

    [Fact]
    public void Ramp_launches_only_at_512_or_faster_and_flight_lasts_14_ticks()
    {
        var core = Bare(Arena(edit: m => { for (var y = 1; y < 26; y++) m[y][10] = 'J'; }));
        var slow = core.Put(0, At(9.9), At(5.5), 0, 400);
        core.Tick();
        Assert.Equal(10, slow.X >> 11);
        Assert.Equal(0, slow.Air);

        var fast = core.Put(1, At(9.7), At(15.5), 0, 800);
        fast.Mask = 2 | 4;
        core.Tick();
        Assert.NotEqual(0, fast.Ev & RallyCore.EvJump);
        Assert.Equal(RallyCore.AirTicks - 1, fast.Air);
        int a = fast.A, vf = fast.VF, launch = core.T;
        var landed = 0;
        for (var i = 0; i < 30 && landed == 0; i++)
        {
            core.Tick();
            if ((fast.Ev & RallyCore.EvLand) != 0) landed = core.T;
            else
            {
                // у повітрі: ні керма, ні газу, ні опору
                Assert.Equal(a, fast.A);
                Assert.Equal(vf, fast.VF);
            }
        }
        Assert.Equal(launch + RallyCore.AirTicks - 1, landed);
        Assert.Equal(0, fast.Air);
        core.Tick();
        Assert.NotEqual(a, fast.A);
    }

    [Fact]
    public void Airborne_car_ignores_mud_and_lands_past_a_three_cell_pit()
    {
        var yarmarok = RallyTracks.Get("yarmarok");
        Assert.Equal('J', yarmarok.Map[3][14]);
        Assert.Equal("MMM", yarmarok.Map[3][15..18]);

        var core = Bare(yarmarok);
        var c = core.Put(0, At(12.5), At(3.5), 0, 840);
        c.Mask = 4;
        var landed = -1;
        var minSpeed = int.MaxValue;
        for (var i = 0; i < 40 && landed < 0; i++)
        {
            core.Tick();
            if (c.Air > 0) minSpeed = Math.Min(minSpeed, c.VF);
            if ((c.Ev & RallyCore.EvLand) != 0) landed = c.X >> 11;
        }
        Assert.True(landed >= 18, $"приземлився в клітинці {landed}");
        Assert.Equal(RallySurface.Road, yarmarok.CodeAt(landed, 3));
        Assert.True(minSpeed >= 800);

        // ледь 512 на трампліні — долітає лише до багна
        var slow = Bare(yarmarok);
        var s = slow.Put(0, At(13.76), At(3.5), 0, 560);
        slow.Tick();
        Assert.Equal(RallyCore.AirTicks - 1, s.Air);
        while (s.Air > 0) slow.Tick();
        Assert.Equal(RallySurface.Mud, yarmarok.CodeAt(s.X >> 11, s.Y >> 11));
    }

    [Fact]
    public void Wall_bounce_reflects_35_percent_and_never_lets_the_centre_inside_a_wall()
    {
        var core = Bare(Arena(edit: m => { for (var y = 1; y < 26; y++) m[y][20] = '#'; }));
        var c = core.Put(0, 20 * S - RallyCore.RWall - 300, At(13.5), 0, 800);
        core.Tick();
        Assert.NotEqual(0, c.Ev & RallyCore.EvWall);
        // 800 − опір 50 = 750 у стіну → назад 750·90/256 = 263
        Assert.True(c.X <= 20 * S - RallyCore.RWall);
        Assert.InRange(c.VF, -270, -250);

        // ковзом уздовж тину — шви між клітинками не чіпляють
        var slide = Bare(Arena(edit: m => { for (var x = 1; x < 47; x++) m[10][x] = '#'; }));
        var s = slide.Put(0, At(5), 11 * S + RallyCore.RWall + 10, 1000, 800);
        s.Mask = 4;
        for (var i = 0; i < 30; i++)
        {
            slide.Tick();
            Assert.True(s.Y >= 11 * S + RallyCore.RWall - 1, $"тик {i}: y {s.Y}");
        }
        Assert.True(s.X > At(13), $"проїхав уздовж тину лише до {s.X / (double)S:0.0}");
        output.WriteLine($"уздовж тину за 30 тиків: {s.X / (double)S - 5:0.0} клітинки");

        // двісті ударів під різними кутами по справжніх трасах: центр ніколи не в стіні
        var rng = new Random(7);
        var hits = 0;
        foreach (var t in RallyTracks.All)
        {
            var k = Bare(t);
            for (var n = 0; n < 40; n++)
            {
                int cell;
                do cell = rng.Next(RallyTrack.Cells);
                while (RallySurface.IsWall(t.Tile[cell]) || t.Tile[cell] == RallySurface.Hay);
                var car = k.Put(0, (cell % RallyTrack.Cols) * S + rng.Next(S), (cell / RallyTrack.Cols) * S + rng.Next(S), rng.Next(1024), rng.Next(200, 1409));
                if (InWall(k, car)) continue;
                car.Mask = rng.Next(32);
                for (var i = 0; i < 12; i++)
                {
                    k.Tick();
                    hits += (car.Ev & RallyCore.EvWall) != 0 ? 1 : 0;
                    Assert.False(InWall(k, car), $"{t.Id}: центр у стіні ({car.X},{car.Y})");
                }
            }
        }
        Assert.True(hits > 30, $"ударів лише {hits}");
    }

    [Fact]
    public void Two_substeps_prevent_tunnelling_through_a_one_cell_wall_at_boost_speed()
    {
        var track = Arena(edit: m => { for (var y = 1; y < 26; y++) m[y][20] = '#'; });
        for (var off = 0; off < 1500; off += 37)
        {
            var core = Bare(track);
            var c = core.Put(0, 20 * S - RallyCore.RWall - off, At(13.5), 0, RallyCore.BoostCap);
            c.BoostT = 10;
            c.Mask = 4;
            Run(core, 3);
            Assert.True(c.X < 20 * S, $"відступ {off}: проскочив до {c.X}");
        }
        // і дві машини назустріч на повній — не проходять одна крізь одну
        var arena = Bare(Arena());
        var a = arena.Put(0, At(10), At(13.5), 0, RallyCore.BoostCap);
        var b = arena.Put(1, At(10) + 2 * RallyCore.RCar + 700, At(13.5), 512, RallyCore.BoostCap);
        a.BoostT = b.BoostT = 5;
        Run(arena, 3);
        Assert.True(a.X < b.X);
    }

    [Fact]
    public void Hay_bale_absorbs_speed_and_pushes_out()
    {
        var core = Bare(Arena(edit: m => m[13][20] = 'H'));
        var c = core.Put(0, 20 * S + S / 2 - 1536 - 200, 13 * S + S / 2, 0, 800);
        core.Tick();
        Assert.NotEqual(0, c.Ev & RallyCore.EvHay);
        long dx = c.X - (20 * S + S / 2), dy = c.Y - (13 * S + S / 2);
        Assert.True(dx * dx + dy * dy >= 1534L * 1534);
        Assert.True(c.VF < 0 && Math.Abs(c.VF) < 800 * 160 / 256, $"після копиці {c.VF}");
        // збоку — копиця лише обтирає, машина їде далі
        var side = Bare(Arena(edit: m => m[13][20] = 'H'));
        var s = side.Put(0, At(17), 13 * S + S / 2 - 1400, 0, 700);
        s.Mask = 4;
        Run(side, 20);
        Assert.True(s.X > At(22), $"ковзом повз копицю лише до {s.X / (double)S:0.0}");
    }

    [Fact]
    public void Head_on_cars_exchange_half_their_closing_speed_and_separate()
    {
        var core = Bare(Arena());
        var a = core.Put(0, At(20), At(13.5), 0, 640);
        var b = core.Put(1, At(20) + 2000, At(13.5), 512, 640);
        core.Tick();
        Assert.NotEqual(0, a.Ev & RallyCore.EvCar);
        Assert.NotEqual(0, b.Ev & RallyCore.EvCar);
        // по 600 назустріч (після опору) → зіткнення 1200, e = ½ → кожен відлітає з 300 назад
        Assert.InRange(a.VX, -310, -290);
        Assert.InRange(b.VX, 290, 310);
        Assert.True(Dist2(a, b) >= 1536L * 1536);
        Assert.Equal(a.VX, -b.VX);
        core.Tick();
        Assert.Equal(0, a.Ev & RallyCore.EvCar);
    }

    [Fact]
    public void Ghost_cars_never_collide()
    {
        var core = Bare(Arena());
        var a = core.Put(0, At(20), At(13.5), 0, 640);
        var b = core.Put(1, At(20) + 1000, At(13.5), 512, 640);
        a.Ghost = true;
        core.Tick();
        Assert.Equal(0, a.Ev & RallyCore.EvCar);
        Assert.True(a.VX > 0 && b.VX < 0);
    }

    [Fact]
    public void An_airborne_car_flies_over_a_grounded_one()
    {
        var core = Bare(Arena());
        var a = core.Put(0, At(20), At(13.5), 0, 640);
        var b = core.Put(1, At(20) + 1000, At(13.5), 512, 640);
        a.Air = 6;
        core.Tick();
        Assert.Equal(0, b.Ev & RallyCore.EvCar);
        Assert.True(a.VX > 0 && b.VX < 0);
        // а двоє в повітрі — стукаються
        b.Air = 6;
        b.X = a.X + 1000;
        b.Y = a.Y;
        core.Tick();
        Assert.NotEqual(0, b.Ev & RallyCore.EvCar);
    }

    [Fact]
    public void Clamping_keeps_cars_inside_the_world()
    {
        var core = Bare(Arena());
        var a = core.Put(0, RallyCore.RWall + 10, At(13.5), 512, 900);
        var b = core.Put(1, RallyCore.RWall + 600, At(13.5), 512, 900);
        var c = core.Put(2, RallyCore.WorldW - RallyCore.RWall - 10, RallyCore.WorldH - RallyCore.RWall - 10, 128, 1400);
        for (var i = 0; i < 20; i++)
        {
            core.Tick();
            foreach (var car in new[] { a, b, c })
            {
                Assert.InRange(car.X, RallyCore.RWall, RallyCore.WorldW - RallyCore.RWall);
                Assert.InRange(car.Y, RallyCore.RWall, RallyCore.WorldH - RallyCore.RWall);
            }
        }
    }

    [Fact]
    public void Same_inputs_same_seed_same_hash()
    {
        var journal = RallyReplays.Record("yarmarok", 3, 6, 1000, seed: 11, drift: 60);
        var h1 = RallyReplays.Play(journal);
        var h2 = RallyReplays.Play(journal);
        Assert.Equal(h1, h2);
        Assert.Equal(journal.Hash, h1);
        // інший журнал — інший хеш
        var other = RallyReplays.Record("yarmarok", 3, 6, 1000, seed: 12, drift: 60);
        Assert.NotEqual(journal.Hash, other.Hash);
    }

    [Fact]
    public void Replay_journals_produce_the_recorded_hashes()
    {
        var dir = RallyReplays.Dir();
        if (Environment.GetEnvironmentVariable("RALLY_WRITE_REPLAYS") == "1") RallyReplays.WriteAll(dir);
        var files = Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.Equal(4, files.Length);
        foreach (var file in files)
        {
            var journal = RallyReplays.Load(file);
            Assert.Equal(journal.Hash, RallyReplays.Play(journal));
            output.WriteLine($"{Path.GetFileName(file)}: {journal.Track}, {journal.Inputs.Count} вводів, хеш {journal.Hash}");
        }
    }

    [Fact]
    public void An_autopilot_laps_every_track_in_a_sane_time()
    {
        foreach (var t in RallyTracks.All)
        {
            var core = new RallyCore(t, 3);
            core.Grid(0, "traktor");
            var pilot = new RallyPilot(t);
            var laps = new List<int>();
            for (var i = 0; i < 4000 && core.Cars[0].Fin == 0; i++)
            {
                core.Cars[0].Mask = pilot.Mask(core, 0);
                core.Tick();
                if ((core.Cars[0].Ev & RallyCore.EvLap) != 0) laps.Add(core.Cars[0].LastMs);
            }
            Assert.Equal(3, laps.Count);
            Assert.All(laps, ms => Assert.InRange(ms, 8000, 16000));
            output.WriteLine($"{t.Id}: {string.Join(" · ", laps.Select(ms => Rally.Clock(ms, 2)))}");
        }
    }

    // =============================================================================================
    // Ворота, кола, час
    // =============================================================================================

    [Fact]
    public void Cars_start_with_next_gate_1_and_lap_0_and_the_start_line_does_not_count_at_first()
    {
        var core = new RallyCore(RallyTracks.Get("selo"), 3);
        core.Grid(0, "moped");
        var c = core.Cars[0];
        Assert.Equal((1, 0), (c.Next, c.Lap));
        Assert.Equal((19 * S + S / 2, 2 * S + S / 2), (c.X, c.Y));
        Assert.Equal("moped", c.Car);
        c.Mask = 4;
        while (c.X < 21 * S) core.Tick();
        Assert.Equal((1, 0), (c.Next, c.Lap));
        Assert.Equal(0, c.LastMs);
        while (c.X < 35 * S) core.Tick();
        Assert.Equal(2, c.Next);
    }

    [Fact]
    public void Skipping_a_gate_gives_no_progress_until_you_go_back()
    {
        var core = Bare();
        var c = core.Put(0, At(41.5), At(14.9), 256, 400);
        c.Next = 2;
        core.Tick();
        Assert.Equal(15, c.Y >> 11);
        Assert.Equal(2, c.Next);
        core.Put(0, At(41.5), At(8.9), 256, 400);
        core.Tick();
        Assert.Equal(3, c.Next);
        core.Put(0, At(41.5), At(14.9), 256, 400);
        core.Tick();
        Assert.Equal(4, c.Next);
    }

    [Fact]
    public void Driving_backwards_through_gates_changes_nothing()
    {
        var core = Bare();
        var c = core.Put(0, At(41.5), At(9.1), 768, 400);
        c.Next = 3;
        core.Tick();
        Assert.Equal(8, c.Y >> 11);
        Assert.Equal(3, c.Next);
        // і задом через лінію — не коло
        var d = core.Put(1, At(20.1), At(3.5), 0, -300);
        d.Next = 1;
        d.Lap = 1;
        core.Tick();
        Assert.Equal(19, d.X >> 11);
        Assert.Equal((1, 1), (d.Next, d.Lap));
        Assert.Equal(0, d.Ev & RallyCore.EvLap);
    }

    [Fact]
    public void Completing_all_gates_then_the_line_counts_a_lap_with_sub_tick_time()
    {
        int LapAt(int x0, int vf)
        {
            var core = Bare();
            core.T = 75 + 300;
            var c = core.Put(0, x0, At(3.5), 0, vf);
            c.Next = 0;
            c.LapStartMs = 1000;
            while ((c.Ev & RallyCore.EvLap) == 0) core.Tick();
            Assert.Equal((1, 1), (c.Lap, c.Next));
            var n = core.T - 76;
            var ms = c.LastMs + 1000;
            Assert.InRange(ms, n * 40, n * 40 + 40);
            Assert.Equal(ms, c.LapStartMs);
            Assert.Equal(c.LastMs, c.BestMs);
            return ms;
        }
        var early = LapAt(20 * S - 100, 700);
        var late = LapAt(20 * S - 600, 700);
        var faster = LapAt(20 * S - 600, 1300);
        Assert.NotEqual(early, late);
        Assert.True(early < late);
        Assert.True(faster < late);
        // усі три — в одному тику, але з різною часткою
        Assert.Equal(early / 40, late / 40);
    }

    [Fact]
    public void Best_and_last_lap_are_tracked_per_car()
    {
        var t = RallyTracks.Get("selo");
        var core = new RallyCore(t, 5);
        core.Grid(0, "traktor");
        core.Grid(3, "viz");
        var pilot = new RallyPilot(t);
        var laps = new[] { new List<int>(), new List<int>() };
        for (var i = 0; i < 3000 && (laps[0].Count < 3 || laps[1].Count < 3); i++)
        {
            core.Cars[0].Mask = pilot.Mask(core, 0);
            // другий ледачий: газ лише через тик
            core.Cars[3].Mask = pilot.Mask(core, 3) & (i % 2 == 0 ? 31 : ~4);
            core.Tick();
            if ((core.Cars[0].Ev & RallyCore.EvLap) != 0) laps[0].Add(core.Cars[0].LastMs);
            if ((core.Cars[3].Ev & RallyCore.EvLap) != 0) laps[1].Add(core.Cars[3].LastMs);
        }
        Assert.Equal(laps[0].Min(), core.Cars[0].BestMs);
        Assert.Equal(laps[0][^1], core.Cars[0].LastMs);
        Assert.Equal(laps[1].Min(), core.Cars[3].BestMs);
        Assert.True(core.Cars[3].BestMs > core.Cars[0].BestMs);
    }

    [Fact]
    public void Finish_makes_a_ghost_and_starts_the_20_second_timeout()
    {
        var h = Table(2);
        Green(h);
        var core = Core(h);
        ReadyToFinish(core, 0, 2);
        for (var i = 0; i < 10 && core.Cars[0].Fin == 0; i++) h.Tick();
        var c = core.Cars[0];
        Assert.Equal(1, c.Fin);
        Assert.True(c.Ghost);
        Assert.Equal(3, c.Lap);
        Assert.Equal(RallyCore.TimeoutTicks, Game(h).Left);
        Assert.Contains(h.Outbox.OfType<TableSaid>(), s => s.Line.Text.Contains("Оля — перше місце на фініші"));
        h.Tick();
        Assert.Equal(RallyCore.TimeoutTicks - 1, Game(h).Left);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        var f = h.View(null).GetProperty("f");
        Assert.Equal(RallyCore.TimeoutTicks - 1, f.GetProperty("s").GetInt32());
        Assert.Equal(1, f.GetProperty("c")[10].GetInt32());
        // привид воріт не рахує
        var gate = c.Next;
        core.Put(0, At(33.6), At(3.5), 0, 600);
        h.Tick(3);
        Assert.Equal(gate, c.Next);
    }

    [Fact]
    public void Timeout_ranks_the_rest_by_progress_and_ends_the_race()
    {
        var h = Table(3);
        Green(h);
        var core = Core(h);
        ReadyToFinish(core, 0, 2);
        core.Cars[2].Next = 4;
        while (core.Cars[0].Fin == 0) h.Tick();
        Assert.Equal(RallyCore.TimeoutTicks, Game(h).Left);
        h.Tick(RallyCore.TimeoutTicks - 1);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        h.Tick();
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        var results = h.View(null).GetProperty("results");
        Assert.Equal([0, 2, 1], results.EnumerateArray().Select(r => r.GetProperty("seat").GetInt32()));
        Assert.Equal(0, results[1].GetProperty("fin").GetInt32());
        Assert.Equal(0, results[1].GetProperty("ms").GetInt32());
        Assert.Contains("Ганна — без фінішу · Петро — без фінішу", LastJournal(h));
    }

    [Fact]
    public void All_finished_ends_the_race_immediately()
    {
        var h = Table(2);
        Green(h);
        var core = Core(h);
        ReadyToFinish(core, 0, 2, 19.5);
        ReadyToFinish(core, 1, 2, 18.8);
        for (var i = 0; i < 20 && h.Room.Status == RoomStatus.Playing; i++) h.Tick();
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal(Rally.PhOver, Game(h).Phase);
        Assert.True(Game(h).Left > RallyCore.TimeoutTicks - 20);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Single(h.Finished);
    }

    [Fact]
    public void Four_minutes_without_a_finisher_ends_with_progress_leaders()
    {
        var h = Table(2);
        Green(h);
        Core(h).Cars[1].Next = 3;
        // хтось усі 4 хвилини смикає ручник на місці — інакше гонку зняла б тиша за кермом (Rally.IdleTicks)
        for (var i = 0; i < RallyCore.MaxRaceTicks - 1; i++)
        {
            if (i % 500 == 0) Ctl(h, 0, i / 500 % 2 == 0 ? 16 : 0);
            h.Tick();
        }
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        h.Tick();
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Room.Result!.Winners);
        Assert.Equal("Сільське ралі · Село: за 4 хвилини ніхто не доїхав — найдалі Петро, 0 кіл з 3", LastJournal(h));
    }

    [Fact]
    public void Ranking_orders_finishers_then_laps_gates_distance()
    {
        var core = Bare();
        for (var i = 0; i < 5; i++) core.Grid(i, "traktor");
        // 0 — на колі 1 перед воротами 3, далеко; 1 — фінішував другим; 2 — фінішував першим;
        // 3 — на колі 1 перед воротами 3, ближче; 4 — на колі 2; 5 — нема
        void Set(int s, int lap, int next, int x, int y, int fin = 0)
        {
            var c = core.Put(s, x, y);
            c.Lap = lap;
            c.Next = next;
            c.Fin = fin;
        }
        Set(0, 1, 3, At(42), At(10));
        Set(1, 3, 1, At(30), At(3), 2);
        Set(2, 3, 1, At(31), At(3), 1);
        Set(3, 1, 3, At(42), At(13));
        Set(4, 2, 0, At(10), At(3));
        core.Rank();
        Assert.Equal([2, 1, 4, 3, 0, 5], core.Order);
        Assert.Equal([5, 2, 1, 4, 3, 0], core.Place);
        // «перед лінією» (Next = 0) — це K−1 воріт кола, а не мінус одні
        Assert.Equal(2 * 8 + 7, core.Passed(core.Cars[4]));
        Assert.Equal(1 * 8 + 2, core.Passed(core.Cars[0]));
    }

    [Fact]
    public void Reset_returns_to_the_last_gate_with_a_stall_and_a_cooldown()
    {
        var h = Table(2);
        Green(h);
        var core = Core(h);
        var t = core.Track;
        var c = core.Put(0, At(30), At(22.5), 1000, 500);
        c.Next = 4;
        c.Mask = 4;
        Assert.True(h.Act(0, "reset").Ok);
        Assert.Equal((t.ResetX[3], t.ResetY[3], t.GateA[3]), (c.X, c.Y, c.A));
        Assert.Equal((0, 0), (c.VF, c.VL));
        h.Tick();
        Assert.NotEqual(0, c.Ev & RallyCore.EvReset);
        Assert.Equal(RallyCore.StallTicks - 1, c.Stall);
        // секунду без керма, хоч газ і тиснуть
        Assert.Equal(0, c.VF);
        var refused = h.Act(0, "reset");
        Assert.False(refused.Ok);
        Assert.Equal("Щойно ж повертали — зачекай", refused.Message);
        h.Tick(RallyCore.StallTicks);
        Assert.True(c.VF > 0);
        h.Tick(RallyCore.ResetCdTicks);
        // ще жодних воріт — на свій слот
        var d = core.Cars[1];
        d.X += 5000;
        Assert.True(h.Act(1, "reset").Ok);
        Assert.Equal((t.SlotX[1], t.SlotY[1], t.Heading), (d.X, d.Y, d.A));
    }

    // =============================================================================================
    // Партія через кімнату
    // =============================================================================================

    [Fact]
    public void Countdown_lasts_75_ticks_freezes_cars_and_remembers_held_gas()
    {
        var h = Table(2);
        var core = Core(h);
        Assert.Equal(Rally.PhCount, Game(h).Phase);
        h.Tick(29);
        Ctl(h, 0, 4);
        var x = core.Cars[0].X;
        h.Tick(45);
        Assert.Equal(74, core.T);
        Assert.Equal(Rally.PhCount, Game(h).Phase);
        Assert.Equal(4, core.Cars[0].Mask);
        h.Tick();
        Assert.Equal(Rally.PhRace, Game(h).Phase);
        Assert.Equal(x, core.Cars[0].X);
        h.Tick();
        Assert.True(core.Cars[0].X > x);
        Assert.Equal(core.Cars[1].X, core.Track.SlotX[1]);
    }

    [Fact]
    public void Frames_in_countdown_come_every_fifth_tick_and_every_tick_in_race()
    {
        var h = Table(2);
        var counted = new List<int>();
        for (var i = 0; i < 90; i++)
        {
            var before = h.Outbox.Count;
            h.Tick();
            if (h.Outbox.Skip(before).OfType<RoomFrame>().Any()) counted.Add(Core(h).T);
        }
        var count = counted.Where(t => t <= 75).ToList();
        Assert.Equal(Enumerable.Range(0, 16).Select(i => i * 5).Where(t => t > 0), count);
        Assert.Equal(Enumerable.Range(76, 15), counted.Where(t => t > 75));
        // гудок на світлофорі — позачерговий кадр
        var h2 = Table(2);
        h2.Tick(2);
        h2.Input(0, "horn");
        var n = h2.Outbox.Count;
        h2.Tick();
        Assert.Single(h2.Outbox.Skip(n).OfType<RoomFrame>());
    }

    [Fact]
    public void Late_engine_ticks_are_caught_up_to_25_steps_a_second_but_no_more_than_three_at_once()
    {
        var h = Table(2);
        Green(h);
        var core = Core(h);
        // каркас приходить рівно за 40 мс — рівно один крок
        var t = core.T;
        h.Tick();
        Assert.Equal(t + 1, core.T);
        // запізнився на 80 мс (зерно таймера Windows) — три кроки, щоб гонка йшла в справжньому часі
        t = core.T;
        h.Clock.AdvanceMs(80);
        h.Tick();
        Assert.Equal(t + 3, core.T);
        // 50 мс — один крок, а решта 10 мс чекає наступного разу: 50 + 70 = 120 мс → 3 кроки на двох викликах
        t = core.T;
        h.Clock.AdvanceMs(10);
        h.Tick();
        Assert.Equal(t + 1, core.T);
        h.Clock.AdvanceMs(30);
        h.Tick();
        Assert.Equal(t + 3, core.T);
        // сервер спав п'ять секунд — не надолужуємо, лише три кроки, і далі знову рівно
        t = core.T;
        h.Clock.AdvanceMs(5000);
        h.Tick();
        Assert.Equal(t + Rally.MaxSteps, core.T);
        h.Tick();
        Assert.Equal(t + Rally.MaxSteps + 1, core.T);
    }

    [Fact]
    public void Events_of_every_caught_up_step_reach_the_frame()
    {
        var h = Table(2);
        Green(h);
        var core = Core(h);
        Assert.True(h.Act(1, "horn").Ok);
        h.Clock.AdvanceMs(80);
        h.Tick();                                   // гудок — на першому з трьох кроків
        Assert.Equal(0, core.Cars[1].Ev & RallyCore.EvHorn);
        var f = (RallyFrame)Game(h).Frame()!;
        Assert.NotEqual(0, f.C[Rally.Stride + 8] & RallyCore.EvHorn);
        h.Tick();
        f = (RallyFrame)Game(h).Frame()!;
        Assert.Equal(0, f.C[Rally.Stride + 8] & RallyCore.EvHorn);
    }

    [Fact]
    public void Solo_race_finishes_as_a_draw_with_a_time_trial_log_line()
    {
        var laps = new RallyLaps(new MemoryGameStore(), a => a());
        var h = Table(1, services: RoomHarness.WithService(laps));
        var pilot = new RallyPilot(Core(h).Track);
        Pilot(h, pilot, [0], () => h.Room.Status == RoomStatus.Finished);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(h.Room.Result!.Draw);
        var c = Core(h).Cars[0];
        Assert.Equal(1, c.Fin);
        var line = LastJournal(h);
        Assert.Equal($"Сільське ралі · Село, 3 кола: Оля наодинці з секундоміром — {Rally.Clock(c.FinishMs, 1)}, найкраще коло {Rally.Clock(c.BestMs, 2)}"
            + $" · новий рекорд траси: Оля, {Rally.Clock(c.BestMs, 2)}", line);
        // перше коло в порожніх рекордах — новий рекорд траси; про фініш соло Глек мовчить
        var said = h.Outbox.OfType<TableSaid>().Select(s => s.Line.Text).ToList();
        Assert.DoesNotContain(said, s => s.Contains("на фініші"));
        Assert.Contains(said, s => s.StartsWith("⏱ Новий рекорд «Село»: Оля"));
        Assert.Equal(c.BestMs, laps.Top("selo")[0].Ms);
        // соло без суперників — ачівок за перемогу нема
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:rally-win3");
    }

    [Fact]
    public void Winner_is_the_first_finisher_and_the_log_lists_gaps_and_best_lap()
    {
        var h = Table(4);
        Green(h);
        var core = Core(h);
        h.Tick(20);
        ReadyToFinish(core, 1, 2);
        h.Tick(3);
        ReadyToFinish(core, 3, 2);
        h.Tick(30);
        ReadyToFinish(core, 0, 2);
        h.Tick(4);
        Assert.Equal([3, 1, 2], new[] { core.Cars[0].Fin, core.Cars[1].Fin, core.Cars[3].Fin });
        h.Tick(RallyCore.TimeoutTicks);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Room.Result!.Winners);
        int w = core.Cars[1].FinishMs, o = core.Cars[0].FinishMs, i = core.Cars[3].FinishMs;
        var best = new[] { 0, 1, 3 }.OrderBy(s => core.Cars[s].BestMs).ThenBy(s => s).First();
        var expected = $"Сільське ралі · Село, 3 кола: 🥇 Петро {Rally.Clock(w, 1)} · 🥈 Іван +{Rally.Gap(i - w)} · 🥉 Оля +{Rally.Gap(o - w)}"
            + $" · Ганна — без фінішу · найкраще коло — {Nicks[best]} {Rally.Clock(core.Cars[best].BestMs, 2)}";
        Assert.Equal(expected, LastJournal(h));
        Assert.Equal("0:41,3", Rally.Clock(41_399, 1));
        Assert.Equal("0:13,42", Rally.Clock(13_429, 2));
        Assert.Equal("1:05,00", Rally.Clock(65_000, 2));
        Assert.Equal("1,2", Rally.Gap(1_250));
    }

    [Fact]
    public void Leaving_mid_race_drops_the_car_and_the_race_continues()
    {
        var h = Table(3);
        Green(h);
        h.Tick(10);
        h.Leave("Петро");
        var core = Core(h);
        Assert.False(core.Cars[1].Present);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Contains(h.Outbox.OfType<Journal>(), j => j.Text == "Сільське ралі: Петро сходить з траси");
        h.Tick();
        var f = h.View(null).GetProperty("f");
        Assert.Equal(-1, f.GetProperty("c")[1 * Rally.Stride + 10].GetInt32());
        Assert.Equal(0, f.GetProperty("r")[1].GetInt32());
        h.Leave("Ганна");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        ReadyToFinish(core, 0, 2);
        h.Tick(5);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
    }

    [Fact]
    public void Leaving_when_everyone_else_finished_ends_the_race_now()
    {
        var h = Table(2);
        Green(h);
        ReadyToFinish(Core(h), 0, 2);
        h.Tick(4);
        Assert.Equal(1, Core(h).Cars[0].Fin);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        // усі пішли посеред гонки — «всі роз'їхались»
        var h2 = Table(2);
        h2.Tick(10);
        h2.Leave("Оля");
        h2.Leave("Петро");
        Assert.Contains(h2.Outbox.OfType<Journal>(), j => j.Text == "Сільське ралі: всі роз'їхались");
    }

    [Fact]
    public void Rematch_rotates_seats_keeps_cars_by_nick_and_rerolls_a_random_track()
    {
        var h = Table(3, new { track = "random", laps = "3" }, seed: 5, start: false);
        Assert.True(h.Act(0, "car", new { car = "moped" }).Ok);
        Assert.True(h.Act(1, "car", new { car = "viz" }).Ok);
        var lobby = h.View(null);
        Assert.True(lobby.GetProperty("random").GetBoolean());
        Assert.Equal(0, lobby.GetProperty("records").GetArrayLength());
        h.Start();
        var seen = new HashSet<string>();
        // Ганна машини не обирала — їй дісталась типова для її першого місця, і далі вона їде за нею
        var ganna = Rally.Cars[2].Id;
        for (var round = 0; round < 8; round++)
        {
            var game = Game(h);
            seen.Add(game.Track.Id);
            var core = Core(h);
            for (var s = 0; s < 3; s++)
            {
                var nick = h.NickOf(s);
                var car = nick == "Оля" ? "moped" : nick == "Петро" ? "viz" : ganna;
                Assert.Equal(car, core.Cars[s].Car);
                Assert.Equal(car, h.View(s).GetProperty("cars")[s].GetString());
            }
            // кінець — усі фінішували
            Green(h);
            for (var s = 0; s < 3; s++) { core.Cars[s].Fin = s + 1; core.Cars[s].Ghost = true; core.Finished = 3; }
            h.Tick();
            Assert.Equal(RoomStatus.Finished, h.Room.Status);
            var was = h.NickOf(0);
            Assert.True(h.Rematch().Ok);
            Assert.NotEqual(was, h.NickOf(0));
            Assert.Equal(1, Core(h).Cars[0].Next);
            Assert.Equal(0, Core(h).T);
        }
        Assert.True(seen.Count >= 3, $"траси: {string.Join(", ", seen)}");
        // конкретна траса — завжди та сама
        var fixedTrack = Table(2, new { track = "nich", laps = "5" });
        Assert.Equal("nich", Game(fixedTrack).Track.Id);
        Assert.Equal(5, fixedTrack.View(null).GetProperty("laps").GetInt32());
    }

    /// <summary>Усі n — на фініші (підсумок), не чекаючи таймауту.</summary>
    static void FinishAll(RoomHarness h, int n)
    {
        var core = Core(h);
        for (var s = 0; s < n; s++) { core.Cars[s].Fin = s + 1; core.Cars[s].Ghost = true; }
        core.Finished = n;
        h.Tick();
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
    }

    [Fact]
    public void A_finished_table_reopened_by_a_newcomer_is_a_fresh_lobby()
    {
        var h = Table(3, start: false);
        Assert.True(h.Act(2, "car", new { car = "viz" }).Ok);
        h.Start();
        Green(h);
        FinishAll(h, 3);
        Assert.Equal(Rally.PhOver, h.View(null).GetProperty("ph").GetInt32());
        // Ганна встала з-за дограного столу, на її місце сіла Нова — каркас відкрив стіл наново
        h.Leave("Ганна");
        Assert.True(h.Join("Нова").Ok);
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);
        var v = h.View(2);
        Assert.Equal(Rally.PhLobby, v.GetProperty("ph").GetInt32());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("results").ValueKind);
        var f = v.GetProperty("f");
        Assert.Equal(Rally.PhLobby, f.GetProperty("ph").GetInt32());
        Assert.Equal(0, f.GetProperty("t").GetInt32());
        Assert.Equal(RallyTracks.Get("selo").SlotX[2], f.GetProperty("c")[2 * Rally.Stride].GetInt32());
        // на місці Ганни — не її віз, а типова машина місця (Нова ще не обирала)
        Assert.Equal(Rally.Cars[2].Id, v.GetProperty("cars")[2].GetString());
        Assert.Null(Game(h).Core);
        // і машину обирати можна всім — і новенькій, і тим, хто лишився
        Assert.True(h.Act(2, "car", new { car = "kopiyka" }).Ok);
        Assert.True(h.Act(0, "car", new { car = "motoblok" }).Ok);
        Assert.Equal("kopiyka", h.View(null).GetProperty("cars")[2].GetString());
        // нова гонка — з нуля
        Assert.True(h.Start().Ok);
        Assert.Equal(Rally.PhCount, Game(h).Phase);
        Assert.Equal(0, Core(h).T);
        Assert.Equal("kopiyka", Core(h).Cars[2].Car);
        Assert.Equal("motoblok", Core(h).Cars[0].Car);
        Green(h);
        FinishAll(h, 3);
        Assert.Equal(2, h.Finished.Count);
        // дограна партія без новачків лишається на столі: картці результату є що показати
        Assert.Equal(Rally.PhOver, h.View(null).GetProperty("ph").GetInt32());
        Assert.Equal(3, h.View(null).GetProperty("results").GetArrayLength());
    }

    [Fact]
    public void Village_champion_needs_three_at_the_green_light_and_a_rival_to_the_end()
    {
        // двоє «статистів» встали ще на світлофорі — на зеленому лише Оля
        var h = Table(3);
        h.Tick(10);
        h.Leave("Петро");
        h.Leave("Ганна");
        Green(h);
        ReadyToFinish(Core(h), 0, 2);
        h.Tick(5);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:rally-win3");
        // троє рушили, але обоє суперників встали посеред гонки — перемагати нема кого
        var gone = Table(3);
        Green(gone);
        gone.Tick(10);
        gone.Leave("Петро");
        gone.Leave("Ганна");
        ReadyToFinish(Core(gone), 0, 2);
        gone.Tick(5);
        Assert.Equal(RoomStatus.Finished, gone.Room.Status);
        Assert.DoesNotContain(gone.Awards, a => a.Reason == "ach:rally-win3");
        // троє рушили, один встав, другий доїздив до кінця — чесна перемога
        var fair = Table(3);
        Green(fair);
        fair.Tick(10);
        fair.Leave("Петро");
        ReadyToFinish(Core(fair), 0, 2);
        fair.Tick(5);
        fair.Tick(RallyCore.TimeoutTicks);
        Assert.Equal(RoomStatus.Finished, fair.Room.Status);
        Assert.Contains(fair.Awards, a => a.Nick == "Оля" && a.Reason == "ach:rally-win3");
    }

    [Fact]
    public void Half_a_minute_without_any_input_ends_the_race()
    {
        // ніхто нічого не тисне: не 4 хвилини, а 30 секунд — і нічия, бо ніхто й перших воріт не взяв
        var h = Table(2);
        Green(h);
        h.Tick(Rally.IdleTicks - 1);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        h.Tick();
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Empty(h.Room.Result!.Winners);
        Assert.Equal("Сільське ралі · Село: пів хвилини ніхто не торкався керма — нічия", LastJournal(h));
        // ручник на місці — теж ввід: відлік тиші починається наново з тика, коли маска лягла
        var busy = Table(2);
        Green(busy);
        busy.Tick(700);
        Ctl(busy, 1, 16);
        busy.Tick(Rally.IdleTicks);
        Assert.Equal(RoomStatus.Playing, busy.Room.Status);
        busy.Tick();
        Assert.Equal(RoomStatus.Finished, busy.Room.Status);
        // хтось проїхав кілька воріт і відійшов — найдалі він, як і після стелі в 4 хвилини
        var far = Table(2);
        Green(far);
        Core(far).Cars[1].Next = 3;
        far.Tick(Rally.IdleTicks);
        Assert.Equal(RoomStatus.Finished, far.Room.Status);
        Assert.Equal([1], far.Room.Result!.Winners);
        Assert.Equal("Сільське ралі · Село: пів хвилини ніхто не торкався керма — найдалі Петро, 0 кіл з 3", LastJournal(far));
        // соло
        var solo = Table(1);
        Green(solo);
        solo.Tick(Rally.IdleTicks);
        Assert.Equal(RoomStatus.Finished, solo.Room.Status);
        Assert.Equal("Сільське ралі · Село, 3 кола: Оля наодинці з секундоміром — пів хвилини без керма, фінішу нема", LastJournal(solo));
    }

    [Fact]
    public void Car_choice_is_lobby_only_and_validated()
    {
        var h = Table(3, start: false);
        var v = h.View(null).GetProperty("cars");
        Assert.Equal(["traktor", "zapor", "moped", null, null, null], v.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Null ? null : e.GetString()));
        var ok = h.Act(2, "car", new { car = "kopiyka" });
        Assert.True(ok.Ok);
        Assert.Contains("«Копійка»", ok.Message);
        Assert.Equal("kopiyka", h.View(null).GetProperty("cars")[2].GetString());
        var before = Views.Text(h.View(null));
        var bad = h.Act(0, "car", new { car = "tesla" });
        Assert.Equal("Такої машини в селі нема", bad.Message);
        Assert.False(h.Act(0, "car", new { wheels = 4 }).Ok);
        Assert.False(h.Act(0, "car", "moped").Ok);
        Assert.Equal(before, Views.Text(h.View(null)));
        h.Start();
        var late = h.Act(0, "car", new { car = "moped" });
        Assert.Equal("Посеред гонки не пересідають", late.Message);
        Assert.Equal("kopiyka", Core(h).Cars[2].Car);
        Assert.Equal("Тут так не ходять", h.Act(0, "fly").Message);
    }

    [Fact]
    public void Late_inputs_apply_immediately_and_report_lateness()
    {
        var h = Table(2);
        Green(h);
        h.Tick(25);
        var core = Core(h);
        var T = core.T;
        Ctl(h, 0, 4, T - 5);
        Assert.Equal(4, core.Cars[0].Mask);
        h.Tick();
        var f = h.View(null).GetProperty("f").GetProperty("c");
        Assert.Equal(6, f[13].GetInt32());
        Assert.Equal(T - 5, f[14].GetInt32());
        Assert.True(core.Cars[0].VF > 0);
    }

    [Fact]
    public void Future_inputs_are_clamped_to_ten_ticks()
    {
        var h = Table(2);
        Green(h);
        var core = Core(h);
        var T = core.T;
        Ctl(h, 0, 4, T + 500);
        Assert.Equal(-(RallyCore.MaxAhead - 1), core.Cars[0].Lt);
        Assert.Equal(T + RallyCore.MaxAhead, core.Cars[0].It);
        h.Tick(RallyCore.MaxAhead - 1);
        Assert.Equal(0, core.Cars[0].Mask);
        h.Tick();
        Assert.Equal(4, core.Cars[0].Mask);
        // тик вводу не спадає: пізніша маска з меншим t лягає на той самий тик, а не раніше
        Ctl(h, 1, 4, core.T + 5);
        Ctl(h, 1, 8, core.T + 2);
        Assert.Equal(core.T + 5, core.Cars[1].It);
        h.Tick(4);
        Assert.Equal(0, core.Cars[1].Mask);
        h.Tick();
        Assert.Equal(8, core.Cars[1].Mask);
    }

    [Fact]
    public void Scheduled_input_applies_exactly_on_its_tick()
    {
        var h = Table(2);
        Green(h);
        var core = Core(h);
        var T = core.T;
        Ctl(h, 0, 4, T + 3);
        Assert.Equal(-2, core.Cars[0].Lt);
        h.Tick(2);
        Assert.Equal(0, core.Cars[0].Mask);
        Assert.Equal(0, core.Cars[0].VF);
        h.Tick();
        Assert.Equal(4, core.Cars[0].Mask);
        // рівно один тик газу з місця: 56 − 56·16/256 = 53
        Assert.Equal(53, core.Cars[0].VF);
    }

    [Fact]
    public void Garbage_control_payload_is_refused_and_changes_nothing()
    {
        var h = Table(2);
        Green(h);
        var before = Views.Text(h.View(null));
        var core = Core(h);
        var T = core.T;
        foreach (var bad in new object?[]
                 {
                     new { t = T + 1, k = 77 }, new { t = T + 1, k = -1 }, new { t = T + 1, k = "x" }, new { k = 4 }, new { t = "5", k = 4 },
                     new { t = 1.5, k = 4 }, new { t = T + 1 }, null, 4, "ctl", new { t = 99999999999L, k = 4 },
                 })
        {
            var r = h.Act(0, "ctl", bad);
            Assert.False(r.Ok);
            Assert.Equal("Кривий ввід", r.Message);
            h.Input(0, "ctl", bad);
        }
        Assert.Equal(before, Views.Text(h.View(null)));
        Assert.All(core.Cars[0].Sched, s => Assert.Equal(-1, s));
        // у лобі — ще нема куди
        var lobby = Table(2, start: false);
        Assert.Equal("Гонка ще не почалась", lobby.Act(0, "ctl", new { t = 1, k = 4 }).Message);
        Assert.Equal("Гонка ще не почалась", lobby.Act(0, "reset").Message);
    }

    [Fact]
    public void Horn_sets_the_event_bit_once_per_second()
    {
        var h = Table(2);
        Green(h);
        var core = Core(h);
        Assert.True(h.Act(1, "horn").Ok);
        Assert.Equal("Не сигналь так часто", h.Act(1, "horn").Message);
        h.Tick();
        Assert.NotEqual(0, core.Cars[1].Ev & RallyCore.EvHorn);
        h.Tick();
        Assert.Equal(0, core.Cars[1].Ev & RallyCore.EvHorn);
        h.Tick(RallyCore.HornCdTicks - 3);
        Assert.False(h.Act(1, "horn").Ok);
        h.Tick();
        Assert.True(h.Act(1, "horn").Ok);
    }

    [Fact]
    public void The_server_accepts_exactly_what_the_module_sends()
    {
        var js = File.ReadAllText(Path.Combine(RallyReplays.Root(), "web", "games", "rally.js"));
        // модуль шле саме такі payload-и (рядки скопійовано з rally.js)
        Assert.Contains("ctx.input('ctl', { t: t, k: k })", js);
        Assert.Contains("ctx.input('reset')", js);
        Assert.Contains("ctx.input('horn')", js);
        Assert.Contains("ctx.act('car', { car: id })", js);
        var h = Table(2, start: false);
        var id = "motoblok";
        Assert.True(h.Act(0, "car", new { car = id }).Ok);
        h.Start();
        Green(h);
        var core = Core(h);
        int t = core.T + 2, k = 4 | 2;
        h.Input(0, "ctl", new { t = t, k = k });
        Assert.Equal(k, core.Cars[0].Sched[t & 15]);
        h.Tick(2);
        Assert.Equal(k, core.Cars[0].Mask);
        h.Input(0, "horn");
        h.Tick();
        Assert.NotEqual(0, core.Cars[0].Ev & RallyCore.EvHorn);
        core.Cars[0].Next = 3;
        h.Input(0, "reset");
        Assert.Equal(RallyCore.StallTicks, core.Cars[0].Stall);
        Assert.Equal(id, core.Cars[0].Car);
    }

    [Fact]
    public void Win_with_three_or_more_awards_the_village_champion_achievement()
    {
        var h = Table(3);
        Green(h);
        ReadyToFinish(Core(h), 2, 2);
        h.Tick(5);
        h.Tick(RallyCore.TimeoutTicks);
        Assert.Equal([2], h.Room.Result!.Winners);
        Assert.Contains(h.Awards, a => a.Nick == "Ганна" && a.Reason == "ach:rally-win3" && a.Shards == 0);
        // на двох — ні
        var two = Table(2);
        Green(two);
        ReadyToFinish(Core(two), 0, 2);
        two.Tick(5);
        two.Tick(RallyCore.TimeoutTicks);
        Assert.Equal(RoomStatus.Finished, two.Room.Status);
        Assert.DoesNotContain(two.Awards, a => a.Reason == "ach:rally-win3");
        Assert.NotNull(AchievementCatalogEntry("rally-win3"));
        Assert.NotNull(AchievementCatalogEntry("rally-record"));
    }

    static object? AchievementCatalogEntry(string key) => Hlechyky.Games.Economy.AchievementCatalog.Get(key);

    [Fact]
    public void Beating_someone_elses_record_awards_the_record_achievement()
    {
        var store = new MemoryGameStore();
        var laps = new RallyLaps(store, a => a());
        laps.Post("selo", "Петро", "viz", 99_000, DateTimeOffset.UnixEpoch);
        var h = Table(2, services: RoomHarness.WithService(laps));
        Assert.Equal("Петро", h.View(null).GetProperty("records")[0].GetProperty("nick").GetString());
        Green(h);
        var core = Core(h);
        ReadyToFinish(core, 0, 0);
        h.Tick(4);
        Assert.Equal(1, core.Cars[0].Lap);
        Assert.Contains(h.Awards, a => a.Nick == "Оля" && a.Reason == "ach:rally-record");
        var records = h.View(null).GetProperty("records");
        Assert.Equal("Оля", records[0].GetProperty("nick").GetString());
        Assert.Equal("traktor", records[0].GetProperty("car").GetString());
        Assert.Equal(core.Cars[0].LastMs, records[0].GetProperty("ms").GetInt32());
        // своє ж коло, ще краще, — рекорд, але вже не «чужий»
        var n = h.Awards.Count;
        core.Cars[0].Next = 0;
        core.Put(0, At(19.3), At(3.5), 0, 800);
        h.Tick(4);
        Assert.Equal(2, core.Cars[0].Lap);
        Assert.Equal(n, h.Awards.Count(a => a.Reason == "ach:rally-record"));
        // наприкінці Глек каже, чий рекорд упав (навіть коли Оля потім покращила вже свій), і це ж — у Журналі
        h.Leave("Петро");
        ReadyToFinish(core, 0, 2);
        h.Tick(4);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var rec = laps.Top("selo")[0];
        Assert.Equal("Оля", rec.Nick);
        var line = $"Оля, {Rally.Clock(rec.Ms, 2)} (було — Петро, 1:39,00)";
        Assert.Contains(h.Outbox.OfType<TableSaid>(), s => s.Line.Text == $"⏱ Новий рекорд «Село»: {line}!");
        Assert.EndsWith($" · новий рекорд траси: {line}", LastJournal(h));
    }

    [Fact]
    public void RallyLaps_keeps_best_per_nick_top_20_and_persists_json()
    {
        var store = new MemoryGameStore();
        var laps = new RallyLaps(store, a => a());
        var t0 = DateTimeOffset.Parse("2026-09-27T10:00:00Z");
        Assert.Equal((1, false), laps.Post("selo", "Оля", "moped", 15_000, t0));
        Assert.Equal((2, false), laps.Post("selo", "Петро", "viz", 16_000, t0));
        Assert.Equal((0, false), laps.Post("selo", "оля", "moped", 15_500, t0));
        Assert.Equal((1, false), laps.Post("selo", "Оля", "zapor", 14_000, t0));
        Assert.Equal((1, true), laps.Post("selo", "Петро", "viz", 13_000, t0));
        // рівні мс — старіший вище
        Assert.Equal((3, false), laps.Post("selo", "Ганна", "traktor", 14_000, t0.AddMinutes(1)));
        Assert.Equal(["Петро", "Оля", "Ганна"], laps.Top("selo").Select(l => l.Nick));
        for (var i = 0; i < 30; i++) laps.Post("selo", $"гість{i}", "traktor", 20_000 + i, t0);
        Assert.Equal(RallyLaps.Keep, laps.Top("selo", 100).Count);
        Assert.Equal((0, false), laps.Post("selo", "Повільний", "viz", 90_000, t0));
        Assert.Empty(laps.Top("ozero"));
        Assert.Equal((0, false), laps.Post("нема", "Оля", "viz", 1, t0));

        var json = store.LoadState(RallyLaps.Key("selo"))!;
        using var doc = JsonDocument.Parse(json);
        var first = doc.RootElement[0];
        Assert.Equal("Петро", first.GetProperty("n").GetString());
        Assert.Equal(13_000, first.GetProperty("ms").GetInt32());
        Assert.Equal("viz", first.GetProperty("c").GetString());
        Assert.True(first.TryGetProperty("at", out _));

        var again = new RallyLaps(store, a => a());
        Assert.Equal(laps.Top("selo", 20).Select(l => (l.Nick, l.Ms)), again.Top("selo", 20).Select(l => (l.Nick, l.Ms)));
        // криве сховище не валить сервіс
        store.SaveState(RallyLaps.Key("ozero"), "{ це не json");
        Assert.Empty(new RallyLaps(store, a => a()).Top("ozero"));
        // через DI сервіс реєструється одним рядком
        var sp = new ServiceCollection().AddSingleton<IGameStore>(store);
        RallySetup.AddRally(sp);
        Assert.NotNull(sp.BuildServiceProvider().GetService<RallyLaps>());
    }

    // =============================================================================================
    // Види й кадри
    // =============================================================================================

    [Fact]
    public void Spectator_view_equals_player_view()
    {
        var h = Table(3);
        Green(h);
        h.Tick(30);
        var spectator = Views.Text(h.View(null));
        for (var s = 0; s < 3; s++) Assert.Equal(spectator, Views.Text(h.View(s)));
        // і в лобі
        var lobby = Table(2, start: false);
        Assert.Equal(Views.Text(lobby.View(null)), Views.Text(lobby.View(1)));
    }

    [Fact]
    public void View_carries_static_track_data_and_frame_carries_90_ints()
    {
        var h = Table(2);
        var v = h.View(0);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("turn").ValueKind);
        Assert.Equal(Rally.PhCount, v.GetProperty("ph").GetInt32());
        Assert.Equal(3, v.GetProperty("laps").GetInt32());
        var track = v.GetProperty("track");
        Assert.Equal("selo", track.GetProperty("id").GetString());
        Assert.Equal("Село", track.GetProperty("title").GetString());
        Assert.Equal(48, track.GetProperty("cols").GetInt32());
        Assert.Equal(27, track.GetProperty("rows").GetInt32());
        Assert.Equal(32, track.GetProperty("cell").GetInt32());
        Assert.False(track.GetProperty("night").GetBoolean());
        Assert.Equal(27, track.GetProperty("map").GetArrayLength());
        Assert.Equal(8, track.GetProperty("gates").GetArrayLength());
        Assert.Equal(20, track.GetProperty("gates")[0][0][0].GetInt32());
        Assert.Equal(8, track.GetProperty("gateA").GetArrayLength());
        Assert.Equal([656, 128], track.GetProperty("gateC")[0].EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(6, track.GetProperty("slots").GetArrayLength());
        Assert.Equal([0, 640, 1], track.GetProperty("line").EnumerateArray().Select(e => e.GetInt32()));
        var f = v.GetProperty("f");
        Assert.Equal(0, f.GetProperty("t").GetInt32());
        Assert.Equal(Rally.PhCount, f.GetProperty("ph").GetInt32());
        Assert.Equal(75, f.GetProperty("s").GetInt32());
        Assert.Equal(RallyCore.Seats * Rally.Stride, f.GetProperty("c").GetArrayLength());
        Assert.Equal(6, f.GetProperty("r").GetArrayLength());
        var c = f.GetProperty("c");
        Assert.Equal(19 * S + S / 2, c[0].GetInt32());
        Assert.Equal(2 * S + S / 2, c[1].GetInt32());
        Assert.Equal(1, c[7].GetInt32());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("results").ValueKind);
        // кадр — той самий, що й у виді
        h.Tick(5);
        var frame = h.Outbox.OfType<RoomFrame>().Last().Frame;
        Assert.Equal(Views.Text(frame), Views.Text(Views.Json(h.View(null)).GetProperty("f")));
    }

    [Fact]
    public void Frame_json_is_under_1536_bytes_with_six_cars()
    {
        var h = Table(6, new { track = "yarmarok", laps = "3" });
        var pilot = new RallyPilot(Core(h).Track, new Random(3)) { Drift = 100 };
        var biggest = 0;
        var total = 0L;
        var n = 0;
        var t0 = Core(h).T;
        Pilot(h, pilot, [0, 1, 2, 3, 4, 5], () =>
        {
            var size = Views.Text(Game(h).Frame()).Length;
            biggest = Math.Max(biggest, System.Text.Encoding.UTF8.GetByteCount(Views.Text(Game(h).Frame())));
            total += size;
            n++;
            return Core(h).T - t0 > 900;
        });
        output.WriteLine($"кадр на шістьох: у середньому {total / n} Б, найбільший {biggest} Б");
        Assert.True(biggest < 1536, $"кадр {biggest} Б");
    }

    [Fact]
    public void View_json_is_under_8_kb()
    {
        var laps = new RallyLaps(new MemoryGameStore(), a => a());
        for (var i = 0; i < 12; i++) laps.Post("kukurudza", $"Гонщик з довгим ніком {i}", "kopiyka", 30_000 + i * 777, DateTimeOffset.UnixEpoch);
        var h = Table(6, new { track = "kukurudza", laps = "7" }, services: RoomHarness.WithService(laps));
        Green(h);
        var size = System.Text.Encoding.UTF8.GetByteCount(Views.Text(h.View(null)));
        output.WriteLine($"вид: {size} Б");
        Assert.True(size < 8192, $"вид {size} Б");
        Assert.Equal(10, h.View(null).GetProperty("records").GetArrayLength());
    }

    [Fact]
    public void Absent_seats_are_zero_with_fin_minus_one()
    {
        var h = Table(2);
        Green(h);
        h.Tick(10);
        var c = h.View(null).GetProperty("f").GetProperty("c");
        for (var s = 2; s < 6; s++)
            for (var i = 0; i < Rally.Stride; i++)
                Assert.Equal(i == 10 ? -1 : 0, c[s * Rally.Stride + i].GetInt32());
        Assert.Equal(0, c[10].GetInt32());
        var r = h.View(null).GetProperty("f").GetProperty("r").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal([1, 2], r.Take(2).Order());
        Assert.Equal([0, 0, 0, 0], r.Skip(2));
        // у лобі — те саме, машини на слотах
        var lobby = Table(3, start: false).View(null).GetProperty("f");
        Assert.Equal(0, lobby.GetProperty("ph").GetInt32());
        Assert.Equal(-1, lobby.GetProperty("c")[3 * Rally.Stride + 10].GetInt32());
        Assert.Equal(RallyTracks.Get("selo").SlotX[2], lobby.GetProperty("c")[2 * Rally.Stride].GetInt32());
    }

    [Fact]
    public void Results_appear_only_in_over_phase()
    {
        var h = Table(2);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("results").ValueKind);
        Green(h);
        ReadyToFinish(Core(h), 1, 2);
        h.Tick(4);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("results").ValueKind);
        h.Tick(RallyCore.TimeoutTicks);
        var results = h.View(null).GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());
        var first = results[0];
        Assert.Equal(1, first.GetProperty("seat").GetInt32());
        Assert.Equal(1, first.GetProperty("fin").GetInt32());
        Assert.Equal(Core(h).Cars[1].FinishMs, first.GetProperty("ms").GetInt32());
        Assert.Equal(3, first.GetProperty("laps").GetInt32());
        Assert.Equal(Rally.PhOver, h.View(null).GetProperty("ph").GetInt32());
    }

    [Fact]
    public void Six_pilots_race_to_the_end_on_every_track()
    {
        foreach (var id in RallyTracks.Ids)
        {
            var h = Table(6, new { track = id, laps = "3" }, seed: 9);
            var pilot = new RallyPilot(Core(h).Track, new Random(9)) { Drift = 40 };
            var bumps = 0;
            Pilot(h, pilot, [0, 1, 2, 3, 4, 5], () =>
            {
                if (h.Room.Status != RoomStatus.Playing) return true;
                for (var s = 0; s < 6; s++) bumps += (Core(h).Cars[s].Ev & RallyCore.EvCar) != 0 ? 1 : 0;
                return false;
            }, 5000);
            Assert.Equal(RoomStatus.Finished, h.Room.Status);
            Assert.Single(h.Room.Result!.Winners);
            var finishers = Enumerable.Range(0, 6).Count(s => Core(h).Cars[s].Fin > 0);
            output.WriteLine($"{id}: фінішувало {finishers}/6, зіткнень {bumps}, {LastJournal(h)}");
            Assert.True(finishers >= 3, $"{id}: фінішувало лише {finishers}");
            Assert.True(bumps > 0, $"{id}: жодного зіткнення");
        }
    }

    // =============================================================================================
    // Швидкодія
    // =============================================================================================

    [Fact]
    [Trait("Category", "Perf")]
    public void Six_cars_3000_ticks_with_input_stay_under_a_second()
    {
        var h = Table(6, new { track = "yarmarok", laps = "7" });
        var game = Game(h);
        var core = Core(h);
        var rng = new Random(1);
        // прогрів JIT
        for (var i = 0; i < 200; i++) { game.Tick(); game.Frame(); }
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 3000; i++)
        {
            if (i % 7 == 0)
                for (var s = 0; s < 6; s++) core.Schedule(s, core.T + 1, 4 | rng.Next(4) | (rng.Next(6) == 0 ? 16 : 0));
            game.Tick();
            game.Frame();
        }
        sw.Stop();
        var us = sw.Elapsed.TotalMilliseconds * 1000 / 3000;
        output.WriteLine($"тик на шістьох із кадром: {us:0.00} мкс");
        // гонка ще йде: якби вона скінчилась, Tick() повертав би одразу, і число було б порожнім
        Assert.Equal(Rally.PhRace, game.Phase);
        Assert.Equal(3200, core.T);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"3000 тиків за {sw.ElapsedMilliseconds} мс");
        Assert.True(us < 250, $"тик {us:0.0} мкс");
    }
}
