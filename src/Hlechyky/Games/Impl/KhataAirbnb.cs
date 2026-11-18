using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Світ для «Здам хату» — з відкритого набору Inside Airbnb (insideairbnb.com, ліцензія CC BY 4.0): справжні оголошення
/// понад сотні міст — посилання, назва, тип житла, обкладинка, ціна за добу в місцевій валюті. Тут лише чисті речі:
/// каталог міст, розбір сторінки з файлами, CSV, відбір і переклад у квартири банку. Мережа — у <see cref="KhataImport"/>.
/// Сам набір ми не перевидаємо: відібране живе в базі сервера (не в репозиторії), фото показуються з сервера Airbnb.
/// </summary>
public static partial class KhataAirbnb
{
    public const string IndexUrl = "https://insideairbnb.com/get-the-data/";
    /// <summary>Офіційний курс НБУ: гривень за одиницю валюти. Через нього місцеві ціни стають доларами.</summary>
    public const string RatesUrl = "https://bank.gov.ua/NBUStatService/v1/statdirectory/exchange?json";
    /// <summary>Скільки оголошень беремо з одного міста.</summary>
    public const int PerCity = 60;
    /// <summary>Скільки з них може бути «хатою мрії»: замок чи інше диво, житло біля пляжу чи з видом на море, вілла з басейном.</summary>
    public const int DreamPerCity = 25;
    /// <summary>Яка частка цієї квоти — «мрії» з ціною, якої ніхто не чекає (<see cref="KhataFlat.IsOdd"/>): кожна третя.</summary>
    public const int DreamOddShare = 3;
    /// <summary>Ціна за ніч у доларах, у межах якої оголошення схоже на справжнє (за межами — заглушки й одруківки хазяїв).</summary>
    public const int MinUsd = 8, MaxUsd = 5000;
    public const int MinReviews = 3;
    public const double MinRating = 4.0;

    /// <summary>Місто набору: як воно зветься в адресі файлу, як ми пишемо його гравцеві, регіон гри й валюта цін.</summary>
    public sealed record City(string Slug, string Name, string Region, string Currency);

    const string Eu = KhataRegions.Eu, World = KhataRegions.World;

