using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hlechyky.Games;

namespace Hlechyky;

// =====================================================================================================================
// «Своя фотка» (записка Назара, 28.09.2026): вміння з Лавки за 2000 🏺 назавжди — ставити своє фото замість літери чи
// значка. Обрізає й стискає браузер (256×256, WebP або JPEG, ~20–80 КБ), сервер лише перевіряє й кладе файлом у
// data/avatars: нових бібліотек і перекодування тут нема, тож і довіри до вмісту — рівно стільки, скільки дають магічні
// байти, розмір і розміри в заголовку. Міняти — безкоштовно, але не частіше разу на добу; прибрати — будь-коли;
// адмін може зняти будь-яке фото (людина лишається власником вміння й може одразу поставити інше).
// =====================================================================================================================

/// <summary>Тека з фотками — <c>data/avatars</c> (у тестах — тимчасова).</summary>
public sealed record LavkaPhotoDir(string Path);

/// <summary>
/// Що за картинка — за магічними байтами, а не за тим, що про себе каже браузер чи ім'я файла: JPEG, PNG або WebP
/// і розміри з заголовка. Решта (GIF, SVG, HTML під чужим розширенням) — null.
/// </summary>
public sealed record LavkaImage(string Mime, string Ext, int Width, int Height)
{
    static ReadOnlySpan<byte> Png => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Лише тип — для віддачі файла: розміри тут ні до чого, досить початку.</summary>
    public static string? MimeOf(ReadOnlySpan<byte> b) =>
        b.Length >= 8 && b[..8].SequenceEqual(Png) ? "image/png"
        : b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b.Slice(8, 4).SequenceEqual("WEBP"u8) ? "image/webp"
        : b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? "image/jpeg"
        : null;

    public static LavkaImage? Sniff(ReadOnlySpan<byte> b) => MimeOf(b) switch
    {
        "image/png" => b.Length >= 24 && b.Slice(12, 4).SequenceEqual("IHDR"u8)
            ? new("image/png", "png", Side(BinaryPrimitives.ReadUInt32BigEndian(b[16..])), Side(BinaryPrimitives.ReadUInt32BigEndian(b[20..])))
            : null,
        "image/webp" => WebP(b),
        "image/jpeg" => Jpeg(b),
        _ => null,
    };

    /// <summary>Розмір сторони; нереальні (понад мільйон) — нулем, і таке фото не пройде перевірку.</summary>
    static int Side(uint v) => v is > 0 and <= 1_000_000 ? (int)v : 0;

