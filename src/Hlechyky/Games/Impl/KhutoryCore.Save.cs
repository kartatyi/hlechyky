using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Знімок ядра «Хуторів» для перезапуску сервера (<see cref="Khutory.Save"/>): плаский DTO з усім станом партії —
/// поле, руки, грамоти й колода (в тому ж порядку), банк, фігури, Кармелюк, фаза й розстановка, пропозиції, події з seq,
/// журнал, лічильники статистики. Випадковість не бережемо: відновлене ядро бере <c>Random</c> нового процесу.
/// </summary>
public sealed class KhCoreSnap
{
    // Поле.
    public string BoardId { get; set; } = "";
    public int[] T { get; set; } = [];
    public int[] Num { get; set; } = [];
    public int[] PortEdge { get; set; } = [];
    public int[] PortKind { get; set; } = [];

    // Склад і правила партії.
    public bool[] In { get; set; } = [];
    public int[] Order { get; set; } = [];
    public int Target { get; set; }
    public bool Friendly { get; set; }

    // Фаза й розстановка.
    public int Phase { get; set; }
    public int Turn { get; set; }
    public int TurnIdx { get; set; }
    public int SetupIdx { get; set; }
    public bool SetupRoad { get; set; }
    public int SetupTown { get; set; }

    // Фігури.
    public int[] VOwner { get; set; } = [];
    public bool[] VCity { get; set; } = [];
    public int[] EOwner { get; set; } = [];
    public int[] Towns { get; set; } = [];
    public int[] Cities { get; set; } = [];
    public int[] Roads { get; set; } = [];

    // Руки, банк, грамоти.
    public int[][] Hand { get; set; } = [];
    public int[] Bank { get; set; } = [];
    public List<int> Deck { get; set; } = [];
    public KhDevSnap[][] Devs { get; set; } = [];
    public int[] Knights { get; set; } = [];
    public bool PlayedDev { get; set; }
    public int PlayedOther { get; set; }
    public int? Longest { get; set; }
    public int? Army { get; set; }
    public int[] RoadLen { get; set; } = [];

    // Кидок, Кармелюк, скидання, Толока.
    public int Robber { get; set; }
    public int[]? Dice { get; set; }
    public bool Rolled { get; set; }
    public int[] DiscardNeed { get; set; } = [];
    public int Back { get; set; }
    public List<int> StealFrom { get; set; } = [];
    public int Road2Left { get; set; }

    // Пропозиції, події, журнал.
    public List<KhOfferSnap> Offers { get; set; } = [];
    public int OfferId { get; set; }
    public List<KhEventSnap> Events { get; set; } = [];
    public long Seq { get; set; }
    public List<string> Log { get; set; } = [];
    public List<string> Journal { get; set; } = [];
    public int? Winner { get; set; }
    public bool Abandoned { get; set; }

    // Статистика кінця.
    public int[] DiceCount { get; set; } = [];
    public int Sevens { get; set; }
    public int Rounds { get; set; }
    public List<int[]> Timeline { get; set; } = [];
    public int[] Trades { get; set; } = [];
    public int[] Got { get; set; } = [];
    public int[] Stolen { get; set; } = [];
    public int[] Robbed { get; set; } = [];
    public List<int> Rolls { get; set; } = [];
    public int[][] Built { get; set; } = [];
}

public sealed record KhDevSnap(int K, bool Fresh);

public sealed record KhOfferSnap(int Id, int From, int? To, int[] Give, int[] Get, List<int> Acc, List<int> Dec);

/// <summary>Подія: значення полів — як JSON (на дріт підуть ті самі байти), прості назад стають int/string/bool/int[].</summary>
public sealed record KhEventSnap(long Seq, string K, List<KhFieldSnap> Data, string? SecretKey, JsonElement? Secret, int[] Insiders);

public sealed record KhFieldSnap(string Key, JsonElement? Value);

