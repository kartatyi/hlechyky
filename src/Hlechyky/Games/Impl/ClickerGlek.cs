using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>Налаштування порад Дядька Глека (<c>Clicker:Glek</c>, наживо).</summary>
public sealed class ClickerGlekOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Книга фраз — від кореня сайту.</summary>
    public string File { get; set; } = "data/clicker/glek-tips.json";
    /// <summary>Ніки, до яких Глек звертається в жіночому роді (пари {пішов|пішла} у книзі).</summary>
    public List<string> She { get; set; } = [];
    /// <summary>Та сама підказка в моменті — не частіше за стільки хвилин.</summary>
    public double MomentMinutes { get; set; } = 20;
    /// <summary>«Потім» ховає пораду на стільки годин.</summary>
    public double LaterHours { get; set; } = 24;
}

/// <summary>
/// Книга порад Дядька Глека (<c>data/clicker/glek-tips.json</c>): на кожен вид поради з аналітика — кілька фраз його
/// голосом із підстановками. Як книга ведучого «Своєї гри»: ключі з підкресленням — коментарі автора, невідомий ключ чи
/// підстановка — рядок у лозі, а не тиша на проді.
/// </summary>
public sealed partial class ClickerGlekBook
{
    /// <summary>Які підстановки має кожен вид (крім спільних {gain} і {nick}).</summary>
    public static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        ["clay-play"] = ["clay", "from"],
        ["clay-leave"] = ["clay", "from"],
        ["clay-form"] = ["clay", "from"],
        ["album-stove"] = ["have", "slots"],
        ["album-grid"] = ["cells", "stars", "first"],
        ["fair-rep"] = ["list"],
        ["guest-rep"] = ["list"],
        ["mark-passive"] = ["mark", "tier", "at"],
        ["slava"] = ["tier", "need"],
        ["toloka-gate"] = ["tier", "building"],
        ["toloka-lay"] = ["building", "stage"],
        ["toloka-collect"] = ["building", "stage", "first"],
        ["wallet"] = ["spent"],
        ["fire"] = ["items"],
    };

    readonly Dictionary<string, string[]> _pools;

    ClickerGlekBook(Dictionary<string, string[]> pools) => _pools = pools;

    public static readonly ClickerGlekBook Empty = new([]);

    public int Count(string kind) => _pools.TryGetValue(kind, out var p) ? p.Length : 0;

    /// <summary>Фраза для виду: та сама на весь день для того самого гончаря (без гри з її генератором), різна в різні дні.</summary>
    public string Pick(string kind, string nick, string day)
    {
        if (!_pools.TryGetValue(kind, out var pool) || pool.Length == 0) return "";
        var seed = 17;
        foreach (var ch in kind + "|" + Auth.NickKey(nick) + "|" + day) seed = unchecked(seed * 31 + ch);
        return pool[(int)((uint)seed % (uint)pool.Length)];
    }

    /// <summary>Підставити числа й форму роду: {key} — зі словника, {він|вона} — за <paramref name="she"/>.</summary>
    public static string Fill(string template, IReadOnlyDictionary<string, string> vars, bool she)
    {
        var s = Gender().Replace(template, m => she ? m.Groups[2].Value : m.Groups[1].Value);
        return Placeholder().Replace(s, m => vars.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }

    public static (ClickerGlekBook Book, IReadOnlyList<string> Problems) Parse(string json)
    {
        var problems = new List<string>();
        Dictionary<string, JsonElement>? raw;
        try { raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json); }
        catch (JsonException e) { return (Empty, [$"кривий JSON: {e.Message}"]); }
        if (raw is null) return (Empty, ["порожній файл"]);
        var pools = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (key, el) in raw)
        {
            if (key.StartsWith('_')) continue;
            if (!Allowed.TryGetValue(key, out var allowed)) { problems.Add($"{key}: невідомий вид поради"); continue; }
            if (el.ValueKind != JsonValueKind.Array) { problems.Add($"{key}: має бути список фраз"); continue; }
            var lines = el.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).ToArray();
            var bad = lines.SelectMany(l => Placeholder().Matches(Gender().Replace(l, "")).Select(m => m.Groups[1].Value))
                .Where(p => p is not ("gain" or "nick") && !allowed.Contains(p)).Distinct().ToList();
            var broken = lines.Where(l => l.Count(c => c == '{') != l.Count(c => c == '}')).ToList();
            if (bad.Count > 0) { problems.Add($"{key}: невідомі підстановки {string.Join(", ", bad.Select(b => "{" + b + "}"))}"); continue; }
            if (broken.Count > 0) { problems.Add($"{key}: незакриті дужки — «{broken[0]}»"); continue; }
            pools[key] = lines;
        }
        foreach (var key in Allowed.Keys.Where(k => !pools.ContainsKey(k) && !raw.ContainsKey(k)))
            problems.Add($"{key}: фраз нема — цієї поради Глек не скаже");
        return (new ClickerGlekBook(pools), problems);
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\{([^{}|]+)\|([^{}|]+)\}")]
    private static partial Regex Gender();
}

