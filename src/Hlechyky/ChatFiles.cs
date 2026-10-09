using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Hlechyky.Games;
using Microsoft.AspNetCore.SignalR;

namespace Hlechyky;

// =====================================================================================================================
// Файли в Балачках (05.10.2026): картинка, відео, звук чи будь-що інше до 32 МБ. Кидають лише люди з акаунтом (сайт
// відкритий, а диск — свій), лише в загальні Балачки. Файл лягає в data/chatfiles під іменем-хешем вмісту (той самий
// файл двічі — одна копія), у рядку бази — JSON ChatFile з ім'ям і розміром. Тека тримається в межах ChatFiles:MaxGb
// (10 ГБ): що понад — найстаріше стирається, а рядок лишається з «файл уже прибрано».
// Що показувати прямо в рядку, вирішують магічні байти, а не ім'я чи Content-Type браузера: картинки (JPEG, PNG, WebP,
// GIF, AVIF), відео (MP4, WebM), звук (MP3, OGG, WAV, FLAC, M4A). Решта віддається лише на скачування як
// application/octet-stream — тож HTML чи SVG під виглядом фото на нашому домені не виконається.
// =====================================================================================================================

/// <summary>Тека з файлами Балачок і скільки байтів вона може важити разом.</summary>
public sealed record ChatFilesDir(string Path, long MaxTotalBytes);

/// <summary>Що за файл — за першими байтами: вид для рядка, тип для віддачі і розміри картинки, якщо їх видно.</summary>
public sealed record ChatFileKind(string Type, string Mime, int? W = null, int? H = null)
{
    public const string Image = "image", Video = "video", Audio = "audio", Other = "file";
    public static readonly ChatFileKind Download = new(Other, "application/octet-stream");

    public bool Inline => Type != Other;

    public static ChatFileKind Sniff(ReadOnlySpan<byte> b)
    {
        if (LavkaImage.Sniff(b) is { } img)
            return img.Width > 0 && img.Height > 0 ? new(Image, img.Mime, img.Width, img.Height) : new(Image, img.Mime);
        if (LavkaImage.MimeOf(b) is { } mime) return new(Image, mime);           // JPEG, у якого кадр далі за прочитане
        if (b.Length >= 10 && (b[..6].SequenceEqual("GIF87a"u8) || b[..6].SequenceEqual("GIF89a"u8)))
            return new(Image, "image/gif", BinaryPrimitives.ReadUInt16LittleEndian(b[6..]), BinaryPrimitives.ReadUInt16LittleEndian(b[8..]));
        if (b.Length >= 12 && b.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var brand = b.Slice(8, 4);
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8)) return new(Image, "image/avif");
            if (brand.SequenceEqual("M4A "u8) || brand.SequenceEqual("M4B "u8)) return new(Audio, "audio/mp4");
            // HEIC з айфона браузери не малюють — хай краще скачають, ніж бачать розбиту картинку
            if (brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) || brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8))
                return Download;
            return new(Video, "video/mp4");                                      // isom, mp42, avc1, qt (MOV з телефона) …
        }
        if (b.Length >= 4 && b[0] == 0x1A && b[1] == 0x45 && b[2] == 0xDF && b[3] == 0xA3) return new(Video, "video/webm");
        if (b.Length >= 4 && b[..4].SequenceEqual("OggS"u8)) return new(Audio, "audio/ogg");
        if (b.Length >= 4 && b[..4].SequenceEqual("fLaC"u8)) return new(Audio, "audio/flac");
        if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b.Slice(8, 4).SequenceEqual("WAVE"u8)) return new(Audio, "audio/wav");
        if (b.Length >= 3 && b[..3].SequenceEqual("ID3"u8)) return new(Audio, "audio/mpeg");
        if (b.Length >= 2 && b[0] == 0xFF && (b[1] & 0xE6) is 0xE2 or 0xE4 or 0xE6) return new(Audio, "audio/mpeg");  // кадр MPEG, шар III/II
        return Download;
    }
}

/// <summary>Відповідь на кидок файла: вдалось — рядок, що піде всім; ні — що сказати людині.</summary>
public sealed record ChatFileReply(bool Ok, string Message, ChatMessage? Line = null);

public sealed partial class ChatFiles(Db db, ChatFlood flood, IClock clock, ChatFilesDir dir, ILogger<ChatFiles>? log = null)
{
    public const string UrlPrefix = "/api/chat/file/";
    public const long MaxBytes = 32L * 1024 * 1024;
    /// <summary>Скільки одна людина може накидати за годину — щоб ніхто не забив 10 ГБ за вечір і не витіснив чужі фото.</summary>
    public const long HourBytes = 512L * 1024 * 1024;
    public const int HourFiles = 60;
    const int HeadBytes = 64 * 1024;

    public const string NeedAccount = "Файли кидають лише ті, хто з акаунтом — зареєструй нік (тисни на свій нік угорі)";
    public const string TooBig = "Завеликий файл — до 32 МБ";
    public const string Empty = "Файл порожній";
    public const string TooMuch = "На цю годину файлів досить — дай диску перепочити";

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex HashName();

