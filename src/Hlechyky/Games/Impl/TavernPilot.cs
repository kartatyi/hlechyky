namespace Hlechyky.Games.Impl;

/// <summary>
/// «🤖 бот» Корчми — гравець-бот, коли людина сидить за столом сама. Його відвідувач — такий самий гравець, як людина
/// (<c>Owner</c> = місце бота: три серця, кухлі в різних місцях, корчмар виносить за двері, очки — ті самі), ходить
/// мозком юрми (<see cref="TavernCore.Think"/>: смуга, вагання, лави, переступання) — ззовні його не відрізнити. Що
/// відрізняє: куди він іде (до бочки, де ще не пив) і що робить сам через ті самі дії, що й людина, — «drink» і «punch»
/// (гра дає їх делегатами, тож перевірки й відмови ті самі).
///
/// Рівні: легкий рідко йде до «своєї» бочки, довго стоїть і вечеряє, п'є не одразу, першим не б'є; звичайний п'є
/// впевненіше й зрідка перевіряє кулаком підозрілого поруч, коли корчмар не дивиться; сильний п'є швидко, стежить, хто
/// п'є в різних місцях і хто махає кулаками, полює на підозрілого — але теж лише тоді, коли корчмар спокійний.
/// </summary>
public sealed class TavernPilot(TavernCore core, Random rng, LiveBots.Level level, Func<int?, ActResult> punch, Func<ActResult> drink)
{
    /// <summary>Імовірність (%), що нова ціль — приступка, де ще не пив: легкий, звичайний, сильний.</summary>
    static readonly int[] TaskPct = [25, 60, 95];
    /// <summary>Найдовше стояння/сидіння (тики): юрма стоїть і по 20 с, сильний так не марнує час.</summary>
    static readonly int[] StandCap = [750, 100, 12];
    /// <summary>Скільки «думає», ставши на нову приступку, перш ніж хильнути.</summary>
    static readonly int[] SipMin = [10, 3, 1], SipMax = [30, 12, 5];
    /// <summary>Підозра: за кухоль у ще одному місці, за «махнув — і сам отетерів», за «винесли при корчмарі».</summary>
    static readonly int[] DrinkGain = [0, 150, 300], DazeGain = [0, 150, 300], ThrowGain = [0, 200, 400];
    static readonly int[] WatchEvery = [0, 12, 6];
    /// <summary>З якої підозри полює, як часто думає про це, шанс (%) і як далеко (од., по осях) іде за підозрілим.</summary>
    static readonly int[] HuntFrom = [2000, 500, 250], HuntEvery = [0, 25, 8], HuntPct = [0, 35, 100], HuntReach = [0, 120, 200];
    /// <summary>Скільки тиків ганяється, поки не кине, і скільки відпочиває після удару.</summary>
    static readonly int[] HuntFor = [0, 100, 150], RestMin = [0, 200, 100], RestMax = [0, 400, 200];
    /// <summary>Дати здачі тому, хто вдарив (%): як селяни (35 %), сильний — завжди, коли вдарила людина.</summary>
    const int PaybackPct = 35;
    public const int Sure = 1000, Cleared = -1000;

    readonly TavernCore _core = core;
    readonly Random _rng = rng;
    readonly int _lvl = LiveBots.Index(level);

    TavernSeat? _seat;
    int[] _sus = [], _drank = [];
    int _now, _huntIn, _huntLeft, _rest, _mark = -1, _sip = -1, _hearts, _hitBy = -1, _payback = -1, _paybackWait;

    public int Id { get; private set; } = -1;
    public LiveBots.Level Level => level;
    public int SuspicionOf(int id) => id >= 0 && id < _sus.Length ? _sus[id] : 0;
    public int Mark => _mark;

    /// <summary>Новий раунд: свій відвідувач і місце (серця, де вже пив), чиста пам'ять.</summary>
    public void Reset(int id, int n, TavernSeat seat)
    {
        Id = id;
        _seat = seat;
        _sus = new int[n];
        _drank = new int[n];
        _now = _huntLeft = _rest = _paybackWait = 0;
        _huntIn = HuntEvery[_lvl];
        _mark = _hitBy = _payback = _sip = -1;
        _hearts = seat.Hearts;
    }

