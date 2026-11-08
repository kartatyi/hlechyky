using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Книга фраз прожарки дня (<c>data/clicker/roast.json</c>): на кожен вид — кілька фраз Глека з підстановками
/// <c>{nick}</c>, числами з відмінюванням <c>{n:раз|рази|разів}</c> (також <c>points</c>, <c>days</c>: число й форма слова
/// за ним — 1 раз, 2 рази, 5 разів, 11 разів, 21 раз) і парами роду <c>{виграв|виграла}</c>. Ключі з підкресленням — коментарі автора; невідомий
/// вид чи підстановка — рядок у лозі, а не тиша на проді (так само, як книга порад Глека).
/// </summary>
public sealed class ClickerRoastBook
{
    public static readonly string[] Kinds = ["intro", "masterDay", "catcherDay", "clicks", "stoker", "envoy", "absent", "lazy", "outro"];

    static readonly string[] Vars = ["nick", "n", "points", "days"];

    readonly Dictionary<string, string[]> _pools;

    ClickerRoastBook(Dictionary<string, string[]> pools) => _pools = pools;

    public static readonly ClickerRoastBook Empty = new([]);

    public int Count(string kind) => _pools.TryGetValue(kind, out var p) ? p.Length : 0;

    /// <summary>Фраза навмання з виду; порожньо — виду в книзі нема.</summary>
    public string Pick(string kind, Random rng) =>
        _pools.TryGetValue(kind, out var pool) && pool.Length > 0 ? pool[rng.Next(pool.Length)] : "";

    public static (ClickerRoastBook Book, IReadOnlyList<string> Problems) Parse(string json)
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
            if (!Kinds.Contains(key)) { problems.Add($"{key}: невідомий вид прожарки"); continue; }
            if (el.ValueKind != JsonValueKind.Array) { problems.Add($"{key}: має бути список фраз"); continue; }
            var lines = el.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).ToArray();
            var bad = lines.SelectMany(Unknown).Distinct().ToList();
            if (bad.Count > 0) { problems.Add($"{key}: невідомі підстановки {string.Join(", ", bad.Select(b => "{" + b + "}"))}"); continue; }
            if (lines.FirstOrDefault(l => l.Count(c => c == '{') != l.Count(c => c == '}')) is { } broken)
            { problems.Add($"{key}: незакриті дужки — «{broken}»"); continue; }
            pools[key] = lines;
        }
        foreach (var key in Kinds.Where(k => !pools.ContainsKey(k) && !raw.ContainsKey(k)))
            problems.Add($"{key}: фраз нема — цього рядка прожарки не буде");
        return (new ClickerRoastBook(pools), problems);
    }

    /// <summary>Підстановки рядка, яких прожарка не знає (пари роду {а|б} не рахуються).</summary>
    static IEnumerable<string> Unknown(string line)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(line, @"\{(?:n|points|days):[^{}|:]+\|[^{}|:]+\|[^{}|:]+\}", "");
        // Відмінювання не за зразком: чужа змінна або не три форми — {x:а|б|в}, {n:раз|рази}.
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(s, @"\{(\w+):"))
            yield return m.Groups[1].Value + ":…";
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\{[^{}|]+\|[^{}|]+\}", "");
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(s, @"\{(\w+)\}"))
            if (!Vars.Contains(m.Groups[1].Value)) yield return m.Groups[1].Value;
    }

    static readonly System.Text.RegularExpressions.Regex Declined =
        new(@"\{(n|points|days):([^{}|:]+)\|([^{}|:]+)\|([^{}|:]+)\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Підставити у фразу нік, числа й рід. <c>{n:раз|рази|разів}</c> стає «12 разів» — число з пробілом між тисячами й форма
    /// слова за ним (1 раз, 2 рази, 5 разів, 11 разів, 21 раз, 22 рази); голе <c>{n}</c> — лише число.
    /// </summary>
    public static string Fill(string template, string nick, long n, long points, int days, bool she)
    {
        var s = Declined.Replace(template, m =>
        {
            var v = m.Groups[1].Value switch { "n" => n, "points" => points, _ => days };
            return ClickerRoast.Num(v) + " " + Clicker.Plural(v, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value);
        });
        return ClickerGlekBook.Fill(s, new Dictionary<string, string>
        {
            ["nick"] = nick, ["n"] = ClickerRoast.Num(n), ["points"] = ClickerRoast.Num(points), ["days"] = days.ToString(CultureInfo.InvariantCulture),
        }, she);
    }
}

