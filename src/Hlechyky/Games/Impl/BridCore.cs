namespace Hlechyky.Games.Impl;

/// <summary>
/// Брід під водою: сітка каменів <see cref="Cols"/>×<see cref="Rows"/>, з яких безпечні лише стежка й кілька тупиків.
/// Ряд 0 — біля нашого берега, ряд <c>Rows − 1</c> — біля того. Стежка — випадкове блукання вперед і вбік (без
/// повернень назад), і кожна нова клітинка торкається лише попередньої: обхідних коротких стежок нема, на кожен крок —
/// один правильний камінь. Тупик — відгалуження на 1–2 камені, що нікуди не веде (не в першому й не в останньому ряду).
/// </summary>
public sealed class BridFord
{
    /// <summary>Скільки разів поспіль стежка може йти вбік і як часто звертає.</summary>
    const int SideRun = 2;
    const double SideChance = 0.42;

    public int Cols { get; }
    public int Rows { get; }
    readonly bool[] _safe;
    readonly List<(int X, int Y)> _path = [], _branch = [];
    /// <summary>Стежка від ряду 0 до останнього — у порядку кроків.</summary>
    public IReadOnlyList<(int X, int Y)> Path => _path;
    /// <summary>Камені тупиків (безпечні, але до того берега не ведуть).</summary>
    public IReadOnlyList<(int X, int Y)> Branches => _branch;

    /// <summary>Розмір броду за раундом: щоразу ширша й довша річка. Вечірка — як перший раунд.</summary>
    public static (int Cols, int Rows) SizeOf(int round) => round switch { <= 1 => (5, 9), 2 => (6, 10), _ => (7, 11) };

    public BridFord(int cols, int rows, Random rng)
    {
        Cols = cols;
        Rows = rows;
        _safe = new bool[cols * rows];
        var x = rng.Next(cols);
        var y = 0;
        Mark(x, y, _path);
        int run = 0, dir = 0;
        while (y < rows - 1)
        {
            // У першому ряду вбік не звертаємо: з берега вгадують рівно один камінь.
            if (y > 0 && run < SideRun && rng.NextDouble() < SideChance)
            {
                var d = run > 0 ? dir : rng.Next(2) == 0 ? -1 : 1;
                var ok = Free(x + d, y, x, y);
                if (!ok && run == 0) { d = -d; ok = Free(x + d, y, x, y); }
                if (ok)
                {
                    x += d;
                    dir = d;
                    run++;
                    Mark(x, y, _path);
                    continue;
                }
            }
            y++;
            run = 0;
            Mark(x, y, _path);
        }
        // Тупики: приблизно один на чотири ряди. Не вийшло за 40 спроб — то й без них.
        var want = rows / 4;
        for (var tries = 0; tries < 40 && want > 0; tries++)
        {
            var o = _path[rng.Next(1, _path.Count - 2)];
            var (dx, dy) = rng.Next(3) switch { 0 => (-1, 0), 1 => (1, 0), _ => (0, 1) };
            var len = 1 + rng.Next(2);
            var added = new List<(int, int)>(len);
            int px = o.X, py = o.Y;
            var good = true;
            for (var k = 1; k <= len && good; k++)
            {
                int cx = o.X + dx * k, cy = o.Y + dy * k;
                good = cy >= 1 && cy < rows - 1 && Free(cx, cy, px, py);
                if (!good) break;
                _safe[cx + cy * cols] = true;   // тимчасово: наступний камінь тупика перевіряється вже з ним
                added.Add((cx, cy));
                px = cx;
                py = cy;
            }
            if (!good)
            {
                foreach (var (ax, ay) in added) _safe[ax + ay * cols] = false;
                continue;
            }
            _branch.AddRange(added);
            want--;
        }
    }

    void Mark(int x, int y, List<(int X, int Y)> into)
    {
        _safe[x + y * Cols] = true;
        into.Add((x, y));
    }

    /// <summary>Камінь вільний під стежку: у річці, ще не безпечний, і з безпечних сусідів має лише той, звідки прийшли.</summary>
    bool Free(int x, int y, int px, int py)
    {
        if (!In(x, y) || _safe[x + y * Cols]) return false;
        foreach (var (dx, dy) in BridCore.Dirs)
        {
            int nx = x + dx, ny = y + dy;
            if (In(nx, ny) && _safe[nx + ny * Cols] && (nx != px || ny != py)) return false;
        }
        return true;
    }