    /// <summary>
    /// Подія гри (кадр): удар <c>[1, хто, кого, як, місце]</c> і «за двері» <c>[3, хто, місце]</c>. Свій удар каже
    /// правду: ціль упала (а я отетерів) — то селянин; втратила серце — людина. Удар у мене запам'ятовуємо: якщо з ним
    /// пропало серце — била людина.
    /// </summary>
    public void See(int[] e)
    {
        if (Id < 0 || e.Length < 3) return;
        if (e[0] == 1 && e.Length >= 4)
        {
            int a = e[1], t = e[2], how = e[3];
            if (a < 0 || a >= _sus.Length) return;
            if (a == Id)
            {
                if (t >= 0 && t < _sus.Length) _sus[t] = how == 1 ? Cleared : Sure;
                if (t >= 0) { _mark = -1; _huntLeft = 0; _rest = _rng.Next(RestMin[_lvl], RestMax[_lvl] + 1); }
                return;
            }
            if (t == Id)
            {
                _hitBy = a;
                if (_rng.Next(100) < PaybackPct) { _payback = a; _paybackWait = _rng.Next(0, 16); }
                return;
            }
            // махнув — ціль упала, а він отетерів: так видає себе людина (хоча й бот зрідка так само)
            if (how == 1) Add(a, DazeGain[_lvl]);
        }
        else if (e[0] == 3 && e[1] >= 0 && e[1] < _sus.Length && e[1] != Id) Add(e[1], ThrowGain[_lvl]);
    }

    void Add(int id, int gain)
    {
        if (gain > 0 && _sus[id] > Cleared) _sus[id] = Math.Min(Sure - 1, _sus[id] + gain);
    }

    /// <summary>Думка на тик — до мозку юрми; дії — лише через делегати гри (ті самі, що в людини).</summary>
    public void Think(TavernGuest v)
    {
        _now++;
        var s = _seat;
        if (s is null || v.Out) return;
        if (s.Hearts < _hearts)
        {
            // пропало серце — вдарила людина (бот сердець не забирає); сильний дає здачі завжди
            if (_hitBy >= 0 && _hitBy < _sus.Length) _sus[_hitBy] = Sure;
            if (_lvl == 2 && _hitBy >= 0) { _payback = _hitBy; _paybackWait = _rng.Next(0, 8); }
            _hearts = s.Hearts;
        }
        if (_rest > 0) _rest--;
        if (_core.Open) Watch();
        if (!v.Free) return;
        if (_core.Open)
        {
            if (Payback(v) || Sip(v, s) || Hunt(v)) return;
            if (v.Stand > StandCap[_lvl]) v.Stand = StandCap[_lvl];
            if (v.Target < 0 && v.Stand == 0 && v.Wander == 0 && _mark < 0 && _rng.Next(100) < TaskPct[_lvl]) Task(v, s);
        }
        _core.Think(v);
    }

    /// <summary>Хто п'є в різних місцях: людина збирає три кухлі, бот п'є де прийдеться.</summary>
    void Watch()
    {
        var every = WatchEvery[_lvl];
        if (every == 0 || _now % every != 0) return;
        var v = _core.V;
        for (var i = 0; i < v.Length; i++)
        {
            if (i == Id) continue;
            var q = v[i];
            if (_sus[i] > 0 && _sus[i] < Sure) _sus[i]--;
            if (q.Drink <= 0 || q.DrinkPlace < 0) continue;
            var bit = 1 << q.DrinkPlace;
            if ((_drank[i] & bit) != 0) continue;
            var had = _drank[i] != 0;
            _drank[i] |= bit;
            if (had) Add(i, DrinkGain[_lvl]);
        }
    }

    /// <summary>Стоїть на приступці, де ще не пив, — хильнути (дія «drink»), трохи подумавши.</summary>
    bool Sip(TavernGuest v, TavernSeat s)
    {
        var p = TavernCore.PlaceAt(v);
        if (p < 0 || s.Places[p] || v.Sit || v.DrinkCool > 0) { _sip = -1; return false; }
        if (_sip < 0) _sip = _rng.Next(SipMin[_lvl], SipMax[_lvl] + 1);
        if (_sip-- > 0) { v.Want = -1; return true; }
        _sip = -1;
        return drink().Ok;
    }

