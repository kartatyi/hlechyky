using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hlechyky.Games;
using Microsoft.AspNetCore.SignalR;

namespace Hlechyky;

/// <summary>
/// Що зараз має показувати екран. <see cref="Preset"/> не null — показати пресет (red/green/blue/white/black),
/// інакше — колір <see cref="Color"/> 0–1023. Яскравість і калібровка діють завжди. <see cref="Rev"/> росте з
/// кожною зміною: так екран бачить, що щось змінилось, і не перемальовує те саме.
/// </summary>
public sealed record ScreenState(long Rev, int Brightness, int Color, string? Preset, bool Calibration, DateTimeOffset? UpdatedAt, string? By);

/// <summary>Що міняє адмін; чого нема (null) — лишається як було.</summary>
public sealed record ScreenPatch(int? Brightness = null, int? Color = null, string? Preset = null, bool? Calibration = null);

/// <summary>
/// 🖥 Віддалений екран (вкладка «Екран» у Бібліотеці, docs/screen.md). Адмін міняє яскравість, колір, пресет і
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
    public static readonly string[] Presets = ["red", "green", "blue", "white", "black"];
    /// <summary>Скільки після останнього запиту екран ще вважаємо «на зв'язку» (довге опитування — раз на ≤ 55 с).</summary>
    public static readonly TimeSpan SeenFresh = TimeSpan.FromSeconds(70);

    readonly IGameStore _store;
    readonly IClock _clock;
    readonly ILogger<Screen> _log;
    readonly object _gate = new();
    ScreenState _state;
    string _key;
    TaskCompletionSource _changed = NewSignal();
    int _streams;              // відкриті WebSocket-и й довгі опитування, що зараз чекають
    DateTimeOffset? _seenAt;   // останній запит екрана будь-яким способом

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
            if (json is not null) saved = JsonSerializer.Deserialize<Saved>(json);
        }
        catch (Exception ex) { log.LogWarning(ex, "стан екрана не прочитався — починаю з типового"); }
        _state = saved?.State ?? new ScreenState(0, 100, 0, null, false, null, null);
        _key = string.IsNullOrEmpty(saved?.Key) ? NewKey() : saved!.Key!;
        if (saved?.Key != _key) Save();
    }

    sealed record Saved(ScreenState? State, string? Key);

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

    /// <summary>Застосувати зміну адміна. Пресет null + колір — вийти з пресету на свій колір. Помилка — текст, інакше null.</summary>
    public string? Apply(ScreenPatch p, string by)
    {
        string? preset = null;
        if (p.Preset is not null)
        {
            preset = p.Preset.Trim().ToLowerInvariant();
            if (preset is "" or "none") preset = null;
            else if (!Presets.Contains(preset)) return "Нема такого пресету: " + p.Preset;
        }
        if (p.Brightness is null && p.Color is null && p.Preset is null && p.Calibration is null) return "Нічого не змінено";
        TaskCompletionSource old;
        ScreenState now;
        lock (_gate)
        {
            var s = _state;
            var color = p.Color is { } c ? Math.Clamp(c, 0, MaxColor) : s.Color;
            // Свій колір без пресету в тому ж запиті — значить, пресет знято; пресет без кольору — колір лишається.
            var nextPreset = p.Preset is not null ? preset : p.Color is not null ? null : s.Preset;
            now = s with
            {
                Rev = s.Rev + 1,
                Brightness = p.Brightness is { } b ? Math.Clamp(b, 0, MaxBrightness) : s.Brightness,
                Color = color,
                Preset = nextPreset,
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
        lock (_gate) s = new Saved(_state, _key);
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
        rev = s.Rev, brightness = s.Brightness, color = s.Color, preset = s.Preset, calibration = s.Calibration, updatedAt = s.UpdatedAt,
    };

    /// <summary>Що бачить адмін: стан, хто міняв, чи екран на зв'язку; ключ — лише на прямий запит, не в розсилці.</summary>
    public object AdminView(bool withKey)
    {
        ScreenState s;
        int streams;
        DateTimeOffset? seen;
        string key;
        lock (_gate) { s = _state; streams = _streams; seen = _seenAt; key = _key; }
        var online = streams > 0 || (seen is { } t && _clock.UtcNow - t <= SeenFresh);
        return new
        {
            state = new { rev = s.Rev, brightness = s.Brightness, color = s.Color, preset = s.Preset, calibration = s.Calibration, updatedAt = s.UpdatedAt, by = s.By },
            device = new { online, streams, seenAt = seen },
            key = withKey ? key : null,
        };
    }

    // =============================================================================================
    // HTTP
    // =============================================================================================

    public sealed record PatchBody(int? Brightness, int? Color, string? Preset, bool? Calibration);

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
            var err = s.Apply(new ScreenPatch(body.Brightness, body.Color, body.Preset, body.Calibration), Auth.Nick(c));
            return err is null ? Results.Ok(s.AdminView(withKey: false)) : Results.BadRequest(new { message = err });
        });
        app.MapPost("/api/screen/key", (HttpContext c, Screen s) =>
        {
            if (!Auth.IsAdmin(c)) return Results.Json(new { message = "Це бачить лише адмін" }, statusCode: 403);
            s.NewDeviceKey();
            return Results.Ok(s.AdminView(withKey: true));
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
        // Слухаємо екран: він нічого не мусить слати, але так видно, що з'єднання закрили, — і тоді виходимо.
        var reader = Task.Run(async () =>
        {
            var buf = new byte[1024];
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var r = await ws.ReceiveAsync(buf, stop.Token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    Seen();
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
