using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Games.Economy;

/// <summary>Скільки слів у кожному списку — для рядка в лозі при старті і для тестів.</summary>
public sealed record WordsStats(int Small5, int Guess5, int Hangman, long Full)
{
    public override string ToString() =>
        $"відповіді {Small5}, спроби {Guess5}, віселиця {Hangman}, великий словник {(Full > 0 ? Full.ToString("N0") : "нема")}";
}

/// <summary>
/// Українські словники для Глек-слова, Віселиці та Ерудита. Читає data/words при створенні
/// (синглтон, створюється на старті сервера — див. WordsSetup).
///
/// Головне правило: цей клас ніколи не падає. Нема файлу, файл без прав, крива дата, крива база —
/// порожній список або порожній рядок, попередження в лог і <see cref="Loaded"/> = false; гра сама
/// вирішує, що сказати гравцеві («Нема словника, віселиця відпочиває»).
///
/// Пам'ять: малі списки живуть у HashSet (разом ~38 тис. слів, кілька мегабайт), великий словник
/// (3.4 млн форм) — у SQLite-файлі uk-all.db, у пам'ять не читається взагалі. Файл будується один
/// раз із uk-all.txt у фоновому потоці; поки будується, <see cref="FullLoaded"/> = false і Ерудит
/// працює на малих списках (дочекатись кінця збирання можна через <see cref="FullReady"/>).
/// </summary>
public sealed class Words : IDisposable
{
    /// <summary>Українська абетка. Усе, чого тут нема — апостроф, латиниця, «ё», дефіс, цифри — робить слово невалідним.</summary>
    const string Alphabet = "абвгґдеєжзиіїйклмнопрстуфхцчшщьюя";
    static readonly SearchValues<char> Letters = SearchValues.Create(Alphabet);

    /// <summary>Сід перестановки списку відповідей. Міняти не можна: зміниться порядок слів дня.</summary>
    const int ShuffleSeed = 2026;

    /// <summary>Той самий день 1, що й у <see cref="Days.Number"/> — але без винятку на кривому рядку.</summary>
    static readonly DateOnly Epoch = new(2026, 9, 10);

    /// <summary>Довша за це спроба — точно не п'ятилітерне слово; нормалізувати таке не варто (з дроту приходить до 8 КБ).</summary>
    const int MaxGuessLength = 32;

    readonly ILogger<Words>? _log;
    readonly string _dir;
    readonly HashSet<string> _guess5 = new(StringComparer.Ordinal);
    readonly HashSet<string> _small = new(StringComparer.Ordinal);   // усе, що знаємо без великого словника
    readonly string[] _five;        // відповіді Глек-слова в порядку файлу
    readonly string[] _daily;       // ті самі, перемішані сталою перестановкою — слово дня беремо звідси
    readonly string[] _hangman;

    readonly object _dbLock = new();
    readonly TaskCompletionSource _fullReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    SqliteConnection? _db;
    long _fullCount;                // рахуємо один раз при підключенні: COUNT(*) по 3.4 млн рядків — це ~90 мс
    bool _disposed;

    public Words(ILogger<Words>? log = null) : this(Paths.Resolve("data/words"), log) { }

    public Words(string dir, ILogger<Words>? log = null)
    {
        _dir = dir;
        _log = log;

        _five = ReadList("uk-5.txt");
        _hangman = ReadList("uk-hangman.txt");
        foreach (var w in ReadList("uk-guess.txt")) _guess5.Add(w);
        foreach (var w in _five) _guess5.Add(w);   // відповідь завжди приймається як спроба

        foreach (var w in _guess5) _small.Add(w);
        foreach (var w in _hangman) _small.Add(w);

        _daily = Shuffle(_five, ShuffleSeed);

        Loaded = _five.Length > 0 && _hangman.Length > 0;
        if (!Loaded)
            _log?.LogWarning("Словники: у {Dir} нема списків (uk-5.txt / uk-hangman.txt) — словесні ігри скажуть «нема словника»", _dir);

        OpenFull();
        _log?.LogInformation("Словники: {Stats}", Stats);
    }

