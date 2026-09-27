using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Посилка на чужу толоку: від кого, на яку будову й етап її несли, виріб і скільки. Лежить у скриньці цеху, доки
/// господар толоки не зробить першу дію (як дарунки й гостинці).
/// </summary>
public sealed record TolokaParcel(string From, string FromKey, string Building, int Stage, string Ware, string Style,
    int Quality, int N, DateTimeOffset At);

/// <summary>
/// Чужа толока очима помічника — зі збереження друга (живе коло може бути на дію новішим). <see cref="EndsAt"/> —
/// коли достроїться закладений етап (null — етап ще збирають); <see cref="Got"/> — скільки вже піднесли друзі за
/// кожною вимогою; <see cref="Helpers"/> — хто вже був на цьому етапі.
/// </summary>
public sealed record TolokaPeek(ClickerBuilding Building, int Stage, DateTimeOffset? EndsAt, int[] Got, IReadOnlyList<string> Helpers)
{
    public TolokaStage StageRow => Building.Stages[Stage];
}

/// <summary>Що вийшло з посилкою: відмова або скільки виробів пішло, на яку будову й етап.</summary>
public sealed record TolokaSent(string? Error, int N = 0, string Building = "", string Stage = "");

/// <summary>
/// Друзі на толоці (пакет A одинадцятого оновлення, docs/games/specs/clicker-v11.md §2, як зроблено —
/// clicker-v11-a.md). Помічник підносить вироби зі своєї комори на поточний етап друга (<c>guild {op:"toloka"}</c>),
/// пошта цеху везе їх навіть до того, хто зараз не грає, а господар на першій дії приймає посилку через
/// <see cref="TolokaReceive"/>: вироби йдуть у рядки вимог, а сам друг скорочує будову (−15 %, до трьох друзів).
/// Помічникові — «гостинець толоки»: 10 хв власного пасиву за кожен виріб.
///
/// Чому помічник дивиться у збереження друга, а не в його кімнату: цех не тримає посилань на кімнати (два замки в
/// різному порядку — і сервер став). Збереження пишеться після кожної дії, а годинник етапу, що достроївся офлайн,
/// ми докручуємо самі (<see cref="TolokaPeekSave"/>), тож помічник бачить те саме, що побачить друг, коли зайде.
/// </summary>
public sealed partial class Clicker
{
    /// <summary>Стільки виробів можна піднести одним разом.</summary>
    public const int TolokaHelpMax = 10;
    /// <summary>Гостинець толоки: стільки хвилин власного пасиву помічникові за кожен піднесений виріб.</summary>
    public const int TolokaTreatMinutes = 10;

    static readonly JsonSerializerOptions TolokaWire = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Поточна будова за правилами <see cref="TolokaCurrent"/>, але для чужого збереження: перша незбудована, якщо до
    /// неї вже доріс (велика — з червоного золотого або коли одна велика вже стоїть).
    /// </summary>
    static ClickerBuilding? TolokaNextOf(IReadOnlySet<string> built, double total)
    {
        if (total < TolokaSmallFrom && built.Count == 0) return null;
        foreach (var b in Buildings)
        {
            if (built.Contains(b.Key)) continue;
            return b.Big && total < TolokaBigFrom && !built.Any(k => BuildingOf(k)?.Big == true) ? null : b;
        }
        return null;
    }

    /// <summary>
    /// Толока друга з його збереження станом на <paramref name="now"/>: якщо закладений етап уже достроївся, поки
    /// друга не було, — дивимось на наступний (так само зробить його <see cref="SyncToloka"/>). null — будови нема:
    /// ще нема гривні, чекає червоного золотого, усе збудовано або збереження зіпсоване.
    /// </summary>
    internal static TolokaPeek? TolokaPeekSave(string? json, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        JsonObject root;
        TolokaRow? row;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return null;
            root = o;
            row = root["toloka"] is JsonObject t ? t.Deserialize<TolokaRow>(TolokaWire) : null;
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }

        var total = root["total"] is JsonValue tv && tv.TryGetValue<double>(out var tot) && double.IsFinite(tot) ? tot : 0;
        var built = new HashSet<string>((row?.Built ?? []).Where(k => BuildingOf(k) is not null), StringComparer.Ordinal);
        if (TolokaNextOf(built, total) is not { } b) return null;

