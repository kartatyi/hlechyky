using System.Globalization;
using Hlechyky.Games.Economy;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Пам'ять «Байкарів»: хто яке питання вже бачив і коли — щоб у наступній партії були свіжі. Копія
/// <see cref="SkilkySeen"/> під своєю таблицею <c>bluff_seen</c> (файл чужої гри не узагальнюємо).
/// <para>
/// Одна відмінність: запис (<see cref="Mark"/>) іде не під замком кімнати, а фоном — чергою на пулі потоків, по одному
/// запису за раз. Позначка ставиться з тика, а тик тримає замок; база там — зайва затримка всім за столом. Читання
/// (<see cref="LastSeen"/>) — раз на партію, у <c>Start()</c>, як у «Скільки?».
/// </para>
/// Ніколи не кидає. Без бази, із зайнятою чи покаліченою — мовчить, і питання просто тасуються.
/// </summary>
public sealed class BluffSeen(Db? db)
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS bluff_seen(
            nick_key TEXT NOT NULL, q_key TEXT NOT NULL, seen_at TEXT NOT NULL, times INTEGER NOT NULL DEFAULT 1,
            PRIMARY KEY(nick_key, q_key)) WITHOUT ROWID;
        """;

    static readonly Lock Gate = new();
    static Task _tail = Task.CompletedTask;

    volatile bool _ready;

    /// <summary>Коли допишеться все, що вже стоїть у черзі (для тестів).</summary>
    public static Task Idle
    {
        get { lock (Gate) return _tail; }
    }

    public static string NickKey(string nick) => EconomyStore.Key(nick);

    /// <summary>Для кожного питання, яке бачив хоч хтось із цих гравців, — коли його бачили востаннє.</summary>
    public Dictionary<string, DateTimeOffset> LastSeen(IReadOnlyCollection<string> nickKeys)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        if (db is null || nickKeys.Count == 0) return result;
        try
        {
            db.With(c =>
            {
                Ensure(c);
                using var cmd = c.CreateCommand();
                var names = nickKeys.Select((key, i) =>
                {
                    cmd.Parameters.AddWithValue("$n" + i.ToString(CultureInfo.InvariantCulture), key);
                    return "$n" + i.ToString(CultureInfo.InvariantCulture);
                }).ToList();
                cmd.CommandText = $"SELECT q_key, MAX(seen_at) FROM bluff_seen WHERE nick_key IN ({string.Join(",", names)}) GROUP BY q_key";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    result[r.GetString(0)] = DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                return 0;
            });
        }
        catch (Exception)
        {
            result.Clear();
        }
        return result;
    }

    /// <summary>Ці гравці щойно побачили це питання. Запис — фоном, у черзі за попередніми.</summary>
    public void Mark(IReadOnlyCollection<string> nickKeys, BluffQuestion question, DateTimeOffset now)
    {
        if (db is null || nickKeys.Count == 0) return;
        var keys = nickKeys.ToArray();
        var q = question.Key;
        lock (Gate) _tail = _tail.ContinueWith(_ => Write(keys, q, now), CancellationToken.None,
            TaskContinuationOptions.None, TaskScheduler.Default);
    }

    void Write(string[] nickKeys, string questionKey, DateTimeOffset now)
    {
        if (db is null) return;
        try
        {
            db.With(c =>
            {
                Ensure(c);
                using var tx = c.BeginTransaction();
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO bluff_seen(nick_key, q_key, seen_at) VALUES($n, $q, $at)
                    ON CONFLICT(nick_key, q_key) DO UPDATE SET seen_at = excluded.seen_at, times = times + 1
                    """;
                var n = cmd.Parameters.Add("$n", SqliteType.Text);
                cmd.Parameters.AddWithValue("$q", questionKey);
                // UTC і «O»: однакова довжина рядка, тож MAX(seen_at) у SQL — справді найсвіжіший час.
                cmd.Parameters.AddWithValue("$at", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                foreach (var key in nickKeys)
                {
                    n.Value = key;
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
                return 0;
            });
        }
        catch (Exception)
        {
            // Не запам'ятали — питання колись повториться раніше, ніж могло б. Партія від цього не страждає.
        }
    }

    void Ensure(SqliteConnection c)
    {
        if (_ready) return;
        using var cmd = c.CreateCommand();
        cmd.CommandText = Schema;
        cmd.ExecuteNonQuery();
        _ready = true;
    }

    /// <summary>
    /// Найсвіжіші для цього столу: спершу небачені ніким, далі — бачені найдавніше; серед однаково свіжих — порядок
    /// <paramref name="shuffled"/> (сортування стабільне). Без повторів, щонайбільше <paramref name="take"/>.
    /// </summary>
    public static List<T> Freshest<T>(IReadOnlyList<T> shuffled, Func<T, string> key,
        IReadOnlyDictionary<string, DateTimeOffset> lastSeen, int take) =>
        [.. shuffled
            .OrderBy(x => lastSeen.TryGetValue(key(x), out var at) ? at : DateTimeOffset.MinValue)
            .Take(take)];
}
