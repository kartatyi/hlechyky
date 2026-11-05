using static Hlechyky.Games.Impl.SolyanyiMap;

namespace Hlechyky.Games.Impl;

public enum SlPhase { Setup, Play, Draw, Keep, Over }

/// <summary>
/// Подія для анімацій і журналу (контракт §7), як <see cref="SoEvent"/>: одне поле може бути приховане
/// (<see cref="SecretKey"/> — його бачать лише <see cref="Insiders"/>, решта отримує null), а вся подія — призначена лише
/// одному місцю (<see cref="Only"/>): для інших її нема зовсім, <see cref="For"/> дає null. Така подія не бере номер зі спільного
/// <c>seq</c> (пропуск у ньому виказав би суперникам, що хтось виконав замовлення): її <see cref="Seq"/> — той, після якого вона
/// сталась, а порядковий номер — власний лічильник місця <see cref="Own"/>.
/// </summary>
public sealed class SlEvent
{
    public long Seq;
    public string K = "";
    public List<KeyValuePair<string, object?>> Data = [];
    public string? SecretKey;
    public object? Secret;
    public int[] Insiders = [];
    public int Only = -1;
    /// <summary>Номер події «лише власнику» в лічильнику її місця (<see cref="SolyanyiCore.OwnSeq"/>); 0 — подія спільна.</summary>
    public long Own;

    public Dictionary<string, object?>? For(int? seat)
    {
        if (Only >= 0 && seat != Only) return null;
        var d = new Dictionary<string, object?> { ["seq"] = Seq, ["k"] = K };
        if (Own > 0) d["own"] = Own;
        foreach (var (k, v) in Data) d[k] = v;
        if (SecretKey is not null) d[SecretKey] = seat is { } s && Insiders.Contains(s) ? Secret : null;
        return d;
    }

    public object? this[string key] => Data.FirstOrDefault(d => d.Key == key).Value;
}

/// <summary>Законна оплата шляху (§2.4): <see cref="N"/> карт вантажу <see cref="C"/> і <see cref="W"/> волів; лише воли — C = Wild, N = 0.</summary>
public sealed record SlPay(int C, int N, int W);

/// <summary>Що можна сказати про вільний шлях місцю (§5, <c>me.claims</c>): усі законні оплати або чого бракує й чому.</summary>
public sealed record SlQuote(int Route, bool Ok, List<SlPay> Combos, int Short, string Why);

/// <summary>
/// Законні дії місця зараз (§5): з цього ж будуються <c>me.can</c>, <c>me.why</c>, <c>me.claims</c>, а Глек бере кандидатів із
/// тих самих методів ядра (<see cref="SolyanyiCore.ClaimOk"/>, <see cref="SolyanyiCore.Pays"/>) — вид, бот і <c>Act</c> не розходяться.
/// </summary>
public sealed class SlLegal
{
    public bool Draw, Deck, Tickets, Claim, Pass;
    public int[] Market = [];
    public Dictionary<string, string> Why = [];
    /// <summary>Кожен незайнятий шлях за зростанням id — і поза моїм ходом (для підсвітки).</summary>
    public List<SlQuote> Claims = [];
}

/// <summary>
/// Правила «Соляного шляху» (контракт §2) без каркаса: ні Ctx, ні годинника — випадковість приходить у <see cref="Begin"/>,
/// порядок викликів фіксований (§2.1); далі <see cref="Rng"/> потрібен лише, щоб перетасувати скид. Кожна дія — метод, що
/// повертає помилку людською мовою (тексти §6) або null; при помилці стан не міняється. Рука — лічильники на 9 видів
/// (вісім вантажів + віл), карти без id; колоди — списки, індекс 0 — верх.
/// </summary>
public sealed partial class SolyanyiCore
{
    public bool[] In = new bool[MaxSeats];
    /// <summary>Місця в грі за зростанням.</summary>
    public int[] Order = [];
    public int Seats => Order.Length;
    /// <summary>Опція <c>wagons</c>: скільки возів на старті.</summary>
    public int Wagons0 = 45;
    public Func<int, string> NameOf = s => $"гравець {s + 1}";
    /// <summary>Тасує скид. Після Load — Ctx.Rng нового процесу (§8).</summary>
    public Random Rng = new(0);

    public bool Started;
    public SlPhase Phase = SlPhase.Setup;
    public bool Over => Phase == SlPhase.Over;
    /// <summary>Коло (0 — ще фаза setup), хто починає кожне коло, чий хід, скільки ходів зроблено.</summary>
    public int Round, First, Turn, Turns;
    /// <summary>Почалось останнє коло, скільки ходів до кінця (-1 — ще ні), хто його запустив (-1 — ніхто).</summary>
    public bool Last;
    public int Left = -1, LastBy = -1;
    /// <summary>Пропусків поспіль (§2.2).</summary>
    public int Passes;

    public List<int> Deck = [];
    public List<int> Discard = [];
    /// <summary>Ринок: вид карти на кожному місці, -1 — порожньо.</summary>
    public int[] Market = [-1, -1, -1, -1, -1];
    public List<int> TicketDeck = [];