    /// <summary>Малі списки на місці: Глек-слово і Віселиця можуть грати.</summary>
    public bool Loaded { get; }

    /// <summary>Великий словник (uk-all.db) готовий: Ерудит приймає будь-яку словоформу, а не лише з малих списків.</summary>
    public bool FullLoaded => Volatile.Read(ref _db) is not null;

    /// <summary>
    /// Завершується, коли з великим словником усе зрозуміло: або його підключено, або його нема / не
    /// зібрався. Ніколи не кидає. Потрібен тестам і тому, хто хоче показати розмір словника не раніше,
    /// ніж він відомий; звичайним гравцям вистачає <see cref="FullLoaded"/>.
    /// </summary>
    public Task FullReady => _fullReady.Task;

    /// <summary>Розміри списків. Дешева: великий словник порахований один раз при підключенні.</summary>
    public WordsStats Stats => new(_five.Length, _guess5.Count, _hangman.Length, FullCount());

    // ---------------------------------------------------------------------------------------
    // Нормалізація
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Нижній регістр + обрізані пробіли. Повертає null, якщо в слові є щось не з української абетки:
    /// апостроф у будь-якому вигляді (' ’ ʼ ‘ `), латиниця, «ё», дефіс, цифри. Слів з апострофом у наших
    /// списках нема свідомо — інакше в Глек-слові довелось би вирішувати, чи це шоста клітинка.
    /// </summary>
    public static string? Normalize(string? word)
    {
        if (string.IsNullOrWhiteSpace(word)) return null;
        var w = word.Trim().ToLowerInvariant();
        return w.AsSpan().ContainsAnyExcept(Letters) ? null : w;
    }

    // ---------------------------------------------------------------------------------------
    // Глек-слово
    // ---------------------------------------------------------------------------------------

    /// <summary>Чи можна таке слово ввести як спробу: рівно 5 українських літер і воно є в uk-guess ∪ uk-5.</summary>
    public bool IsValid5(string? word)
    {
        // довжину дивимось до нормалізації: інакше на кожну сміттєву спробу з дроту йде копія рядка
        if (word is null || word.Length is < 5 or > MaxGuessLength) return false;
        var w = Normalize(word);
        return w is { Length: 5 } && _guess5.Contains(w);
    }

    /// <summary>
    /// Слово дня за днем Києва (<c>yyyy-MM-dd</c>): йдемо сталою перестановкою списку відповідей, тому
    /// повтор трапиться не раніше, ніж через <see cref="WordsStats.Small5"/> днів (зараз — понад чотири
    /// роки). Це та форма, якою має користуватись Глек-слово. Крива дата (наприклад зі збереженого
    /// стану) — попередження в лог і порожній рядок, а не виняток.
    /// </summary>
    public string Daily5ForDay(string? day)
    {
        if (_daily.Length == 0) return "";
        if (day is null || !DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            _log?.LogWarning("Словники: «{Day}» — не день у форматі yyyy-MM-dd, слова дня нема", day);
            return "";
        }
        var n = (long)d.DayNumber - Epoch.DayNumber;   // те саме число, що Days.Number(day) - 1
        var i = (int)(((n % _daily.Length) + _daily.Length) % _daily.Length);
        return _daily[i];
    }

    /// <summary>
    /// Слово дня за довільним сідом (наприклад <c>Days.Seed("wordle", day)</c>). Детерміноване, але
    /// гарантії «без повторів у році» тут нема: сід — це хеш, і два різні дні можуть дати той самий
    /// індекс (на 1500 відповідей це десятки повторів на рік). Лишається лише для сумісності.
    /// </summary>
    [Obsolete("Слово дня — Daily5ForDay(day): воно не повторюється роками. Daily5(seed) лишений для сумісності зі старим specs/wordle.md.")]
    public string Daily5(int seed)
    {
        if (_daily.Length == 0) return "";
        var i = (int)((uint)seed % (uint)_daily.Length);
        return _daily[i];
    }

