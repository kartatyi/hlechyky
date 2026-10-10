using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Правила «Двадцять одно в Глека» (docs/games/specs/blackjack.md §2) — без каркаса: карти, очки, колода з seed, роздача,
/// дії гравця, гра Глека, розрахунок рук. Ті самі класи грають і за столом, і в симуляції RTP (тести), тож відсоток у
/// spec — це відсоток саме цього коду.
/// <para>
/// Правила (рішення 10.10.2026, RTP базової стратегії ≈ 96,7 % — §2.6): 6 колод, нова колода на кожну роздачу; блекджек
/// платить 1:1; Глек добирає на м'яких 17 (H17), заглядає під туза й десятку (з блекджеком — роздача одразу кінчається,
/// програють лише ставки, і блекджек гравця теж: «двадцять одно на двадцять одно — старшинство за Глеком»); подвоєння —
/// на перших двох картах і лише на жорстких 9, 10, 11, після спліту — ні; спліт пари однакових карт до трьох рук; розбиті
/// тузи — по одній карті, далі не б'ються; 21 після спліту — не блекджек; страховки й «здатись» нема.
/// </para>
/// </summary>
public static class BlackjackCore
{
    public const int Decks = 6, CardsInShoe = 52 * Decks;
    /// <summary>Скільки рук може стати з одного боксу (спліт і ще один спліт).</summary>
    public const int MaxHands = 3;
    /// <summary>Блекджек платить BjWin : BjOf (1:1 — ставка повертається вдвічі).</summary>
    public const int BjWin = 1, BjOf = 1;
    /// <summary>Глек добирає на м'яких 17.</summary>
    public const bool HitSoft17 = true;
    /// <summary>Жорсткі суми, на яких можна подвоїти (лише перші дві карти, не після спліту).</summary>
    public static readonly int[] DoubleOn = [9, 10, 11];

    public const string Ranks = "A23456789TJQK", Suits = "shdc";

    /// <summary>Усі 52 коди карт: «As», «Td» — як у покері (ранг, масть).</summary>
    public static readonly string[] Codes = BuildCodes();

    static string[] BuildCodes()
    {
        var all = new string[52];
        for (var i = 0; i < 52; i++) all[i] = string.Concat(Ranks[i % 13], Suits[i / 13]);
        return all;
    }

    /// <summary>Чи це код карти («As», «Td»).</summary>
    public static bool IsCard(string? c) => c is { Length: 2 } && Ranks.Contains(c[0]) && Suits.Contains(c[1]);

    /// <summary>Вага карти: туз — 1 (м'якість рахує <see cref="Score"/>), 10/J/Q/K — 10.</summary>
    public static int Value(string c) => c[0] switch
    {
        'A' => 1,
        'T' or 'J' or 'Q' or 'K' => 10,
        var r => r - '0',
    };

    /// <summary>Очки руки: найкраща сума ≤ 21 (туз як 11, якщо влазить) і чи вона м'яка.</summary>
    public static (int Total, bool Soft) Score(IReadOnlyList<string> cards)
    {
        int hard = 0;
        var ace = false;
        foreach (var c in cards)
        {
            var v = Value(c);
            hard += v;
            if (v == 1) ace = true;
        }
        return ace && hard + 10 <= 21 ? (hard + 10, true) : (hard, false);
    }

    public static int Total(IReadOnlyList<string> cards) => Score(cards).Total;

    /// <summary>Блекджек: дві перші карти на 21 (після спліту — ні, це перевіряє рука).</summary>
    public static bool Natural(IReadOnlyList<string> cards) => cards.Count == 2 && Total(cards) == 21;

    /// <summary>Глек добирає: менше 17, або м'які 17 (H17).</summary>
    public static bool DealerHits(IReadOnlyList<string> cards)
    {
        var (t, soft) = Score(cards);
        return t < 17 || (HitSoft17 && t == 17 && soft);
    }

    /// <summary>Чи Глек заглядає під відкриту карту (туз чи десятка).</summary>
    public static bool Peeks(string up) => Value(up) is 1 or 10;

    // ---------- чесна колода ----------

