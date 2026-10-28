using static Hlechyky.Games.Impl.SorochyntsiCards;

namespace Hlechyky.Games.Impl;

public enum SoPhase { Play, Discard, Guest, Over }

/// <summary>
/// Подія для анімацій і журналу (контракт §7), як <see cref="KhEvent"/>: одне поле може бути приховане
/// (<see cref="SecretKey"/>) — його бачать лише <see cref="Insiders"/>, решта отримує null (бронь наосліп).
/// </summary>
public sealed class SoEvent
{
    public long Seq;
    public string K = "";
    public List<KeyValuePair<string, object?>> Data = [];
    public string? SecretKey;
    public object? Secret;
    public int[] Insiders = [];

    public Dictionary<string, object?> For(int? seat)
    {
        var d = new Dictionary<string, object?> { ["seq"] = Seq, ["k"] = K };
        foreach (var (k, v) in Data) d[k] = v;
        if (SecretKey is not null) d[SecretKey] = seat is { } s && Insiders.Contains(s) ? Secret : null;
        return d;
    }

    public object? this[string key] => Data.FirstOrDefault(d => d.Key == key).Value;
}

/// <summary>Броньована крамниця: id і чи взята наосліп (тоді id бачить лише власник, §2.4).</summary>
public sealed record SoRes(int Id, bool Blind);

/// <summary>
/// Що коштує крамниця місцю (§2.5): <see cref="Pay"/> — що піде (5 товарів + дукати), <see cref="Gap"/> — чого бракує
/// жетонами по товарах (до дукатів), <see cref="Short"/> — скільки бракує й після дукатів (0 — можна купити).
/// </summary>
public sealed record SoQuote(int Id, int[] Pay, int[] Gap, int GapSum, int Short)
{
    public bool Ok => Short == 0;
}

/// <summary>
/// Законні дії місця, якби зараз був його хід у фазі play (§2.3–§2.8): з цього ж будуються <c>me.take</c>, <c>me.can</c>,
/// <c>me.why</c> у виді і кандидати Глека — тож вид і бот ніколи не розходяться з <c>Act</c>.
/// </summary>
public sealed class SoLegal
{
    /// <summary>Непорожні купи (індекси буденних товарів).</summary>
    public int[] Piles = [];
    /// <summary>Скільки різних брати: min(3, непорожніх куп).</summary>
    public int Need;
    /// <summary>Купи з ≥ 4 — можна «два однакові».</summary>
    public int[] Two = [];
    public bool Take, Reserve, Pass;
    public bool[] Deck = new bool[3];
    /// <summary>Крамниці (ряди + своя бронь), які можна купити зараз.</summary>
    public List<SoQuote> Buys = [];
    public Dictionary<string, string> Why = [];
}

/// <summary>
/// Правила «Сорочинського ярмарку» (контракт §2) без каркаса: ні Ctx, ні годинника — випадковість приходить лише в
/// <see cref="Begin"/> у фіксованому порядку (§2.1). Кожна дія — метод, що повертає помилку людською мовою (тексти §6)
/// або null; при помилці стан не міняється. Жетони — масив на 6 (п'ять товарів + дукат), знижки — на 6 (п'ять товарів +
/// цвіт папороті), місця — 0..3.
/// </summary>
public sealed partial class SorochyntsiCore
{
    public bool[] In = new bool[MaxSeats];
    /// <summary>Місця в грі за зростанням.</summary>
    public int[] Order = [];
    public int Seats => Order.Length;
    public int Target = 16;
    public Func<int, string> NameOf = s => $"гравець {s + 1}";

    public bool Started;
    public SoPhase Phase = SoPhase.Play;
    public bool Over => Phase == SoPhase.Over;
    /// <summary>Коло (з 1), хто починає кожне коло і чий хід.</summary>
    public int Round, First, Turn;
    /// <summary>Останнє коло: булава взята або коло 40.</summary>
    public bool Last;
    public string? LastReason;

