using System.Text.Json;
using static Hlechyky.Games.Impl.SkarbCards;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Скарб Полуботка» — гра з прихованими ролями на 3–10: копачі риють хід від колодязя до гетьманського золота, таємні
/// кроти шкодять (контракт <c>docs/games/specs/skarb.md</c>). Правила — у <see cref="SkarbCore"/>, Глеки обох ролей —
/// <see cref="SkarbBot"/>, голос ведучого — <see cref="SkarbLines"/>; тут каркас, як у «Хуторах»: опції, розсадка з
/// Глеками, таймер ходу, пауза між раундами, вихід і повернення, вид. Гра прихована (<c>Hidden</c>): роль, рука й мапи
/// кожного їдуть лише йому.
/// </summary>
public sealed class Skarb : Game
{
    public override GameInfo Info { get; } = new(
        "skarb", "Скарб Полуботка", "Скарб Полуботка", GameGroup.Party, 1, MaxSeats, Start: StartMode.ByHost, Hidden: true,
        Rated: false, Score: ScoreOrder.None,
        Options:
        [
            BoardBots.Option(9),
            new GameOption("timer", "Час на хід", [("none", "без часу"), ("30", "30 с"), ("60", "60 с"), ("90", "90 с")], "60"),
            new GameOption("allies", "Кроти",
                [("0", "кожен сам по собі — не знають одне одного"), ("1", "знають своїх")], "0"),
        ],
        Hint: "Скарб Полуботка — на 3–10: копачі риють хід під селом до гетьманського золота, а серед них таємні Кроти "
            + "завалюють тунелі й ламають кирки. Три раунди, ролі щоразу нові, виграє той, у кого більше злитків. Бракує людей — "
            + "підсядуть Глеки 🤖");

    static readonly string[] Colors =
        ["червоні", "сині", "помаранчеві", "фіолетові", "зелені", "рожеві", "бірюзові", "гірчичні", "брунатні", "сірі"];

    /// <summary>Скільки показуємо роль на початку раунду: перший хід раунду чекає на стільки довше.</summary>
    public const int RoleRevealMs = 4000;
    /// <summary>Скільки задрімала людина має на свій хід сама, перш ніж Глек зіграє за неї.</summary>
    public const int AwayGraceMs = 5000;
    /// <summary>Пауза між раундами (фаза round).</summary>
    public const int PauseMs = 15_000;

    int _timerMs = 60_000;
    int _botsWanted;
    bool _allies;

    SkarbCore _core = new([], new Random(0));
    string?[] _bots = new string?[MaxSeats];
    string?[] _nicks = new string?[MaxSeats];
    readonly bool[] _away = new bool[MaxSeats];
    readonly int[] _missed = new int[MaxSeats];
    SkarbBot _brain = new();

    // час
    string _turnKey = "";
    DateTimeOffset _turnAt, _roundAt, _startedAt;
    DateTimeOffset? _deadline, _botAt, _nextAt;
    int _deadlineMs;
    readonly HashSet<int> _ready = [];
    int _endsSeen;
    bool _announced;

    // голос
    readonly SkarbGlek _glek = new();
    long _heardSeq;
    bool _closeSaid;
    readonly HashSet<int> _rich = [];

