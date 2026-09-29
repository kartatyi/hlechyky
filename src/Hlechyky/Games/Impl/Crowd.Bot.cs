namespace Hlechyky.Games.Impl;

/// <summary>
/// «Юрма» з 🤖 ботами (сам за столом → «🤖 + бот»). Боти-суперники — такі самі гравці, як людина: свій селянин у
/// юрмі, свій список, три камінці. Ходить бот тим самим мозком, що й NPC (<see cref="CrowdCore.Think"/>) — тож у
/// кадрі його не відрізнити від юрби, — лише цілі обирає свої: частіше — лоток зі списку. Купує й стріляє через ті
/// самі <c>BuyAt</c>/<c>ShootAt</c>, що й людина (торг, перезарядка, дальність). Полює за тим, що видно з кадру
/// (<see cref="CrowdEye"/>): хто стрельнув, хто стояв на спалаху, хто смикається не по-селянськи.
///
/// Ботів два: на ярмарку з одним суперником «знайди одного серед 24» надто порожньо, а двоє ще й підозрюють одне
/// одного — їхні постріли й спалахи дають людині сліди. Застрелив одного — раунд іде далі (живих ще двоє).
/// </summary>
public sealed partial class Crowd
{
    /// <summary>Скільки 🤖 ботів сідає до самотньої людини.</summary>
    public const int BotCount = 2;
    /// <summary>Як часто (%) бот, обираючи нову ціль, іде до лотка зі свого списку (решта — як юрма).</summary>
    static readonly int[] ListPick = [35, 55, 65];
    /// <summary>Чи торгується бот (%), дійшовши до свого лотка: легкий часто «передумує».</summary>
    static readonly int[] BuyChance = [50, 85, 100];
    /// <summary>З якої відстані стріляє: звичайний підходить ближче, ніж треба, сильний — з повної дальності.</summary>
    static readonly int[] FireDist = [0, 110, 150];
    /// <summary>Скільки тиків бот женеться за одним, перш ніж махнути рукою (≈ 16 с).</summary>
    const int ChaseTicks = 400;

    sealed class Mind
    {
        public int Hunt = -1, Delay, NextEval, Chase;
    }

    readonly SoloBot _solo = new();
    int[] _bots = [];
    Mind[] _minds = [];
    readonly CrowdEye _eye = new();

    /// <summary>Для тестів: місця ботів цієї партії й рівень.</summary>
    public IReadOnlyList<int> BotsForTests => _bots;
    public CrowdEye EyeForTests => _eye;

    public override bool ActsInLobby => true;

    public override string? CanStart() => _solo.CanStart(Ctx, Seats);

    public override string? SeatBot(int seat) =>
        _started && Array.IndexOf(_bots, seat) >= 0 && !Ctx.Seated(seat) ? LiveBots.Name : null;

    int[] BotSeats() => CrowdBots.FreeSeats(Ctx, Seats, BotCount);

    /// <summary>Поле виду <c>bot</c>: місця ботів у партії (і після неї), у лобі — куди сядуть, якщо кликали.</summary>
    int[]? BotView() =>
        _started && _bots.Length > 0 ? _bots
        : (!_started || _phase == PhaseOver) && _solo.Wanted ? BotSeats()
        : null;

    string BotNick(int seat) => CrowdBots.Nick(_s[seat].Bot, _bots.Length, _s[seat].Nick, SeatNames[seat]);

    void BotsNewRound()
    {
        if (_bots.Length == 0) return;
        _eye.Reset(Core.N);
        _minds = [.. _bots.Select(_ => new Mind())];
    }

    /// <summary>Мозок ботів-гравців — після юрби, перед спільним кроком (як людина: ввід між тиками).</summary>
    void BotsThink()
    {
        if (_bots.Length == 0) return;
        var lvl = LiveBots.Index(_solo.Level);
        var rng = Ctx.Rng;
        for (var b = 0; b < _bots.Length; b++)
        {
            var seat = _bots[b];
            var s = _s[seat];
            if (!s.Active || !s.Alive || s.Me < 0) continue;
            var v = Core.V[s.Me];
            s.MoveAt = _clock;                       // «клавішу тримає» — HeldKeys не відпускає
            if (_phase == PhaseGo && lvl > 0 && Hunt(seat, s, v, _minds[b], lvl)) continue;

            // Куди далі — вирішуємо до Think, інакше він візьме випадкову ціль юрби.
            if (v.Target < 0 && v.Stand == 0 && v.Wander == 0 && !v.Blocked && v.Upright && v.Haggle == 0)
            {
                var need = NeededStall(s, v);
                if (need >= 0 && rng.Next(100) < ListPick[lvl])
                {
                    var st = CrowdMap.Stalls[need];
                    Core.Aim(v, rng.Next(2) == 0 ? st.C0 : st.C1, need);
                }
            }
            var had = v.Target >= 0;
            Core.Think(v);
            if (had && v.Target < 0 && v.Stand > 0 && _phase == PhaseGo) TryBuy(seat, s, v, lvl);
        }
    }

