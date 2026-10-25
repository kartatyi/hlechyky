namespace Hlechyky.Games.Impl;

/// <summary>
/// Один плід у повітрі: <c>Id</c> — номер (основний розклад з 1, злива — з 10 000·n), <c>K</c> — вид (літера на дроті),
/// <c>At</c> — ігрова мілісекунда вильоту, <c>D</c> — тривалість польоту (ігрові мс, від низу поля до низу). Геометрія —
/// частки поля: <c>X0</c> → <c>X1</c> по горизонталі, <c>H</c> — висота верхівки дуги, <c>Spin</c> — оберти за політ.
/// </summary>
public sealed record ShatkuyFruit(int Id, char K, int At, int D, double X0, double X1, double H, double Spin)
{
    public bool Glek => K == ShatkuyCore.Glek;
    /// <summary>Великий — кавун Ярмарку чи гарбуз-велетень Класики: б'ють багато разів, падіння — не промах.</summary>
    public bool Big => ShatkuyCore.IsBig(K);
    /// <summary>Остання ігрова мить, коли розріз ще приймаємо (політ + запас на мережу).</summary>
    public int Until => At + D + ShatkuyCore.LagMs;
}

/// <summary>
/// Режим: тривалість (0 — до трьох промахів), чи є життя, глеки, особливі плоди й великий кавун наприкінці.
/// v2: <paramref name="Pepper"/> — чи летить перець (у Дзені нема), <paramref name="Series"/> — чи множить серія очки
/// (у Дзені ні), <paramref name="Giants"/> — гарбуз-велетень кожні <see cref="ShatkuyCore.GiantEvery"/> очок (Класика).
/// </summary>
public sealed record ShatkuyMode(string Id, int LenMs, bool Lives, bool Gleks, bool Specials, bool BigKavun,
    bool Pepper = true, bool Series = true, bool Giants = false)
{
    public bool Timed => LenMs > 0;
}

/// <summary>SplitMix64 — маленький детермінований ГВЧ: той самий сід дає той самий розклад (щоденна однакова для всіх).</summary>
public struct ShatkuyRng(ulong seed)
{
    ulong _s = seed;

    public ulong Next()
    {
        var z = _s += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>[0, 1).</summary>
    public double D() => (Next() >> 11) * (1.0 / (1UL << 53));
    public double D(double a, double b) => a + (b - a) * D();
    /// <summary>[0, n).</summary>
    public int I(int n) => (int)(D() * n);
    public bool P(double p) => D() < p;
}

/// <summary>
/// Математика «Шаткуй!» (docs/games/specs/shatkuy.md): види плодів, режими, розклад кидків з сіду й «ігровий час», який
/// мороз сповільнює вдвічі. Розклад — у ігрових мілісекундах і не залежить від того, що людина ріже (крім зливи: її плоди
/// докидаються з власного сіду від номера зливи), тож сервер і браузер бачать ті самі дуги.
/// </summary>
public static class ShatkuyCore
{
    public const char Glek = 'G', Frost = 'F', Double = 'X', Shower = 'Z', Big = 'B';
    /// <summary>
    /// v2 (контракт _tools/shatkuy-v2.md §1, §4): гарбуз <c>p</c> — два удари; часник <c>c</c> розпадається на три зубчики
    /// <c>v</c>; перець <c>h</c> — пропущений пече; картопля <c>o</c> — мала й швидка; <c>P</c> — гарбуз-велетень Класики.
    /// </summary>
    public const char Pumpkin = 'p', Garlic = 'c', Clove = 'v', Pepper = 'h', Potato = 'o', Giant = 'P';
    /// <summary>Звичайні плоди (з v1). Ціна — <see cref="Price"/>: гарбуз 3, решта 1.</summary>
    public const string Plain = "sbagdpkrm";
    public const string Specials = "FXZ";
    /// <summary>Овочі з характером (v2): часник, перець, картопля — підміняють звичайні з третьої хвилі.</summary>
    public const string Veggies = "cho";
    /// <summary>Яка частка звичайних плодів хвилі стає новими овочами (з третьої хвилі).</summary>
    public const double VeggieShare = 0.25;
    /// <summary>Другий удар по гарбузу — іншим змахом і не раніше ніж за стільки справжніх мс після першого.</summary>
    public const int CrackGapMs = 120;
    /// <summary>Картопля летить у 1,35 раза швидше: її політ — стільки від звичайного.</summary>
    public const double PotatoFly = 0.75;
    /// <summary>Пропущений перець пече стільки справжніх мс (марево по краях поля) і рве серію.</summary>
    public const int BurnMs = 2_500;
    public const int Cloves = 3;
    /// <summary>Номери зубчиків: <c>CloveBase + часник·4 + j</c> (часник — лише з основного розкладу).</summary>
    public const int CloveBase = 2_000_000;
    /// <summary>Класика: кожні стільки очок наступна хвиля — гарбуз-велетень.</summary>
    public const int GiantEvery = 100;
    /// <summary>Пороги множника серії: ×2 з 10, ×3 з 25, ×4 з 50 (стеля).</summary>
    public static readonly int[] SeriesAt = [10, 25, 50];

