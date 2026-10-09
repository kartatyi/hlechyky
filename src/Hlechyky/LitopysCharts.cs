using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Hlechyky.Games;
using Hlechyky.Games.Economy;

namespace Hlechyky;

/// <summary>
/// «📈 Графіки» й «📖 Рекорди» у «📊 Хто скільки»: 🏁 Гонка за всі ігри з рухом місць, 📈 Біржа черепків (баланс кожного
/// в часі), 📊 Ело в часі (реплей партій тими самими правилами, що й <see cref="Ratings"/>) і Книга рекордів за весь час.
/// <para>
/// Нових таблиць тут нема: баланс у минулому — це поточний гаманець мінус пізніші рядки леджера, Ело в минулому — реплей
/// <c>game_results</c> від стартової тисячі. Усе з тих самих сирих подій, що й «Огляд», і з тим самим кешем на хвилину.
/// </para>
/// </summary>
public sealed partial class Litopys
{
    /// <summary>Очки 🏁 Гонки: партія за столом (не соло) — за участь, перемога й нічия — ще зверху; розгадана щоденка.</summary>
    public const int RacePlay = 1, RaceWin = 2, RaceDraw = 1, RaceDaily = 2;

    /// <summary>Скільки ліній на 📈 Біржі (за поточним балансом), крім «я».</summary>
    public const int BourseLines = 8;

    /// <summary>Скільки ліній на 📊 Ело (за кількістю партій у вікні), крім «я».</summary>
    public const int EloLines = 8;

    /// <summary>Рекорд «🆕 новий», коли встановлений не раніше стількох днів тому.</summary>
    public static readonly TimeSpan RecordFresh = TimeSpan.FromDays(3);

    /// <summary>Рядок 🏁 Гонки. <see cref="Place"/> — місце з урахуванням нічиїх (однакові очки — однакове місце).</summary>
    public sealed record RaceRow(int Place, string Nick, string Key, int Points, int Games, int Wins, int Draws, int Dailies);

    static partial void MapCharts(RouteGroupBuilder api)
    {
        api.MapGet("/race", (string? period, Litopys l) => l.Race(period));
        api.MapGet("/bourse", (string? period, string? nick, Litopys l) => l.Bourse(period, nick));
        api.MapGet("/elo", (string? game, string? period, Litopys l) => l.EloChart(game, period));
        api.MapGet("/records", (Litopys l) => l.Records());
    }

    // =============================================================================================
    // Вікна періодів у будь-яку мить
    // =============================================================================================

    static string DayBack(string day, int back) =>
        DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(-back).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    static int PeriodDays(string period) => period switch { "day" => 1, "week" => 7, "month" => 30, _ => 0 };

    /// <summary>Початок вікна періоду, яким воно було в мить <paramref name="at"/> (київські дні, як <see cref="Periods"/>).</summary>
    static DateTimeOffset WindowAt(string period, DateTimeOffset at) =>
        PeriodDays(period) is var n and > 0 ? Daily.StartOfDayUtc(DayBack(Days.Of(at), n - 1)) : DateTimeOffset.MinValue;

    static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();

    // =============================================================================================
    // 🏁 Гонка
    // =============================================================================================

