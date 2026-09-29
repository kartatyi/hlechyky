namespace Hlechyky.Games.Impl;

/// <summary>
/// Бот-гравець Танчиків, коли людина за столом сама (не 🤖 хвиль, а звичайний танк свого місця: ті самі швидкість,
/// перезарядка, бонуси й повернення після підбиття). Грає тим самим вводом, що й людина, — «тримаю напрямок» і
/// «💥» через <see cref="TanksCore.Turn"/> / <see cref="TanksCore.Press"/>.
/// <para>
/// Їде не просто «до суперника», а до найближчої клітинки, звідки той на лінії вогню (шлях — Дейкстрою, цегла на ньому
/// дорожча: її треба розстріляти); там розвертається й стріляє. У командах — ламає чужий глек, а свій береже, коли до
/// нього під'їхали. Бонус поблизу — підбирає. Від куль ухиляється вбік, а коли дивиться кулі в лоб — збиває її своєю.
/// </para>
/// <para>
/// Рівні — реакція (як часто думає), точність (скільки вагається, вирівнявшись, і чи стріляє на впередження по тому,
/// хто їде впоперек) та ухиляння (як часто помічає кулю). Легкий ще й блукає.
/// </para>
/// </summary>
public sealed class TanksBot
{
    /// <summary>Як часто думає про шлях (тиків по 40 мс) — легкий / звичайний / сильний.</summary>
    static readonly int[] Every = [7, 4, 2];
    /// <summary>Скільки тиків вагається, вирівнявшись на ціль, перш ніж бахнути.</summary>
    static readonly int[] AimDelay = [7, 3, 1];
    /// <summary>Шанс помітити кулю, що летить у нього, і ухилитися чи збити її.</summary>
    static readonly double[] DodgeP = [0.15, 0.5, 0.8];
    /// <summary>Шанс за думку поїхати навмання (легкий блукає).</summary>
    static readonly double[] Wander = [0.2, 0.05, 0];
    /// <summary>Стріляє на впередження по тому, хто їде впоперек лінії (легкий — туди, де ціль зараз).</summary>
    static readonly bool[] Lead = [false, true, true];
    /// <summary>Як далеко бачить бонус (клітинок шляху).</summary>
    static readonly int[] LootReach = [0, 5, 8];
    /// <summary>Скільки клітинок уздовж лінії вогню вважає «дострілю» (снаряд летить через усе поле, але що далі — то легше ухилитись).</summary>
    const int Range = 9;
    /// <summary>Вартість цеглини на шляху: доїхати, розвернутись і розстріляти.</summary>
    const int BrickCost = 4;

    readonly int _lvl;
    readonly int _seat;
    int _think;
    int _aim;
    /// <summary>Від якої кулі вже ухилявся (чи вирішив не помічати) — жереб на кулю один.</summary>
    int _shell = -1;
    int _dodge;

    int[] _dist = [], _first = [];
    readonly PriorityQueue<int, int> _queue = new();

    public TanksBot(int seat, LiveBots.Level level, int offset = 0)
    {
        _seat = seat;
        _lvl = LiveBots.Index(level);
        _think = offset;
    }

    public int Seat => _seat;

    /// <summary>Думка на тик — перед <see cref="TanksCore.Step"/>.</summary>
    public void Think(TanksCore core, Random rng)
    {
        var me = core.Tanks[_seat];
        if (!me.Alive) { _aim = 0; return; }
        if (_dist.Length != core.Tiles.Length)
        {
            _dist = new int[core.Tiles.Length];
            _first = new int[core.Tiles.Length];
        }

        if (Dodge(core, me, rng)) return;
        if (_dodge > 0) { _dodge--; return; }

        // Стріляє, коли ціль на лінії дула (з упередженням) або цеглина впритул на шляху.
        if (OnTarget(core, me)) { if (++_aim >= AimDelay[_lvl]) Fire(core, me); }
        else _aim = 0;

        if (me.Move >= 0) return;
        var ahead = core.Ahead(me.Cell, me.Dir);
        if (ahead >= 0 && core.Tiles[ahead] == TankTile.Brick && me.Want == me.Dir) Fire(core, me);
        // Думає раз на кілька тиків — або одразу, коли вперся (не в цеглу: ту він розстрілює).
        var stuck = me.Want >= 0 && ahead >= 0 && core.Tiles[ahead] != TankTile.Brick && !core.Drivable(ahead, me);
        if (--_think > 0 && !stuck) return;
        _think = Every[_lvl];

        if (rng.NextDouble() < Wander[_lvl]) { core.Turn(_seat, rng.Next(4)); return; }
        Plan(core, me, rng);
    }

    // ---------- рух ----------

