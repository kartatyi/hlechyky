namespace Hlechyky.Games.Impl;

/// <summary>
/// Розумний Глек «Хуторів» (контракт §8, хвиля 2). Один крок (одна дія) за виклик, як <see cref="KhutoryAuto"/>, але з
/// планом: оцінює вершини (крапки, різноманіття, рідкісні ресурси, пристань під свій надлишок), тримає «ціль» — наступну
/// будівлю (містечко, хата на доброму місці, шлях до неї, грамота) — і торгує під неї: з банком/пристанню найкращим
/// курсом, а людям і Глекам пропонує не більше одного чесного обміну за хід і чекає відповідей до <see cref="OfferWaitMs"/>.
/// На чужі пропозиції каже «так», лише коли обмін наближає його ціль і не допомагає тому, хто за 2 очки до перемоги.
/// Пам'ять (своя пропозиція й коли зроблена) — на партію; новий екземпляр — нова партія.
/// </summary>
public sealed partial class KhutoryBot
{
    /// <summary>Крок не зроблено: Глек чекає відповідей на свою пропозицію (див. <see cref="WaitUntil"/>).</summary>
    public const string Wait = "Глек чекає відповідей";
    /// <summary>Скільки чекати відповідей людей на пропозицію Глека.</summary>
    public const int OfferWaitMs = 10_000;

    static readonly KhutoryGeo G = KhutoryGeo.I;
    const int City = 0, TownGoal = 1, RoadGoal = 2, DevGoal = 3;

    readonly int[] _pending = [-1, -1, -1, -1];
    readonly DateTimeOffset[] _since = new DateTimeOffset[KhutoryCore.MaxSeats];
    readonly long[] _offered = [-1, -1, -1, -1];

    static long TurnKey(KhutoryCore c) => (long)c.Rounds * KhutoryCore.MaxSeats + c.TurnIdx;

    /// <summary>
    /// Зробити один крок за місце <paramref name="s"/>. null — зроблено; <see cref="Wait"/> — чекає відповідей на свою
    /// пропозицію; інше — помилка ядра або «нема що робити». <paramref name="trade"/> = false — без власних пропозицій
    /// (за людину, що відійшла). <paramref name="cards"/> = false — доробити хід за людину, що проспала main
    /// (<see cref="BuildStep"/>): лише будувати; грамот не купувати й не грати, з людьми не мінятись.
    /// </summary>
    public string? Step(KhutoryCore c, int s, Random rng, DateTimeOffset now, bool trade = true, bool cards = true)
    {
        if (c.Over) return "Партія скінчилась";
        switch (c.Phase)
        {
            case KhPhase.Setup when s == c.Turn:
                return c.SetupRoad ? c.PlaceRoad(s, SetupRoad(c, s)) : c.PlaceTown(s, BestTown(c, s, c.TownSpots(s)));
            case KhPhase.Roll when s == c.Turn:
                return WantKnight(c, s) && c.Play(s, KhutoryCore.Knight) is null ? null : c.Roll(s);
            case KhPhase.Discard when c.DiscardNeed[s] > 0:
                return c.Discard(s, DiscardPick(c, s, c.DiscardNeed[s]));
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
                return cards ? MainStep(c, s, now, trade) : BuildStep(c, s);
            case KhPhase.Main:
                foreach (var o in c.Offers)
                    if (c.Addressed(o, s) && !o.Acc.Contains(s) && !o.Dec.Contains(s)) return c.Answer(s, o.Id, Accepts(c, s, o));
                return "Нема що робити";
        }
        return "Нема що робити";
    }

    /// <summary>
    /// До коли Глек на місці <paramref name="s"/> чекає відповідей на свою пропозицію; null — не чекає (можна ходити
    /// одразу). Хтось прийняв, усі відповіли чи час минув — не чекає.
    /// </summary>
    public DateTimeOffset? WaitUntil(KhutoryCore c, int s, DateTimeOffset now)
    {
        if (c.Over || c.Phase != KhPhase.Main || s != c.Turn || _pending[s] < 0) return null;
        var o = c.Offers.FirstOrDefault(x => x.Id == _pending[s]);
        if (o is null || o.Acc.Any(a => !Leader(c, a)) || AllAnswered(c, o)) return null;
        if (c.Offers.Any(x => x.From != s && c.Addressed(x, s))) return null;   // зустрічна — відповісти
        var until = _since[s].AddMilliseconds(OfferWaitMs);
        return now < until ? until : null;
    }

