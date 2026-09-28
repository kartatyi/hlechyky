using System.Globalization;
using Hlechyky.Games.Economy;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Пам'ять «Своєї гри»: хто яку тему вже бачив на полі і коли — щоб «🎲 Мікс» (прохід №3, п. 19) брав спершу теми, яких
/// ця компанія ще не бачила. Копія <see cref="BluffSeen"/> під своєю таблицею <c>svoya_seen</c> (файл чужої гри не
/// узагальнюємо), лише ключ — тема, а не питання.
/// <para>
/// База ніколи не чіпається під замком кімнати: і запис (<see cref="Mark"/>, з тика), і читання (<see cref="Prefetch"/>,
/// коли стіл показують у лобі) ідуть фоном однією чергою. <see cref="LastSeen"/> бере лише те, що вже підтяглось, плюс
/// позначки самого столу в пам'яті. Ніколи не кидає: без бази — мовчить, і «Мікс» просто тасує теми.
/// </para>
/// </summary>
public sealed class SvoyaSeen(Db? db)
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS svoya_seen(
            nick_key TEXT NOT NULL, theme_key TEXT NOT NULL, seen_at TEXT NOT NULL, times INTEGER NOT NULL DEFAULT 1,
            PRIMARY KEY(nick_key, theme_key)) WITHOUT ROWID;
        """;

    static readonly Lock Gate = new();
    static Task _tail = Task.CompletedTask;

    volatile bool _ready;

    readonly Lock _own = new();
    /// <summary>Позначки цього столу: тема → (ніки, коли).</summary>
    readonly Dictionary<string, (string[] Nicks, DateTimeOffset At)> _mine = new(StringComparer.Ordinal);
    /// <summary>Прочитане з бази для кожного ніка: тема → коли бачив востаннє; null — ще в дорозі.</summary>
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

    /// <summary>Підтягнути з бази фоном пам'ять цих ніків (тих, кого ще не читали).</summary>
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

    /// <summary>Чи пам'ять усіх цих ніків уже підтяглась (без бази — завжди так: чекати нема чого).</summary>
    public bool Loaded(IEnumerable<string> nickKeys)
    {
        if (db is null) return true;
        lock (_own)
            foreach (var key in nickKeys)
                if (!_byNick.TryGetValue(key, out var seen) || seen is null) return false;
        return true;
    }

    /// <summary>Для кожної теми, яку бачив хоч хтось із цих гравців, — коли її бачили востаннє. Лише з пам'яті.</summary>
    public Dictionary<string, DateTimeOffset> LastSeen(IReadOnlyCollection<string> nickKeys)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        if (nickKeys.Count == 0) return result;
        var missing = false;
        lock (_own)
        {
            foreach (var key in nickKeys)
            {
                if (!_byNick.TryGetValue(key, out var seen)) missing = true;
                if (seen is null) continue;
                foreach (var (t, at) in seen)
                    if (!result.TryGetValue(t, out var was) || was < at) result[t] = at;
            }
            foreach (var (t, (nicks, at)) in _mine)
                if (nicks.Any(nickKeys.Contains) && (!result.TryGetValue(t, out var was) || was < at)) result[t] = at;
        }
        if (missing) Prefetch(nickKeys);
        return result;
    }

    /// <summary>Ці гравці щойно побачили ці теми на полі. Запис — фоном, у черзі за попередніми.</summary>
    public void Mark(IReadOnlyCollection<string> nickKeys, IReadOnlyCollection<string> themeKeys, DateTimeOffset now)
    {
        if (nickKeys.Count == 0 || themeKeys.Count == 0) return;
        var keys = nickKeys.ToArray();
        var themes = themeKeys.ToArray();
        lock (_own)
            foreach (var t in themes) _mine[t] = (keys, now);
        if (db is null) return;
        Enqueue(() => Write(keys, themes, now));
    }

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
                cmd.CommandText = $"SELECT nick_key, theme_key, seen_at FROM svoya_seen WHERE nick_key IN ({string.Join(",", names)})";
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

    void Write(string[] nickKeys, string[] themeKeys, DateTimeOffset now)
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
                    INSERT INTO svoya_seen(nick_key, theme_key, seen_at) VALUES($n, $t, $at)
                    ON CONFLICT(nick_key, theme_key) DO UPDATE SET seen_at = excluded.seen_at, times = times + 1
                    """;
                var n = cmd.Parameters.Add("$n", SqliteType.Text);
                var t = cmd.Parameters.Add("$t", SqliteType.Text);
                cmd.Parameters.AddWithValue("$at", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                foreach (var key in nickKeys)
                    foreach (var theme in themeKeys)
                    {
                        n.Value = key;
                        t.Value = theme;
                        cmd.ExecuteNonQuery();
                    }
                tx.Commit();
                return 0;
            });
        }
        catch (Exception)
        {
            // Не запам'ятали — тема колись повториться раніше, ніж могла б. Партія від цього не страждає.
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
}
