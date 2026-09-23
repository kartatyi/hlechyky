using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Понг на трьох-чотирьох (арена, <see cref="PongArena"/>) і те, що змінилось для двох: підкрутка, довжина
/// партії, старт господарем. Класична фізика на двох — у <see cref="PongTests"/>.
/// </summary>
public class PongArenaTests
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ігор", "Марта"];

    static RoomHarness Table(int players, int seed = 42, object? options = null)
    {
        var h = new RoomHarness("pong", options: options, seed: seed);
        for (var i = 0; i < players; i++) h.Join(Nicks[i]);
        h.Start();
        return h;
    }

    static JsonElement LastFrame(RoomHarness h) => Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);

    /// <summary>Арена посеред розіграшу: відлік позаду, м'яч там, де його поставив тест.</summary>
    static PongArena Rally(params bool[] plays)
    {
        var a = new PongArena(new Random(1));
        a.Reset(plays.Length == 0 ? [true, true, true, true] : plays);
        a.StartIn = 0;
        return a;
    }

    static void Near(double expected, double actual, double eps = 1e-6) =>
        Assert.True(Math.Abs(expected - actual) <= eps, $"чекали {expected}, отримали {actual}");

    /// <summary>
    /// Боти: кожен живий веде свою ракетку за м'ячем, трохи зсунувшись від центру (щоб віддавати під кутом).
    /// <paramref name="lazy"/> — ці місця стоять стовпом і рано чи пізно вилітають.
    /// </summary>
    static void PlayOut(RoomHarness h, int[] lazy, int limit = 30000)
    {
        for (var i = 0; i < limit && h.Room.Status == RoomStatus.Playing; i++)
        {
            h.Tick(1);
            if (h.Room.Status != RoomStatus.Playing) break;
            if (i % 2 != 0) continue;
            var f = LastFrame(h);
            for (var s = 0; s < 4; s++)
            {
                if (lazy.Contains(s) || h.Room.Seats[s] is null) continue;
                var along = s < 2 ? f.GetProperty("by").GetDouble() : f.GetProperty("bx").GetDouble();
                h.Input(s, "to", new { y = along - 3 });
            }
        }
    }

    // ---------- режим ----------

    [Fact]
    public void Three_at_the_table_play_on_the_arena_and_the_empty_wall_is_blank()
    {
        var h = Table(3);
        var v = h.View(0);

        Assert.Equal("arena", v.GetProperty("mode").GetString());
        var lives = v.GetProperty("lives");
        Assert.Equal(PongArena.DefaultLives, lives[0].GetInt32());
        Assert.Equal(PongArena.DefaultLives, lives[2].GetInt32());
        Assert.Equal(JsonValueKind.Null, lives[3].ValueKind);
        var f = LastFrameOrView(h);
        Assert.Equal(JsonValueKind.Null, f.GetProperty("p")[3].ValueKind);
        Assert.Equal(PongArena.S / 2, f.GetProperty("p")[2].GetDouble());
    }

    static JsonElement LastFrameOrView(RoomHarness h) => h.View(null).GetProperty("frame");

    [Fact]
    public void Two_at_the_table_still_play_the_classic()
    {
        var h = Table(2);
        var v = h.View(0);
        Assert.Equal("duo", v.GetProperty("mode").GetString());
        Assert.Equal(2, v.GetProperty("scores").GetArrayLength());
    }

    [Fact]
    public void A_table_in_the_lobby_shows_the_field_it_is_going_to_play()
    {
        var h = new RoomHarness("pong");
        h.Join("Оля");
        h.Join("Петро");
        Assert.Equal("duo", h.View(null).GetProperty("mode").GetString());
        h.Join("Ігор");
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);
        Assert.Equal("arena", h.View(null).GetProperty("mode").GetString());
        Assert.Equal("руда", h.Room.SafeSeatName(2));
    }

    [Fact]
    public void Nothing_is_hidden_on_the_arena_either()
    {
        var h = Table(4);
        h.Tick(PongArena.StartTicks + 20);
        var mine = h.View(0).ToString();
        Assert.Equal(mine, h.View(3).ToString());
        Assert.Equal(mine, h.View(null).ToString());
    }

    // ---------- фізика арени ----------

    [Fact]
    public void The_bottom_paddle_sends_a_ball_from_its_middle_straight_up()
    {
        var a = Rally();
        a.P[3] = 60;
        (a.Bx, a.By, a.Vx, a.Vy) = (60, PongArena.S - 8, 0, PongArena.StartSpeed);

        a.Step();

        Assert.True(a.Vy < 0, "м'яч мав піти вгору");
        Near(0, a.Vx, 1e-9);
        Assert.Equal(3, a.HitBy);
    }

    [Fact]
    public void The_edge_of_the_top_paddle_gives_sixty_degrees()
    {
        var a = Rally();
        a.P[2] = 60;
        (a.Bx, a.By, a.Vx, a.Vy) = (60 + PongArena.PaddleL / 2, 8, 0, -PongArena.StartSpeed);

        a.Step();

        Assert.True(a.Vy > 0, "від верхньої ракетки м'яч іде вниз");
        Near(60, Math.Atan2(a.Vx, a.Vy) * 180 / Math.PI, 1e-6);
    }

    [Fact]
    public void A_ball_past_a_living_wall_takes_a_life_and_the_last_toucher_gets_the_goal()
    {
        var a = Rally();
        a.P[0] = 30;
        a.P[1] = 60;
        // права відбиває просто в ліву, яка стоїть зовсім не там
        (a.Bx, a.By, a.Vx, a.Vy) = (PongArena.S - 9, 60, PongArena.MaxSpeed, 0);
        int? lost = null;
        for (var i = 0; i < 40 && lost is null; i++) lost = a.Step();

        Assert.Equal(0, lost);
        Assert.Equal(PongArena.DefaultLives - 1, a.L[0]);
        Assert.Equal(1, a.Goals[1]);
        Assert.Equal(1, a.LastBy);
        Assert.Equal(PongArena.ServeTicks, a.ServeIn);
        Near(PongArena.S / 2, a.Bx);
    }

    [Fact]
    public void A_wall_without_a_player_is_just_a_wall()
    {
        var a = Rally(true, true, true, false);
        (a.Bx, a.By, a.Vx, a.Vy) = (60, PongArena.S - 3, 20, PongArena.StartSpeed);

        Assert.Null(a.Step());

        Assert.True(a.Vy < 0, "від глухої нижньої стіни м'яч відскакує вгору");
        Assert.True(a.By <= PongArena.S - PongArena.BallR);
        Assert.Equal(PongArena.DefaultLives, a.L[0]);
    }

    [Fact]
    public void A_ball_straight_between_two_blank_walls_is_nudged_along_them()
    {
        var a = Rally(false, false, true, true);
        (a.Bx, a.By, a.Vx, a.Vy) = (3, 60, -PongArena.StartSpeed, 0);

        a.Step();

        Assert.True(a.Vx > 0);
        Assert.True(Math.Abs(a.Vy) >= PongArena.MinSlide * a.Speed - 1e-9, "м'яч мав піти хоч трохи вздовж стіни");
    }

    [Fact]
    public void A_corner_block_bounces_the_ball_back_into_the_field()
    {
        var a = Rally();
        // Ліва ракетка далеко внизу — м'яч летить у верхній лівий кут по діагоналі.
        a.P[0] = PongArena.MaxP;
        a.P[2] = PongArena.MaxP;
        (a.Bx, a.By, a.Vx, a.Vy) = (PongArena.Corner + 6, PongArena.Corner + 1, -PongArena.StartSpeed, -10);

        for (var i = 0; i < 5; i++) Assert.Null(a.Step());

        Assert.True(a.Vx > 0, "кут мав відбити м'яч праворуч");
        Assert.True(a.Bx >= PongArena.Corner + PongArena.BallR - 1e-9 || a.By >= PongArena.Corner + PongArena.BallR - 1e-9,
            $"м'яч застряг у куті: {a.Bx}, {a.By}");
    }

    [Fact]
    public void Paddles_stay_between_the_corners()
    {
        var a = Rally();
        a.Move(2, -1);
        a.Move(3, 1);
        a.Move(0, -1);
        for (var i = 0; i < 200; i++) a.Step();

        Near(PongArena.MinP, a.P[2]);
        Near(PongArena.MaxP, a.P[3]);
        Near(PongArena.MinP, a.P[0]);
    }

    [Fact]
    public void A_moving_paddle_puts_spin_on_the_ball()
    {
        var still = Rally();
        still.P[3] = 60;
        (still.Bx, still.By, still.Vx, still.Vy) = (60, PongArena.S - 8, 0, PongArena.StartSpeed);
        still.Step();

        var moving = Rally();
        moving.P[3] = 60 - PongArena.PaddleSpeed * PongArena.Dt;   // за тик доїде рівно в 60
        moving.Move(3, 1);
        (moving.Bx, moving.By, moving.Vx, moving.Vy) = (60, PongArena.S - 8, 0, PongArena.StartSpeed);
        moving.Step();

        Near(0, still.Vx, 1e-9);
        Assert.True(moving.Vx > 0, "ракетка їхала праворуч — м'яч мав піти праворуч");
        Near(PongArena.SpinAngle, Math.Atan2(moving.Vx, -moving.Vy) * 180 / Math.PI, 3);
    }

    [Fact]
    public void Ten_seconds_without_a_touch_serve_the_ball_again_without_a_penalty()
    {
        var a = Rally();
        a.Vx = a.Vy = 0;                     // м'яч завмер — ніхто його не дістане
        var serve = a.Serve;
        for (var i = 0; i <= PongArena.IdleTicks; i++) Assert.Null(a.Step());

        Assert.Equal(serve + 1, a.Serve);
        Assert.Equal(PongArena.ServeTicks, a.ServeIn);
        Assert.All(a.L, l => Assert.Equal(PongArena.DefaultLives, l));
    }

    [Fact]
    public void The_last_life_turns_the_wall_blank()
    {
        var a = new PongArena(new Random(3));
        a.Reset([true, true, true, false], 1);
        a.StartIn = 0;
        a.P[0] = PongArena.MinP;
        (a.Bx, a.By, a.Vx, a.Vy) = (5, 90, -PongArena.MaxSpeed, 0);

        Assert.Equal(0, a.Step());
        Assert.False(a.Alive(0));
        Assert.Equal([0], a.Out);
        Assert.Equal(2, a.AliveCount);

        // тепер ліва стіна глуха: м'яч від неї відскакує, життів ні в кого не забирає
        a.ServeIn = 0;
        (a.Bx, a.By, a.Vx, a.Vy) = (5, 90, -PongArena.MaxSpeed, 0);
        Assert.Null(a.Step());
        Assert.True(a.Vx > 0);
    }

    // ---------- кімната ----------

    [Fact]
    public void The_last_one_standing_wins_the_arena()
    {
        var h = Table(3);
        PlayOut(h, lazy: [1, 2]);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        var v = h.View(null);
        Assert.Equal("done", v.GetProperty("phase").GetString());
        Assert.Equal(0, v.GetProperty("winner").GetInt32());
        Assert.Equal(0, v.GetProperty("lives")[1].GetInt32());
        Assert.Equal(0, v.GetProperty("lives")[2].GetInt32());
        var log = h.Outbox.OfType<Journal>().Last().Text;
        Assert.StartsWith("Понг, арена: Оля", log);
        Assert.Contains("Петро", log);
        Assert.Contains("Ігор", log);
        Assert.Single(h.Finished);
    }

    [Fact]
    public void Four_players_play_to_the_end_too()
    {
        var h = Table(4, seed: 9, options: new { len = "short" });
        PlayOut(h, lazy: [0, 2, 3]);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Room.Result!.Winners);
        Assert.Equal(3, h.View(null).GetProperty("target").GetInt32());
    }

    [Fact]
    public void One_leaving_the_arena_does_not_end_it_for_the_rest()
    {
        var h = Table(3);
        h.Tick(PongArena.StartTicks + 10);
        h.Leave("Ігор");

        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        h.Tick(1);
        var f = LastFrame(h);
        Assert.Equal(0, f.GetProperty("l")[2].GetInt32());
        Assert.Contains("встав з-за столу", h.Outbox.OfType<Journal>().Last().Text);

        h.Leave("Петро");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Equal(0, h.View(null).GetProperty("winner").GetInt32());
    }

    [Fact]
    public void Leaving_after_being_knocked_out_changes_nothing_for_the_rest()
    {
        var h = Table(4, seed: 5, options: new { len = "short" });
        // Марта стоїть стовпом, решта ганяються за м'ячем — доки Марта не вилетить.
        for (var i = 0; i < 20000 && LivesOf(h, 3) > 0; i++)
        {
            h.Tick(1);
            var f = LastFrame(h);
            for (var s = 0; s < 3; s++)
                h.Input(s, "to", new { y = (s < 2 ? f.GetProperty("by") : f.GetProperty("bx")).GetDouble() - 3 });
        }
        Assert.Equal(0, LivesOf(h, 3));
        Assert.Equal(RoomStatus.Playing, h.Room.Status);

        h.Leave("Марта");

        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    static int LivesOf(RoomHarness h, int seat) => h.View(null).GetProperty("lives")[seat].GetInt32();

    [Fact]
    public void Rematch_on_the_arena_rotates_the_walls_and_refills_the_lives()
    {
        var h = Table(3);
        PlayOut(h, lazy: [1, 2]);
        h.Rematch("Оля");

        Assert.Equal("Петро", h.Room.Seats[0]);
        Assert.Equal("Ігор", h.Room.Seats[1]);
        Assert.Equal("Оля", h.Room.Seats[2]);
        var v = h.View(0);
        Assert.Equal("arena", v.GetProperty("mode").GetString());
        Assert.Equal("ready", v.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("winner").ValueKind);
        for (var s = 0; s < 3; s++) Assert.Equal(PongArena.DefaultLives, v.GetProperty("lives")[s].GetInt32());
    }

    [Fact]
    public void The_top_player_moves_the_paddle_along_x()
    {
        var h = Table(3);
        h.Input(2, "to", new { y = 90.0 });
        h.Tick(1);
        var f = LastFrame(h);
        Near(PongArena.S / 2 + PongArena.PaddleSpeed * PongArena.Dt, f.GetProperty("p")[2].GetDouble(), 0.05);
        Near(PongArena.S / 2, f.GetProperty("p")[0].GetDouble());
    }

    [Fact]
    public void The_arena_frame_carries_what_the_client_draws()
    {
        var h = Table(4);
        h.Tick(PongArena.StartTicks + 3);
        var f = LastFrame(h);
        foreach (var name in new[] { "mode", "t", "bx", "by", "vx", "vy", "p", "l", "serveIn", "startIn", "winner", "hit", "rally", "lost", "from", "n" })
            Assert.True(Views.Has(f, name), name);
        Assert.Equal(4, f.GetProperty("p").GetArrayLength());
        Assert.Equal(4, f.GetProperty("l").GetArrayLength());
    }

    [Fact]
    public void The_same_seed_plays_the_same_arena()
    {
        static string Play(int seed)
        {
            var h = Table(4, seed);
            for (var i = 0; i < 400 && h.Room.Status == RoomStatus.Playing; i++)
            {
                h.Tick(1);
                if (i % 7 == 0) h.Input(i % 4, "move", new { dir = i % 3 - 1 });
            }
            return string.Join("|", h.Outbox.OfType<RoomFrame>().Select(x => Views.Text(x.Frame)));
        }

        Assert.Equal(Play(3), Play(3));
        Assert.NotEqual(Play(3), Play(4));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void A_thousand_arena_ticks_on_four_take_well_under_two_seconds()
    {
        var a = new PongArena(new Random(1));
        a.Reset([true, true, true, true]);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            if (i % 5 == 0) a.Aim(i % 4, i % 120);
            a.Step();
        }
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"1000 тиків зайняли {sw.Elapsed}");
    }

    // ---------- що змінилось для двох ----------

    [Fact]
    public void Two_players_wait_for_the_host_to_press_start()
    {
        var h = new RoomHarness("pong");
        h.Join("Оля");
        h.Join("Петро");
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);
        Assert.True(h.Start().Ok);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void A_moving_classic_paddle_puts_spin_on_the_ball()
    {
        var core = new PongCore(new Random(1));
        core.Reset();
        core.StartIn = 0;
        core.P[0] = 50 - PongCore.PaddleSpeed * PongCore.Dt;
        core.Move(0, 1);
        (core.Bx, core.By, core.Vx, core.Vy) = (8, 50, -PongCore.StartSpeed, 0);

        core.Step();

        Assert.True(core.Vx > 0);
        Near(PongCore.SpinAngle, Math.Atan2(core.Vy, core.Vx) * 180 / Math.PI, 1e-6);
        Assert.Equal(0, core.HitBy);
        Assert.Equal(1, core.Rally);
    }

    [Fact]
    public void A_short_classic_game_ends_at_five()
    {
        var h = Table(2, options: new { len = "short" });
        Assert.Equal(5, h.View(0).GetProperty("target").GetInt32());
        for (var i = 0; i < 20000 && h.Room.Status == RoomStatus.Playing; i++)
        {
            h.Tick(1);
            if (h.Room.Status != RoomStatus.Playing) break;
            h.Input(0, "to", new { y = LastFrame(h).GetProperty("by").GetDouble() - 4 });
        }
        var s = h.View(null).GetProperty("scores");
        Assert.Equal(5, Math.Max(s[0].GetInt32(), s[1].GetInt32()));
    }

    [Fact]
    public void An_unknown_length_falls_back_to_the_usual_one()
    {
        var h = new RoomHarness("pong", options: new { len = "вічна" });
        h.Join("Оля");
        h.Join("Петро");
        h.Start();
        Assert.Equal(PongCore.Target, h.View(null).GetProperty("target").GetInt32());
    }

    [Fact]
    public void Two_left_after_a_third_stood_up_in_the_lobby_play_the_classic_on_their_seats()
    {
        var h = new RoomHarness("pong");
        h.Join("Оля");
        h.Join("Петро");
        h.Join("Ігор");
        h.Leave("Петро");                     // лишились місця 0 і 2
        h.Start();

        Assert.Equal("duo", h.View(null).GetProperty("mode").GetString());
        Assert.Equal("ліва", h.Room.SafeSeatName(0));
        Assert.Equal("права", h.Room.SafeSeatName(2));
        h.Input(2, "move", new { dir = 1 });
        h.Tick(1);
        Assert.True(LastFrame(h).GetProperty("p")[1].GetDouble() > PongCore.H / 2, "Ігор має керувати правою ракеткою");
        Assert.Equal(2, LastFrame(h).GetProperty("seats")[1].GetInt32());

        h.Leave("Ігор");
        Assert.Equal([0], h.Room.Result!.Winners);
    }

    [Fact]
    public void A_finished_classic_table_reopened_by_a_third_shows_a_fresh_arena_not_the_old_result()
    {
        var h = Table(2);
        h.Leave("Петро");                     // техпоразка — стіл дограний
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal(0, h.View(null).GetProperty("winner").GetInt32());

        h.Join("Петро");
        h.Join("Ігор");                       // дограний стіл відкрито наново, і тепер за ним троє

        Assert.Equal(RoomStatus.Lobby, h.Room.Status);
        var v = h.View(null);
        Assert.Equal("arena", v.GetProperty("mode").GetString());
        Assert.Equal("ready", v.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("winner").ValueKind);
        Assert.True(h.Start().Ok);
        Assert.Equal("arena", h.View(null).GetProperty("mode").GetString());
    }

    [Fact]
    public void A_reopened_classic_table_for_two_starts_from_zero_on_screen()
    {
        var h = Table(3);
        h.Leave("Ігор");
        h.Leave("Петро");                     // арену закрито
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        h.Join("Марта");                      // знову двоє — у лобі класика з нулями

        var v = h.View(null);
        Assert.Equal("duo", v.GetProperty("mode").GetString());
        Assert.Equal(0, v.GetProperty("scores")[0].GetInt32());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("winner").ValueKind);
    }
}