    public int[][] Hand = [.. Enumerable.Range(0, MaxSeats).Select(_ => new int[9])];
    /// <summary>Замовлення місця в порядку взяття.</summary>
    public List<int>[] Tickets = [.. Enumerable.Range(0, MaxSeats).Select(_ => new List<int>())];
    /// <summary>Виконане за id замовлення (власник один).</summary>
    public bool[] Done = new bool[40];
    /// <summary>Пропозиція: у setup — кожному своя, у keep — тому, чий хід.</summary>
    public List<int>[] Offer = [.. Enumerable.Range(0, MaxSeats).Select(_ => new List<int>())];
    /// <summary>Фаза setup: уже обрав.</summary>
    public bool[] Ready = new bool[MaxSeats];

    /// <summary>Власник шляху за id, -1 — вільний.</summary>
    public int[] Owner = Enumerable.Repeat(-1, Routes.Length).ToArray();
    /// <summary>Зайняті шляхи місця в порядку зайняття.</summary>
    public List<int>[] Taken = [.. Enumerable.Range(0, MaxSeats).Select(_ => new List<int>())];
    public int[] Wagons = new int[MaxSeats];
    /// <summary>Очки за шляхи (відкрито) і найдовша дорога зараз.</summary>
    public int[] RoutePts = new int[MaxSeats], Road = new int[MaxSeats];
    /// <summary>Фаза draw: чим було перше взяття — вид карти з ринку, 9 — наосліп, -1 — ще нічого.</summary>
    public int FirstPick = -1;

    public string Reason = "";
    public int[] Winners = [];
    /// <summary>Наприкінці: місця з Найдовшою валкою, її довжина й дорога кожного в порядку руху.</summary>
    public int[] LongestSeats = [];
    public int LongestLen;
    public Dictionary<int, int[]> LongestRoutes = [];

    public List<SlEvent> Events = [];
    public long Seq;
    /// <summary>Лічильники подій «лише власнику», свій на кожне місце (§7): наскрізь через партії столу, як <see cref="Seq"/>.</summary>
    public long[] OwnSeq = new long[MaxSeats];
    public List<string> Log = [];
    /// <summary>Рядки для журналу кімнати (гра забирає й чистить).</summary>
    public List<string> Journal = [];

    // статистика кінця (§9)
    public List<int[]> Timeline = [];
    public int[] Claims = new int[MaxSeats], Biggest = new int[MaxSeats], Draws = new int[MaxSeats], Drawn = new int[MaxSeats];
    public int[] Facedown = new int[MaxSeats], FaceWild = new int[MaxSeats], WildsPaid = new int[MaxSeats], TicketDraws = new int[MaxSeats];
    public int[] TicketsKept = new int[MaxSeats], Blocks = new int[MaxSeats], Crimea = new int[MaxSeats];
    public int[] FirstClaim = Enumerable.Repeat(-1, MaxSeats).ToArray();
    /// <summary>Блоки Глека: шлях → кому заступив дорогу (лише голос ведучого, усе відкрите).</summary>
    public Dictionary<int, int> Blocked = [];

    public SolyanyiCore(IEnumerable<int> seats)
    {
        foreach (var s in seats) In[s] = true;
        Order = [.. Enumerable.Range(0, MaxSeats).Where(s => In[s])];
    }

    /// <summary>Парні шляхи: на 2–3 гравці другий закрито для всіх, на 4–5 — можна обидва, але не одному (§2.4).</summary>
    public bool OnePairOnly => Seats <= 3;

    /// <summary>Місця за порядком ходів, починаючи від першого гравця.</summary>
    public IEnumerable<int> FromFirst()
    {
        var i = Array.IndexOf(Order, First);
        for (var k = 0; k < Seats; k++) yield return Order[(i + k) % Seats];
    }

    public int Next(int s) => Order[(Array.IndexOf(Order, s) + 1) % Seats];

    // =====================================================================================
    // Початок (§2.1)
    // =====================================================================================

    /// <summary>
    /// Розклад партії. Порядок викликів <paramref name="rng"/> — як у контракті: Фішер–Єйтс колоди, Фішер–Єйтс замовлень,
    /// перший гравець; далі роздача, ринок і замовлення згори.
    /// </summary>
    public void Begin(Random rng)
    {
        Rng = rng;
        var cards = new int[DeckSize];
        for (var i = 0; i < DeckSize; i++) cards[i] = i < DeckEach * 8 ? i / DeckEach : Wild;
        Shuffle(cards, rng);
        Deck = [.. cards];
        var tickets = Enumerable.Range(0, SolyanyiMap.Tickets.Length).ToArray();
        Shuffle(tickets, rng);
        TicketDeck = [.. tickets];
        First = Order[rng.Next(Seats)];
        Turn = First;
        Started = true;
        Phase = SlPhase.Setup;
        foreach (var s in Order) Wagons[s] = Wagons0;
        foreach (var s in FromFirst())
        {
            for (var i = 0; i < HandStart; i++) Hand[s][Pop()]++;
            Emit("deal", ("seat", s), ("cards", HandStart), ("tickets", SolyanyiMap.Offer));
        }
        Refill(0);
        foreach (var s in FromFirst())
        {
            Offer[s] = [.. TicketDeck.Take(SolyanyiMap.Offer)];
            TicketDeck.RemoveRange(0, Offer[s].Count);
        }
    }

