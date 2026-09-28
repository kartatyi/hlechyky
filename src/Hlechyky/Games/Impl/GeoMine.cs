using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Games.Impl;

/// <summary>Фото друга для «Де це?»: хто, де (точка сітки мапи й координати), як назвав і що розповів.</summary>
public sealed record GeoMinePhoto(string Id, string Nick, string NickKey, int X, int Y, double Lat, double Lon,
    string Region, string Title, string Story, DateTimeOffset At, int Bytes)
{
    /// <summary>Id місця в грі й у пам'яті <see cref="GeoSeen"/>: «u:» — щоб ніколи не збігтися з банком.</summary>
    public string PlaceId => "u:" + Id;

    /// <summary>Місце для партії: категорія «friends», складність середня, фото одне — без ліцензії, автор — нік.</summary>
    public GeoPlace Place() => new(PlaceId, Title, Region, GeoMine.Cat, Lat, Lon, 2, "",
        [new GeoPhoto(Title, "mine:" + Id, Nick, "", "", "")]);
}

/// <summary>Відповідь на закидання чи видалення фото.</summary>
public sealed record GeoMineReply(bool Ok, string Message, GeoMinePhoto? Photo = null);

/// <summary>
/// «📷 Мої фото» (п. 52): друзі закидають свої знімки — і вони одразу йдуть у гру, без модерації; адмін може
/// прибрати будь-яке. Метадані — таблиця <c>geo_mine</c>, файли — <c>data/geo-mine/&lt;12 hex&gt;.jpg</c>.
/// <para>
/// Гра під замком кімнати читає лише <see cref="All"/> — знімок у пам'яті, який фоном підняли з бази на старті
/// й підміняють цілим масивом на кожну зміну. База й диск — лише в запитах (<see cref="AddAsync"/>,
/// <see cref="Delete"/>).
/// </para>
/// <para>
/// Нічому з клієнта не віримо. Браузер і так перетискає фото через canvas (це губить EXIF), але сервер
/// <b>сам</b> декодує файл ffmpeg-ом і кодує наново без метаданих (<c>-map_metadata -1</c>), а потім ще раз
/// проганяє через <see cref="GeoImage.Strip"/>. Не розкодувалось — це не фото, відмова. Розширення й MIME нічого
/// не важать: вхідний формат визначаємо за магічними байтами, і ffmpeg читає рівно цей формат і лише з файла
/// (<c>-protocol_whitelist file</c>) — жодних «плейлистів» з мережі. Координати знімка — лише з шпильки на мапі.
/// </para>
/// </summary>
public sealed class GeoMine
{
    public const string Cat = "friends";
    public const int MaxPerNick = 20;
    public const long MaxTotal = 60L * 1024 * 1024;
    /// <summary>Файл після перетискання — не більше. Типово виходить ~120–200 КБ.</summary>
    public const int MaxFile = 400 * 1024;
    /// <summary>Тіло запиту — не більше (браузер шле вже стиснене ~180 КБ; мегабайт — із запасом, але не сирий знімок).</summary>
    public const int MaxBody = 1024 * 1024;
    /// <summary>Сторона після перетискання.</summary>
    public const int Side = 1280;
    /// <summary>Сторона на вході — не більше: маленький файл не має розгортатись у гігантський кадр у ffmpeg.</summary>
    public const int MaxInSide = 6000;
    public const int MinSide = 200;
    public const int TitleMax = 60, StoryMax = 280;

    public const string NoTool = "Фото зараз не приймаються: серверу нема чим їх перевірити";
    public const string NotImage = "Це не схоже на фото: годяться JPEG, PNG чи WebP";
    public const string NotLand = "Постав шпильку туди, де знято, — на суходолі України";

    const string Schema = """
        CREATE TABLE IF NOT EXISTS geo_mine(
            id TEXT PRIMARY KEY, nick TEXT NOT NULL, nick_key TEXT NOT NULL, x INTEGER NOT NULL, y INTEGER NOT NULL,
            region TEXT NOT NULL, title TEXT NOT NULL, story TEXT NOT NULL, at TEXT NOT NULL, bytes INTEGER NOT NULL);
        """;

    readonly Db? _db;
    readonly ILogger? _log;
    readonly object _gate = new();
    readonly SemaphoreSlim _tool = new(1, 1);
    volatile GeoMinePhoto[] _all = [];

    public GeoMine(Db? db, string dir, string? ffmpeg, ILogger? log = null)
    {
        _db = db;
        Dir = dir;
        Ffmpeg = ffmpeg;
        _log = log;
        Loaded = db is null ? Task.CompletedTask : Task.Run(Load);
    }

