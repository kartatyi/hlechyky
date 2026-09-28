using System.Collections.Concurrent;
using System.Globalization;
using Hlechyky.Games.Economy;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>Рядок табло дня на картці: хто, за скільки спроб і часу, скільки днів поспіль (🔥).</summary>
public sealed record DayCardRow(string Nick, int Attempts, int Ms, int Streak);

/// <summary>
/// Табло дня на самій картці щоденної гри (Сапер дня, Цеглини дня): «Сьогодні: Оля 1:23 🔥4 · Петро 2:10» і хто з
/// тих, що грали цього тижня, сьогодні ще не проходив (їхній вогник от-от згасне).
/// </summary>
public sealed record DayCard(string Day, IReadOnlyList<DayCardRow> Rows, IReadOnlyList<DayCardRow> Waiting, int Ver);

/// <summary>
/// Табло дня й серії 🔥 для карток щоденних ігор. Гра звертається лише до пам'яті (під замком кімнати — жодного
/// SQLite): день і серії дочитуються з <c>daily_results</c> фоном, а свіжий результат кімната докладає сама
/// (<see cref="Note"/>) — тож сусід по сайту бачить його одразу, не чекаючи бази. Каркасну таблицю щоденних не пише:
/// туди результат кладе <c>Rewards</c> зі <c>Ctx.Score</c>, як і раніше.
/// </summary>
public sealed class DailyCard(Db? db, IClock clock)
{
    /// <summary>Скільки рядків табло й «ще не проходили» віддаємо у вид — більше на картці не влізе.</summary>
    public const int MaxRows = 8, MaxWaiting = 6;
    /// <summary>Хто грав за останні стільки днів, а сьогодні ще ні, — у «ще не проходили».</summary>
    public const int WaitDays = 7;
    /// <summary>Серію рахуємо не далі, ніж на стільки днів назад.</summary>
    const int StreakWindow = 400;
    /// <summary>Як часто перечитувати день із бази: результати з цього процесу приходять і так, це — страховка.</summary>
    static readonly TimeSpan Stale = TimeSpan.FromMinutes(10);

    sealed class Snap
    {
        public string Day = "";
        /// <summary>Нік-ключ → найкращий результат сьогодні.</summary>
        public readonly Dictionary<string, (string Nick, int A, int Ms)> Today = new(StringComparer.Ordinal);
        /// <summary>Нік-ключ → (нік, серія до вчора включно, останній день до сьогодні).</summary>
        public readonly Dictionary<string, (string Nick, int Streak, string Last)> Past = new(StringComparer.Ordinal);
        public DateTimeOffset LoadedAt;
        public bool Loaded;
        public int Ver;
        public DayCard? Card;
    }

    readonly ConcurrentDictionary<string, Snap> _snaps = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, bool> _loading = new(StringComparer.Ordinal);

    public static void Add(IServiceCollection services)
    {
        services.AddSingleton(sp => new DailyCard(sp.GetService<Db>(), sp.GetRequiredService<IClock>()));
        services.AddSingleton(sp => new MinesGhosts(sp.GetService<Db>(), sp.GetRequiredService<IClock>()));
        services.AddSingleton(sp => new TyperaceChat(sp.GetService<Db>(), sp.GetRequiredService<IClock>()));
        services.AddHostedService(sp => new TyperaceChat.WarmUp(sp.GetRequiredService<TyperaceChat>()));
    }

    static string Key(string nick) => EconomyStore.Key(nick);

    static string Shift(string day, int by) =>
        DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(by).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Прогріти табло гри заздалегідь (з Configure — поза замком). Не чекає на базу.</summary>
    public void Warm(string game) => Get(game);

    /// <summary>Версія табло: міняється з кожним новим результатом чи дочитаною базою (реалтайм-гра шле вид лише тоді).</summary>
    public int Version(string game) => _snaps.TryGetValue(game, out var s) ? s.Ver : 0;

    /// <summary>Табло на сьогодні з пам'яті; null — день ще не дочитано (прийде з наступним видом).</summary>
    public DayCard? Get(string game)
    {
        var day = Days.Today(clock);
        var snap = _snaps.GetOrAdd(game, _ => new Snap { Day = day });
        lock (snap)
        {
            if (snap.Day != day)
            {
                // новий день: учорашнє «сьогодні» стало минулим — перечитуємо все фоном
                snap.Day = day;
                snap.Today.Clear();
                snap.Past.Clear();
                snap.Loaded = false;
                snap.Card = null;
                snap.Ver++;
            }
            if (db is not null && (!snap.Loaded || clock.UtcNow - snap.LoadedAt > Stale) && _loading.TryAdd(game, true))
                _ = Task.Run(() => Load(game, day));
            if (!snap.Loaded && db is not null) return null;
            return snap.Card ??= Build(snap);
        }
    }

