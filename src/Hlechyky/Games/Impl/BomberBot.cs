namespace Hlechyky.Games.Impl;

/// <summary>
/// Бот Бомбера, коли людина за столом сама (їх троє: див. spec «Соло з ботом»). Грає тим самим вводом, що й людина —
/// «тримаю напрямок» і «клади бомбу» через <see cref="BomberCore.Turn"/> / <see cref="BomberCore.Bomb"/>, з тими самими
/// швидкостями, лімітом бомб і прокляттями. Бачить те, що й людина: поле, бомби з запалами, полум'я, бонуси.
/// <para>
/// Класика бомбермена: карта небезпеки (коли кожна клітинка загориться — з ланцюгами бомб), пошук у ширину до клітинки,
/// де не горітиме, з перевіркою «встигну пробігти, поки там не палає». Бомбу кладе, лише коли після неї є куди втекти;
/// ламає ящики, бере бонуси, полює на суперника, а сильний ще й заганяє в кут — кладе бомбу, після якої супернику
/// нікуди подітись.
/// </para>
/// <para>
/// Рівні — не швидкість (вона чесна), а «очі»: чужу бомбу бот помічає не одразу (легкий — через півсекунди), легкий не
/// рахує ланцюгів, інколи ставить бомбу навмання і в паніці тікає не туди; сильний бачить бонуси здалеку, прокляття
/// «навпаки» розгадує і майже не помиляється — але так само може опинитись між двома бомбами.
/// </para>
/// </summary>
public sealed class BomberBot
{
    /// <summary>Через скільки тиків (по 60 мс) бот помічає чужу бомбу — легкий / звичайний / сильний. Свою знає одразу.</summary>
    static readonly int[] Notice = [8, 4, 1];
    /// <summary>Шанс поставити бомбу, не перевіривши, чи є куди втекти: легкий інколи сам себе заганяє.</summary>
    static readonly double[] Reckless = [0.05, 0.01, 0];
    /// <summary>Шанс, тікаючи, смикнутися «куди очі дивляться» замість найближчої безпечної клітинки.</summary>
    static readonly double[] Panic = [0.06, 0.01, 0];
    /// <summary>Як далеко (кроків шляху) бот помічає бонус.</summary>
    static readonly int[] BonusReach = [3, 7, 12];
    /// <summary>Шанс кинути бомбу, коли суперник на лінії вогню (за одну думку).</summary>
    static readonly double[] Aggro = [0.2, 0.55, 0.9];
    /// <summary>З якої відстані (Манхеттен) бот кидає ящики й іде полювати.</summary>
    static readonly int[] HuntNear = [0, 4, 7];

    const int Never = int.MaxValue;

    readonly int _lvl;
    readonly int _seat;

    // Робочі масиви: коли клітинка загориться (_at, Never — ні) і до якого тика догоратиме (_end); пошук у ширину.
    int[] _at = [], _end = [], _dist = [], _first = [];
    readonly Queue<int> _queue = new();
    readonly List<(int Cell, int Range, int Fuse)> _bombs = [];
    int[] _fuse = [];
    readonly List<(double Score, int Cell)> _spots = [];
    /// <summary>Клітинка, де вже кидали жереб «бомба навмання».</summary>
    int _rolled = -1;

    public BomberBot(int seat, LiveBots.Level level)
    {
        _seat = seat;
        _lvl = LiveBots.Index(level);
    }

    public int Seat => _seat;

