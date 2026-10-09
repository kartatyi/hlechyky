using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Hlechyky.Games.Impl;

/// <summary>Ворожка (лише slot-glek): що на кону, скільки вгадано, останній хід.</summary>
public sealed class SlotGamble
{
    /// <summary>На кону зараз (уже в гаманці — ставкою Ворожки він списується лише на ході).</summary>
    public int Amount { get; set; }
    /// <summary>Скільки разів поспіль вгадано (до <see cref="SlotGame.GambleMax"/>).</summary>
    public int Steps { get; set; }
    /// <summary>Можна ще вгадувати. Програв чи дійшов до п'яти — false, а результат лишається для показу.</summary>
    public bool Open { get; set; }
    /// <summary>Номер ходу Ворожки в цьому оберті — клієнт програє карту, коли він новий; він же в ключах леджера.</summary>
    public int N { get; set; }
    public string? Pick { get; set; }
    public string? Card { get; set; }
    public bool? Ok { get; set; }
    /// <summary>Останні карти, свіжа — перша (до 6).</summary>
    public List<string> Hist { get; set; } = [];
}

/// <summary>Стан автомата одного гравця (Persistent: живе між заходами).</summary>
public sealed class SlotState
{
    /// <summary>Номер останнього оберту — <c>last.seq</c> у виді.</summary>
    public int Seq { get; set; }
    public int Bet { get; set; }
    /// <summary>Останній сценарій — клієнт програє його, коли <see cref="Seq"/> новий.</summary>
    public JsonObject? Last { get; set; }
    public SlotGamble? Gamble { get; set; }
    /// <summary>Стан математики між обертами (у slot-glek порожній).</summary>
    public JsonObject MathState { get; set; } = new();
    /// <summary>Найбільший множник за оберт — за весь час і за день (для Ctx.Score: таблиця тримає найкраще за день).</summary>
    public double Best { get; set; }
    public string Day { get; set; } = "";
    public double DayBest { get; set; }
    public int Spins { get; set; }
    public long Staked { get; set; }
    public long Won { get; set; }
    /// <summary>Ачівки, про які вже попросили (щоб не слати сигнал на кожен занос).</summary>
    public List<string> Ach { get; set; } = [];

    /// <summary>
    /// Епоха процесу: нова на кожен <see cref="SlotGame.Start"/> і <see cref="SlotGame.Load"/>. Входить у ключі леджера, бо
    /// після падіння стан може відкотитись до старшого Save, і <see cref="Seq"/> повторився б — а повтор ключа списання
    /// economy вважає «вже списано» (оберт задарма). Нова епоха робить ключ унікальним завжди.
    /// </summary>
    [JsonIgnore] public string Epoch { get; set; } = "";
    /// <summary>Гаманець на мить останньої дії (у вид, щоб не ходити в базу з View).</summary>
    [JsonIgnore] public int Wallet { get; set; }
}

/// <summary>
/// Спільна база автоматів (docs/games/specs/slots.md §2): соло, приватна, Persistent, старт одразу. Оберт рахує сервер
/// цілком в одному <see cref="Act"/> — ставка, сценарій (з бонусом наперед), виграш, Скарбничка; клієнт лише програє
/// <c>last.script</c>. Математика — окремим чистим класом <see cref="ISlotMath"/>; новий автомат = нащадок із
/// <see cref="Info"/> і <see cref="SlotMath"/>.
///
/// Гроші й падіння процесу. Порядок оберту: (1) порахувати результат і Скарбничку (чисто, в пам'яті); (2) виграшний
/// оберт — запис у журнал каси <see cref="SlotsBank.Open"/>, синхронно; (3) списати ставку ключем <c>{ref}:bet</c>;
/// (4) нарахувати <c>{ref}:win</c> і <c>{ref}:jp</c>, прибрати запис. Впало між (2) і (3) — списання нема, відновлення
/// прибере запис; між (3) і (4) — відновлення на старті (чи підмітання за 2 хв) знайде списання в леджері й нарахує тими
/// самими ключами. Повтор будь-якого кроку нічого не подвоїть: ключі ідемпотентні, а епоха в ключі (<see cref="SlotState.Epoch"/>)
/// не дасть відкоченому стану повторити старий ключ. Програшний оберт журналу не пише: там лише списання, губити нічого.
/// </summary>
public abstract class SlotGame : Game
{
    public const string ActSpin = "spin", ActGamble = "gamble", ActCollect = "collect";
    public const int GambleMax = 5;
    public const string OffText = "Автомати на перерві — Глек змащує барабани";
    public const string ClosedText = "Автомати зачинені — каса не відповідає";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    SlotsBank? _bank;

