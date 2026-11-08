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

/// <summary>Який Глек сидить за столом (опція <c>glek</c>, §3): хитрий (типово) чи простий (перший, жадібний).</summary>
public enum SoBotLevel { Smart, Easy }

/// <summary>
/// Глек-гравець «Сорочинського ярмарку» (контракт §8.1). Бачить лише те, що бачив би гравець на цьому місці: ряди, банк,
/// кількість карт у колодах, відкриті прилавки суперників і свою бронь — ні порядку колод, ні чужої броні наосліп не
/// читає. Детермінований (рівність — менший id, раніший товар, раніший рівень) і ніколи не ходить незаконно: кожен
/// кандидат — з <see cref="SorochyntsiCore.Legal"/> / <see cref="SorochyntsiCore.TakeOptions"/>, тих самих, що й у виді.
/// Два рівні: <see cref="SoBotLevel.Easy"/> — перший жадібний Глек як був; <see cref="SoBotLevel.Smart"/> — купує раніше,
/// бронює лише коли варто, знає останнє коло й бере булаву лише тоді, коли з нею виграє.
/// </summary>
public static class SorochyntsiBot
{
    /// <summary>Скільки кроків щонайбільше в одному ході (дія, кілька повернень, гість) — запобіжник від зациклення.</summary>
    const int StepCap = 8;

    /// <summary>
    /// Один повний хід місця <paramref name="s"/>: дія (якщо ще не зроблена), повернення зайвого, вибір гостя. Той самий
    /// метод доробляє хід за того, хто проспав строк. Повертає, скільки кроків зроблено.
    /// </summary>
    public static int Step(SorochyntsiCore c, int s, SoBotLevel level = SoBotLevel.Smart)
    {
        var steps = 0;
        var round = c.Turns;
        while (!c.Over && c.Turn == s && c.Turns == round && steps < StepCap)
        {
            var err = Apply(c, s, Decide(c, s, level));
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

    /// <summary>
    /// Що зробив би Глек рівня <paramref name="level"/> на місці <paramref name="s"/> у поточній фазі (хід Глека, доробити
    /// хід за того, хто проспав, і «💡 Глек радить» — він завжди хитрий).
    /// </summary>
    public static SoMove Decide(SorochyntsiCore c, int s, SoBotLevel level = SoBotLevel.Smart) => c.Phase switch
    {
        SoPhase.Discard => new SoMove("discard", Give: Give(c, s, level == SoBotLevel.Smart)),
        SoPhase.Guest => new SoMove("guest", Guest: PickGuest(c, s)),
        _ => level == SoBotLevel.Smart ? ActSmart(c, s) : ActEasy(c, s),
    };

    /// <summary>Рівень з опції столу <c>glek</c>: <c>easy</c> — простий, решта (і нема) — хитрий.</summary>
    public static SoBotLevel Level(string? option) => option == "easy" ? SoBotLevel.Easy : SoBotLevel.Smart;

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

    /// <summary>
    /// Оцінки видимих крамниць. Хитрий відрізняється двома речами: вага гостя не буває від'ємною (ділимо на всю вимогу
    /// гостя, 8 чи 9, а не на 8), і крамниця, після якої гість прийде одразу, важить ще й його 3 ⭐.
    /// </summary>
    static Look See(SorochyntsiCore c, int s, bool smart)
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
        var pending = GuestAfter(c, b, -1, -1);
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
            if (smart)
            {
                // Хитрий рахує й те, що одного товару за хід береш щонайбільше два (а частіше — один).
                var maxGap = e.Gap.Max();
                e.Turns = Math.Max(e.Turns, (Math.Max(0, maxGap - t[Gold]) + 1) / 2);
            }
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
                    var left = SorochyntsiCore.GuestLeft(b, gu);
                    v += smart ? GuestStep * (1 - (left - 1) / (double)need.Sum()) : 0.6 * (1 - left / 8.0);
                }
            }
            else if (smart && b[Fern] == 0 && mine < FernFrom && !late) v += FernEarly;
            else v += b[Fern] == 0 ? 4 + (late ? 2 : 0) : 0.2;
            if (x.K > 0) v += WinsStarshyna(c, s, c.Icons[s] + 1) ? 3 : 0.3;
            if (smart && pending < 0 && GuestAfter(c, b, x.Bonus, -1) >= 0) v += GuestPoints * wP;
            e.Val = v;
            e.Score = smart ? v / Math.Pow(1 + e.Turns, TurnPow) : v / (1 + e.Turns);
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

