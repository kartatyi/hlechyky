using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static Hlechyky.Tests.Games.FuraTests;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Чумаки», хвиля D3 (specs/fura.md, «Мапи на сервері (D3)»): опція столу <c>map</c> і вимикач <c>Fura:Maps</c>, мапа
/// Житомира по HTTP (gzip, ETag), Дейкстра списком суміжності, рейс від замовлення до плати на Житомирі, колона там само,
/// і фура по мапах: поза й рейс свої на кожну мапу, рейси (номери, плата) і гараж — спільні.
/// </summary>
public sealed class FuraMapsTests
{
    static readonly FuraMap Zh = FuraMap.Get(FuraMap.Zhytomyr);

    /// <summary>Гараж фур поверх одного сховища — та сама фура за столами різних мап; <paramref name="maps"/> — вимикач.</summary>
    static IServiceProvider Services(FakeStore store, string? maps = null)
    {
        var s = new ServiceCollection().AddSingleton(_ => new FuraGarage(store, a => a()));
        if (maps is not null) s.AddSingleton<IOptionsMonitor<FuraOptions>>(new FixedOptions<FuraOptions>(new FuraOptions { Maps = maps }));
        return s.BuildServiceProvider();
    }

    /// <summary>Стіл мапи <paramref name="map"/> (опція лобі), за ним — <paramref name="nick"/>. Без <paramref name="services"/> —
    /// свій гараж поверх <see cref="RoomHarness.Store"/>.</summary>
    static RoomHarness Table(string? map, string nick = "Оля", IServiceProvider? services = null)
    {
        RoomHarness? h = null;
        h = new RoomHarness("fura", options: map is null ? null : new { map }, seed: 42, services: services ?? Garage(() => h!.Store));
        Assert.True(h.Join(nick).Ok, h.Reply.Message);
        return h;
    }

    static string MapOf(RoomHarness h, int? seat = 0) => h.View(seat).GetProperty("map").GetString()!;

    static FuraDock DockOn(FuraMap map, JsonElement order) =>
        map.Place(order.GetProperty("from").GetString()!)!.Dock(order.GetProperty("dock").GetString()!)!;

    /// <summary>
    /// Як <see cref="FuraTests.Haul"/>, але на будь-якій мапі: узяти, зачепити, «петля» на довжину дороги, навпростець до
    /// першого боксу отримувача, стати й відчепитись. Сервер не знає вулиць — лише позу, тож підставні пози проходять.
    /// </summary>
    static JsonElement HaulOn(FuraMap map, RoomHarness h, object id, int seat = 0, double speed = 15, bool take = true)
    {
        if (take) Assert.True(h.Act(seat, "take", new { id }).Ok, h.Reply.Message);
        var order = Order(h, seat);
        var dock = DockOn(map, order);
        Pose(h, dock.X, dock.Z, seat: seat);
        h.Tick();
        Pose(h, dock.X, dock.Z, c: true, seat: seat);
        Assert.Equal("haul", Phase(h, seat));
        var km = order.GetProperty("km").GetDouble();
        var at = (dock.X, dock.Z);
        Drive(h, ref at, (dock.X + km * 500, dock.Z), speed, seat: seat);
        Drive(h, ref at, (dock.X, dock.Z), speed, seat: seat);
        var target = map.Place(order.GetProperty("to").GetString()!)!.Docks[0];
        Drive(h, ref at, (target.X, target.Z), speed, seat: seat);
        h.Tick();
        Pose(h, target.X, target.Z, 0, c: false, ry: target.Yaw, seat: seat);
        return order;
    }

    // ---------- опція й вимикач ----------

    [Fact]
    public void Map_is_a_table_option_with_kray_by_default()
    {
        Assert.Equal(["kray", "zhytomyr"], FuraMap.Known);
        var opt = Assert.Single(new Fura().Info.Options!, o => o.Key == Fura.MapKey);
        Assert.Equal("kray", opt.Default);
        Assert.Equal(["kray", "zhytomyr"], opt.Values.Select(v => v.Value));
        Assert.Equal(["🌾 Чумацький край", "🏙 Житомир"], opt.Values.Select(v => v.Label));
    }

