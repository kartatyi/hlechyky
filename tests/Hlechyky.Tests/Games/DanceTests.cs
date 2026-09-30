using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Вечорниці: мапу, крок, фігуру й мозок ботів перевіряємо на голому <see cref="DanceCore"/> (там танцюриста можна
/// поставити рівно туди, куди треба, і самим кликати фігури), а раунди, очки, дії й приховане — через справжню кімнату
/// (<see cref="RoomHarness"/>). Клас у серійній колекції через перф-тест.
/// </summary>
[Collection(SerialPerf.Name)]
public class DanceTests(ITestOutputHelper output)
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марта", "Юрко", "Соня", "Богдан"];

    // ---------- підмостки ----------

    static RoomHarness Table(int players = 2, int seed = 42, object? options = null)
    {
        var h = new RoomHarness("dance", options, seed: seed);
        foreach (var nick in Nicks.Take(players)) h.Join(nick);
        h.Start();
        return h;
    }

    static Dance G(RoomHarness h) => (Dance)h.Room.Game;
    static DanceCore Core(RoomHarness h) => G(h).CoreForTests;
    static DanceSeat S(RoomHarness h, int seat) => G(h).SeatForTests(seat);
    static DanceVillager Me(RoomHarness h, int seat) => Core(h).V[S(h, seat).Me];

    static void Go(RoomHarness h)
    {
        for (var i = 0; i < 200 && G(h).Phase != Dance.PhaseGo; i++) h.Tick();
        Assert.Equal(Dance.PhaseGo, G(h).Phase);
    }

    /// <summary>До наступного виклику музик (фігура вже лунає, такт попереду).</summary>
    static void ToCall(RoomHarness h)
    {
        for (var i = 0; i < 100 && G(h).CallFig >= 0; i++) h.Tick();      // спершу дограти поточний
        for (var i = 0; i < 400 && G(h).CallFig < 0; i++) h.Tick();
        Assert.True(G(h).CallFig >= 0, "музики мовчать");
    }

    /// <summary>Тикати, доки поточний тик раунду не стане <paramref name="t"/>.</summary>
    static void TickTo(RoomHarness h, int t)
    {
        Assert.True(G(h).T <= t, $"уже {G(h).T}, а треба {t}");
        while (G(h).T < t) h.Tick();
    }

    /// <summary>Гравець тисне фігуру так, щоб її вперше видно було в кадрі тика «такт + <paramref name="off"/>».</summary>
    static ActResult PressAt(RoomHarness h, int seat, int off, int? fig = null)
    {
        TickTo(h, G(h).Beat + off - 1);
        return h.Act(seat, "fig", new { f = fig ?? G(h).CallFig });
    }

    /// <summary>До кінця вікна такту: суд відбувся.</summary>
    static void ToJudge(RoomHarness h) => TickTo(h, G(h).Beat + DanceCore.Late + 1);

    static DanceVillager Put(DanceVillager v, int cx, int cy, int ox = 0, int oy = 0, int dir = 0)
    {
        v.X = cx * DanceMap.Cell + DanceMap.Cell / 2 + ox;
        v.Y = cy * DanceMap.Cell + DanceMap.Cell / 2 + oy;
        v.Dir = dir;
        v.Want = -1;
        v.Moving = false;
        v.Blocked = false;
        return v;
    }

    /// <summary>У коло: клітинка (15, 10) — поруч із центром кола.</summary>
    static DanceVillager InRing(DanceVillager v, int ox = 0) => Put(v, 15, 10, ox);

    /// <summary>Усі боти, крім переданих, стоять далеко внизу й нікуди не йдуть (фігури танцюють і далі).</summary>
    static void Park(RoomHarness h, params int[] keep)
    {
        var i = 0;
        foreach (var v in Core(h).V)
        {
            if (Array.IndexOf(keep, v.Id) >= 0) continue;
            var cell = Parking[i++ % Parking.Length];
            Put(v, cell % DanceMap.W, cell / DanceMap.W);
            v.Stand = 10_000;
        }
    }

    /// <summary>Два нижні ряди подвір'я — далеко від кола й від тих, кого тест ставить поруч.</summary>
    static readonly int[] Parking = [.. DanceMap.Walkable.Where(c => c / DanceMap.W >= 17)];

    /// <summary>Ядро з одним гравцем (місце 0) і кількома ботами, які стоять і нікуди не хочуть.</summary>
    static DanceCore Bare(int bots = 3, int seed = 1)
    {
        var core = new DanceCore(new Random(seed));
        core.Deal([0], bots);
        foreach (var v in core.V)
        {
            DanceCore.Forget(v);
            v.Stand = 0;
        }
        return core;
    }

    /// <summary>Один тик голого ядра в порядку гри: годинники, мозок ботів, крок.</summary>
    static void CoreTick(DanceCore core)
    {
        core.T++;
        core.TimersAll();
        core.ThinkAll();
        core.StepAll();
    }

    static JsonElement LastFrame(RoomHarness h) => Views.Json(((RoomFrame)h.Outbox.Last(o => o is RoomFrame)).Frame);
    static string FrameText(RoomHarness h) => Views.Text(G(h).Frame());

    // =============================================================================================
    // Мапа
    // =============================================================================================

    [Fact]
    public void Map_is_30_by_20_fenced_and_every_walkable_cell_reaches_every_other()
    {
        Assert.Equal(20, DanceMap.Rows.Length);
        Assert.All(DanceMap.Rows, r => Assert.Equal(30, r.Length));
        Assert.All(DanceMap.Rows[0], c => Assert.Equal('#', c));
        Assert.All(DanceMap.Rows[19], c => Assert.Equal('#', c));
        Assert.All(DanceMap.Rows, r => Assert.True(r[0] == '#' && r[29] == '#'));
        Assert.Equal(442, DanceMap.Walkable.Length);
        var hops = DanceMap.NextHop;
        foreach (var target in DanceMap.Walkable)
            foreach (var cell in DanceMap.Walkable)
            {
                var hop = hops[target * DanceMap.Cells + cell];
                if (cell == target) Assert.Equal(DanceMap.NoHop, hop);
                else Assert.InRange(hop, 0, 3);
            }
        Assert.Equal(32, DanceMap.Girls.Distinct().Count());
        Assert.Equal(32, DanceMap.Boys.Distinct().Count());
    }

    [Fact]
    public void The_circle_is_clear_and_sits_right_below_the_musicians()
    {
        // жодної перешкоди в колі й на 16 довкола — танцюрист у колі ніде не впирається
        for (var x = DanceMap.CircleX - DanceMap.CircleR - 16; x <= DanceMap.CircleX + DanceMap.CircleR + 16; x += 4)
            for (var y = DanceMap.CircleY - DanceMap.CircleR - 16; y <= DanceMap.CircleY + DanceMap.CircleR + 16; y += 4)
            {
                long dx = x - DanceMap.CircleX, dy = y - DanceMap.CircleY;
                if (dx * dx + dy * dy <= (DanceMap.CircleR + 16) * (DanceMap.CircleR + 16)) Assert.True(DanceMap.PassableAt(x, y), $"({x},{y})");
            }
        Assert.Equal(32, DanceMap.Ring.Length);
        Assert.All(DanceMap.Ring, c => Assert.True(DanceMap.InCircle(DanceMap.CenterX(c), DanceMap.CenterY(c))));
        // точка стояння в клітинці кола (±15) — усе одно в колі
        Assert.All(DanceMap.Ring, c => Assert.True(DanceMap.InCircle(DanceMap.CenterX(c) + 15, DanceMap.CenterY(c) - 15) || DanceMap.InCircle(DanceMap.CenterX(c) - 15, DanceMap.CenterY(c) + 15)));
        // музики — над колом, «біля музик» — рядок просто під помостом
        var m = DanceMap.Rows[1].IndexOf('M');
        Assert.Equal(DanceMap.CircleX, (m + 2) * DanceMap.Cell);
        Assert.Equal(6, DanceMap.Stage.Length);
        Assert.All(DanceMap.Stage, c => Assert.Equal(3, c / DanceMap.W));
    }

    [Fact]
    public void Next_hop_table_leads_to_the_target_within_bfs_distance_steps()
    {
        var rng = new Random(7);
        var walk = DanceMap.Walkable;
        for (var pair = 0; pair < 50; pair++)
        {
            var from = walk[rng.Next(walk.Length)];
            var to = walk[rng.Next(walk.Length)];
            var dist = Bfs(from)[to];
            var cell = from;
            var steps = 0;
            while (cell != to)
            {
                var hop = DanceMap.NextHop[to * DanceMap.Cells + cell];
                cell = (cell / DanceMap.W + DanceCore.DY[hop]) * DanceMap.W + cell % DanceMap.W + DanceCore.DX[hop];
                Assert.True(DanceMap.Pass[cell]);
                Assert.True(++steps <= dist, "шлях довший за BFS");
            }
            Assert.Equal(dist, steps);
        }
    }

    static int[] Bfs(int from)
    {
        var d = Enumerable.Repeat(-1, DanceMap.Cells).ToArray();
        var q = new Queue<int>();
        d[from] = 0;
        q.Enqueue(from);
        while (q.Count > 0)
        {
            var u = q.Dequeue();
            for (var k = 0; k < 4; k++)
            {
                int x = u % DanceMap.W + DanceCore.DX[k], y = u / DanceMap.W + DanceCore.DY[k];
                var n = y * DanceMap.W + x;
                if (x < 0 || y < 0 || x >= DanceMap.W || y >= DanceMap.H || !DanceMap.Pass[n] || d[n] >= 0) continue;
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
        Put(player, 12, 5, 2, -3);
        Put(bot, 12, 5, 2, -3);
        var script = new[] { 0, 0, 1, 0, 3, 2, 1, 1, 0, -1 };
        var a = new List<(int, int, int, bool)>();
        var b = new List<(int, int, int, bool)>();
        for (var t = 0; t < 100; t++)
        {
            player.Want = bot.Want = script[t / 10];
            DanceCore.Step(player);
            DanceCore.Step(bot);
            a.Add((player.X, player.Y, player.Dir, player.Moving));
            b.Add((bot.X, bot.Y, bot.Dir, bot.Moving));
        }
        Assert.Equal(a, b);
        Assert.Equal(player.State, bot.State);
    }

    [Fact]
    public void Everyone_moves_exactly_three_units_or_not_at_all_and_never_into_an_obstacle()
    {
        var h = Table(8, seed: 11, options: new { crowd = "big" });
        var rng = new Random(9);
        var core = Core(h);
        var was = core.V.Select(v => (v.X, v.Y)).ToArray();
        for (var t = 0; t < 2500; t++)
        {
            if (t % 5 == 0)
                for (var s = 0; s < 8; s++) h.Input(s, "move", new { dir = rng.Next(-1, 4) });
            if (t % 37 == 0) h.Act(t % 8, "fig", new { f = t % 4 });
            h.Tick();
            if (G(h).Phase is not (Dance.PhaseStart or Dance.PhaseGo)) break;
            foreach (var v in core.V)
            {
                int dx = Math.Abs(v.X - was[v.Id].X), dy = Math.Abs(v.Y - was[v.Id].Y);
                Assert.True((dx == 0 && dy == 0) || (dx == 3 && dy == 0) || (dx == 0 && dy == 3), $"id {v.Id}: {dx},{dy}");
                Assert.Equal(dx + dy > 0, v.Moving);
                Assert.True(DanceMap.BoxFits(v.X, v.Y), $"id {v.Id} у ({v.X},{v.Y})");
                was[v.Id] = (v.X, v.Y);
            }
        }
    }

    [Fact]
    public void Posing_stunned_offended_and_out_dancers_do_not_move_whatever_they_want()
    {
        var core = Bare(3);
        var q = core.V;
        for (var i = 0; i < q.Length; i++) Put(q[i], 10 + i * 2, 5);
        q[0].Pose = 5;
        q[1].Stun = 5;
        q[2].Offended = 5;
        q[3].Dead = true;
        foreach (var v in q) v.Want = 0;
        var xs = q.Select(v => v.X).ToArray();
        for (var i = 0; i < 4; i++)
            foreach (var v in q)
            {
                DanceCore.Step(v);
                Assert.False(v.Moving);
            }
        Assert.Equal(xs, q.Select(v => v.X));
        q[0].Pose = 0;
        DanceCore.Step(q[0]);
        Assert.Equal(xs[0] + DanceCore.Speed, q[0].X);
    }

    // =============================================================================================
    // Фігура — одна функція на всіх
    // =============================================================================================

    [Fact]
    public void A_bot_and_a_player_pose_for_the_same_frames_and_are_judged_the_same_way()
    {
        var core = Bare(1);
        var player = core.V.Single(v => v.Owner == 0);
        var bot = core.V.Single(v => v.Owner < 0);
        InRing(player);
        InRing(bot, 6);
        bot.Stand = 1000;
        core.T = 100;
        core.Announce(2, 130);
        bot.PlanAt = 133;
        bot.PlanFig = 2;
        var seenP = new List<(int, int)>();
        var seenB = new List<(int, int)>();
        for (var t = 101; t <= 160; t++)
        {
            if (t == 133) Assert.True(core.Perform(player, 2, t, true));      // гравець тисне між тиками 132 і 133
            core.T = t;
            core.TimersAll();
            core.ThinkAll();
            core.StepAll();
            if (t == 130 + DanceCore.Late + 1) core.Judge();
            if (player.State >= 4) seenP.Add((t, player.State));
            if (bot.State >= 4) seenB.Add((t, bot.State));
        }
        Assert.Equal(seenP, seenB);
        Assert.Equal(DanceCore.PoseTicks - 1, seenP.Count);                // лише фігура, жодного «?»
        Assert.Equal((133, 5 + 2), seenP[0]);                              // уперше видно саме на тику 133
        Assert.True(player.Hit && player.HitIn && bot.Hit && bot.HitIn);
    }

    [Fact]
    public void The_window_is_five_ticks_early_to_ten_late_and_late_poses_are_taken_until_twenty_five()
    {
        foreach (var (off, ok, taken) in new[] { (-6, false, false), (-5, true, true), (0, true, true), (10, true, true), (11, false, true), (25, false, true), (26, false, false) })
        {
            var core = Bare(0);
            var p = core.V[0];
            core.T = 50;
            core.Announce(1, 100);
            Assert.Equal(taken, core.Perform(p, 1, 100 + off, true));
            Assert.Equal(ok, p.Hit);
            if (taken) Assert.Equal(ok ? 6 : 10, p.State);                 // 5+1 — вціляв, 9+1 — ні
        }
        // не та фігура в самий такт — видно, що не та
        var c = Bare(0);
        c.Announce(3, 100);
        Assert.True(c.Perform(c.V[0], 0, 100, true));
        Assert.False(c.V[0].Hit);
        Assert.Equal(9, c.V[0].State);
        // друга фігура на той самий виклик — ні
        Assert.False(c.Perform(c.V[0], 3, 101, true));
        // виклику нема — ні
        c.EndCall();
        var d = Bare(0);
        Assert.False(d.Perform(d.V[0], 0, 5, true));
    }

    [Fact]
    public void Everyone_on_their_feet_who_did_not_hit_gets_a_question_mark_for_a_second()
    {
        var core = Bare(4);
        var q = core.V;
        for (var i = 0; i < q.Length; i++) { Put(q[i], 10 + i * 2, 5); q[i].Stand = 1000; }
        core.T = 10;
        core.Announce(0, 40);
        foreach (var v in q) v.PlanAt = v.PlanFig = -1;           // ніхто з ботів не танцює
        var hitter = q[0];
        core.Perform(hitter, 0, 40, true);
        q[1].Offended = 100;                                        // сидить — не судимо
        q[2].Stun = 100;                                            // отетерів — «збився», але видно отетеріння
        core.T = 51;
        core.Judge();
        Assert.Equal(0, hitter.Miss);
        Assert.Equal(0, q[1].Miss);
        Assert.Equal(DanceCore.MissTicks, q[2].Miss);
        Assert.Equal(4, q[2].State);
        Assert.Equal(DanceCore.MissTicks, q[3].Miss);
        Assert.Equal(13, q[3].State);
        for (var i = 0; i < DanceCore.MissTicks; i++) core.TimersAll();
        Assert.Equal(0, q[3].State);
    }

    /// <summary>Скільки ботів вцілює, спізнюється, плутає й мріє — за тисячу викликів.</summary>
    [Fact]
    public void Bots_hit_about_nine_calls_in_ten_and_otherwise_are_late_wrong_or_dreaming()
    {
        var core = new DanceCore(new Random(3));
        core.Deal([], 40);
        int hits = 0, late = 0, wrong = 0, none = 0, total = 0;
        var offsets = new SortedSet<int>();
        for (var call = 0; call < 250; call++)
        {
            var beat = core.T + 40;
            var fig = call % 4;
            core.Announce(fig, beat);
            var first = new int[core.N];
            Array.Fill(first, int.MinValue);
            while (core.T < beat + DanceCore.LateMax)
            {
                CoreTick(core);
                if (core.T == beat + DanceCore.Late + 1) core.Judge();
                foreach (var v in core.V)
                    if (first[v.Id] == int.MinValue && v.State is >= 5 and <= 12) first[v.Id] = core.T - beat;
            }
            foreach (var v in core.V)
            {
                if (!v.Upright) continue;
                total++;
                if (!v.Danced) none++;
                else if (v.PoseFig != fig) wrong++;
                else if (!v.Hit) late++;
                else { hits++; offsets.Add(first[v.Id]); }
            }
            core.EndCall();
            for (var i = 0; i < 60; i++) CoreTick(core);
        }
        output.WriteLine($"вціляли {hits * 1000 / total}‰, спізнились {late * 1000 / total}‰, переплутали {wrong * 1000 / total}‰, замріялись {none * 1000 / total}‰");
        Assert.InRange(hits * 1000 / total, 880, 940);
        Assert.InRange(late * 1000 / total, 25, 60);
        Assert.InRange(wrong * 1000 / total, 20, 50);
        Assert.InRange(none * 1000 / total, 7, 30);
        // вціляні боти стають у фігуру в кожен тик вікна, від −5 до +10
        Assert.Equal(Enumerable.Range(-DanceCore.Early, DanceCore.Early + DanceCore.Late + 1), offsets);
    }

    // =============================================================================================
    // Музики
    // =============================================================================================

    /// <summary>Усі виклики раунду: (тик виклику, фігура, такт, відлік, темп) — з кадрів, як їх бачить клієнт.</summary>
    static List<(int At, int Fig, int Beat, int Lead, int Tempo)> Calls(RoomHarness h)
    {
        var calls = new List<(int, int, int, int, int)>();
        var prev = -1;
        while (G(h).Phase is Dance.PhaseStart or Dance.PhaseGo)
        {
            h.Tick();
            var f = LastFrame(h);
            var m = f.GetProperty("m").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            if (m[0] >= 0 && prev < 0) calls.Add((f.GetProperty("t").GetInt32(), m[0], m[1], m[2], m[3]));
            prev = m[0];
        }
        return calls;
    }

    [Fact]
    public void Musicians_call_a_figure_every_four_to_eight_seconds_with_a_second_of_warning()
    {
        var h = Table(2, seed: 13, options: new { rounds = "1" });
        var calls = Calls(h);
        output.WriteLine($"викликів за раунд: {calls.Count}");
        Assert.InRange(calls.Count, 16, 24);
        Assert.All(calls, c => Assert.Equal(c.Beat - c.Lead, c.At));
        Assert.All(calls, c => Assert.Equal(Dance.Pulse[c.Tempo] * Dance.LeadPulses, c.Lead));
        Assert.All(calls, c => Assert.InRange(c.Lead, 24, 36));                      // ≈ 1–1,5 с попередження
        Assert.InRange(calls[0].Beat - Dance.StartTicks, 72, 108);                     // перший такт — за 3–4 с
        for (var i = 1; i < calls.Count; i++)
        {
            var gap = calls[i].Beat - calls[i - 1].Beat;
            Assert.InRange(gap, 100, 200);                                            // 4–8 с
            Assert.Equal(0, gap % Dance.Pulse[calls[i].Tempo]);                         // такт лягає на пульс музики
        }
        Assert.Equal(4, calls.Select(c => c.Fig).Distinct().Count());
    }

    [Fact]
    public void Music_speeds_up_towards_the_end_of_the_round()
    {
        var h = Table(2, seed: 15, options: new { rounds = "1" });
        var calls = Calls(h);
        var tempos = calls.Select(c => c.Tempo).ToList();
        Assert.Equal(0, tempos[0]);
        Assert.Equal(2, tempos[^1]);
        Assert.Equal(tempos.Order(), tempos);                                         // лише пришвидшується
        double Gap(int tempo)
        {
            var g = new List<int>();
            for (var i = 1; i < calls.Count; i++) if (calls[i].Tempo == tempo) g.Add(calls[i].Beat - calls[i - 1].Beat);
            return g.Average();
        }
        Assert.True(Gap(0) > Gap(1) && Gap(1) > Gap(2), $"{Gap(0)} {Gap(1)} {Gap(2)}");
        Assert.Equal(0, Dance.TempoAt(Dance.RoundTicks));
        Assert.Equal(1, Dance.TempoAt(Dance.Tempo1Left));
        Assert.Equal(2, Dance.TempoAt(Dance.Tempo2Left));
    }

    // =============================================================================================
    // Боти
    // =============================================================================================

    [Fact]
    public void Bots_stand_from_half_a_second_to_twenty_pause_on_the_way_and_fidget()
    {
        var core = new DanceCore(new Random(21));
        core.Deal([], 40);
        var target = new int[core.N];
        var stand = new int[core.N];
        var dir = new int[core.N];
        var lens = new List<int>();
        int pauses = 0, fidgets = 0, turned = 0;
        for (var t = 0; t < 6000; t++)
        {
            for (var i = 0; i < core.N; i++) (target[i], stand[i], dir[i]) = (core.V[i].Target, core.V[i].Stand, core.V[i].Dir);
            CoreTick(core);
            foreach (var v in core.V)
            {
                if (target[v.Id] >= 0 && v.Target < 0 && v.Stand > 0 && !v.Moving)
                {
                    lens.Add(v.Stand);
                    if (v.Dir != dir[v.Id]) turned++;                      // дійшов — на місці не обертається
                }
                if (target[v.Id] >= 0 && v.Target == target[v.Id] && v.Want < 0 && stand[v.Id] == 0 && v.Pose == 0) pauses++;
                if (stand[v.Id] > 3 && v.Want >= 0) fidgets++;
            }
        }
        Assert.True(lens.Count > 800, $"{lens.Count}");
        Assert.All(lens, s => Assert.InRange(s, DanceCore.StandMin, DanceCore.LongStandMax));
        var share = (int from, int to) => lens.Count(s => s >= from && s <= to) * 100 / lens.Count;
        Assert.InRange(share(DanceCore.StandMin, DanceCore.StandMax), 65, 82);
        Assert.InRange(share(301, DanceCore.LongStandMax), 1, 8);
        Assert.Equal(0, turned);
        Assert.True(pauses > 200, $"зупинок посеред дороги {pauses}");
        Assert.True(fidgets > 50, $"переступань {fidgets}");
    }

    [Fact]
    public void A_bot_that_is_blocked_picks_a_new_target_next_tick()
    {
        var core = Bare(1);
        var bot = core.V.Single(v => v.Owner < 0);
        Put(bot, 8, 1, oy: -8);
        bot.Target = DanceMap.Walkable[^1];
        bot.Want = 3;
        DanceCore.Step(bot);
        Assert.True(bot.Blocked);
        core.Think(bot);
        Assert.Equal(-1, bot.Target);
        core.Think(bot);
        Assert.True(bot.Target >= 0);
    }

    [Fact]
    public void Bots_reach_their_targets_and_never_get_stuck()
    {
        var arrivals = 0;
        var blocked = new List<string>();
        for (var seed = 4; seed <= 6; seed++)
        {
            var core = new DanceCore(new Random(seed));
            core.Deal([], 48);
            var stand = new int[core.N];
            var target = new int[core.N];
            var wander = new int[core.N];
            for (var t = 0; t < 3000; t++)
            {
                for (var i = 0; i < core.N; i++) (stand[i], target[i], wander[i]) = (core.V[i].Stand, core.V[i].Target, core.V[i].Wander);
                CoreTick(core);
                foreach (var v in core.V)
                {
                    var fidget = stand[v.Id] > 0 || wander[v.Id] > 0 || v.Wander > 0;
                    if (v.Blocked && v.Target >= 0 && !fidget) blocked.Add($"сід {seed} тик {t} id {v.Id} ({v.X},{v.Y}) ціль {v.Target}");
                    if (target[v.Id] >= 0 && v.Target < 0 && v.Stand > 0) arrivals++;
                }
            }
        }
        Assert.True(blocked.Count == 0, string.Join("\n", blocked.Take(10)));
        Assert.True(arrivals > 900, $"дійшли лише {arrivals} разів");
    }

    [Fact]
    public void The_circle_is_never_empty_and_some_bots_dance_six_calls_in_a_row_in_it()
    {
        // гравець, що витанцьовує стрічку, стоїть у колі шість тактів поспіль — боти мусять робити так само
        var core = new DanceCore(new Random(17));
        core.Deal([], 32);
        var run = new int[core.N];
        var runs = new List<int>();
        var inside = new List<int>();
        for (var call = 0; call < 100; call++)
        {
            for (var i = 0; i < 150; i++) CoreTick(core);
            var n = 0;
            foreach (var v in core.V)
            {
                if (DanceMap.InCircle(v.X, v.Y)) { run[v.Id]++; n++; }
                else { if (run[v.Id] > 0) runs.Add(run[v.Id]); run[v.Id] = 0; }
            }
            inside.Add(n);
        }
        output.WriteLine($"у колі на такт: {inside.Min()}–{inside.Max()}, у середньому {inside.Average():F1}; серій ≥ 6: {runs.Count(r => r >= 6)} з {runs.Count}");
        Assert.True(inside.Min() >= 2, "коло порожніє");
        Assert.True(inside.Average() >= 5);
        Assert.True(runs.Count(r => r >= Dance.RibbonStreak) >= 10, $"серій ≥ 6 лише {runs.Count(r => r >= Dance.RibbonStreak)}");
    }

    [Fact]
    public void An_offended_bot_sits_for_three_seconds_then_walks_again()
    {
        var h = Table(2, seed: 5);
        Go(h);
        var me = Me(h, 0);
        var bot = Core(h).V.First(v => v.Owner < 0);
        Park(h, me.Id, bot.Id);
        Put(me, 10, 5, dir: 0);
        Put(bot, 10, 5, ox: 20);
        Assert.True(h.Act(0, "slap", new { }).Ok, h.Reply.Message);
        Assert.Equal(2, bot.State);
        h.Tick(DanceCore.OffendTicks - 1);
        Assert.Equal(2, bot.State);
        h.Tick();
        Assert.True(bot.Upright);
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
    public void Match_starts_with_a_three_second_look_around_where_figures_and_slaps_are_refused()
    {
        var h = Table(2, seed: 19);
        Assert.Equal(Dance.PhaseStart, G(h).Phase);
        Assert.Equal(Dance.StartTicks, G(h).Left);
        Assert.False(h.Act(0, "fig", new { f = 0 }).Ok);
        Assert.Equal("Музики ще настроюються", h.Reply.Message);
        Assert.False(h.Act(0, "slap", new { }).Ok);
        Assert.Equal("Музики ще настроюються", h.Reply.Message);
        // ходити можна всім
        var me = Me(h, 0);
        Put(me, 12, 5);
        h.Input(0, "move", new { dir = 0 });
        h.Tick(10);
        Assert.Equal(12 * 32 + 16 + 30, me.X);
        h.Tick(Dance.StartTicks - 10);
        Assert.Equal(Dance.PhaseGo, G(h).Phase);
        Assert.Equal(Dance.RoundTicks, G(h).Left);
    }

    [Fact]
    public void Six_hits_in_a_row_in_the_circle_win_the_ribbon_and_the_round()
    {
        var h = Table(2, seed: 23);
        Go(h);
        var me = Me(h, 0);
        Park(h, me.Id, Me(h, 1).Id);
        for (var k = 1; k <= Dance.RibbonStreak; k++)
        {
            ToCall(h);
            InRing(me);
            Assert.True(PressAt(h, 0, 2).Ok, h.Reply.Message);
            ToJudge(h);
            if (k < Dance.RibbonStreak)
            {
                Assert.Equal(k, S(h, 0).Streak);
                Assert.Equal(k, h.View(0).GetProperty("me").GetProperty("streak").GetInt32());
                Assert.Equal(Dance.PhaseGo, G(h).Phase);
            }
        }
        Assert.Equal(Dance.PhaseReveal, G(h).Phase);
        var r = h.View(null).GetProperty("reveal");
        Assert.Equal("ribbon", r.GetProperty("why").GetString());
        Assert.Equal([0], r.GetProperty("winners").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(Dance.RibbonStreak * Dance.PtCircle + Dance.PtRound, S(h, 0).Total);
        var row = r.GetProperty("rows").EnumerateArray().Single(x => x.GetProperty("seat").GetInt32() == 0);
        Assert.Equal(Dance.RibbonStreak, row.GetProperty("good").GetInt32());
        Assert.Equal(Dance.RibbonStreak, row.GetProperty("circle").GetInt32());
        Assert.True(row.GetProperty("win").GetBoolean());
        // Петро не танцював жодної — шість разів схибив
        Assert.Equal(Dance.RibbonStreak, r.GetProperty("rows").EnumerateArray().Single(x => x.GetProperty("seat").GetInt32() == 1).GetProperty("bad").GetInt32());
    }

    [Fact]
    public void A_miss_or_a_hit_outside_the_circle_starts_the_streak_again()
    {
        var h = Table(2, seed: 25);
        Go(h);
        var me = Me(h, 0);
        Park(h, me.Id, Me(h, 1).Id);
        void Call(bool ring, int off, bool right)
        {
            ToCall(h);
            if (ring) InRing(me); else Put(me, 3, 5);
            var f = right ? G(h).CallFig : (G(h).CallFig + 1) % 4;
            if (off <= DanceCore.LateMax) Assert.True(PressAt(h, 0, off, f).Ok, h.Reply.Message);
            ToJudge(h);
            TickTo(h, G(h).Beat + DanceCore.LateMax);
        }
        Call(true, 0, true);
        Call(true, 3, true);
        Assert.Equal(2, S(h, 0).Streak);
        Assert.Equal("ring", S(h, 0).Last);
        Call(false, 0, true);                          // у такт, але поза колом
        Assert.Equal(0, S(h, 0).Streak);
        Assert.Equal("ok", S(h, 0).Last);
        Call(true, 0, true);
        Call(true, 12, true);                          // спізнився
        Assert.Equal(0, S(h, 0).Streak);
        Assert.Equal("miss", S(h, 0).Last);
        Call(true, 0, true);
        Call(true, 0, false);                          // не та фігура
        Assert.Equal(0, S(h, 0).Streak);
        Call(true, 99, true);                          // не станцював нічого
        Assert.Equal(0, S(h, 0).Streak);
        Assert.Equal((5, 3, 4, 2), (S(h, 0).Good, S(h, 0).Bad, S(h, 0).Circle, S(h, 0).Best));
    }

    [Fact]
    public void Two_players_getting_the_ribbon_on_the_same_beat_both_win()
    {
        var h = Table(3, seed: 27);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id, Me(h, 2).Id);
        for (var k = 0; k < Dance.RibbonStreak; k++)
        {
            ToCall(h);
            InRing(Me(h, 0));
            InRing(Me(h, 1), 10);
            TickTo(h, G(h).Beat - 1);
            Assert.True(h.Act(0, "fig", new { f = G(h).CallFig }).Ok);
            Assert.True(h.Act(1, "fig", new { f = G(h).CallFig }).Ok);
            ToJudge(h);
        }
        var r = h.View(null).GetProperty("reveal");
        Assert.Equal("ribbon", r.GetProperty("why").GetString());
        Assert.Equal([0, 1], r.GetProperty("winners").EnumerateArray().Select(e => e.GetInt32()));
    }

    [Fact]
    public void Last_player_standing_wins_the_round()
    {
        var h = Table(3, seed: 29);
        Go(h);
        var a = Me(h, 0);
        Park(h, a.Id, Me(h, 1).Id, Me(h, 2).Id);
        Put(a, 10, 5, dir: 0);
        Put(Me(h, 1), 10, 5, ox: 20);
        Assert.True(h.Act(0, "slap", new { }).Ok, h.Reply.Message);
        h.Tick(Dance.SlapCoolTicks);
        Put(Me(h, 2), a.X / 32, a.Y / 32, ox: a.X % 32 - 16 - 20);
        Assert.True(h.Act(0, "slap", new { id = S(h, 2).Me }).Ok, h.Reply.Message);
        h.Tick();
        var r = h.View(null).GetProperty("reveal");
        Assert.Equal("last", r.GetProperty("why").GetString());
        Assert.Equal([0], r.GetProperty("winners").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(2 * Dance.PtKill + Dance.PtRound, S(h, 0).Total);
    }

    [Fact]
    public void Time_out_gives_the_round_to_the_most_hits_in_the_circle_and_a_tie_to_nobody()
    {
        var h = Table(2, seed: 31, options: new { rounds = "3" });
        Go(h);
        S(h, 1).Circle = 2;
        S(h, 0).Circle = 1;
        h.Tick(Dance.RoundTicks - 1);
        Assert.Equal(Dance.PhaseGo, G(h).Phase);
        S(h, 1).Circle = 2;               // суд міг скинути — рахуємо на самому кінці
        S(h, 0).Circle = 1;
        h.Tick();
        var r = h.View(null).GetProperty("reveal");
        Assert.Equal("time", r.GetProperty("why").GetString());
        Assert.Equal([1], r.GetProperty("winners").EnumerateArray().Select(e => e.GetInt32()));

        var g = Table(2, seed: 33, options: new { rounds = "1" });
        Go(g);
        g.Tick(Dance.RoundTicks);
        Assert.Equal("none", g.View(null).GetProperty("reveal").GetProperty("why").GetString());
        Assert.Empty(g.View(null).GetProperty("reveal").GetProperty("winners").EnumerateArray());
    }

    [Fact]
    public void Reveal_lasts_six_seconds_freezes_everyone_and_then_the_next_round_starts_fresh()
    {
        var h = Table(2, seed: 35);
        Go(h);
        var ids = (S(h, 0).Me, S(h, 1).Me);
        h.Input(0, "move", new { dir = 0 });
        h.Tick(Dance.RoundTicks);
        Assert.Equal(Dance.PhaseReveal, G(h).Phase);
        var frozen = Core(h).V.Select(v => (v.X, v.Y)).ToArray();
        h.Input(0, "move", new { dir = 1 });
        h.Tick(Dance.RevealTicks - 1);
        Assert.Equal(frozen, Core(h).V.Select(v => (v.X, v.Y)));
        Assert.All(Core(h).V, v => Assert.True(v.State is 0 or 2 or 3));
        h.Tick();
        Assert.Equal(Dance.PhaseStart, G(h).Phase);
        Assert.Equal(2, G(h).RoundNo);
        Assert.NotEqual(ids, (S(h, 0).Me, S(h, 1).Me));
        Assert.All(new[] { 0, 1 }, s => Assert.True(S(h, s).Alive && S(h, s).Streak == 0 && S(h, s).Good == 0 && S(h, s).Bad == 0));
        Assert.Equal(-1, G(h).CallFig);
    }

    [Fact]
    public void After_the_last_round_the_match_finishes_with_totals_in_the_journal()
    {
        var h = Table(2, seed: 37, options: new { rounds = "1" });
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id);
        ToCall(h);
        InRing(Me(h, 0));
        Assert.True(PressAt(h, 0, 0).Ok);
        h.Tick(Dance.RoundTicks + Dance.RevealTicks);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Finished.Single().Result.Winners);
        Assert.Contains("Вечорниці: Оля 4 : Петро 0", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Contains(h.Scores, s => s.Nick == "Оля" && s.Score == 4);
        Assert.Equal("end", h.View(null).GetProperty("result").GetProperty("why").GetString());
    }

    [Fact]
    public void Equal_totals_share_the_win_and_all_zero_is_a_draw()
    {
        var h = Table(3, seed: 39, options: new { rounds = "1" });
        Go(h);
        S(h, 0).Total = 5;
        S(h, 1).Total = 5;
        h.Tick(Dance.RoundTicks + Dance.RevealTicks);
        Assert.Equal([0, 1], h.Finished.Single().Result.Winners);

        var g = Table(2, seed: 41, options: new { rounds = "1" });
        Go(g);
        g.Tick(Dance.RoundTicks + Dance.RevealTicks);
        Assert.Empty(g.Finished.Single().Result.Winners);
        Assert.Contains("нічия", g.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void Options_set_rounds_and_the_crowd_and_junk_falls_back()
    {
        Assert.Equal(1, Rounds(new { rounds = "1" }));
        Assert.Equal(5, Rounds(new { rounds = "5" }));
        Assert.Equal(3, Rounds(new { rounds = "7" }));
        Assert.Equal(26, Core(Table(2, options: new { crowd = "auto" })).N);
        Assert.Equal(48, Core(Table(8)).N);
        Assert.Equal(22, Core(Table(2, options: new { crowd = "small" })).N);
        Assert.Equal(50, Core(Table(2, options: new { crowd = "big" })).N);
        Assert.Equal(26, Core(Table(2, options: new { crowd = "лава" })).N);

        static int Rounds(object o) => Table(2, options: o).View(null).GetProperty("of").GetInt32();
    }

    // =============================================================================================
    // Фігура гравця
    // =============================================================================================

    [Fact]
    public void Figure_refusals_say_what_went_wrong_and_change_nothing()
    {
        var h = Table(2, seed: 43);
        Go(h);
        var me = Me(h, 0);
        Park(h, me.Id, Me(h, 1).Id);
        Assert.False(h.Act(0, "fig", new { f = 0 }).Ok);
        Assert.Equal("Музики ще не кликали фігуру", h.Reply.Message);
        ToCall(h);
        Assert.False(h.Act(0, "fig", new { f = 7 }).Ok);
        Assert.Equal("Такої фігури нема", h.Reply.Message);
        if (G(h).T + 1 < G(h).Beat - DanceCore.Early)
        {
            Assert.False(h.Act(0, "fig", new { f = G(h).CallFig }).Ok);
            Assert.Equal("Зарано — дочекайся такту", h.Reply.Message);
            Assert.False(me.Danced);
        }
        Assert.True(PressAt(h, 0, 0).Ok);
        Assert.False(h.Act(0, "fig", new { f = G(h).CallFig }).Ok);
        Assert.Equal("Ти вже відтанцював цю фігуру", h.Reply.Message);
        TickTo(h, G(h).Beat + DanceCore.LateMax);
        Assert.Equal(-1, G(h).CallFig);
        Assert.False(h.Act(1, "fig", new { f = 0 }).Ok);
        Assert.Equal("Проґавив — чекай наступної фігури", h.Reply.Message);
    }

    [Fact]
    public void Counts_and_the_streak_change_only_at_the_judge_not_at_the_press()
    {
        var h = Table(2, seed: 45);
        Go(h);
        var me = Me(h, 0);
        Park(h, me.Id, Me(h, 1).Id);
        ToCall(h);
        InRing(me);
        Assert.True(PressAt(h, 0, -2).Ok);
        h.Tick();
        Assert.Equal(0, S(h, 0).Good);
        Assert.True(h.View(0).GetProperty("me").GetProperty("danced").GetBoolean());
        TickTo(h, G(h).Beat + DanceCore.Late);
        Assert.Equal(0, S(h, 0).Streak);
        h.Tick();
        Assert.Equal((1, 1, 1), (S(h, 0).Good, S(h, 0).Circle, S(h, 0).Streak));
        Assert.Equal("ring", h.View(0).GetProperty("me").GetProperty("last").GetString());
    }

    // =============================================================================================
    // Ляпас
    // =============================================================================================

    [Fact]
    public void Slapping_a_bot_offends_it_and_stuns_the_slapper_for_everyone_to_see()
    {
        var h = Table(2, seed: 47);
        Go(h);
        var me = Me(h, 0);
        var bot = Core(h).V.First(v => v.Owner < 0);
        Park(h, me.Id, bot.Id);
        Put(me, 10, 5, dir: 0);
        Put(bot, 10, 5, ox: 24);
        Assert.True(h.Act(0, "slap", new { }).Ok, h.Reply.Message);
        h.Tick();
        var f = LastFrame(h);
        Assert.Contains(f.GetProperty("ev").EnumerateArray(), e => e.GetArrayLength() == 5 && e[0].GetInt32() == 1 && e[1].GetInt32() == me.Id && e[2].GetInt32() == bot.Id && e[3].GetInt32() == 0 && e[4].GetInt32() == -1);
        Assert.Equal(2, f.GetProperty("v")[bot.Id * 4 + 3].GetInt32());
        Assert.Equal(4, f.GetProperty("v")[me.Id * 4 + 3].GetInt32());      // отетерів — видно всім
        // отетерілий не ходить і не танцює, а за 1,5 с оговтується
        h.Input(0, "move", new { dir = 1 });
        h.Tick(5);
        Assert.False(me.Moving);
        Assert.False(h.Act(0, "slap", new { }).Ok);
        Assert.Equal("Ти ще отетерілий — не до танців", h.Reply.Message);
        TickTo(h, G(h).T + Dance.StunTicks);
        Assert.Equal(0, me.Stun);
        h.Tick();
        Assert.True(me.Moving);
        Assert.True(S(h, 0).Alive);
        Assert.Equal(0, S(h, 0).Total);
    }

    [Fact]
    public void Slapping_a_player_knocks_him_out_names_his_seat_and_pays_two_points()
    {
        var h = Table(3, seed: 49);
        Go(h);
        var me = Me(h, 0);
        var him = Me(h, 2);
        Park(h, me.Id, him.Id);
        Put(me, 10, 5, dir: 2);
        Put(him, 10, 5, ox: -20);
        Assert.True(h.Act(0, "slap", new { }).Ok, h.Reply.Message);
        h.Tick();
        Assert.False(S(h, 2).Alive);
        Assert.Equal(3, him.State);
        Assert.Equal(0, me.Stun);                         // влучив у гравця — не отетерів
        Assert.Equal(Dance.PtKill, S(h, 0).Total);
        Assert.Contains(LastFrame(h).GetProperty("ev").EnumerateArray(), e => e[0].GetInt32() == 1 && e[3].GetInt32() == 1 && e[4].GetInt32() == 2);
        var dead = h.View(null).GetProperty("dead").EnumerateArray().Single();
        Assert.Equal((2, him.Id), (dead.GetProperty("seat").GetInt32(), dead.GetProperty("id").GetInt32()));
        // вибулий більше не танцює й не б'ється
        ToCall(h);
        Assert.False(h.Act(2, "fig", new { f = 0 }).Ok);
        Assert.Equal("Тебе вже вивели з танцю — дивись, хто кого", h.Reply.Message);
    }

    [Fact]
    public void The_slap_takes_the_nearest_on_their_feet_within_45_degrees_and_36_units_in_front()
    {
        var core = Bare(4);
        var p = core.V.Single(v => v.Owner == 0);
        var bots = core.V.Where(v => v.Owner < 0).ToArray();
        Put(p, 12, 5, dir: 0);
        Put(bots[0], 12, 5, ox: 30, oy: 0);          // перед собою, 30
        Put(bots[1], 12, 5, ox: 20, oy: 18);         // 20 вперед, 18 убік — 42°, ближче
        Put(bots[2], 12, 5, ox: 10, oy: -14);        // 54° — поза конусом
        Put(bots[3], 12, 5, ox: -10);                // позаду
        Assert.Equal(bots[1].Id, core.Reach(p));
        bots[1].Offended = 10;                       // сидить — не рахується
        Assert.Equal(bots[0].Id, core.Reach(p));
        bots[0].X = p.X + 37;                         // задалеко
        Assert.Equal(-1, core.Reach(p));
        p.Dir = 2;
        Assert.Equal(bots[3].Id, core.Reach(p));
    }

    [Fact]
    public void An_explicit_slap_target_is_taken_up_to_52_units_any_side_and_refused_beyond()
    {
        var h = Table(2, seed: 51);
        Go(h);
        var me = Me(h, 0);
        var bot = Core(h).V.First(v => v.Owner < 0);
        Park(h, me.Id, bot.Id);
        Put(me, 12, 5, dir: 0);
        Put(bot, 12, 5, ox: -30, oy: 43);            // позаду й збоку, 52,4 — задалеко
        Assert.False(h.Act(0, "slap", new { id = bot.Id }).Ok);
        Assert.Equal("Не дотягнешся — підійди ближче", h.Reply.Message);
        bot.Y -= 1;                                  // 51,7
        Assert.True(h.Act(0, "slap", new { id = bot.Id }).Ok, h.Reply.Message);
        Assert.Equal(1, me.Dir);                     // обернувся до того, кого вдарив
    }

    [Fact]
    public void No_slapping_yourself_a_seated_one_while_dancing_or_before_the_hand_recovers()
    {
        var h = Table(2, seed: 53);
        Go(h);
        var me = Me(h, 0);
        var bot = Core(h).V.First(v => v.Owner < 0);
        Park(h, me.Id, bot.Id);
        Put(Me(h, 1), 25, 17);
        Put(me, 12, 5, dir: 0);
        Put(bot, 12, 5, ox: 20);
        Assert.False(h.Act(0, "slap", new { id = me.Id }).Ok);
        Assert.Equal("Себе по щоці? Не годиться", h.Reply.Message);
        Assert.False(h.Act(0, "slap", new { id = 999 }).Ok);
        Assert.Equal("Такого танцюриста нема", h.Reply.Message);
        Put(bot, 12, 5, ox: 60);
        Assert.False(h.Act(0, "slap", new { }).Ok);
        Assert.Equal("Поруч нікого — ляпас у повітря", h.Reply.Message);
        Put(bot, 12, 5, ox: 20);
        bot.Offended = 30;
        Assert.False(h.Act(0, "slap", new { id = bot.Id }).Ok);
        Assert.Equal("Той і так уже сидить", h.Reply.Message);
        bot.Offended = 0;
        me.Pose = 5;
        Assert.False(h.Act(0, "slap", new { }).Ok);
        Assert.Equal("Спершу дотанцюй", h.Reply.Message);
        me.Pose = 0;
        Assert.True(h.Act(0, "slap", new { }).Ok);
        h.Tick(Dance.StunTicks);
        Assert.False(h.Act(0, "slap", new { }).Ok);
        Assert.Equal("Рука ще не відійшла", h.Reply.Message);
        h.Tick(Dance.SlapCoolTicks - Dance.StunTicks);
        Assert.Equal(0, S(h, 0).SlapCool);
    }

    [Fact]
    public void A_stunned_dancer_cannot_dance_and_misses_the_figure()
    {
        var h = Table(2, seed: 55);
        Go(h);
        var me = Me(h, 0);
        var bot = Core(h).V.First(v => v.Owner < 0);
        Park(h, me.Id, bot.Id);
        ToCall(h);
        TickTo(h, G(h).Beat - 3);
        Put(me, 12, 5, dir: 0);
        Put(bot, 12, 5, ox: 20);
        Assert.True(h.Act(0, "slap", new { }).Ok);
        h.Tick();
        Assert.False(h.Act(0, "fig", new { f = G(h).CallFig }).Ok);
        Assert.Equal("Ти ще отетерілий — не до танців", h.Reply.Message);
        ToJudge(h);
        Assert.Equal(("miss", 1), (S(h, 0).Last, S(h, 0).Bad));
    }

    // =============================================================================================
    // Вихід, F5, «Ще раз»
    // =============================================================================================

    [Fact]
    public void A_leaver_becomes_a_bot_that_keeps_walking_and_dancing()
    {
        var h = Table(3, seed: 57);
        Go(h);
        var his = Me(h, 2);
        var n = Core(h).N;
        h.Leave("Ганна");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(-1, his.Owner);
        var at = (his.X, his.Y);
        var danced = false;
        for (var i = 0; i < 600; i++)
        {
            h.Tick();
            danced |= his.State is >= 5 and <= 12;
        }
        Assert.NotEqual(at, (his.X, his.Y));
        Assert.True(danced, "колишній гравець не станцював жодної фігури");
        Assert.Equal(4 * n, LastFrame(h).GetProperty("v").GetArrayLength());
        var seat = h.View(null).GetProperty("seats").EnumerateArray().Single(s => s.GetProperty("seat").GetInt32() == 2);
        Assert.True(seat.GetProperty("out").GetBoolean());
        h.Tick(Dance.RoundTicks + Dance.RevealTicks);
        Assert.Equal(n, Core(h).N);
        Assert.Equal(2, Core(h).V.Count(v => v.Owner >= 0));
    }

    [Fact]
    public void When_only_one_player_remains_the_match_ends_in_his_favour_with_a_reveal()
    {
        var h = Table(2, seed: 59);
        Go(h);
        h.Tick(300);
        var petro = S(h, 1).Me;
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Finished.Single().Result.Winners);
        Assert.Contains("Оля лишається танцювати", h.Outbox.OfType<Journal>().Last().Text);
        var v = h.View(0);
        Assert.Equal("left", v.GetProperty("result").GetProperty("why").GetString());
        var ids = v.GetProperty("reveal").GetProperty("ids").EnumerateArray().ToDictionary(e => e.GetProperty("seat").GetInt32(), e => e.GetProperty("id").GetInt32());
        Assert.Equal(petro, ids[1]);
        Assert.All(Core(h).V, q => Assert.False(q.Moving));
    }

    [Fact]
    public void Rematch_gives_a_clean_match_with_rotated_seats()
    {
        var h = Table(2, seed: 61, options: new { rounds = "1" });
        Go(h);
        S(h, 0).Total = 7;
        h.Tick(Dance.RoundTicks + Dance.RevealTicks);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        h.Rematch("Оля");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("Петро", h.NickOf(0));
        Assert.Equal(Dance.PhaseStart, G(h).Phase);
        Assert.Equal(1, G(h).RoundNo);
        Assert.All(new[] { 0, 1 }, s => Assert.Equal(0, S(h, s).Total));
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("result").ValueKind);
    }

    [Fact]
    public void Move_from_an_out_player_or_during_the_reveal_is_swallowed_silently()
    {
        var h = Table(3, seed: 63);
        Go(h);
        var him = Me(h, 1);
        Park(h, Me(h, 0).Id, him.Id);
        Put(Me(h, 0), 12, 5, dir: 0);
        Put(him, 12, 5, ox: 20);
        Assert.True(h.Act(0, "slap", new { }).Ok);
        var x = him.X;
        Assert.True(h.Act(1, "move", new { dir = 0 }).Ok);
        h.Tick(5);
        Assert.Equal(x, him.X);
        Assert.False(h.Act(0, "move", new { dir = 7 }).Ok);
        Assert.Equal("Такого напрямку нема", h.Reply.Message);
        h.Tick(Dance.RoundTicks);
        Assert.Equal(Dance.PhaseReveal, G(h).Phase);
        Assert.True(h.Act(0, "move", 2).Ok);
        Assert.Equal(-1, Me(h, 0).Want);
    }

    [Fact]
    public void A_held_arrow_that_is_not_confirmed_for_three_seconds_is_let_go()
    {
        var h = Table(2, seed: 65);
        Go(h);
        var me = Me(h, 0);
        Park(h, me.Id, Me(h, 1).Id);
        Put(me, 1, 5);
        h.Input(0, "move", new { dir = 0 });
        for (var i = 0; i < 4; i++)
        {
            h.Tick(25);
            h.Input(0, "move", new { dir = 0 });
        }
        var x = me.X;
        h.Tick(Dance.MoveHoldTicks + 1);
        Assert.Equal(-1, me.Want);
        Assert.True(me.X > x);
        var at = me.X;
        h.Tick(100);
        Assert.Equal(at, me.X);
    }

    [Fact]
    public void A_match_that_has_not_started_refuses_every_action_and_shows_a_quiet_yard()
    {
        var h = new RoomHarness("dance", seed: 1);
        h.Join("Оля");
        h.Join("Петро");
        var view = h.View(0);
        Assert.Equal("lobby", view.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("me").ValueKind);
        Assert.Equal(24 * 4, view.GetProperty("v").GetArrayLength());
        var game = (Dance)h.Room.Game;
        Assert.Equal("Партія ще не почалась", game.Act(0, "fig", Views.Payload(new { f = 1 })).Message);
        Assert.Equal("Партія ще не почалась", game.Act(0, "slap", Views.Payload(new { })).Message);
    }

    // =============================================================================================
    // Приховане
    // =============================================================================================

    [Fact]
    public void The_frame_has_t_ph_left_v_m_ev_and_exactly_four_numbers_per_dancer()
    {
        var h = Table(5, seed: 67);
        Go(h);
        ToCall(h);
        var f = LastFrame(h);
        Assert.Equal(["t", "ph", "left", "v", "m", "ev"], f.EnumerateObject().Select(p => p.Name));
        Assert.Equal(4 * Core(h).N, f.GetProperty("v").GetArrayLength());
        Assert.All(f.GetProperty("v").EnumerateArray(), e => Assert.Equal(JsonValueKind.Number, e.ValueKind));
        var m = f.GetProperty("m").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal([G(h).CallFig, G(h).Beat, G(h).Lead, G(h).TempoNow], m);
    }

    [Fact]
    public void Frame_json_never_mentions_seats_owners_streaks_or_nicks()
    {
        var h = Table(4, seed: 69);
        Go(h);
        var me = Me(h, 0);
        Park(h, me.Id, Me(h, 1).Id);
        Put(me, 12, 5, dir: 0);
        Put(Core(h).V.First(v => v.Owner < 0), 12, 5, ox: 20);
        h.Act(0, "slap", new { });
        ToCall(h);
        InRing(Me(h, 1));
        PressAt(h, 1, 0);
        ToJudge(h);
        foreach (var f in h.Outbox.OfType<RoomFrame>())
        {
            var text = Views.Text(f.Frame);
            foreach (var word in new[] { "\"seat", "\"me", "\"owner", "\"streak", "\"nick", "\"alive", "\"good", "\"bad", "\"circle" })
                Assert.DoesNotContain(word, text);
        }
    }

    [Fact]
    public void Player_ids_are_shuffled_among_bots_across_seeds()
    {
        int high = 0, notFirst = 0;
        for (var seed = 1; seed <= 100; seed++)
        {
            var core = new DanceCore(new Random(seed));
            core.Deal([0, 1], 24);
            var ids = core.V.Where(v => v.Owner >= 0).Select(v => v.Id).ToArray();
            if (ids.Any(id => id >= core.N / 2)) high++;
            if (ids.Any(id => id >= 2)) notFirst++;
        }
        Assert.True(high > 50, $"{high}");
        Assert.True(notFirst > 90, $"{notFirst}");
    }

    [Fact]
    public void Watcher_view_has_no_me_and_other_seats_see_only_their_own()
    {
        var h = Table(3, seed: 71);
        Go(h);
        var watcher = h.View(null);
        Assert.Equal(JsonValueKind.Null, watcher.GetProperty("me").ValueKind);
        Assert.Equal(JsonValueKind.Null, watcher.GetProperty("reveal").ValueKind);
        Assert.Empty(watcher.GetProperty("dead").EnumerateArray());
        Assert.Equal(S(h, 0).Me, h.View(0).GetProperty("me").GetProperty("id").GetInt32());
        Assert.Equal(S(h, 1).Me, h.View(1).GetProperty("me").GetProperty("id").GetInt32());
        Assert.Equal(Strip(watcher), Strip(h.View(1)));
        Assert.Equal(Strip(watcher), Strip(h.View(2)));
        Assert.DoesNotContain("\"streak\"", Strip(h.View(0)));
    }

    static string Strip(JsonElement view)
    {
        var d = view.EnumerateObject().Where(p => p.Name != "me").ToDictionary(p => p.Name, p => p.Value);
        return Views.Text(d);
    }

    [Fact]
    public void An_out_player_sees_what_a_watcher_sees_plus_his_own_me()
    {
        var h = Table(3, seed: 73);
        Go(h);
        var him = Me(h, 1);
        Park(h, Me(h, 0).Id, him.Id);
        Put(Me(h, 0), 12, 5, dir: 0);
        Put(him, 12, 5, ox: 20);
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
        var h = Table(2, seed: 75, options: new { rounds = "1" });
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
        Go(h);
        h.Tick(Dance.RoundTicks - 1);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
        h.Tick();
        var ids = h.View(null).GetProperty("reveal").GetProperty("ids").EnumerateArray().Select(e => (e.GetProperty("seat").GetInt32(), e.GetProperty("id").GetInt32())).ToList();
        Assert.Equal([(0, S(h, 0).Me), (1, S(h, 1).Me)], ids);
        h.Tick(Dance.RevealTicks);
        Assert.Equal(Dance.PhaseOver, G(h).Phase);
        Assert.NotEqual(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
    }

    [Fact]
    public void A_figure_press_sends_no_view_and_round_counts_stay_hidden_until_the_reveal()
    {
        var h = Table(3, seed: 77);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id, Me(h, 2).Id);
        ToCall(h);
        InRing(Me(h, 0));
        TickTo(h, G(h).Beat - 1);
        var views = h.Outbox.OfType<RoomViews>().Count();
        Assert.True(h.Act(0, "fig", new { f = G(h).CallFig }).Ok);
        h.Tick();
        // натиснув — вид нікому не полетів: «хтось тиснув саме зараз» не мусить бути видно
        Assert.Equal(views, h.Outbox.OfType<RoomViews>().Count());
        ToJudge(h);
        Assert.True(h.Outbox.OfType<RoomViews>().Count() > views, "на суд вид летить усім однаково");
        // очки й лічильники — ще сховані: «+1 Олі» в мить, коли над кимось «?», назвав би гравців
        foreach (int? seat in new int?[] { null, 1, 2 })
        {
            var seats = h.View(seat).GetProperty("seats").EnumerateArray().ToList();
            Assert.All(seats, s => Assert.Equal(0, s.GetProperty("total").GetInt32()));
            Assert.All(seats, s => Assert.Equal(JsonValueKind.Null, s.GetProperty("good").ValueKind));
            Assert.All(seats, s => Assert.Equal(JsonValueKind.Null, s.GetProperty("bad").ValueKind));
        }
        Assert.Equal(1, h.View(0).GetProperty("me").GetProperty("good").GetInt32());
        while (G(h).Phase == Dance.PhaseGo) h.Tick();
        var open = h.View(null).GetProperty("seats").EnumerateArray().Single(s => s.GetProperty("seat").GetInt32() == 0);
        Assert.Equal(S(h, 0).Good, open.GetProperty("good").GetInt32());
        Assert.Equal(S(h, 0).Total, open.GetProperty("total").GetInt32());
    }

    /// <summary>
    /// Людина за клавіатурою: тримає стрілку 5–41 тик, відпускає на 1–31, зрідка задумується на 4–10 с. На виклик
    /// тисне фігуру з реакцією й мережею (−3…+10 тиків від такту), інколи зарано (−5…−4), інколи спізнюється, плутає
    /// чи й зовсім проґавлює.
    /// </summary>
    sealed class DanceHuman(Random rng)
    {
        int _left, _dir = -1;
        public int PressAt = int.MinValue, PressFig = -1;

        public int Walk()
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

        public void Hear(int fig, int beat)
        {
            var r = rng.Next(100);
            PressFig = fig;
            PressAt = r switch
            {
                < 80 => beat + rng.Next(-3, 11),
                < 88 => beat + rng.Next(-5, -3),
                < 94 => beat + rng.Next(11, 26),
                < 97 => beat + rng.Next(-3, 11),
                _ => int.MinValue,
            };
            if (r is >= 94 and < 97) PressFig = (fig + 1 + rng.Next(3)) % 4;
        }
    }

    /// <summary>Що видно в кадрах про одну сторону: де стоять, скільки, якою смугою йдуть, коли й як танцюють.</summary>
    sealed class DanceTrace
    {
        public readonly HashSet<(int, int)> Spots = [];
        public long Standing, StandingOff;
        public readonly Dictionary<int, int> Runs = [];
        public int RunCount;
        public readonly HashSet<(int, int)> Lanes = [];
        public long Moving, MovingOff;
        /// <summary>Перший кадр фігури відносно такту й стан (вціляв/ні) — як бачить глядач.</summary>
        public readonly HashSet<(int, int)> Poses = [];
        /// <summary>Скільки кадрів тривала фігура.</summary>
        public readonly HashSet<int> PoseLens = [];
        /// <summary>Крок і стан за тик.</summary>
        public readonly HashSet<(int, int, int)> Steps = [];

        public static int RunBin(int len) => len switch { <= 3 => 0, <= 7 => 1, <= 11 => 2, <= 24 => 3, <= 50 => 4, <= 100 => 5, <= 200 => 6, _ => 7 };
        public static bool Off(int r) => Math.Abs(r - 16) > 8;
        public int Share(int from, int to) => Runs.Where(p => p.Key >= from && p.Key <= to).Sum(p => p.Value) * 1000 / Math.Max(1, RunCount);
    }

    /// <summary>
    /// Вечорниці на голому ядрі: 4 «людини» й 40 ботів, музики кличуть фігуру кожні 4–8 с. Порядок тика — як у грі:
    /// натиск «між тиками», годинники, мозок ботів, крок, суд. Для кожного пишемо все, що видно в кадрі.
    /// </summary>
    static (DanceTrace Players, DanceTrace Bots) Observe(int seeds, int ticks = 6000)
    {
        var players = new DanceTrace();
        var bots = new DanceTrace();
        for (var seed = 1; seed <= seeds; seed++)
        {
            var core = new DanceCore(new Random(seed));
            var music = new Random(seed + 100);
            core.Deal([0, 1, 2, 3], 40);
            var humans = Enumerable.Range(0, 4).Select(i => new DanceHuman(new Random(seed * 10 + i))).ToArray();
            var n = core.N;
            var px = core.V.Select(v => v.X).ToArray();
            var py = core.V.Select(v => v.Y).ToArray();
            var ps = core.V.Select(v => v.State).ToArray();
            var run = new int[n];
            var poseLen = new int[n];
            int beat = 120, fig = -1;
            for (var t = 1; t <= ticks; t++)
            {
                if (t == beat - 30)
                {
                    fig = music.Next(4);
                    core.T = t - 1;
                    core.Announce(fig, beat);
                    foreach (var hm in humans) hm.Hear(fig, beat);
                }
                foreach (var v in core.V)
                {
                    if (v.Owner < 0) continue;
                    v.Want = humans[v.Owner].Walk();
                    if (humans[v.Owner].PressAt == t) core.Perform(v, humans[v.Owner].PressFig, t, true);
                }
                core.T = t;
                core.TimersAll();
                core.ThinkAll();
                core.StepAll();
                if (t == beat + DanceCore.Late + 1) core.Judge();
                if (t == beat + DanceCore.LateMax) { core.EndCall(); beat += 10 * music.Next(13, 18); }
                foreach (var v in core.V)
                {
                    var side = v.Owner >= 0 ? players : bots;
                    var s = v.State;
                    side.Steps.Add((v.X - px[v.Id], v.Y - py[v.Id], s));
                    var posing = s is >= 5 and <= 12;
                    // перший кадр фігури: коли відносно такту, вціляв чи ні, та фігура чи інша
                    if (posing && !(ps[v.Id] is >= 5 and <= 12)) side.Poses.Add((t - core.CallBeat, (s >= 9 ? 2 : 0) + ((s - 5) % 4 == fig ? 1 : 0)));
                    if (posing) poseLen[v.Id]++;
                    else if (poseLen[v.Id] > 0) { side.PoseLens.Add(poseLen[v.Id]); poseLen[v.Id] = 0; }
                    int rx = v.X % 32, ry = v.Y % 32;
                    var still = s is 0 or 13 && v.X == px[v.Id] && v.Y == py[v.Id];
                    if (still)
                    {
                        side.Standing++;
                        if (DanceTrace.Off(rx) || DanceTrace.Off(ry)) side.StandingOff++;
                        side.Spots.Add((rx / 4, ry / 4));
                        run[v.Id]++;
                    }
                    else if (!posing)
                    {
                        if (run[v.Id] > 0 && t > 200)
                        {
                            var bin = DanceTrace.RunBin(run[v.Id]);
                            side.Runs[bin] = side.Runs.GetValueOrDefault(bin) + 1;
                            side.RunCount++;
                        }
                        run[v.Id] = 0;
                        if (v.Moving)
                        {
                            var across = v.Dir is 0 or 2 ? ry : rx;
                            side.Moving++;
                            if (DanceTrace.Off(across)) side.MovingOff++;
                            side.Lanes.Add((v.Dir & 1, across / 4));
                        }
                    }
                    px[v.Id] = v.X;
                    py[v.Id] = v.Y;
                    ps[v.Id] = s;
                }
            }
        }
        return (players, bots);
    }

    static readonly Lazy<(DanceTrace Players, DanceTrace Bots)> Seen = new(() => Observe(3));

    [Fact]
    public void Players_pose_on_the_same_ticks_for_the_same_frames_as_bots()
    {
        var (players, bots) = Seen.Value;
        output.WriteLine($"фігури: гравців {players.Poses.Count} різних, ботів {bots.Poses.Count}; тривалість {string.Join(",", players.PoseLens)} / {string.Join(",", bots.PoseLens)}");
        output.WriteLine("лише в гравців: " + string.Join(" ", players.Poses.Except(bots.Poses).Order()));
        Assert.Subset(bots.Poses, players.Poses);
        Assert.Subset(bots.PoseLens, players.PoseLens);
    }

    [Fact]
    public void Per_tick_steps_and_states_of_players_are_a_subset_of_those_of_bots()
    {
        var (players, bots) = Seen.Value;
        Assert.Subset(bots.Steps, players.Steps);
    }

    [Fact]
    public void Players_stand_and_walk_where_bots_stand_and_walk()
    {
        var (players, bots) = Seen.Value;
        output.WriteLine($"стоять поза ±8: гравці {players.StandingOff * 100 / players.Standing}%, боти {bots.StandingOff * 100 / bots.Standing}%; ідуть поза смугою ±8: {players.MovingOff * 100 / players.Moving}% / {bots.MovingOff * 100 / bots.Moving}%");
        Assert.Subset(bots.Spots, players.Spots);
        Assert.Subset(bots.Lanes, players.Lanes);
        Assert.True(bots.StandingOff * 2 >= players.StandingOff * bots.Standing / players.Standing);
        Assert.True(bots.MovingOff * 2 >= players.MovingOff * bots.Moving / players.Moving);
    }

    [Fact]
    public void Players_stand_as_long_or_as_short_as_bots_do()
    {
        var (players, bots) = Seen.Value;
        string Show(DanceTrace s) => string.Join(" ", Enumerable.Range(0, 8).Select(b => $"{b}:{s.Share(b, b) / 10.0:F1}%"));
        output.WriteLine($"серії стояння, гравці: {Show(players)}");
        output.WriteLine($"серії стояння, боти:   {Show(bots)}");
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
        var h = Table(2, seed: 79);
        var me = Me(h, 0);
        Put(me, 12, 5);
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
        ToCall(h);
        TickTo(h, G(h).Beat - 1);
        Assert.True(h.Act(0, "fig", new { f = G(h).CallFig }).Ok, h.Reply.Message);    // 1–4 / J K L I / Ⓐ Ⓧ LB RB
        TickTo(h, G(h).Beat + DanceCore.LateMax);
        Put(me, 12, 5, dir: 0);
        Put(bot, 12, 5, ox: 20);
        Assert.True(h.Act(0, "slap", new { }).Ok, h.Reply.Message);                   // пробіл / RT: хто перед тобою
        h.Tick(Dance.SlapCoolTicks);
        bot.Offended = 0;
        Assert.True(h.Act(0, "slap", new { id = bot.Id }).Ok, h.Reply.Message);        // клік чи тап по танцюристу
        var before = Views.Text(h.View(0));
        foreach (var (action, payload) in new (string, object?)[] { ("move", new { dir = "up" }), ("move", new { d = 1 }), ("jump", null), ("slap", "x"), ("fig", new { f = 9 }), ("fig", new { fig = 1 }) })
            Assert.False(h.Act(0, action, payload).Ok);
        Assert.Equal(before, Views.Text(h.View(0)));
    }

    static List<string> Replay(int seed)
    {
        var h = Table(3, seed: seed);
        var frames = new List<string>();
        for (var t = 0; t < 900; t++)
        {
            if (t % 11 == 0) h.Input(t % 3, "move", new { dir = t % 5 - 1 });
            if (t % 7 == 0) h.Act(t % 3, "fig", new { f = t % 4 });
            if (t % 97 == 0) h.Act(t % 3, "slap", new { });
            h.Tick();
            frames.Add(Views.Text(((RoomFrame)h.Outbox.Last(o => o is RoomFrame)).Frame));
        }
        frames.Add(Views.Text(h.View(0)));
        return frames;
    }

    [Fact]
    public void Same_seed_and_same_inputs_give_byte_identical_frames()
    {
        var a = Replay(81);
        Assert.Equal(a, Replay(81));
        Assert.NotEqual(a, Replay(82));
    }

    [Fact]
    public void Views_json_matches_the_spec_shape()
    {
        var h = Table(2, seed: 83);
        Go(h);
        var v = h.View(0);
        foreach (var key in new[] { "phase", "round", "of", "left", "t", "width", "height", "cell", "n", "map", "circle", "need", "looks", "names", "v", "m", "seats", "dead", "me", "reveal", "result", "turn" })
            Assert.True(Views.Has(v, key), key);
        var n = v.GetProperty("n").GetInt32();
        Assert.Equal(30, v.GetProperty("width").GetInt32());
        Assert.Equal(20, v.GetProperty("map").GetArrayLength());
        Assert.Equal(5 * n, v.GetProperty("looks").GetArrayLength());
        Assert.Equal(n, v.GetProperty("names").GetArrayLength());
        Assert.Equal(4 * n, v.GetProperty("v").GetArrayLength());
        Assert.Equal(4, v.GetProperty("m").GetArrayLength());
        Assert.Equal(Dance.RibbonStreak, v.GetProperty("need").GetInt32());
        Assert.Equal(DanceMap.CircleR, v.GetProperty("circle").GetProperty("r").GetInt32());
        var me = v.GetProperty("me");
        foreach (var key in new[] { "id", "alive", "streak", "good", "bad", "circle", "danced", "last", "slapCool", "stun" }) Assert.True(Views.Has(me, key), key);
        var seat = v.GetProperty("seats")[0];
        foreach (var key in new[] { "seat", "nick", "alive", "out", "good", "bad", "total" }) Assert.True(Views.Has(seat, key), key);
        // дівчата й парубки: імена за статтю вигляду
        var looks = v.GetProperty("looks").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        var names = v.GetProperty("names").EnumerateArray().Select(e => e.GetString()!).ToArray();
        for (var i = 0; i < n; i++) Assert.Contains(names[i], looks[i * 5] == 0 ? DanceMap.Girls : DanceMap.Boys);
        Assert.Equal(n, names.Distinct().Count());
        output.WriteLine($"вид на {n} танцюристів: {Views.Text(G(h).View(0)).Length} Б");
    }

    [Fact]
    public void Catalog_lists_dance_as_live_by_host_hidden_tick_forty_with_a_module()
    {
        var info = new Dance().Info;
        Assert.Equal(("dance", "Вечорниці", "вечорниці"), (info.Id, info.Title, info.Accusative));
        Assert.Equal(GameGroup.Live, info.Group);
        Assert.Equal((1, 8), (info.MinPlayers, info.MaxPlayers));   // самому — з 🤖 ботами (без них CanStart не пустить)
        Assert.Equal(40, info.TickMs);
        Assert.Equal(StartMode.ByHost, info.Start);
        Assert.True(info.Hidden);
        Assert.Equal(ScoreOrder.HigherIsBetter, info.Score);
        Assert.Equal(["rounds", "crowd", "botlvl"], info.Options!.Select(o => o.Key));
        var root = FindRoot();
        Assert.True(File.Exists(Path.Combine(root, "web", "games", "dance.js")));
        Assert.True(File.Exists(Path.Combine(root, "web", "games", "dance.css")));
        Assert.Equal("червоний", new Dance().SeatName(7));
    }

    [Fact]
    public void The_module_sends_only_actions_and_payloads_the_server_reads()
    {
        var js = File.ReadAllText(Path.Combine(FindRoot(), "web", "games", "dance.js"));
        var sent = System.Text.RegularExpressions.Regex.Matches(js, @"ctx\.(?:act|input)\('(\w+)'")
            .Select(m => m.Groups[1].Value).Distinct().Order().ToArray();
        Assert.Equal(["fig", "move", "slap"], sent);
        Assert.Contains("ctx.input('move', { dir: d })", js);
        Assert.Contains("ctx.input('move', { dir: -1 })", js);
        Assert.Contains("ctx.act('fig', { f })", js);
        Assert.Contains("ctx.act('slap', id == null ? {} : { id })", js);
        // Ⓑ — вихід зі столу, Ⓨ — підказки пада: модуль їх не забирає
        Assert.DoesNotContain("btn === 'b'", js);
        Assert.DoesNotContain("btn === 'y'", js);
    }

    [Fact]
    public void Module_constants_match_the_server_rules()
    {
        var js = File.ReadAllText(Path.Combine(FindRoot(), "web", "games", "dance.js"));
        int Const(string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(js, @"\b" + name + @"\s*=\s*(\d+)");
            Assert.True(m.Success, name);
            return int.Parse(m.Groups[1].Value);
        }
        Assert.Equal(Dance.TickMs, Const("TICK_MS"));
        Assert.Equal(DanceMap.Cell, Const("CELL"));
        Assert.Equal(DanceMap.WorldW, Const("WW"));
        Assert.Equal(DanceMap.WorldH, Const("WH"));
        Assert.Equal(DanceCore.SlapRange, Const("SLAP_RANGE"));
        Assert.Equal(DanceCore.SlapRangeMax, Const("SLAP_MAX"));
        Assert.Equal(DanceCore.SlapCos2Milli, Const("SLAP_CONE"));
        Assert.Equal(DanceCore.Early, Const("EARLY"));
        Assert.Equal(DanceCore.Late, Const("LATE"));
        Assert.Equal(DanceCore.LateMax, Const("LATE_MAX"));
        Assert.Equal(Dance.SlapCoolTicks * Dance.TickMs, Const("SLAP_COOL_MS"));
        Assert.Equal(Dance.StunTicks * Dance.TickMs, Const("STUN_MS"));
    }

    static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "web", "games"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("не знайшов корінь репозиторію");
    }

    [Fact]
    public void Achievements_ribbon_and_slap_are_requested_exactly_when_earned()
    {
        Assert.NotNull(AchievementCatalog.Get("dance-ribbon"));
        Assert.NotNull(AchievementCatalog.Get("dance-slap"));
        // Оля першим же ляпасом виводить Ганну; Петро тихо витанцьовує стрічку — обоє по ачівці
        var h = Table(3, seed: 85);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id, Me(h, 2).Id);
        Put(Me(h, 0), 12, 5, dir: 0);
        Put(Me(h, 2), 12, 5, ox: 20);
        Assert.True(h.Act(0, "slap", new { }).Ok, h.Reply.Message);
        for (var k = 0; k < Dance.RibbonStreak; k++)
        {
            ToCall(h);
            InRing(Me(h, 1));
            Assert.True(PressAt(h, 1, 1).Ok, h.Reply.Message);
            ToJudge(h);
        }
        Assert.Equal(Dance.PhaseReveal, G(h).Phase);
        var awards = h.Awards.Select(a => (a.Nick, a.Reason, a.Shards)).ToList();
        Assert.Contains(("Оля", "ach:dance-slap", 0), awards);
        Assert.Contains(("Петро", "ach:dance-ribbon", 0), awards);
        Assert.Equal(2, awards.Count);

        // ляснув бота першим — ачівки нема; хто бився — стрічка вже не «тиха»
        var g = Table(2, seed: 87);
        Go(g);
        var bot = Core(g).V.First(v => v.Owner < 0);
        Park(g, Me(g, 0).Id, bot.Id, Me(g, 1).Id);
        Put(Me(g, 0), 12, 5, dir: 0);
        Put(bot, 12, 5, ox: 20);
        Assert.True(g.Act(0, "slap", new { }).Ok, g.Reply.Message);
        g.Tick(Dance.StunTicks);
        for (var k = 0; k < Dance.RibbonStreak; k++)
        {
            ToCall(g);
            InRing(Me(g, 0));
            Assert.True(PressAt(g, 0, 1).Ok, g.Reply.Message);
            ToJudge(g);
        }
        Assert.Equal("ribbon", g.View(null).GetProperty("reveal").GetProperty("why").GetString());
        Assert.Empty(g.Awards);
    }

    [Fact]
    public void Players_start_apart_and_nobody_shares_a_name()
    {
        for (var seed = 1; seed <= 30; seed++)
        {
            var core = new DanceCore(new Random(seed));
            core.Deal([0, 1, 2, 3, 4, 5, 6, 7], 48);
            var players = core.V.Where(v => v.Owner >= 0).ToArray();
            for (var i = 0; i < players.Length; i++)
                for (var j = i + 1; j < players.Length; j++)
                {
                    int dx = Math.Abs(players[i].X / 32 - players[j].X / 32), dy = Math.Abs(players[i].Y / 32 - players[j].Y / 32);
                    Assert.True(Math.Max(dx, dy) >= 3, $"сід {seed}");
                }
            Assert.Equal(56, core.NamesNow().Distinct().Count());
            Assert.All(core.V, v => Assert.True(DanceMap.BoxFits(v.X, v.Y)));
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
        for (var attempt = 0; attempt < 3 && best >= 1000; attempt++)
        {
            var h = Table(8, seed: 90 + attempt, options: new { crowd = "big", rounds = "5" });
            var rng = new Random(attempt);
            var sw = Stopwatch.StartNew();
            for (var t = 0; t < 3000 && h.Room.Status == RoomStatus.Playing; t++)
            {
                if (t % 10 == 0)
                    for (var s = 0; s < 8; s++) h.Input(s, "move", new { dir = rng.Next(-1, 4) });
                if (t % 50 == 25)
                    for (var s = 0; s < 8; s++) h.Act(s, "fig", new { f = rng.Next(4) });
                if (t % 100 == 50)
                    for (var s = 0; s < 8; s += 2) h.Act(s, "slap", new { });
                h.Tick();
            }
            best = Math.Min(best, sw.ElapsedMilliseconds);
        }
        var pure = PureTickMicros();
        output.WriteLine($"3000 тиків через кімнату: {best} мс; чистий Tick()+Frame(): {pure:F1} мкс");
        Assert.True(best < 1000, $"{best} мс");
        Assert.True(pure < 250, $"{pure} мкс на тик");
    }

    static double PureTickMicros()
    {
        var bestUs = double.MaxValue;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var h = Table(8, seed: 95 + attempt, options: new { crowd = "big", rounds = "5" });
            var game = G(h);
            Go(h);
            var rng = new Random(attempt);
            for (var i = 0; i < 200; i++) game.Tick();
            var sw = Stopwatch.StartNew();
            var n = 0;
            for (var t = 0; t < 3000 && game.Phase != Dance.PhaseOver; t++)
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
    public void A_frame_with_fifty_six_dancers_serialises_under_1200_bytes()
    {
        var h = Table(8, seed: 99, options: new { crowd = "big" });
        Go(h);
        Assert.Equal(56, Core(h).N);
        var max = 0;
        for (var t = 0; t < 600; t++)
        {
            h.Tick();
            max = Math.Max(max, FrameText(h).Length);
        }
        output.WriteLine($"кадр на 56 танцюристів: до {max} Б");
        Assert.True(max < 1200, $"{max} Б");
    }
}
