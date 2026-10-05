using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hlechyky.Games;

/// <summary>
/// Столи переживають перезапуск сервера (02.10.2026: «зроби щоб столи не видалялися під час перезапуску»).
/// <para>
/// Перед перезапуском <c>start.ps1</c> кличе <c>POST /api/internal/freeze</c>: столи завмирають (<see cref="Rooms.Freeze"/>),
/// турнір і столи лягають у <see cref="FileName"/>, і процес можна вбивати. Новий процес на старті, ще до першого
/// з'єднання, повертає їх (<see cref="RestoreAtStart"/>) — з тими самими id, тож браузери після реконекту просто знову
/// дивляться на свій стіл. Про всяк випадок (сервер міг і впасти) знімок без заморозки пишеться раз на
/// <see cref="Every"/>; знімок, старший за <see cref="MaxAge"/>, на старті не береться — це вже не «ті самі столи».
/// </para>
/// <para>
/// <c>GET /api/internal/busy</c> — партії, які перезапуск зараз перервав би: <c>deploy.ps1</c> чекає, поки їх не лишиться.
/// Усе <c>/api/internal/*</c> — лише з цієї машини й лише з ключем із <see cref="KeyFile"/> (заголовок <c>X-Control-Key</c>).
/// </para>
/// </summary>
public sealed class TablesKeeper(Rooms rooms, Tournament? tournament, IClock clock, ILogger<TablesKeeper> log) : BackgroundService
{
    public const string FileName = "data/tables.json";
    public const string KeyFile = "data/control.key";
    public const string KeyHeader = "X-Control-Key";
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(3);
    /// <summary>Довше столи замороженими не стоять: перезапуск, що не відбувся, не має морозити сайт навіки.</summary>
    public static readonly TimeSpan MaxFrozen = TimeSpan.FromSeconds(60);

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    readonly object _write = new();

    /// <summary>Тека, відносно якої лежать файли (у тестах — тимчасова).</summary>
    public string Root { get; init; } = Paths.Root;

    string PathOf(string name) => Path.Combine(Root, name);

    // ---------- заморозка ----------

    /// <summary>Заморозити столи й записати чистий знімок. Повертає, скільки чого зберегли.</summary>
    public object Freeze()
    {
        lock (_write)
        {
            var tables = rooms.Freeze() with { Tournament = tournament?.Freeze() };
            Write(tables);
            var playing = tables.Rooms.Count(r => r.Status == RoomStatus.Playing && !string.IsNullOrEmpty(r.State));
            var cut = tables.Rooms.Count(r => r.Status == RoomStatus.Playing && string.IsNullOrEmpty(r.State) && r.Views is not null);
            log.LogInformation("Столи заморожено перед перезапуском: {Count} у знімку, партій далі {Playing}, переривається {Cut}",
                tables.Rooms.Count, playing, cut);
            return new { ok = true, tables = tables.Rooms.Count, resumes = playing, interrupts = cut, tournament = tables.Tournament is not null };
        }
    }

    /// <summary>Перезапуск скасовано: розморозити й одразу переписати знімок звичайним (інакше старий чистий знімок пролежав би до наступного).</summary>
    public void Thaw()
    {
        lock (_write)
        {
            if (!rooms.Frozen) return;
            rooms.Thaw();
            log.LogInformation("Столи розморожено — перезапуску не було");
            Write(rooms.Capture(clean: false) with { Tournament = tournament?.Freeze() });
        }
    }

    /// <summary>Знімок про всяк випадок (без заморозки). Заморожено — не чіпаємо: на диску вже чистий.</summary>
    public void Snapshot()
    {
        lock (_write)
        {
            if (rooms.Frozen) return;
            Write(rooms.Capture(clean: false) with { Tournament = tournament?.Freeze() });
        }
    }

    void Write(FrozenTables tables)
    {
        // Той самий знімок, що й минулого разу (усі сидять у лобі й мовчать), — диск не смикаємо. Але не довше за
        // пів MaxAge: старт бере лише свіжий знімок, а час у файлі мусить бути правдою.
        var probe = JsonSerializer.SerializeToUtf8Bytes(tables with { At = default }, Json);
        if (!tables.Clean && _probe is not null && probe.AsSpan().SequenceEqual(_probe) && tables.At - _probeAt < MaxAge / 2) return;
        var path = PathOf(FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(tables, Json));
        File.Move(tmp, path, overwrite: true);
        _probe = probe;
        _probeAt = tables.At;
    }

