namespace Hlechyky.Games.Impl;

/// <summary>
/// «🤖 бот» Гарячого горщика — гравець-бот, коли людина сидить за столом сама. Його селянин — такий самий гравець, як
/// людина (<c>Owner</c> = місце бота: ляпас його оглушує, вибух у руках виводить з раунду, очки — за тими ж правилами),
/// а думає він тут, через той самий ввід, що й людина: <c>Want</c>, <see cref="PotatoCore.TryPass"/>,
/// <see cref="PotatoCore.TrySlap"/>. Гуляє мозком юрми (<see cref="PotatoCore.Walk"/>) і зрідка ляскає з тією самою
/// частотою, що й селяни, — ззовні його не відрізнити від селянина, поки сам себе не видасть полюванням.
///
/// Рівні: легкий довго роздивляється горщик у руках і часто гуляє з ним до іскор (рве в руках — людина бере раунд),
/// ні на кого не полює; звичайний спихає горщик, як селянин-шукач, помічає тих, хто ляснув селянина чи оглух від
/// ляпаса, і зрідка перевіряє таких ляпасом; сильний спихає швидко, стежить, хто довго й уперто йде до нього («людина
/// полює»), полює на підозрілого, несе йому горщик і передає саме йому. Сильний теж помиляється: ляснув селянина —
/// отетерів на 3 с і видав себе.
/// </summary>
public sealed class PotatoPilot(PotatoCore core, Random rng, LiveBots.Level level)
{
    /// <summary>Скільки стоїть «ой, гаряче», отримавши горщик (тики): легкий, звичайний, сильний.</summary>
    static readonly int[] CatchMin = [15, 4, 3], CatchMax = [45, 20, 10];
    /// <summary>Скільки ще вагається впритул перед передачею.</summary>
    static readonly int[] WaitMax = [16, 8, 3];
    /// <summary>Імовірність (%) «потримаю й погуляю» з горщиком і до якого диму: легкий — до «трусить», звичайний — до іскор.</summary>
    static readonly int[] HoldPct = [45, 15, 0], HoldUntil = [3, 2, 1];
    /// <summary>Як часто (тики) дивиться на юрму — хто йде до нього; 0 — не дивиться.</summary>
    static readonly int[] WatchEvery = [0, 20, 10];
    /// <summary>Скільки підозри додає «уперто йде до мене» і «ляснув селянина — сам отетерів».</summary>
    static readonly int[] ApproachGain = [0, 60, 140], SlapperGain = [0, 250, 450];
    /// <summary>З якої підозри полює (0..1000) і як часто переглядає, на кого.</summary>
    static readonly int[] HuntFrom = [2000, 600, 250], HuntEvery = [0, 25, 6];
    /// <summary>Звичайний іде перевіряти не завжди: шанс (%) на кожному перегляді; і скільки тиків ганяється, поки не кине.</summary>
    static readonly int[] HuntPct = [0, 35, 100], HuntFor = [0, 150, 400];
    /// <summary>
    /// Оглушив людину — відпочиває від полювання (тики, від і до): інакше, раз упізнавши, ляскав би її що 4 с до кінця
    /// раунду, і від сильного бота не було б рятунку. Людина за цей час може відійти в юрму чи дати здачі.
    /// </summary>
    static readonly int[] RestMin = [0, 300, 150], RestMax = [0, 500, 300];
    /// <summary>Далі цього (од., по осях) звичайний на підозрілого не полює — лише коли той поруч.</summary>
    const int NormalHuntReach = 256;
    /// <summary>«Близько» для «йде до мене» — 6 клітинок, і за раз наблизитись щонайменше на 9 од. (три кроки).</summary>
    const int WatchRange = 192, CloserBy = 9, CloserTimes = 3;
    /// <summary>Підозра, що означає «точно людина» (оглух від ляпаса), і «точно селянин» (ляснув — сам отетерів).</summary>
    public const int Sure = 1000, Cleared = -1000;

    readonly PotatoCore _core = core;
    readonly Random _rng = rng;
    readonly int _lvl = LiveBots.Index(level);

    int[] _sus = [], _dist = [], _closer = [];
    int _now, _huntIn, _huntLeft, _suspect = -1, _rest;
    bool _dazed, _carrying;
    int _stand, _hold, _wait, _chase = -1, _chaseAt;

    /// <summary>Id свого селянина в цьому раунді.</summary>
    public int Id { get; private set; } = -1;
    public LiveBots.Level Level => level;
    /// <summary>Для тестів: підозра на селянина й на кого зараз полює.</summary>
    public int SuspicionOf(int id) => id >= 0 && id < _sus.Length ? _sus[id] : 0;
    public int Suspect => _suspect;

    /// <summary>Новий раунд: свій селянин, чиста пам'ять.</summary>
    public void Reset(int id, int n)
    {
        Id = id;
        _sus = new int[n];
        _dist = new int[n];
        _closer = new int[n];
        _now = _huntLeft = _rest = 0;
        _huntIn = HuntEvery[_lvl];
        _suspect = -1;
        _dazed = _carrying = false;
        _stand = _hold = 0;
        _wait = _chase = -1;
    }