    /// <summary>
    /// Гість, що прийде до того, у кого знижки <paramref name="b"/> (+ одна знижка <paramref name="plus"/>, якщо ≥ 0), —
    /// перший такий на столі, крім <paramref name="skip"/>; -1 — нікого.
    /// </summary>
    static int GuestAfter(SorochyntsiCore c, int[] b, int plus, int skip)
    {
        foreach (var g in c.Table)
        {
            if (g == skip || c.GuestBy[g] >= 0) continue;
            var need = Guests[g].Need;
            var ok = true;
            for (var i = 0; i < 5 && ok; i++) ok = b[i] + (i == plus ? 1 : 0) >= need[i];
            if (ok) return g;
        }
        return -1;
    }

    // =====================================================================================
    // Простий Глек (опція glek = easy): перший жадібний алгоритм, без змін (§8.1)
    // =====================================================================================

    static SoMove ActEasy(SorochyntsiCore c, int s)
    {
        var legal = c.Legal(s);
        var look = See(c, s, smart: false);
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
        if (TakeFor(c, s, look) is { } pick) return new SoMove("take", Got: pick);

        // 6. Бронь найкращої з ряду або наосліп з колоди III (цвіту не видно ні в мене, ні в рядах III).
        if (Fallback(c, s, legal, look) is { } fb) return fb;

        // 7. Пас — лише законний; інакше будь-що законне (не мало б статись: купівлі з п. 2 нижче порогу, а взяти й бронювати нема як).
        if (legal.Pass) return new SoMove("pass");
        return Rescue(c, s);
    }

    /// <summary>
    /// Взяти: перебрати всі законні take і взяти той, що дає найбільше 3·Σ min(взято, gap(T)) + Σ min(взято, gap(T2)) −
    /// 2·(понад 10); рівність — купи з більшою кількістю. Нема що брати — null.
    /// </summary>
    static int[]? TakeFor(SorochyntsiCore c, int s, Look look)
    {
        var ntok = c.NTok(s);
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
        return pick;
    }

    /// <summary>Коли взяти нема чого: наосліп з колоди III (цвіту ніде не видно), бронь найкращої з ряду, будь-яка колода.</summary>
    static SoMove? Fallback(SorochyntsiCore c, int s, SoLegal legal, Look look)
    {
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
        return null;
    }

    // =====================================================================================
    // Хитрий Глек (опція glek = smart, типово; і «💡 Глек радить»)
    // =====================================================================================

    /// <summary>Поки в мене менше стільки крамниць, купую й дешевші за чистою цінністю: поріг <see cref="EarlyBuy"/> замість 1.</summary>
    const int EarlyShops = 6;
    const double EarlyBuy = 0.5;
    /// <summary>Бронь-ціль (лише коли ціль от-от купить суперник) лишає вільним щонайменше одне місце броні.</summary>
    const int ReserveKeep = 2;
    /// <summary>
    /// Булава без певного виграшу: до кола <see cref="SureUntil"/> — ні (будую далі); з нього — так, хіба що програю й без
    /// жодної відповіді суперників; з кола <see cref="ForceFrom"/> — так завжди (щоб партія не тяглась до межі кіл). Обидва —
    /// від межі кіл столу (опція rounds): при 40 — кола 27 і 31, як і було.
    /// </summary>
    static int SureUntil(SorochyntsiCore c) => c.RoundCap - 13;
    static int ForceFrom(SorochyntsiCore c) => c.RoundCap - 9;
    /// <summary>Вага кроку до гостя: найближчий (бракує однієї знижки) — уся, перший крок — 1/8 чи 1/9.</summary>
    const double GuestStep = 0.6;
    /// <summary>Цвіт папороті, поки в мене менше <see cref="FernFrom"/> крамниць і ⭐ далеко до цілі, важить лише <see cref="FernEarly"/> (не +4).</summary>
    const int FernFrom = 8;
    const double FernEarly = 0.5;
    /// <summary>Ціль — найбільше val / (1 + ходів)^<see cref="TurnPow"/>: далека дорога крамниця не відбирає товари в близьких.</summary>
    const double TurnPow = 1.6;
    /// <summary>Шукати цвіт у колоді III бронею наосліп, коли до цілі ≤ стільки ⭐, а цвіту ніде не видно.</summary>
    const int FernDigPts = 6;

