using System.Collections.Concurrent;
using System.Globalization;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Microsoft.Data.Sqlite;

namespace Hlechyky;

/// <summary>
/// «📰 Газета дня», «📅 Цього дня місяць тому», «🎯 Мої цілі» й «🔔 Нове в статистиці» — угорі «✨ Огляду».
/// <para>
/// Газета — випуск «Глечицького вісника» за київський день: заголовки складаються самі з подій дня (хто кого, сенсації,
/// казино, рекорди, Лавка, прожарки Глека, нічна тусня…), кожен має вагу, найважчий — головний. Формулювання — кілька
/// варіантів на тип, вибір із зерном від дати: однаково для всіх і різне щодня. Минулі випуски не міняються — їх
/// пам'ятаємо до перезапуску; сьогоднішній живе, як решта «Хто скільки», хвилину.
/// </para>
/// Текст заголовка — частини: рядок або <c>{ n: нік }</c>, щоб клієнт малював ніки так само, як усюди (колір, картка).
/// </summary>
public sealed partial class Litopys
{
    /// <summary>Перший випуск — день, з якого в базі є події.</summary>
    public const string GazetteFirst = "2026-09-07";

    /// <summary>Скільки заголовків, крім головного.</summary>
    public const int GazetteRows = 7;

    /// <summary>Менше стількох заголовків сьогодні — показуємо вчорашній випуск.</summary>
    public const int GazetteMin = 2;

    /// <summary>Скільки разів одна людина може з'явитися в одному випуску.</summary>
    public const int GazettePerNick = 3;

    /// <summary>
    /// «Сенсація»: переможений мав до цього дня щонайменше <see cref="UpsetVeteran"/> перемог за столами, переможець —
    /// не більше <see cref="UpsetRookie"/> (і вісім разів менше). Партії з кількома переможцями (команди) не рахуємо.
    /// </summary>
    public const int UpsetVeteran = 15, UpsetRookie = 10;

    /// <summary>Суперник для цілі «Хто кого» — той, кого видно за стільки останніх днів.</summary>
    public const int RivalFresh = 14;

    /// <summary>Скільки цілей показуємо.</summary>
    public const int GoalRows = 5;

    /// <summary>Нічна тусня — дії з 00:00 до 04:59 (як «Нічна сова»).</summary>
    readonly ConcurrentDictionary<string, Issue> _issues = new(StringComparer.Ordinal);

    // =============================================================================================
    // Частини тексту
    // =============================================================================================

    /// <summary>Нік у тексті: клієнт малює його кольором і з карткою.</summary>
    public sealed record NickPart(string N);

