using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hlechyky.Games.Impl;

/// <summary>Вимикачі «Чумаків» у конфігу (секція <c>Fura</c>, specs/fura.md «Гараж (0.0.6) — контракт»).</summary>
public sealed class FuraOptions
{
    /// <summary>Гараж 0.0.6. false — усі їдуть «Чумаком» без тюнінгу, купувати й обирати не можна; ДВ рахується як завжди.</summary>
    public bool Garage { get; set; } = true;

    /// <summary>
    /// Які мапи можна обрати опцією столу (<see cref="FuraMap.Known"/> через кому; хвиля D). Недозволена чи невідома в
    /// опціях стола — край. Прод — «kray», поки клієнт міста (D2) не готовий; дев — «kray,zhytomyr».
    /// </summary>
    public string Maps { get; set; } = FuraMap.DefaultId;

    /// <summary>Дозволені мапи: відомі з <see cref="Maps"/>; край — завжди.</summary>
    public IReadOnlyList<string> AllowedMaps() =>
    [
        .. FuraMap.Known.Where(id => id == FuraMap.DefaultId
            || (Maps ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(id, StringComparer.Ordinal)),
    ];
}

/// <summary>
/// Що стіл знає про людину поза грою: чи нік — акаунт (купувати в гаражі — лише акаунтам, як у Лавці) і що з групи
/// «🚚 Фура» Лавки вдягнуто (слот → id речі). Без сервісу (тести) — усі акаунти, нічого не вдягнуто.
/// </summary>
public sealed class FuraWardrobe(Func<string, bool>? isAccount = null, Func<string, IReadOnlyDictionary<string, string>>? worn = null)
{
    static readonly Dictionary<string, string> Nothing = new(StringComparer.Ordinal);

    public bool IsAccount(string nick) => isAccount?.Invoke(nick) ?? true;

    public IReadOnlyDictionary<string, string> Worn(string nick) => worn?.Invoke(nick) ?? Nothing;
}

/// <summary>
/// Правила гаража — <c>data/fura/garage.json</c> (той самий файл читає браузер): рівні й крива ДВ, тягачі, причепи, гілки
/// тюнінгу й стеля корисності. Тут же — рахунок рівня з ДВ і <see cref="Params"/>, готові числа фізики для браузера.
/// </summary>
public sealed class FuraBook
{
    public const string File = "data/fura/garage.json";
    public const string Chumak = "chumak", Tent = "tent";
    public const string Tractor = "tractor", Trailer = "trailer", Tune = "tune";

    public sealed record LevelRules(int Max, int Base, int Step, int Star);

    public sealed record XpRules(double Base, double PerKm, double OnTime, double Exact, double Col, double Mentor, int MentorGap,
        int MinK, int FullTrips, double Tired, int RetroPerTrip, int RetroMax);

    /// <summary>Тягач чи причіп: <see cref="Set"/> — його числа фізики (поверх «Чумака» з «Тентом»).</summary>
    public sealed record Thing(string Id, string Title, int Lvl, int Price, string Carries, double Xp, IReadOnlyDictionary<string, double> Set);

    /// <summary>
    /// Гілка тюнінгу: <see cref="Mul"/> — +частка параметра за щабель (на перших <see cref="MulSteps"/>), <see cref="HurtPer"/> —
    /// множник шкоди за щабель (0 — нема), <see cref="Set"/> — що вмикає щабель N (число чи так/ні).
    /// </summary>
    public sealed record Branch(string Id, string Title, int Steps, int[] Levels, IReadOnlyDictionary<string, double> Mul, int MulSteps,
        double HurtPer, IReadOnlyDictionary<int, IReadOnlyDictionary<string, object>> Set);

    /// <summary>Що можна купити: тягач, причіп або щабель (<see cref="Step"/> гілки <see cref="Branch"/>).</summary>
    public sealed record Buyable(string Id, string Kind, string Title, int Lvl, int Price, string? Branch = null, int Step = 0);

