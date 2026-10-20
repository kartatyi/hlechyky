namespace Hlechyky.Games.Impl;

/// <summary>Траса «Намиста»: жолоб, місце (тло), скільки кольорів і намистин, швидкість хвоста (su/крок) і норма часу.</summary>
public sealed record NamystoLevel(int N, string Name, int Place, int Colors, int Beads, int Speed, int Par, NamystoTrack Track)
{
    /// <summary>Жолоб для браузера: x, y по черзі (su) — рівно ті точки, з яких рахує сервер.</summary>
    public int[] Wire()
    {
        var w = new int[Track.Count * 2];
        for (var i = 0; i < Track.Count; i++) { w[2 * i] = Track.X[i]; w[2 * i + 1] = Track.Y[i]; }
        return w;
    }
}

/// <summary>
/// Дванадцять трас «Намиста» (specs/namysto.md §3): чотири місця по три. Жолоби будуються тут раз, з дробовою
/// тригонометрією, — і вже готовими цілими точками їдуть у браузер, тож симуляції тригонометрія не потрібна.
/// </summary>
public static class NamystoTracks
{
    public const int Count = 12;
    /// <summary>Крок точок жолоба: 6 px.</summary>
    public const double StepPx = 6;
    public static readonly string[] Places = ["Хата", "Левада", "Ярмарок", "Купальська ніч"];

    const double C = 360, W = 720;

    static readonly Lazy<NamystoLevel[]> _levels = new(Build);
    public static IReadOnlyList<NamystoLevel> Levels => _levels.Value;

    public static NamystoLevel Level(int n) => Levels[Math.Clamp(n, 1, Count) - 1];

    /// <summary>«Намисто дня»: траса за зерном дня, кольорів і намистин — як посередині кампанії.</summary>
    public static NamystoLevel Daily(string day)
    {
        var l = Levels[Days.Seed("namysto-track", day) % Count];
        return l with { Colors = 5, Beads = 120, Speed = 10, Par = 140 };
    }

    static NamystoLevel[] Build() =>
    [
        L(1, "Равлик", 0, 4, 80, 8, 90, Spiral(C, C, 318, 118, 2.6, -Math.PI / 2, 1), C, C),
        L(2, "Підкова", 0, 4, 90, 8, 100, Spiral(C, C, 330, 120, 2.2, Math.PI, -1, kx: 1, ky: 0.8), C, C),
        L(3, "Макітра", 0, 4, 100, 9, 110, Spiral(C, C, 320, 130, 2.3, Math.PI * 0.75, 1, squircle: 0.5), C, C),
        L(4, "Змійка", 1, 5, 100, 9, 120, Rows(), C, 645),
        L(5, "Хвиля", 1, 5, 110, 10, 130, Spiral(C, C, 315, 125, 2.0, 0, 1, wave: (6, 16)), C, C),
        L(6, "Квітка", 1, 5, 120, 10, 140, Spiral(C, C, 300, 120, 2.0, -Math.PI / 2, -1, petals: (5, 0.12)), C, C),
        L(7, "Карусель", 2, 5, 130, 11, 150, Spiral(C, C, 330, 120, 2.2, Math.PI / 2, 1, kx: 0.82, ky: 1), C, C),
        L(8, "Писанка", 2, 6, 130, 11, 160, Spiral(C, 340, 300, 120, 2.2, 0, -1, egg: 0.13), C, 340),
        L(9, "Бублик", 2, 6, 140, 12, 170, Spiral(C, 380, 318, 112, 3.0, Math.PI * 0.75, -1), C, 380),
        L(10, "Вінок", 3, 6, 150, 12, 180, Spiral(C, C, 320, 120, 2.6, -Math.PI / 4, 1, wave: (7, 9)), C, C),
        L(11, "Папороть", 3, 6, 160, 13, 190, Columns(), 625, C),
        L(12, "Зоряний шлях", 3, 6, 180, 14, 210, Spiral(C, C, 325, 115, 2.8, Math.PI / 2, -1, squircle: 2.0 / 3, wave: (5, 10)), C, C),
    ];

    static NamystoLevel L(int n, string name, int place, int colors, int beads, int speed, int par, List<(double X, double Y)> path, double sx, double sy)
    {
        var pts = Resample(WithEntry(path), StepPx);
        var xy = new List<int>(pts.Count * 2);
        foreach (var (x, y) in pts)
        {
            int ix = (int)Math.Round(x * NamystoSim.Px), iy = (int)Math.Round(y * NamystoSim.Px);
            // однакові сусідні точки дали б відрізок нульової довжини
            if (xy.Count >= 2 && xy[^2] == ix && xy[^1] == iy) continue;
            xy.Add(ix);
            xy.Add(iy);
        }
        return new NamystoLevel(n, name, place, colors, beads, speed, par,
            new NamystoTrack([.. xy], (int)Math.Round(sx * NamystoSim.Px), (int)Math.Round(sy * NamystoSim.Px)));
    }

    // ---------- форми ----------

