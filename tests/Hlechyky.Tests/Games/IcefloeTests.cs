using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Крижина: фізику, кригу й сніжки перевіряємо на голому <see cref="IcefloeCore"/> (там тіло можна поставити
/// рівно туди, куди треба), а раунди, партію, вихід і кадри — через кімнату, як гратимуть люди.
/// Номери в коментарях — пункти плану тестів зі spec §8.
/// </summary>
[Collection(SerialPerf.Name)]
public class IcefloeTests(ITestOutputHelper output)
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марко", "Соня", "Тарас", "Леся"];
    const double C = IcefloeCore.Cx;

    // ---------- підмостки ----------

    static RoomHarness Table(int players = 2, int seed = 42, string wins = "2")
    {
        var h = new RoomHarness("icefloe", new { wins }, seed);
        foreach (var nick in Nicks.Take(players)) h.Join(nick);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static Icefloe Game(RoomHarness h) => (Icefloe)h.Room.Game;
    static IcefloeCore Core(RoomHarness h) => Game(h).Core;
    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString() ?? "";

    static void ToGo(RoomHarness h)
    {
        for (var i = 0; i < 300 && Game(h).Phase != Icefloe.PhGo && h.Room.Status == RoomStatus.Playing; i++) h.Tick();
        Assert.Equal(Icefloe.PhGo, Game(h).Phase);
    }

    /// <summary>Тіло — за край ставка (у кут), на наступному підкроці шубовсне.</summary>
    static void Sink(RoomHarness h, int seat)
    {
        var b = Core(h).Bodies[seat];
        b.B.X = 40;
        b.B.Y = 40;
        b.B.Vx = b.B.Vy = 0;
    }

    /// <summary>Закінчити раунд: усіх, крім переможця, — у воду (−1 — усіх разом, нічия), і дочекатись наступної гри або кінця.</summary>
    static void Round(RoomHarness h, int winner)
    {
        ToGo(h);
        for (var s = 0; s < IcefloeCore.Seats; s++)
            if (s != winner && Core(h).Bodies[s].Plays && Core(h).Bodies[s].Alive) Sink(h, s);
        h.Tick();
        Assert.Equal(Icefloe.PhEnd, Game(h).Phase);
        for (var i = 0; i < 200 && h.Room.Status == RoomStatus.Playing && Game(h).Phase != Icefloe.PhGo; i++) h.Tick();
    }

    /// <summary>Голе ядро з рівною великою кригою і n тілами на спавнах.</summary>
    static IcefloeCore Bare(int n = 2, int seed = 1, double radius = 1100)
    {
        var c = new IcefloeCore(new Random(seed));
        c.ResetParty([.. Enumerable.Range(0, IcefloeCore.Seats).Select(i => i < n)]);
        c.NewRound(1100);
        Flat(c, radius);
        return c;
    }

    static void Flat(IcefloeCore c, double r)
    {
        for (var i = 0; i < IcefloeCore.Vertices; i++) c.R[i] = r;
    }

    static IcefloeBody Put(IcefloeCore c, int seat, double x, double y, double vx = 0, double vy = 0)
    {
        var b = c.Bodies[seat];
        b.Plays = b.Alive = true;
        b.B = new ArenaBody(x, y, IcefloeCore.BodyR, 1) { Vx = vx, Vy = vy };
        b.Want = -1;
        return b;
    }

    static double Speed(IcefloeBody b) => Math.Sqrt(b.B.Vx * b.B.Vx + b.B.Vy * b.B.Vy);

    static JsonElement Frame(RoomHarness h)
    {
        var room = h.Room;
        lock (room.Sync) return Views.Json(room.Game.Frame());
    }

    static List<int[]> Events(IcefloeCore c) => [.. c.Events()];

    // ---------- світ і старт ----------

    [Fact] // 1
    public void Base_radius_grows_with_the_table()
    {
        Assert.Equal(800, IcefloeCore.BaseRadius(2));
        Assert.Equal(800, IcefloeCore.BaseRadius(3));
        Assert.Equal(950, IcefloeCore.BaseRadius(4));
        Assert.Equal(950, IcefloeCore.BaseRadius(5));
        Assert.Equal(1100, IcefloeCore.BaseRadius(6));
        Assert.Equal(1100, IcefloeCore.BaseRadius(8));

        var h = Table(4);
        var ice = h.View(null).GetProperty("ice");
        Assert.Equal(950, ice.GetProperty("r0").GetInt32());
        foreach (var v in ice.GetProperty("v").EnumerateArray())
            Assert.InRange(v.GetInt32(), (int)Math.Floor(950 * 0.92), (int)Math.Ceiling(950 * 1.08));
        // найбільша крижина все одно не лізе на берег, а берег на вісьмох не виходить за ставок
        foreach (var r0 in new[] { 800, 950, 1100 })
            Assert.True(r0 * 1.08 < IcefloeCore.ShoreOf(r0) - IcefloeCore.BodyR / 2);
        Assert.Equal(IcefloeCore.ShoreOf(950), ice.GetProperty("r0").GetInt32() * 1.08 + IcefloeCore.ShoreGap, 0);
        Assert.Equal((int)IcefloeCore.ShoreOf(950), h.View(null).GetProperty("shore").GetInt32());
        Assert.True(IcefloeCore.ShoreOf(1100) + IcefloeCore.BankGap < IcefloeCore.Pond / 2);   // вибулі стоять у межах ставка
    }

    [Fact] // 2
    public void Spawns_are_evenly_spread_and_face_the_center()
    {
        for (var n = 2; n <= 8; n++)
        {
            var c = new IcefloeCore(new Random(n));
            c.ResetParty([.. Enumerable.Range(0, IcefloeCore.Seats).Select(i => i < n)]);
            var r0 = IcefloeCore.BaseRadius(n);
            c.NewRound(r0);
            for (var k = 0; k < n; k++)
            {
                var b = c.Bodies[k].B;
                var dx = b.X - C;
                var dy = b.Y - C;
                Assert.Equal(0.6 * r0, Math.Sqrt(dx * dx + dy * dy), 6);
                var want = -90 + k * 360.0 / n;
                var got = Math.Atan2(dy, dx) * 180 / Math.PI;
                var diff = ((got - want) % 360 + 540) % 360 - 180;
                Assert.True(Math.Abs(diff) < 1e-6, $"n={n}, k={k}: кут {got}, треба {want}");
                // обличчям до центру: сектор не далі за пів сектора від напрямку на центр
                var (fx, fy) = ArenaPhysics.Dir16(c.Bodies[k].Face);
                Assert.True(-(fx * dx + fy * dy) / Math.Sqrt(dx * dx + dy * dy) > Math.Cos(11.3 * Math.PI / 180));
                Assert.Equal(0, b.Vx);
            }
            Assert.Equal(1, c.Bodies[0].Face is 4 ? 1 : 0);   // перший угорі — дивиться вниз
        }
    }

    [Fact] // 3
    public void Lobby_view_shows_fresh_ice_without_spending_rng()
    {
        static RoomHarness Seated(bool peek)
        {
            var h = new RoomHarness("icefloe", new { wins = "2" }, 77);
            h.Join("Оля");
            h.Join("Петро");
            if (peek)
                for (var i = 0; i < 5; i++) h.View(null);
            return h;
        }

        var looked = Seated(true);
        var v = looked.View(null);
        Assert.Equal("lobby", v.GetProperty("phase").GetString());
        Assert.All(v.GetProperty("ice").GetProperty("v").EnumerateArray(), x => Assert.Equal(800, x.GetInt32()));
        var p = v.GetProperty("frame").GetProperty("p");
        Assert.NotEqual(JsonValueKind.Null, p[0].ValueKind);
        Assert.NotEqual(JsonValueKind.Null, p[1].ValueKind);
        Assert.Equal(JsonValueKind.Null, p[2].ValueKind);

        // Хто довго дивився на стіл до старту, той і отримав ту саму кригу, що й той, хто не дивився.
        var quiet = Seated(false);
        looked.Start();
        quiet.Start();
        Assert.Equal(quiet.View(null).GetProperty("ice").GetRawText(), looked.View(null).GetProperty("ice").GetRawText());
    }

    [Fact] // 4
    public void Edge_at_follows_the_straight_edge_between_vertices()
    {
        var c = Bare();
        for (var i = 0; i < IcefloeCore.Vertices; i++) c.R[i] = 900 + 10 * i;
        var step = Math.PI / 12;
        for (var i = 0; i < IcefloeCore.Vertices; i++) Assert.Equal(c.R[i], c.EdgeAt(i * step), 6);

        // між двома рівними вершинами край — хорда: посередині cos 7.5° від радіуса, а не сам радіус
        Flat(c, 1000);
        Assert.Equal(1000 * Math.Cos(step / 2), c.EdgeAt(step / 2), 6);
        Assert.True(c.OnIce(C + 1000, C));                 // рівно у вершині — ще на кризі
        Assert.False(c.OnIce(C + 1000.5, C));
        Assert.False(c.OnIce(C + 995 * Math.Cos(step / 2), C + 995 * Math.Sin(step / 2)));   // хорда проходить ближче
        // танення їсть кожну вершину, а розтала до нуля — з'їдає край
        c.Melt = 400;
        Assert.Equal(600, c.EdgeAt(0), 6);
        c.Melt = 1200;
        Assert.Equal(0, c.EdgeAt(1.0));
        Assert.False(c.OnIce(C + 1, C));
    }

    // ---------- рух ----------

    [Fact] // 5
    public void Thrust_reaches_ninety_percent_of_terminal_speed_within_thirty_ticks()
    {
        var c = Bare(1, radius: 1188);
        var b = Put(c, 0, C - 600, C);
        c.Move(0, 0);
        // Тяга діє до тертя в тому самому підкроці: гранична швидкість 1800·0.96/2 = 864 (spec округлював до 900).
        var terminal = IcefloeCore.Thrust * (1 - IcefloeCore.Mu * IcefloeCore.H) / IcefloeCore.Mu;
        for (var t = 0; t < 30; t++) c.Step(true);
        Assert.True(b.Alive);
        Assert.InRange(Speed(b), 0.9 * terminal, terminal);
        for (var t = 0; t < 30; t++) c.Step(true);
        Assert.True(Speed(b) < 900);
    }

    [Fact] // 6
    public void Speed_never_exceeds_the_cap_even_after_a_dash_into_a_collision()
    {
        var c = Bare(2, radius: 1188);
        var a = Put(c, 0, C - 200, C, 1000, 0);
        var b = Put(c, 1, C + 100, C, -1000, 0);
        a.Face = 0;
        Assert.Null(c.Dash(0));
        Assert.Equal(IcefloeCore.VMax, Speed(a), 6);
        c.Move(0, 0);
        c.Move(1, 8);
        var met = false;
        for (var t = 0; t < 10; t++)
        {
            c.Step(true);
            met |= Events(c).Any(e => e[0] == IcefloeCore.EvBump);
            Assert.True(Speed(a) <= IcefloeCore.VMax + 1e-9 && Speed(b) <= IcefloeCore.VMax + 1e-9);
        }
        Assert.True(met);
        // сніжка в тіло, що вже летить на стелі, стелю теж не пробиває
        b.B.Vx = IcefloeCore.VMax;
        b.B.Vy = 0;
        b.B.X = C;
        b.B.Y = C;
        a.B.X = C - 500;
        a.B.Y = C - 500;
        c.Balls[0] = new IcefloeBall { On = true, Id = 99, Owner = 0, Ttl = 20, X = C - 100, Y = C, Vx = 800, Vy = 0 };
        c.Step(true);
        Assert.True(Speed(b) <= IcefloeCore.VMax + 1e-9);
    }

    [Fact] // 7
    public void Releasing_the_key_lets_the_body_slide_to_a_stop()
    {
        var c = Bare(1, radius: 1188);
        var b = Put(c, 0, C - 700, C, 864, 0);
        c.Move(0, -1);
        var x0 = b.B.X;
        for (var t = 0; t < 50; t++) c.Step(true);
        Assert.True(b.Alive);
        Assert.True(Speed(b) < 30, $"швидкість {Speed(b)}");
        Assert.InRange(b.B.X - x0, 380, 460);   // «вільне ковзання до зупинки ≈ 450 см»
    }

    [Fact] // 8
    public void Face_follows_the_last_held_sector_and_survives_release()
    {
        var c = Bare();
        c.Move(0, 5);
        Assert.Equal(5, c.Bodies[0].Face);
        Assert.Equal(5, c.Bodies[0].Want);
        c.Move(0, -1);
        Assert.Equal(5, c.Bodies[0].Face);
        Assert.Equal(-1, c.Bodies[0].Want);
        c.Move(0, 12);
        Assert.Equal(12, c.Bodies[0].Face);
    }

    [Fact] // 9
    public void Spikes_cut_terminal_speed_and_slide_distance()
    {
        double Slide(bool spikes)
        {
            var c = Bare(1, radius: 1188);
            var b = Put(c, 0, C - 900, C, 800, 0);
            b.Spikes = spikes ? 100000 : 0;
            var x0 = b.B.X;
            for (var t = 0; t < 150; t++) c.Step(true);
            return b.B.X - x0;
        }

        var ratio = Slide(false) / Slide(true);
        Assert.InRange(ratio, 2.3, 2.7);

        var k = Bare(1, radius: 1188);
        var s = Put(k, 0, C - 900, C);
        s.Spikes = 100000;
        k.Move(0, 0);
        for (var t = 0; t < 60; t++) k.Step(true);
        Assert.InRange(Speed(s), 300, 360);   // гранична в шипах ≈ 1800·0.9/5 = 324
    }

    [Fact] // 10
    public void Diagonal_sector_moves_along_the_expected_angle()
    {
        var c = Bare(1, radius: 1188);
        var b = Put(c, 0, C - 300, C - 300);
        c.Move(0, 2);
        for (var t = 0; t < 10; t++) c.Step(true);
        Assert.True(b.B.Vx > 100);
        Assert.Equal(b.B.Vx, b.B.Vy, 9);        // 45° униз-праворуч (y униз)
        Assert.Equal(b.B.X - (C - 300), b.B.Y - (C - 300), 9);
    }

    // ---------- зіткнення й ривок ----------

    [Fact] // 11
    public void Head_on_collision_of_equal_bodies_swaps_velocity_with_restitution()
    {
        var a = new ArenaBody(0, 0, 60, 1) { Vx = 100 };
        var b = new ArenaBody(119, 0, 60, 1) { Vx = -100 };
        var j = ArenaPhysics.Collide(ref a, ref b, IcefloeCore.E);
        Assert.Equal(185, j, 9);
        Assert.Equal(-85, a.Vx, 9);
        Assert.Equal(85, b.Vx, 9);
        Assert.Equal(0, a.Vy);
    }

    [Fact] // 12
    public void Overlapping_bodies_are_pushed_apart_within_one_substep()
    {
        var a = new ArenaBody(0, 0, 60, 1);
        var b = new ArenaBody(80, 0, 60, 1);
        Assert.Equal(0, ArenaPhysics.Collide(ref a, ref b, IcefloeCore.E));   // стоять — імпульсу нема
        Assert.Equal(120, b.X - a.X, 9);
        Assert.Equal(-20, a.X, 9);                                            // навпіл при рівних масах

        var heavy = new ArenaBody(0, 0, 60, 1 / 1.8);
        var light = new ArenaBody(60, 0, 60, 1);
        ArenaPhysics.Collide(ref heavy, ref light, IcefloeCore.E);
        Assert.Equal(120, light.X - heavy.X, 9);
        Assert.True(light.X - 60 > -heavy.X);                                // легший відлітає далі
    }

    [Fact] // 13
    public void Dash_adds_impulse_along_face_and_starts_cooldown()
    {
        var h = Table(2);
        ToGo(h);
        var b = Core(h).Bodies[0];
        Assert.Equal(4, b.Face);                                              // угорі, дивиться вниз
        Assert.True(h.Act(0, "dash").Ok);
        Assert.Equal(IcefloeCore.DashImpulse, b.B.Vy, 9);
        Assert.Equal(0, b.B.Vx, 9);
        Assert.Equal(IcefloeCore.DashCd, b.Cd);
        Assert.Contains(Events(Core(h)), e => e[0] == IcefloeCore.EvDash && e[1] == 0);
        h.Tick(24);
        var second = h.Act(0, "dash");
        Assert.False(second.Ok);
        Assert.Equal("Ще не готово", second.Message);
        h.Tick();
        Assert.True(h.Act(0, "dash").Ok);
    }

    [Fact] // 14
    public void A_dashing_body_hits_twice_as_hard_for_six_ticks()
    {
        double Knock(bool dash)
        {
            var c = Bare(2, radius: 1188);
            var a = Put(c, 0, C - 200, C);
            var b = Put(c, 1, C - 75, C);                // 125 см між центрами: зачепить уже в першому підкроці
            a.Face = 0;
            if (dash) Assert.Null(c.Dash(0));
            else a.B.Vx = IcefloeCore.DashImpulse;
            c.Step(true);
            return b.B.Vx;
        }

        var hard = Knock(true);
        var soft = Knock(false);
        Assert.InRange(hard / soft, 1.25, 1.4);   // (1+e)·v·(2/3) проти (1+e)·v·(1/2): 4/3

        var k = Bare(1, radius: 1188);
        Put(k, 0, C, C).Face = 0;
        Assert.Null(k.Dash(0));
        for (var t = 1; t <= IcefloeCore.HitTicks; t++)
        {
            Assert.True(k.Bodies[0].Hit > 0, $"тик {t}");
            k.Step(true);
        }
        Assert.Equal(0, k.Bodies[0].Hit);
    }

    [Fact] // 15
    public void Fast_bodies_do_not_tunnel_through_each_other()
    {
        for (var off = 0.0; off < 44; off += 2.75)
        {
            var c = Bare(2, radius: 1188);
            var a = Put(c, 0, C - 400 - off, C, IcefloeCore.VMax, 0);
            var b = Put(c, 1, C + 400, C, -IcefloeCore.VMax, 0);
            var bumped = false;
            for (var t = 0; t < 30; t++)
            {
                c.Step(true);
                Assert.True(b.B.X - a.B.X >= 2 * IcefloeCore.BodyR - 1e-6, $"зсув {off}: тіла пройшли одне крізь одне");
                bumped |= a.B.Vx < 0;
            }
            Assert.True(bumped, $"зсув {off}: зіткнення не було");
        }
    }

    [Fact] // 16
    public void Heavy_jug_body_barely_moves_when_pushed()
    {
        double Push(bool jug)
        {
            var c = Bare(2, radius: 1188);
            var a = Put(c, 0, C, C);
            a.Jug = jug ? 100 : 0;
            Put(c, 1, C - 125, C, 500, 0);
            c.Step(true);
            return a.B.Vx;
        }

        var normal = Push(false);
        var heavy = Push(true);
        Assert.True(heavy < normal * 0.75, $"глек {heavy} проти {normal}");
        Assert.True(heavy > 0);
    }

    // ---------- падіння й берег ----------

    [Fact] // 17
    public void Body_whose_center_crosses_the_edge_falls_and_is_recorded_in_order()
    {
        var c = Bare(3, radius: 800);
        Put(c, 0, C + 790, C, 600, 0);
        Put(c, 1, C, C - 780, 0, -300);                  // до краю 20 см: за тик проїде ~12 — ще живий
        Put(c, 2, C, C);
        c.Step(true);
        Assert.False(c.Bodies[0].Alive);
        Assert.True(c.Bodies[1].Alive);
        for (var t = 0; t < 10 && c.Bodies[1].Alive; t++) c.Step(true);
        Assert.False(c.Bodies[1].Alive);
        Assert.Equal([0, 1], c.Out);
        Assert.True(c.Bodies[2].Alive);
        Assert.Equal(1, c.AliveCount);
    }

    [Fact] // 18
    public void Body_hanging_half_over_the_edge_is_still_alive()
    {
        var c = Bare(1, radius: 800);
        var b = Put(c, 0, C + 799, C);   // пів тіла над водою, центр — ще на кризі
        c.Step(true);
        Assert.True(b.Alive);
        Assert.Empty(c.Out);
    }

    [Fact] // 19
    public void Push_within_two_seconds_credits_the_pusher()
    {
        var c = Bare(2, radius: 800);
        var victim = Put(c, 0, C + 500, C);
        Put(c, 1, C + 370, C, 900, 0);
        for (var t = 0; t < 60 && victim.Alive; t++) c.Step(true);
        Assert.False(victim.Alive);
        Assert.Equal(1, c.Bodies[1].Pushouts);
        Assert.Equal([(0, 1)], c.ByList);
    }

    [Fact] // 20
    public void Fall_without_recent_contact_credits_nobody()
    {
        var c = Bare(2, radius: 800);
        var a = Put(c, 0, C + 500, C);
        Put(c, 1, C + 370, C, 900, 0);
        c.Step(true);
        Assert.Equal(1, a.LastBy);
        // Зачепили давно (понад 2 с тому) — і зсковзнув сам.
        a.LastAt = c.T - IcefloeCore.CreditTicks - 1;
        a.B.X = C + 795;
        a.B.Vx = 300;
        c.Bodies[1].B.X = C - 300;
        c.Bodies[1].B.Vx = 0;
        c.Step(true);
        Assert.False(a.Alive);
        Assert.Equal(0, c.Bodies[1].Pushouts);
        Assert.Contains(Events(c), e => e[0] == IcefloeCore.EvSplash && e[1] == 0 && e[2] == -1);
    }

    [Fact] // 21
    public void Fallen_player_stands_on_the_bank_at_the_fall_angle_with_three_snowballs()
    {
        var h = Table(3);
        ToGo(h);
        var b = Core(h).Bodies[1];
        var angle = Math.Atan2(b.B.Y - C, b.B.X - C);
        b.B.X = C + 1150 * Math.Cos(angle);
        b.B.Y = C + 1150 * Math.Sin(angle);
        h.Tick();
        Assert.False(b.Alive);
        Assert.Equal(IcefloeCore.BankAmmoMax, b.BankAmmo);
        Assert.Equal(angle, b.BankAngle, 6);
        var p = Frame(h).GetProperty("p")[1];
        Assert.Equal(16, p[5].GetInt32() & 16);
        Assert.Equal(0, p[5].GetInt32() & 1);
        var bank = Core(h).Bank;
        Assert.Equal(IcefloeCore.ShoreOf(800) + IcefloeCore.BankGap, bank);   // трійко на кризі 800 — берег близько
        Assert.Equal(Math.Round(C + bank * Math.Cos(angle)), p[0].GetInt32());
        Assert.Equal(Math.Round(C + bank * Math.Sin(angle)), p[1].GetInt32());
        Assert.Equal(3, p[7].GetInt32());
        Assert.Equal([1], h.View(null).GetProperty("out").EnumerateArray().Select(x => x.GetInt32()));
    }

    [Fact] // 22
    public void Bank_throw_flies_straight_and_shoves_the_target_by_three_hundred()
    {
        var c = Bare(2, radius: 1188);
        var thrower = c.Bodies[0];
        thrower.Plays = true;
        thrower.Alive = false;
        thrower.BankAngle = Math.PI;                 // лівий берег
        thrower.BankAmmo = 3;
        thrower.Face = 0;                            // цілить праворуч
        var target = Put(c, 1, C, C);
        Assert.Null(c.Throw(0));
        var ball = c.Balls.Single(x => x.On);
        Assert.Equal(C - c.Bank, ball.X, 6);
        Assert.Equal(C, ball.Y, 6);
        var hit = false;
        for (var t = 0; t < 60 && !hit; t++)
        {
            c.Step(true);
            hit = Events(c).Any(e => e[0] == IcefloeCore.EvBall && e[1] == 1 && e[2] == 0);
            if (!hit) Assert.Equal(0, target.B.Vx);
        }
        Assert.True(hit);
        Assert.InRange(target.B.Vx, 300 * 0.96 - 1e-6, 300);
        Assert.Equal(0, target.B.Vy, 9);
        Assert.Equal(0, target.LastBy);
        Assert.DoesNotContain(c.Balls, x => x.On);
    }

    [Fact]
    public void Bank_snowball_that_knocks_someone_in_credits_the_thrower()
    {
        var c = Bare(2, radius: 800);
        var thrower = c.Bodies[0];
        thrower.Plays = true;
        thrower.Alive = false;
        thrower.BankAngle = Math.PI;
        thrower.BankAmmo = 3;
        thrower.Face = 0;
        var target = Put(c, 1, C + 790, C);           // на самому краї
        Assert.Null(c.Throw(0));
        for (var t = 0; t < 80 && target.Alive; t++) c.Step(true);
        Assert.False(target.Alive);
        Assert.Equal(1, thrower.Pushouts);
    }

    [Fact] // 23
    public void Bank_throws_are_limited_to_three_with_two_second_cooldown()
    {
        var h = Table(2);
        ToGo(h);
        Sink(h, 1);
        h.Tick();
        Assert.Equal(Icefloe.PhEnd, Game(h).Phase);   // на двох це вже кінець раунду — кидати не можна
        Assert.Equal("Зачекай, зараз почнемо", h.Act(1, "throw").Message);

        var c = Bare(3, radius: 1188);
        var b = c.Bodies[2];
        b.Alive = false;
        b.BankAngle = 0;
        b.BankAmmo = 3;
        b.Face = 8;
        Assert.Null(c.Throw(2));
        Assert.Equal("Не так швидко", c.Throw(2));
        for (var k = 0; k < 2; k++)
        {
            for (var t = 0; t < IcefloeCore.BankThrowCd - 1; t++) c.Step(true);
            Assert.Equal("Не так швидко", c.Throw(2));
            c.Step(true);
            Assert.Null(c.Throw(2));
        }
        for (var t = 0; t < IcefloeCore.BankThrowCd; t++) c.Step(true);
        Assert.Equal("Сніжок нема", c.Throw(2));
    }

    [Fact] // 24
    public void Snowball_never_hits_its_owner_and_dies_at_the_pond_edge_or_after_three_seconds()
    {
        var c = Bare(1, radius: 1188);
        var b = Put(c, 0, C, C);
        b.Ammo = 2;
        b.Face = 0;
        Assert.Null(c.Throw(0));                         // вилітає на 70 см — усередині власного тіла + радіус сніжки
        c.Step(true);
        Assert.Equal(0, b.B.Vx);
        Assert.Contains(c.Balls, x => x.On);
        for (var t = 0; t < 45; t++) c.Step(true);     // 1300 − 70 см до краю ставка — за 1.54 с
        Assert.DoesNotContain(c.Balls, x => x.On);
        Assert.Equal(1, b.Ammo);

        // Уздовж діагоналі ставок довгий, але сніжка живе лише три секунди.
        var k = Bare(1, radius: 1188);
        k.Balls[3] = new IcefloeBall { On = true, Id = 1, Owner = 0, Ttl = IcefloeCore.BallTtl, X = 1, Y = 1, Vx = 800 * ArenaPhysics.D, Vy = 800 * ArenaPhysics.D };
        for (var t = 0; t < IcefloeCore.BallTtl - 1; t++) k.Step(true);
        Assert.True(k.Balls[3].On);
        k.Step(true);
        Assert.False(k.Balls[3].On);
    }

    [Fact] // 25
    public void More_than_sixteen_snowballs_in_flight_is_refused()
    {
        var c = Bare(1, radius: 1188);
        var b = Put(c, 0, C, C);
        b.Ammo = 2;
        for (var i = 0; i < IcefloeCore.BallSlots; i++)
            c.Balls[i] = new IcefloeBall { On = true, Id = i + 1, Owner = 5, Ttl = 50, X = 10, Y = 10 + i };
        Assert.Equal("Забагато сніжок у повітрі", c.Throw(0));
        Assert.Equal(2, b.Ammo);
        c.Balls[7].On = false;
        Assert.Null(c.Throw(0));
    }

    // ---------- крига ----------

    [Fact] // 26
    public void Crack_warning_appears_fifty_ticks_before_the_break_and_names_the_arc()
    {
        var h = Table(2);
        ToGo(h);
        var c = Core(h);
        var iv = c.Iv;
        while (c.Rt < IcefloeCore.FirstWarn - 1) h.Tick();
        Assert.Equal(JsonValueKind.Null, Frame(h).GetProperty("crack").ValueKind);
        h.Tick();
        var crack = Frame(h).GetProperty("crack");
        Assert.InRange(crack[0].GetInt32(), 0, 23);
        Assert.InRange(crack[1].GetInt32(), 5, 7);
        h.Tick(IcefloeCore.WarnTicks - 1);
        Assert.Equal(JsonValueKind.Array, Frame(h).GetProperty("crack").ValueKind);
        Assert.Equal(iv, c.Iv);
        h.Tick();
        Assert.Equal(JsonValueKind.Null, Frame(h).GetProperty("crack").ValueKind);
        Assert.Equal(iv + 1, c.Iv);
        Assert.Equal(iv + 1, h.View(null).GetProperty("ice").GetProperty("iv").GetInt32());
    }

    [Fact] // 27
    public void Break_shrinks_the_arc_by_22_percent_and_everything_by_10_and_bumps_iv()
    {
        var c = Bare();
        Flat(c, 1000);
        var iv = c.Iv;
        c.Break(22, 5);                                   // дуга через нуль: 22, 23, 0, 1, 2
        foreach (var i in new[] { 22, 23, 0, 1, 2 }) Assert.Equal(1000 * 0.78 * 0.9, c.R[i], 9);
        foreach (var i in new[] { 3, 10, 21 }) Assert.Equal(900, c.R[i], 9);
        Assert.Equal(iv + 1, c.Iv);
        Assert.True(c.Broke);
        Assert.Contains(Events(c), e => e is [IcefloeCore.EvBreak, 22, 5]);

        // у кімнаті відкол розсилає повний вид одразу (TickResult.Both)
        var h = Table(2);
        ToGo(h);
        while (Core(h).Rt < IcefloeCore.FirstWarn + IcefloeCore.WarnTicks - 1) h.Tick();
        var before = h.Outbox.Count;
        h.Tick();
        Assert.Contains(h.Outbox.Skip(before), m => m is RoomViews);
    }

    [Fact] // 28
    public void Melting_starts_at_tick_1300_and_grows_one_centimeter_per_tick()
    {
        var c = new IcefloeCore(new Random(3));
        c.ResetParty(new bool[IcefloeCore.Seats]);
        c.NewRound(1100);
        c.Rt = IcefloeCore.MeltFrom - 2;
        c.Step(true);
        Assert.Equal(0, c.Melt);
        c.Step(true);
        Assert.Equal(1, c.Melt);
        c.Step(true);
        Assert.Equal(2, c.Melt);
        for (var t = 0; t < 25; t++) c.Step(true);
        Assert.Equal(27, c.Melt);                         // 25 см/с
    }

    [Fact] // 29
    public void Pickup_left_beyond_the_edge_sinks()
    {
        var c = Bare(1, radius: 700);
        Put(c, 0, C, C);
        c.Rt = 10;
        c.Pickups[0] = new IcefloePickup { On = true, Kind = 1, Ttl = 200, X = C + 690, Y = C };
        c.Pickups[1] = new IcefloePickup { On = true, Kind = 0, Ttl = 200, X = C - 300, Y = C };
        c.Step(true);
        Assert.True(c.Pickups[0].On);
        c.Melt = 20;                                      // крига підтанула — підбирачка вже над водою
        c.Step(true);
        Assert.False(c.Pickups[0].On);
        Assert.True(c.Pickups[1].On);
    }

    [Fact] // 30
    public void Round_hits_the_cap_at_1875_as_a_draw_when_two_survive()
    {
        var h = Table(2);
        ToGo(h);
        var c = Core(h);
        c.Rt = IcefloeCore.CapTicks - 2;
        h.Tick();
        Assert.Equal(Icefloe.PhGo, Game(h).Phase);
        h.Tick();
        Assert.Equal(Icefloe.PhEnd, Game(h).Phase);
        var v = h.View(null);
        Assert.Equal(-1, v.GetProperty("lastRound").GetProperty("winner").GetInt32());
        Assert.All(v.GetProperty("wins").EnumerateArray().Take(2), w => Assert.Equal(0, w.GetInt32()));
        Assert.Contains(Events(c), e => e is [IcefloeCore.EvRound, -1]);
    }

    // ---------- підбирачки ----------

    [Fact] // 31
    public void Pickups_spawn_from_tick_125_every_150_ticks_up_to_two_on_the_ice()
    {
        var c = new IcefloeCore(new Random(9));
        c.ResetParty(new bool[IcefloeCore.Seats]);
        c.NewRound(1100);
        int Count() => c.Pickups.Count(p => p.On);
        while (c.Rt < IcefloeCore.PickupFrom - 1) c.Step(true);
        Assert.Equal(0, Count());
        c.Step(true);
        Assert.Equal(1, Count());
        while (c.Rt < IcefloeCore.PickupFrom + IcefloeCore.PickupEvery - 1) c.Step(true);
        Assert.Equal(1, Count());
        c.Step(true);
        Assert.Equal(2, Count());
        while (c.Rt < IcefloeCore.PickupFrom + 2 * IcefloeCore.PickupEvery) c.Step(true);
        Assert.Equal(2, Count());                         // місця нема — третя не з'являється
        foreach (var p in c.Pickups) Assert.True(c.OnIce(p.X, p.Y));
        Assert.All(c.Pickups, p => Assert.InRange(p.Kind, 0, 2));
    }

    [Fact] // 32
    public void Spikes_jug_and_snowball_pickups_apply_their_effects_for_200_ticks()
    {
        var c = Bare(1, radius: 1188);
        var b = Put(c, 0, C, C);
        c.Rt = 10;
        c.Pickups[0] = new IcefloePickup { On = true, Kind = IcefloeCore.KindSpikes, Ttl = 300, X = C + 50, Y = C };
        c.Pickups[1] = new IcefloePickup { On = true, Kind = IcefloeCore.KindJug, Ttl = 300, X = C - 50, Y = C };
        c.Step(true);
        Assert.Equal(IcefloeCore.EffectTicks, b.Spikes);
        Assert.Equal(IcefloeCore.EffectTicks, b.Jug);
        Assert.Equal(IcefloeCore.SpikeMu, b.Mu);
        Assert.Equal(IcefloeCore.JugMass, b.Mass);
        Assert.Equal(IcefloeCore.JugThrust, b.Thrust);
        Assert.Contains(Events(c), e => e is [IcefloeCore.EvPickup, 0, IcefloeCore.KindSpikes]);
        for (var t = 0; t < IcefloeCore.EffectTicks - 1; t++) c.Step(true);
        Assert.Equal(1, b.Spikes);
        c.Step(true);
        Assert.Equal(0, b.Spikes);
        Assert.Equal(IcefloeCore.Mu, b.Mu);

        for (var k = 0; k < 3; k++)
        {
            c.Pickups[0] = new IcefloePickup { On = true, Kind = IcefloeCore.KindBall, Ttl = 300, X = b.B.X, Y = b.B.Y };
            c.Step(true);
        }
        Assert.Equal(IcefloeCore.IceAmmoMax, b.Ammo);     // більше двох сніжок у кишеню не лізе
    }

    // ---------- партія і кімната ----------

    [Fact] // 33
    public void Round_ends_when_one_body_is_left_and_awards_the_win()
    {
        var h = Table(2);
        ToGo(h);
        var iv = Core(h).Iv;
        Sink(h, 1);
        h.Tick();
        Assert.Equal(Icefloe.PhEnd, Game(h).Phase);
        Assert.Equal(1, Core(h).Bodies[0].Wins);
        var v = h.View(null);
        Assert.Equal("end", v.GetProperty("phase").GetString());
        Assert.Equal(0, v.GetProperty("lastRound").GetProperty("winner").GetInt32());
        Assert.Equal(1, v.GetProperty("wins")[0].GetInt32());
        h.Tick(Icefloe.EndTicks - 1);
        Assert.Equal(Icefloe.PhEnd, Game(h).Phase);
        h.Tick();
        Assert.Equal(Icefloe.PhReady, Game(h).Phase);
        Assert.Equal(2, Game(h).RoundNo);
        Assert.NotEqual(iv, Core(h).Iv);
        Assert.True(Core(h).Bodies[1].Alive);             // усі знову на кризі
        h.Tick(Icefloe.ReadyNext);
        Assert.Equal(Icefloe.PhGo, Game(h).Phase);
    }

    [Fact] // 34
    public void Party_ends_at_the_configured_wins_and_writes_the_journal_line()
    {
        var h = Table(2);
        Round(h, 0);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Round(h, 0);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("Крижина: Оля 2 : Петро 0", h.Outbox.OfType<Journal>().Last().Text);
        var fin = h.Finished.Single();
        Assert.Equal([0], fin.Result.Winners);
        Assert.Equal(0L, fin.Result.Scores![0]);
        Assert.Equal(0L, fin.Result.Scores![1]);
        var v = h.View(null);
        Assert.Equal("over", v.GetProperty("phase").GetString());
        Assert.Equal(0, v.GetProperty("winner").GetInt32());
    }

    [Fact] // 35
    public void Option_wins_1_makes_a_single_round_party()
    {
        var h = Table(3, wins: "1");
        Round(h, 2);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([2], h.Finished.Single().Result.Winners);

        Assert.Equal(3, Game(Table(2, wins: "3")).Need);
        Assert.Equal(2, Game(Table(2, wins: "99")).Need);   // чуже значення каркас зводить до типового
    }

    [Fact] // 36
    public void Twelve_rounds_cap_gives_the_party_to_the_leader_or_a_draw()
    {
        var h = Table(3, wins: "3");
        foreach (var w in new[] { 0, 1, 2, 0, 1 }) Round(h, w);
        for (var r = 0; r < 6; r++) Round(h, -1);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(12, Game(h).RoundNo);
        Round(h, -1);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0, 1], h.Finished.Single().Result.Winners);   // по двоє — спільна перемога лідерів
        Assert.StartsWith("Крижина: Оля 2 : Петро 2 : Ганна 1", h.Outbox.OfType<Journal>().Last().Text);

        var d = Table(2, wins: "3");
        for (var r = 0; r < Icefloe.RoundsMax; r++) Round(d, -1);
        Assert.Equal(RoomStatus.Finished, d.Room.Status);
        Assert.True(d.Finished.Single().Result.Draw);
        Assert.EndsWith("— нічия", d.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact] // 37
    public void Ready_phase_stores_intents_but_nothing_moves()
    {
        var h = Table(2);
        Assert.Equal(Icefloe.PhReady, Game(h).Phase);
        var b = Core(h).Bodies[0];
        var (x, y) = (b.B.X, b.B.Y);
        h.Input(0, "move", new { a = 0 });
        Assert.Equal(0, b.Want);
        Assert.Equal(0, b.Face);
        h.Tick(10);
        Assert.Equal((x, y), (b.B.X, b.B.Y));
        ToGo(h);
        Assert.Equal((x, y), (b.B.X, b.B.Y));
        h.Tick();
        Assert.True(b.B.X > x);
        Assert.Equal(y, b.B.Y, 9);
    }

    [Fact] // 38
    public void Dash_and_throw_are_refused_outside_go_with_the_right_texts()
    {
        var lobby = new RoomHarness("icefloe", new { wins = "2" });
        lobby.Join("Оля");
        lobby.Join("Петро");
        Assert.Equal("Чекаємо на гравців", lobby.Act(0, "dash").Message);

        var h = Table(3);
        Assert.Equal("Зачекай, зараз почнемо", h.Act(0, "dash").Message);
        Assert.Equal("Зачекай, зараз почнемо", h.Act(0, "throw").Message);
        Assert.Equal("Такого напрямку нема", h.Act(0, "move", new { a = 16 }).Message);
        Assert.Equal("Тут так не ходять", h.Act(0, "jump").Message);
        ToGo(h);
        Assert.Equal("Сніжок нема", h.Act(0, "throw").Message);
        Sink(h, 2);
        h.Tick();
        Assert.Equal("Ти у воді — кидай сніжки", h.Act(2, "dash").Message);
        Assert.True(h.Act(2, "throw").Ok);
        Assert.Equal("Не так швидко", h.Act(2, "throw").Message);

        var over = Table(2, wins: "1");
        Round(over, 0);
        Assert.Equal("Партію зіграно, тисни «Ще раз»", over.Act(0, "dash").Message);
    }

    [Fact] // 39
    public void Leaving_with_three_at_the_table_keeps_the_party_going()
    {
        var h = Table(3);
        ToGo(h);
        h.Leave("Ганна");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.False(Core(h).Bodies[2].Plays);
        Assert.Contains(h.Outbox.OfType<Journal>(), j => j.Text == "Крижина: Ганна встав з-за столу — решта грає далі");
        Assert.Equal(JsonValueKind.Null, Frame(h).GetProperty("p")[2].ValueKind);
        h.Tick(5);
        Assert.Equal(Icefloe.PhGo, Game(h).Phase);
    }

    [Fact] // 40
    public void Leaving_with_two_at_the_table_hands_the_party_to_the_other()
    {
        var h = Table(2);
        ToGo(h);
        h.Leave("Оля");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Finished.Single().Result.Winners);
        Assert.Equal("Крижина: Оля встав з-за столу, партію не дограли", h.Room.Result!.Text);
        Assert.Equal("over", h.View(null).GetProperty("phase").GetString());
    }

    [Fact] // 41
    public void Leaving_mid_round_as_one_of_two_survivors_ends_the_round_for_the_other()
    {
        var h = Table(3);
        ToGo(h);
        Sink(h, 2);
        h.Tick();
        Assert.Equal(Icefloe.PhGo, Game(h).Phase);
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        h.Tick();
        Assert.Equal(Icefloe.PhEnd, Game(h).Phase);
        Assert.Equal(1, Core(h).Bodies[0].Wins);
        Assert.Equal(0, Core(h).Bodies[2].Wins);
    }

    [Fact] // 42
    public void Rematch_rotates_seats_and_starts_a_clean_party_keeping_the_series()
    {
        var h = Table(2, wins: "1");
        Round(h, 0);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(h.Rematch().Ok);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("Петро", h.NickOf(0));
        Assert.Equal("Оля", h.NickOf(1));
        var v = h.View(null);
        Assert.Equal("ready", v.GetProperty("phase").GetString());
        Assert.Equal(1, v.GetProperty("round").GetInt32());
        Assert.All(v.GetProperty("wins").EnumerateArray().Take(2), w => Assert.Equal(0, w.GetInt32()));
        var series = v.GetProperty("series");
        Assert.Equal(1, series.GetProperty("games").GetInt32());
        Assert.Equal(1, series.GetProperty("wins")[1].GetInt32());   // Оля тепер на місці 1
        Assert.Equal(0, series.GetProperty("wins")[0].GetInt32());
    }

    [Fact] // 43
    public void Views_are_identical_for_every_seat_and_the_spectator()
    {
        var h = Table(4);
        ToGo(h);
        h.Input(1, "move", new { a = 3 });
        h.Tick(30);
        var spectator = h.View(null).GetRawText();
        for (var s = 0; s < 4; s++) Assert.Equal(spectator, h.View(s).GetRawText());
        // Ховати нема чого: у виді й кадрі — лише числа світу, без ніків і без чиїхось «секретів».
        Assert.DoesNotContain("Оля", spectator);
        foreach (var name in new[] { "phase", "round", "need", "wins", "pushouts", "ice", "pond", "bank", "out", "series", "frame" })
            Assert.True(Views.Has(h.View(null), name), name);
    }

    [Fact] // 44
    public void Frame_of_eight_players_stays_under_a_kilobyte()
    {
        var h = Table(8);
        ToGo(h);
        var c = Core(h);
        for (var s = 0; s < 8; s++) h.Input(s, "move", new { a = (s * 5) % 16 });
        h.Tick(40);
        // Найгірше: троє у воді, 16 сніжок, дві підбирачки, стос подій.
        var sizes = new List<int>();
        for (var s = 5; s < 8; s++) Sink(h, s);
        h.Tick();
        for (var i = 0; i < IcefloeCore.BallSlots; i++)
            c.Balls[i] = new IcefloeBall { On = true, Id = 1000 + i, Owner = 5, Ttl = 50, X = 1234.5, Y = 876.25, Vx = -565.7, Vy = 565.7 };
        c.Pickups[0] = new IcefloePickup { On = true, Kind = 2, Ttl = 100, X = 1200, Y = 1300 };
        c.Pickups[1] = new IcefloePickup { On = true, Kind = 1, Ttl = 100, X = 1400, Y = 1100 };
        for (var k = 0; k < 6; k++) c.Event(IcefloeCore.EvBump, 0, 1, 1290, 1210, 380);
        var worst = Views.Text(h.Room.Game.Frame()).Length;

        var typical = Table(8);
        ToGo(typical);
        for (var s = 0; s < 8; s++) typical.Input(s, "move", new { a = (s * 3) % 16 });
        for (var t = 0; t < 150; t++)
        {
            typical.Tick();
            sizes.Add(Views.Text(typical.Room.Game.Frame()).Length);
        }
        output.WriteLine($"кадр на вісьмох: типовий {sizes.Average():F0} Б (макс {sizes.Max()}), найгірший {worst} Б");
        Assert.True(sizes.Max() < 1024, $"типовий кадр {sizes.Max()} Б");
        Assert.True(worst < 1536, $"найгірший кадр {worst} Б");
    }

    [Fact] // 45
    public void Frames_fly_every_fifth_tick_on_the_countdown_and_every_tick_while_the_ice_moves()
    {
        var h = Table(2);
        int Frames(int ticks)
        {
            var before = h.Outbox.OfType<RoomFrame>().Count();
            h.Tick(ticks);
            return h.Outbox.OfType<RoomFrame>().Count() - before;
        }

        Assert.InRange(Frames(50), 10, 11);                      // відлік: кожен 5-й тик
        ToGo(h);
        Assert.Equal(25, Frames(25));                            // гра: щотика
        h.Input(0, "move", new { a = 4 });
        h.Tick(20);
        Sink(h, 1);
        h.Tick();
        Assert.Equal(Icefloe.PhEnd, Game(h).Phase);
        Assert.Equal(10, Frames(10));                            // кінець раунду: Оля ще їде — щотика
        h.Input(0, "move", new { a = -1 });
        h.Tick(55);                                             // доковзала й стоїть
        Assert.InRange(Frames(5), 1, 2);
    }

    [Fact] // 46
    public void Same_seed_and_input_log_give_the_same_view()
    {
        static string Play(int seed)
        {
            var h = Table(4, seed);
            for (var t = 0; t < 600; t++)
            {
                if (t % 17 == 0) h.Input(t % 4, "move", new { a = (t / 17) % 16 });
                if (t % 29 == 0) h.Input((t + 1) % 4, "dash");
                if (t % 41 == 0) h.Input((t + 2) % 4, "throw");
                if (t % 53 == 0) h.Input((t + 3) % 4, "move", new { a = -1 });
                h.Tick();
            }
            return h.View(null).GetRawText();
        }

        Assert.Equal(Play(5), Play(5));
        Assert.NotEqual(Play(5), Play(6));
    }

    [Fact] // 47
    public void Move_accepts_exactly_what_the_module_sends()
    {
        var h = Table(2);
        var b = Core(h).Bodies[0];
        Assert.True(h.Act(0, "move", new { a = 5 }).Ok);        // так шле icefloe.js
        Assert.Equal(5, b.Want);
        Assert.True(h.Act(0, "move", 9).Ok);                    // голе число
        Assert.Equal(9, b.Want);
        Assert.True(h.Act(0, "move", new { a = -1 }).Ok);
        Assert.Equal(-1, b.Want);
        Assert.Equal(9, b.Face);
        Assert.Equal("Такого напрямку нема", h.Act(0, "move", new { dir = 1 }).Message);
        Assert.Equal("Такого напрямку нема", h.Act(0, "move", "5").Message);
        Assert.Equal("Такого напрямку нема", h.Act(0, "move", new { a = 2.5 }).Message);
        Assert.Equal("Такого напрямку нема", h.Act(0, "move", new { a = -2 }).Message);
        Assert.Equal(-1, b.Want);
        ToGo(h);
        Assert.True(h.Act(0, "dash", new { }).Ok);              // ривок і сніжка — порожній payload
        Assert.True(h.Act(1, "dash", null).Ok);
    }

    [Fact] // 48
    public void Achievements_dry_and_push5_are_requested_only_when_earned()
    {
        var h = Table(2);
        Round(h, 0);
        Round(h, 1);
        Round(h, 0);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:icefloe-dry");   // Оля раз падала
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:icefloe-push5");

        var dry = Table(3, wins: "1");
        Core(dry).Bodies[0].Pushouts = 4;
        ToGo(dry);
        // Оля штовхає Ганну з краю — п'ятий випхнутий за партію
        var c = Core(dry);
        c.Bodies[2].B.X = C + 780;
        c.Bodies[2].B.Y = C;
        c.Bodies[0].B.X = C + 650;
        c.Bodies[0].B.Y = C;
        c.Bodies[0].B.Vx = 900;
        for (var t = 0; t < 20 && c.Bodies[2].Alive; t++) dry.Tick();
        Assert.Equal(5, c.Bodies[0].Pushouts);
        Round(dry, 0);
        Assert.Equal(RoomStatus.Finished, dry.Room.Status);
        Assert.Contains(dry.Awards, a => a.Reason == "ach:icefloe-dry" && a.Nick == "Оля");
        Assert.Contains(dry.Awards, a => a.Reason == "ach:icefloe-push5" && a.Nick == "Оля");
        Assert.DoesNotContain(dry.Awards, a => a.Nick != "Оля");
        Assert.Equal(5L, dry.Finished.Single().Result.Scores![0]);
    }

    [Fact] // 49
    [Trait("Category", "Perf")]
    public void Three_thousand_ticks_with_eight_bodies_stay_under_a_second()
    {
        var h = Table(8, wins: "3");
        ToGo(h);
        // прогрів JIT — щоб міряти гру, а не компілятор
        h.Tick(50);
        var sw = Stopwatch.StartNew();
        var ticks = 0;
        var parties = 1;
        for (var t = 0; t < 3000; t++)
        {
            if (h.Room.Status != RoomStatus.Playing)
            {
                // партію дограли (на вісьмох з ривками раунди короткі) — «Ще раз» поза заміром і далі
                sw.Stop();
                Assert.True(h.Rematch().Ok);
                parties++;
                sw.Start();
            }
            for (var s = 0; s < 8; s++)
            {
                if ((t + s * 3) % 13 == 0) h.Input(s, "move", new { a = (t / 13 + s * 5) % 16 });
                if ((t + s) % 25 == 0) h.Input(s, "dash");
                if ((t + s * 7) % 50 == 0) h.Input(s, "throw");
            }
            h.Tick();
            ticks++;
        }
        sw.Stop();
        var per = sw.Elapsed.TotalMilliseconds / ticks;
        output.WriteLine($"Крижина на вісьмох: {ticks} тиків кімнати ({parties} партій) за {sw.Elapsed.TotalMilliseconds:F0} мс — {per:F4} мс на тик (з кадром)");
        Assert.Equal(3000, ticks);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"{ticks} тиків зайняли {sw.Elapsed}");
        Assert.True(per <= 0.25, $"середній тик {per} мс");

        // Голе ядро без кімнати: сама фізика
        var core = Bare(8, radius: 1100);
        core.NewRound(1100);
        var st = Stopwatch.StartNew();
        for (var t = 0; t < 3000; t++)
        {
            for (var s = 0; s < 8; s++)
            {
                core.Move(s, (t / 9 + s * 5) % 16);
                if (!core.Bodies[s].Alive) { core.Bodies[s].Alive = true; core.Bodies[s].B.X = C; core.Bodies[s].B.Y = C + s * 30; }
            }
            if (core.Rt > 1200) core.NewRound(1100);
            core.Step(true);
        }
        st.Stop();
        output.WriteLine($"ядро: {st.Elapsed.TotalMilliseconds / 3000:F4} мс на тик");
    }

    [Fact] // 50: передбачення свого тіла в браузері = сервер
    public void Prediction_matches_the_browser_fixture()
    {
        var fx = ArenaPredictFixture.Load();
        var logs = fx["icefloe"]!.AsArray();
        Assert.True(logs.Count >= 5);
        var i = 0;
        foreach (var log in logs) ArenaPredictFixture.AssertSame(ArenaPredictFixture.RunIcefloe(log!), log!["expect"]!, $"журнал {i++}");
    }

    [Fact] // 51
    public void A_finished_table_reopened_by_a_newcomer_shows_fresh_ice()
    {
        var h = Table(2, wins: "1");
        Round(h, 0);
        h.Leave("Петро");
        h.Join("Ганна");
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);
        var v = h.View(null);
        Assert.Equal("lobby", v.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("winner").ValueKind);
        Assert.All(v.GetProperty("ice").GetProperty("v").EnumerateArray(), x => Assert.Equal(800, x.GetInt32()));
    }

    [Fact] // 52
    public void Seat_names_are_colors_and_the_catalog_passport_is_right()
    {
        var h = Table(8);
        var info = h.Room.Info;
        Assert.Equal(("icefloe", GameGroup.Live, 2, 8, 40, StartMode.ByHost, false), (info.Id, info.Group, info.MinPlayers, info.MaxPlayers, info.TickMs, info.Start, info.Rated));
        Assert.Equal(["синій", "рудий", "зелений", "жовтий", "бузковий", "м’ятний", "рожевий", "сірий"],
            Enumerable.Range(0, 8).Select(h.Room.Game.SeatName));
        var one = new RoomHarness("icefloe");
        one.Join("Оля");
        Assert.False(one.Start().Ok);                              // сам на сам — не починаємо
    }

    // ---------- після рецензій ----------

    [Fact] // 54 (рецензія коду): зв'язок пропав із затиснутим напрямком — тяга гасне, тіло не їде саме у воду
    public void A_held_intent_without_keepalive_fades_and_the_body_only_coasts()
    {
        var h = Table(2);
        ToGo(h);
        var b = Core(h).Bodies[0];                                // угорі криги, тягне вниз — через центр
        h.Input(0, "move", new { a = 4 });
        h.Tick(Icefloe.KeepTicks - 1);
        Assert.Equal(4, b.Want);                                  // поки свіже — тягне
        h.Tick();
        Assert.Equal(-1, b.Want);                                 // підтвердження не прийшло — відпустили
        Assert.Equal(4, b.Face);                                  // обличчя лишилось: ривок туди ж
        var y = b.B.Y;
        var v0 = b.B.Vy;
        Assert.True(v0 > 500, $"{v0}");
        h.Tick(60);
        Assert.True(b.B.Vy < v0 * 0.2, $"тіло мало гальмувати: {v0:0} → {b.B.Vy:0}");
        Assert.True(b.B.Y - y < v0 / IcefloeCore.Mu + 1, "доковзало не далі за гальмівний шлях");
        Assert.True(b.Alive, "без тяги тіло не доїхало до води");

        // браузер досилає той самий намір раз на 0.4 с — тоді тягне весь час
        var k = Table(2);
        ToGo(k);
        var c = Core(k).Bodies[1];
        for (var t = 0; t < 100; t++)
        {
            if (t % 10 == 0) k.Input(1, "move", new { a = 4 });
            k.Tick();
            Assert.Equal(4, c.Want);
        }
    }

    [Fact] // 55: відлік вікно наміру не з'їдає — хто тримав стрілку з відліку, рушає зі свистком
    public void Countdown_does_not_count_against_the_intent_window()
    {
        var h = Table(2);
        h.Input(0, "move", new { a = 8 });
        ToGo(h);
        var b = Core(h).Bodies[0];
        Assert.Equal(8, b.Want);
        h.Tick(Icefloe.KeepTicks - 1);
        Assert.Equal(8, b.Want);
        h.Tick();
        Assert.Equal(-1, b.Want);
    }

    [Fact] // 56 (рецензія коду): причина нічиєї раунду — у виді, і вона не перемикається, поки світ доковзує
    public void Draw_reason_comes_with_the_view_and_does_not_flip_during_the_end_phase()
    {
        var h = Table(3);
        ToGo(h);
        var c = Core(h);
        Sink(h, 2);
        h.Tick();
        c.Rt = IcefloeCore.CapTicks - 1;
        h.Tick();
        Assert.Equal(Icefloe.PhEnd, Game(h).Phase);
        var lr = h.View(null).GetProperty("lastRound");
        Assert.Equal(-1, lr.GetProperty("winner").GetInt32());
        Assert.True(lr.GetProperty("byTime").GetBoolean());
        // у фазі кінця один із двох уцілілих доїжджає у воду — у кадрі живий лишився один, а причина та сама
        Sink(h, 1);
        h.Tick();
        Assert.False(c.Bodies[1].Alive);
        Assert.True(h.View(null).GetProperty("lastRound").GetProperty("byTime").GetBoolean());

        // усі шубовснули разом — це не «час вийшов»
        var d = Table(2);
        ToGo(d);
        Sink(d, 0);
        Sink(d, 1);
        d.Tick();
        var dl = d.View(null).GetProperty("lastRound");
        Assert.Equal(-1, dl.GetProperty("winner").GetInt32());
        Assert.False(dl.GetProperty("byTime").GetBoolean());
    }

    [Fact] // 57 (плейтест): типово — «авто»: на двох–чотирьох до двох перемог, на п'ятьох і більше — один раунд
    public void Auto_wins_is_two_for_a_small_table_and_one_from_five()
    {
        static RoomHarness Auto(int players)
        {
            var h = new RoomHarness("icefloe", null, 5);
            foreach (var nick in Nicks.Take(players)) h.Join(nick);
            return h;
        }

        var four = Auto(4);
        Assert.Equal(2, NeedOf(four));                                 // лобі показує, що буде
        Assert.True(four.Start().Ok);
        Assert.Equal(2, Game(four).Need);

        var five = Auto(5);
        Assert.Equal(1, NeedOf(five));
        Assert.True(five.Start().Ok);
        Assert.Equal(1, Game(five).Need);
        Round(five, 3);
        Assert.Equal(RoomStatus.Finished, five.Room.Status);     // один раунд — і партія
        Assert.Equal([3], five.Finished.Single().Result.Winners);

        Assert.Equal(2, Game(Table(6, wins: "2")).Need);         // обрали явно — так і буде
        Assert.Equal("auto", Auto(2).Room.Info.Options![0].Default);

        static int NeedOf(RoomHarness r) => r.View(null).GetProperty("need").GetInt32();
    }
}