    /// <summary>
    /// Строк пропозиції <paramref name="id"/> для виду (контракт §5, <c>offers[].until</c>): до коли Глек чекає відповідей
    /// на неї; null — це не пропозиція Глека, що чекає (людська — живе до кінця ходу). Після перезапуску <see cref="Shift"/>
    /// уже зсунув <c>_since</c>, тож строк правильний і тоді.
    /// </summary>
    public DateTimeOffset? OfferUntil(int id)
    {
        for (var s = 0; s < KhutoryCore.MaxSeats; s++)
            if (_pending[s] == id) return _since[s].AddMilliseconds(OfferWaitMs);
        return null;
    }

    // =========================================================================================
    // Хід
    // =========================================================================================

    /// <summary>
    /// Крок за людину, що проспала main: місто, хата, обмін у банку — лише коли так збирається будова цього ж ходу,
    /// шлях до місця під хату чи (з надлишку) для Чумацького шляху; далі — кінець ходу. Грамоти й зустрічні — її рішення.
    /// </summary>
    string? BuildStep(KhutoryCore c, int s)
    {
        if (c.WhyNot(s, "city") is null)
            return c.PlaceCity(s, c.CitySpots(s).OrderByDescending(v => VertexPips(c, v)).ThenBy(v => v).First());
        if (c.WhyNot(s, "town") is null)
            return c.PlaceTown(s, BestTown(c, s, c.TownSpots(s)));
        var t = new Think(c, s);
        t.Goals.RemoveAll(g => g.Kind == DevGoal);
        var hand = c.Hand[s];
        var reach = t.Goals.Where(g => KhutoryCore.Has(hand, g.Cost) || Reachable(hand, g.Cost, t.Rates, c.Bank))
            .OrderByDescending(g => g.Weight).FirstOrDefault();
        var goal = reach ?? t.Best(hand);
        if (goal is null) return c.End(s);
        if (goal.Kind == RoadGoal && c.WhyNot(s, "road") is null && TowardTown(c, s) is { } road) return c.PlaceRoad(s, road);
        var missing = Missing(hand, goal.Cost);
        if (reach is not null && missing.Count > 0 && TradeToward(c, s, goal.Cost, t) is { } err) return err == "" ? null : err;
        if (c.WhyNot(s, "road") is null && Missing(Sub(hand, KhutoryCore.RoadCost), goal.Cost).Count == missing.Count
            && RoadForLongest(c, s) is { } longer)
            return c.PlaceRoad(s, longer);
        return c.End(s);
    }

