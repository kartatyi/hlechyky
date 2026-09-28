using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Заготовки Дядька Глека, коли він сідає третім за стіл «Шпигуна» удвох (<c>data/spy/glek.json</c>):
/// <c>hints</c> — правдиві, але туманні відповіді за локацією (без назви місця й без ролей), <c>vague</c> — відповіді
/// «ні про що» для будь-якої локації (так відповідає Глек-шпигун), <c>ask</c> — його питання людям. Вантажиться раз на
/// процес і ніколи не кидає: нема файла чи кривий JSON — вбудований мінімум, і Глек відповідає лише туманно.
/// </summary>
public sealed class SpyBot
{
    public const string FileName = "data/spy/glek.json";

    public IReadOnlyList<string> Ask { get; }
    public IReadOnlyList<string> Vague { get; }
    public IReadOnlyDictionary<string, string[]> Hints { get; }

    public SpyBot(IReadOnlyList<string> ask, IReadOnlyList<string> vague, IReadOnlyDictionary<string, string[]> hints)
    {
        Ask = ask.Count > 0 ? ask : Fallback.Ask;
        Vague = vague.Count > 0 ? vague : Fallback.Vague;
        Hints = hints;
    }

    static readonly SpyBot Fallback = new(
        ["Що тут найчастіше чути?", "Тут дорого?", "Сюди ходять компанією?", "Чим тут пахне?"],
        ["Тут буває людно, а буває й порожньо.", "Хто тут бував, той зрозуміє.", "Тут кожен знає своє діло."],
        new Dictionary<string, string[]>());

    static readonly Lazy<SpyBot> Cached = new(() => Load(Paths.Resolve(FileName)));

    public static SpyBot Default => Cached.Value;

    public static SpyBot Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return Fallback;
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            var hints = new Dictionary<string, string[]>(StringComparer.Ordinal);
            if (root.TryGetProperty("hints", out var h) && h.ValueKind == JsonValueKind.Object)
                foreach (var p in h.EnumerateObject())
                    if (Strings(p.Value) is { Count: > 0 } list) hints[p.Name] = [.. list];
            return new SpyBot(Strings(root.TryGetProperty("ask", out var a) ? a : default),
                Strings(root.TryGetProperty("vague", out var v) ? v : default), hints);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return Fallback;
        }
    }

    static List<string> Strings(JsonElement e)
    {
        var list = new List<string>();
        if (e.ValueKind != JsonValueKind.Array) return list;
        foreach (var x in e.EnumerateArray())
            if (x.ValueKind == JsonValueKind.String && x.GetString()?.Trim() is { Length: > 0 and <= 160 } s) list.Add(s);
        return list;
    }
}
