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
public abstract partial class SlotGame : Game
{
    public const string ActSpin = "spin", ActGamble = "gamble", ActCollect = "collect", ActBuy = "buy";
    public const int GambleMax = 5;
    public const string OffText = "Автомати на перерві — Глек змащує барабани";
    public const string ClosedText = "Автомати зачинені — каса не відповідає";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    SlotsBank? _bank;
    GlekBonus? _bonus;
    SlotFreeSpins? _free;

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
        ConfigureTourney();   // турнір тижня (SlotTourneyGame.cs)
        try { _bonus = Ctx?.Services?.GetService<GlekBonus>(); }
        catch { _bonus = null; }
        try { _free = Ctx?.Services?.GetService<SlotFreeSpins>(); }
        catch { _free = null; }
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
            daily = o.JackpotDaily > 0 ? o.JackpotDaily : (int?)null,   // пакет 5: «Глек докидає N щодня» (null — не докидає)
            last = State.Last is null ? null : new { seq = State.Seq, script = State.Last.DeepClone() },
            gamble = g is null ? null : new
            {
                amount = g.Amount, steps = g.Steps, max = GambleMax, open = g.Open, n = g.N,
                pick = g.Pick, card = g.Card, ok = g.Ok, hist = g.Hist.ToArray(),
            },
            best = State.Best,
            spins = State.Spins,
            table = SlotMath.Table(),
            buy = BuyPrice(o) is { } price ? new { price } : null,   // «Купити бонус»: ціна в ставках (null — не продається)
            saldo = SaldoWire(seat),   // пакет 5: чесне сальдо всіх автоматів (null — сервісу нема)
            tourney = TourneyView(seat),   // турнір тижня (SlotTourneyGame.cs); null — вимкнено
            dobavka = _bonus?.View(seat is { } s ? Ctx.NickOf(s) : null),   // доплата Глека: { pct, cap, left } (null — нема)
            free = o.Enabled ? _free?.View(seat is { } s2 ? Ctx.NickOf(s2) : null) : null,   // безкоштовні прокрути: { bet, total, left, person }
        };
    }

    /// <summary>Пакет 5 «Азарт чесно»: поставив / повернув у всіх автоматах — сьогодні й за весь час (з пам'яті сервісу).</summary>
    object? SaldoWire(int? seat)
    {
        if (seat is null || Ctx?.NickOf(0) is not { } nick) return null;
        try { return (Ctx.Services?.GetService(typeof(Hlechyky.Games.Economy.IAzartSaldo)) as Hlechyky.Games.Economy.IAzartSaldo)?.Get("slot", nick).Wire(); }
        catch { return null; }
    }

    // ---------- дії ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action == ActTourney) return TourneySpin(seat, payload);   // турнір тижня на фішках (SlotTourneyGame.cs)
        if (action is not (ActSpin or ActGamble or ActCollect or ActBuy)) return ActResult.Fail("Тут так не ходять");
        if (_bank is null) return ActResult.Fail(ClosedText);
        if (Ctx.NickOf(seat) is not { } nick) return ActResult.Fail("Тут так не ходять");
        return action switch
        {
            ActSpin => Spin(seat, nick, payload, false),
            ActBuy => Spin(seat, nick, payload, true),
            ActGamble => Gamble(seat, nick, payload),
            _ => Collect(),
        };
    }

    string Ref(int seq) => $"slot:{Ctx.RoomId}-{State.Epoch}:{seq}";

    /// <summary>Ціна купленого бонусу в ставках, якщо купівля зараз увімкнена в цьому автоматі; інакше null.</summary>
    int? BuyPrice(SlotsOptions o) => SlotMath is ISlotBuyBonus b && o.BuyBonus ? o.BuyPriceFor(b.BuyMinPrice) : null;

    /// <summary>
    /// Оберт (<paramref name="buy"/> = false) або куплений бонус: та сама послідовність грошей, лише списується ціна
    /// (ставка × <see cref="SlotsOptions.BuyPrice"/>) з причиною <c>slot-buy</c>, а сценарій — <see cref="ISlotBuyBonus.Buy"/>.
    /// Внесок у Скарбничку — з усієї списаної суми (кожна ставка кладе свій відсоток).
    /// Безкоштовний прокрут (<c>free: true</c>, <see cref="SlotFreeSpins"/>): ставка — з налаштувань, нічого не списуємо,
    /// Скарбничку не годуємо, доплати Глека нема; виграш — тим самим журналом каси.
    /// </summary>
    ActResult Spin(int seat, string nick, JsonElement payload, bool buy)
    {
        var bank = _bank!;
        var o = bank.O;
        if (!o.Enabled) return ActResult.Fail(OffText);
        // Повтор того самого запиту (клієнт шле seq, який бачив): оберт уже пішов — нічого не робимо, вид і так новий.
        if (Int(payload, "seq") is { } seen && seen != State.Seq) return ActResult.Done;
        var free = !buy && payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("free", out var f) && f.ValueKind == JsonValueKind.True;
        var price = buy ? BuyPrice(o) : 1;
        if (price is null) return ActResult.Fail("Бонус тут не продається — крути, горно саме розгориться");
        var bets = o.AllowedBets();
        int bet;
        if (free)
        {
            if (_free is not { } fs || fs.Total <= 0) return ActResult.Fail("Безкоштовних прокрутів сьогодні нема");
            if (!fs.Take(nick))
                return ActResult.Fail(fs.Person(nick) ? "Безкоштовні прокрути на сьогодні скінчились — нові опівночі"
                    : "Безкоштовні прокрути — лише для акаунтів");
            bet = fs.Bet;
        }
        else if (Int(payload, "bet") is not { } b || !bets.Contains(b))
            return ActResult.Fail($"Така ставка не йде: {string.Join(", ", bets)}");
        else bet = b;
        var cost = free ? 0 : bet * price.Value;
        var wallet = bank.Balance(nick);
        State.Wallet = wallet;
        if (wallet < cost)
            return ActResult.Fail(buy ? $"Бракує черепків: у гаманці {wallet}, а бонус коштує {cost}" : $"Бракує черепків: у гаманці {wallet}, а ставка {bet}");

        var seq = State.Seq + 1;
        var reference = Ref(seq);
        var rng = R;
        var outcome = buy ? ((ISlotBuyBonus)SlotMath).Buy(bet, rng) : SlotMath.Spin(bet, rng, State.MathState);
        if (buy) outcome.Script["price"] = cost;
        var jackpot = free ? 0 : bank.Feed(cost, rng);
        // Доплата Глека: +N % до виграшу (стеля на день) — одразу в журнал, щоб відновлення після падіння платило те саме
        var bonus = !free && outcome.Win > 0 && _bonus is { } gb ? gb.Take(nick, outcome.Win) : 0;
        var win = outcome.Win + bonus;
        var pending = new SlotPending(reference, nick, Info.Id, cost, win, jackpot, Ctx.Clock.UtcNow);
        var journal = win > 0 || jackpot > 0;
        if (journal && !bank.Open(pending))
        {
            if (!free) bank.Unfeed(cost, jackpot);
            if (bonus > 0) _bonus!.Return(nick, bonus);
            if (free) _free!.Return(nick);
            return ActResult.Fail(ClosedText);
        }
        var taken = free ? true : bank.Charge(nick, cost, buy ? $"slot-buy:{Info.Id}" : $"slot-bet:{Info.Id}", reference + ":bet");
        if (taken is null)
        {
            // Списання могло й пройти: запис журналу (і внесок у Скарбничку) лишаємо — підмітання звірить із леджером
            // і доплатить виграш (з доплатою Глека — з обліку її не знімаємо) або прибере запис. Ключ цього оберту більше не беремо. Старий сценарій і Ворожку —
            // геть: інакше вид дав би «last» з новим seq і минулим обертом, і клієнт програв би його як новий.
            State.Seq = seq;
            State.Last = null;
            State.Gamble = null;
            return ActResult.Fail(ClosedText);
        }
        if (taken == false)
        {
            if (journal) bank.Forget(reference);
            bank.Unfeed(cost, jackpot);
            if (bonus > 0) _bonus!.Return(nick, bonus);
            State.Wallet = bank.Balance(nick);
            return ActResult.Fail(buy ? $"Бракує черепків: у гаманці {State.Wallet}, а бонус коштує {cost}" : $"Бракує черепків: у гаманці {State.Wallet}, а ставка {bet}");
        }
        if (journal) bank.Close(pending);   // не вийшло — запис лишився, підмітання доплатить тими самими ключами

        var script = outcome.Script;
        if (bonus > 0)
        {
            // перед «jackpot»: кіт спершу докручує лічильник на доплату, тоді Скарбничка
            ((JsonArray)script["steps"]!).Add(new JsonObject { ["t"] = "dobavka", ["amount"] = bonus, ["base"] = outcome.Win });
            script["win"] = win;
            script["dobavka"] = bonus;
        }
        if (jackpot > 0)
        {
            ((JsonArray)script["steps"]!).Add(new JsonObject { ["t"] = "jackpot", ["amount"] = jackpot });
            script["jackpot"] = jackpot;
        }
        if (free) script["free"] = new JsonObject { ["bet"] = bet, ["left"] = _free!.Left(nick), ["total"] = _free.Total };
        State.Seq = seq;
        if (!free) State.Bet = bet;   // безкоштовний іде своєю ставкою — обрану людиною не чіпаємо
        State.Last = script;
        State.MathState = outcome.State;
        State.Gamble = SlotMath.Gamble && win > 0 ? new SlotGamble { Amount = win, Open = true } : null;
        State.Spins++;
        State.Staked += cost;
        State.Won += win + jackpot;
        State.Wallet = wallet - cost + win + jackpot;

        // множник заносу — від того, що заплачено: у купленому бонусі це ціна, а не ставка (інакше «×30» за бонус, що коштував 100×);
        // виграш — з доплатою Глека (те, що людина бачить)
        // безкоштовний — від номінальної ставки: це справжній оберт тієї ставки
        var basis = free ? bet : cost;
        var mult = Math.Round(basis > 0 ? (double)win / basis : 0, 2);
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
        bank.Brag(nick, Info.Id, Info.Title, basis, win, jackpot);
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
