namespace Hlechyky.Games.Impl;

/// <summary>Один банк після роздачі: скільки, кому (Winners/Shares — паралельно), якою рукою (0 — без шоудауну).</summary>
public sealed record PokerPot(int Amount, int[] Winners, int[] Shares, int Score, bool Showdown);

/// <summary>Банк поточної роздачі для виду: сума і хто за нього бореться (позиції).</summary>
public sealed record PokerPotView(int Amount, int[] Seats);

/// <summary>Що можна зробити позиції, чий хід. Raise* = 0 — рейзити не можна; суми рейзу — «до».</summary>
public sealed record PokerLegal(bool Check, int Call, int RaiseMin, int RaiseMax, int AllIn);

/// <summary>
/// Рушій техаського холдему без ліміту, без каркаса (як DurakCore): позиції 0..N-1 за годинниковою, стеки, роздача,
/// кола ставок, мінрейз, неповний олл-ін, побічні банки, шоудаун. Детермінований для заданої колоди. Усе стан —
/// публічні властивості, щоб гра зберегла його JSON-ом цілком (перезапуск сайту).
/// </summary>
public sealed class PokerCore
{
    public int N { get; set; } = 8;
    /// <summary>Фішки позиції поза ставками цієї роздачі. Живуть і між роздачами.</summary>
    public int[] Stack { get; set; } = new int[8];

    public bool Live { get; set; }
    public long HandNo { get; set; }
    public bool[] Dealt { get; set; } = new bool[8];
    public bool[] Folded { get; set; } = new bool[8];
    public bool[] AllIn { get; set; } = new bool[8];
    /// <summary>Ходив у цьому колі після останнього ПОВНОГО рейзу (неповний олл-ін цього не скидає — торги не відкриває).</summary>
    public bool[] Acted { get; set; } = new bool[8];
    /// <summary>Ставка в цьому колі.</summary>
    public int[] Bet { get; set; } = new int[8];
    /// <summary>Усе, що позиція внесла в цю роздачу (разом із Bet).</summary>
    public int[] Total { get; set; } = new int[8];
    public int[][] Hole { get; set; } = [[], [], [], [], [], [], [], []];
    public List<int> Board { get; set; } = [];
    public int[] Deck { get; set; } = [];
    public int DeckPos { get; set; }

    public int Button { get; set; } = -1;
    public int SbPos { get; set; } = -1;
    public int BbPos { get; set; } = -1;
    /// <summary>Чий хід (позиція) або -1.</summary>
    public int Turn { get; set; } = -1;
    /// <summary>0 префлоп, 1 флоп, 2 терн, 3 рівер, 4 — роздачу зіграно.</summary>
    public int Street { get; set; }
    public int SmallBlind { get; set; }
    public int BigBlind { get; set; }
    public int CurrentBet { get; set; }
    /// <summary>Розмір останнього повного бету/рейзу в цьому колі (мінімальний наступний рейз — на стільки ж).</summary>
    public int MinRaise { get; set; }
    /// <summary>Торгів більше нема (олл-ін і всі під'їхали): гра докладає дошку з паузами (<see cref="RunoutStep"/>).</summary>
    public bool Runout { get; set; }
    /// <summary>Карти тих, хто дійшов, відкрито (олл-ін під'їхали або шоудаун).</summary>
    public bool ShowAll { get; set; }
    /// <summary>Підсумок останньої роздачі.</summary>
    public List<PokerPot> Wins { get; set; } = [];
    /// <summary>Хто дійшов до шоудауну в останній роздачі (їхні карти відкриті).</summary>
    public bool[] Shown { get; set; } = new bool[8];

    public PokerCore() { }

    public PokerCore(int n)
    {
        N = n;
        Stack = new int[n];
        Dealt = new bool[n];
        Folded = new bool[n];
        AllIn = new bool[n];
        Acted = new bool[n];
        Bet = new int[n];
        Total = new int[n];
        Shown = new bool[n];
        Hole = [.. Enumerable.Range(0, n).Select(_ => Array.Empty<int>())];
    }

