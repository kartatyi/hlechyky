using static Hlechyky.Games.Impl.SolyanyiMap;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Один крок Глека: <c>draw</c> (Slot або Deck), <c>claim</c> (Route, C — вид карти, W; Block — це блок суперника Victim),
/// <c>tickets</c>, <c>keep</c> (Ids), <c>pass</c>.
/// </summary>
public sealed record SlMove(string Act, int Slot = -1, bool Deck = false, int Route = -1, int C = -1, int W = 0, int[]? Ids = null,
    bool Block = false, int Victim = -1);

/// <summary>Дерево шляхів під замовлення (§8.1): вільні шляхи плану, скільки це возів, які замовлення вже не довезти.</summary>
public sealed record SlPlan(List<int> Zero, int Need, List<int> Dead);

/// <summary>
/// Глек-гравець «Соляного шляху» (контракт §8.1). Бачить лише те, що бачив би гравець на цьому місці: мапу, власників шляхів,
/// ринок, кількості в колодах, свою руку й свої замовлення — порядку колод, чужих рук і замовлень не читає. Детермінований, без
/// випадковості (рівність — менший id, раніший вантаж) і ніколи не ходить незаконно: кандидати — з <see cref="SolyanyiCore.ClaimOk"/>
/// і <see cref="SolyanyiCore.Pays"/>, тих самих, що й у виді.
/// </summary>
public static class SolyanyiBot
{
    /// <summary>Скільки кроків щонайбільше в одному ході (два взяття) — запобіжник від зациклення.</summary>
    const int StepCap = 4;

    /// <summary>
    /// Один повний хід місця від поточної фази: дія, якщо ще не зроблена, друге взяття чи вибір; у setup — вибір цього місця.
    /// Той самий метод доробляє хід за того, хто проспав строк. Повертає, скільки кроків зроблено.
    /// </summary>
    public static int Step(SolyanyiCore c, int s)
    {
        if (c.Phase == SlPhase.Setup)
        {
            if (c.Ready[s]) return 0;
            if (Apply(c, s, Decide(c, s)) is not null) c.Keep(s, [.. c.Offer[s].Take(c.KeepMin)]);
            return 1;
        }
        var steps = 0;
        var turns = c.Turns;
        while (!c.Over && c.Turn == s && c.Turns == turns && steps < StepCap)
        {
            // Хибний крок (не мав би статись) — рятівний законний, щоб стіл не завис.
            if (Apply(c, s, Decide(c, s)) is not null) Apply(c, s, Rescue(c, s));
            steps++;
        }
        return steps;
    }

    /// <summary>Застосувати крок до ядра (через ті самі методи, що й дії людей).</summary>
    public static string? Apply(SolyanyiCore c, int s, SlMove m)
    {
        switch (m.Act)
        {
            case "draw":
                return c.Draw(s, m.Slot >= 0 ? m.Slot : null, m.Deck);
            case "claim":
                var err = c.Claim(s, m.Route, m.C >= 0 ? Cards[m.C] : null, m.W);
                if (err is null && m.Block) c.NoteBlock(s, m.Route, m.Victim);
                return err;
            case "tickets":
                return c.TakeTickets(s);
            case "keep":
                return c.Keep(s, m.Ids);
            default:
                return c.Pass(s);
        }
    }

    /// <summary>Рятівний крок: перше законне, що знайдеться.</summary>
    static SlMove Rescue(SolyanyiCore c, int s)
    {
        if (c.Phase is SlPhase.Keep or SlPhase.Setup) return new SlMove("keep", Ids: [.. c.Offer[s].Take(c.KeepMin)]);
        var second = c.Phase == SlPhase.Draw;
        if (c.Deck.Count > 0 || c.Discard.Count > 0) return new SlMove("draw", Deck: true);
        for (var k = 0; k < MarketSize; k++)
            if (c.Market[k] >= 0 && (!second || c.Market[k] != Wild)) return new SlMove("draw", Slot: k);
        for (var r = 0; r < Routes.Length; r++)
            if (c.ClaimOk(s, r)) return Claim(c, s, r, null);
        return c.TicketDeck.Count > 0 ? new SlMove("tickets") : new SlMove("pass");
    }