    /// <summary>Новий seed: 32 байти hex (64 символи) з <paramref name="rng"/> (тести) чи криптографічного генератора.</summary>
    public static string NewSeed(Random? rng)
    {
        var bytes = new byte[32];
        if (rng is null) RandomNumberGenerator.Fill(bytes);
        else rng.NextBytes(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>sha256 від UTF-8 рядка, hex малими.</summary>
    public static string Hash(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    /// <summary>
    /// Колода роздачі з seed (§4): 312 карт (6 колод; карта i — <c>Codes[i % 52]</c>), упорядковані за
    /// <c>sha256(seed + ":" + i)</c> (hex, порядок рядків), рівні — за i. Браузер (ⓘ) повторює те саме.
    /// </summary>
    public static string[] Shoe(string seed)
    {
        var keys = new (string Key, int I)[CardsInShoe];
        for (var i = 0; i < CardsInShoe; i++) keys[i] = (Hash(seed + ":" + i), i);
        Array.Sort(keys, (a, b) =>
        {
            var c = string.CompareOrdinal(a.Key, b.Key);
            return c != 0 ? c : a.I.CompareTo(b.I);
        });
        var shoe = new string[CardsInShoe];
        for (var k = 0; k < CardsInShoe; k++) shoe[k] = Codes[keys[k].I % 52];
        return shoe;
    }

    /// <summary>Повернення руки за підсумком: 0 — програш, ставка — нічия, удвічі — виграш (блекджек 1:1 теж).</summary>
    public static long Pays(string outcome, int stake) => outcome switch
    {
        "bj" => stake + (long)stake * BjWin / BjOf,
        "win" => 2L * stake,
        "push" => stake,
        _ => 0,
    };
}

/// <summary>Одна рука гравця: карти, ставка, подвоєння/спліт, підсумок.</summary>
public sealed class BjHand
{
    public List<string> Cards { get; set; } = [];
    /// <summary>Ставка на руку (без подвоєння).</summary>
    public int Bet { get; set; }
    public bool Doubled { get; set; }
    /// <summary>Рука зі спліту — блекджеку й подвоєння нема.</summary>
    public bool Split { get; set; }
    /// <summary>Розбиті тузи: по одній карті, далі рука не ходить.</summary>
    public bool Aces { get; set; }
    public bool Done { get; set; }
    /// <summary>Підсумок: bj | win | push | lose | bust; null — ще не розраховано.</summary>
    public string? Outcome { get; set; }
    public int Return { get; set; }

    [JsonIgnore] public int Stake => Doubled ? Bet * 2 : Bet;
    [JsonIgnore] public bool IsNatural => !Split && BlackjackCore.Natural(Cards);
    [JsonIgnore] public bool Bust => BlackjackCore.Total(Cards) > 21;
}

/// <summary>Бокс гравця на роздачу: нік, колір (місце), базова ставка й руки (після спліту — до трьох).</summary>
public sealed class BjBox
{
    public string Nick { get; set; } = "";
    public int Color { get; set; }
    public int Bet { get; set; }
    public List<BjHand> Hands { get; set; } = [];

    [JsonIgnore] public int Staked => (int)Math.Min(int.MaxValue, Hands.Sum(h => (long)h.Stake));
    [JsonIgnore] public int Returned => (int)Math.Min(int.MaxValue, Hands.Sum(h => (long)h.Return));
}

/// <summary>
/// Роздача: бокси, карти Глека, чий хід. Карти беруться функцією <c>draw</c> (за столом — з колоди seed, у симуляції — з
/// випадкової). Увесь стан — публічні властивості (JSON, перезапуск сайту).
/// </summary>
public sealed class BjDeal
{
    public const string Hit = "hit", Stand = "stand", Double = "double", Split = "split";

    public List<BjBox> Boxes { get; set; } = [];
    /// <summary>Карти Глека: [0] — відкрита, [1] — закрита (поки <see cref="HoleOpen"/> false).</summary>
    public List<string> Dealer { get; set; } = [];
    public bool HoleOpen { get; set; }
    /// <summary>Глек підглянув і має блекджек: роздача скінчилась одразу.</summary>
    public bool DealerBj { get; set; }
    /// <summary>Чий хід: бокс і рука; -1 — гравці своє відходили.</summary>
    public int Box { get; set; } = -1;
    public int Hand { get; set; } = -1;
    public bool Settled { get; set; }

    [JsonIgnore] public bool PlayersDone => Box < 0;
    [JsonIgnore] public BjHand? Current => Box >= 0 ? Boxes[Box].Hands[Hand] : null;

    /// <summary>
    /// Роздати: по карті кожному боксу, відкрита Глекові, по другій кожному, закрита Глекові. Глек заглядає під туза й
    /// десятку: блекджек — роздачі кінець (<see cref="DealerBj"/>). Блекджеки гравців стоять самі.
    /// </summary>
    public static BjDeal Start(IEnumerable<(string Nick, int Color, int Bet)> boxes, Func<string> draw)
    {
        var deal = new BjDeal();
        foreach (var (nick, color, bet) in boxes)
            deal.Boxes.Add(new BjBox { Nick = nick, Color = color, Bet = bet, Hands = [new BjHand { Bet = bet }] });
        if (deal.Boxes.Count == 0) throw new InvalidOperationException("роздача без боксів");
        foreach (var b in deal.Boxes) b.Hands[0].Cards.Add(draw());
        deal.Dealer.Add(draw());
        foreach (var b in deal.Boxes) b.Hands[0].Cards.Add(draw());
        deal.Dealer.Add(draw());
        if (BlackjackCore.Peeks(deal.Dealer[0]) && BlackjackCore.Natural(deal.Dealer))
        {
            deal.DealerBj = true;
            deal.HoleOpen = true;
            foreach (var b in deal.Boxes) b.Hands[0].Done = true;
        }
        else
            foreach (var b in deal.Boxes)
                if (b.Hands[0].IsNatural) b.Hands[0].Done = true;
        deal.Box = 0;
        deal.Hand = 0;
        deal.Advance();
        return deal;
    }

    /// <summary>Що можна зробити поточній руці: (hit, stand, double, split).</summary>
    public (bool Hit, bool Stand, bool Double, bool Split) Allowed()
    {
        if (Current is not { Done: false } h) return default;
        var hands = Boxes[Box].Hands.Count;
        var two = h.Cards.Count == 2;
        var (t, soft) = BlackjackCore.Score(h.Cards);
        var dbl = two && !h.Split && !soft && BlackjackCore.DoubleOn.Contains(t);
        var split = two && h.Cards[0][0] == h.Cards[1][0] && hands < BlackjackCore.MaxHands;
        return (true, true, dbl, split);
    }

    /// <summary>Дія поточної руки. null — зроблено (хід пішов далі, якщо рука скінчилась), інакше текст відмови.</summary>
    public string? Act(string action, Func<string> draw)
    {
        if (Current is not { Done: false } h) return "Зараз не твій хід";
        var can = Allowed();
        switch (action)
        {
            case Hit:
                h.Cards.Add(draw());
                if (BlackjackCore.Total(h.Cards) >= 21) h.Done = true;
                break;
            case Stand:
                h.Done = true;
                break;
            case Double:
                if (!can.Double) return "Подвоїти можна лише на перших двох картах, коли в тебе 9, 10 чи 11";
                h.Doubled = true;
                h.Cards.Add(draw());
                h.Done = true;
                break;
            case Split:
                if (!can.Split)
                    return Boxes[Box].Hands.Count >= BlackjackCore.MaxHands
                        ? "Більше трьох рук не буває"
                        : "Розбити можна лише пару однакових карт";
                var second = new BjHand { Bet = h.Bet, Split = true, Cards = [h.Cards[1]] };
                h.Cards.RemoveAt(1);
                h.Split = true;
                Boxes[Box].Hands.Insert(Hand + 1, second);
                var aces = h.Cards[0][0] == 'A';
                foreach (var x in new[] { h, second })
                {
                    x.Cards.Add(draw());
                    x.Aces = aces;
                    if (aces || BlackjackCore.Total(x.Cards) == 21) x.Done = true;
                }
                break;
            default:
                return "Тут так не ходять";
        }
        Advance();
        return null;
    }

    /// <summary>Хід — першій руці, що ще не скінчилась (у порядку боксів і рук). Нема — гравці відходили.</summary>
    public void Advance()
    {
        for (var b = Math.Max(0, Box); b < Boxes.Count; b++)
        {
            var hands = Boxes[b].Hands;
            for (var i = b == Box ? Math.Max(0, Hand) : 0; i < hands.Count; i++)
                if (!hands[i].Done)
                {
                    Box = b;
                    Hand = i;
                    return;
                }
        }
        Box = -1;
        Hand = -1;
    }

    /// <summary>Чи Глекові є проти кого добирати: хоч одна рука не перебрала й не блекджек.</summary>
    public bool AnyLive() => !DealerBj && Boxes.Any(b => b.Hands.Any(h => !h.Bust && !h.IsNatural));

    /// <summary>Глек відкриває закриту й добирає (H17), якщо є проти кого. Кличеться, коли гравці відходили.</summary>
    public void DealerPlays(Func<string> draw)
    {
        HoleOpen = true;
        if (!AnyLive()) return;
        while (BlackjackCore.DealerHits(Dealer)) Dealer.Add(draw());
    }

    /// <summary>Розрахунок кожної руки: підсумок і повернення.</summary>
    public void Settle()
    {
        var dealer = BlackjackCore.Total(Dealer);
        var dealerBj = DealerBj || BlackjackCore.Natural(Dealer);
        foreach (var b in Boxes)
            foreach (var h in b.Hands)
            {
                string outcome;
                // блекджек на блекджек — старшинство за Глеком (§2.5): так RTP і тримається нижче 97 %
                if (h.IsNatural) outcome = dealerBj ? "lose" : "bj";
                else if (dealerBj) outcome = "lose";
                else if (h.Bust) outcome = "bust";
                else if (dealer > 21) outcome = "win";
                else
                {
                    var t = BlackjackCore.Total(h.Cards);
                    outcome = t > dealer ? "win" : t == dealer ? "push" : "lose";
                }
                h.Outcome = outcome;
                h.Return = (int)Math.Min(int.MaxValue, BlackjackCore.Pays(outcome, h.Stake));
            }
        Settled = true;
    }
}

/// <summary>
/// Базова стратегія для правил <see cref="BlackjackCore"/> (§2.6): що вигідніше — стояти, добрати, подвоїти чи розбити,
/// за очікуванням на нескінченній колоді, з урахуванням того, що Глек уже заглянув під туза й десятку. Таблиці рахуються
/// раз при першому зверненні (мілісекунди). Нею відповідає «💡 Підказка» і нею ж грає симуляція RTP.
/// </summary>
public static class BlackjackStrategy
{
    static readonly double[] P = BuildP();
    static double[] BuildP()
    {
        var p = new double[11];
        for (var v = 1; v <= 9; v++) p[v] = 1.0 / 13;
        p[10] = 4.0 / 13;
        return p;
    }

    /// <summary>Таблиці очікувань для відкритої карти Глека (1 — туз … 10).</summary>
    sealed class Up
    {
        public readonly double[] Stand = new double[22];        // [сума 0..21]
        public readonly double[,] Hit = new double[32, 2];       // [жорстка сума, є туз] — добрати й далі грати найкраще
        public readonly double[,] Dbl = new double[32, 2];
        public readonly double[] Split3 = new double[11];        // спліт пари [вага], можна ще раз (до трьох рук)
        public readonly double[] Split2 = new double[11];        // спліт пари, коли більше розбивати не можна
    }

    static readonly Lazy<Up[]> Tables = new(Build);

    static int Best(int hard, bool ace) => ace && hard + 10 <= 21 ? hard + 10 : hard;

    static Up[] Build()
    {
        var all = new Up[11];
        for (var up = 1; up <= 10; up++) all[up] = BuildUp(up);
        return all;
    }

    /// <summary>Розподіл підсумку Глека: [17..21] — сума, 22 — перебрав; блекджек виключено (Глек уже заглянув).</summary>
    static double[] DealerDist(int up)
    {
        var dist = new double[23];
        double bj = 0;
        void Rec(int hard, bool ace, int n, double p)
        {
            var b = Best(hard, ace);
            var soft = ace && hard + 10 <= 21;
            if (n == 2 && b == 21) { bj += p; return; }
            if (b > 21) { dist[22] += p; return; }
            if (b > 17 || (b == 17 && !(BlackjackCore.HitSoft17 && soft))) { dist[b] += p; return; }
            for (var c = 1; c <= 10; c++) Rec(hard + c, ace || c == 1, n + 1, p * P[c]);
        }
        Rec(up, up == 1, 1, 1.0);
        // Під туза й десятку Глек заглядає (блекджек можливий лише з ними): гравець ходить, коли блекджеку в Глека нема.
        for (var i = 0; i < dist.Length; i++) dist[i] /= 1 - bj;
        return dist;
    }

    static Up BuildUp(int up)
    {
        var u = new Up();
        var d = DealerDist(up);
        for (var b = 0; b <= 21; b++)
        {
            double ev = d[22];
            for (var k = 17; k <= 21; k++) ev += b > k ? d[k] : b < k ? -d[k] : 0;
            u.Stand[b] = ev;
        }
        double StandEv(int b) => b > 21 ? -1 : u.Stand[b];

        // Найкраще з «стояти / добрати» від жорсткої суми й туза (рекурсія з пам'яттю, згори вниз).
        var play = new double?[32, 2];
        double Play(int hard, bool ace)
        {
            var b = Best(hard, ace);
            if (b > 21) return -1;
            var a = ace ? 1 : 0;
            if (play[hard, a] is { } done) return done;
            var s = StandEv(b);
            var best = b == 21 ? s : Math.Max(s, HitEv(hard, ace));
            play[hard, a] = best;
            return best;
        }
        double HitEv(int hard, bool ace)
        {
            double ev = 0;
            for (var c = 1; c <= 10; c++) ev += P[c] * Play(hard + c, ace || c == 1);
            return ev;
        }
        double DblEv(int hard, bool ace)
        {
            double ev = 0;
            for (var c = 1; c <= 10; c++) ev += P[c] * 2 * StandEv(Best(hard + c, ace || c == 1));
            return ev;
        }
        for (var hard = 2; hard <= 21; hard++)
            for (var a = 0; a < 2; a++)
            {
                u.Hit[hard, a] = HitEv(hard, a == 1);
                u.Dbl[hard, a] = DblEv(hard, a == 1);
            }

        // Спліт (без подвоєння після спліту; тузи — по одній карті, без повторного спліту).
        for (var r = 1; r <= 10; r++)
        {
            if (r == 1)
            {
                double one = 0;
                for (var c = 1; c <= 10; c++) one += P[c] * StandEv(Best(1 + c, true));
                u.Split3[r] = u.Split2[r] = 2 * one;
                continue;
            }
            double Hv(int c) => Math.Max(StandEv(Best(r + c, c == 1)), Best(r + c, c == 1) == 21 ? -9 : HitEv(r + c, c == 1));
            var p = P[r];
            var q = 1 - p;
            double vn = 0;
            for (var c = 1; c <= 10; c++) if (c != r) vn += P[c] * Hv(c);
            vn /= q;
            var vrr = Hv(r);
            var A = q * vn + p * vrr;                       // рука добирає, розбити далі не можна
            var B = q * vn + p * Math.Max(2 * A, vrr);      // рука добирає, ще можна розбити раз
            u.Split3[r] = q * (vn + B) + p * Math.Max(3 * A, vrr + B);
            u.Split2[r] = 2 * A;
        }
        return u;
    }

    /// <summary>Очікування кожного дозволеного ходу (на одиницю ставки руки) — для підказки й тестів.</summary>
    public static IReadOnlyDictionary<string, double> Evs(BjDeal deal)
    {
        var res = new Dictionary<string, double>(StringComparer.Ordinal);
        if (deal.Current is not { Done: false } h) return res;
        var up = BlackjackCore.Value(deal.Dealer[0]);
        var t = Tables.Value[up];
        var hard = h.Cards.Sum(BlackjackCore.Value);
        var ace = h.Cards.Any(c => c[0] == 'A');
        var best = Best(hard, ace);
        var can = deal.Allowed();
        res[BjDeal.Stand] = best > 21 ? -1 : t.Stand[best];
        if (best < 21) res[BjDeal.Hit] = t.Hit[hard, ace ? 1 : 0];
        if (can.Double) res[BjDeal.Double] = t.Dbl[hard, ace ? 1 : 0];
        if (can.Split)
        {
            var v = BlackjackCore.Value(h.Cards[0]);
            res[BjDeal.Split] = deal.Boxes[deal.Box].Hands.Count == 1 ? t.Split3[v] : t.Split2[v];
        }
        return res;
    }

    /// <summary>Хід за базовою стратегією для поточної руки роздачі (hit/stand/double/split); null — не хід.</summary>
    public static string? Move(BjDeal deal)
    {
        var evs = Evs(deal);
        if (evs.Count == 0) return null;
        string? best = null;
        var bestEv = double.NegativeInfinity;
        // рівні — у порядку «стою, беру, подвоюю, б'ю»: простіший хід виграє нічию
        foreach (var a in new[] { BjDeal.Stand, BjDeal.Hit, BjDeal.Double, BjDeal.Split })
            if (evs.TryGetValue(a, out var ev) && ev > bestEv + 1e-12)
            {
                best = a;
                bestEv = ev;
            }
        return best;
    }

    /// <summary>
    /// Таблиця базової стратегії для spec (рядок на руку, стовпці — відкрита карта Глека 2..10, туз): S стою, H беру,
    /// D подвоюю (не можна — беру), P б'ю пару. Жорсткі 5–17, м'які 13–20, пари 2–A.
    /// </summary>
    public static string Chart()
    {
        var sb = new StringBuilder();
        int[] ups = [2, 3, 4, 5, 6, 7, 8, 9, 10, 1];
        string Cell(List<string> cards, int up)
        {
            var deal = new BjDeal
            {
                Boxes = [new BjBox { Bet = 1, Hands = [new BjHand { Bet = 1, Cards = cards }] }],
                Dealer = [up == 1 ? "Ah" : up == 10 ? "Th" : $"{up}h", "2c"], Box = 0, Hand = 0,
            };
            return Move(deal) switch { BjDeal.Stand => "S", BjDeal.Hit => "H", BjDeal.Double => "D", BjDeal.Split => "P", _ => "?" };
        }
        sb.AppendLine("      2 3 4 5 6 7 8 9 T A");
        for (var t = 5; t <= 17; t++)
        {
            // жорстка сума без туза й без пари: 2+3=5 … 2+9=11; від 12 — десятка + (t−10)
            List<string> cards = t <= 11 ? ["2d", $"{t - 2}c"] : ["Td", $"{t - 10}c"];
            sb.Append($"H{t,-4} ").AppendJoin(' ', ups.Select(u => Cell(cards, u))).AppendLine();
        }
        for (var t = 13; t <= 20; t++)
        {
            List<string> cards = ["Ad", $"{t - 11}c"];
            sb.Append($"S{t,-4} ").AppendJoin(' ', ups.Select(u => Cell(cards, u))).AppendLine();
        }
        foreach (var r in "23456789TA")
        {
            List<string> cards = [$"{r}d", $"{r}c"];
            sb.Append($"P{(r == 'T' ? "10" : r.ToString()),-4} ").AppendJoin(' ', ups.Select(u => Cell(cards, u))).AppendLine();
        }
        return sb.ToString();
    }
}

/// <summary>Швидка випадкова колода для симуляції: 6 колод, кожна роздача — з повної колоди (часткове тасування).</summary>
public sealed class BlackjackShoe(Random rng)
{
    readonly string[] _cards = Enumerable.Range(0, BlackjackCore.CardsInShoe).Select(i => BlackjackCore.Codes[i % 52]).ToArray();
    int _pos;

    /// <summary>Нова роздача — уся колода знову (тасуємо після кожної роздачі).</summary>
    public void Reset() => _pos = 0;

    public string Next()
    {
        var j = rng.Next(_pos, _cards.Length);
        (_cards[_pos], _cards[j]) = (_cards[j], _cards[_pos]);
        return _cards[_pos++];
    }
}
