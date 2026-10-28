using static Hlechyky.Games.Impl.SkarbCards;

namespace Hlechyky.Games.Impl;

public enum SkPhase { Play, Round, Over }

/// <summary>
/// Подія для анімацій і журналу (§7), як <see cref="KhEvent"/>: одне поле може бути приховане (<see cref="SecretKey"/>) —
/// його бачать лише <see cref="Insiders"/>, решта — null. Поля нижче <see cref="Data"/> на дріт не йдуть: це публічні
/// підсумки ходу для Глеків (відстані фронту до міток до/після, чи глуха), щоб не рахувати їх наново.
/// </summary>
public sealed class SkEvent
{
    public long Seq;
    public string K = "";
    public List<KeyValuePair<string, object?>> Data = [];
    public string? SecretKey;
    public object? Secret;
    public int[] Insiders = [];

    /// <summary>Хто ходив (-1 — ніхто) і на кого (-1 — ні на кого).</summary>
    public int Seat = -1, Target = -1;
    /// <summary>Відстань фронту до кожної мітки до й після ходу/завалу (§8.1).</summary>
    public int[]? D0, D1;
    /// <summary>Хід — глухий і з'єднаний з колодязем; завал — прибрано глуху.</summary>
    public bool Dead;

    public Dictionary<string, object?> For(int? seat)
    {
        var d = new Dictionary<string, object?> { ["seq"] = Seq, ["k"] = K };
        foreach (var (k, v) in Data) d[k] = v;
        if (SecretKey is not null) d[SecretKey] = seat is { } s && Insiders.Contains(s) ? Secret : null;
        return d;
    }

    public object? this[string key] => Data.FirstOrDefault(d => d.Key == key).Value;
}

/// <summary>
/// Хід одного місця: карта й ціль. Для ходу — X, Y, R; зламати — Seat; полагодити — Seat і Tool (0..2, -1 — сам
/// здогадайся); мапа — Goal; завал — X, Y; <see cref="Pass"/> — скинути карту.
/// </summary>
public readonly record struct SkMove(int Card, int X = 0, int Y = 0, int R = 0, int Seat = -1, int Tool = -1, int Goal = -1, bool Pass = false);

/// <summary>Підсумок раунду (§5.1).</summary>
public sealed class SkRoundEnd
{
    public int N;
    public string Side = "none";
    public int? Finisher;
    public bool[] Mole = new bool[SkarbCore.MaxSeats];
    public bool AsideMole;
    public int GoldGoal;
    /// <summary>Хто скільки взяв: місце, злитків, карти (для кротів — null), у черзі видачі.</summary>
    public List<(int Seat, int N, int[]? Cards)> Gold = [];
    /// <summary>Злитки за партію після раунду, за порядком місць.</summary>
    public int[] Totals = [];
}

/// <summary>Сфотографований фронт (§8.1): порожні клітинки за відкритими з'єднаними краями й відстані до міток.</summary>
public sealed class SkFront
{
    /// <summary>Клітинки фронту, де можна класти (без міток), за зростанням індексу (тобто за y, потім x).</summary>
    public int[] Cells = [];
    /// <summary>Скільки з'єднаних країв дивиться в кожну клітинку <see cref="Cells"/>.</summary>
    public int[] Into = [];
    public int Edges;
    /// <summary>Найменша Манхеттенська відстань від фронту до кожної мітки (99 — фронту нема; 0 — впирається).</summary>
    public int[] Dist = [99, 99, 99];
    /// <summary>Найближча клітинка фронту до мітки і відстань без неї (для оцінки ходу за O(1)).</summary>
    public int[] BestCell = [-1, -1, -1];
    public int[] Second = [99, 99, 99];
}

/// <summary>
/// Правила «Скарбу Полуботка» (контракт §2) без каркаса: ні Ctx, ні годинника — випадковість приходить ззовні, порядок
/// її викликів фіксований (§2.1). Кожен хід — метод, що повертає помилку людською мовою або null; при помилці стан не
/// міняється. Поле — масив клітинок 17 × 9 (<see cref="SkarbCards.Cell"/>), мітки й колодязь там теж лежать як карти.
/// </summary>
public sealed class SkarbCore
{
    public const int MaxSeats = SkarbCards.MaxSeats;
    public const int StartCard = -2, GoalCard = -3;
    public static readonly int StartCell = Cell(0, 0);
    public static readonly int[] GoalCells = [Cell(GoalX, GoalY[0]), Cell(GoalX, GoalY[1]), Cell(GoalX, GoalY[2])];

    readonly Random _rng;
    public bool[] In = new bool[MaxSeats];
    /// <summary>Місця в грі за зростанням.</summary>
    public int[] Order = [];
    public int Seats => Order.Length;
    public bool Allies;
    public Func<int, string> NameOf = s => $"гравець {s + 1}";

    public SkPhase Phase = SkPhase.Play;
    public bool Started;
    public int Round;
    public int First, Turn, LastMover = -1;
    public int TurnsInRound, Turns;
    /// <summary>seq події «round» поточного раунду (події до неї — з минулих раундів).</summary>
    public long RoundSeq;

    // ролі
    public bool[] Mole = new bool[MaxSeats];
    public bool AsideMole;
    public int MolesInDeck, DiggersInDeck;

    // мітки
    public int GoldGoal;
    public bool[] GoalOpen = new bool[3];
    public int[] GoalR = new int[3];