    /// <summary>Скільки ігрових мс після падіння ще приймаємо розріз (мережа, годинник).</summary>
    public const int LagMs = 600;
    /// <summary>На скільки ігрових мс наперед сервер показує розклад — щоб браузер малював виліт вчасно.</summary>
    public const int LookMs = 1_000;
    /// <summary>Від «Поїхали» до першої ігрової мілісекунди.</summary>
    public const int LeadMs = 1_200;
    /// <summary>Мороз (справжні мс) — ігровий час іде вдвічі повільніше.</summary>
    public const int FrostMs = 5_000;
    public const double FrostRate = 0.5;
    /// <summary>Подвійні очки (справжні мс).</summary>
    public const int DoubleMs = 7_000;
    /// <summary>Злива: стільки плодів з боків, кожні <see cref="ShowerStepMs"/>.</summary>
    public const int ShowerCount = 18, ShowerStepMs = 170;
    /// <summary>Великий кавун: вилітає за стільки до кінця, летить повільно, ударів — до <see cref="BigMaxHits"/>.</summary>
    public const int BigBeforeEndMs = 7_000, BigFlyMs = 5_000, BigMaxHits = 30, BigGapMs = 60;
    /// <summary>Глек у режимах на час — мінус стільки очок.</summary>
    public const int GlekPenalty = 10;
    public const int Lives = 3, LifeEvery = 100;

    public static readonly ShatkuyMode Classic = new("classic", 0, Lives: true, Gleks: true, Specials: false, BigKavun: false, Giants: true);
    public static readonly ShatkuyMode Fair = new("fair", 60_000, Lives: false, Gleks: true, Specials: true, BigKavun: true);
    public static readonly ShatkuyMode Zen = new("zen", 90_000, Lives: false, Gleks: false, Specials: false, BigKavun: false, Pepper: false, Series: false);
    public static readonly ShatkuyMode Daily = new("daily", 45_000, Lives: false, Gleks: true, Specials: true, BigKavun: true);
    /// <summary>«Шаткуй разом» (v2 §6): хвилина за правилами Ярмарку, кожен на своєму полі з тим самим розкладом.</summary>
    public static readonly ShatkuyMode Party = new("party", 60_000, Lives: false, Gleks: true, Specials: true, BigKavun: true);

    public static bool IsPlain(char k) => Plain.Contains(k);
    public static bool IsBig(char k) => k is Big or Giant;

    /// <summary>Чи падіння плоду — промах (життя в Класиці, кінець серії): звичайні, часник і картопля. Перець пече окремо, зубчики, глек, особливі й великі — ні.</summary>
    public static bool IsMiss(char k) => IsPlain(k) || k is Garlic or Potato;

    /// <summary>Основна ціна розрізаного плоду (її й множить серія).</summary>
    public static int Price(char k) => k switch { Pumpkin => 3, Pepper or Potato => 2, _ => 1 };

    /// <summary>Множник серії: ×1 до 9 поспіль, ×2 з 10, ×3 з 25, ×4 з 50.</summary>
    public static int Mult(int streak) => streak >= SeriesAt[2] ? 4 : streak >= SeriesAt[1] ? 3 : streak >= SeriesAt[0] ? 2 : 1;