    /// <summary>Колоди рівнів (індекс 0 — верх) і ряди <c>Rows[tier − 1][slot]</c> (-1 — порожнє місце).</summary>
    public List<int>[] Decks = [[], [], []];
    public int[][] Rows = [[-1, -1, -1, -1], [-1, -1, -1, -1], [-1, -1, -1, -1]];
    public int[] Bank = new int[6];
    /// <summary>Банк на початку партії (для інваріантів: банк + руки — завжди стільки).</summary>
    public int[] Bank0 = new int[6];

    public int[][] Tok = [.. Enumerable.Range(0, MaxSeats).Select(_ => new int[6])];
    public int[][] Bonus = [.. Enumerable.Range(0, MaxSeats).Select(_ => new int[6])];
    /// <summary>Куплені крамниці в порядку купівлі.</summary>
    public List<int>[] Cards = [.. Enumerable.Range(0, MaxSeats).Select(_ => new List<int>())];
    public List<SoRes>[] Reserved = [.. Enumerable.Range(0, MaxSeats).Select(_ => new List<SoRes>())];
    public int[] CardPts = new int[MaxSeats];
    public int[] Icons = new int[MaxSeats];

    /// <summary>Гості цієї партії в порядку столу і до кого прийшов кожен (за id гостя; -1 — ще на столі).</summary>
    public List<int> Table = [];
    public int[] GuestBy = Enumerable.Repeat(-1, 10).ToArray();
    public List<int>[] GuestsOf = [.. Enumerable.Range(0, MaxSeats).Select(_ => new List<int>())];

    public int Starshyna = -1, Bulava = -1;
    /// <summary>Фаза discard: скільки повернути; фаза guest: з кого обирати.</summary>
    public int DiscardN;
    public List<int> GuestChoice = [];
    /// <summary>Пропусків поспіль (§2.8).</summary>
    public int Passes;
    public string Reason = "";
    public int[] Winners = [];

    public List<SoEvent> Events = [];
    public long Seq;
    public List<string> Log = [];
    /// <summary>Рядки для журналу кімнати (гра забирає й чистить).</summary>
    public List<string> Journal = [];

    // статистика кінця (§9)
    public int Turns;
    public List<int[]> Timeline = [];
    public int[] Takes = new int[MaxSeats], TokensGot = new int[MaxSeats], Reserves = new int[MaxSeats], Blinds = new int[MaxSeats];
    public int[] GoldSpent = new int[MaxSeats], Returned = new int[MaxSeats], Ferns = new int[MaxSeats];
    public int[] FirstT3 = [-1, -1, -1, -1];
    public int[][] Buys = [.. Enumerable.Range(0, MaxSeats).Select(_ => new int[3])];

    public SorochyntsiCore(IEnumerable<int> seats)
    {
        foreach (var s in seats) In[s] = true;
        Order = [.. Enumerable.Range(0, MaxSeats).Where(s => In[s])];
    }

    // =====================================================================================
    // Початок (§2.1)
    // =====================================================================================

    /// <summary>
    /// Розклад партії. Порядок викликів <paramref name="rng"/> — як у контракті: Фішер–Єйтс колод I, II, III (id за
    /// зростанням), Фішер–Єйтс гостей 0–9 (на стіл — перші гравців + 1), перший гравець.
    /// </summary>
    public void Begin(Random rng)
    {
        var lo = 0;
        for (var t = 0; t < 3; t++)
        {
            var ids = Enumerable.Range(lo, TierSize[t]).ToArray();
            Shuffle(ids, rng);
            Decks[t] = [.. ids];
            lo += TierSize[t];
        }
        var guests = Enumerable.Range(0, Guests.Length).ToArray();
        Shuffle(guests, rng);
        Table = [.. guests.Take(Seats + 1)];
        First = Order[rng.Next(Seats)];
        var pile = Pile(Seats);
        for (var g = 0; g < 5; g++) Bank[g] = pile;
        Bank[Gold] = GoldPile;
        Bank0 = [.. Bank];
        for (var t = 0; t < 3; t++)
            for (var slot = 0; slot < Row; slot++)
                Rows[t][slot] = Draw(t);
        Started = true;
        Phase = SoPhase.Play;
        Round = 1;
        Turn = First;
        Emit("round", ("n", Round), ("first", First));
        Say($"Ярмарок відкрито — першим торгує {NameOf(First)}");
        Emit("turn", ("seat", Turn));
    }

