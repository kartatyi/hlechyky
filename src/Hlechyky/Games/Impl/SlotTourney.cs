using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games.Economy;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>Турнір тижня в слотах (appsettings.json → «Slots:Tourney», наживо). docs/games/specs/slot-tourney.md.</summary>
public sealed class SlotTourneyOptions
{
    /// <summary>false — турніру нема: дія <c>tspin</c> відмовляє, вид і лобі його не показують, призів нема.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Фішок на спробу.</summary>
    public int Chips { get; set; } = 1000;
    /// <summary>Обертів на спробу.</summary>
    public int Spins { get; set; } = 50;
    /// <summary>Ставка оберту у фішках (не менша за <see cref="SlotsOptions.MinBet"/>).</summary>
    public int Bet { get; set; } = 20;
    /// <summary>Призи за місця 1, 2, 3… у черепках від дому. Порожньо — <see cref="DefaultPrizes"/>.</summary>
    public int[]? Prizes { get; set; }
    /// <summary>Призи — лише коли людей у таблиці щонайменше стільки.</summary>
    public int MinPlayers { get; set; } = 2;

    public static readonly int[] DefaultPrizes = [1000, 500, 250];
    public int[] PrizeList => Prizes is { Length: > 0 } p ? p : DefaultPrizes;
    public int BetSafe => Math.Max(SlotsOptions.MinBet, Bet);
}

/// <summary>Тиждень турніру: з понеділка 00:00 до неділі 21:00 за Києвом; автомат тижня — по черзі з п'яти.</summary>
public static class SlotWeek
{
    /// <summary>Порядок автоматів тижня (тиждень 19.10.2026 — перший).</summary>
    public static readonly string[] Machines = ["slot-glek", "slot-cascade", "slot-cluster", "slot-hold", "slot-slidy"];

    public static readonly IReadOnlyDictionary<string, string> Titles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["slot-glek"] = "Однорукий Глек", ["slot-cascade"] = "Розбиті глеки", ["slot-cluster"] = "Цвіт папороті",
        ["slot-hold"] = "Козацький скарб", ["slot-slidy"] = "Сліди на полиці",
    };

    static readonly DateOnly Anchor = new(2026, 10, 19);

    /// <summary>Понеділок київського тижня, у якому <paramref name="now"/>.</summary>
    public static DateOnly Monday(DateTimeOffset now)
    {
        var local = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Days.Kyiv).DateTime);
        return local.AddDays(-(((int)local.DayOfWeek + 6) % 7));
    }

    /// <summary>Ідентифікатор тижня — дата його понеділка, «2026-10-19».</summary>
    public static string Id(DateTimeOffset now) => Id(Monday(now));
    public static string Id(DateOnly monday) => monday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static DateOnly Parse(string week) => DateOnly.ParseExact(week, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Неділя 21:00 за Києвом цього тижня, у UTC.</summary>
    public static DateTimeOffset Close(DateOnly monday)
    {
        var local = monday.AddDays(6).ToDateTime(new TimeOnly(21, 0), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, Days.Kyiv), TimeSpan.Zero);
    }

    public static DateTimeOffset Close(string week) => Close(Parse(week));

    /// <summary>Автомат тижня: детерміновано від номера тижня.</summary>
    public static string Game(DateOnly monday)
    {
        var i = (int)Math.Floor((monday.DayNumber - Anchor.DayNumber) / 7.0);
        return Machines[((i % Machines.Length) + Machines.Length) % Machines.Length];
    }

    public static string Title(string game) => Titles.TryGetValue(game, out var t) ? t : game;
}

/// <summary>Результат турнірного оберту.</summary>
public sealed record TourneySpin(JsonObject Script, int Chips, int Spin, int Of, bool Done);

/// <summary>Рядок табло тижня.</summary>
public sealed record TourneyRow(string Key, string Nick, int Best, DateTimeOffset At, int Tries);