    /// <summary>
    /// Шаблон → частини: «§0», «§1»… — ніки з <paramref name="nicks"/>, решта — текст. Числа, назви ігор і пісень
    /// вставляємо в шаблон заздалегідь (у них «§» не буває).
    /// </summary>
    static List<object> Parts(string tpl, params string[] nicks)
    {
        var list = new List<object>();
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < tpl.Length; i++)
        {
            if (tpl[i] == '§' && i + 1 < tpl.Length && char.IsDigit(tpl[i + 1]) && tpl[i + 1] - '0' < nicks.Length)
            {
                if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
                list.Add(new NickPart(nicks[tpl[i + 1] - '0']));
                i++;
            }
            else sb.Append(tpl[i]);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

    /// <summary>Стале зерно (FNV-1a): <c>string.GetHashCode</c> різний у кожному процесі, а випуск мусить бути однаковим.</summary>
    static uint Seed(string s)
    {
        var h = 0x811c9dc5u;
        foreach (var ch in s) { h ^= ch; h *= 0x01000193u; }
        return h;
    }

    /// <summary>Один із варіантів — однаковий для всіх у цей день і для цього сюжету, інший — завтра.</summary>
    static string Pick(string day, string salt, params string[] variants) => variants[(int)(Seed(day + "|" + salt) % (uint)variants.Length)];

    static string Sig(params string[] bits) => Seed(string.Join("\u001f", bits)).ToString("x8", CultureInfo.InvariantCulture);

    /// <summary>«утретє», «уп'яте»… — для серій «N-й раз поспіль».</summary>
    static string Nth(int n) => n switch
    {
        2 => "удруге", 3 => "утретє", 4 => "вчетверте", 5 => "уп'яте", 6 => "ушосте", 7 => "усьоме", 8 => "увосьме",
        9 => "вдев'яте", 10 => "вдесяте", _ => n + "-й раз",
    };

    /// <summary>«Виконавець — Пісня» для заголовка.</summary>
    static string Song(string? artist, string? title) =>
        string.IsNullOrWhiteSpace(title) ? (artist ?? "").Trim() : string.IsNullOrWhiteSpace(artist) ? title.Trim() : artist.Trim() + " — " + title.Trim();

    /// <summary>«у «Блеф»», «в шахи» — після ніка (на приголосну), як у закликах за стіл.</summary>
    string Into(string game) => Calls.Into(names.Accusative(game), afterConsonant: true);

    // =============================================================================================
    // Заголовки
    // =============================================================================================

    /// <summary>Заголовок: вага — порядок у випуску; Who — ключі людей, щоб одна людина не з'їла всю газету.</summary>
    sealed record NewsHead(string Kind, int Weight, string Icon, List<object> Parts, List<object>? Sub, string? Href, string[] Who);

    sealed record Issue(string Day, string Weather, List<NewsHead> Heads, string? TopNick, int TopRounds, string? Song);

    /// <summary>Усе про один київський день: події з «за весь час», обрізані по межах дня, і те, чого в Raw нема.</summary>
    sealed class DayData
    {
        public required string Day;
        public required Raw All;
        public required Raw D;
        public DateTimeOffset Start, End;
        public List<(string Target, string? Buyer, bool Anon, string Kind)> Ads = [];
        public List<(string Key, string Nick, string Ach, bool First)> Ach = [];
        public List<(string Key, string Nick, string Item, string Source, string? From, int Price)> Lavka = [];
        public List<(string Song, int N)> Hits = [];
        public List<(string Song, int N)> Songs = [];
        public int GlekSongs;
    }

    static string NextDay(string day, int add) =>
        DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(add).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    DayData LoadDay(string day)
    {
        var all = RawOf("all");
        var start = Daily.StartOfDayUtc(day);
        var end = Daily.StartOfDayUtc(NextDay(day, 1));
        bool In(DateTimeOffset at) => at >= start && at < end;
        var d = new Raw { Since = start, FirstDay = day, Nicks = all.Nicks };
        d.Results = all.Results.Where(r => In(r.At)).ToList();
        d.Chat = all.Chat.Where(m => In(m.At)).ToList();
        d.Requests = all.Requests.Where(q => In(q.At)).ToList();
        d.Likes = all.Likes.Where(l => In(l.At)).ToList();
        d.Ledger = all.Ledger.Where(l => In(l.At)).ToList();
        d.Time = all.Time.Where(t => t.Day == day).ToList();
        d.Bans = all.Bans.Where(b => In(b.At)).ToList();
        var data = new DayData { Day = day, All = all, D = d, Start = start, End = end };
        var dj = DjKeys();
        var (s, e) = (Iso(start), Iso(end));
        db.With(c =>
        {
            // live_ads і lavka_owned заводять свої сховища — у голій базі (тести, свіжий сайт) їх може ще не бути
            var tables = new HashSet<string>(StringComparer.Ordinal);
            using (var cmd = Cmd(c, "SELECT name FROM sqlite_master WHERE type = 'table' AND name IN ('live_ads', 'lavka_owned')"))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) tables.Add(r.GetString(0));

            using (var cmd = Cmd(c, $"SELECT nick_key, game FROM daily_results WHERE solved = 1 AND day = $d AND {Bots.NotBot("nick_key")}", ("$d", day)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) d.Solved.Add((r.GetString(0), r.GetString(1)));

            if (tables.Contains("live_ads"))
            using (var cmd = Cmd(c, """
                SELECT COALESCE(target_key, ''), target, buyer, anon, kind FROM live_ads
                WHERE status = 'aired' AND kind IN ('roast', 'order') AND day = $d
                """, ("$d", day)))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var key = r.GetString(0).Length > 0 ? r.GetString(0) : Auth.NickKey(Str(r, 1) ?? "");
                    if (key.Length == 0 || dj.Contains(key)) continue;
                    data.Ads.Add((key, Str(r, 2), !r.IsDBNull(3) && r.GetInt32(3) != 0, r.GetString(4)));
                }

            // ачівка «уперше на сайті» — коли раніше за цей день її не мав ніхто
            using (var cmd = Cmd(c, $"""
                SELECT a.nick_key, a.nick, a.key,
                       NOT EXISTS (SELECT 1 FROM achievements b WHERE b.key = a.key AND b.unlocked_at < $s AND {Bots.NotBot("b.nick_key")})
                FROM achievements a WHERE a.unlocked_at >= $s AND a.unlocked_at < $e AND {Bots.NotBot("a.nick_key")} ORDER BY a.unlocked_at
                """, ("$s", s), ("$e", e)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) data.Ach.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3) != 0));

            if (tables.Contains("lavka_owned"))
            using (var cmd = Cmd(c, "SELECT nick_key, item, source, from_nick, price FROM lavka_owned WHERE at >= $s AND at < $e", ("$s", s), ("$e", e)))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    data.Lavka.Add((r.GetString(0), all.Nick(r.GetString(0)), r.GetString(1), r.GetString(2), Str(r, 3), r.IsDBNull(4) ? 0 : r.GetInt32(4)));

            using (var cmd = Cmd(c, """
                SELECT t.artist, t.title, COUNT(*) FROM likes l JOIN tracks t ON t.id = l.track_id
                WHERE l.created_at >= $s AND l.created_at < $e AND t.artist IS NOT NULL
                GROUP BY l.track_id ORDER BY 3 DESC, MAX(l.created_at) DESC LIMIT 3
                """, ("$s", s), ("$e", e)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) data.Hits.Add((Song(Str(r, 0), Str(r, 1)), r.GetInt32(2)));

            // що крутили на замовлення: Глек від свого імені ставить рекламу й прожарки — то не пісні
            using (var cmd = Cmd(c, """
                SELECT t.artist, t.title, COUNT(*) FROM plays p INDEXED BY ix_plays_started JOIN tracks t ON t.id = p.track_id
                WHERE p.source = 'user' AND p.started_at >= $s AND p.started_at < $e AND p.track_id NOT LIKE 'voice-%'
                  AND t.artist IS NOT NULL AND p.requested_by NOT IN ($dj1, $dj2)
                GROUP BY p.track_id ORDER BY 3 DESC, MAX(p.id) DESC LIMIT 3
                """, ("$s", s), ("$e", e), ("$dj1", site.CurrentValue.DjName), ("$dj2", PeopleEndpoints.AutoDjNick)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) data.Songs.Add((Song(Str(r, 0), Str(r, 1)), r.GetInt32(2)));

            using (var cmd = Cmd(c, "SELECT COUNT(*) FROM plays INDEXED BY ix_plays_started WHERE source = 'autodj' AND started_at >= $s AND started_at < $e",
                       ("$s", s), ("$e", e)))
                data.GlekSongs = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        });
        return data;
    }

    /// <summary>Випуск за день: минулі (позавчора й раніше) — назавжди, свіжі — на хвилину.</summary>
    Issue IssueOf(string day)
    {
        var today = Days.Today(clock);
        if (string.CompareOrdinal(day, NextDay(today, -1)) < 0) return _issues.GetOrAdd(day, d => BuildIssue(LoadDay(d)));
        return Cached("gazette:" + day, () => BuildIssue(LoadDay(day)));
    }

    Issue BuildIssue(DayData x) => new(x.Day, Weather(x), Headlines(x), TopOf(x).Nick, TopOf(x).N, x.Songs.Select(s => s.Song).FirstOrDefault());

    static (string? Nick, int N) TopOf(DayData x)
    {
        var top = x.D.Results.GroupBy(r => r.Key).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault();
        return top is null ? (null, 0) : (x.D.Nick(top.Key), top.Count());
    }

    static int Rounds(Raw d) => d.Results.Where(r => !r.Solo).Select(r => (r.Room, r.Round)).Distinct().Count() + d.Results.Count(r => r.Solo);

    /// <summary>«Погода в Глечиках» — шапка випуску: скільки партій, реплік і пісень, а небо — від того, як гуло.</summary>
    static string Weather(DayData x)
    {
        var rounds = Rounds(x.D);
        var sky = rounds >= 50 || x.D.Chat.Count >= 100 ? "🔥 спекотно" : rounds >= 10 ? "☀ ясно" : rounds > 0 || x.D.Chat.Count > 0 ? "⛅ мінлива хмарність" : "🌫 туман і тиша";
        var bits = new List<string> { sky };
        if (rounds > 0) bits.Add(Count(rounds, "партія", "партії", "партій"));
        if (x.D.Chat.Count > 0) bits.Add(Count(x.D.Chat.Count, "репліка", "репліки", "реплік"));
        if (x.D.Requests.Count > 0) bits.Add(Count(x.D.Requests.Count, "пісня", "пісні", "пісень") + " на замовлення");
        return string.Join(" · ", bits);
    }