    static void Shuffle(int[] a, Random rng)
    {
        for (var i = a.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (a[i], a[j]) = (a[j], a[i]);
        }
    }

    int Pop()
    {
        var c = Deck[0];
        Deck.RemoveAt(0);
        return c;
    }

    // =====================================================================================
    // Ринок (§2.3)
    // =====================================================================================

    /// <summary>Скид стає колодою (подія <c>shuffle</c>).</summary>
    void Reshuffle()
    {
        var a = Discard.ToArray();
        Shuffle(a, Rng);
        Deck = [.. a];
        Discard.Clear();
        Emit("shuffle", ("n", a.Length));
        Say("Скид перетасовано");
    }

    /// <summary>
    /// Поповнення ринку: кожне порожнє місце за зростанням — згори колоди (порожня — спершу скид); ≥ 3 воли, ще є заміни й у
    /// колоді + скиді ≥ 3 не-воли — ринок у скид і наново. <paramref name="vacated"/> — маска щойно спорожнілих місць: якщо
    /// взяти нема чого, клієнт мусить дізнатись про дірку (<c>refill</c> із null); старі дірки вдруге не оголошуємо.
    /// </summary>
    void Refill(int vacated)
    {
        var wipes = 0;
        while (true)
        {
            for (var slot = 0; slot < MarketSize; slot++)
            {
                if (Market[slot] >= 0) continue;
                if (Deck.Count == 0 && Discard.Count > 0) Reshuffle();
                if (Deck.Count > 0)
                {
                    Market[slot] = Pop();
                    Emit("refill", ("slot", slot), ("card", Cards[Market[slot]]));
                }
                else if ((vacated & (1 << slot)) != 0) Emit("refill", ("slot", slot), ("card", null));
            }
            if (Market.Count(c => c == Wild) < WipeAt || wipes >= WipeMax || !EnoughPlain()) return;
            var wiped = Market.Where(c => c >= 0).ToArray();
            Discard.AddRange(wiped);
            vacated = 0;
            for (var slot = 0; slot < MarketSize; slot++)
            {
                if (Market[slot] >= 0) vacated |= 1 << slot;
                Market[slot] = -1;
            }
            wipes++;
            Emit("wipe", ("cards", wiped.Select(c => Cards[c]).ToArray()));
            Say("На ринку три воли — карти змінено");
        }
    }

    bool EnoughPlain()
    {
        var n = 0;
        foreach (var c in Deck) if (c != Wild && ++n >= WipeAt) return true;
        foreach (var c in Discard) if (c != Wild && ++n >= WipeAt) return true;
        return false;
    }

    // =====================================================================================
    // Дії (§6). Перевірки — у порядку контракту.
    // =====================================================================================

    /// <summary>Спільні помилки дій поточного гравця.</summary>
    string? Gate(int s, bool drawOk = false)
    {
        if (Over) return "Партія вже скінчилась";
        if (s < 0 || s >= MaxSeats || !In[s]) return "Ти не граєш за цим столом";
        if (Phase == SlPhase.Setup) return "Спершу всі обирають замовлення";
        if (Turn != s) return $"Зараз їде {NameOf(Turn)}";
        if (Phase == SlPhase.Draw && !drawOk) return "Спершу візьми другу карту";
        if (Phase == SlPhase.Keep) return "Спершу обери замовлення";
        return null;
    }

    /// <summary>
    /// Узяти карту: з місця ринку <paramref name="slot"/> або наосліп (<paramref name="deck"/>). <paramref name="expect"/> — карта, яку
    /// людина бачила на цьому місці: подвійний дотик не забере те, що вже приїхало на заміну (§6). Глеки її не передають.
    /// </summary>
    public string? Draw(int s, int? slot, bool deck, string? expect = null)
    {
        if (Gate(s, drawOk: true) is { } gate) return gate;
        if (slot is null && !deck) return "Не зрозумів, яку карту";
        var second = Phase == SlPhase.Draw;
        var pick = second ? 2 : 1;
        int card;
        if (slot is { } k)
        {
            if (k < 0 || k >= MarketSize) return "Такого місця на ринку нема";
            if (Market[k] < 0) return "На цьому місці порожньо";
            if (expect is not null && Cards[Market[k]] != expect) return "Цю карту вже замінили — глянь на ринок ще раз";
            if (second && Market[k] == Wild) return "Вола з ринку другою картою не беруть";
            card = Market[k];
            Market[k] = -1;
            Hand[s][card]++;
            Emit("draw", ("seat", s), ("from", "market"), ("slot", k), ("pick", pick), ("card", Cards[card]));
            Refill(1 << k);
        }
        else
        {
            if (Deck.Count == 0 && Discard.Count == 0) return "Колода порожня";
            if (Deck.Count == 0) Reshuffle();
            card = Pop();
            Hand[s][card]++;
            Facedown[s]++;
            Emit("draw", [("seat", s), ("from", "deck"), ("slot", null), ("pick", pick)], "card", Cards[card], [s]);
        }
        Drawn[s]++;
        if (!second) Draws[s]++;
        Passes = 0;
        var shown = slot is null ? 9 : card;
        if (!second && shown == Wild)
        {
            FaceWild[s]++;
            Say($"{NameOf(s)} бере вола з ринку — і більше нічого");
            EndTurn(s);
            return null;
        }
        if (second)
        {
            Say(DrawLine(s, FirstPick, shown));
            EndTurn(s);
            return null;
        }
        FirstPick = shown;
        if (SecondPickPossible())
        {
            Phase = SlPhase.Draw;
            return null;
        }
        // Другу карту брати нізвідки — дія кінчається сама.
        Say(DrawLine(s, shown, -1));
        EndTurn(s);
        return null;
    }

