using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Один запис банку: уривок класики, прислів'я або скоромовка (spec §7.1).</summary>
public sealed record TyperaceEntry(string Id, string Kind, string Text, string? Author = null, string? Title = null, int? Year = null);

/// <summary>Звідки текст заїзду — те, що йде у вид (<c>src</c>): автор і твір для класики, «низка з 6» для прислів'їв.</summary>
public sealed record TyperaceSource(string Kind, string? Author, string Title, int? Year);

/// <summary>Текст заїзду: сам уривок, звідки він і які записи банку пішли на нього (для пам'яті «не повторювати»).</summary>
public sealed record TyperacePick(string Text, TyperaceSource Src, IReadOnlyList<string> Ids);

/// <summary>
/// Банк текстів Клавоперегонів із <c>data/typerace/texts.json</c>. Читається раз на процес (<see cref="Default"/>),
/// ніколи не кидає: нема файла чи він битий — банк порожній, і гра чесно каже «тексти кудись подівались».
/// Тести підкладають свій банк через <c>Ctx.Services</c> (<see cref="From"/>).
/// </summary>
public sealed class TyperaceBank
{
    public const string FileName = "data/typerace/texts.json";
    public const string Classic = "classic", Proverbs = "proverbs", Twisters = "twisters";
    public const string All = "all";
    public const string Short = "short", Medium = "medium", Long = "long";

    /// <summary>Довжина → скільки знаків цілимось (spec §7.4). Міняти разом зі spec і <c>typerace.js</c>.</summary>
    public static int TargetLen(string length) => length switch { Short => 150, Long => 600, _ => 300 };

    public static readonly string[] Lengths = [Short, Medium, Long];
    public static readonly string[] Sources = [All, Classic, Proverbs, Twisters];

    static readonly Lazy<TyperaceBank> Cached = new(() => Load(Paths.Resolve(FileName)));

    /// <summary>Банк із файла гри (один на процес).</summary>
    public static TyperaceBank Default => Cached.Value;

    readonly Dictionary<string, List<TyperaceEntry>> _byKind = new(StringComparer.Ordinal)
    {
        [Classic] = [], [Proverbs] = [], [Twisters] = [],
    };

    public IReadOnlyList<TyperaceEntry> Entries { get; }

    public bool Empty => Entries.Count == 0;

    TyperaceBank(IReadOnlyList<TyperaceEntry> entries)
    {
        Entries = entries;
        foreach (var e in entries) _byKind[e.Kind].Add(e);
    }

    public IReadOnlyList<TyperaceEntry> Of(string kind) => _byKind.TryGetValue(kind, out var list) ? list : [];

