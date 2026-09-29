namespace Hlechyky.Games.Impl;

/// <summary>
/// «Голова» бота Землі. Поле — 40×30 клітинок, тож тут можна чесно: пошук у ширину додому (не наступаючи на свій
/// слід) — це ~1200 клітинок, кілька мікросекунд. Бот робить прямокутні петлі: виїхав з наділу на A клітинок,
/// звернув, проїхав B, звернув ще раз — і далі найкоротшим шляхом додому, що й замикає петлю.
///
/// Рівні — розмір петлі й пильність, фізика та сама (<see cref="TerritoryCore.Turn"/>, як у людини):
/// легкий — петлі 2–5 клітинок, довго топчеться вдома й на суперників не зважає: росте повільно й буває зрізаний;
/// звичайний — петлі 3–8, вертається, коли чужа голова ближче до його сліду, ніж він до дому (із запасом);
/// сильний — петлі 4–11 із запасом пильності більшим, а коли поруч чужий слід — їде його різати.
/// </summary>
public sealed class TerritoryBot
{
    static readonly (int Dx, int Dy)[] Deltas = [(1, 0), (0, 1), (-1, 0), (0, -1)];

    readonly int _legMin, _legMax;
    /// <summary>Запас пильності в клітинках; -1 — на суперників не зважає.</summary>
    readonly int _margin;
    /// <summary>Скільки тиків (від..до) топтатись удома між петлями.</summary>
    readonly int _restMin, _restMax;
    readonly double _slip;
    readonly bool _hunt;
    readonly bool _heads;

    enum Mode { Home, Out, Back }
    /// <summary>Спершу «додому»: перша ж думка на наділі складе план петлі.</summary>
    Mode _mode = Mode.Back;
    int _exit, _side, _legA, _legB, _outAt, _restUntil = -1;

    int[] _dist = [], _first = [];
    int[] _queue = [];

    public TerritoryBot(LiveBots.Level level)
    {
        (_legMin, _legMax, _margin, _restMin, _restMax, _slip) = level switch
        {
            LiveBots.Level.Easy => (2, 5, -1, 8, 25, 0.05),
            LiveBots.Level.Hard => (4, 11, 4, 0, 2, 0.0),
            _ => (3, 8, 3, 2, 8, 0.01),
        };
        _hunt = level == LiveBots.Level.Hard;
        _heads = level != LiveBots.Level.Easy;
    }

    public string ModeName => _mode.ToString();

    /// <summary>Куди повернути цього тика (0..3) або null — їхати, як їде. Дзеркало того, що шле людина в <c>turn</c>.</summary>
    public int? Think(TerritoryCore c, int seat, Random rng)
    {
        var r = c.Riders[seat];
        if (!r.On || !r.Alive || r.Turns.Count > 0) return null;
        Ensure(c);
        var me = (byte)(seat + 1);
        var here = c.Cell(r.X, r.Y);
        var home = c.Owner[here] == me;

        if (!r.HasTrail)
        {
            if (!home) { _mode = Mode.Back; }   // нове воскресіння чи збій плану — просто додому
            else if (_mode != Mode.Home)
            {
                _mode = Mode.Home;
                _restUntil = c.Ticks + rng.Next(_restMin, _restMax + 1);
                Plan(c, seat, rng);
            }
        }
        else if (_mode == Mode.Home)
        {
            _mode = Mode.Out;
            _outAt = c.Ticks - 1;
        }

        int want;
        if (_mode == Mode.Home)
        {
            // Топчемось удома (не виїжджаємо), поки не відпочили; далі — в бік виїзду.
            if (c.Ticks < _restUntil) want = Stay(c, seat, r) ?? r.Dir;
            else want = _exit;
        }
        else
        {
            var toHome = Bfs(c, seat, r.X, r.Y, me, target: 0);
            var steps = c.Ticks - _outAt;
            if (_mode == Mode.Out)
            {
                var danger = _margin >= 0 && Threat(c, seat) <= toHome.Dist + _margin;
                if (danger || steps >= _legA + _legB || toHome.Dist < 0) _mode = Mode.Back;
            }
            want = _mode == Mode.Back ? (toHome.First >= 0 ? toHome.First : r.Dir)
                : steps < _legA ? _exit : Turned(_exit, _side);
        }

        // Сильний: чужий слід у кількох кроках — різати, поки свій слід короткий. Після полювання — одразу додому.
        if (_hunt)
        {
            // Лише з дому чи з коротким слідом: мисливець із довгим хвостом сам стає здобиччю (узаємні зрізи).
            var out1 = r.HasTrail ? c.Ticks - _outAt : 0;
            var any = false;
            for (var s = 0; s < TerritoryCore.MaxPlayers; s++) any |= s != seat && c.Riders[s].HasTrail;
            var prey = any && out1 <= 6 ? Bfs(c, seat, r.X, r.Y, me, target: 1) : (Dist: -1, First: -1);
            if (prey.First >= 0 && prey.Dist <= 6 && out1 + prey.Dist <= 7)
            {
                want = prey.First;
                _legA = _legB = 0;
                _restUntil = 0;
            }
        }

        if (_slip > 0 && rng.NextDouble() < _slip) want = rng.Next(4);   // неуважність — але запобіжник нижче
        var go = Safe(c, seat, r, want) ? want : Rescue(c, seat, r);
        return go != r.Dir ? go : null;
    }