    string DrawLine(int s, int a, int b)
    {
        var nick = NameOf(s);
        if (b < 0) return a == 9 ? $"{nick} бере карту наосліп" : $"{nick} бере {CargoAcc[a]}";
        if (a == 9 && b == 9) return $"{nick} бере 2 карти наосліп";
        if (a == 9) return $"{nick} бере {CargoAcc[b]} і карту наосліп";
        if (b == 9) return $"{nick} бере {CargoAcc[a]} і карту наосліп";
        return $"{nick} бере {CargoAcc[a]} і {CargoAcc[b]}";
    }

    bool DeckOpen => Deck.Count > 0 || Discard.Count > 0;
    bool SecondPickPossible() => DeckOpen || Market.Any(c => c >= 0 && c != Wild);

    /// <summary>Чому шлях не можна взяти незалежно від карт (§2.4, пп. 2–3); null — можна.</summary>
    string? ClaimBlock(int s, SlRoute r)
    {
        if (r.Twin >= 0 && Owner[r.Twin] >= 0)
        {
            if (OnePairOnly) return "За столом на 2–3 парний шлях один, і його вже зайнято";
            if (Owner[r.Twin] == s) return "Обидва парні шляхи одному не можна";
        }
        return Wagons[s] < r.Len ? $"Бракує возів: треба {r.Len}, є {Wagons[s]}" : null;
    }

    /// <summary>Зайняти шлях (§2.4): <paramref name="w"/> волів і решту вантажем <paramref name="c"/>; лише воли — c = wild, w = L.</summary>
    public string? Claim(int s, int? route, string? c, int? w)
    {
        if (Gate(s) is { } gate) return gate;
        if (route is not { } id) return "Не зрозумів, який шлях";
        if (id < 0 || id >= Routes.Length) return "Такого шляху на мапі нема";
        if (Owner[id] >= 0) return $"Цей шлях уже зайняв {NameOf(Owner[id])}";
        var r = Routes[id];
        if (ClaimBlock(s, r) is { } block) return block;
        const string confused = "Не зрозумів, чим платиш";
        if (c is null || w is not { } wilds || wilds < 0 || wilds > r.Len) return confused;
        var cargo = CardIndex(c);
        if (cargo < 0) return "Такого вантажу нема";
        if (cargo != Wild && r.Cargo != Grey && r.Cargo != cargo) return $"На цей шлях треба {CargoAcc[r.Cargo]}";
        var n = r.Len - wilds;
        if (cargo == Wild ? wilds != r.Len : n == 0) return confused;
        if (cargo != Wild && Hand[s][cargo] < n) return $"Стільки {CargoGen[cargo]} у тебе нема";
        if (Hand[s][Wild] < wilds) return "Стільки волів у тебе нема";

        if (cargo != Wild)
        {
            Hand[s][cargo] -= n;
            for (var i = 0; i < n; i++) Discard.Add(cargo);
        }
        Hand[s][Wild] -= wilds;
        for (var i = 0; i < wilds; i++) Discard.Add(Wild);
        Owner[id] = s;
        Taken[s].Add(id);
        Wagons[s] -= r.Len;
        RoutePts[s] += r.Pts;
        Road[s] = LongestRoad(s);
        Claims[s]++;
        Biggest[s] = Math.Max(Biggest[s], r.Len);
        WildsPaid[s] += wilds;
        if (FirstClaim[s] < 0) FirstClaim[s] = Round;
        Passes = 0;
        Emit("claim", ("seat", s), ("route", id), ("c", Cards[cargo]), ("n", cargo == Wild ? 0 : n), ("w", wilds), ("pts", r.Pts),
            ("wagons", Wagons[s]), ("road", Road[s]));
        Say($"{NameOf(s)} займає {RouteName(id)} ({r.Len} {WagonWord(r.Len)}, +{r.Pts} ⭐)");
        CheckDone(s);
        Refill(0);
        EndTurn(s);
        return null;
    }

    /// <summary>Узяти замовлення (§2.5): до 3 згори — фаза keep.</summary>
    public string? TakeTickets(int s)
    {
        if (Gate(s) is { } gate) return gate;
        if (TicketDeck.Count == 0) return "Замовлень більше нема";
        var got = TicketDeck.Take(SolyanyiMap.Offer).ToArray();
        TicketDeck.RemoveRange(0, got.Length);
        Offer[s] = [.. got];
        Phase = SlPhase.Keep;
        TicketDraws[s]++;
        Passes = 0;
        Emit("tickets", [("seat", s), ("n", got.Length)], "ids", got, [s]);
        return null;
    }

    /// <summary>Скільки щонайменше лишити зараз.</summary>
    public int KeepMin => Phase == SlPhase.Setup ? KeepStart : KeepLater;

