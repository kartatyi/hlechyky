namespace Hlechyky.Games.Impl;

/// <summary>
/// Бот Тонкого льоду. Двічі думає: рідко — план (пошук у ширину по своєму ярусу з часом тріщин: куди йти, щоб
/// довше лишався цілий лід), часто — кермо (сектор на центр наступної клітинки шляху, стрибок над діркою).
/// Ходить лише вздовж рядків і стовпців: відрізок від будь-якої точки клітинки до центру сусідньої не зачіпає
/// третьої клітинки, тож бот не зрізає кут через дірку.
/// Легкий думає повільно й часом помиляється, звичайний тримається великого цілого льоду й перестрибує дірки,
/// сильний ще й рахує, куди дійде раніше за суперників (як у «Троні»), — і цим відрізає їм дорогу.
/// </summary>
public sealed class ThiniceBot(LiveBots.Level level, int phase)
{
    /// <summary>Скільки тиків клітинка під ногами (для запасу в часі тріщин): 100 / 16 ≈ 6,25.</summary>
    const int TicksPerCell = 7;
    /// <summary>Кермо відпускає точку шляху, коли до її центру ближче за це.</summary>
    const int Reach = 30;
    /// <summary>До краю дірки, з якого відштовхуємось: 24 + 192 польоту = приземлення на 68 у дальню клітинку.</summary>
    const int TakeOff = 24;

    readonly int _plan = level switch { LiveBots.Level.Easy => 8, LiveBots.Level.Normal => 4, _ => 2 };
    readonly int _steer = level == LiveBots.Level.Easy ? 2 : 1;
    /// <summary>Скільки тиків тріщини лишити собі на вихід з центру плитки (до краю — 3 тики); 0 — не стоїть.</summary>
    readonly int _linger = level switch { LiveBots.Level.Easy => 0, LiveBots.Level.Normal => 15, _ => 7 };
    readonly List<(int Cell, bool Jump)> _path = [];
    int _tier = -1;
    int _sector = -1;

    // Буфери пошуку — без алокацій на тик.
    int[] _dist = [], _from = [], _odist = [], _q = [], _region = [], _mine = [];
    bool[] _jumpTo = [];

    public LiveBots.Level Level => level;

    /// <summary>Що зробити цього тика: сектор (−1 — стояти) і чи стрибати.</summary>
    public (int Sector, bool Jump) Think(ThiniceCore c, int seat, Random rng)
    {
        var b = c.Bodies[seat];
        if (!b.In || b.Fall > 0 || b.Air > 0) { _path.Clear(); _tier = -1; return (_sector = -1, false); }
        // Рушив з плитки — не передумує на півдорозі (на виході лишилось кілька тиків, розворот — це падіння):
        // план знову, лише коли стоїть, шлях скінчився чи наступну клітинку вже не пройти.
        var cur = c.CellOf(b);
        var g = c.Ice[b.Tier];
        var committed = _linger > 0 && _path.Count > 0 && cur != _path[0].Cell && !Near(b, cur, c.N)
            && Passable(g[_path[0].Cell], 1);
        if (b.Tier != _tier || _path.Count == 0 || ((c.T + phase) % _plan == 0 && !committed)) Plan(c, seat, rng);
        if ((c.T + phase) % _steer != 0) return (_sector, false);
        return Steer(c, b, rng);
    }

