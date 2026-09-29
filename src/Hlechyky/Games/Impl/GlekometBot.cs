namespace Hlechyky.Games.Impl;

/// <summary>
/// Бот-гармаш Глекометів, коли людина за столом сама. Бачить те саме, що й людина: рельєф, хати, вітер на табло — і
/// рахує в голові «шкільну» параболу (без вирв, підков, скалок), а не проганяє справжню фізику ядра. Перший глек
/// по новій цілі — «на око» (помилка відстані за рівнем), а далі — <b>пристрілка</b>: бот пам'ятає свої останні
/// недольоти/перельоти й розбирає їх на дві причини — «цілю не туди» (сталий зсув) і «вітер дме сильніше, ніж
/// здається» (частка вітру). Найменші квадрати на двох невідомих з довірою до першого враження: легкий вчиться мляво
/// й пам'ятає мало, сильний — швидко. Як людина: «було коротко — дам сили; на такому вітрі зносить більше».
/// <para>
/// Рівні — наскільки він зважає на вітер, як тремтить рука (кут, сила), як жваво виправляється і як часто «ляпне»
/// зовсім мимо. Легкий майже не бачить вітру й виправляється мляво — новачок його переграє; сильний зважає на вітер
/// і за два-три ходи пристрілюється, але рука все одно тремтить і вітер щоходу новий.
/// </para>
/// </summary>
public sealed class GlekometBot
{
    /// <summary>Постріл: кут (0–180), сила (5–100), зброя (індекс комори).</summary>
    public readonly record struct Shot(int A, int P, int W);

    /// <summary>
    /// Яку частку вітру бот бачить спершу (±розкид щоходу) — легкий / звичайний / сильний. Далі частку він «вчить» з
    /// промахів на вітрі; <see cref="TrustWind"/> — наскільки чіпко тримається першого враження (у u², бо промах на
    /// вітрі 5 при далекому кидку — сотні u на одиницю частки).
    /// </summary>
    static readonly double[] WindSense = [0.3, 0.7, 0.9], WindSpread = [0.25, 0.12, 0.06];
    static readonly double[] TrustWind = [4e5, 6e4, 2e4];
    /// <summary>Тремтіння руки: σ кута (градуси) і сили.</summary>
    static readonly double[] AngleSd = [2, 0.8, 0.4], PowerSd = [3, 1.2, 0.6];
    /// <summary>Довіра до першого «на око» проти одного промаху (1 — як один постріл): легкий упертий, сильний — ні.</summary>
    static readonly double[] TrustGut = [1.5, 0.6, 0.25];
    /// <summary>Скільки останніх промахів по цілі тримає в голові.</summary>
    static readonly int[] Memory = [2, 4, 6];
    /// <summary>«На око» перед першим глеком по новій цілі: σ помилки відстані (u) — перший постріл пристрілювальний.</summary>
    static readonly double[] GutSd = [130, 80, 45];
    /// <summary>«Ляпнув»: шанс пострілу, у якому сила зовсім не та.</summary>
    static readonly double[] BlunderP = [0.15, 0.06, 0.02];
    /// <summary>Скільки думає над ходом, мс (від, до).</summary>
    static readonly int[] ThinkMin = [3000, 2000, 1500], ThinkMax = [6000, 4000, 3000];
    /// <summary>Кути, які бот пробує (від найзвичнішого): високий лоб перелітає пагорби, низький — далі б'є.</summary>
    static readonly int[] Angles = [50, 60, 40, 70, 30, 78];

    int _lvl = 1;
    /// <summary>Поправка прицілу (u по x): куди цілити замість хати, щоб лягло в хату.</summary>
    double _corr;
    /// <summary>Вивчена частка вітру.</summary>
    double _m;
    /// <summary>Перше враження по цілі — від нього пристрілка й відштовхується.</summary>
    double _gut;
    /// <summary>Останній постріл у голові бота: з чого, куди, з якою силою й вітром — щоб потім розібрати промах.</summary>
    int _sa, _sw, _swind;
    double _sp, _sm, _sx0, _sy0, _sty, _scorr;
    /// <summary>Пам'ять пострілів по цілі: (чутливість до частки вітру S, y = промах + поправка + S·частка).</summary>
    readonly List<(double S, double Y)> _hist = [];
    int _target = -1, _targetX;
    /// <summary>Куди цілився останній постріл (у голові) і куди метив насправді — для пристрілки.</summary>
    double _aimedX = double.NaN, _tx;
    public double LastMiss { get; private set; } = double.NaN;
    public int Shots { get; private set; }
    /// <summary>Скільки глеків бота вже впало (і пішло в пристрілку).</summary>
    public int Landings { get; private set; }

