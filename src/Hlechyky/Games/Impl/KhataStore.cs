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
/// Звідки квартира: <c>own</c> — додав гравець (фото лежать у нас), решта — справжні оголошення зі сторонніх джерел.
/// Їхні фото ми не копіюємо: гравець бачить їх просто з сайту-джерела, а на розкритті — посилання на саме оголошення.
/// </summary>
public static class KhataSources
{
    public const string Own = "own";
    /// <summary>Відкритий набір Inside Airbnb (CC BY 4.0): ціна за ніч, одне фото-обкладинка.</summary>
    public const string Airbnb = "airbnb";
    /// <summary>Офіційний API DIM.RIA: довгострокова оренда в Україні, ціна за місяць, усі фото оголошення.</summary>
    public const string Ria = "ria";
    /// <summary>
    /// Оренда з OLX — лише для внутрішньої гри кола друзів: набирається по кнопці розробника, живе тиждень
    /// (<see cref="KhataOlx.Life"/>), іде тільки за столи, де всі з акаунтами, глядачам не показується.
    /// </summary>
    public const string Olx = "olx";

    public static string Label(string source) => source switch { Airbnb => "Airbnb", Ria => "DIM.RIA", Olx => "OLX", _ => "" };

    /// <summary>Адреса фото для показу: сервери фото Airbnb й OLX самі віддають зменшений варіант (оригінали важать мегабайти).</summary>
    public static string Show(string source, string photo) => source switch
    {
        Airbnb => photo + "?im_w=960",
        Olx => photo + ";s=1000x1000",
        _ => photo,
    };

    /// <summary>Мініатюра — для списку «Як це було» й перевірки, чи фото ще живе.</summary>
    public static string Thumb(string source, string photo) => source switch
    {
        Airbnb => photo + "?im_w=320",
        Olx => photo + ";s=320x320",
        _ => photo,
    };
}

/// <summary>
/// Квартира банку «Здам хату»: місто (єдине, що гравець бачить текстом), ціна, фото, хто й коли додав і стан:
/// <c>pending</c> — чекає на схвалення, <c>ok</c> — у грі. Своя (<see cref="KhataSources.Own"/>) — фото це імена файлів у
/// <c>data/khata</c>, ціна за місяць. Зовнішня — фото це адреси на сайті-джерелі, <see cref="Link"/> — саме оголошення,
/// <see cref="About"/> — що це за житло (показуємо лише після відповіді), <see cref="Per"/> — за місяць чи за ніч (добу).
/// <see cref="Dream"/> — «хата мрії» (замок, цікава архітектура, море, озеро…), <see cref="Odd"/> — ціна несподівана.
/// </summary>
public sealed record KhataFlat(long Id, string Region, string City, int Price, IReadOnlyList<string> Photos, string Author,
    DateTimeOffset At, string Status, string Source = KhataSources.Own, string ExtId = "", string? Link = null,
    string Per = KhataFlat.Month, string? About = null, bool Dream = false, bool Odd = false)
{
    public const string Pending = "pending", Ok = "ok";
    public const string Month = "month", Night = "night";

    /// <summary>
    /// Ціна, якої ніхто не чекає: щонайменше вдвічі нижча за звичайну для цього місця або щонайменше втричі вища.
    /// На таких усі за столом і помиляються — у «Хатах мрії» кожна третя саме така (<see cref="Odd"/>).
    /// </summary>
    public static bool IsOdd(int price, int typical) => typical > 0 && (price * 2L <= typical || price >= typical * 3L);
    /// <summary>Ключ для пам'яті «хто що бачив» (<see cref="GameSeen"/>).</summary>
    public string Key => "f" + Id.ToString(CultureInfo.InvariantCulture);
    public string Unit => KhataRegions.Unit(Region);
    public bool Live => Status == Ok;
    public bool External => Source != KhataSources.Own;
    public IEnumerable<string> Urls => External
        ? Photos.Select(p => KhataSources.Show(Source, p))
        : Photos.Select(f => KhataStore.UrlPrefix + f);
    public string Thumb => External ? KhataSources.Thumb(Source, Photos[0]) : KhataStore.UrlPrefix + Photos[0];
}

/// <summary>Оголошення зі стороннього джерела — те, що імпорт віддає банку.</summary>
public sealed record KhataExternal(string ExtId, string Region, string City, int Price, string Per, IReadOnlyList<string> Photos,
    string Link, string? About, bool Dream = false, bool Odd = false);

