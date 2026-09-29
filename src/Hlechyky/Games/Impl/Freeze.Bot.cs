namespace Hlechyky.Games.Impl;

/// <summary>
/// «Замри!» з 🤖 ботами (сам за столом → «🤖 + бот»). Бот-суперник — гравець: свій селянин біля тину, мета та сама —
/// глек. Ходить відрізками юрби (ті самі <see cref="FreezeCore.RestChance"/>/<see cref="FreezeCore.RunMin"/>, темп за
/// рівнем), на «Замри!» стає з людською затримкою (легкий частіше незграбний), після пісні рушає не одразу — тож у
/// кадрі він як усі. Відмінність одна: юрба під хатою розвертається, а бот, дійшовши до своєї межі, рве до глека.
/// Штурхає через ту саму <c>PushAt</c>, що й людина, тих, кого викрив кадр (<see cref="CrowdEye"/>): хто впав від
/// штурхана, торкнувся глека в естафеті чи зайшов ближче за межу юрби (1064) — точно гравець; хто штурхав чи попався
/// Бабі — підозрілий.
///
/// Ботів два в «Кожен за себе» (з одним на широкому лузі нема кого підозрювати, а «сходинки» за часом дають +2/+1
/// лише від трьох гравців) і один в естафеті — чесні команди один на один.
/// </summary>
public sealed partial class Freeze
{
    /// <summary>
    /// Темп ходи бота за рівнем (0 ледачий … 3 нетерплячий, як у юрби): бот іде по глек, тож не ледачий — інакше за
    /// 90 с не дійшов би ніколи (юрба під пісню Баби проходить луг за ≈ 50–70 с).
    /// </summary>
    static readonly int[] BotTempo = [2, 3, 3];
    /// <summary>Крок убік (%) — сильний марнує менше.</summary>
    static readonly int[] SideBot = [12, 12, 6];
    /// <summary>Незграба на «Замри!» (‰): стає вже тоді, коли Баба дивиться, — і його ловлять. Юрба — 45.</summary>
    static readonly int[] ClumsyBot = [120, 45, 10];
    /// <summary>Реакція на «Замри!» у тиках (бот бачить поворот тиком пізніше, ніж юрба, — як людина з кадру).</summary>
    static readonly int[] ReactLo = [4, 2, 2], ReactHi = [11, 11, 7];
    /// <summary>Озирнулась — з цим шансом (%) бот про всяк випадок стає.</summary>
    static readonly int[] CautionBot = [20, 30, 50];
    /// <summary>Знову співає — рушає за 0…стільки тиків.</summary>
    static readonly int[] StartMaxBot = [30, 30, 12];
    /// <summary>Звідки бот рве до глека без перепочинків.</summary>
    static readonly int[] RushX = [1000, 920, 860];
    /// <summary>З якої відстані штурхає (з id — до 44).</summary>
    static readonly int[] FireDist = [0, 30, 40];
    /// <summary>Підозра з кадру: упав від штурхана / торкнувся глека / за межею юрби — точно гравець.</summary>
    const int SeenPlayer = 200, Pusher = 45, CaughtPts = 30;
    const int ChaseTicks = 250, AfterPush = 150;

    sealed class Mind
    {
        public int Hunt = -1, Delay, NextEval, Chase, Baba = -1;
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

    /// <summary>Естафета — один бот (команди один на один), кожен за себе — двоє.</summary>
    int[] BotSeats() => CrowdBots.FreeSeats(Ctx, Seats, _relay ? 1 : 2);

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
        foreach (var seat in _bots)
            if (_s[seat].Me >= 0) FreezeCore.Forget(Core.V[_s[seat].Me]);
    }

    /// <summary>Мозок — до тика ядра, за Бабою з минулого кадру: так само, як людина тисне між тиками.</summary>
    void BotsThink()
    {
        if (_bots.Length == 0) return;
        var lvl = LiveBots.Index(_solo.Level);
        var rng = Ctx.Rng;
        var baba = Core.Baba;
        for (var b = 0; b < _bots.Length; b++)
        {
            var seat = _bots[b];
            var s = _s[seat];
            if (!s.Active || s.Me < 0) continue;
            var v = Core.V[s.Me];
            var m = _minds[b];
            s.MoveAt = _clock;
            var was = m.Baba;
            m.Baba = baba;
            if (v.Caught > 0 || v.Down > 0 || v.JustBack)
            {
                // під пальцем Баби, лежить чи щойно на старті — з чистого аркуша
                FreezeCore.Forget(v);
                v.Hold = baba is FreezeCore.Turn or FreezeCore.Watch;
                m.Hunt = -1;
                continue;
            }
            if (baba != was)
            {
                if (baba == FreezeCore.Turn)
                {
                    v.Hold = true;
                    if (v.Want >= 0 && v.StopIn == 0)
                        v.StopIn = rng.Next(1000) < ClumsyBot[lvl]
                            ? rng.Next(FreezeCore.ReactMax, FreezeCore.ReactMax + 7)
                            : rng.Next(ReactLo[lvl], ReactHi[lvl] + 1);
                }
                else if (baba == FreezeCore.Glance)
                {
                    if (v.Want >= 0 && rng.Next(100) < CautionBot[lvl])
                    {
                        v.Hold = true;
                        v.StopIn = rng.Next(ReactLo[lvl], ReactHi[lvl] + 1);
                    }
                }
                else if (baba == FreezeCore.Sing)
                {
                    FreezeCore.Forget(v);
                    v.StartIn = rng.Next(0, StartMaxBot[lvl] + 1);
                }
            }
            if (v.StopIn > 0)
            {
                if (--v.StopIn == 0)
                {
                    v.Want = -1;
                    v.Run = 0;
                }
                continue;
            }
            if (v.Hold || baba is FreezeCore.Turn or FreezeCore.Watch)
            {
                v.Want = -1;
                continue;
            }
            if (v.StartIn > 0)
            {
                v.StartIn--;
                v.Want = -1;
                continue;
            }
            if (v.Dazed > 0)
            {
                v.Want = -1;
                continue;
            }
            if (lvl > 0 && Hunt(seat, s, v, m, lvl)) continue;
            Walk(v, lvl);
        }
    }