    /// <summary>Думка на тик — перед <see cref="BomberCore.Step"/>. Бомбер посеред кроку просто їде далі.</summary>
    public void Think(BomberCore core, Random rng)
    {
        var me = core.Players[_seat];
        if (me.Ghost) { Haunt(core, me, rng); return; }
        if (!me.Alive || me.Move >= 0) return;
        Ensure(core.Tiles.Length);

        var here = me.Cell;
        var st = StepTicks(me);
        Danger(core, -1, 0);
        Bfs(core, here, st);

        // 1. Горітиме тут — тікати.
        if (_at[here] != Never)
        {
            Go(core, me, Escape(core, here, rng), rng);
            return;
        }

        // 2. Бомба: є що ламати чи кого підірвати — і є куди втекти.
        if (me.Fused < me.Bombs && WantBomb(core, me, here, st, rng))
        {
            core.Bomb(_seat);
            Danger(core, -1, 0);
            Bfs(core, here, st);
            Go(core, me, Escape(core, here, rng), rng);
            return;
        }

        // 3. Куди йти: бонус, місце під ящиками, суперник.
        Go(core, me, Goal(core, me, here, st), rng);
    }

    // ---------- рішення ----------

    bool WantBomb(BomberCore core, BomberMan me, int here, int st, Random rng)
    {
        var (boxes, foes, allies) = Blast(core, here, me.Range);
        if (allies && core.FriendlyFire && _lvl > 0) return false;   // своїх під дружній вогонь не підставляє
        var reason = boxes > 0 || (foes && rng.NextDouble() < Aggro[_lvl]);
        if (!reason && _lvl == 2) reason = Trap(core, me, here);
        if (!reason) return false;
        // Навмання — раз на клітинку, а не щотику: інакше «інколи» за секунду стояння ставало «напевно».
        if (here != _rolled)
        {
            _rolled = here;
            if (rng.NextDouble() < Reckless[_lvl]) return true;
        }
        return CanFlee(core, here, me.Range, st);
    }

    /// <summary>Після бомби тут є клітинка, куди встигну добігти й де не горітиме.</summary>
    bool CanFlee(BomberCore core, int here, int range, int st)
    {
        var ok = Flees(core, here, range, st);
        Danger(core, -1, 0);
        Bfs(core, here, st);
        return ok;
    }

    /// <summary>Те саме без відновлення карти: після нього <c>_at/_dist</c> — від уявної бомби в <paramref name="from"/>.</summary>
    bool Flees(BomberCore core, int from, int range, int st)
    {
        Danger(core, from, range);
        Bfs(core, from, st);
        for (var c = 0; c < _dist.Length; c++)
            if (_dist[c] > 0 && Safe(c, _dist[c] * st)) return true;
        return false;
    }

    /// <summary>
    /// Сильний: суперник поблизу, і після моєї бомби йому нікуди втекти (а мені є) — кладу. Так бот заганяє в кут.
    /// </summary>
    bool Trap(BomberCore core, BomberMan me, int here)
    {
        var st = StepTicks(me);
        for (var s = 0; s < BomberCore.Seats; s++)
        {
            if (!Foe(core, s)) continue;
            var foe = core.Players[s];
            var at = core.Center(foe);
            if (Math.Abs(core.X(at) - core.X(here)) + Math.Abs(core.Y(at) - core.Y(here)) > me.Range + 2) continue;
            Danger(core, here, me.Range);
            var fst = StepTicks(foe);
            Bfs(core, at, fst);
            var out_ = false;
            for (var c = 0; c < _dist.Length && !out_; c++)
                if (_dist[c] >= 0 && Safe(c, _dist[c] * fst)) out_ = true;
            if (out_) continue;
            Bfs(core, here, st);
            var mine = false;
            for (var c = 0; c < _dist.Length && !mine; c++)
                if (_dist[c] > 0 && Safe(c, _dist[c] * st)) mine = true;
            Danger(core, -1, 0);
            Bfs(core, here, st);
            if (mine) return true;
        }
        Danger(core, -1, 0);
        Bfs(core, here, st);
        return false;
    }

