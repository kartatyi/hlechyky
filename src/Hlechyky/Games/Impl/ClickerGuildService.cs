using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Hlechyky.Games.Impl;

/// <summary>Дарунок у скриньці чи на полиці: від кого, що саме і коли.</summary>
public sealed record GuildGift(string From, string Ware, string Style, int Quality, DateTimeOffset At);

/// <summary>
/// Допомога другові в дорозі (v9 §E.2): гостинець (<c>treat</c>), підмайстер у гості (<c>lend</c>) чи похвала
/// (<c>cheer</c>). Лежить у скриньці отримувача, доки той не зайде: <see cref="Minutes"/> — скільки хвилин
/// його власного пасиву відсипати (гостинець) або скільки триватиме баф (підмайстер, похвала).
/// </summary>
public sealed record GuildBoost(string From, string Kind, int Minutes, DateTimeOffset At);

/// <summary>Що гончар може зробити для друзів сьогодні й скільки гостинців він сам уже прийняв (§E.2).</summary>
public sealed record GuildHelp(int TreatLeft, bool LendLeft, IReadOnlyList<string> Cheered);

/// <summary>Підціль воза: стільки виробів цього виду (будь-який розпис і якість).</summary>
public sealed record WagonSub(string Ware, int Need, int Have);

/// <summary>Хто скільки поклав на віз (без місць і «ти останній» — лише внесок).</summary>
public sealed record WagonGiver(string Nick, int N);

/// <summary>
/// Віз одного дня очима одного гончаря: ціль, що вже лежить, рівень (0 — ще ні, 1 бронза, 2 срібло, 3 золото),
/// скільки дав він сам і на який рівень уже забрав нагороду.
/// </summary>
public sealed record WagonInfo(
    string Day, DateTimeOffset EndsAt, int Potters, int Goal, int Total, IReadOnlyList<WagonSub> Subs,
    IReadOnlyList<WagonGiver> Givers, int Tier, int Mine, int Claimed);

/// <summary>Усе, що кімнаті треба від цеху для виду: сьогоднішній віз, вчорашній, скільки дарунків лишилось і допомога дня.</summary>
public sealed record GuildSummary(WagonInfo Today, WagonInfo Prev, int GiftsLeft, GuildHelp Help);

/// <summary>Що сталось після внеску: віз після нього і рівень, якого він щойно вперше досяг (0 — нічого нового).</summary>
public sealed record WagonGive(WagonInfo Wagon, int Reached);

/// <summary>
/// Нагорода воза: за який день, який рівень, на який уже платили раніше і скільки виробів поклав сам гончар
/// (з цього рахується його пай, §E.1). <c>Error</c> — відмова.
/// </summary>
public sealed record WagonClaim(string? Error, string Day, int Tier, int Was, int Mine = 0);

/// <summary>
/// Цех гончарів — спільне для всіх кімнат Гончарного кола: денний віз, скринька дарунків і список гончарів. Один
/// на сервер, свій замок, стан — JSON у <see cref="IGameStore"/> під ключем <see cref="StoreKey"/>, пишеться після
/// кожної зміни. Посилань на кімнати не тримає — лише дані за ніком у нижньому регістрі (як <c>Rooms.NickKey</c>).
///
/// Кімната кличе його з-під свого замка (Act/Sync), тож усередині — жодних викликів у кімнати: інакше два замки
/// в різному порядку. Час приходить параметром від гри (<c>Ctx.Clock</c>): так дві кімнати в тестах живуть кожна
/// своїм годинником, а сервіс нічого не вгадує. Власний <see cref="IClock"/> — лише для ендпоінтів.
///
/// Дух: друзів 2–4, тож нічого змагального. Ціль воза росте з кількістю гончарів учорашнього дня, нагорода однакова
/// кожному, хто поклав хоч п'ять виробів, пропущений день нічого не забирає.
/// </summary>
public sealed partial class ClickerGuildService
{
    public const string StoreKey = "clicker-guild";
    /// <summary>Скільки виробів на воза «важить» один гончар (бронза) за день. Мінімум — двоє, навіть коли грає один.</summary>
    public const int PerPotter = 12, MinPotters = 2;
    /// <summary>Хто поклав на віз хоч стільки — забирає нагороду.</summary>
    public const int MinGive = 5;
    public const int GiftsPerDay = 3, ShelfSize = 12, MailMax = 40;

    // ---------- допомога другові (v9 §E.2) ----------
    /// <summary>Гостинець буває лише такий: 10, 30 чи 60 хвилин свого пасиву.</summary>
    public static readonly int[] TreatSizes = [10, 30, 60];
    /// <summary>Друг дістає вдвічі більше хвилин — але СВОГО пасиву: багатий не ламає гру бідному.</summary>
    public const int TreatBack = 2;
    /// <summary>Стеля на отримувача: стільки хвилин гостинців за київський день від усіх разом.</summary>
    public const int TreatCapMinutes = 120;
    /// <summary>Підмайстер гостює в друга стільки годин, і ліплення там іде вдвічі швидше.</summary>
    public const int LendHours = 24;
    public const double LendWork = 0.5;
    /// <summary>Похвала: стільки хвилин +10 % до всього.</summary>
    public const int CheerMinutes = 60;
    public const double CheerMult = 1.1;
    /// <summary>Скільки днів тримати в стані: сьогоднішній, вчорашній (його нагорода ще забирається) і ще два про запас.</summary>
    public const int DaysKept = 4;
    public const int RosterMax = 100;
    /// <summary>Пороги рівнів у частках цілі: бронза 100 %, срібло 150 %, золото 200 %.</summary>
    public static readonly double[] TierShare = [0, 1, 1.5, 2];
    /// <summary>Хвилини власного пасиву за рівень.</summary>
    public static readonly int[] TierMinutes = [0, 10, 20, 35];
    public static readonly string[] TierNames = ["", "бронза", "срібло", "золото"];
    /// <summary>«Нагороду за бронзу» — знахідний відмінок.</summary>
    static readonly string[] TierNamesAcc = ["", "бронзу", "срібло", "золото"];

