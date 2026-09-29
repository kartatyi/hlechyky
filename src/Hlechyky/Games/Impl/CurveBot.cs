namespace Hlechyky.Games.Impl;

/// <summary>
/// «Голова» бота кривулі. Поле безперервне, тож повного пошуку тут нема й бути не може — бот «промацує» простір
/// кількома траєкторіями того самого плавного керма, що й у людини: тримати поворот 4, 10, 20 тиків чи весь час
/// (а потім прямо), або їхати прямо. Для кожної — скільки тиків до першого сліду чи стіни; обирає кермо, за яким
/// вільніше. Кожна траєкторія — десятки кроків по растру ядра (<see cref="CurveCore.HitBy"/>), тож думка — десятки
/// мікросекунд, і думає бот не щотика, а з реакцією свого рівня.
///
/// Рівні — це реакція, далекозорість і неуважність, а не інша фізика:
/// легкий думає раз на 200 мс, бачить на ~35 од уперед, частенько «задивляється» і не помічає чужих голів —
/// у тісняві врізається сам; звичайний — 120 мс і ~65 од; сильний — 80 мс, ~100 од, обминає голови суперників
/// і підрізає: вискакує поперед носа того, хто поруч, щоб лишити слід упоперек його дороги.
/// </summary>
public sealed class CurveBot
{
    readonly int _every;
    readonly int _look;
    readonly double _slip;
    readonly bool _heads;
    readonly bool _cut;
    readonly bool _inv;
    int _next;
    /// <summary>Кермо, яке бот зараз «тримає» — уже з поправкою на 🔄 (те, куди насправді має повернути).</summary>
    int _eff;
    /// <summary>Блукання на вільному полі: куди й скільки ще тиків звертати, щоб бот не їздив лише по лінійці.</summary>
    int _wander, _wanderLeft;

    /// <summary>Скільки тиків тримати поворот у пробних траєкторіях; останнє — «до кінця».</summary>
    static readonly int[] Holds = [4, 10, 20, int.MaxValue];

    public CurveBot(LiveBots.Level level, int phase = 0)
    {
        (_every, _look, _slip) = level switch
        {
            LiveBots.Level.Easy => (5, 22, 0.14),
            LiveBots.Level.Hard => (2, 62, 0.01),
            _ => (3, 40, 0.04),
        };
        _heads = level != LiveBots.Level.Easy;
        _cut = level == LiveBots.Level.Hard;
        // Легкий про 🔄 «забуває»: кермо навпаки збиває його так само, як новачка.
        _inv = level != LiveBots.Level.Easy;
        _next = phase;
    }

    /// <summary>Чи час думати цього тика (реакція рівня; різні боти — у різні тики).</summary>
    public bool Due(int t)
    {
        // Лічильник тиків раунду з новим раундом починається з нуля — тоді «далеко до наступної думки» означає «вже».
        if (t < _next && _next - t <= _every) return false;
        _next = t + _every;
        return true;
    }

    /// <summary>Що натиснути: -1 ліворуч, 0 прямо, 1 праворуч — рівно те, що людина шле в <c>turn</c>.</summary>
    public int Think(CurveCore c, int seat, Random rng)
    {
        var h = c.Heads[seat];
        if (!h.Present || !h.Alive) return 0;
        var raw = RawOf(h, _eff);
        // «Задивився»: тримає те саме кермо, що й тримав, хоч би що там попереду.
        if (rng.NextDouble() < _slip) return raw;

        Span<int> free = stackalloc int[3];
        for (var e = -1; e <= 1; e++)
        {
            var best = 0;
            if (e == 0) best = Free(c, seat, 0, 0);
            else
                foreach (var hold in Holds)
                {
                    best = Math.Max(best, Free(c, seat, e, hold));
                    if (best >= _look) break;
                }
            free[e + 1] = best;
        }

        // Туди, де вільніше; рівні шанси вирішує звичка — що вже тримаємо (без смикання кермом туди-сюди).
        var pick = _eff;
        for (var e = -1; e <= 1; e++)
            if (free[e + 1] > free[pick + 1]) pick = e;
        if (free[pick + 1] >= _look)
        {
            // Попереду чисто — або підрізаємо, або трохи блукаємо, або їдемо прямо; лише якщо й там чисто.
            var want = _cut && Cut(c, seat) is { } cut ? cut : Wander(rng);
            if (free[want + 1] >= _look) pick = want;
        }
        else _wanderLeft = 0;
        _eff = pick;
        return RawOf(h, pick);
    }

