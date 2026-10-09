using System.Collections.Concurrent;
using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Ставка одного гравця на раунд. Соті: <c>Auto</c> — автозабір, <c>Out</c> — на чому забрав.</summary>
public sealed class LelkaBet
{
    public string Nick { get; set; } = "";
    public int Amount { get; set; }
    public int? Auto { get; set; }
    public int? Out { get; set; }
    public int Win { get; set; }
    /// <summary>Каса вже списала ставку (після закриття прийому; відповідь черги каси приходить за мить після зльоту).</summary>
    public bool Taken { get; set; }
}

public sealed class LelkaHist
{
    public int Round { get; set; }
    public double Crash { get; set; }
}

public sealed class LelkaState
{
    public string Phase { get; set; } = Lelka.Bets;
    public int Round { get; set; }
    public string Epoch { get; set; } = "";
    public DateTimeOffset? Until { get; set; }
    public DateTimeOffset? StartAt { get; set; }
    public string Seed { get; set; } = "";
    public string Hash { get; set; } = "";
    /// <summary>Точка падіння, соті. Таємниця до падіння: у кадр не йде.</summary>
    public int Crash { get; set; } = 100;
    public List<LelkaBet> Bets { get; set; } = [];
    public List<LelkaHist> History { get; set; } = [];
    public Dictionary<string, int> Wallets { get; set; } = [];
    public Dictionary<string, string> Notes { get; set; } = [];
    public LelkaRound? Pending { get; set; }
    public DateTimeOffset? JournalAt { get; set; }
}

/// <summary>
/// Лелека — crash на всіх (docs/games/specs/lelka.md). Дядько Глек веде стіл за розкладом: ставки 8 с → політ (множник
/// e^(0,075·t)) → «шубовсть» 2 с → пауза 3 с → ставки… Точка падіння вирішена на початку прийому (видно лише її sha256),
/// seed — після падіння. Гроші — через <see cref="LelkaBook"/>: списання при закритті прийому, виграш — щойно забрано.
/// Тік у базу не ходить: усе грошове — у черзі каси, відповіді стіл забирає зі своєї скриньки (<see cref="Inbox"/>) на тіку.
/// Один спільний стіл на сайт: <see cref="ISharedTable"/> — «Сісти» веде за наявний стіл, якщо він є.
/// </summary>
public sealed class Lelka : Game, ISharedTable
{
    public const int TickMs = 100;
    public const int BetMs = 8_000, CrashMs = 2_000, PauseMs = 3_000, FrameEveryMs = 1_000;
    public const int HistoryLen = 20, MaxSeats = 12;
    /// <summary>«Великий виграш» — від ×10; «велика ставка» для «пролетів» — від 300; політ у Балачки сайту — від ×50.</summary>
    public const int BigCents = 1_000, BigLoss = 300, SiteCents = 5_000, BraveCents = 10;
    public static readonly TimeSpan JournalGap = TimeSpan.FromMinutes(5);

    public const string Bets = "bets", Flight = "flight", Crashed = "crash", Pause = "pause";
    public const string ActBet = "bet", ActCancel = "cancel", ActCash = "cash", ActAuto = "auto";

    public const string ClosedText = "Каса зачинена — спробуй трохи згодом";
    public const string OffText = "Лелека відпочиває — ставок зараз не приймаю";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override GameInfo Info { get; } = new(
        "lelka", "Лелека", "лелеку", GameGroup.Party, 1, MaxSeats, TickMs: TickMs, Start: StartMode.Immediate, Hidden: true,
        Score: ScoreOrder.HigherIsBetter, Coop: true,
        Hint: "Лелека несе Глека вгору, множник росте — забери черепки, поки не впустила. Один політ на всіх");

    LelkaState S = new();
    LelkaBook? _book;
    bool _dirty, _settledOnLoad;
    /// <summary>Відповіді черги каси (списання, гаманці) — стан столу міняємо лише під замком кімнати, на тіку чи дії.</summary>
    readonly ConcurrentQueue<Action> _inbox = new();
    DateTimeOffset _framedAt;