/// <summary>
/// Турнір тижня в слотах (пакет 5 «Азарт чесно», 20.10). Рівні фішки, без ризику для гаманця: щодня (київська доба)
/// одна спроба — <see cref="SlotTourneyOptions.Chips"/> фішок, <see cref="SlotTourneyOptions.Spins"/> обертів по
/// <see cref="SlotTourneyOptions.Bet"/> на автоматі тижня. Оберт рахує та сама математика автомата (<see cref="ISlotMath.Spin"/>)
/// зі своїм станом спроби; гаманець, леджер, сальдо азарту й Скарбничка турніру не торкаються. У залік — найкраща спроба
/// (рівні — хто раніше). У неділю о 21:00 — призи від дому (<c>slot-prize:&lt;тиждень&gt;</c>, ключ на тиждень+місце),
/// якщо людей ≥ <see cref="SlotTourneyOptions.MinPlayers"/>, і рядок у Балачки.
/// <para>
/// Пам'ять — істина для гри (можна під замком кімнати); база — своя таблиця <c>slot_tourney</c>: читання раз на старті,
/// запис фоном раз на <see cref="FlushEvery"/> (падіння губить щонайбільше кілька обертів спроби).
/// </para>
/// </summary>
public sealed class SlotTourney : BackgroundService
{
    public const string Schema = """
        CREATE TABLE IF NOT EXISTS slot_tourney(
            week TEXT NOT NULL, day TEXT NOT NULL, nick_key TEXT NOT NULL, nick TEXT NOT NULL, game TEXT NOT NULL,
            chips INTEGER NOT NULL, spins INTEGER NOT NULL, done INTEGER NOT NULL, at TEXT NOT NULL, math TEXT,
            PRIMARY KEY(week, day, nick_key));
        CREATE TABLE IF NOT EXISTS slot_tourney_paid(week TEXT PRIMARY KEY, at TEXT NOT NULL, summary TEXT NOT NULL);
        """;

    public static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(2);
    /// <summary>Як часто перевіряти, чи не пора платити, і оновлювати список людей (акаунти без ботів).</summary>
    public static readonly TimeSpan SettleEvery = TimeSpan.FromSeconds(30);

    sealed class Attempt
    {
        public string Week = "", Day = "", Key = "", Nick = "", Game = "";
        public int Chips, Spins;
        public bool Done;
        public DateTimeOffset At;
        public JsonObject Math = new();
    }

    readonly Db? _db;
    readonly IStakes? _stakes;
    readonly Func<SlotTourneyOptions> _opts;
    readonly IClock _clock;
    readonly ISlotsWire? _wire;
    readonly ILogger<SlotTourney>? _log;
    readonly Func<string, bool>? _person;
    readonly object _gate = new();
    readonly Dictionary<(string Day, string Key), Attempt> _att = [];
    readonly HashSet<string> _paid = new(StringComparer.Ordinal);
    readonly HashSet<(string Day, string Key)> _dirty = [];
    volatile Bots.Set? _people;

    /// <param name="person">Хто людина (тести). null — акаунти без ботів із бази; без бази — усі, крім гостей і «🤖».</param>
    public SlotTourney(Db? db, IStakes? stakes, Func<SlotTourneyOptions> options, IClock? clock = null, ISlotsWire? wire = null,
        ILogger<SlotTourney>? log = null, Func<string, bool>? person = null)
    {
        _db = db;
        _stakes = stakes;
        _opts = options;
        _clock = clock ?? new SystemClock();
        _wire = wire;
        _log = log;
        _person = person;
        try { Load(); }
        catch (Exception ex) { _log?.LogWarning(ex, "турнір слотів: не прочитав спроби — починаю з чистого"); }
    }