    // поле
    public int[] CellM = new int[Cells];
    public bool[] CellC = new bool[Cells];
    public int[] CellCard = new int[Cells];
    public int[] CellR = new int[Cells];
    public int[] CellBy = new int[Cells];
    public string?[] CellS = new string?[Cells];
    /// <summary>Клітинки з ходами в порядку, як їх клали.</summary>
    public List<int> Placed = [];

    // карти
    public List<int> Deck = [];
    public List<int>[] Hand = [.. Enumerable.Range(0, MaxSeats).Select(_ => new List<int>())];
    public List<int> Discard = [];
    /// <summary>Зламані інструменти: біти кирка 1, лампа 2, тачка 4.</summary>
    public int[] Broken = new int[MaxSeats];
    /// <summary>Що місце бачило під мітками цього раунду: 0 — не дивилось, 1 — камінь, 2 — скриня.</summary>
    public int[][] Peek = [.. Enumerable.Range(0, MaxSeats).Select(_ => new int[3])];
    readonly bool[] _skipped = new bool[MaxSeats];

    // злитки
    public List<int> GoldDeck = [];
    public int[] Gold = new int[MaxSeats];
    public List<SkRoundEnd> Ends = [];
    public int[] Winners = [];
    public bool Abandoned;

    public List<SkEvent> Events = [];
    public long Seq;
    public List<string> Log = [];
    /// <summary>Ключові рядки для журналу кімнати (підсумки раундів і партії) — гра забирає їх у Ctx.Log.</summary>
    public List<string> Journal = [];

    // статистика (§9)
    public int[] Paths = new int[MaxSeats], DeadEnds = new int[MaxSeats], Breaks = new int[MaxSeats], Fixes = new int[MaxSeats],
        Maps = new int[MaxSeats], Rockfalls = new int[MaxSeats], Passes = new int[MaxSeats], MoleRounds = new int[MaxSeats],
        Won = new int[MaxSeats], MoleGoldGot = new int[MaxSeats], Finished = new int[MaxSeats];

    // кеш з'єднань: версія поля росте на кожну зміну
    int _ver;
    int _reachVer = -1, _frontVer = -1;
    readonly byte[] _reach = new byte[Cells];
    readonly byte[] _scratch = new byte[Cells];
    readonly int[] _stack = new int[Cells * 4];
    SkFront _front = new();

    public SkarbCore(IEnumerable<int> seats, Random rng)
    {
        _rng = rng;
        foreach (var s in seats) In[s] = true;
        Order = [.. Enumerable.Range(0, MaxSeats).Where(s => In[s])];
        (MolesInDeck, DiggersInDeck) = Pack(Math.Max(Order.Length, MinSeats));
        ClearBoard();
    }

    public bool Over => Phase == SkPhase.Over;

    // =====================================================================================
    // Партія й раунд (§2.1)
    // =====================================================================================

    /// <summary>Початок партії: тасуємо злитки, обираємо першого, роздаємо раунд 1.</summary>
    public void Begin()
    {
        var g = GoldCards();
        Shuffle(g);
        GoldDeck = [.. g];
        First = Order[_rng.Next(Order.Length)];
        Started = true;
        BeginRound();
    }

    /// <summary>Наступний раунд після паузи (фаза round): першим ходить наступний за тим, хто зробив останній хід.</summary>
    public string? NextRound()
    {
        if (Phase != SkPhase.Round) return "Зараз не перерва між раундами";
        First = NextSeat(LastMover);
        BeginRound();
        return null;
    }

    void Shuffle<T>(IList<T> a)
    {
        for (var i = a.Count - 1; i >= 1; i--)
        {
            var j = _rng.Next(i + 1);
            (a[i], a[j]) = (a[j], a[i]);
        }
    }

    /// <summary>Місця за порядком ходів, починаючи з <paramref name="from"/>.</summary>
    public IEnumerable<int> Ring(int from)
    {
        var i = Array.IndexOf(Order, from);
        if (i < 0) i = 0;
        for (var k = 0; k < Order.Length; k++) yield return Order[(i + k) % Order.Length];
    }

    public int NextSeat(int s)
    {
        var i = Array.IndexOf(Order, s);
        return Order[(i + 1) % Order.Length];
    }

    public int PrevSeat(int s)
    {
        var i = Array.IndexOf(Order, s);
        return Order[(i - 1 + Order.Length) % Order.Length];
    }

    void ClearBoard()
    {
        Array.Fill(CellM, -1);
        Array.Clear(CellC);
        Array.Fill(CellCard, -1);
        Array.Clear(CellR);
        Array.Fill(CellBy, -1);
        Array.Clear(CellS);
        Placed.Clear();
        CellM[StartCell] = Start.M;
        CellC[StartCell] = true;
        CellCard[StartCell] = StartCard;
        CellS[StartCell] = "start";
        Array.Clear(GoalOpen);
        Array.Clear(GoalR);
        _ver++;
    }