    /// <summary>Найближча клітинка, де не горітиме; легкий у паніці інколи смикається навмання.</summary>
    int Escape(BomberCore core, int here, Random rng)
    {
        if (rng.NextDouble() < Panic[_lvl])
        {
            var d = rng.Next(4);
            var c = core.Ahead(here, d);
            return core.Walkable(c) ? c : here;
        }
        var st = StepTicks(core.Players[_seat]);
        int best = -1, bestD = Never;
        for (var c = 0; c < _dist.Length; c++)
            if (_dist[c] > 0 && _dist[c] < bestD && Safe(c, _dist[c] * st)) (best, bestD) = (c, _dist[c]);
        if (best >= 0) return best;
        // Безпечної нема — хоч туди, де загориться найпізніше.
        var late = _at[here];
        for (var c = 0; c < _dist.Length; c++)
            if (_dist[c] > 0 && _at[c] > late) (best, late) = (c, _at[c]);
        return best >= 0 ? best : here;
    }

    /// <summary>Куди йти, коли тут спокійно: бонус поблизу, місце під ящиками, суперник.</summary>
    int Goal(BomberCore core, BomberMan me, int here, int st)
    {
        // бонус
        int best = -1, bestD = Never;
        foreach (var drop in core.Drops)
        {
            var c = drop.Cell;
            if (drop.Kind == BomberBonus.Skull && _lvl > 0) continue;   // легкий хапає і черепи
            var d = _dist[c];
            if (d < 0 || d > BonusReach[_lvl] || d >= bestD || !Safe(c, d * st)) continue;
            (best, bestD) = (c, d);
        }
        if (best >= 0) return best;

        var foe = NearestFoe(core, here, out var foeDist);
        var hunt = foe >= 0 && foeDist <= HuntNear[_lvl];

        // Місце, звідки бомба зачепить найбільше ящиків (ближче — краще), — і звідки після неї є куди втекти:
        // інакше бот стоїть у кутку між ящиками й чекає дива.
        if (!hunt)
        {
            _spots.Clear();
            for (var c = 0; c < _dist.Length; c++)
            {
                var d = _dist[c];
                if (d < 0 || !Safe(c, d * st)) continue;
                var (boxes, _, _) = Blast(core, c, me.Range);
                if (boxes > 0) _spots.Add((boxes / (d + 2.0), c));
            }
            if (_spots.Count > 0)
            {
                _spots.Sort((a, b) => b.Score.CompareTo(a.Score));
                for (var i = 0; i < _spots.Count && i < 8 && best < 0; i++)
                    if (Flees(core, _spots[i].Cell, me.Range, st)) best = _spots[i].Cell;
                Danger(core, -1, 0);
                Bfs(core, here, st);
                if (best >= 0) return best;
            }
        }

        // полювання: досяжна клітинка найближче до суперника
        if (foe >= 0)
        {
            var bestM = Never;
            for (var c = 0; c < _dist.Length; c++)
            {
                var d = _dist[c];
                if (d < 0 || !Safe(c, d * st)) continue;
                var m = (Math.Abs(core.X(c) - core.X(foe)) + Math.Abs(core.Y(c) - core.Y(foe))) * 4 + d;
                if (m < bestM) (bestM, best) = (m, c);
            }
            if (best >= 0) return best;
        }
        return here;
    }

    /// <summary>Перший крок до клітинки <paramref name="goal"/> — тим самим «тримаю напрямок», що й у людини.</summary>
    void Go(BomberCore core, BomberMan me, int goal, Random rng)
    {
        var dir = -1;
        if (goal != me.Cell)
        {
            if (goal >= 0 && _dist[goal] > 0) dir = _first[goal];
            else for (var d = 0; d < 4; d++) if (core.Ahead(me.Cell, d) == goal) dir = d;
        }
        // «Навпаки» звичайний і сильний розгадують; легкий плутається, як і людина вперше.
        if (dir >= 0 && me.Curse == BomberCurse.Reverse && _lvl > 0) dir = (dir + 2) % 4;
        core.Turn(_seat, dir);
    }

