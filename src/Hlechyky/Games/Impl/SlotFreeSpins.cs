using Hlechyky.Games.Economy;

namespace Hlechyky.Games.Impl;

/// <summary>Безкоштовні прокрути в слотах (секція <c>Slots:Free</c>, наживо).</summary>
public sealed class SlotFreeOptions
{
    /// <summary>Скільки безкоштовних прокрутів на людину за київський день (спільні на всі автомати). 0 — нема.</summary>
    public int Spins { get; set; }
    /// <summary>Ставка кожного безкоштовного прокруту.</summary>
    public int Bet { get; set; } = 50;
}

/// <summary>
/// «🎁 Безкоштовні прокрути» (25.10.2026): кожному акаунту (не гостю й не боту) — <see cref="SlotFreeOptions.Spins"/> обертів
/// на день по <see cref="SlotFreeOptions.Bet"/> у будь-якому автоматі; опівночі згоряють. Ставку не списуємо, виграш —
/// звичайним шляхом каси. Скарбничку такий оберт не годує, доплати Глека на нього нема.
/// Облік — як у <see cref="GlekBonus"/>: пам'ять під замком, у базу (<c>slot-free</c> у денних лічильниках) — дописом.
/// </summary>
public sealed class SlotFreeSpins
{
    public const string CounterKey = "slot-free";

    readonly IDayCounters _counters;
    readonly Func<SlotFreeOptions> _opts;
    readonly Func<string, bool> _person;
    readonly IClock _clock;
    readonly Action<Action> _defer;
    readonly ILogger<SlotFreeSpins>? _log;
    readonly object _gate = new();
    readonly Dictionary<string, int> _used = new(StringComparer.Ordinal);   // nickKey → використано сьогодні
    readonly Dictionary<string, bool> _people = new(StringComparer.Ordinal);   // nickKey → людина (до кінця дня)
    string _day = "";

    /// <param name="person">Хто людина (акаунт без ботів).</param>
    /// <param name="defer">Як дописати лічильник у базу: типово — пул потоків; тести — <c>a =&gt; a()</c>.</param>
    public SlotFreeSpins(IDayCounters counters, Func<SlotFreeOptions> options, Func<string, bool> person, IClock? clock = null,
        Action<Action>? defer = null, ILogger<SlotFreeSpins>? log = null)
    {
        _counters = counters;
        _opts = options;
        _person = person;
        _clock = clock ?? new SystemClock();
        _defer = defer ?? (a => ThreadPool.QueueUserWorkItem(_ => a()));
        _log = log;
    }

    public int Total => Math.Max(0, _opts().Spins);
    public int Bet => Math.Max(1, _opts().Bet);

    /// <summary>Людина? «Так» від бази пам'ятаємо до кінця дня — вид питає щоразу.</summary>
    public bool Person(string nick)
    {
        var key = EconomyStore.Key(nick);
        lock (_gate)
        {
            Fresh();
            if (_people.TryGetValue(key, out var known)) return known;
        }
        bool yes;
        try { yes = _person(nick); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "безкоштовні прокрути: не з'ясував, чи {Nick} людина", nick);
            return false;
        }
        if (yes) lock (_gate) _people[key] = true;   // «ні» не пам'ятаємо: гість може завести акаунт посеред дня
        return yes;
    }

    /// <summary>Скільки ще лишилось сьогодні (0 — скінчились, вимкнено чи не людина).</summary>
    public int Left(string nick)
    {
        var total = Total;
        if (total <= 0 || string.IsNullOrWhiteSpace(nick) || !Person(nick)) return 0;
        var key = EconomyStore.Key(nick);
        lock (_gate) return Math.Max(0, total - UsedIn(key, Fresh()));
    }

    /// <summary>Узяти один прокрут. false — нема (скінчились, вимкнено чи не людина).</summary>
    public bool Take(string nick)
    {
        var total = Total;
        if (total <= 0 || string.IsNullOrWhiteSpace(nick) || !Person(nick)) return false;
        var key = EconomyStore.Key(nick);
        string day;
        lock (_gate)
        {
            day = Fresh();
            var used = UsedIn(key, day);
            if (used >= total) return false;
            _used[key] = used + 1;
        }
        Persist(key, day, 1);
        return true;
    }

    /// <summary>Повернути прокрут, коли оберт зірвався до руху грошей.</summary>
    public void Return(string nick)
    {
        if (string.IsNullOrWhiteSpace(nick)) return;
        var key = EconomyStore.Key(nick);
        string day;
        lock (_gate)
        {
            day = Fresh();
            _used[key] = Math.Max(0, UsedIn(key, day) - 1);
        }
        Persist(key, day, -1);
    }

    /// <summary>Для виду: <c>{ bet, total, left, person }</c> або null, коли прокрутів нема.</summary>
    public object? View(string? nick)
    {
        var total = Total;
        if (total <= 0) return null;
        var person = nick is not null && Person(nick);
        return new { bet = Bet, total, left = person ? Left(nick!) : 0, person };
    }

    string Fresh()
    {
        var day = Days.Today(_clock);
        if (day != _day) { _day = day; _used.Clear(); _people.Clear(); }
        return day;
    }

    int UsedIn(string key, string day)
    {
        if (_used.TryGetValue(key, out var n)) return n;
        try { n = Math.Max(0, _counters.Counter(key, CounterKey, day)); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "безкоштовні прокрути: не прочитав використане {Nick} за {Day}", key, day);
            n = 0;
        }
        _used[key] = n;
        return n;
    }

    void Persist(string key, string day, int by) => _defer(() =>
    {
        try { _counters.Bump(key, CounterKey, day, by); }
        catch (Exception ex) { _log?.LogWarning(ex, "безкоштовні прокрути: не записав {Nick} {By}", key, by); }
    });
}