    /// <summary>Розгадані щоденки з часом (у «сирих» подіях часу нема — Гонці він потрібен для руху місць).</summary>
    List<(string Key, DateTimeOffset At)> Solves() => Cached("ch:solves", () => db.With(c =>
    {
        var list = new List<(string, DateTimeOffset)>();
        using var cmd = Cmd(c, "SELECT nick_key, created_at FROM daily_results WHERE solved = 1");
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), Ts(r.GetString(1))));
        return list;
    }));

    /// <summary>
    /// Таблиця 🏁 Гонки періоду <paramref name="period"/> за станом на мить <paramref name="at"/>: вікно — яким воно було
    /// тоді (за день — від київської півночі того дня). Газеті й цілям — той самий рахунок, що й вкладці.
    /// </summary>
    public List<RaceRow> RaceTable(string period, DateTimeOffset at) => RaceRows(WindowAt(Norm(period), at), at);

    List<RaceRow> RaceRows(DateTimeOffset since, DateTimeOffset until)
    {
        var raw = RawOf("all");
        var dj = DjKeys();
        var acc = new Dictionary<string, int[]>(StringComparer.Ordinal);   // очки, партії, перемоги, нічиї, щоденки
        int[] Of(string key) => acc.TryGetValue(key, out var a) ? a : acc[key] = new int[5];
        foreach (var r in raw.Results)
        {
            if (r.Solo || r.At < since || r.At > until || dj.Contains(r.Key)) continue;
            var a = Of(r.Key);
            a[1]++;
            a[0] += RacePlay;
            if (r.Outcome == "win") { a[2]++; a[0] += RaceWin; }
            else if (r.Outcome == "draw") { a[3]++; a[0] += RaceDraw; }
        }
        foreach (var (key, at) in Solves())
        {
            if (at < since || at > until || dj.Contains(key)) continue;
            var a = Of(key);
            a[4]++;
            a[0] += RaceDaily;
        }
        var sorted = acc.Where(kv => kv.Value[0] > 0)
            .OrderByDescending(kv => kv.Value[0]).ThenByDescending(kv => kv.Value[2]).ThenBy(kv => raw.Nick(kv.Key), StringComparer.Ordinal)
            .ToList();
        var rows = new List<RaceRow>(sorted.Count);
        for (var i = 0; i < sorted.Count; i++)
        {
            var (key, a) = (sorted[i].Key, sorted[i].Value);
            var place = i > 0 && rows[i - 1].Points == a[0] ? rows[i - 1].Place : i + 1;
            rows.Add(new RaceRow(place, raw.Nick(key), key, a[0], a[1], a[2], a[3], a[4]));
        }
        return rows;
    }

    /// <summary>
    /// GET /api/stats/race?period= — 🏁 Гонка: таблиця, рух місць (за день — за останню годину, бо доба тому вікна ще не
    /// було; інакше — за добу, у тому самому вікні), хто виграв попереднє вікно й хто найвище злетів.
    /// </summary>
    public object Race(string? period)
    {
        var p = Norm(period);
        return Cached("race:" + p, () =>
        {
            var now = clock.UtcNow;
            var since = WindowAt(p, now);
            var rows = RaceRows(since, now);
            var back = p == "day" ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
            var before = now - back > since ? RaceRows(since, now - back) : [];
            var was = before.ToDictionary(r => r.Key, r => r.Place, StringComparer.Ordinal);
            var moves = rows.Select(r => (Row: r, Move: was.TryGetValue(r.Key, out var w) ? w - r.Place : (int?)null)).ToList();
            var climb = moves.Where(m => m.Move > 0).OrderByDescending(m => m.Move).ThenBy(m => m.Row.Place).FirstOrDefault();

            object? prev = null;
            if (PeriodDays(p) is var n and > 0)
            {
                var pStart = Daily.StartOfDayUtc(DayBack(Days.Of(since), n));
                var top = RaceRows(pStart, since.AddTicks(-1)).TakeWhile(r => r.Place == 1).Take(3).ToList();
                if (top.Count > 0)
                    prev = new
                    {
                        word = p switch { "day" => "вчора", "week" => "минулого тижня", _ => "попередні 30 днів" },
                        nicks = top.Select(r => r.Nick),
                        points = top[0].Points,
                    };
            }
            return new
            {
                period = p,
                since = Periods.FirstDay(p, clock),
                moveWord = p == "day" ? "за годину" : "за добу",
                rows = moves.Select(m => new
                {
                    place = m.Row.Place, nick = m.Row.Nick, points = m.Row.Points, games = m.Row.Games, wins = m.Row.Wins,
                    draws = m.Row.Draws, dailies = m.Row.Dailies, move = m.Move, fresh = before.Count > 0 && m.Move is null,
                }),
                climb = climb.Row is null ? null : new { nick = climb.Row.Nick, by = climb.Move },
                prev,
                points = new { play = RacePlay, win = RacePlay + RaceWin, draw = RacePlay + RaceDraw, daily = RaceDaily },
            };
        });
    }

    // =============================================================================================
    // 📈 Біржа черепків
    // =============================================================================================

    sealed record LRow(string Key, int Delta, string Reason, DateTimeOffset At);

    /// <summary>Баланс людини в часі: <see cref="Pts"/> — після кожного рядка леджера (за зростанням), <see cref="Base"/> —
    /// що мало б бути до першого рядка (0, коли леджер повний).</summary>
    sealed record Track(string Key, int Now, long Base, List<(DateTimeOffset At, long Bal)> Pts);

    sealed class Bank
    {
        public List<LRow> Ledger = [];
        public Dictionary<string, Track> Tracks = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Леджер за весь час і баланс кожного гаманця в часі: від поточного <c>wallets.balance</c> назад мінус пізніші
    /// <c>delta</c>. Леджер, що почався пізніше за гаманець, дасть <see cref="Track.Base"/> ≠ 0 — тоді лінія просто
    /// починається з першого відомого запису.
    /// </summary>
    Bank BankOf() => Cached("ch:bank", () =>
    {
        var bank = new Bank();
        var wallets = new Dictionary<string, int>(StringComparer.Ordinal);
        db.With(c =>
        {
            using (var cmd = Cmd(c, "SELECT nick_key, balance FROM wallets"))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) wallets[r.GetString(0)] = r.GetInt32(1);
            using (var cmd = Cmd(c, "SELECT nick_key, delta, reason, created_at FROM ledger ORDER BY id"))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) bank.Ledger.Add(new LRow(r.GetString(0), r.GetInt32(1), r.GetString(2), Ts(r.GetString(3))));
        });
        foreach (var g in bank.Ledger.GroupBy(l => l.Key))
        {
            var rows = g.ToList();
            long bal = wallets.GetValueOrDefault(g.Key);
            var pts = new (DateTimeOffset, long)[rows.Count];
            for (var i = rows.Count - 1; i >= 0; i--)
            {
                pts[i] = (rows[i].At, bal);
                bal -= rows[i].Delta;
            }
            bank.Tracks[g.Key] = new Track(g.Key, wallets.GetValueOrDefault(g.Key), bal, [.. pts]);
        }
        return bank;
    });

    /// <summary>Скільки гаманців не сходиться з леджером (баланс до першого рядка не нуль) і на скільки разом.</summary>
    public (int People, long Shards) BourseGap()
    {
        var gaps = BankOf().Tracks.Values.Where(t => t.Base != 0).ToList();
        return (gaps.Count, gaps.Sum(t => Math.Abs(t.Base)));
    }

    /// <summary>Тема рядка леджера — «на чому» розбагатів чи спустив: голови причин гуртом, як їх бачить людина.</summary>
    static string Topic(string reason) => Head(reason) switch
    {
        "listen" => "📻 слухав радіо",
        "ad" => "📣 конкурс реклами",
        "play" or "win" or "draw" or "points" or "award" or "solo" => "🎲 партії",
        "stake" or "stake-win" or "stake-refund" => "🪙 ставки за столом",
        var h when h.StartsWith("table-", StringComparison.Ordinal) => "🃏 столи на черепки",
        var h when h.StartsWith("bet", StringComparison.Ordinal) => "🎲 ставки в Глека",
        var h when h.StartsWith("roulette", StringComparison.Ordinal) => "🎡 рулетка",
        var h when h.StartsWith("slot", StringComparison.Ordinal) => "🎰 слоти",
        var h when h.StartsWith("lelka", StringComparison.Ordinal) => "🐦 Лелека",
        "ach" => "🏅 ачівки",
        "daily" => "🧩 щоденки",
        "clicker" => "🏺 гончарне коло",
        var h when h is "shop" or "gift" || h.StartsWith("curse", StringComparison.Ordinal) => "🛍 Лавка",
        var h when h.StartsWith("ban", StringComparison.Ordinal) || h == "unban" => "🚫 бани треків",
        "liveads" => "🔥 прожарки",
        var h when h.StartsWith("buy", StringComparison.Ordinal) => "💵 купив за гривні",
        var h when h.StartsWith("sell", StringComparison.Ordinal) => "💵 продав за гривні",
        var h => h,
    };

    /// <summary>Ширина «кошика» точок на графіку: у кошику лишається остання зміна, щоб лінія не мала тисяч сходинок.</summary>
    static TimeSpan BourseBucket(string period) => period switch
    {
        "day" => TimeSpan.FromMinutes(15),
        "week" => TimeSpan.FromHours(1),
        "month" => TimeSpan.FromHours(3),
        _ => TimeSpan.FromHours(6),
    };

    /// <summary>Лінія балансу у вікні: точка на початку вікна (якщо гаманець уже був), зміни (останні в кошику) і «зараз».</summary>
    static List<long[]> TrackPts(Track t, DateTimeOffset since, DateTimeOffset now, TimeSpan bucket)
    {
        var pts = new List<long[]>();
        var i = t.Pts.FindLastIndex(x => x.At < since);
        if (i >= 0) pts.Add([Ms(since), t.Pts[i].Bal]);
        long lastBucket = long.MinValue;
        foreach (var (at, bal) in t.Pts)
        {
            if (at < since || at > now) continue;
            var b = at.UtcTicks / bucket.Ticks;
            if (b == lastBucket && pts.Count > 0) pts[^1] = [Ms(at), bal];
            else pts.Add([Ms(at), bal]);
            lastBucket = b;
        }
        if (pts.Count > 0) pts.Add([Ms(now), pts[^1][1]]);
        return pts;
    }

    /// <summary>GET /api/stats/bourse?period=&amp;nick= — 📈 Біржа: лінії топу за балансом (+ «я»), хто розбагатів і хто спустив.</summary>
    public object Bourse(string? period, string? nick)
    {
        var p = Norm(period);
        var me = Auth.NickKey(nick ?? "");
        var common = Cached("bourse:" + p, () =>
        {
            var now = clock.UtcNow;
            var since = WindowAt(p, now);
            var bank = BankOf();
            var raw = RawOf("all");
            var dj = DjKeys();
            var bucket = BourseBucket(p);
            var lines = bank.Tracks.Values.Where(t => !dj.Contains(t.Key))
                .Select(t => (t, Pts: TrackPts(t, since, now, bucket)))
                .Where(x => x.Pts.Count > 0)
                .OrderByDescending(x => x.t.Now).ThenBy(x => raw.Nick(x.t.Key), StringComparer.Ordinal)
                .ToList();

            var net = bank.Ledger.Where(l => l.At >= since && l.At <= now && !dj.Contains(l.Key)).GroupBy(l => l.Key)
                .Select(g => (Key: g.Key, Net: g.Sum(l => (long)l.Delta),
                    Topics: g.GroupBy(l => Topic(l.Reason)).Select(t => (Topic: t.Key, Sum: t.Sum(l => (long)l.Delta))).ToList()))
                .ToList();
            object Mover(string key, long sum, string topic, long topicSum) =>
                new { nick = raw.Nick(key), net = sum, topic, topicNet = topicSum, now = bank.Tracks.TryGetValue(key, out var t) ? t.Now : 0 };
            var up = net.Where(x => x.Net > 0).OrderByDescending(x => x.Net).Take(3)
                .Select(x => x.Topics.MaxBy(y => y.Sum) is var best ? Mover(x.Key, x.Net, best.Topic, best.Sum) : null).ToList();
            var down = net.Where(x => x.Net < 0).OrderBy(x => x.Net).Take(3)
                .Select(x => x.Topics.MinBy(y => y.Sum) is var worst ? Mover(x.Key, x.Net, worst.Topic, worst.Sum) : null).ToList();
            var gap = BourseGap();
            return new BourseData(p, lines.Select(x => (x.t.Key, raw.Nick(x.t.Key), x.t.Now, x.Pts)).ToList(), up, down, gap.People, gap.Shards);
        });
        var shown = common.Lines.Take(BourseLines).ToList();
        if (me.Length > 0 && !shown.Any(l => l.Key == me) && common.Lines.FirstOrDefault(l => l.Key == me) is { Key: not null } mine)
            shown.Add(mine);
        return new
        {
            period = p,
            series = shown.Select(l => new { nick = l.Nick, now = l.Now, pts = l.Pts }),
            people = common.Lines.Count,
            up = common.Up,
            down = common.Down,
            gap = new { people = common.GapPeople, shards = common.GapShards },
        };
    }

    sealed record BourseData(string Period, List<(string Key, string Nick, int Now, List<long[]> Pts)> Lines, List<object> Up,
        List<object> Down, int GapPeople, long GapShards);

    // =============================================================================================
    // 📊 Ело в часі — реплей
    // =============================================================================================

    /// <summary>Одна рейтингова партія очима гравця: Ело й лічильник партій після неї.</summary>
    public sealed record EloPoint(DateTimeOffset At, int Elo, int Games, double Score);

    sealed class EloBook
    {
        /// <summary>гра → ключ ніка → точки за зростанням часу.</summary>
        public Dictionary<string, Dictionary<string, List<EloPoint>>> Games = new(StringComparer.Ordinal);
        public Dictionary<string, int> Rounds = new(StringComparer.Ordinal);
        public Dictionary<string, string> Nicks = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Реплей Ело: партії рейтингових ігор на двох (<c>Rated</c> і <c>MaxPlayers == 2</c>, як у <c>Rewards.Elo</c>) у тому
    /// порядку, в якому вони записались, від стартової <see cref="Ratings.Start"/>; новий рейтинг — <see cref="Ratings.Next"/>,
    /// тобто та сама формула й ті самі K, що рахували живий <c>ratings</c>. Перемога — 1, нічия чи «нема переможця» — ½.
    /// </summary>
    EloBook EloOf() => Cached("ch:elo", () =>
    {
        var book = new EloBook();
        var rounds = new Dictionary<(string Room, int Round), List<(string Game, string Key, string Nick, string Outcome, DateTimeOffset At)>>();
        var order = new List<(string, int)>();
        db.With(c =>
        {
            using var cmd = Cmd(c, "SELECT room_id, round, game, nick_key, nick, outcome, created_at FROM game_results WHERE outcome <> 'solo' ORDER BY id");
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var game = r.GetString(2);
                if (names.Get(game) is not { Rated: true, MaxPlayers: 2 }) continue;
                var k = (r.GetString(0), r.GetInt32(1));
                if (!rounds.TryGetValue(k, out var list)) { rounds[k] = list = []; order.Add(k); }
                list.Add((game, r.GetString(3), r.GetString(4), r.GetString(5), Ts(r.GetString(6))));
            }
        });
        var state = new Dictionary<(string Game, string Key), (int Elo, int Games)>();
        foreach (var k in order)
        {
            var rows = rounds[k];
            if (rows.Count != 2 || rows[0].Key == rows[1].Key) continue;
            var (a, b) = (rows[0], rows[1]);
            var scoreA = a.Outcome == "draw" || b.Outcome == "draw" ? 0.5
                : a.Outcome == "win" ? 1.0 : b.Outcome == "win" ? 0.0 : 0.5;
            var ra = state.GetValueOrDefault((a.Game, a.Key), (Elo: Ratings.Start, Games: 0));
            var rb = state.GetValueOrDefault((b.Game, b.Key), (Elo: Ratings.Start, Games: 0));
            (int Elo, int Games) na = (Ratings.Next(ra.Elo, ra.Games, scoreA, rb.Elo), ra.Games + 1);
            (int Elo, int Games) nb = (Ratings.Next(rb.Elo, rb.Games, 1 - scoreA, ra.Elo), rb.Games + 1);
            state[(a.Game, a.Key)] = na;
            state[(b.Game, b.Key)] = nb;
            if (!book.Games.TryGetValue(a.Game, out var g)) book.Games[a.Game] = g = new(StringComparer.Ordinal);
            void Add(string key, string nick, (int Elo, int Games) v, double s, DateTimeOffset at)
            {
                if (!g.TryGetValue(key, out var l)) g[key] = l = [];
                l.Add(new EloPoint(at, v.Elo, v.Games, s));
                book.Nicks[key] = nick.Trim();
            }
            Add(a.Key, a.Nick, na, scoreA, a.At);
            Add(b.Key, b.Nick, nb, 1 - scoreA, b.At);
            book.Rounds[a.Game] = book.Rounds.GetValueOrDefault(a.Game) + 1;
        }
        return book;
    });

    /// <summary>Чим закінчився реплей: (гра, ключ ніка) → Ело й партії. Має збігатися з <c>ratings</c> для рейтингових ігор.</summary>
    public IReadOnlyDictionary<(string Game, string Key), (int Elo, int Games)> EloReplay() =>
        EloOf().Games.SelectMany(g => g.Value.Select(p => (Key: (g.Key, p.Key), Last: p.Value[^1])))
            .ToDictionary(x => x.Key, x => (x.Last.Elo, x.Last.Games));

    /// <summary>
    /// GET /api/stats/elo?game=&amp;period= — 📊 Ело в часі: ігри з Ело й хоча б двома людьми, лінії тих, хто грав у вікні
    /// (точка на початку вікна — рейтинг, з яким людина в нього зайшла), і таблиця: зараз, зміна, пік, партії.
    /// </summary>
    public object EloChart(string? game, string? period)
    {
        var p = Norm(period);
        return Cached("elo:" + p + ":" + (game ?? ""), () =>
        {
            var book = EloOf();
            var raw = RawOf("all");
            string NickOf(string key) => raw.Nicks.ContainsKey(key) ? raw.Nick(key) : book.Nicks.GetValueOrDefault(key, key);
            var games = book.Games.Where(g => g.Value.Count >= 2)
                .OrderByDescending(g => book.Rounds.GetValueOrDefault(g.Key)).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new { id = g.Key, title = names.Title(g.Key), people = g.Value.Count, rounds = book.Rounds.GetValueOrDefault(g.Key) })
                .ToList();
            var id = games.Any(g => g.id == game) ? game! : games.FirstOrDefault()?.id;
            if (id is null) return (object)new { period = p, games, game = (string?)null, series = Array.Empty<object>(), rows = Array.Empty<object>() };

            var now = clock.UtcNow;
            var since = WindowAt(p, now);
            var people = book.Games[id].Select(kv =>
            {
                var all = kv.Value;
                var inWin = all.Where(x => x.At >= since).ToList();
                var before = all.LastOrDefault(x => x.At < since);
                var start = before?.Elo ?? Ratings.Start;
                var pts = new List<long[]>();
                if (inWin.Count > 0)
                {
                    pts.Add([Ms(before is null ? inWin[0].At.AddSeconds(-1) : since), start]);
                    pts.AddRange(inWin.Select(x => new long[] { Ms(x.At), x.Elo }));
                    pts.Add([Ms(now), inWin[^1].Elo]);
                }
                return new
                {
                    nick = NickOf(kv.Key), elo = all[^1].Elo, delta = inWin.Count > 0 ? inWin[^1].Elo - start : 0, peak = all.Max(x => x.Elo),
                    games = inWin.Count, wins = inWin.Count(x => x.Score == 1), draws = inWin.Count(x => x.Score == 0.5),
                    losses = inWin.Count(x => x.Score == 0), pts,
                };
            }).Where(x => x.games > 0).OrderByDescending(x => x.elo).ThenBy(x => x.nick, StringComparer.Ordinal).ToList();
            return new
            {
                period = p, games, game = id, title = names.Title(id),
                series = people.OrderByDescending(x => x.games).Take(EloLines + 4).Select(x => new { nick = x.nick, pts = x.pts }),
                rows = people.Select(x => new { x.nick, x.elo, x.delta, x.peak, x.games, x.wins, x.draws, x.losses }),
            };
        });
    }

    // =============================================================================================
    // 📖 Книга рекордів
    // =============================================================================================

    /// <summary>Заявка на рекорд: хто, скільки, коли (і що саме — <see cref="Note"/>).</summary>
    sealed record Entry(string Key, double Value, DateTimeOffset At, string? Note = null);

    /// <summary>
    /// Хто тримає рекорд і кого перебили: заявки по черзі в часі, рекорд — лише строго більше за поточний; рівне —
    /// співтримач. <c>Prev</c> — останній тримач до нинішнього, якщо це інша людина (свій рекорд перебив сам — тоді
    /// тримач перед ним).
    /// </summary>
    static (List<Entry> Top, Entry? Prev) Crown(IEnumerable<Entry> entries)
    {
        var top = new List<Entry>();
        Entry? prev = null;
        foreach (var e in entries.Where(e => e.Value > 0).OrderBy(e => e.At).ThenBy(e => e.Key, StringComparer.Ordinal))
        {
            if (top.Count == 0 || e.Value > top[0].Value)
            {
                // співтримач, що пішов у відрив, перебив не себе, а іншого співтримача
                if (top.LastOrDefault(t => t.Key != e.Key) is { } other) prev = other;
                top = [e];
            }
            else if (e.Value == top[0].Value && top.All(t => t.Key != e.Key)) top.Add(e);
        }
        return (top, prev);
    }

    sealed record Rec(string Key, string Icon, string Title, string What, List<string> Nicks, double Value, string Text,
        DateTimeOffset At, string? Note, bool Fresh, List<string>? PrevNicks, string? PrevText, DateTimeOffset? PrevAt, bool Site = false);

    static string Shards(long n) => Count(n, "черепок", "черепки", "черепків");

    /// <summary>Виграш — голова «win», «…-win» чи «…-prize» (банк ставки, приз турніру, рулетка, слоти, ставки в Глека).</summary>
    static bool IsWin(string head) => head == "win" || head.EndsWith("-win", StringComparison.Ordinal) || head.EndsWith("-prize", StringComparison.Ordinal)
        || head == "slot-jackpot";

    static bool IsCasino(string head) => head.StartsWith("roulette", StringComparison.Ordinal) || head.StartsWith("slot", StringComparison.Ordinal);

    List<Rec> RecordList() => Cached("records", () =>
    {
        var now = clock.UtcNow;
        var raw = RawOf("all");
        var bank = BankOf();
        var dj = DjKeys();
        var list = new List<Rec>();
        void Add(string key, string icon, string title, string what, IEnumerable<Entry> entries, Func<double, string> text,
            bool withPrev = true, bool site = false, Func<string, string>? who = null)
        {
            var (top, prev) = Crown(site ? entries : entries.Where(e => !dj.Contains(e.Key)));
            if (top.Count == 0) return;
            who ??= raw.Nick;
            var at = top.Max(t => t.At);
            list.Add(new Rec(key, icon, title, what, top.Take(3).Select(t => who(t.Key)).ToList(), top[0].Value, text(top[0].Value),
                top[0].At, top[0].Note, now - at <= RecordFresh,
                withPrev && prev is not null ? [who(prev.Key)] : null, withPrev ? prev is null ? null : text(prev.Value) : null,
                withPrev ? prev?.At : null, site));
        }
        string Tail(string reason) => reason.IndexOf(':') is var i and >= 0 ? reason[(i + 1)..] : "";
        string Source(string reason) => Head(reason) switch
        {
            var h when h.StartsWith("roulette", StringComparison.Ordinal) => "рулетка",
            "slot-jackpot" => "Скарбничка Глека",
            var h when h.StartsWith("bet", StringComparison.Ordinal) => "ставка в Глека",
            "stake-win" => "банк за ставку — " + names.Title(Tail(reason)),
            "table-prize" => "приз турніру — " + names.Title(Tail(reason)),
            _ => names.Title(Tail(reason)),
        };
        var kyivDay = (DateTimeOffset t) => Days.Of(t);
        var dayStart = (string d) => Daily.StartOfDayUtc(d);

        // 💰 виграші
        var wins = bank.Ledger.Where(l => l.Delta > 0 && IsWin(Head(l.Reason))).ToList();
        Add("win", "💰", "Найбільший разовий виграш", "один виграш за раз: партія, ставка, приз, рулетка чи слоти",
            wins.Select(l => new Entry(l.Key, l.Delta, l.At, Source(l.Reason))), v => Shards((long)v));
        Add("casino", "🎰", "Зірвав банк у казино", "найбільший разовий виграш у рулетку чи слоти",
            wins.Where(l => IsCasino(Head(l.Reason))).Select(l => new Entry(l.Key, l.Delta, l.At, Source(l.Reason))), v => Shards((long)v));

        // 🔥 серія: заявка на кожну перемогу з довжиною серії на ту мить (нічия серію не рве, як BestStreak)
        var multi = raw.Results.Where(r => !r.Solo).ToList();
        var streaks = new List<Entry>();
        foreach (var g in multi.GroupBy(r => r.Key))
        {
            var run = 0;
            foreach (var r in g.OrderBy(r => r.At))
                if (r.Outcome == "win") streaks.Add(new Entry(g.Key, ++run, r.At, names.Title(r.Game)));
                else if (r.Outcome == "loss") run = 0;
        }
        Add("streak", "🔥", "Найдовша серія перемог", "перемоги поспіль за столами, нічия серію не рве", streaks,
            v => Count((long)v, "перемога поспіль", "перемоги поспіль", "перемог поспіль"));

        // по днях — заявка на кінець кожного дня (час — київська північ дня: рекорд дня свіжий, поки той день недавно)
        IEnumerable<Entry> PerDay<T>(IEnumerable<T> rows, Func<T, string> key, Func<T, DateTimeOffset> at) =>
            rows.GroupBy(x => (Key: key(x), Day: kyivDay(at(x)))).Select(g => new Entry(g.Key.Key, g.Count(), dayStart(g.Key.Day), g.Key.Day));
        Add("games-day", "🎲", "Найбільше партій за день", "партії за столами однієї людини за київську добу",
            PerDay(multi, r => r.Key, r => r.At), v => Count((long)v, "партія", "партії", "партій"));
        Add("chat-day", "💬", "Найбільше реплік за день", "балачки однієї людини за добу",
            PerDay(raw.Chat, m => m.Key, m => m.At), v => Count((long)v, "репліка", "репліки", "реплік"));
        Add("songs-day", "🎵", "Найбільше пісень за день", "скільки пісень людина закинула в ефір за добу",
            PerDay(raw.Requests, q => q.Key, q => q.At), v => Count((long)v, "пісня", "пісні", "пісень"));
        Add("long-day", "⏱", "Найдовший день на сайті", "скільки часу людина пробула на сайті за одну добу",
            raw.Time.Where(t => t.Place == "site").Select(t => new Entry(t.Key, t.Sec, dayStart(t.Day), t.Day)), Dur);
        Add("night", "🦉", "Нічний марафон", "найбільше дій (партії, репліки, пісні, ❤) з 00:00 до 05:00 за одну ніч",
            raw.Actions().Where(a => TimeZoneInfo.ConvertTime(a.At, Days.Kyiv).Hour < OwlUntil)
                .GroupBy(a => (a.Key, Day: kyivDay(a.At))).Select(g => new Entry(g.Key.Key, g.Count(), dayStart(g.Key.Day), g.Key.Day)),
            v => Count((long)v, "дія", "дії", "дій"));

        // 🌋 день сайту: тримач — сам день
        var rounds = multi.GroupBy(r => (r.Room, r.Round)).Select(g => kyivDay(g.First().At)).GroupBy(d => d).ToDictionary(g => g.Key, g => g.Count());
        var chats = raw.Chat.GroupBy(m => kyivDay(m.At)).ToDictionary(g => g.Key, g => g.Count());
        var songs = raw.Requests.GroupBy(q => kyivDay(q.At)).ToDictionary(g => g.Key, g => g.Count());
        var days = rounds.Keys.Concat(chats.Keys).Concat(songs.Keys).Distinct().ToList();
        Add("busy-day", "🌋", "Найжвавіший день сайту", "партії, репліки й пісні всіх разом за одну добу",
            days.Select(d => new Entry(d, rounds.GetValueOrDefault(d) + chats.GetValueOrDefault(d) + songs.GetValueOrDefault(d), dayStart(d),
                Count(rounds.GetValueOrDefault(d), "партія", "партії", "партій") + " · " + Count(chats.GetValueOrDefault(d), "репліка", "репліки", "реплік")
                + " · " + Count(songs.GetValueOrDefault(d), "пісня", "пісні", "пісень"))),
            v => Count((long)v, "подія", "події", "подій"), site: true, who: d => d);

        // 🤑 пік балансу — з біржі
        Add("rich", "🤑", "Найбагатший будь-коли", "найбільше черепків у гаманці одночасно",
            bank.Tracks.Values.SelectMany(t => t.Pts.Select(x => new Entry(t.Key, x.Bal, x.At))), v => Shards((long)v));

        // 📊 пік Ело — з реплею
        var elo = EloOf();
        Add("elo", "📊", "Найвище Ело", "найбільший рейтинг, до якого людина колись доходила в грі на двох",
            elo.Games.SelectMany(g => g.Value.SelectMany(kv => kv.Value.Select(x => new Entry(kv.Key, x.Elo, x.At, names.Title(g.Key))))),
            v => Num(v) + " Ело");

        // 💎 і ❤ — накопичуються з часом, тож «перебив» тут нема кого
        var pearl = raw.Chat.Where(m => m.Likes > 0).ToList();
        Add("pearl", "💎", "Найлайкнутіша репліка", "репліка в Балачках, яку вподобали найбільше",
            pearl.Select(m => new Entry(m.Key, m.Likes, m.At, Quote(m))), v => Count((long)v, "вподобайка", "вподобайки", "вподобайок"), withPrev: false);
        var hits = Hits();
        if (hits.Count > 0)
        {
            var top = hits.TakeWhile(h => h.Likes == hits[0].Likes).Take(3).ToList();
            list.Add(new Rec("hit", "❤", "Найлайкнутіша пісня", "трек з найбільшою кількістю ❤ — і хто його закинув першим",
                top.Select(h => h.By).OfType<string>().Distinct().ToList(), top[0].Likes, Count(top[0].Likes, "вподобайка", "вподобайки", "вподобайок"),
                top.Max(h => h.At), string.Join(" · ", top.Select(h => h.Title)), now - top.Max(h => h.At) <= RecordFresh, null, null, null));
        }

        // 🦄 рідкісні ачівки (є не більше ніж у RareHolders людей): заявка на кожну — скільки рідкісних у людини на ту мить
        var achs = Achs();
        var rare = achs.GroupBy(a => a.Ach).Where(g => g.Count() <= RareHolders).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        Add("rare", "🦄", "Найбільше рідкісних ачівок", "ачівки, які мають не більше ніж " + Count(RareHolders, "людина", "людини", "людей"),
            achs.Where(a => rare.Contains(a.Ach)).GroupBy(a => a.Key)
                .SelectMany(g => g.OrderBy(a => a.At).Select((a, i) => new Entry(g.Key, i + 1, a.At))),
            v => Count((long)v, "рідкісна ачівка", "рідкісні ачівки", "рідкісних ачівок"));
        return list;
    });

    static string Quote(Msg m)
    {
        var t = m.Text.Trim();
        if (t.Length == 0) return m.File is null ? "…" : "📎 файл";
        return t.Length > 140 ? t[..139] + "…" : t;
    }

    sealed record Hit(string Title, int Likes, string? By, DateTimeOffset At);

    /// <summary>Найлайкнутіші треки (без голосу Глека) і хто першим закинув кожен.</summary>
    List<Hit> Hits() => db.With(c =>
    {
        var list = new List<Hit>();
        using (var cmd = Cmd(c, """
            SELECT l.track_id, COALESCE(t.artist, ''), COALESCE(t.title, l.track_id), COUNT(*) n, MAX(l.created_at),
                   (SELECT p.requested_by FROM plays p WHERE p.track_id = l.track_id AND p.source = 'user' AND p.requested_by IS NOT NULL ORDER BY p.id LIMIT 1)
            FROM likes l LEFT JOIN tracks t ON t.id = l.track_id
            WHERE l.track_id NOT LIKE 'voice-%' GROUP BY l.track_id ORDER BY n DESC, MAX(l.created_at) LIMIT 5
            """))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var artist = r.GetString(1);
                var by = Str(r, 5);
                list.Add(new Hit((artist.Length > 0 ? artist + " — " : "") + r.GetString(2), r.GetInt32(3),
                    by is null || DjKeys().Contains(Auth.NickKey(by)) ? null : by.Trim(), Ts(r.GetString(4))));
            }
        return list;
    });

    List<(string Key, string Ach, DateTimeOffset At)> Achs() => db.With(c =>
    {
        var list = new List<(string, string, DateTimeOffset)>();
        using var cmd = Cmd(c, "SELECT nick_key, key, unlocked_at FROM achievements");
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1), Ts(r.GetString(2))));
        return list;
    });

    /// <summary>GET /api/stats/records — 📖 Книга рекордів Глечиків за весь час.</summary>
    public object Records() => new
    {
        records = RecordList().Select(r => new
        {
            key = r.Key, icon = r.Icon, title = r.Title, what = r.What, nicks = r.Site ? [] : r.Nicks, site = r.Site,
            day = r.Site ? r.Nicks.FirstOrDefault() : null,
            value = r.Value, text = r.Text, at = r.At, note = r.Note, fresh = r.Fresh,
            prev = r.PrevNicks is null ? null : new { nicks = r.Site ? [] : r.PrevNicks, day = r.Site ? r.PrevNicks.FirstOrDefault() : null, text = r.PrevText, at = r.PrevAt },
        }),
        freshDays = (int)RecordFresh.TotalDays,
    };

    /// <summary>
    /// Сигнатура Книги рекордів — для «🔔 нове»: міняється, коли рекорд перебили чи з'явився співтримач, і не міняється
    /// від того, що рекорд просто старіє.
    /// </summary>
    public string RecordsSig()
    {
        var s = string.Join("|", RecordList().Select(r => r.Key + "=" + string.Join(",", r.Nicks) + ":" + r.Value.ToString(CultureInfo.InvariantCulture)));
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..12].ToLowerInvariant();
    }
}
