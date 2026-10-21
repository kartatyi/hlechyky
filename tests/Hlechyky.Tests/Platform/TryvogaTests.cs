using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Platform;

/// <summary>«🚨 Тривога» (docs/tryvoga.md): розбір NEPTUN, «чи стосується мого району», застарілі дані, архів.</summary>
public sealed class TryvogaTests : IDisposable
{
    // Записи — у формі справжніх відповідей /api/v1/alerts і /api/v1/threats (21.10.2026), скорочені.
    const string Alerts = """
        {"version":1792543601,"updatedAt":"2026-10-21T00:46:41.063380986Z",
         "raions":[
          {"key":"житомирський","name":"Житомирський район","oblast":"Житомирська область","since":"2026-10-20T23:45:44.567936Z","level":"yellow","reasons":["Дронова загроза (жовтий рівень)"]},
          {"key":"бахмутський","name":"Бахмутський район","oblast":"Донецька область","since":"2026-10-20T17:30:10.49869Z","level":"red","reasons":["Ракетна загроза (червоний рівень)"]},
          {"key":"","name":"без ключа","oblast":"","since":"2026-10-20T17:30:10Z","level":"red"},
          {"key":"бучанський","name":"Бучанський район","oblast":"Київська область","since":"не дата","level":"yellow"}],
         "oblasts":[
          {"key":"луганська","name":"Луганська область","oblast":"Луганська область","since":"2022-04-04T16:45:00Z","level":"red"},
          {"key":"м. київ","name":"м. Київ","oblast":"м. Київ","since":"2026-10-20T23:38:42.553129Z","level":"yellow","reasons":["Дронова загроза (жовтий рівень)"]},
          {"key":"житомирська","name":"Житомирська область","oblast":"Житомирська область","since":"2026-10-21T00:10:00Z"}]}
        """;

    const string Threats = """
        {"epoch":"rt-5ea3e304","seq":12231,"serverTime":"2026-10-21T00:48:46.466Z","threats":[
          {"id":"trk_1","type":"uav","title":"БпЛА","region":"Чернігівська область","district":"","locality":"Гончарівське",
           "lat":51.749083905808526,"lon":31.665341285318966,"heading":223.271,"confidenceLevel":"medium","sourceCount":5,
           "updatedAt":"2026-10-21T00:36:15Z","explanationShort":"БпЛА курсом на Гончарівське. Підтверджень: 5.","status":"active",
           "uncertaintyKm":6,"positionQuality":"approx","lifecycle":"uncertain",
           "trail":[{"lat":51.50999582930899,"lon":31.305653308814737,"t":"2026-10-21T00:14:22Z"},
                    {"lat":51.5132666708445,"lon":31.310187884579875,"t":"2026-10-21T00:14:35Z"},
                    {"lat":51.51075322170994,"lon":31.30670333009787,"t":"2026-10-21T00:14:35Z"}]},
          {"id":"trk_2","type":"uav","region":"Одеська область","lat":46.48,"lon":30.73,"count":3,"areaOnly":true,"updatedAt":"2026-10-21T00:40:00Z"},
          {"id":"trk_3","type":"kab","region":"Донецька область","lat":48.5,"lon":37.9,"velocity":{"bearingDeg":250,"speedKmh":420},"advisory":true},
          {"id":"битий","type":"uav","lat":"51"}]}
        """;

    static readonly DateTimeOffset Now = new(2026, 10, 21, 0, 50, 0, TimeSpan.Zero);

    readonly string _dir = Path.Combine(Path.GetTempPath(), "hlechyky-tryvoga-" + Guid.NewGuid().ToString("N"));
    readonly FakeClock _clock = new() { UtcNow = Now };
    readonly FixedOptions<TryvogaOptions> _options;
    readonly TryvogaArchive _archive;
    readonly MemoryGameStore _store = new();

    public TryvogaTests()
    {
        _options = new FixedOptions<TryvogaOptions>(new TryvogaOptions { ArchivePath = Path.Combine(_dir, "tryvoga.db") });
        _archive = new TryvogaArchive(_options, NullLogger<TryvogaArchive>.Instance);
    }

