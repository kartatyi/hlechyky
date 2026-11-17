using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Оренда квартир з OLX для «Здам хату» — рішення власника сайту 17.11.2026: гра внутрішня, для вечора з друзями, і
/// саме на OLX найбільше «дивних ремонтів». Правила OLX не дозволяють збирати оголошення для показу на інших сайтах,
/// тож це тримається в межах «гортаємо OLX разом»: набір лише по кнопці розробника й невеликий (десятки запитів із
/// паузами), живе тиждень (<see cref="Life"/>) і зникає з бази, іде тільки за столи, де всі з акаунтами, глядачам не
/// показується. Фото не копіюємо (показуємо з сервера OLX), імен, телефонів, заголовків і описів не беремо; на
/// розкритті — посилання на саме оголошення. Тут лише чисті речі: адреси запитів, розбір і відбір. Мережа — у <see cref="KhataImport"/>.
/// Крім оренди квартир за місяць, тим самим набором беремо «хати мрії» з України — подобові будинки за темами
/// (<see cref="Themes"/>: замок, озеро, A-frame…), по сторінці на тему, ціна за добу. Заголовок там читаємо лише щоб
/// звірити тему — у банк він не йде.
/// </summary>
public static partial class KhataOlx
{
    public const string Api = "https://www.olx.ua/api/v1/offers/";
    /// <summary>Категорія OLX «довгострокова оренда квартир».</summary>
    public const int Category = 1760;
    /// <summary>Області в OLX мають номери 1…25.</summary>
    public const int Regions = 25;
    public const int PageSize = 40;
    /// <summary>Скільки оголошень беремо з області за один набір: по третині — з найдешевших, найдорожчих і свіжих.</summary>
    public const int PerRegion = 24;
    /// <summary>«Спершу дешеві» — від цієї ціни: нижче самі заглушки на кшталт «1 грн, ціну скажу».</summary>
    public const int CheapFrom = 2500;
    public const int MaxPhotos = 12;
    /// <summary>Ціна за місяць у гривнях, у межах якої це схоже на оренду квартири.</summary>
    public const int MinUah = 1500, MaxUah = 400_000;
    /// <summary>
    /// Сторінка «найдорожчі» повна продажу, що заблукав у оренду: «1 кімната, 35 м², 386 964 грн» — це ціна квартири,
    /// а «100 м² за 109 260 грн» — ціна квадрата в доларах. Тому оренду, дорожчу за <see cref="DearFrom"/> на місяць,
    /// беремо, лише коли квадрат коштує не більше за стелю (у Києві вона вища: там справді здають пентхауси), а без
    /// площі — не беремо зовсім.
    /// </summary>
    public const int DearFrom = 60_000, MaxUahPerM2 = 1000, MaxUahPerM2Kyiv = 2500;
    /// <summary>Скільки набране оголошення живе в грі.</summary>
    public static readonly TimeSpan Life = TimeSpan.FromDays(7);

    /// <summary>Категорія OLX «будинки подобово».</summary>
    public const int DayCategory = 3710;
    /// <summary>
    /// Ціна за добу в гривнях, у межах якої це схоже на подобову оренду цілого будинку. Дешевше за тисячу — це майже
    /// завжди «з людини» (котедж на вісім кімнат за 500 грн): у грі така ціна була б не несподіванкою, а обманом.
    /// </summary>
    public const int MinDayUah = 1000, MaxDayUah = 250_000;
    /// <summary>Скільки оголошень беремо з однієї теми «хат мрії» за набір.</summary>
    public const int PerTheme = 12;

    /// <summary>
    /// Тема «хат мрії» з України: що шукаємо серед подобових будинків і за якими словами в заголовку впізнаємо, що
    /// знайшлося саме воно (пошук OLX чіпляється й за опис: «неподалік замок» — ще не замок).
    /// </summary>
    public sealed record Theme(string Query, string[] Words);

    public static readonly IReadOnlyList<Theme> Themes =
    [
        new("замок", ["замок", "замк", "castle"]),
        new("біля озера", ["озер"]),
        new("a-frame", ["a-frame", "aframe", "a frame", "a- frame", "a-freme", "а-фрейм", "афрейм"]),
        new("шале", ["шале", "chalet"]),
        new("будинок на дереві", ["на дереві", "на деревах", "на дереве"]),
        new("купольний будинок", ["купол", "глемпінг", "глемпинг", "глэмпинг"]),
        new("біля моря", ["море", "моря", "морі", "морем"]),
        new("садиба", ["садиб", "маєт", "усадьб"]),
        new("вілла", ["вілл", "вилл", "villa"]),
        new("будинок на воді", ["на воді", "на воде", "плавуч"]),
        new("будинок в лісі", ["в лісі", "у лісі", "в лесу", "лісов"]),
        new("панорамний", ["панорам"]),
    ];