    /// <summary>
    /// Міста, які беремо. Валюта — місцева (так записані ціни в наборі). Міст, чиєї валюти нема в курсі НБУ (Бразилія,
    /// Аргентина, Колумбія, Чилі, Кенія, Тайвань, Беліз), тут нема: перерахувати їхні ціни нічим.
    /// </summary>
    public static readonly IReadOnlyList<City> Cities =
    [
        new("amsterdam", "Амстердам, Нідерланди", Eu, "EUR"),
        new("antwerp", "Антверпен, Бельгія", Eu, "EUR"),
        new("athens", "Афіни, Греція", Eu, "EUR"),
        new("barcelona", "Барселона, Іспанія", Eu, "EUR"),
        new("bergamo", "Бергамо, Італія", Eu, "EUR"),
        new("berlin", "Берлін, Німеччина", Eu, "EUR"),
        new("bologna", "Болонья, Італія", Eu, "EUR"),
        new("bordeaux", "Бордо, Франція", Eu, "EUR"),
        new("bristol", "Бристоль, Англія", Eu, "GBP"),
        new("brussels", "Брюссель, Бельгія", Eu, "EUR"),
        new("budapest", "Будапешт, Угорщина", Eu, "HUF"),
        new("copenhagen", "Копенгаген, Данія", Eu, "DKK"),
        new("crete", "Крит, Греція", Eu, "EUR"),
        new("dublin", "Дублін, Ірландія", Eu, "EUR"),
        new("edinburgh", "Единбург, Шотландія", Eu, "GBP"),
        new("euskadi", "Країна Басків, Іспанія", Eu, "EUR"),
        new("florence", "Флоренція, Італія", Eu, "EUR"),
        new("geneva", "Женева, Швейцарія", Eu, "CHF"),
        new("ghent", "Гент, Бельгія", Eu, "EUR"),
        new("girona", "Жирона, Іспанія", Eu, "EUR"),
        new("greater-manchester", "Манчестер, Англія", Eu, "GBP"),
        new("ireland", "Ірландія", Eu, "EUR"),
        new("istanbul", "Стамбул, Туреччина", Eu, "TRY"),
        new("lisbon", "Лісабон, Португалія", Eu, "EUR"),
        new("london", "Лондон, Англія", Eu, "GBP"),
        new("lyon", "Ліон, Франція", Eu, "EUR"),
        new("madrid", "Мадрид, Іспанія", Eu, "EUR"),
        new("malaga", "Малага, Іспанія", Eu, "EUR"),
        new("mallorca", "Мальорка, Іспанія", Eu, "EUR"),
        new("malta", "Мальта", Eu, "EUR"),
        new("menorca", "Менорка, Іспанія", Eu, "EUR"),
        new("milan", "Мілан, Італія", Eu, "EUR"),
        new("munich", "Мюнхен, Німеччина", Eu, "EUR"),
        new("naples", "Неаполь, Італія", Eu, "EUR"),
        new("oslo", "Осло, Норвегія", Eu, "NOK"),
        new("paris", "Париж, Франція", Eu, "EUR"),
        new("pays-basque", "Країна Басків, Франція", Eu, "EUR"),
        new("porto", "Порту, Португалія", Eu, "EUR"),
        new("prague", "Прага, Чехія", Eu, "CZK"),
        new("puglia", "Апулія, Італія", Eu, "EUR"),
        new("riga", "Рига, Латвія", Eu, "EUR"),
        new("rome", "Рим, Італія", Eu, "EUR"),
        new("rotterdam", "Роттердам, Нідерланди", Eu, "EUR"),
        new("sevilla", "Севілья, Іспанія", Eu, "EUR"),
        new("sicily", "Сицилія, Італія", Eu, "EUR"),
        new("south-aegean", "Егейські острови, Греція", Eu, "EUR"),
        new("stockholm", "Стокгольм, Швеція", Eu, "SEK"),
        new("the-hague", "Гаага, Нідерланди", Eu, "EUR"),
        new("thessaloniki", "Салоніки, Греція", Eu, "EUR"),
        new("trentino", "Трентіно, Італія", Eu, "EUR"),
        new("valencia", "Валенсія, Іспанія", Eu, "EUR"),
        new("vaud", "Во, Швейцарія", Eu, "CHF"),
        new("venice", "Венеція, Італія", Eu, "EUR"),
        new("vienna", "Відень, Австрія", Eu, "EUR"),
        new("zurich", "Цюрих, Швейцарія", Eu, "CHF"),

        new("asheville", "Ешвілл, США", World, "USD"),
        new("austin", "Остін, США", World, "USD"),
        new("bangkok", "Бангкок, Таїланд", World, "THB"),
        new("boston", "Бостон, США", World, "USD"),
        new("bozeman", "Бозмен, Монтана, США", World, "USD"),
        new("brisbane", "Брисбен, Австралія", World, "AUD"),
        new("cape-town", "Кейптаун, ПАР", World, "ZAR"),
        new("chicago", "Чикаго, США", World, "USD"),
        new("clark-county-nv", "Лас-Вегас, США", World, "USD"),
        new("denver", "Денвер, США", World, "USD"),
        new("hawaii", "Гаваї, США", World, "USD"),
        new("hong-kong", "Гонконг", World, "HKD"),
        new("los-angeles", "Лос-Анджелес, США", World, "USD"),
        new("melbourne", "Мельбурн, Австралія", World, "AUD"),
        new("mexico-city", "Мехіко, Мексика", World, "MXN"),
        new("montreal", "Монреаль, Канада", World, "CAD"),
        new("nashville", "Нашвілл, США", World, "USD"),
        new("new-orleans", "Новий Орлеан, США", World, "USD"),
        new("new-york-city", "Нью-Йорк, США", World, "USD"),
        new("new-zealand", "Нова Зеландія", World, "NZD"),
        new("portland", "Портленд, США", World, "USD"),
        new("quebec-city", "Квебек, Канада", World, "CAD"),
        new("san-diego", "Сан-Дієго, США", World, "USD"),
        new("san-francisco", "Сан-Франциско, США", World, "USD"),
        new("seattle", "Сіетл, США", World, "USD"),
        new("singapore", "Сингапур", World, "SGD"),
        new("sydney", "Сідней, Австралія", World, "AUD"),
        new("tasmania", "Тасманія, Австралія", World, "AUD"),
        new("tokyo", "Токіо, Японія", World, "JPY"),
        new("toronto", "Торонто, Канада", World, "CAD"),
        new("vancouver", "Ванкувер, Канада", World, "CAD"),
        new("washington-dc", "Вашингтон, США", World, "USD"),
        new("western-australia", "Західна Австралія", World, "AUD"),
    ];

