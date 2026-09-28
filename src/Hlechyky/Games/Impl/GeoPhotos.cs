using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// JPEG без метаданих. До розкриття в клієнта нема нічого, крім картинки, — тож у картинці не має лишитись
/// нічого, крім пікселів: EXIF (з GPS!), XMP, ICC-профіль, коментарі — геть.
/// <para>
/// Лишаються: SOI, APP0 (JFIF), APP14 (Adobe — прапорець колірного перетворення, без нього CMYK-файли
/// вивертались би негативом), DQT, SOFn, DHT, DRI, SOS із даними й EOI. Єдине, що переживає з EXIF, — поворот
/// кадру (Orientation 2–8): без нього знімок «боком» так боком і показувався б. Для нього пишемо власний
/// крихітний APP1 рівно з одним тегом — ні GPS, ні дати, ні камери там нема.
/// </para>
/// </summary>
public static class GeoImage
{
    /// <summary>Очищений JPEG або null, якщо це не JPEG чи послідовність сегментів бита.</summary>
    public static byte[]? Strip(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;
        var head = new List<(byte Marker, int From, int Len)>();
        var orientation = 0;
        var i = 2;
        while (i < data.Length)
        {
            if (data[i] != 0xFF) return null;
            while (i < data.Length && data[i] == 0xFF) i++;       // заповнювачі FF FF … дозволені стандартом
            if (i >= data.Length) return null;
            var m = data[i++];
            if (m == 0xD9 || m == 0xD8 || m is >= 0xD0 and <= 0xD7 || m == 0x01) return null;   // до SOS таких не буває
            if (i + 2 > data.Length) return null;
            var len = (data[i] << 8) | data[i + 1];
            if (len < 2 || i + len > data.Length) return null;
            if (m == 0xDA)
            {
                // SOS: далі стиснені дані до кінця файла (у прогресивних — ще сканування й таблиці, але вже без
                // метаданих). Файл мусить закінчуватись EOI — інакше це обрізане завантаження.
                if (!EndsWithEoi(data)) return null;
                return Assemble(data, head, orientation, i - 2);
            }
            var drop = (m >= 0xE1 && m <= 0xEF && m != 0xEE) || m == 0xFE;
            if (m == 0xE1 && orientation == 0) orientation = Orientation(data.Slice(i + 2, len - 2));
            if (!drop) head.Add((m, i - 2, len + 2));
            i += len;
        }
        return null;   // жодного сканування — картинки нема
    }

    static bool EndsWithEoi(ReadOnlySpan<byte> data)
    {
        // хвіст нулів після EOI трапляється; далі за 64 байти не шукаємо
        var end = data.Length;
        var stop = Math.Max(2, data.Length - 64);
        while (end > stop && data[end - 1] == 0) end--;
        return end >= 2 && data[end - 2] == 0xFF && data[end - 1] == 0xD9;
    }

    static byte[] Assemble(ReadOnlySpan<byte> data, List<(byte Marker, int From, int Len)> head, int orientation, int sos)
    {
        using var ms = new MemoryStream(data.Length);
        ms.Write([0xFF, 0xD8]);
        var wroteExif = orientation is < 2 or > 8;
        // APP0 іде першим (JFIF), а поворот — одразу за ним: браузери читають APP1 будь-де в заголовку.
        foreach (var (m, from, len) in head) if (m == 0xE0) ms.Write(data.Slice(from, len));
        if (!wroteExif) ms.Write(OrientationSegment(orientation));
        foreach (var (m, from, len) in head) if (m != 0xE0) ms.Write(data.Slice(from, len));
        ms.Write(data[sos..]);
        return ms.ToArray();
    }

