using System.Security.Cryptography;
using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Ставка гравця на наступну роздачу (лише намір — гаманець не чіпається до «Роздаю!»).</summary>
public sealed class BjBet
{
    public string Nick { get; set; } = "";
    /// <summary>Місце — колір боксу й порядок ходу.</summary>
    public int Color { get; set; }
    public int Amount { get; set; }
    /// <summary>«Роздавай!»: коли готові всі, хто сидить, Глек не чекає кінця вікна ставок.</summary>
    public bool Ready { get; set; }
}

public sealed class BjGlek
{
    public string Mood { get; set; } = "idle";
    public string? Say { get; set; }
    public long Seq { get; set; }
}

public sealed class BjResult
{
    public string Nick { get; set; } = "";
    public int Color { get; set; }
    public int Staked { get; set; }
    public int Paid { get; set; }
    public int Net { get; set; }
    /// <summary>Підсумок кожної руки: bj | win | push | lose | bust.</summary>
    public List<string> Hands { get; set; } = [];
    /// <summary>Найбільше карт у руці на 21 (≥ 5 — «💯 21 з п'яти карт»), 0 — нема.</summary>
    public int Five { get; set; }
}

/// <summary>Остання розрахована роздача: підсумки, карти Глека й seed (перевірка «чесно наперед»).</summary>
public sealed class BjLast
{
    public int Round { get; set; }
    public string Seed { get; set; } = "";
    public string Hash { get; set; } = "";
    public List<string> Dealer { get; set; } = [];
    public bool DealerBj { get; set; }
    /// <summary>Усі карти роздачі в порядку, як ішли з колоди.</summary>
    public List<string> Drawn { get; set; } = [];
    public string? Big { get; set; }
    public List<BjResult> Results { get; set; } = [];
}

public sealed class BjMark
{
    public string Nick { get; set; } = "";
    public int N { get; set; }
}

/// <summary>Рекорди столу (спільний — від створення столу; соло — свої, назавжди).</summary>
public sealed class BjRecords
{
    public BjMark? Win { get; set; }
    public BjMark? Streak { get; set; }
    /// <summary>Хто останнім зібрав 21 з п'яти карт і скільки таких рук за столом загалом.</summary>
    public BjMark? Five { get; set; }
    public int Fives { get; set; }
}

/// <summary>Увесь стан «Двадцять одно» — одним JSON-ом (Save/Load, продовження після перезапуску). Словники — за ключем ніка.</summary>
public sealed class BlackjackState
{
    public string Phase { get; set; } = BlackjackGame.Bets;
    public DateTimeOffset? Until { get; set; }
    /// <summary>Лічильник роздач: росте на кожну спробу роздати — ключ леджера не повторюється.</summary>
    public int RoundNo { get; set; }
    /// <summary>Лише для довідки: Load бере новий.</summary>
    public string Epoch { get; set; } = "";
    /// <summary>Seed наступної (чи поточної) роздачі: його відбиток видно до ставок, сам seed — після розрахунку.</summary>
    public string Seed { get; set; } = "";
    /// <summary>Тести: карти, що підуть першими (перед колодою з seed).</summary>
    public List<string> Head { get; set; } = [];
    /// <summary>Скільки карт роздачі вже взято (Head, далі колода з seed).</summary>
    public int Drawn { get; set; }
    public List<BjBet> Bets { get; set; } = [];
    public BjDeal? Deal { get; set; }
    /// <summary>Запис каси поточної роздачі (від «Роздаю!» до виплати).</summary>
    public BjRound? Pending { get; set; }
    /// <summary>Мить, коли Глек почав відкривати свої карти (фаза glek).</summary>
    public DateTimeOffset? GlekAt { get; set; }
    public int Shown { get; set; }
    public BjLast? Last { get; set; }
    /// <summary>Ставка минулої роздачі (для «Як минулого разу»).</summary>
    public Dictionary<string, int> LastBet { get; set; } = [];
    /// <summary>Скільки роздач поспіль у плюсі (нічия серії не рве й не подовжує).</summary>
    public Dictionary<string, int> Streak { get; set; } = [];
    public BjRecords Records { get; set; } = new();
    public int EmptyRounds { get; set; }
    public bool Hurried { get; set; }
    public BjGlek Glek { get; set; } = new();
    public DateTimeOffset? JournalAt { get; set; }
    public DateTimeOffset LastActionAt { get; set; }
    /// <summary>Кеш гаманців: підказка для виду, правда — гаманець у шапці сайту.</summary>
    public Dictionary<string, int> Wallets { get; set; } = [];
    public Dictionary<string, string> Notes { get; set; } = [];
}

/// <summary>
/// «Двадцять одно в Глека» (docs/games/specs/blackjack.md): блекджек проти Дядька Глека. Спільне для столу й соло: ставки,
/// роздача (каса <see cref="BlackjackBook"/>), ходи, гра Глека, розрахунок, вид, Save/Load. Різниця — лише фази: стіл
/// роздає за розкладом і чекає на кожне рішення 15 с (<see cref="Blackjack"/>), соло — коли тиснеш «Роздати»
/// (<see cref="BlackjackSolo"/>). Правила й карти — <see cref="BlackjackCore"/>/<see cref="BjDeal"/>.
/// </summary>
public abstract class BlackjackGame : Game
{
    public const int TickMs = 250;
    public const int BetMs = 15_000, TurnMs = 15_000, HurryMs = 5_000, FlipMs = 700, StepMs = 800, EndMs = 1_000,
        ResultMs = 4_000, DozeMs = 120_000;
    public const int EmptyToDoze = 3;
    public static readonly TimeSpan JournalGap = TimeSpan.FromMinutes(10);
    /// <summary>Найбільше, що поверне бокс: три руки по виграшу (подвоєння після спліту нема) — 6 ставок.</summary>
    public const int MaxReturnX = 6;

