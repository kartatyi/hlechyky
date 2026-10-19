using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Картинки «Мемології»: фоновий завантажувач у <c>cache/memes</c>, нормалізація ffmpeg-ом і роздача за токеном.
/// <para>
/// Кожен мем качається раз і перетворюється на три JPEG без метаданих: два кадри проявлення з великих пікселів
/// (<see cref="PixelSides"/>) і чіткий (довша сторона ≤ <see cref="MaxSide"/>, з застосованим <c>crop</c>; у gif —
/// перший кадр). Ім'я в кеші — SHA-256 адреси й рамки (<see cref="KeyFor"/>) плюс номер кадру: ні назви, ні id.
/// Клієнт бачить лише <c>/api/games/memy/&lt;24 hex&gt;.jpg</c> — випадковий токен, свій на кожен кадр.
/// </para>
/// <para>
/// Гра під замком кімнати лише читає множину готових і видає токени — усе в пам'яті; мережа, диск і ffmpeg — тільки
/// тут, у фоні, і в HTTP-обробнику. Невдалі завантаження — у лог і в <see cref="Failed"/> (для адмінки).
/// </para>
/// </summary>
public sealed class MemyImages : BackgroundService
{
    public const string UserAgent = GeoPhotos.UserAgent;
    public static readonly TimeSpan TokenLife = TimeSpan.FromMinutes(45);
    public const int MaxTokens = 4000;
    /// <summary>Скільки можна качати за одну картинку: gif буває важким, а нам із нього потрібен лише перший кадр.</summary>
    public const long MaxBytes = 16L * 1024 * 1024;
    public const long CacheCap = 200L * 1024 * 1024;
    /// <summary>Довша сторона чіткої картинки.</summary>
    public const int MaxSide = 960;
    /// <summary>Кадри проявлення: скільки «пікселів» на довшій стороні (кадр 0, кадр 1). Далі — чітка.</summary>
    public static readonly int[] PixelSides = [24, 60];
    /// <summary>
    /// Піксельні кадри розтягуються найближчим сусідом рівно до розміру чіткого — картинка на екрані не стрибає між
    /// кадрами. Запасний граф для ffmpeg без «scale» зі зразком (до 7.1) — до цієї довшої сторони.
    /// </summary>
    public const int PixelOut = 480;
    /// <summary>Кадрів на мем: два піксельні й чіткий (останній).</summary>
    public const int Frames = 3;
    public const int Clear = Frames - 1;
    /// <summary>Після стількох невдач поспіль мем засинає до рестарту.</summary>
    public const int FailsBeforeSleep = 3;
    static readonly TimeSpan Every = TimeSpan.FromMinutes(30);
    static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(4);
    static readonly TimeSpan FfmpegTimeout = TimeSpan.FromSeconds(30);

    readonly IClock _clock;
    readonly ILogger? _log;
    readonly HttpClient? _http;
    readonly TimeSpan _pause;
    readonly bool _online;
    readonly SemaphoreSlim _poke = new(0, 1);
    readonly ConcurrentDictionary<string, (string Path, DateTimeOffset Expires)> _tokens = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, int> _fails = new(StringComparer.Ordinal);
    /// <summary>Мем → чому не вийшло (останнє). Що тут і в <see cref="_asleep"/> — до рестарту не чіпаємо.</summary>
    readonly ConcurrentDictionary<string, string> _failed = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, bool> _asleep = new(StringComparer.Ordinal);
    /// <summary>Що гра попросила першим (стіл чекає): завантажувач бере це раніше за решту банку.</summary>
    readonly ConcurrentQueue<string> _wanted = new();
    readonly object _readyLock = new();
    /// <summary>Готові меми (усі три кадри на диску). Незмінний знімок: читачі беруть посилання без замка.</summary>
    volatile HashSet<string> _ready = new(StringComparer.Ordinal);
    bool _warnedNoFfmpeg;

    public MemyImages(MemyBank bank, string dir, IClock clock, string? ffmpeg, ILogger? log = null,
        HttpMessageHandler? handler = null, TimeSpan? pause = null, bool online = true)
    {
        Bank = bank;
        Dir = dir;
        Ffmpeg = ffmpeg;
        _clock = clock;
        _log = log;
        _pause = pause ?? TimeSpan.FromMilliseconds(300);
        _online = online;
        if (online || handler is not null)
        {
            _http = new HttpClient(handler ?? new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            }) { Timeout = TimeSpan.FromSeconds(30) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }
        Rescan();
    }

