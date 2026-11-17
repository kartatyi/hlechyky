using System.Net;
using System.Text;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Здам хату», оренда з OLX — внутрішня гра кола друзів: розбір відповіді (беремо лише місто, ціну, фото, посилання й
/// сухі факти), відбір, набір по кнопці, тиждень життя, і головне — хто це бачить: лише ті, хто сидить за столом, і лише
/// коли за столом усі з акаунтами. Мережі тут нема: відповіді складені за формою справжніх.
/// </summary>
public class KhataOlxTests
{
    const int MaxTicks = 600;

    static string Dir() => Path.Combine(Path.GetTempPath(), "khata-olx-" + Guid.NewGuid().ToString("n")[..8]);

    /// <summary>Одне оголошення у формі відповіді OLX. Текстові поля (заголовок, опис, продавець) є — щоб перевірити, що ми їх не беремо.</summary>
    static string Offer(long id, string city = "Харків", string price = """{ "value": 9000, "type": "price", "currency": "UAH", "converted_value": null, "converted_currency": null }""",
        bool business = false, int photos = 3, string status = "active", int category = 1760, string? url = null, string extra = "") => $$"""
        {
          "id": {{id}}, "url": "{{url ?? $"https://www.olx.ua/d/uk/obyavlenie/orenda-kvartiri-ID{id}.html"}}", "title": "Здам квартиру, власник", "description": "Довгий опис із телефоном 0501234567",
          "status": "{{status}}", "business": {{(business ? "true" : "false")}}, "category": { "id": {{category}}, "type": "real_estate" },
          "user": { "id": 77, "name": "Марія" }, "contact": { "name": "Марія", "phone": true },
          "location": { "city": { "id": 280, "name": "{{city}}" }, "region": { "id": 8, "name": "Харківська область" } },
          "params": [
            { "key": "price", "name": "Ціна", "value": {{price}} },
            { "key": "floor", "name": "Поверх", "value": { "key": "4", "label": "4" } },
            { "key": "total_floors", "name": "Поверховість", "value": { "key": "9", "label": "9" } },
            { "key": "total_area", "name": "Загальна площа", "value": { "key": "52", "label": "52 м²" } },
            { "key": "number_of_rooms_string", "name": "Кількість кімнат", "value": { "key": "dvuhkomnatnye", "label": "2 кімнати" } },
            { "key": "repair", "name": "Ремонт", "value": { "key": "3", "label": "Житловий стан" } }{{extra}}
          ],
          "photos": [{{string.Join(", ", Enumerable.Range(1, photos).Select(i => $$"""{ "id": {{i}}, "filename": "f{{id}}x{{i}}-UA", "width": 1000, "height": 750, "link": "https://ireland.apollo.olxcdn.com:443/v1/files/f{{id}}x{{i}}-UA/image;s={width}x{height}" }"""))}}]
        }
        """;

    static string Page(params string[] offers) => "{ \"data\": [" + string.Join(", ", offers) + "], \"metadata\": { \"total_elements\": 1000 } }";

    [Fact]
    public void Requests_ask_one_page_of_long_term_rent_per_region()
    {
        var fresh = KhataOlx.Url(8, KhataOlx.Page.Fresh);
        Assert.StartsWith("https://www.olx.ua/api/v1/offers/?", fresh);
        foreach (var part in new[] { "category_id=1760", "region_id=8", "limit=40", "offset=0", "currency=UAH", "sort_by=created_at%3Adesc" })
            Assert.Contains(part, fresh);
        var cheap = KhataOlx.Url(8, KhataOlx.Page.Cheap);
        Assert.Contains("sort_by=filter_float_price%3Aasc", cheap);
        Assert.Contains("filter_float_price%3Afrom=2500", cheap);
        var dear = KhataOlx.Url(8, KhataOlx.Page.Dear);
        Assert.Contains("sort_by=filter_float_price%3Adesc", dear);
        Assert.Contains("filter_float_price%3Ato=400000", dear);
        // «хати мрії»: подобові будинки за темою — без області, сторінка на тему
        var day = KhataOlx.DayUrl(KhataOlx.Themes[0]);
        Assert.StartsWith("https://www.olx.ua/api/v1/offers/?", day);
        foreach (var part in new[] { "category_id=3710", "limit=40", "offset=0", "currency=UAH", "query=%D0%B7%D0%B0%D0%BC%D0%BE%D0%BA" })
            Assert.Contains(part, day);
        Assert.DoesNotContain("region_id", day);
        Assert.Equal(KhataOlx.Themes.Count + KhataOlx.Regions, KhataOlx.Steps);
    }

    /// <summary>Подобовий будинок у формі відповіді OLX (категорія 3710): тут ціна за добу, є область і тип будинку.</summary>
    static string DayOffer(long id, string title = "A-frame біля озера Синевир", string city = "Синевирська Поляна", string region = "Закарпатська область",
        string price = """{ "value": 3500, "type": "price", "currency": "UAH" }""", bool business = true, int category = 3710) => $$"""
        {
          "id": {{id}}, "url": "https://www.olx.ua/d/uk/obyavlenie/budinok-ID{{id}}.html", "title": "{{title}}", "description": "Опис із телефоном 0671112233",
          "status": "active", "business": {{(business ? "true" : "false")}}, "category": { "id": {{category}}, "type": "accommodation" },
          "user": { "id": 78, "name": "Stepan" },
          "location": { "city": { "id": 1, "name": "{{city}}" }, "region": { "id": 2, "name": "{{region}}" } },
          "params": [
            { "key": "price", "name": "Ціна", "value": {{price}} },
            { "key": "property_type_houses", "name": "Тип", "value": { "key": "cottage", "label": "Котедж" } },
            { "key": "total_area", "name": "Площа", "value": { "key": "45", "label": "45 м²" } },
            { "key": "number_of_rooms", "name": "Кімнат", "value": { "key": "1", "label": "1" } },
            { "key": "sleep_places", "name": "Спальних місць", "value": { "key": "2", "label": "2" } },
            { "key": "repair", "name": "Ремонт", "value": { "key": "5", "label": "Авторський проект" } }
          ],
          "photos": [{ "id": 1, "filename": "d{{id}}-UA", "link": "https://ireland.apollo.olxcdn.com:443/v1/files/d{{id}}-UA/image;s={width}x{height}" }]
        }
        """;

    static KhataOlx.Theme Lake => KhataOlx.Themes.Single(t => t.Query == "біля озера");

    [Fact]
    public void A_daily_house_is_a_dream_home_priced_per_day_with_its_region_and_no_title()
    {
        var flat = Assert.Single(KhataOlx.ParseDay(Page(DayOffer(1)), Lake)).Flat;
        Assert.Equal(("1", KhataRegions.Ua, "Синевирська Поляна, Закарпатська обл.", 3500, KhataFlat.Night, true),
            (flat.ExtId, flat.Region, flat.City, flat.Price, flat.Per, flat.Dream));
        Assert.Equal("котедж · 1 кімната · 45 м² · спальних місць: 2 · ремонт: авторський проект", flat.About);
        var kept = JsonSerializer.Serialize(flat);
        foreach (var secret in new[] { "A-frame", "Stepan", "0671112233" }) Assert.DoesNotContain(secret, kept);   // заголовок читаємо, але не беремо

        // тему має підтвердити заголовок: пошук OLX чіпляється й за опис
        Assert.Empty(KhataOlx.ParseDay(Page(DayOffer(2, title: "Будинок, неподалік замок")), Lake));
        Assert.Single(KhataOlx.ParseDay(Page(DayOffer(2, title: "ЗАМОК люкс класу")), KhataOlx.Themes[0]));
        // обласному центру область не дописуємо
        Assert.Equal("Київ", KhataOlx.ParseDay(Page(DayOffer(3, city: "Київ", region: "Київська область")), Lake)[0].Flat.City);
        Assert.Equal("Одеса", KhataOlx.ParseDay(Page(DayOffer(3, city: "Одеса", region: "Одеська область")), Lake)[0].Flat.City);
        Assert.Equal("Кременчук, Полтавська обл.", KhataOlx.ParseDay(Page(DayOffer(3, city: "Кременчук", region: "Полтавська область")), Lake)[0].Flat.City);
        // «договірна» з числом для подобових — теж ціна (так стоїть у кожному третьому)
        var arranged = """{ "value": 22000, "type": "arranged", "currency": "UAH" }""";
        Assert.Equal(22000, KhataOlx.ParseDay(Page(DayOffer(4, price: arranged)), Lake)[0].Flat.Price);
        // не та категорія; ціна «з людини» (котедж за 500 грн) — не ціна будинку
        Assert.Empty(KhataOlx.ParseDay(Page(DayOffer(5, category: 1760)), Lake));
        Assert.Empty(KhataOlx.ParseDay(Page(DayOffer(6, price: """{ "value": 500, "type": "price", "currency": "UAH" }""")), Lake));
        // і навпаки: подобовий будинок у місячну оренду квартир не пролазить
        Assert.Empty(KhataOlx.Parse(Page(DayOffer(7))));
    }

    [Fact]
    public void Dream_homes_come_from_every_theme_once_and_surprising_prices_are_marked()
    {
        KhataOlx.Offer D(int id, int uah) =>
            KhataOlx.ParseDay(Page(DayOffer(id, price: $$"""{ "value": {{uah}}, "type": "price", "currency": "UAH" }""")), Lake)[0];
        var lake = new[] { D(1, 3000), D(2, 3500), D(3, 4000), D(4, 1500), D(5, 50000) };
        var frame = new[] { D(3, 4000), D(6, 3600), D(7, 3400) };                        // «3» знайшлось в обох темах
        var picked = KhataOlx.PickDay([lake, frame], perTheme: 4, new Random(2), known: id => id == "2");
        Assert.Equal(["1", "3", "4", "5", "6", "7"], picked.Select(p => p.ExtId).Order());
        Assert.All(picked, p => Assert.True(p.Dream));
        // серединна ціна знайденого — 3 500 за добу: 1 500 — несподівано дешево, 50 000 — несподівано дорого
        Assert.Equal(["4", "5"], picked.Where(p => p.Odd).Select(p => p.ExtId).Order());
        Assert.Equal(2, KhataOlx.PickDay([lake, frame], perTheme: 1, new Random(2), _ => false).Count);
        Assert.Empty(KhataOlx.PickDay([], 4, new Random(1), _ => false));
    }

    [Fact]
    public void An_offer_gives_only_the_city_the_price_the_photos_the_link_and_dry_facts()
    {
        var offer = Assert.Single(KhataOlx.Parse(Page(Offer(101))));
        var flat = offer.Flat;
        Assert.False(offer.Business);
        Assert.Equal(("101", KhataRegions.Ua, "Харків", 9000, KhataFlat.Month), (flat.ExtId, flat.Region, flat.City, flat.Price, flat.Per));
        Assert.Equal("https://www.olx.ua/d/uk/obyavlenie/orenda-kvartiri-ID101.html", flat.Link);
        Assert.Equal("2 кімнати · 52 м² · 4 поверх із 9 · ремонт: житловий стан", flat.About);
        Assert.Equal(
        [
            "https://ireland.apollo.olxcdn.com/v1/files/f101x1-UA/image",
            "https://ireland.apollo.olxcdn.com/v1/files/f101x2-UA/image",
            "https://ireland.apollo.olxcdn.com/v1/files/f101x3-UA/image",
        ], flat.Photos);
        Assert.Equal(flat.Photos[0] + ";s=1000x1000", KhataSources.Show(KhataSources.Olx, flat.Photos[0]));
        Assert.Equal(flat.Photos[0] + ";s=320x320", KhataSources.Thumb(KhataSources.Olx, flat.Photos[0]));
        // ні заголовка, ні опису, ні імені, ні телефона ми з оголошення не виносимо
        var kept = JsonSerializer.Serialize(flat);
        foreach (var secret in new[] { "Марія", "0501234567", "власник", "Довгий опис" }) Assert.DoesNotContain(secret, kept);
    }

    [Fact]
    public void The_price_is_hryvnias_and_a_negotiable_one_is_no_price()
    {
        var usd = """{ "value": 600, "type": "price", "currency": "USD", "converted_value": 27315, "converted_currency": "UAH" }""";
        Assert.Equal(27315, KhataOlx.Parse(Page(Offer(1, price: usd)))[0].Flat.Price);
        var arranged = """{ "value": 25000, "type": "arranged", "currency": "UAH", "converted_value": null, "converted_currency": null }""";
        Assert.Empty(KhataOlx.Parse(Page(Offer(2, price: arranged))));
        var noRate = """{ "value": 600, "type": "price", "currency": "USD", "converted_value": null, "converted_currency": null }""";
        Assert.Empty(KhataOlx.Parse(Page(Offer(3, price: noRate))));
        Assert.Empty(KhataOlx.Parse(Page(Offer(4, price: """{ "value": 300, "type": "price", "currency": "UAH" }"""))));        // подобова
        Assert.Empty(KhataOlx.Parse(Page(Offer(5, price: """{ "value": 5000000, "type": "price", "currency": "UAH" }"""))));    // продаж
        // продаж, що заблукав у оренду: дорого й забагато за квадрат (тут 52 м² → 2 100 грн/м²) — не беремо; у Києві стеля вища
        var sale = """{ "value": 109260, "type": "price", "currency": "UAH" }""";
        Assert.Empty(KhataOlx.Parse(Page(Offer(7, price: sale))));
        Assert.Single(KhataOlx.Parse(Page(Offer(8, price: sale, city: "Київ"))));
        Assert.Single(KhataOlx.Parse(Page(Offer(9, price: """{ "value": 58000, "type": "price", "currency": "UAH" }"""))));   // до 60 000 квадратів не рахуємо
        // площа з дробом — по-людськи, а не «62.50 м²»
        var half = Offer(6, extra: """, { "key": "total_area", "name": "Загальна площа", "value": { "key": "62.5", "label": "62.50 м²" } }""").Replace("""{ "key": "total_area", "name": "Загальна площа", "value": { "key": "52", "label": "52 м²" } },""", "");
        Assert.Contains("62,5 м²", KhataOlx.Parse(Page(half))[0].Flat.About);
    }

    [Fact]
    public void What_is_not_a_live_rental_listing_with_photos_is_dropped()
    {
        Assert.Empty(KhataOlx.Parse(Page(Offer(1, status: "removed_by_user"))));
        Assert.Empty(KhataOlx.Parse(Page(Offer(2, category: 13))));
        Assert.Empty(KhataOlx.Parse(Page(Offer(3, photos: 0))));
        Assert.Empty(KhataOlx.Parse(Page(Offer(4, url: "https://evil.example/x"))));
        Assert.Empty(KhataOlx.Parse(Page(Offer(5, city: ""))));
        Assert.Empty(KhataOlx.Parse("""{ "error": { "status": 403 } }"""));
        Assert.Empty(KhataOlx.Parse("<html>blocked</html>"));
        Assert.Equal(KhataOlx.MaxPhotos, KhataOlx.Parse(Page(Offer(6, photos: 20)))[0].Flat.Photos.Count);
        Assert.Equal(["7", "9"], KhataOlx.Parse(Page(Offer(7), Offer(8, photos: 0), Offer(9))).Select(o => o.Flat.ExtId));
    }

    [Theory]
    [InlineData("https://ireland.apollo.olxcdn.com:443/v1/files/abc-UA/image;s={width}x{height}", "https://ireland.apollo.olxcdn.com/v1/files/abc-UA/image")]
    [InlineData("https://frankfurt.apollo.olxcdn.com/v1/files/a_b-1/image", "https://frankfurt.apollo.olxcdn.com/v1/files/a_b-1/image")]
    public void A_photo_link_loses_its_size_template(string link, string photo) => Assert.Equal(photo, KhataOlx.Photo(link));

    [Theory]
    [InlineData("")]
    [InlineData("http://ireland.apollo.olxcdn.com/v1/files/abc/image")]
    [InlineData("https://evil.example/v1/files/abc/image")]
    [InlineData("https://olxcdn.com.evil.example/v1/files/abc/image")]
    [InlineData("https://ireland.apollo.olxcdn.com/v1/files/../../etc/image")]
    public void A_strange_photo_link_is_not_taken(string link) => Assert.Null(KhataOlx.Photo(link));

    [Fact]
    public void Pick_takes_a_third_cheap_a_third_dear_the_rest_fresh_private_first_and_nothing_known()
    {
        KhataOlx.Offer O(int id, bool business) => KhataOlx.Parse(Page(Offer(id, business: business)))[0];
        var cheap = Enumerable.Range(1, 10).Select(i => O(i, business: i > 4)).ToList();          // 1–4 приватні
        var fresh = Enumerable.Range(11, 10).Select(i => O(i, business: i % 2 == 0)).ToList();     // непарні приватні
        var dear = Enumerable.Range(21, 10).Select(i => O(i, business: i > 22)).ToList();          // 21–22 приватні
        var picked = KhataOlx.Pick(fresh, cheap, dear, take: 9, new Random(3), known: id => id == "2");
        Assert.Equal(9, picked.Count);
        Assert.Equal(9, picked.Select(p => p.ExtId).Distinct().Count());
        Assert.DoesNotContain(picked, p => p.ExtId == "2");
        var ids = picked.Select(p => int.Parse(p.ExtId)).ToList();
        Assert.Equal([1, 3, 4], ids.Where(i => i <= 10).Order());                                 // третина — з дешевих, приватні першими
        var fromDear = ids.Where(i => i > 20).ToList();
        Assert.Equal(3, fromDear.Count);                                                          // третина — з найдорожчих
        Assert.Contains(21, fromDear);
        Assert.Contains(22, fromDear);
        var fromFresh = ids.Where(i => i is > 10 and <= 20).ToList();
        Assert.Equal(3, fromFresh.Count);
        Assert.All(fromFresh, i => Assert.True(i % 2 == 1));                                      // зі свіжих — теж приватні
        // свіжих і дорогих мало — добираємо дешевими
        Assert.Equal(6, KhataOlx.Pick(fresh.Take(1).ToList(), cheap, [], 6, new Random(1), _ => false).Count);
        Assert.Empty(KhataOlx.Pick([], [], [], 6, new Random(1), _ => false));
    }

    // ------------------------------------------------------------------------------------------
    // банк: тиждень життя
    // ------------------------------------------------------------------------------------------

    static KhataExternal Ext(string id, string city = "Харків") =>
        new(id, KhataRegions.Ua, city, 9000, KhataFlat.Month, [$"https://ireland.apollo.olxcdn.com/v1/files/p{id}-UA/image"],
            $"https://www.olx.ua/d/uk/obyavlenie/x-ID{id}.html", "2 кімнати · 52 м²");

    [Fact]
    public void Listings_live_a_week_and_leave_nothing_behind_but_a_mark()
    {
        using var temp = new TempDb();
        var clock = new FakeClock();
        var store = new KhataStore(temp.Db, Dir(), clock);
        store.Import(KhataSources.Olx, [Ext("1"), Ext("2")]);
        clock.AdvanceMs(3 * 24 * 3600 * 1000);
        store.Import(KhataSources.Olx, [Ext("3")]);
        store.Import(KhataSources.Airbnb, [new KhataExternal("a1", KhataRegions.Eu, "Рига, Латвія", 80, KhataFlat.Night,
            ["https://a0.muscache.com/im/pictures/a1-cover.jpg"], "https://www.airbnb.com/rooms/1", null)]);

        clock.AdvanceMs(5 * 24 * 3600 * 1000);                                   // першим двом — вісім днів, третьому — п'ять
        Assert.Equal(2, store.Expire(KhataSources.Olx, clock.UtcNow - KhataOlx.Life));
        Assert.Equal(["3"], store.Live.Where(f => f.Source == KhataSources.Olx).Select(f => f.ExtId));
        Assert.Single(store.Live, f => f.Source == KhataSources.Airbnb);          // чужого джерела строк не чіпає
        Assert.Equal(0, store.Expire(KhataSources.Olx, clock.UtcNow - KhataOlx.Life));
        Assert.Equal(0, store.Expire(KhataSources.Own, clock.UtcNow));

        // у базі від відпущених лишилась тільки позначка «вже було» — без міста, ціни, фото й посилання
        var rows = temp.Db.With(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT ext_id, city, price, photos, link, about, removed FROM khata_flats WHERE source = 'olx' ORDER BY ext_id";
            using var r = cmd.ExecuteReader();
            var list = new List<(string Ext, string City, long Price, string Photos, bool NoLink, bool NoAbout, long Removed)>();
            while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.IsDBNull(4), r.IsDBNull(5), r.GetInt64(6)));
            return list;
        });
        Assert.Equal([("1", "", 0L, "[]", true, true, 1L), ("2", "", 0L, "[]", true, true, 1L)], rows.Take(2));
        Assert.Equal(("3", "Харків", 0L), (rows[2].Ext, rows[2].City, rows[2].Removed));

        // і вдруге ті самі оголошення в гру не вертаються — навіть після рестарту
        var again = new KhataStore(temp.Db, Dir(), clock);
        Assert.Equal((0, 0, 2), again.Import(KhataSources.Olx, [Ext("1"), Ext("2")]));
        Assert.True(again.Knows(KhataSources.Olx, "1"));
    }

    // ------------------------------------------------------------------------------------------
    // набір по кнопці
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

    [Fact]
    public async Task The_button_walks_the_themes_and_the_regions_once_and_lets_the_old_go()
    {
        KhataImport.OlxGap = TimeSpan.Zero;
        var clock = new FakeClock();
        var store = new KhataStore(null, Dir(), clock);
        store.Import(KhataSources.Olx, [Ext("900")]);
        clock.AdvanceMs(8 * 24 * 3600 * 1000);

        var net = new FakeNet(url =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
            if (query["region_id"] is null)                                            // тема «хат мрії»: подобові будинки
                return query["query"] == "біля озера" ? Json(Page(DayOffer(701), DayOffer(702, title: "Будинок, неподалік замок"))) : Json(Page());
            var region = int.Parse(query["region_id"]!);
            var cheap = url.Contains("filter_float_price");
            return region switch
            {
                5 => Json(Page(Offer(cheap ? 501 : 502, city: "Львів"), Offer(cheap ? 503 : 504, city: "Львів"))),
                8 => Json(Page(Offer(cheap ? 801 : 802))),
                9 => new HttpResponseMessage(HttpStatusCode.InternalServerError),      // область «лежить» — ідемо далі
                _ => Json(Page()),
            };
        });
        var import = new KhataImport(store, new HttpClient(net), clock);
        Assert.Empty(net.Asked);                                                       // сам сервер на OLX не ходить — лише по кнопці
        await import.RunOlx(CancellationToken.None);

        Assert.Equal(KhataOlx.Themes.Count + KhataOlx.Regions * 3, net.Asked.Count);    // сторінка на тему й три на область
        Assert.Equal(["501", "502", "503", "504", "701", "801", "802"], store.Live.Select(f => f.ExtId).Order());     // «900» відпущено: йому вже вісім днів
        Assert.All(store.Live, f => Assert.Equal((KhataSources.Olx, "OLX", "₴"), (f.Source, f.Author, f.Unit)));
        var dream = Assert.Single(store.Live, f => f.Dream);                           // подобовий будинок біля озера — «хата мрії», ціна за добу
        Assert.Equal(("701", KhataFlat.Night), (dream.ExtId, dream.Per));
        Assert.All(store.Live.Where(f => !f.Dream), f => Assert.Equal(KhataFlat.Month, f.Per));
        Assert.False(import.Olx.Running);
        Assert.Equal((KhataOlx.Steps, 7), (import.Olx.Done, import.Olx.Added));

        await import.RunOlx(CancellationToken.None);                                   // удруге ті самі — нічого нового
        Assert.Equal(7, store.Live.Count);
        Assert.Equal(0, import.Olx.Added);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_refusal_stops_the_run_at_once(HttpStatusCode status)
    {
        KhataImport.OlxGap = TimeSpan.Zero;
        var net = new FakeNet(url => !url.Contains("region_id=") ? Json(Page()) : url.Contains("region_id=1&") ? Json(Page(Offer(11))) : new HttpResponseMessage(status));
        var store = new KhataStore(null, Dir(), new FakeClock());
        var import = new KhataImport(store, new HttpClient(net), new FakeClock());
        await import.RunOlx(CancellationToken.None);
        Assert.Equal(KhataOlx.Themes.Count + 3 + 1, net.Asked.Count);                  // теми, область 1 (три сторінки) і перша ж відмова — далі не стукаємо
        Assert.Single(store.Live);
        Assert.False(import.Olx.Running);
        Assert.Contains("відмовив", import.Olx.Message);
    }

    [Fact]
    public async Task A_refusal_on_the_dream_themes_keeps_what_was_found_and_stops()
    {
        KhataImport.OlxGap = TimeSpan.Zero;
        var castle = Uri.EscapeDataString("замок");
        var net = new FakeNet(url => url.Contains("query=" + castle) ? Json(Page(DayOffer(1, title: "Замок над річкою"))) : new HttpResponseMessage(HttpStatusCode.Forbidden));
        var store = new KhataStore(null, Dir(), new FakeClock());
        var import = new KhataImport(store, new HttpClient(net), new FakeClock());
        await import.RunOlx(CancellationToken.None);
        Assert.Equal(2, net.Asked.Count);                                              // перша тема й перша ж відмова
        Assert.True(Assert.Single(store.Live).Dream);
        Assert.Contains("відмовив", import.Olx.Message);
    }

    // ------------------------------------------------------------------------------------------
    // гра: хто це бачить
    // ------------------------------------------------------------------------------------------

    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;

    static void Until(RoomHarness h, string phase)
    {
        for (var i = 0; i < MaxTicks && h.Room.Status == RoomStatus.Playing && Phase(h) != phase; i++) h.Tick(1);
        Assert.Equal(phase, Phase(h));
    }

    static KhataStore Bank()
    {
        var store = new KhataStore(null, Dir(), new FakeClock());
        store.Import(KhataSources.Olx, [Ext("1", "Харків"), Ext("2", "Львів"), Ext("3", "Одеса")]);
        return store;
    }

    [Fact]
    public void Those_at_the_table_see_the_listing_and_a_spectator_does_not()
    {
        var store = Bank();
        var h = new RoomHarness("khata", options: new { flats = "3" }, services: RoomHarness.WithService(store), seed: 5);
        h.Join("Оля");
        h.Join("Петро");
        Assert.True(h.Start().Ok);
        Until(h, "ask");

        var seat = h.View(0);
        Assert.False(seat.GetProperty("closed").GetBoolean());
        Assert.Contains(seat.GetProperty("city").GetString(), new[] { "Харків", "Львів", "Одеса" });
        Assert.Equal(("₴", "month"), (seat.GetProperty("unit").GetString(), seat.GetProperty("per").GetString()));
        Assert.EndsWith(";s=1000x1000", seat.GetProperty("photos")[0].GetString());

        var watcher = h.View(null);
        Assert.True(watcher.GetProperty("closed").GetBoolean());
        Assert.Equal("", watcher.GetProperty("city").GetString());
        Assert.Equal(0, watcher.GetProperty("photos").GetArrayLength());
        Assert.DoesNotContain("olxcdn", watcher.GetRawText());
        Assert.DoesNotContain("olx.ua", watcher.GetRawText());

        Assert.True(h.Act(0, "answer", new { value = 9000 }).Ok);
        Assert.True(h.Act(1, "answer", new { value = 12000 }).Ok);
        h.Tick(1);
        var rev = h.View(0).GetProperty("reveal");
        Assert.Equal(("olx", "OLX", "2 кімнати · 52 м²"), (rev.GetProperty("source").GetString(), rev.GetProperty("by").GetString(), rev.GetProperty("about").GetString()));
        Assert.StartsWith("https://www.olx.ua/", rev.GetProperty("link").GetString());
        var seen = h.View(null);                                                       // глядачеві на розкритті — лише числа
        Assert.Equal(9000, seen.GetProperty("reveal").GetProperty("price").GetInt32());
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("reveal").GetProperty("link").ValueKind);
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("reveal").GetProperty("about").ValueKind);
        Assert.DoesNotContain("olx.ua", seen.GetRawText());

        Until(h, "done");
        Assert.StartsWith("https://ireland.apollo.olxcdn.com/", h.View(0).GetProperty("recap")[0].GetProperty("photo").GetString());
        var recap = h.View(null).GetProperty("recap")[0];
        Assert.Equal(JsonValueKind.Null, recap.GetProperty("photo").ValueKind);
        Assert.Equal(JsonValueKind.Null, recap.GetProperty("link").ValueKind);
        Assert.DoesNotContain("olxcdn", h.View(null).GetRawText());
        Assert.DoesNotContain("olx.ua", h.View(null).GetRawText());
    }

    [Fact]
    public void A_guest_at_the_table_keeps_the_listings_out_of_the_party()
    {
        var store = Bank();
        store.Import(KhataSources.Airbnb, [new KhataExternal("a1", KhataRegions.Eu, "Рига, Латвія", 80, KhataFlat.Night,
            ["https://a0.muscache.com/im/pictures/a1-cover.jpg"], "https://www.airbnb.com/rooms/1", null)]);

        var mixed = new RoomHarness("khata", options: new { flats = "5" }, services: RoomHarness.WithService(store), seed: 2);
        mixed.Join("Оля");
        mixed.Join("гість Вася");
        Assert.True(mixed.Start().Ok);
        Assert.Equal(1, mixed.View(0).GetProperty("of").GetInt32());                   // лишилась одна Рига — оголошень OLX у партії нема
        Until(mixed, "ask");
        Assert.Equal("Рига, Латвія", mixed.View(0).GetProperty("city").GetString());

        var own = new RoomHarness("khata", options: new { flats = "5" }, services: RoomHarness.WithService(store), seed: 2);
        own.Join("Оля");
        own.Join("Петро");
        own.Start();
        Assert.Equal(4, own.View(0).GetProperty("of").GetInt32());                     // усі з акаунтами — три з OLX і Рига
    }

    [Fact]
    public void In_dream_homes_every_third_flat_has_a_price_nobody_expects()
    {
        var store = new KhataStore(null, Dir(), new FakeClock());
        var flats = new List<KhataExternal>();
        for (var i = 0; i < 40; i++)
            flats.Add(new KhataExternal("d" + i, i % 2 == 0 ? KhataRegions.Eu : KhataRegions.World, "Місто мрії " + i, 100 + i, KhataFlat.Night,
                [$"https://a0.muscache.com/im/pictures/d{i}-cover.jpg"], "https://www.airbnb.com/rooms/" + i, null, Dream: true, Odd: i % 5 == 0));
        for (var i = 0; i < 40; i++)
            flats.Add(new KhataExternal("p" + i, KhataRegions.Eu, "Звичайне місто " + i, 80, KhataFlat.Night,
                [$"https://a0.muscache.com/im/pictures/p{i}-cover.jpg"], "https://www.airbnb.com/rooms/9" + i, null));
        store.Import(KhataSources.Airbnb, flats);
        Assert.Equal((80, 40), (store.CountFit(null), store.CountFit(null, dream: true)));

        const string Name = "Місто мрії ";
        foreach (var (count, odd) in new[] { (3, 1), (5, 2), (7, 2), (10, 3) })
            for (var seed = 1; seed <= 3; seed++)
            {
                var h = new RoomHarness("khata", options: new { flats = count.ToString(), pick = "dream" }, services: RoomHarness.WithService(store), seed: seed);
                h.Join("Оля");
                Assert.True(h.Start().Ok);
                var cities = new List<string>();
                for (var round = 0; round < count; round++)
                {
                    Until(h, "ask");
                    cities.Add(h.View(0).GetProperty("city").GetString()!);
                    Until(h, round < count - 1 ? "between" : "done");
                }
                Assert.All(cities, c => Assert.StartsWith(Name, c));                                // лише «мрії»
                Assert.Equal(odd, cities.Count(c => int.Parse(c[Name.Length..]) % 5 == 0));         // і кожна третя — з несподіваною ціною
            }

        // «мрій» із несподіваною ціною нема — партія однаково збирається; самі несподівані — теж
        var few = new KhataStore(null, Dir(), new FakeClock());
        few.Import(KhataSources.Airbnb, [.. flats.Where(f => f.Odd).Take(3)]);
        var all = new RoomHarness("khata", options: new { flats = "3", pick = "dream" }, services: RoomHarness.WithService(few), seed: 1);
        all.Join("Оля");
        Assert.True(all.Start().Ok);
        Assert.Equal(3, all.View(0).GetProperty("of").GetInt32());

        // а де «мрій» нема — чесно кажемо до старту
        var none = new RoomHarness("khata", options: new { pick = "dream", region = "ua" }, services: RoomHarness.WithService(store), seed: 1);
        none.Join("Оля");
        Assert.False(none.Start().Ok);
    }

    [Fact]
    public void Regions_take_turns_so_the_big_world_does_not_drown_ukraine()
    {
        var store = new KhataStore(null, Dir(), new FakeClock());
        var world = new List<KhataExternal>();
        for (var i = 0; i < 300; i++)
            world.Add(new KhataExternal("w" + i, i % 2 == 0 ? KhataRegions.Eu : KhataRegions.World, "Місто світу " + i, 100 + i, KhataFlat.Night,
                [$"https://a0.muscache.com/im/pictures/w{i}-cover.jpg"], "https://www.airbnb.com/rooms/" + i, null));
        store.Import(KhataSources.Airbnb, world);
        store.Import(KhataSources.Olx, [.. Enumerable.Range(0, 6).Select(i => Ext("u" + i, "Місто України " + i))]);

        for (var seed = 1; seed <= 6; seed++)
        {
            var h = new RoomHarness("khata", options: new { flats = "5" }, services: RoomHarness.WithService(store), seed: seed);
            h.Join("Оля");
            h.Start();
            var ua = 0;
            for (var round = 0; round < 5; round++)
            {
                Until(h, "ask");
                if (h.View(0).GetProperty("unit").GetString() == "₴") ua++;
                Until(h, round < 4 ? "between" : "done");
            }
            Assert.InRange(ua, 1, 2);       // шість українських проти трьохсот світових — а в партії з п'яти їх одна-дві
        }
    }
}
