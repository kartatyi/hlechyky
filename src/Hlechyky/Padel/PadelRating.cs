using Hlechyky.Games;
using static Hlechyky.Padel.PadelMoneySql;

namespace Hlechyky.Padel;

// =====================================================================================================================
// Рейтинг, статистика й відзнаки (контракт §3.4) — над історією половини гри (IPadelHistory) і зборами.
// Ело рахується переграванням усіх результатів за часом: так прив'язка гостя до акаунта чи виправлений рахунок
// одразу дають правильні числа, і нічого не треба «доправляти» в базі.
// =====================================================================================================================

/// <summary>Відзнака: ключ, емодзі, назва, за що.</summary>
public sealed record PadelBadgeKind(string Key, string Emoji, string Title, string Text);

public static class PadelRatingBadges
{
    public static readonly IReadOnlyList<PadelBadgeKind> All =
    [
        new("first", "🍳", "Перша пательня", "Перший результат у Падельні"),
        new("bagel", "🥯", "Бублик", "Сет «на суху» — 6:0 (у швидкому 4:0)"),
        new("comeback", "🔄", "Камбек", "Виграний сет після 1:5 (у швидкому після 0:3)"),
        new("golden", "✨", "Золота рука", "3 виграні вирішальні очки за матч"),
        new("tiebreak", "🧊", "Нерви зі сталі", "3 виграні тайбрейки"),
        new("streak", "🔥", "На хвилі", "5 перемог поспіль"),
        new("social", "🤝", "Душа компанії", "У парі з 10 різними людьми"),
        new("regular", "📅", "Завсідник", "10 зборів на падел"),
        new("champion", "👑", "Король турніру", "Перше місце в турнірі"),
        new("podium", "🏅", "П'єдестал", "3 рази в трійці турніру"),
        new("wall", "🧱", "Залізна стіна", "Нагорода «Залізна стіна» в турнірі"),
        new("marathon", "🏃", "Марафонець", "50 результатів"),
    ];

    public static PadelBadgeKind? Get(string key) => All.FirstOrDefault(b => b.Key == key);
}

/// <summary>Порахована статистика однієї людини (канонічний pid).</summary>
public sealed class PadelRatingStat(string pid)
{
    public string Pid { get; } = pid;
    public double R { get; set; } = PadelRating.Start;
    public int Played, Wins, Draws, Losses, For, Against;
    public readonly List<(DateTimeOffset At, double R)> History = [];
    public readonly Dictionary<string, (int Played, int Wins)> Partners = new(StringComparer.Ordinal);
    public readonly Dictionary<string, (int Played, int Wins, int Losses)> Rivals = new(StringComparer.Ordinal);
    /// <summary>Свої результати: сам результат, моя команда (0/1) і канонічні склади.</summary>
    public readonly List<(PadelResult Res, int Team, string[][] Teams)> Results = [];

    public int Outcome(PadelResult r, int team) => r.Winner < 0 ? 0 : r.Winner == team ? 1 : -1;
}

/// <summary>Знімок усього порахованого: статистика людей, пари, відзнаки.</summary>
public sealed class PadelRatingBook
{
    public required Dictionary<string, PadelRatingStat> Stats { get; init; }
    /// <summary>Пари (a &lt; b) → разом зіграно й виграно.</summary>
    public required Dictionary<(string A, string B), (int Played, int Wins)> Pairs { get; init; }
    /// <summary>pid → ключ відзнаки → коли заслужено.</summary>
    public required Dictionary<string, Dictionary<string, DateTimeOffset>> Badges { get; init; }
    public required List<PadelRatingStat> Rows { get; init; }
    public required List<PadelRatingStat> Provisional { get; init; }
    public required Dictionary<string, string> Titles { get; init; }
    public required Dictionary<string, int> Delta7 { get; init; }
}

/// <summary>Сховище відзнак: що вже видано (і тост за нього пішов або був мовчки на першому прогоні).</summary>
public sealed class PadelBadgeStore
{
    readonly Db _db;

    public PadelBadgeStore(Db db)
    {
        _db = db;
        _db.With(c => { Exec(c, "CREATE TABLE IF NOT EXISTS padel_badges(pid TEXT NOT NULL, key TEXT NOT NULL, at TEXT NOT NULL, PRIMARY KEY(pid, key));"); });
    }

    public bool Add(string pid, string key, DateTimeOffset at) => _db.With(c =>
        Exec(c, "INSERT OR IGNORE INTO padel_badges(pid, key, at) VALUES($p, $k, $at)", ("$p", pid), ("$k", key), ("$at", Iso(at))) > 0);

