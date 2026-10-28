using static Hlechyky.Games.Impl.SkarbCards;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Глек «Скарбу Полуботка» за обидві ролі (контракт §8.1). Бачить лише те, що бачив би гравець на цьому місці: публічне
/// поле й події, свою руку, роль, свої <c>peeks</c> і (для крота з опцією «знають своїх») інших кротів. Чужих рук, ролей і
/// положення скрині не читає — єдиний виняток, прописаний у контракті: кріт не кладе хід, після якого відкриється скриня
/// (<see cref="SkarbCore.WouldOpenGold"/> на позиченому полі). Підказка людині (<c>oracle: false</c>) і цього не знає.
/// <para>
/// Пам'ять — підозра кожного місця до кожного (s ∈ [0,02; 0,98]) і хто скільки «копав» цього раунду; ведеться для всіх
/// місць, людських теж (для «💡 Глек радить»). Новий екземпляр — нова партія. Кожен кандидат — з <see cref="SkarbCore.Legal"/>.
/// </para>
/// </summary>
public sealed class SkarbBot
{
    const int Max = SkarbCore.MaxSeats;
    const double Lo = 0.02, Hi = 0.98;
    static readonly double[] Prior = [0.3, 0.4, 0.3];

    readonly double[,] _sus = new double[Max, Max];
    /// <summary>Кріт із «знають своїх» знає інших кротів точно — на них нічого не рахує.</summary>
    readonly bool[,] _ally = new bool[Max, Max];
    /// <summary>Скільки ходів, що наблизили фронт до цілі спостерігача, зробило місце цього раунду.</summary>
    readonly int[,] _digs = new int[Max, Max];
    long _heard;

    public double Suspicion(int observer, int target) => _sus[observer, target];
    public int Digs(int observer, int target) => _digs[observer, target];

    // =====================================================================================
    // Спостереження (лише публічні події)
    // =====================================================================================

    /// <summary>Пройти нові події ядра (seq більший за почутий) і оновити підозри.</summary>
    public void Observe(SkarbCore c)
    {
        foreach (var e in c.Events)
        {
            if (e.Seq <= _heard) continue;
            _heard = e.Seq;
            Hear(c, e);
        }
    }

    void Hear(SkarbCore c, SkEvent e)
    {
        switch (e.K)
        {
            case "round":
                {
                    var s0 = c.Seats > 0 ? (double)c.MolesInDeck / c.Seats : 0.2;
                    for (var o = 0; o < Max; o++)
                        for (var t = 0; t < Max; t++)
                        {
                            _sus[o, t] = s0;
                            _digs[o, t] = 0;
                            _ally[o, t] = c.Allies && c.In[o] && c.In[t] && o != t && c.Mole[o] && c.Mole[t];
                            if (_ally[o, t]) _sus[o, t] = 1;
                        }
                    break;
                }
            case "place" when e.D0 is { } d0 && e.D1 is { } d1:
                foreach (var o in c.Order)
                {
                    if (o == e.Seat || _ally[o, e.Seat]) continue;
                    if (e.Dead) { Add(o, e.Seat, 0.25); continue; }
                    var t = Target(c, o);
                    if (d0[t] >= 99 || d1[t] >= 99) continue;
                    var delta = d1[t] - d0[t];
                    if (delta > 0) Add(o, e.Seat, 0.10);
                    else if (delta < 0)
                    {
                        Add(o, e.Seat, -0.10);
                        _digs[o, e.Seat]++;
                    }
                }
                break;
            case "rockfall" when e.D0 is { } d0 && e.D1 is { } d1:
                foreach (var o in c.Order)
                {
                    if (o == e.Seat || _ally[o, e.Seat]) continue;
                    var t = Target(c, o);
                    if (e.Dead) Add(o, e.Seat, -0.20);
                    else if (d0[t] < 99 && d1[t] > d0[t]) Add(o, e.Seat, 0.30);
                }
                break;
            case "break" when e.Target >= 0:
                foreach (var o in c.Order)
                {
                    if (o == e.Seat || _ally[o, e.Seat]) continue;
                    if (o == e.Target) { if (!c.Mole[o]) Add(o, e.Seat, 0.20); continue; }
                    if (_sus[o, e.Target] < 0.3) Add(o, e.Seat, 0.20);
                    else if (_sus[o, e.Target] > 0.6) Add(o, e.Seat, -0.15);
                }
                break;
            case "fix" when e.Target >= 0 && e.Target != e.Seat:
                foreach (var o in c.Order)
                {
                    if (o == e.Seat || _ally[o, e.Seat]) continue;
                    if (o == e.Target || _sus[o, e.Target] < 0.3) Add(o, e.Seat, -0.10);
                    else if (_sus[o, e.Target] > 0.6) Add(o, e.Seat, 0.15);
                }
                break;
        }
    }

