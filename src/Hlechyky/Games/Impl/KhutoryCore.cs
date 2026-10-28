namespace Hlechyky.Games.Impl;

public enum KhPhase { Setup, Roll, Discard, Robber, Steal, Main, Road2, Over }

/// <summary>Грамота в руці: вид (0 козак, 1 скарб, 2 толока, 3 врожайний рік, 4 ярмарок) і чи куплена цього ходу.</summary>
public sealed class KhDev
{
    public int K;
    public bool Fresh;
}

/// <summary>Пропозиція обміну між гравцями (контракт §6). Give — що дає автор.</summary>
public sealed class KhOffer
{
    public int Id, From;
    public int? To;
    public int[] Give = new int[5], Get = new int[5];
    public List<int> Acc = [], Dec = [];
}

/// <summary>
/// Подія для анімацій і журналу (§7). Одне поле може бути приховане (<see cref="SecretKey"/>): його значення бачать
/// лише <see cref="Insiders"/>, решта — null.
/// </summary>
public sealed class KhEvent
{
    public long Seq;
    public string K = "";
    public List<KeyValuePair<string, object?>> Data = [];
    public string? SecretKey;
    public object? Secret;
    public int[] Insiders = [];

    public Dictionary<string, object?> For(int? seat)
    {
        var d = new Dictionary<string, object?> { ["seq"] = Seq, ["k"] = K };
        foreach (var (k, v) in Data) d[k] = v;
        if (SecretKey is not null) d[SecretKey] = seat is { } s && Insiders.Contains(s) ? Secret : null;
        return d;
    }
}

/// <summary>
/// Правила «Хуторів» (база Катану, контракт §2) без каркаса: ні Ctx, ні годинника — випадковість приходить ззовні.
/// Кожен хід — метод, що повертає помилку людською мовою або null; при помилці стан не міняється (як <c>DurakCore</c>).
/// Ресурси — числа 0..4 (дерево, глина, вовна, зерно, залізо), місця — 0..3.
/// </summary>
public sealed class KhutoryCore
{
    public const int MaxSeats = 4, RoadPieces = 15, TownPieces = 5, CityPieces = 4, BankEach = 19;
    public const int Knight = 0, Vp = 1, Toloka = 2, Plenty = 3, Mono = 4;
    public static readonly string[] DevKinds = ["knight", "vp", "road", "plenty", "mono"];
    public static readonly string[] ResGen = ["дерева", "глини", "вовни", "зерна", "заліза"];
    public static readonly string[] ResAcc = ["дерево", "глину", "вовну", "зерно", "залізо"];
    public static readonly int[] RoadCost = [1, 1, 0, 0, 0], TownCost = [1, 1, 1, 1, 0], CityCost = [0, 0, 0, 2, 3], DevCost = [0, 0, 1, 1, 1];

    static readonly KhutoryGeo G = KhutoryGeo.I;

    public readonly KhutoryBoard B;
    readonly Random _rng;
    public bool[] In = new bool[MaxSeats];
    public int[] Order = [];
    public int Target = 8;
    public bool Friendly = true;
    /// <summary>Як назвати місце в журналі (нік; гра підставляє свої).</summary>
    public Func<int, string> NameOf = s => $"гравець {s + 1}";

    public KhPhase Phase = KhPhase.Setup;
    public int Turn;
    public int TurnIdx;
    public int SetupIdx;
    public bool SetupRoad;
    public int SetupTown = -1;

    public int[] VOwner = Enumerable.Repeat(-1, KhutoryGeo.Verts).ToArray();
    public bool[] VCity = new bool[KhutoryGeo.Verts];
    public int[] EOwner = Enumerable.Repeat(-1, KhutoryGeo.EdgeCount).ToArray();
    public int[][] Hand = [.. Enumerable.Range(0, MaxSeats).Select(_ => new int[5])];
    public int[] Bank = [BankEach, BankEach, BankEach, BankEach, BankEach];
    public List<int> Deck = [];
    public List<KhDev>[] Devs = [.. Enumerable.Range(0, MaxSeats).Select(_ => new List<KhDev>())];
    public int[] Knights = new int[MaxSeats];
    public bool PlayedDev;
    /// <summary>Скільки зіграно грамот, крім козаків (козаки — у <see cref="Knights"/>).</summary>
    public int PlayedOther;
    public int? Longest, Army;
    public int[] RoadLen = new int[MaxSeats];
    public int Robber;
    public int[]? Dice;
    public bool Rolled;
    public int[] DiscardNeed = new int[MaxSeats];
    /// <summary>Куди повертатись після Кармелюка: Main (сімка, козак після кидка) або Roll (козак до кидка).</summary>
    public KhPhase Back = KhPhase.Main;
    public List<int> StealFrom = [];
    public int Road2Left;
    public List<KhOffer> Offers = [];
    int _offerId;
    public List<KhEvent> Events = [];
    public long Seq;
    public List<string> Log = [];
    /// <summary>Ключові рядки для журналу кімнати (кидок, будівля, нагорода, перемога) — гра забирає їх у Ctx.Log.</summary>
    public List<string> Journal = [];
    public int? Winner;
    /// <summary>Партію перервано (усі люди встали).</summary>
    public bool Abandoned;

    // Статистика кінця (§9).
    public int[] DiceCount = new int[11];
    public int Sevens, Rounds;
    public List<int[]> Timeline = [];
    public int[] Trades = new int[MaxSeats], Got = new int[MaxSeats], Stolen = new int[MaxSeats];
    public int[][] Built = [.. Enumerable.Range(0, MaxSeats).Select(_ => new int[3])];

