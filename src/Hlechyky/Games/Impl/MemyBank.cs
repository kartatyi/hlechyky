using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Один мем банку «Мемології» (specs/memy.md §4 — контракт): що показати, що зараховувати й що розповісти на розкритті.
/// </summary>
/// <param name="Crop">null або [x, y, w, h] — частки 0–1 від ширини й висоти оригіналу.</param>
public sealed record MemyMeme(
    string Id, string Cat, string Name, string[] Names, string? Phrase, string[] Phrases, int Year, int Difficulty,
    string[] Tags, string About, string ImageUrl, string ImagePage, double[]? Crop, string[] Sources)
{
    /// <summary>Чи годиться мем для режиму «Підпис»: канонічна фраза є (і її нема на картинці).</summary>
    public bool HasPhrase => Phrase is not null;

    /// <summary>Що зараховується в режимі «Назву»: назва, інші назви — і фраза теж (знаєш фразу — знаєш мем).</summary>
    public IEnumerable<string> NameAnswers => [Name, .. Names, .. PhraseAnswers];

    /// <summary>Що зараховується в режимі «Підпис»: канонічна фраза й інші її написання.</summary>
    public IEnumerable<string> PhraseAnswers => Phrase is null ? [] : [Phrase, .. Phrases];
}

/// <summary>Категорії мемів (опція <c>cat</c>, поле <c>cat</c> банку).</summary>
public static class MemyCats
{
    public const string All = "all", World = "world", Ua = "ua", Old = "old";

    public static readonly IReadOnlyList<(string Key, string Label)> List =
    [
        (All, "Усі"),
        (World, "🌍 Світові"),
        (Ua, "🇺🇦 Українські"),
        (Old, "🕰 Старий інтернет"),
    ];

    public static bool Known(string cat) => cat is World or Ua or Old;

    /// <summary>Префікс id за категорією: <c>w001</c> світові, <c>u001</c> українські, <c>o001</c> старий інтернет.</summary>
    public static char Prefix(string cat) => cat switch { World => 'w', Ua => 'u', _ => 'o' };

    /// <summary>Обрані категорії з опції («world,old»). Порожнє чи «усі» — усі три.</summary>
    public static IReadOnlySet<string> Parse(string? option)
    {
        var picked = GameOption.Split(option);
        var known = picked.Where(Known).ToHashSet(StringComparer.Ordinal);
        if (picked.Contains(All) || known.Count == 0) return new HashSet<string>([World, Ua, Old], StringComparer.Ordinal);
        return known;
    }
}

/// <summary>
/// Банк мемів із <c>data/memes/memes.json</c> (формат — specs/memy.md §4). Ніколи не кидає: нема файла чи битий JSON —
/// порожній банк і попередження в лог; кривий запис відкидається поодинці з рядком у лозі, решта грає. Картинок тут
/// нема — лише адреси; качає й нормалізує їх <see cref="MemyImages"/>.
/// </summary>
public sealed partial class MemyBank
{
    public const string FileName = "data/memes/memes.json";
    public const int NameMin = 2, NameMax = 60, AboutMax = 300, PhraseMax = 160, TagsMax = 3;

    public static readonly MemyBank Empty = new([], []);

    readonly Dictionary<string, MemyMeme> _byId;

    public MemyBank(IReadOnlyList<MemyMeme> memes, IReadOnlyList<string> problems)
    {
        Memes = memes;
        Problems = problems;
        _byId = memes.ToDictionary(m => m.Id, StringComparer.Ordinal);
    }

    public IReadOnlyList<MemyMeme> Memes { get; }

    /// <summary>Що відкинуто при читанні: «w017: about довше за 300 символів» — для логу й тестів.</summary>
    public IReadOnlyList<string> Problems { get; }

    public MemyMeme? Find(string id) => _byId.GetValueOrDefault(id);

    public static MemyBank Load(string path, ILogger? log)
    {
        try
        {
            if (!File.Exists(path))
            {
                log?.LogWarning("Мемологія: банку мемів {Path} нема — грати нема в що", path);
                return Empty;
            }
            return Parse(File.ReadAllText(path), log);
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Мемологія: банк мемів {Path} не прочитався", path);
            return Empty;
        }
    }

    public static MemyBank Parse(string json, ILogger? log)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            log?.LogWarning(ex, "Мемологія: банк мемів — битий JSON");
            return new MemyBank([], ["битий JSON"]);
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("memes", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
            {
                log?.LogWarning("Мемологія: у банку мемів нема масиву memes");
                return new MemyBank([], ["нема масиву memes"]);
            }
            var memes = new List<MemyMeme>();
            var problems = new List<string>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var n = 0;
            foreach (var e in arr.EnumerateArray())
            {
                n++;
                var id = e.ValueKind == JsonValueKind.Object ? Str(e, "id") : "";
                if (Read(e, out var why) is { } meme)
                {
                    if (!ids.Add(meme.Id)) { problems.Add($"{meme.Id}: повтор id"); continue; }
                    memes.Add(meme);
                }
                else problems.Add($"{(id.Length > 0 ? id : "#" + n.ToString(CultureInfo.InvariantCulture))}: {why}");
            }
            foreach (var p in problems) log?.LogWarning("Мемологія: відкинуто запис банку — {Problem}", p);
            return new MemyBank(memes, problems);
        }
    }

