using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Games.Impl;

/// <summary>Своя картинка «Склей глек»: файл у data/sklei, хто й коли поставив, чи сховав автор, скільки разів її склали інші.</summary>
public sealed record SkleiOwn(long Id, string File, string Author, DateTimeOffset At, int Bytes, bool Hidden, int Solved)
{
    public string Url => SkleiDeck.UrlPrefix + File;
    public SkleiPicture Picture => new($"{SkleiKind.Own}:{Id}", SkleiKind.Own, Url, $"картинка від {Author}", Author);
}

/// <summary>Малюнок з альбому Піктіонарі, який можна клеїти: номер, автор, загадане слово.</summary>
public sealed record SkleiArt(long Id, string Author, string Word)
{
    public SkleiPicture Picture => new($"{SkleiKind.Art}:{Id}", SkleiKind.Art, $"{SkleiDeck.ArtPrefix}{Id}", $"малюнок {Author}", Author);
}

/// <summary>Склав картинку в партії від двох людей — подія для фону: +1 автору, лічильники ачівок.</summary>
public sealed record SkleiSolved(string PicKey, string Kind, string? Author, string Solver, string Ref);

/// <summary>Відповідь на дію з картинкою: чи вдалось, що сказати, і (після завантаження) сама картинка.</summary>
public sealed record SkleiReply(bool Ok, string Message, SkleiOwn? Pic = null);

/// <summary>
/// Колода «Склей глек» (specs/sklei.md §6): свої картинки (купують акаунти за <see cref="Price"/> черепків), малюнки з
/// альбому Піктіонарі й фото «Де це?». Гра бере картинки під замком кімнати — тому все, що вона читає, лежить у пам'яті
/// (знімки-масиви), а база й гаманці — лише з HTTP чи з фонового циклу, куди гра кидає події <see cref="SkleiSolved"/>.
/// Без бази (тести) — та сама логіка в пам'яті.
/// </summary>
public sealed partial class SkleiDeck : BackgroundService
{
    public const string UrlPrefix = "/api/games/sklei/pic/";
    public const string ArtPrefix = "/api/games/sklei/art/";
    public const int Price = 400;
    public const int MaxBytes = 300 * 1024;
    public const int MaxSide = 768;
    public const int MinSide = 64;
    /// <summary>Скільки своїх (не знятих) картинок може мати один акаунт.</summary>
    public const int MaxPerAuthor = 60;
    /// <summary>Автору — +1 черепок за кожного, хто склав його картинку, але не більше стількох на добу.</summary>
    public const int AuthorDailyCap = 30;
    public const int FriendsForAch = 10, AuthorForAch = 25;
    public static readonly TimeSpan ArtRefresh = TimeSpan.FromMinutes(5);

    const string Schema = """
        CREATE TABLE IF NOT EXISTS sklei_pics(id INTEGER PRIMARY KEY AUTOINCREMENT, file TEXT NOT NULL, author TEXT NOT NULL,
          author_key TEXT NOT NULL, at TEXT NOT NULL, bytes INTEGER NOT NULL, hidden INTEGER NOT NULL DEFAULT 0,
          removed INTEGER NOT NULL DEFAULT 0, solved INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS sklei_stats(nick_key TEXT PRIMARY KEY, nick TEXT NOT NULL, friends INTEGER NOT NULL);
        """;

    [GeneratedRegex("^[0-9a-f]{20}\\.(jpg|png|webp)$")]
    private static partial Regex FileName();

    readonly Db? _db;
    readonly string _dir;
    readonly IClock _clock;
    readonly ILogger? _log;
    readonly PictionaryStore? _album;
    readonly object _gate = new();
    readonly Channel<SkleiSolved> _events = Channel.CreateUnbounded<SkleiSolved>();
    readonly Dictionary<string, int> _friends = new(StringComparer.Ordinal);
    List<SkleiOwn> _own = [];          // усі незняті, разом зі схованими
    long _memId;
    // Знімки для гри: читаються без замка (заміна посилання атомарна).
    volatile SkleiOwn[] _ownLive = [];
    volatile SkleiArt[] _arts = [];

