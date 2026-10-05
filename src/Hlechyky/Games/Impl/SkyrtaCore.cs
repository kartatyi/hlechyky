namespace Hlechyky.Games.Impl;

/// <summary>
/// Рушій «Скирти» (docs/games/specs/skyrta.md): рух снопа над скиртою і як він лягає. Усе — цілими числами й
/// чистими функціями від часу, бо той самий код повторює браузер (web/games/skyrta.js): гравець тапає миттєво, а
/// сервер за номером снопа й моментом тапу рахує те саме, що й клієнт. Тому тут ні double, ні Math.Sin — лише
/// цілі, що й у JS дають біт-у-біт той самий результат (усі ділення — над невід'ємними числами).
/// Одиниці: довжина — «одиниці поля» (поле 1000 завширшки), час — мілісекунди від старту гри (фази «go»).
/// </summary>
public static class SkyrtaCore
{
    public const int TickMs = 40;
    public const int Seats = 8;

    /// <summary>Поле: ширина й межі, між якими ходить ЦЕНТР снопа (крайні снопи звисають за поле — так і задумано).</summary>
    public const int FieldW = 1000, Lo = 100, Hi = 900, Span = Hi - Lo;

    /// <summary>Підвалина скирти: снопи з першого стають на неї. Ширша за неї скирта не буває.</summary>
    public const int BaseW = 400, BaseL = (FieldW - BaseW) / 2;

    /// <summary>Швидкість снопа, одиниць/с: від висоти скирти (скільки вже покладено), зі стелею.</summary>
    public const int V0 = 420, Vk = 28, Vmax = 1100;

    /// <summary>Пауза між снопами: обрізок падає, новий виїжджає. Промах — довше (сніп летить із скирти).</summary>
    public const int Gap = 350, MissGap = 900;

    /// <summary>Вітер: налітає через <see cref="WindDelay"/> після трьох ідеальних сусіда й дме <see cref="WindMs"/>; сніп ×1,5.</summary>
    public const int WindMs = 3000, WindDelay = 500;

    /// <summary>Скільки вікон вітру пам'ятає один сніп (більше — лише в того, хто стоїть AFK під вітрами).</summary>
    public const int MaxWinds = 4;

    /// <summary>Гойдання: з якої висоти (покладено ≥ стільки) і яке — трикутник ±<see cref="SwayA"/> з періодом <see cref="SwayP"/> мс.</summary>
    public const int SwayFrom = 15, SwayA = 22, SwayP = 1400;

    /// <summary>Ідеальний сніп повертає ширину: <see cref="Grow"/>, а в серії від <see cref="BigFrom"/> — <see cref="GrowBig"/>.</summary>
    public const int Grow = 6, GrowBig = 16, BigFrom = 3;

    /// <summary>Скільки ідеальних поспіль пускає вітер на сусіда (і кожні наступні стільки ж).</summary>
    public const int WindStreak = 3;

    /// <summary>
    /// Звірка тапу з годинником сервера: момент тапу може бути в минулому не більш як на <see cref="MaxLag"/> (зв'язок,
    /// телефон «задумався») і в майбутньому не більш як на <see cref="Lead"/> (годинник браузера трохи попереду).
    /// Далі — притискаємо до меж: сніп падає там, де був у ту мить, а клієнт бере виправлення з кадру.
    /// </summary>
    public const int MaxLag = 1200, Lead = 250;

    /// <summary>Швидкість снопа на висоті <paramref name="h"/> (стільки снопів уже покладено).</summary>
    public static int Speed(int h) => Math.Min(Vmax, V0 + Vk * Math.Max(0, h));

    /// <summary>Допуск «ідеального» снопа, одиниць: що швидше сніп, то ширший (у часі це ±30–40 мс).</summary>
    public static int Tol(int v) => 6 + v / 40;

    /// <summary>Непарний сніп виїжджає зліва, парний — справа.</summary>
    public static bool FromLeft(int n) => (n & 1) == 1;

    /// <summary>Чи гойдає сніп на висоті <paramref name="h"/>.</summary>
    public static bool Sways(int h) => h >= SwayFrom;

    /// <summary>
    /// Скільки мілісекунд вітру припало на сніп, що виїхав о <paramref name="s0"/> і їде вже <paramref name="t"/> мс.
    /// <paramref name="wind"/> — вікна [a, b) у мс гри (абсолютні), пари підряд: a0, b0, a1, b1…
    /// </summary>
    public static int WindOverlap(int s0, int t, ReadOnlySpan<int> wind)
    {
        var sum = 0;
        var end = s0 + t;
        for (var i = 0; i + 1 < wind.Length; i += 2)
        {
            var a = Math.Max(wind[i], s0);
            var b = Math.Min(wind[i + 1], end);
            if (b > a) sum += b - a;
        }
        return sum;
    }

    /// <summary>
    /// Центр снопа через <paramref name="t"/> мс після виїзду: шлях = v·(t + вітер/2), трикутна хвиля між
    /// <see cref="Lo"/> і <see cref="Hi"/>, плюс гойдання вище <see cref="SwayFrom"/>.
    /// </summary>
    public static int Center(int t, int v, bool fromLeft, bool sway, int s0, ReadOnlySpan<int> wind)
    {
        if (t < 0) t = 0;
        var eff2 = 2L * t + WindOverlap(s0, t, wind);
        var d = v * eff2 / 2000;
        var p = d % (2 * Span);
        var e = (int)(p <= Span ? p : 2 * Span - p);
        var c = fromLeft ? Lo + e : Hi - e;
        if (sway) c += Sway(t);
        return c;
    }

    /// <summary>Гойдання — трикутник ±<see cref="SwayA"/>, у мить виїзду нуль.</summary>
    public static int Sway(int t)
    {
        const int half = SwayP / 2;
        var q = (t + SwayP / 4) % SwayP;
        return q < half ? -SwayA + 2 * SwayA * q / half : 3 * SwayA - 2 * SwayA * q / half;
    }

    /// <summary>Лівий край снопа ширини <paramref name="w"/> з центром <paramref name="c"/>.</summary>
    public static int Left(int c, int w) => c - (w >> 1);

    /// <summary>Що вийшло з тапу: 0 — обрізало, 1 — ідеально, 2 — промах (сніп злетів).</summary>
    public const int KindCut = 0, KindPerfect = 1, KindMiss = 2;

    /// <summary>
    /// Сніп ліг: попередній верх [<paramref name="pl"/>, pl+pw), сніп тієї ж ширини з лівим краєм <paramref name="l"/>.
    /// <paramref name="streak"/> — ідеальних поспіль ДО цього снопа. Повертає новий верх і що вийшло.
    /// </summary>
    public static (int L, int W, int Kind) Land(int pl, int pw, int l, int v, int streak)
    {
        if (Math.Abs(l - pl) <= Tol(v))
        {
            var g = streak + 1 >= BigFrom ? GrowBig : Grow;
            var nw = Math.Min(BaseW, pw + g);
            return (pl - ((nw - pw) >> 1), nw, KindPerfect);
        }
        var ol = Math.Max(l, pl);
        var or = Math.Min(l + pw, pl + pw);
        return or - ol <= 0 ? (pl, pw, KindMiss) : (ol, or - ol, KindCut);
    }
}