    public const string Idle = "idle", Bets = "bets", Play = "play", GlekTurn = "glek", Result = "result";
    public const string ActBet = "bet", ActClear = "clear", ActRebet = "rebet", ActDeal = "deal";

    public const string ClosedText = "Каса зачинена — спробуй трохи згодом";
    public const string OffText = "Глек відпочиває — ставок зараз не приймаю";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected BlackjackState S = new();
    BlackjackBook? _book;
    bool _dirty;
    string[]? _shoe;
    string _shoeSeed = "";
    bool _settledOnLoad;

    /// <summary>Шов лише для тестів: ці карти підуть першими в наступній роздачі (далі — колода з seed).</summary>
    public List<string>? Rig { get; set; }
    /// <summary>Для тестів: стан гри.</summary>
    public BlackjackState State => S;

    protected abstract bool Solo { get; }
    DateTimeOffset Now => Ctx.Clock.UtcNow;
    BlackjackOptions Opts => _book?.Options ?? new BlackjackOptions();
    string Table => $"{Ctx.RoomId}:{S.Epoch}";

    public override string SeatName(int seat) => $"місце {seat + 1}";

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        try { _book = Ctx?.Services?.GetService<BlackjackBook>(); }
        catch { _book = null; }
        if (_book is { Options.Enabled: false }) throw new GameError(OffText);
    }

    /// <summary>Чистий стіл (створення, «Ще раз» після переривання). Рекорди, серії, минулі ставки й лічильник реплік живуть далі.</summary>
    public override void Start()
    {
        var keep = S;
        S = new BlackjackState
        {
            RoundNo = keep.RoundNo,
            Last = keep.Last,
            LastBet = keep.LastBet,
            Streak = keep.Streak,
            Records = keep.Records,
            JournalAt = keep.JournalAt,
            Glek = keep.Glek,
            Epoch = NewEpoch(),
            LastActionAt = Now,
        };
        OpenBets(afterSigh: false);
    }

    public override object? Frame() => null;

    // ---------- дії ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (_book is null) return ActResult.Fail(ClosedText);
        if (Ctx.NickOf(seat) is not { } nick) return ActResult.Fail("Тут так не ходять");
        var key = Key(nick);
        var now = Now;
        ActResult result;
        switch (action)
        {
            case ActBet or ActClear or ActRebet or ActDeal:
                if (BetPhaseRefusal(now) is { } no) return ActResult.Fail(no);
                if (action != ActClear && !Opts.Enabled) return ActResult.Fail(OffText);
                var wallet = _book.Balance(nick);
                S.Wallets[key] = wallet;
                result = action switch
                {
                    ActBet => Bet(seat, nick, key, payload, wallet),
                    ActClear => Clear(key),
                    ActRebet => Rebet(seat, nick, key, wallet),
                    _ => Deal(seat, nick, key, payload, wallet),
                };
                break;
            case BjDeal.Hit or BjDeal.Stand or BjDeal.Double or BjDeal.Split:
                result = Move(nick, key, action, now);
                break;
            default:
                return ActResult.Fail("Тут так не ходять");
        }
        if (!result.Ok) return result;
        S.LastActionAt = now;
        if (Solo && S.Glek.Mood == "doze") Glek("idle", Pick(BlackjackLines.Open));
        _dirty = true;
        return result;
    }

    string? BetPhaseRefusal(DateTimeOffset now)
    {
        if (S.Phase is Play or GlekTurn) return Solo ? "Роздача йде — спершу дограй руку" : "Роздача йде — ставки на наступну";
        if (S.Phase == Result) return "Глек рахує — ставки за мить";
        if (!Solo && S.Phase == Bets && S.Until is { } u && now >= u) return "Карти вже роздаються — чекай наступної роздачі";
        return null;
    }

    /// <summary>Чи підходить сума боксу: null — так, інакше текст відмови.</summary>
    string? Fits(long total, int wallet)
    {
        var o = Opts;
        if (total < o.MinBet) return $"Найменша ставка — {o.MinBet} 🏺";
        if (o.MaxBet > 0 && total > o.MaxBet) return $"Найбільша ставка — {o.MaxBet} 🏺";
        if (total > int.MaxValue / MaxReturnX) return "Завелика ставка — каса стільки за раз не виплатить";
        if (total > wallet) return $"Бракує черепків: у гаманці {wallet}";
        return null;
    }

    ActResult Bet(int seat, string nick, string key, JsonElement payload, int wallet)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("amount", out var a)
            || a.ValueKind != JsonValueKind.Number || !a.TryGetInt32(out var amount) || amount < 1)
            return ActResult.Fail("Ставка — ціле число черепків, від 1");
        var bet = BetOf(key);
        if (Fits((long)(bet?.Amount ?? 0) + amount, wallet) is { } no) return ActResult.Fail(no);
        Wake();
        bet = BetFor(nick, key, seat);
        bet.Amount += amount;
        bet.Ready = false;
        return ActResult.Done;
    }

    ActResult Clear(string key)
    {
        if (BetOf(key) is not { Amount: > 0 } bet) return ActResult.Fail("Нема чого знімати");
        S.Bets.Remove(bet);
        return ActResult.Done;
    }

    ActResult Rebet(int seat, string nick, string key, int wallet)
    {
        if (!S.LastBet.TryGetValue(key, out var last) || last <= 0) return ActResult.Fail("Минулої роздачі ставки не було");
        if (Fits(last, wallet) is { } no) return ActResult.Fail(no);
        Wake();
        var bet = BetFor(nick, key, seat);
        bet.Amount = last;
        bet.Ready = false;
        return ActResult.Done;
    }

    /// <summary>
    /// Стіл — «Роздавай!» (я готовий; коли готові всі, хто сидить, Глек роздає одразу). Соло — роздати зараз; <c>amount</c>
    /// у payload — поставити саме стільки, без ставки на столі — ставка минулої роздачі.
    /// </summary>
    ActResult Deal(int seat, string nick, string key, JsonElement payload, int wallet)
    {
        int? amount = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("amount", out var a)
            ? (a.ValueKind == JsonValueKind.Number && a.TryGetInt32(out var x) && x >= 1 ? x : -1)
            : null;
        if (amount == -1) return ActResult.Fail("Ставка — ціле число черепків, від 1");
        var bet = BetOf(key);
        var want = amount ?? (bet is { Amount: > 0 } ? bet.Amount : S.LastBet.GetValueOrDefault(key));
        if (want <= 0) return ActResult.Fail("Спершу постав ставку");
        if (Fits(want, wallet) is { } no) return ActResult.Fail(no);
        Wake();
        bet = BetFor(nick, key, seat);
        var before = bet.Amount;
        bet.Amount = want;
        if (!Solo)
        {
            bet.Ready = true;
            if (AllReady()) Close(Now);
            return ActResult.Done;
        }
        if (Close(Now) is { } why)
        {
            bet.Amount = before;
            if (before <= 0) S.Bets.Remove(bet);
            return ActResult.Fail(why);
        }
        return ActResult.Done;
    }

    /// <summary>Хід поточної руки: беру, стою, подвоюю, б'ю. Подвоєння й спліт — окреме списання з гаманця.</summary>
    ActResult Move(string nick, string key, string action, DateTimeOffset now)
    {
        if (S.Phase != Play || S.Deal is not { } deal || S.Pending is not { } pending)
            return ActResult.Fail(S.Phase == GlekTurn ? "Глек уже грає свою руку" : "Зараз не час ходити — спершу ставка");
        if (deal.Current is not { } hand || Key(deal.Boxes[deal.Box].Nick) != key)
            return ActResult.Fail(deal.Box >= 0 ? $"Чекай свого ходу — зараз ходить {deal.Boxes[deal.Box].Nick}" : "Зараз не твій хід");
        var can = deal.Allowed();
        if (action is BjDeal.Double or BjDeal.Split)
        {
            if (action == BjDeal.Double && !can.Double) return ActResult.Fail("Подвоїти можна лише на перших двох картах, коли в тебе 9, 10 чи 11");
            if (action == BjDeal.Split && !can.Split)
                return ActResult.Fail(deal.Boxes[deal.Box].Hands.Count >= BlackjackCore.MaxHands ? "Більше трьох рук не буває" : "Розбити можна лише пару однакових карт");
            var wallet = _book!.Balance(nick);
            var step = action == BjDeal.Double ? "d" : "s" + deal.Boxes[deal.Box].Hands.Count;
            var reason = (action == BjDeal.Double ? "blackjack-double:" : "blackjack-split:") + Info.Id;
            if (hand.Bet > wallet || !_book.Take(nick, hand.Bet, reason, BlackjackBook.BetRef(pending.Table, pending.Round, step, nick)))
            {
                S.Wallets[key] = wallet;
                return ActResult.Fail($"Бракує черепків: треба ще {hand.Bet}, у гаманці {wallet}");
            }
            S.Wallets[key] = Math.Max(0, wallet - hand.Bet);
        }
        if (deal.Act(action, Draw) is { } err) return ActResult.Fail(err);
        AfterMove(now);
        return ActResult.Done;
    }

    /// <summary>Після ходу: гравці відходили — Глек; ні — новий відлік на рішення (стіл).</summary>
    void AfterMove(DateTimeOffset now)
    {
        if (S.Deal!.PlayersDone) Resolve(now);
        else S.Until = Solo ? null : now.AddMilliseconds(TurnMs);
        _dirty = true;
    }

    void Wake()
    {
        if (Solo || S.Phase != Idle) return;
        S.EmptyRounds = 0;
        OpenBets(afterSigh: false);
    }

    /// <summary>Усі, хто сидить, поставили й сказали «Роздавай!».</summary>
    bool AllReady()
    {
        var any = false;
        for (var seat = 0; seat < Info.MaxPlayers; seat++)
        {
            if (Ctx.NickOf(seat) is not { } nick) continue;
            if (BetOf(Key(nick)) is not { Amount: > 0, Ready: true }) return false;
            any = true;
        }
        return any;
    }

    // ---------- тик ----------

    public override TickResult Tick()
    {
        var now = Now;
        switch (S.Phase)
        {
            case Bets when !Solo && S.Until is { } until:
                if (now >= until || AllReady())
                {
                    if (S.Bets.Any(b => b.Amount > 0)) Close(now);
                    else EmptyRound();
                }
                else if (!S.Hurried && until - now <= TimeSpan.FromMilliseconds(HurryMs) && S.Bets.Any(b => b.Amount > 0))
                {
                    S.Hurried = true;
                    Glek("hurry", Pick(BlackjackLines.Hurry));
                }
                break;
            case Bets when Solo:
                if (S.Glek.Mood != "doze" && now - S.LastActionAt >= TimeSpan.FromMilliseconds(DozeMs)) Glek("doze", Pick(BlackjackLines.Doze));
                break;
            case Play when !Solo && S.Deal is { Current: not null } deal:
                var owner = deal.Boxes[deal.Box].Nick;
                var gone = SeatOf(Key(owner)) is null;
                if (gone || (S.Until is { } u && now >= u))
                {
                    deal.Act(BjDeal.Stand, Draw);
                    AfterMove(now);
                    // після AfterMove: Глек, що вже відкриває карти, теж скаже, чому рука стала
                    if (!gone) Glek(S.Phase == Play ? "idle" : S.Glek.Mood, string.Format(BlackjackLines.Timeout, owner));
                }
                break;
            case GlekTurn:
                var shown = ShownAt(now);
                if (shown != S.Shown)
                {
                    S.Shown = shown;
                    _dirty = true;
                }
                if (S.Until is { } end && now >= end) Payout(now);
                break;
            case Result when S.Until is { } until && now >= until:
                S.Deal = null;
                S.Notes.Clear();
                if (AnySeated()) OpenBets(afterSigh: false);
                else Doze();
                break;
        }
        if (!_dirty) return TickResult.None;
        _dirty = false;
        return new TickResult(false, true);
    }

    /// <summary>Нове вікно ставок: новий seed (відбиток — одразу у виді), стіл — 15 с, соло — без таймера.</summary>
    void OpenBets(bool afterSigh, bool keepBets = false)
    {
        S.Phase = Bets;
        S.Until = Solo ? null : Now.AddMilliseconds(BetMs);
        S.Hurried = false;
        if (!keepBets) S.Bets.Clear();
        foreach (var b in S.Bets) b.Ready = false;
        S.Seed = BlackjackCore.NewSeed(Ctx.Seeded ? Ctx.Rng : null);
        S.Head = [];
        S.Drawn = 0;
        if (_book is not null)
            for (var seat = 0; seat < Info.MaxPlayers; seat++)
                if (Ctx.NickOf(seat) is { } nick) S.Wallets[Key(nick)] = _book.Balance(nick);
        if (!afterSigh) Glek("idle", Pick(BlackjackLines.Open));
        _dirty = true;
    }

    void Doze()
    {
        S.Phase = Idle;
        S.Until = null;
        S.Hurried = false;
        Glek("doze", Pick(BlackjackLines.Doze));
    }

    void EmptyRound()
    {
        S.EmptyRounds++;
        S.Bets.Clear();
        if (!AnySeated() || S.EmptyRounds >= EmptyToDoze)
        {
            Doze();
            return;
        }
        var line = Pick(BlackjackLines.Sigh);
        Glek("sigh", line);
        if (S.EmptyRounds == 1) Ctx.Say(line);
        OpenBets(afterSigh: true);
    }

    // ---------- роздача ----------

    /// <summary>Карта з колоди роздачі: спершу <see cref="BlackjackState.Head"/> (тести), далі колода з seed.</summary>
    string Draw()
    {
        if (S.Drawn < S.Head.Count) return S.Head[S.Drawn++];
        if (_shoe is null || _shoeSeed != S.Seed)
        {
            _shoe = BlackjackCore.Shoe(S.Seed);
            _shoeSeed = S.Seed;
        }
        var i = S.Drawn - S.Head.Count;
        if (i >= _shoe.Length) throw new GameError("Колода скінчилась — так не буває");
        S.Drawn++;
        return _shoe[i];
    }

    /// <summary>Усі карти роздачі в порядку, як ішли з колоди (для перевірки seed).</summary>
    List<string> Drawn()
    {
        var list = new List<string>(S.Drawn);
        for (var i = 0; i < S.Drawn && i < S.Head.Count; i++) list.Add(S.Head[i]);
        if (S.Drawn > S.Head.Count)
        {
            _shoe ??= BlackjackCore.Shoe(S.Seed);
            list.AddRange(_shoe.Take(S.Drawn - S.Head.Count));
        }
        return list;
    }

    /// <summary>
    /// «Роздаю!» (§3.3): запис у касу, тоді списання кожному, тоді карти. Під замком кімнати. null — роздано; інакше текст
    /// (соло віддає його гравцеві, стіл — Глекові).
    /// </summary>
    string? Close(DateTimeOffset now)
    {
        var bets = S.Bets.Where(b => b.Amount > 0).OrderBy(b => b.Color).ToList();
        S.Notes.Clear();
        var round = ++S.RoundNo;
        var table = Table;
        var record = new BjRound(table, round, Info.Id, now, false, [.. bets.Select(b => new BjPay(b.Nick, b.Amount, null))]);
        if (_book is null || !_book.Open(record))
        {
            if (Solo) return ClosedText;
            Glek("sigh", BlackjackLines.BookStuck);
            OpenBets(afterSigh: true, keepBets: true);
            return BlackjackLines.BookStuck;
        }

        var paid = new List<BjBet>();
        foreach (var b in bets)
        {
            var key = Key(b.Nick);
            var wallet0 = _book.Balance(b.Nick);
            if (_book.Take(b.Nick, b.Amount, $"blackjack-bet:{Info.Id}", BlackjackBook.BetRef(table, round, "b", b.Nick)))
            {
                paid.Add(b);
                S.Wallets[key] = Math.Max(0, wallet0 - b.Amount);
                continue;
            }
            S.Wallets[key] = wallet0;
            if (Solo)
            {
                _book.Drop(table, round);
                return $"Бракує черепків: у гаманці {wallet0}, а ставка {b.Amount}";
            }
            S.Notes[key] = $"Черепків не стало — твою ставку ({b.Amount}) знято";
        }
        if (paid.Count == 0)
        {
            _book.Drop(table, round);
            Glek("sigh", BlackjackLines.NobodyPaid);
            OpenBets(afterSigh: true);
            return BlackjackLines.NobodyPaid;
        }

        if (Rig is { } rig)
        {
            S.Head = [.. rig];
            Rig = null;
        }
        S.Drawn = 0;
        S.Pending = record with { Pays = [.. paid.Select(b => new BjPay(b.Nick, b.Amount, null))] };
        foreach (var b in paid) S.LastBet[Key(b.Nick)] = b.Amount;
        S.Bets.Clear();
        S.EmptyRounds = 0;
        S.Deal = BjDeal.Start(paid.Select(b => (b.Nick, b.Color, b.Amount)), Draw);
        var up = S.Deal.Dealer[0];
        if (S.Deal.DealerBj) { }
        else if (up[0] == 'A') Glek("deal", Pick(BlackjackLines.NoInsurance));
        else if (BlackjackCore.Peeks(up)) Glek("deal", BlackjackLines.Peeked);
        else Glek("deal", Pick(BlackjackLines.Deal));
        if (S.Deal.PlayersDone) Resolve(now);
        else
        {
            S.Phase = Play;
            S.Until = Solo ? null : now.AddMilliseconds(TurnMs);
        }
        _dirty = true;
        return null;
    }

    /// <summary>
    /// Гравці відходили: Глек відкриває закриту й добирає, руки розраховано, підсумки — у запис каси (сирота заплатить за
    /// ними). Виплата — коли Глек показав свої карти (<see cref="Payout"/>): тост гаманця не випереджає карт.
    /// </summary>
    void Resolve(DateTimeOffset now)
    {
        var deal = S.Deal!;
        if (!deal.DealerBj) deal.DealerPlays(Draw);
        deal.Settle();
        var p = S.Pending!;
        var fin = p with { Final = true, Pays = [.. deal.Boxes.Select(b => new BjPay(b.Nick, b.Staked, b.Returned))] };
        _book?.Finalize(fin);
        S.Pending = fin;
        S.Phase = GlekTurn;
        S.GlekAt = now;
        S.Shown = 2;
        var extra = Math.Max(0, deal.Dealer.Count - 2);
        // закрита — одразу, кожна добрана — через StepMs, остання ще EndMs на очах
        S.Until = now.AddMilliseconds(FlipMs + Math.Max(0, extra - 1) * StepMs + EndMs);
        if (!deal.DealerBj) Glek("flip", null);
        _dirty = true;
    }

    /// <summary>Скільки карт Глека видно в мить <paramref name="now"/> фази glek: закрита відкривається одразу, далі — по карті.</summary>
    int ShownAt(DateTimeOffset now)
    {
        var count = S.Deal?.Dealer.Count ?? 0;
        if (S.GlekAt is not { } at) return count;
        var t = (now - at).TotalMilliseconds;
        if (t < FlipMs) return Math.Min(count, 2);
        return Math.Min(count, 3 + (int)((t - FlipMs) / StepMs));
    }

    /// <summary>
    /// Глек показав карти: виплати (поза замком), підсумки, серії, рекорди, ачівки, таблиця, Журнал, Глек.
    /// <paramref name="quiet"/> — роздачу вже розраховано раніше (соло-стан у сховищі лишився в «glek»): лише стан.
    /// </summary>
    void Payout(DateTimeOffset now, bool quiet = false)
    {
        var deal = S.Deal!;
        var fin = S.Pending;
        if (fin is not null && !quiet) _book?.Settle(fin);
        S.Shown = deal.Dealer.Count;
        var dealerTotal = BlackjackCore.Total(deal.Dealer);
        var results = new List<(BjResult R, BjBox Box)>();
        foreach (var box in deal.Boxes)
        {
            var key = Key(box.Nick);
            var r = new BjResult
            {
                Nick = box.Nick, Color = box.Color, Staked = box.Staked, Paid = box.Returned, Net = box.Returned - box.Staked,
                Hands = [.. box.Hands.Select(h => h.Outcome ?? "lose")],
                Five = box.Hands.Where(h => h.Cards.Count >= 5 && BlackjackCore.Total(h.Cards) == 21).Select(h => h.Cards.Count).DefaultIfEmpty(0).Max(),
            };
            results.Add((r, box));
            S.Wallets[key] = (int)Math.Min(int.MaxValue, (long)S.Wallets.GetValueOrDefault(key) + box.Returned);
            if (r.Net > 0) S.Streak[key] = S.Streak.GetValueOrDefault(key) + 1;
            else if (r.Net < 0) S.Streak[key] = 0;
        }
        var ordered = results.OrderByDescending(x => x.R.Net).ToList();
        var staked = results.Sum(x => (long)x.R.Staked);
        var paidOut = results.Sum(x => (long)x.R.Paid);

        // Глек: блекджек > гопак (великий виграш чи 21 з п'яти карт) > перебрав > платить > забирає.
        var dancer = ordered.FirstOrDefault(x => x.R.Net >= 1000 || x.R.Five >= 5);
        string mood, say;
        string? big = null;
        if (deal.DealerBj)
        {
            mood = "bj";
            say = deal.Boxes.Any(b => b.Hands[0].IsNatural) ? BlackjackLines.BjOnBj : Pick(BlackjackLines.DealerBj);
        }
        else if (dancer.R is not null)
        {
            mood = "dance";
            big = dancer.R.Nick;
            say = dancer.R.Five >= 5 && dancer.R.Net < 1000
                ? string.Format(BlackjackLines.Five, big, dancer.R.Five)
                : string.Format(Pick(BlackjackLines.Dance), big, dancer.R.Net);
        }
        else if (dealerTotal > 21) { mood = "bust"; say = string.Format(Pick(BlackjackLines.Bust), dealerTotal); }
        else if (paidOut > staked) { mood = "clap"; say = string.Format(Pick(BlackjackLines.Clap), dealerTotal); }
        else if (ordered.Any(x => x.R.Hands.Contains("bj"))) { mood = "clap"; say = BlackjackLines.Natural; }
        else { mood = "rake"; say = string.Format(Pick(BlackjackLines.Rake), dealerTotal); }

        S.Last = new BjLast
        {
            Round = fin?.Round ?? S.RoundNo, Seed = S.Seed, Hash = BlackjackCore.Hash(S.Seed), Dealer = [.. deal.Dealer],
            DealerBj = deal.DealerBj, Drawn = Drawn(), Big = big, Results = [.. ordered.Select(x => x.R)],
        };
        if (!quiet)
        {
            Glek(mood, say);
            Announce(now, ordered, mood, say);
        }
        S.Pending = null;
        S.GlekAt = null;
        if (Solo) OpenBets(afterSigh: true);
        else
        {
            S.Phase = Result;
            S.Until = now.AddMilliseconds(ResultMs);
        }
        _dirty = true;
    }

    /// <summary>Розрахунок для людей: рекорди, ачівки, таблиця, балачка столу, Журнал. Раз на роздачу.</summary>
    void Announce(DateTimeOffset now, List<(BjResult R, BjBox Box)> ordered, string mood, string say)
    {
        var rec = S.Records;
        foreach (var (r, box) in ordered)
        {
            var key = Key(r.Nick);
            if (r.Net > 0 && (rec.Win is null || r.Net > rec.Win.N)) rec.Win = new BjMark { Nick = r.Nick, N = r.Net };
            var streak = S.Streak.GetValueOrDefault(key);
            if (streak > 0 && (rec.Streak is null || streak > rec.Streak.N)) rec.Streak = new BjMark { Nick = r.Nick, N = streak };
            if (r.Five >= 5)
            {
                rec.Fives++;
                rec.Five = new BjMark { Nick = r.Nick, N = r.Five };
            }

            // Ачівки й таблиця — лише тим, хто в мить розрахунку сидить.
            if (SeatOf(key) is not { } seat) continue;
            if (r.Hands.Contains("bj")) Ctx.Award(seat, 0, "ach:blackjack-natural");
            if (r.Five >= 5) Ctx.Award(seat, 0, "ach:blackjack-five");
            if (streak >= 5) Ctx.Award(seat, 0, "ach:blackjack-streak5");
            if (box.Hands.Count >= 3 && r.Hands.All(h => h == "win")) Ctx.Award(seat, 0, "ach:blackjack-split3");
            if (r.Net > 0) Ctx.Score(seat, r.Net);
        }

        // Балачка столу — щонайбільше рядок на роздачу.
        if (!Solo)
        {
            var lost = ordered.Sum(x => (long)x.R.Staked - x.R.Paid);
            if (mood == "dance" || (mood == "bj" && lost >= 50)) Ctx.Say(say);
        }

        // Журнал: великий виграш чи 21 з п'яти карт, не частіше раз на 10 хв на стіл.
        var star = ordered.FirstOrDefault(x => x.R.Net >= 2000 || (x.R.Five >= 5 && x.R.Staked >= 50));
        if (star.R is not null && (S.JournalAt is not { } at || now - at >= JournalGap))
        {
            S.JournalAt = now;
            var where = Solo ? "Двадцять одно сам на сам" : "Двадцять одно в Глека";
            Ctx.Log(star.R.Net >= 2000
                ? $"🃏 {where}: {star.R.Nick} виграє {star.R.Net} черепків"
                : $"🃏 {where}: {star.R.Nick} збирає 21 з {star.R.Five} карт");
        }
    }

    // ---------- вид ----------

    public override object View(int? seat)
    {
        var now = Now;
        var nick = seat is { } s ? Ctx.NickOf(s) : null;
        var me = nick is null ? null : Key(nick);
        var o = Opts;
        int? phaseMs = S.Phase switch
        {
            Bets => Solo ? null : BetMs,
            Play => Solo ? null : TurnMs,
            GlekTurn => S.Until is { } gu && S.GlekAt is { } ga ? (int)(gu - ga).TotalMilliseconds : null,
            Result => ResultMs,
            _ => null,
        };
        var until = S.Phase is Idle || (Solo && S.Phase is Bets or Play) ? null : S.Until;
        var deal = S.Deal;
        int? turn = null;
        if (S.Phase == Play && deal?.Current is not null)
            turn = Solo ? 0 : SeatOf(Key(deal.Boxes[deal.Box].Nick));
        return new
        {
            mode = Solo ? "solo" : "table",
            phase = S.Phase,
            until,
            leftMs = until is { } u ? Left(u, now) : (int?)null,
            phaseMs,
            turn,
            round = S.RoundNo,
            hash = string.IsNullOrEmpty(S.Seed) ? null : BlackjackCore.Hash(S.Seed),
            limits = new { min = o.MinBet, max = o.MaxBet },
            on = o.Enabled,
            closed = _book is null,
            dealer = DealerView(deal),
            boxes = Boxes(me),
            last = LastView(),
            records = new
            {
                win = Mark(S.Records.Win),
                streak = Mark(S.Records.Streak),
                five = Mark(S.Records.Five),
                fives = S.Records.Fives,
            },
            glek = new { mood = S.Glek.Mood, say = S.Glek.Say, seq = S.Glek.Seq },
            me = me is null ? null : Me(me, now),
        };
    }

    static object? Mark(BjMark? m) => m is null ? null : new { nick = m.Nick, n = m.N };

    static int Left(DateTimeOffset until, DateTimeOffset now) => (int)Math.Clamp((until - now).TotalMilliseconds, 0, int.MaxValue);

    /// <summary>Карти Глека: закрита — null, поки не відкрив; у фазі glek — стільки, скільки вже показав.</summary>
    object? DealerView(BjDeal? deal)
    {
        if (deal is null) return null;
        var count = S.Phase == GlekTurn ? Math.Min(S.Shown, deal.Dealer.Count) : deal.Dealer.Count;
        var cards = new List<string?>();
        for (var i = 0; i < count; i++) cards.Add(i == 1 && !deal.HoleOpen ? null : deal.Dealer[i]);
        var visible = cards.Where(c => c is not null).Cast<string>().ToList();
        var (total, soft) = BlackjackCore.Score(visible);
        return new { cards, total, soft, bj = deal.HoleOpen && BlackjackCore.Natural(deal.Dealer) && count >= 2 };
    }

    object[] Boxes(string? me)
    {
        var list = new List<object>();
        var deal = S.Deal;
        if (deal is not null)
        {
            for (var b = 0; b < deal.Boxes.Count; b++)
            {
                var box = deal.Boxes[b];
                var key = Key(box.Nick);
                var seat = SeatOf(key);
                var settled = deal.Settled && S.Phase != GlekTurn;
                list.Add(new
                {
                    nick = box.Nick, seat, color = box.Color, here = seat is not null, mine = key == me,
                    bet = box.Bet, ready = true, staked = box.Staked,
                    paid = settled ? box.Returned : (int?)null,
                    hands = box.Hands.Select((h, i) =>
                    {
                        var (total, soft) = BlackjackCore.Score(h.Cards);
                        return new
                        {
                            cards = h.Cards.ToArray(), bet = h.Bet, doubled = h.Doubled, split = h.Split, done = h.Done,
                            total, soft, bj = h.IsNatural, bust = total > 21,
                            active = S.Phase == Play && deal.Box == b && deal.Hand == i,
                            outcome = settled ? h.Outcome : null,
                            ret = settled ? h.Return : (int?)null,
                        };
                    }).ToArray(),
                });
            }
            return [.. list];
        }
        foreach (var bet in S.Bets.Where(x => x.Amount > 0).OrderBy(x => x.Color))
        {
            var key = Key(bet.Nick);
            var seat = SeatOf(key);
            list.Add(new
            {
                nick = bet.Nick, seat, color = bet.Color, here = seat is not null, mine = key == me,
                bet = bet.Amount, ready = bet.Ready, staked = 0, paid = (int?)null, hands = Array.Empty<object>(),
            });
        }
        return [.. list];
    }

    object? LastView() => S.Last is not { } l ? null : new
    {
        round = l.Round, seed = l.Seed, hash = l.Hash, dealer = l.Dealer.ToArray(), dealerBj = l.DealerBj,
        total = BlackjackCore.Total(l.Dealer), drawn = l.Drawn.ToArray(), big = l.Big,
        results = l.Results.Select(r => new
        {
            nick = r.Nick, color = r.Color, staked = r.Staked, paid = r.Paid, net = r.Net, hands = r.Hands.ToArray(), five = r.Five,
        }).ToArray(),
    };

    object Me(string key, DateTimeOffset now)
    {
        var wallet = S.Wallets.GetValueOrDefault(key);
        var bet = BetOf(key);
        var open = BetsOpen(now);
        var o = Opts;
        var rebet = S.LastBet.GetValueOrDefault(key);
        var deal = S.Deal;
        var myTurn = S.Phase == Play && deal?.Current is not null && Key(deal.Boxes[deal.Box].Nick) == key;
        object? actions = null;
        object? hint = null;
        if (myTurn)
        {
            var can = deal!.Allowed();
            var hand = deal.Current!;
            actions = new { hit = can.Hit, stand = can.Stand, @double = can.Double, split = can.Split, cost = hand.Bet };
            if (BlackjackStrategy.Move(deal) is { } move) hint = new { move, text = HintText(move) };
        }
        var mine = deal?.Boxes.FirstOrDefault(b => Key(b.Nick) == key);
        return new
        {
            wallet,
            bet = bet?.Amount ?? 0,
            ready = bet?.Ready ?? false,
            free = (int)Math.Max(0, (long)wallet - (open ? bet?.Amount ?? 0 : 0)),
            staked = mine?.Staked ?? 0,
            canBet = open && o.Enabled,
            canDeal = open && o.Enabled && ((bet?.Amount ?? 0) > 0 || (Solo && rebet > 0)),
            canRebet = open && o.Enabled && rebet > 0 && rebet <= wallet,
            rebet,
            actions,
            hint,
            streak = S.Streak.GetValueOrDefault(key),
            note = S.Notes.GetValueOrDefault(key),
        };
    }

    static string HintText(string move) => move switch
    {
        BjDeal.Hit => "Бери карту",
        BjDeal.Stand => "Стій",
        BjDeal.Double => "Подвоюй",
        BjDeal.Split => "Розбивай пару",
        _ => "",
    };

    bool BetsOpen(DateTimeOffset now) => Solo
        ? S.Phase == Bets
        : S.Phase == Idle || (S.Phase == Bets && S.Until is { } u && now < u);

    // ---------- Save/Load ----------

    /// <summary>Увесь стан (§3.5): фаза, дедлайни, ставки, роздача (колода — з seed і лічильника карт), запис каси, рекорди.</summary>
    public override string? Save() => JsonSerializer.Serialize(S, Json);

    /// <summary>
    /// Новий epoch на кожне відновлення (ключі нових роздач не повторять старих). Роздача, що йшла: каса ще тримає запис —
    /// граємо далі; не тримає — її вже розрахували без нас: дограна (glek) — доводимо мовчки, обірвана посеред ходів
    /// (play) — каса повернула ставки, кажемо людям і відкриваємо ставки.
    /// </summary>
    public override void Load(string json)
    {
        var s = JsonSerializer.Deserialize<BlackjackState>(json, Json) ?? throw new InvalidOperationException("порожній стан двадцяти одного");
        if (s.Phase is not (Idle or Bets or Play or GlekTurn or Result) || (s.Phase is Play or GlekTurn && (s.Deal is null || s.Pending is null)))
            throw new InvalidOperationException($"невідома фаза двадцяти одного: {s.Phase}");
        if (Solo && s.Phase is Idle or Result) s.Phase = Bets;
        s.Epoch = NewEpoch();
        S = s;
        _shoe = null;
        _settledOnLoad = false;
        if (S.Phase is Play or GlekTurn && _book is not null && S.Pending is { } p && _book.Holds(p.Table, p.Round) == false)
        {
            if (S.Phase == GlekTurn)
            {
                Payout(Now, quiet: true);
                Glek("idle", null);
            }
            else Voided(p);
            _settledOnLoad = true;
        }
        if (S.Phase == Bets && string.IsNullOrEmpty(S.Seed)) S.Seed = BlackjackCore.NewSeed(Ctx.Seeded ? Ctx.Rng : null);
        for (var seat = 0; seat < Info.MaxPlayers; seat++) Refresh(seat);
        _dirty = true;
    }

    /// <summary>Роздачу обірвано посеред ходів, і каса вже розрахувала її сама (повернула списане): кажемо людям, що сталось.</summary>
    void Voided(BjRound p)
    {
        foreach (var pay in p.Pays)
        {
            var key = Key(pay.Nick);
            S.Notes[key] = _book!.Outcome(p.Table, p.Round, pay.Nick) switch
            {
                { Back: > 0 } o => $"Роздачу перервав перезапуск — ставку ({o.Back}) повернуто",
                { Paid: > 0 } o => $"Роздачу дограно без тебе — повернуто {o.Paid}",
                _ => "Роздачу перервав перезапуск — гроші не списувались",
            };
        }
        S.Deal = null;
        S.Pending = null;
        S.GlekAt = null;
        OpenBets(afterSigh: true);
        Glek("idle", "Перезапуск перервав роздачу — гроші повернуто. Роздаю наново");
    }

    /// <summary>Чи стіл чекає саме на цю роздачу — тоді каса її не чіпає (<see cref="BlackjackBook.HeldBy"/>).</summary>
    public bool Holds(string table, int round) =>
        S.Phase is Play or GlekTurn && S.Pending is { } p && p.Round == round && string.Equals(p.Table, table, StringComparison.Ordinal);

    public override void Resumed(TimeSpan pause)
    {
        if (!_settledOnLoad && S.Until is not null) S.Until += pause;
        if (!_settledOnLoad && S.GlekAt is not null) S.GlekAt += pause;
        _settledOnLoad = false;
        S.LastActionAt += pause;
        _dirty = true;
    }

    // ---------- дрібниці ----------

    string NewEpoch() => Ctx.Seeded
        ? ((uint)Ctx.Rng.Next(int.MinValue, int.MaxValue)).ToString("x8")
        : RandomNumberGenerator.GetHexString(8, lowercase: true);

    void Glek(string mood, string? say)
    {
        S.Glek = new BjGlek { Mood = mood, Say = say, Seq = S.Glek.Seq + 1 };
        _dirty = true;
    }

    string Pick(string[] bank) => BlackjackLines.Pick(bank, Ctx.Rng);

    static string Key(string nick) => Rooms.NickKey(nick);

    BjBet? BetOf(string key) => S.Bets.FirstOrDefault(b => Key(b.Nick) == key);

    BjBet BetFor(string nick, string key, int seat)
    {
        if (BetOf(key) is { } bet)
        {
            bet.Nick = nick;
            bet.Color = seat;
            return bet;
        }
        bet = new BjBet { Nick = nick, Color = seat };
        S.Bets.Add(bet);
        return bet;
    }

    int? SeatOf(string key)
    {
        for (var seat = 0; seat < Info.MaxPlayers; seat++)
            if (Ctx.NickOf(seat) is { } nick && Key(nick) == key) return seat;
        return null;
    }

    bool AnySeated()
    {
        for (var seat = 0; seat < Info.MaxPlayers; seat++)
            if (Ctx.Seated(seat)) return true;
        return false;
    }

    /// <summary>Гаманець у кеші перечитати (людина сіла за стіл).</summary>
    protected void Refresh(int seat)
    {
        if (_book is not null && Ctx.NickOf(seat) is { } nick) S.Wallets[Key(nick)] = _book.Balance(nick);
        _dirty = true;
    }

    /// <summary>Людина встала: її ставку на наступну роздачу знімаємо (гаманець ще не чіпали); руки, що вже на сукні, дограють.</summary>
    protected void DropBet(int seat)
    {
        if (Ctx.NickOf(seat) is { } nick && BetOf(Key(nick)) is { } bet) S.Bets.Remove(bet);
        _dirty = true;
    }
}