    List<NewsHead> Headlines(DayData x)
    {
        var day = x.Day;
        var d = x.D;
        var all = x.All;
        var list = new List<NewsHead>();
        string N(string key) => d.Nick(key);
        void Add(string kind, int w, string icon, List<object> parts, List<object>? sub, string? href, params string[] who) =>
            list.Add(new NewsHead(kind, w, icon, parts, sub, href, who));

        var multi = d.Results.Where(r => !r.Solo).ToList();
        var titles = TitlesOf(d, "day").ToDictionary(t => t.Key, StringComparer.Ordinal);
        Title? T(string key, double min) => titles.TryGetValue(key, out var t) && t.Value >= min && t.Nicks.Count == 1 ? t : null;
        string K(Title t) => Auth.NickKey(t.Nicks[0]);

        // ---- хто з'явився вперше: перший день, коли людину видно ----
        var firstSeen = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var lastSeen = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var a in all.Actions())
        {
            if (!firstSeen.TryGetValue(a.Key, out var f) || a.At < f) firstSeen[a.Key] = a.At;
            if (!lastSeen.TryGetValue(a.Key, out var l) || a.At > l) lastSeen[a.Key] = a.At;
        }
        bool Newbie(string key) => firstSeen.TryGetValue(key, out var f) && f >= x.Start && f < x.End;
        // у старому випуску новенький — лише той, хто потім повернувся: нік на один вечір (жарт чи тролінг) — не новина
        var fresh = string.CompareOrdinal(day, NextDay(Days.Today(clock), -1)) >= 0;
        bool Stayed(string key) => fresh || lastSeen.TryGetValue(key, out var l) && l >= x.End;

        // ---- кар'єра до цього дня: скільки перемог за столами ----
        var careerWins = all.Results.Where(r => !r.Solo && r.Outcome == "win" && r.At < x.Start)
            .GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        // ---- сенсація: хтось без досвіду обіграв бувалого ----
        var upset = multi.GroupBy(r => (r.Room, r.Round)).Where(g => g.Count(r => r.Outcome == "win") == 1)
            .SelectMany(g => g.Where(w => w.Outcome == "win").SelectMany(w => g.Where(l => l.Outcome == "loss" && l.Key != w.Key)
                .Select(l => (W: w.Key, L: l.Key, w.Game, Lw: careerWins.GetValueOrDefault(l.Key), Ww: careerWins.GetValueOrDefault(w.Key)))))
            .Where(u => u.Lw >= UpsetVeteran && u.Ww <= UpsetRookie && u.Ww * 8 <= u.Lw)
            .OrderByDescending(u => u.Lw - u.Ww).ThenBy(u => u.W, StringComparer.Ordinal).FirstOrDefault();
        if (upset.W is not null)
        {
            var nb = Newbie(upset.W);
            Add("upset", 95, "😱", Parts(Pick(day, "upset",
                    nb ? "Сенсація! Новачок §0 обіграє самого §1" : "Сенсація! §0 обіграє самого §1",
                    "Хто б міг подумати: §0 кладе на лопатки §1",
                    "Глек протирає окуляри: §0 перемагає §1"), N(upset.W), N(upset.L)),
                Parts("§1 має за плечима " + Count(upset.Lw, "перемогу", "перемоги", "перемог") + ", §0 — " + (upset.Ww == 0 ? "жодної" : Num(upset.Ww))
                      + ". Гра — «" + names.Title(upset.Game) + "».", N(upset.W), N(upset.L)),
                "#games/new/" + upset.Game, upset.W, upset.L);
        }

        // ---- хто кого: серії поспіль (уся історія до кінця дня) і розгроми за день ----
        var events = all.Results.Where(r => !r.Solo && r.At < x.End).GroupBy(r => (r.Room, r.Round))
            .SelectMany(g => g.Where(w => w.Outcome == "win").SelectMany(w => g.Where(l => l.Outcome == "loss" && l.Key != w.Key)
                .Select(l => (W: w.Key, L: l.Key, w.Game, w.At))))
            .OrderBy(v => v.At).ToList();
        var pairStreaks = events.GroupBy(v => string.CompareOrdinal(v.W, v.L) < 0 ? (v.W, v.L) : (v.L, v.W))
            .Select(g =>
            {
                var seq = g.ToList();
                var last = seq[^1];
                var run = seq.AsEnumerable().Reverse().TakeWhile(v => v.W == last.W).ToList();
                return (last.W, last.L, Run: run.Count, Today: run.Count(v => v.At >= x.Start), last.Game);
            })
            .Where(p => p.Today > 0 && p.Run >= 3)
            .OrderByDescending(p => p.Run).ThenBy(p => p.W, StringComparer.Ordinal).ToList();
        var usedPairs = new HashSet<(string, string)>();
        foreach (var p in pairStreaks.Take(2))
        {
            usedPairs.Add((p.W, p.L));
            Add("pair-streak", 60 + 4 * Math.Min(p.Run, 10), "⚔", Parts(Pick(day, "pair:" + p.W + p.L,
                    "§0 " + Nth(p.Run) + " поспіль обіграє §1",
                    "Серія триває: §0 бере гору над §1 " + Nth(p.Run) + " поспіль",
                    "§1, тримайся: §0 перемагає тебе " + Nth(p.Run) + " поспіль"), N(p.W), N(p.L)),
                Parts("Остання — «" + names.Title(p.Game) + "». " + Pick(day, "pair-sub:" + p.L,
                    "§1 уже точить реванш.", "Глек ставить на §1 наступного разу.", "Рахунок серії — " + p.Run + ":0."), N(p.W), N(p.L)),
                "#games/new/" + p.Game, p.W, p.L);
        }
        foreach (var r in RivalsOf(Beats(multi)).Where(r => r.Aw >= 3 && r.Aw >= 2 * r.Bw && !usedPairs.Contains((r.A, r.B))).Take(1))
            Add("pair-day", 45 + Math.Min(r.Aw, 20), "🥊", Parts(Pick(day, "pday:" + r.A,
                    "§0 — §1: " + r.Aw + ":" + r.Bw + " за день",
                    "§0 не дає спуску §1: " + r.Aw + ":" + r.Bw,
                    "Розгром дня: §0 проти §1 — " + r.Aw + ":" + r.Bw), N(r.A), N(r.B)),
                r.Games.Count > 0 ? Parts("Найбільше — " + string.Join(", ", r.Games.Select(g => "«" + names.Title(g) + "»")) + ".") : null,
                r.Games.Count > 0 ? "#games/new/" + r.Games[0] : null, r.A, r.B);

