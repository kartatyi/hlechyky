namespace Hlechyky.Games.Impl;

/// <summary>
/// «Купальська ніч» з 🤖 ботами (сам за столом → «🤖 + бот»). Бот-суперник — гравець: свій список із трьох кладок,
/// дві головешки, та сама рука. Ходить мозком юрби (<see cref="KupalaCore.Think"/>), лише цілі обирає свої — частіше
/// кладку зі списку; NPC-вінок і NPC-ляпас для нього вимкнено: пускає вінок і ляскає через ті самі
/// <c>LaunchAt</c>/<c>SlapAt</c>, що й людина. Бачить бот, як людина, лише освітлених (<see cref="KupalaCore.Lit"/>):
/// полює тільки на тих, кого видно, а підозру (<see cref="CrowdEye"/>) набирає з публічного — хто ляснув (видно
/// завжди), хто стояв на кладці, коли вінок пішов на воду, хто зірвав папороть (точно гравець).
///
/// Ботів два — як у Юрмі: у темряві одного не знайти зовсім, а двоє ще й ляскають одне одного, даючи людині сліди.
/// </summary>
public sealed partial class Kupala
{
    public const int BotCount = 2;
    static readonly int[] ListPick = [35, 55, 65];
    static readonly int[] LaunchChance = [50, 85, 100];
    /// <summary>З якої відстані ляскає (з id — до 56).</summary>
    static readonly int[] FireDist = [0, 40, 52];
    /// <summary>Папороть зірвав — точно гравець (NPC її не рвуть).</summary>
    const int SeenPlayer = 200;
    const int ChaseTicks = 400, LostTicks = 50;

    sealed class Mind
    {
        public int Hunt = -1, Delay, NextEval, Chase, Dark;
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
            if (v.Target < 0 && v.Stand == 0 && v.Wander == 0 && !v.Blocked && v.Upright && v.Busy == 0 && v.Stun == 0)
            {
                var need = NeededSpot(s, v);
                if (need >= 0 && rng.Next(100) < ListPick[lvl]) Core.Aim(v, KupalaMap.Spots[need].Cell, need);
            }
            var had = v.Target >= 0;
            Core.Think(v);
            if (had && v.Target < 0 && v.Stand > 0 && _phase == PhaseGo) TryLaunch(seat, s, v, lvl);
        }
    }

    static int NeededSpot(KupalaSeat s, KupalaVillager v)
    {
        int best = -1;
        long bd = long.MaxValue;
        for (var i = 0; i < ListLen; i++)
        {
            if (s.Done[i]) continue;
            var sp = KupalaMap.Spots[s.List[i]];
            var d = KupalaCore.Dist2(v, sp.X, sp.Y);
            if (d < bd) { bd = d; best = s.List[i]; }
        }
        return best;
    }

    /// <summary>Став на кладку зі списку — пускає вінок (легкий інколи передумує), сильний зрідка блефує на чужій.</summary>
    void TryLaunch(int seat, KupalaSeat s, KupalaVillager v, int lvl)
    {
        var k = KupalaCore.SpotAt(v);
        if (k < 0 || s.LaunchCool > 0) return;
        var mine = false;
        for (var i = 0; i < ListLen; i++) if (s.List[i] == k && !s.Done[i]) mine = true;
        var rng = Ctx.Rng;
        if (mine ? rng.Next(100) < LaunchChance[lvl] : lvl == 2 && rng.Next(100) < 8) LaunchAt(seat, k);
    }

    /// <summary>Полює лише на освітлених: пішов у темряву — бот іде туди, де бачив востаннє, і за 2 с махає рукою.</summary>
    bool Hunt(int seat, KupalaSeat s, KupalaVillager v, Mind m, int lvl)
    {
        var lit = Core.Lit;
        if (m.Hunt < 0 && _t >= m.NextEval)
        {
            m.NextEval = _t + CrowdEye.HuntEvery[lvl];
            var id = _eye.Best(s.Me, CrowdEye.HuntMin[lvl], i => Core.V[i].Upright && i < lit.Length && lit[i]);
            if (id >= 0)
            {
                m.Hunt = id;
                m.Delay = CrowdEye.Reaction[lvl];
                m.Chase = ChaseTicks;
                m.Dark = 0;
            }
        }
        if (m.Hunt < 0) return false;
        var q = Core.V[m.Hunt];
        var seen = m.Hunt < lit.Length && lit[m.Hunt];
        m.Dark = seen ? 0 : m.Dark + 1;
        if (!q.Upright || --m.Chase <= 0 || m.Dark > LostTicks)
        {
            m.Hunt = -1;
            return false;
        }
        if (m.Delay > 0) m.Delay--;
        var d2 = KupalaCore.Dist2(q, v.X, v.Y);
        if (seen && m.Delay == 0 && s.SlapCool == 0 && v.Busy == 0 && v.Stun == 0 && d2 <= (long)FireDist[lvl] * FireDist[lvl])
        {
            SlapAt(seat, m.Hunt);
            m.Hunt = -1;
            v.Want = -1;
            return true;
        }
        if (seen && v.Busy == 0 && v.Stun == 0 && (v.Target < 0 || _t % 10 == 0)) KupalaCore.AimAt(v, q.X, q.Y);
        Core.Think(v);
        return true;
    }

    /// <summary>Після тика: що публічне — ляпаси, вінки, папороть; сильний ще й придивляється до ходи освітлених.</summary>
    void BotsSee()
    {
        if (_bots.Length == 0) return;
        _eye.Tick(_t);
        foreach (var e in _raw) See(e);
        foreach (var e in Core.Events) See(e);
        if (_solo.Level != LiveBots.Level.Hard) return;
        var lit = Core.Lit;
        foreach (var q in Core.V)
            if (q.Id < lit.Length && lit[q.Id]) _eye.Watch(q.Id, q.Moving, q.Dir, q.Blocked && q.Want >= 0 && q.Upright);
    }

    void See(int[] e)
    {
        switch (e[0])
        {
            case 1:
                _eye.Add(e[1], CrowdEye.Shooter);
                if (e[3] == 0) _eye.Clear(e[2]);       // упав і встане — просто селянин
                break;
            case 2:
                foreach (var q in Core.V)
                    if (q.Upright && KupalaCore.SpotAt(q) == e[1]) _eye.Add(q.Id, CrowdEye.Flash);
                break;
            case 4: _eye.Add(e[1], SeenPlayer); break;
        }
    }

    void FinishWithBots(int[] winners, string line)
    {
        var human = -1;
        for (var i = 0; i < Seats; i++)
            if (_s[i].Active && !_s[i].Bot) human = i;
        CrowdBots.Finish(Ctx, winners, human, human >= 0 ? _s[human].Nick : "", _solo.Level, $"{Info.Title}: {line}", line);
    }
}