        var stage = Math.Max(0, row?.Stage ?? 0);
        var laidAt = row?.LaidAt ?? default;
        var helpers = (row?.Helpers ?? []).Where(h => h is { Length: > 0 and <= 64 }).Take(8).ToList();
        var got = new int[8];
        foreach (var (i, n) in row?.Got ?? [])
            if (i is >= 0 and < 8 && n > 0) got[i] = Math.Min(n, 10_000);
        if (stage >= b.Stages.Length) { stage = 0; laidAt = default; Array.Clear(got); }

        DateTimeOffset? ends = null;
        if (laidAt != default)
        {
            var relic = RelicSumOf("toloka", root["relics"] is JsonObject r && r["toloka"] is JsonValue lv && lv.TryGetValue<int>(out var lvl) ? lvl : 0);
            var cut = Math.Min(TolokaCutMax, TolokaHelperCut * Math.Min(TolokaHelpersMax, helpers.Count) + relic);
            var at = (laidAt > now ? now : laidAt) + TimeSpan.FromHours(b.Stages[stage].Hours * (1 - cut));
            if (at > now) ends = at;
            else
            {
                // Етап достроївся без господаря: наступний ще ніхто не закладав, друзів на ньому ще не було.
                helpers.Clear();
                Array.Clear(got);
                if (++stage >= b.Stages.Length)
                {
                    built.Add(b.Key);
                    if (TolokaNextOf(built, total) is not { } next) return null;
                    b = next;
                    stage = 0;
                }
            }
        }
        return new TolokaPeek(b, stage, ends, got[..b.Stages[stage].Needs.Length], helpers);
    }

    /// <summary>Сума важеля реліквії на рівні <paramref name="level"/> — те саме, що <see cref="Relic"/>, для чужого збереження.</summary>
    static double RelicSumOf(string key, int level)
    {
        if (level <= 0 || Relics.FirstOrDefault(x => x.Key == key) is not { } r) return 0;
        var sum = r.Step * Math.Min(level, RelicMaxLevel);
        return r.Cap > 0 ? Math.Min(r.Cap, sum) : sum;
    }

    /// <summary>Чи йде цей виріб хоч у якусь вимогу етапу.</summary>
    internal static bool TolokaFits(TolokaStage st, ItemInfo item) => st.Needs.Any(n => TolokaFitsNeed(n, item));

    /// <summary>Та сама відповідність, що й у <see cref="TolokaLay"/>: виріб, якість не нижче, розпис, якщо просили.</summary>
    internal static bool TolokaFitsNeed(TolokaNeed n, ItemInfo item) => TolokaMatch(n)(item);

    /// <summary>«глечик, добрий і кращий; миска» — чого просить етап, для відмови «не те несеш».</summary>
    internal static string TolokaNeedsText(TolokaStage st) => string.Join("; ", st.Needs.Select(NeedWord));

    // ---------- дія помічника: guild { op: "toloka", to, key, n } ----------

    /// <summary>
    /// Піднести вироби з власної комори на поточний етап друга. Цех перевіряє, чи етап цього ще просить (з урахуванням
    /// того, що вже їде поштою), і кладе посилку в скриньку; тут — вироби з комори, лічильник і гостинець толоки.
    /// </summary>
    ActResult GuildToloka(ClickerGuildService svc, JsonElement payload, DateTimeOffset now)
    {
        if (ParseItem(Str(payload, "key")) is not { } it) return ActResult.Fail("Такого виробу в коморі нема");
        var have = ItemCount(x => x == it);
        if (have <= 0) return ActResult.Fail("Такого виробу в коморі нема");
        var raw = Num(payload, "n") ?? 1;
        if (raw <= 0) return ActResult.Fail("Скільки піднести — хоч один");
        var n = (int)Math.Min(Math.Min(raw, TolokaHelpMax), have);
        var to = Str(payload, "to").Trim();
        var r = svc.TolokaSend(GuildKey, GuildNick, to, it, n, now);
        if (r.Error is { } why) return ActResult.Fail(why);
        TakeItems(x => x == it, r.N);
        TolokaHelped(r.N);
        var gain = TreatGain(TolokaTreatMinutes * r.N);
        Add(gain);
        return ActResult.Accept($"🤝 {r.N} × {WareOf(it.Ware)!.Name.ToLowerInvariant()} — на толоку до {to} («{r.Stage}»). "
            + $"Гостинець толоки: +{PotsShort(gain)}");
    }

    /// <summary>
    /// Посилки з чужих толок, що чекали в цеху (кличе <see cref="SyncGuild"/> лише на дії — як дарунки: забране у виді
    /// жило б тільки в пам'яті кімнати й губилось би з перезапуском).
    /// </summary>
    void TakeTolokaMail(ClickerGuildService svc, DateTimeOffset now)
    {
        if (svc.TakeToloka(GuildKey) is not { } box) return;
        // Спершу годинник: етап, що достроївся, поки посилка їхала, — уже готовий, і помічник ніс на НАСТУПНИЙ
        // (так його бачив цех, TolokaPeekSave). Інакше вироби впали б у комору, а поміч — на вже зведений етап.
        SyncToloka(now);
        foreach (var p in box)
        {
            if (WareOf(p.Ware) is null || p.Quality is < 1 or > QualityMax || p.N <= 0) continue;
            var style = p.Style ?? "";
            if (style.Length > 0 && Styles.All(s => s.Key != style)) continue;
            var from = Who(p.From) is { Length: > 0 } f ? f : "друг";
            var item = new ItemInfo(p.Ware, style, p.Quality);
            var n = Math.Min(p.N, TolokaHelpMax);
            TolokaReceive(from, item, n);
            AwayNote($"🤝 {from} підніс на толоку: {n} × {WareOf(p.Ware)!.Name.ToLowerInvariant()}");
        }
    }
}