    void Plan(TanksCore core, Tank me, Random rng)
    {
        Paths(core, me.Cell);

        // бонус поблизу — по дорозі
        int best = -1, bestD = int.MaxValue;
        foreach (var d in core.Drops)
            if (_dist[d.Cell] >= 0 && _dist[d.Cell] <= LootReach[_lvl] && _dist[d.Cell] < bestD) (best, bestD) = (d.Cell, _dist[d.Cell]);

        // клітинка, звідки ціль на лінії вогню: найближча за шляхом
        var aimFrom = -1;
        var aimDir = -1;
        if (best < 0)
        {
            bestD = int.MaxValue;
            foreach (var target in Targets(core))
                for (var d = 0; d < 4; d++)
                {
                    var c = target;
                    for (var n = 1; n <= Range; n++)
                    {
                        c = core.Ahead(c, d);
                        if (c < 0 || core.Tiles[c] is TankTile.Steel or TankTile.Brick or TankTile.Base) break;
                        if (_dist[c] < 0 || _dist[c] >= bestD) continue;
                        (best, bestD, aimFrom, aimDir) = (c, _dist[c], c, (d + 2) % 4);
                    }
                }
        }
        if (best < 0) { core.Turn(_seat, rng.Next(4)); return; }

        if (best == me.Cell)
        {
            // Уже на лінії: розвернутись на ціль і стояти (коротке «тримаю напрямок», як тап стрілкою).
            if (aimFrom == me.Cell && aimDir >= 0)
            {
                core.Turn(_seat, aimDir);
                core.Turn(_seat, -1);
            }
            else core.Turn(_seat, -1);
            return;
        }
        var dir = _first[best];
        var next = core.Ahead(me.Cell, dir);
        // Поперек дороги інший танк — об'їхати боком, а не впиратися.
        if (next >= 0 && core.Tiles[next] != TankTile.Brick && !core.Drivable(next, me))
            dir = (dir + (rng.Next(2) == 0 ? 1 : 3)) % 4;
        core.Turn(_seat, dir);
    }

    /// <summary>Цілі: живі суперники; у командах — ще й чужий глек, а коли до свого під'їхали — лише ті, хто під'їхав.</summary>
    IEnumerable<int> Targets(TanksCore core)
    {
        var team = core.Team[_seat];
        if (team >= 0 && core.BaseCell[team] >= 0 && core.BaseUp[team] && _lvl > 0)
        {
            var home = core.BaseCell[team];
            var raid = false;
            for (var i = 0; i < TanksCore.Seats; i++)
                if (Foe(core, i) && Manhattan(core, core.Tanks[i].Cell, home) <= 5) { raid = true; yield return core.Tanks[i].Cell; }
            if (raid) yield break;
        }
        for (var i = 0; i < TanksCore.Seats; i++)
            if (Foe(core, i)) yield return core.Tanks[i].Cell;
        if (team >= 0)
        {
            var other = 1 - team;
            if (core.BaseCell[other] >= 0 && core.BaseUp[other]) yield return core.BaseCell[other];
        }
    }

    /// <summary>Дейкстра від танка: вільна клітинка (кущ, лід) — 1, цегла — <see cref="BrickCost"/>, сталь — стіна.</summary>
    void Paths(TanksCore core, int start)
    {
        Array.Fill(_dist, -1);
        _queue.Clear();
        _dist[start] = 0;
        _first[start] = -1;
        _queue.Enqueue(start, 0);
        while (_queue.TryDequeue(out var c, out var dc))
        {
            if (dc > _dist[c]) continue;
            for (var d = 0; d < 4; d++)
            {
                var n = core.Ahead(c, d);
                if (n < 0) continue;
                var tile = core.Tiles[n];
                if (tile is TankTile.Steel or TankTile.Base) continue;
                var nd = dc + (tile == TankTile.Brick ? BrickCost : 1);
                if (_dist[n] >= 0 && _dist[n] <= nd) continue;
                _dist[n] = nd;
                _first[n] = c == start ? d : _first[c];
                _queue.Enqueue(n, nd);
            }
        }
    }

    // ---------- стрільба й ухиляння ----------

    void Fire(TanksCore core, Tank me)
    {
        if (me.Reload > 0 || me.ShellsOut >= (me.Twin ? 2 : 1)) return;
        core.Press(_seat);
        _aim = 0;
    }