    [GeneratedRegex(@"^[wuo]\d{3,4}$")]
    private static partial Regex IdShape();

    /// <summary>Один запис за контрактом §4; null і пояснення — що саме не так.</summary>
    static MemyMeme? Read(JsonElement e, out string why)
    {
        why = "";
        if (e.ValueKind != JsonValueKind.Object) { why = "не об'єкт"; return null; }
        var id = Str(e, "id");
        var cat = Str(e, "cat");
        if (!IdShape().IsMatch(id)) { why = "id не за зразком w001 / u001 / o001"; return null; }
        if (!MemyCats.Known(cat)) { why = $"невідома категорія «{cat}»"; return null; }
        if (id[0] != MemyCats.Prefix(cat)) { why = $"префікс id не відповідає категорії {cat}"; return null; }
        var name = Str(e, "name");
        if (name.Length is < NameMin or > NameMax) { why = $"назва не {NameMin}–{NameMax} символів"; return null; }
        if (!Strings(e, "names", out var names)) { why = "names — не масив рядків"; return null; }

        string? phrase = null;
        if (e.TryGetProperty("phrase", out var ph) && ph.ValueKind != JsonValueKind.Null)
        {
            if (ph.ValueKind != JsonValueKind.String || (ph.GetString() ?? "").Trim() is not { Length: > 0 } p) { why = "phrase — не рядок і не null"; return null; }
            if (p.Length > PhraseMax) { why = $"фраза довша за {PhraseMax} символів"; return null; }
            phrase = p;
        }
        if (!Strings(e, "phrases", out var phrases)) { why = "phrases — не масив рядків"; return null; }
        if (phrase is null && phrases.Length > 0) { why = "phrases без phrase"; return null; }

        if (Int(e, "year") is not { } year || year is < 1970 or > 2100) { why = "рік — не ціле 1970–2100"; return null; }
        if (Int(e, "difficulty") is not { } d || d is < 1 or > 3) { why = "складність не 1–3"; return null; }
        if (!Strings(e, "tags", out var tags) || tags.Length is < 1 or > TagsMax) { why = $"tags — 1–{TagsMax} рядки"; return null; }
        var about = Str(e, "about");
        if (about.Length == 0) { why = "нема about"; return null; }
        if (about.Length > AboutMax) { why = $"about довше за {AboutMax} символів"; return null; }

        if (!e.TryGetProperty("image", out var img) || img.ValueKind != JsonValueKind.Object) { why = "нема image"; return null; }
        var url = Str(img, "url");
        var page = Str(img, "page");
        if (!Https(url)) { why = "image.url — не https-адреса"; return null; }
        if (!Https(page)) { why = "image.page — не https-адреса"; return null; }

        double[]? crop = null;
        if (e.TryGetProperty("crop", out var cr) && cr.ValueKind != JsonValueKind.Null)
        {
            crop = Crop(cr);
            if (crop is null) { why = "crop — не [x, y, w, h] у частках 0–1 у межах картинки"; return null; }
        }

        if (!Strings(e, "sources", out var sources) || sources.Length == 0) { why = "нема sources"; return null; }
        if (sources.FirstOrDefault(s => !Https(s)) is { } bad) { why = $"джерело не https: {bad}"; return null; }

        return new MemyMeme(id, cat, name, names, phrase, phrases, year, d, tags, about, url, page, crop, sources);
    }

    /// <summary>[x, y, w, h]: частки 0–1, ширина й висота більші за нуль, і рамка не вилазить за картинку.</summary>
    static double[]? Crop(JsonElement cr)
    {
        if (cr.ValueKind != JsonValueKind.Array || cr.GetArrayLength() != 4) return null;
        var v = new double[4];
        var i = 0;
        foreach (var x in cr.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Number || !x.TryGetDouble(out var d) || d is < 0 or > 1 || double.IsNaN(d)) return null;
            v[i++] = d;
        }
        const double Slack = 1e-6;
        if (v[2] <= 0.01 || v[3] <= 0.01 || v[0] + v[2] > 1 + Slack || v[1] + v[3] > 1 + Slack) return null;
        return v;
    }

    /// <summary>Абсолютна https-адреса без чужого порту.</summary>
    public static bool Https(string url) =>
        url.Length > 0 && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort;

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

    static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    /// <summary>Масив непорожніх рядків (порожні викидає); поля нема чи null — порожній масив; не масив рядків — false.</summary>
    static bool Strings(JsonElement e, string name, out string[] list)
    {
        list = [];
        if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return true;
        if (v.ValueKind != JsonValueKind.Array) return false;
        var got = new List<string>();
        foreach (var x in v.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.String) return false;
            if ((x.GetString() ?? "").Trim() is { Length: > 0 } s) got.Add(s);
        }
        list = [.. got];
        return true;
    }
}
