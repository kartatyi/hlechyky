using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Кривуля, прохід №3 (29.09.2026): поле-тор, бонуси Achtung, «хто кого», команди. Правила — на голому
/// ядрі, опції, очки й кадр — через кімнату.
/// </summary>
public class CurveSweep3Tests
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марко", "Зоя", "Тарас", "Леся"];

    static CurveCore Core(int seats = 1, bool wrap = false, bool bonuses = false)
    {
        var core = new CurveCore(new Random(1), false) { Wrap = wrap, Bonuses = bonuses };
        core.Reset([.. Enumerable.Range(0, seats).Select(_ => true)]);
        return core;
    }

    static void Put(CurveCore core, int seat, double x, double y, double a)
    {
        var h = core.Heads[seat];
        (h.X, h.Y, h.A, h.Turn) = (x, y, a, 0);
        h.Trail.Clear();
        h.Painted = -1;
        h.Trail.Add(new CurvePoint(x, y, false));
    }

    static RoomHarness Table(int players, object? options = null, int seed = 42)
    {
        var h = new RoomHarness("curve", options, seed: seed);
        foreach (var nick in Nicks.Take(players)) h.Join(nick);
        h.Start();
        return h;
    }

    static CurveGame Game(RoomHarness h) => (CurveGame)h.Room.Game;

    static JsonElement LastFrame(RoomHarness h) => Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);

    // ---------- поле-тор ----------

    [Fact]
    public void On_a_torus_a_curve_leaves_on_the_right_and_comes_back_on_the_left()
    {
        var core = Core(wrap: true);
        Put(core, 0, core.W - 1, 100, 0);
        for (var i = 0; i < 5; i++) Assert.Empty(core.Step());

        var h = core.Heads[0];
        Assert.True(h.Alive);
        Assert.InRange(h.X, 0, 8);
        Assert.Contains(h.Trail, p => p.Jump);
    }

    [Fact]
    public void Without_the_torus_the_same_ride_ends_in_the_wall()
    {
        var core = Core();
        Put(core, 0, core.W - 3, 100, 0);
        var dead = core.Step();
        Assert.Equal([0], dead);
        Assert.Equal((0, -1, false), core.Deaths[0]);
    }

    [Fact]
    public void On_a_torus_the_trail_across_the_edge_still_kills()
    {
        var core = Core(2, wrap: true);
        // зелена їде вздовж лівого краю вниз і лишає слід на x ≈ 1
        Put(core, 1, 1, 20, Math.PI / 2);
        for (var i = 0; i < 40; i++) core.Step();
        // жовта з правого краю їде праворуч — крізь край просто в той слід
        Put(core, 0, core.W - 0.5, 40, 0);
        var dead = new List<int>();
        for (var i = 0; i < 4 && dead.Count == 0; i++) dead = core.Step();
        Assert.Equal([0], dead);
        Assert.Equal(1, core.Deaths[0].Killer);
    }

    [Fact]
    public void The_polyline_does_not_draw_a_line_across_the_whole_field_after_a_jump()
    {
        List<CurvePoint> trail = [new(297, 100, false), new(298.6, 100, false), new(0.2, 100, false, Jump: true), new(1.8, 100, false)];
        var (pts, gaps) = CurveCore.Polyline(trail);
        Assert.Contains(gaps, g => pts[2 * g] == 0);
    }

    // ---------- хто кого ----------

    [Fact]
    public void A_crash_says_whose_trail_it_was()
    {
        var core = Core(2);
        Put(core, 1, 50, 100, 0);
        for (var i = 0; i < 20; i++) core.Step();
        var p = core.Heads[1].Trail[3];
        Put(core, 0, p.X, p.Y - 5, Math.PI / 2);
        var dead = new List<int>();
        for (var i = 0; i < 4 && dead.Count == 0; i++) dead = core.Step();

        Assert.Equal([0], dead);
        Assert.Equal((0, 1, false), core.Deaths[0]);
    }

    [Fact]
    public void Own_trail_is_blamed_on_yourself_and_head_on_on_each_other()
    {
        var self = Core();
        self.Heads[0].Turn = 1;
        var dead = new List<int>();
        for (var i = 0; i < 200 && dead.Count == 0; i++) dead = self.Step();
        Assert.Equal((0, 0, false), self.Deaths[0]);

        var duel = Core(2);
        Put(duel, 0, 100, 100, 0);
        Put(duel, 1, 105, 100, Math.PI);
        for (var i = 0; i < 3 && duel.Deaths.Count == 0; i++) duel.Step();
        Assert.Contains((0, 1, true), duel.Deaths);
        Assert.Contains((1, 0, true), duel.Deaths);
    }

    [Fact]
    public void The_frame_carries_the_crash_and_the_view_counts_trail_traps()
    {
        var h = Table(2);
        h.Tick(CurveCore.ReadyTicks + 20);
        var field = Game(h).Field;
        var p = field.Heads[1].Trail[3];
        var a = field.Heads[0];
        (a.X, a.Y, a.A, a.Turn) = (p.X - 5 * Math.Cos(field.Heads[1].A + Math.PI / 2), p.Y - 5 * Math.Sin(field.Heads[1].A + Math.PI / 2), field.Heads[1].A + Math.PI / 2, 0);
        for (var i = 0; i < 4 && a.Alive; i++) h.Tick();

        Assert.False(a.Alive);
        var ev = h.Outbox.OfType<RoomFrame>().Select(f => Views.Json(f.Frame)).Last(f => f.TryGetProperty("ev", out _)).GetProperty("ev");
        Assert.Equal(0, ev[0][0].GetInt32());
        Assert.Equal(1, ev[0][1].GetInt32());
        Assert.Equal(1, h.View(0).GetProperty("kills")[1].GetInt32());
        h.Tick();
        Assert.False(LastFrame(h).TryGetProperty("ev", out _), "подія їде лише в тому кадрі, де сталась");
    }

    // ---------- бонуси ----------

    [Fact]
    public void Picking_a_bonus_removes_it_and_speeds_you_up()
    {
        var core = Core(bonuses: true);
        Put(core, 0, 100, 100, 0);
        core.Items.Add(new CurveBonus(CurveCore.BFastMe, 106, 100));
        core.Step();

        Assert.Empty(core.Items);
        Assert.Equal([(0, CurveCore.BFastMe)], core.Picks);
        var x = core.Heads[0].X;
        core.Step();
        Assert.Equal(CurveCore.Speed * CurveCore.FastK, core.Heads[0].X - x, 6);
    }

    [Fact]
    public void Red_bonuses_hit_everyone_else_and_blue_clears_the_field()
    {
        var core = Core(3, bonuses: true);
        Put(core, 0, 50, 50, 0);
        Put(core, 1, 150, 100, 0);
        Put(core, 2, 200, 150, 0);
        core.Apply(0, CurveCore.BInvert);
        core.Apply(0, CurveCore.BFat);
        Assert.Equal(0, core.Heads[0].Inv);
        Assert.True(core.Heads[1].Inv > 0 && core.Heads[2].Fat > 0);

        // 🔄: тримає праворуч — їде ліворуч
        core.Heads[1].Turn = 1;
        var a = core.Heads[1].A;
        core.Step();
        Assert.True(core.Heads[1].A < a);

        for (var i = 0; i < 10; i++) core.Step();
        Assert.NotEqual(0, core.Cell((int)core.Heads[0].Trail[2].X, (int)core.Heads[0].Trail[2].Y));
        core.Apply(2, CurveCore.BClear);
        Assert.Equal(1, core.Cleared);
        for (var y = 0; y < core.H; y++)
            for (var x = 0; x < core.W; x++)
                Assert.Equal(0, core.Cell(x, y));
    }

    [Fact]
    public void A_fat_slow_curve_never_crashes_into_its_own_fresh_trail()
    {
        var core = Core(bonuses: true);
        Put(core, 0, 150, 100, 0);
        var h = core.Heads[0];
        h.Fat = 10_000;
        h.Slow = 10_000;
        h.Turn = 1;
        // коло на 🐢 має діаметр ~14 од і оберт за ~50 тиків (товстий слід наздоганяє десь на 44-му): до того свіжий слід не вбиває
        for (var i = 0; i < 40; i++) Assert.Empty(core.Step());
        // а товстий слід справді товстий: 3 од убік від точки сліду — теж слід
        var p = h.Trail[5];
        Assert.True(h.Trail.Skip(1).Take(10).All(q => q.Fat));
        Assert.NotEqual(0, core.Cell((int)Math.Floor(p.X), (int)Math.Floor(p.Y)));
    }

    [Fact]
    public void Through_the_walls_bonus_lets_one_curve_leave_the_field()
    {
        var core = Core(bonuses: true);
        Put(core, 0, core.W - 3, 100, 0);
        core.Apply(0, CurveCore.BThrough);
        for (var i = 0; i < 5; i++) Assert.Empty(core.Step());
        Assert.InRange(core.Heads[0].X, 0, 8);
    }

    [Fact]
    public void Bonuses_appear_only_when_the_option_is_on()
    {
        var on = Core(2, bonuses: true);
        var off = Core(2);
        var seen = 0;
        for (var i = 0; i < 600; i++)
        {
            foreach (var c in new[] { on, off })
                foreach (var hd in c.Heads.Where(x => x.Present)) { hd.Alive = true; hd.Turn = 1; }
            on.Step();
            off.Step();
            seen = Math.Max(seen, on.Items.Count);
            Assert.InRange(on.Items.Count, 0, CurveCore.MaxBonuses);
        }
        Assert.True(seen > 0);
        Assert.Empty(off.Items);
    }

    [Fact]
    public void Default_frame_has_no_bonus_fields_and_bonus_frame_stays_small()
    {
        var plain = Table(2);
        plain.Tick(CurveCore.ReadyTicks + 5);
        var f = LastFrame(plain);
        Assert.False(f.TryGetProperty("b", out _));
        Assert.False(f.GetProperty("heads")[0].TryGetProperty("fx", out _));
        Assert.False(plain.View(0).TryGetProperty("wrap", out _));

        var fun = Table(8, new { bonus = "1", walls = "1" });
        var biggest = 0;
        for (var i = 0; i < 400; i++)
        {
            foreach (var hd in Game(fun).Field.Heads.Where(x => x.Present)) hd.Turn = i % 60 < 30 ? 1 : -1;
            fun.Tick();
            biggest = Math.Max(biggest, Views.Text(fun.Outbox.OfType<RoomFrame>().Last().Frame).Length);
        }
        Assert.True(LastFrame(fun).TryGetProperty("b", out _));
        Assert.True(fun.View(0).GetProperty("wrap").GetBoolean());
        Assert.True(biggest <= 1500, $"кадр {biggest} Б");
    }

    [Fact]
    public void Eight_with_bonuses_and_torus_tick_cheaply()
    {
        var fun = Table(8, new { bonus = "1", walls = "1" });
        fun.Tick(CurveCore.ReadyTicks);
        var game = Game(fun);
        var sw = Stopwatch.StartNew();
        var ticks = 0;
        for (var i = 0; i < 2500; i++)
        {
            foreach (var hd in game.Field.Heads.Where(x => x.Present)) hd.Turn = (i / 25 + hd.GetHashCode()) % 3 - 1;
            game.Tick();
            ticks++;
        }
        sw.Stop();
        Assert.True(sw.Elapsed.TotalMilliseconds / ticks < 0.25, $"тик {sw.Elapsed.TotalMilliseconds / ticks:F3} мс");
    }

    // ---------- команди ----------

    [Fact]
    public void Teams_split_the_table_by_seating_order_and_play_to_ten_per_rival()
    {
        var h = Table(4, new { teams = "1" });
        Assert.Equal([0, 1, 0, 1, -1, -1, -1, -1], Game(h).Teams);
        var v = h.View(0);
        Assert.Equal(20, v.GetProperty("target").GetInt32());
        Assert.Equal(0, v.GetProperty("teams")[2].GetInt32());
    }

    [Fact]
    public void An_odd_table_plays_every_man_for_himself_and_says_why()
    {
        var h = Table(3, new { teams = "1" });
        Assert.Null(Game(h).Teams);
        Assert.Contains("Команд не буде", h.View(0).GetProperty("note").GetString());
    }

    [Fact]
    public void A_team_scores_for_fallen_rivals_and_the_round_ends_when_one_team_is_left()
    {
        var h = Table(4, new { teams = "1" });
        h.Tick(CurveCore.ReadyTicks);
        var field = Game(h).Field;
        // з «Ящірок» (місця 1 і 3) вилітає одна — «Вужам» очко, раунд триває
        field.Heads[1].X = CurveCore.R + 1;
        field.Heads[1].A = Math.PI;
        h.Tick();
        var s = h.View(0).GetProperty("scores");
        Assert.Equal(1, s[0].GetInt32());
        Assert.Equal(1, s[2].GetInt32());
        Assert.Equal(0, s[1].GetInt32());
        Assert.Equal("play", h.View(0).GetProperty("phase").GetString());

        // «Вуж» 0 вилітає — очко «Ящіркам», бо місце 3 ще живе
        field.Heads[0].X = CurveCore.R + 1;
        field.Heads[0].Y = 20;
        field.Heads[0].A = Math.PI;
        h.Tick();
        Assert.Equal(1, h.View(0).GetProperty("scores")[3].GetInt32());

        // і остання «Ящірка» — лишились самі «Вужі»: раунд закінчено
        field.Heads[3].X = field.W - CurveCore.R - 1;
        field.Heads[3].A = 0;
        h.Tick();
        Assert.Equal("between", h.View(0).GetProperty("phase").GetString());
        Assert.Equal(2, h.View(0).GetProperty("scores")[2].GetInt32());
    }

    [Fact]
    public void When_a_whole_team_leaves_the_other_team_wins()
    {
        var h = Table(4, new { teams = "1" });
        h.Tick(CurveCore.ReadyTicks);
        h.Leave(Nicks[1]);
        Assert.Empty(h.Finished);
        h.Leave(Nicks[3]);
        var fin = Assert.Single(h.Finished);
        Assert.Contains("Вужі", fin.Result.Text);
        Assert.Equal([0, 2], fin.Result.Winners);
    }
}