    string? MainStep(KhutoryCore c, int s, DateTimeOffset now, bool trade)
    {
        var t = new Think(c, s);
        var hand = c.Hand[s];

        // Зустрічна мені — вирішити одразу.
        foreach (var o in c.Offers)
            if (o.From != s && c.Addressed(o, s))
                return Accepts(c, s, o) && c.Deal(s, o.Id, o.From) is null ? null : c.Answer(s, o.Id, false);

        // Своя пропозиція: хтось прийняв — міняємось; усі відмовили чи минув час — скасувати.
        if (_pending[s] >= 0)
        {
            var o = c.Offers.FirstOrDefault(x => x.Id == _pending[s] && x.From == s);
            if (o is null) _pending[s] = -1;
            else
            {
                var ok = o.Acc.Where(a => !Leader(c, a)).OrderBy(c.PublicPoints).ThenBy(a => a).ToList();
                if (ok.Count > 0)
                {
                    _pending[s] = -1;
                    return c.Deal(s, o.Id, ok[0]) is null ? null : c.Cancel(s, o.Id);
                }
                if (AllAnswered(c, o) || now >= _since[s].AddMilliseconds(OfferWaitMs))
                {
                    _pending[s] = -1;
                    return c.Cancel(s, o.Id);
                }
                return Wait;
            }
        }

        if (c.WhyNot(s, "city") is null)
            return c.PlaceCity(s, c.CitySpots(s).OrderByDescending(v => VertexPips(c, v)).ThenBy(v => v).First());
        if (c.WhyNot(s, "town") is null)
            return c.PlaceTown(s, BestTown(c, s, c.TownSpots(s)));

        // Що можна зібрати обмінами вже цього ходу — те й будуємо (найцінніше); інакше — найближча за колами ціль.
        var goal = t.Goals.Where(g => KhutoryCore.Has(hand, g.Cost) || Reachable(hand, g.Cost, t.Rates, c.Bank))
            .OrderByDescending(g => g.Weight).FirstOrDefault() ?? t.Best(hand);

        // Грамоти: одна за хід, не свіжі.
        if (!c.PlayedDev)
        {
            var ready = c.Devs[s].Where(d => !d.Fresh).Select(d => d.K).ToHashSet();
            if (ready.Contains(KhutoryCore.Knight) && WantKnight(c, s)) return c.Play(s, KhutoryCore.Knight);
            if (ready.Contains(KhutoryCore.Toloka) && goal?.Kind == RoadGoal && c.RoadsLeft(s) >= 2
                && c.Play(s, KhutoryCore.Toloka) is null) return null;
            if (goal is not null)
            {
                var miss = t.Hardest(Missing(hand, goal.Cost));
                if (ready.Contains(KhutoryCore.Plenty) && miss.Count > 0)
                {
                    var a = miss[0];
                    var b = miss.Count > 1 ? miss[1] : t.Hardest(Missing(hand, KhutoryCore.CityCost)).FirstOrDefault(a);
                    if (c.Play(s, KhutoryCore.Plenty, a, b) is null) return null;
                }
                if (ready.Contains(KhutoryCore.Mono) && miss.Count > 0)
                {
                    var r = miss.Distinct().OrderByDescending(x => Others(c, s, x)).First();
                    if (Others(c, s, r) >= 2 && c.Play(s, KhutoryCore.Mono, r) is null) return null;
                }
            }
        }

        if (goal is null)
            return c.WhyNot(s, "dev") is null ? c.Buy(s) : c.End(s);

        if (goal.Kind == RoadGoal && c.WhyNot(s, "road") is null && TowardTown(c, s) is { } road) return c.PlaceRoad(s, road);
        if (goal.Kind == DevGoal && c.WhyNot(s, "dev") is null) return c.Buy(s);

        var missing = Missing(hand, goal.Cost);
        if (missing.Count > 0)
        {
            // Пропозиція гравцям — одна за хід, якщо банк міняє не дешевше 3:1.
            if (trade && _offered[s] != TurnKey(c) && OfferFor(c, s, t, goal) is { } offer)
            {
                _offered[s] = TurnKey(c);
                if (c.Offer(s, offer.give, offer.get, null) is null)
                {
                    _pending[s] = c.Offers[^1].Id;
                    _since[s] = now;
                    return null;
                }
            }
            // Банк / пристань — коли обмінами можна зібрати ціль цього ж ходу або рука загрубла (сімка близько).
            if ((Reachable(hand, goal.Cost, t.Rates, c.Bank) || c.Total(s) > 7) && TradeToward(c, s, goal.Cost, t) is { } err)
                return err == "" ? null : err;
        }

        // Шлях із надлишку — подовжити свій найдовший (Чумацький шлях), коли не з'їдає того, чого бракує цілі.
        if (c.WhyNot(s, "road") is null && Missing(Sub(hand, KhutoryCore.RoadCost), goal.Cost).Count == missing.Count
            && RoadForLongest(c, s) is { } longer)
            return c.PlaceRoad(s, longer);

        // Грамота з надлишку — коли не з'їдає того, чого бракує цілі (або рука загрубла).
        if (c.WhyNot(s, "dev") is null)
        {
            var after = Sub(hand, KhutoryCore.DevCost);
            if (Missing(after, goal.Cost).Count == missing.Count || c.Total(s) > 7) return c.Buy(s);
        }
        return c.End(s);
    }