public sealed partial class KhutoryCore
{
    /// <summary>Так само, як вид іде на дріт: анонімні об'єкти подій лягають у знімок тими ж назвами.</summary>
    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    /// <summary>Увесь стан ядра — у DTO (копії масивів: знімок не пливе з подальшими ходами).</summary>
    public KhCoreSnap Snapshot() => new()
    {
        BoardId = B.Id,
        T = [.. B.T],
        Num = [.. B.Num],
        PortEdge = [.. B.PortEdge],
        PortKind = [.. B.PortKind],
        In = [.. In],
        Order = [.. Order],
        Target = Target,
        Friendly = Friendly,
        Phase = (int)Phase,
        Turn = Turn,
        TurnIdx = TurnIdx,
        SetupIdx = SetupIdx,
        SetupRoad = SetupRoad,
        SetupTown = SetupTown,
        VOwner = [.. VOwner],
        VCity = [.. VCity],
        EOwner = [.. EOwner],
        Towns = [.. _towns],
        Cities = [.. _cities],
        Roads = [.. _roads],
        Hand = [.. Hand.Select(h => (int[])h.Clone())],
        Bank = [.. Bank],
        Deck = [.. Deck],
        Devs = [.. Devs.Select(l => l.Select(d => new KhDevSnap(d.K, d.Fresh)).ToArray())],
        Knights = [.. Knights],
        PlayedDev = PlayedDev,
        PlayedOther = PlayedOther,
        Longest = Longest,
        Army = Army,
        RoadLen = [.. RoadLen],
        Robber = Robber,
        Dice = Dice is { } d ? [.. d] : null,
        Rolled = Rolled,
        DiscardNeed = [.. DiscardNeed],
        Back = (int)Back,
        StealFrom = [.. StealFrom],
        Road2Left = Road2Left,
        Offers = [.. Offers.Select(o => new KhOfferSnap(o.Id, o.From, o.To, [.. o.Give], [.. o.Get], [.. o.Acc], [.. o.Dec]))],
        OfferId = _offerId,
        Events = [.. Events.Select(e => new KhEventSnap(e.Seq, e.K,
            [.. e.Data.Select(f => new KhFieldSnap(f.Key, ToJson(f.Value)))], e.SecretKey, ToJson(e.Secret), [.. e.Insiders]))],
        Seq = Seq,
        Log = [.. Log],
        Journal = [.. Journal],
        Winner = Winner,
        Abandoned = Abandoned,
        DiceCount = [.. DiceCount],
        Sevens = Sevens,
        Rounds = Rounds,
        Timeline = [.. Timeline.Select(r => (int[])r.Clone())],
        Trades = [.. Trades],
        Got = [.. Got],
        Stolen = [.. Stolen],
        Robbed = [.. Robbed],
        Rolls = [.. Rolls],
        Built = [.. Built.Select(b => (int[])b.Clone())],
    };

