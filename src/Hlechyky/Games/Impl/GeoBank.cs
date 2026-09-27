using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Одне фото місця з Вікісховища: звідки качати й кого підписати після розкриття.</summary>
public sealed record GeoPhoto(string Title, string Url, string Author, string License, string LicenseUrl, string Page);

/// <summary>
/// Місце банку «Де це?» (формат <c>geo</c> з CONTENT-FORMATS.md). До розкриття з нього до клієнта не йде нічого —
/// лише непрозорий токен фото від <see cref="GeoPhotos"/>.
/// </summary>
public sealed record GeoPlace(string Id, string Name, string Region, string Cat, double Lat, double Lon,
    int Difficulty, string Wikidata, IReadOnlyList<GeoPhoto> Photos);

/// <summary>Категорії місць — ключі опції «Місця».</summary>
public static class GeoCats
{
    public const string All = "all";
    public static readonly IReadOnlyList<(string Key, string Label)> List =
    [
        (All, "Усі місця"),
        ("city", "Міста"),
        ("castle", "Замки й храми"),
        ("nature", "Природа"),
        ("village", "Села й побут"),
    ];

    public static bool Known(string cat) => cat is "city" or "castle" or "nature" or "village";

    /// <summary>Обрані категорії з опції («city,nature»). Порожнє чи «усі» — усі чотири.</summary>
    public static IReadOnlySet<string> Parse(string? option)
    {
        var picked = GameOption.Split(option);
        var known = picked.Where(Known).ToHashSet(StringComparer.Ordinal);
        if (picked.Contains(All) || known.Count == 0) return new HashSet<string>(["city", "castle", "nature", "village"], StringComparer.Ordinal);
        return known;
    }
}

/// <summary>
/// Банк місць із <c>data/geo/places.json</c>. Читається раз на процес (<see cref="Default"/>), ніколи не кидає:
/// нема файла чи битий JSON — порожній банк і попередження в лог; криві записи відкидаються поодинці.
/// Качати чуже з інтернету сервер не буде — фото лише з <c>upload.wikimedia.org</c> і <c>thumb.wikimedia.org</c>.
/// </summary>
public sealed class GeoBank
{
    public const string FileName = "data/geo/places.json";

    static readonly Lazy<GeoBank> Lazy = new(() => Load(Paths.Resolve(FileName), null));

    /// <summary>Банк сервера (читається при першому зверненні).</summary>
    public static GeoBank Default => Lazy.Value;

    public static readonly GeoBank Empty = new([], []);

    readonly Dictionary<string, GeoPlace> _byId;

    public GeoBank(IReadOnlyList<GeoPlace> places, IReadOnlyList<string> problems)
    {
        Places = places;
        Problems = problems;
        _byId = places.ToDictionary(p => p.Id, StringComparer.Ordinal);
    }

    public IReadOnlyList<GeoPlace> Places { get; }
    /// <summary>Що відкинуто при читанні й чому — для тестів і логу.</summary>
    public IReadOnlyList<string> Problems { get; }

    public GeoPlace? Find(string id) => _byId.GetValueOrDefault(id);

    public static GeoBank Load(string path, ILogger? log)
    {
        try
        {
            if (!File.Exists(path))
            {
                log?.LogWarning("«Де це?»: банку місць {Path} нема — грати нема в що", path);
                return Empty;
            }
            return Parse(File.ReadAllText(path), log);
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "«Де це?»: банк місць {Path} не прочитався", path);
            return Empty;
        }
    }

    public static GeoBank Parse(string json, ILogger? log)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            log?.LogWarning(ex, "«Де це?»: банк місць — битий JSON");
            return new GeoBank([], ["битий JSON"]);
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("places", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
                return new GeoBank([], ["нема масиву places"]);
            var places = new List<GeoPlace>();
            var problems = new List<string>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var n = 0;
            foreach (var e in arr.EnumerateArray())
            {
                n++;
                var id = e.ValueKind == JsonValueKind.Object ? Str(e, "id") : "";
                if (Read(e, out var why) is { } place)
                {
                    if (!ids.Add(place.Id)) { problems.Add($"{place.Id}: повтор id"); continue; }
                    places.Add(place);
                }
                else problems.Add($"{(id.Length > 0 ? id : "#" + n)}: {why}");
            }
            foreach (var p in problems) log?.LogWarning("«Де це?»: відкинуто запис банку — {Problem}", p);
            return new GeoBank(places, problems);
        }
    }

    static GeoPlace? Read(JsonElement e, out string why)
    {
        why = "";
        if (e.ValueKind != JsonValueKind.Object) { why = "не об'єкт"; return null; }
        var id = Str(e, "id");
        var name = Str(e, "name");
        var cat = Str(e, "cat");
        if (id.Length == 0) { why = "нема id"; return null; }
        if (name.Length == 0) { why = "нема назви"; return null; }
        if (!GeoCats.Known(cat)) { why = $"невідома категорія «{cat}»"; return null; }
        if (Num(e, "lat") is not { } lat || lat < 44.0 || lat > 52.6) { why = "широта поза Україною"; return null; }
        if (Num(e, "lon") is not { } lon || lon < 22.0 || lon > 40.5) { why = "довгота поза Україною"; return null; }
        if (Num(e, "difficulty") is not { } d || (d != 1 && d != 2 && d != 3)) { why = "складність не 1–3"; return null; }
        var photos = new List<GeoPhoto>();
        if (e.TryGetProperty("photos", out var ph) && ph.ValueKind == JsonValueKind.Array)
            foreach (var p in ph.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                // Посилання йдуть у <a href> під фото — лише https (і сторінка файла — лише на Вікісховищі),
                // інакше порожньо: «javascript:…» чи чужий хост у банку не стане клікабельним.
                var photo = new GeoPhoto(Str(p, "title"), Str(p, "url"), Str(p, "author"), Str(p, "license"),
                    Link(Str(p, "licenseUrl"), null), Link(Str(p, "page"), "commons.wikimedia.org"));
                if (!GoodHost(photo.Url) || photo.Title.Length == 0 || photo.Author.Length == 0 || photo.License.Length == 0) continue;
                photos.Add(photo);
            }
        if (photos.Count == 0) { why = "нема жодного придатного фото (лише https з upload/thumb.wikimedia.org, з назвою, автором і ліцензією)"; return null; }
        return new GeoPlace(id, name, Str(e, "region"), cat, lat, lon, (int)d, Str(e, "wikidata"), photos);
    }

    /// <summary>
    /// Посилання для підпису: абсолютне https без порту (або <paramref name="host"/>, коли він названий);
    /// <c>http://creativecommons.org/…</c> (так його пишуть старі шаблони Вікісховища) підтягуємо до https.
    /// Усе інше — порожній рядок: клієнт тоді покаже автора й ліцензію текстом, без посилання.
    /// </summary>
    public static string Link(string url, string? host)
    {
        if (url.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var u) || !u.IsDefaultPort) return "";
        if (u.Scheme == Uri.UriSchemeHttp && u.Host == "creativecommons.org" && host is null)
            return "https://" + url["http://".Length..];
        if (u.Scheme != Uri.UriSchemeHttps) return "";
        return host is null || u.Host == host ? url : "";
    }

    /// <summary>Лише https і лише два хости Вікімедіа — жодних довільних адрес.</summary>
    public static bool GoodHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && (u.Host == "upload.wikimedia.org" || u.Host == "thumb.wikimedia.org") && u.IsDefaultPort;

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

    static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
}
