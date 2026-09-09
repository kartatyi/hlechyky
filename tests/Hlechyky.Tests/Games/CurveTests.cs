using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Кривуля: спершу ядро (<see cref="CurveCore"/>) — рух, повороти, стіни, власний слід і дірки, —
/// потім та сама гра через кімнату, як її побачать люди (TESTING.md §4).
/// </summary>
public class CurveTests
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван"];

    // ---------- ядро ----------

    /// <summary>Одна кривуля на відомому місці й з відомим курсом: усе інше в раунді нам заважає.</summary>
    static CurveCore Solo(double x = 150, double y = 100, double a = 0, bool gaps = false)
    {
        var core = new CurveCore(new Random(1), gaps);
        core.Reset([true]);
        Put(core, 0, x, y, a);
        return core;
    }

    static void Put(CurveCore core, int seat, double x, double y, double a)
    {
        var h = core.Heads[seat];
        h.X = x;
        h.Y = y;
        h.A = a;
        h.Turn = 0;
        h.Trail.Clear();
        h.Trail.Add(new CurvePoint(x, y, false));
    }

    /// <summary>
    /// Найгірший слід, який узагалі допускає запобіжник: кривуля в'ється всі <see cref="CurveCore.MaxRoundTicks"/>
    /// тиків, жодна точка не лягає на пряму (стискати нічого), і кожні 2 с — дірка.
    /// </summary>
    static List<CurvePoint> Wiggly(int seat)
    {
        var trail = new List<CurvePoint>(CurveCore.MaxRoundTicks);
        for (var t = 0; t < CurveCore.MaxRoundTicks; t++)
            trail.Add(new CurvePoint(
                150 + 140 * Math.Sin((t + seat) * 0.11),
                100 + 90 * Math.Sin((t + seat) * 0.37),
                t % 60 < CurveCore.GapTicks));
        return trail;
    }

    [Fact]
    public void A_curve_goes_forty_units_in_twenty_five_ticks()
    {
        var core = Solo();
        for (var i = 0; i < 25; i++) core.Step();

        Assert.Equal(190, core.Heads[0].X, 6);   // 150 + 40
        Assert.Equal(100, core.Heads[0].Y, 6);
        Assert.True(core.Heads[0].Alive);
    }

    [Fact]
    public void Holding_a_turn_for_twenty_five_ticks_is_half_a_circle()
    {
        var core = Solo();
        core.Heads[0].Turn = 1;
        for (var i = 0; i < 25; i++) core.Step();

        Assert.Equal(Math.PI, core.Heads[0].A, 9);
    }

    [Fact]
    public void A_curve_that_lets_go_of_the_turn_goes_straight_again()
    {
        var core = Solo();
        core.Heads[0].Turn = 1;
        core.Step();
        core.Heads[0].Turn = 0;
        var was = core.Heads[0].A;
        for (var i = 0; i < 10; i++) core.Step();

        Assert.Equal(was, core.Heads[0].A, 9);
    }

    [Fact]
    public void The_wall_is_death()
    {
        var core = Solo(x: 10, y: 100, a: Math.PI);   // носом у ліву стіну
        var died = new List<int>();
        for (var i = 0; i < 20 && died.Count == 0; i++) died = core.Step();

        Assert.Equal([0], died);
        Assert.False(core.Heads[0].Alive);
    }

    [Fact]
    public void A_straight_run_never_kills_a_curve_on_its_own_fresh_tail()
    {
        var core = Solo(x: 10, y: 100);
        for (var i = 0; i < 170; i++) Assert.Empty(core.Step());

        Assert.True(core.Heads[0].Alive);
        Assert.True(core.Heads[0].X > 280);
    }

    [Fact]
    public void A_full_circle_brings_a_curve_into_its_own_trail()
    {
        var core = Solo();
        core.Heads[0].Turn = 1;
        var died = -1;
        for (var t = 1; t <= 200 && died < 0; t++)
            if (core.Step().Count > 0) died = t;

        Assert.InRange(died, 40, 200);
        Assert.False(core.Heads[0].Alive);
    }

    [Fact]
    public void A_hole_in_the_trail_lets_the_curve_through()
    {
        var plain = Solo();
        plain.Heads[0].Turn = 1;
        var deadAt = 0;
        for (var t = 1; t <= 200 && deadAt == 0; t++)
            if (plain.Step().Count > 0) deadAt = t;
        Assert.True(deadAt > 0);

        var holed = Solo();
        holed.Heads[0].Turn = 1;
        // Дірка з першого ж тика: перехрестя, на якому гине суцільний слід, лишається порожнім.
        holed.Heads[0].Trail[0] = new CurvePoint(holed.Heads[0].X, holed.Heads[0].Y, true);
        holed.Heads[0].GapLeft = 12;
        for (var t = 1; t <= deadAt; t++) holed.Step();

        Assert.True(holed.Heads[0].Alive, $"суцільний слід убив на {deadAt} тику, дірка мала пропустити");
    }

    [Fact]
    public void Two_heads_that_meet_face_to_face_both_die()
    {
        var core = new CurveCore(new Random(1), gaps: false);
        core.Reset([true, true]);
        Put(core, 0, 140, 100, 0);
        Put(core, 1, 150, 100, Math.PI);

        var died = new List<int>();
        for (var i = 0; i < 10 && died.Count == 0; i++) died = core.Step();

        Assert.Equal([0, 1], died);
    }

    [Fact]
    public void A_curve_that_catches_up_from_behind_dies_alone()
    {
        var core = new CurveCore(new Random(1), gaps: false);
        core.Reset([true, true]);
        Put(core, 0, 100, 100, 0);      // лідер їде праворуч і нічого не робить
        Put(core, 1, 96.5, 100, 0);     // задній їде туди ж і в'їхав йому в спину

        Assert.Equal([1], core.Step());
        Assert.True(core.Heads[0].Alive, "лідера, якого наздогнали ззаду, не за що вбивати");
    }

    [Fact]
    public void Two_curves_riding_side_by_side_do_not_bump_each_other()
    {
        var core = new CurveCore(new Random(1), gaps: false);
        core.Reset([true, true]);
        Put(core, 0, 100, 100, 0);
        Put(core, 1, 100, 103.5, 0);    // пліч-о-пліч, ближче ніж 2r, але одне одному не спереду

        Assert.Empty(core.Step());
        Assert.Equal(2, core.AliveCount);
    }

    [Fact]
    public void Someone_elses_trail_kills_just_as_well_as_your_own()
    {
        var core = new CurveCore(new Random(1), gaps: false);
        core.Reset([true, true]);
        Put(core, 0, 100, 100, 0);            // їде праворуч і стелить слід
        Put(core, 1, 120, 60, Math.PI / 2);   // падає згори на цей слід
        core.Heads[1].Turn = 0;

        var died = new List<int>();
        for (var i = 0; i < 40 && !died.Contains(1); i++) died = core.Step();

        Assert.Contains(1, died);
        Assert.True(core.Heads[0].Alive);
    }

    [Fact]
    public void Four_curves_start_away_from_the_walls_and_from_each_other()
    {
        for (var seed = 1; seed <= 40; seed++)
        {
            var core = new CurveCore(new Random(seed));
            core.Reset([true, true, true, true]);
            var heads = core.Heads;
            foreach (var h in heads)
            {
                Assert.InRange(h.X, 30, CurveCore.W - 30);
                Assert.InRange(h.Y, 30, CurveCore.H - 30);
            }
            for (var a = 0; a < 4; a++)
                for (var b = a + 1; b < 4; b++)
                {
                    var (dx, dy) = (heads[a].X - heads[b].X, heads[a].Y - heads[b].Y);
                    Assert.True(Math.Sqrt(dx * dx + dy * dy) >= 20, $"сід {seed}: {a} і {b} сіли впритул");
                }
        }
    }

    [Fact]
    public void Holes_come_every_two_to_four_seconds_and_last_a_quarter()
    {
        var core = new CurveCore(new Random(5));
        core.Reset([true]);
        var flags = new List<bool>();
        for (var t = 0; t < 500; t++)
        {
            // Слід щоразу забуваємо: нас цікавить лише ритм дірок, а не те, обо що ця кривуля вб'ється.
            Put(core, 0, 150, 100, 0);
            core.Step();
            flags.Add(core.Heads[0].Gap);
        }

        var runs = new List<(bool On, int Len)>();
        foreach (var f in flags)
            if (runs.Count > 0 && runs[^1].On == f) runs[^1] = (f, runs[^1].Len + 1);
            else runs.Add((f, 1));

        // Останній пробіг обрізаний кінцем вимірювання — його не рахуємо.
        runs.RemoveAt(runs.Count - 1);
        Assert.Contains(runs, r => r.On);
        foreach (var r in runs.Where(r => r.On)) Assert.Equal(CurveCore.GapTicks, r.Len);
        foreach (var r in runs.Where(r => !r.On))
            Assert.InRange(r.Len, CurveCore.GapMinTicks - 1, CurveCore.GapMaxTicks);
    }

    [Fact]
    public void The_same_seed_plays_the_same_round()
    {
        static string Play(int seed)
        {
            var core = new CurveCore(new Random(seed));
            core.Reset([true, true, true, true]);
            for (var t = 0; t < 120; t++)
            {
                if (t == 10) core.Turn(0, 1);
                if (t == 30) core.Turn(1, -1);
                if (t == 50) core.Turn(0, 0);
                core.Step();
            }
            return string.Join("|", core.Heads.Select(h => $"{h.X:F6},{h.Y:F6},{h.A:F6},{h.Alive}"));
        }

        Assert.Equal(Play(7), Play(7));
        Assert.NotEqual(Play(7), Play(8));
    }

    [Fact]
    public void A_polyline_collapses_a_straight_run_and_keeps_the_holes()
    {
        var trail = new List<CurvePoint>();
        for (var i = 0; i < 60; i++) trail.Add(new CurvePoint(10 + i * 1.6, 100, i is >= 20 and < 26));
        var (pts, gaps) = CurveCore.Polyline(trail);

        Assert.True(pts.Length / 2 < 20, $"пряма з 60 точок стиснулась лише до {pts.Length / 2}");
        Assert.Equal(6, gaps.Length);
        Assert.Equal(10, pts[0]);
        Assert.Equal(100, pts[1]);
        Assert.Equal(104, pts[^2]);          // 10 + 59 * 1.6 = 104.4
        foreach (var i in gaps) Assert.InRange(i, 1, pts.Length / 2 - 1);
    }

    [Fact]
    public void A_polyline_of_the_longest_round_is_thinned_to_the_budget()
    {
        var (pts, gaps) = CurveCore.Polyline(Wiggly(0));

        Assert.InRange(pts.Length / 2, CurveCore.MaxPts / 2, CurveCore.MaxPts + 1);   // тут прорідження таки спрацювало
        Assert.NotEmpty(gaps);                                  // дірки з викинутих точок не губляться
        foreach (var i in gaps) Assert.InRange(i, 0, pts.Length / 2 - 1);
    }

    [Fact]
    public void An_empty_table_does_not_touch_the_dice()
    {
        static string Deal(bool peeked)
        {
            var rng = new Random(7);
            if (peeked)
            {
                // Стіл у лобі теж просить поле для вида — і не має зсувати роздачу.
                var idle = new CurveCore(rng);
                idle.Reset(new bool[CurveCore.Seats]);
            }
            var core = new CurveCore(rng);
            core.Reset([true, true, true, true]);
            return string.Join("|", core.Heads.Select(h => $"{h.X:F6},{h.Y:F6},{h.A:F6}"));
        }

        Assert.Equal(Deal(false), Deal(true));
    }

    [Fact]
    public void Stopping_the_round_kills_everyone_who_still_rides()
    {
        var core = new CurveCore(new Random(1), gaps: false);
        core.Reset([true, true, true]);
        core.Heads[1].Alive = false;

        Assert.Equal([0, 2], core.StopAll());
        Assert.Equal(0, core.AliveCount);
        Assert.Empty(core.StopAll());
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Four_curves_for_two_and_a_half_thousand_ticks_are_fast()
    {
        var core = new CurveCore(new Random(11));
        core.Reset([true, true, true, true]);
        var sw = Stopwatch.StartNew();
        for (var t = 0; t < 2500; t++)
        {
            // Кривулі гинуть — щоразу піднімаємо їх назад, щоб міряти повне навантаження на чотирьох.
            for (var s = 0; s < 4; s++)
            {
                var h = core.Heads[s];
                if (!h.Alive) { h.Alive = true; Put(core, s, 40 + s * 60, 40 + s * 30, s); }
                h.Turn = (t / 17 + s) % 3 - 1;
            }
            core.Step();
        }
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"2500 тиків зайняли {sw.Elapsed}");
    }

    // ---------- кімната ----------

    static RoomHarness Table(int players = 2, int seed = 42)
    {
        var h = new RoomHarness("curve", seed: seed);
        for (var i = 0; i < players; i++) h.Join(Nicks[i]);
        h.Start();
        return h;
    }

    static CurveCore Field(RoomHarness h) => ((CurveGame)h.Room.Game).Field;

    /// <summary>Поставити кривулю носом у стіну: наступний тик — і місце вибуло.</summary>
    static void Doom(RoomHarness h, int seat)
    {
        var head = Field(h).Heads[seat];
        head.X = CurveCore.R + 1;
        head.Y = CurveCore.H / 2.0;
        head.A = Math.PI;
        head.Turn = 0;
    }

    /// <summary>Дотикати «Готуйсь» до кінця — далі кривулі вже їдуть.</summary>
    static void Ready(RoomHarness h) => h.Tick(CurveCore.ReadyTicks);

    [Fact]
    public void A_table_waits_for_the_host_and_only_then_rides()
    {
        var h = new RoomHarness("curve");
        h.Join("Оля");
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);

        h.Join("Петро");
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);   // ByHost: повний стіл сам не стартує

        Assert.True(h.Start().Ok);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("ready", h.View(0).GetProperty("phase").GetString());
    }

    [Fact]
    public void Nobody_moves_while_the_countdown_runs()
    {
        var h = Table();
        var was = h.View(0).GetProperty("heads").ToString();
        h.Tick(CurveCore.ReadyTicks - 1);

        Assert.Equal("ready", h.View(0).GetProperty("phase").GetString());
        Assert.Equal(was, h.View(0).GetProperty("heads").ToString());
        Assert.Equal(CurveCore.TickMs, h.View(0).GetProperty("startIn").GetInt32());

        h.Tick(1);
        Assert.Equal("play", h.View(0).GetProperty("phase").GetString());
        Assert.Equal(0, h.View(0).GetProperty("startIn").GetInt32());
    }

    [Fact]
    public void Every_tick_sends_a_frame_and_a_death_sends_the_views_too()
    {
        var h = Table();
        h.Tick(1);
        Assert.Single(h.Outbox.OfType<RoomFrame>());
        Assert.Equal(3, h.Outbox.OfType<RoomViews>().Count());   // два входи й «Почати»; тик шле лише кадр

        Ready(h);
        var views = h.Outbox.OfType<RoomViews>().Count();
        Doom(h, 1);
        h.Tick(1);
        Assert.True(h.Outbox.OfType<RoomViews>().Count() > views, "смерть має оновити повні види");
    }

    [Fact]
    public void The_frame_carries_exactly_what_the_client_draws()
    {
        var h = Table();
        h.Tick(1);
        var f = Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);

        foreach (var name in new[] { "t", "r", "heads", "s", "phase", "startIn" })
            Assert.True(Views.Has(f, name), name);
        Assert.Equal(CurveCore.Seats, f.GetProperty("heads").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, f.GetProperty("heads")[2].ValueKind);   // місця 3 і 4 вільні
        var head = f.GetProperty("heads")[0];
        foreach (var name in new[] { "x", "y", "a", "alive", "gap" })
            Assert.True(Views.Has(head, name), name);
        Assert.True(head.GetProperty("alive").GetBoolean());
        Assert.Equal(1, f.GetProperty("r").GetInt32());
        Assert.True(Views.Text(h.Outbox.OfType<RoomFrame>().Last().Frame).Length < 400, "кадр має лишатись компактним");
    }

    [Fact]
    public void The_view_carries_the_polylines_and_the_score()
    {
        var h = Table(3);
        Ready(h);
        h.Tick(40);

        var v = h.View(1);
        foreach (var name in new[] { "width", "height", "turn", "round", "target", "phase", "startIn", "scores", "heads", "segments", "winners" })
            Assert.True(Views.Has(v, name), name);
        Assert.Equal(CurveCore.W, v.GetProperty("width").GetInt32());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("turn").ValueKind);
        Assert.Equal(20, v.GetProperty("target").GetInt32());          // 10 × (3 − 1)
        Assert.Equal(CurveCore.Seats, v.GetProperty("segments").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("segments")[3].ValueKind);

        var pts = v.GetProperty("segments")[0].GetProperty("pts");
        Assert.True(pts.GetArrayLength() >= 4 && pts.GetArrayLength() % 2 == 0);
        Assert.True(Views.Has(v.GetProperty("segments")[0], "gaps"));
        // Той самий вид усім: у кривулі ховати нема чого.
        Assert.Equal(Views.Text(h.Room.Game.View(null)), Views.Text(h.Room.Game.View(1)));
    }

    [Fact]
    public void The_longest_round_of_four_still_fits_into_the_view()
    {
        var h = Table(4);
        Ready(h);
        h.Tick(10);
        // Підміняємо сліди найгіршими, які дозволяє запобіжник: наживо четверо стільки не проживуть,
        // але межу «≤ 32 КБ при 4 гравцях» вид має тримати й тоді.
        for (var s = 0; s < 4; s++)
        {
            var trail = Field(h).Heads[s].Trail;
            trail.Clear();
            trail.AddRange(Wiggly(s));
        }

        var v = h.View(null);
        for (var s = 0; s < 4; s++)
            Assert.InRange(v.GetProperty("segments")[s].GetProperty("pts").GetArrayLength() / 2, 2, CurveCore.MaxPts + 1);
        var text = Views.Text(h.Room.Game.View(null));
        Assert.True(text.Length < 32 * 1024, $"вид роздувся до {text.Length} байтів");
    }

    [Fact]
    public void An_ordinary_round_keeps_every_bend_of_the_trail()
    {
        var h = Table(4);
        Ready(h);
        for (var i = 0; i < 4; i++) h.Input(i, "turn", new { d = i % 2 == 0 ? 1 : -1 });
        h.Tick(1200);

        var text = Views.Text(h.Room.Game.View(null));
        Assert.True(text.Length < 32 * 1024, $"вид роздувся до {text.Length} байтів");
        // Звичайний раунд у проріджування не впирається: у ньому точок на порядок менше за бюджет.
        foreach (var seg in h.View(null).GetProperty("segments").EnumerateArray())
            if (seg.ValueKind != JsonValueKind.Null)
                Assert.True(seg.GetProperty("pts").GetArrayLength() / 2 < CurveCore.MaxPts, "звичайний раунд не мали прорідити");
    }

    [Fact]
    public void A_turn_arrives_as_d_and_only_from_a_real_seat()
    {
        var h = Table();
        Ready(h);
        h.Input(0, "turn", new { d = 1 });
        Assert.Equal(1, Field(h).Heads[0].Turn);

        h.Input(0, "turn", new { d = 0 });
        Assert.Equal(0, Field(h).Heads[0].Turn);

        h.Input(0, "turn", new { d = -5 });          // з дроту прийде всяке
        Assert.Equal(-1, Field(h).Heads[0].Turn);

        h.Rooms.Input(h.RoomId, "Чужий", "turn", Views.Payload(new { d = 1 }));
        Assert.Equal(-1, Field(h).Heads[0].Turn);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void The_payload_the_module_sends_is_the_payload_the_game_reads()
    {
        var h = Table();
        Ready(h);
        // Модуль шле рівно { d }; чужа назва поля не має мовчки нікуди повертати.
        h.Input(0, "turn", new { dir = 1 });
        Assert.Equal(0, Field(h).Heads[0].Turn);

        Assert.False(h.Act(0, "turn", new { dir = 1 }).Ok);
        Assert.False(h.Act(0, "move", new { d = 1 }).Ok);
        Assert.True(h.Act(0, "turn", new { d = 1 }).Ok);
        Assert.Equal(1, Field(h).Heads[0].Turn);
    }

    [Fact]
    public void A_death_gives_a_point_to_everyone_still_riding()
    {
        var h = Table(3);
        Ready(h);

        Doom(h, 2);
        h.Tick(1);
        Assert.Equal([1, 1, 0, 0], h.View(0).GetProperty("scores").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        Assert.Equal("play", h.View(0).GetProperty("phase").GetString());   // двоє живих — раунд триває

        Doom(h, 1);
        h.Tick(1);
        Assert.Equal([2, 1, 0, 0], h.View(0).GetProperty("scores").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        Assert.Equal("between", h.View(0).GetProperty("phase").GetString());
    }

    [Fact]
    public void After_the_pause_a_clean_round_starts()
    {
        var h = Table();
        Ready(h);
        Doom(h, 1);
        h.Tick(1);
        Assert.Equal(1, h.View(0).GetProperty("round").GetInt32());
        Assert.Equal(CurveCore.BetweenTicks * CurveCore.TickMs, h.View(0).GetProperty("startIn").GetInt32());

        h.Tick(CurveCore.BetweenTicks);
        Assert.Equal(2, h.View(0).GetProperty("round").GetInt32());
        Assert.Equal("ready", h.View(0).GetProperty("phase").GetString());
        Assert.Equal(2, Field(h).AliveCount);
        Assert.Equal(2, h.View(0).GetProperty("segments")[0].GetProperty("pts").GetArrayLength());
        Assert.Equal([1, 0, 0, 0], h.View(0).GetProperty("scores").EnumerateArray().Select(x => x.GetInt32()).ToArray());
    }

    [Fact]
    public void The_game_runs_until_ten_points_per_rival()
    {
        var h = Table();
        Assert.Equal(CurveGame.PerRival, h.View(0).GetProperty("target").GetInt32());

        for (var round = 1; round <= CurveGame.PerRival; round++)
        {
            Ready(h);
            Doom(h, 1);
            h.Tick(1);
            if (round < CurveGame.PerRival) h.Tick(CurveCore.BetweenTicks);
        }

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Equal("done", h.View(0).GetProperty("phase").GetString());
        Assert.Equal("Кривуля: Оля жовта 10, Петро зелена 0", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Single(h.Finished);
    }

    [Fact]
    public void A_finished_game_stops_ticking()
    {
        var h = Table();
        for (var round = 1; round <= CurveGame.PerRival; round++)
        {
            Ready(h);
            Doom(h, 1);
            h.Tick(1);
            if (round < CurveGame.PerRival) h.Tick(CurveCore.BetweenTicks);
        }
        var frames = h.Outbox.OfType<RoomFrame>().Count();
        h.Tick(50);

        Assert.Equal(frames, h.Outbox.OfType<RoomFrame>().Count());
    }

    [Fact]
    public void Rematch_deals_a_clean_game_with_the_seats_swapped()
    {
        var h = Table();
        for (var round = 1; round <= CurveGame.PerRival; round++)
        {
            Ready(h);
            Doom(h, 1);
            h.Tick(1);
            if (round < CurveGame.PerRival) h.Tick(CurveCore.BetweenTicks);
        }

        Assert.True(h.Rematch("Оля").Ok);
        Assert.Equal("Оля", h.Room.Seats[1]);          // «Ще раз» обертає місця
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(1, h.View(0).GetProperty("round").GetInt32());
        Assert.Equal("ready", h.View(0).GetProperty("phase").GetString());
        Assert.Equal([0, 0, 0, 0], h.View(0).GetProperty("scores").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("winners").ValueKind);
    }

    [Fact]
    public void Leaving_a_duel_ends_the_game()
    {
        var h = Table();
        Ready(h);
        h.Leave("Петро");

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Contains("лишився сам", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void Leaving_a_crowd_only_puts_out_that_one_curve()
    {
        var h = Table(4);
        Ready(h);
        h.Tick(5);
        h.Leave("Іван");

        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.False(Field(h).Heads[3].Alive);
        Assert.Equal(3, Field(h).AliveCount);
        Assert.False(h.View(0).GetProperty("heads")[3].GetProperty("alive").GetBoolean());
        // Слід того, хто пішов, лишається в растрі до кінця раунду і далі вбиває — тож він має
        // лишатись і на екрані, інакше решта гине об порожнє місце.
        var gone = Field(h).Heads[3].Trail[0];
        Assert.NotEqual(0, Field(h).Cell((int)gone.X, (int)gone.Y));
        var seg = h.View(0).GetProperty("segments")[3];
        Assert.NotEqual(JsonValueKind.Null, seg.ValueKind);
        Assert.True(seg.GetProperty("pts").GetArrayLength() >= 2);

        // А от у наступному раунді місця вже нема.
        Doom(h, 1);
        Doom(h, 2);
        h.Tick(1);
        h.Tick(CurveCore.BetweenTicks);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(2, h.View(0).GetProperty("round").GetInt32());
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("segments")[3].ValueKind);
    }

    [Fact]
    public void The_target_shrinks_with_the_table()
    {
        var h = Table(4);
        Assert.Equal(CurveGame.PerRival * 3, h.View(0).GetProperty("target").GetInt32());

        h.Leave("Ганна");
        Assert.Equal(CurveGame.PerRival * 2, h.View(0).GetProperty("target").GetInt32());

        h.Leave("Іван");
        Assert.Equal(CurveGame.PerRival, h.View(0).GetProperty("target").GetInt32());
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void A_leader_who_already_passed_the_smaller_target_wins_at_once()
    {
        var h = Table(4);
        for (var round = 1; round <= 4; round++)
        {
            Ready(h);
            for (var s = 1; s <= 3; s++) Doom(h, s);
            h.Tick(1);                                  // +3 Олі за раунд
            h.Tick(CurveCore.BetweenTicks);
        }
        Assert.Equal(RoomStatus.Playing, h.Room.Status);   // 12 очок із 30 — ще грати й грати

        h.Leave("Ганна");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);   // на трьох треба 20

        h.Leave("Іван");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);  // на двох треба 10, а вже 12
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Contains("Оля жовта 12", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void A_round_that_drags_on_closes_itself()
    {
        var h = Table();
        Ready(h);
        for (var t = 0; t < CurveCore.MaxRoundTicks + 1; t++)
        {
            // Тримаємо кривуль у вічній дірці посеред поля: слід не пишеться, гинути нема об що.
            for (var s = 0; s < 2; s++)
            {
                var head = Field(h).Heads[s];
                head.X = 100 + s * 60;
                head.Y = 100;
                head.A = 0;
                head.GapLeft = CurveCore.MaxRoundTicks * 2;
                head.Trail.Clear();
            }
            h.Tick(1);
        }

        Assert.Equal("between", h.View(0).GetProperty("phase").GetString());
        Assert.Equal([0, 0, 0, 0], h.View(0).GetProperty("scores").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void Curve_is_in_the_catalog_as_a_live_game_for_two_to_four()
    {
        var game = Assert.Single(new Registry().Catalog, g => g.Id == "curve");

        Assert.Equal("live", game.Group);
        Assert.Equal("byHost", game.Start);
        Assert.Equal(2, game.MinPlayers);
        Assert.Equal(4, game.MaxPlayers);
        Assert.Equal(CurveCore.TickMs, game.TickMs);
        Assert.False(game.Rated);            // ставки тут неможливі: їх дають лише рейтинговим іграм на двох
        Assert.Equal("curve", game.Module);
        Assert.NotEmpty(game.Hint);
    }

    [Fact]
    public void Seat_names_are_the_four_colours()
    {
        var h = Table(4);
        Assert.Equal(["жовта", "зелена", "глиняна", "біла"], h.Room.Summary().SeatNames);
    }
}