/// <summary>Книга порад для всіх кімнат кола: читається з файлу, перечитується, коли файл змінився (перевірка — раз на 30 с).</summary>
public sealed class ClickerGlekService(Func<ClickerGlekOptions> options, ILogger<ClickerGlekService>? log = null)
{
    static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(30);
    readonly object _gate = new();
    ClickerGlekBook _book = ClickerGlekBook.Empty;
    DateTime _stamp = DateTime.MinValue;
    string _path = "";
    DateTime _checked = DateTime.MinValue;

    public ClickerGlekOptions O => options();

    /// <summary>Книга для тестів і для тих, кому файл не потрібен.</summary>
    public ClickerGlekBook? Fixed { get; init; }

    public bool IsShe(string nick) => O.She.Any(n => Auth.NickKey(n) == Auth.NickKey(nick));

    public ClickerGlekBook Book
    {
        get
        {
            if (Fixed is { } f) return f;
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                var path = Paths.Resolve(O.File);
                if (path == _path && now - _checked < CheckEvery) return _book;
                _checked = now;
                var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                if (path == _path && stamp == _stamp) return _book;
                _path = path;
                _stamp = stamp;
                if (stamp == DateTime.MinValue)
                {
                    log?.LogWarning("коло: книги порад Глека {Path} нема — Глек мовчить", path);
                    return _book = ClickerGlekBook.Empty;
                }
                try
                {
                    var (book, problems) = ClickerGlekBook.Parse(File.ReadAllText(path));
                    foreach (var p in problems) log?.LogWarning("коло: книга порад Глека: {Problem}", p);
                    return _book = book;
                }
                catch (IOException e)
                {
                    log?.LogWarning("коло: книгу порад Глека {Path} не прочитати: {Err}", path, e.Message);
                    return _book;
                }
            }
        }
    }
}

public static class ClickerGlekSetup
{
    public static IServiceCollection Add(IServiceCollection services)
    {
        services.AddOptions<ClickerGlekOptions>().BindConfiguration("Clicker:Glek");
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptionsMonitor<ClickerGlekOptions>>();
            return new ClickerGlekService(() => opts.CurrentValue, sp.GetService<ILogger<ClickerGlekService>>());
        });
        return services;
    }
}

/// <summary>Порада Глека для клієнта: вид (за ним «потім»), готовий текст, куди вести кнопкою.</summary>
public sealed record ClickerGlekTip(string Kind, string Text, string Where, double Gain);

