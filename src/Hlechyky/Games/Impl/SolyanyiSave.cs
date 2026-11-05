using System.Text.Json;
using static Hlechyky.Games.Impl.SolyanyiMap;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Знімок «Соляного шляху» для перезапуску сервера (контракт §8, як <see cref="SoSnap"/>). Плаский, з версією <see cref="V"/>:
/// нове поле — нова властивість (стара версія знімка дасть типове значення), несумісна зміна — V + 1. Опцій тут нема: каркас
/// кличе <see cref="Solyanyi.Configure"/> перед Start + Load. Часи — абсолютні; <see cref="Solyanyi.Resumed"/> зсуває їх на паузу.
/// </summary>
public sealed class SlSnap
{
    public int V { get; set; } = 1;
    public SlCoreSnap Core { get; set; } = new();

    // Місця.
    public string?[] Bots { get; set; } = [];
    public string?[] Nicks { get; set; } = [];
    public bool[] Away { get; set; } = [];
    public int[] Missed { get; set; } = [];

    // Строки.
    public string TurnKey { get; set; } = "";
    public string PhaseKey { get; set; } = "";
    public DateTimeOffset TurnAt { get; set; }
    public DateTimeOffset PhaseAt { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? Deadline { get; set; }
    public int DeadlineMs { get; set; }
    public DateTimeOffset? BotAt { get; set; }
    public bool Announced { get; set; }

    // Глек-ведучий: що вже помітив за партію (запобіжник частоти — наново).
    public int[] HeardClaimed { get; set; } = [];
    public int[] HeardCrimea { get; set; } = [];
    public int[] HeardRoad { get; set; } = [];
}

/// <summary>Подія зі знімка (поля — JSON, як пішли б на дріт).</summary>
public sealed record SlEventSnap(long Seq, string K, List<KhFieldSnap> Data, string? SecretKey, JsonElement? Secret, int[] Insiders, int Only, long Own = 0);

/// <summary>Ядро цілком: колода й скид у тому ж порядку, ринок, замовлення, руки, шляхи, фаза, події, статистика.</summary>
public sealed class SlCoreSnap
{
    public bool[] In { get; set; } = [];
    public int Wagons0 { get; set; }
    public bool Started { get; set; }
    public int Phase { get; set; }
    public int Round { get; set; }
    public int First { get; set; }
    public int Turn { get; set; }
    public int Turns { get; set; }
    public bool Last { get; set; }
    public int Left { get; set; } = -1;
    public int LastBy { get; set; } = -1;
    public int Passes { get; set; }

    public int[] Deck { get; set; } = [];
    public int[] Discard { get; set; } = [];
    public int[] Market { get; set; } = [];
    public int[] TicketDeck { get; set; } = [];

    public int[][] Hand { get; set; } = [];
    public int[][] Tickets { get; set; } = [];
    public bool[] Done { get; set; } = [];
    public int[][] Offer { get; set; } = [];
    public bool[] Ready { get; set; } = [];

    public int[] Owner { get; set; } = [];
    public int[][] Taken { get; set; } = [];
    public int[] Wagons { get; set; } = [];
    public int[] RoutePts { get; set; } = [];
    public int[] Road { get; set; } = [];
    public int FirstPick { get; set; } = -1;

    public string Reason { get; set; } = "";
    public int[] Winners { get; set; } = [];
    public int[] LongestSeats { get; set; } = [];
    public int LongestLen { get; set; }

    public List<SlEventSnap> Events { get; set; } = [];
    public long Seq { get; set; }
    public long[] OwnSeq { get; set; } = [];
    public List<string> Log { get; set; } = [];
    public List<string> Journal { get; set; } = [];

