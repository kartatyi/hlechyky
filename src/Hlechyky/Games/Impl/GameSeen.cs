using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Hlechyky.Games.Economy;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «🧠 Пам'ять баченого між партіями» для будь-якої гри: хто що вже бачив (слово, трек, завдання) і коли. Одна таблиця
/// <c>game_seen</c> на всі ігри — ключ (гра, нік, що), тож переживає рестарти й деплої, і своя в кожного ніка. Узагальнення
/// <see cref="SkilkySeen"/> / <see cref="BluffSeen"/> / <see cref="SvoyaSeen"/> (ті лишились у своїх таблицях).
/// <para>
/// Під замком кімнати — лише пам'ять: <see cref="Peek"/> (хто з сидячих що бачив) і <see cref="Remember"/> (кімната
/// показала — пам'ять одразу, база фоном). Читання бази (<see cref="Prefetch"/>) і запис — фоном. Пам'ять одна на базу
/// (як у <see cref="SkilkySeen"/>), тож нові об'єкти <c>new GameSeen(db)</c> дешеві й бачать те саме.
/// </para>
/// <para>
/// Стеля — <see cref="Cap"/> останніх записів на (гра, нік): старіші фон видаляє і з бази, і з пам'яті. Ніколи не кидає:
/// без бази (тести з порожнім провайдером) мовчить, і гра вибирає, як раніше.
/// </para>
/// <para>
/// Як підключити гру: ключі сидячих — <see cref="Table"/>; у <c>CanStart</c>/<c>Start</c> — <see cref="Prefetch"/>; при
/// виборі — <see cref="Peek"/> + <see cref="Order{T}"/> (або <see cref="Pick{T}"/>); коли показали — <see cref="Remember"/>.
/// </para>
/// </summary>
public sealed class GameSeen(Db? db)
{
    /// <summary>Скільки останніх записів пам'ятаємо на (гра, нік).</summary>
    public const int Cap = 3000;

    const string Schema = """
        CREATE TABLE IF NOT EXISTS game_seen(
            game TEXT NOT NULL, nick_key TEXT NOT NULL, item TEXT NOT NULL, at TEXT NOT NULL,
            PRIMARY KEY(game, nick_key, item)) WITHOUT ROWID;
        """;

    /// <summary>Скільки людей із тих, що сидять, це бачили, і коли востаннє (найсвіжіший раз серед них).</summary>
    public readonly record struct Seen(int By, DateTimeOffset Last);

    sealed class Memo
    {
        /// <summary>гра → нік → що → коли.</summary>
        public readonly Dictionary<string, Dictionary<string, Dictionary<string, DateTimeOffset>>> ByGame = new(StringComparer.Ordinal);
        public readonly HashSet<string> Loaded = new(StringComparer.Ordinal);
        public readonly HashSet<string> Loading = new(StringComparer.Ordinal);
        public readonly ConcurrentQueue<(string Game, string[] Nicks, string[] Items, DateTimeOffset At)> Pending = new();
        public int Flushing;
        public volatile bool Ready;
    }

    static readonly ConditionalWeakTable<Db, Memo> Pool = new();

    Memo? Shared => db is null ? null : Pool.GetValue(db, _ => new Memo());

    /// <summary>Пам'ять із сервісів кімнати (<c>Ctx.Services</c>); без бази — мовчазна.</summary>
    public static GameSeen Of(IServiceProvider services) => new(services.GetService(typeof(Db)) as Db);

    /// <summary>Ключ ніка — той самий, що в економіці: без пробілів по краях і без регістру.</summary>
    public static string NickKey(string nick) => EconomyStore.Key(nick);

    /// <summary>Ключі людей, що зараз сидять за столом (боти й глядачі не рахуються), без повторів.</summary>
    public static string[] Table(IRoomContext ctx, int seats) =>
        [.. Enumerable.Range(0, seats).Where(ctx.Seated).Select(ctx.NickOf).OfType<string>()
            .Where(n => n.Length > 0).Select(NickKey).Distinct(StringComparer.Ordinal)];

    static Dictionary<string, Dictionary<string, DateTimeOffset>> GameOf(Memo m, string game)
    {
        if (!m.ByGame.TryGetValue(game, out var g)) m.ByGame[game] = g = new(StringComparer.Ordinal);
        return g;
    }

    static void Merge(Memo m, string game, IEnumerable<string> nicks, IReadOnlyCollection<string> items, DateTimeOffset at)
    {
        lock (m)
        {
            var g = GameOf(m, game);
            foreach (var n in nicks)
            {
                if (!g.TryGetValue(n, out var seen)) g[n] = seen = new(StringComparer.Ordinal);
                foreach (var it in items)
                    if (!seen.TryGetValue(it, out var was) || was < at) seen[it] = at;
            }
        }
    }

