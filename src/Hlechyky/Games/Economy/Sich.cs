using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Economy;

/// <summary>Звання вічного рейтингу Січі: від <see cref="Min"/> Ело.</summary>
public sealed record SichTier(string Icon, string Title, int Min);

/// <summary>Рядок тижневого заліку. <see cref="ByDay"/> — очки за пн…нд.</summary>
public sealed record SichRow(string Nick, int Place, int Points, int Solved, int[] ByDay, int Elo, string Rank, string RankIcon, int EloToday);

/// <summary>Мій рахунок проти <see cref="Nick"/>: скільки разів я обійшов (W) і мене обійшли (L) — за тиждень і за весь час.</summary>
public sealed record SichDuel(string Nick, int W, int L, int AllW, int AllL);

/// <summary>«Що, якби»: закрий сьогодні <see cref="Game"/> першим — до +<see cref="Gain"/> очок і <see cref="Place"/> місце.</summary>
public sealed record SichIf(string Game, string Title, int Gain, int Place);

/// <summary>Номінація тижня і хто її тримає (рівні — усі).</summary>
public sealed record SichNom(string Id, string Icon, string Title, string Hint, IReadOnlyList<string> Nicks, int Value);

/// <summary>Рядок вічного рейтингу.</summary>
public sealed record SichEternal(string Nick, int Elo, string Rank, string RankIcon, int Days, int Today);

/// <summary>Я в Січі: місце тижня, звання і що далі, дуелі, «що, якби».</summary>
public sealed record SichMe(string Nick, int? Place, int Points, int Elo, string Rank, string RankIcon, string? NextRank, int NextNeed,
    IReadOnlyList<SichDuel> Duels, IReadOnlyList<SichIf> WhatIf);

public sealed record SichRules(IReadOnlyList<int> Prizes, int NomPrize, int MinPlayers, IReadOnlyList<SichTier> Tiers);

/// <summary>Те, що віддає GET /api/games/sich.</summary>
public sealed record SichView(string Week, string From, string To, bool Live, int DaysLeft, int Players, IReadOnlyList<SichRow> Rows,
    IReadOnlyList<SichNom> Noms, IReadOnlyList<SichEternal> Eternal, IReadOnlyList<string> Crown, IReadOnlyList<string> Weeks,
    SichRules Rules, SichMe? Me);

/// <summary>
/// Рядок таблички тижня в «☀ Сьогодні»: <see cref="Move"/> — на скільки місць піднявся (+) чи впав (−) за сьогодні;
/// <see cref="New"/> — до сьогодні очок не мав.
/// </summary>
public sealed record SichTop(string Nick, int Place, int Points, int Move, bool New = false);

/// <summary>Хто сьогодні вже зіграв головоломку: місце, очки Січі за неї, чи розв'язав; <see cref="Pts"/> — очки гри (лише «очкові»).</summary>
public sealed record SichSeat(string Nick, int Place, int Score, bool Solved, long? Pts = null);

/// <summary>
/// Коротко для «☀ Сьогодні»: картка, табличка тижня (<see cref="Top"/>, я — <see cref="MyDays"/> за пн…нд), хто що сьогодні
/// зіграв (<see cref="Games"/>) і скільки очок ще лежить на столі (<see cref="Left"/> — якщо закрити все, що не торкався, першим).
/// </summary>
public sealed record SichBrief(int? Place, int Of, int Points, string? Leader, int LeaderPoints, int Gain,
    IReadOnlyList<SichTop>? Top = null, int[]? MyDays = null, int TodayIndex = 0, int DaysLeft = 0, int Left = 0,
    IReadOnlyDictionary<string, IReadOnlyList<SichSeat>>? Games = null);

/// <summary>Підбитий тиждень — так, як його побачили в ніч на понеділок (дошки очок старі дні потім забувають).</summary>
public sealed class SichWeek
{
    public string Week { get; set; } = "";
    public int Players { get; set; }
    public List<SichRow> Rows { get; set; } = [];
    public List<SichNom> Noms { get; set; } = [];
    /// <summary>👑 на наступний тиждень. Порожньо — тиждень не відбувся (замало людей).</summary>
    public List<string> Crown { get; set; } = [];
}

