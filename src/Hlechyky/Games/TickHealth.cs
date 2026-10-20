using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;

namespace Hlechyky.Games;

/// <summary>
/// Замір «дьоргання» (20.10): чи сам сервер тикає рівно. TickEngine на кожному колі каже, наскільки цикл прокинувся
/// пізніше за свої <see cref="TickEngine.StepMs"/> і скільки тривали тики кімнат, прибирання й розсилка; на кожну
/// кімнату — наскільки її тик запізнився проти розкладу. Раз на 10 с, коли бодай одна реалтайм-кімната грала, — рядок
/// підсумку в data/diag/tick-&lt;дата&gt;.jsonl; кола з помітним затиком — окремими рядками «spike» з GC і пулом потоків
/// саме в ту мить. Рахується в потоці циклу без замків; на диск пише фоновий <see cref="DiagLog"/>. Вимикач — Diag:Tick.
/// </summary>
public sealed class TickHealth(DiagLog log, IConfiguration? cfg = null)
{
    public const int WindowMs = 10_000;
    /// <summary>Від скількох мс запізнення кадр бомбера вже стоїть: запас клієнта на тихій мережі — чверть тика (15 мс).</summary>
    public const double SpikeMs = 25;
    /// <summary>Розсилка, прибирання чи тики кімнат за коло довші за це — теж затик, навіть якщо коло встигло.</summary>
    public const double SlowMs = 15;
    const int SpikesPerWindow = 40;
    const int Keep = 90;   // підсумків у пам'яті для /api/diag/tick — 15 хв гри

    sealed class RoomAcc
    {
        public string Game = "";
        public int TickMs, Seats, Watchers, Rebased, Over;
        public double WorkMax;
        public readonly List<double> Late = new(256);
    }

    readonly object _ringLock = new();
    readonly Queue<object> _ring = new();

    long _windowAt = Stopwatch.GetTimestamp();
    readonly List<double> _gaps = new(1100), _ticks = new(1100), _flushes = new(1100);
    double _choresMax;
    int _spikes, _spikesDropped, _pendingMax;
    readonly Dictionary<string, RoomAcc> _rooms = new(StringComparer.Ordinal);
    TimeSpan _gcWindow = GC.GetTotalPauseDuration(), _gcLoop = GC.GetTotalPauseDuration();
    int _g0 = GC.CollectionCount(0), _g1 = GC.CollectionCount(1), _g2 = GC.CollectionCount(2);
    long _alloc = GC.GetTotalAllocatedBytes(false), _fullIdx = GC.GetGCMemoryInfo(GCKind.FullBlocking).Index;
    TimeSpan _cpu = Environment.CpuUsage.TotalTime;
    (long Idle, long Total) _sys = SystemTimes();
    bool _on = On(cfg);

    // найгірша кімната поточного кола
    double _loopLate;
    string? _loopRoom, _loopGame;

    static bool On(IConfiguration? cfg) => cfg?.GetValue("Diag:Tick", true) ?? true;

    /// <summary>Кімната відтикала: на скільки мс пізніше за розклад (<see cref="Room.NextTickAt"/>) і скільки тривав сам тик.</summary>
    public void RoomTick(Room room, double lateMs, double workMs)
    {
        if (!_on) return;
        var seats = 0;
        foreach (var s in room.Seats) if (s is not null) seats++;
        RoomTick(room.Id, room.Info.Id, room.Info.TickMs, seats, room.Watchers.Count, lateMs, workMs);
    }

    public void RoomTick(string id, string game, int tickMs, int seats, int watchers, double lateMs, double workMs)
    {
        if (!_on) return;
        if (!_rooms.TryGetValue(id, out var acc)) _rooms[id] = acc = new RoomAcc();
        acc.Game = game;
        acc.TickMs = tickMs;
        acc.Seats = seats;
        acc.Watchers = watchers;
        acc.Late.Add(lateMs);
        if (tickMs > 0 && lateMs >= tickMs) acc.Rebased++;   // Rooms.Tick не надолужує — розклад зсунувся на цілий тик
        if (lateMs > SpikeMs) acc.Over++;
        if (workMs > acc.WorkMax) acc.WorkMax = workMs;
        if (lateMs > _loopLate) { _loopLate = lateMs; _loopRoom = id; _loopGame = game; }
    }