    /// <summary>Математика цього автомата.</summary>
    protected abstract ISlotMath SlotMath { get; }

    public SlotState State { get; private set; } = new();

    /// <summary>Підставна випадковість (тести). null — сід кімнати в тестах, <see cref="CryptoSlotRng"/> у проді.</summary>
    public ISlotRng? Rng { get; set; }

    ISlotRng R => Rng ?? (Ctx.Seeded ? new SeededSlotRng(Ctx.Rng) : CryptoSlotRng.Instance);
    SlotsOptions O => _bank?.O ?? new SlotsOptions();

    public override string SeatName(int seat) => "гравець";

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        try { _bank = Ctx?.Services?.GetService<SlotsBank>(); }
        catch { _bank = null; }
    }

    /// <summary>Відкриття автомата (і «Ще раз»): статистика й останній сценарій лишаються, епоха — нова.</summary>
    public override void Start()
    {
        State.Epoch = NewEpoch();
        Refresh();
    }

    /// <summary>
    /// У збереження — без цілого сценарію: після перезавантаження клієнт старий оберт не програє, лише ставить його поле
    /// (перший крок spin/set), тож решта кроків (каскади, респіни, бонус — до ~45 КБ у кластері) у базі зайва.
    /// Повний сценарій живе в пам'яті для виду поточної вкладки.
    /// </summary>
    public override string? Save()
    {
        var full = State.Last;
        try
        {
            State.Last = FieldOnly(full);
            return JsonSerializer.Serialize(State, Json);
        }
        finally { State.Last = full; }
    }

    /// <summary>Сценарій → лише поле останнього оберту (перший крок spin/set) і виграш.</summary>
    public static JsonObject? FieldOnly(JsonObject? script)
    {
        if (script is null) return null;
        var field = (script["steps"] as JsonArray)?.FirstOrDefault(s => s?["t"]?.GetValueKind() == JsonValueKind.String
            && s["t"]!.GetValue<string>() is "spin" or "set");
        var trimmed = new JsonObject { ["steps"] = field is null ? new JsonArray() : new JsonArray(field.DeepClone()) };
        if (script["win"] is { } win) trimmed["win"] = win.DeepClone();
        return trimmed;
    }

    public override void Load(string json)
    {
        State = JsonSerializer.Deserialize<SlotState>(json, Json) ?? new SlotState();
        State.MathState ??= new JsonObject();
        State.Hist();
        State.Epoch = NewEpoch();
        Refresh();
    }

    void Refresh()
    {
        if (_bank is not null && Ctx?.NickOf(0) is { } nick) State.Wallet = _bank.Balance(nick);
    }

    static string NewEpoch() => RandomNumberGenerator.GetHexString(8, lowercase: true);

    // ---------- вид ----------

    public override object View(int? seat)
    {
        var o = O;
        var bets = o.AllowedBets();
        var g = State.Gamble;
        return new
        {
            on = o.Enabled && _bank is not null,
            bet = State.Bet > 0 && bets.Contains(State.Bet) ? State.Bet : bets.FirstOrDefault(),
            bets,
            maxBet = o.MaxBet,
            balance = seat is null ? 0 : State.Wallet,
            jackpot = _bank?.Pot ?? 0,
            mustHit = o.MustHitOn ? o.JackpotMustHit : (int?)null,   // «має впасти до» (null — межі нема)
            last = State.Last is null ? null : new { seq = State.Seq, script = State.Last.DeepClone() },
            gamble = g is null ? null : new
            {
                amount = g.Amount, steps = g.Steps, max = GambleMax, open = g.Open, n = g.N,
                pick = g.Pick, card = g.Card, ok = g.Ok, hist = g.Hist.ToArray(),
            },
            best = State.Best,
            spins = State.Spins,
            table = SlotMath.Table(),
        };
    }

    // ---------- дії ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action is not (ActSpin or ActGamble or ActCollect)) return ActResult.Fail("Тут так не ходять");
        if (_bank is null) return ActResult.Fail(ClosedText);
        if (Ctx.NickOf(seat) is not { } nick) return ActResult.Fail("Тут так не ходять");
        return action switch
        {
            ActSpin => Spin(seat, nick, payload),
            ActGamble => Gamble(seat, nick, payload),
            _ => Collect(),
        };
    }

    string Ref(int seq) => $"slot:{Ctx.RoomId}-{State.Epoch}:{seq}";

    ActResult Spin(int seat, string nick, JsonElement payload)
    {
        var bank = _bank!;
        var o = bank.O;
        if (!o.Enabled) return ActResult.Fail(OffText);
        // Повтор того самого запиту (клієнт шле seq, який бачив): оберт уже пішов — нічого не робимо, вид і так новий.
        if (Int(payload, "seq") is { } seen && seen != State.Seq) return ActResult.Done;
        var bets = o.AllowedBets();
        if (Int(payload, "bet") is not { } bet || !bets.Contains(bet))
            return ActResult.Fail($"Така ставка не йде: {string.Join(", ", bets)}");
        var wallet = bank.Balance(nick);
        State.Wallet = wallet;
        if (wallet < bet) return ActResult.Fail($"Бракує черепків: у гаманці {wallet}, а ставка {bet}");

        var seq = State.Seq + 1;
        var reference = Ref(seq);
        var rng = R;
        var outcome = SlotMath.Spin(bet, rng, State.MathState);
        var jackpot = bank.Feed(bet, rng);
        var pending = new SlotPending(reference, nick, Info.Id, bet, outcome.Win, jackpot, Ctx.Clock.UtcNow);
        var journal = outcome.Win > 0 || jackpot > 0;
        if (journal && !bank.Open(pending))
        {
            bank.Unfeed(bet, jackpot);
            return ActResult.Fail(ClosedText);
        }
        var taken = bank.Charge(nick, bet, $"slot-bet:{Info.Id}", reference + ":bet");
        if (taken is null)
        {
            // Списання могло й пройти: запис журналу (і внесок у Скарбничку) лишаємо — підмітання звірить із леджером
            // і доплатить виграш або прибере запис. Ключ цього оберту більше не беремо.
            State.Seq = seq;
            return ActResult.Fail(ClosedText);
        }
        if (taken == false)
        {
            if (journal) bank.Forget(reference);
            bank.Unfeed(bet, jackpot);
            State.Wallet = bank.Balance(nick);
            return ActResult.Fail($"Бракує черепків: у гаманці {State.Wallet}, а ставка {bet}");
        }
        if (journal) bank.Close(pending);   // не вийшло — запис лишився, підмітання доплатить тими самими ключами

        var script = outcome.Script;
        if (jackpot > 0)
        {
            ((JsonArray)script["steps"]!).Add(new JsonObject { ["t"] = "jackpot", ["amount"] = jackpot });
            script["jackpot"] = jackpot;
        }
        State.Seq = seq;
        State.Bet = bet;
        State.Last = script;
        State.MathState = outcome.State;
        State.Gamble = SlotMath.Gamble && outcome.Win > 0 ? new SlotGamble { Amount = outcome.Win, Open = true } : null;
        State.Spins++;
        State.Staked += bet;
        State.Won += outcome.Win + jackpot;
        State.Wallet = wallet - bet + outcome.Win + jackpot;

        var mult = Math.Round(outcome.Mult(bet), 2);
        if (mult > State.Best) State.Best = mult;
        var day = Days.Today(Ctx.Clock).ToString();
        if (day != State.Day) { State.Day = day; State.DayBest = 0; }
        if (mult > State.DayBest)
        {
            State.DayBest = mult;
            Ctx.Score(seat, mult);
        }
        if (mult >= 10) Ach(seat, "slot-big");
        if (mult >= 50) Ach(seat, "slot-epic");
        if (jackpot > 0) Ach(seat, "slot-jackpot");
        bank.Brag(nick, Info.Id, Info.Title, bet, outcome.Win, jackpot);
        return ActResult.Done;
    }

    ActResult Gamble(int seat, string nick, JsonElement payload)
    {
        var bank = _bank!;
        if (!SlotMath.Gamble) return ActResult.Fail("Тут Ворожки нема");
        if (!bank.O.Enabled) return ActResult.Fail(OffText);
        var g = State.Gamble;
        if (g is null || !g.Open || g.Amount <= 0 || g.Steps >= GambleMax) return ActResult.Fail("Ворожка вже пішла — крути далі");
        var pick = Str(payload, "pick");
        if (pick is not ("r" or "b")) return ActResult.Fail("Червона чи чорна?");

        var n = g.N + 1;
        var reference = $"{Ref(State.Seq)}:g{n}";
        var card = R.Next(2) == 0 ? "r" : "b";
        var ok = card == pick;
        var stake = g.Amount;
        var pending = new SlotPending(reference, nick, Info.Id, stake, ok ? stake * 2 : 0, 0, Ctx.Clock.UtcNow);
        if (ok && !bank.Open(pending)) return ActResult.Fail(ClosedText);
        var taken = bank.Charge(nick, stake, $"slot-bet:{Info.Id}", reference + ":bet");
        if (taken is null)
        {
            g.Open = false;   // як і в оберті: запис лишається підмітанню, Ворожка на цьому закінчилась
            return ActResult.Fail(ClosedText);
        }
        if (taken == false)
        {
            if (ok) bank.Forget(reference);
            g.Open = false;
            State.Wallet = bank.Balance(nick);
            return ActResult.Fail($"Бракує черепків: на кону {stake}, а в гаманці {State.Wallet}");
        }
        if (ok) bank.Close(pending);

        g.N = n;
        g.Pick = pick;
        g.Card = card;
        g.Ok = ok;
        g.Hist.Insert(0, card);
        if (g.Hist.Count > 6) g.Hist.RemoveRange(6, g.Hist.Count - 6);
        State.Wallet = Math.Max(0, State.Wallet - stake) + (ok ? stake * 2 : 0);
        if (ok)
        {
            g.Amount = stake * 2;
            g.Steps++;
            State.Won += stake;
            if (g.Steps >= GambleMax)
            {
                g.Open = false;
                Ach(seat, "slot-vorozhka");
            }
        }
        else
        {
            g.Amount = 0;
            g.Open = false;
            State.Won -= stake;
        }
        return ActResult.Done;
    }

    ActResult Collect()
    {
        State.Gamble = null;
        return ActResult.Done;
    }

    void Ach(int seat, string key)
    {
        if (State.Ach.Contains(key)) return;
        State.Ach.Add(key);
        Ctx.Award(seat, 0, "ach:" + key);
    }

    static int? Int(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    static string? Str(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

static class SlotStateExt
{
    /// <summary>Старе збереження без історії Ворожки — порожня історія замість null.</summary>
    public static void Hist(this SlotState s)
    {
        if (s.Gamble is { } g) g.Hist ??= [];
        s.Ach ??= [];
    }
}

/// <summary>«Однорукий Глек»: 3×3, 5 ліній, Глек дикий, Ворожка після виграшу. Математика — <see cref="SlotGlekMath"/>.</summary>
public sealed class SlotGlek : SlotGame
{
    static readonly SlotGlekMath MathImpl = new();

    public override GameInfo Info { get; } = new(
        "slot-glek", "Однорукий Глек", "Однорукого Глека", GameGroup.Solo, 1, 1, Start: StartMode.Immediate,
        Private: true, Persistent: true, Score: ScoreOrder.HigherIsBetter,
        Hint: "Ретро-автомат на 5 ліній: смикай ручку, лови три Глеки, а після виграшу — Ворожка ×2", Client: "slot");

    protected override ISlotMath SlotMath => MathImpl;
}

/// <summary>
/// «Цвіт папороті»: поле 7×7, кластери від 5, каскади, шкала папороті (світлячки, русалка, цвіт + 5 вільних) — усе в
/// одному оберті. Математика — <see cref="SlotClusterMath"/>.
/// </summary>
public sealed class SlotCluster : SlotGame
{
    static readonly SlotClusterMath MathImpl = new();

    public override GameInfo Info { get; } = new(
        "slot-cluster", "Цвіт папороті", "Цвіт папороті", GameGroup.Solo, 1, 1, Start: StartMode.Immediate,
        Private: true, Persistent: true, Score: ScoreOrder.HigherIsBetter,
        Hint: "Купальська ніч на полі 7×7: кластери квітів, каскади, світлячки й русалка, а цвіт папороті — вільні оберти", Client: "slot");

    protected override ISlotMath SlotMath => MathImpl;
}