    public List<int[]> Timeline { get; set; } = [];
    public int[] Claims { get; set; } = [];
    public int[] Biggest { get; set; } = [];
    public int[] Draws { get; set; } = [];
    public int[] Drawn { get; set; } = [];
    public int[] Facedown { get; set; } = [];
    public int[] FaceWild { get; set; } = [];
    public int[] WildsPaid { get; set; } = [];
    public int[] TicketDraws { get; set; } = [];
    public int[] TicketsKept { get; set; } = [];
    public int[] Blocks { get; set; } = [];
    public int[] Crimea { get; set; } = [];
    public int[] FirstClaim { get; set; } = [];
    /// <summary>Блоки Глека парами [шлях, кому].</summary>
    public int[][] Blocked { get; set; } = [];
}

public sealed partial class SolyanyiCore
{
    /// <summary>Так само, як вид іде на дріт: поля подій лягають у знімок тими ж байтами.</summary>
    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    /// <summary>Увесь стан ядра — у DTO (копії масивів: знімок не пливе з подальшими ходами).</summary>
    public SlCoreSnap Snapshot() => new()
    {
        In = [.. In],
        Wagons0 = Wagons0,
        Started = Started,
        Phase = (int)Phase,
        Round = Round,
        First = First,
        Turn = Turn,
        Turns = Turns,
        Last = Last,
        Left = Left,
        LastBy = LastBy,
        Passes = Passes,
        Deck = [.. Deck],
        Discard = [.. Discard],
        Market = [.. Market],
        TicketDeck = [.. TicketDeck],
        Hand = [.. Hand.Select(h => (int[])h.Clone())],
        Tickets = [.. Tickets.Select(t => t.ToArray())],
        Done = [.. Done],
        Offer = [.. Offer.Select(o => o.ToArray())],
        Ready = [.. Ready],
        Owner = [.. Owner],
        Taken = [.. Taken.Select(t => t.ToArray())],
        Wagons = [.. Wagons],
        RoutePts = [.. RoutePts],
        Road = [.. Road],
        FirstPick = FirstPick,
        Reason = Reason,
        Winners = [.. Winners],
        LongestSeats = [.. LongestSeats],
        LongestLen = LongestLen,
        Events = [.. Events.Select(e => new SlEventSnap(e.Seq, e.K,
            [.. e.Data.Select(f => new KhFieldSnap(f.Key, ToJson(f.Value)))], e.SecretKey, ToJson(e.Secret), [.. e.Insiders], e.Only, e.Own))],
        Seq = Seq,
        OwnSeq = [.. OwnSeq],
        Log = [.. Log],
        Journal = [.. Journal],
        Timeline = [.. Timeline.Select(r => (int[])r.Clone())],
        Claims = [.. Claims],
        Biggest = [.. Biggest],
        Draws = [.. Draws],
        Drawn = [.. Drawn],
        Facedown = [.. Facedown],
        FaceWild = [.. FaceWild],
        WildsPaid = [.. WildsPaid],
        TicketDraws = [.. TicketDraws],
        TicketsKept = [.. TicketsKept],
        Blocks = [.. Blocks],
        Crimea = [.. Crimea],
        FirstClaim = [.. FirstClaim],
        Blocked = [.. Blocked.Select(p => new[] { p.Key, p.Value })],
    };

    /// <summary>Ядро зі знімка. Дороги Найдовшої валки перераховуються зі шляхів. <c>NameOf</c> і <c>Rng</c> гра ставить сама.</summary>
    public static SolyanyiCore FromSnapshot(SlCoreSnap s)
    {
        const int n = MaxSeats;
        var c = new SolyanyiCore(Enumerable.Range(0, n).Where(i => i < s.In.Length && s.In[i]))
        {
            Wagons0 = s.Wagons0,
            Started = s.Started,
            Phase = (SlPhase)s.Phase,
            Round = s.Round,
            First = s.First,
            Turn = s.Turn,
            Turns = s.Turns,
            Last = s.Last,
            Left = s.Left,
            LastBy = s.LastBy,
            Passes = s.Passes,
            Deck = [.. s.Deck],
            Discard = [.. s.Discard],
            Market = s.Market.Length == MarketSize ? [.. s.Market] : [-1, -1, -1, -1, -1],
            TicketDeck = [.. s.TicketDeck],
            Hand = [.. Enumerable.Range(0, n).Select(i => i < s.Hand.Length ? Fit(s.Hand[i], 9) : new int[9])],
            Tickets = [.. Enumerable.Range(0, n).Select(i => i < s.Tickets.Length ? s.Tickets[i].ToList() : [])],
            Done = Fit(s.Done, SolyanyiMap.Tickets.Length),
            Offer = [.. Enumerable.Range(0, n).Select(i => i < s.Offer.Length ? s.Offer[i].ToList() : [])],
            Ready = Fit(s.Ready, n),
            Owner = s.Owner.Length == Routes.Length ? [.. s.Owner] : Enumerable.Repeat(-1, Routes.Length).ToArray(),
            Taken = [.. Enumerable.Range(0, n).Select(i => i < s.Taken.Length ? s.Taken[i].ToList() : [])],
            Wagons = Fit(s.Wagons, n),
            RoutePts = Fit(s.RoutePts, n),
            Road = Fit(s.Road, n),
            FirstPick = s.FirstPick,
            Reason = s.Reason,
            Winners = [.. s.Winners],
            LongestSeats = [.. s.LongestSeats],
            LongestLen = s.LongestLen,
            Events = [.. s.Events.Select(e => new SlEvent
            {
                Seq = e.Seq,
                K = e.K,
                Data = [.. e.Data.Select(f => new KeyValuePair<string, object?>(f.Key, FromJson(f.Value)))],
                SecretKey = e.SecretKey,
                Secret = FromJson(e.Secret),
                Insiders = [.. e.Insiders],
                Only = e.Only,
                Own = e.Own,
            })],
            Seq = s.Seq,
            OwnSeq = Fit(s.OwnSeq, n),
            Log = [.. s.Log],
            Journal = [.. s.Journal],
            Timeline = [.. s.Timeline.Select(r => (int[])r.Clone())],
            Claims = Fit(s.Claims, n),
            Biggest = Fit(s.Biggest, n),
            Draws = Fit(s.Draws, n),
            Drawn = Fit(s.Drawn, n),
            Facedown = Fit(s.Facedown, n),
            FaceWild = Fit(s.FaceWild, n),
            WildsPaid = Fit(s.WildsPaid, n),
            TicketDraws = Fit(s.TicketDraws, n),
            TicketsKept = Fit(s.TicketsKept, n),
            Blocks = Fit(s.Blocks, n),
            Crimea = Fit(s.Crimea, n),
            FirstClaim = s.FirstClaim.Length == n ? [.. s.FirstClaim] : Enumerable.Repeat(-1, n).ToArray(),
            Blocked = s.Blocked.Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]),
        };
        c.LongestRoutes = c.LongestSeats.ToDictionary(x => x, c.LongestPath);
        return c;
    }

    /// <summary>Глибока копія (тести: та сама публічна картина з іншими колодами — той самий хід Глека).</summary>
    public SolyanyiCore Clone()
    {
        var c = FromSnapshot(Snapshot());
        c.NameOf = NameOf;
        c.Rng = Rng;
        return c;
    }

    static T[] Fit<T>(T[]? a, int n)
    {
        var r = new T[n];
        if (a is not null) Array.Copy(a, r, Math.Min(n, a.Length));
        return r;
    }

    static JsonElement? ToJson(object? v) => v is null ? null : JsonSerializer.SerializeToElement(v, Wire);

    /// <summary>
    /// Назад у C#: числа — int, рядки, так/ні, масив чисел — int[]. Решта (дороги Найдовшої валки, карти ринку) лишається
    /// JsonElement — на дріт іде те саме, а Глек-ведучий слухає лише нові події.
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

