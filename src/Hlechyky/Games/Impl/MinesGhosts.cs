using System.Collections.Concurrent;
using System.Globalization;

namespace Hlechyky.Games.Impl;

/// <summary>Найкращий забіг дня в Сапері дня: хто, за скільки спроб і часу, і ходи — «сотні секунди + дія + клітинка».</summary>
public sealed record MinesGhost(string Nick, int Attempts, int Ms, string Moves);

/// <summary>
/// «👻 Привид найшвидшого» Сапера дня: найкращий (менше спроб, потім менше часу) розв'язок дня разом із записом ходів —
/// після власного розв'язку його можна програти на своєму полі хід за ходом. Кімната звертається лише до пам'яті; таблицю
/// <c>mines_ghost</c> пишемо й читаємо фоном (перезапуск сервера посеред дня привида не губить).
/// </summary>
public sealed class MinesGhosts(Db? db, IClock clock)
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS mines_ghost(
            day TEXT PRIMARY KEY, nick TEXT NOT NULL, attempts INTEGER NOT NULL, ms INTEGER NOT NULL,
            moves TEXT NOT NULL, created_at TEXT NOT NULL) WITHOUT ROWID;
        """;

    readonly ConcurrentDictionary<string, MinesGhost> _best = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, bool> _loaded = new(StringComparer.Ordinal);
    bool _schema;

    static bool Better(MinesGhost a, MinesGhost b) => a.Attempts < b.Attempts || (a.Attempts == b.Attempts && a.Ms < b.Ms);

    /// <summary>Прогріти день (з Configure — поза замком). Не чекає на базу.</summary>
    public void Warm(string day)
    {
        if (db is null || !_loaded.TryAdd(day, true)) return;
        foreach (var old in _best.Keys.Where(d => string.CompareOrdinal(d, day) < 0).ToList()) _best.TryRemove(old, out _);
        _ = Task.Run(() => Load(day));
    }

    /// <summary>Найкращий привид дня з пам'яті (null — ще нема або не дочитано).</summary>
    public MinesGhost? Best(string day)
    {
        Warm(day);
        return _best.GetValueOrDefault(day);
    }

    /// <summary>Розв'язок дня: кращий за наявний — стає привидом (у пам'ять одразу, у базу фоном).</summary>
    public void Offer(string day, MinesGhost g)
    {
        while (true)
        {
            if (_best.TryGetValue(day, out var was))
            {
                if (!Better(g, was)) return;
                if (_best.TryUpdate(day, g, was)) break;
            }
            else if (_best.TryAdd(day, g)) break;
        }
        if (db is null) return;
        _ = Task.Run(() =>
        {
            try
            {
                db.With(c =>
                {
                    Ensure(c);
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = """
                        INSERT INTO mines_ghost(day, nick, attempts, ms, moves, created_at) VALUES($d, $n, $a, $m, $v, $t)
                        ON CONFLICT(day) DO UPDATE SET nick = $n, attempts = $a, ms = $m, moves = $v, created_at = $t
                        WHERE $a < attempts OR ($a = attempts AND $m < ms)
                        """;
                    cmd.Parameters.AddWithValue("$d", day);
                    cmd.Parameters.AddWithValue("$n", g.Nick);
                    cmd.Parameters.AddWithValue("$a", g.Attempts);
                    cmd.Parameters.AddWithValue("$m", g.Ms);
                    cmd.Parameters.AddWithValue("$v", g.Moves);
                    cmd.Parameters.AddWithValue("$t", clock.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                    cmd.ExecuteNonQuery();
                });
            }
            catch (Exception) { /* привид — забава: не записався, то й не біда */ }
        });
    }

    /// <summary>Лише для тестів: дочитати день із бази зараз.</summary>
    public void LoadNow(string day) { _loaded[day] = true; Load(day); }

    void Load(string day)
    {
        try
        {
            MinesGhost? g = null;
            db!.With(c =>
            {
                Ensure(c);
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT nick, attempts, ms, moves FROM mines_ghost WHERE day = $d";
                cmd.Parameters.AddWithValue("$d", day);
                using var r = cmd.ExecuteReader();
                if (r.Read()) g = new MinesGhost(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3));
            });
            if (g is not null)
                while (true)
                {
                    if (_best.TryGetValue(day, out var was))
                    {
                        if (!Better(g, was) || _best.TryUpdate(day, g, was)) break;
                    }
                    else if (_best.TryAdd(day, g)) break;
                }
        }
        catch (Exception) { _loaded.TryRemove(day, out _); }
    }

    void Ensure(Microsoft.Data.Sqlite.SqliteConnection c)
    {
        if (_schema) return;
        using var cmd = c.CreateCommand();
        cmd.CommandText = Schema;
        cmd.ExecuteNonQuery();
        _schema = true;
    }
}
