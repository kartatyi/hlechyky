using System.Globalization;

namespace Hlechyky.Games.Economy;

/// <summary>
/// Періоди статистики — однакові на всьому сайті: <c>day</c> — сьогодні, <c>week</c> — сьогодні й шість днів перед ним,
/// <c>month</c> — сьогодні й двадцять дев'ять перед ним, <c>all</c> — без межі. Дні київські (<see cref="Days"/>), а не
/// «24 години назад»: таблиця «за сьогодні» о пів на першу ночі має бути порожньою, а не вчорашнім вечором.
/// Таблиці ігор, «Час», топ закидальників, рейтинг треків і профіль людини беруть межі звідси, щоб «тиждень»
/// в одній вкладці не був іншим тижнем, ніж у сусідній.
/// </summary>
public static class Periods
{
    /// <summary>Чи це один із чотирьох періодів. Порожнє й невідоме — ні: тоді кожен ендпоінт робить, як робив досі.</summary>
    public static bool Known(string? period) => period is "day" or "week" or "month" or "all";

    /// <summary>Перший київський день періоду ("2026-09-20"); null — за весь час (і для невідомого періоду).</summary>
    public static string? FirstDay(string? period, IClock clock) => period switch
    {
        "day" => Days.Today(clock),
        "week" => DaysBack(clock, 6),
        "month" => DaysBack(clock, 29),
        _ => null,
    };

    /// <summary>Київська північ першого дня періоду в UTC; за весь час — <see cref="DateTimeOffset.MinValue"/>.</summary>
    public static DateTimeOffset Since(string? period, IClock clock) =>
        FirstDay(period, clock) is { } day ? Daily.StartOfDayUtc(day) : DateTimeOffset.MinValue;

    /// <summary>Київська північ дня, що був <paramref name="back"/> днів тому (0 — сьогоднішня), в UTC.</summary>
    public static DateTimeOffset SinceDaysBack(IClock clock, int back) => Daily.StartOfDayUtc(DaysBack(clock, back));

    /// <summary>Київський день <paramref name="back"/> днів тому ("2026-09-20").</summary>
    public static string DaysBack(IClock clock, int back) =>
        DateOnly.ParseExact(Days.Today(clock), "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(-back)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