    public bool InHand(int p) => Live && Dealt[p] && !Folded[p];
    bool Active(int p) => Dealt[p] && !Folded[p] && !AllIn[p];
    public int NotFolded => Enumerable.Range(0, N).Count(p => Dealt[p] && !Folded[p]);
    int ActiveCount => Enumerable.Range(0, N).Count(Active);

    /// <summary>Наступна за годинниковою позиція (після from), для якої ok; -1 — нема.</summary>
    public int Next(int from, Func<int, bool> ok)
    {
        for (var i = 1; i <= N; i++)
        {
            var q = ((from + i) % N + N) % N;
            if (ok(q)) return q;
        }
        return -1;
    }

    /// <summary>
    /// Нова роздача: <paramref name="dealIn"/> — хто отримує карти (у кожного мусять бути фішки), кнопка — на
    /// <paramref name="button"/> (гра рухає її сама), колода — 52 карти в порядку роздачі.
    /// </summary>
    public void StartHand(bool[] dealIn, int button, int sb, int bb, int[] deck)
    {
        var players = Enumerable.Range(0, N).Where(p => dealIn[p] && Stack[p] > 0).ToArray();
        if (players.Length < 2) throw new InvalidOperationException("роздача на одного");
        HandNo++;
        Live = true;
        Runout = false;
        ShowAll = false;
        Wins = [];
        Board = [];
        Deck = deck;
        DeckPos = 0;
        SmallBlind = sb;
        BigBlind = bb;
        for (var p = 0; p < N; p++)
        {
            Dealt[p] = players.Contains(p);
            Folded[p] = false;
            AllIn[p] = false;
            Acted[p] = false;
            Bet[p] = 0;
            Total[p] = 0;
            Shown[p] = false;
            Hole[p] = [];
        }
        Button = Dealt[button] ? button : Next(button, p => Dealt[p]);
        // Хедз-ап: кнопка ставить малий і ходить першою префлоп, а постфлоп — другою.
        SbPos = players.Length == 2 ? Button : Next(Button, p => Dealt[p]);
        BbPos = Next(SbPos, p => Dealt[p]);
        // по карті кожному, починаючи ліворуч від кнопки, двічі
        var order = new List<int>();
        for (var p = Next(Button, q => Dealt[q]); order.Count < players.Length; p = Next(p, q => Dealt[q])) order.Add(p);
        var holes = order.ToDictionary(p => p, _ => new List<int>(2));
        for (var round = 0; round < 2; round++)
            foreach (var p in order) holes[p].Add(Deck[DeckPos++]);
        foreach (var (p, h) in holes) Hole[p] = [.. h];

        Street = 0;
        Post(SbPos, sb);
        Post(BbPos, bb);
        CurrentBet = bb;
        MinRaise = bb;
        // Префлоп першим ходить той, хто після великого сліпого (у хедз-апі це кнопка-малий).
        Advance(BbPos);
    }

    void Post(int p, int amount)
    {
        var x = Math.Min(amount, Stack[p]);
        Stack[p] -= x;
        Bet[p] += x;
        Total[p] += x;
        if (Stack[p] == 0) AllIn[p] = true;
    }

    public PokerLegal? Legal(int p)
    {
        if (!Live || Runout || Turn != p) return null;
        var toCall = Math.Max(0, CurrentBet - Bet[p]);
        var call = Math.Min(toCall, Stack[p]);
        var all = Bet[p] + Stack[p];
        // Рейзити можна, якщо торги для мене відкриті (не ходив після останнього повного рейзу), фішок більше, ніж
        // на колл, і є кому відповісти (хоч один суперник ще не в олл-іні).
        var others = Enumerable.Range(0, N).Any(q => q != p && Active(q));
        int min = 0, max = 0;
        if (!Acted[p] && Stack[p] > toCall && others)
        {
            max = all;
            min = Math.Min(CurrentBet + MinRaise, all);
        }
        return new PokerLegal(toCall == 0, call, min, max, Stack[p]);
    }

