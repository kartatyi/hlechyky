using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Hlechyky.Games.Economy;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>Як минулий день Глек-слова пройшов у самій щоденній грі: розв'язано/ні (+ спроби) і моя сітка того дня.</summary>
public sealed record WordleDayResult(bool Solved, int Attempts, IReadOnlyList<string> Guesses);

/// <summary>
/// Мої минулі дні Глек-слова для архіву (пакет 4): <c>daily_results</c> за останні <see cref="WordleArchive.Window"/> днів
/// і мої сітки тих днів із <c>game_state</c>. Кімната архіву читає лише пам'ять (під замком — жодного SQLite), а базу
/// сервіс дочитує фоном; поки не дочитав — <see cref="Get"/> віддає null, і вид каже «секунду».
/// </summary>
public sealed class WordleHistory(Db? db, IClock clock)
{
    static readonly TimeSpan Stale = TimeSpan.FromMinutes(5);

    sealed class Entry
    {
        public string Today = "";
        public DateTimeOffset At;
        public Dictionary<string, WordleDayResult> Days = new(StringComparer.Ordinal);
    }

    readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, bool> _loading = new(StringComparer.Ordinal);

    /// <summary>Мої зіграні в щоденному дні (день → результат); null — ще дочитується (без бази — порожньо).</summary>
    public IReadOnlyDictionary<string, WordleDayResult>? Get(string nick)
    {
        var key = EconomyStore.Key(nick);
        if (key.Length == 0) return null;
        var today = Days.Today(clock);
        _cache.TryGetValue(key, out var e);
        if (db is null) return e?.Days ?? new Dictionary<string, WordleDayResult>();
        if ((e is null || e.Today != today || clock.UtcNow - e.At > Stale) && _loading.TryAdd(key, true))
            _ = Task.Run(() => Load(key, today));
        return e is not null && e.Today == today ? e.Days : null;
    }

    /// <summary>Лише для тестів: покласти в пам'ять готове (без бази) чи дочитати зараз.</summary>
    public void Put(string nick, IReadOnlyDictionary<string, WordleDayResult> days) =>
        _cache[EconomyStore.Key(nick)] = new Entry { Today = Days.Today(clock), At = clock.UtcNow, Days = new(days, StringComparer.Ordinal) };

    public void LoadNow(string nick) => Load(EconomyStore.Key(nick), Days.Today(clock));

    void Load(string key, string today)
    {
        try
        {
            var since = WordleArchive.Shift(today, -WordleArchive.Window);
            var days = new Dictionary<string, WordleDayResult>(StringComparer.Ordinal);
            db!.With(c =>
            {
                var grids = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = """
                        SELECT key, json FROM game_state
                        WHERE substr(key, 1, 13) = 'daily:wordle:' AND substr(key, 25) = $k
                        """;
                    cmd.Parameters.AddWithValue("$k", key);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var day = r.GetString(0).Substring(13, 10);
                        if (string.CompareOrdinal(day, since) < 0 || string.CompareOrdinal(day, today) >= 0) continue;
                        if (WordleFriends.Parse(r.GetString(1), day, key) is { } g) grids[day] = g.Guesses;
                    }
                }
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = """
                        SELECT day, solved, attempts FROM daily_results
                        WHERE nick_key = $k AND game = $g AND day >= $s AND day < $t
                        """;
                    cmd.Parameters.AddWithValue("$k", key);
                    cmd.Parameters.AddWithValue("$g", WordleSetup.Game);
                    cmd.Parameters.AddWithValue("$s", since);
                    cmd.Parameters.AddWithValue("$t", today);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var day = r.GetString(0);
                        days[day] = new WordleDayResult(r.GetInt32(1) == 1, r.GetInt32(2), grids.GetValueOrDefault(day) ?? []);
                    }
                }
            });
            _cache[key] = new Entry { Today = today, At = clock.UtcNow, Days = days };
        }
        catch (Exception) { /* архів — не облік: не дочитали — спробуємо з наступним видом */ }
        finally { _loading.TryRemove(key, out _); }
    }
}

/// <summary>
/// Архів Глек-слова (пакет 4): учорашні й давніші слова дня — за останні <see cref="Window"/> днів. Окрема прихована
/// соло-гра з модулем <c>wordle.js</c>: одна особиста кімната на людину (ключ <c>wordle-archive:нік</c>, БЕЗ префікса
/// <c>daily:</c> — у daily_results нічого не пишеться), а день обирають усередині (<c>open</c>): каркас не передає
/// опцій соло-грі. Без серії, табла, Журналу й черепків — ні Score, ні Award, ні Finish, ні Log. Грати можна лише дні,
/// які в щоденному я не дограв (не розв'язав і не програв); дограні — лише подивитись свою сітку того дня.
/// </summary>
public sealed class WordleArchive : Game
{
    /// <summary>На скільки днів назад сягає архів.</summary>
    public const int Window = 30;

    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    sealed record SavedDay(string[]? Guesses, bool Hard = false);
    sealed record Saved(Dictionary<string, SavedDay>? Days, string? Cur);

