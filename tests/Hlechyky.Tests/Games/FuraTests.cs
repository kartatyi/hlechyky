using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Чумаки-далекобійники», етап 1 (specs/fura.md, «Етап 1»): дошка, фази рейсу, підбір і здача, плата — через справжні
/// кімнати (<see cref="RoomHarness"/>) за столом на одного (етап 2: стіл до восьми, фура — у <see cref="FuraGarage"/>).
/// Правила судді окремо — <see cref="FuraJudgeTests"/>, колона й кілька фур — <see cref="FuraConvoyTests"/>.
/// </summary>
public sealed class FuraTests
{
    internal static readonly FuraMap Map = FuraMap.Get();

    /// <summary>Тик гри, с (TickMs 100).</summary>
    internal const double Dt = 0.1;

    /// <summary>Гараж фур (запис одразу, без пулу потоків) поверх сховища <paramref name="store"/>. Той самий провайдер
    /// у двох столів — та сама фура за двома столами (дві вкладки, перезапуск сайту).</summary>
    internal static IServiceProvider Garage(Func<IGameStore> store) =>
        new ServiceCollection().AddSingleton(_ => new FuraGarage(store(), a => a())).BuildServiceProvider();

    /// <summary>Стіл зі своїм гаражем поверх <see cref="RoomHarness.Store"/> (або спільним — <paramref name="garage"/>).</summary>
    internal static RoomHarness New(int seed = 42, IServiceProvider? garage = null)
    {
        RoomHarness? h = null;
        h = new RoomHarness("fura", seed: seed, services: garage ?? Garage(() => h!.Store));
        return h;
    }

    internal static RoomHarness Open(string nick = "Оля", int seed = 42, IServiceProvider? garage = null)
    {
        var h = New(seed, garage);
        Assert.True(h.Solo(nick).Ok, h.Reply.Message);
        return h;
    }

    internal static void Pose(RoomHarness h, double x, double z, double v = 0, bool c = false, int k = 100,
        double? rx = null, double? rz = null, double ry = 0, int seat = 0) =>
        h.Input(seat, "pose", new { x, z, v, c, k, rx = rx ?? x, rz = rz ?? z, ry });

    internal static JsonElement Order(RoomHarness h, int seat = 0) => h.View(seat).GetProperty("order");

    internal static FuraPlace From(JsonElement order) => Map.Place(order.GetProperty("from").GetString()!)!;
    internal static FuraPlace To(JsonElement order) => Map.Place(order.GetProperty("to").GetString()!)!;
    internal static FuraDock DockOf(JsonElement order) => From(order).Dock(order.GetProperty("dock").GetString()!)!;

    internal static string Phase(RoomHarness h, int seat = 0) => h.View(seat).GetProperty("phase").GetString()!;

    /// <summary>Їхати прямо з <paramref name="at"/> до <paramref name="to"/> зі швидкістю м/с, знімок на кожен тик (0,1 с).</summary>
    internal static void Drive(RoomHarness h, ref (double X, double Z) at, (double X, double Z) to, double speed, int k = 100, int seat = 0)
    {
        var step = speed * Dt;
        while (true)
        {
            var d = FuraJudge.Dist(at.X, at.Z, to.X, to.Z);
            if (d < 1e-9) break;
            var s = Math.Min(step, d);
            at = (at.X + (to.X - at.X) / d * s, at.Z + (to.Z - at.Z) / d * s);
            h.Tick();
            Pose(h, at.X, at.Z, speed, c: true, k: k, seat: seat);
        }
    }

    /// <summary>
    /// Узяти замовлення <paramref name="id"/>, зачепити причіп і довезти: спершу «петля» туди-назад на довжину дороги
    /// (щоб пройдено ≥ 0,9 км; <paramref name="loop"/> false — без неї), далі навпростець до першого боксу отримувача,
    /// стати й відчепитись рівно в боксі.
    /// </summary>
    internal static JsonElement Haul(RoomHarness h, object? id = null, double speed = 20, int k = 100, bool auto = false, bool loop = true,
        int seat = 0, bool take = true)
    {
        if (take) Assert.True(h.Act(seat, "take", new { id = id ?? 1 }).Ok, h.Reply.Message);
        var order = Order(h, seat);
        var dock = DockOf(order);
        Pose(h, dock.X, dock.Z, seat: seat);
        if (auto) Assert.True(h.Act(seat, "auto").Ok, h.Reply.Message);
        h.Tick();
        Pose(h, dock.X, dock.Z, c: true, seat: seat);
        Assert.Equal("haul", Phase(h, seat));
        var km = order.GetProperty("km").GetDouble();
        var at = (dock.X, dock.Z);
        if (loop)
        {
            Drive(h, ref at, (dock.X + km * 500, dock.Z), speed, k, seat);
            Drive(h, ref at, (dock.X, dock.Z), speed, k, seat);
        }
        var target = To(order).Docks[0];
        Drive(h, ref at, (target.X, target.Z), speed, k, seat);
        h.Tick();
        Pose(h, target.X, target.Z, 0, c: false, k: k, ry: target.Yaw, seat: seat);
        return order;
    }