    public required string Raw { get; init; }
    public required LevelRules Levels { get; init; }
    public required XpRules Xp { get; init; }
    public required int FarLvl { get; init; }
    public required double FarXp { get; init; }
    public required double CapPower { get; init; }
    public required double CapHurt { get; init; }
    public required double CapLimits { get; init; }
    public required IReadOnlyDictionary<string, object> Base { get; init; }
    public required IReadOnlyList<Thing> Tractors { get; init; }
    public required IReadOnlyList<Thing> Trailers { get; init; }
    public required IReadOnlyList<int> Prices { get; init; }
    public required IReadOnlyList<int> StepLevels { get; init; }
    public required IReadOnlyList<Branch> Branches { get; init; }
    /// <summary>Рівень → id титулу Лавки, що він дає (ачівка <c>fura-&lt;id&gt;</c>).</summary>
    public required IReadOnlyDictionary<int, string> Titles { get; init; }
    /// <summary>Догляд (Гараж 2) — лише вигляд: свій номер, мийка, рихтування й як швидко брудниться та б'ється.</summary>
    public required CareRules Care { get; init; }

    /// <summary>
    /// Ціни догляду й темп: бруд — <see cref="DirtPerKm"/> за км рейсу; вм'ятини — <see cref="DentsPerLoss"/> × частка
    /// розбитого вантажу (здав 80 % — +0,2 × це) і <see cref="DentsPerHit"/> за удар чужої фури (масштаб — сила удару).
    /// </summary>
    public sealed record CareRules(int Plate, int Wash, int Fix, double DirtPerKm, double DentsPerLoss, double DentsPerHit);

    static readonly OkOnce<FuraBook> Default = new(() => Parse(System.IO.File.ReadAllText(Paths.Resolve(File))));

    /// <summary>
    /// Книга з диска (раз за життя процесу; перший раз — з Configure, поза замком кімнати). Пам'ятає лише успіх: невдалий
    /// читок (файл саме переписують) не лишає столи fura без книги до перезапуску — наступний виклик читає знову.
    /// </summary>
    public static FuraBook Load() => Default.Value;

    /// <summary>«Віл», «Двигун, щабель 2» — підпис покупки в гаманці. Невідомий id — як є.</summary>
    public static string Label(string id)
    {
        try { return Load().Find(id)?.Title ?? id; }
        catch (Exception) { return id; }
    }