    byte[]? _probe;
    DateTimeOffset _probeAt;

    // ---------- старт ----------

    /// <summary>Повернути столи зі знімка. Кличе Program.cs після Build і до Run — раніше, ніж хтось під'єднається.</summary>
    public RestoreReport? RestoreAtStart()
    {
        var path = PathOf(FileName);
        if (!File.Exists(path)) return null;
        FrozenTables? tables;
        try { tables = JsonSerializer.Deserialize<FrozenTables>(File.ReadAllBytes(path), Json); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Столи: знімок {File} не читається — стартуємо без столів", FileName);
            return null;
        }
        if (tables is null || tables.Version != FrozenTables.CurrentVersion) return null;
        var age = clock.UtcNow - tables.At;
        if (age > MaxAge)
        {
            log.LogInformation("Столи: знімок старий ({Age:0} хв) — стартуємо без столів", age.TotalMinutes);
            return null;
        }
        var report = rooms.Restore(tables);
        if (tables.Tournament is { ValueKind: JsonValueKind.Object } t && tournament is not null)
        {
            try { tournament.Restore(t); }
            catch (Exception ex) { log.LogWarning(ex, "Столи: турнір не відновився"); }
        }
        log.LogInformation(
            "Столи: відновлено {Tables} (партій далі {Continued}, перервано {Interrupted}) і соло-кімнат {Solo}; не вдалось {Skipped}; знімок {Kind} {Age:0.0} с тому",
            report.Tables, report.Continued, report.Interrupted, report.Solo, report.Skipped, tables.Clean ? "чистий" : "про всяк випадок", age.TotalSeconds);
        // Одразу свій знімок: у старому лежить чистий, і якби цей процес упав за мить, партії відновились би вдруге — без ходів, зроблених уже тут.
        try { Snapshot(); }
        catch (Exception ex) { log.LogWarning(ex, "Столи: знімок після старту не записався"); }
        return report;
    }

    // ---------- фон ----------

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var next = clock.UtcNow + Every;
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var now = clock.UtcNow;
                if (rooms.Frozen && now - rooms.FrozenAt > MaxFrozen)
                {
                    log.LogWarning("Столи стоять замороженими {Sec:0} с, а перезапуску нема — розморожую", (now - rooms.FrozenAt).TotalSeconds);
                    Thaw();
                }
                if (now >= next)
                {
                    next = now + Every;
                    Snapshot();
                }
            }
            catch (Exception ex) { log.LogWarning(ex, "Столи: знімок не записався"); }
        }
    }

    // ---------- ключ і ендпоінти ----------

    /// <summary>Ключ до /api/internal/*: створюється раз і лежить у data/ (у git не їде). Його читають start.ps1 і deploy.ps1.</summary>
    public string ControlKey()
    {
        var path = PathOf(KeyFile);
        try
        {
            if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: >= 32 } existing) return existing;
        }
        catch (IOException) { }
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, key);
        return key;
    }

    bool Allowed(HttpContext c) => Allowed(c, _key ??= ControlKey());

    /// <summary>
    /// Запит до /api/internal/* пускаємо лише з цієї машини й лише з ключем. Через Caddy адреса — уже людини
    /// (UseForwardedHeaders), тож ззовні сюди не дістатись навіть із ключем.
    /// </summary>
    public static bool Allowed(HttpContext c, string key)
    {
        if (c.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip)) return false;
        var got = c.Request.Headers[KeyHeader].ToString();
        return key.Length > 0 && got.Length == key.Length
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(got), Encoding.ASCII.GetBytes(key));
    }

    string? _key;

    public static void Map(WebApplication app)
    {
        var keeper = app.Services.GetRequiredService<TablesKeeper>();
        keeper._key = keeper.ControlKey();
        app.MapPost("/api/internal/freeze", (HttpContext c) => keeper.Allowed(c) ? Results.Ok(keeper.Freeze()) : Results.NotFound());
        app.MapPost("/api/internal/thaw", (HttpContext c) =>
        {
            if (!keeper.Allowed(c)) return Results.NotFound();
            keeper.Thaw();
            return Results.Ok(new { ok = true });
        });
        app.MapGet("/api/internal/busy", (HttpContext c) => keeper.Allowed(c) ? Results.Ok(keeper.Busy()) : Results.NotFound());
    }

    /// <summary>Хто зараз грає так, що перезапуск це переривав би.</summary>
    public object Busy() => new { busy = rooms.Busy(), frozen = rooms.Frozen };
}