/// <summary>
/// Поради Дядька Глека (аналітика кола, етап 4): аналітик (ClickerAdvisor.cs) дає числа, Глек — слова з книги фраз.
/// <list type="bullet">
/// <item>Картка «🏺 Глек радить» у «📈 Літописі»: три найсильніші поради — у знімку щоденника (оновлюється з ним).</item>
/// <item>Підказка в моменті — у виді кола (<c>glek</c>): глина біля кола, глина перед виходом, етап Толоки можна закладати.
/// Та сама — не частіше за <see cref="ClickerGlekOptions.MomentMinutes"/>.</item>
/// <item>«Потім» (дія <c>glek</c>, <c>{ later: вид }</c>) ховає вид на добу; виконана порада зникає сама — аналітик її
/// більше не дає.</item>
/// </list>
/// Чому не «клацай частіше»: це не порада (слово користувача 25.10). Кожна фраза — механіка й число.
/// </summary>
public sealed partial class Clicker
{
    /// <summary>Скільки порад на картці.</summary>
    public const int GlekCardSize = 3;
    /// <summary>Підказка «постав іншу глину» — лише коли виграш хоч такий: дрібниця не варта того, щоб смикати гравця.</summary>
    public const double GlekClayMin = 1.2;
    /// <summary>Гончар без дії стільки — уже «відходить»: час підказати білу глину.</summary>
    public static readonly TimeSpan GlekLeaveAfter = TimeSpan.FromMinutes(2);
    /// <summary>Підказку в моменті перераховуємо не частіше (у ній модель глини).</summary>
    static readonly TimeSpan GlekMomentCache = TimeSpan.FromSeconds(15);

    readonly Dictionary<string, DateTimeOffset> _glekLater = new(StringComparer.Ordinal);
    readonly Dictionary<string, DateTimeOffset> _glekShown = new(StringComparer.Ordinal);
    DateTimeOffset _glekMomentAt;
    object? _glekMoment;

    ClickerGlekService? _glekSvc;
    bool _glekLooked;

    ClickerGlekService? GlekSvc()
    {
        if (!_glekLooked) { _glekSvc = Ctx.Services.GetService<ClickerGlekService>(); _glekLooked = true; }
        return _glekSvc;
    }

    static readonly Dictionary<string, (string Acc, string Nom)> ClayWords = new(StringComparer.Ordinal)
    {
        [""] = ("звичайну глину", "звичайна"), ["red"] = ("червону глину", "червона"), ["white"] = ("білу глину", "біла"),
        ["black"] = ("чорну глину", "чорна"), ["blue"] = ("голубу глину", "голуба"), ["kaolin"] = ("каолін", "каолін"),
    };

    /// <summary>«×2,3» для великого виграшу, «+12 %» — для меншого.</summary>
    public static string GlekGain(double gain) =>
        gain >= 1.5 ? "×" + gain.ToString("0.#", Uk) : "+" + Math.Max(1, Math.Round((gain - 1) * 100)).ToString("0", Uk) + " %";

    bool GlekLater(string kind, DateTimeOffset now) => _glekLater.TryGetValue(kind, out var until) && until > now;

    /// <summary>Наскільки порада варта місця на картці: «до всього» й година біля кола — як є, решта приглушена.</summary>
    static double GlekScore(ClickerTip t) => t.Kind switch
    {
        "toloka-lay" => 1.3,                 // можна зробити просто зараз
        "toloka-collect" => 1.04,
        "fire" => Math.Sqrt(t.Gain),         // палить комору й рівні — не першим ділом
        _ => t.Measure is "idle" or "form" ? Math.Sqrt(t.Gain) : t.Gain,
    };

