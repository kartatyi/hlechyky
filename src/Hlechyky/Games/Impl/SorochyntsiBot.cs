using static Hlechyky.Games.Impl.SorochyntsiCards;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Один крок Глека: <c>take</c> (Got — набір на 5 товарів), <c>reserve</c> (Card з ряду або Tier 1–3 наосліп), <c>buy</c>
/// (Card), <c>pass</c>, <c>discard</c> (Give — на 6 жетонів), <c>guest</c> (Guest).
/// </summary>
public sealed record SoMove(string Act, int[]? Got = null, int Card = -1, int Tier = 0, int[]? Give = null, int Guest = -1)
{
    /// <summary>Товари «взяти» назвами, як їх шле клієнт.</summary>
    public string[] Goods() => Got is null ? [] : [.. Enumerable.Range(0, 5).SelectMany(g => Enumerable.Repeat(SorochyntsiCards.Goods[g], Got[g]))];
}

/// <summary>
/// Глек-гравець «Сорочинського ярмарку» (контракт §8.1). Бачить лише те, що бачив би гравець на цьому місці: ряди, банк,
/// кількість карт у колодах, відкриті прилавки суперників і свою бронь — ні порядку колод, ні чужої броні наосліп не
/// читає. Детермінований (рівність — менший id, раніший товар, раніший рівень) і ніколи не ходить незаконно: кожен
/// кандидат — з <see cref="SorochyntsiCore.Legal"/> / <see cref="SorochyntsiCore.TakeOptions"/>, тих самих, що й у виді.
/// </summary>
public static class SorochyntsiBot
{
    /// <summary>Скільки кроків щонайбільше в одному ході (дія, кілька повернень, гість) — запобіжник від зациклення.</summary>
    const int StepCap = 8;

    /// <summary>
    /// Один повний хід місця <paramref name="s"/>: дія (якщо ще не зроблена), повернення зайвого, вибір гостя. Той самий
    /// метод доробляє хід за того, хто проспав строк. Повертає, скільки кроків зроблено.
    /// </summary>
    public static int Step(SorochyntsiCore c, int s)
    {
        var steps = 0;
        var round = c.Turns;
        while (!c.Over && c.Turn == s && c.Turns == round && steps < StepCap)
        {
            var err = Apply(c, s, Decide(c, s));
            // Хибний крок (не мав би статись) — рятівний законний, щоб стіл не завис.
            if (err is not null) Apply(c, s, Rescue(c, s));
            steps++;
        }
        return steps;
    }

    /// <summary>Застосувати крок до ядра (через ті самі методи, що й дії людей).</summary>
    public static string? Apply(SorochyntsiCore c, int s, SoMove m) => m.Act switch
    {
        "take" => c.Take(s, m.Goods()),
        "reserve" => m.Card >= 0 ? c.Reserve(s, m.Card, null) : c.Reserve(s, null, m.Tier),
        "buy" => c.Buy(s, m.Card),
        "discard" => c.Discard(s, [.. Enumerable.Range(0, 6).Where(i => m.Give![i] > 0).Select(i => new KeyValuePair<string, int>(Tokens[i], m.Give![i]))]),
        "guest" => c.Guest(s, m.Guest),
        _ => c.Pass(s),
    };

    /// <summary>Що зробив би Глек на місці <paramref name="s"/> у поточній фазі (для ходу Глека й для «💡 Глек радить»).</summary>
    public static SoMove Decide(SorochyntsiCore c, int s) => c.Phase switch
    {
        SoPhase.Discard => new SoMove("discard", Give: Give(c, s)),
        SoPhase.Guest => new SoMove("guest", Guest: PickGuest(c, s)),
        _ => Act(c, s),
    };

    // =====================================================================================
    // Оцінка (§8.1)
    // =====================================================================================

    sealed class Eval
    {
        public int Id;
        public double Val, Score;
        public int Turns;
        public int[] Gap = new int[5];
        public int[] Req = new int[5];
        public bool InRow;
    }