    static SoMove ActSmart(SorochyntsiCore c, int s)
    {
        var legal = c.Legal(s);
        var look = See(c, s, smart: true);
        var t = c.Tok[s];
        var ntok = c.NTok(s);
        var mine = c.Cards[s].Count;

        // Останнє коло: цей хід — мій останній, рахують лише ⭐ (і менше крамниць при рівності).
        if (c.Last) return LastTurn(c, s, legal, look);

        // 1. Булава — лише та купівля, після якої я виграю навіть проти найкращих відповідей тих, хто ще ходить у цьому
        //    колі (їхня найкраща купівля з відкритого + гість + старшина). Ні — такі купівлі відкладаю й будую далі; з кола
        //    SureUntil беру, якщо не програю й без їхніх відповідей, з ForceFrom — будь-яку (партія затяглась).
        var hold = new HashSet<int>();
        SoQuote? win = null;
        var winMargin = int.MinValue;
        foreach (var q in legal.Buys)
        {
            if (!GivesBulava(c, s, q.Id)) continue;
            hold.Add(q.Id);
            var m = Margin(c, s, q.Id, -1);
            if (m > winMargin || (m == winMargin && q.Id < win!.Id)) { win = q; winMargin = m; }
        }
        if (win is not null && (winMargin > 0 || c.Round >= ForceFrom(c)
            || (c.Round >= SureUntil(c) && Margin(c, s, win.Id, -1, respond: false) > 0)))
            return new SoMove("buy", Card: win.Id);

        // 2. Найкраща доступна купівля за чистою цінністю; поки крамниць мало — поріг нижчий.
        SoQuote? best = null, cheap = null;
        double bestNet = double.MinValue, cheapNet = double.MinValue;
        foreach (var q in legal.Buys)
        {
            if (hold.Contains(q.Id)) continue;
            var e = look.All.First(x => x.Id == q.Id);
            var paid = q.Pay[0] + q.Pay[1] + q.Pay[2] + q.Pay[3] + q.Pay[4] + 1.5 * q.Pay[Gold];
            var net = e.Val - 0.35 * paid;
            if (net > bestNet || (net == bestNet && q.Id < best!.Id)) { best = q; bestNet = net; }
            if (e.Val >= 0.5 && (net > cheapNet || (net == cheapNet && q.Id < cheap!.Id))) { cheap = q; cheapNet = net; }
        }
        if (best is not null && bestNet >= (mine < EarlyShops ? EarlyBuy : 1.0)) return new SoMove("buy", Card: best.Id);
        if (ntok >= 8 && cheap is not null) return new SoMove("buy", Card: cheap.Id);

        // 3. Бронь-захист: суперник наступним ходом бере булаву крамницею з ряду — і з нею мене обходить.
        if (legal.Reserve && c.Bulava < 0 && Threat(c, s) is { } danger) return new SoMove("reserve", Card: danger);

        // 4. Бронь-ціль — лише коли ціль от-от купить суперник (бракує йому ≤ 1), а мені до неї ще далеко. Бронь «про
        //    запас» на кожну дорогу ціль (як у простого) забирає ходи й дукати — у грі проти простого вона програє.
        if (look.T is { InRow: true } T && legal.Reserve && Deck[T.Id].Tier >= 2 && c.Bank[Gold] > 0
            && T.Gap.Sum() - t[Gold] >= 4 && c.Reserved[s].Count < ReserveKeep && Contested(c, s, T.Id))
            return new SoMove("reserve", Card: T.Id);

        // 4а. ⭐ уже близько, а цвіту папороті нема ні в мене, ні в ряду III, ні в броні — без нього булави не бачити:
        //     шукати його в колоді III (бронь наосліп). Без цього партії удвох часом стояли до 40 кіл: усі п'ять цвітів —
        //     у колоді, а дорогих крамниць III, що її гортають, ніхто не купує.
        if (c.Bonus[s][Fern] == 0 && legal.Deck[2] && c.Points(s) >= c.Target - FernDigPts
            && !c.Reserved[s].Any(r => Deck[r.Id].Bonus == Fern) && !c.Rows[2].Any(id => id >= 0 && Deck[id].Bonus == Fern))
            return new SoMove("reserve", Tier: 3);

        // 5. Взяти.
        if (TakeFor(c, s, look) is { } pick) return new SoMove("take", Got: pick);

        // 6. Бронь (наосліп III, найкраща з ряду, будь-яка колода).
        if (Fallback(c, s, legal, look) is { } fb) return fb;

        // 7. Нема що робити, крім відкладеної булави, — беру її: краще скінчити, ніж стояти.
        if (win is not null) return new SoMove("buy", Card: win.Id);
        if (legal.Pass) return new SoMove("pass");
        return Rescue(c, s);
    }

