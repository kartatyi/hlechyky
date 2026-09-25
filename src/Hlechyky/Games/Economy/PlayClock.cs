namespace Hlechyky.Games.Economy;

/// <summary>
/// Хто де скільки часу провів. Кожна вкладка має кілька незалежних каналів: <see cref="Where"/> — де саме людина
/// зараз (гра, глядач, лобі, решта сайту), <see cref="Site"/> — чи вона взагалі на сайті, <see cref="Listen"/> —
/// чи грає плеєр радіо. «Де» й «на сайті» вкладка каже хабу сама (<c>Here</c>): лише поки вона видима й людина
/// щось робить; межу бездіяльності тримає клієнт, гру й місце перевіряє сервер. Дві вкладки в тому самому місці —
/// один час, а не подвійний. Секунди копляться в пам'яті й раз на хвилину лягають у economy_counters
/// (<see cref="EconomyStore.TimePrefix"/>, по київських днях), тож падіння сервера губить щонайбільше хвилину.
/// </summary>
public sealed class PlayClock(EconomyStore store, IClock clock, ILogger<PlayClock> log) : BackgroundService
{
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(1);

    /// <summary>Канал «де людина»: місце <c>game:&lt;гра&gt;</c>, <c>watch:&lt;гра&gt;</c>, <see cref="Lobby"/> чи <see cref="Page"/>.</summary>
    public const string Where = "where";
    /// <summary>Канал і водночас місце «на сайті взагалі» — вкладка видима, людина тут.</summary>
    public const string Site = "site";
    /// <summary>Канал і водночас місце «слухав радіо» — плеєр на сторінці грає.</summary>
    public const string Listen = "listen";
    /// <summary>Місце: розділ «Ігри», але не за столом (лобі, профіль, таблиці, щоденне).</summary>
    public const string Lobby = "lobby";
    /// <summary>Місце: решта сайту (радіо, балачки, бібліотека).</summary>
    public const string Page = "page";

    public static string Game(string gameId) => "game:" + gameId;
    public static string Watch(string gameId) => "watch:" + gameId;

    /// <summary>Більший хвіст бездіяльності не відрізаємо: клієнт стільки й не пришле, а дивне число не з'їсть годину.</summary>
    public const int MaxIdleMs = 15 * 60 * 1000;

    /// <summary>Відрізок одного ніка в одному місці: хто з вкладок у ньому, звідки ще не зараховано, звідки почався.</summary>
    sealed class Run
    {
        public readonly HashSet<(string Conn, string Channel)> Conns = [];
        public DateTimeOffset From;
        public DateTimeOffset Start;
    }

    readonly object _lock = new();
    readonly Dictionary<(string Conn, string Channel), (string Key, string Place)> _at = [];
    readonly Dictionary<(string Key, string Place), Run> _runs = [];
    /// <summary>Ще не записані секунди; дробові хвости чекають наступного запису.</summary>
    readonly Dictionary<(string Key, string Place, string Day), double> _owed = [];

    /// <summary>
    /// Канал <paramref name="channel"/> вкладки <paramref name="connId"/> тепер у місці <paramref name="place"/>
    /// (null — ніде). <paramref name="idleMs"/> — скільки людина вже нічого не чіпала, коли вкладка вимкнулась
    /// через бездіяльність: цей хвіст не рахуємо.
    /// </summary>
    public void Set(string connId, string nick, string channel, string? place, int idleMs = 0)
    {
        var key = Economy.Key(nick);
        (string Key, string Place)? target = key.Length > 0 && !string.IsNullOrEmpty(place) ? (key, place) : null;
        var slot = (connId, channel);
        var now = clock.UtcNow;
        lock (_lock)
        {
            if (_at.TryGetValue(slot, out var cur))
            {
                if (target == cur) return;
                Leave(slot, cur, now, idleMs);
            }
            if (target is not { } at) return;
            _at[slot] = at;
            if (!_runs.TryGetValue(at, out var run)) _runs[at] = run = new Run { From = now, Start = now };
            run.Conns.Add(slot);
        }
    }

    /// <summary>З'єднання зникло: закрили вкладку чи обірвався зв'язок — усі його канали гаснуть.</summary>
    public void Drop(string connId)
    {
        var now = clock.UtcNow;
        lock (_lock)
            foreach (var (slot, at) in _at.Where(x => x.Key.Conn == connId).ToList())
                Leave(slot, at, now, 0);
    }

    void Leave((string Conn, string Channel) slot, (string Key, string Place) at, DateTimeOffset now, int idleMs)
    {
        _at.Remove(slot);
        if (!_runs.TryGetValue(at, out var run)) return;
        run.Conns.Remove(slot);
        if (run.Conns.Count > 0) return;   // інша вкладка ще тут — бездіяльність цієї нічого не означає
        _runs.Remove(at);
        var end = now - TimeSpan.FromMilliseconds(Math.Clamp(idleMs, 0, MaxIdleMs));
        if (end < run.Start) end = run.Start;
        // Може вийти й від'ємне: хвилинний запис уже зарахував частину хвоста — тоді віднімаємо її назад.
        Owe(at, (end - run.From).TotalSeconds, now);
    }

    void Owe((string Key, string Place) at, double seconds, DateTimeOffset now)
    {
        var k = (at.Key, at.Place, Days.Of(now));
        _owed[k] = _owed.GetValueOrDefault(k) + seconds;
    }

    /// <summary>Зарахувати все, що набігло, і записати в базу. Публічний — тестам, сторінці «Час» і зупинці сервера.</summary>
    public void Flush()
    {
        var now = clock.UtcNow;
        var today = Days.Of(now);
        var rows = new List<TimeRow>();
        lock (_lock)
        {
            foreach (var (at, run) in _runs)
            {
                Owe(at, (now - run.From).TotalSeconds, now);
                run.From = now;
            }
            foreach (var (k, seconds) in _owed.ToList())
            {
                var whole = (int)Math.Truncate(seconds);
                var rest = seconds - whole;
                if (whole != 0) rows.Add(new TimeRow(k.Key, k.Place, k.Day, whole));
                // дріб учорашнього дня вже нікуди не доросте — викидаємо, щоб словник не пух
                if (rest == 0 || k.Day != today) _owed.Remove(k);
                else _owed[k] = rest;
            }
        }
        try { store.AddTime(rows); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "час на сайті не записався — спробуємо з наступною хвилиною");
            lock (_lock)
                foreach (var r in rows)
                {
                    var k = (r.NickKey, r.Place, r.Day);
                    _owed[k] = _owed.GetValueOrDefault(k) + r.Seconds;
                }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Period);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { Flush(); }
                catch (Exception ex) { log.LogWarning(ex, "хвилинний запис часу спіткнувся"); }
            }
        }
        catch (OperationCanceledException) { /* зупинка сервера */ }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        try { Flush(); }   // на зупинці (деплой, рестарт) не губимо навіть останньої хвилини
        catch (Exception ex) { log.LogWarning(ex, "час на зупинці не записався"); }
    }
}