    public KhutoryCore(KhutoryBoard board, IEnumerable<int> seats, Random rng)
    {
        B = board;
        _rng = rng;
        foreach (var s in seats) In[s] = true;
        Robber = board.SteppeHex;
        for (var i = 0; i < 14; i++) Deck.Add(Knight);
        for (var i = 0; i < 5; i++) Deck.Add(Vp);
        for (var i = 0; i < 2; i++) { Deck.Add(Toloka); Deck.Add(Plenty); Deck.Add(Mono); }
        for (var i = Deck.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (Deck[i], Deck[j]) = (Deck[j], Deck[i]);
        }
    }

    /// <summary>Почати розстановку: порядок — за місцями від першого.</summary>
    public void Begin(int first)
    {
        var seats = Enumerable.Range(0, MaxSeats).Where(s => In[s]).ToList();
        var at = Math.Max(0, seats.IndexOf(first));
        Order = [.. seats.Skip(at).Concat(seats.Take(at))];
        Phase = KhPhase.Setup;
        SetupIdx = 0;
        SetupRoad = false;
        Turn = Order[0];
        Timeline.Add(PointsRow());
        Emit("turn", ("seat", Turn));
        Say($"Розстановка: першим ставить {NameOf(Turn)}");
    }

    // =====================================================================================
    // Запити
    // =====================================================================================

    public int Seats => Order.Length;
    public bool Over => Phase == KhPhase.Over;
    public int Total(int s) => Hand[s].Sum();
    // Лічильники фігур — перераховує RecountRoads після кожної зміни на полі (вона ж і для тестів, що ставлять фігури руками).
    readonly int[] _towns = new int[MaxSeats], _cities = new int[MaxSeats], _roads = new int[MaxSeats];
    public int TownsOf(int s) => _towns[s];
    public int CitiesOf(int s) => _cities[s];
    public int RoadsOf(int s) => _roads[s];
    public int RoadsLeft(int s) => RoadPieces - RoadsOf(s);
    public int TownsLeft(int s) => TownPieces - TownsOf(s);
    public int CitiesLeft(int s) => CityPieces - CitiesOf(s);

    /// <summary>Відкриті очки: хати, містечка, нагороди (без скарбів у руці).</summary>
    public int PublicPoints(int s) => TownsOf(s) + 2 * CitiesOf(s) + (Longest == s ? 2 : 0) + (Army == s ? 2 : 0);
    /// <summary>Повні очки (зі скарбами).</summary>
    public int Points(int s) => PublicPoints(s) + Devs[s].Count(d => d.K == Vp);

    int[] PointsRow() => [.. Enumerable.Range(0, Math.Max(1, MaxIn + 1)).Select(s => In[s] ? Points(s) : 0)];
    int MaxIn => Enumerable.Range(0, MaxSeats).Where(s => In[s]).DefaultIfEmpty(0).Max();

    /// <summary>Вершина вільна й жодна сусідня не зайнята (правило відстані).</summary>
    public bool TownFree(int v)
    {
        if (VOwner[v] >= 0) return false;
        foreach (var u in G.VNb[v]) if (VOwner[u] >= 0) return false;
        return true;
    }

    bool TouchesOwnRoad(int s, int v)
    {
        foreach (var e in G.VE[v]) if (EOwner[e] == s) return true;
        return false;
    }

    /// <summary>Шлях можна покласти: ребро вільне й продовжує свою хату/шлях не крізь чужу хату.</summary>
    public bool RoadOk(int s, int e)
    {
        if (EOwner[e] >= 0) return false;
        foreach (var v in G.Edges[e])
        {
            if (VOwner[v] == s) return true;
            if (VOwner[v] >= 0) continue;
            foreach (var x in G.VE[v]) if (x != e && EOwner[x] == s) return true;
        }
        return false;
    }

    public List<int> TownSpots(int s)
    {
        var list = new List<int>();
        for (var v = 0; v < KhutoryGeo.Verts; v++)
            if (TownFree(v) && (Phase == KhPhase.Setup || TouchesOwnRoad(s, v))) list.Add(v);
        return list;
    }

    public List<int> RoadSpots(int s)
    {
        var list = new List<int>();
        if (Phase == KhPhase.Setup)
        {
            if (SetupTown >= 0) foreach (var e in G.VE[SetupTown]) if (EOwner[e] < 0) list.Add(e);
            return list;
        }
        for (var e = 0; e < KhutoryGeo.EdgeCount; e++) if (RoadOk(s, e)) list.Add(e);
        return list;
    }

    public List<int> CitySpots(int s)
    {
        var list = new List<int>();
        for (var v = 0; v < KhutoryGeo.Verts; v++) if (VOwner[v] == s && !VCity[v]) list.Add(v);
        return list;
    }

    /// <summary>Хто має хату/містечко на гексі.</summary>
    public IEnumerable<int> OwnersAt(int h) => G.HexV[h].Select(v => VOwner[v]).Where(o => o >= 0).Distinct();

    /// <summary>Куди можна поставити Кармелюка (з правилом дружнього Кармелюка).</summary>
    public List<int> RobberSpots(int s)
    {
        var all = Enumerable.Range(0, KhutoryGeo.Hexes).Where(h => h != Robber).ToList();
        if (!Friendly) return all;
        var kind = all.Where(h => !OwnersAt(h).Any(o => o != s && PublicPoints(o) <= 2)).ToList();
        return kind.Count > 0 ? kind : all;
    }