    /// <summary>
    /// Мій останній хід (останнє коло): з усіх купівель, броні з ряду (забрати в того, хто ще ходить, його найкращу
    /// крамницю) і «нічого» (взяти товари чи пас) — той, після якого мій запас проти найкращих відповідей найбільший.
    /// Купівля без ⭐ лише додає крамницю, а рівність розбиває МЕНШЕ крамниць — тож «нічого» краще за неї.
    /// </summary>
    static SoMove LastTurn(SorochyntsiCore c, int s, SoLegal legal, Look look)
    {
        var bestM = Margin(c, s, -1, -1);
        SoMove? best = null;
        foreach (var q in legal.Buys.OrderBy(q => q.Id))
        {
            var m = Margin(c, s, q.Id, -1);
            if (m > bestM) { bestM = m; best = new SoMove("buy", Card: q.Id); }
        }
        if (legal.Reserve)
            foreach (var id in look.All.Where(e => e.InRow).Select(e => e.Id).Order())
            {
                var m = Margin(c, s, -1, id);
                if (m > bestM) { bestM = m; best = new SoMove("reserve", Card: id); }
            }
        if (best is not null) return best;
        // «Нічого»: товари (не купівля — крамниць не додає), а як нема — пас чи бронь наосліп.
        if (TakeFor(c, s, look) is { } pick) return new SoMove("take", Got: pick);
        if (legal.Pass) return new SoMove("pass");
        for (var tr = 2; tr >= 0; tr--)
            if (legal.Deck[tr]) return new SoMove("reserve", Tier: tr + 1);
        return Rescue(c, s);
    }

    /// <summary>Позиція місця в колі від <c>First</c> (0 — ходить першим).</summary>
    static int Pos(SorochyntsiCore c, int x) =>
        (Array.IndexOf(c.Order, x) - Array.IndexOf(c.Order, c.First) + c.Seats) % c.Seats;

    /// <summary>Крамниці, які місце <paramref name="o"/> могло б купити на очах у всіх: ряди й його бронь з ряду (наосліп — не видно).</summary>
    static IEnumerable<int> OpenTo(SorochyntsiCore c, int o)
    {
        for (var tr = 0; tr < 3; tr++)
            for (var slot = 0; slot < Row; slot++)
                if (c.Rows[tr][slot] >= 0) yield return c.Rows[tr][slot];
        foreach (var r in c.Reserved[o])
            if (!r.Blind) yield return r.Id;
    }