    public static FuraBook Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        static double D(JsonElement e, string name, double fallback) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;
        static int I(JsonElement e, string name, int fallback) => (int)Math.Round(D(e, name, fallback));
        static string S(JsonElement e, string name, string fallback) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
        static int[] Ints(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? [.. v.EnumerateArray().Select(x => (int)Math.Round(x.GetDouble()))] : [];
        static object? Value(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
        static Dictionary<string, object> Values(JsonElement e)
        {
            var map = new Dictionary<string, object>(StringComparer.Ordinal);
            if (e.ValueKind == JsonValueKind.Object)
                foreach (var p in e.EnumerateObject())
                    if (Value(p.Value) is { } v) map[p.Name] = v;
            return map;
        }

        var lv = r.GetProperty("levels");
        var xp = r.GetProperty("xp");
        var far = r.TryGetProperty("far", out var f) ? f : default;
        var cap = r.GetProperty("cap");
        var baseSet = Values(r.GetProperty("base"));
        // Числа фізики тягача/причепа — лише ті, що є в base: решта полів (title, note, sound…) у фізику не йде
        Thing Read(JsonElement t) => new(S(t, "id", ""), S(t, "title", ""), Math.Max(1, I(t, "lvl", 1)), Math.Max(0, I(t, "price", 0)),
            S(t, "carries", Tent), D(t, "xp", 1),
            t.EnumerateObject().Where(p => baseSet.ContainsKey(p.Name) && p.Value.ValueKind == JsonValueKind.Number)
                .ToDictionary(p => p.Name, p => p.Value.GetDouble(), StringComparer.Ordinal));
        var tune = r.GetProperty("tune");
        var branches = new List<Branch>();
        foreach (var b in tune.GetProperty("branches").EnumerateArray())
        {
            var mul = b.TryGetProperty("mul", out var m) && m.ValueKind == JsonValueKind.Object
                ? m.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble(), StringComparer.Ordinal)
                : new Dictionary<string, double>(StringComparer.Ordinal);
            var set = new Dictionary<int, IReadOnlyDictionary<string, object>>();
            if (b.TryGetProperty("set", out var s) && s.ValueKind == JsonValueKind.Object)
                foreach (var p in s.EnumerateObject())
                    if (int.TryParse(p.Name, out var step)) set[step] = Values(p.Value);
            var steps = Math.Max(1, I(b, "steps", 1));
            branches.Add(new Branch(S(b, "id", ""), S(b, "title", ""), steps, Ints(b, "levels"), mul, Math.Clamp(I(b, "mulSteps", steps), 0, steps),
                D(b, "hurtPer", 0), set));
        }
        var care = r.TryGetProperty("care", out var ca) && ca.ValueKind == JsonValueKind.Object ? ca : JsonDocument.Parse("{}").RootElement;
        var titles = new Dictionary<int, string>();
        if (r.TryGetProperty("titles", out var ti) && ti.ValueKind == JsonValueKind.Object)
            foreach (var p in ti.EnumerateObject())
                if (int.TryParse(p.Name, out var l) && p.Value.GetString() is { Length: > 0 } id) titles[l] = id;

        return new FuraBook
        {
            Raw = json,
            Levels = new(I(lv, "max", 20), I(lv, "base", 150), I(lv, "step", 50), Math.Max(1, I(lv, "star", 2500))),
            Xp = new(D(xp, "base", 20), D(xp, "perKm", 25), D(xp, "onTime", 1.1), D(xp, "exact", 1.1), D(xp, "col", 1.15),
                D(xp, "mentor", 1.2), I(xp, "mentorGap", 5), I(xp, "minK", 20), I(xp, "fullTrips", 10), D(xp, "tired", 0.5),
                I(xp, "retroPerTrip", 80), I(xp, "retroMax", 2100)),
            FarLvl = far.ValueKind == JsonValueKind.Object ? I(far, "lvl", 10) : int.MaxValue,
            FarXp = far.ValueKind == JsonValueKind.Object ? D(far, "xp", 1.3) : 1,
            CapPower = D(cap, "power", double.MaxValue),
            CapHurt = D(cap, "hurt", 0),
            CapLimits = D(cap, "limits", double.MaxValue),
            Base = baseSet,
            Tractors = [.. r.GetProperty("tractors").EnumerateArray().Select(Read)],
            Trailers = [.. r.GetProperty("trailers").EnumerateArray().Select(Read)],
            Prices = Ints(tune, "prices"),
            StepLevels = Ints(tune, "levels"),
            Branches = branches,
            Titles = titles,
            Care = new(I(care, "plate", 1000), I(care, "wash", 100), I(care, "fix", 300), D(care, "dirtPerKm", 0.05),
                D(care, "dentsPerLoss", 0.5), D(care, "dentsPerHit", 0.1)),
        };
    }

    // ---------- рівні ----------

    /// <summary>Скільки ДВ усього треба, щоб стати рівнем <paramref name="lvl"/> (рівень 1 — 0).</summary>
    public long Total(int lvl)
    {
        long t = 0;
        for (var l = 1; l < Math.Min(lvl, Levels.Max + 1); l++) t += Levels.Base + Levels.Step * (l - 1);
        return t;
    }

    public int LevelOf(long xp)
    {
        var l = 1;
        while (l < Levels.Max && xp >= Total(l + 1)) l++;
        return l;
    }