    /// <summary>Шов лише для тестів: точка падіння наступних раундів (соті), seed тоді нічого не важить.</summary>
    public Func<int>? Rig { get; set; }
    public LelkaState State => S;

    DateTimeOffset Now => Ctx.Clock.UtcNow;
    LelkaOptions Opts => _book?.Options ?? new LelkaOptions();
    string Table => $"{Ctx.RoomId}:{S.Epoch}";

    public override string SeatName(int seat) => $"місце {seat + 1}";

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        try { _book = Ctx?.Services?.GetService<LelkaBook>(); }
        catch { _book = null; }
        if (_book is { Options.Enabled: false }) throw new GameError(OffText);
    }

    /// <summary>Чистий стіл. Історія падінь, номер раунду й пауза Журналу переживають «Ще раз».</summary>
    public override void Start()
    {
        var keep = S;
        S = new LelkaState { Round = keep.Round, History = keep.History, JournalAt = keep.JournalAt, Epoch = NewEpoch() };
        OpenBets(Now);
    }

    public override bool Resumable => true;
    public override bool LateJoin(string nick) => true;
    public override string? LateJoinGreeting(string nick) => "Сідай! Ставки — поки лелека на землі, забирати — поки летить";
    public override void OnJoin(int seat) => Refresh(seat);
    /// <summary>Не техпоразка: ставка лишається й летить (автозабір спрацює й без людини).</summary>
    public override void OnLeave(int seat) => _dirty = true;
    /// <summary>Стіл без людей ще долітає: найдовше — 8 с прийому + 92 с польоту + 5 с.</summary>
    public override TimeSpan HoldEmpty => TimeSpan.FromSeconds(120);

    // ---------- дії ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (_book is null) return ActResult.Fail(ClosedText);
        Inbox();
        if (action is not (ActBet or ActCancel or ActCash or ActAuto)) return ActResult.Fail("Тут так не ходять");
        if (Ctx.NickOf(seat) is not { } nick) return ActResult.Fail("Тут так не ходять");
        var key = Rooms.NickKey(nick);
        var wallet = _book.Balance(nick);
        S.Wallets[key] = wallet - Reserved(key);
        var now = Now;
        var r = action switch
        {
            ActBet => Bet(nick, key, payload, wallet, now),
            ActCancel => Cancel(key, now),
            ActCash => Cash(key, now),
            _ => Auto(key, payload, now),
        };
        if (r.Ok) _dirty = true;
        return r;
    }

    bool Open(DateTimeOffset now) => S.Phase == Bets && S.Until is { } u && now < u;

    ActResult Bet(string nick, string key, JsonElement payload, int wallet, DateTimeOffset now)
    {
        if (!Open(now)) return ActResult.Fail("Лелека вже злітає — став на наступний політ");
        var o = Opts;
        if (!o.Enabled) return ActResult.Fail(OffText);
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("amount", out var a) || a.ValueKind != JsonValueKind.Number
            || !a.TryGetInt32(out var amount)) return ActResult.Fail("Ставка — ціле число черепків");
        if (amount < o.MinBet) return ActResult.Fail($"Найменша ставка — {o.MinBet} 🏺");
        if (o.MaxBet > 0 && amount > o.MaxBet) return ActResult.Fail($"Найбільша ставка — {o.MaxBet} 🏺");
        if (amount > wallet) return ActResult.Fail($"Бракує черепків: у гаманці {Math.Max(0, wallet)}");
        int? auto = null;
        if (payload.TryGetProperty("auto", out var ae) && ae.ValueKind != JsonValueKind.Null)
        {
            if (ReadAuto(ae, out auto) is { } bad) return ActResult.Fail(bad);
        }
        var bet = BetOf(key);
        if (bet is null)
        {
            bet = new LelkaBet { Nick = nick };
            S.Bets.Add(bet);
        }
        bet.Nick = nick;
        bet.Amount = amount;
        bet.Auto = auto;
        S.Wallets[key] = wallet - amount;
        S.Notes.Remove(key);
        return ActResult.Done;
    }

    ActResult Cancel(string key, DateTimeOffset now)
    {
        if (!Open(now)) return ActResult.Fail("Пізно — ставки вже в небі");
        if (BetOf(key) is not { } bet) return ActResult.Fail("Нема чого скасовувати");
        S.Bets.Remove(bet);
        S.Wallets[key] += bet.Amount;
        return ActResult.Done;
    }

    ActResult Cash(string key, DateTimeOffset now)
    {
        if (S.Phase != Flight) return ActResult.Fail(S.Phase == Bets ? "Лелека ще на землі" : "Пізно — лелека вже впустила глека");
        if (BetOf(key) is not { } bet) return ActResult.Fail("Твоєї ставки в цьому польоті нема");
        if (!bet.Taken) return ActResult.Fail("Каса ще списує ставку — ще мить");
        if (bet.Out is { } was) return ActResult.Fail($"Уже забрано на ×{Fmt(was)}");
        var m = CentsNow(now);
        if (m >= S.Crash) return ActResult.Fail("Пізно — лелека вже впустила глека");
        CashOut(bet, m);
        return ActResult.Accept($"Є! ×{Fmt(m)}: +{bet.Win} 🏺");
    }

    ActResult Auto(string key, JsonElement payload, DateTimeOffset now)
    {
        if (S.Phase is not (Bets or Flight) || (S.Phase == Bets && !Open(now))) return ActResult.Fail("Зараз не можна");
        if (BetOf(key) is not { } bet) return ActResult.Fail("Спершу постав");
        if (bet.Out is not null) return ActResult.Fail("Уже забрано");
        int? auto = null;
        var x = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("x", out var xe) ? xe : default;
        if (x.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) && ReadAuto(x, out auto) is { } bad) return ActResult.Fail(bad);
        bet.Auto = auto;
        // Посеред польоту поставив нижче, ніж уже є, — це «забрати зараз» за поточним множником.
        if (S.Phase == Flight && bet.Taken && auto is { } a)
        {
            var m = CentsNow(now);
            if (m < S.Crash && a <= m) CashOut(bet, m);
        }
        return ActResult.Done;
    }

    static string? ReadAuto(JsonElement e, out int? cents)
    {
        cents = null;
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out var x) || LelkaCore.Cents(x) is not { } c)
            return "Автозабір — число, як 2 чи 1.5";
        if (c < LelkaCore.MinAutoCents) return "Автозабір — від ×1,01";
        if (c > LelkaCore.MaxCents) return "Автозабір — до ×1000";
        cents = c;
        return null;
    }

    // ---------- розклад ----------

    public override TickResult Tick()
    {
        var now = Now;
        Inbox();
        switch (S.Phase)
        {
            case Bets when S.Until is { } u && now >= u:
                Close(now);
                break;
            case Flight:
                Fly(now);
                break;
            case Crashed when S.Until is { } u && now >= u:
                S.Phase = Pause;
                S.Until = now.AddMilliseconds(PauseMs);
                _dirty = true;
                break;
            case Pause when S.Until is { } u && now >= u:
                // Вимкнено — новий прийом не відкриваємо: стіл тихо стоїть, доки не ввімкнуть (без порожніх раундів).
                if (Opts.Enabled) OpenBets(now);
                else
                {
                    S.Until = null;
                    _dirty = true;
                }
                break;
            case Pause when S.Until is null && Opts.Enabled:
                OpenBets(now);
                break;
        }
        if (_dirty)
        {
            _dirty = false;
            _framedAt = now;
            return TickResult.Both;
        }
        if (S.Phase == Flight && now - _framedAt >= TimeSpan.FromMilliseconds(FrameEveryMs))
        {
            _framedAt = now;
            return TickResult.FrameOnly;
        }
        return TickResult.None;
    }

    /// <summary>Новий раунд: seed і його hash — уже зараз, точка падіння вирішена. Гаманці всіх, хто сидить, — свіжі.</summary>
    void OpenBets(DateTimeOffset now)
    {
        S.Round++;
        S.Seed = LelkaCore.NewSeed(Ctx.Seeded ? Ctx.Rng : null);
        S.Hash = LelkaCore.Hash(S.Seed);
        S.Crash = Rig?.Invoke() ?? LelkaCore.CrashCents(S.Seed);
        S.Bets.Clear();
        S.Notes.Clear();
        S.Pending = null;
        S.Phase = Bets;
        S.Until = now.AddMilliseconds(BetMs);
        S.StartAt = null;
        // Свіжі гаманці — з черги каси (не з тіку); прийдуть за мить, а доти — ті, що були.
        if (_book is not null)
        {
            var nicks = new List<string>();
            for (var seat = 0; seat < Info.MaxPlayers; seat++)
                if (Ctx.NickOf(seat) is { } nick) nicks.Add(nick);
            _book.Balances(nicks, all => _inbox.Enqueue(() => Wallets(all)));
            Inbox();
        }
        _dirty = true;
    }

    /// <summary>Гаманці з черги каси: вільне = гаманець − ще не списана ставка цього прийому.</summary>
    void Wallets(IReadOnlyDictionary<string, int> all)
    {
        foreach (var (nick, wallet) in all)
        {
            var key = Rooms.NickKey(nick);
            S.Wallets[key] = wallet - Reserved(key);
        }
        _dirty = true;
    }

    /// <summary>Забрати відповіді черги каси (під замком кімнати).</summary>
    void Inbox()
    {
        while (_inbox.TryDequeue(out var apply)) apply();
    }

    /// <summary>
    /// «Злітаємо!»: лелека летить одразу, а запис у касу й списання кожному — у черзі каси (<see cref="LelkaBook.Launch"/>).
    /// Поки відповіді нема, ставка летить, але забрати її не можна (автозабір спрацює за своїм множником, щойно списалось).
    /// Не записалось — ставки знято; комусь не списалось — його ставку знято, людині рядок (<see cref="Launched"/>).
    /// </summary>
    void Close(DateTimeOffset now)
    {
        S.Phase = Flight;
        S.StartAt = now;
        S.Until = null;
        _dirty = true;
        if (S.Bets.Count > 0)
        {
            if (_book is null)
            {
                foreach (var b in S.Bets) S.Notes[Rooms.NickKey(b.Nick)] = "Каса заїла — ставку не взято, черепки цілі";
                S.Bets.Clear();
                Ctx.Say(LelkaLines.BookStuck);
            }
            else
            {
                var pending = new LelkaRound(Table, S.Round, Info.Id, S.Crash, now, [.. S.Bets.Select(b => new LelkaPay(b.Nick, b.Amount, b.Auto, null))]);
                S.Pending = pending;
                _book.Launch(pending, res => _inbox.Enqueue(() => Launched(pending, res)));
                Inbox();
            }
        }
        Fly(now);
    }

    /// <summary>Відповідь каси на зліт: хто списався — летить по-справжньому, хто ні — ставку знято.</summary>
    void Launched(LelkaRound pending, IReadOnlyList<LelkaTake>? res)
    {
        if (S.Round != pending.Round || !string.Equals(Table, pending.Table, StringComparison.Ordinal)) return;
        _dirty = true;
        if (res is null)
        {
            foreach (var b in S.Bets) S.Notes[Rooms.NickKey(b.Nick)] = "Каса заїла — ставку не взято, черепки цілі";
            S.Bets.Clear();
            S.Pending = null;
            Ctx.Say(LelkaLines.BookStuck);
            return;
        }
        foreach (var t in res)
        {
            var key = Rooms.NickKey(t.Nick);
            var stake = pending.Pays.FirstOrDefault(p => Rooms.NickKey(p.Nick) == key)?.Stake ?? 0;
            var b = BetOf(key);
            if (t.Ok)
            {
                if (b is not null) b.Taken = true;
                S.Wallets[key] = Math.Max(0, t.Wallet - stake);
                continue;
            }
            S.Wallets[key] = t.Wallet;
            S.Notes[key] = $"Черепків не стало — ставку ({stake}) знято";
            if (b is not null) S.Bets.Remove(b);
        }
        if (S.Pending is { } p && p.Round == pending.Round)
            S.Pending = S.Bets.Count == 0 ? null : p with { Pays = [.. S.Bets.Select(b => new LelkaPay(b.Nick, b.Amount, b.Auto, null))] };
        // Автозабори, до яких долетіли, поки каса списувала, — зараз, за їхнім множником (Fly на цьому ж тіку).
    }

    /// <summary>Тик польоту: автозаборі (за своїм множником, якщо до нього долетіли), тоді — чи не впала.</summary>
    void Fly(DateTimeOffset now)
    {
        var m = LelkaCore.CentsAt((now - S.StartAt!.Value).TotalMilliseconds);
        var reach = Math.Min(m, S.Crash);
        foreach (var b in S.Bets)
            if (b.Taken && b.Out is null && b.Auto is { } a && a <= reach) CashOut(b, a);
        if (m >= S.Crash) Crash(now, quiet: false);
    }

    void CashOut(LelkaBet b, int cents)
    {
        b.Out = cents;
        b.Win = LelkaCore.Win(b.Amount, cents);
        var key = Rooms.NickKey(b.Nick);
        S.Wallets[key] = (int)Math.Min(int.MaxValue, (long)S.Wallets.GetValueOrDefault(key) + b.Win);
        if (S.Pending is { } p) _book?.Cash(p.Table, p.Round, b.Nick, b.Win);
        _dirty = true;
    }

    /// <summary>
    /// «Шубовсть»: розрахунок (поза замком), історія, ачівки, балачка, Журнал, Балачки сайту.
    /// <paramref name="quiet"/> — каса вже розрахувала раунд без столу (Load після довгої перерви): лише стан.
    /// </summary>
    void Crash(DateTimeOffset now, bool quiet)
    {
        foreach (var b in S.Bets.Where(b => b.Out is null)) b.Win = 0;
        // Ставка, на яку каса ще не відповіла, — без «Return»: розрахунок гляне в леджер і вирішить, як сироті
        // (автозабір ≤ точки — платить, падіння одразу — програш, інакше ставку назад; забрати ж людина не могла).
        if (S.Pending is { } p && !quiet)
            _book?.Settle(p with { Pays = [.. S.Bets.Select(b => new LelkaPay(b.Nick, b.Amount, b.Auto, b.Taken ? b.Win : null))] });
        S.Pending = null;
        S.Phase = Crashed;
        S.Until = now.AddMilliseconds(CrashMs);
        S.History.Insert(0, new LelkaHist { Round = S.Round, Crash = LelkaCore.X(S.Crash) });
        if (S.History.Count > HistoryLen) S.History.RemoveRange(HistoryLen, S.History.Count - HistoryLen);
        _dirty = true;
        if (!quiet) Announce(now);
    }

    void Announce(DateTimeOffset now)
    {
        var taken = S.Bets.Where(b => b.Taken).ToList();
        foreach (var b in taken)
        {
            if (SeatOf(Rooms.NickKey(b.Nick)) is not { } seat || b.Out is not { } o) continue;
            if (o >= BigCents) Ctx.Award(seat, 0, "ach:lelka-10");
            if (S.Crash - o <= BraveCents) Ctx.Award(seat, 0, "ach:lelka-brave");
            if (b.Win > b.Amount) Ctx.Score(seat, b.Win - b.Amount);
        }
        var x = Fmt(S.Crash);
        var star = taken.Where(b => b.Out >= BigCents).OrderByDescending(b => b.Win).FirstOrDefault();
        var flop = taken.Where(b => b.Out is null && b.Amount >= BigLoss).OrderByDescending(b => b.Amount).FirstOrDefault();
        string? journal = null;
        if (star is not null)
        {
            Ctx.Say(string.Format(Pick(LelkaLines.BigWin), star.Nick, Fmt(star.Out!.Value), star.Win));
            journal = $"🪶 Лелека: {star.Nick} — ×{Fmt(star.Out!.Value)}, +{star.Win} 🏺";
        }
        else if (flop is not null)
        {
            Ctx.Say(string.Format(Pick(S.Crash <= 100 ? LelkaLines.Instant : LelkaLines.Flop), flop.Nick, x, flop.Amount));
            journal = $"🪶 Лелека: {flop.Nick} — мінус {flop.Amount} 🏺, глек упав на ×{x}";
        }
        else if (S.Crash <= 100 && taken.Count > 0) Ctx.Say(Pick(LelkaLines.Instant0));
        if (journal is not null && (S.JournalAt is not { } at || now - at >= JournalGap))
        {
            S.JournalAt = now;
            Ctx.Log(journal);
        }
        if (S.Crash >= SiteCents && taken.Count > 0) _book?.Announce(string.Format(Pick(LelkaLines.Space), x));
    }

    // ---------- кадр і вид ----------

    int CentsNow(DateTimeOffset now) => S.Phase switch
    {
        Flight => Math.Max(100, Math.Min(LelkaCore.CentsAt((now - S.StartAt!.Value).TotalMilliseconds), S.Crash - 1)),
        Crashed or Pause => S.Crash,
        _ => 100,
    };

    bool Revealed => S.Phase is Crashed or Pause;

    Dictionary<string, object?> FrameData(DateTimeOffset now) => new()
    {
        ["phase"] = S.Phase,
        ["round"] = S.Round,
        ["now"] = now,
        ["until"] = S.Until,
        ["startAt"] = S.StartAt,
        ["k"] = LelkaCore.K,
        ["m"] = LelkaCore.X(CentsNow(now)),
        ["crash"] = Revealed ? LelkaCore.X(S.Crash) : null,
        ["hash"] = S.Hash,
        ["seed"] = Revealed ? S.Seed : null,
        ["bets"] = S.Bets.OrderByDescending(b => b.Amount).ThenBy(b => b.Nick, StringComparer.Ordinal).Select(BetDto).ToList(),
        ["history"] = S.History.Select(h => new { round = h.Round, crash = h.Crash }).ToList(),
    };

    static object BetDto(LelkaBet b) => new
    {
        nick = b.Nick,
        amount = b.Amount,
        auto = b.Auto is { } a ? LelkaCore.X(a) : (double?)null,
        @out = b.Out is { } o ? LelkaCore.X(o) : (double?)null,
        win = b.Win,
    };

    public override object? Frame() => FrameData(Now);

    public override object View(int? seat)
    {
        var view = FrameData(Now);
        var nick = seat is { } s ? Ctx.NickOf(s) : null;
        var key = nick is null ? null : Rooms.NickKey(nick);
        var o = Opts;
        view["mine"] = key is not null && BetOf(key) is { } b
            ? new { amount = b.Amount, auto = b.Auto is { } a ? LelkaCore.X(a) : (double?)null, @out = b.Out is { } x ? LelkaCore.X(x) : (double?)null, win = b.Win }
            : null;
        view["wallet"] = key is null ? null : S.Wallets.TryGetValue(key, out var w) ? Math.Max(0, w) : null;
        view["limits"] = new { min = o.MinBet, max = o.MaxBet };
        view["on"] = o.Enabled && _book is not null;
        view["note"] = key is not null && S.Notes.TryGetValue(key, out var note) ? note : null;
        return view;
    }

    // ---------- збереження ----------

    public override string? Save() => JsonSerializer.Serialize(S, Json);

    /// <summary>Новий epoch на кожне відновлення: раунд зі старого стану ніколи не перепише ключ уже списаного.</summary>
    public override void Load(string json)
    {
        var s = JsonSerializer.Deserialize<LelkaState>(json, Json) ?? throw new InvalidOperationException("порожній стан лелеки");
        if (s.Phase is not (Bets or Flight or Crashed or Pause) || (s.Phase == Flight && s.StartAt is null))
            throw new InvalidOperationException($"невідома фаза лелеки: {s.Phase}");
        s.Epoch = NewEpoch();
        S = s;
        _settledOnLoad = false;
        // Раунд, який каса вже розрахувала без столу (сирота), — доводимо мовчки: гроші й так пішли раз.
        if (S.Phase == Flight && _book is not null && (S.Pending is null || _book.Holds(S.Pending.Table, S.Pending.Round) == false))
        {
            Crash(Now, quiet: true);
            _settledOnLoad = true;
        }
        for (var seat = 0; seat < Info.MaxPlayers; seat++) Refresh(seat);
        _dirty = true;
    }

    /// <summary>Чи стіл чекає саме на цей раунд (лелека ще летить) — тоді каса його не чіпає.</summary>
    public bool Holds(string table, int round) =>
        S.Phase == Flight && S.Pending is { } p && p.Round == round && string.Equals(p.Table, table, StringComparison.Ordinal);

    public override void Resumed(TimeSpan pause)
    {
        if (!_settledOnLoad)
        {
            S.Until += pause;
            S.StartAt += pause;
        }
        _settledOnLoad = false;
        _dirty = true;
    }

    // ---------- дрібниці ----------

    string NewEpoch() => Ctx.Seeded
        ? ((uint)Ctx.Rng.Next(int.MinValue, int.MaxValue)).ToString("x8")
        : System.Security.Cryptography.RandomNumberGenerator.GetHexString(8, lowercase: true);

    LelkaBet? BetOf(string key) => S.Bets.FirstOrDefault(b => Rooms.NickKey(b.Nick) == key);

    /// <summary>Скільки людина вже поставила на цей прийом (ще не списано) — гаманець у виді показує вільне.</summary>
    int Reserved(string key) => BetOf(key) is { Taken: false } b ? b.Amount : 0;

    int? SeatOf(string key)
    {
        for (var seat = 0; seat < Info.MaxPlayers; seat++)
            if (Ctx.NickOf(seat) is { } nick && Rooms.NickKey(nick) == key) return seat;
        return null;
    }

    void Refresh(int seat)
    {
        if (_book is not null && Ctx.NickOf(seat) is { } nick)
        {
            var key = Rooms.NickKey(nick);
            S.Wallets[key] = _book.Balance(nick) - Reserved(key);
        }
        _dirty = true;
    }

    string Pick(string[] bank) => bank[Ctx.Rng.Next(bank.Length)];

    /// <summary>Соті → «2,37».</summary>
    public static string Fmt(int cents) => $"{cents / 100},{cents % 100:00}";
}