    /// <summary>Радіуси нових плодів (частки меншого боку поля, як <c>KINDS[k].r</c> у shatkuy.js) — для клієнта у <c>k.r</c>.</summary>
    public static readonly IReadOnlyDictionary<string, double> NewRadii = new Dictionary<string, double>
    {
        ["c"] = 0.055, ["v"] = 0.035, ["h"] = 0.05, ["o"] = 0.045, ["P"] = 0.17,
    };

    // ---------- ігровий час ----------

    /// <summary>
    /// Ігрова мілісекунда для справжньої <paramref name="t"/> (мс від t0): мороз — відрізки справжнього часу, у яких гра йде
    /// з <see cref="FrostRate"/>. Відрізки не перетинаються (новий мороз під час старого лише подовжує його).
    /// </summary>
    public static int Game(int t, IReadOnlyList<(int S, int E)> frost)
    {
        double slow = 0;
        foreach (var (s, e) in frost)
        {
            if (t <= s) break;
            slow += Math.Min(t, e) - s;
        }
        return (int)Math.Floor(t - slow * (1 - FrostRate));
    }

    /// <summary>Мороз з мить <paramref name="t"/>: подовжити поточний або почати новий.</summary>
    public static void AddFrost(List<(int S, int E)> frost, int t) => AddSpan(frost, t, FrostMs);

    public static void AddSpan(List<(int S, int E)> spans, int t, int ms)
    {
        if (spans.Count > 0 && spans[^1].E > t) spans[^1] = (spans[^1].S, t + ms);
        else spans.Add((t, t + ms));
    }

    public static bool Inside(IReadOnlyList<(int S, int E)> spans, int t)
    {
        foreach (var (s, e) in spans) if (t >= s && t < e) return true;
        return false;
    }

    // ---------- злива ----------

    /// <summary>Злива №<paramref name="n"/> (з 1), що почалась на ігровій <paramref name="g"/>: плоди з обох боків навхрест.</summary>
    public static List<ShatkuyFruit> ShowerOf(ulong seed, int n, int g)
    {
        var rng = new ShatkuyRng(seed ^ (0xA5A5_5A5A_1234_4321UL * (ulong)n));
        var list = new List<ShatkuyFruit>(ShowerCount);
        for (var i = 0; i < ShowerCount; i++)
        {
            var left = i % 2 == 0;
            var x1 = rng.D(0.35, 0.68);
            list.Add(new ShatkuyFruit(10_000 * n + i + 1, Plain[rng.I(Plain.Length)], g + 250 + i * ShowerStepMs, rng.I(400) + 1_900,
                left ? -0.06 : 1.06, left ? x1 : 1 - x1, rng.D(0.5, 0.82), rng.D(-1.4, 1.4)));
        }
        return list;
    }

    // ---------- часник ----------

    public static int CloveId(int garlic, int j) => CloveBase + garlic * 4 + j;

