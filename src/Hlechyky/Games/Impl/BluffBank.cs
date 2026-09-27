using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Одне питання «Байкарів»: текст із пропуском <c>___</c>, дивна правда, інші її написання (<see cref="Accept"/> — щоб
/// сервер не прийняв правду за брехню) і заготовлені брехні Глека (<see cref="Decoys"/>), які йдуть на стіл, коли
/// гравців мало, і які видає кнопка «🎲 Хай Глек збреше».
/// </summary>
public sealed class BluffQuestion
{
    public string Id { get; init; } = "";
    /// <summary>Тема — один із ключів <see cref="BluffCats.All"/> (крім «all»); невідома стає «odd».</summary>
    public string Cat { get; init; } = BluffCats.Odd;
    /// <summary>Текст із рівно одним пропуском <c>___</c>.</summary>
    public string Q { get; init; } = "";
    /// <summary>Правда так, як її показати на картці.</summary>
    public string Answer { get; init; } = "";
    public IReadOnlyList<string> Accept { get; init; } = [];
    public IReadOnlyList<string> Decoys { get; init; } = [];
    /// <summary>«А насправді…» для розкриття; може бути порожнім.</summary>
    public string Note { get; init; } = "";
    /// <summary>Де факт перевірено. У гру не потрапляє — записка для людей.</summary>
    public string Source { get; init; } = "";

    /// <summary>Правда в усіх написаннях: відповідь і <see cref="Accept"/>.</summary>
    public IEnumerable<string> Forms
    {
        get
        {
            yield return Answer;
            foreach (var a in Accept) yield return a;
        }
    }

    string? _key;

    /// <summary>
    /// Ключ для пам'яті «хто бачив» (<see cref="BluffSeen"/>): 16 hex SHA-256 від тексту. Від тексту, а не від id: банк
    /// переписують і сортують, а переписане питання й так нове.
    /// </summary>
    public string Key => _key ??= Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Q.Trim())))[..16];
}

/// <summary>Теми банку — опція столу «Теми» (можна кілька).</summary>
public static class BluffCats
{
    public const string Any = "all";
    public const string Odd = "odd";

    /// <summary>Те, що бачить господар у списку при «+ Стіл», у цьому самому порядку.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> All =
    [
        (Any, "Усі теми"),
        ("ukraine", "Україна"),
        ("history", "Історія"),
        ("nature", "Природа й тварини"),
        ("science", "Наука й техніка"),
        ("food", "Їжа й побут"),
        ("world", "Світ і звичаї"),
        ("sport", "Спорт і розваги"),
        ("lang", "Слова й мова"),
        (Odd, "Дивне"),
    ];

    /// <summary>Чи це справжня тема (не «усі»).</summary>
    public static bool Known(string? key) => key is not null && key != Any && All.Any(c => c.Key == key);

    /// <summary>«Україна» для «ukraine»; невідомий ключ — «Дивне».</summary>
    public static string Label(string? key)
    {
        foreach (var (k, label) in All) if (k == key && k != Any) return label;
        return "Дивне";
    }

    /// <summary>
    /// Обрані теми з опції столу («ukraine,science»). null — усі: і коли обрано «Усі теми», і коли зі знайомих не
    /// лишилось жодної (як <see cref="SkilkyTopics.Parse"/>).
    /// </summary>
    public static IReadOnlySet<string>? Parse(string? option)
    {
        var picked = GameOption.Split(option);
        if (picked.Contains(Any)) return null;
        var known = picked.Where(Known).ToHashSet(StringComparer.Ordinal);
        return known.Count == 0 ? null : known;
    }

    public static bool Fits(BluffQuestion q, IReadOnlySet<string>? cats) => cats is null || cats.Contains(q.Cat);
}

/// <summary>Темп партії: секунди на брехню й на вибір правди.</summary>
public static class BluffPace
{
    public const string Fast = "fast";
    public const string Normal = "normal";
    public const string Slow = "slow";

    public static readonly IReadOnlyList<(string Key, string Label)> All =
        [(Fast, "Швидкий"), (Normal, "Звичайний"), (Slow, "Спокійний")];

