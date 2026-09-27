using Hlechyky.Games;
using Hlechyky.Games.Impl;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Де це?»: проєкція, мапа з Natural Earth, очки й відстань, банк місць (docs/games/specs/geo.md §5, §7, §8).
/// </summary>
public class GeoMapTests
{
    static readonly string MapPath = Paths.Resolve("web/games/geo-map.json");

    // ---------------------------------------------------------------------------------------
    // проєкція
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(50.4501, 30.5234, 1884, 681)]    // Київ
    [InlineData(49.8397, 24.0297, 496, 807)]     // Львів
    [InlineData(44.6166, 33.5254, 2569, 2612)]   // Севастополь
    [InlineData(48.5740, 39.3078, 3798, 1218)]   // Луганськ
    [InlineData(48.4319, 22.2005, 51, 1229)]     // Чоп
    [InlineData(49.9935, 36.2304, 3097, 803)]    // Харків
    public void Albers_forward_matches_the_six_control_points_within_one_unit(double lat, double lon, int x, int y)
    {
        var (px, py) = GeoMap.Project(lat, lon);
        Assert.InRange(px, x - 1, x + 1);
        Assert.InRange(py, y - 1, y + 1);
    }

    [Fact]
    public void Albers_round_trip_returns_within_two_hundred_metres_for_a_grid_of_points()
    {
        for (var lat = 44.0; lat <= 52.5; lat += 1)
            for (var lon = 22.0; lon <= 40.5; lon += 1)
            {
                var (x, y) = GeoMap.Project(lat, lon);
                var (blat, blon) = GeoMap.Unproject(x, y);
                // округлення до цілої клітинки — до пів одиниці по кожній осі, тобто ≤ 0,24 км
                Assert.True(GeoScore.Km(lat, lon, blat, blon) < 0.25, $"{lat},{lon}");
                var (ex, ey) = GeoMap.ProjectExact(lat, lon);
                var (elat, elon) = GeoMap.Unproject(ex, ey);
                Assert.True(Math.Abs(elat - lat) < 1e-9 && Math.Abs(elon - lon) < 1e-9, "точна зворотна");
            }
    }

    public static TheoryData<string> Banks => ["main", "starter"];

    static GeoBank BankOf(string which) => which == "main" ? GeoCache.Main : GeoCache.Starter;

    [Theory]
    [MemberData(nameof(Banks))]
    public void Every_bank_place_projects_inside_the_map_grid(string which)
    {
        var bank = BankOf(which);
        Assert.NotEmpty(bank.Places);
        foreach (var p in bank.Places)
        {
            var (x, y) = GeoMap.Project(p.Lat, p.Lon);
            Assert.True(GeoMap.Inside(x, y), $"{p.Id} {p.Name}: ({x}, {y})");
            Assert.InRange(x, 30, GeoMap.W - 30);
            Assert.InRange(y, 30, GeoMap.H - 30);
        }
    }

    // ---------------------------------------------------------------------------------------
    // мапа
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Map_file_is_under_150_kb_has_27_regions_and_all_arcs_are_referenced()
    {
        Assert.True(File.Exists(MapPath), "мапу збирає data/geo/build-map.py");
        Assert.InRange(new FileInfo(MapPath).Length, 10_000, 150 * 1024);
        var map = GeoMapFile.Load(MapPath)!;
        Assert.Equal(27, map.Regions.Count);
        Assert.All(map.RefCount, n => Assert.InRange(n, 1, 2));
        Assert.Contains(map.RefCount, n => n == 1);      // контур
        Assert.Contains(map.RefCount, n => n == 2);      // межі областей
        Assert.True(map.Rivers >= 6, "Дніпро та інші річки");
        Assert.Equal(26, map.Cities);
        Assert.All(map.Arcs, a => Assert.True(a.Length >= 4 && a.Length % 2 == 0));
        Assert.All(map.Arcs.SelectMany(a => a), v => Assert.InRange(v, 0, GeoMap.W));
    }

    [Fact]
    public void Every_ring_closes_on_itself()
    {
        var map = GeoMapFile.Load(MapPath)!;
        foreach (var r in map.Regions)
            foreach (var ring in r.Rings)
            {
                Assert.True(ring.Length >= 8, $"{r.Id}: кільце з {ring.Length / 2} точок");
                Assert.Equal(ring[0], ring[^2]);
                Assert.Equal(ring[1], ring[^1]);
            }
    }

    [Fact]
    public void Regions_are_the_27_units_of_ukraine_with_crimea_and_sevastopol_and_ukrainian_names()
    {
        var map = GeoMapFile.Load(MapPath)!;
        var ids = map.Regions.Select(r => r.Id).ToHashSet();
        Assert.Equal(27, ids.Count);
        Assert.Contains("UA-43", ids);
        Assert.Contains("UA-40", ids);
        Assert.Contains("UA-30", ids);
        Assert.Equal("Автономна Республіка Крим", map.Regions.Single(r => r.Id == "UA-43").Name);
        Assert.Equal("Київська область", map.Regions.Single(r => r.Id == "UA-32").Name);
        Assert.All(map.Regions, r => Assert.Matches("^[А-ЯІЇЄҐа-яіїєґ' -]+$", r.Name));
    }

    [Theory]
    [InlineData(50.4501, 30.5234, "UA-30")]     // Київ
    [InlineData(44.6166, 33.5254, "UA-40")]     // Севастополь
    [InlineData(44.9484, 34.1000, "UA-43")]     // Сімферополь
    [InlineData(49.8397, 24.0297, "UA-46")]     // Львів
    [InlineData(50.2000, 30.2000, "UA-32")]     // Київська область поза містом
    [InlineData(49.9935, 36.2304, "UA-63")]     // Харків
    public void Region_at_known_points_is_right(double lat, double lon, string id)
    {
        var (x, y) = GeoMap.Project(lat, lon);
        Assert.Equal(id, GeoMap.RegionAt(x, y));
    }

    [Fact]
    public void Region_at_the_sea_and_abroad_is_null()
    {
        var (sx, sy) = GeoMap.Project(43.5, 34.0);     // Чорне море
        Assert.Null(GeoMap.RegionAt(sx, sy));
        var (mx, my) = GeoMap.Project(47.0, 28.8);     // Кишинів
        Assert.Null(GeoMap.RegionAt(mx, my));
    }

    /// <summary>
    /// Кожна точка банку лягає на суходіл України (а не в море чи за кордон) — і саме в ту область, яку банк
    /// називає. Для головного банку (~250 місць) це заразом перевірка, що збирачі не переплутали lat/lon.
    /// </summary>
    [Theory]
    [MemberData(nameof(Banks))]
    public void Every_bank_place_lies_on_ukrainian_land_in_the_region_the_bank_names(string which)
    {
        var map = GeoMapFile.Load(MapPath)!;
        var byName = map.Regions.ToDictionary(r => r.Name, r => r.Id);
        var bank = BankOf(which);
        Assert.NotEmpty(bank.Places);
        foreach (var p in bank.Places)
        {
            Assert.True(byName.TryGetValue(p.Region, out var want), $"{p.Id}: невідома область «{p.Region}»");
            var (x, y) = GeoMap.ProjectExact(p.Lat, p.Lon);
            // Мапа спрощена до ~0,7 км, а деякі місця стоять на самій межі (Говерла, Пікуй — на гребені між двома
            // областями), тож досить, щоб названа область була в радіусі п’яти кілометрів.
            Assert.True(Touches(map, want, x, y, 15), $"{p.Id} {p.Name}: {want}, а мапа каже {map.RegionAt(x, y)}");
        }
    }

    static bool Touches(GeoMapFile map, string id, double x, double y, double radius)
    {
        if (map.RegionAt(x, y) == id) return true;
        for (var r = 1.0; r <= radius; r += 1)
            for (var a = 0; a < 24; a++)
                if (map.RegionAt(x + r * Math.Cos(a * Math.PI / 12), y + r * Math.Sin(a * Math.PI / 12)) == id) return true;
        return false;
    }

    [Fact]
    public void Cities_lie_on_the_projection_and_inside_their_country()
    {
        var map = GeoMapFile.Load(MapPath)!;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(MapPath));
        var cities = doc.RootElement.GetProperty("cities").EnumerateArray().ToList();
        var kyiv = cities.Single(c => c.GetProperty("name").GetString() == "Київ");
        var (kx, ky) = GeoMap.Project(50.4500, 30.5236);
        Assert.Equal(kx, kyiv.GetProperty("x").GetInt32());
        Assert.Equal(ky, kyiv.GetProperty("y").GetInt32());
        Assert.Equal(7, cities.Count(c => c.GetProperty("lvl").GetInt32() == 1));
        foreach (var c in cities)
            Assert.NotNull(map.RegionNear(c.GetProperty("x").GetInt32(), c.GetProperty("y").GetInt32(), 9));
    }

    // ---------------------------------------------------------------------------------------
    // відстань і очки
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 5000)]
    [InlineData(1, 5000)]
    [InlineData(5, 4890)]
    [InlineData(10, 4756)]
    [InlineData(20, 4499)]
    [InlineData(50, 3808)]
    [InlineData(100, 2885)]
    [InlineData(150, 2185)]
    [InlineData(200, 1655)]
    [InlineData(300, 950)]
    [InlineData(400, 545)]
    [InlineData(500, 313)]
    [InlineData(700, 103)]
    [InlineData(1000, 19)]
    [InlineData(1300, 4)]
    public void Points_follow_the_control_table(double km, int points) => Assert.Equal(points, GeoScore.Points(km));

    [Fact]
    public void Points_are_continuous_at_one_kilometre_and_never_negative()
    {
        Assert.Equal(5000, GeoScore.Points(1.0));
        Assert.Equal(5000, GeoScore.Points(1.0001));
        Assert.Equal(4986, GeoScore.Points(1.5));
        var last = 5000;
        for (var km = 0.0; km < 3000; km += 7.3)
        {
            var p = GeoScore.Points(km);
            Assert.InRange(p, 0, last);
            last = p;
        }
        Assert.Equal(0, GeoScore.Points(double.NaN));
        Assert.Equal(0, GeoScore.Points(5000));
    }

    [Fact]
    public void Haversine_kyiv_to_lviv_is_about_469_km()
    {
        Assert.InRange(GeoScore.Km(50.4501, 30.5234, 49.8397, 24.0297), 467, 471);
        Assert.Equal(0, GeoScore.Km(48.5, 31.5, 48.5, 31.5), 9);
        // одна одиниця сітки ≈ 0,335 км
        var (x, y) = GeoMap.Project(48.5, 31.5);
        var (lat, lon) = GeoMap.Unproject(x + 1, y);
        var (lat0, lon0) = GeoMap.Unproject(x, y);
        Assert.InRange(GeoScore.Km(lat0, lon0, lat, lon), 0.30, 0.37);
    }

    [Fact]
    public void Km_text_formats_under_one_under_ten_and_thousands()
    {
        Assert.Equal("менше кілометра", GeoText.Km(0.4));
        Assert.Equal("1,0 км", GeoText.Km(1));
        Assert.Equal("7,3 км", GeoText.Km(7.26));
        Assert.Equal("10 км", GeoText.Km(9.97));
        Assert.Equal("12 км", GeoText.Km(12.4));
        Assert.Equal("1 043 км", GeoText.Km(1043.2));
        Assert.Equal("21 340", GeoText.Num(21340));
        Assert.Equal("0", GeoText.Num(0));
    }

    // ---------------------------------------------------------------------------------------
    // банк
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Bank_reads_the_starter_file_with_19_valid_places()
    {
        var bank = GeoCache.Starter;
        Assert.Equal(19, bank.Places.Count);
        Assert.Empty(bank.Problems);
        Assert.Equal(19, bank.Places.Select(p => p.Id).Distinct().Count());
        Assert.All(bank.Places, p =>
        {
            Assert.NotEmpty(p.Photos);
            Assert.All(p.Photos, ph => Assert.True(GeoBank.GoodHost(ph.Url)));
            Assert.All(p.Photos, ph => Assert.False(string.IsNullOrWhiteSpace(ph.Author)));
        });
        var kam = bank.Find("g0001")!;
        Assert.Equal("Кам'янець-Подільська фортеця", kam.Name);
        Assert.Equal("castle", kam.Cat);
        Assert.Equal(2, bank.Find("g0016")!.Photos.Count);
    }

    /// <summary>
    /// Головний банк гри (підготували збирачі й куратор): усі записи валідні, id і фото не повторюються, кожна
    /// категорія й складність мають із чого вибрати навіть на столі з десятьма раундами, посилання — лише https.
    /// </summary>
    [Fact]
    public void Main_bank_has_250_valid_places_and_every_filter_has_enough_to_play()
    {
        var bank = GeoCache.Main;
        Assert.Empty(bank.Problems);
        Assert.InRange(bank.Places.Count, 250, 2000);
        Assert.Equal(bank.Places.Count, bank.Places.Select(p => p.Id).Distinct().Count());
        var photos = bank.Places.SelectMany(p => p.Photos).ToList();
        Assert.Equal(photos.Count, photos.Select(ph => ph.Url).Distinct().Count());
        Assert.Equal(photos.Count, photos.Select(ph => GeoPhotos.FileNameFor(ph.Url)).Distinct().Count());
        Assert.All(photos, ph =>
        {
            Assert.True(GeoBank.GoodHost(ph.Url), ph.Url);
            Assert.StartsWith("https://commons.wikimedia.org/", ph.Page);
            Assert.True(ph.LicenseUrl.Length == 0 || ph.LicenseUrl.StartsWith("https://", StringComparison.Ordinal), ph.LicenseUrl);
            Assert.False(string.IsNullOrWhiteSpace(ph.Author));
            Assert.DoesNotContain("<", ph.Author);
        });
        foreach (var cat in new[] { "city", "castle", "nature", "village" })
        {
            var inCat = bank.Places.Where(p => p.Cat == cat).ToList();
            Assert.True(inCat.Count >= 40, $"{cat}: {inCat.Count}");
            Assert.True(inCat.Count(p => p.Difficulty == 1) >= 10, $"{cat}: легких мало");
            Assert.True(inCat.Count(p => p.Difficulty >= 2) >= 10, $"{cat}: складних мало");
        }
    }

    [Fact]
    public void Bank_drops_records_with_bad_cat_coordinates_or_foreign_photo_hosts_and_keeps_the_rest()
    {
        const string photo = """{"title":"File:a.jpg","url":"https://upload.wikimedia.org/a.jpg","author":"Я","license":"CC0","licenseUrl":"","page":"https://commons.wikimedia.org/wiki/File:a.jpg"}""";
        var json = $$"""
        { "version": 1, "places": [
          { "id": "ok1", "name": "Добре", "region": "Київ", "cat": "city", "lat": 50.45, "lon": 30.52, "difficulty": 1, "photos": [{{photo}}] },
          { "id": "bad-cat", "name": "Криве", "region": "Київ", "cat": "beach", "lat": 50.45, "lon": 30.52, "difficulty": 1, "photos": [{{photo}}] },
          { "id": "bad-lat", "name": "Криве", "region": "Київ", "cat": "city", "lat": 55.75, "lon": 37.61, "difficulty": 1, "photos": [{{photo}}] },
          { "id": "bad-host", "name": "Криве", "region": "Київ", "cat": "city", "lat": 50.45, "lon": 30.52, "difficulty": 1,
            "photos": [{"title":"File:b.jpg","url":"https://evil.example/b.jpg","author":"Я","license":"CC0"}] },
          { "id": "http", "name": "Криве", "region": "Київ", "cat": "city", "lat": 50.45, "lon": 30.52, "difficulty": 1,
            "photos": [{"title":"File:b.jpg","url":"http://upload.wikimedia.org/b.jpg","author":"Я","license":"CC0"}] },
          { "id": "bad-diff", "name": "Криве", "region": "Київ", "cat": "city", "lat": 50.45, "lon": 30.52, "difficulty": 4, "photos": [{{photo}}] },
          { "id": "no-name", "name": "", "region": "Київ", "cat": "city", "lat": 50.45, "lon": 30.52, "difficulty": 1, "photos": [{{photo}}] },
          { "id": "ok1", "name": "Повтор", "region": "Київ", "cat": "city", "lat": 50.45, "lon": 30.52, "difficulty": 1, "photos": [{{photo}}] },
          { "id": "mixed", "name": "Одне фото криве", "region": "Київ", "cat": "nature", "lat": 50.45, "lon": 30.52, "difficulty": 3,
            "photos": [{"title":"File:c.jpg","url":"https://evil.example/c.jpg","author":"Я","license":"CC0"}, {{photo}}] },
          42
        ] }
        """;
        var bank = GeoBank.Parse(json, null);
        Assert.Equal(["ok1", "mixed"], bank.Places.Select(p => p.Id));
        Assert.Single(bank.Find("mixed")!.Photos);
        Assert.Equal(8, bank.Problems.Count);
    }

    /// <summary>
    /// Рецензія: <c>page</c> і <c>licenseUrl</c> ідуть у <c>&lt;a href&gt;</c> під фото, а банк на ~250 місць
    /// готують інші агенти. Кривий запис лишається в грі (фото ж добре), але посилання з нього — порожні.
    /// </summary>
    [Theory]
    [InlineData("javascript:alert(1)", "javascript:alert(1)", "", "")]
    [InlineData("http://commons.wikimedia.org/wiki/File:a.jpg", "http://evil.example/l", "", "")]
    [InlineData("https://evil.example/wiki/File:a.jpg", "https://evil.example/l", "", "https://evil.example/l")]
    [InlineData("https://commons.wikimedia.org:8443/wiki/File:a.jpg", "data:text/html,hi", "", "")]
    [InlineData("//commons.wikimedia.org/wiki/File:a.jpg", "creativecommons.org/licenses/by/3.0", "", "")]
    [InlineData("https://commons.wikimedia.org/wiki/File:a.jpg", "http://creativecommons.org/licenses/by/3.0",
        "https://commons.wikimedia.org/wiki/File:a.jpg", "https://creativecommons.org/licenses/by/3.0")]
    [InlineData("https://commons.wikimedia.org/wiki/File:a.jpg", "",
        "https://commons.wikimedia.org/wiki/File:a.jpg", "")]
    public void Photo_links_are_https_only_and_the_page_lives_on_commons(string page, string license, string wantPage, string wantLicense)
    {
        var json = $$"""
        { "version": 1, "places": [
          { "id": "p1", "name": "Місце", "region": "Київ", "cat": "city", "lat": 50.45, "lon": 30.52, "difficulty": 1,
            "photos": [{"title":"File:a.jpg","url":"https://upload.wikimedia.org/a.jpg","author":"Я","license":"CC BY 3.0",
                        "licenseUrl":{{System.Text.Json.JsonSerializer.Serialize(license)}},"page":{{System.Text.Json.JsonSerializer.Serialize(page)}}}] }
        ] }
        """;
        var bank = GeoBank.Parse(json, null);
        var ph = Assert.Single(Assert.Single(bank.Places).Photos);
        Assert.Equal(wantPage, ph.Page);
        Assert.Equal(wantLicense, ph.LicenseUrl);
    }

    [Fact]
    public void Missing_or_broken_bank_file_gives_an_empty_bank_not_an_exception()
    {
        Assert.Empty(GeoBank.Load(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid() + ".json"), null).Places);
        Assert.Empty(GeoBank.Parse("{ це не json", null).Places);
        Assert.Empty(GeoBank.Parse("[]", null).Places);
        Assert.Empty(GeoBank.Parse("""{"places": 5}""", null).Places);
    }

    [Fact]
    public void Category_option_reads_all_and_unknown_as_every_category()
    {
        Assert.Equal(4, GeoCats.Parse("all").Count);
        Assert.Equal(4, GeoCats.Parse("").Count);
        Assert.Equal(4, GeoCats.Parse("beach").Count);
        Assert.Equal(4, GeoCats.Parse("city,all").Count);
        Assert.Equal(["city", "nature"], GeoCats.Parse("city,nature,beach").Order());
    }
}