    static void Shuffle(int[] a, Random rng)
    {
        for (var i = a.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (a[i], a[j]) = (a[j], a[i]);
        }
    }

    int Draw(int t)
    {
        if (Decks[t].Count == 0) return -1;
        var id = Decks[t][0];
        Decks[t].RemoveAt(0);
        return id;
    }

    // =====================================================================================
    // Рахунок
    // =====================================================================================

    /// <summary>Очки (§2.1): ⭐ крамниць + 3 за кожного гостя + 3 за Січову старшину.</summary>
    public int Points(int s) => CardPts[s] + GuestPoints * GuestsOf[s].Count + (Starshyna == s ? StarshynaPoints : 0);

    public int NTok(int s)
    {
        var t = Tok[s];
        return t[0] + t[1] + t[2] + t[3] + t[4] + t[5];
    }

    /// <summary>Чи покривають знижки <paramref name="b"/> вимогу гостя.</summary>
    public static bool Covers(int[] b, int guest)
    {
        var need = Guests[guest].Need;
        for (var g = 0; g < 5; g++) if (b[g] < need[g]) return false;
        return true;
    }

    /// <summary>Скільки знижок бракує гостю (Σ max(0, need − b)).</summary>
    public static int GuestLeft(int[] b, int guest)
    {
        var need = Guests[guest].Need;
        var n = 0;
        for (var g = 0; g < 5; g++) n += Math.Max(0, need[g] - b[g]);
        return n;
    }

    /// <summary>Умова булави (§2.6): ⭐ ≥ ціль і знижка кожним із шести товарів.</summary>
    public bool BulavaReady(int points, int[] b)
    {
        if (points < Target) return false;
        for (var g = 0; g < 6; g++) if (b[g] < 1) return false;
        return true;
    }

    /// <summary>Ціна крамниці <paramref name="id"/> для того, у кого знижки <paramref name="b"/> і жетони <paramref name="t"/> (§2.5).</summary>
    public static SoQuote QuoteFor(int id, int[] b, int[] t)
    {
        var c = Deck[id];
        var pay = new int[6];
        var gap = new int[5];
        var sum = 0;
        for (var g = 0; g < 5; g++)
        {
            var req = Math.Max(0, c.Cost[g] - b[g]);
            var use = Math.Min(req, t[g]);
            pay[g] = use;
            gap[g] = req - use;
            sum += gap[g];
        }
        var shortBy = Math.Max(0, sum - t[Gold]);
        if (shortBy == 0) pay[Gold] = sum;
        return new SoQuote(id, pay, gap, sum, shortBy);
    }

    public SoQuote Quote(int s, int id) => QuoteFor(id, Bonus[s], Tok[s]);

    /// <summary>«бракує 2 зілля й 2 глини, дукатів у тебе: 1» (§5).</summary>
    public static string WhyShort(SoQuote q, int gold) => "бракує " + Missing(q, gold);

    static string Missing(SoQuote q, int gold)
    {
        var parts = new List<string>();
        for (var g = 0; g < 5; g++) if (q.Gap[g] > 0) parts.Add($"{q.Gap[g]} {GoodGen[g]}");
        var list = parts.Count <= 1 ? string.Concat(parts) : string.Join(", ", parts.Take(parts.Count - 1)) + " й " + parts[^1];
        return gold >= 1 ? $"{list}, дукатів у тебе: {gold}" : list;
    }

