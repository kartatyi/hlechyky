using System.Text.Json;
using Hlechyky.Games.Economy;

namespace Hlechyky.Games.Impl;

/// <summary>Сітка одного гравця за сьогодні: його спроби (слова) і чи грає він у складному режимі ✦.</summary>
public sealed record WordleGrid(string Nick, IReadOnlyList<string> Guesses, bool Hard);

/// <summary>
/// Сітки друзів у Глек-слові (пакет 4): хто сьогодні що писав. Кімната гри звертається лише до пам'яті (під замком —
/// жодного SQLite): сьогоднішні стани <c>game_state</c> (<c>daily:wordle:день:%</c>) дочитуються фоном, а кожну
/// свіжу спробу кімната докладає сама (<see cref="Note"/>), як <see cref="DailyCard.Note"/>. Слова тут лежать
/// відкрито — що з них потрапить у вид (лише кольори чи й літери), вирішує гра: до мого кінця дня — лише G/Y/B.
/// </summary>
public sealed class WordleFriends(Db? db, IClock clock)
{
    /// <summary>Як часто перечитувати день із бази: спроби з цього процесу приходять і так, це — страховка.</summary>
    static readonly TimeSpan Stale = TimeSpan.FromMinutes(10);

    readonly object _sync = new();
    string _day = "";
    readonly Dictionary<string, WordleGrid> _grids = new(StringComparer.Ordinal);
    bool _loaded;
    DateTimeOffset _loadedAt;
    int _loading;

    /// <summary>Прогріти день заздалегідь (з Configure — поза замком). Не чекає на базу.</summary>
    public void Warm() => Today();

    /// <summary>Сьогоднішні сітки з пам'яті (порожньо, поки день дочитується).</summary>
    public IReadOnlyList<WordleGrid> Today()
    {
        var day = Days.Today(clock);
        lock (_sync)
        {
            Roll(day);
            if (db is not null && (!_loaded || clock.UtcNow - _loadedAt > Stale) && Interlocked.CompareExchange(ref _loading, 1, 0) == 0)
                _ = Task.Run(() => Load(day));
            return [.. _grids.Values];
        }
    }

    /// <summary>Кімната щойно прийняла спробу (чи відкрила збережений день): докласти в пам'ять. Чужий день — мимо.</summary>
    public void Note(string day, string nick, IReadOnlyList<string> guesses, bool hard)
    {
        if (string.IsNullOrWhiteSpace(nick) || day != Days.Today(clock)) return;
        lock (_sync)
        {
            Roll(day);
            Put(EconomyStore.Key(nick), new WordleGrid(nick, [.. guesses], hard));
        }
    }

    /// <summary>Лише для тестів: дочитати день із бази зараз.</summary>
    public void LoadNow() => Load(Days.Today(clock));

    void Roll(string day)
    {
        if (_day == day) return;
        _day = day;
        _grids.Clear();
        _loaded = false;
    }

    /// <summary>Довший список спроб перекриває коротший: спроби лише додаються, тож довший — новіший.</summary>
    void Put(string key, WordleGrid g)
    {
        if (_grids.TryGetValue(key, out var was) && was.Guesses.Count > g.Guesses.Count) return;
        _grids[key] = g;
    }

    void Load(string day)
    {
        try
        {
            var prefix = $"daily:{WordleSetup.Game}:{day}:";
            var found = db!.With(c =>
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    SELECT g.key, g.json,
                        (SELECT d.nick FROM daily_results d WHERE d.game = $g AND d.nick_key = substr(g.key, $len + 1)
                         ORDER BY d.day DESC LIMIT 1)
                    FROM game_state g WHERE substr(g.key, 1, $len) = $p
                    """;
                cmd.Parameters.AddWithValue("$g", WordleSetup.Game);
                cmd.Parameters.AddWithValue("$p", prefix);
                cmd.Parameters.AddWithValue("$len", prefix.Length);
                using var r = cmd.ExecuteReader();
                var list = new List<(string Key, WordleGrid Grid)>();
                while (r.Read())
                {
                    var key = r.GetString(0)[prefix.Length..];
                    var nick = r.IsDBNull(2) ? key : r.GetString(2);
                    if (Parse(r.GetString(1), day, nick) is { } grid) list.Add((key, grid));
                }
                return list;
            });
            lock (_sync)
            {
                if (_day.Length > 0 && _day != day) return;   // день уже перекинувся — ці стани вчорашні
                Roll(day);
                foreach (var (key, grid) in found) Put(key, grid);
                _loaded = true;
                _loadedAt = clock.UtcNow;
            }
        }
        catch (Exception) { /* сітки друзів — радість, а не облік: без них гра однаково йде */ }
        finally { Interlocked.Exchange(ref _loading, 0); }
    }

    /// <summary>Збережений стан дня Глек-слова → сітка. Старий формат без <c>hard</c> — звичайний режим.</summary>
    internal static WordleGrid? Parse(string json, string day, string nick)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("day", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() != day) return null;
            if (!root.TryGetProperty("guesses", out var g) || g.ValueKind != JsonValueKind.Array) return null;
            var guesses = g.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => Words.Normalize(x.GetString() ?? "")).OfType<string>().Where(w => w.Length == Wordle.Len)
                .Take(Wordle.MaxTries).ToList();
            if (guesses.Count == 0) return null;
            var hard = root.TryGetProperty("hard", out var h) && h.ValueKind == JsonValueKind.True;
            return new WordleGrid(nick, guesses, hard);
        }
        catch (JsonException) { return null; }
    }
}
