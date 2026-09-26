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

    public override GameInfo Info { get; } = new(
        "dice", "Під глеком", "«Під глеком»", GameGroup.Party, 2, DiceCore.MaxSeats,
        TickMs: TickMs, Start: StartMode.ByHost, Hidden: true, Private: false, Persistent: false, Rated: false,
        Score: ScoreOrder.None,
        Options:
        [
            new GameOption("dice", "Кісточок у кожного", [("5", "5 — класика"), ("3", "3 — швидка партія")], "5"),
            new GameOption("turn", "Час на хід", [("30", "30 с"), ("45", "45 с"), ("60", "60 с")], "30"),
            new GameOption("exact", "«Точно!»", [("on", "є"), ("off", "нема")], "on"),
            new GameOption("palifico", "Паліфіко", [("on", "є"), ("off", "нема")], "on"),
        ],
        Hint: "Брехливі кості: у кожного п'ять кісточок під глеком, бачиш лише свої. Став, скільки їх на столі, або кажи «Брешеш!». Одиниці — джокери");

    static readonly string[] Names = ["перший", "другий", "третій", "четвертий", "п'ятий", "шостий"];

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

    /// <summary>Підсумок партії у виді: переможець, порядок вибування, скільки раундів, слово Глека.</summary>
    sealed record DiceResult(int Winner, int[] Places, int Rounds, string Say);

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
        // Хто починає перший раунд — випадкове живе місце (один виклик Rng, до кидків).
        var starter = seats[Ctx.Rng.Next(seats.Count)];
        StartRound(starter, Ctx.Clock.UtcNow);
    }

    // ---------- дії ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action is not ("bid" or "liar" or "exact" or "ready")) return ActResult.Fail("Тут так не ходять");
        if (_phase == DicePhase.Done) return ActResult.Fail("Партію зіграно, тисни «Ще раз»");
        if (_phase == DicePhase.Shake) return ActResult.Fail("Ще трусимо глеки");
        if (seat is < 0 or >= DiceCore.MaxSeats || !_core.Dealt[seat] || !_core.Alive[seat])
            return ActResult.Fail("Ти без кісточок — дивись і вболівай");

        var now = Ctx.Clock.UtcNow;
        if (_phase == DicePhase.Reveal)
        {
            if (action != "ready") return ActResult.Fail(action == "exact" ? Stale : "Глеки вже підняли — рахуємо");
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
                SetTimer(now, _turnMs);
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
    /// Три порівняння й кінець: фаза вийшла за часом, або всі живі натиснули «Далі». Жодних алокацій; види
    /// летять лише тоді, коли щось змінилось (<see cref="_dirty"/>).
    /// </summary>
    public override TickResult Tick()
    {
        if (_phase == DicePhase.Done) return TickResult.None;
        var now = Ctx.Clock.UtcNow;
        switch (_phase)
        {
            case DicePhase.Shake when now >= _endsAt:
                _phase = DicePhase.Bid;
                SetTimer(now, _turnMs);
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
        return TickResult.Both;
    }

    /// <summary>
    /// Встав з-за столу — вибуває цієї ж миті. Лишився один — перемога йому. Посеред трусіння чи ставок раунд
    /// перетрушується без того, хто пішов (кількості на столі вже інші, а ставки пропадають); розкриття
    /// дограється як є.
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
            Finish(winner, $"{Info.Title}: {nick} встав з-за столу, перемога — {Nick(winner)}",
                $"{Nick(winner)} лишається за столом сам — перемога.");
            return;
        }
        if (_phase is DicePhase.Shake or DicePhase.Bid)
        {
            StartRound(_core.NextAlive(seat), Ctx.Clock.UtcNow, reshake: true, leave: $"{nick} встав з-за столу — перетрушуємо");
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
            });
        }

        int[]? my = null;
        if (me >= 0)
            my = _reveal is not null && _phase is DicePhase.Reveal or DicePhase.Done
                ? (int[])_reveal.Hands[me].Clone()
                : _core.HandOf(me);

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
            reveal,
            result = _result is { } r ? new { winner = r.Winner, places = r.Places, rounds = r.Rounds, say = r.Say } : null,
        };
    }

    /// <summary>Публічний крихітний кадр: фаза, хід, час і лічильники кісточок. Жодної грані. Летить лише разом із видами.</summary>
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

    void StartRound(int starter, DateTimeOffset now, bool reshake = false, string? leave = null)
    {
        _core.NewRound(Ctx.Rng, starter, _palificoRule, reshake);
        _phase = DicePhase.Shake;
        SetTimer(now, ShakeMs);
        _reveal = null;
        var who = Nick(starter);
        var line = _core.Palifico
            ? $"Паліфіко! {who} на одній кісточці: глечики — звичайні одиниці, а грань не міняють."
            : $"Раунд {_core.Round}. Починає {who}";
        _note = leave is null ? line : $"{leave}. {line}";
        _dirty = true;
    }

    /// <summary>Час ходу вийшов: на відкритті — найнижча ставка за гравця (⏰), зі ставкою на столі — «Брешеш!» за нього.</summary>
    void Timeout(DateTimeOffset now)
    {
        if (_core.Bid is null)
        {
            var low = _core.Lowest(_core.Turn);
            _core.PlaceBid(low.Seat, low.Q, low.F, auto: true);
            SetTimer(now, _turnMs);
            _dirty = true;
            return;
        }
        Reveal("timeout", _core.Turn, now);
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
        if (o.Gainer is { } g) Ctx.Award(g, 0, "ach:dice-exact");
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
        Finish(winner, log, DiceSay.Victory(Ctx.Rng, Nick(winner), rounds, _core.Count[winner], dealt == 2));
    }

    void Finish(int winner, string log, string say)
    {
        _phase = DicePhase.Done;
        _dirty = true;
        _result = new DiceResult(winner, [.. _core.Places], _core.Round, say);
        var scores = new Dictionary<int, long>(DiceCore.MaxSeats);
        for (var s = 0; s < DiceCore.MaxSeats; s++)
            if (_core.Dealt[s]) scores[s] = s == winner ? _core.Count[s] : 0;
        if (winner >= 0 && _core.WasAtOne[winner]) Ctx.Award(winner, 0, "ach:dice-comeback");
        Ctx.Finish(winner >= 0 ? [winner] : [], log, scores);
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
/// Фразу обирає <c>Ctx.Rng</c> — після кидків раунду, тож руки від вибору фрази не зсуваються.
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

    // {c} — хто сказав «Брешеш!»/«Точно!», {b} — автор ставки, {n} — «3 п'ятірки», {q} — скільки ставили, {k} — скільки є.
    static readonly string[] TrueBid =
    [
        "{c} не вірить — а даремно: на столі {n}.",
        "Чесна ставка: {n}. {c} віддає кісточку.",
        "Було {k}, ставили {q}. {c} платить кісточкою.",
    ];
    static readonly string[] FalseBid =
    [
        "Розкусили! На столі лише {n}. {b} платить кісточкою.",
        "{b} загинає: {q}, а є {k}. Кісточка геть.",
        "Блеф не пройшов — {b} віддає кісточку.",
    ];
    static readonly string[] ExactHit =
    [
        "Точнісінько {k}! {c} повертає кісточку.",
        "Око-алмаз: {c} влучає рівно в {n}.",
    ];
    static readonly string[] ExactMiss =
    [
        "Не рівно: {k}, а не {q}. {c} платить кісточкою.",
        "Мимо: на столі {n}, а не {q} — {c} віддає кісточку.",
    ];
    static readonly string[] SleepTrue = ["Ставка чесна ({n}) — мінус кісточка."];
    static readonly string[] SleepFalse = ["А мовчання врятувало: лише {n}. {b} платить кісточкою."];
    static readonly string[] Wins = ["Останній глек — {w}. Партія за {r}.", "{w} перетрушує всіх."];

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
        if (o.Out && o.Loser is { } l) line += $" {nick(l)} лишається без кісточок — дивиться далі.";
        return line;
    }

    public static string Victory(Random rng, string winner, int rounds, int left, bool duel) =>
        duel
            ? $"Дуель скінчилась: {winner} лишає {DiceLeft(left)}."
            : Wins[rng.Next(Wins.Length)].Replace("{w}", winner).Replace("{r}", Rounds(rounds));

    static string Fill(string text, string caller, string bidder, int count, DiceBid bid) => text
        .Replace("{c}", caller)
        .Replace("{b}", bidder)
        .Replace("{n}", Faces(count, bid.F))
        .Replace("{q}", bid.Q.ToString(CultureInfo.InvariantCulture))
        .Replace("{k}", count.ToString(CultureInfo.InvariantCulture));
}
