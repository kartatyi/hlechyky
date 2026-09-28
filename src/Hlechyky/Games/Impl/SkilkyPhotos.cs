using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Одне фото рубрики «📷 Якого року?»: звідки качати (мініатюра Вікісховища 960 px; у кеші — до 800 px і ≤ 200 КБ), рік зйомки, підпис після
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
    static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(1500);
    /// <summary>Вікісховище сказало 429 («забагато») — коло зупиняємо й приходимо знову за стільки.</summary>
    static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(3);
    bool _throttled;
    /// <summary>Скільки може важити фото в кеші: більше — перетискаємо (мініатюри Вікісховища 960 px важать 250–400 КБ,
    /// а з телефона на мобільному інтернеті кожне фото раунду — на очах у всіх).</summary>
    public const int TargetBytes = 200 * 1024;
    /// <summary>До якої ширини стискати (у грі фото не буває ширшим за ~800 px).</summary>
    public const int TargetWidth = 800;

    readonly IClock _clock;
    readonly ILogger? _log;
    readonly HttpClient? _http;
    readonly ConcurrentDictionary<string, bool> _ready = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, (string Id, DateTimeOffset Until)> _tokens = new(StringComparer.Ordinal);
    readonly SemaphoreSlim _poke = new(0, 1);
    /// <summary>Перетискач (у проді — ffmpeg): байти JPEG → легші байти або null. Лише у фоні, ніколи під замком кімнати.</summary>
    readonly Func<byte[], CancellationToken, Task<byte[]?>>? _shrink;
    /// <summary>Файли, які вже пробували перетиснути (не смикати ffmpeg щопівгодини тим самим).</summary>
    readonly ConcurrentDictionary<string, bool> _shrinkTried = new(StringComparer.Ordinal);

    public SkilkyPhotos(IReadOnlyList<SkilkyPhoto> photos, string dir, IClock clock, ILogger? log = null, bool online = true,
        Func<byte[], CancellationToken, Task<byte[]?>>? shrink = null)
    {
        _shrink = shrink;
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
    public static SkilkyPhotos Offline(IReadOnlyList<SkilkyPhoto> photos, string dir, IClock? clock = null,
        Func<byte[], CancellationToken, Task<byte[]?>>? shrink = null) =>
        new(photos, dir, clock ?? new SystemClock(), online: false, shrink: shrink);

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
            try { await _poke.WaitAsync(_throttled ? Cooldown : Every, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Одне коло: докачати все, чого ще нема. Повертає, скільки нових.</summary>
    public async Task<int> PassAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        var got = 0;
        _throttled = false;
        foreach (var p in All)
        {
            if (_ready.ContainsKey(p.Id)) continue;
            if (await FetchAsync(p, ct)) got++;
            if (_throttled)
            {
                _log?.LogInformation("«Якого року?»: Вікісховище просить пригальмувати (429) — решту докачаю за {Min} хв", Cooldown.TotalMinutes);
                break;
            }
            await Task.Delay(Gap, ct);
        }
        if (got > 0) _log?.LogInformation("«Якого року?»: докачав {Count} фото, готових {Ready} з {All}", got, _ready.Count, All.Count);
        await ShrinkCachedAsync(ct);
        return got;
    }

    /// <summary>Докачані раніше (ще до перетискання) важкі файли — перетиснути, кожен лише раз. Повертає, скільки полегшало.</summary>
    public async Task<int> ShrinkCachedAsync(CancellationToken ct)
    {
        if (_shrink is null) return 0;
        var n = 0;
        foreach (var p in All)
        {
            if (!_ready.ContainsKey(p.Id) || _shrinkTried.ContainsKey(p.Id)) continue;
            var path = PathFor(p.Id);
            try
            {
                if (new FileInfo(path).Length <= TargetBytes) continue;
                _shrinkTried[p.Id] = true;
                var bytes = await System.IO.File.ReadAllBytesAsync(path, ct);
                if (await StoreAsync(p.Id, bytes, ct) && new FileInfo(path).Length < bytes.Length) n++;
            }
            catch (IOException) { }
        }
        if (n > 0) _log?.LogInformation("«Якого року?»: перетиснув {Count} важких фото", n);
        return n;
    }

    /// <summary>Покласти в кеш, дорогою перетиснувши, якщо важче за <see cref="TargetBytes"/> або JPEG кривий. Не вийшло — кладемо як є.</summary>
    public async Task<bool> StoreAsync(string id, byte[] bytes, CancellationToken ct)
    {
        // Перетискаємо й «криві» JPEG (хвіст після кінця картинки тощо), які інакше кеш не бере: ffmpeg пише чистий.
        if (_shrink is not null && (bytes.Length > TargetBytes || GeoImage.Strip(bytes) is null))
        {
            try
            {
                if (await _shrink(bytes, ct) is { Length: > 0 } small && small.Length < bytes.Length) bytes = small;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log?.LogWarning("«Якого року?»: {Id} не перетиснувся — {Error}", id, ex.Message); }
        }
        return Store(id, bytes);
    }

    /// <summary>Справжній перетискач: ffmpeg з <c>YtDlp:FfmpegDir</c>, ширина до <see cref="TargetWidth"/>, якість 4, а якщо
    /// й так важко — 7, а тоді ще й 640 px. Нема ffmpeg чи він упав — null (фото піде як є).</summary>
    public static Func<byte[], CancellationToken, Task<byte[]?>> FfmpegShrinker(string ffmpeg, string tmpDir) => async (bytes, ct) =>
    {
        Directory.CreateDirectory(tmpDir);
        var stem = Path.Combine(tmpDir, "shrink-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)));
        var src = stem + "-in.jpg";
        var dst = stem + "-out.jpg";
        try
        {
            await System.IO.File.WriteAllBytesAsync(src, bytes, ct);
            byte[]? best = null;
            foreach (var (w, q) in new[] { (TargetWidth, "4"), (TargetWidth, "7"), (640, "7") })
            {
                // Висока вузька картинка на 800 px ширини буває завелика — тоді ще й менша ширина.
                if (!await RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-i", src, "-map_metadata", "-1",
                        "-vf", $"scale='min({w},iw)':-2", "-q:v", q, dst], ct)) return best;
                best = await System.IO.File.ReadAllBytesAsync(dst, ct);
                if (best.Length <= TargetBytes) break;
            }
            return best;
        }
        finally
        {
            try { System.IO.File.Delete(src); } catch (IOException) { }
            try { System.IO.File.Delete(dst); } catch (IOException) { }
        }
    };

    static async Task<bool> RunAsync(string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
        if (p is null) return false;
        using (p)
        {
            var e = p.StandardError.ReadToEndAsync(ct);
            var o = p.StandardOutput.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                if (ct.IsCancellationRequested) throw;
                return false;
            }
            await Task.WhenAll(e, o);
            return p.ExitCode == 0;
        }
    }

    async Task<bool> FetchAsync(SkilkyPhoto p, CancellationToken ct)
    {
        try
        {
            using var resp = await _http!.GetAsync(p.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests) { _throttled = true; return false; }
            if (resp.StatusCode != HttpStatusCode.OK) { _log?.LogWarning("«Якого року?»: {Id} відповів {Code}", p.Id, (int)resp.StatusCode); return false; }
            if (!string.Equals(resp.Content.Headers.ContentType?.MediaType, "image/jpeg", StringComparison.OrdinalIgnoreCase)) return false;
            if (resp.Content.Headers.ContentLength > MaxBytes) return false;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > MaxBytes) return false;
            return await StoreAsync(p.Id, bytes, ct);
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
            var dir = Path.Combine(Paths.Resolve(cache), "skilky");
            var ffDir = sp.GetService<IOptionsMonitor<YtDlpOptions>>()?.CurrentValue.FfmpegDir ?? "tools/yt-dlp";
            var ffmpeg = Path.Combine(Paths.Resolve(ffDir), OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            if (!System.IO.File.Exists(ffmpeg)) ffmpeg = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";   // хай шукає в PATH
            return new SkilkyPhotos(SkilkyPhotos.Load(Paths.Resolve(SkilkyPhotos.FileName)), dir, sp.GetService<IClock>() ?? new SystemClock(),
                log, shrink: SkilkyPhotos.FfmpegShrinker(ffmpeg, Path.Combine(dir, "tmp")));
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
