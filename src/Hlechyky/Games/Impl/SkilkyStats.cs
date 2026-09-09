using System.Globalization;

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

    long Read(string key) => key switch
    {
        "plays7d" => Scalar("SELECT COUNT(*) FROM plays WHERE started_at >= $s", ("$s", Since(7))),
        "plays30d" => Scalar("SELECT COUNT(*) FROM plays WHERE started_at >= $s", ("$s", Since(30))),
        "likesTotal" => Scalar("SELECT COUNT(*) FROM likes"),
        // Голосове — не трек: у списку «різних треків» його бути не повинно, зате воно має свій рядок банку.
        "tracksTotal" => Scalar("SELECT COUNT(*) FROM tracks WHERE id NOT LIKE $p", ("$p", VoicePrefix)),
        "voiceTotal" => Scalar("SELECT COUNT(*) FROM tracks WHERE id LIKE $p", ("$p", VoicePrefix)),
        // Системні рядки Журналу — це не балачки, їх пише сам сервер.
        "chatTotal" => Scalar("SELECT COUNT(*) FROM chat WHERE kind <> 'system'"),
        "minutesPlayed30d" => Scalar("""
            SELECT COALESCE(SUM(t.duration_sec), 0) / 60
            FROM plays p JOIN tracks t ON t.id = p.track_id
            WHERE p.started_at >= $s
            """, ("$s", Since(30))),
        "topRequesterCount7d" => Scalar("""
            SELECT COALESCE(MAX(n), 0) FROM (
                SELECT COUNT(*) AS n FROM plays
                WHERE source = 'user' AND requested_by IS NOT NULL AND started_at >= $s
                GROUP BY requested_by)
            """, ("$s", Since(7))),
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