/// <summary>Книга прожарки для сервісу дня: читається з файлу <see cref="ClickerDayOptions.RoastFile"/>, перечитується зі зміною файлу.</summary>
public sealed class ClickerRoastService(Func<ClickerDayOptions> options, ILogger<ClickerRoastService>? log = null)
{
    static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(30);
    readonly object _gate = new();
    ClickerRoastBook _book = ClickerRoastBook.Empty;
    DateTime _stamp = DateTime.MinValue, _checked = DateTime.MinValue;
    string _path = "";

    /// <summary>Книга для тестів і для тих, кому файл не потрібен.</summary>
    public ClickerRoastBook? Fixed { get; init; }

    public ClickerRoastBook Book
    {
        get
        {
            if (Fixed is { } f) return f;
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                var path = Paths.Resolve(options().RoastFile);
                if (path == _path && now - _checked < CheckEvery) return _book;
                _checked = now;
                var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                if (path == _path && stamp == _stamp) return _book;
                _path = path;
                _stamp = stamp;
                if (stamp == DateTime.MinValue)
                {
                    log?.LogWarning("коло: книги прожарки {Path} нема — прожарки не буде", path);
                    return _book = ClickerRoastBook.Empty;
                }
                try
                {
                    var (book, problems) = ClickerRoastBook.Parse(File.ReadAllText(path));
                    foreach (var p in problems) log?.LogWarning("коло: книга прожарки: {Problem}", p);
                    return _book = book;
                }
                catch (IOException e)
                {
                    log?.LogWarning("коло: книгу прожарки {Path} не прочитати: {Err}", path, e.Message);
                    return _book;
                }
            }
        }
    }
}

/// <summary>Переможець дошки дня для прожарки: нік і очки.</summary>
public sealed record RoastWinner(string Nick, int Points);

/// <summary>Гончар, що грав у цю добу: кліки за день і різниці лічильників з попереднім знімком щоденника.</summary>
public sealed record RoastPerson(string Nick, long Clicks, long Stoker, int Envoys);

/// <summary>Гончар, що зник: нік і скільки днів його не було видно.</summary>
public sealed record RoastAbsent(string Nick, int Days);

/// <summary>
/// Прожарка дня (v13 Г6, docs/games/specs/clicker-v13.md): о 23:30 після підсумку Глек пише в Балачки окреме повідомлення
/// з дружніми підколками — переможці «Майстра» й «Ловця дня» та ще до <see cref="Extras"/> рядків із тих, що знайшлися:
/// хто найбільше клацав, у кого палій пахав за трьох, хто наповнив посольського, хто зник, хто лише зазирнув. Дані — зі
/// щоденника кола (ClickerDiaryService), фрази — з книги <see cref="ClickerRoastBook"/>. Лише люди (щоденник гостей і
/// ботів не пише). Менше двох людей за добу — прожарки нема.
/// </summary>
public static class ClickerRoast
{
    public const int Extras = 3, WindowDays = 14, ClicksMin = 300, StokerMin = 3, LazyBelow = 200, AbsentFrom = 2;

    static readonly string[] ExtraKinds = ["clicks", "stoker", "envoy", "absent", "lazy"];

    /// <summary>
    /// Дані дня з рядків щоденника за <see cref="WindowDays"/> днів до <paramref name="day"/> включно. Різниці лічильників
    /// (партії палія, повні посольські) — з останнім знімком до цього дня; нема такого — різниця невідома, тобто нуль.
    /// </summary>
    public static (List<RoastPerson> Today, List<RoastAbsent> Absent) Gather(IEnumerable<ClickerDiaryRow> rows, string day)
    {
        var date = DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var yesterday = date.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var byKey = rows.GroupBy(r => Auth.NickKey(r.Nick)).Where(g => g.Key.Length > 0).ToList();
        var today = new List<RoastPerson>();
        var absent = new List<RoastAbsent>();
        foreach (var g in byKey)
        {
            var mine = g.OrderBy(r => r.Day, StringComparer.Ordinal).ToList();
            var now = mine.LastOrDefault(r => r.Day == day);
            var before = mine.LastOrDefault(r => string.CompareOrdinal(r.Day, day) < 0);
            if (now is not null)
            {
                var a = Read(now.Json);
                var b = before is null ? null : Read(before.Json);
                today.Add(new RoastPerson(now.Nick, a.Clicks,
                    b is null ? 0 : Math.Max(0, a.Auto - b.Auto), b is null ? 0 : (int)Math.Max(0, a.Envoys - b.Envoys)));
            }
            else if (before is not null && before.Day != yesterday
                && DateOnly.TryParseExact(before.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var last))
                absent.Add(new RoastAbsent(before.Nick, date.DayNumber - last.DayNumber));
        }
        return (today, absent);
    }