    /// <summary>
    /// Хто кого наприкінці, якщо цей хід місця <paramref name="s"/> — останній для нього (останнє коло чи булава):
    /// <paramref name="s"/> купує <paramref name="buy"/> (-1 — ні) або бронює з ряду <paramref name="removed"/> (-1 — ні),
    /// потім кожен, хто ще ходить у цьому колі, відповідає найкращою для себе купівлею з відкритого (ряди й своя бронь з
    /// ряду) — з гостем і старшиною. Повертає найгірший для <paramref name="s"/> запас: min по суперниках
    /// <c>(⭐ мої − ⭐ його)·64 + (крамниць у нього − у мене)</c>: &gt; 0 — виграю в усіх, 0 — нічия, &lt; 0 — хтось обходить.
    /// <paramref name="only"/> ≥ 0 — лише проти цього місця.
    /// </summary>
    static int Margin(SorochyntsiCore c, int s, int buy, int removed, int only = -1, bool respond = true)
    {
        var b = c.Bonus[s];
        var icons = c.Icons[s];
        var shops = c.Cards[s].Count;
        var pts = c.CardPts[s] + GuestPoints * c.GuestsOf[s].Count;
        var plus = -1;
        if (buy >= 0)
        {
            var x = Deck[buy];
            plus = x.Bonus;
            icons += x.K;
            shops++;
            pts += x.Pts;
            removed = buy;
        }
        var star = c.Starshyna;
        if (buy >= 0 && Deck[buy].K > 0 && star != s && icons >= StarshynaIcons && (star < 0 || icons > c.Icons[star])) star = s;
        var starIcons = star == s ? icons : star >= 0 ? c.Icons[star] : 0;
        // Гість приходить наприкінці будь-якого ходу, якщо знижки його покривають (і той, що чекав із минулого ходу).
        var guest = GuestAfter(c, b, plus, -1);
        if (guest >= 0) pts += GuestPoints;
        var me = Pos(c, s);
        var worst = int.MaxValue;
        foreach (var o in c.Order)
        {
            if (o == s || (only >= 0 && o != only)) continue;
            var oBase = c.CardPts[o] + GuestPoints * c.GuestsOf[o].Count;
            var oShops = c.Cards[o].Count;
            var myNow = pts + (star == s ? StarshynaPoints : 0);
            var w = (myNow - oBase - (star == o ? StarshynaPoints : 0)) * 64 + oShops - shops;
            if (Pos(c, o) > me)
            {
                // Ще ходить: «нічого» (хіба що гість, який уже чекає) або найкраща купівля.
                if (GuestAfter(c, c.Bonus[o], -1, guest) >= 0) w -= GuestPoints * 64;
                foreach (var y in respond ? OpenTo(c, o) : Enumerable.Empty<int>())
                {
                    if (y == removed || !SorochyntsiCore.QuoteFor(y, c.Bonus[o], c.Tok[o]).Ok) continue;
                    var yc = Deck[y];
                    var oi = c.Icons[o] + yc.K;
                    var oStar = star;
                    if (yc.K > 0 && oStar != o && oi >= StarshynaIcons && (oStar < 0 || oi > starIcons)) oStar = o;
                    var oP = oBase + yc.Pts + (GuestAfter(c, c.Bonus[o], yc.Bonus, guest) >= 0 ? GuestPoints : 0)
                        + (oStar == o ? StarshynaPoints : 0);
                    var myP = pts + (oStar == s ? StarshynaPoints : 0);
                    w = Math.Min(w, (myP - oP) * 64 + oShops + 1 - shops);
                }
            }
            worst = Math.Min(worst, w);
        }
        return worst == int.MaxValue ? 1 : worst;
    }

    /// <summary>
    /// Бронь-захист хитрого: суперник має рівно одну крамницю в ряду, що дасть йому булаву, і з нею він обійшов би мене
    /// (з моєю відповіддю, якщо я ходжу після нього) — її й забронювати. Кілька таких — одною бронею не спиниш.
    /// </summary>
    static int? Threat(SorochyntsiCore c, int s)
    {
        foreach (var o in c.Order)
        {
            if (o == s) continue;
            var trig = OpenTo(c, o).Where(y => SorochyntsiCore.QuoteFor(y, c.Bonus[o], c.Tok[o]).Ok && GivesBulava(c, o, y)).Distinct().ToList();
            if (trig.Count != 1 || c.InRows(trig[0]) is null) continue;
            if (Margin(c, o, trig[0], -1, only: s) > 0) return trig[0];
        }
        return null;
    }

    /// <summary>Чи купить ціль <paramref name="id"/> хтось із суперників щонайпізніше через хід (бракує ≤ 1 після дукатів).</summary>
    static bool Contested(SorochyntsiCore c, int s, int id) =>
        c.Order.Any(o => o != s && SorochyntsiCore.QuoteFor(id, c.Bonus[o], c.Tok[o]).Short <= 1);

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
    static int[] Give(SorochyntsiCore c, int s, bool smart)
    {
        var look = See(c, s, smart);
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
