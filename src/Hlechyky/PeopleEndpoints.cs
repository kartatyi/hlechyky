using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Microsoft.Extensions.Options;

namespace Hlechyky;

/// <summary>
/// Люди й статистика: картка будь-кого за ніком, свій гаманець детально, «Хто скільки» з однаковим перемикачем
/// періоду (день · тиждень · місяць · весь час), історія ефіру з «Мої» й «Показати ще», ряд «Часто граємо» в лобі.
/// Періоди — <see cref="Periods"/>: київські дні, однакові на всьому сайті. Кожен маршрут — окремий статичний метод,
/// щоб тести кликали його рівно так, як це робить сервер.
/// </summary>
public static class PeopleEndpoints
{
    /// <summary>Скільки рухів гаманця найбільше віддаємо за раз.</summary>
    public const int LedgerMax = 100;

    /// <summary>
    /// Підпис, яким рушій ефіру позначає в черзі вибори самого Глека (RadioEngine, QueueItem.RequestedBy). У програвання
    /// він не пишеться (там у Глека source autodj без імені), але як колись туди потрапить — це теж Глек, а не людина.
    /// </summary>
    public const string AutoDjNick = "auto-DJ";

    /// <summary>Скільки найбільше рядків у картці людини (топ, останні, лайки).</summary>
    const int PersonRows = 5;

    public static WebApplication MapPeople(this WebApplication app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/people/{nick}", Person);
        api.MapGet("/top", Top);
        api.MapGet("/rating", Rating);
        api.MapGet("/history", History);
        api.MapGet("/games/ledger", Ledger);
        api.MapGet("/games/popular", Popular);
        return app;
    }

    // ---------- картка людини ----------

