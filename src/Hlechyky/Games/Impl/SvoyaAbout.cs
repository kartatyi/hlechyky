using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «👥 Про нас» (прохід №3, п. 18): автотема з життя сайту — хто цього тижня закинув на радіо найбільше пісень, у яку
/// гру грали найчастіше, хто найбільше перемагав, заробив черепків тощо. Таке можуть лише Глечики, і воно щоразу свіже.
/// <para>
/// Правила: лише факти, які легко перевірити (у коментарі до запитання — число), і нічого образливого (ні «хто
/// найбільше програв», ні «чий трек скіпали»). Нічия на першому місці чи замало даних — запитання не буде.
/// Запитань менше за <see cref="MinQuestions"/> — тема недоступна, і в лобі пише чому.
/// </para>
/// <para>
/// База читається лише тут і лише поза замком кімнати: список пакетів (HTTP) збирає знімок сам, а гра бере вже
/// готовий <see cref="Current"/> з пам'яті й просить <see cref="Refresh"/> фоном. Дані Гончарного кола — лише
/// рядки <c>game_results</c> (код і збереження кола не чіпаємо).
/// </para>
/// </summary>
public sealed partial class SvoyaAbout(Db? db, IClock clock, Func<string, string?>? gameTitle = null, ILogger<SvoyaAbout>? log = null)
{
    public const string Id = "x_about";
    public const string Title = "👥 Про нас";
    public const int MinQuestions = 6;
    /// <summary>Скільки знімок вважається свіжим: потім перезбирається (фоном — для гри, одразу — для списку).</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);

    public sealed record Snapshot(SvoyaPack? Pack, string Reason, int Questions, DateTimeOffset At);

    /// <summary>Рядок рейтингу: хто/що, скільки, і додаток (виконавець для пісні).</summary>
    public sealed record Rank(string Name, int N, string? Extra = null);

    /// <summary>Сирі факти з бази — окремо від складання запитань, щоб те перевірялось без бази.</summary>
    public sealed class Facts
    {
        public List<Rank> WeekRequests { get; init; } = [];
        public List<Rank> WeekLikes { get; init; } = [];
        public List<Rank> TopTracks { get; init; } = [];
        public List<Rank> LikedTracks { get; init; } = [];
        public List<Rank> WeekArtists { get; init; } = [];
        public List<Rank> WeekGames { get; init; } = [];
        public List<Rank> WeekWins { get; init; } = [];
        /// <summary>Найпопулярніші ігри на кількох (id гри) — і хто в них найчастіше перемагав.</summary>
        public List<(string Game, List<Rank> Winners)> GameWinners { get; init; } = [];
        public List<Rank> AllGames { get; init; } = [];
        public List<Rank> Earned { get; init; } = [];
        public List<Rank> Achievements { get; init; } = [];
        public List<Rank> WeekClicker { get; init; } = [];
        public List<Rank> Daily { get; init; } = [];
    }

    readonly ILogger _log = (ILogger?)log ?? NullLogger.Instance;
    readonly Lock _build = new();
    volatile Snapshot? _snap;
    int _busy;

    /// <summary>Останній зібраний знімок (null — ще не збирали). Для гри: лише пам'ять, без бази.</summary>
    public Snapshot? Current => _snap;

    /// <summary>Знімок для списку пакетів: несвіжий чи нема — зібрати зараз (це HTTP-запит, не замок кімнати).</summary>
    public Snapshot Get()
    {
        if (_snap is { } s && clock.UtcNow - s.At < Fresh) return s;
        lock (_build)
        {
            if (_snap is { } again && clock.UtcNow - again.At < Fresh) return again;
            return _snap = BuildNow();
        }
    }

    /// <summary>Перезібрати фоном, якщо знімок несвіжий. Нічого не чекає.</summary>
    public void Refresh()
    {
        if (_snap is { } s && clock.UtcNow - s.At < Fresh) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        Task.Run(() =>
        {
            try { Get(); }
            finally { Volatile.Write(ref _busy, 0); }
        });
    }

    Snapshot BuildNow()
    {
        var now = clock.UtcNow;
        try
        {
            var facts = Read(now);
            var (pack, n, reason) = Compose(facts, gameTitle ?? (_ => null), now);
            return new Snapshot(pack, reason, n, now);
        }
        catch (Exception e)
        {
            _log.LogWarning("своя гра: тема «Про нас» не зібралась: {Err}", e.Message);
            return new Snapshot(null, "Тема «Про нас» зараз не збирається — спробуй згодом", 0, now);
        }
    }