    [GeneratedRegex(@"https://data\.insideairbnb\.com/(?:[^""/\s<>]+/)*?([^""/\s<>]+)/(\d{4}-\d{2}-\d{2})/data/listings\.csv\.gz")]
    private static partial Regex FileLink();

    /// <summary>Зі сторінки «Get the Data»: місто (остання частина шляху перед датою) → адреса найсвіжішого listings.csv.gz.</summary>
    public static Dictionary<string, string> Latest(string indexHtml)
    {
        var best = new Dictionary<string, (string Date, string Url)>(StringComparer.Ordinal);
        foreach (Match m in FileLink().Matches(indexHtml))
        {
            var slug = Uri.UnescapeDataString(m.Groups[1].Value).ToLowerInvariant();
            var date = m.Groups[2].Value;
            if (!best.TryGetValue(slug, out var was) || string.CompareOrdinal(date, was.Date) > 0) best[slug] = (date, m.Value);
        }
        return best.ToDictionary(kv => kv.Key, kv => kv.Value.Url, StringComparer.Ordinal);
    }

    /// <summary>Курс НБУ як є: скільки гривень в одиниці кожної валюти.</summary>
    public static Dictionary<string, double> UahPer(string ratesJson)
    {
        var uah = new Dictionary<string, double>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(ratesJson);
        foreach (var e in doc.RootElement.EnumerateArray())
            if (e.TryGetProperty("cc", out var cc) && cc.GetString() is { Length: 3 } code && e.TryGetProperty("rate", out var rate)
                && rate.TryGetDouble(out var r) && r > 0) uah[code] = r;
        return uah;
    }

    /// <summary>Курс НБУ → скільки доларів в одиниці кожної валюти (USD = 1).</summary>
    public static Dictionary<string, double> UsdPer(string ratesJson)
    {
        var uah = UahPer(ratesJson);
        if (!uah.TryGetValue("USD", out var usd)) return [];
        return uah.ToDictionary(kv => kv.Key, kv => kv.Value / usd, StringComparer.Ordinal);
    }