    readonly object _gate = new();
    readonly Dictionary<string, List<(DateTimeOffset At, long Bytes)>> _hour = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Прийняти файл <paramref name="body"/> від <paramref name="nick"/> з підписом <paramref name="caption"/> (може бути
    /// порожній) і, якщо треба, у відповідь на <paramref name="replyTo"/>. Рядок уже в базі; розіслати його — справа того,
    /// хто кликав (<see cref="Map"/>).
    /// </summary>
    public async Task<ChatFileReply> UploadAsync(string nick, bool isUser, string? name, string? caption, long? replyTo,
        long? declaredLength, Stream body, CancellationToken ct)
    {
        if (!isUser) return new(false, NeedAccount);
        if (declaredLength > MaxBytes) return new(false, TooBig);
        if (declaredLength == 0) return new(false, Empty);
        var now = clock.UtcNow;
        lock (_gate)
        {
            var used = Recent(nick, now);
            if (used.Count >= HourFiles || used.Sum(u => u.Bytes) + (declaredLength ?? 0) > HourBytes) return new(false, TooMuch);
        }
        var text = Cut(caption);
        if (flood.Check(nick, text, now) is { } tooFast) return new(false, tooFast);

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

            var hash = Convert.ToHexString(sha.GetHashAndReset())[..32].ToLowerInvariant();
            var kind = ChatFileKind.Sniff(head.AsSpan(0, headLen));
            var final = Path.Combine(dir.Path, hash);
            lock (_gate)
            {
                if (File.Exists(final)) File.SetLastWriteTimeUtc(final, DateTime.UtcNow);   // той самий файл уже є — свіжіший, щоб не стерся першим
                else File.Move(part, final);
                Recent(nick, now).Add((now, size));
            }
            Evict(keep: hash);

            var file = new ChatFile(hash, CleanName(name, kind), size, kind.Type, kind.W, kind.H);
            var line = db.AddChat(nick, text, "chat", replyTo: replyTo, file: file);
            log?.LogInformation("Балачки: {Nick} кинув файл {Name} ({Size} Б, {Type})", nick, file.Name, size, kind.Type);
            return new(true, "", line);
        }
        catch (IOException ex)
        {
            log?.LogWarning(ex, "Балачки: не вдалось зберегти файл від {Nick}", nick);
            return new(false, "Файл не ліг на диск — спробуй ще раз");
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { /* приберемо наступного разу */ }
        }
    }

    /// <summary>Шлях до файла за хешем; чуже ім'я чи вже стертий — null.</summary>
    public string? Resolve(string hash)
    {
        if (!HashName().IsMatch(hash)) return null;
        var path = Path.Combine(dir.Path, hash);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Стерти файл з диска — адмін прибрав репліку з ним (<see cref="ChatModeration"/>), а більше ніде його нема.</summary>
    public void Drop(string hash)
    {
        if (!HashName().IsMatch(hash)) return;
        lock (_gate)
        {
            var f = new FileInfo(Path.Combine(dir.Path, hash));
            if (f.Exists && TryDelete(f)) log?.LogInformation("Балачки: файл {Hash} стерто — адмін прибрав репліку", hash);
        }
    }

    /// <summary>Тримаємо теку в межах <see cref="ChatFilesDir.MaxTotalBytes"/>: найстаріші (за останнім кидком) — геть.</summary>
    public void Evict(string? keep = null)
    {
        lock (_gate)
        {
            if (!Directory.Exists(dir.Path)) return;
            var files = new DirectoryInfo(dir.Path).GetFiles().ToList();
            // недокачане, що лишилось від обірваного сервера
            foreach (var p in files.Where(f => f.Name.EndsWith(".part", StringComparison.Ordinal) && f.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1)))
                TryDelete(p);
            var kept = files.Where(f => HashName().IsMatch(f.Name)).OrderBy(f => f.LastWriteTimeUtc).ToList();
            var total = kept.Sum(f => f.Length);
            foreach (var f in kept)
            {
                if (total <= dir.MaxTotalBytes) break;
                if (f.Name == keep) continue;
                var len = f.Length;                     // після Delete FileInfo скидає кеш і Length кидає FileNotFound
                if (TryDelete(f)) total -= len;
            }
        }
    }

    bool TryDelete(FileInfo f)
    {
        try { f.Delete(); return true; }
        catch (IOException ex) { log?.LogWarning(ex, "Балачки: не вдалось стерти {File}", f.Name); return false; }
        catch (UnauthorizedAccessException ex) { log?.LogWarning(ex, "Балачки: не вдалось стерти {File}", f.Name); return false; }
    }

    List<(DateTimeOffset At, long Bytes)> Recent(string nick, DateTimeOffset now)
    {
        if (!_hour.TryGetValue(nick, out var list)) _hour[nick] = list = [];
        list.RemoveAll(u => now - u.At >= TimeSpan.FromHours(1));
        return list;
    }