/// <summary>Цех — пошта толоки: скринька посилок і перевірка «чи етап цього ще просить».</summary>
public sealed partial class ClickerGuildService
{
    sealed partial class State
    {
        /// <summary>Посилки на толоку за ніком господаря — він забере їх на першій дії (пакет A v11).</summary>
        public Dictionary<string, List<TolokaParcel>> Toloka { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>Скільки посилок може чекати в одній скриньці толоки: більше — хай господар спершу зайде.</summary>
    public const int TolokaMailMax = 40;

    /// <summary>Збереження кола друга — поза замком цеху (база повільна). null — нема або база не відповіла.</summary>
    string? SaveOf(string key)
    {
        try { return _store.LoadState("clicker:" + key); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "збереження кола {Nick} не прочиталось", key);
            return null;
        }
    }

    /// <summary>
    /// Скільки ще бракує за кожною вимогою етапу друга після того, що вже піднесли, і того, що їде поштою (посилки
    /// розкладаються по вимогах так само, як їх розкладе <see cref="Clicker.TolokaReceive"/>).
    /// </summary>
    static int[] TolokaLeft(TolokaPeek peek, IEnumerable<TolokaParcel> pending)
    {
        var st = peek.StageRow;
        var left = st.Needs.Select((n, i) => Math.Max(0, n.N - peek.Got[i])).ToArray();
        if (peek.EndsAt is not null) return left;          // етап уже будується — посилки йдуть у комору, не в рядки
        foreach (var p in pending)
        {
            var item = new ItemInfo(p.Ware, p.Style, p.Quality);
            var rest = p.N;
            for (var i = 0; i < left.Length && rest > 0; i++)
            {
                if (!Clicker.TolokaFitsNeed(st.Needs[i], item)) continue;
                var take = Math.Min(rest, left[i]);
                left[i] -= take;
                rest -= take;
            }
        }
        return left;
    }

    List<TolokaParcel> PendingLocked(State s, string toKey, TolokaPeek peek) =>
        s.Toloka.TryGetValue(toKey, out var box)
            ? box.Where(p => p.Building == peek.Building.Key && p.Stage == peek.Stage).ToList()
            : [];