    /// <summary>
    /// Що з цієї гри бачили ці гравці: що → (скільки з них бачили, коли востаннє). Небаченого ніким у словнику нема.
    /// Миттєво, з пам'яті — годиться під замком кімнати; ще не дочитано з бази — віддає що є, а фон дочитує.
    /// </summary>
    public Dictionary<string, Seen> Peek(string game, IReadOnlyCollection<string> nickKeys)
    {
        var result = new Dictionary<string, Seen>(StringComparer.Ordinal);
        if (Shared is not { } m || nickKeys.Count == 0) return result;
        Prefetch(game);
        lock (m)
        {
            if (!m.ByGame.TryGetValue(game, out var g)) return result;
            foreach (var n in nickKeys.Distinct(StringComparer.Ordinal))
                if (g.TryGetValue(n, out var seen))
                    foreach (var (it, at) in seen)
                        result[it] = result.TryGetValue(it, out var was)
                            ? new Seen(was.By + 1, was.Last < at ? at : was.Last)
                            : new Seen(1, at);
        }
        return result;
    }

    /// <summary>Попросити фон прочитати пам'ять гри з бази, якщо ще не читав. Миттєво (лише ставить задачу).</summary>
    public void Prefetch(string game)
    {
        if (Shared is not { } m) return;
        lock (m)
            if (m.Loaded.Contains(game) || !m.Loading.Add(game)) return;
        _ = Task.Run(() => Load(m, game));
    }

    /// <summary>Прочитати пам'ять гри з бази зараз (тести й фон; під замком кімнати — ні).</summary>
    public void Warm(string game)
    {
        if (Shared is not { } m) return;
        lock (m) m.Loading.Add(game);
        Load(m, game);
    }

    void Load(Memo m, string game)
    {
        try
        {
            var rows = new List<(string N, string I, DateTimeOffset At)>();
            db!.With(c =>
            {
                Ensure(m, c);
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT nick_key, item, at FROM game_seen WHERE game = $g";
                cmd.Parameters.AddWithValue("$g", game);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    rows.Add((r.GetString(0), r.GetString(1), ParseAt(r.GetString(2))));
            });
            lock (m)
            {
                var g = GameOf(m, game);
                foreach (var (n, it, at) in rows)
                {
                    if (!g.TryGetValue(n, out var seen)) g[n] = seen = new(StringComparer.Ordinal);
                    if (!seen.TryGetValue(it, out var was) || was < at) seen[it] = at;
                }
                m.Loaded.Add(game);
            }
        }
        catch (Exception)
        {
            // Нема бази чи вона зайнята — пам'ять із того, що показали кімнати; наступний Prefetch спробує ще.
        }
        finally { lock (m) m.Loading.Remove(game); }
    }

    /// <summary>
    /// Кімната показала це цим гравцям: пам'ять — одразу (наступна партія вже знає), база — фоном. Миттєво, без I/O.
    /// </summary>
    public void Remember(string game, IReadOnlyCollection<string> nickKeys, IEnumerable<string> items, DateTimeOffset now)
    {
        if (Shared is not { } m || nickKeys.Count == 0) return;
        var its = items.Where(i => !string.IsNullOrEmpty(i)).Distinct(StringComparer.Ordinal).ToArray();
        if (its.Length == 0) return;
        var nicks = nickKeys.Distinct(StringComparer.Ordinal).ToArray();
        Merge(m, game, nicks, its, now);
        m.Pending.Enqueue((game, nicks, its, now));
        if (Interlocked.CompareExchange(ref m.Flushing, 1, 0) == 0) _ = Task.Run(() => Drain(m));
    }

