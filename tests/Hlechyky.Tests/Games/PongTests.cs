using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Понг: спершу фізика <see cref="PongCore"/> (відбої, кути, швидкість, голи) — там її видно очима й без
/// кімнати, — а потім те, що бачить браузер: кадри, види, Журнал, рематч (TESTING.md §4).
/// </summary>
public class PongTests
{
    // ---------- підмостки ----------

    static RoomHarness Table(int seed = 42)
    {
        var h = new RoomHarness("pong", seed: seed);
        h.Join("Оля");
        h.Join("Петро");
        return h;
    }

    /// <summary>Пропустити «готуйсь», щоб дійти до м'яча.</summary>
    static void Ready(RoomHarness h) => h.Tick(PongCore.StartTicks);

    static JsonElement LastFrame(RoomHarness h) => Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);

    /// <summary>
    /// Догати партію до кінця: ліва ганяється за м'ячем (цілиться трохи вище центру, щоб віддавати під
    /// кутом і не зациклитись на горизонтальному пінг-понзі), права стоїть стовпом.
    /// </summary>
    static void PlayOut(RoomHarness h, int limit = 8000)
    {
        for (var i = 0; i < limit && h.Room.Status == RoomStatus.Playing; i++)
        {
            h.Tick(1);
            if (h.Room.Status != RoomStatus.Playing) break;
            h.Input(0, "to", new { y = LastFrame(h).GetProperty("by").GetDouble() - 4 });
        }
    }

    /// <summary>Ядро посеред розіграшу: «готуйсь» позаду, м'яч там, де його поставив тест.</summary>
    static PongCore Rally(int seed = 1)
    {
        var core = new PongCore(new Random(seed));
        core.Reset();
        core.StartIn = 0;
        return core;
    }

    static void Near(double expected, double actual, double eps = 1e-6) =>
        Assert.True(Math.Abs(expected - actual) <= eps, $"чекали {expected}, отримали {actual}");

    /// <summary>Куди летить м'яч, у градусах від горизонталі.</summary>
    static double Angle(PongCore core) => Math.Atan2(core.Vy, Math.Abs(core.Vx)) * 180 / Math.PI;

    // ---------- фізика ----------

    [Fact]
    public void Reset_puts_both_paddles_and_the_ball_in_the_middle()
    {
        var core = new PongCore(new Random(1));
        core.Reset();

        Near(PongCore.H / 2, core.P[0]);
        Near(PongCore.H / 2, core.P[1]);
        Near(PongCore.W / 2, core.Bx);
        Near(PongCore.H / 2, core.By);
        Near(0, core.Speed);
        Assert.Equal(PongCore.StartTicks, core.StartIn);
        Assert.Equal(0, core.S[0] + core.S[1]);
    }

    [Fact]
    public void A_ball_into_the_middle_of_a_paddle_comes_straight_back()
    {
        var core = Rally();
        (core.Bx, core.By, core.Vx, core.Vy) = (8, 50, -PongCore.StartSpeed, 0);
        core.P[0] = 50;

        core.Step();

        Assert.True(core.Vx > 0, "м'яч мав полетіти назад праворуч");
        Near(0, core.Vy, 1e-9);
        Near(0, Angle(core), 1e-9);
    }

    [Fact]
    public void A_ball_into_the_edge_of_a_paddle_leaves_at_sixty_degrees()
    {
        var core = Rally();
        // Попадання рівно в нижній край: центр ракетки на 50, край — на 59.
        (core.Bx, core.By, core.Vx, core.Vy) = (8, 50 + PongCore.PaddleH / 2, -PongCore.StartSpeed, 0);
        core.P[0] = 50;

        core.Step();

        Near(PongCore.BounceAngle, Angle(core), 1e-6);
        Assert.True(core.Vx > 0);
        Assert.True(core.Vy > 0, "нижній край мав відправити м'яч униз");
    }

    [Fact]
    public void The_right_paddle_is_a_mirror_of_the_left()
    {
        var core = Rally();
        (core.Bx, core.By, core.Vx, core.Vy) = (PongCore.W - 8, 50 - PongCore.PaddleH / 2, PongCore.StartSpeed, 0);
        core.P[1] = 50;

        core.Step();

        Assert.True(core.Vx < 0, "від правої ракетки м'яч має піти ліворуч");
        Near(-PongCore.BounceAngle, Angle(core), 1e-6);
    }

    [Fact]
    public void The_top_and_the_bottom_mirror_the_ball()
    {
        var core = Rally();
        (core.Bx, core.By, core.Vx, core.Vy) = (80, 2, 0, -PongCore.StartSpeed);
        core.Step();
        Assert.True(core.Vy > 0, "від верху м'яч мав відскочити вниз");
        Assert.True(core.By >= PongCore.BallR, $"м'яч виліз за верх: {core.By}");

        (core.By, core.Vy) = (PongCore.H - 2, PongCore.StartSpeed);
        core.Step();
        Assert.True(core.Vy < 0, "від низу м'яч мав відскочити вгору");
        Assert.True(core.By <= PongCore.H - PongCore.BallR, $"м'яч виліз за низ: {core.By}");
    }

    [Fact]
    public void Every_bounce_off_a_paddle_adds_six_percent()
    {
        var core = Rally();
        (core.Bx, core.By, core.Vx, core.Vy) = (8, 50, -PongCore.StartSpeed, 0);
        core.P[0] = 50;

        core.Step();

        Near(PongCore.StartSpeed * PongCore.SpeedUp, core.Speed, 1e-6);
    }

    [Fact]
    public void Speed_never_climbs_over_the_ceiling()
    {
        var core = Rally();
        (core.Bx, core.By, core.Vx, core.Vy) = (8, 50, -(PongCore.MaxSpeed - 1), 0);
        core.P[0] = 50;

        core.Step();

        Near(PongCore.MaxSpeed, core.Speed, 1e-6);
    }

    [Fact]
    public void A_fast_ball_does_not_tunnel_through_a_paddle()
    {
        var core = Rally();
        // 160 од/с — це 6.4 одиниці за тик: кінцева точка вже за ракеткою, і без перевірки
        // відрізка руху м'яч пройшов би наскрізь.
        (core.Bx, core.By, core.Vx, core.Vy) = (8, 50, -PongCore.MaxSpeed, 0);
        core.P[0] = 50;

        core.Step();

        Assert.True(core.Vx > 0, $"м'яч прошив ракетку наскрізь: bx={core.Bx}");
        Assert.True(core.Bx > PongCore.Plane(0));
    }

    [Fact]
    public void A_ball_that_bounced_off_the_ceiling_still_reaches_the_paddle()
    {
        var core = Rally();
        core.P[0] = 10;                  // ракетка вгорі: накриває y від 1 до 19
        // За один тик м'яч спершу дістає стелі, а вже потім площини ракетки: пряма з початку в кінець
        // проходить вище поля, хоча насправді м'яч перетинає площину по середині ракетки.
        (core.Bx, core.By, core.Vx, core.Vy) = (9.5, 2.0, -100, -110);

        core.Step();

        Assert.True(core.Vx > 0, $"відскочив від стелі просто в ракетку, а йому зарахували промах: by={core.By}");
    }

    [Fact]
    public void A_ball_that_bounced_off_the_floor_still_reaches_the_paddle()
    {
        var core = Rally();
        core.P[0] = 90;                  // дзеркальний випадок: ракетка внизу
        (core.Bx, core.By, core.Vx, core.Vy) = (9.5, PongCore.H - 2.0, -100, 110);

        core.Step();

        Assert.True(core.Vx > 0, $"відскочив від підлоги просто в ракетку, а йому зарахували промах: by={core.By}");
    }

    [Fact]
    public void A_ball_that_grazes_the_very_corner_of_a_paddle_still_comes_back()
    {
        var core = Rally();
        core.P[0] = 50;
        // Рівно на межі зони попадання: половина ракетки плюс радіус м'яча.
        (core.Bx, core.By, core.Vx, core.Vy) =
            (8, 50 + PongCore.PaddleH / 2 + PongCore.BallR - 1e-6, -PongCore.StartSpeed, 0);

        core.Step();

        Assert.True(core.Vx > 0, "м'яч зачепив ракетку самим краєм — це відбій, а не гол");
        Near(PongCore.BounceAngle, Angle(core), 1e-6);   // край віддає рівно 60°, не більше
    }

    [Fact]
    public void A_ball_just_past_the_corner_of_a_paddle_is_a_goal()
    {
        var core = Rally();
        core.P[0] = 50;
        (core.Bx, core.By, core.Vx, core.Vy) =
            (8, 50 + PongCore.PaddleH / 2 + PongCore.BallR + 0.5, -PongCore.StartSpeed, 0);

        for (var i = 0; i < 10 && core.S[1] == 0; i++) core.Step();

        Assert.Equal(1, core.S[1]);
    }

    [Fact]
    public void A_ball_past_the_left_edge_is_a_point_for_the_right()
    {
        var core = Rally();
        (core.Bx, core.By, core.Vx, core.Vy) = (8, 90, -PongCore.MaxSpeed, 0);
        core.P[0] = 50;                       // ракетка далеко: рятувати нікому

        Assert.Null(core.Step());             // проминув ракетку, але з поля ще не вийшов
        var scorer = core.Step();

        Assert.Equal(1, scorer);
        Assert.Equal(1, core.S[1]);
        Assert.Equal(0, core.S[0]);
    }

    [Fact]
    public void After_a_goal_the_ball_waits_a_second_in_the_middle()
    {
        var core = Rally();
        (core.Bx, core.By, core.Vx, core.Vy) = (2, 90, -PongCore.MaxSpeed, 0);
        core.Step();

        Assert.Equal(PongCore.ServeTicks, core.ServeIn);
        Near(PongCore.W / 2, core.Bx);
        Near(PongCore.H / 2, core.By);
        Near(0, core.Speed);

        for (var i = 0; i < PongCore.ServeTicks - 1; i++) core.Step();
        Near(0, core.Speed);                  // усю паузу м'яч стоїть
        core.Step();
        Near(PongCore.StartSpeed, core.Speed, 1e-9);
    }

    [Fact]
    public void The_serve_goes_towards_the_one_who_missed()
    {
        var core = Rally();
        (core.Bx, core.By, core.Vx, core.Vy) = (2, 90, -PongCore.MaxSpeed, 0);
        core.Step();                          // пропустила ліва
        for (var i = 0; i < PongCore.ServeTicks; i++) core.Step();
        Assert.True(core.Vx < 0, "подавати мали в бік лівої");

        (core.Bx, core.By, core.Vx, core.Vy) = (PongCore.W - 2, 90, PongCore.MaxSpeed, 0);
        core.Step();                          // а тепер пропустила права
        for (var i = 0; i < PongCore.ServeTicks; i++) core.Step();
        Assert.True(core.Vx > 0, "подавати мали в бік правої");
    }

    [Fact]
    public void A_serve_never_leaves_the_thirty_degree_cone()
    {
        var core = new PongCore(new Random(3));
        for (var i = 0; i < 200; i++)
        {
            core.Launch(0);
            Near(PongCore.StartSpeed, core.Speed, 1e-9);
            Assert.True(Math.Abs(Angle(core)) <= PongCore.ServeAngle + 1e-9, $"кут подачі {Angle(core)}");
        }
    }

    [Fact]
    public void A_paddle_never_leaves_the_field()
    {
        var core = Rally();
        core.StartIn = 500;                   // хай працюють лише ракетки

        core.Move(0, 1);
        for (var i = 0; i < 200; i++) core.Step();
        Near(PongCore.MaxY, core.P[0]);

        core.Move(0, -1);
        for (var i = 0; i < 200; i++) core.Step();
        Near(PongCore.MinY, core.P[0]);
    }

    [Fact]
    public void A_finger_moves_the_paddle_no_faster_than_the_paddle_runs()
    {
        var core = Rally();
        core.StartIn = 500;
        core.P[0] = 50;
        core.Aim(0, PongCore.MaxY);           // палець смикнув аж у низ поля

        core.Step();

        Near(50 + PongCore.PaddleSpeed * PongCore.Dt, core.P[0]);   // 2.4 од за тик, не більше
        for (var i = 0; i < 100; i++) core.Step();
        Near(PongCore.MaxY, core.P[0]);       // але доїде, і далі стоятиме
    }

    [Fact]
    public void The_last_input_wins_over_the_previous_one()
    {
        var core = Rally();
        core.StartIn = 500;
        core.P[0] = 50;

        core.Move(0, 1);
        core.Step();
        core.Aim(0, 50);                      // палець сказав «стій отут» — утримання клавіші скасовано
        core.Step();
        core.Step();

        Near(50, core.P[0], 1e-9);
    }

    [Fact]
    public void The_same_seed_plays_the_same_game()
    {
        static string Play(int seed)
        {
            var core = new PongCore(new Random(seed));
            core.Reset();
            for (var i = 0; i < 400; i++)
            {
                if (i % 31 == 0) core.Move(0, i / 31 % 3 - 1);
                if (i % 23 == 0) core.Aim(1, i % 90);
                core.Step();
            }
            return $"{core.Bx:F6},{core.By:F6},{core.Vx:F6},{core.Vy:F6},{core.P[0]:F6},{core.P[1]:F6},{core.S[0]}:{core.S[1]}";
        }

        Assert.Equal(Play(17), Play(17));
        Assert.NotEqual(Play(17), Play(18));   // а різні сіди дають різні подачі
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Five_thousand_ticks_take_less_than_a_second()
    {
        var core = new PongCore(new Random(11));
        core.Reset();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 5000; i++)
        {
            if (i % 7 == 0) core.Aim(0, i % 100);
            if (i % 11 == 0) core.Move(1, i / 11 % 3 - 1);
            core.Step();
        }
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"5000 тиків зайняли {sw.Elapsed}");
    }

    // ---------- кімната ----------

    [Fact]
    public void A_table_waiting_for_a_rival_already_looks_like_a_field()
    {
        var h = new RoomHarness("pong");
        h.Join("Оля");

        var v = h.View(null);
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);
        Assert.Equal("ready", v.GetProperty("phase").GetString());
        Near(PongCore.W / 2, v.GetProperty("frame").GetProperty("bx").GetDouble());
        Near(PongCore.H / 2, v.GetProperty("frame").GetProperty("p")[0].GetDouble());
    }

    [Fact]
    public void The_view_has_the_shape_from_the_spec()
    {
        var h = Table();
        var v = h.View(0);

        foreach (var name in new[] { "scores", "phase", "startIn", "winner", "target", "turn", "frame" })
            Assert.True(Views.Has(v, name), name);
        Assert.Equal(PongCore.Target, v.GetProperty("target").GetInt32());
        Assert.Equal(2, v.GetProperty("scores").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("winner").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("turn").ValueKind);
    }

    [Fact]
    public void Nothing_is_hidden_from_anyone()
    {
        var h = Table();
        Ready(h);
        h.Tick(10);

        var mine = h.View(0).ToString();
        Assert.Equal(mine, h.View(1).ToString());
        Assert.Equal(mine, h.View(null).ToString());
    }

    [Fact]
    public void Countdown_runs_before_the_ball_moves()
    {
        var h = Table();
        Assert.Equal(PongCore.StartTicks, h.View(0).GetProperty("startIn").GetInt32());

        h.Tick(5);
        Assert.Equal(PongCore.StartTicks - 5, h.View(0).GetProperty("startIn").GetInt32());
        var f = LastFrame(h);
        Near(PongCore.W / 2, f.GetProperty("bx").GetDouble());
        Near(0, f.GetProperty("vx").GetDouble());
        // клієнт малює «Готуйсь» саме з кадру: види в цій фазі більше не летять
        Assert.Equal(PongCore.StartTicks - 5, f.GetProperty("startIn").GetInt32());

        h.Tick(PongCore.StartTicks - 5);
        Assert.Equal("play", h.View(0).GetProperty("phase").GetString());
        Assert.Equal(0, h.View(0).GetProperty("startIn").GetInt32());
        Assert.True(Math.Abs(LastFrame(h).GetProperty("vx").GetDouble()) > 0, "після відліку м'яч має полетіти");
    }

    [Fact]
    public void Every_tick_sends_a_frame_and_the_serve_sends_the_views_too()
    {
        var h = Table();
        h.Tick(3);
        Assert.Equal(3, h.Outbox.OfType<RoomFrame>().Count());
        Assert.Equal(2, h.Outbox.OfType<RoomViews>().Count());   // по одному на кожен вхід; тики шлють лише кадри

        Ready(h);                                                 // тик, на якому «готуйсь» скінчився
        Assert.Equal(3, h.Outbox.OfType<RoomViews>().Count());
    }

    [Fact]
    public void The_frame_carries_exactly_what_the_client_draws()
    {
        var h = Table();
        Ready(h);
        h.Tick(3);
        var f = LastFrame(h);

        foreach (var name in new[] { "t", "bx", "by", "vx", "vy", "p", "s", "serveIn", "startIn", "winner" })
            Assert.True(Views.Has(f, name), name);
        Assert.Equal(2, f.GetProperty("p").GetArrayLength());
        Assert.Equal(2, f.GetProperty("s").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, f.GetProperty("winner").ValueKind);
        Assert.Equal(PongCore.StartTicks + 3, f.GetProperty("t").GetInt32());

        // Округлення до 0.1 — це третина розміру кадру, а 25 кадрів на секунду летять кожному глядачу.
        foreach (var name in new[] { "bx", "by", "vx", "vy" })
        {
            var v = f.GetProperty(name).GetDouble();
            Near(Math.Round(v, 1), v, 1e-9);
        }
    }

    [Fact]
    public void The_input_payload_is_exactly_what_the_module_sends()
    {
        var h = Table();
        var step = PongCore.PaddleSpeed * PongCore.Dt;

        h.Input(0, "move", new { dir = 1 });          // утримання клавіші «вниз»
        h.Tick(1);
        Near(PongCore.H / 2 + step, LastFrame(h).GetProperty("p")[0].GetDouble(), 1e-9);

        h.Input(1, "to", new { y = 90.0 });           // палець на канвасі
        h.Tick(1);
        Near(PongCore.H / 2 + step, LastFrame(h).GetProperty("p")[1].GetDouble(), 1e-9);

        h.Input(0, "move", new { dir = 0 });          // клавішу відпустили
        var stopped = LastFrame(h).GetProperty("p")[0].GetDouble();
        h.Tick(2);
        Near(stopped, LastFrame(h).GetProperty("p")[0].GetDouble(), 1e-9);
    }

    [Fact]
    public void An_input_from_a_stranger_changes_nothing()
    {
        var h = Table();
        h.Tick(1);
        var before = LastFrame(h).GetProperty("p")[0].GetDouble();

        h.Rooms.Input(h.RoomId, "Чужий", "move", Views.Payload(new { dir = 1 }));
        h.Tick(1);

        Near(before, LastFrame(h).GetProperty("p")[0].GetDouble(), 1e-9);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void An_unknown_action_is_refused_in_human_words()
    {
        var h = Table();
        var r = h.Act(0, "стрибок", new { dir = 1 });

        Assert.False(r.Ok);
        Assert.Equal("Тут так не ходять", r.Message);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void Seven_points_finish_the_game_and_the_journal_carries_the_score()
    {
        var h = Table();
        PlayOut(h);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var scores = h.View(null).GetProperty("scores");
        var (a, b) = (scores[0].GetInt32(), scores[1].GetInt32());
        Assert.Equal(PongCore.Target, Math.Max(a, b));
        Assert.True(Math.Min(a, b) < PongCore.Target, "переможець має бути один");

        var winner = Assert.Single(h.Room.Result!.Winners);
        Assert.Equal(a > b ? 0 : 1, winner);
        var log = h.Outbox.OfType<Journal>().Last().Text;
        Assert.StartsWith("Понг:", log);
        Assert.Contains($"{Math.Max(a, b)}:{Math.Min(a, b)}", log);
        Assert.Contains(h.NickOf(winner), log);
        Assert.Single(h.Finished);
        Assert.Equal("done", h.View(null).GetProperty("phase").GetString());
        Assert.Equal(winner, h.View(null).GetProperty("winner").GetInt32());
    }

    [Fact]
    public void A_finished_game_stops_ticking()
    {
        var h = Table();
        h.Leave("Петро");                       // техпоразка: партія скінчилась
        Assert.Equal(RoomStatus.Finished, h.Room.Status);

        var frames = h.Outbox.OfType<RoomFrame>().Count();
        h.Tick(10);
        Assert.Equal(frames, h.Outbox.OfType<RoomFrame>().Count());
    }

    [Fact]
    public void Standing_up_in_the_middle_hands_the_game_to_the_other()
    {
        var h = Table();
        Ready(h);
        h.Tick(10);
        h.Leave("Петро");

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Contains("встав з-за столу", h.Outbox.OfType<Journal>().Last().Text);
        // Партія скінчилась — вид і кадр мають це показати, інакше на полі застигне «граємо» без підсумку.
        var v = h.View(null);
        Assert.Equal("done", v.GetProperty("phase").GetString());
        Assert.Equal(0, v.GetProperty("winner").GetInt32());
    }

    [Fact]
    public void Rematch_swaps_the_sides_and_starts_from_a_clean_zero()
    {
        var h = Table();
        PlayOut(h);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);

        h.Rematch("Оля");

        Assert.Equal("Петро", h.Room.Seats[0]);   // місця обернулись
        Assert.Equal("Оля", h.Room.Seats[1]);
        var v = h.View(0);
        Assert.Equal("ready", v.GetProperty("phase").GetString());
        Assert.Equal(PongCore.StartTicks, v.GetProperty("startIn").GetInt32());
        Assert.Equal(0, v.GetProperty("scores")[0].GetInt32());
        Assert.Equal(0, v.GetProperty("scores")[1].GetInt32());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("winner").ValueKind);
        // Ракетки теж повертаються в центр: рематч — це чиста партія, а не пауза.
        Near(PongCore.H / 2, v.GetProperty("frame").GetProperty("p")[0].GetDouble());
        Near(PongCore.H / 2, v.GetProperty("frame").GetProperty("p")[1].GetDouble());
    }

    [Fact]
    public void Two_rooms_with_the_same_seed_play_the_same_ball()
    {
        static string Play(int seed)
        {
            var h = Table(seed);
            for (var i = 0; i < 300 && h.Room.Status == RoomStatus.Playing; i++)
            {
                h.Tick(1);
                if (h.Room.Status != RoomStatus.Playing) break;
                if (i % 9 == 0) h.Input(1, "to", new { y = LastFrame(h).GetProperty("by").GetDouble() });
            }
            // Порівнюємо всю траєкторію, а не останній кадр: у паузі після гола м'яч у будь-якій
            // партії стоїть у центрі, і однакові кадри там нічого не доводили б.
            return string.Join("|", h.Outbox.OfType<RoomFrame>().Select(x => Views.Text(x.Frame)));
        }

        Assert.Equal(Play(7), Play(7));
        Assert.NotEqual(Play(7), Play(8));
    }

    [Fact]
    public void A_ball_past_the_right_edge_is_a_point_for_the_left()
    {
        var core = Rally();
        (core.Bx, core.By, core.Vx, core.Vy) = (PongCore.W - 8, 10, PongCore.MaxSpeed, 0);
        core.P[1] = 50;

        Assert.Null(core.Step());
        Assert.Equal(0, core.Step());
        Assert.Equal(1, core.S[0]);
        Assert.Equal(0, core.S[1]);
    }

    [Fact]
    public void Pong_is_in_the_catalog_as_a_live_game_on_two()
    {
        var game = Assert.Single(new Registry().Catalog, g => g.Id == "pong");

        Assert.Equal("live", game.Group);
        Assert.Equal("Понг", game.Title);
        Assert.Equal(PongCore.TickMs, game.TickMs);
        Assert.Equal(2, game.MinPlayers);
        Assert.Equal(2, game.MaxPlayers);
        Assert.True(game.Rated);                // ставка можлива лише при MaxPlayers == 2 і Rated
        Assert.Equal("pong", game.Module);
        Assert.False(game.Private);
    }

    [Fact]
    public void Seat_names_are_the_left_and_the_right_one()
    {
        var h = Table();
        Assert.Equal("ліва", h.Room.SafeSeatName(0));
        Assert.Equal("права", h.Room.SafeSeatName(1));
    }
}