    /// <summary>Скільки кроків у наборі: теми «хат мрії» й області.</summary>
    public static int Steps => Themes.Count + Regions;

    /// <summary>Подобові будинки за темою: одна сторінка.</summary>
    public static string DayUrl(Theme theme) =>
        $"{Api}?offset=0&limit={Num(PageSize)}&category_id={Num(DayCategory)}&currency=UAH&query={Uri.EscapeDataString(theme.Query)}";

    /// <summary>Яку сторінку області просимо: свіжі, найдешевші чи найдорожчі.</summary>
    public enum Page { Fresh, Cheap, Dear }

    /// <summary>Оголошення області: одна сторінка кожного виду, не більше.</summary>
    public static string Url(int region, Page page) =>
        $"{Api}?offset=0&limit={Num(PageSize)}&category_id={Num(Category)}&currency=UAH&region_id={Num(region)}" + page switch
        {
            Page.Cheap => $"&sort_by=filter_float_price%3Aasc&filter_float_price%3Afrom={Num(CheapFrom)}",
            Page.Dear => $"&sort_by=filter_float_price%3Adesc&filter_float_price%3Ato={Num(MaxUah)}",
            _ => "&sort_by=created_at%3Adesc",
        };

    static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>Оголошення з відповіді: квартира для банку й чи це агенція (приватні цікавіші — їх беремо першими).</summary>
    public sealed record Offer(KhataExternal Flat, bool Business);

    [GeneratedRegex(@"^https://[a-z0-9.-]+\.olxcdn\.com(?::443)?(/v1/files/[A-Za-z0-9_-]+/image)(?:;.*)?$")]
    private static partial Regex PhotoLink();

    /// <summary>Шаблон фото з відповіді (<c>…/image;s={width}x{height}</c>) → адреса без розміру; розмір додає <see cref="KhataSources.Show"/>.</summary>
    public static string? Photo(string? link)
    {
        if (string.IsNullOrEmpty(link)) return null;
        var m = PhotoLink().Match(link);
        if (!m.Success) return null;
        var host = new Uri(link[..link.IndexOf("/v1/", StringComparison.Ordinal)]).Host;
        return $"https://{host}{m.Groups[1].Value}";
    }

    /// <summary>
    /// Відповідь OLX <c>{ data: [ … ] }</c> → оголошення, які годяться: активне, з цієї категорії, з містом, справжньою
    /// ціною (не «договірна») у межах місячної оренди, хоч одним фото й посиланням на olx.ua. З оголошення беремо лише
    /// місто, ціну, фото, посилання та сухі факти для розкриття (кімнати, площа, поверх, ремонт).
    /// </summary>
    public static List<Offer> Parse(string json) => Parse(json, null);

    /// <summary>
    /// Те саме для подобових будинків теми: ціна — за добу (тут і «договірна» з числом — ціна: так стоїть у кожному
    /// третьому), до села дописуємо область, і лишаємо тільки ті, де тему підтверджує заголовок. Усе — «хати мрії».
    /// </summary>
    public static List<Offer> ParseDay(string json, Theme theme) => Parse(json, theme);