    /// <summary>Ходки після останнього рівня: кожні <see cref="LevelRules.Star"/> ДВ — зірочка.</summary>
    public int StarsOf(long xp) => xp < Total(Levels.Max) ? 0 : (int)Math.Min(int.MaxValue, (xp - Total(Levels.Max)) / Levels.Star);

    // ---------- речі гаража ----------

    public Thing? TractorOf(string? id) => Tractors.FirstOrDefault(t => t.Id == id);
    public Thing? TrailerOf(string? id) => Trailers.FirstOrDefault(t => t.Id == id);
    /// <summary>Причіп, що везе вантажі типу <paramref name="carries"/> (перший у списку: для «tent» — сам «Тент»).</summary>
    public Thing? TrailerFor(string carries) => Trailers.FirstOrDefault(t => t.Carries == carries);
    public Branch? BranchOf(string? id) => Branches.FirstOrDefault(b => b.Id == id);

    /// <summary>Рівень щабля <paramref name="step"/> (1…) гілки: свій список гілки, інакше спільний.</summary>
    public int StepLevel(Branch b, int step)
    {
        var list = b.Levels.Length > 0 ? b.Levels : [.. StepLevels];
        return list.Length == 0 ? 1 : list[Math.Clamp(step, 1, list.Length) - 1];
    }

    public int StepPrice(int step) => Prices.Count == 0 ? 0 : Prices[Math.Clamp(step, 1, Prices.Count) - 1];

    /// <summary>Що купується за id: <c>vil</c>, <c>izoterm</c>, <c>engine2</c> (гілка + номер щабля). null — нема такого.</summary>
    public Buyable? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (TractorOf(id) is { } t) return new(t.Id, Tractor, t.Title, t.Lvl, t.Price);
        if (TrailerOf(id) is { } tr) return new(tr.Id, Trailer, tr.Title, tr.Lvl, tr.Price);
        var digits = id.Length;
        while (digits > 0 && char.IsAsciiDigit(id[digits - 1])) digits--;
        if (digits == id.Length || digits == 0 || BranchOf(id[..digits]) is not { } b) return null;
        if (!int.TryParse(id[digits..], out var step) || step < 1 || step > b.Steps) return null;
        return new($"{b.Id}{step}", Tune, $"{b.Title}, щабель {step}", StepLevel(b, step), StepPrice(step), b.Id, step);
    }

    // ---------- фізика ----------

    /// <summary>
    /// Готові числа фізики (specs/fura.md, «params»): «Чумак» із «Тентом» (<c>base</c>) → тягач → причіп → щаблі тюнінгу →
    /// стеля корисності. <c>limits</c> і <c>hurt</c> тягача й причепа множаться (вони відносні), решта — ставиться.
    /// <paramref name="tune"/> null — без тюнінгу («⚖ Рівні фури», гість, гараж вимкнено).
    /// </summary>
    public Dictionary<string, object> Params(string tractor, string trailer, IReadOnlyDictionary<string, int>? tune)
    {
        var p = new Dictionary<string, object>(StringComparer.Ordinal) { ["tractor"] = tractor, ["trailer"] = trailer };
        foreach (var (k, v) in Base) p[k] = v;
        double N(string k) => p.TryGetValue(k, out var v) && v is double d ? d : 0;
        void Put(Thing? t)
        {
            if (t is null) return;
            foreach (var (k, v) in t.Set) p[k] = k is "limits" or "hurt" ? N(k) * v : v;
        }
        Put(TractorOf(tractor));
        Put(TrailerOf(trailer));
        if (tune is not null)
            foreach (var b in Branches)
            {
                var n = Math.Clamp(tune.TryGetValue(b.Id, out var x) ? x : 0, 0, b.Steps);
                if (n == 0) continue;
                foreach (var (k, per) in b.Mul) p[k] = N(k) * (1 + per * Math.Min(n, b.MulSteps));
                if (b.HurtPer > 0) p["hurt"] = N("hurt") * Math.Pow(b.HurtPer, n);
                for (var s = 1; s <= n; s++)
                    if (b.Set.TryGetValue(s, out var set))
                        foreach (var (k, v) in set) p[k] = v;
            }
        // стеля корисності: прокачаний не «чит» (GARAGE.md §3)
        if (N("power") > CapPower) p["power"] = CapPower;
        if (N("hurt") < CapHurt) p["hurt"] = CapHurt;
        if (N("limits") > CapLimits) p["limits"] = CapLimits;
        foreach (var k in p.Keys.ToList())
            if (p[k] is double d) p[k] = Math.Round(d, 4);
        return p;
    }
}