    public void Dispose()
    {
        // лише свій пул: ClearAllPools закривав би з'єднання паралельних тестів (див. TempDb)
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _options.CurrentValue.ArchivePath }.ToString()))
            SqliteConnection.ClearPool(c);
        try { Directory.Delete(_dir, true); } catch (IOException) { /* хай лежить у temp */ }
    }

    Tryvoga Service() => new(_options, new NullHub(), _archive, _store, _clock, NullLogger<Tryvoga>.Instance);

    static AlertSet A() => Tryvoga.ParseAlerts(Alerts)!;
    static ThreatSet Th(string json = Threats) => Tryvoga.ParseThreats(json)!;

    // ---------- розбір ----------

    [Fact]
    public void Alerts_parse_raions_and_whole_units_and_skip_broken_rows()
    {
        var a = A();
        Assert.Equal(1792543601, a.Version);
        Assert.Equal(5, a.Rows.Count);                       // без ключа й без дати — геть
        var z = a.Rows.Single(r => r.Key == "житомирський");
        Assert.False(z.Whole);
        Assert.Equal("yellow", z.Level);
        Assert.Equal("Житомирська область", z.Oblast);
        Assert.Equal(["Дронова загроза (жовтий рівень)"], z.Reasons);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 23, 45, 44, 567, TimeSpan.Zero), z.Since, TimeSpan.FromMilliseconds(1));
        Assert.True(a.Rows.Single(r => r.Key == "м. київ").Whole);
        Assert.Equal("red", a.Rows.Single(r => r.Key == "житомирська").Level);   // без рівня — звичайна тривога, червона
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>502 Bad Gateway</html>")]
    [InlineData("{\"error\":\"rate\"}")]
    [InlineData("[]")]
    public void Not_an_alerts_answer_is_null(string body)
    {
        Assert.Null(Tryvoga.ParseAlerts(body));
        Assert.Null(Tryvoga.ParseThreats(body));
    }

    [Fact]
    public void Threats_parse_position_trail_and_flags()
    {
        var t = Th();
        Assert.Equal("rt-5ea3e304", t.Epoch);
        Assert.Equal(3, t.Items.Count);                      // «битий» без числових координат — геть
        var d = t.Items[0];
        Assert.Equal(("trk_1", "uav", "Гончарівське"), (d.Id, d.Type, d.Locality));
        Assert.Equal(223.271, d.Heading);
        Assert.Equal(("approx", 6.0, 5), (d.Quality, d.UncertaintyKm!.Value, d.Sources!.Value));
        Assert.Equal(3, d.Trail.Count);
        Assert.Contains("Гончарівське", d.Raw);              // кирилицею, а не escape-кодами
        Assert.DoesNotContain("trail", d.Raw);                // слід і так лягає окремими точками
        Assert.True(t.Items[1].AreaOnly);
        Assert.Equal(3, t.Items[1].Count);
        Assert.True(t.Items[2].Advisory);
        Assert.Equal(420, t.Items[2].SpeedKmh);
        Assert.Null(t.Items[2].UpdatedAt);
    }

    // ---------- мій район ----------

    [Fact]
    public void Status_is_own_raion_or_whole_oblast_or_kyiv()
    {
        var a = A();
        var z = Tryvoga.Status(a, "житомирський", "житомирська");
        Assert.True(z.On);
        Assert.Equal("red", z.Level);                        // жовта районна + червона обласна → червона
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 23, 45, 44, 567, TimeSpan.Zero), z.Since!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Contains("Дронова загроза (жовтий рівень)", z.Reasons);

        Assert.True(Tryvoga.Status(a, "бердичівський", "житомирська").On);    // уся область — і сусідній район теж
        Assert.True(Tryvoga.Status(a, "щастинський", "луганська").On);
        Assert.Equal("yellow", Tryvoga.Status(a, "м. київ", "м. київ").Level);
        Assert.False(Tryvoga.Status(a, "бучанський", "київська").On);         // запис з битою датою не рахується
        Assert.False(Tryvoga.Status(a, "львівський", "львівська").On);
        Assert.True(Tryvoga.Status(a, "бахмутський", null).On);                // район без області — лише свій запис
        Assert.False(Tryvoga.Status(null, "житомирський", "житомирська").On);
        // запис району не вмикає тривогу всій області: «житомирська» — не «житомирський»
        var onlyRaion = new AlertSet(1, null, [a.Rows.Single(r => r.Key == "житомирський")]);
        Assert.False(Tryvoga.Status(onlyRaion, "бердичівський", "житомирська").On);
    }

    [Fact]
    public void Without_a_poll_the_site_says_stale_not_quiet()
    {
        var t = Service();
        Assert.True(t.Stale(Now));
        var v = JsonSerializer.SerializeToElement(t.View("гість Вася", null, null));
        Assert.True(v.GetProperty("stale").GetBoolean());
        Assert.False(v.GetProperty("status").GetProperty("on").GetBoolean());

        t.Feed(A(), null);
        Assert.False(t.Stale(Now));
        _clock.UtcNow = Now.AddSeconds(179);
        Assert.False(t.Stale(_clock.UtcNow));
        _clock.UtcNow = Now.AddSeconds(181);
        Assert.True(t.Stale(_clock.UtcNow));
    }

    [Fact]
    public void View_answers_for_asked_region_then_account_then_default()
    {
        var t = Service();
        t.Feed(A(), Th());
        JsonElement V(string nick, string? k = null, string? o = null) => JsonSerializer.SerializeToElement(t.View(nick, k, o));

        var guest = V("гість Вася");                         // типовий — Житомир
        Assert.Equal("житомирський", guest.GetProperty("def").GetProperty("Key").GetString());
        Assert.True(guest.GetProperty("status").GetProperty("on").GetBoolean());
        Assert.Equal(JsonValueKind.Null, guest.GetProperty("my").ValueKind);
        Assert.Equal(5, guest.GetProperty("alerts").GetArrayLength());

        Assert.False(V("гість Вася", "львівський", "львівська").GetProperty("status").GetProperty("on").GetBoolean());
        Assert.True(V("гість Вася", "львівський", "<script>").GetProperty("status").GetProperty("on").GetBoolean());   // криве — типовий

        Assert.False(t.SetMine("гість Вася", "львівський", "львівська", "Львівський район"));
        Assert.True(t.SetMine("владік", "львівський", "львівська", "Львівський район"));
        var mine = V("Владік");
        Assert.Equal("Львівський район", mine.GetProperty("my").GetProperty("Name").GetString());
        Assert.False(mine.GetProperty("status").GetProperty("on").GetBoolean());
        Assert.False(t.SetMine("владік", "x'; DROP", "львівська", "?"));
        Assert.False(t.SetMine("владік", "львівський", "львівська", new string('я', 81)));
    }

    [Fact]
    public void Threats_view_is_compact_and_marks_stale()
    {
        var t = Service();
        var empty = JsonSerializer.SerializeToElement(t.ThreatsView());
        Assert.True(empty.GetProperty("stale").GetBoolean());
        t.Feed(null, Th());
        var v = JsonSerializer.SerializeToElement(t.ThreatsView());
        Assert.False(v.GetProperty("stale").GetBoolean());
        var d = v.GetProperty("threats")[0];
        Assert.Equal(51.7491, d.GetProperty("lat").GetDouble());
        Assert.Equal(3, d.GetProperty("trail").GetArrayLength());
        Assert.Equal(51.51, d.GetProperty("trail")[0][0].GetDouble());     // слід — від найстарішої точки
    }

    [Fact]
    public void Disabled_says_off()
    {
        _options.CurrentValue.Enabled = false;
        var t = Service();
        Assert.True(JsonSerializer.SerializeToElement(t.View("владік", null, null)).GetProperty("off").GetBoolean());
        Assert.True(JsonSerializer.SerializeToElement(t.ThreatsView()).GetProperty("off").GetBoolean());
    }

    // ---------- 🧭 Довкола мене: без GPS стоїть у місті району ----------

    /// <summary>
    /// «Довкола мене» (web/dovkola.js) без GPS рахує від обласного центру всередині мого району — «towns» у мапі. Міста мають
    /// лягати туди, де вони є насправді (та сама проєкція, що й «Де це?»), а середина Житомирського району — за
    /// ~14 км від Житомира: тому й беремо місто, а не середину.
    /// </summary>
    [Fact]
    public void Map_towns_land_on_real_cities_and_zhytomyr_is_far_from_its_raion_middle()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Paths.Resolve("web/static/tryvoga-map.json")));
        var towns = doc.RootElement.GetProperty("towns").EnumerateArray().ToList();
        Assert.True(towns.Count >= 24, $"міст {towns.Count}");
        (double, double) At(string name)
        {
            var t = towns.Single(x => x.GetProperty("name").GetString() == name);
            return GeoMap.Unproject(t.GetProperty("x").GetDouble(), t.GetProperty("y").GetDouble());
        }
        foreach (var (name, lat, lon) in new[] { ("Житомир", 50.2547, 28.6587), ("Київ", 50.4501, 30.5234), ("Вінниця", 49.2331, 28.4682), ("Рівне", 50.6199, 26.2516) })
        {
            var (la, lo) = At(name);
            Assert.True(GeoScore.Km(la, lo, lat, lon) < 3, $"{name}: {la:0.000}, {lo:0.000}");
        }
        var c = doc.RootElement.GetProperty("units").EnumerateArray().Single(u => u.GetProperty("k").GetString() == "житомирський").GetProperty("c");
        var (clat, clon) = GeoMap.Unproject(c[0].GetDouble(), c[1].GetDouble());
        var (zlat, zlon) = At("Житомир");
        Assert.True(GeoScore.Km(clat, clon, zlat, zlon) > 8);
    }

    // ---------- детальний шар мапи (OSM) ----------

    /// <summary>
    /// web/static/tryvoga-detail.json (data/geo/build-tryvoga-detail.py): усі обласні центри рівнем 1 і там, де вони є
    /// (тезка-смт — Миколаїв на Львівщині — рівнем 1 не стає), міста лягають у сітку тієї ж проєкції, є вода, річки й
    /// траси, атрибуція OSM, і файл легкий — його довантажують на телефоні.
    /// </summary>
    [Fact]
    public void Detail_layer_has_every_oblast_centre_once_in_its_place_and_stays_light()
    {
        var path = Paths.Resolve("web/static/tryvoga-detail.json");
        Assert.True(new FileInfo(path).Length < 400_000, $"{new FileInfo(path).Length} байт");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        Assert.Contains("OpenStreetMap", root.GetProperty("attr").GetString());
        var places = root.GetProperty("places").EnumerateArray().ToList();
        var capitals = places.Where(p => p.GetProperty("l").GetInt32() == 1).Select(p => p.GetProperty("n").GetString()).ToList();
        Assert.Equal(capitals.Count, capitals.Distinct().Count());
        Assert.True(capitals.Count >= 25, $"обласних центрів {capitals.Count}");
        Assert.True(places.Count > 200, $"міст {places.Count}");
        foreach (var p in places)
        {
            int x = p.GetProperty("x").GetInt32(), y = p.GetProperty("y").GetInt32();
            Assert.True(x is > 0 and < 4000 && y is > 0 and < 2730, $"{p.GetProperty("n").GetString()}: {x}, {y}");
        }
        var zh = places.Single(p => p.GetProperty("n").GetString() == "Житомир");
        var (la, lo) = GeoMap.Unproject(zh.GetProperty("x").GetDouble(), zh.GetProperty("y").GetDouble());
        Assert.True(GeoScore.Km(la, lo, 50.2547, 28.6587) < 3, $"Житомир: {la:0.000}, {lo:0.000}");
        Assert.Contains(root.GetProperty("rivers").EnumerateArray(), r => r.GetProperty("n").GetString() == "Дніпро");
        Assert.True(root.GetProperty("water").GetArrayLength() >= 5);
        Assert.True(root.GetProperty("roads").GetArrayLength() >= 20);
    }

    // ---------- архів ----------

    SqliteConnection Db()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _options.CurrentValue.ArchivePath, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    long Count(string sql)
    {
        using var c = Db();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    string? Str(string sql)
    {
        using var c = Db();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as string;
    }

    [Fact]
    public void Archive_keeps_each_point_once_and_closes_gone_targets()
    {
        _archive.SaveThreats(Th(), Now);
        Assert.Equal(3, Count("SELECT COUNT(*) FROM threats"));
        // trk_1: 3 точки сліду + поточна; trk_2 «лише область» — без точки; trk_3 без часу — поточна з часом знімка
        Assert.Equal(4, Count("SELECT COUNT(*) FROM threat_points WHERE uid = 'rt-5ea3e304/trk_1'"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM threat_points WHERE uid = 'rt-5ea3e304/trk_2'"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM threat_points WHERE uid = 'rt-5ea3e304/trk_3'"));
        Assert.Equal("2026-10-21 00:14:22.000", Str("SELECT first_at FROM threats WHERE id = 'trk_1'"));
        Assert.Equal("Чернігівська область", Str("SELECT first_region FROM threats WHERE id = 'trk_1'"));

        // той самий знімок ще раз — нічого нового; те саме місце з новим часом (ще одне підтвердження) — теж
        _archive.SaveThreats(Th(), Now.AddSeconds(10));
        Assert.Equal(5, Count("SELECT COUNT(*) FROM threat_points"));
        _archive.SaveThreats(Th(Threats.Replace("\"updatedAt\":\"2026-10-21T00:36:15Z\"", "\"updatedAt\":\"2026-10-21T00:36:45Z\"")), Now.AddSeconds(15));
        Assert.Equal(5, Count("SELECT COUNT(*) FROM threat_points"));
        Assert.Equal("2026-10-21 00:36:45.000", Str("SELECT last_at FROM threats WHERE id = 'trk_1'"));

        // trk_1 зрушив, trk_2 і trk_3 зникли
        var moved = Threats.Replace("\"lat\":51.749083905808526", "\"lat\":51.7").Replace("\"updatedAt\":\"2026-10-21T00:36:15Z\"", "\"updatedAt\":\"2026-10-21T00:37:15Z\"");
        var one = Th(moved) with { Items = [Th(moved).Items[0]] };
        _archive.SaveThreats(one, Now.AddSeconds(20));
        Assert.Equal(5, Count("SELECT COUNT(*) FROM threat_points WHERE uid = 'rt-5ea3e304/trk_1'"));
        Assert.Equal("2026-10-21 00:37:15.000", Str("SELECT last_at FROM threats WHERE id = 'trk_1'"));
        Assert.Null(Str("SELECT gone_at FROM threats WHERE id = 'trk_1'"));
        Assert.Equal("2026-10-21 00:50:20.000", Str("SELECT gone_at FROM threats WHERE id = 'trk_2'"));

        // повернувся — знову відкритий; перша поява не пересувається
        _archive.SaveThreats(Th(), Now.AddSeconds(30));
        Assert.Null(Str("SELECT gone_at FROM threats WHERE id = 'trk_2'"));
        Assert.Equal("2026-10-21 00:14:22.000", Str("SELECT first_at FROM threats WHERE id = 'trk_1'"));
        Assert.Equal("2026-10-21 00:37:15.000", Str("SELECT last_at FROM threats WHERE id = 'trk_1'"));   // старий знімок не відкочує
    }

    [Fact]
    public void Archive_records_alert_spells_and_level_changes()
    {
        var a = A();
        _archive.SaveAlerts(a, Now);
        _archive.SaveAlerts(a, Now.AddSeconds(15));
        Assert.Equal(5, Count("SELECT COUNT(*) FROM alerts"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM alerts WHERE until IS NOT NULL"));
        Assert.Equal("[\"Дронова загроза (жовтий рівень)\"]", Str("SELECT reasons FROM alerts WHERE key = 'житомирський'"));

        // у Житомирському жовта стала червоною, у Бахмутському відбій
        var z = a.Rows.Single(r => r.Key == "житомирський") with { Level = "red" };
        var next = a with { Rows = [.. a.Rows.Where(r => r.Key is not "житомирський" and not "бахмутський"), z] };
        _archive.SaveAlerts(next, Now.AddSeconds(30));
        Assert.Equal(6, Count("SELECT COUNT(*) FROM alerts"));
        Assert.Equal("2026-10-21 00:50:30.000", Str("SELECT until FROM alerts WHERE key = 'бахмутський'"));
        Assert.Equal("2026-10-21 00:50:30.000", Str("SELECT until FROM alerts WHERE key = 'житомирський' AND level = 'yellow'"));
        Assert.Null(Str("SELECT until FROM alerts WHERE key = 'житомирський' AND level = 'red'"));
        Assert.Equal(1, Count("SELECT whole FROM alerts WHERE key = 'м. київ'"));
    }

    sealed class NullHub : IHubContext<RadioHub>
    {
        public IHubClients Clients => new C();
        public IGroupManager Groups => throw new NotSupportedException();

        sealed class C : IHubClients
        {
            public IClientProxy All => new P();
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => All;
            public IClientProxy Client(string connectionId) => All;
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => All;
            public IClientProxy Group(string groupName) => All;
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => All;
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => All;
            public IClientProxy User(string userId) => All;
            public IClientProxy Users(IReadOnlyList<string> userIds) => All;
        }

        sealed class P : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default) => Task.CompletedTask;
        }
    }
}