    public string Dir { get; }
    public string? Ffmpeg { get; }
    /// <summary>Фонове читання бази на старті (тести чекають на нього).</summary>
    public Task Loaded { get; }
    /// <summary>Стеля всієї полиці (тести ставлять меншу).</summary>
    public long TotalCap { get; init; } = MaxTotal;

    /// <summary>
    /// Перетворювач «що прислали → чистий JPEG або null». Типово — ffmpeg (<see cref="ReencodeAsync"/>); тести
    /// підставляють свій.
    /// </summary>
    public Func<byte[], CancellationToken, Task<byte[]?>>? Encoder { get; init; }

    /// <summary>Усі фото друзів, старші першими. Знімок — його можна читати під будь-яким замком.</summary>
    public IReadOnlyList<GeoMinePhoto> All => _all;

    public GeoMinePhoto? Find(string? id)
    {
        if (id is null) return null;
        foreach (var p in _all) if (p.Id == id) return p;
        return null;
    }

    public List<GeoMinePhoto> Of(string nick)
    {
        var key = Auth.NickKey(nick);
        var list = new List<GeoMinePhoto>();
        foreach (var p in _all) if (p.NickKey == key) list.Add(p);
        return list;
    }

    public long TotalBytes
    {
        get { long n = 0; foreach (var p in _all) n += p.Bytes; return n; }
    }

    public string PathOf(string id) => Path.Combine(Dir, id + ".jpg");

    /// <summary>Файл фото для віддачі: лише «&lt;12 hex&gt;» з живого запису — інакше null.</summary>
    public string? Resolve(string? id) => IsId(id) && Find(id) is not null ? PathOf(id!) : null;