    /// <summary>Вибір замовлень: у setup — кожен ще не обраний, у keep — поточний.</summary>
    public string? Keep(int s, int[]? ids)
    {
        if (Over) return "Партія вже скінчилась";
        if (s < 0 || s >= MaxSeats || !In[s]) return "Ти не граєш за цим столом";
        if (Phase is not (SlPhase.Setup or SlPhase.Keep)) return "Зараз нема чого обирати";
        var setup = Phase == SlPhase.Setup;
        if (setup && Ready[s]) return "Ти вже обрав";
        if (!setup && Turn != s) return $"Зараз їде {NameOf(Turn)}";
        if (ids is null) return "Не зрозумів, які замовлення";
        var offer = Offer[s];
        if (ids.Any(i => !offer.Contains(i)) || ids.Distinct().Count() != ids.Length) return "Цього замовлення тобі не пропонували";
        if (ids.Length < KeepMin) return $"Лиши щонайменше {KeepMin}";

        var kept = offer.Where(ids.Contains).ToList();
        var back = offer.Where(i => !ids.Contains(i)).Order().ToList();
        var from = offer.Count;
        Tickets[s].AddRange(kept);
        TicketDeck.AddRange(back);
        Offer[s] = [];
        TicketsKept[s] += kept.Count;
        if (setup)
        {
            Ready[s] = true;
            Emit("setup", ("seat", s), ("kept", kept.Count));
            Say($"{NameOf(s)} обрав замовлення: лишає {kept.Count}");
            CheckDone(s);
            if (Order.All(o => Ready[o])) BeginPlay();
            return null;
        }
        Emit("keep", ("seat", s), ("kept", kept.Count), ("back", back.Count));
        Say($"{NameOf(s)} бере замовлення: лишає {kept.Count} з {from}");
        CheckDone(s);
        EndTurn(s);
        return null;
    }

    /// <summary>Пропустити — лише коли жодна інша дія неможлива.</summary>
    public string? Pass(int s)
    {
        if (Gate(s) is { } gate) return gate;
        if (!PassLegal(s)) return "Пропускати не можна — є що робити";
        Passes++;
        Emit("pass", ("seat", s));
        Say($"{NameOf(s)} пропускає хід — нема що робити");
        EndTurn(s);
        return null;
    }

    public bool PassLegal(int s) => !DrawLegal && TicketDeck.Count == 0 && !AnyClaim(s);
    bool DrawLegal => DeckOpen || Market.Any(c => c >= 0);

    /// <summary>Глек поставив блок: лічильник на партію й запис для голосу.</summary>
    public void NoteBlock(int s, int route, int victim)
    {
        Blocks[s]++;
        Blocked[route] = victim;
    }

    /// <summary>Записати рядок у журнал столу (гра — про вихід, Глека тощо).</summary>
    public void Note(string line) => Say(line);

    // =====================================================================================
    // Кінець ходу й партії (§2.6, §2.7)
    // =====================================================================================

    void BeginPlay()
    {
        Phase = SlPhase.Play;
        Round = 1;
        Turn = First;
        Timeline.Add(new int[MaxSeats]);
        Emit("round", ("n", Round), ("first", First));
        Emit("turn", ("seat", Turn));
    }

    void EndTurn(int s)
    {
        Turns++;
        Phase = SlPhase.Play;
        FirstPick = -1;
        if (Last) Left--;
        else if (Wagons[s] <= LastAt)
        {
            Last = true;
            Left = Seats;
            LastBy = s;
            Emit("last", ("seat", s), ("wagons", Wagons[s]));
            Say($"У {NameOf(s)} лишилось {Wagons[s]} {WagonWord(Wagons[s])} — кожен має ще один хід", journal: true);
        }
        var next = Next(s);
        var wrapped = next == First;
        if (wrapped) Timeline.Add([.. RoutePts]);
        if (Passes >= Seats || (Last && Left <= 0))
        {
            if (!wrapped) Timeline.Add([.. RoutePts]);
            End(Passes >= Seats ? "stall" : "wagons");
            return;
        }
        if (wrapped)
        {
            Round++;
            Emit("round", ("n", Round), ("first", First));
        }
        Turn = next;
        Emit("turn", ("seat", Turn));
    }

    /// <summary>Сума виконаних замовлень місця.</summary>
    public int DonePts(int s) => Tickets[s].Where(t => Done[t]).Sum(t => SolyanyiMap.Tickets[t].Pts);
    /// <summary>Сума невиконаних замовлень місця (штраф).</summary>
    public int OpenPts(int s) => Tickets[s].Where(t => !Done[t]).Sum(t => SolyanyiMap.Tickets[t].Pts);
    public int DoneCount(int s) => Tickets[s].Count(t => Done[t]);
    /// <summary>Бонус Найдовшої валки (рахується наприкінці).</summary>
    public int Bonus(int s) => LongestSeats.Contains(s) ? LongestBonus : 0;
    /// <summary><c>total</c> (§2.7). До кінця партії — без бонусу (<c>me.score</c>).</summary>
    public int Total(int s) => RoutePts[s] + DonePts(s) - OpenPts(s) + Bonus(s);