    /// <summary>Текст поради голосом Глека; нема фрази — null (Глек цю пораду пропускає).</summary>
    ClickerGlekTip? GlekText(ClickerTip t, DateTimeOffset now)
    {
        if (GlekSvc() is not { } svc || Ctx.NickOf(0) is not { Length: > 0 } nick) return null;
        var template = svc.Book.Pick(t.Kind, nick, Days.Of(now));
        if (template.Length == 0) return null;
        var v = new Dictionary<string, string>(StringComparer.Ordinal) { ["gain"] = GlekGain(t.Gain), ["nick"] = nick };
        string F(string k) => t.Facts.TryGetValue(k, out var o) ? Convert.ToString(o, Uk) ?? "" : "";
        switch (t.Kind)
        {
            case "clay-play" or "clay-leave" or "clay-form":
                v["clay"] = ClayWords.TryGetValue(F("clay"), out var to) ? to.Acc : F("name");
                v["from"] = ClayWords.TryGetValue(_clay, out var from) ? from.Nom : "";
                break;
            case "album-stove":
                v["have"] = F("have");
                v["slots"] = F("slots");
                break;
            case "album-grid":
                var cells = (List<string>)t.Facts["cells"];
                var stars = ((List<string>)t.Facts["stars"]).Where(s => !cells.Contains(s)).ToList();
                v["cells"] = cells.Count.ToString(Uk);
                v["stars"] = ((List<string>)t.Facts["stars"]).Count.ToString(Uk);
                v["first"] = string.Join(", ", cells.Select(c => GlekCell(c, false)).Concat(stars.Select(c => GlekCell(c, true))).Take(3));
                break;
            case "fair-rep":
                v["list"] = string.Join(" · ", ((Dictionary<string, int>)t.Facts["need"]).OrderByDescending(x => x.Value)
                    .Select(x => (FairVillages.FirstOrDefault(f => f.Key == x.Key)?.Name ?? x.Key) + " " + x.Value.ToString(Uk)));
                break;
            case "guest-rep":
                v["list"] = string.Join(" · ", ((Dictionary<string, int>)t.Facts["need"]).OrderByDescending(x => x.Value)
                    .Select(x => (Guests.FirstOrDefault(g => g.Key == x.Key)?.Name ?? x.Key) + " " + x.Value.ToString(Uk)));
                break;
            case "mark-passive":
                v["mark"] = F("mark");
                v["tier"] = F("tierName");
                v["at"] = F("at");
                break;
            case "slava":
                v["tier"] = F("tierName");
                v["need"] = F("need");
                break;
            case "toloka-gate":
                v["tier"] = F("tierName");
                v["building"] = F("buildingName");
                break;
            case "toloka-lay" or "toloka-collect":
                v["building"] = F("buildingName");
                v["stage"] = F("stageName");
                v["first"] = string.Join(", ", ((List<object>)t.Facts["left"]).Take(3).Select(GlekNeed));
                break;
            case "wallet":
                v["spent"] = PotsShort(t.Cost);
                break;
            case "fire":
                var items = Convert.ToInt64(t.Facts["items"], Uk);
                v["items"] = items.ToString(Uk) + " " + Plural(items, "виріб", "вироби", "виробів");
                break;
        }
        return new ClickerGlekTip(t.Kind, ClickerGlekBook.Fill(template, v, svc.IsShe(nick)), t.Where, t.Gain);
    }

    /// <summary>«горщик · межигірський» (клітинка) чи «горщик · межигірський ⭐» (зірка: потрібен дзвінкий).</summary>
    static string GlekCell(string cell, bool star)
    {
        var parts = cell.Split('|');
        var ware = WareOf(parts[0])?.Name.ToLowerInvariant() ?? parts[0];
        var style = parts.Length > 1 && parts[1].Length > 0 ? StyleWord(parts[1]) : "без розпису";
        return ware + " · " + style + (star ? " ⭐" : "");
    }

    static string GlekNeed(object o)
    {
        var j = JsonSerializer.SerializeToElement(o);
        var ware = WareOf(j.GetProperty("ware").GetString())?.Name.ToLowerInvariant() ?? "";
        var style = j.GetProperty("style").GetString() ?? "";
        var q = j.GetProperty("q").GetInt32();
        var need = j.GetProperty("need").GetInt32() - j.GetProperty("have").GetInt32();
        return $"{ware} ×{Math.Max(1, need).ToString(Uk)}" + (style.Length > 0 ? $" ({StyleWord(style)})" : "")
            + (q >= 4 ? ", розкішні" : q == 3 ? ", дзвінкі" : "");
    }

