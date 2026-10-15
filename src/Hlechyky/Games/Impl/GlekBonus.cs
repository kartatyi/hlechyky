using Hlechyky.Games.Economy;

namespace Hlechyky.Games.Impl;

/// <summary>Доплата Глека до виграшів у слотах і Лелеці (секція <c>Azart</c>).</summary>
public sealed class AzartBonusOptions
{
    /// <summary>Скільки відсотків Глек докидає до кожного виграшу. 0 — доплати нема.</summary>
    public int BonusPct { get; set; } = 6;
    /// <summary>Стеля доплати на людину за київський день. 0 — без стелі.</summary>
    public int BonusDailyCap { get; set; } = 3000;
}

/// <summary>Денні лічильники (у проді — <see cref="EconomyStore"/>, у тестах — у пам'яті).</summary>
public interface IDayCounters
{
    int Bump(string nickKey, string key, string day, int by);
    int Counter(string nickKey, string key, string day);
}

/// <summary>Лічильники економіки: <c>economy_counters</c>.</summary>
public sealed class EconomyDayCounters(EconomyStore store) : IDayCounters
{
    public int Bump(string nickKey, string key, string day, int by) => store.Bump(nickKey, key, day, by);
    public int Counter(string nickKey, string key, string day) => store.Counter(nickKey, key, day);
}

/// <summary>Лічильники в пам'яті (тести, заміри).</summary>
public sealed class MemoryDayCounters : IDayCounters
{
    readonly Dictionary<string, int> _n = new(StringComparer.Ordinal);
    public int Bump(string nickKey, string key, string day, int by)
    {
        lock (_n) return _n[$"{nickKey}|{key}|{day}"] = _n.GetValueOrDefault($"{nickKey}|{key}|{day}") + by;
    }
    public int Counter(string nickKey, string key, string day)
    {
        lock (_n) return _n.GetValueOrDefault($"{nickKey}|{key}|{day}");
    }
}

/// <summary>
/// «Доплата Глека»: +<see cref="AzartBonusOptions.BonusPct"/> % до кожного виграшу, не більше
/// <see cref="AzartBonusOptions.BonusDailyCap"/> на людину за київський день. Облік — у пам'яті під замком (рахується
/// атомарно, без бази в замку кімнати), а в базу (<c>glek-bonus</c> у денних лічильниках) — дописом відкладено; після
/// перезапуску денне видане читається з бази при першому зверненні.
/// </summary>
public sealed class GlekBonus
{
    public const string CounterKey = "glek-bonus";

    readonly IDayCounters _counters;
    readonly Func<AzartBonusOptions> _opts;
    readonly IClock _clock;
    readonly Action<Action> _defer;
    readonly ILogger<GlekBonus>? _log;
    readonly object _gate = new();
    readonly Dictionary<string, int> _used = new(StringComparer.Ordinal);   // nickKey → видано сьогодні
    string _day = "";

    /// <param name="defer">Як дописати лічильник у базу: типово — пул потоків; тести — <c>a =&gt; a()</c>.</param>
    public GlekBonus(IDayCounters counters, Func<AzartBonusOptions> options, IClock? clock = null, Action<Action>? defer = null,
        ILogger<GlekBonus>? log = null)
    {
        _counters = counters;
        _opts = options;
        _clock = clock ?? new SystemClock();
        _defer = defer ?? (a => ThreadPool.QueueUserWorkItem(_ => a()));
        _log = log;
    }

    public AzartBonusOptions O => _opts();
    public int Pct => Math.Max(0, O.BonusPct);
    public int Cap => Math.Max(0, O.BonusDailyCap);

    /// <summary>Скільки б доплатив до <paramref name="win"/> без стелі.</summary>
    public static int Of(int win, int pct) => win <= 0 || pct <= 0 ? 0 : (int)Math.Min(int.MaxValue, Math.Round((double)win * pct / 100, MidpointRounding.AwayFromZero));

    /// <summary>Доплата до виграшу <paramref name="win"/> (уже врахована в денному обліку). 0 — нема чи стелю вичерпано.</summary>
    public int Take(string nick, int win)
    {
        var want = Of(win, Pct);
        if (want <= 0 || string.IsNullOrWhiteSpace(nick)) return 0;
        var cap = Cap;
        var key = EconomyStore.Key(nick);
        int give;
        string day;
        lock (_gate)
        {
            day = Fresh();
            var used = UsedIn(key, day);
            give = cap > 0 ? Math.Max(0, Math.Min(want, cap - used)) : want;
            if (give <= 0) return 0;
            _used[key] = used + give;
        }
        Persist(key, day, give);
        return give;
    }

    /// <summary>Відкотити доплату, коли оберт/ставка зірвались до руху грошей.</summary>
    public void Return(string nick, int amount)
    {
        if (amount <= 0 || string.IsNullOrWhiteSpace(nick)) return;
        var key = EconomyStore.Key(nick);
        string day;
        lock (_gate)
        {
            day = Fresh();
            _used[key] = Math.Max(0, UsedIn(key, day) - amount);
        }
        Persist(key, day, -amount);
    }

    /// <summary>Скільки ще можна доплатити сьогодні (int.MaxValue — без стелі; 0 — доплати нема).</summary>
    public int Left(string nick)
    {
        if (Pct <= 0) return 0;
        var cap = Cap;
        if (cap <= 0) return int.MaxValue;
        var key = EconomyStore.Key(nick);
        lock (_gate) return Math.Max(0, cap - UsedIn(key, Fresh()));
    }

    /// <summary>Для виду: <c>{ pct, cap, left }</c> або null, коли доплати нема.</summary>
    public object? View(string? nick)
    {
        var pct = Pct;
        if (pct <= 0) return null;
        var cap = Cap;
        return new { pct, cap, left = nick is null ? (int?)null : cap > 0 ? Left(nick) : null };
    }

    string Fresh()
    {
        var day = Days.Today(_clock);
        if (day != _day) { _day = day; _used.Clear(); }
        return day;
    }

    int UsedIn(string key, string day)
    {
        if (_used.TryGetValue(key, out var n)) return n;
        try { n = Math.Max(0, _counters.Counter(key, CounterKey, day)); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "доплата Глека: не прочитав видане {Nick} за {Day}", key, day);
            n = 0;
        }
        _used[key] = n;
        return n;
    }

    void Persist(string key, string day, int by) => _defer(() =>
    {
        try { _counters.Bump(key, CounterKey, day, by); }
        catch (Exception ex) { _log?.LogWarning(ex, "доплата Глека: лічильник {Nick} ({By}) не записався", key, by); }
    });
}