    public bool In(int x, int y) => x >= 0 && x < Cols && y >= 0 && y < Rows;
    public bool Safe(int x, int y) => In(x, y) && _safe[x + y * Cols];
    public bool Safe(int idx) => _safe[idx];
}

/// <summary>Гравець на броді. <c>Y = −1</c> — наш берег, <c>Y = Rows</c> — той берег (перейшов).</summary>
public sealed class BridRunner
{
    public bool Plays;
    /// <summary>Де стоїть (у стрибку — куди летить) і звідки стрибнув.</summary>
    public int X, Y, Fx, Fy;
    /// <summary>Тиків стрибка лишилось (0 — стоїть) і тиків «мокрий» (у воді, потім — на берег).</summary>
    public int Jump, Wet;
    /// <summary>Напрямок, натиснутий у стрибку: скочить одразу, як приземлиться (−1 — нема).</summary>
    public int Queue = -1;
    /// <summary>Місце на тому березі в цьому раунді (0 — ще бреде).</summary>
    public int Place;
    /// <summary>Очки партії (3/2/1 за раунд).</summary>
    public int Points;
    /// <summary>Найдальший ряд раунду, на якому стояв (1..Rows; 0 — ще не ступав).</summary>
    public int Best;
    /// <summary>Падінь за раунд; скільки разів ступив першим на нерозвіданий камінь; чи всі його камені розвідав сам.</summary>
    public int Falls, News;
    public bool Pioneer = true;

    /// <summary>Може стрибати: грає, не в повітрі, не мокрий і ще не перейшов.</summary>
    public bool Standing => Plays && Jump == 0 && Wet == 0 && Place == 0;
}

/// <summary>
/// Світ одного раунду: брід, хто де, мокрі сліди, затоплені камені, черга фінішу. Тик 50 мс; стрибок — 6 тиків
/// (0,3 с), «мокрий» — 30 тиків (1,5 с), слід висихає за 100 тиків (5 с), раунд — до 1200 тиків (60 с).
/// Фази й очки — у <see cref="Brid"/>.
/// </summary>
public sealed class BridCore(Random rng)
{
    public const int Seats = 8, TickMs = 50, JumpTicks = 6, WetTicks = 30, TrailTicks = 100, RoundTicks = 1200;
    /// <summary>Події кадру: [вид, місце, x, y].</summary>
    public const int EvSplash = 0, EvFinish = 1, EvBank = 2;
    /// <summary>Напрямки як у <c>HGames.ui.dpad</c>: 0 → вправо, 1 ↓ назад (до нашого берега), 2 ← вліво, 3 ↑ вперед.</summary>
    public static readonly (int Dx, int Dy)[] Dirs = [(1, 0), (0, -1), (-1, 0), (0, 1)];

    readonly Random _rng = rng;
    readonly List<int[]> _ev = [];

    public BridRunner[] Runners { get; } = [.. Enumerable.Range(0, Seats).Select(_ => new BridRunner())];
    public BridFord? Ford { get; private set; }
    public bool[] Sunk { get; private set; } = [];
    /// <summary>Тик, коли висохне слід на камені (≤ T — сухий).</summary>
    public int[] Trail { get; private set; } = [];
    /// <summary>Хто першим стояв на безпечному камені (−1 — ніхто).</summary>
    public int[] FirstBy { get; private set; } = [];
    public int T, Rt;
    public List<int> Order { get; } = [];
    public int Cols => Ford?.Cols ?? BridFord.SizeOf(1).Cols;
    public int Rows => Ford?.Rows ?? BridFord.SizeOf(1).Rows;
    public int Idx(int x, int y) => x + y * Cols;