    /// <summary>Курс обміну з банком для кожного ресурсу (4, 3 з пристанню «3:1», 2 з пристанню ресурсу).</summary>
    public int[] Rates(int s)
    {
        int[] r = [4, 4, 4, 4, 4];
        for (var i = 0; i < B.PortEdge.Length; i++)
        {
            var e = G.Edges[B.PortEdge[i]];
            if (VOwner[e[0]] != s && VOwner[e[1]] != s) continue;
            if (B.PortKind[i] == KhutoryBoard.AnyPort) { for (var x = 0; x < 5; x++) r[x] = Math.Min(r[x], 3); }
            else r[B.PortKind[i]] = 2;
        }
        return r;
    }

    /// <summary>Чого сервер чекає від гравця зараз: town | road | roll | discard | robber | steal | null.</summary>
    public string? Must(int s) => Phase switch
    {
        KhPhase.Setup when s == Turn => SetupRoad ? "road" : "town",
        KhPhase.Roll when s == Turn => "roll",
        KhPhase.Discard when DiscardNeed[s] > 0 => "discard",
        KhPhase.Robber when s == Turn => "robber",
        KhPhase.Steal when s == Turn => "steal",
        KhPhase.Road2 when s == Turn => "road",
        _ => null,
    };

    /// <summary>Чи чекає стіл чогось від цього гравця (для ботів): обов'язкове, свій хід або відповідь на пропозицію.</summary>
    public bool MayAct(int s)
    {
        if (Over || !In[s]) return false;
        if (Must(s) is not null) return true;
        if (Phase != KhPhase.Main) return false;
        return s == Turn || Offers.Any(o => Addressed(o, s) && !o.Acc.Contains(s) && !o.Dec.Contains(s));
    }

    /// <summary>
    /// Чи може гравець відповісти на пропозицію: на пропозицію поточного — адресати (усі чи один), на зустрічну —
    /// сам поточний.
    /// </summary>
    public bool Addressed(KhOffer o, int s) => o.From == Turn
        ? s != Turn && s >= 0 && s < MaxSeats && In[s] && (o.To is null || o.To == s)
        : s == Turn;

    public string PhaseName => Phase.ToString().ToLowerInvariant();

    public static bool Has(int[] hand, int[] cost)
    {
        for (var r = 0; r < 5; r++) if (hand[r] < cost[r]) return false;
        return true;
    }

    /// <summary>«бракує 2 зерна й 3 заліза».</summary>
    public static string Lack(int[] hand, int[] cost)
    {
        var parts = new List<string>();
        for (var r = 0; r < 5; r++) if (hand[r] < cost[r]) parts.Add($"{cost[r] - hand[r]} {ResGen[r]}");
        return "бракує " + Join(parts);
    }

    public static string Join(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => "",
        1 => parts[0],
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " й " + parts[^1],
    };

    /// <summary>Що з будівель доступне гравцеві зараз (карти, фігури, фаза, місце) і чому ні — людською мовою.</summary>
    public (Dictionary<string, bool> can, Dictionary<string, string> why) CanWhy(int s)
    {
        var can = new Dictionary<string, bool>();
        var why = new Dictionary<string, string>();
        foreach (var kind in new[] { "road", "town", "city", "dev" })
        {
            var reason = WhyNot(s, kind);
            can[kind] = reason is null;
            if (reason is not null) why[kind] = reason;
        }
        return (can, why);
    }

    public string? WhyNot(int s, string kind)
    {
        if (Over) return "партія скінчилась";
        if (s != Turn) return "не твій хід";
        if (Phase == KhPhase.Roll) return "спершу кинь кубики";
        if (Phase != KhPhase.Main) return "зараз не час";
        switch (kind)
        {
            case "road":
                if (RoadsLeft(s) <= 0) return "шляхи скінчились";
                if (RoadSpots(s).Count == 0) return "нема куди класти шлях";
                return Has(Hand[s], RoadCost) ? null : Lack(Hand[s], RoadCost);
            case "town":
                if (TownsLeft(s) <= 0) return "хати скінчились — перебудуй якусь на містечко";
                if (TownSpots(s).Count == 0) return "нема вільного місця біля твого шляху";
                return Has(Hand[s], TownCost) ? null : Lack(Hand[s], TownCost);
            case "city":
                if (CitiesLeft(s) <= 0) return "містечка скінчились";
                if (CitySpots(s).Count == 0) return "нема хати, щоб перебудувати";
                return Has(Hand[s], CityCost) ? null : Lack(Hand[s], CityCost);
            default:
                if (Deck.Count == 0) return "грамоти скінчились";
                return Has(Hand[s], DevCost) ? null : Lack(Hand[s], DevCost);
        }
    }

    // =====================================================================================
    // Розстановка й будівництво
    // =====================================================================================

    string? Mine(int s)
    {
        if (Over) return "Партія вже скінчилась";
        if (s < 0 || s >= MaxSeats || !In[s]) return "Ти не граєш за цим столом";
        if (s != Turn) return $"Зараз хід {NameOf(Turn)}";
        return null;
    }