    /// <summary>
    /// Коло циклу скінчилось. gap — від минулого пробудження до цього (норма — <see cref="TickEngine.StepMs"/>), далі —
    /// скільки тривали тики кімнат разом, прибирання (раз на секунду) і розсилка.
    /// </summary>
    public void Loop(double gapMs, double tickMs, double choresMs, double flushMs, DateTimeOffset now)
    {
        var pause = GC.GetTotalPauseDuration();
        var gcMs = (pause - _gcLoop).TotalMilliseconds;
        _gcLoop = pause;
        if (_on)
        {
            _gaps.Add(gapMs);
            _ticks.Add(tickMs);
            _flushes.Add(flushMs);
            if (choresMs > _choresMax) _choresMax = choresMs;
            var late = gapMs - TickEngine.StepMs;
            if (late > SpikeMs || _loopLate > SpikeMs || tickMs > SlowMs || flushMs > SlowMs || choresMs > SlowMs)
                Spike(now, late, tickMs, choresMs, flushMs, gcMs);
        }
        _loopLate = 0;
        _loopRoom = _loopGame = null;
        if (Stopwatch.GetElapsedTime(_windowAt).TotalMilliseconds >= WindowMs) CloseWindow(now);
    }

    void Spike(DateTimeOffset now, double late, double tickMs, double choresMs, double flushMs, double gcMs)
    {
        var pending = (int)Math.Min(int.MaxValue, ThreadPool.PendingWorkItemCount);
        if (pending > _pendingMax) _pendingMax = pending;
        if (_spikes >= SpikesPerWindow) { _spikesDropped++; return; }
        _spikes++;
        Write(new
        {
            k = "spike", at = now.ToLocalTime().ToString("O"),
            late = R(late), tick = R(tickMs), chores = R(choresMs), flush = R(flushMs),
            room = _loopRoom, game = _loopGame, roomLate = R(_loopLate),
            gc = R(gcMs), gen = gcMs > 0 ? GC.GetGCMemoryInfo(GCKind.Any).Generation : (int?)null, g2 = GC.CollectionCount(2) - _g2,
            tp = new { threads = ThreadPool.ThreadCount, pending },
        });
    }

