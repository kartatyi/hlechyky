using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hlechyky.Games.Impl;

/// <summary>Одна позиція на полі: поле й скільки на ньому.</summary>
public sealed class RouletteChip
{
    public string Spot { get; set; } = "";
    public int Amount { get; set; }
}

/// <summary>Ставки одного гравця за коло.</summary>
public sealed class RouletteHand
{
    public string Nick { get; set; } = "";
    /// <summary>Місце в мить першої ставки кола — колір фішок, поки він за столом не сидить.</summary>
    public int Color { get; set; }
    /// <summary>У порядку першого торкання поля.</summary>
    public List<RouletteChip> Bets { get; set; } = [];
    /// <summary>Стек відміни: кожен крок — що він доклав (ставка — одне поле, «Повторити»/«×2» — кілька).</summary>
    public List<List<RouletteChip>> Steps { get; set; } = [];

    [JsonIgnore] public int Total => (int)Math.Min(int.MaxValue, Bets.Sum(b => (long)b.Amount));
}

/// <summary>Хто заплатив за закрите коло (списання пройшло).</summary>
public sealed class RoulettePaid
{
    public string Nick { get; set; } = "";
    public int Color { get; set; }
    public int Stake { get; set; }
    /// <summary>Гаманець перед списанням — для «Ва-банку».</summary>
    public int Wallet0 { get; set; }
}

public sealed class RouletteSpin
{
    public int No { get; set; }
    public int N { get; set; }
    public DateTimeOffset At { get; set; }
    public DateTimeOffset Until { get; set; }
}

public sealed class RouletteResult
{
    public string Nick { get; set; } = "";
    public int Color { get; set; }
    public int Staked { get; set; }
    public int Paid { get; set; }
    public int Net { get; set; }
    public List<string> Hits { get; set; } = [];
}

/// <summary>Останнє розраховане коло.</summary>
public sealed class RouletteLast
{
    public int No { get; set; }
    public int N { get; set; }
    public int Staked { get; set; }
    public int Paid { get; set; }
    public string? Big { get; set; }
    public List<RouletteResult> Results { get; set; } = [];
}

public sealed class RouletteGlek
{
    public string Mood { get; set; } = "idle";
    public string? Say { get; set; }
    public long Seq { get; set; }
}

/// <summary>Увесь стан рулетки — одним JSON-ом (Save/Load, продовження після перезапуску). Словники — за ключем ніка.</summary>
public sealed class RouletteState
{
    public string Phase { get; set; } = RouletteGame.Bets;
    public DateTimeOffset? Until { get; set; }
    /// <summary>Лічильник закритих кіл: росте на кожне закриття, і на невдале — ключ леджера не повторюється.</summary>
    public int SpinNo { get; set; }
    /// <summary>Лише для довідки: Load бере новий.</summary>
    public string Epoch { get; set; } = "";
    public List<RouletteHand> Hands { get; set; } = [];
    public PendingSpin? Pending { get; set; }
    public List<RoulettePaid> Paid { get; set; } = [];
    public RouletteSpin? Spin { get; set; }
    /// <summary>Останні числа, найновіше першим.</summary>
    public List<int> History { get; set; } = [];
    public RouletteLast? Last { get; set; }
    public Dictionary<string, List<RouletteChip>> LastBets { get; set; } = [];
    /// <summary>Серія «червоне вп'яте»: скільки своїх кіл поспіль ставка на червоне — і червоне.</summary>
    public Dictionary<string, int> Red { get; set; } = [];
    public int EmptyRounds { get; set; }
    public bool Hurried { get; set; }
    public RouletteGlek Glek { get; set; } = new();
    public DateTimeOffset? JournalAt { get; set; }
    public DateTimeOffset LastActionAt { get; set; }
    /// <summary>Кеш гаманців: підказка для «вільних», правда — гаманець у шапці сайту.</summary>
    public Dictionary<string, int> Wallets { get; set; } = [];
    /// <summary>Рядок для гравця цього кола («Черепків не стало…»).</summary>
    public Dictionary<string, string> Notes { get; set; } = [];
}

/// <summary>
/// Рулетка — європейська, одне зеро, Дядько Глек за колесом (docs/games/specs/roulette.md). Спільне для столу й соло:
/// ставки, дії, закриття кола (каса <see cref="RouletteBook"/>), розрахунок, вид, Save/Load. Різниця — лише фази:
/// стіл крутить Глек за розкладом (<see cref="Roulette"/>), соло — коли тиснеш «Крутити» (<see cref="RouletteSolo"/>).
/// Види шлються з тика (після Act у реалтаймі каркас їх не шле), тож ставка доходить до всіх за ≤ 250 мс.
/// </summary>
public abstract class RouletteGame : Game
{
    public const int TickMs = 250;
    public const int BetMs = 25_000, SpinMs = 6_000, ResultMs = 5_000, HurryMs = 5_000, SoloSpinMs = 5_000, DozeMs = 120_000;
    public const int MaxSpots = 60, HistoryLen = 20, EmptyToDoze = 3;
    public static readonly TimeSpan JournalGap = TimeSpan.FromMinutes(10);