    /// <summary>Видимі місцю крамниці: усі в рядах (I, II, III; місця 0–3), потім своя бронь.</summary>
    public IEnumerable<int> Visible(int s)
    {
        for (var t = 0; t < 3; t++)
            for (var slot = 0; slot < Row; slot++)
                if (Rows[t][slot] >= 0) yield return Rows[t][slot];
        foreach (var r in Reserved[s]) yield return r.Id;
    }

    /// <summary>Де крамниця в рядах: (рівень 0–2, місце) або null.</summary>
    public (int Tier, int Slot)? InRows(int id)
    {
        for (var t = 0; t < 3; t++)
            for (var slot = 0; slot < Row; slot++)
                if (Rows[t][slot] == id) return (t, slot);
        return null;
    }

    public bool RowsEmpty()
    {
        for (var t = 0; t < 3; t++)
            for (var slot = 0; slot < Row; slot++)
                if (Rows[t][slot] >= 0) return false;
        return true;
    }

    // =====================================================================================
    // Законні дії (§2.3–§2.8)
    // =====================================================================================

    /// <summary>Що місце могло б зробити, якби зараз був його хід у фазі play (і чому ні — тексти помилок Act).</summary>
    public SoLegal Legal(int s)
    {
        var l = new SoLegal
        {
            Piles = [.. Enumerable.Range(0, 5).Where(g => Bank[g] > 0)],
            Two = [.. Enumerable.Range(0, 5).Where(g => Bank[g] >= TwoMin)],
        };
        l.Need = Math.Min(3, l.Piles.Length);
        l.Take = l.Piles.Length > 0;
        if (!l.Take) l.Why["take"] = "Купи порожні";
        var full = Reserved[s].Count >= ReserveMax;
        l.Reserve = !full && !RowsEmpty();
        if (!l.Reserve) l.Why["reserve"] = full ? "Бронювати можна щонайбільше 3 крамниці" : "Цієї крамниці в рядах нема";
        for (var t = 0; t < 3; t++)
        {
            l.Deck[t] = !full && Decks[t].Count > 0;
            if (!l.Deck[t]) l.Why[$"deck{t + 1}"] = full ? "Бронювати можна щонайбільше 3 крамниці" : $"Колода {Roman[t]} рівня скінчилась";
        }
        foreach (var id in Visible(s))
        {
            var q = Quote(s, id);
            if (q.Ok) l.Buys.Add(q);
        }
        l.Pass = !l.Take && !l.Reserve && !l.Deck.Any(d => d) && l.Buys.Count == 0;
        if (!l.Pass) l.Why["pass"] = "Пропускати не можна — є що робити";
        return l;
    }

    /// <summary>Усі законні «взяти» як набори на 5 товарів (три різні / скорочені / два однакові), у сталому порядку.</summary>
    public List<int[]> TakeOptions()
    {
        var piles = Enumerable.Range(0, 5).Where(g => Bank[g] > 0).ToArray();
        var list = new List<int[]>();
        if (piles.Length >= 3)
        {
            for (var a = 0; a < piles.Length; a++)
                for (var b = a + 1; b < piles.Length; b++)
                    for (var c = b + 1; c < piles.Length; c++)
                    {
                        var got = new int[5];
                        got[piles[a]] = got[piles[b]] = got[piles[c]] = 1;
                        list.Add(got);
                    }
        }
        else if (piles.Length > 0)
        {
            var got = new int[5];
            foreach (var g in piles) got[g] = 1;
            list.Add(got);
        }
        foreach (var g in piles)
        {
            if (Bank[g] < TwoMin) continue;
            var got = new int[5];
            got[g] = 2;
            list.Add(got);
        }
        return list;
    }

    // =====================================================================================
    // Дії (§6). Кожна — помилка або null; при помилці стан не міняється.
    // =====================================================================================