    /// <summary>
    /// Подія ядра (кличе гра, збираючи журнал): ляпас каже, хто гравець. Оглух той, кого ляснули, — то гравець, а
    /// гравців тут двоє, тож це людина. Ляснув і сам отетерів — ляснув селянина: селяни так роблять рідко, людина — часто.
    /// </summary>
    public void See(PotatoEvent e)
    {
        if (e.Kind != PotatoEvent.Slap || Id < 0) return;
        if (e.A == Id)
        {
            // власна перевірка: оглух — людина, отетерів я — то був селянин
            if (e.B >= 0 && e.B < _sus.Length) _sus[e.B] = e.C == e.B ? Sure : Cleared;
            _suspect = -1;
            _huntLeft = 0;
            if (e.C == e.B) _rest = _rng.Next(RestMin[_lvl], RestMax[_lvl] + 1);
            return;
        }
        if (e.C == e.B && e.B != Id && e.B >= 0 && e.B < _sus.Length) _sus[e.B] = Sure;
        else if (e.C == e.A && e.A >= 0 && e.A < _sus.Length && _sus[e.A] > Cleared)
            _sus[e.A] = Math.Min(Sure, _sus[e.A] + SlapperGain[_lvl]);
    }

    /// <summary>Думка на тик: після мозку юрми, до кроку. Лежить чи вибув — нічого; оглушений — стоїть.</summary>
    public void Think(PotatoVillager v)
    {
        _now++;
        if (v.Dead || v.Fallen > 0) return;
        if (v.Stun > 0) { v.Want = -1; _dazed = true; return; }
        if (_dazed)
        {
            _dazed = false;
            PotatoCore.Forget(v);
        }
        if (!_core.Live) { _carrying = false; _core.Walk(v); return; }
        Watch(v);
        if (v.Pot >= 0) { Carry(v); return; }
        if (_carrying)
        {
            _carrying = false;
            PotatoCore.ForgetWalk(v);
        }
        // ляпас «для виду» — так само рідко, як селяни (інакше бот єдиний у юрмі ніколи б не ляскав)
        if (v.SlapCool == 0)
        {
            var t = _core.SlapTarget(v);
            if (t >= 0 && _rng.Next(10_000) < (_core.V[t].Pot >= 0 ? PotatoCore.BotSlapCarrierPer10k : PotatoCore.BotSlapPer10k))
            {
                _core.TrySlap(v, null);
                return;
            }
        }
        if (Hunt(v)) return;
        if (_lvl > 0 && Shy(v)) return;
        _core.Walk(v);
    }

    /// <summary>Хто вперто йде до мене — підозра (людина, що полює, йде до цілі; селянин — до випадкової клітинки).</summary>
    void Watch(PotatoVillager me)
    {
        var every = WatchEvery[_lvl];
        if (every == 0 || _now % every != 0) return;
        var v = _core.V;
        for (var i = 0; i < v.Length; i++)
        {
            if (i == Id) continue;
            var q = v[i];
            if (_sus[i] > 0 && _sus[i] < Sure) _sus[i]--;
            if (!q.Standing || q.Pot >= 0) { _closer[i] = 0; _dist[i] = 0; continue; }
            var d = Math.Abs(q.X - me.X) + Math.Abs(q.Y - me.Y);
            if (d < WatchRange && _dist[i] > 0 && d <= _dist[i] - CloserBy)
            {
                if (++_closer[i] >= CloserTimes)
                {
                    _closer[i] = 0;
                    if (_sus[i] > Cleared) _sus[i] = Math.Min(Sure - 1, _sus[i] + ApproachGain[_lvl]);
                }
            }
            else if (d >= _dist[i]) _closer[i] = 0;
            _dist[i] = d;
        }
    }

    /// <summary>Найпідозріліший на ногах (не я), якщо підозра дотягує до порога рівня; -1 — нікого.</summary>
    int Top(int from)
    {
        int best = -1, bestS = from - 1;
        var v = _core.V;
        for (var i = 0; i < v.Length; i++)
        {
            if (i == Id || !v[i].Standing) continue;
            if (_sus[i] > bestS) { bestS = _sus[i]; best = i; }
        }
        return best;
    }

