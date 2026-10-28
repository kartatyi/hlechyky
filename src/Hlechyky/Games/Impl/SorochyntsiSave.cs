using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Знімок «Сорочинського ярмарку» для перезапуску сервера (контракт §8, як <see cref="KhutorySnap"/>). Плаский, з версією
/// <see cref="V"/>: нове поле — нова властивість (стара версія знімка дасть типове значення), несумісна зміна — V + 1.
/// Опцій тут нема: каркас кличе <see cref="Sorochyntsi.Configure"/> перед Start + Load, а ціль живе в ядрі. Часи —
/// абсолютні; <see cref="Sorochyntsi.Resumed"/> зсуває їх на паузу.
/// </summary>
public sealed class SoSnap
{
    public int V { get; set; } = 1;
    public SoCoreSnap Core { get; set; } = new();

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

    // Глек-ведучий: що вже казав за партію (запобіжник частоти — наново).
    public int[] Bought { get; set; } = [];
    public bool T3Said { get; set; }
}

/// <summary>Ядро цілком: колоди в тому ж порядку, ряди, банк, прилавки, бронь з blind, гості, нагороди, фаза, події, статистика.</summary>
public sealed class SoCoreSnap
{
    public bool[] In { get; set; } = [];
    public int Target { get; set; }
    public bool Started { get; set; }
    public int Phase { get; set; }
    public int Round { get; set; }
    public int First { get; set; }
    public int Turn { get; set; }
    public bool Last { get; set; }
    public string? LastReason { get; set; }

    public int[][] Decks { get; set; } = [];
    public int[][] Rows { get; set; } = [];
    public int[] Bank { get; set; } = [];
    public int[] Bank0 { get; set; } = [];
    public int[][] Tok { get; set; } = [];
    public int[][] Cards { get; set; } = [];
    public int[][] ResIds { get; set; } = [];
    public bool[][] ResBlind { get; set; } = [];

    public int[] Table { get; set; } = [];
    public int[] GuestBy { get; set; } = [];
    public int[][] GuestsOf { get; set; } = [];
    public int Starshyna { get; set; } = -1;
    public int Bulava { get; set; } = -1;
    public int DiscardN { get; set; }
    public int[] GuestChoice { get; set; } = [];
    public int Passes { get; set; }
    public string Reason { get; set; } = "";
    public int[] Winners { get; set; } = [];

    public List<KhEventSnap> Events { get; set; } = [];
    public long Seq { get; set; }
    public List<string> Log { get; set; } = [];
    public List<string> Journal { get; set; } = [];

    public int Turns { get; set; }
    public List<int[]> Timeline { get; set; } = [];
    public int[] Takes { get; set; } = [];
    public int[] TokensGot { get; set; } = [];
    public int[] Reserves { get; set; } = [];
    public int[] Blinds { get; set; } = [];
    public int[] GoldSpent { get; set; } = [];
    public int[] Returned { get; set; } = [];
    public int[] Ferns { get; set; } = [];
    public int[] FirstT3 { get; set; } = [];
    public int[][] Buys { get; set; } = [];
}