    /// <summary>З яких виробів складаються підцілі: найпоширеніші, відкриті майже в кожного, і їхні частки від цілі.</summary>
    static readonly (string Ware, double Share)[] SubPool =
        [("pot", 0.25), ("bowl", 0.2), ("jug", 0.15), ("makitra", 0.12), ("dish", 0.1)];

    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    readonly object _lock = new();
    readonly IGameStore _store;
    readonly IClock _clock;
    readonly ILogger? _log;
    State? _state;

    public ClickerGuildService(IGameStore store, IClock clock, ILogger<ClickerGuildService>? log = null)
    {
        _store = store;
        _clock = clock;
        _log = log;
    }

    // ---------- стан ----------

    sealed partial class State
    {
        public Dictionary<string, DayRow> Days { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, PotterRow> Potters { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<GuildGift>> Mail { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, SentRow> Sent { get; set; } = new(StringComparer.Ordinal);

        /// <summary>Пошта допомоги (§E.2): скринька за ніком отримувача — він забере її на синхронізації.</summary>
        public Dictionary<string, List<GuildBoost>> Boosts { get; set; } = new(StringComparer.Ordinal);
        /// <summary>Що гончар уже зробив для друзів сьогодні (підмайстер один, похвала — раз на друга).</summary>
        public Dictionary<string, HelpRow> Helps { get; set; } = new(StringComparer.Ordinal);
        /// <summary>Скільки хвилин гостинців отримувач уже прийняв за день — стеля спільна на всіх дарувальників.</summary>
        public Dictionary<string, TreatRow> Treats { get; set; } = new(StringComparer.Ordinal);

        /// <summary>Тижневі вози старого стану — лише щоб раз перенести останній з них у день (див. <c>Migrate</c>).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, DayRow>? Weeks { get; set; }
    }

    sealed class DayRow
    {
        public int Total { get; set; }
        public Dictionary<string, int> Wares { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, GiverRow> Givers { get; set; } = new(StringComparer.Ordinal);
        /// <summary>На який рівень кожен уже забрав нагороду.</summary>
        public Dictionary<string, int> Claimed { get; set; } = new(StringComparer.Ordinal);
        /// <summary>Найвищий рівень, про який уже сказали в Журнал: двічі одне й те саме не кажемо.</summary>
        public int Logged { get; set; }
    }

    sealed class GiverRow
    {
        public string Nick { get; set; } = "";
        public int N { get; set; }
    }

    sealed class PotterRow
    {
        public string Nick { get; set; } = "";
        public int Rank { get; set; }
        public DateTimeOffset Seen { get; set; }
        /// <summary>
        /// Клейма гончаря — для науки майстра (<see cref="TopStamps"/>). null — ще не знаємо: рядок зі старого стану,
        /// а гончар відтоді кола не відкривав; тоді одноразово читаємо з його збереження.
        /// </summary>
        public int? Stamps { get; set; }
    }

    sealed class SentRow
    {
        public string Day { get; set; } = "";
        public int N { get; set; }
    }

    sealed class HelpRow
    {
        public string Day { get; set; } = "";
        /// <summary>Підмайстер уже пішов у гості: він один, тож на день — одна позичка.</summary>
        public bool Lend { get; set; }
        /// <summary>Кого вже хвалив сьогодні (ключі ніків): кожному другові — раз на день.</summary>
        public List<string> Cheer { get; set; } = [];
    }

    sealed class TreatRow
    {
        public string Day { get; set; } = "";
        public int Minutes { get; set; }
    }

    /// <summary>Нік у ключ — так само, як <c>Rooms.NickKey</c>: без пробілів по краях, у нижньому регістрі.</summary>
    public static string Key(string? nick) => (nick ?? "").Trim().ToLowerInvariant();

    /// <summary>Стан читаємо з бази при першому зверненні, а не в конструкторі: DI будує сервіс раніше, ніж база готова.</summary>
    State S()
    {
        if (_state is not null) return _state;
        State? s = null;
        string? json;
        try { json = _store.LoadState(StoreKey); }
        catch (Exception ex)
        {
            // База не відповіла (замкнена після деплою тощо) — НЕ кешуємо порожній стан і нічого не пишемо: інакше перший
            // же внесок перезаписав би вози, скриньки й список гончарів. Цей виклик працює з тимчасовим порожнім, наступний
            // спробує прочитати знову.
            _log?.LogWarning(ex, "стан цеху гончарів не прочитався — спробуємо ще раз");
            return Normalize(new State());
        }
        try
        {
            if (json is { Length: > 0 }) s = JsonSerializer.Deserialize<State>(json, Wire);
        }
        catch (JsonException ex)
        {
            _log?.LogWarning(ex, "стан цеху гончарів зіпсований — починаємо з чистого");
        }
        return _state = Normalize(Migrate(s ?? new State(), _clock.UtcNow));
    }

    /// <summary>
    /// Вози були тижневі, стали денні — ключі стану інші, тож останній тижневий віз переносимо в сьогоднішній день:
    /// покладене руками не зникає, нагорода за нього забирається як за сьогоднішній. Раз і назавжди: після переносу
    /// <c>weeks</c> у стані більше нема (наступний запис їх не пише).
    /// </summary>
    static State Migrate(State s, DateTimeOffset now)
    {
        if (s.Weeks is { Count: > 0 } weeks && (s.Days is null || s.Days.Count == 0))
        {
            var last = weeks.Where(x => x.Value is not null).OrderBy(x => x.Key, StringComparer.Ordinal).LastOrDefault();
            if (last.Value is not null) (s.Days ??= new(StringComparer.Ordinal))[Days.Of(now)] = last.Value;
        }
        s.Weeks = null;
        return s;
    }

    static State Normalize(State s)
    {
        // Чого не було в старому стані (чи в руках, що правили базу) — порожнє, а не null.
        s.Days = Clean(s.Days);
        s.Potters = Clean(s.Potters);
        s.Mail = Clean(s.Mail);
        s.Sent = Clean(s.Sent);
        // Допомоги в старому стані не було (v7/v8) — порожні скриньки, а не null.
        s.Boosts = Clean(s.Boosts);
        s.Helps = Clean(s.Helps);
        s.Treats = Clean(s.Treats);
        foreach (var h in s.Helps.Values) h.Cheer = h.Cheer?.Where(x => x is { Length: > 0 }).ToList() ?? [];
        foreach (var w in s.Days.Values)
        {
            w.Wares = Clean(w.Wares);
            w.Givers = Clean(w.Givers);
            w.Claimed = Clean(w.Claimed);
        }
        // Звань у старому стані не було — порожні списки (ClickerGuildTitles.cs).
        NormalizeTitles(s);
        return s;
    }

    static Dictionary<string, T> Clean<T>(Dictionary<string, T>? d)
    {
        var r = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var (k, v) in d ?? []) if (k is not null && v is not null) r[k] = v;
        return r;
    }

    /// <summary>Записати стан. Кличеться під замком — порядок записів той самий, що й порядок змін.</summary>
    void Save()
    {
        if (_state is null) return;                       // стан не прочитався — тимчасовий порожній у базу не пишемо
        try { _store.SaveState(StoreKey, JsonSerializer.Serialize(_state, Wire)); }
        catch (Exception ex) { _log?.LogWarning(ex, "стан цеху гончарів не записався"); }
    }

    // ---------- день за Києвом ----------

    /// <summary>Київський день, у якому <paramref name="now"/>: «2026-09-16». Рядки порівнюються як дати.</summary>
    public static string DayOf(DateTimeOffset now) => Days.Of(now);

    /// <summary>День перед тим, у якому <paramref name="now"/>.</summary>
    public static string DayBefore(DateTimeOffset now) => Days.Of(now.AddDays(-1));

    /// <summary>Київська північ — коли сьогоднішній віз від'їжджає.</summary>
    public static DateTimeOffset DayEnds(DateTimeOffset now) => Days.NextMidnight(now);

    // ---------- ціль воза ----------

    /// <summary>
    /// Ціль воза: <see cref="PerPotter"/> виробів на гончаря ×0,85–1,15 (зерно дня), до п'ятірок; і дві-три підцілі
    /// з найпоширеніших виробів — кожна своя частка цілі ×0,8–1,2. Та сама пара (день, гончарі) — та сама ціль.
    /// </summary>
    public static (int Goal, IReadOnlyList<(string Ware, int Need)> Subs) GoalFor(string day, int potters)
    {
        potters = Math.Max(MinPotters, potters);
        var rng = new Random(Days.Seed("clicker-wagon", day));
        var goal = Fives(PerPotter * potters * (0.85 + 0.3 * rng.NextDouble()));
        var pool = SubPool.OrderBy(_ => rng.Next()).ToList();
        var count = 2 + rng.Next(2);
        var subs = pool.Take(count)
            .OrderBy(x => Array.FindIndex(SubPool, p => p.Ware == x.Ware))
            .Select(x => (x.Ware, Math.Max(5, Fives(goal * x.Share * (0.8 + 0.4 * rng.NextDouble())))))
            .ToList();
        return (goal, subs);
    }

    static int Fives(double v) => Math.Max(5, (int)Math.Round(v / 5, MidpointRounding.AwayFromZero) * 5);

    /// <summary>Рівень воза: доки хоч одна підціль не закрита — нуль; далі за часткою цілі.</summary>
    public static int TierOf(int goal, int total, IEnumerable<WagonSub> subs)
    {
        if (goal <= 0 || subs.Any(s => s.Have < s.Need)) return 0;
        for (var t = TierShare.Length - 1; t >= 1; t--)
            if (total >= (int)Math.Ceiling(goal * TierShare[t])) return t;
        return 0;
    }

    /// <summary>Скільки гончарів «важить» день: ті, хто давав хоч щось учора (не менше двох).</summary>
    static int PottersFor(State s, string before) =>
        Math.Max(MinPotters, s.Days.TryGetValue(before, out var w) ? w.Givers.Values.Count(g => g.N > 0) : 0);

    WagonInfo Info(State s, string day, string before, DateTimeOffset endsAt, string nickKey)
    {
        var potters = PottersFor(s, before);
        var (goal, subs) = GoalFor(day, potters);
        s.Days.TryGetValue(day, out var row);
        var subRows = subs.Select(x => new WagonSub(x.Ware, x.Need, row?.Wares.GetValueOrDefault(x.Ware) ?? 0)).ToList();
        var total = row?.Total ?? 0;
        var givers = (row?.Givers.Values ?? Enumerable.Empty<GiverRow>())
            .Where(g => g.N > 0)
            // За абеткою, не за внеском: у цеху не змагаються, хто більше.
            .OrderBy(g => g.Nick, StringComparer.Create(CultureInfo.GetCultureInfo("uk-UA"), true))
            .Select(g => new WagonGiver(g.Nick, g.N))
            .ToList();
        return new WagonInfo(day, endsAt, potters, goal, total, subRows, givers, TierOf(goal, total, subRows),
            row?.Givers.GetValueOrDefault(nickKey)?.N ?? 0, row?.Claimed.GetValueOrDefault(nickKey) ?? 0);
    }

    WagonInfo Current(State s, DateTimeOffset now, string nickKey) =>
        Info(s, DayOf(now), DayBefore(now), DayEnds(now), nickKey);

    WagonInfo Previous(State s, DateTimeOffset now, string nickKey)
    {
        // Учорашній віз від'їхав опівночі — тобто в «наступну північ» учорашнього дня, з поправкою на переведення часу.
        var yesterday = now.AddDays(-1);
        return Info(s, DayOf(yesterday), DayBefore(yesterday), Days.NextMidnight(yesterday), nickKey);
    }

    /// <summary>Сьогоднішній віз, учорашній, скільки дарунків ще можна сьогодні й допомога дня — для виду кімнати.</summary>
    public GuildSummary Summary(string nickKey, DateTimeOffset now)
    {
        lock (_lock)
        {
            var s = S();
            return new GuildSummary(Current(s, now, nickKey), Previous(s, now, nickKey),
                GiftsLeftLocked(s, nickKey, now), HelpLocked(s, nickKey, now));
        }
    }

    /// <summary>Покласти вироби на сьогоднішній віз. Вироби вже забрала з комори кімната — тут лише облік.</summary>
    public WagonGive Give(string nickKey, string nick, string ware, int n, DateTimeOffset now)
    {
        lock (_lock)
        {
            var s = S();
            var day = DayOf(now);
            if (n <= 0) return new WagonGive(Current(s, now, nickKey), 0);
            if (!s.Days.TryGetValue(day, out var row)) s.Days[day] = row = new DayRow();
            row.Total += n;
            row.Wares[ware] = row.Wares.GetValueOrDefault(ware) + n;
            if (!row.Givers.TryGetValue(nickKey, out var giver)) row.Givers[nickKey] = giver = new GiverRow();
            giver.Nick = nick;
            giver.N += n;
            var info = Current(s, now, nickKey);
            var reached = info.Tier > row.Logged ? info.Tier : 0;
            row.Logged = Math.Max(row.Logged, info.Tier);
            Prune(s, now);
            Save();
            return new WagonGive(info, reached);
        }
    }

    /// <summary>
    /// Забрати нагороду воза: сьогоднішнього або вчорашнього (до кінця сьогоднішнього дня). Хто поклав хоч
    /// <see cref="MinGive"/>, забирає рівень, якого віз досяг; віз потім доріс — можна добрати різницю. Без
    /// <paramref name="day"/> — спершу вчорашній.
    /// </summary>
    public WagonClaim Claim(string nickKey, string? day, DateTimeOffset now)
    {
        lock (_lock)
        {
            var s = S();
            var cur = Current(s, now, nickKey);
            var prev = Previous(s, now, nickKey);
            WagonInfo w;
            if (string.IsNullOrEmpty(day)) w = Claimable(prev) ? prev : cur;
            else if (day == cur.Day) w = cur;
            else if (day == prev.Day) w = prev;
            else return new WagonClaim("Той віз уже давно поїхав — нагороди за нього не забрати", day, 0, 0);

            if (w.Mine < MinGive)
                return new WagonClaim($"Нагорода — тим, хто поклав на віз хоч {MinGive} виробів (у тебе {w.Mine})", w.Day, 0, 0);
            if (w.Tier == 0) return new WagonClaim("Віз ще не наповнився навіть до бронзи — докладаймо разом", w.Day, 0, 0);
            if (w.Claimed >= w.Tier) return new WagonClaim($"Нагороду за {TierNamesAcc[w.Tier]} ти вже забрав", w.Day, 0, 0);

            if (!s.Days.TryGetValue(w.Day, out var row)) return new WagonClaim("Такого воза нема", w.Day, 0, 0);
            row.Claimed[nickKey] = w.Tier;
            Save();
            return new WagonClaim(null, w.Day, w.Tier, w.Claimed, w.Mine);
        }
    }

    static bool Claimable(WagonInfo w) => w.Mine >= MinGive && w.Tier > w.Claimed;

    /// <summary>Старі дні — геть: нагорода за них однаково вже не забирається.</summary>
    static void Prune(State s, DateTimeOffset now)
    {
        var oldest = Days.Of(now.AddDays(-(DaysKept - 1)));
        foreach (var key in s.Days.Keys.Where(k => string.CompareOrdinal(k, oldest) < 0).ToList()) s.Days.Remove(key);
        // Денні рядки допомоги живуть рівно день: учорашні однаково нічого не тримають.
        var today = Days.Of(now);
        foreach (var key in s.Helps.Where(x => x.Value.Day != today).Select(x => x.Key).ToList()) s.Helps.Remove(key);
        foreach (var key in s.Treats.Where(x => x.Value.Day != today).Select(x => x.Key).ToList()) s.Treats.Remove(key);
    }

    // ---------- дарунки ----------

    int GiftsLeftLocked(State s, string nickKey, DateTimeOffset now) =>
        s.Sent.TryGetValue(nickKey, out var r) && r.Day == Days.Of(now) ? Math.Max(0, GiftsPerDay - r.N) : GiftsPerDay;

    public int GiftsLeft(string nickKey, DateTimeOffset now)
    {
        lock (_lock) return GiftsLeftLocked(S(), nickKey, now);
    }

    /// <summary>
    /// Відправити дарунок у скриньку друга. null — відправлено; інакше — чому ні. Виріб з комори забирає кімната
    /// лише після «так». Отримувач мусить мати збереження кола — дарувати в нікуди не даємо.
    /// </summary>
    public string? Gift(string fromKey, string fromNick, string toNick, ItemInfo item, DateTimeOffset now)
    {
        var toKey = Key(toNick);
        if (toKey.Length == 0) return "Кому дарувати? Обери гончаря";
        if (toKey == fromKey) return "Собі дарувати — то вже не дарунок 🙂";
        if (!SatDownAtWheel(toKey)) return $"{toNick.Trim()} ще не сідав за гончарне коло — дарунку нікуди стати";
        lock (_lock)
        {
            var s = S();
            var today = Days.Of(now);
            if (!s.Sent.TryGetValue(fromKey, out var sent) || sent.Day != today) s.Sent[fromKey] = sent = new SentRow { Day = today };
            if (sent.N >= GiftsPerDay) return $"Сьогодні вже {GiftsPerDay} дарунки — щедрість повернеться завтра";
            if (!s.Mail.TryGetValue(toKey, out var box)) s.Mail[toKey] = box = [];
            box.Add(new GuildGift(fromNick.Trim(), item.Ware, item.Style, item.Quality, now));
            if (box.Count > MailMax) box.RemoveRange(0, box.Count - MailMax);
            sent.N++;
            Save();
            return null;
        }
    }

    /// <summary>Забрати все зі скриньки (кличе Sync кімнати отримувача). Порожньо — null, і нічого не пишемо.</summary>
    public List<GuildGift>? TakeMail(string nickKey)
    {
        lock (_lock)
        {
            var s = S();
            if (!s.Mail.Remove(nickKey, out var box) || box.Count == 0) return null;
            Save();
            return box;
        }
    }

    /// <summary>Чи сідав гончар за коло: дарувати й помагати в нікуди не даємо. База — поза замком, читати її довго.</summary>
    bool SatDownAtWheel(string key)
    {
        try { return !string.IsNullOrEmpty(_store.LoadState("clicker:" + key)); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "не вдалось глянути, чи є збереження в {Nick}", key);
            return false;
        }
    }

    // ---------- допомога другові (v9 §E.2) ----------

    static HelpRow HelpRowFor(State s, string key, string day)
    {
        if (!s.Helps.TryGetValue(key, out var row) || row.Day != day) s.Helps[key] = row = new HelpRow { Day = day };
        return row;
    }

    static TreatRow TreatRowFor(State s, string key, string day)
    {
        if (!s.Treats.TryGetValue(key, out var row) || row.Day != day) s.Treats[key] = row = new TreatRow { Day = day };
        return row;
    }

    GuildHelp HelpLocked(State s, string nickKey, DateTimeOffset now)
    {
        var day = Days.Of(now);
        var mine = s.Helps.TryGetValue(nickKey, out var h) && h.Day == day ? h : null;
        var got = s.Treats.TryGetValue(nickKey, out var t) && t.Day == day ? t.Minutes : 0;
        return new GuildHelp(Math.Max(0, TreatCapMinutes - got), mine is null || !mine.Lend, mine?.Cheer.ToList() ?? []);
    }

    /// <summary>Що гончар ще може зробити для друзів сьогодні (і скільки гостинців прийняв сам).</summary>
    public GuildHelp Help(string nickKey, DateTimeOffset now)
    {
        lock (_lock) return HelpLocked(S(), nickKey, now);
    }

    /// <summary>
    /// Послати другові допомогу: <c>treat</c> (гостинець на 10/30/60 хв), <c>lend</c> (підмайстер у гості на добу)
    /// чи <c>cheer</c> (похвала на годину). null — пішло; інакше — чому ні. Глеки за гостинець кімната списує
    /// лише після «так». Межі дня рахуються тут: гостинцю — стеля на ОТРИМУВАЧА, підмайстрові й похвалі —
    /// на дарувальника.
    /// </summary>
    public string? Boost(string fromKey, string fromNick, string toNick, string kind, int minutes, DateTimeOffset now)
    {
        var toKey = Key(toNick);
        var who = toNick.Trim();
        if (toKey.Length == 0) return "Кому помагати? Обери гончаря";
        if (toKey == fromKey) return "Самому собі помагати — то просто робота 🙂";
        if (!SatDownAtWheel(toKey)) return $"{who} ще не сідав за гончарне коло — помагати нікому";
        lock (_lock)
        {
            var s = S();
            var day = Days.Of(now);
            var help = HelpRowFor(s, fromKey, day);
            int carry;
            switch (kind)
            {
                case "treat":
                    if (Array.IndexOf(TreatSizes, minutes) < 0) return "Гостинець буває на 10, 30 або 60 хвилин";
                    carry = minutes * TreatBack;
                    var treat = TreatRowFor(s, toKey, day);
                    var left = TreatCapMinutes - treat.Minutes;
                    if (left <= 0) return $"{who} сьогодні вже наївся гостинців — завтра зголодніє знову";
                    if (carry > left) return $"{who} сьогодні прийме ще {left} хв гостинців — пришли менший";
                    treat.Minutes += carry;
                    break;
                case "lend":
                    if (help.Lend) return "Підмайстер у цеху один, і сьогодні він уже пішов у гості";
                    help.Lend = true;
                    carry = LendHours * 60;
                    break;
                case "cheer":
                    if (help.Cheer.Contains(toKey, StringComparer.Ordinal))
                        return $"{who} сьогодні вже чув(ла) від тебе добре слово — завтра скажеш ще";
                    help.Cheer.Add(toKey);
                    carry = CheerMinutes;
                    break;
                default:
                    return "Такої допомоги в цеху не знають";
            }
            if (!s.Boosts.TryGetValue(toKey, out var box)) s.Boosts[toKey] = box = [];
            box.Add(new GuildBoost(fromNick.Trim(), kind, carry, now));
            if (box.Count > MailMax) box.RemoveRange(0, box.Count - MailMax);
            Save();
            return null;
        }
    }

    /// <summary>Забрати всю допомогу зі скриньки (кличе Sync кімнати отримувача). Порожньо — null, і нічого не пишемо.</summary>
    public List<GuildBoost>? TakeBoosts(string nickKey)
    {
        lock (_lock)
        {
            var s = S();
            if (!s.Boosts.Remove(nickKey, out var box) || box.Count == 0) return null;
            Save();
            return box;
        }
    }

    // ---------- гончарі ----------

    /// <summary>
    /// Гончар відкрив коло, дістав новий ранг чи обпалив майстерню: у список цеху. Пишемо, лише коли щось змінилось
    /// чи настав новий день. <paramref name="stamps"/> — його клейма (null — не чіпати те, що вже знаємо).
    /// </summary>
    public void Hello(string nickKey, string nick, int rank, DateTimeOffset now, int? stamps = null)
    {
        if (nickKey.Length == 0) return;
        lock (_lock)
        {
            var s = S();
            s.Potters.TryGetValue(nickKey, out var p);
            var known = stamps is { } n ? Math.Max(0, n) : p?.Stamps;
            if (p is not null && p.Nick == nick && p.Rank == rank && p.Stamps == known && Days.Of(p.Seen) == Days.Of(now)) return;
            s.Potters[nickKey] = new PotterRow { Nick = nick, Rank = rank, Seen = now, Stamps = known };
            if (s.Potters.Count > RosterMax)
                foreach (var old in s.Potters.OrderBy(x => x.Value.Seen).Take(s.Potters.Count - RosterMax).Select(x => x.Key).ToList())
                    s.Potters.Remove(old);
            Save();
        }
    }

    /// <summary>Коли знову пробувати дочитати клейма зі збережень, якщо минулого разу база не відповіла.</summary>
    DateTimeOffset _stampsRetryAt;

    /// <summary>
    /// Найкращий гончар округи, крім <paramref name="exceptKey"/>: нік і клейма — від них рахується наука майстра.
    /// Кого список цеху ще не знає з клеймами (стан із часів до науки, а гончар відтоді кола не відкривав), читаємо
    /// з його збереження один раз; далі число живе в списку й оновлюється його ж обпалами. Нікого — <c>("", 0)</c>.
    /// Кличеться з-під замка кімнати (обпал, вид), тож базу чіпаємо лише для невідомих і не частіше, ніж раз на хвилину.
    /// </summary>
    public (string Nick, int Stamps) TopStamps(string exceptKey)
    {
        List<string> unknown;
        lock (_lock)
            unknown = _clock.UtcNow < _stampsRetryAt ? [] : S().Potters.Where(x => x.Value.Stamps is null).Select(x => x.Key).ToList();
        if (unknown.Count > 0)
        {
            var read = new Dictionary<string, int>(StringComparer.Ordinal);
            var failed = false;
            foreach (var key in unknown)
            {
                try { read[key] = StampsOf(_store.LoadState("clicker:" + key)); }
                catch (Exception ex)
                {
                    _log?.LogWarning(ex, "клейма гончаря {Nick} не прочитались", key);
                    failed = true;
                }
            }
            lock (_lock)
            {
                var s = S();
                var changed = false;
                foreach (var (key, n) in read)
                    if (s.Potters.TryGetValue(key, out var p) && p.Stamps is null) { p.Stamps = n; changed = true; }
                if (changed) Save();
                if (failed) _stampsRetryAt = _clock.UtcNow.AddMinutes(1);
            }
        }
        lock (_lock)
        {
            var best = S().Potters
                .Where(x => x.Key != exceptKey && x.Value.Stamps > 0)
                .OrderByDescending(x => x.Value.Stamps)
                .ThenBy(x => x.Key, StringComparer.Ordinal)
                .FirstOrDefault();
            return best.Value is { } row ? (row.Nick, row.Stamps ?? 0) : ("", 0);
        }
    }

    /// <summary>Клейма зі збереження кола: поле <c>stamps</c>, або 0, коли збереження нема чи воно зіпсоване.</summary>
    static int StampsOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        try { return JsonNode.Parse(json) is JsonObject o ? Math.Max(0, Int(o["stamps"])) : 0; }
        catch (JsonException) { return 0; }
    }