    /// <summary>Закрити вікно зараз: підсумок (якщо було що підсумовувати) — у файл і в пам'ять, лічильники — з нуля.</summary>
    public void CloseWindow(DateTimeOffset now)
    {
        var secs = Stopwatch.GetElapsedTime(_windowAt).TotalSeconds;
        _windowAt = Stopwatch.GetTimestamp();
        var pause = GC.GetTotalPauseDuration();
        var cpu = Environment.CpuUsage.TotalTime;
        var sys = SystemTimes();
        var alloc = GC.GetTotalAllocatedBytes(false);
        var full = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        if (_on && (_rooms.Count > 0 || _spikes > 0))
        {
            var mem = GC.GetGCMemoryInfo();
            double Pc(long x) => mem.TotalAvailableMemoryBytes > 0 ? R(100.0 * x / mem.TotalAvailableMemoryBytes) : 0;
            var sysTotal = sys.Total - _sys.Total;
            var summary = new
            {
                k = "win", at = now.ToLocalTime().ToString("O"), sec = R(secs),
                loops = _gaps.Count,
                gap = Pct(_gaps),
                over25 = _gaps.Count(g => g - TickEngine.StepMs > 25),
                over50 = _gaps.Count(g => g - TickEngine.StepMs > 50),
                over100 = _gaps.Count(g => g - TickEngine.StepMs > 100),
                tick = Pct(_ticks), flush = Pct(_flushes), chores = R(_choresMax),
                rooms = _rooms.Select(kv => new
                {
                    id = kv.Key, game = kv.Value.Game, tickMs = kv.Value.TickMs, ticks = kv.Value.Late.Count,
                    seats = kv.Value.Seats, watchers = kv.Value.Watchers, late = Pct(kv.Value.Late),
                    over25 = kv.Value.Over, rebased = kv.Value.Rebased, work = R(kv.Value.WorkMax),
                }).ToList(),
                gc = new
                {
                    pause = R((pause - _gcWindow).TotalMilliseconds),
                    g0 = GC.CollectionCount(0) - _g0, g1 = GC.CollectionCount(1) - _g1, g2 = GC.CollectionCount(2) - _g2,
                    heapMb = R(GC.GetTotalMemory(false) / 1048576.0),
                    allocMb = R((alloc - _alloc) / 1048576.0),
                    // повне блокуюче збирання за вікно (його пауза — на весь процес) і зайнятість пам'яті машини: від
                    // highLoad % GC збирає gen2 значно частіше
                    fullPause = full.Index != _fullIdx && full.PauseDurations.Length > 0 ? R(full.PauseDurations[0].TotalMilliseconds) : (double?)null,
                    memLoad = Pc(mem.MemoryLoadBytes), highLoad = Pc(mem.HighMemoryLoadThresholdBytes),
                },
                // з якою точністю зараз цокає системний таймер (FineTimer просить 1 мс; Windows 11 може не дати)
                timerMs = TimerMs(),
                tp = new { threads = ThreadPool.ThreadCount, pending = ThreadPool.PendingWorkItemCount, pendingMax = _pendingMax },
                // проц — частка всіх ядер машини, що з'їв сервер; машина — уся зайнятість (ігри, збірки, сесії поруч)
                cpu = new
                {
                    proc = R((cpu - _cpu).TotalMilliseconds / (secs * 1000 * Environment.ProcessorCount) * 100),
                    machine = sysTotal > 0 ? R((1 - (double)(sys.Idle - _sys.Idle) / sysTotal) * 100) : (double?)null,
                },
                spikes = _spikes, spikesDropped = _spikesDropped,
            };
            Write(summary);
            lock (_ringLock)
            {
                _ring.Enqueue(summary);
                while (_ring.Count > Keep) _ring.Dequeue();
            }
        }
        _gaps.Clear();
        _ticks.Clear();
        _flushes.Clear();
        _rooms.Clear();
        _choresMax = 0;
        _spikes = _spikesDropped = _pendingMax = 0;
        _gcWindow = pause;
        _g0 = GC.CollectionCount(0);
        _g1 = GC.CollectionCount(1);
        _g2 = GC.CollectionCount(2);
        _cpu = cpu;
        _sys = sys;
        _alloc = alloc;
        _fullIdx = full.Index;
        _on = On(cfg);
    }

    void Write(object entry) => log.Write("tick", entry);

    /// <summary>Останні підсумки вікон (новіші — в кінці).</summary>
    public List<object> Recent()
    {
        lock (_ringLock) return [.. _ring];
    }

    static object? Pct(List<double> xs)
    {
        if (xs.Count == 0) return null;
        var s = xs.ToArray();
        Array.Sort(s);
        double At(double q) => s[Math.Min(s.Length - 1, (int)(s.Length * q))];
        return new { p50 = R(At(0.5)), p95 = R(At(0.95)), p99 = R(At(0.99)), max = R(s[^1]) };
    }

    static double R(double x) => Math.Round(x, 1);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("ntdll.dll")]
    static extern int NtQueryTimerResolution(out uint min, out uint max, out uint current);

    /// <summary>Поточний крок системного таймера в мс (15,6 — типовий, 1 — хтось попросив точний); null — не Windows.</summary>
    static double? TimerMs()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return NtQueryTimerResolution(out _, out _, out var cur) == 0 ? Math.Round(cur / 10_000.0, 2) : null; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    /// <summary>Скільки машина простояла і скільки пройшло (у сотнях нс, сумарно по ядрах); kernel уже містить idle.</summary>
    static (long Idle, long Total) SystemTimes()
    {
        if (!OperatingSystem.IsWindows()) return (0, 0);
        try { return GetSystemTimes(out var idle, out var kernel, out var user) ? (idle, kernel + user) : (0, 0); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return (0, 0); }
    }

    /// <summary>
    /// /api/diag/jerk — метр «дьоргання» з браузера (web/games/core.js, HGames.ui.jerkMeter): раз на кілька секунд гри
    /// підсумок того, як приходили кадри й як малювалось. Лягає в data/diag/jerk-&lt;дата&gt;.jsonl поруч із підсумками тика,
    /// щоб уранці звести: стояв сервер, гикала мережа чи браузер. Вимикач — Diag:Jerk. /api/diag/tick — останні
    /// підсумки тика наживо, лише розробнику.
    /// </summary>
    public static void Map(WebApplication app)
    {
        var inbox = new JerkInbox(app.Services.GetRequiredService<DiagLog>());
        app.MapPost("/api/diag/jerk", async (HttpContext c, IConfiguration config) =>
        {
            if (!config.GetValue("Diag:Jerk", true)) return Results.Ok(new { ok = false, off = true });
            var code = await inbox.Accept(Auth.Nick(c), c.Connection.RemoteIpAddress?.ToString(), c.Request.ContentLength,
                c.Request.Body, Environment.TickCount64, c.RequestAborted);
            return code == 200 ? Results.Ok(new { ok = true }) : Results.StatusCode(code);
        });
        app.MapGet("/api/diag/tick", (HttpContext c, TickHealth health) =>
            Auth.IsAdmin(c) ? Results.Ok(health.Recent()) : Results.NotFound());
    }
}