    /// <summary>
    /// CSV за RFC 4180 потоком: поля в лапках, подвоєні лапки, переноси рядків усередині поля (описи житла на пів сторінки).
    /// Файл великого міста — сотні мегабайтів тексту, тому ніякого «прочитати все в пам'ять».
    /// </summary>
    public static IEnumerable<List<string>> Csv(TextReader reader)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var any = false;
        int ch;
        while ((ch = reader.Read()) >= 0)
        {
            var c = (char)ch;
            if (quoted)
            {
                if (c != '"') { field.Append(c); continue; }
                if (reader.Peek() == '"') { reader.Read(); field.Append('"'); }
                else quoted = false;
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; any = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); any = true; break;
                case '\r': break;
                case '\n':
                    if (any || field.Length > 0)
                    {
                        row.Add(field.ToString());
                        yield return row;
                        row = [];
                    }
                    field.Clear();
                    any = false;
                    break;
                default: field.Append(c); any = true; break;
            }
        }
        if (any || field.Length > 0)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }

    /// <summary>«$1,338.49» → 1338.49. Порожнє чи не число — null.</summary>
    public static double? Price(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var clean = raw.Replace("$", "").Replace(",", "").Trim();
        return double.TryParse(clean, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
    }

    /// <summary>
    /// Обкладинка з набору → адреса, з якої сервер фото Airbnb віддає зменшений варіант (шлях <c>/im/</c>; розмір додає
    /// <see cref="KhataSources.Show"/>). Не їхній сервер чи дивна адреса — null: такого оголошення не беремо.
    /// </summary>
    public static string? Photo(string? url)
    {
        const string host = "https://a0.muscache.com/";
        if (string.IsNullOrEmpty(url) || !url.StartsWith(host, StringComparison.Ordinal)) return null;
        var cut = url.IndexOfAny(['?', '#']);
        var path = (cut < 0 ? url : url[..cut])[host.Length..];
        if (path.StartsWith("im/", StringComparison.Ordinal)) path = path[3..];
        if (!path.StartsWith("pictures/", StringComparison.Ordinal) || path.Length < 14 || path.Any(ch => char.IsWhiteSpace(ch) || ch is '"' or '<' or '>')) return null;
        return host + "im/" + path;
    }

    /// <summary>Рядок набору, який нам годиться: є фото, ціна й назва.</summary>
    public sealed record Row(string Id, string Name, string Photo, double Price, string PropertyType, string RoomType, int Bedrooms,
        int Reviews, double? Rating, Perk Perks = Perk.None);

    /// <summary>Чим житло гарне, за позначками зручностей у наборі: пляж поруч, вид на море, вода поруч, басейн.</summary>
    [Flags]
    public enum Perk { None = 0, Beach = 1, SeaView = 2, Water = 4, Pool = 8 }

    /// <summary>
    /// Колонка <c>amenities</c> (JSON-масив підписів на кшталт «Beachfront», «Sea view», «Private pool») → позначки.
    /// «Pool table» і «Pool view» басейном не рахуємо.
    /// </summary>
    public static Perk PerksOf(string? amenitiesJson)
    {
        var perks = Perk.None;
        if (string.IsNullOrWhiteSpace(amenitiesJson)) return perks;
        try
        {
            using var doc = JsonDocument.Parse(amenitiesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return perks;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.String) continue;
                var a = (e.GetString() ?? "").ToLowerInvariant();
                if (a.Contains("beachfront") || a.Contains("beach access")) perks |= Perk.Beach;
                if (a.Contains("sea view") || a.Contains("ocean view") || a.Contains("beach view") || a.Contains("bay view")) perks |= Perk.SeaView;
                if (a.Contains("waterfront") || a.Contains("lake view") || a.Contains("river view") || a.Contains("canal view")
                    || a.Contains("harbor view") || a.Contains("marina view")) perks |= Perk.Water;
                if (a.Contains("pool") && !a.Contains("table") && !a.Contains("view") && !a.Contains("toys") && !a.Contains("cover")) perks |= Perk.Pool;
            }
        }
        catch (JsonException) { }
        return perks;
    }

    /// <summary>
    /// Слова в назві, що обіцяють цікаву архітектуру чи старовину (початок слова, кількома мовами): готика, середньовіччя,
    /// бароко, палац, маєток, абатство, фортеця, вежа, «архітекторський»… Хазяї таким пишаються й виносять у назву.
    /// </summary>
    static readonly string[] Grand =
    [
        "gothic", "gotic", "gótic", "gotisch", "medieval", "médiéval", "mittelalter", "baroque", "barocco", "barock", "renaissance",
        "rinascimento", "victorian", "georgian", "tudor", "heritage", "manor", "manoir", "mansion", "palace", "palazzo", "palacio",
        "palais", "castle", "château", "chateau", "castello", "castillo", "schloss", "abbey", "abbaye", "abbazia", "monaster", "convent",
        "chapel", "chapelle", "cappella", "church", "église", "chiesa", "fortress", "fortezza", "citadel", "architect", "haussmann",
        "siècle", "secolo", "siglo", "century", "jugendstil",
    ];

    /// <summary>
    /// Слова «поруч із»: «Castle view», «Close to the Palace», «5 min to the Abbey» — це про сусіда, а не про саме житло.
    /// З ними архітектурі в назві не віримо. («Historic center» і «X Tower» сюди ж: тому їх нема в списку вище.)
    /// </summary>
    static readonly HashSet<string> Nearby = new(StringComparer.OrdinalIgnoreCase)
    {
        "view", "views", "near", "nearby", "close", "next", "min", "mins", "minute", "minutes", "walk", "walking", "steps", "vista",
        "vistas", "vicino", "près", "proche", "cerca", "junto", "nähe", "blick", "opposite", "facing", "behind",
    };

    /// <summary>Слова «розкоші»: самі по собі — реклама, але разом із басейном чи морем уже щось означають.</summary>
    static readonly string[] Fancy = ["penthouse", "luxury", "luxurious", "lujo", "lusso", "luxe", "luxus", "designer", "villa", "infinity", "rooftop"];

    static bool Named(string name, string[] words)
    {
        foreach (var word in Words(name))
            foreach (var w in words)
                if (word.StartsWith(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Цікава архітектура чи старовина — за назвою оголошення.</summary>
    public static bool IsGrand(string name) => Named(name, Grand) && !Words(name).Any(Nearby.Contains);

    /// <summary>
    /// «Хата мрії»: диво, якому можна вірити (замок, вежа, човен…), цікава архітектура (готика, палац, маєток — за назвою),
    /// житло біля пляжу чи з видом на море, або вілла чи «люкс» із басейном. Такі беремо окремою квотою й позначаємо —
    /// у грі з них збирається добірка «Хати мрії».
    /// </summary>
    public static bool IsDream(Row r) =>
        IsWow(r.PropertyType, r.Name) || IsGrand(r.Name) || (r.Perks & (Perk.Beach | Perk.SeaView)) != 0
        || (r.Perks.HasFlag(Perk.Pool) && (string.Equals(r.PropertyType, "Entire villa", StringComparison.OrdinalIgnoreCase) || Named(r.Name, Fancy)));

    /// <summary>
    /// Наскільки це «мрія»: диво й архітектура важать більше за будь-яке море з басейном (замок без моря — вище за
    /// квартиру на пляжі), далі пляж і море, басейн — приємний додаток.
    /// </summary>
    static int Rank(Row r) =>
        (IsWow(r.PropertyType, r.Name) ? 8 : 0) + (IsGrand(r.Name) ? 6 : 0) + (r.Perks.HasFlag(Perk.Beach) ? 2 : 0)
        + (r.Perks.HasFlag(Perk.SeaView) ? 2 : 0) + (r.Perks.HasFlag(Perk.Pool) ? 1 : 0);

    /// <summary>Рядки файлу міста: колонки шукаємо за назвами з першого рядка (їх там 90, і порядок міняється).</summary>
    public static IEnumerable<Row> Rows(TextReader csv)
    {
        Dictionary<string, int>? at = null;
        foreach (var r in Csv(csv))
        {
            if (at is null)
            {
                at = [];
                for (var i = 0; i < r.Count; i++) at[r[i].Trim().TrimStart('﻿')] = i;
                if (!at.ContainsKey("id") || !at.ContainsKey("picture_url") || !at.ContainsKey("price")) yield break;
                continue;
            }
            string Get(string col) => at.TryGetValue(col, out var i) && i < r.Count ? r[i].Trim() : "";
            var id = Get("id");
            if (id.Length == 0 || !id.All(char.IsAsciiDigit)) continue;
            if (Photo(Get("picture_url")) is not { } photo || Price(Get("price")) is not { } price) continue;
            var name = Get("name");
            if (name.Length == 0) continue;
            _ = int.TryParse(Get("bedrooms"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var beds);
            _ = int.TryParse(Get("number_of_reviews"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var reviews);
            double? rating = double.TryParse(Get("review_scores_rating"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rt) ? rt : null;
            yield return new Row(id, name, photo, price, Get("property_type"), Get("room_type"), beds, reviews, rating, PerksOf(Get("amenities")));
        }
    }

    /// <summary>Тип житла з набору → як ми назвемо його на розкритті. Чого тут нема — про тип мовчимо.</summary>
    static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Entire rental unit"] = "квартира", ["Entire condo"] = "квартира", ["Entire home"] = "будинок", ["Entire loft"] = "лофт",
        ["Entire serviced apartment"] = "апартаменти", ["Entire villa"] = "вілла", ["Entire townhouse"] = "таунхаус",
        ["Entire cottage"] = "котедж", ["Entire cabin"] = "хатина", ["Entire chalet"] = "шале", ["Entire bungalow"] = "бунгало",
        ["Entire guesthouse"] = "гостьовий будинок", ["Entire guest suite"] = "гостьові апартаменти",
        ["Entire vacation home"] = "дачний будинок", ["Entire place"] = "житло",
        ["Castle"] = "замок", ["Treehouse"] = "будиночок на дереві", ["Houseboat"] = "плавучий будинок", ["Boat"] = "човен",
        ["Cave"] = "печера", ["Dome"] = "купол", ["Windmill"] = "вітряк", ["Tower"] = "вежа", ["Lighthouse"] = "маяк",
        ["Island"] = "острів", ["Yurt"] = "юрта", ["Hut"] = "хижа", ["Barn"] = "стодола", ["Train"] = "вагон", ["Bus"] = "автобус",
        ["Farm stay"] = "ферма", ["Camper/RV"] = "кемпер", ["Shipping container"] = "контейнер", ["Trullo"] = "трулло",
        ["Cycladic home"] = "кікладський будинок", ["Dammuso"] = "даммузо", ["Riad"] = "ріад", ["Ryokan"] = "рьокан",
        ["Tent"] = "намет", ["Igloo"] = "іглу", ["Plane"] = "літак", ["Earthen home"] = "землянка", ["Tiny home"] = "крихітний будинок",
        ["Shepherd’s hut"] = "пастуша хатка", ["Shepherd's hut"] = "пастуша хатка", ["Religious building"] = "колишній храм",
        ["Tipi"] = "тіпі", ["Ranch"] = "ранчо", ["Casa particular"] = "приватний дім",
    };

    /// <summary>
    /// «Дива»: житло, на яке цікаво глянути й без гри. Їх беремо окремою квотою, а не як пощастить. «Tiny home» і кемперів
    /// тут нема свідомо: у наборі це кожне десяте оголошення (звичайні флігелі й фургони), і вони з'їдали всю квоту див.
    /// </summary>
    /// <remarks>
    /// Тип хазяїн обирає сам, і «замком» чи «маяком» у наборі буває звичайна студія. Тому диву віримо, лише коли й назва
    /// оголошення каже те саме — слово з цього списку (початок слова, кількома мовами). Порожній список — тип місцевий
    /// і сам за себе каже (трулло, даммузо, ріад): йому віримо без назви.
    /// </remarks>
    static readonly Dictionary<string, string[]> Wow = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Castle"] = ["castle", "château", "chateau", "castillo", "castello", "castell", "castelo", "schloss", "kasteel", "zamek", "palace", "palazzo", "palacio", "palais"],
        ["Tower"] = ["tower", "torre", "turm", "toren", "torri"],
        ["Lighthouse"] = ["lighthouse", "faro", "phare", "leuchtturm", "farol", "vuurtoren"],
        ["Windmill"] = ["windmill", "molen", "moulin", "mulino", "molino", "mühle", "muhle"],
        ["Treehouse"] = ["treehouse", "tree", "cabane", "baumhaus", "boomhut", "árbol", "arbol", "albero", "arbre"],
        ["Cave"] = ["cave", "cueva", "grotta", "grotte", "troglo", "höhle", "sassi", "cavern", "grot"],
        ["Boat"] = ["boat", "yacht", "sail", "voilier", "barco", "velero", "veliero", "boot", "schiff", "péniche", "peniche", "barca", "bateau", "catamaran", "ship", "barge", "gulet", "tekne", "segel"],
        ["Houseboat"] = ["houseboat", "boat", "hausboot", "woonboot", "péniche", "peniche", "barge", "bateau", "barco", "ship", "floating", "husbåd", "woonark", "boot", "yacht"],
        ["Dome"] = ["dome", "glamping", "geodesic", "bubble", "cúpula", "cupula", "kuppel", "domo", "igloo"],
        ["Igloo"] = ["igloo", "iglu"],
        ["Yurt"] = ["yurt", "jurte", "yourte"],
        ["Island"] = ["island", "isla", "île", "ile", "isola", "insel"],
        ["Train"] = ["train", "wagon", "railway", "carriage", "caboose", "tren", "treno", "waggon"],
        ["Bus"] = ["bus", "autobus"],
        ["Plane"] = ["plane", "aircraft", "avion", "jet", "boeing"],
        ["Barn"] = ["barn", "grange", "granero", "fienile", "scheune", "schuur"],
        ["Hut"] = ["hut", "cabane", "rifugio", "cabaña", "cabana", "hütte", "hutte", "refuge", "bothy"],
        ["Earthen home"] = ["earth", "cob", "adobe", "tierra", "terra", "hobbit", "mud"],
        ["Shipping container"] = ["container", "contenedor", "conteneur"],
        ["Shepherd’s hut"] = ["shepherd", "pastor", "berger", "hut"],
        ["Shepherd's hut"] = ["shepherd", "pastor", "berger", "hut"],
        ["Religious building"] = ["church", "chapel", "convent", "monaster", "iglesia", "chiesa", "église", "eglise", "kirche", "capilla", "cappella", "abbey", "kloster", "kerk", "temple"],
        ["Tipi"] = ["tipi", "teepee", "tepee"],
        ["Trullo"] = [], ["Cycladic home"] = [], ["Dammuso"] = [], ["Riad"] = [], ["Ryokan"] = [],
    };

    /// <summary>Тип із «див» узагалі (вірити йому чи ні — <see cref="IsWow(string, string)"/>).</summary>
    public static bool IsWowType(string propertyType) => Wow.ContainsKey(propertyType);

    /// <summary>Диво, якому можна вірити: тип зі списку, і назва оголошення його підтверджує.</summary>
    public static bool IsWow(string propertyType, string name)
    {
        if (!Wow.TryGetValue(propertyType, out var words)) return false;
        if (words.Length == 0) return true;
        foreach (var word in Words(name))
            foreach (var w in words)
                if (word.StartsWith(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static IEnumerable<string> Words(string text)
    {
        var from = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var letter = i < text.Length && char.IsLetter(text[i]);
            if (letter && from < 0) from = i;
            else if (!letter && from >= 0) { yield return text[from..i]; from = -1; }
        }
    }

    /// <summary>«студія», «1 спальня», «3 спальні», «6 спалень».</summary>
    public static string Bedrooms(int n)
    {
        if (n <= 0) return "студія";
        var d = n % 10;
        var h = n % 100;
        var word = d == 1 && h != 11 ? "спальня" : d is >= 2 and <= 4 && h is < 12 or > 14 ? "спальні" : "спалень";
        return $"{n.ToString(CultureInfo.InvariantCulture)} {word}";
    }

    /// <summary>Що це за житло — рядок для розкриття: «Назва з оголошення» · тип · спальні · біля пляжу · з басейном.</summary>
    public static string About(Row r)
    {
        var name = new string([.. r.Name.Where(ch => !char.IsControl(ch))]).Trim();
        if (name.Length > 90) name = name[..89].TrimEnd() + "…";
        var parts = new List<string> { $"«{name}»" };
        // «Замок», якого назва не підтверджує, — це, найпевніше, студія з вигадливим хазяїном: про тип тоді мовчимо.
        if (Types.TryGetValue(r.PropertyType, out var type) && (!IsWowType(r.PropertyType) || IsWow(r.PropertyType, r.Name))) parts.Add(type);
        parts.Add(Bedrooms(r.Bedrooms));
        if (r.Perks.HasFlag(Perk.Beach)) parts.Add("біля пляжу");
        if (r.Perks.HasFlag(Perk.SeaView)) parts.Add("вид на море");
        else if (r.Perks.HasFlag(Perk.Water) && !r.Perks.HasFlag(Perk.Beach)) parts.Add("біля води");
        if (r.Perks.HasFlag(Perk.Pool)) parts.Add("з басейном");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Відібрати оголошення міста: лише ціле житло (не кімната), з відгуками, з притомною ціною. Спершу — «хати мрії»
    /// (<see cref="IsDream"/>; найцікавіші першими, кожна третя — з несподіваною ціною, не більше <see cref="DreamPerCity"/>), решта — порівну з п'яти цінових
    /// смуг від найдешевших до найдорожчих: так у місті є і халупа, і палац, а вгадувати цікаво.
    /// </summary>
    public static List<KhataExternal> Pick(IEnumerable<Row> rows, City city, double usdPerUnit, int take, Random rng)
    {
        var good = new List<(Row R, int Usd)>();
        foreach (var r in rows)
        {
            if (!string.Equals(r.RoomType, "Entire home/apt", StringComparison.OrdinalIgnoreCase)) continue;
            if (r.Reviews < MinReviews || r.Rating is < MinRating) continue;
            var usd = (int)Math.Round(r.Price * usdPerUnit, MidpointRounding.AwayFromZero);
            if (usd is < MinUsd or > MaxUsd) continue;
            good.Add((r, usd));
        }
        Shuffle(good, rng);
        var picked = new List<(Row R, int Usd)>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        // «Мрії»: головне — цікавість, тож усюди йдемо від найвиразніших (замки, архітектура, море; OrderBy стабільний,
        // а список уже перетасовано — серед однакових вибір випадковий). Третину квоти віддаємо «мріям» із ціною, якої
        // ніхто не чекає: удвічі дешевше за звичайне для міста або втричі дорожче — порівну з обох боків, скільки є.
        var typical = good.Count == 0 ? 0 : good.Select(g => g.Usd).Order().ElementAt(good.Count / 2);
        var dreamQuota = Math.Min(DreamPerCity, take);
        var dreams = good.Where(g => IsDream(g.R)).OrderByDescending(g => Rank(g.R)).ToList();
        var cheap = dreams.Where(g => g.Usd < typical && KhataFlat.IsOdd(g.Usd, typical)).ToList();
        var dear = dreams.Where(g => g.Usd > typical && KhataFlat.IsOdd(g.Usd, typical)).ToList();
        var oddQuota = dreamQuota / DreamOddShare;
        var fromCheap = Math.Min(cheap.Count, Math.Max(oddQuota / 2, oddQuota - dear.Count));
        foreach (var g in cheap.Take(fromCheap).Concat(dear.Take(oddQuota - fromCheap)).Concat(dreams))
        {
            if (picked.Count >= dreamQuota) break;
            if (taken.Add(g.R.Id)) picked.Add(g);
        }

        // решта — лише звичайне житло: «мрій» понад квоту не добираємо, інакше курортне місто стало б самими віллами
        var rest = good.Where(g => !IsDream(g.R)).OrderBy(g => g.Usd).ToList();
        const int bands = 5;
        var need = take - picked.Count;
        for (var b = 0; b < bands && need > 0 && rest.Count > 0; b++)
        {
            var from = rest.Count * b / bands;
            var to = rest.Count * (b + 1) / bands;
            var band = rest.GetRange(from, to - from);
            Shuffle(band, rng);
            // смуги беруть порівну; остання добирає все, чого бракує
            var quota = b == bands - 1 ? need : (int)Math.Ceiling((take - picked.Count) / (double)(bands - b));
            foreach (var g in band.Take(Math.Min(quota, need)))
                if (taken.Add(g.R.Id)) { picked.Add(g); need--; }
        }

        return [.. picked.Select(g => new KhataExternal(g.R.Id, city.Region, city.Name, g.Usd, KhataFlat.Night, [g.R.Photo],
            "https://www.airbnb.com/rooms/" + g.R.Id, About(g.R), IsDream(g.R), KhataFlat.IsOdd(g.Usd, typical)))];
    }

    static void Shuffle<T>(List<T> list, Random rng)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