    public string? PlaceTown(int s, int v)
    {
        if (Mine(s) is { } err) return err;
        if (v < 0 || v >= KhutoryGeo.Verts) return "Нема такого місця";
        if (Phase == KhPhase.Setup)
        {
            if (SetupRoad) return "Тепер поклади шлях від своєї хати";
            if (!TownFree(v)) return VOwner[v] >= 0 ? "Тут уже хтось стоїть" : "Надто близько до сусідньої хати";
            Put(s, v);
            if (SetupIdx >= Seats)
            {
                // Друга хата розстановки — по карті з кожного сусіднього гекса.
                var got = new List<string>();
                foreach (var h in G.VH[v])
                {
                    var r = B.ResOf(h);
                    if (r < 0 || Bank[r] == 0) continue;
                    Bank[r]--; Hand[s][r]++; Got[s]++;
                    got.Add(ResAcc[r]);
                }
                if (got.Count > 0) Say($"{NameOf(s)} бере на почин: {string.Join(", ", got)}");
            }
            SetupTown = v;
            SetupRoad = true;
            Emit("build", ("seat", s), ("what", "town"), ("at", v));
            return null;
        }
        if (Phase != KhPhase.Main) return Phase == KhPhase.Roll ? "Спершу кинь кубики" : "Зараз не час будувати";
        if (TownsLeft(s) <= 0) return "Хати скінчились — перебудуй якусь на містечко";
        if (!TownFree(v)) return VOwner[v] >= 0 ? "Тут уже хтось стоїть" : "Надто близько до сусідньої хати";
        if (!TouchesOwnRoad(s, v)) return "Хату ставлять лише біля свого шляху";
        if (!Has(Hand[s], TownCost)) return "Хата — це дерево, глина, вовна й зерно: " + Lack(Hand[s], TownCost);
        Pay(s, TownCost);
        Put(s, v);
        Emit("build", ("seat", s), ("what", "town"), ("at", v));
        Say($"{NameOf(s)} ставить хату");
        AfterBuild();
        return null;
    }

    public string? PlaceRoad(int s, int e)
    {
        if (Mine(s) is { } err) return err;
        if (e < 0 || e >= KhutoryGeo.EdgeCount) return "Нема такого місця";
        if (Phase == KhPhase.Setup)
        {
            if (!SetupRoad) return "Спершу постав хату";
            if (EOwner[e] >= 0) return "Тут уже є шлях";
            if (!G.Edges[e].Contains(SetupTown)) return "Шлях кладуть від щойно поставленої хати";
            EOwner[e] = s;
            Built[s][0]++;
            Emit("build", ("seat", s), ("what", "road"), ("at", e));
            RecountRoads();
            SetupRoad = false;
            SetupTown = -1;
            SetupIdx++;
            if (SetupIdx >= 2 * Seats)
            {
                Timeline.Add(PointsRow());
                TurnIdx = 0;
                Turn = Order[0];
                Phase = KhPhase.Roll;
                Emit("turn", ("seat", Turn));
                Say($"Розставились! Перший кидає {NameOf(Turn)}");
            }
            else
            {
                Turn = SetupIdx < Seats ? Order[SetupIdx] : Order[2 * Seats - 1 - SetupIdx];
                Emit("turn", ("seat", Turn));
            }
            return null;
        }
        if (Phase is not (KhPhase.Main or KhPhase.Road2)) return Phase == KhPhase.Roll ? "Спершу кинь кубики" : "Зараз не час будувати";
        if (RoadsLeft(s) <= 0) return "Шляхи скінчились";
        if (EOwner[e] >= 0) return "Тут уже є шлях";
        if (!RoadOk(s, e)) return "Шлях продовжує лише твій шлях чи хату — і не крізь чужу хату";
        var free = Phase == KhPhase.Road2;
        if (!free && !Has(Hand[s], RoadCost)) return "Шлях — це дерево й глина: " + Lack(Hand[s], RoadCost);
        if (!free) Pay(s, RoadCost);
        EOwner[e] = s;
        Built[s][0]++;
        Emit("build", ("seat", s), ("what", "road"), ("at", e));
        if (free)
        {
            Road2Left--;
            if (Road2Left <= 0 || RoadsLeft(s) <= 0 || RoadSpots(s).Count == 0) Phase = KhPhase.Main;
        }
        AfterBuild();
        return null;
    }

    public string? PlaceCity(int s, int v)
    {
        if (Mine(s) is { } err) return err;
        if (Phase != KhPhase.Main) return Phase == KhPhase.Roll ? "Спершу кинь кубики" : "Зараз не час будувати";
        if (v < 0 || v >= KhutoryGeo.Verts || VOwner[v] != s || VCity[v]) return "Містечко ставлять лише на свою хату";
        if (CitiesLeft(s) <= 0) return "Містечка скінчились";
        if (!Has(Hand[s], CityCost)) return "Містечко — це 2 зерна й 3 заліза: " + Lack(Hand[s], CityCost);
        Pay(s, CityCost);
        VCity[v] = true;
        Built[s][2]++;
        Emit("build", ("seat", s), ("what", "city"), ("at", v));
        // У журнал кімнати — лише перше містечко гравця (решта є у view.log).
        Say($"{NameOf(s)} ставить містечко", journal: Built[s][2] == 1);
        AfterBuild();
        return null;
    }

    void Put(int s, int v)
    {
        VOwner[v] = s;
        VCity[v] = false;
        Built[s][1]++;
        RecountRoads();
    }

    void Pay(int s, int[] cost)
    {
        for (var r = 0; r < 5; r++) { Hand[s][r] -= cost[r]; Bank[r] += cost[r]; }
    }

