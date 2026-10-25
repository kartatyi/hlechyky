using System.Globalization;

namespace Hlechyky.Games.Economy;

/// <summary>Мій результат у головоломці дня.</summary>
public sealed record DailyMe(bool Solved, int Attempts, int Ms, long? Points = null);

/// <summary>Рядок топу дня.</summary>
public sealed record DailyTopRow(string Nick, int Attempts, int Ms);

/// <summary>Одна головоломка «Щоденного глека» очима конкретної людини.</summary>
public sealed record DailyPuzzle(string Game, string Title, DailyMe? Me, int Streak, int SolvedCount,
    IReadOnlyList<DailyTopRow> Top);

/// <summary>Те, що віддає GET /api/games/daily.</summary>
public sealed record DailyStatus(string Day, int No, DateTimeOffset NextMidnight, IReadOnlyList<DailyPuzzle> Puzzles,
    DailyPay? Pay = null, DailyMoney? Money = null, SichBrief? Sich = null);

/// <summary>Одна нічна виплата: гра, що за неї (best — «за місце», top — приз топу), скільки.</summary>
public sealed record DailyNightItem(string Game, string Title, string Kind, int Amount);

/// <summary>
/// Черепки «Щоденного глека» для смужки «Сьогодні»: скільки вже прийшло сьогодні (усього й по головоломках), скільки ще
/// можна взяти до кінця дня (оцінка: решта головоломок із серією + «повний глек») і що прийшло вночі за вчора.
/// </summary>
public sealed record DailyMoney(int Today, IReadOnlyDictionary<string, int> ByGame, int Left, string NightDay, int Night,
    IReadOnlyList<DailyNightItem> NightItems);

/// <summary>
/// Скільки платить «Щоденний глек» (уже × Boost) — для смужки «Сьогодні»: база, серія, «повний глек» (скільки треба й
/// скільки вже), нічні «за місце» й призи топу.
/// </summary>
public sealed record DailyPay(int Reward, int StreakPct, int StreakMaxPct, int FullNeed, int FullBonus, int Solved,
    int BestMax, IReadOnlyList<int> Top, int TopMin)
{
    public static DailyPay Of(EconomyOptions o, int puzzles, int solved) => new(
        o.Earn(o.DailyReward), Math.Max(0, o.DailyStreakPct), Math.Max(0, o.DailyStreakMaxPct),
        o.DailyFullBonus > 0 ? Math.Min(Math.Max(0, o.DailyFullNeed), puzzles) : 0, o.Earn(Math.Max(0, o.DailyFullBonus)), solved,
        o.Earn(Math.Max(0, o.DailyBestMax)), [.. (o.DailyTopPrizes ?? []).Select(p => o.Earn(Math.Max(0, p)))], o.DailyTopMinSolvers);
}

/// <summary>
/// «Щоденний глек»: тонкий сервіс над <see cref="Days"/> з контракту. День, номер дня і сід — там;
/// тут — хто що розв'язав, серії й топ дня (specs/daily.md).
/// </summary>
public sealed class Daily(EconomyStore store, GameNames names, IClock clock, IEnumerable<IDailyPoints>? points = null)
{
    readonly IDailyPoints[] _points = points?.ToArray() ?? [];

    /// <summary>Чи міряється головоломка очками (більше — краще), а не спробами й часом.</summary>
    public bool HasPoints(string game) => _points.Any(p => p.Game == game);

    public long? PointsOf(string game, string day, string nick)
    {
        foreach (var p in _points)
            if (p.Game == game)
                try { return p.Points(day, nick); }
                catch { return null; }
        return null;
    }

    /// <summary>Скільки днів назад дивимось, коли рахуємо серію.</summary>
    const int StreakWindow = 400;

    /// <summary>Скільки рядків у топі дня однієї головоломки.</summary>
    const int TopRows = 10;

    public string Today() => Days.Today(clock);

    /// <summary>Id ігор, що входять у щоденне (усі, хто позначився <see cref="IDailyGame"/>).</summary>
    public IReadOnlyList<string> Puzzles => names.Daily;

