using System.Globalization;
using System.Text.Json;
using Hlechyky.Games.Economy;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>Налаштування тижневих змагань кола (<c>Clicker:Week</c>, наживо): «Хто більше виріс» і «Спільне замовлення округи».</summary>
public sealed class ClickerWeekOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>«⛵ Хто більше виріс» (v13 Г4). Вимикач.</summary>
    public bool GrowEnabled { get; set; } = true;
    /// <summary>Черепки за 1-ше, 2-ге, 3-тє місце (база, × Economy:Boost).</summary>
    public int[]? GrowPrizes { get; set; }
    /// <summary>«Вітрила тижня»: відсотки до всього за 1-ше, 2-ге, 3-тє місце.</summary>
    public double[]? SailsPct { get; set; }
    public int GrowMinPlayers { get; set; } = 2;
    /// <summary>Скільки днів тижня мусить грати гончар (по <see cref="GrowMinMinutes"/> хвилин біля кола).</summary>
    public int GrowMinDays { get; set; } = 3;
    public int GrowMinMinutes { get; set; } = 5;
    /// <summary>Нижче цього total у змаганні не беруть участі (сотні глеків росли б «в рази» на порожньому місці).</summary>
    public double GrowMinTotal { get; set; } = 1e30;

    /// <summary>«🤝 Спільне замовлення округи» (v13 Г5). Вимикач.</summary>
    public bool CoopEnabled { get; set; } = true;
    /// <summary>Частка від усіх виробів, обпалених людьми минулого тижня.</summary>
    public double CoopShare { get; set; } = 0.15;
    public int CoopMin { get; set; } = 500;
    public int CoopMax { get; set; } = 5000;
    public int CoopQuality { get; set; } = 3;
    /// <summary>Скільки від обсягу мусить довезти гончар, щоб дістати бонус.</summary>
    public double CoopMinShare { get; set; } = 0.05;
    /// <summary>Стеля внеску одного гончара — частка обсягу (рецензія v13: один сильний гравець не закриває замовлення сам).</summary>
    public double CoopMaxShare { get; set; } = 0.25;
    public double CoopBonusPct { get; set; } = 10;

    /// <summary>Скільки діб діють нагороди тижня від підбиття.</summary>
    public int RewardDays { get; set; } = 7;

    public static readonly int[] DefaultGrowPrizes = [300, 150, 75];
    public static readonly double[] DefaultSails = [20, 10, 5];
    public int[] GrowPrizeList => GrowPrizes is { Length: > 0 } p ? p : DefaultGrowPrizes;
    public double[] SailsList => SailsPct is { Length: > 0 } p ? p : DefaultSails;
}

/// <summary>Нагорода тижня до кола, що чекає гончаря в сервісі: <c>sails</c> («Вітрила», % до всього) чи <c>coop</c> (+10 %). Ключ — <c>week:{тиждень}:grow:{місце}</c> / <c>week:{тиждень}:coop</c>.</summary>
public sealed record ClickerWeekPrize(string Key, string Kind, double Pct, DateTimeOffset Until, string Why);

/// <summary>Рядок табло росту: очки — у скільки разів виріс total за тиждень.</summary>
public sealed record ClickerGrowRow(int Place, string Nick, double X, int Days, bool Ok);

/// <summary>Спільне замовлення тижня: що, скільки просять, скільки довезли, до коли, чи виконане.</summary>
public sealed record ClickerCoopOrder(string Week, string Ware, int Quality, int Volume, int Done, DateTimeOffset Ends, bool Fulfilled);