    // ---------- стіл і вид ----------

    [Fact]
    public void Solo_opens_a_table_for_one_on_stage_two_with_a_board_of_five()
    {
        var h = Open();
        var v = h.View(0);
        Assert.Equal(2, v.GetProperty("stage").GetInt32());
        Assert.Equal(0, v.GetProperty("seat").GetInt32());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("col").ValueKind);
        var me = Assert.Single(v.GetProperty("convoy").EnumerateArray());
        Assert.Equal("Оля", me.GetProperty("nick").GetString());
        Assert.Equal("free", me.GetProperty("phase").GetString());
        Assert.Equal("kray", v.GetProperty("map").GetString());
        Assert.Equal("free", v.GetProperty("phase").GetString());
        Assert.Equal(0, v.GetProperty("trips").GetInt32());
        Assert.Equal(5, v.GetProperty("board").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("order").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("trip").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("pose").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("result").ValueKind);
        Assert.Equal(200, v.GetProperty("cap").GetInt32());   // Boost 1 у тестах
        var info = h.Room.Info;
        Assert.True(info is { MinPlayers: 1, MaxPlayers: 8, Persistent: false, Private: false, TickMs: 100, Start: StartMode.Immediate });
        Assert.Equal(TimeSpan.FromSeconds(60), h.Room.Game.HoldEmpty);
        Assert.True(h.Room.Game.LateJoin("Петро"));
    }

    [Fact]
    public void Board_orders_follow_the_map()
    {
        var h = Open();
        foreach (var o in h.View(0).GetProperty("board").EnumerateArray())
        {
            var cargo = Map.CargoOf(o.GetProperty("cargo").GetString()!)!;
            var from = o.GetProperty("from").GetString()!;
            var to = o.GetProperty("to").GetString()!;
            Assert.Contains(from, cargo.From);
            Assert.Contains(to, cargo.To);
            Assert.NotEqual(from, to);
            Assert.NotNull(Map.Place(from)!.Dock(o.GetProperty("dock").GetString()!));
            var km = o.GetProperty("km").GetDouble();
            Assert.Equal(Map.Km(from, to), km);
            Assert.Equal(FuraJudge.Minutes(km), o.GetProperty("minutes").GetInt32());
            Assert.Equal(FuraJudge.Pay(km, cargo.Mult, 100, false, false, false), o.GetProperty("pay").GetInt32());
        }
        var ids = h.View(0).GetProperty("board").EnumerateArray().Select(o => o.GetProperty("id").GetInt32()).ToArray();
        Assert.Equal([1, 2, 3, 4, 5], ids);
    }

    static string Key(IEnumerable<FuraOrder> board) => string.Join(";", board.Select(o => $"{o.Cargo}:{o.From}:{o.Dock}:{o.To}"));

    static string Key(JsonElement board) => string.Join(";", board.EnumerateArray().Select(o =>
        $"{o.GetProperty("cargo").GetString()}:{o.GetProperty("from").GetString()}:{o.GetProperty("dock").GetString()}:{o.GetProperty("to").GetString()}"));

    [Fact]
    public void Board_is_deterministic_by_day_nick_and_trips_not_by_room_seed()
    {
        var a = Open("Оля", seed: 1);
        var b = Open("Оля", seed: 999);
        Assert.Equal(a.View(0).GetProperty("board").GetRawText(), b.View(0).GetProperty("board").GetRawText());

        var day = Days.Today(a.Clock);
        Assert.Equal(Key(Fura.MakeBoard(Map, day, "оля", 0)), Key(a.View(0).GetProperty("board")));
        Assert.NotEqual(Key(Fura.MakeBoard(Map, day, "оля", 0)), Key(Fura.MakeBoard(Map, day, "петро", 0)));
        Assert.NotEqual(Key(Fura.MakeBoard(Map, day, "оля", 0)), Key(Fura.MakeBoard(Map, "2026-09-11", "оля", 0)));
        Assert.NotEqual(Key(Fura.MakeBoard(Map, day, "оля", 0)), Key(Fura.MakeBoard(Map, day, "оля", 1)));
    }

    [Fact]
    public void A_new_day_brings_a_new_board_while_free()
    {
        var h = Open();
        var before = h.View(0).GetProperty("board").GetRawText();
        h.Clock.Advance(TimeSpan.FromDays(1));
        h.Tick();
        Assert.NotEqual(before, h.View(0).GetProperty("board").GetRawText());
    }

    [Fact]
    public void Map_shortest_path_sums_polyline_segments()
    {
        // депо → Полтавка трасою: dvir → j1 → j4 → poltavka, три прямі відрізки
        static double D(double x1, double z1, double x2, double z2) => FuraJudge.Dist(x1, z1, x2, z2);
        var expected = D(-150, -250, 250, 150) + D(250, 150, 650, 550) + D(650, 550, 900, 800);
        Assert.Equal(expected, Map.Meters("dvir", "poltavka"), 6);
        // Гончарівка → Полтавка: ґрунтовка з чотирма зламами коротша за круг через депо
        var dirt = D(-850, 650, -400, 950) + D(-400, 950, 100, 950) + D(100, 950, 450, 880) + D(450, 880, 650, 880) + D(650, 880, 900, 800);
        Assert.Equal(dirt, Map.Meters("honchar", "poltavka"), 6);
        Assert.Equal(Math.Round(dirt / 1000, 1), Map.Km("honchar", "poltavka"));
        Assert.Equal(0, Map.Meters("sil", "sil"));
    }

    // ---------- take / cancel / next ----------

    [Fact]
    public void Take_puts_the_order_on_and_cancel_returns_the_same_board()
    {
        var h = Open();
        var board = h.View(0).GetProperty("board").GetRawText();
        var r = h.Act(0, "take", new { id = 3 });
        Assert.True(r.Ok, r.Message);
        var v = h.View(0);
        Assert.Equal("pickup", v.GetProperty("phase").GetString());
        Assert.Equal(3, v.GetProperty("order").GetProperty("id").GetInt32());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("board").ValueKind);
        Assert.Equal(h.Clock.UtcNow, v.GetProperty("trip").GetProperty("takenAt").GetDateTimeOffset());

        Assert.True(h.Act(0, "cancel").Ok);
        Assert.Equal("free", Phase(h));
        Assert.Equal(board, h.View(0).GetProperty("board").GetRawText());
    }

    [Fact]
    public void Take_accepts_a_string_id_too()
    {
        var h = Open();
        Assert.True(h.Act(0, "take", new { id = "2" }).Ok);
        Assert.Equal(2, Order(h).GetProperty("id").GetInt32());
    }

    [Fact]
    public void Cancel_works_mid_haul()
    {
        var h = Open();
        h.Act(0, "take", new { id = 1 });
        var dock = DockOf(Order(h));
        Pose(h, dock.X, dock.Z, c: true);
        Assert.Equal("haul", Phase(h));
        Assert.True(h.Act(0, "cancel").Ok);
        Assert.Equal("free", Phase(h));
    }

    [Fact]
    public void Illegal_actions_leave_the_state_as_it_was()
    {
        var h = Open();
        void Refused(string action, object? payload = null)
        {
            var before = h.Room.Game.Save();
            Assert.False(h.Act(0, action, payload).Ok, action);
            Assert.Equal(before, h.Room.Game.Save());
        }
        Refused("cancel");
        Refused("deliver");
        Refused("next");
        Refused("auto");
        Refused("take", new { id = 9 });
        Refused("take", new { });
        Refused("take");
        Refused("go");
        Refused("pose", new { x = 1 });
        Refused("pose", new { x = "1", z = 0, rx = 0, rz = 0 });

        Assert.True(h.Act(0, "take", new { id = 1 }).Ok);
        Refused("take", new { id = 2 });
        Refused("next");
        Refused("deliver");       // ще pickup
        Refused("auto");          // тягача ще ніде не видно
        Pose(h, 0, 0);
        Refused("auto");          // далеко від боксу
    }

    [Fact]
    public void Input_with_garbage_is_ignored()
    {
        var h = Open();
        var before = h.Room.Game.Save();
        h.Input(0, "pose", new { x = "NaN", z = 0 });
        h.Input(0, "pose", "hello");
        h.Input(0, "take", new { id = 77 });
        Assert.Equal(before, h.Room.Game.Save());
    }

    [Fact]
    public void Pose_can_come_as_an_act_too_and_is_then_saved()
    {
        var h = Open();
        Assert.True(h.Act(0, "pose", new { x = 1.5, z = -2, v = 3, c = 1, k = 77.4, rx = 1.5, rz = -10, ry = 0.5 }).Ok);
        var pose = h.View(0).GetProperty("pose");
        Assert.Equal(1.5, pose.GetProperty("x").GetDouble());
        Assert.True(pose.GetProperty("c").GetBoolean());
        Assert.Equal(77, pose.GetProperty("k").GetInt32());
        Assert.Equal(0.5, pose.GetProperty("h").GetDouble());   // старий клієнт без h — курс тягача = курс причепа
        Assert.Equal(0, pose.GetProperty("f").GetInt32());
        Assert.Equal(h.Clock.UtcNow, pose.GetProperty("at").GetDateTimeOffset());
        Assert.Contains("\"x\":1.5", h.Store.LoadState(FuraGarage.Key("оля")));

        // Каркас кличе Act і для Input — поза пишеться не частіше за раз на 5 с (і коли змінилось зчеплення)
        var saves = h.Store.Saves;
        h.Tick();
        h.Input(0, "pose", new { x = 2.0, z = -2, c = 1, rx = 2.0, rz = -10 });
        Assert.Equal(saves, h.Store.Saves);
        h.Tick();
        h.Input(0, "pose", new { x = 2.0, z = -2, c = 0, rx = 2.0, rz = -10 });
        Assert.Equal(saves + 1, h.Store.Saves);
        h.Clock.Advance(Fura.KeepPoseSec);
        h.Input(0, "pose", new { x = 2.5, z = -2, rx = 2.5, rz = -10, h = 1.2, f = 6 });
        Assert.Equal(saves + 2, h.Store.Saves);
        Assert.Contains("\"h\":1.2", h.Store.LoadState(FuraGarage.Key("оля")));
        // решта дій пишуться завжди
        Assert.True(h.Act(0, "take", new { id = 1 }).Ok);
        Assert.Equal(saves + 3, h.Store.Saves);
        Assert.Contains("\"phase\":\"pickup\"", h.Store.LoadState(FuraGarage.Key("оля")));
    }

    // ---------- підбір ----------

    [Fact]
    public void Pickup_needs_the_hitched_trailer_within_12_m_of_the_sender_dock()
    {
        var h = Open();
        h.Act(0, "take", new { id = 1 });
        var dock = DockOf(Order(h));
        Pose(h, dock.X, dock.Z + 19, c: true, rx: dock.X, rz: dock.Z + 12.5);
        Assert.Equal("pickup", Phase(h));
        h.Tick();
        Pose(h, dock.X, dock.Z + 19, c: false, rx: dock.X, rz: dock.Z + 11);
        Assert.Equal("pickup", Phase(h));
        h.Tick();
        Pose(h, dock.X, dock.Z + 19, c: true, rx: dock.X, rz: dock.Z + 11);
        Assert.Equal("haul", Phase(h));
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("trip").GetProperty("voided").ValueKind);
    }

    [Fact]
    public void Auto_hitch_needs_the_truck_within_30_m_of_the_dock()
    {
        var h = Open();
        h.Act(0, "take", new { id = 1 });
        var dock = DockOf(Order(h));
        Pose(h, dock.X + 31, dock.Z);
        Assert.False(h.Act(0, "auto").Ok);
        h.Tick();
        Pose(h, dock.X + 29, dock.Z);
        Assert.True(h.Act(0, "auto").Ok);
        Assert.True(h.View(0).GetProperty("trip").GetProperty("auto").GetBoolean());
    }

    // ---------- рейс і плата ----------

    [Fact]
    public void A_long_drive_to_the_trailer_does_not_eat_the_deadline()
    {
        // Термін — на рейс із вантажем: 25.10 перший живий рейс «запізнився», бо годинник рахував і дорогу порожняком
        var h = Open();
        Assert.True(h.Act(0, "take", new { id = 1 }).Ok, h.Reply.Message);
        var order = Order(h);
        var dock = DockOf(order);
        var minutes = order.GetProperty("minutes").GetInt32();
        // порожняком повільно, довше за весь термін
        var at = (X: dock.X + 400, Z: dock.Z);
        var ticks = (int)Math.Ceiling((minutes * 60 + 60) / Dt);
        for (var i = 0; i < ticks; i++)
        {
            at = (dock.X + 400 * (1 - (i + 1.0) / ticks), dock.Z);
            h.Tick();
            Pose(h, at.X, at.Z, 400.0 / (ticks * Dt));
        }
        h.Tick();
        Pose(h, dock.X, dock.Z, c: true);
        Assert.Equal("haul", Phase(h));
        var km = order.GetProperty("km").GetDouble();
        var p = (dock.X, dock.Z);
        Drive(h, ref p, (dock.X + km * 500, dock.Z), 20);
        Drive(h, ref p, (dock.X, dock.Z), 20);
        var target = To(order).Docks[0];
        Drive(h, ref p, (target.X, target.Z), 20);
        h.Tick();
        Pose(h, target.X, target.Z, 0, c: false, ry: target.Yaw);
        Assert.True(h.Act(0, "deliver").Ok, h.Reply.Message);
        Assert.True(h.View(0).GetProperty("result").GetProperty("onTime").GetBoolean());
    }

    [Fact]
    public void An_honest_trip_pays_with_bonuses_and_awards_fura_n()
    {
        var h = Open();
        var order = Haul(h);
        var r = h.Act(0, "deliver");
        Assert.True(r.Ok, r.Message);

        var v = h.View(0);
        Assert.Equal("done", v.GetProperty("phase").GetString());
        Assert.Equal(1, v.GetProperty("trips").GetInt32());
        var res = v.GetProperty("result");
        var km = order.GetProperty("km").GetDouble();
        var mult = Map.CargoOf(order.GetProperty("cargo").GetString()!)!.Mult;
        var pay = FuraJudge.Pay(km, mult, 100, onTime: true, exact: true, auto: false);
        Assert.True(res.GetProperty("onTime").GetBoolean());
        Assert.True(res.GetProperty("exact").GetBoolean());
        Assert.False(res.GetProperty("auto").GetBoolean());
        Assert.False(res.GetProperty("broken").GetBoolean());
        Assert.Equal(100, res.GetProperty("k").GetInt32());
        Assert.Equal(pay, res.GetProperty("base").GetInt32());
        Assert.Equal(pay, res.GetProperty("shards").GetInt32());
        Assert.Equal(1, res.GetProperty("trip").GetInt32());
        Assert.Equal(km, res.GetProperty("km").GetDouble());
        Assert.True(res.GetProperty("driven").GetDouble() >= 0.9 * km);

        var award = Assert.Single(h.Awards);
        Assert.Equal("fura:1", award.Reason);
        Assert.Equal(pay, award.Shards);
        Assert.Equal("Оля", award.Nick);

        // «Далі» — нова дошка (рейсів уже 1 — інший сід)
        Assert.True(h.Act(0, "next").Ok);
        Assert.Equal("free", Phase(h));
        Assert.Equal(Key(Fura.MakeBoard(Map, Days.Today(h.Clock), "оля", 1)), Key(h.View(0).GetProperty("board")));
    }

    [Fact]
    public void Damage_and_auto_hitch_lower_the_pay()
    {
        var h = Open();
        var order = Haul(h, k: 70, auto: true);
        Assert.True(h.Act(0, "deliver").Ok, h.Reply.Message);
        var res = h.View(0).GetProperty("result");
        var km = order.GetProperty("km").GetDouble();
        var mult = Map.CargoOf(order.GetProperty("cargo").GetString()!)!.Mult;
        Assert.Equal(70, res.GetProperty("k").GetInt32());
        Assert.True(res.GetProperty("auto").GetBoolean());
        Assert.Equal(FuraJudge.Pay(km, mult, 70, true, true, true), res.GetProperty("base").GetInt32());
        Assert.Equal(FuraJudge.Pay(km, mult, 70, true, true, true), Assert.Single(h.Awards).Shards);
    }

    [Fact]
    public void Broken_cargo_pays_nothing_but_counts_as_a_trip()
    {
        var h = Open("Петро");
        Haul(h, k: 15);
        Assert.True(h.Act(0, "deliver").Ok);
        var res = h.View(0).GetProperty("result");
        Assert.True(res.GetProperty("broken").GetBoolean());
        Assert.Equal(0, res.GetProperty("base").GetInt32());
        Assert.Equal(0, res.GetProperty("shards").GetInt32());
        Assert.Empty(h.Awards);
        Assert.Equal(1, h.View(0).GetProperty("trips").GetInt32());
    }

    [Fact]
    public void Too_fast_on_average_voids_the_trip_at_delivery()
    {
        // 150 км/год — жодного порушення, але в середньому швидше за 100 км/год
        var h = Open();
        Haul(h, speed: 150 / 3.6, loop: false);
        Assert.Equal(0, h.View(0).GetProperty("trip").GetProperty("violations").GetInt32());
        var r = h.Act(0, "deliver");
        Assert.False(r.Ok);
        Assert.Contains("швидко", r.Message);
        Assert.Equal("free", Phase(h));
        Assert.Empty(h.Awards);
    }

    [Fact]
    public void A_teleport_voids_the_trip_and_deliver_says_why()
    {
        var h = Open();
        h.Act(0, "take", new { id = 1 });
        var order = Order(h);
        var dock = DockOf(order);
        Pose(h, dock.X, dock.Z, c: true);
        h.Tick(2);   // швидкість міряється від 0,15 с
        var to = To(order).Docks[0];
        Pose(h, to.X, to.Z, c: false, ry: to.Yaw);
        Assert.Equal(JsonValueKind.String, h.View(0).GetProperty("trip").GetProperty("voided").ValueKind);
        var r = h.Act(0, "deliver");
        Assert.False(r.Ok);
        Assert.Contains("недійсний", r.Message);
        Assert.Equal("free", Phase(h));
        Assert.Empty(h.Awards);
    }

    [Fact]
    public void Deliver_refuses_until_stopped_unhitched_fresh_and_in_the_yard()
    {
        var h = Open();
        var order = Haul(h);
        var to = To(order);
        var dock = to.Docks[0];
        var at = (dock.X, dock.Z);
        string Refused()
        {
            var before = h.Room.Game.Save();
            var r = h.Act(0, "deliver");
            Assert.False(r.Ok);
            Assert.Equal(before, h.Room.Game.Save());
            return r.Message;
        }
        void StopAt(double x, double z)
        {
            Drive(h, ref at, (x, z), 8);
            h.Tick();
            Pose(h, x, z);
        }

        h.Tick();
        Pose(h, dock.X, dock.Z, v: 0.6);
        Assert.Contains("зупинись", Refused());
        h.Tick();
        Pose(h, dock.X, dock.Z, c: true);
        Assert.Contains("відчепи", Refused());
        // поза двором (+15 м) і далі 25 м від боксів
        StopAt(to.Yard.X + to.Yard.W / 2 + 16, to.Yard.Z);
        Assert.Contains("не в дворі", Refused());
        // у дворі, розширеному на 15 м
        var inX = to.Yard.X + to.Yard.W / 2 + 14;
        StopAt(inX, to.Yard.Z);
        h.Clock.Advance(2.1);
        Assert.Contains("телеметрії", Refused());
        Pose(h, inX, to.Yard.Z);
        var ok = h.Act(0, "deliver");
        Assert.True(ok.Ok, ok.Message);
        Assert.False(h.View(0).GetProperty("result").GetProperty("exact").GetBoolean());
    }

    [Fact]
    public void Rescue_forgives_one_small_jump()
    {
        var h = Open();
        h.Act(0, "take", new { id = 1 });
        var dock = DockOf(Order(h));
        Pose(h, dock.X, dock.Z, c: true);
        Assert.True(h.Act(0, "rescue").Ok);
        Assert.True(h.View(0).GetProperty("trip").GetProperty("rescue").GetBoolean());
        h.Tick(2);
        // 150 км/год (8,3 м за два тики) + 3 м «на колеса»
        Pose(h, dock.X + 150 / 3.6 * 2 * Dt + 3, dock.Z, c: true);
        var trip = h.View(0).GetProperty("trip");
        Assert.Equal(0, trip.GetProperty("violations").GetInt32());
        Assert.False(trip.GetProperty("rescue").GetBoolean());
    }

    // ---------- фура в гаражі ----------

    [Fact]
    public void A_new_table_picks_the_trip_up_from_the_store_mid_haul()
    {
        // Деплой чи «закрив і відкрив»: стола вже нема, а фура в сховищі — новий стіл бере рейс посеред haul
        var h = Open();
        h.Act(0, "take", new { id = 2 });
        var dock = DockOf(Order(h));
        Pose(h, dock.X, dock.Z, c: true);
        var at = (dock.X, dock.Z);
        Drive(h, ref at, (dock.X + 300, dock.Z), 20, k: 87);
        h.Act(0, "rescue");
        Assert.Equal("haul", Phase(h));
        var view = h.View(0).GetRawText();
        Assert.Contains("\"minK\":87", view);

        var again = New(seed: 7);
        again.Clock.UtcNow = h.Clock.UtcNow;
        again.Store.SaveState(FuraGarage.Key("оля"), h.Store.LoadState(FuraGarage.Key("оля"))!);
        Assert.True(again.Solo("Оля").Ok, again.Reply.Message);
        Assert.Equal(view, again.View(0).GetRawText());

        // і їде далі з тієї самої пози, без паузи-телепорту
        again.Tick(2);
        Pose(again, at.X + 4, at.Z, 20, c: true, k: 87);
        Assert.Equal(JsonValueKind.Null, again.View(0).GetProperty("trip").GetProperty("voided").ValueKind);
    }

    [Fact]
    public void Load_of_Save_restores_the_table_after_a_restart()
    {
        // Стіл Resumable: інакше вічна партія тримала б деплой (Rooms.Busy)
        var h = Open();
        Assert.True(h.Room.Game.Resumable);
        var free = h.View(0).GetRawText();
        lock (h.Room.Sync) h.Room.Game.Load(h.Room.Game.Save()!);
        Assert.Equal(free, h.View(0).GetRawText());

        h.Act(0, "take", new { id = 1 });
        var dock = DockOf(Order(h));
        Pose(h, dock.X, dock.Z, c: true);
        var at = (dock.X, dock.Z);
        Drive(h, ref at, (dock.X + 200, dock.Z), 20);   // лише Input: у знімку поза свіжа
        var haul = h.View(0).GetRawText();
        lock (h.Room.Sync) h.Room.Game.Load(h.Room.Game.Save()!);
        Assert.Equal(haul, h.View(0).GetRawText());

        var d = Open("Петро");
        Haul(d);
        Assert.True(d.Act(0, "deliver").Ok, d.Reply.Message);
        var done = d.View(0).GetRawText();
        lock (d.Room.Sync) d.Room.Game.Load(d.Room.Game.Save()!);
        Assert.Equal(done, d.View(0).GetRawText());
    }

    [Fact]
    public void Garbage_in_storage_starts_clean()
    {
        var h = New();
        h.Store.SaveState(FuraGarage.Key("оля"), "{\"phase\":\"haul\",\"order\":{\"cargo\":\"нема\"}}");
        Assert.True(h.Solo("Оля").Ok);
        Assert.Equal("free", Phase(h));

        var p = New();
        p.Store.SaveState(FuraGarage.Key("петро"), "не json");
        p.Store.SaveState(FuraGarage.OldKey("петро"), "[1,2]");
        Assert.True(p.Solo("Петро").Ok);
        Assert.Equal("free", Phase(p));

        var q = New();
        q.Store.SaveState(FuraGarage.OldKey("яся"), "{\"phase\":\"pickup\",\"order\":{\"id\":1}}");
        Assert.True(q.Solo("Яся").Ok);
        Assert.Equal("free", Phase(q));
    }

    // ---------- вид летить із тика ----------

    [Fact]
    public void Changes_reach_the_browser_on_the_next_tick()
    {
        var h = Open();
        h.Tick();   // стартовий вид
        h.Outbox.Clear();
        h.Act(0, "take", new { id = 1 });
        Assert.DoesNotContain(h.Outbox, m => m is RoomViews);   // реалтайм: Act видів не шле
        h.Tick();
        Assert.Contains(h.Outbox, m => m is RoomViews);
        h.Outbox.Clear();
        h.Tick();
        Assert.DoesNotContain(h.Outbox, m => m is RoomViews);   // нічого не мінялось — тиша
        Assert.DoesNotContain(h.Outbox, m => m is RoomFrame);
    }

    // ---------- мапа по HTTP ----------

    [Fact]
    public void Map_endpoint_serves_only_known_maps()
    {
        var ok = Assert.IsType<ContentHttpResult>(FuraSetup.Map("kray"));
        Assert.Equal(File.ReadAllText(Paths.Resolve("data/fura/maps/kray.json")), ok.ResponseContent);
        Assert.StartsWith("application/json", ok.ContentType);
        Assert.IsType<NotFound>(FuraSetup.Map("../appsettings"));
        Assert.IsType<NotFound>(FuraSetup.Map("KRAY"));
        Assert.IsType<NotFound>(FuraSetup.Map(""));
    }
}