    /// <summary>Вибір Глека для місця на цю фазу (§8.1). Той самий для ходу, поради (<c>me.tip</c>) і дорібки за того, хто проспав.</summary>
    public static SlMove Decide(SolyanyiCore c, int s) => c.Phase switch
    {
        SlPhase.Setup or SlPhase.Keep => new SlMove("keep", Ids: Keep(c, s)),
        SlPhase.Draw => DrawMove(c, s, Plan(c, s)) ?? Rescue(c, s),
        _ => Act(c, s),
    };

    // =====================================================================================
    // Планувальник
    // =====================================================================================

    /// <summary>Невиконані замовлення місця.</summary>
    static List<int> Open(SolyanyiCore c, int s)
    {
        var list = new List<int>(c.Tickets[s].Count);
        foreach (var t in c.Tickets[s]) if (!c.Done[t]) list.Add(t);
        return list;
    }

    /// <summary>План під свої невиконані замовлення.</summary>
    public static SlPlan Plan(SolyanyiCore c, int s) => Plan(c, s, Open(c, s));

    /// <summary>
    /// Жадібне дерево Штейнера (§8.1): поки є замовлення — найдешевше за Дейкстрою, де шляхи плану вже нічого не коштують
    /// (рівність — більше ⭐, далі менший id). Шляху нема чи возів не вистачить — у <c>Dead</c>.
    /// </summary>
    public static SlPlan Plan(SolyanyiCore c, int s, IReadOnlyList<int> tickets)
    {
        var zero = new List<int>();
        var dead = new List<int>();
        var need = 0;
        var cost = c.Costs(s);
        var left = new List<int>(tickets);
        var path = new List<int>();
        while (left.Count > 0)
        {
            var best = -1;
            var bestD = 0;
            for (var i = left.Count - 1; i >= 0; i--)
            {
                var t = Tickets[left[i]];
                var d = Dijkstra(t.A, t.B, cost);
                if (d < 0)
                {
                    dead.Add(left[i]);
                    left.RemoveAt(i);
                    if (best > i) best--;
                    continue;
                }
                if (best < 0 || d < bestD || (d == bestD && Better(t, Tickets[left[best]])))
                {
                    best = i;
                    bestD = d;
                }
            }
            if (best < 0) break;
            var pick = left[best];
            left.RemoveAt(best);
            if (need + bestD > c.Wagons[s])
            {
                dead.Add(pick);
                continue;
            }
            need += bestD;
            var tk = Tickets[pick];
            Dijkstra(tk.A, tk.B, cost, path);
            foreach (var r in path)
                if (c.Owner[r] < 0 && cost[r] > 0)
                {
                    cost[r] = 0;
                    zero.Add(r);
                }
        }
        return new SlPlan(zero, need, dead);
    }

    static bool Better(SlTicket a, SlTicket b) => a.Pts > b.Pts || (a.Pts == b.Pts && a.Id < b.Id);

