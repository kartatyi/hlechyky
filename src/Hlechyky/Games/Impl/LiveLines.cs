using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hlechyky.Games.Impl;

/// <summary>Одна репліка ролика: хто каже (коротке ім'я голосу для <see cref="TtsService.Voice"/>), що і яким темпом.</summary>
public sealed record LiveLine(string Voice, string Text, string Rate, bool Rim = false);

/// <summary>
/// Сценарій живого ролика: стиль підкладки й репліки по черзі. <see cref="Facts"/> — види фактів, що пішли в ролик:
/// з них рахується «той самий факт тій самій людині — не частіше ніж раз на добу».
/// </summary>
public sealed record LiveScript(string Kind, string Style, IReadOnlyList<LiveLine> Lines, IReadOnlyList<string> Facts)
{
    /// <summary>Текст для вкладки адміна й журналу роликів — без голосів, «ба-дум-тсс» позначено.</summary>
    public string Text => string.Join(" ", Lines.Select(l => l.Rim ? l.Text + " [ба-дум-тсс]" : l.Text));
}

/// <summary>Факт про гравця (або рядок новин): вид, «соковитість» 0–1 і значення для плейсхолдерів шаблону.</summary>
public sealed record LiveFact(string Kind, double Juice, IReadOnlyDictionary<string, string> Values)
{
    public static LiveFact Of(string kind, double juice, params (string Key, object Value)[] values) =>
        new(kind, Math.Clamp(juice, 0, 1), values.ToDictionary(v => v.Key,
            v => Convert.ToString(v.Value, CultureInfo.InvariantCulture) ?? "", StringComparer.Ordinal));
}

/// <summary>
/// Банк фраз <c>data/liveads/lines.json</c> і заповнення шаблонів. Шаблони пишуться без граматичного роду: рід ніка
/// невідомий, а «владік програв» і «Смауг програла» в одному шаблоні не живуть. Множина — формами в дужках:
/// <c>{n} {гра|гри|ігор}</c> бере останнє число, що стоїть перед ним. Репліка з <c>{rim}</c> закінчується «ба-дум-тсс»,
/// приставка <c>П:</c>/<c>Г:</c> віддає її Поліні чи Глекові. Невідомий плейсхолдер — шаблон відкидається цілим:
/// краще інший варіант, ніж «{rival}» в ефірі.
/// </summary>
public sealed class LiveLines
{
    public const string Glek = "ostap";
    public const string Polina = "polina";

