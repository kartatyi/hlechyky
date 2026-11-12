using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Microsoft.AspNetCore.SignalR;

namespace Hlechyky;

/// <summary>
/// Що зараз має показувати екран: колір — коди каналів <see cref="R"/>, <see cref="G"/>, <see cref="B"/> по 0–1023,
/// яскравість 0–100, калібровка. Пресет — лише спосіб поставити готові R/G/B, у стані його нема. <see cref="Rev"/>
/// росте з кожною зміною: так екран бачить, що щось змінилось, і не перемальовує те саме.
/// </summary>
public sealed record ScreenState(long Rev, int Brightness, int R, int G, int B, bool Calibration, DateTimeOffset? UpdatedAt, string? By,
    ScreenMeasureAsk? Measure = null);

/// <summary>Що міняє адмін; чого нема (null) — лишається як було. <see cref="Preset"/> ставить його R/G/B, канал, переданий разом із ним, важливіший.</summary>
public sealed record ScreenPatch(int? Brightness = null, int? R = null, int? G = null, int? B = null, string? Preset = null, bool? Calibration = null);

/// <summary>Пресет кольору: ім'я на сторінці й коди R/G/B, які він ставить.</summary>
public sealed record ScreenPreset(string Id, string Name, int R, int G, int B);

/// <summary>
/// Адмін натиснув «🔬 Виміряти колір»: програма має виміряти й відповісти з цим <see cref="Id"/>. Висить у стані, доки
/// не відповість (без строку: на малій яскравості вимір іде довго) або доки адмін не скасує. Решта — що екран
/// показував у мить запиту: вимір належить саме до цього.
/// </summary>
public sealed record ScreenMeasureAsk(long Id, DateTimeOffset At, string? By, int Brightness = 0, int R = 0, int G = 0, int B = 0, bool Calibration = false);

/// <summary>
/// Вимір кольору від програми: координати xy (CIE 1931) і корельована колірна температура в кельвінах; що екран
/// показував (на мить запиту) і <see cref="Changed"/> — чи міняли налаштування, поки міряло.
/// </summary>
public sealed record ScreenMeasure(long Id, double X, double Y, double K, DateTimeOffset At, int Brightness, int R, int G, int B, bool Calibration,
    DateTimeOffset? AskedAt = null, bool Changed = false);

/// <summary>Що пише програма екрана: температуру (°C) і/або вимір кольору. Чого нема — не чіпаємо.</summary>
public sealed record ScreenReport(double? Temperature = null, ScreenMeasureReport? Measure = null);

/// <summary>Вимір у звіті. <see cref="Id"/> — з запиту (<c>measure.id</c> у стані); нема — відповідь на поточний запит або вимір «сам».</summary>
public sealed record ScreenMeasureReport(long? Id = null, double? X = null, double? Y = null, double? K = null);

/// <summary>
/// 🖥 Віддалений екран (вкладка «Екран» у Бібліотеці, docs/screen.md). Адмін міняє яскравість, колір (R/G/B), пресет і
/// калібровку з будь-якого пристрою — екран (окрема програма) тримає з'єднання з сайтом і дізнається про зміну
/// одразу: WebSocket <c>/api/screen/ws</c> або довге опитування <c>/api/screen/device?since=</c>.
/// Програма входить ключем пристрою (не акаунтом): ключ бачить і міняє лише адмін. Стан і ключ — у game_state
/// (<c>screen</c>), тож переживають перезапуск сервера.
/// </summary>
public sealed class Screen
{
    public const string StoreKey = "screen";
    public const int MaxBrightness = 100;
    public const int MaxColor = 1023;

    /// <summary>Пресети кольору — у такому порядку на сторінці. Новий — рядком сюди, напр. <c>new("magenta", "Маджента", 1023, 0, 1023)</c>.</summary>
    public static readonly IReadOnlyList<ScreenPreset> Presets =
    [
        new("red", "Червоний", MaxColor, 0, 0),
        new("green", "Зелений", 0, MaxColor, 0),
        new("blue", "Синій", 0, 0, MaxColor),
        new("white", "Білий", MaxColor, MaxColor, MaxColor),
        new("black", "Чорний", 0, 0, 0),
    ];