    /// <summary>
    /// Полювання: раз на кілька тиків вибирає підозрілого, іде до нього й ляскає впритул (оглух — людина, мішень для
    /// горщика; отетерів сам — то був селянин, забуває). Звичайний іде не завжди й лише на близького; легкий не полює.
    /// </summary>
    bool Hunt(PotatoVillager v)
    {
        if (_lvl == 0) return false;
        if (_rest > 0) { _rest--; return false; }
        if (--_huntIn <= 0)
        {
            _huntIn = HuntEvery[_lvl];
            if (_suspect < 0 || _huntLeft <= 0)
            {
                var t = Top(HuntFrom[_lvl]);
                _suspect = -1;
                if (t >= 0 && _rng.Next(100) < HuntPct[_lvl]
                    && (_lvl == 2 || Math.Abs(_core.V[t].X - v.X) + Math.Abs(_core.V[t].Y - v.Y) <= NormalHuntReach))
                {
                    _suspect = t;
                    _huntLeft = HuntFor[_lvl];
                    PotatoCore.ForgetWalk(v);
                }
            }
        }
        if (_suspect < 0) return false;
        var q = _core.V[_suspect];
        if (!q.Standing || _sus[_suspect] <= 0 || --_huntLeft <= 0)
        {
            _suspect = -1;
            PotatoCore.ForgetWalk(v);
            return false;
        }
        var d2 = PotatoCore.Dist2(q, v.X, v.Y);
        if (v.SlapCool == 0 && q.Upright && d2 <= (long)PotatoCore.SlapRange * PotatoCore.SlapRange)
        {
            _core.TrySlap(v, _suspect);
            return true;
        }
        // рука ще не відійшла чи він оглушений — тримається поруч, не пре впритул (так дивно ходять лише люди)
        if (d2 <= (long)PotatoCore.SlapRange * PotatoCore.SlapRange) { v.Want = -1; return true; }
        _core.Toward(v, q.X, q.Y);
        return true;
    }

    /// <summary>Горщик поруч — відходить, як боязкий селянин (менше ловить горщиків).</summary>
    bool Shy(PotatoVillager v)
    {
        if (v.Shy > 0)
        {
            if (--v.Shy == 0 || v.ShyFrom < 0 || _core.V[v.ShyFrom].Pot < 0) { PotatoCore.ForgetWalk(v); return false; }
            PotatoCore.Away(v, _core.V[v.ShyFrom]);
            return true;
        }
        var c = _core.NearCarrier(v, out var heat);
        if (c < 0 || _rng.Next(1000) >= PotatoCore.ShyMilli * (heat >= 3 ? 2 : 1)) return false;
        v.Shy = _rng.Next(PotatoCore.ShyMin, PotatoCore.ShyMax + 1);
        v.ShyFrom = c;
        v.Stand = v.Wander = 0;
        PotatoCore.Away(v, _core.V[c]);
        return true;
    }

    /// <summary>
    /// Горщик у руках: мить стоїть, далі (легкий і звичайний — інколи) гуляє з ним до пори, а тоді шукає, кому віддати.
    /// Сильний і звичайний, коли поруч підозрілий, тицяють горщик саме йому (рвоне в людини — +2 і вона вибула).
    /// </summary>
    void Carry(PotatoVillager v)
    {
        var heat = _core.Heat(v.Pot);
        if (!_carrying)
        {
            _carrying = true;
            PotatoCore.ForgetWalk(v);
            _stand = _rng.Next(CatchMin[_lvl], CatchMax[_lvl] + 1);
            _hold = _rng.Next(100) < HoldPct[_lvl] ? _rng.Next(PotatoCore.HoldOnMin, PotatoCore.HoldOnMax + 1) : 0;
            _wait = _chase = -1;
        }
        if (_stand > 0)
        {
            _stand--;
            v.Want = -1;
            return;
        }
        if (_hold > 0 && heat < HoldUntil[_lvl])
        {
            _hold--;
            _core.Walk(v);
            return;
        }
        var p = _core.Pots[v.Pot];
        var ready = _core.Now - p.Since >= PotatoCore.HoldMin;
        var mark = _lvl > 0 ? Top(HuntFrom[_lvl]) : -1;
        if (mark >= 0 && ready && PotatoCore.Dist2(_core.V[mark], v.X, v.Y) <= (long)PotatoCore.PassRange * PotatoCore.PassRange
            && _core.TryPass(v, mark) == PotatoNo.None)
            return;
        if (ready && _core.PassTarget(v) >= 0 && !(mark >= 0 && _lvl == 2 && heat < 2))
        {
            if (_wait < 0) _wait = _rng.Next(0, WaitMax[_lvl] + 1);
            if (_wait-- == 0)
            {
                _core.TryPass(v, null);
                return;
            }
        }
        // до кого йти: сильний (поки не іскрить) — до підозрілого, інакше — до найближчого, кому можна
        if (mark >= 0 && _lvl == 2 && heat < 2) { _core.Toward(v, _core.V[mark].X, _core.V[mark].Y); return; }
        if (_chase < 0 || --_chaseAt <= 0 || !_core.V[_chase].Standing || _core.V[_chase].Pot >= 0)
        {
            _chase = _core.Nearest(v, p);
            _chaseAt = _rng.Next(10, 26);
        }
        if (_chase < 0) { _core.Walk(v); return; }
        _core.Toward(v, _core.V[_chase].X, _core.V[_chase].Y);
    }
}