    /// <summary>
    /// Банк зі списку записів: кожен текст нормалізується, записи без id/тексту, з невідомим видом, з дубльованим id або
    /// з недрукованим знаком мовчки відкидаються — як і на завантаженні з файла.
    /// </summary>
    public static TyperaceBank From(IEnumerable<TyperaceEntry> raw)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<TyperaceEntry>();
        foreach (var e in raw)
        {
            if (string.IsNullOrWhiteSpace(e.Id) || e.Kind is not (Classic or Proverbs or Twisters)) continue;
            var text = TyperaceText.Normalize(e.Text);
            if (!TyperaceText.IsTypeable(text) || !seen.Add(e.Id)) continue;
            list.Add(e with { Text = text });
        }
        return new TyperaceBank(list);
    }

    /// <summary>Прочитати банк із файла. Нема файла, битий JSON — порожній банк, а не виняток.</summary>
    public static TyperaceBank Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new TyperaceBank([]);
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("texts", out var texts)
                || texts.ValueKind != JsonValueKind.Array) return new TyperaceBank([]);
            var raw = new List<TyperaceEntry>();
            foreach (var t in texts.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object) continue;
                var id = Str(t, "id");
                var kind = Str(t, "kind");
                var text = Str(t, "text");
                if (id is null || kind is null || text is null) continue;
                int? year = t.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number && y.TryGetInt32(out var yy) ? yy : null;
                raw.Add(new TyperaceEntry(id, kind, text, Str(t, "author"), Str(t, "title"), year));
            }
            return From(raw);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new TyperaceBank([]);
        }
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    // ---------------------------------------------------------------------------------------------
    // Вибір тексту заїзду (spec §7.4)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Текст для заїзду за опціями. <paramref name="used"/> — пам'ять кімнати: що вже було, того не даємо, доки є
    /// щось нове; вичерпалось — пам'ять про цей вид чиститься. null — банк порожній (або нема жодного тексту цього виду).
    /// </summary>
    public TyperacePick? Pick(string length, string source, Random rng, ISet<string> used)
    {
        var target = TargetLen(length);
        var kind = source switch
        {
            Classic or Proverbs or Twisters => source,
            _ => rng.Next(100) switch { < 50 => Classic, < 80 => Proverbs, _ => Twisters },
        };
        // обраного виду в банку нема (скажімо, скоромовки ще не завезли) — беремо будь-який наявний
        if (Of(kind).Count == 0)
        {
            kind = Of(Classic).Count > 0 ? Classic : Of(Proverbs).Count > 0 ? Proverbs : Twisters;
            if (Of(kind).Count == 0) return null;
        }
        return kind == Classic ? PickClassic(target, rng, used) : Assemble(kind, target, rng, used);
    }

    /// <summary>Уривки класики, нарізані під ціль, — лише ті, що в неї влазять (кеш на банк: банк один на процес).</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<int, (TyperaceEntry Entry, string Text)[]> _cuts = new();

    /// <summary>
    /// Чи годиться шматок для цілі: не коротший за половину й не довший за подвійну. Уривок, що починається реченням
    /// «Йду.», а далі має одне речення на 500 знаків, для «коротко» не годиться — для «середньо» саме те.
    /// </summary>
    public static bool Fits(int length, int target) => length >= target / 2 && length <= target * 2;

    (TyperaceEntry Entry, string Text)[] CutsFor(int target) => _cuts.GetOrAdd(target, t =>
    {
        var all = Of(Classic);
        var fit = new List<(TyperaceEntry, string)>(all.Count);
        foreach (var e in all)
        {
            var cut = Cut(e.Text, t);
            if (Fits(cut.Length, t)) fit.Add((e, cut));
        }
        // жоден не влазить (крихітний банк) — беремо як є, аби гра не стала
        if (fit.Count == 0) foreach (var e in all) fit.Add((e, Cut(e.Text, t)));
        return [.. fit];
    });

    TyperacePick PickClassic(int target, Random rng, ISet<string> used)
    {
        var fit = CutsFor(target);
        var fresh = new List<(TyperaceEntry Entry, string Text)>(fit.Length);
        foreach (var x in fit) if (!used.Contains(x.Entry.Id)) fresh.Add(x);
        if (fresh.Count == 0)
        {
            foreach (var x in fit) used.Remove(x.Entry.Id);
            fresh.AddRange(fit);
        }
        var (pick, text) = fresh[rng.Next(fresh.Count)];
        used.Add(pick.Id);
        return new TyperacePick(text, new TyperaceSource(Classic, pick.Author, pick.Title ?? "", pick.Year), [pick.Id]);
    }

    /// <summary>
    /// Шматок уривка потрібного розміру: речення від початку, доки довжина менша за 0,8 цілі; речення, що вивело б
    /// за 1,35 цілі, не беремо; якщо вже перше довше — лише воно. Закінчується завжди на кінці речення.
    /// </summary>
    public static string Cut(string passage, int target)
    {
        var min = (int)(0.8 * target);
        var max = (int)(1.35 * target);
        if (passage.Length <= max && passage.Length >= min) return passage;
        var end = 0;
        var at = 0;
        while (at < passage.Length)
        {
            var next = SentenceEnd(passage, at);
            if (end > 0 && next > max)
            {
                // ще закоротко, а наступне речення виводить за вікно: беремо те, що ближче до цілі
                // («Йду.» замість ~150 знаків було б нечесно коротким заїздом)
                if (end < min && Math.Abs(next - target) < Math.Abs(end - target)) end = next;
                break;
            }
            end = next;
            if (end >= min) break;
            // пропустити пробіл чи перенос після речення
            at = end;
            while (at < passage.Length && passage[at] is ' ' or '\n') at++;
        }
        return passage[..end].TrimEnd(' ', '\n');
    }

    /// <summary>
    /// Де закінчується речення, що починається з <paramref name="from"/>: позиція одразу після «. ! ? ...» (і
    /// закривних «» )», за якими пробіл, перенос чи кінець. Крапка після цифри чи ініціала («Т. Шевченко») не рахується.
    /// </summary>
    public static int SentenceEnd(string s, int from)
    {
        for (var i = from; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch is not ('.' or '!' or '?')) continue;
            var j = i;
            while (j < s.Length && s[j] is '.' or '!' or '?') j++;
            while (j < s.Length && s[j] is '»' or ')') j++;
            if (j < s.Length && s[j] is not (' ' or '\n')) { i = j - 1; continue; }
            if (ch == '.' && j == i + 1 && i > 0)
            {
                var before = s[i - 1];
                if (char.IsDigit(before)) { i = j - 1; continue; }
                // ініціал: одна велика літера після пробілу чи початку
                if (char.IsUpper(before) && (i < 2 || s[i - 2] is ' ' or '\n' or '(' or '«')) { i = j - 1; continue; }
            }
            return j;
        }
        return s.Length;
    }

    /// <summary>
    /// Низка прислів'їв чи скоромовок довжиною близько <paramref name="target"/>: перемішуємо, додаємо через пробіл,
    /// доки коротше за ціль мінус 20; те, що вивело б за ціль плюс 40, пропускаємо на користь коротшого.
    /// </summary>
    TyperacePick Assemble(string kind, int target, Random rng, ISet<string> used)
    {
        var all = Of(kind);
        var pool = new List<TyperaceEntry>(all.Count);
        var freshLen = 0;
        foreach (var e in all)
            if (!used.Contains(e.Id)) { pool.Add(e); freshLen += e.Text.Length + 1; }
        // нового не вистачить навіть на одну низку — пам'ять про цей вид починаємо спочатку
        if (pool.Count == 0 || freshLen < Math.Min(target - 20, TotalLen(all)))
        {
            foreach (var e in all) used.Remove(e.Id);
            pool.Clear();
            pool.AddRange(all);
        }
        // Фішер — Єйтс тим самим Random, що й решта кімнати: той самий сід — та сама низка
        for (var i = pool.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        var parts = new List<TyperaceEntry>();
        var len = 0;
        foreach (var e in pool)
        {
            if (len >= target - 20) break;
            var add = e.Text.Length + (parts.Count > 0 ? 1 : 0);
            if (len + add > target + 40 && parts.Count > 0) continue;
            parts.Add(e);
            len += add;
        }
        foreach (var e in parts) used.Add(e.Id);
        var text = string.Join(' ', parts.Select(p => p.Text));
        var title = kind == Proverbs
            ? $"Прислів’я — низка з {parts.Count}"
            : $"Скоромовки — {parts.Count} поспіль";
        return new TyperacePick(text, new TyperaceSource(kind, null, title, null), [.. parts.Select(p => p.Id)]);
    }

    static int TotalLen(IReadOnlyList<TyperaceEntry> list)
    {
        var n = 0;
        foreach (var e in list) n += e.Text.Length + 1;
        return n;
    }
}