    void Add(int o, int t, double d) => _sus[o, t] = Math.Clamp(_sus[o, t] + d, Lo, Hi);

    // =====================================================================================
    // Віра про скриню
    // =====================================================================================

    /// <summary>Імовірність скрині під кожною міткою для місця <paramref name="o"/> (з його мап і відкритих каменів).</summary>
    public static double[] Belief(SkarbCore c, int o)
    {
        var p = new double[3];
        for (var g = 0; g < 3; g++)
            if (c.Peek[o][g] == 2) { p[g] = 1; return p; }
        var stone = new bool[3];
        var any = false;
        for (var g = 0; g < 3; g++)
        {
            stone[g] = c.Peek[o][g] == 1 || c.GoalOpen[g];
            any |= stone[g];
        }
        if (!any) return (double[])Prior.Clone();
        var left = stone.Count(x => !x);
        for (var g = 0; g < 3; g++) p[g] = stone[g] || left == 0 ? 0 : 1.0 / left;
        return p;
    }

    /// <summary>Ціль: мітка з найбільшою вірою (рівність — менший індекс).</summary>
    public static int Target(SkarbCore c, int o)
    {
        var p = Belief(c, o);
        var t = 0;
        for (var g = 1; g < 3; g++) if (p[g] > p[t]) t = g;
        return t;
    }

    static bool GoldKnown(SkarbCore c, int o) => Belief(c, o).Any(x => x >= 0.999);

    // =====================================================================================
    // Оцінка ходу (O(1) на кандидата: лише краї нової карти)
    // =====================================================================================

    readonly record struct Eval(SkMove Mv, int Cell, bool Through, int D0, int D1, int D2, int Exits, int Boundary, int FrontAfter)
    {
        public int D(int g) => g == 0 ? D0 : g == 1 ? D1 : D2;
    }

    static Eval Evaluate(SkarbCore c, SkFront f, SkMove mv)
    {
        var sh = SkarbCore.CardOf(mv.Card).Shape!;
        var m = Mask(sh, mv.R);
        var cell = Cell(mv.X, mv.Y);
        Span<int> d = [f.BestCell[0] == cell ? f.Second[0] : f.Dist[0], f.BestCell[1] == cell ? f.Second[1] : f.Dist[1],
            f.BestCell[2] == cell ? f.Second[2] : f.Dist[2]];
        int exits = 0, boundary = 0;
        for (var e = 0; e < 4; e++)
        {
            if ((m >> e & 1) == 0) continue;
            var nb = Step(cell, e);
            if (nb < 0) { boundary++; continue; }
            if (!sh.C || c.CellM[nb] >= 0) continue;
            int nx = CellX(nb), ny = CellY(nb);
            if (IsShaft(nx, ny)) continue;
            exits++;
            for (var g = 0; g < 3; g++) d[g] = Math.Min(d[g], SkarbCore.Distance(nb, g));
        }
        var i = Array.BinarySearch(f.Cells, cell);
        var into = i >= 0 ? f.Into[i] : 0;
        return new Eval(mv, cell, sh.C, d[0], d[1], d[2], exits, boundary, f.Edges - into + exits);
    }

    static double Score(Eval ev, int delta) => -delta * 3 + ev.FrontAfter * 0.5 - ev.Boundary * 0.5;

    /// <summary>Найкращий за оцінкою (перший при рівності — порядок Legal сталий).</summary>
    static T? Best<T>(IEnumerable<T> items, Func<T, double> score) where T : struct
    {
        T? best = null;
        var top = double.NegativeInfinity;
        foreach (var x in items)
        {
            var v = score(x);
            if (v > top) { top = v; best = x; }
        }
        return best;
    }

    // =====================================================================================
    // Рішення
    // =====================================================================================

    /// <summary>
    /// Один хід Глека за місце <paramref name="s"/> (рівно одна дія або пас). <paramref name="oracle"/> = false — для
    /// підказки людині: кріт уникає ходів, що досягли б будь-якої мітки, де, на його думку, може бути скриня.
    /// </summary>
    public SkMove Decide(SkarbCore c, int s, Random rng, bool oracle = true)
    {
        var legal = c.Legal(s);
        if (legal.Count > 0)
        {
            var mv = c.Mole[s] ? MoleMove(c, s, rng, legal, oracle) : DiggerMove(c, s, legal);
            if (mv is { } got) return got;
        }
        return new SkMove(WorstCard(c, s, Suspects(c, s)), Pass: true);
    }

    bool Suspects(SkarbCore c, int s) => c.Order.Any(t => t != s && !_ally[s, t] && _sus[s, t] >= 0.6);