/// <summary>Денна стеля рейсів у <see cref="Rewards"/>: своя, окремо від AwardDailyCap, сума й стеля × Boost.</summary>
public sealed class FuraRewardsTests
{
    [Fact]
    public void Trips_pay_times_boost_up_to_their_own_cap_and_the_rest_is_cut()
    {
        using var rig = new EconomyRig();
        rig.Options.Boost = 10;
        for (var i = 1; i <= 3; i++) rig.Events.Raise(new AwardEvent("fura", "r1", "Оля", 50, $"fura:{i}"));
        Assert.Equal(1500, rig.Paid("Оля", "award:fura"));
        rig.Events.Raise(new AwardEvent("fura", "r1", "Оля", 45, "fura:4"));
        Assert.Equal(1950, rig.Paid("Оля", "award:fura"));
        rig.Events.Raise(new AwardEvent("fura", "r1", "Оля", 50, "fura:5"));   // влазить лише 50 з 500
        Assert.Equal(2000, rig.Paid("Оля", "award:fura"));
        rig.Events.Raise(new AwardEvent("fura", "r1", "Оля", 50, "fura:6"));
        Assert.Equal(2000, rig.Paid("Оля", "award:fura"));

        // назавтра — знову
        rig.Clock.Advance(TimeSpan.FromDays(1));
        rig.Events.Raise(new AwardEvent("fura", "r1", "Оля", 20, "fura:7"));
        Assert.Equal(2200, rig.Paid("Оля", "award:fura"));
    }