    void AfterBuild()
    {
        RecountRoads();
        Prune();
        CheckWin();
    }

    // =====================================================================================
    // Чумацький шлях і Найсильніша сотня
    // =====================================================================================

    /// <summary>Довжина найдовшого безперервного шляху гравця (чужа хата розриває).</summary>
    public int LongestRoad(int s)
    {
        var used = new bool[KhutoryGeo.EdgeCount];
        var best = 0;
        for (var v = 0; v < KhutoryGeo.Verts; v++)
            if (TouchesOwnRoad(s, v)) best = Math.Max(best, Walk(s, v, used));
        return best;
    }

    int Walk(int s, int v, bool[] used)
    {
        var best = 0;
        foreach (var e in G.VE[v])
        {
            if (EOwner[e] != s || used[e]) continue;
            used[e] = true;
            var w = G.Other(e, v);
            var blocked = VOwner[w] >= 0 && VOwner[w] != s;
            best = Math.Max(best, 1 + (blocked ? 0 : Walk(s, w, used)));
            used[e] = false;
        }
        return best;
    }

    public void RecountRoads()
    {
        Array.Clear(_towns);
        Array.Clear(_cities);
        Array.Clear(_roads);
        for (var v = 0; v < KhutoryGeo.Verts; v++)
            if (VOwner[v] >= 0) { if (VCity[v]) _cities[VOwner[v]]++; else _towns[VOwner[v]]++; }
        foreach (var o in EOwner) if (o >= 0) _roads[o]++;
        for (var s = 0; s < MaxSeats; s++) RoadLen[s] = In[s] ? LongestRoad(s) : 0;
        var max = Enumerable.Range(0, MaxSeats).Where(s => In[s]).Select(s => RoadLen[s]).DefaultIfEmpty(0).Max();
        int? next = Longest;
        if (max < 5) next = null;
        else if (Longest is { } holder && RoadLen[holder] == max) next = holder;
        else
        {
            var top = Enumerable.Range(0, MaxSeats).Where(s => In[s] && RoadLen[s] == max).ToList();
            next = top.Count == 1 ? top[0] : null;
        }
        if (next == Longest) return;
        Longest = next;
        Emit("award", ("kind", "longest"), ("seat", next));
        Say(next is { } n ? $"{NameOf(n)} тримає Чумацький шлях ({RoadLen[n]})" : "Чумацький шлях нічий", journal: true);
    }

    void RecountArmy(int s)
    {
        if (Knights[s] < 3 || Army == s) return;
        if (Army is { } a && Knights[a] >= Knights[s]) return;
        Army = s;
        Emit("award", ("kind", "army"), ("seat", s));
        Say($"{NameOf(s)} веде Найсильнішу сотню ({Knights[s]} козаки)", journal: true);
    }

    void CheckWin()
    {
        if (Phase is KhPhase.Over or KhPhase.Setup) return;
        if (Points(Turn) < Target) return;
        Winner = Turn;
        Phase = KhPhase.Over;
        Offers.Clear();
        Timeline.Add(PointsRow());
        Emit("win", ("seat", Turn));
        Say($"{NameOf(Turn)} перемагає — {Points(Turn)} очок!", journal: true);
    }

    /// <summary>Партію перервано: усі люди встали.</summary>
    public void Abandon()
    {
        if (Over) return;
        Abandoned = true;
        Phase = KhPhase.Over;
        Offers.Clear();
    }

    // =====================================================================================
    // Кидок, сімка, Кармелюк
    // =====================================================================================

    public string? Roll(int s) => Roll(s, _rng.Next(1, 7), _rng.Next(1, 7));

    /// <summary>Кидок із відомими кубиками (тести; справжній — з <see cref="Roll(int)"/>).</summary>
    public string? Roll(int s, int a, int b)
    {
        if (Mine(s) is { } err) return err;
        if (Phase != KhPhase.Roll) return Rolled ? "Кубики вже кинуто" : "Зараз не час кидати";
        Dice = [a, b];
        Rolled = true;
        var sum = a + b;
        DiceCount[sum - 2]++;
        Say($"{NameOf(s)} кидає {a} + {b} = {sum}");
        if (sum == 7)
        {
            Sevens++;
            Emit("roll", ("seat", s), ("d", new[] { a, b }), ("gains", Array.Empty<object>()));
            var any = false;
            for (var x = 0; x < MaxSeats; x++)
            {
                DiscardNeed[x] = In[x] && Total(x) > 7 ? Total(x) / 2 : 0;
                any |= DiscardNeed[x] > 0;
            }
            Back = KhPhase.Main;
            Phase = any ? KhPhase.Discard : KhPhase.Robber;
            if (any) Say("Сімка! У кого більше 7 карт — скидає половину");
            else Say("Сімка! Кармелюк іде в гості");
            return null;
        }
        // Хто що отримує: (гекс, місце, ресурс, скільки).
        var gains = new List<(int h, int seat, int r, int n)>();
        for (var h = 0; h < KhutoryGeo.Hexes; h++)
        {
            if (B.Num[h] != sum || h == Robber) continue;
            var r = B.ResOf(h);
            if (r < 0) continue;
            foreach (var v in G.HexV[h])
                if (VOwner[v] >= 0) gains.Add((h, VOwner[v], r, VCity[v] ? 2 : 1));
        }
        var given = new List<object>();
        for (var r = 0; r < 5; r++)
        {
            var mine = gains.Where(g => g.r == r).ToList();
            if (mine.Count == 0) continue;
            var need = mine.Sum(g => g.n);
            var who = mine.Select(g => g.seat).Distinct().Count();
            if (need > Bank[r] && who > 1)
            {
                Say($"У банку бракує {ResGen[r]} на всіх — ніхто не бере");
                continue;
            }
            foreach (var g in mine)
            {
                var n = Math.Min(g.n, Bank[r]);
                if (n <= 0) continue;
                Bank[r] -= n;
                Hand[g.seat][r] += n;
                Got[g.seat] += n;
                given.Add(new { hex = g.h, seat = g.seat, res = KhutoryBoard.Res[r], n });
            }
        }
        Emit("roll", ("seat", s), ("d", new[] { a, b }), ("gains", given.ToArray()));
        Phase = KhPhase.Main;
        CheckWin();
        return null;
    }

