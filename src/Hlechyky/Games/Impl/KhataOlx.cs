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
/// </summary>
public static partial class KhataOlx
{
    public const string Api = "https://www.olx.ua/api/v1/offers/";
    /// <summary>Категорія OLX «довгострокова оренда квартир».</summary>
    public const int Category = 1760;
    /// <summary>Області в OLX мають номери 1…25.</summary>
    public const int Regions = 25;
    public const int PageSize = 40;
    /// <summary>Скільки оголошень беремо з області за один набір: половину — зі свіжих, половину — з найдешевших.</summary>
    public const int PerRegion = 24;
    /// <summary>«Спершу дешеві» — від цієї ціни: нижче самі заглушки на кшталт «1 грн, ціну скажу».</summary>
    public const int CheapFrom = 2500;
    public const int MaxPhotos = 12;
    /// <summary>Ціна за місяць у гривнях, у межах якої це схоже на оренду квартири.</summary>
    public const int MinUah = 1500, MaxUah = 400_000;
    /// <summary>Скільки набране оголошення живе в грі.</summary>
    public static readonly TimeSpan Life = TimeSpan.FromDays(7);

    /// <summary>Оголошення області: свіжі або найдешевші. Більше за одну сторінку з області не беремо.</summary>
    public static string Url(int region, bool cheap) =>
        $"{Api}?offset=0&limit={Num(PageSize)}&category_id={Num(Category)}&currency=UAH&region_id={Num(region)}"
        + (cheap ? $"&sort_by=filter_float_price%3Aasc&filter_float_price%3Afrom={Num(CheapFrom)}" : "&sort_by=created_at%3Adesc");

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
    public static List<Offer> Parse(string json)
    {
        var list = new List<Offer>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array) return list;
            foreach (var o in data.EnumerateArray())
                if (One(o) is { } offer) list.Add(offer);
        }
        catch (JsonException) { }
        return list;
    }

    static Offer? One(JsonElement o)
    {
        if (o.ValueKind != JsonValueKind.Object) return null;
        if (!o.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id) || id <= 0) return null;
        if (Str(o, "status") is { } status && status != "active") return null;
        if (o.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.Object && cat.TryGetProperty("id", out var catId)
            && catId.TryGetInt32(out var c) && c != Category) return null;
        var url = Str(o, "url");
        if (url is null || !url.StartsWith("https://www.olx.ua/", StringComparison.Ordinal) || url.Any(char.IsWhiteSpace)) return null;
        if (!o.TryGetProperty("location", out var loc) || loc.ValueKind != JsonValueKind.Object || !loc.TryGetProperty("city", out var cityEl)
            || cityEl.ValueKind != JsonValueKind.Object || KhataStore.CleanCity(Str(cityEl, "name")) is not { } city) return null;

        int? price = null;
        string? rooms = null, area = null, repair = null;
        long? floor = null, floors = null;
        if (o.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Array)
            foreach (var p in ps.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Object) continue;
                switch (Str(p, "key"))
                {
                    case "price": price = Uah(v); break;
                    case "number_of_rooms_string": rooms = Label(v); break;
                    case "total_area": area = Area(v) ?? Label(v); break;
                    case "repair": repair = Label(v); break;
                    case "floor": floor = Key(v); break;
                    case "total_floors": floors = Key(v); break;
                }
            }
        if (price is not { } uah || uah < MinUah || uah > MaxUah) return null;

        var photos = new List<string>();
        if (o.TryGetProperty("photos", out var ph) && ph.ValueKind == JsonValueKind.Array)
            foreach (var x in ph.EnumerateArray())
                if (photos.Count < MaxPhotos && x.ValueKind == JsonValueKind.Object && Photo(Str(x, "link")) is { } photo && !photos.Contains(photo))
                    photos.Add(photo);
        if (photos.Count == 0) return null;

        var parts = new List<string>();
        if (rooms is not null) parts.Add(rooms);
        if (area is not null) parts.Add(area);
        if (floor is > 0 and < 200) parts.Add(floors is { } all && all >= floor && all < 200 ? $"{Num(floor.Value)} поверх із {Num(all)}" : $"{Num(floor.Value)} поверх");
        if (repair is not null) parts.Add("ремонт: " + repair.ToLowerInvariant());
        var business = o.TryGetProperty("business", out var b) && b.ValueKind == JsonValueKind.True;
        return new Offer(new KhataExternal(Num(id), KhataRegions.Ua, city, uah, KhataFlat.Month, photos, url,
            parts.Count == 0 ? null : string.Join(" · ", parts)), business);
    }

    /// <summary>Ціна в гривнях: або одразу, або перерахована самим OLX. «Договірна» — не ціна.</summary>
    static int? Uah(JsonElement v)
    {
        if (Str(v, "type") is { } type && type != "price") return null;
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

    static long? Key(JsonElement v) =>
        Str(v, "key") is { } s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>
    /// Що взяти з області: половину — з найдешевших (там і живуть «дивні ремонти»), половину — зі свіжих; у кожній
    /// половині спершу приватні оголошення, потім агенції. Знайомих банку не беремо.
    /// </summary>
    public static List<KhataExternal> Pick(IReadOnlyList<Offer> fresh, IReadOnlyList<Offer> cheap, int take, Random rng, Func<string, bool> known)
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
        Take(cheap, take / 2);
        Take(fresh, take - picked.Count);
        if (picked.Count < take) Take(cheap, take - picked.Count);
        return picked;
    }
}