    /// <summary>Спільні перевірки ходу: чи партія йде, чий хід і чи фаза дозволяє цю дію.</summary>
    public string? Gate(int s, string action)
    {
        if (Over) return "Партія вже скінчилась";
        if (s < 0 || s >= MaxSeats || !In[s]) return "Ти не граєш за цим столом";
        if (s != Turn) return $"Зараз ходить {NameOf(Turn)}";
        return action switch
        {
            "discard" => Phase == SoPhase.Discard ? null : "Зараз нічого повертати",
            "guest" => Phase == SoPhase.Guest ? null : "Гостей зараз не кличуть",
            _ => Phase switch
            {
                SoPhase.Discard => "Спершу поверни зайве: у руці щонайбільше 10 жетонів",
                SoPhase.Guest => "Спершу обери гостя",
                _ => null,
            },
        };
    }

    /// <summary>Перевірити «взяти» (тексти §6 у їхньому порядку); <paramref name="got"/> — що саме піде з банку.</summary>
    public string? TakeError(IReadOnlyList<string>? goods, out int[] got)
    {
        got = new int[5];
        if (goods is null) return "Не зрозумів, що береш";
        if (goods.Contains("gold")) return "Дукат — лише разом із бронею";
        if (goods.Contains("fern")) return "Цвіт папороті жетоном не буває — лише знижкою з крамниці";
        var idx = goods.Select(GoodIdx).ToArray();
        if (idx.Any(i => i < 0)) return "Такого товару на ярмарку нема";
        var k = 0;
        for (var g = 0; g < 5; g++) if (Bank[g] > 0) k++;
        if (k == 0) return "Купи порожні";
        foreach (var g in idx)
            if (Bank[g] == 0) return $"Купа «{GoodName[g]}» порожня";
        if (idx.Length == 2 && idx[0] == idx[1])
        {
            var g = idx[0];
            if (Bank[g] < TwoMin) return $"Два однакові — лише з купи, де щонайменше 4 (у купі «{GoodName[g]}» — {Bank[g]})";
            got[g] = 2;
            return null;
        }
        if (idx.Distinct().Count() != idx.Length) return "Три — то три різні товари";
        if (idx.Length != Math.Min(3, k))
            return k >= 3 ? "Бери три різні товари або два однакові" : $"Бери по одному з кожної непорожньої купи ({k}) або два однакові";
        foreach (var g in idx) got[g] = 1;
        return null;
    }

    public string? Take(int s, IReadOnlyList<string>? goods)
    {
        if (Gate(s, "take") is { } gate) return gate;
        if (TakeError(goods, out var got) is { } err) return err;
        var n = 0;
        for (var g = 0; g < 5; g++)
        {
            Bank[g] -= got[g];
            Tok[s][g] += got[g];
            n += got[g];
        }
        Passes = 0;
        Takes[s]++;
        TokensGot[s] += n;
        Emit("take", ("seat", s), ("got", Map(got)));
        Say($"{NameOf(s)} бере {TakeText(got)}");
        AfterAct(s);
        return null;
    }

    /// <summary>«рибу, зілля й глину» / «2 вовни».</summary>
    static string TakeText(int[] got)
    {
        for (var g = 0; g < 5; g++) if (got[g] >= 2) return $"{got[g]} {GoodGen[g]}";
        var parts = Enumerable.Range(0, 5).Where(g => got[g] > 0).Select(g => GoodAcc[g]).ToList();
        return parts.Count <= 1 ? string.Concat(parts) : string.Join(", ", parts.Take(parts.Count - 1)) + " й " + parts[^1];
    }

