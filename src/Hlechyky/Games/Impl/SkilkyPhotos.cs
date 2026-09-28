using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Одне фото рубрики «📷 Якого року?»: звідки качати (мініатюра Вікісховища ~1024 px), рік зйомки, підпис після
/// відповіді й атрибуція (автор, ліцензія, сторінка файлу) — її показуємо під фото на розкритті.
/// </summary>
public sealed class SkilkyPhoto
{
    public string Id { get; init; } = "";
    /// <summary>«File:…» на Вікісховищі — людям і для оновлення адреси.</summary>
    public string File { get; init; } = "";
    public string Url { get; init; } = "";
    public int Year { get; init; }
    /// <summary>Підпис після відповіді: що це й де («Хрещатик, трамвай №…»).</summary>
    public string Caption { get; init; } = "";
    public string Author { get; init; } = "";
    public string License { get; init; } = "";
    public string Page { get; init; } = "";
    /// <summary>Україна (для балансу бібліотеки; у грі не видно).</summary>
    public bool Ua { get; init; }
}

/// <summary>
/// Бібліотека фото «Якого року?»: маніфест у репо (<c>data/skilky/photos.json</c>), самі файли — у <c>cache/skilky</c>,
/// докачує фон (як «Де це?» зі своїм <c>cache/geo</c>), щоб репо не роздувалось. Гра бере лише готові фото, а клієнт
/// бачить тільки випадковий токен (<c>/api/games/skilky/photo/&lt;24 hex&gt;.jpg</c>): назва файлу на Вікісховищі
/// часто містить рік — підглянути його в адресі не вийде.
/// </summary>
public sealed class SkilkyPhotos : BackgroundService
{
    public const string FileName = "data/skilky/photos.json";
    public static readonly TimeSpan TokenLife = TimeSpan.FromMinutes(45);
    public const int MaxTokens = 2000;
    public const long MaxBytes = 3L * 1024 * 1024;
    static readonly TimeSpan Every = TimeSpan.FromMinutes(30);
    static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(5);
    /// <summary>Пауза між файлами: Вікісховище не любить, коли його смикають пачкою.</summary>
    static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(700);

    readonly IClock _clock;
    readonly ILogger? _log;
    readonly HttpClient? _http;
    readonly ConcurrentDictionary<string, bool> _ready = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, (string Id, DateTimeOffset Until)> _tokens = new(StringComparer.Ordinal);
    readonly SemaphoreSlim _poke = new(0, 1);