    [Theory]
    [InlineData("zhytomyr", null, "zhytomyr")]
    [InlineData("zhytomyr", "kray,zhytomyr", "zhytomyr")]
    [InlineData("zhytomyr", " kray , zhytomyr ", "zhytomyr")]
    [InlineData("zhytomyr", "kray", "kray")]        // прод, поки D2 не готовий
    [InlineData("zhytomyr", "", "kray")]
    [InlineData("kray", "zhytomyr", "kray")]        // край — завжди
    [InlineData("odesa", "kray,zhytomyr,odesa", "kray")]
    [InlineData("ZHYTOMYR", "kray,zhytomyr", "kray")]
    [InlineData(null, "kray,zhytomyr", "kray")]
    public void Pick_takes_a_known_allowed_map_and_falls_back_to_kray(string? asked, string? maps, string expected)
    {
        var allowed = maps is null ? null : new FuraOptions { Maps = maps }.AllowedMaps();
        Assert.Equal(expected, FuraMap.Pick(asked, allowed));
    }

    [Fact]
    public void Without_config_only_kray_and_prod_config_opens_zhytomyr()
    {
        // без конфігу — лише край (безпечно); прод з 0.0.7 відкриває й Житомир (клієнт D2 готовий)
        Assert.Equal(["kray"], new FuraOptions().AllowedMaps());
        using var doc = JsonDocument.Parse(File.ReadAllText(Paths.Resolve("appsettings.json")),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        Assert.Equal("kray,zhytomyr", doc.RootElement.GetProperty("Fura").GetProperty("Maps").GetString());
    }

    [Fact]
    public void A_zhytomyr_table_runs_on_zhytomyr_and_the_view_says_so()
    {
        var h = Table("zhytomyr");
        var v = h.View(0);
        Assert.Equal("zhytomyr", v.GetProperty("map").GetString());
        Assert.Equal("zhytomyr", h.View(null).GetProperty("map").GetString());   // глядач будує той самий світ
        var board = v.GetProperty("board").EnumerateArray().ToList();
        Assert.Equal(Fura.BoardSize, board.Count);
        foreach (var o in board)
        {
            var cargo = Zh.CargoOf(o.GetProperty("cargo").GetString()!)!;
            Assert.Contains(o.GetProperty("from").GetString(), cargo.From);
            Assert.Contains(o.GetProperty("to").GetString(), cargo.To);
            Assert.NotNull(DockOn(Zh, o));
            Assert.Equal(Zh.Km(o.GetProperty("from").GetString()!, o.GetProperty("to").GetString()!), o.GetProperty("km").GetDouble());
            Assert.InRange(o.GetProperty("km").GetDouble(), 0.3, 20);   // місця й на околицях: до 11 км дорогами
        }
    }

    [Fact]
    public void A_disallowed_or_unknown_map_runs_on_kray()
    {
        Assert.Equal("kray", MapOf(Table("zhytomyr", services: Services(new FakeStore(), "kray"))));
        Assert.Equal("kray", MapOf(Table("odesa")));
        Assert.Equal("kray", MapOf(Table(null)));
        Assert.Equal("zhytomyr", MapOf(Table("zhytomyr", services: Services(new FakeStore(), "kray,zhytomyr"))));
    }

    // ---------- HTTP ----------

    static async Task<(int Status, HttpResponse Response, byte[] Body)> Get(string id, string? ifNoneMatch = null, bool gzip = false)
    {
        var c = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        var body = new MemoryStream();
        c.Response.Body = body;
        if (ifNoneMatch is not null) c.Request.Headers.IfNoneMatch = ifNoneMatch;
        if (gzip) c.Request.Headers.AcceptEncoding = "gzip, deflate, br";
        await FuraSetup.Map(id, c).ExecuteAsync(c);
        return (c.Response.StatusCode, c.Response, body.ToArray());
    }

    [Fact]
    public async Task Map_endpoint_serves_zhytomyr_with_gzip_and_etag()
    {
        var file = File.ReadAllText(Paths.Resolve("data/fura/maps/zhytomyr.json"));
        var plain = await Get("zhytomyr");
        Assert.Equal(200, plain.Status);
        Assert.Equal(file, System.Text.Encoding.UTF8.GetString(plain.Body));
        Assert.Equal("no-cache", plain.Response.Headers.CacheControl.ToString());
        var etag = plain.Response.Headers.ETag.ToString();
        Assert.StartsWith("W/\"", etag);

        var packed = await Get("zhytomyr", gzip: true);
        Assert.Equal("gzip", packed.Response.Headers.ContentEncoding.ToString());
        Assert.Equal(etag, packed.Response.Headers.ETag.ToString());
        Assert.True(packed.Body.Length < plain.Body.Length / 4, $"{packed.Body.Length} з {plain.Body.Length}");
        using (var z = new GZipStream(new MemoryStream(packed.Body), CompressionMode.Decompress))
        using (var r = new StreamReader(z))
            Assert.Equal(file, r.ReadToEnd());

        var same = await Get("zhytomyr", ifNoneMatch: etag, gzip: true);
        Assert.Equal(304, same.Status);
        Assert.Empty(same.Body);
        Assert.Equal(200, (await Get("zhytomyr", ifNoneMatch: "W/\"0000\"")).Status);

        // край — так само; ETag у кожної мапи свій
        var kray = await Get("kray", gzip: true);
        Assert.Equal(200, kray.Status);
        Assert.NotEqual(etag, kray.Response.Headers.ETag.ToString());
        Assert.Equal(404, (await Get("../appsettings")).Status);
    }

    [Fact]
    public void Maps_list_follows_the_switch()
    {
        static string[] Ids(string maps) =>
            [.. Views.Json(FuraSetup.Maps(new FuraOptions { Maps = maps })).GetProperty("maps").EnumerateArray().Select(m => m.GetProperty("id").GetString()!)];
        Assert.Equal(["kray"], Ids("kray"));
        Assert.Equal(["kray", "zhytomyr"], Ids("kray,zhytomyr"));
        var one = Views.Json(FuraSetup.Maps(new FuraOptions { Maps = "kray,zhytomyr" }));
        Assert.Equal("Житомир", one.GetProperty("maps")[1].GetProperty("title").GetString());
        Assert.Equal("kray", one.GetProperty("def").GetString());
    }

    // ---------- маршрути ----------

    /// <summary>Дейкстра з переліком усіх доріг на кожен крок — еталон для списку суміжності.</summary>
    static Dictionary<string, double> Naive(FuraMap map, string start)
    {
        var dist = map.Nodes.Keys.ToDictionary(k => k, _ => double.PositiveInfinity, StringComparer.Ordinal);
        dist[start] = 0;
        var done = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var node = dist.Where(kv => !done.Contains(kv.Key) && double.IsFinite(kv.Value)).OrderBy(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
            if (node is null) return dist;
            done.Add(node);
            foreach (var r in map.Roads)
            {
                var other = r.A == node ? r.B : r.B == node ? r.A : null;
                if (other is not null && dist[node] + r.Length < dist[other]) dist[other] = dist[node] + r.Length;
            }
        }
    }

    [Fact]
    public void Adjacency_dijkstra_matches_the_brute_force_one_on_both_maps()
    {
        foreach (var map in new[] { FuraMap.Get(), Zh })
            foreach (var from in map.Places)
            {
                var naive = Naive(map, from.Node);
                foreach (var to in map.Places)
                {
                    Assert.Equal(naive[to.Node], map.Meters(from.Id, to.Id), 6);
                    Assert.Equal(map.Meters(to.Id, from.Id), map.Meters(from.Id, to.Id), 6);   // дороги двобічні
                }
            }
    }

    // ---------- рейс на Житомирі ----------

    [Fact]
    public void A_zhytomyr_trip_goes_from_order_to_payment()
    {
        var h = Table("zhytomyr");
        var order = HaulOn(Zh, h, 1);
        Assert.True(h.Act(0, "deliver").Ok, h.Reply.Message);
        Assert.Equal("done", Phase(h));
        var award = Assert.Single(h.Awards);
        Assert.Equal("fura:1", award.Reason);
        var result = h.View(0).GetProperty("result");
        Assert.Equal(order.GetProperty("to").GetString(), result.GetProperty("to").GetString());
        Assert.Equal(award.Shards, result.GetProperty("base").GetInt32());
        Assert.InRange(award.Shards, 15, 50);
        Assert.True(h.Act(0, "next").Ok);
        Assert.Equal(1, h.View(0).GetProperty("trips").GetInt32());
        Assert.Equal(Fura.BoardSize, h.View(0).GetProperty("board").GetArrayLength());
    }

    [Fact]
    public void The_judge_rules_the_same_on_zhytomyr()
    {
        // здати не там — відмова, стан той самий; телепорт — рейс недійсний
        var h = Table("zhytomyr");
        Assert.True(h.Act(0, "take", new { id = 1 }).Ok);
        var dock = DockOn(Zh, Order(h));
        Pose(h, dock.X, dock.Z);
        h.Tick();
        Pose(h, dock.X, dock.Z, c: true);
        h.Tick();
        Pose(h, dock.X, dock.Z, c: false);
        Assert.False(h.Act(0, "deliver").Ok);
        Assert.Equal("haul", Phase(h));
        h.Tick(2);
        Pose(h, dock.X + 2000, dock.Z, 20, c: true);
        Assert.NotEqual(JsonValueKind.Null, h.View(0).GetProperty("trip").GetProperty("voided").ValueKind);
    }

    [Fact]
    public void A_convoy_runs_on_zhytomyr_within_its_map()
    {
        var store = new FakeStore();
        var services = Services(store);
        var h = Table("zhytomyr", "Оля", services);
        Assert.True(h.Join("Петро").Ok, h.Reply.Message);
        var k = h.View(0).GetProperty("board").EnumerateArray().Last();
        Assert.Equal("k", k.GetProperty("id").GetString());
        Assert.NotNull(Zh.Place(k.GetProperty("from").GetString()!));
        Assert.True(h.Act(0, "take", new { id = "k" }).Ok, h.Reply.Message);
        Assert.True(h.Act(1, "take", new { id = "k" }).Ok, h.Reply.Message);
        var docks = h.View(0).GetProperty("col").GetProperty("docks");
        Assert.NotEqual(docks.GetProperty("Оля").GetString(), docks.GetProperty("Петро").GetString());

        HaulOn(Zh, h, "k", seat: 0, take: false);
        // Петро чекав, поки їхала Оля: його фура стоїть зачеплена в боксі — довозимо так само
        var order = Order(h, 1);
        var dock = DockOn(Zh, order);
        h.Tick();
        Pose(h, dock.X, dock.Z, seat: 1);
        h.Tick();
        Pose(h, dock.X, dock.Z, c: true, seat: 1);
        var at = (dock.X, dock.Z);
        Drive(h, ref at, (dock.X + order.GetProperty("km").GetDouble() * 500, dock.Z), 15, seat: 1);
        Drive(h, ref at, (dock.X, dock.Z), 15, seat: 1);
        var t = Zh.Place(order.GetProperty("to").GetString()!)!.Docks[0];
        Drive(h, ref at, (t.X, t.Z), 15, seat: 1);
        h.Tick();
        Pose(h, t.X, t.Z, 0, c: false, ry: t.Yaw, seat: 1);
        // Оля стоїть на місці — свіжий знімок перед «Здати»
        var o0 = Zh.Place(Order(h, 0).GetProperty("to").GetString()!)!.Docks[0];
        Pose(h, o0.X, o0.Z, ry: o0.Yaw, seat: 0);
        Assert.True(h.Act(0, "deliver").Ok, h.Reply.Message);
        Assert.True(h.Act(1, "deliver").Ok, h.Reply.Message);
        Assert.Contains(h.Awards, a => a.Nick == "Оля" && a.Reason == "fura:1k");
        Assert.Contains(h.Awards, a => a.Nick == "Петро" && a.Reason == "fura:1k");
    }

    // ---------- фура по мапах ----------

    [Fact]
    public void A_kray_trip_waits_while_the_driver_is_in_zhytomyr()
    {
        var store = new FakeStore();
        var services = Services(store);

        // край: узяв, зачепив, від'їхав — і встав посеред рейсу
        var kray = Table(null, services: services);
        Assert.True(kray.Act(0, "take", new { id = 2 }).Ok);
        var dock = DockOf(Order(kray));
        Pose(kray, dock.X, dock.Z, c: true);
        var at = (dock.X, dock.Z);
        Drive(kray, ref at, (dock.X + 300, dock.Z), 20, k: 91);
        Assert.True(kray.Act(0, "pose", new { x = at.X, z = at.Z, v = 20.0, c = true, k = 91, rx = at.X, rz = at.Z, ry = 0.0 }).Ok);
        var krayOrder = Order(kray).GetRawText();
        var krayPose = kray.View(0).GetProperty("pose").GetRawText();
        Assert.True(kray.Leave("Оля").Ok);

        // Житомир: фура в депо, свій рейс від замовлення до плати, номер рейсу — наскрізний
        var zh = Table("zhytomyr", services: services);
        zh.Clock.UtcNow = kray.Clock.UtcNow;
        var v = zh.View(0);
        Assert.Equal("free", v.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("pose").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("order").ValueKind);
        HaulOn(Zh, zh, 1);
        Assert.True(zh.Act(0, "deliver").Ok, zh.Reply.Message);
        Assert.Equal("fura:1", Assert.Single(zh.Awards).Reason);
        Assert.True(zh.Leave("Оля").Ok);

        // запис один: край зверху як був, Житомир — у maps
        var record = JsonNode.Parse(store.LoadState(FuraGarage.Key("оля"))!)!.AsObject();
        Assert.Equal("haul", (string?)record["phase"]);
        Assert.Equal(1, (int)record["trips"]!);
        Assert.Equal("done", (string?)record["maps"]!["zhytomyr"]!["phase"]);
        Assert.Null(record["maps"]!["zhytomyr"]!["trips"]);

        // назад у край: той самий рейс, та сама поза; здача — рейс №2, не повтор оплаченого №1
        var back = Table(null, services: services);
        back.Clock.UtcNow = zh.Clock.UtcNow;
        Assert.Equal("haul", Phase(back));
        Assert.Equal(krayOrder, Order(back).GetRawText());
        Assert.Equal(krayPose, back.View(0).GetProperty("pose").GetRawText());
        Assert.Equal(1, back.View(0).GetProperty("trips").GetInt32());
        Assert.Equal(91, back.View(0).GetProperty("trip").GetProperty("minK").GetInt32());

        var target = To(Order(back)).Docks[0];
        Pose(back, at.X, at.Z, c: true, k: 91);          // після паузи — з того ж місця
        var p = at;
        Drive(back, ref p, (at.X + Order(back).GetProperty("km").GetDouble() * 500, at.Z), 20, k: 91);
        Drive(back, ref p, at, 20, k: 91);
        Drive(back, ref p, (target.X, target.Z), 20, k: 91);
        back.Tick();
        Pose(back, target.X, target.Z, 0, c: false, k: 91, ry: target.Yaw);
        Assert.True(back.Act(0, "deliver").Ok, back.Reply.Message);
        Assert.Equal("fura:2", Assert.Single(back.Awards).Reason);

        // а Житомир свій підсумок не загубив
        var zh2 = Table("zhytomyr", services: services);
        Assert.Equal("done", Phase(zh2));
        Assert.Equal(2, zh2.View(0).GetProperty("trips").GetInt32());
    }

    [Fact]
    public void An_old_record_without_maps_is_kray_and_survives_a_zhytomyr_table()
    {
        // запис 0.0.6: стан краю зверху, без maps
        var h = Table(null);
        Assert.True(h.Act(0, "take", new { id = 1 }).Ok);
        var old = h.Store.LoadState(FuraGarage.Key("оля"))!;
        Assert.DoesNotContain("\"maps\"", old);

        var store = new FakeStore();
        store.SaveState(FuraGarage.Key("оля"), old);
        var zh = Table("zhytomyr", services: Services(store));
        Assert.Equal("free", Phase(zh));
        Assert.True(zh.Act(0, "take", new { id = 3 }).Ok);
        Assert.True(zh.Leave("Оля").Ok);

        var record = JsonNode.Parse(store.LoadState(FuraGarage.Key("оля"))!)!.AsObject();
        Assert.Equal("pickup", (string?)record["phase"]);                    // край — як був
        Assert.Equal(3, (int)record["maps"]!["zhytomyr"]!["order"]!["id"]!);
        var kray = JsonNode.Parse(old)!.AsObject();
        foreach (var (key, value) in kray)
            Assert.True(JsonNode.DeepEquals(value, record[key]), key);

        // і читається як 0.0.6 (відкат): поля краю зверху
        var asOld = JsonSerializer.Deserialize<FuraRig>(record.ToJsonString(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("pickup", asOld.Phase);
    }

    [Fact]
    public void Old_stage_one_solo_record_migrates_into_a_zhytomyr_table_too()
    {
        // ні fura:rig, лише соло-запис етапу 1 — його рейси й рейс краю переходять у новий запис
        var h = Table(null);
        Assert.True(h.Act(0, "take", new { id = 1 }).Ok);
        var old = JsonNode.Parse(h.Store.LoadState(FuraGarage.Key("оля"))!)!.AsObject();
        old["trips"] = 7;
        var store = new FakeStore();
        store.SaveState(FuraGarage.OldKey("оля"), old.ToJsonString());
        var zh = Table("zhytomyr", services: Services(store));
        Assert.Equal(7, zh.View(0).GetProperty("trips").GetInt32());
        Assert.Equal("free", Phase(zh));
        var record = JsonNode.Parse(store.LoadState(FuraGarage.Key("оля"))!)!.AsObject();
        Assert.Equal("pickup", (string?)record["phase"]);
        Assert.Equal(7, (int)record["trips"]!);
    }

    [Fact]
    public void A_broken_zhytomyr_state_starts_clean_and_keeps_kray()
    {
        var store = new FakeStore();
        store.SaveState(FuraGarage.Key("оля"),
            "{\"v\":2,\"phase\":\"pickup\",\"trips\":4,\"order\":{\"id\":1,\"cargo\":\"jugs\",\"from\":\"honchar\",\"dock\":\"1\",\"to\":\"dvir\",\"km\":1,\"minutes\":5,\"base\":20},"
            + "\"maps\":{\"zhytomyr\":{\"phase\":7},\"odesa\":{\"phase\":\"haul\"}}}");
        var zh = Table("zhytomyr", services: Services(store));
        Assert.Equal("free", Phase(zh));
        Assert.Equal(4, zh.View(0).GetProperty("trips").GetInt32());
        Assert.True(zh.Act(0, "take", new { id = 1 }).Ok);
        var record = JsonNode.Parse(store.LoadState(FuraGarage.Key("оля"))!)!.AsObject();
        Assert.Equal("pickup", (string?)record["phase"]);
        Assert.Equal("honchar", (string?)record["order"]!["from"]);
        Assert.Equal("haul", (string?)record["maps"]!["odesa"]!["phase"]);   // чужу мапу не чіпаємо
        Assert.Equal("pickup", (string?)record["maps"]!["zhytomyr"]!["phase"]);
    }

    [Fact]
    public void Load_of_Save_on_zhytomyr_keeps_the_kray_trip()
    {
        var store = new FakeStore();
        var services = Services(store);
        var kray = Table(null, services: services);
        Assert.True(kray.Act(0, "take", new { id = 1 }).Ok);
        Assert.True(kray.Leave("Оля").Ok);

        var zh = Table("zhytomyr", services: services);
        Assert.True(zh.Act(0, "take", new { id = 2 }).Ok);
        var before = zh.View(0).GetRawText();
        lock (zh.Room.Sync) zh.Room.Game.Load(zh.Room.Game.Save()!);
        Assert.Equal(before, zh.View(0).GetRawText());
        Assert.True(zh.Leave("Оля").Ok);
        var record = JsonNode.Parse(store.LoadState(FuraGarage.Key("оля"))!)!.AsObject();
        Assert.Equal("pickup", (string?)record["phase"]);
        Assert.Equal(1, (int)record["order"]!["id"]!);
        Assert.Equal(2, (int)record["maps"]!["zhytomyr"]!["order"]!["id"]!);
    }

    [Fact]
    public void Garage_and_xp_are_shared_between_maps()
    {
        var store = new FakeStore();
        var services = Services(store);
        var zh = Table("zhytomyr", services: services);
        HaulOn(Zh, zh, 1);
        Assert.True(zh.Act(0, "deliver").Ok, zh.Reply.Message);
        var xp = zh.View(0).GetProperty("career").GetProperty("xp").GetInt32();
        Assert.True(xp > 0);
        Assert.True(zh.Leave("Оля").Ok);
        var kray = Table(null, services: services);
        Assert.Equal(xp, kray.View(0).GetProperty("career").GetProperty("xp").GetInt32());
    }
}