    /// <summary>Найближчий нескуплений лоток зі списку; -1 — усе куплено.</summary>
    static int NeededStall(CrowdSeat s, CrowdVillager v)
    {
        int best = -1;
        long bd = long.MaxValue;
        for (var i = 0; i < 4; i++)
        {
            if (s.Done[i]) continue;
            var st = CrowdMap.Stalls[s.List[i]];
            var d = CrowdCore.Dist2(v, st.Fx, st.Fy);
            if (d < bd) { bd = d; best = s.List[i]; }
        }
        return best;
    }

    /// <summary>Дійшов і став: на своєму прилавку — торгується (легкий інколи передумує), сильний зрідка й блефує.</summary>
    void TryBuy(int seat, CrowdSeat s, CrowdVillager v, int lvl)
    {
        var k = CrowdCore.CounterAt(v);
        if (k < 0 || s.BuyCool > 0) return;
        var mine = false;
        for (var i = 0; i < 4; i++) if (s.List[i] == k && !s.Done[i]) mine = true;
        var rng = Ctx.Rng;
        if (mine ? rng.Next(100) < BuyChance[lvl] : lvl == 2 && rng.Next(100) < 8) BuyAt(seat, k);
    }

    /// <summary>
    /// Полювання: раз на <see cref="CrowdEye.HuntEvery"/> тиків — чи нема кого ловити; ловить — іде до нього юрбяною
    /// ходою (ціль — точка підозрілого, переглядається раз на 10 тиків) і після реакції стріляє з
    /// <see cref="FireDist"/>. true — цього тика бот зайнятий гонитвою (Think уже кликано).
    /// </summary>
    bool Hunt(int seat, CrowdSeat s, CrowdVillager v, Mind m, int lvl)
    {
        if (m.Hunt < 0 && s.Stones > 0 && _t >= m.NextEval)
        {
            m.NextEval = _t + CrowdEye.HuntEvery[lvl];
            var id = _eye.Best(s.Me, CrowdEye.HuntMin[lvl], i => Core.V[i].Upright);
            if (id >= 0)
            {
                m.Hunt = id;
                m.Delay = CrowdEye.Reaction[lvl];
                m.Chase = ChaseTicks;
            }
        }
        if (m.Hunt < 0) return false;
        var q = Core.V[m.Hunt];
        if (!q.Upright || s.Stones == 0 || --m.Chase <= 0)
        {
            m.Hunt = -1;
            return false;
        }
        if (m.Delay > 0) m.Delay--;
        var d2 = CrowdCore.Dist2(q, v.X, v.Y);
        if (m.Delay == 0 && s.ShotCool == 0 && v.Haggle == 0 && d2 <= (long)FireDist[lvl] * FireDist[lvl])
        {
            ShootAt(seat, m.Hunt);
            m.Hunt = -1;
            v.Want = -1;
            return true;
        }
        if (v.Haggle == 0 && (v.Target < 0 || _t % 10 == 0)) CrowdCore.AimAt(v, q.X, q.Y);
        Core.Think(v);
        return true;
    }

    /// <summary>Після кроку: підозра тане, а сильний бот ще й придивляється, хто ходить не по-селянськи.</summary>
    void BotsWatch()
    {
        if (_bots.Length == 0) return;
        _eye.Tick(_t);
        if (_solo.Level != LiveBots.Level.Hard) return;
        foreach (var q in Core.V)
            _eye.Watch(q.Id, q.Moving, q.Dir, q.Blocked && q.Want >= 0 && q.Upright);
    }

    /// <summary>Камінець полетів: стрілець — точно не NPC, це бачать усі.</summary>
    void BotsSawShot(int shooter)
    {
        if (_bots.Length > 0) _eye.Add(shooter, CrowdEye.Shooter);
    }

    /// <summary>Лоток спалахнув: хто стоїть на його прилавку — під підозрою (і бот-покупець, і людина, і NPC).</summary>
    void BotsSawFlash(int stall)
    {
        if (_bots.Length == 0) return;
        foreach (var q in Core.V)
            if (q.Upright && CrowdCore.CounterAt(q) == stall) _eye.Add(q.Id, CrowdEye.Flash);
    }

    /// <summary>Кінець партії з ботами: без очок у таблицю й без ачівок (<see cref="CrowdBots.Finish"/>).</summary>
    void FinishWithBots(int[] winners, string line)
    {
        var human = -1;
        for (var i = 0; i < Seats; i++)
            if (_s[i].Active && !_s[i].Bot) human = i;
        CrowdBots.Finish(Ctx, winners, human, human >= 0 ? _s[human].Nick : "", _solo.Level, $"{Info.Title}: {line}", line);
    }
}