    /// <summary>
    /// Спіраль ззовні всередину: радіус рівно спадає від r0 до r1 за turns обертів, кут — від a0 у бік dir (1 — за
    /// годинниковою, бо y донизу). Видозміни: еліпс (kx, ky), «макітра» (суперeліпс: показник до |cos|), хвиля по радіусу,
    /// пелюстки (множник), яйце (ширше знизу).
    /// </summary>
    static List<(double X, double Y)> Spiral(double cx, double cy, double r0, double r1, double turns, double a0, int dir,
        double kx = 1, double ky = 1, double squircle = 1, (int K, double A)? wave = null, (int K, double A)? petals = null, double egg = 0)
    {
        var pts = new List<(double, double)>();
        var total = turns * 2 * Math.PI;
        const double dt = 0.0015;
        for (var t = 0.0; t <= total + 1e-9; t += dt)
        {
            var a = a0 + dir * t;
            var r = r0 + (r1 - r0) * t / total;
            if (wave is { } w) r += w.A * Math.Sin(w.K * a);
            if (petals is { } p) r *= 1 + p.A * Math.Cos(p.K * a);
            if (egg != 0) r *= 1 + egg * Math.Sin(a);
            var c = Math.Cos(a);
            var s = Math.Sin(a);
            if (squircle != 1)
            {
                c = Math.Sign(c) * Math.Pow(Math.Abs(c), squircle);
                s = Math.Sign(s) * Math.Pow(Math.Abs(s), squircle);
            }
            pts.Add((cx + r * kx * c, cy + r * ky * s));
        }
        return pts;
    }

    /// <summary>«Змійка»: п'ять рядів зліва направо й назад, повороти півколами; Глек — унизу.</summary>
    static List<(double X, double Y)> Rows()
    {
        const double x0 = 80, x1 = 640, top = 80, gap = 112;
        var pts = new List<(double, double)>();
        for (var row = 0; row < 5; row++)
        {
            var y = top + row * gap;
            var ltr = row % 2 == 0;
            var last = row == 4;
            var from = ltr ? x0 : x1;
            var to = last ? (ltr ? x1 - 40 : x0 + 40) : (ltr ? x1 : x0);
            Line(pts, from, y, to, y);
            if (last) break;
            // півколо вниз на наступний ряд
            var cx = ltr ? x1 : x0;
            var cy = y + gap / 2;
            Arc(pts, cx, cy, gap / 2, -Math.PI / 2, ltr ? 1 : -1);
        }
        return pts;
    }

    /// <summary>«Папороть»: чотири стовпці згори вниз і назад; Глек — праворуч.</summary>
    static List<(double X, double Y)> Columns()
    {
        const double y0 = 90, y1 = 620, left = 80, gap = 130;
        var pts = new List<(double, double)>();
        for (var col = 0; col < 4; col++)
        {
            var x = left + col * gap;
            var down = col % 2 == 0;
            var last = col == 3;
            var from = down ? y0 : y1;
            var to = last ? (down ? y1 - 40 : y0 + 40) : (down ? y1 : y0);
            Line(pts, x, from, x, to);
            if (last) break;
            var cy = down ? y1 : y0;
            var cx = x + gap / 2;
            // з низу стовпця — півколо праворуч і вгору (кут від π до 0 через низ), з верху — через верх
            Arc(pts, cx, cy, gap / 2, Math.PI, down ? -1 : 1);
        }
        return pts;
    }

    static void Line(List<(double, double)> pts, double ax, double ay, double bx, double by)
    {
        var len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
        var n = Math.Max(1, (int)Math.Ceiling(len / 1.0));
        for (var i = 0; i <= n; i++) pts.Add((ax + (bx - ax) * i / n, ay + (by - ay) * i / n));
    }

    /// <summary>Півколо з кута a0 у бік dir (π радіан).</summary>
    static void Arc(List<(double, double)> pts, double cx, double cy, double r, double a0, int dir)
    {
        var n = Math.Max(8, (int)Math.Ceiling(Math.PI * r / 1.0));
        for (var i = 1; i <= n; i++)
        {
            var a = a0 + dir * Math.PI * i / n;
            pts.Add((cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
        }
    }

    /// <summary>Вхід: продовжити жолоб назад по дотичній, поки не вийде за поле на 60 px, — намистини викочуються з-за краю.</summary>
    static List<(double X, double Y)> WithEntry(List<(double X, double Y)> path)
    {
        var (x0, y0) = path[0];
        // дотична — по точці на ~6 px далі, щоб не ловити шум перших вибірок
        var k = 1;
        double dx = 0, dy = 0, len = 0;
        while (k < path.Count && len < 6) { dx = path[k].X - x0; dy = path[k].Y - y0; len = Math.Sqrt(dx * dx + dy * dy); k++; }
        dx /= len; dy /= len;
        var back = new List<(double, double)>();
        for (var t = 1.0; ; t += 1)
        {
            var x = x0 - dx * t;
            var y = y0 - dy * t;
            back.Add((x, y));
            if (x < -60 || y < -60 || x > W + 60 || y > W + 60) break;
        }
        back.Reverse();
        back.AddRange(path);
        return back;
    }

    /// <summary>Рівні кроки по довжині дуги.</summary>
    static List<(double X, double Y)> Resample(List<(double X, double Y)> path, double step)
    {
        var outp = new List<(double, double)> { path[0] };
        var carry = 0.0;
        for (var i = 1; i < path.Count; i++)
        {
            var (ax, ay) = path[i - 1];
            var (bx, by) = path[i];
            var seg = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            if (seg <= 0) continue;
            var t = step - carry;
            while (t <= seg)
            {
                outp.Add((ax + (bx - ax) * t / seg, ay + (by - ay) * t / seg));
                t += step;
            }
            carry = seg - (t - step);
        }
        var last = path[^1];
        if (outp[^1] != last) outp.Add(last);
        return outp;
    }
}