    /// <summary>
    /// Ядро зі знімка. <paramref name="rng"/> — випадковість нового процесу (колода й порядок — зі знімка як є).
    /// <c>NameOf</c> гра ставить сама.
    /// </summary>
    public static KhutoryCore FromSnapshot(KhCoreSnap s, Random rng)
    {
        var board = new KhutoryBoard
        {
            Id = s.BoardId,
            T = Fit(s.T, KhutoryGeo.Hexes),
            Num = Fit(s.Num, KhutoryGeo.Hexes),
            PortEdge = Fit(s.PortEdge, 9),
            PortKind = Fit(s.PortKind, 9),
        };
        var seats = Enumerable.Range(0, MaxSeats).Where(i => i < s.In.Length && s.In[i]);
        var c = new KhutoryCore(board, seats, rng)
        {
            Order = [.. s.Order],
            Target = s.Target,
            Friendly = s.Friendly,
            Phase = (KhPhase)s.Phase,
            Turn = s.Turn,
            TurnIdx = s.TurnIdx,
            SetupIdx = s.SetupIdx,
            SetupRoad = s.SetupRoad,
            SetupTown = s.SetupTown,
            VOwner = Fit(s.VOwner, KhutoryGeo.Verts),
            VCity = Fit(s.VCity, KhutoryGeo.Verts),
            EOwner = Fit(s.EOwner, KhutoryGeo.EdgeCount),
            Hand = [.. Enumerable.Range(0, MaxSeats).Select(i => i < s.Hand.Length ? Fit(s.Hand[i], 5) : new int[5])],
            Bank = Fit(s.Bank, 5),
            Deck = [.. s.Deck],
            Devs = [.. Enumerable.Range(0, MaxSeats).Select(i => i < s.Devs.Length
                ? s.Devs[i].Select(d => new KhDev { K = d.K, Fresh = d.Fresh }).ToList()
                : new List<KhDev>())],
            Knights = Fit(s.Knights, MaxSeats),
            PlayedDev = s.PlayedDev,
            PlayedOther = s.PlayedOther,
            Longest = s.Longest,
            Army = s.Army,
            RoadLen = Fit(s.RoadLen, MaxSeats),
            Robber = s.Robber,
            Dice = s.Dice is { } d ? [.. d] : null,
            Rolled = s.Rolled,
            DiscardNeed = Fit(s.DiscardNeed, MaxSeats),
            Back = (KhPhase)s.Back,
            StealFrom = [.. s.StealFrom],
            Road2Left = s.Road2Left,
            Offers = [.. s.Offers.Select(o => new KhOffer
            {
                Id = o.Id, From = o.From, To = o.To, Give = Fit(o.Give, 5), Get = Fit(o.Get, 5), Acc = [.. o.Acc], Dec = [.. o.Dec],
            })],
            Events = [.. s.Events.Select(e => new KhEvent
            {
                Seq = e.Seq,
                K = e.K,
                Data = [.. e.Data.Select(f => new KeyValuePair<string, object?>(f.Key, FromJson(f.Value)))],
                SecretKey = e.SecretKey,
                Secret = FromJson(e.Secret),
                Insiders = [.. e.Insiders],
            })],
            Seq = s.Seq,
            Log = [.. s.Log],
            Journal = [.. s.Journal],
            Winner = s.Winner,
            Abandoned = s.Abandoned,
            DiceCount = Fit(s.DiceCount, 11),
            Sevens = s.Sevens,
            Rounds = s.Rounds,
            Timeline = [.. s.Timeline.Select(r => (int[])r.Clone())],
            Trades = Fit(s.Trades, MaxSeats),
            Got = Fit(s.Got, MaxSeats),
            Stolen = Fit(s.Stolen, MaxSeats),
            Robbed = Fit(s.Robbed, MaxSeats),
            Rolls = [.. (s.Rolls ?? []).TakeLast(RollsKeep)],
            Built = [.. Enumerable.Range(0, MaxSeats).Select(i => i < s.Built.Length ? Fit(s.Built[i], 3) : new int[3])],
        };
        c._offerId = s.OfferId;
        Array.Copy(Fit(s.Towns, MaxSeats), c._towns, MaxSeats);
        Array.Copy(Fit(s.Cities, MaxSeats), c._cities, MaxSeats);
        Array.Copy(Fit(s.Roads, MaxSeats), c._roads, MaxSeats);
        return c;
    }

    /// <summary>Масив рівно потрібної довжини (знімок зі старої версії міг бути коротшим — решта нулі).</summary>
    static T[] Fit<T>(T[]? a, int n)
    {
        var r = new T[n];
        if (a is not null) Array.Copy(a, r, Math.Min(n, a.Length));
        return r;
    }

    static JsonElement? ToJson(object? v) => v is null ? null : JsonSerializer.SerializeToElement(v, Wire);

    /// <summary>
    /// Назад у C#: числа — int, рядки, так/ні, масив чисел — int[] (як їх кладе ядро й читає Глек). Решта (улов кидка,
    /// набори обміну) лишається JsonElement — на дріт іде те саме, а Глек слухає лише нові події.
    /// </summary>
    static object? FromJson(JsonElement? v)
    {
        if (v is not { } e) return null;
        switch (e.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.Number when e.TryGetInt32(out var n):
                return n;
            case JsonValueKind.String:
                return e.GetString();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Array when e.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out _)):
                return e.GetArrayLength() == 0 ? e.Clone() : e.EnumerateArray().Select(x => x.GetInt32()).ToArray();
            default:
                return e.Clone();
        }
    }
}
