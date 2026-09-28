using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Аерохокей: біти, шайбу й борти — на голому <see cref="HockeyCore"/>, рахунок, годинник, команди, вихід і
/// кадри — через кімнату. Номери в коментарях — пункти плану тестів зі spec §8.
/// </summary>
[Collection(SerialPerf.Name)]
public class HockeyTests(ITestOutputHelper output)
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван"];

    // ---------- підмостки ----------

    static RoomHarness Table(int players = 2, int seed = 42, string goals = "7")
    {
        var h = new RoomHarness("hockey", new { goals }, seed);
        foreach (var nick in Nicks.Take(players)) h.Join(nick);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static Hockey Game(RoomHarness h) => (Hockey)h.Room.Game;
    static HockeyCore Core(RoomHarness h) => Game(h).Core;

    /// <summary>Відлік — до свистка, пауза після гола — до живої шайби.</summary>
    static void Live(RoomHarness h)
    {
        for (var i = 0; i < 200 && (Core(h).StartIn > 0 || Core(h).ServeIn > 0) && h.Room.Status == RoomStatus.Playing; i++) h.Tick();
    }

    /// <summary>Гол команді team: шайба просто перед чужими воротами, летить у проріз.</summary>
    static void Score(RoomHarness h, int team, int? toucher = null)
    {
        Live(h);
        var c = Core(h);
        c.Puck = new ArenaBody(team == 0 ? 197 : 3, 60, HockeyCore.PuckR, 1) { Vx = team == 0 ? 400 : -400 };
        c.LastTouch = toucher;
        h.Tick();
        Assert.True(c.GoalBy == team || h.Room.Status == RoomStatus.Finished);
    }

    static HockeyCore Bare(params int[] seats)
    {
        var plays = new bool[HockeyCore.Seats];
        foreach (var s in seats) plays[s] = true;
        var c = new HockeyCore(new Random(1));
        c.Reset(plays);
        c.StartIn = 0;          // одразу гра, без відліку
        c.Puck = new ArenaBody(100, 60, HockeyCore.PuckR, 1);
        return c;
    }

    static void Put(HockeyCore c, int seat, double x, double y)
    {
        c.Pads[seat].X = x;
        c.Pads[seat].Y = y;
        c.Aim(seat, x, y);
    }

    static JsonElement Frame(RoomHarness h)
    {
        var room = h.Room;
        lock (room.Sync) return Views.Json(room.Game.Frame());
    }

    static double Speed(ArenaBody b) => Math.Sqrt(b.Vx * b.Vx + b.Vy * b.Vy);

    // ---------- стіл і команди ----------

    [Fact] // 1
    public void Two_players_are_blue_and_clay_by_seat_order()
    {
        Assert.Equal(0, HockeyCore.TeamOf(0, [true, true, false, false]));
        Assert.Equal(1, HockeyCore.TeamOf(1, [true, true, false, false]));
        Assert.Equal(0, HockeyCore.TeamOf(0, [true, false, true, false]));
        Assert.Equal(1, HockeyCore.TeamOf(2, [true, false, true, false]));
        Assert.Equal(0, HockeyCore.TeamOf(1, [false, true, false, true]));

        // Сіли на 0 і 2 (другий устав у лобі) — однаково грають одне проти одного.
        var h = new RoomHarness("hockey", new { goals = "7" });
        h.Join("Оля");
        h.Join("Петро");
        h.Join("Ганна");
        h.Leave("Петро");
        Assert.Equal("синій", h.Room.Game.SeatName(0));
        Assert.Equal("рудий", h.Room.Game.SeatName(2));
        Assert.True(h.Start().Ok);
        var teams = h.View(null).GetProperty("teams");
        Assert.Equal("[[0],[2]]", teams.GetRawText());
        Assert.Equal("рудий", h.Room.Game.SeatName(2));
    }

    [Fact] // 2
    public void Four_players_team_up_by_seat_parity()
    {
        var h = Table(4);
        Assert.Equal("[[0,2],[1,3]]", h.View(null).GetProperty("teams").GetRawText());
        Assert.Equal(["синій", "рудий", "синій", "рудий"], Enumerable.Range(0, 4).Select(h.Room.Game.SeatName));
        var c = Core(h);
        Assert.True(c.Pads[0].X < HockeyCore.Mid && c.Pads[2].X < HockeyCore.Mid);
        Assert.True(c.Pads[1].X > HockeyCore.Mid && c.Pads[3].X > HockeyCore.Mid);
    }

    [Fact] // 3 (прохід №3, п. 180: утрьох стартує з ботом-напарником)
    public void Three_at_the_table_start_with_a_bot_partner_for_the_lonely_one()
    {
        var h = new RoomHarness("hockey");
        foreach (var n in Nicks.Take(3)) h.Join(n);
        Assert.Equal(3, h.View(null).GetProperty("bot").GetInt32());     // у лобі вже видно, куди стане бот
        Assert.True(h.Start().Ok, h.Reply.Message);
        var g = Game(h);
        var c = Core(h);
        Assert.Equal(3, g.Bot);
        Assert.True(c.Plays[3]);
        Assert.Equal(c.Team[1], c.Team[3]);                               // бот — у пару до самотнього рудого
        Assert.Equal(c.Team[0], c.Team[2]);
        Assert.Equal(3, h.View(0).GetProperty("bot").GetInt32());
        Assert.NotNull(Frame(h).GetProperty("p")[6].GetDouble());
    }

    [Fact] // 3b: бот справді грає — сам забиває нерухомим людям — і не отримує ні рахунку, ні нагород
    public void Bot_scores_against_idle_humans_and_gets_no_rewards()
    {
        var h = new RoomHarness("hockey", new { goals = "5" }, 7);
        foreach (var n in Nicks.Take(3)) h.Join(n);
        Assert.True(h.Start().Ok);
        var c = Core(h);
        var hits = 0;
        for (var t = 0; t < 12000 && h.Room.Status == RoomStatus.Playing; t++)
        {
            h.Tick();
            if (c.HitBy == 3) hits++;
        }
        output.WriteLine($"ударів бота {hits}, рахунок {c.S[0]}:{c.S[1]}, голи бота {c.Goals[3]}");
        Assert.True(hits >= 5, $"бот ударив {hits} разів");
        Assert.True(c.Goals[3] >= 1);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var fin = h.Finished.Single();
        Assert.DoesNotContain(3, fin.Result.Winners);
        Assert.False(fin.Result.Scores!.ContainsKey(3));
        Assert.Contains("🤖 бот", h.Room.Result!.Text);
        Assert.DoesNotContain(h.Awards, a => a.Nick is null or "");
    }

    [Fact] // 3c: бот воротар — шайба, що летить у ворота, частіше відбита, ніж пропущена
    public void Bot_goalkeeper_saves_most_straight_shots()
    {
        var saved = 0;
        for (var k = 0; k < 20; k++)
        {
            var c = Bare(0, 1);
            Put(c, 0, 30, 60);
            Put(c, 1, 170, 60);
            var y0 = 20 + k * 4;
            // шайба з центру летить у ліві ворота (сині — місце 0 — бот)
            c.Puck = new ArenaBody(110, y0, HockeyCore.PuckR, 1) { Vx = -380, Vy = (60 + (k % 5 - 2) * 6 - y0) * 380 / 110.0 };
            var goal = false;
            for (var t = 0; t < 40 && !goal; t++)
            {
                c.BotThink(0);
                goal = c.Step() >= 0;
            }
            if (!goal) saved++;
        }
        output.WriteLine($"відбив {saved} з 20");
        Assert.True(saved >= 12, $"відбив лише {saved} з 20");
        Assert.True(saved <= 19, "бот не мусить бути стіною");
    }

    [Fact] // 3d: намір з рукою в русі — біта йде далі швидкістю руки, поки летить наступний намір
    public void Aim_with_hand_speed_keeps_the_paddle_moving_between_intents()
    {
        var c = Bare(0, 1);
        Put(c, 0, 40, 60);
        c.Aim(0, 40, 60, 300, 0);
        c.Step();
        // 8 підкроків ціль повзе 300/с, далі стоїть: 40 + 300 · 0.032 = 49.6
        Assert.Equal(40 + 300 * HockeyCore.AimLead * HockeyCore.H, c.Pads[0].X, 6);
        c.Aim(0, 60, 60, 0, 0);
        c.Step();
        Assert.Equal(60, c.Pads[0].X, 6);                    // 10.4 за тик — 900/с встигає
        Assert.Equal(HockeyCore.PadStep, 3.6, 9);
    }

    [Fact] // 4
    public void Lobby_view_shows_the_table_with_seated_paddles_only()
    {
        var h = new RoomHarness("hockey");
        h.Join("Оля");
        h.Join("Петро");
        var v = h.View(null);
        Assert.Equal("lobby", v.GetProperty("phase").GetString());
        var f = v.GetProperty("frame");
        Assert.Equal(100, f.GetProperty("x").GetDouble());
        Assert.Equal(60, f.GetProperty("y").GetDouble());
        var p = f.GetProperty("p");
        Assert.Equal(25, p[0].GetDouble());
        Assert.Equal(175, p[2].GetDouble());
        for (var i = 4; i < 8; i++) Assert.Equal(JsonValueKind.Null, p[i].ValueKind);
        var table = v.GetProperty("table");
        Assert.Equal(200, table.GetProperty("w").GetInt32());
        Assert.Equal("[40,80]", table.GetProperty("goal").GetRawText());
    }

    // ---------- біти ----------

    [Fact] // 5
    public void Paddle_moves_toward_the_target_no_faster_than_420_per_second()
    {
        var c = Bare(0, 1);
        Put(c, 0, 20, 20);
        c.Aim(0, 90, 110);
        c.Step();
        var d = Math.Sqrt(Math.Pow(c.Pads[0].X - 20, 2) + Math.Pow(c.Pads[0].Y - 20, 2));
        Assert.Equal(HockeyCore.PadSpeed * 0.04, d, 9);        // 16.8 за тик
        Assert.Equal(HockeyCore.PadSpeed, Speed(c.Pads[0]), 6);
        for (var t = 0; t < 20; t++) c.Step();
        Assert.Equal((90.0, 110.0), (c.Pads[0].X, c.Pads[0].Y));   // доїхала й стоїть рівно в цілі
        Assert.Equal(0, Speed(c.Pads[0]));
    }

    [Fact] // 6
    public void Paddle_never_leaves_its_half_or_enters_the_goal_mouth()
    {
        var c = Bare(0, 1);
        c.Aim(0, 190, 60);
        c.Aim(1, -40, -40);
        for (var t = 0; t < 40; t++) c.Step();
        Assert.Equal(HockeyCore.Mid - HockeyCore.PadR, c.Pads[0].X);
        Assert.Equal(HockeyCore.Mid + HockeyCore.PadR, c.Pads[1].X);
        Assert.Equal(HockeyCore.PadR, c.Pads[1].Y);
        c.Aim(0, -30, 60);                                       // у свої ворота — ні
        for (var t = 0; t < 40; t++) c.Step();
        Assert.Equal(HockeyCore.PadR, c.Pads[0].X);
        c.Move(1, 1, 1);
        for (var t = 0; t < 60; t++) c.Step();
        Assert.Equal((HockeyCore.W - HockeyCore.PadR, HockeyCore.TableH - HockeyCore.PadR), (c.Pads[1].X, c.Pads[1].Y));
    }

    [Fact] // 7
    public void Move_direction_is_normalised_on_diagonals()
    {
        var c = Bare(0, 1);
        Put(c, 0, 30, 30);
        c.Move(0, 1, 1);
        c.Step();
        Assert.Equal(16.8 * ArenaPhysics.D, c.Pads[0].X - 30, 9);
        Assert.Equal(16.8 * ArenaPhysics.D, c.Pads[0].Y - 30, 9);
        Assert.Equal(HockeyCore.KeySpeed, Speed(c.Pads[0]), 6);
        c.Move(0, 5, 0);                                         // будь-яке число — лише знак
        var x = c.Pads[0].X;
        c.Step();
        Assert.Equal(16.8, c.Pads[0].X - x, 9);
    }

    [Fact] // 8
    public void New_input_cancels_the_previous_one()
    {
        var c = Bare(0, 1);
        Put(c, 0, 50, 60);
        c.Aim(0, 90, 60);
        c.Move(0, 0, 1);                                         // клавіша після миші — миша забута
        c.Step();
        Assert.Equal(50, c.Pads[0].X, 9);
        Assert.Equal(76.8, c.Pads[0].Y, 9);
        c.Aim(0, 20, 76.8);                                      // миша після клавіші — клавіша забута
        for (var t = 0; t < 5; t++) c.Step();
        Assert.Equal((20.0, 76.8), (c.Pads[0].X, Math.Round(c.Pads[0].Y, 9)));
        c.Move(0, 0, 0);                                         // відпустив — стоїть
        c.Step();
        Assert.Equal(20, c.Pads[0].X, 9);
    }

    [Fact] // 9
    public void Teammates_are_pushed_apart_without_bouncing()
    {
        var c = Bare(0, 1, 2, 3);
        Put(c, 0, 40, 60);
        Put(c, 2, 60, 60);
        c.Aim(0, 50, 60);
        c.Aim(2, 50, 60);
        for (var t = 0; t < 10; t++)
        {
            c.Step();
            var d = Math.Sqrt(Math.Pow(c.Pads[2].X - c.Pads[0].X, 2) + Math.Pow(c.Pads[2].Y - c.Pads[0].Y, 2));
            Assert.True(d >= 2 * HockeyCore.PadR - 1e-6, $"тик {t}: {d}");
        }
        Assert.Equal(60, c.Pads[0].Y, 9);                         // розійшлись уздовж лінії, нікуди не відскочили
        Assert.Equal(60, c.Pads[2].Y, 9);
        Assert.True(c.Pads[0].X < c.Pads[2].X);
    }

    [Fact] // 10
    public void Paddles_keep_moving_during_ready_and_after_a_goal()
    {
        var h = Table(2);
        Assert.True(Core(h).StartIn > 0);
        h.Input(0, "to", new { x = 60, y = 30 });
        h.Tick(10);
        Assert.Equal((60.0, 30.0), (Core(h).Pads[0].X, Core(h).Pads[0].Y));
        Assert.True(Core(h).StartIn > 0);
        Score(h, 0);
        Assert.True(Core(h).ServeIn > 0);
        h.Input(1, "move", new { dx = 0, dy = -1 });             // угору, повз шайбу на подачі (150, 60)
        var y = Core(h).Pads[1].Y;
        h.Tick();
        Assert.Equal(y - 16.8, Core(h).Pads[1].Y, 9);
    }

    // ---------- шайба ----------

    [Fact] // 11
    public void Puck_reflects_off_side_rails_with_restitution_092()
    {
        var b = new ArenaBody(50, 3, HockeyCore.PuckR, 1) { Vx = 100, Vy = -300 };
        ArenaPhysics.ReflectY(ref b, HockeyCore.PuckR, HockeyCore.TableH - HockeyCore.PuckR, HockeyCore.EWall);
        Assert.Equal(6, b.Y, 9);
        Assert.Equal(276, b.Vy, 9);
        Assert.Equal(100, b.Vx);

        var c = Bare(0, 1);
        c.Puck = new ArenaBody(100, 110, HockeyCore.PuckR, 1) { Vy = 500 };
        for (var t = 0; t < 3; t++) c.Step();
        Assert.True(c.Puck.Vy < 0);
        // 30 підкроків тертя по (1 − 0.6·0.004) і один відбій 0.92 — множники, порядок не важить
        Assert.Equal(0.92 * 500 * Math.Pow(1 - HockeyCore.Mu * HockeyCore.H, 3 * HockeyCore.Sub), -c.Puck.Vy, 6);
        Assert.True(c.Puck.Y <= HockeyCore.TableH - HockeyCore.PuckR);
    }

    [Fact] // 12
    public void Puck_reflects_off_the_end_wall_outside_the_goal_mouth()
    {
        var c = Bare(0, 1);
        Put(c, 0, 60, 100);
        c.Puck = new ArenaBody(10, 30, HockeyCore.PuckR, 1) { Vx = -400 };
        for (var t = 0; t < 3; t++) Assert.Equal(-1, c.Step());
        Assert.True(c.Puck.Vx > 0);
        Assert.Equal(0, c.S[1]);

        var g = Bare(0, 1);
        Put(g, 0, 60, 100);
        g.Puck = new ArenaBody(10, 60, HockeyCore.PuckR, 1) { Vx = -400 };
        var scored = -1;
        for (var t = 0; t < 3 && scored < 0; t++) scored = g.Step();
        Assert.Equal(1, scored);                                 // сині пропустили — очко рудим
        Assert.Equal(1, g.S[1]);
    }

    [Fact] // 13
    public void Puck_at_the_post_edge_is_a_wall_at_39_9_and_a_goal_at_40_1()
    {
        int Shot(double y)
        {
            var c = Bare(0, 1);
            Put(c, 0, 60, 100);
            Put(c, 1, 140, 100);
            c.Puck = new ArenaBody(190, y, HockeyCore.PuckR, 1) { Vx = 600 };
            var scored = -1;
            for (var t = 0; t < 4 && scored < 0; t++) scored = c.Step();
            return scored;
        }

        Assert.Equal(-1, Shot(39.9));
        Assert.Equal(0, Shot(40.1));
        Assert.Equal(0, Shot(79.9));
        Assert.Equal(-1, Shot(80.1));
    }

    [Fact] // 14
    public void A_moving_paddle_adds_its_speed_to_the_puck()
    {
        var c = Bare(0, 1);
        Put(c, 0, 30, 60);
        c.Move(0, 1, 0);
        c.Puck = new ArenaBody(30 + HockeyCore.PadR + HockeyCore.PuckR + 2, 60, HockeyCore.PuckR, 1);
        c.Step();
        Assert.Equal(0, c.HitBy);
        Assert.Equal(0, c.LastTouch);
        Assert.InRange(c.Puck.Vx, 1.9 * 420 * 0.98, 1.9 * 420 + 1e-6);   // ≈ 798 мінус тертя пари підкроків
        Assert.Equal(0, c.Puck.Vy, 6);
    }

    [Fact] // 15
    public void A_fast_puck_does_not_tunnel_through_a_fast_paddle()
    {
        for (var phase = 0.0; phase < 11.36; phase += 11.36 / 5)
        {
            var c = Bare(0, 1);
            Put(c, 1, 170, 60);
            c.Move(1, -1, 0);                                    // біта мчить назустріч
            c.Puck = new ArenaBody(110 + phase, 60, HockeyCore.PuckR, 1) { Vx = HockeyCore.VMax };
            var bounced = false;
            for (var t = 0; t < 10 && !bounced; t++)
            {
                c.Step();
                Assert.True(c.Puck.X < c.Pads[1].X, $"фаза {phase}: шайба проскочила біту");
                bounced = c.Puck.Vx < 0;
            }
            Assert.True(bounced, $"фаза {phase}: відбою не було");
            Assert.Equal(1, c.LastTouch);
        }
    }

    [Fact] // 16
    public void Puck_speed_is_capped_at_1000()
    {
        var c = Bare(0, 1);
        Put(c, 1, 150, 60);
        c.Move(1, -1, 0);
        c.Puck = new ArenaBody(130, 60, HockeyCore.PuckR, 1) { Vx = 990 };
        var fastest = 0.0;
        for (var t = 0; t < 3; t++)
        {
            c.Step();
            Assert.True(Speed(c.Puck) <= HockeyCore.VMax + 1e-9);
            fastest = Math.Min(fastest, c.Puck.Vx);             // потім шайба влітає в ліві ворота й стає на подачу
        }
        Assert.True(fastest < -950, $"{fastest}");                // 990 + 1.9·(990+420) урізано до 1000
    }

    [Fact] // 17
    public void Puck_slows_down_by_friction_over_time()
    {
        var c = Bare(0, 1);
        Put(c, 0, 20, 110);
        Put(c, 1, 180, 110);
        c.Puck = new ArenaBody(100, 60, HockeyCore.PuckR, 1) { Vy = 200 };
        for (var t = 0; t < 25; t++) c.Step();
        // 250 підкроків по (1 − 0.6·0.004), відбої від бортів ще й по 0.92
        var free = 200 * Math.Pow(1 - HockeyCore.Mu * HockeyCore.H, 25 * HockeyCore.Sub);
        Assert.True(Speed(c.Puck) <= free + 1e-9);
        Assert.True(Speed(c.Puck) > 80);
    }

    [Fact] // 18
    public void Stalled_puck_gets_nudged_toward_center_after_three_seconds()
    {
        var c = Bare(0, 1);
        Put(c, 0, 20, 110);
        Put(c, 1, 180, 110);
        c.Puck = new ArenaBody(40, 30, HockeyCore.PuckR, 1) { Vx = 5 };
        for (var t = 0; t < HockeyCore.IdleTicks - 1; t++)
        {
            c.Step();
            Assert.False(c.Nudged);
        }
        c.Step();
        Assert.True(c.Nudged);
        Assert.Equal(HockeyCore.NudgeSpeed, Speed(c.Puck), 6);
        var toCenter = Math.Atan2(60 - c.Puck.Y, 100 - c.Puck.X);
        var dir = Math.Atan2(c.Puck.Vy, c.Puck.Vx);
        Assert.True(Math.Abs(dir - toCenter) <= 20.5 * Math.PI / 180, $"{dir} проти {toCenter}");
    }

    [Fact] // 19
    public void Kickoff_launches_after_75_ticks_toward_a_random_team_between_15_and_35_degrees()
    {
        double Kick(int seed)
        {
            var h = Table(2, seed);
            h.Tick(HockeyCore.StartTicks - 1);
            Assert.Equal(0, Speed(Core(h).Puck));
            Assert.Equal("ready", h.View(null).GetProperty("phase").GetString());
            h.Tick();
            Assert.Equal("go", h.View(null).GetProperty("phase").GetString());
            var p = Core(h).Puck;
            // у тику свистка шайба вже пройшла 10 підкроків тертя: 110 · 0.9976¹⁰ ≈ 107.4
            Assert.Equal(HockeyCore.KickSpeed * Math.Pow(1 - HockeyCore.Mu * HockeyCore.H, HockeyCore.Sub), Speed(p), 6);
            var off = Math.Abs(Math.Atan2(p.Vy, Math.Abs(p.Vx))) * 180 / Math.PI;
            Assert.InRange(off, 14.999, 35.001);
            return p.Vx;
        }

        Assert.Equal(Kick(3), Kick(3));
        var sides = Enumerable.Range(1, 12).Select(s => Math.Sign(Kick(s))).Distinct().Count();
        Assert.Equal(2, sides);                                   // подають то в один бік, то в інший
    }

    [Fact] // 19б (плейтест): стартовий розіграш сам у ворота не залітає — перший гол треба забити
    public void Kickoff_alone_never_scores_while_nobody_touches_the_puck()
    {
        for (var seed = 1; seed <= 60; seed++)
        {
            var h = Table(2, seed);
            h.Tick(HockeyCore.StartTicks);
            // п'ять секунд після свистка ніхто не водить біту (стоять на стартових місцях)
            for (var t = 0; t < 125; t++)
            {
                h.Tick();
                Assert.True(Core(h).S[0] + Core(h).S[1] == 0, $"сід {seed}: гол без жодного дотику на {t}-му тику");
            }
            Assert.Null(Core(h).LastTouch);
        }
    }

    // ---------- голи й партія ----------

    [Fact] // 20
    public void Goal_scores_for_the_other_team_and_serves_from_the_conceding_half()
    {
        var h = Table(2);
        Score(h, 1);
        var c = Core(h);
        Assert.Equal([0, 1], c.S);
        Assert.Equal(1, c.N);
        Assert.Equal(HockeyCore.GoalServeTicks, c.ServeIn);
        Assert.Equal((50.0, 60.0), (c.Puck.X, c.Puck.Y));
        Assert.Equal(0, Speed(c.Puck));
        var f = Frame(h);
        Assert.Equal(1, f.GetProperty("goal").GetInt32());
        Assert.Equal("[0,1]", f.GetProperty("s").GetRawText());
        Assert.Equal("[0,1]", h.View(null).GetProperty("score").GetRawText());
        Score(h, 0);
        Assert.Equal((150.0, 60.0), (c.Puck.X, c.Puck.Y));
        Assert.Equal(2, c.N);
    }

    [Fact] // 21
    public void Personal_goal_goes_to_the_last_toucher_of_the_scoring_team()
    {
        var h = Table(4);
        Score(h, 1, toucher: 3);
        var v = h.View(null);
        Assert.Equal(1, v.GetProperty("goals")[3].GetInt32());
        Assert.Equal(0, v.GetProperty("own")[3].GetInt32());
        Score(h, 1);                                             // ніхто не торкався — нікому
        Assert.Equal(1, h.View(null).GetProperty("goals")[3].GetInt32());
        Assert.Equal("[0,2]", h.View(null).GetProperty("score").GetRawText());
    }

    [Fact] // 22
    public void Own_goal_counts_for_the_team_but_is_recorded_as_own()
    {
        var h = Table(2);
        Score(h, 1, toucher: 0);                                 // синій сам заштовхав у свої
        var v = h.View(null);
        Assert.Equal("[0,1]", v.GetProperty("score").GetRawText());
        Assert.Equal(1, v.GetProperty("own")[0].GetInt32());
        Assert.Equal(0, v.GetProperty("goals")[0].GetInt32());
        Assert.Equal(0, v.GetProperty("goals")[1].GetInt32());
    }

    [Fact] // 23
    public void Match_ends_at_target_and_writes_the_journal_line()
    {
        var h = Table(2);
        for (var i = 0; i < 4; i++) Score(h, 1, toucher: 1);
        for (var i = 0; i < 7; i++) Score(h, 0, toucher: 0);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("Аерохокей: Оля 7:4 Петро", h.Room.Result!.Text);
        var fin = h.Finished.Single();
        Assert.Equal([0], fin.Result.Winners);
        Assert.Equal(7L, fin.Result.Scores![0]);
        Assert.Equal(4L, fin.Result.Scores![1]);
        var v = h.View(null);
        Assert.Equal("over", v.GetProperty("phase").GetString());
        Assert.Equal(0, v.GetProperty("winner").GetInt32());
    }

    [Fact] // 24
    public void Four_player_journal_names_both_teammates()
    {
        var h = Table(4, goals: "5");
        for (var i = 0; i < 3; i++) Score(h, 0);
        for (var i = 0; i < 5; i++) Score(h, 1, toucher: i % 2 == 0 ? 1 : 3);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("Аерохокей: Петро і Іван 5:3 Оля і Ганна", h.Room.Result!.Text);
        Assert.Equal([1, 3], h.Finished.Single().Result.Winners);
    }

    [Fact] // 25
    public void Option_goals_5_and_10_change_the_target()
    {
        Assert.Equal(5, Game(Table(2, goals: "5")).Target);
        Assert.Equal(10, Game(Table(2, goals: "10")).Target);
        Assert.Equal(7, Game(Table(2, goals: "8")).Target);
        var h = Table(2, goals: "5");
        for (var i = 0; i < 5; i++) Score(h, 1);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal(5, h.View(null).GetProperty("target").GetInt32());
    }

    [Fact] // 26
    public void Time_cap_gives_the_match_to_the_leader()
    {
        var h = Table(2);
        Score(h, 1);
        Live(h);
        Game(h).Left = 2;
        h.Tick(2);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Finished.Single().Result.Winners);
        Assert.Equal("Аерохокей: Петро 1:0 Оля", h.Room.Result!.Text);
    }

    [Fact] // 27
    public void Equal_score_at_the_cap_turns_on_golden_goal_and_the_next_goal_wins()
    {
        var h = Table(2);
        Score(h, 0);
        Score(h, 1);
        Live(h);
        Game(h).Left = 1;
        var before = h.Outbox.OfType<RoomViews>().Count();
        h.Tick();
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.True(Game(h).Golden);
        Assert.Equal(Hockey.GoldenTicks, Game(h).Left);
        Assert.True(h.View(null).GetProperty("golden").GetBoolean());
        Assert.True(Frame(h).GetProperty("golden").GetBoolean());
        Assert.True(h.Outbox.OfType<RoomViews>().Count() > before);
        Score(h, 1);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("Аерохокей: Петро 2:1 Оля", h.Room.Result!.Text);
    }

    [Fact] // 28
    public void Golden_goal_cap_ends_in_a_draw()
    {
        var h = Table(2);
        Live(h);
        Game(h).Left = 1;
        h.Tick();
        Assert.True(Game(h).Golden);
        Game(h).Left = 1;
        h.Tick();
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(h.Finished.Single().Result.Draw);
        Assert.Equal("Аерохокей: Оля 0:0 Петро — нічия", h.Room.Result!.Text);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("winner").ValueKind);
    }

    [Fact] // 29
    public void Leaving_in_a_duel_hands_the_win_to_the_other_and_closes_the_view()
    {
        var h = Table(2);
        Live(h);
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Finished.Single().Result.Winners);
        Assert.Equal("Аерохокей: Петро встав з-за столу, партію не дограли", h.Room.Result!.Text);
        var v = h.View(null);
        Assert.Equal("over", v.GetProperty("phase").GetString());
        Assert.Equal(0, v.GetProperty("winner").GetInt32());
    }

    [Fact] // 30
    public void Leaving_in_doubles_lets_the_team_play_with_one_paddle()
    {
        var h = Table(4);
        Live(h);
        h.Leave("Ганна");                                        // синя, місце 2
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Contains(h.Outbox.OfType<Journal>(), j => j.Text == "Аерохокей: Ганна встав з-за столу — сині грають одною битою");
        Assert.False(Core(h).Plays[2]);
        Assert.Equal(JsonValueKind.Null, Frame(h).GetProperty("p")[4].ValueKind);
        Assert.Equal("[[0],[1,3]]", h.View(null).GetProperty("teams").GetRawText());
        h.Tick(10);
        Score(h, 0);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact] // 31
    public void Both_teammates_leaving_hands_the_match_to_the_other_team()
    {
        var h = Table(4);
        Live(h);
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        h.Leave("Іван");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0, 2], h.Finished.Single().Result.Winners);
        Assert.Equal(0, h.View(null).GetProperty("winner").GetInt32());
    }

    [Fact] // 32
    public void Rematch_rotates_seats_but_keeps_the_teams_together()
    {
        var h = Table(4, goals: "5");
        for (var i = 0; i < 5; i++) Score(h, 0);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(h.Rematch().Ok);
        // Оля й Ганна (були сині на 0 і 2) — знову разом, тепер руді на непарних місцях
        var ola = Array.IndexOf(h.Room.Seats, "Оля");
        var hanna = Array.IndexOf(h.Room.Seats, "Ганна");
        Assert.Equal(1, ola % 2);
        Assert.Equal(1, hanna % 2);
        Assert.Equal(Core(h).Team[ola], Core(h).Team[hanna]);
        Assert.Equal(1, Core(h).Team[ola]);
        var v = h.View(null);
        Assert.Equal("ready", v.GetProperty("phase").GetString());
        Assert.Equal("[0,0]", v.GetProperty("score").GetRawText());
        var series = v.GetProperty("series");
        Assert.Equal(1, series.GetProperty("games").GetInt32());
        Assert.Equal(1, series.GetProperty("wins")[ola].GetInt32());
        Assert.Equal(1, series.GetProperty("wins")[hanna].GetInt32());

        var duo = Table(2, goals: "5");
        for (var i = 0; i < 5; i++) Score(duo, 1);
        Assert.True(duo.Rematch().Ok);
        Assert.Equal("Петро", duo.NickOf(0));                    // сторони помінялись
        Assert.Equal(0, Core(duo).Team[0]);
    }

    [Fact] // 33
    public void Views_are_identical_for_every_seat_and_the_spectator()
    {
        var h = Table(4);
        h.Input(0, "to", new { x = 40, y = 20 });
        h.Tick(90);
        var spectator = h.View(null).GetRawText();
        for (var s = 0; s < 4; s++) Assert.Equal(spectator, h.View(s).GetRawText());
        Assert.DoesNotContain("Оля", spectator);
        foreach (var name in new[] { "phase", "score", "target", "teams", "goals", "own", "left", "golden", "winner", "series", "table", "frame" })
            Assert.True(Views.Has(h.View(null), name), name);
    }

    [Fact] // 34
    public void Frame_stays_tiny()
    {
        var h = Table(4);
        var sizes = new List<int>();
        var rng = new Random(5);
        for (var t = 0; t < 400; t++)
        {
            if (t % 3 == 0) h.Input(t % 4, "to", new { x = Math.Round(rng.NextDouble() * 200, 2), y = Math.Round(rng.NextDouble() * 120, 2) });
            h.Tick();
            sizes.Add(Views.Text(h.Room.Game.Frame()).Length);
        }
        output.WriteLine($"кадр аерохокею на чотирьох: середній {sizes.Average():F0} Б, макс {sizes.Max()} Б");
        Assert.True(sizes.Max() < 300, $"кадр {sizes.Max()} Б");
    }

    [Fact] // 35
    public void Frames_fly_every_tick_while_anything_moves_and_every_fifth_when_still()
    {
        var h = Table(2);
        int Frames(int ticks)
        {
            var before = h.Outbox.OfType<RoomFrame>().Count();
            h.Tick(ticks);
            return h.Outbox.OfType<RoomFrame>().Count() - before;
        }

        Assert.InRange(Frames(40), 8, 9);                        // відлік, ніхто не рухається
        h.Input(0, "move", new { dx = 0, dy = 1 });
        Assert.Equal(3, Frames(3));                              // біта їде — щотика
        h.Input(0, "move", new { dx = 0, dy = 0 });
        Live(h);
        Assert.Equal(25, Frames(25));                            // шайба в грі — щотика
    }

    [Fact] // 36
    public void Same_seed_and_input_log_give_the_same_view()
    {
        static string Play(int seed)
        {
            var h = Table(4, seed);
            for (var t = 0; t < 2000 && h.Room.Status == RoomStatus.Playing; t++)
            {
                if (t % 7 == 0) h.Input(t % 4, "to", new { x = (t * 37) % 200, y = (t * 53) % 120 });
                if (t % 31 == 0) h.Input((t + 1) % 4, "move", new { dx = (t % 3) - 1, dy = ((t / 3) % 3) - 1 });
                h.Tick();
            }
            return h.View(null).GetRawText();
        }

        Assert.Equal(Play(8), Play(8));
        Assert.NotEqual(Play(8), Play(9));
    }

    [Fact] // 37
    public void To_and_move_accept_exactly_what_the_module_sends()
    {
        var h = Table(2);
        Assert.True(h.Act(0, "to", new { x = 42.5, y = 61.25 }).Ok);     // так шле hockey.js
        h.Tick(10);
        Assert.Equal((42.5, 61.25), (Core(h).Pads[0].X, Core(h).Pads[0].Y));
        Assert.True(h.Act(0, "move", new { dx = 1, dy = -1 }).Ok);
        Assert.True(h.Act(1, "move", new { dx = 0, dy = 0 }).Ok);
        Assert.Equal("Тут так не ходять", h.Act(0, "to", new { x = "a", y = 3 }).Message);
        Assert.Equal("Тут так не ходять", h.Act(0, "to", new { x = (double?)null, y = 3 }).Message);   // NaN з браузера летить як null
        Assert.Equal("Тут так не ходять", h.Act(0, "to", new { y = 3 }).Message);
        Assert.Equal("Тут так не ходять", h.Act(0, "move", 3).Message);
        Assert.Equal("Тут так не ходять", h.Act(0, "move", new { dx = "ліво" }).Message);
        Assert.Equal("Тут так не ходять", h.Act(0, "shoot").Message);
        var x = Core(h).Pads[0].X;
        h.Tick();
        Assert.True(Core(h).Pads[0].X > x);                      // лишився останній добрий ввід — move (1, −1)
    }

    [Fact] // 38
    public void Achievements_dry_and_comeback_are_requested_only_when_earned()
    {
        var dry = Table(2, goals: "5");
        for (var i = 0; i < 5; i++) Score(dry, 0);
        Assert.Contains(dry.Awards, a => a.Reason == "ach:hockey-dry" && a.Nick == "Оля");
        Assert.DoesNotContain(dry.Awards, a => a.Reason == "ach:hockey-comeback");

        var back = Table(4, goals: "5");
        for (var i = 0; i < 3; i++) Score(back, 1);
        for (var i = 0; i < 5; i++) Score(back, 0);
        Assert.Equal(RoomStatus.Finished, back.Room.Status);
        Assert.Equal(["Ганна", "Оля"], back.Awards.Where(a => a.Reason == "ach:hockey-comeback").Select(a => a.Nick).Order());
        Assert.DoesNotContain(back.Awards, a => a.Reason == "ach:hockey-dry");

        var plain = Table(2, goals: "5");
        for (var i = 0; i < 2; i++) Score(plain, 1);
        for (var i = 0; i < 5; i++) Score(plain, 0);
        Assert.Empty(plain.Awards);
    }

    [Fact] // 39
    [Trait("Category", "Perf")]
    public void Five_thousand_ticks_of_doubles_stay_under_a_second()
    {
        var h = Table(4, goals: "10");
        var rng = new Random(3);
        // Цілі — наперед, щоб міряти гру, а не серіалізацію тестових анонімних об'єктів.
        var aims = Enumerable.Range(0, 64).Select(_ => Views.Payload(new { x = rng.NextDouble() * 200, y = rng.NextDouble() * 120 })).ToArray();
        h.Tick(80);
        var sw = Stopwatch.StartNew();
        var ticks = 0;
        var matches = 1;
        for (var t = 0; t < 5000; t++)
        {
            if (h.Room.Status != RoomStatus.Playing)
            {
                sw.Stop();                                       // «Ще раз» поза заміром
                Assert.True(h.Rematch().Ok);
                matches++;
                sw.Start();
            }
            for (var s = 0; s < 4; s++) h.Input(s, "to", aims[(t * 4 + s) % aims.Length]);
            h.Tick();
            ticks++;
        }
        sw.Stop();
        var per = sw.Elapsed.TotalMilliseconds / ticks;
        output.WriteLine($"Аерохокей двоє на двоє: {ticks} тиків кімнати ({matches} партій) за {sw.Elapsed.TotalMilliseconds:F0} мс — {per:F4} мс на тик (з вводом і кадром)");
        Assert.Equal(5000, ticks);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"{ticks} тиків зайняли {sw.Elapsed}");
        Assert.True(per <= 0.25);
    }

    [Fact] // 40: передбачення своєї біти в браузері = сервер
    public void Prediction_matches_the_browser_fixture()
    {
        var fx = ArenaPredictFixture.Load();
        var logs = fx["hockey"]!.AsArray();
        Assert.True(logs.Count >= 5);
        var i = 0;
        foreach (var log in logs) ArenaPredictFixture.AssertSame(ArenaPredictFixture.RunHockey(log!), log!["expect"]!, $"журнал {i++}");
    }

    [Fact] // 41
    public void A_gentle_touch_does_not_reset_the_stall_clock()
    {
        // Шайба ледь повзе в біту, що стоїть, і відскакує так само ледь-ледь: дотик був, а застій лишився.
        // (Застій міряємо лише швидкістю: шайбу, притиснуту до борта, біта тримає на місці — і через три секунди
        // сервер її штовхне, хоч дотик і триває.)
        var c = Bare(0, 1);
        Put(c, 1, 180, 110);
        Put(c, 0, 40, 60);
        c.Puck = new ArenaBody(40 + HockeyCore.PadR + HockeyCore.PuckR + 0.2, 60, HockeyCore.PuckR, 1) { Vx = -10 };
        var touched = false;
        for (var t = 0; t < HockeyCore.IdleTicks - 1; t++)
        {
            c.Step();
            touched |= c.HitBy == 0;
            Assert.False(c.Nudged, $"тик {t}");
        }
        Assert.True(touched);
        Assert.True(Speed(c.Puck) < HockeyCore.IdleSpeed);
        c.Step();
        Assert.True(c.Nudged);
    }

    [Fact] // 42
    public void Catalog_passport_matches_the_spec()
    {
        var h = Table(2);
        var info = h.Room.Info;
        Assert.Equal(("hockey", GameGroup.Live, 2, 4, 40, StartMode.ByHost, false, false),
            (info.Id, info.Group, info.MinPlayers, info.MaxPlayers, info.TickMs, info.Start, info.Rated, info.Hidden));
        Assert.Equal("7", info.Options![0].Default);
        var one = new RoomHarness("hockey");
        one.Join("Оля");
        Assert.False(one.Start().Ok);
    }

    [Fact] // 43
    public void Clock_runs_only_after_the_whistle_and_pauses_nothing_during_serves()
    {
        var h = Table(2);
        h.Tick(HockeyCore.StartTicks - 1);
        Assert.Equal(Hockey.MatchTicks, Game(h).Left);
        h.Tick();
        Assert.Equal(Hockey.MatchTicks - 1, Game(h).Left);
        Score(h, 0);
        var left = Game(h).Left;
        h.Tick(10);                                              // пауза після гола — годинник іде
        Assert.Equal(left - 10, Game(h).Left);
        Assert.Equal(left - 10, Frame(h).GetProperty("left").GetInt32());
    }

    [Fact] // 44: захисник лише зачепив удар — гол нападника, а не автогол
    public void A_deflection_by_the_defender_still_counts_for_the_attacker()
    {
        var h = Table(4);
        Live(h);
        var c = Core(h);
        // Ганна (місце 2, сині) б'є, Петро (1, руді) зачіпає — шайба все одно в рудих воротах
        c.LastTouch = 2;
        Score(h, 0, toucher: 1);
        var v = h.View(null);
        Assert.Equal(1, v.GetProperty("goals")[2].GetInt32());
        Assert.Equal(0, v.GetProperty("own")[1].GetInt32());
        // новий розіграш — старий дотик Ганни вже не рахується: Петро сам заштовхав у свої
        Score(h, 0, toucher: 1);
        v = h.View(null);
        Assert.Equal(1, v.GetProperty("goals")[2].GetInt32());
        Assert.Equal(1, v.GetProperty("own")[1].GetInt32());
        Assert.Equal("[2,0]", v.GetProperty("score").GetRawText());
    }

    [Fact] // 45: дотик за командою пише сама фізика удару, а не лише тестовий сетер
    public void A_real_paddle_hit_is_what_credits_the_goal()
    {
        var c = Bare(0, 1);
        Put(c, 1, 120, 60);
        Put(c, 0, 60, 30);
        // рудий б'є шайбу ліворуч, вона пролітає повз синього й залітає в сині ворота
        c.Puck = new ArenaBody(120 - HockeyCore.PadR - HockeyCore.PuckR - 0.5, 60, HockeyCore.PuckR, 1) { Vx = -1 };
        c.Move(1, -1, 0);
        var scored = -1;
        for (var t = 0; t < 60 && scored < 0; t++)
        {
            if (t == 1) c.Move(1, 0, 0);
            scored = c.Step();
        }
        Assert.Equal(1, scored);
        Assert.Equal(1, c.Goals[1]);
        Assert.Equal(0, c.Own[0]);
    }

    [Fact] // 46: вільні місця в шапці картки — за парністю, як на столі на чотирьох, і до, і після «Почати»
    public void Free_seats_are_named_by_parity_before_and_after_the_start()
    {
        var h = new RoomHarness("hockey", new { goals = "7" });
        h.Join("Оля");
        h.Join("Петро");
        Assert.Equal(["синій", "рудий", "синій", "рудий"], Enumerable.Range(0, 4).Select(h.Room.Game.SeatName));
        Assert.True(h.Start().Ok);
        // на двох Петро (місце 1) — рудий, а вільне місце 2 — синє, а не «рудий вільно»
        Assert.Equal(["синій", "рудий", "синій", "рудий"], Enumerable.Range(0, 4).Select(h.Room.Game.SeatName));
    }

    // ---------- притиснута шайба (після рецензії коду) ----------

    /// <summary>Інваріант живої гри: після кроку центр шайби не ближче до жодної біти, ніж сума радіусів.</summary>
    static void PuckOutsidePads(HockeyCore c, string what)
    {
        for (var i = 0; i < HockeyCore.Seats; i++)
        {
            if (!c.Plays[i]) continue;
            var dx = c.Puck.X - c.Pads[i].X;
            var dy = c.Puck.Y - c.Pads[i].Y;
            var d = Math.Sqrt(dx * dx + dy * dy);
            Assert.True(d >= HockeyCore.PadR + HockeyCore.PuckR - 1e-6, $"{what}: шайба в біті {i} (між центрами {d:0.###})");
        }
    }

    [Fact] // 47: біта тисне шайбу в бічний борт — шайба лишається між бітою й бортом, а не вистрибує крізь біту
    public void A_puck_pressed_into_the_side_rail_stays_between_the_paddle_and_the_rail()
    {
        var c = Bare(0, 1);
        Put(c, 1, 180, 110);
        Put(c, 0, 50, 30);
        c.Puck = new ArenaBody(50, 10, HockeyCore.PuckR, 1);
        c.Aim(0, 50, HockeyCore.PadR);                           // біта рветься в борт крізь шайбу
        for (var t = 0; t < 30; t++)
        {
            c.Step();
            PuckOutsidePads(c, $"тик {t}");
            Assert.True(c.Puck.Y < c.Pads[0].Y, $"тик {t}: шайба проскочила під біту (шайба {c.Puck.Y:0.#}, біта {c.Pads[0].Y:0.#})");
            Assert.True(c.Puck.Y >= HockeyCore.PuckR - 1e-9);
            Assert.True(Speed(c.Puck) < 1, $"тик {t}: затиснута шайба летить {Speed(c.Puck):0}");
        }
        // біта вперлась у затиснуту шайбу: між центрами рівно дотик
        Assert.Equal(HockeyCore.PuckR + HockeyCore.PadR + HockeyCore.PuckR, c.Pads[0].Y, 6);
    }

    [Fact] // 48: те саме біля торця поза прорізом воріт — і шайба не летить навиворіт у бік суперника
    public void A_puck_pressed_into_the_end_wall_does_not_shoot_through_the_paddle()
    {
        var c = Bare(0, 1);
        Put(c, 1, 180, 110);
        Put(c, 0, 40, 20);
        c.Puck = new ArenaBody(10, 20, HockeyCore.PuckR, 1);
        c.Aim(0, HockeyCore.PadR, 20);
        for (var t = 0; t < 30; t++)
        {
            var scored = c.Step();
            Assert.Equal(-1, scored);
            PuckOutsidePads(c, $"тик {t}");
            Assert.True(c.Puck.X < c.Pads[0].X, $"тик {t}: шайба праворуч від біти ({c.Puck.X:0.#} проти {c.Pads[0].X:0.#})");
            Assert.True(c.Puck.Vx < 1, $"тик {t}: шайба полетіла до суперника, vx = {c.Puck.Vx:0}");
        }
    }

    [Fact] // 49: біта йде навскіс через шайбу біля борта — шайба вичавлюється вбік, як кісточка, а не крізь біту
    public void A_puck_squeezed_at_an_angle_squirts_out_sideways()
    {
        var c = Bare(0, 1);
        Put(c, 1, 180, 110);
        Put(c, 0, 58, 26);
        c.Puck = new ArenaBody(50, 8, HockeyCore.PuckR, 1);
        c.Aim(0, 40, HockeyCore.PadR);                           // униз ліворуч — просто через шайбу біля борта
        for (var t = 0; t < 5; t++)                                 // біта 900/с вичавлює швидко — далі шайба вже б'ється об чужу біту
        {
            c.Step();
            PuckOutsidePads(c, $"тик {t}");
            Assert.True(c.Puck.Y >= HockeyCore.PuckR - 1e-9);
            Assert.True(Speed(c.Puck) <= 2 * HockeyCore.PadSpeed, $"тик {t}: {Speed(c.Puck):0}");
        }
        // біта проїхала ліворуч — шайба вислизнула праворуч і котиться геть
        Assert.True(c.Puck.X > c.Pads[0].X + HockeyCore.PadR + HockeyCore.PuckR);
        Assert.True(c.Puck.Vx > 0);
    }

    [Fact] // 50: біти суперників затисли шайбу на центральній лінії — шайба не в біті, біти поступаються
    public void A_puck_sandwiched_between_rival_paddles_stays_outside_both()
    {
        var c = Bare(0, 1);
        Put(c, 0, 80, 60);
        Put(c, 1, 120, 60);
        c.Aim(0, 100, 60);                                       // обидві рвуться в центр, до своєї межі 92 / 108
        c.Aim(1, 100, 60);
        for (var t = 0; t < 30; t++)
        {
            c.Step();
            PuckOutsidePads(c, $"тик {t}");
            Assert.True(c.Pads[0].X < c.Puck.X && c.Puck.X < c.Pads[1].X, $"тик {t}: шайба вискочила з-поміж біт");
        }
    }

    [Fact] // 51: фаз — чотири біти ганяють шайбу до бортів і в кути; після кожного тику шайба не в біті й на столі
    public void Four_paddles_chasing_the_puck_into_the_rails_never_swallow_it()
    {
        var rng = new Random(7);
        var c = Bare(0, 1, 2, 3);
        var inside = 0;
        for (var t = 0; t < 20000; t++)
        {
            for (var s = 0; s < HockeyCore.Seats; s++)
            {
                // ціль — «за шайбою»: точка з того боку шайби, що ближче до найближчого борта чи торця
                var px = c.Puck.X;
                var py = c.Puck.Y;
                var wx = px < 100 ? -1 : 1;
                var wy = py < 60 ? -1 : 1;
                var mode = (t / 40 + s) % 3;
                var (tx, ty) = mode switch
                {
                    0 => (px, py + wy * 30),                     // у бічний борт
                    1 => (px + wx * 30, py),                     // у торець
                    _ => (px + wx * 20, py + wy * 20),           // у кут
                };
                c.Aim(s, tx + rng.NextDouble() * 4 - 2, ty + rng.NextDouble() * 4 - 2);
            }
            c.Step();
            if (c.ServeIn > 0)
            {
                // гол чи «притримав» — шайбу кидаємо деінде й одразу в гру (на цей тик інваріант не міряємо)
                c.ServeIn = 0;
                c.Puck = new ArenaBody(20 + rng.NextDouble() * 160, 10 + rng.NextDouble() * 100, HockeyCore.PuckR, 1);
                continue;
            }
            for (var i = 0; i < HockeyCore.Seats; i++)
            {
                var dx = c.Puck.X - c.Pads[i].X;
                var dy = c.Puck.Y - c.Pads[i].Y;
                if (Math.Sqrt(dx * dx + dy * dy) < HockeyCore.PadR + HockeyCore.PuckR - 1e-6) inside++;
            }
            Assert.InRange(c.Puck.Y, HockeyCore.PuckR - 1e-6, HockeyCore.TableH - HockeyCore.PuckR + 1e-6);
        }
        Assert.Equal(0, inside);
    }

    [Fact] // 52 (ідея плейтесту): гол із підписом — автогол і «з-під борту» сервер знає сам і кладе у вид
    public void Last_goal_knows_own_goals_and_bank_shots()
    {
        var h = Table(2);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("lastGoal").ValueKind);
        Score(h, 0, toucher: 1);                                 // Петро (руді) сам заштовхав у свої
        var lg = h.View(null).GetProperty("lastGoal");
        Assert.Equal((0, 1, true, false),
            (lg.GetProperty("team").GetInt32(), lg.GetProperty("n").GetInt32(), lg.GetProperty("own").GetBoolean(), lg.GetProperty("rail").GetBoolean()));

        // від борта в ворота — «з-під борту»
        var c = Bare(0, 1);
        Put(c, 1, 180, 20);
        c.Puck = new ArenaBody(140, 100, HockeyCore.PuckR, 1) { Vx = 300, Vy = 300 };
        c.LastTouch = 0;
        var scored = -1;
        for (var t = 0; t < 20 && scored < 0; t++) scored = c.Step();
        Assert.Equal(0, scored);
        Assert.True(c.GoalRail);
        Assert.False(c.GoalOwn);

        // просто в ворота — без борту
        c.ServeIn = 0;
        c.Puck = new ArenaBody(180, 60, HockeyCore.PuckR, 1) { Vx = 400 };
        Put(c, 1, 180, 100);
        scored = -1;
        for (var t = 0; t < 20 && scored < 0; t++) scored = c.Step();
        Assert.Equal(0, scored);
        Assert.False(c.GoalRail);
    }

    [Fact] // 53: шайба на подачі й на відліку стоїть — біта на неї не наїжджає, а впирається на дотик
    public void Paddles_do_not_cover_the_puck_waiting_for_the_serve_or_the_whistle()
    {
        var h = Table(2);
        var c = Core(h);
        // на відліку синя біта рветься до центральної лінії, де лежить шайба (дотягується до 92 + 12.5 > 100)
        c.Aim(0, 100, 60);
        for (var t = 0; t < 40; t++)
        {
            h.Tick();
            PuckOutsidePads(c, $"відлік, тик {t}");
        }
        Assert.Equal((100.0, 60.0), (c.Puck.X, c.Puck.Y));      // шайба на місці, біти її не зсунули
        Score(h, 1);                                            // гол синім — подача з (50, 60)
        Assert.True(c.ServeIn > 0);
        c.Aim(0, 50, 60);                                       // синій стає просто на місце подачі
        for (var t = 0; t < HockeyCore.ServeTicks - 1; t++)
        {
            h.Tick();
            PuckOutsidePads(c, $"подача, тик {t}");
        }
        Assert.Equal((50.0, 60.0), (c.Puck.X, c.Puck.Y));
        Assert.Equal(HockeyCore.PadR + HockeyCore.PuckR, Math.Sqrt(Math.Pow(c.Pads[0].X - 50, 2) + Math.Pow(c.Pads[0].Y - 60, 2)), 6);
    }

    [Fact] // 54 (жива перевірка): напарники затисли шайбу в кутку — за три секунди подача суперникам, гра не стоїть
    public void Teammates_trapping_the_puck_in_a_corner_lose_the_serve_to_the_rivals()
    {
        var c = Bare(0, 1, 2, 3);
        Put(c, 1, 180, 60);
        Put(c, 3, 170, 90);
        Put(c, 0, 30, 8);
        Put(c, 2, 8, 30);
        c.Puck = new ArenaBody(HockeyCore.PuckR, HockeyCore.PuckR, HockeyCore.PuckR, 1);
        c.Aim(0, 0, 0);                                         // обидві сині тиснуть шайбу в лівий верхній кут:
        c.Aim(2, 0, 0);                                         // одна вздовж борта, друга вздовж торця
        var n = c.N;
        var t = 0;
        for (; t < HockeyCore.HoldTicks + 40 && c.FoulTo is null; t++)
        {
            Assert.Equal(-1, c.Step());
            if (c.FoulTo is null) PuckOutsidePads(c, $"тик {t}");
        }
        Assert.Equal(1, c.FoulTo);                              // шайбу забрали — подача рудим
        Assert.InRange(t, HockeyCore.HoldTicks - 5, HockeyCore.HoldTicks + 20);
        Assert.Equal((150.0, 60.0), (c.Puck.X, c.Puck.Y));
        Assert.Equal(HockeyCore.ServeTicks, c.ServeIn);
        Assert.Equal(n + 1, c.N);
        Assert.Equal([0, 0], c.S);
        Assert.Null(c.LastTouch);
        c.Step();
        Assert.Null(c.FoulTo);                                  // подія — лише в тому тику
    }

    [Fact] // 55: довгий дотик — один удар: біта, що тисне шайбу в борт, не набиває «удари» щопідкроку
    public void A_long_press_is_one_hit_and_holding_it_for_three_seconds_gives_the_serve_away()
    {
        var c = Bare(0, 1);
        Put(c, 1, 180, 110);
        Put(c, 0, 50, 30);
        c.Puck = new ArenaBody(50, 10, HockeyCore.PuckR, 1);
        c.Aim(0, 50, HockeyCore.PadR);
        var hits = 0;
        for (var t = 0; t < 40; t++)
        {
            c.Step();
            if (c.HitBy == 0) hits++;
        }
        Assert.Equal(1, hits);
        Assert.Equal(1, c.Rally);
        for (var t = 0; t < HockeyCore.HoldTicks && c.FoulTo is null; t++) c.Step();
        Assert.Equal(1, c.FoulTo);                              // синій притримав на своїй половині — подача рудим
        Assert.Equal((150.0, 60.0), (c.Puck.X, c.Puck.Y));
    }

    [Fact] // 56: кадр каже, кому віддали шайбу за притримування
    public void Frame_carries_the_foul_only_in_its_tick()
    {
        var h = Table(2);
        Live(h);
        var c = Core(h);
        Assert.Equal(JsonValueKind.Null, Frame(h).GetProperty("foul").ValueKind);
        c.Puck = new ArenaBody(50, 4.5, HockeyCore.PuckR, 1);
        c.Pads[0].X = 50;
        c.Pads[0].Y = 17;
        c.Aim(0, 50, HockeyCore.PadR);
        c.Held = HockeyCore.HoldTicks - 1;
        h.Tick();
        Assert.Equal(1, Frame(h).GetProperty("foul").GetInt32());
        h.Tick();
        Assert.Equal(JsonValueKind.Null, Frame(h).GetProperty("foul").ValueKind);
    }
}