    /// <summary>Забронювати з ряду (<paramref name="card"/>) або наосліп з колоди (<paramref name="tier"/> 1–3).</summary>
    public string? Reserve(int s, int? card, int? tier)
    {
        if (Gate(s, "reserve") is { } gate) return gate;
        if (card is null && tier is null) return "Не зрозумів, що бронюєш";
        if (Reserved[s].Count >= ReserveMax) return "Бронювати можна щонайбільше 3 крамниці";
        int id, t;
        int? slot;
        if (card is { } c)
        {
            if (InRows(c) is not { } at) return "Цієї крамниці в рядах нема";
            (id, t, slot) = (c, at.Tier, at.Slot);
        }
        else
        {
            t = tier!.Value - 1;
            if (t is < 0 or > 2) return "Такої колоди нема";
            if (Decks[t].Count == 0) return $"Колода {Roman[t]} рівня скінчилась";
            id = Decks[t][0];
            slot = null;
        }
        var gold = Bank[Gold] > 0 ? 1 : 0;
        Bank[Gold] -= gold;
        Tok[s][Gold] += gold;
        var blind = slot is null;
        if (blind) Decks[t].RemoveAt(0);
        Reserved[s].Add(new SoRes(id, blind));
        Passes = 0;
        Reserves[s]++;
        if (blind) Blinds[s]++;
        if (blind)
        {
            Emit("reserve", [("seat", s), ("tier", t + 1), ("slot", null), ("gold", gold)], "card", id, [s]);
            Say($"{NameOf(s)} бронює наосліп з колоди {Roman[t]}" + (gold > 0 ? " і бере дукат" : " — дукатів нема"));
        }
        else
        {
            Emit("reserve", ("seat", s), ("tier", t + 1), ("slot", slot), ("card", id), ("gold", gold));
            Say($"{NameOf(s)} бронює «{Deck[id].Name}»" + (gold > 0 ? " і бере дукат" : " — дукатів нема"));
            Refill(t, slot!.Value);
        }
        AfterAct(s);
        return null;
    }

    void Refill(int t, int slot)
    {
        var next = Draw(t);
        Rows[t][slot] = next;
        Emit("refill", ("tier", t + 1), ("slot", slot), ("card", next >= 0 ? next : (int?)null));
    }

    /// <summary>Купити крамницю з ряду або зі своєї броні; оплата — автоматично (§2.5).</summary>
    public string? Buy(int s, int? card)
    {
        if (Gate(s, "buy") is { } gate) return gate;
        if (card is not { } id) return "Не зрозумів, що купуєш";
        var row = IsCard(id) ? InRows(id) : null;
        var res = Reserved[s].FindIndex(r => r.Id == id);
        if (row is null && res < 0) return "Цієї крамниці нема ні в рядах, ні в твоїй броні";
        var q = Quote(s, id);
        if (!q.Ok) return "Не вистачає: " + Missing(q, Tok[s][Gold]);
        for (var g = 0; g < 6; g++)
        {
            Tok[s][g] -= q.Pay[g];
            Bank[g] += q.Pay[g];
        }
        var c = Deck[id];
        Cards[s].Add(id);
        Bonus[s][c.Bonus]++;
        CardPts[s] += c.Pts;
        Icons[s] += c.K;
        Passes = 0;
        Buys[s][c.Tier - 1]++;
        GoldSpent[s] += q.Pay[Gold];
        if (c.Bonus == Fern) Ferns[s]++;
        if (c.Tier == 3 && FirstT3[s] < 0) FirstT3[s] = Round;
        var from = row is null ? "reserve" : "row";
        var slot = row is { } r ? r.Slot : res;
        if (row is null) Reserved[s].RemoveAt(res);
        Emit("buy", ("seat", s), ("card", id), ("from", from), ("slot", slot), ("pay", Map(q.Pay)), ("bonus", Bonuses[c.Bonus]), ("pts", c.Pts));
        Say($"{NameOf(s)} купує «{c.Name}»" + (c.Pts > 0 ? $" (+{c.Pts} ⭐)" : ""));
        if (row is { } rr) Refill(rr.Tier, rr.Slot);
        CheckStarshyna(s);
        AfterAct(s);
        return null;
    }

