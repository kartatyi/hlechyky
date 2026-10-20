using System.Globalization;
using Hlechyky.Games.Economy;

namespace Hlechyky.Games.Impl;

/// <summary>Рядок заліку тижня: нік і сума місць за 7 днів (менше — краще).</summary>
public sealed record MinesWeekRow(string Nick, int Sum);

/// <summary>
/// «Залік тижня» Сапера дня (велике поле): сума місць за останні 7 днів, сьогодні включно, з <c>daily_results</c>.
/// Місце дня — позиція серед розв'язків (спроби, потім час; однакові — одне місце); не грав чи не розв'язав —
/// «розв'язків того дня + 1». У залік — лише ті, хто грав хоч раз за 7 днів. Кімната бере готове з пам'яті; базу читаємо
/// фоном (раз на 10 хв і з новим днем), а свіжий результат кімната докладає сама (<see cref="Note"/>) — він видно одразу,
/// ще до того, як подія дойде до бази.
/// </summary>
public sealed class MinesWeek(Db? db, IClock clock)
{
    public const string Game = MinesDayGame.BigId;
    public const int Window = 7, MaxRows = 6;
    static readonly TimeSpan Stale = TimeSpan.FromMinutes(10);

    /// <summary>Один результат ніка за день: розв'язав (спроби, мс) чи ні.</summary>
    public readonly record struct Result(string Key, string Nick, string Day, bool Solved, int Attempts, int Ms);

    readonly object _gate = new();
    string _day = "";
    List<Result> _base = [];
    readonly Dictionary<string, Result> _today = new(StringComparer.Ordinal);
    IReadOnlyList<MinesWeekRow>? _board;
    DateTimeOffset _loadedAt;
    bool _loaded;
    int _loading;
    int _gen;

    /// <summary>Залік з пам'яті; null — ще не дочитано з бази (прийде з наступним видом).</summary>
    public IReadOnlyList<MinesWeekRow>? Get()
    {
        var day = Days.Today(clock);
        lock (_gate)
        {
            Roll(day);
            if (db is not null && (!_loaded || clock.UtcNow - _loadedAt > Stale)) Kick(day);
            if (!_loaded && db is not null) return null;
            return _board ??= Rank([.. _base, .. _today.Values], day);
        }
    }

    /// <summary>Кімната щойно записала результат дня (розв'язок чи промах): у пам'ять і перечитати базу фоном.</summary>
    public void Note(string nick, bool solved, int attempts, int ms)
    {
        var day = Days.Today(clock);
        lock (_gate)
        {
            Roll(day);
            var r = new Result(EconomyStore.Key(nick), nick, day, solved, Math.Max(1, attempts), solved ? ms : 0);
            if (!_today.TryGetValue(r.Key, out var was) || Better(r, was)) _today[r.Key] = r;
            _board = null;
            if (db is not null) { _loadedAt = DateTimeOffset.MinValue; Kick(day); }
        }
    }

    /// <summary>Лише для тестів: дочитати базу зараз.</summary>
    public void LoadNow()
    {
        var day = Days.Today(clock);
        int gen;
        lock (_gate) { Roll(day); gen = _gen; }
        Load(day, gen);
    }

    void Roll(string day)
    {
        if (_day == day) return;
        _day = day;
        _today.Clear();
        _base = [];
        _board = null;
        _loaded = false;
        _gen++;
    }

    void Kick(string day)
    {
        if (Interlocked.CompareExchange(ref _loading, 1, 0) != 0) return;
        var gen = _gen;
        _ = Task.Run(() =>
        {
            try { Load(day, gen); }
            finally { Interlocked.Exchange(ref _loading, 0); }
        });
    }

    static string Shift(string day, int by) =>
        DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(by).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    void Load(string day, int gen)
    {
        try
        {
            var rows = new List<Result>();
            db!.With(c =>
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT nick_key, nick, day, solved, attempts, ms FROM daily_results WHERE game = $g AND day >= $s AND day <= $d";
                cmd.Parameters.AddWithValue("$g", Game);
                cmd.Parameters.AddWithValue("$s", Shift(day, -(Window - 1)));
                cmd.Parameters.AddWithValue("$d", day);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    rows.Add(new Result(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3) == 1, r.GetInt32(4), r.GetInt32(5)));
            });
            lock (_gate)
            {
                if (_gen != gen || _day != day) return;
                _base = rows;
                _loaded = true;
                _loadedAt = clock.UtcNow;
                _board = null;
            }
        }
        catch (Exception) { /* залік — радість, а не облік: без нього гра однаково йде */ }
    }

    /// <summary>Краще: розв'язок понад промах; серед розв'язків — менше спроб, потім менше часу; серед промахів — більше спроб.</summary>
    static bool Better(Result a, Result b) =>
        a.Solved != b.Solved ? a.Solved
        : a.Solved ? a.Attempts < b.Attempts || (a.Attempts == b.Attempts && a.Ms < b.Ms)
        : a.Attempts > b.Attempts;

    /// <summary>Залік тижня з усіх результатів вікна (повтори того самого ніка за день зводяться до кращого).</summary>
    public static IReadOnlyList<MinesWeekRow> Rank(IEnumerable<Result> results, string today)
    {
        var since = Shift(today, -(Window - 1));
        var best = new Dictionary<(string Day, string Key), Result>();
        foreach (var r in results)
        {
            if (string.CompareOrdinal(r.Day, since) < 0 || string.CompareOrdinal(r.Day, today) > 0) continue;
            if (!best.TryGetValue((r.Day, r.Key), out var was) || Better(r, was)) best[(r.Day, r.Key)] = r;
        }
        // нік — найсвіжіше написання
        var players = best.Values.GroupBy(r => r.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.Day, StringComparer.Ordinal).First().Nick, StringComparer.Ordinal);
        var sums = players.Keys.ToDictionary(k => k, _ => 0, StringComparer.Ordinal);
        for (var i = 0; i < Window; i++)
        {
            var day = Shift(since, i);
            var solved = best.Values.Where(r => r.Day == day && r.Solved).OrderBy(r => r.Attempts).ThenBy(r => r.Ms).ToList();
            var place = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var j = 0; j < solved.Count; j++)
                place[solved[j].Key] = j > 0 && solved[j].Attempts == solved[j - 1].Attempts && solved[j].Ms == solved[j - 1].Ms
                    ? place[solved[j - 1].Key] : j + 1;
            foreach (var k in players.Keys)
                sums[k] += place.TryGetValue(k, out var p) ? p : solved.Count + 1;
        }
        return [.. sums.Select(kv => new MinesWeekRow(players[kv.Key], kv.Value))
            .OrderBy(r => r.Sum).ThenBy(r => r.Nick, StringComparer.Ordinal).Take(MaxRows)];
    }

    /// <summary>Залік у вид: короткі поля.</summary>
    public static object? Wire(IReadOnlyList<MinesWeekRow>? rows) => rows?.Select(r => new { n = r.Nick, s = r.Sum }).ToArray();
}