    /// <summary>Чесна пропозиція під ціль: 1 надлишку за 1 потрібного, або 2 за 1, коли надлишку багато.</summary>
    static (int[] give, int[] get)? OfferFor(KhutoryCore c, int s, Think t, Goal goal)
    {
        var hand = c.Hand[s];
        var miss = t.Hardest(Missing(hand, goal.Cost));
        if (miss.Count == 0) return null;
        var get = miss[0];
        var giveR = -1;
        var spare = 0;
        for (var r = 0; r < 5; r++)
        {
            if (r == get) continue;
            var sp = hand[r] - goal.Cost[r];
            if (sp > spare || (sp == spare && sp > 0 && giveR >= 0 && t.Rates[r] > t.Rates[giveR])) { spare = sp; giveR = r; }
        }
        if (giveR < 0 || t.Rates[giveR] <= 2) return null;   // пристань 2:1 — простіше з банком
        var give = new int[5];
        var want = new int[5];
        give[giveR] = spare >= 3 ? 2 : 1;
        want[get] = 1;
        var after = Sub(hand, give);
        after[get]++;
        return t.Value(after) > t.Value(hand) + 1e-9 ? (give, want) : null;
    }

    /// <summary>Один обмін із банком під ціль найкращим курсом: "" — обміняв, null — нема чого.</summary>
    static string? TradeToward(KhutoryCore c, int s, int[] cost, Think t)
    {
        var hand = c.Hand[s];
        var miss = t.Hardest(Missing(hand, cost)).Where(r => c.Bank[r] > 0).ToList();
        if (miss.Count == 0) return null;
        var give = -1;
        var best = int.MaxValue;
        var bestSpare = 0;
        for (var r = 0; r < 5; r++)
        {
            if (r == miss[0] || hand[r] - cost[r] < t.Rates[r]) continue;
            var spare = hand[r] - cost[r];
            if (t.Rates[r] < best || (t.Rates[r] == best && spare > bestSpare)) { best = t.Rates[r]; bestSpare = spare; give = r; }
        }
        if (give < 0) return null;
        return c.BankTrade(s, give, miss[0], 1) ?? "";
    }

    // =========================================================================================
    // Відповіді на пропозиції
    // =========================================================================================

    static bool Leader(KhutoryCore c, int o) => c.PublicPoints(o) >= c.Target - 2;

    static bool AllAnswered(KhutoryCore c, KhOffer o)
    {
        for (var x = 0; x < KhutoryCore.MaxSeats; x++)
            if (c.Addressed(o, x) && !o.Acc.Contains(x) && !o.Dec.Contains(x)) return false;
        return true;
    }

    /// <summary>Так — якщо обмін наближає ціль Глека, чесний і не дає перемогти тому, хто за 2 очки від неї.</summary>
    public static bool Accepts(KhutoryCore c, int s, KhOffer o)
    {
        if (Leader(c, o.From)) return false;
        var hand = c.Hand[s];
        if (!KhutoryCore.Has(hand, o.Get)) return false;
        if (o.Get.Sum() > o.Give.Sum() + 1) return false;
        var after = Sub(hand, o.Get);
        for (var r = 0; r < 5; r++) after[r] += o.Give[r];
        var t = new Think(c, s);
        return t.Value(after) > t.Value(hand) + 1e-6;
    }

    // =========================================================================================
    // Ціль
    // =========================================================================================

    sealed record Goal(int Kind, int[] Cost, double Weight);

    /// <summary>Що Глек знає про себе на цей крок: виробіток за коло, курси, можливі цілі.</summary>
    sealed class Think
    {
        public readonly double[] Prod;
        public readonly int[] Rates;
        public readonly List<Goal> Goals = [];
        readonly int[] _bank;

