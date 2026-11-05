using System.Text.Json;
using static Hlechyky.Games.Impl.SolyanyiMap;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Соляний шлях» — настільна гра на 2–5 «прокладай шляхи за замовленнями» (контракт <c>docs/games/specs/solyanyi.md</c>).
/// Мапа — <see cref="SolyanyiMap"/>, правила — <see cref="SolyanyiCore"/>, Глеки — <see cref="SolyanyiBot"/>, голос ведучого —
/// <see cref="SolyanyiLines"/>; тут каркас, як у «Сорочинцях»: опції, розсадка з Глеками, таймер ходу, вихід, пауза й
/// повернення, вид. Гра прихована (<c>Hidden</c>): рука й замовлення їдуть лише власнику.
/// </summary>
public sealed partial class Solyanyi : Game
{
    public override GameInfo Info { get; } = new(
        "solyanyi", "Соляний шлях", "Соляний шлях", GameGroup.Board, 1, MaxSeats, Start: StartMode.ByHost,
        Hidden: true, Rated: false, Score: ScoreOrder.None,
        Options:
        [
            BoardBots.Option(4),
            new GameOption("wagons", "Возів у кожного", [("45", "45 🛞 · звичайна"), ("35", "35 🛞 · коротша")], "45"),
            new GameOption("timer", "Час на хід", [("none", "без часу"), ("45", "45 с"), ("60", "60 с"), ("90", "90 с")], "60"),
        ],
        Hint: "Соляний шлях — на 2–5: збирай карти вантажів, займай шляхи чумацькими возами й довези замовлення з міста в місто, "
            + "поки вози не скінчились. Бракує людей — підсяде Глек 🤖");

    static readonly string[] Colors = ["червоні", "сині", "зелені", "помаранчеві", "фіолетові"];

    /// <summary>Скільки задрімала людина має на свій хід сама, перш ніж Глек зіграє за неї.</summary>
    public const int AwayGraceMs = 5000;
    /// <summary>Фази draw і keep — щонайменше стільки від входу у фазу (не довше за таймер).</summary>
    public const int BackMs = 15_000;
    /// <summary>Фаза setup — стільки або таймер, що більше.</summary>
    public const int SetupMs = 60_000;

    /// <summary>Людей не лишилось (відпав зв'язок) — партія стоїть на паузі 10 хв (як у «Хуторах»).</summary>
    public override TimeSpan HoldEmpty => TimeSpan.FromMinutes(10);

    /// <summary>Останній встав кнопкою — пішов: стіл зникає одразу (пауза лише на обрив зв'язку).</summary>
    public override bool HoldAfterLeave => false;

    int _wagons = 45;
    int _timerMs = 60_000;
    int _botsWanted;

    SolyanyiCore _core = new([]);
    string?[] _bots = new string?[MaxSeats];
    string?[] _nicks = new string?[MaxSeats];
    readonly bool[] _away = new bool[MaxSeats];
    readonly int[] _missed = new int[MaxSeats];

    // час
    string _turnKey = "", _phaseKey = "";
    DateTimeOffset _turnAt, _phaseAt, _startedAt;
    DateTimeOffset? _deadline, _botAt;
    int _deadlineMs;
    bool _announced;

    // голос
    readonly SolyanyiGlek _glek = new();
    readonly SlHeard _heard = new();
    long _heardSeq;

    // порада (§5: вид шлють часто, рахувати її щоразу ні до чого)
    readonly (string Key, object? Tip)[] _tips = new (string, object?)[MaxSeats];

