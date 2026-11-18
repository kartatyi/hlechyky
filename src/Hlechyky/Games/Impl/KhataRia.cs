using System.Globalization;
using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Налаштування «Здам хату» (секція <c>Khata</c>). Ключ DIM.RIA — секрет: лише в <c>appsettings.Local.json</c>.</summary>
public sealed class KhataOptions
{
    /// <summary>Ключ API DIM.RIA (developers.ria.com). Порожній — українську оренду не набираємо.</summary>
    public string RiaKey { get; set; } = "";
    /// <summary>Скільки запитів на годину можна: безкоштовний пакет дає 30, беремо із запасом. Платний пакет — підняти.</summary>
    public int RiaPerHour { get; set; } = 28;
}

/// <summary>
/// Україна для «Здам хату» — довгострокова оренда квартир з офіційного API DIM.RIA (developers.ria.com): пошук віддає
/// номери оголошень міста, а «інформація по id» — місто, ціну, кімнати, площу, поверх, усі фото й посилання. Телефонів і
/// номерів будинків API не віддає. Умова RIA одна — посилання на DOM.RIA (стоїть на кожному розкритті й у лобі).
/// Тут лише чисті речі: каталог міст, адреси запитів і розбір відповідей. Мережа й темп — у <see cref="KhataImport"/>.
/// </summary>
public static class KhataRia
{
    public const string Api = "https://developers.ria.com/dom";
    public const string PhotoHost = "https://cdn.riastatic.com/photos/";
    public const string Site = "https://dom.ria.com/uk/";
    /// <summary>Скільки оголошень набираємо з міста, перш ніж іти далі по колу.</summary>
    public const int PerCity = 30;
    /// <summary>Коли місто вже набране — скільки свіжих добираємо за одне коло.</summary>
    public const int Refresh = 5;
    public const int MaxPhotos = 12;
    /// <summary>Ціна за місяць у гривнях, у межах якої це схоже на оренду квартири (а не на подобову чи одруківку).</summary>
    public const int MinUah = 1500, MaxUah = 400_000;

    /// <summary>Місто: номер у RIA (в обласних центрів він збігається з номером області) і назва.</summary>
    public sealed record City(int Id, string Name);

    /// <summary>Обласні центри. Номери — зі словника областей RIA; місто у відповіді звіряємо (<see cref="Flat"/>).</summary>
    public static readonly IReadOnlyList<City> Cities =
    [
        new(10, "Київ"), new(5, "Львів"), new(12, "Одеса"), new(7, "Харків"), new(11, "Дніпро"), new(1, "Вінниця"),
        new(2, "Житомир"), new(3, "Тернопіль"), new(4, "Хмельницький"), new(6, "Чернігів"), new(8, "Суми"), new(9, "Рівне"),
        new(14, "Запоріжжя"), new(15, "Івано-Франківськ"), new(16, "Кропивницький"), new(18, "Луцьк"), new(19, "Миколаїв"),
        new(20, "Полтава"), new(22, "Ужгород"), new(23, "Херсон"), new(24, "Черкаси"), new(25, "Чернівці"),
    ];

    /// <summary>Пошук: квартири, довгострокова оренда, це місто — перша сотня номерів.</summary>
    public static string SearchUrl(City city, string key) =>
        $"{Api}/search?category=1&realty_type=2&operation_type=3&operation=3&state_id={Num(city.Id)}&city_id={Num(city.Id)}&page=0&api_key={Uri.EscapeDataString(key)}";

    public static string InfoUrl(long id, string key) => $"{Api}/info/{Num(id)}?api_key={Uri.EscapeDataString(key)}";

    static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>Відповідь пошуку <c>{ "count": N, "items": [id, …] }</c> → номери оголошень.</summary>
    public static List<long> Ids(string searchJson)
    {
        var ids = new List<long>();
        try
        {
            using var doc = JsonDocument.Parse(searchJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("items", out var items)
                && items.ValueKind == JsonValueKind.Array)
                foreach (var e in items.EnumerateArray())
                    if (Long(e) is { } id && id > 0) ids.Add(id);
        }
        catch (JsonException) { }
        return ids;
    }