public sealed partial class SorochyntsiCore
{
    /// <summary>Так само, як вид іде на дріт: поля подій лягають у знімок тими ж байтами.</summary>
    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    /// <summary>Увесь стан ядра — у DTO (копії масивів: знімок не пливе з подальшими ходами).</summary>
    public SoCoreSnap Snapshot() => new()
    {
        In = [.. In],
        Target = Target,
        Started = Started,
        Phase = (int)Phase,
        Round = Round,
        First = First,
        Turn = Turn,
        Last = Last,
        LastReason = LastReason,
        Decks = [.. Decks.Select(d => d.ToArray())],
        Rows = [.. Rows.Select(r => (int[])r.Clone())],
        Bank = [.. Bank],
        Bank0 = [.. Bank0],
        Tok = [.. Tok.Select(t => (int[])t.Clone())],
        Cards = [.. Cards.Select(c => c.ToArray())],
        ResIds = [.. Reserved.Select(l => l.Select(r => r.Id).ToArray())],
        ResBlind = [.. Reserved.Select(l => l.Select(r => r.Blind).ToArray())],
        Table = [.. Table],
        GuestBy = [.. GuestBy],
        GuestsOf = [.. GuestsOf.Select(g => g.ToArray())],
        Starshyna = Starshyna,
        Bulava = Bulava,
        DiscardN = DiscardN,
        GuestChoice = [.. GuestChoice],
        Passes = Passes,
        Reason = Reason,
        Winners = [.. Winners],
        Events = [.. Events.Select(e => new KhEventSnap(e.Seq, e.K,
            [.. e.Data.Select(f => new KhFieldSnap(f.Key, ToJson(f.Value)))], e.SecretKey, ToJson(e.Secret), [.. e.Insiders]))],
        Seq = Seq,
        Log = [.. Log],
        Journal = [.. Journal],
        Turns = Turns,
        Timeline = [.. Timeline.Select(r => (int[])r.Clone())],
        Takes = [.. Takes],
        TokensGot = [.. TokensGot],
        Reserves = [.. Reserves],
        Blinds = [.. Blinds],
        GoldSpent = [.. GoldSpent],
        Returned = [.. Returned],
        Ferns = [.. Ferns],
        FirstT3 = [.. FirstT3],
        Buys = [.. Buys.Select(b => (int[])b.Clone())],
    };

