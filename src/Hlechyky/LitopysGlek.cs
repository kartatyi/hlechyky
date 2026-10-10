using System.Globalization;
using System.Text.RegularExpressions;
using Hlechyky.Games;
using Hlechyky.Games.Economy;

namespace Hlechyky;

/// <summary>
/// «🏺 Глек» у «📊 Хто скільки»: як живе Дядько Глек — що ставить і на чиїх піснях учиться, хто його відкидає й чий
/// смак кращий, кого він смажить, що каже, які партії веде і що в його касі (Лавка, казино, роздане).
/// <para>
/// «Вибір Глека» — <c>plays.source = 'autodj'</c>; «ваші» — <c>source = 'user'</c> від людей (разом із узятими підказками
/// Глека: замовила людина). Його власні вставки (реклама, прожарки, новини — <c>requested_by</c> = Глек, трек <c>voice-*</c>)
/// — це його голос, а не пісні: у пісні не йдуть. Вподобайка належить відтворенню, яке грало (останньому до неї) —
/// у <c>likes</c> на трек лише одна від людини, тож «❤ на 100 треків» чесно порівнює і Глека, і людей; свої ж
/// замовлення людина лайкає сама — то не рахуємо.
/// </para>
/// Каса: <c>ledger</c> — це гаманці людей, а Глек — друга сторона кожного запису: що людина віддала, те в нього, що
/// дістала — те він роздав. Тож усе з його боку — з мінусом.
/// </summary>
public sealed partial class Litopys
{
    /// <summary>Ігри, де Дядько Глек — ведучий: веде Мафію, читає Додепи й Байкарів, коментує «Скільки?», роздає ролі в Шпигуні.</summary>
    public static readonly string[] GlekHosts = ["mafia", "dotepy", "bluff", "skilky", "spy"];

    /// <summary>Скільки треків має замовити людина, щоб потрапити в «👅 Чий смак кращий»: на трьох треках ❤ — то лотерея.</summary>
    public static int TasteMin(string period) => period switch { "day" => 3, "week" => 5, "month" => 10, _ => 20 };

    /// <summary>Скільки рядків у списках вкладки.</summary>
    public const int GlekRows = 8;

    /// <summary>Скільки пісень у «🏆 Хітах», «💩 Провалах» і насінні.</summary>
    public const int GlekTop = 5;

    /// <summary>Одне відтворення довше за це — сервер стояв чи плеєр завис: більше не зараховуємо.</summary>
    static readonly TimeSpan PlayCap = TimeSpan.FromMinutes(20);

    /// <summary>Коротша фраза — то рахунок раунду («Найближче — Оля: різниця 9»), а не Глек.</summary>
    const int QuoteMin = 36;

    const string SeedPrefix = "схоже на ";

    static partial void MapGlek(RouteGroupBuilder api) => api.MapGet("/glek", (string? period, Litopys l) => l.Glek(period));

    // =============================================================================================
    // Сирі дані Глека
    // =============================================================================================

    sealed record Play(long Id, string TrackId, string Source, string? ByKey, string? Reason, DateTimeOffset At, double Sec, bool Skipped,
        string? Artist, string? Title, bool Tip)
    {
        public bool Voice => TrackId.StartsWith("voice-", StringComparison.Ordinal);
        /// <summary>Ключ виконавця, як у <c>dj_feedback</c> (рахується раз: нормалізація недешева, а відтворень тисячі).</summary>
        public string? ArtistKey { get; } = string.IsNullOrWhiteSpace(Artist) ? null : AutoDj.ArtistKey(Artist);
        public string Label => string.IsNullOrWhiteSpace(Artist) ? Title ?? TrackId : $"{Artist} — {Title}";
    }
    sealed record Fb(string? Key, string Kind, string ArtistKey);
    sealed record Ad(string Kind, string? TargetKey, string? Target, string? BuyerKey, bool Anon, int Price, string Status, DateTimeOffset At);