/// <summary>Прийом підсумків метра «дьоргання»: не більше 16 КБ, з одного ніка й адреси — не частіше ніж раз на 1,5 с.</summary>
public sealed class JerkInbox(DiagLog diag)
{
    public const int MaxBytes = 16 * 1024;
    public const int MinGapMs = 1500;

    readonly ConcurrentDictionary<string, long> _last = new(StringComparer.Ordinal);

    /// <summary>HTTP-код відповіді: 200 — лягло в data/diag/jerk-&lt;дата&gt;.jsonl; 413, 429, 400 — ні.</summary>
    public async Task<int> Accept(string nick, string? ip, long? length, Stream body, long nowMs, CancellationToken ct = default)
    {
        if (length is not { } len || len > MaxBytes) return 413;
        var key = nick + "|" + ip;
        // з однієї вкладки — раз на ~5 с; дві вкладки однієї людини теж пройдуть, а засипати диск — ні
        if (_last.TryGetValue(key, out var was) && nowMs - was < MinGapMs) return 429;
        _last[key] = nowMs;
        if (_last.Count > 500)
            foreach (var kv in _last)
                if (nowMs - kv.Value > 60_000) _last.TryRemove(kv.Key, out _);
        JsonDocument doc;
        try { doc = await JsonDocument.ParseAsync(body, cancellationToken: ct); }
        catch (JsonException) { return 400; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return 400;
            diag.Write("jerk", new { k = "jerk", at = DateTimeOffset.Now.ToString("O"), nick, m = doc.RootElement.Clone() });
        }
        return 200;
    }
}

/// <summary>
/// Рядки замірів у data/diag/&lt;вид&gt;-&lt;дата&gt;.jsonl. Пише фоновий потік: цикл тика й обробники запитів лише кладуть рядок
/// у чергу (повна — рядок пропадає, а не гальмує гру).
/// </summary>
public sealed class DiagLog
{
    static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly string _dir;
    readonly Channel<(string Kind, string Line)> _queue =
        Channel.CreateBounded<(string, string)>(new BoundedChannelOptions(4000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public DiagLog(string dir, ILogger<DiagLog>? log = null)
    {
        _dir = dir;
        _ = Task.Run(async () =>
        {
            await foreach (var (kind, line) in _queue.Reader.ReadAllAsync())
            {
                try
                {
                    Directory.CreateDirectory(_dir);
                    await File.AppendAllTextAsync(PathOf(kind, DateTime.Now), line + "\n");
                }
                catch (Exception ex) { log?.LogWarning(ex, "замір {Kind} не записався", kind); }
            }
        });
    }

    public string PathOf(string kind, DateTime day) => Path.Combine(_dir, $"{kind}-{day:yyyy-MM-dd}.jsonl");

    public void Write(string kind, object entry) => _queue.Writer.TryWrite((kind, JsonSerializer.Serialize(entry, Json)));

    /// <summary>Для тестів: дочекатись, поки черга спорожніє.</summary>
    public async Task Drain(TimeSpan max)
    {
        var until = DateTime.UtcNow + max;
        while (_queue.Reader.Count > 0 && DateTime.UtcNow < until) await Task.Delay(10);
        await Task.Delay(30);
    }
}