        public Think(KhutoryCore c, int s)
        {
            Prod = Production(c, s);
            Rates = c.Rates(s);
            _bank = c.Bank;
            if (c.CitiesLeft(s) > 0 && c.CitySpots(s) is { Count: > 0 } cs)
                Goals.Add(new Goal(City, KhutoryCore.CityCost, 1.0 + 0.02 * cs.Max(v => VertexPips(c, v))));
            if (c.TownsLeft(s) > 0)
            {
                var ts = c.TownSpots(s);
                if (ts.Count > 0) Goals.Add(new Goal(TownGoal, KhutoryCore.TownCost, 1.05 + 0.02 * ts.Max(v => VertexPips(c, v))));
                else if (c.RoadsLeft(s) > 0 && TowardTown(c, s) is not null) Goals.Add(new Goal(RoadGoal, KhutoryCore.RoadCost, 0.4));
            }
            if (c.Deck.Count > 0)
            {
                var chase = c.Army != s && c.Knights[s] + c.Devs[s].Count(d => d.K == KhutoryCore.Knight) >= 1;
                Goals.Add(new Goal(DevGoal, KhutoryCore.DevCost, chase ? 0.4 : 0.3));
            }
        }

        public Goal? Best(int[] hand)
        {
            Goal? best = null;
            var bestScore = double.MinValue;
            foreach (var g in Goals)
            {
                var sc = Score(g, hand);
                if (sc > bestScore) { bestScore = sc; best = g; }
            }
            return best;
        }

        public double Value(int[] hand) => Goals.Count == 0 ? 0 : Goals.Max(g => Score(g, hand));

        double Score(Goal g, int[] hand) => g.Weight / (1 + Eta(hand, g.Cost));

        /// <summary>
        /// Скільки кіл до цілі: бракує — покриваємо обміном надлишку (найважчі ресурси першими), решту — виробітком
        /// (свого ресурсу або чужих через банк). Кожен обмін — чверть кола штрафу.
        /// </summary>
        double Eta(int[] hand, int[] cost)
        {
            var miss = new double[5];
            var conv = 0;
            for (var r = 0; r < 5; r++)
            {
                if (hand[r] < cost[r])
                {
                    miss[r] = cost[r] - hand[r];
                    if (_bank[r] < miss[r]) miss[r] += 3;   // банк порожній — чекати довго
                }
                else conv += (hand[r] - cost[r]) / Rates[r];
            }
            var total = miss.Sum();
            if (total == 0) return 0;
            var penalty = 0.0;
            foreach (var r in Enumerable.Range(0, 5).OrderBy(r => Prod[r]))
                while (conv > 0 && miss[r] > 0) { miss[r]--; conv--; penalty += 0.25; }
            var all = Prod.Sum();
            var eta = 0.0;
            for (var r = 0; r < 5; r++)
                if (miss[r] > 0) eta = Math.Max(eta, miss[r] / (Prod[r] + (all - Prod[r]) * 0.25 + 0.05));
            return eta + penalty;
        }

        /// <summary>Ресурси, яких бракує, — спершу ті, що родять найгірше.</summary>
        public List<int> Hardest(List<int> missing) => [.. missing.OrderBy(r => Prod[r])];
    }

    /// <summary>Очікуваний виробіток за коло (усі кидки столу), без гекса з Кармелюком.</summary>
    static double[] Production(KhutoryCore c, int s)
    {
        var p = new double[5];
        for (var v = 0; v < KhutoryGeo.Verts; v++)
        {
            if (c.VOwner[v] != s) continue;
            var mult = c.VCity[v] ? 2 : 1;
            foreach (var h in G.VH[v])
                if (h != c.Robber && c.B.ResOf(h) is var r and >= 0) p[r] += c.Seats * mult * KhutoryBoard.Pips(c.B.Num[h]) / 36.0;
        }
        return p;
    }

    static int VertexPips(KhutoryCore c, int v)
    {
        var n = 0;
        foreach (var h in G.VH[v]) if (c.B.ResOf(h) >= 0) n += KhutoryBoard.Pips(c.B.Num[h]);
        return n;
    }

    // =========================================================================================
    // Вершини
    // =========================================================================================

