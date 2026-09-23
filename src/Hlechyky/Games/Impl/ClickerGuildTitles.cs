using System.Text.Json.Nodes;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Що гончар каже цеху про свої звання (docs/games/specs/clicker-titles.md): числа для «перших в окрузі», обрані
/// значки, що має назавжди, і сьогоднішні лічильники для звань дня.
/// </summary>
public sealed record TitleReport(
    IReadOnlyDictionary<string, double> Values, IReadOnlyList<string> Show, IReadOnlyList<string> Has,
    string Day, TitleDayReport? Today);

/// <summary>
/// Лічильники одного київського дня: кліки, кліки з півночі до п'ятої, спіймані з полиці, перший клік після п'ятої
/// ранку і приріст глеків за день (частка: 0,5 — плюс половина).
/// </summary>
public sealed record TitleDayReport(long Clicks, long Night, int Catch, DateTimeOffset? Rooster, double Growth);

/// <summary>
/// Звання округи — спільна частина. Хто тримає кожне «перше в окрузі» звання, хто вчора виграв звання дня і хто
/// веде сьогодні, хто першим вибив рідкісне чи таємне. Кімната звітує раз на хвилину (<see cref="TitlesReport"/>);
/// гончарів, яких цех знає лише зі списку, один раз дочитуємо з їхніх збережень, щоб звання не дісталось тому, хто
/// просто першим зайшов після оновлення.
/// </summary>
public sealed partial class ClickerGuildService
{
    /// <summary>«Перші в окрузі» рахуються лише серед тих, хто грав за останні стільки днів.</summary>
    public const int TitleActiveDays = 14;
    /// <summary>Хто вважається округою для «Кругової поруки».</summary>
    public const int CircleActiveDays = 7;
    /// <summary>Хоч скільки разів двоє перехоплюють одне звання, Журнал каже про це не частіше.</summary>
    public static readonly TimeSpan TitleLogQuiet = TimeSpan.FromMinutes(30);
    /// <summary>Скільки днів лічильників тримати: учорашній (його переможці тримають звання сьогодні), сьогоднішній і запас.</summary>
    public const int TitleDaysKept = 3;
    /// <summary>Пороги звань дня: менше — ще не звання.</summary>
    public const long BeeMin = 100, NightMin = 50;
    public const int CatchMin = 5;