/// <summary>Ліниве значення, що пам'ятає лише успіх: виняток іде викликачу, а наступний виклик пробує знову (на відміну від <see cref="Lazy{T}"/>).</summary>
public sealed class OkOnce<T>(Func<T> make) where T : class
{
    readonly object _gate = new();
    T? _value;

    public T Value
    {
        get
        {
            if (Volatile.Read(ref _value) is { } v) return v;
            lock (_gate)
            {
                if (_value is null) Volatile.Write(ref _value, make());
                return _value!;
            }
        }
    }
}

/// <summary>
/// Гараж водія (0.0.6): досвід (ДВ), хто скільки возив сьогодні, куплені тягачі, причепи й щаблі, що обрано. Належить ніку:
/// <see cref="FuraGarage"/> під <c>fura:garage:&lt;nickKey&gt;</c> (окремо від фури — поза раз на 5 с його не переписує),
/// за тією самою заявкою стола. Стан — публічні властивості (як у <see cref="FuraRig"/>); [JsonIgnore] — те, що знає стіл.
/// </summary>
public sealed class FuraCareer
{
    public static string Key(string nickKey) => $"fura:garage:{nickKey}";

    public int V { get; set; } = 1;
    public long Xp { get; set; }
    /// <summary>Номер останнього рейсу, за який ДВ уже зараховано: рейс № ≤ цього вдруге ДВ не дає.</summary>
    public int LastTrip { get; set; }
    /// <summary>Київський день, до якого рахується <see cref="DayTrips"/>.</summary>
    public string Day { get; set; } = "";
    public int DayTrips { get; set; }
    public FuraOwned Owned { get; set; } = new();
    /// <summary>Гілка → скільки щаблів куплено.</summary>
    public Dictionary<string, int> Tune { get; set; } = new(StringComparer.Ordinal);
    public FuraPick Pick { get; set; } = new();
    /// <summary>Що дав останній рейс (підсумок «+105 ДВ», новий рівень — голос Глека).</summary>
    public FuraGain? Last { get; set; }
    /// <summary>Свій номер (≤ 8 знаків, <see cref="CleanPlate"/>); null — номер із ніка (браузер, <c>plateOf</c>).</summary>
    public string? Plate { get; set; }
    /// <summary>Бруд 0…1 (росте з км рейсів, мийка → 0) і вм'ятини 0…1 (розбитий вантаж, удари; рихтування → 0). Лише вигляд.</summary>
    public double Dirt { get; set; }
    public double Dents { get; set; }

