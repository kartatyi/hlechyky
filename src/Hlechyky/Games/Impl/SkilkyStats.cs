using System.Globalization;
using System.Runtime.CompilerServices;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Динамічні запитання «Скільки?»: ті, відповідь на які знає лише наша база радіо («скільки треків
/// зіграло за тиждень»). Свій SQL живе тут, а не в <see cref="Db"/>: той файл спільний, а ці запити
/// потрібні рівно одній грі. З'єднання беремо гачком <see cref="Db.With{T}"/> — на один короткий запит.
/// <para>
/// База може бути зайнята, стара або взагалі відсутня (тести з порожнім провайдером). Тоді відповідь — 0,
/// а гра такі запитання в партію просто не бере: питати «скільки треків» на порожній базі нецікаво.
/// </para>
/// </summary>
public sealed class SkilkyStats(Db? db, IClock clock)
{
    /// <summary>Ключі, які розуміє <see cref="Value"/>. Усе інше — 0.</summary>
    public static readonly IReadOnlyList<string> Keys =
    [
        "plays7d", "plays30d", "likesTotal", "tracksTotal",
        "voiceTotal", "chatTotal", "minutesPlayed30d", "topRequesterCount7d",
        // Ігри сайту (прохід №3, пункт 45): «ого, стільки?!» і заразом реклама інших ігор.
        "gamesTotal", "games7d", "gamesPlayers", "soloTotal", "dailySolved", "achievementsTotal", "richestEarned",
        "games:tron", "games:skilky", "games:bomber", "games:territory", "games:melody", "games:snake",
        "games:tanks", "games:pictionary", "games:duel", "games:telephone", "games:curve", "games:vohnyk",
        "games:rally", "games:bluff", "games:mafia", "games:battleship",
    ];

    /// <summary>
    /// Скільки живе вже порахуване число. Статистика радіо за кілька хвилин не змінюється, а «Ще раз» за
    /// столом трапляється часто — і кожне з восьми чисел коштує окремого з'єднання й свого COUNT(*)
    /// (по <c>tracks</c> ще й через LIKE, тобто повний скан). Тримати їх кілька хвилин дешевше, ніж
    /// змушувати «Почати» чекати на базу.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    /// <summary>Кеш на цей примірник (тобто на кімнату): між кімнатами й тестами нічого не тече.</summary>
    readonly Dictionary<string, (DateTimeOffset At, long Value)> _cache = new(StringComparer.Ordinal);

    /// <summary>Скільки це в числах прямо зараз. Ніколи не кидає — жодним винятком.</summary>
    public long Value(string? key)
    {
        if (db is null || string.IsNullOrWhiteSpace(key)) return 0;
        var now = clock.UtcNow;
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var hit) && now - hit.At < Ttl) return hit.Value;
        }

        // Ловимо все: обіцянка «ніколи не кидає» — це обіцянка грі. Стара, зайнята чи покалічена база
        // має коштувати одного нецікавого запитання, а не зламаної партії (Rooms закриє стіл).
        long value;
        try { value = Read(key); }
        catch (Exception) { value = 0; }

        lock (_cache) { _cache[key] = (now, value); }
        return value;
    }

    /// <summary>
    /// Спільні числа на всю базу: їх рахує фон, а кімната лише підглядає (<see cref="Peek"/>). Так «Почати» під замком
    /// кімнати не чекає на SQLite (правило каркаса: жодного I/O в Start/Act/Tick).
    /// </summary>
    sealed class Shared
    {
        public Dictionary<string, long> Values = new(StringComparer.Ordinal);
        public DateTimeOffset At = DateTimeOffset.MinValue;
        public int Busy;
    }

    static readonly ConditionalWeakTable<Db, Shared> Pool = new();

    /// <summary>
    /// Число з фонового кешу: миттєво, без бази. Застаріло чи ще не рахувалось — віддаємо що є (0 на першому
    /// «Почати» після рестарту: динамічне просто не потрапить у цю партію) і просимо фон перерахувати всі ключі.
    /// </summary>
    public long Peek(string? key)
    {
        if (db is null || string.IsNullOrWhiteSpace(key)) return 0;
        var shared = Pool.GetValue(db, _ => new Shared());
        Dictionary<string, long> values;
        bool stale;
        lock (shared) { values = shared.Values; stale = clock.UtcNow - shared.At >= Ttl || shared.At > clock.UtcNow; }
        if (stale && Interlocked.CompareExchange(ref shared.Busy, 1, 0) == 0)
            _ = Task.Run(() => Refresh(shared));
        return values.GetValueOrDefault(key);
    }

    /// <summary>Порахувати все зараз (тести й прогрів). Кличе фон; під замком кімнати — ні.</summary>
    public void Warm()
    {
        if (db is null) return;
        var shared = Pool.GetValue(db, _ => new Shared());
        Interlocked.Exchange(ref shared.Busy, 1);
        Refresh(shared);
    }

    void Refresh(Shared shared)
    {
        try
        {
            var fresh = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var key in Keys)
            {
                try { fresh[key] = Read(key); }
                catch (Exception) { fresh[key] = 0; }
            }
            lock (shared) { shared.Values = fresh; shared.At = clock.UtcNow; }
        }
        finally { Interlocked.Exchange(ref shared.Busy, 0); }
    }

    long Read(string key) => key switch
    {
        "plays7d" => Scalar("SELECT COUNT(*) FROM plays WHERE started_at >= $s", ("$s", Since(7))),
        "plays30d" => Scalar("SELECT COUNT(*) FROM plays WHERE started_at >= $s", ("$s", Since(30))),
        "likesTotal" => Scalar("SELECT COUNT(*) FROM likes"),
        // Голосове — не трек: у списку «різних треків» його бути не повинно, зате воно має свій рядок банку.
        "tracksTotal" => Scalar("SELECT COUNT(*) FROM tracks WHERE id NOT LIKE $p", ("$p", VoicePrefix)),
        "voiceTotal" => Scalar("SELECT COUNT(*) FROM tracks WHERE id LIKE $p", ("$p", VoicePrefix)),
        // «Написано в балачках» — людьми: рядки Журналу пише сам сервер, а Глек за вересень наговорив
        // тисячі анонсів своїх треків — з ними відповідь була б про нього, а не про нас. Рядок-заклик за стіл
        // («кличе в мафію») теж пише сервер, хоч і від імені того, хто поставив стіл.
        "chatTotal" => Scalar("SELECT COUNT(*) FROM chat WHERE kind NOT IN ('system', 'invite') AND kind NOT LIKE 'dj%'"),
        "minutesPlayed30d" => Scalar("""
            SELECT COALESCE(SUM(t.duration_sec), 0) / 60
            FROM plays p JOIN tracks t ON t.id = p.track_id
            WHERE p.started_at >= $s
            """, ("$s", Since(30))),
        // INDEXED BY — бо з індексом ніків (Db, ix_plays_requested) планувальник, аби не сортувати групи, пішов би
        // ним через усі програвання замість тижневого проміжку часу
        "topRequesterCount7d" => Scalar("""
            SELECT COALESCE(MAX(n), 0) FROM (
                SELECT COUNT(*) AS n FROM plays INDEXED BY ix_plays_started
                WHERE source = 'user' AND requested_by IS NOT NULL AND started_at >= $s
                GROUP BY requested_by)
            """, ("$s", Since(7))),
        // Партія компанії — це пара (кімната, номер партії); соло-результати й щоденні — окремо.
        "gamesTotal" => Scalar("SELECT COUNT(*) FROM (SELECT DISTINCT room_id, round FROM game_results WHERE outcome <> 'solo')"),
        "games7d" => Scalar("SELECT COUNT(*) FROM (SELECT DISTINCT room_id, round FROM game_results WHERE outcome <> 'solo' AND created_at >= $s)",
            ("$s", Since(7))),
        "gamesPlayers" => Scalar("SELECT COUNT(DISTINCT nick_key) FROM game_results"),
        "soloTotal" => Scalar("SELECT COUNT(*) FROM game_results WHERE outcome = 'solo'"),
        "dailySolved" => Scalar("SELECT COUNT(*) FROM daily_results WHERE solved = 1"),
        "achievementsTotal" => Scalar("SELECT COUNT(*) FROM achievements"),
        "richestEarned" => Scalar("SELECT COALESCE(MAX(earned), 0) FROM wallets"),
        _ when key.StartsWith("games:", StringComparison.Ordinal) => Scalar(
            "SELECT COUNT(*) FROM (SELECT DISTINCT room_id, round FROM game_results WHERE game = $g AND outcome <> 'solo')",
            ("$g", key[6..])),
        _ => 0,
    };

    static string VoicePrefix => VoiceService.Prefix + "%";

    /// <summary>Межа «останніх N днів» у тому самому форматі, у якому час лягає в базу (ISO-8601 «o»).</summary>
    string Since(int days) => clock.UtcNow.AddDays(-days).ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    long Scalar(string sql, params (string Name, object Value)[] ps) => db!.With(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        var raw = cmd.ExecuteScalar();
        return raw is null or DBNull ? 0L : Convert.ToInt64(raw, CultureInfo.InvariantCulture);
    });
}
