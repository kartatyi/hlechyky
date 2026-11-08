using System.Globalization;
using Hlechyky.Games.Economy;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>Налаштування «Майстра дня» й «Ловця дня» (<c>Clicker:Day</c>, наживо).</summary>
public sealed class ClickerDayOptions
{
    public bool Enabled { get; set; } = true;
    public int[]? Prizes { get; set; }
    /// <summary>Скільки людей мусить набрати <see cref="MinPoints"/>, щоб день платив.</summary>
    public int MinPlayers { get; set; } = 2;
    public int MinPoints { get; set; } = 30;
    /// <summary>Хвилини «Свята» за 1-ше, 2-ге, 3-тє місце «Майстра дня» (v13 Г1).</summary>
    public int[]? FeastMinutes { get; set; }

    /// <summary>«Ловець дня» (v13 Г2): друга дошка — спіймані руками глеки з полиці. Вимикач.</summary>
    public bool CatchEnabled { get; set; } = true;
    public int[]? CatchPrizes { get; set; }
    public int CatchMinPlayers { get; set; } = 2;
    public int CatchMinPoints { get; set; } = 20;
    /// <summary>Денна стеля очок одного гончара в «Ловці дня» (рецензія v13): понад — не рахується, щоб скрипт цілий день не виграв. 0 — без стелі.</summary>
    public int CatchMaxPoints { get; set; } = 500;
    /// <summary>Хвилини «Щасливого кошика» за 1-ше, 2-ге, 3-тє місце «Ловця дня».</summary>
    public int[]? BasketMinutes { get; set; }

    /// <summary>Прожарка дня (v13 Г6): о 23:30 після підсумку Глек пише в Балачки підколки з книги фраз. Вимикач.</summary>
    public bool Roast { get; set; } = true;
    public string RoastFile { get; set; } = "data/clicker/roast.json";

    /// <summary>Рішення користувача 21.10: 200 / 100 / 50 черепків.</summary>
    public static readonly int[] DefaultPrizes = [200, 100, 50];
    /// <summary>Рішення користувача 08.11 (Г2): 100 / 50 / 25 черепків — ×Boost = 1000 / 500 / 250.</summary>
    public static readonly int[] DefaultCatchPrizes = [100, 50, 25];
    public static readonly int[] DefaultMinutes = [60, 30, 15];
    public int[] PrizeList => Prizes is { Length: > 0 } p ? p : DefaultPrizes;
    public int[] CatchPrizeList => CatchPrizes is { Length: > 0 } p ? p : DefaultCatchPrizes;
    public int[] FeastList => FeastMinutes is { Length: > 0 } p ? p : DefaultMinutes;
    public int[] BasketList => BasketMinutes is { Length: > 0 } p ? p : DefaultMinutes;
}

/// <summary>Приз до кола, що чекає гончаря в сервісі: хвилини «Свята» чи «Кошика» за місце дня. Ключ — <c>day:{день}:{дошка}:{місце}</c>.</summary>
public sealed record ClickerDayPrize(string Key, string Kind, int Minutes, string Why);

/// <summary>Рядок табло дня.</summary>
public sealed record ClickerDayRow(int Place, string Nick, int Points);

