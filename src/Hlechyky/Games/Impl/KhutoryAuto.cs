namespace Hlechyky.Games.Impl;

/// <summary>
/// Простий, але не дурний Глек для «Хуторів» (контракт §8, хвиля 1) — і автохід за того, хто проспав.
/// Один крок (одна дія) за виклик: ставить хати на родючі вершини, будує, що може, міняє в банку лише тоді, коли
/// обмін одразу дає будівлю, грабує лідера, на пропозиції відповідає «ні» (торгівля з людьми — хвиля 2).
/// </summary>
public static class KhutoryAuto
{
    static readonly KhutoryGeo G = KhutoryGeo.I;

    /// <summary>Зробити один крок за місце <paramref name="s"/>. Повертає помилку ядра (не мала б бути) або «нічого робити».</summary>
    public static string? Step(KhutoryCore c, int s, Random rng)
    {
        if (c.Over) return "Партія скінчилась";
        switch (c.Phase)
        {
            case KhPhase.Setup when s == c.Turn:
                return c.SetupRoad ? c.PlaceRoad(s, SetupRoad(c, s)) : c.PlaceTown(s, BestTown(c, s, c.TownSpots(s)));
            case KhPhase.Roll when s == c.Turn:
                return KnightNeeded(c, s) && c.Play(s, KhutoryCore.Knight) is null ? null : c.Roll(s);
            case KhPhase.Discard when c.DiscardNeed[s] > 0:
                return c.Discard(s, DiscardPick(c.Hand[s], c.DiscardNeed[s]));
            case KhPhase.Robber when s == c.Turn:
                return c.MoveRobber(s, RobberHex(c, s));
            case KhPhase.Steal when s == c.Turn:
                return c.Steal(s, Victim(c, c.StealFrom));
            case KhPhase.Road2 when s == c.Turn:
                {
                    var spots = c.RoadSpots(s);
                    var e = TowardTown(c, s) ?? (spots.Count > 0 ? spots[0] : -1);
                    return e >= 0 ? c.PlaceRoad(s, e) : c.ForceEnd(s);
                }
            case KhPhase.Main when s == c.Turn:
                return MainStep(c, s);
            case KhPhase.Main:
                foreach (var o in c.Offers)
                    if (c.Addressed(o, s) && !o.Acc.Contains(s) && !o.Dec.Contains(s)) return c.Answer(s, o.Id, false);
                return "Нема що робити";
        }
        return "Нема що робити";
    }

    // ---------------------------------------------------------------------------------------
    // Оцінка вершин
    // ---------------------------------------------------------------------------------------

    /// <summary>Сума «крапок» сусідніх гексів + різноманіття + бонус за ресурси, яких гравець ще не має.</summary>
    public static double Score(KhutoryCore c, int s, int v, bool[]? have = null)
    {
        have ??= Producing(c, s);
        var seen = new HashSet<int>();
        double score = 0;
        foreach (var h in G.VH[v])
        {
            var r = c.B.ResOf(h);
            if (r < 0) continue;
            var pips = KhutoryBoard.Pips(c.B.Num[h]);
            score += h == c.Robber ? pips * 0.5 : pips;
            if (seen.Add(r)) score += have[r] ? 0.8 : 2.0;
        }
        for (var i = 0; i < c.B.PortEdge.Length; i++)
            if (G.Edges[c.B.PortEdge[i]].Contains(v)) score += 0.7;
        return score;
    }

    static bool[] Producing(KhutoryCore c, int s)
    {
        var have = new bool[5];
        for (var v = 0; v < KhutoryGeo.Verts; v++)
            if (c.VOwner[v] == s)
                foreach (var h in G.VH[v])
                    if (c.B.ResOf(h) is var r and >= 0) have[r] = true;
        return have;
    }

    public static int BestTown(KhutoryCore c, int s, List<int> spots)
    {
        var have = Producing(c, s);
        var best = -1;
        var bestScore = double.MinValue;
        foreach (var v in spots)
        {
            var sc = Score(c, s, v, have);
            if (sc > bestScore) { bestScore = sc; best = v; }
        }
        return best;
    }

    /// <summary>Шлях від щойно поставленої хати — у бік найкращої вільної вершини на два кроки.</summary>
    static int SetupRoad(KhutoryCore c, int s)
    {
        var have = Producing(c, s);
        var best = -1;
        var bestScore = double.MinValue;
        foreach (var e in c.RoadSpots(s))
        {
            var w = G.Other(e, c.SetupTown);
            double sc = 0;
            foreach (var x in G.VNb[w])
                if (x != c.SetupTown && c.TownFree(x)) sc = Math.Max(sc, Score(c, s, x, have));
            if (sc > bestScore) { bestScore = sc; best = e; }
        }
        return best;
    }