    public string? Discard(int s, int[] res)
    {
        if (Over) return "Партія вже скінчилась";
        if (Phase != KhPhase.Discard || s < 0 || s >= MaxSeats || DiscardNeed[s] <= 0) return "Тобі нічого скидати";
        if (res.Length != 5 || res.Any(n => n < 0)) return "Не зрозумів, що скинути";
        if (res.Sum() != DiscardNeed[s]) return $"Треба скинути рівно {DiscardNeed[s]}";
        for (var r = 0; r < 5; r++) if (Hand[s][r] < res[r]) return $"Стільки {ResGen[r]} у тебе нема";
        for (var r = 0; r < 5; r++) { Hand[s][r] -= res[r]; Bank[r] += res[r]; }
        Emit("discard", ("seat", s), ("n", DiscardNeed[s]));
        Say($"{NameOf(s)} скидає {DiscardNeed[s]}");
        DiscardNeed[s] = 0;
        if (DiscardNeed.All(n => n == 0)) Phase = KhPhase.Robber;
        return null;
    }

    public string? MoveRobber(int s, int h)
    {
        if (Mine(s) is { } err) return err;
        if (Phase != KhPhase.Robber) return "Зараз Кармелюка не кличуть";
        if (h < 0 || h >= KhutoryGeo.Hexes) return "Нема такого гекса";
        if (h == Robber) return "Кармелюк мусить піти на інший гекс";
        if (!RobberSpots(s).Contains(h)) return "Кармелюк дружній — бідних не грабує";
        Robber = h;
        Emit("robber", ("seat", s), ("hex", h));
        Say($"{NameOf(s)} кличе Кармелюка на {TerrainName(B.T[h])}");
        var victims = OwnersAt(h).Where(o => o != s && Total(o) > 0).OrderBy(o => o).ToList();
        if (victims.Count == 0) Phase = Back;
        else if (victims.Count == 1) TakeFrom(s, victims[0]);
        else { StealFrom = victims; Phase = KhPhase.Steal; }
        return null;
    }

    public string? Steal(int s, int victim)
    {
        if (Mine(s) is { } err) return err;
        if (Phase != KhPhase.Steal) return "Зараз нема в кого красти";
        if (!StealFrom.Contains(victim)) return "У цього гравця Кармелюк не бере";
        TakeFrom(s, victim);
        return null;
    }

    void TakeFrom(int s, int victim)
    {
        var pick = _rng.Next(Total(victim));
        var r = 0;
        while (pick >= Hand[victim][r]) { pick -= Hand[victim][r]; r++; }
        Hand[victim][r]--;
        Hand[s][r]++;
        Stolen[s]++;
        Emit("steal", [("seat", s), ("from", victim)], "res", KhutoryBoard.Res[r], [s, victim]);
        Say($"{NameOf(s)} забирає карту в {NameOf(victim)}");
        StealFrom = [];
        Phase = Back;
        Prune();
        CheckWin();
    }

    static string TerrainName(int t) => t switch
    {
        0 => "ліс", 1 => "глинище", 2 => "пасовище", 3 => "лан", 4 => "гори", _ => "Дике поле",
    };

    // =====================================================================================
    // Грамоти
    // =====================================================================================

    public string? Buy(int s)
    {
        if (Mine(s) is { } err) return err;
        if (Phase != KhPhase.Main) return Phase == KhPhase.Roll ? "Спершу кинь кубики" : "Зараз не час";
        if (Deck.Count == 0) return "Грамоти скінчились";
        if (!Has(Hand[s], DevCost)) return "Грамота — це вовна, зерно й залізо: " + Lack(Hand[s], DevCost);
        Pay(s, DevCost);
        var k = Deck[^1];
        Deck.RemoveAt(Deck.Count - 1);
        Devs[s].Add(new KhDev { K = k, Fresh = true });
        Emit("dev", [("seat", s)], "card", DevKinds[k], [s]);
        Say($"{NameOf(s)} купує грамоту");
        Prune();
        CheckWin();
        return null;
    }