    public HashSet<(string Pid, string Key)> All() => _db.With(c =>
        Rows(c, "SELECT pid, key FROM padel_badges", r => (r.GetString(0), r.GetString(1))).ToHashSet());

    public List<(string Key, DateTimeOffset At)> Of(string pid) => _db.With(c =>
        Rows(c, "SELECT key, at FROM padel_badges WHERE pid=$p ORDER BY at, key", r => (r.GetString(0), Ts(r.GetString(1))), ("$p", pid)));
}

/// <summary>
/// Ело, статистика, хімія пар, відзнаки. Рахує ліниво з кешем: знімок живе, доки не змінився «відбиток» історії
/// (кількість і останні результати, турніри, минулі збори, прив'язки гостей) — перевіряється не частіше раз на 10 с.
/// </summary>
public sealed class PadelRating(IPadelHistory history, IPadelPlayers players, PadelGatherStore gathers, PadelBadgeStore badges,
    IPadelWire wire, IClock clock, ILogger<PadelRating> log)
{
    public const double Start = 1200;
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(10);
    readonly object _lock = new();
    PadelRatingBook? _book;
    string? _sig;
    DateTimeOffset _checked;
    bool _primed;
    string? _badgeSig;

    // ---------------------------------------------------------------- Ело

    public static double Expected(double ra, double rb) => 1 / (1 + Math.Pow(10, (rb - ra) / 400));

    /// <summary>Скільки заробила команда A: перемога 1 / поразка 0 / нічия 0,5; у points — частка очок.</summary>
    public static double Score(PadelResult r)
    {
        if (r.Mode == "points" && r.Points is { Length: >= 2 } p)
            return p[0] + p[1] == 0 ? 0.5 : (double)p[0] / (p[0] + p[1]);
        return r.Winner switch { 0 => 1, 1 => 0, _ => 0.5 };
    }

    static int K(int played) => played < 10 ? 40 : 24;

    // ---------------------------------------------------------------- знімок

    /// <summary>Свіжий знімок (з кешу, якщо історія не змінилась).</summary>
    public PadelRatingBook Book()
    {
        lock (_lock)
        {
            var now = clock.UtcNow;
            if (_book is not null && now - _checked < Fresh) return _book;
            var sig = Signature(now);
            _checked = now;
            if (_book is not null && sig == _sig) return _book;
            _book = Build(now);
            _sig = sig;
            return _book;
        }
    }

    /// <summary>
    /// Відбиток усього, від чого залежить знімок. Час — з точністю до 10 хв (delta7 і «Сковорідка тижня» їдуть самі).
    /// </summary>
    string Signature(DateTimeOffset now)
    {
        var h = new HashCode();
        foreach (var r in history.Results())
        {
            h.Add(r.Id);
            h.Add(r.Winner);
            foreach (var t in r.Teams) foreach (var p in t) h.Add(players.Canon(p));
        }
        foreach (var t in history.Tournaments()) { h.Add(t.Id); foreach (var u in t.Ranked) foreach (var p in u) h.Add(players.Canon(p)); }
        var ended = gathers.Ended(now);
        h.Add(ended.Count);
        return $"{h.ToHashCode()}:{ended.LastOrDefault()?.Id}:{now.ToUnixTimeSeconds() / 600}";
    }

    PadelRatingBook Build(DateTimeOffset now)
    {
        var stats = new Dictionary<string, PadelRatingStat>(StringComparer.Ordinal);
        var pairs = new Dictionary<(string, string), (int Played, int Wins)>();
        var got = new Dictionary<string, Dictionary<string, DateTimeOffset>>(StringComparer.Ordinal);
        var streak = new Dictionary<string, int>(StringComparer.Ordinal);
        var tbs = new Dictionary<string, int>(StringComparer.Ordinal);
        PadelRatingStat S(string pid) => stats.TryGetValue(pid, out var s) ? s : stats[pid] = new PadelRatingStat(pid);
        void Give(string pid, string key, DateTimeOffset at)
        {
            if (!got.TryGetValue(pid, out var mine)) got[pid] = mine = new(StringComparer.Ordinal);
            mine.TryAdd(key, at);
        }

        foreach (var r in history.Results().OrderBy(r => r.At))
        {
            if (r.Teams is not { Length: 2 }) continue;
            var teams = r.Teams.Select(t => t.Select(players.Canon).Distinct().ToArray()).ToArray();
            if (teams[0].Length == 0 || teams[1].Length == 0 || teams[0].Intersect(teams[1]).Any()) continue;
            var ra = teams[0].Average(p => S(p).R);
            var rb = teams[1].Average(p => S(p).R);
            var ea = Expected(ra, rb);
            var sa = Score(r);
            // Спершу всі зміни, потім застосувати — щоб рейтинг напарника не встиг зрушити середнє посеред підрахунку
            var deltas = teams[0].Select(p => (p, K(S(p).Played) * (sa - ea)))
                .Concat(teams[1].Select(p => (p, K(S(p).Played) * ((1 - sa) - (1 - ea))))).ToList();
            foreach (var (p, d) in deltas) S(p).R += d;

            for (var t = 0; t < 2; t++)
            {
                var mine = teams[t];
                var them = teams[1 - t];
                var (pf, pa) = Tally(r, t);
                foreach (var p in mine)
                {
                    var s = S(p);
                    var o = s.Outcome(r, t);
                    s.Played++;
                    if (o > 0) s.Wins++; else if (o < 0) s.Losses++; else s.Draws++;
                    s.For += pf;
                    s.Against += pa;
                    s.History.Add((r.At, s.R));
                    s.Results.Add((r, t, teams));
                    foreach (var q in mine.Where(q => q != p))
                    {
                        var x = s.Partners.GetValueOrDefault(q);
                        s.Partners[q] = (x.Played + 1, x.Wins + (o > 0 ? 1 : 0));
                    }
                    foreach (var q in them)
                    {
                        var x = s.Rivals.GetValueOrDefault(q);
                        s.Rivals[q] = (x.Played + 1, x.Wins + (o > 0 ? 1 : 0), x.Losses + (o < 0 ? 1 : 0));
                    }

                    // Відзнаки з результатів
                    Give(p, "first", r.At);
                    var f = r.Facts ?? PadelFacts.None;
                    if (At(f.Bagel, t)) Give(p, "bagel", r.At);
                    if (At(f.Comeback, t)) Give(p, "comeback", r.At);
                    if (At(f.GoldenWon, t) >= 3) Give(p, "golden", r.At);
                    tbs[p] = tbs.GetValueOrDefault(p) + At(f.TieBreaksWon, t);
                    if (tbs[p] >= 3) Give(p, "tiebreak", r.At);
                    streak[p] = o > 0 ? streak.GetValueOrDefault(p) + 1 : 0;
                    if (streak[p] >= 5) Give(p, "streak", r.At);
                    if (s.Partners.Count >= 10) Give(p, "social", r.At);
                    if (s.Played >= 50) Give(p, "marathon", r.At);
                }
                if (mine.Length == 2)
                {
                    var key = string.CompareOrdinal(mine[0], mine[1]) < 0 ? (mine[0], mine[1]) : (mine[1], mine[0]);
                    var x = pairs.GetValueOrDefault(key);
                    pairs[key] = (x.Played + 1, x.Wins + (r.Winner == t ? 1 : 0));
                }
            }
        }

        // Турніри: перше місце, трійка, «Залізна стіна»
        var podiums = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in history.Tournaments().OrderBy(t => t.At))
        {
            for (var place = 0; place < Math.Min(3, t.Ranked.Length); place++)
                foreach (var p in t.Ranked[place].Select(players.Canon).Distinct())
                {
                    if (place == 0) Give(p, "champion", t.At);
                    podiums[p] = podiums.GetValueOrDefault(p) + 1;
                    if (podiums[p] >= 3) Give(p, "podium", t.At);
                }
            if (t.Awards.TryGetValue("wall", out var wall))
                foreach (var p in wall.Select(players.Canon).Distinct()) Give(p, "wall", t.At);
        }

        // Збори, що минули: хто в них ішов
        var visits = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var g in gathers.Ended(now))
            foreach (var p in gathers.Goers(g.Id).Take(g.Slots).Select(x => players.Canon(x.Pid)).Distinct())
            {
                visits[p] = visits.GetValueOrDefault(p) + 1;
                if (visits[p] >= 10) Give(p, "regular", g.Until);
            }

        var rows = stats.Values.Where(s => s.Played >= 3).OrderByDescending(s => s.R).ThenByDescending(s => s.Played).ThenBy(s => s.Pid, StringComparer.Ordinal).ToList();
        var provisional = stats.Values.Where(s => s.Played < 3).OrderByDescending(s => s.Played).ThenByDescending(s => s.R).ThenBy(s => s.Pid, StringComparer.Ordinal).ToList();
        var week = now.AddDays(-7);
        var delta7 = stats.Values.ToDictionary(s => s.Pid, s =>
            (int)Math.Round(s.R - (s.History.LastOrDefault(h => h.At <= week) is { At: var a } h0 && a != default ? h0.R : Start)), StringComparer.Ordinal);
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        if (rows.FirstOrDefault() is { Played: >= 5 } king) titles[king.Pid] = "👑 Король Падельні";
        var pan = stats.Values
            .Select(s => (s, n: s.Results.Count(x => x.Res.At > week), w: s.Results.Count(x => x.Res.At > week && s.Outcome(x.Res, x.Team) > 0)))
            .Where(x => x.n >= 3 && x.w > 0).OrderByDescending(x => x.w).ThenByDescending(x => x.s.R).FirstOrDefault();
        if (pan.s is not null)
            titles[pan.s.Pid] = titles.TryGetValue(pan.s.Pid, out var t0) ? t0 + " · 🍳 Сковорідка тижня" : "🍳 Сковорідка тижня";
        return new PadelRatingBook
        {
            Stats = stats, Pairs = pairs, Badges = got, Rows = rows, Provisional = provisional, Titles = titles, Delta7 = delta7,
        };
    }

    static bool At(bool[]? a, int i) => a is not null && i < a.Length && a[i];
    static int At(int[]? a, int i) => a is not null && i < a.Length ? a[i] : 0;

    /// <summary>Скільки взяла команда й скільки віддала: points — очки; match — гейми (супертайбрейк — один гейм).</summary>
    static (int For, int Against) Tally(PadelResult r, int t)
    {
        if (r.Mode == "points" && r.Points is { Length: >= 2 } p) return (p[t], p[1 - t]);
        int f = 0, a = 0;
        foreach (var s in r.Sets ?? [])
        {
            if (s is not { Length: >= 2 }) continue;
            if (Math.Max(s[0], s[1]) > 7) { if (s[t] > s[1 - t]) f++; else if (s[t] < s[1 - t]) a++; }
            else { f += s[t]; a += s[1 - t]; }
        }
        return (f, a);
    }

    public static string ScoreText(PadelResult r) => r.Mode == "points" && r.Points is { Length: >= 2 } p
        ? $"{p[0]}:{p[1]}"
        : string.Join(" · ", (r.Sets ?? []).Where(s => s is { Length: >= 2 }).Select(s => $"{s[0]}:{s[1]}"));

    // ---------------------------------------------------------------- види

    object P(string pid)
    {
        var p = players.Player(pid);
        return new { pid, name = p.Name, guest = p.Guest };
    }

    public object Rating()
    {
        var b = Book();
        object Row(PadelRatingStat s)
        {
            var p = players.Player(s.Pid);
            return new
            {
                pid = s.Pid, name = p.Name, guest = p.Guest, rating = (int)Math.Round(s.R), delta7 = b.Delta7.GetValueOrDefault(s.Pid),
                played = s.Played, wins = s.Wins, title = b.Titles.GetValueOrDefault(s.Pid),
            };
        }
        return new { rows = b.Rows.Select(Row).ToList(), provisional = b.Provisional.Select(Row).ToList() };
    }

    public object Chemistry()
    {
        var b = Book();
        return new
        {
            pairs = b.Pairs.Where(kv => kv.Value.Played >= 3)
                .Select(kv => (kv.Key, kv.Value, pct: Pct(kv.Value.Wins, kv.Value.Played)))
                .OrderByDescending(x => x.pct).ThenByDescending(x => x.Value.Played).ThenBy(x => x.Key.Item1, StringComparer.Ordinal)
                .Select(x => new
                {
                    a = x.Key.Item1, b = x.Key.Item2, names = new[] { players.Name(x.Key.Item1), players.Name(x.Key.Item2) },
                    played = x.Value.Played, wins = x.Value.Wins, pct = x.pct,
                }).ToList(),
        };
    }

    static int Pct(int wins, int played) => played == 0 ? 0 : (int)Math.Round(100.0 * wins / played);

    public IResult Profile(string pid)
    {
        if (!players.Exists(pid)) return PadelMoneyHttp.Fail("Нема такого гравця", 404);
        var me = players.Canon(pid);
        var b = Book();
        var s = b.Stats.GetValueOrDefault(me) ?? new PadelRatingStat(me);
        var rank = b.Rows.IndexOf(s);
        var partners = s.Partners.Select(kv => new { pid = kv.Key, name = players.Name(kv.Key), played = kv.Value.Played, wins = kv.Value.Wins, pct = Pct(kv.Value.Wins, kv.Value.Played) })
            .OrderByDescending(x => x.played).ThenByDescending(x => x.pct).ThenBy(x => x.pid, StringComparer.Ordinal).ToList();
        var rivals = s.Rivals.Select(kv => new { pid = kv.Key, name = players.Name(kv.Key), played = kv.Value.Played, wins = kv.Value.Wins, losses = kv.Value.Losses })
            .OrderByDescending(x => x.played).ThenByDescending(x => x.losses).ThenBy(x => x.pid, StringComparer.Ordinal).ToList();
        var streak = new { kind = "w", n = 0 };
        foreach (var (res, team, _) in Enumerable.Reverse(s.Results))
        {
            var o = s.Outcome(res, team);
            if (o == 0) break;
            var kind = o > 0 ? "w" : "l";
            if (streak.n > 0 && streak.kind != kind) break;
            streak = new { kind, n = streak.n + 1 };
        }
        return PadelMoneyHttp.Ok(new
        {
            player = P(me),
            rating = (int)Math.Round(s.R),
            rank = rank >= 0 ? rank + 1 : (int?)null,
            played = s.Played, wins = s.Wins, draws = s.Draws, losses = s.Losses, winPct = Pct(s.Wins, s.Played),
            pointsFor = s.For, pointsAgainst = s.Against,
            streak,
            partners,
            rivals,
            best = new
            {
                partner = partners.Where(x => x.played >= 3).OrderByDescending(x => x.pct).ThenByDescending(x => x.played).FirstOrDefault(),
                rival = rivals.FirstOrDefault(),
                nemesis = rivals.Where(x => x.losses >= 3).OrderByDescending(x => x.losses).ThenByDescending(x => x.played).FirstOrDefault(),
            },
            badges = badges.Of(me).Select(x => PadelRatingBadges.Get(x.Key) is { } k
                ? new { key = k.Key, emoji = k.Emoji, title = k.Title, text = k.Text, at = x.At.UtcDateTime } : null).Where(x => x is not null).ToList(),
            recent = Enumerable.Reverse(s.Results).Take(20).Select(x => new
            {
                id = x.Res.Id, at = x.Res.At.UtcDateTime, source = x.Res.Source,
                teams = x.Teams.Select(t => t.Select(P).ToList()).ToList(),
                score = ScoreText(x.Res),
                won = x.Res.Winner < 0 ? (bool?)null : x.Res.Winner == x.Team,
            }).ToList(),
            ratingHistory = s.History.Select(h => new { at = h.At.UtcDateTime, r = (int)Math.Round(h.R) }).ToList(),
        });
    }

    // ---------------------------------------------------------------- відзнаки

    /// <summary>
    /// Видати нові відзнаки. Перший прогін після старту — мовчки (усе, що назбиралось, лягає в базу без тостів);
    /// далі кожна нова — тост акаунту й пінг рейтингу. Повертає, скільки нових. Кличе таймер раз на 10 с; якщо
    /// знімок не змінився — нічого не робить.
    /// </summary>
    public int CheckBadges()
    {
        PadelRatingBook book;
        bool silent;
        lock (_lock)
        {
            _checked = default;   // таймер — саме той, хто й мусить помітити зміну
        }
        book = Book();
        lock (_lock)
        {
            if (_primed && _badgeSig == _sig) return 0;
            silent = !_primed;
            _primed = true;
            _badgeSig = _sig;
        }
        var have = badges.All();
        var fresh = 0;
        foreach (var (pid, keys) in book.Badges)
            foreach (var (key, at) in keys)
            {
                if (have.Contains((pid, key)) || !badges.Add(pid, key, at)) continue;
                fresh++;
                if (!silent && Pid.IsUser(pid) && PadelRatingBadges.Get(key) is { } k)
                    wire.Toast(Pid.NickKey(pid)!, $"🏅 Нова відзнака в Падельні: {k.Title}");
            }
        if (fresh > 0 && !silent) wire.Rating();
        if (fresh > 0) log.LogInformation("Падельня: нових відзнак {Count}{Silent}", fresh, silent ? " (мовчки, старт)" : "");
        return fresh;
    }
}

/// <summary>Маршрути рейтингу: <c>/api/padel/rating</c>, <c>/profile/{pid}</c>, <c>/chemistry</c>. Публічно.</summary>
public static class PadelRatingApi
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/rating", (PadelRating r) => r.Rating());
        api.MapGet("/profile/{pid}", (string pid, PadelRating r) => r.Profile(Uri.UnescapeDataString(pid)));
        api.MapGet("/chemistry", (PadelRating r) => r.Chemistry());
    }
}