    /// <summary>Списати черепки (нік, скільки, причина, ref) → чи списалось. Ставить DI з Economy; тести — свою.</summary>
    public Func<string, int, string, string, bool>? Spend { get; set; }
    /// <summary>Нарахувати автору зі стелею дня (нік, скільки, причина, ref).</summary>
    public Action<string, int, string, string>? Grant { get; set; }
    /// <summary>Відкрити ачівку (нік, ключ) — для тих, що набігають з багатьох партій.</summary>
    public Action<string, string>? Unlock { get; set; }
    /// <summary>Фото «Де це?» (null — фото нема, опція «фото» тоді бере вбудовані).</summary>
    public GeoPhotos? Geo { get; init; }

    public SkleiDeck(Db? db, string dir, IClock clock, PictionaryStore? album = null, ILogger? log = null)
    {
        _db = db;
        _dir = dir;
        _clock = clock;
        _album = album;
        _log = log;
        if (_db is not null)
        {
            try
            {
                _db.With(c =>
                {
                    Exec(c, Schema);
                    var list = new List<SkleiOwn>();
                    using (var cmd = Cmd(c, "SELECT id, file, author, at, bytes, hidden, solved FROM sklei_pics WHERE removed = 0 ORDER BY id"))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                            list.Add(new SkleiOwn(r.GetInt64(0), r.GetString(1), r.GetString(2), Ts(r.GetString(3)), r.GetInt32(4), r.GetInt32(5) != 0, r.GetInt32(6)));
                    using (var cmd = Cmd(c, "SELECT nick_key, friends FROM sklei_stats"))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) _friends[r.GetString(0)] = r.GetInt32(1);
                    _own = list;
                });
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "sklei: не прочитав колоду з бази");
            }
        }
        Publish();
        RefreshArts();
    }

    // =========================================================================================
    // для гри (під замком кімнати: лише пам'ять)
    // =========================================================================================

    /// <summary>Свої картинки, що йдуть у загальний пул (не сховані).</summary>
    public IReadOnlyList<SkleiOwn> Own => _ownLive;
    public IReadOnlyList<SkleiArt> Arts => _arts;

    /// <summary>Скільки фото «Де це?» лежить на диску (місць із хоч одним готовим фото).</summary>
    public int PhotoPlaces => Geo?.ReadyPlaces ?? 0;

    /// <summary>Випадкове готове фото «Де це?»; токен живе 45 хв — на партію досить. Назву місця не кажемо: це ж відповідь у «Де це?».</summary>
    public SkleiPicture? Photo(Random rng, Func<string, bool> used)
    {
        if (Geo is not { } geo || geo.ReadyPlaces == 0) return null;
        var ready = geo.Bank.Places.Where(p => geo.Ready(p.Id)).ToList();
        for (var tries = 0; tries < 6 && ready.Count > 0; tries++)
        {
            var place = ready[rng.Next(ready.Count)];
            var photos = geo.ReadyPhotos(place.Id);
            if (photos.Count == 0) continue;
            var idx = photos[rng.Next(photos.Count)];
            var key = $"{SkleiKind.Photo}:{place.Id}/{idx}";
            if (used(key)) continue;
            try { return new(key, SkleiKind.Photo, $"/api/games/geo/{geo.Issue(place.Id, idx)}.jpg", "фото з «Де це?»", null); }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>Гра каже: картинку склали (лише звичайна партія від двох людей). Обробить фон.</summary>
    public void Report(SkleiSolved e) => _events.Writer.TryWrite(e);

    /// <summary>Скільки малюнків друзів склав нік (для «Чужими руками»).</summary>
    public int Friends(string nick) { lock (_gate) return _friends.GetValueOrDefault(Auth.NickKey(nick)); }

    // =========================================================================================
    // HTTP: свої картинки
    // =========================================================================================

    /// <summary>
    /// Поставити свою картинку: тіло запиту — сам файл (JPEG/PNG/WebP, обрізаний браузером квадратом, ≤ <see cref="MaxSide"/>
    /// і ≤ <see cref="MaxBytes"/>). Спершу перевірка й файл на диску, потім оплата: не списалось — файл геть.
    /// </summary>
    public SkleiReply Upload(string nick, bool account, byte[] bytes)
    {
        if (!account) return new(false, "Свою картинку ставлять лише з акаунта");
        if (bytes.Length == 0) return new(false, "Картинка не дійшла — обери ще раз");
        if (bytes.Length > MaxBytes) return new(false, $"Завелика: до {MaxBytes / 1024} КБ");
        if (LavkaImage.Sniff(bytes) is not { Width: > 0, Height: > 0 } img) return new(false, "Це не схоже на картинку: годяться JPEG, PNG чи WebP");
        if (img.Width > MaxSide || img.Height > MaxSide) return new(false, $"Завелика: до {MaxSide}×{MaxSide}");
        if (Math.Min(img.Width, img.Height) < MinSide) return new(false, "Замала — черепків не набереш");
        if (Math.Abs(img.Width - img.Height) * 50 > Math.Max(img.Width, img.Height)) return new(false, "Картинка має бути квадратна — обріж її");

        var key = Auth.NickKey(nick);
        var file = $"{Convert.ToHexString(SHA256.HashData(bytes))[..20].ToLowerInvariant()}.{img.Ext}";
        lock (_gate)
        {
            var mine = _own.Where(o => Auth.NickKey(o.Author) == key).ToList();
            if (mine.Any(o => o.File == file)) return new(false, "Ця картинка вже є в колоді");
            if (mine.Count >= MaxPerAuthor) return new(false, $"У тебе вже {MaxPerAuthor} картинок — сховай чи попроси зняти старі");
            var path = Path.Combine(_dir, file);
            var fresh = !File.Exists(path);
            try
            {
                Directory.CreateDirectory(_dir);
                if (fresh)
                {
                    var tmp = path + ".tmp";
                    File.WriteAllBytes(tmp, bytes);
                    File.Move(tmp, path, overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.LogWarning(ex, "sklei: картинка {Nick} не лягла на диск", nick);
                return new(false, "Картинка не збереглась — спробуй трохи згодом");
            }
            if (Spend is null || !Spend(nick, Price, "Склей глек — своя картинка", $"sklei-pic:{key}:{file}"))
            {
                if (fresh && !_own.Any(o => o.File == file)) TryDelete(file);
                return new(false, $"Бракує черепків: картинка коштує {Price}");
            }
            var at = _clock.UtcNow;
            long id;
            if (_db is null) id = ++_memId;
            else
            {
                try
                {
                    id = _db.With(c =>
                    {
                        Exec(c, "INSERT INTO sklei_pics(file, author, author_key, at, bytes) VALUES($f, $a, $k, $at, $b)",
                            ("$f", file), ("$a", nick), ("$k", key), ("$at", Iso(at)), ("$b", bytes.Length));
                        using var cmd = Cmd(c, "SELECT last_insert_rowid()");
                        return (long)cmd.ExecuteScalar()!;
                    });
                }
                catch (Exception ex)
                {
                    // Черепки вже списано, а рядка нема — повертаємо, щоб людина не платила за повітря.
                    _log?.LogWarning(ex, "sklei: рядок картинки {Nick} не записався", nick);
                    Grant?.Invoke(nick, Price, "Склей глек — картинка не записалась, повертаю", $"sklei-refund:{key}:{file}");
                    return new(false, "Картинка не записалась — черепки повернуто");
                }
            }
            var pic = new SkleiOwn(id, file, nick, at, bytes.Length, false, 0);
            _own = [.. _own, pic];
            Publish();
            return new(true, "Картинка в колоді — її вже можуть клеїти всі", pic);
        }
    }

    /// <summary>Мої картинки (і сховані) — свіжі згори.</summary>
    public List<SkleiOwn> Mine(string nick)
    {
        var key = Auth.NickKey(nick);
        lock (_gate) return [.. _own.Where(o => Auth.NickKey(o.Author) == key).OrderByDescending(o => o.Id)];
    }

    /// <summary>Сховати (чи повернути) свою картинку з пулу. Чужу — ні.</summary>
    public SkleiReply Hide(string nick, bool account, long id, bool hidden)
    {
        if (!account) return new(false, "Свої картинки — лише з акаунта");
        lock (_gate)
        {
            var i = _own.FindIndex(o => o.Id == id);
            if (i < 0 || Auth.NickKey(_own[i].Author) != Auth.NickKey(nick)) return new(false, "Такої твоєї картинки нема");
            var pic = _own[i] with { Hidden = hidden };
            _db?.Exec("UPDATE sklei_pics SET hidden = $h WHERE id = $id", ("$h", hidden ? 1 : 0), ("$id", id));
            _own = [.. _own.Select(o => o.Id == id ? pic : o)];
            Publish();
            return new(true, hidden ? "Сховано: у партіях її більше не буде" : "Картинка знову в колоді", pic);
        }
    }

    /// <summary>Адмін знімає картинку (без премодерації — знімаємо після): рядок позначено, файл геть.</summary>
    public SkleiReply Remove(long id)
    {
        SkleiOwn? pic;
        lock (_gate)
        {
            pic = _own.FirstOrDefault(o => o.Id == id);
            if (pic is null) return new(false, "Такої картинки нема");
            _db?.Exec("UPDATE sklei_pics SET removed = 1 WHERE id = $id", ("$id", id));
            _own = [.. _own.Where(o => o.Id != id)];
            Publish();
            if (!_own.Any(o => o.File == pic.File)) TryDelete(pic.File);
        }
        return new(true, $"Картинку від {pic.Author} знято", pic);
    }

    /// <summary>Шлях до файла за іменем з адреси; чуже ім'я (жодного «..») чи нема файла — null.</summary>
    public string? Resolve(string? file)
    {
        if (string.IsNullOrEmpty(file) || !FileName().IsMatch(file)) return null;
        var path = Path.Combine(_dir, file);
        return File.Exists(path) ? path : null;
    }

    // =========================================================================================
    // малюнки Піктіонарі
    // =========================================================================================

    /// <summary>Перечитати, які малюнки є в альбомі (номер, автор, слово — без самих штрихів). Кличе фон раз на 5 хв.</summary>
    public void RefreshArts()
    {
        try
        {
            if (_db is not null)
            {
                _arts = _db.With(c =>
                {
                    var list = new List<SkleiArt>();
                    try
                    {
                        using var cmd = Cmd(c, "SELECT id, author, word FROM pictionary_album ORDER BY id");
                        using var r = cmd.ExecuteReader();
                        while (r.Read()) list.Add(new SkleiArt(r.GetInt64(0), r.GetString(1), r.GetString(2)));
                    }
                    catch (SqliteException) { }            // альбому ще нема — малюнків теж
                    return list.ToArray();
                });
            }
            else if (_album is not null)
            {
                var list = new List<SkleiArt>();
                long before = 0;
                for (var page = 0; page < 200; page++)
                {
                    var (items, more) = _album.Page(before, PictionaryStore.PageSize);
                    list.AddRange(items.Select(a => new SkleiArt(a.Id, a.Author, a.Word)));
                    if (!more || items.Count == 0) break;
                    before = items[^1].Id;
                }
                _arts = [.. list.OrderBy(a => a.Id)];
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "sklei: не перечитав альбом Піктіонарі");
        }
    }

    /// <summary>Сам малюнок для клієнта: штрихи як в альбомі (поле z — SketchWire), слово й автор. Нема — null.</summary>
    public (string Z, string Word, string Author)? Art(long id)
    {
        if (_db is not null)
        {
            return _db.With(c =>
            {
                try
                {
                    using var cmd = Cmd(c, "SELECT z, word, author FROM pictionary_album WHERE id = $id", ("$id", id));
                    using var r = cmd.ExecuteReader();
                    return r.Read() ? (r.GetString(0), r.GetString(1), r.GetString(2)) : ((string, string, string)?)null;
                }
                catch (SqliteException) { return null; }
            });
        }
        if (_album is null) return null;
        long before = 0;
        for (var page = 0; page < 200; page++)
        {
            var (items, more) = _album.Page(before, PictionaryStore.PageSize);
            if (items.FirstOrDefault(a => a.Id == id) is { } hit) return (hit.Z, hit.Word, hit.Author);
            if (!more || items.Count == 0) break;
            before = items[^1].Id;
        }
        return null;
    }

    // =========================================================================================
    // фон: події з партій
    // =========================================================================================

    /// <summary>Обробити все, що гра накидала (фон кличе сам; тести — напряму).</summary>
    public void Drain()
    {
        while (_events.Reader.TryRead(out var e))
        {
            try { Handle(e); }
            catch (Exception ex) { _log?.LogWarning(ex, "sklei: подія {Key} не оброблена", e.PicKey); }
        }
    }

    void Handle(SkleiSolved e)
    {
        var solverKey = Auth.NickKey(e.Solver);
        if (e.Author is not { } author || Auth.NickKey(author) == solverKey || solverKey.Length == 0) return;

        // «Чужими руками»: малюнок друга чи чужа своя картинка.
        int friends;
        lock (_gate)
        {
            friends = _friends.GetValueOrDefault(solverKey) + 1;
            _friends[solverKey] = friends;
        }
        _db?.Exec("""
            INSERT INTO sklei_stats(nick_key, nick, friends) VALUES($k, $n, 1)
            ON CONFLICT(nick_key) DO UPDATE SET friends = friends + 1, nick = excluded.nick
            """, ("$k", solverKey), ("$n", e.Solver));
        if (friends >= FriendsForAch) Unlock?.Invoke(e.Solver, "sklei-friends");

        if (e.Kind != SkleiKind.Own || !long.TryParse(e.PicKey.AsSpan(SkleiKind.Own.Length + 1), out var id)) return;
        int solved;
        lock (_gate)
        {
            var pic = _own.FirstOrDefault(o => o.Id == id);
            if (pic is null) return;                          // зняли, поки грали
            solved = pic.Solved + 1;
            _own = [.. _own.Select(o => o.Id == id ? o with { Solved = solved } : o)];
            Publish();
        }
        _db?.Exec("UPDATE sklei_pics SET solved = solved + 1 WHERE id = $id", ("$id", id));
        Grant?.Invoke(author, 1, $"Склей глек — {e.Solver} склав твою картинку", $"sklei-author:{e.Ref}:{solverKey}");
        if (solved >= AuthorForAch) Unlock?.Invoke(author, "sklei-author");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var next = _clock.UtcNow + ArtRefresh;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(30));
                try { await _events.Reader.WaitToReadAsync(wait.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                Drain();
                if (_clock.UtcNow >= next) { RefreshArts(); next = _clock.UtcNow + ArtRefresh; }
            }
        }
        catch (OperationCanceledException) { }
        Drain();
    }

    // =========================================================================================

    void Publish() => _ownLive = [.. _own.Where(o => !o.Hidden)];

    void TryDelete(string file)
    {
        if (!FileName().IsMatch(file)) return;
        try { File.Delete(Path.Combine(_dir, file)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log?.LogWarning(ex, "sklei: {File} не прибрався", file); }
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