    public void Reset(LiveBots.Level level)
    {
        _lvl = LiveBots.Index(level);
        _corr = _gut = 0;
        _m = WindSense[_lvl];
        _hist.Clear();
        _target = -1;
        _aimedX = double.NaN;
        LastMiss = double.NaN;
        Shots = 0;
        Landings = 0;
    }

    public int ThinkMs(Random rng) => ThinkMin[_lvl] + rng.Next(ThinkMax[_lvl] - ThinkMin[_lvl] + 1);

    /// <summary>Глек бота впав у <paramref name="x"/>: недоліт/переліт — у поправку на наступний хід.</summary>
    public void Landed(double x)
    {
        if (double.IsNaN(_aimedX)) return;
        var miss = x - _tx;
        LastMiss = miss;
        Landings++;
        _aimedX = double.NaN;
        // Розсипний розлітається віялом: де впала остання скалка — ще не «куди летів глек», тож не вчимось.
        // Вилетів за край — знаємо лише «далеко», а не де: теж не вчимось, але зсув трохи підправимо.
        if (_sw == GlekometCore.Shards) return;
        if (x <= 0 || x >= GlekometCore.W) { _corr += 0.3 * miss; return; }
        // Скільки зсунула б падіння кожна десята частки вітру — з тієї самої параболи в голові.
        var wpn = GlekometCore.Weapons[_sw];
        double X(double m) => LandX(_sa, _sp, _sx0, _sy0, _sty, _swind * m * GlekometCore.WindAcc * wpn.WindK, GlekometCore.G * wpn.GravK);
        var S = (X(_sm + 0.1) - X(_sm)) / 0.1;
        if (double.IsNaN(S)) S = 0;
        // промах = −поправка + зсув + S·(частка − вжита частка)  ⇒  y = промах + поправка + S·вжита = зсув + S·частка
        _hist.Add((S, miss + _scorr + S * _sm));
        if (_hist.Count > Memory[_lvl]) _hist.RemoveAt(0);
        Learn();
    }

    /// <summary>
    /// Найменші квадрати на «зсув» c і «частку вітру» μ за пам'яттю пострілів, з довірою до першого враження
    /// (c₀ = «на око», μ₀ = як бачить вітер рівень). Система 2×2 — копійки.
    /// </summary>
    void Learn()
    {
        double lc = TrustGut[_lvl], lm = TrustWind[_lvl], m0 = WindSense[_lvl];
        double a11 = lc, a12 = 0, a22 = lm, b1 = lc * _gut, b2 = lm * m0;
        foreach (var (S, y) in _hist)
        {
            a11 += 1;
            a12 += S;
            a22 += S * S;
            b1 += y;
            b2 += S * y;
        }
        var det = a11 * a22 - a12 * a12;
        if (Math.Abs(det) < 1e-9) return;
        _corr = Math.Clamp((b1 * a22 - a12 * b2) / det, -300, 300);
        _m = Math.Clamp((a11 * b2 - a12 * b1) / det, 0, 1.4);
    }

    /// <summary>Спланувати постріл місця <paramref name="me"/> по хаті <paramref name="target"/>; null — нема по кому.</summary>
    public Shot? Plan(GlekometCore core, int me, int target, int[] inv, int kinds, Random rng)
    {
        if (target < 0) return null;
        var hut = core.Huts[me];
        var t = core.Huts[target];
        // Інша ціль чи хата від'їхала далеко — стара пристрілка вже не про неї.
        if (target != _target || Math.Abs(t.X - _targetX) > 60)
        {
            _corr = _gut = Gauss(rng) * GutSd[_lvl];
            _hist.Clear();
            LastMiss = double.NaN;
        }
        _target = target;
        _targetX = t.X;
        var w = Weapon(inv, kinds, rng);
        var wpn = GlekometCore.Weapons[w];
        var m = _m + (rng.NextDouble() * 2 - 1) * WindSpread[_lvl];
        var wind = core.Wind * m;
        var ax = wind * GlekometCore.WindAcc * wpn.WindK;
        var g = GlekometCore.G * wpn.GravK;
        // стріляють із даху хати (Launch), а цілимо в середину чужої хати
        double x0 = hut.X, y0 = hut.Y + GlekometCore.Launch, ty = t.Y + GlekometCore.HutH / 2.0;
        _tx = t.X;
        var aimX = t.X - _corr;
        var right = aimX >= x0;

        int bestA = right ? 45 : 135;
        double bestP = 100;
        var found = false;
        foreach (var a0 in Angles)
        {
            var a = right ? a0 : 180 - a0;
            if (Solve(a, x0, y0, aimX, ty, ax, g) is not { } p) continue;
            if (!found) { bestA = a; bestP = p; found = true; }
            if (Clear(core, a, p, x0, y0, t.X, ty, ax, g)) { bestA = a; bestP = p; break; }
        }
        // рука тремтить
        var ang = bestA + Gauss(rng) * AngleSd[_lvl];
        var pow = bestP + Gauss(rng) * PowerSd[_lvl];
        if (rng.NextDouble() < BlunderP[_lvl]) pow += (rng.Next(2) == 0 ? -1 : 1) * (8 + rng.Next(8));
        _aimedX = aimX;
        (_sa, _sp, _sw, _swind, _sm, _sx0, _sy0, _sty, _scorr) = (bestA, bestP, w, core.Wind, m, x0, y0, ty, _corr);
        Shots++;
        return new Shot((int)Math.Clamp(Math.Round(ang), 0, 180), (int)Math.Clamp(Math.Round(pow), 5, 100), w);
    }

