using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Одне питання «Як облупленого»: до героя на «ти» й два полюси шкали. <see cref="Author"/> — ключ ніка того, хто
/// дописав його за столом (✍ своє питання); у питань банку — null.
/// </summary>
public sealed record OblupQuestion(string Id, string Q, string L, string R, IReadOnlyList<string> Tags, bool Spicy, string? Author = null);

/// <summary>
/// Банк питань із <c>data/oblup/questions.json</c> (specs/oblup.md §2). Читається раз на процес і <b>ніколи не кидає</b>:
/// нема файла чи JSON кривий — банк порожній, і гра чесно скаже, що партії не буде. Записи без <c>id</c>/<c>q</c>/полюсів,
/// задовгі й з повторним <c>id</c> мовчки відкидаються — одна помилка редактора не гасить решту банку.
/// </summary>
public static class OblupBank
{
    /// <summary>Де лежить банк відносно кореня сайту.</summary>
    public const string FileName = "data/oblup/questions.json";
    /// <summary>Довше питання на телефоні вже займає пів картки.</summary>
    public const int MaxQ = 90;
    /// <summary>Полюс підписує край шкали — довгий не влізе під неї.</summary>
    public const int MaxPole = 32;

    static readonly Lazy<(IReadOnlyList<OblupQuestion> List, string? Problem)> Cached = new(() =>
    {
        var list = Load(Paths.Resolve(FileName), out var problem);
        return (list, problem);
    });

    /// <summary>Усі придатні питання банку.</summary>
    public static IReadOnlyList<OblupQuestion> All => Cached.Value.List;

    /// <summary>Що було не так із файлом; null — усе гаразд.</summary>
    public static string? Problem => Cached.Value.Problem;

    /// <summary>Прочитати банк із конкретного файла (тести так і роблять). Не кидає нічого.</summary>
    public static IReadOnlyList<OblupQuestion> Load(string path, out string? problem)
    {
        try
        {
            if (!File.Exists(path))
            {
                problem = $"нема файла {path}";
                return [];
            }
            return Parse(File.ReadAllText(path), out problem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            problem = $"не прочитався {path}: {ex.Message}";
            return [];
        }
    }

    /// <summary>Розібрати текст банку: об'єкт із полем <c>questions</c> або одразу масив.</summary>
    public static IReadOnlyList<OblupQuestion> Parse(string json, out string? problem)
    {
        problem = null;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            var root = doc.RootElement;
            var array = root.ValueKind == JsonValueKind.Array ? root
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("questions", out var p) ? p
                : default;
            if (array.ValueKind != JsonValueKind.Array)
            {
                problem = "у файлі нема масиву questions";
                return [];
            }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<OblupQuestion>();
            var dropped = 0;
            foreach (var e in array.EnumerateArray())
            {
                if (Read(e) is { } q && ids.Add(q.Id)) list.Add(q);
                else dropped++;
            }
            if (dropped > 0) problem = $"відкинуто кривих записів: {dropped}";
            return list;
        }
        catch (JsonException ex)
        {
            problem = "кривий JSON: " + ex.Message;
            return [];
        }
    }

    static OblupQuestion? Read(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var id = Str(e, "id")?.Trim();
        var q = Squeeze(Str(e, "q"));
        var l = Squeeze(Str(e, "l"));
        var r = Squeeze(Str(e, "r"));
        if (string.IsNullOrEmpty(id) || !Fits(q, l, r)) return null;
        var tags = new List<string>();
        if (e.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array)
            foreach (var x in t.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } tag) tags.Add(tag.Trim());
        var spicy = e.TryGetProperty("spicy", out var s) && s.ValueKind == JsonValueKind.True;
        return new OblupQuestion(id, q, l, r, tags, spicy);
    }

    /// <summary>Чи влазить питання на картку: те саме правило для банку й для своїх питань за столом.</summary>
    public static bool Fits(string q, string l, string r) =>
        q.Length is > 0 and <= MaxQ && l.Length is > 0 and <= MaxPole && r.Length is > 0 and <= MaxPole;

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Пробіли всередині — по одному, краї — геть.</summary>
    public static string Squeeze(string? raw) =>
        string.Join(' ', (raw ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>Підміна банку для тестів: якщо такий сервіс є в контейнері, гра бере питання з нього.</summary>
public sealed class OblupQuestions(IReadOnlyList<OblupQuestion> list)
{
    public IReadOnlyList<OblupQuestion> List { get; } = list;
}