    /// <summary>Хід позиції, чия черга. <paramref name="to"/> — для raise: сума ставки «до» в цьому колі.</summary>
    public void Act(int p, string action, int to = 0)
    {
        if (Legal(p) is not { } legal) throw new GameError("Зараз не твій хід");
        switch (action)
        {
            case "fold":
                Folded[p] = true;
                break;
            case "check":
                if (!legal.Check) throw new GameError($"Чек не можна: треба доставити {legal.Call}");
                break;
            case "call":
                if (legal.Check) break;   // колл на нуль — це чек
                Put(p, legal.Call);
                break;
            case "allin":
                if (legal.RaiseMax > 0) RaiseTo(p, Bet[p] + Stack[p]);
                else Put(p, Stack[p]);   // олл-ін, що не дотягує до рейзу (або рейзити вже не можна), — це колл
                break;
            case "raise":
                if (legal.RaiseMax == 0) throw new GameError(legal.Check ? "Рейзити тут не можна — лише чек" : "Рейзити тут не можна — лише колл або фолд");
                if (to > legal.RaiseMax) throw new GameError($"Стільки нема: щонайбільше {legal.RaiseMax}");
                if (to < legal.RaiseMin) throw new GameError($"Замало: рейз щонайменше до {legal.RaiseMin}");
                RaiseTo(p, to);
                break;
            default:
                throw new GameError("Тут так не ходять");
        }
        Acted[p] = true;
        Advance(p);
    }

    void Put(int p, int x)
    {
        x = Math.Min(x, Stack[p]);
        Stack[p] -= x;
        Bet[p] += x;
        Total[p] += x;
        if (Stack[p] == 0) AllIn[p] = true;
    }

    void RaiseTo(int p, int to)
    {
        var size = to - CurrentBet;
        Put(p, to - Bet[p]);
        if (Bet[p] <= CurrentBet) return;   // олл-ін на менше за колл
        if (size >= MinRaise)
        {
            // повний рейз: торги знову відкриті всім
            MinRaise = size;
            for (var q = 0; q < N; q++) if (q != p) Acted[q] = false;
        }
        // неповний олл-ін-рейз ставку підіймає, але торгів тим, хто вже ходив, не відкриває
        CurrentBet = Bet[p];
    }

    /// <summary>Позиція скидає поза чергою (встала з-за столу посеред роздачі). Поставлене лишається в банку.</summary>
    public void ForceFold(int p)
    {
        if (!InHand(p)) return;
        Folded[p] = true;
        Acted[p] = true;
        if (Turn == p || NotFolded <= 1) Advance(p);
        else if (Runout && NotFolded <= 1) Advance(p);
    }

    void Advance(int from)
    {
        if (NotFolded <= 1)
        {
            EndUncontested();
            return;
        }
        if (!Runout)
        {
            var next = Next(from, q => Active(q) && (!Acted[q] || Bet[q] < CurrentBet));
            if (next >= 0)
            {
                Turn = next;
                return;
            }
        }
        EndStreet();
    }

    /// <summary>Невикликану частину найбільшої ставки кола — назад власникові.</summary>
    void ReturnUncalled()
    {
        var top = -1;
        for (var p = 0; p < N; p++) if (Dealt[p] && (top < 0 || Bet[p] > Bet[top])) top = p;
        if (top < 0) return;
        var second = 0;
        for (var p = 0; p < N; p++) if (p != top && Dealt[p]) second = Math.Max(second, Bet[p]);
        var back = Bet[top] - second;
        if (back <= 0) return;
        Bet[top] -= back;
        Total[top] -= back;
        Stack[top] += back;
        if (Stack[top] > 0) AllIn[top] = false;
    }

    void EndStreet()
    {
        Turn = -1;
        ReturnUncalled();
        for (var p = 0; p < N; p++) Bet[p] = 0;
        CurrentBet = 0;
        if (NotFolded <= 1)
        {
            EndUncontested();
            return;
        }
        if (Street >= 3)
        {
            Showdown();
            return;
        }
        if (ActiveCount <= 1)
        {
            // торгів більше нема: карти на стіл, дошку докладає гра з паузами
            Runout = true;
            ShowAll = true;
            return;
        }
        DealStreet();
        MinRaise = BigBlind;
        for (var p = 0; p < N; p++) Acted[p] = false;
        Advance(Button);
    }

    void DealStreet()
    {
        Street++;
        var n = Street == 1 ? 3 : 1;
        for (var i = 0; i < n; i++) Board.Add(Deck[DeckPos++]);
    }

