namespace Hlechyky.Games.Impl;

/// <summary>
/// «🤖 бот» Ковзанки — гравець-бот, коли людина сидить за столом сама. Його селянин — такий самий гравець, як людина
/// (<c>Owner</c> = місце бота: свій кошик, ополонка виводить з раунду, очки — за тими ж правилами), а «клавіші» він тисне
/// мозком юрми (<see cref="SkateCore.Think"/>: ті самі поштовхи, утримання, гальмо, ухиляння від води) — ззовні його не
/// відрізнити від селянина. Що відрізняє: він вибирає заняття. Нове заняття — частіше по ласощі зі свого кошика (це й
/// є завдання), а на сильнішому рівні — ще й розгін на підозрілого, що стоїть біля ополонки.
///
/// Рівні: легкий їде по свої ласощі зрідка, довго стоїть і не таранить; звичайний збирає частіше й зрідка таранить
/// підозрілого біля води; сильний збирає впевнено, мало стоїть, стежить, хто підбирає ласощі й хто вперто їде до нього,
/// і таранить так, щоб зіпхнути в ополонку. Промахується, бо лід слизький для всіх: таран — та сама фізика.
/// </summary>
public sealed class SkatePilot(SkateCore core, Random rng, LiveBots.Level level)
{
    /// <summary>Імовірність (%), що нове заняття — по ласощі зі свого кошика: легкий, звичайний, сильний.</summary>
    static readonly int[] TaskPct = [20, 60, 85];
    /// <summary>Шанс (%) на кожному рішенні кинути заняття юрми посеред і поїхати по своє ласощі.</summary>
    static readonly int[] SwitchPct = [0, 8, 25];
    /// <summary>Найдовше стояння між заняттями (тики): юрма стоїть і по 20 с, сильний так не марнує час.</summary>
    static readonly int[] StandCap = [500, 150, 50];
    /// <summary>Підозра: за друге й далі підхоплене ласощі, за таран у мене, за «вперто їде до мене».</summary>
    static readonly int[] PickGain = [0, 150, 300], RamGain = [0, 150, 250], ApproachGain = [0, 40, 100];
    static readonly int[] WatchEvery = [0, 25, 10];
    /// <summary>З якої підозри таранить, як часто думає про це і шанс (%) на кожній думці.</summary>
    static readonly int[] HuntFrom = [2000, 450, 250], HuntEvery = [0, 30, 8], HuntPct = [0, 30, 100];
    /// <summary>Підозрілий біля води: до краю ополонки не далі (од.) — лише тоді є сенс таранити.</summary>
    static readonly int[] NearWater = [0, 50, 80];
    /// <summary>Після тарана — перепочинок від полювання (тики): інакше ганяв би людину без кінця.</summary>
    static readonly int[] RestMin = [0, 250, 125], RestMax = [0, 450, 250];
    const int ChaseFrom = 320, WatchRange = 256, CloserBy = 12, CloserTimes = 3;
    public const int Sure = 1000;

    readonly SkateCore _core = core;
    readonly Random _rng = rng;
    readonly int _lvl = LiveBots.Index(level);

    SkateSeat? _seat;
    int[] _sus = [], _picks = [], _dist = [], _closer = [];
    int _now, _huntIn, _rest, _mark = -1;

    public int Id { get; private set; } = -1;
    public LiveBots.Level Level => level;
    public int SuspicionOf(int id) => id >= 0 && id < _sus.Length ? _sus[id] : 0;
    /// <summary>На кого зараз розганяється (-1 — ні на кого).</summary>
    public int Mark => _mark;

    /// <summary>Новий раунд: свій селянин і кошик місця, чиста пам'ять.</summary>
    public void Reset(int id, int n, SkateSeat seat)
    {
        Id = id;
        _seat = seat;
        _sus = new int[n];
        _picks = new int[n];
        _dist = new int[n];
        _closer = new int[n];
        _now = _rest = 0;
        _huntIn = HuntEvery[_lvl];
        _mark = -1;
    }