/// <summary>
/// «Майстер дня» (docs/games/specs/clicker-v12.md §5) і «Ловець дня» (v13 Г2): щоденні змагання гончарів. Майстер — за
/// виробами з ручного горна (дзвінкий 1, розкішний 3), ловець — за глеками з полиці, спійманими руками (по 1). Доба — з
/// 23:30 до 23:30 за Києвом; о 23:30 Дядько Глек платить призи першим трьом кожної дошки (ключ
/// <c>clicker-day:{день}:{місце}</c> і <c>clicker-catch:{день}:{місце}</c> — повтор не платить двічі) і пише рядок у Балачки.
/// Сервер лежав — підбиває на старті.
///
/// Крім черепків, місця дають хвилини в запас: «Майстер» — «🎉 Свято» (60/30/15), «Ловець» — «🧺 Щасливий кошик» (60/30/15).
/// Хвилини сервіс лише записує (<see cref="ClickerDayPrize"/>, таблиця <c>clicker_day_awards</c>), а гончар забирає сам на
/// синхронізації (<see cref="PrizesFor"/>) — гра кладе їх у банку й пам'ятає ключ, тож вдруге вони не платяться.
/// Після підсумку — прожарка дня (v13 Г6, <see cref="ClickerRoast"/>): окреме повідомлення Глека з підколками.
///
/// Чому вироби й глеки, а не гроші: глеки ростуть на порядки, і хто попереду за щаблями, той попереду назавжди. Вироби з
/// ручного горна й ловля — реальний час і руки, однакові для всіх (палій і підмайстри працюють без гравця — їх у залік не
/// беремо). Пам'ять — істина; база — запис фоном (раз на 5 с), як у турнірі слотів. Таблиці ловця — окремі
/// (<c>clicker_catch</c>, <c>clicker_catch_paid</c>): наявну <c>clicker_day</c> не чіпаємо.
/// </summary>
public sealed class ClickerDayService : BackgroundService
{
    public const string Schema = """
        CREATE TABLE IF NOT EXISTS clicker_day(
            day TEXT NOT NULL, nick_key TEXT NOT NULL, nick TEXT NOT NULL, points INTEGER NOT NULL, at TEXT NOT NULL,
            PRIMARY KEY(day, nick_key));
        CREATE TABLE IF NOT EXISTS clicker_day_paid(day TEXT PRIMARY KEY, at TEXT NOT NULL, summary TEXT NOT NULL, winner TEXT);
        CREATE TABLE IF NOT EXISTS clicker_catch(
            day TEXT NOT NULL, nick_key TEXT NOT NULL, nick TEXT NOT NULL, points INTEGER NOT NULL, at TEXT NOT NULL,
            PRIMARY KEY(day, nick_key));
        CREATE TABLE IF NOT EXISTS clicker_catch_paid(day TEXT PRIMARY KEY, at TEXT NOT NULL, summary TEXT NOT NULL, winner TEXT);
        CREATE TABLE IF NOT EXISTS clicker_day_awards(
            key TEXT PRIMARY KEY, day TEXT NOT NULL, nick_key TEXT NOT NULL, nick TEXT NOT NULL, kind TEXT NOT NULL,
            minutes INTEGER NOT NULL, why TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS clicker_roast(day TEXT PRIMARY KEY, at TEXT NOT NULL);
        """;

    /// <summary>Межа доби — 23:30 за Києвом (слово користувача 21.10: «кожен день в 23:30»).</summary>
    public static readonly TimeSpan Close = new(23, 30, 0);
    public static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(5), SettleEvery = TimeSpan.FromSeconds(30);
    /// <summary>Скільки рядків табло шле вид.</summary>
    public const int BoardShown = 10;
    /// <summary>Скільки рядків табло їде у вигляді (решту видно в «і ще N нижче»); решта табло — лише в моєму рядку.</summary>
    public const int BoardTop = 5;
    /// <summary>Скільки днів сервіс пам'ятає непрочитані призи до кола (гравець, що не заходив довше, їх губить).</summary>
    public const int AwardKeepDays = 14;

    sealed class Entry
    {
        public string Day = "", Key = "", Nick = "";
        public int Points;
        public DateTimeOffset At;
    }

    /// <summary>Одна дошка дня: свої очки, оплачені дні, перемоги й налаштування призів.</summary>
    sealed class Track(string id, string table, string paidTable, string reason)
    {
        public readonly string Id = id, Table = table, PaidTable = paidTable, Reason = reason;
        public readonly Dictionary<(string Day, string Key), Entry> Entries = [];
        public readonly HashSet<(string Day, string Key)> Dirty = [];
        public readonly HashSet<string> Paid = new(StringComparer.Ordinal);
        /// <summary>Скільки разів кожен був першим (ключ ніка).</summary>
        public readonly Dictionary<string, int> Wins = new(StringComparer.Ordinal);
        /// <summary>Останній підбитий день: рядок підсумку для виду («учора: 1. …»).</summary>
        public (string Day, string Summary)? Last;
        public Func<ClickerDayOptions, int[]> BasePrizes = o => o.PrizeList;
        public Func<ClickerDayOptions, int[]> Minutes = o => o.FeastList;
        public Func<ClickerDayOptions, int> MinPlayers = o => o.MinPlayers, MinPoints = o => o.MinPoints;
        public string Kind = "feast", Title = "", Why = "";
        public Func<List<ClickerDayRow>, string> Line = _ => "";
    }

