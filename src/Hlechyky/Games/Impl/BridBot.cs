namespace Hlechyky.Games.Impl;

/// <summary>
/// Бот Броду. Бачить те саме, що й людина: хто де стоїть, мокрі сліди (поки не висохли) і затоплені камені — і
/// запам'ятовує безпечні камені на <see cref="Mem"/> тиків. Ходить тим самим «step», що й людина.
/// План: пошук ушир по відомих каменях і березі → якщо відомий камінь біля того берега — туди й на берег; ні —
/// найдальший нерозвіданий камінь поруч із відомою стежкою (вперед цінніше, ніж убік, близький — ніж далекий).
/// Рівні: легкий повільний, швидко забуває й часто стрибає навмання; звичайний пам'ятає ~20 с і рідко помиляється;
/// сильний пам'ятає все, швидкий і перед стрибком у невідоме охоче чекає, поки лідер розвідає (але не вічно).
/// </summary>
public sealed class BridBot(LiveBots.Level level, int phase)
{
    const int Never = int.MinValue;
    /// <summary>Скільки тиків бот пам'ятає безпечний камінь після того, як бачив його востаннє.</summary>
    public int Mem => level switch { LiveBots.Level.Easy => 300, LiveBots.Level.Normal => 900, _ => int.MaxValue };
    public LiveBots.Level Level => level;

    object? _ford;
    int[] _seen = [];
    int _next;
    int _waitNode = -1, _waitUntil, _falls;
    // Пошук ушир без алокацій: вузли — берег (ряд −1) і камені, (Rows + 1)·Cols ≤ 14·7.
    int[] _dist = [], _first = [], _queue = [];

    /// <summary>Думає раз на два тики, боти — у різні тики.</summary>
    public bool Due(int t) => (t + phase) % 2 == 0;

    void Ensure(BridCore c)
    {
        if (ReferenceEquals(_ford, c.Ford)) return;
        _ford = c.Ford;
        var n = c.Cols * c.Rows;
        _seen = [.. Enumerable.Repeat(Never, n)];
        var nodes = (c.Rows + 1) * c.Cols;
        _dist = new int[nodes];
        _first = new int[nodes];
        _queue = new int[nodes];
        _waitNode = -1;
        _next = 0;
    }

    bool Known(BridCore c, int idx) => _seen[idx] != Never && (Mem == int.MaxValue || c.T - _seen[idx] <= Mem);

    /// <summary>Подивитись на річку: хто де стоїть (там безпечно) і де ще мокро.</summary>
    public void Observe(BridCore c)
    {
        Ensure(c);
        foreach (var r in c.Runners)
        {
            if (!r.Plays) continue;
            if (r.Jump == 0 && r.Wet == 0 && r.Y >= 0 && r.Y < c.Rows) _seen[c.Idx(r.X, r.Y)] = c.T;
            if (r.Jump > 0 && r.Fy >= 0 && r.Fy < c.Rows) _seen[c.Idx(r.Fx, r.Fy)] = c.T;
        }
        for (var i = 0; i < c.Trail.Length; i++)
            if (c.Trail[i] > c.T) _seen[i] = c.T;
    }

    /// <summary>Куди стрибнути (0..3) або −1 — стояти.</summary>
    public int Think(BridCore c, int seat, Random rng)
    {
        Ensure(c);
        var me = c.Runners[seat];
        // Шубовснув — після берега знову можна почекати лідера на тому самому камені.
        if (me.Falls != _falls) { _falls = me.Falls; _waitNode = -1; }
        if (!me.Standing) return -1;
        if (_next == 0) _next = c.T + level switch { LiveBots.Level.Easy => rng.Next(10, 21), LiveBots.Level.Normal => rng.Next(4, 11), _ => rng.Next(2, 7) };
        if (c.T < _next) return -1;
        var err = level switch { LiveBots.Level.Easy => 0.12, LiveBots.Level.Normal => 0.04, _ => 0 };
        var d = err > 0 && rng.NextDouble() < err ? Random(c, me, rng) : Plan(c, me, rng);
        if (d < 0) return -1;
        _next = c.T + BridCore.JumpTicks + level switch { LiveBots.Level.Easy => rng.Next(4, 10), LiveBots.Level.Normal => rng.Next(2, 6), _ => rng.Next(1, 3) };
        return d;
    }