    // =========================================================================================
    // Запитання з фактів
    // =========================================================================================

    /// <summary>Скласти пакет «Про нас» з фактів. Мало запитань — пакета нема, і причина для лобі.</summary>
    public static (SvoyaPack? Pack, int Questions, string Reason) Compose(Facts f, Func<string, string?> gameTitle, DateTimeOffset now)
    {
        var radio = new List<SvoyaQuestion>();
        if (Leader(f.WeekRequests, 3) is { } rq)
            radio.Add(Who("Хто за останній тиждень закинув на радіо найбільше пісень?", rq, Count(rq.N, "пісня", "пісні", "пісень") + " за тиждень"));
        if (Leader(f.WeekLikes, 3) is { } lk)
            radio.Add(Who("Хто за останній тиждень поставив найбільше сердечок пісням на радіо?", lk, Count(lk.N, "сердечко", "сердечка", "сердечок") + " за тиждень"));
        if (Leader(f.TopTracks.Where(Clean).ToList(), 3) is { } tt)
            radio.Add(What("Яку пісню друзі найчастіше замовляли на радіо за весь час?", tt.Name, $"{tt.Extra} — {Count(tt.N, "замовлення", "замовлення", "замовлень")}"));
        if (Leader(f.LikedTracks.Where(Clean).ToList(), 2) is { } lt)
            radio.Add(What("Яка пісня зібрала на радіо найбільше сердечок за весь час?", lt.Name, $"{lt.Extra} — {Count(lt.N, "сердечко", "сердечка", "сердечок")}"));
        if (Leader(f.WeekArtists.Where(Clean).ToList(), 3) is { } ar)
            radio.Add(What("Якого виконавця найчастіше замовляли на радіо за останній тиждень?", ar.Name, Count(ar.N, "замовлення", "замовлення", "замовлень")));

        var games = new List<SvoyaQuestion>();
        if (Leader(f.WeekGames, 3) is { } wg && gameTitle(wg.Name) is { } wgt)
            games.Add(What("У яку гру на Глечиках за останній тиждень зіграли найбільше партій?", wgt, Count(wg.N, "партія", "партії", "партій") + " за тиждень"));
        if (Leader(f.WeekWins, 3) is { } ww)
            games.Add(Who("Хто за останній тиждень виграв на Глечиках найбільше партій?", ww, Count(ww.N, "перемога", "перемоги", "перемог") + " за тиждень"));
        foreach (var (game, winners) in f.GameWinners)
            if (Leader(winners, 3) is { } gw && gameTitle(game) is { } gt)
                games.Add(Who($"Хто найчастіше перемагав у грі «{gt}»?", gw, Count(gw.N, "перемога", "перемоги", "перемог")));
        if (Leader(f.AllGames, 5) is { } ag)
            games.Add(Who("Хто зіграв на Глечиках найбільше партій за весь час (без Гончарного кола)?", ag, Count(ag.N, "партія", "партії", "партій")));

        var pots = new List<SvoyaQuestion>();
        if (Leader(f.Earned, 50) is { } er)
            pots.Add(Who("Хто за весь час заробив найбільше черепків?", er, Count(er.N, "черепок", "черепки", "черепків")));
        if (Leader(f.Achievements, 3) is { } ac)
            pots.Add(Who("У кого найбільше ачівок на Глечиках?", ac, Count(ac.N, "ачівка", "ачівки", "ачівок")));
        if (Leader(f.WeekClicker, 3) is { } wc)
            pots.Add(Who("Хто за останній тиждень найчастіше сідав за Гончарне коло?", wc, Count(wc.N, "раз", "рази", "разів") + " за тиждень"));
        if (Leader(f.Daily, 3) is { } dl)
            pots.Add(Who("Хто найчастіше розв'язує щоденні задачки на Глечиках — «Слово дня» й інші?", dl, Count(dl.N, "розв'язана", "розв'язані", "розв'язаних")));

        var themes = new List<SvoyaTheme>();
        void Theme(string name, List<SvoyaQuestion> qs)
        {
            if (qs.Count == 0) return;
            var list = qs.Take(5).ToList();
            for (var i = 0; i < list.Count; i++) list[i].Price = (i + 1) * SvoyaPack.PriceStep;
            themes.Add(new SvoyaTheme { Name = name, Questions = list });
        }
        Theme("Радіо", radio);
        Theme("Ігри", games);
        Theme("Черепки й коло", pots);
        var n = themes.Sum(t => t.Questions.Count);
        if (n < MinQuestions || themes.Count < 2)
            return (null, n, $"Замало життя на сайті: вийшло {n} {Plural(n, "запитання", "запитання", "запитань")} з {MinQuestions} потрібних. Послухайте радіо й пограйте — тема збереться сама");
        var pack = new SvoyaPack
        {
            Id = Id, Title = Title, Author = SvoyaBuiltin.Author, AuthorKey = "", Public = true, Source = SvoyaPack.Builtin,
            Description = "Автотема з життя сайту: радіо, ігри, черепки. Факти з бази на " + now.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture),
            Rounds = [new SvoyaRound { Name = "Про нас", Themes = themes }],
            CreatedAt = now, UpdatedAt = now,
        };
        pack.Normalize();
        var errors = pack.Validate();
        return errors.Count > 0 ? (null, n, "Тема «Про нас» не склалась: " + errors[0]) : (pack, n, "");
    }

    /// <summary>Єдиний лідер, у якого щонайменше <paramref name="min"/>; нічия на першому місці — null (не вгадаєш).</summary>
    static Rank? Leader(List<Rank> list, int min)
    {
        var real = list.Where(r => r.Name.Trim().Length > 0 && !r.Name.StartsWith("🤖", StringComparison.Ordinal)).OrderByDescending(r => r.N).ToList();
        if (real.Count == 0 || real[0].N < min) return null;
        return real.Count > 1 && real[1].N == real[0].N ? null : real[0];
    }

    /// <summary>Реклама й «дорослі» доріжки — не для запитань.</summary>
    static bool Clean(Rank r) =>
        !(r.Name + " " + r.Extra).Contains("Реклама", StringComparison.OrdinalIgnoreCase)
        && !(r.Name + " " + r.Extra).Contains("18+", StringComparison.Ordinal);

    static SvoyaQuestion Who(string text, Rank r, string comment) => new()
    {
        Text = text, Answer = Cut(r.Name), Accept = NickAccept(r.Name), Comment = Cut(r.Name) + ": " + comment,
    };

    static SvoyaQuestion What(string text, string answer, string comment) => new()
    {
        Text = text, Answer = Cut(answer), Accept = TitleAccept(answer), Comment = comment,
    };

    static string Cut(string s) => s.Length <= SvoyaPack.AnswerMax ? s : s[..SvoyaPack.AnswerMax];

    /// <summary>
    /// Що ще зарахувати за нік: без «гість », без дужок («микола ( справжній )» → «микола»), і кожне довге слово
    /// ніка окремо («Mariana Matviienko» → «Mariana»): друзі кличуть одне одного коротко.
    /// </summary>
    public static List<string> NickAccept(string nick)
    {
        var list = new List<string>();
        void Add(string s)
        {
            s = Spaces().Replace(s, " ").Trim();
            if (s.Length >= 3 && !s.Equals(nick, StringComparison.OrdinalIgnoreCase) && !list.Contains(s, StringComparer.OrdinalIgnoreCase)) list.Add(Cut(s));
        }
        var bare = nick.StartsWith("гість ", StringComparison.OrdinalIgnoreCase) ? nick[6..] : nick;
        bare = Brackets().Replace(bare, " ");
        Add(bare);
        var words = Spaces().Replace(bare, " ").Trim().Split(' ');
        if (words.Length > 1) foreach (var w in words) if (w.Length >= 4) Add(w);
        return [.. list.Take(SvoyaPack.MaxAccept)];
    }

    /// <summary>Назва пісні без дужок і хвостів («Stefania (Kalush Orchestra)» → «Stefania»).</summary>
    public static List<string> TitleAccept(string title)
    {
        var bare = Spaces().Replace(Brackets().Replace(title, " "), " ").Trim();
        var dash = bare.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 2) bare = bare[..dash].Trim();
        return bare.Length >= 2 && !bare.Equals(title, StringComparison.OrdinalIgnoreCase) ? [Cut(bare)] : [];
    }

    static string Count(int n, string one, string few, string many) => $"{n} {Plural(n, one, few, many)}";

    static string Plural(int n, string one, string few, string many)
    {
        var d = Math.Abs(n) % 100;
        if (d is >= 11 and <= 14) return many;
        return (d % 10) switch { 1 => one, >= 2 and <= 4 => few, _ => many };
    }

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]\s*")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // =========================================================================================
    // База (лише читання)
    // =========================================================================================

    Facts Read(DateTimeOffset now)
    {
        if (db is null) return new Facts();
        var since = now.AddDays(-7).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        return db.With(c =>
        {
            var weekGames = Ranks(c, since, "SELECT game, COUNT(DISTINCT room_id || ':' || round) FROM game_results WHERE created_at >= $since AND game <> 'clicker' GROUP BY game ORDER BY 2 DESC LIMIT 3");
            var popular = Ranks(c, since, "SELECT game, COUNT(DISTINCT room_id || ':' || round) FROM game_results WHERE outcome IN ('win','loss','draw') AND game <> 'clicker' GROUP BY game ORDER BY 2 DESC LIMIT 2");
            var winners = new List<(string, List<Rank>)>();
            foreach (var g in popular)
                winners.Add((g.Name, Ranks(c, since, "SELECT MAX(nick), COUNT(*) FROM game_results WHERE game = $g AND outcome = 'win' GROUP BY nick_key ORDER BY 2 DESC LIMIT 3", ("$g", g.Name))));
            return new Facts
            {
                WeekRequests = Ranks(c, since, "SELECT requested_by, COUNT(*) FROM plays WHERE source = 'user' AND requested_by IS NOT NULL AND started_at >= $since GROUP BY requested_by ORDER BY 2 DESC LIMIT 3"),
                WeekLikes = Ranks(c, since, "SELECT nick, COUNT(*) FROM likes WHERE created_at >= $since GROUP BY nick ORDER BY 2 DESC LIMIT 3"),
                TopTracks = Ranks(c, since, "SELECT t.title, COUNT(*), t.artist FROM plays p JOIN tracks t ON t.id = p.track_id WHERE p.source = 'user' GROUP BY p.track_id ORDER BY 2 DESC LIMIT 8"),
                LikedTracks = Ranks(c, since, "SELECT t.title, COUNT(*), t.artist FROM likes l JOIN tracks t ON t.id = l.track_id GROUP BY l.track_id ORDER BY 2 DESC LIMIT 8"),
                WeekArtists = Ranks(c, since, "SELECT t.artist, COUNT(*) FROM plays p JOIN tracks t ON t.id = p.track_id WHERE p.source = 'user' AND p.started_at >= $since GROUP BY t.artist ORDER BY 2 DESC LIMIT 8"),
                WeekGames = weekGames,
                WeekWins = Ranks(c, since, "SELECT MAX(nick), COUNT(*) FROM game_results WHERE outcome = 'win' AND created_at >= $since GROUP BY nick_key ORDER BY 2 DESC LIMIT 3"),
                GameWinners = winners,
                AllGames = Ranks(c, since, "SELECT MAX(nick), COUNT(*) FROM game_results WHERE game <> 'clicker' GROUP BY nick_key ORDER BY 2 DESC LIMIT 3"),
                Earned = Ranks(c, since, "SELECT nick, earned FROM wallets ORDER BY earned DESC LIMIT 3"),
                Achievements = Ranks(c, since, "SELECT MAX(nick), COUNT(*) FROM achievements GROUP BY nick_key ORDER BY 2 DESC LIMIT 3"),
                WeekClicker = Ranks(c, since, "SELECT MAX(nick), COUNT(*) FROM game_results WHERE game = 'clicker' AND created_at >= $since GROUP BY nick_key ORDER BY 2 DESC LIMIT 3"),
                Daily = Ranks(c, since, "SELECT MAX(nick), COUNT(*) FROM daily_results WHERE solved = 1 GROUP BY nick_key ORDER BY 2 DESC LIMIT 3"),
            };
        });
    }

    /// <summary>Рейтинг із запиту «назва, число[, додаток]». Нема таблиці чи колонки — порожньо (тема просто бідніша).</summary>
    List<Rank> Ranks(SqliteConnection c, string since, string sql, params (string Name, string Value)[] args)
    {
        var list = new List<Rank>();
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            if (sql.Contains("$since", StringComparison.Ordinal)) cmd.Parameters.AddWithValue("$since", since);
            foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (!r.IsDBNull(0))
                    list.Add(new Rank(r.GetString(0), r.IsDBNull(1) ? 0 : r.GetInt32(1), r.FieldCount > 2 && !r.IsDBNull(2) ? r.GetString(2) : null));
        }
        catch (SqliteException e)
        {
            _log.LogDebug("своя гра: «Про нас» без частини фактів: {Err}", e.Message);
        }
        return list;
    }
}
