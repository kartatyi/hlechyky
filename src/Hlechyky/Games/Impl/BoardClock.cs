namespace Hlechyky.Games.Impl;

/// <summary>
/// Шаховий годинник для настільних ігор на двох (шахи, шашки) — необов'язковий, обирають при створенні столу.
/// <para>
/// Тика в цих ігор нема і не буде: з тиком каркас перестав би рахувати ходи й слати види після кожного Act
/// (Rooms.Act), а на ходах тримається нагорода за партію. Тому годинник пасивний: сервер пам'ятає, скільки
/// в кого лишилось і відколи йде час того, хто думає, а «прапорець» перевіряє на кожному Act. Браузер сам
/// відлічує секунди й, коли в когось упав прапорець, шле <c>flag</c> — сервер звіряється зі своїм годинником.
/// </para>
/// Годинник рушає після першого ходу білих: хто сів першим, не мусить поспішати, поки суперник ще шукає стілець.
/// </summary>
public sealed class BoardClock
{
    public static readonly GameOption Option = new("clock", "Годинник",
        [("none", "Без годинника"), ("3", "3 хв + 2 с на хід"), ("5", "5 хв + 3 с на хід"), ("10", "10 хв + 5 с на хід")], "none");

    long _baseMs, _incMs;
    readonly long[] _left = new long[2];
    /// <summary>Чий час іде; null — годинник стоїть (до першого ходу або після кінця партії).</summary>
    int? _running;
    DateTimeOffset _since;

    public bool On => _baseMs > 0;

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        (_baseMs, _incMs) = (options.TryGetValue("clock", out var v) ? v : "none") switch
        {
            "3" => (3 * 60_000L, 2_000L),
            "5" => (5 * 60_000L, 3_000L),
            "10" => (10 * 60_000L, 5_000L),
            _ => (0L, 0L),
        };
        Reset();
    }

    public void Reset()
    {
        _left[0] = _left[1] = _baseMs;
        _running = null;
    }

    /// <summary>Скільки лишилось місцю на момент now (з урахуванням того, що його час, можливо, зараз іде).</summary>
    public long Left(int seat, DateTimeOffset now) =>
        _running == seat ? Math.Max(0, _left[seat] - (long)(now - _since).TotalMilliseconds) : _left[seat];

    /// <summary>Місце, у якого впав прапорець, або null.</summary>
    public int? Flagged(DateTimeOffset now) => On && _running is { } s && Left(s, now) <= 0 ? s : null;

    /// <summary>Місце seat щойно походило: списати його час, додати надбавку й пустити годинник суперника.</summary>
    public void Moved(int seat, DateTimeOffset now)
    {
        if (!On) return;
        if (_running == seat) _left[seat] = Left(seat, now) + _incMs;
        _running = seat == 0 ? 1 : 0;
        _since = now;
    }

    /// <summary>Партія скінчилась: зупинити стрілки там, де вони є.</summary>
    public void Stop(DateTimeOffset now)
    {
        if (_running is not { } s) return;
        _left[s] = Left(s, now);
        _running = null;
    }

    /// <summary>Для виду: мілісекунди на момент відправки (PROTOCOL §4 — msLeft, не години сервера). null — годинника нема.</summary>
    public object? View(DateTimeOffset now) => On
        ? new { ms = new[] { Left(0, now), Left(1, now) }, running = _running, inc = _incMs, total = _baseMs }
        : null;

    public sealed record Snapshot(long Base, long Inc, long Left0, long Left1, int? Running, DateTimeOffset Since);

    public Snapshot Save() => new(_baseMs, _incMs, _left[0], _left[1], _running, _since);

    public void Load(Snapshot? s)
    {
        if (s is null) return;
        (_baseMs, _incMs, _left[0], _left[1], _running, _since) = (s.Base, s.Inc, s.Left0, s.Left1, s.Running, s.Since);
    }
}