    /// <summary>Новий брід; гравці — на нашому березі, рівно розставлені вздовж нього.</summary>
    public void NewRound(int cols, int rows, bool[] plays)
    {
        Ford = new BridFord(cols, rows, _rng);
        var n = cols * rows;
        Sunk = new bool[n];
        Trail = new int[n];
        FirstBy = [.. Enumerable.Repeat(-1, n)];
        Rt = 0;
        Order.Clear();
        _ev.Clear();
        var count = plays.Count(p => p);
        var k = 0;
        for (var s = 0; s < Seats; s++)
        {
            var r = Runners[s];
            r.Plays = s < plays.Length && plays[s];
            r.Jump = r.Wet = r.Place = r.Best = r.Falls = r.News = 0;
            r.Queue = -1;
            r.Pioneer = true;
            r.X = r.Fx = r.Plays ? BankCol(k++, count, cols) : 0;
            r.Y = r.Fy = -1;
        }
    }

    public static int BankCol(int k, int n, int cols) => Math.Min(cols - 1, (2 * k + 1) * cols / (2 * Math.Max(1, n)));

    /// <summary>
    /// Стрибок у напрямку <paramref name="d"/> (людина чи бот — однаково). Помилка — текст відмови, стан не змінено.
    /// На затоплений камінь не пускаємо: він і так видно, що вода, а промах пальцем не мусить коштувати 1,5 с.
    /// </summary>
    public string? TryStep(int seat, int d)
    {
        if (d is < 0 or > 3) return "Такого напрямку нема";
        var r = Runners[seat];
        var (dx, dy) = Dirs[d];
        int nx = r.X + dx, ny = r.Y + dy;
        if (ny < -1) return "Позаду — тільки берег";
        if (nx < 0 || nx >= Cols) return r.Y < 0 ? "Далі берега нема" : "Там уже край броду";
        if (ny >= 0 && ny < Rows && Sunk[Idx(nx, ny)]) return "Там уже хтось шубовснув — вода";
        if (r.Y >= 0 && r.Y < Rows) Trail[Idx(r.X, r.Y)] = T + TrailTicks;   // слід сохне з тієї миті, як зійшов
        r.Fx = r.X;
        r.Fy = r.Y;
        r.X = nx;
        r.Y = ny;
        r.Jump = JumpTicks;
        return null;
    }

    /// <summary>Крок світу в грі: приземлення, вода, вихід на той берег. Події — лише цього тика.</summary>
    public void Step()
    {
        T++;
        Rt++;
        _ev.Clear();
        for (var s = 0; s < Seats; s++)
        {
            var r = Runners[s];
            if (!r.Plays) continue;
            if (r.Wet > 0)
            {
                if (--r.Wet > 0) continue;
                r.Y = r.Fy = -1;
                r.Fx = r.X;
                _ev.Add([EvBank, s, r.X, -1]);
            }
            else if (r.Jump > 0 && --r.Jump == 0) Land(s, r);
        }
    }

    /// <summary>Відлік і пауза між раундами: світ стоїть, лише час іде.</summary>
    public void Idle()
    {
        T++;
        _ev.Clear();
    }

    void Land(int s, BridRunner r)
    {
        if (r.Y == Rows)
        {
            r.Place = Order.Count + 1;
            Order.Add(s);
            _ev.Add([EvFinish, s, r.X, r.Y]);
            return;
        }
        if (r.Y >= 0)
        {
            var c = Idx(r.X, r.Y);
            if (!Ford!.Safe(c))
            {
                Sunk[c] = true;
                r.Falls++;
                r.News++;
                r.Wet = WetTicks;
                r.Queue = -1;
                _ev.Add([EvSplash, s, r.X, r.Y]);
                return;
            }
            if (FirstBy[c] < 0)
            {
                FirstBy[c] = s;
                r.News++;
            }
            else if (FirstBy[c] != s) r.Pioneer = false;
            Trail[c] = T + TrailTicks;
            r.Best = Math.Max(r.Best, r.Y + 1);
        }
        if (r.Queue >= 0)
        {
            var q = r.Queue;
            r.Queue = -1;
            TryStep(s, q);
        }
    }

    /// <summary>Хтось у повітрі чи у воді — кадр треба слати кожен тик.</summary>
    public bool Busy
    {
        get
        {
            foreach (var r in Runners) if (r.Plays && (r.Jump > 0 || r.Wet > 0)) return true;
            return false;
        }
    }

    public int[][] Events() => [.. _ev.Select(e => (int[])e.Clone())];
    public bool AnyEvents => _ev.Count > 0;
    public IEnumerable<int[]> RawEvents => _ev;
}