        // ---- переможець дня й серії ----
        if (T("winner", 3) is { } win)
        {
            var wins = Count((long)win.Value, "перемогу", "перемоги", "перемог");
            Add("winner", 50 + (int)Math.Min(win.Value, 30), "🏆", Parts(Pick(day, "winner",
                    "§0 забирає " + wins + " — стіл аж гнеться",
                    "За столами панує §0: " + win.Text + " за день",
                    "Хто сьогодні на коні? §0 — " + win.Text), win.Nicks[0]),
                win.SecondNick is null ? null : Parts("Слідом — §0: " + win.SecondText + ".", win.SecondNick), "#stats/games", K(win));
        }
        if (T("streak", 4) is { } st)
            Add("streak", 52 + 2 * (int)Math.Min(st.Value, 15), "🔥", Parts(Pick(day, "streak",
                    "§0 не знає поразок: " + st.Text,
                    "Хтось, зупиніть §0! " + st.Text,
                    "§0 у вогні: " + st.Text), st.Nicks[0]), null, null, K(st));

        // ---- казино ----
        var casino = d.Ledger.Where(l => Head(l.Reason) is var h && (h.StartsWith("roulette", StringComparison.Ordinal)
                || h.StartsWith("slot", StringComparison.Ordinal)))
            .GroupBy(l => l.Key).Select(g => (Key: g.Key, Net: g.Sum(l => (long)l.Delta),
                Where: g.Any(l => Head(l.Reason).StartsWith("slot", StringComparison.Ordinal)) && !g.Any(l => Head(l.Reason).StartsWith("roulette", StringComparison.Ordinal)) ? "слотах" : "рулетці"))
            .ToList();
        var jackpot = casino.Where(c => c.Net >= 300).OrderByDescending(c => c.Net).FirstOrDefault();
        if (jackpot.Key is not null)
            Add("jackpot", 55 + (int)Math.Min(jackpot.Net / 100, 30), "🎰", Parts(Pick(day, "jackpot",
                    "§0 зриває банк: +" + Shards(jackpot.Net) + " у " + jackpot.Where,
                    "Фортуна цілує §0 в маківку: +" + Shards(jackpot.Net),
                    "§0 виходить з казино багатшим на " + Shards(jackpot.Net)), N(jackpot.Key)),
                Parts(Pick(day, "jackpot-sub", "Глек нервово перераховує касу.", "Каса Глека схудла, але тримається.")), "#games", jackpot.Key);
        var bust = casino.Where(c => c.Net <= -300).OrderBy(c => c.Net).FirstOrDefault();
        if (bust.Key is not null)
            Add("bust", 50 + (int)Math.Min(-bust.Net / 100, 30), "💸", Parts(Pick(day, "bust",
                    "Рулетка обіймає §0 і забирає " + Shards(-bust.Net),
                    "§0 лишає в " + bust.Where + " " + Shards(-bust.Net) + " — Глек тихо радіє",
                    "§0 вірить у фарт, фарт не вірить у §0: −" + Shards(-bust.Net)), N(bust.Key)), null, "#games", bust.Key);

        // ---- рекорди соло-ігор: краще за все, що було до цього дня ----
        foreach (var g in d.Results.Where(r => r.Solo && r.Score is not null).GroupBy(r => r.Game))
        {
            var info = names.Get(g.Key);
            if (info is null || info.Score == ScoreOrder.None || info.Persistent || names.Daily.Contains(g.Key)) continue;
            var hi = info.Score == ScoreOrder.HigherIsBetter;
            var before = all.Results.Where(r => r.Solo && r.Game == g.Key && r.Score is not null && r.At < x.Start).ToList();
            if (before.Count == 0) continue;
            var old = hi ? before.MaxBy(r => r.Score)! : before.MinBy(r => r.Score)!;
            var best = hi ? g.MaxBy(r => r.Score)! : g.MinBy(r => r.Score)!;
            if (hi ? best.Score <= old.Score : best.Score >= old.Score) continue;
            Add("record", 66, "📈", Parts(Pick(day, "record:" + g.Key,
                    "Новий рекорд сайту " + Into(g.Key) + ": §0 — " + Num(best.Score!.Value),
                    "§0 переписує рекорд " + Into(g.Key) + ": " + Num(best.Score!.Value)), N(best.Key)),
                Parts("Попередній — " + Num(old.Score!.Value) + (old.Key == best.Key ? ", теж свій." : " у §0."), N(old.Key)),
                "#games/new/" + g.Key, best.Key);
        }

