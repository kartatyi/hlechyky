using Hlechyky.Games.Economy;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Слова на 4 і 6 літер для Глек-слова наввипередки (опція «Довжина слова»). П'ятилітерні — як і були, у
/// <see cref="Words"/>: щоденне слово лишається на п'ять літер, а ці списки — лише для столу в «Компанії».
/// <para>
/// Відповіді — малі ручні списки <c>uk-4.txt</c> / <c>uk-6.txt</c> (звичні загальні слова, генератор —
/// <c>data/words/make-46.py</c>). Спроби — усі словоформи потрібної довжини з великого словника
/// <c>uk-all.db</c>: їх піднімаємо в пам'ять один раз у фоні на старті сервера, бо під замком кімнати до
/// бази ходити не можна (≈ 8 тис. на 4 літери і ≈ 78 тис. на 6). Поки база не прочиталась (чи її нема
/// взагалі), приймаємо хоча б відповіді — гра від цього не падає, лише стає суворішою.
/// </para>
/// </summary>
public sealed class WordleLists
{
    public static readonly int[] Lengths = [4, 6];

    readonly Dictionary<int, string[]> _answers = [];
    readonly Dictionary<int, HashSet<string>> _answerSet = [];
    volatile Dictionary<int, HashSet<string>> _guesses = [];
    readonly ILogger? _log;

    /// <summary>Завершується, коли спроби з бази прочитано (або з'ясувалось, що бази нема).</summary>
    public Task Ready { get; }

    public WordleLists(string dir, ILogger<WordleLists>? log = null)
    {
        _log = log;
        foreach (var n in Lengths) SetAnswers(n, Read(Path.Combine(dir, $"uk-{n}.txt"), n));
        var db = Path.Combine(dir, "uk-all.db");
        Ready = File.Exists(db) ? Task.Run(() => LoadGuesses(db)) : Task.CompletedTask;
    }

    /// <summary>Для тестів: списки як є, без файлів.</summary>
    public WordleLists(IEnumerable<string> answers, IEnumerable<string>? guesses = null)
    {
        foreach (var n in Lengths) SetAnswers(n, [.. answers.Select(Words.Normalize).OfType<string>().Where(w => w.Length == n).Distinct()]);
        var g = new Dictionary<int, HashSet<string>>();
        foreach (var w in (guesses ?? []).Select(Words.Normalize).OfType<string>())
        {
            if (!g.TryGetValue(w.Length, out var set)) g[w.Length] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(w);
        }
        _guesses = g;
        Ready = Task.CompletedTask;
    }

    void SetAnswers(int n, string[] list)
    {
        _answers[n] = list;
        _answerSet[n] = new HashSet<string>(list, StringComparer.Ordinal);
    }

    /// <summary>Відповіді потрібної довжини (порожньо — такої довжини нема).</summary>
    public IReadOnlyList<string> Answers(int len) => _answers.TryGetValue(len, out var a) ? a : [];

    /// <summary>Чи можна ввести таке слово як спробу: відповідь або будь-яка словоформа з великого словника.</summary>
    public bool IsValid(string? word, int len)
    {
        if (word is null || word.Length != len) return false;
        if (_answerSet.TryGetValue(len, out var a) && a.Contains(word)) return true;
        return _guesses.TryGetValue(len, out var g) && g.Contains(word);
    }

    string[] Read(string path, int len)
    {
        try
        {
            if (!File.Exists(path))
            {
                _log?.LogWarning("Глек-слово: нема списку {Path} — довжини {Len} не буде", path, len);
                return [];
            }
            return [.. File.ReadLines(path).Select(Words.Normalize).OfType<string>().Where(w => w.Length == len).Distinct()];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning(e, "Глек-слово: не прочитав {Path}", path);
            return [];
        }
    }

    void LoadGuesses(string db)
    {
        try
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
            using var c = new SqliteConnection(cs);
            c.Open();
            var map = new Dictionary<int, HashSet<string>>();
            foreach (var n in Lengths)
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT w FROM words WHERE length(w) = $n";
                cmd.Parameters.AddWithValue("$n", n);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    if (Words.Normalize(r.GetString(0)) is { } w && w.Length == n) set.Add(w);
                map[n] = set;
            }
            _guesses = map;
            _log?.LogInformation("Глек-слово: спроби на 4 і 6 літер — {A:N0} і {B:N0}", map[4].Count, map[6].Count);
        }
        catch (Exception e) when (e is SqliteException or IOException or InvalidOperationException)
        {
            _log?.LogWarning(e, "Глек-слово: великий словник не прочитався — на 4 і 6 літер приймаю лише відповіді");
        }
    }
}