    void BeginRound()
    {
        Round++;
        Phase = SkPhase.Play;
        TurnsInRound = 0;
        var n = Order.Length;
        // 1) ролі: спершу кроти, потім копачі; по одній за порядком ходів від першого, остання — набік
        var roles = new List<bool>();
        for (var i = 0; i < MolesInDeck; i++) roles.Add(true);
        for (var i = 0; i < DiggersInDeck; i++) roles.Add(false);
        Shuffle(roles);
        Array.Clear(Mole);
        var k = 0;
        foreach (var s in Ring(First)) Mole[s] = roles[k++];
        AsideMole = roles[k];
        // 2) скриня — під однією з міток
        GoldGoal = _rng.Next(3);
        // 3) колода — у порядку id, тасуємо, роздаємо по HAND згори
        var deck = Enumerable.Range(0, DeckSize).ToList();
        Shuffle(deck);
        foreach (var h in Hand) h.Clear();
        var hand = SkarbCards.Hand(n);
        foreach (var s in Ring(First))
        {
            Hand[s].AddRange(deck.Take(hand));
            deck.RemoveRange(0, hand);
        }
        Deck = deck;
        Discard.Clear();
        Array.Clear(Broken);
        foreach (var p in Peek) Array.Clear(p);
        Array.Clear(_skipped);
        ClearBoard();
        Turn = First;
        RoundSeq = Emit("round", ("n", Round), ("first", First), ("hand", hand)).Seq;
        Say($"Раунд {Round}: кожному по {hand} карт, починає {NameOf(First)}");
        Emit("turn", ("seat", Turn));
    }

    /// <summary>Усі люди пішли: партія скінчилась без переможця.</summary>
    public void Abandon()
    {
        if (Over) return;
        Abandoned = true;
        Winners = [];
        Phase = SkPhase.Over;
    }

    // =====================================================================================
    // З'єднання (§2.4)
    // =====================================================================================

    /// <summary>
    /// Для кожної клітинки — біти країв, з'єднаних із колодязем. Вузли графа — (клітинка, край) для кожного відкритого краю
    /// карти; наскрізна карта з'єднує свої краї між собою, глуха — ні; сусіди — коли обидва краї до одне одного відкриті.
    /// </summary>
    public byte[] Reach()
    {
        if (_reachVer == _ver) return _reach;
        ComputeReach(_reach);
        _reachVer = _ver;
        return _reach;
    }

    void ComputeReach(byte[] r)
    {
        Array.Clear(r);
        var top = 0;
        r[StartCell] = (byte)CellM[StartCell];
        for (var d = 0; d < 4; d++) if ((CellM[StartCell] >> d & 1) != 0) _stack[top++] = StartCell * 4 + d;
        while (top > 0)
        {
            var node = _stack[--top];
            int cell = node >> 2, d = node & 3;
            var nb = Step(cell, d);
            if (nb < 0) continue;
            var m = CellM[nb];
            if (m < 0) continue;
            var od = Opp(d);
            if ((m >> od & 1) == 0 || (r[nb] >> od & 1) != 0) continue;
            if (CellC[nb])
            {
                var fresh = m & ~r[nb];
                r[nb] |= (byte)m;
                for (var e = 0; e < 4; e++)
                    if (e != od && (fresh >> e & 1) != 0) _stack[top++] = nb * 4 + e;
            }
            else r[nb] |= (byte)(1 << od);
        }
    }

    /// <summary>Клітинка з'єднана з колодязем (хоч один її край).</summary>
    public bool Linked(int cell) => Reach()[cell] != 0;

    static int Manh(int cell, int goal) => Math.Abs(CellX(cell) - GoalX) + Math.Abs(CellY(cell) - GoalY[goal]);

    public static int Distance(int cell, int goal) => Manh(cell, goal);

    /// <summary>Фронт (§8.1) — кешується на версію поля.</summary>
    public SkFront Front()
    {
        if (_frontVer == _ver) return _front;
        _front = BuildFront(Reach());
        _frontVer = _ver;
        return _front;
    }

    SkFront BuildFront(byte[] r)
    {
        var into = new Dictionary<int, int>();
        var f = new SkFront();
        var goalHit = new bool[3];
        for (var cell = 0; cell < Cells; cell++)
        {
            var bits = r[cell];
            if (bits == 0) continue;
            for (var d = 0; d < 4; d++)
            {
                if ((bits >> d & 1) == 0) continue;
                var nb = Step(cell, d);
                if (nb < 0 || CellM[nb] >= 0) continue;
                int nx = CellX(nb), ny = CellY(nb);
                if (IsShaft(nx, ny)) continue;
                var g = GoalAt(nx, ny);
                if (g >= 0) { goalHit[g] = true; continue; }
                into[nb] = into.GetValueOrDefault(nb) + 1;
                f.Edges++;
            }
        }
        f.Cells = [.. into.Keys.Order()];
        f.Into = [.. f.Cells.Select(c => into[c])];
        for (var g = 0; g < 3; g++)
        {
            int best = 99, second = 99, bestCell = -1;
            if (goalHit[g]) { best = 0; second = 0; }
            foreach (var c in f.Cells)
            {
                var d = Manh(c, g);
                if (d < best) { second = best; best = d; bestCell = c; }
                else if (d < second) second = d;
            }
            f.Dist[g] = best;
            f.BestCell[g] = goalHit[g] ? -1 : bestCell;
            f.Second[g] = second;
        }
        return f;
    }

    /// <summary>Відстань фронту до мітки, якби клітинки <paramref name="cell"/> не було (для завалу).</summary>
    public int DistWithout(int cell, int goal)
    {
        var (m, c) = (CellM[cell], CellC[cell]);
        CellM[cell] = -1;
        ComputeReach(_scratch);
        var f = BuildFront(_scratch);
        CellM[cell] = m;
        CellC[cell] = c;
        return f.Dist[goal];
    }

    public int[] Dists() => (int[])Front().Dist.Clone();

    // =====================================================================================
    // Законність (§2.3)
    // =====================================================================================

