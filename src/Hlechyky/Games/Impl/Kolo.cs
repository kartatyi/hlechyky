using System.Collections.Concurrent;
using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Скільки людина поставила на один множник за раунд.</summary>
public sealed class KoloStake
{
    public int Pick { get; set; }
    public int Amount { get; set; }
}

/// <summary>Ставки одного гравця на раунд (на кілька множників одразу).</summary>
public sealed class KoloBet
{
    public string Nick { get; set; } = "";
    /// <summary>Колір у виді — місце в мить першої ставки раунду.</summary>
    public int Color { get; set; }
    public List<KoloStake> Stakes { get; set; } = [];
    /// <summary>Каса вже списала ставки (відповідь черги приходить за мить після «Ставки зроблено!»).</summary>
    public bool Taken { get; set; }

    public int Total() => Stakes.Sum(s => s.Amount);
    public int On(int pick) => Stakes.FirstOrDefault(s => s.Pick == pick)?.Amount ?? 0;
    public IEnumerable<(int Pick, int Amount)> Pairs() => Stakes.Select(s => (s.Pick, s.Amount));
}

public sealed class KoloHist
{
    public int Round { get; set; }
    public int Seg { get; set; }
    public int X { get; set; }
}

public sealed class KoloResult
{
    public string Nick { get; set; } = "";
    public int Color { get; set; }
    public int Staked { get; set; }
    public int Paid { get; set; }
    public int Net { get; set; }
}

/// <summary>Останній розрахований раунд: сегмент, відбиток і seed (для «ⓘ»), хто скільки виграв.</summary>
public sealed class KoloLast
{
    public int Round { get; set; }
    public int Seg { get; set; }
    public int X { get; set; }
    public string Hash { get; set; } = "";
    public string Seed { get; set; } = "";
    public int Staked { get; set; }
    public int Paid { get; set; }
    public string? Big { get; set; }
    public List<KoloResult> Results { get; set; } = [];
}

public sealed class KoloGlek
{
    public string Mood { get; set; } = "idle";
    public string? Say { get; set; }
    public int Seq { get; set; }
}

public sealed class KoloState
{
    public string Phase { get; set; } = Kolo.Bets;
    public int Round { get; set; }
    public string Epoch { get; set; } = "";
    public DateTimeOffset? Until { get; set; }
    public string Seed { get; set; } = "";
    public string Hash { get; set; } = "";
    /// <summary>Сегмент цього раунду — вирішено разом із seed на початку ставок. Таємниця до закриття ставок.</summary>
    public int Seg { get; set; }
    public List<KoloBet> Bets { get; set; } = [];
    public List<KoloHist> History { get; set; } = [];
    /// <summary>Вільні черепки (гаманець мінус ще не списані ставки) — кеш для виду, правда — гаманець у шапці.</summary>
    public Dictionary<string, int> Wallets { get; set; } = [];
    public Dictionary<string, string> Notes { get; set; } = [];
    /// <summary>Ставки з останнього раунду, за який людина платила, — для «🔁 Повторити».</summary>
    public Dictionary<string, List<KoloStake>> LastBets { get; set; } = [];
    public KoloLast? Last { get; set; }
    public KoloRound? Pending { get; set; }
    public KoloGlek Glek { get; set; } = new();
    public bool Hurried { get; set; }
    public int EmptyRounds { get; set; }
    public DateTimeOffset? JournalAt { get; set; }
}

/// <summary>
/// «Гончарне колесо» (docs/games/specs/kolo.md): спільне колесо на сайт, Дядько Глек крутить за розкладом — ставки 10 с
/// → крутиться 5 с → виплати 3 с → ставки… Ставлять на множники ×2 / ×3 / ×6 / ×30 (на кілька одразу); сегмент
/// вирішено на початку ставок (видно лише sha256 від seed), seed — коли колесо стало. Гроші — через
/// <see cref="KoloBook"/>: списання в мить «Ставки зроблено!», виграш — коли колесо стало. Тік у базу не ходить.
/// </summary>
public sealed class Kolo : Game, ISharedTable
{
    public const int TickMs = 250;
    public const int BetMs = 10_000, SpinMs = 5_000, ResultMs = 3_000, HurryMs = 3_000;
    public const int HistoryLen = 20, MaxSeats = 12;
    /// <summary>Журнал: чистий виграш від 2000 або ×30 зі ставкою від 50; не частіше раз на 10 хв. Гопак — від 1000.</summary>
    public const int JournalNet = 2_000, JournalGlek = 50, DanceNet = 1_000, CrackChat = 50;
    public static readonly TimeSpan JournalGap = TimeSpan.FromMinutes(10);