    /// <summary>Правила поточної партії (тести й діагностика).</summary>
    public SolyanyiCore Core => _core;

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _wagons = options.TryGetValue("wagons", out var w) && w == "35" ? 35 : 45;
        _timerMs = options.TryGetValue("timer", out var t)
            ? t switch { "none" => 0, "45" => 45_000, "90" => 90_000, _ => 60_000 }
            : 60_000;
        _botsWanted = BoardBots.Read(options, 4);
    }

    public override string? CanStart()
    {
        var humans = BoardBots.Humans(Ctx, MaxSeats);
        return humans + Math.Min(_botsWanted, MaxSeats - humans) >= 2 ? null
            : "Самому не доїхати: хай хтось сяде — або додай 🤖 Глека";
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
        // seq подій (і лічильники подій «лише власнику») — наскрізні через усі партії столу: клієнт тримає найбільші побачені й анімує лише новіші.
        var seq = _core.Seq;
        _core = new SolyanyiCore(seats) { Wagons0 = _wagons, NameOf = Nick, Seq = seq, OwnSeq = _core.OwnSeq };
        _announced = false;
        _turnKey = _phaseKey = "";
        _deadline = _botAt = null;
        _deadlineMs = 0;
        _heardSeq = seq;
        _heard.Clear();
        _startedAt = Ctx.Clock.UtcNow;
        _glek.Reset();
        Array.Clear(_tips);
        // Порядок Ctx.Rng — як у контракті §2.1: колода, замовлення, перший гравець; голос — після.
        _core.Begin(Ctx.Rng);
        Glek(SlMoment.Start);
        // Справжні Глеки обирають замовлення одразу, за порядком ходів.
        foreach (var s in _core.FromFirst().ToArray())
            if (IsBot(s)) SolyanyiBot.Step(_core, s);
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
        var error = action switch
        {
            "draw" => _core.Draw(seat, Int(payload, "slot"), True(payload, "deck"), String(payload, "card")),
            "claim" => _core.Claim(seat, Int(payload, "route"), String(payload, "c"), Int(payload, "w")),
            "tickets" => _core.TakeTickets(seat),
            "keep" => _core.Keep(seat, Ints(payload, "ids")),
            "pass" => _core.Pass(seat),
            _ => "Тут так не ходять",
        };
        if (error is not null) return ActResult.Fail(error);
        // Будь-яка своя дія повертає людину з-під опіки Глека.
        if (_away[seat])
        {
            _core.Note($"{Nick(seat)} знову веде валку сам");
            Glek(SlMoment.Back, Who(seat));
        }
        _away[seat] = false;
        _missed[seat] = 0;
        After();
        return ActResult.Done;
    }

    /// <summary>Хід Глека (штовхає клієнт людини): увесь хід того, чия черга, — два взяття, шлях або замовлення з вибором.</summary>
    ActResult BotTurn()
    {
        if (_botAt is not { } at) return ActResult.Fail("Глекам нема що робити");
        if (Ctx.Clock.UtcNow < at) return ActResult.Fail("Глек ще думає");
        var s = _core.Turn;
        if (!Auto(s)) return ActResult.Fail("Глекам нема що робити");
        SolyanyiBot.Step(_core, s);
        After();
        return ActResult.Done;
    }

    /// <summary>
    /// Минув строк: за того, хто тягне, доробляє Глек (дія, друге взяття, вибір; у setup — вибір за кожного, хто ще не обрав).
    /// Двічі поспіль проспав — далі за нього Глек.
    /// </summary>
    ActResult Timeout()
    {
        if (_timerMs <= 0) return ActResult.Fail("Тут без часу");
        if (_deadline is not { } dl || Ctx.Clock.UtcNow < dl) return ActResult.Fail("Ще є час");
        if (_core.Phase == SlPhase.Setup)
            foreach (var s in _core.FromFirst().ToArray())
            {
                if (!_core.Ready[s]) Doze(s);
            }
        else Doze(_core.Turn);
        After();
        return ActResult.Done;
    }

    void Doze(int s)
    {
        var human = !Auto(s);
        SolyanyiBot.Step(_core, s);
        if (!human) return;
        _core.Note($"{Nick(s)} задрімав — Глек їде за нього");
        if (++_missed[s] >= 2)
        {
            _away[s] = true;
            Glek(SlMoment.Away, Who(s));
        }
        else Glek(SlMoment.LongThink, Who(s));
    }

    /// <summary>Після кожної зміни: журнал кімнати, голос, кінець партії, строк, Глеки.</summary>
    void After()
    {
        foreach (var line in _core.Journal) Ctx.Log($"{Info.Title}: {line}");
        _core.Journal.Clear();
        Voice();
        Announce();
        Rearm();
        Arm();
    }

    /// <summary>
    /// Строк (§8): початок ходу + таймер; у фазах draw і keep — той самий строк, але щонайменше <see cref="BackMs"/> від входу у
    /// фазу; setup — <see cref="SetupMs"/> або таймер, що більше. Ключі ходу й фази пам'ятаємо й без таймера — від них рахує Глек.
    /// </summary>
    void Rearm()
    {
        var c = _core;
        if (c.Over) { _deadline = null; _deadlineMs = 0; return; }
        var now = Ctx.Clock.UtcNow;
        var turnKey = c.Phase == SlPhase.Setup ? "setup" : $"{c.Turns}|{c.Turn}";
        if (turnKey != _turnKey)
        {
            _turnKey = turnKey;
            _turnAt = _phaseAt = now;
            _phaseKey = $"{turnKey}|{c.Phase}";
            var ms = c.Phase == SlPhase.Setup ? Math.Max(SetupMs, _timerMs) : _timerMs;
            if (_timerMs > 0)
            {
                _deadline = now.AddMilliseconds(ms);
                _deadlineMs = ms;
            }
        }
        var phaseKey = $"{turnKey}|{c.Phase}";
        if (phaseKey != _phaseKey)
        {
            _phaseKey = phaseKey;
            _phaseAt = now;
            if (_timerMs > 0 && c.Phase is SlPhase.Draw or SlPhase.Keep)
            {
                var back = Math.Min(_timerMs, BackMs);
                var floor = now.AddMilliseconds(back);
                if (_deadline is not { } d || d < floor)
                {
                    _deadline = floor;
                    _deadlineMs = back;
                }
            }
        }
        if (_timerMs <= 0) { _deadline = null; _deadlineMs = 0; }
    }

    void Arm()
    {
        _botAt = null;
        var c = _core;
        // У setup Глеки вже обрали, а за людину обирає timeout чи вихід: штовхати нема кого.
        if (c.Over || !c.Started || c.Phase == SlPhase.Setup) return;
        var s = c.Turn;
        if (!Auto(s)) return;
        var at = (c.Phase == SlPhase.Play ? _turnAt : _phaseAt).AddMilliseconds(BoardBots.ThinkMs);
        // Пільга задрімалому: настав хід людини, за яку грає Глек, — перші 5 с не чіпаємо, хай повернеться сама.
        if (_away[s] && !IsBot(s) && Ctx.Seated(s) && at < _turnAt.AddMilliseconds(AwayGraceMs)) at = _turnAt.AddMilliseconds(AwayGraceMs);
        _botAt = at;
    }

    void Announce()
    {
        var c = _core;
        if (_announced || !c.Over) return;
        _announced = true;
        _botAt = null;
        _deadline = null;
        var scores = new Dictionary<int, long>();
        foreach (var s in c.Order)
            if (Ctx.Seated(s) && !IsBot(s)) scores[s] = c.Total(s);
        Awards();
        var w = c.Winners;
        var top = c.Total(w[0]);
        var rest = string.Join(", ", c.Order.Where(s => !w.Contains(s)).Select(s => $"{Nick(s)} {c.Total(s)}"));
        var text = $"{Info.Title}: {string.Join(", ", w.Select(Nick))} — {top} ⭐";
        if (rest.Length > 0) text += $" ({rest})";
        // Переможцем може бути місце Глека (як у «Хуторах»): решта людей програли, а не нічия.
        Ctx.Finish([.. w], text, scores, w.Length > 1 ? "🤝 Валку довели разом" : null);
    }

    /// <summary>Ачівки §9.1 — лише людям за столом, не away.</summary>
    void Awards()
    {
        var c = _core;
        foreach (var s in c.Order)
        {
            if (IsBot(s) || _away[s] || !Ctx.Seated(s)) continue;
            var won = c.Winners.Contains(s);
            if (c.Crimea[s] >= 2) Ctx.Award(s, 0, "ach:sol-perekop");
            if (won && c.LongestSeats.Contains(s)) Ctx.Award(s, 0, "ach:sol-valka");
            if (c.Biggest[s] >= 6) Ctx.Award(s, 0, "ach:sol-six");
            if (c.Tickets[s].Count >= 5 && c.Tickets[s].All(t => c.Done[t])) Ctx.Award(s, 0, "ach:sol-clean");
            if (won && c.Wagons[s] == 0 && c.Seats >= 3) Ctx.Award(s, 0, "ach:sol-dry");
        }
    }

    /// <summary>Чи сидить за столом ще хтось із людей, крім <paramref name="seat"/>.</summary>
    bool OthersSeated(int seat) => _core.Order.Any(s => s != seat && !IsBot(s) && Ctx.Seated(s));

    /// <summary>
    /// Людина встала посеред партії — за неї сідає Глек (<c>away</c>, нік лишається); у фазі setup він одразу й обирає. Людей не
    /// лишилось — партія НЕ кінчається, а стає на паузу: тихо, ні Глеків, ні строку, стіл чекає <see cref="HoldEmpty"/>.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (_core.Over || seat < 0 || seat >= MaxSeats || !_core.In[seat] || IsBot(seat)) return;
        var nick = Nick(seat);
        _away[seat] = true;
        if (!OthersSeated(seat))
        {
            After();
            return;
        }
        _core.Note($"Глек сідає замість {nick}");
        Ctx.Log($"{Info.Title}: {nick} встає — за нього їде Глек");
        Glek(SlMoment.Away, Who(seat));
        if (_core.Phase == SlPhase.Setup && !_core.Ready[seat]) SolyanyiBot.Step(_core, seat);
        After();
    }

    /// <summary>Той самий нік сідає на СВОЄ місце — з тією ж рукою, замовленнями, возами й шляхами. Чужий нік — ні.</summary>
    public override int? Rejoin(string nick)
    {
        if (_core.Over) return null;
        foreach (var s in _core.Order)
            if (!IsBot(s) && !Ctx.Seated(s) && string.Equals(_nicks[s], nick, StringComparison.OrdinalIgnoreCase))
                return s;
        return null;
    }

    public override string? LateJoinGreeting(string nick) => "Твоя валка на місці — Глек вів її замість тебе";

    /// <summary>Людина повернулась на своє місце (<see cref="Rejoin"/>): Глек віддає валку.</summary>
    public override void OnJoin(int seat)
    {
        if (_core.Over || seat < 0 || seat >= MaxSeats || !_core.In[seat] || IsBot(seat)) return;
        var nick = Nick(seat);
        // Стіл стояв на паузі чи зараз його черга — строк свіжий: старий, що минув, поки нікого не було, дав би timeout одразу.
        if (!OthersSeated(seat) || _core.Turn == seat) _turnKey = "";
        _away[seat] = false;
        _missed[seat] = 0;
        _core.Note($"{nick} повертається до своєї валки");
        Ctx.Log($"{Info.Title}: {nick} повертається до своєї валки — Глек віддає батіг");
        Glek(SlMoment.Back, Who(seat));
        // Хто вийшов останнім, не обравши, — обирати за нього було кому лише зараз: без таймера стіл стояв би вічно.
        if (_core.Phase == SlPhase.Setup)
            foreach (var s in _core.FromFirst().ToArray())
                if (s != seat && !_core.Ready[s] && !IsBot(s) && !Ctx.Seated(s)) SolyanyiBot.Step(_core, s);
        After();
    }

    // =====================================================================================
    // Глек-ведучий (§14.1)
    // =====================================================================================

    bool Glek(SlMoment m, Dictionary<string, string>? vars = null)
    {
        if (_glek.Say(m, Ctx.Clock.UtcNow, Ctx.Rng, vars) is not { } line) return false;
        Ctx.Say(line);
        return true;
    }

    Dictionary<string, string> Who(int seat) => new() { ["who"] = Nick(seat) };

    /// <summary>Пройти нові події ядра й озватися. Події «лише власнику» (<c>done</c>) Глек не чує: нового <c>seq</c> вони не мають.</summary>
    void Voice()
    {
        var c = _core;
        foreach (var e in c.Events.ToList())
        {
            if (e.Seq <= _heardSeq) continue;
            _heardSeq = e.Seq;
            if (e.Only >= 0) continue;
            if (e.K == "turn") { _glek.NewTurn(); continue; }
            if (SolyanyiLines.Hear(e, c, Nick, _heard) is { } said) Glek(said.M, said.Vars);
            if (e.K == "claim" && e["seat"] is int seat && SolyanyiLines.HearRoad(c, Nick, _heard, seat) is { } road) Glek(road.M, road.Vars);
        }
    }

    // =====================================================================================
    // Вид (§5)
    // =====================================================================================

    public override object View(int? seat)
    {
        var c = _core;
        var started = c.Started;
        var me = started && seat is { } s0 && s0 >= 0 && s0 < MaxSeats && c.In[s0] && !IsBot(s0) ? s0 : -1;
        var over = c.Over;
        var setup = c.Phase == SlPhase.Setup;
        var players = c.Order.Select(s => new
        {
            seat = s,
            nick = Nick(s),
            bot = _bots[s],
            color = s,
            hand = c.Hand[s].Sum(),
            tickets = c.Tickets[s].Count,
            wagons = c.Wagons[s],
            points = c.RoutePts[s],
            road = c.Road[s],
            routes = c.Taken[s].ToArray(),
            ready = !setup || c.Ready[s],
            away = _away[s],
        }).ToArray();
        var leaders = c.RoadLeaders();
        // Спершу відсіяти чуже, тоді брати останні 40: інакше число подій у вікні виказало б суперникам приховане.
        var events = c.Events.Select(e => e.For(me >= 0 ? me : null)).Where(e => e is not null).TakeLast(40).ToArray();
        return new
        {
            v = 1,
            phase = !started ? null : c.Phase switch { SlPhase.Setup => "setup", SlPhase.Play => "play", SlPhase.Draw => "draw", SlPhase.Keep => "keep", _ => "over" },
            board = new { sig = Sig, doubles = c.OnePairOnly ? "one" : "both" },
            round = c.Round,
            turns = c.Turns,
            wagons = _wagons,
            last = c.Last,
            left = c.Last && !over ? c.Left : (int?)null,
            players,
            order = (int[])c.Order.Clone(),
            first = started ? c.First : (int?)null,
            turn = started && !over && !setup ? c.Turn : (int?)null,
            claimed = Enumerable.Range(0, Routes.Length).Where(r => c.Owner[r] >= 0).Select(r => new { route = r, seat = c.Owner[r] }).ToArray(),
            closed = c.Closed(),
            market = c.Market.Select(m => m >= 0 ? Cards[m] : null).ToArray(),
            deck = c.Deck.Count,
            discard = c.Discard.Count,
            ticketsLeft = c.TicketDeck.Count,
            longest = new { len = leaders.Length > 0 ? c.Road[leaders[0]] : 0, seats = leaders },
            events,
            seq = c.Seq,
            deadline = over ? null : _deadline,
            timerMs = !over && _deadline is not null ? _deadlineMs : (int?)null,
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
        var must = c.Over ? null
            : c.Phase == SlPhase.Setup ? (c.Ready[s] ? null : "setup")
            : c.Turn != s ? null
            : c.Phase switch { SlPhase.Play => "act", SlPhase.Draw => "draw", SlPhase.Keep => "keep", _ => null };
        var legal = c.Legal(s);
        var plan = SolyanyiBot.Plan(c, s);
        var claims = new Dictionary<string, object>();
        foreach (var q in legal.Claims)
        {
            claims[q.Route.ToString()] = q.Ok
                ? new
                {
                    ok = true,
                    combos = q.Combos.Select(p => new { c = Cards[p.C], n = p.N, w = p.W }).ToArray(),
                    best = SolyanyiBot.BestPay(c, s, q.Route, q.Combos, plan),
                }
                : new { ok = false, @short = q.Short, why = q.Why };
        }
        var routes = c.RoutePts[s];
        var done = c.DonePts(s);
        var open = c.OpenPts(s);
        var path = new List<int>();
        var tickets = c.Tickets[s].Select(id =>
        {
            if (c.Done[id]) return new { id, done = true, need = (int?)0, path = Array.Empty<int>(), dead = false };
            var d = c.Need(s, id, path);
            return d < 0
                ? new { id, done = false, need = (int?)null, path = Array.Empty<int>(), dead = true }
                : new { id, done = false, need = (int?)d, path = path.ToArray(), dead = false };
        }).ToArray();
        var choosing = must is "setup" or "keep";
        return new
        {
            seat = s,
            own = c.OwnSeq[s],
            hand = Enumerable.Range(0, Cards.Length).ToDictionary(i => Cards[i], i => c.Hand[s][i]),
            tickets,
            score = new { routes, done, open, total = routes + done - open },
            must,
            offer = choosing ? c.Offer[s].Select(id => { var d = c.Need(s, id); return new { id, need = d < 0 ? (int?)null : d }; }).ToArray() : [],
            keepMin = choosing ? c.KeepMin : 0,
            can = new { draw = legal.Draw, deck = legal.Deck, market = legal.Market, tickets = legal.Tickets, claim = legal.Claim, pass = legal.Pass },
            why = legal.Why,
            claims,
            tip = must is null ? null : Tip(s),
        };
    }

    /// <summary>«💡 Глек радить» (§5), закешована на seq + місце (і руку: чужі ходи її не міняють, а тести — буває).</summary>
    object? Tip(int s)
    {
        var c = _core;
        var key = $"{c.Seq}|{string.Join(',', c.Hand[s])}|{c.Phase}";
        if (_tips[s].Key == key) return _tips[s].Tip;
        var tip = TipWire(SolyanyiBot.Decide(c, s));
        _tips[s] = (key, tip);
        return tip;
    }

    /// <summary>Порада у форматі дроту.</summary>
    static object TipWire(SlMove m) => m.Act switch
    {
        "claim" => new { act = "claim", route = m.Route, c = Cards[m.C], w = m.W },
        "draw" when m.Deck => new { act = "draw", deck = true },
        "draw" => new { act = "draw", slot = m.Slot },
        "tickets" => new { act = "tickets" },
        "keep" => new { act = "keep", ids = m.Ids },
        _ => (object)new { act = "pass" },
    };

    object Over()
    {
        var c = _core;
        return new
        {
            winners = (int[])c.Winners.Clone(),
            reason = c.Reason,
            lastBy = c.LastBy >= 0 ? c.LastBy : (int?)null,
            points = c.Order.Select(c.Total).ToArray(),
            final = c.Order.Select(s => new
            {
                seat = s,
                routes = c.RoutePts[s],
                done = c.DonePts(s),
                open = c.OpenPts(s),
                bonus = c.Bonus(s),
                total = c.Total(s),
                road = c.Road[s],
                tickets = c.Tickets[s].Select(t => new { id = t, done = c.Done[t] }).ToArray(),
            }).ToArray(),
            longest = new
            {
                len = c.LongestLen,
                seats = (int[])c.LongestSeats.Clone(),
                routes = c.LongestRoutes.ToDictionary(p => p.Key.ToString(), p => (int[])p.Value.Clone()),
            },
            stats = new
            {
                rounds = c.Round,
                turns = c.Turns,
                ms = (long)(Ctx.Clock.UtcNow - _startedAt).TotalMilliseconds,
                timeline = c.Timeline.Select(r => (int[])r.Clone()).ToArray(),
                seats = c.Order.Select(s => new
                {
                    seat = s, claims = c.Claims[s], wagonsUsed = c.Wagons0 - c.Wagons[s], biggest = c.Biggest[s], draws = c.Draws[s],
                    drawn = c.Drawn[s], facedown = c.Facedown[s], faceWild = c.FaceWild[s], wildsPaid = c.WildsPaid[s],
                    ticketDraws = c.TicketDraws[s], ticketsKept = c.TicketsKept[s], blocks = c.Blocks[s], crimea = c.Crimea[s],
                    firstClaim = c.FirstClaim[s] >= 0 ? c.FirstClaim[s] : (int?)null,
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

    static bool True(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.True;

    static string? String(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;

    /// <summary>Масив цілих або null («Не зрозумів, які замовлення»).</summary>
    static int[]? Ints(JsonElement p, string name)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var x) || x.ValueKind != JsonValueKind.Array) return null;
        var list = new List<int>();
        foreach (var e in x.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out var n)) return null;
            list.Add(n);
        }
        return [.. list];
    }
}