    /// <summary>
    /// Відповідь «інформація по id» → квартира банку; null — не годиться (не те місто, нема фото чи посилання, ціна не
    /// схожа на місячну оренду). Ціну беремо в гривнях: або одразу, або з перерахунку RIA (<c>priceArr</c>), або за курсом НБУ.
    /// </summary>
    public static KhataExternal? Flat(string infoJson, City city, IReadOnlyDictionary<string, double> uahPer)
    {
        try
        {
            using var doc = JsonDocument.Parse(infoJson);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            if (!r.TryGetProperty("realty_id", out var idEl) || Long(idEl) is not { } id || id <= 0) return null;
            // Місто — те, яке питали: назву пишемо свою, тож чуже місто під нею опинитись не має.
            if (!r.TryGetProperty("city_id", out var cityEl) || Long(cityEl) != city.Id) return null;
            if (r.TryGetProperty("realty_type_id", out var typeEl) && Long(typeEl) is { } type && type != 2) return null;
            if (Uah(r, uahPer) is not { } uah || uah < MinUah || uah > MaxUah) return null;
            var url = r.TryGetProperty("beautiful_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(url) || url.Contains("://", StringComparison.Ordinal) || url.Any(char.IsWhiteSpace)) return null;
            var photos = Photos(r);
            if (photos.Count == 0) return null;
            return new KhataExternal(Num(id), KhataRegions.Ua, city.Name, uah, KhataFlat.Month, photos, Site + url.TrimStart('/'), About(r));
        }
        catch (JsonException) { return null; }
    }

    static int? Uah(JsonElement r, IReadOnlyDictionary<string, double> uahPer)
    {
        // priceArr — ціна, яку RIA сама перерахувала в усі валюти; «3» — гривня (як price_cur у пошуку).
        if (r.TryGetProperty("priceArr", out var arr) && arr.ValueKind == JsonValueKind.Object && arr.TryGetProperty("3", out var p3)
            && Money(p3) is { } ready) return Whole(ready);
        if (!r.TryGetProperty("price", out var pEl) || Money(pEl) is not { } price) return null;
        var cur = r.TryGetProperty("currency_type", out var c) && c.ValueKind == JsonValueKind.String ? (c.GetString() ?? "").Trim().ToLowerInvariant() : "";
        return cur switch
        {
            "грн" or "грн." or "uah" or "₴" => Whole(price),
            "$" or "usd" => uahPer.TryGetValue("USD", out var usd) ? Whole(price * usd) : null,
            "€" or "eur" => uahPer.TryGetValue("EUR", out var eur) ? Whole(price * eur) : null,
            _ => null,
        };
    }

    static int? Whole(double v) => v is > 0 and < int.MaxValue ? (int)Math.Round(v, MidpointRounding.AwayFromZero) : null;

    /// <summary>Гроші з відповіді: число або рядок «25 000».</summary>
    static double? Money(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var n)) return n > 0 ? n : null;
        if (e.ValueKind != JsonValueKind.String) return null;
        var clean = new string([.. (e.GetString() ?? "").Where(ch => char.IsAsciiDigit(ch) || ch is '.' or ',')]).Replace(',', '.');
        return double.TryParse(clean, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
    }

    static long? Long(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number when e.TryGetInt64(out var n) => n,
        JsonValueKind.String when long.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
        _ => null,
    };

    /// <summary>
    /// Фото оголошення: головне першим, решта за номером. Адреса — файл із відповіді з «xl» перед розширенням (так велить
    /// документація RIA); беремо лише відносні шляхи до jpg без сюрпризів.
    /// </summary>
    static List<string> Photos(JsonElement r)
    {
        var files = new List<(long Id, string File)>();
        if (r.TryGetProperty("photos", out var ph) && ph.ValueKind == JsonValueKind.Object)
            foreach (var p in ph.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String
                    && Clean(f.GetString()) is { } file)
                    files.Add((p.Value.TryGetProperty("id", out var pid) && Long(pid) is { } n ? n : long.MaxValue, file));
        var main = r.TryGetProperty("main_photo", out var m) && m.ValueKind == JsonValueKind.String ? Clean(m.GetString()) : null;
        var ordered = files.OrderBy(x => x.File == main ? 0 : 1).ThenBy(x => x.Id).Select(x => x.File).Distinct().ToList();
        if (ordered.Count == 0 && main is not null) ordered.Add(main);
        return [.. ordered.Take(MaxPhotos).Select(f => PhotoHost + f[..^4] + "xl.jpg")];
    }

    static string? Clean(string? file)
    {
        if (string.IsNullOrEmpty(file) || !file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || file.Length > 200) return null;
        return file.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '/' or '.' or '_' or '-') && !file.Contains("..", StringComparison.Ordinal)
            ? file.TrimStart('/') : null;
    }

    /// <summary>«1 кімната», «2 кімнати», «5 кімнат».</summary>
    public static string Rooms(int n)
    {
        var d = n % 10;
        var h = n % 100;
        var word = d == 1 && h != 11 ? "кімната" : d is >= 2 and <= 4 && h is < 12 or > 14 ? "кімнати" : "кімнат";
        return $"{n.ToString(CultureInfo.InvariantCulture)} {word}";
    }

    /// <summary>Що це за квартира — для розкриття: «2 кімнати · 54 м² · 5 поверх із 9».</summary>
    static string? About(JsonElement r)
    {
        var parts = new List<string>();
        if (r.TryGetProperty("rooms_count", out var rooms) && Long(rooms) is { } n && n is > 0 and < 30) parts.Add(Rooms((int)n));
        if (r.TryGetProperty("total_square_meters", out var sq) && Money(sq) is { } m2 && m2 < 2000)
            parts.Add(m2.ToString("0.#", CultureInfo.InvariantCulture).Replace('.', ',') + " м²");
        if (r.TryGetProperty("floor", out var fl) && Long(fl) is { } floor && floor is > 0 and < 200)
            parts.Add(r.TryGetProperty("floors_count", out var fc) && Long(fc) is { } all && all >= floor && all < 200
                ? $"{Num(floor)} поверх із {Num(all)}" : $"{Num(floor)} поверх");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}
