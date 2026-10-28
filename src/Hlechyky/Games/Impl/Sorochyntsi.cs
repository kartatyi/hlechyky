using System.Text.Json;
using static Hlechyky.Games.Impl.SorochyntsiCards;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Сорочинський ярмарок» — карткова гра на 2–4 «збери крамниці й розбудуй торгівлю» (контракт
/// <c>docs/games/specs/sorochyntsi.md</c>). Правила — у <see cref="SorochyntsiCore"/>, Глеки — <see cref="SorochyntsiBot"/>,
/// голос ведучого — <see cref="SorochyntsiLines"/>; тут каркас, як у «Хуторах»: опції, розсадка з Глеками, таймер ходу,
/// вихід, пауза й повернення, вид. Гра прихована (<c>Hidden</c>): бронь наосліп їде лише власнику.
/// </summary>
public sealed partial class Sorochyntsi : Game
{
    public override GameInfo Info { get; } = new(
        "sorochyntsi", "Сорочинський ярмарок", "Сорочинський ярмарок", GameGroup.Board, 1, MaxSeats, Start: StartMode.ByHost,
        Hidden: true, Rated: false, Score: ScoreOrder.None,
        Options:
        [
            BoardBots.Option(3),
            new GameOption("vp", "Грати до", [("14", "14 ⭐ · коротша"), ("16", "16 ⭐ · звичайна"), ("18", "18 ⭐ · довга")], "16"),
            new GameOption("timer", "Час на хід", [("none", "без часу"), ("45", "45 с"), ("60", "60 с"), ("90", "90 с")], "60"),
        ],
        Hint: "Сорочинський ярмарок — на 2–4: бери товари, купуй крамниці, що дають знижку назавжди, клич гостей і першим "
            + "здобудь гетьманську булаву. Бракує людей — підсяде Глек 🤖");

    static readonly string[] Colors = ["червоні", "сині", "білі", "рожеві"];

    /// <summary>Скільки задрімала людина має на свій хід сама, перш ніж Глек зіграє за неї.</summary>
    public const int AwayGraceMs = 5000;
    /// <summary>Фази discard і guest — щонайменше стільки від входу у фазу (не довше за таймер).</summary>
    public const int BackMs = 15_000;

    /// <summary>Людей не лишилось (відпав зв'язок) — партія стоїть на паузі 10 хв (як у «Хуторах»).</summary>
    public override TimeSpan HoldEmpty => TimeSpan.FromMinutes(10);

    /// <summary>Останній встав кнопкою — пішов: стіл зникає одразу (пауза лише на обрив зв'язку).</summary>
    public override bool HoldAfterLeave => false;

    int _target = 16;
    int _timerMs = 60_000;
    int _botsWanted;

    SorochyntsiCore _core = new([]);
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
    readonly SorochyntsiGlek _glek = new();
    long _heardSeq;
    readonly HashSet<int> _bought = [];
    bool _t3Said;

    /// <summary>Правила поточної партії (тести й діагностика).</summary>
    public SorochyntsiCore Core => _core;

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _target = options.TryGetValue("vp", out var vp) && vp is "14" or "16" or "18" ? int.Parse(vp) : 16;
        _timerMs = options.TryGetValue("timer", out var t)
            ? t switch { "none" => 0, "45" => 45_000, "90" => 90_000, _ => 60_000 }
            : 60_000;
        _botsWanted = BoardBots.Read(options, 3);
    }

    public override string? CanStart()
    {
        var humans = BoardBots.Humans(Ctx, MaxSeats);
        return humans + Math.Min(_botsWanted, MaxSeats - humans) >= 2 ? null
            : "Самому нема з ким торгувати: хай хтось сяде — або додай 🤖 Глека";
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
        _core = new SorochyntsiCore(seats) { Target = _target, NameOf = Nick, Seq = seq };
        _announced = false;
        _turnKey = _phaseKey = "";
        _deadline = _botAt = null;
        _deadlineMs = 0;
        _heardSeq = seq;
        _bought.Clear();
        _t3Said = false;
        _startedAt = Ctx.Clock.UtcNow;
        _glek.Reset();
        // Порядок Ctx.Rng — як у контракті §2.1: колоди, гості, перший гравець; голос — після.
        _core.Begin(Ctx.Rng);
        Glek(SoMoment.Start);
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
            "take" => _core.Take(seat, Strings(payload, "goods")),
            "reserve" => _core.Reserve(seat, Int(payload, "card"), Int(payload, "tier")),
            "buy" => _core.Buy(seat, Int(payload, "card")),
            "pass" => _core.Pass(seat),
            "discard" => _core.Discard(seat, Give(payload)),
            "guest" => _core.Guest(seat, Int(payload, "guest")),
            _ => "Тут так не ходять",
        };
        if (error is not null) return ActResult.Fail(error);
        // Будь-яка своя дія повертає людину з-під опіки Глека.
        if (_away[seat])
        {
            _core.Note($"{Nick(seat)} знову торгує сам");
            Glek(SoMoment.Back, Who(seat));
        }
        _away[seat] = false;
        _missed[seat] = 0;
        After();
        return ActResult.Done;
    }

    /// <summary>Хід Глека (штовхає клієнт людини): увесь хід того, чия черга, — дія, повернення зайвого, вибір гостя.</summary>
    ActResult BotTurn()
    {
        if (_botAt is not { } at) return ActResult.Fail("Глекам нема що робити");
        if (Ctx.Clock.UtcNow < at) return ActResult.Fail("Глек ще думає");
        var s = _core.Turn;
        if (!Auto(s)) return ActResult.Fail("Глекам нема що робити");
        SorochyntsiBot.Step(_core, s);
        After();
        return ActResult.Done;
    }

    /// <summary>Минув строк: за того, хто тягне, хід доробляє Глек. Двічі поспіль проспав — далі за нього Глек.</summary>
    ActResult Timeout()
    {
        if (_timerMs <= 0) return ActResult.Fail("Тут без часу");
        if (_deadline is not { } dl || Ctx.Clock.UtcNow < dl) return ActResult.Fail("Ще є час");
        var s = _core.Turn;
        var human = !Auto(s);
        SorochyntsiBot.Step(_core, s);
        if (human)
        {
            _core.Note($"{Nick(s)} задрімав — Глек торгує за нього");
            if (++_missed[s] >= 2)
            {
                _away[s] = true;
                Glek(SoMoment.Away, Who(s));
            }
            else Glek(SoMoment.LongThink, Who(s));
        }
        After();
        return ActResult.Done;
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
    /// Строк (§8): початок ходу + таймер; у фазах discard і guest — той самий строк, але щонайменше <see cref="BackMs"/>
    /// від входу у фазу. Ключі ходу й фази пам'ятаємо й без таймера — від них рахує Глек.
    /// </summary>
    void Rearm()
    {
        var c = _core;
        if (c.Over) { _deadline = null; _deadlineMs = 0; return; }
        var now = Ctx.Clock.UtcNow;
        var turnKey = $"{c.Turns}|{c.Turn}";
        if (turnKey != _turnKey)
        {
            _turnKey = turnKey;
            _turnAt = _phaseAt = now;
            _phaseKey = $"{turnKey}|{c.Phase}";
            if (_timerMs > 0)
            {
                _deadline = now.AddMilliseconds(_timerMs);
                _deadlineMs = _timerMs;
            }
        }
        var phaseKey = $"{turnKey}|{c.Phase}";
        if (phaseKey != _phaseKey)
        {
            _phaseKey = phaseKey;
            _phaseAt = now;
            if (_timerMs > 0 && c.Phase is SoPhase.Discard or SoPhase.Guest)
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
        if (c.Over || !c.Started) return;
        var s = c.Turn;
        if (!Auto(s)) return;
        var at = (c.Phase == SoPhase.Play ? _turnAt : _phaseAt).AddMilliseconds(BoardBots.ThinkMs);
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
            if (Ctx.Seated(s) && !IsBot(s)) scores[s] = c.Points(s);
        Awards();
        var w = c.Winners;
        var top = c.Points(w[0]);
        var rest = string.Join(", ", c.Order.Where(s => !w.Contains(s)).Select(s => $"{Nick(s)} {c.Points(s)}"));
        var text = $"{Info.Title}: {string.Join(", ", w.Select(Nick))} — {top} ⭐";
        if (rest.Length > 0) text += $" ({rest})";
        // Переможцем може бути місце Глека (як у «Хуторах»): решта людей програли, а не нічия.
        Ctx.Finish([.. w], text, scores, w.Length > 1 ? "🤝 Ярмарок поділили" : null);
    }

    /// <summary>Ачівки §9.1 — лише людям за столом, не away.</summary>
    void Awards()
    {
        var c = _core;
        foreach (var s in c.Order)
        {
            if (IsBot(s) || _away[s] || !Ctx.Seated(s)) continue;
            var won = c.Winners.Contains(s);
            if (won && c.Bulava == s) Ctx.Award(s, 0, "ach:sor-bulava");
            if (c.Ferns[s] >= 2) Ctx.Award(s, 0, "ach:sor-fern");
            if (c.GuestsOf[s].Count >= 3) Ctx.Award(s, 0, "ach:sor-guests");
            if (c.Starshyna == s && c.Icons[s] >= 6) Ctx.Award(s, 0, "ach:sor-kish");
            if (won && c.Seats >= 3 && c.Reserves[s] == 0) Ctx.Award(s, 0, "ach:sor-honest");
        }
    }

    /// <summary>Чи сидить за столом ще хтось із людей, крім <paramref name="seat"/>.</summary>
    bool OthersSeated(int seat) => _core.Order.Any(s => s != seat && !IsBot(s) && Ctx.Seated(s));

    /// <summary>
    /// Людина встала посеред партії — за неї сідає Глек (<c>away</c>, нік лишається). Людей не лишилось — партія НЕ
    /// кінчається, а стає на паузу: тихо, ні Глеків, ні строку (Act — лише тим, хто сидить), стіл чекає <see cref="HoldEmpty"/>.
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
        Ctx.Log($"{Info.Title}: {nick} встає — за нього торгує Глек");
        Glek(SoMoment.Away, Who(seat));
        After();
    }

    /// <summary>Той самий нік сідає на СВОЄ місце — з тими самими жетонами, крамницями й бронею. Чужий нік — ні.</summary>
    public override int? Rejoin(string nick)
    {
        if (_core.Over) return null;
        foreach (var s in _core.Order)
            if (!IsBot(s) && !Ctx.Seated(s) && string.Equals(_nicks[s], nick, StringComparison.OrdinalIgnoreCase))
                return s;
        return null;
    }

    public override string? LateJoinGreeting(string nick) => "Твій прилавок на місці — Глек торгував замість тебе";

    /// <summary>Людина повернулась на своє місце (<see cref="Rejoin"/>): Глек віддає прилавок.</summary>
    public override void OnJoin(int seat)
    {
        if (_core.Over || seat < 0 || seat >= MaxSeats || !_core.In[seat] || IsBot(seat)) return;
        var nick = Nick(seat);
        // Стіл стояв на паузі чи зараз його черга — строк свіжий: старий, що минув, поки нікого не було, дав би timeout одразу.
        if (!OthersSeated(seat) || _core.Turn == seat) _turnKey = "";
        _away[seat] = false;
        _missed[seat] = 0;
        _core.Note($"{nick} повертається до свого прилавка");
        Ctx.Log($"{Info.Title}: {nick} повертається до свого прилавка — Глек віддає кермо");
        Glek(SoMoment.Back, Who(seat));
        After();
    }

    // =====================================================================================
    // Глек-ведучий (§14.1)
    // =====================================================================================

    bool Glek(SoMoment m, Dictionary<string, string>? vars = null)
    {
        if (_glek.Say(m, Ctx.Clock.UtcNow, Ctx.Rng, vars) is not { } line) return false;
        Ctx.Say(line);
        return true;
    }

    Dictionary<string, string> Who(int seat) => new() { ["who"] = Nick(seat) };

    /// <summary>Пройти нові події ядра й озватися; далі — «майже булава» за відкритими очками й знижками.</summary>
    void Voice()
    {
        var c = _core;
        foreach (var e in c.Events.ToList())
        {
            if (e.Seq <= _heardSeq) continue;
            _heardSeq = e.Seq;
            if (e.K == "turn") { _glek.NewTurn(); continue; }
            var first = false;
            var t3 = false;
            if (e.K == "buy" && e["seat"] is int bs && e["card"] is int bc)
            {
                first = _bought.Add(bs);
                t3 = Deck[bc].Tier == 3 && !_t3Said;
                if (t3) _t3Said = true;
            }
            if (SorochyntsiLines.Hear(e, c, Nick, first, t3) is { } said) Glek(said.M, said.Vars);
        }
        if (c.Over || c.Bulava >= 0) return;
        foreach (var s in c.Order)
        {
            var shortBy = Math.Max(0, c.Target - c.Points(s));
            var missing = Enumerable.Range(0, 6).Where(g => c.Bonus[s][g] == 0).ToList();
            if (shortBy > 3 || missing.Count > 1 || (shortBy == 0 && missing.Count == 0) || _glek.NearSaid(Nick(s))) continue;
            Glek(SoMoment.NearBulava, new()
            {
                ["who"] = Nick(s), ["points"] = shortBy.ToString(), ["good"] = missing.Count == 1 ? GoodAcc[missing[0]] : "",
            });
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
        var players = c.Order.Select(s => new
        {
            seat = s,
            nick = Nick(s),
            bot = _bots[s],
            color = s,
            tokens = TokMap(c.Tok[s]),
            ntok = c.NTok(s),
            bonus = BonusMap(c.Bonus[s]),
            cards = c.Cards[s].ToArray(),
            reserved = c.Reserved[s].Select(r => Res(r, s == me)).ToArray(),
            guests = c.GuestsOf[s].ToArray(),
            icons = c.Icons[s],
            points = c.Points(s),
            starshyna = c.Starshyna == s,
            bulava = c.Bulava == s,
            away = _away[s],
        }).ToArray();
        var events = c.Events.Skip(Math.Max(0, c.Events.Count - 40)).Select(e => e.For(me >= 0 ? me : null)).ToArray();
        return new
        {
            v = 1,
            phase = !started ? null : c.Phase switch { SoPhase.Play => "play", SoPhase.Discard => "discard", SoPhase.Guest => "guest", _ => "over" },
            target = c.Target,
            round = c.Round,
            maxRounds = MaxRounds,
            last = c.Last,
            players,
            order = (int[])c.Order.Clone(),
            first = started ? c.First : (int?)null,
            turn = started && !over ? c.Turn : (int?)null,
            bank = TokMap(c.Bank),
            rows = c.Rows.Select(r => r.Select(id => id >= 0 ? id : (int?)null).ToArray()).ToArray(),
            decks = c.Decks.Select(d => d.Count).ToArray(),
            guests = c.Table.Select(g => new { id = g, by = c.GuestBy[g] >= 0 ? c.GuestBy[g] : (int?)null }).ToArray(),
            starshyna = c.Starshyna >= 0 ? new { seat = c.Starshyna, icons = c.Icons[c.Starshyna] } : null,
            bulava = c.Bulava >= 0 ? c.Bulava : (int?)null,
            discard = c.Phase == SoPhase.Discard ? new { seat = c.Turn, n = c.DiscardN } : null,
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

    static Dictionary<string, int> TokMap(int[] a) => Enumerable.Range(0, 6).ToDictionary(i => Tokens[i], i => a[i]);
    static Dictionary<string, int> BonusMap(int[] a) => Enumerable.Range(0, 6).ToDictionary(i => Bonuses[i], i => a[i]);

    /// <summary>Бронь на дроті: чужа наосліп — без id (лише рівень).</summary>
    static object Res(SoRes r, bool own) => new { id = r.Blind && !own ? null : (int?)r.Id, tier = Deck[r.Id].Tier, blind = r.Blind };

    object Me(int s)
    {
        var c = _core;
        var must = c.Over || c.Turn != s ? null
            : c.Phase switch { SoPhase.Play => "act", SoPhase.Discard => "discard", SoPhase.Guest => "guest", _ => null };
        var legal = c.Legal(s);
        var act = must == "act";
        var cards = new Dictionary<string, object>();
        foreach (var id in c.Visible(s))
        {
            var q = c.Quote(s, id);
            cards[id.ToString()] = q.Ok
                ? new { ok = true, pay = SorochyntsiCore.Map(q.Pay) }
                : new { ok = false, need = SorochyntsiCore.Map(q.Gap), @short = q.Short, why = SorochyntsiCore.WhyShort(q, c.Tok[s][Gold]) };
        }
        object? bulava = null;
        if (c.Bulava < 0)
            bulava = new
            {
                points = Math.Max(0, c.Target - c.Points(s)),
                goods = Enumerable.Range(0, 6).Where(g => c.Bonus[s][g] == 0).Select(g => Bonuses[g]).ToArray(),
            };
        return new
        {
            seat = s,
            reserved = c.Reserved[s].Select(r => Res(r, true)).ToArray(),
            must,
            discard = must == "discard" ? c.DiscardN : 0,
            guests = must == "guest" ? c.GuestChoice.ToArray() : [],
            take = new
            {
                piles = legal.Piles.Select(g => Goods[g]).ToArray(),
                need = legal.Need,
                two = legal.Two.Select(g => Goods[g]).ToArray(),
            },
            can = new
            {
                take = act && legal.Take,
                reserve = act && legal.Reserve,
                reserveDeck = legal.Deck.Select(d => act && d).ToArray(),
                pass = act && legal.Pass,
            },
            why = act ? new Dictionary<string, string>(legal.Why) : [],
            cards,
            bulava,
            tip = must is null ? null : Tip(SorochyntsiBot.Decide(c, s)),
        };
    }

    /// <summary>«💡 Глек радить» (§5) у форматі дроту.</summary>
    static object Tip(SoMove m) => m.Act switch
    {
        "take" => new { act = "take", goods = m.Goods() },
        "reserve" when m.Card >= 0 => new { act = "reserve", card = m.Card },
        "reserve" => new { act = "reserve", tier = m.Tier },
        "buy" => new { act = "buy", card = m.Card },
        "discard" => new { act = "discard", give = SorochyntsiCore.Map(m.Give!) },
        "guest" => new { act = "guest", guest = m.Guest },
        _ => (object)new { act = "pass" },
    };

    object Over()
    {
        var c = _core;
        return new
        {
            winners = (int[])c.Winners.Clone(),
            reason = c.Reason,
            bulava = c.Bulava >= 0 ? c.Bulava : (int?)null,
            points = c.Order.Select(c.Points).ToArray(),
            cards = c.Order.Select(s => c.Cards[s].Count).ToArray(),
            stats = new
            {
                rounds = c.Timeline.Count,
                turns = c.Turns,
                ms = (long)(Ctx.Clock.UtcNow - _startedAt).TotalMilliseconds,
                timeline = c.Timeline.Select(r => (int[])r.Clone()).ToArray(),
                seats = c.Order.Select(s => new
                {
                    seat = s, takes = c.Takes[s], tokens = c.TokensGot[s], reserves = c.Reserves[s], blind = c.Blinds[s],
                    buys = (int[])c.Buys[s].Clone(), goldSpent = c.GoldSpent[s], returned = c.Returned[s],
                    guests = c.GuestsOf[s].Count, icons = c.Icons[s], fern = c.Ferns[s],
                    firstT3 = c.FirstT3[s] >= 0 ? c.FirstT3[s] : (int?)null,
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

    /// <summary>Масив рядків або null («Не зрозумів, що береш»).</summary>
    static string[]? Strings(JsonElement p, string name)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var x) || x.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var e in x.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String) return null;
            list.Add(e.GetString()!);
        }
        return [.. list];
    }

    /// <summary>{ "fish": 1, "gold": 1 } → пари; null — не об'єкт або не цілі ≥ 0 («Не зрозумів, що повертаєш»).</summary>
    static List<KeyValuePair<string, int>>? Give(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("give", out var x) || x.ValueKind != JsonValueKind.Object) return null;
        var list = new List<KeyValuePair<string, int>>();
        foreach (var prop in x.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt32(out var n) || n < 0) return null;
            list.Add(new(prop.Name, n));
        }
        return list;
    }
}
