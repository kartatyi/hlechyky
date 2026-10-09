using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Hlechyky.Games;

namespace Hlechyky;

// =====================================================================================================================
// Файли в записках «💡 Розробнику» (09.10.2026): скрін бага, запис екрана, будь-що до 10 МБ. Кидають усі, хто може
// писати записки (з ніком, гостя теж), — і в нову записку, і в переписку, з обох боків. Файл вантажиться одразу, як його
// вибрали, і «чекає» (FeedbackFile.FeedbackId = null); із запискою чи повідомленням летять лише id. Те, що не дочекалось
// доби, прибирається. На диску — data/feedbackfiles під іменем-хешем вмісту (той самий скрін двічі — одна копія), тека
// тримається в межах FeedbackFiles:MaxGb. Видно файл лише за адресою з випадковим ключем — її бачать автор і розробник:
// записки не публічні, як Балачки, а гість на сервері — лише заголовок X-Nick, тож перевіряти «чий» нема чим.
// Що показати прямо в записці, а що лише дати скачати — за магічними байтами, як у Балачках (ChatFileKind).
// =====================================================================================================================

/// <summary>Тека з файлами записок і скільки байтів вона може важити разом.</summary>
public sealed record FeedbackFilesDir(string Path, long MaxTotalBytes);

public sealed partial class FeedbackFiles(FeedbackStore store, IClock clock, FeedbackFilesDir dir, ILogger<FeedbackFiles>? log = null)
{
    public const long MaxBytes = 10L * 1024 * 1024;
    /// <summary>Скільки один нік може накидати за годину: на кілька записів екрана досить, а диск не забити.</summary>
    public const long HourBytes = 100L * 1024 * 1024;
    public const int HourFiles = 30;
    /// <summary>Скільки файл чекає на записку, поки його не приберуть.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromDays(1);
    const int HeadBytes = 64 * 1024;

    public const string Nameless = "Спершу назвись — тоді й файл прикріпиш";
    public const string TooBig = "Завеликий файл — до 10 МБ";
    public const string Empty = "Файл порожній";
    public const string TooMuch = "На цю годину файлів досить — решту прикріпиш трохи згодом";

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex HashName();

    readonly object _gate = new();

    public sealed record Reply(bool Ok, string Message, FeedbackFile? File = null);

    /// <summary>Прийняти файл <paramref name="body"/> від <paramref name="nick"/>; він чекатиме на записку.</summary>
    public async Task<Reply> UploadAsync(string nick, string? name, long? declaredLength, Stream body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(nick) || Auth.NickKey(nick) == Auth.Guest) return new(false, Nameless);
        if (declaredLength > MaxBytes) return new(false, TooBig);
        if (declaredLength == 0) return new(false, Empty);
        var now = clock.UtcNow;
        var (count, bytes) = store.FilesSince(nick, now.AddHours(-1));
        if (count >= HourFiles || bytes + (declaredLength ?? 0) > HourBytes) return new(false, TooMuch);

        Sweep(now);
        Directory.CreateDirectory(dir.Path);
        var part = Path.Combine(dir.Path, $".up-{Guid.NewGuid():N}.part");
        try
        {
            long size = 0;
            var head = new byte[HeadBytes];
            var headLen = 0;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var f = File.Create(part))
            {
                var buf = new byte[81920];
                int n;
                while ((n = await body.ReadAsync(buf, ct)) > 0)
                {
                    size += n;
                    if (size > MaxBytes) return new(false, TooBig);
                    if (headLen < HeadBytes)
                    {
                        var take = Math.Min(n, HeadBytes - headLen);
                        Array.Copy(buf, 0, head, headLen, take);
                        headLen += take;
                    }
                    sha.AppendData(buf, 0, n);
                    await f.WriteAsync(buf.AsMemory(0, n), ct);
                }
            }
            if (size == 0) return new(false, Empty);
            if (bytes + size > HourBytes) return new(false, TooMuch);   // довжину не сказали — рахуємо за фактом

