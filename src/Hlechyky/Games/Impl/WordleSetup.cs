using System.Globalization;
using System.Text.Json;
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
        // Програні дні до пакета 4 лежать лише в game_state — раз на старті дописуємо їх у daily_results (фоном).
        if (app.Services.GetService<Db>() is { } bdb)
            _ = Task.Run(() => BackfillMisses(bdb, app.Services.GetService<Words>(), app.Services.GetService<ILogger<WordleLists>>()));

        // Моя серія й розподіл спроб у щоденному Глек-слові. З пакета 4 програний день теж пишеться в
        // daily_results (solved = 0, Ctx.DailyMiss) — це стовпчик «✗»; серія тримається лише на вгаданих.
        app.MapGet("/api/games/wordle/stats", (HttpContext c, Daily daily, Db db) =>
        {
            var nick = Auth.Nick(c);
            // гостям панель «Щоденний глек» теж рахує серію — тож і тут; лише безіменний «гість» спільний на всіх
            if (Auth.NickKey(nick) == Auth.Guest) return Results.Json(WordleStats.Empty);
            var rows = db.With(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT day, attempts, solved FROM daily_results
                    WHERE nick_key = $n AND game = $g
                    ORDER BY day DESC LIMIT 2000
                    """;
                cmd.Parameters.AddWithValue("$n", EconomyStore.Key(nick));
                cmd.Parameters.AddWithValue("$g", Game);
                using var r = cmd.ExecuteReader();
                var list = new List<(string Day, int Attempts)>();
                var missed = new List<string>();
                while (r.Read())
                    if (r.GetInt32(2) == 1) list.Add((r.GetString(0), r.GetInt32(1)));
                    else missed.Add(r.GetString(0));
                return (list, missed);
            });
            return Results.Json(WordleStats.Of(rows.list, daily.Today(), rows.missed));
        });
        return app;
    }

    /// <summary>
    /// Одноразове (й ідемпотентне) дописування старих програних днів: до пакета 4 шість промахів лишались лише в
    /// <c>game_state</c> (<c>daily:wordle:день:нік</c>), а в <c>daily_results</c> не писалось нічого. INSERT OR IGNORE:
    /// уже записаний день (розв'язок чи промах) не чіпаємо. Нік — останній відомий з daily_results, інакше сам ключ.
    /// </summary>
    public static int BackfillMisses(Db db, Words? words, ILogger? log = null)
    {
        try
        {
            var added = db.With(c =>
            {
                var states = new List<(string Day, string Key, string Json)>();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT key, json FROM game_state WHERE key LIKE 'daily:wordle:%'";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var parts = r.GetString(0).Split(':', 4);
                        if (parts.Length == 4 && parts[2].Length == 10) states.Add((parts[2], parts[3], r.GetString(1)));
                    }
                }
                var n = 0;
                foreach (var (day, key, json) in states)
                {
                    if (!LostDay(json, day, words)) continue;
                    using var nk = c.CreateCommand();
                    nk.CommandText = "SELECT nick FROM daily_results WHERE nick_key = $n ORDER BY day DESC LIMIT 1";
                    nk.Parameters.AddWithValue("$n", key);
                    var nick = nk.ExecuteScalar() as string ?? key;
                    using var ins = c.CreateCommand();
                    ins.CommandText = """
                        INSERT OR IGNORE INTO daily_results(day, game, nick_key, nick, solved, attempts, ms, created_at)
                        VALUES($d, $g, $n, $nk, 0, $a, 0, $t)
                        """;
                    ins.Parameters.AddWithValue("$d", day);
                    ins.Parameters.AddWithValue("$g", Game);
                    ins.Parameters.AddWithValue("$n", key);
                    ins.Parameters.AddWithValue("$nk", nick);
                    ins.Parameters.AddWithValue("$a", Wordle.MaxTries);
                    ins.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("o"));
                    n += ins.ExecuteNonQuery();
                }
                return n;
            });
            if (added > 0) log?.LogInformation("Глек-слово: дописано {N} старих програних днів у daily_results", added);
            return added;
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Глек-слово: старі програні дні не дописались");
            return 0;
        }
    }

    /// <summary>
    /// Стан дня з game_state — це програш: шість спроб, і слова того дня серед них нема (словника нема — віримо
    /// прапорцю <c>solved</c>).
    /// </summary>
    internal static bool LostDay(string json, string day, Words? words)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("day", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() != day) return false;
            if (!root.TryGetProperty("guesses", out var g) || g.ValueKind != JsonValueKind.Array) return false;
            var list = g.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => Words.Normalize(x.GetString() ?? "")).ToList();
            if (list.Count < Wordle.MaxTries) return false;
            var answer = words?.Daily5ForDay(day) ?? "";
            if (answer.Length == Wordle.Len) return !list.Take(Wordle.MaxTries).Contains(answer);
            return !(root.TryGetProperty("solved", out var s) && s.ValueKind == JsonValueKind.True);
        }
        catch (JsonException) { return false; }
    }
}

/// <summary>
/// Що бачить картка дограного дня: серія, найдовша серія, скільки днів узято і за скільки спроб, а з пакета 4 —
/// скільки днів не вийшло (<c>Miss</c>, стовпчик «✗»).
/// </summary>
public sealed record WordleStats(int Streak, int Best, int Solved, int[] Dist, int Today, int Miss = 0, bool MissToday = false)
{
    public static readonly WordleStats Empty = new(0, 0, 0, new int[Wordle.MaxTries], 0);

    /// <summary>
    /// Зі списку вгаданих днів. Серія — як у панелі «Щоденний глек»: сьогодні ще не грав — тримається
    /// вчорашнім днем. <paramref name="today"/> — київський день «yyyy-MM-dd».
    /// </summary>
    public static WordleStats Of(IEnumerable<(string Day, int Attempts)> rows, string today, IEnumerable<string>? missed = null)
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
        // програні дні — лише ті, що не розв'язані: розв'язок того самого дня промах перекриває
        var miss = (missed ?? []).Where(m => DateOnly.TryParseExact(m, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var md) && !days.Contains(md)).Distinct(StringComparer.Ordinal).Count();
        var missToday = todayAttempts == 0 && (missed ?? []).Contains(today);
        return new WordleStats(streak, best, days.Count, dist, todayAttempts, miss, missToday);
    }
}