    /// <summary>Олл-ін, всі під'їхали: наступна вулиця на стіл (true — роздачу дограно, банки роздано).</summary>
    public bool RunoutStep()
    {
        if (!Live || !Runout) return !Live;
        if (Street < 3)
        {
            DealStreet();
            return false;
        }
        Showdown();
        return true;
    }

    /// <summary>Роздачу скасовано (турнір зупинено): кожному назад те, що він у неї вніс.</summary>
    public void VoidHand()
    {
        if (!Live) return;
        for (var p = 0; p < N; p++) Stack[p] += Total[p];
        Wins = [];
        Close();
    }

    /// <summary>Решта дошки одразу (тести, швидкий прогін).</summary>
    public void RunoutAll()
    {
        while (Live && Runout) RunoutStep();
    }

    void EndUncontested()
    {
        ReturnUncalled();
        var w = Enumerable.Range(0, N).First(p => Dealt[p] && !Folded[p]);
        var amount = Total.Sum();
        Stack[w] += amount;
        Wins = [new PokerPot(amount, [w], [amount], 0, false)];
        Close();
    }

    void Close()
    {
        for (var p = 0; p < N; p++)
        {
            Total[p] = 0;
            Bet[p] = 0;
            AllIn[p] = false;
        }
        Live = false;
        Runout = false;
        Turn = -1;
        Street = 4;
    }

    /// <summary>Побічні банки за внесками: рівні — внески тих, хто не скинув; у банку рівня — усі внески до нього.</summary>
    public List<PokerPotView> Pots(bool collectedOnly)
    {
        var paid = new int[N];
        for (var p = 0; p < N; p++) paid[p] = Total[p] - (collectedOnly ? Bet[p] : 0);
        var levels = Enumerable.Range(0, N).Where(p => Dealt[p] && !Folded[p]).Select(p => paid[p]).Where(x => x > 0).Distinct().Order().ToList();
        var pots = new List<PokerPotView>();
        var prev = 0;
        foreach (var level in levels)
        {
            var amount = 0;
            for (var p = 0; p < N; p++) amount += Math.Min(paid[p], level) - Math.Min(paid[p], prev);
            var seats = Enumerable.Range(0, N).Where(p => Dealt[p] && !Folded[p] && paid[p] >= level).ToArray();
            if (amount > 0) pots.Add(new PokerPotView(amount, seats));
            prev = level;
        }
        var rest = paid.Sum() - pots.Sum(x => x.Amount);
        if (rest > 0)
        {
            if (pots.Count == 0) pots.Add(new PokerPotView(rest, [.. Enumerable.Range(0, N).Where(p => Dealt[p] && !Folded[p])]));
            else pots[^1] = pots[^1] with { Amount = pots[^1].Amount + rest };
        }
        return pots;
    }

    public int Score(int p) => PokerHand.Eval([.. Hole[p], .. Board]);

    void Showdown()
    {
        ShowAll = true;
        var wins = new List<PokerPot>();
        foreach (var pot in Pots(false))
        {
            var scores = pot.Seats.ToDictionary(p => p, Score);
            var best = scores.Values.Max();
            // непарна фішка — першому переможцеві ліворуч від кнопки
            var winners = new List<int>();
            for (var p = Next(Button, _ => true); winners.Count < scores.Count(kv => kv.Value == best); p = Next(p, _ => true))
                if (scores.TryGetValue(p, out var s) && s == best) winners.Add(p);
            var share = pot.Amount / winners.Count;
            var odd = pot.Amount - share * winners.Count;
            var shares = winners.Select((_, i) => share + (i < odd ? 1 : 0)).ToArray();
            for (var i = 0; i < winners.Count; i++) Stack[winners[i]] += shares[i];
            wins.Add(new PokerPot(pot.Amount, [.. winners], shares, best, pot.Seats.Length > 1));
            foreach (var p in pot.Seats) Shown[p] = pot.Seats.Length > 1 || Shown[p];
        }
        Wins = wins;
        Close();
    }

    /// <summary>Скільки фішок усього: стеки + внесене в роздачу (для інваріанта).</summary>
    public long Chips => Stack.Sum(x => (long)x) + Total.Sum(x => (long)x);
}