    public const string Bets = "bets", Spin = "spin", Result = "result", Off = "off";
    public const string ActBet = "bet", ActClear = "clear", ActRepeat = "repeat";

    public const string ClosedText = "Каса зачинена — спробуй трохи згодом";
    public const string OffText = "Гончарне колесо відпочиває — ставок зараз не приймаю";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override GameInfo Info { get; } = new(
        "kolo", "Гончарне колесо", "гончарне колесо", GameGroup.Party, 1, MaxSeats, TickMs: TickMs, Start: StartMode.Immediate,
        Hidden: true, Score: ScoreOrder.HigherIsBetter, Coop: true,
        Hint: "Глек крутить гончарне колесо для всіх: ставиш на ×2, ×3, ×6 чи ×30 — і чекаєш, що зліпиться. Можна на кілька одразу");

    KoloState S = new();
    KoloBook? _book;
    bool _dirty, _settledOnLoad;
    readonly ConcurrentQueue<Action> _inbox = new();

    /// <summary>Шов лише для тестів: сегмент наступних раундів (seed тоді нічого не важить).</summary>
    public Func<int>? Rig { get; set; }
    public KoloState State => S;

    DateTimeOffset Now => Ctx.Clock.UtcNow;
    KoloOptions Opts => _book?.Options ?? new KoloOptions();
    string Table => $"{Ctx.RoomId}:{S.Epoch}";