    /// <summary>Тестовий хук: підсунути «бачено» лише в пам'ять (без бази й без фону) і вважати гру прочитаною.</summary>
    public void Seed(string game, IReadOnlyCollection<string> nickKeys, IEnumerable<string> items, DateTimeOffset at)
    {
        if (Shared is not { } m) return;
        Merge(m, game, nickKeys, items.ToArray(), at);
        lock (m) m.Loaded.Add(game);
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
            var touched = new HashSet<(string, string)>();
            while (m.Pending.TryDequeue(out var job))
            {
                Write(m, job.Game, job.Nicks, job.Items, job.At);
                foreach (var n in job.Nicks) touched.Add((job.Game, n));
            }
            foreach (var (game, nick) in touched) Trim(m, game, nick);
        }
        catch (Exception)
        {
            // Не записали — колись повториться раніше, ніж могло б. Партія від цього не страждає.
        }
        finally { Interlocked.Exchange(ref m.Flushing, 0); }
        // Поки звільняли прапорець, могло прилетіти ще — підхопити.
        if (!m.Pending.IsEmpty && Interlocked.CompareExchange(ref m.Flushing, 1, 0) == 0) Drain(m);
    }

    void Write(Memo m, string game, string[] nicks, string[] items, DateTimeOffset at)
    {
        try
        {
            db!.With(c =>
            {
                Ensure(m, c);
                using var tx = c.BeginTransaction();
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO game_seen(game, nick_key, item, at) VALUES($g, $n, $i, $at)
                    ON CONFLICT(game, nick_key, item) DO UPDATE SET at = MAX(at, excluded.at)
                    """;
                cmd.Parameters.AddWithValue("$g", game);
                var n = cmd.Parameters.Add("$n", SqliteType.Text);
                var i = cmd.Parameters.Add("$i", SqliteType.Text);
                // UTC і «O»: однакова довжина рядка, тож порівняння рядків у SQL — це порівняння часу.
                cmd.Parameters.AddWithValue("$at", FormatAt(at));
                foreach (var nick in nicks)
                    foreach (var item in items)
                    {
                        n.Value = nick;
                        i.Value = item;
                        cmd.ExecuteNonQuery();
                    }
                tx.Commit();
            });
        }
        catch (Exception) { }
    }

    /// <summary>Лишити на (гра, нік) лише <see cref="Cap"/> найсвіжіших — і в пам'яті, і в базі.</summary>
    void Trim(Memo m, string game, string nick)
    {
        lock (m)
        {
            if (m.ByGame.TryGetValue(game, out var g) && g.TryGetValue(nick, out var seen) && seen.Count > Cap)
                foreach (var old in seen.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                             .Skip(Cap).Select(kv => kv.Key).ToList())
                    seen.Remove(old);
        }
        try
        {
            db!.With(c =>
            {
                Ensure(m, c);
                using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    DELETE FROM game_seen WHERE game = $g AND nick_key = $n AND item NOT IN (
                        SELECT item FROM game_seen WHERE game = $g AND nick_key = $n ORDER BY at DESC, item LIMIT $cap)
                    """;
                cmd.Parameters.AddWithValue("$g", game);
                cmd.Parameters.AddWithValue("$n", nick);
                cmd.Parameters.AddWithValue("$cap", Cap);
                cmd.ExecuteNonQuery();
            });
        }
        catch (Exception) { }
    }

    /// <summary>Скільки записів у базі на (гра, нік) — для тестів і перевірок.</summary>
    public int CountInDb(string game, string nickKey)
    {
        if (Shared is not { } m) return 0;
        try
        {
            return db!.With(c =>
            {
                Ensure(m, c);
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM game_seen WHERE game = $g AND nick_key = $n";
                cmd.Parameters.AddWithValue("$g", game);
                cmd.Parameters.AddWithValue("$n", nickKey);
                return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            });
        }
        catch (Exception) { return 0; }
    }

    static void Ensure(Memo m, SqliteConnection c)
    {
        if (m.Ready) return;
        using var cmd = c.CreateCommand();
        cmd.CommandText = Schema;
        cmd.ExecuteNonQuery();
        m.Ready = true;
    }

    static string FormatAt(DateTimeOffset at) => at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    static DateTimeOffset ParseAt(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>
    /// Правило вибору: спершу те, що бачило найменше з тих, хто сидить; при рівності — бачене давніше (небачене — першим);
    /// далі — порядок <paramref name="shuffled"/> (сортування стабільне, тож тасувати треба до виклику).
    /// </summary>
    public static List<T> Order<T>(IEnumerable<T> shuffled, Func<T, string> key, IReadOnlyDictionary<string, Seen> seen) =>
        seen.Count == 0
            ? [.. shuffled]
            : [.. shuffled
                .Select(x => (X: x, S: seen.TryGetValue(key(x), out var s) ? s : default))
                .OrderBy(p => p.S.By).ThenBy(p => p.S.Last).Select(p => p.X)];

    /// <summary>
    /// Найсвіжіше одне: серед кандидатів лишає тих, у кого найменше (By, Last), і з них бере випадково. Порожня історія —
    /// просто <c>rng.Next</c> з усіх, як було.
    /// </summary>
    public static T Pick<T>(IReadOnlyList<T> candidates, Func<T, string> key, IReadOnlyDictionary<string, Seen> seen, Random rng)
    {
        if (seen.Count == 0 || candidates.Count <= 1) return candidates[rng.Next(candidates.Count)];
        var scored = candidates.Select(x => (X: x, S: seen.TryGetValue(key(x), out var s) ? s : default)).ToList();
        var best = scored.Min(p => (p.S.By, p.S.Last));
        var top = scored.Where(p => (p.S.By, p.S.Last) == best).Select(p => p.X).ToList();
        return top[rng.Next(top.Count)];
    }
}