    /// <summary>Свій виробіток у крапках за ресурсом.</summary>
    static int[] OwnPips(KhutoryCore c, int s)
    {
        var p = new int[5];
        for (var v = 0; v < KhutoryGeo.Verts; v++)
            if (c.VOwner[v] == s)
                foreach (var h in G.VH[v])
                    if (c.B.ResOf(h) is var r and >= 0) p[r] += KhutoryBoard.Pips(c.B.Num[h]) * (c.VCity[v] ? 2 : 1);
        return p;
    }

    /// <summary>
    /// Оцінка вершини: крапки, бонус за ресурс, якого ще нема, пристань — під свій надлишок (2:1 свого ресурсу
    /// дорожча, коли його родиться багато). Рідкість ресурсу й дорожчі пристані пробували — на самогрі слабше.
    /// </summary>
    static double VScore(KhutoryCore c, int v, int[] own)
    {
        var seen = new HashSet<int>();
        var here = new int[5];
        double score = 0;
        foreach (var h in G.VH[v])
        {
            var r = c.B.ResOf(h);
            if (r < 0) continue;
            var pips = KhutoryBoard.Pips(c.B.Num[h]);
            here[r] += pips;
            score += h == c.Robber ? pips * 0.5 : pips;
            if (seen.Add(r)) score += own[r] == 0 ? 2.0 : 0.6;
        }
        for (var i = 0; i < c.B.PortEdge.Length; i++)
        {
            if (!G.Edges[c.B.PortEdge[i]].Contains(v)) continue;
            var k = c.B.PortKind[i];
            if (k == KhutoryBoard.AnyPort) score += 0.7;
            else score += own[k] + here[k] >= 6 ? 1.5 : 0.7;
        }
        return score;
    }

    static int BestTown(KhutoryCore c, int s, List<int> spots)
    {
        var own = OwnPips(c, s);
        var best = -1;
        var bestScore = double.MinValue;
        foreach (var v in spots)
        {
            var sc = VScore(c, v, own);
            if (sc > bestScore) { bestScore = sc; best = v; }
        }
        return best;
    }

    /// <summary>Шлях від щойно поставленої хати — у бік найкращої вільної вершини на два кроки.</summary>
    static int SetupRoad(KhutoryCore c, int s)
    {
        var own = OwnPips(c, s);
        var best = -1;
        var bestScore = double.MinValue;
        foreach (var e in c.RoadSpots(s))
        {
            var w = G.Other(e, c.SetupTown);
            double sc = 0;
            foreach (var x in G.VNb[w])
                if (x != c.SetupTown && c.TownFree(x)) sc = Math.Max(sc, VScore(c, x, own));
            if (sc > bestScore) { bestScore = sc; best = e; }
        }
        return best;
    }

