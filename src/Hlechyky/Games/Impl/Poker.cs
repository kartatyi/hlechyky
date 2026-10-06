using System.Security.Cryptography;
using System.Text.Json;
using Hlechyky.Games.Economy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hlechyky.Games.Impl;

/// <summary>Позиція за покерним столом (0..7 за годинниковою). Кімнатне місце може змінитись (повернувся на інше крісло).</summary>
public sealed class PokerSeat
{
    public string? Nick { get; set; }
    public string? Bot { get; set; }
    /// <summary>Місце в кімнаті (Ctx.NickOf), яким ця позиція показується в шапці столу.</summary>
    public int RoomSeat { get; set; }
    /// <summary>empty — нікого; play — грає; wait — кеш: підсів, грає з наступної роздачі; out — кеш: без фішок
    /// (програв чи встав після ☕), може докупитись; bust — вибув із турніру; watch — турнір: не вніс внесок.</summary>
    public string State { get; set; } = "empty";
    public bool Away { get; set; }
    public DateTimeOffset? AwaySince { get; set; }
    /// <summary>Прострочені ходи поспіль.</summary>
    public int Strikes { get; set; }
    /// <summary>Турнір: людина пішла з-за столу (OnLeave), фішки чекають, сліпі платяться.</summary>
    public bool Gone { get; set; }
    public int Place { get; set; }
    /// <summary>Кеш: скільки черепків вніс за цей стіл (викуп + докупи).</summary>
    public int Bought { get; set; }
    /// <summary>Турнір на черепки: внесок.</summary>
    public int Fee { get; set; }

    public string? Name => Nick ?? Bot;
}

public sealed record PokerWinView(int Pos, string Name, int Amount, string? Hand, string[]? Cards);
public sealed record PokerLast(long Hand, List<PokerWinView> Wins, string Text);

/// <summary>Увесь стан партії — одним JSON-ом (Save/Load, продовження після перезапуску).</summary>
public sealed class PokerState
{
    public PokerCore Core { get; set; } = new(Poker.Seats);
    public List<PokerSeat> Seats { get; set; } = [];
    /// <summary>Хто сидів на позиції на початку роздачі — власник внесеного в неї (Total).</summary>
    public string?[] HandNicks { get; set; } = new string?[Poker.Seats];
    public int[] StartStacks { get; set; } = new int[Poker.Seats];
    public int Level { get; set; }
    public DateTimeOffset? LevelAt { get; set; }
    public DateTimeOffset? NextHandAt { get; set; }
    public DateTimeOffset? TurnUntil { get; set; }
    public DateTimeOffset? BotAt { get; set; }
    public DateTimeOffset? StepAt { get; set; }
    public DateTimeOffset? EmptySince { get; set; }
    /// <summary>Скільки гра списала (викупи, внески) і виплатила через банк столу.</summary>
    public long In { get; set; }
    public long Out { get; set; }
    public int Entrants { get; set; }
    public int Pool { get; set; }
    public int Humans { get; set; }
    public bool Over { get; set; }
    public int ShowPos { get; set; } = -1;
    public PokerLast? Last { get; set; }
    /// <summary>Шанси на початку олл-ін-прогону (для бед-біту), по позиціях; null — не було.</summary>
    public double[]? RunEq { get; set; }
    public long BigPotHand { get; set; }
}

/// <summary>
/// Покер — техаський холдем без ліміту на 2–8 (specs/poker.md). Три формати: турнір на інтерес (з ботами), турнір на
/// черепки (внески → приз) і кеш-стіл на черепки (викуп, підсісти/встати будь-коли). Гроші — через банк столу каркаса.
/// Тик 100 мс: таймери ходу, пауза між роздачами, дошка при олл-іні, рівні сліпих; види шлються з тика (після Act у
/// реалтаймі каркас їх не шле), тож хід людини доходить до всіх за ≤100 мс.
/// </summary>
public sealed class Poker : Game
{
    public const int Seats = 8;
    public const int TickMs = 100;
    public const int StartChips = 1000;
    public static readonly TimeSpan TurnTime = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan HandPause = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan RunoutPause = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan CashAwayLimit = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan TourGiveUp = TimeSpan.FromMinutes(10);

    public static readonly (int Sb, int Bb)[] Levels =
    [
        (10, 20), (15, 30), (25, 50), (50, 100), (75, 150), (100, 200), (150, 300), (200, 400), (300, 600), (500, 1000),
        (750, 1500), (1000, 2000), (1500, 3000), (2000, 4000), (3000, 6000), (5000, 10000), (7500, 15000), (10000, 20000),
    ];

    public override GameInfo Info { get; } = new(
        "poker", "Покер", "покер", GameGroup.Board, 1, Seats, TickMs: TickMs, Start: StartMode.ByHost, Hidden: true,
        Options:
        [
            new GameOption("format", "Формат",
                [("fun", "Турнір на інтерес"), ("tour", "Турнір на черепки"), ("cash", "Кеш-стіл на черепки")], "fun"),
            new GameOption("stakes", "Сліпі кеш-столу", [("1", "1/2 · викуп 100"), ("2", "2/5 · викуп 250"), ("5", "5/10 · викуп 500")], "1"),
            new GameOption("buyin", "Внесок турніру на черепки", [("20", "20"), ("50", "50"), ("100", "100"), ("250", "250")], "50"),
            new GameOption("pace", "Темп турніру", [("6", "звичайний: сліпі ростуть що 6 хв"), ("3", "швидкий: що 3 хв")], "6"),
            BoardBots.Option(5),
            new GameOption("botlvl", "🤖 Глек грає", [("normal", "звичайно"), ("easy", "легко")], "normal"),
        ],
        Hint: "Техаський холдем без ліміту на 2–8: турнір на інтерес (можна з Глеком 🤖), турнір на черепки або кеш-стіл, "
            + "де черепки лише переходять між гравцями — Глек нічого не бере");

