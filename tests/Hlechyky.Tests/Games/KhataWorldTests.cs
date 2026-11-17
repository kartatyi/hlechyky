using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Здам хату», справжні оголошення: розбір набору Inside Airbnb (сторінка з файлами, CSV, ціни, фото), відбір оголошень,
/// імпорт у банк (без дублів, зняте не воскресає), перевірка «чи фото ще живе» і гра з оголошеннями «за ніч».
/// Мережі тут нема: замість неї — підставний обробник запитів.
/// </summary>
public class KhataWorldTests
{
    const int MaxTicks = 600;

    static string Dir() => Path.Combine(Path.GetTempPath(), "khata-world-" + Guid.NewGuid().ToString("n")[..8]);

    static KhataExternal Ext(string id, string city = "Рига, Латвія", int price = 80, string region = KhataRegions.Eu,
        string? about = "«Затишна квартира» · квартира · 1 спальня") =>
        new(id, region, city, price, KhataFlat.Night, [$"https://a0.muscache.com/im/pictures/{id}-cover.jpg"],
            "https://www.airbnb.com/rooms/" + id, about);

    // ------------------------------------------------------------------------------------------
    // розбір набору
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void The_index_gives_the_freshest_file_of_every_city()
    {
        const string html = """
            <a href="https://data.insideairbnb.com/latvia/riga/riga/2026-06-25/data/listings.csv.gz">listings.csv.gz</a>
            <a href="https://data.insideairbnb.com/latvia/riga/riga/2026-09-25/data/listings.csv.gz">listings.csv.gz</a>
            <a href="https://data.insideairbnb.com/latvia/riga/riga/2026-09-25/visualisations/listings.csv">summary</a>
            <a href="https://data.insideairbnb.com/japan/kantō/tokyo/2026-09-25/data/listings.csv.gz">x</a>
            <a href="https://data.insideairbnb.com/ireland/2026-09-17/data/listings.csv.gz">x</a>
            <a href="https://data.insideairbnb.com/united-states/ny/new-york-city/2026-09-14/data/listings.csv.gz">x</a>
            <a href="https://data.insideairbnb.com/colombia/dc/bogot%C3%A1/2026-09-14/data/listings.csv.gz">x</a>
            """;
        var files = KhataAirbnb.Latest(html);
        Assert.Equal(["bogotá", "ireland", "new-york-city", "riga", "tokyo"], files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("https://data.insideairbnb.com/latvia/riga/riga/2026-09-25/data/listings.csv.gz", files["riga"]);
        Assert.Equal("https://data.insideairbnb.com/japan/kantō/tokyo/2026-09-25/data/listings.csv.gz", files["tokyo"]);
    }

    [Fact]
    public void Every_catalog_city_is_named_once_and_priced_in_a_real_currency()
    {
        Assert.Equal(KhataAirbnb.Cities.Count, KhataAirbnb.Cities.Select(c => c.Slug).Distinct().Count());
        Assert.Equal(KhataAirbnb.Cities.Count, KhataAirbnb.Cities.Select(c => c.Name).Distinct().Count());
        Assert.All(KhataAirbnb.Cities, c =>
        {
            Assert.True(c.Region is KhataRegions.Eu or KhataRegions.World, c.Slug);
            Assert.Equal(c.Name, KhataStore.CleanCity(c.Name));       // влазить у місто банку як є
            Assert.Matches("^[A-Z]{3}$", c.Currency);
            Assert.Equal(c.Slug, c.Slug.ToLowerInvariant());
        });
    }

    [Fact]
    public void Csv_reads_quotes_commas_and_line_breaks_inside_fields()
    {
        const string text = "id,name,description\r\n1,\"Loft, центр\",\"перший рядок\nдругий \"\"у лапках\"\"\"\r\n\r\n2,,x\n3,\"\",last";
        var rows = KhataAirbnb.Csv(new StringReader(text)).ToList();
        Assert.Equal(4, rows.Count);
        Assert.Equal(["id", "name", "description"], rows[0]);
        Assert.Equal(["1", "Loft, центр", "перший рядок\nдругий \"у лапках\""], rows[1]);
        Assert.Equal(["2", "", "x"], rows[2]);
        Assert.Equal(["3", "", "last"], rows[3]);
    }

    [Theory]
    [InlineData("$35.00", 35)]
    [InlineData("$1,338.49", 1338.49)]
    [InlineData("12000", 12000)]
    public void Price_is_read_from_the_dataset_form(string raw, double value) => Assert.Equal(value, KhataAirbnb.Price(raw));

    [Theory]
    [InlineData("")]
    [InlineData("$0.00")]
    [InlineData("n/a")]
    public void No_price_is_no_price(string raw) => Assert.Null(KhataAirbnb.Price(raw));

    [Theory]
    [InlineData("https://a0.muscache.com/pictures/hosting/Hosting-329426/original/6d9d.jpeg", "https://a0.muscache.com/im/pictures/hosting/Hosting-329426/original/6d9d.jpeg")]
    [InlineData("https://a0.muscache.com/pictures/a664ffa0-5d7f.jpg?aki_policy=large", "https://a0.muscache.com/im/pictures/a664ffa0-5d7f.jpg")]
    [InlineData("https://a0.muscache.com/im/pictures/miso/Hosting-1/original/x.jpeg?im_w=720", "https://a0.muscache.com/im/pictures/miso/Hosting-1/original/x.jpeg")]
    public void A_cover_becomes_the_address_of_its_small_copy(string raw, string photo)
    {
        Assert.Equal(photo, KhataAirbnb.Photo(raw));
        Assert.Equal(photo + "?im_w=960", KhataSources.Show(KhataSources.Airbnb, photo));
        Assert.Equal(photo + "?im_w=320", KhataSources.Thumb(KhataSources.Airbnb, photo));
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://a0.muscache.com/pictures/x-insecure.jpg")]
    [InlineData("https://evil.example/pictures/aaaaaaaa.jpg")]
    [InlineData("https://a0.muscache.com/user/1234.jpg")]
    [InlineData("https://a0.muscache.com/pictures/a b.jpg")]
    public void A_strange_cover_is_not_taken(string raw) => Assert.Null(KhataAirbnb.Photo(raw));

    [Fact]
    public void Rates_turn_into_dollars_per_unit()
    {
        const string json = """[{"r030":840,"txt":"Долар США","rate":42.0,"cc":"USD"},{"cc":"EUR","rate":46.2},{"cc":"JPY","rate":0.28},{"cc":"XAU","rate":0}]""";
        var usd = KhataAirbnb.UsdPer(json);
        Assert.Equal(1, usd["USD"]);
        Assert.Equal(1.1, usd["EUR"], 6);
        Assert.Equal(0.28 / 42, usd["JPY"], 9);
        Assert.False(usd.ContainsKey("XAU"));
        Assert.Empty(KhataAirbnb.UsdPer("""[{"cc":"EUR","rate":46.2}]"""));     // без долара перерахувати нічим
    }

    [Theory]
    [InlineData(0, "студія")]
    [InlineData(1, "1 спальня")]
    [InlineData(3, "3 спальні")]
    [InlineData(5, "5 спалень")]
    [InlineData(11, "11 спалень")]
    [InlineData(22, "22 спальні")]
    public void Bedrooms_are_counted_in_ukrainian(int n, string text) => Assert.Equal(text, KhataAirbnb.Bedrooms(n));

    const string Head = "id,listing_url,name,description,picture_url,property_type,room_type,bedrooms,price,number_of_reviews,review_scores_rating";

    static string Line(int id, string name, string type = "Entire rental unit", string room = "Entire home/apt", int beds = 1,
        string price = "$100.00", int reviews = 10, string rating = "4.8", string? picture = null) =>
        $"{id},https://www.airbnb.com/rooms/{id},\"{name}\",\"опис, з комою\nі переносом\",{picture ?? $"https://a0.muscache.com/pictures/hosting/Hosting-{id}/original/cover-{id}.jpeg"},{type},{room},{beds},\"{price}\",{reviews},{rating}";

    [Fact]
    public void Rows_keep_only_listings_with_a_photo_a_price_and_a_name()
    {
        var csv = string.Join('\n', Head,
            Line(1, "Гарна"),
            Line(2, "Без ціни", price: ""),
            Line(3, "Без фото", picture: ""),
            Line(4, ""),
            Line(5, "Чуже фото", picture: "https://example.com/x.jpg"),
            "abc,,,,,,,,,,",
            Line(6, "Замок", type: "Castle", beds: 4, price: "$1,250.50", rating: ""));
        var rows = KhataAirbnb.Rows(new StringReader(csv)).ToList();
        Assert.Equal(["1", "6"], rows.Select(r => r.Id));
        Assert.Equal(("Замок", "Castle", 4, 1250.5, 10), (rows[1].Name, rows[1].PropertyType, rows[1].Bedrooms, rows[1].Price, rows[1].Reviews));
        Assert.Null(rows[1].Rating);
        Assert.Equal("https://a0.muscache.com/im/pictures/hosting/Hosting-1/original/cover-1.jpeg", rows[0].Photo);
        Assert.Empty(KhataAirbnb.Rows(new StringReader("a,b,c\n1,2,3")));      // не той файл — нічого й не беремо
    }

    static readonly KhataAirbnb.City Riga = new("riga", "Рига, Латвія", KhataRegions.Eu, "EUR");

    static KhataAirbnb.Row Row(int id, double price, string type = "Entire rental unit", string room = "Entire home/apt",
        int reviews = 10, double? rating = 4.8, int beds = 1, string? name = null) =>
        new(id.ToString(), name ?? "Житло " + id, $"https://a0.muscache.com/im/pictures/p{id}xxxxxx.jpg", price, type, room, beds, reviews, rating);

    [Theory]
    [InlineData("Castle", "Château de la Loire with pool", true)]
    [InlineData("Castle", "Private House, Clean Room near BTS", false)]      // «замок» лише зі слів хазяїна
    [InlineData("Tower", "Torre medievale nel borgo", true)]
    [InlineData("Tower", "Glam Lux Penthouse, Rooftop Pool", false)]
    [InlineData("Lighthouse", "Cozy Studio in Los Angeles", false)]
    [InlineData("Treehouse", "Magical Tree House in the woods", true)]
    [InlineData("Houseboat", "SleepBoat - Cozy Modern Houseboat", true)]
    [InlineData("Boat", "Voilier Dufour 35 Classic", true)]
    [InlineData("Trullo", "Casa nel verde", true)]                             // місцевий тип — віримо й без назви
    [InlineData("Entire villa", "Castle view villa", false)]                   // не диво за типом
    public void A_wonder_is_trusted_only_when_the_title_agrees(string type, string name, bool wow) =>
        Assert.Equal(wow, KhataAirbnb.IsWow(type, name));

    [Fact]
    public void A_doubtful_wonder_is_not_called_by_its_type_on_the_reveal()
    {
        Assert.Equal("«Château du Lac» · замок · 4 спальні", KhataAirbnb.About(Row(1, 100, type: "Castle", beds: 4, name: "Château du Lac")));
        Assert.Equal("«Clean Room near BTS» · 6 спалень", KhataAirbnb.About(Row(2, 100, type: "Castle", beds: 6, name: "Clean Room near BTS")));
        Assert.Equal("«Щось» · студія", KhataAirbnb.About(Row(3, 100, type: "Невідомий тип", beds: 0, name: "Щось")));
    }

    [Fact]
    public void Pick_takes_whole_homes_with_reviews_and_a_sane_price_in_dollars()
    {
        var rows = new[]
        {
            Row(1, 100),
            Row(2, 100, room: "Private room"),
            Row(3, 100, reviews: 1),
            Row(4, 100, rating: 3.2),
            Row(5, 5),                 // 5,5 $ — заглушка
            Row(6, 9000),              // 9 900 $ за ніч — теж
            Row(7, 100, rating: null), // без оцінки, але з відгуками — годиться
        };
        var picked = KhataAirbnb.Pick(rows, Riga, usdPerUnit: 1.1, take: 60, new Random(1));
        Assert.Equal(["1", "7"], picked.Select(p => p.ExtId).Order());
        var one = picked.Single(p => p.ExtId == "1");
        Assert.Equal(("Рига, Латвія", KhataRegions.Eu, 110, KhataFlat.Night), (one.City, one.Region, one.Price, one.Per));
        Assert.Equal("https://www.airbnb.com/rooms/1", one.Link);
        Assert.Equal("«Житло 1» · квартира · 1 спальня", one.About);
        Assert.Equal(["https://a0.muscache.com/im/pictures/p1xxxxxx.jpg"], one.Photos);
    }

    [Fact]
    public void Pick_spreads_over_prices_and_keeps_a_place_for_wonders()
    {
        var rows = new List<KhataAirbnb.Row>();
        for (var i = 1; i <= 500; i++) rows.Add(Row(i, 20 + i));                                   // 21…520 — рівно по зростанню
        for (var i = 501; i <= 540; i++) rows.Add(Row(i, 300, type: i % 2 == 0 ? "Castle" : "Treehouse", beds: 5, name: i % 2 == 0 ? "Old Castle " + i : "Treehouse " + i));
        for (var i = 541; i <= 560; i++) rows.Add(Row(i, 9, type: "Castle", name: "Просто кімната " + i));      // «замки» лише зі слів хазяїна — не дива
        var picked = KhataAirbnb.Pick(rows, Riga, 1, take: 60, new Random(7));
        Assert.Equal(60, picked.Count);
        Assert.Equal(60, picked.Select(p => p.ExtId).Distinct().Count());
        var wonders = picked.Count(p => int.Parse(p.ExtId) is > 500 and <= 540);
        Assert.Equal(KhataAirbnb.DreamPerCity, wonders);
        Assert.DoesNotContain(picked, p => int.Parse(p.ExtId) > 540 && p.About!.Contains("замок"));     // сумнівний «замок» замком не звемо
        Assert.Contains(picked, p => p.About!.Contains("замок") || p.About.Contains("будиночок на дереві"));
        // звичайні — з усіх п'яти цінових смуг, а не лише з дешевих чи дорогих
        var plain = picked.Where(p => int.Parse(p.ExtId) <= 500).Select(p => p.Price).ToList();
        for (var band = 0; band < 5; band++)
            Assert.Contains(plain, usd => usd > 20 + band * 100 && usd <= 20 + (band + 1) * 100);
        // мало рядків — беремо, скільки є
        Assert.Equal(3, KhataAirbnb.Pick(rows.Take(3), Riga, 1, 60, new Random(1)).Count);
        Assert.Empty(KhataAirbnb.Pick([], Riga, 1, 60, new Random(1)));
    }

    [Theory]
    [InlineData("Gothic Revival loft by the cathedral", true)]
    [InlineData("Palazzo storico nel centro", true)]
    [InlineData("Appartement dans un château du XVIIe", true)]
    [InlineData("Medieval tower house", true)]
    [InlineData("Architect designed home", true)]
    [InlineData("17th century merchant house", true)]
    [InlineData("Cozy studio near metro", false)]
    [InlineData("Квартира біля вокзалу", false)]
    [InlineData("Modern Penthouse Close to the Palace", false)]      // палац — сусід, а не це житло
    [InlineData("Castle view apartment", false)]
    [InlineData("Historic Center Luxury flat", false)]               // «історичний центр» — це адреса
    [InlineData("Luxe 20th Floor Mercury Tower Apartment", false)]   // «Tower» — назва висотки
    public void Grand_architecture_is_read_from_the_title(string name, bool grand) => Assert.Equal(grand, KhataAirbnb.IsGrand(name));

    [Fact]
    public void A_dream_home_is_a_wonder_grand_architecture_the_sea_or_a_villa_with_a_pool()
    {
        Assert.True(KhataAirbnb.IsDream(Row(1, 100, type: "Castle", name: "Old Castle")));
        Assert.True(KhataAirbnb.IsDream(Row(2, 100, name: "Baroque palace apartment")));
        Assert.True(KhataAirbnb.IsDream(Row(3, 100) with { Perks = KhataAirbnb.Perk.Beach }));
        Assert.True(KhataAirbnb.IsDream(Row(4, 100) with { Perks = KhataAirbnb.Perk.SeaView }));
        Assert.True(KhataAirbnb.IsDream(Row(5, 100, type: "Entire villa") with { Perks = KhataAirbnb.Perk.Pool }));
        Assert.True(KhataAirbnb.IsDream(Row(6, 100, name: "Luxury penthouse") with { Perks = KhataAirbnb.Perk.Pool }));
        Assert.False(KhataAirbnb.IsDream(Row(7, 100) with { Perks = KhataAirbnb.Perk.Pool }));      // басейн у дворі багатоповерхівки — ще не мрія
        Assert.False(KhataAirbnb.IsDream(Row(8, 100, name: "Luxury penthouse")));                   // «люкс» без басейну — лише реклама
        Assert.False(KhataAirbnb.IsDream(Row(9, 100) with { Perks = KhataAirbnb.Perk.Water }));
        Assert.Equal(KhataAirbnb.Perk.Beach | KhataAirbnb.Perk.Pool, KhataAirbnb.PerksOf("[\"Beachfront\", \"Private pool\", \"Pool table\", \"Wifi\"]"));
        Assert.Equal(KhataAirbnb.Perk.None, KhataAirbnb.PerksOf("[\"Pool table\", \"Pool view\", \"Wifi\"]"));
        Assert.Equal(KhataAirbnb.Perk.None, KhataAirbnb.PerksOf("не json"));
        Assert.Equal("«Житло 3» · квартира · 1 спальня · біля пляжу · з басейном",
            KhataAirbnb.About(Row(3, 100) with { Perks = KhataAirbnb.Perk.Beach | KhataAirbnb.Perk.Pool }));
    }

    [Fact]
    public void Among_dream_homes_a_third_have_a_price_nobody_expects_and_the_rest_are_simply_the_best()
    {
        var rows = new List<KhataAirbnb.Row>();
        for (var i = 1; i <= 300; i++) rows.Add(Row(i, 100));                                               // звичайне житло: серединна ціна — 100
        for (var i = 301; i <= 340; i++) rows.Add(Row(i, 110, type: "Castle", name: "Old Castle " + i));    // замки за звичайну ціну
        for (var i = 341; i <= 350; i++) rows.Add(Row(i, 40, type: "Castle", name: "Old Castle " + i));     // замки задешево
        for (var i = 351; i <= 360; i++) rows.Add(Row(i, 900, type: "Castle", name: "Old Castle " + i));    // і задорого
        for (var i = 361; i <= 380; i++) rows.Add(Row(i, 35, name: "Gothic loft " + i));                    // готика задешево — «мрія» скромніша
        var picked = KhataAirbnb.Pick(rows, Riga, 1, take: 60, new Random(5));
        Assert.Equal(60, picked.Count);
        var dreams = picked.Where(p => p.Dream).ToList();
        Assert.Equal(KhataAirbnb.DreamPerCity, dreams.Count);
        Assert.All(picked.Where(p => !p.Dream), p => Assert.Equal(100, p.Price));
        Assert.All(picked, p => Assert.Equal(p.Price is <= 50 or >= 300, p.Odd));
        var odd = dreams.Where(p => p.Odd).ToList();
        Assert.True(odd.Count >= KhataAirbnb.DreamPerCity / KhataAirbnb.DreamOddShare);
        Assert.True(odd.Count(p => p.Price < 100) >= 4 && odd.Count(p => p.Price > 100) >= 4);              // і дешеві, і дорогі
        // цікавість важить більше за ціну: поки вистачає замків, дешева «готика» у квоту не йде
        Assert.DoesNotContain(dreams, p => p.About!.Contains("Gothic"));
    }

    [Theory]
    [InlineData(100, 100, false)]
    [InlineData(50, 100, true)]
    [InlineData(51, 100, false)]
    [InlineData(299, 100, false)]
    [InlineData(300, 100, true)]
    [InlineData(300, 0, false)]
    public void A_price_is_unexpected_when_half_the_usual_or_triple(int price, int typical, bool odd) =>
        Assert.Equal(odd, KhataFlat.IsOdd(price, typical));

    // ------------------------------------------------------------------------------------------
    // банк
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Import_adds_once_updates_what_changed_and_never_revives_the_dropped()
    {
        var store = new KhataStore(null, Dir(), new FakeClock());
        Assert.Equal((2, 0, 0), store.Import(KhataSources.Airbnb, [Ext("11"), Ext("12", price: 250)]));
        Assert.Equal(2, store.Live.Count);
        var flat = store.Live.Single(f => f.ExtId == "11");
        Assert.True(flat.External);
        Assert.Equal(("Airbnb", KhataFlat.Ok, "$", KhataFlat.Night, "https://www.airbnb.com/rooms/11"), (flat.Author, flat.Status, flat.Unit, flat.Per, flat.Link));
        Assert.Equal(["https://a0.muscache.com/im/pictures/11-cover.jpg?im_w=960"], flat.Urls);
        Assert.Equal("https://a0.muscache.com/im/pictures/11-cover.jpg?im_w=320", flat.Thumb);

        Assert.Equal((0, 0, 0), store.Import(KhataSources.Airbnb, [Ext("11"), Ext("12", price: 250)]));     // те саме вдруге — нічого
        Assert.Equal((1, 1, 1), store.Import(KhataSources.Airbnb, [Ext("11", price: 95), Ext("13"), Ext("13")]));   // ціна, нове, повтор у пачці
        Assert.Equal(95, store.Live.Single(f => f.ExtId == "11").Price);
        Assert.Equal(flat.Id, store.Live.Single(f => f.ExtId == "11").Id);                                  // та сама квартира — той самий номер

        Assert.True(store.Drop(flat.Id));
        Assert.False(store.Drop(flat.Id));
        Assert.Equal((0, 0, 1), store.Import(KhataSources.Airbnb, [Ext("11")]));                            // зняте не воскресає
        Assert.Equal(["12", "13"], store.Live.Select(f => f.ExtId).Order());
        Assert.Equal(2, store.BySource[KhataSources.Airbnb]);
        Assert.Equal(2, store.CountFit(null));
        Assert.Equal(2, store.CountFit(new HashSet<string> { KhataRegions.Eu }));
        Assert.Equal(0, store.CountFit(new HashSet<string> { KhataRegions.Ua }));
    }

    [Fact]
    public void Import_refuses_what_does_not_look_like_a_listing()
    {
        var store = new KhataStore(null, Dir(), new FakeClock());
        var bad = new[]
        {
            Ext("1") with { Region = "mars" },
            Ext("2") with { City = " " },
            Ext("3") with { Price = 0 },
            Ext("4") with { Per = "year" },
            Ext("5") with { Photos = [] },
            Ext("6") with { Photos = ["http://a0.muscache.com/im/pictures/x.jpg"] },
            Ext("7") with { Link = "javascript:alert(1)" },
            Ext("") ,
        };
        Assert.Equal((0, 0, bad.Length), store.Import(KhataSources.Airbnb, bad));
        Assert.Equal((0, 0, 1), store.Import(KhataSources.Own, [Ext("9")]));     // «свої» імпортом не приходять
        Assert.Empty(store.Live);
    }

    [Fact]
    public void Listings_live_apart_from_the_players_own_flats()
    {
        var store = new KhataStore(null, Dir(), new FakeClock());
        store.Import(KhataSources.Airbnb, [Ext("11")]);
        var id = store.Live[0].Id;
        Assert.Empty(store.Mine("Airbnb"));
        Assert.Empty(store.Everything());
        Assert.Equal(0, store.PendingCount);
        Assert.False(store.Approve(id).Ok);
        Assert.False(store.Edit(id, "eu", "Рига", 10).Ok);
        Assert.False(store.Remove(id, "Оля", true, false).Ok);          // гравець чужого оголошення не зніме
        Assert.Single(store.Live);
        Assert.True(store.Remove(id, "Глек", true, true).Ok);           // розробник — зніме (недоречне фото)
        Assert.Empty(store.Live);
        Assert.Equal((0, 0, 1), store.Import(KhataSources.Airbnb, [Ext("11")]));
    }

    [Fact]
    public void A_listing_is_found_by_the_photo_the_player_saw()
    {
        var store = new KhataStore(null, Dir(), new FakeClock());
        store.Import(KhataSources.Airbnb, [Ext("11"), Ext("12")]);
        Assert.Equal("12", store.ByPhoto("https://a0.muscache.com/im/pictures/12-cover.jpg?im_w=960")!.ExtId);
        Assert.Equal("11", store.ByPhoto("https://a0.muscache.com/im/pictures/11-cover.jpg")!.ExtId);
        Assert.Null(store.ByPhoto("https://a0.muscache.com/im/pictures/99-cover.jpg?im_w=960"));
        Assert.Null(store.ByPhoto(null));
        // коло перевірки йде по всіх і вертається на початок
        var ids = store.Live.Select(f => f.Id).ToArray();
        Assert.Equal([ids[0], ids[1], ids[0]], new[] { store.NextToCheck()!.Id, store.NextToCheck()!.Id, store.NextToCheck()!.Id });
    }

    [Fact]
    public void The_bank_of_listings_survives_a_restart_and_an_old_table_grows_new_columns()
    {
        using var temp = new TempDb();
        // таблиця першої версії гри — ще без джерел
        temp.Db.Exec("""
            CREATE TABLE khata_flats(id INTEGER PRIMARY KEY AUTOINCREMENT, region TEXT NOT NULL, city TEXT NOT NULL,
              price INTEGER NOT NULL, photos TEXT NOT NULL, author TEXT NOT NULL, author_key TEXT NOT NULL, at TEXT NOT NULL,
              status TEXT NOT NULL DEFAULT 'pending', removed INTEGER NOT NULL DEFAULT 0)
            """);
        temp.Db.Exec("INSERT INTO khata_flats(region, city, price, photos, author, author_key, at, status) VALUES('ua', 'Львів', 17500, '[\"0123456789abcdef0123.jpg\"]', 'Оля', 'оля', '2026-11-17T00:00:00.0000000+00:00', 'ok')");

        var dir = Dir();
        var store = new KhataStore(temp.Db, dir, new FakeClock());
        var old = Assert.Single(store.Live);
        Assert.Equal((KhataSources.Own, KhataFlat.Month, false), (old.Source, old.Per, old.External));

        store.Import(KhataSources.Airbnb, [Ext("11"), Ext("12", city: "Токіо, Японія", price: 90, region: KhataRegions.World, about: null)]);
        store.Drop(store.Live.Single(f => f.ExtId == "11").Id);

        var again = new KhataStore(temp.Db, dir, new FakeClock());
        Assert.Equal(2, again.Live.Count);
        var tokyo = again.Live.Single(f => f.External);
        Assert.Equal(("12", "Токіо, Японія", 90, KhataFlat.Night, KhataRegions.World, "https://www.airbnb.com/rooms/12"),
            (tokyo.ExtId, tokyo.City, tokyo.Price, tokyo.Per, tokyo.Region, tokyo.Link));
        Assert.Null(tokyo.About);
        Assert.Equal((0, 0, 1), again.Import(KhataSources.Airbnb, [Ext("11")]));     // зняте пам'ятає й після рестарту
    }

    // ------------------------------------------------------------------------------------------
    // мережа: імпорт і перевірка фото
    // ------------------------------------------------------------------------------------------

    sealed class FakeNet(Func<string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.AbsoluteUri);
            lock (Asked) Asked.Add(url);
            return Task.FromResult(answer(url));
        }
    }