    public override string SeatName(int seat) => $"місце {seat + 1}";

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        try { _book = Ctx?.Services?.GetService<KoloBook>(); }
        catch { _book = null; }
        if (_book is { Options.Enabled: false }) throw new GameError(OffText);
    }

    /// <summary>Чисте колесо. Номер раунду, історія, «Повторити», лічильник реплік і пауза Журналу переживають «Ще раз».</summary>
    public override void Start()
    {
        var keep = S;
        S = new KoloState
        {
            Round = keep.Round, History = keep.History, LastBets = keep.LastBets, JournalAt = keep.JournalAt,
            Glek = new KoloGlek { Seq = keep.Glek.Seq }, Epoch = NewEpoch(),
        };
        OpenBets(Now);
    }

    public override bool Resumable => true;
    public override bool LateJoin(string nick) => true;
    public override string? LateJoinGreeting(string nick) => "Сідай до колеса! Ставки — поки Глек не крутнув";
    public override void OnJoin(int seat) => Refresh(seat);
    /// <summary>Не техпоразка: ставки лишаються на колі й зіграють.</summary>
    public override void OnLeave(int seat) => _dirty = true;
    /// <summary>Стіл без людей ще хвилину докручує те, що на ньому лежить (раунд — 18 с).</summary>
    public override TimeSpan HoldEmpty => TimeSpan.FromSeconds(60);

    // ---------- дії ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (_book is null) return ActResult.Fail(ClosedText);
        Inbox();
        if (action is not (ActBet or ActClear or ActRepeat)) return ActResult.Fail("Тут так не ходять");
        if (Ctx.NickOf(seat) is not { } nick) return ActResult.Fail("Тут так не ходять");
        var now = Now;
        if (Shut(now) is { } shut) return ActResult.Fail(shut);
        if (action != ActClear && !Opts.Enabled) return ActResult.Fail(OffText);
        var key = Rooms.NickKey(nick);
        S.Wallets[key] = _book.Balance(nick) - Reserved(key);
        var r = action switch
        {
            ActBet => Bet(seat, nick, key, payload),
            ActClear => Clear(key),
            _ => Repeat(seat, nick, key),
        };
        if (r.Ok) _dirty = true;
        return r;
    }

    /// <summary>Чому ставок зараз не приймають; null — приймають.</summary>
    string? Shut(DateTimeOffset now) => S.Phase switch
    {
        Bets when S.Until is { } u && now < u => null,
        Bets or Spin => "Ставки зроблено — чекай наступного кола",
        Result => "Глек рахує — ставки за мить",
        _ => OffText,
    };

    ActResult Bet(int seat, string nick, string key, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return ActResult.Fail("Не зрозумів ставки");
        if (!payload.TryGetProperty("pick", out var pe) || pe.ValueKind != JsonValueKind.Number || !pe.TryGetInt32(out var pick)
            || !KoloCore.IsPick(pick)) return ActResult.Fail("Такого множника на колесі нема");
        if (!payload.TryGetProperty("amount", out var ae) || ae.ValueKind != JsonValueKind.Number || !ae.TryGetInt32(out var amount)
            || amount < 1) return ActResult.Fail("Ставка — ціле число черепків");
        var o = Opts;
        if (amount < o.MinBet) return ActResult.Fail($"Найменша ставка — {o.MinBet} 🏺");
        var bet = BetOf(key);
        var on = (long)(bet?.On(pick) ?? 0) + amount;
        if (o.MaxBet > 0 && on > o.MaxBet) return ActResult.Fail($"На один множник — щонайбільше {o.MaxBet} 🏺");
        if (on > KoloCore.MaxPerPick) return ActResult.Fail("Завелика ставка — каса стільки за раз не виплатить");
        var free = S.Wallets[key];
        if (amount > free) return ActResult.Fail($"Бракує черепків: вільних {Math.Max(0, free)}");
        Place(seat, nick, key, [(pick, amount)]);
        return ActResult.Done;
    }

    ActResult Clear(string key)
    {
        if (BetOf(key) is not { } bet || bet.Total() == 0) return ActResult.Fail("Нема чого знімати");
        S.Bets.Remove(bet);
        S.Wallets[key] += bet.Total();
        return ActResult.Done;
    }

    ActResult Repeat(int seat, string nick, string key)
    {
        if (!S.LastBets.TryGetValue(key, out var last) || last.Count == 0)
            return ActResult.Fail("Минулого разу ставок не було — нема чого повторювати");
        var o = Opts;
        var bet = BetOf(key);
        foreach (var s in last)
        {
            var on = (long)(bet?.On(s.Pick) ?? 0) + s.Amount;
            if (o.MaxBet > 0 && on > o.MaxBet) return ActResult.Fail($"На один множник — щонайбільше {o.MaxBet} 🏺");
            if (on > KoloCore.MaxPerPick) return ActResult.Fail("Завелика ставка — каса стільки за раз не виплатить");
            if (s.Amount < o.MinBet) return ActResult.Fail($"Найменша ставка — {o.MinBet} 🏺");
        }
        var cost = last.Sum(s => (long)s.Amount);
        var free = S.Wallets[key];
        if (cost > free) return ActResult.Fail($"Бракує черепків на повтор: треба {cost}, вільних {Math.Max(0, free)}");
        Place(seat, nick, key, [.. last.Select(s => (s.Pick, s.Amount))]);
        return ActResult.Done;
    }

    void Place(int seat, string nick, string key, IReadOnlyList<(int Pick, int Amount)> add)
    {
        var bet = BetOf(key);
        if (bet is null)
        {
            bet = new KoloBet { Nick = nick, Color = seat };
            S.Bets.Add(bet);
        }
        bet.Nick = nick;
        foreach (var (pick, amount) in add)
        {
            if (bet.Stakes.FirstOrDefault(s => s.Pick == pick) is { } st) st.Amount += amount;
            else bet.Stakes.Add(new KoloStake { Pick = pick, Amount = amount });
            S.Wallets[key] -= amount;
        }
        bet.Stakes.Sort((a, b) => Array.IndexOf(KoloCore.Picks, a.Pick).CompareTo(Array.IndexOf(KoloCore.Picks, b.Pick)));
        S.Notes.Remove(key);
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
            case Bets when !S.Hurried && S.Until is { } u && (u - now).TotalMilliseconds <= HurryMs && S.Bets.Count > 0:
                S.Hurried = true;
                Glek("hurry", Pick(KoloLines.Hurry));
                break;
            case Spin when S.Until is { } u && now >= u:
                Land(now, quiet: false);
                break;
            case Result when S.Until is { } u && now >= u:
                // Вимкнено — нових ставок не відкриваємо: колесо тихо стоїть, доки не ввімкнуть (без порожніх кіл).
                if (Opts.Enabled) OpenBets(now);
                else GoOff();
                break;
            case Off when Opts.Enabled:
                OpenBets(now);
                break;
        }
        if (!_dirty) return TickResult.None;
        _dirty = false;
        return new TickResult(false, true);
    }

    /// <summary>Новий раунд: seed і його відбиток — уже зараз, сегмент вирішено. Гаманці всіх, хто сидить, — свіжі.</summary>
    void OpenBets(DateTimeOffset now)
    {
        S.Round++;
        S.Seed = KoloCore.NewSeed(Ctx.Seeded ? Ctx.Rng : null);
        S.Hash = KoloCore.Hash(S.Seed);
        var seg = Rig?.Invoke() ?? KoloCore.Seg(S.Seed);
        if (seg < 0 || seg >= KoloCore.Size) throw new GameError($"Такого сегмента на колі нема: {seg}");
        S.Seg = seg;
        S.Bets.Clear();
        S.Notes.Clear();
        S.Pending = null;
        S.Phase = Bets;
        S.Until = now.AddMilliseconds(BetMs);
        S.Hurried = false;
        Glek("idle", S.EmptyRounds == 0 ? Pick(KoloLines.Open) : null);
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

    void GoOff()
    {
        S.Phase = Off;
        S.Until = null;
        Glek("doze", KoloLines.Off);
        _dirty = true;
    }

    void Wallets(IReadOnlyDictionary<string, int> all)
    {
        foreach (var (nick, wallet) in all)
        {
            var key = Rooms.NickKey(nick);
            S.Wallets[key] = wallet - Reserved(key);
        }
        _dirty = true;
    }

    void Inbox()
    {
        while (_inbox.TryDequeue(out var apply)) apply();
    }

    /// <summary>
    /// «Ставки зроблено!»: колесо крутиться одразу, а запис у касу й списання кожному — у черзі каси
    /// (<see cref="KoloBook.Launch"/>). Не записалось — ставки знято; комусь не списалось — його ставки знято, людині рядок.
    /// Нема ставок — колесо однаково крутиться (історія жива, чесність та сама), а грошей ніхто не чіпає.
    /// </summary>
    void Close(DateTimeOffset now)
    {
        S.Phase = Spin;
        S.Until = now.AddMilliseconds(SpinMs);
        _dirty = true;
        S.Bets.RemoveAll(b => b.Total() == 0);
        if (S.Bets.Count == 0)
        {
            S.EmptyRounds++;
            if (S.EmptyRounds == 1) Glek("sigh", Pick(KoloLines.Sigh));
            else Glek("call", null);
            return;
        }
        S.EmptyRounds = 0;
        if (_book is null)
        {
            Stuck();
            return;
        }
        var x = KoloCore.X(S.Seg);
        var pending = new KoloRound(Table, S.Round, Info.Id, S.Seg, x, now,
            [.. S.Bets.Select(b => new KoloPay(b.Nick, b.Total(), (int)KoloCore.Return(b.Pairs(), x)))]);
        S.Pending = pending;
        Glek("call", Pick(KoloLines.Call));
        _book.Launch(pending, res => _inbox.Enqueue(() => Launched(pending, res)));
        Inbox();
    }

    void Stuck()
    {
        foreach (var b in S.Bets) S.Notes[Rooms.NickKey(b.Nick)] = "Каса заїла — ставки не взято, черепки цілі";
        S.Bets.Clear();
        S.Pending = null;
        Glek("sigh", KoloLines.BookStuck);
        _dirty = true;
    }

    /// <summary>Відповідь каси на «Ставки зроблено!»: хто списався — грає, хто ні — ставки знято, людині рядок.</summary>
    void Launched(KoloRound pending, IReadOnlyList<KoloTake>? res)
    {
        if (S.Round != pending.Round || !string.Equals(Table, pending.Table, StringComparison.Ordinal)) return;
        _dirty = true;
        // Колесо вже стало (каса відповіла запізно): розрахунок пішов у ту саму чергу після списань — гроші правильні,
        // тут лише гаманці й рядок людині.
        var live = S.Phase == Spin;
        if (res is null)
        {
            if (live) Stuck();
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
                if (live) S.Wallets[key] = Math.Max(0, t.Wallet - stake);
                continue;
            }
            S.Wallets[key] = t.Wallet;
            S.Notes[key] = $"Черепків не стало — твої ставки ({stake}) знято";
            if (live && b is not null) S.Bets.Remove(b);
        }
        if (live && S.Pending is { } p && p.Round == pending.Round)
        {
            var left = S.Bets.Select(b => Rooms.NickKey(b.Nick)).ToHashSet();
            S.Pending = left.Count == 0 ? null : p with { Pays = [.. p.Pays.Where(x => left.Contains(Rooms.NickKey(x.Nick)))] };
            if (S.Pending is null) Glek("sigh", KoloLines.NoMoney);
        }
    }

    /// <summary>
    /// Колесо стало: виплати (поза замком), історія, «Повторити», ачівки, таблиця, балачка, Журнал.
    /// <paramref name="quiet"/> — каса вже розрахувала раунд без столу (Load після довгої перерви): лише стан.
    /// </summary>
    void Land(DateTimeOffset now, bool quiet)
    {
        var x = KoloCore.X(S.Seg);
        if (S.Pending is { } p && !quiet) _book?.Settle(p);
        S.Pending = null;
        S.Phase = Result;
        S.Until = now.AddMilliseconds(ResultMs);
        S.History.Insert(0, new KoloHist { Round = S.Round, Seg = S.Seg, X = x });
        if (S.History.Count > HistoryLen) S.History.RemoveRange(HistoryLen, S.History.Count - HistoryLen);
        var results = new List<KoloResult>();
        foreach (var b in S.Bets)
        {
            var key = Rooms.NickKey(b.Nick);
            var staked = b.Total();
            var paid = (int)KoloCore.Return(b.Pairs(), x);
            results.Add(new KoloResult { Nick = b.Nick, Color = b.Color, Staked = staked, Paid = paid, Net = paid - staked });
            if (paid > 0) S.Wallets[key] = (int)Math.Min(int.MaxValue, (long)S.Wallets.GetValueOrDefault(key) + paid);
            S.LastBets[key] = [.. b.Stakes.Select(s => new KoloStake { Pick = s.Pick, Amount = s.Amount })];
        }
        results.Sort((a, b) => b.Net != a.Net ? b.Net.CompareTo(a.Net) : string.CompareOrdinal(a.Nick, b.Nick));
        S.Last = new KoloLast
        {
            Round = S.Round, Seg = S.Seg, X = x, Hash = S.Hash, Seed = S.Seed, Staked = results.Sum(r => r.Staked),
            Paid = results.Sum(r => r.Paid), Results = results,
        };
        _dirty = true;
        if (quiet) Glek("idle", null);
        else Announce(now, S.Last);
    }

    void Announce(DateTimeOffset now, KoloLast last)
    {
        var x = last.X;
        foreach (var r in last.Results)
        {
            if (SeatOf(Rooms.NickKey(r.Nick)) is not { } seat) continue;
            if (r.Net > 0) Ctx.Score(seat, r.Net);
            if (x == 30 && r.Paid > 0) Ctx.Award(seat, 0, "ach:kolo-30");
            if (x == KoloCore.Crack && r.Staked > 0) Ctx.Award(seat, 0, "ach:kolo-crack");
        }
        var shout = KoloCore.Shout(x);
        if (last.Results.Count == 0)
        {
            Glek("idle", shout);
            return;
        }
        var best = last.Results[0];
        string? chat = null;
        if ((x == 30 && best.Paid > 0) || best.Net >= DanceNet)
        {
            last.Big = best.Nick;
            var line = string.Format(Pick(KoloLines.Dance), best.Nick, best.Paid);
            Glek("dance", shout + " " + line);
            chat = line;
        }
        else if (x == KoloCore.Crack)
        {
            var line = Pick(KoloLines.Crack);
            Glek("laugh", line);
            if (last.Staked >= CrackChat) chat = line;
        }
        else if (last.Paid > last.Staked) Glek("clap", shout + " " + Pick(KoloLines.Clap));
        else Glek("rake", shout + " " + Pick(KoloLines.Rake));
        if (chat is not null) Ctx.Say(chat);
        var star = last.Results.FirstOrDefault(r => r.Net > 0
            && (r.Net >= JournalNet || (x == 30 && S.Bets.FirstOrDefault(b => b.Nick == r.Nick)?.On(30) >= JournalGlek)));
        if (star is not null && (S.JournalAt is not { } at || now - at >= JournalGap))
        {
            S.JournalAt = now;
            Ctx.Log($"🏺 Гончарне колесо: {star.Nick} виграє {star.Net} 🏺 — {shout}");
        }
    }

    // ---------- вид ----------

    public override object View(int? seat)
    {
        var now = Now;
        var left = S.Until is { } u ? Math.Max(0, (int)Math.Ceiling((u - now).TotalMilliseconds)) : (int?)null;
        var x = KoloCore.X(S.Seg);
        var turned = S.Phase is Spin or Result or Off;
        var revealed = S.Phase is Result or Off;
        var nick = seat is { } s0 ? Ctx.NickOf(s0) : null;
        var myKey = nick is null ? null : Rooms.NickKey(nick);

        var players = new List<object>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var s = 0; s < Info.MaxPlayers; s++)
        {
            if (Ctx.NickOf(s) is not { } n) continue;
            var key = Rooms.NickKey(n);
            seen.Add(key);
            players.Add(PlayerDto(n, s, BetOf(key), here: true, mine: key == myKey));
        }
        foreach (var b in S.Bets)
        {
            var key = Rooms.NickKey(b.Nick);
            if (seen.Add(key)) players.Add(PlayerDto(b.Nick, null, b, here: false, mine: key == myKey));
        }

        object? me = null;
        if (myKey is not null)
        {
            var mine = BetOf(myKey);
            var open = Shut(now) is null && Opts.Enabled && _book is not null;
            var repeat = S.LastBets.TryGetValue(myKey, out var lb) ? lb.Sum(z => z.Amount) : 0;
            var onTable = mine?.Total() ?? 0;
            me = new
            {
                free = Math.Max(0, S.Wallets.GetValueOrDefault(myKey)),
                onTable,
                canClear = open && onTable > 0,
                canRepeat = open && repeat > 0,
                repeatCost = repeat,
                note = S.Notes.TryGetValue(myKey, out var note) ? note : null,
            };
        }

        var o = Opts;
        return new
        {
            phase = S.Phase,
            round = S.Round,
            until = S.Until,
            leftMs = left,
            phaseMs = S.Phase switch { Bets => BetMs, Spin => SpinMs, Result => ResultMs, _ => (int?)null },
            hash = S.Hash,
            seed = revealed ? S.Seed : null,
            wheel = KoloCore.Wheel,
            picks = KoloCore.Picks,
            spin = turned ? new { no = S.Round, seg = S.Seg, x, leftMs = S.Phase == Spin ? left ?? 0 : 0, ms = SpinMs } : null,
            history = S.History.Select(h => new { round = h.Round, seg = h.Seg, x = h.X }).ToList(),
            players,
            totals = KoloCore.Picks.Select(p => new
            {
                pick = p,
                amount = S.Bets.Sum(b => b.On(p)),
                people = S.Bets.Count(b => b.On(p) > 0),
            }).ToList(),
            onTable = S.Bets.Sum(b => b.Total()),
            last = S.Last is { } l ? new
            {
                round = l.Round, seg = l.Seg, x = l.X, hash = l.Hash, seed = l.Seed, staked = l.Staked, paid = l.Paid, big = l.Big,
                results = l.Results.Select(r => new { nick = r.Nick, color = r.Color, staked = r.Staked, paid = r.Paid, net = r.Net }).ToList(),
            } : null,
            glek = new { mood = S.Glek.Mood, say = S.Glek.Say, seq = S.Glek.Seq },
            me,
            limits = new { min = o.MinBet, max = o.MaxBet },
            on = o.Enabled && _book is not null,
        };
    }

    static object PlayerDto(string nick, int? seat, KoloBet? bet, bool here, bool mine) => new
    {
        nick,
        seat,
        color = bet?.Color ?? seat ?? 0,
        here,
        mine,
        total = bet?.Total() ?? 0,
        bets = (bet?.Stakes ?? []).Select(s => new { pick = s.Pick, amount = s.Amount }).ToList(),
    };

    // ---------- збереження ----------

    public override string? Save() => JsonSerializer.Serialize(S, Json);

    /// <summary>Новий epoch на кожне відновлення: раунд зі старого стану ніколи не перепише ключ уже списаного.</summary>
    public override void Load(string json)
    {
        var s = JsonSerializer.Deserialize<KoloState>(json, Json) ?? throw new InvalidOperationException("порожній стан колеса");
        if (s.Phase is not (Bets or Spin or Result or Off)) throw new InvalidOperationException($"невідома фаза колеса: {s.Phase}");
        if (s.Seg < 0 || s.Seg >= KoloCore.Size) throw new InvalidOperationException($"невідомий сегмент колеса: {s.Seg}");
        s.Epoch = NewEpoch();
        S = s;
        _settledOnLoad = false;
        // Раунд, який каса вже розрахувала без столу (сирота), — доводимо мовчки: гроші й так пішли раз.
        if (S.Phase == Spin && S.Pending is { } p && _book is not null && _book.Holds(p.Table, p.Round) == false)
        {
            Land(Now, quiet: true);
            _settledOnLoad = true;
        }
        for (var seat = 0; seat < Info.MaxPlayers; seat++) Refresh(seat);
        _dirty = true;
    }

    /// <summary>Чи стіл чекає саме на цей раунд (колесо ще крутиться) — тоді каса його не чіпає.</summary>
    public bool Holds(string table, int round) =>
        S.Phase == Spin && S.Pending is { } p && p.Round == round && string.Equals(p.Table, table, StringComparison.Ordinal);

    public override void Resumed(TimeSpan pause)
    {
        if (!_settledOnLoad) S.Until += pause;
        _settledOnLoad = false;
        _dirty = true;
    }

    // ---------- дрібниці ----------

    string NewEpoch() => Ctx.Seeded
        ? ((uint)Ctx.Rng.Next(int.MinValue, int.MaxValue)).ToString("x8")
        : System.Security.Cryptography.RandomNumberGenerator.GetHexString(8, lowercase: true);

    KoloBet? BetOf(string key) => S.Bets.FirstOrDefault(b => Rooms.NickKey(b.Nick) == key);

    /// <summary>Скільки людина вже поставила на цей раунд (ще не списано).</summary>
    int Reserved(string key) => BetOf(key) is { Taken: false } b && S.Phase == Bets ? b.Total() : 0;

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

    void Glek(string mood, string? say)
    {
        S.Glek.Mood = mood;
        S.Glek.Say = say;
        S.Glek.Seq++;
        _dirty = true;
    }

    string Pick(string[] bank) => bank[Ctx.Rng.Next(bank.Length)];
}