    sealed class GlekRaw
    {
        public List<Play> Plays = [];
        public Dictionary<long, List<string>> LikesOf = [];   // відтворення → хто вподобав (ключі)
        public List<Fb> Feedback = [];
        public List<Ad> Ads = [];
        public Dictionary<string, int> Said = new(StringComparer.Ordinal);
        /// <summary>Як писати ключ ніка, якого нема в подіях «Літопису» (замовив, відкинув, замовив прожарку).</summary>
        public Dictionary<string, string> Nicks = new(StringComparer.Ordinal);

        public void Seen(string? key, string? nick)
        {
            if (key is { Length: > 0 } && !string.IsNullOrWhiteSpace(nick)) Nicks.TryAdd(key, nick.Trim());
        }
    }

    static bool HasTable(Microsoft.Data.Sqlite.SqliteConnection c, string name)
    {
        using var cmd = Cmd(c, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $n", ("$n", name));
        return cmd.ExecuteScalar() is not null;
    }

    GlekRaw LoadGlek(DateTimeOffset since)
    {
        var g = new GlekRaw();
        var s = Iso(since);
        var now = clock.UtcNow;
        var dj = DjKeys();
        db.With(c =>
        {
            using (var cmd = Cmd(c, """
                SELECT p.id, p.track_id, p.source, p.requested_by, p.reason, p.started_at, p.ended_at, p.skipped, p.duration_sec, t.artist, t.title, p.via
                FROM plays p INDEXED BY ix_plays_started LEFT JOIN tracks t ON t.id = p.track_id
                WHERE p.started_at >= $s AND p.source IN ('autodj', 'user') ORDER BY p.started_at
                """, ("$s", s)))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var at = Ts(r.GetString(5));
                    var dur = r.IsDBNull(8) ? 0 : r.GetInt32(8);
                    var end = r.IsDBNull(6) ? at.AddSeconds(Math.Min(dur, Math.Max(0, (now - at).TotalSeconds))) : Ts(r.GetString(6));
                    var sec = Math.Clamp((end - at).TotalSeconds, 0, PlayCap.TotalSeconds);
                    var by = Str(r, 3) is { } nick ? Auth.NickKey(nick) : null;
                    g.Seen(by, Str(r, 3));
                    g.Plays.Add(new Play(r.GetInt64(0), Str(r, 1) ?? "", r.GetString(2), by, Str(r, 4), at, sec, r.GetInt32(7) != 0,
                        Str(r, 9), Str(r, 10), Str(r, 11) == "suggestion"));
                }

            // Вподобайка — тому відтворенню цього треку, що почалось останнім до неї. Раніше за період — не наше.
            var byTrack = g.Plays.GroupBy(p => p.TrackId).ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);
            using (var cmd = Cmd(c, "SELECT track_id, nick, created_at FROM likes WHERE created_at >= $s", ("$s", s)))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    if (!byTrack.TryGetValue(r.GetString(0), out var list)) continue;
                    var at = Ts(r.GetString(2));
                    var play = list.LastOrDefault(p => p.At <= at);
                    if (play is null) continue;
                    if (!g.LikesOf.TryGetValue(play.Id, out var who)) g.LikesOf[play.Id] = who = [];
                    who.Add(Auth.NickKey(r.GetString(1)));
                }

            using (var cmd = Cmd(c, "SELECT nick, kind, artist_key FROM dj_feedback WHERE created_at >= $s", ("$s", s)))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var key = Str(r, 0) is { } nick ? Auth.NickKey(nick) : null;
                    g.Seen(key, Str(r, 0));
                    if (key is not null && (key.Length == 0 || dj.Contains(key))) key = null;
                    g.Feedback.Add(new Fb(key, r.GetString(1), r.GetString(2)));
                }

