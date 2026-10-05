namespace Hlechyky.Games.Impl;

/// <summary>
/// Бот Стрибозаврів для вечірки: бачить найближчу перешкоду своєї доріжки й тисне те саме, що людина (<c>in {s, k}</c>
/// через <see cref="RunnerSim.Input"/>): стрибок із утриманням — над брилою чи ямою, пригнутись — під бурульку й
/// птеродактиля. Рівень — у точності: легкий частіше помиляється з моментом і часом не стрибає зовсім, сильний — рідко.
/// Рандом — лише <see cref="Random"/> столу; думає раз на крок, дешево (прохід по ≤ 64 перешкодах).
/// </summary>
public sealed class DinoBot(int seat, LiveBots.Level level)
{
    public int Seat => seat;
    /// <summary>Розкид моменту стрибка (кроків) і шанс прогавити перешкоду (%) — за рівнем.</summary>
    static readonly int[] Jitter = [5, 2, 1], Miss = [14, 5, 1];
    readonly int _lvl = LiveBots.Index(level);
    int _sent = -1, _holdTill = -1, _target = -1, _shift, _skip = -1;

    public void Think(RunnerSim sim, Random rng)
    {
        var p = sim.P[seat];
        if (!p.Plays || p.Out || p.Down || sim.S < sim.ReadySteps) return;
        var run = sim.Run;
        var sp = sim.Speed(run);
        var x0 = sim.PaceX(run + 1) - p.Lag;
        var x1 = x0 + RunnerDino.HitW;

        // найближча загроза попереду: тверда перешкода, яма чи активна сніжна брила
        int best = int.MaxValue, id = -1, top = 0, bas = 0, close = sp;
        var pit = false;
        for (var i = 0; i < sim.ObstacleCount; i++)
        {
            ref readonly var o = ref sim.Obstacle(i);
            if (o.Kind == RunnerKind.Hill || p.HasPassed(o.Id)) continue;
            var ox = o.XAt(run);
            if (ox + o.W <= x0 || ox >= best) continue;
            best = ox;
            id = o.Id;
            pit = o.Kind == RunnerKind.Pit;
            bas = o.Base - p.Y;
            top = pit ? 0 : o.Base + o.H - p.Y;
            close = sp + (o.Kind == RunnerKind.Ptero && run > o.Since ? RunnerDino.PteroV : 0);
        }
        for (var i = 0; i < sim.SnowCount; i++)
        {
            ref readonly var o = ref sim.SnowBlock(i);
            if (o.Since > run || p.HasPassed(o.Id) || o.X + o.W <= x0 || o.X >= best) continue;
            best = o.X;
            id = o.Id;
            pit = false;
            bas = o.Base - p.Y;
            top = o.Base + o.H - p.Y;
            close = sp;
        }

        var k = sim.S < _holdTill ? 1 : 0;
        if (id >= 0)
        {
            if (id != _target)
            {
                // нова перешкода: свій розкид моменту й чи не прогавить її зовсім
                _target = id;
                var j = Jitter[_lvl];
                _shift = rng.Next(-j, j + 1);
                _skip = rng.Next(100) < Miss[_lvl] ? id : -1;
            }
            var dist = best - x1;
            var overhead = !pit && bas >= RunnerDino.DuckH && bas < RunnerDino.HitH;
            if (_skip == id) { }
            else if (overhead)
            {
                if (!p.Air && dist < close * (6 + _shift) + RunnerDino.DuckW - RunnerDino.HitW) k = 2;
            }
            else if (bas < RunnerDino.HitH && !p.Air && sim.S >= _holdTill)
            {
                var lead = StepsToClear(top) + 1 + _shift;
                if (dist <= close * Math.Max(1, lead))
                {
                    _holdTill = sim.S + RunnerDino.HoldMax;
                    k = 5;
                }
            }
        }
        if (k == _sent && k != 5) return;
        _sent = k == 5 ? 1 : k;
        sim.Input(seat, sim.S, k);
    }

    /// <summary>За скільки кроків стрибок з утриманням підніме ноги вище за <paramref name="top"/> (суб над землею).</summary>
    static int StepsToClear(int top)
    {
        int h = 0, v = RunnerDino.JumpV;
        for (var n = 1; n < 40; n++)
        {
            v -= n <= RunnerDino.HoldMax ? RunnerDino.GHold : RunnerDino.G;
            h += v;
            if (h > top + 32) return n;
        }
        return 40;
    }
}
