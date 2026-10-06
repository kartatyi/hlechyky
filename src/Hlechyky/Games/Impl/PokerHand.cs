namespace Hlechyky.Games.Impl;

/// <summary>
/// Карти й оцінювач рук покеру. Карта — int 0..51: ранг = c / 4 (0 — двійка … 12 — туз), масть = c % 4 (♠♥♦♣).
/// На дроті карта — рядок «As», «Td», «7c» (ранг 23456789TJQKA + масть shdc).
/// Оцінка 5–7 карт — int, більше = краще: категорія (0 старша карта … 8 стрит-флеш) у старших бітах, далі до п'яти
/// рангів-кікерів по 4 біти. Роял-флеш — стрит-флеш до туза.
/// </summary>
public static class PokerHand
{
    public const string Ranks = "23456789TJQKA";
    public const string Suits = "shdc";

    public const int HighCard = 0, Pair = 1, TwoPair = 2, Trips = 3, Straight = 4, Flush = 5, FullHouse = 6, Quads = 7, StraightFlush = 8;

    public static int Rank(int card) => card >> 2;
    public static int Suit(int card) => card & 3;
    public static string Code(int card) => $"{Ranks[card >> 2]}{Suits[card & 3]}";

    public static int Parse(string code)
    {
        var r = Ranks.IndexOf(char.ToUpperInvariant(code[0]));
        var s = Suits.IndexOf(char.ToLowerInvariant(code[1]));
        if (code.Length != 2 || r < 0 || s < 0) throw new ArgumentException($"не карта: {code}");
        return r * 4 + s;
    }

