using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Хутори» — Катан по-нашому на 2–4 (контракт <c>docs/games/specs/khutory.md</c>). Правила — у <see cref="KhutoryCore"/>,
/// Глеки — <see cref="KhutoryBot"/>, автохід за того, хто проспав, — <see cref="KhutoryAuto"/>; тут каркас: опції, розсадка з Глеками, таймер ходу, вихід і вид.
/// Гра прихована (<c>Hidden</c>): рука й грамоти кожного їдуть лише йому.
/// </summary>
public sealed class Khutory : Game
{
    public override GameInfo Info { get; } = new(
        "khutory", "Хутори", "Хутори", GameGroup.Board, 1, KhutoryCore.MaxSeats, Start: StartMode.ByHost, Hidden: true,
        Options:
        [
            new GameOption("vp", "Грати до", [("8", "8 очок · ~40 хв"), ("10", "10 очок · класика"), ("12", "12 очок · довга")], "8"),
            new GameOption("map", "Карта", [("random", "випадкова, рівна"), ("starter", "для новачків (завжди та сама)")], "random"),
            new GameOption("friendly", "Кармелюк", [("1", "дружній: не грабує тих, у кого ≤ 2 очки"), ("0", "як у книжці")], "1"),
            new GameOption("timer", "Час на хід", [("none", "без часу"), ("60", "60 с"), ("90", "90 с"), ("180", "180 с")], "90"),
            BoardBots.Option(3),
        ],
        Hint: "Хутори — Катан по-нашому на 2–4: став хати на родючі землі, торгуйся з сусідами, будуй Чумацький шлях. "
            + "Бракує людей — підсяде Глек 🤖");

    static readonly string[] Colors = ["червоні", "сині", "помаранчеві", "фіолетові"];
    /// <summary>Решта фаз (крім main) — не довше за 40 с.</summary>
    const int ShortMs = 40_000;

    int _target = 8;
    bool _starter;
    bool _friendly = true;
    int _timerMs = 90_000;
    int _botsWanted;

    KhutoryCore _core = new(KhutoryBoard.Starter(), [], new Random(0));
    string?[] _bots = new string?[KhutoryCore.MaxSeats];
    string?[] _nicks = new string?[KhutoryCore.MaxSeats];
    readonly bool[] _away = new bool[KhutoryCore.MaxSeats];
    readonly int[] _missed = new int[KhutoryCore.MaxSeats];
    DateTimeOffset? _deadline;
    int _deadlineMs;
    string _deadlineKey = "";
    /// <summary>Строк main — один на хід: Кармелюк (козак) чи Толока посеред ходу його не поновлюють.</summary>
    string _mainKey = "";
    DateTimeOffset? _mainDeadline;
    DateTimeOffset? _botAt;
    /// <summary>Розумний Глек (пам'ять його пропозицій — на партію).</summary>
    KhutoryBot _brain = new();
    bool _announced;
    string _reason = "points";