    /// <summary>
    /// <c>GET /api/games/clicker/guild</c>: гончарі цеху (за абеткою) зі значками звань, сьогоднішній віз і дошка звань
    /// округи (хто що тримає, хто веде сьогодні, хто перший вибив таємне).
    /// </summary>
    public object Roster(string? meNick)
    {
        var now = _clock.UtcNow;
        var me = Key(meNick);
        EnsureTitleStats();
        lock (_lock)
        {
            var s = S();
            var today = Current(s, now, me);
            s.Days.TryGetValue(today.Day, out var row);
            return new
            {
                day = WagonView(today),
                potters = s.Potters
                    .OrderBy(x => x.Value.Nick, StringComparer.Create(CultureInfo.GetCultureInfo("uk-UA"), true))
                    .Select(x => new
                    {
                        nick = x.Value.Nick, rank = x.Value.Rank, seenAt = x.Value.Seen,
                        gave = row?.Givers.GetValueOrDefault(x.Key)?.N ?? 0, me = x.Key == me,
                        // Звання (docs/games/specs/clicker-titles.md): до трьох значків і «перший гончар округи» — золотом.
                        badges = BadgesOf(s, x.Key, now),
                        first = s.TitleHolds.TryGetValue(Clicker.TitleFirst, out var f) && f.Key == x.Key,
                    })
                    .ToList(),
                titles = TitleBoard(s, now),
            };
        }
    }