    static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };

    static HttpResponseMessage Gz(string csv)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(Encoding.UTF8.GetBytes(csv));
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(ms.ToArray()) };
    }

    [Fact]
    public async Task The_world_import_reads_the_files_and_fills_the_bank()
    {
        KhataImport.CityGap = TimeSpan.Zero;
        const string index = """
            <a href="https://data.insideairbnb.com/latvia/riga/riga/2026-09-25/data/listings.csv.gz">x</a>
            <a href="https://data.insideairbnb.com/japan/kantō/tokyo/2026-09-25/data/listings.csv.gz">x</a>
            <a href="https://data.insideairbnb.com/italy/lazio/rome/2026-09-17/data/listings.csv.gz">x</a>
            """;
        const string rates = """[{"cc":"USD","rate":40},{"cc":"EUR","rate":44},{"cc":"JPY","rate":0.25}]""";
        var riga = string.Join('\n', Head, Line(1, "Old Town loft", type: "Entire loft"), Line(2, "Кімната", room: "Private room"), Line(3, "Tower", type: "Tower", price: "$200.00"));
        var tokyo = string.Join('\n', Head, Line(7, "Shinjuku flat", price: "$16,000.00", beds: 0));
        var net = new FakeNet(url =>
            url == KhataAirbnb.RatesUrl ? Text(rates)
            : url == KhataAirbnb.IndexUrl ? Text(index)
            : url.Contains("/riga/") ? Gz(riga)
            : url.Contains("/tokyo/") ? Gz(tokyo)
            : new HttpResponseMessage(HttpStatusCode.InternalServerError));     // Рим «лежить» — пропускаємо й ідемо далі

        var store = new KhataStore(null, Dir(), new FakeClock());
        var import = new KhataImport(store, new HttpClient(net), new FakeClock());
        await import.RunAirbnb(CancellationToken.None);

        Assert.Equal(["1", "3", "7"], store.Live.Select(f => f.ExtId).Order());
        var loft = store.Live.Single(f => f.ExtId == "1");
        Assert.Equal(("Рига, Латвія", 110, KhataRegions.Eu, "«Old Town loft» · лофт · 1 спальня"), (loft.City, loft.Price, loft.Region, loft.About));
        var flat = store.Live.Single(f => f.ExtId == "7");
        Assert.Equal(("Токіо, Японія", 100, KhataRegions.World, "«Shinjuku flat» · квартира · студія"), (flat.City, flat.Price, flat.Region, flat.About));
        Assert.Contains("вежа", store.Live.Single(f => f.ExtId == "3").About);

        var state = import.State;
        Assert.False(state.Running);
        Assert.Equal((KhataAirbnb.Cities.Count, 3), (state.Done, state.Added));
        Assert.Contains("нових оголошень — 3", state.Message);
        Assert.Contains("не далося", state.Message);       // Рим і всі міста, яких у списку файлів не було
        Assert.Contains(net.Asked, u => u.Contains("/kantō/tokyo/"));

        await import.RunAirbnb(CancellationToken.None);    // удруге — тих самих не дублює
        Assert.Equal(3, store.Live.Count);
        Assert.Equal(0, import.State.Added);
    }

    [Fact]
    public async Task The_import_says_so_when_it_cannot_even_start()
    {
        var store = new KhataStore(null, Dir(), new FakeClock());
        var import = new KhataImport(store, new HttpClient(new FakeNet(_ => new HttpResponseMessage(HttpStatusCode.BadGateway))), new FakeClock());
        await import.RunAirbnb(CancellationToken.None);
        Assert.False(import.State.Running);
        Assert.Contains("Не вийшло", import.State.Message);
        Assert.Empty(store.Live);
    }

    [Fact]
    public async Task A_listing_whose_photo_is_gone_leaves_the_bank_and_a_hiccup_does_not_count()
    {
        var store = new KhataStore(null, Dir(), new FakeClock());
        store.Import(KhataSources.Airbnb, [Ext("11"), Ext("12"), Ext("13")]);
        var net = new FakeNet(url => new HttpResponseMessage(
            url.Contains("/11-") ? HttpStatusCode.NotFound : url.Contains("/12-") ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        var import = new KhataImport(store, new HttpClient(net), new FakeClock());

        foreach (var flat in store.Live.ToArray()) await import.Check(flat, CancellationToken.None);
        Assert.Equal(["12", "13"], store.Live.Select(f => f.ExtId).Order());      // 404 — знято; 503 — це не «нема фото»
        Assert.All(net.Asked, u => Assert.EndsWith("?im_w=320", u));               // питаємо мініатюру, не оригінал

        Assert.True(import.Suspect("https://a0.muscache.com/im/pictures/13-cover.jpg?im_w=960"));
        Assert.False(import.Suspect("https://a0.muscache.com/im/pictures/13-cover.jpg?im_w=960"));    // уже в черзі
        Assert.False(import.Suspect("https://a0.muscache.com/im/pictures/nobody.jpg"));
        Assert.False(import.Suspect("/api/games/khata/pic/0123456789abcdef0123.jpg"));                // свої фото — не сюди
    }

    // ------------------------------------------------------------------------------------------
    // гра
    // ------------------------------------------------------------------------------------------

    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;

    static void Until(RoomHarness h, string phase)
    {
        for (var i = 0; i < MaxTicks && h.Room.Status == RoomStatus.Playing && Phase(h) != phase; i++) h.Tick(1);
        Assert.Equal(phase, Phase(h));
    }

    [Fact]
    public void A_listing_is_played_by_the_night_and_opens_up_only_after_the_answers()
    {
        var store = new KhataStore(null, Dir(), new FakeClock());
        store.Import(KhataSources.Airbnb, [Ext("11", city: "Дордонь, Франція", price: 140, about: "«Medieval castle» · замок · 4 спальні")]);
        var h = new RoomHarness("khata", services: RoomHarness.WithService(store), seed: 3);
        h.Join("Оля");
        h.Join("Петро");
        Assert.True(h.Start().Ok);
        Until(h, "ask");

        var v = h.View(0);
        Assert.Equal(("Дордонь, Франція", "$", "night"), (v.GetProperty("city").GetString(), v.GetProperty("unit").GetString(), v.GetProperty("per").GetString()));
        Assert.Equal("https://a0.muscache.com/im/pictures/11-cover.jpg?im_w=960", v.GetProperty("photos")[0].GetString());
        var raw = v.GetRawText();
        Assert.DoesNotContain("castle", raw);              // ні назви, ні посилання, ні ціни до відповіді
        Assert.DoesNotContain("airbnb.com/rooms", raw);

        Assert.True(h.Act(0, "answer", new { value = 400 }).Ok);
        Assert.True(h.Act(1, "answer", new { value = "140" }).Ok);
        h.Tick(1);
        var rev = h.View(null).GetProperty("reveal");
        Assert.Equal(140, rev.GetProperty("price").GetInt32());
        Assert.Equal(("airbnb", "Airbnb", "«Medieval castle» · замок · 4 спальні", "https://www.airbnb.com/rooms/11"),
            (rev.GetProperty("source").GetString(), rev.GetProperty("by").GetString(), rev.GetProperty("about").GetString(), rev.GetProperty("link").GetString()));
        Assert.Equal(store.Live[0].Id, rev.GetProperty("id").GetInt64());

        Until(h, "done");
        var recap = h.View(null).GetProperty("recap")[0];
        Assert.Equal(("night", "https://www.airbnb.com/rooms/11", "https://a0.muscache.com/im/pictures/11-cover.jpg?im_w=320"),
            (recap.GetProperty("per").GetString(), recap.GetProperty("link").GetString(), recap.GetProperty("photo").GetString()));
        Assert.Equal(Khata.Shards(1000), h.Awards.Single().Shards);      // Петро влучив точно; Олі за 45 балів черепків нема
    }

    [Fact]
    public void A_party_does_not_repeat_a_city_while_there_are_others()
    {
        var store = new KhataStore(null, Dir(), new FakeClock());
        var items = new List<KhataExternal>();
        for (var i = 0; i < 20; i++) items.Add(Ext("p" + i, city: "Париж, Франція"));
        string[] others = ["Рим, Італія", "Рига, Латвія", "Токіо, Японія", "Осло, Норвегія"];
        for (var i = 0; i < others.Length; i++) items.Add(Ext("o" + i, city: others[i]));
        store.Import(KhataSources.Airbnb, items);

        for (var seed = 1; seed <= 5; seed++)
        {
            var h = new RoomHarness("khata", options: new { flats = "5" }, services: RoomHarness.WithService(store), seed: seed);
            h.Join("Оля");
            h.Start();
            var cities = new List<string>();
            for (var round = 0; round < 5; round++)
            {
                Until(h, "ask");
                cities.Add(h.View(null).GetProperty("city").GetString()!);
                Until(h, round < 4 ? "between" : "done");
            }
            Assert.Equal(5, cities.Distinct().Count());     // п'ять різних міст, хоч Парижів у банку вп'ятеро більше
        }

        // а коли міст менше, ніж раундів, — добираємо повторами, партія не коротшає
        var seven = new RoomHarness("khata", options: new { flats = "7" }, services: RoomHarness.WithService(store), seed: 9);
        seven.Join("Оля");
        seven.Start();
        Assert.Equal(7, seven.View(null).GetProperty("of").GetInt32());
    }
}