    public static SkCard CardOf(int id) => SkarbCards.Deck[id];

    public bool Has(int s, int card) => s >= 0 && s < MaxSeats && Hand[s].Contains(card);

    /// <summary>Чи сходяться краї маски <paramref name="m"/> у клітинці з усіма сусідами-картами.</summary>
    bool EdgesMatch(int cell, int m)
    {
        for (var d = 0; d < 4; d++)
        {
            var nb = Step(cell, d);
            if (nb < 0) continue;
            var nm = CellM[nb];
            if (nm < 0) continue;
            if ((m >> d & 1) != (nm >> Opp(d) & 1)) return false;
        }
        return true;
    }

    /// <summary>Хоч один мій відкритий край дивиться на відкритий з'єднаний край сусіда.</summary>
    bool Connects(int cell, int m)
    {
        var r = Reach();
        for (var d = 0; d < 4; d++)
        {
            if ((m >> d & 1) == 0) continue;
            var nb = Step(cell, d);
            if (nb < 0 || CellM[nb] < 0) continue;
            var od = Opp(d);
            if ((CellM[nb] >> od & 1) != 0 && (r[nb] >> od & 1) != 0) return true;
        }
        return false;
    }

    /// <summary>Помилка покласти маску в (x, y) (без перевірки руки й інструментів) або null.</summary>
    public string? PlaceError(int x, int y, int m)
    {
        if (!InBounds(x, y)) return "Далі тверда скеля — тут не копають";
        if (IsShaft(x, y) || (x == 0 && y == 0)) return "Тут колодязь — копай поруч";
        if (GoalAt(x, y) >= 0) return "Тут мітка скарбу — до неї треба докопатись";
        var cell = Cell(x, y);
        if (CellM[cell] >= 0) return "Тут уже прокопано";
        if (!EdgesMatch(cell, m)) return "Хід не сходиться з сусідньою картою";
        if (!Connects(cell, m)) return "Хід мусить тягнутись від колодязя";
        return null;
    }

    public int FirstBroken(int s) => FirstTool(Broken[s]);

    /// <summary>
    /// Усі законні ходи місця (крім пасу) — те саме, що прийме <see cref="Apply"/>, у порядку руки; для ходу — за y, x, r
    /// (симетричні — лише r = 0), для завалу — за y, x.
    /// </summary>
    public List<SkMove> Legal(int s)
    {
        var list = new List<SkMove>();
        if (Phase != SkPhase.Play || s != Turn) return list;
        var seen = new HashSet<int>();
        foreach (var id in Hand[s])
        {
            if (!seen.Add(id)) continue;
            LegalFor(s, id, list);
        }
        return list;
    }

    public void LegalFor(int s, int id, List<SkMove> list)
    {
        var card = CardOf(id);
        switch (card.K)
        {
            case SkKind.Path:
                {
                    if (Broken[s] != 0) break;
                    var sh = card.Shape!;
                    var sym = Symmetric(sh);
                    foreach (var cell in Front().Cells)
                        for (var r = 0; r < (sym ? 1 : 2); r++)
                        {
                            var m = Mask(sh, r);
                            if (EdgesMatch(cell, m) && Connects(cell, m)) list.Add(new SkMove(id, CellX(cell), CellY(cell), r));
                        }
                    break;
                }
            case SkKind.Break:
                foreach (var t in Order)
                    if (t != s && (Broken[t] & card.Tools) == 0) list.Add(new SkMove(id, Seat: t));
                break;
            case SkKind.Fix:
                foreach (var t in Order)
                    for (var tool = 0; tool < 3; tool++)
                        if ((card.Tools >> tool & 1) != 0 && (Broken[t] >> tool & 1) != 0) list.Add(new SkMove(id, Seat: t, Tool: tool));
                break;
            case SkKind.Map:
                for (var g = 0; g < 3; g++) if (!GoalOpen[g]) list.Add(new SkMove(id, Goal: g));
                break;
            case SkKind.Rockfall:
                foreach (var cell in Placed.Order()) list.Add(new SkMove(id, CellX(cell), CellY(cell)));
                break;
        }
    }

    /// <summary>Чому для карти нема жодної цілі (§5 why).</summary>
    public string WhyNot(int s, int id) => CardOf(id).K switch
    {
        SkKind.Path => Broken[s] != 0 ? BrokenWhy[FirstBroken(s)] : "Цій карті нема де лягти",
        SkKind.Break => "Усім, кому можна, вже зламано",
        SkKind.Fix => "Нема що лагодити",
        SkKind.Map => "Усі мітки вже відкриті",
        _ => "Нема що завалювати",
    };

    // =====================================================================================
    // Ходи (§2.2, §2.5, §6)
    // =====================================================================================

    /// <summary>Спільні перевірки play/pass: фаза й черга.</summary>
    public string? Gate(int s)
    {
        if (Phase == SkPhase.Over) return "Партія вже скінчилась";
        if (Phase == SkPhase.Round) return "Раунд скінчився — чекаємо наступного";
        if (s != Turn) return $"Зараз копає {NameOf(Turn)}";
        return null;
    }

