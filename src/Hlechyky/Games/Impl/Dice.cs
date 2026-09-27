using System.Globalization;
using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Фаза раунду: трусимо глеки → ставки по колу → глеки догори → (новий раунд або кінець).</summary>
public enum DicePhase { Shake, Bid, Reveal, Done }

/// <summary>
/// «Під глеком» — брехливі кості (Perudo) на 2–6. У кожного п'ять кісточок під своїм глеком, бачиш лише свої.
/// По колу ставиш, скільки кісточок із такою гранню лежить на всьому столі (кожна наступна ставка вища), або
/// кажеш «Брешеш!» — глеки піднімаються, хто помилився, губить кісточку. Одиниці-«глечики» — джокери.
/// Останній із кісточками виграє. Правила — у <see cref="DiceCore"/>; тут фази, час, опції, вид і Глек.
/// <para>
/// Гра покрокова, але з тиком (250 мс): у реалтайм-кімнаті каркас шле види лише з тика, а таймер ходу й
/// фази все одно треба рахувати. Тик — три порівняння і <see cref="TickResult.None"/> майже завжди; усе,
/// що змінилось, — позначка <see cref="_dirty"/>, і найближчий тик розсилає види.
/// </para>
/// <para>
/// Гра <c>Hidden</c>: до розкриття чужих граней нема ніде — ні в чужому виді, ні в глядача, ні в кадрі. Грані
/// всіх з'являються лише в <c>reveal.dice</c>, коли глеки вже підняли.
/// </para>
/// </summary>
public sealed class Dice : Game
{
    /// <summary>Скільки трусимо глеки на початку раунду: коротка вистава, щоб усі встигли глянути на свої.</summary>
    public const int ShakeMs = 1500;
    /// <summary>Скільки висить розкриття (або поки всі живі не натиснуть «Далі»).</summary>
    public const int RevealMs = 6000;
    public const int TickMs = 250;
    /// <summary>Скільки таймаутів поспіль — і гравець «сонний»: далі йому лише <see cref="SleepyTurnMs"/> на хід.</summary>
    public const int SleepyAfter = 2;
    /// <summary>Хід сонного: стіл із шести не чекає по пів хвилини того, хто вийшов по чай. Прокинувся — знову повний час.</summary>
    public const int SleepyTurnMs = 10_000;
    /// <summary>Реакції (🤨 😏 😂) — не частіше за раз на стільки з одного місця.</summary>
    public const int ReactGapMs = 2000;
    public const int ReactKinds = 3;

    public override GameInfo Info { get; } = new(
        "dice", "Під глеком", "«Під глеком»", GameGroup.Party, 2, DiceCore.MaxSeats,
        TickMs: TickMs, Start: StartMode.ByHost, Hidden: true, Private: false, Persistent: false, Rated: false,
        Score: ScoreOrder.None,
        Options:
        [
            new GameOption("dice", "Кісточок у кожного", [("5", "5 — класика"), ("3", "3 — швидка партія")], "5"),
            new GameOption("turn", "Час на хід", [("30", "30 с"), ("45", "45 с на хід"), ("60", "60 с на хід")], "30"),
            // Підпис «є» новачкові нічого не каже, а «нема» чипом у списку столів — і поготів: пояснюємо просто в підписі.
            new GameOption("exact", "«Точно!»", [("on", "є — вгадав рівно, повертаєш кісточку"), ("off", "без «Точно!»")], "on"),
            new GameOption("palifico", "Паліфіко", [("on", "є — на одній кісточці раунд без джокерів"), ("off", "без паліфіко")], "on"),
        ],
        Hint: "Брехливі кості: у кожного під глеком кісточки, бачиш лише свої. Став, скільки їх на всьому столі, або кажи «Брешеш!». Одиниці — джокери");

    static readonly string[] Names = ["перший", "другий", "третій", "четвертий", "п'ятий", "шостий"];
    static readonly TickResult ViewsOnly = new(false, true);

    const string Stale = "Ставка вже змінилась — глянь ще раз";

    // ---------- правила столу (опції) ----------
    int _diceRule = DiceCore.MaxDice;
    int _turnMs = 30_000;
    bool _exactRule = true;
    bool _palificoRule = true;