    /// <summary>
    /// Ціль на лінії дула: суперник (сильний і звичайний — з упередженням: де він буде, коли долетить снаряд) чи
    /// чужий глек, і між нами нема сталі, цегли чи свого.
    /// </summary>
    bool OnTarget(TanksCore core, Tank me)
    {
        var (mx, my) = (core.CenterX(me), core.CenterY(me));
        var (dx, dy) = TanksCore.Deltas[me.Dir];
        var speed = me.Rapid ? TanksCore.RapidShellSpeed : TanksCore.ShellSpeed;
        for (var i = 0; i < TanksCore.Seats; i++)
        {
            if (!Foe(core, i)) continue;
            var t = core.Tanks[i];
            var (fx, fy) = (core.CenterX(t), core.CenterY(t));
            var along = (fx - mx) * dx + (fy - my) * dy;
            if (along <= 0 || along > Range * TanksCore.Sub) continue;
            if (Lead[_lvl] && t.Move >= 0)
            {
                var ticks = along / speed;
                var step = t.Fast ? TanksCore.FastStepSub : TanksCore.StepSub;
                fx += TanksCore.Deltas[t.Move].Dx * step * ticks;
                fy += TanksCore.Deltas[t.Move].Dy * step * ticks;
            }
            var off = Math.Abs((fx - mx) * dy) + Math.Abs((fy - my) * dx);   // відхилення від лінії дула
            if (off > TanksCore.Half - 1) continue;
            if (Clear(core, me.Cell, me.Dir, core.Cell(Math.Clamp(fx / TanksCore.Sub, 0, core.W - 1), Math.Clamp(fy / TanksCore.Sub, 0, core.H - 1)))) return true;
        }
        var team = core.Team[_seat];
        if (team >= 0 && core.BaseCell[1 - team] is var b && b >= 0 && core.BaseUp[1 - team])
        {
            var c = me.Cell;
            if (me.Move >= 0 && me.Step * 2 >= TanksCore.Sub) c = core.Ahead(c, me.Move);
            for (var n = 1; n <= Range; n++)
            {
                c = core.Ahead(c, me.Dir);
                if (c < 0 || core.Tiles[c] is TankTile.Steel or TankTile.Brick) break;
                if (c == b) return true;
                if (core.Tiles[c] == TankTile.Base) break;
            }
        }
        return false;
    }

    /// <summary>Від клітинки <paramref name="from"/> у бік <paramref name="dir"/> до <paramref name="to"/> — ні сталі, ні цегли, ні свого танка.</summary>
    bool Clear(TanksCore core, int from, int dir, int to)
    {
        var c = from;
        for (var n = 0; n <= Range + 1; n++)
        {
            if (c == to) return true;
            c = core.Ahead(c, dir);
            if (c < 0 || core.Tiles[c] is TankTile.Steel or TankTile.Brick or TankTile.Base) return false;
            for (var i = 0; i < TanksCore.Seats; i++)
                if (i != _seat && core.Tanks[i].Alive && core.Tanks[i].Cell == c && !Foe(core, i)) return false;
        }
        return false;
    }

    /// <summary>
    /// Куля летить у бота: дивиться їй у лоб і може стріляти — збиває своєю; інакше — вбік, якщо там можна проїхати.
    /// Жереб «помітив / не помітив» — один на кулю.
    /// </summary>
    bool Dodge(TanksCore core, Tank me, Random rng)
    {
        var (mx, my) = (core.CenterX(me), core.CenterY(me));
        foreach (var s in core.Shells)
        {
            if (s.Owner == _seat || (core.Team[s.Owner] >= 0 && core.Team[s.Owner] == core.Team[_seat])) continue;
            var (dx, dy) = TanksCore.Deltas[s.Dir];
            var toward = (mx - s.X) * dx + (my - s.Y) * dy;              // скільки ще летіти до мене вздовж
            var off = Math.Abs((mx - s.X) * dy) + Math.Abs((my - s.Y) * dx);
            if (toward <= 0 || off > TanksCore.Half + 2 || toward > s.Speed * 10) continue;
            if (_shell == s.Id) return false;
            _shell = s.Id;
            if (rng.NextDouble() >= DodgeP[_lvl]) return false;
            if (me.Dir == (s.Dir + 2) % 4 && me.Reload == 0 && me.ShellsOut < (me.Twin ? 2 : 1))
            {
                core.Press(_seat);
                return true;
            }
            if (me.Move >= 0) return false;                               // посеред кроку не звернеш
            var side = (s.Dir + 1 + 2 * rng.Next(2)) % 4;
            if (!core.Drivable(core.Ahead(me.Cell, side), me)) side = (side + 2) % 4;
            if (!core.Drivable(core.Ahead(me.Cell, side), me)) return false;
            core.Turn(_seat, side);
            _dodge = 4;
            _think = Every[_lvl] + 4;
            return true;
        }
        return false;
    }

    bool Foe(TanksCore core, int i)
    {
        if (i == _seat || !core.Tanks[i].Alive || !core.Tanks[i].Plays) return false;
        var team = core.Team[_seat];
        return team < 0 || core.Team[i] != team;
    }

    static int Manhattan(TanksCore core, int a, int b) => Math.Abs(core.X(a) - core.X(b)) + Math.Abs(core.Y(a) - core.Y(b));
}
