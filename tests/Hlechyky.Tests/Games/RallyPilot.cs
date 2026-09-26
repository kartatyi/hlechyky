using Hlechyky.Games.Impl;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Автопілот для тестів і журналів паритету: поле відстаней до кожних воріт (Дейкстра по клітинках, узбіччя
/// й трава дорожчі), машина дивиться на кілька клітинок уперед уздовж спуску й кермує туди. Не ідеальний
/// гонщик, а чесний водій: вписується в повороти, гальмує перед крутими, інколи тягне ручник і, застрягши,
/// здає назад. Детермінований, якщо детермінований <see cref="Random"/>, — журнали пишуться з фіксованим сідом.
/// Двійник для живої перевірки в браузері — <c>docs/games/dev/rally-bots.js</c>.
/// </summary>
public sealed class RallyPilot
{
    const int Cols = RallyTrack.Cols, Cells = RallyTrack.Cells;
    readonly RallyTrack _t;
    readonly int[][] _dist;
    readonly int[][] _next;
    readonly int[] _stuck = new int[RallyCore.Seats];
    readonly int[] _back = new int[RallyCore.Seats];
    readonly int[] _gate = new int[RallyCore.Seats];
    readonly Random? _rng;
    /// <summary>Ймовірність (у тисячних на тик) смикнути ручник на повороті — для різноманіття журналів.</summary>
    public int Drift { get; init; } = 0;
    /// <summary>Наскільки обережно: 0 — завжди газ, більше — раніше гальмує.</summary>
    public int Care { get; init; } = 1;

    public RallyPilot(RallyTrack track, Random? rng = null)
    {
        _t = track;
        _rng = rng;
        _dist = new int[track.K][];
        _next = new int[track.K][];
        for (var g = 0; g < track.K; g++) (_dist[g], _next[g]) = Field(g);
    }

    bool Open(int x, int y)
    {
        var code = _t.CodeAt(x, y);
        return !RallySurface.IsWall(code) && code != RallySurface.Hay;
    }

    int Penalty(int x, int y)
    {
        var code = _t.CodeAt(x, y);
        var p = code switch
        {
            RallySurface.Grass or RallySurface.Corn => 14,
            RallySurface.Mud => 30,
            RallySurface.Oil => 8,
            _ => 0,
        };
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
                if (!Open(x + dx, y + dy)) { p += 10; dy = 2; break; }
        return p;
    }

    (int[] Dist, int[] Next) Field(int g)
    {
        var dist = new int[Cells];
        var next = new int[Cells];
        Array.Fill(dist, int.MaxValue);
        Array.Fill(next, -1);
        var pq = new PriorityQueue<int, int>();
        for (var c = 0; c < Cells; c++)
            if (_t.GateAt[c] == g && Open(c % Cols, c / Cols))
            {
                dist[c] = 0;
                pq.Enqueue(c, 0);
            }
        while (pq.TryDequeue(out var c, out var d))
        {
            if (d != dist[c]) continue;
            int x = c % Cols, y = c / Cols;
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx, ny = y + dy;
                    if (!Open(nx, ny)) continue;
                    if (dx != 0 && dy != 0 && (!Open(x + dx, y) || !Open(x, y + dy))) continue;
                    // шукаємо шлях від воріт назад: n — звідки їдуть у c
                    var n = ny * Cols + nx;
                    var nd = d + (dx != 0 && dy != 0 ? 14 : 10) + Penalty(nx, ny);
                    if (nd >= dist[n]) continue;
                    dist[n] = nd;
                    next[n] = c;
                    pq.Enqueue(n, nd);
                }
        }
        return (dist, next);
    }

    /// <summary>Маска керування для машини на цей тик.</summary>
    public int Mask(RallyCore core, int seat)
    {
        var c = core.Cars[seat];
        if (!c.Present) return 0;
        var speed = Math.Abs(c.VF);
        if (_back[seat] > 0)
        {
            _back[seat]--;
            return 8 | (_back[seat] % 20 < 10 ? 1 : 2);
        }
        if (speed < 40 && c.Stall == 0 && core.T > RallyCore.CountTicks + 5)
        {
            if (++_stuck[seat] > 18)
            {
                _stuck[seat] = 0;
                _back[seat] = 14;
            }
        }
        else _stuck[seat] = 0;

        // точка прицілу: кілька клітинок уздовж спуску поля (далі — що швидше їдемо)
        // привид воріт не рахує — ведемо його «уявні» ворота самі
        if (!c.Ghost) _gate[seat] = c.Next;
        else if (_dist[_gate[seat]][RallyCore.CellOf(c.X, c.Y)] == 0) _gate[seat] = (_gate[seat] + 1) % _t.K;
        var g = _gate[seat];
        var cell = RallyCore.CellOf(c.X, c.Y);
        var ahead = 2 + speed / 280;
        for (var i = 0; i < ahead; i++)
        {
            if (_dist[g][cell] == 0) g = (g + 1) % _t.K;
            var n = _next[g][cell];
            if (n < 0) break;
            cell = n;
        }
        var tx = (cell % Cols) * RallyTrack.CellSub + RallyTrack.CellSub / 2;
        var ty = (cell / Cols) * RallyTrack.CellSub + RallyTrack.CellSub / 2;
        var want = (int)Math.Round(Math.Atan2(ty - c.Y, tx - c.X) / (2 * Math.PI) * 1024) & 1023;
        var diff = ((want - c.A + 512) & 1023) - 512;
        var mask = 0;
        if (diff > 10) mask |= 2;
        else if (diff < -10) mask |= 1;
        var turn = Math.Abs(diff);
        if (turn < 110 || speed < 300) mask |= 4;
        else if (turn > 170 * Care && speed > 520) mask |= 8;
        if (Drift > 0 && _rng is not null && turn > 90 && speed > 500 && _rng.Next(1000) < Drift) mask |= 16;
        return mask;
    }
}