    /// <summary>
    /// Перше ребро найкращої дороги до вільної вершини, де можна буде поставити хату (пошук ушир від своєї мережі,
    /// не крізь чужі хати й шляхи). null — нікуди.
    /// </summary>
    public static int? TowardTown(KhutoryCore c, int s)
    {
        var have = Producing(c, s);
        var dist = Enumerable.Repeat(-1, KhutoryGeo.Verts).ToArray();
        var first = Enumerable.Repeat(-1, KhutoryGeo.Verts).ToArray();
        var queue = new Queue<int>();
        for (var v = 0; v < KhutoryGeo.Verts; v++)
        {
            var mine = c.VOwner[v] == s || (c.VOwner[v] < 0 && G.VE[v].Any(e => c.EOwner[e] == s));
            if (!mine) continue;
            dist[v] = 0;
            queue.Enqueue(v);
        }
        int? best = null;
        var bestScore = double.MinValue;
        while (queue.Count > 0)
        {
            var v = queue.Dequeue();
            if (dist[v] >= 4) continue;
            foreach (var e in G.VE[v])
            {
                if (c.EOwner[e] >= 0) continue;
                var w = G.Other(e, v);
                if (dist[w] >= 0) continue;
                dist[w] = dist[v] + 1;
                first[w] = dist[v] == 0 ? e : first[v];
                if (c.TownFree(w))
                {
                    var sc = Score(c, s, w, have) - 2.5 * dist[w];
                    if (sc > bestScore) { bestScore = sc; best = first[w]; }
                }
                if (c.VOwner[w] < 0) queue.Enqueue(w);
            }
        }
        return best;
    }

    // ---------------------------------------------------------------------------------------
    // Хід
    // ---------------------------------------------------------------------------------------

    static string? MainStep(KhutoryCore c, int s)
    {
        var hand = c.Hand[s];
        var have = Producing(c, s);
        // Дорога до хати — пошук ушир дорогий, рахуємо раз на крок.
        int? toward = null;
        var towardDone = false;
        int? Toward()
        {
            if (!towardDone) { toward = TowardTown(c, s); towardDone = true; }
            return toward;
        }
        if (c.WhyNot(s, "city") is null)
            return c.PlaceCity(s, c.CitySpots(s).OrderByDescending(v => Score(c, s, v, have)).First());
        if (c.WhyNot(s, "town") is null)
            return c.PlaceTown(s, BestTown(c, s, c.TownSpots(s)));

        // Грамоти: не свіжі, одна за хід.
        if (!c.PlayedDev)
        {
            var ready = c.Devs[s].Where(d => !d.Fresh).Select(d => d.K).ToHashSet();
            if (ready.Contains(KhutoryCore.Knight) && KnightNeeded(c, s)) return c.Play(s, KhutoryCore.Knight);
            var needRoad = c.TownsLeft(s) > 0 && c.TownSpots(s).Count == 0 && Toward() is not null;
            if (ready.Contains(KhutoryCore.Toloka) && needRoad && c.Play(s, KhutoryCore.Toloka) is null) return null;
            if (ready.Contains(KhutoryCore.Plenty))
            {
                var goal = Goal(c, s, Toward) ?? KhutoryCore.CityCost;
                var want = Missing(hand, goal);
                var a = want.Count > 0 ? want[0] : 4;
                var b = want.Count > 1 ? want[1] : a;
                if (c.Play(s, KhutoryCore.Plenty, a, b) is null) return null;
            }
            if (ready.Contains(KhutoryCore.Mono))
            {
                var r = Enumerable.Range(0, 5).OrderByDescending(x => Others(c, s, x)).First();
                if (Others(c, s, r) >= 3 && c.Play(s, KhutoryCore.Mono, r) is null) return null;
            }
        }

        // Шлях — коли хат ставити нікуди, а є куди йти.
        if (c.TownsLeft(s) > 0 && c.TownSpots(s).Count == 0 && c.WhyNot(s, "road") is null && Toward() is { } road)
            return c.PlaceRoad(s, road);

        // Обмін із банком — лише якщо він одразу веде до будівлі.
        if (Goal(c, s, Toward) is { } cost && TradeToward(c, s, cost) is { } err0) return err0 == "" ? null : err0;

        if (c.WhyNot(s, "dev") is null) return c.Buy(s);
        return c.End(s);
    }