    /// <summary>
    /// Три зубчики часнику, розрізаного на ігровій <paramref name="g"/>: вилітають віялом угору з точки, де часник був
    /// за формулою польоту (частки поля, без радіуса). Кожен — звичайна дуга, яку браузер малює тим самим <c>where</c>:
    /// її виліт <c>at</c> — у минулому, так що на <c>g</c> зубчик саме в точці розрізу й ще піднімається. Розкид — із сіду
    /// розкладу й номера часнику: той самий розріз дає ті самі зубчики (Шаткуй дня, «разом»).
    /// </summary>
    public static List<ShatkuyFruit> ClovesOf(ulong seed, ShatkuyFruit garlic, int g)
    {
        var s = Math.Clamp((g - garlic.At) / (double)garlic.D, 0, 1);
        var x = garlic.X0 + (garlic.X1 - garlic.X0) * s;
        var y = garlic.H * 4 * s * (1 - s);   // висота над низом поля, частки висоти
        var rng = new ShatkuyRng(seed ^ (0xC1C1_0B0B_5EED_0001UL * (ulong)(garlic.Id + 1)));
        var list = new List<ShatkuyFruit>(Cloves);
        for (var j = 0; j < Cloves; j++)
        {
            var d = 1_500 + rng.I(300);
            var h = Math.Clamp(y + rng.D(0.12, 0.22), 0.2, 0.95);
            var q = Math.Clamp(y / h, 0, 0.98);   // 4·s0·(1 − s0) на висоті розрізу
            var s0 = (1 - Math.Sqrt(1 - q)) / 2;    // висхідна гілка дуги
            var x1 = Math.Clamp(x + (j - 1) * 0.16 + rng.D(-0.03, 0.03), 0.04, 0.96);
            var x0 = (x - x1 * s0) / (1 - s0);
            list.Add(new ShatkuyFruit(CloveId(garlic.Id, j), Clove, g - (int)Math.Round(s0 * d), d, x0, x1, h, rng.D(-2, 2)));
        }
        return list;
    }
}

/// <summary>
/// Розклад кидків режиму, лінивий: <see cref="Until"/> докидає хвилі, поки наступна не далі за потрібну ігрову мить.
/// Темп росте з часом: у Класиці — без кінця, у режимах на час — до кінця партії; глеки й особливі — за режимом.
/// </summary>
public sealed class ShatkuyPlan
{
    readonly ShatkuyMode _mode;
    ShatkuyRng _rng;
    int _next = 400, _id, _wave;
    int _specialAt;
    char _lastSpecial;
    bool _big;
    int _giants;

    public List<ShatkuyFruit> Fruits { get; } = [];
    public ulong Seed { get; }

    public ShatkuyPlan(ShatkuyMode mode, ulong seed)
    {
        _mode = mode;
        Seed = seed;
        _rng = new ShatkuyRng(seed);
        _specialAt = 7_000 + _rng.I(4_000);
    }

    /// <summary>
    /// Класика (v2 §4): наступна ще не докинута хвиля — гарбуз-велетень; хвилі на час його польоту не летять.
    /// ГВЧ велетень не чіпає — далі розклад той самий, лише зсунутий.
    /// </summary>
    public void QueueGiant() => _giants++;

    /// <summary>Докинути хвилі, що вилітають до ігрової <paramref name="g"/> включно.</summary>
    public void Until(int g)
    {
        while (_next <= g)
        {
            if (_giants > 0 && !_mode.Timed)
            {
                _giants--;
                Fruits.Add(new ShatkuyFruit(++_id, ShatkuyCore.Giant, _next, ShatkuyCore.BigFlyMs, 0.5, 0.5, 0.6, 0.25));
                _next += ShatkuyCore.BigFlyMs + 600;
                continue;
            }
            if (_mode.Timed && _next >= _mode.LenMs - (_mode.BigKavun ? ShatkuyCore.BigBeforeEndMs : 1_200))
            {
                if (_mode.BigKavun && !_big)
                {
                    _big = true;
                    var at = _mode.LenMs - ShatkuyCore.BigBeforeEndMs + 300;
                    Fruits.Add(new ShatkuyFruit(++_id, ShatkuyCore.Big, at, ShatkuyCore.BigFlyMs, 0.5, 0.5, 0.6, 0.25));
                }
                _next = int.MaxValue;
                return;
            }
            Wave(_next);
        }
    }

    /// <summary>Скільки в середньому між хвилями й до скількох плодів у хвилі — від ігрової секунди.</summary>
    (int Gap, int Max) Tempo(int g)
    {
        var s = g / 1000.0;
        if (!_mode.Timed) return ((int)Math.Max(750, 2_300 - 22 * s), (int)Math.Min(6, 2 + s / 22));
        var p = Math.Clamp(g / (double)_mode.LenMs, 0, 1);
        var gap = _mode.Gleks ? 1_650 - 650 * p : 1_450 - 500 * p;
        return ((int)gap, (int)Math.Round(3 + 2.5 * p));
    }