    /// <summary>Січова старшина (§2.7): ⚔ ≥ 3 і плитка нічия або в покупця строго більше, ніж у власника.</summary>
    void CheckStarshyna(int s)
    {
        if (Starshyna == s || Icons[s] < StarshynaIcons) return;
        if (Starshyna >= 0 && Icons[s] <= Icons[Starshyna]) return;
        var from = Starshyna;
        Starshyna = s;
        Emit("starshyna", ("seat", s), ("from", from >= 0 ? from : (int?)null), ("icons", Icons[s]));
        Say($"Січова старшина — у {NameOf(s)} ({Icons[s]} ⚔)", journal: true);
    }

    public string? Pass(int s)
    {
        if (Gate(s, "pass") is { } gate) return gate;
        if (!Legal(s).Pass) return "Пропускати не можна — є що робити";
        Passes++;
        Emit("pass", ("seat", s));
        Say($"{NameOf(s)} пропускає хід — нема що робити");
        if (Passes >= Seats)
        {
            Turns++;
            End("stall");
            return null;
        }
        AfterAct(s);
        return null;
    }

    /// <summary>Повернути рівно <see cref="DiscardN"/> жетонів (фаза discard). <paramref name="give"/> — за назвами жетонів.</summary>
    public string? Discard(int s, IReadOnlyList<KeyValuePair<string, int>>? give)
    {
        if (Gate(s, "discard") is { } gate) return gate;
        if (give is null || give.Any(p => p.Value < 0)) return "Не зрозумів, що повертаєш";
        var a = new int[6];
        foreach (var (name, n) in give)
        {
            var i = TokenIdx(name);
            if (i < 0) return "Такого жетона нема";
            a[i] += n;
        }
        for (var i = 0; i < 6; i++)
            if (a[i] > Tok[s][i]) return $"Стільки {(i == Gold ? "дукатів" : GoodGen[i])} у тебе нема";
        if (a.Sum() != DiscardN) return $"Поверни рівно {DiscardN} {TokenWord(DiscardN)}";
        for (var i = 0; i < 6; i++)
        {
            Tok[s][i] -= a[i];
            Bank[i] += a[i];
        }
        Returned[s] += DiscardN;
        Emit("discard", ("seat", s), ("gave", Map(a)));
        Say($"{NameOf(s)} повертає {DiscardN} {TokenWord(DiscardN)}");
        DiscardN = 0;
        Phase = SoPhase.Play;
        GuestStep(s);
        return null;
    }

    /// <summary>Обрати гостя (фаза guest).</summary>
    public string? Guest(int s, int? guest)
    {
        if (Gate(s, "guest") is { } gate) return gate;
        if (guest is not { } g) return "Не зрозумів, кого кличеш";
        if (!GuestChoice.Contains(g)) return "Цей гість до тебе ще не піде";
        GuestChoice.Clear();
        Phase = SoPhase.Play;
        Arrive(s, g);
        Finish(s);
        return null;
    }

    /// <summary>Записати рядок у журнал столу (гра — про вихід, Глека тощо).</summary>
    public void Note(string line) => Say(line);

    // =====================================================================================
    // Кінець ходу (§2.6)
    // =====================================================================================

    /// <summary>1. Зайве понад 10 — фаза discard; інакше далі.</summary>
    void AfterAct(int s)
    {
        var n = NTok(s);
        if (n > HandMax)
        {
            Phase = SoPhase.Discard;
            DiscardN = n - HandMax;
            return;
        }
        GuestStep(s);
    }

    /// <summary>2. Гість: один — сам, кілька — вибір (фаза guest), щонайбільше один за хід.</summary>
    void GuestStep(int s)
    {
        var ok = Table.Where(g => GuestBy[g] < 0 && Covers(Bonus[s], g)).ToList();
        if (ok.Count > 1)
        {
            Phase = SoPhase.Guest;
            GuestChoice = ok;
            return;
        }
        if (ok.Count == 1) Arrive(s, ok[0]);
        Finish(s);
    }

    void Arrive(int s, int g)
    {
        GuestBy[g] = s;
        GuestsOf[s].Add(g);
        Emit("guest", ("seat", s), ("guest", g));
        Say($"До {NameOf(s)} у гості — {Guests[g].Name} (+{GuestPoints} ⭐)");
    }