    static List<Offer> Parse(string json, Theme? theme)
    {
        var list = new List<Offer>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array) return list;
            foreach (var o in data.EnumerateArray())
                if (One(o, theme) is { } offer) list.Add(offer);
        }
        catch (JsonException) { }
        return list;
    }

    static Offer? One(JsonElement o, Theme? theme)
    {
        if (o.ValueKind != JsonValueKind.Object) return null;
        var day = theme is not null;
        if (!o.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id) || id <= 0) return null;
        if (Str(o, "status") is { } status && status != "active") return null;
        if (o.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.Object && cat.TryGetProperty("id", out var catId)
            && catId.TryGetInt32(out var c) && c != (day ? DayCategory : Category)) return null;
        var url = Str(o, "url");
        if (url is null || !url.StartsWith("https://www.olx.ua/", StringComparison.Ordinal) || url.Any(char.IsWhiteSpace)) return null;
        // Тему звіряємо із заголовком — і все: сам заголовок далі не йде.
        if (theme is not null && (Str(o, "title") is not { } title || !theme.Words.Any(w => title.Contains(w, StringComparison.OrdinalIgnoreCase)))) return null;
        if (!o.TryGetProperty("location", out var loc) || loc.ValueKind != JsonValueKind.Object || !loc.TryGetProperty("city", out var cityEl)
            || cityEl.ValueKind != JsonValueKind.Object || KhataStore.CleanCity(Str(cityEl, "name")) is not { } city) return null;
        if (day) city = Place(city, loc.TryGetProperty("region", out var regEl) && regEl.ValueKind == JsonValueKind.Object ? Str(regEl, "name") : null);

        int? price = null;
        string? rooms = null, area = null, repair = null, kind = null, beds = null;
        double m2 = 0;
        long? floor = null, floors = null;
        if (o.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Array)
            foreach (var p in ps.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Object) continue;
                switch (Str(p, "key"))
                {
                    case "price": price = Uah(v, arranged: day); break;
                    case "number_of_rooms": rooms = Rooms(v); break;
                    case "property_type_houses": kind = Label(v); break;
                    case "sleep_places": beds = Label(v); break;
                    case "number_of_rooms_string": rooms = Label(v); break;
                    case "total_area":
                        area = Area(v) ?? Label(v);
                        if (Str(v, "key") is { } sq && double.TryParse(sq, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n2)) m2 = n2;
                        break;
                    case "repair": repair = Label(v); break;
                    case "floor": floor = Key(v); break;
                    case "total_floors": floors = Key(v); break;
                }
            }
        if (price is not { } uah || uah < (day ? MinDayUah : MinUah) || uah > (day ? MaxDayUah : MaxUah)) return null;
        if (!day && uah > DearFrom && !(m2 > 0 && uah <= m2 * (city == "Київ" ? MaxUahPerM2Kyiv : MaxUahPerM2))) return null;

        var photos = new List<string>();
        if (o.TryGetProperty("photos", out var ph) && ph.ValueKind == JsonValueKind.Array)
            foreach (var x in ph.EnumerateArray())
                if (photos.Count < MaxPhotos && x.ValueKind == JsonValueKind.Object && Photo(Str(x, "link")) is { } photo && !photos.Contains(photo))
                    photos.Add(photo);
        if (photos.Count == 0) return null;

        var parts = new List<string>();
        if (kind is not null) parts.Add(kind.ToLowerInvariant());
        if (rooms is not null) parts.Add(rooms);
        if (area is not null) parts.Add(area);
        if (beds is not null) parts.Add("спальних місць: " + beds);
        if (floor is > 0 and < 200) parts.Add(floors is { } all && all >= floor && all < 200 ? $"{Num(floor.Value)} поверх із {Num(all)}" : $"{Num(floor.Value)} поверх");
        if (repair is not null) parts.Add("ремонт: " + repair.ToLowerInvariant());
        var business = o.TryGetProperty("business", out var b) && b.ValueKind == JsonValueKind.True;
        return new Offer(new KhataExternal(Num(id), KhataRegions.Ua, city, uah, day ? KhataFlat.Night : KhataFlat.Month, photos, url,
            parts.Count == 0 ? null : string.Join(" · ", parts), Dream: day), business);
    }

    /// <summary>Ціна в гривнях: або одразу, або перерахована самим OLX. «Договірна» — не ціна.</summary>
    static int? Uah(JsonElement v, bool arranged = false)
    {
        if (Str(v, "type") is { } type && type != "price" && !(arranged && type == "arranged")) return null;
        double? n = Str(v, "currency") == "UAH" ? Double(v, "value")
            : Str(v, "converted_currency") == "UAH" ? Double(v, "converted_value") : null;
        return n is > 0 and < int.MaxValue ? (int)Math.Round(n.Value, MidpointRounding.AwayFromZero) : null;
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static double? Double(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : null;

    /// <summary>Короткий підпис значення («3 кімнати», «69 м²», «Євроремонт») — без керівних символів і не довший за 40 знаків.</summary>
    static string? Label(JsonElement v)
    {
        var s = Str(v, "label");
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = new string([.. s.Where(ch => !char.IsControl(ch))]).Trim();
        return s.Length is > 0 and <= 40 ? s : null;
    }

    /// <summary>Площа по-людськи: «62,5 м²», а не «62.50 м²», як стоїть у підписі OLX.</summary>
    static string? Area(JsonElement v) =>
        Str(v, "key") is { } s && double.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var m2) && m2 is > 0 and < 5000
            ? m2.ToString("0.#", CultureInfo.InvariantCulture).Replace('.', ',') + " м²" : null;

    /// <summary>Кімнати подобового будинку: OLX дає голе число — робимо «3 кімнати».</summary>
    static string? Rooms(JsonElement v)
    {
        if (Label(v) is not { } s) return null;
        if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n is < 1 or > 99) return s + " кімн.";
        var word = n % 10 == 1 && n % 100 != 11 ? "кімната" : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? "кімнати" : "кімнат";
        return $"{Num(n)} {word}";
    }

    /// <summary>
    /// «Синевирська Поляна, Закарпатська обл.» — саме село нічого не каже, а гравець бачить лише це. Обласному центру
    /// («Київ» — «Київська область», «Одеса» — «Одеська») область не дописуємо: впізнаємо за спільним початком.
    /// </summary>
    static string Place(string city, string? region)
    {
        var area = (region ?? "").Trim();
        if (area.Length == 0 || area.StartsWith(city[..Math.Min(4, city.Length)], StringComparison.OrdinalIgnoreCase)) return city;
        const string full = " область";
        if (area.EndsWith(full, StringComparison.OrdinalIgnoreCase)) area = area[..^full.Length] + " обл.";
        var place = KhataStore.CleanCity($"{city}, {area}");
        return place ?? city;
    }

    static long? Key(JsonElement v) =>
        Str(v, "key") is { } s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>
    /// Що взяти з області: третину — з найдешевших (там і живуть «дивні ремонти»), третину — з найдорожчих («скільки-
    /// скільки?!»), решту — зі свіжих; у кожній частині спершу приватні оголошення, потім агенції. Знайомих банку не беремо.
    /// </summary>
    public static List<KhataExternal> Pick(IReadOnlyList<Offer> fresh, IReadOnlyList<Offer> cheap, IReadOnlyList<Offer> dear, int take,
        Random rng, Func<string, bool> known)
    {
        var picked = new List<KhataExternal>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        void Take(IReadOnlyList<Offer> from, int n)
        {
            var pool = from.Where(o => !known(o.Flat.ExtId) && !taken.Contains(o.Flat.ExtId)).ToList();
            for (var i = pool.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (pool[i], pool[j]) = (pool[j], pool[i]);
            }
            foreach (var o in pool.OrderBy(o => o.Business ? 1 : 0).Take(n))
                if (taken.Add(o.Flat.ExtId)) picked.Add(o.Flat);
        }
        Take(cheap, take / 3);
        Take(dear, take / 3);
        Take(fresh, take - picked.Count);
        if (picked.Count < take) Take(cheap, take - picked.Count);
        if (picked.Count < take) Take(dear, take - picked.Count);
        return picked;
    }

    /// <summary>
    /// «Хати мрії» з України: з кожної теми — до <paramref name="perTheme"/> незнайомих оголошень (приватні першими);
    /// одне оголошення — один раз, хоч би в скількох темах знайшлось. «Несподівана ціна» — відносно серединної ціни
    /// всього знайденого.
    /// </summary>
    public static List<KhataExternal> PickDay(IReadOnlyList<IReadOnlyList<Offer>> themes, int perTheme, Random rng, Func<string, bool> known)
    {
        var prices = themes.SelectMany(t => t).DistinctBy(o => o.Flat.ExtId).Select(o => o.Flat.Price).Order().ToList();
        var typical = prices.Count == 0 ? 0 : prices[prices.Count / 2];
        var picked = new List<KhataExternal>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var theme in themes)
        {
            var pool = theme.Where(o => !known(o.Flat.ExtId) && !taken.Contains(o.Flat.ExtId)).ToList();
            for (var i = pool.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (pool[i], pool[j]) = (pool[j], pool[i]);
            }
            foreach (var o in pool.OrderBy(o => o.Business ? 1 : 0).Take(perTheme))
                if (taken.Add(o.Flat.ExtId)) picked.Add(o.Flat with { Odd = KhataFlat.IsOdd(o.Flat.Price, typical) });
        }
        return picked;
    }
}