/// <summary>Репліки Дядька Глека біля гончарного колеса (ніки — лише в називному, без дієслів минулого часу).</summary>
public static class KoloLines
{
    public const string BookStuck = "Каса заїла — це коло без ставок, черепки цілі";
    public const string NoMoney = "Черепків ні в кого не стало — кручу для краси";
    public const string Off = "Колесо відпочиває — Глек миє руки від глини 💤";

    public static readonly string[] Open =
    [
        "Ставки на круг! Що сьогодні ліпимо?",
        "Миска, горщик, макітра чи глек — обирайте, поки глина м'яка",
        "Черепки на стіл — Глек крутить!",
    ];

    public static readonly string[] Hurry =
    [
        "Останні ставки! Нога вже на педалі",
        "Три секунди — і круг пішов. Хто не встиг, той місить глину",
    ];

    public static readonly string[] Call =
    [
        "Ставки зроблено — крутимо!",
        "Поїхали! Глина пішла по колу",
        "Все, руки від столу — круг крутиться",
    ];

    public static readonly string[] Sigh =
    [
        "Ех… ніхто не ставить. Покручу для себе",
        "Круг без ставок — як глечик без молока",
    ];

    /// <summary>{0} — нік, {1} — скільки повернулось.</summary>
    public static readonly string[] Dance =
    [
        "{0} забирає {1} 🏺 — Глек іде в гопак!",
        "Отакої, {0}: {1} 🏺! Тримайте мене семеро",
        "{0} — {1} 🏺. Оце руки в людини, не те що в мене",
    ];

    public static readonly string[] Crack =
    [
        "Тріснув! Хе-хе, черепки до мене — на новий глечик",
        "Трісь! Не вдався глечик — усе, що на столі, моє",
        "Тріснув, любі мої. Буває й у майстра",
    ];

    public static readonly string[] Clap =
    [
        "Ну ви сьогодні й наліпили — плачу!",
        "Каса плаче, а Глек платить",
    ];

    public static readonly string[] Rake =
    [
        "Що зліпилось, те зліпилось — решта до мене",
        "Глина своє знає, а черепки — мої",
    ];
}