    // ---------- стан партії ----------
    readonly DiceCore _core = new();
    /// <summary>Ніки на старті: місце того, хто встав, каркас звільняє одразу, а назвати його треба й далі.</summary>
    string?[] _nicks = new string?[DiceCore.MaxSeats];
    DicePhase _phase = DicePhase.Shake;
    DateTimeOffset _endsAt;
    int _phaseMs = ShakeMs;
    /// <summary>Щось змінилось — найближчий тик розсилає види (у реалтайм-кімнаті каркас не шле їх з Act).</summary>
    bool _dirty;
    /// <summary>Рядок під час трусіння: «Раунд 3. Починає Оля» / паліфіко / «Петро встав — перетрушуємо».</summary>
    string? _note;
    DiceOutcome? _reveal;
    DiceResult? _result;
    /// <summary>Таймаутів поспіль у місця (скидає будь-яка власна дія): від <see cref="SleepyAfter"/> — короткий хід.</summary>
    readonly int[] _sleep = new int[DiceCore.MaxSeats];
    /// <summary>Реакції: яка остання (0..2), скільки їх було (клієнт показує бульбашку, коли число росте), коли.</summary>
    readonly int[] _reactE = new int[DiceCore.MaxSeats];
    readonly int[] _reactN = new int[DiceCore.MaxSeats];
    readonly DateTimeOffset[] _reactAt = new DateTimeOffset[DiceCore.MaxSeats];
    // Смішні нагороди в підсумку: усе — з розкриттів і таймаутів, тобто з того, що й так бачили всі.
    readonly int[] _timeouts = new int[DiceCore.MaxSeats];
    readonly int[] _exacts = new int[DiceCore.MaxSeats];
    readonly int[] _catches = new int[DiceCore.MaxSeats];
    (int Seat, int Q, int F, int Count) _bluff = (-1, 0, 0, 0);

    /// <summary>Підсумок партії у виді: переможець, порядок вибування, скільки раундів, слово Глека, смішні нагороди.</summary>
    sealed record DiceResult(int Winner, int[] Places, int Rounds, string Say, string[] Fun);

    /// <summary>Правила без кімнати — для тестів (руки руками, стартер руками).</summary>
    public DiceCore Core => _core;
    public DicePhase Phase => _phase;

    public override string SeatName(int seat) => seat >= 0 && seat < Names.Length ? Names[seat] : $"гравець {seat + 1}";