    /// <summary>До приступки, де ще не пив: сильний — до найближчої, решта — до випадкової з тих.</summary>
    void Task(TavernGuest v, TavernSeat s)
    {
        int best = -1;
        long bestD = long.MaxValue;
        var places = TavernMap.Places;
        var start = _rng.Next(places.Length);
        for (var j = 0; j < places.Length; j++)
        {
            var pl = places[(start + j) % places.Length];
            if (s.Places[pl.I]) continue;
            var cell = pl.Cells[_rng.Next(pl.Cells.Length)];
            long dx = TavernMap.CenterX(cell) - v.X, dy = TavernMap.CenterY(cell) - v.Y;
            var d2 = _lvl == 2 ? dx * dx + dy * dy : j;
            if (d2 < bestD) { bestD = d2; best = cell; }
        }
        if (best < 0) return;
        _core.Aim(v, best, TavernMap.PlaceOf[best]);
    }

    /// <summary>Здача тому, хто вдарив: лише коли корчмар спокійний і той ще поруч.</summary>
    bool Payback(TavernGuest v)
    {
        if (_payback < 0) return false;
        if (_paybackWait > 0) { _paybackWait--; return false; }
        var id = _payback;
        _payback = -1;
        return Swing(v, _core.V[id]);
    }

    /// <summary>Кулаком того, хто поруч: обертається (по довшій осі) і б'є — лише при спокійному корчмарі.</summary>
    bool Swing(TavernGuest v, TavernGuest q)
    {
        if (!_core.Barman.Calm || v.Sit || v.PunchCool > 0 || !q.Upright) return false;
        int dx = q.X - v.X, dy = q.Y - v.Y;
        if ((long)dx * dx + (long)dy * dy > (long)TavernCore.PunchReach * TavernCore.PunchReach * 3 / 4) return false;
        var dir = Math.Abs(dx) >= Math.Abs(dy) ? (dx >= 0 ? 0 : 2) : (dy >= 0 ? 1 : 3);
        return punch(dir).Ok;
    }

    /// <summary>
    /// Полювання: раз на кілька тиків вибирає найпідозрілішого поблизу, іде до його клітинки й б'є впритул — лише коли
    /// корчмар спокійний (при ньому кидає й іде пити: стояти над кимось, поки корчмар дивиться, — теж видати себе).
    /// Два кухлі вже є — не відволікається, допиває третій. Легкий першим не б'є.
    /// </summary>
    bool Hunt(TavernGuest v)
    {
        if (_lvl == 0 || _rest > 0 || (_seat?.Mugs ?? 0) >= 2) { _mark = -1; return false; }
        if (!_core.Barman.Calm) { _mark = -1; return false; }
        if (_mark < 0)
        {
            if (--_huntIn > 0) return false;
            _huntIn = HuntEvery[_lvl];
            if (_rng.Next(100) >= HuntPct[_lvl]) return false;
            int best = -1, bestS = HuntFrom[_lvl] - 1;
            for (var i = 0; i < _sus.Length; i++)
            {
                var q = _core.V[i];
                if (i == Id || !q.Upright || _sus[i] <= bestS) continue;
                if (Math.Abs(q.X - v.X) + Math.Abs(q.Y - v.Y) > HuntReach[_lvl]) continue;
                bestS = _sus[i];
                best = i;
            }
            if (best < 0) return false;
            _mark = best;
            _huntLeft = HuntFor[_lvl];
        }
        var m = _core.V[_mark];
        if (!m.Upright || _sus[_mark] <= 0 || --_huntLeft <= 0) { _mark = -1; return false; }
        if (Swing(v, m)) return true;
        long dx = m.X - v.X, dy = m.Y - v.Y;
        if (dx * dx + dy * dy <= (long)TavernCore.PunchReach * TavernCore.PunchReach / 2)
        {
            v.Want = -1;                                    // поруч — чекає, поки кулак відпочине
            return true;
        }
        if (v.Sit) return false;                            // встане мозок юрми
        var cell = TavernMap.CellOf(m.X, m.Y);
        if (v.Target != cell || _now % 10 == 0) _core.Aim(v, cell, -1);
        return false;
    }
}