    /// <summary>Вибір замовлень (§8.1): усі законні підмножини пропозиції, найбільший <c>val</c>; рівність — менше карт, потім менші id.</summary>
    public static int[] Keep(SolyanyiCore c, int s)
    {
        var offer = c.Offer[s].Order().ToArray();
        var min = c.KeepMin;
        var mine = Open(c, s);
        var without = Plan(c, s, mine).Need;
        var wagons = c.Wagons[s];
        var best = Array.Empty<int>();
        var bestVal = double.NegativeInfinity;
        for (var mask = 1; mask < 1 << offer.Length; mask++)
        {
            var pick = Enumerable.Range(0, offer.Length).Where(i => (mask & (1 << i)) != 0).Select(i => offer[i]).ToArray();
            if (pick.Length < min) continue;
            var plan = Plan(c, s, [.. mine, .. pick]);
            var val = pick.Sum(t => Tickets[t].Pts) - 2.0 * plan.Dead.Where(pick.Contains).Sum(t => Tickets[t].Pts)
                - 0.35 * (plan.Need - without) - (wagons - plan.Need < 4 ? 6 : 0);
            if (val > bestVal + 1e-9 || (Math.Abs(val - bestVal) <= 1e-9 && Fewer(pick, best)))
            {
                bestVal = val;
                best = pick;
            }
        }
        return best.Length > 0 ? best : [.. offer.Take(min)];
    }

    /// <summary>Менше карт, далі менші id (обидва — за зростанням).</summary>
    static bool Fewer(int[] a, int[] b)
    {
        if (a.Length != b.Length) return a.Length < b.Length;
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return a[i] < b[i];
        return false;
    }

    /// <summary>
    /// Індекс оплати, яку обрав би Глек (§8.1, <c>best</c> у <c>me.claims</c>): менше волів і менше шкоди стосу, потрібному
    /// плану; витратити вантаж повністю — плюс; лише воли — −100. Рівність — раніший вантаж.
    /// </summary>
    public static int BestPay(SolyanyiCore c, int s, int route, List<SlPay> pays, SlPlan plan)
    {
        var h = c.Hand[s];
        var best = 0;
        var bestScore = int.MinValue;
        for (var i = 0; i < pays.Count; i++)
        {
            var p = pays[i];
            int score;
            if (p.C == Wild) score = -100;
            else
            {
                var need = 0;
                foreach (var z in plan.Zero)
                    if (z != route && Routes[z].Cargo == p.C) need += Routes[z].Len;
                score = -6 * p.W - 2 * Math.Max(0, Math.Min(need, h[p.C]) - (h[p.C] - p.N)) + (p.N == h[p.C] ? 1 : 0);
            }
            if (score > bestScore || (score == bestScore && p.C < pays[best].C))
            {
                bestScore = score;
                best = i;
            }
        }
        return best;
    }

    static SlMove Claim(SolyanyiCore c, int s, int route, SlPlan? plan, bool block = false, int victim = -1)
    {
        var pays = c.Pays(s, route);
        var p = pays[BestPay(c, s, route, pays, plan ?? Plan(c, s))];
        return new SlMove("claim", Route: route, C: p.C, W: p.W, Block: block, Victim: victim);
    }

    // =====================================================================================
    // Хід (§8.1): перше застосовне
    // =====================================================================================

    static SlMove Act(SolyanyiCore c, int s)
    {
        var plan = Plan(c, s);
        if (c.Last)
        {
            if (LastClaim(c, s) is { } lastRoute) return Claim(c, s, lastRoute, plan);
            return DrawMove(c, s, plan) ?? Fallback(c, s, plan);
        }
        if (PlanClaim(c, s, plan) is { } planned) return Claim(c, s, planned, plan);
        if (FindBlock(c, s) is { } block) return Claim(c, s, block.Route, plan, true, block.Victim);
        if (plan.Zero.Count == 0)
        {
            if (c.Wagons[s] >= 12 && c.TicketDeck.Count > 0) return new SlMove("tickets");
            if (c.Hand[s].Sum() >= 4 && Longest(c, s, adjacent: true) is { } longest) return Claim(c, s, longest, plan);
        }
        return DrawMove(c, s, plan) ?? Fallback(c, s, plan);
    }

    /// <summary>6. Шлях (найдовший, менший id), або замовлення, або пропуск (лише якщо законний).</summary>
    static SlMove Fallback(SolyanyiCore c, int s, SlPlan plan)
    {
        if (Longest(c, s, adjacent: false) is { } r) return Claim(c, s, r, plan);
        return c.TicketDeck.Count > 0 ? new SlMove("tickets") : new SlMove("pass");
    }