    [Fact]
    public void Trips_do_not_eat_the_award_cap_and_awards_do_not_eat_the_trip_cap()
    {
        using var rig = new EconomyRig();
        rig.Options.Boost = 10;
        for (var i = 1; i <= 4; i++) rig.Events.Raise(new AwardEvent("fura", "r1", "Оля", 50, $"fura:{i}"));
        rig.Events.Raise(new AwardEvent("skilky", "s1", "Оля", 30, "skilky"));
        Assert.Equal(300, rig.Paid("Оля", "award:skilky"));       // AwardDailyCap 30 × 10 — ціла

        using var other = new EconomyRig();
        other.Options.Boost = 10;
        other.Events.Raise(new AwardEvent("skilky", "s1", "Петро", 30, "skilky"));
        other.Events.Raise(new AwardEvent("fura", "r1", "Петро", 50, "fura:1"));
        Assert.Equal(500, other.Paid("Петро", "award:fura"));
    }

    [Fact]
    public void The_same_trip_pays_once_even_from_another_table()
    {
        // Ref без кімнати (fura:<нік>:<N>): той самий рейс за двома столами (дві вкладки) — одна плата
        using var rig = new EconomyRig();
        rig.Events.Raise(new AwardEvent("fura", "r1", "Оля", 40, "fura:1"));
        rig.Events.Raise(new AwardEvent("fura", "r1", "Оля", 40, "fura:1"));
        rig.Events.Raise(new AwardEvent("fura", "r2", "Оля", 40, "fura:1"));
        Assert.Equal(40, rig.Paid("Оля", "award:fura"));
        rig.Events.Raise(new AwardEvent("fura", "r2", "Петро", 40, "fura:1"));   // інший нік — свій рейс №1
        Assert.Equal(40, rig.Paid("Петро", "award:fura"));
    }