    /// <summary>Куди натиснути, щоб повернути в бік <paramref name="eff"/>: під 🔄 кермо навпаки (якщо рівень це помічає).</summary>
    int RawOf(CurveHead h, int eff) => _inv && h.Inv > 0 ? -eff : eff;

    int Wander(Random rng)
    {
        if (_wanderLeft > 0)
        {
            _wanderLeft--;
            return _wander;
        }
        // Нечасто, на пів секунди–секунду: плавні дуги, а не смикання.
        if (rng.NextDouble() < 0.18)
        {
            _wander = rng.Next(2) == 0 ? -1 : 1;
            _wanderLeft = rng.Next(2, 6);
            return _wander;
        }
        return 0;
    }

    /// <summary>
    /// Підрізати: найближчий живий суперник за ~70 од, точка на ~25 од поперед його носа. Якщо ми до неї ближчі,
    /// ніж він, — кермо туди: наш слід ляже впоперек його дороги.
    /// </summary>
    int? Cut(CurveCore c, int seat)
    {
        var me = c.Heads[seat];
        var bestD = 70.0 * 70.0;
        CurveHead? prey = null;
        for (var s = 0; s < CurveCore.Seats; s++)
        {
            var o = c.Heads[s];
            if (s == seat || !o.Present || !o.Alive) continue;
            var d = Sq(o.X - me.X, o.Y - me.Y);
            if (d < bestD) { bestD = d; prey = o; }
        }
        if (prey is null) return null;
        const double lead = 25;
        var (px, py) = (prey.X + Math.Cos(prey.A) * lead, prey.Y + Math.Sin(prey.A) * lead);
        if (Sq(px - me.X, py - me.Y) >= lead * lead * 1.4) return null;
        var want = Math.Atan2(py - me.Y, px - me.X) - me.A;
        want = Math.IEEERemainder(want, 2 * Math.PI);
        return Math.Abs(want) < 0.12 ? 0 : Math.Sign(want);
    }

    static double Sq(double x, double y) => x * x + y * y;

    /// <summary>
    /// Скільки тиків кривуля проживе, якщо <paramref name="hold"/> тиків тримати поворот <paramref name="eff"/>,
    /// а далі їхати прямо. Та сама кінематика, що в <see cref="CurveCore.Step"/>; чужі голови (крім легкого) —
    /// як точки, що їдуть прямо своїм курсом.
    /// </summary>
    int Free(CurveCore c, int seat, int eff, int hold)
    {
        var h = c.Heads[seat];
        var (x, y, a) = (h.X, h.Y, h.A);
        var sp = CurveCore.SpeedOf(h);
        var through = c.Wrap || h.Through > 0;
        for (var i = 1; i <= _look; i++)
        {
            if (i <= hold) a += eff * CurveCore.TurnStep;
            x += Math.Cos(a) * sp;
            y += Math.Sin(a) * sp;
            if (through)
            {
                if (x < 0) x += c.W; else if (x >= c.W) x -= c.W;
                if (y < 0) y += c.H; else if (y >= c.H) y -= c.H;
            }
            else if (c.Wall(x, y)) return i - 1;
            if (c.HitBy(x, y, a) != 0) return i - 1;
            if (_heads && i <= 25 && NearHead(c, seat, x, y, i)) return i - 1;
        }
        return _look;
    }

    /// <summary>Чи буде на кроці <paramref name="i"/> поруч чужа голова (вона — прямо своїм курсом).</summary>
    static bool NearHead(CurveCore c, int seat, double x, double y, int i)
    {
        for (var s = 0; s < CurveCore.Seats; s++)
        {
            var o = c.Heads[s];
            if (s == seat || !o.Present || !o.Alive) continue;
            var osp = CurveCore.SpeedOf(o) * i;
            if (Sq(o.X + Math.Cos(o.A) * osp - x, o.Y + Math.Sin(o.A) * osp - y) < 36) return true;
        }
        return false;
    }
}