    /// <summary>Для тестів: без мережі й без фонового циклу — готовим вважається те, що вже лежить у <paramref name="dir"/>.</summary>
    public static MemyImages Offline(MemyBank bank, string dir, IClock? clock = null) =>
        new(bank, dir, clock ?? new SystemClock(), null, online: false);

    public MemyBank Bank { get; }
    public string Dir { get; }
    /// <summary>ffmpeg для нормалізації; без нього нічого нового готовим не стане (кеш, що вже є, грає).</summary>
    public string? Ffmpeg { get; }

    /// <summary>
    /// Ключ мему в кеші: хеш адреси й рамки (змінили <c>crop</c> — картинка переробиться) і нічого більше. З адреси
    /// його не вгадати, бо клієнт адреси не бачить; з токена — тим паче.
    /// </summary>
    public static string KeyFor(MemyMeme m)
    {
        var crop = m.Crop is null ? "" : string.Join(",", m.Crop.Select(x => x.ToString("R", CultureInfo.InvariantCulture)));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(m.ImageUrl + "|" + crop)))[..32];
    }

    public static string FileNameFor(MemyMeme m, int frame) => $"{KeyFor(m)}-{frame.ToString(CultureInfo.InvariantCulture)}.jpg";

    public string PathFor(MemyMeme m, int frame) => Path.Combine(Dir, FileNameFor(m, frame));

    // ---------------------------------------------------------------------------------------
    // готові
    // ---------------------------------------------------------------------------------------

    /// <summary>Усі три кадри мему лежать на диску.</summary>
    public bool Ready(string id) => _ready.Contains(id);

    public int ReadyCount => _ready.Count;

    /// <summary>Мем не скачався й до рестарту більше не пробуватиметься: гра його пропускає.</summary>
    public bool Gone(string id) => _asleep.ContainsKey(id);

    /// <summary>Що не вийшло: id → причина (останнє слово). Для адмінки й логів.</summary>
    public IReadOnlyDictionary<string, string> Failed => _failed;

    /// <summary>Перечитати теку: що з банку вже лежить на диску всіма трьома кадрами.</summary>
    public void Rescan()
    {
        var next = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in Bank.Memes)
        {
            var all = true;
            for (var f = 0; f < Frames && all; f++) all = File.Exists(PathFor(m, f));
            if (all) next.Add(m.Id);
        }
        lock (_readyLock) _ready = next;
    }

    void MarkReady(string id)
    {
        lock (_readyLock) _ready = new HashSet<string>(_ready, StringComparer.Ordinal) { id };
    }

    /// <summary>Здатись на мемі до рестарту: гра його пропустить (і тести так імітують мертве посилання).</summary>
    public void GiveUp(string id, string why)
    {
        _failed[id] = why;
        _asleep[id] = true;
    }

    // ---------------------------------------------------------------------------------------
    // токени
    // ---------------------------------------------------------------------------------------

    /// <summary>Новий непрозорий токен на кадр мему: 24 hex, живе <see cref="TokenLife"/>, на кожен кадр — свій.</summary>
    public string Issue(string id, int frame)
    {
        var m = Bank.Find(id) ?? throw new ArgumentException("нема такого мему", nameof(id));
        if (frame is < 0 or >= Frames) throw new ArgumentOutOfRangeException(nameof(frame));
        var now = _clock.UtcNow;
        if (_tokens.Count >= MaxTokens) Trim(now);
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        _tokens[token] = (PathFor(m, frame), now + TokenLife);
        return token;
    }

    void Trim(DateTimeOffset now)
    {
        foreach (var (k, v) in _tokens) if (v.Expires <= now) _tokens.TryRemove(k, out _);
        var extra = _tokens.Count - MaxTokens + 1;
        if (extra <= 0) return;
        foreach (var k in _tokens.OrderBy(kv => kv.Value.Expires).Take(extra).Select(kv => kv.Key).ToList())
            _tokens.TryRemove(k, out _);
    }

    /// <summary>Шлях до файла за токеном; null — токен кривий, чужий чи протух.</summary>
    public string? Resolve(string? token)
    {
        if (!GeoPhotos.IsToken(token) || !_tokens.TryGetValue(token!, out var hit)) return null;
        if (hit.Expires <= _clock.UtcNow) { _tokens.TryRemove(token!, out _); return null; }
        return hit.Path;
    }

    // ---------------------------------------------------------------------------------------
    // завантажувач
    // ---------------------------------------------------------------------------------------

    /// <summary>Стіл чекає на ці меми: поставити їх першими в чергу й штовхнути фон. Миттєво, без I/O.</summary>
    public void Want(IEnumerable<string> ids)
    {
        if (!_online) return;
        foreach (var id in ids)
        {
            if (_wanted.Count >= 200) break;
            if (!Ready(id) && !Gone(id)) _wanted.Enqueue(id);
        }
        Poke();
    }

    /// <summary>Штовхнути фоновий цикл (гра чекає на картинку). Дешево: без винятків, коли вже штовхнули.</summary>
    public void Poke()
    {
        if (!_online || _poke.CurrentCount > 0) return;
        try { _poke.Release(); } catch (SemaphoreFullException) { /* уже штовхнули */ }
    }

    /// <summary>
    /// Один прохід: усе, чого нема на диску, — по одному, з паузою. Спершу те, що попросив стіл (<see cref="Want"/>),
    /// потім решта банку за порядком.
    /// </summary>
    public async Task<int> PassAsync(CancellationToken ct)
    {
        if (_http is null) return 0;
        if (Ffmpeg is null || !File.Exists(Ffmpeg))
        {
            if (!_warnedNoFfmpeg) _log?.LogWarning("Мемологія: нема ffmpeg ({Path}) — нових картинок не буде", Ffmpeg);
            _warnedNoFfmpeg = true;
            return 0;
        }
        var tried = new HashSet<string>(StringComparer.Ordinal);
        var got = 0;
        while (Next(tried) is { } m)
        {
            ct.ThrowIfCancellationRequested();
            tried.Add(m.Id);
            if (await FetchAsync(m, ct)) got++;
            if (_pause > TimeSpan.Zero) await Task.Delay(_pause, ct);
        }
        return got;
    }

    MemyMeme? Next(HashSet<string> tried)
    {
        while (_wanted.TryDequeue(out var id))
            if (!tried.Contains(id) && !Ready(id) && !Gone(id) && Bank.Find(id) is { } w) return w;
        foreach (var m in Bank.Memes)
            if (!tried.Contains(m.Id) && !Ready(m.Id) && !Gone(m.Id)) return m;
        return null;
    }

    /// <summary>Скачати й нормалізувати один мем. Безнадійне (не картинка, завелике, ffmpeg не розкодував) засинає одразу, мережеве — після трьох спроб.</summary>
    public async Task<bool> FetchAsync(MemyMeme m, CancellationToken ct)
    {
        if (_http is null || Ffmpeg is null) return false;
        Directory.CreateDirectory(Dir);
        var raw = Path.Combine(Dir, KeyFor(m) + "." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)) + ".src.tmp");
        try
        {
            var (ok, final, why) = await Download(m.ImageUrl, raw, ct);
            if (ok) (ok, why) = await NormalizeAsync(Ffmpeg, raw, m, Dir, _log, ct);
            if (ok)
            {
                _fails.TryRemove(m.Id, out _);
                _failed.TryRemove(m.Id, out _);
                MarkReady(m.Id);
                return true;
            }
            _log?.LogWarning("Мемологія: {Id} ({Name}) не скачався — {Why}", m.Id, m.Name, why);
            _failed[m.Id] = why;
            // Не розкодувалось чи це не картинка — повтор нічого не змінить.
            if (final || why.StartsWith("ffmpeg", StringComparison.Ordinal)) _asleep[m.Id] = true;
            else if (_fails.AddOrUpdate(m.Id, 1, (_, n) => n + 1) >= FailsBeforeSleep) _asleep[m.Id] = true;
            return false;
        }
        finally
        {
            try { if (File.Exists(raw)) File.Delete(raw); } catch (IOException) { }
        }
    }

    /// <summary>(вдалось, безнадійно, чому ні). Пише сирий файл у <paramref name="to"/>.</summary>
    async Task<(bool Ok, bool Final, string Why)> Download(string url, string to, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http!.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode != HttpStatusCode.OK) return (false, resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone, $"відповідь {(int)resp.StatusCode}");
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && type != "application/octet-stream" && type.Length > 0)
                return (false, true, $"не картинка ({type})");
            if (resp.Content.Headers.ContentLength > MaxBytes) return (false, true, "завелике");
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            await using (var file = File.Create(to))
            {
                var buf = new byte[81920];
                long total = 0;
                int n;
                while ((n = await stream.ReadAsync(buf, ct)) > 0)
                {
                    total += n;
                    if (total > MaxBytes) return (false, true, $"завелике (понад {MaxBytes / (1024 * 1024)} МБ)");
                    await file.WriteAsync(buf.AsMemory(0, n), ct);
                }
                if (total == 0) return (false, false, "порожня відповідь");
            }
            return (true, false, "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return (false, false, ex.Message); }
    }

    /// <summary>
    /// Сирий файл → три JPEG мему (одним запуском ffmpeg): рамка <c>crop</c>, довша сторона ≤ <see cref="MaxSide"/>,
    /// без метаданих; з чіткої — два піксельні кадри. Після ffmpeg кожен ще раз іде через <see cref="GeoImage.Strip"/>
    /// (ffmpeg дописує свій коментар) і лише тоді атомарно стає на місце; чіткий — останнім, бо готовність = усі три.
    /// </summary>
    public static async Task<(bool Ok, string Why)> NormalizeAsync(string ffmpeg, string src, MemyMeme m, string dir, ILogger? log, CancellationToken ct)
    {
        var key = KeyFor(m);
        var tmp = new string[Frames];
        for (var f = 0; f < Frames; f++) tmp[f] = Path.Combine(dir, $"{key}-{f.ToString(CultureInfo.InvariantCulture)}.tmp.jpg");
        try
        {
            var (ok, err) = await RunAsync(ffmpeg, Args(src, m.Crop, tmp), ct);
            // старий ffmpeg не знає «scale» зі зразком — ще раз запасним графом (розтяг до PixelOut)
            if (!ok) (ok, err) = await RunAsync(ffmpeg, Args(src, m.Crop, tmp, exact: false), ct);
            if (!ok) return (false, "ffmpeg не розкодував: " + err);
            var clean = new byte[Frames][];
            for (var f = 0; f < Frames; f++)
            {
                if (!File.Exists(tmp[f]) || GeoImage.Strip(await File.ReadAllBytesAsync(tmp[f], ct)) is not { } c) return (false, "ffmpeg дав битий JPEG");
                clean[f] = c;
            }
            for (var f = 0; f < Frames; f++)
            {
                await File.WriteAllBytesAsync(tmp[f], clean[f], ct);
                File.Move(tmp[f], Path.Combine(dir, FileNameFor(m, f)), overwrite: true);
            }
            return (true, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            log?.LogWarning("Мемологія: ffmpeg на {Id} спіткнувся — {Error}", m.Id, ex.Message);
            return (false, "ffmpeg: " + ex.Message);
        }
        finally
        {
            foreach (var t in tmp) try { if (File.Exists(t)) File.Delete(t); } catch (IOException) { }
        }
    }

    /// <summary>Аргументи ffmpeg: один вхід, три виходи (кадр 0, кадр 1, чіткий), кожен — один кадр без метаданих.</summary>
    public static List<string> Args(string src, double[]? crop, string[] outs, bool exact = true)
    {
        static string N(double x) => x.ToString("0.######", CultureInfo.InvariantCulture);
        static string Long(int side, int keep) => $"w='if(gte(iw,ih),{side},{keep})':h='if(gte(iw,ih),{keep},{side})'";
        var cut = crop is null ? "" : $"crop=w=iw*{N(crop[2])}:h=ih*{N(crop[3])}:x=iw*{N(crop[0])}:y=ih*{N(crop[1])},";
        // чітка: рамка, потім довша сторона ≤ MaxSide (менші не розтягуємо), парні сторони — для 4:2:0
        var graph = $"[0:v:0]{cut}scale=w='min({MaxSide},iw)':h='min({MaxSide},ih)':force_original_aspect_ratio=decrease:force_divisible_by=2,"
            + (exact
                // піксельні: зменшити до кількох десятків «пікселів», потім найближчим сусідом до розміру чіткої (зразок r0/r1)
                ? "split=5[c][a][b][r0][r1];"
                    + $"[a]scale={Long(PixelSides[0], -1)}:flags=area[a1];[a1][r0]scale=w=rw:h=rh:flags=neighbor[p0];"
                    + $"[b]scale={Long(PixelSides[1], -1)}:flags=area[b1];[b1][r1]scale=w=rw:h=rh:flags=neighbor[p1]"
                : "split=3[c][a][b];"
                    + $"[a]scale={Long(PixelSides[0], -2)}:flags=area,scale={Long(PixelOut, -2)}:flags=neighbor[p0];"
                    + $"[b]scale={Long(PixelSides[1], -2)}:flags=area,scale={Long(PixelOut, -2)}:flags=neighbor[p1]");
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-threads", "1", "-i", src, "-filter_complex", graph };
        string[] labels = ["[p0]", "[p1]", "[c]"];
        for (var f = 0; f < Frames; f++)
            args.AddRange(["-map", labels[f], "-frames:v", "1", "-map_metadata", "-1", "-q:v", f == Clear ? "4" : "5", outs[f]]);
        return args;
    }

    static async Task<(bool Ok, string Error)> RunAsync(string exe, List<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return (false, "не запустився");
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { /* уже вийшов — не біда */ }
        var err = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(FfmpegTimeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            return (false, "не вклався в 30 с");
        }
        var text = (await err).Trim();
        return (p.ExitCode == 0, text.Length > 200 ? text[..200] : text);
    }

    // ---------------------------------------------------------------------------------------
    // кеш на диску
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Прибирання теки: файли, яких нема в банку, — геть; недописані тимчасові старші за годину — геть; понад
    /// <see cref="CacheCap"/> — спершу ті, що найдавніше віддавали гравцям.
    /// </summary>
    public int Sweep()
    {
        if (!Directory.Exists(Dir)) return 0;
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in Bank.Memes) for (var f = 0; f < Frames; f++) wanted.Add(FileNameFor(m, f));
        var removed = 0;
        var now = DateTime.UtcNow;
        var keep = new List<FileInfo>();
        foreach (var file in new DirectoryInfo(Dir).EnumerateFiles())
        {
            if (file.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || file.Name.EndsWith(".tmp.jpg", StringComparison.OrdinalIgnoreCase))
            {
                if (now - file.LastWriteTimeUtc > TimeSpan.FromHours(1) && TryDelete(file)) removed++;
                continue;
            }
            if (!wanted.Contains(file.Name)) { if (TryDelete(file)) removed++; continue; }
            keep.Add(file);
        }
        var total = keep.Sum(f => f.Length);
        if (total > CacheCap)
            foreach (var file in keep.OrderBy(f => f.LastAccessTimeUtc))
            {
                if (total <= CacheCap) break;
                var len = file.Length;
                if (TryDelete(file)) { removed++; total -= len; }
            }
        if (removed > 0) Rescan();
        return removed;
    }

    bool TryDelete(FileInfo f)
    {
        try { f.Delete(); return true; }
        catch (Exception ex) { _log?.LogWarning("Мемологія: не видалив {File} — {Error}", f.Name, ex.Message); return false; }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_online) return;
        try { await Task.Delay(FirstDelay, ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Rescan();
                var got = await PassAsync(ct);
                var gone = Sweep();
                if (got > 0 || gone > 0)
                    _log?.LogInformation("Мемологія: картинок докачано {Got}, прибрано {Gone}; готових {Ready} з {All}, не вийшло {Failed}",
                        got, gone, ReadyCount, Bank.Memes.Count, _failed.Count);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log?.LogWarning(ex, "Мемологія: прохід завантажувача спіткнувся"); }
            try { await _poke.WaitAsync(Every, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public override void Dispose()
    {
        _http?.Dispose();
        _poke.Dispose();
        base.Dispose();
    }
}