    /// <summary>Привид: летить до найближчого живого суперника і, опинившись поруч, кидає помсту.</summary>
    void Haunt(BomberCore core, BomberMan me, Random rng)
    {
        if (me.Move >= 0) return;
        var at = core.Center(me);
        var foe = NearestFoe(core, at, out var dist);
        if (foe < 0) { core.Turn(_seat, -1); return; }
        if (me.Revenge && dist <= 1 && rng.NextDouble() < Aggro[_lvl] && core.Revenge(_seat)) return;
        var (dx, dy) = (core.X(foe) - core.X(at), core.Y(foe) - core.Y(at));
        var dir = Math.Abs(dx) >= Math.Abs(dy) ? (dx > 0 ? 0 : dx < 0 ? 2 : -1) : (dy > 0 ? 1 : 3);
        if (dir < 0) dir = dy > 0 ? 1 : dy < 0 ? 3 : -1;
        core.Turn(_seat, dir);
    }

    // ---------- що бачить ----------

    bool Foe(BomberCore core, int s)
    {
        if (s == _seat || !core.Players[s].Alive) return false;
        return core.Teams is null || core.Players[s].Team < 0 || core.Players[s].Team != core.Players[_seat].Team;
    }

    int NearestFoe(BomberCore core, int from, out int dist)
    {
        dist = Never;
        var cell = -1;
        for (var s = 0; s < BomberCore.Seats; s++)
        {
            if (!Foe(core, s)) continue;
            var c = core.Center(core.Players[s]);
            var d = Math.Abs(core.X(c) - core.X(from)) + Math.Abs(core.Y(c) - core.Y(from));
            if (d < dist) (dist, cell) = (d, c);
        }
        return cell;
    }

    /// <summary>Що зачепить бомба з клітинки: ящики (ще не приречені чужим вогнем), суперники, свої.</summary>
    (int Boxes, bool Foes, bool Allies) Blast(BomberCore core, int from, int range)
    {
        var boxes = 0;
        var foes = false;
        var allies = false;
        void Who(int cell)
        {
            for (var s = 0; s < BomberCore.Seats; s++)
            {
                if (s == _seat || !core.Players[s].Alive || core.Center(core.Players[s]) != cell) continue;
                if (Foe(core, s)) foes = true;
                else allies = true;
            }
        }
        Who(from);
        for (var d = 0; d < 4; d++)
        {
            var c = from;
            for (var i = 0; i < range; i++)
            {
                c = core.Ahead(c, d);
                if (c < 0 || core.Tiles[c] == BomberTile.Wall) break;
                if (core.Tiles[c] == BomberTile.Box)
                {
                    if (_at[c] == Never) boxes++;
                    break;
                }
                Who(c);
            }
        }
        return (boxes, foes, allies);
    }

    /// <summary>Тут можна стояти, прибігши за <paramref name="arrive"/> тиків: не горітиме взагалі або вже догорить.</summary>
    bool Safe(int c, int arrive) => _at[c] == Never || _end[c] < arrive;

    static int StepTicks(BomberMan p) =>
        BomberCore.Sub / (p.Curse == BomberCurse.Slow ? BomberCore.SnailStep : p.Boots ? BomberCore.FastStep : BomberCore.SlowStep);

    void Ensure(int n)
    {
        if (_at.Length == n) return;
        _at = new int[n];
        _end = new int[n];
        _dist = new int[n];
        _first = new int[n];
    }