            var hash = Convert.ToHexString(sha.GetHashAndReset())[..32].ToLowerInvariant();
            var kind = ChatFileKind.Sniff(head.AsSpan(0, headLen));
            var final = Path.Combine(dir.Path, hash);
            lock (_gate)
            {
                if (File.Exists(final)) File.SetLastWriteTimeUtc(final, DateTime.UtcNow);   // свіжіший — щоб не стерся першим
                else File.Move(part, final);
            }
            Evict(keep: hash);

            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            var file = store.AddFile(nick, hash, key, ChatFiles.CleanName(name, kind), size, kind.Type, kind.W, kind.H, now);
            log?.LogInformation("Записки: {Nick} прикріпив {Name} ({Size} Б, {Type})", nick, file.Name, size, kind.Type);
            return new(true, "", file);
        }
        catch (IOException ex)
        {
            log?.LogWarning(ex, "Записки: не вдалось зберегти файл від {Nick}", nick);
            return new(false, "Файл не ліг на диск — спробуй ще раз");
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { /* приберемо наступного разу */ }
        }
    }

    /// <summary>Файл і шлях до нього за id і ключем з адреси; не той ключ чи вже стертий — null.</summary>
    public (FeedbackFile File, string Path)? Resolve(long id, string key)
    {
        if (store.File(id) is not { } f || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(f.Key), System.Text.Encoding.ASCII.GetBytes(key ?? "")))
            return null;
        if (!HashName().IsMatch(f.Hash)) return null;
        var path = Path.Combine(dir.Path, f.Hash);
        return File.Exists(path) ? (f, path) : null;
    }

    /// <summary>Файли, що чекали понад добу, — з бази, а ті, на які більше ніщо не посилається, — і з диска.</summary>
    public void Sweep(DateTimeOffset now)
    {
        foreach (var hash in store.DropStale(now - Wait))
        {
            if (!HashName().IsMatch(hash)) continue;
            lock (_gate) TryDelete(new FileInfo(Path.Combine(dir.Path, hash)));
        }
    }

    /// <summary>Тримаємо теку в межах <see cref="FeedbackFilesDir.MaxTotalBytes"/>: найстаріші — геть, рядок лишається.</summary>
    public void Evict(string? keep = null)
    {
        lock (_gate)
        {
            if (!Directory.Exists(dir.Path)) return;
            var files = new DirectoryInfo(dir.Path).GetFiles().ToList();
            foreach (var p in files.Where(f => f.Name.EndsWith(".part", StringComparison.Ordinal) && f.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1)))
                TryDelete(p);
            var kept = files.Where(f => HashName().IsMatch(f.Name)).OrderBy(f => f.LastWriteTimeUtc).ToList();
            var total = kept.Sum(f => f.Length);
            foreach (var f in kept)
            {
                if (total <= dir.MaxTotalBytes) break;
                if (f.Name == keep) continue;
                var len = f.Length;
                if (TryDelete(f)) total -= len;
            }
        }
    }

    bool TryDelete(FileInfo f)
    {
        try { if (f.Exists) f.Delete(); return true; }
        catch (IOException ex) { log?.LogWarning(ex, "Записки: не вдалось стерти {File}", f.Name); return false; }
        catch (UnauthorizedAccessException ex) { log?.LogWarning(ex, "Записки: не вдалось стерти {File}", f.Name); return false; }
    }

    // ---------- HTTP ----------

    public static WebApplication Map(WebApplication app)
    {
        // Тіло — сам файл, ім'я — у заголовку X-File-Name (encodeURIComponent: заголовки лише ASCII).
        app.MapPost("/api/feedback/file", async (HttpContext c, FeedbackFiles files) =>
        {
            if (c.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = MaxBytes + 1;
            var raw = c.Request.Headers["X-File-Name"].ToString();
            string? name;
            try { name = raw.Length == 0 ? null : Uri.UnescapeDataString(raw); } catch (UriFormatException) { name = null; }
            Reply r;
            try { r = await files.UploadAsync(Auth.Nick(c), name, c.Request.ContentLength, c.Request.Body, c.RequestAborted); }
            catch (BadHttpRequestException) { r = new(false, TooBig); }   // Kestrel обірвав тіло понад ліміт
            catch (OperationCanceledException) { return Results.Empty; }  // людина прибрала файл чи закрила вкладку
            if (!r.Ok || r.File is not { } f) return Results.BadRequest(new { ok = false, message = r.Message });
            return Results.Ok(new { ok = true, file = new { id = f.Id, name = f.Name, size = f.Size, type = f.Type, w = f.W, h = f.H, url = f.Url } });
        });

        // Роздача — за id і ключем; ім'я в адресі — для скачування й щоб у вкладці було видно, що це.
        app.MapGet(FeedbackFile.UrlPrefix + "{id:long}/{key}/{name}", (long id, string key, string name, HttpContext c, FeedbackFiles files) =>
        {
            if (files.Resolve(id, key) is not var (file, path)) return Results.NotFound();
            Span<byte> head = stackalloc byte[64];
            int n;
            try
            {
                using var f = File.OpenRead(path);
                n = f.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            }
            catch (IOException) { return Results.NotFound(); }
            var kind = ChatFileKind.Sniff(head[..n]);
            c.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            c.Response.Headers["X-Content-Type-Options"] = "nosniff";
            c.Response.Headers["Content-Security-Policy"] = "default-src 'none'; img-src 'self'; media-src 'self'; style-src 'unsafe-inline'; sandbox";
            c.Response.Headers["X-Robots-Tag"] = "noindex";
            return kind.Inline
                ? Results.File(path, kind.Mime, enableRangeProcessing: true)
                : Results.File(path, kind.Mime, file.Name, enableRangeProcessing: true);
        });
        return app;
    }
}