    /// <summary>APP1 «Exif» рівно з одним тегом Orientation (big-endian TIFF): 36 байтів разом із маркером і довжиною.</summary>
    public static byte[] OrientationSegment(int orientation) =>
    [
        0xFF, 0xE1, 0x00, 0x22,
        (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
        (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,
        0x00, 0x01,
        0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, (byte)orientation, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
    ];

    /// <summary>Тег Orientation з тіла APP1 (без маркера й довжини); 0 — нема або це не EXIF.</summary>
    static int Orientation(ReadOnlySpan<byte> p)
    {
        try
        {
            if (p.Length < 14 || p[0] != 'E' || p[1] != 'x' || p[2] != 'i' || p[3] != 'f' || p[4] != 0) return 0;
            var t = p[6..].ToArray();
            bool le;
            if (t[0] == 'I' && t[1] == 'I') le = true;
            else if (t[0] == 'M' && t[1] == 'M') le = false;
            else return 0;
            int U16(int o) => le ? t[o] | (t[o + 1] << 8) : (t[o] << 8) | t[o + 1];
            long U32(int o) => le
                ? (uint)(t[o] | (t[o + 1] << 8) | (t[o + 2] << 16) | (t[o + 3] << 24))
                : (uint)((t[o] << 24) | (t[o + 1] << 16) | (t[o + 2] << 8) | t[o + 3]);
            var ifd = U32(4);
            if (ifd < 8 || ifd + 2 > t.Length) return 0;
            var n = U16((int)ifd);
            for (var k = 0; k < n; k++)
            {
                var e = (int)ifd + 2 + k * 12;
                if (e + 12 > t.Length) return 0;
                if (U16(e) != 0x0112) continue;
                var v = U16(e + 8);
                return v is >= 1 and <= 8 ? v : 0;
            }
            return 0;
        }
        catch (IndexOutOfRangeException) { return 0; }
    }

    /// <summary>Чи є в JPEG сегмент з цим маркером до першого SOS (для тестів і перевірки кешу).</summary>
    public static bool HasSegment(ReadOnlySpan<byte> data, byte marker)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return false;
        var i = 2;
        while (i + 4 <= data.Length && data[i] == 0xFF)
        {
            var m = data[i + 1];
            if (m == marker) return true;
            if (m == 0xDA) return false;
            i += 2 + ((data[i + 2] << 8) | data[i + 3]);
        }
        return false;
    }
}

/// <summary>
/// Фото «Де це?»: фоновий завантажувач із Вікісховища в <c>cache/geo</c>, облік готових і роздача за токеном.
/// <para>
/// Ім'я файла в кеші — SHA-256 адреси (перші 32 hex) + <c>.jpg</c>: ні назви, ні id місця. Метадані зрізає
/// <see cref="GeoImage.Strip"/>. Клієнт отримує лише <c>/api/games/geo/&lt;24 hex&gt;.jpg</c> — випадковий
/// токен, що живе 45 хвилин. Гра (під замком кімнати) лише читає множину готових і видає токени — усе це в
/// пам'яті; диск і мережа — тільки тут, у фоні, і в HTTP-обробнику.
/// </para>
/// </summary>
public sealed class GeoPhotos : BackgroundService
{
    public const string UserAgent = "HlechykyGames/1.0 (https://hlechyky.pp.ua)";
    public static readonly TimeSpan TokenLife = TimeSpan.FromMinutes(45);
    public const int MaxTokens = 2000;
    public const long MaxBytes = 8L * 1024 * 1024;
    public const long CacheCap = 400L * 1024 * 1024;
    /// <summary>Після стількох невдач поспіль — одна спроба оновити адресу через API Вікісховища.</summary>
    public const int FailsBeforeRefresh = 3;
    static readonly TimeSpan Every = TimeSpan.FromMinutes(20);
    static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(3);

    readonly IClock _clock;
    readonly ILogger? _log;
    readonly HttpClient? _http;
    readonly TimeSpan _pause;
    readonly bool _online;
    readonly SemaphoreSlim _poke = new(0, 1);
    readonly ConcurrentDictionary<string, (string Path, DateTimeOffset Expires, byte[]? Key)> _tokens = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, int> _fails = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, string> _alias = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, bool> _asleep = new(StringComparer.Ordinal);
    readonly object _readyLock = new();
    /// <summary>Місце → індекси готових фото. Незмінний знімок: читачі беруть посилання без замка.</summary>
    volatile Dictionary<string, int[]> _ready = new(StringComparer.Ordinal);

    public GeoPhotos(GeoBank bank, string dir, IClock clock, ILogger? log = null, HttpMessageHandler? handler = null,
        TimeSpan? pause = null, bool online = true)
    {
        Bank = bank;
        Dir = dir;
        _clock = clock;
        _log = log;
        _pause = pause ?? TimeSpan.FromMilliseconds(250);
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
    public static GeoPhotos Offline(GeoBank bank, string dir, IClock? clock = null) =>
        new(bank, dir, clock ?? new SystemClock(), online: false);

    public GeoBank Bank { get; }
    public string Dir { get; }
    /// <summary>ffmpeg для перетискання фото до ~180 КБ (<see cref="GeoShrink"/>); null — не тиснемо.</summary>
    public string? Ffmpeg { get; init; }

    /// <summary>Ім'я файла в кеші для адреси фото: хеш і нічого більше.</summary>
    public static string FileNameFor(string url) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32] + ".jpg";