    /// <summary>Хода відрізками юрби за темпом рівня; від <see cref="RushX"/> — просто до глека.</summary>
    void Walk(FreezeVillager v, int lvl)
    {
        var rng = Ctx.Rng;
        if (v.Run > 0)
        {
            v.Run--;
            if (!v.Blocked) return;
            v.Run = 0;
        }
        if (v.Rest > 0)
        {
            v.Rest--;
            v.Want = -1;
            return;
        }
        if (v.X >= RushX[lvl])
        {
            v.Want = 0;
            v.Run = 200;
            return;
        }
        var t = BotTempo[lvl];
        var r = rng.Next(100);
        if (r < FreezeCore.RestChance[t])
        {
            v.Want = -1;
            v.Rest = rng.Next(4, FreezeCore.RestMax[t] + 1);
            return;
        }
        r -= FreezeCore.RestChance[t];
        if (r < SideBot[lvl])
        {
            v.Want = rng.Next(2) == 0 ? 1 : 3;
            v.Run = rng.Next(4, 27);
            return;
        }
        if (r < SideBot[lvl] + 4 - 2 * lvl)
        {
            v.Want = 2;
            v.Run = rng.Next(4, 19);
            return;
        }
        v.Want = 0;
        v.Run = rng.Next(FreezeCore.RunMin[t], FreezeCore.RunMax[t] + 1);
    }

    /// <summary>Полювання: найпідозріліший поруч (не позаду далі ніж на 60) — підійти й штурхнути, поки Баба співає.</summary>
    bool Hunt(int seat, FreezeSeat s, FreezeVillager v, Mind m, int lvl)
    {
        if (m.Hunt < 0 && _t >= m.NextEval)
        {
            m.NextEval = _t + CrowdEye.HuntEvery[lvl];
            var id = _eye.Best(s.Me, CrowdEye.HuntMin[lvl], i =>
            {
                var q = Core.V[i];
                // під хатою бот не женеться — лише штурхне того, хто вже поруч (і рве до глека далі)
                var reach = v.X >= RushX[lvl] ? 50 : 160;
                return !q.Still && q.Guard == 0 && q.X >= v.X - 60 && Math.Abs(q.X - v.X) < reach && Math.Abs(q.Y - v.Y) < reach;
            });
            if (id >= 0)
            {
                m.Hunt = id;
                m.Delay = CrowdEye.Reaction[lvl];
                m.Chase = ChaseTicks;
            }
        }
        if (m.Hunt < 0) return false;
        var q = Core.V[m.Hunt];
        if (q.Still || q.Guard > 0 || --m.Chase <= 0)
        {
            m.Hunt = -1;
            return false;
        }
        if (m.Delay > 0) m.Delay--;
        if (m.Delay == 0 && v.PushCool == 0 && Core.PushAllowed && FreezeCore.Dist2(q, v) <= (long)FireDist[lvl] * FireDist[lvl])
        {
            PushAt(seat, m.Hunt);
            m.Hunt = -1;
            m.NextEval = _t + AfterPush;
            v.Want = -1;
            return true;
        }
        int dx = q.X - v.X, dy = q.Y - v.Y;
        v.Want = Math.Abs(dx) > Math.Abs(dy) ? (dx > 0 ? 0 : 2) : (dy > 0 ? 1 : 3);
        v.Run = 0;
        return true;
    }

    /// <summary>Після тика: що бачив кожен у кадрі — штурхани, хто впав, кого впіймали, хто зайшов за межу юрби.</summary>
    void BotsSee()
    {
        if (_bots.Length == 0) return;
        _eye.Tick(_t);
        foreach (var e in Core.Ev)
        {
            switch (e[0])
            {
                case 1:
                    _eye.Add(e[1], Pusher);
                    if (e[3] == 1) _eye.Add(e[2], SeenPlayer);
                    else _eye.Clear(e[2]);         // лише хитнувся — отже, просто селянин
                    break;
                case 2: _eye.Add(e[1], CaughtPts); break;
                case 4: _eye.Add(e[1], SeenPlayer); break;
            }
        }
        foreach (var q in Core.V)
            if (q.X > FreezeCore.BotMaxX && _eye.Sus(q.Id) < SeenPlayer) _eye.Add(q.Id, SeenPlayer);
    }

    void FinishWithBots(int[] winners, string log, string line)
    {
        var human = -1;
        for (var i = 0; i < Seats; i++)
            if (_s[i].Active && !_s[i].Bot) human = i;
        CrowdBots.Finish(Ctx, winners, human, human >= 0 ? _s[human].Nick : "", _solo.Level, log, line);
    }
}