/// <summary>
/// Тижневі змагання кола (docs/games/specs/clicker-v13.md, Г4 і Г5). Тиждень — з понеділка до неділі 23:30 за Києвом (та сама
/// межа, що в «Майстра дня», <see cref="ClickerDayService.Close"/>); ключ тижня — дата тієї неділі. Сервіс не веде свого
/// рахунку росту — рахує його зі щоденника кола (<see cref="ClickerDiaryService"/>), а свій стан тримає лише для замовлення.
///
/// <b>Г4 «⛵ Хто більше виріс».</b> Очки — total наприкінці тижня ÷ total на початку (початок — <c>dayStart</c> першого рядка
/// тижня, інакше останній total до тижня). Умова: щонайменше <see cref="ClickerWeekOptions.GrowMinDays"/> дні з
/// <see cref="ClickerWeekOptions.GrowMinMinutes"/> хв гри (<c>clicker_hours</c>) і total від 1e30. Призи: 300/150/75 × Boost 🏺,
/// «Вітрила тижня» +20/10/5 % до всього на 7 діб від підбиття (вимпел на хаті) і рядок у Балачки.
///
/// <b>Г5 «🤝 Спільне замовлення округи».</b> Щотижня одне замовлення: виріб (відкритий у більшості активних гончарів) якості
/// від 3, обсяг — 15 % усіх виробів, обпалених людьми минулого тижня (не менше 500, не більше 5000). Кожен везе зі своєї
/// комори дією <c>guild { op: "coop", n }</c> (<see cref="CoopAdd"/>); виконали до кінця тижня — кому довіз від 5 % обсягу,
/// +10 % до всього на 7 діб.
///
/// Нагороди до кола сервіс лише записує (таблиця <c>clicker_week_awards</c>); гончар забирає сам на синхронізації
/// (<see cref="PrizesFor"/>), ключ у збереженні гри — повтор не платить. Пам'ять — істина; база — запис фоном.
/// </summary>
public sealed class ClickerWeekService : BackgroundService
{
    public const string Schema = """
        CREATE TABLE IF NOT EXISTS clicker_week_seen(week TEXT PRIMARY KEY);
        CREATE TABLE IF NOT EXISTS clicker_week_paid(week TEXT PRIMARY KEY, at TEXT NOT NULL, summary TEXT NOT NULL, winner TEXT);
        CREATE TABLE IF NOT EXISTS clicker_week_awards(
            key TEXT PRIMARY KEY, week TEXT NOT NULL, nick_key TEXT NOT NULL, nick TEXT NOT NULL, kind TEXT NOT NULL,
            pct REAL NOT NULL, until TEXT NOT NULL, why TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS clicker_coop(
            week TEXT PRIMARY KEY, ware TEXT NOT NULL, quality INTEGER NOT NULL, volume INTEGER NOT NULL, made TEXT NOT NULL,
            done TEXT, settled INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS clicker_coop_part(
            week TEXT NOT NULL, nick_key TEXT NOT NULL, nick TEXT NOT NULL, n INTEGER NOT NULL,
            PRIMARY KEY(week, nick_key));
        """;

    public static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(5), SettleEvery = TimeSpan.FromSeconds(30);
    /// <summary>Скільки днів сервіс пам'ятає непрочитані нагороди (гончар, що не заходив довше, їх губить).</summary>
    public const int AwardKeepDays = 21;
    /// <summary>Скільки рядків живого табло росту їде у вид.</summary>
    public const int BoardShown = 5;

    sealed class Coop
    {
        public string Week = "", Ware = "";
        public int Quality, Volume;
        public DateTimeOffset Made, Done;
        public bool Settled, Dirty;
        public readonly Dictionary<string, (string Nick, int N)> Parts = new(StringComparer.Ordinal);
        public int Total => Parts.Values.Sum(p => p.N);
    }

    sealed record Award(string Key, string Week, string NickKey, string Nick, string Kind, double Pct, DateTimeOffset Until, string Why);

    /// <summary>Гончар за тиждень зі щоденника: початок, кінець, дні гри, скільки виробів обпалив і які вироби відкриті.</summary>
    sealed record Potter(string Key, string Nick, double Start, double End, int Days, long FiredStart, long FiredEnd, HashSet<string> Wares);

    readonly Db? _db;
    readonly IStakes? _stakes;
    readonly Func<ClickerWeekOptions> _opts;
    readonly IClock _clock;
    readonly ISlotsWire? _wire;
    readonly ILogger<ClickerWeekService>? _log;
    readonly Func<string, bool>? _person;
    readonly object _gate = new();
    readonly Dictionary<string, Coop> _coops = new(StringComparer.Ordinal);
    readonly Dictionary<string, Award> _awards = new(StringComparer.Ordinal);
    readonly HashSet<string> _seen = new(StringComparer.Ordinal), _paid = new(StringComparer.Ordinal);
    (string Week, string Summary)? _last;
    (string Week, DateTimeOffset At, List<ClickerGrowRow> Rows)? _board;

    public ClickerWeekService(Db? db, IStakes? stakes, Func<ClickerWeekOptions> options, IClock? clock = null, ISlotsWire? wire = null,
        ILogger<ClickerWeekService>? log = null, Func<string, bool>? person = null)
    {
        _db = db;
        _stakes = stakes;
        _opts = options;
        _clock = clock ?? new SystemClock();
        _wire = wire;
        _log = log;
        _person = person;
        try { Load(); }
        catch (Exception ex) { _log?.LogWarning(ex, "тиждень кола: не прочитав стан — починаю з чистого"); }
        Touch(_clock.UtcNow);
    }

    public ClickerWeekOptions O => _opts();