    static int Turned(int dir, int side) => (dir + (side > 0 ? 1 : 3)) % 4;

    /// <summary>План петлі: куди виїжджати (де найбільше чужого поля до стіни), на який бік звертати й скільки.</summary>
    void Plan(TerritoryCore c, int seat, Random rng)
    {
        var r = c.Riders[seat];
        var best = -1;
        for (var d = 0; d < 4; d++)
        {
            var room = Ray(c, seat, r.X, r.Y, d);
            var score = room * 4 + rng.Next(6);
            if (room >= 2 && score > best) { best = score; _exit = d; }
        }
        _legA = rng.Next(_legMin, _legMax + 1);
        _legB = rng.Next(_legMin, _legMax + 1);
        var left = Ray(c, seat, r.X, r.Y, (_exit + 3) % 4);
        var right = Ray(c, seat, r.X, r.Y, (_exit + 1) % 4);
        _side = right > left || (right == left && rng.Next(2) == 0) ? 1 : -1;
    }

    /// <summary>Скільки клітинок від краю наділу в напрямку d до стіни (свою землю проскакуємо).</summary>
    static int Ray(TerritoryCore c, int seat, int x, int y, int d)
    {
        var me = (byte)(seat + 1);
        var (dx, dy) = Deltas[d];
        var n = 0;
        for (x += dx, y += dy; x >= 0 && y >= 0 && x < c.W && y < c.H; x += dx, y += dy)
            if (c.Owner[c.Cell(x, y)] != me) n++;
        return n;
    }

    /// <summary>Лишитись удома: напрямок, що веде на свою клітинку (спершу — прямо).</summary>
    static int? Stay(TerritoryCore c, int seat, TerritoryRider r)
    {
        var me = (byte)(seat + 1);
        foreach (var d in new[] { r.Dir, (r.Dir + 1) % 4, (r.Dir + 3) % 4 })
        {
            var (x, y) = (r.X + Deltas[d].Dx, r.Y + Deltas[d].Dy);
            if (x >= 0 && y >= 0 && x < c.W && y < c.H && c.Owner[c.Cell(x, y)] == me) return d;
        }
        return null;
    }

    /// <summary>Найближча чужа жива голова до мого сліду чи до мене — у кроках (манхеттен).</summary>
    static int Threat(TerritoryCore c, int seat)
    {
        var me = (byte)(seat + 1);
        var best = int.MaxValue;
        // Поле проходимо раз, а голів лише кілька: слід — зовнішній цикл.
        for (var i = 0; i < c.Cells; i++)
        {
            if (c.Trail[i] != me) continue;
            var (x, y) = (i % c.W, i / c.W);
            for (var s = 0; s < TerritoryCore.MaxPlayers; s++)
            {
                var o = c.Riders[s];
                if (s != seat && o.On && o.Alive) best = Math.Min(best, Math.Abs(x - o.X) + Math.Abs(y - o.Y));
            }
        }
        return best;
    }