    /// <summary>Правила поточної партії (тести й діагностика).</summary>
    public SkarbCore Core => _core;
    public SkarbBot Brain => _brain;

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _timerMs = options.TryGetValue("timer", out var t)
            ? t switch { "none" => 0, "30" => 30_000, "90" => 90_000, _ => 60_000 }
            : 60_000;
        _allies = options.TryGetValue("allies", out var a) && a == "1";
        _botsWanted = BoardBots.Read(options, 9);
    }

    public override string? CanStart()
    {
        var humans = BoardBots.Humans(Ctx, MaxSeats);
        return humans + Math.Min(_botsWanted, MaxSeats - humans) >= MinSeats ? null
            : "Утрьох щонайменше: поклич людей або додай 🤖 Глеків";
    }

    public override string SeatName(int seat) => seat >= 0 && seat < Colors.Length ? Colors[seat] : $"гравець {seat + 1}";

    bool IsBot(int seat) => seat >= 0 && seat < MaxSeats && _bots[seat] is not null;
    public override string? SeatBot(int seat) => IsBot(seat) ? _bots[seat] : null;
    /// <summary>За місце ходить Глек: справжній бот або людина, що вийшла чи проспала двічі.</summary>
    bool Auto(int seat) => IsBot(seat) || _away[seat];

    public override void Start()
    {
        _bots = BoardBots.Seat(Ctx, MaxSeats, _botsWanted);
        var seats = Enumerable.Range(0, MaxSeats).Where(s => Ctx.Seated(s) || _bots[s] is not null).ToArray();
        _nicks = [.. Enumerable.Range(0, MaxSeats).Select(s => _bots[s] ?? Ctx.NickOf(s))];
        Array.Clear(_away);
        Array.Clear(_missed);
        // seq подій — наскрізний через усі партії столу: клієнт тримає найбільший побачений і анімує лише новіші.
        var seq = _core.Seq;
        _core = new SkarbCore(seats, Ctx.Rng) { Allies = _allies, NameOf = Nick, Seq = seq };
        _brain = new SkarbBot();
        _announced = false;
        _turnKey = "";
        _deadline = _botAt = _nextAt = null;
        _ready.Clear();
        _endsSeen = 0;
        _heardSeq = seq;
        _closeSaid = false;
        _rich.Clear();
        _startedAt = Ctx.Clock.UtcNow;
        _glek.Reset();
        // Порядок Ctx.Rng — як у контракті §2.1: спершу колода злитків і перший гравець, потім роздача раунду; голос — після.
        _core.Begin();
        Glek(SkMoment.Start);
        After();
    }

    string Nick(int seat) => _nicks[seat] ?? Ctx.NickOf(seat) ?? SeatName(seat);

    // =====================================================================================
    // Дії (§6)
    // =====================================================================================

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (_core.Over) return ActResult.Fail("Партія вже скінчилась");
        if (action == BoardBots.Nudge) return BotTurn();
        if (action == "timeout") return Timeout();
        if (seat < 0 || seat >= MaxSeats || !_core.In[seat]) return ActResult.Fail("Ти не граєш за цим столом");
        if (IsBot(seat)) return ActResult.Fail("Тут грає Глек");
        string? error;
        switch (action)
        {
            case "play": error = Play(seat, payload); break;
            case "pass":
                error = _core.Gate(seat) ?? (Int(payload, "card") is { } pc ? _core.Pass(seat, pc) : "Не зрозумів, яку карту скинути");
                break;
            case "next":
                if (_core.Phase != SkPhase.Round) return ActResult.Fail("Зараз не перерва між раундами");
                if (!Next(seat)) return ActResult.Done;
                error = null;
                break;
            default: return ActResult.Fail("Тут так не ходять");
        }
        if (error is not null) return ActResult.Fail(error);
        // Будь-яка своя дія повертає людину з-під опіки Глека.
        if (_away[seat])
        {
            _core.Note($"{Nick(seat)} знову копає сам");
            Glek(SkMoment.Back, Who(seat));
        }
        _away[seat] = false;
        _missed[seat] = 0;
        After();
        return ActResult.Done;
    }

    string? Play(int seat, JsonElement p)
    {
        if (_core.Gate(seat) is { } g) return g;
        if (Int(p, "card") is not { } card) return "Не зрозумів, яку карту";
        if (!_core.Has(seat, card)) return "Такої карти в тебе нема";
        return SkarbCore.CardOf(card).K switch
        {
            SkKind.Path => _core.Place(seat, card, Int(p, "x"), Int(p, "y"), Turn(p)),
            SkKind.Break => _core.BreakTool(seat, card, Int(p, "seat")),
            SkKind.Fix => _core.FixTool(seat, card, Int(p, "seat"), Tool(p)),
            SkKind.Map => _core.LookMap(seat, card, Int(p, "goal")),
            _ => _core.Rockfall(seat, card, Int(p, "x"), Int(p, "y")),
        };
    }

    /// <summary>«tool» ремонту: не прислали — null (ядро здогадається); невідомий — 99 («Скажи, що саме лагодити»).</summary>
    static int? Tool(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("tool", out var t) || t.ValueKind == JsonValueKind.Null) return null;
        var i = t.ValueKind == JsonValueKind.String ? ToolIdx(t.GetString()) : -1;
        return i >= 0 ? i : 99;
    }

    /// <summary>«r» ходу: не прислали — 0; прислали не ціле — -1 (ядро скаже «лише перевернути»).</summary>
    static int Turn(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("r", out var r) || r.ValueKind == JsonValueKind.Null) return 0;
        return r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out var n) ? n : -1;
    }

    /// <summary>«Я готовий» у паузі між раундами. true — щось змінилось (новий раунд або новий у ready).</summary>
    bool Next(int seat)
    {
        var now = Ctx.Clock.UtcNow;
        var late = _nextAt is { } at && now >= at;
        if (!_ready.Add(seat) && !late) return false;
        var all = _core.Order.Where(s => !IsBot(s) && !_away[s] && Ctx.Seated(s)).All(_ready.Contains);
        if (late || all) NewRound();
        return true;
    }

    void NewRound()
    {
        _ready.Clear();
        _nextAt = null;
        _closeSaid = false;
        _core.NextRound();
    }

    /// <summary>
    /// Хід Глека (штовхає клієнт людини): рівно один хід за того, чия черга, — справжнього бота або людини, що відійшла.
    /// Хід, що не вдався (не мав би), — пас найгіршою картою, щоб стіл не завис.
    /// </summary>
    ActResult BotTurn()
    {
        if (_botAt is not { } at) return ActResult.Fail("Глекам нема що робити");
        if (Ctx.Clock.UtcNow < at) return ActResult.Fail("Глек ще думає");
        var s = _core.Turn;
        if (_core.Phase != SkPhase.Play || !Auto(s)) return ActResult.Fail("Глекам нема що робити");
        _brain.Observe(_core);
        var mv = _brain.Decide(_core, s, Ctx.Rng);
        if (_core.Apply(s, mv) is not null) _core.Apply(s, SkarbBot.Worst(_core, s));
        After();
        return ActResult.Done;
    }

    /// <summary>Минув строк: за того, хто тягне, — пас найгіршою для його ролі картою. Двічі поспіль — далі за нього Глек.</summary>
    ActResult Timeout()
    {
        if (_deadline is not { } dl || _core.Phase != SkPhase.Play) return ActResult.Fail("Тут без часу");
        if (Ctx.Clock.UtcNow < dl) return ActResult.Fail("Ще є час");
        var s = _core.Turn;
        _core.Apply(s, SkarbBot.Worst(_core, s));
        if (!Auto(s) && ++_missed[s] >= 2)
        {
            _away[s] = true;
            _core.Note($"{Nick(s)} задрімав — Глек копає замість нього");
            Glek(SkMoment.Away, Who(s));
        }
        After();
        return ActResult.Done;
    }

    /// <summary>Після кожної зміни: журнал кімнати, Глеки, голос, кінець раунду й партії, строк.</summary>
    void After()
    {
        foreach (var line in _core.Journal) Ctx.Log($"{Info.Title}: {line}");
        _core.Journal.Clear();
        _brain.Observe(_core);
        Voice();
        RoundEnds();
        Announce();
        Rearm();
        Arm();
    }

    /// <summary>Новий підсумок раунду: пауза, ачівки (лише людям за столом, не away).</summary>
    void RoundEnds()
    {
        while (_endsSeen < _core.Ends.Count)
        {
            var end = _core.Ends[_endsSeen++];
            var now = Ctx.Clock.UtcNow;
            _roundAt = now;
            _ready.Clear();
            _nextAt = _core.Phase == SkPhase.Round ? now.AddMilliseconds(PauseMs) : null;
            var lone = end.Side == "moles" && _core.Seats >= 6 && _core.Order.Count(o => end.Mole[o]) == 1;
            foreach (var s in _core.Order)
            {
                if (IsBot(s) || _away[s] || !Ctx.Seated(s)) continue;
                if (end.Finisher == s) Ctx.Award(s, 0, "ach:skarb-gold");
                if (end.Side == "moles" && end.Mole[s])
                {
                    Ctx.Award(s, 0, "ach:skarb-mole");
                    if (lone) Ctx.Award(s, 0, "ach:skarb-lone");
                }
                if (_core.Gold[s] >= 12 && _rich.Add(s)) Ctx.Award(s, 0, "ach:skarb-rich");
            }
        }
    }

    void Rearm()
    {
        if (_core.Phase != SkPhase.Play)
        {
            _deadline = null;
            _deadlineMs = 0;
            return;
        }
        var key = $"{_core.Round}|{_core.Turns}";
        if (key == _turnKey) return;
        _turnKey = key;
        var now = Ctx.Clock.UtcNow;
        _turnAt = now;
        if (_core.TurnsInRound == 0) _roundAt = now;
        if (_timerMs <= 0) { _deadline = null; _deadlineMs = 0; return; }
        _deadlineMs = _timerMs + (_core.TurnsInRound == 0 ? RoleRevealMs : 0);
        _deadline = now.AddMilliseconds(_deadlineMs);
    }

    void Arm()
    {
        _botAt = null;
        if (_core.Phase != SkPhase.Play) return;
        var s = _core.Turn;
        if (!Auto(s)) return;
        var at = _turnAt.AddMilliseconds(BoardBots.ThinkMs);
        if (_core.TurnsInRound == 0 && at < _roundAt.AddMilliseconds(RoleRevealMs)) at = _roundAt.AddMilliseconds(RoleRevealMs);
        // Пільга задрімалому: настав хід людини, за яку грає Глек, — перші 5 с не чіпаємо, хай повернеться сама.
        if (_away[s] && !IsBot(s) && Ctx.Seated(s) && at < _turnAt.AddMilliseconds(AwayGraceMs)) at = _turnAt.AddMilliseconds(AwayGraceMs);
        _botAt = at;
    }

    void Announce()
    {
        if (_announced || !_core.Over) return;
        _announced = true;
        _botAt = null;
        _deadline = null;
        var scores = new Dictionary<int, long>();
        foreach (var s in _core.Order)
            if (Ctx.Seated(s) && !IsBot(s)) scores[s] = _core.Gold[s];
        if (_core.Abandoned)
        {
            Ctx.Finish([], $"{Info.Title}: усі розійшлись — скарб лишився в землі", scores);
            return;
        }
        var w = _core.Winners;
        var rest = string.Join(", ", _core.Order.Where(s => !w.Contains(s)).Select(s => $"{Nick(s)} {_core.Gold[s]}"));
        var top = _core.Gold[w[0]];
        var text = w.Length == 1
            ? $"{Info.Title}: найбагатший — {Nick(w[0])}, {Bars(top)}"
            : $"{Info.Title}: найбагатші — {string.Join(", ", w.Select(Nick))}, по {Bars(top)}";
        if (rest.Length > 0) text += $" ({rest})";
        // Переможцем може бути місце Глека (як у Хуторах): решта людей програли, а не нічия.
        Ctx.Finish([.. w], text, scores, w.Length > 1 ? "🤝 Скарб поділили" : null);
    }

    /// <summary>Людина встала посеред партії — за неї сідає Глек; людей не лишилось — партія без переможця.</summary>
    public override void OnLeave(int seat)
    {
        if (_core.Over || seat < 0 || seat >= MaxSeats || !_core.In[seat] || IsBot(seat)) return;
        var nick = Nick(seat);
        var people = _core.Order.Any(s => s != seat && !IsBot(s) && Ctx.Seated(s));
        if (!people)
        {
            _core.Abandon();
            After();
            return;
        }
        _away[seat] = true;
        _ready.Remove(seat);
        _core.Note($"Глек сідає замість {nick}");
        Ctx.Log($"{Info.Title}: {nick} встає — за нього копає Глек");
        Glek(SkMoment.Away, Who(seat));
        After();
    }

    /// <summary>Хто встав посеред партії (чи випав і не встиг за Grace), сідає назад на своє місце — з тією ж роллю й рукою.</summary>
    public override int? Rejoin(string nick)
    {
        if (_core.Over) return null;
        foreach (var s in _core.Order)
            if (!IsBot(s) && !Ctx.Seated(s) && string.Equals(_nicks[s], nick, StringComparison.OrdinalIgnoreCase))
                return s;
        return null;
    }

    public override string? LateJoinGreeting(string nick) => "Твоя лопата на місці — Глек копав замість тебе";

    /// <summary>Людина повернулась на своє місце (<see cref="Rejoin"/>): Глек віддає лопату.</summary>
    public override void OnJoin(int seat)
    {
        if (_core.Over || seat < 0 || seat >= MaxSeats || !_core.In[seat] || IsBot(seat)) return;
        var nick = Nick(seat);
        _away[seat] = false;
        _missed[seat] = 0;
        _core.Note($"{nick} повертається до своєї лопати");
        Ctx.Log($"{Info.Title}: {nick} повертається до своєї лопати — Глек віддає кермо");
        Glek(SkMoment.Back, Who(seat));
        After();
    }

    // =====================================================================================
    // Глек-ведучий (§14.1)
    // =====================================================================================

    bool Glek(SkMoment m, Dictionary<string, string>? vars = null)
    {
        if (_glek.Say(m, Ctx.Clock.UtcNow, Ctx.Rng, vars) is not { } line) return false;
        Ctx.Say(line);
        return true;
    }

    Dictionary<string, string> Who(int seat) => new() { ["who"] = Nick(seat) };

    /// <summary>Пройти нові події ядра й озватися. Посеред раунду — лише публічне (ролей Глек не знає).</summary>
    void Voice()
    {
        foreach (var e in _core.Events.ToList())
        {
            if (e.Seq <= _heardSeq) continue;
            _heardSeq = e.Seq;
            if (e.K == "turn") { _glek.NewTurn(); continue; }
            if (e.K == "round") _closeSaid = false;
            if (SkarbLines.Hear(e, _core, Nick) is { } said) Glek(said.M, said.Vars);
        }
        // «Близько» — раз за раунд: фронт за ≤ 2 від закритої мітки.
        if (!_closeSaid && _core.Phase == SkPhase.Play)
        {
            var f = _core.Front();
            if (Enumerable.Range(0, 3).Any(g => !_core.GoalOpen[g] && f.Dist[g] <= 2)) _closeSaid = Glek(SkMoment.Close);
        }
    }

    // =====================================================================================
    // Вид (§5)
    // =====================================================================================

    static readonly object Grid = new
    {
        x0 = X0, x1 = X1, y0 = Y0, y1 = Y1, start = new[] { 0, 0 },
        goals = new[] { new[] { GoalX, GoalY[0] }, new[] { GoalX, GoalY[1] }, new[] { GoalX, GoalY[2] } },
        shaft = new[] { new[] { 0, -1 }, new[] { 0, -2 }, new[] { 0, -3 }, new[] { 0, -4 } },
    };

    static string Role(bool mole) => mole ? "mole" : "digger";

    public override object View(int? seat)
    {
        var c = _core;
        var started = c.Started;
        var me = started && seat is { } s0 && s0 >= 0 && s0 < MaxSeats && c.In[s0] && !IsBot(s0) ? s0 : -1;
        var play = c.Phase == SkPhase.Play;
        var lastEnd = !play && c.Ends.Count > 0 ? c.Ends[^1] : null;
        var r = c.Reach();
        var board = new List<object> { Card(c, SkarbCore.StartCell) };
        board.AddRange(c.Placed.Select(cell => Card(c, cell)));
        var linked = new List<int[]>();
        if (r[SkarbCore.StartCell] != 0) linked.Add([0, 0]);
        linked.AddRange(c.Placed.Where(cell => r[cell] != 0).Select(cell => new[] { CellX(cell), CellY(cell) }));
        linked.AddRange(Enumerable.Range(0, 3).Where(g => c.GoalOpen[g] && r[SkarbCore.GoalCells[g]] != 0)
            .Select(g => new[] { GoalX, GoalY[g] }));
        var players = c.Order.Select(s => new
        {
            seat = s,
            nick = Nick(s),
            bot = _bots[s],
            color = s,
            hand = c.Hand[s].Count,
            broken = Enumerable.Range(0, 3).Where(t => (c.Broken[s] >> t & 1) != 0).Select(t => Tools[t]).ToArray(),
            gold = c.Gold[s],
            role = lastEnd is null ? null : Role(lastEnd.Mole[s]),
            away = _away[s],
        }).ToArray();
        var goals = Enumerable.Range(0, 3).Select(g =>
        {
            var open = c.GoalOpen[g];
            var reveal = open || lastEnd is not null;
            var sh = reveal ? c.GoalShape(g) : null;
            return new
            {
                i = g, x = GoalX, y = GoalY[g], open,
                kind = reveal ? (g == c.GoldGoal ? "gold" : "stone") : null,
                s = sh?.S,
                r = open ? c.GoalR[g] : (int?)null,
                m = open ? Mask(sh!, c.GoalR[g]) : (int?)null,
                c = open ? true : (bool?)null,
            };
        }).ToArray();
        var over = c.Over;
        var events = c.Events.Skip(Math.Max(0, c.Events.Count - 40)).Select(e => e.For(me >= 0 ? me : null)).ToArray();
        return new
        {
            v = 1,
            grid = Grid,
            phase = !started ? null : c.Phase switch { SkPhase.Play => "play", SkPhase.Round => "round", _ => "over" },
            round = c.Round,
            rounds = Rounds,
            pack = new { moles = c.MolesInDeck, diggers = c.DiggersInDeck },
            players,
            order = (int[])c.Order.Clone(),
            first = started ? c.First : (int?)null,
            turn = started && play ? c.Turn : (int?)null,
            board,
            linked,
            goals,
            deck = c.Deck.Count,
            goldLeft = c.GoldDeck.Count,
            roundEnd = lastEnd is null ? null : RoundEnd(lastEnd),
            nextAt = c.Phase == SkPhase.Round ? _nextAt : null,
            ready = c.Phase == SkPhase.Round ? _ready.Order().ToArray() : [],
            events,
            seq = c.Seq,
            deadline = play ? _deadline : null,
            timerMs = play && _deadline is not null ? _deadlineMs : (int?)null,
            bots = _bots.Any(x => x is not null) ? (string?[])_bots.Clone() : null,
            botIn = _botAt is { } at && play ? Math.Max(0, (int)(at - Ctx.Clock.UtcNow).TotalMilliseconds) : (int?)null,
            log = c.Log.Skip(Math.Max(0, c.Log.Count - 12)).ToArray(),
            over = over ? Over() : null,
            me = me >= 0 ? Me(me) : null,
        };
    }

    static object Card(SkarbCore c, int cell) => new
    {
        x = CellX(cell),
        y = CellY(cell),
        s = c.CellS[cell],
        r = c.CellR[cell],
        m = c.CellM[cell],
        c = c.CellC[cell],
        by = c.CellBy[cell] >= 0 ? c.CellBy[cell] : (int?)null,
        id = c.CellCard[cell] >= 0 ? c.CellCard[cell] : (int?)null,
    };

    object RoundEnd(SkRoundEnd e) => new
    {
        n = e.N,
        side = e.Side,
        finisher = e.Finisher,
        roles = _core.Order.ToDictionary(s => s.ToString(), s => Role(e.Mole[s])),
        aside = Role(e.AsideMole),
        goals = Enumerable.Range(0, 3).Select(g => new { i = g, kind = g == e.GoldGoal ? "gold" : "stone" }).ToArray(),
        gold = e.Gold.Select(x => new { seat = x.Seat, n = x.N, cards = x.Cards is null ? null : (int[])x.Cards.Clone() }).ToArray(),
        totals = (int[])e.Totals.Clone(),
    };

    static object HandCard(int id)
    {
        var card = SkarbCore.CardOf(id);
        return card.K switch
        {
            SkKind.Path => new { id, k = "path", s = card.Shape!.S },
            SkKind.Break => new { id, k = "break", tool = Tools[FirstTool(card.Tools)] },
            SkKind.Fix => new { id, k = "fix", tools = Enumerable.Range(0, 3).Where(t => (card.Tools >> t & 1) != 0).Select(t => Tools[t]).ToArray() },
            SkKind.Map => (object)new { id, k = "map" },
            _ => new { id, k = "rockfall" },
        };
    }

    object Me(int s)
    {
        var c = _core;
        var must = c.Phase == SkPhase.Play && c.Turn == s ? "play"
            : c.Phase == SkPhase.Round && !_ready.Contains(s) ? "next"
            : null;
        Dictionary<string, object>? legal = null;
        Dictionary<string, string>? why = null;
        object? tip = null;
        if (must == "play")
        {
            legal = [];
            why = [];
            var list = new List<SkMove>();
            foreach (var id in c.Hand[s].Distinct())
            {
                list.Clear();
                c.LegalFor(s, id, list);
                var kind = SkarbCore.CardOf(id).K;
                legal[id.ToString()] = kind switch
                {
                    SkKind.Path => list.Select(m => new[] { m.X, m.Y, m.R }).ToArray(),
                    SkKind.Break => list.Select(m => m.Seat).ToArray(),
                    SkKind.Fix => list.Select(m => new object[] { m.Seat, Tools[m.Tool] }).ToArray(),
                    SkKind.Map => list.Select(m => m.Goal).ToArray(),
                    _ => (object)list.Select(m => new[] { m.X, m.Y }).ToArray(),
                };
                if (list.Count == 0) why[id.ToString()] = c.WhyNot(s, id);
            }
            // «💡 Глек радить»: що зробив би Глек на моєму місці з моєю роллю й лише з того, що знаю я.
            var mv = _brain.Decide(c, s, new Random(unchecked((int)(c.Seq * 31 + s))), oracle: false);
            tip = mv.Pass ? new { card = mv.Card, pass = true }
                : SkarbCore.CardOf(mv.Card).K switch
                {
                    SkKind.Path => new { card = mv.Card, x = mv.X, y = mv.Y, r = mv.R },
                    SkKind.Break => new { card = mv.Card, seat = mv.Seat },
                    SkKind.Fix => new { card = mv.Card, seat = mv.Seat, tool = Tools[mv.Tool] },
                    SkKind.Map => new { card = mv.Card, goal = mv.Goal },
                    _ => (object)new { card = mv.Card, x = mv.X, y = mv.Y },
                };
        }
        var peeks = new Dictionary<string, string>();
        for (var g = 0; g < 3; g++)
            if (c.Peek[s][g] != 0) peeks[g.ToString()] = c.Peek[s][g] == 2 ? "gold" : "stone";
        return new
        {
            seat = s,
            role = Role(c.Mole[s]),
            allies = c.Allies && c.Mole[s] ? c.Order.Where(o => o != s && c.Mole[o]).ToArray() : null,
            hand = c.Hand[s].Select(HandCard).ToArray(),
            peeks,
            must,
            legal,
            why,
            tip,
        };
    }

    object Over()
    {
        var c = _core;
        return new
        {
            winners = (int[])c.Winners.Clone(),
            reason = c.Abandoned ? "left" : "gold",
            gold = c.Order.Select(s => c.Gold[s]).ToArray(),
            rounds = c.Ends.Select(RoundEnd).ToArray(),
            stats = new
            {
                turns = c.Turns,
                ms = (long)(Ctx.Clock.UtcNow - _startedAt).TotalMilliseconds,
                timeline = c.Ends.Select(e => (int[])e.Totals.Clone()).ToArray(),
                seats = c.Order.Select(s => new
                {
                    seat = s, paths = c.Paths[s], deadEnds = c.DeadEnds[s], breaks = c.Breaks[s], fixes = c.Fixes[s],
                    maps = c.Maps[s], rockfalls = c.Rockfalls[s], passes = c.Passes[s], moleRounds = c.MoleRounds[s],
                    won = c.Won[s], moleGold = c.MoleGoldGot[s], finished = c.Finished[s],
                }).ToArray(),
            },
        };
    }

    // =====================================================================================
    // Розбір payload
    // =====================================================================================

    static int? Int(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var n)
            ? n : null;

    static string? Str(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
}