    void End(string reason)
    {
        Phase = SlPhase.Over;
        Reason = reason;
        if (reason == "stall") LastBy = -1;
        Left = Last ? Math.Max(0, Left) : -1;
        LongestLen = Order.Max(s => Road[s]);
        LongestSeats = LongestLen > 0 ? [.. Order.Where(s => Road[s] == LongestLen)] : [];
        LongestRoutes = LongestSeats.ToDictionary(s => s, LongestPath);
        var top = Order.Max(Total);
        var tied = Order.Where(o => Total(o) == top).ToArray();
        var most = tied.Max(DoneCount);
        tied = [.. tied.Where(o => DoneCount(o) == most)];
        if (tied.Any(o => LongestSeats.Contains(o))) tied = [.. tied.Where(o => LongestSeats.Contains(o))];
        Winners = tied;
        Emit("longest", ("seats", (int[])LongestSeats.Clone()), ("len", LongestLen),
            ("routes", LongestRoutes.ToDictionary(p => p.Key.ToString(), p => p.Value)));
        foreach (var s in LongestSeats)
            Say($"Найдовша валка — {NameOf(s)}: {LongestLen} {WagonWord(LongestLen)} (+{LongestBonus} ⭐)", journal: true);
        Emit("over", ("winners", (int[])Winners.Clone()), ("reason", reason), ("points", Order.Select(Total).ToArray()));
    }

    // =====================================================================================
    // Мережа: зв'язність, виконані замовлення, Найдовша валка
    // =====================================================================================

    /// <summary>Мережа місця: представник кожного міста (свої шляхи — зв'язки).</summary>
    public int[] Net(int s)
    {
        var p = new int[Cities.Length];
        for (var i = 0; i < p.Length; i++) p[i] = i;
        foreach (var id in Taken[s])
        {
            var (a, b) = (Find(p, Routes[id].A), Find(p, Routes[id].B));
            if (a != b) p[Math.Max(a, b)] = Math.Min(a, b);
        }
        for (var i = 0; i < p.Length; i++) p[i] = Find(p, i);
        return p;
    }

    static int Find(int[] p, int x)
    {
        while (p[x] != x) x = p[x] = p[p[x]];
        return x;
    }

    /// <summary>Замовлення місця, що вже з'єднані (кружними шляхами й через чужі міста теж), — виконані; подія лише власнику.</summary>
    void CheckDone(int s)
    {
        var open = false;
        foreach (var t in Tickets[s]) open |= !Done[t];
        if (!open) return;
        var net = Net(s);
        foreach (var t in Tickets[s])
        {
            var tk = SolyanyiMap.Tickets[t];
            if (Done[t] || net[tk.A] != net[tk.B]) continue;
            Done[t] = true;
            if (IsCrimea(tk)) Crimea[s]++;
            EmitDone(s, t);
        }
    }

    /// <summary>Найдовша дорога місця (§2.7): кожен шлях щонайбільше раз, місто — скільки завгодно разів; сума <c>len</c>.</summary>
    public int LongestRoad(int s) => SlTrail.Longest(Taken[s], null);

    /// <summary>Сама дорога для підсвітки — id шляхів у порядку руху.</summary>
    public int[] LongestPath(int s)
    {
        var path = new List<int>();
        SlTrail.Longest(Taken[s], path);
        return [.. path];
    }

    /// <summary>Хто зараз веде за найдовшою дорогою (відкрито, §5).</summary>
    public int[] RoadLeaders()
    {
        var best = Order.Length > 0 ? Order.Max(s => Road[s]) : 0;
        return best > 0 ? [.. Order.Where(s => Road[s] == best)] : [];
    }

    // =====================================================================================
    // Законні дії й ціни (§5)
    // =====================================================================================

    /// <summary>Вартість шляху для місця (§8.1): 0 — свій, -1 — нема (чужий чи закритий для мене), інакше <c>len</c>.</summary>
    public int Cost(int s, int id)
    {
        var o = Owner[id];
        if (o == s) return 0;
        if (o >= 0) return -1;
        var r = Routes[id];
        if (r.Twin >= 0 && Owner[r.Twin] >= 0 && (OnePairOnly || Owner[r.Twin] == s)) return -1;
        return r.Len;
    }

    public int[] Costs(int s)
    {
        var c = new int[Routes.Length];
        for (var i = 0; i < c.Length; i++) c[i] = Cost(s, i);
        return c;
    }

    /// <summary>Закриті правилом 2–3 шляхи за зростанням id; на 4–5 — завжди порожньо.</summary>
    public int[] Closed() => OnePairOnly
        ? [.. Routes.Where(r => Owner[r.Id] < 0 && r.Twin >= 0 && Owner[r.Twin] >= 0).Select(r => r.Id)]
        : [];

    /// <summary>Швидка відповідь «чи можна зараз оплатити й зайняти» (те саме, що <see cref="Quote"/>.Ok, без переліку оплат).</summary>
    public bool ClaimOk(int s, int id)
    {
        if (Owner[id] >= 0) return false;
        var r = Routes[id];
        if (r.Twin >= 0 && Owner[r.Twin] >= 0 && (OnePairOnly || Owner[r.Twin] == s)) return false;
        if (Wagons[s] < r.Len) return false;
        var h = Hand[s];
        return Stack(h, r) + h[Wild] >= r.Len;
    }