    sealed record Award(string Key, string Day, string NickKey, string Nick, string Kind, int Minutes, string Why);

    readonly Db? _db;
    readonly IStakes? _stakes;
    readonly Func<ClickerDayOptions> _opts;
    readonly IClock _clock;
    readonly ISlotsWire? _wire;
    readonly Achievements? _ach;
    readonly ILogger<ClickerDayService>? _log;
    readonly Func<string, bool>? _person;
    readonly object _gate = new();
    readonly Track _m, _c;
    readonly Track[] _tracks;
    readonly Dictionary<string, Award> _awards = new(StringComparer.Ordinal);
    readonly HashSet<string> _roasted = new(StringComparer.Ordinal);
    volatile Bots.Set? _people;

    /// <param name="person">Хто людина (тести). null — акаунти без ботів із бази; без бази — усі, крім гостей і «🤖».</param>
    public ClickerDayService(Db? db, IStakes? stakes, Func<ClickerDayOptions> options, IClock? clock = null, ISlotsWire? wire = null,
        Achievements? achievements = null, ILogger<ClickerDayService>? log = null, Func<string, bool>? person = null)
    {
        _db = db;
        _stakes = stakes;
        _opts = options;
        _clock = clock ?? new SystemClock();
        _wire = wire;
        _ach = achievements;
        _log = log;
        _person = person;
        _m = new Track("master", "clicker_day", "clicker_day_paid", "clicker-day")
        {
            Title = "Майстер дня", Why = "За «Майстра дня»", Kind = "feast", Line = MasterLine,
        };
        _c = new Track("catcher", "clicker_catch", "clicker_catch_paid", "clicker-catch")
        {
            Title = "Ловець дня", Why = "За «Ловця дня»", Kind = "basket", Line = CatcherLine,
            BasePrizes = o => o.CatchPrizeList, Minutes = o => o.BasketList, MinPlayers = o => o.CatchMinPlayers, MinPoints = o => o.CatchMinPoints,
        };
        _tracks = [_m, _c];
        try { Load(); }
        catch (Exception ex) { _log?.LogWarning(ex, "майстер дня: не прочитав очки — починаю з чистого"); }
    }

    public ClickerDayOptions O => _opts();

    /// <summary>Призи «Майстра», які справді платимо: база з <see cref="ClickerDayOptions.PrizeList"/> × Economy:Boost.</summary>
    public int[] Prizes => [.. O.PrizeList.Select(Earn)];

    /// <summary>Призи «Ловця дня» в черепках: база з <see cref="ClickerDayOptions.CatchPrizeList"/> × Economy:Boost.</summary>
    public int[] CatchPrizes => [.. O.CatchPrizeList.Select(Earn)];

    /// <summary>Множник заробітку (Economy:Boost): призи — заробіток грою, тож × Boost. Типово — як є (тести).</summary>
    public Func<int, int> Earn { get; init; } = n => n;

    /// <summary>Щоденник кола — звідки прожарка бере кліки, партії палія й посольських. null — прожарка лише про переможців.</summary>
    public ClickerDiaryService? Diary { get; init; }
    /// <summary>Книга фраз прожарки. null чи порожня — прожарки нема.</summary>
    public Func<ClickerRoastBook?>? RoastBook { get; init; }
    /// <summary>Чи звертається Глек до цього ніка в жіночому роді (<c>Clicker:Glek:She</c>).</summary>
    public Func<string, bool>? She { get; init; }
    /// <summary>Жереб фраз прожарки (у тестах — з зерном).</summary>
    public Random Rng { get; init; } = Random.Shared;

    // ---------- доба ----------