    /// <summary>Зіграти грамоту. res1/res2 — ресурси для Врожайного року (обидва) чи Ярмарку (перший).</summary>
    public string? Play(int s, int k, int res1 = -1, int res2 = -1)
    {
        if (Mine(s) is { } err) return err;
        if (k is < 0 or > 4) return "Нема такої грамоти";
        if (k == Vp) return "Скарб не грають — він уже рахується тобі в очки";
        if (Phase == KhPhase.Roll ? k != Knight : Phase != KhPhase.Main)
            return Phase == KhPhase.Roll ? "До кидка можна зіграти лише козака" : "Зараз не час грати грамоту";
        if (PlayedDev) return "Одну грамоту за хід — уже зіграно";
        var card = Devs[s].FirstOrDefault(d => d.K == k && !d.Fresh);
        if (card is null)
            return Devs[s].Any(d => d.K == k) ? "Грамоту, куплену цього ходу, ще не можна грати" : "Такої грамоти в тебе нема";
        switch (k)
        {
            case Toloka:
                if (RoadsLeft(s) <= 0) return "Шляхи скінчились";
                if (RoadSpots(s).Count == 0) return "Нема куди класти шлях";
                break;
            case Plenty:
                if (res1 is < 0 or > 4 || res2 is < 0 or > 4) return "Обери два ресурси";
                if (res1 == res2 ? Bank[res1] < 2 : Bank[res1] < 1 || Bank[res2] < 1) return "У банку стільки нема";
                break;
            case Mono:
                if (res1 is < 0 or > 4) return "Обери ресурс";
                break;
        }
        Devs[s].Remove(card);
        PlayedDev = true;
        if (k != Knight) PlayedOther++;
        Emit("dev", ("seat", s), ("card", DevKinds[k]), ("play", true));
        switch (k)
        {
            case Knight:
                Knights[s]++;
                Say($"{NameOf(s)} грає козака");
                RecountArmy(s);
                Back = Phase;
                Phase = KhPhase.Robber;
                break;
            case Toloka:
                Say($"{NameOf(s)} скликає толоку — два шляхи задарма");
                Road2Left = Math.Min(2, RoadsLeft(s));
                Phase = KhPhase.Road2;
                break;
            case Plenty:
                Bank[res1]--; Hand[s][res1]++;
                Bank[res2]--; Hand[s][res2]++;
                Say($"{NameOf(s)}: Врожайний рік — бере {ResAcc[res1]} й {ResAcc[res2]}");
                break;
            case Mono:
                var took = 0;
                for (var o = 0; o < MaxSeats; o++)
                {
                    if (o == s || !In[o]) continue;
                    took += Hand[o][res1];
                    Hand[s][res1] += Hand[o][res1];
                    Hand[o][res1] = 0;
                }
                Say($"{NameOf(s)}: Ярмарок — усі віддають {ResAcc[res1]} ({took})");
                break;
        }
        Prune();
        CheckWin();
        return null;
    }

    // =====================================================================================
    // Торгівля
    // =====================================================================================

    public string? BankTrade(int s, int give, int get, int n)
    {
        if (Mine(s) is { } err) return err;
        if (Phase != KhPhase.Main) return Phase == KhPhase.Roll ? "Спершу кинь кубики" : "Зараз не час торгувати";
        if (give is < 0 or > 4 || get is < 0 or > 4) return "Не зрозумів, що на що міняти";
        if (give == get) return "Міняти ресурс на той самий — сенсу нема";
        if (n < 1 || n > 19) return "Скільки взяти?";
        var rate = Rates(s)[give];
        if (Hand[s][give] < rate * n) return $"За {n} треба віддати {rate * n} {ResGen[give]}";
        if (Bank[get] < n) return $"У банку нема стільки {ResGen[get]}";
        Hand[s][give] -= rate * n; Bank[give] += rate * n;
        Hand[s][get] += n; Bank[get] -= n;
        Trades[s]++;
        Emit("trade", ("seat", s), ("with", "bank"), ("give", ResMap(give, rate * n)), ("get", ResMap(get, n)));
        Say($"{NameOf(s)} міняє в банку {rate * n} {ResGen[give]} на {n} {ResGen[get]}");
        Prune();
        return null;
    }

    static Dictionary<string, int> ResMap(int r, int n) => new() { [KhutoryBoard.Res[r]] = n };
    /// <summary>Усі п'ять ресурсів (рука, банк, курси).</summary>
    public static Dictionary<string, int> ResMap5(int[] a)
    {
        var d = new Dictionary<string, int>();
        for (var r = 0; r < 5; r++) d[KhutoryBoard.Res[r]] = a[r];
        return d;
    }

    public static Dictionary<string, int> ResMap(int[] a)
    {
        var d = new Dictionary<string, int>();
        for (var r = 0; r < 5; r++) if (a[r] > 0) d[KhutoryBoard.Res[r]] = a[r];
        return d;
    }

    public string? Offer(int s, int[] give, int[] get, int? to)
    {
        if (Over) return "Партія вже скінчилась";
        if (s < 0 || s >= MaxSeats || !In[s]) return "Ти не граєш за цим столом";
        if (Phase != KhPhase.Main) return "Торгуються після кидка, у хід гравця";
        if (give.Length != 5 || get.Length != 5 || give.Any(n => n < 0) || get.Any(n => n < 0)) return "Не зрозумів пропозицію";
        if (give.Sum() == 0 || get.Sum() == 0) return "Треба і щось дати, і щось попросити";
        for (var r = 0; r < 5; r++) if (give[r] > 0 && get[r] > 0) return "Один ресурс і даєш, і просиш — так не міняються";
        if (!Has(Hand[s], give)) return "У тебе нема стільки карт: " + Lack(Hand[s], give);
        if (Offers.Count(o => o.From == s) >= 3) return "Не більше трьох пропозицій водночас — скасуй якусь";
        int? target;
        if (s == Turn)
        {
            if (to is { } t && (t == s || t < 0 || t >= MaxSeats || !In[t])) return "Нема такого гравця";
            target = to;
        }
        else target = Turn;
        Offers.Add(new KhOffer { Id = ++_offerId, From = s, To = target, Give = (int[])give.Clone(), Get = (int[])get.Clone() });
        return null;
    }