    /// <summary>Картка «🏺 Глек радить»: найсильніші поради, крім відкладених «потім».</summary>
    List<ClickerGlekTip> GlekCard(ClickerAdvice advice, DateTimeOffset now)
    {
        if (GlekSvc() is not { } svc || !svc.O.Enabled) return [];
        return [.. advice.Tips.Where(t => !GlekLater(t.Kind, now) && (t.Gain >= 1.01 || t.Kind.StartsWith("toloka-", StringComparison.Ordinal)))
            .GroupBy(t => t.Kind).Select(g => g.MaxBy(t => t.Gain)!)       // віх «пасив» буває кілька — на картці одна, найкраща
            .OrderByDescending(GlekScore)
            .Select(t => GlekText(t, now)).OfType<ClickerGlekTip>().Take(GlekCardSize)];
    }

    /// <summary>
    /// Підказка в моменті (вид <c>glek</c>): гончар клацає не на найкращій для кола глині (і не ліпить свідомо на голубій),
    /// гончар затих — пора подумати про білу, етап Толоки можна закладати. Одна за раз, кожна — не частіше за
    /// <see cref="ClickerGlekOptions.MomentMinutes"/>.
    /// </summary>
    object? GlekMoment(DateTimeOffset now)
    {
        if (GlekSvc() is not { } svc || !svc.O.Enabled || _goneAt != default || _handsAt == default) return null;
        if (now - _glekMomentAt < GlekMomentCache) return _glekMoment;
        _glekMomentAt = now;
        _glekMoment = null;
        var every = TimeSpan.FromMinutes(Math.Max(1, svc.O.MomentMinutes));
        bool Fresh(string kind) => !GlekLater(kind, now) && (!_glekShown.TryGetValue(kind, out var at) || now - at >= every);
        var tips = new List<ClickerTip>();
        var idle = now - _handsAt;
        if (idle < TimeSpan.FromSeconds(30) && _clay != "blue" && Fresh("clay-play"))
        {
            var clays = ClayModels();
            AdviseClay(clays, tips);
            tips.RemoveAll(t => t.Kind != "clay-play" || t.Gain < GlekClayMin);
        }
        else if (idle >= GlekLeaveAfter && Fresh("clay-leave"))
        {
            AdviseClay(ClayModels(), tips);
            tips.RemoveAll(t => t.Kind != "clay-leave" || t.Gain < GlekClayMin);
        }
        if (tips.Count == 0 && Fresh("toloka-lay"))
        {
            AdviseToloka(tips);
            tips.RemoveAll(t => t.Kind != "toloka-lay");
        }
        if (tips.Count == 0 || GlekText(tips[0], now) is not { } tip) return null;
        _glekShown[tip.Kind] = now;
        return _glekMoment = new { id = tip.Kind + ":" + now.ToUnixTimeSeconds(), kind = tip.Kind, text = tip.Text, where = tip.Where };
    }

    /// <summary>Дія <c>glek</c>: <c>{ later: вид }</c> — сховати пораду на добу.</summary>
    ActResult GlekAct(JsonElement payload)
    {
        var kind = Str(payload, "later");
        if (!ClickerGlekBook.Allowed.ContainsKey(kind)) return ActResult.Fail("Такої поради нема");
        var now = Ctx.Clock.UtcNow;
        var hours = GlekSvc()?.O.LaterHours ?? 24;
        _glekLater[kind] = now + TimeSpan.FromHours(Math.Clamp(hours, 0.1, 24 * 14));
        foreach (var k in _glekLater.Where(x => x.Value <= now).Select(x => x.Key).ToList()) _glekLater.Remove(k);
        _glekMoment = null;
        _glekMomentAt = default;
        _dSnapAt = default;          // картка в Літописі — одразу без цієї поради
        return ActResult.Accept("🏺 Добре, нагадаю пізніше");
    }

    Dictionary<string, DateTimeOffset>? SaveGlek() => _glekLater.Count == 0 ? null : new(_glekLater, StringComparer.Ordinal);

    void LoadGlek(Dictionary<string, DateTimeOffset>? later)
    {
        _glekLater.Clear();
        if (later is null) return;
        foreach (var (k, v) in later) if (ClickerGlekBook.Allowed.ContainsKey(k)) _glekLater[k] = v;
    }
}