    /// <summary>Публічна картина й оцінки всіх видимих крамниць (12 у рядах + своя бронь), від найкращої за score.</summary>
    sealed class Look
    {
        public readonly List<Eval> All = [];
        public Eval? T, T2;
    }

    static Look See(SorochyntsiCore c, int s)
    {
        var b = c.Bonus[s];
        var t = c.Tok[s];
        var mine = c.Cards[s].Count;
        var pts = c.Points(s);
        var late = pts >= c.Target - 6;
        var wP = mine < 6 ? 0.7 : late ? 1.5 : 1.0;
        // Скільки ще треба кожного товару крамницям у рядах (для ваги знижки bw).
        var rowNeed = new int[5];
        for (var tr = 0; tr < 3; tr++)
            for (var slot = 0; slot < Row; slot++)
            {
                var id = c.Rows[tr][slot];
                if (id < 0) continue;
                for (var g = 0; g < 5; g++) rowNeed[g] += Math.Max(0, Deck[id].Cost[g] - b[g]);
            }
        var look = new Look();
        foreach (var id in c.Visible(s))
        {
            var x = Deck[id];
            var e = new Eval { Id = id, InRow = c.InRows(id) is not null };
            var gap = 0;
            for (var g = 0; g < 5; g++)
            {
                e.Req[g] = Math.Max(0, x.Cost[g] - b[g]);
                e.Gap[g] = Math.Max(0, e.Req[g] - t[g]);
                gap += e.Gap[g];
            }
            e.Turns = (Math.Max(0, gap - t[Gold]) + 2) / 3;
            var v = x.Pts * wP;
            if (x.Bonus != Fern)
            {
                var g = x.Bonus;
                var bw = 0.3 + 0.15 * rowNeed[g] / 12.0;
                if (mine < 6) bw *= 1.5;
                if (b[g] == 0) bw += late ? 2 : 1;
                v += bw;
                foreach (var gu in c.Table)
                {
                    if (c.GuestBy[gu] >= 0) continue;
                    var need = Guests[gu].Need;
                    if (need[g] == 0 || b[g] >= need[g]) continue;
                    v += 0.6 * (1 - SorochyntsiCore.GuestLeft(b, gu) / 8.0);
                }
            }
            else v += b[Fern] == 0 ? 4 + (late ? 2 : 0) : 0.2;
            if (x.K > 0) v += WinsStarshyna(c, s, c.Icons[s] + 1) ? 3 : 0.3;
            e.Val = v;
            e.Score = v / (1 + e.Turns);
            look.All.Add(e);
        }
        // Найкраща за score; рівність — менший id.
        var sorted = look.All.OrderByDescending(e => e.Score).ThenBy(e => e.Id).ToList();
        look.T = sorted.Count > 0 ? sorted[0] : null;
        look.T2 = sorted.Count > 1 ? sorted[1] : null;
        return look;
    }

    /// <summary>Чи стане старшина місця <paramref name="s"/> з <paramref name="icons"/> ⚔ (§2.7; уже його — ні).</summary>
    static bool WinsStarshyna(SorochyntsiCore c, int s, int icons) =>
        c.Starshyna != s && icons >= StarshynaIcons && (c.Starshyna < 0 || icons > c.Icons[c.Starshyna]);

    /// <summary>
    /// Чи дасть купівля <paramref name="id"/> булаву місцю <paramref name="s"/> (з гостем, що прийде, і старшиною) — за
    /// відкритими знижками й очками.
    /// </summary>
    static bool GivesBulava(SorochyntsiCore c, int s, int id)
    {
        if (c.Bulava >= 0) return false;
        var x = Deck[id];
        var b = (int[])c.Bonus[s].Clone();
        b[x.Bonus]++;
        var pts = c.Points(s) + x.Pts;
        if (x.K > 0 && WinsStarshyna(c, s, c.Icons[s] + 1)) pts += StarshynaPoints;
        if (c.Table.Any(g => c.GuestBy[g] < 0 && SorochyntsiCore.Covers(b, g))) pts += GuestPoints;
        return c.BulavaReady(pts, b);
    }