    /// <summary>Щоденник кола — звідки береться ріст і дні гри. null — змагань нема.</summary>
    public ClickerDiaryService? Diary { get; init; }
    /// <summary>Множник заробітку (Economy:Boost): призи — заробіток грою, тож × Boost.</summary>
    public Func<int, int> Earn { get; init; } = n => n;
    /// <summary>Жереб виробу замовлення (у тестах — з зерном).</summary>
    public Random Rng { get; init; } = Random.Shared;
    /// <summary>Як довго живе знімок табло, перш ніж перерахувати (щоденник — це запити до бази).</summary>
    public TimeSpan BoardTtl { get; init; } = TimeSpan.FromSeconds(90);

    // ---------- тиждень ----------

    /// <summary>Ключ тижня: дата неділі, о 23:30 якої він закінчується. Після неділі 23:30 — уже наступний.</summary>
    public static string WeekOf(DateTimeOffset now)
    {
        var day = DateOnly.ParseExact(ClickerDayService.DayOf(now), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return day.AddDays((7 - (int)day.DayOfWeek) % 7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>Коли тиждень закінчується (UTC): неділя 23:30 за Києвом — літній і зимовий час рахує зона.</summary>
    public static DateTimeOffset Ends(string week) => ClickerDayService.Ends(week);

    static DateOnly DateOf(string day) => DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    static string Fmt(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Понеділок тижня (київська календарна дата): з нього щоденник кола рахує дні тижня.</summary>
    public static string Monday(string week) => Fmt(DateOf(week).AddDays(-6));

    public static string Before(string week, int weeks = 1) => Fmt(DateOf(week).AddDays(-7 * weeks));

    // ---------- люди ----------

    bool IsPerson(string nick) => _person is not null ? _person(nick) : Diary?.IsPerson(nick) ?? (!Bots.Looks(nick) && !Auth.IsGuestNick(nick));

    // ---------- щоденник: ріст і дні гри ----------

    static double Dbl(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d) && double.IsFinite(d) ? d : 0;

    /// <summary>Скільки виробів усього обпалено і які види вже обпалювались (за знімком щоденника).</summary>
    static (long Total, HashSet<string> Wares) Fired(JsonElement root)
    {
        var wares = new HashSet<string>(StringComparer.Ordinal);
        long sum = 0;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("fired", out var f) && f.ValueKind == JsonValueKind.Object)
            foreach (var kv in f.EnumerateObject())
                if (kv.Value.ValueKind == JsonValueKind.Number && kv.Value.TryGetInt64(out var n) && n > 0) { sum += n; wares.Add(kv.Name); }
        return (sum, wares);
    }

    /// <summary>Гончарі (люди) за тиждень: рядки щоденника від понеділка до неділі й години гри. Порожній — без щоденника.</summary>
    List<Potter> Gather(string week)
    {
        var result = new List<Potter>();
        if (Diary is not { Enabled: true } diary) return result;
        var monday = Monday(week);
        var rows = diary.Range(Before(monday, 2), week);
        var hours = diary.HoursRange(monday, week);
        var need = O.GrowMinMinutes * 60;
        var play = hours.GroupBy(h => (h.Key, h.Row.Day)).Where(g => g.Sum(x => x.Row.Seconds) >= need)
            .GroupBy(g => g.Key.Key).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var g in rows.GroupBy(r => Auth.NickKey(r.Nick)))
        {
            var mine = g.OrderBy(r => r.Day, StringComparer.Ordinal).ToList();
            var inWeek = mine.Where(r => string.CompareOrdinal(r.Day, monday) >= 0).ToList();
            if (inWeek.Count == 0 || !IsPerson(inWeek[^1].Nick)) continue;
            var before = mine.LastOrDefault(r => string.CompareOrdinal(r.Day, monday) < 0);
            try
            {
                using var first = JsonDocument.Parse(inWeek[0].Json);
                using var last = JsonDocument.Parse(inWeek[^1].Json);
                var start = Dbl(first.RootElement, "dayStart");
                var firedStart = 0L;
                if (before is not null)
                {
                    using var b = JsonDocument.Parse(before.Json);
                    if (start <= 0) start = Dbl(b.RootElement, "total");
                    firedStart = Fired(b.RootElement).Total;
                }
                else firedStart = Fired(first.RootElement).Total;
                var (firedEnd, wares) = Fired(last.RootElement);
                result.Add(new Potter(g.Key, inWeek[^1].Nick, start, Dbl(last.RootElement, "total"), play.GetValueOrDefault(g.Key), firedStart, firedEnd, wares));
            }
            catch (JsonException) { /* зіпсований знімок — гончаря пропускаємо */ }
        }
        return result;
    }

    List<ClickerGrowRow> BuildBoard(string week)
    {
        var o = O;
        var rows = Gather(week).Where(p => p.Start > 0 && p.End > 0)
            .Select(p => (P: p, X: p.End / p.Start, Ok: p.Days >= o.GrowMinDays && p.End >= o.GrowMinTotal))
            .OrderByDescending(x => x.Ok).ThenByDescending(x => x.X).ThenBy(x => x.P.Key, StringComparer.Ordinal).ToList();
        return [.. rows.Select((x, i) => new ClickerGrowRow(i + 1, x.P.Nick, x.X, x.P.Days, x.Ok))];
    }

    /// <summary>Живе табло росту поточного тижня (знімок живе <see cref="BoardTtl"/>).</summary>
    public List<ClickerGrowRow> Board(DateTimeOffset now)
    {
        var week = WeekOf(now);
        lock (_gate)
            if (_board is { } b && b.Week == week && now - b.At >= TimeSpan.Zero && now - b.At < BoardTtl) return b.Rows;
        return RefreshBoard(now);
    }

    /// <summary>Перерахувати табло з щоденника й покласти в знімок (фон кличе це сам, щоб вид гончаря не ходив по базу).</summary>
    List<ClickerGrowRow> RefreshBoard(DateTimeOffset now)
    {
        var week = WeekOf(now);
        var rows = BuildBoard(week);
        lock (_gate) _board = (week, now, rows);
        return rows;
    }

    // ---------- спільне замовлення ----------

    public ClickerCoopOrder? CoopNow(DateTimeOffset now)
    {
        if (!O.Enabled || !O.CoopEnabled) return null;
        var week = WeekOf(now);
        lock (_gate) return _coops.TryGetValue(week, out var c) ? Order(c) : null;
    }

    static ClickerCoopOrder Order(Coop c) => new(c.Week, c.Ware, c.Quality, c.Volume, c.Total, Ends(c.Week), c.Done != default);

    /// <summary>Скільки довіз цей гончар у спільне замовлення поточного тижня.</summary>
    public int CoopMine(string nick, DateTimeOffset now)
    {
        var week = WeekOf(now);
        lock (_gate) return _coops.TryGetValue(week, out var c) ? c.Parts.GetValueOrDefault(Auth.NickKey(nick)).N : 0;
    }

    /// <summary>Скільки найменше мусить довезти гончар, щоб дістати бонус.</summary>
    public static int MinPart(int volume, double share) => Math.Max(1, (int)Math.Ceiling(volume * share - 1e-9));

    /// <summary>Стеля внеску одного гончара: не менша за поріг бонусу.</summary>
    public static int MaxPart(int volume, double share, double minShare) =>
        Math.Max(MinPart(volume, minShare), (int)Math.Floor(volume * share + 1e-9));

    /// <summary>Скільки ще може довезти цей гончар до своєї стелі (0 — немає замовлення чи стеля вичерпана).</summary>
    public int CoopRoom(string nick, DateTimeOffset now)
    {
        var week = WeekOf(now);
        var key = Auth.NickKey(nick);
        lock (_gate)
            return _coops.TryGetValue(week, out var c)
                ? Math.Max(0, MaxPart(c.Volume, O.CoopMaxShare, O.CoopMinShare) - c.Parts.GetValueOrDefault(key).N)
                : 0;
    }

    /// <summary>
    /// Записати довезене: не більше, ніж ще бракує до обсягу, і не вище за стелю внеску одного гончара. Повертає, скільки прийнято (0 — замовлення нема, вже виконане, тиждень
    /// скінчився чи гончар не людина) — стільки гра й забирає з комори. Останнє довезене закриває замовлення й пише рядок у Балачки.
    /// </summary>
    public int CoopAdd(string nick, int n, DateTimeOffset now)
    {
        if (n <= 0 || !O.Enabled || !O.CoopEnabled || !IsPerson(nick)) return 0;
        var key = Auth.NickKey(nick);
        if (key.Length == 0) return 0;
        var week = WeekOf(now);
        string? shout = null;
        int take;
        lock (_gate)
        {
            if (!_coops.TryGetValue(week, out var c) || c.Settled || c.Done != default || Ends(week) <= now) return 0;
            take = Math.Min(n, c.Volume - c.Total);
            take = Math.Min(take, MaxPart(c.Volume, O.CoopMaxShare, O.CoopMinShare) - c.Parts.GetValueOrDefault(key).N);
            if (take <= 0) return 0;
            c.Parts[key] = (nick, c.Parts.GetValueOrDefault(key).N + take);
            c.Dirty = true;
            if (c.Total >= c.Volume)
            {
                c.Done = now;
                shout = DoneLine(c);
            }
        }
        if (shout is not null && _wire is not null)
        {
            try { _wire.Chat(shout); }
            catch (Exception ex) { _log?.LogWarning(ex, "спільне замовлення: рядок у Балачки не пішов"); }
        }
        return take;
    }

    string DoneLine(Coop c)
    {
        var w = Clicker.WareOf(c.Ware)?.Name.ToLowerInvariant() ?? c.Ware;
        var min = MinPart(c.Volume, O.CoopMinShare);
        var who = c.Parts.Values.Count(p => p.N >= min);
        return $"🤝 Округа виконала спільне замовлення: {c.Volume} × {w} (якість {c.Quality}+)! "
            + $"{who} {(who == 1 ? "гончар" : "гончарів")} з внеску від {min} — +{O.CoopBonusPct:0.##} % до всього від Дядька Глека, коли тиждень підіб'ють";
    }

    /// <summary>Нове замовлення на тиждень: виріб, відкритий у більшості активних гончарів, і обсяг від обпаленого минулого тижня.</summary>
    Coop NewOrder(string week, DateTimeOffset now)
    {
        var o = O;
        var last = Gather(Before(week));
        var active = last.Where(p => p.Days >= 1).ToList();
        if (active.Count == 0) active = last;
        var open = active.Count == 0 ? [] : Clicker.Wares.Where(w => active.Count(p => p.Wares.Contains(w.Key)) * 2 > active.Count).Select(w => w.Key).ToList();
        var ware = open.Count > 0 ? open[Rng.Next(open.Count)] : Clicker.Wares[0].Key;
        var fired = last.Sum(p => Math.Max(0, p.FiredEnd - p.FiredStart));
        var volume = (int)Math.Clamp(Math.Round(fired * o.CoopShare), o.CoopMin, Math.Max(o.CoopMin, o.CoopMax));
        return new Coop { Week = week, Ware = ware, Quality = Math.Max(1, o.CoopQuality), Volume = volume, Made = now, Dirty = true };
    }

    // ---------- нагороди ----------

    /// <summary>Нагороди тижня («Вітрила», +10 %), що чекають цього гончаря й ще діють.</summary>
    public List<ClickerWeekPrize> PrizesFor(string nick, DateTimeOffset now)
    {
        var key = Auth.NickKey(nick);
        if (key.Length == 0) return [];
        lock (_gate)
            return [.. _awards.Values.Where(a => a.NickKey == key && a.Until > now).OrderBy(a => a.Key, StringComparer.Ordinal)
                .Select(a => new ClickerWeekPrize(a.Kind == "coop" ? $"week:{a.Week}:coop" : a.Key, a.Kind, a.Pct, a.Until, a.Why))];
    }

    void AddAward(Award a)
    {
        lock (_gate)
        {
            if (_awards.ContainsKey(a.Key)) return;
            _awards[a.Key] = a;
        }
        try
        {
            _db?.Exec("INSERT OR IGNORE INTO clicker_week_awards(key, week, nick_key, nick, kind, pct, until, why) VALUES($k, $w, $nk, $n, $kind, $p, $u, $why)",
                ("$k", a.Key), ("$w", a.Week), ("$nk", a.NickKey), ("$n", a.Nick), ("$kind", a.Kind), ("$p", a.Pct),
                ("$u", a.Until.UtcDateTime.ToString("o")), ("$why", a.Why));
        }
        catch (Exception ex) { _log?.LogWarning(ex, "тиждень кола: не записав нагороду {Key}", a.Key); }
    }

    // ---------- вид ----------

    static string X(double x) => x >= 100 ? x.ToString("0", CultureInfo.InvariantCulture) : x.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',');

    /// <summary>
    /// Вид для кола, коротко: кінець тижня, табло росту (перші п'ять і моє місце) і спільне замовлення (що, скільки, моя частка).
    /// null — змагань вимкнено. Усе, що клієнт знає сам (пороги, відсотки вітрил), їде лише тим, що залежить від налаштувань.
    /// </summary>
    public object? Wire(string nick, DateTimeOffset now)
    {
        var o = O;
        if (!o.Enabled) return null;
        var key = Auth.NickKey(nick);
        var week = WeekOf(now);
        object? grow = null, coop = null;
        if (o.GrowEnabled)
        {
            var board = Board(now);
            var mine = board.FirstOrDefault(r => Auth.NickKey(r.Nick) == key);
            grow = new
            {
                prizes = o.GrowPrizeList.Select(Earn),
                sails = o.SailsList,
                days = o.GrowMinDays,
                mine = mine is null ? null : new { place = mine.Place, x = Math.Round(mine.X, 2), days = mine.Days, ok = mine.Ok },
                board = board.Take(BoardShown).Select(r => new { nick = r.Nick, x = Math.Round(r.X, 2), ok = r.Ok, me = Auth.NickKey(r.Nick) == key }),
                // Підсумок минулого тижня видно перші три доби: далі це лише вага у виді, що летить щопачки кліків.
                last = _last is { } l && now - Ends(l.Week) < TimeSpan.FromDays(3) ? l.Summary : null,
            };
        }
        if (o.CoopEnabled)
        {
            lock (_gate)
            {
                if (_coops.TryGetValue(week, out var c))
                    coop = new
                    {
                        ware = c.Ware, q = c.Quality, vol = c.Volume, done = c.Total, mine = c.Parts.GetValueOrDefault(key).N,
                        min = MinPart(c.Volume, o.CoopMinShare), max = MaxPart(c.Volume, o.CoopMaxShare, o.CoopMinShare), fin = c.Done != default, pct = o.CoopBonusPct,
                        people = c.Parts.Count,
                    };
            }
        }
        return new { ends = Ends(week), person = IsPerson(nick), grow, coop };
    }

    // ---------- підбиття ----------

    /// <summary>Позначити тиждень «сервіс його застав»: підбиваємо лише такі (щоб не платити за тижні до виходу змагань).</summary>
    void Touch(DateTimeOffset now)
    {
        var week = WeekOf(now);
        bool add;
        lock (_gate) add = _seen.Add(week);
        if (!add) return;
        try { _db?.Exec("INSERT OR IGNORE INTO clicker_week_seen(week) VALUES($w)", ("$w", week)); }
        catch (Exception ex) { _log?.LogWarning(ex, "тиждень кола: не записав тиждень {Week}", week); }
    }

    /// <summary>
    /// Підбити тиждень, що закінчився (найближчий), і завести замовлення на поточний. Повертає, скільки справ зроблено
    /// (підбито «Хто більше виріс», підбито замовлення, створено замовлення).
    /// </summary>
    public int Settle(DateTimeOffset now)
    {
        var o = O;
        if (!o.Enabled) return 0;
        var done = 0;
        var cur = WeekOf(now);
        var ended = Before(cur);
        bool seen, paid;
        lock (_gate) { seen = _seen.Contains(ended); paid = _paid.Contains(ended); }
        if (seen && !paid && o.GrowEnabled && SettleGrow(ended, now)) done++;
        if (o.CoopEnabled)
        {
            List<Coop> due;
            lock (_gate) due = [.. _coops.Values.Where(c => !c.Settled && Ends(c.Week) <= now).OrderBy(c => c.Week, StringComparer.Ordinal)];
            foreach (var c in due) if (SettleCoop(c, now)) done++;
        }
        Touch(now);
        if (o.CoopEnabled && Diary is { Enabled: true })
        {
            bool has;
            lock (_gate) has = _coops.ContainsKey(cur);
            if (!has)
            {
                var c = NewOrder(cur, now);
                lock (_gate) _coops[cur] = c;
                FlushNow();
                done++;
                _log?.LogInformation("спільне замовлення {Week}: {Ware} × {Volume}", cur, c.Ware, c.Volume);
            }
        }
        if (done > 0) Prune(now);
        return done;
    }

    bool SettleGrow(string week, DateTimeOffset now)
    {
        var o = O;
        var board = BuildBoard(week).Where(r => r.Ok).ToList();
        var prizes = o.GrowPrizeList.Select(Earn).ToArray();
        var sails = o.SailsList;
        var winners = board.Count >= Math.Max(1, o.GrowMinPlayers) ? board.Take(prizes.Length).ToList() : [];
        try
        {
            for (var i = 0; i < winners.Count; i++)
                if (prizes[i] > 0 && _stakes is not null)
                    _stakes.Grant(winners[i].Nick, prizes[i], $"clicker-week-grow:{week}", $"clicker-week-grow:{week}:{i + 1}");
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "тиждень кола: призи {Week} не пройшли — спробую згодом", week);
            return false;
        }
        var until = Ends(week).AddDays(Math.Max(1, o.RewardDays));
        for (var i = 0; i < winners.Count; i++)
            if (i < sails.Length && sails[i] > 0)
                AddAward(new Award($"week:{week}:grow:{i + 1}", week, Auth.NickKey(winners[i].Nick), winners[i].Nick, "sails", sails[i], until, "⛵ Вітрила тижня"));
        var summary = winners.Count > 0
            ? "⛵ Хто більше виріс за тиждень у Гончарному колі: " + string.Join(" · ", winners.Select(w => $"{w.Place}. {w.Nick} ×{X(w.X)}"))
                + $" — вітрила на тиждень: {string.Join(" / ", sails.Take(winners.Count).Select(p => "+" + p.ToString("0.##") + " %"))}"
            : "";
        lock (_gate)
        {
            _paid.Add(week);
            if (summary.Length > 0) _last = (week, summary);
        }
        try
        {
            _db?.Exec("INSERT OR IGNORE INTO clicker_week_paid(week, at, summary, winner) VALUES($w, $t, $s, $n)",
                ("$w", week), ("$t", now.UtcDateTime.ToString("o")), ("$s", summary), ("$n", winners.Count > 0 ? winners[0].Nick : null));
        }
        catch (Exception ex) { _log?.LogWarning(ex, "тиждень кола: не записав оплату {Week}", week); }
        if (winners.Count > 0) Shout(summary);
        _log?.LogInformation("тиждень кола {Week} підбито: {Summary}", week, summary);
        return true;
    }