    /// <summary>День, що закінчується найближчої 23:30 за Києвом: після 23:30 — уже завтрашній.</summary>
    public static string DayOf(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, Days.Kyiv);
        var date = DateOnly.FromDateTime(local.DateTime);
        if (local.TimeOfDay >= Close) date = date.AddDays(1);
        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>Коли день закінчується (UTC): 23:30 його дати за Києвом.</summary>
    public static DateTimeOffset Ends(string day)
    {
        var date = DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var local = date.ToDateTime(TimeOnly.FromTimeSpan(Close));
        if (Days.Kyiv.IsInvalidTime(local)) local = local.AddHours(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, Days.Kyiv), TimeSpan.Zero);
    }

    // ---------- люди ----------

    bool IsPerson(string nick)
    {
        if (_person is not null) return _person(nick);
        if (_people is { } set) return !set.Has(nick);
        return !Bots.Looks(nick) && !Auth.IsGuestNick(nick) && Auth.NickKey(nick) != Auth.Guest;
    }

    void RefreshPeople()
    {
        if (_db is null || _person is not null) return;
        try { _people = Bots.Load(_db); }
        catch (Exception ex) { _log?.LogWarning(ex, "майстер дня: не прочитав акаунти й ботів"); }
    }

    // ---------- очки (під замком кімнати гончаря: лише пам'ять) ----------

    /// <summary>Очки за партію з ручного горна. Гості й боти — не в табло (гратимуть, але очок не мають).</summary>
    public void Add(string nick, int points, DateTimeOffset now) => AddTo(_m, nick, points, now, O.Enabled);

    /// <summary>Очки «Ловця дня»: спійманий руками глек з полиці — 1. Гості й боти — не в табло.</summary>
    public void AddCatch(string nick, int points, DateTimeOffset now) => AddTo(_c, nick, points, now, O.Enabled && O.CatchEnabled, O.CatchMaxPoints > 0 ? O.CatchMaxPoints : int.MaxValue);

    void AddTo(Track t, string nick, int points, DateTimeOffset now, bool on, int cap = int.MaxValue)
    {
        if (points <= 0 || !on || !IsPerson(nick)) return;
        var key = Auth.NickKey(nick);
        if (key.Length == 0) return;
        var day = DayOf(now);
        lock (_gate)
        {
            if (t.Paid.Contains(day)) return;
            if (!t.Entries.TryGetValue((day, key), out var e)) t.Entries[(day, key)] = e = new Entry { Day = day, Key = key };
            e.Nick = nick;
            if (e.Points >= cap) return;
            e.Points = (int)Math.Min(cap, (long)e.Points + points);
            // Рівні — хто раніше набрав своє: час останнього приросту.
            e.At = now;
            t.Dirty.Add((day, key));
        }
    }

    static List<ClickerDayRow> BoardLocked(Track t, string day) =>
        [.. t.Entries.Values.Where(e => e.Day == day && e.Points > 0).OrderByDescending(e => e.Points).ThenBy(e => e.At)
            .ThenBy(e => e.Key, StringComparer.Ordinal).Select((e, i) => new ClickerDayRow(i + 1, e.Nick, e.Points))];

    public List<ClickerDayRow> Board(string day) { lock (_gate) return BoardLocked(_m, day); }

    public List<ClickerDayRow> BoardCatch(string day) { lock (_gate) return BoardLocked(_c, day); }

    public int WinsOf(string nick) { lock (_gate) return _m.Wins.GetValueOrDefault(Auth.NickKey(nick)); }

    /// <summary>Призи до кола (хвилини «Свята» й «Кошика»), що чекають цього гончаря за останні <see cref="AwardKeepDays"/> днів.</summary>
    public List<ClickerDayPrize> PrizesFor(string nick, DateTimeOffset now)
    {
        var key = Auth.NickKey(nick);
        if (key.Length == 0) return [];
        var cut = DayOf(now.AddDays(-AwardKeepDays));
        lock (_gate)
            return [.. _awards.Values.Where(a => a.NickKey == key && string.CompareOrdinal(a.Day, cut) >= 0)
                .OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => new ClickerDayPrize(a.Key, a.Kind, a.Minutes, a.Why))];
    }

    /// <summary>
    /// Табло однієї дошки для виду, коротко (межа розміру виду топ-гончаря, ClickerSpeedTests): рядок — <c>[нік, очки]</c>, місце — це
    /// номер у списку; моє місце — <c>mine = [місце, очки]</c> (поза п'ятіркою клієнт малює його окремим рядком); пустого нема
    /// (немає мене — немає mine, ніхто не грав — board = []). Призи, хвилини й пороги — у каталозі (<see cref="CatalogWire"/>).
    /// Підсумок минулого дня — лише першу добу після підбиття: далі це вага, що летить щопачки кліків.
    /// </summary>
    Dictionary<string, object?> WireTrack(Track t, string day, string key, DateTimeOffset now)
    {
        var board = BoardLocked(t, day);
        var mine = board.FirstOrDefault(r => Auth.NickKey(r.Nick) == key);
        var fresh = t.Last is { } l && now < Ends(l.Day).AddDays(1) ? l : ((string Day, string Summary)?)null;
        var wire = new Dictionary<string, object?>
        {
            ["board"] = board.Take(BoardTop).Select(r => new object[] { r.Nick, r.Points }),
            ["players"] = board.Count,
        };
        if (mine is not null) wire["mine"] = new[] { mine.Place, mine.Points };
        if (fresh is { } f) wire["last"] = new { day = f.Day, line = BodyOf(f.Summary) };
        return wire;
    }

    /// <summary>
    /// Підсумок без заголовка («🏆 Майстер дня в Гончарному колі: ») — у виді він повторював би заголовок вкладки, а клієнт його
    /// однаково зрізає (lastLine у clicker-day.js); у Балачки рядок іде повний.
    /// </summary>
    static string BodyOf(string summary) =>
        System.Text.RegularExpressions.Regex.Replace(summary, @"^(?:🏆|🧺)\s*(?:Майстер|Ловець) дня(?: в Гончарному колі)?:\s*", "");

    /// <summary>
    /// Незмінне про дошки дня для каталогу (їде раз, а не щопачки кліків): призи, хвилини «Свята» й «Кошика», пороги.
    /// null — «Майстра дня» вимкнено.
    /// </summary>
    public object? CatalogWire()
    {
        var o = O;
        if (!o.Enabled) return null;
        return new
        {
            prizes = Prizes,
            feast = o.FeastList,
            minPoints = o.MinPoints,
            minPlayers = o.MinPlayers,
            catcher = o.CatchEnabled ? new { prizes = CatchPrizes, minutes = o.BasketList, minPoints = o.CatchMinPoints, minPlayers = o.CatchMinPlayers } : null,
        };
    }

    /// <summary>
    /// Вид для кола: день, до коли, моє місце й очки, табло (перші п'ять), учорашній підсумок — це «Майстер дня»
    /// (як було у v12); поруч <c>catcher</c> — така сама дошка «Ловця дня» (немає, поки її вимкнено). Не людина (гість,
    /// бот) — <c>guest: true</c>; порожнього не шлемо (див. <see cref="WireTrack"/>).
    /// </summary>
    public object? Wire(string nick, DateTimeOffset now)
    {
        var o = O;
        if (!o.Enabled) return null;
        var day = DayOf(now);
        var key = Auth.NickKey(nick);
        lock (_gate)
        {
            var wire = WireTrack(_m, day, key, now);
            wire["day"] = day;
            wire["ends"] = Ends(day);
            if (!IsPerson(nick)) wire["guest"] = true;
            if (o.CatchEnabled) wire["catcher"] = WireTrack(_c, day, key, now);
            return wire;
        }
    }

    // ---------- підбиття ----------

    /// <summary>Прожарку після затяжного простою сервера вже не пишемо: новина старша за пів доби нікому не цікава.</summary>
    static readonly TimeSpan RoastStale = TimeSpan.FromHours(6);

    /// <summary>
    /// Підбити всі дні, що вже закінчились і ще не оплачені, — по кожній дошці окремо: призи першим (якщо людей з порогом
    /// очок досить), хвилини «Свята»/«Кошика» в призи до кола, рядок у Балачки, ачівки переможцеві «Майстра». Потім —
    /// прожарка дня. Повертає, скільки днів підбито.
    /// </summary>
    public int Settle(DateTimeOffset now)
    {
        List<string> days;
        lock (_gate)
            days = [.. _tracks.SelectMany(t => t.Entries.Values.Where(e => !t.Paid.Contains(e.Day)).Select(e => e.Day)).Distinct()
                .Where(d => Ends(d) <= now).Order(StringComparer.Ordinal)];
        var done = 0;
        foreach (var day in days)
        {
            var settled = false;
            var won = new Dictionary<string, List<ClickerDayRow>>();
            foreach (var t in _tracks)
            {
                bool has;
                lock (_gate) has = !t.Paid.Contains(day) && t.Entries.Keys.Any(k => k.Day == day);
                if (!has) continue;
                if (SettleTrack(t, day, now) is { } winners) { settled = true; won[t.Id] = winners; }
            }
            if (!settled) continue;
            done++;
            Roast(day, now, won);
        }
        if (done > 0) Prune(now);
        return done;
    }

    /// <summary>Підбити одну дошку дня. null — призи не пройшли (повторимо згодом), інакше — переможці (можуть бути порожні).</summary>
    List<ClickerDayRow>? SettleTrack(Track t, string day, DateTimeOffset now)
    {
        var o = O;
        List<ClickerDayRow> board;
        lock (_gate) board = BoardLocked(t, day);
        var minPoints = t.MinPoints(o);
        var eligible = board.Where(r => r.Points >= minPoints).ToList();
        var prizes = t.BasePrizes(o).Select(Earn).ToArray();
        var minutes = t.Minutes(o);
        var winners = eligible.Count >= Math.Max(1, t.MinPlayers(o)) ? eligible.Take(prizes.Length).ToList() : [];
        try
        {
            for (var i = 0; i < winners.Count; i++)
                if (prizes[i] > 0 && _stakes is not null)
                    _stakes.Grant(winners[i].Nick, prizes[i], $"{t.Reason}:{day}", $"{t.Reason}:{day}:{i + 1}");
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "{Title}: призи дня {Day} не пройшли — спробую згодом", t.Title, day);
            return null;
        }
        for (var i = 0; i < winners.Count; i++)
            if (i < minutes.Length && minutes[i] > 0)
                AddAward(new Award($"day:{day}:{t.Id}:{winners[i].Place}", day, Auth.NickKey(winners[i].Nick), winners[i].Nick, t.Kind, minutes[i], t.Why));
        var summary = winners.Count > 0 ? t.Line(winners)
            : board.Count > 0 ? $"🏆 {t.Title}: без призів — порогу {minPoints} очок досягли {eligible.Count}" : "";
        var winner = winners.Count > 0 ? winners[0].Nick : null;
        lock (_gate)
        {
            t.Paid.Add(day);
            if (winner is not null) t.Wins[Auth.NickKey(winner)] = t.Wins.GetValueOrDefault(Auth.NickKey(winner)) + 1;
            if (summary.Length > 0) t.Last = (day, summary);
        }
        try
        {
            _db?.Exec($"INSERT OR IGNORE INTO {t.PaidTable}(day, at, summary, winner) VALUES($d, $t, $s, $w)",
                ("$d", day), ("$t", now.UtcDateTime.ToString("o")), ("$s", summary), ("$w", winner));
        }
        catch (Exception ex) { _log?.LogWarning(ex, "{Title}: не записав оплату дня {Day}", t.Title, day); }
        if (winner is not null && t == _m)
        {
            try
            {
                _ach?.Unlock(winner, "potter-day-win");
                if (WinsOf(winner) >= 7) _ach?.Unlock(winner, "potter-day-7");
            }
            catch (Exception ex) { _log?.LogWarning(ex, "майстер дня: ачівки не пройшли"); }
        }
        if (winners.Count > 0 && _wire is not null)
        {
            try { _wire.Chat(summary); }
            catch (Exception ex) { _log?.LogWarning(ex, "{Title}: рядок у Балачки не пішов", t.Title); }
        }
        _log?.LogInformation("{Title}: {Day} підбито — {Summary}", t.Title, day, summary);
        return winners;
    }

    static string Places(List<ClickerDayRow> winners) => string.Join(" · ", winners.Select(w => $"{w.Place}. {w.Nick} {w.Points}"));

    static string MasterLine(List<ClickerDayRow> winners) =>
        "🏆 Майстер дня в Гончарному колі: " + Places(winners) + " — призи й Свято від Дядька Глека";

    static string CatcherLine(List<ClickerDayRow> winners) =>
        "🧺 Ловець дня в Гончарному колі: " + Places(winners) + " — призи й Кошик від Дядька Глека";

    void AddAward(Award a)
    {
        lock (_gate)
        {
            if (_awards.ContainsKey(a.Key)) return;
            _awards[a.Key] = a;
        }
        try
        {
            _db?.Exec("INSERT OR IGNORE INTO clicker_day_awards(key, day, nick_key, nick, kind, minutes, why) VALUES($k, $d, $nk, $n, $kind, $m, $w)",
                ("$k", a.Key), ("$d", a.Day), ("$nk", a.NickKey), ("$n", a.Nick), ("$kind", a.Kind), ("$m", a.Minutes), ("$w", a.Why));
        }
        catch (Exception ex) { _log?.LogWarning(ex, "майстер дня: не записав приз до кола {Key}", a.Key); }
    }

    // ---------- прожарка дня (v13 Г6) ----------

    /// <summary>
    /// Після підсумку дня Глек пише в Балачки окреме повідомлення з підколками (ClickerRoast.cs). Раз на день: коли всі дошки дня
    /// вже підбито. Дані — щоденник кола за останні 14 днів. Вимикач — <c>Clicker:Day:Roast</c>.
    /// </summary>
    void Roast(string day, DateTimeOffset now, Dictionary<string, List<ClickerDayRow>> won)
    {
        var o = O;
        if (!o.Roast) return;
        lock (_gate)
        {
            if (_roasted.Contains(day)) return;
            if (_tracks.Any(t => !t.Paid.Contains(day) && t.Entries.Keys.Any(k => k.Day == day))) return;
            _roasted.Add(day);
        }
        if (now - Ends(day) > RoastStale) return;
        try
        {
            if (RoastBook?.Invoke() is not { } book) return;
            var today = new List<RoastPerson>();
            var absent = new List<RoastAbsent>();
            if (Diary is { Enabled: true } diary)
            {
                var from = DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(-ClickerRoast.WindowDays)
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                (today, absent) = ClickerRoast.Gather(diary.Range(from, day), day);
            }
            RoastWinner[] Of(string id) => won.TryGetValue(id, out var rows) ? [.. rows.Select(r => new RoastWinner(r.Nick, r.Points))] : [];
            var text = ClickerRoast.Compose(book, Of(_m.Id), Of(_c.Id), today, absent, She ?? (_ => false), Rng);
            try { _db?.Exec("INSERT OR IGNORE INTO clicker_roast(day, at) VALUES($d, $t)", ("$d", day), ("$t", now.UtcDateTime.ToString("o"))); }
            catch (Exception ex) { _log?.LogWarning(ex, "прожарка дня: не записав, що {Day} вже прожарено", day); }
            if (text is null) { _log?.LogInformation("прожарка дня {Day}: людей замало — мовчу", day); return; }
            _wire?.Chat(text);
            _log?.LogInformation("прожарка дня {Day} — у Балачках", day);
        }
        catch (Exception ex) { _log?.LogWarning(ex, "прожарка дня {Day} не вдалась", day); }
    }

    /// <summary>Оплачені минулі дні й прострочені призи до кола з пам'яті геть (у базі лишаються). Незаписане спершу пишемо.</summary>
    void Prune(DateTimeOffset now)
    {
        FlushNow();
        var today = DayOf(now);
        var cut = DayOf(now.AddDays(-AwardKeepDays));
        lock (_gate)
        {
            foreach (var t in _tracks)
                foreach (var k in t.Entries.Keys.Where(k => t.Paid.Contains(k.Day) && string.CompareOrdinal(k.Day, today) < 0).ToList())
                    t.Entries.Remove(k);
            foreach (var k in _awards.Where(a => string.CompareOrdinal(a.Value.Day, cut) < 0).Select(a => a.Key).ToList()) _awards.Remove(k);
        }
    }

    // ---------- база ----------

    void Load()
    {
        if (_db is null) return;
        _db.Exec(Schema);
        var cut = DayOf(_clock.UtcNow.AddDays(-AwardKeepDays));
        _db.With(c =>
        {
            foreach (var t in _tracks)
            {
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = $"SELECT day, summary, winner FROM {t.PaidTable} ORDER BY day";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var day = r.GetString(0);
                        t.Paid.Add(day);
                        if (!r.IsDBNull(1) && r.GetString(1).Length > 0) t.Last = (day, r.GetString(1));
                        if (!r.IsDBNull(2)) { var k = Auth.NickKey(r.GetString(2)); if (k.Length > 0) t.Wins[k] = t.Wins.GetValueOrDefault(k) + 1; }
                    }
                }
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = $"SELECT day, nick_key, nick, points, at FROM {t.Table} WHERE day NOT IN (SELECT day FROM {t.PaidTable})";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var e = new Entry
                        {
                            Day = r.GetString(0), Key = r.GetString(1), Nick = r.GetString(2), Points = r.GetInt32(3),
                            At = DateTimeOffset.TryParse(r.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : default,
                        };
                        t.Entries[(e.Day, e.Key)] = e;
                    }
                }
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT key, day, nick_key, nick, kind, minutes, why FROM clicker_day_awards WHERE day >= $cut";
                cmd.Parameters.AddWithValue("$cut", cut);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    _awards[r.GetString(0)] = new Award(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetString(6));
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT day FROM clicker_roast ORDER BY day DESC LIMIT 60";
                using var r = cmd.ExecuteReader();
                while (r.Read()) _roasted.Add(r.GetString(0));
            }
        });
    }

    public void FlushNow()
    {
        if (_db is null) { lock (_gate) foreach (var t in _tracks) t.Dirty.Clear(); return; }
        var snap = new List<(Track T, string Day, string Key, string Nick, int Points, string At)>();
        lock (_gate)
            foreach (var t in _tracks)
            {
                snap.AddRange(t.Dirty.Where(t.Entries.ContainsKey).Select(k => t.Entries[k]).Select(e => (t, e.Day, e.Key, e.Nick, e.Points, e.At.UtcDateTime.ToString("o"))));
                t.Dirty.Clear();
            }
        if (snap.Count == 0) return;
        try
        {
            _db.With(c =>
            {
                using var tx = c.BeginTransaction();
                foreach (var e in snap)
                {
                    using var cmd = c.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = $"INSERT OR REPLACE INTO {e.T.Table}(day, nick_key, nick, points, at) VALUES($d, $k, $n, $p, $t)";
                    cmd.Parameters.AddWithValue("$d", e.Day);
                    cmd.Parameters.AddWithValue("$k", e.Key);
                    cmd.Parameters.AddWithValue("$n", e.Nick);
                    cmd.Parameters.AddWithValue("$p", e.Points);
                    cmd.Parameters.AddWithValue("$t", e.At);
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            });
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "майстер дня: не записав очки — повторю");
            lock (_gate) foreach (var e in snap) e.T.Dirty.Add((e.Day, e.Key));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        RefreshPeople();
        try { Settle(_clock.UtcNow); }
        catch (Exception ex) { _log?.LogWarning(ex, "майстер дня: підбиття на старті не вдалось"); }
        var last = _clock.UtcNow;
        using var timer = new PeriodicTimer(FlushEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                FlushNow();
                if (_clock.UtcNow - last < SettleEvery) continue;
                last = _clock.UtcNow;
                RefreshPeople();
                try { Settle(last); }
                catch (Exception ex) { _log?.LogWarning(ex, "майстер дня: підбиття не вдалось"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        FlushNow();
    }
}

/// <summary>Підключення: опції <c>Clicker:Day</c>, книга прожарки й сервіс.</summary>
public static class ClickerDaySetup
{
    public static IServiceCollection Add(IServiceCollection services)
    {
        services.AddOptions<ClickerDayOptions>().BindConfiguration("Clicker:Day");
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptionsMonitor<ClickerDayOptions>>();
            return new ClickerRoastService(() => opts.CurrentValue, sp.GetService<ILogger<ClickerRoastService>>());
        });
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptionsMonitor<ClickerDayOptions>>();
            var roast = sp.GetRequiredService<ClickerRoastService>();
            var glek = sp.GetService<ClickerGlekService>();
            return new ClickerDayService(sp.GetService<Db>(), sp.GetService<IStakes>(), () => opts.CurrentValue, sp.GetService<IClock>(),
                sp.GetService<ISlotsWire>(), sp.GetService<Achievements>(), sp.GetService<ILogger<ClickerDayService>>())
            {
                Earn = n => sp.GetService<IOptionsMonitor<EconomyOptions>>()?.CurrentValue.Earn(n) ?? n,
                Diary = sp.GetService<ClickerDiaryService>(),
                RoastBook = () => roast.Book,
                She = nick => glek?.IsShe(nick) ?? false,
            };
        });
        services.AddHostedService(sp => sp.GetRequiredService<ClickerDayService>());
        return services;
    }
}