    /// <summary>
    /// GET /api/people/{nick} — музичне обличчя будь-кого: скільки закидав, що найчастіше й що останнє, що лайкав,
    /// які плейлисти завів. Регістр ніка не рахується, гості («гість Вася») — такі самі люди. Ігри, ачівки й час —
    /// у /api/games/profile?nick=. Нік, якого сайт ніколи не бачив, — 404.
    /// </summary>
    public static IResult Person(string nick, Db db, EconomyStore store, IClock clock)
    {
        // Kestrel розкодовує шлях увесь, крім «/»: нік зі скісною приходить як «%2F»
        nick = nick.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);
        var key = Auth.NickKey(nick);
        if (key.Length == 0) return Unknown();
        var account = db.FindAccount(nick);
        var wallet = store.Wallet(key);
        var radio = db.Person(nick, Periods.Since("week", clock), Periods.Since("month", clock), PersonRows);
        // Слід в іграх — останнім і лише тоді, коли ніде більше нема: і для «не бачили», і для того, як нік пишеться.
        var (inGames, gameNick) = account is null && wallet is null && !radio.Seen ? store.Trace(key) : (true, null);
        if (!inGames) return Unknown();
        return Results.Ok(new
        {
            nick = account?.Nick ?? wallet?.Nick ?? radio.Nick ?? gameNick ?? nick.Trim(),
            account = account is not null,
            music = new
            {
                requests = new { week = radio.Week, month = radio.Month, all = radio.All },
                top = radio.Top,
                recent = radio.Recent,
                likes = new { count = radio.LikeCount, recent = radio.Likes },
                playlists = radio.Playlists,
            },
        });
    }

    static IResult Unknown() => Results.NotFound(new { message = "Такого тут не бачили" });

    // ---------- «Хто скільки»: закидальники й треки ----------

    /// <summary>
    /// GET /api/top?period=day|week|month|all — хто скільки закинув: люди окремо, автодиджей окремо (<c>dj</c>, null —
    /// нічого не ставив). Старий ?days= (7 днів назад від зараз) лишається для старих клієнтів; period — головніший.
    /// </summary>
    public static object Top(string? period, int? days, Db db, IClock clock, IOptionsMonitor<SiteOptions> site)
    {
        var known = Periods.Known(period);
        var since = known ? Periods.Since(period, clock) : clock.UtcNow.AddDays(-Math.Clamp(days ?? 7, 1, 365));
        var dj = site.CurrentValue.DjName;
        var (people, djCount) = db.TopRequesters(since, [dj, AutoDjNick]);
        return new
        {
            period = known ? period : null,
            requesters = people,
            dj = djCount > 0 ? new { nick = dj, count = djCount } : null,
        };
    }

    /// <summary>GET /api/rating?period=&amp;sort= — рейтинг треків. Старий ?days= лишається, period — головніший.</summary>
    public static object Rating(HttpContext c, string? period, int? days, string? sort, Db db, TrackCache cache, IClock clock) =>
        RatingOf(c, period, days, sort, db, clock, () => (cache.Usage(), cache.LimitBytes));

    /// <summary>
    /// Те саме, але місце на диску приходить ззовні: <paramref name="disk"/> питаємо лише для адміна — іншим
    /// «скільки займає кеш» ні до чого, а обхід теки з тисячами файлів не безкоштовний.
    /// </summary>
    public static object RatingOf(HttpContext c, string? period, int? days, string? sort, Db db, IClock clock,
        Func<((long Bytes, int Files) Usage, long LimitBytes)> disk)
    {
        var since = Periods.Known(period) ? Periods.Since(period, clock) : clock.UtcNow.AddDays(-Math.Clamp(days ?? 7, 1, 3650));
        object? cache = null;
        if (Auth.IsAdmin(c))
        {
            var ((bytes, files), limitBytes) = disk();
            cache = new { bytes, files, limitBytes };
        }
        return new { tracks = db.TrackRatings(since, sort ?? "plays", 100), cache };
    }

    /// <summary>
    /// GET /api/history?n=80&amp;by=&lt;нік&gt;&amp;before=&lt;id&gt; — що грало, свіже згори. by — лише закинуте цим ніком
    /// (будь-яким регістром), before — лише старіше за програвання з цим id («Показати ще»). Без них — як завжди.
    /// </summary>
    public static List<HistoryEntry> History(int? n, string? by, long? before, Db db) =>
        db.History(Math.Clamp(n ?? 50, 1, 500), string.IsNullOrWhiteSpace(by) ? null : by, before);

    // ---------- свій гаманець ----------

    /// <summary>
    /// GET /api/games/ledger?limit=30 — свій гаманець детально: баланс, останні рухи (до <see cref="LedgerMax"/>) і місяць
    /// за групами — звідки прийшли й куди пішли. Нік — лише той, під яким людина зайшла: чужого гаманця тут не видно.
    /// </summary>
    public static object Ledger(HttpContext c, int? limit, EconomyStore store, Economy economy, IClock clock)
    {
        var nick = Auth.Nick(c);
        var key = EconomyStore.Key(nick);
        var w = economy.Wallet(nick);
        var month = store.LedgerSums(key, Periods.Since("month", clock))
            .GroupBy(s => Cat(s.Reason))
            .Select(g => new { cat = g.Key, title = CatTitle(g.Key), earned = g.Sum(s => s.Earned), spent = g.Sum(s => s.Spent) })
            .OrderByDescending(g => g.earned + g.spent).ThenBy(g => g.cat, StringComparer.Ordinal)
            .ToList();
        return new
        {
            balance = w.Balance, earned = w.Earned, spent = w.Spent,
            items = store.LedgerOf(key, Math.Clamp(limit ?? 30, 1, LedgerMax))
                .Select(i => new { delta = i.Delta, reason = i.Reason, text = economy.Reason(i.Reason), at = i.At }),
            month,
        };
    }

    /// <summary>
    /// Група причини для «звідки прийшли, куди пішли» — голова коду («win:chess» → «win»); ставку, виграш банку й
    /// повернення ставки зводимо в одну «stake», бан, викуп і повернення за бан — в одну «ban».
    /// </summary>
    public static string Cat(string reason)
    {
        var i = reason.IndexOf(':');
        return (i < 0 ? reason : reason[..i]) switch
        {
            "stake-win" or "stake-refund" => "stake",
            "unban" or "ban-refund" => "ban",
            var head => head,
        };
    }

    /// <summary>Коротка назва групи; невідома — сам код: нова причина хай краще видна кодом, ніж зникає з гаманця.</summary>
    public static string CatTitle(string cat) => cat switch
    {
        "listen" => "слухання радіо",
        "win" => "перемоги",
        "draw" => "нічиї",
        "play" => "участь у партіях",
        "solo" => "соло-ігри",
        "points" => "очки в іграх",
        "daily" => "щоденний глек",
        "ach" => "ачівки",
        "stake" => "ставки",
        "clicker" => "обмін глеків",
        "ad" => "реклама",
        "award" => "нагороди в іграх",
        "ban" => "бан-лист",
        "shop" => "Лавка",
        "gift" => "подарунки",
        "buy" => "куплено за гривні",
        "buy-gift" => "куплено в подарунок",
        _ => cat,
    };

    // ---------- лобі ----------

    /// <summary>
    /// GET /api/games/popular?days=30 — ряд «Часто граємо»: ігри від тієї, де за останні <c>days</c> київських днів
    /// (сьогодні теж) дограли найбільше різних столів; players — скільки різних людей за ними сиділо.
    /// </summary>
    public static object Popular(int? days, EconomyStore store, IClock clock)
    {
        var d = Math.Clamp(days ?? 30, 1, 365);
        return new { days = d, games = store.Popular(Periods.SinceDaysBack(clock, d - 1)) };
    }
}
