using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Games.Impl;

/// <summary>Де квартира: від цього залежить валюта (Україна — гривні, решта — долари) й опція столу «Міста».</summary>
public static class KhataRegions
{
    public const string Any = "all";
    public const string Ua = "ua", Eu = "eu", World = "world";

    /// <summary>Значення опції «Міста» (можна кілька): усе, Україна, Європа, решта світу.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> Choices =
    [
        (Any, "Усі міста"),
        (Ua, "Україна"),
        (Eu, "Європа"),
        (World, "Світ"),
    ];

    public static bool Known(string? region) => region is Ua or Eu or World;

    /// <summary>Знак валюти біля поля: Україна — «₴», решта — «$».</summary>
    public static string Unit(string region) => region == Ua ? "₴" : "$";

    /// <summary>Опція столу → множина регіонів; null — усі.</summary>
    public static IReadOnlySet<string>? Parse(string? option)
    {
        var picked = GameOption.Split(option);
        if (picked.Count == 0 || picked.Contains(Any)) return null;
        var known = picked.Where(Known).ToHashSet(StringComparer.Ordinal);
        return known.Count == 0 || known.Count == 3 ? null : known;
    }
}

/// <summary>
/// Квартира банку «Здам хату»: місто (єдине, що гравець бачить текстом), ціна оренди за місяць, 1–5 фото (імена файлів у
/// <c>data/khata</c>), хто й коли додав і стан: <c>pending</c> — чекає на схвалення, <c>ok</c> — у грі.
/// </summary>
public sealed record KhataFlat(long Id, string Region, string City, int Price, IReadOnlyList<string> Photos, string Author,
    DateTimeOffset At, string Status)
{
    public const string Pending = "pending", Ok = "ok";
    /// <summary>Ключ для пам'яті «хто що бачив» (<see cref="GameSeen"/>).</summary>
    public string Key => "f" + Id.ToString(CultureInfo.InvariantCulture);
    public string Unit => KhataRegions.Unit(Region);
    public bool Live => Status == Ok;
    public IEnumerable<string> Urls => Photos.Select(f => KhataStore.UrlPrefix + f);
}

/// <summary>Відповідь на дію з квартирою: чи вдалось, що сказати людині, і сама квартира.</summary>
public sealed record KhataReply(bool Ok, string Message, KhataFlat? Flat = null);

/// <summary>
/// Банк квартир «Здам хату» (specs/khata.md): його наповнюють самі гравці — фото, місто й ціна; розробник схвалює, і
/// лише тоді квартира йде в гру. Фото стискає браузер (сервер звіряє магічні байти, вагу й розміри), лежать вони в
/// <c>data/khata</c> під іменем-хешем вмісту. Гра читає банк під замком кімнати, тому готові квартири — знімок у пам'яті
/// (<see cref="Live"/>); база й диск — лише з HTTP. Без бази (тести) — та сама логіка в пам'яті.
/// </summary>
public sealed partial class KhataStore
{
    public const string Dir = "data/khata";
    public const string UrlPrefix = "/api/games/khata/pic/";
    public const int MaxPhotos = 5;
    /// <summary>Скільки важить одне фото: браузер тисне до ~1280 px і 150–300 КБ, решта — запас.</summary>
    public const int MaxBytes = 400 * 1024;
    public const int MaxSide = 1920, MinSide = 240;
    public const int MaxCity = 40, MinCity = 2;
    public const int MaxPrice = 100_000_000;
    /// <summary>Скільки квартир одного автора може чекати на схвалення водночас.</summary>
    public const int MaxPending = 10;
    /// <summary>Автору за схвалену квартиру (база; у гаманець іде × Economy:Boost). Раз на квартиру.</summary>
    public const int AuthorReward = 30;

    const string Schema = """
        CREATE TABLE IF NOT EXISTS khata_flats(id INTEGER PRIMARY KEY AUTOINCREMENT, region TEXT NOT NULL, city TEXT NOT NULL,
          price INTEGER NOT NULL, photos TEXT NOT NULL, author TEXT NOT NULL, author_key TEXT NOT NULL, at TEXT NOT NULL,
          status TEXT NOT NULL DEFAULT 'pending', removed INTEGER NOT NULL DEFAULT 0);
        """;

