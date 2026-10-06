namespace Hlechyky.Games.Impl;

/// <summary>
/// Глек 🤖 за покерним столом (лише «на інтерес»): префлоп — формула Чена (групи рук), постфлоп — шанси Монте-Карло
/// проти стількох випадкових рук, скільки суперників у роздачі; далі шанси банку, трохи блефу й випадковості з
/// <c>Ctx.Rng</c>. Легкий — частіше коллить і помиляється. Грає пристойно, а не ідеально.
/// </summary>
public static class PokerBot
{
    /// <summary>Сила стартової руки за Ченом: −1…20.</summary>
    public static double Chen(int[] hole)
    {
        int a = PokerHand.Rank(hole[0]), b = PokerHand.Rank(hole[1]);
        if (b > a) (a, b) = (b, a);
        static double High(int r) => r switch { 12 => 10, 11 => 8, 10 => 7, 9 => 6, _ => (r + 2) / 2.0 };
        var score = High(a);
        if (a == b) return Math.Max(5, score * 2);
        if (PokerHand.Suit(hole[0]) == PokerHand.Suit(hole[1])) score += 2;
        var gap = a - b - 1;
        score -= gap switch { 0 => 0, 1 => 1, 2 => 2, 3 => 4, _ => 5 };
        if (gap <= 1 && a < 10) score += 1;
        return Math.Ceiling(score);
    }

    /// <summary>
    /// Частка банку, яку в середньому бере кожна з рук <paramref name="hands"/> (відомі карти) при дошці
    /// <paramref name="board"/>, плюс <paramref name="randomOpps"/> випадкових рук. Монте-Карло на <paramref name="samples"/> прогонів.
    /// </summary>
    public static double[] Equity(IReadOnlyList<int[]> hands, IReadOnlyList<int> board, int randomOpps, Random rng, int samples)
    {
        var used = new bool[52];
        foreach (var h in hands) foreach (var c in h) used[c] = true;
        foreach (var c in board) used[c] = true;
        var rest = Enumerable.Range(0, 52).Where(c => !used[c]).ToArray();
        var eq = new double[hands.Count];
        var need = 5 - board.Count + randomOpps * 2;
        Span<int> seven = stackalloc int[7];
        var scores = new int[hands.Count + randomOpps];
        for (var s = 0; s < samples; s++)
        {
            // частковий Фішер-Єйтс: перші need карт — випадкові
            for (var i = 0; i < need && i < rest.Length; i++)
            {
                var j = i + rng.Next(rest.Length - i);
                (rest[i], rest[j]) = (rest[j], rest[i]);
            }
            var k = 0;
            var full = new int[5];
            for (var i = 0; i < board.Count; i++) full[i] = board[i];
            for (var i = board.Count; i < 5; i++) full[i] = rest[k++];
            var best = -1;
            for (var h = 0; h < scores.Length; h++)
            {
                int c0, c1;
                if (h < hands.Count) { c0 = hands[h][0]; c1 = hands[h][1]; }
                else { c0 = rest[k++]; c1 = rest[k++]; }
                seven[0] = c0; seven[1] = c1;
                for (var i = 0; i < 5; i++) seven[2 + i] = full[i];
                scores[h] = PokerHand.Eval(seven);
                if (scores[h] > best) best = scores[h];
            }
            var n = 0;
            for (var h = 0; h < scores.Length; h++) if (scores[h] == best) n++;
            for (var h = 0; h < hands.Count; h++) if (scores[h] == best) eq[h] += 1.0 / n;
        }
        for (var h = 0; h < eq.Length; h++) eq[h] /= Math.Max(1, samples);
        return eq;
    }

    /// <summary>Хід бота: дія й (для raise) сума «до».</summary>
    public static (string Action, int To) Decide(PokerCore c, int p, Random rng, bool easy)
    {
        if (c.Legal(p) is not { } legal) return ("fold", 0);
        var opps = Math.Max(1, c.NotFolded - 1);
        double eq;
        if (c.Board.Count == 0)
        {
            var chen = (PokerBot.Chen(c.Hole[p]) + 1) / 21.0;
            eq = Math.Pow(0.3 + 0.55 * chen, 1 + 0.35 * (opps - 1));
        }
        else eq = Equity([c.Hole[p]], c.Board, opps, rng, 160)[0];
        eq += (rng.NextDouble() - 0.5) * (easy ? 0.3 : 0.1);

        var pot = c.Total.Sum();
        var toCall = legal.Call;
        var odds = toCall == 0 ? 0 : toCall / (double)(pot + toCall);
        var canRaise = legal.RaiseMax > 0;
        int Size(double frac)
        {
            var to = c.CurrentBet + Math.Max(c.MinRaise, (int)(pot * frac));
            to = Math.Clamp(to, legal.RaiseMin, legal.RaiseMax);
            return to;
        }
        var strong = 1.0 / (opps + 1) + (easy ? 0.3 : 0.22);
        if (toCall == 0)
        {
            var bluff = !easy && rng.NextDouble() < 0.07;
            if (canRaise && (eq > strong || bluff) && rng.NextDouble() < 0.8)
            {
                var to = Size(0.5 + rng.NextDouble() * 0.3);
                return to >= legal.RaiseMax ? ("allin", 0) : ("raise", to);
            }
            return ("check", 0);
        }
        if (canRaise && eq > strong + 0.1 && rng.NextDouble() < (easy ? 0.3 : 0.55))
        {
            var to = Size(0.7 + rng.NextDouble() * 0.4);
            return to >= legal.RaiseMax ? ("allin", 0) : ("raise", to);
        }
        if (eq >= odds + (easy ? -0.05 : 0.04)) return ("call", 0);
        if (easy && rng.NextDouble() < 0.2) return ("call", 0);
        return ("fold", 0);
    }
}