    /// <summary>Хід Глека (чи будь-який готовий <see cref="SkMove"/>) з усіма перевірками.</summary>
    public string? Apply(int s, SkMove mv)
    {
        if (Gate(s) is { } g) return g;
        if (!Has(s, mv.Card)) return "Такої карти в тебе нема";
        if (mv.Pass) return Pass(s, mv.Card);
        return CardOf(mv.Card).K switch
        {
            SkKind.Path => Place(s, mv.Card, mv.X, mv.Y, mv.R),
            SkKind.Break => BreakTool(s, mv.Card, mv.Seat),
            SkKind.Fix => FixTool(s, mv.Card, mv.Seat, mv.Tool < 0 ? null : mv.Tool),
            SkKind.Map => LookMap(s, mv.Card, mv.Goal),
            _ => Rockfall(s, mv.Card, mv.X, mv.Y),
        };
    }

    string? Common(int s, int card, SkKind want)
    {
        if (Gate(s) is { } g) return g;
        if (!Has(s, card)) return "Такої карти в тебе нема";
        return CardOf(card).K == want ? null : "Тут так не ходять";
    }

    /// <summary>Покласти хід.</summary>
    public string? Place(int s, int card, int? x, int? y, int r = 0)
    {
        if (Common(s, card, SkKind.Path) is { } e) return e;
        if (x is not { } px || y is not { } py) return "Не зрозумів, куди класти";
        if (Broken[s] != 0) return BrokenWhy[FirstBroken(s)];
        if (r is not (0 or 1)) return "Карту можна лише перевернути: 0 або 180°";
        var sh = CardOf(card).Shape!;
        if (Symmetric(sh)) r = 0;
        var m = Mask(sh, r);
        if (PlaceError(px, py, m) is { } err) return err;

        var cell = Cell(px, py);
        var d0 = Dists();
        Hand[s].Remove(card);
        CellM[cell] = m;
        CellC[cell] = sh.C;
        CellCard[cell] = card;
        CellR[cell] = r;
        CellBy[cell] = s;
        CellS[cell] = sh.S;
        Placed.Add(cell);
        _ver++;
        Paths[s]++;
        var dead = !sh.C && Linked(cell);
        if (!sh.C) DeadEnds[s]++;
        var ev = Emit("place", ("seat", s), ("card", card), ("x", px), ("y", py), ("s", sh.S), ("r", r), ("m", m), ("c", sh.C));
        ev.Seat = s;
        ev.D0 = d0;
        ev.D1 = Dists();
        ev.Dead = dead;
        Say(sh.C ? $"{NameOf(s)} прокопав хід" : $"{NameOf(s)} прокопав глухий хід");
        if (OpenGoals(s))
        {
            EndRound(s);
            return null;
        }
        EndTurn(s);
        return null;
    }

    /// <summary>
    /// Відкрити закриті мітки, до яких дійшов з'єднаний край (§2.4): поворот — за першим досяжним напрямом серед W N S E;
    /// камінь перераховує граф — повторюємо, доки нових відкриттів нема. true — відкрили скриню.
    /// </summary>
    bool OpenGoals(int by)
    {
        while (true)
        {
            var r = Reach();
            var hits = new List<(int G, int D)>();
            for (var g = 0; g < 3; g++)
                if (!GoalOpen[g] && ReachDir(r, g) is { } d) hits.Add((g, d));
            if (hits.Count == 0) return false;
            var gold = false;
            foreach (var (g, d) in hits)
            {
                var sh = GoalShape(g);
                var rr = (sh.M >> d & 1) != 0 ? 0 : (Rot(sh.M) >> d & 1) != 0 ? 1 : 0;
                var m = Mask(sh, rr);
                var cell = GoalCells[g];
                GoalOpen[g] = true;
                GoalR[g] = rr;
                CellM[cell] = m;
                CellC[cell] = true;
                CellCard[cell] = GoalCard;
                CellR[cell] = rr;
                CellS[cell] = sh.S;
                _ver++;
                var kind = g == GoldGoal ? "gold" : "stone";
                Emit("goal", ("i", g), ("x", GoalX), ("y", GoalY[g]), ("kind", kind), ("s", sh.S), ("r", rr), ("m", m), ("c", true), ("by", by)).Seat = by;
                if (g == GoldGoal)
                {
                    gold = true;
                    Say($"{NameOf(by)} докопався до скарбу Полуботка!");
                }
                else Say($"Відкопали {GoalPlaceAcc[g]} — там лише каміння");
            }
            if (gold) return true;
        }
    }

    /// <summary>Порядок, у якому мітка шукає, хто до неї докопався: W N S E (§2.4).</summary>
    static readonly int[] ReachOrder = [3, 0, 2, 1];

    /// <summary>Напрям від мітки до першої (W N S E) сусідньої карти, чий край до мітки відкритий і з'єднаний, або null.</summary>
    int? ReachDir(byte[] r, int g)
    {
        var gc = GoalCells[g];
        foreach (var d in ReachOrder)
        {
            var nb = Step(gc, d);
            if (nb < 0 || CellM[nb] < 0) continue;
            var od = Opp(d);
            if ((CellM[nb] >> od & 1) != 0 && (r[nb] >> od & 1) != 0) return d;
        }
        return null;
    }

    /// <summary>Форма мітки: скриня або камінь A (менший індекс) / B (більший).</summary>
    public SkShape GoalShape(int g)
    {
        if (g == GoldGoal) return SkarbCards.Gold;
        var other = Enumerable.Range(0, 3).Where(i => i != GoldGoal && i != g).Single();
        return g < other ? StoneA : StoneB;
    }