    public SkilkyPhotos(IReadOnlyList<SkilkyPhoto> photos, string dir, IClock clock, ILogger? log = null, bool online = true)
    {
        All = photos;
        ById = photos.GroupBy(p => p.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        Dir = dir;
        _clock = clock;
        _log = log;
        if (online)
        {
            _http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(40) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(GeoPhotos.UserAgent);
        }
        Rescan();
    }

    /// <summary>Без мережі — тести й перевірки: лише те, що вже лежить у теці.</summary>
    public static SkilkyPhotos Offline(IReadOnlyList<SkilkyPhoto> photos, string dir, IClock? clock = null) =>
        new(photos, dir, clock ?? new SystemClock(), online: false);

    public IReadOnlyList<SkilkyPhoto> All { get; }
    public IReadOnlyDictionary<string, SkilkyPhoto> ById { get; }
    public string Dir { get; }

    /// <summary>Скільки фото вже можна показувати.</summary>
    public int ReadyCount => _ready.Count;
    public bool Ready(string id) => _ready.ContainsKey(id);
    public IEnumerable<SkilkyPhoto> ReadyPhotos => All.Where(p => _ready.ContainsKey(p.Id));

    public string PathFor(string id) => Path.Combine(Dir, id + ".jpg");

    /// <summary>Маніфест. Ніколи не кидає: нема файла чи він битий — бібліотека порожня, тема просто не стартує.</summary>
    public static IReadOnlyList<SkilkyPhoto> Load(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return [];
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            var arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
                : doc.RootElement.TryGetProperty("photos", out var p) ? p : default;
            if (arr.ValueKind != JsonValueKind.Array) return [];
            var list = JsonSerializer.Deserialize<List<SkilkyPhoto>>(arr.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
            return [.. list.Where(x => GoodId(x.Id) && x.Year is >= 1826 and <= 2100 && GoodHost(x.Url))];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Ідентифікатор — він же ім'я файлу в кеші: лише латиниця, цифри й дефіс.</summary>
    public static bool GoodId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 60 && id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    public static bool GoodHost(string? url) =>
        url is not null && url.StartsWith("https://upload.wikimedia.org/", StringComparison.Ordinal);

    /// <summary>Перечитати теку: що з маніфесту вже лежить готове.</summary>
    public void Rescan()
    {
        foreach (var p in All)
            if (System.IO.File.Exists(PathFor(p.Id))) _ready[p.Id] = true;
            else _ready.TryRemove(p.Id, out _);
    }

    /// <summary>Токен на фото для клієнта (живе <see cref="TokenLife"/>). Лише пам'ять — годиться під замком кімнати.</summary>
    public string Issue(string id)
    {
        var now = _clock.UtcNow;
        if (_tokens.Count >= MaxTokens)
            foreach (var (k, v) in _tokens) if (v.Until <= now) _tokens.TryRemove(k, out _);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        _tokens[token] = (id, now + TokenLife);
        return token;
    }

    public string? Resolve(string? token)
    {
        if (token is null || !_tokens.TryGetValue(token, out var t) || t.Until <= _clock.UtcNow) return null;
        return PathFor(t.Id);
    }

    /// <summary>Тема «Якого року?» без готових фото — попросити фон не чекати свого півгодинного кола.</summary>
    public void Poke()
    {
        if (_poke.CurrentCount == 0) try { _poke.Release(); } catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_http is null || All.Count == 0) return;
        try { await Task.Delay(FirstDelay, ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await PassAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { _log?.LogWarning(ex, "«Якого року?»: прохід докачування впав"); }
            try { await _poke.WaitAsync(Every, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Одне коло: докачати все, чого ще нема. Повертає, скільки нових.</summary>
    public async Task<int> PassAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        var got = 0;
        foreach (var p in All)
        {
            if (_ready.ContainsKey(p.Id)) continue;
            if (await FetchAsync(p, ct)) got++;
            await Task.Delay(Gap, ct);
        }
        if (got > 0) _log?.LogInformation("«Якого року?»: докачав {Count} фото, готових {Ready} з {All}", got, _ready.Count, All.Count);
        return got;
    }

    async Task<bool> FetchAsync(SkilkyPhoto p, CancellationToken ct)
    {
        try
        {
            using var resp = await _http!.GetAsync(p.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode != HttpStatusCode.OK) { _log?.LogWarning("«Якого року?»: {Id} відповів {Code}", p.Id, (int)resp.StatusCode); return false; }
            if (!string.Equals(resp.Content.Headers.ContentType?.MediaType, "image/jpeg", StringComparison.OrdinalIgnoreCase)) return false;
            if (resp.Content.Headers.ContentLength > MaxBytes) return false;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > MaxBytes) return false;
            return Store(p.Id, bytes);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log?.LogWarning("«Якого року?»: {Id} не скачався — {Error}", p.Id, ex.Message);
            return false;
        }
    }

    /// <summary>Покласти файл у кеш: без EXIF (там буває дата зйомки — готова підказка) і атомарно, через .tmp.</summary>
    public bool Store(string id, byte[] bytes)
    {
        var clean = GeoImage.Strip(bytes);
        if (clean is null || !GoodId(id)) return false;
        Directory.CreateDirectory(Dir);
        var path = PathFor(id);
        var tmp = path + ".tmp";
        System.IO.File.WriteAllBytes(tmp, clean);
        System.IO.File.Move(tmp, path, overwrite: true);
        _ready[id] = true;
        return true;
    }

    public override void Dispose()
    {
        _http?.Dispose();
        _poke.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// Підключення «Якого року?» — два рядки з <see cref="GamesSetup"/>: сервіс із фоновим докачуванням і роздача фото
/// за токеном.
/// </summary>
public static class SkilkySetup
{
    public static IServiceCollection AddSkilky(IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var cache = sp.GetService<IOptionsMonitor<YtDlpOptions>>()?.CurrentValue.CacheDir ?? "cache";
            var log = sp.GetService<ILogger<SkilkyPhotos>>();
            return new SkilkyPhotos(SkilkyPhotos.Load(Paths.Resolve(SkilkyPhotos.FileName)), Path.Combine(Paths.Resolve(cache), "skilky"),
                sp.GetService<IClock>() ?? new SystemClock(), log);
        });
        services.AddHostedService(sp => sp.GetRequiredService<SkilkyPhotos>());
        services.AddSingleton<SkilkyDailyBoard>();
        return services;
    }

    public static WebApplication MapSkilky(WebApplication app)
    {
        app.MapGet("/api/games/skilky/photo/{file}", (string file, SkilkyPhotos photos, HttpContext c) =>
        {
            if (!file.EndsWith(".jpg", StringComparison.Ordinal)) return Results.NotFound();
            if (photos.Resolve(file[..^4]) is not { } path || !System.IO.File.Exists(path)) return Results.NotFound();
            c.Response.Headers.CacheControl = "private, max-age=1800";
            c.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(path, "image/jpeg");
        });
        return app;
    }
}