    (int, bool) Steer(ThiniceCore c, ThiniceBody b, Random rng)
    {
        var n = c.N;
        var cur = c.CellOf(b);
        // Відпускаємо пройдені точки: стоїмо в ній і близько до центру.
        while (_path.Count > 0 && _path[0].Cell == cur && !_path[0].Jump && Near(b, cur, n)) _path.RemoveAt(0);
        if (_path.Count == 0) return (_sector = -1, false);
        var (next, jump) = _path[0];
        // Кожна нова плитка дає лише секунду, тож бігти без потреби — палити лід. Тямущий стоїть у центрі своєї
        // тріснутої плитки, поки вона тримає, і ступає на свіжу в останню мить; легкий біжить завжди.
        var here = c.Ice[b.Tier][cur];
        if (_linger > 0 && here > _linger && Near(b, cur, n)) return (_sector = -1, false);
        var tx = next % n * ThiniceCore.Sub + ThiniceCore.Sub / 2;
        var ty = next / n * ThiniceCore.Sub + ThiniceCore.Sub / 2;
        if (jump)
        {
            // Стрибок — прямо вздовж осі: напрям з різниці клітинок, відштовхуємось біля краю дірки.
            var dx = Math.Sign(next % n - cur % n);
            var dy = Math.Sign(next / n - cur / n);
            _sector = dx > 0 ? 0 : dy > 0 ? 4 : dx < 0 ? 8 : 12;
            var edge = dx > 0 ? (cur % n + 1) * ThiniceCore.Sub - b.X
                : dx < 0 ? b.X - cur % n * ThiniceCore.Sub
                : dy > 0 ? (cur / n + 1) * ThiniceCore.Sub - b.Y
                : b.Y - cur / n * ThiniceCore.Sub;
            if (edge <= TakeOff && b.Cd == 0)
            {
                _path.RemoveAt(0);
                return (_sector, true);
            }
            return (_sector, false);
        }
        var s = ThiniceCore.SectorOf(Math.Atan2(ty - b.Y, tx - b.X) * 180 / Math.PI);
        // Легкий часом смикає кермо не туди.
        if (level == LiveBots.Level.Easy && rng.Next(10) == 0) s = (s + (rng.Next(2) == 0 ? 1 : 15)) % 16;
        return (_sector = s, false);
    }

    static bool Near(ThiniceBody b, int cell, int n)
    {
        var dx = cell % n * ThiniceCore.Sub + ThiniceCore.Sub / 2 - b.X;
        var dy = cell / n * ThiniceCore.Sub + ThiniceCore.Sub / 2 - b.Y;
        return dx * dx + dy * dy <= Reach * Reach;
    }

    void Ensure(int size)
    {
        if (_dist.Length == size) return;
        _dist = new int[size]; _from = new int[size]; _odist = new int[size]; _q = new int[size]; _region = new int[size]; _mine = new int[size];
        _jumpTo = new bool[size];
    }

    /// <summary>Чи пройде центр клітинку, дійшовши за <paramref name="steps"/> кроків: ціла — так; тріщить — якщо встигне.</summary>
    bool Passable(int v, int steps)
    {
        if (v == 0) return true;
        if (v == ThiniceCore.Gone) return false;
        // Легкий не рахує тріщин як слід — бачить «ще стоїть».
        var spare = level == LiveBots.Level.Easy ? 2 : TicksPerCell + 3;
        return v > steps * TicksPerCell + spare;
    }

    void Plan(ThiniceCore c, int seat, Random rng)
    {
        var b = c.Bodies[seat];
        var n = c.N;
        var size = n * n;
        Ensure(size);
        _tier = b.Tier;
        _path.Clear();
        var g = c.Ice[b.Tier];
        var start = c.CellOf(b);
        Bfs(g, n, start, b.Cd == 0 && level != LiveBots.Level.Easy || (level == LiveBots.Level.Easy && b.Cd == 0 && rng.Next(2) == 0));
        Regions(g, n);
        if (level == LiveBots.Level.Hard)
        {
            Rivals(c, seat, g, n);
            // Суперник поруч на тому ж ярусі — бій за лід: крок за «Троном». Сам — мете лід, як звичайний.
            var close = false;
            for (var v = 0; v < size && !close; v++) close = _odist[v] >= 0 && _odist[v] <= 4 && _dist[v] >= 0 && _dist[v] <= 4;
            if (close && Territory(c, b, g, n, start, rng) is var step and >= 0)
            {
                _path.Add((step, false));
                return;
            }
        }

        var best = -1;
        var bestScore = double.MinValue;
        for (var v = 0; v < size; v++)
        {
            if (v == start || _dist[v] < 0 || g[v] != 0) continue;
            var d = _dist[v];
            var score = Math.Min(_region[v], 60) * 1.0 + Local(g, n, v) * 2.0 - d * 1.5;
            // Інші на тому ж ярусі тріщать лід навколо себе — тримаємось оддалік.
            foreach (var o in c.Bodies)
            {
                if (o == b || !o.Plays || !o.In || o.Tier != b.Tier || o.Fall > 0) continue;
                var oc = c.CellOf(o);
                var md = Math.Abs(oc % n - v % n) + Math.Abs(oc / n - v / n);
                if (md <= 2) score -= (3 - md) * 3;
            }
            if (level == LiveBots.Level.Hard && _odist[v] >= 0)
            {
                // Клітинки, куди дійду раніше за суперника й поруч із ним, — межа його льоду: пройти там = відрізати.
                if (d < _odist[v] && _odist[v] <= 3) score += 6;
                else if (_odist[v] <= d) score -= 2;
            }
            if (level == LiveBots.Level.Easy) score += rng.NextDouble() * 12;
            else score += rng.NextDouble() * 1.5;
            if (score > bestScore) { bestScore = score; best = v; }
        }
        if (best < 0) best = Escape(g, n, start);
        if (best < 0) return;
        // Шлях від цілі назад до старту.
        for (var v = best; v != start && v >= 0; v = _from[v]) _path.Add((v, _jumpTo[v]));
        _path.Reverse();
    }