    /// <summary>
    /// Подія ядра: хто підбирає ласощі (людина збирає кошик — селянин підбирає зрідка), хто в мене врізався, хто кого
    /// зіпхнув. Шубовснув гравець (не я) — то людина, і вона вже вибула.
    /// </summary>
    public void See(int[] e)
    {
        if (Id < 0 || e.Length < 2) return;
        switch (e[0])
        {
            case SkateCore.EvPick when e[1] != Id && e[1] < _sus.Length:
                if (++_picks[e[1]] >= 2) Add(e[1], PickGain[_lvl]);
                break;
            case SkateCore.EvKnock when e.Length >= 3:
                if (e[2] == Id && e[1] < _sus.Length) Add(e[1], RamGain[_lvl]);
                if (e[1] == Id && _mark >= 0)
                {
                    // врізався — розгін скінчено, хоч куди той покотився
                    _mark = -1;
                    _rest = _rng.Next(RestMin[_lvl], RestMax[_lvl] + 1);
                }
                break;
            case SkateCore.EvSplash when e.Length >= 3 && e[2] >= 0 && e[1] != Id && e[1] < _sus.Length:
                _sus[e[1]] = Sure;
                break;
        }
    }

    void Add(int id, int gain)
    {
        if (gain > 0) _sus[id] = Math.Min(Sure - 1, _sus[id] + gain);
    }

    /// <summary>Думка на тик: спершу (коли «відпустив клавішу») вирішує заняття сам, далі клавіші тисне мозок юрми.</summary>
    public void Think(SkateVillager v)
    {
        _now++;
        if (!v.OnIce) { v.Want = -1; _mark = -1; return; }
        if (_core.Trading) Watch(v);
        if (_rest > 0) _rest--;
        if (_core.Trading && v.Fallen == 0 && v.Alarm == 0 && v.Hold == 0)
        {
            if (!Charge(v))
            {
                if (v.Mode == SkateCore.ModeStand)
                {
                    if (v.Stand > StandCap[_lvl]) v.Stand = StandCap[_lvl];
                    if (v.Stand <= 0 && _rng.Next(100) < TaskPct[_lvl]) Task(v);
                }
                // заняття юрми (петля, «до точки», квач) звичайний і сильний інколи кидають посеред — по своє ласощі
                else if (!(v.Mode == SkateCore.ModeItem && v.Item >= 0 && _seat is { } me && Wanted(me, _core.Slots[v.Item].Kind))
                    && SwitchPct[_lvl] > 0 && _rng.Next(100) < SwitchPct[_lvl]) Task(v);
            }
        }
        _core.Think(v);
    }

    /// <summary>Хто вперто їде до мене — підозра (людина-мисливець цілиться, селянин їде до випадкової точки).</summary>
    void Watch(SkateVillager me)
    {
        var every = WatchEvery[_lvl];
        if (every == 0 || _now % every != 0) return;
        var v = _core.V;
        for (var i = 0; i < v.Length; i++)
        {
            if (i == Id) continue;
            var q = v[i];
            if (_sus[i] > 0 && _sus[i] < Sure) _sus[i]--;
            if (!q.Upright) { _closer[i] = _dist[i] = 0; continue; }
            var d = (Math.Abs(q.X - me.X) + Math.Abs(q.Y - me.Y)) / SkateCore.Fp;
            if (d < WatchRange && _dist[i] > 0 && d <= _dist[i] - CloserBy)
            {
                if (++_closer[i] >= CloserTimes)
                {
                    _closer[i] = 0;
                    Add(i, ApproachGain[_lvl]);
                }
            }
            else if (d >= _dist[i]) _closer[i] = 0;
            _dist[i] = d;
        }
    }