    /// <summary>Опції столу. Каркас зводить їх до дозволених значень, але чуже значення тут теж не зламає нічого.</summary>
    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _diceRule = options.TryGetValue("dice", out var d) && d == "3" ? 3 : DiceCore.MaxDice;
        _turnMs = options.TryGetValue("turn", out var t) && t is "45" or "60"
            ? int.Parse(t, CultureInfo.InvariantCulture) * 1000
            : 30_000;
        _exactRule = !(options.TryGetValue("exact", out var e) && e == "off");
        _palificoRule = !(options.TryGetValue("palifico", out var p) && p == "off");
    }

    public override void Start()
    {
        var seats = new List<int>(DiceCore.MaxSeats);
        for (var s = 0; s < DiceCore.MaxSeats; s++)
        {
            _nicks[s] = Ctx.NickOf(s);
            if (Ctx.Seated(s)) seats.Add(s);
        }
        _core.Deal(seats, _diceRule);
        _reveal = null;
        _result = null;
        Array.Clear(_sleep);
        Array.Clear(_reactE);
        Array.Clear(_reactN);
        Array.Clear(_reactAt);
        Array.Clear(_timeouts);
        Array.Clear(_exacts);
        Array.Clear(_catches);
        _bluff = (-1, 0, 0, 0);
        // Хто починає перший раунд — випадкове живе місце (один виклик Rng, до кидків).
        var starter = seats[Ctx.Rng.Next(seats.Count)];
        StartRound(starter, Ctx.Clock.UtcNow);
    }

    // ---------- дії ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        var r = Do(seat, action, payload);
        // Будь-яка власна дія — людина за столом: сонний прокидається, наступний хід знову повний.
        if (r.Ok && seat is >= 0 and < DiceCore.MaxSeats) _sleep[seat] = 0;
        return r;
    }

    ActResult Do(int seat, string action, JsonElement payload)
    {
        if (action is not ("bid" or "liar" or "exact" or "ready" or "react")) return ActResult.Fail("Тут так не ходять");
        if (_phase == DicePhase.Done) return ActResult.Fail("Партію зіграно, тисни «Ану ще раз»");
        var now = Ctx.Clock.UtcNow;
        // Реакція — лише косметика: у будь-якій фазі й навіть тому, хто вже без кісточок (він же дивиться далі).
        if (action == "react") return React(seat, payload, now);
        if (_phase == DicePhase.Shake) return ActResult.Fail("Ще трусимо глеки");
        if (seat is < 0 or >= DiceCore.MaxSeats || !_core.Dealt[seat] || !_core.Alive[seat])
            return ActResult.Fail("Ти без кісточок — дивись і вболівай");

        // Час ходу вийшов, а тик ще не встиг (≤ 250 мс): дуга в людини вже на нулі. Рахуємо так, як порахував
        // би тик, і ставку не приймаємо — інакше «проспав» залежало б від того, де саме в тику натиснули.
        if (_phase == DicePhase.Bid && now >= _endsAt)
        {
            Timeout(now);
            return ActResult.Fail(_phase == DicePhase.Reveal ? "Час вийшов — глеки вже піднімають" : "Час вийшов — хід пішов далі");
        }

        if (_phase == DicePhase.Reveal)
        {
            // Два «Точно!» одночасно — звична справа: другий не встиг, і так йому й кажемо (не «ставка змінилась»).
            if (action != "ready") return ActResult.Fail(action == "exact" ? "Не встиг — глеки вже підняли" : "Глеки вже підняли — рахуємо");
            if (_core.MarkReady(seat)) _dirty = true;
            return ActResult.Done;
        }

        switch (action)
        {
            case "bid":
            {
                if (seat != _core.Turn) return ActResult.Fail("Зараз не твій хід");
                if (!TryBid(payload, out var q, out var f)) return ActResult.Fail("Не зрозумів ставки");
                if (_core.PlaceBid(seat, q, f) is { } why) return ActResult.Fail(why);
                SetTimer(now, TurnMs(_core.Turn));
                _dirty = true;
                return ActResult.Done;
            }
            case "liar":
            {
                if (seat != _core.Turn) return ActResult.Fail("Зараз не твій хід");
                if (_core.Bid is not { } bid) return ActResult.Fail("Нема ставки — нема кому не вірити");
                // null — «не вірю тому, що зараз на столі»; об'єкт — «не вірю саме цьому»: застаріла ставка — відмова.
                if (payload.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) && !Matches(payload, bid))
                    return ActResult.Fail(Stale);
                Reveal("liar", seat, now);
                return ActResult.Done;
            }
            case "exact":
            {
                if (!_exactRule) return ActResult.Fail("За цим столом «Точно!» не грають");
                if (_core.Bid is not { } bid) return ActResult.Fail("Ще нема ставки");
                if (seat == bid.Seat) return ActResult.Fail("На свою ж ставку «Точно!» не кажуть");
                if (_core.Count[seat] >= _core.Dice) return ActResult.Fail("У тебе повний глек — «Точно!» нічого не дасть");
                // Payload обов'язковий: «Точно!» кажуть поза чергою, і гонка з чужою ставкою — звичайна справа.
                if (!Matches(payload, bid)) return ActResult.Fail(Stale);
                Reveal("exact", seat, now);
                return ActResult.Done;
            }
            default:
                return ActResult.Fail("Зараз нема чого пропускати");
        }
    }

    /// <summary>
    /// 🤨 / 😏 / 😂 над своїм чіпом: блеф — це розмова, а з телефона писати в балачку незручно. Правил не чіпає,
    /// у Журнал не йде; не частіше за раз на <see cref="ReactGapMs"/>, щоб не перетворилось на спам.
    /// </summary>
    ActResult React(int seat, JsonElement payload, DateTimeOffset now)
    {
        if (seat is < 0 or >= DiceCore.MaxSeats || !_core.Dealt[seat]) return ActResult.Fail("Ти не граєш за цим столом");
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("e", out var je)
            || je.ValueKind != JsonValueKind.Number || !je.TryGetInt32(out var e) || e is < 0 or >= ReactKinds)
            return ActResult.Fail("Не зрозумів реакції");
        if (now - _reactAt[seat] < TimeSpan.FromMilliseconds(ReactGapMs)) return ActResult.Fail("Не так часто — хай усі розгледять");
        _reactAt[seat] = now;
        _reactE[seat] = e;
        _reactN[seat]++;
        _dirty = true;
        return ActResult.Done;
    }

    /// <summary>
    /// Три порівняння й кінець: фаза вийшла за часом, або всі живі натиснули «Далі». Жодних алокацій; види
    /// летять лише тоді, коли щось змінилось (<see cref="_dirty"/>). Кадр не летить зовсім: модуль його не
    /// читає, а все, що в ньому є, приходить у виді.
    /// </summary>
    public override TickResult Tick()
    {
        if (_phase == DicePhase.Done) return TickResult.None;
        var now = Ctx.Clock.UtcNow;
        switch (_phase)
        {
            case DicePhase.Shake when now >= _endsAt:
                _phase = DicePhase.Bid;
                SetTimer(now, TurnMs(_core.Turn));
                _dirty = true;
                break;
            case DicePhase.Bid when now >= _endsAt:
                Timeout(now);
                break;
            case DicePhase.Reveal when now >= _endsAt || _core.ReadyCount >= _core.AliveCount:
                AfterReveal(now);
                break;
        }
        if (!_dirty) return TickResult.None;
        _dirty = false;
        return ViewsOnly;
    }

    /// <summary>
    /// Встав з-за столу — вибуває цієї ж миті. Лишився один — перемога йому. Посеред трусіння чи ставок раунд
    /// перетрушується без того, хто пішов (кількості на столі вже інші, а ставки пропадають), але починає його
    /// той самий стартер — він здобув це право, програвши минуле розкриття, і його паліфіко не згорає. Пішов
    /// сам стартер — починає наступний за ним. Розкриття дограється як є.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (_phase == DicePhase.Done || seat is < 0 or >= DiceCore.MaxSeats || !_core.Dealt[seat]) return;
        var wasAlive = _core.Alive[seat];
        _core.Drop(seat);
        _dirty = true;
        if (!wasAlive) return;

        var nick = Nick(seat);
        if (_core.AliveCount <= 1)
        {
            var winner = _core.FirstAlive();
            // Ніки — лише з дієсловами теперішнього часу: рід гравця нам невідомий («Оля встав» ріже вухо).
            Finish(winner, $"{Info.Title}: {nick} встає з-за столу, перемога — {Nick(winner)}",
                $"За столом лишається тільки {Nick(winner)} — перемога.", revealed: false);
            return;
        }
        if (_phase is DicePhase.Shake or DicePhase.Bid)
        {
            var starter = _core.Alive[_core.Starter] ? _core.Starter : _core.NextAlive(seat);
            StartRound(starter, Ctx.Clock.UtcNow, reshake: true, leave: $"{DiceSay.Cap(nick)} встає з-за столу — перетрушуємо");
            return;
        }
        if (_reveal is { } o && o.Next == seat) o.Next = _core.NextAlive(seat);
    }

    // ---------- вид ----------

    public override object View(int? seat)
    {
        var me = seat is { } s && s >= 0 && s < DiceCore.MaxSeats && _core.Dealt[s] ? s : -1;
        var players = new List<object>(DiceCore.MaxSeats);
        for (var i = 0; i < DiceCore.MaxSeats; i++)
        {
            if (!_core.Dealt[i]) continue;
            players.Add(new
            {
                seat = i,
                nick = Nick(i),
                dice = _core.Count[i],
                alive = _core.Alive[i],
                left = _core.Left[i],
                palificoUsed = _core.PalificoUsed[i],
                wasAtOne = _core.WasAtOne[i],
                sleepy = _sleep[i] >= SleepyAfter && _core.Alive[i],
            });
        }

        // Після кінця партії «мої» — лише те, що й так показали на розкритті. Дограли виходом посеред ставок —
        // рук ніхто не бачив, і в перевідкритому лобі на місці переможця вже може сидіти новенький: не кажемо нічого.
        int[]? my = null;
        if (me >= 0)
            my = _reveal is not null && _phase is DicePhase.Reveal or DicePhase.Done
                ? (int[])_reveal.Hands[me].Clone()
                : _phase == DicePhase.Done ? [] : _core.HandOf(me);

        var bid = _core.Bid;
        var canExact = me >= 0 && _phase == DicePhase.Bid && _exactRule && bid is { } b && b.Seat != me
            && _core.Alive[me] && _core.Count[me] < _core.Dice;

        var ready = new List<int>(DiceCore.MaxSeats);
        if (_phase == DicePhase.Reveal)
            for (var i = 0; i < DiceCore.MaxSeats; i++) if (_core.Ready[i]) ready.Add(i);

        var history = new object[_core.History.Count];
        for (var i = 0; i < history.Length; i++) history[i] = BidView(_core.History[i]);

        object? reveal = null;
        if (_reveal is { } o)
        {
            var dice = new int[DiceCore.MaxSeats][];
            for (var i = 0; i < dice.Length; i++) dice[i] = (int[])o.Hands[i].Clone();
            reveal = new
            {
                kind = o.Kind,
                caller = o.Caller,
                bid = BidView(o.Bid),
                count = o.Count,
                jokers = o.Jokers,
                dice,
                loser = o.Loser,
                gainer = o.Gainer,
                @out = o.Out,
                next = o.Next,
                say = o.Say,
            };
        }

        return new
        {
            turn = _phase == DicePhase.Bid ? _core.Turn : (int?)null,
            phase = PhaseName(_phase),
            round = _core.Round,
            endsAt = _endsAt,
            phaseMs = _phaseMs,
            rules = new { dice = _core.Dice, turnMs = _turnMs, exact = _exactRule, palifico = _palificoRule },
            palifico = _core.Palifico,
            wild = !_core.Palifico,
            starter = _core.Starter,
            total = _core.Total,
            players,
            my,
            bid = bid is { } cur ? BidView(cur) : null,
            history,
            canExact,
            ready,
            note = _phase == DicePhase.Shake ? _note : null,
            react = (int[])_reactE.Clone(),
            reactN = (int[])_reactN.Clone(),
            reveal,
            result = _result is { } r ? new { winner = r.Winner, places = r.Places, rounds = r.Rounds, say = r.Say, fun = r.Fun } : null,
        };
    }

    /// <summary>
    /// Публічний крихітний кадр: фаза, хід, час і лічильники кісточок. Жодної грані. Тик його не шле (усе це є у
    /// виді, а модуль кадрів не читає) — лишився на випадок, якщо каркас колись попросить кадр сам.
    /// </summary>
    public override object? Frame() => new
    {
        ph = PhaseName(_phase),
        turn = _phase == DicePhase.Bid ? _core.Turn : (int?)null,
        endsAt = _endsAt,
        n = (int[])_core.Count.Clone(),
    };

    static object BidView(DiceBid b) => new { seat = b.Seat, q = b.Q, f = b.F, auto = b.Auto };

    static string PhaseName(DicePhase phase) => phase switch
    {
        DicePhase.Shake => "shake",
        DicePhase.Bid => "bid",
        DicePhase.Reveal => "reveal",
        _ => "done",
    };

    // ---------- перебіг ----------

    void SetTimer(DateTimeOffset now, int ms)
    {
        _endsAt = now.AddMilliseconds(ms);
        _phaseMs = ms;
    }

    /// <summary>Час на хід місця: повний, а сонному (двічі поспіль проспав) — десять секунд, поки не прокинеться.</summary>
    int TurnMs(int seat) =>
        seat is >= 0 and < DiceCore.MaxSeats && _sleep[seat] >= SleepyAfter ? Math.Min(SleepyTurnMs, _turnMs) : _turnMs;

    void StartRound(int starter, DateTimeOffset now, bool reshake = false, string? leave = null)
    {
        _core.NewRound(Ctx.Rng, starter, _palificoRule, reshake);
        _phase = DicePhase.Shake;
        SetTimer(now, ShakeMs);
        _reveal = null;
        var who = Nick(starter);
        var line = _core.Palifico
            ? $"Паліфіко! {DiceSay.Cap(who)} на одній кісточці: глечики — звичайні одиниці, а грань не міняють."
            : $"Раунд {_core.Round}. Починає {who}";
        _note = leave is null ? line : $"{leave}. {line}";
        _dirty = true;
    }

    /// <summary>Час ходу вийшов: на відкритті — найнижча ставка за гравця (⏰), зі ставкою на столі — «Брешеш!» за нього.</summary>
    void Timeout(DateTimeOffset now)
    {
        var sleeper = _core.Turn;
        _sleep[sleeper]++;
        _timeouts[sleeper]++;
        if (_core.Bid is null)
        {
            var low = _core.Lowest(sleeper);
            _core.PlaceBid(low.Seat, low.Q, low.F, auto: true);
            SetTimer(now, TurnMs(_core.Turn));
            _dirty = true;
            return;
        }
        Reveal("timeout", sleeper, now);
    }

    void Reveal(string kind, int caller, DateTimeOffset now)
    {
        var o = _core.Resolve(kind, caller);
        o.Say = DiceSay.Reveal(Ctx.Rng, o, Nick);
        _reveal = o;
        _phase = DicePhase.Reveal;
        SetTimer(now, RevealMs);
        _dirty = true;
        // Вдале «Точно!» завжди повертає кісточку (з повним глеком його не сказати) — ачівка саме тут.
        if (o.Gainer is { } g)
        {
            Ctx.Award(g, 0, "ach:dice-exact");
            _exacts[g]++;
        }
        // Брехню розкрили: автор ставки загнув. Найбільший загин партії — у підсумок (різниця від двох, бо
        // «на одну більше» — це ще не блеф, а сміливість).
        if (kind != "exact" && o.Loser == o.Bid.Seat)
        {
            if (kind == "liar") _catches[caller]++;
            var gap = o.Bid.Q - o.Count;
            if (gap >= 2 && gap > _bluff.Q - _bluff.Count) _bluff = (o.Bid.Seat, o.Bid.Q, o.Bid.F, o.Count);
        }
    }

    void AfterReveal(DateTimeOffset now)
    {
        if (_core.AliveCount <= 1)
        {
            Done();
            return;
        }
        StartRound(_reveal?.Next ?? _core.NextAlive(_core.Starter), now);
    }

    /// <summary>Лишився один із кісточками — його перемога. Журнал — один рядок із <c>Finish</c>, без балачки.</summary>
    void Done()
    {
        var winner = _core.FirstAlive();
        var dealt = 0;
        for (var s = 0; s < DiceCore.MaxSeats; s++) if (_core.Dealt[s]) dealt++;
        var rounds = _core.Round;
        string log;
        if (dealt == 2)
        {
            var other = -1;
            for (var s = 0; s < DiceCore.MaxSeats; s++) if (_core.Dealt[s] && s != winner) other = s;
            log = $"{Info.Title}: {Nick(winner)} {_core.Count[winner]}:0 {Nick(other)} за {DiceSay.Rounds(rounds)}";
        }
        else
        {
            var gone = string.Join(", ", _core.Places.Select(Nick));
            log = $"{Info.Title}: перемога — {Nick(winner)}; вибули по черзі: {gone} ({DiceSay.Rounds(rounds)})";
        }
        Finish(winner, log, DiceSay.Victory(Ctx.Rng, Nick(winner), rounds, _core.Count[winner], dealt == 2), revealed: true);
    }

    /// <summary>
    /// Кінець партії. <paramref name="revealed"/> — дограли за столом (останнє розкриття); false — суперник просто
    /// встав. Ачівку «З однієї кісточки» даємо лише за перше: інакше її фармили б змовою «я спускаюсь до однієї,
    /// ти встаєш».
    /// </summary>
    void Finish(int winner, string log, string say, bool revealed)
    {
        _phase = DicePhase.Done;
        _dirty = true;
        _result = new DiceResult(winner, [.. _core.Places], _core.Round, say, Fun());
        var scores = new Dictionary<int, long>(DiceCore.MaxSeats);
        for (var s = 0; s < DiceCore.MaxSeats; s++)
            if (_core.Dealt[s]) scores[s] = s == winner ? _core.Count[s] : 0;
        if (revealed && winner >= 0 && _core.WasAtOne[winner]) Ctx.Award(winner, 0, "ach:dice-comeback");
        Ctx.Finish(winner >= 0 ? [winner] : [], log, scores);
    }

    /// <summary>
    /// Смішні нагороди під п'єдесталом — привід для балачки й реваншу: найнахабніший розкритий блеф, снайпер
    /// «Точно!», нюх на брехню, соня. Лише те, що справді було; нічия в лічильнику — перший за місцем.
    /// </summary>
    string[] Fun()
    {
        var list = new List<string>(4);
        if (_bluff.Seat >= 0)
            list.Add($"🤥 Найнахабніший блеф — {Nick(_bluff.Seat)}: {DiceCore.BidText(_bluff.Q, _bluff.F)}, а було {_bluff.Count}");
        if (Best(_exacts, 1) is { } sn)
            list.Add($"🎯 Снайпер — {Nick(sn)}: {_exacts[sn]} {DiceSay.Plural(_exacts[sn], "влучне", "влучні", "влучних")} «Точно!»");
        if (Best(_catches, 2) is { } nose)
            list.Add($"🕵 Нюх на брехню — {Nick(nose)}: {_catches[nose]} {DiceSay.Plural(_catches[nose], "раз", "рази", "разів")} «Брешеш!» у яблучко");
        if (Best(_timeouts, 2) is { } sleepy)
            list.Add($"😴 Соня — {Nick(sleepy)}: {_timeouts[sleepy]} {DiceSay.Plural(_timeouts[sleepy], "хід", "ходи", "ходів")} проспано");
        return [.. list];
    }

    int? Best(int[] counts, int min)
    {
        int? best = null;
        for (var s = 0; s < DiceCore.MaxSeats; s++)
            if (_core.Dealt[s] && counts[s] >= min && (best is null || counts[s] > counts[best.Value])) best = s;
        return best;
    }

    /// <summary>
    /// Нік місця: спершу той, що сидів на старті партії (місце того, хто встав, каркас звільняє одразу, а в
    /// дограній кімнаті на нього вже може сісти новенький), далі — хто сидить зараз, далі — «третій».
    /// </summary>
    string Nick(int seat) =>
        (seat is >= 0 and < DiceCore.MaxSeats ? (_core.Dealt[seat] ? _nicks[seat] : null) ?? Ctx.NickOf(seat) : null) ?? SeatName(seat);

    // ---------- payload ----------

    /// <summary>Ставка на дроті — рівно <c>{ q, f }</c> JSON-цілими: «3», 2.5 чи рядок — «Не зрозумів ставки».</summary>
    static bool TryBid(JsonElement p, out int q, out int f)
    {
        q = f = 0;
        return p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("q", out var jq) && jq.ValueKind == JsonValueKind.Number && jq.TryGetInt32(out q)
            && p.TryGetProperty("f", out var jf) && jf.ValueKind == JsonValueKind.Number && jf.TryGetInt32(out f);
    }

    static bool Matches(JsonElement p, DiceBid bid) => TryBid(p, out var q, out var f) && q == bid.Q && f == bid.F;
}