    public static ScreenPreset? FindPreset(string? id) =>
        id is null ? null : Presets.FirstOrDefault(p => string.Equals(p.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
    /// <summary>Скільки після останнього запиту екран ще вважаємо «на зв'язку» (довге опитування — раз на ≤ 55 с).</summary>
    public static readonly TimeSpan SeenFresh = TimeSpan.FromSeconds(70);
    /// <summary>Скільки останніх вимірів кольору пам'ятаємо (новіші — першими).</summary>
    public const int MaxMeasures = 20;
    /// <summary>Та сама температура частіше за це адмінам не розсилається (змінилась — розсилаємо одразу).</summary>
    static readonly TimeSpan TempEcho = TimeSpan.FromSeconds(15);

    readonly IGameStore _store;
    readonly IClock _clock;
    readonly ILogger<Screen> _log;
    readonly object _gate = new();
    ScreenState _state;
    string _key;
    TaskCompletionSource _changed = NewSignal();
    int _streams;              // відкриті WebSocket-и й довгі опитування, що зараз чекають
    DateTimeOffset? _seenAt;   // останній запит екрана будь-яким способом
    long _measureSeq;          // номер останнього запиту виміру
    readonly List<ScreenMeasure> _measures = [];
    (double Value, DateTimeOffset At)? _temp;   // остання температура від програми — лише в пам'яті
    DateTimeOffset _tempEchoAt;

    /// <summary>Комусь із адмінів сказати «екран змінився» (у проді — подія хаба <c>screen</c>).</summary>
    public event Action<object>? AdminChanged;

    public Screen(IGameStore store, IClock clock, ILogger<Screen> log)
    {
        _store = store;
        _clock = clock;
        _log = log;
        Saved? saved = null;
        try
        {
            var json = store.LoadState(StoreKey);
            if (json is not null) saved = JsonSerializer.Deserialize<Saved>(Migrate(json));
        }
        catch (Exception ex) { log.LogWarning(ex, "стан екрана не прочитався — починаю з типового"); }
        _state = saved?.State ?? new ScreenState(0, 100, MaxColor, MaxColor, MaxColor, false, null, null);
        _key = string.IsNullOrEmpty(saved?.Key) ? NewKey() : saved!.Key!;
        _measureSeq = Math.Max(saved?.MeasureSeq ?? 0, _state.Measure?.Id ?? 0);
        if (saved?.Measures is { } ms) _measures.AddRange(ms.Take(MaxMeasures));
        if (saved?.Key != _key) Save();
    }

    sealed record Saved(ScreenState? State, string? Key, long MeasureSeq = 0, List<ScreenMeasure>? Measures = null);

    /// <summary>
    /// До 12.11 колір був одним числом <c>Color</c> плюс <c>Preset</c>. Тепер — R/G/B: пресет перекладаємо в його коди,
    /// число без пресету (значення в нього не було) — білим. Стосується стану, запиту виміру й вимірів.
    /// </summary>
    internal static string Migrate(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject root) return json;
        static void Fix(JsonNode? n)
        {
            if (n is not JsonObject o || o.ContainsKey("R")) return;
            var p = FindPreset(o["Preset"]?.GetValue<string>());
            o["R"] = p?.R ?? MaxColor;
            o["G"] = p?.G ?? MaxColor;
            o["B"] = p?.B ?? MaxColor;
            o.Remove("Color");
            o.Remove("Preset");
        }
        Fix(root["State"]);
        Fix(root["State"]?["Measure"]);
        if (root["Measures"] is JsonArray list) foreach (var m in list) Fix(m);
        return root.ToJsonString();
    }

    static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public ScreenState State { get { lock (_gate) return _state; } }
    public string Key { get { lock (_gate) return _key; } }

    /// <summary>Чи підходить ключ екрана (порівнюємо за сталий час).</summary>
    public bool KeyOk(string? given)
    {
        var want = Encoding.UTF8.GetBytes(Key);
        return !string.IsNullOrEmpty(given) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), want);
    }

    /// <summary>Застосувати зміну адміна (пресет = його R/G/B, канал із того ж запиту важливіший). Помилка — текст, інакше null.</summary>
    public string? Apply(ScreenPatch p, string by)
    {
        var preset = FindPreset(p.Preset);
        if (p.Preset is not null && preset is null) return "Нема такого пресету: " + p.Preset;
        if (p.Brightness is null && p.R is null && p.G is null && p.B is null && preset is null && p.Calibration is null) return "Нічого не змінено";
        static int Ch(int? given, int? fromPreset, int was) => given is { } v ? Math.Clamp(v, 0, MaxColor) : fromPreset ?? was;
        TaskCompletionSource old;
        ScreenState now;
        lock (_gate)
        {
            var s = _state;
            now = s with
            {
                Rev = s.Rev + 1,
                Brightness = p.Brightness is { } b ? Math.Clamp(b, 0, MaxBrightness) : s.Brightness,
                R = Ch(p.R, preset?.R, s.R),
                G = Ch(p.G, preset?.G, s.G),
                B = Ch(p.B, preset?.B, s.B),
                Calibration = p.Calibration ?? s.Calibration,
                UpdatedAt = _clock.UtcNow,
                By = by,
            };
            _state = now;
            old = _changed;
            _changed = NewSignal();
        }
        old.TrySetResult();
        Save();
        AdminChanged?.Invoke(AdminView(withKey: false));
        return null;
    }

    /// <summary>
    /// «🔬 Виміряти колір»: новий запит із наступним номером іде програмі в стані (<c>measure</c>), попередній
    /// невиконаний ним замінюється. Повертає номер запиту.
    /// </summary>
    public long AskMeasure(string by)
    {
        TaskCompletionSource old;
        long id;
        lock (_gate)
        {
            id = ++_measureSeq;
            var s = _state;
            _state = s with { Rev = s.Rev + 1, Measure = new ScreenMeasureAsk(id, _clock.UtcNow, by, s.Brightness, s.R, s.G, s.B, s.Calibration) };
            old = _changed;
            _changed = NewSignal();
        }
        old.TrySetResult();
        Save();
        AdminChanged?.Invoke(AdminView(withKey: false));
        return id;
    }

    /// <summary>Скасувати запит виміру: екран отримає <c>measure: null</c> (може перервати). Не було запиту — false.</summary>
    public bool CancelMeasure()
    {
        TaskCompletionSource old;
        lock (_gate)
        {
            if (_state.Measure is null) return false;
            _state = _state with { Rev = _state.Rev + 1, Measure = null };
            old = _changed;
            _changed = NewSignal();
        }
        old.TrySetResult();
        Save();
        AdminChanged?.Invoke(AdminView(withKey: false));
        return true;
    }

    /// <summary>
    /// Звіт програми: температура (°C, лише в пам'яті) і/або вимір кольору. Вимір із номером поточного запиту знімає
    /// запит зі стану (екран дізнається, що відповідь дійшла). Помилка — текст, інакше null.
    /// </summary>
    public string? Report(ScreenReport r)
    {
        if (r.Temperature is null && r.Measure is null) return "Порожній звіт: чекаю temperature і/або measure";
        if (r.Temperature is { } t && (!double.IsFinite(t) || t < -100 || t > 300)) return "temperature — градуси Цельсія, від −100 до 300";
        double x = 0, y = 0, k = 0;
        if (r.Measure is { } m)
        {
            if (m.X is not { } mx || m.Y is not { } my || m.K is not { } mk) return "measure: потрібні x, y і k";
            if (!double.IsFinite(mx) || !double.IsFinite(my) || mx < 0 || mx > 1 || my < 0 || my > 1) return "measure: x і y — від 0 до 1";
            if (!double.IsFinite(mk) || mk <= 0 || mk > 1_000_000) return "measure: k — кельвіни, більше 0";
            (x, y, k) = (mx, my, mk);
        }
        var tell = false;
        var save = false;
        TaskCompletionSource? old = null;
        lock (_gate)
        {
            var now = _clock.UtcNow;
            if (r.Measure is { } rm)
            {
                var pending = _state.Measure;
                var id = rm.Id ?? pending?.Id ?? 0;
                if (id < 0 || id > _measureSeq) return "Нема такого запиту виміру: " + id;
                var s = _state;
                // Відповідь на поточний запит — «що показував» беремо з мити запиту і дивимось, чи щось міняли, поки міряло.
                // Запізніла (запит уже замінили чи скасували) чи «сама» — те, що зараз.
                var ask = pending is not null && pending.Id == id ? pending : null;
                _measures.Insert(0, ask is null
                    ? new ScreenMeasure(id, x, y, k, now, s.Brightness, s.R, s.G, s.B, s.Calibration)
                    : new ScreenMeasure(id, x, y, k, now, ask.Brightness, ask.R, ask.G, ask.B, ask.Calibration, ask.At,
                        Changed: ask.Brightness != s.Brightness || ask.R != s.R || ask.G != s.G || ask.B != s.B || ask.Calibration != s.Calibration));
                if (_measures.Count > MaxMeasures) _measures.RemoveRange(MaxMeasures, _measures.Count - MaxMeasures);
                if (pending is not null && pending.Id == id)
                {
                    _state = s with { Rev = s.Rev + 1, Measure = null };
                    old = _changed;
                    _changed = NewSignal();
                }
                tell = save = true;
            }
            if (r.Temperature is { } temp)
            {
                if (_temp is not { } prev || Math.Abs(prev.Value - temp) >= 0.05 || now - _tempEchoAt >= TempEcho)
                {
                    tell = true;
                    _tempEchoAt = now;
                }
                _temp = (temp, now);
            }
        }
        old?.TrySetResult();
        if (save) Save();
        if (tell) AdminChanged?.Invoke(AdminView(withKey: false));
        return null;
    }

    /// <summary>Новий ключ: старий одразу перестає пускати (відкриті з'єднання з ним закриваються).</summary>
    public string NewDeviceKey()
    {
        TaskCompletionSource old;
        lock (_gate)
        {
            _key = NewKey();
            old = _changed;
            _changed = NewSignal();
        }
        old.TrySetResult();   // розбудити тих, хто чекає: вони перевірять ключ і підуть
        Save();
        return Key;
    }

    void Save()
    {
        Saved s;
        lock (_gate) s = new Saved(_state, _key, _measureSeq, [.. _measures]);
        try { _store.SaveState(StoreKey, JsonSerializer.Serialize(s)); }
        catch (Exception ex) { _log.LogWarning(ex, "стан екрана не записався"); }
    }

    /// <summary>
    /// Дочекатися стану, новішого за <paramref name="since"/>, або вийти за <paramref name="max"/>. Повертає
    /// поточний стан (той самий Rev, якщо нічого не сталося).
    /// </summary>
    public async Task<ScreenState> WaitAsync(long since, TimeSpan max, CancellationToken ct)
    {
        Task signal;
        lock (_gate)
        {
            if (_state.Rev != since) return _state;
            signal = _changed.Task;
        }
        try { await signal.WaitAsync(max, ct); }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { }
        return State;
    }

    /// <summary>Екран щойно звернувся (будь-яким способом).</summary>
    public void Seen()
    {
        bool cameBack;
        lock (_gate)
        {
            var now = _clock.UtcNow;
            cameBack = _streams == 0 && (_seenAt is null || now - _seenAt > SeenFresh);
            _seenAt = now;
        }
        if (cameBack) AdminChanged?.Invoke(AdminView(withKey: false));
    }

    /// <summary>Екран тримає з'єднання (WebSocket чи довге опитування). Dispose — відпустив.</summary>
    public IDisposable Stream()
    {
        Seen();   // спершу «прийшов» (адміну — подія, якщо екран був зник), потім лічимо з'єднання
        lock (_gate) _streams++;
        return new Release(this);
    }

    sealed class Release(Screen s) : IDisposable
    {
        int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1) return;
            lock (s._gate)
            {
                s._streams--;
                s._seenAt = s._clock.UtcNow;
            }
        }
    }

    /// <summary>Що бачить екран — тільки те, що треба показати.</summary>
    public static object DeviceView(ScreenState s) => new
    {
        rev = s.Rev, brightness = s.Brightness, r = s.R, g = s.G, b = s.B, calibration = s.Calibration, updatedAt = s.UpdatedAt,
        measure = s.Measure is { } m ? new { id = m.Id, at = m.At } : null,
    };

    static object MeasureView(ScreenMeasure m) => new
    {
        id = m.Id, x = m.X, y = m.Y, k = m.K, at = m.At, askedAt = m.AskedAt, changed = m.Changed,
        brightness = m.Brightness, r = m.R, g = m.G, b = m.B, calibration = m.Calibration,
    };

    /// <summary>Що бачить адмін: стан, хто міняв, чи екран на зв'язку; ключ — лише на прямий запит, не в розсилці.</summary>
    public object AdminView(bool withKey)
    {
        ScreenState s;
        int streams;
        DateTimeOffset? seen;
        string key;
        ScreenMeasure[] measures;
        (double Value, DateTimeOffset At)? temp;
        lock (_gate) { s = _state; streams = _streams; seen = _seenAt; key = _key; measures = [.. _measures]; temp = _temp; }
        var online = streams > 0 || (seen is { } t && _clock.UtcNow - t <= SeenFresh);
        return new
        {
            state = new
            {
                rev = s.Rev, brightness = s.Brightness, r = s.R, g = s.G, b = s.B, calibration = s.Calibration, updatedAt = s.UpdatedAt, by = s.By,
                measure = s.Measure is { } m ? new { id = m.Id, at = m.At, by = m.By } : null,
            },
            presets = Presets.Select(p => new { id = p.Id, name = p.Name, r = p.R, g = p.G, b = p.B }).ToArray(),
            device = new { online, streams, seenAt = seen },
            temperature = temp is { } tt ? new { value = tt.Value, at = tt.At } : null,
            measures = measures.Select(MeasureView).ToArray(),
            key = withKey ? key : null,
        };
    }

    // =============================================================================================
    // HTTP
    // =============================================================================================

    public sealed record PatchBody(int? Brightness, int? R, int? G, int? B, string? Preset, bool? Calibration);

    static string? DeviceKeyOf(HttpContext c)
    {
        var h = c.Request.Headers["X-Screen-Key"].ToString();
        return h.Length > 0 ? h : c.Request.Query["key"].ToString();
    }

    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static void Map(WebApplication app)
    {
        // ---- адмін (кука акаунта) ----
        app.MapGet("/api/screen", (HttpContext c, Screen s) =>
            Auth.IsAdmin(c) ? Results.Ok(s.AdminView(withKey: true)) : Results.Json(new { message = "Це бачить лише адмін" }, statusCode: 403));
        app.MapPost("/api/screen", (HttpContext c, PatchBody body, Screen s) =>
        {
            if (!Auth.IsAdmin(c)) return Results.Json(new { message = "Це бачить лише адмін" }, statusCode: 403);
            var err = s.Apply(new ScreenPatch(body.Brightness, body.R, body.G, body.B, body.Preset, body.Calibration), Auth.Nick(c));
            return err is null ? Results.Ok(s.AdminView(withKey: false)) : Results.BadRequest(new { message = err });
        });
        app.MapPost("/api/screen/key", (HttpContext c, Screen s) =>
        {
            if (!Auth.IsAdmin(c)) return Results.Json(new { message = "Це бачить лише адмін" }, statusCode: 403);
            s.NewDeviceKey();
            return Results.Ok(s.AdminView(withKey: true));
        });
        app.MapPost("/api/screen/measure", (HttpContext c, Screen s) =>
        {
            if (!Auth.IsAdmin(c)) return Results.Json(new { message = "Це бачить лише адмін" }, statusCode: 403);
            s.AskMeasure(Auth.Nick(c));
            return Results.Ok(s.AdminView(withKey: false));
        });
        app.MapPost("/api/screen/measure/cancel", (HttpContext c, Screen s) =>
        {
            if (!Auth.IsAdmin(c)) return Results.Json(new { message = "Це бачить лише адмін" }, statusCode: 403);
            s.CancelMeasure();
            return Results.Ok(s.AdminView(withKey: false));
        });

        // Програма пише: { temperature } і/або { measure: { id, x, y, k } }.
        app.MapPost("/api/screen/device", (HttpContext c, Screen s, ScreenReport body) =>
        {
            if (!s.KeyOk(DeviceKeyOf(c))) return Results.Json(new { message = "Не той ключ екрана" }, statusCode: 401);
            s.Seen();
            var err = s.Report(body);
            return err is null ? Results.Ok(new { ok = true }) : Results.BadRequest(new { message = err });
        });

        // ---- екран (ключ пристрою) ----
        // Довге опитування: ?since=<rev> — відповідь, щойно стан зміниться, або через wait секунд (типово 25, ≤ 55).
        // Без since — одразу поточний стан.
        app.MapGet("/api/screen/device", async (HttpContext c, Screen s, long? since, int? wait) =>
        {
            if (!s.KeyOk(DeviceKeyOf(c))) return Results.Json(new { message = "Не той ключ екрана" }, statusCode: 401);
            c.Response.Headers.CacheControl = "no-store";
            if (since is null || since != s.State.Rev)
            {
                s.Seen();
                return Results.Ok(DeviceView(s.State));
            }
            using (s.Stream())
            {
                var st = await s.WaitAsync(since.Value, TimeSpan.FromSeconds(Math.Clamp(wait ?? 25, 1, 55)), c.RequestAborted);
                if (!s.KeyOk(DeviceKeyOf(c))) return Results.Json(new { message = "Ключ екрана змінено" }, statusCode: 401);
                return Results.Ok(DeviceView(st));
            }
        });

        // WebSocket: одразу поточний стан, далі — кожна зміна окремим JSON-повідомленням. Окрема гілка зі своїм
        // UseWebSockets, щоб не чіпати налаштувань хаба SignalR.
        app.Map(new PathString("/api/screen/ws"), b =>
        {
            b.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
            b.Run(c => c.RequestServices.GetRequiredService<Screen>().ServeSocket(c));
        });
    }

    async Task ServeSocket(HttpContext c)
    {
        if (!c.WebSockets.IsWebSocketRequest)
        {
            c.Response.StatusCode = 400;
            await c.Response.WriteAsJsonAsync(new { message = "Тут WebSocket: ws(s)://…/api/screen/ws?key=…" });
            return;
        }
        if (!KeyOk(DeviceKeyOf(c)))
        {
            c.Response.StatusCode = 401;
            await c.Response.WriteAsJsonAsync(new { message = "Не той ключ екрана" });
            return;
        }
        var key = DeviceKeyOf(c);
        using var ws = await c.WebSockets.AcceptWebSocketAsync();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted);
        using var hold = Stream();
        // Слухаємо екран: він може слати звіти (той самий JSON, що й POST /api/screen/device, без відповіді), а ще так
        // видно, що з'єднання закрили, — і тоді виходимо.
        var reader = Task.Run(async () =>
        {
            var buf = new byte[4096];
            using var msg = new MemoryStream();
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var r = await ws.ReceiveAsync(buf, stop.Token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    Seen();
                    if (r.MessageType != WebSocketMessageType.Text) continue;
                    if (msg.Length + r.Count <= 16 * 1024) msg.Write(buf, 0, r.Count);
                    if (!r.EndOfMessage) continue;
                    try
                    {
                        if (JsonSerializer.Deserialize<ScreenReport>(msg.ToArray(), Web) is { } report)
                        {
                            var err = Report(report);
                            if (err is not null) _log.LogInformation("звіт екрана через WebSocket не прийнято: {Err}", err);
                        }
                    }
                    catch (JsonException ex) { _log.LogInformation("екран прислав не JSON: {Msg}", ex.Message); }
                    msg.SetLength(0);
                }
            }
            catch { /* обірвалось — так само виходимо */ }
            stop.Cancel();
        });
        try
        {
            long sent = -1;
            while (!stop.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var st = await WaitAsync(sent, TimeSpan.FromSeconds(30), stop.Token);
                if (!KeyOk(key)) { await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "key changed", CancellationToken.None); break; }
                if (st.Rev == sent) continue;
                var bytes = JsonSerializer.SerializeToUtf8Bytes(DeviceView(st), Web);
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, stop.Token);
                sent = st.Rev;
                Seen();
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            stop.Cancel();
            try { await reader; } catch { }
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
            }
        }
    }
}

/// <summary>Розсилка адмінам (група хаба, куди <see cref="FeedbackDevGroup"/> кладе кожного адміна): «екран змінився».</summary>
public sealed class ScreenAdminWire(Screen screen, IHubContext<RadioHub> hub, ILogger<ScreenAdminWire> log)
{
    public void Attach() => screen.AdminChanged += view => _ = SendAsync(view);

    async Task SendAsync(object view)
    {
        try { await hub.Clients.Group(FeedbackDevGroup.Name).SendAsync("screen", view); }
        catch (Exception ex) { log.LogWarning(ex, "адмінам не долетіла зміна екрана"); }
    }
}