    SkMove? DiggerMove(SkarbCore c, int s, List<SkMove> legal)
    {
        var f = c.Front();
        var t = Target(c, s);
        var d0 = f.Dist[t];
        var kinds = legal.Select(m => SkarbCore.CardOf(m.Card).K).ToArray();
        IEnumerable<SkMove> Of(SkKind k) => legal.Where((m, i) => kinds[i] == k);

        // 1. Сам зламаний і має ремонт — полагодити себе.
        if (c.Broken[s] != 0)
            foreach (var m in Of(SkKind.Fix))
                if (m.Seat == s) return m;

        var paths = Of(SkKind.Path).Select(m => Evaluate(c, f, m)).Where(e => e.Through).ToList();
        // 2. Хід, що зменшує відстань до цілі, — найкращий.
        if (Best(paths.Where(e => e.D(t) < d0), e => Score(e, e.D(t) - d0)) is { } good) return good.Mv;

        // 3. Скриня невідома, фронт близько (або ходу нема) — мапа на найближчу невідому мітку.
        if (!GoldKnown(c, s))
        {
            var unknown = Enumerable.Range(0, 3).Where(g => !c.GoalOpen[g] && c.Peek[s][g] == 0).ToArray();
            var noPath = !paths.Any(e => e.D(t) <= d0);
            if (unknown.Length > 0 && (unknown.Any(g => f.Dist[g] < 4) || noPath))
            {
                var maps = Of(SkKind.Map).Where(m => unknown.Contains(m.Goal)).ToList();
                if (maps.Count > 0) return maps.MinBy(m => f.Dist[m.Goal] * 4 + m.Goal);
            }
        }

        // 4. Довірений зламаний — полагодити того, хто більше копав.
        if (Best(Of(SkKind.Fix).Where(m => m.Seat != s && _sus[s, m.Seat] < 0.3), m => _digs[s, m.Seat] - m.Seat * 0.01) is { } help)
            return help;

        // 5. Підозрілий із цілим інструментом — зламати найпідозрілішому.
        if (Best(Of(SkKind.Break).Where(m => _sus[s, m.Seat] >= 0.6), m => _sus[s, m.Seat]) is { } hit) return hit;

        // 6. Глуха карта перекрила фронт на шляху до цілі — завалити її.
        var r = c.Reach();
        if (Best(Of(SkKind.Rockfall).Where(m =>
            {
                var cell = Cell(m.X, m.Y);
                return !c.CellC[cell] && r[cell] != 0 && SkarbCore.Distance(cell, t) < d0;
            }), m => -SkarbCore.Distance(Cell(m.X, m.Y), t)) is { } clear)
            return clear;

        // 7. Хід, що не гіршає відстань, — тримати фронт живим.
        if (Best(paths.Where(e => e.D(t) <= d0), e => Score(e, e.D(t) - d0)) is { } keep) return keep.Mv;
        return null;
    }