    [GeneratedRegex("^[0-9a-f]{20}\\.(jpg|png|webp)$")]
    private static partial Regex FileName();

    readonly Db? _db;
    readonly string _dir;
    readonly IClock _clock;
    readonly ILogger? _log;
    readonly object _gate = new();
    List<KhataFlat> _all = [];     // усі незняті: і схвалені, і ті, що чекають
    long _memId;
    volatile KhataFlat[] _live = [];

    /// <summary>Нарахувати автору за схвалену квартиру (нік, скільки, ref). Ставить DI з Economy; тести — свою.</summary>
    public Action<string, int, string>? Reward { get; set; }

    public KhataStore(Db? db, string dir, IClock clock, ILogger? log = null)
    {
        _db = db;
        _dir = dir;
        _clock = clock;
        _log = log;
        if (_db is not null)
        {
            try
            {
                _db.With(c =>
                {
                    Exec(c, Schema);
                    var list = new List<KhataFlat>();
                    using var cmd = Cmd(c, "SELECT id, region, city, price, photos, author, at, status FROM khata_flats WHERE removed = 0 ORDER BY id");
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var photos = Files(r.GetString(4));
                        if (photos.Count == 0) continue;
                        list.Add(new KhataFlat(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), photos, r.GetString(5),
                            Ts(r.GetString(6)), r.GetString(7)));
                    }
                    _all = list;
                });
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "khata: не прочитав банк квартир з бази");
            }
        }
        Publish();
    }

    void Publish() => _live = [.. _all.Where(f => f.Live)];

    static List<string> Files(string json)
    {
        try { return [.. (JsonSerializer.Deserialize<string[]>(json) ?? []).Where(f => FileName().IsMatch(f))]; }
        catch (JsonException) { return []; }
    }

    // =========================================================================================
    // для гри (під замком кімнати: лише пам'ять)
    // =========================================================================================

    /// <summary>Схвалені квартири — те, з чого гра набирає партію.</summary>
    public IReadOnlyList<KhataFlat> Live => _live;

    /// <summary>Схвалені квартири обраних регіонів (null — усі).</summary>
    public List<KhataFlat> Fit(IReadOnlySet<string>? regions) =>
        [.. _live.Where(f => regions is null || regions.Contains(f.Region))];

    // =========================================================================================
    // HTTP
    // =========================================================================================

    /// <summary>Місто: без зайвих пробілів і керівних символів. Порожнє чи задовге — null.</summary>
    public static string? CleanCity(string? raw)
    {
        var words = new string([.. (raw ?? "").Where(ch => !char.IsControl(ch))])
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var city = string.Join(' ', words);
        return city.Length is >= MinCity and <= MaxCity ? city : null;
    }

    /// <summary>
    /// Додати квартиру: регіон, місто, ціна за місяць і 1–5 фото (JPEG/PNG/WebP, уже стиснуті браузером). Лише з акаунта.
    /// Від розробника — одразу в грі, від решти — чекає на схвалення.
    /// </summary>
    public KhataReply Add(string nick, bool account, bool admin, string? region, string? rawCity, long price, IReadOnlyList<byte[]> photos)
    {
        if (!account) return new(false, "Квартири додають лише з акаунта");
        if (!KhataRegions.Known(region)) return new(false, "Обери, де квартира: Україна, Європа чи світ");
        if (CleanCity(rawCity) is not { } city) return new(false, $"Напиши місто: від {MinCity} до {MaxCity} знаків");
        if (price < 1) return new(false, "Ціна — додатне число: скільки коштує оренда за місяць");
        if (price > MaxPrice) return new(false, "Забагато як на оренду — перевір нулі");
        if (photos.Count == 0) return new(false, "Додай хоча б одне фото квартири");
        if (photos.Count > MaxPhotos) return new(false, $"Не більше {MaxPhotos} фото на квартиру");

        var files = new List<string>(photos.Count);
        foreach (var bytes in photos)
        {
            if (bytes.Length == 0) return new(false, "Одне з фото не дійшло — обери ще раз");
            if (bytes.Length > MaxBytes) return new(false, $"Фото завелике: до {MaxBytes / 1024} КБ кожне");
            if (LavkaImage.Sniff(bytes) is not { Width: > 0, Height: > 0 } img) return new(false, "Це не схоже на фото: годяться JPEG, PNG чи WebP");
            if (img.Width > MaxSide || img.Height > MaxSide) return new(false, $"Фото завелике: до {MaxSide} px по довшій стороні");
            if (Math.Min(img.Width, img.Height) < MinSide) return new(false, "Фото замале — на ньому нічого не роздивишся");
            var file = $"{Convert.ToHexString(SHA256.HashData(bytes))[..20].ToLowerInvariant()}.{img.Ext}";
            if (files.Contains(file)) return new(false, "Двічі те саме фото — прибери повтор");
            files.Add(file);
        }

        var key = Auth.NickKey(nick);
        lock (_gate)
        {
            if (_all.Any(f => f.Photos.Any(files.Contains))) return new(false, "Таке фото вже є в банку — схоже, цю квартиру вже додали");
            if (!admin && _all.Count(f => !f.Live && Auth.NickKey(f.Author) == key) >= MaxPending)
                return new(false, $"У тебе вже {MaxPending} квартир чекає на схвалення — дочекайся, поки їх переглянуть");
            try
            {
                Directory.CreateDirectory(_dir);
                for (var i = 0; i < files.Count; i++)
                {
                    var path = Path.Combine(_dir, files[i]);
                    if (File.Exists(path)) continue;
                    var tmp = path + ".tmp";
                    File.WriteAllBytes(tmp, photos[i]);
                    File.Move(tmp, path, overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.LogWarning(ex, "khata: фото {Nick} не лягли на диск", nick);
                foreach (var f in files) TryDelete(f);
                return new(false, "Фото не збереглись — спробуй трохи згодом");
            }

            var at = _clock.UtcNow;
            var status = admin ? KhataFlat.Ok : KhataFlat.Pending;
            long id;
            if (_db is null) id = ++_memId;
            else
            {
                try
                {
                    id = _db.With(c =>
                    {
                        Exec(c, "INSERT INTO khata_flats(region, city, price, photos, author, author_key, at, status) VALUES($r, $c, $p, $ph, $a, $k, $at, $s)",
                            ("$r", region), ("$c", city), ("$p", price), ("$ph", JsonSerializer.Serialize(files)), ("$a", nick), ("$k", key),
                            ("$at", Iso(at)), ("$s", status));
                        using var cmd = Cmd(c, "SELECT last_insert_rowid()");
                        return (long)cmd.ExecuteScalar()!;
                    });
                }
                catch (Exception ex)
                {
                    _log?.LogWarning(ex, "khata: рядок квартири {Nick} не записався", nick);
                    foreach (var f in files) TryDelete(f);
                    return new(false, "Квартира не записалась — спробуй трохи згодом");
                }
            }
            var flat = new KhataFlat(id, region!, city, (int)price, files, nick, at, status);
            _all = [.. _all, flat];
            Publish();
            if (admin) Pay(flat);
            return new(true, admin ? "Квартира в банку — її вже можуть вгадувати" : "Дякую! Квартира чекає на схвалення — потім піде в гру", flat);
        }
    }

    /// <summary>Розробник схвалює: квартира йде в гру, автору — <see cref="AuthorReward"/>.</summary>
    public KhataReply Approve(long id)
    {
        lock (_gate)
        {
            var i = _all.FindIndex(f => f.Id == id);
            if (i < 0) return new(false, "Такої квартири нема");
            if (_all[i].Live) return new(true, "Вона вже в грі", _all[i]);
            var flat = _all[i] with { Status = KhataFlat.Ok };
            _db?.Exec("UPDATE khata_flats SET status = $s WHERE id = $id", ("$s", KhataFlat.Ok), ("$id", id));
            _all = [.. _all.Select(f => f.Id == id ? flat : f)];
            Publish();
            Pay(flat);
            return new(true, $"Схвалено: {flat.City} від {flat.Author} — у грі", flat);
        }
    }

    void Pay(KhataFlat flat)
    {
        try { Reward?.Invoke(flat.Author, AuthorReward, $"khata-flat:{flat.Id.ToString(CultureInfo.InvariantCulture)}"); }
        catch (Exception ex) { _log?.LogWarning(ex, "khata: нагорода автору {Nick} не нарахувалась", flat.Author); }
    }

    /// <summary>Розробник виправляє місто, регіон чи ціну (одруківка, зайвий нуль) — фото лишаються ті самі.</summary>
    public KhataReply Edit(long id, string? region, string? rawCity, long price)
    {
        if (!KhataRegions.Known(region)) return new(false, "Обери, де квартира: Україна, Європа чи світ");
        if (CleanCity(rawCity) is not { } city) return new(false, $"Напиши місто: від {MinCity} до {MaxCity} знаків");
        if (price is < 1 or > MaxPrice) return new(false, "Ціна — додатне число: скільки коштує оренда за місяць");
        lock (_gate)
        {
            var i = _all.FindIndex(f => f.Id == id);
            if (i < 0) return new(false, "Такої квартири нема");
            var flat = _all[i] with { Region = region!, City = city, Price = (int)price };
            _db?.Exec("UPDATE khata_flats SET region = $r, city = $c, price = $p WHERE id = $id", ("$r", region), ("$c", city), ("$p", price), ("$id", id));
            _all = [.. _all.Select(f => f.Id == id ? flat : f)];
            Publish();
            return new(true, "Виправлено", flat);
        }
    }

    /// <summary>Зняти квартиру: автор — свою, розробник — будь-яку. Рядок позначено, фото з диска геть.</summary>
    public KhataReply Remove(long id, string nick, bool account, bool admin)
    {
        lock (_gate)
        {
            var flat = _all.FirstOrDefault(f => f.Id == id);
            if (flat is null) return new(false, "Такої квартири нема");
            var own = account && Auth.NickKey(flat.Author) == Auth.NickKey(nick);
            if (!admin && !own) return new(false, "Зняти можна лише свою квартиру");
            _db?.Exec("UPDATE khata_flats SET removed = 1 WHERE id = $id", ("$id", id));
            _all = [.. _all.Where(f => f.Id != id)];
            Publish();
            foreach (var file in flat.Photos)
                if (!_all.Any(f => f.Photos.Contains(file))) TryDelete(file);
            return new(true, own ? "Квартиру знято" : $"Квартиру від {flat.Author} знято", flat);
        }
    }

    /// <summary>Мої квартири (і ті, що чекають) — свіжі згори.</summary>
    public List<KhataFlat> Mine(string nick)
    {
        var key = Auth.NickKey(nick);
        lock (_gate) return [.. _all.Where(f => Auth.NickKey(f.Author) == key).OrderByDescending(f => f.Id)];
    }

    /// <summary>Усі незняті квартири (розробнику): спершу ті, що чекають, далі свіжі згори.</summary>
    public List<KhataFlat> Everything()
    {
        lock (_gate) return [.. _all.OrderBy(f => f.Live).ThenByDescending(f => f.Id)];
    }

    public int PendingCount { get { lock (_gate) return _all.Count(f => !f.Live); } }

    /// <summary>Шлях до файла за іменем з адреси; чуже ім'я (жодного «..») чи нема файла — null.</summary>
    public string? Resolve(string? file)
    {
        if (string.IsNullOrEmpty(file) || !FileName().IsMatch(file)) return null;
        var path = Path.Combine(_dir, file);
        return File.Exists(path) ? path : null;
    }

    void TryDelete(string file)
    {
        if (!FileName().IsMatch(file)) return;
        try { File.Delete(Path.Combine(_dir, file)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log?.LogWarning(ex, "khata: {File} не прибрався", file); }
    }

    static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
    static DateTimeOffset Ts(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    static SqliteCommand Cmd(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    static void Exec(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = Cmd(c, sql, ps);
        cmd.ExecuteNonQuery();
    }
}
