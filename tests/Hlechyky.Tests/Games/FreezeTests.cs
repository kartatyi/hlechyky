using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Замри!»: крок, Бабу й мозок ботів перевіряємо на голому <see cref="FreezeCore"/> (там селянина можна поставити рівно
/// туди, куди треба, а Бабу — у потрібний стан), а раунди, очки, дії й приховане — через справжню кімнату
/// (<see cref="RoomHarness"/>). Клас у серійній колекції через перф-тест.
/// </summary>
[Collection(SerialPerf.Name)]
public class FreezeTests(ITestOutputHelper output)
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марта", "Юрко", "Соня", "Богдан"];

    // ---------- підмостки ----------

    static RoomHarness Table(int players = 2, int seed = 42, object? options = null)
    {
        var h = new RoomHarness("freeze", options, seed: seed);
        foreach (var nick in Nicks.Take(players)) h.Join(nick);
        h.Start();
        return h;
    }

    static Freeze G(RoomHarness h) => (Freeze)h.Room.Game;
    static FreezeCore Core(RoomHarness h) => G(h).CoreForTests;
    static FreezeSeat S(RoomHarness h, int seat) => G(h).SeatForTests(seat);
    static FreezeVillager Me(RoomHarness h, int seat) => Core(h).V[S(h, seat).Me];

    static void Go(RoomHarness h)
    {
        for (var i = 0; i < 200 && G(h).Phase != Freeze.PhaseGo; i++) h.Tick();
        Assert.Equal(Freeze.PhaseGo, G(h).Phase);
    }

    /// <summary>Баба співає «вічно» — щоб тест сам вирішував, коли вона обертається.</summary>
    static void Sing(RoomHarness h) => Core(h).SetBabaForTests(FreezeCore.Sing, 1_000_000);

    static FreezeVillager Put(FreezeVillager v, int x, int y, int dir = 0)
    {
        v.X = x;
        v.Y = y;
        v.Dir = dir;
        v.Want = -1;
        v.Moving = false;
        v.Blocked = false;
        return v;
    }

    /// <summary>Усі боти, крім переданих, відведені до краю лугу й «отетеріли» навіки: не думають, не ходять, не штурхають.</summary>
    static void Park(RoomHarness h, params int[] keep)
    {
        var i = 0;
        foreach (var v in Core(h).V)
        {
            if (v.Owner >= 0 || Array.IndexOf(keep, v.Id) >= 0) continue;
            Put(v, 100 + i % 24 * 40, i / 24 % 2 == 0 ? FreezeCore.MaxY : FreezeCore.MinY);
            v.Dazed = 1_000_000;
            i++;
        }
    }

    /// <summary>Голе ядро: <paramref name="players"/> місць і боти; Баба вже співає.</summary>
    static FreezeCore Bare(int players = 0, int bots = 40, int seed = 1)
    {
        var core = new FreezeCore(new Random(seed));
        core.Deal([.. Enumerable.Range(0, players)], bots);
        core.Open();
        return core;
    }

    static string FrameText(RoomHarness h) => Views.Text(G(h).Frame());

    static JsonElement LastFrame(RoomHarness h) => Views.Json(((RoomFrame)h.Outbox.Last(o => o is RoomFrame)).Frame);

    static List<int[]> Events(RoomHarness h, int since) =>
        [.. h.Outbox.Skip(since).OfType<RoomFrame>().SelectMany(f => Views.Json(f.Frame).GetProperty("ev").EnumerateArray()
            .Select(e => e.EnumerateArray().Select(x => x.GetInt32()).ToArray()))];

    // =============================================================================================
    // Луг і крок — один на всіх
    // =============================================================================================

    [Fact]
    public void The_meadow_runs_from_the_fence_to_the_house_and_bots_stop_short_of_the_jug()
    {
        Assert.True(FreezeCore.MinX <= FreezeCore.StartMinX && FreezeCore.StartMaxX < FreezeCore.FinishX);
        Assert.True(FreezeCore.FinishX < FreezeCore.MaxX && FreezeCore.MaxX <= FreezeCore.WorldW);
        Assert.True(FreezeCore.MinY < FreezeCore.MaxY && FreezeCore.MaxY <= FreezeCore.WorldH);
        Assert.True(FreezeCore.GoalMax <= FreezeCore.BotMaxX && FreezeCore.BotMaxX < FreezeCore.FinishX);
        // від тину до глека — ≈ 20 с чистої ходи
        var walk = (FreezeCore.FinishX - FreezeCore.StartMaxX) / FreezeCore.Speed * Freeze.TickMs;
        Assert.InRange(walk, 18_000, 24_000);
    }

    [Fact]
    public void A_bot_and_a_player_given_the_same_want_walk_the_same_path()
    {
        var core = Bare(players: 1, bots: 3);
        var p = core.V.Single(v => v.Owner == 0);
        var b = core.V.First(v => v.Owner < 0);
        Put(p, 200, 120);
        Put(b, 200, 120);
        var rng = new Random(5);
        for (var t = 0; t < 400; t++)
        {
            var want = rng.Next(-1, 4);
            p.Want = b.Want = want;
            FreezeCore.Step(p);
            FreezeCore.Step(b);
            Assert.Equal((p.X, p.Y, p.Dir, p.State, p.Blocked), (b.X, b.Y, b.Dir, b.State, b.Blocked));
        }
    }

    [Fact]
    public void Everyone_moves_exactly_two_units_along_one_axis_or_is_sent_back_to_the_fence()
    {
        var h = Table(4, seed: 7);
        var core = Core(h);
        var rng = new Random(1);
        var was = core.V.Select(v => (v.X, v.Y)).ToArray();
        var jumps = 0;
        for (var t = 0; t < 2500 && G(h).Phase is Freeze.PhaseStart or Freeze.PhaseGo; t++)
        {
            if (t % 7 == 0)
                for (var s = 0; s < 4; s++) h.Input(s, "move", new { dir = rng.Next(-1, 4) });
            h.Tick();
            foreach (var v in core.V)
            {
                int dx = v.X - was[v.Id].X, dy = v.Y - was[v.Id].Y;
                var ok = (dx, dy) is (0, 0) or (2, 0) or (-2, 0) or (0, 2) or (0, -2);
                if (!ok)
                {
                    // повернули на старт: x — біля тину, висота та сама
                    Assert.InRange(v.X, FreezeCore.StartMinX, FreezeCore.StartMaxX);
                    Assert.Equal(0, dy);
                    jumps++;
                }
                Assert.InRange(v.X, FreezeCore.MinX, FreezeCore.MaxX);
                Assert.InRange(v.Y, FreezeCore.MinY, FreezeCore.MaxY);
                was[v.Id] = (v.X, v.Y);
            }
        }
        output.WriteLine($"повернень на старт за партію: {jumps}");
        Assert.True(jumps > 0);
    }

    [Fact]
    public void Facing_turns_even_when_the_step_is_blocked_by_the_edge_of_the_meadow()
    {
        var core = Bare(players: 1, bots: 1);
        var p = Put(core.V.Single(v => v.Owner == 0), 300, FreezeCore.MinY, dir: 0);
        p.Want = 3;
        FreezeCore.Step(p);
        Assert.Equal(3, p.Dir);
        Assert.Equal(FreezeCore.MinY, p.Y);
        Assert.False(p.Moving);
        Assert.True(p.Blocked);
    }

    [Fact]
    public void Lying_dazed_and_caught_villagers_do_not_move_whatever_they_want()
    {
        var core = Bare(players: 1, bots: 1);
        var p = Put(core.V.Single(v => v.Owner == 0), 300, 200);
        foreach (var set in new Action<FreezeVillager>[] { v => v.Down = 5, v => v.Dazed = 5, v => v.Caught = 5 })
        {
            p.Down = p.Dazed = p.Caught = 0;
            set(p);
            p.Want = 0;
            FreezeCore.Step(p);
            Assert.Equal((300, 200), (p.X, p.Y));
            Assert.False(p.Moving);
        }
        Assert.Equal(3, p.State);
        p.Caught = 0;
        p.Dazed = 1;
        Assert.Equal(4, p.State);
        p.Dazed = 0;
        p.Down = 1;
        Assert.Equal(2, p.State);
    }

    // =============================================================================================
    // Баба Параска
    // =============================================================================================

    [Fact]
    public void Baba_sings_glances_turns_and_watches_for_random_times_in_range()
    {
        var runs = new Dictionary<int, List<int>> { [0] = [], [1] = [], [2] = [], [3] = [] };
        var moves = new HashSet<(int, int)>();
        for (var seed = 1; seed <= 3; seed++)
        {
            var core = Bare(bots: 10, seed: seed);
            int state = core.Baba, len = 0;
            var first = true;
            for (var t = 0; t < 40_000; t++)
            {
                core.Ev.Clear();
                core.TickGo();
                if (core.Baba == state) { len++; continue; }
                if (!first) runs[state].Add(len);
                first = false;
                moves.Add((state, core.Baba));
                state = core.Baba;
                len = 1;
            }
        }
        output.WriteLine($"співає {runs[0].Min()}–{runs[0].Max()}, озирається {runs[3].Min()}–{runs[3].Max()}, обертається {runs[1].Min()}–{runs[1].Max()}, дивиться {runs[2].Min()}–{runs[2].Max()}; переходи: {string.Join(" ", moves)}");
        Assert.All(runs[0], n => Assert.InRange(n, FreezeCore.ShortSingMin, FreezeCore.SingMax));
        Assert.All(runs[3], n => Assert.InRange(n, FreezeCore.GlanceMin, FreezeCore.GlanceMax));
        Assert.All(runs[1], n => Assert.Equal(FreezeCore.Grace, n));
        Assert.All(runs[2], n => Assert.InRange(n, FreezeCore.WatchMin, FreezeCore.WatchMax));
        // співає то коротко, то довго; озирнулась — і обертається, і співає далі (обманка)
        Assert.True(runs[0].Distinct().Count() > 30);
        Assert.Equal(
            new HashSet<(int, int)> { (0, 1), (0, 3), (3, 1), (3, 0), (1, 2), (2, 0) },
            moves);
        var glances = runs[3].Count;
        Assert.InRange(glances * 100 / (runs[1].Count + 1), 20, 60);
    }

    [Fact]
    public void Grace_lets_you_walk_eleven_ticks_after_the_turn_and_the_next_step_is_caught()
    {
        var h = Table(2, seed: 11);
        Go(h);
        Park(h);
        Put(Me(h, 0), 300, 150);
        Put(Me(h, 1), 300, 250);
        for (var i = 0; i < 3000 && Core(h).Baba != FreezeCore.Turn; i++) h.Tick();
        // цей тик Баба обернулась і вже крикнула — кадр пішов; хто йде, має ще 10 тиків
        Assert.Equal(FreezeCore.Turn, Core(h).Baba);
        Assert.Equal(FreezeCore.Grace, Core(h).BabaLeft);
        Assert.Equal(1, LastFrame(h).GetProperty("b").GetInt32());
        h.Input(0, "move", new { dir = 0 });
        h.Input(1, "move", new { dir = 0 });
        for (var i = 0; i < FreezeCore.Grace - 1; i++)
        {
            h.Tick();
            Assert.Equal(FreezeCore.Turn, Core(h).Baba);
            Assert.True(Me(h, 0).Moving && Me(h, 1).Moving);
        }
        var mark = h.Outbox.Count;
        h.Input(0, "move", new { dir = -1 });              // Оля відпустила вчасно, Петро — ні
        h.Tick();
        Assert.Equal(FreezeCore.Watch, Core(h).Baba);
        Assert.Equal(0, Me(h, 0).Caught);
        Assert.Equal(FreezeCore.CaughtTicks, Me(h, 1).Caught);
        Assert.Contains(Events(h, mark), e => e is [2, var id] && id == Me(h, 1).Id);
        Assert.Equal(3, LastFrame(h).GetProperty("v")[Me(h, 1).Id * 4 + 3].GetInt32());
    }

    [Fact]
    public void A_caught_villager_stands_under_babas_finger_until_she_turns_away_then_goes_back_to_the_fence()
    {
        var h = Table(2, seed: 12);
        Go(h);
        Park(h);
        var him = Put(Me(h, 1), 600, 222);
        Core(h).SetBabaForTests(FreezeCore.Watch, 90);
        var mark = h.Outbox.Count;
        h.Input(1, "move", new { dir = 0 });
        h.Tick();
        Assert.Equal(FreezeCore.CaughtTicks, him.Caught);
        var at = (him.X, him.Y);
        h.Input(1, "move", new { dir = -1 });
        var ticks = 0;
        while (Core(h).Baba == FreezeCore.Watch)
        {
            h.Tick();
            ticks++;
            if (Core(h).Baba == FreezeCore.Watch) Assert.Equal(at, (him.X, him.Y));
            Assert.True(ticks < 200);
        }
        Assert.True(ticks > FreezeCore.CaughtTicks);        // дивилась довше за секунду — і він стояв до кінця
        for (var i = 0; i < 3 && him.Caught > 0; i++) h.Tick();
        Assert.Equal(0, him.Caught);
        Assert.InRange(him.X, FreezeCore.StartMinX, FreezeCore.StartMaxX);
        Assert.Equal(222, him.Y);
        var ev = Events(h, mark);
        Assert.Contains(ev, e => e is [2, var id] && id == him.Id);
        Assert.Contains(ev, e => e is [3, var id] && id == him.Id);
        Assert.Equal(1, S(h, 1).Caught);
        Assert.Equal(1, h.View(1).GetProperty("me").GetProperty("caught").GetInt32());
    }

    [Fact]
    public void Bots_and_players_are_caught_and_sent_back_alike()
    {
        var core = Bare(players: 1, bots: 3, seed: 3);
        var p = Put(core.V.Single(v => v.Owner == 0), 500, 150);
        var b = Put(core.V.First(v => v.Owner < 0), 500, 250);
        foreach (var v in core.V.Where(v => v != p && v != b)) v.Dazed = 1_000_000;
        core.SetBabaForTests(FreezeCore.Watch, 30);
        p.Want = 0;
        b.Want = 0;
        b.StopIn = 5;                   // незграба: ще йде, хоч Баба вже дивиться
        core.Ev.Clear();
        core.TickGo();
        Assert.Equal(FreezeCore.CaughtTicks, p.Caught);
        Assert.Equal(FreezeCore.CaughtTicks, b.Caught);
        Assert.Equal(3, p.State);
        Assert.Equal(3, b.State);
        Assert.Equal([p.Id, b.Id], core.Ev.Where(e => e[0] == 2).Select(e => e[1]).Order());
        for (var t = 0; t < 200 && (p.Caught > 0 || b.Caught > 0); t++)
        {
            core.Ev.Clear();
            core.TickGo();
        }
        Assert.InRange(p.X, FreezeCore.StartMinX, FreezeCore.StartMaxX);
        Assert.InRange(b.X, FreezeCore.StartMinX, FreezeCore.StartMaxX);
        Assert.Equal((150, 250), (p.Y, b.Y));
    }

    [Fact]
    public void The_frame_counts_down_the_look_and_says_nothing_about_how_long_she_will_sing()
    {
        var h = Table(2, seed: 13);
        var prev = -1;
        var prevB = -1;
        var seenWatch = false;
        for (var t = 0; t < 3000 && G(h).Phase is Freeze.PhaseStart or Freeze.PhaseGo; t++)
        {
            h.Tick();
            var f = LastFrame(h);
            var ph = f.GetProperty("ph").GetString();
            int b = f.GetProperty("b").GetInt32(), bl = f.GetProperty("bl").GetInt32();
            if (ph == "start") { Assert.Equal(f.GetProperty("left").GetInt32(), bl); Assert.Equal(2, b); continue; }
            if (ph != "go") break;
            if (b is 0 or 3) Assert.Equal(0, bl);
            else
            {
                if (prevB is 1 or 2) Assert.Equal(prev - 1, bl);
                if (b == 2) seenWatch = true;
                Assert.True(bl >= 1);
            }
            if (prevB == 2 && b == 0) Assert.Equal(1, prev);
            prev = bl;
            prevB = b;
        }
        Assert.True(seenWatch);
    }

    // =============================================================================================
    // Боти
    // =============================================================================================

    /// <summary>
    /// Повороти Баби на голому ядрі: для кожного бота, що йшов у мить повороту, — на якому тику після повороту він
    /// зробив останній крок (0…10 — встиг) або що його впіймали (незграба).
    /// </summary>
    static (Dictionary<int, int> Last, int Moving, int Clumsy) TurnReactions(int seeds, int ticks = 20_000)
    {
        var last = new Dictionary<int, int>();
        int moving = 0, clumsy = 0;
        for (var seed = 1; seed <= seeds; seed++)
        {
            var core = Bare(bots: 40, seed: seed);
            var turnAt = -1;
            var watch = new Dictionary<int, int>();       // id → останній тик кроку
            var was = core.Baba;
            for (var t = 0; t < ticks; t++)
            {
                core.Ev.Clear();
                core.TickGo();
                if (core.Baba == FreezeCore.Turn && was != FreezeCore.Turn)
                {
                    turnAt = t;
                    watch.Clear();
                    foreach (var v in core.V.Where(v => v.Moving)) watch[v.Id] = t;
                    moving += watch.Count;
                }
                else if (turnAt >= 0 && watch.Count > 0)
                {
                    foreach (var e in core.Ev.Where(e => e[0] == 2 && watch.ContainsKey(e[1])))
                    {
                        clumsy++;
                        watch.Remove(e[1]);
                    }
                    foreach (var id in watch.Keys.ToList())
                    {
                        if (core.V[id].Moving) { watch[id] = t; continue; }
                        var k = watch[id] - turnAt;
                        last[k] = last.GetValueOrDefault(k) + 1;
                        watch.Remove(id);
                    }
                }
                was = core.Baba;
            }
        }
        return (last, moving, clumsy);
    }

    [Fact]
    public void Bots_stop_within_the_grace_like_people_and_a_few_clumsy_ones_get_caught()
    {
        var (last, moving, clumsy) = TurnReactions(3);
        output.WriteLine($"останній крок після повороту (тик: скільки): {string.Join(" ", last.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value}"))}; ішли {moving}, незграб {clumsy} ({clumsy * 1000 / Math.Max(1, moving)}‰)");
        Assert.All(last.Keys, k => Assert.InRange(k, 0, FreezeCore.Grace - 1));
        // людина з пінгом 20–90 мс і реакцією 200–300 мс робить останній крок на 5–10-му тику — у ботів це звична справа
        for (var k = 1; k <= FreezeCore.Grace - 1; k++) Assert.True(last.GetValueOrDefault(k) * 30 >= moving - clumsy, $"тик {k}: {last.GetValueOrDefault(k)}");
        Assert.InRange(clumsy * 1000 / moving, 15, 70);
    }

    [Fact]
    public void Bots_never_touch_the_jug_and_turn_back_under_the_house()
    {
        var core = Bare(bots: 40, seed: 4);
        var dir = core.V.Select(v => v.Dir).ToArray();
        var turned = 0;
        var maxX = 0;
        for (var t = 0; t < 30_000; t++)
        {
            core.Ev.Clear();
            core.TickGo();
            foreach (var v in core.V)
            {
                maxX = Math.Max(maxX, v.X);
                if (v.Moving && dir[v.Id] == 0 && v.Dir == 2 && v.X >= FreezeCore.GoalMin) turned++;
                if (v.Moving) dir[v.Id] = v.Dir;
            }
        }
        output.WriteLine($"найдалі бот: {maxX} (фініш {FreezeCore.FinishX}); розворотів під хатою: {turned}");
        Assert.True(maxX <= FreezeCore.BotMaxX);
        Assert.True(maxX >= FreezeCore.GoalMax - 40);
        Assert.True(turned > 40);
    }

    [Fact]
    public void Bots_walk_forward_rest_step_aside_and_step_back()
    {
        var core = Bare(bots: 30, seed: 5);
        var steps = new int[4];
        long still = 0, moving = 0;
        for (var t = 0; t < 6000; t++)
        {
            core.Ev.Clear();
            core.TickGo();
            foreach (var v in core.V)
            {
                if (v.Moving) { steps[v.Dir]++; moving++; }
                else if (core.Baba == FreezeCore.Sing && !v.Still) still++;
            }
        }
        output.WriteLine($"кроки →{steps[0]} ↓{steps[1]} ←{steps[2]} ↑{steps[3]}; стоять, поки Баба співає: {still * 100 / (still + moving)}%");
        Assert.All(steps, n => Assert.True(n > 100));
        Assert.True(steps[0] > steps[1] + steps[2] + steps[3]);
        Assert.InRange(still * 100 / (still + moving), 15, 60);
    }

    [Fact]
    public void Bots_push_now_and_then_and_a_bot_that_pushes_a_bot_is_dazed()
    {
        var pushes = 0;
        var seen = new HashSet<int>();
        for (var seed = 1; seed <= 3; seed++)
        {
            var core = Bare(bots: 40, seed: seed);
            for (var t = 0; t < 10_000; t++)
            {
                core.Ev.Clear();
                core.TickGo();
                foreach (var e in core.Ev.Where(e => e[0] == 1))
                {
                    pushes++;
                    Assert.Equal(0, e[3]);                         // бота не звалиш — отетерів сам
                    Assert.Equal(FreezeCore.DazeTicks, core.V[e[1]].Dazed);
                    Assert.Equal(4, core.V[e[1]].State);
                    Assert.True(FreezeCore.Dist2(core.V[e[1]], core.V[e[2]]) <= FreezeCore.PushRange * FreezeCore.PushRange);
                    seen.Add(e[1]);
                }
            }
        }
        output.WriteLine($"ботячих штурханів за 3 × 400 с: {pushes}");
        Assert.InRange(pushes, 6, 200);
        Assert.True(seen.Count > 3);
    }

    [Fact]
    public void Nobody_moves_while_everyone_looks_around_at_the_fence()
    {
        var h = Table(3, seed: 14);
        var at = Core(h).V.Select(v => (v.X, v.Y)).ToArray();
        for (var s = 0; s < 3; s++) h.Input(s, "move", new { dir = 0 });
        for (var i = 0; i < Freeze.StartTicks - 1; i++)
        {
            h.Tick();
            Assert.Equal(Freeze.PhaseStart, G(h).Phase);
            Assert.Equal(at, Core(h).V.Select(v => (v.X, v.Y)).ToArray());
        }
        Assert.All(Core(h).V, v => Assert.InRange(v.X, FreezeCore.StartMinX, FreezeCore.StartMaxX));
        // тримав стрілку з відліку (модуль підтверджує її раз на секунду) — рушає з першою нотою, як бот напоготові
        h.Input(0, "move", new { dir = 0 });
        h.Tick();
        Assert.Equal(Freeze.PhaseGo, G(h).Phase);
        h.Tick();
        Assert.True(Me(h, 0).X > at[Me(h, 0).Id].X);
    }

    // =============================================================================================
    // Раунд і партія
    // =============================================================================================

    [Fact]
    public void Match_starts_with_three_seconds_of_looking_around_then_baba_sings()
    {
        var h = Table(2, seed: 15);
        Assert.Equal(Freeze.PhaseStart, G(h).Phase);
        Assert.False(h.Act(0, "push", new { }).Ok);
        Assert.Equal("Зачекай, Баба ще не заспівала", h.Reply.Message);
        h.Tick(Freeze.StartTicks - 1);
        Assert.Equal(Freeze.PhaseStart, G(h).Phase);
        h.Tick();
        Assert.Equal(Freeze.PhaseGo, G(h).Phase);
        Assert.Equal(FreezeCore.Sing, Core(h).Baba);
        Assert.InRange(Core(h).BabaLeft, FreezeCore.FirstSingMin, FreezeCore.SingMax);
        Assert.Equal(0, LastFrame(h).GetProperty("b").GetInt32());
    }

    [Fact]
    public void Touching_the_jug_ends_the_round_at_once_with_plus_three()
    {
        var h = Table(2, seed: 16);
        Go(h);
        Park(h);
        Sing(h);
        Put(Me(h, 0), FreezeCore.FinishX - 1, 200);
        h.Input(0, "move", new { dir = 0 });
        h.Tick();
        Assert.Equal(Freeze.PhaseReveal, G(h).Phase);
        var r = h.View(null).GetProperty("reveal");
        Assert.Equal("jug", r.GetProperty("why").GetString());
        Assert.Equal([0], r.GetProperty("winners").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal((3, 0), (S(h, 0).Total, S(h, 1).Total));
        var row = r.GetProperty("rows").EnumerateArray().Single(x => x.GetProperty("seat").GetInt32() == 0);
        Assert.True(row.GetProperty("win").GetBoolean());
        Assert.Equal(3, row.GetProperty("pts").GetInt32());
    }

    [Fact]
    public void Two_players_touching_the_jug_in_one_tick_both_win_the_round()
    {
        var h = Table(3, seed: 17);
        Go(h);
        Park(h);
        Sing(h);
        Put(Me(h, 0), FreezeCore.FinishX - 2, 150);
        Put(Me(h, 1), FreezeCore.FinishX - 1, 250);
        h.Input(0, "move", new { dir = 0 });
        h.Input(1, "move", new { dir = 0 });
        h.Tick();
        Assert.Equal(Freeze.PhaseReveal, G(h).Phase);
        Assert.Equal((3, 3, 0), (S(h, 0).Total, S(h, 1).Total, S(h, 2).Total));
    }

    [Fact]
    public void Crossing_the_line_while_baba_watches_is_a_catch_not_a_win()
    {
        var h = Table(2, seed: 18);
        Go(h);
        Park(h);
        var me = Put(Me(h, 0), FreezeCore.FinishX - 1, 200);
        Core(h).SetBabaForTests(FreezeCore.Watch, 60);
        h.Input(0, "move", new { dir = 0 });
        h.Tick();
        Assert.Equal(Freeze.PhaseGo, G(h).Phase);
        Assert.Equal(FreezeCore.CaughtTicks, me.Caught);
        Assert.Equal(0, S(h, 0).Total);
        h.Tick(80);
        Assert.Equal(Freeze.PhaseGo, G(h).Phase);
        Assert.True(me.X < FreezeCore.FinishX - 400);
    }

    [Fact]
    public void Slipping_over_the_line_while_baba_shouts_wins_and_earns_both_achievements()
    {
        var h = Table(2, seed: 19);
        Go(h);
        Park(h);
        Put(Me(h, 0), FreezeCore.FinishX - 1, 200);
        Core(h).SetBabaForTests(FreezeCore.Turn, 5, 60);
        h.Input(0, "move", new { dir = 0 });
        h.Tick();
        Assert.Equal(Freeze.PhaseReveal, G(h).Phase);
        var awards = h.Awards.Select(a => (a.Nick, a.Reason, a.Shards)).ToList();
        Assert.Contains(("Оля", "ach:freeze-bold", 0), awards);
        Assert.Contains(("Оля", "ach:freeze-clean", 0), awards);
        Assert.Equal(2, awards.Count);
    }

    [Fact]
    public void When_time_runs_out_the_furthest_gets_two_and_the_second_one_with_three_players()
    {
        var h = Table(3, seed: 20);
        Go(h);
        Park(h);
        Sing(h);
        Put(Me(h, 0), 500, 150);
        Put(Me(h, 1), 400, 200);
        Put(Me(h, 2), 300, 250);
        h.Tick(Freeze.RoundTicks - 1);
        Assert.Equal(Freeze.PhaseGo, G(h).Phase);
        h.Tick();
        Assert.Equal(Freeze.PhaseReveal, G(h).Phase);
        var r = h.View(null).GetProperty("reveal");
        Assert.Equal("time", r.GetProperty("why").GetString());
        Assert.Equal([0], r.GetProperty("winners").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal((2, 1, 0), (S(h, 0).Total, S(h, 1).Total, S(h, 2).Total));
        Assert.Equal(500, r.GetProperty("rows")[0].GetProperty("x").GetInt32());
    }

    [Fact]
    public void When_time_runs_out_on_two_only_the_leader_scores_and_a_tie_shares_the_step()
    {
        var h = Table(2, seed: 21);
        Go(h);
        Park(h);
        Sing(h);
        Put(Me(h, 0), 300, 150);
        Put(Me(h, 1), 700, 200);
        h.Tick(Freeze.RoundTicks);
        Assert.Equal((0, 2), (S(h, 0).Total, S(h, 1).Total));

        var g = Table(3, seed: 22);
        Go(g);
        Park(g);
        Sing(g);
        Put(Me(g, 0), 600, 150);
        Put(Me(g, 1), 600, 200);
        Put(Me(g, 2), 100, 250);
        g.Tick(Freeze.RoundTicks);
        Assert.Equal((2, 2, 1), (S(g, 0).Total, S(g, 1).Total, S(g, 2).Total));
    }

    [Fact]
    public void Reveal_lasts_six_seconds_everyone_stands_and_the_next_round_starts_fresh_at_the_fence()
    {
        var h = Table(2, seed: 23);
        Go(h);
        Park(h);
        Sing(h);
        Put(Me(h, 0), FreezeCore.FinishX - 1, 200);
        h.Input(0, "move", new { dir = 0 });
        h.Tick();
        Assert.Equal(Freeze.PhaseReveal, G(h).Phase);
        var at = Core(h).V.Select(v => (v.X, v.Y)).ToArray();
        var frames = h.Outbox.OfType<RoomFrame>().Count();
        h.Input(0, "move", new { dir = 0 });           // у розкритті ввід ковтається мовчки
        Assert.True(h.Act(1, "move", new { dir = 2 }).Ok);
        h.Tick(Freeze.RevealTicks - 1);
        Assert.Equal(Freeze.PhaseReveal, G(h).Phase);
        Assert.Equal(at, Core(h).V.Select(v => (v.X, v.Y)).ToArray());
        Assert.InRange(h.Outbox.OfType<RoomFrame>().Count() - frames, Freeze.RevealTicks / Freeze.RevealFrameEvery - 2, Freeze.RevealTicks / Freeze.RevealFrameEvery + 1);
        h.Tick();
        Assert.Equal(Freeze.PhaseStart, G(h).Phase);
        Assert.Equal(2, G(h).RoundNo);
        Assert.All(Core(h).V, v => Assert.InRange(v.X, FreezeCore.StartMinX, FreezeCore.StartMaxX));
        Assert.All(Core(h).V, v => Assert.Equal(0, v.State));
        Assert.Equal(3, S(h, 0).Total);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
    }

    [Fact]
    public void After_the_last_round_the_match_finishes_with_totals_in_the_journal()
    {
        var h = Table(2, seed: 24, options: new { rounds = "1" });
        Go(h);
        Park(h);
        Sing(h);
        Put(Me(h, 1), FreezeCore.FinishX - 1, 200);
        h.Input(1, "move", new { dir = 0 });
        h.Tick();
        h.Tick(Freeze.RevealTicks);
        Assert.Equal(Freeze.PhaseOver, G(h).Phase);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Finished.Single().Result.Winners);
        Assert.Contains("«Замри!»: Петро 3 : Оля 0", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Contains(h.Scores, s => s.Nick == "Петро" && s.Score == 3);
        Assert.Contains(h.Scores, s => s.Nick == "Оля" && s.Score == 0);
        var res = h.View(null).GetProperty("result");
        Assert.Equal("end", res.GetProperty("why").GetString());
        Assert.Equal(3, res.GetProperty("totals")[1].GetInt32());
    }

    [Fact]
    public void Equal_totals_share_the_win_and_everyone_equal_is_a_draw()
    {
        var h = Table(3, seed: 25, options: new { rounds = "1" });
        Go(h);
        Park(h);
        Sing(h);
        Put(Me(h, 0), FreezeCore.FinishX - 1, 150);
        Put(Me(h, 1), FreezeCore.FinishX - 1, 250);
        h.Input(0, "move", new { dir = 0 });
        h.Input(1, "move", new { dir = 0 });
        h.Tick(1 + Freeze.RevealTicks);
        Assert.Equal([0, 1], h.Finished.Single().Result.Winners.Order());

        var g = Table(2, seed: 26, options: new { rounds = "1" });
        Go(g);
        Park(g);
        Sing(g);
        Put(Me(g, 0), FreezeCore.FinishX - 1, 150);
        Put(Me(g, 1), FreezeCore.FinishX - 1, 250);
        g.Input(0, "move", new { dir = 0 });
        g.Input(1, "move", new { dir = 0 });
        g.Tick(1 + Freeze.RevealTicks);
        Assert.Empty(g.Finished.Single().Result.Winners);
        Assert.Contains("нічия", g.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void Rounds_and_crowd_options_are_honoured_and_junk_falls_back()
    {
        foreach (var (value, rounds) in new[] { ("1", 1), ("5", 5), ("3", 3), ("7", 3), ("x", 3) })
        {
            var h = Table(2, seed: 27, options: new { rounds = value });
            Assert.Equal(rounds, h.View(null).GetProperty("of").GetInt32());
        }
        Assert.Equal(22, Core(Table(2, options: new { crowd = "auto" })).N);
        Assert.Equal(25, Core(Table(3, options: new { crowd = "auto" })).N);
        Assert.Equal(16, Core(Table(2, options: new { crowd = "small" })).N);
        Assert.Equal(48, Core(Table(8, options: new { crowd = "big" })).N);
        Assert.Equal(22, Core(Table(2, options: new { crowd = "huge" })).N);
        Assert.Equal(40, Freeze.BotsFor(8) + 8);
    }

    // =============================================================================================
    // Штурхан
    // =============================================================================================

    [Fact]
    public void Pushing_a_player_knocks_him_down_for_everyone_to_see()
    {
        var h = Table(3, seed: 28);
        Go(h);
        Park(h);
        Sing(h);
        var me = Put(Me(h, 0), 300, 200, dir: 0);
        var him = Put(Me(h, 1), 320, 200);
        var mark = h.Outbox.Count;
        Assert.True(h.Act(0, "push", new { }).Ok, h.Reply.Message);
        Assert.Equal(FreezeCore.DownTicks, him.Down);
        Assert.Equal(0, me.Dazed);
        h.Tick();
        Assert.Contains(Events(h, mark), e => e.SequenceEqual(new[] { 1, me.Id, him.Id, 1 }));
        Assert.Equal(2, LastFrame(h).GetProperty("v")[him.Id * 4 + 3].GetInt32());
        // лежить і не йде, хоч би що тримав
        h.Input(1, "move", new { dir = 0 });
        h.Tick(FreezeCore.DownTicks - 2);
        Assert.Equal(320, him.X);
        Assert.Equal(1, him.Down);
        h.Tick();
        Assert.Equal(0, him.Down);
        Assert.Equal(FreezeCore.GuardTicks, him.Guard);
        Assert.Equal(322, him.X);                        // устав — і того ж тика ступив
        Assert.Equal((1, 1), (S(h, 0).Pushes, S(h, 0).Hits));
    }

    [Fact]
    public void Pushing_a_bot_dazes_the_pusher_for_everyone_to_see()
    {
        var h = Table(2, seed: 29);
        Go(h);
        var bot = Core(h).V.First(v => v.Owner < 0);
        Park(h, bot.Id);
        Sing(h);
        var me = Put(Me(h, 0), 300, 200, dir: 0);
        Put(bot, 316, 200);
        var mark = h.Outbox.Count;
        Assert.True(h.Act(0, "push", new { }).Ok, h.Reply.Message);
        Assert.Equal(FreezeCore.DazeTicks, me.Dazed);
        Assert.Equal(0, bot.Down);
        h.Input(0, "move", new { dir = 0 });
        h.Tick();
        Assert.Contains(Events(h, mark), e => e.SequenceEqual(new[] { 1, me.Id, bot.Id, 0 }));
        Assert.Equal(4, LastFrame(h).GetProperty("v")[me.Id * 4 + 3].GetInt32());
        Assert.Equal(300, me.X);
        h.Tick(FreezeCore.DazeTicks - 2);
        Assert.Equal(300, me.X);
        h.Tick();
        Assert.Equal(302, me.X);
        Assert.Equal((1, 0), (S(h, 0).Pushes, S(h, 0).Hits));
    }

    [Fact]
    public void Push_is_refused_while_baba_turns_or_watches_but_allowed_while_she_only_glances()
    {
        var h = Table(3, seed: 30);
        Go(h);
        Park(h);
        Put(Me(h, 0), 300, 200, dir: 0);
        Put(Me(h, 1), 320, 200);
        foreach (var state in new[] { FreezeCore.Turn, FreezeCore.Watch })
        {
            Core(h).SetBabaForTests(state, 30);
            Assert.False(h.Act(0, "push", new { }).Ok);
            Assert.Equal("Баба дивиться — замри!", h.Reply.Message);
        }
        Core(h).SetBabaForTests(FreezeCore.Glance, 10);
        Assert.True(h.Act(0, "push", new { }).Ok, h.Reply.Message);
    }

    [Fact]
    public void Hands_rest_three_seconds_between_pushes_and_who_got_up_is_spared_for_two()
    {
        var h = Table(3, seed: 31);
        Go(h);
        Park(h);
        Sing(h);
        var me = Put(Me(h, 0), 300, 200, dir: 0);
        var him = Put(Me(h, 1), 320, 200);
        var third = Put(Me(h, 2), 330, 200, dir: 2);
        Assert.True(h.Act(0, "push", new { }).Ok);
        Assert.False(h.Act(0, "push", new { id = third.Id }).Ok);
        Assert.Equal("Руки ще не відійшли", h.Reply.Message);
        Assert.False(h.Act(2, "push", new { id = him.Id }).Ok);
        Assert.Equal("Лежачого не штурхають", h.Reply.Message);
        h.Tick(FreezeCore.DownTicks);
        Assert.Equal(0, him.Down);
        Assert.False(h.Act(2, "push", new { id = him.Id }).Ok);
        Assert.Equal("Дай людині встати", h.Reply.Message);
        h.Tick(FreezeCore.GuardTicks);
        Assert.True(h.Act(2, "push", new { id = him.Id }).Ok, h.Reply.Message);
        Assert.Equal(FreezeCore.PushCoolTicks, third.PushCool);
        Assert.Equal(0, me.PushCool);                    // 90 тиків минуло — руки відійшли
    }

    [Fact]
    public void Auto_push_takes_the_nearest_one_in_front_and_a_click_reaches_a_bit_further()
    {
        var h = Table(4, seed: 32);
        Go(h);
        Park(h);
        Sing(h);
        var me = Put(Me(h, 0), 500, 200, dir: 0);
        var behind = Put(Me(h, 1), 490, 200);
        var near = Put(Me(h, 2), 520, 210);
        var far = Put(Me(h, 3), 530, 200);
        Assert.Equal(near.Id, Core(h).Nearest(me));
        near.X = 540;                  // за 32 — уже не дістати
        Assert.Equal(far.Id, Core(h).Nearest(me));
        far.X = 540;
        Assert.Equal(-1, Core(h).Nearest(me));
        Assert.False(h.Act(0, "push", new { }).Ok);
        Assert.Equal("Нікого поруч", h.Reply.Message);
        // клік — у будь-який бік, до 44
        Put(far, 544, 200);
        Put(behind, 455, 200);
        Assert.False(h.Act(0, "push", new { id = behind.Id }).Ok);
        Assert.Equal("Далеко — не дотягнешся", h.Reply.Message);
        Assert.True(h.Act(0, "push", new { id = far.Id }).Ok, h.Reply.Message);
        Assert.Equal(FreezeCore.DownTicks, far.Down);
    }

    [Fact]
    public void Nobody_pushes_himself_a_lying_one_or_a_ghost_and_the_pusher_must_be_on_his_feet()
    {
        var h = Table(3, seed: 33);
        Go(h);
        Park(h);
        Sing(h);
        var me = Put(Me(h, 0), 300, 200, dir: 0);
        var him = Put(Me(h, 1), 320, 200);
        foreach (var (payload, text) in new (object?, string)[]
        {
            (new { id = me.Id }, "Себе штурхати — якось дивно"),
            (new { id = 999 }, "Такого селянина нема"),
            (new { id = -1 }, "Такого селянина нема"),
            (new { id = "x" }, "Такого селянина нема"),
            ("x", "Такого селянина нема"),
        })
        {
            Assert.False(h.Act(0, "push", payload).Ok);
            Assert.Equal(text, h.Reply.Message);
        }
        him.Caught = 5;
        Assert.False(h.Act(0, "push", new { id = him.Id }).Ok);
        Assert.Equal("Лежачого не штурхають", h.Reply.Message);
        him.Caught = 0;
        foreach (var (set, text) in new (Action, string)[]
        {
            (() => me.Down = 5, "Ти лежиш — спершу встань"),
            (() => me.Dazed = 5, "Ти ще отетерілий"),
            (() => me.Caught = 5, "Тебе впіймали — вертайся на старт"),
        })
        {
            me.Down = me.Dazed = me.Caught = 0;
            set();
            Assert.False(h.Act(0, "push", new { }).Ok);
            Assert.Equal(text, h.Reply.Message);
        }
    }

    [Fact]
    public void Lying_or_dazed_while_baba_watches_is_not_moving()
    {
        var h = Table(3, seed: 34);
        Go(h);
        Park(h);
        Sing(h);
        Put(Me(h, 0), 300, 200, dir: 0);
        var him = Put(Me(h, 1), 320, 200);
        Assert.True(h.Act(0, "push", new { }).Ok);
        h.Input(1, "move", new { dir = 0 });           // тримає стрілку, лежачи
        Core(h).SetBabaForTests(FreezeCore.Watch, 30);
        h.Tick(20);
        Assert.Equal(0, him.Caught);
        Assert.Equal(320, him.X);
    }

    // =============================================================================================
    // Вихід, F5, «Ще раз»
    // =============================================================================================

    [Fact]
    public void A_leaver_becomes_a_bot_and_the_round_goes_on()
    {
        var h = Table(3, seed: 35);
        Go(h);
        var his = Me(h, 2);
        var n = Core(h).N;
        h.Leave("Ганна");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(-1, his.Owner);
        Assert.Equal(n, Core(h).N);
        var at = (his.X, his.Y);
        h.Tick(400);
        Assert.NotEqual(at, (his.X, his.Y));
        Assert.Equal(4 * n, LastFrame(h).GetProperty("v").GetArrayLength());
        var seat = h.View(null).GetProperty("seats").EnumerateArray().Single(s => s.GetProperty("seat").GetInt32() == 2);
        Assert.True(seat.GetProperty("out").GetBoolean());
        Assert.Equal("Ганна", seat.GetProperty("nick").GetString());
        h.Tick(Freeze.RoundTicks + Freeze.RevealTicks);
        Assert.Equal(2, G(h).RoundNo);
        Assert.Equal(n, Core(h).N);
        Assert.Equal(2, Core(h).V.Count(v => v.Owner >= 0));
    }

    [Fact]
    public void A_leaver_while_baba_watches_stands_still_like_every_bot()
    {
        var h = Table(3, seed: 36);
        Go(h);
        Park(h);
        var his = Put(Me(h, 2), 400, 200);
        h.Input(2, "move", new { dir = 0 });
        Core(h).SetBabaForTests(FreezeCore.Watch, 40);
        h.Leave("Ганна");
        h.Tick(30);
        Assert.Equal(400, his.X);
        Assert.Equal(0, his.Caught);
    }

    [Fact]
    public void When_only_one_player_remains_the_match_ends_in_his_favour()
    {
        var h = Table(2, seed: 37);
        Go(h);
        h.Leave("Оля");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Finished.Single().Result.Winners);
        Assert.Contains("Петро сам-на-сам із Бабою Параскою", h.Outbox.OfType<Journal>().Last().Text);
        var v = h.View(null);
        Assert.Equal("left", v.GetProperty("result").GetProperty("why").GetString());
        Assert.Equal("left", v.GetProperty("reveal").GetProperty("why").GetString());
        Assert.Equal(2, v.GetProperty("reveal").GetProperty("ids").GetArrayLength());
    }

    [Fact]
    public void Rematch_gives_a_clean_match_with_rotated_seats()
    {
        var h = Table(2, seed: 38, options: new { rounds = "1" });
        Go(h);
        Park(h);
        Sing(h);
        Put(Me(h, 0), FreezeCore.FinishX - 1, 200);
        h.Input(0, "move", new { dir = 0 });
        h.Tick(1 + Freeze.RevealTicks);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        h.Rematch("Оля");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("Петро", h.NickOf(0));
        Assert.Equal(Freeze.PhaseStart, G(h).Phase);
        Assert.Equal(1, G(h).RoundNo);
        Assert.All(new[] { 0, 1 }, s => Assert.Equal(0, S(h, s).Total));
        Assert.Equal("Петро", h.View(null).GetProperty("seats")[0].GetProperty("nick").GetString());
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("result").ValueKind);
    }

    [Fact]
    public void A_held_arrow_that_is_not_confirmed_for_three_seconds_is_let_go()
    {
        var h = Table(2, seed: 39);
        Go(h);
        Park(h);
        Sing(h);
        var me = Put(Me(h, 0), 300, 200);
        h.Input(0, "move", new { dir = 1 });
        h.Tick(Freeze.MoveHoldTicks);
        Assert.Equal(1, me.Want);
        h.Tick();
        Assert.Equal(-1, me.Want);
        // модуль підтверджує раз на секунду — тоді тримається скільки завгодно
        h.Input(0, "move", new { dir = 1 });
        for (var i = 0; i < 4; i++)
        {
            h.Tick(25);
            h.Input(0, "move", new { dir = 1 });
        }
        Assert.Equal(1, me.Want);
    }

    [Fact]
    public void Match_that_has_not_started_refuses_every_action()
    {
        var h = new RoomHarness("freeze", seed: 1);
        h.Join("Оля");
        h.Join("Петро");
        var view = h.View(0);
        Assert.Equal("lobby", view.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("me").ValueKind);
        Assert.Equal(20 * 4, view.GetProperty("v").GetArrayLength());
        var game = (Freeze)h.Room.Game;
        Assert.Equal("Партія ще не почалась", game.Act(0, "move", Views.Payload(new { dir = 1 })).Message);
        Assert.Equal("Партія ще не почалась", game.Act(0, "push", Views.Payload(new { })).Message);
    }

    // =============================================================================================
    // Приховане
    // =============================================================================================

    [Fact]
    public void Frame_has_exactly_seven_keys_and_four_numbers_per_villager()
    {
        var h = Table(5, seed: 40);
        Go(h);
        h.Tick();
        var f = LastFrame(h);
        Assert.Equal(["t", "ph", "left", "b", "bl", "v", "ev"], f.EnumerateObject().Select(p => p.Name));
        Assert.Equal(4 * Core(h).N, f.GetProperty("v").GetArrayLength());
        Assert.All(f.GetProperty("v").EnumerateArray(), e => Assert.Equal(JsonValueKind.Number, e.ValueKind));
    }

    [Fact]
    public void Frame_json_never_mentions_seats_owners_or_counters()
    {
        var h = Table(4, seed: 41);
        Go(h);
        Park(h);
        Sing(h);
        Put(Me(h, 0), 300, 200, dir: 0);
        Put(Me(h, 1), 320, 200);
        h.Act(0, "push", new { });
        Core(h).SetBabaForTests(FreezeCore.Watch, 30);
        h.Input(2, "move", new { dir = 0 });
        h.Tick(60);
        Assert.Contains(h.Outbox.OfType<RoomFrame>(), f => Views.Text(f.Frame).Contains("[2,"));
        foreach (var f in h.Outbox.OfType<RoomFrame>())
        {
            var text = Views.Text(f.Frame);
            foreach (var word in new[] { "\"seat", "\"me", "\"owner", "\"nick", "\"total", "\"caught", "\"hits", "\"pushes" })
                Assert.DoesNotContain(word, text);
        }
    }

    [Fact]
    public void Player_ids_are_shuffled_among_bots_across_seeds()
    {
        int high = 0, notFirst = 0;
        for (var seed = 1; seed <= 100; seed++)
        {
            var core = new FreezeCore(new Random(seed));
            core.Deal([0, 1], 20);
            var ids = core.V.Where(v => v.Owner >= 0).Select(v => v.Id).ToArray();
            if (ids.Any(id => id >= core.N / 2)) high++;
            if (ids.Any(id => id >= 2)) notFirst++;
        }
        Assert.True(high > 50, $"{high}");
        Assert.True(notFirst > 90, $"{notFirst}");
    }

    [Fact]
    public void Everyone_is_dealt_alike_at_the_fence_facing_the_house()
    {
        var px = new List<int>();
        var bx = new List<int>();
        for (var seed = 1; seed <= 60; seed++)
        {
            var core = new FreezeCore(new Random(seed));
            core.Deal([0, 1, 2, 3], 20);
            foreach (var v in core.V)
            {
                Assert.InRange(v.X, FreezeCore.StartMinX, FreezeCore.StartMaxX);
                Assert.InRange(v.Y, FreezeCore.MinY, FreezeCore.MaxY);
                Assert.Equal(0, v.Dir);
                Assert.Equal(0, v.State);
                (v.Owner >= 0 ? px : bx).Add(v.X);
            }
            Assert.Equal(24, core.V.Select(v => v.Name).Distinct().Count());
        }
        // середня відстань від тину — однакова (різниця — шум)
        Assert.InRange(px.Average() - bx.Average(), -4, 4);
    }

    [Fact]
    public void Watcher_view_has_no_me_and_other_seats_see_only_their_own_me()
    {
        var h = Table(3, seed: 42);
        Go(h);
        var watcher = h.View(null);
        Assert.Equal(JsonValueKind.Null, watcher.GetProperty("me").ValueKind);
        Assert.Equal(JsonValueKind.Null, watcher.GetProperty("reveal").ValueKind);
        var mine = h.View(0).GetProperty("me");
        Assert.Equal(S(h, 0).Me, mine.GetProperty("id").GetInt32());
        var other = h.View(1);
        Assert.Equal(S(h, 1).Me, other.GetProperty("me").GetProperty("id").GetInt32());
        Assert.Equal(Strip(watcher), Strip(other));
        Assert.Equal(Strip(watcher), Strip(h.View(0)));
        Assert.DoesNotContain("\"ids\"", Views.Text(watcher));
    }

    /// <summary>Вид без <c>me</c> — щоб порівняти два види.</summary>
    static string Strip(JsonElement view) =>
        Views.Text(view.EnumerateObject().Where(p => p.Name != "me").ToDictionary(p => p.Name, p => p.Value));

    [Fact]
    public void Knocked_down_or_caught_players_see_no_more_than_a_watcher()
    {
        var h = Table(3, seed: 43);
        Go(h);
        Park(h);
        Sing(h);
        Put(Me(h, 0), 300, 200, dir: 0);
        Put(Me(h, 1), 320, 200);
        Assert.True(h.Act(0, "push", new { }).Ok);
        Core(h).SetBabaForTests(FreezeCore.Watch, 40);
        h.Input(2, "move", new { dir = 0 });
        h.Tick();
        Assert.True(Me(h, 2).Caught > 0 && Me(h, 1).Down > 0);
        Assert.Equal(Strip(h.View(null)), Strip(h.View(1)));
        Assert.Equal(Strip(h.View(null)), Strip(h.View(2)));
        Assert.DoesNotContain("\"ids\"", Views.Text(h.View(2)));
    }

    [Fact]
    public void Reveal_ids_appear_only_in_reveal_and_over_phases()
    {
        var h = Table(2, seed: 44, options: new { rounds = "1" });
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
        Go(h);
        Park(h);
        Sing(h);
        h.Tick(Freeze.RoundTicks - 1);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
        h.Tick();
        var ids = h.View(null).GetProperty("reveal").GetProperty("ids").EnumerateArray()
            .Select(e => (e.GetProperty("seat").GetInt32(), e.GetProperty("id").GetInt32())).ToList();
        Assert.Equal([(0, S(h, 0).Me), (1, S(h, 1).Me)], ids);
        h.Tick(Freeze.RevealTicks);
        Assert.Equal(Freeze.PhaseOver, G(h).Phase);
        Assert.NotEqual(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
    }

    [Fact]
    public void Per_tick_steps_and_states_of_players_are_a_subset_of_those_of_bots()
    {
        var h = Table(4, seed: 45);
        var rng = new Random(3);
        var core = Core(h);
        var botSteps = new HashSet<(int, int, int)>();
        var playerSteps = new HashSet<(int, int, int)>();
        var was = core.V.Select(v => (v.X, v.Y)).ToArray();
        for (var t = 0; t < 3000 && G(h).Phase is Freeze.PhaseStart or Freeze.PhaseGo; t++)
        {
            if (t % 9 == 0)
                for (var s = 0; s < 4; s++) h.Input(s, "move", new { dir = rng.Next(-1, 4) });
            h.Tick();
            foreach (var v in core.V)
            {
                int dx = v.X - was[v.Id].X, dy = v.Y - was[v.Id].Y;
                // повернення на старт — одним кошиком: відстань до тину в кожного своя
                // іде (1) — напрямок кроку; решта станів — лише «зрушив чи ні» (впіймали на кроці вгору чи вправо — те саме)
                var moved = Math.Abs(dx) + Math.Abs(dy);
                // стоїть (0), а зрушив — це повернення на старт (хоч би й на одиницю, якщо впіймали біля тину)
                var jump = moved > FreezeCore.Speed || (moved > 0 && v.State == 0);
                var step = jump ? (99, 99, v.State) : v.State == 1 ? (dx, dy, 1) : (moved, 0, v.State);
                // «лежить» (2) — лише гравець, і це публічний наслідок штурхана (spec §2.5), а не ознака ходи
                if (v.State != 2) (v.Owner >= 0 ? playerSteps : botSteps).Add(step);
                was[v.Id] = (v.X, v.Y);
            }
        }
        output.WriteLine($"гравці: {string.Join(" ", playerSteps.Order())}; боти: {string.Join(" ", botSteps.Order())}");
        Assert.Subset(botSteps, playerSteps);
    }

    [Fact]
    public void Pushes_and_catches_send_no_views_so_nobody_learns_whose_they_were()
    {
        var h = Table(3, seed: 46);
        Go(h);
        Park(h);
        Sing(h);
        Put(Me(h, 0), 300, 200, dir: 0);
        Put(Me(h, 1), 320, 200);
        h.Tick(2);
        var views = h.Outbox.OfType<RoomViews>().Count();
        Assert.True(h.Act(0, "push", new { }).Ok);
        h.Tick(3);
        Core(h).SetBabaForTests(FreezeCore.Watch, 40);
        h.Input(2, "move", new { dir = 0 });
        h.Tick(3);
        Assert.True(Me(h, 2).Caught > 0);
        Assert.Equal(views, h.Outbox.OfType<RoomViews>().Count());
    }

    // =============================================================================================
    // Контракт і детермінізм
    // =============================================================================================

    [Fact]
    public void The_server_accepts_exactly_what_the_module_sends()
    {
        var h = Table(2, seed: 47);
        var me = Me(h, 0);
        h.Input(0, "move", new { dir = 0 });              // keydown
        Assert.Equal(0, me.Want);
        h.Input(0, "move", 2);                             // голе число теж
        Assert.Equal(2, me.Want);
        h.Input(0, "move", new { dir = -1 });             // keyup
        Assert.Equal(-1, me.Want);
        Go(h);
        var bot = Core(h).V.First(v => v.Owner < 0);
        Park(h, bot.Id);
        Sing(h);
        Put(me, 300, 200, dir: 0);
        Put(bot, 316, 200);
        Assert.True(h.Act(0, "push", new { }).Ok, h.Reply.Message);                  // пробіл: найближчий попереду
        h.Tick(FreezeCore.PushCoolTicks);
        Put(Me(h, 1), 330, 220);
        Assert.True(h.Act(0, "push", new { id = Me(h, 1).Id }).Ok, h.Reply.Message);  // клік по селянину
        var before = Views.Text(h.View(0));
        foreach (var (action, payload) in new (string, object?)[] { ("move", new { dir = "up" }), ("move", new { d = 1 }), ("move", 4), ("jump", null), ("push", "x"), ("push", new { id = 999 }) })
            Assert.False(h.Act(0, action, payload).Ok);
        Assert.Equal(before, Views.Text(h.View(0)));
    }

    [Fact]
    public void The_module_sends_only_moves_and_pushes_in_the_shapes_the_server_reads()
    {
        var js = File.ReadAllText(Path.Combine(FindRoot(), "web", "games", "freeze.js"));
        var sent = System.Text.RegularExpressions.Regex.Matches(js, @"ctx\.(?:act|input)\('(\w+)'")
            .Select(m => m.Groups[1].Value).Distinct().Order().ToArray();
        Assert.Equal(["move", "push"], sent);
        Assert.Contains("ctx.input('move', { dir: d })", js);
        Assert.Contains("ctx.input('move', { dir: -1 })", js);
        Assert.Contains("ctx.act('push', id == null ? {} : { id })", js);
    }

    [Fact]
    public void Module_constants_match_the_server_rules()
    {
        var js = File.ReadAllText(Path.Combine(FindRoot(), "web", "games", "freeze.js"));
        int Const(string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(js, @"\b" + name + @"\s*=\s*(\d+)");
            Assert.True(m.Success, name);
            return int.Parse(m.Groups[1].Value);
        }
        Assert.Equal(Freeze.TickMs, Const("TICK_MS"));
        Assert.Equal(FreezeCore.WorldW, Const("WW"));
        Assert.Equal(FreezeCore.WorldH, Const("WH"));
        Assert.Equal(FreezeCore.FinishX, Const("FINISH_X"));
        Assert.Equal(FreezeCore.StartMaxX, Const("START_X"));
        Assert.Equal(FreezeCore.MinY, Const("MIN_Y"));
        Assert.Equal(FreezeCore.MaxY, Const("MAX_Y"));
        Assert.Equal(FreezeCore.PushRange, Const("PUSH_RANGE"));
        Assert.Equal(FreezeCore.PushRangeMax, Const("PUSH_MAX"));
        Assert.Equal(FreezeCore.PushCoolTicks * Freeze.TickMs, Const("PUSH_COOL_MS"));
        Assert.Equal(FreezeCore.Grace, Const("GRACE"));
        Assert.Equal(Freeze.RoundTicks, Const("ROUND_TICKS"));
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
            if (t % 37 == 0) h.Act((t + 1) % 3, "push", new { });
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
        Assert.Equal(a, Replay(60));
        Assert.NotEqual(a, Replay(61));
    }

    [Fact]
    public void Views_json_matches_the_spec_shape()
    {
        var h = Table(2, seed: 48);
        Go(h);
        var v = h.View(0);
        foreach (var key in new[] { "phase", "round", "of", "left", "t", "w", "h", "finish", "n", "looks", "names", "v", "b", "bl", "seats", "me", "reveal", "result", "turn" })
            Assert.True(Views.Has(v, key), key);
        var n = v.GetProperty("n").GetInt32();
        Assert.Equal(FreezeCore.WorldW, v.GetProperty("w").GetInt32());
        Assert.Equal(FreezeCore.FinishX, v.GetProperty("finish").GetInt32());
        Assert.Equal(4 * n, v.GetProperty("looks").GetArrayLength());
        Assert.Equal(n, v.GetProperty("names").GetArrayLength());
        Assert.Equal(4 * n, v.GetProperty("v").GetArrayLength());
        foreach (var key in new[] { "id", "cool", "caught" }) Assert.True(Views.Has(v.GetProperty("me"), key), key);
        foreach (var key in new[] { "seat", "nick", "out", "total" }) Assert.True(Views.Has(v.GetProperty("seats")[0], key), key);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("turn").ValueKind);
        Park(h);
        Sing(h);
        Put(Me(h, 0), FreezeCore.FinishX - 1, 200);
        h.Input(0, "move", new { dir = 0 });
        h.Tick();
        var r = h.View(0).GetProperty("reveal");
        foreach (var key in new[] { "winners", "why", "ids", "rows", "trails" }) Assert.True(Views.Has(r, key), key);
        foreach (var key in new[] { "seat", "x", "caught", "hits", "win", "pts" }) Assert.True(Views.Has(r.GetProperty("rows")[0], key), key);
        output.WriteLine($"вид на {n} селян: {Views.Text(G(h).View(0)).Length} Б");
    }

    [Fact]
    public void Catalog_lists_freeze_as_live_by_host_hidden_tick_forty_with_css()
    {
        var info = new Freeze().Info;
        Assert.Equal("freeze", info.Id);
        Assert.Equal("Замри!", info.Title);
        Assert.Equal(GameGroup.Live, info.Group);
        Assert.Equal((2, 8), (info.MinPlayers, info.MaxPlayers));
        Assert.Equal(40, info.TickMs);
        Assert.Equal(StartMode.ByHost, info.Start);
        Assert.True(info.Hidden);
        Assert.Equal(ScoreOrder.HigherIsBetter, info.Score);
        Assert.Equal(["rounds", "crowd"], info.Options!.Select(o => o.Key));
        Assert.True(File.Exists(Path.Combine(FindRoot(), "web", "games", "freeze.js")));
        Assert.True(File.Exists(Path.Combine(FindRoot(), "web", "games", "freeze.css")));
        Assert.Equal("рожевий", new Freeze().SeatName(5));
        Assert.True(RoomHarness.NewRegistry().Has("freeze"));
    }


    [Fact]
    public void Achievements_are_requested_exactly_when_earned()
    {
        Assert.NotNull(AchievementCatalog.Get("freeze-clean"));
        Assert.NotNull(AchievementCatalog.Get("freeze-bold"));
        // Олю раз упіймали — дійшла, але не «кам'яна»; Баба співала — і не «під носом»
        var h = Table(2, seed: 49);
        Go(h);
        Park(h);
        var me = Put(Me(h, 0), FreezeCore.FinishX - 1, 200);
        Core(h).SetBabaForTests(FreezeCore.Watch, 30);
        h.Input(0, "move", new { dir = 0 });
        h.Tick();
        Assert.Equal(1, S(h, 0).Caught);
        Sing(h);
        me.Caught = 0;
        Put(me, FreezeCore.FinishX - 1, 200);
        h.Input(0, "move", new { dir = 0 });
        h.Tick();
        Assert.Equal(Freeze.PhaseReveal, G(h).Phase);
        Assert.Empty(h.Awards);
        // чисто, поки Баба співає, — лише «кам'яна»
        var g = Table(2, seed: 50);
        Go(g);
        Park(g);
        Sing(g);
        Put(Me(g, 1), FreezeCore.FinishX - 1, 200);
        g.Input(1, "move", new { dir = 0 });
        g.Tick();
        Assert.Equal([("Петро", "ach:freeze-clean")], g.Awards.Select(a => (a.Nick, a.Reason)));
    }

    // =============================================================================================
    // Баланс (заміри для spec) і швидкодія
    // =============================================================================================

    /// <summary>
    /// Людина, що йде завжди, коли можна: рушає за 7 тиків після того, як Баба заспівала, і відпускає клавішу за 8 тиків
    /// після «Замри!» (пінг + реакція); озирнулась — іде далі. Скільки триває раунд і скільки ботів ловлять.
    /// </summary>
    [Fact]
    public void A_greedy_player_reaches_the_jug_in_half_a_minute_to_a_minute()
    {
        var times = new List<int>();
        var caughtBots = 0;
        for (var seed = 1; seed <= 6; seed++)
        {
            var core = Bare(players: 1, bots: 20, seed: seed);
            var p = core.V.Single(v => v.Owner == 0);
            int since = 0, was = core.Baba;
            for (var t = 1; t <= 20_000; t++)
            {
                since = core.Baba == was ? since + 1 : 0;
                was = core.Baba;
                p.Want = core.Baba switch
                {
                    FreezeCore.Sing or FreezeCore.Glance => since >= 7 || p.Want == 0 ? 0 : -1,
                    FreezeCore.Turn => since < 8 ? p.Want : -1,
                    _ => -1,
                };
                core.Ev.Clear();
                core.TickGo();
                caughtBots += core.Ev.Count(e => e[0] == 2 && core.V[e[1]].Owner < 0);
                if (p.X >= FreezeCore.FinishX) { times.Add(t); break; }
            }
        }
        output.WriteLine($"жадібний гравець дійшов за {string.Join(", ", times.Select(t => $"{t * Freeze.TickMs / 1000.0:F1}"))} с; ботів упіймали за ці раунди: {caughtBots}");
        Assert.Equal(6, times.Count);
        Assert.InRange(times.Average() * Freeze.TickMs / 1000, 25, 60);
        Assert.True(caughtBots >= 6);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Three_thousand_ticks_with_eight_players_and_forty_bots_fit_in_a_second()
    {
        long best = long.MaxValue;
        for (var attempt = 0; attempt < 3 && best >= 1000; attempt++)
        {
            var h = Table(8, seed: 70 + attempt, options: new { crowd = "big", rounds = "5" });
            var rng = new Random(attempt);
            var sw = Stopwatch.StartNew();
            for (var t = 0; t < 3000 && h.Room.Status == RoomStatus.Playing; t++)
            {
                if (t % 10 == 0)
                    for (var s = 0; s < 8; s++) h.Input(s, "move", new { dir = rng.Next(-1, 4) });
                if (t % 100 == 50)
                    for (var s = 0; s < 8; s++) h.Act(s, "push", new { });
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
            var h = Table(8, seed: 90 + attempt, options: new { crowd = "big", rounds = "5" });
            var game = G(h);
            Go(h);
            var rng = new Random(attempt);
            for (var i = 0; i < 200; i++) game.Tick();
            var sw = Stopwatch.StartNew();
            var n = 0;
            for (var t = 0; t < 3000 && game.Phase != Freeze.PhaseOver; t++)
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
    public void A_frame_with_forty_eight_villagers_serialises_under_1200_bytes()
    {
        var h = Table(8, seed: 72, options: new { crowd = "big" });
        Go(h);
        Assert.Equal(48, Core(h).N);
        var max = 0;
        for (var t = 0; t < 1500 && G(h).Phase == Freeze.PhaseGo; t++)
        {
            if (t % 50 == 0) h.Act(t / 50 % 8, "push", new { });
            h.Tick();
            max = Math.Max(max, FrameText(h).Length);
        }
        output.WriteLine($"кадр на 48 селян: до {max} Б");
        Assert.True(max < 1200, $"{max} Б");
    }
}
