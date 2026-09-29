using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Корчма: мапу, крок і мозок ботів перевіряємо на голому <see cref="TavernCore"/> (там відвідувача можна поставити
/// рівно туди, куди треба), а раунди, очки, кулаки, кухлі, корчмаря й приховане — через справжню кімнату
/// (<see cref="RoomHarness"/>). Клас у серійній колекції через перф-тест.
/// </summary>
[Collection(SerialPerf.Name)]
public partial class TavernTests(ITestOutputHelper output)
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марта", "Юрко", "Соня", "Богдан"];

    // ---------- підмостки ----------

    static RoomHarness Table(int players = 2, int seed = 42, object? options = null)
    {
        var h = new RoomHarness("tavern", options, seed: seed);
        foreach (var nick in Nicks.Take(players)) h.Join(nick);
        h.Start();
        return h;
    }

    static Tavern G(RoomHarness h) => (Tavern)h.Room.Game;
    static TavernCore Core(RoomHarness h) => G(h).CoreForTests;
    static TavernSeat S(RoomHarness h, int seat) => G(h).SeatForTests(seat);
    static TavernGuest Me(RoomHarness h, int seat) => Core(h).V[S(h, seat).Me];

    static void Go(RoomHarness h)
    {
        for (var i = 0; i < 200 && G(h).Phase != Tavern.PhaseGo; i++) h.Tick();
        Assert.Equal(Tavern.PhaseGo, G(h).Phase);
    }

    /// <summary>Поставити відвідувача в центр клітинки (x, y) + зсув; стоїть, нікуди не хоче, не сидить.</summary>
    static TavernGuest Put(TavernGuest v, int cx, int cy, int ox = 0, int oy = 0, int dir = 0)
    {
        v.X = cx * TavernMap.Cell + TavernMap.Cell / 2 + ox;
        v.Y = cy * TavernMap.Cell + TavernMap.Cell / 2 + oy;
        v.Dir = dir;
        v.Want = -1;
        v.Moving = false;
        v.Blocked = false;
        v.Sit = false;
        v.Rise = 0;
        return v;
    }

    /// <summary>Бот-мішень: стоїть на місці, кулаком не махає, здачі не дає.</summary>
    static TavernGuest Pin(TavernGuest v)
    {
        v.Stand = 100_000;
        v.PunchCool = 100_000;
        v.Grudge = -1;
        v.Wander = 0;
        v.Target = -1;
        return v;
    }

    /// <summary>Корчмар не гримає (поки тест сам не попросить).</summary>
    static void Calm(RoomHarness h)
    {
        var b = Core(h).Barman;
        b.Mode = 0;
        b.NextShout = 1_000_000;
    }

    /// <summary>Ядро з одним гравцем (місце 0) і кількома ботами, які стоять і нікуди не хочуть.</summary>
    static TavernCore Bare(int bots = 3, int seed = 1)
    {
        var core = new TavernCore(new Random(seed));
        core.Deal([0], bots);
        foreach (var v in core.V)
        {
            TavernCore.Forget(v);
            v.Stand = 0;
            v.Sit = false;
        }
        return core;
    }

    /// <summary>Усі, крім переданих (боти й гравці), «сплять» надворі й у нижньому ряду: не ходять, не б'ються, не заважають кулаку.</summary>
    static void Park(RoomHarness h, params int[] keep)
    {
        Calm(h);
        var i = 0;
        int[] rows = [17, 18, 13];
        foreach (var v in Core(h).V)
        {
            if (Array.IndexOf(keep, v.Id) >= 0) continue;
            Put(v, 1 + i % 28, rows[i / 28 % 3]);
            Pin(v);
            v.Drink = 0;
            v.Wind = 0;
            i++;
        }
    }

    static (int X, int Y) CellXY(int cell) => (cell % TavernMap.W, cell / TavernMap.W);

    /// <summary>Стати на приступку місця й випити кухоль до дна.</summary>
    static void DrinkAt(RoomHarness h, int seat, int place)
    {
        var (x, y) = CellXY(TavernMap.Places[place].Cells[0]);
        Put(Me(h, seat), x, y);
        Assert.True(h.Act(seat, "drink", new { }).Ok, h.Reply.Message);
        h.Tick(Tavern.DrinkTicks);
    }

    /// <summary>Гравець seat стоїть у (10, 5) обличчям праворуч, ціль — рівно на клітинку правіше.</summary>
    static TavernGuest Face(RoomHarness h, int seat, TavernGuest target)
    {
        var me = Put(Me(h, seat), 10, 5, dir: 0);
        Put(target, 11, 5);
        return me;
    }

    static string FrameText(RoomHarness h) => Views.Text(G(h).Frame());

    static JsonElement LastFrame(RoomHarness h) => Views.Json(((RoomFrame)h.Outbox.Last(o => o is RoomFrame)).Frame);

    static IEnumerable<JsonElement> Events(RoomHarness h, int since) =>
        h.Outbox.Skip(since).OfType<RoomFrame>().SelectMany(f => Views.Json(f.Frame).GetProperty("ev").EnumerateArray());

    static int[] Ev(JsonElement e) => [.. e.EnumerateArray().Select(x => x.GetInt32())];

    // =============================================================================================
    // Мапа й навігація
    // =============================================================================================

    [Fact]
    public void Map_is_30_by_20_with_walls_around_and_a_door_to_the_porch()
    {
        Assert.Equal(20, TavernMap.Rows.Length);
        Assert.All(TavernMap.Rows, r => Assert.Equal(30, r.Length));
        Assert.All(TavernMap.Rows[0], c => Assert.Equal('#', c));
        Assert.All(TavernMap.Rows[19], c => Assert.Equal('#', c));
        Assert.All(TavernMap.Rows, r => Assert.True(r[0] == '#' && r[29] == '#'));
        // зала від ґанку — стіною, у ній рівно четверо дверей-клітинок
        Assert.Equal("dddd", TavernMap.Rows[16].Replace("#", ""));
        Assert.Equal(TavernMap.Walkable.Length, TavernMap.Pass.Count(p => p));
        Assert.Equal(48, TavernMap.Names.Distinct().Count());
        output.WriteLine($"прохідних клітинок: {TavernMap.Walkable.Length}, лав: {TavernMap.Benches.Length}, ґанку біля дверей: {TavernMap.Porch.Length}");
    }

    [Fact]
    public void Every_walkable_cell_is_reachable_from_every_other()
    {
        var hops = TavernMap.NextHop;
        foreach (var target in TavernMap.Walkable)
            foreach (var cell in TavernMap.Walkable)
            {
                var hop = hops[target * TavernMap.Cells + cell];
                if (cell == target) Assert.Equal(TavernMap.NoHop, hop);
                else Assert.InRange(hop, 0, 3);
            }
    }

    [Fact]
    public void Every_place_has_its_body_and_steps_where_the_map_says()
    {
        Assert.Equal(5, TavernMap.Places.Length);
        foreach (var p in TavernMap.Places)
        {
            var body = p.I == 0 ? 'K' : 'O';
            for (var x = p.X; x < p.X + p.W; x++) Assert.Equal(body, TavernMap.Rows[p.Y][x]);
            foreach (var c in p.Cells)
            {
                var (x, y) = CellXY(c);
                Assert.Equal(':', TavernMap.Rows[y][x]);
                Assert.Equal(p.Face == 3 ? p.Y + 1 : p.Y - 1, y);       // приступка — під корпусом (3) чи над ним (1)
                Assert.Equal(p.I, TavernMap.PlaceOf[c]);
            }
        }
        // кожна «:» на мапі — чиясь приступка
        for (var i = 0; i < TavernMap.Cells; i++)
            Assert.Equal(TavernMap.Rows[i / TavernMap.W][i % TavernMap.W] == ':', TavernMap.PlaceOf[i] >= 0);
        Assert.Equal(["Шинквас", "Медовуха", "Квас", "Сидр", "Слив'янка"], TavernMap.Places.Select(p => p.Name));
    }

    [Fact]
    public void Benches_face_their_table_and_the_porch_is_outside_the_door()
    {
        Assert.Equal(64, TavernMap.Benches.Length);
        foreach (var b in TavernMap.Benches)
        {
            var (x, y) = CellXY(b);
            var face = TavernMap.BenchFace[b];
            Assert.Equal('T', TavernMap.Rows[face == 1 ? y + 1 : y - 1][x]);
        }
        Assert.All(TavernMap.Porch, c => Assert.Equal(',', TavernMap.Rows[c / TavernMap.W][c % TavernMap.W]));
        Assert.All(TavernMap.Porch, c => Assert.True(c / TavernMap.W >= 17));
        Assert.Equal(24, TavernMap.Porch.Length);
    }

    [Fact]
    public void Next_hop_table_leads_to_the_target_within_bfs_distance_steps()
    {
        var rng = new Random(7);
        var walk = TavernMap.Walkable;
        for (var pair = 0; pair < 50; pair++)
        {
            var from = walk[rng.Next(walk.Length)];
            var to = walk[rng.Next(walk.Length)];
            var dist = Bfs(from)[to];
            var cell = from;
            var steps = 0;
            while (cell != to)
            {
                var hop = TavernMap.NextHop[to * TavernMap.Cells + cell];
                Assert.InRange(hop, 0, 3);
                cell = (cell / TavernMap.W + TavernCore.DY[hop]) * TavernMap.W + cell % TavernMap.W + TavernCore.DX[hop];
                Assert.True(TavernMap.Pass[cell]);
                steps++;
                Assert.True(steps <= dist, "шлях довший за BFS");
            }
            Assert.Equal(dist, steps);
        }
    }

    static int[] Bfs(int from)
    {
        var d = Enumerable.Repeat(-1, TavernMap.Cells).ToArray();
        var q = new Queue<int>();
        d[from] = 0;
        q.Enqueue(from);
        while (q.Count > 0)
        {
            var u = q.Dequeue();
            for (var k = 0; k < 4; k++)
            {
                int x = u % TavernMap.W + TavernCore.DX[k], y = u / TavernMap.W + TavernCore.DY[k];
                var n = y * TavernMap.W + x;
                if (x < 0 || y < 0 || x >= TavernMap.W || y >= TavernMap.H || !TavernMap.Pass[n] || d[n] >= 0) continue;
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
        Put(player, 6, 5, 2, -3);
        Put(bot, 6, 5, 2, -3);
        var script = new[] { 0, 0, 1, 1, 3, 2, 1, 1, 0, -1 };
        var a = new List<(int, int, int, int)>();
        var b = new List<(int, int, int, int)>();
        for (var t = 0; t < 100; t++)
        {
            player.Want = bot.Want = script[t / 10];
            TavernCore.Step(player);
            TavernCore.Step(bot);
            a.Add((player.X, player.Y, player.Dir, player.State));
            b.Add((bot.X, bot.Y, bot.Dir, bot.State));
        }
        Assert.Equal(a, b);
        Assert.DoesNotContain(a, s => s.Item4 == 2);     // лавою проходять, не сідаючи: сідають лише дією
    }

    [Fact]
    public void Everyone_moves_exactly_three_units_or_not_at_all_when_nobody_fights()
    {
        var core = new TavernCore(new Random(3));
        core.Deal([0, 1, 2, 3], 30);
        core.Open = false;                               // ні бійок, ні кухлів — лише хода
        var rng = new Random(5);
        var was = core.V.Select(v => (v.X, v.Y)).ToArray();
        for (var t = 0; t < 1500; t++)
        {
            if (t % 7 == 0)
                foreach (var v in core.V)
                    if (v.Owner >= 0) v.Want = rng.Next(-1, 4);
            core.TimersAll();
            core.BarmanTick();
            core.ThinkAll();
            core.StepAll();
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
    public void A_guest_never_overlaps_an_obstacle_cell_with_its_box()
    {
        var h = Table(8, seed: 11, options: new { crowd = "big", rounds = "3" });
        var rng = new Random(9);
        for (var t = 0; t < 3000 && G(h).Phase != Tavern.PhaseOver; t++)
        {
            if (t % 5 == 0)
                for (var s = 0; s < 8; s++) h.Input(s, "move", new { dir = rng.Next(-1, 4) });
            if (t % 60 == 30)
                for (var s = 0; s < 8; s++) h.Act(s, s % 2 == 0 ? "punch" : "sit", new { });
            h.Tick();
            foreach (var v in Core(h).V) Assert.True(TavernMap.BoxFits(v.X, v.Y), $"id {v.Id} у ({v.X},{v.Y})");
        }
    }

    [Fact]
    public void A_sitting_guest_rises_for_ten_ticks_before_walking_bot_or_player_alike()
    {
        var core = Bare(1);
        var p = core.V.Single(v => v.Owner == 0);
        var bot = core.V.Single(v => v.Owner < 0);
        foreach (var v in new[] { p, bot })
        {
            Put(v, 7, 6);                                // лава над столом
            v.Sit = true;
            v.Dir = TavernCore.BenchAt(v);
            v.Want = 3;
        }
        Assert.Equal(1, p.Dir);                          // лицем до столу
        var states = new List<(int, int)>();
        for (var t = 0; t < 12; t++)
        {
            TavernCore.Step(p);
            TavernCore.Step(bot);
            Assert.Equal((p.X, p.Y, p.State), (bot.X, bot.Y, bot.State));
            states.Add((p.State, p.Y));
        }
        // 9 тиків ще сидить, десятого встав, одинадцятого пішов
        Assert.All(states.Take(9), s => Assert.Equal(2, s.Item1));
        Assert.Equal(0, states[9].Item1);
        Assert.Equal((1, 6 * 32 + 16 - 3), states[10]);
    }

    [Fact]
    public void Winding_drinking_dazed_fallen_and_knocked_out_guests_do_not_walk()
    {
        var core = Bare(4);
        var guests = core.V.ToArray();
        int[] stateOf = [3, 4, 5, 6, 7];
        for (var i = 0; i < 5; i++)
        {
            var v = Put(guests[i], 3 + i * 2, 9);
            v.Want = 0;
            switch (i)
            {
                case 0: v.Wind = 5; break;
                case 1: v.Drink = 5; v.DrinkPlace = 0; break;
                case 2: v.Dazed = 5; break;
                case 3: v.Fallen = 5; break;
                default: TavernCore.KnockOut(v); v.Want = 0; break;
            }
            TavernCore.Step(v);
            Assert.False(v.Moving);
            Assert.Equal((3 + i * 2) * 32 + 16, v.X);
            Assert.Equal(stateOf[i], v.State);
        }
    }

    [Fact]
    public void Knockback_carries_thirty_six_units_and_stops_at_a_table()
    {
        var core = Bare(1);
        var v = Put(core.V[0], 3, 9);
        TavernCore.Knock(v, 0);
        for (var i = 0; i < TavernCore.KnockTicks; i++)
        {
            TavernCore.Step(v);
            Assert.Equal(i < TavernCore.KnockTicks - 1 ? 8 : 0, v.State);
        }
        Assert.Equal(3 * 32 + 16 + 36, v.X);
        Assert.Equal(0, v.State);
        // униз на стіл — зупиняється одразу
        Put(v, 7, 8);
        TavernCore.Knock(v, 3);
        TavernCore.Step(v);
        TavernCore.Step(v);
        Assert.Equal(8 * 32 + 16 - 6, v.Y);             // перший крок — ще на лаві, другий — уперся
        Assert.Equal(0, v.Knock);
    }

    // =============================================================================================
    // Боти
    // =============================================================================================

    [Fact]
    public void Bots_sit_on_benches_facing_the_table_and_drink_at_the_places()
    {
        var core = new TavernCore(new Random(21));
        core.Deal([], 30);
        int sits = 0, drinks = 0;
        var wasSit = new bool[core.N];
        var places = new HashSet<int>();
        for (var t = 0; t < 3000; t++)
        {
            core.TimersAll();
            foreach (var id in core.Drank) { drinks++; places.Add(TavernCore.PlaceAt(core.V[id])); }
            core.ThinkAll();
            core.StepAll();
            foreach (var v in core.V)
            {
                if (v.Sit && !wasSit[v.Id])
                {
                    sits++;
                    Assert.Equal(TavernCore.BenchAt(v), v.Dir);
                }
                if (v.Drink > 0) Assert.Equal(TavernMap.Places[TavernCore.PlaceAt(v)].Face, v.Dir);
                wasSit[v.Id] = v.Sit;
            }
        }
        output.WriteLine($"за 2 хв на 30 ботів: сідали {sits}, кухлів {drinks}, місць {places.Count}");
        Assert.True(sits > 30, $"сідали {sits}");
        Assert.True(drinks > 30, $"пили {drinks}");
        Assert.Equal(5, places.Count);
    }

    [Fact]
    public void Bots_brawl_now_and_then_with_the_very_same_windup_as_a_player()
    {
        var core = new TavernCore(new Random(22));
        core.Deal([], 32);
        core.ResetBarman();
        core.Barman.NextShout = 1_000_000;
        var since = new int[core.N];
        var gaps = new List<int>();
        int strikes = 0, hits = 0;
        for (var t = 0; t < 1500 * 3; t++)
        {
            core.TimersAll();
            foreach (var id in core.Struck)
            {
                strikes++;
                gaps.Add(t - since[id]);
                var a = core.V[id];
                a.PunchCool = TavernCore.PunchCoolTicks;
                var q = core.Reach(a);
                if (q >= 0) { hits++; core.Hit(a, core.V[q], core.CoinFall()); }
            }
            core.ThinkAll();
            core.StepAll();
            foreach (var v in core.V)
                if (v.Wind == TavernCore.WindTicks - 1) since[v.Id] = t;
        }
        output.WriteLine($"ударів ботів за 3 хв на 32 ботів: {strikes}, влучних {hits}");
        Assert.InRange(strikes, 20, 120);                // ≈ раз на 1,5–9 с на всю корчму: удар — не вирок
        Assert.All(gaps, g => Assert.Equal(TavernCore.WindTicks - 1, g));

        // гравець: натиснув пробіл — у кадрах 9 тиків замаху, на десятому «бам»
        var h = Table(2, seed: 18);
        Go(h);
        Park(h, Me(h, 0).Id);
        var bot = Pin(Core(h).V.First(v => v.Owner < 0));
        var me = Face(h, 0, bot);
        var from = h.Outbox.Count;
        Assert.True(h.Act(0, "punch", new { }).Ok, h.Reply.Message);
        var windup = 0;
        for (var t = 0; t < 12; t++)
        {
            h.Tick();
            var f = LastFrame(h);
            if (f.GetProperty("v")[me.Id * 4 + 3].GetInt32() == 3) windup++;
            if (f.GetProperty("ev").EnumerateArray().Any(e => e[0].GetInt32() == 1)) break;
        }
        Assert.Equal(TavernCore.WindTicks - 1, windup);
        Assert.Contains(Events(h, from), e => Ev(e) is [1, var a, var b, 1, -1] && a == me.Id && b == bot.Id);
    }

    [Fact]
    public void Bots_drink_exactly_as_long_as_a_player_does()
    {
        var core = new TavernCore(new Random(31));
        core.Deal([], 30);
        var since = new int[core.N];
        var gaps = new List<int>();
        for (var t = 0; t < 3000; t++)
        {
            core.TimersAll();
            foreach (var id in core.Drank) gaps.Add(t - since[id]);
            core.ThinkAll();
            core.StepAll();
            foreach (var v in core.V)
                if (v.Drink == TavernCore.DrinkTicks - 1) since[v.Id] = t;
        }
        Assert.NotEmpty(gaps);
        Assert.All(gaps, g => Assert.Equal(TavernCore.DrinkTicks - 1, g));

        var h = Table(2, seed: 19);
        Go(h);
        var me = Me(h, 0);
        Park(h, me.Id, Me(h, 1).Id);
        var (x, y) = CellXY(TavernMap.Places[1].Cells[0]);
        Put(me, x, y);
        Assert.True(h.Act(0, "drink", new { }).Ok, h.Reply.Message);
        var first = -1;
        for (var t = 0; t < 40; t++)
        {
            h.Tick();
            var f = LastFrame(h);
            if (first < 0 && f.GetProperty("v")[me.Id * 4 + 3].GetInt32() == 4) first = t;
            if (f.GetProperty("ev").EnumerateArray().Any(e => Ev(e) is [2, 1]))
            {
                Assert.Equal(TavernCore.DrinkTicks - 1, t - first);
                return;
            }
        }
        Assert.Fail("кухля нема");
    }

    [Fact]
    public void A_hit_bot_sometimes_hits_back_once_it_comes_to()
    {
        var paybacks = 0;
        for (var seed = 1; seed <= 40; seed++)
        {
            var core = Bare(1, seed);
            core.Barman.NextShout = 1_000_000;
            var p = Put(core.V.Single(v => v.Owner == 0), 10, 5, dir: 0);
            var bot = Put(core.V.Single(v => v.Owner < 0), 11, 5);
            core.Hit(p, bot, fall: false);               // відкинуло — бот не лежить, отямиться швидко
            for (var t = 0; t < 60; t++)
            {
                core.TimersAll();
                core.ThinkAll();
                TavernCore.Step(bot);
                p.X = bot.X - 30;                        // кривдник поруч
                p.Y = bot.Y;
                if (bot.Wind > 0)
                {
                    Assert.Equal(2, bot.Dir);             // обернувся до кривдника
                    paybacks++;
                    break;
                }
            }
        }
        output.WriteLine($"дали здачі {paybacks} з 40");
        Assert.InRange(paybacks, 5, 25);
    }

    [Fact]
    public void Bots_keep_their_fists_down_while_the_barman_watches_unless_drunk()
    {
        var core = new TavernCore(new Random(24));
        core.Deal([], 40);
        int calm = 0, watched = 0, drunkWatched = 0;
        foreach (var v in core.V) v.Drunk = v.Id % 2 == 0;
        for (var t = 0; t < 1500 * 4; t++)
        {
            var b = core.Barman;
            b.Mode = t / 500 % 2 == 0 ? 0 : 2;
            b.NextShout = 1_000_000;
            core.TimersAll();
            foreach (var id in core.Struck) core.V[id].PunchCool = TavernCore.PunchCoolTicks;
            core.ThinkAll();
            foreach (var v in core.V)
                if (v.Wind == TavernCore.WindTicks - 1)
                {
                    if (b.Mode == 0) calm++;
                    else if (v.Drunk) drunkWatched++;
                    else watched++;
                }
            core.StepAll();
        }
        output.WriteLine($"замахів: корчмар спокійний {calm}, дивиться — тверезі {watched}, п'яненькі {drunkWatched}");
        Assert.Equal(0, watched);
        Assert.True(drunkWatched > 0);
        Assert.True(calm > drunkWatched);
    }

    [Fact]
    public void Bots_reach_their_targets_and_never_get_stuck()
    {
        var arrivals = 0;
        var blocked = new List<string>();
        for (var seed = 4; seed <= 6; seed++)
        {
            var core = new TavernCore(new Random(seed));
            core.Deal([], 40);
            core.Open = false;
            foreach (var v in core.V) v.Drunk = false;    // хитання — свідоме «уперся», тут його не рахуємо
            var stand = new int[core.N];
            var target = new int[core.N];
            var wander = new int[core.N];
            for (var t = 0; t < 3000; t++)
            {
                for (var i = 0; i < core.N; i++) (stand[i], target[i], wander[i]) = (core.V[i].Stand, core.V[i].Target, core.V[i].Wander);
                core.TimersAll();
                core.ThinkAll();
                core.StepAll();
                foreach (var v in core.V)
                {
                    var fidget = stand[v.Id] > 0 || wander[v.Id] > 0 || v.Wander > 0;
                    if (v.Blocked && v.Target >= 0 && !fidget) blocked.Add($"сід {seed} тик {t} id {v.Id} ({v.X},{v.Y}) ціль {v.Target} → ({v.Tx},{v.Ty}) хоче {v.Want}");
                    if (target[v.Id] >= 0 && v.Target < 0 && (v.Stand > 0 || v.Sit)) arrivals++;
                    Assert.True(TavernMap.BoxFits(v.X, v.Y));
                }
            }
        }
        Assert.True(blocked.Count == 0, string.Join("\n", blocked.Take(10)));
        Assert.True(arrivals > 700, $"дійшли лише {arrivals} разів");
    }

    [Fact]
    public void Drunk_bots_sway_off_their_way_now_and_then()
    {
        var core = new TavernCore(new Random(26));
        core.Deal([], 40);
        core.Open = false;
        foreach (var v in core.V) v.Drunk = v.Id < 10;
        var sways = new int[2];
        for (var t = 0; t < 3000; t++)
        {
            var wander = core.V.Select(v => v.Wander).ToArray();
            core.ThinkAll();
            foreach (var v in core.V)
                if (wander[v.Id] == 0 && v.Wander > 0 && v.Stand == 0 && !v.Sit) sways[v.Drunk ? 0 : 1]++;
            core.StepAll();
        }
        output.WriteLine($"хитань: п'яненькі (10) {sways[0]}, тверезі (30) {sways[1]}");
        Assert.True(sways[0] > sways[1], "п'яненькі хитаються частіше за тверезих");
    }

    [Fact]
    public void Fallen_bot_gets_up_after_two_seconds_and_walks_again()
    {
        var h = Table(2, seed: 5);
        Go(h);
        var bot = Core(h).V.First(v => v.Owner < 0);
        Park(h, Me(h, 0).Id, bot.Id);
        Pin(bot);
        Face(h, 0, bot);
        Assert.True(h.Act(0, "punch", new { }).Ok, h.Reply.Message);
        h.Tick(TavernCore.WindTicks);
        Assert.Equal(6, bot.State);
        h.Tick(TavernCore.FallTicks - 1);
        Assert.Equal(6, bot.State);
        h.Tick();
        Assert.Equal(0, bot.Fallen);
        var walked = false;
        for (var i = 0; i < 200 && !walked; i++)
        {
            h.Tick();
            walked = bot.Moving;
        }
        Assert.True(walked);
    }

    // =============================================================================================
    // Раунд і фази
    // =============================================================================================

    [Fact]
    public void Match_starts_with_a_three_second_look_around_where_fists_and_mugs_wait_but_benches_do_not()
    {
        var h = Table(2, seed: 30);
        Assert.Equal(Tavern.PhaseStart, G(h).Phase);
        Assert.Equal(Tavern.StartTicks, G(h).Left);
        Assert.False(h.Act(0, "punch", new { }).Ok);
        Assert.Equal("Зачекай, корчма ще не відчинилась", h.Reply.Message);
        Assert.False(h.Act(0, "drink", new { }).Ok);
        Assert.Equal("Зачекай, корчма ще не відчинилась", h.Reply.Message);
        var me = Put(Me(h, 0), 7, 6);
        Assert.True(h.Act(0, "sit", new { }).Ok, h.Reply.Message);
        Assert.True(me.Sit);
        var him = Put(Me(h, 1), 10, 9);
        h.Input(1, "move", new { dir = 0 });
        var x = him.X;
        h.Tick(5);
        Assert.NotEqual(x, him.X);                        // ходити можна й на відліку
        h.Tick(Tavern.StartTicks - 5);
        Assert.Equal(Tavern.PhaseGo, G(h).Phase);
        Assert.Equal(Tavern.RoundTicks, G(h).Left);
        Assert.Equal(Tavern.Hearts, S(h, 0).Hearts);
    }

    [Fact]
    public void Round_lasts_sixty_seconds_then_the_most_mugs_on_their_feet_win()
    {
        var h = Table(3, seed: 32);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id, Me(h, 2).Id);
        DrinkAt(h, 1, 3);
        Park(h, Me(h, 0).Id, Me(h, 1).Id, Me(h, 2).Id);
        h.Tick(Tavern.RoundTicks - Tavern.DrinkTicks - 1);
        Assert.Equal(Tavern.PhaseGo, G(h).Phase);
        h.Tick();
        Assert.Equal(Tavern.PhaseReveal, G(h).Phase);
        var r = h.View(null).GetProperty("reveal");
        Assert.Equal("time", r.GetProperty("why").GetString());
        Assert.Equal([1], r.GetProperty("winners").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(Tavern.PtMug + Tavern.PtRound, S(h, 1).Total);
    }

    [Fact]
    public void Timeout_ties_break_on_hearts_and_then_nobody_takes_the_round()
    {
        var h = Table(2, seed: 34);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id);
        h.Tick(Tavern.RoundTicks);
        Assert.Equal("none", h.View(null).GetProperty("reveal").GetProperty("why").GetString());

        var g = Table(2, seed: 36);
        Go(g);
        Park(g, Me(g, 0).Id, Me(g, 1).Id);
        Face(g, 0, Me(g, 1));
        Assert.True(g.Act(0, "punch", new { }).Ok, g.Reply.Message);
        g.Tick(Tavern.RoundTicks);
        var r = g.View(null).GetProperty("reveal");
        Assert.Equal("time", r.GetProperty("why").GetString());
        Assert.Equal([0], r.GetProperty("winners").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(2, S(g, 1).Hearts);
    }

    [Fact]
    public void Three_mugs_in_three_different_places_end_the_round_at_once()
    {
        var h = Table(2, seed: 38);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id);
        DrinkAt(h, 0, 0);
        h.Tick(TavernCore.DrinkCoolTicks);
        DrinkAt(h, 0, 0);                                   // той самий шинквас удруге — не рахується
        Assert.Equal(1, S(h, 0).Mugs);
        h.Tick(TavernCore.DrinkCoolTicks);
        DrinkAt(h, 0, 2);
        h.Tick(TavernCore.DrinkCoolTicks);
        Assert.Equal(Tavern.PhaseGo, G(h).Phase);
        DrinkAt(h, 0, 4);
        Assert.Equal(Tavern.PhaseReveal, G(h).Phase);
        var r = h.View(null).GetProperty("reveal");
        Assert.Equal("mugs", r.GetProperty("why").GetString());
        Assert.Equal([0], r.GetProperty("winners").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(3 * Tavern.PtMug + Tavern.PtRound, S(h, 0).Total);
        Assert.Equal(new[] { true, false, true, false, true }, S(h, 0).Places);
    }

    [Fact]
    public void Last_player_standing_wins_the_round()
    {
        var h = Table(2, seed: 40, options: new { rounds = "3" });
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id);
        S(h, 1).Hearts = 1;
        Face(h, 0, Me(h, 1));
        Assert.True(h.Act(0, "punch", new { }).Ok, h.Reply.Message);
        h.Tick(TavernCore.WindTicks);
        Assert.Equal(Tavern.PhaseReveal, G(h).Phase);
        var r = h.View(null).GetProperty("reveal");
        Assert.Equal("last", r.GetProperty("why").GetString());
        Assert.Equal([0], r.GetProperty("winners").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(Tavern.PtHit + Tavern.PtKo + Tavern.PtRound, S(h, 0).Total);
    }

    [Fact]
    public void Reveal_lasts_six_seconds_freezes_everyone_and_then_the_next_round_starts_fresh()
    {
        var h = Table(3, seed: 42, options: new { rounds = "3" });
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id);
        Face(h, 0, Me(h, 1));
        h.Act(0, "punch", new { });
        h.Tick(Tavern.RoundTicks);
        Assert.Equal(Tavern.PhaseReveal, G(h).Phase);
        var at = Core(h).V.Select(v => (v.X, v.Y, v.State)).ToArray();
        h.Input(2, "move", new { dir = 0 });
        h.Tick(Tavern.RevealTicks - 1);
        Assert.Equal(at, Core(h).V.Select(v => (v.X, v.Y, v.State)).ToArray());
        var oldIds = Enumerable.Range(0, 3).Select(s => S(h, s).Me).ToArray();
        h.Tick();
        Assert.Equal(Tavern.PhaseStart, G(h).Phase);
        Assert.Equal(2, G(h).RoundNo);
        Assert.All(Enumerable.Range(0, 3), s =>
        {
            Assert.Equal(Tavern.Hearts, S(h, s).Hearts);
            Assert.Equal(0, S(h, s).Mugs);
            Assert.True(S(h, s).Alive);
        });
        Assert.NotEqual(oldIds, Enumerable.Range(0, 3).Select(s => S(h, s).Me).ToArray());
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
    }

    [Fact]
    public void After_the_last_round_the_match_finishes_with_totals_in_the_journal()
    {
        var h = Table(2, seed: 44, options: new { rounds = "3" });
        for (var round = 0; round < 3; round++)
        {
            Go(h);
            Park(h, Me(h, 0).Id, Me(h, 1).Id);
            if (round < 2) DrinkAt(h, 1, round + 1);
            h.Tick(Tavern.RoundTicks + Tavern.RevealTicks);
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal(Tavern.PhaseOver, G(h).Phase);
        var line = h.Outbox.OfType<Journal>().Last().Text;
        Assert.Contains("Корчма: Петро 8 : Оля 0", line);
        Assert.Equal([1], h.Finished.Single().Result.Winners);
        Assert.Contains(h.Scores, s => s.Nick == "Петро" && s.Score == 8);
        Assert.Contains(h.Scores, s => s.Nick == "Оля" && s.Score == 0);
    }

    [Fact]
    public void Equal_totals_make_shared_winners_and_all_zero_makes_a_draw()
    {
        var h = Table(2, seed: 46, options: new { rounds = "3" });
        for (var round = 0; round < 3; round++)
        {
            Go(h);
            h.Tick(Tavern.RoundTicks + Tavern.RevealTicks);
        }
        Assert.Empty(h.Finished.Single().Result.Winners);
        Assert.Contains("нічия", h.Outbox.OfType<Journal>().Last().Text);

        var g = Table(3, seed: 48, options: new { rounds = "3" });
        for (var round = 0; round < 3; round++)
        {
            Go(g);
            Park(g, Me(g, 0).Id, Me(g, 1).Id, Me(g, 2).Id);
            if (round == 0) DrinkAt(g, 0, 1);
            if (round == 1) DrinkAt(g, 1, 3);
            g.Tick(Tavern.RoundTicks + Tavern.RevealTicks);
        }
        Assert.Equal([0, 1], g.Finished.Single().Result.Winners.Order());
    }

    [Fact]
    public void Rounds_and_crowd_options_are_honoured_and_junk_falls_back()
    {
        foreach (var (opt, want) in new[] { ("3", 3), ("7", 7), ("5", 5), ("9", 5), ("x", 5) })
        {
            var h = Table(2, seed: 50, options: new { rounds = opt });
            Assert.Equal(want, h.View(null).GetProperty("of").GetInt32());
        }
        Assert.Equal(5, Table(2, seed: 50).View(null).GetProperty("of").GetInt32());
        Assert.Equal(2 + 24, Core(Table(2, seed: 52)).N);
        Assert.Equal(4 + 30, Core(Table(4, seed: 52)).N);
        Assert.Equal(8 + 40, Core(Table(8, seed: 52)).N);
        Assert.Equal(3 + 24, Core(Table(3, seed: 52, options: new { crowd = "small" })).N);
        Assert.Equal(3 + 40, Core(Table(3, seed: 52, options: new { crowd = "big" })).N);
        Assert.Equal(3 + 27, Core(Table(3, seed: 52, options: new { crowd = "junk" })).N);
    }

    // =============================================================================================
    // Кулак
    // =============================================================================================

    [Fact]
    public void Punching_a_bot_knocks_it_down_and_dazes_the_puncher_for_everyone_to_see()
    {
        var h = Table(2, seed: 54);
        Go(h);
        var bot = Core(h).V.First(v => v.Owner < 0);
        Park(h, Me(h, 0).Id, bot.Id);
        Pin(bot);
        var me = Face(h, 0, bot);
        var from = h.Outbox.Count;
        Assert.True(h.Act(0, "punch", new { }).Ok, h.Reply.Message);
        h.Tick(TavernCore.WindTicks);
        Assert.Contains(Events(h, from), e => Ev(e) is [1, var a, var b, 1, -1] && a == me.Id && b == bot.Id);
        Assert.Equal(TavernCore.FallTicks, bot.Fallen);
        Assert.Equal(TavernCore.DazeTicks, me.Dazed);
        var f = LastFrame(h).GetProperty("v");
        Assert.Equal(5, f[me.Id * 4 + 3].GetInt32());       // отетерів — це видно всім
        Assert.Equal(6, f[bot.Id * 4 + 3].GetInt32());
        Assert.Equal(Tavern.Hearts, S(h, 0).Hearts);
        Assert.Equal(0, S(h, 0).Total);                      // за бота очок нема
        // отетерілий не ходить і не б'ється
        h.Input(0, "move", new { dir = 0 });
        var x = me.X;
        h.Tick();
        Assert.Equal(x, me.X);
        Assert.False(h.Act(0, "punch", new { }).Ok);
        Assert.Equal("Тебе ще хитає", h.Reply.Message);
    }

    [Fact]
    public void Punching_a_player_takes_a_heart_and_knocks_him_back_without_daze()
    {
        var h = Table(2, seed: 56);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id);
        var him = Me(h, 1);
        var me = Face(h, 0, him);
        var from = h.Outbox.Count;
        Assert.True(h.Act(0, "punch", new { }).Ok, h.Reply.Message);
        h.Tick(TavernCore.WindTicks);
        Assert.Contains(Events(h, from), e => Ev(e) is [1, var a, var b, 2, -1] && a == me.Id && b == him.Id);
        Assert.Equal(Tavern.Hearts - 1, S(h, 1).Hearts);
        Assert.Equal(0, me.Dazed);
        Assert.Equal(8, him.State);
        h.Tick(TavernCore.KnockTicks);
        Assert.Equal(11 * 32 + 16 + TavernCore.KnockTicks * TavernCore.KnockSpeed, him.X);
        Assert.Equal(0, him.State);
        Assert.True(S(h, 1).Alive);
        // чужих сердець не видно нікому: ні глядачу, ні йому самому в чужих місцях
        Assert.DoesNotContain("\"hearts\"", Views.Text(h.View(null)));
        Assert.Equal(Tavern.Hearts - 1, h.View(1).GetProperty("me").GetProperty("hearts").GetInt32());
        Assert.Equal(Tavern.Hearts, h.View(0).GetProperty("me").GetProperty("hearts").GetInt32());
    }

    [Fact]
    public void The_third_heart_knocks_a_player_out_and_names_him_to_everyone()
    {
        var h = Table(3, seed: 58);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id);
        var him = Me(h, 1);
        var from = h.Outbox.Count;
        for (var i = 0; i < Tavern.Hearts; i++)
        {
            Face(h, 0, him);
            Assert.True(h.Act(0, "punch", new { }).Ok, h.Reply.Message);
            h.Tick(TavernCore.PunchCoolTicks + TavernCore.WindTicks);
        }
        Assert.Contains(Events(h, from), e => Ev(e) is [1, _, var b, 3, 1] && b == him.Id);
        Assert.True(him.Out);
        Assert.Equal(7, him.State);
        Assert.False(S(h, 1).Alive);
        var dead = h.View(2).GetProperty("dead").EnumerateArray().Single();
        Assert.Equal((1, him.Id), (dead.GetProperty("seat").GetInt32(), dead.GetProperty("id").GetInt32()));
        Assert.Equal(Tavern.PhaseGo, G(h).Phase);             // на трьох раунд іде далі
        Assert.Equal(3 * Tavern.PtHit + Tavern.PtKo, S(h, 0).Total);
        Assert.Equal(1, S(h, 0).Kos);
        // вибулого не б'ють, і сам він уже нічого не може
        Face(h, 0, him);
        TavernCore.KnockOut(him);
        Assert.Equal(-1, Core(h).Reach(Me(h, 0)));
        Assert.False(h.Act(1, "punch", new { }).Ok);
        Assert.Equal("Тебе вже винесли — дивись, хто кого", h.Reply.Message);
    }

    [Fact]
    public void Punch_with_a_direction_turns_first_and_a_crooked_direction_is_refused()
    {
        var h = Table(2, seed: 60);
        Go(h);
        var bot = Core(h).V.First(v => v.Owner < 0);
        Park(h, Me(h, 0).Id, bot.Id);
        Pin(bot);
        var me = Put(Me(h, 0), 10, 5, dir: 0);
        Put(bot, 10, 4);                                     // бот згори, а я дивлюсь праворуч
        Assert.True(h.Act(0, "punch", new { dir = 3 }).Ok, h.Reply.Message);
        Assert.Equal(3, me.Dir);
        h.Tick(TavernCore.WindTicks);
        Assert.Equal(6, bot.State);
        foreach (var bad in new object[] { new { dir = 4 }, new { dir = -1 }, new { dir = "up" }, "x" })
        {
            Assert.False(h.Act(0, "punch", bad).Ok);
            Assert.Equal("Такого напрямку нема", h.Reply.Message);
        }
    }

    [Fact]
    public void Fists_need_a_second_between_punches_and_cannot_swing_sitting_or_mid_swing()
    {
        var h = Table(2, seed: 62);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id);
        var me = Put(Me(h, 0), 10, 9, dir: 0);
        Assert.True(h.Act(0, "punch", new { }).Ok);
        Assert.False(h.Act(0, "punch", new { }).Ok);
        Assert.Equal("Ти вже замахнувся", h.Reply.Message);
        h.Tick(TavernCore.WindTicks);
        Assert.False(h.Act(0, "punch", new { }).Ok);
        Assert.Equal("Кулак ще не відпочив", h.Reply.Message);
        h.Tick(TavernCore.PunchCoolTicks);
        Put(me, 7, 6);
        Assert.True(h.Act(0, "sit", new { }).Ok);
        Assert.False(h.Act(0, "punch", new { }).Ok);
        Assert.Equal("Сидячи не розмахнешся — встань", h.Reply.Message);
        Assert.False(h.Act(0, "drink", new { }).Ok);
        Assert.Equal("Кухоль — біля шинквасу чи бочки, не на лаві", h.Reply.Message);
        Assert.True(h.Act(0, "sit", new { }).Ok);           // устати — теж «sit»
        h.Tick(TavernCore.RiseTicks);
        Assert.False(me.Sit);
        Assert.True(h.Act(0, "punch", new { }).Ok, h.Reply.Message);
    }

    [Fact]
    public void Swinging_at_thin_air_is_a_public_whiff_and_costs_the_cooldown()
    {
        var h = Table(2, seed: 64);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id);
        var me = Put(Me(h, 0), 10, 9, dir: 0);
        var from = h.Outbox.Count;
        Assert.True(h.Act(0, "punch", new { }).Ok);
        h.Tick(TavernCore.WindTicks);
        Assert.Contains(Events(h, from), e => Ev(e) is [1, var a, -1, 0, -1] && a == me.Id);
        Assert.Equal(0, me.Dazed);
        Assert.Equal(TavernCore.PunchCoolTicks, me.PunchCool);
    }

    [Fact]
    public void A_sitting_guest_can_be_hit_and_is_knocked_off_the_bench()
    {
        var h = Table(2, seed: 66);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id);
        var him = Put(Me(h, 1), 7, 6);
        Assert.True(h.Act(1, "sit", new { }).Ok);
        Assert.Equal(2, him.State);
        Put(Me(h, 0), 6, 6, dir: 0);
        Assert.True(h.Act(0, "punch", new { }).Ok);
        h.Tick(TavernCore.WindTicks);
        Assert.False(him.Sit);
        Assert.Equal(8, him.State);
        Assert.Equal(Tavern.Hearts - 1, S(h, 1).Hearts);
    }

    [Fact]
    public void A_punch_spills_the_mug_so_the_drink_does_not_count()
    {
        var h = Table(2, seed: 68);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id);
        var (x, y) = CellXY(TavernMap.Places[0].Cells[2]);
        Put(Me(h, 1), x, y);
        Assert.True(h.Act(1, "drink", new { }).Ok);
        Put(Me(h, 0), x - 1, y, dir: 0);
        Assert.True(h.Act(0, "punch", new { }).Ok);
        var from = h.Outbox.Count;
        h.Tick(Tavern.DrinkTicks + 5);
        Assert.DoesNotContain(Events(h, from), e => e[0].GetInt32() == 2);
        Assert.Equal(0, S(h, 1).Mugs);
    }

    // =============================================================================================
    // Корчмар
    // =============================================================================================

    [Fact]
    public void The_barman_shouts_then_watches_and_a_punch_then_throws_you_out_for_a_heart()
    {
        var h = Table(2, seed: 70);
        Go(h);
        var bot = Core(h).V.First(v => v.Owner < 0);
        Park(h, Me(h, 0).Id, bot.Id);
        Pin(bot);
        var b = Core(h).Barman;
        b.NextShout = 1;
        var from = h.Outbox.Count;
        h.Tick();
        Assert.Contains(Events(h, from), e => Ev(e) is [4, 1]);
        Assert.Equal(1, b.Mode);
        Assert.Equal(1, LastFrame(h).GetProperty("k")[3].GetInt32());
        h.Tick(TavernCore.ShoutTicks);
        Assert.True(b.Watching);
        Assert.Equal(2, LastFrame(h).GetProperty("k")[3].GetInt32());
        var me = Face(h, 0, bot);
        from = h.Outbox.Count;
        Assert.True(h.Act(0, "punch", new { }).Ok);
        h.Tick(TavernCore.WindTicks);
        Assert.Contains(Events(h, from), e => Ev(e) is [3, var id, -1] && id == me.Id);
        Assert.True(me.Y >= 17 * 32, "за двері — на ґанок");
        Assert.Equal(6, me.State);
        Assert.Equal(Tavern.Hearts - 1, S(h, 0).Hearts);
        Assert.True(S(h, 0).Alive);
        // полежав — і сам іде назад
        h.Tick(TavernCore.FallTicks);
        h.Input(0, "move", new { dir = 0 });
        var x = me.X;
        h.Tick(3);
        Assert.True(me.X > x);
        // корчмар подивився й заспокоївся
        h.Tick(TavernCore.WatchTicks);
        Assert.True(b.Calm);
    }

    [Fact]
    public void A_punch_that_lands_while_he_is_still_shouting_is_forgiven()
    {
        var h = Table(2, seed: 72);
        Go(h);
        var bot = Core(h).V.First(v => v.Owner < 0);
        Park(h, Me(h, 0).Id, bot.Id);
        Pin(bot);
        var me = Face(h, 0, bot);
        Core(h).Barman.NextShout = 1;
        h.Tick();
        Assert.Equal(1, Core(h).Barman.Mode);
        var from = h.Outbox.Count;
        Assert.True(h.Act(0, "punch", new { }).Ok);
        h.Tick(TavernCore.WindTicks);
        Assert.DoesNotContain(Events(h, from), e => e[0].GetInt32() == 3);
        Assert.True(me.Y < 16 * 32);
        Assert.Equal(Tavern.Hearts, S(h, 0).Hearts);
    }

    [Fact]
    public void Thrown_out_with_the_last_heart_is_out_of_the_round_by_name()
    {
        var h = Table(2, seed: 74);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id);
        S(h, 0).Hearts = 1;
        var b = Core(h).Barman;
        b.Mode = 2;
        b.Left = 100;
        var me = Put(Me(h, 0), 10, 9, dir: 0);
        var from = h.Outbox.Count;
        Assert.True(h.Act(0, "punch", new { }).Ok);
        h.Tick(TavernCore.WindTicks);
        Assert.Contains(Events(h, from), e => Ev(e) is [3, var id, 0] && id == me.Id);
        Assert.True(me.Out);
        Assert.Equal(Tavern.PhaseReveal, G(h).Phase);
        Assert.Equal([1], h.View(null).GetProperty("reveal").GetProperty("winners").EnumerateArray().Select(e => e.GetInt32()));
    }

    [Fact]
    public void A_landed_punch_can_hurry_the_barman_and_the_barman_rides_in_every_frame()
    {
        var hurried = 0;
        for (var seed = 1; seed <= 30; seed++)
        {
            var core = Bare(1, seed);
            core.Barman.NextShout = 1000;
            core.Provoke();
            if (core.Barman.NextShout <= TavernCore.ProvokeMax) hurried++;
        }
        Assert.InRange(hurried, 3, 20);

        var h = Table(2, seed: 76);
        h.Tick();
        var k = LastFrame(h).GetProperty("k");
        Assert.Equal(4, k.GetArrayLength());
        Assert.InRange(k[0].GetInt32(), TavernMap.BarmanMinX, TavernMap.BarmanMaxX);
        Assert.Equal(TavernMap.BarmanY, k[1].GetInt32());
        // корчмар ходить своїм закутком, у залу не виходить
        Go(h);
        for (var t = 0; t < 600; t++)
        {
            h.Tick();
            var b = Core(h).Barman;
            Assert.InRange(b.X, TavernMap.BarmanMinX, TavernMap.BarmanMaxX);
        }
    }

    // =============================================================================================
    // Кухлі
    // =============================================================================================

    [Fact]
    public void A_mug_needs_the_step_of_a_place_takes_a_second_and_then_a_breather()
    {
        var h = Table(2, seed: 78);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id);
        var me = Put(Me(h, 0), 10, 9);
        Assert.False(h.Act(0, "drink", new { }).Ok);
        Assert.Equal("Підійди до шинквасу чи бочки", h.Reply.Message);
        var (x, y) = CellXY(TavernMap.Places[3].Cells[1]);
        Put(me, x, y, dir: 2);
        var from = h.Outbox.Count;
        Assert.True(h.Act(0, "drink", new { }).Ok);
        Assert.Equal(TavernMap.Places[3].Face, me.Dir);
        Assert.False(h.Act(0, "drink", new { }).Ok);
        Assert.Equal("Ти вже п'єш", h.Reply.Message);
        h.Input(0, "move", new { dir = 2 });
        h.Tick(Tavern.DrinkTicks - 1);
        Assert.Equal(x * 32 + 16, me.X);                   // п'є — не йде
        Assert.DoesNotContain(Events(h, from), e => e[0].GetInt32() == 2);
        h.Input(0, "move", new { dir = -1 });
        h.Tick();
        Assert.Contains(Events(h, from), e => Ev(e) is [2, 3]);
        Assert.Equal(1, S(h, 0).Mugs);
        Assert.False(h.Act(0, "drink", new { }).Ok);
        Assert.Equal("Дай духу перевести", h.Reply.Message);
        h.Tick(TavernCore.DrinkCoolTicks);
        Assert.True(h.Act(0, "drink", new { }).Ok, h.Reply.Message);
    }

    // =============================================================================================
    // Вихід, F5, «Ще раз»
    // =============================================================================================

    [Fact]
    public void A_leaver_becomes_a_bot_and_the_round_goes_on()
    {
        var h = Table(3, seed: 80);
        Go(h);
        var his = Me(h, 2);
        var n = Core(h).N;
        h.Leave("Ганна");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(-1, his.Owner);
        Assert.Equal(n, Core(h).N);
        var moved = false;
        var at = (his.X, his.Y);
        for (var i = 0; i < 300 && !moved; i++)
        {
            h.Tick();
            moved = (his.X, his.Y) != at;
        }
        Assert.True(moved, "колишній гравець-бот так і стоїть");
        Assert.Equal(4 * n, LastFrame(h).GetProperty("v").GetArrayLength());
        var seat = h.View(null).GetProperty("seats").EnumerateArray().Single(s => s.GetProperty("seat").GetInt32() == 2);
        Assert.True(seat.GetProperty("out").GetBoolean());
        Assert.Equal("Ганна", seat.GetProperty("nick").GetString());
        h.Tick(Tavern.RoundTicks + Tavern.RevealTicks);
        Assert.Equal(2, G(h).RoundNo);
        Assert.Equal(n, Core(h).N);
        Assert.Equal(2, Core(h).V.Count(v => v.Owner >= 0));
    }

    [Fact]
    public void When_only_one_player_remains_the_match_ends_in_his_favour_with_a_reveal()
    {
        var h = Table(2, seed: 82);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id);
        DrinkAt(h, 1, 2);
        var olya = S(h, 0).Me;
        h.Leave("Оля");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Finished.Single().Result.Winners);
        Assert.Contains("Петро допиває", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Contains(h.Scores, s => s.Nick == "Петро" && s.Score == 1);
        var v = h.View(1);
        Assert.Equal("left", v.GetProperty("result").GetProperty("why").GetString());
        var ids = v.GetProperty("reveal").GetProperty("ids").EnumerateArray().ToDictionary(e => e.GetProperty("seat").GetInt32(), e => e.GetProperty("id").GetInt32());
        Assert.Equal(olya, ids[0]);
        Assert.All(Core(h).V, q => Assert.False(q.Moving));
    }

    [Fact]
    public void Rematch_gives_a_clean_match_with_rotated_seats()
    {
        var h = Table(2, seed: 84, options: new { rounds = "3" });
        for (var i = 0; i < 3; i++)
        {
            Go(h);
            Park(h, Me(h, 0).Id, Me(h, 1).Id);
            DrinkAt(h, 0, 0);
            h.Tick(Tavern.RoundTicks + Tavern.RevealTicks);
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        h.Rematch("Оля");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("Петро", h.NickOf(0));
        Assert.Equal(Tavern.PhaseStart, G(h).Phase);
        Assert.Equal(1, G(h).RoundNo);
        Assert.All(new[] { 0, 1 }, s => Assert.Equal(0, S(h, s).Total));
        Assert.All(new[] { 0, 1 }, s => Assert.Equal(Tavern.Hearts, S(h, s).Hearts));
        Assert.Equal("Петро", h.View(null).GetProperty("seats")[0].GetProperty("nick").GetString());
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("result").ValueKind);
    }

    [Fact]
    public void Move_from_a_knocked_out_player_or_during_the_reveal_is_swallowed_silently()
    {
        var h = Table(3, seed: 86);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id);
        var him = Me(h, 1);
        S(h, 1).Hearts = 1;
        Face(h, 0, him);
        h.Act(0, "punch", new { });
        h.Tick(TavernCore.WindTicks);
        Assert.True(him.Out);
        var x = him.X;
        h.Input(1, "move", new { dir = 0 });
        Assert.True(h.Act(1, "move", new { dir = 0 }).Ok);         // вибулому — «так», але ні кроку
        h.Tick(5);
        Assert.Equal(x, him.X);
        Assert.False(h.Act(0, "move", new { dir = 7 }).Ok);
        Assert.Equal("Такого напрямку нема", h.Reply.Message);
        h.Tick(Tavern.RoundTicks);
        Assert.Equal(Tavern.PhaseReveal, G(h).Phase);
        Assert.True(h.Act(0, "move", 2).Ok);
        Assert.Equal(-1, Me(h, 0).Want);
    }

    [Fact]
    public void Nothing_works_before_the_host_starts_and_every_seat_view_carries_its_own_me_for_F5()
    {
        var h = new RoomHarness("tavern", seed: 1);
        h.Join("Оля");
        h.Join("Петро");
        var view = h.View(0);
        Assert.Equal("lobby", view.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("me").ValueKind);
        Assert.Equal(24 * 4, view.GetProperty("v").GetArrayLength());
        var game = (Tavern)h.Room.Game;
        Assert.Equal("Партія ще не почалась", game.Act(0, "punch", Views.Payload(new { })).Message);
        Assert.False(game.Act(0, "move", Views.Payload(new { dir = 1 })).Ok);

        h.Start();
        Go(h);
        h.Tick(100);
        // F5: місце те саме — вид знову каже, хто я, з серцями й кухлями
        var me = h.View(1).GetProperty("me");
        Assert.Equal(S(h, 1).Me, me.GetProperty("id").GetInt32());
        Assert.Equal(S(h, 1).Hearts, me.GetProperty("hearts").GetInt32());
    }

    [Fact]
    public void A_held_arrow_that_is_not_confirmed_for_three_seconds_is_let_go()
    {
        var h = Table(2, seed: 88);
        Go(h);
        var me = Me(h, 0);
        Park(h, me.Id, Me(h, 1).Id);
        Put(me, 2, 9);
        h.Input(0, "move", new { dir = 0 });
        for (var i = 0; i < 4; i++)
        {
            h.Tick(25);
            h.Input(0, "move", new { dir = 0 });
        }
        Assert.True(me.Moving);
        h.Tick(Tavern.MoveHoldTicks + 1);
        Assert.Equal(-1, me.Want);
        var at = me.X;
        h.Tick(50);
        Assert.Equal(at, me.X);
    }

    // =============================================================================================
    // Приховане
    // =============================================================================================

    [Fact]
    public void Frame_has_four_numbers_per_guest_four_for_the_barman_and_no_other_keys()
    {
        var h = Table(5, seed: 90);
        Go(h);
        h.Tick();
        var f = LastFrame(h);
        Assert.Equal(["t", "ph", "left", "v", "k", "ev"], f.EnumerateObject().Select(p => p.Name));
        Assert.Equal(4 * Core(h).N, f.GetProperty("v").GetArrayLength());
        Assert.Equal(4, f.GetProperty("k").GetArrayLength());
        Assert.All(f.GetProperty("v").EnumerateArray(), e => Assert.Equal(JsonValueKind.Number, e.ValueKind));
    }

    [Fact]
    public void Frame_json_never_mentions_seats_hearts_or_mugs()
    {
        var h = Table(4, seed: 92);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id);
        Face(h, 0, Me(h, 1));
        h.Act(0, "punch", new { });
        h.Tick(TavernCore.WindTicks);
        DrinkAt(h, 2, 1);
        foreach (var f in h.Outbox.OfType<RoomFrame>())
        {
            var text = Views.Text(f.Frame);
            foreach (var word in new[] { "\"seat", "\"me", "\"owner", "\"heart", "\"mug", "\"nick", "\"alive", "\"place" })
                Assert.DoesNotContain(word, text);
        }
    }

    [Fact]
    public void Player_ids_are_shuffled_among_bots_across_seeds()
    {
        var high = 0;
        var notFirst = 0;
        for (var seed = 1; seed <= 100; seed++)
        {
            var core = new TavernCore(new Random(seed));
            core.Deal([0, 1], 24);
            var ids = core.V.Where(v => v.Owner >= 0).Select(v => v.Id).ToArray();
            if (ids.Any(id => id >= core.N / 2)) high++;
            if (ids.Any(id => id >= 2)) notFirst++;
        }
        Assert.True(high > 50, $"{high}");
        Assert.True(notFirst > 90, $"{notFirst}");
    }

    [Fact]
    public void Watcher_view_has_no_me_and_other_seats_differ_only_by_their_own_me()
    {
        var h = Table(3, seed: 94);
        Go(h);
        var watcher = h.View(null);
        Assert.Equal(JsonValueKind.Null, watcher.GetProperty("me").ValueKind);
        Assert.Equal(JsonValueKind.Null, watcher.GetProperty("reveal").ValueKind);
        Assert.Empty(watcher.GetProperty("dead").EnumerateArray());
        var mine = h.View(0).GetProperty("me");
        Assert.Equal(S(h, 0).Me, mine.GetProperty("id").GetInt32());
        Assert.Equal(Strip(watcher), Strip(h.View(1)));
        Assert.Equal(Strip(watcher), Strip(h.View(2)));
        Assert.DoesNotContain("\"hearts\"", Strip(h.View(0)));
    }

    /// <summary>Вид без <c>me</c> — щоб порівняти два види.</summary>
    static string Strip(JsonElement view)
    {
        var d = view.EnumerateObject().Where(p => p.Name != "me").ToDictionary(p => p.Name, p => p.Value);
        return Views.Text(d);
    }

    [Fact]
    public void A_knocked_out_player_sees_what_a_watcher_sees_plus_his_own_me()
    {
        var h = Table(3, seed: 96);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id);
        var him = Me(h, 1);
        S(h, 1).Hearts = 1;
        Face(h, 0, him);
        h.Act(0, "punch", new { });
        h.Tick(TavernCore.WindTicks);
        var dead = h.View(1);
        Assert.False(dead.GetProperty("me").GetProperty("alive").GetBoolean());
        Assert.Equal(Strip(h.View(null)), Strip(dead));
        var ids = dead.GetProperty("dead").EnumerateArray().Select(d => d.GetProperty("id").GetInt32()).ToList();
        Assert.Equal([him.Id], ids);
        Assert.DoesNotContain("\"ids\"", Views.Text(dead));
    }

    [Fact]
    public void Reveal_ids_appear_only_in_reveal_and_over_phases()
    {
        var h = Table(2, seed: 98, options: new { rounds = "3" });
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
        Go(h);
        h.Tick(Tavern.RoundTicks - 1);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
        Assert.DoesNotContain("\"trails\"", Views.Text(h.View(1)));
        h.Tick();
        var reveal = h.View(null).GetProperty("reveal");
        var ids = reveal.GetProperty("ids").EnumerateArray().Select(e => (e.GetProperty("seat").GetInt32(), e.GetProperty("id").GetInt32())).ToList();
        Assert.Equal([(0, S(h, 0).Me), (1, S(h, 1).Me)], ids);
        Assert.Equal(2, reveal.GetProperty("trails").GetArrayLength());
        h.Tick(Tavern.RevealTicks);
        Assert.Equal(Tavern.PhaseStart, G(h).Phase);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
    }

    [Fact]
    public void Round_points_and_mugs_stay_hidden_until_the_reveal()
    {
        var h = Table(3, seed: 100, options: new { rounds = "3" });
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id, Me(h, 2).Id);
        Face(h, 0, Me(h, 1));
        h.Act(0, "punch", new { });
        h.Tick(TavernCore.WindTicks);
        DrinkAt(h, 2, 4);
        Assert.Equal(Tavern.PtHit, S(h, 0).Total);
        Assert.Equal(1, S(h, 2).Mugs);
        foreach (int? seat in new int?[] { null, 0, 1, 2 })
        {
            var seats = h.View(seat).GetProperty("seats").EnumerateArray().ToList();
            Assert.All(seats, s => Assert.Equal(0, s.GetProperty("total").GetInt32()));
            Assert.All(seats, s => Assert.Equal(JsonValueKind.Null, s.GetProperty("mugs").ValueKind));
        }
        while (G(h).Phase == Tavern.PhaseGo) h.Tick();
        var open = h.View(null).GetProperty("seats").EnumerateArray().ToDictionary(s => s.GetProperty("seat").GetInt32());
        Assert.Equal(1, open[2].GetProperty("mugs").GetInt32());
        Assert.Equal(S(h, 0).Total, open[0].GetProperty("total").GetInt32());
    }

    [Fact]
    public void A_bots_mug_sends_views_just_like_a_players_and_a_players_windup_sends_none()
    {
        var h = Table(3, seed: 102);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id, Me(h, 2).Id);
        var bot = Core(h).V.First(v => v.Owner < 0);
        var (x, y) = CellXY(TavernMap.Places[2].Cells[0]);
        Put(bot, x, y);
        bot.Drink = 3;
        bot.DrinkPlace = 2;
        var views = h.Outbox.OfType<RoomViews>().Count();
        h.Tick(3);
        Assert.Contains(LastFrame(h).GetProperty("ev").EnumerateArray(), e => Ev(e) is [2, 2]);
        Assert.True(h.Outbox.OfType<RoomViews>().Count() > views, "бот випив — види мають полетіти, як після кухля гравця");

        // замах гравця не шле видів (бот замахується без них): «вид прийшов» не мусить казати «це гравець»
        Put(Me(h, 0), 10, 9);
        views = h.Outbox.OfType<RoomViews>().Count();
        Assert.True(h.Act(0, "punch", new { }).Ok);
        h.Tick(TavernCore.WindTicks - 1);
        Assert.Equal(views, h.Outbox.OfType<RoomViews>().Count());
        h.Tick();
        Assert.True(h.Outbox.OfType<RoomViews>().Count() > views, "удар долетів — види летять для всіх ударів однаково");
    }

    [Fact]
    public void Per_tick_displacement_and_state_values_of_players_are_a_subset_of_those_of_bots()
    {
        var h = Table(4, seed: 104);
        var rng = new Random(3);
        var core = Core(h);
        var bots = new HashSet<(int, int, int)>();
        var players = new HashSet<(int, int, int)>();
        var was = core.V.Select(v => (v.X, v.Y)).ToArray();
        for (var t = 0; t < 1400 && G(h).Phase is Tavern.PhaseStart or Tavern.PhaseGo; t++)
        {
            if (t % 9 == 0)
                for (var s = 0; s < 4; s++) h.Input(s, "move", new { dir = rng.Next(-1, 4) });
            if (t % 97 == 5)
                for (var s = 0; s < 4; s++) h.Act(s, "sit", new { });
            h.Tick();
            foreach (var v in core.V)
            {
                var step = (v.X - was[v.Id].X, v.Y - was[v.Id].Y, v.State);
                if (Math.Abs(step.Item1) + Math.Abs(step.Item2) <= 12)       // виніс за двері — стрибок, не крок
                    (v.Owner >= 0 ? players : bots).Add(step);
                was[v.Id] = (v.X, v.Y);
            }
        }
        output.WriteLine($"кроків-станів: гравці {players.Count}, боти {bots.Count}");
        Assert.Subset(bots, players);
    }

    /// <summary>
    /// Людина за клавіатурою: тримає стрілку 5–41 тик (інколи одразу перемикає на іншу), відпускає на 1–31 тик,
    /// зрідка задумується на 4–10 с. Так і ходять, і стоять живі гравці.
    /// </summary>
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

    /// <summary>Що видно в кадрах про одну сторону: де стоять і якою смугою ходять.</summary>
    sealed class Trace
    {
        public readonly HashSet<(int, int)> Spots = [];
        public long Standing, StandingOff;
        public readonly HashSet<(int, int)> Lanes = [];
        public long Moving, MovingOff;

        public static bool Off(int r) => Math.Abs(r - 16) > 8;
    }

    /// <summary>Корчма на голому ядрі (без бійок і кухлів): 4 «людини» і 30 ботів, 6000 тиків на сід.</summary>
    static (Trace Players, Trace Bots) Observe(int seeds, int ticks = 6000)
    {
        var players = new Trace();
        var bots = new Trace();
        for (var seed = 1; seed <= seeds; seed++)
        {
            var core = new TavernCore(new Random(seed));
            core.Deal([0, 1, 2, 3], 30);
            core.Open = false;
            var humans = Enumerable.Range(0, 4).Select(i => new Human(new Random(seed * 10 + i))).ToArray();
            var px = core.V.Select(v => v.X).ToArray();
            var py = core.V.Select(v => v.Y).ToArray();
            for (var t = 0; t < ticks; t++)
            {
                foreach (var v in core.V)
                    if (v.Owner >= 0) v.Want = humans[v.Owner].Next();
                core.TimersAll();
                core.ThinkAll();
                core.StepAll();
                foreach (var v in core.V)
                {
                    var side = v.Owner >= 0 ? players : bots;
                    int rx = v.X % 32, ry = v.Y % 32;
                    if (v.State == 0 && v.X == px[v.Id] && v.Y == py[v.Id])
                    {
                        side.Standing++;
                        if (Trace.Off(rx) || Trace.Off(ry)) side.StandingOff++;
                        side.Spots.Add((rx / 4, ry / 4));
                    }
                    else if (v.State == 1)
                    {
                        var across = v.Dir is 0 or 2 ? ry : rx;
                        side.Moving++;
                        if (Trace.Off(across)) side.MovingOff++;
                        side.Lanes.Add((v.Dir & 1, across / 4));
                    }
                    px[v.Id] = v.X;
                    py[v.Id] = v.Y;
                }
            }
        }
        return (players, bots);
    }

    [Fact]
    public void Players_stand_on_spots_and_walk_lanes_where_bots_do_too()
    {
        var (players, bots) = Observe(3);
        output.WriteLine($"стоять поза ±8: гравці {players.StandingOff * 100 / players.Standing}%, боти {bots.StandingOff * 100 / bots.Standing}%; ідуть поза смугою ±8: гравці {players.MovingOff * 100 / players.Moving}%, боти {bots.MovingOff * 100 / bots.Moving}%");
        Assert.Subset(bots.Spots, players.Spots);
        Assert.Subset(bots.Lanes, players.Lanes);
        Assert.True(bots.StandingOff * 2 >= players.StandingOff * bots.Standing / players.Standing);
        Assert.True(bots.MovingOff * 2 >= players.MovingOff * bots.Moving / players.Moving);
    }

    // =============================================================================================
    // Контракт і детермінізм
    // =============================================================================================

    [Fact]
    public void The_server_accepts_exactly_what_the_module_sends()
    {
        var h = Table(2, seed: 106);
        var me = Me(h, 0);
        Put(me, 10, 5);
        h.Input(0, "move", new { dir = 0 });            // як шле модуль на keydown
        h.Tick();
        Assert.Equal(0, me.Want);
        h.Input(0, "move", 2);                           // голе число теж
        Assert.Equal(2, me.Want);
        h.Input(0, "move", new { dir = -1 });           // keyup
        Assert.Equal(-1, me.Want);
        Go(h);
        var bot = Core(h).V.First(v => v.Owner < 0);
        Park(h, me.Id, bot.Id);
        Pin(bot);
        Face(h, 0, bot);
        Assert.True(h.Act(0, "punch", new { }).Ok, h.Reply.Message);           // пробіл
        h.Tick(TavernCore.WindTicks + TavernCore.DazeTicks + TavernCore.PunchCoolTicks);
        Calm(h);                                          // бійку почув корчмар — хай не гримає посеред тесту
        Put(me, 10, 9, dir: 0);
        Assert.True(h.Act(0, "punch", new { dir = 1 }).Ok, h.Reply.Message);    // клік мишкою: у бік кліку
        h.Tick(TavernCore.WindTicks);
        var (x, y) = CellXY(TavernMap.Places[0].Cells[0]);
        Put(me, x, y);
        Assert.True(h.Act(0, "drink", new { }).Ok, h.Reply.Message);            // E
        h.Tick(Tavern.DrinkTicks);
        Put(me, 7, 6);
        Assert.True(h.Act(0, "sit", new { }).Ok, h.Reply.Message);              // F
        Assert.True(h.Act(0, "sit", new { }).Ok, h.Reply.Message);              // F ще раз — встати
        // сміття — відмова, стан той самий
        h.Tick(TavernCore.RiseTicks);
        var before = Views.Text(h.View(0));
        foreach (var (action, payload) in new (string, object?)[] { ("move", new { dir = "up" }), ("move", new { d = 1 }), ("jump", null), ("punch", "x"), ("punch", new { dir = 9 }) })
            Assert.False(h.Act(0, action, payload).Ok);
        Assert.Equal(before, Views.Text(h.View(0)));
    }

    [Fact]
    public void The_module_sends_only_actions_and_payloads_the_server_reads()
    {
        var js = File.ReadAllText(Path.Combine(FindRoot(), "web", "games", "tavern.js"));
        var sent = Regex.Matches(js, @"ctx\.(?:act|input)\('(\w+)'").Select(m => m.Groups[1].Value).Distinct().Order().ToArray();
        Assert.Equal(["drink", "move", "punch", "sit"], sent);
        Assert.Contains("ctx.input('move', { dir: d })", js);
        Assert.Contains("ctx.input('move', { dir: -1 })", js);
        Assert.Contains("ctx.act('punch', dir == null ? {} : { dir })", js);
        Assert.Contains("ctx.act('drink', {})", js);
        Assert.Contains("ctx.act('sit', {})", js);
    }

    [Fact]
    public void Module_constants_match_the_server_rules()
    {
        var js = File.ReadAllText(Path.Combine(FindRoot(), "web", "games", "tavern.js"));
        int Const(string name)
        {
            var m = Regex.Match(js, @"\b" + name + @"\s*=\s*(\d+)");
            Assert.True(m.Success, name);
            return int.Parse(m.Groups[1].Value);
        }
        Assert.Equal(Tavern.TickMs, Const("TICK_MS"));
        Assert.Equal(TavernMap.Cell, Const("CELL"));
        Assert.Equal(TavernMap.WorldW, Const("WW"));
        Assert.Equal(TavernMap.WorldH, Const("WH"));
        Assert.Equal(TavernCore.PunchReach, Const("REACH"));
        Assert.Equal(TavernCore.PunchCos2Milli, Const("CONE"));
        Assert.Equal(TavernCore.PunchClose, Const("CLOSE"));
        Assert.Equal(TavernCore.PunchCoolTicks * Tavern.TickMs, Const("PUNCH_COOL_MS"));
        Assert.Equal(TavernCore.DrinkCoolTicks * Tavern.TickMs, Const("DRINK_COOL_MS"));
        Assert.Equal(TavernCore.DrinkTicks * Tavern.TickMs, Const("DRINK_MS"));
        Assert.Equal(TavernCore.WindTicks * Tavern.TickMs, Const("WIND_MS"));
        Assert.Equal(Tavern.Hearts, Const("HEARTS"));
        Assert.Equal(Tavern.MugsToWin, Const("MUGS"));
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
        for (var t = 0; t < 700; t++)
        {
            if (t % 11 == 0) h.Input(t % 3, "move", new { dir = t % 5 - 1 });
            if (t % 97 == 0) h.Act(t % 3, "punch", new { });
            if (t % 53 == 0) h.Act((t + 1) % 3, "drink", new { });
            if (t % 71 == 0) h.Act((t + 2) % 3, "sit", new { });
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
        foreach (var key in new[] { "phase", "round", "of", "left", "t", "width", "height", "cell", "n", "map", "places", "looks", "names", "v", "k", "seats", "dead", "me", "reveal", "result", "turn" })
            Assert.True(Views.Has(v, key), key);
        var n = v.GetProperty("n").GetInt32();
        Assert.Equal(30, v.GetProperty("width").GetInt32());
        Assert.Equal(20, v.GetProperty("map").GetArrayLength());
        Assert.Equal(4 * n, v.GetProperty("looks").GetArrayLength());
        Assert.Equal(n, v.GetProperty("names").GetArrayLength());
        Assert.Equal(4 * n, v.GetProperty("v").GetArrayLength());
        var place = v.GetProperty("places")[3];
        Assert.Equal("Сидр", place.GetProperty("name").GetString());
        Assert.Equal("сидру", place.GetProperty("what").GetString());
        foreach (var key in new[] { "i", "emoji", "x", "y", "w", "face", "cells", "fx", "fy" }) Assert.True(Views.Has(place, key), key);
        var me = v.GetProperty("me");
        foreach (var key in new[] { "id", "hearts", "places", "mugs", "cool", "drinkCool", "drink", "sit", "alive" }) Assert.True(Views.Has(me, key), key);
        var seat = v.GetProperty("seats")[0];
        foreach (var key in new[] { "seat", "nick", "alive", "out", "mugs", "total" }) Assert.True(Views.Has(seat, key), key);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("turn").ValueKind);
        output.WriteLine($"вид на {n} відвідувачів: {Views.Text(G(h).View(0)).Length} Б");
    }

    [Fact]
    public void Catalog_lists_tavern_as_live_by_host_hidden_tick_forty_with_css()
    {
        var info = new Tavern().Info;
        Assert.Equal("tavern", info.Id);
        Assert.Equal("Корчма", info.Title);
        Assert.Equal("корчму", info.Accusative);
        Assert.Equal(GameGroup.Live, info.Group);
        Assert.Equal((1, 8), (info.MinPlayers, info.MaxPlayers));   // сам — лише з «🤖 + бот» (TavernBotTests)
        Assert.Equal(40, info.TickMs);
        Assert.Equal(StartMode.ByHost, info.Start);
        Assert.True(info.Hidden);
        Assert.False(info.Rated);
        Assert.Equal(ScoreOrder.HigherIsBetter, info.Score);
        Assert.Equal(["rounds", "crowd", "botlvl"], info.Options!.Select(o => o.Key));
        Assert.Equal("5", info.Options![0].Default);
        var root = FindRoot();
        Assert.True(File.Exists(Path.Combine(root, "web", "games", "tavern.js")));
        Assert.True(File.Exists(Path.Combine(root, "web", "games", "tavern.css")));
        Assert.Equal("фіолетовий", new Tavern().SeatName(6));
    }

    [Fact]
    public void Achievements_brawler_and_quiet_guest_are_requested_exactly_when_earned()
    {
        Assert.NotNull(AchievementCatalog.Get("tavern-ko"));
        Assert.NotNull(AchievementCatalog.Get("tavern-quiet"));
        // Оля вибиває двох; Петро (четвертий) тихо п'є три кухлі — обоє по ачівці
        var h = Table(4, seed: 112);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id, Me(h, 2).Id);
        S(h, 1).Hearts = 1;
        S(h, 2).Hearts = 1;
        foreach (var victim in new[] { 1, 2 })
        {
            Face(h, 0, Me(h, victim));
            Assert.True(h.Act(0, "punch", new { }).Ok, h.Reply.Message);
            h.Tick(TavernCore.WindTicks + TavernCore.PunchCoolTicks);
        }
        Assert.Equal(Tavern.PhaseGo, G(h).Phase);
        foreach (var place in new[] { 1, 3 })
        {
            DrinkAt(h, 3, place);
            h.Tick(TavernCore.DrinkCoolTicks);
        }
        DrinkAt(h, 3, 4);
        Assert.Equal(Tavern.PhaseReveal, G(h).Phase);
        var awards = h.Awards.Select(a => (a.Nick, a.Reason, a.Shards)).ToList();
        Assert.Contains(("Оля", "ach:tavern-ko", 0), awards);
        Assert.Contains(("Іван", "ach:tavern-quiet", 0), awards);
        Assert.Equal(2, awards.Count);

        // хто хоч раз махнув кулаком — не «тихий», навіть у повітря
        var g = Table(2, seed: 114);
        Go(g);
        Park(g, Me(g, 0).Id, Me(g, 1).Id);
        Put(Me(g, 0), 10, 9);
        g.Act(0, "punch", new { });
        g.Tick(TavernCore.WindTicks + TavernCore.PunchCoolTicks);
        foreach (var place in new[] { 0, 2 })
        {
            DrinkAt(g, 0, place);
            g.Tick(TavernCore.DrinkCoolTicks);
        }
        DrinkAt(g, 0, 4);
        Assert.Equal(Tavern.PhaseReveal, G(g).Phase);
        Assert.Empty(g.Awards);
    }

    [Fact]
    public void Players_start_apart_inside_and_nobody_starts_on_a_step()
    {
        var sitting = 0;
        for (var seed = 1; seed <= 30; seed++)
        {
            var core = new TavernCore(new Random(seed));
            core.Deal([0, 1, 2, 3, 4, 5, 6, 7], 40);
            Assert.All(core.V, v => Assert.Equal(-1, TavernCore.PlaceAt(v)));
            Assert.All(core.V, v => Assert.True(v.Y < 16 * 32, "на старті всі в залі, не надворі"));
            var players = core.V.Where(v => v.Owner >= 0).ToArray();
            Assert.All(players, p => Assert.False(p.Sit));
            for (var i = 0; i < players.Length; i++)
                for (var j = i + 1; j < players.Length; j++)
                {
                    int dx = Math.Abs(players[i].X / 32 - players[j].X / 32), dy = Math.Abs(players[i].Y / 32 - players[j].Y / 32);
                    Assert.True(Math.Max(dx, dy) >= 3, $"сід {seed}");
                }
            Assert.Equal(48, core.V.Select(v => v.Name).Distinct().Count());
            sitting += core.V.Count(v => v.Sit);
        }
        Assert.True(sitting > 30, $"на старті сиділо {sitting} ботів за 30 роздач");     // хтось уже сидить за столом
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
            var h = Table(8, seed: 120 + attempt, options: new { crowd = "big", rounds = "7" });
            var rng = new Random(attempt);
            var sw = Stopwatch.StartNew();
            for (var t = 0; t < 3000 && h.Room.Status == RoomStatus.Playing; t++)
            {
                if (t % 10 == 0)
                    for (var s = 0; s < 8; s++) h.Input(s, "move", new { dir = rng.Next(-1, 4) });
                if (t % 100 == 50)
                    for (var s = 0; s < 8; s++) h.Act(s, s % 2 == 0 ? "punch" : "drink", new { });
                h.Tick();
            }
            best = Math.Min(best, sw.ElapsedMilliseconds);
        }
        var pure = PureTickMicros();
        output.WriteLine($"3000 тиків через кімнату: {best} мс; чистий Tick(): {pure:F1} мкс");
        Assert.True(best < 1000, $"{best} мс");
        Assert.True(pure < 250, $"{pure} мкс на тик");
    }

    /// <summary>Середній Tick() + Frame() на 48 відвідувачах без кімнати й розсилки, найкращий із трьох заходів.</summary>
    static double PureTickMicros()
    {
        var bestUs = double.MaxValue;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var h = Table(8, seed: 130 + attempt, options: new { crowd = "big", rounds = "7" });
            var game = G(h);
            Go(h);
            var rng = new Random(attempt);
            for (var i = 0; i < 200; i++) game.Tick();                     // прогрів JIT
            var sw = Stopwatch.StartNew();
            var n = 0;
            for (var t = 0; t < 3000 && game.Phase != Tavern.PhaseOver; t++)
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
    public void A_frame_with_forty_eight_guests_serialises_under_1000_bytes()
    {
        var h = Table(8, seed: 140, options: new { crowd = "big" });
        Go(h);
        Assert.Equal(48, Core(h).N);
        var max = 0;
        for (var t = 0; t < 600; t++)
        {
            h.Tick();
            max = Math.Max(max, FrameText(h).Length);
        }
        output.WriteLine($"кадр на 48 відвідувачів: до {max} Б");
        Assert.True(max < 1000, $"{max} Б");
    }
}