/// <summary>
/// Слово Дядька Глека на картці розкриття (у балачку — ні: урок «Скільки?» 23.09). Ніки — лише в називному
/// і лише з дієсловами теперішнього часу: рід гравця нам невідомий, а «Оля не повірив» ріже вухо.
/// Нік на початку речення — з великої (<c>{C}</c>, <c>{B}</c>, <c>{W}</c>): гостьове «гість Ярина» інакше
/// починало б речення з малої. Фразу обирає <c>Ctx.Rng</c> — після кидків раунду, тож руки від вибору фрази
/// не зсуваються.
/// </summary>
public static class DiceSay
{
    static readonly string[][] Forms =
    [
        ["глечик", "глечики", "глечиків"],
        ["двійка", "двійки", "двійок"],
        ["трійка", "трійки", "трійок"],
        ["четвірка", "четвірки", "четвірок"],
        ["п'ятірка", "п'ятірки", "п'ятірок"],
        ["шістка", "шістки", "шісток"],
    ];
    static readonly string[] Zero = ["жодного глечика", "жодної двійки", "жодної трійки", "жодної четвірки", "жодної п'ятірки", "жодної шістки"];

    // {c} — хто сказав «Брешеш!»/«Точно!», {b} — автор ставки ({C}/{B} — те саме з великої, на початку речення),
    // {n} — «3 п'ятірки», {ln} — «лише 3 п'ятірки» або «жодної п'ятірки» (не «лише жодної»), {q} — скільки
    // ставили, {k} — скільки є.
    static readonly string[] TrueBid =
    [
        "{C} не вірить — а даремно: на столі {n}.",
        "Чесна ставка: {n}. {C} віддає кісточку.",
        "Було {k}, ставили {q}. {C} платить кісточкою.",
    ];
    static readonly string[] FalseBid =
    [
        "Розкусили! На столі {ln}. {B} платить кісточкою.",
        "{B} загинає: {q}, а є {k}. Кісточка геть.",
        "Блеф не пройшов — {b} віддає кісточку.",
    ];
    static readonly string[] ExactHit =
    [
        "Точнісінько {k}! {C} повертає кісточку.",
        "Влучне око: {c} попадає рівно в {n}.",
    ];
    static readonly string[] ExactMiss =
    [
        "Не рівно: {k}, а не {q}. {C} платить кісточкою.",
        "Мимо: на столі {n}, а не {q} — {c} віддає кісточку.",
    ];
    static readonly string[] SleepTrue = ["Ставка чесна ({n}) — мінус кісточка."];
    static readonly string[] SleepFalse = ["А мовчання врятувало: на столі {ln}. {B} платить кісточкою."];
    static readonly string[] Wins = ["Останній глек — {w}. Партія за {r}.", "{W} перетрушує всіх."];