    SkMove? MoleMove(SkarbCore c, int s, Random rng, List<SkMove> legal, bool oracle)
    {
        var f = c.Front();
        var belief = Belief(c, s);
        var t = Target(c, s);
        var d = f.Dist[t];
        bool far = d > 4, adj = d <= 1;
        var possible = 0;
        for (var g = 0; g < 3; g++) if (belief[g] > 0) possible |= 1 << g;
        var kinds = legal.Select(m => SkarbCore.CardOf(m.Card).K).ToArray();
        IEnumerable<SkMove> Of(SkKind k) => legal.Where((m, i) => kinds[i] == k);
        bool Safe(SkMove m)
        {
            var sh = SkarbCore.CardOf(m.Card).Shape!;
            var mask = Mask(sh, m.R);
            return oracle ? !c.WouldOpenGold(m.X, m.Y, mask, sh.C) : (c.WouldReach(m.X, m.Y, mask, sh.C) & possible) == 0;
        }
        // 1. Ніколи не відкриває скриню сам.
        var paths = Of(SkKind.Path).Where(Safe).Select(m => Evaluate(c, f, m)).ToList();

        // 2. Сам зламаний і має ремонт — найчастіше лагодиться (копачі теж лагодяться).
        var self = Of(SkKind.Fix).Where(m => m.Seat == s).ToList();
        if (c.Broken[s] != 0 && self.Count > 0 && rng.NextDouble() < 0.6) return self[0];

        // 3. Впритул — завалити карту фронту, прибирання якої найбільше віддаляє скриню.
        if (adj)
        {
            var r = c.Reach();
            var rocks = Of(SkKind.Rockfall).Where(m =>
            {
                var cell = Cell(m.X, m.Y);
                return c.CellC[cell] && r[cell] != 0 && SkarbCore.Distance(cell, t) <= d + 2;
            }).ToList();
            if (Best(rocks.Select(m => (Mv: m, D: c.DistWithout(Cell(m.X, m.Y), t))), x => x.D) is { } rock && rock.D > d)
                return rock.Mv;
        }

        if (!far)
        {
            // 4. Близько — глуха на край фронту, найближчий до скрині; інакше злам найактивнішому копачеві.
            if (Best(paths.Where(e => !e.Through), e => -SkarbCore.Distance(e.Cell, t)) is { } plug) return plug.Mv;
            if (Best(Of(SkKind.Break).Where(m => !_ally[s, m.Seat] && c.Broken[m.Seat] == 0), m => _digs[s, m.Seat] - _sus[s, m.Seat])
                is { } stop)
                return stop;
            // Нічим заткнути — хоч завести вбік.
            if (Best(paths.Where(e => e.Through && e.D(t) > d), e => e.D(t) - d + e.Exits * 0.1) is { } away) return away.Mv;
        }
        else
        {
            // 5. Далеко: зрідка ховається під копача, інакше копає «вбік» або дивиться мапу.
            if (rng.NextDouble() < 0.35)
            {
                var known = belief[t] >= 0.999;
                var decoy = known
                    ? Enumerable.Range(0, 3).Where(g => g != t && !c.GoalOpen[g]).DefaultIfEmpty(-1).MinBy(g => g < 0 ? 99 : f.Dist[g])
                    : 1;
                if (decoy >= 0
                    && Best(paths.Where(e => e.Through && e.D(decoy) < f.Dist[decoy]), e => (e.D(t) - d) * 2 + Score(e, e.D(decoy) - f.Dist[decoy]))
                        is { } hide)
                    return hide.Mv;
            }
            else
            {
                var stone = Enumerable.Range(0, 3).FirstOrDefault(g => c.Peek[s][g] == 1 && !c.GoalOpen[g], -1);
                if (Best(paths.Where(e => e.Through && e.D(t) - d is 0 or 1),
                        e => (stone >= 0 ? -e.D(stone) * 2 : 0) + (e.D(t) - d) + e.Exits * 0.1) is { } aside)
                    return aside.Mv;
                if (!GoldKnown(c, s))
                {
                    var maps = Of(SkKind.Map).Where(m => c.Peek[s][m.Goal] == 0).ToList();
                    if (maps.Count > 0) return maps.MinBy(m => f.Dist[m.Goal] * 4 + m.Goal);
                }
            }
        }

        // 6. Полагодити свого; або зрідка — копача, для прикриття.
        foreach (var m in Of(SkKind.Fix))
            if (m.Seat != s && _ally[s, m.Seat]) return m;
        var cover = Of(SkKind.Fix).Where(m => m.Seat != s).ToList();
        if (far && cover.Count > 0 && rng.NextDouble() < 0.3) return cover[0];
        return null;
    }

    // =====================================================================================
    // Найгірша карта (пас і таймаут)
    // =====================================================================================

    /// <summary>Пас найменш корисною для ролі карти (§8 таймаут): <see cref="WorstCard"/>.</summary>
    public static SkMove Worst(SkarbCore c, int s) => new(WorstCard(c, s, false), Pass: true);

    /// <summary>
    /// Карта, яку бот цієї ролі скинув би (§8.1 «Пас»). Копач: глуха > злам, коли підозрюваних нема > зайва мапа (скриня
    /// відома) > завал > ремонт > найгірший хід > потрібна мапа. Кріт — навпаки: хороші наскрізні ходи й ремонти першими.
    /// </summary>
    public static int WorstCard(SkarbCore c, int s, bool suspects)
    {
        var hand = c.Hand[s];
        if (hand.Count == 0) return -1;
        var mole = c.Mole[s];
        var known = GoldKnown(c, s);
        var best = hand[0];
        var top = double.PositiveInfinity;
        foreach (var id in hand)
        {
            var card = SkarbCore.CardOf(id);
            double v = mole
                ? card.K switch
                {
                    SkKind.Path when card.Shape!.C => 4 - Edges(card.Shape.M),
                    SkKind.Fix => 3,
                    SkKind.Map => known ? 3.5 : 6,
                    SkKind.Break => 7,
                    SkKind.Rockfall => 8,
                    _ => 9,
                }
                : card.K switch
                {
                    SkKind.Path when !card.Shape!.C => 0,
                    SkKind.Break => suspects ? 6 : 1,
                    SkKind.Map => known ? 2 : 15,
                    SkKind.Rockfall => 3,
                    SkKind.Fix => 4,
                    _ => 10 + Edges(card.Shape!.M),
                };
            if (v < top) { top = v; best = id; }
        }
        return best;
    }
}
