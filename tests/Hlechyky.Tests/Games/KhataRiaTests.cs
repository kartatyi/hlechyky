using System.Net;
using System.Text;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Здам хату», Україна з DIM.RIA: адреси запитів, розбір пошуку й «інформації по id» (за прикладом відповіді з
/// документації developers.ria.com), темп і відмови. Наживо це ще не ганялось — ключ реєструє власник сайту, тому тут
/// мережа підставна, а форма відповіді — з документації.
/// </summary>
public class KhataRiaTests
{
    static readonly KhataRia.City Kyiv = KhataRia.Cities.Single(c => c.Name == "Київ");
    static readonly Dictionary<string, double> Uah = new() { ["USD"] = 42, ["EUR"] = 46 };

    static string Dir() => Path.Combine(Path.GetTempPath(), "khata-ria-" + Guid.NewGuid().ToString("n")[..8]);

    /// <summary>Відповідь «інформація по id» — форма з документації, зміст — оренда.</summary>
    static string Info(long id = 13825265, int cityId = 10, string price = "\"price\": 18000, \"currency_type\": \"грн\"",
        string url = "realty-dolgosrochnaya-arenda-kvartira-kiev-goloseevskiy-13825265.html", string photos = """
            "photos": {
              "80704540": { "id": 80704540, "file": "dom/photo/8070/807045/80704540/80704540.jpg", "o": "h" },
              "80704539": { "id": 80704539, "file": "dom/photo/8070/807045/80704539/80704539.jpg", "o": "h" },
              "80704541": { "id": 80704541, "file": "dom/photo/8070/807045/80704541/80704541.jpg", "o": "h" }
            },
            """, string extra = "") => $$"""
        {
          "street_name": "Онуфрія Трутенка вулиця", "rooms_count": 2, "type": "realty", "state_name": "Київська", "state_id": 10,
          "beautiful_url": "{{url}}", "description": "Светлая квартира", {{price}}, "floor": 5, "floors_count": 9,
          "main_photo": "dom/photo/8070/807045/80704539/80704539.jpg", "realty_type_name": "Квартира", "city_name": "Киев",
          "realty_type_id": 2, "city_id": {{cityId}}, "total_square_meters": 54, {{photos}} {{extra}}
          "realty_id": {{id}}
        }
        """;

    [Fact]
    public void Requests_ask_for_long_term_flat_rent_in_one_city()
    {
        var search = KhataRia.SearchUrl(Kyiv, "my key");
        Assert.StartsWith("https://developers.ria.com/dom/search?", search);
        foreach (var part in new[] { "category=1", "realty_type=2", "operation_type=3", "state_id=10", "city_id=10", "page=0", "api_key=my%20key" })
            Assert.Contains(part, search);
        Assert.Equal("https://developers.ria.com/dom/info/13825265?api_key=k", KhataRia.InfoUrl(13825265, "k"));
        Assert.Equal(KhataRia.Cities.Count, KhataRia.Cities.Select(c => c.Id).Distinct().Count());
        Assert.Equal(KhataRia.Cities.Count, KhataRia.Cities.Select(c => c.Name).Distinct().Count());
    }

    [Fact]
    public void Search_gives_ids_and_survives_garbage()
    {
        Assert.Equal([101, 102, 103], KhataRia.Ids("""{ "count": 3, "items": [101, "102", 103, "x", -5] }"""));
        Assert.Empty(KhataRia.Ids("""{ "count": 0, "items": [] }"""));
        Assert.Empty(KhataRia.Ids("""{ "error": "limit" }"""));
        Assert.Empty(KhataRia.Ids("<html>502</html>"));
    }

    [Fact]
    public void A_listing_becomes_a_flat_with_all_its_photos()
    {
        var flat = KhataRia.Flat(Info(), Kyiv, Uah)!;
        Assert.Equal(("13825265", KhataRegions.Ua, "Київ", 18000, KhataFlat.Month), (flat.ExtId, flat.Region, flat.City, flat.Price, flat.Per));
        Assert.Equal("https://dom.ria.com/uk/realty-dolgosrochnaya-arenda-kvartira-kiev-goloseevskiy-13825265.html", flat.Link);
        Assert.Equal("2 кімнати · 54 м² · 5 поверх із 9", flat.About);
        // головне фото першим, решта за номером; «xl» перед розширенням — як велить документація
        Assert.Equal(
        [
            "https://cdn.riastatic.com/photos/dom/photo/8070/807045/80704539/80704539xl.jpg",
            "https://cdn.riastatic.com/photos/dom/photo/8070/807045/80704540/80704540xl.jpg",
            "https://cdn.riastatic.com/photos/dom/photo/8070/807045/80704541/80704541xl.jpg",
        ], flat.Photos);
    }

    [Theory]
    [InlineData("\"price\": 450, \"currency_type\": \"$\"", 18900)]                                  // долари — за курсом НБУ
    [InlineData("\"price\": 400, \"currency_type\": \"€\"", 18400)]
    [InlineData("\"price\": 450, \"currency_type\": \"$\", \"priceArr\": { \"1\": \"450\", \"2\": \"411\", \"3\": \"18 933\" }", 18933)]   // перерахунок RIA головніший
    [InlineData("\"price\": \"25 000\", \"currency_type\": \"грн\"", 25000)]
    public void The_price_is_always_in_hryvnias_per_month(string price, int uah) =>
        Assert.Equal(uah, KhataRia.Flat(Info(price: price), Kyiv, Uah)!.Price);

    [Fact]
    public void What_does_not_look_like_a_monthly_rent_in_that_city_is_dropped()
    {
        Assert.Null(KhataRia.Flat(Info(cityId: 5), Kyiv, Uah));                                              // інше місто — під «Київ» не піде
        Assert.Null(KhataRia.Flat(Info(price: "\"price\": 600, \"currency_type\": \"грн\""), Kyiv, Uah));    // подобова
        Assert.Null(KhataRia.Flat(Info(price: "\"price\": 9000000, \"currency_type\": \"грн\""), Kyiv, Uah)); // це вже продаж
        Assert.Null(KhataRia.Flat(Info(price: "\"price\": 500, \"currency_type\": \"zł\""), Kyiv, Uah));     // валюта, якої не знаємо
        Assert.Null(KhataRia.Flat(Info(price: "\"price\": 450, \"currency_type\": \"$\""), Kyiv, new Dictionary<string, double>()));   // курсу нема
        Assert.Null(KhataRia.Flat(Info(photos: "\"photos\": {},").Replace("\"main_photo\": \"dom/photo/8070/807045/80704539/80704539.jpg\",", ""), Kyiv, Uah));
        Assert.Null(KhataRia.Flat(Info(url: ""), Kyiv, Uah));
        Assert.Null(KhataRia.Flat(Info(url: "https://evil.example/x.html"), Kyiv, Uah));
        Assert.Null(KhataRia.Flat(Info(photos: "\"photos\": { \"1\": { \"id\": 1, \"file\": \"../../etc/passwd.jpg\" } },").Replace("\"main_photo\": \"dom/photo/8070/807045/80704539/80704539.jpg\",", ""), Kyiv, Uah));
        Assert.Null(KhataRia.Flat("""{ "error": "not found" }""", Kyiv, Uah));
        Assert.Null(KhataRia.Flat("не json", Kyiv, Uah));
        Assert.Null(KhataRia.Flat(Info(extra: "").Replace("\"realty_type_id\": 2", "\"realty_type_id\": 3"), Kyiv, Uah));   // кімната, не квартира
    }

    [Theory]
    [InlineData(1, "1 кімната")]
    [InlineData(2, "2 кімнати")]
    [InlineData(5, "5 кімнат")]
    [InlineData(11, "11 кімнат")]
    public void Rooms_are_counted_in_ukrainian(int n, string text) => Assert.Equal(text, KhataRia.Rooms(n));

    // ------------------------------------------------------------------------------------------
    // темп і відмови
    // ------------------------------------------------------------------------------------------

    sealed class FakeNet(Func<string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (Asked) Asked.Add(url);
            return Task.FromResult(answer(url));
        }
    }

    static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    static readonly KhataOptions Opts = new() { RiaKey = "k", RiaPerHour = 30 };

    [Fact]
    public async Task One_request_per_step_first_the_search_then_the_listings()
    {
        var first = KhataRia.Cities[0];
        var net = new FakeNet(url =>
            url.Contains("/dom/search") ? Json("""{ "count": 3, "items": [201, 202, 203] }""")
            : url.Contains("/dom/info/201") ? Json(Info(id: 201, cityId: first.Id))
            : url.Contains("/dom/info/202") ? Json(Info(id: 202, cityId: 999))            // чуже місто
            : url.Contains("/dom/info/203") ? Json(Info(id: 203, cityId: first.Id, photos: """
                "photos": { "9": { "id": 9, "file": "dom/photo/1/2/3/203.jpg" } },
                """))
            : url.Contains("bank.gov.ua") ? Json("""[{"cc":"USD","rate":42}]""")
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var store = new KhataStore(null, Dir(), new FakeClock());
        var import = new KhataImport(store, new HttpClient(net), new FakeClock());

        var gap = await import.RiaStep(Opts, CancellationToken.None);
        Assert.Equal(TimeSpan.FromMinutes(2), gap);                       // 30 на годину — запит раз на дві хвилини
        Assert.Single(net.Asked);
        Assert.Contains($"city_id={first.Id}", net.Asked[0]);
        Assert.Empty(store.Live);

        for (var i = 0; i < 3; i++) await import.RiaStep(Opts, CancellationToken.None);
        Assert.Equal(["201", "203"], store.Live.Select(f => f.ExtId).Order());
        Assert.All(store.Live, f => Assert.Equal((KhataSources.Ria, "DIM.RIA", first.Name, "₴", KhataFlat.Month), (f.Source, f.Author, f.City, f.Unit, f.Per)));
        Assert.Equal(4, net.Asked.Count(u => u.Contains("developers.ria.com")));     // пошук + три оголошення, жодного зайвого
        Assert.Equal(2, import.Ria.Added);
        Assert.True(import.Ria.HasKey);

        // черга порожня — наступний крок іде в наступне місто
        await import.RiaStep(Opts, CancellationToken.None);
        Assert.Contains($"city_id={KhataRia.Cities[1].Id}", net.Asked[^1]);
    }

    [Fact]
    public async Task Known_listings_cost_no_request_the_second_time_round()
    {
        var net = new FakeNet(url =>
            url.Contains("/dom/search") ? Json("""{ "count": 2, "items": [301, 302] }""")
            : url.Contains("/dom/info/301") ? Json(Info(id: 301, cityId: 10))
            : url.Contains("/dom/info/302") ? Json(Info(id: 302, cityId: 999))
            : Json("[]"));
        var store = new KhataStore(null, Dir(), new FakeClock());
        var import = new KhataImport(store, new HttpClient(net), new FakeClock());
        // повне коло по містах і ще раз: 301 уже в банку, 302 не згодилось — про обидва вдруге не питаємо
        var asked = 0;
        for (var i = 0; i < 200; i++)
        {
            await import.RiaStep(Opts, CancellationToken.None);
            asked = net.Asked.Count(u => u.Contains("/dom/info/"));
        }
        Assert.Equal(2, asked);
        Assert.Single(store.Live);
        Assert.True(store.Knows(KhataSources.Ria, "301"));
        Assert.False(store.Knows(KhataSources.Ria, "302"));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, 1)]
    [InlineData(HttpStatusCode.Unauthorized, 1)]
    [InlineData(HttpStatusCode.Forbidden, 6)]
    [InlineData(HttpStatusCode.PaymentRequired, 6)]
    public async Task A_refusal_means_a_long_pause_and_the_same_request_next_time(HttpStatusCode status, int hours)
    {
        var refuse = true;
        var net = new FakeNet(url => refuse ? new HttpResponseMessage(status)
            : url.Contains("/dom/search") ? Json("""{ "items": [401] }""") : Json(Info(id: 401, cityId: KhataRia.Cities[0].Id)));
        var store = new KhataStore(null, Dir(), new FakeClock());
        var import = new KhataImport(store, new HttpClient(net), new FakeClock());

        Assert.Equal(TimeSpan.FromHours(hours), await import.RiaStep(Opts, CancellationToken.None));
        Assert.False(string.IsNullOrEmpty(import.Ria.Message));
        refuse = false;
        await import.RiaStep(Opts, CancellationToken.None);
        Assert.Contains($"city_id={KhataRia.Cities[0].Id}", net.Asked[^1]);       // те саме місто, а не наступне

        refuse = true;                                                            // відмова посеред черги — оголошення не губиться
        Assert.Equal(TimeSpan.FromHours(hours), await import.RiaStep(Opts, CancellationToken.None));
        refuse = false;
        await import.RiaStep(Opts, CancellationToken.None);
        Assert.Equal(["401"], store.Live.Select(f => f.ExtId));
    }

    [Fact]
    public void Options_default_to_no_key_and_a_pace_under_the_free_limit()
    {
        var o = new KhataOptions();
        Assert.Equal("", o.RiaKey);
        Assert.InRange(o.RiaPerHour, 1, 30);
        Assert.Equal("DIM.RIA", KhataSources.Label(KhataSources.Ria));
        const string photo = "https://cdn.riastatic.com/photos/dom/photo/1/2/3/3xl.jpg";
        Assert.Equal(photo, KhataSources.Show(KhataSources.Ria, photo));
    }
}