    /// <summary>3. Булава, 4. хід до наступного (нове коло, кінець партії).</summary>
    void Finish(int s)
    {
        if (Bulava < 0 && BulavaReady(Points(s), Bonus[s]))
        {
            Bulava = s;
            Emit("bulava", ("seat", s));
            Say($"{NameOf(s)} бере гетьманську булаву! Останнє коло", journal: true);
            if (!Last)
            {
                Last = true;
                Emit("last", ("reason", "bulava"));
            }
            LastReason = "bulava";
        }
        Turns++;
        var next = Order[(Array.IndexOf(Order, s) + 1) % Seats];
        if (next == First)
        {
            Timeline.Add([.. Enumerable.Range(0, MaxSeats).Select(x => In[x] ? Points(x) : 0)]);
            if (Last)
            {
                End(Bulava >= 0 ? "bulava" : "rounds");
                return;
            }
            Round++;
            if (Round >= MaxRounds)
            {
                Last = true;
                LastReason = "rounds";
                Emit("last", ("reason", "rounds"));
                Say($"Коло {MaxRounds} — останнє: ярмарок зачиняється", journal: true);
            }
            Emit("round", ("n", Round), ("first", First));
        }
        Turn = next;
        Emit("turn", ("seat", Turn));
    }

    /// <summary>Кінець партії (§2.9): найбільше ⭐, далі менше куплених крамниць, далі спільна перемога.</summary>
    void End(string reason)
    {
        Phase = SoPhase.Over;
        Reason = reason;
        GuestChoice.Clear();
        DiscardN = 0;
        if (reason == "stall") Timeline.Add([.. Enumerable.Range(0, MaxSeats).Select(x => In[x] ? Points(x) : 0)]);
        var top = Order.Max(Points);
        var tied = Order.Where(o => Points(o) == top).ToArray();
        var fewest = tied.Min(o => Cards[o].Count);
        Winners = [.. tied.Where(o => Cards[o].Count == fewest)];
        Emit("over", ("winners", (int[])Winners.Clone()), ("reason", reason), ("points", Order.Select(Points).ToArray()));
        var names = string.Join(", ", Winners.Select(NameOf));
        var why = reason switch { "stall" => "товари скінчились", "rounds" => $"ярмарок зачинився після {MaxRounds} кіл", _ => $"булава в {NameOf(Bulava)}" };
        Say(Winners.Length == 1
            ? $"Голова ярмарку — {names}: {top} ⭐ ({why})"
            : $"Ярмарок поділили: {names} — по {top} ⭐ ({why})", journal: true);
    }

    // =====================================================================================
    // Події й журнал
    // =====================================================================================

    /// <summary>{ "fish": 1, "gold": 1 } — лише ненульові, у порядку жетонів.</summary>
    public static Dictionary<string, int> Map(int[] a)
    {
        var d = new Dictionary<string, int>();
        for (var i = 0; i < a.Length; i++) if (a[i] > 0) d[Tokens[i]] = a[i];
        return d;
    }

    SoEvent Emit(string k, params (string key, object? val)[] data) => Emit(k, data, null, null, []);

    SoEvent Emit(string k, (string key, object? val)[] data, string? secretKey, object? secret, int[] insiders)
    {
        var e = new SoEvent
        {
            Seq = ++Seq,
            K = k,
            Data = [.. data.Select(d => new KeyValuePair<string, object?>(d.key, d.val))],
            SecretKey = secretKey,
            Secret = secret,
            Insiders = insiders,
        };
        Events.Add(e);
        if (Events.Count > 120) Events.RemoveRange(0, Events.Count - 60);
        return e;
    }

    void Say(string line, bool journal = false)
    {
        Log.Add(line);
        if (Log.Count > 40) Log.RemoveRange(0, Log.Count - 12);
        if (journal) Journal.Add(line);
    }
}
