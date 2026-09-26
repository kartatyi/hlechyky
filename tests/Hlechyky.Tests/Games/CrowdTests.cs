using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Юрма: мапу, крок і мозок ботів перевіряємо на голому <see cref="CrowdCore"/> (там селянина можна поставити рівно
/// туди, куди треба), а раунди, очки, дії й приховане — через справжню кімнату (<see cref="RoomHarness"/>).
/// Клас у серійній колекції через перф-тест.
/// </summary>
[Collection(SerialPerf.Name)]
public class CrowdTests(ITestOutputHelper output)
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марта", "Юрко", "Соня", "Богдан"];

    // ---------- підмостки ----------

    static RoomHarness Table(int players = 2, int seed = 42, object? options = null)
    {
        var h = new RoomHarness("crowd", options, seed: seed);
        foreach (var nick in Nicks.Take(players)) h.Join(nick);
        h.Start();
        return h;
    }

    static Crowd G(RoomHarness h) => (Crowd)h.Room.Game;
    static CrowdCore Core(RoomHarness h) => G(h).CoreForTests;
    static CrowdSeat S(RoomHarness h, int seat) => G(h).SeatForTests(seat);
    static CrowdVillager Me(RoomHarness h, int seat) => Core(h).V[S(h, seat).Me];

    static void Go(RoomHarness h)
    {
        for (var i = 0; i < 200 && G(h).Phase != Crowd.PhaseGo; i++) h.Tick();
        Assert.Equal(Crowd.PhaseGo, G(h).Phase);
    }

    /// <summary>Поставити селянина в центр клітинки (x, y) + зсув; стоїть, нікуди не хоче.</summary>
    static CrowdVillager Put(CrowdVillager v, int cx, int cy, int ox = 0, int oy = 0, int dir = 0)
    {
        v.X = cx * CrowdMap.Cell + CrowdMap.Cell / 2 + ox;
        v.Y = cy * CrowdMap.Cell + CrowdMap.Cell / 2 + oy;
        v.Dir = dir;
        v.Want = -1;
        v.Moving = false;
        v.Blocked = false;
        return v;
    }

    /// <summary>Ядро з одним гравцем (місце 0) і кількома ботами, які стоять і нікуди не хочуть.</summary>
    static CrowdCore Bare(int bots = 3, int seed = 1)
    {
        var core = new CrowdCore(new Random(seed));
        core.Deal([0], bots);
        foreach (var v in core.V)
        {
            CrowdCore.Forget(v);
            v.Stand = 0;
        }
        return core;
    }

    /// <summary>Усі боти, крім переданих, «сплять» — стоять далеко й не заважають конусу.</summary>
    static void Park(RoomHarness h, params int[] keep)
    {
        var i = 0;
        foreach (var v in Core(h).V)
        {
            if (Array.IndexOf(keep, v.Id) >= 0) continue;
            Put(v, 1 + i % 28, i / 28 % 2 == 0 ? 16 : 18);
            v.Stand = 10_000;
            i++;
        }
    }

    static int Seat0Stall(RoomHarness h, int seat, int n = 0) => S(h, seat).List[n];

    static CrowdVillager AtCounter(RoomHarness h, int seat, int stall)
    {
        var st = CrowdMap.Stalls[stall];
        return Put(Me(h, seat), st.C0 % CrowdMap.W, st.C0 / CrowdMap.W);
    }

    static void Buy(RoomHarness h, int seat, int stall)
    {
        AtCounter(h, seat, stall);
        Assert.True(h.Act(seat, "buy", new { stall }).Ok, h.Reply.Message);
        h.Tick(Crowd.HaggleTicks);
    }

    static string FrameText(RoomHarness h) => Views.Text(G(h).Frame());

    static JsonElement LastFrame(RoomHarness h) => Views.Json(((RoomFrame)h.Outbox.Last(o => o is RoomFrame)).Frame);

    // =============================================================================================
    // Мапа й навігація
    // =============================================================================================

    [Fact]
    public void Map_is_30_by_20_and_every_row_has_thirty_cells()
    {
        Assert.Equal(20, CrowdMap.Rows.Length);
        Assert.All(CrowdMap.Rows, r => Assert.Equal(30, r.Length));
        Assert.All(CrowdMap.Rows[0], c => Assert.Equal('#', c));
        Assert.All(CrowdMap.Rows[19], c => Assert.Equal('#', c));
        Assert.All(CrowdMap.Rows, r => Assert.True(r[0] == '#' && r[29] == '#'));
        Assert.Equal(446, CrowdMap.Walkable.Length);
        Assert.Equal(64, CrowdMap.Names.Distinct().Count());
    }

    [Fact]
    public void Every_walkable_cell_is_reachable_from_every_other()
    {
        var hops = CrowdMap.NextHop;
        foreach (var target in CrowdMap.Walkable)
            foreach (var cell in CrowdMap.Walkable)
            {
                var hop = hops[target * CrowdMap.Cells + cell];
                if (cell == target) Assert.Equal(CrowdMap.NoHop, hop);
                else Assert.InRange(hop, 0, 3);
            }
    }

    [Fact]
    public void Every_stall_has_a_two_cell_counter_of_path_in_front_of_its_body()
    {
        Assert.Equal(12, CrowdMap.Stalls.Length);
        foreach (var s in CrowdMap.Stalls)
        {
            Assert.Equal(s.Letter, CrowdMap.Rows[s.Y][s.X]);
            Assert.Equal(s.Letter, CrowdMap.Rows[s.Y][s.X + 1]);
            var cy = s.C0 / CrowdMap.W;
            Assert.Equal(s.Face == 3 ? s.Y + 1 : s.Y - 1, cy);
            Assert.Equal('=', CrowdMap.Rows[cy][s.C0 % CrowdMap.W]);
            Assert.Equal('=', CrowdMap.Rows[cy][s.C1 % CrowdMap.W]);
            Assert.Equal(s.C0 + 1, s.C1);
            Assert.Equal(s.I, CrowdMap.CounterOf[s.C0]);
            Assert.Equal(s.I, CrowdMap.CounterOf[s.C1]);
        }
        // верхній ряд дивиться вгору на корпус, нижній — униз
        Assert.All(CrowdMap.TopStalls, k => Assert.Equal(3, CrowdMap.Stalls[k].Face));
        Assert.All(CrowdMap.BottomStalls, k => Assert.Equal(1, CrowdMap.Stalls[k].Face));
    }

    [Fact]
    public void Next_hop_table_leads_to_the_target_within_bfs_distance_steps()
    {
        var rng = new Random(7);
        var walk = CrowdMap.Walkable;
        for (var pair = 0; pair < 50; pair++)
        {
            var from = walk[rng.Next(walk.Length)];
            var to = walk[rng.Next(walk.Length)];
            var dist = Bfs(from)[to];
            var cell = from;
            var steps = 0;
            while (cell != to)
            {
                var hop = CrowdMap.NextHop[to * CrowdMap.Cells + cell];
                Assert.InRange(hop, 0, 3);
                cell = (cell / CrowdMap.W + CrowdCore.DY[hop]) * CrowdMap.W + cell % CrowdMap.W + CrowdCore.DX[hop];
                Assert.True(CrowdMap.Pass[cell]);
                steps++;
                Assert.True(steps <= dist, "шлях довший за BFS");
            }
            Assert.Equal(dist, steps);
        }
    }

    static int[] Bfs(int from)
    {
        var d = Enumerable.Repeat(-1, CrowdMap.Cells).ToArray();
        var q = new Queue<int>();
        d[from] = 0;
        q.Enqueue(from);
        while (q.Count > 0)
        {
            var u = q.Dequeue();
            for (var k = 0; k < 4; k++)
            {
                int x = u % CrowdMap.W + CrowdCore.DX[k], y = u / CrowdMap.W + CrowdCore.DY[k];
                var n = y * CrowdMap.W + x;
                if (x < 0 || y < 0 || x >= CrowdMap.W || y >= CrowdMap.H || !CrowdMap.Pass[n] || d[n] >= 0) continue;
                d[n] = d[u] + 1;
                q.Enqueue(n);
            }
        }
        return d;
    }

    [Fact]
    public void Stall_anchor_is_within_buy_range_of_both_counter_cells()
    {
        (int Fx, int Fy)[] spec = [(160, 80), (352, 80), (544, 80), (736, 80), (832, 208), (704, 400), (800, 464), (608, 464), (416, 464), (224, 464), (192, 368), (192, 208)];
        foreach (var s in CrowdMap.Stalls)
        {
            Assert.Equal(spec[s.I], (s.Fx, s.Fy));
            foreach (var c in new[] { s.C0, s.C1 })
                for (var ox = -6; ox <= 6; ox += 6)
                    for (var oy = -6; oy <= 6; oy += 6)
                    {
                        long dx = CrowdMap.CenterX(c) + ox - s.Fx, dy = CrowdMap.CenterY(c) + oy - s.Fy;
                        Assert.True(dx * dx + dy * dy <= CrowdCore.BuyRange * CrowdCore.BuyRange);
                    }
        }
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
        Put(player, 3, 3, 2, -3);
        Put(bot, 3, 3, 2, -3);
        var script = new[] { 0, 0, 1, 0, 3, 2, 1, 1, 0, -1 };
        var a = new List<(int, int, int, bool)>();
        var b = new List<(int, int, int, bool)>();
        for (var t = 0; t < 100; t++)
        {
            player.Want = bot.Want = script[t / 10];
            CrowdCore.Step(player);
            CrowdCore.Step(bot);
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
        for (var t = 0; t < 500; t++)
        {
            if (t % 7 == 0)
                for (var s = 0; s < 4; s++) h.Input(s, "move", new { dir = rng.Next(-1, 4) });
            h.Tick();
            if (G(h).Phase is not (Crowd.PhaseStart or Crowd.PhaseGo)) break;
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
    public void A_villager_never_overlaps_an_obstacle_cell_with_its_box()
    {
        var h = Table(8, seed: 11, options: new { crowd = "big" });
        var rng = new Random(9);
        for (var t = 0; t < 3000 && G(h).Phase != Crowd.PhaseOver; t++)
        {
            if (t % 5 == 0)
                for (var s = 0; s < 8; s++) h.Input(s, "move", new { dir = rng.Next(-1, 4) });
            h.Tick();
            foreach (var v in Core(h).V) Assert.True(CrowdMap.BoxFits(v.X, v.Y), $"id {v.Id} у ({v.X},{v.Y})");
        }
    }

    [Fact]
    public void Facing_turns_even_when_the_step_is_blocked()
    {
        var core = Bare(0);
        var p = Put(core.V[0], 2, 1, ox: -8, oy: -8, dir: 0);  // у кутку клітинки: згори паркан, ліворуч дерево
        p.Want = 3;
        CrowdCore.Step(p);
        Assert.Equal(3, p.Dir);
        Assert.False(p.Moving);
        Assert.True(p.Blocked);
        Assert.Equal(40, p.Y);
        Assert.Equal(0, p.State);
        p.Want = 1;
        CrowdCore.Step(p);
        Assert.Equal(43, p.Y);                         // униз — вільно
        p.Want = 2;                                    // ліворуч — дерево в (1,1): лише повернувся
        CrowdCore.Step(p);
        Assert.Equal((2, 72), (p.Dir, p.X));
        Assert.True(p.Blocked);
    }

    [Fact]
    public void Haggling_and_fallen_villagers_do_not_move_whatever_they_want()
    {
        var core = Bare(2);
        var p = core.V.Single(v => v.Owner == 0);
        var bot = core.V.First(v => v.Owner < 0);
        Put(p, 10, 3);
        Put(bot, 12, 3);
        p.Haggle = 5;
        p.Want = 0;
        bot.Fallen = 5;
        bot.Want = 0;
        for (var i = 0; i < 5; i++)
        {
            CrowdCore.Step(p);
            CrowdCore.Step(bot);
            Assert.False(p.Moving);
            Assert.False(bot.Moving);
        }
        Assert.Equal(10 * 32 + 16, p.X);
        Assert.Equal(12 * 32 + 16, bot.X);
        Assert.Equal(2, bot.State);
        p.Haggle = 0;
        CrowdCore.Step(p);
        Assert.Equal(10 * 32 + 19, p.X);
    }

    // =============================================================================================
    // Боти
    // =============================================================================================

    [Fact]
    public void Bots_stand_between_twelve_and_seventy_five_ticks_and_face_the_stall_at_a_counter()
    {
        var core = new CrowdCore(new Random(21));
        core.Deal([], 40);
        var before = new int[core.N];
        var lens = new List<int>();
        int counter = 0, facing = 0;
        for (var t = 0; t < 3000; t++)
        {
            for (var i = 0; i < core.N; i++) before[i] = core.V[i].Stand;
            core.ThinkAll();
            core.StepAll();
            foreach (var v in core.V)
            {
                if (before[v.Id] != 0 || v.Stand == 0) continue;
                lens.Add(v.Stand);                             // щойно прийшов: стоятиме рівно стільки
                if (CrowdMap.CounterOf[CrowdMap.CellOf(v.X, v.Y)] is var k and >= 0)
                {
                    counter++;
                    if (v.Dir == CrowdMap.Stalls[k].Face) facing++;
                }
            }
        }
        Assert.True(lens.Count > 300);
        Assert.All(lens, s => Assert.InRange(s, CrowdCore.StandMin, CrowdCore.StandMax));
        Assert.True(lens.Min() <= 15 && lens.Max() >= 72, "стояння не розкидане на весь проміжок");
        Assert.True(counter > 100);
        Assert.True(facing * 10 >= counter * 9, $"обличчям до лотка {facing} з {counter}");
    }

    [Fact]
    public void A_bot_that_is_blocked_picks_a_new_target_next_tick()
    {
        var core = Bare(1);
        var bot = core.V.Single(v => v.Owner < 0);
        Put(bot, 2, 1, oy: -8);
        bot.Target = CrowdMap.Walkable[^1];
        bot.Want = 3;
        CrowdCore.Step(bot);
        Assert.True(bot.Blocked);
        core.Think(bot);
        Assert.Equal(-1, bot.Target);
        Assert.Equal(-1, bot.Want);
        core.Think(bot);
        Assert.True(bot.Target >= 0);
    }

    [Fact]
    public void Bots_reach_their_targets_and_never_get_stuck_in_corridors()
    {
        var core = new CrowdCore(new Random(4));
        core.Deal([], 48);
        var arrivals = 0;
        var blocked = 0;
        var stand = new int[core.N];
        for (var t = 0; t < 3000; t++)
        {
            for (var i = 0; i < core.N; i++) stand[i] = core.V[i].Stand;
            core.ThinkAll();
            core.StepAll();
            foreach (var v in core.V)
            {
                if (v.Blocked && v.Target >= 0) blocked++;          // уперся, ідучи до цілі, — отже, застряг би
                if (stand[v.Id] == 0 && v.Stand > 0 && v.Wander == 0) arrivals++;
                Assert.True(CrowdMap.BoxFits(v.X, v.Y));
            }
        }
        Assert.Equal(0, blocked);
        Assert.True(arrivals > 300, $"дійшли лише {arrivals} разів");
    }

    [Fact]
    public void Some_bots_wander_off_the_lattice_after_standing()
    {
        var core = new CrowdCore(new Random(8));
        core.Deal([], 40);
        var offLattice = 0;
        var wandered = 0;
        var lastDir = core.V.Select(v => v.Dir).ToArray();
        for (var t = 0; t < 3000; t++)
        {
            core.ThinkAll();
            core.StepAll();
            foreach (var v in core.V)
            {
                if (v.Wander > 0) wandered++;
                if (v.Moving && v.Dir != lastDir[v.Id])
                {
                    // поворот на ходу: де саме в клітинці — «решітка» була б рівно в центрі
                    int ox = v.X % 32 - 16, oy = v.Y % 32 - 16;
                    if (ox != 0 && oy != 0) offLattice++;
                }
                lastDir[v.Id] = v.Dir;
            }
        }
        Assert.True(wandered > 0);
        Assert.True(offLattice > 100, $"поворотів поза центрами клітинок лише {offLattice}");
    }

    [Fact]
    public void Fallen_bot_gets_up_after_seventy_five_ticks_and_walks_again()
    {
        var h = Table(2, seed: 5);
        Go(h);
        var me = Me(h, 0);
        var bot = Core(h).V.First(v => v.Owner < 0);
        Park(h, me.Id, bot.Id);
        Put(me, 10, 3, dir: 0);
        Put(bot, 12, 3);
        Assert.True(h.Act(0, "shoot", new { }).Ok, h.Reply.Message);
        Assert.Equal(2, bot.State);
        h.Tick(CrowdCore.FallTicks - 1);
        Assert.Equal(2, bot.State);
        h.Tick();
        Assert.Equal(0, bot.Fallen);
        var walked = false;
        for (var i = 0; i < 150 && !walked; i++)
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
    public void Match_starts_with_a_three_second_look_around_where_shots_and_buys_are_refused()
    {
        var h = Table();
        Assert.Equal(Crowd.PhaseStart, G(h).Phase);
        Assert.Equal(Crowd.StartTicks, G(h).Left);
        Assert.False(h.Act(0, "shoot", new { }).Ok);
        Assert.Equal("Зачекай, ярмарок ще не відкрився", h.Reply.Message);
        Assert.False(h.Act(0, "buy", new { }).Ok);
        Assert.Equal("Зачекай, ярмарок ще не відкрився", h.Reply.Message);
        h.Tick(Crowd.StartTicks - 1);
        Assert.Equal(Crowd.PhaseStart, G(h).Phase);
        h.Tick();
        Assert.Equal(Crowd.PhaseGo, G(h).Phase);
        Assert.Equal(Crowd.RoundTicks, G(h).Left);
    }

    [Fact]
    public void Everyone_may_walk_during_the_look_around()
    {
        var h = Table(seed: 2);
        var me = Me(h, 0);
        Put(me, 10, 3);
        var x = me.X;
        h.Input(0, "move", new { dir = 0 });
        h.Tick(10);
        Assert.Equal(x + 30, me.X);
        Assert.Contains(Core(h).V, v => v.Owner < 0 && v.Moving);
    }

    [Fact]
    public void Round_lasts_ninety_seconds_then_the_most_shopping_alive_player_wins()
    {
        var h = Table(seed: 4);
        Go(h);
        Buy(h, 1, Seat0Stall(h, 1));
        Assert.Equal(1, S(h, 1).Bought);
        h.Tick(Crowd.RoundTicks);
        Assert.Equal(Crowd.PhaseReveal, G(h).Phase);
        var reveal = h.View(null).GetProperty("reveal");
        Assert.Equal([1], reveal.GetProperty("winners").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal("time", reveal.GetProperty("why").GetString());
        Assert.Equal(Crowd.PtBuy + Crowd.PtRound, S(h, 1).Total);
    }

    [Fact]
    public void Timeout_with_equal_shopping_gives_a_round_without_a_winner()
    {
        var h = Table(seed: 4);
        Go(h);
        h.Tick(Crowd.RoundTicks);
        Assert.Equal(Crowd.PhaseReveal, G(h).Phase);
        var reveal = h.View(null).GetProperty("reveal");
        Assert.Empty(reveal.GetProperty("winners").EnumerateArray());
        Assert.Equal("none", reveal.GetProperty("why").GetString());
        Assert.Equal(0, S(h, 0).Total);
        Assert.Equal(0, S(h, 1).Total);
    }

    [Fact]
    public void Completing_the_list_ends_the_round_at_once_with_plus_three()
    {
        var h = Table(seed: 6);
        Go(h);
        var list = S(h, 0).List.ToArray();
        for (var i = 0; i < 3; i++)
        {
            Buy(h, 0, list[i]);
            h.Tick(Crowd.BuyCoolTicks);
        }
        Assert.Equal(Crowd.PhaseGo, G(h).Phase);
        Buy(h, 0, list[3]);
        Assert.Equal(Crowd.PhaseReveal, G(h).Phase);
        Assert.Equal(4 * Crowd.PtBuy + Crowd.PtRound, S(h, 0).Total);
        var reveal = h.View(1).GetProperty("reveal");
        Assert.Equal("list", reveal.GetProperty("why").GetString());
        Assert.Equal([0], reveal.GetProperty("winners").EnumerateArray().Select(e => e.GetInt32()));
    }

    [Fact]
    public void Two_players_completing_in_the_same_tick_both_win_the_round()
    {
        var h = Table(3, seed: 6);
        Go(h);
        foreach (var seat in new[] { 0, 1 })
            foreach (var k in S(h, seat).List.Take(3))
            {
                Buy(h, seat, k);
                h.Tick(Crowd.BuyCoolTicks);
            }
        AtCounter(h, 0, S(h, 0).List[3]);
        AtCounter(h, 1, S(h, 1).List[3]);
        Assert.True(h.Act(0, "buy", new { }).Ok, h.Reply.Message);
        Assert.True(h.Act(1, "buy", new { }).Ok, h.Reply.Message);
        h.Tick(Crowd.HaggleTicks);
        Assert.Equal(Crowd.PhaseReveal, G(h).Phase);
        var winners = h.View(null).GetProperty("reveal").GetProperty("winners").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal([0, 1], winners);
        Assert.Equal(7, S(h, 0).Total);
        Assert.Equal(7, S(h, 1).Total);
    }

    [Fact]
    public void Last_player_standing_wins_the_round()
    {
        var h = Table(3, seed: 8);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id, Me(h, 2).Id);
        Put(Me(h, 0), 10, 3, dir: 0);
        Put(Me(h, 1), 12, 3);
        Put(Me(h, 2), 20, 16);
        Assert.True(h.Act(0, "shoot", new { }).Ok, h.Reply.Message);
        h.Tick();
        Assert.Equal(Crowd.PhaseGo, G(h).Phase);
        Put(Me(h, 2), 13, 3);
        h.Tick(Crowd.ShotCoolTicks);
        Put(Me(h, 0), 10, 3, dir: 0);
        Put(Me(h, 2), 12, 3);
        Assert.True(h.Act(0, "shoot", new { id = Me(h, 2).Id }).Ok, h.Reply.Message);
        h.Tick();
        Assert.Equal(Crowd.PhaseReveal, G(h).Phase);
        var reveal = h.View(null).GetProperty("reveal");
        Assert.Equal("last", reveal.GetProperty("why").GetString());
        Assert.Equal([0], reveal.GetProperty("winners").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(2 * Crowd.PtKill + Crowd.PtRound, S(h, 0).Total);
    }

    [Fact]
    public void Reveal_lasts_six_seconds_freezes_everyone_and_then_the_next_round_starts_fresh()
    {
        var h = Table(seed: 10);
        Go(h);
        Buy(h, 0, Seat0Stall(h, 0));
        var oldIds = (S(h, 0).Me, S(h, 1).Me);
        var oldList = S(h, 0).List.ToArray();
        while (G(h).Phase == Crowd.PhaseGo) h.Tick();
        Assert.Equal(Crowd.PhaseReveal, G(h).Phase);
        Assert.Equal(Crowd.RevealTicks, G(h).Left);
        h.Input(0, "move", new { dir = 0 });
        var frozen = Core(h).V.Select(v => (v.X, v.Y)).ToArray();
        h.Tick(Crowd.RevealTicks - 1);
        Assert.Equal(frozen, Core(h).V.Select(v => (v.X, v.Y)).ToArray());
        Assert.Equal(Crowd.PhaseReveal, G(h).Phase);
        h.Tick();
        Assert.Equal(Crowd.PhaseStart, G(h).Phase);
        Assert.Equal(2, G(h).RoundNo);
        Assert.All(new[] { 0, 1 }, s =>
        {
            Assert.True(S(h, s).Alive);
            Assert.Equal(Crowd.Stones, S(h, s).Stones);
            Assert.Equal(0, S(h, s).Bought);
            Assert.All(S(h, s).Done, d => Assert.False(d));
        });
        Assert.True((S(h, 0).Me, S(h, 1).Me) != oldIds || !S(h, 0).List.SequenceEqual(oldList));
        Assert.Equal(4, S(h, 0).Total);     // 1 покупка + 3 за раунд — очки партії лишаються
    }

    [Fact]
    public void After_the_last_round_the_match_finishes_with_totals_in_the_journal()
    {
        var h = Table(seed: 12, options: new { rounds = "1" });
        Go(h);
        Buy(h, 0, Seat0Stall(h, 0));
        h.Tick(Crowd.RoundTicks + Crowd.RevealTicks);
        Assert.Equal(Crowd.PhaseOver, G(h).Phase);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var line = h.Outbox.OfType<Journal>().Last().Text;
        Assert.Equal("Юрма: Оля 4 : Петро 0", line);
        Assert.Equal([0], h.Finished.Single().Result.Winners);
        Assert.Contains(h.Scores, s => s.Nick == "Оля" && s.Score == 4);
        Assert.Contains(h.Scores, s => s.Nick == "Петро" && s.Score == 0);
        var result = h.View(null).GetProperty("result");
        Assert.Equal(4, result.GetProperty("totals")[0].GetInt32());
    }

    [Fact]
    public void Equal_totals_make_shared_winners_and_all_zero_makes_a_draw()
    {
        var draw = Table(seed: 14, options: new { rounds = "1" });
        Go(draw);
        draw.Tick(Crowd.RoundTicks + Crowd.RevealTicks);
        Assert.True(draw.Finished.Single().Result.Draw);
        Assert.EndsWith("— нічия", draw.Outbox.OfType<Journal>().Last().Text);

        var h = Table(3, seed: 14, options: new { rounds = "1" });
        Go(h);
        Buy(h, 0, Seat0Stall(h, 0));
        h.Tick(Crowd.BuyCoolTicks);
        Buy(h, 1, Seat0Stall(h, 1));
        h.Tick(Crowd.RoundTicks + Crowd.RevealTicks);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0, 1], h.Finished.Single().Result.Winners.Order());
        Assert.False(h.Finished.Single().Result.Draw);
    }

    [Fact]
    public void Rounds_option_one_and_five_are_honoured_and_junk_falls_back_to_three()
    {
        foreach (var (opt, want) in new[] { ("1", 1), ("5", 5), ("3", 3), ("7", 3), ("абв", 3) })
        {
            var h = Table(seed: 1, options: new { rounds = opt });
            Assert.Equal(want, h.View(null).GetProperty("of").GetInt32());
            var rounds = 0;
            for (var i = 0; i < 20_000 && h.Room.Status == RoomStatus.Playing; i++)
            {
                if (G(h).Phase == Crowd.PhaseStart && G(h).Left == Crowd.StartTicks) rounds++;
                h.Tick();
            }
            Assert.Equal(want, rounds);
        }
    }

    [Fact]
    public void Crowd_option_sets_bot_count_and_auto_follows_the_table()
    {
        Assert.Equal([24, 24, 28, 32, 34, 36, 38, 40], Enumerable.Range(1, 8).Select(Crowd.BotsFor));
        Assert.Equal(2 + 24, Table(2).View(null).GetProperty("n").GetInt32());
        Assert.Equal(5 + 34, Table(5).View(null).GetProperty("n").GetInt32());
        Assert.Equal(2 + 20, Table(2, options: new { crowd = "small" }).View(null).GetProperty("n").GetInt32());
        Assert.Equal(8 + 48, Table(8, options: new { crowd = "big" }).View(null).GetProperty("n").GetInt32());
        Assert.Equal(3 + 28, Table(3, options: new { crowd = "шось" }).View(null).GetProperty("n").GetInt32());
    }

    // =============================================================================================
    // Купівля
    // =============================================================================================

    [Fact]
    public void Buy_needs_standing_on_the_counter_and_says_come_closer_otherwise()
    {
        var h = Table(seed: 16);
        Go(h);
        var st = CrowdMap.Stalls[2];
        var me = Me(h, 0);
        Put(me, 10, 8);                       // посеред трави, далеко від усього
        Assert.False(h.Act(0, "buy", new { }).Ok);
        Assert.Equal("Підійди до лотка ближче", h.Reply.Message);
        // трава одразу збоку від прилавка — ближче за 40 до точки прилавка, але боти тут не торгуються, тож і гравцеві не можна
        me.X = st.Fx + 36;
        me.Y = st.Fy;
        Assert.True(CrowdCore.Dist2(me, st.Fx, st.Fy) <= CrowdCore.BuyRange * CrowdCore.BuyRange);
        Assert.False(h.Act(0, "buy", new { }).Ok);
        Assert.Equal("Підійди до лотка ближче", h.Reply.Message);
        // на прилавку: край дальньої клітинки — теж можна; чужий номер лотка — ні
        me.X = st.Fx + 31;
        Assert.Equal(2, CrowdCore.CounterAt(me));
        Assert.False(h.Act(0, "buy", new { stall = 0 }).Ok);
        Assert.Equal("Підійди до лотка ближче", h.Reply.Message);
        Assert.Equal(0, me.Haggle);
        Assert.True(h.Act(0, "buy", new { stall = 2 }).Ok, h.Reply.Message);
        Assert.Equal(Crowd.HaggleTicks, me.Haggle);
        // обидві клітинки прилавка кожного лотка — у межах BuyRange від його точки
        foreach (var s in CrowdMap.Stalls)
            foreach (var c in new[] { s.C0, s.C1 })
                for (var dx = -8; dx <= 7; dx++)
                    for (var dy = -8; dy <= 7; dy++)
                    {
                        long x = CrowdMap.CenterX(c) + dx - s.Fx, y = CrowdMap.CenterY(c) + dy - s.Fy;
                        Assert.True(x * x + y * y <= CrowdCore.BuyRange * CrowdCore.BuyRange);
                    }
    }

    [Fact]
    public void Buy_takes_twenty_five_ticks_of_haggling_then_flashes_the_stall_for_everyone()
    {
        var h = Table(seed: 18);
        Go(h);
        var k = Seat0Stall(h, 0);
        var me = AtCounter(h, 0, k);
        h.Input(0, "move", new { dir = 0 });           // тримає стрілку — під час торгу не рушить
        Assert.True(h.Act(0, "buy", new { }).Ok, h.Reply.Message);
        var at = (me.X, me.Y);
        h.Tick(Crowd.HaggleTicks - 1);
        Assert.Equal(at, (me.X, me.Y));
        Assert.Equal(CrowdMap.Stalls[k].Face, me.Dir);
        Assert.Equal(0, S(h, 0).Bought);
        var before = h.Outbox.OfType<RoomFrame>().Count();
        h.Tick();
        Assert.Equal(1, S(h, 0).Bought);
        var flashes = h.Outbox.OfType<RoomFrame>().Skip(before).Select(f => Views.Json(f.Frame).GetProperty("ev"))
            .SelectMany(e => e.EnumerateArray()).Where(e => e[0].GetInt32() == 2).ToList();
        Assert.Single(flashes);
        Assert.Equal(k, flashes[0][1].GetInt32());
        h.Tick();
        Assert.Empty(LastFrame(h).GetProperty("ev").EnumerateArray());
        Assert.Contains(h.Outbox.Skip(h.Outbox.Count - 3), o => o is RoomViews);
    }

    [Fact]
    public void Buying_a_stall_off_the_list_flashes_but_does_not_count()
    {
        var h = Table(seed: 20);
        Go(h);
        var off = Enumerable.Range(0, 12).First(k => Array.IndexOf(S(h, 0).List, k) < 0);
        Buy(h, 0, off);
        Assert.Equal(0, S(h, 0).Bought);
        Assert.Equal(0, S(h, 0).Total);
        Assert.Contains(LastFrame(h).GetProperty("ev").EnumerateArray(), e => e[0].GetInt32() == 2 && e[1].GetInt32() == off);
        Assert.Equal(Crowd.BuyCoolTicks, S(h, 0).BuyCool);
    }

    [Fact]
    public void Being_shot_while_haggling_cancels_the_purchase()
    {
        var h = Table(seed: 22);
        Go(h);
        var k = Seat0Stall(h, 1);
        var victim = AtCounter(h, 1, k);
        Assert.True(h.Act(1, "buy", new { }).Ok, h.Reply.Message);
        h.Tick(10);
        var hunter = Me(h, 0);
        hunter.X = victim.X - 60;
        hunter.Y = victim.Y;
        Assert.True(h.Act(0, "shoot", new { id = victim.Id }).Ok, h.Reply.Message);
        h.Tick(30);
        Assert.Equal(0, S(h, 1).Bought);
        Assert.DoesNotContain(h.Outbox.OfType<RoomFrame>(), f => Views.Json(f.Frame).GetProperty("ev").EnumerateArray().Any(e => e[0].GetInt32() == 2));
    }

    [Fact]
    public void Buy_cooldown_is_fifty_ticks_and_explicit_stall_index_is_validated()
    {
        var h = Table(seed: 24);
        Go(h);
        var k = Seat0Stall(h, 0);
        Buy(h, 0, k);
        Assert.False(h.Act(0, "buy", new { }).Ok);
        Assert.Equal("Продавець ще рахує решту", h.Reply.Message);
        h.Tick(Crowd.BuyCoolTicks - 1);
        Assert.False(h.Act(0, "buy", new { }).Ok);
        h.Tick();
        foreach (var bad in new object[] { new { stall = 12 }, new { stall = -1 }, new { stall = "мед" }, new { stall = 1.5 }, "мед" })
        {
            Assert.False(h.Act(0, "buy", bad).Ok);
            Assert.Equal("Такого лотка нема", h.Reply.Message);
        }
        Assert.True(h.Act(0, "buy", new { stall = k }).Ok, h.Reply.Message);
        Assert.False(h.Act(0, "buy", new { }).Ok);
        Assert.Equal("Ти вже торгуєшся", h.Reply.Message);
        Assert.False(h.Act(0, "shoot", new { }).Ok);
        Assert.Equal("Спершу доторгуйся", h.Reply.Message);
    }

    // =============================================================================================
    // Рогатка
    // =============================================================================================

    [Fact]
    public void Shooting_a_bot_knocks_it_down_and_the_frame_shows_who_shot_whom()
    {
        var h = Table(seed: 26);
        Go(h);
        var me = Me(h, 0);
        var bot = Core(h).V.First(v => v.Owner < 0);
        Park(h, me.Id, bot.Id);
        Put(me, 10, 3, dir: 0);
        Put(bot, 13, 3);
        Assert.True(h.Act(0, "shoot", new { }).Ok, h.Reply.Message);
        h.Tick();
        var f = LastFrame(h);
        var shot = f.GetProperty("ev").EnumerateArray().Single(e => e[0].GetInt32() == 1);
        Assert.Equal([1, me.Id, bot.Id, 0, -1], shot.EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(2, f.GetProperty("v")[bot.Id * 4 + 3].GetInt32());
        Assert.Equal(Crowd.Stones - 1, S(h, 0).Stones);
        Assert.Equal(0, S(h, 0).Total);
        Assert.True(S(h, 1).Alive);
    }

    [Fact]
    public void Shooting_a_player_eliminates_him_reveals_his_seat_and_pays_two_points()
    {
        var h = Table(3, seed: 28);
        Go(h);
        var me = Me(h, 0);
        var him = Me(h, 1);
        Park(h, me.Id, him.Id);
        Put(me, 10, 3, dir: 0);
        Put(him, 12, 3);
        Assert.True(h.Act(0, "shoot", new { }).Ok, h.Reply.Message);
        h.Tick();
        var f = LastFrame(h);
        var shot = f.GetProperty("ev").EnumerateArray().Single(e => e[0].GetInt32() == 1);
        Assert.Equal([1, me.Id, him.Id, 1, 1], shot.EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(3, f.GetProperty("v")[him.Id * 4 + 3].GetInt32());
        Assert.False(S(h, 1).Alive);
        Assert.Equal(Crowd.PtKill, S(h, 0).Total);
        var dead = h.View(2).GetProperty("dead").EnumerateArray().Single();
        Assert.Equal(1, dead.GetProperty("seat").GetInt32());
        Assert.Equal(him.Id, dead.GetProperty("id").GetInt32());
        Assert.False(h.Act(1, "shoot", new { }).Ok);
        Assert.Equal("Тебе вже збили — дивись, хто кого", h.Reply.Message);
        Assert.False(h.Act(1, "buy", new { }).Ok);
        Assert.Equal("Тебе вже збили — дивись, хто кого", h.Reply.Message);
    }

    [Fact]
    public void Three_stones_per_round_then_refused_and_one_second_cooldown_between_shots()
    {
        var h = Table(seed: 30);
        Go(h);
        var me = Me(h, 0);
        var bot = Core(h).V.First(v => v.Owner < 0);
        Park(h, me.Id, bot.Id, Me(h, 1).Id);
        for (var i = 0; i < 3; i++)
        {
            Put(me, 10, 3, dir: 0);
            Put(bot, 12, 3);
            bot.Fallen = 0;
            Assert.True(h.Act(0, "shoot", new { id = bot.Id }).Ok, h.Reply.Message);
            bot.Fallen = 0;
            Assert.False(h.Act(0, "shoot", new { id = bot.Id }).Ok);
            Assert.Equal(i < 2 ? "Рогатка ще натягується" : "Камінці скінчились", h.Reply.Message);
            h.Tick(Crowd.ShotCoolTicks - 1);
            if (i < 2)
            {
                Assert.False(h.Act(0, "shoot", new { id = bot.Id }).Ok);
                Assert.Equal("Рогатка ще натягується", h.Reply.Message);
            }
            h.Tick();
        }
        Assert.False(h.Act(0, "shoot", new { id = bot.Id }).Ok);
        Assert.Equal("Камінці скінчились", h.Reply.Message);
        Assert.Equal(0, S(h, 0).Stones);
    }

    [Fact]
    public void Cone_pick_takes_the_nearest_within_thirty_five_degrees_and_160_units()
    {
        var core = Bare(4);
        var p = core.V.Single(v => v.Owner == 0);
        var bots = core.V.Where(v => v.Owner < 0).ToArray();
        Put(p, 10, 8, dir: 0);
        // 1: прямо на 150 — у конусі; 2: на 100 під кутом ~40° — поза ним; 3: прямо на 170 — задалеко; 4: позаду
        bots[0].X = p.X + 150; bots[0].Y = p.Y;
        bots[1].X = p.X + 77; bots[1].Y = p.Y + 64;
        bots[2].X = p.X + 170; bots[2].Y = p.Y;
        bots[3].X = p.X - 20; bots[3].Y = p.Y;
        Assert.Equal(bots[0].Id, core.Cone(p));
        bots[1].Y = p.Y + 50;                   // ~33° — тепер у конусі й ближчий
        Assert.Equal(bots[1].Id, core.Cone(p));
        bots[1].Fallen = 5;                     // лежачий не рахується
        Assert.Equal(bots[0].Id, core.Cone(p));
        bots[0].X = p.X + 161;
        Assert.Equal(-1, core.Cone(p));
        // рівна відстань — менший id
        bots[1].Fallen = 0;
        bots[1].X = p.X + 90; bots[1].Y = p.Y;
        bots[2].X = p.X + 90; bots[2].Y = p.Y;
        Assert.Equal(Math.Min(bots[1].Id, bots[2].Id), core.Cone(p));
        p.Dir = 3;
        Assert.Equal(-1, core.Cone(p));
    }

    [Fact]
    public void Explicit_target_is_accepted_up_to_190_units_and_refused_beyond()
    {
        var h = Table(seed: 32);
        Go(h);
        var me = Me(h, 0);
        var bot = Core(h).V.First(v => v.Owner < 0);
        Park(h, me.Id, bot.Id, Me(h, 1).Id);
        Put(me, 5, 3, dir: 2);                  // дивиться геть — явній цілі напрямок не важить
        bot.X = me.X + 191; bot.Y = me.Y;
        Assert.False(h.Act(0, "shoot", new { id = bot.Id }).Ok);
        Assert.Equal("Далеко — не долетить", h.Reply.Message);
        bot.X = me.X + 190;
        Assert.True(h.Act(0, "shoot", new { id = bot.Id }).Ok, h.Reply.Message);
        Assert.Equal(CrowdCore.FallTicks, bot.Fallen);
    }

    [Fact]
    public void Cannot_shoot_yourself_a_fallen_villager_or_a_dead_one()
    {
        var h = Table(3, seed: 34);
        Go(h);
        var me = Me(h, 0);
        Put(me, 10, 3, dir: 0);
        Assert.False(h.Act(0, "shoot", new { id = me.Id }).Ok);
        Assert.Equal("У себе стріляти не годиться", h.Reply.Message);
        var bot = Core(h).V.First(v => v.Owner < 0);
        Put(bot, 11, 3);
        bot.Fallen = 10;
        Assert.False(h.Act(0, "shoot", new { id = bot.Id }).Ok);
        Assert.Equal("Лежачого не б'ють", h.Reply.Message);
        var him = Me(h, 1);
        Put(him, 12, 3);
        him.Dead = true;
        Assert.False(h.Act(0, "shoot", new { id = him.Id }).Ok);
        Assert.Equal("Лежачого не б'ють", h.Reply.Message);
        foreach (var bad in new object[] { new { id = 999 }, new { id = -1 }, new { id = "Параска" }, "x" })
        {
            Assert.False(h.Act(0, "shoot", bad).Ok);
            Assert.Equal("Такого селянина нема", h.Reply.Message);
        }
        Park(h, me.Id);
        Put(me, 10, 3, dir: 0);
        Assert.False(h.Act(0, "shoot", new { }).Ok);
        Assert.Equal("Перед тобою нікого", h.Reply.Message);
        Assert.Equal(Crowd.Stones, S(h, 0).Stones);
    }

    [Fact]
    public void A_victim_cannot_shoot_back_after_the_first_stone_lands_in_the_same_tick()
    {
        // Постріли застосовуються одразу в Act, тож «двоє збили одне одного за тик» неможливо: хто перший — той і вцілів.
        var h = Table(3, seed: 36);
        Go(h);
        var a = Me(h, 0);
        var b = Me(h, 1);
        Park(h, a.Id, b.Id);
        Put(a, 10, 3, dir: 0);
        Put(b, 12, 3, dir: 2);
        Assert.True(h.Act(0, "shoot", new { }).Ok, h.Reply.Message);
        Assert.False(h.Act(1, "shoot", new { }).Ok);
        Assert.Equal("Тебе вже збили — дивись, хто кого", h.Reply.Message);
        h.Tick();
        Assert.True(S(h, 0).Alive);
        Assert.False(S(h, 1).Alive);
        Assert.Equal(Crowd.PhaseGo, G(h).Phase);     // ще двоє живих
    }

    // =============================================================================================
    // Вихід, F5, «Ще раз»
    // =============================================================================================

    [Fact]
    public void A_leaver_becomes_a_bot_and_the_round_goes_on()
    {
        var h = Table(3, seed: 38);
        Go(h);
        var his = Me(h, 2);
        var n = Core(h).N;
        h.Leave("Ганна");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(-1, his.Owner);
        Assert.Equal(n, Core(h).N);
        var at = (his.X, his.Y);
        h.Tick(100);
        Assert.NotEqual(at, (his.X, his.Y));
        Assert.Equal(4 * n, LastFrame(h).GetProperty("v").GetArrayLength());
        var seat = h.View(null).GetProperty("seats").EnumerateArray().Single(s => s.GetProperty("seat").GetInt32() == 2);
        Assert.True(seat.GetProperty("out").GetBoolean());
        Assert.Equal("Ганна", seat.GetProperty("nick").GetString());
        // наступний раунд: той самий розмір юрми, а гравців — двоє
        h.Tick(Crowd.RoundTicks + Crowd.RevealTicks);
        Assert.Equal(2, G(h).RoundNo);
        Assert.Equal(n, Core(h).N);
        Assert.Equal(2, Core(h).V.Count(v => v.Owner >= 0));
    }

    [Fact]
    public void When_only_one_player_remains_the_match_ends_in_his_favour()
    {
        var h = Table(2, seed: 40);
        Go(h);
        Buy(h, 1, Seat0Stall(h, 1));
        h.Leave("Оля");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Finished.Single().Result.Winners);
        Assert.Contains("Петро лишається на ярмарку", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Contains(h.Scores, s => s.Nick == "Петро" && s.Score == 1);
    }

    [Fact]
    public void Rematch_gives_a_clean_match_with_rotated_seats()
    {
        var h = Table(2, seed: 42, options: new { rounds = "1" });
        Go(h);
        Buy(h, 0, Seat0Stall(h, 0));
        h.Tick(Crowd.RoundTicks + Crowd.RevealTicks);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        h.Rematch("Оля");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("Петро", h.NickOf(0));
        Assert.Equal(Crowd.PhaseStart, G(h).Phase);
        Assert.Equal(1, G(h).RoundNo);
        Assert.All(new[] { 0, 1 }, s => Assert.Equal(0, S(h, s).Total));
        var seats = h.View(null).GetProperty("seats").EnumerateArray().ToList();
        Assert.Equal("Петро", seats[0].GetProperty("nick").GetString());
        Assert.True(h.View(null).GetProperty("reveal").ValueKind == JsonValueKind.Null);
        Assert.True(h.View(null).GetProperty("result").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public void Move_input_from_a_dead_or_reveal_phase_player_is_swallowed_silently()
    {
        var h = Table(3, seed: 44);
        Go(h);
        var him = Me(h, 1);
        Park(h, Me(h, 0).Id, him.Id);
        Put(Me(h, 0), 10, 3, dir: 0);
        Put(him, 12, 3);
        Assert.True(h.Act(0, "shoot", new { }).Ok);
        Assert.True(h.Act(1, "move", new { dir = 0 }).Ok);        // мертвому — «так», але без жодного кроку
        h.Tick(5);
        Assert.Equal(12 * 32 + 16, him.X);
        Assert.False(h.Act(0, "move", new { dir = 7 }).Ok);
        Assert.Equal("Такого напрямку нема", h.Reply.Message);
        h.Tick(Crowd.RoundTicks);
        Assert.Equal(Crowd.PhaseReveal, G(h).Phase);
        var me = Me(h, 0);
        Assert.True(h.Act(0, "move", 2).Ok);
        Assert.Equal(-1, me.Want);
    }

    [Fact]
    public void Match_that_has_not_started_refuses_every_action()
    {
        var h = new RoomHarness("crowd", seed: 1);
        h.Join("Оля");
        h.Join("Петро");
        var view = h.View(0);
        Assert.Equal("lobby", view.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("me").ValueKind);
        Assert.Equal(24 * 4, view.GetProperty("v").GetArrayLength());
        var game = (Crowd)h.Room.Game;
        Assert.False(game.Act(0, "move", Views.Payload(new { dir = 1 })).Ok);
        Assert.Equal("Партія ще не почалась", game.Act(0, "shoot", Views.Payload(new { })).Message);
    }

    // =============================================================================================
    // Приховане
    // =============================================================================================

    [Fact]
    public void Frame_has_exactly_four_numbers_per_villager_and_no_other_keys()
    {
        var h = Table(5, seed: 46);
        Go(h);
        h.Tick();
        var f = LastFrame(h);
        Assert.Equal(["t", "ph", "left", "v", "ev"], f.EnumerateObject().Select(p => p.Name));
        Assert.Equal(4 * Core(h).N, f.GetProperty("v").GetArrayLength());
        Assert.All(f.GetProperty("v").EnumerateArray(), e => Assert.Equal(JsonValueKind.Number, e.ValueKind));
    }

    [Fact]
    public void Frame_json_never_mentions_seats_owners_lists_or_stones()
    {
        var h = Table(4, seed: 48);
        Go(h);
        var me = Me(h, 0);
        Park(h, me.Id);
        Put(me, 10, 3, dir: 0);
        Put(Core(h).V.First(v => v.Owner < 0), 12, 3);
        h.Act(0, "shoot", new { });
        Buy(h, 1, Seat0Stall(h, 1));
        foreach (var f in h.Outbox.OfType<RoomFrame>())
        {
            var text = Views.Text(f.Frame);
            foreach (var word in new[] { "\"seat", "\"me", "\"owner", "\"list", "\"stone", "\"nick", "\"alive" })
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
            var core = new CrowdCore(new Random(seed));
            core.Deal([0, 1], 24);
            var ids = core.V.Where(v => v.Owner >= 0).Select(v => v.Id).ToArray();
            if (ids.Any(id => id >= core.N / 2)) high++;
            if (ids.Any(id => id >= 2)) notFirst++;
        }
        Assert.True(high > 50, $"{high}");
        Assert.True(notFirst > 90, $"{notFirst}");
    }

    [Fact]
    public void Watcher_view_has_no_me_and_other_seats_do_not_see_my_id_before_death()
    {
        var h = Table(3, seed: 50);
        Go(h);
        var watcher = h.View(null);
        Assert.Equal(JsonValueKind.Null, watcher.GetProperty("me").ValueKind);
        Assert.Equal(JsonValueKind.Null, watcher.GetProperty("reveal").ValueKind);
        Assert.Empty(watcher.GetProperty("dead").EnumerateArray());
        var mine = h.View(0).GetProperty("me");
        Assert.Equal(S(h, 0).Me, mine.GetProperty("id").GetInt32());
        Assert.Equal(S(h, 0).List, mine.GetProperty("list").EnumerateArray().Select(e => e.GetInt32()));
        var other = h.View(1);
        Assert.Equal(S(h, 1).Me, other.GetProperty("me").GetProperty("id").GetInt32());
        // Вид іншого місця відрізняється від глядацького лише своїм me
        var a = Strip(other);
        var b = Strip(watcher);
        Assert.Equal(b, a);
        // чужих списків і камінців нема ніде, крім свого me
        Assert.DoesNotContain("\"stones\"", Views.Text(Strip(h.View(0))));
    }

    /// <summary>Вид без <c>me</c> — щоб порівняти два види.</summary>
    static string Strip(JsonElement view)
    {
        var d = view.EnumerateObject().Where(p => p.Name != "me").ToDictionary(p => p.Name, p => p.Value);
        return Views.Text(d);
    }

    [Fact]
    public void Dead_player_view_equals_watcher_view_plus_his_own_me()
    {
        var h = Table(3, seed: 52);
        Go(h);
        var him = Me(h, 1);
        Park(h, Me(h, 0).Id, him.Id);
        Put(Me(h, 0), 10, 3, dir: 0);
        Put(him, 12, 3);
        Assert.True(h.Act(0, "shoot", new { }).Ok);
        h.Tick();
        var dead = h.View(1);
        Assert.False(dead.GetProperty("me").GetProperty("alive").GetBoolean());
        Assert.Equal(Strip(h.View(null)), Strip(dead));
        var text = Views.Text(dead);
        // живих чужих id мертвий не бачить: у виді нема жодного id, крім свого й загиблих
        var ids = dead.GetProperty("dead").EnumerateArray().Select(d => d.GetProperty("id").GetInt32()).ToList();
        Assert.Equal([him.Id], ids);
        Assert.DoesNotContain("\"ids\"", text);
    }

    [Fact]
    public void Reveal_ids_appear_only_in_reveal_and_over_phases()
    {
        var h = Table(2, seed: 54, options: new { rounds = "1" });
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
        Go(h);
        h.Tick(Crowd.RoundTicks - 1);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
        h.Tick();
        var reveal = h.View(null).GetProperty("reveal");
        var ids = reveal.GetProperty("ids").EnumerateArray().Select(e => (e.GetProperty("seat").GetInt32(), e.GetProperty("id").GetInt32())).ToList();
        Assert.Equal([(0, S(h, 0).Me), (1, S(h, 1).Me)], ids);
        h.Tick(Crowd.RevealTicks);
        Assert.Equal(Crowd.PhaseOver, G(h).Phase);
        Assert.NotEqual(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
    }

    [Fact]
    public void Per_tick_displacement_and_state_values_of_players_are_a_subset_of_those_of_bots()
    {
        var h = Table(4, seed: 56);
        var rng = new Random(3);
        var core = Core(h);
        var botSteps = new HashSet<(int, int, int)>();
        var playerSteps = new HashSet<(int, int, int)>();
        var botPos = new HashSet<(int, int)>();
        var playerPos = new HashSet<(int, int)>();
        var was = core.V.Select(v => (v.X, v.Y)).ToArray();
        for (var t = 0; t < 2000 && G(h).Phase is Crowd.PhaseStart or Crowd.PhaseGo; t++)
        {
            if (t % 9 == 0)
                for (var s = 0; s < 4; s++) h.Input(s, "move", new { dir = rng.Next(-1, 4) });
            h.Tick();
            foreach (var v in core.V)
            {
                var step = (v.X - was[v.Id].X, v.Y - was[v.Id].Y, v.State);
                (v.Owner >= 0 ? playerSteps : botSteps).Add(step);
                // остача від клітинки — «квантування» позиції: і в тих, і в тих будь-яка з 32×32
                (v.Owner >= 0 ? playerPos : botPos).Add((v.X % 32, v.Y % 32));
                was[v.Id] = (v.X, v.Y);
            }
        }
        Assert.Subset(botSteps, playerSteps);
        Assert.True(botPos.Count > 200, $"боти стоять лише на {botPos.Count} остачах");
    }

    // =============================================================================================
    // Контракт і детермінізм
    // =============================================================================================

    [Fact]
    public void The_server_accepts_exactly_what_the_module_sends()
    {
        var h = Table(2, seed: 58);
        var me = Me(h, 0);
        Put(me, 10, 3);
        h.Input(0, "move", new { dir = 0 });            // як шле модуль на keydown
        h.Tick();
        Assert.Equal(0, me.Want);
        h.Input(0, "move", 2);                           // голе число теж
        Assert.Equal(2, me.Want);
        h.Input(0, "move", new { dir = -1 });           // keyup
        Assert.Equal(-1, me.Want);
        Go(h);
        Park(h, me.Id);
        var bot = Core(h).V.First(v => v.Owner < 0);
        Put(me, 10, 3, dir: 0);
        Put(bot, 12, 3);
        Assert.True(h.Act(0, "shoot", new { }).Ok, h.Reply.Message);                 // пробіл: конус
        Assert.Equal(CrowdCore.FallTicks, bot.Fallen);
        h.Tick(Crowd.ShotCoolTicks);
        bot.Fallen = 0;
        Assert.True(h.Act(0, "shoot", new { id = bot.Id }).Ok, h.Reply.Message);      // клік по селянину
        var k = Seat0Stall(h, 0);
        AtCounter(h, 0, k);
        Assert.True(h.Act(0, "buy", new { }).Ok, h.Reply.Message);                   // E: найближчий
        h.Tick(Crowd.HaggleTicks + Crowd.BuyCoolTicks);
        Assert.True(h.Act(0, "buy", new { stall = k }).Ok, h.Reply.Message);          // клік по лотку
        // сміття — відмова, стан той самий
        var before = Views.Text(h.View(0));
        foreach (var (action, payload) in new (string, object?)[] { ("move", new { dir = "up" }), ("move", new { d = 1 }), ("jump", null), ("shoot", "x"), ("buy", new { stall = 99 }) })
            Assert.False(h.Act(0, action, payload).Ok);
        Assert.Equal(before, Views.Text(h.View(0)));
    }

    static List<string> Replay(int seed)
    {
        var h = Table(3, seed: seed);
        var frames = new List<string>();
        for (var t = 0; t < 600; t++)
        {
            if (t % 11 == 0) h.Input(t % 3, "move", new { dir = t % 5 - 1 });
            if (t % 97 == 0) h.Act(t % 3, "shoot", new { });
            if (t % 53 == 0) h.Act((t + 1) % 3, "buy", new { });
            h.Tick();
            frames.Add(Views.Text(((RoomFrame)h.Outbox.Last(o => o is RoomFrame)).Frame));
        }
        frames.Add(Views.Text(h.View(0)));
        return frames;
    }

    [Fact]
    public void Same_seed_and_same_inputs_give_byte_identical_frames()
    {
        var a = Replay(60);
        var b = Replay(60);
        Assert.Equal(a, b);
        Assert.NotEqual(a, Replay(61));
    }

    [Fact]
    public void Views_json_matches_the_spec_shape()
    {
        var h = Table(2, seed: 62);
        Go(h);
        var v = h.View(0);
        foreach (var key in new[] { "phase", "round", "of", "left", "t", "width", "height", "cell", "n", "map", "stalls", "looks", "names", "v", "seats", "dead", "me", "reveal", "result", "turn" })
            Assert.True(Views.Has(v, key), key);
        var n = v.GetProperty("n").GetInt32();
        Assert.Equal(30, v.GetProperty("width").GetInt32());
        Assert.Equal(20, v.GetProperty("map").GetArrayLength());
        Assert.Equal(4 * n, v.GetProperty("looks").GetArrayLength());
        Assert.Equal(n, v.GetProperty("names").GetArrayLength());
        Assert.Equal(4 * n, v.GetProperty("v").GetArrayLength());
        var stall = v.GetProperty("stalls")[2];
        Assert.Equal("Мед", stall.GetProperty("name").GetString());
        Assert.Equal("меду", stall.GetProperty("what").GetString());
        foreach (var key in new[] { "i", "emoji", "x", "y", "fx", "fy", "face" }) Assert.True(Views.Has(stall, key), key);
        var me = v.GetProperty("me");
        foreach (var key in new[] { "id", "list", "done", "stones", "cool", "haggle", "alive" }) Assert.True(Views.Has(me, key), key);
        var seat = v.GetProperty("seats")[0];
        foreach (var key in new[] { "seat", "nick", "alive", "out", "bought", "total" }) Assert.True(Views.Has(seat, key), key);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("turn").ValueKind);
        var size = Views.Text(G(h).View(0)).Length;
        output.WriteLine($"вид на {n} селян: {size} Б");
    }

    [Fact]
    public void Catalog_lists_crowd_as_live_by_host_hidden_tick_forty_with_css()
    {
        var info = new Crowd().Info;
        Assert.Equal("crowd", info.Id);
        Assert.Equal("Юрма", info.Title);
        Assert.Equal("юрму", info.Accusative);
        Assert.Equal(GameGroup.Live, info.Group);
        Assert.Equal((2, 8), (info.MinPlayers, info.MaxPlayers));
        Assert.Equal(40, info.TickMs);
        Assert.Equal(StartMode.ByHost, info.Start);
        Assert.True(info.Hidden);
        Assert.False(info.Rated);
        Assert.Equal(ScoreOrder.HigherIsBetter, info.Score);
        Assert.Equal(["rounds", "crowd"], info.Options!.Select(o => o.Key));
        var root = FindRoot();
        Assert.True(File.Exists(Path.Combine(root, "web", "games", "crowd.js")));
        Assert.True(File.Exists(Path.Combine(root, "web", "games", "crowd.css")));
        Assert.Equal("фіолетовий", new Crowd().SeatName(6));
    }

    [Fact]
    public void The_module_sends_only_actions_and_payloads_the_server_reads()
    {
        var js = File.ReadAllText(Path.Combine(FindRoot(), "web", "games", "crowd.js"));
        var sent = System.Text.RegularExpressions.Regex.Matches(js, @"ctx\.(?:act|input)\('(\w+)'")
            .Select(m => m.Groups[1].Value).Distinct().Order().ToArray();
        Assert.Equal(["buy", "move", "shoot"], sent);
        // форми payload — ті самі, що перевіряє The_server_accepts_exactly_what_the_module_sends
        Assert.Contains("ctx.input('move', { dir: d })", js);
        Assert.Contains("ctx.input('move', { dir: -1 })", js);
        Assert.Contains("ctx.act('shoot', id == null ? {} : { id })", js);
        Assert.Contains("ctx.act('buy', stall == null ? {} : { stall })", js);
    }

    [Fact]
    public void Module_constants_match_the_server_rules()
    {
        var js = File.ReadAllText(Path.Combine(FindRoot(), "web", "games", "crowd.js"));
        int Const(string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(js, @"\b" + name + @"\s*=\s*(\d+)");
            Assert.True(m.Success, name);
            return int.Parse(m.Groups[1].Value);
        }
        Assert.Equal(Crowd.TickMs, Const("TICK_MS"));
        Assert.Equal(CrowdMap.Cell, Const("CELL"));
        Assert.Equal(CrowdMap.WorldW, Const("WW"));
        Assert.Equal(CrowdMap.WorldH, Const("WH"));
        Assert.Equal(CrowdCore.ShotRange, Const("SHOT_RANGE"));
        Assert.Equal(CrowdCore.ShotRangeMax, Const("SHOT_MAX"));
        Assert.Equal(CrowdCore.ConeCos2Milli, Const("CONE"));
        Assert.Equal(Crowd.ShotCoolTicks * Crowd.TickMs, Const("SHOT_COOL_MS"));
        Assert.Equal(Crowd.BuyCoolTicks * Crowd.TickMs, Const("BUY_COOL_MS"));
        Assert.Equal(Crowd.HaggleTicks * Crowd.TickMs, Const("HAGGLE_MS"));
    }

    static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "web", "games"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("не знайшов корінь репозиторію");
    }

    [Fact]
    public void Achievements_eye_and_quiet_are_requested_exactly_when_earned()
    {
        Assert.NotNull(AchievementCatalog.Get("crowd-eye"));
        Assert.NotNull(AchievementCatalog.Get("crowd-quiet"));
        // Оля першим камінцем збиває Ганну; Петро тихо скуповується — обоє по ачівці
        var h = Table(3, seed: 64);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 2).Id);
        Put(Me(h, 0), 10, 3, dir: 0);
        Put(Me(h, 2), 12, 3);
        Assert.True(h.Act(0, "shoot", new { }).Ok, h.Reply.Message);
        foreach (var k in S(h, 1).List)
        {
            Buy(h, 1, k);
            if (G(h).Phase == Crowd.PhaseGo) h.Tick(Crowd.BuyCoolTicks);
        }
        Assert.Equal(Crowd.PhaseReveal, G(h).Phase);
        var awards = h.Awards.Select(a => (a.Nick, a.Reason, a.Shards)).ToList();
        Assert.Contains(("Оля", "ach:crowd-eye", 0), awards);
        Assert.Contains(("Петро", "ach:crowd-quiet", 0), awards);
        Assert.Equal(2, awards.Count);

        // промах по боту першим камінцем — ачівки нема; хто стріляв — не «тихий»
        var g = Table(2, seed: 66);
        Go(g);
        var bot = Core(g).V.First(v => v.Owner < 0);
        Park(g, Me(g, 0).Id, bot.Id);
        Put(Me(g, 0), 10, 3, dir: 0);
        Put(bot, 12, 3);
        Assert.True(g.Act(0, "shoot", new { }).Ok, g.Reply.Message);
        foreach (var k in S(g, 0).List)
        {
            Buy(g, 0, k);
            if (G(g).Phase == Crowd.PhaseGo) g.Tick(Crowd.BuyCoolTicks);
        }
        Assert.Equal(Crowd.PhaseReveal, G(g).Phase);
        Assert.Empty(g.Awards);
    }

    [Fact]
    public void Lists_have_one_top_one_bottom_one_side_and_four_distinct_stalls()
    {
        for (var seed = 1; seed <= 30; seed++)
        {
            var h = Table(4, seed: seed);
            for (var s = 0; s < 4; s++)
            {
                var list = S(h, s).List;
                Assert.Equal(4, list.Distinct().Count());
                Assert.Contains(list, k => CrowdMap.TopStalls.Contains(k));
                Assert.Contains(list, k => CrowdMap.BottomStalls.Contains(k));
                Assert.Contains(list, k => CrowdMap.SideStalls.Contains(k));
            }
        }
    }

    [Fact]
    public void Players_start_apart_and_nobody_starts_on_a_counter()
    {
        for (var seed = 1; seed <= 30; seed++)
        {
            var core = new CrowdCore(new Random(seed));
            core.Deal([0, 1, 2, 3, 4, 5, 6, 7], 48);
            Assert.All(core.V, v => Assert.Equal(-1, CrowdMap.CounterOf[CrowdMap.CellOf(v.X, v.Y)]));
            var players = core.V.Where(v => v.Owner >= 0).ToArray();
            for (var i = 0; i < players.Length; i++)
                for (var j = i + 1; j < players.Length; j++)
                {
                    int dx = Math.Abs(players[i].X / 32 - players[j].X / 32), dy = Math.Abs(players[i].Y / 32 - players[j].Y / 32);
                    Assert.True(Math.Max(dx, dy) >= 3, $"сід {seed}");
                }
            Assert.Equal(56, core.V.Select(v => v.Name).Distinct().Count());
        }
    }

    // =============================================================================================
    // Швидкодія
    // =============================================================================================

    [Fact]
    [Trait("Category", "Perf")]
    public void Three_thousand_ticks_with_eight_players_and_forty_eight_bots_fit_in_a_second()
    {
        long best = long.MaxValue;
        var ticks = 0;
        for (var attempt = 0; attempt < 3 && best >= 1000; attempt++)
        {
            var h = Table(8, seed: 70 + attempt, options: new { crowd = "big", rounds = "5" });
            var game = G(h);
            var rng = new Random(attempt);
            var sw = Stopwatch.StartNew();
            ticks = 0;
            for (var t = 0; t < 3000 && h.Room.Status == RoomStatus.Playing; t++)
            {
                if (t % 10 == 0)
                    for (var s = 0; s < 8; s++) h.Input(s, "move", new { dir = rng.Next(-1, 4) });
                if (t % 100 == 50)
                    for (var s = 0; s < 8; s++) h.Act(s, s % 2 == 0 ? "shoot" : "buy", new { });
                h.Tick();
                ticks++;
            }
            best = Math.Min(best, sw.ElapsedMilliseconds);
        }
        // Чистий тик гри без кімнати — окремо: він і є «середній Tick» бюджету хвилі.
        var pure = PureTickMicros();
        output.WriteLine($"3000 тиків через кімнату: {best} мс; чистий Tick(): {pure:F1} мкс");
        Assert.True(best < 1000, $"{best} мс");
        Assert.True(pure < 250, $"{pure} мкс на тик");
    }

    /// <summary>Середній Tick() + Frame() на 56 селянах без кімнати й розсилки, найкращий із трьох заходів.</summary>
    static double PureTickMicros()
    {
        var bestUs = double.MaxValue;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var h = Table(8, seed: 90 + attempt, options: new { crowd = "big", rounds = "5" });
            var game = G(h);
            Go(h);
            var rng = new Random(attempt);
            for (var i = 0; i < 200; i++) game.Tick();                     // прогрів JIT
            var sw = Stopwatch.StartNew();
            var n = 0;
            for (var t = 0; t < 3000 && game.Phase != Crowd.PhaseOver; t++)
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
    public void A_frame_with_fifty_six_villagers_serialises_under_1200_bytes()
    {
        var h = Table(8, seed: 72, options: new { crowd = "big" });
        Go(h);
        var me = Me(h, 0);
        Park(h, me.Id);
        Put(me, 10, 3, dir: 0);
        Put(Core(h).V.First(v => v.Owner < 0 && v.Id != me.Id), 12, 3);
        h.Act(0, "shoot", new { });
        h.Tick();
        Assert.Equal(56, Core(h).N);
        var text = FrameText(h);
        output.WriteLine($"кадр на 56 селян: {text.Length} Б");
        Assert.True(text.Length < 1200, $"{text.Length} Б");
        // і посеред звичайного ходу (усі розбрелись) — теж
        for (var t = 0; t < 300; t++)
        {
            h.Tick();
            Assert.True(FrameText(h).Length < 1200);
        }
    }
}