    // =====================================================================================
    // Хід (§8.1, перше застосовне)
    // =====================================================================================

    static SoMove Act(SorochyntsiCore c, int s)
    {
        var legal = c.Legal(s);
        var look = See(c, s);
        var t = c.Tok[s];
        var ntok = c.NTok(s);

        // 1. Купівля, що дає булаву, — з кількох більше ⭐, далі менший id.
        var win = legal.Buys.Where(q => GivesBulava(c, s, q.Id)).OrderByDescending(q => Deck[q.Id].Pts).ThenBy(q => q.Id).FirstOrDefault();
        if (win is not null) return new SoMove("buy", Card: win.Id);

        // 2. Найкраща доступна купівля за чистою цінністю.
        SoQuote? best = null, cheap = null;
        double bestNet = double.MinValue, cheapNet = double.MinValue;
        foreach (var q in legal.Buys)
        {
            var e = look.All.First(x => x.Id == q.Id);
            var paid = q.Pay[0] + q.Pay[1] + q.Pay[2] + q.Pay[3] + q.Pay[4] + 1.5 * q.Pay[Gold];
            var net = e.Val - 0.35 * paid;
            if (net > bestNet || (net == bestNet && q.Id < best!.Id)) { best = q; bestNet = net; }
            if (e.Val >= 0.5 && (net > cheapNet || (net == cheapNet && q.Id < cheap!.Id))) { cheap = q; cheapNet = net; }
        }
        if (best is not null && bestNet >= 1.0) return new SoMove("buy", Card: best.Id);
        if (ntok >= 8 && cheap is not null) return new SoMove("buy", Card: cheap.Id);

        // 3. Бронь-захист: суперник наступним ходом купує з ряду крамницю, що дасть йому булаву.
        if (legal.Reserve && c.Bulava < 0)
        {
            foreach (var o in c.Order)
            {
                if (o == s) continue;
                for (var tr = 0; tr < 3; tr++)
                    for (var slot = 0; slot < Row; slot++)
                    {
                        var id = c.Rows[tr][slot];
                        if (id >= 0 && c.Quote(o, id).Ok && GivesBulava(c, o, id)) return new SoMove("reserve", Card: id);
                    }
            }
        }

        // 4. Бронь-ціль: дорога ціль II–III рівня в ряду, поки є дукат.
        if (look.T is { InRow: true } T && legal.Reserve && Deck[T.Id].Tier >= 2 && c.Bank[Gold] > 0
            && T.Gap.Sum() - t[Gold] >= 4)
            return new SoMove("reserve", Card: T.Id);

        // 5. Взяти: найкорисніше для T і T2, без зайвого понад 10.
        if (legal.Take)
        {
            int[]? pick = null;
            var pickScore = int.MinValue;
            var pickPile = -1;
            foreach (var got in c.TakeOptions())
            {
                var n = got.Sum();
                var sc = -2 * Math.Max(0, ntok + n - HandMax);
                var pile = 0;
                for (var g = 0; g < 5; g++)
                {
                    if (got[g] == 0) continue;
                    if (look.T is { } a) sc += 3 * Math.Min(got[g], a.Gap[g]);
                    if (look.T2 is { } b2) sc += Math.Min(got[g], b2.Gap[g]);
                    pile += c.Bank[g];
                }
                if (sc > pickScore || (sc == pickScore && pile > pickPile))
                {
                    pick = got;
                    pickScore = sc;
                    pickPile = pile;
                }
            }
            if (pick is not null) return new SoMove("take", Got: pick);
        }

        // 6. Бронь найкращої з ряду або наосліп з колоди III (цвіту не видно ні в мене, ні в рядах III).
        var fernSeen = c.Bonus[s][Fern] > 0 || c.Reserved[s].Any(r => Deck[r.Id].Bonus == Fern)
            || c.Rows[2].Any(id => id >= 0 && Deck[id].Bonus == Fern);
        if (!fernSeen && legal.Deck[2]) return new SoMove("reserve", Tier: 3);
        if (legal.Reserve)
        {
            var row = look.All.Where(e => e.InRow).OrderByDescending(e => e.Score).ThenBy(e => e.Id).First();
            return new SoMove("reserve", Card: row.Id);
        }
        for (var tr = 2; tr >= 0; tr--)
            if (legal.Deck[tr]) return new SoMove("reserve", Tier: tr + 1);

        // 7. Пас — лише законний; інакше будь-що законне (не мало б статись: купівлі з п. 2 нижче порогу, а взяти й бронювати нема як).
        if (legal.Pass) return new SoMove("pass");
        return Rescue(c, s);
    }