    /// <summary>
    /// Чи відкрив би хід (маска <paramref name="m"/> у клітинці) скриню — з каскадом каменів. Для Глека-крота (§8.1 п. 1):
    /// рахуємо на позиченому полі й усе повертаємо.
    /// </summary>
    public bool WouldOpenGold(int x, int y, int m, bool c)
    {
        var cell = Cell(x, y);
        // швидко: без сусідньої закритої мітки й без чужої відірваної карти нового з'єднаного нема — лише сама карта
        var near = false;
        for (var d = 0; d < 4 && !near; d++)
        {
            var nb = Step(cell, d);
            if (nb < 0) continue;
            var g = Array.IndexOf(GoalCells, nb);
            if (g >= 0 && !GoalOpen[g]) near = true;
            else if ((m >> d & 1) != 0 && CellM[nb] >= 0 && !Linked(nb)) near = true;
        }
        if (!near) return false;

        var (sm, sc) = (CellM[cell], CellC[cell]);
        CellM[cell] = m;
        CellC[cell] = c;
        var opened = new List<int>();
        var gold = false;
        while (!gold)
        {
            ComputeReach(_scratch);
            var any = false;
            for (var g = 0; g < 3; g++)
            {
                if (CellM[GoalCells[g]] >= 0 || ReachDir(_scratch, g) is not { } d) continue;
                any = true;
                if (g == GoldGoal) { gold = true; break; }
                var sh = GoalShape(g);
                var rr = (sh.M >> d & 1) != 0 ? 0 : 1;
                CellM[GoalCells[g]] = Mask(sh, rr);
                CellC[GoalCells[g]] = true;
                opened.Add(g);
            }
            if (!any) break;
        }
        foreach (var g in opened)
        {
            CellM[GoalCells[g]] = -1;
            CellC[GoalCells[g]] = false;
        }
        CellM[cell] = sm;
        CellC[cell] = sc;
        return gold;
    }

    /// <summary>Які закриті мітки хід досяг би одразу (без каскаду) — біти 0..2. Для підказки людині без підглядання.</summary>
    public int WouldReach(int x, int y, int m, bool c)
    {
        var cell = Cell(x, y);
        var (sm, sc) = (CellM[cell], CellC[cell]);
        CellM[cell] = m;
        CellC[cell] = c;
        ComputeReach(_scratch);
        var bits = 0;
        for (var g = 0; g < 3; g++)
            if (!GoalOpen[g] && ReachDir(_scratch, g) is not null) bits |= 1 << g;
        CellM[cell] = sm;
        CellC[cell] = sc;
        return bits;
    }

    /// <summary>Зламати інструмент іншому.</summary>
    public string? BreakTool(int s, int card, int? target)
    {
        if (Common(s, card, SkKind.Break) is { } e) return e;
        if (target is not { } t) return "Не зрозумів, кому";
        if (t < 0 || t >= MaxSeats || !In[t]) return "Такого копача за столом нема";
        if (t == s) return "Собі ламати не годиться";
        var tools = CardOf(card).Tools;
        var tool = FirstTool(tools);
        if ((Broken[t] & tools) != 0) return $"У {NameOf(t)} {ToolAcc[tool]} вже зламано";
        Hand[s].Remove(card);
        Discard.Add(card);
        Broken[t] |= tools;
        Breaks[s]++;
        var ev = Emit("break", ("seat", s), ("card", card), ("target", t), ("tool", Tools[tool]));
        ev.Seat = s;
        ev.Target = t;
        Say(tool switch
        {
            0 => $"{NameOf(s)} зламав кирку {NameOf(t)}",
            1 => $"{NameOf(s)} розбив лампу {NameOf(t)}",
            _ => $"{NameOf(s)} зняв колесо з тачки {NameOf(t)}",
        });
        EndTurn(s);
        return null;
    }

    /// <summary>Полагодити (себе теж). Подвійна без <paramref name="tool"/> — сама, якщо зламано рівно один із двох.</summary>
    public string? FixTool(int s, int card, int? target, int? tool)
    {
        if (Common(s, card, SkKind.Fix) is { } e) return e;
        if (target is not { } t) return "Не зрозумів, кому лагодити";
        if (t < 0 || t >= MaxSeats || !In[t]) return "Такого копача за столом нема";
        var tools = CardOf(card).Tools;
        int fix;
        if (tool is { } want)
        {
            if (want < 0 || want > 2 || (tools >> want & 1) == 0) return "Скажи, що саме лагодити";
            fix = want;
        }
        else
        {
            var both = tools & Broken[t];
            if (both != 0 && (both & (both - 1)) != 0) return "Скажи, що саме лагодити";
            fix = both != 0 ? FirstTool(both) : FirstTool(tools);
        }
        if ((Broken[t] >> fix & 1) == 0) return $"Тут нічого лагодити — {ToolName[fix]} ціла";
        Hand[s].Remove(card);
        Discard.Add(card);
        Broken[t] &= ~(1 << fix);
        Fixes[s]++;
        var ev = Emit("fix", ("seat", s), ("card", card), ("target", t), ("tool", Tools[fix]));
        ev.Seat = s;
        ev.Target = t;
        Say(t == s ? $"{NameOf(s)} полагодив собі {ToolAcc[fix]}" : $"{NameOf(s)} полагодив {ToolAcc[fix]} {NameOf(t)}");
        EndTurn(s);
        return null;
    }