    public string PathFor(GeoPhoto photo) => Path.Combine(Dir, FileNameFor(photo.Url));

    // ---------------------------------------------------------------------------------------
    // готові фото
    // ---------------------------------------------------------------------------------------

    public bool Ready(string placeId) => _ready.ContainsKey(placeId);

    public IReadOnlyList<int> ReadyPhotos(string placeId) => _ready.TryGetValue(placeId, out var list) ? list : [];

    /// <summary>Скільки місць мають хоч одне готове фото.</summary>
    public int ReadyPlaces => _ready.Count;

    /// <summary>Перечитати теку: що з банку вже лежить на диску. Кличеться у фоні (і в Offline — одразу).</summary>
    public void Rescan()
    {
        var next = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var place in Bank.Places)
        {
            List<int>? got = null;
            for (var k = 0; k < place.Photos.Count; k++)
                if (File.Exists(PathFor(place.Photos[k]))) (got ??= []).Add(k);
            if (got is not null) next[place.Id] = [.. got];
        }
        lock (_readyLock) _ready = next;
    }

    void MarkReady(GeoPlace place, int idx, bool ready)
    {
        lock (_readyLock)
        {
            var next = new Dictionary<string, int[]>(_ready, StringComparer.Ordinal);
            var list = next.TryGetValue(place.Id, out var had) ? had.ToList() : [];
            if (ready && !list.Contains(idx)) list.Add(idx);
            if (!ready) list.Remove(idx);
            list.Sort();
            if (list.Count == 0) next.Remove(place.Id);
            else next[place.Id] = [.. list];
            _ready = next;
        }
    }

    // ---------------------------------------------------------------------------------------
    // токени
    // ---------------------------------------------------------------------------------------

    /// <summary>Новий непрозорий токен на фото місця: 24 hex, живе <see cref="TokenLife"/>.</summary>
    public string Issue(string placeId, int photoIdx)
    {
        var place = Bank.Find(placeId) ?? throw new ArgumentException("нема такого місця", nameof(placeId));
        return IssueFile(PathFor(place.Photos[photoIdx]));
    }

    /// <summary>Токен на будь-який файл, який гра сама вибрала (фото друзів — <see cref="GeoMine"/>).</summary>
    public string IssueFile(string path) => Add(path, null);

