using System.Globalization;
using Hlechyky.Games.Economy;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Підключення Глек-слова (прохід №3): списки на 4 і 6 літер для гри наввипередки й «серія та розподіл
/// спроб» на картці дограного дня — як у класиці, щоб щоденна звичка трималась на видимій серії.
/// </summary>
public static class WordleSetup
{
    public const string Game = "wordle";

    public static IServiceCollection AddWordle(this IServiceCollection services)
    {
        services.AddSingleton(sp => new WordleLists(Paths.Resolve("data/words"), sp.GetService<ILogger<WordleLists>>()));
        return services;
    }

    public static WebApplication MapWordle(this WebApplication app)
    {
        // списки піднімаються зараз, на старті, а не під замком першого столу на 4 чи 6 літер
        app.Services.GetService<WordleLists>();

        // Моя серія й розподіл спроб у щоденному Глек-слові. Не розв'язані дні в daily_results не пишуться,
        // тож «не вгадав» тут не рахується — лише вгадані, за скільки спроб.
        app.MapGet("/api/games/wordle/stats", (HttpContext c, Daily daily, Db db) =>
        {
            var nick = Auth.Nick(c);
            if (Auth.IsGuestNick(nick)) return Results.Json(WordleStats.Empty);
            var rows = db.With(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT day, attempts FROM daily_results
                    WHERE nick_key = $n AND game = $g AND solved = 1
                    ORDER BY day DESC LIMIT 2000
                    """;
                cmd.Parameters.AddWithValue("$n", EconomyStore.Key(nick));
                cmd.Parameters.AddWithValue("$g", Game);
                using var r = cmd.ExecuteReader();
                var list = new List<(string Day, int Attempts)>();
                while (r.Read()) list.Add((r.GetString(0), r.GetInt32(1)));
                return list;
            });
            return Results.Json(WordleStats.Of(rows, daily.Today()));
        });
        return app;
    }
}

/// <summary>Що бачить картка дограного дня: серія, найдовша серія, скільки днів узято і за скільки спроб.</summary>
public sealed record WordleStats(int Streak, int Best, int Solved, int[] Dist, int Today)
{
    public static readonly WordleStats Empty = new(0, 0, 0, new int[Wordle.MaxTries], 0);

    /// <summary>
    /// Зі списку вгаданих днів. Серія — як у панелі «Щоденний глек»: сьогодні ще не грав — тримається
    /// вчорашнім днем. <paramref name="today"/> — київський день «yyyy-MM-dd».
    /// </summary>
    public static WordleStats Of(IEnumerable<(string Day, int Attempts)> rows, string today)
    {
        var dist = new int[Wordle.MaxTries];
        var days = new HashSet<DateOnly>();
        var todayAttempts = 0;
        foreach (var (day, attempts) in rows)
        {
            if (!DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
            if (!days.Add(d)) continue;
            if (attempts >= 1 && attempts <= Wordle.MaxTries) dist[attempts - 1]++;
            if (day == today) todayAttempts = attempts;
        }
        var t = DateOnly.ParseExact(today, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var cur = days.Contains(t) ? t : t.AddDays(-1);
        var streak = 0;
        while (days.Contains(cur)) { streak++; cur = cur.AddDays(-1); }
        var best = 0;
        foreach (var d in days)
        {
            if (days.Contains(d.AddDays(-1))) continue;   // рахуємо лише від початку кожної серії
            var n = 0;
            for (var x = d; days.Contains(x); x = x.AddDays(1)) n++;
            if (n > best) best = n;
        }
        return new WordleStats(streak, best, days.Count, dist, todayAttempts);
    }
}