    bool SettleCoop(Coop c, DateTimeOffset now)
    {
        var o = O;
        var min = MinPart(c.Volume, o.CoopMinShare);
        string? line = null;
        lock (_gate)
        {
            if (c.Settled) return false;
            c.Settled = true;
            c.Dirty = true;
        }
        if (c.Done != default)
        {
            var until = Ends(c.Week).AddDays(Math.Max(1, o.RewardDays));
            List<(string Key, string Nick)> got;
            lock (_gate) got = [.. c.Parts.Where(p => p.Value.N >= min).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, p.Value.Nick))];
            foreach (var (key, nick) in got)
                if (IsPerson(nick))
                    AddAward(new Award($"week:{c.Week}:coop:{key}", c.Week, key, nick, "coop", o.CoopBonusPct, until, "🤝 Спільне замовлення округи"));
            line = $"🤝 Спільне замовлення округи підбито: {got.Count} {(got.Count == 1 ? "гончар" : "гончарів")} довезли від {min} — +{o.CoopBonusPct:0.##} % до всього на тиждень";
        }
        else if (c.Parts.Count > 0)
            line = $"🤝 Спільне замовлення округи не довезли: {c.Total} з {c.Volume} — цього разу без бонусу, у понеділок буде нове";
        FlushNow();
        if (line is not null) Shout(line);
        return true;
    }

    void Shout(string text)
    {
        if (_wire is null || text.Length == 0) return;
        try { _wire.Chat(text); }
        catch (Exception ex) { _log?.LogWarning(ex, "тиждень кола: рядок у Балачки не пішов"); }
    }

    /// <summary>Підбиті замовлення й прострочені нагороди з пам'яті геть (у базі лишаються).</summary>
    void Prune(DateTimeOffset now)
    {
        var cut = Before(WeekOf(now), 3);
        lock (_gate)
        {
            foreach (var w in _coops.Where(c => c.Value.Settled && !c.Value.Dirty && string.CompareOrdinal(c.Key, cut) < 0).Select(c => c.Key).ToList()) _coops.Remove(w);
            foreach (var k in _awards.Where(a => string.CompareOrdinal(a.Value.Week, cut) < 0).Select(a => a.Key).ToList()) _awards.Remove(k);
        }
    }

    // ---------- база ----------

    void Load()
    {
        if (_db is null) return;
        _db.Exec(Schema);
        var cut = Before(WeekOf(_clock.UtcNow), 3);
        _db.With(c =>
        {
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT week FROM clicker_week_seen";
                using var r = cmd.ExecuteReader();
                while (r.Read()) _seen.Add(r.GetString(0));
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT week, summary FROM clicker_week_paid ORDER BY week";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    _paid.Add(r.GetString(0));
                    if (r.GetString(1).Length > 0) _last = (r.GetString(0), r.GetString(1));
                }
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT key, week, nick_key, nick, kind, pct, until, why FROM clicker_week_awards WHERE week >= $cut";
                cmd.Parameters.AddWithValue("$cut", cut);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    _awards[r.GetString(0)] = new Award(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetDouble(5),
                        ParseAt(r.GetString(6)), r.GetString(7));
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT week, ware, quality, volume, made, done, settled FROM clicker_coop WHERE week >= $cut";
                cmd.Parameters.AddWithValue("$cut", cut);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    _coops[r.GetString(0)] = new Coop
                    {
                        Week = r.GetString(0), Ware = r.GetString(1), Quality = r.GetInt32(2), Volume = r.GetInt32(3), Made = ParseAt(r.GetString(4)),
                        Done = r.IsDBNull(5) ? default : ParseAt(r.GetString(5)), Settled = r.GetInt64(6) != 0,
                    };
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT week, nick_key, nick, n FROM clicker_coop_part WHERE week >= $cut";
                cmd.Parameters.AddWithValue("$cut", cut);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    if (_coops.TryGetValue(r.GetString(0), out var co)) co.Parts[r.GetString(1)] = (r.GetString(2), r.GetInt32(3));
            }
        });
    }

    static DateTimeOffset ParseAt(string s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : default;

    public void FlushNow()
    {
        var snap = new List<(string Week, string Ware, int Q, int Vol, DateTimeOffset Made, DateTimeOffset Done, bool Settled, List<(string Key, string Nick, int N)> Parts)>();
        lock (_gate)
            foreach (var c in _coops.Values.Where(c => c.Dirty))
            {
                snap.Add((c.Week, c.Ware, c.Quality, c.Volume, c.Made, c.Done, c.Settled, [.. c.Parts.Select(p => (p.Key, p.Value.Nick, p.Value.N))]));
                c.Dirty = false;
            }
        if (snap.Count == 0 || _db is null) return;
        try
        {
            _db.With(conn =>
            {
                using var tx = conn.BeginTransaction();
                foreach (var s in snap)
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "INSERT OR REPLACE INTO clicker_coop(week, ware, quality, volume, made, done, settled) VALUES($w, $ware, $q, $v, $m, $d, $s)";
                        cmd.Parameters.AddWithValue("$w", s.Week);
                        cmd.Parameters.AddWithValue("$ware", s.Ware);
                        cmd.Parameters.AddWithValue("$q", s.Q);
                        cmd.Parameters.AddWithValue("$v", s.Vol);
                        cmd.Parameters.AddWithValue("$m", s.Made.UtcDateTime.ToString("o"));
                        cmd.Parameters.AddWithValue("$d", s.Done == default ? DBNull.Value : s.Done.UtcDateTime.ToString("o"));
                        cmd.Parameters.AddWithValue("$s", s.Settled ? 1 : 0);
                        cmd.ExecuteNonQuery();
                    }
                    foreach (var p in s.Parts)
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.Transaction = tx;
                        cmd.CommandText = "INSERT OR REPLACE INTO clicker_coop_part(week, nick_key, nick, n) VALUES($w, $k, $n, $c)";
                        cmd.Parameters.AddWithValue("$w", s.Week);
                        cmd.Parameters.AddWithValue("$k", p.Key);
                        cmd.Parameters.AddWithValue("$n", p.Nick);
                        cmd.Parameters.AddWithValue("$c", p.N);
                        cmd.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            });
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "спільне замовлення: не записав — повторю");
            lock (_gate) foreach (var s in snap) if (_coops.TryGetValue(s.Week, out var c)) c.Dirty = true;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { Settle(_clock.UtcNow); }
        catch (Exception ex) { _log?.LogWarning(ex, "тиждень кола: підбиття на старті не вдалось"); }
        var last = _clock.UtcNow;
        using var timer = new PeriodicTimer(FlushEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                FlushNow();
                if (_clock.UtcNow - last < SettleEvery) continue;
                last = _clock.UtcNow;
                try
                {
                    Settle(last);
                    // Табло свіжим тримає фон, а не чийсь вид під замком кімнати.
                    if (O.Enabled && O.GrowEnabled) RefreshBoard(last);
                }
                catch (Exception ex) { _log?.LogWarning(ex, "тиждень кола: підбиття не вдалось"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        FlushNow();
    }
}

/// <summary>Підключення: опції <c>Clicker:Week</c> і сервіс.</summary>
public static class ClickerWeekSetup
{
    public static IServiceCollection Add(IServiceCollection services)
    {
        services.AddOptions<ClickerWeekOptions>().BindConfiguration("Clicker:Week");
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptionsMonitor<ClickerWeekOptions>>();
            return new ClickerWeekService(sp.GetService<Db>(), sp.GetService<IStakes>(), () => opts.CurrentValue, sp.GetService<IClock>(),
                sp.GetService<ISlotsWire>(), sp.GetService<ILogger<ClickerWeekService>>())
            {
                Earn = n => sp.GetService<IOptionsMonitor<EconomyOptions>>()?.CurrentValue.Earn(n) ?? n,
                Diary = sp.GetService<ClickerDiaryService>(),
            };
        });
        services.AddHostedService(sp => sp.GetRequiredService<ClickerWeekService>());
        return services;
    }
}