    /// <summary>Стара мапа: таємно глянути під закриту мітку.</summary>
    public string? LookMap(int s, int card, int? goal)
    {
        if (Common(s, card, SkKind.Map) is { } e) return e;
        if (goal is not (>= 0 and <= 2)) return "Не зрозумів, яку мітку";
        var g = goal.Value;
        if (GoalOpen[g]) return "Цю мітку вже відкрито";
        Hand[s].Remove(card);
        Discard.Add(card);
        var kind = g == GoldGoal ? "gold" : "stone";
        Peek[s][g] = g == GoldGoal ? 2 : 1;
        Maps[s]++;
        var ev = Emit("map", [("seat", s), ("card", card), ("goal", g)], "kind", kind, [s]);
        ev.Seat = s;
        Say($"{NameOf(s)} глянув старою мапою під {GoalPlaceAcc[g]}");
        EndTurn(s);
        return null;
    }

    /// <summary>Завал: прибрати будь-який хід (не колодязь і не мітку).</summary>
    public string? Rockfall(int s, int card, int? x, int? y)
    {
        if (Common(s, card, SkKind.Rockfall) is { } e) return e;
        if (x is not { } px || y is not { } py) return "Не зрозумів, що завалити";
        if ((px == 0 && py == 0) || GoalAt(px, py) >= 0) return "Колодязь і мітки не завалиш";
        if (!InBounds(px, py) || CellCard[Cell(px, py)] < 0) return "Тут нема ходу, щоб завалити";
        var cell = Cell(px, py);
        var d0 = Dists();
        var dead = !CellC[cell];
        var shape = CellS[cell];
        Hand[s].Remove(card);
        Discard.Add(card);
        Discard.Add(CellCard[cell]);
        CellM[cell] = -1;
        CellC[cell] = false;
        CellCard[cell] = -1;
        CellR[cell] = 0;
        CellBy[cell] = -1;
        CellS[cell] = null;
        Placed.Remove(cell);
        _ver++;
        Rockfalls[s]++;
        var ev = Emit("rockfall", ("seat", s), ("card", card), ("x", px), ("y", py), ("s", shape));
        ev.Seat = s;
        ev.D0 = d0;
        ev.D1 = Dists();
        ev.Dead = dead;
        Say($"{NameOf(s)} завалив хід");
        EndTurn(s);
        return null;
    }

    /// <summary>Пас: скинути карту сорочкою догори (яку — нікому не кажемо).</summary>
    public string? Pass(int s, int? card)
    {
        if (Gate(s) is { } g) return g;
        if (card is not { } id) return "Не зрозумів, яку карту скинути";
        if (!Has(s, id)) return "Такої карти в тебе нема";
        Hand[s].Remove(id);
        Discard.Add(id);
        Passes[s]++;
        Emit("pass", ("seat", s)).Seat = s;
        Say($"{NameOf(s)} скинув карту");
        EndTurn(s);
        return null;
    }

    /// <summary>Після дії: витягти карту, передати хід наступному з картами або скінчити раунд (кроти).</summary>
    void EndTurn(int s)
    {
        LastMover = s;
        Turns++;
        TurnsInRound++;
        if (Deck.Count > 0)
        {
            Hand[s].Add(Deck[0]);
            Deck.RemoveAt(0);
            if (Deck.Count == 0)
            {
                Emit("deckout");
                Say("Колода скінчилась — доходимо тим, що в руках");
            }
        }
        if (Order.All(o => Hand[o].Count == 0))
        {
            EndRound(null);
            return;
        }
        foreach (var n in Ring(s).Skip(1).Append(s))
        {
            if (Hand[n].Count > 0)
            {
                Turn = n;
                Emit("turn", ("seat", n));
                return;
            }
            // Порожні руки — пропускаємо; кажемо про це раз за раунд, щоб журнал не тонув.
            if (_skipped[n]) continue;
            _skipped[n] = true;
            Emit("skip", ("seat", n)).Seat = n;
            Say($"у {NameOf(n)} порожні руки");
        }
    }

    // =====================================================================================
    // Кінець раунду й партії (§2.6, §2.7)
    // =====================================================================================