    static bool IsId(string? id) => id is { Length: 12 } && id.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    /// Назва чи історія: без керівних символів і «перевертачів» тексту (bidi), зайві пробіли — геть. В історії
    /// переноси рядків можна, але не більше одного порожнього рядка підряд. Обрізано до <paramref name="max"/>.
    /// </summary>
    public static string Clean(string? s, int max, bool lines)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var rows = new List<string>();
        foreach (var raw in s.Replace("\r\n", "\n").Split('\n'))
        {
            var chars = raw.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
                if (char.IsControl(chars[i]) || chars[i] is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069') or '\u200E' or '\u200F' or '\u2028' or '\u2029')
                    chars[i] = ' ';
            rows.Add(string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        }
        string t;
        if (!lines) t = string.Join(' ', rows.Where(r => r.Length > 0));
        else
        {
            var sb = new System.Text.StringBuilder();
            var blank = 0;
            foreach (var r in rows)
            {
                if (r.Length == 0) { blank++; continue; }
                if (sb.Length > 0) sb.Append(blank > 0 ? "\n\n" : "\n");
                sb.Append(r);
                blank = 0;
            }
            t = sb.ToString();
        }
        if (t.Length > max) t = t[..max].TrimEnd();
        return t;
    }

    /// <summary>Назва області з мапи за точкою сітки; null — море, закордон чи мапи нема.</summary>
    public static string? RegionName(int x, int y)
    {
        if (GeoMap.File is not { } map || map.RegionAt(x, y) is not { } id) return null;
        foreach (var r in map.Regions) if (r.Id == id) return r.Name;
        return null;
    }

    // ---------------------------------------------------------------------------------------
    // закинути
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Закинути фото. <paramref name="body"/> — тіло запиту як є (≤ <see cref="MaxBody"/> + 1). Нік — уже
    /// перевірений викликачем (гостям сюди не можна). Кидає лише на скасування.
    /// </summary>
    public async Task<GeoMineReply> AddAsync(string nick, byte[] body, int x, int y, string? title, string? story,
        DateTimeOffset now, CancellationToken ct)
    {
        var key = Auth.NickKey(nick);
        if (body.Length == 0) return new(false, "Фото не дійшло — обери ще раз");
        if (body.Length > MaxBody) return new(false, $"Завелике фото: до {MaxBody / 1024} КБ");
        if (!GeoMap.Inside(x, y) || RegionName(x, y) is not { } region) return new(false, NotLand);
        var name = Clean(title, TitleMax, lines: false);
        if (name.Length < 2) return new(false, "Назви місце — хоч двома літерами");
        var tale = Clean(story, StoryMax, lines: true);
        if (Quota(key) is { } full) return new(false, full);
        var img = LavkaImage.Sniff(body);
        if (img is null) return new(false, NotImage);
        if (img.Width > MaxInSide || img.Height > MaxInSide) return new(false, $"Завелика картинка: до {MaxInSide}×{MaxInSide}");
        if (Math.Min(img.Width, img.Height) < MinSide) return new(false, $"Замале фото: хоч {MinSide} пікселів з меншого боку");

        var encoder = Encoder ?? (Ffmpeg is not null && File.Exists(Ffmpeg) ? (b, t) => ReencodeAsync(Ffmpeg, Dir, b, t) : null);
        if (encoder is null) return new(false, NoTool);
        byte[]? got;
        // Один ffmpeg за раз на весь сайт: п'ятеро друзів разом не мають покласти процесор проду.
        if (!await _tool.WaitAsync(TimeSpan.FromSeconds(30), ct)) return new(false, "Сервер зайнятий іншими фото — спробуй за хвилину");
        try { got = await encoder(body, ct); }
        finally { _tool.Release(); }
        // Друга лінія: що б не вийшло з перетискання — метадані геть; не JPEG — не фото.
        var clean = got is null ? null : GeoImage.Strip(got);
        if (clean is null || LavkaImage.Sniff(clean) is not { Mime: "image/jpeg" } outImg || Math.Min(outImg.Width, outImg.Height) < 32)
            return new(false, NotImage);
        if (clean.Length > MaxFile) return new(false, "Фото не стислось до розумного розміру — спробуй інше");

        var (lat, lon) = GeoMap.Unproject(x, y);
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var photo = new GeoMinePhoto(id, Auth.CleanNick(nick), key, x, y, Math.Round(lat, 5), Math.Round(lon, 5), region, name, tale,
            now, clean.Length);
        Directory.CreateDirectory(Dir);
        var path = PathOf(id);
        var tmp = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tmp, clean, ct);
            lock (_gate)
            {
                // Ліміти ще раз — уже під замком: два паралельні закидання не проскочать удвох в останнє місце.
                if (Quota(key) is { } late) { TryDelete(tmp); return new(false, late); }
                File.Move(tmp, path, overwrite: false);
                _all = [.. _all, photo];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tmp);
            _log?.LogWarning("«Де це?»: не зберіг фото друга — {Error}", ex.Message);
            return new(false, "Не вдалось зберегти — спробуй ще раз");
        }
        Write(photo);
        return new(true, "📸 Фото в грі! Друзі шукатимуть його за столом з опцією «Фото друзів»", photo);
    }

    string? Quota(string key)
    {
        var mine = 0;
        long total = 0;
        foreach (var p in _all)
        {
            if (p.NickKey == key) mine++;
            total += p.Bytes;
        }
        if (mine >= MaxPerNick) return $"У тебе вже {MaxPerNick} фото — прибери якесь старе, щоб закинути нове";
        if (total + MaxFile > TotalCap) return "Полиця фото друзів повна — хай адмін чи автори приберуть старі";
        return null;
    }

    /// <summary>
    /// ffmpeg: розкодувати рівно той формат, що в магічних байтах, лише з локального файла; зменшити до
    /// <see cref="Side"/> з більшого боку; закодувати JPEG без метаданих. Спершу q 5, завелике — q 9. Один потік,
    /// нижчий пріоритет, 20 с на все. null — не розкодувалось.
    /// </summary>
    public static async Task<byte[]?> ReencodeAsync(string ffmpeg, string dir, byte[] body, CancellationToken ct)
    {
        var fmt = LavkaImage.MimeOf(body) switch
        {
            "image/jpeg" => "jpeg_pipe",
            "image/png" => "png_pipe",
            "image/webp" => "webp_pipe",
            _ => null,
        };
        if (fmt is null) return null;
        Directory.CreateDirectory(dir);
        var stem = Path.Combine(dir, "in-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
        var src = stem + ".bin";
        var dst = stem + ".out.jpg";
        try
        {
            await File.WriteAllBytesAsync(src, body, ct);
            foreach (var q in new[] { "5", "9" })
            {
                if (!await RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-threads", "1",
                        "-protocol_whitelist", "file", "-f", fmt, "-i", src, "-frames:v", "1", "-map_metadata", "-1",
                        "-vf", $"scale='min({Side},iw)':'min({Side},ih)':force_original_aspect_ratio=decrease",
                        "-pix_fmt", "yuvj420p", "-q:v", q, "-f", "mjpeg", dst], ct)) return null;
                var bytes = await File.ReadAllBytesAsync(dst, ct);
                if (bytes.Length > 230 * 1024 && q == "5") continue;
                return bytes;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally
        {
            TryDelete(src);
            TryDelete(dst);
        }
    }

    static async Task<bool> RunAsync(string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return false;
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { /* уже вийшов — не біда */ }
        var err = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            return false;
        }
        try { await err; } catch (OperationCanceledException) { }
        return p.ExitCode == 0;
    }

    // ---------------------------------------------------------------------------------------
    // прибрати
    // ---------------------------------------------------------------------------------------

    /// <summary>Прибрати фото: автор — своє, адмін — будь-яке. Файл і рядок бази — геть одразу.</summary>
    public GeoMineReply Delete(string? id, string nick, bool admin)
    {
        GeoMinePhoto? gone;
        lock (_gate)
        {
            gone = Find(id);
            if (gone is null) return new(false, "Такого фото вже нема");
            if (!admin && gone.NickKey != Auth.NickKey(nick)) return new(false, "Це не твоє фото");
            _all = [.. _all.Where(p => p.Id != gone.Id)];
        }
        TryDelete(PathOf(gone.Id));
        if (_db is not null)
            try
            {
                _db.With(c =>
                {
                    Ensure(c);
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "DELETE FROM geo_mine WHERE id = $id";
                    cmd.Parameters.AddWithValue("$id", gone.Id);
                    cmd.ExecuteNonQuery();
                });
            }
            catch (Exception ex) { _log?.LogWarning("«Де це?»: не стер рядок фото друга — {Error}", ex.Message); }
        return new(true, admin && gone.NickKey != Auth.NickKey(nick) ? $"Прибрано фото {gone.Nick}" : "Фото прибрано", gone);
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { /* прибиральник не мусить падати */ }
    }

    // ---------------------------------------------------------------------------------------
    // база
    // ---------------------------------------------------------------------------------------

    void Load()
    {
        try
        {
            var list = new List<GeoMinePhoto>();
            _db!.With(c =>
            {
                Ensure(c);
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT id, nick, nick_key, x, y, region, title, story, at, bytes FROM geo_mine ORDER BY at";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var id = r.GetString(0);
                    // запис без файла (хтось почистив теку) у гру не йде: «битий» раунд гірший за відсутній
                    if (!IsId(id) || !File.Exists(PathOf(id))) continue;
                    int x = r.GetInt32(3), y = r.GetInt32(4);
                    var (lat, lon) = GeoMap.Unproject(x, y);
                    list.Add(new GeoMinePhoto(id, r.GetString(1), r.GetString(2), x, y, Math.Round(lat, 5), Math.Round(lon, 5),
                        r.GetString(5), r.GetString(6), r.GetString(7),
                        DateTimeOffset.Parse(r.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), r.GetInt32(9)));
                }
                return 0;
            });
            lock (_gate)
            {
                // закинуте, поки читали базу, не губимо
                var have = list.Select(p => p.Id).ToHashSet();
                _all = [.. list, .. _all.Where(p => !have.Contains(p.Id))];
            }
        }
        catch (Exception ex) { _log?.LogWarning("«Де це?»: не прочитав фото друзів — {Error}", ex.Message); }
    }

    void Write(GeoMinePhoto p)
    {
        if (_db is null) return;
        try
        {
            _db.With(c =>
            {
                Ensure(c);
                using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    INSERT OR REPLACE INTO geo_mine(id, nick, nick_key, x, y, region, title, story, at, bytes)
                    VALUES($id, $nick, $key, $x, $y, $region, $title, $story, $at, $bytes)
                    """;
                cmd.Parameters.AddWithValue("$id", p.Id);
                cmd.Parameters.AddWithValue("$nick", p.Nick);
                cmd.Parameters.AddWithValue("$key", p.NickKey);
                cmd.Parameters.AddWithValue("$x", p.X);
                cmd.Parameters.AddWithValue("$y", p.Y);
                cmd.Parameters.AddWithValue("$region", p.Region);
                cmd.Parameters.AddWithValue("$title", p.Title);
                cmd.Parameters.AddWithValue("$story", p.Story);
                cmd.Parameters.AddWithValue("$at", p.At.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$bytes", p.Bytes);
                cmd.ExecuteNonQuery();
            });
        }
        catch (Exception ex) { _log?.LogWarning("«Де це?»: не записав фото друга в базу — {Error}", ex.Message); }
    }

    volatile bool _ready;

    void Ensure(SqliteConnection c)
    {
        if (_ready) return;
        using var cmd = c.CreateCommand();
        cmd.CommandText = Schema;
        cmd.ExecuteNonQuery();
        _ready = true;
    }
}