/// <summary>Відповідь на дію з квартирою: чи вдалось, що сказати людині, і сама квартира.</summary>
public sealed record KhataReply(bool Ok, string Message, KhataFlat? Flat = null);

/// <summary>
/// Банк квартир «Здам хату» (specs/khata.md). Дві частини в одній таблиці: справжні оголошення зі сторонніх джерел
/// (<see cref="Import"/>; фото лишаються на сайті-джерелі) і квартири, які додали самі гравці (фото стискає браузер,
/// лежать у <c>data/khata</c> під іменем-хешем; розробник схвалює). Гра читає банк під замком кімнати, тому готові
/// квартири — знімок у пам'яті (<see cref="Live"/>); база й диск — лише з HTTP і фону. Без бази (тести) — те саме в пам'яті.
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

    /// <summary>Колонки, що з'явились разом зі сторонніми джерелами: старій таблиці їх додаємо, нова народжується з ними.</summary>
    static readonly (string Name, string Ddl)[] Added =
    [
        ("source", "TEXT NOT NULL DEFAULT 'own'"),
        ("ext_id", "TEXT NOT NULL DEFAULT ''"),
        ("link", "TEXT"),
        ("per", "TEXT NOT NULL DEFAULT 'month'"),
        ("about", "TEXT"),
        ("dream", "INTEGER NOT NULL DEFAULT 0"),
        ("odd", "INTEGER NOT NULL DEFAULT 0"),
    ];

    [GeneratedRegex("^[0-9a-f]{20}\\.(jpg|png|webp)$")]
    private static partial Regex FileName();

    readonly Db? _db;
    readonly string _dir;
    readonly IClock _clock;
    readonly ILogger? _log;
    readonly object _gate = new();
    List<KhataFlat> _all = [];     // усі незняті: зовнішні, свої схвалені й ті, що чекають
    /// <summary>Зняті зовнішні оголошення («джерело:id»): повторний імпорт їх не воскрешає.</summary>
    readonly HashSet<string> _gone = new(StringComparer.Ordinal);
    long _memId;
    long _cursor;
    volatile KhataFlat[] _live = [];
    volatile IReadOnlyDictionary<string, int> _byRegion = new Dictionary<string, int>();
    volatile IReadOnlyDictionary<string, int> _bySource = new Dictionary<string, int>();
    volatile IReadOnlyDictionary<string, int> _dreamByRegion = new Dictionary<string, int>();

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
                    var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    using (var info = Cmd(c, "PRAGMA table_info(khata_flats)"))
                    using (var r = info.ExecuteReader())
                        while (r.Read()) have.Add(r.GetString(1));
                    foreach (var (name, ddl) in Added)
                        if (!have.Contains(name)) Exec(c, $"ALTER TABLE khata_flats ADD COLUMN {name} {ddl}");
                    Exec(c, "CREATE INDEX IF NOT EXISTS khata_flats_ext ON khata_flats(source, ext_id)");

                    var list = new List<KhataFlat>();
                    using (var cmd = Cmd(c, "SELECT id, region, city, price, photos, author, at, status, source, ext_id, link, per, about, dream, odd FROM khata_flats WHERE removed = 0 ORDER BY id"))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                        {
                            var source = r.GetString(8);
                            var photos = Files(r.GetString(4), source != KhataSources.Own);
                            if (photos.Count == 0) continue;
                            list.Add(new KhataFlat(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), photos, r.GetString(5),
                                Ts(r.GetString(6)), r.GetString(7), source, r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10),
                                r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12), r.GetInt64(13) != 0, r.GetInt64(14) != 0));
                        }
                    using (var cmd = Cmd(c, "SELECT source, ext_id FROM khata_flats WHERE removed = 1 AND source <> 'own'"))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) _gone.Add(GoneKey(r.GetString(0), r.GetString(1)));
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

    static string GoneKey(string source, string extId) => source + ":" + extId;

    void Publish()
    {
        var live = _all.Where(f => f.Live).ToArray();
        _byRegion = live.GroupBy(f => f.Region, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        _bySource = live.GroupBy(f => f.Source, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        _dreamByRegion = live.Where(f => f.Dream).GroupBy(f => f.Region, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        _live = live;
    }

    /// <summary>Фото з рядка бази: свої — лише імена наших файлів, зовнішні — лише адреси https.</summary>
    static List<string> Files(string json, bool external)
    {
        try
        {
            return [.. (JsonSerializer.Deserialize<string[]>(json) ?? [])
                .Where(f => external ? f.StartsWith("https://", StringComparison.Ordinal) : FileName().IsMatch(f))];
        }
        catch (JsonException) { return []; }
    }

    // =========================================================================================
    // для гри (під замком кімнати: лише пам'ять)
    // =========================================================================================

    /// <summary>Квартири в грі — те, з чого вона набирає партію.</summary>
    public IReadOnlyList<KhataFlat> Live => _live;

    /// <summary>Квартири обраних регіонів (null — усі); <paramref name="dream"/> — лише «хати мрії».</summary>
    public List<KhataFlat> Fit(IReadOnlySet<string>? regions, bool dream = false) =>
        [.. _live.Where(f => (regions is null || regions.Contains(f.Region)) && (!dream || f.Dream))];

    /// <summary>Скільки їх — без копіювання списку: вид кімнати питає це на кожну розсилку.</summary>
    public int CountFit(IReadOnlySet<string>? regions, bool dream = false)
    {
        var by = dream ? _dreamByRegion : _byRegion;
        return regions is null ? by.Values.Sum() : regions.Sum(r => by.GetValueOrDefault(r));
    }

    /// <summary>Скільки в грі «хат мрії» з кожного регіону.</summary>
    public IReadOnlyDictionary<string, int> DreamByRegion => _dreamByRegion;

    /// <summary>Скільки квартир у грі з кожного регіону й з кожного джерела.</summary>
    public IReadOnlyDictionary<string, int> ByRegion => _byRegion;
    public IReadOnlyDictionary<string, int> BySource => _bySource;

    // =========================================================================================
    // сторонні джерела
    // =========================================================================================

    /// <summary>
    /// Прийняти оголошення зі стороннього джерела: нові — додати (одразу в грі), знайомі — оновити, якщо щось змінилось.
    /// Зняті раніше (фото зникло, розробник прибрав) не воскресають. Старих, яких у новому наборі нема, не чіпаємо:
    /// оголошення могли зняти, а квартира з ціною на той день лишається загадкою, поки живе її фото.
    /// </summary>
    public (int Added, int Updated, int Skipped) Import(string source, IReadOnlyCollection<KhataExternal> items)
    {
        if (source == KhataSources.Own || string.IsNullOrEmpty(source)) return (0, 0, items.Count);
        var at = _clock.UtcNow;
        var label = KhataSources.Label(source);
        lock (_gate)
        {
            var known = _all.Where(f => f.Source == source).ToDictionary(f => f.ExtId, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var fresh = new List<KhataExternal>();
            var changed = new List<KhataFlat>();
            var skipped = 0;
            foreach (var it in items)
            {
                var city = CleanCity(it.City);
                var ok = !string.IsNullOrEmpty(it.ExtId) && KhataRegions.Known(it.Region) && city is not null
                    && it.Price is >= 1 and <= MaxPrice && it.Per is KhataFlat.Month or KhataFlat.Night
                    && it.Photos.Count is >= 1 and <= MaxPhotos * 4 && it.Photos.All(p => p.StartsWith("https://", StringComparison.Ordinal))
                    && it.Link.StartsWith("https://", StringComparison.Ordinal);
                if (!ok || _gone.Contains(GoneKey(source, it.ExtId)) || !seen.Add(it.ExtId)) { skipped++; continue; }
                var clean = it with { City = city! };
                if (!known.TryGetValue(it.ExtId, out var was)) fresh.Add(clean);
                else if (was.Region != clean.Region || was.City != clean.City || was.Price != clean.Price || was.Per != clean.Per
                    || was.Link != clean.Link || was.About != clean.About || was.Dream != clean.Dream || was.Odd != clean.Odd || !was.Photos.SequenceEqual(clean.Photos))
                    changed.Add(was with { Region = clean.Region, City = clean.City, Price = clean.Price, Per = clean.Per,
                        Link = clean.Link, About = clean.About, Photos = clean.Photos, Dream = clean.Dream, Odd = clean.Odd });
            }

            var ids = new long[fresh.Count];
            if (_db is null) for (var i = 0; i < ids.Length; i++) ids[i] = ++_memId;
            else
            {
                try
                {
                    _db.With(c =>
                    {
                        Exec(c, "BEGIN");
                        try
                        {
                            for (var i = 0; i < fresh.Count; i++)
                            {
                                var f = fresh[i];
                                Exec(c, """
                                    INSERT INTO khata_flats(region, city, price, photos, author, author_key, at, status, source, ext_id, link, per, about, dream, odd)
                                    VALUES($r, $c, $p, $ph, $a, $k, $at, 'ok', $s, $e, $l, $per, $ab, $d, $o)
                                    """,
                                    ("$r", f.Region), ("$c", f.City), ("$p", f.Price), ("$ph", JsonSerializer.Serialize(f.Photos)), ("$a", label),
                                    ("$k", source), ("$at", Iso(at)), ("$s", source), ("$e", f.ExtId), ("$l", f.Link), ("$per", f.Per), ("$ab", f.About), ("$d", f.Dream ? 1 : 0), ("$o", f.Odd ? 1 : 0));
                                using var cmd = Cmd(c, "SELECT last_insert_rowid()");
                                ids[i] = (long)cmd.ExecuteScalar()!;
                            }
                            foreach (var f in changed)
                                Exec(c, "UPDATE khata_flats SET region = $r, city = $c, price = $p, photos = $ph, link = $l, per = $per, about = $ab, dream = $d, odd = $o WHERE id = $id",
                                    ("$r", f.Region), ("$c", f.City), ("$p", f.Price), ("$ph", JsonSerializer.Serialize(f.Photos)), ("$l", f.Link),
                                    ("$per", f.Per), ("$ab", f.About), ("$d", f.Dream ? 1 : 0), ("$o", f.Odd ? 1 : 0), ("$id", f.Id));
                            Exec(c, "COMMIT");
                        }
                        catch
                        {
                            Exec(c, "ROLLBACK");
                            throw;
                        }
                    });
                }
                catch (Exception ex)
                {
                    _log?.LogWarning(ex, "khata: оголошення з {Source} не записались", source);
                    return (0, 0, items.Count);
                }
            }

            var byId = changed.ToDictionary(f => f.Id);
            var next = new List<KhataFlat>(_all.Count + fresh.Count);
            foreach (var f in _all) next.Add(byId.GetValueOrDefault(f.Id, f));
            for (var i = 0; i < fresh.Count; i++)
            {
                var f = fresh[i];
                next.Add(new KhataFlat(ids[i], f.Region, f.City, f.Price, f.Photos, label, at, KhataFlat.Ok, source, f.ExtId, f.Link, f.Per, f.About, f.Dream, f.Odd));
            }
            _all = next;
            Publish();
            return (fresh.Count, changed.Count, skipped);
        }
    }

    /// <summary>Чи банк уже знає це оголошення джерела (є в грі або зняте) — щоб не питати про нього джерело вдруге.</summary>
    public bool Knows(string source, string extId)
    {
        lock (_gate) return _gone.Contains(GoneKey(source, extId)) || _all.Any(f => f.Source == source && f.ExtId == extId);
    }

    /// <summary>Наступне зовнішнє оголошення на перевірку «чи фото ще живе» — по колу.</summary>
    public KhataFlat? NextToCheck()
    {
        var live = _live;
        KhataFlat? first = null, next = null;
        foreach (var f in live)
        {
            if (!f.External) continue;
            first ??= f;
            if (f.Id > Interlocked.Read(ref _cursor)) { next = f; break; }
        }
        var pick = next ?? first;
        if (pick is not null) Interlocked.Exchange(ref _cursor, pick.Id);
        return pick;
    }

    /// <summary>Зовнішнє оголошення за адресою його фото (так, як її бачить гравець) — для скарги «фото не відкрилось».</summary>
    public KhataFlat? ByPhoto(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var cut = url.IndexOf('?');
        var bare = cut < 0 ? url : url[..cut];
        foreach (var f in _live)
            if (f.External && f.Photos.Contains(bare)) return f;
        return null;
    }

    /// <summary>
    /// Зняти зовнішнє оголошення (фото зникло або розробник прибрав). Повторний імпорт його не поверне. У рядку бази
    /// лишається тільки «таке вже було» — джерело й номер; міста, ціни, фото й посилання ми по собі не тримаємо.
    /// </summary>
    public bool Drop(long id)
    {
        lock (_gate)
        {
            var flat = _all.FirstOrDefault(f => f.Id == id && f.External);
            if (flat is null) return false;
            _db?.Exec(Forget + " WHERE id = $id", ("$id", id));
            _gone.Add(GoneKey(flat.Source, flat.ExtId));
            _all = [.. _all.Where(f => f.Id != id)];
            Publish();
            return true;
        }
    }

    const string Forget = "UPDATE khata_flats SET removed = 1, city = '', price = 0, photos = '[]', link = NULL, about = NULL";

    /// <summary>
    /// Відпустити оголошення джерела, набрані раніше за <paramref name="before"/>: з гри геть, з бази — теж (лишається
    /// лише позначка «вже було»). Так живе OLX: набір на тиждень, а не архів чужих оголошень.
    /// </summary>
    public int Expire(string source, DateTimeOffset before)
    {
        if (source == KhataSources.Own) return 0;
        lock (_gate)
        {
            var old = _all.Where(f => f.Source == source && f.At < before).ToList();
            if (old.Count == 0) return 0;
            _db?.Exec(Forget + " WHERE source = $s AND removed = 0 AND at < $t", ("$s", source), ("$t", Iso(before)));
            foreach (var f in old) _gone.Add(GoneKey(f.Source, f.ExtId));
            var ids = old.Select(f => f.Id).ToHashSet();
            _all = [.. _all.Where(f => !ids.Contains(f.Id))];
            Publish();
            return old.Count;
        }
    }

    // =========================================================================================
    // HTTP: квартири від гравців
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
            if (_all.Any(f => !f.External && f.Photos.Any(files.Contains))) return new(false, "Таке фото вже є в банку — схоже, цю квартиру вже додали");
            if (!admin && _all.Count(f => !f.External && !f.Live && Auth.NickKey(f.Author) == key) >= MaxPending)
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
            var i = _all.FindIndex(f => f.Id == id && !f.External);
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
            var i = _all.FindIndex(f => f.Id == id && !f.External);
            if (i < 0) return new(false, "Такої квартири нема");
            var flat = _all[i] with { Region = region!, City = city, Price = (int)price };
            _db?.Exec("UPDATE khata_flats SET region = $r, city = $c, price = $p WHERE id = $id", ("$r", region), ("$c", city), ("$p", price), ("$id", id));
            _all = [.. _all.Select(f => f.Id == id ? flat : f)];
            Publish();
            return new(true, "Виправлено", flat);
        }
    }

    /// <summary>
    /// Зняти квартиру: автор — свою, розробник — будь-яку (і зовнішнє оголошення теж — скажімо, з недоречним фото).
    /// Рядок позначено, свої фото з диска геть.
    /// </summary>
    public KhataReply Remove(long id, string nick, bool account, bool admin)
    {
        lock (_gate)
        {
            var flat = _all.FirstOrDefault(f => f.Id == id);
            if (flat is null) return new(false, "Такої квартири нема");
            if (flat.External)
                return !admin ? new(false, "Зняти можна лише свою квартиру")
                    : Drop(id) ? new(true, $"Оголошення з {flat.Author} прибрано з гри", flat) : new(false, "Такої квартири нема");
            var own = account && Auth.NickKey(flat.Author) == Auth.NickKey(nick);
            if (!admin && !own) return new(false, "Зняти можна лише свою квартиру");
            _db?.Exec("UPDATE khata_flats SET removed = 1 WHERE id = $id", ("$id", id));
            _all = [.. _all.Where(f => f.Id != id)];
            Publish();
            foreach (var file in flat.Photos)
                if (!_all.Any(f => !f.External && f.Photos.Contains(file))) TryDelete(file);
            return new(true, own ? "Квартиру знято" : $"Квартиру від {flat.Author} знято", flat);
        }
    }

    /// <summary>Мої квартири (і ті, що чекають) — свіжі згори.</summary>
    public List<KhataFlat> Mine(string nick)
    {
        var key = Auth.NickKey(nick);
        lock (_gate) return [.. _all.Where(f => !f.External && Auth.NickKey(f.Author) == key).OrderByDescending(f => f.Id)];
    }

    /// <summary>Усі незняті квартири від гравців (розробнику): спершу ті, що чекають, далі свіжі згори. Зовнішні сюди не йдуть — їх тисячі.</summary>
    public List<KhataFlat> Everything()
    {
        lock (_gate) return [.. _all.Where(f => !f.External).OrderBy(f => f.Live).ThenByDescending(f => f.Id)];
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