    /// <summary>
    /// Піднести на толоку друга: null в <c>Error</c> — посилка вже в скриньці. Вироби з комори забирає кімната лише
    /// після «так». Правила:
    /// - етап збирають — беремо не більше, ніж ще бракує за вимогами, у які виріб іде;
    /// - етап уже будується — вироби ляжуть у комору друга на наступні етапи, а сам помічник скоротить будову; тож
    ///   раз на етап від кожного (інакше двоє з закладеними етапами ганяли б вироби туди-сюди заради гостинців);
    /// - виріб, що не йде в жодну вимогу, — відмова з тим, чого етап просить.
    /// </summary>
    public TolokaSent TolokaSend(string fromKey, string fromNick, string toNick, ItemInfo item, int n, DateTimeOffset now)
    {
        var toKey = Key(toNick);
        var who = toNick.Trim();
        if (toKey.Length == 0) return new("Кому нести? Обери гончаря");
        if (toKey == fromKey) return new("На свою толоку — «Закласти етап» у «Селі» 🙂");
        if (n <= 0) return new("Скільки піднести — хоч один");
        var json = SaveOf(toKey);
        if (string.IsNullOrEmpty(json)) return new($"{who} ще не сідав за гончарне коло — толоки там нема");
        if (Clicker.TolokaPeekSave(json, now) is not { } peek) return new($"{who}: зараз на толоці нема будови");
        var st = peek.StageRow;
        var where = $"«{peek.Building.Name}», етап «{st.Name}»";
        if (!Clicker.TolokaFits(st, item)) return new($"Цей виріб на {where} не йде. Просять: {Clicker.TolokaNeedsText(st)}");
        lock (_lock)
        {
            var s = S();
            var pending = PendingLocked(s, toKey, peek);
            int take;
            if (peek.EndsAt is not null)
            {
                var been = peek.Helpers.Contains(fromNick.Trim(), StringComparer.OrdinalIgnoreCase)
                    || pending.Any(p => p.FromKey == fromKey);
                if (been) return new($"{who}: «{st.Name}» уже будується, а ти на цьому етапі вже був(ла) — неси на наступний");
                take = n;
            }
            else
            {
                var left = TolokaLeft(peek, pending);
                var room = st.Needs.Select((x, i) => Clicker.TolokaFitsNeed(x, item) ? left[i] : 0).Sum();
                if (room <= 0) return new($"На {where} цього вже досить — {who} вже має все, що просили");
                take = Math.Min(n, room);
            }
            if (!s.Toloka.TryGetValue(toKey, out var box)) s.Toloka[toKey] = box = [];
            if (box.Count >= TolokaMailMax) return new($"{who}: скринька толоки повна — хай спершу зайде й прийме вироби");
            box.Add(new TolokaParcel(fromNick.Trim(), fromKey, peek.Building.Key, peek.Stage, item.Ware, item.Style, item.Quality, take, now));
            Save();
            return new(null, take, peek.Building.Name, st.Name);
        }
    }

    /// <summary>Забрати всі посилки толоки (кличе Sync кімнати господаря на дії). Порожньо — null, і нічого не пишемо.</summary>
    public List<TolokaParcel>? TakeToloka(string nickKey)
    {
        lock (_lock)
        {
            var s = S();
            if (!s.Toloka.Remove(nickKey, out var box) || box.Count == 0) return null;
            Save();
            return box;
        }
    }

    /// <summary>
    /// Толока друга для його хати (кнопка «🤝 Піднести на толоку»): будова, етап, чи вже будується, хто був і скільки
    /// ще бракує за кожною вимогою — з урахуванням посилок у дорозі. Лише те, що треба помічникові. null — будови нема.
    /// </summary>
    object? TolokaView(string key, string json, DateTimeOffset now)
    {
        if (Clicker.TolokaPeekSave(json, now) is not { } peek) return null;
        int[] left;
        List<string> coming;
        lock (_lock)
        {
            var pending = PendingLocked(S(), key, peek);
            left = TolokaLeft(peek, pending);
            coming = pending.Select(p => p.From).ToList();
        }
        var st = peek.StageRow;
        return new
        {
            building = peek.Building.Key,
            stage = peek.Stage,
            endsAt = peek.EndsAt,
            helpers = peek.Helpers.Concat(coming).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            needs = st.Needs.Select((x, i) => new { ware = x.Ware, n = x.N, q = x.Q, style = x.Style, left = left[i] }).ToList(),
            max = Clicker.TolokaHelpMax,
            treat = Clicker.TolokaTreatMinutes,
        };
    }
}