public sealed partial class Solyanyi
{
    static readonly JsonSerializerOptions SnapJson = new(JsonSerializerDefaults.Web);

    /// <summary>Save тримає всю партію: ядро, місця з Глеками, строки — партія переживає деплой.</summary>
    public override bool Resumable => true;

    public override string? Save()
    {
        if (!_core.Started) return null;   // стіл ще не грав
        var snap = new SlSnap
        {
            Core = _core.Snapshot(),
            Bots = [.. _bots],
            Nicks = [.. _nicks],
            Away = [.. _away],
            Missed = [.. _missed],
            TurnKey = _turnKey,
            PhaseKey = _phaseKey,
            TurnAt = _turnAt,
            PhaseAt = _phaseAt,
            StartedAt = _startedAt,
            Deadline = _deadline,
            DeadlineMs = _deadlineMs,
            BotAt = _botAt,
            Announced = _announced,
            HeardClaimed = [.. _heard.Claimed.Order()],
            HeardCrimea = [.. _heard.Crimea.Order()],
            HeardRoad = [.. _heard.RoadLed.Order()],
        };
        return JsonSerializer.Serialize(snap, SnapJson);
    }

    /// <summary>
    /// Партія зі знімка — поверх свіжого <see cref="Start"/> (каркас кличе Start + Load + Resumed). Голос Глека-ведучого починає
    /// наново, але події до знімка вважає почутими: історію вдруге не озвучує. Ядро бере <c>Ctx.Rng</c> нового процесу.
    /// </summary>
    public override void Load(string json)
    {
        var snap = JsonSerializer.Deserialize<SlSnap>(json, SnapJson)
            ?? throw new InvalidOperationException("порожній знімок «Соляного шляху»");
        if (snap.V != 1) throw new InvalidOperationException($"знімок «Соляного шляху» версії {snap.V} — ця збірка вміє лише 1");
        const int n = MaxSeats;
        _bots = Fit(snap.Bots, n);
        _nicks = Fit(snap.Nicks, n);
        Array.Copy(Fit(snap.Away, n), _away, n);
        Array.Copy(Fit(snap.Missed, n), _missed, n);
        _core = SolyanyiCore.FromSnapshot(snap.Core);
        _core.NameOf = Nick;
        _core.Rng = Ctx.Rng;
        _turnKey = snap.TurnKey;
        _phaseKey = snap.PhaseKey;
        _turnAt = snap.TurnAt;
        _phaseAt = snap.PhaseAt;
        _startedAt = snap.StartedAt;
        _deadline = snap.Deadline;
        _deadlineMs = snap.DeadlineMs;
        _botAt = snap.BotAt;
        _announced = snap.Announced;
        _heard.Clear();
        foreach (var s in snap.HeardClaimed) _heard.Claimed.Add(s);
        foreach (var s in snap.HeardCrimea) _heard.Crimea.Add(s);
        foreach (var s in snap.HeardRoad) _heard.RoadLed.Add(s);
        _glek.Reset();
        _heardSeq = _core.Seq;
        Array.Clear(_tips);
    }

    /// <summary>Сервер стояв <paramref name="pause"/>: строк ходу, Глеки й пільга задрімалому чекають рівно стільки ж.</summary>
    public override void Resumed(TimeSpan pause)
    {
        if (pause <= TimeSpan.Zero) return;
        if (_deadline is { } d) _deadline = d + pause;
        if (_botAt is { } b) _botAt = b + pause;
        _turnAt += pause;
        _phaseAt += pause;
        _startedAt += pause;
    }

    static T[] Fit<T>(T[]? a, int n)
    {
        var r = new T[n];
        if (a is not null) Array.Copy(a, r, Math.Min(n, a.Length));
        return r;
    }
}