    string _format = "fun";
    int _sb = 1, _bb = 2, _buyin = 100, _fee = 50, _levelMin = 6, _botsWanted;
    bool _easy;
    PokerState _s = new();
    bool _dirty;
    (string Nick, int Amount)? _joining;
    ILogger? _log;

    PokerCore C => _s.Core;
    bool Cash => _format == "cash";
    bool Money => _format != "fun";
    DateTimeOffset Now => Ctx.Clock.UtcNow;
    TimeSpan LevelLen => TimeSpan.FromMinutes(_levelMin);
    /// <summary>Для тестів: стан партії.</summary>
    public PokerState State => _s;
    public string Format => _format;
    public int BuyInAmount => _buyin;

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _format = options.GetValueOrDefault("format") is "tour" or "cash" ? options["format"] : "fun";
        (_sb, _bb) = options.GetValueOrDefault("stakes") switch { "2" => (2, 5), "5" => (5, 10), _ => (1, 2) };
        _buyin = 50 * _bb;
        _fee = int.TryParse(options.GetValueOrDefault("buyin"), out var f) && f is 20 or 50 or 100 or 250 ? f : 50;
        _levelMin = options.GetValueOrDefault("pace") == "3" ? 3 : 6;
        _botsWanted = BoardBots.Read(options, 5);
        _easy = options.GetValueOrDefault("botlvl") == "easy";
        if (Money && _botsWanted > 0)
            throw new GameError("🤖 Глек сідає лише за стіл на інтерес — на черепки з ботами не грають");
        try { _log = Ctx?.Services?.GetService<ILoggerFactory>()?.CreateLogger("Poker"); }
        catch { _log = null; }
    }

    public override bool Resumable => true;

    public override TimeSpan HoldEmpty => _format switch
    {
        "tour" => TourGiveUp + TimeSpan.FromMinutes(5),
        "fun" => TimeSpan.FromMinutes(5),
        _ => TimeSpan.Zero,
    };

    public override string SeatName(int seat) => $"місце {seat + 1}";

    public override string? SeatBot(int seat) =>
        _s.Seats.FirstOrDefault(x => x.RoomSeat == seat && x.Bot is not null)?.Bot;

    int HumansSeated() => Enumerable.Range(0, Seats).Count(Ctx.Seated);

    public override string? CanStart()
    {
        var humans = Enumerable.Range(0, Seats).Where(Ctx.Seated).Select(s => Ctx.NickOf(s)!).ToList();
        if (Cash && _s.Seats.Count > 0) return "Цей кеш-стіл уже розійшовся — відкрий новий";
        if (_format == "fun")
            return humans.Count + Math.Min(_botsWanted, Seats - humans.Count) >= 2 ? null
                : "Самому нема з ким: хай хтось сяде — або відкрий стіл з «🤖 Глек підсідає»";
        if (humans.Count < 2) return Cash ? "Кеш-стіл — від двох людей" : "Турнір на черепки — від двох людей";
        var need = Cash ? _buyin : _fee;
        var poor = humans.Where(n => Ctx.Balance(n) < need).ToList();
        if (poor.Count == 0) return null;
        var what = Cash ? "викуп" : "внесок";
        return poor.Count == 1
            ? $"У {poor[0]} бракує черепків на {what} ({need})"
            : $"Бракує черепків на {what} ({need}): {string.Join(", ", poor)}";
    }

    // ---------- старт ----------

    public override void Start()
    {
        _s = new PokerState { Seats = [.. Enumerable.Range(0, Seats).Select(i => new PokerSeat { RoomSeat = i })] };
        _dirty = true;
        _joining = null;
        var bots = _format == "fun" ? BoardBots.Seat(Ctx, Seats, _botsWanted) : new string?[Seats];
        for (var i = 0; i < Seats; i++)
        {
            if (Ctx.NickOf(i) is { } nick) _s.Seats[i].Nick = nick;
            else if (bots[i] is { } bot) _s.Seats[i].Bot = bot;
        }
        _s.Humans = _s.Seats.Count(x => x.Nick is not null);

        for (var p = 0; p < Seats; p++)
        {
            var x = _s.Seats[p];
            if (x.Name is null) continue;
            if (Cash)
            {
                if (Ctx.BuyIn(x.Nick!, _buyin, TableMoney.BuyIn))
                {
                    x.State = "play";
                    x.Bought = _buyin;
                    C.Stack[p] = _buyin;
                    _s.In += _buyin;
                }
                else
                {
                    x.State = "out";
                    Ctx.Log($"Покер: у {x.Nick} не вистачило черепків на викуп ({_buyin}) — можна докупитись");
                }
            }
            else if (_format == "tour")
            {
                if (Ctx.BuyIn(x.Nick!, _fee, TableMoney.Fee))
                {
                    x.State = "play";
                    x.Fee = _fee;
                    _s.Pool += _fee;
                    _s.In += _fee;
                    _s.Entrants++;
                    C.Stack[p] = StartChips;
                }
                else
                {
                    x.State = "watch";
                    Ctx.Log($"Покер: у {x.Nick} не вистачило черепків на внесок ({_fee}) — дивиться");
                }
            }
            else
            {
                x.State = "play";
                _s.Entrants++;
                C.Stack[p] = StartChips;
            }
        }

        if (!Cash)
        {
            _s.Level = 0;
            _s.LevelAt = Now + LevelLen;
            if (_s.Entrants < 2)
            {
                _s.Over = true;
                Ctx.Finish([], "Покер: на турнір не зібрались — внески повернуто", null, "🃏 Турнір не відбувся");
                return;
            }
        }
        var (sb, bb) = Blinds();
        Ctx.Log(_format switch
        {
            "cash" => $"Покер: кеш-стіл {_sb}/{_bb}, викуп {_buyin} — черепки лише переходять між гравцями",
            "tour" => $"Покер: турнір на черепки, {_s.Entrants} учасників, банк {_s.Pool}, сліпі {sb}/{bb}",
            _ => $"Покер: турнір на інтерес, {_s.Entrants} гравців, сліпі {sb}/{bb}",
        });
        StartHand();
    }

    (int Sb, int Bb) Blinds() => Cash ? (_sb, _bb) : Levels[Math.Min(_s.Level, Levels.Length - 1)];

    /// <summary>Колода: на проді — криптографічне тасування; з фіксованим сідом каркаса (тести) — від Ctx.Rng.</summary>
    int[] Shuffle()
    {
        var deck = Enumerable.Range(0, 52).ToArray();
        for (var i = deck.Length - 1; i > 0; i--)
        {
            var j = Ctx.Seeded ? Ctx.Rng.Next(i + 1) : RandomNumberGenerator.GetInt32(i + 1);
            (deck[i], deck[j]) = (deck[j], deck[i]);
        }
        return deck;
    }

    bool DealsIn(int p)
    {
        var x = _s.Seats[p];
        if (C.Stack[p] <= 0) return false;
        return Cash ? x.Nick is not null && x.State is "play" or "wait" && !x.Away : x.State == "play";
    }

    void StartHand()
    {
        _dirty = true;
        _s.NextHandAt = null;
        var deal = Enumerable.Range(0, Seats).Select(DealsIn).ToArray();
        if (deal.Count(d => d) < 2)
        {
            if (!Cash && _s.Seats.Count(x => x.State == "play") <= 1) EndTournament();
            return;   // кеш: чекаємо, хто підсяде чи повернеться (тик спробує ще)
        }
        for (var p = 0; p < Seats; p++)
        {
            if (deal[p] && _s.Seats[p].State == "wait") _s.Seats[p].State = "play";
            _s.HandNicks[p] = deal[p] ? _s.Seats[p].Name : null;
            _s.StartStacks[p] = deal[p] ? C.Stack[p] : 0;
        }
        var button = C.Button < 0
            ? Enumerable.Range(0, Seats).Where(p => deal[p]).ElementAt(Ctx.Rng.Next(deal.Count(d => d)))
            : C.Next(C.Button, p => deal[p]);
        var (sb, bb) = Blinds();
        _s.ShowPos = -1;
        _s.RunEq = null;
        _s.StepAt = null;
        C.StartHand(deal, button, sb, bb, Shuffle());
        AfterMove();
    }

    // ---------- хід ----------

    /// <summary>Після будь-якої зміни роздачі: новий хід — новий дедлайн, кінець — підсумок.</summary>
    void AfterMove()
    {
        _dirty = true;
        if (!C.Live)
        {
            HandEnded();
            return;
        }
        if (C.Runout)
        {
            _s.TurnUntil = null;
            if (_s.StepAt is null)
            {
                _s.StepAt = Now + RunoutPause;
                RunoutStarted();
            }
            return;
        }
        _s.TurnUntil = Now + TurnTime;
        _s.BotAt = Now + TimeSpan.FromMilliseconds(700 + Ctx.Rng.Next(900));
    }

    void RunoutStarted()
    {
        var live = Enumerable.Range(0, Seats).Where(p => C.Dealt[p] && !C.Folded[p]).ToArray();
        if (live.Length < 2) return;
        try
        {
            var eq = PokerBot.Equity([.. live.Select(p => C.Hole[p])], C.Board, 0, new Random(unchecked((int)C.HandNo * 7919)), 400);
            _s.RunEq = new double[Seats];
            for (var i = 0; i < live.Length; i++) _s.RunEq[live[i]] = eq[i];
        }
        catch (Exception ex) { _log?.LogDebug(ex, "шанси прогону не порахувались"); }
        if (Ctx.Rng.NextDouble() < 0.6) Ctx.Say(PokerLines.AllInLine(Ctx.Rng));
    }

    /// <summary>Карти позиції p в цій роздачі — справді того, хто зараз на ній сидить (а не того, хто встав і чиє місце зайняли).</summary>
    bool Owns(int p) => C.HandNo > 0 && string.Equals(_s.HandNicks[p], _s.Seats[p].Name, StringComparison.OrdinalIgnoreCase);

    int PosOf(int seat)
    {
        var nick = Ctx.NickOf(seat);
        if (nick is null) return -1;
        for (var p = 0; p < _s.Seats.Count; p++)
            if (_s.Seats[p].RoomSeat == seat && string.Equals(_s.Seats[p].Nick, nick, StringComparison.OrdinalIgnoreCase)) return p;
        return -1;
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (_s.Over) throw new GameError("Гру скінчено");
        var p = PosOf(seat);
        if (p < 0) throw new GameError("Ти не за цим столом");
        var x = _s.Seats[p];
        switch (action)
        {
            case "fold" or "check" or "call" or "raise" or "allin":
            {
                if (!C.Live || C.Runout || C.Turn != p) throw new GameError("Зараз не твій хід");
                var to = 0;
                if (action == "raise")
                {
                    if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("to", out var t) || !t.TryGetInt32(out to))
                        throw new GameError("Скільки ставиш? Сума «до»");
                }
                C.Act(p, action, to);
                x.Strikes = 0;
                x.Away = false;   // походив сам — отже, тут
                x.AwaySince = null;
                AfterMove();
                return ActResult.Done;
            }
            case "away":
                if (x.Away) throw new GameError("Ти вже відійшов ☕");
                x.Away = true;
                x.AwaySince = Now;
                _dirty = true;
                return ActResult.Accept(Cash
                    ? "☕ Відійшов: роздачі тебе пропускають. Тисни «Я тут», як повернешся (через 10 хв стек повернеться в гаманець)"
                    : "☕ Відійшов: за тебе скидатиме. Тисни «Я тут», як повернешся");
            case "back":
                if (!x.Away) throw new GameError("Ти й так за столом");
                x.Away = false;
                x.AwaySince = null;
                x.Strikes = 0;
                _dirty = true;
                if (Ctx.Rng.NextDouble() < 0.5) Ctx.Say(PokerLines.BackLine(Ctx.Rng, x.Name!));
                if (Cash && !C.Live) _s.NextHandAt ??= Now + TimeSpan.FromSeconds(1);
                return ActResult.Done;
            case "rebuy":
                return Rebuy(p);
            case "show":
            {
                if (C.Live || C.Wins.Count != 1 || C.Wins[0].Showdown || C.Wins[0].Winners[0] != p || _s.ShowPos == p || !Owns(p))
                    throw new GameError("Показати карти можна, коли виграв роздачу без шоудауну");
                _s.ShowPos = p;
                _dirty = true;
                Ctx.Log($"Покер: {x.Name} показує карти — {string.Join(" ", C.Hole[p].Select(PokerHand.Code))}");
                return ActResult.Done;
            }
            default:
                throw new GameError("Тут так не ходять");
        }
    }

    ActResult Rebuy(int p)
    {
        var x = _s.Seats[p];
        if (!Cash) throw new GameError("Докупитись можна лише за кеш-столом");
        if (C.InHand(p)) throw new GameError("Докупитись можна між роздачами");
        var cost = _buyin - C.Stack[p];
        if (cost <= 0) throw new GameError($"Докупитись можна, коли стек менший за {_buyin}");
        if (Ctx.Balance(x.Nick!) < cost) throw new GameError($"Бракує черепків: треба {cost}");
        if (!Ctx.BuyIn(x.Nick!, cost, TableMoney.BuyIn)) throw new GameError($"Бракує черепків: треба {cost}");
        C.Stack[p] += cost;
        x.Bought += cost;
        _s.In += cost;
        if (x.State == "out") x.State = C.Live ? "wait" : "play";
        x.Away = false;
        x.AwaySince = null;
        _dirty = true;
        if (!C.Live) _s.NextHandAt ??= Now + TimeSpan.FromSeconds(1);
        Ctx.Log($"Покер: {x.Nick} докуповується на {cost} — у нього {C.Stack[p]}");
        return ActResult.Accept($"Докупився: −{cost} {Economy.Economy.Shards(cost)}, стек {C.Stack[p]}");
    }

    // ---------- тик ----------

    public override TickResult Tick()
    {
        if (_s.Over) return TickResult.None;
        var now = Now;
        if (!Cash && _s.LevelAt is { } at && now >= at)
        {
            while (_s.LevelAt <= now) { _s.Level++; _s.LevelAt += LevelLen; }
            var (sb, bb) = Blinds();
            Ctx.Log($"Покер: сліпі ростуть — {sb}/{bb} з наступної роздачі");
            _dirty = true;
        }
        if (Cash) KickAway(now);
        if (_s.Over) return Flush();

        if (C.Live)
        {
            if (C.Runout)
            {
                if (_s.StepAt is { } step && now >= step)
                {
                    _dirty = true;
                    if (C.RunoutStep()) { _s.StepAt = null; HandEnded(); }
                    else _s.StepAt = now + RunoutPause;
                }
            }
            else if (C.Turn >= 0)
            {
                var p = C.Turn;
                var x = _s.Seats[p];
                if (x.Bot is not null) { if (now >= _s.BotAt) BotMove(p); }
                else if (x.Away || x.Gone || x.Nick is null) { if (now >= _s.BotAt) AutoMove(p, false); }
                else if (now >= _s.TurnUntil) AutoMove(p, true);
            }
        }
        else if (_s.NextHandAt is { } next && now >= next) StartHand();
        else if (Cash && _s.NextHandAt is null && !_s.Over) _s.NextHandAt = now + TimeSpan.FromSeconds(1);

        if (_format == "tour" && !_s.Over)
        {
            if (HumansSeated() > 0) _s.EmptySince = null;
            else if (_s.EmptySince is null) _s.EmptySince = now;
            else if (now - _s.EmptySince >= TourGiveUp) EndByChips();
        }
        return Flush();
    }

    TickResult Flush()
    {
        if (!_dirty) return TickResult.None;
        _dirty = false;
        return new TickResult(false, true);
    }

    void AutoMove(int p, bool timeout)
    {
        var x = _s.Seats[p];
        var legal = C.Legal(p)!;
        if (timeout)
        {
            x.Strikes++;
            if (x.Strikes >= 2 && !x.Away)
            {
                x.Away = true;
                x.AwaySince = Now;
                Ctx.Log($"Покер: {x.Name} двічі проґавив хід — відійшов ☕");
            }
        }
        C.Act(p, legal.Check ? "check" : "fold");
        AfterMove();
    }

    void BotMove(int p)
    {
        var (a, to) = PokerBot.Decide(C, p, Ctx.Rng, _easy);
        try { C.Act(p, a, to); }
        catch (GameError) { C.Act(p, C.Legal(p)!.Check ? "check" : "fold"); }
        AfterMove();
    }

    /// <summary>Кеш: ☕ довше 10 хв — стек у гаманець, людина лишається за столом без фішок (може докупитись).</summary>
    void KickAway(DateTimeOffset now)
    {
        for (var p = 0; p < Seats; p++)
        {
            var x = _s.Seats[p];
            if (x.Nick is null || !x.Away || x.AwaySince is not { } since || now - since < CashAwayLimit || C.InHand(p)) continue;
            if (C.Stack[p] <= 0 && x.State == "out") continue;
            var amount = C.Stack[p];
            if (CashOutStack(p))
            {
                x.State = "out";
                x.Away = false;
                x.AwaySince = null;
                Ctx.Log($"Покер: {x.Nick} відійшов надовго — {amount} повернуто в гаманець, місце за ним");
                _dirty = true;
            }
        }
    }

    bool CashOutStack(int p)
    {
        var x = _s.Seats[p];
        var amount = C.Stack[p];
        if (amount <= 0) return true;
        if (!Ctx.CashOut(x.Nick!, amount, TableMoney.CashOut))
        {
            _log?.LogError("покер {Room}: каркас не дав виплатити {Nick} {Amount}", Ctx.RoomId, x.Nick, amount);
            return false;
        }
        C.Stack[p] = 0;
        _s.Out += amount;
        return true;
    }

    // ---------- кінець роздачі ----------

    void HandEnded()
    {
        _dirty = true;
        _s.StepAt = null;
        _s.TurnUntil = null;
        var wins = new List<PokerWinView>();
        var lines = new List<string>();
        foreach (var pot in C.Wins)
            for (var i = 0; i < pot.Winners.Length; i++)
            {
                var p = pot.Winners[i];
                var name = _s.HandNicks[p] ?? _s.Seats[p].Name ?? "?";
                var hand = pot.Showdown ? PokerHand.Name(pot.Score) : null;
                var five = pot.Showdown ? PokerHand.BestFive([.. C.Hole[p], .. C.Board]).Select(PokerHand.Code).ToArray() : null;
                wins.Add(new PokerWinView(p, name, pot.Shares[i], hand, five));
            }
        foreach (var g in wins.GroupBy(w => w.Pos))
        {
            var w = g.First();
            var hand = g.Select(x => x.Hand).FirstOrDefault(h => h is not null);
            lines.Add($"{w.Name} забирає {g.Sum(x => x.Amount)}{(hand is null ? "" : " — " + hand)}");
        }
        var text = string.Join("; ", lines);
        _s.Last = new PokerLast(C.HandNo, wins, text);
        if (text.Length > 0) Ctx.Log($"Покер: {text}");
        Signals(wins);

        if (Cash)
        {
            for (var p = 0; p < Seats; p++)
                if (_s.Seats[p].State == "play" && C.Stack[p] <= 0) _s.Seats[p].State = "out";
            CheckInvariant();
        }
        else
        {
            var busted = Enumerable.Range(0, Seats).Where(p => _s.Seats[p].State == "play" && C.Stack[p] <= 0)
                .OrderByDescending(p => _s.StartStacks[p]).ToList();
            var alive = _s.Seats.Count(x => x.State == "play") - busted.Count;
            for (var i = 0; i < busted.Count; i++)
            {
                var x = _s.Seats[busted[i]];
                x.State = "bust";
                x.Place = alive + 1 + i;
                Ctx.Log($"Покер: {x.Name} вибуває — {x.Place}-е місце");
                if (x.Nick is not null && Ctx.Rng.NextDouble() < 0.7) Ctx.Say(PokerLines.BustLine(Ctx.Rng, x.Name!, x.Place));
            }
            if (alive <= 1)
            {
                EndTournament();
                return;
            }
        }
        _s.NextHandAt = Now + HandPause;
    }

    /// <summary>Ачівки й репліки Глека після роздачі.</summary>
    void Signals(List<PokerWinView> wins)
    {
        var bb = Math.Max(1, C.BigBlind);
        foreach (var pot in C.Wins.Where(w => w.Showdown))
            foreach (var p in pot.Winners)
            {
                var cat = PokerHand.Category(pot.Score);
                if (cat < PokerHand.Quads) continue;
                var royal = cat == PokerHand.StraightFlush && PokerHand.Short(pot.Score) == "роял-флеш";
                if (SeatOfHuman(p) is { } s)
                {
                    Ctx.Award(s, 0, "ach:poker-quads");
                    if (royal) Ctx.Award(s, 0, "ach:poker-royal");
                }
                var name = _s.HandNicks[p] ?? "?";
                Ctx.Say(royal ? PokerLines.RoyalLine(Ctx.Rng, name) : PokerLines.QuadsLine(Ctx.Rng, name, PokerHand.Name(pot.Score)));
            }
        var total = C.Wins.Sum(w => w.Amount);
        var top = wins.GroupBy(w => w.Pos).Select(g => (g.First().Name, Amount: g.Sum(x => x.Amount))).OrderByDescending(x => x.Amount).FirstOrDefault();
        // бед-біт: у програвшого на початку прогону було 80 %+
        if (_s.RunEq is { } eq && top.Name is not null)
        {
            var winners = wins.Select(w => w.Pos).ToHashSet();
            for (var p = 0; p < Seats; p++)
                if (eq[p] >= 0.8 && !winners.Contains(p) && _s.HandNicks[p] is { } loser)
                {
                    Ctx.Say(PokerLines.BadBeatLine(Ctx.Rng, loser, top.Name, (int)Math.Round(eq[p] * 100)));
                    _s.BigPotHand = C.HandNo;
                    return;
                }
        }
        if (total >= 40 * bb && C.HandNo - _s.BigPotHand >= 3 && top.Name is not null && Ctx.Rng.NextDouble() < 0.7)
        {
            _s.BigPotHand = C.HandNo;
            Ctx.Say(PokerLines.BigPotLine(Ctx.Rng, top.Name, top.Amount));
        }
    }

    /// <summary>Кімнатне місце людини на позиції p, якщо вона зараз сидить; інакше null.</summary>
    int? SeatOfHuman(int p)
    {
        var x = _s.Seats[p];
        return x.Nick is not null && string.Equals(Ctx.NickOf(x.RoomSeat), x.Nick, StringComparison.OrdinalIgnoreCase) ? x.RoomSeat : null;
    }

    /// <summary>Кеш: Σ стеків + внесене в роздачу = списане − виплачене. Порушення — у лог (гроші!).</summary>
    public bool CheckInvariant()
    {
        if (!Cash) return true;
        var ok = C.Chips == _s.In - _s.Out;
        if (!ok) _log?.LogError("покер {Room}: фішок {Chips}, а списано−виплачено {Held}", Ctx.RoomId, C.Chips, _s.In - _s.Out);
        return ok;
    }

    // ---------- кінець партії ----------

    void EndTournament()
    {
        if (_s.Over) return;
        var winner = Enumerable.Range(0, Seats).FirstOrDefault(p => _s.Seats[p].State == "play" && C.Stack[p] > 0, -1);
        if (winner < 0) winner = Enumerable.Range(0, Seats).Where(p => _s.Seats[p].State == "play").DefaultIfEmpty(-1).First();
        if (winner >= 0) _s.Seats[winner].Place = 1;
        Finale(winner, $"{(winner >= 0 ? _s.Seats[winner].Name : "?")} виграє турнір", null);
    }

    /// <summary>Турнір на черепки, з-за столу пішли всі й не повернулись: місця — за фішками.</summary>
    void EndByChips()
    {
        // Спершу скасувати роздачу (внесене в неї — назад у стеки), а вже тоді рахувати місця: інакше сліпі й ставки
        // роздачі, що йшла, не враховано.
        if (C.Live) C.VoidHand();
        var order = Enumerable.Range(0, Seats).Where(p => _s.Seats[p].State == "play")
            .OrderByDescending(p => C.Stack[p]).ThenBy(p => p).ToList();
        var alive = order.Count;
        for (var i = 0; i < order.Count; i++) _s.Seats[order[i]].Place = i + 1;
        Finale(alive > 0 ? order[0] : -1, "усі пішли з-за столу — місця за фішками", "🏁 Турнір зупинено: усі пішли");
    }

    void Finale(int winner, string text, string? verdict)
    {
        _s.Over = true;
        _dirty = true;
        var prize = "";
        if (_format == "tour" && _s.Pool > 0)
        {
            var second = _s.Seats.FirstOrDefault(x => x.Place == 2 && x.Nick is not null);
            var (first, two) = Prizes(_s.Pool, _s.Entrants);
            if (winner >= 0 && _s.Seats[winner].Nick is { } w1 && Ctx.CashOut(w1, first, TableMoney.Prize))
            {
                _s.Out += first;
                prize = $", приз {first}";
            }
            if (two > 0 && second?.Nick is { } w2 && Ctx.CashOut(w2, two, TableMoney.Prize))
            {
                _s.Out += two;
                prize += $", {w2} — {two}";
            }
        }
        var scores = new Dictionary<int, long>();
        foreach (var x in _s.Seats)
            if (x.Nick is not null && x.Place > 0 && string.Equals(Ctx.NickOf(x.RoomSeat), x.Nick, StringComparison.OrdinalIgnoreCase))
                scores[x.RoomSeat] = x.Place;
        int[] winners = [];
        if (winner >= 0 && SeatOfHuman(winner) is { } ws)
        {
            winners = [ws];
            if (_s.Humans >= 4) Ctx.Award(ws, 0, "ach:poker-champ");
        }
        var name = winner >= 0 ? _s.Seats[winner].Name : null;
        verdict ??= winners.Length == 0 && name is not null ? $"🏆 Переміг {name}" : null;
        Ctx.Finish(winners, $"Покер: {text}{prize}", scores.Count > 0 ? scores : null, verdict);
    }

    /// <summary>Приз турніру на черепки: 2–4 учасники — усе першому; від 5 — 70/30 (решта від ділення — першому).</summary>
    public static (int First, int Second) Prizes(int pool, int entrants)
    {
        if (entrants < 5) return (pool, 0);
        var second = pool * 30 / 100;
        return (pool - second, second);
    }

    // ---------- вихід і повернення ----------

    public override void OnLeave(int seat)
    {
        var p = PosOf(seat);
        if (p < 0 || _s.Over) return;
        var x = _s.Seats[p];
        _dirty = true;
        if (Cash)
        {
            if (C.InHand(p))
            {
                var (turn, hand) = (C.Turn, C.HandNo);
                C.ForceFold(p);
                Ctx.Log($"Покер: {x.Nick} встає посеред роздачі — фолд");
                // чужий хід не скидаємо: дедлайн того, хто думає, лишається той самий
                if (C.Live && !C.Runout && C.Turn == turn && C.HandNo == hand) _dirty = true;
                else AfterMove();
            }
            var amount = C.Stack[p];
            if (!CashOutStack(p)) return;   // гроші не пішли — місце не чіпаємо, фішки лишаються за ним
            if (amount > 0) Ctx.Log($"Покер: {x.Nick} встає з-за столу й забирає {amount}");
            _s.Seats[p] = new PokerSeat { RoomSeat = x.RoomSeat };
            var others = _s.Seats.Count(y => y.Nick is not null && Ctx.Seated(y.RoomSeat));
            if (others == 0)
            {
                _s.Over = true;
                Ctx.Finish([], "Покер: кеш-стіл розійшовся — усі забрали свої черепки", null, "💰 Стіл розійшовся");
            }
            return;
        }
        x.Gone = true;
        x.Away = true;
        x.AwaySince ??= Now;
        if (x.State == "play") Ctx.Log($"Покер: {x.Nick} відходить від столу — фішки чекають ☕");
    }

    public override bool LateJoin(string nick)
    {
        if (_s.Over || _s.Seats.Count == 0) return false;
        var mine = _s.Seats.FirstOrDefault(x => string.Equals(x.Nick, nick, StringComparison.OrdinalIgnoreCase));
        if (!Cash) return mine is { Gone: true };
        if (mine is not null || _s.Seats.All(x => x.Nick is not null)) return false;
        if (Ctx.Balance(nick) < _buyin || !Ctx.BuyIn(nick, _buyin, TableMoney.BuyIn)) return false;
        _joining = (nick, _buyin);
        return true;
    }

    public override string? LateJoinRefusal(string nick)
    {
        if (_s.Over || _s.Seats.Count == 0) return null;
        if (!Cash) return "Турнір уже йде: підсісти не можна — повернутись може лише той, хто грав";
        var bal = Ctx.Balance(nick);
        return bal < _buyin ? $"Щоб сісти, треба {_buyin} {Economy.Economy.Shards(_buyin)} — у тебе {bal}" : null;
    }

    public override string? LateJoinGreeting(string nick) =>
        Cash && _joining is { } j && string.Equals(j.Nick, nick, StringComparison.OrdinalIgnoreCase)
            ? $"Є! Ти за столом: викуп {j.Amount} {Economy.Economy.Shards(j.Amount)} — граєш з наступної роздачі"
            : null;

    public override void OnJoin(int seat)
    {
        var nick = Ctx.NickOf(seat)!;
        _dirty = true;
        if (Cash)
        {
            var p = _s.Seats.FindIndex(x => x.RoomSeat == seat);
            if (p < 0 || _joining is not { } j || !string.Equals(j.Nick, nick, StringComparison.OrdinalIgnoreCase)) return;
            _joining = null;
            _s.Seats[p] = new PokerSeat { Nick = nick, RoomSeat = seat, State = "wait", Bought = j.Amount };
            C.Stack[p] += j.Amount;
            _s.In += j.Amount;
            if (!C.Live) _s.NextHandAt ??= Now + TimeSpan.FromSeconds(1);
            Ctx.Log($"Покер: {nick} підсідає за стіл (викуп {j.Amount}) — грає з наступної роздачі");
            return;
        }
        var me = _s.Seats.FindIndex(x => string.Equals(x.Nick, nick, StringComparison.OrdinalIgnoreCase));
        if (me < 0) return;
        var was = _s.Seats[me].RoomSeat;
        if (was != seat)
        {
            // каркас посадив на інше крісло: та позиція, що показувалась на ньому (бот чи порожня), бере старе
            var other = _s.Seats.FindIndex(x => x.RoomSeat == seat);
            if (other >= 0) _s.Seats[other].RoomSeat = was;
            _s.Seats[me].RoomSeat = seat;
        }
        var x = _s.Seats[me];
        x.Gone = false;
        x.Away = false;
        x.AwaySince = null;
        x.Strikes = 0;
        _s.EmptySince = null;
        if (x.State == "play") Ctx.Say(PokerLines.BackLine(Ctx.Rng, nick));
    }

    // ---------- гроші при обриві ----------

    public override IReadOnlyDictionary<string, int>? SettleTable()
    {
        if (!Money) return null;
        var owed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Add(string? nick, int amount)
        {
            if (nick is null || amount <= 0) return;
            owed[nick] = owed.GetValueOrDefault(nick) + amount;
        }
        if (Cash)
        {
            for (var p = 0; p < Seats; p++)
            {
                Add(_s.Seats[p].Nick, C.Stack[p]);
                // роздачу скасовано: внесене в неї — тому, хто вносив (і тому, хто вже встав)
                if (C.Live) Add(_s.HandNicks[p], C.Total[p]);
            }
        }
        else if (!_s.Over)
            foreach (var x in _s.Seats) Add(x.Nick, x.Fee);
        return owed;
    }

    // ---------- збереження ----------

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override string? Save() => JsonSerializer.Serialize(_s, Json);

    public override void Load(string json)
    {
        _s = JsonSerializer.Deserialize<PokerState>(json, Json) ?? throw new InvalidOperationException("порожній стан покеру");
        _dirty = true;
    }

    public override void Resumed(TimeSpan pause)
    {
        DateTimeOffset? Shift(DateTimeOffset? t) => t + pause;
        _s.LevelAt = Shift(_s.LevelAt);
        _s.NextHandAt = Shift(_s.NextHandAt);
        _s.TurnUntil = Shift(_s.TurnUntil);
        _s.BotAt = Shift(_s.BotAt);
        _s.StepAt = Shift(_s.StepAt);
        _s.EmptySince = Shift(_s.EmptySince);
        foreach (var x in _s.Seats) x.AwaySince = Shift(x.AwaySince);
        _dirty = true;
    }

    // ---------- вид ----------

    public override object View(int? seat)
    {
        var me = seat is int s ? PosOf(s) : -1;
        var c = C;
        var legal = me >= 0 ? c.Legal(me) : null;
        var seats = new List<object>(Seats);
        for (var p = 0; p < _s.Seats.Count; p++)
        {
            var x = _s.Seats[p];
            var dealt = c.Dealt[p] && c.Hole[p].Length == 2 && c.HandNo > 0;
            var open = dealt && ((p == me && Owns(p))
                || (c.Live && c.ShowAll && !c.Folded[p])
                || (!c.Live && (c.Shown[p] || _s.ShowPos == p)));
            var state = x.Name is null ? "empty"
                : x.State is "bust" or "out" or "watch" or "wait" ? x.State
                : c.Live && c.Dealt[p] && c.Folded[p] ? "fold"
                : c.Live && c.Dealt[p] && c.AllIn[p] ? "allin"
                : c.Live && !c.Dealt[p] ? "sitout"
                : "play";
            seats.Add(new
            {
                pos = p,
                seat = x.RoomSeat,
                name = x.Name,
                bot = x.Bot is not null,
                stack = c.Stack[p],
                bet = c.Live ? c.Bet[p] : 0,
                state,
                away = x.Away,
                gone = x.Gone,
                cards = open ? c.Hole[p].Select(PokerHand.Code).ToArray() : null,
                hasCards = c.Live && c.Dealt[p] && !c.Folded[p],
                place = x.Place > 0 ? x.Place : (int?)null,
                dealer = c.HandNo > 0 && c.Button == p,
                sb = c.Live && c.SbPos == p,
                bb = c.Live && c.BbPos == p,
            });
        }
        string? myHand = null;
        string[]? myBest = null;
        if (me >= 0 && c.Dealt[me] && c.Hole[me].Length == 2 && Owns(me))
        {
            int[] cards = [.. c.Hole[me], .. c.Board];
            if (cards.Length >= 5)
            {
                var score = PokerHand.Eval(cards);
                myHand = PokerHand.Name(score);
                myBest = PokerHand.BestFive(cards).Select(PokerHand.Code).ToArray();
            }
            else if (PokerHand.Rank(cards[0]) == PokerHand.Rank(cards[1]))
                myHand = $"пара: {PairName(cards[0])}";
        }
        var turnPos = c.Live && !c.Runout && c.Turn >= 0 ? c.Turn : -1;
        var (sb, bb) = Blinds();
        var nextLevel = Cash ? ((int, int)?)null : Levels[Math.Min(_s.Level + 1, Levels.Length - 1)];
        var mine = me >= 0 ? _s.Seats[me] : null;
        var (first, second) = Prizes(_s.Pool, _s.Entrants);
        return new
        {
            format = _format,
            hand = c.HandNo,
            street = c.Street,
            live = c.Live,
            blinds = new { sb = c.Live ? c.SmallBlind : sb, bb = c.Live ? c.BigBlind : bb },
            level = Cash ? (int?)null : _s.Level + 1,
            nextBlinds = nextLevel is { } nl ? new { sb = nl.Item1, bb = nl.Item2 } : null,
            levelAt = Cash ? null : _s.LevelAt,
            me = me >= 0 ? me : (int?)null,
            turn = turnPos >= 0 ? _s.Seats[turnPos].RoomSeat : (int?)null,
            turnPos = turnPos >= 0 ? turnPos : (int?)null,
            turnUntil = turnPos >= 0 ? _s.TurnUntil : null,
            turnMs = (int)TurnTime.TotalMilliseconds,
            seats,
            board = c.HandNo > 0 ? c.Board.Select(PokerHand.Code).ToArray() : [],
            pots = c.Live ? c.Pots(true).Select(x => new { amount = x.Amount, seats = x.Seats }).ToArray() : [],
            potTotal = c.Live ? c.Total.Sum() : 0,
            actions = legal is { } l && me == turnPos
                ? new { check = l.Check, call = l.Call, raiseMin = l.RaiseMin, raiseMax = l.RaiseMax, allIn = l.AllIn, pot = c.Total.Sum() }
                : null,
            myHand,
            myBest,
            runout = c.Live && c.Runout,
            showdown = !c.Live && c.HandNo > 0 && c.Shown.Any(v => v),
            last = _s.Last,
            canShow = me >= 0 && !c.Live && c.Wins.Count == 1 && !c.Wins[0].Showdown && c.Wins[0].Winners[0] == me && _s.ShowPos != me && Owns(me),
            nextHandAt = c.Live ? null : _s.NextHandAt,
            waiting = Cash && !c.Live && !_s.Over && Enumerable.Range(0, Seats).Count(DealsIn) < 2 ? "Чекаємо, хто підсяде" : null,
            cash = Cash
                ? new
                {
                    sb = _sb, bb = _bb, buyin = _buyin,
                    bought = mine?.Bought ?? 0,
                    canRebuy = mine?.Nick is not null && !c.InHand(me) && c.Stack[me] < _buyin,
                    rebuyCost = mine?.Nick is not null ? Math.Max(0, _buyin - c.Stack[me]) : 0,
                }
                : null,
            tour = _format == "tour" ? new { fee = _fee, pool = _s.Pool, entrants = _s.Entrants, prizes = second > 0 ? new[] { first, second } : [first] } : null,
            over = _s.Over,
        };
    }

    static readonly string[] Pairs = ["двійки", "трійки", "четвірки", "п'ятірки", "шістки", "сімки", "вісімки", "дев'ятки", "десятки", "валети", "дами", "королі", "тузи"];
    static string PairName(int card) => Pairs[PokerHand.Rank(card)];
}