/// <summary>
/// «Двадцять одно в Глека» за столом: до п'яти боксів проти Глека. Ставки 15 с (чи доки всі не скажуть «Роздавай!») →
/// роздача → кожен ходить по черзі, 15 с на рішення (не встиг — стоїть) → Глек добирає → виплати → ставки…
/// </summary>
public sealed class Blackjack : BlackjackGame
{
    public override GameInfo Info { get; } = new(
        "blackjack", "Двадцять одно в Глека", "двадцять одно в Глека", GameGroup.Party, 1, 5, TickMs: TickMs,
        Start: StartMode.Immediate, Hidden: true, Score: ScoreOrder.HigherIsBetter, Coop: true,
        Hint: "Блекджек проти Дядька Глека: до п'яти за столом, кожен — проти круп'є. Набери ближче до 21, ніж Глек, і не перебери");

    protected override bool Solo => false;

    /// <summary>Save тримає ввесь стан (дедлайни, роздачу, запис каси), тож деплой стіл не чекає.</summary>
    public override bool Resumable => true;

    public override bool LateJoin(string nick) => true;

    public override string? LateJoinGreeting(string nick) => "Сідай ближче! Ставка — до «Роздаю!», черепки — з гаманця";

    public override void OnJoin(int seat) => Refresh(seat);

    /// <summary>Не техпоразка: руки на сукні дограють (стоять), ставка на наступну роздачу знімається.</summary>
    public override void OnLeave(int seat) => DropBet(seat);

    /// <summary>Стіл без людей ще хвилину догравав те, що на сукні (руки стоять самі, Глек добирає).</summary>
    public override TimeSpan HoldEmpty => TimeSpan.FromSeconds(60);
}

/// <summary>«Двадцять одно» сам на сам: свій стіл, без таймера. Стан — у game_state після кожної дії.</summary>
public sealed class BlackjackSolo : BlackjackGame
{
    public override GameInfo Info { get; } = new(
        "blackjack-solo", "Двадцять одно: сам на сам", "двадцять одно сам на сам", GameGroup.Solo, 1, 1, TickMs: TickMs,
        Start: StartMode.Immediate, Private: true, Persistent: true, Score: ScoreOrder.HigherIsBetter,
        Hint: "Ти й Дядько Глек за одним сукном: став, тисни «Роздати» — і думай скільки хочеш", Client: "blackjack");

    protected override bool Solo => true;
}