    /// <summary>Те саме обрізання, що й у хабі: 500 символів і не посеред смайла.</summary>
    static string Cut(string? text)
    {
        var t = (text ?? "").Trim();
        return t.Length <= 500 ? t : t[..(char.IsHighSurrogate(t[499]) ? 499 : 500)];
    }

    /// <summary>Ім'я для показу й скачування: без тек і керівних символів, до 120 знаків, з розширенням.</summary>
    public static string CleanName(string? raw, ChatFileKind kind)
    {
        var s = (raw ?? "").Replace('\\', '/');
        s = s[(s.LastIndexOf('/') + 1)..];
        s = new string(s.Where(ch => !char.IsControl(ch) && ch is not ('"' or '<' or '>' or '|' or ':' or '*' or '?')).ToArray()).Trim().Trim('.');
        if (s.Length > 120)
        {
            var ext = Path.GetExtension(s);
            if (ext.Length > 12) ext = "";
            s = s[..(120 - ext.Length)].TrimEnd() + ext;
        }
        if (s.Length > 0) return s;
        return kind.Type switch
        {
            ChatFileKind.Image => "картинка." + kind.Mime[(kind.Mime.IndexOf('/') + 1)..].Replace("jpeg", "jpg"),
            ChatFileKind.Video => "відео." + (kind.Mime == "video/webm" ? "webm" : "mp4"),
            ChatFileKind.Audio => "звук",
            _ => "файл",
        };
    }

    // ---------- HTTP ----------

    public static WebApplication Map(WebApplication app)
    {
        // Тіло запиту — сам файл; ім'я, підпис і відповідь — у заголовках (encodeURIComponent: заголовки лише ASCII).
        app.MapPost("/api/chat/file", async (HttpContext c, ChatFiles files, IHubContext<RadioHub> hub, DjBrain brain, ChatModeration mod) =>
        {
            if (c.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = MaxBytes + 1;
            // Заборони адміна (🔇, 🚫, файли всім, 🐢) — ще до тіла: нема чого тягнути 32 МБ, щоб відмовити.
            var admin = Auth.IsAdmin(c);
            if (!admin && (mod.WriteRefusal(Auth.Nick(c), Auth.IsUser(c), Auth.Ip(c)) ?? mod.MediaRefusal(Auth.Nick(c), Auth.IsUser(c), Auth.Ip(c))
                    ?? mod.SlowRefusal(Auth.Nick(c))) is { } refused)
                return Results.BadRequest(new { ok = false, message = refused });
            static string? H(HttpContext c, string name)
            {
                var v = c.Request.Headers[name].ToString();
                if (v.Length == 0) return null;
                try { return Uri.UnescapeDataString(v); } catch (UriFormatException) { return null; }
            }
            long? replyTo = long.TryParse(c.Request.Headers["X-Reply-To"].ToString(), out var r) && r > 0 ? r : null;
            ChatFileReply res;
            try
            {
                res = await files.UploadAsync(Auth.Nick(c), Auth.IsUser(c), H(c, "X-File-Name"), H(c, "X-Caption"), replyTo,
                    c.Request.ContentLength, c.Request.Body, c.RequestAborted);
            }
            catch (BadHttpRequestException) { res = new(false, TooBig); }   // Kestrel обірвав тіло понад ліміт
            catch (OperationCanceledException) { return Results.Empty; }   // людина скасувала чи закрила вкладку
            if (!res.Ok || res.Line is not { } line) return Results.BadRequest(new { ok = false, message = res.Message });
            if (!admin) mod.NoteSaid(line.Nick);
            await hub.Clients.All.SendAsync("chat", line);
            if (line.Text.Length > 0) brain.OnChat(line.Nick, line.Text);
            return Results.Ok(new { ok = true, id = line.Id });
        });

        // Роздача — лише за хешем; ім'я в адресі — для скачування й щоб у вкладці було видно, що це.
        app.MapGet(UrlPrefix + "{hash}/{name}", (string hash, string name, HttpContext c, ChatFiles files) =>
        {
            if (files.Resolve(hash) is not { } path) return Results.NotFound();
            Span<byte> head = stackalloc byte[64];
            int n;
            try
            {
                using var f = File.OpenRead(path);
                n = f.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            }
            catch (IOException) { return Results.NotFound(); }     // саме стерли
            var kind = ChatFileKind.Sniff(head[..n]);
            c.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            c.Response.Headers["X-Content-Type-Options"] = "nosniff";
            c.Response.Headers["Content-Security-Policy"] = "default-src 'none'; img-src 'self'; media-src 'self'; style-src 'unsafe-inline'; sandbox";
            return kind.Inline
                ? Results.File(path, kind.Mime, enableRangeProcessing: true)
                : Results.File(path, kind.Mime, CleanName(name, kind), enableRangeProcessing: true);
        });
        return app;
    }
}