    public string? Answer(int s, int id, bool ok)
    {
        if (Over) return "Партія вже скінчилась";
        var o = Offers.FirstOrDefault(x => x.Id == id);
        if (o is null) return "Цієї пропозиції вже нема";
        if (!Addressed(o, s)) return "Ця пропозиція не тобі";
        if (o.From != Turn && !ok)
        {
            // Зустрічну поточний відхилив — її просто нема.
            Offers.Remove(o);
            return null;
        }
        if (ok)
        {
            if (!Has(Hand[s], o.Get)) return "Тобі нема чим заплатити: " + Lack(Hand[s], o.Get);
            if (!o.Acc.Contains(s)) o.Acc.Add(s);
            o.Dec.Remove(s);
        }
        else
        {
            if (!o.Dec.Contains(s)) o.Dec.Add(s);
            o.Acc.Remove(s);
        }
        return null;
    }

    public string? Deal(int s, int id, int with)
    {
        if (Mine(s) is { } err) return err;
        if (Phase != KhPhase.Main) return "Зараз не час торгувати";
        var o = Offers.FirstOrDefault(x => x.Id == id);
        if (o is null) return "Цієї пропозиції вже нема";
        int[] give, get;   // з погляду поточного
        if (o.From == s)
        {
            if (!o.Acc.Contains(with)) return "Цей гравець пропозицію не приймав";
            (give, get) = (o.Give, o.Get);
        }
        else
        {
            if (with != o.From) return "Міняються з автором пропозиції";
            (give, get) = (o.Get, o.Give);
        }
        if (!Has(Hand[s], give)) return "Тобі вже бракує карт: " + Lack(Hand[s], give);
        if (!Has(Hand[with], get)) return $"У {NameOf(with)} уже нема потрібних карт";
        for (var r = 0; r < 5; r++)
        {
            Hand[s][r] += get[r] - give[r];
            Hand[with][r] += give[r] - get[r];
        }
        Offers.Remove(o);
        Trades[s]++;
        Trades[with]++;
        Emit("trade", ("seat", s), ("with", with), ("give", ResMap(give)), ("get", ResMap(get)));
        Say($"{NameOf(s)} міняється з {NameOf(with)}");
        Prune();
        return null;
    }

    public string? Cancel(int s, int id)
    {
        var o = Offers.FirstOrDefault(x => x.Id == id);
        if (o is null) return "Цієї пропозиції вже нема";
        if (o.From != s) return "Скасувати може лише автор";
        Offers.Remove(o);
        return null;
    }

    /// <summary>Рука змінилась — пропозиції, яких уже не виконати, зникають, а «так» без карт знімається.</summary>
    void Prune()
    {
        Offers.RemoveAll(o => !Has(Hand[o.From], o.Give));
        foreach (var o in Offers) o.Acc.RemoveAll(a => !Has(Hand[a], o.Get));
    }

    // =====================================================================================
    // Хід
    // =====================================================================================

    public string? End(int s)
    {
        if (Mine(s) is { } err) return err;
        if (Phase != KhPhase.Main) return Phase == KhPhase.Roll ? "Спершу кинь кубики" : "Спершу заверши, що почав";
        NextTurn();
        return null;
    }

    /// <summary>Завершити хід за того, хто проспав (main або road2 — толоку недобудовано, та й годі).</summary>
    public string? ForceEnd(int s)
    {
        if (Mine(s) is { } err) return err;
        if (Phase is not (KhPhase.Main or KhPhase.Road2)) return "Зараз не час";
        NextTurn();
        return null;
    }

    void NextTurn()
    {
        Offers.Clear();
        foreach (var d in Devs[Turn]) d.Fresh = false;
        PlayedDev = false;
        Rolled = false;
        Dice = null;
        Road2Left = 0;
        TurnIdx = (TurnIdx + 1) % Seats;
        if (TurnIdx == 0)
        {
            Rounds++;
            Timeline.Add(PointsRow());
        }
        Turn = Order[TurnIdx];
        Phase = KhPhase.Roll;
        Emit("turn", ("seat", Turn));
        CheckWin();
    }

    // =====================================================================================
    // Події й журнал
    // =====================================================================================

    void Emit(string k, params (string key, object? val)[] data) => Emit(k, data, null, null, []);

    void Emit(string k, (string key, object? val)[] data, string? secretKey, object? secret, int[] insiders)
    {
        Events.Add(new KhEvent
        {
            Seq = ++Seq,
            K = k,
            Data = [.. data.Select(d => new KeyValuePair<string, object?>(d.key, d.val))],
            SecretKey = secretKey,
            Secret = secret,
            Insiders = insiders,
        });
        if (Events.Count > 60) Events.RemoveRange(0, Events.Count - 30);
    }

    void Say(string line, bool journal = false)
    {
        Log.Add(line);
        if (Log.Count > 40) Log.RemoveRange(0, Log.Count - 12);
        if (journal) Journal.Add(line);
    }

    /// <summary>Записати рядок у журнал столу (гра — про вихід, Глека тощо).</summary>
    public void Note(string line) => Say(line);
}