    public SlotTourneyOptions O => _opts();
    public DateTimeOffset Now => _clock.UtcNow;

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
        catch (Exception ex) { _log?.LogWarning(ex, "турнір слотів: не прочитав акаунти й ботів"); }
    }

    // ---------- оберт (під замком кімнати: лише пам'ять) ----------

    /// <summary>
    /// Турнірний оберт: сьогоднішня спроба (чи незакінчена з учора — того ж тижня), математика автомата зі станом спроби.
    /// Повертає текст відмови або null і результат у <paramref name="result"/>.
    /// </summary>
    public string? Spin(string nick, string gameId, ISlotMath math, ISlotRng rng, DateTimeOffset now, out TourneySpin? result)
    {
        result = null;
        var o = O;
        if (!o.Enabled) return "Турніру зараз нема — Глек рахує фішки";
        var key = EconomyStore.Key(nick);
        if (key.Length == 0) return "Тут так не ходять";
        var monday = SlotWeek.Monday(now);
        var week = SlotWeek.Id(monday);
        if (now >= SlotWeek.Close(monday)) return "Турнір тижня вже підбито — новий з понеділка";
        var game = SlotWeek.Game(monday);
        if (gameId != game) return $"Турнір тижня — у «{SlotWeek.Title(game)}»";
        var bet = o.BetSafe;
        var day = Days.Of(now);
        lock (_gate)
        {
            var a = _att.Values.FirstOrDefault(x => x.Key == key && x.Week == week && !x.Done);
            if (a is null)
            {
                if (_att.ContainsKey((day, key))) return "Сьогоднішню спробу вже зіграно — завтра нова";
                a = new Attempt { Week = week, Day = day, Key = key, Nick = nick.Trim(), Game = game, Chips = o.Chips, At = now };
                _att[(day, key)] = a;
            }
            if (a.Chips < bet)
            {
                a.Done = true;
                _dirty.Add((a.Day, a.Key));
                return "Фішки скінчились — спробу зараховано";
            }
            var outcome = math.Spin(bet, rng, a.Math);
            a.Math = outcome.State;
            a.Chips = a.Chips - bet + outcome.Win;
            a.Spins++;
            a.At = now;
            a.Nick = nick.Trim();
            if (a.Spins >= o.Spins || a.Chips < bet) a.Done = true;
            _dirty.Add((a.Day, a.Key));
            result = new TourneySpin(outcome.Script, a.Chips, a.Spins, o.Spins, a.Done);
            return null;
        }
    }

    // ---------- табло ----------

    /// <summary>Табло тижня: лише люди; у залік — найкраща закінчена спроба (<paramref name="all"/> — і незакінчені).</summary>
    public IReadOnlyList<TourneyRow> Board(string week, bool all = false)
    {
        lock (_gate) return BoardLocked(week, all);
    }

    List<TourneyRow> BoardLocked(string week, bool all) =>
        [.. _att.Values.Where(a => a.Week == week && IsPerson(a.Nick)).GroupBy(a => a.Key)
            .Select(g =>
            {
                var best = g.Where(a => all || a.Done).OrderByDescending(a => a.Chips).ThenBy(a => a.At).FirstOrDefault();
                var last = g.OrderByDescending(a => a.At).First();
                return best is null ? null : new TourneyRow(g.Key, last.Nick, best.Chips, best.At, g.Count());
            })
            .OfType<TourneyRow>()
            .OrderByDescending(r => r.Best).ThenBy(r => r.At)];

    /// <summary>
    /// Вид турніру для автомата <paramref name="gameId"/> і гравця <paramref name="nick"/> (null — без «моє»);
    /// null — турнір вимкнено.
    /// </summary>
    public object? Wire(string? gameId, string? nick, DateTimeOffset now, int top = 10)
    {
        var o = O;
        if (!o.Enabled) return null;
        var monday = SlotWeek.Monday(now);
        var week = SlotWeek.Id(monday);
        var close = SlotWeek.Close(monday);
        var game = SlotWeek.Game(monday);
        var key = nick is null ? "" : EconomyStore.Key(nick);
        var day = Days.Of(now);
        lock (_gate)
        {
            var board = BoardLocked(week, false);
            object? my = null;
            if (key.Length > 0)
            {
                var a = _att.Values.FirstOrDefault(x => x.Key == key && x.Week == week && !x.Done)
                    ?? (_att.TryGetValue((day, key), out var t) && t.Week == week ? t : null);
                if (a is not null) my = new { chips = a.Chips, spin = a.Spins, of = o.Spins, left = Math.Max(0, o.Spins - a.Spins), done = a.Done };
            }
            var place = key.Length == 0 ? -1 : board.FindIndex(r => r.Key == key);
            return new
            {
                week, game, title = SlotWeek.Title(game), here = gameId == game,
                ends = close.UtcDateTime, open = now < close,
                chips = o.Chips, spins = o.Spins, bet = o.BetSafe, prizes = o.PrizeList, minPlayers = o.MinPlayers,
                person = nick is not null && IsPerson(nick),
                my,
                mine = place < 0 ? null : new { place = place + 1, best = board[place].Best, tries = board[place].Tries },
                players = board.Count,
                board = board.Take(top).Select((r, i) => new { place = i + 1, nick = r.Nick, best = r.Best, tries = r.Tries, me = r.Key == key }),
            };
        }
    }

    // ---------- призи ----------

    /// <summary>Рядок у Балачки після виплати.</summary>
    public static string Line(string game, IReadOnlyList<TourneyRow> winners) =>
        $"🏆 Турнір тижня ({SlotWeek.Title(game)}): "
        + string.Join(" · ", winners.Select((r, i) => $"{i + 1}. {r.Nick} {Num(r.Best)}"))
        + " — призи від Дядька Глека";

    static readonly NumberFormatInfo Nf = new() { NumberGroupSeparator = " ", NumberGroupSizes = [3] };
    static string Num(int n) => n.ToString("#,0", Nf);

    /// <summary>
    /// Підбити всі тижні, що вже закінчились і ще не оплачені: незакінчені спроби зараховуються як є, призи — ключем
    /// <c>slot-prize:{тиждень}:{місце}</c> (повтор не платить двічі), тиждень позначається оплаченим, рядок — у Балачки.
    /// Повертає, скільки тижнів підбито.
    /// </summary>
    public int Settle(DateTimeOffset now)
    {
        List<string> weeks;
        lock (_gate)
            weeks = [.. _att.Values.Select(a => a.Week).Distinct().Where(w => !_paid.Contains(w) && SlotWeek.Close(w) <= now).Order()];
        var done = 0;
        foreach (var week in weeks)
        {
            var o = O;
            List<TourneyRow> board;
            lock (_gate)
            {
                foreach (var a in _att.Values.Where(a => a.Week == week && !a.Done))
                {
                    a.Done = true;
                    _dirty.Add((a.Day, a.Key));
                }
                board = BoardLocked(week, true);
            }
            var prizes = o.PrizeList;
            var winners = board.Count >= Math.Max(1, o.MinPlayers) ? board.Take(prizes.Length).ToList() : [];
            var game = SlotWeek.Game(SlotWeek.Parse(week));
            try
            {
                for (var i = 0; i < winners.Count; i++)
                    if (prizes[i] > 0 && _stakes is not null)
                        _stakes.Grant(winners[i].Nick, prizes[i], $"slot-prize:{week}", $"slot-prize:{week}:{i + 1}");
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "турнір слотів: призи тижня {Week} не пройшли — спробую згодом", week);
                continue;
            }
            var summary = winners.Count > 0 ? Line(game, winners) : $"без призів: людей {board.Count}";
            lock (_gate) _paid.Add(week);
            try
            {
                _db?.Exec("INSERT OR IGNORE INTO slot_tourney_paid(week, at, summary) VALUES($w, $t, $s)",
                    ("$w", week), ("$t", now.UtcDateTime.ToString("o")), ("$s", summary));
            }
            catch (Exception ex) { _log?.LogWarning(ex, "турнір слотів: не записав оплату тижня {Week}", week); }
            done++;
            if (winners.Count > 0 && _wire is not null)
            {
                try { _wire.Chat(summary); }
                catch (Exception ex) { _log?.LogWarning(ex, "турнір слотів: рядок у Балачки не пішов"); }
            }
            _log?.LogInformation("турнір слотів: тиждень {Week} підбито — {Summary}", week, summary);
        }
        if (done > 0) Prune(now);
        return done;
    }

    public bool Paid(string week) { lock (_gate) return _paid.Contains(week); }

    /// <summary>Оплачені минулі тижні з пам'яті геть (у базі лишаються). Незаписане спершу пишемо.</summary>
    void Prune(DateTimeOffset now)
    {
        FlushNow();
        var current = SlotWeek.Id(now);
        lock (_gate)
            foreach (var k in _att.Where(p => p.Value.Week != current && _paid.Contains(p.Value.Week) && !_dirty.Contains(p.Key)).Select(p => p.Key).ToList())
                _att.Remove(k);
    }

    // ---------- сховище ----------

    void Load()
    {
        if (_db is null) return;
        _db.Exec(Schema);
        var rows = _db.With(c =>
        {
            var paid = new List<string>();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT week FROM slot_tourney_paid";
                using var r = cmd.ExecuteReader();
                while (r.Read()) paid.Add(r.GetString(0));
            }
            var list = new List<Attempt>();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT week, day, nick_key, nick, game, chips, spins, done, at, math FROM slot_tourney
                    WHERE week NOT IN (SELECT week FROM slot_tourney_paid) OR week >= $w
                    """;
                cmd.Parameters.AddWithValue("$w", SlotWeek.Id(_clock.UtcNow));
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    JsonObject math;
                    try { math = (r.IsDBNull(9) ? null : JsonNode.Parse(r.GetString(9)) as JsonObject) ?? new JsonObject(); }
                    catch (JsonException) { math = new JsonObject(); }
                    list.Add(new Attempt
                    {
                        Week = r.GetString(0), Day = r.GetString(1), Key = r.GetString(2), Nick = r.GetString(3), Game = r.GetString(4),
                        Chips = r.GetInt32(5), Spins = r.GetInt32(6), Done = r.GetInt32(7) != 0,
                        At = DateTimeOffset.Parse(r.GetString(8), CultureInfo.InvariantCulture), Math = math,
                    });
                }
            }
            return (paid, list);
        });
        lock (_gate)
        {
            foreach (var w in rows.paid) _paid.Add(w);
            foreach (var a in rows.list) _att[(a.Day, a.Key)] = a;
        }
        RefreshPeople();
    }

    /// <summary>Записати змінені спроби. Знімок — під замком, SQLite — поза ним.</summary>
    public void FlushNow()
    {
        if (_db is null) { lock (_gate) _dirty.Clear(); return; }
        List<(string Week, string Day, string Key, string Nick, string Game, int Chips, int Spins, bool Done, string At, string Math)> snap;
        lock (_gate)
        {
            if (_dirty.Count == 0) return;
            snap = [.. _dirty.Where(_att.ContainsKey).Select(k => _att[k])
                .Select(a => (a.Week, a.Day, a.Key, a.Nick, a.Game, a.Chips, a.Spins, a.Done, a.At.UtcDateTime.ToString("o"), a.Math.ToJsonString()))];
            _dirty.Clear();
        }
        try
        {
            _db.With(c =>
            {
                using var tx = c.BeginTransaction();
                foreach (var a in snap)
                {
                    using var cmd = c.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT OR REPLACE INTO slot_tourney(week, day, nick_key, nick, game, chips, spins, done, at, math)
                        VALUES($w, $d, $k, $n, $g, $c, $s, $done, $at, $m)
                        """;
                    cmd.Parameters.AddWithValue("$w", a.Week);
                    cmd.Parameters.AddWithValue("$d", a.Day);
                    cmd.Parameters.AddWithValue("$k", a.Key);
                    cmd.Parameters.AddWithValue("$n", a.Nick);
                    cmd.Parameters.AddWithValue("$g", a.Game);
                    cmd.Parameters.AddWithValue("$c", a.Chips);
                    cmd.Parameters.AddWithValue("$s", a.Spins);
                    cmd.Parameters.AddWithValue("$done", a.Done ? 1 : 0);
                    cmd.Parameters.AddWithValue("$at", a.At);
                    cmd.Parameters.AddWithValue("$m", a.Math);
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            });
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "турнір слотів: спроби не записались — спробую згодом");
            lock (_gate) foreach (var a in snap) _dirty.Add((a.Day, a.Key));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Settle(_clock.UtcNow);
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
                catch (Exception ex) { _log?.LogWarning(ex, "турнір слотів: підбиття не вдалось"); }
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

/// <summary>Підключення: опції <c>Slots:Tourney</c>, сервіс, <c>GET /api/slots/tourney</c>.</summary>
public static class SlotTourneySetup
{
    public static IServiceCollection Add(IServiceCollection services)
    {
        services.AddOptions<SlotTourneyOptions>().BindConfiguration("Slots:Tourney");
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptionsMonitor<SlotTourneyOptions>>();
            return new SlotTourney(sp.GetService<Db>(), sp.GetService<IStakes>(), () => opts.CurrentValue, sp.GetService<IClock>(),
                sp.GetService<ISlotsWire>(), sp.GetService<ILogger<SlotTourney>>());
        });
        services.AddHostedService(sp => sp.GetRequiredService<SlotTourney>());
        return services;
    }

    /// <summary><c>GET /api/slots/tourney</c> — турнір тижня для лобі «Азарт»: автомат, кінець, перші п'ять (без «моє»).</summary>
    public static void Map(WebApplication app) =>
        app.MapGet("/api/slots/tourney", (SlotTourney t) =>
            t.Wire(null, null, t.Now, 5) is { } w ? Results.Json(w) : Results.Json(new { off = true }));
}