    sealed partial class State
    {
        public Dictionary<string, TitleStatsRow> TitleStats { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, TitleHoldRow> TitleHolds { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, Dictionary<string, TitleDayRow>> TitleDays { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, TitleFirstRow> TitleFirsts { get; set; } = new(StringComparer.Ordinal);
    }

    sealed class TitleStatsRow
    {
        public string Nick { get; set; } = "";
        public DateTimeOffset Seen { get; set; }
        public Dictionary<string, double> Values { get; set; } = new(StringComparer.Ordinal);
        public List<string> Show { get; set; } = [];
        public List<string> Has { get; set; } = [];
    }

    /// <summary>Хто тримає «перше в окрузі» звання, з яким числом, відколи і коли про нього востаннє казали в Журнал.</summary>
    sealed class TitleHoldRow
    {
        public string Key { get; set; } = "";
        public string Nick { get; set; } = "";
        public double Value { get; set; }
        public DateTimeOffset At { get; set; }
        public DateTimeOffset LoggedAt { get; set; }
    }

    sealed class TitleDayRow
    {
        public string Nick { get; set; } = "";
        public long Clicks { get; set; }
        public long Night { get; set; }
        public int Catch { get; set; }
        public DateTimeOffset? Rooster { get; set; }
        public double Growth { get; set; }
    }

    sealed class TitleFirstRow
    {
        public string Nick { get; set; } = "";
        public DateTimeOffset At { get; set; }
    }

    static void NormalizeTitles(State s)
    {
        s.TitleStats = Clean(s.TitleStats);
        s.TitleHolds = Clean(s.TitleHolds);
        s.TitleFirsts = Clean(s.TitleFirsts);
        s.TitleDays = Clean(s.TitleDays);
        foreach (var (day, rows) in s.TitleDays.ToList()) s.TitleDays[day] = Clean(rows);
        foreach (var row in s.TitleStats.Values)
        {
            row.Values = Clean(row.Values);
            row.Show = row.Show?.Where(x => x is { Length: > 0 }).ToList() ?? [];
            row.Has = row.Has?.Where(x => x is { Length: > 0 }).ToList() ?? [];
        }
    }

    /// <summary>Змінюється з кожним звітом: за ним кешуємо переможців дня.</summary>
    long _titlesVersion;
    DateTimeOffset _titlesRetryAt;
    (string Day, long Version, Dictionary<string, (string Key, string Nick, double Value)> Won)? _dayWon;

    // ---------- звіт кімнати ----------

    /// <summary>
    /// Гончар звітує: свої числа, обрані значки, що має, і лічильники дня. Повертає звання, які він щойно перехопив
    /// у когось іншого, — кімната скаже про це в Журнал. Перше призначення після оновлення тихе: інакше Журнал
    /// засипало б тринадцятьма рядками, хоча ніхто нічого не робив.
    /// </summary>
    public IReadOnlyList<(string Title, string From)> TitlesReport(string key, string nick, DateTimeOffset now, TitleReport r)
    {
        if (key.Length == 0) return [];
        EnsureTitleStats();
        lock (_lock)
        {
            var s = S();
            s.TitleStats[key] = new TitleStatsRow
            {
                Nick = nick,
                Seen = now,
                Values = r.Values.Where(kv => double.IsFinite(kv.Value) && kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
                Show = r.Show.Take(Clicker.BadgesMax).ToList(),
                Has = r.Has.ToList(),
            };
            if (r.Today is { } d && r.Day.Length > 0)
            {
                if (!s.TitleDays.TryGetValue(r.Day, out var rows)) s.TitleDays[r.Day] = rows = new(StringComparer.Ordinal);
                rows[key] = new TitleDayRow
                {
                    Nick = nick, Clicks = Math.Max(0, d.Clicks), Night = Math.Max(0, d.Night), Catch = Math.Max(0, d.Catch),
                    Rooster = d.Rooster, Growth = double.IsFinite(d.Growth) ? Math.Max(0, d.Growth) : 0,
                };
                // Старі дні — геть: лишаємо найсвіжіші, рядки-дати порівнюються як дати.
                foreach (var old in s.TitleDays.Keys.OrderByDescending(x => x, StringComparer.Ordinal).Skip(TitleDaysKept).ToList())
                    s.TitleDays.Remove(old);
            }
            var taken = RecomputeHolds(s, now, key);
            _titlesVersion++;
            Save();
            return taken;
        }
    }

    /// <summary>
    /// Перерахувати, хто тримає кожне «перше в окрузі» звання. Рівні числа — звання лишається в того, хто вже його
    /// тримав. Журналові кажемо лише тоді, коли звання перехопив саме <paramref name="reporter"/> і не частіше за
    /// <see cref="TitleLogQuiet"/>.
    /// </summary>
    List<(string Title, string From)> RecomputeHolds(State s, DateTimeOffset now, string reporter)
    {
        var taken = new List<(string, string)>();
        var from = now.AddDays(-TitleActiveDays);
        foreach (var t in Clicker.Titles.Where(x => x.Kind == Clicker.TitleTop))
        {
            var best = s.TitleStats
                .Where(x => x.Value.Seen >= from && x.Value.Values.GetValueOrDefault(t.Key) > 0)
                .Select(x => (Key: x.Key, Row: x.Value, Value: x.Value.Values[t.Key]))
                .ToList();
            s.TitleHolds.TryGetValue(t.Key, out var hold);
            if (best.Count == 0)
            {
                if (hold is not null) s.TitleHolds.Remove(t.Key);
                continue;
            }
            var top = best.Max(x => x.Value);
            var keep = hold is not null ? best.FirstOrDefault(x => x.Key == hold.Key && x.Value >= top) : default;
            if (keep.Row is not null)
            {
                hold!.Value = keep.Value;
                hold.Nick = keep.Row.Nick;
                continue;
            }
            var win = best.Where(x => x.Value >= top).OrderBy(x => x.Key, StringComparer.Ordinal).First();
            var fresh = new TitleHoldRow { Key = win.Key, Nick = win.Row.Nick, Value = win.Value, At = now, LoggedAt = hold?.LoggedAt ?? default };
            // «Забирає» — лише в того, хто ще грає: звання, що звільнилось за давністю, переходить мовчки.
            var rival = hold is not null && best.Any(x => x.Key == hold.Key);
            if (rival && win.Key == reporter && hold!.Key != reporter && now - hold.LoggedAt >= TitleLogQuiet)
            {
                taken.Add((t.Key, hold.Nick));
                fresh.LoggedAt = now;
            }
            s.TitleHolds[t.Key] = fresh;
        }
        return taken;
    }

    /// <summary>
    /// Гончарі зі списку цеху, про чиї звання цех ще не чув, — один раз зі збереження (<see cref="Clicker.TitleStatsFromSave"/>).
    /// Базу чіпаємо поза замком цеху й не частіше, ніж раз на хвилину, якщо минулого разу вона не відповіла.
    /// </summary>
    void EnsureTitleStats()
    {
        List<(string Key, string Nick, DateTimeOffset Seen)> unknown;
        lock (_lock)
        {
            if (_clock.UtcNow < _titlesRetryAt) return;
            var s = S();
            unknown = s.Potters.Where(x => !s.TitleStats.ContainsKey(x.Key)).Select(x => (x.Key, x.Value.Nick, x.Value.Seen)).ToList();
        }
        if (unknown.Count == 0) return;
        var read = new List<(string Key, string Nick, DateTimeOffset Seen, Clicker.SavedTitles Stats)>();
        var failed = false;
        foreach (var u in unknown)
        {
            try { read.Add((u.Key, u.Nick, u.Seen, Clicker.TitleStatsFromSave(_store.LoadState("clicker:" + u.Key)))); }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "звання гончаря {Nick} не прочитались", u.Key);
                failed = true;
            }
        }
        lock (_lock)
        {
            var s = S();
            foreach (var (key, nick, seen, stats) in read)
                if (!s.TitleStats.ContainsKey(key))
                    s.TitleStats[key] = new TitleStatsRow
                    {
                        Nick = nick, Seen = seen,
                        Values = new Dictionary<string, double>(stats.Values, StringComparer.Ordinal),
                        Show = stats.Show.ToList(), Has = stats.Has.ToList(),
                    };
            if (failed) _titlesRetryAt = _clock.UtcNow.AddMinutes(1);
            if (read.Count > 0)
            {
                // Перше знайомство — теж перерахунок, але тихий: ніхто нічого ні в кого не забирав.
                RecomputeHolds(s, _clock.UtcNow, "");
                _titlesVersion++;
                Save();
            }
        }
    }

    // ---------- що тримає гончар ----------

    /// <summary>Звання, які <paramref name="key"/> тримає просто зараз: «перші в окрузі» й учорашні звання дня.</summary>
    public IReadOnlyList<string> TitlesHeld(string key, DateTimeOffset now)
    {
        if (key.Length == 0) return [];
        lock (_lock)
        {
            var s = S();
            var held = s.TitleHolds.Where(x => x.Value.Key == key).Select(x => x.Key).ToList();
            foreach (var (title, won) in DayWinners(s, now))
                if (won.Key == key) held.Add(title);
            return held;
        }
    }

    /// <summary>
    /// Переможці вчорашнього дня за кожним званням дня (вони й тримають звання весь сьогоднішній день). Кешуємо, доки
    /// не прийшов новий звіт: вид питає це з кожною пачкою кліків.
    /// </summary>
    Dictionary<string, (string Key, string Nick, double Value)> DayWinners(State s, DateTimeOffset now)
    {
        var day = DayBefore(now);
        if (_dayWon is { } c && c.Day == day && c.Version == _titlesVersion) return c.Won;
        var won = s.TitleDays.TryGetValue(day, out var rows) ? DayLeaders(rows) : new();
        _dayWon = (day, _titlesVersion, won);
        return won;
    }

    /// <summary>Хто веде за кожним званням дня серед рядків одного дня. Рівні — за абеткою ключів, щоб не смикалось.</summary>
    static Dictionary<string, (string Key, string Nick, double Value)> DayLeaders(Dictionary<string, TitleDayRow> rows)
    {
        var r = new Dictionary<string, (string, string, double)>(StringComparer.Ordinal);
        void Max(string title, Func<TitleDayRow, double> of, double min)
        {
            var best = rows.Where(x => of(x.Value) >= min && of(x.Value) > 0)
                .OrderByDescending(x => of(x.Value)).ThenBy(x => x.Key, StringComparer.Ordinal).FirstOrDefault();
            if (best.Value is not null) r[title] = (best.Key, best.Value.Nick, of(best.Value));
        }
        Max("bee", x => x.Clicks, BeeMin);
        Max("night", x => x.Night, NightMin);
        Max("catch", x => x.Catch, CatchMin);
        Max("rising", x => x.Growth, 0);
        var early = rows.Where(x => x.Value.Rooster is not null)
            .OrderBy(x => x.Value.Rooster).ThenBy(x => x.Key, StringComparer.Ordinal).FirstOrDefault();
        if (early.Value?.Rooster is { } at) r["rooster"] = (early.Key, early.Value.Nick, at.ToUnixTimeMilliseconds());
        return r;
    }

    // ---------- рідкісні й таємні: хто перший ----------

    /// <summary>Гончар вибив рідкісне чи таємне звання. true — він перший в окрузі (тоді таємне стає відомим усім).</summary>
    public bool TitleEarned(string key, string nick, string title, DateTimeOffset now)
    {
        if (key.Length == 0) return false;
        lock (_lock)
        {
            var s = S();
            if (s.TitleFirsts.ContainsKey(title)) return false;
            s.TitleFirsts[title] = new TitleFirstRow { Nick = nick, At = now };
            _titlesVersion++;
            Save();
            return true;
        }
    }

    /// <summary>Чи таємне звання вже хтось вибив — тоді його назву й умову видно всім.</summary>
    public bool TitleRevealed(string title)
    {
        lock (_lock) return S().TitleFirsts.ContainsKey(title);
    }

    /// <summary>Хто з округи грав за останні <paramref name="days"/> днів, крім самого <paramref name="key"/>.</summary>
    public IReadOnlyList<string> ActiveOthers(string key, DateTimeOffset now, int days)
    {
        lock (_lock)
        {
            var from = now.AddDays(-days);
            return S().Potters.Where(x => x.Key != key && x.Value.Seen >= from).Select(x => x.Key).ToList();
        }
    }

    // ---------- для списку цеху й хати друга ----------

    /// <summary>Усе, що гончар має зараз: зароблене (зі звіту чи збереження) плюс те, що тримає в окрузі.</summary>
    List<string> HasOf(State s, string key, DateTimeOffset now)
    {
        var has = s.TitleStats.TryGetValue(key, out var row) ? row.Has.ToList() : [];
        has.AddRange(s.TitleHolds.Where(x => x.Value.Key == key).Select(x => x.Key));
        foreach (var (title, won) in DayWinners(s, now))
            if (won.Key == key) has.Add(title);
        return has.Distinct(StringComparer.Ordinal).Where(k => Clicker.TitleDef(k) is not null).ToList();
    }

    /// <summary>Значки біля ніка в списку цеху: обрані (з тих, що ще є) або найрідкісніші.</summary>
    object[] BadgesOf(State s, string key, DateTimeOffset now)
    {
        var show = s.TitleStats.TryGetValue(key, out var row) ? row.Show : [];
        return Clicker.Badges(HasOf(s, key, now), show)
            .Select(k => Clicker.TitleDef(k)!)
            .Select(t => (object)new { key = t.Key, icon = t.Icon, name = t.Name })
            .ToArray();
    }

    /// <summary>Дошка звань для вкладки «Село»: хто що тримає, хто веде сьогодні, хто перший вибив і відкриті таємні.</summary>
    object TitleBoard(State s, DateTimeOffset now)
    {
        var today = s.TitleDays.TryGetValue(DayOf(now), out var rows) ? DayLeaders(rows) : new();
        return new
        {
            tops = s.TitleHolds.ToDictionary(x => x.Key, x => (object)new { nick = x.Value.Nick, v = x.Value.Value, at = x.Value.At }, StringComparer.Ordinal),
            days = DayWinners(s, now).ToDictionary(x => x.Key, x => (object)new { nick = x.Value.Nick, v = x.Value.Value }, StringComparer.Ordinal),
            lead = today.ToDictionary(x => x.Key, x => (object)new { nick = x.Value.Nick, v = x.Value.Value }, StringComparer.Ordinal),
            firsts = s.TitleFirsts.ToDictionary(x => x.Key, x => (object)new { nick = x.Value.Nick, at = x.Value.At }, StringComparer.Ordinal),
            // Таємне звання, яке хтось уже вибив (чи має — «Вічного учня» цех бачить і зі збереження), відкривається всім:
            // назва, значок і умова. Інакше воно світилось би біля ніка в списку й лишалось «???» у розділі звань.
            secrets = Clicker.Titles.Where(t => t.Kind == Clicker.TitleSecret
                    && (s.TitleFirsts.ContainsKey(t.Key) || s.TitleStats.Values.Any(r => r.Has.Contains(t.Key))))
                .ToDictionary(t => t.Key, t => (object)new { icon = t.Icon, name = t.Name, desc = t.Desc }, StringComparer.Ordinal),
        };
    }

    /// <summary>Стіна звань у хаті друга: що він має зараз, найрідкісніші спершу, з датою для зароблених назавжди.</summary>
    object[] WallOf(State s, string key, DateTimeOffset now, IReadOnlyDictionary<string, DateTimeOffset> earned)
    {
        var has = HasOf(s, key, now);
        // «Вічний учень» у збереженні — лише позначка «про нього вже казали»: він живий, поки гончар учень, і це знає звіт.
        foreach (var k in earned.Keys) if (!has.Contains(k) && k != Clicker.TitleApprentice && Clicker.TitleDef(k) is not null) has.Add(k);
        return Clicker.ByRarity(has)
            .Select(k => Clicker.TitleDef(k)!)
            .Select(t => (object)new
            {
                key = t.Key, icon = t.Icon, name = t.Name, desc = t.Desc, kind = t.Kind,
                at = earned.TryGetValue(t.Key, out var at) ? at : (DateTimeOffset?)null,
            })
            .ToArray();
    }
}