    /// <summary>Чого прагнути: містечко, хата, шлях до хати, грамота — перше, що можна дістати обміном.</summary>
    static int[]? Goal(KhutoryCore c, int s, Func<int?> toward)
    {
        var rates = c.Rates(s);
        var hand = c.Hand[s];
        if (c.CitiesLeft(s) > 0 && c.CitySpots(s).Count > 0 && Reachable(hand, KhutoryCore.CityCost, rates, c.Bank)) return KhutoryCore.CityCost;
        if (c.TownsLeft(s) > 0 && c.TownSpots(s).Count > 0 && Reachable(hand, KhutoryCore.TownCost, rates, c.Bank)) return KhutoryCore.TownCost;
        if (c.TownsLeft(s) > 0 && c.TownSpots(s).Count == 0 && c.RoadsLeft(s) > 0 && toward() is not null
            && Reachable(hand, KhutoryCore.RoadCost, rates, c.Bank)) return KhutoryCore.RoadCost;
        if (c.Deck.Count > 0 && Reachable(hand, KhutoryCore.DevCost, rates, c.Bank)) return KhutoryCore.DevCost;
        return null;
    }

    static bool Reachable(int[] hand, int[] cost, int[] rates, int[] bank)
    {
        var deficit = 0;
        var spare = 0;
        for (var r = 0; r < 5; r++)
        {
            if (hand[r] < cost[r])
            {
                deficit += cost[r] - hand[r];
                if (bank[r] < cost[r] - hand[r]) return false;
            }
            else spare += (hand[r] - cost[r]) / rates[r];
        }
        return deficit > 0 && spare >= deficit;
    }

    /// <summary>Один обмін у бік цілі: "" — обміняв, null — не треба/не можна, інше — помилка ядра.</summary>
    static string? TradeToward(KhutoryCore c, int s, int[] cost)
    {
        var hand = c.Hand[s];
        var rates = c.Rates(s);
        var get = Missing(hand, cost).FirstOrDefault(-1);
        if (get < 0) return null;
        var give = -1;
        var best = 0;
        for (var r = 0; r < 5; r++)
        {
            var spare = (hand[r] - cost[r]) / rates[r];
            if (r != get && hand[r] >= cost[r] && spare > best) { best = spare; give = r; }
        }
        if (give < 0) return null;
        return c.BankTrade(s, give, get, 1) ?? "";
    }

    static List<int> Missing(int[] hand, int[] cost)
    {
        var list = new List<int>();
        for (var r = 0; r < 5; r++)
            for (var n = hand[r]; n < cost[r]; n++) list.Add(r);
        return list;
    }

    static int Others(KhutoryCore c, int s, int r)
    {
        var n = 0;
        for (var o = 0; o < KhutoryCore.MaxSeats; o++) if (o != s && c.In[o]) n += c.Hand[o][r];
        return n;
    }

    /// <summary>Кармелюк сидить на гексі біля своєї хати — пора козака (якщо є готовий і ще не грав).</summary>
    static bool KnightNeeded(KhutoryCore c, int s) =>
        !c.PlayedDev && c.Devs[s].Any(d => d.K == KhutoryCore.Knight && !d.Fresh)
        && c.OwnersAt(c.Robber).Contains(s) && c.B.ResOf(c.Robber) >= 0;

    // ---------------------------------------------------------------------------------------
    // Сімка
    // ---------------------------------------------------------------------------------------

    /// <summary>Скинути з найчисленніших.</summary>
    public static int[] DiscardPick(int[] hand, int need)
    {
        var left = (int[])hand.Clone();
        var drop = new int[5];
        for (var i = 0; i < need; i++)
        {
            var r = 0;
            for (var x = 1; x < 5; x++) if (left[x] > left[r]) r = x;
            left[r]--;
            drop[r]++;
        }
        return drop;
    }

    /// <summary>Гекс для Кармелюка: найбільше шкоди лідерові, своїх не чіпати.</summary>
    static int RobberHex(KhutoryCore c, int s)
    {
        var spots = c.RobberSpots(s);
        var best = spots[0];
        var bestScore = double.MinValue;
        foreach (var h in spots)
        {
            double sc = 0;
            var pips = KhutoryBoard.Pips(c.B.Num[h]);
            foreach (var v in G.HexV[h])
            {
                var o = c.VOwner[v];
                if (o < 0) continue;
                var mult = c.VCity[v] ? 2 : 1;
                if (o == s) sc -= 100 + pips * mult;
                else sc += (c.PublicPoints(o) + 1) * (pips + 1) * mult + (c.Total(o) > 0 ? 1 : 0);
            }
            if (sc > bestScore) { bestScore = sc; best = h; }
        }
        return best;
    }

    /// <summary>У кого красти: лідер за відкритими очками, далі — у кого більше карт.</summary>
    static int Victim(KhutoryCore c, List<int> from) =>
        from.OrderByDescending(o => c.PublicPoints(o)).ThenByDescending(o => c.Total(o)).First();
}
