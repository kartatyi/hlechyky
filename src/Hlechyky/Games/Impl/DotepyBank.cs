using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Одне завдання «Дотепів»: дурне питання, на яке відповідають одним рядком і без «правильної» відповіді.
/// <see cref="Final"/> — годиться для «Останнього дотепу», де його пишуть усі разом.
/// </summary>
public sealed record DotepyPrompt(string Id, string Text, IReadOnlyList<string> Tags, bool Final);

/// <summary>
/// Банк завдань із <c>data/dotepy/prompts.json</c> (формат — <c>D:/or-wt/_wave2/CONTENT-FORMATS.md</c>,
/// specs/dotepy.md §7). Читається раз на процес і <b>ніколи не кидає</b>: нема файла чи JSON кривий — банк
/// порожній, а гра чесно скаже, що партії не буде. Записи без <c>id</c>/<c>text</c>, задовгі й з повторним
/// <c>id</c> мовчки відкидаються: одна помилка редактора не має гасити решту чотирьохсот завдань.
/// </summary>
public static class DotepyBank
{
    /// <summary>Де лежить банк відносно кореня сайту.</summary>
    public const string FileName = "data/dotepy/prompts.json";
    /// <summary>Довше за це завдання на картку не влізе (і Глек читатиме його пів хвилини).</summary>
    public const int MaxText = 120;

    static readonly Lazy<(IReadOnlyList<DotepyPrompt> List, string? Problem)> Cached = new(() =>
    {
        var list = Load(Paths.Resolve(FileName), out var problem);
        return (list, problem);
    });

    /// <summary>Усі придатні завдання банку.</summary>
    public static IReadOnlyList<DotepyPrompt> All => Cached.Value.List;

    /// <summary>Що було не так із файлом (для рядка в лозі на старті сервера); null — усе гаразд.</summary>
    public static string? Problem => Cached.Value.Problem;

    public static IReadOnlyList<DotepyPrompt> Load(string path) => Load(path, out _);

    /// <summary>Прочитати банк із конкретного файла (тести так і роблять). Не кидає нічого.</summary>
    public static IReadOnlyList<DotepyPrompt> Load(string path, out string? problem)
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

    /// <summary>
    /// Розібрати текст банку. Корінь — об'єкт із полем <c>prompts</c> (як у форматі) або одразу масив.
    /// </summary>
    public static IReadOnlyList<DotepyPrompt> Parse(string json, out string? problem)
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
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("prompts", out var p) ? p
                : default;
            if (array.ValueKind != JsonValueKind.Array)
            {
                problem = "у файлі нема масиву prompts";
                return [];
            }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<DotepyPrompt>();
            var dropped = 0;
            foreach (var e in array.EnumerateArray())
            {
                if (Read(e) is { } prompt && ids.Add(prompt.Id)) list.Add(prompt);
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

    static DotepyPrompt? Read(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var id = Str(e, "id")?.Trim();
        var text = Squeeze(Str(e, "text"));
        if (string.IsNullOrEmpty(id) || text.Length == 0 || text.Length > MaxText) return null;
        var tags = new List<string>();
        if (e.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array)
            foreach (var x in t.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } tag) tags.Add(tag.Trim());
        var final = e.TryGetProperty("final", out var f) && f.ValueKind == JsonValueKind.True;
        return new DotepyPrompt(id, text, tags, final);
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Пробіли всередині — по одному, краї — геть: редактор міг лишити подвійний пробіл чи перенос.</summary>
    static string Squeeze(string? raw) =>
        string.Join(' ', (raw ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>
/// Підміна банку для тестів і для майбутніх тем: якщо такий сервіс є в контейнері, гра бере завдання з нього,
/// а не з <see cref="DotepyBank.All"/>.
/// </summary>
public sealed class DotepyPrompts(IReadOnlyList<DotepyPrompt> list)
{
    public IReadOnlyList<DotepyPrompt> List { get; } = list;
}

/// <summary>
/// Які завдання вже грали на цьому сервері нещодавно (останні <see cref="Dotepy.SeenRing"/>) — щоб два столи
/// одного вечора не отримували ті самі дурниці. Лише в пам'яті процесу: пам'ять на нік — на потім, коли банк
/// виросте (specs/dotepy.md §12.9). Тести підкладають свій екземпляр через сервіси — спільний статичний
/// зробив би партії недетермінованими.
/// </summary>
public sealed class DotepySeen(int capacity)
{
    /// <summary>Один на процес — його й бере гра, якщо сервіс не підкладено.</summary>
    public static readonly DotepySeen Shared = new(Dotepy.SeenRing);

    readonly object _lock = new();
    readonly Dictionary<string, long> _stamp = new(StringComparer.Ordinal);
    long _clock;

    public int Capacity { get; } = Math.Max(1, capacity);

    /// <summary>Ці завдання щойно пішли на стіл. Найстаріші випадають, коли пам'ять повна.</summary>
    public void Mark(IEnumerable<string> ids)
    {
        lock (_lock)
        {
            foreach (var id in ids)
            {
                _stamp[id] = ++_clock;
                while (_stamp.Count > Capacity)
                {
                    string? oldest = null;
                    var min = long.MaxValue;
                    foreach (var (k, v) in _stamp)
                        if (v < min) { min = v; oldest = k; }
                    if (oldest is null) break;
                    _stamp.Remove(oldest);
                }
            }
        }
    }

    /// <summary>
    /// Знімок того, що пам'ятаємо: id → штамп (більший — пізніше). Копія — щоб не тримати замок, поки гра тасує пул;
    /// штамп — щоб повторювати спершу давно бачене, а не щойно зігране сусіднім столом.
    /// </summary>
    public Dictionary<string, long> Snapshot()
    {
        lock (_lock) return new Dictionary<string, long>(_stamp, StringComparer.Ordinal);
    }
}

/// <summary>
/// Підставні відповіді для тих, хто не встиг (specs/dotepy.md §2.6). За них голосують як за звичайні — люди
/// люблять «Мій кіт сів на клавіатуру», — але очок автор не отримує: інакше було б вигідно мовчати.
/// </summary>
public static class DotepyStock
{
    public static readonly IReadOnlyList<string> Lines =
    [
        "Ой, мовчу.",
        "Тут мала бути шедевральна відповідь.",
        "Мій кіт сів на клавіатуру.",
        "Нема слів. Узагалі.",
        "Пас. Наступне питання.",
        "…",
        "Я це знав, але забув.",
        "Спитайте в Глека.",
        "Так. Це відповідь.",
        "Тиша. Гучна.",
        "Пишу-пишу… не встиг.",
        "Це секрет.",
    ];

    /// <summary>Випадкова підставна, якої ще нема на цій картці (якщо таких не лишилось — будь-яка).</summary>
    public static string Pick(Random rng, ICollection<string> taken)
    {
        var free = 0;
        foreach (var line in Lines) if (!taken.Contains(line)) free++;
        if (free == 0) return Lines[rng.Next(Lines.Count)];
        var n = rng.Next(free);
        foreach (var line in Lines)
        {
            if (taken.Contains(line)) continue;
            if (n-- == 0) return line;
        }
        return Lines[0];
    }
}