    sealed class Play
    {
        public readonly List<string> Guesses = [];
        public bool Hard;
    }

    readonly Dictionary<string, Play> _plays = new(StringComparer.Ordinal);
    Words? _words;
    WordleHistory? _history;
    /// <summary>Відкритий день; порожній — список днів.</summary>
    string _cur = "";

    public override GameInfo Info { get; } = new(
        "wordle-archive", "Глек-слово: архів", "архів Глек-слова", GameGroup.Solo, 1, 1,
        Start: StartMode.Immediate, Hidden: true, Private: true, Persistent: true, Score: ScoreOrder.None,
        Hint: "Слова минулих днів: догравай пропущені. Без серії й черепків", Client: "wordle");

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _words = Ctx.Services.GetService<Words>();
        _history = Ctx.Services.GetService<WordleHistory>();
    }

    /// <summary>«Ще раз» нічого не стирає: архів — шухляда, а не партія.</summary>
    public override void Start() => _words ??= Ctx.Services.GetService<Words>();

    internal static string Shift(string day, int by) =>
        DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(by).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Дні архіву від учора назад: не сьогодні, не майбутнє і не раніше за день №1.</summary>
    IEnumerable<string> ArchiveDays()
    {
        var today = Days.Today(Ctx.Clock);
        for (var i = 1; i <= Window; i++)
        {
            var d = Shift(today, -i);
            if (Days.Number(d) < 1) yield break;
            yield return d;
        }
    }

    bool InWindow(string day) => ArchiveDays().Contains(day, StringComparer.Ordinal);

    string Answer(string day) => _words?.Daily5ForDay(day) ?? "";

    IReadOnlyDictionary<string, WordleDayResult>? Mine() => Ctx.NickOf(0) is { } n ? _history?.Get(n) : null;

    static bool Over(Play p, string answer) => p.Guesses.Contains(answer) || p.Guesses.Count >= Wordle.MaxTries;

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        switch (action)
        {
            case "open":
            {
                var day = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("day", out var d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString() ?? "" : "";
                if (!InWindow(day)) return ActResult.Fail("Такого дня в архіві нема");
                _cur = day;
                return ActResult.Done;
            }
            case "back":
                _cur = "";
                return ActResult.Done;
            case "guess":
                return Guess(payload);
            case "hard":
            {
                if (_cur.Length == 0) return ActResult.Fail("Спершу обери день");
                if (Mine() is not { } mine || mine.ContainsKey(_cur)) return ActResult.Fail("Цей день зіграно ще тоді — лише подивитись");
                var p = _plays.GetValueOrDefault(_cur) ?? new Play();
                if (p.Guesses.Count > 0) return ActResult.Fail("✦ Складний режим обирають до першої спроби");
                p.Hard = Wordle.HardOn(payload);
                _plays[_cur] = p;
                return ActResult.Done;
            }
            default:
                return ActResult.Fail("Тут так не ходять");
        }
    }

    ActResult Guess(JsonElement payload)
    {
        if (_cur.Length == 0 || !InWindow(_cur)) return ActResult.Fail("Спершу обери день");
        var answer = Answer(_cur);
        if (answer.Length != Wordle.Len) return ActResult.Fail("Словника нема — архів без слів");
        if (Mine() is not { } mine) return ActResult.Fail("Ще гортаю архів — секунду");
        if (mine.ContainsKey(_cur)) return ActResult.Fail("Цей день зіграно ще тоді — лише подивитись");
        var play = _plays.GetValueOrDefault(_cur) ?? new Play();
        if (Over(play, answer)) return ActResult.Fail("Цей день у архіві вже дограно");

        if (Wordle.Word(payload) is not { } raw) return ActResult.Fail("Не зрозумів, що за слово");
        var word = Words.Normalize(raw);
        if (word is null || word.Length != Wordle.Len) return ActResult.Fail("Треба рівно п'ять українських літер");
        if (_words?.IsValid5(word) != true) return ActResult.Fail("Такого слова не знаю");
        if (play.Hard && Wordle.HardMiss(answer, play.Guesses, word) is { } miss) return ActResult.Fail(miss);

        play.Guesses.Add(word);
        _plays[_cur] = play;
        if (word == answer) return ActResult.Accept("Оце так! Узято з архіву");
        return ActResult.Done;
    }

    public override object View(int? seat)
    {
        var mine = Mine();
        var days = ArchiveDays().Select(d =>
        {
            var answer = Answer(d);
            var r = mine?.GetValueOrDefault(d);
            var p = _plays.GetValueOrDefault(d);
            var arch = p is null || p.Guesses.Count == 0 ? ""
                : p.Guesses.Contains(answer) ? "s" : p.Guesses.Count >= Wordle.MaxTries ? "x" : "p";
            return new
            {
                day = d,
                no = Days.Number(d),
                mine = r is null ? "" : r.Solved ? "s" : "x",
                a = r?.Attempts ?? 0,
                arch,
                aa = p?.Guesses.Count ?? 0,
            };
        }).ToArray();

        return new
        {
            archive = true,
            loading = mine is null,
            days,
            cur = _cur.Length == 0 ? null : DayView(_cur, mine),
            max = Wordle.MaxTries,
        };
    }

    /// <summary>
    /// Відкритий день. Зіграний ще тоді — моя тодішня сітка (лише подивитись, відповідь уже не секрет). Інакше — архівна
    /// партія: відповідь у виді лише після кінця, як у щоденному.
    /// </summary>
    object DayView(string day, IReadOnlyDictionary<string, WordleDayResult>? mine)
    {
        var answer = Answer(day);
        var done = mine?.GetValueOrDefault(day);
        IReadOnlyList<string> guesses;
        bool solved, failed, hard = false;
        if (done is not null)
        {
            guesses = done.Guesses;
            solved = done.Solved;
            failed = !done.Solved;
        }
        else
        {
            var p = _plays.GetValueOrDefault(day);
            guesses = p?.Guesses ?? [];
            hard = p?.Hard ?? false;
            solved = guesses.Contains(answer);
            failed = !solved && guesses.Count >= Wordle.MaxTries;
        }
        var over = solved || failed;
        return new
        {
            day,
            no = Days.Number(day),
            view = done is not null,
            rows = guesses.Select(w => new { word = w, marks = Wordle.Marks(answer, w) }).ToArray(),
            attempts = done is not null && guesses.Count == 0 ? done.Attempts : guesses.Count,
            solved,
            failed,
            hard,
            answer = over ? answer : null,
            keys = Wordle.KeysOf(answer, guesses),
            share = over && done is null ? Share(day, guesses, solved, hard, answer) : null,
            noWords = answer.Length != Wordle.Len,
        };
    }

    static string Share(string day, IReadOnlyList<string> guesses, bool solved, bool hard, string answer)
    {
        var sb = new StringBuilder();
        sb.Append("Глек-слово #").Append(Days.Number(day)).Append(" (архів) ")
          .Append(solved ? guesses.Count.ToString(CultureInfo.InvariantCulture) : "X").Append('/').Append(Wordle.MaxTries);
        if (hard) sb.Append(" ✦");
        foreach (var w in guesses)
        {
            sb.Append('\n');
            foreach (var m in Wordle.Marks(answer, w)) sb.Append(m switch { 'G' => "🟩", 'Y' => "🟨", _ => "⬛" });
        }
        return sb.ToString();
    }

    public override string? Save()
    {
        var days = _plays.Where(kv => kv.Value.Guesses.Count > 0 || kv.Value.Hard)
            .ToDictionary(kv => kv.Key, kv => new SavedDay([.. kv.Value.Guesses], kv.Value.Hard), StringComparer.Ordinal);
        return JsonSerializer.Serialize(new Saved(days, _cur.Length == 0 ? null : _cur), Wire);
    }

    /// <summary>Дні поза вікном архіву відкидаємо (вони вже не відкриються); спроби нормалізуємо, хвіст за відповіддю ріжемо.</summary>
    public override void Load(string json)
    {
        Saved? saved;
        try { saved = JsonSerializer.Deserialize<Saved>(json, Wire); }
        catch (JsonException) { return; }
        if (saved is null) return;
        _plays.Clear();
        foreach (var (day, sd) in saved.Days ?? [])
        {
            if (!InWindow(day)) continue;
            var answer = Answer(day);
            var p = new Play { Hard = sd.Hard };
            foreach (var raw in sd.Guesses ?? [])
            {
                if (p.Guesses.Count >= Wordle.MaxTries) break;
                if (Words.Normalize(raw) is not { Length: Wordle.Len } w) continue;
                p.Guesses.Add(w);
                if (w == answer) break;
            }
            if (p.Guesses.Count > 0 || p.Hard) _plays[day] = p;
        }
        _cur = saved.Cur is { } c && InWindow(c) ? c : "";
    }
}