    void Wave(int g)
    {
        _wave++;
        var (gap, max) = Tempo(g);
        var n = 1 + _rng.I(max);
        if (_wave <= 2) n = Math.Min(n, 2);
        var pattern = _rng.D();
        var speed = _mode.Timed ? 1.0 : Math.Max(0.82, 1 - g / 400_000.0);

        // що летить: звичайні, глек(и), особливий
        var kinds = new List<char>(n + 2);
        for (var i = 0; i < n; i++) kinds.Add(Plain());
        if (_mode.Gleks && g > 5_000)
        {
            var p = _mode.Timed ? 0.22 + 0.18 * (g / (double)_mode.LenMs) : Math.Min(0.45, 0.1 + g / 250_000.0);
            if (_rng.P(p)) kinds.Insert(_rng.I(kinds.Count + 1), ShatkuyCore.Glek);
            if (!_mode.Timed && g > 60_000 && _rng.P(0.25)) kinds.Insert(_rng.I(kinds.Count + 1), ShatkuyCore.Glek);
        }
        if (_mode.Specials && g >= _specialAt)
        {
            var pick = ShatkuyCore.Specials[_rng.I(3)];
            if (pick == _lastSpecial) pick = ShatkuyCore.Specials[(ShatkuyCore.Specials.IndexOf(pick) + 1 + _rng.I(2)) % 3];
            _lastSpecial = pick;
            kinds.Insert(_rng.I(kinds.Count + 1), pick);
            _specialAt = g + 9_000 + _rng.I(5_000);
        }

        var m = kinds.Count;
        if (pattern < 0.6 || m == 1)
        {
            // віяло: усі з низу разом, розкидані по ширині
            for (var i = 0; i < m; i++)
            {
                var x0 = m == 1 ? _rng.D(0.2, 0.8) : 0.14 + 0.72 * (i + _rng.D(0.15, 0.85)) / m;
                Add(kinds[i], g + _rng.I(120), x0, Toward(x0), speed);
            }
        }
        else if (pattern < 0.85)
        {
            // низка: один за одним від краю до краю
            var ltr = _rng.P(0.5);
            for (var i = 0; i < m; i++)
            {
                var x0 = 0.15 + 0.7 * (m == 1 ? 0.5 : i / (double)(m - 1));
                if (!ltr) x0 = 1 - x0;
                Add(kinds[i], g + i * 190, x0, x0 + (ltr ? 0.08 : -0.08), speed);
            }
            gap += (m - 1) * 190;
        }
        else
        {
            // навхрест: із кутів до протилежних боків
            for (var i = 0; i < m; i++)
            {
                var left = i % 2 == 0;
                var x0 = left ? _rng.D(0.06, 0.22) : _rng.D(0.78, 0.94);
                Add(kinds[i], g + (i / 2) * 140, x0, left ? _rng.D(0.6, 0.85) : _rng.D(0.15, 0.4), speed);
            }
        }
        _next = g + gap + _rng.I(gap / 3 + 1) - gap / 6;
    }

    /// <summary>Звичайний плід хвилі; з третьої хвилі ~25 % — нові овочі (картопля й часник частіше, перець рідше).</summary>
    char Plain()
    {
        var k = ShatkuyCore.Plain[_rng.I(ShatkuyCore.Plain.Length)];
        if (_wave <= 2 || !_rng.P(ShatkuyCore.VeggieShare)) return k;
        var r = _rng.D();
        if (!_mode.Pepper) return r < 0.5 ? ShatkuyCore.Potato : ShatkuyCore.Garlic;
        return r < 0.4 ? ShatkuyCore.Potato : r < 0.8 ? ShatkuyCore.Garlic : ShatkuyCore.Pepper;
    }

    double Toward(double x0) => Math.Clamp(x0 + (0.5 - x0) * _rng.D(0.1, 0.6) + _rng.D(-0.08, 0.08), 0.08, 0.92);

    void Add(char k, int at, double x0, double x1, double speed)
    {
        var d = (int)((2_600 + _rng.I(600)) * speed * (k == ShatkuyCore.Potato ? ShatkuyCore.PotatoFly : 1));
        var h = k == ShatkuyCore.Glek ? _rng.D(0.5, 0.78) : _rng.D(0.55, 0.88);
        Fruits.Add(new ShatkuyFruit(++_id, k, at, d, x0, x1, h, _rng.D(-1.5, 1.5)));
    }
}