    public const string Idle = "idle", Bets = "bets", Spinning = "spin", Result = "result";
    public const string ActBet = "bet", ActUndo = "undo", ActClear = "clear", ActRepeat = "repeat", ActDouble = "double", ActSpin = "spin";

    public const string ClosedText = "Каса зачинена — спробуй трохи згодом";
    public const string TooManyText = "Досить — у тебе вже 60 ставок на полі";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected RouletteState S = new();
    RouletteBook? _book;
    bool _dirty;
    /// <summary>Load сам дорахував коло, яке каса вже виплатила: дедлайн — від «зараз», Resumed його не зсуває.</summary>
    bool _settledOnLoad;

    /// <summary>Шов лише для тестів: «випаде 17». Поза 0..36 — помилка.</summary>
    public Func<int>? Rig { get; set; }
    /// <summary>Для тестів: стан гри.</summary>
    public RouletteState State => S;

    protected abstract bool Solo { get; }
    int SpinLen => Solo ? SoloSpinMs : SpinMs;
    DateTimeOffset Now => Ctx.Clock.UtcNow;

    public override string SeatName(int seat) => $"місце {seat + 1}";

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        try { _book = Ctx?.Services?.GetService<RouletteBook>(); }
        catch { _book = null; }
    }

    /// <summary>
    /// Чистий стіл (створення, «Ще раз» після переривання, продовження — тоді одразу Load). Історія чисел, минулі ставки,
    /// серії й лічильник реплік Глека живуть в екземплярі гри й переживають «Ще раз».
    /// </summary>
    public override void Start()
    {
        var keep = S;
        S = new RouletteState
        {
            SpinNo = keep.SpinNo,
            History = keep.History,
            Last = keep.Last,
            LastBets = keep.LastBets,
            Red = keep.Red,
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
        if (action is not (ActBet or ActUndo or ActClear or ActRepeat or ActDouble or ActSpin)) return ActResult.Fail("Тут так не ходять");
        if (action == ActSpin && !Solo) return ActResult.Fail("Тут колесо крутить Глек — за розкладом");
        var now = Now;
        if (Solo && S.Phase == Spinning) return ActResult.Fail("Колесо крутиться — дочекайся");
        if (!Solo && (S.Phase == Spinning || (S.Phase == Bets && S.Until is { } u && now >= u)))
            return ActResult.Fail("Ставки зроблено — чекай наступного кола");
        if (!Solo && S.Phase == Result) return ActResult.Fail("Глек рахує виграші — ставки за мить");
        if (Ctx.NickOf(seat) is not { } nick) return ActResult.Fail("Тут так не ходять");

        var key = Key(nick);
        var wallet = _book.Balance(nick);
        S.Wallets[key] = wallet;
        var result = action switch
        {
            ActBet => Bet(seat, nick, key, payload, wallet),
            ActUndo => Undo(key),
            ActClear => Clear(key),
            ActRepeat => Repeat(seat, nick, key, wallet),
            ActDouble => Double(key, wallet),
            _ => Spin(seat, nick, key, payload, wallet),
        };
        if (!result.Ok) return result;
        S.LastActionAt = now;
        if (Solo && S.Glek.Mood == "doze") Glek("idle", Pick(RouletteLines.Open));
        _dirty = true;
        return result;
    }

    ActResult Bet(int seat, string nick, string key, JsonElement payload, int wallet)
    {
        if (RouletteCore.Read(payload, out var spot) is { } bad) return ActResult.Fail(bad);
        if (payload.TryGetProperty("amount", out var a) is false || a.ValueKind != JsonValueKind.Number || !a.TryGetInt32(out var amount) || amount < 1)
            return ActResult.Fail("Ставка — ціле число черепків, від 1");
        var hand = HandOf(key);
        var count = hand?.Bets.Count ?? 0;
        if (count >= MaxSpots && hand!.Bets.All(b => b.Spot != spot)) return ActResult.Fail(TooManyText);
        var free = (long)wallet - (hand?.Total ?? 0);
        if (amount > free) return ActResult.Fail($"Бракує черепків: вільних {Math.Max(0, free)}");
        List<RouletteChip> step = [new RouletteChip { Spot = spot, Amount = amount }];
        if (TooBig(hand, step) is { } big) return ActResult.Fail(big);
        Wake();
        Put(HandFor(nick, key, seat), step);
        return ActResult.Done;
    }

    ActResult Undo(string key)
    {
        if (HandOf(key) is not { Steps.Count: > 0 } hand) return ActResult.Fail("Нема чого знімати");
        var step = hand.Steps[^1];
        hand.Steps.RemoveAt(hand.Steps.Count - 1);
        foreach (var chip in step)
            if (hand.Bets.FirstOrDefault(b => b.Spot == chip.Spot) is { } on)
            {
                on.Amount -= chip.Amount;
                if (on.Amount <= 0) hand.Bets.Remove(on);
            }
        return ActResult.Done;
    }

    ActResult Clear(string key)
    {
        if (HandOf(key) is not { } hand || hand.Total <= 0) return ActResult.Fail("Нема чого знімати");
        hand.Bets.Clear();
        hand.Steps.Clear();
        return ActResult.Done;
    }

    ActResult Repeat(int seat, string nick, string key, int wallet)
    {
        if (RepeatCheck(key, wallet) is { } no) return ActResult.Fail(no);
        Wake();
        Put(HandFor(nick, key, seat), Clone(S.LastBets[key]));
        return ActResult.Done;
    }

    /// <summary>Чи можна поставити ще раз минуле коло: null — так, інакше текст відмови.</summary>
    string? RepeatCheck(string key, int wallet)
    {
        if (!S.LastBets.TryGetValue(key, out var last) || last.Count == 0) return "Минулого кола ставок не було — нема чого повторювати";
        var hand = HandOf(key);
        var fresh = last.Count(c => hand is null || hand.Bets.All(b => b.Spot != c.Spot));
        if ((hand?.Bets.Count ?? 0) + fresh > MaxSpots) return TooManyText;
        var cost = Sum(last);
        var free = (long)wallet - (hand?.Total ?? 0);
        return cost > free ? $"Бракує черепків на повтор: треба {cost}, вільних {Math.Max(0, free)}" : TooBig(hand, last);
    }

    ActResult Double(string key, int wallet)
    {
        if (HandOf(key) is not { } hand || hand.Total <= 0) return ActResult.Fail("Нема чого подвоювати");
        long cost = hand.Total;
        var free = (long)wallet - hand.Total;
        if (cost > free) return ActResult.Fail($"Бракує черепків, щоб подвоїти: треба ще {cost}, вільних {Math.Max(0, free)}");
        if (TooBig(hand, hand.Bets) is { } big) return ActResult.Fail(big);
        Put(hand, Clone(hand.Bets));
        return ActResult.Done;
    }

    /// <summary>Соло: закрити коло й крутити. <c>again</c> на порожньому полі — спершу «Повторити», тоді крутити (одна дія).</summary>
    ActResult Spin(int seat, string nick, string key, JsonElement payload, int wallet)
    {
        var again = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("again", out var g) && g.ValueKind == JsonValueKind.True;
        var hand = HandOf(key);
        var repeated = false;
        if (hand is null || hand.Total <= 0)
        {
            if (!again || !S.LastBets.TryGetValue(key, out var last) || last.Count == 0) return ActResult.Fail("Спершу постав хоч один черепок");
            if (RepeatCheck(key, wallet) is { } no) return ActResult.Fail(no);
            hand = HandFor(nick, key, seat);
            Put(hand, Clone(last));
            repeated = true;
        }
        if (Close() is { } why)
        {
            if (repeated)
            {
                hand.Bets.Clear();
                hand.Steps.Clear();
            }
            return ActResult.Fail(why);
        }
        return ActResult.Done;
    }

    /// <summary>Спільний стіл дрімає — перша прийнята ставка будить його з повним вікном ставок.</summary>
    void Wake()
    {
        if (Solo || S.Phase != Idle) return;
        S.EmptyRounds = 0;
        OpenBets(afterSigh: false);
    }

    /// <summary>
    /// Чи влізе виграш у гаманець після кроку (§12): гаманці й леджер — <c>int</c>, тож на одному полі щонайбільше
    /// <see cref="RouletteCore.MaxPerSpot"/>, а найбільше, що рука поверне за будь-якого числа, — щонайбільше
    /// <see cref="int.MaxValue"/>. Лімітів ставок нема — це межа арифметики, а не правило. null — влізе.
    /// </summary>
    static string? TooBig(RouletteHand? hand, List<RouletteChip> step)
    {
        var sums = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var b in hand?.Bets ?? []) sums[b.Spot] = sums.GetValueOrDefault(b.Spot) + b.Amount;
        foreach (var c in step) sums[c.Spot] = sums.GetValueOrDefault(c.Spot) + c.Amount;
        if (sums.Values.Any(a => a > RouletteCore.MaxPerSpot)) return RouletteCore.Say.SpotTooBig;
        return RouletteCore.MaxReturn(sums.Select(kv => (kv.Key, (int)kv.Value))) > int.MaxValue ? RouletteCore.Say.WinTooBig : null;
    }

    static void Put(RouletteHand hand, List<RouletteChip> step)
    {
        foreach (var chip in step)
        {
            if (hand.Bets.FirstOrDefault(b => b.Spot == chip.Spot) is { } on) on.Amount += chip.Amount;
            else hand.Bets.Add(new RouletteChip { Spot = chip.Spot, Amount = chip.Amount });
        }
        hand.Steps.Add(step);
    }

    // ---------- тик ----------

    public override TickResult Tick()
    {
        var now = Now;
        if (Solo) TickSolo(now);
        else TickTable(now);
        if (!_dirty) return TickResult.None;
        _dirty = false;
        return new TickResult(false, true);
    }

    void TickTable(DateTimeOffset now)
    {
        switch (S.Phase)
        {
            case Bets when S.Until is { } until:
                if (now >= until)
                {
                    if (S.Hands.Any(h => h.Total > 0)) Close();
                    else EmptyRound();
                }
                else if (!S.Hurried && until - now <= TimeSpan.FromMilliseconds(HurryMs) && S.Hands.Any(h => h.Total > 0))
                {
                    S.Hurried = true;
                    Glek("hurry", Pick(RouletteLines.Hurry));
                }
                break;
            case Spinning when S.Spin is { } spin && now >= spin.Until:
                Settle();
                break;
            case Result when S.Until is { } until && now >= until:
                S.Hands.Clear();
                S.Notes.Clear();
                S.Spin = null;
                if (AnySeated()) OpenBets(afterSigh: false);
                else Doze();
                break;
        }
    }

    void TickSolo(DateTimeOffset now)
    {
        if (S.Phase == Spinning && S.Spin is { } spin && now >= spin.Until) Settle();
        else if (S.Phase == Bets && S.Glek.Mood != "doze" && now - S.LastActionAt >= TimeSpan.FromMilliseconds(DozeMs))
            Glek("doze", Pick(RouletteLines.Doze));
    }

    /// <summary>Нове вікно ставок: стіл — 25 с, соло — без таймера. Гаманці всіх, хто сидить, — свіжі.</summary>
    void OpenBets(bool afterSigh)
    {
        S.Phase = Bets;
        S.Until = Solo ? null : Now.AddMilliseconds(BetMs);
        S.Hurried = false;
        if (_book is not null)
            for (var seat = 0; seat < Info.MaxPlayers; seat++)
                if (Ctx.NickOf(seat) is { } nick) S.Wallets[Key(nick)] = _book.Balance(nick);
        if (!afterSigh) Glek("idle", Pick(RouletteLines.Open));
        _dirty = true;
    }

    void Doze()
    {
        S.Phase = Idle;
        S.Until = null;
        S.Hurried = false;
        Glek("doze", Pick(RouletteLines.Doze));
    }

    /// <summary>Вікно ставок минуло порожнім: Глек зітхає; нікого за столом чи третє порожнє поспіль — дрімає.</summary>
    void EmptyRound()
    {
        S.EmptyRounds++;
        S.Hands.Clear();
        if (!AnySeated() || S.EmptyRounds >= EmptyToDoze)
        {
            Doze();
            return;
        }
        var line = Pick(RouletteLines.Sigh);
        Glek("sigh", line);
        if (S.EmptyRounds == 1) Ctx.Say(line);
        OpenBets(afterSigh: true);
    }

    // ---------- коло ----------

    /// <summary>Номер кишеньки: рівномірно 0..36, у мить закриття. Сід кімнати — лише в тестах (<see cref="IRoomContext.Seeded"/>).</summary>
    public int Draw()
    {
        var n = Rig?.Invoke() ?? (Ctx.Seeded ? Ctx.Rng.Next(37) : RandomNumberGenerator.GetInt32(37));
        if (n is < 0 or > 36) throw new GameError("Кулька вискочила з колеса");
        return n;
    }

    /// <summary>
    /// «Ставки зроблено!» (§3.3): запис у касу, тоді списання кожному одним рухом, тоді колесо. Під замком кімнати.
    /// null — крутиться; інакше текст (соло віддає його гравцеві, стіл — Глекові).
    /// </summary>
    string? Close()
    {
        var now = Now;
        var hands = S.Hands.Where(h => h.Total > 0).ToList();
        S.Notes.Clear();
        var spin = ++S.SpinNo;
        var n = Draw();
        var table = $"{Ctx.RoomId}:{S.Epoch}";
        var pays = hands.Select(h => new SpinPay(h.Nick, h.Total, Returns(h.Bets, n))).ToList();
        var pending = new PendingSpin(table, spin, Info.Id, n, now, pays);
        if (_book is null || !_book.Open(pending))
        {
            if (Solo) return ClosedText;
            Glek("sigh", RouletteLines.BookStuck);
            OpenBets(afterSigh: true);
            return RouletteLines.BookStuck;
        }

        var paid = new List<RoulettePaid>();
        foreach (var h in hands)
        {
            var key = Key(h.Nick);
            var stake = h.Total;
            var wallet0 = _book.Balance(h.Nick);
            if (_book.Take(h.Nick, stake, $"roulette-bet:{Info.Id}", $"roulette-bet:{table}:{spin}:{key}"))
            {
                paid.Add(new RoulettePaid { Nick = h.Nick, Color = ColorFor(h), Stake = stake, Wallet0 = wallet0 });
                S.Wallets[key] = Math.Max(0, wallet0 - stake);
                continue;
            }
            S.Wallets[key] = wallet0;
            if (Solo)
            {
                _book.Drop(table, spin);
                return $"Бракує черепків: у гаманці {wallet0}, а на столі {stake}";
            }
            S.Notes[key] = $"Черепків не стало — твої ставки ({stake}) знято";
            h.Bets.Clear();
            h.Steps.Clear();
        }
        if (paid.Count == 0)
        {
            _book.Drop(table, spin);
            Glek("sigh", RouletteLines.NobodyPaid);
            OpenBets(afterSigh: true);
            return RouletteLines.NobodyPaid;
        }

        S.Pending = pending;
        S.Paid = paid;
        S.EmptyRounds = 0;
        S.Spin = new RouletteSpin { No = spin, N = n, At = now, Until = now.AddMilliseconds(SpinLen) };
        S.Phase = Spinning;
        S.Until = S.Spin.Until;
        Glek("call", Pick(RouletteLines.Call));
        _dirty = true;
        return null;
    }

    /// <summary>
    /// Кулька лягла: виплати (поза замком), останнє коло, історія, серії, ачівки, таблиця, Журнал, Глек.
    /// <paramref name="quiet"/> — коло вже розраховане раніше (каса його виплатила, а соло-стан у сховищі лишився в «spin»):
    /// лише стан — ні каси, ні ачівок, ні таблиці, ні Журналу, ні балачки, і Глек мовчить.
    /// </summary>
    void Settle(bool quiet = false)
    {
        var spin = S.Spin!;
        var n = spin.N;
        var now = Now;
        var pending = S.Pending;
        if (pending is not null && !quiet) _book?.Settle(pending);

        var results = new List<(RouletteResult R, RoulettePaid P, List<RouletteChip> Bets)>();
        foreach (var p in S.Paid)
        {
            var key = Key(p.Nick);
            var bets = HandOf(key)?.Bets ?? [];
            var ret = pending?.Pays.FirstOrDefault(x => Key(x.Nick) == key)?.Return ?? Returns(bets, n);
            var r = new RouletteResult
            {
                Nick = p.Nick, Color = p.Color, Staked = p.Stake, Paid = ret, Net = ret - p.Stake,
                Hits = [.. bets.Where(b => RouletteCore.Covers(b.Spot).Contains(n)).Select(b => b.Spot)],
            };
            results.Add((r, p, bets));
            S.Wallets[key] = (int)Math.Min(int.MaxValue, (long)S.Wallets.GetValueOrDefault(key) + ret);
            if (bets.Count > 0) S.LastBets[key] = Clone(bets);
            var red = bets.Any(b => b.Spot == "red") && RouletteCore.ColorOf(n) == "r";
            S.Red[key] = red ? S.Red.GetValueOrDefault(key) + 1 : 0;
        }
        var ordered = results.OrderByDescending(x => x.R.Net).ToList();
        var staked = (int)Math.Min(int.MaxValue, results.Sum(x => (long)x.R.Staked));
        var paidOut = (int)Math.Min(int.MaxValue, results.Sum(x => (long)x.R.Paid));
        var lost = results.Sum(x => (long)x.R.Staked - x.R.Paid);

        // Гопак: хтось виграв число зі ставкою ≥ 10 на ньому, або чиєсь net ≥ 1000.
        bool Dances((RouletteResult R, RoulettePaid P, List<RouletteChip> Bets) x) =>
            x.R.Net >= 1000 || x.Bets.Any(b => RouletteCore.Type(b.Spot) == "straight" && b.Amount >= 10 && x.R.Hits.Contains(b.Spot));
        var dancer = ordered.FirstOrDefault(Dances);
        string mood, say;
        string? big = null;
        if (dancer.R is not null)
        {
            mood = "dance";
            big = dancer.R.Nick;
            say = RouletteLines.Number(n) + " " + string.Format(Pick(RouletteLines.Dance), big, n);
        }
        else if (n == 0 && lost > 0) { mood = "laugh"; say = Pick(RouletteLines.Zero); }
        else if (paidOut > staked) { mood = "clap"; say = RouletteLines.Number(n) + " " + Pick(RouletteLines.Clap); }
        else { mood = "rake"; say = RouletteLines.Number(n) + " " + Pick(RouletteLines.Rake); }

        S.Last = new RouletteLast { No = spin.No, N = n, Staked = staked, Paid = paidOut, Big = big, Results = [.. ordered.Select(x => x.R)] };
        S.History.Insert(0, n);
        if (S.History.Count > HistoryLen) S.History.RemoveRange(HistoryLen, S.History.Count - HistoryLen);
        if (!quiet)
        {
            Glek(mood, say);
            Announce(n, now, ordered, mood, say, lost);
        }
        FinishSettle(now);
    }

    /// <summary>Розрахунок для людей: ачівки, таблиця, балачка столу, Журнал. Лише раз на коло — у мить, коли кулька лягла.</summary>
    void Announce(int n, DateTimeOffset now, List<(RouletteResult R, RoulettePaid P, List<RouletteChip> Bets)> ordered,
        string mood, string say, long lost)
    {
        // Ва-банк: увесь гаманець (від 50) на одне коло.
        var allIn = ordered.FirstOrDefault(x => x.P.Wallet0 >= 50 && x.P.Stake == x.P.Wallet0);

        // Ачівки й таблиця — лише тим, хто в мить розрахунку сидить.
        foreach (var (r, p, bets) in ordered)
        {
            if (SeatOf(Key(r.Nick)) is not { } seat) continue;
            if (r.Hits.Any(h => RouletteCore.Type(h) == "straight")) Ctx.Award(seat, 0, "ach:roulette-straight");
            if (r.Hits.Contains("straight:0")) Ctx.Award(seat, 0, "ach:roulette-zero");
            if (p.Wallet0 >= 50 && p.Stake == p.Wallet0 && r.Net > 0) Ctx.Award(seat, 0, "ach:roulette-allin");
            if (S.Red.GetValueOrDefault(Key(r.Nick)) >= 5) Ctx.Award(seat, 0, "ach:roulette-red5");
            if (r.Net >= 1000) Ctx.Award(seat, 0, "ach:roulette-hopak");
            if (r.Net > 0) Ctx.Score(seat, r.Net);
        }

        // Балачка столу — щонайбільше рядок на коло.
        if (!Solo)
        {
            if (mood == "dance") Ctx.Say(say);
            else if (allIn.R is not null)
                Ctx.Say(string.Format(allIn.R.Net > 0 ? RouletteLines.AllInWon : RouletteLines.AllInLost, allIn.R.Nick));
            else if (mood == "laugh" && lost >= 50) Ctx.Say(say);
        }

        // Журнал: великий виграш або влучне число зі ставкою ≥ 50, не частіше раз на 10 хв на стіл.
        var star = ordered.FirstOrDefault(x => x.R.Net > 0 && (x.R.Net >= 2000
            || x.Bets.Any(b => RouletteCore.Type(b.Spot) == "straight" && b.Amount >= 50 && x.R.Hits.Contains(b.Spot))));
        if (star.R is not null && (S.JournalAt is not { } at || now - at >= JournalGap))
        {
            S.JournalAt = now;
            Ctx.Log($"🎡 {(Solo ? "Рулетка сам на сам" : "Рулетка")}: {star.R.Nick} виграє {star.R.Net} черепків — випало {n}");
        }
    }

    /// <summary>Коло позаду: соло — знову ставки, стіл — 5 с показати виграші.</summary>
    void FinishSettle(DateTimeOffset now)
    {
        S.Pending = null;
        S.Paid = [];
        if (Solo)
        {
            S.Hands.Clear();
            S.Phase = Bets;
            S.Until = null;
        }
        else
        {
            S.Phase = Result;
            S.Until = now.AddMilliseconds(ResultMs);
        }
        _dirty = true;
    }

    /// <summary>Скільки поверне рука, коли випало <paramref name="n"/>. <see cref="TooBig"/> не пускає на стіл того, що не влізе в int.</summary>
    static int Returns(List<RouletteChip> bets, int n) =>
        (int)Math.Min(int.MaxValue, bets.Sum(b => RouletteCore.Return(b.Spot, b.Amount, n)));

    // ---------- вид ----------

    public override object View(int? seat)
    {
        var now = Now;
        var nick = seat is { } s ? Ctx.NickOf(s) : null;
        var me = nick is null ? null : Key(nick);
        int? phaseMs = S.Phase switch
        {
            Bets => Solo ? null : (int?)BetMs,
            Spinning => SpinLen,
            Result => ResultMs,
            _ => null,
        };
        var until = S.Phase switch
        {
            Bets => Solo ? null : S.Until,
            Spinning => S.Spin?.Until ?? S.Until,
            Result => S.Until,
            _ => null,
        };
        return new
        {
            mode = Solo ? "solo" : "table",
            phase = S.Phase,
            until,
            leftMs = until is { } u ? Left(u, now) : (int?)null,
            phaseMs,
            spin = S.Spin is { } sp
                ? new { no = sp.No, n = sp.N, c = RouletteCore.ColorOf(sp.N), until = sp.Until, leftMs = Left(sp.Until, now), ms = SpinLen }
                : null,
            history = S.History.Select(x => new { n = x, c = RouletteCore.ColorOf(x) }).ToArray(),
            players = Players(me),
            onTable = (int)Math.Min(int.MaxValue, S.Hands.Sum(h => (long)h.Total)),
            last = S.Last is { } l
                ? new
                {
                    no = l.No, n = l.N, c = RouletteCore.ColorOf(l.N), staked = l.Staked, paid = l.Paid,
                    results = l.Results.Select(r => new { nick = r.Nick, color = r.Color, staked = r.Staked, paid = r.Paid, net = r.Net, hits = r.Hits.ToArray() }).ToArray(),
                    big = l.Big,
                }
                : null,
            glek = new { mood = S.Glek.Mood, say = S.Glek.Say, seq = S.Glek.Seq },
            me = me is null ? null : Me(me, now),
            closed = _book is null,
        };
    }

    static int Left(DateTimeOffset until, DateTimeOffset now) => (int)Math.Clamp((until - now).TotalMilliseconds, 0, int.MaxValue);

    object[] Players(string? me)
    {
        var list = new List<object>();
        var seated = new HashSet<string>(StringComparer.Ordinal);
        for (var seat = 0; seat < Info.MaxPlayers; seat++)
        {
            if (Ctx.NickOf(seat) is not { } nick) continue;
            var key = Key(nick);
            seated.Add(key);
            var hand = HandOf(key);
            list.Add(Player(nick, seat, seat, true, key == me, hand));
        }
        foreach (var hand in S.Hands)
        {
            var key = Key(hand.Nick);
            if (seated.Contains(key) || hand.Total <= 0) continue;
            list.Add(Player(hand.Nick, null, hand.Color, false, false, hand));
        }
        return [.. list];
    }

    static object Player(string nick, int? seat, int color, bool here, bool mine, RouletteHand? hand) => new
    {
        nick, seat, color, here, mine,
        total = hand?.Total ?? 0,
        bets = (hand?.Bets ?? []).Select(b => new { spot = b.Spot, amount = b.Amount }).ToArray(),
    };

    object Me(string key, DateTimeOffset now)
    {
        var hand = HandOf(key);
        var wallet = S.Wallets.GetValueOrDefault(key);
        var onTable = hand?.Total ?? 0;
        var open = BetsOpen(now);
        // У bets фішки — лише намір; після «Ставки зроблено!» вони вже списані й сидять у гаманці мінусом.
        var free = (int)Math.Max(0, (long)wallet - (open ? onTable : 0));
        var repeatCost = S.LastBets.TryGetValue(key, out var last) ? Sum(last) : 0;
        return new
        {
            wallet,
            onTable,
            free,
            canUndo = open && hand is { Steps.Count: > 0 },
            canRepeat = open && repeatCost > 0 && repeatCost <= free,
            repeatCost,
            canDouble = open && onTable > 0 && onTable <= free,
            note = S.Notes.GetValueOrDefault(key),
        };
    }

    bool BetsOpen(DateTimeOffset now) => Solo
        ? S.Phase == Bets
        : S.Phase == Idle || (S.Phase == Bets && S.Until is { } u && now < u);

    // ---------- Save/Load ----------

    /// <summary>Увесь стан (§3.5): фаза, дедлайни, ставки, незакрите коло, історія, серії, Глек, кеш гаманців.</summary>
    public override string? Save() => JsonSerializer.Serialize(S, Json);

    /// <summary>Новий epoch на кожне відновлення: коло зі старого стану ніколи не перепише ключ уже списаного.</summary>
    public override void Load(string json)
    {
        var s = JsonSerializer.Deserialize<RouletteState>(json, Json) ?? throw new InvalidOperationException("порожній стан рулетки");
        if (s.Phase is not (Idle or Bets or Spinning or Result) || (s.Phase == Spinning && s.Spin is null))
            throw new InvalidOperationException($"невідома фаза рулетки: {s.Phase}");
        if (Solo && s.Phase is Idle or Result) s.Phase = Bets;
        s.Epoch = NewEpoch();
        S = s;
        // Соло каркас зберігає лише після дії, а кулька лягає в тику — тож у сховищі коло могло лишитись у «spin», хоча
        // його давно розраховано. Каса вже не тримає запису — значить, виплачено: доводимо стан мовчки, бо Журнал, таблиця,
        // ачівки й Глек уже своє сказали тоді (інакше кожне відкриття повторювало б їх). Касу не прочитати — граємо як є:
        // гроші однаково підуть раз (ключі леджера).
        _settledOnLoad = false;
        if (S.Phase == Spinning && _book is not null && (S.Pending is null || _book.Holds(S.Pending.Table, S.Pending.Spin) == false))
        {
            Settle(quiet: true);
            Glek("idle", null);
            _settledOnLoad = true;
        }
        // Кеш гаманців зі збереження застарів (між відкриттями гаманець жив своїм життям) — свіжі для всіх, хто сидить.
        for (var seat = 0; seat < Info.MaxPlayers; seat++) Refresh(seat);
        _dirty = true;
    }

    /// <summary>Чи стіл чекає саме на це коло (його кулька ще не лягла) — тоді каса його не чіпає (<see cref="RouletteBook.HeldBy"/>).</summary>
    public bool Holds(string table, int spin) =>
        S.Phase == Spinning && S.Pending is { } p && p.Spin == spin && string.Equals(p.Table, table, StringComparison.Ordinal);

    public override void Resumed(TimeSpan pause)
    {
        if (!_settledOnLoad) S.Until += pause;
        _settledOnLoad = false;
        if (S.Spin is { } spin)
        {
            spin.At += pause;
            spin.Until += pause;
        }
        S.LastActionAt += pause;
        _dirty = true;
    }

    // ---------- дрібниці ----------

    string NewEpoch() => Ctx.Seeded
        ? ((uint)Ctx.Rng.Next(int.MinValue, int.MaxValue)).ToString("x8")
        : RandomNumberGenerator.GetHexString(8, lowercase: true);

    void Glek(string mood, string? say)
    {
        S.Glek = new RouletteGlek { Mood = mood, Say = say, Seq = S.Glek.Seq + 1 };
        _dirty = true;
    }

    string Pick(string[] bank) => RouletteLines.Pick(bank, Ctx.Rng);

    static string Key(string nick) => Rooms.NickKey(nick);

    RouletteHand? HandOf(string key) => S.Hands.FirstOrDefault(h => Key(h.Nick) == key);

    RouletteHand HandFor(string nick, string key, int seat)
    {
        if (HandOf(key) is { } hand)
        {
            hand.Nick = nick;
            if (hand.Total <= 0) hand.Color = seat;
            return hand;
        }
        hand = new RouletteHand { Nick = nick, Color = seat };
        S.Hands.Add(hand);
        return hand;
    }

    /// <summary>Колір фішок: сидить — його місце, пішов — місце в мить першої ставки.</summary>
    int ColorFor(RouletteHand hand) => SeatOf(Key(hand.Nick)) ?? hand.Color;

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

    static int Sum(List<RouletteChip> bets) => (int)Math.Min(int.MaxValue, bets.Sum(b => (long)b.Amount));

    static List<RouletteChip> Clone(List<RouletteChip> bets) => [.. bets.Select(b => new RouletteChip { Spot = b.Spot, Amount = b.Amount })];

    /// <summary>Гаманець у кеші перечитати (людина сіла за стіл).</summary>
    protected void Refresh(int seat)
    {
        if (_book is not null && Ctx.NickOf(seat) is { } nick) S.Wallets[Key(nick)] = _book.Balance(nick);
        _dirty = true;
    }

    protected void Touch() => _dirty = true;
}