    /// <summary>Віз у форму для дроту — однакова в розі кімнати й у списку цеху.</summary>
    public static object WagonView(WagonInfo w) => new
    {
        id = w.Day, endsAt = w.EndsAt, potters = w.Potters, goal = w.Goal, total = w.Total,
        subs = w.Subs.Select(x => new { ware = x.Ware, need = x.Need, have = x.Have }),
        givers = w.Givers.Select(g => new { nick = g.Nick, n = g.N }),
        tier = w.Tier, mine = w.Mine, claimed = w.Claimed,
        pct = w.Goal > 0 ? Math.Round(100.0 * w.Total / w.Goal, 1) : 0,
    };

    // ---------- хата друга ----------

    /// <summary><c>GET /api/games/clicker/house?nick=</c>: публічний знімок зі збереження друга або null.</summary>
    public object? House(string? nick)
    {
        var key = Key(nick);
        if (key.Length == 0) return null;
        string? json;
        try { json = _store.LoadState("clicker:" + key); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "хата {Nick} не прочиталась", key);
            return null;
        }
        string display;
        object[] wall;
        // Стіна звань: зароблене назавжди — зі збереження, «перші в окрузі» й звання дня — з цеху.
        var earned = Clicker.TitleStatsFromSave(json).Earned;
        lock (_lock)
        {
            var s = S();
            display = s.Potters.TryGetValue(key, out var p) ? p.Nick : (nick ?? "").Trim();
            wall = WallOf(s, key, _clock.UtcNow, earned);
        }
        return HouseSnapshot(display, json, wall);
    }