    /// <summary>
    /// Чим стріляти. Легкий — глеком (зрідка розсипним навмання); звичайний і сильний, коли вже пристрілялись
    /// (минулий недоліт/переліт ≲ 30 u), б'ють вареником, а коли «майже» — розсипним, що накриває ширше.
    /// </summary>
    int Weapon(int[] inv, int kinds, Random rng)
    {
        bool Has(int w) => w < kinds && inv[w] != 0;
        var miss = double.IsNaN(LastMiss) ? double.MaxValue : Math.Abs(LastMiss);
        if (_lvl == 0) return Has(GlekometCore.Shards) && rng.NextDouble() < 0.15 ? GlekometCore.Shards : GlekometCore.Pot;
        if (miss < 30 && Has(GlekometCore.Varenyk)) return GlekometCore.Varenyk;
        if (miss < 80 && Has(GlekometCore.Shards) && rng.NextDouble() < 0.5) return GlekometCore.Shards;
        return GlekometCore.Pot;
    }

    /// <summary>Сила, з якою глек під кутом <paramref name="a"/> ляже в <paramref name="aimX"/> (бісекція; null — не докинути).</summary>
    static double? Solve(int a, double x0, double y0, double aimX, double ty, double ax, double g)
    {
        var dir = aimX >= x0 ? 1 : -1;
        double lo = 5, hi = 100;
        var xHi = LandX(a, hi, x0, y0, ty, ax, g);
        if (double.IsNaN(xHi) || (xHi - aimX) * dir < 0) return null;
        var xLo = LandX(a, lo, x0, y0, ty, ax, g);
        if (!double.IsNaN(xLo) && (xLo - aimX) * dir > 0) return lo;
        for (var i = 0; i < 22; i++)
        {
            var mid = (lo + hi) / 2;
            var x = LandX(a, mid, x0, y0, ty, ax, g);
            if (double.IsNaN(x) || (x - aimX) * dir < 0) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2;
    }

    /// <summary>Де парабола перетне висоту цілі на спаді; NaN — туди не долетить.</summary>
    static double LandX(int a, double p, double x0, double y0, double ty, double ax, double g)
    {
        var v = GlekometCore.VPerPower * p;
        var rad = a * Math.PI / 180;
        double vx = v * Math.Cos(rad), vy = v * Math.Sin(rad);
        var disc = vy * vy + 2 * g * (y0 - ty);
        if (disc < 0) return double.NaN;
        var tt = (vy + Math.Sqrt(disc)) / g;
        return x0 + vx * tt + 0.5 * ax * tt * tt;
    }

    /// <summary>Чи не впреться парабола в пагорб (чи не вилетить за край) раніше, ніж дійде до цілі.</summary>
    static bool Clear(GlekometCore core, int a, double p, double x0, double y0, double tx, double ty, double ax, double g)
    {
        var v = GlekometCore.VPerPower * p;
        var rad = a * Math.PI / 180;
        double vx = v * Math.Cos(rad), vy = v * Math.Sin(rad);
        var disc = vy * vy + 2 * g * (y0 - ty);
        if (disc < 0) return false;
        var total = (vy + Math.Sqrt(disc)) / g;
        const int N = 32;
        for (var i = 1; i < N; i++)
        {
            var tt = total * i / N;
            var x = x0 + vx * tt + 0.5 * ax * tt * tt;
            var y = y0 + vy * tt - 0.5 * g * tt * tt;
            if (x < 0 || x > GlekometCore.W) return false;
            if (Math.Abs(x - x0) < GlekometCore.HutHalf + 6 || Math.Abs(x - tx) < GlekometCore.HutHalf + 10) continue;
            if (y < core.H[GlekometCore.Col((int)x)] + 4) return false;
        }
        return true;
    }

    static double Gauss(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * rng.NextDouble());
    }
}