/// <summary>
/// Січ щоденних (25.10.2026): тижневий залік усіх головоломок «Щоденного глека» і вічний рейтинг Ело зі званнями.
///
/// Очки за головоломку дня: розв'язав — 1 і ще по очку за кожного, кого обійшов (спроби, тоді час; головоломки з очками —
/// очки), хто взявся й не розв'язав — 0, і його обходять усі, хто розв'язав. Тиждень — пн…нд київським часом. У ніч на
/// понеділок — підсумок: призи місцям (<see cref="EconomyOptions.SichPrizes"/>), номінації 🐓 🎯 ⚡ 🔗
/// (<see cref="EconomyOptions.SichNomPrize"/>) і 👑 «Гетьман тижня» на весь наступний тиждень, коли очки набрало
/// щонайменше <see cref="EconomyOptions.SichMinPlayers"/> людей. Лише люди: акаунти без ботів.
///
/// Ело: кожна головоломка дня — партія між усіма, хто її грав (кожен з кожним, K ділиться на n−1). Підбиті дні лежать у
/// game_state (<c>sich:elo</c>), бо дошки очок старі дні забувають; свіжі (вчора до підбиття й сьогодні) дораховуються
/// на льоту. Глек у Балачках — лише коли змінився лідер тижня (не частіше трьох разів на день, удень), в неділю ввечері
/// і з підсумком тижня.
/// </summary>
public sealed class Sich(EconomyStore store, Daily daily, Economy economy, GameNames names, IGameStore states, IClock clock,
    IOptionsMonitor<EconomyOptions> opts, IOutbox outbox, ILogger<Sich> log) : BackgroundService
{
    public const double Start = 1000, K = 24;
    /// <summary>Звання за Ело — найвище, до якого дотягнув.</summary>
    public static readonly SichTier[] Tiers =
    [
        new("🟤", "Глина", 0), new("🔶", "Черепок", 985), new("🍶", "Глечик", 1015), new("🥣", "Макітра", 1050), new("🔱", "Гетьман", 1100),
    ];
    /// <summary>Скільки разів на день Глек оголошує нового лідера, і з якої до котрої години (Київ).</summary>
    const int LeaderDaily = 3, LeaderFrom = 9, LeaderTo = 23;
    /// <summary>Лідера оголошуємо, лише коли в нього стільки очок (зранку понеділка «лідер з 1 очком» — не новина).</summary>
    const int LeaderMinPoints = 5;
    /// <summary>Скільки разів чекати на дошки очок, перш ніж підбити день чи тиждень без них (раз на хвилину).</summary>
    const int PointsTries = 5;
    static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(20);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------------ дні й тижні

    static DateOnly D(string day) => DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    static string S(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    static string Shift(string day, int n) => S(D(day).AddDays(n));

    /// <summary>Понеділок тижня, у який падає <paramref name="day"/>.</summary>
    public static string WeekOf(string day)
    {
        var d = D(day);
        return S(d.AddDays(-(((int)d.DayOfWeek + 6) % 7)));
    }

    /// <summary>«19–25.10» або «26.10–01.11».</summary>
    public static string Range(string monday)
    {
        var a = D(monday);
        var b = a.AddDays(6);
        return a.Month == b.Month ? $"{a:dd}–{b:dd.MM}" : $"{a:dd.MM}–{b:dd.MM}";
    }

    public static SichTier TierOf(double elo) => Tiers.Last(t => elo >= t.Min);

    // ------------------------------------------------------------------ головоломка дня → місця й очки

    public sealed record Entry(string Day, string Game, string Key, string Nick, bool Solved, int Attempts, int Ms, DateTimeOffset At, long? Pts);
    /// <summary>Місце в головоломці дня (рівні — спільне) і очки Січі за неї.</summary>
    public sealed record Placed(Entry E, int Place, int Score);
    public sealed record GameDay(string Day, string Game, bool Points, List<Placed> All);

    /// <summary>
    /// Місця й очки: розв'язки — за очками гри (більше — краще; без очок — у хвіст) або за спробами, тоді часом, як топ
    /// дня; промахи — усі разом останні, з нулем.
    /// </summary>
    public static List<Placed> Rank(IReadOnlyList<Entry> rows, bool points)
    {
        (long, long) Key(Entry e) => points ? (e.Pts is { } p ? -p : long.MaxValue, 0) : (e.Attempts, e.Ms);
        var solved = rows.Where(r => r.Solved).OrderBy(Key).ThenBy(r => r.At).ToList();
        var failed = rows.Count - solved.Count;
        var list = new List<Placed>(rows.Count);
        for (var i = 0; i < solved.Count; i++)
        {
            var k = Key(solved[i]);
            var place = i > 0 && Key(solved[i - 1]) == k ? list[i - 1].Place : i + 1;
            var worse = solved.Count(s => Key(s).CompareTo(k) > 0);
            list.Add(new Placed(solved[i], place, 1 + worse + failed));
        }
        foreach (var r in rows.Where(r => !r.Solved)) list.Add(new Placed(r, solved.Count + 1, 0));
        return list;
    }

    /// <summary>Усі головоломки людей за дні від і до. <paramref name="missing"/> — дошка очок ще не дочитала когось.</summary>
    public List<GameDay> Load(string from, string to, out bool missing)
    {
        missing = false;
        var puzzles = daily.Puzzles.ToHashSet(StringComparer.Ordinal);
        var result = new List<GameDay>();
        foreach (var g in store.DailyPeople(from, to).Where(x => puzzles.Contains(x.Row.Game)).GroupBy(x => (x.Row.Day, x.Row.Game)))
        {
            var points = daily.HasPoints(g.Key.Game);
            var rows = g.Select(x =>
            {
                var r = x.Row;
                return new Entry(r.Day, r.Game, r.NickKey, r.Nick, r.Solved, r.Attempts, r.Ms, x.At,
                    points && r.Solved ? daily.PointsOf(r.Game, r.Day, r.Nick) : null);
            }).ToList();
            if (points && rows.Any(r => r.Solved && r.Pts is null)) missing = true;
            result.Add(new GameDay(g.Key.Day, g.Key.Game, points, Rank(rows, points)));
        }
        return result;
    }

    // ------------------------------------------------------------------ Ело

    public sealed class EloMan
    {
        public string Nick { get; set; } = "";
        public double Elo { get; set; } = Start;
        /// <summary>Скільки головоломок-днів зіграно.</summary>
        public int Days { get; set; }
    }

    /// <summary>Вічний рейтинг: Ело кожного і хто кого скільки разів обійшов (ключ — «a|b», a &lt; b).</summary>
    public sealed class EloBook
    {
        public string Through { get; set; } = "";
        public Dictionary<string, EloMan> People { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int[]> Pairs { get; set; } = new(StringComparer.Ordinal);

        public EloBook Copy() => new()
        {
            Through = Through,
            People = People.ToDictionary(p => p.Key, p => new EloMan { Nick = p.Value.Nick, Elo = p.Value.Elo, Days = p.Value.Days }, StringComparer.Ordinal),
            Pairs = Pairs.ToDictionary(p => p.Key, p => (int[])p.Value.Clone(), StringComparer.Ordinal),
        };

        public double EloOf(string key) => People.TryGetValue(key, out var m) ? m.Elo : Start;

        /// <summary>Скільки разів <paramref name="a"/> обійшов <paramref name="b"/> і навпаки.</summary>
        public (int W, int L) Pair(string a, string b)
        {
            var less = string.CompareOrdinal(a, b) < 0;
            if (!Pairs.TryGetValue(less ? a + "|" + b : b + "|" + a, out var p)) return (0, 0);
            return less ? (p[0], p[1]) : (p[1], p[0]);
        }

        public void Apply(GameDay gd)
        {
            var all = gd.All;
            foreach (var p in all)
            {
                if (!People.TryGetValue(p.E.Key, out var m)) People[p.E.Key] = m = new EloMan();
                m.Nick = p.E.Nick;
                m.Days++;
            }
            if (all.Count < 2) return;
            var r = all.Select(p => People[p.E.Key].Elo).ToArray();
            var d = new double[all.Count];
            for (var i = 0; i < all.Count; i++)
                for (var j = i + 1; j < all.Count; j++)
                {
                    var s = all[i].Place < all[j].Place ? 1 : all[i].Place > all[j].Place ? 0 : 0.5;
                    var e = 1 / (1 + Math.Pow(10, (r[j] - r[i]) / 400));
                    d[i] += s - e;
                    d[j] -= s - e;
                    if (s == 0.5) continue;
                    var (a, b) = (all[i].E.Key, all[j].E.Key);
                    var less = string.CompareOrdinal(a, b) < 0;
                    var key = less ? a + "|" + b : b + "|" + a;
                    if (!Pairs.TryGetValue(key, out var w)) Pairs[key] = w = new int[2];
                    w[(s == 1) == less ? 0 : 1]++;
                }
            for (var i = 0; i < all.Count; i++) People[all[i].E.Key].Elo += K / (all.Count - 1) * d[i];
        }

        public void Apply(IEnumerable<GameDay> days)
        {
            foreach (var gd in days.OrderBy(x => x.Day, StringComparer.Ordinal).ThenBy(x => x.Game, StringComparer.Ordinal)) Apply(gd);
        }
    }

    const string EloKey = "sich:elo", WeeksKey = "sich:weeks", LeaderKey = "sich:leader";
    static string WeekKey(string monday) => "sich:week:" + monday;

    T? Read<T>(string key) where T : class
    {
        try { return states.LoadState(key) is { } j ? JsonSerializer.Deserialize<T>(j, Json) : null; }
        catch (Exception ex) { log.LogWarning(ex, "січ: не прочитав {Key}", key); return null; }
    }
    void Write<T>(string key, T value) => states.SaveState(key, JsonSerializer.Serialize(value, Json));

    readonly object _gate = new();
    EloBook? _book;
    EloBook Book() { lock (_gate) return _book ??= Read<EloBook>(EloKey) ?? new EloBook(); }

    /// <summary>
    /// Підбити Ело по вчорашній день: підбиті дні — у базу. Повертає false, коли дошка очок ще дочитує якийсь день і
    /// чекати ще є сенс (тоді — наступного разу).
    /// </summary>
    public async Task<bool> CatchUp(CancellationToken ct = default)
    {
        var yesterday = Shift(Today(), -1);
        var book = Book().Copy();
        var from = book.Through.Length > 0 ? Shift(book.Through, 1) : store.DailyFirstDay();
        if (from is null || string.CompareOrdinal(from, yesterday) > 0) return true;
        var changed = false;
        try
        {
            for (var day = from; string.CompareOrdinal(day, yesterday) <= 0; day = Shift(day, 1))
            {
                var gds = Load(day, day, out var missing);
                // дошки очок піднімають старий день фоном — дамо їм мить
                for (var i = 0; missing && i < 8; i++)
                {
                    await Task.Delay(250, ct);
                    gds = Load(day, day, out missing);
                }
                if (missing && day == yesterday && ++_eloTries < PointsTries) return false;
                book.Apply(gds);
                book.Through = day;
                changed = true;
            }
        }
        finally
        {
            if (changed)
            {
                Write(EloKey, book);
                lock (_gate) { _book = book; _live = null; }
            }
        }
        _eloTries = 0;
        return true;
    }
    int _eloTries;

    /// <summary>Ело зараз: підбите + свіжі дні на льоту. <paramref name="before"/> — те саме без сьогоднішнього дня.</summary>
    (EloBook Now, EloBook Before) Live()
    {
        lock (_gate)
        {
            if (_live is { } l && clock.UtcNow - l.At < CacheFor) return (l.Now, l.Before);
        }
        var today = Today();
        var before = Book().Copy();
        var from = before.Through.Length > 0 ? Shift(before.Through, 1) : store.DailyFirstDay() ?? today;
        var gds = string.CompareOrdinal(from, today) <= 0 ? Load(from, today, out _) : [];
        before.Apply(gds.Where(g => g.Day != today));
        var now = before.Copy();
        now.Apply(gds.Where(g => g.Day == today));
        lock (_gate) _live = (now, before, clock.UtcNow);
        return (now, before);
    }
    (EloBook Now, EloBook Before, DateTimeOffset At)? _live;

    // ------------------------------------------------------------------ тиждень

    sealed record Stand(string Key, string Nick, int Points, int Solved, int[] ByDay);
    sealed record WeekCalc(string Monday, List<GameDay> Days, List<(Stand S, int Place)> Table, bool Missing, DateTimeOffset At);

    string Today() => Days.Today(clock);

    WeekCalc Calc(string monday)
    {
        lock (_gate)
            if (_weeks.TryGetValue(monday, out var c) && clock.UtcNow - c.At < CacheFor) return c;
        var to = Shift(monday, 6);
        var today = Today();
        if (string.CompareOrdinal(to, today) > 0) to = today;
        var gds = Load(monday, to, out var missing);
        var stands = new Dictionary<string, Stand>(StringComparer.Ordinal);
        foreach (var gd in gds)
        {
            var i = D(gd.Day).DayNumber - D(monday).DayNumber;
            foreach (var p in gd.All)
            {
                var s = stands.TryGetValue(p.E.Key, out var x) ? x : new Stand(p.E.Key, p.E.Nick, 0, 0, new int[7]);
                s.ByDay[i] += p.Score;
                stands[p.E.Key] = s with { Nick = p.E.Nick, Points = s.Points + p.Score, Solved = s.Solved + (p.E.Solved ? 1 : 0) };
            }
        }
        var sorted = stands.Values.OrderByDescending(s => s.Points).ThenByDescending(s => s.Solved).ThenBy(s => s.Nick, StringComparer.Ordinal).ToList();
        var table = new List<(Stand, int)>(sorted.Count);
        for (var i = 0; i < sorted.Count; i++)
        {
            var same = i > 0 && sorted[i].Points == sorted[i - 1].Points && sorted[i].Solved == sorted[i - 1].Solved;
            table.Add((sorted[i], same ? table[i - 1].Item2 : i + 1));
        }
        var calc = new WeekCalc(monday, gds, table, missing, clock.UtcNow);
        // дошки очок ще дочитують базу (щойно після старту) — такий залік не тримаємо: за мить він буде точнішим
        if (!missing) lock (_gate) _weeks[monday] = calc;
        return calc;
    }
    readonly Dictionary<string, WeekCalc> _weeks = new(StringComparer.Ordinal);

    static int Players(WeekCalc w) => w.Table.Count(t => t.S.Points > 0);

    List<SichRow> Rows(WeekCalc w, EloBook now, EloBook before) => w.Table.Select(t =>
    {
        var elo = now.EloOf(t.S.Key);
        var tier = TierOf(elo);
        return new SichRow(t.S.Nick, t.Place, t.S.Points, t.S.Solved, t.S.ByDay, (int)Math.Round(elo), tier.Title, tier.Icon,
            (int)Math.Round(elo - before.EloOf(t.S.Key)));
    }).ToList();

    /// <summary>
    /// Номінації: 🐓 найбільше перших розв'язків дня, 🎯 найменше спроб (де спроби різнились), ⚡ найшвидший розв'язок —
    /// там, де розв'язало щонайменше двоє, і щонайменше двічі за тиждень; 🔗 найбільше днів тижня з розв'язками (рівні — більше розв'язків).
    /// </summary>
    public static List<SichNom> Noms(IReadOnlyList<GameDay> days, IReadOnlyList<(string Key, string Nick, int Solved, int[] ByDay)> people)
    {
        var nick = people.ToDictionary(p => p.Key, p => p.Nick, StringComparer.Ordinal);
        var early = new Dictionary<string, int>(StringComparer.Ordinal);
        var sniper = new Dictionary<string, int>(StringComparer.Ordinal);
        var fast = new Dictionary<string, int>(StringComparer.Ordinal);
        static void Inc(Dictionary<string, int> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;
        foreach (var gd in days)
        {
            var solved = gd.All.Where(p => p.E.Solved).Select(p => p.E).ToList();
            if (solved.Count < 2) continue;
            var first = solved.MinBy(e => e.At)!;
            if (solved.Count(e => e.At == first.At) == 1) Inc(early, first.Key);
            if (!gd.Points)
            {
                var min = solved.Min(e => e.Attempts);
                if (solved.Any(e => e.Attempts > min))
                    foreach (var e in solved.Where(e => e.Attempts == min)) Inc(sniper, e.Key);
            }
            var timed = solved.Where(e => e.Ms > 0).ToList();
            if (timed.Count >= 2)
            {
                var ms = timed.Min(e => e.Ms);
                if (timed.Count(e => e.Ms == ms) == 1) Inc(fast, timed.First(e => e.Ms == ms).Key);
            }
        }
        var list = new List<SichNom>();
        // раз на тиждень — випадковість, а не номінація
        void Add(string id, string icon, string title, string hint, Dictionary<string, int> d)
        {
            if (d.Count == 0 || d.Values.Max() < 2) return;
            var top = d.Values.Max();
            list.Add(new SichNom(id, icon, title, hint,
                d.Where(x => x.Value == top).Select(x => nick.GetValueOrDefault(x.Key, x.Key)).Order(StringComparer.Ordinal).ToList(), top));
        }
        Add("early", "🐓", "Ранній птах", "найчастіше розв'язує першим за день", early);
        Add("sniper", "🎯", "Снайпер", "найчастіше влучає за найменше спроб", sniper);
        Add("fast", "⚡", "Блискавка", "найчастіше найшвидший", fast);
        var iron = people.Where(p => p.Solved > 0).Select(p => (p.Key, Days: p.ByDay.Count(x => x > 0), p.Solved)).ToList();
        if (iron.Count > 0)
        {
            var best = iron.Max(x => (x.Days, x.Solved));
            list.Add(new SichNom("iron", "🔗", "Залізна серія", "найбільше днів тижня з розв'язками",
                iron.Where(x => (x.Days, x.Solved) == best).Select(x => nick[x.Key]).Order(StringComparer.Ordinal).ToList(), best.Days));
        }
        return list;
    }

    List<SichNom> Noms(WeekCalc w) => Noms(w.Days, w.Table.Select(t => (t.S.Key, t.S.Nick, t.S.Solved, t.S.ByDay)).ToList());

    // ------------------------------------------------------------------ що віддаємо

    SichRules Rules()
    {
        var o = opts.CurrentValue;
        return new SichRules((o.SichPrizes ?? []).Select(p => o.Earn(Math.Max(0, p))).ToList(), o.Earn(Math.Max(0, o.SichNomPrize)),
            Math.Max(1, o.SichMinPlayers), Tiers);
    }

    /// <summary>👑 на цей тиждень — переможці минулого (якщо він відбувся й уже підбитий).</summary>
    public IReadOnlyList<string> Crown()
    {
        var prev = Shift(WeekOf(Today()), -7);
        lock (_gate)
            if (_crown is { } c && c.Week == prev) return c.Nicks;
        var nicks = Read<SichWeek>(WeekKey(prev))?.Crown ?? [];
        lock (_gate) _crown = (prev, nicks);
        return nicks;
    }
    (string Week, List<string> Nicks)? _crown;

    /// <summary>Звання всіх, хто має в Січі бодай один день: нік → (значок, звання, Ело).</summary>
    public IReadOnlyDictionary<string, (string Icon, string Title, int Elo)> Ranks()
    {
        var (now, _) = Live();
        return now.People.Values.GroupBy(m => m.Nick, StringComparer.Ordinal).ToDictionary(g => g.Key, g =>
        {
            var m = g.First();
            var t = TierOf(m.Elo);
            return (t.Icon, t.Title, (int)Math.Round(m.Elo));
        }, StringComparer.Ordinal);
    }

    public SichView View(string? nick, string? week = null)
    {
        var today = Today();
        var current = WeekOf(today);
        var monday = week is { Length: 10 } && DateOnly.TryParseExact(week, "yyyy-MM-dd", out var wd) ? WeekOf(S(wd)) : current;
        if (string.CompareOrdinal(monday, current) > 0) monday = current;
        var live = monday == current;
        var (now, before) = Live();
        var weeks = Read<List<string>>(WeeksKey) ?? [];
        var eternal = now.People.Values.Where(m => m.Days > 0).OrderByDescending(m => m.Elo).Select(m =>
        {
            var t = TierOf(m.Elo);
            var key = Economy.Key(m.Nick);
            return new SichEternal(m.Nick, (int)Math.Round(m.Elo), t.Title, t.Icon, m.Days, (int)Math.Round(m.Elo - before.EloOf(key)));
        }).ToList();

        List<SichRow> rows;
        List<SichNom> noms;
        int players;
        WeekCalc? calc = null;
        if (!live && Read<SichWeek>(WeekKey(monday)) is { } saved)
        {
            rows = saved.Rows;
            noms = saved.Noms;
            players = saved.Players;
        }
        else
        {
            calc = Calc(monday);
            rows = Rows(calc, now, before);
            noms = Noms(calc);
            players = Players(calc);
        }
        var daysLeft = live ? Math.Max(0, D(Shift(monday, 6)).DayNumber - D(today).DayNumber) : 0;
        SichMe? me = null;
        if (!string.IsNullOrWhiteSpace(nick))
        {
            var key = Economy.Key(nick);
            var elo = now.EloOf(key);
            var tier = TierOf(elo);
            var next = Tiers.FirstOrDefault(t => t.Min > elo);
            var mine = rows.FirstOrDefault(r => Economy.Key(r.Nick) == key);
            me = new SichMe(nick, mine?.Place, mine?.Points ?? 0, (int)Math.Round(elo), tier.Title, tier.Icon, next?.Title,
                next is null ? 0 : (int)Math.Ceiling(next.Min - elo), calc is null ? [] : Duels(calc, now, key),
                live && calc is not null ? WhatIf(calc, key, today) : []);
        }
        return new SichView(monday, monday, Shift(monday, 6), live, daysLeft, players, rows, noms, eternal, Crown(),
            weeks.OrderDescending(StringComparer.Ordinal).ToList(), Rules(), me);
    }

    List<SichDuel> Duels(WeekCalc w, EloBook now, string me)
    {
        var week = new Dictionary<string, (int W, int L)>(StringComparer.Ordinal);
        foreach (var gd in w.Days)
        {
            var mine = gd.All.FirstOrDefault(p => p.E.Key == me);
            if (mine is null) continue;
            foreach (var p in gd.All.Where(p => p.E.Key != me))
            {
                var (wins, losses) = week.GetValueOrDefault(p.E.Key);
                if (mine.Place < p.Place) wins++;
                else if (mine.Place > p.Place) losses++;
                week[p.E.Key] = (wins, losses);
            }
        }
        var rivals = now.People.Keys.Where(k => k != me && (week.ContainsKey(k) || now.Pair(me, k) is not (0, 0)));
        return rivals.Select(k =>
        {
            var (aw, al) = now.Pair(me, k);
            var (ww, wl) = week.GetValueOrDefault(k);
            return new SichDuel(now.People[k].Nick, ww, wl, aw, al);
        }).OrderByDescending(d => d.W + d.L).ThenByDescending(d => d.AllW + d.AllL).Take(8).ToList();
    }

    /// <summary>Головоломки, яких я сьогодні ще не торкався: скільки дадуть, якщо закрити першим, і на яке місце підніме.</summary>
    List<SichIf> WhatIf(WeekCalc w, string me, string today, int take = 3)
    {
        var mine = w.Table.FirstOrDefault(t => t.S.Key == me).S?.Points ?? 0;
        var others = w.Table.Where(t => t.S.Key != me).Select(t => t.S.Points).ToList();
        var byGame = w.Days.Where(g => g.Day == today).ToDictionary(g => g.Game, StringComparer.Ordinal);
        var list = new List<SichIf>();
        foreach (var game in daily.Puzzles)
        {
            var gd = byGame.GetValueOrDefault(game);
            if (gd is not null && gd.All.Any(p => p.E.Key == me)) continue;
            var gain = 1 + (gd?.All.Count ?? 0);
            list.Add(new SichIf(game, names.Title(game), gain, 1 + others.Count(p => p > mine + gain)));
        }
        return list.OrderByDescending(x => x.Gain).ThenBy(x => x.Place).Take(take).ToList();
    }

    /// <summary>Скільки рядків таблички тижня в «☀ Сьогодні» (я — завжди, навіть нижче).</summary>
    const int BriefTop = 5;

    /// <summary>
    /// Місця тижня до сьогоднішнього дня — щоб показати ▲▼ за сьогодні. Рівні очки — спільне місце (розв'язки до сьогодні
    /// окремо не зберігаємо, тож без другого ключа).
    /// </summary>
    static Dictionary<string, int> PlacesBefore(WeekCalc w, int todayIndex)
    {
        var pts = w.Table.Select(t => (t.S.Key, P: t.S.Points - (todayIndex is >= 0 and < 7 ? t.S.ByDay[todayIndex] : 0)))
            .Where(x => x.P > 0).ToList();
        return pts.ToDictionary(x => x.Key, x => 1 + pts.Count(o => o.P > x.P), StringComparer.Ordinal);
    }

    public SichBrief Brief(string nick)
    {
        var today = Today();
        var monday = WeekOf(today);
        var w = Calc(monday);
        var key = Economy.Key(nick);
        var me = w.Table.FirstOrDefault(t => t.S.Key == key);
        var lead = w.Table.FirstOrDefault();
        var ifs = WhatIf(w, key, today, int.MaxValue);
        var gain = ifs.Select(x => x.Gain).DefaultIfEmpty(0).Max();
        var idx = D(today).DayNumber - D(monday).DayNumber;
        var before = PlacesBefore(w, idx);
        var scored = w.Table.Where(t => t.S.Points > 0).ToList();
        // нуль очок (самі промахи) — не в табличці: це залік, а не список гравців
        SichTop Row((Stand S, int Place) t) => before.TryGetValue(t.S.Key, out var b)
            ? new SichTop(t.S.Nick, t.Place, t.S.Points, b - t.Place)
            : new SichTop(t.S.Nick, t.Place, t.S.Points, 0, New: true);
        var top = scored.Take(BriefTop).Select(Row).ToList();
        if (me.S is { Points: > 0 } && !top.Any(t => Economy.Key(t.Nick) == key)) top.Add(Row(me));
        var games = w.Days.Where(g => g.Day == today).ToDictionary(g => g.Game,
            g => (IReadOnlyList<SichSeat>)g.All.OrderBy(p => p.Place).Select(p => new SichSeat(p.E.Nick, p.Place, p.Score, p.E.Solved, p.E.Pts)).ToList(),
            StringComparer.Ordinal);
        return new SichBrief(me.S is null ? null : me.Place, Players(w), me.S?.Points ?? 0, lead.S?.Nick, lead.S?.Points ?? 0, gain,
            top, me.S?.ByDay ?? new int[7], idx, Math.Max(0, 6 - idx), ifs.Sum(x => x.Gain), games);
    }

    // ------------------------------------------------------------------ підсумок тижня

    /// <summary>
    /// Підбити тиждень: знімок у базу, призи й номінації (ключі леджера — раз на тиждень), рядок Глека. −1 — дошка очок
    /// ще дочитує (не <paramref name="final"/>: згодом), інакше — скільки роздано.
    /// </summary>
    public int Settle(string monday, bool final = true)
    {
        if (Read<SichWeek>(WeekKey(monday)) is not null) return 0;
        lock (_gate) _weeks.Remove(monday);   // кеш міг лишитись із вечора неділі
        var w = Calc(monday);
        if (w.Missing && !final)
        {
            lock (_gate) _weeks.Remove(monday);   // наступна спроба — свіжими очками
            return -1;
        }
        var (now, before) = Live();
        var rules = Rules();
        var players = Players(w);
        var held = players >= rules.MinPlayers;
        var snap = new SichWeek
        {
            Week = monday, Players = players, Rows = Rows(w, now, before).Select(r => r with { EloToday = 0 }).ToList(), Noms = Noms(w),
            Crown = held ? w.Table.Where(t => t.Place == 1 && t.S.Points > 0).Select(t => t.S.Nick).ToList() : [],
        };
        Write(WeekKey(monday), snap);
        var weeks = Read<List<string>>(WeeksKey) ?? [];
        if (!weeks.Contains(monday)) { weeks.Add(monday); Write(WeeksKey, weeks); }
        lock (_gate) _crown = null;

        var range = Range(monday);
        var given = 0;
        if (held)
        {
            foreach (var (s, place) in w.Table.Where(t => t.S.Points > 0))
            {
                var prize = place <= rules.Prizes.Count ? rules.Prizes[place - 1] : 0;
                if (prize > 0 && economy.Grant(s.Nick, prize, "sich-week", $"sich:{monday}:{s.Key}",
                        $"+{prize} {Economy.Shards(prize)}: 🏆 Січ щоденних за {range} — {place} місце") == GrantResult.Applied)
                    given += prize;
            }
            if (rules.NomPrize > 0)
                foreach (var n in snap.Noms)
                    foreach (var who in n.Nicks)
                        if (economy.Grant(who, rules.NomPrize, "sich-nom:" + n.Id, $"sich-nom:{n.Id}:{monday}:{Economy.Key(who)}",
                                $"+{rules.NomPrize} {Economy.Shards(rules.NomPrize)}: {n.Icon} {n.Title} тижня в Січі щоденних ({range})") == GrantResult.Applied)
                            given += rules.NomPrize;
        }
        var text = SettleText(range, snap, held);
        if (text is not null) Say(text);
        log.LogInformation("січ: тиждень {Week} підбито — {Players} у заліку, роздано {Shards}", monday, players, given);
        return given;
    }

    public static string? SettleText(string range, SichWeek w, bool held)
    {
        var scored = w.Rows.Where(r => r.Points > 0).ToList();
        if (scored.Count == 0) return null;
        if (!held)
            return $"🏆 Січ щоденних за {range}: у заліку лише {string.Join(" і ", scored.Select(r => r.Nick))} — корона цього разу без господаря. "
                + "Кличте друзів у щоденні, бо так і заржавіє!";
        string Who(int place) => string.Join(", ", scored.Where(r => r.Place == place).Select(r => $"{r.Nick} ({r.Points})"));
        var podium = new List<string>();
        foreach (var (place, medal) in new[] { (2, "🥈"), (3, "🥉") })
            if (Who(place) is { Length: > 0 } x) podium.Add(medal + " " + x);
        var noms = w.Noms.Select(n => n.Icon + " " + string.Join(", ", n.Nicks)).ToList();
        return $"🏆 Січ щоденних за {range} — підсумок! 👑 Гетьман тижня: {Who(1)}."
            + (podium.Count > 0 ? " " + string.Join(" · ", podium) + "." : "")
            + (noms.Count > 0 ? " Номінації: " + string.Join(" · ", noms) + "." : "")
            + " Нова Січ уже почалась — гайда в щоденні!";
    }

    // ------------------------------------------------------------------ Глек у Балачках

    public sealed class LeaderState
    {
        public string Week { get; set; } = "";
        /// <summary>Останній одноосібний лідер тижня (нічия його не скидає: обгін рахуємо від нього).</summary>
        public string Top { get; set; } = "";
        public string Day { get; set; } = "";
        public int Count { get; set; }
        public DateTimeOffset Last { get; set; }
        /// <summary>Тиждень, про чий останній вечір Глек уже сказав.</summary>
        public string Teaser { get; set; } = "";
    }

    void Say(string text)
    {
        try { outbox.Post(new DjSays(text)); }
        catch (Exception ex) { log.LogWarning(ex, "січ: рядок у Балачки не пішов"); }
    }

    /// <summary>Новий лідер тижня (удень, до трьох разів на день) і останній вечір неділі.</summary>
    public void Announce()
    {
        var today = Today();
        var monday = WeekOf(today);
        var w = Calc(monday);
        var st = Read<LeaderState>(LeaderKey) ?? new LeaderState();
        var leaders = w.Table.Where(t => t.Place == 1 && t.S.Points > 0).Select(t => t.S.Nick).ToList();
        var hour = TimeZoneInfo.ConvertTime(clock.UtcNow, Days.Kyiv).Hour;
        var sunday = Shift(monday, 6) == today;
        var changed = false;
        if (st.Week != monday)
        {
            st = new LeaderState { Week = monday, Teaser = st.Teaser };
            changed = true;
        }
        if (leaders.Count == 1 && !string.Equals(leaders[0], st.Top, StringComparison.Ordinal))
        {
            if (st.Day != today) { st.Day = today; st.Count = 0; }
            var lead = w.Table[0].S;
            // перший лідер тижня — не новина: оголошуємо лише обгін
            if (st.Top.Length > 0 && lead.Points >= LeaderMinPoints && st.Count < LeaderDaily && hour >= LeaderFrom && hour < LeaderTo
                && clock.UtcNow - st.Last >= TimeSpan.FromMinutes(30))
            {
                var prev = w.Table.FirstOrDefault(t => string.Equals(t.S.Nick, st.Top, StringComparison.Ordinal)).S;
                Say($"👀 Січ щоденних: новий лідер тижня — {lead.Nick}, {lead.Points} {Pts(lead.Points)}!"
                    + (prev is null ? "" : $" {prev.Nick} позаду з {prev.Points}.")
                    + (sunday ? " До опівночі ще можна все перевернути." : " До неділі ще можна все перевернути."));
                st.Count++;
                st.Last = clock.UtcNow;
            }
            st.Top = leaders[0];
            changed = true;
        }
        if (sunday && hour >= 20 && hour < LeaderTo && st.Teaser != monday && w.Table.Count(t => t.S.Points > 0) >= 2)
        {
            var a = w.Table[0].S;
            var b = w.Table[1].S;
            var prize = Rules().Prizes.FirstOrDefault();
            Say($"⏳ Останній вечір Січі щоденних: попереду {a.Nick} ({a.Points}), за {b.Nick} — {b.Points}. Опівночі — 👑 Гетьман тижня"
                + (prize > 0 ? $" і {prize} 🏺 першому" : "") + ". Хто ще не закрив щоденні — саме час!");
            st.Teaser = monday;
            changed = true;
        }
        if (changed) Write(LeaderKey, st);
    }

    static string Pts(int n) => (n % 100 is >= 11 and <= 14) ? "очок" : (n % 10) switch { 1 => "очко", 2 or 3 or 4 => "очки", _ => "очок" };

    // ------------------------------------------------------------------ фон

    /// <summary>Перший тиждень, який Січ підбиває (з призами й 👑): тиждень, коли вона з'явилась. Старші — лише в історії Ело.</summary>
    public const string FirstWeek = "2026-10-19";
    int _weekTries;

    public async Task Tick(CancellationToken ct = default)
    {
        await CatchUp(ct);
        var prev = Shift(WeekOf(Today()), -7);
        if (string.CompareOrdinal(prev, FirstWeek) >= 0 && Read<SichWeek>(WeekKey(prev)) is null
            && Settle(prev, final: ++_weekTries >= PointsTries) >= 0) _weekTries = 0;
        Announce();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(40), ct); }
        catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await Tick(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { log.LogWarning(ex, "січ: прохід не вдався — спробую за хвилину"); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}