    /// <summary>1. Останній хід: найбільше очок, разом із замовленнями, які цей шлях зв'язує. Рівність — менший id.</summary>
    static int? LastClaim(SolyanyiCore c, int s)
    {
        int? best = null;
        var bestScore = int.MinValue;
        var net = c.Net(s);
        var open = Open(c, s);
        for (var r = 0; r < Routes.Length; r++)
        {
            if (!c.ClaimOk(s, r)) continue;
            var rt = Routes[r];
            var (ra, rb) = (net[rt.A], net[rt.B]);
            var score = rt.Pts;
            foreach (var t in open)
            {
                var (ta, tb) = (net[Tickets[t].A], net[Tickets[t].B]);
                if ((ta == ra && tb == rb) || (ta == rb && tb == ra)) score += 2 * Tickets[t].Pts;
            }
            if (score > bestScore)
            {
                bestScore = score;
                best = r;
            }
        }
        return best;
    }

    /// <summary>
    /// 2. Шлях із плану, який можу оплатити: найбільший <c>ризик + len</c> (рівність — більший len, менший id). Ризик —
    /// найбільше подовження серед моїх невиконаних замовлень, якщо шлях заберуть; шляху більше нема — 20.
    /// </summary>
    static int? PlanClaim(SolyanyiCore c, int s, SlPlan plan)
    {
        int? best = null;
        var bestScore = int.MinValue;
        var bestLen = 0;
        int[]? cost = null;
        List<(SlTicket T, int D)>? open = null;
        foreach (var r in plan.Zero)
        {
            if (!c.ClaimOk(s, r)) continue;
            if (open is null)
            {
                cost = c.Costs(s);
                open = [];
                foreach (var id in Open(c, s))
                {
                    var t = Tickets[id];
                    var d = Dijkstra(t.A, t.B, cost);
                    if (d >= 0) open.Add((t, d));
                }
            }
            var was = cost![r];
            cost[r] = -1;
            var risk = 0;
            foreach (var (t, d) in open)
            {
                var without = Dijkstra(t.A, t.B, cost);
                risk = Math.Max(risk, without < 0 ? 20 : without - d);
            }
            cost[r] = was;
            var len = Routes[r].Len;
            var score = risk + len;
            if (best is null || score > bestScore || (score == bestScore && (len > bestLen || (len == bestLen && r < best))))
            {
                best = r;
                bestScore = score;
                bestLen = len;
            }
        }
        return best;
    }

    /// <summary>
    /// 3. Блок: дві найбільші окремі мережі суперника (у кожній ≥ 2 шляхи) ось-ось зійдуться — стати на їхньому найкоротшому
    /// зв'язку (1 ≤ d ≤ 3). Лише публічне: чужих замовлень Глек не знає. На 4–5 — шлях без пари (бо другий лишиться).
    /// </summary>
    static (int Route, int Victim)? FindBlock(SolyanyiCore c, int s)
    {
        if (c.Blocks[s] >= 2 || c.Wagons[s] < 10) return null;
        var path = new List<int>();
        for (var o = c.Next(s); o != s; o = c.Next(o))
        {
            if (c.Taken[o].Count < 4) continue;
            var net = c.Net(o);
            var nets = new Dictionary<int, (int Count, int Len)>();
            foreach (var id in c.Taken[o])
            {
                var root = net[Routes[id].A];
                nets.TryGetValue(root, out var v);
                nets[root] = (v.Count + 1, v.Len + Routes[id].Len);
            }
            var big = nets.Where(p => p.Value.Count >= 2)
                .OrderByDescending(p => p.Value.Len).ThenByDescending(p => p.Value.Count).ThenBy(p => p.Key)
                .Take(2).Select(p => p.Key).ToArray();
            if (big.Length < 2) continue;
            // Свої шляхи суперника нічого не коштують, тож зв'язок між мережами — Дейкстра між будь-якими їхніми містами.
            var d = Dijkstra(big[0], big[1], c.Costs(o), path);
            if (d is < 1 or > 3) continue;
            int? best = null;
            foreach (var r in path)
            {
                if (c.Owner[r] >= 0 || !c.ClaimOk(s, r)) continue;
                if (!c.OnePairOnly && Routes[r].Twin >= 0) continue;
                if (best is null || Routes[r].Len < Routes[best.Value].Len || (Routes[r].Len == Routes[best.Value].Len && r < best)) best = r;
            }
            if (best is { } route) return (route, o);
        }
        return null;
    }