    /// <summary>
    /// Запечатане фото наступного раунду: токен на <c>.bin</c> (файл, зашифрований AES-GCM) і ключ до нього.
    /// Браузер тягне шифр, поки всі дивляться розкриття, а ключ приходить лише у «Готуйсь» — тож наперед
    /// скачане не підглянеш навіть у консолі. За цим токеном <c>.jpg</c> не віддається.
    /// </summary>
    public (string Token, byte[] Key) IssueSealed(string path)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        return (Add(path, key), key);
    }

    string Add(string path, byte[]? key)
    {
        var now = _clock.UtcNow;
        if (_tokens.Count >= MaxTokens) Trim(now);
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        _tokens[token] = (path, now + TokenLife, key);
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
        if (!IsToken(token)) return null;
        if (!_tokens.TryGetValue(token!, out var hit)) return null;
        if (hit.Expires <= _clock.UtcNow) { _tokens.TryRemove(token!, out _); return null; }
        return hit.Key is null ? hit.Path : null;
    }

    /// <summary>Шлях і ключ запечатаного фото; null — токен кривий, протух або це звичайний (не запечатаний).</summary>
    public (string Path, byte[] Key)? ResolveSealed(string? token)
    {
        if (!IsToken(token) || !_tokens.TryGetValue(token!, out var hit) || hit.Key is null) return null;
        if (hit.Expires <= _clock.UtcNow) { _tokens.TryRemove(token!, out _); return null; }
        return (hit.Path, hit.Key);
    }

    /// <summary>
    /// Шифр для запечатаного фото: <c>nonce(12) ‖ шифротекст ‖ tag(16)</c> — саме те, що WebCrypto
    /// <c>AES-GCM</c> розшифровує одним викликом (tag у кінці, як і в нього).
    /// </summary>
    public static byte[] Seal(byte[] plain, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var outp = new byte[12 + plain.Length + 16];
        nonce.CopyTo(outp, 0);
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plain, outp.AsSpan(12, plain.Length), outp.AsSpan(12 + plain.Length, 16));
        return outp;
    }

    public static bool IsToken(string? token)
    {
        if (token is not { Length: 24 }) return false;
        foreach (var c in token) if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    // ---------------------------------------------------------------------------------------
    // кеш на диску
    // ---------------------------------------------------------------------------------------

    /// <summary>Покласти фото в кеш: зрізати метадані й атомарно записати. false — це не JPEG.</summary>
    public bool Store(string url, byte[] bytes)
    {
        var clean = GeoImage.Strip(bytes);
        if (clean is null) return false;
        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, FileNameFor(url));
        var tmp = path + "." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)) + ".tmp";
        File.WriteAllBytes(tmp, clean);
        File.Move(tmp, path, overwrite: true);
        return true;
    }

    /// <summary>Штовхнути фоновий цикл: гра попросила, а готових фото бракує.</summary>
    public void Poke()
    {
        if (!_online) return;
        try { _poke.Release(); } catch (SemaphoreFullException) { /* уже штовхнули */ }
    }

    /// <summary>
    /// Один прохід завантажувача: чого з банку нема на диску — качаємо послідовно з паузою (етикет Вікімедіа).
    /// Спершу по першому фото кожного місця, потім решту: так після першого запуску грати можна якнайшвидше.
    /// </summary>
    public async Task<int> PassAsync(CancellationToken ct)
    {
        if (_http is null) return 0;
        var got = 0;
        var queue = new List<(GeoPlace Place, int Idx)>();
        foreach (var p in Bank.Places) queue.Add((p, 0));
        foreach (var p in Bank.Places) for (var k = 1; k < p.Photos.Count; k++) queue.Add((p, k));
        foreach (var (place, idx) in queue)
        {
            ct.ThrowIfCancellationRequested();
            var photo = place.Photos[idx];
            if (_asleep.ContainsKey(photo.Url)) continue;
            if (File.Exists(PathFor(photo))) { MarkReady(place, idx, true); continue; }
            if (await FetchAsync(photo, ct))
            {
                MarkReady(place, idx, true);
                got++;
            }
            if (_pause > TimeSpan.Zero) await Task.Delay(_pause, ct);
        }
        return got;
    }

    /// <summary>Скачати одне фото. Невдачі рахуються; після трьох — одна спроба оновити адресу, далі сон до рестарту.</summary>
    public async Task<bool> FetchAsync(GeoPhoto photo, CancellationToken ct)
    {
        if (_http is null) return false;
        var url = _alias.GetValueOrDefault(photo.Url, photo.Url);
        var (ok, final) = await TryDownload(photo.Url, url, ct);
        if (ok) { _fails.TryRemove(photo.Url, out _); return true; }
        if (final) { _asleep[photo.Url] = true; return false; }
        var fails = _fails.AddOrUpdate(photo.Url, 1, (_, n) => n + 1);
        if (fails < FailsBeforeRefresh) return false;
        if (_alias.ContainsKey(photo.Url)) { _asleep[photo.Url] = true; return false; }
        var fresh = await RefreshUrl(photo.Title, ct);
        if (fresh is null || fresh == url)
        {
            _asleep[photo.Url] = true;
            _log?.LogWarning("«Де це?»: фото {Title} не качається і нової адреси нема — до рестарту не чіпаю", photo.Title);
            return false;
        }
        _alias[photo.Url] = fresh;
        (ok, _) = await TryDownload(photo.Url, fresh, ct);
        if (!ok) _asleep[photo.Url] = true;
        return ok;
    }

    /// <summary>(вдалось, безнадійно): безнадійно — не JPEG або завелике, повтор нічого не змінить.</summary>
    async Task<(bool Ok, bool Final)> TryDownload(string key, string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http!.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode != HttpStatusCode.OK)
            {
                _log?.LogWarning("«Де це?»: {Url} відповів {Code}", url, (int)resp.StatusCode);
                return (false, false);
            }
            var type = resp.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(type, "image/jpeg", StringComparison.OrdinalIgnoreCase))
            {
                _log?.LogWarning("«Де це?»: {Url} — не JPEG ({Type}), пропускаю", url, type);
                return (false, true);
            }
            if (resp.Content.Headers.ContentLength > MaxBytes)
            {
                _log?.LogWarning("«Де це?»: {Url} завелике ({Bytes} Б)", url, resp.Content.Headers.ContentLength);
                return (false, true);
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();
            var buf = new byte[81920];
            int n;
            while ((n = await stream.ReadAsync(buf, ct)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > MaxBytes)
                {
                    _log?.LogWarning("«Де це?»: {Url} завелике (понад {Mb} МБ)", url, MaxBytes / (1024 * 1024));
                    return (false, true);
                }
            }
            if (!Store(key, ms.ToArray()))
            {
                _log?.LogWarning("«Де це?»: {Url} — битий JPEG", url);
                return (false, false);
            }
            return (true, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log?.LogWarning("«Де це?»: {Url} не скачався — {Error}", url, ex.Message);
            return (false, false);
        }
    }

    /// <summary>Свіжа адреса мініатюри 1280 через API Вікісховища (файл перейменували чи переклали).</summary>
    async Task<string?> RefreshUrl(string title, CancellationToken ct)
    {
        try
        {
            var api = "https://commons.wikimedia.org/w/api.php?action=query&format=json&prop=imageinfo&iiprop=url&iiurlwidth=1280&titles="
                + Uri.EscapeDataString(title);
            using var resp = await _http!.GetAsync(api, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            foreach (var page in doc.RootElement.GetProperty("query").GetProperty("pages").EnumerateObject())
                if (page.Value.TryGetProperty("imageinfo", out var ii) && ii.GetArrayLength() > 0)
                {
                    var info = ii[0];
                    var url = info.TryGetProperty("thumburl", out var t) ? t.GetString() : info.TryGetProperty("url", out var u) ? u.GetString() : null;
                    if (url is not null && url.IndexOf('?') is var q and >= 0) url = url[..q];
                    return url is not null && GeoBank.GoodHost(url) ? url : null;
                }
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log?.LogWarning("«Де це?»: не оновив адресу {Title} — {Error}", title, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Прибирання теки: файли, яких нема в банку, — геть; недописані .tmp старші за годину — геть; понад
    /// <see cref="CacheCap"/> — спершу ті, що найдавніше віддавали гравцям.
    /// </summary>
    public int Sweep()
    {
        if (!Directory.Exists(Dir)) return 0;
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Bank.Places) foreach (var ph in p.Photos) wanted.Add(FileNameFor(ph.Url));
        var removed = 0;
        var now = DateTime.UtcNow;
        var keep = new List<FileInfo>();
        foreach (var f in new DirectoryInfo(Dir).EnumerateFiles())
        {
            if (f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                if (now - f.LastWriteTimeUtc > TimeSpan.FromHours(1) && TryDelete(f)) removed++;
                continue;
            }
            if (!wanted.Contains(f.Name)) { if (TryDelete(f)) removed++; continue; }
            keep.Add(f);
        }
        var total = keep.Sum(f => f.Length);
        if (total > CacheCap)
            foreach (var f in keep.OrderBy(f => f.LastAccessTimeUtc))
            {
                if (total <= CacheCap) break;
                var len = f.Length;
                if (TryDelete(f)) { removed++; total -= len; }
            }
        if (removed > 0) Rescan();
        return removed;
    }

    bool TryDelete(FileInfo f)
    {
        try { f.Delete(); return true; }
        catch (Exception ex) { _log?.LogWarning("«Де це?»: не видалив {File} — {Error}", f.Name, ex.Message); return false; }
    }

    /// <summary>Відмітити, що файл щойно віддали: прибиральник понад стелю бере найдавніше віддані.</summary>
    public static void Touch(string path)
    {
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch (Exception) { /* не критично */ }
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
                if (Ffmpeg is not null) await GeoShrink.AllAsync(Ffmpeg, Dir, _log, ct);
                if (got > 0 || gone > 0)
                    _log?.LogInformation("«Де це?»: фото докачано {Got}, прибрано {Gone}; готових місць {Ready} з {All}", got, gone, ReadyPlaces, Bank.Places.Count);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log?.LogWarning(ex, "«Де це?»: прохід завантажувача спіткнувся"); }
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
