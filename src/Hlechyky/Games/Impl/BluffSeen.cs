using System.Globalization;
using Hlechyky.Games.Economy;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Пам'ять «Байкарів»: хто яке питання вже бачив і коли — щоб у наступній партії були свіжі. Копія
/// <see cref="SkilkySeen"/> під своєю таблицею <c>bluff_seen</c> (файл чужої гри не узагальнюємо).
/// <para>
/// Відмінність від «Скільки?»: база ніколи не чіпається під замком кімнати. І запис (<see cref="Mark"/>, з тика), і
/// читання (<see cref="Prefetch"/>, коли стіл показують у лобі) ідуть фоном — однією чергою на пулі потоків, тож
/// читання бачить усе, що записали перед ним. <see cref="LastSeen"/> (його кличе <c>Start()</c>) бере лише те, що вже
/// підтяглось, плюс позначки самого столу в пам'яті: «Ще раз» не повторить щойно бачене, навіть коли фоновий запис
/// ще не встиг, а стіл, що встиг натиснути «Почати» раніше за базу, просто зіграє без старої пам'яті.
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

    readonly Lock _own = new();
    /// <summary>Позначки цього столу: питання → (ніки, коли). Питань за вечір — десятки, тож не чистимо.</summary>
    readonly Dictionary<string, (string[] Nicks, DateTimeOffset At)> _mine = new(StringComparer.Ordinal);
    /// <summary>Прочитане з бази для кожного ніка: питання → коли бачив востаннє; null — ще в дорозі.</summary>
    readonly Dictionary<string, Dictionary<string, DateTimeOffset>?> _byNick = new(StringComparer.Ordinal);

    /// <summary>Коли допишеться й дочитається все, що вже стоїть у черзі (для тестів).</summary>
    public static Task Idle
    {
        get { lock (Gate) return _tail; }
    }

    public static string NickKey(string nick) => EconomyStore.Key(nick);

    static void Enqueue(Action work)
    {
        lock (Gate) _tail = _tail.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>
    /// Підтягнути з бази фоном пам'ять цих ніків (тих, кого ще не читали). Кличе гра, коли стіл показують у лобі:
    /// поки люди сідають і тиснуть «Почати», база встигає.
    /// </summary>
    public void Prefetch(IEnumerable<string> nickKeys)
    {
        if (db is null) return;
        List<string>? need = null;
        lock (_own)
            foreach (var key in nickKeys)
                if (_byNick.TryAdd(key, null)) (need ??= []).Add(key);
        if (need is null) return;
        Enqueue(() =>
        {
            var read = Read(need);
            lock (_own)
                foreach (var key in need) _byNick[key] = read.TryGetValue(key, out var seen) ? seen : [];
        });
    }

    /// <summary>
    /// Для кожного питання, яке бачив хоч хтось із цих гравців, — коли його бачили востаннє. Лише з пам'яті (у базу не
    /// ходить): прочитане фоном + позначки цього столу. Кого ще не читали — просимо прочитати на наступний раз.
    /// </summary>
    public Dictionary<string, DateTimeOffset> LastSeen(IReadOnlyCollection<string> nickKeys)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        if (nickKeys.Count == 0) return result;
        var missing = false;
        lock (_own)
            foreach (var key in nickKeys)
            {
                if (!_byNick.TryGetValue(key, out var seen)) missing = true;
                if (seen is null) continue;
                foreach (var (q, at) in seen)
                    if (!result.TryGetValue(q, out var was) || was < at) result[q] = at;
            }
        if (missing) Prefetch(nickKeys);
        foreach (var (q, (nicks, at)) in _mine)
            if (nicks.Any(nickKeys.Contains) && (!result.TryGetValue(q, out var was) || was < at)) result[q] = at;
        return result;
    }

    /// <summary>Ці гравці щойно побачили це питання. Запис — фоном, у черзі за попередніми.</summary>
    public void Mark(IReadOnlyCollection<string> nickKeys, BluffQuestion question, DateTimeOffset now)
    {
        if (nickKeys.Count == 0) return;
        var keys = nickKeys.ToArray();
        var q = question.Key;
        _mine[q] = (keys, now);
        if (db is null) return;
        Enqueue(() => Write(keys, q, now));
    }

    /// <summary>Пам'ять цих ніків із бази: нік → (питання → коли). Будь-яка біда — порожньо.</summary>
    Dictionary<string, Dictionary<string, DateTimeOffset>> Read(List<string> nickKeys)
    {
        var result = new Dictionary<string, Dictionary<string, DateTimeOffset>>(StringComparer.Ordinal);
        if (db is null || nickKeys.Count == 0) return result;
        try
        {
            db.With(c =>
            {
                Ensure(c);
                using var cmd = c.CreateCommand();
                var names = new List<string>(nickKeys.Count);
                for (var i = 0; i < nickKeys.Count; i++)
                {
                    var name = "$n" + i.ToString(CultureInfo.InvariantCulture);
                    cmd.Parameters.AddWithValue(name, nickKeys[i]);
                    names.Add(name);
                }
                cmd.CommandText = $"SELECT nick_key, q_key, seen_at FROM bluff_seen WHERE nick_key IN ({string.Join(",", names)})";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (!result.TryGetValue(r.GetString(0), out var seen))
                        result[r.GetString(0)] = seen = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
                    seen[r.GetString(1)] = DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                }
                return 0;
            });
        }
        catch (Exception)
        {
            result.Clear();
        }
        return result;
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
                // UTC і «O»: однакова довжина рядка, тож рядки порівнюються як час.
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
