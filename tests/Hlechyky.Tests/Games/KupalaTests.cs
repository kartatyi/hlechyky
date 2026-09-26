using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Купальська ніч: мапу, крок, світло й мозок ботів перевіряємо на голому <see cref="KupalaCore"/> (там селянина
/// можна поставити рівно туди, куди треба), а раунди, очки, дії й приховане — через справжню кімнату
/// (<see cref="RoomHarness"/>). Клас у серійній колекції через перф-тест.
/// </summary>
[Collection(SerialPerf.Name)]
public class KupalaTests(ITestOutputHelper output)
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марта", "Юрко", "Соня", "Богдан"];

    // ---------- підмостки ----------

    static RoomHarness Table(int players = 2, int seed = 42, object? options = null)
    {
        var h = new RoomHarness("kupala", options, seed: seed);
        foreach (var nick in Nicks.Take(players)) h.Join(nick);
        h.Start();
        return h;
    }

    static Kupala G(RoomHarness h) => (Kupala)h.Room.Game;
    static KupalaCore Core(RoomHarness h) => G(h).CoreForTests;
    static KupalaSeat S(RoomHarness h, int seat) => G(h).SeatForTests(seat);
    static KupalaVillager Me(RoomHarness h, int seat) => Core(h).V[S(h, seat).Me];

    static void Go(RoomHarness h)
    {
        for (var i = 0; i < 200 && G(h).Phase != Kupala.PhaseGo; i++) h.Tick();
        Assert.Equal(Kupala.PhaseGo, G(h).Phase);
    }

    static KupalaVillager Put(KupalaVillager v, int cx, int cy, int ox = 0, int oy = 0, int dir = 0)
    {
        v.X = cx * KupalaMap.Cell + KupalaMap.Cell / 2 + ox;
        v.Y = cy * KupalaMap.Cell + KupalaMap.Cell / 2 + oy;
        v.Dir = dir;
        v.Want = -1;
        v.Moving = false;
        v.Blocked = false;
        return v;
    }

    static KupalaCore Bare(int bots = 3, int seed = 1)
    {
        var core = new KupalaCore(new Random(seed));
        core.Deal([0], bots);
        foreach (var v in core.V)
        {
            KupalaCore.Forget(v);
            v.Stand = 0;
        }
        return core;
    }

    /// <summary>Гасимо все, крім вогнищ: світлячки сплять, зарниці й папороті не буде.</summary>
    static void Dark(KupalaCore core)
    {
        for (var i = 0; i < KupalaCore.FlyCount; i++)
        {
            core.FlyOn[i] = false;
            core.FlyLeft[i] = 1_000_000;
        }
        core.SkyNext = int.MaxValue;
        core.Sky = 0;
        core.FernAt = int.MaxValue;
    }

    /// <summary>Усі, крім переданих, стоять у лісі (там темно) й нікуди не хочуть.</summary>
    static void Park(RoomHarness h, params int[] keep)
    {
        var i = 0;
        foreach (var v in Core(h).V)
        {
            if (Array.IndexOf(keep, v.Id) >= 0) continue;
            var cell = KupalaMap.FernCells[i++ % KupalaMap.FernCells.Length];
            Put(v, cell % KupalaMap.W, cell / KupalaMap.W);
            v.Stand = 10_000;
            v.Wander = 0;
        }
    }

    /// <summary>Темний куток лугу — ряд 11, колонки 7…10: жодне вогнище туди не дістає.</summary>
    const int DarkRow = 11;
    /// <summary>Світле місце — клітинка (13, 8) над вогнищем (13, 9).</summary>
    const int LitX = 13, LitY = 8;

    static KupalaVillager AtSpot(RoomHarness h, int seat, int spot)
    {
        var s = KupalaMap.Spots[spot];
        return Put(Me(h, seat), s.X, s.Y, dir: 1);
    }

    static void Launch(RoomHarness h, int seat, int spot)
    {
        AtSpot(h, seat, spot);
        Assert.True(h.Act(seat, "launch", new { spot }).Ok, h.Reply.Message);
        h.Tick(Kupala.LaunchTicks);
    }

    static string FrameText(RoomHarness h) => Views.Text(G(h).Frame());

    static JsonElement LastFrame(RoomHarness h) => Views.Json(((RoomFrame)h.Outbox.Last(o => o is RoomFrame)).Frame);

    static List<int> FrameIds(JsonElement f)
    {
        var v = f.GetProperty("v").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        var ids = new List<int>();
        for (var i = 0; i < v.Length; i += 5) ids.Add(v[i]);
        return ids;
    }

    static List<int[]> Events(RoomHarness h) =>
        h.Outbox.OfType<RoomFrame>().SelectMany(f => Views.Json(f.Frame).GetProperty("ev").EnumerateArray()
            .Select(e => e.EnumerateArray().Select(x => x.GetInt32()).ToArray())).ToList();

    // =============================================================================================
    // Мапа
    // =============================================================================================

    [Fact]
    public void Map_is_30_by_20_with_343_walkable_cells_all_reachable_from_each_other()
    {
        Assert.Equal(20, KupalaMap.Rows.Length);
        Assert.All(KupalaMap.Rows, r => Assert.Equal(30, r.Length));
        Assert.Equal(343, KupalaMap.Walkable.Length);
        Assert.Equal(64, KupalaMap.Names.Distinct().Count());
        var hops = KupalaMap.NextHop;
        foreach (var target in KupalaMap.Walkable)
            foreach (var cell in KupalaMap.Walkable)
            {
                var hop = hops[target * KupalaMap.Cells + cell];
                if (cell == target) Assert.Equal(KupalaMap.NoHop, hop);
                else Assert.InRange(hop, 0, 3);
            }
    }

    [Fact]
    public void Eight_spots_sit_on_the_bank_right_above_the_river()
    {
        Assert.Equal(8, KupalaMap.Spots.Length);
        foreach (var s in KupalaMap.Spots)
        {
            Assert.Equal(KupalaMap.BankRow, s.Y);
            Assert.Equal('w', KupalaMap.Rows[s.Y + 1][s.X]);
            Assert.Equal(s.I, KupalaMap.SpotOf[s.Cell]);
            Assert.True(KupalaMap.Pass[s.Cell]);
        }
        Assert.Equal(8, KupalaMap.LeftSpots.Concat(KupalaMap.MidSpots).Concat(KupalaMap.RightSpots).Distinct().Count());
        // вінки пливуть по воді одразу під берегом
        Assert.True(KupalaMap.WaterAt(100, KupalaMap.WreathY));
        Assert.False(KupalaMap.WaterAt(100, KupalaMap.WreathY - 16));
    }

    [Fact]
    public void Fire_rings_and_fern_cells_are_walkable_and_the_forest_is_dark()
    {
        Assert.Equal(4, KupalaMap.Fires.Length);
        foreach (var ring in KupalaMap.FireRing)
        {
            Assert.NotEmpty(ring);
            Assert.All(ring, c => Assert.True(KupalaMap.Pass[c] && KupalaMap.SpotOf[c] < 0));
        }
        Assert.Equal(73, KupalaMap.FernCells.Length);
        Assert.All(KupalaMap.FernCells, c => Assert.InRange(c / KupalaMap.W, KupalaMap.ForestFrom, KupalaMap.ForestTo));
        // ліс і більша частина берега — у темряві: вогнища туди не дістають
        var core = Bare(0);
        Dark(core);
        core.BuildLights();
        Assert.All(KupalaMap.FernCells, c => Assert.False(core.LitAt(KupalaMap.CenterX(c), KupalaMap.CenterY(c)), $"клітинка {c}"));
        var litSpots = KupalaMap.Spots.Count(s => core.LitAt(KupalaMap.CenterX(s.Cell), KupalaMap.CenterY(s.Cell)));
        Assert.Equal(1, litSpots);
    }

    [Fact]
    public void Next_hop_table_leads_to_the_target_within_bfs_distance_steps()
    {
        var rng = new Random(7);
        var walk = KupalaMap.Walkable;
        for (var pair = 0; pair < 50; pair++)
        {
            var from = walk[rng.Next(walk.Length)];
            var to = walk[rng.Next(walk.Length)];
            var dist = Bfs(from)[to];
            var cell = from;
            var steps = 0;
            while (cell != to)
            {
                var hop = KupalaMap.NextHop[to * KupalaMap.Cells + cell];
                Assert.InRange(hop, 0, 3);
                cell = (cell / KupalaMap.W + KupalaCore.DY[hop]) * KupalaMap.W + cell % KupalaMap.W + KupalaCore.DX[hop];
                Assert.True(KupalaMap.Pass[cell]);
                steps++;
                Assert.True(steps <= dist, "шлях довший за BFS");
            }
            Assert.Equal(dist, steps);
        }
    }

    static int[] Bfs(int from)
    {
        var d = Enumerable.Repeat(-1, KupalaMap.Cells).ToArray();
        var q = new Queue<int>();
        d[from] = 0;
        q.Enqueue(from);
        while (q.Count > 0)
        {
            var u = q.Dequeue();
            for (var k = 0; k < 4; k++)
            {
                int x = u % KupalaMap.W + KupalaCore.DX[k], y = u / KupalaMap.W + KupalaCore.DY[k];
                var n = y * KupalaMap.W + x;
                if (x < 0 || y < 0 || x >= KupalaMap.W || y >= KupalaMap.H || !KupalaMap.Pass[n] || d[n] >= 0) continue;
                d[n] = d[u] + 1;
                q.Enqueue(n);
            }
        }
        return d;
    }

    // =============================================================================================
    // Рух — один для всіх
    // =============================================================================================

    [Fact]
    public void A_bot_and_a_player_given_the_same_want_walk_the_same_path()
    {
        var core = Bare(1);
        var player = core.V.Single(v => v.Owner == 0);
        var bot = core.V.Single(v => v.Owner < 0);
        Put(player, 8, 11, 2, -3);
        Put(bot, 8, 11, 2, -3);
        var script = new[] { 0, 0, 1, 0, 3, 2, 1, 1, 0, -1 };
        var a = new List<(int, int, int, bool)>();
        var b = new List<(int, int, int, bool)>();
        for (var t = 0; t < 100; t++)
        {
            player.Want = bot.Want = script[t / 10];
            KupalaCore.Step(player);
            KupalaCore.Step(bot);
            a.Add((player.X, player.Y, player.Dir, player.Moving));
            b.Add((bot.X, bot.Y, bot.Dir, bot.Moving));
        }
        Assert.Equal(a, b);
        Assert.Equal(player.State, bot.State);
    }

    [Fact]
    public void Everyone_moves_exactly_three_units_or_not_at_all()
    {
        var h = Table(4, seed: 3);
        var rng = new Random(5);
        var core = Core(h);
        var was = core.V.Select(v => (v.X, v.Y)).ToArray();
        for (var t = 0; t < 600; t++)
        {
            if (t % 7 == 0)
                for (var s = 0; s < 4; s++) h.Input(s, "move", new { dir = rng.Next(-1, 4) });
            h.Tick();
            if (G(h).Phase is not (Kupala.PhaseStart or Kupala.PhaseGo)) break;
            foreach (var v in core.V)
            {
                int dx = Math.Abs(v.X - was[v.Id].X), dy = Math.Abs(v.Y - was[v.Id].Y);
                Assert.True((dx == 0 && dy == 0) || (dx == 3 && dy == 0) || (dx == 0 && dy == 3), $"id {v.Id}: {dx},{dy}");
                Assert.Equal(dx + dy > 0, v.Moving);
                was[v.Id] = (v.X, v.Y);
            }
        }
    }

    [Fact]
    public void A_villager_never_overlaps_a_tree_a_fire_or_the_river_with_its_box()
    {
        var h = Table(8, seed: 11, options: new { folk = "big" });
        var rng = new Random(9);
        for (var t = 0; t < 3000 && G(h).Phase != Kupala.PhaseOver; t++)
        {
            if (t % 5 == 0)
                for (var s = 0; s < 8; s++) h.Input(s, "move", new { dir = rng.Next(-1, 4) });
            h.Tick();
            foreach (var v in Core(h).V) Assert.True(KupalaMap.BoxFits(v.X, v.Y), $"id {v.Id} у ({v.X},{v.Y})");
        }
    }

    [Fact]
    public void Busy_stunned_fallen_and_dead_villagers_do_not_move_whatever_they_want()
    {
        var core = Bare(3);
        var p = core.V.Single(v => v.Owner == 0);
        var bots = core.V.Where(v => v.Owner < 0).ToArray();
        Put(p, 8, 11);
        Put(bots[0], 9, 11);
        Put(bots[1], 10, 11);
        Put(bots[2], 7, 11);
        p.Busy = 5;
        bots[0].Fallen = 5;
        bots[1].Stun = 5;
        bots[2].Dead = true;
        foreach (var v in core.V) v.Want = 3;
        for (var i = 0; i < 5; i++)
        {
            core.StepAll();
            Assert.All(core.V, v => Assert.False(v.Moving));
        }
        Assert.Equal(11 * 32 + 16, p.Y);
        Assert.Equal((2, 0, 3), (bots[0].State, bots[1].State, bots[2].State));
        p.Busy = 0;
        bots[1].Stun = 0;
        core.StepAll();
        Assert.Equal(11 * 32 + 13, p.Y);
        Assert.Equal(11 * 32 + 13, bots[1].Y);
    }

    // =============================================================================================
    // Боти
    // =============================================================================================

    [Fact]
    public void Bots_warm_up_by_the_fires_but_also_roam_the_dark()
    {
        var core = new KupalaCore(new Random(4));
        core.Deal([], 30);
        core.Night = true;
        Dark(core);
        long lit = 0, all = 0;
        var seenLit = new bool[core.N];
        var seenDark = new bool[core.N];
        for (var t = 0; t < 4000; t++)
        {
            core.T = t;
            core.TimersAll();
            foreach (var id in core.Done) { var v = core.V[id]; if (v.BusyWhat is >= 0 and < 8) core.Launch(v.BusyWhat); v.BusyWhat = -1; }
            core.LightsTick();
            core.ThinkAll();
            core.StepAll();
            core.ComputeLit(false);
            core.Events.Clear();
            if (t < 300) continue;
            for (var i = 0; i < core.N; i++)
            {
                all++;
                if (core.Lit[i]) { lit++; seenLit[i] = true; }
                else seenDark[i] = true;
            }
        }
        var share = lit * 100 / all;
        output.WriteLine($"боти у світлі {share}% часу");
        Assert.InRange(share, 25, 75);
        Assert.True(seenLit.Count(x => x) >= core.N * 9 / 10, "майже кожен бот хоч раз гріється біля вогнища");
        Assert.True(seenDark.Count(x => x) >= core.N * 9 / 10, "і майже кожен хоч раз іде в темряву");
    }

    [Fact]
    public void Bots_launch_wreaths_now_and_then_exactly_as_long_as_a_player_does()
    {
        var core = new KupalaCore(new Random(6));
        core.Deal([], 30);
        core.Night = true;
        var launches = 0;
        var standAt = new int[core.N];
        for (var t = 0; t < 20000; t++)
        {
            core.TimersAll();
            foreach (var id in core.Done)
            {
                var v = core.V[id];
                Assert.InRange(v.BusyWhat, 0, 7);
                Assert.Equal(v.BusyWhat, KupalaCore.SpotAt(v));
                Assert.Equal(1, v.Dir);                          // обличчям до води, як гравець
                Assert.Equal(KupalaCore.BusyTicks - 1, t - standAt[id]);   // від першого тика стояння — 24, як у гравця
                core.Launch(v.BusyWhat);
                v.BusyWhat = -1;
                launches++;
            }
            core.ThinkAll();
            foreach (var v in core.V)
                if (v.Busy == KupalaCore.BusyTicks - 1 && v.BusyWhat >= 0) standAt[v.Id] = t;
            core.StepAll();
            core.Events.Clear();
        }
        output.WriteLine($"ботів-вінків за 800 с на 30 ботах: {launches}");
        Assert.InRange(launches, 30, 400);
    }

    [Fact]
    public void Bots_now_and_then_slap_a_bot_or_whiff_but_never_touch_a_player()
    {
        var core = new KupalaCore(new Random(8));
        core.Deal([0, 1, 2, 3], 30);
        core.Night = true;
        var slaps = new List<int[]>();
        for (var t = 0; t < 40000; t++)
        {
            core.TimersAll();
            core.ThinkAll();
            foreach (var e in core.Events.Where(e => e[0] == 1))
            {
                slaps.Add(e);
                var a = core.V[e[1]];
                Assert.True(a.Owner < 0);
                Assert.NotEqual(1, e[3]);                          // гравців бот не чіпає
                if (e[3] == 0)
                {
                    Assert.Equal(KupalaCore.StunTicks, a.Stun);      // отетерів, як гравець
                    Assert.Equal(KupalaCore.FallTicks, core.V[e[2]].Fallen);
                }
            }
            core.Events.Clear();
            core.StepAll();
        }
        output.WriteLine($"ляпасів ботів за 1600 с: {slaps.Count} (влучних {slaps.Count(e => e[3] == 0)})");
        Assert.InRange(slaps.Count, 10, 80);
        Assert.Contains(slaps, e => e[3] == 2);
        Assert.All(core.V.Where(v => v.Owner >= 0), v => Assert.False(v.Dead));
    }

    [Fact]
    public void A_slapped_bot_lies_seventy_five_ticks_then_walks_again()
    {
        var h = Table(2, seed: 12);
        Go(h);
        var me = Me(h, 0);
        var bot = Core(h).V.First(v => v.Owner < 0);
        Park(h, me.Id, bot.Id);
        Put(me, 8, DarkRow, dir: 0);
        Put(bot, 9, DarkRow);
        Assert.True(h.Act(0, "slap", new { }).Ok, h.Reply.Message);
        Assert.Equal(2, bot.State);
        h.Tick(KupalaCore.FallTicks - 1);
        Assert.Equal(2, bot.State);
        h.Tick();
        Assert.True(bot.Upright);
        var at = (bot.X, bot.Y);
        h.Tick(400);
        Assert.NotEqual(at, (bot.X, bot.Y));
    }

    // =============================================================================================
    // Світло
    // =============================================================================================

    [Fact]
    public void Only_villagers_standing_in_light_are_in_the_frame()
    {
        var h = Table(4, seed: 14);
        Go(h);
        for (var round = 0; round < 6; round++)
        {
            h.Tick(97);
            var f = LastFrame(h);
            var l = f.GetProperty("l").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            var sky = f.GetProperty("sky").GetInt32();
            var expected = Core(h).V.Where(v => sky > 0 || Enumerable.Range(0, l.Length / 4)
                .Any(i => (long)(v.X - l[i * 4 + 1]) * (v.X - l[i * 4 + 1]) + (long)(v.Y - l[i * 4 + 2]) * (v.Y - l[i * 4 + 2]) <= (long)l[i * 4 + 3] * l[i * 4 + 3]))
                .Select(v => v.Id).ToList();
            Assert.Equal(expected, FrameIds(f));
            if (sky == 0) Assert.InRange(expected.Count, 1, Core(h).N - 1);
        }
    }

    [Fact]
    public void A_player_in_the_dark_vanishes_from_the_frame_even_for_himself_and_me_has_no_coordinates()
    {
        var h = Table(2, seed: 16);
        Go(h);
        Dark(Core(h));
        var me = Me(h, 0);
        Park(h, me.Id);
        Put(me, 8, DarkRow);
        h.Tick();
        Assert.DoesNotContain(me.Id, FrameIds(LastFrame(h)));
        var view = h.View(0);
        Assert.DoesNotContain(me.Id, FrameIds(view));
        var mine = view.GetProperty("me");
        Assert.Equal(["id", "list", "done", "torches", "cool", "launchCool", "stun", "busy", "fern", "alive"], mine.EnumerateObject().Select(p => p.Name));
        // у світлі — є, як і всі
        Put(me, LitX, LitY);
        h.Tick();
        var f = LastFrame(h);
        var ids = FrameIds(f);
        Assert.Contains(me.Id, ids);
        var v = f.GetProperty("v").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        var at = ids.IndexOf(me.Id) * 5;
        Assert.Equal((me.X, me.Y), (v[at + 1], v[at + 2]));
    }

    [Fact]
    public void Fires_flicker_without_randomness_and_the_frame_carries_the_radius_used()
    {
        var radii = new HashSet<int>();
        for (var t = 0; t < 96; t++)
            for (var k = 0; k < 4; k++)
            {
                var r = KupalaCore.FireRadius(k, t);
                Assert.InRange(r, KupalaCore.FireR - 6, KupalaCore.FireR + 6);
                radii.Add(r);
            }
        Assert.True(radii.Count >= 8);
        var h = Table(2, seed: 18);
        Go(h);
        h.Tick(5);
        var f = LastFrame(h);
        var t0 = f.GetProperty("t").GetInt32();
        var l = f.GetProperty("l").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        for (var k = 0; k < 4; k++)
        {
            Assert.Equal(KupalaCore.LFire, l[k * 4]);
            Assert.Equal((KupalaMap.Fires[k].X, KupalaMap.Fires[k].Y), (l[k * 4 + 1], l[k * 4 + 2]));
            Assert.Equal(KupalaCore.FireRadius(k, t0), l[k * 4 + 3]);
        }
    }

    [Fact]
    public void Summer_lightning_shows_everyone_for_twelve_ticks()
    {
        var h = Table(3, seed: 20);
        Go(h);
        var core = Core(h);
        Dark(core);
        Park(h);
        h.Tick();
        Assert.True(FrameIds(LastFrame(h)).Count < core.N);
        core.SkyNext = core.NightT + 1;
        var counts = new List<int>();
        for (var i = 0; i < 14; i++)
        {
            h.Tick();
            counts.Add(FrameIds(LastFrame(h)).Count);
        }
        Assert.Equal(12, counts.Count(c => c == core.N));
        Assert.Equal(core.N, counts[0]);
        Assert.True(counts[^1] < core.N);
        Assert.InRange(core.SkyNext - core.NightT, KupalaCore.SkyEveryMin - 14, KupalaCore.SkyEveryMax);
    }

    [Fact]
    public void A_torch_lands_four_cells_ahead_burns_six_seconds_and_everyone_sees_where_it_flew_from()
    {
        var h = Table(2, seed: 22);
        Go(h);
        var core = Core(h);
        Dark(core);
        var me = Me(h, 0);
        var bot = core.V.First(v => v.Owner < 0);
        Park(h, me.Id, bot.Id);
        Put(me, 8, DarkRow, dir: 2);
        Put(bot, 4, DarkRow);
        bot.Stand = 10_000;
        h.Tick();
        Assert.DoesNotContain(bot.Id, FrameIds(LastFrame(h)));
        Assert.True(h.Act(0, "torch", new { }).Ok, h.Reply.Message);
        h.Tick();
        var f = LastFrame(h);
        Assert.Contains(bot.Id, FrameIds(f));
        Assert.DoesNotContain(me.Id, FrameIds(f));
        var ev = f.GetProperty("ev").EnumerateArray().Single().EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal([3, -1, 8 * 32 + 16, DarkRow * 32 + 16, 8 * 32 + 16 - KupalaCore.TorchDist, DarkRow * 32 + 16, 0], ev);
        Assert.Equal(1, S(h, 0).Torches);
        h.Tick(KupalaCore.TorchTicks - 3);
        Assert.Contains(bot.Id, FrameIds(LastFrame(h)));
        h.Tick(3);
        Assert.DoesNotContain(bot.Id, FrameIds(LastFrame(h)));
        Assert.Empty(core.Torches);
    }

    [Fact]
    public void A_torch_thrown_into_the_river_hisses_out()
    {
        var h = Table(2, seed: 24);
        Go(h);
        AtSpot(h, 0, 1);
        Assert.True(h.Act(0, "torch", new { }).Ok, h.Reply.Message);
        h.Tick();
        Assert.Empty(Core(h).Torches);
        var ev = Events(h).Single(e => e[0] == 3);
        Assert.Equal(1, ev[6]);
    }

    [Fact]
    public void A_wreath_floats_downstream_and_its_candle_lights_the_one_who_launched_it()
    {
        var h = Table(2, seed: 26);
        Go(h);
        var core = Core(h);
        Dark(core);
        var me = Me(h, 0);
        Park(h, me.Id);
        AtSpot(h, 0, 1);
        h.Tick();
        Assert.DoesNotContain(me.Id, FrameIds(LastFrame(h)));
        Assert.True(h.Act(0, "launch", new { }).Ok, h.Reply.Message);
        h.Tick(Kupala.LaunchTicks);
        var f = LastFrame(h);
        Assert.Contains(me.Id, FrameIds(f));
        Assert.Contains(f.GetProperty("ev").EnumerateArray(), e => e[0].GetInt32() == 2 && e[1].GetInt32() == 1);
        var w = Assert.Single(core.Wreaths);
        Assert.Equal(KupalaMap.WreathY, w.Y);
        var x0 = w.X;
        h.Tick(60);
        Assert.Equal(x0 + 60 * KupalaCore.WreathSpeed, core.Wreaths[0].X);
        Assert.DoesNotContain(me.Id, FrameIds(LastFrame(h)));
    }

    [Fact]
    public void A_firefly_lights_whoever_it_flies_over()
    {
        var h = Table(2, seed: 28);
        Go(h);
        var core = Core(h);
        Dark(core);
        var bot = core.V.First(v => v.Owner < 0);
        Park(h, bot.Id);
        Put(bot, 8, DarkRow);
        bot.Stand = 10_000;
        h.Tick();
        Assert.DoesNotContain(bot.Id, FrameIds(LastFrame(h)));
        core.FlyOn[0] = true;
        core.FlyX[0] = bot.X - 10;
        core.FlyY[0] = bot.Y;
        h.Tick();
        Assert.Contains(bot.Id, FrameIds(LastFrame(h)));
        var l = LastFrame(h).GetProperty("l").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Contains(Enumerable.Range(0, l.Length / 4), i => l[i * 4] == KupalaCore.LFly && l[i * 4 + 3] == KupalaCore.FlyR);
    }

    [Fact]
    public void Dusk_look_around_and_dawn_reveal_show_everyone()
    {
        var h = Table(2, seed: 30, options: new { rounds = "1" });
        h.Tick();
        Assert.Equal(Kupala.PhaseStart, G(h).Phase);
        Assert.Equal(Core(h).N, FrameIds(LastFrame(h)).Count);
        Go(h);
        Park(h);
        Dark(Core(h));
        h.Tick();
        Assert.True(FrameIds(LastFrame(h)).Count < Core(h).N);
        h.Tick(Kupala.RoundTicks);
        Assert.Equal(Kupala.PhaseReveal, G(h).Phase);
        h.Tick(Kupala.RevealFrameEvery);
        Assert.Equal(Core(h).N, FrameIds(LastFrame(h)).Count);
        h.Tick(Kupala.RevealTicks);
        Assert.Equal(Kupala.PhaseOver, G(h).Phase);
        Assert.Equal(Core(h).N, FrameIds(h.View(null)).Count);
    }

    // =============================================================================================
    // Раунд і фази
    // =============================================================================================

    [Fact]
    public void Match_starts_with_three_seconds_of_dusk_where_slaps_torches_and_wreaths_are_refused()
    {
        var h = Table(2, seed: 32);
        Assert.Equal(Kupala.PhaseStart, G(h).Phase);
        foreach (var a in new[] { "slap", "torch", "launch" })
        {
            Assert.False(h.Act(0, a, new { }).Ok);
            Assert.Equal("Зачекай, сонце ще не сіло", h.Reply.Message);
        }
        var me = Me(h, 0);
        Put(me, 8, DarkRow);
        h.Input(0, "move", new { dir = 0 });
        h.Tick(3);
        Assert.Equal(8 * 32 + 16 + 9, me.X);                // ходити в сутінках можна
        h.Tick(Kupala.StartTicks - 3);
        Assert.Equal(Kupala.PhaseGo, G(h).Phase);
        Assert.Equal(Kupala.RoundTicks, G(h).Left);
    }

    [Fact]
    public void Round_lasts_ninety_seconds_then_the_alive_player_with_most_wreaths_wins()
    {
        var h = Table(3, seed: 34);
        Go(h);
        Launch(h, 1, S(h, 1).List[0]);
        h.Tick(Kupala.RoundTicks - Kupala.LaunchTicks - 1);
        Assert.Equal(Kupala.PhaseGo, G(h).Phase);
        h.Tick();
        Assert.Equal(Kupala.PhaseReveal, G(h).Phase);
        var r = h.View(null).GetProperty("reveal");
        Assert.Equal("time", r.GetProperty("why").GetString());
        Assert.Equal([1], r.GetProperty("winners").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(Kupala.PtWreath + Kupala.PtRound, S(h, 1).Total);
    }

    [Fact]
    public void Timeout_with_equal_wreaths_gives_a_round_without_a_winner()
    {
        var h = Table(2, seed: 36);
        Go(h);
        h.Tick(Kupala.RoundTicks);
        var r = h.View(null).GetProperty("reveal");
        Assert.Equal("none", r.GetProperty("why").GetString());
        Assert.Empty(r.GetProperty("winners").EnumerateArray());
    }

    [Fact]
    public void Launching_all_three_wreaths_ends_the_round_at_once_with_plus_three()
    {
        var h = Table(2, seed: 38);
        Go(h);
        var list = S(h, 0).List.ToArray();
        Launch(h, 0, list[0]);
        h.Tick(Kupala.LaunchCoolTicks);
        Launch(h, 0, list[1]);
        h.Tick(Kupala.LaunchCoolTicks);
        Assert.Equal(Kupala.PhaseGo, G(h).Phase);
        Launch(h, 0, list[2]);
        Assert.Equal(Kupala.PhaseReveal, G(h).Phase);
        Assert.Equal("list", h.View(null).GetProperty("reveal").GetProperty("why").GetString());
        Assert.Equal(3 * Kupala.PtWreath + Kupala.PtRound, S(h, 0).Total);
    }

    [Fact]
    public void Last_player_standing_wins_the_round()
    {
        var h = Table(3, seed: 40);
        Go(h);
        Dark(Core(h));
        Park(h, Me(h, 0).Id, Me(h, 1).Id, Me(h, 2).Id);
        Put(Me(h, 0), 8, DarkRow, dir: 0);
        Put(Me(h, 1), 9, DarkRow);
        Assert.True(h.Act(0, "slap", new { }).Ok, h.Reply.Message);
        h.Tick(Kupala.SlapCoolTicks);
        Put(Me(h, 0), 8, DarkRow, dir: 0);
        Put(Me(h, 2), 9, DarkRow);
        Assert.True(h.Act(0, "slap", new { }).Ok, h.Reply.Message);
        h.Tick();
        Assert.Equal(Kupala.PhaseReveal, G(h).Phase);
        var r = h.View(null).GetProperty("reveal");
        Assert.Equal("last", r.GetProperty("why").GetString());
        Assert.Equal(2 * Kupala.PtKill + Kupala.PtRound, S(h, 0).Total);
    }

    [Fact]
    public void Reveal_lasts_six_seconds_freezes_everyone_and_then_the_next_round_starts_fresh()
    {
        var h = Table(2, seed: 42);
        Go(h);
        AtSpot(h, 0, 1);
        h.Act(0, "torch", new { });
        var ids = (S(h, 0).Me, S(h, 1).Me);
        var lists = (S(h, 0).List.ToArray(), S(h, 1).List.ToArray());
        h.Tick(Kupala.RoundTicks);
        Assert.Equal(Kupala.PhaseReveal, G(h).Phase);
        var at = Core(h).V.Select(v => (v.X, v.Y)).ToArray();
        h.Input(0, "move", new { dir = 0 });
        h.Tick(Kupala.RevealTicks - 1);
        Assert.Equal(at, Core(h).V.Select(v => (v.X, v.Y)).ToArray());
        h.Tick();
        Assert.Equal(Kupala.PhaseStart, G(h).Phase);
        Assert.Equal(2, G(h).RoundNo);
        Assert.Equal(Kupala.TorchesPerRound, S(h, 0).Torches);
        Assert.True(S(h, 0).Alive && S(h, 1).Alive);
        Assert.Empty(Core(h).Wreaths);
        Assert.Empty(Core(h).Torches);
        Assert.False(ids == (S(h, 0).Me, S(h, 1).Me) && lists.Item1.SequenceEqual(S(h, 0).List) && lists.Item2.SequenceEqual(S(h, 1).List));
    }

    [Fact]
    public void After_the_last_round_the_match_finishes_with_totals_in_the_journal()
    {
        var h = Table(2, seed: 44, options: new { rounds = "1" });
        Go(h);
        Launch(h, 1, S(h, 1).List[0]);
        h.Tick(Kupala.RoundTicks + Kupala.RevealTicks);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Finished.Single().Result.Winners);
        Assert.Contains("Купальська ніч: Петро 4 : Оля 0", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Contains(h.Scores, s => s.Nick == "Петро" && s.Score == 4);
        var result = h.View(null).GetProperty("result");
        Assert.Equal("end", result.GetProperty("why").GetString());
    }

    [Fact]
    public void Equal_totals_make_shared_winners_and_all_zero_makes_a_draw()
    {
        var h = Table(3, seed: 46, options: new { rounds = "1" });
        Go(h);
        Launch(h, 0, S(h, 0).List[0]);
        Launch(h, 1, S(h, 1).List[0]);
        h.Tick(Kupala.RoundTicks + Kupala.RevealTicks);
        Assert.Equal([0, 1], h.Finished.Single().Result.Winners);

        var g = Table(2, seed: 48, options: new { rounds = "1" });
        Go(g);
        g.Tick(Kupala.RoundTicks + Kupala.RevealTicks);
        Assert.Empty(g.Finished.Single().Result.Winners);
        Assert.Contains("нічия", g.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void Options_rounds_and_folk_are_honoured_and_junk_falls_back()
    {
        foreach (var (opt, rounds) in new[] { ("1", 1), ("5", 5), ("7", 3), ("x", 3) })
        {
            var h = Table(2, seed: 50, options: new { rounds = opt });
            Go(h);
            for (var r = 1; r < rounds; r++) h.Tick(Kupala.RoundTicks + Kupala.RevealTicks + Kupala.StartTicks);
            Assert.Equal(rounds, G(h).RoundNo);
            h.Tick(Kupala.RoundTicks + Kupala.RevealTicks);
            Assert.Equal(RoomStatus.Finished, h.Room.Status);
        }
        Assert.Equal(2 + 22, Core(Table(2, seed: 52)).N);
        Assert.Equal(8 + 34, Core(Table(8, seed: 52)).N);
        Assert.Equal(3 + 16, Core(Table(3, seed: 52, options: new { folk = "small" })).N);
        Assert.Equal(3 + 40, Core(Table(3, seed: 52, options: new { folk = "big" })).N);
        Assert.Equal(3 + 24, Core(Table(3, seed: 52, options: new { folk = "???" })).N);
    }

    // =============================================================================================
    // Вінки й папороть
    // =============================================================================================

    [Fact]
    public void Launch_needs_standing_on_a_spot_and_says_where_otherwise()
    {
        var h = Table(2, seed: 54);
        Go(h);
        Put(Me(h, 0), 8, DarkRow);
        Assert.False(h.Act(0, "launch", new { }).Ok);
        Assert.Equal("Стань на кладку над водою", h.Reply.Message);
        Put(Me(h, 0), KupalaMap.Spots[2].X + 1, KupalaMap.BankRow);   // берег поруч із кладкою — не кладка
        Assert.False(h.Act(0, "launch", new { }).Ok);
        AtSpot(h, 0, 2);
        Assert.False(h.Act(0, "launch", new { spot = 3 }).Ok);
        Assert.Equal("Стань на кладку над водою", h.Reply.Message);
        Assert.True(h.Act(0, "launch", new { spot = 2 }).Ok);
        Assert.False(h.Act(0, "launch", new { }).Ok);
        Assert.Equal("Ти вже пускаєш вінок", h.Reply.Message);
        // з краю клітинки кладки — теж можна
        var g = Table(2, seed: 55);
        Go(g);
        Put(Me(g, 0), KupalaMap.Spots[4].X, KupalaMap.BankRow, ox: -8, oy: 6);
        Assert.True(g.Act(0, "launch", new { }).Ok, g.Reply.Message);
    }

    [Fact]
    public void Launch_takes_twenty_five_ticks_of_standing_then_a_wreath_event_reaches_everyone_once()
    {
        var h = Table(2, seed: 56);
        Go(h);
        var k = S(h, 0).List[0];
        var me = AtSpot(h, 0, k);
        Assert.True(h.Act(0, "launch", new { }).Ok);
        h.Input(0, "move", new { dir = 3 });
        h.Tick(Kupala.LaunchTicks - 1);
        Assert.Equal(KupalaMap.BankRow * 32 + 16, me.Y);               // стоїть, хоч і тисне вгору
        Assert.DoesNotContain(Events(h), e => e[0] == 2 && e[1] == k);
        h.Tick();
        Assert.Single(Events(h), e => e[0] == 2 && e[1] == k);
        Assert.True(S(h, 0).Done[0]);
        Assert.Equal(1, S(h, 0).Wreaths);
        h.Tick();
        Assert.True(me.Moving);
    }

    [Fact]
    public void A_wreath_off_the_list_floats_but_does_not_count()
    {
        var h = Table(2, seed: 58);
        Go(h);
        var other = Enumerable.Range(0, 8).First(k => !S(h, 0).List.Contains(k));
        Launch(h, 0, other);
        Assert.Single(Core(h).Wreaths);
        Assert.Equal(0, S(h, 0).Wreaths);
        Assert.Equal(0, S(h, 0).Total);
    }

    [Fact]
    public void Being_slapped_while_launching_cancels_the_wreath()
    {
        var h = Table(2, seed: 60);
        Go(h);
        Dark(Core(h));
        var k = S(h, 1).List[0];
        var him = AtSpot(h, 1, k);
        Assert.True(h.Act(1, "launch", new { }).Ok);
        Put(Me(h, 0), KupalaMap.Spots[k].X - 1, KupalaMap.BankRow, dir: 0);
        Park(h, Me(h, 0).Id, him.Id);
        h.Tick(5);
        Assert.True(h.Act(0, "slap", new { }).Ok, h.Reply.Message);
        h.Tick(Kupala.LaunchTicks);
        Assert.DoesNotContain(Events(h), e => e[0] == 2);
        Assert.Equal(0, S(h, 1).Wreaths);
        Assert.Equal(Kupala.PhaseReveal, G(h).Phase);
    }

    [Fact]
    public void Launch_cooldown_is_fifty_ticks_and_explicit_spot_is_validated()
    {
        var h = Table(2, seed: 62);
        Go(h);
        var list = S(h, 0).List;
        Launch(h, 0, list[0]);
        AtSpot(h, 0, list[1]);
        Assert.False(h.Act(0, "launch", new { }).Ok);
        Assert.Equal("Сплітаєш новий вінок…", h.Reply.Message);
        h.Tick(Kupala.LaunchCoolTicks);
        Assert.False(h.Act(0, "launch", new { spot = 8 }).Ok);
        Assert.Equal("Такої кладки нема", h.Reply.Message);
        Assert.False(h.Act(0, "launch", new { spot = "два" }).Ok);
        Assert.True(h.Act(0, "launch", new { spot = list[1] }).Ok, h.Reply.Message);
    }

    [Fact]
    public void Fern_blooms_once_in_the_forest_between_thirty_and_sixty_seconds_of_night()
    {
        for (var seed = 1; seed <= 6; seed++)
        {
            var h = Table(2, seed: seed * 7);
            Go(h);
            var core = Core(h);
            var t = 0;
            while (core.FernCell < 0 && t < Kupala.RoundTicks) { h.Tick(); t++; }
            Assert.InRange(core.NightT, KupalaCore.FernAtMin, KupalaCore.FernAtMax);
            Assert.Contains(core.FernCell, KupalaMap.FernCells);
            Assert.Contains(Events(h), e => e[0] == 5);
            var l = LastFrame(h).GetProperty("l").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            Assert.Contains(Enumerable.Range(0, l.Length / 4), i => l[i * 4] == KupalaCore.LFern);
        }
    }

    [Fact]
    public void Picking_the_fern_takes_a_second_pays_two_and_bots_never_pick_it()
    {
        var h = Table(2, seed: 64);
        Go(h);
        var core = Core(h);
        core.FernAt = core.NightT + 1;
        h.Tick();
        var cell = core.FernCell;
        Assert.True(cell >= 0);
        // бот стоїть просто на квітці — і не рве
        var bot = core.V.First(v => v.Owner < 0);
        Put(bot, cell % KupalaMap.W, cell / KupalaMap.W);
        bot.Stand = 200;
        h.Tick(100);
        Assert.Equal(cell, core.FernCell);
        var me = Put(Me(h, 0), cell % KupalaMap.W, cell / KupalaMap.W);
        Assert.True(h.Act(0, "launch", new { }).Ok, h.Reply.Message);
        h.Tick(Kupala.LaunchTicks - 1);
        Assert.Contains(me.Id, FrameIds(LastFrame(h)));       // квітка сама світить — того, хто рве, видно
        h.Tick();
        Assert.Equal(-1, core.FernCell);
        Assert.True(S(h, 0).Fern);
        Assert.Equal(Kupala.PtFern, S(h, 0).Total);
        // зірвав — і квітка згасла в руці: у кадрі знову темно, тож подія вже не називає його
        Assert.Equal([4, -1], Events(h).Single(e => e[0] == 4));
        // удруге не зацвіте
        h.Tick(Kupala.RoundTicks - 200);
        Assert.Equal(-1, core.FernCell);
    }

    // =============================================================================================
    // Ляпас і головешка
    // =============================================================================================

    [Fact]
    public void Slapping_a_bot_knocks_it_down_and_stuns_the_slapper_for_two_seconds()
    {
        var h = Table(2, seed: 66);
        Go(h);
        var me = Me(h, 0);
        var bot = Core(h).V.First(v => v.Owner < 0);
        Park(h, me.Id, bot.Id);
        Put(me, LitX - 1, LitY, dir: 0);
        Put(bot, LitX, LitY);
        Assert.True(h.Act(0, "slap", new { }).Ok, h.Reply.Message);
        Assert.Equal(2, bot.State);
        Assert.Equal(KupalaCore.StunTicks, me.Stun);
        h.Input(0, "move", new { dir = 3 });
        h.Tick();
        var ev = LastFrame(h).GetProperty("ev").EnumerateArray().Single().EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal([1, me.Id, bot.Id, 0, -1, bot.X, bot.Y], ev);
        Assert.False(h.Act(0, "slap", new { }).Ok);
        Assert.Equal("Ти ще отетерілий — постій", h.Reply.Message);
        Assert.False(h.Act(0, "torch", new { }).Ok);
        h.Tick(KupalaCore.StunTicks - 2);
        Assert.Equal(LitY * 32 + 16, me.Y);                    // стояв, хоч і тиснув угору
        Assert.Equal(1, me.Stun);
        h.Tick(2);
        Assert.True(me.Moving);
        Assert.Equal(0, S(h, 0).Total);
    }

    [Fact]
    public void Slapping_a_player_knocks_him_out_names_him_to_everyone_and_pays_two()
    {
        var h = Table(3, seed: 68);
        Go(h);
        var him = Me(h, 1);
        Park(h, Me(h, 0).Id, him.Id);
        Put(Me(h, 0), LitX - 1, LitY, dir: 0);
        Put(him, LitX, LitY);
        Assert.True(h.Act(0, "slap", new { id = him.Id }).Ok, h.Reply.Message);
        Assert.Equal(0, Me(h, 0).Stun);                     // по гравцю — не отетерів
        Assert.True(him.Dead);
        Assert.False(S(h, 1).Alive);
        Assert.Equal(Kupala.PtKill, S(h, 0).Total);
        h.Tick();
        var ev = Events(h).Single(e => e[0] == 1);
        Assert.Equal([1, Me(h, 0).Id, him.Id, 1, 1], ev[..5]);
        var dead = h.View(null).GetProperty("dead").EnumerateArray().Single();
        Assert.Equal((1, him.Id), (dead.GetProperty("seat").GetInt32(), dead.GetProperty("id").GetInt32()));
        Assert.Equal(3, him.State);
        // він і лишається лежати до кінця раунду
        h.Tick(300);
        Assert.Equal(3, him.State);
        Assert.Equal((LitX * 32 + 16, LitY * 32 + 16), (him.X, him.Y));
    }

    [Fact]
    public void A_whiff_is_heard_by_everyone_and_costs_a_second()
    {
        var h = Table(2, seed: 70);
        Go(h);
        Dark(Core(h));
        var me = Me(h, 0);
        Park(h, me.Id);
        Put(me, 8, DarkRow, dir: 0);
        Assert.True(h.Act(0, "slap", new { }).Ok, h.Reply.Message);
        Assert.Equal(0, me.Stun);
        Assert.False(h.Act(0, "slap", new { }).Ok);
        Assert.Equal("Рука ще не відійшла", h.Reply.Message);
        h.Tick();
        var ev = Events(h).Single(e => e[0] == 1);
        Assert.Equal([1, -1, -1, 2, -1, me.X + KupalaCore.WhiffAhead, me.Y], ev);
        h.Tick(Kupala.SlapCoolTicks - 1);
        Assert.True(h.Act(0, "slap", new { }).Ok, h.Reply.Message);
    }

    [Fact]
    public void Slap_takes_the_nearest_within_sixty_degrees_and_forty_units_in_the_dark_as_well()
    {
        var core = Bare(4);
        var p = core.V.Single(v => v.Owner == 0);
        var b = core.V.Where(v => v.Owner < 0).ToArray();
        Put(p, 8, DarkRow, dir: 0);
        Put(b[0], 8, DarkRow, ox: 30, oy: 0);             // прямо, 30
        Put(b[1], 8, DarkRow, ox: 20, oy: 20);            // 45°, 28 — ближчий
        Put(b[2], 8, DarkRow, ox: 12, oy: 25);            // 64° — поза конусом
        Put(b[3], 8, DarkRow, ox: -10, oy: 0);            // позаду
        Assert.Equal(b[1].Id, core.Cone(p));
        b[1].Fallen = 5;                                  // лежачого не б'ють
        Assert.Equal(b[0].Id, core.Cone(p));
        b[0].X = p.X + 41;
        Assert.Equal(-1, core.Cone(p));
        Assert.Equal(-1, core.Cone(p, botsOnly: true));
    }

    [Fact]
    public void Explicit_slap_target_reaches_fifty_six_units_and_refuses_the_rest()
    {
        var h = Table(2, seed: 72);
        Go(h);
        var me = Me(h, 0);
        var bot = Core(h).V.First(v => v.Owner < 0);
        Park(h, me.Id, bot.Id);
        Put(me, 8, DarkRow, dir: 3);
        Put(bot, 8, DarkRow, ox: 57);
        Assert.False(h.Act(0, "slap", new { id = bot.Id }).Ok);
        Assert.Equal("Не дотягнешся", h.Reply.Message);
        Assert.False(h.Act(0, "slap", new { id = me.Id }).Ok);
        Assert.Equal("Себе по щоці? Не треба", h.Reply.Message);
        Assert.False(h.Act(0, "slap", new { id = 999 }).Ok);
        Assert.Equal("Такого селянина нема", h.Reply.Message);
        Assert.False(h.Act(0, "slap", new { id = "Параска" }).Ok);
        Put(bot, 8, DarkRow, ox: 56);
        Assert.True(h.Act(0, "slap", new { id = bot.Id }).Ok, h.Reply.Message);
        Assert.Equal(0, me.Dir);                          // обернувся до того, кого б'є
        h.Tick(KupalaCore.StunTicks);
        Assert.False(h.Act(0, "slap", new { id = bot.Id }).Ok);
        Assert.Equal("Лежачого не б'ють", h.Reply.Message);
    }

    [Fact]
    public void Two_torches_per_round_one_second_apart()
    {
        var h = Table(2, seed: 74);
        Go(h);
        Put(Me(h, 0), 8, DarkRow, dir: 3);
        Assert.True(h.Act(0, "torch", new { }).Ok);
        Assert.False(h.Act(0, "torch", new { }).Ok);
        Assert.Equal("Рука ще не відійшла", h.Reply.Message);
        h.Tick(Kupala.SlapCoolTicks);
        Assert.True(h.Act(0, "torch", new { }).Ok);
        h.Tick(Kupala.SlapCoolTicks);
        Assert.False(h.Act(0, "torch", new { }).Ok);
        Assert.Equal("Головешки скінчились", h.Reply.Message);
        Assert.False(h.Act(0, "torch", "туди").Ok);
    }

    // =============================================================================================
    // Вихід, F5, «Ще раз»
    // =============================================================================================

    [Fact]
    public void A_leaver_becomes_a_bot_and_the_night_goes_on()
    {
        var h = Table(3, seed: 76);
        Go(h);
        var his = Me(h, 2);
        var n = Core(h).N;
        h.Leave("Ганна");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(-1, his.Owner);
        Assert.Equal(n, Core(h).N);
        var at = (his.X, his.Y);
        h.Tick(300);
        Assert.NotEqual(at, (his.X, his.Y));
        var seat = h.View(null).GetProperty("seats").EnumerateArray().Single(s => s.GetProperty("seat").GetInt32() == 2);
        Assert.True(seat.GetProperty("out").GetBoolean());
        h.Tick(Kupala.RoundTicks + Kupala.RevealTicks);
        Assert.Equal(2, G(h).RoundNo);
        Assert.Equal(n, Core(h).N);
        Assert.Equal(2, Core(h).V.Count(v => v.Owner >= 0));
    }

    [Fact]
    public void When_only_one_player_remains_the_match_ends_in_his_favour_and_everyone_is_revealed()
    {
        var h = Table(2, seed: 78);
        Go(h);
        Launch(h, 1, S(h, 1).List[0]);
        h.Leave("Оля");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Finished.Single().Result.Winners);
        Assert.Contains("Петро зустрічає світанок", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Contains(h.Scores, s => s.Nick == "Петро" && s.Score == 1);
        var v = h.View(null);
        Assert.Equal("left", v.GetProperty("result").GetProperty("why").GetString());
        Assert.Equal(2, v.GetProperty("reveal").GetProperty("ids").GetArrayLength());
        Assert.Equal(Core(h).N, FrameIds(v).Count);
    }

    [Fact]
    public void Rematch_gives_a_clean_match_with_rotated_seats()
    {
        var h = Table(2, seed: 80, options: new { rounds = "1" });
        Go(h);
        Launch(h, 0, S(h, 0).List[0]);
        h.Tick(Kupala.RoundTicks + Kupala.RevealTicks);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        h.Rematch("Оля");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("Петро", h.NickOf(0));
        Assert.Equal(Kupala.PhaseStart, G(h).Phase);
        Assert.Equal(1, G(h).RoundNo);
        Assert.All(new[] { 0, 1 }, s => Assert.Equal(0, S(h, s).Total));
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("result").ValueKind);
    }

    [Fact]
    public void Move_input_from_a_dead_or_reveal_phase_player_is_swallowed_silently()
    {
        var h = Table(3, seed: 82);
        Go(h);
        var him = Me(h, 1);
        Park(h, Me(h, 0).Id, him.Id);
        Put(Me(h, 0), 8, DarkRow, dir: 0);
        Put(him, 9, DarkRow);
        Assert.True(h.Act(0, "slap", new { }).Ok);
        Assert.True(h.Act(1, "move", new { dir = 0 }).Ok);
        h.Tick(5);
        Assert.Equal(9 * 32 + 16, him.X);
        Assert.False(h.Act(0, "move", new { dir = 7 }).Ok);
        Assert.Equal("Такого напрямку нема", h.Reply.Message);
        Assert.False(h.Act(1, "slap", new { }).Ok);
        Assert.Equal("Тебе вже вибили — дивись, хто кого", h.Reply.Message);
        h.Tick(Kupala.RoundTicks);
        Assert.Equal(Kupala.PhaseReveal, G(h).Phase);
        Assert.True(h.Act(0, "move", 2).Ok);
        Assert.Equal(-1, Me(h, 0).Want);
    }

    [Fact]
    public void Match_that_has_not_started_refuses_every_action_and_shows_a_dusk_preview()
    {
        var h = new RoomHarness("kupala", seed: 1);
        h.Join("Оля");
        h.Join("Петро");
        var view = h.View(0);
        Assert.Equal("lobby", view.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("me").ValueKind);
        Assert.Equal(22 * 5, view.GetProperty("v").GetArrayLength());
        var game = (Kupala)h.Room.Game;
        Assert.False(game.Act(0, "move", Views.Payload(new { dir = 1 })).Ok);
        Assert.Equal("Партія ще не почалась", game.Act(0, "slap", Views.Payload(new { })).Message);
    }

    [Fact]
    public void A_held_arrow_that_is_not_confirmed_for_three_seconds_is_let_go()
    {
        var h = Table(2, seed: 84);
        Go(h);
        var me = Me(h, 0);
        Put(me, 8, DarkRow);
        h.Input(0, "move", new { dir = 0 });
        h.Input(0, "move", new { dir = 2 });
        h.Tick(Kupala.MoveHoldTicks);
        Assert.Equal(2, me.Want);
        h.Tick(2);
        Assert.Equal(-1, me.Want);
    }

    // =============================================================================================
    // Приховане
    // =============================================================================================

    [Fact]
    public void Frame_has_exactly_the_spec_keys_and_five_numbers_per_lit_villager_sorted_by_id()
    {
        var h = Table(5, seed: 86);
        Go(h);
        h.Tick(30);
        var f = LastFrame(h);
        Assert.Equal(["t", "ph", "left", "sky", "v", "l", "ev"], f.EnumerateObject().Select(p => p.Name));
        var v = f.GetProperty("v");
        Assert.Equal(0, v.GetArrayLength() % 5);
        Assert.Equal(0, f.GetProperty("l").GetArrayLength() % 4);
        var ids = FrameIds(f);
        Assert.Equal(ids.Order(), ids);
        Assert.Equal(ids.Distinct().Count(), ids.Count);
        Assert.Equal(Core(h).Lit.Count(x => x), ids.Count);
    }

    [Fact]
    public void Frame_json_never_mentions_seats_owners_lists_or_torches_left()
    {
        var h = Table(4, seed: 88);
        Go(h);
        var me = Me(h, 0);
        Park(h, me.Id, Me(h, 1).Id);
        Put(me, 8, DarkRow, dir: 0);
        Put(Core(h).V.First(v => v.Owner < 0), 9, DarkRow);
        h.Act(0, "slap", new { });
        Launch(h, 1, S(h, 1).List[0]);
        h.Act(1, "torch", new { });
        h.Tick(3);
        foreach (var f in h.Outbox.OfType<RoomFrame>())
        {
            var text = Views.Text(f.Frame);
            foreach (var word in new[] { "\"seat", "\"me", "\"owner", "\"list", "\"torches", "\"nick", "\"alive", "\"done" })
                Assert.DoesNotContain(word, text);
        }
    }

    [Fact]
    public void Events_name_only_those_the_frame_shows()
    {
        var h = Table(3, seed: 90);
        Go(h);
        var core = Core(h);
        Dark(core);
        var me = Me(h, 0);
        var bot = core.V.First(v => v.Owner < 0);
        var him = Me(h, 2);
        Park(h, me.Id, bot.Id, him.Id);
        // у темряві: чути ляпас і де, але не хто кого
        Put(me, 8, DarkRow, dir: 0);
        Put(bot, 9, DarkRow);
        Assert.True(h.Act(0, "slap", new { }).Ok);
        h.Tick();
        Assert.Equal([1, -1, -1, 0, -1, bot.X, bot.Y], Events(h).Last(e => e[0] == 1));
        h.Tick(KupalaCore.StunTicks);
        // у світлі — видно обох
        Put(me, LitX - 1, LitY, dir: 0);
        Put(bot, LitX, LitY);
        bot.Fallen = 0;
        Assert.True(h.Act(0, "slap", new { }).Ok, h.Reply.Message);
        h.Tick();
        Assert.Equal([1, me.Id, bot.Id, 0, -1, bot.X, bot.Y], Events(h).Last(e => e[0] == 1));
        h.Tick(KupalaCore.StunTicks);
        // вибитого гравця називають завжди (він і так у dead), того, хто вибив у темряві, — ні
        Put(me, 8, DarkRow, dir: 0);
        Put(him, 9, DarkRow);
        Assert.True(h.Act(0, "slap", new { }).Ok, h.Reply.Message);
        h.Tick();
        Assert.Equal([1, -1, him.Id, 1, 2], Events(h).Last(e => e[0] == 1)[..5]);
        Assert.DoesNotContain(me.Id, FrameIds(LastFrame(h)));
    }

    [Fact]
    public void Visibility_depends_on_where_one_stands_not_on_who_one_is()
    {
        var h = Table(4, seed: 92);
        Go(h);
        h.Tick(40);
        var core = Core(h);
        var before = core.Pack();
        var owners = core.V.Select(v => v.Owner).ToArray();
        foreach (var v in core.V) v.Owner = v.Owner >= 0 ? -1 : 0;     // гравці стали ботами й навпаки
        core.ComputeLit(core.Sky > 0);
        Assert.Equal(before, core.Pack());
        for (var i = 0; i < owners.Length; i++) core.V[i].Owner = owners[i];
    }

    [Fact]
    public void Player_ids_are_shuffled_among_bots_across_seeds()
    {
        var high = 0;
        var notFirst = 0;
        for (var seed = 1; seed <= 100; seed++)
        {
            var core = new KupalaCore(new Random(seed));
            core.Deal([0, 1], 22);
            var ids = core.V.Where(v => v.Owner >= 0).Select(v => v.Id).ToArray();
            if (ids.Any(id => id >= core.N / 2)) high++;
            if (ids.Any(id => id >= 2)) notFirst++;
        }
        Assert.True(high > 50, $"{high}");
        Assert.True(notFirst > 90, $"{notFirst}");
    }

    static string Strip(JsonElement view)
    {
        var d = view.EnumerateObject().Where(p => p.Name != "me").ToDictionary(p => p.Name, p => p.Value);
        return Views.Text(d);
    }

    [Fact]
    public void Watcher_view_has_no_me_and_other_seats_see_the_same_but_their_own_me()
    {
        var h = Table(3, seed: 94);
        Go(h);
        h.Tick(20);
        var watcher = h.View(null);
        Assert.Equal(JsonValueKind.Null, watcher.GetProperty("me").ValueKind);
        Assert.Equal(JsonValueKind.Null, watcher.GetProperty("reveal").ValueKind);
        Assert.Empty(watcher.GetProperty("dead").EnumerateArray());
        var mine = h.View(0).GetProperty("me");
        Assert.Equal(S(h, 0).Me, mine.GetProperty("id").GetInt32());
        Assert.Equal(S(h, 0).List, mine.GetProperty("list").EnumerateArray().Select(e => e.GetInt32()));
        foreach (var seat in new[] { 0, 1, 2 }) Assert.Equal(Strip(watcher), Strip(h.View(seat)));
        Assert.DoesNotContain("\"torches\"", Strip(h.View(0)));
    }

    [Fact]
    public void Dead_player_view_equals_watcher_view_plus_his_own_me()
    {
        var h = Table(3, seed: 96);
        Go(h);
        var him = Me(h, 1);
        Park(h, Me(h, 0).Id, him.Id);
        Put(Me(h, 0), 8, DarkRow, dir: 0);
        Put(him, 9, DarkRow);
        Assert.True(h.Act(0, "slap", new { }).Ok);
        h.Tick();
        var dead = h.View(1);
        Assert.False(dead.GetProperty("me").GetProperty("alive").GetBoolean());
        Assert.Equal(Strip(h.View(null)), Strip(dead));
        Assert.Equal([him.Id], dead.GetProperty("dead").EnumerateArray().Select(d => d.GetProperty("id").GetInt32()));
        Assert.DoesNotContain("\"ids\"", Views.Text(dead));
    }

    [Fact]
    public void Reveal_ids_appear_only_in_reveal_and_over_phases()
    {
        var h = Table(2, seed: 98, options: new { rounds = "1" });
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
        Go(h);
        h.Tick(Kupala.RoundTicks - 1);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
        h.Tick();
        var ids = h.View(null).GetProperty("reveal").GetProperty("ids").EnumerateArray()
            .Select(e => (e.GetProperty("seat").GetInt32(), e.GetProperty("id").GetInt32())).ToList();
        Assert.Equal([(0, S(h, 0).Me), (1, S(h, 1).Me)], ids);
        h.Tick(Kupala.RevealTicks);
        Assert.Equal(Kupala.PhaseOver, G(h).Phase);
        Assert.NotEqual(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
    }

    [Fact]
    public void Round_points_and_wreaths_stay_hidden_until_the_reveal()
    {
        var h = Table(3, seed: 100);
        Go(h);
        Launch(h, 1, S(h, 1).List[0]);
        var him = Me(h, 2);
        Park(h, Me(h, 0).Id, him.Id);
        Put(Me(h, 0), 8, DarkRow, dir: 0);
        Put(him, 9, DarkRow);
        Assert.True(h.Act(0, "slap", new { }).Ok);
        h.Tick();
        foreach (var s in h.View(null).GetProperty("seats").EnumerateArray())
        {
            Assert.Equal(0, s.GetProperty("total").GetInt32());
            Assert.Equal(JsonValueKind.Null, s.GetProperty("wreaths").ValueKind);
        }
        h.Tick(Kupala.RoundTicks);
        var seats = h.View(null).GetProperty("seats").EnumerateArray().ToDictionary(s => s.GetProperty("seat").GetInt32());
        Assert.Equal(Kupala.PtKill, seats[0].GetProperty("total").GetInt32());
        Assert.Equal(1, seats[1].GetProperty("wreaths").GetInt32());
    }

    [Fact]
    public void A_bot_wreath_sends_views_just_like_a_player_wreath()
    {
        var h = Table(2, seed: 102);
        Go(h);
        var bot = Core(h).V.First(v => v.Owner < 0);
        Park(h, bot.Id);
        Put(bot, KupalaMap.Spots[5].X, KupalaMap.BankRow, dir: 1);
        bot.Busy = KupalaCore.BusyTicks - 1;
        bot.BusyWhat = 5;
        h.Tick(KupalaCore.BusyTicks - 2);
        var views = h.Outbox.Count(o => o is RoomViews);
        h.Tick();
        Assert.Contains(Events(h), e => e[0] == 2 && e[1] == 5);
        Assert.True(h.Outbox.Count(o => o is RoomViews) > views);
    }

    [Fact]
    public void Per_tick_displacement_and_state_values_of_players_are_a_subset_of_those_of_bots()
    {
        var h = Table(4, seed: 104);
        var rng = new Random(3);
        var core = Core(h);
        var botSteps = new HashSet<(int, int, int)>();
        var playerSteps = new HashSet<(int, int, int)>();
        var was = core.V.Select(v => (v.X, v.Y)).ToArray();
        for (var t = 0; t < 2000 && G(h).Phase is Kupala.PhaseStart or Kupala.PhaseGo; t++)
        {
            if (t % 9 == 0)
                for (var s = 0; s < 4; s++) h.Input(s, "move", new { dir = rng.Next(-1, 4) });
            h.Tick();
            foreach (var v in core.V)
            {
                var step = (v.X - was[v.Id].X, v.Y - was[v.Id].Y, v.State);
                (v.Owner >= 0 ? playerSteps : botSteps).Add(step);
                was[v.Id] = (v.X, v.Y);
            }
        }
        Assert.Subset(botSteps, playerSteps);
    }

    /// <summary>Людина за клавіатурою: тримає стрілку 5–41 тик, відпускає на 1–31, зрідка задумується на 4–10 с.</summary>
    sealed class Human(Random rng)
    {
        int _left, _dir = -1;

        public int Next()
        {
            if (_left-- > 0) return _dir;
            if (_dir >= 0 && rng.Next(100) >= 35)
            {
                _dir = -1;
                _left = rng.Next(100) < 6 ? rng.Next(100, 251) : rng.Next(0, 31);
            }
            else
            {
                _dir = rng.Next(4);
                _left = rng.Next(4, 41);
            }
            return _dir;
        }
    }

    sealed class Trace
    {
        public readonly HashSet<(int, int)> Spots = [];
        public long Standing, StandingOff;
        public readonly Dictionary<int, int> Runs = [];
        public int RunCount;
        public readonly HashSet<(int, int)> Lanes = [];
        public long Moving, MovingOff;

        public static int RunBin(int len) => len switch { <= 3 => 0, <= 7 => 1, <= 11 => 2, <= 24 => 3, <= 50 => 4, <= 100 => 5, <= 200 => 6, _ => 7 };
        public static bool Off(int r) => Math.Abs(r - 16) > 8;
        public int Share(int from, int to) => Runs.Where(p => p.Key >= from && p.Key <= to).Sum(p => p.Value) * 1000 / Math.Max(1, RunCount);
    }

    /// <summary>Ніч на голому ядрі: 4 «людини» і 30 ботів, 6000 тиків на сід, порядок тика — як у грі.</summary>
    static (Trace Players, Trace Bots) Observe(int seeds, int ticks = 6000)
    {
        var players = new Trace();
        var bots = new Trace();
        for (var seed = 1; seed <= seeds; seed++)
        {
            var core = new KupalaCore(new Random(seed));
            core.Deal([0, 1, 2, 3], 30);
            core.Night = true;
            var humans = Enumerable.Range(0, 4).Select(i => new Human(new Random(seed * 10 + i))).ToArray();
            var n = core.N;
            var px = core.V.Select(v => v.X).ToArray();
            var py = core.V.Select(v => v.Y).ToArray();
            var run = new int[n];
            for (var t = 0; t < ticks; t++)
            {
                foreach (var v in core.V)
                    if (v.Owner >= 0) v.Want = humans[v.Owner].Next();
                core.TimersAll();
                foreach (var id in core.Done) core.V[id].BusyWhat = -1;
                core.ThinkAll();
                core.StepAll();
                core.Events.Clear();
                foreach (var v in core.V)
                {
                    var side = v.Owner >= 0 ? players : bots;
                    int rx = v.X % 32, ry = v.Y % 32;
                    var still = v.State == 0 && v.X == px[v.Id] && v.Y == py[v.Id];
                    if (still)
                    {
                        side.Standing++;
                        if (Trace.Off(rx) || Trace.Off(ry)) side.StandingOff++;
                        side.Spots.Add((rx / 4, ry / 4));
                        run[v.Id]++;
                    }
                    else
                    {
                        if (run[v.Id] > 0 && t > 200)
                        {
                            var bin = Trace.RunBin(run[v.Id]);
                            side.Runs[bin] = side.Runs.GetValueOrDefault(bin) + 1;
                            side.RunCount++;
                        }
                        run[v.Id] = 0;
                        if (v.State == 1)
                        {
                            var across = v.Dir is 0 or 2 ? ry : rx;
                            side.Moving++;
                            if (Trace.Off(across)) side.MovingOff++;
                            side.Lanes.Add((v.Dir & 1, across / 4));
                        }
                    }
                    px[v.Id] = v.X;
                    py[v.Id] = v.Y;
                }
            }
        }
        return (players, bots);
    }

    [Fact]
    public void Players_stand_where_bots_stand_walk_their_lanes_and_stand_as_long()
    {
        var (players, bots) = Observe(3);
        output.WriteLine($"стоять поза ±8: гравці {players.StandingOff * 100 / players.Standing}%, боти {bots.StandingOff * 100 / bots.Standing}%");
        output.WriteLine($"ідуть поза смугою ±8: гравці {players.MovingOff * 100 / players.Moving}%, боти {bots.MovingOff * 100 / bots.Moving}%");
        string Show(Trace s) => string.Join(" ", Enumerable.Range(0, 8).Select(b => $"{b}:{s.Share(b, b) / 10.0:F1}%"));
        output.WriteLine($"серії стояння, гравці: {Show(players)}");
        output.WriteLine($"серії стояння, боти:   {Show(bots)}");
        Assert.Subset(bots.Spots, players.Spots);
        Assert.True(bots.StandingOff * 2 >= players.StandingOff * bots.Standing / players.Standing);
        Assert.Subset(bots.Lanes, players.Lanes);
        Assert.True(bots.MovingOff * 2 >= players.MovingOff * bots.Moving / players.Moving);
        Assert.Subset(bots.Runs.Keys.ToHashSet(), players.Runs.Keys.ToHashSet());
        Assert.True(bots.Share(0, 2) * 2 >= players.Share(0, 2));
        Assert.True(bots.Share(6, 7) * 2 >= players.Share(6, 7));
    }

    // =============================================================================================
    // Контракт і детермінізм
    // =============================================================================================

    [Fact]
    public void The_server_accepts_exactly_what_the_module_sends()
    {
        var h = Table(2, seed: 106);
        var me = Me(h, 0);
        Put(me, 8, DarkRow);
        h.Input(0, "move", new { dir = 0 });
        h.Tick();
        Assert.Equal(0, me.Want);
        h.Input(0, "move", 2);
        Assert.Equal(2, me.Want);
        h.Input(0, "move", new { dir = -1 });
        Assert.Equal(-1, me.Want);
        Go(h);
        var bot = Core(h).V.First(v => v.Owner < 0);
        Park(h, me.Id, bot.Id);
        Put(me, 8, DarkRow, dir: 0);
        Put(bot, 9, DarkRow);
        Assert.True(h.Act(0, "slap", new { }).Ok, h.Reply.Message);                 // пробіл: хто перед тобою
        Assert.Equal(KupalaCore.FallTicks, bot.Fallen);
        h.Tick(KupalaCore.StunTicks);
        bot.Fallen = 0;
        Assert.True(h.Act(0, "slap", new { id = bot.Id }).Ok, h.Reply.Message);      // клік по селянину
        h.Tick(KupalaCore.StunTicks);
        Assert.True(h.Act(0, "torch", new { }).Ok, h.Reply.Message);                 // R
        var k = S(h, 0).List[0];
        AtSpot(h, 0, k);
        Assert.True(h.Act(0, "launch", new { }).Ok, h.Reply.Message);                // E
        h.Tick(Kupala.LaunchTicks + Kupala.LaunchCoolTicks);
        Assert.True(h.Act(0, "launch", new { spot = k }).Ok, h.Reply.Message);        // клік по кладці
        var before = Views.Text(h.View(0));
        foreach (var (action, payload) in new (string, object?)[] { ("move", new { dir = "up" }), ("move", new { d = 1 }), ("jump", null), ("slap", "x"), ("launch", new { spot = 99 }), ("torch", 5) })
            Assert.False(h.Act(0, action, payload).Ok);
        Assert.Equal(before, Views.Text(h.View(0)));
    }

    [Fact]
    public void The_module_sends_only_actions_and_payloads_the_server_reads()
    {
        var js = File.ReadAllText(Path.Combine(FindRoot(), "web", "games", "kupala.js"));
        var sent = System.Text.RegularExpressions.Regex.Matches(js, @"ctx\.(?:act|input)\('(\w+)'")
            .Select(m => m.Groups[1].Value).Distinct().Order().ToArray();
        Assert.Equal(["launch", "move", "slap", "torch"], sent);
        Assert.Contains("ctx.input('move', { dir: d })", js);
        Assert.Contains("ctx.input('move', { dir: -1 })", js);
        Assert.Contains("ctx.act('slap', id == null ? {} : { id })", js);
        Assert.Contains("ctx.act('launch', spot == null ? {} : { spot })", js);
        Assert.Contains("ctx.act('torch', {})", js);
    }

    [Fact]
    public void Module_constants_match_the_server_rules()
    {
        var js = File.ReadAllText(Path.Combine(FindRoot(), "web", "games", "kupala.js"));
        int Const(string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(js, @"\b" + name + @"\s*=\s*(\d+)");
            Assert.True(m.Success, name);
            return int.Parse(m.Groups[1].Value);
        }
        Assert.Equal(Kupala.TickMs, Const("TICK_MS"));
        Assert.Equal(KupalaMap.Cell, Const("CELL"));
        Assert.Equal(KupalaMap.WorldW, Const("WW"));
        Assert.Equal(KupalaMap.WorldH, Const("WH"));
        Assert.Equal(KupalaCore.SlapReach, Const("SLAP_REACH"));
        Assert.Equal(KupalaCore.SlapReachMax, Const("SLAP_MAX"));
        Assert.Equal(KupalaCore.SlapCos2Milli, Const("CONE"));
        Assert.Equal(Kupala.SlapCoolTicks * Kupala.TickMs, Const("SLAP_COOL_MS"));
        Assert.Equal(Kupala.LaunchCoolTicks * Kupala.TickMs, Const("LAUNCH_COOL_MS"));
        Assert.Equal(Kupala.LaunchTicks * Kupala.TickMs, Const("LAUNCH_MS"));
        Assert.Equal(KupalaCore.StunTicks * Kupala.TickMs, Const("STUN_MS"));
        Assert.Equal(KupalaCore.TorchTicks * Kupala.TickMs, Const("TORCH_MS"));
        Assert.Equal(Kupala.RoundTicks, Const("ROUND_TICKS"));
    }

    static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "web", "games"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("не знайшов корінь репозиторію");
    }

    static List<string> Replay(int seed)
    {
        var h = Table(3, seed: seed);
        var frames = new List<string>();
        for (var t = 0; t < 900; t++)
        {
            if (t % 11 == 0) h.Input(t % 3, "move", new { dir = t % 5 - 1 });
            if (t % 97 == 0) h.Act(t % 3, "slap", new { });
            if (t % 131 == 0) h.Act(t % 3, "torch", new { });
            if (t % 53 == 0) h.Act((t + 1) % 3, "launch", new { });
            h.Tick();
            frames.Add(Views.Text(((RoomFrame)h.Outbox.Last(o => o is RoomFrame)).Frame));
        }
        frames.Add(Views.Text(h.View(0)));
        return frames;
    }

    [Fact]
    public void Same_seed_and_same_inputs_give_byte_identical_frames()
    {
        var a = Replay(108);
        var b = Replay(108);
        Assert.Equal(a, b);
        Assert.NotEqual(a, Replay(109));
    }

    [Fact]
    public void Views_json_matches_the_spec_shape()
    {
        var h = Table(2, seed: 110);
        Go(h);
        var v = h.View(0);
        foreach (var key in new[] { "phase", "round", "of", "left", "t", "width", "height", "cell", "n", "map", "spots", "fires", "looks", "names", "v", "l", "sky", "seats", "dead", "me", "reveal", "result", "turn" })
            Assert.True(Views.Has(v, key), key);
        var n = v.GetProperty("n").GetInt32();
        Assert.Equal(20, v.GetProperty("map").GetArrayLength());
        Assert.Equal(4 * n, v.GetProperty("looks").GetArrayLength());
        Assert.Equal(n, v.GetProperty("names").GetArrayLength());
        Assert.Equal(4, v.GetProperty("fires").GetArrayLength());
        var spot = v.GetProperty("spots")[0];
        foreach (var key in new[] { "i", "name", "where", "emoji", "x", "y" }) Assert.True(Views.Has(spot, key), key);
        Assert.Equal("під вербою", spot.GetProperty("where").GetString());
        var seat = v.GetProperty("seats")[0];
        foreach (var key in new[] { "seat", "nick", "alive", "out", "wreaths", "total" }) Assert.True(Views.Has(seat, key), key);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("turn").ValueKind);
        h.Tick(Kupala.RoundTicks);
        var row = h.View(0).GetProperty("reveal").GetProperty("rows")[0];
        foreach (var key in new[] { "seat", "wreaths", "kills", "fern", "win", "pts" }) Assert.True(Views.Has(row, key), key);
        output.WriteLine($"вид на {n} селян: {Views.Text(G(h).View(0)).Length} Б");
    }

    [Fact]
    public void Catalog_lists_kupala_as_live_by_host_hidden_tick_forty_with_css()
    {
        var info = new Kupala().Info;
        Assert.Equal("kupala", info.Id);
        Assert.Equal("Купальська ніч", info.Title);
        Assert.Equal(GameGroup.Live, info.Group);
        Assert.Equal((2, 8), (info.MinPlayers, info.MaxPlayers));
        Assert.Equal(40, info.TickMs);
        Assert.Equal(StartMode.ByHost, info.Start);
        Assert.True(info.Hidden);
        Assert.False(info.Rated);
        Assert.Equal(ScoreOrder.HigherIsBetter, info.Score);
        Assert.Equal(["rounds", "folk"], info.Options!.Select(o => o.Key));
        var root = FindRoot();
        Assert.True(File.Exists(Path.Combine(root, "web", "games", "kupala.js")));
        Assert.True(File.Exists(Path.Combine(root, "web", "games", "kupala.css")));
        Assert.Equal("фіолетовий", new Kupala().SeatName(6));
    }

    [Fact]
    public void Achievements_fern_and_blind_are_requested_exactly_when_earned()
    {
        Assert.NotNull(AchievementCatalog.Get("kupala-fern"));
        Assert.NotNull(AchievementCatalog.Get("kupala-blind"));
        // Оля в темряві вибиває Ганну (теж у темряві); Петро рве папороть
        var h = Table(3, seed: 112, options: new { rounds = "1" });
        Go(h);
        var core = Core(h);
        Dark(core);
        Park(h, Me(h, 0).Id, Me(h, 1).Id, Me(h, 2).Id);
        Put(Me(h, 0), 8, DarkRow, dir: 0);
        Put(Me(h, 2), 9, DarkRow);
        h.Tick();
        Assert.True(h.Act(0, "slap", new { }).Ok, h.Reply.Message);
        core.FernAt = core.NightT + 1;
        h.Tick();
        Put(Me(h, 1), core.FernCell % KupalaMap.W, core.FernCell / KupalaMap.W);
        Assert.True(h.Act(1, "launch", new { }).Ok, h.Reply.Message);
        h.Tick(Kupala.LaunchTicks);
        Assert.Empty(h.Awards);                                // ачівки — лише на кінці раунду
        h.Tick(Kupala.RoundTicks);
        var awards = h.Awards.Select(a => (a.Nick, a.Reason, a.Shards)).ToList();
        Assert.Contains(("Оля", "ach:kupala-blind", 0), awards);
        Assert.Contains(("Петро", "ach:kupala-fern", 0), awards);
        Assert.Equal(2, awards.Count);

        // у світлі — не «навпомацки»
        var g = Table(2, seed: 114, options: new { rounds = "1" });
        Go(g);
        Park(g, Me(g, 0).Id, Me(g, 1).Id);
        Put(Me(g, 0), LitX - 1, LitY, dir: 0);
        Put(Me(g, 1), LitX, LitY);
        g.Tick();
        Assert.True(g.Act(0, "slap", new { }).Ok, g.Reply.Message);
        g.Tick(2);
        Assert.Empty(g.Awards);
    }

    [Fact]
    public void Lists_have_one_left_one_middle_and_one_right_spot()
    {
        for (var seed = 1; seed <= 30; seed++)
        {
            var h = Table(4, seed: seed);
            for (var s = 0; s < 4; s++)
            {
                var list = S(h, s).List;
                Assert.Equal(3, list.Distinct().Count());
                Assert.Contains(list, k => KupalaMap.LeftSpots.Contains(k));
                Assert.Contains(list, k => KupalaMap.MidSpots.Contains(k));
                Assert.Contains(list, k => KupalaMap.RightSpots.Contains(k));
            }
        }
    }

    [Fact]
    public void Players_start_apart_and_nobody_starts_on_a_spot()
    {
        for (var seed = 1; seed <= 30; seed++)
        {
            var core = new KupalaCore(new Random(seed));
            core.Deal([0, 1, 2, 3, 4, 5, 6, 7], 40);
            Assert.All(core.V, v => Assert.Equal(-1, KupalaCore.SpotAt(v)));
            var players = core.V.Where(v => v.Owner >= 0).ToArray();
            for (var i = 0; i < players.Length; i++)
                for (var j = i + 1; j < players.Length; j++)
                {
                    int dx = Math.Abs(players[i].X / 32 - players[j].X / 32), dy = Math.Abs(players[i].Y / 32 - players[j].Y / 32);
                    Assert.True(Math.Max(dx, dy) >= 3, $"сід {seed}");
                }
            Assert.Equal(48, core.V.Select(v => v.Name).Distinct().Count());
        }
    }

    // =============================================================================================
    // Швидкодія
    // =============================================================================================

    [Fact]
    [Trait("Category", "Perf")]
    public void Three_thousand_ticks_with_eight_players_and_forty_bots_fit_in_a_second()
    {
        long best = long.MaxValue;
        for (var attempt = 0; attempt < 3 && best >= 1000; attempt++)
        {
            var h = Table(8, seed: 120 + attempt, options: new { folk = "big", rounds = "5" });
            var rng = new Random(attempt);
            var sw = Stopwatch.StartNew();
            for (var t = 0; t < 3000 && h.Room.Status == RoomStatus.Playing; t++)
            {
                if (t % 10 == 0)
                    for (var s = 0; s < 8; s++) h.Input(s, "move", new { dir = rng.Next(-1, 4) });
                if (t % 100 == 50)
                    for (var s = 0; s < 8; s++) h.Act(s, s % 3 == 0 ? "slap" : s % 3 == 1 ? "launch" : "torch", new { });
                h.Tick();
            }
            best = Math.Min(best, sw.ElapsedMilliseconds);
        }
        var pure = PureTickMicros();
        output.WriteLine($"3000 тиків через кімнату: {best} мс; чистий Tick() + Frame(): {pure:F1} мкс");
        Assert.True(best < 1000, $"{best} мс");
        Assert.True(pure < 250, $"{pure} мкс на тик");
    }

    static double PureTickMicros()
    {
        var bestUs = double.MaxValue;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var h = Table(8, seed: 130 + attempt, options: new { folk = "big", rounds = "5" });
            var game = G(h);
            Go(h);
            var rng = new Random(attempt);
            for (var i = 0; i < 200; i++) game.Tick();
            var sw = Stopwatch.StartNew();
            var n = 0;
            for (var t = 0; t < 3000 && game.Phase != Kupala.PhaseOver; t++)
            {
                if (t % 10 == 0)
                    for (var s = 0; s < 8; s++) game.Act(s, "move", Views.Payload(new { dir = rng.Next(-1, 4) }));
                var r = game.Tick();
                if (r.Frame) game.Frame();
                n++;
            }
            bestUs = Math.Min(bestUs, sw.Elapsed.TotalMicroseconds / Math.Max(1, n));
        }
        return bestUs;
    }

    [Fact]
    public void A_frame_serialises_under_two_kilobytes_even_when_lightning_shows_all_forty_eight()
    {
        var h = Table(8, seed: 140, options: new { folk = "big" });
        Go(h);
        var core = Core(h);
        var max = 0;
        var sum = 0L;
        for (var t = 0; t < 1500; t++)
        {
            if (t % 300 == 0) core.SkyNext = core.NightT + 1;
            h.Tick();
            var len = FrameText(h).Length;
            max = Math.Max(max, len);
            sum += len;
        }
        output.WriteLine($"кадр на 48 селян: у середньому {sum / 1500} Б, найбільший {max} Б");
        Assert.Equal(48, core.N);
        Assert.True(max < 2000, $"{max} Б");
    }
}