    /// <summary>Перша літера — велика: нік на початку речення («гість Ярина…» → «Гість Ярина…»).</summary>
    public static string Cap(string s) =>
        s.Length > 0 && char.IsLower(s[0]) ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    /// <summary>«3 п'ятірки», «1 глечик», «жодної шістки».</summary>
    public static string Faces(int count, int face)
    {
        var i = Math.Clamp(face, 1, 6) - 1;
        return count <= 0 ? Zero[i] : $"{count} {Plural(count, Forms[i][0], Forms[i][1], Forms[i][2])}";
    }

    /// <summary>«1 раунд», «3 раунди», «7 раундів».</summary>
    public static string Rounds(int n) => $"{n} {Plural(n, "раунд", "раунди", "раундів")}";

    /// <summary>«1 кісточку», «3 кісточки», «5 кісточок» (знахідний після «лишає»).</summary>
    public static string DiceLeft(int n) => $"{n} {Plural(n, "кісточку", "кісточки", "кісточок")}";

    public static string Plural(int n, string one, string few, string many)
    {
        var d = n % 10;
        var h = n % 100;
        if (d == 1 && h != 11) return one;
        if (d is >= 2 and <= 4 && h is < 12 or > 14) return few;
        return many;
    }

    public static string Reveal(Random rng, DiceOutcome o, Func<int, string> nick)
    {
        var b = o.Bid;
        var callerLost = o.Loser == o.Caller;
        string[] bank = o.Kind switch
        {
            "exact" => o.Gainer is not null ? ExactHit : ExactMiss,
            "timeout" => callerLost ? SleepTrue : SleepFalse,
            _ => callerLost ? TrueBid : FalseBid,
        };
        var line = Fill(bank[rng.Next(bank.Length)], nick(o.Caller), nick(b.Seat), o.Count, b);
        if (o.Kind == "timeout") line = $"Час вийшов: {nick(o.Caller)} мовчить — рахуємо як «Брешеш!». {line}";
        if (o.Out && o.Loser is { } l) line += $" {Cap(nick(l))} лишається без кісточок — дивиться далі.";
        return line;
    }

    public static string Victory(Random rng, string winner, int rounds, int left, bool duel) =>
        duel
            ? $"Дуель скінчилась: {winner} лишає {DiceLeft(left)}."
            : Wins[rng.Next(Wins.Length)].Replace("{W}", Cap(winner)).Replace("{w}", winner).Replace("{r}", Rounds(rounds));

    static string Fill(string text, string caller, string bidder, int count, DiceBid bid) => text
        .Replace("{C}", Cap(caller))
        .Replace("{B}", Cap(bidder))
        .Replace("{c}", caller)
        .Replace("{b}", bidder)
        .Replace("{ln}", count > 0 ? "лише " + Faces(count, bid.F) : Faces(count, bid.F))
        .Replace("{n}", Faces(count, bid.F))
        .Replace("{q}", bid.Q.ToString(CultureInfo.InvariantCulture))
        .Replace("{k}", count.ToString(CultureInfo.InvariantCulture));
}