    /// <summary>
    /// Чи можна туди: не за поле, не на свій слід, не назад, не в клітинку, куди зараз ступить чужа голова (крім
    /// легкого), а поза домом — щоб звідти ще був шлях додому.
    /// </summary>
    bool Safe(TerritoryCore c, int seat, TerritoryRider r, int d)
    {
        if (d == (r.Dir + 2) % 4) return false;
        var (x, y) = (r.X + Deltas[d].Dx, r.Y + Deltas[d].Dy);
        if (x < 0 || y < 0 || x >= c.W || y >= c.H) return false;
        var me = (byte)(seat + 1);
        var cell = c.Cell(x, y);
        if (c.Trail[cell] == me) return false;
        if (_heads)
            for (var s = 0; s < TerritoryCore.MaxPlayers; s++)
            {
                var o = c.Riders[s];
                if (s != seat && o.On && o.Alive && o.X + Deltas[o.Dir].Dx == x && o.Y + Deltas[o.Dir].Dy == y) return false;
            }
        if (c.Owner[cell] == me) return true;
        // Поза домом: з тієї клітинки має лишатись дорога додому, що не йде через мою теперішню клітинку (вона — слід).
        return Bfs(c, seat, x, y, me, target: 0, block: c.Cell(r.X, r.Y)).Dist >= 0;
    }

    /// <summary>Запобіжник: безпечний напрямок, що найкоротше веде додому (або хоч якийсь).</summary>
    int Rescue(TerritoryCore c, int seat, TerritoryRider r)
    {
        var me = (byte)(seat + 1);
        int best = r.Dir, bestD = int.MaxValue;
        foreach (var d in new[] { r.Dir, (r.Dir + 1) % 4, (r.Dir + 3) % 4 })
        {
            if (!Safe(c, seat, r, d)) continue;
            var (x, y) = (r.X + Deltas[d].Dx, r.Y + Deltas[d].Dy);
            var dist = c.Owner[c.Cell(x, y)] == me ? 0 : Bfs(c, seat, x, y, me, 0, c.Cell(r.X, r.Y)).Dist;
            if (dist >= 0 && dist < bestD) { bestD = dist; best = d; }
        }
        return best;
    }

    void Ensure(TerritoryCore c)
    {
        if (_dist.Length == c.Cells) return;
        _dist = new int[c.Cells];
        _first = new int[c.Cells];
        _queue = new int[c.Cells];
    }

    /// <summary>
    /// Пошук у ширину від (x, y) по клітинках без мого сліду. target 0 — до найближчої своєї землі, 1 — до найближчого
    /// чужого сліду. Повертає довжину шляху й перший крок (-1, -1 — не дістатись; 0 — уже на місці).
    /// </summary>
    (int Dist, int First) Bfs(TerritoryCore c, int seat, int x, int y, byte me, int target, int block = -1)
    {
        Ensure(c);
        Array.Fill(_dist, -1);
        var start = c.Cell(x, y);
        if (Hit(start)) return (0, -1);
        int head = 0, tail = 0;
        _dist[start] = 0;
        _first[start] = -1;
        _queue[tail++] = start;
        while (head < tail)
        {
            var cur = _queue[head++];
            var (cx, cy) = (cur % c.W, cur / c.W);
            for (var d = 0; d < 4; d++)
            {
                var (nx, ny) = (cx + Deltas[d].Dx, cy + Deltas[d].Dy);
                if (nx < 0 || ny < 0 || nx >= c.W || ny >= c.H) continue;
                var n = c.Cell(nx, ny);
                if (_dist[n] >= 0 || n == block || c.Trail[n] == me) continue;
                _dist[n] = _dist[cur] + 1;
                _first[n] = cur == start ? d : _first[cur];
                if (Hit(n)) return (_dist[n], _first[n]);
                _queue[tail++] = n;
            }
        }
        return (-1, -1);

        bool Hit(int cell) => target == 0 ? c.Owner[cell] == me : c.Trail[cell] != 0 && c.Trail[cell] != me;
    }
}