    // ---------------------------------------------------------------------------------------
    // Віселиця
    // ---------------------------------------------------------------------------------------

    /// <summary>Випадкове слово для Віселиці в межах довжин. Порожній рядок — якщо словника нема або нічого не підійшло.</summary>
    public string RandomHangman(Random rng, int minLen = 5, int maxLen = 12)
    {
        ArgumentNullException.ThrowIfNull(rng);
        if (_hangman.Length == 0) return "";
        if (minLen > maxLen) (minLen, maxLen) = (maxLen, minLen);

        // спершу тицяємо навмання (майже завжди влучаємо з першого разу — весь список у 5..12),
        // і лише як не пощастило, збираємо повний перелік потрібних довжин
        for (var attempt = 0; attempt < 24; attempt++)
        {
            var w = _hangman[rng.Next(_hangman.Length)];
            if (w.Length >= minLen && w.Length <= maxLen) return w;
        }
        var fit = _hangman.Where(w => w.Length >= minLen && w.Length <= maxLen).ToArray();
        return fit.Length == 0 ? "" : fit[rng.Next(fit.Length)];
    }

    // ---------------------------------------------------------------------------------------
    // Ерудит
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Чи є таке слово в словнику. З великим словником — будь-яка словоформа; без нього — лише те, що
    /// в малих списках (тоді Ерудит вмикає режим «малий словник» з оскарженням, див. specs/scrabble.md).
    /// </summary>
    public bool IsWord(string? word)
    {
        var w = Normalize(word);
        if (w is null || w.Length < 2) return false;
        if (_small.Contains(w)) return true;

        if (Volatile.Read(ref _db) is null) return false;
        lock (_dbLock)
        {
            var db = _db;               // перечитуємо під локом: закрити з'єднання можна тільки тут
            if (db is null) return false;
            try
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT 1 FROM words WHERE w = $w";
                cmd.Parameters.AddWithValue("$w", w);
                return cmd.ExecuteScalar() is not null;
            }
            catch (Exception e) when (e is SqliteException or InvalidOperationException or ObjectDisposedException)
            {
                _log?.LogWarning(e, "Словники: великий словник відповів помилкою, далі працюємо на малих списках");
                db.Dispose();
                Interlocked.Exchange(ref _fullCount, 0);
                Volatile.Write(ref _db, null);
                return false;
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // Читання і великий словник
    // ---------------------------------------------------------------------------------------

    string[] ReadList(string name)
    {
        var path = Path.Combine(_dir, name);
        if (!File.Exists(path))
        {
            _log?.LogWarning("Словники: нема файлу {Path}", path);
            return [];
        }
        try
        {
            var list = new List<string>(4096);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines(path))
            {
                var w = Normalize(line);
                if (w is not null && seen.Add(w)) list.Add(w);
            }
            return [.. list];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning(e, "Словники: не прочитав {Path}", path);
            return [];
        }
    }

    /// <summary>Перестановка Фішера–Єйтса зі сталим сідом: порядок слів дня однаковий на будь-якій машині.</summary>
    static string[] Shuffle(string[] source, int seed)
    {
        var a = (string[])source.Clone();
        var rng = new Random(seed);
        for (var i = a.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (a[i], a[j]) = (a[j], a[i]);
        }
        return a;
    }

    /// <summary>
    /// Підключає uk-all.db, якщо він є; якщо є лише uk-all.txt — будує базу у фоні (це кілька десятків
    /// секунд на 3.4 млн слів, тримати на цьому старт сервера нема сенсу). У будь-якому разі врешті
    /// завершує <see cref="FullReady"/>.
    /// </summary>
    void OpenFull()
    {
        string db, txt;
        try
        {
            db = Path.Combine(_dir, "uk-all.db");
            txt = Path.Combine(_dir, "uk-all.txt");
            if (File.Exists(db)) { Connect(db); _fullReady.TrySetResult(); return; }
            if (!File.Exists(txt)) { _fullReady.TrySetResult(); return; }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log?.LogWarning(e, "Словники: не подивився на великий словник у {Dir}", _dir);
            _fullReady.TrySetResult();
            return;
        }

        _log?.LogInformation("Словники: будую великий словник із {Txt} у фоні", txt);
        _ = Task.Run(() =>
        {
            try
            {
                // ім'я з номером процесу: два сервери (чи сервер і тести) на одному data/words не б'ються
                var tmp = $"{db}.{Environment.ProcessId}.building";
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    try { File.Delete(tmp + suffix); } catch (IOException) { /* залишки минулої спроби */ }

                var sw = Stopwatch.StartNew();
                var n = Build(tmp, txt);
                File.Move(tmp, db, overwrite: true);
                foreach (var suffix in new[] { "-wal", "-shm" })
                    try { File.Delete(tmp + suffix); } catch (IOException) { /* уже прибрано */ }

                Connect(db);
                _log?.LogInformation("Словники: великий словник готовий — {N:N0} слів за {Sec:0.0} с", n, sw.Elapsed.TotalSeconds);
            }
            catch (Exception e)
            {
                // ловимо все: у фоновому потоці цей виняток нікому спостерігати, а Ерудит має грати далі
                _log?.LogWarning(e, "Словники: не вийшло зібрати великий словник — Ерудит гратиме на малих списках");
            }
            finally
            {
                _fullReady.TrySetResult();
            }
        });
    }

    static long Build(string dbPath, string txtPath)
    {
        // Pooling=false: інакше пул притримає файл відкритим і File.Move нижче впаде
        var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
        using var c = new SqliteConnection(cs);
        c.Open();
        using (var pragma = c.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;" +
                                 "CREATE TABLE IF NOT EXISTS words(w TEXT PRIMARY KEY) WITHOUT ROWID;";
            pragma.ExecuteNonQuery();
        }

        long n = 0;
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO words(w) VALUES($w)";
        var p = cmd.Parameters.Add("$w", SqliteType.Text);
        foreach (var line in File.ReadLines(txtPath))
        {
            var w = Normalize(line);
            if (w is null || w.Length < 2) continue;
            p.Value = w;
            cmd.ExecuteNonQuery();
            n++;
        }
        tx.Commit();
        return n;
    }

    void Connect(string path)
    {
        SqliteConnection? c = null;
        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString();
            c = new SqliteConnection(cs);
            c.Open();

            // COUNT(*) по WITHOUT ROWID таблиці на 3.4 млн рядків — це повний обхід дерева (~90 мс).
            // Робимо його рівно раз тут, щоб Stats потім була безкоштовна і не морозила IsWord.
            long n;
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM words";
                n = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            lock (_dbLock)
            {
                if (_disposed) { c.Dispose(); return; }
                _db?.Dispose();
                Interlocked.Exchange(ref _fullCount, n);   // спершу лічильник, потім видимість бази
                Volatile.Write(ref _db, c);
            }
        }
        catch (Exception e) when (e is SqliteException or InvalidOperationException)
        {
            c?.Dispose();
            _log?.LogWarning(e, "Словники: не відкрив {Path}", path);
        }
    }

    long FullCount() => Volatile.Read(ref _db) is null ? 0 : Interlocked.Read(ref _fullCount);

    /// <summary>Закриває великий словник. DI-хост кличе сам; у тестах — using.</summary>
    public void Dispose()
    {
        lock (_dbLock)
        {
            _disposed = true;
            _db?.Dispose();
            _db = null;
            Interlocked.Exchange(ref _fullCount, 0);
        }
    }
}
