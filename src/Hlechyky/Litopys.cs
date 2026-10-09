using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Hlechyky;

/// <summary>
/// «Літопис» — що за період сталося в Глечиках. «✨ Огляд» у «📊 Хто скільки»: підсумок із розкладкою по днях, звання
/// (і похвали, і підколи), «⚔ Хто кого», «🕐 Коли ми тусимо», «💎 Перл» Балачок і рідкісні ачівки; «🎮 Усі ігри» над
/// таблицями; «🎤 Виконавці» й «🎧 Хто слухає» в музиці; «✨ Цікавинки» в профілі.
/// <para>
/// Сирі події періоду (партії, репліки, замовлення, вподобайки, гаманець, час) читаємо одним махом і рахуємо в пам'яті:
/// їх тисячі, а не мільйони, і так звання, суперництва й теплова карта бачать одні й ті самі рядки. Готова відповідь
/// живе <see cref="CacheFor"/>: «Хто скільки» відкривають гуртом, а база тим часом пише живий ефір.
/// </para>
/// Години й дні тижня — київські (<see cref="Days.Kyiv"/>), періоди — <see cref="Periods"/>, як і в решті «Хто скільки».
/// </summary>
public sealed partial class Litopys(Db db, GameNames names, IClock clock, IOptionsMonitor<SiteOptions> site)
{
    /// <summary>Скільки живе порахована відповідь.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    /// <summary>Скільки пар у «Хто кого».</summary>
    public const int RivalRows = 8;

    /// <summary>Ачівка рідкісна, коли її мають не більше стількох людей.</summary>
    public const int RareHolders = 2;

    /// <summary>Скільки рідкісних ачівок показуємо.</summary>
    public const int RareRows = 10;

    /// <summary>Скільки виконавців у «🎤 Виконавці».</summary>
    public const int ArtistRows = 12;

    /// <summary>Скільки людей у «🎧 Хто слухає» й «❤ Хто лайкає».</summary>
    public const int PeopleRows = 20;

    /// <summary>Нічна сова — дії з 00:00 до 04:59 за Києвом; ранній птах — з 05:00 до 08:59.</summary>
    public const int OwlUntil = 5, LarkUntil = 9;

    static readonly CultureInfo Uk = CultureInfo.GetCultureInfo("uk-UA");

    readonly ConcurrentDictionary<string, (DateTimeOffset At, object Value)> _cache = new(StringComparer.Ordinal);

    T Cached<T>(string key, Func<T> make) where T : notnull
    {
        var now = clock.UtcNow;
        if (_cache.TryGetValue(key, out var c) && now >= c.At && now - c.At < CacheFor && c.Value is T hit) return hit;
        var v = make();
        _cache[key] = (now, v);
        return v;
    }

    /// <summary>Невідомий чи порожній період — «за весь час», як таблиці ігор.</summary>
    static string Norm(string? period) => Periods.Known(period) ? period! : "all";

    // =============================================================================================
    // Сирі події
    // =============================================================================================

    sealed record Res(string Room, int Round, string Game, string Key, string Outcome, double? Score, DateTimeOffset At)
    {
        public bool Solo => Outcome == "solo";
    }
    sealed record Msg(long Id, string Key, string Text, string? File, DateTimeOffset At, int Likes);
    sealed record Req(string Key, string TrackId, string? Artist, DateTimeOffset At);
    sealed record Act(string Key, DateTimeOffset At);
    sealed record Led(string Key, int Delta, string Reason, DateTimeOffset At);
    sealed record Tm(string Key, string Place, string Day, int Sec);

    /// <summary>Усе, що сталося від <see cref="Since"/>. <see cref="Nicks"/> — як писати ключ ніка (свіжіше написання).</summary>
    sealed class Raw
    {
        public DateTimeOffset Since;
        public string? FirstDay;
        public List<Res> Results = [];
        public List<Msg> Chat = [];
        public List<Req> Requests = [];
        public List<Act> Likes = [];
        public List<Led> Ledger = [];
        public List<Tm> Time = [];
        public List<(string Key, string Game)> Solved = [];
        public List<Act> Bans = [];
        public Dictionary<string, int> Listened = new(StringComparer.Ordinal);
        public Dictionary<string, string> Nicks = new(StringComparer.Ordinal);

        public string Nick(string key) => Nicks.TryGetValue(key, out var n) ? n : key;

        /// <summary>Дії людини, з яких видно, коли вона тут: партії, репліки, замовлення й вподобайки.</summary>
        public IEnumerable<Act> Actions() =>
            Results.Select(r => new Act(r.Key, r.At))
                .Concat(Chat.Select(m => new Act(m.Key, m.At)))
                .Concat(Requests.Select(q => new Act(q.Key, q.At)))
                .Concat(Likes);
    }

