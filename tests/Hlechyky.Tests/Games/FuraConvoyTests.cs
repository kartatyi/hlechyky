using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using static Hlechyky.Tests.Games.FuraTests;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Чумаки», етап 2 (specs/fura.md, «Етап 2 — колона»): стіл до восьми фур, кадр привидів, пізній вхід, фура в гаражі
/// (OnLeave, новий стіл, міграція зі старого соло-запису, той самий нік за двома столами), колонне замовлення й бонус,
/// що бачить глядач.
/// </summary>
public sealed class FuraConvoyTests
{
    /// <summary>Стіл на двох: Оля створила, Петро сів.</summary>
    static RoomHarness Two(IServiceProvider? garage = null)
    {
        var h = Open("Оля", garage: garage);
        Assert.True(h.Join("Петро").Ok, h.Reply.Message);
        return h;
    }

    static JsonElement Col(RoomHarness h, int? seat = 0) => h.View(seat).GetProperty("col");

    static JsonElement LastFrame(RoomHarness h) => Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);

    static int[] Ints(JsonElement a) => [.. a.EnumerateArray().Select(x => x.GetInt32())];

    /// <summary>Свіжий знімок на місці, де фура стоїть (перед «Здати» після того, як їхав хтось інший).</summary>
    static void Stand(RoomHarness h, JsonElement order, int seat)
    {
        var t = To(order).Docks[0];
        h.Tick();
        Pose(h, t.X, t.Z, ry: t.Yaw, seat: seat);
    }

    static int Bonus(int @base) => (int)Math.Ceiling(Math.Round(@base * 0.15, 6));

    // ---------- стіл і кадр ----------

    [Fact]
    public void Two_rigs_get_a_ghost_frame_and_a_solo_does_not()
    {
        var solo = Open();
        Pose(solo, 1, 2);
        solo.Tick(3);
        Assert.DoesNotContain(solo.Outbox, m => m is RoomFrame);

        var h = Two();
        h.Input(0, "pose", new { x = 10.0, z = 20.0, v = 5.0, c = true, k = 90, rx = 10.0, rz = 12.0, ry = 0.5, h = 0.25, f = 3 });
        h.Input(1, "pose", new { x = -3.2, z = 7.0, rx = -3.2, rz = 0.0 });   // старий клієнт: h = ry, f = 0
        h.Outbox.Clear();
        h.Tick();
        var f = LastFrame(h);
        Assert.True(f.GetProperty("t").GetInt64() > 0);
        Assert.Equal([0, 100, 200, 250, 50, 1, 100, 120, 500, 3, 1, -32, 70, 0, 0, 0, -32, 0, 0, 0], Ints(f.GetProperty("p")));

        // поза старша за 3 с — фури в кадрі нема
        h.Clock.Advance(2);
        h.Input(1, "pose", new { x = -3.2, z = 7.0, rx = -3.2, rz = 0.0 });
        h.Clock.Advance(1.1);
        h.Outbox.Clear();
        h.Tick();
        Assert.Equal(1, Ints(LastFrame(h).GetProperty("p"))[0]);
        Assert.Equal(10, LastFrame(h).GetProperty("p").GetArrayLength());

        // за столом знову одна фура — кадрів нема
        Assert.True(h.Leave("Петро").Ok);
        h.Outbox.Clear();
        h.Tick(3);
        Assert.DoesNotContain(h.Outbox, m => m is RoomFrame);
    }

    [Fact]
    public void A_friend_joins_mid_trip_and_the_trip_goes_on()
    {
        var h = Open();
        Assert.True(h.Act(0, "take", new { id = 1 }).Ok);
        var dock = DockOf(Order(h));
        Pose(h, dock.X, dock.Z, c: true);
        var at = (dock.X, dock.Z);
        Drive(h, ref at, (dock.X + 100, dock.Z), 20);
        var trip = h.View(0).GetProperty("trip").GetRawText();

        Assert.True(h.Join("Петро").Ok, h.Reply.Message);
        Assert.Equal("haul", Phase(h));
        Assert.Equal(trip, h.View(0).GetProperty("trip").GetRawText());

        var v = h.View(1);
        Assert.Equal(2, v.GetProperty("stage").GetInt32());
        Assert.Equal(1, v.GetProperty("seat").GetInt32());
        Assert.Equal("free", v.GetProperty("phase").GetString());
        Assert.Equal(6, v.GetProperty("board").GetArrayLength());   // п'ять своїх + колонне
        var convoy = v.GetProperty("convoy").EnumerateArray().ToArray();
        Assert.Equal(2, convoy.Length);
        Assert.Equal("Оля", convoy[0].GetProperty("nick").GetString());
        Assert.Equal("haul", convoy[0].GetProperty("phase").GetString());
        Assert.Equal(Order(h).GetProperty("cargo").GetString(), convoy[0].GetProperty("cargo").GetString());
        Assert.Equal(Order(h).GetProperty("to").GetString(), convoy[0].GetProperty("to").GetString());
        Assert.False(convoy[0].GetProperty("col").GetBoolean());
        Assert.Equal(JsonValueKind.Null, convoy[1].GetProperty("cargo").ValueKind);

        Drive(h, ref at, (dock.X + 200, dock.Z), 20);
        Assert.Equal("haul", Phase(h));
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("trip").GetProperty("voided").ValueKind);
        Assert.True(h.View(0).GetProperty("trip").GetProperty("driven").GetDouble() >= 0.19);
    }

    [Fact]
    public void Spectator_sees_the_convoy_but_no_ones_board_order_or_trip()
    {
        var h = Two();
        Assert.True(h.Act(0, "take", new { id = 1 }).Ok);
        var w = h.View(null);
        foreach (var key in new[] { "seat", "phase", "board", "order", "trip", "pose", "result" })
            Assert.Equal(JsonValueKind.Null, w.GetProperty(key).ValueKind);
        Assert.Equal(2, w.GetProperty("convoy").GetArrayLength());
        Assert.Equal(2, w.GetProperty("stage").GetInt32());

        // і місце бачить свою дошку, а не чужу
        var mine = h.View(1);
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("order").ValueKind);
        var day = Days.Today(h.Clock);
        var own = Fura.MakeBoard(Map, day, "петро", 0).Select(o => o.Cargo + o.From + o.To + o.Dock);
        Assert.Equal(own, mine.GetProperty("board").EnumerateArray().Take(5)
            .Select(o => o.GetProperty("cargo").GetString() + o.GetProperty("from").GetString() + o.GetProperty("to").GetString() + o.GetProperty("dock").GetString()));
    }

    // ---------- фура в гаражі ----------

    [Fact]
    public void Leaving_keeps_the_rig_and_a_new_table_picks_it_up_mid_haul()
    {
        var store = new FakeStore();
        var garage = Garage(() => store);
        var a = Two(garage);
        Assert.True(a.Act(0, "take", new { id = 1 }).Ok);
        var dock = DockOf(Order(a));
        Pose(a, dock.X, dock.Z, c: true);
        var at = (dock.X, dock.Z);
        Drive(a, ref at, (dock.X + 150, dock.Z), 20);   // лише Input — у сховище не пише
        var trip = a.View(0).GetProperty("trip").GetRawText();
        var pose = a.View(0).GetProperty("pose").GetRawText();
        Assert.DoesNotContain("\"x\":" + (dock.X + 150).ToString(System.Globalization.CultureInfo.InvariantCulture),
            store.LoadState(FuraGarage.Key("оля")));

        Assert.True(a.Leave("Оля").Ok);   // без техпоразки: Петро їде далі
        Assert.Empty(a.Finished);
        Assert.Single(a.View(1).GetProperty("convoy").EnumerateArray());
        Assert.Contains("\"phase\":\"haul\"", store.LoadState(FuraGarage.Key("оля")));

        var b = New(seed: 5, garage: garage);
        b.Clock.UtcNow = a.Clock.UtcNow;
        Assert.True(b.Solo("Оля").Ok, b.Reply.Message);
        Assert.Equal("haul", Phase(b));
        Assert.Equal(trip, b.View(0).GetProperty("trip").GetRawText());
        Assert.Equal(pose, b.View(0).GetProperty("pose").GetRawText());
        b.Tick(2);
        Pose(b, at.X + 4, at.Z, 20, c: true);
        Assert.Equal(JsonValueKind.Null, b.View(0).GetProperty("trip").GetProperty("voided").ValueKind);
    }

    [Fact]
    public void An_old_solo_record_is_migrated_and_kept_and_trips_go_on()
    {
        // Запис етапу 1 (Persistent, ключ fura:<нік>) — та сама форма полів, v = 1, без колони
        var x = Open();
        Assert.True(x.Act(0, "take", new { id = 1 }).Ok);
        var order = Order(x);
        var dock = DockOf(order);
        Pose(x, dock.X, dock.Z, c: true);
        var at = (dock.X, dock.Z);
        Drive(x, ref at, (dock.X + 100, dock.Z), 20);
        Assert.True(x.Act(0, "pose", new { x = at.X, z = at.Z, v = 20, c = true, rx = at.X, rz = at.Z }).Ok);
        var node = JsonNode.Parse(x.Store.LoadState(FuraGarage.Key("оля"))!)!.AsObject();
        node.Remove("colRoom");
        node.Remove("colNo");
        node["v"] = 1;
        node["trips"] = 4;
        var old = node.ToJsonString();

        var h = New();
        h.Clock.UtcNow = x.Clock.UtcNow;
        h.Store.SaveState(FuraGarage.OldKey("оля"), old);
        Assert.True(h.Solo("Оля").Ok, h.Reply.Message);
        Assert.Equal("haul", Phase(h));
        Assert.Equal(4, h.View(0).GetProperty("trips").GetInt32());
        Assert.Equal(old, h.Store.LoadState(FuraGarage.OldKey("оля")));          // старий не видаляємо
        Assert.Contains("\"trips\":4", h.Store.LoadState(FuraGarage.Key("оля")));  // новий уже є

        // довозить — і рейс №5
        var km = order.GetProperty("km").GetDouble();
        Drive(h, ref at, (at.X + km * 500, at.Z), 20);
        Drive(h, ref at, (dock.X, dock.Z), 20);
        var t = To(order).Docks[0];
        Drive(h, ref at, (t.X, t.Z), 20);
        h.Tick();
        Pose(h, t.X, t.Z, ry: t.Yaw);
        Assert.True(h.Act(0, "deliver").Ok, h.Reply.Message);
        Assert.Equal("fura:5", Assert.Single(h.Awards).Reason);

        // а наступного разу — вже з нового запису, старий не перебиває
        var again = New();
        again.Clock.UtcNow = h.Clock.UtcNow;
        again.Store.SaveState(FuraGarage.OldKey("оля"), old);
        again.Store.SaveState(FuraGarage.Key("оля"), h.Store.LoadState(FuraGarage.Key("оля"))!);
        Assert.True(again.Solo("Оля").Ok);
        Assert.Equal(5, again.View(0).GetProperty("trips").GetInt32());
    }

    [Fact]
    public void Two_tables_one_nick_the_later_one_judges_and_the_trip_pays_once()
    {
        var store = new FakeStore();
        var garage = Garage(() => store);
        var a = Open("Оля", garage: garage);
        // за одним сервером другий стіл тим самим ніком каркас і так не дасть
        Assert.False(a.Rooms.Create("Оля", "fura", null).Reply.Ok);

        Assert.True(a.Act(0, "take", new { id = 1 }).Ok);
        var dock = DockOf(Order(a));
        Pose(a, dock.X, dock.Z, c: true);
        Assert.Equal("haul", Phase(a));

        // друга вкладка — інший стіл (тут — інші Rooms зі спільним гаражем): бере свіжу фуру
        var b = New(seed: 3, garage: garage);
        b.Clock.UtcNow = a.Clock.UtcNow;
        Assert.True(b.Solo("Оля").Ok);
        Assert.Equal("haul", Phase(b));

        foreach (var act in new[] { "deliver", "cancel", "next", "pose" })
        {
            var r = a.Act(0, act, new { x = 0, z = 0, rx = 0, rz = 0 });
            Assert.False(r.Ok, act);
            Assert.Equal("Ти вже в рейсі за іншим столом", r.Message);
        }
        a.Outbox.Clear();
        a.Tick();
        Assert.DoesNotContain(a.Outbox, m => m is RoomFrame);

        Haul(b, take: false);
        Assert.True(b.Act(0, "deliver").Ok, b.Reply.Message);
        Assert.Equal("fura:1", Assert.Single(b.Awards).Reason);
        Assert.Empty(a.Awards);

        // перший стіл, ідучи, фуру вже не перепише
        Assert.True(a.Leave("Оля").Ok);
        Assert.Contains("\"phase\":\"done\"", store.LoadState(FuraGarage.Key("оля")));
    }

    // ---------- колонне замовлення ----------

    [Fact]
    public void Convoy_from_take_to_bonus_for_both()
    {
        var h = Two();
        var k = h.View(0).GetProperty("board")[5];
        Assert.Equal("k", k.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, k.GetProperty("dock").ValueKind);
        Assert.Equal(k.GetRawText(), h.View(1).GetProperty("board")[5].GetRawText());   // одне на стіл
        var expected = Fura.MakeColOrder(Map, Days.Today(h.Clock), h.RoomId, 1)!;
        Assert.Equal(expected.Cargo, k.GetProperty("cargo").GetString());
        Assert.Equal(expected.From, k.GetProperty("from").GetString());
        Assert.Equal(expected.To, k.GetProperty("to").GetString());

        var r = h.Act(0, "take", new { id = "k" });
        Assert.True(r.Ok, r.Message);
        var col = Col(h);
        Assert.Equal(1, col.GetProperty("no").GetInt32());
        Assert.Equal("k", col.GetProperty("order").GetProperty("id").GetString());
        Assert.False(col.GetProperty("order").TryGetProperty("dock", out _));
        Assert.Equal(["Оля"], col.GetProperty("members").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal(JsonValueKind.Null, col.GetProperty("firstAt").ValueKind);
        Assert.Equal(expected.Dock, col.GetProperty("docks").GetProperty("Оля").GetString());
        Assert.Equal(expected.Dock, Order(h).GetProperty("dock").GetString());
        Assert.Equal("k", Order(h).GetProperty("id").GetString());
        Assert.True(h.View(0).GetProperty("convoy")[0].GetProperty("col").GetBoolean());

        // Петро бачить «приєднатися» і бере те саме
        Assert.Equal("k", h.View(1).GetProperty("board")[5].GetProperty("id").GetString());
        Assert.True(h.Act(1, "take", new { id = "k" }).Ok, h.Reply.Message);
        col = Col(h, null);   // колона видна й глядачеві
        Assert.Equal(["Оля", "Петро"], col.GetProperty("members").EnumerateArray().Select(m => m.GetString()));
        var d0 = col.GetProperty("docks").GetProperty("Оля").GetString();
        var d1 = col.GetProperty("docks").GetProperty("Петро").GetString();
        Assert.NotEqual(d0, d1);
        Assert.Equal(d1, Order(h, 1).GetProperty("dock").GetString());

        var o0 = Haul(h, seat: 0, take: false);
        var o1 = Haul(h, seat: 1, take: false);
        Stand(h, o0, 0);
        Assert.True(h.Act(0, "deliver").Ok, h.Reply.Message);
        var base0 = h.View(0).GetProperty("result").GetProperty("base").GetInt32();
        Assert.Equal(["fura:1"], h.Awards.Select(a => a.Reason));
        col = Col(h);
        Assert.Equal(JsonValueKind.String, col.GetProperty("firstAt").ValueKind);
        Assert.Equal("Оля", Assert.Single(col.GetProperty("done").EnumerateArray()).GetProperty("nick").GetString());
        Assert.Equal(0, h.View(0).GetProperty("result").GetProperty("bonus").GetInt32());   // сама одна здача — без бонусу

        Stand(h, o1, 1);
        r = h.Act(1, "deliver");
        Assert.True(r.Ok, r.Message);
        Assert.Contains("+15 %", r.Message);
        var base1 = h.View(1).GetProperty("result").GetProperty("base").GetInt32();
        var bonuses = h.Awards.Where(a => a.Reason.EndsWith('k')).ToDictionary(a => a.Nick, a => (a.Reason, a.Shards));
        Assert.Equal(("fura:1k", Bonus(base0)), bonuses["Оля"]);
        Assert.Equal(("fura:1k", Bonus(base1)), bonuses["Петро"]);
        Assert.Equal(4, h.Awards.Count);
        var res0 = h.View(0).GetProperty("result");
        Assert.True(res0.GetProperty("col").GetBoolean());
        Assert.Equal(Bonus(base0), res0.GetProperty("bonus").GetInt32());
        Assert.Equal(Bonus(base1), h.View(1).GetProperty("result").GetProperty("bonus").GetInt32());

        // усі здали — колона скінчилась, наступна вже інша (№2)
        Assert.Equal(JsonValueKind.Null, Col(h).ValueKind);
        Assert.True(h.Act(0, "next").Ok);
        var next = h.View(0).GetProperty("board")[5];
        var second = Fura.MakeColOrder(Map, Days.Today(h.Clock), h.RoomId, 2)!;
        Assert.Equal(second.Cargo + second.From + second.To,
            next.GetProperty("cargo").GetString() + next.GetProperty("from").GetString() + next.GetProperty("to").GetString());
    }

    [Fact]
    public void Convoy_is_for_two_and_more_and_no_bigger_than_the_sender_docks()
    {
        var solo = Open();
        Assert.Equal(5, solo.View(0).GetProperty("board").GetArrayLength());   // на одного колонного нема
        var lone = solo.Act(0, "take", new { id = "k" });
        Assert.False(lone.Ok);

        var h = Open("Гравець0");
        for (var s = 1; s < 8; s++) Assert.True(h.Join($"Гравець{s}").Ok, h.Reply.Message);
        var from = Map.Place(h.View(0).GetProperty("board")[5].GetProperty("from").GetString()!)!;
        Assert.InRange(from.Docks.Length, 2, 7);
        for (var s = 0; s < from.Docks.Length; s++) Assert.True(h.Act(s, "take", new { id = "k" }).Ok, h.Reply.Message);
        var docks = Col(h).GetProperty("docks").EnumerateObject().Select(p => p.Value.GetString()).ToArray();
        Assert.Equal(from.Docks.Length, docks.Distinct().Count());
        var full = h.Act(from.Docks.Length, "take", new { id = "k" });
        Assert.False(full.Ok);
        Assert.Equal("Колона повна", full.Message);
        Assert.Equal("free", Phase(h, from.Docks.Length));
    }

    [Fact]
    public void Cancel_leaves_the_convoy_and_the_last_one_out_ends_it()
    {
        var h = Two();
        Assert.True(h.Act(0, "take", new { id = "k" }).Ok);
        Assert.True(h.Act(1, "take", new { id = "k" }).Ok);
        var r = h.Act(1, "cancel");
        Assert.True(r.Ok);
        Assert.Equal("Ти вийшов з колони", r.Message);
        Assert.Equal(["Оля"], Col(h).GetProperty("members").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal("free", Phase(h, 1));
        Assert.Equal("k", h.View(1).GetProperty("board")[5].GetProperty("id").GetString());   // можна назад
        Assert.False(h.View(1).GetProperty("convoy")[1].GetProperty("col").GetBoolean());

        // передумав — знову в колоні
        Assert.True(h.Act(1, "take", new { id = "k" }).Ok);
        Assert.Equal(2, Col(h).GetProperty("members").GetArrayLength());
        Assert.True(h.Act(1, "cancel").Ok);
        Assert.True(h.Act(0, "cancel").Ok);
        Assert.Equal(JsonValueKind.Null, Col(h).ValueKind);
        var next = h.View(0).GetProperty("board")[5];
        var second = Fura.MakeColOrder(Map, Days.Today(h.Clock), h.RoomId, 2)!;
        Assert.Equal(second.Cargo + second.From + second.To,
            next.GetProperty("cargo").GetString() + next.GetProperty("from").GetString() + next.GetProperty("to").GetString());
    }

    [Fact]
    public void Delivery_after_the_five_minute_window_has_no_bonus()
    {
        var h = Two();
        Assert.True(h.Act(0, "take", new { id = "k" }).Ok);
        Assert.True(h.Act(1, "take", new { id = "k" }).Ok);
        var o0 = Haul(h, seat: 0, take: false);
        var o1 = Haul(h, seat: 1, take: false);
        Stand(h, o0, 0);
        Assert.True(h.Act(0, "deliver").Ok, h.Reply.Message);

        h.Clock.Advance(FuraConvoy.Window + TimeSpan.FromSeconds(1));
        h.Tick();
        Assert.Equal(JsonValueKind.Null, Col(h).ValueKind);   // вікно минуло — колони нема
        Stand(h, o1, 1);
        Assert.True(h.Act(1, "deliver").Ok, h.Reply.Message);
        Assert.Equal(["fura:1", "fura:1"], h.Awards.Select(a => a.Reason));
        Assert.Equal(0, h.View(1).GetProperty("result").GetProperty("bonus").GetInt32());
        Assert.True(h.View(1).GetProperty("result").GetProperty("col").GetBoolean());
    }

    [Fact]
    public void Who_left_the_table_gets_no_bonus_and_late_joiners_wait_for_the_next_convoy()
    {
        var h = Two();
        Assert.True(h.Join("Яся").Ok);
        Assert.True(h.Act(0, "take", new { id = "k" }).Ok);
        Assert.True(h.Act(1, "take", new { id = "k" }).Ok);
        var o0 = Haul(h, seat: 0, take: false);
        var o1 = Haul(h, seat: 1, take: false);
        Stand(h, o0, 0);
        Assert.True(h.Act(0, "deliver").Ok, h.Reply.Message);

        // хтось здав — Ясі колонного вже не взяти
        Assert.Equal(5, h.View(2).GetProperty("board").GetArrayLength());
        var late = h.Act(2, "take", new { id = "k" });
        Assert.False(late.Ok);

        Assert.True(h.Leave("Оля").Ok);
        Stand(h, o1, 1);
        Assert.True(h.Act(1, "deliver").Ok, h.Reply.Message);
        var bonus = Assert.Single(h.Awards, a => a.Reason.EndsWith('k'));
        Assert.Equal("Петро", bonus.Nick);
    }

    [Fact]
    public void A_member_who_does_not_come_back_within_a_minute_drops_out_of_the_convoy()
    {
        var h = Two();
        Assert.True(h.Join("Яся").Ok);
        Assert.True(h.Act(0, "take", new { id = "k" }).Ok);
        Assert.True(h.Act(1, "take", new { id = "k" }).Ok);
        Assert.True(h.Leave("Петро").Ok);
        h.Tick();
        Assert.Equal(2, Col(h).GetProperty("members").GetArrayLength());   // F5 — місце в колоні чекає
        h.Clock.Advance(61);
        h.Tick();
        Assert.Equal(["Оля"], Col(h).GetProperty("members").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal("k", h.View(2).GetProperty("board")[5].GetProperty("id").GetString());
    }
}