    /// <summary>Будь-який законний крок поточної фази: купити, взяти, забронювати, пас; повернути перші жетони; перший гість.</summary>
    public static SoMove Rescue(SorochyntsiCore c, int s)
    {
        if (c.Phase == SoPhase.Discard)
        {
            var give = new int[6];
            var left = c.DiscardN;
            for (var i = 0; i < 6 && left > 0; i++)
            {
                var n = Math.Min(left, c.Tok[s][i]);
                give[i] = n;
                left -= n;
            }
            return new SoMove("discard", Give: give);
        }
        if (c.Phase == SoPhase.Guest) return new SoMove("guest", Guest: c.GuestChoice.Min());
        var legal = c.Legal(s);
        if (legal.Buys.Count > 0) return new SoMove("buy", Card: legal.Buys.MinBy(q => q.Id)!.Id);
        if (legal.Take) return new SoMove("take", Got: c.TakeOptions()[0]);
        if (legal.Reserve) return new SoMove("reserve", Card: c.Visible(s).First(id => c.InRows(id) is not null));
        for (var tr = 0; tr < 3; tr++)
            if (legal.Deck[tr]) return new SoMove("reserve", Tier: tr + 1);
        return new SoMove("pass");
    }

    // =====================================================================================
    // Повернути зайве й гість
    // =====================================================================================

    /// <summary>
    /// По одному жетону з найменшою цінністю 3·need(T) + need(T2) серед тих, що понад потребу цілі T; нема таких —
    /// найменш цінний товар; дукати — останніми. Рівність — раніший товар.
    /// </summary>
    static int[] Give(SorochyntsiCore c, int s)
    {
        var look = See(c, s);
        var t = (int[])c.Tok[s].Clone();
        var give = new int[6];
        int Need(Eval? e, int g) => e is null ? 0 : e.Req[g];
        for (var k = 0; k < c.DiscardN; k++)
        {
            var pick = -1;
            var pickVal = int.MaxValue;
            for (var pass = 0; pass < 2 && pick < 0; pass++)
                for (var g = 0; g < 5; g++)
                {
                    if (t[g] == 0) continue;
                    if (pass == 0 && t[g] <= Need(look.T, g)) continue;
                    var val = 3 * Need(look.T, g) + Need(look.T2, g);
                    if (val < pickVal) { pick = g; pickVal = val; }
                }
            if (pick < 0) pick = Gold;
            t[pick]--;
            give[pick]++;
        }
        return give;
    }

    /// <summary>Гість, до якого найближчий суперник (найменший його залишок); рівність — менший id.</summary>
    static int PickGuest(SorochyntsiCore c, int s)
    {
        int Closest(int g)
        {
            var best = int.MaxValue;
            foreach (var o in c.Order)
                if (o != s) best = Math.Min(best, SorochyntsiCore.GuestLeft(c.Bonus[o], g));
            return best;
        }
        return c.GuestChoice.OrderBy(Closest).ThenBy(g => g).First();
    }
}