    /// <summary>
    /// Публічне зі збереження кола — і ніщо інше. Чиста функція: береться лише перелічене (рівні драбини, прикраси,
    /// знаряддя, розписи, альбом і кахлі — якщо такі поля є, ранг, полиця дарунків, вироби, найкращі з комори, глеки
    /// за весь час). Око майстра, глеки в кишені, купці, скринька — не йдуть. Зіпсований JSON — null.
    /// </summary>
    public static object? HouseSnapshot(string nick, string? json, object[]? titles = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        JsonObject root;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return null;
            root = o;
        }
        catch (JsonException) { return null; }

        var upgrades = root["upgrades"] as JsonObject;
        var house = root["house"] as JsonObject;
        var craft = root["craft"] as JsonObject;
        var guild = root["guild"] as JsonObject;
        var album = root["album"] as JsonObject;
        var kiln = root["kiln"] as JsonObject;

        var ladder = Clicker.Shop
            .Select(u => new { key = u.Key, name = u.Name, level = Math.Max(0, Int(upgrades?[u.Key])) })
            .Where(x => x.level > 0)
            .ToList();

        var fired = 0L;
        if (craft?["firedBy"] is JsonObject firedBy)
            foreach (var (ware, n) in firedBy)
                if (Clicker.WareOf(ware) is not null) fired += Math.Max(0, Long(n));