    /// <summary>Скільки карт одного вантажу піде на цей шлях: свого — кольоровий, найбільший стос — сірий.</summary>
    static int Stack(int[] h, SlRoute r)
    {
        if (r.Cargo != Grey) return h[r.Cargo];
        var best = 0;
        for (var i = 0; i < 8; i++) if (h[i] > best) best = h[i];
        return best;
    }

    public bool AnyClaim(int s)
    {
        for (var i = 0; i < Routes.Length; i++) if (ClaimOk(s, i)) return true;
        return false;
    }

    /// <summary>Усі законні оплати шляху (§5): <c>w</c> за зростанням, далі за <c>CARGO</c>, віл останній.</summary>
    public List<SlPay> Pays(int s, int id)
    {
        var r = Routes[id];
        var h = Hand[s];
        var list = new List<SlPay>();
        for (var c = 0; c < 8; c++)
        {
            if (r.Cargo != Grey && r.Cargo != c) continue;
            for (var n = Math.Min(h[c], r.Len); n >= 1; n--)
                if (r.Len - n <= h[Wild]) list.Add(new SlPay(c, n, r.Len - n));
        }
        if (h[Wild] >= r.Len) list.Add(new SlPay(Wild, 0, r.Len));
        list.Sort((a, b) => a.W != b.W ? a.W.CompareTo(b.W) : a.C.CompareTo(b.C));
        return list;
    }

    /// <summary>Ціна вільного шляху місцю: оплати або перша застосовна причина відмови (тексти §5).</summary>
    public SlQuote Quote(int s, int id)
    {
        var r = Routes[id];
        if (ClaimBlock(s, r) is { } block) return new SlQuote(id, false, [], 0, block);
        var h = Hand[s];
        var shortBy = r.Len - Stack(h, r) - h[Wild];
        if (shortBy <= 0) return new SlQuote(id, true, Pays(s, id), 0, "");
        return new SlQuote(id, false, [], shortBy, r.Cargo != Grey
            ? $"бракує {shortBy} {CargoGen[r.Cargo]}"
            : $"бракує {shortBy} {(shortBy == 1 ? "карти" : "карт")} одного вантажу");
    }

    /// <summary>Що законно місцю зараз. Поза його ходом усе «ні», але ціни шляхів є завжди (для підсвітки).</summary>
    public SlLegal Legal(int s)
    {
        var l = new SlLegal();
        for (var i = 0; i < Routes.Length; i++)
            if (Owner[i] < 0) l.Claims.Add(Quote(s, i));
        if (Over || Phase == SlPhase.Setup || Turn != s) return l;
        if (Phase == SlPhase.Draw)
        {
            l.Draw = true;
            l.Deck = DeckOpen;
            l.Market = [.. Enumerable.Range(0, MarketSize).Where(k => Market[k] >= 0 && Market[k] != Wild)];
            return l;
        }
        if (Phase != SlPhase.Play) return l;
        l.Deck = DeckOpen;
        l.Market = [.. Enumerable.Range(0, MarketSize).Where(k => Market[k] >= 0)];
        l.Draw = l.Deck || l.Market.Length > 0;
        l.Tickets = TicketDeck.Count > 0;
        l.Claim = l.Claims.Any(q => q.Ok);
        l.Pass = !l.Draw && !l.Tickets && !l.Claim;
        if (!l.Draw) l.Why["draw"] = "Карт ніде нема";
        if (!l.Tickets) l.Why["tickets"] = "Замовлень більше нема";
        if (!l.Claim) l.Why["claim"] = "Жодного шляху зараз не потягнеш";
        if (!l.Pass) l.Why["pass"] = "Пропускати не можна — є що робити";
        return l;
    }

    /// <summary>Скільки ще возів треба на замовлення за <see cref="Cost"/> і які вільні шляхи (за зростанням id); -1 — шляху вже нема.</summary>
    public int Need(int s, int ticket, List<int>? path = null)
    {
        var t = SolyanyiMap.Tickets[ticket];
        var d = Dijkstra(t.A, t.B, Costs(s), path);
        if (path is not null)
        {
            path.RemoveAll(r => Owner[r] >= 0);
            path.Sort();
        }
        return d;
    }

    // =====================================================================================
    // Події й журнал
    // =====================================================================================

    /// <summary>1 віз, 2 вози, 5 возів.</summary>
    public static string WagonWord(int n)
    {
        int a = Math.Abs(n) % 100, b = a % 10;
        if (a is > 10 and < 20) return "возів";
        return b == 1 ? "віз" : b is >= 2 and <= 4 ? "вози" : "возів";
    }

    SlEvent Emit(string k, params (string key, object? val)[] data) => Emit(k, data, null, null, []);

    SlEvent Emit(string k, (string key, object? val)[] data, string? secretKey, object? secret, int[] insiders)
    {
        var e = new SlEvent
        {
            Seq = ++Seq,
            K = k,
            Data = [.. data.Select(d => new KeyValuePair<string, object?>(d.key, d.val))],
            SecretKey = secretKey,
            Secret = secret,
            Insiders = insiders,
        };
        Keep(e);
        return e;
    }