            if (HasTable(c, "live_ads"))
            {
                using var cmd = Cmd(c, """
                    SELECT kind, target_key, target, buyer_key, anon, price, status, created_at, buyer FROM live_ads WHERE created_at >= $s
                    """, ("$s", s));
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var anon = r.GetInt32(4) != 0;
                    g.Seen(Str(r, 1), Str(r, 2));
                    if (!anon) g.Seen(Str(r, 3), Str(r, 8));   // анонімного замовника не пишемо навіть у словник ніків
                    g.Ads.Add(new Ad(r.GetString(0), Str(r, 1), Str(r, 2), Str(r, 3), anon, r.GetInt32(5), r.GetString(6),
                        Ts(r.GetString(7))));
                }
            }

            using (var cmd = Cmd(c, """
                SELECT kind, COUNT(*) FROM chat WHERE kind IN ('dj', 'dj-game', 'dj-auto') AND created_at >= $s GROUP BY kind
                """, ("$s", s)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) g.Said[r.GetString(0)] = r.GetInt32(1);
        });
        return g;
    }

    /// <summary>
    /// Хто надихає Глека: пісня-насіння («схоже на X — Y») → хто з людей замовляв її найчастіше (порівну — хто першим),
    /// за весь час. Пісні, яких ніхто з людей не замовляв, — його власні вибори: від них він сідиться сам.
    /// </summary>
    Dictionary<string, (string Key, string Nick)> Muses() => Cached("glek-muses", () => db.With(c =>
    {
        var dj = DjKeys();
        var best = new Dictionary<string, (string Key, string Nick, int N, string First)>(StringComparer.Ordinal);
        using var cmd = Cmd(c, """
            SELECT t.artist, t.title, p.requested_by, COUNT(*), MIN(p.started_at) FROM plays p JOIN tracks t ON t.id = p.track_id
            WHERE p.source = 'user' AND p.requested_by IS NOT NULL GROUP BY p.track_id, p.requested_by
            """);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var key = Auth.NickKey(r.GetString(2));
            if (key.Length == 0 || dj.Contains(key)) continue;
            var artist = r.GetString(0);
            var label = string.IsNullOrWhiteSpace(artist) ? r.GetString(1) : $"{artist} — {r.GetString(1)}";
            var (n, first) = (r.GetInt32(3), r.GetString(4));
            if (!best.TryGetValue(label, out var b) || n > b.N || (n == b.N && string.CompareOrdinal(first, b.First) < 0))
                best[label] = (key, r.GetString(2).Trim(), n, first);
        }
        return best.ToDictionary(kv => kv.Key, kv => (kv.Value.Key, kv.Value.Nick), StringComparer.Ordinal);
    }));

    // =============================================================================================
    // Каса
    // =============================================================================================

    /// <summary>Рядок каси: куди йде голова причини гаманця (група, пункт, значок, підпис).</summary>
    public sealed record KasaItem(string Group, string Item, string Icon, string Label);

    static readonly Dictionary<string, KasaItem> KasaMap = BuildKasaMap();

    static Dictionary<string, KasaItem> BuildKasaMap()
    {
        var m = new Dictionary<string, KasaItem>(StringComparer.Ordinal);
        void Put(KasaItem item, params string[] heads) { foreach (var h in heads) m[h] = item; }
        Put(new("earn", "lavka", "🛒", "Лавка, подарунки й прокльони"), "shop", "gift", "curse", "curse-ransom", "curse-reveal");
        Put(new("earn", "roast", "🔥", "прожарки на замовлення"), "liveads", "liveads-refund");
        Put(new("earn", "ban", "🚫", "бани й викупи треків"), "ban", "unban", "ban-refund");
        Put(new("casino", "roulette", "🎡", "Рулетка"), "roulette-bet", "roulette-win", "roulette-back");
        Put(new("casino", "slots", "🎰", "слоти"), "slot-bet", "slot-win", "slot-jackpot");
        Put(new("casino", "lelka", "🕊", "Лелека"), "lelka-bet", "lelka-win", "lelka-back");
        Put(new("casino", "bets", "🎲", "ставки на події й столи"), "bet", "bet-win", "bet-back");
        Put(new("tables", "tables", "🃏", "банк столів на черепки"),
            "stake", "stake-win", "stake-refund", "table-buyin", "table-fee", "table-cashout", "table-prize", "table-refund");
        Put(new("exchange", "uah", "💱", "черепки за гривні"), "buy", "buy-gift", "sell", "sell-back");
        Put(new("give", "listen", "🎧", "за слухання радіо"), "listen");
        Put(new("give", "games", "🎮", "за партії й перемоги"), "play", "win", "draw", "solo", "points");
        Put(new("give", "ach", "🏆", "за ачівки"), "ach");
        Put(new("give", "daily", "📅", "за щоденки"), "daily");
        Put(new("give", "clicker", "🏺", "обмін глеків із Гончарного кола"), "clicker");
        Put(new("give", "ads", "📣", "конкурс реклами"), "ad");
        Put(new("give", "award", "🎖", "нагороди за гру"), "award");
        return m;
    }

    /// <summary>Куди в касі йде причина гаманця; невідома голова — в «інше» під своєю назвою, щоб нічого не губилось.</summary>
    public static KasaItem KasaOf(string reason)
    {
        var head = Head(reason);
        return KasaMap.TryGetValue(head, out var item) ? item : new KasaItem("other", head, "❔", head);
    }

    static readonly (string Key, string Icon, string Label)[] KasaGroups =
    [
        ("earn", "💼", "Заробив"), ("casino", "🎰", "Казино"), ("tables", "🃏", "Столи"), ("exchange", "💱", "Обмінник"),
        ("give", "🎁", "Роздав"), ("other", "❔", "Інше"),
    ];

    object Kasa(Raw raw, string p)
    {
        var dj = DjKeys();
        var rows = raw.Ledger.Where(l => !dj.Contains(l.Key)).Select(l => (L: l, K: KasaOf(l.Reason))).ToList();
        var groups = KasaGroups.Select(gr =>
        {
            var mine = rows.Where(x => x.K.Group == gr.Key).ToList();
            var items = mine.GroupBy(x => x.K.Item).Select(x => new
            {
                key = x.Key, icon = x.First().K.Icon, label = x.First().K.Label,
                sum = -x.Sum(y => (long)y.L.Delta),
                bets = -x.Where(y => y.L.Delta < 0).Sum(y => (long)y.L.Delta),
                wins = x.Where(y => y.L.Delta > 0).Sum(y => (long)y.L.Delta),
                n = x.Count(),
            }).OrderByDescending(x => Math.Abs(x.sum)).ThenBy(x => x.key, StringComparer.Ordinal).ToList();
            return new { key = gr.Key, icon = gr.Icon, label = gr.Label, sum = items.Sum(x => x.sum), items };
        }).Where(gr => gr.items.Count > 0).ToList();

        // Хто заніс: витрати в «Заробив» плюс чистий програш у казино
        var people = rows.GroupBy(x => x.L.Key).Select(x => new
        {
            key = x.Key,
            earn = -x.Where(y => y.K.Group == "earn").Sum(y => (long)y.L.Delta),
            bets = -x.Where(y => y.K.Group == "casino" && y.L.Delta < 0).Sum(y => (long)y.L.Delta),
            wins = x.Where(y => y.K.Group == "casino" && y.L.Delta > 0).Sum(y => (long)y.L.Delta),
            casinoRows = x.Count(y => y.K.Group == "casino"),
        }).ToList();
        var payers = people.Select(x => new { nick = raw.Nick(x.key), x.earn, casino = Math.Max(0, x.bets - x.wins), sum = x.earn + Math.Max(0, x.bets - x.wins) })
            .Where(x => x.sum > 0).OrderByDescending(x => x.sum).ThenBy(x => x.nick, StringComparer.Ordinal).Take(GlekRows).ToList();
        var gamblers = people.Where(x => x.casinoRows > 0)
            .Select(x => new { nick = raw.Nick(x.key), x.bets, x.wins, net = x.wins - x.bets })
            .OrderByDescending(x => x.net).ThenBy(x => x.nick, StringComparer.Ordinal).ToList();

        return new
        {
            total = groups.Sum(x => x.sum),
            groups,
            payers,
            gamblers,
            series = KasaSeries(rows.Select(x => (x.L, Casino: x.K.Group == "casino")).ToList(), raw, p),
        };
    }

    /// <summary>
    /// Каса в часі: за день — по годинах, інакше — по днях (київських); за весь час — наростаючим підсумком, інакше —
    /// скільки за кожне відро. Точка — [мс початку відра, усе разом, казино].
    /// </summary>
    object KasaSeries(List<(Led L, bool Casino)> rows, Raw raw, string p)
    {
        var hourly = p == "day";
        var cumulative = p == "all";
        DateTimeOffset BucketOf(DateTimeOffset at)
        {
            var k = TimeZoneInfo.ConvertTime(at, Days.Kyiv);
            if (hourly) k = new DateTimeOffset(k.Year, k.Month, k.Day, k.Hour, 0, 0, k.Offset);
            else return Daily.StartOfDayUtc(Days.Of(at));
            return k.ToUniversalTime();
        }
        var now = clock.UtcNow;
        var first = raw.FirstDay is { } fd ? Daily.StartOfDayUtc(fd)
            : rows.Count > 0 ? BucketOf(rows.Min(x => x.L.At)) : BucketOf(now);
        var sums = rows.GroupBy(x => BucketOf(x.L.At))
            .ToDictionary(g => g.Key, g => (All: -g.Sum(x => (long)x.L.Delta), Casino: -g.Where(x => x.Casino).Sum(x => (long)x.L.Delta)));
        var pts = new List<long[]>();
        long all = 0, casino = 0;
        var last = BucketOf(now);
        for (var b = BucketOf(first); b <= last; b = hourly ? b.AddHours(1) : Daily.StartOfDayUtc(Days.Of(b.AddHours(36))))
        {
            var v = sums.GetValueOrDefault(b);
            if (cumulative) { all += v.All; casino += v.Casino; } else (all, casino) = v;
            pts.Add([b.ToUnixTimeMilliseconds(), all, casino]);
        }
        return new { unit = hourly ? "hour" : "day", cumulative, pts };
    }

    // =============================================================================================
    // Цитата дня
    // =============================================================================================

    static readonly Regex Private = new(@"\d[\d \-]{6,}\d|https?://|@\w|\.(com|ua|net|org)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Sentence = new(@"(?<=[.!?…])\s+", RegexOptions.CultureInvariant);

    /// <summary>Стабільний хеш рядка (string.GetHashCode щозапуску інший) — зерно, однакове для всіх.</summary>
    public static uint Fnv(string s)
    {
        var h = 2166136261u;
        foreach (var ch in s) h = (h ^ ch) * 16777619u;
        return h;
    }

    /// <summary>
    /// «🗯 Цитата Глека»: одна його фраза з Балачок (його слова за столами) чи з прожарок, що пішли в ефір. Пул — усе до
    /// сьогоднішньої київської півночі, зерно — дата: протягом дня однакова для всіх, завтра — інша. Без посилань, пошти
    /// й довгих чисел (телефон, картка). З прожарки — речення-два, без шапки «Прожарка від Дядька Глека».
    /// </summary>
    object? Quote() => Cached("glek-quote", () =>
    {
        var today = Days.Today(clock);
        var before = Iso(Daily.StartOfDayUtc(today));
        var pool = new List<(string Text, string At, string From)>();
        db.With(c =>
        {
            using (var cmd = Cmd(c, """
                SELECT text, created_at FROM chat WHERE kind IN ('dj', 'dj-game') AND deleted_at IS NULL AND created_at < $b ORDER BY id
                """, ("$b", before)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) pool.Add((r.GetString(0).Trim(), r.GetString(1), "chat"));
            if (!HasTable(c, "live_ads")) return;
            using (var cmd = Cmd(c, """
                SELECT text, COALESCE(played_at, created_at) FROM live_ads
                WHERE kind = 'roast' AND status = 'aired' AND created_at < $b ORDER BY id
                """, ("$b", before)))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var parts = Sentence.Split(Tidy(r.GetString(0)));
                    // по два речення: «Глек не каже, що це погано. Глек каже, що це статистика.»
                    for (var i = 0; i + 1 < parts.Length; i += 2)
                        pool.Add((parts[i] + " " + parts[i + 1], r.GetString(1), "roast"));
                }
        });
        var ok = pool.Where(x => x.Text.Length is >= QuoteMin and <= 200 && !Private.IsMatch(x.Text)
                                 && !x.Text.StartsWith("Прожарка від", StringComparison.Ordinal)
                                 && !x.Text.StartsWith("Хвилинка статистики", StringComparison.Ordinal)).ToList();
        if (ok.Count == 0) return (object)Nobody;
        var q = ok[(int)(Fnv(today) % (uint)ok.Count)];
        return new { text = q.Text, at = q.At, from = q.From, day = today };
    }) is var v && ReferenceEquals(v, Nobody) ? null : v;

    /// <summary>Мітки для голосу («[ба-дум-тсс]») у тексті — барабан, щоб читалось.</summary>
    static string Tidy(string text) => Regex.Replace(text, @"\s*\[[^\]]{1,30}\]\s*", " 🥁 ").Trim();

    /// <summary>Остання прожарка, що пішла в ефір (за весь час — щоб було що почитати й у тихий день).</summary>
    object? FreshRoast() => db.With(c =>
    {
        if (!HasTable(c, "live_ads")) return null;
        using var cmd = Cmd(c, """
            SELECT target, text, COALESCE(played_at, created_at), seconds FROM live_ads
            WHERE kind = 'roast' AND status = 'aired' AND text <> '' ORDER BY COALESCE(played_at, created_at) DESC, id DESC LIMIT 1
            """);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new { target = Str(r, 0), text = Tidy(r.GetString(1)), at = r.GetString(2), sec = r.GetInt32(3) } : (object?)null;
    });

    // =============================================================================================
    // GET /api/stats/glek
    // =============================================================================================

    /// <summary>GET /api/stats/glek?period= — усе для вкладки «🏺 Глек» одним запитом.</summary>
    public object Glek(string? period)
    {
        var p = Norm(period);
        return Cached("glek:" + p, () => BuildGlek(p));
    }

    object BuildGlek(string p)
    {
        var raw = RawOf(p);
        var g = LoadGlek(raw.Since);
        var dj = DjKeys();
        string Nick(string key) => raw.Nicks.TryGetValue(key, out var n) || g.Nicks.TryGetValue(key, out n) ? n : key;

        var glekPlays = g.Plays.Where(x => x.Source == "autodj").ToList();
        var peoplePlays = g.Plays.Where(x => x.Source == "user" && !x.Voice && x.ByKey is { Length: > 0 } k && !dj.Contains(k)).ToList();
        var voice = g.Plays.Where(x => x.Source == "user" && x.Voice && x.ByKey is { } k && dj.Contains(k)).ToList();
        // чужі вподобайки: своє замовлення людина лайкає сама — то не оцінка смаку
        int LikesOn(Play x) => g.LikesOf.TryGetValue(x.Id, out var who) ? who.Count(w => w != x.ByKey) : 0;

        // ---------- 🎛 діджей ----------
        var air = new
        {
            glek = glekPlays.Count,
            people = peoplePlays.Count,
            glekSec = (long)glekPlays.Sum(x => x.Sec),
            peopleSec = (long)peoplePlays.Sum(x => x.Sec),
            glekSkips = glekPlays.Count(x => x.Skipped),
            peopleSkips = peoplePlays.Count(x => x.Skipped),
            glekLikes = glekPlays.Sum(LikesOn),
            peopleLikes = peoplePlays.Sum(LikesOn),
            tips = peoplePlays.Count(x => x.Tip),
            rejects = g.Feedback.Count,
        };

        var muses = Muses();
        foreach (var m in muses.Values) g.Seen(m.Key, m.Nick);
        var seeded = glekPlays.Where(x => x.Reason?.StartsWith(SeedPrefix, StringComparison.Ordinal) == true)
            .Select(x => x.Reason![SeedPrefix.Length..]).ToList();
        var seedPeople = seeded.Where(muses.ContainsKey).GroupBy(l => muses[l].Key)
            .Select(x => new { nick = Nick(x.Key), n = x.Count() })
            .OrderByDescending(x => x.n).ThenBy(x => x.nick, StringComparer.Ordinal).Take(GlekRows).ToList();
        var seeds = new
        {
            people = seedPeople,
            self = seeded.Count(l => !muses.ContainsKey(l)),
            archive = glekPlays.Count(x => x.Reason == "з нашого архіву"),
            top = seeded.GroupBy(l => l, StringComparer.Ordinal)
                .Select(x => new { label = x.Key, n = x.Count(), nick = muses.TryGetValue(x.Key, out var m) ? Nick(m.Key) : null })
                .OrderByDescending(x => x.n).ThenBy(x => x.label, StringComparer.Ordinal).Take(GlekTop).ToList(),
        };

        var hits = glekPlays.GroupBy(x => x.TrackId)
            .Select(x => new { label = x.First().Label, likes = x.Sum(LikesOn), plays = x.Count() })
            .Where(x => x.likes > 0).OrderByDescending(x => x.likes).ThenByDescending(x => x.plays).ThenBy(x => x.label, StringComparer.Ordinal)
            .Take(GlekTop).ToList();

        // Провали — виконавці, яких Глек пхав, а ви відкидали («не те» в черзі й швидкий скіп)
        var byArtist = glekPlays.Where(x => x.ArtistKey is not null).GroupBy(x => x.ArtistKey!).ToList();
        var artistName = byArtist
            .ToDictionary(x => x.Key, x => x.GroupBy(y => y.Artist!.Trim()).OrderByDescending(y => y.Count()).First().Key, StringComparer.Ordinal);
        var artistPlays = byArtist.ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var flops = g.Feedback.GroupBy(f => f.ArtistKey)
            .Select(x => new
            {
                artist = artistName.GetValueOrDefault(x.Key) ?? x.Key,
                n = x.Count(), skip = x.Count(f => f.Kind == "skip"), dismiss = x.Count(f => f.Kind == "dismiss"),
                plays = artistPlays.GetValueOrDefault(x.Key),
            })
            .OrderByDescending(x => x.n).ThenBy(x => x.artist, StringComparer.Ordinal).Take(GlekTop).ToList();

        var artists = byArtist
            .Select(x => new { artist = artistName[x.Key], n = x.Count(), likes = x.Sum(LikesOn) })
            .OrderByDescending(x => x.n).ThenBy(x => x.artist, StringComparer.Ordinal).Take(GlekRows).ToList();

        // ---------- ⚔ проти вас ----------
        var critics = g.Feedback.Where(f => f.Key is not null).GroupBy(f => f.Key!)
            .Select(x => new { nick = Nick(x.Key), n = x.Count(), skip = x.Count(f => f.Kind == "skip"), dismiss = x.Count(f => f.Kind == "dismiss") })
            .OrderByDescending(x => x.n).ThenBy(x => x.nick, StringComparer.Ordinal).Take(GlekRows).ToList();

        var min = TasteMin(p);
        var tasteAll = peoplePlays.GroupBy(x => x.ByKey!)
            .Select(x => new { key = x.Key, plays = x.Count(), likes = x.Sum(LikesOn), skips = x.Count(y => y.Skipped) }).ToList();
        object Row(string? nick, int plays, int likes, int skips, bool glek) => new
        {
            nick, glek, plays, likes, skips,
            per100 = plays == 0 ? 0 : Math.Round(likes * 100.0 / plays, 1),
            skipPct = plays == 0 ? 0 : (int)Math.Round(skips * 100.0 / plays),
        };
        var taste = tasteAll.Where(x => x.plays >= min)
            .Select(x => (Per: x.plays == 0 ? 0 : (double)x.likes / x.plays, Row: Row(Nick(x.key), x.plays, x.likes, x.skips, false)))
            .Append((Per: glekPlays.Count == 0 ? -1 : (double)air.glekLikes / glekPlays.Count,
                Row: Row(site.CurrentValue.DjName, glekPlays.Count, air.glekLikes, air.glekSkips, true)))
            .Where(x => x.Per >= 0)
            .OrderByDescending(x => x.Per).Select(x => x.Row).ToList();
        var fewTaste = tasteAll.Count(x => x.plays < min);

        // ---------- 🔥 прожарки й балачки ----------
        var roasts = g.Ads.Where(a => a.Kind == "roast" && a.Status != "failed").ToList();
        var targets = roasts.Where(a => !string.IsNullOrEmpty(a.TargetKey) || !string.IsNullOrEmpty(a.Target))
            .GroupBy(a => a.TargetKey is { Length: > 0 } k ? k : Auth.NickKey(a.Target!))
            .Select(x => new { nick = Nick(x.Key), written = x.Count(), aired = x.Count(a => a.Status == "aired") })
            .OrderByDescending(x => x.written).ThenByDescending(x => x.aired).ThenBy(x => x.nick, StringComparer.Ordinal).Take(GlekRows).ToList();
        var paid = g.Ads.Where(a => a.Price > 0 && a.Status != "failed").ToList();
        var buyers = paid.Where(a => !a.Anon && a.BuyerKey is { Length: > 0 }).GroupBy(a => a.BuyerKey!)
            .Select(x => new { nick = Nick(x.Key), n = x.Count(), shards = x.Sum(a => a.Price) })
            .OrderByDescending(x => x.shards).ThenBy(x => x.nick, StringComparer.Ordinal).Take(GlekRows).ToList();
        var anon = paid.Where(a => a.Anon).ToList();
        var hosted = raw.Results.Where(r => !r.Solo && GlekHosts.Contains(r.Game)).GroupBy(r => r.Game)
            .Select(x => new { game = x.Key, title = names.Title(x.Key), rounds = x.Select(r => (r.Room, r.Round)).Distinct().Count() })
            .OrderByDescending(x => x.rounds).ThenBy(x => x.game, StringComparer.Ordinal).ToList();
        var talk = new
        {
            roasts = roasts.Count,
            aired = roasts.Count(a => a.Status == "aired"),
            targets,
            buyers,
            anon = new { n = anon.Count, shards = anon.Sum(a => a.Price) },
            said = g.Said.GetValueOrDefault("dj") + g.Said.GetValueOrDefault("dj-game"),
            announced = g.Said.GetValueOrDefault("dj-auto"),
            voice = voice.Count,
            voiceSec = (long)voice.Sum(x => x.Sec),
            hosted,
            fresh = FreshRoast(),
            quote = Quote(),
        };

        return new
        {
            period = p,
            dj = site.CurrentValue.DjName,
            day = Days.Today(clock),
            tasteMin = min,
            air,
            seeds,
            hits,
            flops,
            artists,
            critics,
            taste,
            fewTaste,
            talk,
            kasa = Kasa(raw, p),
        };
    }

}