/// <summary>
/// Рулетка за столом: спільне колесо для всіх, хто сидить. Глек крутить сам за розкладом (ставки 25 с → крутиться 6 с →
/// виплати 5 с → ставки…), підсісти можна будь-коли, встав — ставки лишаються й зіграють.
/// </summary>
public sealed class Roulette : RouletteGame
{
    public override GameInfo Info { get; } = new(
        "roulette", "Рулетка", "рулетку", GameGroup.Party, 1, 8, TickMs: TickMs, Start: StartMode.Immediate, Hidden: true,
        Score: ScoreOrder.HigherIsBetter, Coop: true,
        Hint: "Дядько Глек крутить колесо для всіх: ставиш черепки — і молишся на кульку. Європейська, одне зеро");

    protected override bool Solo => false;

    /// <summary>Save тримає ввесь стан (дедлайни, незакрите коло), тож деплой стіл не чекає.</summary>
    public override bool Resumable => true;

    public override bool LateJoin(string nick) => true;

    public override string? LateJoinGreeting(string nick) => "Сідай ближче! Ставки — до «Ставки зроблено!», черепки — з гаманця";

    public override void OnJoin(int seat) => Refresh(seat);

    /// <summary>Не техпоразка: ставки людини лишаються на столі й розраховуються.</summary>
    public override void OnLeave(int seat) => Touch();

    /// <summary>Стіл без людей ще хвилину докручує те, що на ньому лежить (найдовше — 25 + 6 + 5 с).</summary>
    public override TimeSpan HoldEmpty => TimeSpan.FromSeconds(60);
}

/// <summary>Рулетка сам на сам: своє колесо — ставиш і тиснеш «Крутити», коли хочеш. Стан — у game_state після кожної дії.</summary>
public sealed class RouletteSolo : RouletteGame
{
    public override GameInfo Info { get; } = new(
        "roulette-solo", "Рулетка: сам на сам", "рулетку сам на сам", GameGroup.Solo, 1, 1, TickMs: TickMs,
        Start: StartMode.Immediate, Private: true, Persistent: true, Score: ScoreOrder.HigherIsBetter,
        Hint: "Своє колесо: ставиш, тиснеш «Крутити» — і Глек кидає кульку лише для тебе", Client: "roulette");

    protected override bool Solo => true;
}