    public static int[] ParseMany(string codes) =>
        codes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Parse).ToArray();

    public static int Category(int score) => score >> 20;

    static int Make(int cat, int a = 0, int b = 0, int c = 0, int d = 0, int e = 0) =>
        (cat << 20) | (a << 16) | (b << 12) | (c << 8) | (d << 4) | e;

    /// <summary>Найстарша карта стриту в масці рангів (колесо A-2-3-4-5 — до п'ятірки, 3), або -1.</summary>
    static int StraightHigh(int mask)
    {
        for (var hi = 12; hi >= 4; hi--)
            if (((mask >> (hi - 4)) & 0x1F) == 0x1F) return hi;
        return (mask & 0x100F) == 0x100F ? 3 : -1;
    }

    /// <summary>Оцінка найкращої п'ятірки з 5–7 карт.</summary>
    public static int Eval(ReadOnlySpan<int> cards)
    {
        Span<int> cnt = stackalloc int[13];
        Span<int> suitMask = stackalloc int[4];
        var all = 0;
        foreach (var c in cards)
        {
            cnt[c >> 2]++;
            suitMask[c & 3] |= 1 << (c >> 2);
            all |= 1 << (c >> 2);
        }
        var flush = -1;
        for (var s = 0; s < 4; s++)
            if (System.Numerics.BitOperations.PopCount((uint)suitMask[s]) >= 5) flush = s;
        if (flush >= 0 && StraightHigh(suitMask[flush]) is var sf and >= 0) return Make(StraightFlush, sf);

        int quad = -1, t1 = -1, t2 = -1, p1 = -1, p2 = -1, p3 = -1;
        for (var r = 12; r >= 0; r--)
        {
            switch (cnt[r])
            {
                case 4: quad = r; break;
                case 3: if (t1 < 0) t1 = r; else if (t2 < 0) t2 = r; break;
                case 2: if (p1 < 0) p1 = r; else if (p2 < 0) p2 = r; else if (p3 < 0) p3 = r; break;
            }
        }
        if (quad >= 0) return Make(Quads, quad, Top(cnt, 1, quad));
        if (t1 >= 0 && (t2 >= 0 || p1 >= 0)) return Make(FullHouse, t1, Math.Max(t2, p1));
        if (flush >= 0)
        {
            var m = suitMask[flush];
            Span<int> k = stackalloc int[5];
            var n = 0;
            for (var r = 12; r >= 0 && n < 5; r--) if ((m & (1 << r)) != 0) k[n++] = r;
            return Make(Flush, k[0], k[1], k[2], k[3], k[4]);
        }
        if (StraightHigh(all) is var st and >= 0) return Make(Straight, st);
        if (t1 >= 0)
        {
            var (a, b, _) = Kickers(cnt, t1, -1);
            return Make(Trips, t1, a, b);
        }
        if (p2 >= 0)
        {
            var (a, _, _) = Kickers(cnt, p1, p2);
            return Make(TwoPair, p1, p2, a);
        }
        if (p1 >= 0)
        {
            var (a, b, c) = Kickers(cnt, p1, -1);
            return Make(Pair, p1, a, b, c);
        }
        Span<int> h = stackalloc int[5];
        var hn = 0;
        for (var r = 12; r >= 0 && hn < 5; r--) if (cnt[r] > 0) h[hn++] = r;
        return Make(HighCard, h[0], h[1], h[2], h[3], h[4]);
    }

    /// <summary>Найстарший ранг, що є серед карт і не дорівнює except.</summary>
    static int Top(ReadOnlySpan<int> cnt, int _, int except)
    {
        for (var r = 12; r >= 0; r--) if (r != except && cnt[r] > 0) return r;
        return 0;
    }

    /// <summary>До трьох найстарших рангів-кікерів, крім x і y (третя пара теж іде в кікери).</summary>
    static (int, int, int) Kickers(ReadOnlySpan<int> cnt, int x, int y)
    {
        Span<int> k = stackalloc int[3];
        var n = 0;
        for (var r = 12; r >= 0 && n < 3; r--) if (r != x && r != y && cnt[r] > 0) k[n++] = r;
        return (k[0], k[1], k[2]);
    }

    public static int Eval(IReadOnlyList<int> cards)
    {
        Span<int> s = stackalloc int[cards.Count];
        for (var i = 0; i < cards.Count; i++) s[i] = cards[i];
        return Eval(s);
    }

    /// <summary>Найкраща п'ятірка з 5–7 карт (для підсвітки): перебір C(7,5) = 21.</summary>
    public static int[] BestFive(IReadOnlyList<int> cards)
    {
        if (cards.Count <= 5) return [.. cards];
        var best = -1;
        int[] pick = [];
        Span<int> five = stackalloc int[5];
        var n = cards.Count;
        for (var a = 0; a < n; a++)
        for (var b = a + 1; b < n; b++)
        {
            // викидаємо дві: a і b
            var k = 0;
            for (var i = 0; i < n; i++) if (i != a && i != b && k < 5) five[k++] = cards[i];
            if (k < 5) continue;
            var sc = Eval(five);
            if (sc > best) { best = sc; pick = five.ToArray(); }
        }
        return pick;
    }

    // ---------- назви українською ----------

    static readonly string[] One = ["двійка", "трійка", "четвірка", "п'ятірка", "шістка", "сімка", "вісімка", "дев'ятка", "десятка", "валет", "дама", "король", "туз"];
    static readonly string[] Many = ["двійки", "трійки", "четвірки", "п'ятірки", "шістки", "сімки", "вісімки", "дев'ятки", "десятки", "валети", "дами", "королі", "тузи"];
    static readonly string[] OnMany = ["двійках", "трійках", "четвірках", "п'ятірках", "шістках", "сімках", "вісімках", "дев'ятках", "десятках", "валетах", "дамах", "королях", "тузах"];
    static readonly string[] UpTo = ["двійки", "трійки", "четвірки", "п'ятірки", "шістки", "сімки", "вісімки", "дев'ятки", "десятки", "валета", "дами", "короля", "туза"];

    static readonly string[] Cats = ["старша карта", "пара", "дві пари", "трійка", "стрит", "флеш", "фул-хаус", "каре", "стрит-флеш"];

    /// <summary>Коротко: «фул-хаус», «роял-флеш».</summary>
    public static string Short(int score)
    {
        var cat = Category(score);
        return cat == StraightFlush && ((score >> 16) & 15) == 12 ? "роял-флеш" : Cats[cat];
    }

    /// <summary>Повно: «фул-хаус, трійки на королях», «стрит до дами», «дві пари: королі й сімки».</summary>
    public static string Name(int score)
    {
        int a = (score >> 16) & 15, b = (score >> 12) & 15;
        return Category(score) switch
        {
            HighCard => $"старша карта — {One[a]}",
            Pair => $"пара: {Many[a]}",
            TwoPair => $"дві пари: {Many[a]} й {Many[b]}",
            Trips => $"трійка: {Many[a]}",
            Straight => $"стрит до {UpTo[a]}",
            Flush => $"флеш, старша — {One[a]}",
            FullHouse => $"фул-хаус, {Many[a]} на {OnMany[b]}",
            Quads => $"каре: {Many[a]}",
            _ => a == 12 ? "роял-флеш" : $"стрит-флеш до {UpTo[a]}",
        };
    }
}
