using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hlechyky.Games;

namespace Hlechyky;

// =====================================================================================================================
// «Своє тло» (записка #49 від Smaug, 14.10.2026): річ полиці тла за 2500 🏺 назавжди — своя картинка-банер у шапці
// профілю замість візерунка. Усе — як у «Своєї фотки» (LavkaPhotos.cs): обрізає й стискає браузер (банер 3:1, 1200×400,
// WebP або JPEG, ~100–300 КБ), сервер лише перевіряє магічні байти, розмір і розміри та кладе файлом у data/backgrounds.
// Міняти — безкоштовно, але не частіше разу на добу; прибрати — будь-коли; адмін знімає вже після (вміння лишається).
// Нова картинка одразу вдягається в слот тла: вдягнув потім візерунок — своє знялось (файл лишається, можна вдягти назад).
// =====================================================================================================================

/// <summary>Тека зі своїми тлами — <c>data/backgrounds</c> (у тестах — тимчасова).</summary>
public sealed record LavkaBgDir(string Path);

/// <summary>Правила свого тла: хто може поставити, що саме, як часто; файли на диску; адмінський перегляд і «Зняти».</summary>
public sealed partial class LavkaBackgrounds(LavkaStore store, Lavka lavka, ILavkaWire wire, IClock clock, LavkaBgDir dir,
    ILogger<LavkaBackgrounds> log)
{
    public const string UrlPrefix = "/api/lavka/bg/";
    /// <summary>Браузер шле 1200×400 на 100–300 КБ; 400 КБ — з запасом, але не на сирий знімок з камери.</summary>
    public const int MaxBytes = 400 * 1024;
    /// <summary>Більше сторону не пускаємо: маленький файл може розгорнутись у гігантську картинку в браузері кожного.</summary>
    public const int MaxSide = 2000;

    public const string NotOwned = "Своє тло — з Лавки: спершу купи його";
    public const string Empty = "Картинка не дійшла — обери ще раз";
    public const string NotImage = "Це не схоже на картинку: годяться JPEG, PNG чи WebP";
    public const string Saved = "Тло стоїть — його вже бачать усі";

    /// <summary>Ім'я файла: хеш ніка (кирилиця й пробіли в імена файлів не йдуть) + версія вмісту + тип.</summary>
    [GeneratedRegex("^[0-9a-f]{16}-[0-9a-f]{12}\\.(jpg|png|webp)$")]
    private static partial Regex FileName();

    readonly object _gate = new();

    static string Hex(ReadOnlySpan<byte> data, int chars) => Convert.ToHexString(SHA256.HashData(data))[..chars].ToLowerInvariant();

    /// <summary>
    /// Поставити тло (<paramref name="bytes"/> — тіло запиту як є, не довше за <see cref="MaxBytes"/> + 1) і одразу вдягти
    /// його. Перше після купівлі й перше після того, як адмін зняв, — одразу; далі — не частіше разу на добу.
    /// </summary>
    public LavkaPhotoReply Set(string nick, bool account, byte[] bytes)
    {
        if (!account) return new(false, Lavka.NotAccount);
        var now = clock.UtcNow;
        string file;
        string? old;
        lock (_gate)
        {
            if (!store.Owns(nick, LavkaCatalog.OwnBg)) return new(false, NotOwned);
            if (lavka.ReadyAt(nick, LavkaCatalog.OwnBg) is { } ready)
                return new(false, $"Нове тло — через {Lavka.Left(ready - now)}", store.BgPhoto(nick)?.Url, ready);
            if (bytes.Length == 0) return new(false, Empty);
            if (bytes.Length > MaxBytes) return new(false, $"Завеликий файл: до {MaxBytes / 1024} КБ");
            if (LavkaImage.Sniff(bytes) is not { Width: > 0, Height: > 0 } img) return new(false, NotImage);
            if (img.Width > MaxSide || img.Height > MaxSide)
                return new(false, $"Завелика картинка: до {MaxSide}×{MaxSide}");

            file = $"{Hex(Encoding.UTF8.GetBytes(Auth.NickKey(nick)), 16)}-{Hex(bytes, 12)}.{img.Ext}";
            try
            {
                Directory.CreateDirectory(dir.Path);
                var path = Path.Combine(dir.Path, file);
                // Та сама картинка вдруге — той самий файл: не переписуємо, його саме може хтось тягнути.
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
                log.LogWarning(ex, "тло {Nick} не лягло на диск", nick);
                return new(false, "Тло не збереглось — спробуй ще раз трохи згодом");
            }
            old = store.SetBgPhoto(nick, file, bytes.Length, now);
            store.UsePerk(nick, LavkaCatalog.OwnBg, now);
            // поставив — значить, хоче бачити: своє тло стає на місце візерунка
            store.Wear(nick, LavkaKind.Bg, LavkaCatalog.OwnBg);
        }
        if (old is not null && old != file) Delete(old);
        lavka.Announce(nick);
        return new(true, Saved, UrlPrefix + file, lavka.ReadyAt(nick, LavkaCatalog.OwnBg));
    }

    /// <summary>
    /// Прибрати своє тло: файл — геть, а якщо воно було вдягнуте — профіль без тла. Без обмежень; перерва на нову картинку
    /// при цьому не скидається (інакше «прибрати й поставити» обходило б добу).
    /// </summary>
    public LavkaPhotoReply Remove(string nick, bool account)
    {
        if (!account) return new(false, Lavka.NotAccount);
        string? old;
        var worn = false;
        lock (_gate)
        {
            old = store.DropBgPhoto(nick);
            if (old is not null && store.Worn(nick, LavkaKind.Bg) == LavkaCatalog.OwnBg)
            {
                store.Unwear(nick, LavkaKind.Bg);
                worn = true;
            }
        }
        if (old is null) return new(false, "Свого тла й так нема");
        Delete(old);
        lavka.Announce(nick);
        return new(true, worn ? "Тло прибрано — профіль знову без тла" : "Тло прибрано", null, lavka.ReadyAt(nick, LavkaCatalog.OwnBg));
    }

    /// <summary>Усі поставлені тла — для адміна, свіжі згори.</summary>
    public List<LavkaPhotoRow> All() => store.AllBgPhotos();

    /// <summary>
    /// Адмін знімає тло: файл — геть, слот тла порожніє (якщо там було своє), вміння лишається за людиною, і нову
    /// картинку можна ставити одразу (перерва скинута) — зняли не за «часто міняв», а за те, що на картинці.
    /// </summary>
    public LavkaPhotoReply TakeDown(string? nick)
    {
        var name = Auth.CleanNick((nick ?? "").Trim().TrimStart('@'));
        if (name.Length == 0) return new(false, "Чиє тло зняти?");
        LavkaPhotoRow? row;
        lock (_gate)
        {
            row = store.BgPhoto(name);
            if (row is null)
            {
                var of = NickCases.Genitive(name);
                return new(false, $"{NickCases.AtStart(of)} {of} свого тла нема");
            }
            store.DropBgPhoto(row.Nick);
            if (store.Worn(row.Nick, LavkaKind.Bg) == LavkaCatalog.OwnBg) store.Unwear(row.Nick, LavkaKind.Bg);
            store.ForgetPerk(row.Nick, LavkaCatalog.OwnBg);
        }
        Delete(row.File);
        lavka.Announce(row.Nick);
        try { wire.Toast(row.Nick, "🖼 Розробник зняв твоє тло — можна одразу поставити інше"); }
        catch (Exception ex) { log.LogWarning(ex, "не сказали {Nick}, що тло знято", row.Nick); }
        return new(true, $"Тло {NickCases.Genitive(row.Nick)} знято");
    }

    /// <summary>Шлях до файла тла за іменем з адреси; null — ім'я не наше (жодного «..», жодних чужих файлів) або файла нема.</summary>
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
            log.LogWarning(ex, "старе тло {File} не прибралось з диска", file);
        }
    }
}