    [Fact]
    public void Convoy_bonus_pays_once_per_trip_under_the_same_cap()
    {
        using var rig = new EconomyRig();
        rig.Options.Boost = 10;
        rig.Options.FuraDailyCap = 60;
        rig.Events.Raise(new AwardEvent("fura", "r1", "Оля", 50, "fura:3"));
        rig.Events.Raise(new AwardEvent("fura", "r1", "Оля", 8, "fura:3k"));
        rig.Events.Raise(new AwardEvent("fura", "r2", "Оля", 8, "fura:3k"));    // повтор — нічого
        Assert.Equal(580, rig.Paid("Оля", "award:fura"));
        rig.Events.Raise(new AwardEvent("fura", "r1", "Оля", 8, "fura:4k"));    // влазить лише 20 з 80
        Assert.Equal(600, rig.Paid("Оля", "award:fura"));
    }

    [Fact]
    public void Fura_cap_is_configurable_and_defaults_to_200()
    {
        Assert.Equal(200, new EconomyOptions().FuraDailyCap);
        using var rig = new EconomyRig();
        rig.Options.FuraDailyCap = 60;
        rig.Events.Raise(new AwardEvent("fura", "r1", "Оля", 50, "fura:1"));
        rig.Events.Raise(new AwardEvent("fura", "r1", "Оля", 50, "fura:2"));
        Assert.Equal(60, rig.Paid("Оля", "award:fura"));
    }
}

