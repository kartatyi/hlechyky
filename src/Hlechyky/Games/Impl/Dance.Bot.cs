namespace Hlechyky.Games.Impl;

/// <summary>
/// «Вечорниці» з 🤖 ботами (сам за столом → «🤖 + бот»). Бот-суперник — такий самий танцюрист-гравець, як людина:
/// ходить мозком юрби (<see cref="DanceCore.Think"/>), фігуру показує тією самою <see cref="DanceCore.Perform"/> через
/// план на тик (як NPC — тож і в кадрі фігура стоїть рівно ті самі 14 кадрів), але частіше тримається кола й рідше
/// збивається — іде по стрічку. Ляпає через ту саму <c>SlapAt</c>, що й людина (перезарядка, дальність, отетеріння
/// за ляпас по NPC). Кого ляпати — з того, що видно в кадрі (<see cref="CrowdEye"/>): хто вже бив, над ким частіше
/// «?», хто смикається не по-танцюристськи.
///
/// Ботів два — з тих самих причин, що в Юрмі: одного серед 24 надто важко знайти, а двоє дають сліди одне на одного.
/// </summary>
public sealed partial class Dance
{
    public const int BotCount = 2;
    /// <summary>Як часто (%) бот поза колом обирає ціль у колі (у юрби — 35).</summary>
    static readonly int[] RingPickBot = [30, 50, 60];
    /// <summary>Як часто (%) бот у колі лишається в колі (у юрби — 65).</summary>
    static readonly int[] RingStayBot = [55, 75, 85];
    /// <summary>Промахи бота на виклик, ‰: замріявся / спізнився / переплутав. Звичайний — як юрба (9 %).</summary>
    static readonly int[] NoneMilli = [60, 15, 5], LateMilli = [80, 40, 20], WrongMilli = [60, 35, 10];
    /// <summary>З якої відстані ляпає (дальність ляпаса з id — 52).</summary>
    static readonly int[] FireDist = [0, 32, 44];
    const int ChaseTicks = 400;

    sealed class Mind
    {
        public int Hunt = -1, Delay, NextEval, Chase;
    }

    readonly SoloBot _solo = new();
    int[] _bots = [];
    Mind[] _minds = [];
    readonly CrowdEye _eye = new();

    public IReadOnlyList<int> BotsForTests => _bots;
    public CrowdEye EyeForTests => _eye;

    public override bool ActsInLobby => true;

    public override string? CanStart() => _solo.CanStart(Ctx, Seats);

    public override string? SeatBot(int seat) =>
        _started && Array.IndexOf(_bots, seat) >= 0 && !Ctx.Seated(seat) ? LiveBots.Name : null;

    int[] BotSeats() => CrowdBots.FreeSeats(Ctx, Seats, BotCount);

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

    /// <summary>Музики вигукнули фігуру: бот-гравець, як і юрба, одразу вирішує, коли й що станцює.</summary>
    void BotsPlan()
    {
        if (_bots.Length == 0) return;
        var lvl = LiveBots.Index(_solo.Level);
        var rng = Ctx.Rng;
        foreach (var seat in _bots)
        {
            var s = _s[seat];
            if (!s.Active || !s.Alive || s.Me < 0) continue;
            var v = Core.V[s.Me];
            if (!v.Upright) continue;
            var r = rng.Next(1000);
            if (r < NoneMilli[lvl]) continue;
            if (r < NoneMilli[lvl] + LateMilli[lvl])
            {
                v.PlanAt = _beat + rng.Next(DanceCore.Late + 1, DanceCore.LateMax + 1);
                v.PlanFig = _callFig;
                continue;
            }
            // сильний тисне рівніше (−2…+5), решта — як юрба (−5…+10)
            v.PlanAt = _beat + (lvl == 2 ? rng.Next(-2, 3) + rng.Next(0, 4) : rng.Next(-DanceCore.Early, 4) + rng.Next(0, 8));
            v.PlanFig = r < NoneMilli[lvl] + LateMilli[lvl] + WrongMilli[lvl]
                ? (_callFig + rng.Next(1, DanceCore.Figures)) % DanceCore.Figures : _callFig;
        }
    }

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
            s.MoveAt = _clock;
            if (_phase == PhaseGo && lvl > 0 && Hunt(seat, s, v, _minds[b], lvl)) continue;
            if (v.Target < 0 && v.Stand == 0 && v.Wander == 0 && !v.Blocked && v.Upright && v.Pose == 0 && v.Stun == 0)
            {
                var pick = DanceMap.InCircle(v.X, v.Y) ? RingStayBot[lvl] : RingPickBot[lvl];
                if (rng.Next(100) < pick) Core.Aim(v, DanceMap.Ring[rng.Next(DanceMap.Ring.Length)]);
            }
            Core.Think(v);
        }
    }

    /// <summary>Полювання — як у Юрмі: раз на кілька тиків шукає найпідозрілішого, іде до нього ходою юрби й ляпає.</summary>
    bool Hunt(int seat, DanceSeat s, DanceVillager v, Mind m, int lvl)
    {
        if (m.Hunt < 0 && _t >= m.NextEval)
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
        if (!q.Upright || --m.Chase <= 0)
        {
            m.Hunt = -1;
            return false;
        }
        if (m.Delay > 0) m.Delay--;
        var d2 = DanceCore.Dist2(q, v.X, v.Y);
        if (m.Delay == 0 && s.SlapCool == 0 && v.Pose == 0 && v.Stun == 0 && d2 <= (long)FireDist[lvl] * FireDist[lvl])
        {
            SlapAt(seat, m.Hunt);
            m.Hunt = -1;
            v.Want = -1;
            return true;
        }
        if (v.Pose == 0 && v.Stun == 0 && (v.Target < 0 || _t % 10 == 0)) DanceCore.AimAt(v, q.X, q.Y);
        Core.Think(v);
        return true;
    }

    void BotsWatch()
    {
        if (_bots.Length == 0) return;
        _eye.Tick(_t);
        if (_solo.Level != LiveBots.Level.Hard) return;
        foreach (var q in Core.V)
            _eye.Watch(q.Id, q.Moving, q.Dir, q.Blocked && q.Want >= 0 && q.Upright);
    }

    void BotsSawShot(int slapper)
    {
        if (_bots.Length > 0) _eye.Add(slapper, CrowdEye.Shooter);
    }

    /// <summary>Суд такту: над ким «?» — трохи підозри (юрба збивається лише в 9 % тактів, новачок — частіше).</summary>
    void BotsSawMiss()
    {
        if (_bots.Length == 0) return;
        foreach (var q in Core.V)
            if (q.Upright && q.Miss == DanceCore.MissTicks) _eye.Add(q.Id, CrowdEye.Miss);
    }

    void FinishWithBots(int[] winners, string line)
    {
        var human = -1;
        for (var i = 0; i < Seats; i++)
            if (_s[i].Active && !_s[i].Bot) human = i;
        CrowdBots.Finish(Ctx, winners, human, human >= 0 ? _s[human].Nick : "", _solo.Level, $"{Info.Title}: {line}", line);
    }
}