    /// <summary>Перше ребро до найкращої вільної вершини для хати (ушир від своєї мережі, ≤ 4 ребер). null — нікуди.</summary>
    static int? TowardTown(KhutoryCore c, int s)
    {
        var own = OwnPips(c, s);
        var dist = new int[KhutoryGeo.Verts];
        var first = new int[KhutoryGeo.Verts];
        Array.Fill(dist, -1);
        Array.Fill(first, -1);
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
                    var sc = VScore(c, w, own) - 3.0 * dist[w];
                    if (sc > bestScore) { bestScore = sc; best = first[w]; }
                }
                if (c.VOwner[w] < 0) queue.Enqueue(w);
            }
        }
        return best;
    }

    /// <summary>
    /// Ребро, що подовжує свій найдовший шлях (у бік хати — краще), — коли Чумацький шлях ще не свій із запасом.
    /// null — не треба або нема такого.
    /// </summary>
    static int? RoadForLongest(KhutoryCore c, int s)
    {
        if (c.RoadsLeft(s) <= 1) return null;
        var cur = c.RoadLen[s];
        var others = c.Order.Where(o => o != s).Select(o => c.RoadLen[o]).DefaultIfEmpty(0).Max();
        if (c.Longest == s && cur > others + 1) return null;
        var toward = TowardTown(c, s);
        int? best = null;
        var bestLen = cur;
        foreach (var e in c.RoadSpots(s))
        {
            c.EOwner[e] = s;
            var len = c.LongestRoad(s);
            c.EOwner[e] = -1;
            if (len > bestLen || (len == bestLen && len > cur && e == toward)) { bestLen = len; best = e; }
        }
        return best;
    }

    // =========================================================================================
    // Сімка, Кармелюк, козак
    // =========================================================================================

    /// <summary>Скинути надлишок поза ціллю: спершу те, чого понад ціль найбільше.</summary>
    static int[] DiscardPick(KhutoryCore c, int s, int need)
    {
        var cost = new Think(c, s).Best(c.Hand[s])?.Cost ?? new int[5];
        var left = (int[])c.Hand[s].Clone();
        var drop = new int[5];
        for (var i = 0; i < need; i++)
        {
            var r = -1;
            for (var x = 0; x < 5; x++)
            {
                if (left[x] == 0) continue;
                if (r < 0 || left[x] - cost[x] > left[r] - cost[r] || (left[x] - cost[x] == left[r] - cost[r] && left[x] > left[r])) r = x;
            }
            left[r]--;
            drop[r]++;
        }
        return drop;
    }

    /// <summary>Лідер серед суперників: відкриті очки, далі — більше карт.</summary>
    static int LeaderOf(KhutoryCore c, int s) =>
        c.Order.Where(o => o != s).OrderByDescending(c.PublicPoints).ThenByDescending(c.Total).First();

    /// <summary>Кармелюк — на найродючіший гекс лідера, не на свій.</summary>
    static int RobberHex(KhutoryCore c, int s)
    {
        var lead = LeaderOf(c, s);
        var spots = c.RobberSpots(s);
        var best = spots[0];
        var bestScore = double.MinValue;
        foreach (var h in spots)
        {
            double sc = 0;
            var pips = c.B.ResOf(h) >= 0 ? KhutoryBoard.Pips(c.B.Num[h]) : 0;
            foreach (var v in G.HexV[h])
            {
                var o = c.VOwner[v];
                if (o < 0) continue;
                var mult = c.VCity[v] ? 2 : 1;
                if (o == s) sc -= 1000;
                else sc += pips * mult * (o == lead ? 10 : 1) + (c.Total(o) > 0 ? 0.5 : 0);
            }
            if (sc > bestScore) { bestScore = sc; best = h; }
        }
        return best;
    }

    /// <summary>Красти в лідера (відкриті очки), далі — в того, в кого більше карт.</summary>
    static int Victim(KhutoryCore c, List<int> from) =>
        from.OrderByDescending(o => c.PublicPoints(o)).ThenByDescending(o => c.Total(o)).First();

    /// <summary>
    /// Козак — коли Кармелюк сидить на родючому гексі біля своєї хати або щоб забрати Найсильнішу сотню: готовий
    /// козак іде в сотню одразу (до трьох — шлях до сотні), поки сотня не своя з запасом.
    /// </summary>
    static bool WantKnight(KhutoryCore c, int s)
    {
        if (c.PlayedDev || !c.Devs[s].Any(d => d.K == KhutoryCore.Knight && !d.Fresh)) return false;
        if (c.B.ResOf(c.Robber) >= 0 && c.OwnersAt(c.Robber).Contains(s)) return true;
        if (c.Army != s) return true;
        var most = c.Order.Where(o => o != s).Select(o => c.Knights[o]).DefaultIfEmpty(0).Max();
        return c.Knights[s] <= most + 1;
    }

    // =========================================================================================
    // Дрібне
    // =========================================================================================

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

    static List<int> Missing(int[] hand, int[] cost)
    {
        var list = new List<int>();
        for (var r = 0; r < 5; r++)
            for (var n = hand[r]; n < cost[r]; n++) list.Add(r);
        return list;
    }

    static int[] Sub(int[] a, int[] b)
    {
        var x = new int[5];
        for (var r = 0; r < 5; r++) x[r] = a[r] - b[r];
        return x;
    }

    static int Others(KhutoryCore c, int s, int r)
    {
        var n = 0;
        for (var o = 0; o < KhutoryCore.MaxSeats; o++) if (o != s && c.In[o]) n += c.Hand[o][r];
        return n;
    }
}
