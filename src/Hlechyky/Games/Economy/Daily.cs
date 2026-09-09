using System.Globalization;

namespace Hlechyky.Games.Economy;

/// <summary>Мій результат у головоломці дня.</summary>
public sealed record DailyMe(bool Solved, int Attempts, int Ms);

/// <summary>Рядок топу дня.</summary>
public sealed record DailyTopRow(string Nick, int Attempts, int Ms);

/// <summary>Одна головоломка «Щоденного глека» очима конкретної людини.</summary>
public sealed record DailyPuzzle(string Game, string Title, DailyMe? Me, int Streak, int SolvedCount,
    IReadOnlyList<DailyTopRow> Top);

/// <summary>Те, що віддає GET /api/games/daily.</summary>
public sealed record DailyStatus(string Day, int No, DateTimeOffset NextMidnight, IReadOnlyList<DailyPuzzle> Puzzles);

/// <summary>
/// «Щоденний глек»: тонкий сервіс над <see cref="Days"/> з контракту. День, номер дня і сід — там;
/// тут — хто що розв'язав, серії й топ дня (specs/daily.md).
/// </summary>
public sealed class Daily(EconomyStore store, GameNames names, IClock clock)
{
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

    /// <summary>Панель «Щоденний глек» для одного ніка. Уся вибірка — двома походами в базу, не по чотири на гру.</summary>
    public DailyStatus Status(string nick)
    {
        var day = Today();
        var games = Puzzles;
        var streaks = Streaks(nick);
        var puzzles = store.DailyPanel(day, Economy.Key(nick), games, TopRows).Select(p => new DailyPuzzle(
            p.Game,
            names.Title(p.Game),
            p.Mine is null ? null : new DailyMe(p.Mine.Solved, p.Mine.Attempts, p.Mine.Ms),
            streaks.GetValueOrDefault(p.Game),
            p.SolvedCount,
            p.Top.Select(r => new DailyTopRow(r.Nick, r.Attempts, r.Ms)).ToList())).ToList();
        return new DailyStatus(day, Days.Number(day), Days.NextMidnight(clock.UtcNow), puzzles);
    }
}