    /// <summary>
    /// Карта небезпеки: коли кожна клітинка загориться і до якого тика догоратиме. Полум'я, що вже є; бомби — з
    /// ланцюгами (бомба, яку зачепить сусідка, бахне разом із нею; легкий цього не рахує); плюс
    /// <paramref name="extra"/> — бомба, яку бот лише думає поставити. Чужу свіжу бомбу бот ще «не помітив».
    /// </summary>
    void Danger(BomberCore core, int extra, int extraRange)
    {
        Array.Fill(_at, Never);
        Array.Fill(_end, -1);
        for (var c = 0; c < _at.Length; c++)
            if (core.Flame[c] > 0) { _at[c] = 0; _end[c] = core.Flame[c] - 1; }

        _bombs.Clear();
        foreach (var b in core.Bombs)
        {
            var age = (b.Revenge ? BomberCore.RevengeFuse : BomberCore.FuseTicks) - b.Fuse;
            if (b.Owner != _seat && age < Notice[_lvl]) continue;
            _bombs.Add((b.Cell, b.Range, b.Fuse));
        }
        if (extra >= 0) _bombs.Add((extra, extraRange, BomberCore.FuseTicks));
        var m = _bombs.Count;
        if (_fuse.Length < m) _fuse = new int[Math.Max(m, 8)];
        for (var i = 0; i < m; i++) _fuse[i] = _bombs[i].Fuse;

        // Ланцюги: запал бомби, яку зачепить інша, не довший за запал тієї. Кілька проходів — поки щось міняється.
        if (_lvl > 0)
            for (var pass = 0; pass <= m; pass++)
            {
                var changed = false;
                for (var a = 0; a < m; a++)
                    for (var d = 0; d < 4; d++)
                    {
                        var c = _bombs[a].Cell;
                        for (var i = 0; i < _bombs[a].Range; i++)
                        {
                            c = core.Ahead(c, d);
                            if (c < 0 || core.Tiles[c] != BomberTile.Free) break;
                            for (var b = 0; b < m; b++)
                                if (_bombs[b].Cell == c && _fuse[b] > _fuse[a]) { _fuse[b] = _fuse[a]; changed = true; }
                        }
                    }
                if (!changed) break;
            }

        for (var a = 0; a < m; a++)
        {
            var t = _fuse[a];
            Mark(_bombs[a].Cell, t);
            for (var d = 0; d < 4; d++)
            {
                var c = _bombs[a].Cell;
                for (var i = 0; i < _bombs[a].Range; i++)
                {
                    c = core.Ahead(c, d);
                    if (c < 0 || core.Tiles[c] == BomberTile.Wall) break;
                    Mark(c, t);
                    if (core.Tiles[c] == BomberTile.Box) break;
                }
            }
        }

        // Стискання: клітинки, які скоро стануть стіною, — небезпечні назавжди.
        if (core.Shrink && core.Ticks + 90 >= BomberCore.ShrinkFrom)
        {
            var t0 = core.Ticks < BomberCore.ShrinkFrom ? BomberCore.ShrinkFrom - core.Ticks : 1;
            for (var k = 0; k < 40 && core.Shrunk + k < core.ShrinkOrder.Length; k++)
            {
                var c = core.ShrinkOrder[core.Shrunk + k];
                _at[c] = Math.Min(_at[c], t0 + k * core.ShrinkEvery);
                _end[c] = Never - 1;
            }
        }
    }

    void Mark(int c, int t)
    {
        if (t < _at[c]) _at[c] = t;
        var e = t + BomberCore.FlameTicks - 1;
        if (e > _end[c]) _end[c] = e;
    }

    /// <summary>
    /// Пошук у ширину від <paramref name="start"/> по прохідних клітинках: крок — <paramref name="st"/> тиків, і в
    /// клітинку, що палатиме саме тоді, коли бот через неї пробігає, не заходимо. <c>_first</c> — перший крок шляху.
    /// </summary>
    void Bfs(BomberCore core, int start, int st)
    {
        Array.Fill(_dist, -1);
        _queue.Clear();
        _dist[start] = 0;
        _first[start] = -1;
        _queue.Enqueue(start);
        while (_queue.Count > 0)
        {
            var c = _queue.Dequeue();
            var nd = _dist[c] + 1;
            var tIn = nd * st - st / 2 - 1;
            var tOut = nd * st + st / 2 + 1;
            for (var d = 0; d < 4; d++)
            {
                var n = core.Ahead(c, d);
                if (n < 0 || _dist[n] >= 0 || !core.Walkable(n)) continue;
                if (_at[n] != Never && _at[n] <= tOut && _end[n] >= tIn) continue;
                _dist[n] = nd;
                _first[n] = c == start ? d : _first[c];
                _queue.Enqueue(n);
            }
        }
    }
}
