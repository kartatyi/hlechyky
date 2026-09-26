using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Одна локація «Шпигуна»: де всі (крім шпигуна) опинились і ким там можна бути. <see cref="Set"/> — набір для
/// опції столу (<c>ua</c> — наші, <c>classic</c> — класика настільної гри).
/// </summary>
public sealed record SpyLocation(string Id, string Title, string Icon, string Set, string[] Roles);

/// <summary>
/// Банк локацій із <c>data/spy/locations.json</c>. Читається один раз на процес (файл малий, а столів «Шпигуна»
/// може бути кілька). Ніколи не кидає: нема файла чи він битий — банк порожній, і гра чесно скаже, що партії не
/// буде. Кривий запис (без назви, з дивним id, з невідомим набором, з куцим списком ролей, повтор id) мовчки
/// відкидається — одна описка в JSON не має гасити всю гру.
/// <para>
/// Тести підкладають свій екземпляр через <c>Ctx.Services</c>; у проді там нічого нема, і гра бере <see cref="Default"/>
/// (рівно як <see cref="PictionaryWords"/>).
/// </para>
/// </summary>
public sealed partial class SpyLocations
{
    /// <summary>Де лежить банк відносно кореня репозиторію.</summary>
    public const string FileName = "data/spy/locations.json";

    /// <summary>«Усі» в опції столу: з рештою не поєднується (так каже <see cref="GameOption.Multi"/>).</summary>
    public const string AnySet = "all";

    /// <summary>Набори так, як їх бачить господар при «+ Стіл», у цьому самому порядку.</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> Sets =
    [
        (AnySet, "Усі"),
        ("ua", "Наші"),
        ("classic", "Класика"),
    ];

    /// <summary>Найменше ролей на локацію: менше — і двоє з трьох гравців були б «тим самим».</summary>
    public const int MinRoles = 3;

    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip };
    static readonly Lazy<SpyLocations> Cached = new(() => Load(Paths.Resolve(FileName)));

    /// <summary>Бойовий банк із репозиторію.</summary>
    public static SpyLocations Default => Cached.Value;

    [GeneratedRegex("^[a-z0-9-]{2,24}$")]
    private static partial Regex IdPattern();

    /// <summary>Усі придатні локації в порядку файла.</summary>
    public IReadOnlyList<SpyLocation> All { get; }

    public SpyLocations(IEnumerable<SpyLocation?> entries)
    {
        var list = new List<SpyLocation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (Clean(e) is not { } loc || !seen.Add(loc.Id)) continue;
            list.Add(loc);
        }
        All = list;
    }

    /// <summary>Скільки локацій у наборі (<see cref="AnySet"/> — усього).</summary>
    public int CountIn(string set) => set == AnySet ? All.Count : All.Count(l => l.Set == set);

    /// <summary>
    /// Пул локацій для обраних наборів у порядку файла. Порожній вибір чи «усі» серед обраних — увесь банк
    /// (каркас і так зводить опцію до цього, але гра не має на це спиратись).
    /// </summary>
    public List<SpyLocation> Pool(IReadOnlyList<string> sets)
    {
        var all = sets.Count == 0 || sets.Contains(AnySet);
        var pool = new List<SpyLocation>(All.Count);
        foreach (var l in All)
            if (all || sets.Contains(l.Set)) pool.Add(l);
        return pool;
    }

    /// <summary>Прочитати банк із файла.</summary>
    public static SpyLocations Load(string path)
    {
        try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : new SpyLocations([]); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new SpyLocations([]); }
    }

    /// <summary>Текст у форматі файла: об'єкт <c>{ locations: [...] }</c> або одразу масив. Битий JSON — порожній банк.</summary>
    public static SpyLocations Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            var array = root.ValueKind == JsonValueKind.Array ? root
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("locations", out var l) ? l
                : default;
            if (array.ValueKind != JsonValueKind.Array) return new SpyLocations([]);
            var list = new List<SpyLocation?>();
            // Записи читаємо по одному: запис із ролями-числами не має валити весь файл.
            foreach (var item in array.EnumerateArray())
            {
                try { list.Add(item.Deserialize<Raw>(Options) is { } raw ? raw.ToLocation() : null); }
                catch (JsonException) { /* кривий запис — мимо */ }
            }
            return new SpyLocations(list);
        }
        catch (JsonException) { return new SpyLocations([]); }
    }

    /// <summary>Запис у тому вигляді, в якому він лежить у файлі. Усе необов'язкове — перевіряє <see cref="Clean"/>.</summary>
    sealed class Raw
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Icon { get; set; }
        public string? Set { get; set; }
        public string?[]? Roles { get; set; }

        public SpyLocation ToLocation() => new(Id ?? "", Title ?? "", Icon ?? "", Set ?? "", [.. (Roles ?? []).Select(r => r ?? "")]);
    }

    /// <summary>Запис, придатний до гри, або null. Порожні ролі й повтори ролей викидаємо, пробіли підрізаємо.</summary>
    static SpyLocation? Clean(SpyLocation? e)
    {
        if (e is null) return null;
        var id = (e.Id ?? "").Trim();
        var title = (e.Title ?? "").Trim();
        var set = (e.Set ?? "").Trim();
        if (!IdPattern().IsMatch(id) || title.Length == 0) return null;
        if (set == AnySet || !Sets.Any(s => s.Value == set)) return null;
        var roles = (e.Roles ?? [])
            .Select(r => (r ?? "").Trim())
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (roles.Length < MinRoles) return null;
        var icon = string.IsNullOrWhiteSpace(e.Icon) ? "📍" : e.Icon.Trim();
        return new SpyLocation(id, title, icon, set, roles);
    }
}