    /// <summary>Кімната щойно записала результат дня: докласти в пам'ять (краще — спроби, потім час — перекриває гірше).</summary>
    public void Note(string game, string nick, int attempts, int ms)
    {
        var day = Days.Today(clock);
        var snap = _snaps.GetOrAdd(game, _ => new Snap { Day = day });
        lock (snap)
        {
            if (snap.Day != day) return;   // межа доби посеред партії — наступне читання дня однаково перечитає базу
            var k = Key(nick);
            if (snap.Today.TryGetValue(k, out var was) && (was.A < attempts || (was.A == attempts && was.Ms <= ms))) return;
            snap.Today[k] = (nick, attempts, ms);
            snap.Card = null;
            snap.Ver++;
        }
    }

    /// <summary>Серія ніка на сьогодні (з пам'яті): до вчора включно плюс сьогодні, якщо вже пройдено.</summary>
    public int StreakOf(string game, string nick)
    {
        if (!_snaps.TryGetValue(game, out var snap)) return 0;
        lock (snap)
        {
            var k = Key(nick);
            var past = snap.Past.TryGetValue(k, out var p) ? p.Streak : 0;
            return past + (snap.Today.ContainsKey(k) ? 1 : 0);
        }
    }

    static DayCard Build(Snap s)
    {
        var rows = s.Today.Select(kv => new DayCardRow(kv.Value.Nick, kv.Value.A, kv.Value.Ms,
                (s.Past.TryGetValue(kv.Key, out var p) ? p.Streak : 0) + 1))
            .OrderBy(r => r.Attempts).ThenBy(r => r.Ms).ThenBy(r => r.Nick, StringComparer.Ordinal)
            .Take(MaxRows).ToList();
        var since = Shift(s.Day, -WaitDays);
        var waiting = s.Past.Where(kv => !s.Today.ContainsKey(kv.Key) && string.CompareOrdinal(kv.Value.Last, since) >= 0)
            .OrderByDescending(kv => kv.Value.Streak).ThenByDescending(kv => kv.Value.Last, StringComparer.Ordinal)
            .Take(MaxWaiting).Select(kv => new DayCardRow(kv.Value.Nick, 0, 0, kv.Value.Streak)).ToList();
        return new DayCard(s.Day, rows, waiting, s.Ver);
    }

    /// <summary>Лише для тестів: дочитати день із бази зараз.</summary>
    public void LoadNow(string game) => Load(game, Days.Today(clock));

    void Load(string game, string day)
    {
        try
        {
            var since = Shift(day, -StreakWindow);
            var rows = new List<(string Key, string Nick, string Day, int A, int Ms)>();
            db!.With(c =>
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT nick_key, nick, day, attempts, ms FROM daily_results WHERE game = $g AND solved = 1 AND day >= $s AND day <= $d";
                cmd.Parameters.AddWithValue("$g", game);
                cmd.Parameters.AddWithValue("$s", since);
                cmd.Parameters.AddWithValue("$d", day);
                using var r = cmd.ExecuteReader();
                while (r.Read()) rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4)));
            });
            var byNick = rows.GroupBy(r => r.Key, StringComparer.Ordinal);
            var snap = _snaps.GetOrAdd(game, _ => new Snap { Day = day });
            lock (snap)
            {
                if (snap.Day != day) return;
                snap.Past.Clear();
                foreach (var g in byNick)
                {
                    var days = g.Select(r => r.Day).ToHashSet(StringComparer.Ordinal);
                    var nick = g.OrderByDescending(r => r.Day, StringComparer.Ordinal).First().Nick;
                    var d = Shift(day, -1);
                    var n = 0;
                    while (days.Contains(d)) { n++; d = Shift(d, -1); }
                    var last = g.Where(r => r.Day != day).Select(r => r.Day).DefaultIfEmpty("").Max(StringComparer.Ordinal) ?? "";
                    snap.Past[g.Key] = (nick, n, last);
                    foreach (var t in g.Where(r => r.Day == day))
                        if (!snap.Today.TryGetValue(g.Key, out var was) || t.A < was.A || (t.A == was.A && t.Ms < was.Ms))
                            snap.Today[g.Key] = (t.Nick, t.A, t.Ms);
                }
                snap.Loaded = true;
                snap.LoadedAt = clock.UtcNow;
                snap.Card = null;
                snap.Ver++;
            }
        }
        catch (Exception) { /* табло — радість, а не облік: без нього гра однаково йде */ }
        finally { _loading.TryRemove(game, out _); }
    }

    /// <summary>Табло у вид: короткі поля, бо летить з кожним видом картки.</summary>
    public static object? Wire(DayCard? card) => card is null ? null : new
    {
        day = card.Day,
        rows = card.Rows.Select(r => new { n = r.Nick, a = r.Attempts, ms = r.Ms, st = r.Streak }).ToArray(),
        wait = card.Waiting.Select(r => new { n = r.Nick, st = r.Streak }).ToArray(),
    };
}