[Trait("Category", "Perf")]
[Collection(SerialPerf.Name)]
public sealed class FuraPerfTests
{
    [Fact]
    public void A_thousand_ticks_with_poses_take_well_under_two_seconds()
    {
        var h = new RoomHarness("fura");
        Assert.True(h.Solo("Оля").Ok);
        h.Act(0, "take", new { id = 1 });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            h.Tick();
            h.Input(0, "pose", new { x = i * 2.0, z = 0.0, v = 20.0, c = true, k = 100, rx = i * 2.0, rz = -8.0, ry = 1.57 });
            _ = h.View(0);
        }
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} мс");
    }

    [Fact]
    public void A_thousand_ticks_of_eight_rigs_with_ghost_frames_take_well_under_two_seconds()
    {
        var h = new RoomHarness("fura");
        Assert.True(h.Solo("Гравець0").Ok);
        for (var s = 1; s < 8; s++) Assert.True(h.Join($"Гравець{s}").Ok, h.Reply.Message);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            h.Tick();
            for (var s = 0; s < 8; s++)
                h.Input(s, "pose", new { x = i * 2.0, z = s * 20.0, v = 20.0, c = true, k = 100, rx = i * 2.0, rz = s * 20.0 - 8, ry = 1.57, h = 1.57, f = 2 });
        }
        sw.Stop();
        Assert.Contains(h.Outbox, m => m is RoomFrame);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} мс");
    }
}