    /// <summary>Найдовший шлях, який можна зайняти: рівність — той, що прилягає до моєї мережі (якщо просили), далі менший id.</summary>
    static int? Longest(SolyanyiCore c, int s, bool adjacent)
    {
        int? best = null;
        var bestLen = 0;
        var bestNear = false;
        var mine = new bool[Cities.Length];
        if (adjacent)
            foreach (var id in c.Taken[s])
            {
                mine[Routes[id].A] = true;
                mine[Routes[id].B] = true;
            }
        for (var r = 0; r < Routes.Length; r++)
        {
            if (!c.ClaimOk(s, r)) continue;
            var rt = Routes[r];
            var near = adjacent && (mine[rt.A] || mine[rt.B]);
            if (rt.Len > bestLen || (rt.Len == bestLen && near && !bestNear))
            {
                best = r;
                bestLen = rt.Len;
                bestNear = near;
            }
        }
        return best;
    }

    /// <summary>
    /// 5. Карти: не-віл із найбільшим <c>want</c> (рівність — менше місце); першим взяттям, якщо такого нема, а Σ want ≥ 4 — вола;
    /// інакше наосліп. Колоди нема — перший не-віл ринку; перше взяття й лише воли — віл; нема — null, кінець дії.
    /// </summary>
    static SlMove? DrawMove(SolyanyiCore c, int s, SlPlan plan)
    {
        var second = c.Phase == SlPhase.Draw;
        var h = c.Hand[s];
        Span<int> want = stackalloc int[8];
        want.Clear();
        var grey = -1;
        foreach (var z in plan.Zero)
        {
            var r = Routes[z];
            if (r.Cargo != Grey) want[r.Cargo] += Math.Max(0, r.Len - h[r.Cargo]);
            else if (grey < 0 || r.Len > Routes[grey].Len || (r.Len == Routes[grey].Len && z < grey)) grey = z;
        }
        if (grey >= 0)
        {
            var stack = 0;
            for (var i = 1; i < 8; i++) if (h[i] > h[stack]) stack = i;
            want[stack] += Math.Max(0, Routes[grey].Len - h[stack]);
        }
        var slot = -1;
        var wildSlot = -1;
        var plainSlot = -1;
        var sum = 0;
        for (var i = 0; i < 8; i++) sum += want[i];
        for (var k = 0; k < MarketSize; k++)
        {
            var card = c.Market[k];
            if (card < 0) continue;
            if (card == Wild)
            {
                if (wildSlot < 0) wildSlot = k;
                continue;
            }
            if (plainSlot < 0) plainSlot = k;
            if (want[card] > 0 && (slot < 0 || want[card] > want[c.Market[slot]])) slot = k;
        }
        if (slot >= 0) return new SlMove("draw", Slot: slot);
        if (!second && sum >= 4 && wildSlot >= 0) return new SlMove("draw", Slot: wildSlot);
        if (c.Deck.Count > 0 || c.Discard.Count > 0) return new SlMove("draw", Deck: true);
        if (plainSlot >= 0) return new SlMove("draw", Slot: plainSlot);
        if (!second && wildSlot >= 0) return new SlMove("draw", Slot: wildSlot);
        return null;
    }
}