    /// <summary>Скриню відкрив <paramref name="finisher"/> (копачі) або null — колода й руки скінчились.</summary>
    void EndRound(int? finisher)
    {
        var end = new SkRoundEnd { N = Round, Finisher = finisher, AsideMole = AsideMole, GoldGoal = GoldGoal };
        Array.Copy(Mole, end.Mole, MaxSeats);
        var moles = Order.Where(o => Mole[o]).ToArray();
        if (finisher is { } f)
        {
            end.Side = "diggers";
            var k = Math.Min(Math.Min(Order.Length, 9), GoldDeck.Count);
            var cards = GoldDeck.Take(k).OrderDescending().ToArray();
            GoldDeck.RemoveRange(0, k);
            // Черга: finisher, якщо копач, інакше перший копач «назад» від нього; далі «назад», лише копачі.
            var queue = new List<int>();
            var cur = f;
            for (var i = 0; i < Order.Length; i++)
            {
                if (!Mole[cur]) queue.Add(cur);
                cur = PrevSeat(cur);
            }
            var got = queue.ToDictionary(q => q, _ => new List<int>());
            for (var i = 0; i < cards.Length && queue.Count > 0; i++) got[queue[i % queue.Count]].Add(cards[i]);
            foreach (var q in queue)
            {
                if (got[q].Count == 0) continue;
                var n = got[q].Sum();
                Gold[q] += n;
                end.Gold.Add((q, n, [.. got[q]]));
            }
            Finished[f]++;
            Say($"Копачі докопались! Скарб Полуботка відкопав {NameOf(f)}", journal: true);
        }
        else if (moles.Length > 0)
        {
            end.Side = "moles";
            var each = MoleGold(moles.Length);
            foreach (var m in moles)
            {
                Gold[m] += each;
                MoleGoldGot[m] += each;
                end.Gold.Add((m, each, null));
            }
            Say($"Кроти взяли своє: {string.Join(", ", moles.Select(NameOf))} — по {Bars(each)}", journal: true);
        }
        else
        {
            end.Side = "none";
            Say("Колода скінчилась, а скарб лишився в землі — кротів цього разу не було", journal: true);
        }
        foreach (var o in Order)
        {
            if (Mole[o]) MoleRounds[o]++;
            if ((end.Side == "moles" && Mole[o]) || (end.Side == "diggers" && !Mole[o])) Won[o]++;
        }
        end.Totals = [.. Order.Select(o => Gold[o])];
        Ends.Add(end);
        Emit("end", ("n", Round), ("side", end.Side), ("finisher", finisher));
        if (Round >= Rounds)
        {
            Phase = SkPhase.Over;
            var top = Order.Max(o => Gold[o]);
            var rich = Order.Where(o => Gold[o] == top).ToArray();
            var best = rich.Max(o => Won[o]);
            Winners = [.. rich.Where(o => Won[o] == best)];
            Emit("over", ("winners", (int[])Winners.Clone()), ("gold", Order.Select(o => Gold[o]).ToArray()));
            Say(Winners.Length == 1
                ? $"Найбагатший — {NameOf(Winners[0])}: {Bars(Gold[Winners[0]])}"
                : $"Скарб поділили: {string.Join(", ", Winners.Select(NameOf))} — по {Bars(top)}", journal: true);
        }
        else Phase = SkPhase.Round;
    }

    /// <summary>Покласти хід руками, без правил і без відкриття міток (тести й стенди).</summary>
    public void Put(int x, int y, string shape, int r = 0, int by = -1, int id = -1)
    {
        var sh = ShapeOf(shape);
        var cell = Cell(x, y);
        Placed.Remove(cell);
        CellM[cell] = Mask(sh, r);
        CellC[cell] = sh.C;
        CellCard[cell] = id >= 0 ? id : SkarbCards.Deck.First(d => d.Shape == sh).Id;
        CellR[cell] = r;
        CellBy[cell] = by;
        CellS[cell] = sh.S;
        Placed.Add(cell);
        _ver++;
    }

    /// <summary>Скільки кротів у грі цього раунду.</summary>
    public int MolesInPlay => Order.Count(o => Mole[o]);

    // =====================================================================================
    // Копія (Глек рахує «а що, якби», тести)
    // =====================================================================================

    public SkarbCore Clone(Random? rng = null)
    {
        var c = new SkarbCore(Order, rng ?? new Random(0))
        {
            Allies = Allies, NameOf = NameOf, Phase = Phase, Started = Started, Round = Round, First = First, Turn = Turn,
            LastMover = LastMover, TurnsInRound = TurnsInRound, Turns = Turns, RoundSeq = RoundSeq, AsideMole = AsideMole, GoldGoal = GoldGoal,
            Deck = [.. Deck], Discard = [.. Discard], GoldDeck = [.. GoldDeck], Placed = [.. Placed], Seq = Seq,
            Abandoned = Abandoned, Winners = (int[])Winners.Clone(), Ends = [.. Ends], Log = [.. Log],
            Events = [.. Events],
        };
        Array.Copy(Mole, c.Mole, MaxSeats);
        Array.Copy(GoalOpen, c.GoalOpen, 3);
        Array.Copy(GoalR, c.GoalR, 3);
        Array.Copy(CellM, c.CellM, Cells);
        Array.Copy(CellC, c.CellC, Cells);
        Array.Copy(CellCard, c.CellCard, Cells);
        Array.Copy(CellR, c.CellR, Cells);
        Array.Copy(CellBy, c.CellBy, Cells);
        Array.Copy(CellS, c.CellS, Cells);
        Array.Copy(Broken, c.Broken, MaxSeats);
        Array.Copy(Gold, c.Gold, MaxSeats);
        Array.Copy(_skipped, c._skipped, MaxSeats);
        for (var s = 0; s < MaxSeats; s++)
        {
            c.Hand[s] = [.. Hand[s]];
            c.Peek[s] = (int[])Peek[s].Clone();
        }
        foreach (var (src, dst) in new[]
        {
            (Paths, c.Paths), (DeadEnds, c.DeadEnds), (Breaks, c.Breaks), (Fixes, c.Fixes), (Maps, c.Maps), (Rockfalls, c.Rockfalls),
            (Passes, c.Passes), (MoleRounds, c.MoleRounds), (Won, c.Won), (MoleGoldGot, c.MoleGoldGot), (Finished, c.Finished),
        }) Array.Copy(src, dst, MaxSeats);
        c._ver++;
        return c;
    }

    // =====================================================================================
    // Події й журнал
    // =====================================================================================

    SkEvent Emit(string k, params (string key, object? val)[] data) => Emit(k, data, null, null, []);

    SkEvent Emit(string k, (string key, object? val)[] data, string? secretKey, object? secret, int[] insiders)
    {
        var e = new SkEvent
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

    /// <summary>Записати рядок у журнал столу (гра — про вихід, Глека тощо).</summary>
    public void Note(string line) => Say(line);
}