    void Keep(SlEvent e)
    {
        Events.Add(e);
        if (Events.Count > 120) Events.RemoveRange(0, Events.Count - 60);
    }

    /// <summary>Подія <c>done</c>: бачить лише власник (§7), номер — із його лічильника, спільний <see cref="Seq"/> не рухається.</summary>
    void EmitDone(int s, int ticket) => Keep(new SlEvent
    {
        Seq = Seq,
        K = "done",
        Data = [new("seat", s), new("ticket", ticket)],
        Only = s,
        Own = ++OwnSeq[s],
    });

    void Say(string line, bool journal = false)
    {
        Log.Add(line);
        if (Log.Count > 40) Log.RemoveRange(0, Log.Count - 12);
        if (journal) Journal.Add(line);
    }
}

/// <summary>
/// Найдовша безперервна дорога (§2.7): перебір у глибину з використаними шляхами в бітовій масці (возів ≤ 45, тож шляхів у
/// гравця щонайбільше 28). Те саме, що DFS із кожного міста, лише без зайвого, бо Глек рахує це після кожного свого шляху:
/// починати є сенс тільки з міст непарного степеня (дорогу, що обривається в місті парного степеня, завжди можна продовжити,
/// довжини ж додатні), мережа без таких міст обходиться вся; гілку, якій уже не вистачить шляхів побити найкраще, не
/// дивимось; те саме місто з тими самими використаними шляхами (петлю проїхали навпаки) вдруге не обходимо; а щоб лишити лише
/// 2 непарні міста, решті непарних треба щось пропустити — коли дорога сягнула цієї стелі, перебір закінчено.
/// </summary>
internal sealed class SlTrail
{
    readonly record struct Edge(int To, int Bit, int Len, int Id);

    readonly Edge[]?[] _adj;
    readonly List<int>? _cur;
    readonly HashSet<ulong> _seen = [];
    List<int>? _bestPath;
    int _best, _cap;

    SlTrail(Edge[]?[] adj, bool withPath)
    {
        _adj = adj;
        _cur = withPath ? [] : null;
    }

    public static int Longest(IReadOnlyList<int> taken, List<int>? path)
    {
        path?.Clear();
        if (taken.Count == 0) return 0;
        var ids = taken.Order().ToArray();
        var lists = new List<Edge>?[SolyanyiMap.Cities.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            var r = SolyanyiMap.Routes[ids[i]];
            (lists[r.A] ??= []).Add(new Edge(r.B, i, r.Len, r.Id));
            (lists[r.B] ??= []).Add(new Edge(r.A, i, r.Len, r.Id));
        }
        // Спершу довші шляхи: добра дорога знаходиться рано, і решту гілок відсікає стеля.
        var adj = lists.Select(l => l?.OrderByDescending(e => e.Len).ThenBy(e => e.Id).ToArray()).ToArray();
        var t = new SlTrail(adj, path is not null);
        var seen = new bool[adj.Length];
        var comp = new List<int>();
        var stack = new Stack<int>();
        for (var c = 0; c < adj.Length; c++)
        {
            if (adj[c] is null || seen[c]) continue;
            // Компонента: її міста й сума шляхів — стеля для відсікання.
            comp.Clear();
            stack.Push(c);
            seen[c] = true;
            var twice = 0;
            while (stack.TryPop(out var u))
            {
                comp.Add(u);
                foreach (var e in adj[u]!)
                {
                    twice += e.Len;
                    if (seen[e.To]) continue;
                    seen[e.To] = true;
                    stack.Push(e.To);
                }
            }
            comp.Sort();
            var total = twice / 2;
            if (total <= t._best) continue;
            var starts = comp.Where(u => adj[u]!.Length % 2 == 1).ToList();
            // Кожне непарне місто, крім двох кінців дороги, мусить втратити шлях, а шлях лежить у двох містах.
            var lost = starts.Select(u => adj[u]!.Min(e => e.Len)).OrderDescending().Skip(2).Sum();
            t._cap = total - (lost + 1) / 2;
            if (starts.Count == 0) starts.Add(comp[0]);
            foreach (var start in starts) t.Walk(start, 0, 0, total);
        }
        if (path is not null && t._bestPath is not null) path.AddRange(t._bestPath);
        return t._best;
    }

    /// <summary><paramref name="left"/> — скільки возів компоненти ще не їхали: більше, ніж стільки, ця гілка не додасть.</summary>
    void Walk(int at, ulong used, int len, int left)
    {
        if (len > _best)
        {
            _best = len;
            if (_cur is not null) _bestPath = [.. _cur];
        }
        var edges = _adj[at]!;
        // Місто в шість старших бітів: шляхів менше за 58. Розвилки достатньо: повторні обходи сходяться саме в них.
        if (edges.Length > 2 && !_seen.Add(used | ((ulong)at << 58))) return;
        foreach (var e in edges)
        {
            var bit = 1UL << e.Bit;
            if ((used & bit) != 0) continue;
            if (len + left <= _best || _best >= _cap) return;
            _cur?.Add(e.Id);
            Walk(e.To, used | bit, len + e.Len, left - e.Len);
            _cur?.RemoveAt(_cur.Count - 1);
        }
    }
}