    /// <summary>По ласощі зі свого кошика, що ще не взяте: найближче, видне по льоду. Нема такого — заняття вибере юрма.</summary>
    void Task(SkateVillager v)
    {
        var s = _seat;
        if (s is null) return;
        int best = -1;
        long bestD = long.MaxValue;
        for (var k = 0; k < _core.Slots.Length; k++)
        {
            var it = _core.Slots[k];
            if (!it.Here || !Wanted(s, it.Kind) || !SkateMap.Clear(v.X / SkateCore.Fp, v.Y / SkateCore.Fp, it.X, it.Y)) continue;
            long dx = (long)it.X * SkateCore.Fp - v.X, dy = (long)it.Y * SkateCore.Fp - v.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 < bestD) { bestD = d2; best = k; }
        }
        if (best < 0) return;
        var slot = _core.Slots[best];
        v.Mode = SkateCore.ModeItem;
        v.ModeLeft = 40;
        v.Item = best;
        v.Chase = -1;
        v.Tx = slot.X * SkateCore.Fp;
        v.Ty = slot.Y * SkateCore.Fp;
        v.Cruise = _rng.Next(SkateCore.Vmax / 3, SkateCore.Vmax * 3 / 4);
        v.Stand = 0;
    }

    static bool Wanted(SkateSeat s, int kind)
    {
        for (var i = 0; i < 4; i++)
            if (s.List[i] == kind && !s.Got[i]) return true;
        return false;
    }

    /// <summary>
    /// Розгін на підозрілого біля ополонки. Сильний їде так, щоб удар ніс того до води (ціль — центр ополонки за ним),
    /// і лише коли він справді між ботом і водою; звичайний — просто на нього, куди винесе. Легкий не таранить.
    /// </summary>
    bool Charge(SkateVillager v)
    {
        if (_lvl == 0 || _rest > 0) return false;
        if (_mark >= 0)
        {
            var m = _core.V[_mark];
            if (!m.Upright || v.Mode is not (SkateCore.ModeChase or SkateCore.ModePoint)) { _mark = -1; return false; }
            return true;
        }
        if (--_huntIn > 0) return false;
        _huntIn = HuntEvery[_lvl];
        if (_rng.Next(100) >= HuntPct[_lvl]) return false;
        int best = -1, bestS = HuntFrom[_lvl] - 1;
        for (var i = 0; i < _sus.Length; i++)
            if (i != Id && _sus[i] > bestS && _core.V[i].Upright) { bestS = _sus[i]; best = i; }
        if (best < 0) return false;
        var q = _core.V[best];
        int px = v.X / SkateCore.Fp, py = v.Y / SkateCore.Fp, qx = q.X / SkateCore.Fp, qy = q.Y / SkateCore.Fp;
        if (Math.Abs(qx - px) + Math.Abs(qy - py) > ChaseFrom || !SkateMap.Clear(px, py, qx, qy)) return false;
        var hole = WaterNear(qx, qy, NearWater[_lvl]);
        if (hole is null) return false;
        if (_lvl == 2)
        {
            // таран несе туди, куди їхав: сильний б'є лише тоді, коли підозрілий між ним і водою
            long ax = qx - px, ay = qy - py, bx = hole.X - qx, by = hole.Y - qy;
            var dot = ax * bx + ay * by;
            if (dot <= 0 || dot * dot * 10 < 6 * (ax * ax + ay * ay) * (bx * bx + by * by)) return false;
            v.Mode = SkateCore.ModePoint;
            v.Tx = hole.X * SkateCore.Fp;
            v.Ty = hole.Y * SkateCore.Fp;
            v.ModeLeft = 12;
        }
        else
        {
            v.Mode = SkateCore.ModeChase;
            v.Chase = best;
            v.ModeLeft = 14;
        }
        v.Cruise = SkateCore.Vmax;
        v.Item = -1;
        v.Stand = 0;
        _mark = best;
        return true;
    }

    /// <summary>Відкрита ополонка, до краю якої від (x, y) не далі <paramref name="within"/> од.; null — нема.</summary>
    SkateHole? WaterNear(int x, int y, int within)
    {
        foreach (var h in _core.Holes)
        {
            if (!h.Open) continue;
            long dx = x - h.X, dy = y - h.Y, r = h.R + within;
            if (dx * dx + dy * dy <= r * r) return h;
        }
        return null;
    }
}
