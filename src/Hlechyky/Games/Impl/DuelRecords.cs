namespace Hlechyky.Games.Impl;

/// <summary>
/// «Найшвидша рука» (п. 64): особистий рекорд реакції кожного й рекорд тижня — спільні для Дуелі, Перестрілки
/// й Турніру стрільців (реакція є реакція). Самі результати давно лежать у таблиці соло-результатів (<c>Ctx.Score</c>),
/// тож окремо нічого не пишемо: на старті сервера один раз фоном читаємо звідти, а далі гра доповнює словник у пам'яті.
/// Під замком кімнати — лише цей словник, ні бази, ні очікування.
/// </summary>
public sealed class DuelRecords
{
    /// <summary>Ігри, чиї реакції йдуть у спільний рекорд.</summary>
    public static readonly string[] Games = ["duel", "shootout", "duelcup"];

    /// <summary>Рекорд тижня: хто й скільки.</summary>
    public sealed record Best(string Nick, long Ms);

    readonly object _lock = new();
    readonly Dictionary<string, Best> _pb = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Найкраще «від цього дня і далі»: (київський день, нік, мс). Тиждень — останні сім днів.</summary>
    readonly List<(string Day, Best B)> _days = [];
    volatile bool _loaded;

    /// <param name="seed">Звідки взяти старі результати: (гра, від коли, скільки) → (нік, найкращі мс). null — нізвідки.</param>
    /// <param name="inline">Тести: читати одразу, а не фоном.</param>
    public DuelRecords(Func<string, DateTimeOffset, int, IReadOnlyList<(string Nick, double Best)>>? seed, IClock clock, bool inline = false)
    {
        if (seed is null) { _loaded = true; return; }
        if (inline) Seed(seed, clock);
        else ThreadPool.QueueUserWorkItem(_ => Seed(seed, clock));
    }

    public bool Loaded => _loaded;

    void Seed(Func<string, DateTimeOffset, int, IReadOnlyList<(string Nick, double Best)>> seed, IClock clock)
    {
        try
        {
            foreach (var g in Games)
            {
                foreach (var (nick, best) in seed(g, DateTimeOffset.MinValue, 5000))
                    Put(nick, (long)best);
                // «від дня back і далі»: найкраще в кожному такому вікні — і є рекорд тижня, поки цей день у тижні
                for (var back = 0; back <= 6; back++)
                {
                    var rows = seed(g, Hlechyky.Games.Economy.Periods.SinceDaysBack(clock, back), 1);
                    if (rows.Count > 0) PutDay(Hlechyky.Games.Economy.Periods.DaysBack(clock, back), rows[0].Nick, (long)rows[0].Best);
                }
            }
        }
        catch (Exception) { /* база впала — рекорди почнуться з чистого аркуша */ }
        finally { _loaded = true; }
    }

    void Put(string nick, long ms)
    {
        lock (_lock)
            if (!_pb.TryGetValue(nick, out var old) || ms < old.Ms) _pb[nick] = new Best(nick, ms);
    }

    void PutDay(string day, string nick, long ms)
    {
        lock (_lock)
        {
            var i = _days.FindIndex(d => d.Day == day);
            if (i < 0) _days.Add((day, new Best(nick, ms)));
            else if (ms < _days[i].B.Ms) _days[i] = (day, new Best(nick, ms));
        }
    }

    /// <summary>
    /// Новий влучний постріл. 'week' — побив рекорд тижня, 'pb' — свій рекорд, null — ні те, ні те. Перший у житті
    /// постріл рекордом не зветься (нема що бити), і поки старі результати не дочитані — теж мовчимо.
    /// </summary>
    public string? Post(string nick, long ms, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(nick) || ms <= 0) return null;
        var day = Days.Of(at);
        var from = WeekStart(day);
        lock (_lock)
        {
            var week = WeekIn(from);
            var had = _pb.TryGetValue(nick, out var mine);
            string? flag = null;
            if (_loaded)
            {
                if (week is not null && ms < week.Ms) flag = "week";
                else if (had && ms < mine!.Ms) flag = "pb";
            }
            if (!had || ms < mine!.Ms) _pb[nick] = new Best(nick, ms);
            var i = _days.FindIndex(d => d.Day == day);
            if (i < 0) _days.Add((day, new Best(nick, ms)));
            else if (ms < _days[i].B.Ms) _days[i] = (day, new Best(nick, ms));
            _days.RemoveAll(d => string.CompareOrdinal(d.Day, from) < 0);
            return flag;
        }
    }

    /// <summary>Рекорд тижня на мить <paramref name="at"/>; null — тиждень тихий.</summary>
    public Best? Week(DateTimeOffset at)
    {
        var from = WeekStart(Days.Of(at));
        lock (_lock) return WeekIn(from);
    }

    /// <summary>Особистий рекорд; null — ще не стріляв (або стріляв до того, як ми почали рахувати).</summary>
    public long? Personal(string? nick)
    {
        if (string.IsNullOrWhiteSpace(nick)) return null;
        lock (_lock) return _pb.TryGetValue(nick, out var b) ? b.Ms : null;
    }

    Best? WeekIn(string from)
    {
        Best? best = null;
        foreach (var (day, b) in _days)
            if (string.CompareOrdinal(day, from) >= 0 && (best is null || b.Ms < best.Ms)) best = b;
        return best;
    }

    static string WeekStart(string day) =>
        DateOnly.ParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).AddDays(-6)
            .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Для виду: <c>{ week: { n, ms } | null, pb: [мс кожного з місць] }</c>. <paramref name="nicks"/> — ніки на місцях.
    /// </summary>
    public object View(DateTimeOffset at, IReadOnlyList<string?> nicks)
    {
        var w = Week(at);
        var pb = new long?[nicks.Count];
        for (var i = 0; i < nicks.Count; i++) pb[i] = Personal(nicks[i]);
        return new { week = w is null ? null : new { n = w.Nick, ms = w.Ms }, pb };
    }
}

/// <summary>Підключення Дуелі: спільні рекорди реакції. Кличеться одним рядком із <see cref="GamesSetup"/>.</summary>
public static class DuelSetup
{
    public static IServiceCollection AddDuel(IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var store = sp.GetService<Hlechyky.Games.Economy.EconomyStore>();
            Func<string, DateTimeOffset, int, IReadOnlyList<(string, double)>>? seed = store is null ? null
                : (g, since, n) => [.. store.TopSolo(g, since, false, n).Select(r => (r.Nick, r.Best))];
            return new DuelRecords(seed, sp.GetRequiredService<IClock>());
        });
        return services;
    }
}