    static SqliteCommand Cmd(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("o");
    static DateTimeOffset Ts(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    HashSet<string> DjKeys() => [Auth.NickKey(site.CurrentValue.DjName), Auth.NickKey(PeopleEndpoints.AutoDjNick)];

    /// <summary>Сирі події періоду — з кешу: «Огляд», «Усі ігри», музика й профілі беруть ті самі рядки.</summary>
    Raw RawOf(string period) => Cached("raw:" + period, () => Load(Periods.Since(period, clock), Periods.FirstDay(period, clock)));

    Raw Load(DateTimeOffset since, string? firstDay)
    {
        var raw = new Raw { Since = since, FirstDay = firstDay };
        var s = Iso(since);
        var dj = DjKeys();
        // нік → як його писати: спершу як у гаманці, потім — найсвіжіше написання з подій
        void Seen(string key, string nick)
        {
            if (key.Length > 0 && !raw.Nicks.ContainsKey(key)) raw.Nicks[key] = nick.Trim();
        }
        db.With(c =>
        {
            using (var cmd = Cmd(c, "SELECT nick_key, nick FROM wallets"))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) Seen(r.GetString(0), r.GetString(1));

            using (var cmd = Cmd(c, """
                SELECT room_id, round, game, nick_key, nick, outcome, score, created_at FROM game_results
                WHERE created_at >= $s ORDER BY created_at DESC, id DESC
                """, ("$s", s)))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var key = r.GetString(3);
                    Seen(key, r.GetString(4));
                    raw.Results.Add(new Res(r.GetString(0), r.GetInt32(1), r.GetString(2), key, r.GetString(5),
                        r.IsDBNull(6) ? null : r.GetDouble(6), Ts(r.GetString(7))));
                }

            // Репліки людей (не Журнал і не Глек), без прибраних адміном; лайки — разом, щоб не ходити по кожну
            using (var cmd = Cmd(c, """
                SELECT m.id, m.nick, m.text, m.file, m.created_at, (SELECT COUNT(*) FROM chat_likes l WHERE l.chat_id = m.id)
                FROM chat m WHERE m.kind = 'chat' AND m.deleted_at IS NULL AND m.created_at >= $s ORDER BY m.id DESC
                """, ("$s", s)))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var nick = r.GetString(1);
                    var key = Auth.NickKey(nick);
                    if (key.Length == 0 || dj.Contains(key)) continue;
                    Seen(key, nick);
                    raw.Chat.Add(new Msg(r.GetInt64(0), key, r.GetString(2), Str(r, 3), Ts(r.GetString(4)), r.GetInt32(5)));
                }

            // Замовлення людей: Глек ставить рекламу й прожарки від свого імені — то не людський смак
            using (var cmd = Cmd(c, """
                SELECT p.requested_by, p.track_id, t.artist, p.started_at FROM plays p INDEXED BY ix_plays_started
                LEFT JOIN tracks t ON t.id = p.track_id
                WHERE p.source = 'user' AND p.requested_by IS NOT NULL AND p.started_at >= $s ORDER BY p.id DESC
                """, ("$s", s)))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var nick = r.GetString(0);
                    var key = Auth.NickKey(nick);
                    if (key.Length == 0 || dj.Contains(key)) continue;
                    Seen(key, nick);
                    raw.Requests.Add(new Req(key, Str(r, 1) ?? "", Str(r, 2), Ts(r.GetString(3))));
                }

            using (var cmd = Cmd(c, "SELECT nick, created_at FROM likes WHERE created_at >= $s ORDER BY created_at DESC", ("$s", s)))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var key = Auth.NickKey(r.GetString(0));
                    Seen(key, r.GetString(0));
                    raw.Likes.Add(new Act(key, Ts(r.GetString(1))));
                }

            using (var cmd = Cmd(c, "SELECT nick_key, delta, reason, created_at FROM ledger WHERE created_at >= $s", ("$s", s)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) raw.Ledger.Add(new Led(r.GetString(0), r.GetInt32(1), r.GetString(2), Ts(r.GetString(3))));

            using (var cmd = Cmd(c, """
                SELECT nick_key, substr(key, 6), day, n FROM economy_counters
                WHERE key LIKE 'time:%' AND ($f IS NULL OR day >= $f) AND n > 0
                """, ("$f", firstDay)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) raw.Time.Add(new Tm(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3)));

            using (var cmd = Cmd(c, "SELECT nick_key, nick, game FROM daily_results WHERE solved = 1 AND ($f IS NULL OR day >= $f)", ("$f", firstDay)))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    Seen(r.GetString(0), r.GetString(1));
                    raw.Solved.Add((r.GetString(0), r.GetString(2)));
                }