    public string Sign { get; init; } = "Глечики. Слухай, як гуде.";
    public Dictionary<string, string> Spoken { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> Intro { get; init; } = new(StringComparer.Ordinal);
    public List<string> Link { get; init; } = [];
    public List<string> Outro { get; init; } = [];
    public Dictionary<string, List<string>> Facts { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> Events { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> News { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> Styles { get; init; } = new(StringComparer.Ordinal);

    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static LiveLines Parse(string json)
    {
        var raw = JsonSerializer.Deserialize<LiveLines>(json, Json) ?? new LiveLines();
        // Ключі spoken — ключі ніків (нижній регістр), щоб «Smaug» і «smaug» читались однаково.
        return new LiveLines
        {
            Sign = raw.Sign, Intro = raw.Intro, Link = raw.Link, Outro = raw.Outro, Facts = raw.Facts, Events = raw.Events,
            News = raw.News, Styles = raw.Styles,
            Spoken = raw.Spoken.ToDictionary(kv => Auth.NickKey(kv.Key), kv => kv.Value, StringComparer.Ordinal),
        };
    }

    public static LiveLines Load(string path) => Parse(File.ReadAllText(path));

    // ---------- ніки вголос ----------

    static readonly Regex NotSpeakable = new(@"[^\p{L}\p{N}\s'’\-]", RegexOptions.Compiled);

    /// <summary>
    /// Як нік звучить у рекламі: словник вимови (Smaug → Смауг), інакше — сам нік без дужок, емодзі й розділових
    /// знаків, бо edge-tts читає «( справжній )» як «дужка справжній дужка».
    /// </summary>
    public string Say(string nick)
    {
        if (Spoken.TryGetValue(Auth.NickKey(nick), out var s)) return s;
        var clean = string.Join(' ', NotSpeakable.Replace(nick, " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return clean.Length > 0 ? clean : "хтось без імені";
    }

    // ---------- множина ----------

    /// <summary>Українська множина: 1 гра, 2–4 гри, 5+ ігор; 11–14 — завжди «ігор».</summary>
    public static string Plural(long n, string one, string few, string many)
    {
        var abs = Math.Abs(n);
        if (abs % 100 is >= 11 and <= 14) return many;
        return (abs % 10) switch { 1 => one, 2 or 3 or 4 => few, _ => many };
    }

    static readonly Regex Token = new(@"\{([^{}]+)\}", RegexOptions.Compiled);
    static readonly Regex Number = new(@"-?\d+", RegexOptions.Compiled);

    /// <summary>
    /// Заповнити шаблон. null — у шаблоні плейсхолдер, якого нема у <paramref name="values"/>, або форма множини без
    /// числа перед нею: такий шаблон не для цього факту.
    /// </summary>
    public static string? Fill(string template, IReadOnlyDictionary<string, string> values)
    {
        var sb = new StringBuilder();
        long? last = null;
        var at = 0;
        foreach (Match m in Token.Matches(template))
        {
            var before = template[at..m.Index];
            sb.Append(before);
            if (LastNumber(before) is { } nb) last = nb;
            at = m.Index + m.Length;
            var body = m.Groups[1].Value;
            if (body == "rim") continue;               // маркер репліки, не текст
            if (body.Contains('|'))
            {
                var forms = body.Split('|');
                if (forms.Length != 3 || last is not { } n) return null;
                sb.Append(Plural(n, forms[0], forms[1], forms[2]));
                continue;
            }
            if (!values.TryGetValue(body, out var v)) return null;
            sb.Append(v);
            if (LastNumber(v) is { } nv) last = nv;
        }
        sb.Append(template[at..]);
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    static long? LastNumber(string s)
    {
        var ms = Number.Matches(s);
        return ms.Count > 0 && long.TryParse(ms[^1].Value, out var n) ? n : null;
    }

    /// <summary>Усі плейсхолдери шаблону (без форм множини й маркера) — для тесту банку.</summary>
    public static IEnumerable<string> Keys(string template) =>
        Token.Matches(template).Select(m => m.Groups[1].Value).Where(b => b != "rim" && !b.Contains('|'));

    /// <summary>Репліка з шаблону: приставка голосу, маркер «ба-дум-тсс», текст.</summary>
    public static LiveLine? Line(string template, IReadOnlyDictionary<string, string> values, string voice, string rate)
    {
        var t = template.TrimStart();
        if (t.StartsWith("П:", StringComparison.Ordinal)) { voice = Polina; t = t[2..]; }
        else if (t.StartsWith("Г:", StringComparison.Ordinal)) { voice = Glek; t = t[2..]; }
        var rim = t.Contains("{rim}", StringComparison.Ordinal);
        return Fill(t, values) is { Length: > 0 } text ? new LiveLine(voice, text, rate, rim) : null;
    }

    /// <summary>Випадковий шаблон, що заповнюється цими значеннями; null — жоден не підійшов.</summary>
    public LiveLine? Pick(IReadOnlyList<string>? pool, IReadOnlyDictionary<string, string> values, string voice, string rate, Random rng)
    {
        if (pool is null || pool.Count == 0) return null;
        foreach (var i in Enumerable.Range(0, pool.Count).OrderBy(_ => rng.Next()))
            if (Line(pool[i], values, voice, rate) is { } line) return line;
        return null;
    }

    public string Style(string kind, Random rng, string fallback = "hold") =>
        Styles.TryGetValue(kind, out var s) && s.Count > 0 ? s[rng.Next(s.Count)] : fallback;
}