    /// <summary>Ядро зі знімка. Знижки, ⭐ крамниць і ⚔ перераховуються з куплених крамниць. <c>NameOf</c> гра ставить сама.</summary>
    public static SorochyntsiCore FromSnapshot(SoCoreSnap s)
    {
        const int n = MaxSeatsN;
        var c = new SorochyntsiCore(Enumerable.Range(0, n).Where(i => i < s.In.Length && s.In[i]))
        {
            Target = s.Target,
            Started = s.Started,
            Phase = (SoPhase)s.Phase,
            Round = s.Round,
            First = s.First,
            Turn = s.Turn,
            Last = s.Last,
            LastReason = s.LastReason,
            Decks = [.. Enumerable.Range(0, 3).Select(t => t < s.Decks.Length ? s.Decks[t].ToList() : [])],
            Rows = [.. Enumerable.Range(0, 3).Select(t => t < s.Rows.Length ? FitRow(s.Rows[t]) : [-1, -1, -1, -1])],
            Bank = Fit(s.Bank, 6),
            Bank0 = Fit(s.Bank0, 6),
            Tok = [.. Enumerable.Range(0, n).Select(i => i < s.Tok.Length ? Fit(s.Tok[i], 6) : new int[6])],
            Cards = [.. Enumerable.Range(0, n).Select(i => i < s.Cards.Length ? s.Cards[i].ToList() : [])],
            Reserved = [.. Enumerable.Range(0, n).Select(i => i < s.ResIds.Length
                ? s.ResIds[i].Select((id, k) => new SoRes(id, i < s.ResBlind.Length && k < s.ResBlind[i].Length && s.ResBlind[i][k])).ToList()
                : new List<SoRes>())],
            Table = [.. s.Table],
            GuestBy = s.GuestBy.Length == 10 ? [.. s.GuestBy] : Enumerable.Repeat(-1, 10).ToArray(),
            GuestsOf = [.. Enumerable.Range(0, n).Select(i => i < s.GuestsOf.Length ? s.GuestsOf[i].ToList() : [])],
            Starshyna = s.Starshyna,
            Bulava = s.Bulava,
            DiscardN = s.DiscardN,
            GuestChoice = [.. s.GuestChoice],
            Passes = s.Passes,
            Reason = s.Reason,
            Winners = [.. s.Winners],
            Events = [.. s.Events.Select(e => new SoEvent
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
            Turns = s.Turns,
            Timeline = [.. s.Timeline.Select(r => (int[])r.Clone())],
            Takes = Fit(s.Takes, n),
            TokensGot = Fit(s.TokensGot, n),
            Reserves = Fit(s.Reserves, n),
            Blinds = Fit(s.Blinds, n),
            GoldSpent = Fit(s.GoldSpent, n),
            Returned = Fit(s.Returned, n),
            Ferns = Fit(s.Ferns, n),
            FirstT3 = s.FirstT3.Length == n ? [.. s.FirstT3] : [-1, -1, -1, -1],
            Buys = [.. Enumerable.Range(0, n).Select(i => i < s.Buys.Length ? Fit(s.Buys[i], 3) : new int[3])],
        };
        for (var i = 0; i < n; i++)
            foreach (var id in c.Cards[i])
            {
                var card = SorochyntsiCards.Deck[id];
                c.Bonus[i][card.Bonus]++;
                c.CardPts[i] += card.Pts;
                c.Icons[i] += card.K;
            }
        return c;
    }

    const int MaxSeatsN = SorochyntsiCards.MaxSeats;

    /// <summary>Глибока копія (тести: та сама публічна картина з іншими колодами — той самий хід Глека).</summary>
    public SorochyntsiCore Clone()
    {
        var c = FromSnapshot(Snapshot());
        c.NameOf = NameOf;
        return c;
    }

    static int[] FitRow(int[]? a)
    {
        var r = new[] { -1, -1, -1, -1 };
        if (a is not null) Array.Copy(a, r, Math.Min(4, a.Length));
        return r;
    }

    static T[] Fit<T>(T[]? a, int n)
    {
        var r = new T[n];
        if (a is not null) Array.Copy(a, r, Math.Min(n, a.Length));
        return r;
    }

    static JsonElement? ToJson(object? v) => v is null ? null : JsonSerializer.SerializeToElement(v, Wire);

    /// <summary>
    /// Назад у C#: числа — int, рядки, так/ні, масив чисел — int[]. Решта (набори жетонів) лишається JsonElement — на дріт
    /// іде те саме, а Глек-ведучий слухає лише нові події.
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

public sealed partial class Sorochyntsi
{
    static readonly JsonSerializerOptions SnapJson = new(JsonSerializerDefaults.Web);

    /// <summary>Save тримає всю партію: ядро, місця з Глеками, строки — партія переживає деплой.</summary>
    public override bool Resumable => true;

    public override string? Save()
    {
        if (!_core.Started) return null;   // стіл ще не грав
        var snap = new SoSnap
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
            Bought = [.. _bought.Order()],
            T3Said = _t3Said,
        };
        return JsonSerializer.Serialize(snap, SnapJson);
    }

    /// <summary>
    /// Партія зі знімка — поверх свіжого <see cref="Start"/> (каркас кличе Start + Load + Resumed). Голос Глека-ведучого
    /// починає наново, але події до знімка вважає почутими: історію вдруге не озвучує.
    /// </summary>
    public override void Load(string json)
    {
        var snap = JsonSerializer.Deserialize<SoSnap>(json, SnapJson)
            ?? throw new InvalidOperationException("порожній знімок «Сорочинського ярмарку»");
        if (snap.V != 1) throw new InvalidOperationException($"знімок «Сорочинського ярмарку» версії {snap.V} — ця збірка вміє лише 1");
        const int n = SorochyntsiCards.MaxSeats;
        _bots = Fit(snap.Bots, n);
        _nicks = Fit(snap.Nicks, n);
        Array.Copy(Fit(snap.Away, n), _away, n);
        Array.Copy(Fit(snap.Missed, n), _missed, n);
        _core = SorochyntsiCore.FromSnapshot(snap.Core);
        _core.NameOf = Nick;
        _turnKey = snap.TurnKey;
        _phaseKey = snap.PhaseKey;
        _turnAt = snap.TurnAt;
        _phaseAt = snap.PhaseAt;
        _startedAt = snap.StartedAt;
        _deadline = snap.Deadline;
        _deadlineMs = snap.DeadlineMs;
        _botAt = snap.BotAt;
        _announced = snap.Announced;
        _bought.Clear();
        foreach (var s in snap.Bought) _bought.Add(s);
        _t3Said = snap.T3Said;
        _glek.Reset();
        _heardSeq = _core.Seq;
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