    /// <summary>(на брехню, на вибір) у секундах; невідомий темп — звичайний.</summary>
    public static (int Write, int Pick) Seconds(string? pace) => pace switch
    {
        Fast => (30, 20),
        Slow => (60, 45),
        _ => (45, 30),
    };
}

/// <summary>
/// Свій банк замість файла — через <c>Ctx.Services</c>, як фрази Зіпсованого телефону: тести грають на відомих питаннях
/// і не ламаються, коли автори переписують справжній банк. У проді не реєструється — гра бере <see cref="BluffBank.All"/>.
/// </summary>
public sealed record BluffBankSource(IReadOnlyList<BluffQuestion> Questions);

/// <summary>
/// Банк питань із <c>data/bluff/questions.json</c> (формат bluff із CONTENT-FORMATS.md). Читається раз на процес.
/// Ніколи не кидає: нема файла чи він битий — банк порожній, і стіл чесно скаже, що грати нема в що. Кривий запис
/// (без пропуску, з двома пропусками, без правди) мовчки відкидається — одна помилка авторів не гасить гру.
/// </summary>
public static partial class BluffBank
{
    public const string FileName = "data/bluff/questions.json";
    /// <summary>Пропуск у питанні — рівно три підкреслення (чотири й більше теж читаємо як пропуск).</summary>
    public const string Blank = "___";

    static readonly Lazy<IReadOnlyList<BluffQuestion>> Cached = new(() => Load(Paths.Resolve(FileName)));

    public static IReadOnlyList<BluffQuestion> All => Cached.Value;

    [GeneratedRegex("_{3,}")]
    private static partial Regex Blanks();

    public static IReadOnlyList<BluffQuestion> Load(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Корінь — об'єкт із полем <c>questions</c> або одразу масив. Поля, крім q й answer, необов'язкові.</summary>
    public static IReadOnlyList<BluffQuestion> Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            var array = root.ValueKind == JsonValueKind.Array ? root
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("questions", out var q) ? q
                : default;
            if (array.ValueKind != JsonValueKind.Array) return [];
            var list = new List<BluffQuestion>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in array.EnumerateArray())
                if (Read(e) is { } question && keys.Add(question.Key)) list.Add(question);
            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    static BluffQuestion? Read(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var text = BluffText.Clean(Str(e, "q"));
        var answer = BluffText.Clean(Str(e, "answer"));
        if (text.Length == 0 || answer.Length == 0) return null;
        var blanks = Blanks().Matches(text);
        if (blanks.Count != 1) return null;
        text = Blanks().Replace(text, Blank);
        var cat = Str(e, "cat").Trim();
        var accept = Strs(e, "accept");
        var forms = new List<string>(accept.Count + 1) { answer };
        forms.AddRange(accept);
        // Заготовку, що слово в слово збігається з правдою (у будь-якому написанні, з відмінком чи одруківкою), — геть:
        // вона сиділа б на столі близнюком правди. Грубішого сита гравців (правда всередині фрази, шматок) тут нема:
        // заготовки пишуть і звіряють люди, а «сорок центів» при правді «сорокова формула» — чесна брехня.
        var decoys = new List<string>();
        foreach (var d in Strs(e, "decoys"))
            if (!forms.Any(f => BluffText.LooksSame(d, f)) && !decoys.Any(x => BluffText.LooksSame(x, d)) && d.Length <= Bluff.MaxLie)
                decoys.Add(d);
        return new BluffQuestion
        {
            Id = Str(e, "id").Trim(),
            Cat = BluffCats.Known(cat) ? cat : BluffCats.Odd,
            Q = text,
            Answer = answer,
            Accept = accept,
            Decoys = decoys,
            Note = BluffText.Clean(Str(e, "note")),
            Source = Str(e, "source").Trim(),
        };
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static List<string> Strs(JsonElement e, string name)
    {
        var list = new List<string>();
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return list;
        foreach (var x in v.EnumerateArray())
            if (x.ValueKind == JsonValueKind.String && BluffText.Clean(x.GetString()) is { Length: > 0 } s) list.Add(s);
        return list;
    }
}
