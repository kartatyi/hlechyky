namespace Hlechyky.Games.Impl;

/// <summary>
/// Кругляш на площині — спільна цеглинка пакета «arena» (Крижина й Аерохокей): позиція, швидкість, радіус і
/// обернена маса. <see cref="InvM"/> = 0 — тіло кінематичне (біта аерохокею): штовхає, але саме не зрушує.
/// Структура, а не клас: ядра тримають тіла в масивах і міняють їх через <c>ref</c>, без жодної алокації в тику.
/// </summary>
public struct ArenaBody
{
    public double X, Y, Vx, Vy, R, InvM;

    public ArenaBody(double x, double y, double r, double invM)
    {
        X = x;
        Y = y;
        R = r;
        InvM = invM;
    }

    public readonly double Speed => Math.Sqrt(Vx * Vx + Vy * Vy);
}

/// <summary>
/// Фізика кругляшів для обох ігор арени. Усе — чиста математика на <c>double</c> без алокацій і без
/// тригонометрії в гарячому циклі: напрямки беруться з таблиці <see cref="Dir16"/>, записаної літералами.
/// Та сама таблиця й ті самі формули (у тому самому порядку операцій) живуть у браузері
/// (<c>web/games/icefloe.js</c>, <c>hockey.js</c>) — так передбачення свого тіла чи біти дає рівно те
/// саме число, що й сервер (тест <c>Prediction_matches_the_browser_fixture</c>).
/// </summary>
public static class ArenaPhysics
{
    /// <summary>cos 22.5°, sin 22.5° і cos 45° — літерали, щоб C# і JS мали однакові біти.</summary>
    public const double C1 = 0.9238795325112867, S1 = 0.3826834323650898, D = 0.7071067811865476;

    /// <summary>Сектор 0..15 по 22.5°: 0 — праворуч, далі за годинниковою (вісь y дивиться вниз).</summary>
    static readonly double[] Cos16 = [1, C1, D, S1, 0, -S1, -D, -C1, -1, -C1, -D, -S1, 0, S1, D, C1];
    static readonly double[] Sin16 = [0, S1, D, C1, 1, C1, D, S1, 0, -S1, -D, -C1, -1, -C1, -D, -S1];

    /// <summary>Сектор 0..15 → одиничний вектор. Поза межами — нуль (так «відпустив» не рухає).</summary>
    public static (double Cx, double Sy) Dir16(int a) => a is >= 0 and < 16 ? (Cos16[a], Sin16[a]) : (0, 0);

    public static double Cos(int a) => Cos16[a & 15];
    public static double Sin(int a) => Sin16[a & 15];

    /// <summary>
    /// Пружний удар двох кругляшів. Якщо вони перетнулись — завжди розводимо на повну глибину перетину,
    /// пропорційно оберненим масам; імпульс — лише коли вони йдуть назустріч (<c>vr &lt; 0</c>).
    /// Повертає |J| (0 — не торкались або розлітались). Обидва кінематичні — нічого не робимо.
    /// </summary>
    public static double Collide(ref ArenaBody a, ref ArenaBody b, double e)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var rr = a.R + b.R;
        var d2 = dx * dx + dy * dy;
        if (d2 >= rr * rr) return 0;
        var inv = a.InvM + b.InvM;
        if (inv <= 0) return 0;
        double d, nx, ny;
        if (d2 < 1e-12)
        {
            // Центри збіглись (буває лише в штучних тестах) — розводимо праворуч, аби не ділити на нуль.
            d = 0;
            nx = 1;
            ny = 0;
        }
        else
        {
            d = Math.Sqrt(d2);
            nx = dx / d;
            ny = dy / d;
        }
        var pen = rr - d;
        var ka = pen * a.InvM / inv;
        var kb = pen * b.InvM / inv;
        a.X -= nx * ka;
        a.Y -= ny * ka;
        b.X += nx * kb;
        b.Y += ny * kb;

        var vr = (b.Vx - a.Vx) * nx + (b.Vy - a.Vy) * ny;
        if (vr >= 0) return 0;
        var j = -(1 + e) * vr / inv;
        a.Vx -= j * nx * a.InvM;
        a.Vy -= j * ny * a.InvM;
        b.Vx += j * nx * b.InvM;
        b.Vy += j * ny * b.InvM;
        return j;
    }

    /// <summary>Відбій від вертикальних стін x = min і x = max: дзеркало позиції й нормальної швидкості з пружністю e.</summary>
    public static void ReflectX(ref ArenaBody b, double min, double max, double e)
    {
        if (b.X < min)
        {
            b.X = 2 * min - b.X;
            if (b.Vx < 0) b.Vx = -b.Vx * e;
        }
        else if (b.X > max)
        {
            b.X = 2 * max - b.X;
            if (b.Vx > 0) b.Vx = -b.Vx * e;
        }
    }

    /// <summary>Те саме для горизонтальних стін y = min і y = max.</summary>
    public static void ReflectY(ref ArenaBody b, double min, double max, double e)
    {
        if (b.Y < min)
        {
            b.Y = 2 * min - b.Y;
            if (b.Vy < 0) b.Vy = -b.Vy * e;
        }
        else if (b.Y > max)
        {
            b.Y = 2 * max - b.Y;
            if (b.Vy > 0) b.Vy = -b.Vy * e;
        }
    }

    /// <summary>Крок з тертям: p += v·h; v *= (1 − mu·h); |v| ≤ vmax. Порядок операцій — як у браузері.</summary>
    public static void Integrate(ref ArenaBody b, double h, double mu, double vmax)
    {
        b.X += b.Vx * h;
        b.Y += b.Vy * h;
        var k = 1 - mu * h;
        b.Vx *= k;
        b.Vy *= k;
        Cap(ref b, vmax);
    }

    /// <summary>Жорстка стеля модуля швидкості.</summary>
    public static void Cap(ref ArenaBody b, double vmax)
    {
        var s2 = b.Vx * b.Vx + b.Vy * b.Vy;
        if (s2 <= vmax * vmax) return;
        var k = vmax / Math.Sqrt(s2);
        b.Vx *= k;
        b.Vy *= k;
    }
}