    /// <summary>Правила поточної партії (тести й діагностика).</summary>
    public KhutoryCore Core => _core;

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _target = options.TryGetValue("vp", out var vp) && vp is "8" or "10" or "12" ? int.Parse(vp) : 8;
        _starter = options.TryGetValue("map", out var map) && map == "starter";
        _friendly = !(options.TryGetValue("friendly", out var f) && f == "0");
        _timerMs = options.TryGetValue("timer", out var t)
            ? t switch { "none" => 0, "60" => 60_000, "180" => 180_000, _ => 90_000 }
            : 90_000;
        _botsWanted = BoardBots.Read(options, 3);
    }

    public override string? CanStart()
    {
        var humans = BoardBots.Humans(Ctx, KhutoryCore.MaxSeats);
        return humans + Math.Min(_botsWanted, KhutoryCore.MaxSeats - humans) >= 2 ? null
            : "Самому нема з ким: хай хтось сяде — або відкрий стіл з «🤖 Глек підсідає»";
    }

    public override string SeatName(int seat) => seat >= 0 && seat < Colors.Length ? Colors[seat] : $"гравець {seat + 1}";

    bool IsBot(int seat) => seat >= 0 && seat < _bots.Length && _bots[seat] is not null;
    public override string? SeatBot(int seat) => IsBot(seat) ? _bots[seat] : null;
    /// <summary>За місце ходить Глек: справжній бот або людина, що вийшла чи проспала двічі.</summary>
    bool Auto(int seat) => IsBot(seat) || _away[seat];

    public override void Start()
    {
        _bots = BoardBots.Seat(Ctx, KhutoryCore.MaxSeats, _botsWanted);
        var seats = Enumerable.Range(0, KhutoryCore.MaxSeats).Where(s => Ctx.Seated(s) || _bots[s] is not null).ToArray();
        _nicks = [.. Enumerable.Range(0, KhutoryCore.MaxSeats).Select(s => _bots[s] ?? Ctx.NickOf(s))];
        Array.Clear(_away);
        Array.Clear(_missed);
        var board = _starter ? KhutoryBoard.Starter() : KhutoryBoard.Random(Ctx.Rng);
        // seq подій — наскрізний через усі партії столу: клієнт тримає найбільший побачений і анімує лише новіші.
        var seq = _core.Seq;
        _core = new KhutoryCore(board, seats, Ctx.Rng) { Target = _target, Friendly = _friendly, NameOf = Nick, Seq = seq };
        _brain = new KhutoryBot();
        _announced = false;
        _reason = "points";
        _deadlineKey = "";
        _deadline = null;
        _mainKey = "";
        _mainDeadline = null;
        _core.Begin(seats[Ctx.Rng.Next(seats.Length)]);
        After();
    }

    string Nick(int seat) => _nicks[seat] ?? Ctx.NickOf(seat) ?? SeatName(seat);

    // =====================================================================================
    // Дії
    // =====================================================================================

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action == BoardBots.Nudge) return BotTurn();
        if (action == "timeout") return Timeout();
        if (_core.Over) return ActResult.Fail("Партія вже скінчилась");
        if (seat < 0 || seat >= KhutoryCore.MaxSeats || !_core.In[seat]) return ActResult.Fail("Ти не граєш за цим столом");
        if (IsBot(seat)) return ActResult.Fail("Тут грає Глек");
        var error = Do(seat, action, payload);
        if (error is not null) return ActResult.Fail(error);
        // Будь-яка своя дія повертає людину з-під опіки Глека.
        if (_away[seat]) _core.Note($"{Nick(seat)} знову грає сам");
        _away[seat] = false;
        _missed[seat] = 0;
        After();
        return ActResult.Done;
    }

    string? Do(int seat, string action, JsonElement p)
    {
        switch (action)
        {
            case "town": return Int(p, "v") is { } v ? _core.PlaceTown(seat, v) : "Не зрозумів, куди ставити";
            case "road": return Int(p, "e") is { } e ? _core.PlaceRoad(seat, e) : "Не зрозумів, куди класти";
            case "city": return Int(p, "v") is { } c ? _core.PlaceCity(seat, c) : "Не зрозумів, куди ставити";
            case "roll": return _core.Roll(seat);
            case "buy": return _core.Buy(seat);
            case "play":
                {
                    var k = Array.IndexOf(KhutoryCore.DevKinds, Str(p, "k"));
                    if (k < 0) return "Не зрозумів, яку грамоту";
                    int r1 = -1, r2 = -1;
                    if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("res", out var res))
                    {
                        if (res.ValueKind == JsonValueKind.String) r1 = ResIdx(res.GetString());
                        else if (res.ValueKind == JsonValueKind.Array)
                        {
                            var list = res.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? ResIdx(x.GetString()) : -1).ToList();
                            if (list.Count > 0) r1 = list[0];
                            if (list.Count > 1) r2 = list[1];
                        }
                    }
                    return _core.Play(seat, k, r1, r2);
                }
            case "discard": return Res(p, "res") is { } d ? _core.Discard(seat, d) : "Не зрозумів, що скинути";
            case "robber": return Int(p, "h") is { } h ? _core.MoveRobber(seat, h) : "Не зрозумів, куди Кармелюка";
            case "steal": return Int(p, "seat") is { } v2 ? _core.Steal(seat, v2) : "Не зрозумів, у кого";
            case "bank":
                {
                    var give = ResIdx(Str(p, "give"));
                    var get = ResIdx(Str(p, "get"));
                    return give < 0 || get < 0 ? "Не зрозумів, що на що міняти" : _core.BankTrade(seat, give, get, Int(p, "n") ?? 1);
                }
            case "offer":
                {
                    if (Res(p, "give") is not { } give || Res(p, "get") is not { } get) return "Не зрозумів пропозицію";
                    return _core.Offer(seat, give, get, Int(p, "to"));
                }
            case "answer":
                {
                    if (Int(p, "id") is not { } id) return "Не зрозумів, на яку пропозицію";
                    var ok = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
                    return _core.Answer(seat, id, ok);
                }
            case "deal":
                return Int(p, "id") is { } did && Int(p, "seat") is { } with ? _core.Deal(seat, did, with) : "Не зрозумів, з ким міняти";
            case "cancel": return Int(p, "id") is { } cid ? _core.Cancel(seat, cid) : "Не зрозумів, яку пропозицію";
            case "end": return _core.End(seat);
            default: return "Тут так не ходять";
        }
    }

    /// <summary>
    /// Хід Глека (штовхає клієнт людини): один крок першого, на кого чекає стіл, від поточного. Глек, що чекає
    /// відповідей на свою пропозицію, пропускає — тим часом відповідають інші Глеки.
    /// </summary>
    ActResult BotTurn()
    {
        if (_core.Over || _botAt is not { } at || Ctx.Clock.UtcNow < at) return ActResult.Fail("Глек ще думає");
        foreach (var s in Waiting())
        {
            if (!Auto(s)) continue;
            // Людину, що відійшла, веде той самий Глек, але без власних пропозицій.
            var err = _brain.Step(_core, s, Ctx.Rng, Ctx.Clock.UtcNow, trade: IsBot(s));
            if (err == KhutoryBot.Wait) continue;
            // Крок, що не вдався (не мав би), — завершити хід, щоб стіл не завис.
            if (err is not null && _core.Phase is KhPhase.Main or KhPhase.Road2 && _core.Turn == s) _core.ForceEnd(s);
            After();
            return ActResult.Done;
        }
        Arm();
        return ActResult.Fail(_botAt is null ? "Глекам нема що робити" : "Глек чекає відповідей");
    }

    /// <summary>Місця, від яких стіл чогось чекає, починаючи з поточного.</summary>
    IEnumerable<int> Waiting()
    {
        for (var i = 0; i < KhutoryCore.MaxSeats; i++)
        {
            var s = (_core.Turn + i) % KhutoryCore.MaxSeats;
            if (_core.MayAct(s)) yield return s;
        }
    }

    /// <summary>
    /// Минув строк: за того, хто тягне, ходить автохід — розстановка, кидок, скидання, Кармелюк; у main/road2 хід
    /// просто завершується. Двічі поспіль проспав — далі за нього грає Глек.
    /// </summary>
    ActResult Timeout()
    {
        if (_core.Over) return ActResult.Fail("Партія вже скінчилась");
        if (_deadline is not { } dl) return ActResult.Fail("Тут без часу");
        if (Ctx.Clock.UtcNow < dl) return ActResult.Fail("Ще є час");
        var sleepers = _core.Phase == KhPhase.Discard
            ? Enumerable.Range(0, KhutoryCore.MaxSeats).Where(s => _core.DiscardNeed[s] > 0).ToList()
            : [_core.Turn];
        foreach (var s in sleepers)
        {
            switch (_core.Phase)
            {
                case KhPhase.Setup:
                    var idx = _core.SetupIdx;
                    for (var i = 0; i < 2 && !_core.Over && _core.Phase == KhPhase.Setup && _core.SetupIdx == idx; i++)
                        KhutoryAuto.Step(_core, s, Ctx.Rng);
                    break;
                case KhPhase.Roll:
                    _core.Roll(s);
                    break;
                case KhPhase.Discard:
                    KhutoryAuto.Step(_core, s, Ctx.Rng);
                    break;
                case KhPhase.Robber:
                case KhPhase.Steal:
                    for (var i = 0; i < 2 && _core.Phase is KhPhase.Robber or KhPhase.Steal && _core.Turn == s; i++)
                        KhutoryAuto.Step(_core, s, Ctx.Rng);
                    break;
                default:
                    _core.ForceEnd(s);
                    break;
            }
            if (!Auto(s) && ++_missed[s] >= 2)
            {
                _away[s] = true;
                _core.Note($"{Nick(s)} задрімав — Глек грає замість нього");
            }
        }
        _deadlineKey = "";   // строк — наново, навіть якщо фаза та сама
        After();
        return ActResult.Done;
    }

    /// <summary>Після кожної зміни: журнал кімнати, кінець партії, строк, Глеки.</summary>
    void After()
    {
        foreach (var line in _core.Journal) Ctx.Log($"{Info.Title}: {line}");
        _core.Journal.Clear();
        Announce();
        Rearm();
        Arm();
    }

    void Rearm()
    {
        if (_core.Over || _timerMs <= 0) { _deadline = null; _deadlineMs = 0; _deadlineKey = ""; return; }
        var key = _core.Phase == KhPhase.Discard
            ? "discard"
            : $"{_core.Phase}|{_core.Turn}|{_core.SetupIdx}|{_core.SetupRoad}";
        if (key == _deadlineKey) return;
        _deadlineKey = key;
        if (_core.Phase == KhPhase.Main)
        {
            var main = $"{_core.Turn}|{_core.Rounds}|{_core.TurnIdx}";
            _deadlineMs = _timerMs;
            if (main == _mainKey && _mainDeadline is { } md) { _deadline = md; return; }
            _mainKey = main;
            _deadline = _mainDeadline = Ctx.Clock.UtcNow.AddMilliseconds(_timerMs);
            return;
        }
        _deadlineMs = Math.Min(_timerMs, ShortMs);
        _deadline = Ctx.Clock.UtcNow.AddMilliseconds(_deadlineMs);
    }

    void Arm()
    {
        _botAt = null;
        if (_core.Over) return;
        // Глек, що чекає відповідей на свою пропозицію, — до кінця очікування (хтось відповів — After перерахує).
        var now = Ctx.Clock.UtcNow;
        foreach (var s in Waiting())
        {
            if (!Auto(s)) continue;
            var at = _brain.WaitUntil(_core, s, now) ?? now.AddMilliseconds(BoardBots.ThinkMs);
            if (_botAt is null || at < _botAt) _botAt = at;
        }
    }

    void Announce()
    {
        if (_announced || !_core.Over) return;
        _announced = true;
        _botAt = null;
        _deadline = null;
        var scores = new Dictionary<int, long>();
        for (var s = 0; s < KhutoryCore.MaxSeats; s++)
            if (_core.In[s] && Ctx.Seated(s) && !IsBot(s)) scores[s] = _core.Points(s);
        if (_core.Winner is not { } w)
        {
            Ctx.Finish([], $"{Info.Title}: партію покинули — переможця нема", scores);
            return;
        }
        // Переможець — місце навіть тоді, коли там Глек (бот чи за людину, що вийшла): решта людей програли, а не нічия.
        // Каркас це вміє: виплата ставок і гімн шукають нік за місцем і порожнє/Глекове пропускають (як у Доміно).
        int[] winners = [w];
        var rest = string.Join(", ", _core.Order.Where(s => s != w).Select(s => $"{Nick(s)} {_core.Points(s)}"));
        Ctx.Finish(winners, $"{Info.Title}: перемагає {Nick(w)} — {_core.Points(w)} очок ({rest})", scores);
    }

    /// <summary>Людина встала посеред партії — за неї сідає Глек; людей не лишилось — партія без переможця.</summary>
    public override void OnLeave(int seat)
    {
        if (_core.Over || seat < 0 || seat >= KhutoryCore.MaxSeats || !_core.In[seat] || IsBot(seat)) return;
        var nick = Nick(seat);
        var people = Enumerable.Range(0, KhutoryCore.MaxSeats)
            .Any(s => s != seat && _core.In[s] && !IsBot(s) && Ctx.Seated(s));
        if (!people)
        {
            _reason = "left";
            _core.Abandon();
            After();
            return;
        }
        _away[seat] = true;
        _core.Note($"Глек сідає замість {nick}");
        Ctx.Log($"{Info.Title}: {nick} встає — за нього грає Глек");
        After();
    }

    // =====================================================================================
    // Вид
    // =====================================================================================

    public override object View(int? seat)
    {
        var c = _core;
        var me = seat is { } s0 && s0 >= 0 && s0 < KhutoryCore.MaxSeats && c.In[s0] && !IsBot(s0) ? s0 : -1;
        var g = KhutoryGeo.I;
        var b = c.B;
        var board = new
        {
            id = b.Id,
            hexes = Enumerable.Range(0, KhutoryGeo.Hexes).Select(h => new
            {
                q = g.Q[h], r = g.R[h], t = KhutoryBoard.Terrains[b.T[h]], num = b.Num[h] == 0 ? (int?)null : b.Num[h],
                v = (int[])g.HexV[h].Clone(),
            }).ToArray(),
            vx = g.Vx.Select(p => (double[])p.Clone()).ToArray(),
            edges = g.Edges.Select(e => (int[])e.Clone()).ToArray(),
            ports = Enumerable.Range(0, b.PortEdge.Length).Select(i => new
            {
                e = b.PortEdge[i],
                kind = b.PortKind[i] == KhutoryBoard.AnyPort ? "any" : KhutoryBoard.Res[b.PortKind[i]],
                rate = b.PortKind[i] == KhutoryBoard.AnyPort ? 3 : 2,
            }).ToArray(),
        };
        var players = c.Order.Order().Select(s => new
        {
            seat = s,
            nick = Nick(s),
            bot = _bots[s],
            color = s,
            cards = c.Total(s),
            dev = c.Devs[s].Count,
            knights = c.Knights[s],
            road = c.RoadLen[s],
            points = c.PublicPoints(s),
            left = new { road = c.RoadsLeft(s), town = c.TownsLeft(s), city = c.CitiesLeft(s) },
            away = _away[s],
        }).ToArray();
        var over = c.Over;
        var events = c.Events.Skip(Math.Max(0, c.Events.Count - 30)).Select(e => e.For(me >= 0 ? me : null)).ToArray();
        return new
        {
            v = 1,
            board,
            players,
            order = (int[])c.Order.Clone(),
            turn = over ? (int?)null : c.Turn,
            phase = c.PhaseName,
            setup = c.Phase == KhPhase.Setup ? new { step = c.SetupRoad ? "road" : "town", round = c.SetupIdx < c.Seats ? 1 : 2 } : null,
            dice = c.Dice is { } d ? (int[])d.Clone() : null,
            rolled = c.Rolled,
            robber = c.Robber,
            towns = Enumerable.Range(0, KhutoryGeo.Verts).Where(v => c.VOwner[v] >= 0)
                .Select(v => new { v, seat = c.VOwner[v], city = c.VCity[v] }).ToArray(),
            roads = Enumerable.Range(0, KhutoryGeo.EdgeCount).Where(e => c.EOwner[e] >= 0)
                .Select(e => new { e, seat = c.EOwner[e] }).ToArray(),
            bank = KhutoryCore.ResMap5(c.Bank),
            devLeft = c.Deck.Count,
            longest = c.Longest is { } l ? new { seat = l, len = c.RoadLen[l] } : null,
            army = c.Army is { } a ? new { seat = a, n = c.Knights[a] } : null,
            target = c.Target,
            discard = c.Phase == KhPhase.Discard
                ? Enumerable.Range(0, KhutoryCore.MaxSeats).Where(x => c.DiscardNeed[x] > 0)
                    .ToDictionary(x => x.ToString(), x => c.DiscardNeed[x])
                : null,
            offers = c.Offers.Select(o => new
            {
                id = o.Id, from = o.From, to = o.To, give = KhutoryCore.ResMap(o.Give), get = KhutoryCore.ResMap(o.Get),
                acc = o.Acc.ToArray(), dec = o.Dec.ToArray(),
            }).ToArray(),
            events,
            seq = c.Seq,
            deadline = over ? null : _deadline,
            timerMs = _deadline is null ? (int?)null : _deadlineMs,
            bots = _bots.Any(x => x is not null) ? (string?[])_bots.Clone() : null,
            botIn = _botAt is { } at && !over ? Math.Max(0, (int)(at - Ctx.Clock.UtcNow).TotalMilliseconds) : (int?)null,
            log = c.Log.Skip(Math.Max(0, c.Log.Count - 12)).ToArray(),
            over = over ? Over() : null,
            me = me >= 0 ? Me(me) : null,
        };
    }

    object Me(int s)
    {
        var c = _core;
        var must = c.Must(s);
        var (can, why) = c.CanWhy(s);
        object? spots = null;
        if (!c.Over && s == c.Turn)
        {
            if (c.Phase == KhPhase.Setup)
                spots = new { town = c.SetupRoad ? Array.Empty<int>() : c.TownSpots(s).ToArray(), road = c.SetupRoad ? c.RoadSpots(s).ToArray() : Array.Empty<int>(), city = Array.Empty<int>() };
            else if (c.Phase == KhPhase.Main)
                spots = new { town = c.TownSpots(s).ToArray(), road = c.RoadSpots(s).ToArray(), city = c.CitySpots(s).ToArray() };
            else if (c.Phase == KhPhase.Road2)
                spots = new { town = Array.Empty<int>(), road = c.RoadSpots(s).ToArray(), city = Array.Empty<int>() };
        }
        return new
        {
            seat = s,
            hand = KhutoryCore.ResMap5(c.Hand[s]),
            dev = c.Devs[s].Select(d => new { k = KhutoryCore.DevKinds[d.K], fresh = d.Fresh }).ToArray(),
            points = c.Points(s),
            playedDev = c.PlayedDev && s == c.Turn,
            rates = KhutoryCore.ResMap5(c.Rates(s)),
            must,
            spots,
            can,
            why,
            discard = c.Phase == KhPhase.Discard ? c.DiscardNeed[s] : 0,
            robber = must == "robber" ? c.RobberSpots(s).ToArray() : null,
            steal = must == "steal" ? c.StealFrom.ToArray() : null,
        };
    }

    object Over()
    {
        var c = _core;
        var width = c.Order.Max() + 1;
        return new
        {
            winner = c.Winner,
            reason = c.Abandoned ? "left" : _reason,
            points = Enumerable.Range(0, width).Select(s => c.In[s] ? c.Points(s) : 0).ToArray(),
            stats = new
            {
                dice = (int[])c.DiceCount.Clone(),
                sevens = c.Sevens,
                rounds = c.Rounds,
                timeline = c.Timeline.Select(r => (int[])r.Clone()).ToArray(),
                seats = c.Order.Order().Select(s => new
                {
                    seat = s, trades = c.Trades[s], got = c.Got[s], stolen = c.Stolen[s],
                    built = new { road = c.Built[s][0], town = c.Built[s][1], city = c.Built[s][2] },
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

    static int ResIdx(string? name) => name is null ? -1 : Array.IndexOf(KhutoryBoard.Res, name);

    /// <summary>{ "wood": 2, "ore": 1 } → масив на 5; null — щось не так (невідомий ресурс, від'ємне, не число).</summary>
    static int[]? Res(JsonElement p, string name)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var x) || x.ValueKind != JsonValueKind.Object) return null;
        var a = new int[5];
        foreach (var prop in x.EnumerateObject())
        {
            var r = ResIdx(prop.Name);
            if (r < 0 || prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt32(out var n) || n < 0 || n > 19) return null;
            a[r] = n;
        }
        return a;
    }
}