    /// <summary>Північ київського дня в UTC — межа для «за сьогодні» в таблицях.</summary>
    public static DateTimeOffset StartOfDayUtc(string day)
    {
        var d = DateTime.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(d, Days.Kyiv), TimeSpan.Zero);
    }

    static string Prev(string day) =>
        DateOnly.ParseExact(day, "yyyy-MM-dd").AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Записати результат дня. Гірший результат кращий не псує; повторний виклик того самого дня —
    /// не новий рядок, а оновлення.
    /// </summary>
    public DailyRow Record(string game, string nick, bool solved, int attempts, int ms, string? day = null)
    {
        var d = day ?? Today();
        var row = new DailyRow(d, game, Economy.Key(nick), nick, solved, attempts, ms);
        store.SaveDaily(row, clock.UtcNow);
        return store.Daily(d, game, row.NickKey) ?? row;
    }

    /// <summary>
    /// Серія: скільки днів поспіль до сьогодні розв'язано. Сьогодні ще не грав — серія тримається
    /// вчорашнім днем (інакше вона «падала» б щоранку до першої партії).
    /// </summary>
    public int Streak(string nick, string game)
    {
        var days = store.SolvedDays(Economy.Key(nick), game, StreakWindow).ToHashSet(StringComparer.Ordinal);
        return StreakOf(days, Today());
    }

    /// <summary>
    /// Серії в усіх головоломках одразу — одним запитом. Панель і профіль показують їх усі, а окремий
    /// запит на кожну гру перетворював би HTTP-запит на десяток з'єднань до бази.
    /// </summary>
    public Dictionary<string, int> Streaks(string nick)
    {
        var byGame = store.SolvedDaysAll(Economy.Key(nick), StreakWindow);
        var today = Today();
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var game in Puzzles) result[game] = StreakOf(byGame.GetValueOrDefault(game), today);
        foreach (var (game, days) in byGame) result[game] = StreakOf(days, today);
        return result;
    }

    static int StreakOf(HashSet<string>? days, string today)
    {
        if (days is null || days.Count == 0) return 0;
        var day = today;
        // сьогодні ще не грав — серія тримається вчорашнім днем
        if (!days.Contains(day)) day = Prev(day);
        var n = 0;
        while (days.Contains(day)) { n++; day = Prev(day); }
        return n;
    }

    public DailyRow? MyResult(string nick, string game, string? day = null) =>
        store.Daily(day ?? Today(), game, Economy.Key(nick));

    public List<DailyTopRow> Top(string game, string? day = null, int n = 10) =>
        store.DailyTop(day ?? Today(), game, n).Select(r => new DailyTopRow(r.Nick, r.Attempts, r.Ms)).ToList();

    /// <summary>
    /// Черепки щоденних для ніка (лише людям — гостям надбавок нема, і рахувати їм «ще можна» було б неправдою; null).
    /// </summary>
    public DailyMoney? Money(string nick, DailyStatus status, EconomyOptions o)
    {
        var key = Economy.Key(nick);
        if (string.IsNullOrWhiteSpace(nick) || !store.IsPerson(key)) return null;
        var day = status.Day;
        var from = StartOfDayUtc(day);
        var today = store.LedgerByReason(key, from, from.AddDays(2), "daily:", "daily-streak:", "daily-full");
        var byGame = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (reason, n) in today)
        {
            var i = reason.IndexOf(':');
            if (i < 0) continue;
            var game = reason[(i + 1)..];
            byGame[game] = byGame.GetValueOrDefault(game) + n;
        }
        var reward = o.Earn(o.DailyReward);
        var left = status.Puzzles.Where(p => p.Me?.Solved != true)
            .Sum(p => reward + (o.DailyStreakPct > 0 && o.DailyStreakMaxPct > 0 ? o.DailyStreakBonus(reward, p.Streak + 1) : 0));
        var need = o.DailyFullBonus > 0 ? Math.Min(Math.Max(0, o.DailyFullNeed), status.Puzzles.Count) : 0;
        if (need > 0 && !today.ContainsKey("daily-full")) left += o.Earn(o.DailyFullBonus);
        var yday = Prev(day);
        // нічні виплати за вчора приходять після опівночі — тобто вже сьогоднішньою датою
        var night = store.LedgerByReason(key, from, from.AddDays(2), "daily-best:", "daily-top:")
            .Select(x => (Reason: x.Key, Amount: x.Value)).ToList();
        var items = night.Select(x =>
        {
            var i = x.Reason.IndexOf(':');
            var game = x.Reason[(i + 1)..];
            return new DailyNightItem(game, names.Title(game), x.Reason[..i] == "daily-top" ? "top" : "best", x.Amount);
        }).Where(x => x.Amount > 0).OrderByDescending(x => x.Amount).ToList();
        return new DailyMoney(today.Values.Sum(), byGame, left, yday, items.Sum(x => x.Amount), items);
    }

    /// <summary>Панель «Щоденний глек» для одного ніка. Уся вибірка — двома походами в базу, не по чотири на гру.</summary>
    public DailyStatus Status(string nick)
    {
        var day = Today();
        var games = Puzzles;
        var streaks = Streaks(nick);
        var puzzles = store.DailyPanel(day, Economy.Key(nick), games, TopRows).Select(p => new DailyPuzzle(
            p.Game,
            names.Title(p.Game),
            p.Mine is null ? null : new DailyMe(p.Mine.Solved, p.Mine.Attempts, p.Mine.Ms, PointsOf(p.Game, day, nick)),
            streaks.GetValueOrDefault(p.Game),
            p.SolvedCount,
            p.Top.Select(r => new DailyTopRow(r.Nick, r.Attempts, r.Ms)).ToList())).ToList();
        return new DailyStatus(day, Days.Number(day), Days.NextMidnight(clock.UtcNow), puzzles);
    }
}