    /// <summary>
    /// Сильний: з кожного сусіднього кроку — скільки цілих плиток дійду раніше за всіх суперників (оцінка з «Трона»).
    /// Свій лід береже, а крок на межу з суперником забирає лід у нього — так і відрізає дорогу. −1 — кроку нема.
    /// </summary>
    int Territory(ThiniceCore c, ThiniceBody b, int[] g, int n, int start, Random rng)
    {
        var best = -1;
        var bestScore = double.MinValue;
        int x0 = start % n, y0 = start / n;
        foreach (var (dx, dy) in Dirs)
        {
            int nx = x0 + dx, ny = y0 + dy;
            if (nx < 0 || ny < 0 || nx >= n || ny >= n) continue;
            var u = ny * n + nx;
            if (!Passable(g[u], 1)) continue;
            var score = Owned(g, n, u) + Local(g, n, u) * 0.5 + (g[u] == 0 ? 2 : 0) + rng.NextDouble();
            foreach (var o in c.Bodies)
            {
                if (o == b || !o.Plays || !o.In || o.Tier != b.Tier || o.Fall > 0) continue;
                var oc = c.CellOf(o);
                if (oc == u) score -= 4;   // на ту саму плитку — вона вже тріщить під ним
            }
            if (score > bestScore) { bestScore = score; best = u; }
        }
        return best;
    }