        // ---- новенькі ----
        var newbies = d.Actions().GroupBy(a => a.Key).Where(g => g.Count() >= 2).Select(g => g.Key).Where(k => Newbie(k) && Stayed(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        foreach (var nb in newbies.Take(2))
        {
            var played = d.Results.Count(r => r.Key == nb);
            var said = d.Chat.Count(m => m.Key == nb);
            var what = played > 0 ? Count(played, "партія", "партії", "партій") : said > 0 ? Count(said, "репліка", "репліки", "реплік") : "перші кроки";
            Add("newbie", 70, "🐣", Parts(Pick(day, "newbie:" + nb,
                    "У Глечиках поповнення: §0",
                    "Нове обличчя в Глечиках — §0",
                    "Вітаймо §0 — свіжа душа на сайті"), N(nb)),
                Parts("Перший день — і вже " + what + ". Глек наливає чаю."), null, nb);
        }

        // ---- ачівки ----
        foreach (var a in x.Ach.Where(a => a.First).GroupBy(a => a.Ach).Take(2))
        {
            var cat = AchievementCatalog.Get(a.Key);
            if (cat is null || cat.Hidden) continue;
            var who = a.First();
            Add("ach-first", 58, cat.Icon, Parts(Pick(day, "ach:" + a.Key,
                    "Уперше на сайті: «" + cat.Title + "» — у §0",
                    "§0 вибиває «" + cat.Title + "», якої ще ні в кого не було",
                    "Небачене: «" + cat.Title + "» дістається §0"), who.Nick), Parts(cat.Text), null, who.Key);
        }
        if (x.Ach.Count >= 6)
        {
            var top = x.Ach.GroupBy(a => a.Key).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First();
            Add("ach-rain", 32 + Math.Min(x.Ach.Count, 30), "🏅", Parts(Pick(day, "achrain",
                    "Ачівкопад: " + Count(x.Ach.Count, "ачівка", "ачівки", "ачівок") + " за день, найбільше — у §0 (" + top.Count() + ")",
                    "§0 збирає ачівки, як гриби: " + top.Count() + " за день"), top.First().Nick), null, null, top.Key);
        }

        // ---- музика ----
        if (x.Hits.FirstOrDefault() is { N: >= 2 } hit)
            Add("hit", 35 + 3 * Math.Min(hit.N, 10), "❤", Parts(Pick(day, "hit",
                    "Хіт дня — «" + hit.Song + "»: " + hit.N + " ❤",
                    "Глечики сьогодні люблять «" + hit.Song + "» — " + hit.N + " ❤",
                    "Серце дня віддано «" + hit.Song + "»")), null, "#stats/music");
        if (x.Songs.FirstOrDefault() is { N: >= 3 } song)
            Add("song", 30 + Math.Min(song.N, 10), "🔁", Parts(Pick(day, "song",
                    "«" + song.Song + "» не вилазить з ефіру: " + Count(song.N, "замовлення", "замовлення", "замовлень"),
                    "Знову й знову: «" + song.Song + "» — " + song.N + " рази на замовлення")), null, "#stats/music");
        if (T("dj", 8) is { } djt)
            Add("dj", 33 + (int)Math.Min(djt.Value / 2, 15), "🎵", Parts(Pick(day, "dj",
                    "§0 крутить ефір: " + djt.Text + " за день",
                    "Діджей дня — §0: " + djt.Text), djt.Nicks[0]), null, "#stats/music", K(djt));
        if (T("censor", 2) is { } cen)
            Add("censor", 30, "🚫", Parts("§0 відправляє в бан " + cen.Text + " — цензура не дрімає", cen.Nicks[0]), null, null, K(cen));

        // ---- прожарки Глека ----
        var roasts = x.Ads.GroupBy(a => a.Target).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        if (roasts.Count > 0)
        {
            var hot = roasts[0];
            var ordered = hot.FirstOrDefault(a => a.Kind == "order" || a.Buyer is not null);
            var many = hot.Count() > 1 ? " — аж " + Count(hot.Count(), "раз", "рази", "разів") : "";
            Add("roast", 30 + 4 * Math.Min(x.Ads.Count, 10), "🔥", Parts(ordered.Target is not null
                    ? Pick(day, "order", "Хтось замовляє прожарку §0 — Глек виконує з радістю", "Таємний доброзичливець платить, щоб Глек підсмажив §0")
                    : Pick(day, "roast", "Глек прожарює §0" + many, "На сковорідці Глека — §0" + many, "§0 потрапляє Глекові під гарячу руку" + many), N(hot.Key)),
                roasts.Count > 1 ? Parts("Усього прожарок за день — " + x.Ads.Count + ", дісталось ще " + string.Join(", ", roasts.Skip(1).Take(3).Select((_, i) => "§" + i)) + ".",
                    roasts.Skip(1).Take(3).Select(g => N(g.Key)).ToArray()) : null,
                "#efir", hot.Key);
        }

        // ---- Лавка ----
        var buy = x.Lavka.Select(l => (l, Item: LavkaCatalog.Get(l.Item))).Where(v => v.Item is not null)
            .OrderByDescending(v => v.l.Price).FirstOrDefault();
        if (buy.Item is not null)
        {
            var l = buy.l;
            var gift = l.Source == "gift" && !string.IsNullOrEmpty(l.From);
            Add("lavka", 44 + Math.Min(l.Price / 100, 20), "🛍", gift
                    ? Parts(Pick(day, "gift", "§1 дарує §0 " + LavkaCatalog.Quoted(buy.Item) + " — оце дружба!", "Подарунок дня: " + LavkaCatalog.Quoted(buy.Item) + " від §1 для §0"), l.Nick, l.From!)
                    : Parts(Pick(day, "buy", "§0 гуляє в Лавці: " + LavkaCatalog.Quoted(buy.Item) + " за " + Shards(l.Price), "Обновка дня: §0 бере " + LavkaCatalog.Quoted(buy.Item)), l.Nick),
                x.Lavka.Count > 1 ? Parts("Усього покупок у Лавці за день — " + x.Lavka.Count + ".") : null, "#lavka",
                gift ? [l.Key, Auth.NickKey(l.From!)] : [l.Key]);
        }

        // ---- ніч, ранок, балачки ----
        var night = d.Actions().Where(a => TimeZoneInfo.ConvertTime(a.At, Days.Kyiv).Hour < OwlUntil).ToList();
        var owls = night.GroupBy(a => a.Key).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).ToList();
        if (owls.Count >= 2)
        {
            var last = TimeZoneInfo.ConvertTime(night.Max(a => a.At), Days.Kyiv).ToString("HH:mm", CultureInfo.InvariantCulture);
            var who = owls.Take(3).ToArray();
            var names3 = who.Length == 2 ? "§0 і §1" : "§0, §1 і §2";
            Add("night", 40 + 3 * Math.Min(owls.Count, 6), "🌙", Parts(Pick(day, "night",
                    "Нічна тусня: " + names3 + " не сплять до " + last,
                    "Глечики не сплять: " + names3 + " гудуть до " + last), who.Select(N).ToArray()),
                owls.Count > 3 ? Parts("А всього вночі — " + Count(owls.Count, "людина", "людини", "людей") + ".") : null, null, who);
        }
        else if (T("owl", 5) is { } owl)
            Add("owl", 34, "🦉", Parts(Pick(day, "owl", "§0 — нічна сова: " + owl.Text, "Поки всі сплять, §0 не спить: " + owl.Text), owl.Nicks[0]), null, null, K(owl));
        if (T("lark", 5) is { } lark)
            Add("lark", 28, "🐓", Parts(Pick(day, "lark", "§0 встає з півнями: " + lark.Text, "Ранній птах дня — §0: " + lark.Text), lark.Nicks[0]), null, null, K(lark));
        if (T("chatter", 15) is { } ch)
            Add("chatter", 35 + (int)Math.Min(ch.Value / 10, 20), "💬", Parts(Pick(day, "chatter",
                    "§0 не замовкає: " + ch.Text + " у Балачках",
                    "Язик без кісток: §0 — " + ch.Text,
                    "Головний балакун дня — §0: " + ch.Text), ch.Nicks[0]), null, "#chat", K(ch));
        if (T("soul", 3) is { } soul)
            Add("soul", 36, "🫶", Parts("§0 — душа компанії: " + soul.Text + " на репліках", soul.Nicks[0]), null, "#chat", K(soul));
        if (d.Chat.Count == 0 && multi.Count >= 10)
            Add("silence", 26, "🤐", Parts(Pick(day, "silence",
                "У Балачках тиша — " + Count(Rounds(d), "партія", "партії", "партій") + " зіграно мовчки",
                "Мовчазний день: грають багато, а в Балачках ні слова")), null, "#chat");
        else if (T("silent", 5) is { } sil)
            Add("silent", 27, "🙊", Parts("§0 грає й мовчить як риба: " + sil.Text, sil.Nicks[0]), null, "#chat", K(sil));

        // ---- гра дня, невгамовні, головоломки ----
        var game = d.Results.Where(r => !r.Solo).GroupBy(r => r.Game)
            .Select(g => (Game: g.Key, N: g.Select(r => (r.Room, r.Round)).Distinct().Count()))
            .OrderByDescending(g => g.N).ThenBy(g => g.Game, StringComparer.Ordinal).FirstOrDefault();
        if (game.N >= 5)
            Add("game", 30 + Math.Min(game.N / 2, 20), "🎲", Parts(Pick(day, "game:" + game.Game,
                    "Гра дня — " + names.Title(game.Game) + ": " + Count(game.N, "партія", "партії", "партій"),
                    "Усі за стіл " + Into(game.Game) + ": " + Count(game.N, "партія", "партії", "партій") + " за день")), null, "#games/new/" + game.Game);
        if (T("tireless", 15) is { } tl)
            Add("tireless", 31 + (int)Math.Min(tl.Value / 5, 15), "🎲", Parts(Pick(day, "tireless",
                    "§0 грає " + Count((long)tl.Value, "партію", "партії", "партій") + " за день — Глек уже ставить чайник",
                    "Невгамовний дня — §0: " + tl.Text), tl.Nicks[0]), null, "#stats/games", K(tl));
        if (T("omnivore", 5) is { } om)
            Add("omnivore", 29, "🎮", Parts("§0 пробує все: " + om.Text + " за день", om.Nicks[0]), null, "#games", K(om));
        if (T("loser", 6) is { } lo)
            Add("loser", 27, "😵", Parts(Pick(day, "loser", "§0 програє " + Count((long)lo.Value, "раз", "рази", "разів") + " — але не здається!", "Стійкість дня: §0 — " + lo.Text + ", і знову за стіл"), lo.Nicks[0]), null, null, K(lo));
        if (T("peace", 4) is { } pc)
            Add("peace", 26, "🤝", Parts("Мир, дружба, нічия: §0 — " + pc.Text, pc.Nicks[0]), null, null, K(pc));
        if (T("puzzler", 3) is { } pz)
            Add("puzzler", 30, "🧩", Parts(Pick(day, "puzzler", "§0 розгадує " + pz.Text + " за день", "Головоломник дня — §0: " + pz.Text), pz.Nicks[0]), null, "#games", K(pz));

        // ---- гроші й час ----
        if (T("rich", 200) is { } rich)
            Add("rich", 30, "🏺", Parts("§0 заробляє " + rich.Text + " за день", rich.Nicks[0]), null, null, K(rich));
        var clicker = d.Time.Where(t => t.Place == "game:clicker").GroupBy(t => t.Key)
            .Select(g => (Key: g.Key, Sec: g.Sum(t => (long)t.Sec))).OrderByDescending(g => g.Sec).FirstOrDefault();
        if (clicker.Sec >= 3600)
            Add("clicker", 28, "🏺", Parts(Pick(day, "clicker",
                    "§0 не відходить від Гончарного кола: " + Dur(clicker.Sec),
                    "Гончар дня — §0: " + Dur(clicker.Sec) + " за колом"), N(clicker.Key)), null, "#games/new/clicker", clicker.Key);
        if (T("ear", 7200) is { } ear)
            Add("ear", 27, "🎧", Parts(Pick(day, "ear", "§0 не вимикає радіо: " + ear.Text, "Вірне вухо дня — §0: " + ear.Text + " ефіру"), ear.Nicks[0]), null, "#efir", K(ear));
        if (T("resident", 3 * 3600) is { } res)
            Add("resident", 25, "🏠", Parts("§0 прописується на сайті: " + res.Text, res.Nicks[0]), null, null, K(res));

        // ---- тихий день: хоч щось ----
        if (list.Count < 3 && x.GlekSongs > 0)
            Add("glek", 12, "🏺", Parts(Pick(day, "glek",
                "Глек сам собі діджей: " + Count(x.GlekSongs, "пісня", "пісні", "пісень") + " в ефірі",
                "Поки всі зайняті, Глек крутить " + Count(x.GlekSongs, "пісню", "пісні", "пісень") + " сам")), null, "#efir");

        // порядок: вага, потім стале зерно — щоб рівні не стрибали
        var ordered2 = list.OrderByDescending(h => h.Weight).ThenBy(h => Seed(day + h.Kind)).ToList();
        var pick = new List<NewsHead>();
        var per = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var h in ordered2)
        {
            if (pick.Count > GazetteRows) break;
            if (h.Who.Any(k => per.GetValueOrDefault(k) >= GazettePerNick)) continue;
            foreach (var k in h.Who) per[k] = per.GetValueOrDefault(k) + 1;
            pick.Add(h);
        }
        return pick;
    }

    // =============================================================================================
    // GET /api/stats/gazette
    // =============================================================================================

    static object HeadJson(NewsHead h) => new { kind = h.Kind, icon = h.Icon, parts = h.Parts, sub = h.Sub, href = h.Href };

    static bool IsDay(string? s) => s is { Length: 10 } && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>Номер випуску: перший — <see cref="GazetteFirst"/>.</summary>
    static int IssueNo(string day) =>
        DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture).DayNumber
        - DateOnly.ParseExact(GazetteFirst, "yyyy-MM-dd", CultureInfo.InvariantCulture).DayNumber + 1;

    /// <summary>Який випуск свіжий: сьогоднішній, а як там ще порожньо — учорашній.</summary>
    string FreshDay()
    {
        var today = Days.Today(clock);
        if (today == GazetteFirst || IssueOf(today).Heads.Count >= GazetteMin) return today;
        return NextDay(today, -1);
    }

    /// <summary>
    /// GET /api/stats/gazette?day= — випуск дня. Без дня — свіжий (сьогодні або, як ще тихо, учора з позначкою
    /// <c>fallback</c>) і картка «📅 Цього дня місяць тому».
    /// </summary>
    public object Gazette(string? day)
    {
        var today = Days.Today(clock);
        var front = !IsDay(day);
        var d = front ? FreshDay() : day!;
        if (string.CompareOrdinal(d, GazetteFirst) < 0) d = GazetteFirst;
        if (string.CompareOrdinal(d, today) > 0) d = today;
        var issue = IssueOf(d);
        return new
        {
            day = d,
            no = IssueNo(d),
            today = d == today,
            fallback = front && d != today,
            prev = d == GazetteFirst ? null : NextDay(d, -1),
            next = d == today ? null : NextDay(d, 1),
            weather = issue.Weather,
            lead = issue.Heads.Count > 0 ? HeadJson(issue.Heads[0]) : null,
            items = issue.Heads.Skip(1).Select(HeadJson),
            ago = front ? Ago(today) : null,
        };
    }

    /// <summary>«📅 Цього дня місяць тому» (нема — тиждень тому): 2–3 заголовки того випуску, хто найбільше грав і що крутили.</summary>
    object? Ago(string today)
    {
        var t = DateOnly.ParseExact(today, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var (back, label) in new[] { (t.AddMonths(-1), "Цього дня місяць тому"), (t.AddDays(-7), "Цього дня тиждень тому") })
        {
            var day = back.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (string.CompareOrdinal(day, GazetteFirst) < 0) continue;
            var issue = IssueOf(day);
            if (issue.Heads.Count == 0 && issue.TopNick is null && issue.Song is null) continue;
            return new
            {
                day, label, no = IssueNo(day),
                heads = issue.Heads.Take(3).Select(HeadJson),
                top = issue.TopNick is null ? null : new { nick = issue.TopNick, rounds = issue.TopRounds },
                song = issue.Song,
            };
        }
        return null;
    }

    // =============================================================================================
    // GET /api/stats/goals — «🎯 Мої цілі»
    // =============================================================================================

    /// <summary>
    /// Ціль: що зробити (<c>Parts</c>), скільки бракує (<c>Need</c>), прогрес <c>Have</c> з <c>Of</c>, куди натиснути.
    /// <c>Ease</c> — 0 (рукою подати) … 1 (ледь досяжно): за ним цілі й сортуються.
    /// </summary>
    public sealed record Goal(string Kind, string Icon, List<object> Parts, double Need, double Have, double Of, string? Href, string? Label, double Ease);

    /// <summary>Звання тижня, яке можна відвоювати: чим міряти людину й скільки бракує, щоб це було «ось-ось».</summary>
    sealed record GoalSpec(string Key, Func<Raw, string, double> Score, Func<double, string> Unit, double Cap, string Href, string Label);

    static readonly GoalSpec[] TitleGoals =
    [
        new("winner", (r, k) => r.Results.Count(x => !x.Solo && x.Key == k && x.Outcome == "win"), v => Count((long)v, "перемога", "перемоги", "перемог"), 8, "#games", "За стіл"),
        new("tireless", (r, k) => r.Results.Count(x => x.Key == k), v => Count((long)v, "партія", "партії", "партій"), 15, "#games", "Грати"),
        new("omnivore", (r, k) => r.Results.Where(x => x.Key == k).Select(x => x.Game).Distinct().Count(), v => Count((long)v, "нова гра", "нові гри", "нових ігор"), 3, "#games", "До ігор"),
        new("puzzler", (r, k) => r.Solved.Count(x => x.Key == k), v => Count((long)v, "щоденний глек", "щоденні глеки", "щоденних глеків"), 4, "#games", "Розгадати"),
        new("chatter", (r, k) => r.Chat.Count(x => x.Key == k), v => Count((long)v, "репліка", "репліки", "реплік"), 30, "#chat", "У Балачки"),
        new("dj", (r, k) => r.Requests.Count(x => x.Key == k), v => Count((long)v, "пісня", "пісні", "пісень"), 10, "#efir", "Закинути пісню"),
        new("heart", (r, k) => r.Likes.Count(x => x.Key == k), v => Count((long)v, "вподобайка", "вподобайки", "вподобайок"), 10, "#efir", "До ефіру"),
        new("rich", (r, k) => r.Ledger.Where(x => x.Key == k && x.Delta > 0 && !EconomyStore.Exchange(x.Reason)).Sum(x => (double)x.Delta),
            v => Shards((long)v), 300, "#games", "Заробити"),
        new("ear", (r, k) => r.Time.Where(x => x.Key == k && x.Place == PlayClock.Listen).Sum(x => (double)x.Sec), v => Dur(v) + " радіо", 3 * 3600, "#efir", "Увімкнути радіо"),
    ];

    /// <summary>GET /api/stats/goals?nick= — 3–5 найближчих досяжних цілей людини.</summary>
    public object Goals(string? nick)
    {
        var key = Auth.NickKey(nick ?? "");
        if (key.Length == 0) return new { nick = (string?)null, goals = Array.Empty<object>() };
        return Cached("goals:" + key, () => (object)BuildGoals(key));
    }

    object BuildGoals(string key)
    {
        var week = RawOf("week");
        var all = RawOf("all");
        var me = all.Nick(key);
        var goals = new List<Goal>();
        if (!all.Actions().Any(a => a.Key == key)) return new { nick = me, goals };

        // ---- звання тижня: кого ось-ось обжену, а своє — хто наступає на п'яти ----
        var titles = TitlesOf(week, "week").ToDictionary(t => t.Key, StringComparer.Ordinal);
        var defend = new List<Goal>();
        foreach (var g in TitleGoals)
        {
            if (!titles.TryGetValue(g.Key, out var t)) continue;
            var mine = g.Score(week, key);
            var holder = t.Nicks.Any(n => Auth.NickKey(n) == key);
            var badge = t.Icon + " " + t.Name;
            if (holder)
            {
                if (t.SecondNick is null) continue;
                var second = g.Score(week, Auth.NickKey(t.SecondNick));
                var lead = mine - second;
                if (lead <= 0 || lead > g.Cap / 2) continue;
                defend.Add(new Goal("hold", t.Icon, Parts(badge + " тижня поки твій, але відрив від §0 — лише " + g.Unit(lead), t.SecondNick),
                    lead, second, mine, g.Href, g.Label, 0.5 + lead / g.Cap / 2));
                continue;
            }
            var need = Math.Max(t.Value - mine + (g.Key == "ear" ? 60 : 1), 0);
            if (need <= 0 || need > g.Cap) continue;
            goals.Add(new Goal("title", t.Icon, Parts("Ще " + g.Unit(need) + " — і " + badge + " тижня твій"
                    + (t.Nicks.Count == 1 ? " (зараз у §0)" : ""), t.Nicks[0]),
                need, mine, t.Value + (g.Key == "ear" ? 60 : 1), g.Href, g.Label, need / g.Cap));
        }
        goals.AddRange(defend.OrderBy(x => x.Ease).Take(1));

        // ---- хто кого (за весь час): ось-ось зрівняюсь чи обжену ----
        // лише ті, хто ще ходить: суперник, якого не видно два тижні, — не ціль, а спогад
        var recent = clock.UtcNow.AddDays(-RivalFresh);
        var around = all.Actions().Where(a => a.At >= recent).Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        var rivals = RivalsOf(Beats(all.Results)).Where(r => (r.A == key || r.B == key) && around.Contains(r.A == key ? r.B : r.A))
            .Select(r => r.A == key ? (Other: r.B, W: r.Aw, L: r.Bw) : (Other: r.A, W: r.Bw, L: r.Aw)).ToList();
        foreach (var r in rivals.Where(r => r.L >= r.W && r.L - r.W <= 3 && r.L + r.W >= 2).OrderBy(r => r.L - r.W).ThenByDescending(r => r.L + r.W).Take(2))
        {
            var gap = r.L - r.W;
            var other = all.Nick(r.Other);
            goals.Add(gap == 0
                ? new Goal("rival", "⚔", Parts("Одна перемога над §0 — і ти попереду в «Хто кого» (зараз " + r.W + ":" + r.L + ")", other),
                    1, r.W, r.W + 1, "#games", "За стіл", 0.1)
                : new Goal("rival", "⚔", Parts("Ще " + Count(gap, "перемога", "перемоги", "перемог") + " над §0 — і ви квити (зараз " + r.W + ":" + r.L + ")", other),
                    gap, r.W, r.L, "#games", "За стіл", gap / 4.0));
        }

        // ---- свій рекорд соло-гри проти рекорду сайту ----
        var solo = all.Results.Where(r => r.Solo && r.Score is not null).GroupBy(r => r.Game)
            .Select(g =>
            {
                var info = names.Get(g.Key);
                if (info is null || info.Score == ScoreOrder.None || info.Persistent || names.Daily.Contains(g.Key)) return null;
                var hi = info.Score == ScoreOrder.HigherIsBetter;
                var mine = g.Where(r => r.Key == key).ToList();
                if (mine.Count == 0) return null;
                var best = hi ? g.MaxBy(r => r.Score)! : g.MinBy(r => r.Score)!;
                var my = hi ? mine.Max(r => r.Score)!.Value : mine.Min(r => r.Score)!.Value;
                if (best.Key == key || best.Score == my || best.Score is not > 0 || my <= 0) return null;
                var ratio = hi ? my / best.Score.Value : best.Score.Value / my;   // 1 — рекорд
                if (ratio < 0.6) return null;
                return new Goal("record", "📈", Parts("Твій рекорд " + Into(g.Key) + " — " + Num(my) + ", рекорд сайту — " + Num(best.Score.Value) + " у §0", all.Nick(best.Key)),
                    Math.Abs(best.Score.Value - my), ratio, 1, "#games/new/" + g.Key, "Побити", 1 - ratio);
            })
            .Where(x => x is not null).OrderBy(x => x!.Ease).FirstOrDefault();
        if (solo is not null) goals.Add(solo);

        // ---- ачівка, яку мають майже всі, а я ні ----
        var ach = db.With(c =>
        {
            using var cmd = Cmd(c, $"SELECT key, nick_key FROM achievements WHERE {Bots.NotBot("nick_key")}");
            using var r = cmd.ExecuteReader();
            var list = new List<(string Key, string Who)>();
            while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
            return list;
        });
        var holders = ach.Select(a => a.Who).Distinct().Count();
        var mineAch = ach.Where(a => a.Who == key).Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        if (holders >= 3)
        {
            var common = ach.GroupBy(a => a.Key).Where(g => !mineAch.Contains(g.Key) && g.Count() * 2 >= holders)
                .Select(g => (A: AchievementCatalog.Get(g.Key), N: g.Count()))
                .Where(v => v.A is { Hidden: false })
                .OrderByDescending(v => v.N).ThenBy(v => v.A!.Key, StringComparer.Ordinal).FirstOrDefault();
            if (common.A is not null)
                goals.Add(new Goal("ach", common.A.Icon, Parts("«" + common.A.Title + "» уже мають " + common.N + " з " + holders + " — а ти ні: " + common.A.Text),
                    1, 0, 1, "#games", "До ігор", 0.4));
        }

        MoreGoals(key, goals);

        // по одній кожного роду спершу, далі — що ближче
        var picked = goals.OrderBy(x => x.Ease).GroupBy(x => x.Kind).SelectMany(g => g.Select((x, i) => (x, i)))
            .OrderBy(v => v.i).ThenBy(v => v.x.Ease).Select(v => v.x).Take(GoalRows).OrderBy(x => x.Ease).ToList();
        return new
        {
            nick = me,
            goals = picked.Select(x => new
            {
                kind = x.Kind, icon = x.Icon, parts = x.Parts, need = x.Need, have = x.Have, of = x.Of, href = x.Href, label = x.Label,
            }),
        };
    }

    /// <summary>
    /// Точка розширення для зведення: ще цілі людини (напр. місце в «📈 Гонці» — пакет charts). Додай реалізацію в
    /// своєму partial-файлі: <c>partial void MoreGoals(string key, List&lt;Goal&gt; goals) { goals.Add(new Goal("race", …)); }</c>.
    /// <c>Ease</c> 0…1 — наскільки близько.
    /// </summary>
    partial void MoreGoals(string key, List<Goal> goals);

    // =============================================================================================
    // GET /api/stats/pulse — «🔔 Нове в статистиці»
    // =============================================================================================

    /// <summary>
    /// GET /api/stats/pulse?nick= — короткі сигнатури того, що цікаво людині, по вкладках: <c>overview</c> — свіжий
    /// випуск газети, мої звання тижня, хто веде в моїх «хто кого», перл тижня. Клієнт порівнює з тим, що людина вже
    /// бачила, і ставить крапку на 📊.
    /// </summary>
    public object Pulse(string? nick)
    {
        var key = Auth.NickKey(nick ?? "");
        return Cached("pulse:" + key, () => (object)new { parts = BuildPulse(key) });
    }

    Dictionary<string, string> BuildPulse(string key)
    {
        var week = RawOf("week");
        var pearl = week.Chat.Where(m => m.Likes >= 1).OrderByDescending(m => m.Likes).ThenByDescending(m => m.Id).Select(m => m.Id).FirstOrDefault();
        var bits = new List<string> { "g:" + FreshDay(), "p:" + pearl.ToString(CultureInfo.InvariantCulture) };
        if (key.Length > 0)
        {
            bits.Add("t:" + string.Join(",", TitlesOf(week, "week").Where(t => t.Nicks.Any(n => Auth.NickKey(n) == key)).Select(t => t.Key).Order(StringComparer.Ordinal)));
            // хто веде в моїх парах: міняється, лише коли хтось когось обігнав, а не на кожну партію
            bits.Add("r:" + string.Join(",", RivalsOf(Beats(RawOf("all").Results)).Where(r => r.A == key || r.B == key)
                .Select(r => (r.A == key ? r.B : r.A) + (r.Aw == r.Bw ? "=" : r.A == key ? "+" : "-")).Order(StringComparer.Ordinal)));
        }
        var parts = new Dictionary<string, string>(StringComparer.Ordinal) { ["overview"] = Sig([.. bits]) };
        MorePulse(key, parts);
        return parts;
    }

    /// <summary>
    /// Точка розширення для зведення: сигнатури інших вкладок (<c>glek</c>, <c>charts</c>, <c>records</c>) —
    /// <c>parts["records"] = Sig(…)</c>. Ключ — як ключ вкладки в <c>HPeople.statsTab</c>: клієнт ставить «нове» саме їй.
    /// </summary>
    partial void MorePulse(string key, Dictionary<string, string> parts);

    static partial void MapNews(RouteGroupBuilder api)
    {
        api.MapGet("/gazette", (string? day, Litopys l) => l.Gazette(day));
        api.MapGet("/goals", (string? nick, Litopys l) => l.Goals(nick));
        api.MapGet("/pulse", (string? nick, Litopys l) => l.Pulse(nick));
    }
}