    /// <summary>Навмання: будь-який стрибок, який сервер прийме.</summary>
    static int Random(BridCore c, BridRunner me, Random rng)
    {
        Span<int> ok = stackalloc int[4];
        var n = 0;
        for (var d = 0; d < 4; d++)
        {
            var (dx, dy) = BridCore.Dirs[d];
            int nx = me.X + dx, ny = me.Y + dy;
            if (ny < -1 || nx < 0 || nx >= c.Cols || (ny >= 0 && ny < c.Rows && c.Sunk[c.Idx(nx, ny)])) continue;
            ok[n++] = d;
        }
        return n == 0 ? -1 : ok[rng.Next(n)];
    }

    int Plan(BridCore c, BridRunner me, Random rng)
    {
        int cols = c.Cols, rows = c.Rows;
        Array.Fill(_dist, -1);
        var start = (me.Y + 1) * cols + me.X;
        _dist[start] = 0;
        _first[start] = -1;
        int head = 0, tail = 0;
        _queue[tail++] = start;
        var noise = level switch { LiveBots.Level.Easy => 5.0, LiveBots.Level.Normal => 2.0, _ => 0.5 };
        double best = double.MinValue;
        int bestDir = -1, bestFrom = -1;
        var exit = false;
        while (head < tail)
        {
            var u = _queue[head++];
            int ux = u % cols, uy = u / cols - 1;
            for (var d = 0; d < 4; d++)
            {
                var (dx, dy) = BridCore.Dirs[d];
                int nx = ux + dx, ny = uy + dy;
                var dir = u == start ? d : _first[u];
                if (ny == rows)
                {
                    // На той берег — з будь-якого відомого каменя останнього ряду; найближчий знайдено першим.
                    if (!exit) { exit = true; bestDir = dir; bestFrom = u; best = double.MaxValue; }
                    continue;
                }
                if (ny < -1 || nx < 0 || nx >= cols) continue;
                var v = (ny + 1) * cols + nx;
                if (ny >= 0)
                {
                    var idx = nx + ny * cols;
                    if (c.Sunk[idx]) continue;
                    if (!Known(c, idx))
                    {
                        if (exit) continue;
                        var score = ny * 4 + (d == 3 ? 2 : 0) - _dist[u] + rng.NextDouble() * noise;
                        if (score > best) { best = score; bestDir = dir; bestFrom = u; }
                        continue;
                    }
                }
                if (_dist[v] >= 0) continue;
                _dist[v] = _dist[u] + 1;
                _first[v] = dir;
                _queue[tail++] = v;
            }
        }
        if (bestDir < 0) return -1;
        // «Йде за лідером»: перед стрибком у невідоме сильний (і зрідка звичайний) чекає, поки хтось попереду розвідає.
        if (!exit && bestFrom == start && level != LiveBots.Level.Easy)
        {
            if (_waitNode != start)
            {
                _waitNode = start;
                var want = Leader(c, me) && rng.NextDouble() < (level == LiveBots.Level.Hard ? 0.7 : 0.35);
                _waitUntil = want ? c.T + (level == LiveBots.Level.Hard ? 30 : 14) : c.T;
            }
            if (c.T < _waitUntil) return -1;
        }
        return bestDir;
    }

    /// <summary>Хтось інший бреде не позаду мене — є на кого дивитись.</summary>
    static bool Leader(BridCore c, BridRunner me)
    {
        foreach (var o in c.Runners)
            if (o != me && o.Plays && o.Place == 0 && o.Wet == 0 && o.Y >= 0 && o.Y >= me.Y) return true;
        return false;
    }
}