    static LavkaImage? WebP(ReadOnlySpan<byte> b)
    {
        if (b.Length < 30) return null;
        var chunk = b.Slice(12, 4);
        // VP8 (стиснення з втратами): ключовий кадр із «9D 01 2A», далі ширина й висота по 14 біт
        if (chunk.SequenceEqual("VP8 "u8) && b[23] == 0x9D && b[24] == 0x01 && b[25] == 0x2A)
            return new("image/webp", "webp", BinaryPrimitives.ReadUInt16LittleEndian(b[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(b[28..]) & 0x3FFF);
        // VP8L (без втрат): підпис 0x2F, далі (ширина−1) і (висота−1) по 14 біт
        if (chunk.SequenceEqual("VP8L"u8) && b[20] == 0x2F)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(b[21..]);
            return new("image/webp", "webp", (int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }
        // VP8X (розширений): полотно (ширина−1) і (висота−1) по 24 біти
        if (chunk.SequenceEqual("VP8X"u8))
            return new("image/webp", "webp", (b[24] | b[25] << 8 | b[26] << 16) + 1, (b[27] | b[28] << 8 | b[29] << 16) + 1);
        return null;
    }

    /// <summary>JPEG: ідемо сегментами до кадру (SOF0…SOF15, крім DHT/JPG/DAC) — там висота й ширина.</summary>
    static LavkaImage? Jpeg(ReadOnlySpan<byte> b)
    {
        var i = 2;
        while (i + 4 <= b.Length)
        {
            if (b[i] != 0xFF) return null;
            var m = b[i + 1];
            if (m == 0xFF) { i++; continue; }                                  // заповнювач
            if (m is 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }       // маркери без довжини
            if (m is 0xD9 or 0xDA) return null;                                // кінець чи дані — а кадру так і не було
            var len = BinaryPrimitives.ReadUInt16BigEndian(b[(i + 2)..]);
            if (len < 2) return null;
            if (m is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                if (i + 9 > b.Length) return null;
                return new("image/jpeg", "jpg", BinaryPrimitives.ReadUInt16BigEndian(b[(i + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(b[(i + 5)..]));
            }
            i += 2 + len;
        }
        return null;
    }
}

/// <summary>Відповідь на дію з фото: чи вдалось, що сказати людині, адреса фото тепер і коли можна поставити нове.</summary>
public sealed record LavkaPhotoReply(bool Ok, string Message, string? Url = null, DateTimeOffset? ReadyAt = null);

/// <summary>Правила своєї фотки: хто може поставити, що саме, як часто; файли на диску; адмінський перегляд і «Зняти».</summary>
public sealed partial class LavkaPhotos(LavkaStore store, Lavka lavka, ILavkaWire wire, IClock clock, LavkaPhotoDir dir,
    ILogger<LavkaPhotos> log)
{
    public const string UrlPrefix = "/api/lavka/photo/";
    /// <summary>Браузер шле 256×256 на 20–80 КБ; 200 КБ — з запасом на «важке» фото, але не на сирий знімок з камери.</summary>
    public const int MaxBytes = 200 * 1024;
    /// <summary>Більше сторону не пускаємо: маленький файл може розгорнутись у гігантську картинку в браузері кожного.</summary>
    public const int MaxSide = 1024;

    public const string NotOwned = "Своя фотка — вміння з Лавки: спершу купи його";
    public const string Empty = "Фото не дійшло — обери ще раз";
    public const string NotImage = "Це не схоже на фото: годяться JPEG, PNG чи WebP";
    public const string Saved = "Фото стоїть — його вже бачать усі";

    /// <summary>Ім'я файла: хеш ніка (кирилиця й пробіли в імена файлів не йдуть) + версія вмісту + тип.</summary>
    [GeneratedRegex("^[0-9a-f]{16}-[0-9a-f]{12}\\.(jpg|png|webp)$")]
    private static partial Regex FileName();

    readonly object _gate = new();

    static string Hex(ReadOnlySpan<byte> data, int chars) => Convert.ToHexString(SHA256.HashData(data))[..chars].ToLowerInvariant();

    /// <summary>
    /// Поставити фото (<paramref name="bytes"/> — тіло запиту як є, не довше за <see cref="MaxBytes"/> + 1). Перше після
    /// купівлі й перше після того, як адмін зняв, — одразу; далі — не частіше разу на добу.
    /// </summary>
    public LavkaPhotoReply Set(string nick, bool account, byte[] bytes)
    {
        if (!account) return new(false, Lavka.NotAccount);
        var now = clock.UtcNow;
        string file;
        string? old;
        lock (_gate)
        {
            if (!store.Owns(nick, LavkaCatalog.Photo)) return new(false, NotOwned);
            if (lavka.ReadyAt(nick, LavkaCatalog.Photo) is { } ready)
                return new(false, $"Нове фото — через {Lavka.Left(ready - now)}", store.Photo(nick)?.Url, ready);
            if (bytes.Length == 0) return new(false, Empty);
            if (bytes.Length > MaxBytes) return new(false, $"Завелике фото: до {MaxBytes / 1024} КБ");
            if (LavkaImage.Sniff(bytes) is not { Width: > 0, Height: > 0 } img) return new(false, NotImage);
            if (img.Width > MaxSide || img.Height > MaxSide)
                return new(false, $"Завелика картинка: до {MaxSide}×{MaxSide}");

            file = $"{Hex(Encoding.UTF8.GetBytes(Auth.NickKey(nick)), 16)}-{Hex(bytes, 12)}.{img.Ext}";
            try
            {
                Directory.CreateDirectory(dir.Path);
                var path = Path.Combine(dir.Path, file);
                // Те саме фото вдруге — той самий файл: не переписуємо, його саме може хтось тягнути.
                if (!File.Exists(path))
                {
                    // спершу поруч, потім одним рухом: хто відкриє адресу посеред запису, не отримає пів файла
                    var tmp = path + ".tmp";
                    File.WriteAllBytes(tmp, bytes);
                    File.Move(tmp, path, overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning(ex, "фото {Nick} не лягло на диск", nick);
                return new(false, "Фото не збереглось — спробуй ще раз трохи згодом");
            }
            old = store.SetPhoto(nick, file, bytes.Length, now);
            store.UsePerk(nick, LavkaCatalog.Photo, now);
        }
        if (old is not null && old != file) Delete(old);
        lavka.Announce(nick);
        return new(true, Saved, UrlPrefix + file, lavka.ReadyAt(nick, LavkaCatalog.Photo));
    }

    /// <summary>Прибрати своє фото — повернутись до значка чи літери. Без обмежень; перерва на нове фото при цьому не скидається.</summary>
    public LavkaPhotoReply Remove(string nick, bool account)
    {
        if (!account) return new(false, Lavka.NotAccount);
        string? old;
        lock (_gate) old = store.DropPhoto(nick);
        if (old is null) return new(false, "Фото й так нема");
        Delete(old);
        lavka.Announce(nick);
        return new(true, "Фото прибрано — знову значок", null, lavka.ReadyAt(nick, LavkaCatalog.Photo));
    }

    /// <summary>Усі поставлені фото — для адміна, свіжі згори.</summary>
    public List<LavkaPhotoRow> All() => store.AllPhotos();

    /// <summary>
    /// Адмін знімає фото: файл — геть, вміння лишається за людиною, і нове фото можна ставити одразу (перерва скинута) —
    /// зняли не за «часто міняв», а за те, що саме на фото.
    /// </summary>
    public LavkaPhotoReply TakeDown(string? nick)
    {
        var name = Auth.CleanNick((nick ?? "").Trim().TrimStart('@'));
        if (name.Length == 0) return new(false, "Чиє фото зняти?");
        LavkaPhotoRow? row;
        lock (_gate)
        {
            row = store.Photo(name);
            if (row is null)
            {
                var of = NickCases.Genitive(name);
                return new(false, $"{NickCases.AtStart(of)} {of} фото нема");
            }
            store.DropPhoto(row.Nick);
            store.ForgetPerk(row.Nick, LavkaCatalog.Photo);
        }
        Delete(row.File);
        lavka.Announce(row.Nick);
        try { wire.Toast(row.Nick, "📷 Розробник зняв твоє фото — можна одразу поставити інше"); }
        catch (Exception ex) { log.LogWarning(ex, "не сказали {Nick}, що фото знято", row.Nick); }
        return new(true, $"Фото {NickCases.Genitive(row.Nick)} знято");
    }

    /// <summary>Шлях до файла фото за іменем з адреси; null — ім'я не наше (жодного «..», жодних чужих файлів) або файла нема.</summary>
    public string? Resolve(string? file)
    {
        if (string.IsNullOrEmpty(file) || !FileName().IsMatch(file)) return null;
        var path = Path.Combine(dir.Path, file);
        return File.Exists(path) ? path : null;
    }

    void Delete(string file)
    {
        if (!FileName().IsMatch(file)) return;
        try { File.Delete(Path.Combine(dir.Path, file)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "старе фото {File} не прибралось з диска", file);
        }
    }
}
