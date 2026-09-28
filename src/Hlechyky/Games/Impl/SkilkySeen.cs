using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Hlechyky.Games.Economy;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Пам'ять «Скільки?»: хто яке запитання вже бачив і коли. Живе в базі, тож переживає рестарти й деплої, і
/// своя в кожного ніка — хто пропустив партію, свої запитання ще побачить. Таблиця й SQL тут, як у реклами:
/// від <see cref="Db"/> беремо лише з'єднання.
/// <para>
/// Кімната з базою не говорить (правило каркаса: жодного I/O під замком кімнати): вона підглядає в пам'ять
/// (<see cref="Peek"/>) і кладе «бачили» в чергу (<see cref="Remember"/>), а читає й пише SQLite фон — як
/// у <see cref="SkilkyStats"/>. Пам'ять одна на базу й одразу знає все, що кімнати цього процесу показали.
/// </para>
/// <para>
/// Ніколи не кидає. Нема бази (тести з порожнім провайдером), зайнята чи покалічена — пам'ять мовчить, і
/// запитання просто тасуються, як раніше: повтор кращий за зламану партію.
/// </para>
/// </summary>
public sealed class SkilkySeen(Db? db)
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS skilky_seen(
            nick_key TEXT NOT NULL, q_key TEXT NOT NULL, seen_at TEXT NOT NULL, times INTEGER NOT NULL DEFAULT 1,
            PRIMARY KEY(nick_key, q_key)) WITHOUT ROWID;
        """;

    bool _ready;

    /// <summary>Спільна пам'ять на базу: хто що бачив (з бази — фоном, з кімнат — одразу) і черга на запис.</summary>
    sealed class Memo
    {
        public readonly Dictionary<string, Dictionary<string, DateTimeOffset>> ByNick = new(StringComparer.Ordinal);
        public readonly ConcurrentQueue<(string[] Nicks, string[] Keys, DateTimeOffset At)> Pending = new();
        public bool Loaded;
        public int Loading;
        public int Flushing;
    }

    static readonly ConditionalWeakTable<Db, Memo> Pool = new();

    Memo? Shared => db is null ? null : Pool.GetValue(db, _ => new Memo());

    static void Merge(Memo m, IEnumerable<string> nickKeys, IEnumerable<string> qKeys, DateTimeOffset at)
    {
        lock (m)
            foreach (var n in nickKeys)
            {
                if (!m.ByNick.TryGetValue(n, out var seen)) m.ByNick[n] = seen = new(StringComparer.Ordinal);
                foreach (var q in qKeys)
                    if (!seen.TryGetValue(q, out var was) || was < at) seen[q] = at;
            }
    }

    /// <summary>
    /// Те саме, що <see cref="LastSeen"/>, але з пам'яті — миттєво, без бази: годиться під замком кімнати. Пам'ять ще
    /// не прочитана з бази (перше «Почати» після рестарту) — віддаємо що є, а фон тим часом дочитує.
    /// </summary>
    public Dictionary<string, DateTimeOffset> Peek(IReadOnlyCollection<string> nickKeys)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        if (Shared is not { } m || nickKeys.Count == 0) return result;
        Prefetch();
        lock (m)
            foreach (var n in nickKeys)
                if (m.ByNick.TryGetValue(n, out var seen))
                    foreach (var (q, at) in seen)
                        if (!result.TryGetValue(q, out var was) || was < at) result[q] = at;
        return result;
    }

    /// <summary>Попросити фон прочитати пам'ять із бази, якщо ще не читав. Миттєво (лише ставить задачу).</summary>
    public void Prefetch()
    {
        if (Shared is not { } m || m.Loaded || Interlocked.CompareExchange(ref m.Loading, 1, 0) != 0) return;
        _ = Task.Run(() => Load(m));
    }

    /// <summary>Прочитати пам'ять із бази зараз (тести й фон; під замком кімнати — ні).</summary>
    public void Warm()
    {
        if (Shared is not { } m) return;
        Interlocked.Exchange(ref m.Loading, 1);
        Load(m);
    }

    void Load(Memo m)
    {
        try
        {
            var rows = new List<(string N, string Q, DateTimeOffset At)>();
            db!.With(c =>
            {
                Ensure(c);
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT nick_key, q_key, seen_at FROM skilky_seen";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    rows.Add((r.GetString(0), r.GetString(1),
                        DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
                return 0;
            });
            foreach (var (n, q, at) in rows) Merge(m, [n], [q], at);
            lock (m) m.Loaded = true;
        }
        catch (Exception)
        {
            // Нема бази чи вона зайнята — пам'ять із того, що показали кімнати; наступне «Почати» спробує ще.
        }
        finally { Interlocked.Exchange(ref m.Loading, 0); }
    }

    /// <summary>
    /// Кімната показала запитання цим гравцям: пам'ять — одразу (наступна партія вже знає), база — фоном.
    /// Миттєво, без I/O.
    /// </summary>
    public void Remember(IReadOnlyCollection<string> nickKeys, SkilkyQuestion question, DateTimeOffset now)
    {
        if (Shared is not { } m || nickKeys.Count == 0) return;
        var nicks = nickKeys.ToArray();
        Merge(m, nicks, [question.Key], now);
        m.Pending.Enqueue((nicks, [question.Key], now));
        if (Interlocked.CompareExchange(ref m.Flushing, 1, 0) == 0) _ = Task.Run(() => Drain(m));
    }

    /// <summary>Дописати чергу в базу зараз (тести; фон робить це сам).</summary>
    public void Flush()
    {
        if (Shared is not { } m) return;
        while (Interlocked.CompareExchange(ref m.Flushing, 1, 0) != 0) Thread.Sleep(1);
        Drain(m);
    }

    void Drain(Memo m)
    {
        try
        {
            while (m.Pending.TryDequeue(out var job))
                Write(job.Nicks, job.Keys, job.At);
        }
        finally { Interlocked.Exchange(ref m.Flushing, 0); }
        // Поки звільняли прапорець, могло прилетіти ще — підхопити.
        if (!m.Pending.IsEmpty && Interlocked.CompareExchange(ref m.Flushing, 1, 0) == 0) Drain(m);
    }

    /// <summary>Ключ ніка — той самий, що в економіці: без пробілів по краях і без регістру.</summary>
    public static string NickKey(string nick) => EconomyStore.Key(nick);

    /// <summary>
    /// Для кожного запитання, яке бачив хоч хтось із цих гравців, — коли його бачили востаннє (найсвіжіший
    /// раз серед усіх). Запитань, яких не бачив ніхто, у словнику нема.
    /// </summary>
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
                    cmd.Parameters.AddWithValue("$n" + i, key);
                    return "$n" + i;
                }).ToList();
                cmd.CommandText = $"SELECT q_key, MAX(seen_at) FROM skilky_seen WHERE nick_key IN ({string.Join(",", names)}) GROUP BY q_key";
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

    /// <summary>Ці гравці щойно побачили це запитання.</summary>
    public void Mark(IReadOnlyCollection<string> nickKeys, SkilkyQuestion question, DateTimeOffset now) =>
        Mark(nickKeys, [question], now);

    /// <summary>Ці гравці бачили ці запитання — одним записом у базу (і в пам'ять). Синхронно: фон і тести.</summary>
    public void Mark(IReadOnlyCollection<string> nickKeys, IEnumerable<SkilkyQuestion> questions, DateTimeOffset now)
    {
        if (db is null || nickKeys.Count == 0) return;
        var keys = questions.Select(q => q.Key).ToArray();
        Merge(Shared!, nickKeys, keys, now);
        Write(nickKeys, keys, now);
    }

    void Write(IReadOnlyCollection<string> nickKeys, IReadOnlyCollection<string> keys, DateTimeOffset now)
    {
        try
        {
            if (db is null) return;
            db.With(c =>
            {
                Ensure(c);
                using var tx = c.BeginTransaction();
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO skilky_seen(nick_key, q_key, seen_at) VALUES($n, $q, $at)
                    ON CONFLICT(nick_key, q_key) DO UPDATE SET seen_at = excluded.seen_at, times = times + 1
                    """;
                var n = cmd.Parameters.Add("$n", SqliteType.Text);
                var q = cmd.Parameters.Add("$q", SqliteType.Text);
                // UTC і «O»: однакова довжина рядка, тож MAX(seen_at) у SQL — це справді найсвіжіший час.
                cmd.Parameters.AddWithValue("$at", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                foreach (var qk in keys)
                    foreach (var key in nickKeys)
                    {
                        n.Value = key;
                        q.Value = qk;
                        cmd.ExecuteNonQuery();
                    }
                tx.Commit();
                return 0;
            });
        }
        catch (Exception)
        {
            // Не запам'ятали — запитання колись повториться раніше, ніж могло б. Партія від цього не страждає.
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
    /// Найсвіжіші для цього столу: спершу ті, яких не бачив ніхто, далі — бачені найдавніше. Порядок
    /// усередині однаково свіжих бере з <paramref name="shuffled"/> (сортування стабільне), тож тасувати треба
    /// до виклику. Без жодного повтору, щонайбільше <paramref name="take"/>.
    /// </summary>
    public static List<T> Freshest<T>(IReadOnlyList<T> shuffled, Func<T, string> key,
        IReadOnlyDictionary<string, DateTimeOffset> lastSeen, int take) =>
        [.. shuffled
            .OrderBy(x => lastSeen.TryGetValue(key(x), out var at) ? at : DateTimeOffset.MinValue)
            .Take(take)];
}