            using (var cmd = Cmd(c, "SELECT by_nick, created_at FROM bans WHERE by_nick IS NOT NULL AND created_at >= $s", ("$s", s)))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var key = Auth.NickKey(r.GetString(0));
                    if (key.Length == 0 || dj.Contains(key)) continue;
                    Seen(key, r.GetString(0));
                    raw.Bans.Add(new Act(key, Ts(r.GetString(1))));
                }

            // Хто що слухав: play_listeners пишеться з 13.09, рядок — людина під трек, що грав, поки в неї грав плеєр
            using (var cmd = Cmd(c, """
                SELECT pl.nick, COUNT(*) FROM play_listeners pl JOIN plays p ON p.id = pl.play_id
                WHERE p.started_at >= $s GROUP BY pl.nick
                """, ("$s", s)))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var key = Auth.NickKey(r.GetString(0));
                    if (key.Length == 0) continue;
                    Seen(key, r.GetString(0));
                    raw.Listened[key] = raw.Listened.GetValueOrDefault(key) + r.GetInt32(1);
                }
        });
        // Гра на кількох, що рахує очки (Піктіонарі, Мелодія, Танки), пише кожному ще й рядок «solo» — особистий рекорд.
        // Партією він не є: лишаємо «solo» лише справжнім соло-іграм і кооперативу без звичайних рядків (Рулетка).
        var multiGames = raw.Results.Where(r => !r.Solo).Select(r => r.Game).ToHashSet(StringComparer.Ordinal);
        raw.Results.RemoveAll(r => r.Solo && (multiGames.Contains(r.Game) || names.Get(r.Game) is { Solo: false, Coop: false }));
        return raw;
    }

    // =============================================================================================
    // Хто кого
    // =============================================================================================

    /// <summary>
    /// Перемоги однієї людини над іншою: у кожній партії на кількох кожен, хто виграв, обіграв кожного, хто програв
    /// (нічия — нікого). Командні ігри (Мафія, Позивні) так само: уся команда-переможниця — над усією командою, що
    /// програла. Ключ — (переможець, переможений).
    /// </summary>
    static Dictionary<(string W, string L), (int N, Dictionary<string, int> Games)> Beats(IEnumerable<Res> results)
    {
        var map = new Dictionary<(string, string), (int, Dictionary<string, int>)>();
        foreach (var g in results.Where(r => !r.Solo).GroupBy(r => (r.Room, r.Round)))
        {
            var rows = g.ToList();
            foreach (var w in rows.Where(r => r.Outcome == "win"))
                foreach (var l in rows.Where(r => r.Outcome == "loss" && r.Key != w.Key))
                {
                    var k = (w.Key, l.Key);
                    if (!map.TryGetValue(k, out var v)) v = (0, new Dictionary<string, int>(StringComparer.Ordinal));
                    v.Item2[w.Game] = v.Item2.GetValueOrDefault(w.Game) + 1;
                    map[k] = (v.Item1 + 1, v.Item2);
                }
        }
        return map;
    }

    sealed record Rival(string A, string B, int Aw, int Bw, List<string> Games)
    {
        public int N => Aw + Bw;
    }

    static List<Rival> RivalsOf(Dictionary<(string W, string L), (int N, Dictionary<string, int> Games)> beats)
    {
        var pairs = new Dictionary<(string, string), (int, int, Dictionary<string, int>)>();
        foreach (var ((w, l), (n, games)) in beats)
        {
            var (a, b) = string.CompareOrdinal(w, l) < 0 ? (w, l) : (l, w);
            if (!pairs.TryGetValue((a, b), out var v)) v = (0, 0, new Dictionary<string, int>(StringComparer.Ordinal));
            foreach (var (game, k) in games) v.Item3[game] = v.Item3.GetValueOrDefault(game) + k;
            pairs[(a, b)] = w == a ? (v.Item1 + n, v.Item2, v.Item3) : (v.Item1, v.Item2 + n, v.Item3);
        }
        return pairs.Select(p =>
            {
                var ((a, b), (aw, bw, games)) = p;
                var top = games.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key).Take(3).ToList();
                // лідер пари — ліворуч
                return aw >= bw ? new Rival(a, b, aw, bw, top) : new Rival(b, a, bw, aw, top);
            })
            .OrderByDescending(r => r.N).ThenBy(r => Math.Abs(r.Aw - r.Bw)).ThenBy(r => r.A, StringComparer.Ordinal)
            .ToList();
    }

    // =============================================================================================
    // Звання
    // =============================================================================================

    sealed record Holder(string Key, double Value);

    /// <summary>
    /// Одне звання: хто (кілька — коли нічия на першому місці, до трьох), скільки й що це значить; <c>second</c> — хто
    /// наступний (щоб було кого наздоганяти). Roast — підкол, а не похвала: клієнт малює його іншим кольором.
    /// </summary>
    sealed record Title(string Key, string Icon, string Name, string What, bool Roast, List<string> Nicks, double Value, string Text,
        string? SecondNick, string? SecondText);

    static string Plural(long n, string one, string few, string many)
    {
        var a = Math.Abs(n) % 100;
        var b = a % 10;
        return a is >= 11 and <= 14 ? many : b == 1 ? one : b is >= 2 and <= 4 ? few : many;
    }

    static string Num(double n) => n.ToString("#,0", Uk);
    static string Count(long n, string one, string few, string many) => Num(n) + " " + Plural(n, one, few, many);

    /// <summary>«2 год 5 хв», «45 хв» — як <c>dur</c> на клієнті.</summary>
    public static string Dur(double sec)
    {
        var m = (long)Math.Round(sec / 60);
        if (m < 1) return "<1 хв";
        if (m < 60) return m + " хв";
        var h = m / 60;
        var rest = m % 60;
        return h + " год" + (rest > 0 && h < 100 ? " " + rest + " хв" : "");
    }

    /// <summary>Найменше ігор, з якого рахуємо «Снайпера», — щоб дві партії з двох не робили людину снайпером.</summary>
    public static int SniperMin(string period) => period switch { "day" => 4, "week" => 10, "month" => 20, _ => 30 };

    List<Title> TitlesOf(Raw raw, string period)
    {
        var list = new List<Title>();
        void Add(string key, string icon, string name, string what, IEnumerable<Holder> scores, Func<double, string> text,
            bool roast = false, double min = 1)
        {
            var ranked = scores.Where(h => h.Value >= min).OrderByDescending(h => h.Value)
                .ThenBy(h => raw.Nick(h.Key), StringComparer.Ordinal).ToList();
            if (ranked.Count == 0) return;
            var top = ranked[0].Value;
            var first = ranked.TakeWhile(h => h.Value == top).ToList();
            var second = ranked.FirstOrDefault(h => h.Value < top);
            list.Add(new Title(key, icon, name, what, roast, first.Take(3).Select(h => raw.Nick(h.Key)).ToList(), top, text(top),
                second is null ? null : raw.Nick(second.Key), second is null ? null : text(second.Value)));
        }
        static IEnumerable<Holder> By<T>(IEnumerable<T> rows, Func<T, string> key, Func<IGrouping<string, T>, double> value) =>
            rows.GroupBy(key).Select(g => new Holder(g.Key, value(g)));

        var multi = raw.Results.Where(r => !r.Solo).ToList();
        var wins = By(multi, r => r.Key, g => g.Count(r => r.Outcome == "win")).ToList();

        // ---- похвали ----
        Add("winner", "🏆", "Переможець", "найбільше перемог за столами", wins, v => Count((long)v, "перемога", "перемоги", "перемог"));

        var min = SniperMin(period);
        Add("sniper", "💯", "Відмінник", "найвищий відсоток перемог (від " + min + " партій)",
            multi.GroupBy(r => r.Key).Where(g => g.Count() >= min)
                .Select(g => new Holder(g.Key, Math.Round(100.0 * g.Count(r => r.Outcome == "win") / g.Count()))),
            v => v + " % перемог");

        Add("streak", "🔥", "Серійник", "найдовша серія перемог поспіль",
            multi.GroupBy(r => r.Key).Select(g => new Holder(g.Key, BestStreak(g))),
            v => Count((long)v, "перемога", "перемоги", "перемог") + " поспіль", min: 3);

        Add("omnivore", "🎮", "Всеїдний", "найбільше різних ігор",
            By(raw.Results, r => r.Key, g => g.Select(r => r.Game).Distinct().Count()),
            v => Count((long)v, "гра", "гри", "ігор"), min: 3);

        Add("tireless", "🎲", "Невгамовний", "найбільше партій (і соло теж)",
            By(raw.Results, r => r.Key, g => g.Count()), v => Count((long)v, "партія", "партії", "партій"), min: 3);

        Add("puzzler", "🧩", "Головоломник", "найбільше розгаданих щоденних глеків",
            By(raw.Solved, x => x.Key, g => g.Count()), v => Count((long)v, "глек", "глеки", "глеків"));

        Add("chatter", "💬", "Балакун", "найбільше тяпав у Балачках",
            By(raw.Chat, m => m.Key, g => g.Count()), v => Count((long)v, "репліка", "репліки", "реплік"), min: 3);

        Add("soul", "🫶", "Душа компанії", "найбільше ❤ на своїх репліках",
            By(raw.Chat, m => m.Key, g => g.Sum(m => m.Likes)), v => Count((long)v, "вподобайка", "вподобайки", "вподобайок"), min: 2);

        Add("dj", "🎵", "Діджей", "найбільше закинутих пісень",
            By(raw.Requests, q => q.Key, g => g.Count()), v => Count((long)v, "пісня", "пісні", "пісень"), min: 2);

        Add("heart", "❤", "Серцеїд", "найбільше вподобайок трекам",
            By(raw.Likes, l => l.Key, g => g.Count()), v => Count((long)v, "вподобайка", "вподобайки", "вподобайок"), min: 2);

        Add("ear", "🎧", "Вірне вухо", "найдовше грало радіо",
            By(raw.Time.Where(t => t.Place == PlayClock.Listen), t => t.Key, g => g.Sum(t => t.Sec)), Dur, min: 60);

        Add("resident", "🏠", "Прописався", "найбільше часу на сайті",
            By(raw.Time.Where(t => t.Place == PlayClock.Site), t => t.Key, g => g.Sum(t => t.Sec)), Dur, min: 60);

        var actions = raw.Actions().Select(a => (a.Key, Hour: TimeZoneInfo.ConvertTime(a.At, Days.Kyiv).Hour)).ToList();
        Add("owl", "🦉", "Нічна сова", "найбільше діяв з опівночі до п'ятої",
            By(actions.Where(a => a.Hour < OwlUntil), a => a.Key, g => g.Count()), v => Count((long)v, "дія", "дії", "дій") + " вночі", min: 3);

        Add("lark", "🐓", "Ранній птах", "найбільше діяв з п'ятої до дев'ятої ранку",
            By(actions.Where(a => a.Hour is >= OwlUntil and < LarkUntil), a => a.Key, g => g.Count()),
            v => Count((long)v, "дія", "дії", "дій") + " зранку", min: 3);

        var earned = raw.Ledger.Where(l => l.Delta > 0 && !EconomyStore.Exchange(l.Reason)).ToList();
        Add("rich", "🏺", "Скарбник", "найбільше заробив черепків",
            By(earned, l => l.Key, g => g.Sum(l => l.Delta)), v => "+" + Count((long)v, "черепок", "черепки", "черепків"), min: 5);

        Add("dandy", "🛍", "Модник", "найбільше лишив у Лавці Дядька Глека",
            By(raw.Ledger.Where(l => l.Delta < 0 && (Head(l.Reason) is "shop" or "gift")), l => l.Key, g => -g.Sum(l => l.Delta)),
            v => Count((long)v, "черепок", "черепки", "черепків"));

        // ---- підколи ----
        Add("loser", "😵", "Невдаха", "найбільше поразок — але не здається",
            By(multi, r => r.Key, g => g.Count(r => r.Outcome == "loss")), v => Count((long)v, "поразка", "поразки", "поразок"),
            roast: true, min: 3);

        Add("peace", "🤝", "Миротворець", "найбільше нічиїх",
            By(multi, r => r.Key, g => g.Count(r => r.Outcome == "draw")), v => Count((long)v, "нічия", "нічиї", "нічиїх"),
            roast: true, min: 2);

        var roulette = raw.Ledger.Where(l => Head(l.Reason) is "roulette-bet" or "roulette-win")
            .GroupBy(l => l.Key).Select(g => (g.Key, Net: g.Sum(l => l.Delta))).ToList();
        Add("casino", "🎰", "Рулетка обібрала", "найбільше програв у рулетку",
            roulette.Select(x => new Holder(x.Key, -x.Net)), v => "−" + Count((long)v, "черепок", "черепки", "черепків"), roast: true);
        Add("lucky", "🍀", "Фартовий", "найбільше виграв у рулетку",
            roulette.Select(x => new Holder(x.Key, x.Net)), v => "+" + Count((long)v, "черепок", "черепки", "черепків"), roast: true);

        Add("censor", "🚫", "Цензор", "найбільше треків у бані",
            By(raw.Bans, b => b.Key, g => g.Count()), v => Count((long)v, "трек", "треки", "треків"), roast: true);

        // «Мовчун» — грає багато, а в Балачках ні пари з вуст: найбільше партій на одну репліку
        var said = raw.Chat.GroupBy(m => m.Key).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        Add("silent", "🙊", "Мовчун", "грає багато, а в Балачках — ні пари з вуст",
            raw.Results.GroupBy(r => r.Key).Where(g => g.Count() >= 10)
                .Select(g => new Holder(g.Key, Math.Round((double)g.Count() / (1 + said.GetValueOrDefault(g.Key)), 1))),
            v => "партій на репліку: " + v.ToString("0.#", Uk), roast: true, min: 5);

        return list;
    }

    /// <summary>Голова причини гаманця: «roulette-bet:roulette» → «roulette-bet».</summary>
    static string Head(string reason) => reason.IndexOf(':') is var i and >= 0 ? reason[..i] : reason;

    /// <summary>Найдовша серія перемог (нічия серію не рве, поразка — рве), у порядку партій.</summary>
    static int BestStreak(IEnumerable<Res> rows)
    {
        int best = 0, run = 0;
        foreach (var r in rows.OrderBy(r => r.At))
        {
            if (r.Outcome == "win") best = Math.Max(best, ++run);
            else if (r.Outcome == "loss") run = 0;
        }
        return best;
    }

    // =============================================================================================
    // «✨ Огляд»
    // =============================================================================================

    /// <summary>GET /api/stats/overview?period= — усе для «✨ Огляду» одним запитом.</summary>
    public object Overview(string? period)
    {
        var p = Norm(period);
        return Cached("overview:" + p, () => BuildOverview(p));
    }

    object BuildOverview(string p)
    {
        var raw = RawOf(p);
        var multi = raw.Results.Where(r => !r.Solo).ToList();
        var people = raw.Actions().Select(a => a.Key)
            .Concat(raw.Time.Where(t => t.Place == PlayClock.Site).Select(t => t.Key))
            .ToHashSet(StringComparer.Ordinal);
        var rivals = RivalsOf(Beats(raw.Results));
        return new
        {
            period = p,
            since = raw.FirstDay,
            first = FirstDayOf(raw),
            totals = new
            {
                rounds = multi.Select(r => (r.Room, r.Round)).Distinct().Count(),
                solo = raw.Results.Count(r => r.Solo),
                people = people.Count,
                songs = raw.Requests.Count,
                messages = raw.Chat.Count,
                likes = raw.Likes.Count,
                chatLikes = raw.Chat.Sum(m => m.Likes),
                timeSec = raw.Time.Where(t => t.Place == PlayClock.Site).Sum(t => (long)t.Sec),
                listenSec = raw.Time.Where(t => t.Place == PlayClock.Listen).Sum(t => (long)t.Sec),
                shards = raw.Ledger.Where(l => l.Delta > 0 && !EconomyStore.Exchange(l.Reason)).Sum(l => (long)l.Delta),
            },
            series = Series(raw, p),
            titles = TitlesOf(raw, p).Select(t => new
            {
                key = t.Key, icon = t.Icon, title = t.Name, what = t.What, roast = t.Roast, nicks = t.Nicks, value = t.Value,
                text = t.Text, second = t.SecondNick is null ? null : new { nick = t.SecondNick, text = t.SecondText },
            }),
            rivals = rivals.Take(RivalRows).Select(r => new
            {
                a = raw.Nick(r.A), b = raw.Nick(r.B), aw = r.Aw, bw = r.Bw,
                games = r.Games.Select(g => new { game = g, title = names.Title(g) }),
            }),
            heat = Heat(raw.Actions()),
            pearl = Pearl(raw),
            rare = Rare(),
        };
    }

    /// <summary>Найраніший київський день, за який є хоч щось, — для «за весь час» («з 7 вересня»).</summary>
    static string? FirstDayOf(Raw raw)
    {
        var first = raw.Actions().Select(a => a.At).DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
        var fromTime = raw.Time.Select(t => t.Day).DefaultIfEmpty(null).Min(StringComparer.Ordinal);
        var day = first == DateTimeOffset.MaxValue ? null : Days.Of(first);
        return day is null ? fromTime : fromTime is null || string.CompareOrdinal(day, fromTime) <= 0 ? day : fromTime;
    }

    /// <summary>
    /// Розкладка періоду: день — по годинах, тиждень і місяць — по днях, увесь час — по днях (а як днів понад
    /// <see cref="MaxDayBars"/> — по тижнях від понеділка). Час на сайті лічильники знають лише по днях — у годинах його нема.
    /// </summary>
    object Series(Raw raw, string p)
    {
        var kyiv = (DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Days.Kyiv);
        if (p == "day")
        {
            var hours = Enumerable.Range(0, 24).Select(h => new
            {
                at = h.ToString("00", CultureInfo.InvariantCulture),
                rounds = raw.Results.Where(r => !r.Solo && kyiv(r.At).Hour == h).Select(r => (r.Room, r.Round)).Distinct().Count()
                         + raw.Results.Count(r => r.Solo && kyiv(r.At).Hour == h),
                songs = raw.Requests.Count(q => kyiv(q.At).Hour == h),
                messages = raw.Chat.Count(m => kyiv(m.At).Hour == h),
                timeSec = 0L,
            }).ToList();
            return new { unit = "hour", buckets = hours };
        }
        var today = DateOnly.ParseExact(Days.Today(clock), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var startDay = raw.FirstDay ?? FirstDayOf(raw) ?? Days.Today(clock);
        var start = DateOnly.ParseExact(startDay, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var weekly = today.DayNumber - start.DayNumber + 1 > MaxDayBars;
        // ключ відра: день або понеділок його тижня
        string Bucket(string day)
        {
            if (!weekly) return day;
            var d = DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            return d.AddDays(-(((int)d.DayOfWeek + 6) % 7)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        var keys = new List<string>();
        for (var d = start; d <= today; d = d.AddDays(1))
        {
            var k = Bucket(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (keys.Count == 0 || keys[^1] != k) keys.Add(k);
        }
        var rounds = raw.Results.Where(r => !r.Solo).GroupBy(r => (r.Room, r.Round)).Select(g => Bucket(Days.Of(g.First().At)))
            .Concat(raw.Results.Where(r => r.Solo).Select(r => Bucket(Days.Of(r.At))))
            .GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var songs = raw.Requests.GroupBy(q => Bucket(Days.Of(q.At))).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var msgs = raw.Chat.GroupBy(m => Bucket(Days.Of(m.At))).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var time = raw.Time.Where(t => t.Place == PlayClock.Site).GroupBy(t => Bucket(t.Day))
            .ToDictionary(g => g.Key, g => g.Sum(t => (long)t.Sec), StringComparer.Ordinal);
        return new
        {
            unit = weekly ? "week" : "day",
            buckets = keys.Select(k => new
            {
                at = k, rounds = rounds.GetValueOrDefault(k), songs = songs.GetValueOrDefault(k),
                messages = msgs.GetValueOrDefault(k), timeSec = time.GetValueOrDefault(k),
            }).ToList(),
        };
    }

    /// <summary>Скільки найбільше стовпчиків-днів, далі — тижні.</summary>
    public const int MaxDayBars = 62;

    /// <summary>
    /// «🕐 Коли ми тусимо»: дії по днях тижня (понеділок — 0) і годинах за Києвом; peak — найжвавіша клітинка.
    /// </summary>
    static object Heat(IEnumerable<Act> actions)
    {
        var cells = new int[7][];
        for (var i = 0; i < 7; i++) cells[i] = new int[24];
        foreach (var a in actions)
        {
            var t = TimeZoneInfo.ConvertTime(a.At, Days.Kyiv);
            cells[((int)t.DayOfWeek + 6) % 7][t.Hour]++;
        }
        int max = 0, pd = 0, ph = 0;
        for (var d = 0; d < 7; d++)
            for (var h = 0; h < 24; h++)
                if (cells[d][h] > max) (max, pd, ph) = (cells[d][h], d, h);
        return new { cells, max, peak = max > 0 ? new { dow = pd, hour = ph } : null };
    }

    /// <summary>«💎 Перл»: найлайкнутіша репліка періоду (нічия — свіжіша) з тими, хто лайкнув.</summary>
    object? Pearl(Raw raw)
    {
        var m = raw.Chat.Where(x => x.Likes >= 1).OrderByDescending(x => x.Likes).ThenByDescending(x => x.Id).FirstOrDefault();
        if (m is null) return null;
        var likers = db.With(c =>
        {
            using var cmd = Cmd(c, "SELECT nick FROM chat_likes WHERE chat_id = $id ORDER BY created_at, rowid", ("$id", m.Id));
            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        });
        ChatFile? file = null;
        if (!string.IsNullOrEmpty(m.File))
            try { file = JsonSerializer.Deserialize<ChatFile>(m.File, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch (JsonException) { /* битий рядок — перл і без файла перл */ }
        return new
        {
            id = m.Id, nick = raw.Nick(m.Key), text = m.Text, likes = m.Likes, at = m.At, likers,
            file = file is null ? null : new { type = file.Type, name = file.Name, url = file.Url, w = file.W, h = file.H },
        };
    }

    /// <summary>Рідкісні ачівки — за весь час: ті, що мають не більше <see cref="RareHolders"/> людей, найрідкісніші й свіжіші згори.</summary>
    List<object> Rare() => Cached("rare", () =>
    {
        var rows = db.With(c =>
        {
            using var cmd = Cmd(c, "SELECT key, nick, unlocked_at FROM achievements");
            using var r = cmd.ExecuteReader();
            var list = new List<(string Key, string Nick, string At)>();
            while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
            return list;
        });
        return rows.GroupBy(x => x.Key)
            .Where(g => g.Count() <= RareHolders && AchievementCatalog.Get(g.Key) is not null)
            .OrderBy(g => g.Count()).ThenByDescending(g => g.Max(x => x.At), StringComparer.Ordinal)
            .Take(RareRows)
            .Select(g =>
            {
                var a = AchievementCatalog.Get(g.Key)!;
                return (object)new { key = a.Key, icon = a.Icon, title = a.Title, text = a.Text, holders = g.Select(x => x.Nick).ToList() };
            })
            .ToList();
    });

    /// <summary>Скільки людей має кожну ачівку — для «рідкісна: лише в N» у профілі.</summary>
    Dictionary<string, int> AchievementHolders() => Cached("ach-holders", () => db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT key, COUNT(*) FROM achievements GROUP BY key");
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        while (r.Read()) map[r.GetString(0)] = r.GetInt32(1);
        return map;
    }));

    // =============================================================================================
    // «🎮 Усі ігри»
    // =============================================================================================

    /// <summary>
    /// GET /api/stats/games?period= — кожна гра за період: скільки партій (у соло — заходів) і людей, хто чемпіон
    /// (найбільше перемог; у соло — найкращий результат), хто грав найбільше, коли грали востаннє.
    /// </summary>
    public object Games(string? period)
    {
        var p = Norm(period);
        return Cached("games:" + p, () =>
        {
            var raw = RawOf(p);
            var games = raw.Results.GroupBy(r => r.Game).Select(g =>
                {
                    var multi = g.Where(r => !r.Solo).ToList();
                    var solo = g.Where(r => r.Solo).ToList();
                    var info = names.Get(g.Key);
                    var top = g.GroupBy(r => r.Key).Select(x => (x.Key, N: multi.Count == 0 ? x.Count() : x.Count(r => !r.Solo)))
                        .OrderByDescending(x => x.N).ThenBy(x => raw.Nick(x.Key), StringComparer.Ordinal).First();
                    var champ = multi.Where(r => r.Outcome == "win").GroupBy(r => r.Key)
                        .Select(x => (x.Key, N: x.Count())).OrderByDescending(x => x.N).ThenBy(x => raw.Nick(x.Key), StringComparer.Ordinal)
                        .Select(x => new { nick = raw.Nick(x.Key), wins = x.N }).FirstOrDefault();
                    var lower = info?.Score == ScoreOrder.LowerIsBetter;
                    var scored = solo.Where(r => r.Score is not null).ToList();
                    var best = scored.Count == 0 ? null
                        : (lower ? scored.OrderBy(r => r.Score) : scored.OrderByDescending(r => r.Score)).ThenBy(r => r.At)
                            .Select(r => new { nick = raw.Nick(r.Key), score = r.Score }).First();
                    return new
                    {
                        game = g.Key, title = names.Title(g.Key),
                        rounds = multi.Select(r => (r.Room, r.Round)).Distinct().Count() + solo.Count,
                        solo = multi.Count == 0,
                        players = g.Select(r => r.Key).Distinct().Count(),
                        champ, best,
                        order = lower ? "lower" : "higher",
                        top = new { nick = raw.Nick(top.Key), n = top.N },
                        last = g.Max(r => r.At),
                    };
                })
                .OrderByDescending(x => x.rounds).ThenByDescending(x => x.last)
                .ToList();
            return new { period = p, rounds = games.Sum(x => x.rounds), games };
        });
    }

    // =============================================================================================
    // Музика: виконавці, хто слухає, хто лайкає
    // =============================================================================================

    /// <summary>Не пісні, а голос Глека (реклама, прожарки) чи згенероване: у виконавцях їм не місце.</summary>
    static bool NotSong(Req q) => q.Artist is null || q.TrackId.StartsWith("voice-", StringComparison.Ordinal)
                                  || q.TrackId.StartsWith("gen", StringComparison.Ordinal);

    /// <summary>
    /// GET /api/stats/music?period= — 🎤 кого найбільше закидали (з тими, хто закидав), 🎧 хто скільки слухав (час, поки
    /// грав плеєр, і скільки треків застав) і ❤ хто скільки лайкав.
    /// </summary>
    public object Music(string? period)
    {
        var p = Norm(period);
        return Cached("music:" + p, () =>
        {
            var raw = RawOf(p);
            var artists = raw.Requests.Where(q => !NotSong(q))
                .GroupBy(q => q.Artist!.Trim().ToLowerInvariant())
                .Select(g => new
                {
                    artist = g.GroupBy(q => q.Artist!.Trim()).OrderByDescending(x => x.Count()).First().Key,
                    n = g.Count(),
                    people = g.Select(q => q.Key).Distinct().Count(),
                    fans = g.GroupBy(q => q.Key).OrderByDescending(x => x.Count()).ThenBy(x => raw.Nick(x.Key), StringComparer.Ordinal)
                        .Take(3).Select(x => new { nick = raw.Nick(x.Key), n = x.Count() }).ToList(),
                })
                .OrderByDescending(a => a.n).ThenByDescending(a => a.people).ThenBy(a => a.artist, StringComparer.Ordinal)
                .Take(ArtistRows).ToList();
            var listenSec = raw.Time.Where(t => t.Place == PlayClock.Listen).GroupBy(t => t.Key)
                .ToDictionary(g => g.Key, g => g.Sum(t => (long)t.Sec), StringComparer.Ordinal);
            var listeners = listenSec.Keys.Concat(raw.Listened.Keys).Distinct()
                .Select(k => new { key = k, sec = listenSec.GetValueOrDefault(k), tracks = raw.Listened.GetValueOrDefault(k) })
                .Where(x => x.sec >= 60 || x.tracks > 0)
                .OrderByDescending(x => x.sec).ThenByDescending(x => x.tracks)
                .Take(PeopleRows)
                .Select(x => new { nick = raw.Nick(x.key), x.sec, x.tracks }).ToList();
            var likers = raw.Likes.GroupBy(l => l.Key)
                .Select(g => new { nick = raw.Nick(g.Key), n = g.Count() })
                .OrderByDescending(x => x.n).ThenBy(x => x.nick, StringComparer.Ordinal).Take(PeopleRows).ToList();
            return new { period = p, artists, listeners, likers };
        });
    }

    // =============================================================================================
    // «✨ Цікавинки» людини
    // =============================================================================================

    /// <summary>
    /// GET /api/stats/person/{nick} — цікавинки людини за весь час: з якого дня тут, улюблена гра, кривдник і жертва,
    /// суперники, коли буває (години й дні тижня), улюблений виконавець, найдовша серія, рідкісні ачівки й звання цього
    /// тижня. Ніде не бачили — null (ендпоінт віддає 404).
    /// </summary>
    public object? Person(string nick)
    {
        var key = Auth.NickKey(nick);
        if (key.Length == 0) return null;
        var v = Cached("person:" + key, () => BuildPerson(key) ?? Nobody);
        return ReferenceEquals(v, Nobody) ? null : v;
    }

    /// <summary>«Такого не бачили» в кеші — щоб і про незнайомця база не смикалась частіше за раз на <see cref="CacheFor"/>.</summary>
    static readonly object Nobody = new();

    object? BuildPerson(string key)
    {
        var raw = RawOf("all");
        var mine = raw.Results.Where(r => r.Key == key).ToList();
        var msgs = raw.Chat.Where(m => m.Key == key).ToList();
        var reqs = raw.Requests.Where(q => q.Key == key).ToList();
        var likes = raw.Likes.Where(l => l.Key == key).ToList();
        var acts = raw.Actions().Where(a => a.Key == key).ToList();
        var time = raw.Time.Where(t => t.Key == key).ToList();
        if (mine.Count == 0 && acts.Count == 0 && time.Count == 0) return null;

        var multi = mine.Where(r => !r.Solo).ToList();
        int wins = multi.Count(r => r.Outcome == "win"), losses = multi.Count(r => r.Outcome == "loss"), draws = multi.Count(r => r.Outcome == "draw");

        // улюблена гра — де найдовше сидів (час пишеться з 26.09), а як часу нема — де найбільше партій
        var played = time.Where(t => t.Place.StartsWith("game:", StringComparison.Ordinal))
            .GroupBy(t => t.Place[5..]).ToDictionary(g => g.Key, g => g.Sum(t => (long)t.Sec), StringComparer.Ordinal);
        var rounds = mine.GroupBy(r => r.Game).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var fav = played.Keys.Concat(rounds.Keys).Distinct()
            .Select(g => (Game: g, Sec: played.GetValueOrDefault(g), N: rounds.GetValueOrDefault(g)))
            .OrderByDescending(x => x.Sec).ThenByDescending(x => x.N).ThenBy(x => x.Game, StringComparer.Ordinal)
            .Select(x => new { game = x.Game, title = names.Title(x.Game), sec = x.Sec, n = x.N })
            .FirstOrDefault();

        var beats = Beats(raw.Results);
        var rivals = RivalsOf(beats).Where(r => r.A == key || r.B == key)
            .Select(r => r.A == key ? (Other: r.B, W: r.Aw, L: r.Bw) : (Other: r.A, W: r.Bw, L: r.Aw))
            .ToList();
        var nemesis = rivals.Where(r => r.L >= 2 && r.L > r.W).OrderByDescending(r => r.L).ThenBy(r => r.W).FirstOrDefault();
        var victim = rivals.Where(r => r.W >= 2 && r.W > r.L).OrderByDescending(r => r.W).ThenBy(r => r.L).FirstOrDefault();

        var hours = new int[24];
        var dows = new int[7];
        foreach (var a in acts)
        {
            var t = TimeZoneInfo.ConvertTime(a.At, Days.Kyiv);
            hours[t.Hour]++;
            dows[((int)t.DayOfWeek + 6) % 7]++;
        }

        var artist = reqs.Where(q => !NotSong(q)).GroupBy(q => q.Artist!.Trim())
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new { artist = g.Key, n = g.Count() }).FirstOrDefault();

        var holders = AchievementHolders();
        var rare = db.With(c =>
        {
            using var cmd = Cmd(c, "SELECT key FROM achievements WHERE nick_key = $k", ("$k", key));
            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        })
            .Select(k => (A: AchievementCatalog.Get(k), N: holders.GetValueOrDefault(k)))
            .Where(x => x.A is not null && x.N is > 0 and <= RareHolders + 1)
            .OrderBy(x => x.N).ThenBy(x => x.A!.Title, StringComparer.Ordinal)
            .Select(x => new { key = x.A!.Key, icon = x.A.Icon, title = x.A.Title, holders = x.N })
            .ToList();

        var nick = raw.Nick(key);
        var week = TitlesOf(RawOf("week"), "week").Where(t => t.Nicks.Any(n => Auth.NickKey(n) == key))
            .Select(t => new { key = t.Key, icon = t.Icon, title = t.Name, text = t.Text, roast = t.Roast }).ToList();

        var firstAt = acts.Select(a => a.At).DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
        var firstTime = time.Select(t => t.Day).DefaultIfEmpty(null).Min(StringComparer.Ordinal);
        var firstDay = firstAt == DateTimeOffset.MaxValue ? firstTime
            : firstTime is null || string.CompareOrdinal(Days.Of(firstAt), firstTime) <= 0 ? Days.Of(firstAt) : firstTime;

        return new
        {
            nick,
            first = firstDay,
            totals = new
            {
                rounds = mine.Count, solo = mine.Count(r => r.Solo), wins, losses, draws,
                games = mine.Select(r => r.Game).Distinct().Count(),
                messages = msgs.Count, chatLikes = msgs.Sum(m => m.Likes), songs = reqs.Count, likes = likes.Count,
                listenSec = time.Where(t => t.Place == PlayClock.Listen).Sum(t => (long)t.Sec),
            },
            streak = BestStreak(multi),
            fav,
            nemesis = nemesis.Other is null ? null : new { nick = raw.Nick(nemesis.Other), w = nemesis.W, l = nemesis.L },
            victim = victim.Other is null ? null : new { nick = raw.Nick(victim.Other), w = victim.W, l = victim.L },
            rivals = rivals.Take(6).Select(r => new { nick = raw.Nick(r.Other), w = r.W, l = r.L }),
            hours, dows,
            artist,
            rare,
            titles = week,
        };
    }

    // =============================================================================================
    // Маршрути
    // =============================================================================================

    public static WebApplication Map(WebApplication app)
    {
        var api = app.MapGroup("/api/stats");
        api.MapGet("/overview", (string? period, Litopys l) => l.Overview(period));
        api.MapGet("/games", (string? period, Litopys l) => l.Games(period));
        api.MapGet("/music", (string? period, Litopys l) => l.Music(period));
        MapGlek(api);     // 🏺 Глек — LitopysGlek.cs
        MapCharts(api);   // 📈 Графіки й 📖 Рекорди — LitopysCharts.cs
        MapNews(api);     // 📰 Газета, 📅 місяць тому, 🎯 цілі, 🔔 нове — LitopysNews.cs
        api.MapGet("/person/{nick}", (string nick, Litopys l) =>
        {
            // Kestrel розкодовує шлях увесь, крім «/»: нік зі скісною приходить як «%2F»
            var p = l.Person(nick.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase));
            return p is null ? Results.NotFound(new { message = "Такого тут не бачили" }) : Results.Ok(p);
        });
        return app;
    }

    static partial void MapGlek(RouteGroupBuilder api);
    static partial void MapCharts(RouteGroupBuilder api);
    static partial void MapNews(RouteGroupBuilder api);
}