    sealed record Counters(long Clicks, long Auto, long Envoys);

    static Counters Read(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            long Num(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d) && double.IsFinite(d) ? (long)Math.Min(d, long.MaxValue / 2) : 0;
            var auto = r.TryGetProperty("kiln", out var k) && k.ValueKind == JsonValueKind.Object && k.TryGetProperty("auto", out var a) ? Num(a) : 0;
            return new Counters(r.TryGetProperty("dayClicks", out var c) ? Num(c) : 0, auto, r.TryGetProperty("envoysFull", out var e) ? Num(e) : 0);
        }
        catch (JsonException) { return new Counters(0, 0, 0); }
    }

    /// <summary>
    /// Текст прожарки або null (менше двох людей за добу, книги нема). Рядки йдуть так: intro, переможці, до трьох рядків
    /// із доступних (навмання, жодної людини двічі), outro. Рід у фразах — за <paramref name="she"/>.
    /// </summary>
    public static string? Compose(ClickerRoastBook book, IReadOnlyList<RoastWinner> master, IReadOnlyList<RoastWinner> catcher,
        IReadOnlyList<RoastPerson> today, IReadOnlyList<RoastAbsent> absent, Func<string, bool> she, Random rng)
    {
        var people = today.Select(p => Auth.NickKey(p.Nick)).Concat(master.Select(w => Auth.NickKey(w.Nick)))
            .Concat(catcher.Select(w => Auth.NickKey(w.Nick))).Where(k => k.Length > 0).Distinct().Count();
        if (people < 2) return null;

        var lines = new List<string>();
        var named = new HashSet<string>(StringComparer.Ordinal);
        void Say(string kind, string nick, long n = 0, long points = 0, int days = 0)
        {
            var template = book.Pick(kind, rng);
            if (template.Length == 0) return;
            lines.Add(ClickerRoastBook.Fill(template, nick, n, points, days, she(nick)));
            named.Add(Auth.NickKey(nick));
        }

        var intro = book.Pick("intro", rng);
        if (intro.Length > 0) lines.Add(intro);
        if (master.Count > 0) Say("masterDay", master[0].Nick, points: master[0].Points);
        if (catcher.Count > 0) Say("catcherDay", catcher[0].Nick, n: catcher[0].Points);

        var extras = 0;
        foreach (var kind in ExtraKinds.OrderBy(_ => rng.Next()))
        {
            if (extras >= Extras) break;
            var free = today.Where(p => !named.Contains(Auth.NickKey(p.Nick))).ToList();
            var before = lines.Count;
            switch (kind)
            {
                case "clicks" when free.Where(p => p.Clicks >= ClicksMin).MaxBy(p => p.Clicks) is { } p:
                    Say(kind, p.Nick, n: p.Clicks);
                    break;
                case "stoker" when free.Where(p => p.Stoker >= StokerMin).MaxBy(p => p.Stoker) is { } p:
                    Say(kind, p.Nick, n: p.Stoker);
                    break;
                case "envoy" when free.Where(p => p.Envoys >= 1).MaxBy(p => p.Envoys) is { } p:
                    Say(kind, p.Nick, n: p.Envoys);
                    break;
                case "absent" when absent.Where(a => a.Days >= AbsentFrom && !named.Contains(Auth.NickKey(a.Nick))).MaxBy(a => a.Days) is { } a:
                    Say(kind, a.Nick, days: a.Days);
                    break;
                case "lazy" when free.Where(p => p.Clicks < LazyBelow).MinBy(p => p.Clicks) is { } p:
                    Say(kind, p.Nick, n: p.Clicks);
                    break;
            }
            if (lines.Count > before) extras++;
        }
        var outro = book.Pick("outro", rng);
        if (outro.Length > 0) lines.Add(outro);
        // Самі intro/outro без жодного підсумку — не прожарка.
        return lines.Count <= (intro.Length > 0 ? 1 : 0) + (outro.Length > 0 ? 1 : 0) ? null : string.Join("\n", lines);
    }

    /// <summary>Число з пробілом між тисячами: «12 345».</summary>
    internal static string Num(long n) => n.ToString("#,##0", CultureInfo.InvariantCulture).Replace(',', ' ');
}