        var best = new List<(ItemInfo Item, int N)>();
        if (craft?["items"] is JsonObject items)
            foreach (var (key, n) in items)
                if (Clicker.ParseItem(key) is { } it && Int(n) > 0) best.Add((it, Int(n)));

        var gifts = new List<object>();
        if (guild?["shelf"] is JsonArray shelf)
            foreach (var g in shelf.OfType<JsonObject>())
            {
                var ware = Str(g["ware"]);
                var style = Str(g["style"]);
                var q = Int(g["quality"]);
                if (Clicker.WareOf(ware) is null || q is < 1 or > Clicker.QualityMax || (style.Length > 0 && Clicker.Styles.All(x => x.Key != style))) continue;
                gifts.Add(new { from = Str(g["from"]), ware, style, q, at = Str(g["at"]) });
                if (gifts.Count >= ShelfSize) break;
            }

        return new
        {
            nick,
            total = Math.Max(0, Pots(root["total"])),
            stamps = Math.Max(0, Int(root["stamps"])),
            firings = Math.Max(0, Int(root["firings"])),
            ladder,
            decor = Known(house?["decor"], k => Clicker.Decor.Any(d => d.Key == k)),
            tools = Known(house?["tools"], k => Clicker.Tools.Any(t => t.Key == k)),
            styles = Known(root["styles"], k => Clicker.Styles.Any(s => s.Key == k)),
            wear = Clicker.Styles.Any(s => s.Key == Str(root["wear"])) ? Str(root["wear"]) : "",
            // Альбом у збереженні — «виріб → список розписів»: клітинок стільки, скільки розписів у всіх списках.
            album = album?["cells"] is JsonObject cells ? cells.Sum(kv => kv.Value is JsonArray a ? a.Count : 0) : (int?)null,
            // Скільки клітинок в альбомі всього — щоб хата друга показала «89 %», не знаючи правил альбому.
            albumSize = Clicker.AlbumSize,
            // Зірки (Q≥3 у клітинці) пише пакет «Альбом»; старе збереження їх не має — тоді null.
            stars = album?["stars"] is JsonObject stars ? stars.Sum(kv => kv.Value is JsonArray a ? a.Count : 0) : (int?)null,
            // «Виставка» (§C.4): до трьох клітинок, які гончар поставив на видноту.
            show = Show(album?["show"]),
            tiles = CountOf(album?["stove"]),
            // Стан горна — щоб було видно, чи щось зараз пече друг (час судить клієнт: сервер тут без годинника).
            kiln = kiln is null ? null : new
            {
                batch = kiln["batch"] is JsonArray b ? b.Count(x => Clicker.WareOf(Str(x)) is not null) : 0,
                style = Clicker.Styles.Any(x => x.Key == Str(kiln["style"])) ? Str(kiln["style"]) : "",
                beauty = Math.Clamp(Int(kiln["beauty"]), 0, 100),
                litAt = Str(kiln["litAt"]),
                coolUntil = Str(kiln["coolUntil"]),
                batches = Math.Max(0, Int(kiln["batches"])),
            },
            // Дивовижі (§F.4) — скільки знайдено; поля ще може не бути (старе збереження чи гілка без «Хати»).
            wonders = CountOf(house?["wonders"]) ?? (house?["wonders"] is JsonValue ? Math.Max(0, Int(house["wonders"])) : (int?)null),
            // Ключі знайдених дивовиж — сцена малює їх у хаті друга (раніше вона чекала список і падала на числі).
            wonderKeys = house?["wonders"] is JsonObject found ? found.Select(kv => kv.Key).Where(k => Clicker.Wonders.Any(w => w.Key == k)).ToList() : [],
            // Ім'я хати (§F.3) — на вивісці замість «Хата гончаря».
            houseName = Cut(Str(house?["name"]), 24),
            rank = Math.Clamp(Int(guild?["rank"]), 0, Clicker.GuildRanks.Length - 1),
            gifts,
            formed = Math.Max(0, Long(craft?["formed"])),
            fired,
            best = best
                .OrderByDescending(x => x.Item.Quality)
                .ThenByDescending(x => Clicker.StyleValue(x.Item.Style))
                .ThenByDescending(x => Array.FindIndex(Clicker.Wares, w => w.Key == x.Item.Ware))
                .Take(3)
                .Select(x => new { ware = x.Item.Ware, style = x.Item.Style, q = x.Item.Quality, n = x.N })
                .ToList(),
            // Стіна звань (clicker-titles.md): що гончар має зараз, найрідкісніші спершу; і пам'ятний глечик «Округа».
            titles = titles ?? [],
            keepsake = Clicker.KeepsakeIn(root),
        };
    }

    static List<string> Known(JsonNode? node, Func<string, bool> known) =>
        node is JsonArray a ? a.Select(Str).Where(k => k.Length > 0 && known(k)).Distinct(StringComparer.Ordinal).ToList() : [];

    static int? CountOf(JsonNode? node) => node switch
    {
        JsonArray a => a.Count,
        JsonObject o => o.Count,
        _ => null,
    };

    /// <summary>
    /// «Виставка» альбому (§C.4) — до трьох клітинок на видноті. Формат ключа пише пакет «Альбом»: беремо і
    /// повний ключ виробу (<c>ware|style|q</c>), і коротку пару <c>ware|style</c>, щоб знімок не залежав від
    /// того, котрий із них там опиниться.
    /// </summary>
    static List<object> Show(JsonNode? node)
    {
        var list = new List<object>();
        if (node is not JsonArray a) return list;
        foreach (var raw in a)
        {
            var key = Str(raw);
            if (key.Length == 0) continue;
            // Розбираємо самі, а не через ParseItem: той зараз не пускає розкішних (Q4), а виставка їх якраз і ждатиме.
            var parts = key.Split('|');
            var ware = parts[0];
            var style = parts.Length > 1 ? parts[1] : "";
            var q = parts.Length > 2 && int.TryParse(parts[2], out var n) ? n : 1;
            if (Clicker.WareOf(ware) is null) continue;
            if (style.Length > 0 && Clicker.Styles.All(x => x.Key != style)) continue;
            list.Add(new { ware, style, q = Math.Clamp(q, 1, 4) });
            if (list.Count >= 3) break;
        }
        return list;
    }

    static string Cut(string s, int max) => s.Length <= max ? s : s[..max];

    static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    static long Long(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l : (long)Math.Clamp(Pots(n), -9.2e18, 9.2e18);

    /// <summary>Глеки з чужого збереження: з дев'ятого оновлення вони double і бувають більші за long.</summary>
    static double Pots(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var x) && double.IsFinite(x) ? x : 0;

    static int Int(JsonNode? n) => (int)Math.Clamp(Long(n), int.MinValue, int.MaxValue);
}

/// <summary>
/// Підключення цеху: один рядок у <c>GamesSetup.AddHlechykyGames</c> і один у <c>MapHlechykyGames</c>.
/// </summary>
public static class ClickerGuildSetup
{
    public static IServiceCollection AddClickerGuild(this IServiceCollection services)
    {
        services.AddSingleton(sp => new ClickerGuildService(
            sp.GetRequiredService<IGameStore>(), sp.GetRequiredService<IClock>(), sp.GetService<ILogger<ClickerGuildService>>()));
        return services;
    }

    public static WebApplication MapClickerGuild(this WebApplication app)
    {
        // Список гончарів і віз — будь-кому з сайту: це те саме, що й так видно в Журналі.
        app.MapGet("/api/games/clicker/guild", (HttpContext c, ClickerGuildService guild) => Results.Ok(guild.Roster(Auth.Nick(c))));

        // Хата друга: лише публічне зі збереження (див. HouseSnapshot).
        app.MapGet("/api/games/clicker/house", (string? nick, ClickerGuildService guild) =>
            guild.House(nick) is { } house
                ? Results.Ok(house)
                : Results.NotFound(new { ok = false, message = "Такої хати нема — гончар ще не сідав за коло" }));
        return app;
    }
}