/// <summary>Репліки Глека за столом Лелеки.</summary>
public static class LelkaLines
{
    public const string BookStuck = "Каса заїла — цей політ без ставок, черепки цілі";

    /// <summary>{0} — нік, {1} — множник, {2} — виграш.</summary>
    public static readonly string[] BigWin =
    [
        "{0} зіскакує з лелеки на ×{1} і несе додому {2} 🏺 — оце нерви!",
        "Ого! {0} забирає на ×{1}: {2} 🏺. Лелека аж озирнулась",
        "{0} — ×{1}, {2} 🏺. Я б так не зміг, у мене руки глиняні",
    ];

    /// <summary>{0} — нік, {1} — точка падіння, {2} — ставка.</summary>
    public static readonly string[] Flop =
    [
        "{0} — мінус {2} 🏺: глек гепнувся на ×{1}. Шубовсть!",
        "Ой-ой, {0}: {2} 🏺 полетіли з глеком на ×{1}. Лелека не винна",
        "{0} чекає ще трошки — і ще трошки — і ×{1}. Дзень! Мінус {2} 🏺",
    ];

    public static readonly string[] Instant =
    [
        "{0}, лелека гикнула ще на землі — ×{1}, і {2} 🏺 у черепки",
        "Злетіли й одразу шубовсть! {0}: мінус {2} 🏺",
    ];

    public static readonly string[] Instant0 =
    [
        "Лелека гикнула на старті — ×1,00. Буває!",
        "Не встигли й злетіти — дзень! Наступного разу пощастить",
    ];

    /// <summary>{0} — точка падіння (Балачки сайту).</summary>
    public static readonly string[] Space =
    [
        "🦢 Лелека з Глеком долетіла аж до ×{0} — у космос! Хто встиг забрати, той і пан",
        "🦢 ×{0}! Лелека несла Глека повз зорі. За столом Лелеки сьогодні жарко",
    ];
}