    /// <summary>Змінилось і ще не віддано в гараж.</summary>
    [JsonIgnore] public bool Dirty { get; set; }
    [JsonIgnore] public bool Account { get; set; }
    /// <summary>Вдягнуте з Лавки («🚚 Фура»): слот → id речі.</summary>
    [JsonIgnore] public IReadOnlyDictionary<string, string> Worn { get; set; } = new Dictionary<string, string>();

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// Гараж із JSON. null — нема чи нечитний зовсім. Невідомі (вже нема в книзі) речі й гілки відкидаються, щаблі — у межах
    /// гілки, «Чумак» і «Тент» є завжди, обране — лише своє.
    /// </summary>
    public static FuraCareer? Parse(string? json, FuraBook book)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        FuraCareer? c;
        try { c = JsonSerializer.Deserialize<FuraCareer>(json, Json); }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
        if (c is null) return null;
        c.Xp = Math.Max(0, c.Xp);
        c.LastTrip = Math.Max(0, c.LastTrip);
        c.DayTrips = Math.Max(0, c.DayTrips);
        c.Day ??= "";
        c.Plate = CleanPlate(c.Plate);
        c.Dirt = Wear01(c.Dirt);
        c.Dents = Wear01(c.Dents);
        c.Owned ??= new();
        c.Owned.Tractors = Known(c.Owned.Tractors, FuraBook.Chumak, id => book.TractorOf(id) is not null);
        c.Owned.Trailers = Known(c.Owned.Trailers, FuraBook.Tent, id => book.TrailerOf(id) is not null);
        var tune = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (k, v) in c.Tune ?? [])
            if (book.BranchOf(k) is { } b && v > 0) tune[k] = Math.Min(v, b.Steps);
        c.Tune = tune;
        c.Pick ??= new();
        if (!c.Owned.Tractors.Contains(c.Pick.Tractor)) c.Pick.Tractor = FuraBook.Chumak;
        if (!c.Owned.Trailers.Contains(c.Pick.Trailer) || book.TrailerOf(c.Pick.Trailer)?.Carries != FuraBook.Tent) c.Pick.Trailer = FuraBook.Tent;
        c.V = 1;
        return c;
    }

    static List<string> Known(List<string>? ids, string first, Func<string, bool> known)
    {
        var list = new List<string> { first };
        foreach (var id in ids ?? [])
            if (id is not null && known(id) && !list.Contains(id)) list.Add(id);
        return list;
    }

    public const int PlateMax = 8;

    /// <summary>
    /// Свій номер: великі літери (українська й латинська абетки), цифри, пробіл і дефіс; решта викидається, пробіли не
    /// подвоюються, ≤ <see cref="PlateMax"/> знаків. Порожній — null (номер із ніка).
    /// </summary>
    public static string? CleanPlate(string? raw)
    {
        if (raw is null) return null;
        var sb = new System.Text.StringBuilder();
        foreach (var ch in raw.Normalize(System.Text.NormalizationForm.FormC).ToUpperInvariant())
        {
            var ok = ch is >= 'A' and <= 'Z' or >= '0' and <= '9' or >= 'А' and <= 'Я' or 'Є' or 'І' or 'Ї' or 'Ґ' or '-'
                && ch is not ('Ы' or 'Э' or 'Ъ' or 'Ё');
            if (ok) sb.Append(ch);
            else if (char.IsWhiteSpace(ch) && sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        }
        var s = sb.ToString().Trim();
        if (s.Length > PlateMax) s = s[..PlateMax].TrimEnd();
        return s.Length == 0 ? null : s;
    }

    static double Wear01(double x) => double.IsFinite(x) ? Math.Clamp(Math.Round(x, 3), 0, 1) : 0;

    /// <summary>
    /// Знос за щойно зданий рейс <paramref name="trip"/> (ідемпотентно разом із ДВ: рейс, уже зарахований, не брудить удруге).
    /// Бруд — від км, вм'ятини — від розбитого вантажу (ціле <paramref name="k"/> %).
    /// </summary>
    public void Wear(FuraBook book, int trip, double km, int k)
    {
        if (trip <= LastTrip) return;
        Dirt = Wear01(Dirt + Math.Max(0, km) * book.Care.DirtPerKm);
        Dents = Wear01(Dents + Math.Clamp(100 - k, 0, 100) / 100.0 * book.Care.DentsPerLoss);
    }

    /// <summary>Удар чужої фури (сила 0…1 від найбільшої): вм'ятини. true — помітно змінилось (≥ 0,05), варто записати.</summary>
    public bool Hit(FuraBook book, double force)
    {
        var before = Dents;
        Dents = Wear01(Dents + Math.Clamp(force, 0, 1) * book.Care.DentsPerHit);
        var seen = Math.Floor(Dents * 20) != Math.Floor(before * 20);
        if (seen) Dirty = true;
        return seen;
    }

    /// <summary>Перше відкриття гаража: ретро-досвід за вже зроблені рейси, не вище Р8 (<see cref="FuraBook.XpRules.RetroMax"/>).</summary>
    public static FuraCareer Retro(int trips, FuraBook book) => new()
    {
        Xp = Math.Min((long)Math.Max(0, trips) * book.Xp.RetroPerTrip, book.Xp.RetroMax),
        LastTrip = Math.Max(0, trips),
        Dirty = true,
    };

    public int TuneOf(string branch) => Tune.TryGetValue(branch, out var n) ? n : 0;

    public bool Has(FuraBook.Buyable item) => item.Kind switch
    {
        FuraBook.Tractor => Owned.Tractors.Contains(item.Id),
        FuraBook.Trailer => Owned.Trailers.Contains(item.Id),
        _ => TuneOf(item.Branch!) >= item.Step,
    };

    public void Grant(FuraBook.Buyable item)
    {
        switch (item.Kind)
        {
            case FuraBook.Tractor: if (!Owned.Tractors.Contains(item.Id)) Owned.Tractors.Add(item.Id); break;
            case FuraBook.Trailer: if (!Owned.Trailers.Contains(item.Id)) Owned.Trailers.Add(item.Id); break;
            default: Tune[item.Branch!] = Math.Max(TuneOf(item.Branch!), item.Step); break;
        }
        Dirty = true;
    }

    /// <summary>
    /// ДВ за рейс <paramref name="trip"/> (specs/fura.md, «Досвід водія»). Рейс, за який ДВ уже було (№ ≤ <see cref="LastTrip"/>),
    /// — null, нічого не змінено. Інакше рахує й день (втома — з 11-го рейсу за київський день) і повертає, що дав рейс.
    /// </summary>
    public FuraGain? Gain(FuraBook book, int trip, double km, int k, bool onTime, bool exact, bool col, bool mentor,
        double trailerXp, bool far, string day)
    {
        if (trip <= LastTrip) return null;
        var r = book.Xp;
        if (Day != day)
        {
            Day = day;
            DayTrips = 0;
        }
        var tired = DayTrips >= r.FullTrips;
        DayTrips++;
        LastTrip = trip;
        var lvl = book.LevelOf(Xp);
        var xp = 0;
        if (k >= r.MinK)
        {
            var x = Math.Round((r.Base + r.PerKm * km) * k / 100.0, MidpointRounding.AwayFromZero);
            if (onTime) x *= r.OnTime;
            if (exact) x *= r.Exact;
            if (col) x *= r.Col;
            if (mentor) x *= r.Mentor;
            if (far) x *= book.FarXp;
            x *= trailerXp;
            if (tired) x *= r.Tired;
            xp = (int)Math.Round(x, MidpointRounding.AwayFromZero);
        }
        Xp += xp;
        var now = book.LevelOf(Xp);
        Last = new FuraGain(trip, xp, now > lvl ? now : null, tired, mentor);
        Dirty = true;
        return Last;
    }
}

public sealed class FuraOwned
{
    public List<string> Tractors { get; set; } = [FuraBook.Chumak];
    public List<string> Trailers { get; set; } = [FuraBook.Tent];
}

/// <summary>Обраний тягач і причіп під звичайні («тентові») вантажі; «Ізотерм» і «Низькорамник» чіпляються самі під свій вантаж.</summary>
public sealed class FuraPick
{
    public string Tractor { get; set; } = FuraBook.Chumak;
    public string Trailer { get; set; } = FuraBook.Tent;
}

/// <summary>Що дав рейс: ДВ, новий рівень (null — той самий), чи вже була втома, чи був наставник у колоні.</summary>
public sealed record FuraGain(int Trip, int Xp, int? Up, bool Tired, bool Mentor);