    /// <summary>Скільки цілих плиток бот, ступивши на <paramref name="from"/>, дійде раніше за найближчого суперника.</summary>
    int Owned(int[] g, int n, int from)
    {
        Array.Fill(_mine, -1);
        int head = 0, tail = 0, owned = 0;
        _mine[from] = 1;
        _q[tail++] = from;
        while (head < tail)
        {
            var v = _q[head++];
            if (g[v] == 0 && (_odist[v] < 0 || _mine[v] < _odist[v])) owned++;
            int x = v % n, y = v / n;
            foreach (var (dx, dy) in Dirs)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= n || ny >= n) continue;
                var u = ny * n + nx;
                if (_mine[u] >= 0 || !Passable(g[u], _mine[v] + 1)) continue;
                // Суперник там раніше — далі через цю клітинку не наше.
                if (_odist[u] >= 0 && _odist[u] <= _mine[v] + 1) continue;
                _mine[u] = _mine[v] + 1;
                _q[tail++] = u;
            }
        }
        return owned;
    }

    /// <summary>Цілого льоду нема: сусід, що протримається найдовше (стояти — гірше).</summary>
    int Escape(int[] g, int n, int start)
    {
        var best = -1;
        var bestV = g[start] == ThiniceCore.Gone ? 0 : g[start];
        int x0 = start % n, y0 = start / n;
        foreach (var (dx, dy) in Dirs)
        {
            int nx = x0 + dx, ny = y0 + dy;
            if (nx < 0 || ny < 0 || nx >= n || ny >= n) continue;
            var v = ny * n + nx;
            if (g[v] == ThiniceCore.Gone) continue;
            var life = g[v] == 0 ? int.MaxValue : g[v];
            if (life > bestV) { bestV = life; best = v; }
        }
        if (best >= 0) { _from[best] = start; _jumpTo[best] = false; }
        return best;
    }

    static int Local(int[] g, int n, int v)
    {
        int x = v % n, y = v / n, k = 0;
        for (var yy = Math.Max(0, y - 2); yy <= Math.Min(n - 1, y + 2); yy++)
            for (var xx = Math.Max(0, x - 2); xx <= Math.Min(n - 1, x + 2); xx++)
                if (g[yy * n + xx] == 0) k++;
        return k;
    }

    static readonly (int Dx, int Dy)[] Dirs = [(1, 0), (0, 1), (-1, 0), (0, -1)];

    /// <summary>Відстані в кроках від старту з урахуванням часу тріщин; стрибок — ребро через одну непрохідну клітинку.</summary>
    void Bfs(int[] g, int n, int start, bool jumps)
    {
        Array.Fill(_dist, -1);
        Array.Fill(_from, -1);
        Array.Clear(_jumpTo);
        int head = 0, tail = 0;
        _dist[start] = 0;
        _q[tail++] = start;
        while (head < tail)
        {
            var v = _q[head++];
            int x = v % n, y = v / n, d = _dist[v];
            foreach (var (dx, dy) in Dirs)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= n || ny >= n) continue;
                var u = ny * n + nx;
                if (_dist[u] < 0 && Passable(g[u], d + 1))
                {
                    _dist[u] = d + 1;
                    _from[u] = v;
                    _q[tail++] = u;
                    continue;
                }
                // Стрибок лише з першого кроку (перезарядка 3 с — другого в плані не буде) і лише над непрохідною.
                if (!jumps || d != 0 || Passable(g[u], d + 1)) continue;
                int jx = nx + dx, jy = ny + dy;
                if (jx < 0 || jy < 0 || jx >= n || jy >= n) continue;
                var w = jy * n + jx;
                if (_dist[w] >= 0 || !Passable(g[w], d + 3)) continue;
                _dist[w] = d + 3;   // дорожче за звичайний шлях: стрибок береже на крайній випадок
                _from[w] = v;
                _jumpTo[w] = true;
                _q[tail++] = w;
            }
        }
    }

    /// <summary>Розмір зв'язної області цілого льоду кожної клітинки.</summary>
    void Regions(int[] g, int n)
    {
        Array.Fill(_region, 0);
        var size = n * n;
        var mark = _odist;   // тимчасово: позначка області
        Array.Fill(mark, -1);
        for (var s = 0; s < size; s++)
        {
            if (g[s] != 0 || mark[s] >= 0) continue;
            int head = 0, tail = 0;
            _q[tail++] = s;
            mark[s] = s;
            while (head < tail)
            {
                var v = _q[head++];
                int x = v % n, y = v / n;
                foreach (var (dx, dy) in Dirs)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= n || ny >= n) continue;
                    var u = ny * n + nx;
                    if (g[u] != 0 || mark[u] >= 0) continue;
                    mark[u] = s;
                    _q[tail++] = u;
                }
            }
            for (var i = 0; i < tail; i++) _region[_q[i]] = tail;
        }
    }

    /// <summary>Відстані від найближчого суперника на тому ж ярусі (пошук з усіх разом).</summary>
    void Rivals(ThiniceCore c, int seat, int[] g, int n)
    {
        Array.Fill(_odist, -1);
        int head = 0, tail = 0;
        for (var s = 0; s < ThiniceCore.Seats; s++)
        {
            var o = c.Bodies[s];
            if (s == seat || !o.Plays || !o.In || o.Tier != c.Bodies[seat].Tier || o.Fall > 0) continue;
            var oc = c.CellOf(o);
            if (_odist[oc] >= 0) continue;
            _odist[oc] = 0;
            _q[tail++] = oc;
        }
        while (head < tail)
        {
            var v = _q[head++];
            int x = v % n, y = v / n;
            foreach (var (dx, dy) in Dirs)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= n || ny >= n) continue;
                var u = ny * n + nx;
                if (_odist[u] >= 0 || !Passable(g[u], _odist[v] + 1)) continue;
                _odist[u] = _odist[v] + 1;
                _q[tail++] = u;
            }
        }
    }
}
