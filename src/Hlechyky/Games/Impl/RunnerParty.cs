using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Кадр пакета «runner» (dino.md §4.2, storks.md §4.2): крок, фаза і стан кожного місця короткими масивами.
/// Порожні поля (сніжки, пінги, події) на дріт не йдуть — кадр 25 разів на секунду, байти рахуються.
/// </summary>
public sealed class RunnerFrame
{
    public int S { get; init; }
    public string Ph { get; init; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? D { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? M { get; init; }
    public int[]?[] P { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int[][]? Sn { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int?[]? Pg { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public object[][]? Ev { get; init; }
    /// <summary>Лелеки з «ключем»: останні вирішені блоки ключа [крок, маска, …] (RunnerSim.WireKey).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int[]? Ky { get; init; }
}

/// <summary>
/// Спільне для партій Стрибозаврів і Лелек: раунди (відлік → біг → таблиця), очки за місця, вихід
/// посеред партії, ввід із перемотуванням, вид і кадр. Правила руху — у <see cref="RunnerSim"/>, а тут —
/// лише те, що навколо: хто за столом, скільки раундів, хто переміг.
/// </summary>
public abstract class RunnerParty : Game
{
    public const int ReadySteps = 150, RoundCap = 6000, OverSteps = 200, PmCap = 1000;
    public const string Lobby = "lobby", Ready = "ready", Running = "run", Over = "over", Done = "done";
    const int Seats = RunnerSim.Seats;

    protected abstract RunnerMode Mode { get; }
    protected abstract string[] Names { get; }
    /// <summary>Друга опція: сніжки (Стрибозаври) чи запасне пір'я (Лелеки).</summary>
    protected bool Extra = true;

    protected RunnerSim? Sim;
    protected string Phase = Lobby;

    /// <summary>Світ поточного раунду (тести й діагностика; правила звідси не міняються).</summary>
    public RunnerSim? World => Sim;
    public string PhaseName => Phase;
    int _optRounds = 3, _rounds = 3, _round, _seed, _overLeft;
    protected int N0;
    readonly int[] _points = new int[Seats];
    readonly int[] _eggsParty = new int[Seats];
    readonly List<int[]> _roundPoints = [];
    readonly int?[] _ping = new int?[Seats];
    bool _pinged, _fresh;
    readonly bool[] _announced = new bool[Seats];
    protected readonly bool[] Awarded = new bool[Seats];
    int _evSeen;
    object? _result;
    RunnerPacer _pacer;
    /// <summary>
    /// Режим вечірки (docs/games/specs/party-minigame.md): один забіг, боти на місцях <c>bots</c> (їх дає нащадок через
    /// <see cref="NewBot"/>), стеля бігу <see cref="PartyRoundCap"/>, scores — метри. Без нагород і серії.
    /// </summary>
    protected PartyMode? PartyM;
    public bool Party => PartyM is not null;
    /// <summary>Забіг у вечірці — 65 с (з відліком 3 с — 68 с під стелю 75 с).</summary>
    public const int PartyRoundCap = 3250;
    readonly bool[] _partyBot = new bool[Seats];
    readonly DinoBot?[] _bots = new DinoBot?[Seats];
    readonly long[] _partyMetres = new long[Seats];
    bool In(int seat) => Ctx.Seated(seat) || _partyBot[seat];
    /// <summary>Мозок бота вечірки для місця; null — гра ботів не має (тоді місце просто стоїть).</summary>
    protected virtual DinoBot? NewBot(int seat, LiveBots.Level level) => null;
    public override string? SeatBot(int seat) => seat >= 0 && seat < Seats && _partyBot[seat] && !Ctx.Seated(seat) ? LiveBots.Name : null;

    public override string SeatName(int seat) => seat >= 0 && seat < Names.Length ? Names[seat] : base.SeatName(seat);

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _optRounds = options.TryGetValue("rounds", out var r) && r is "1" or "3" or "5" ? int.Parse(r) : 3;
        Extra = !(options.TryGetValue(ExtraKey, out var e) && e == "off");
        PartyM = PartyMode.Read(options);
        if (PartyM is not null) { _optRounds = 1; Extra = true; }
    }

    protected abstract string ExtraKey { get; }

    public override void Start()
    {
        N0 = 0;
        Array.Clear(_partyBot);
        Array.Clear(_bots);
        Array.Clear(_partyMetres);
        if (PartyM is { } pm)
            foreach (var b in pm.Bots)
                if (b >= 0 && b < Seats && !Ctx.Seated(b))
                {
                    _partyBot[b] = true;
                    _bots[b] = NewBot(b, pm.Level);
                }
        for (var i = 0; i < Seats; i++) if (In(i)) N0++;
        _rounds = N0 <= 1 || PartyM is not null ? 1 : _optRounds;
        Array.Clear(_points);
        Array.Clear(_eggsParty);
        Array.Clear(Awarded);
        _roundPoints.Clear();
        _round = 0;
        _result = null;
        _pacer.Reset();
        OnPartyStart();
        NewRound();
    }

    protected virtual void OnPartyStart() { }

    void NewRound()
    {
        _round++;
        _seed = Ctx.Rng.Next(1, int.MaxValue);
        var plays = new bool[Seats];
        for (var i = 0; i < Seats; i++) plays[i] = In(i);
        Sim = new RunnerSim(Mode, _seed, plays, ReadySteps, PmCap, snowOn: Mode == RunnerMode.Dino && Extra && N0 > 1, featherOn: Extra);
        Array.Clear(_announced);
        Array.Clear(_ping);
        _pinged = false;
        _evSeen = 0;
        Phase = Ready;
        _fresh = true;
        OnRoundStart();
    }

    protected virtual void OnRoundStart() { }

    // ---------- ввід ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (Sim is null || seat < 0 || seat >= Seats) return ActResult.Fail("Партія ще не почалась");
        switch (action)
        {
            case "in":
                if (Phase is not (Ready or Running)) return ActResult.Fail("Зачекай старту");
                if (!TryIn(payload, out var s, out var k)) return ActResult.Fail("Тут так не ходять");
                return Sim.Input(seat, s, k) ? ActResult.Done : ActResult.Fail("Запізно");
            case "ping":
                if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("t", out var t)
                    || t.ValueKind != JsonValueKind.Number || !t.TryGetInt32(out var tv)) return ActResult.Fail("Тут так не ходять");
                _ping[seat] = tv & 0xFFFFF;
                _pinged = true;
                return ActResult.Done;
            default:
                return Other(seat, action, payload);
        }
    }

    /// <summary>Решта дій (кидок сніжки). Типово — нічого такого тут нема.</summary>
    protected virtual ActResult Other(int seat, string action, JsonElement payload) => ActResult.Fail("Тут так не ходять");

    /// <summary>Рівно те, що шле модуль: <c>{ s: int, k: 0..7 }</c>; решта полів не заважає, сміття — ні.</summary>
    public static bool TryIn(JsonElement payload, out int s, out int k)
    {
        s = k = 0;
        if (payload.ValueKind != JsonValueKind.Object) return false;
        if (!payload.TryGetProperty("s", out var se) || se.ValueKind != JsonValueKind.Number || !se.TryGetInt32(out s)) return false;
        if (!payload.TryGetProperty("k", out var ke) || ke.ValueKind != JsonValueKind.Number || !ke.TryGetInt32(out k)) return false;
        return k is >= 0 and <= 7;
    }

    // ---------- тик ----------

    public override TickResult Tick()
    {
        if (Sim is null) return TickResult.None;
        switch (Phase)
        {
            case Ready:
            case Running:
            {
                var view = _fresh;
                _fresh = false;
                var steps = _pacer.Due(Ctx.Clock.UtcNow);
                for (var i = 0; i < steps; i++)
                {
                    // боти вечірки тиснуть перед кроком — тим самим вводом, що й людина між тиками
                    foreach (var bot in _bots) bot?.Think(Sim, Ctx.Rng);
                    Sim.Step();
                    if (Phase == Ready && Sim.S >= ReadySteps)
                    {
                        Phase = Running;
                        view = true;
                    }
                    view |= AfterStep();
                    if (RoundEnds())
                    {
                        EndRound();
                        return TickResult.Both;
                    }
                }
                return view ? TickResult.Both : TickResult.FrameOnly;
            }
            case Over:
                _overLeft -= _pacer.Due(Ctx.Clock.UtcNow);
                if (_overLeft > 0) return TickResult.None;
                if (_round < _rounds)
                {
                    NewRound();
                    return TickResult.Both;
                }
                FinishParty();
                return TickResult.Both;
            default:
                return TickResult.None;
        }
    }

    /// <summary>Після кожного кроку: нові вибулі, ачівки. true — хтось вибув (треба розіслати вид).</summary>
    bool AfterStep()
    {
        var sim = Sim!;
        var changed = false;
        var run = sim.S - 1 - ReadySteps;
        for (var i = 0; i < Seats; i++)
        {
            var p = sim.P[i];
            if (!p.Plays || !p.Out || _announced[i]) continue;
            _announced[i] = true;
            changed = true;
            _partyMetres[i] = MetresOf(sim, p, run);
            OnOut(i, p, run);
        }
        for (; _evSeen < sim.EventCount; _evSeen++) OnEvent(sim.Event(_evSeen));
        OnStepDone(run);
        return changed;
    }

    protected virtual void OnOut(int seat, RunnerPlayer p, int run) { }

    protected virtual void OnEvent(in RunnerEvent e) { }

    protected virtual void OnStepDone(int run) { }

    /// <summary>Скільки ще за столом у цьому раунді й скільки з них біжить/летить.</summary>
    (int Table, int Alive) Count()
    {
        int n = 0, alive = 0;
        foreach (var p in Sim!.P)
        {
            if (!p.Plays) continue;
            n++;
            if (!p.Out) alive++;
        }
        return (n, alive);
    }

    bool RoundEnds()
    {
        var (n, alive) = Count();
        if (alive == 0) return true;
        if (n >= 2 && alive <= 1) return true;
        return Sim!.S - ReadySteps >= (PartyM is null ? RoundCap : PartyRoundCap);
    }

    /// <summary>Скільки метрів пробіг сам гравець (лінія темпу мінус відставання).</summary>
    static long MetresOf(RunnerSim sim, RunnerPlayer p, int run) =>
        Math.Max(0, sim.PaceX(Math.Max(0, run + 1)) - p.Lag) / RunnerDino.SubPerMetre;

    /// <summary>Scores вечірки — метри кожного місця: вибулий — скільки пробіг до лавини, живий — скільки вже. Не грав — −1.</summary>
    public IReadOnlyDictionary<int, long> PartyScores()
    {
        var r = new Dictionary<int, long>(Ctx.Players);
        var sim = Sim;
        for (var i = 0; i < Ctx.Players; i++)
        {
            if (sim is null || i >= Seats || !sim.P[i].Plays) { r[i] = -1; continue; }
            var p = sim.P[i];
            r[i] = p.Out ? _partyMetres[i] : MetresOf(sim, p, Math.Max(0, sim.S - 1 - ReadySteps));
        }
        return r;
    }

    void PartyOver()
    {
        Phase = Done;
        var scores = PartyScores();
        var best = scores.Count == 0 ? 0 : scores.Values.Max();
        var winners = scores.Where(kv => kv.Value == best && kv.Value >= 0).Select(kv => kv.Key).Order().ToArray();
        _result = new
        {
            winners,
            draw = false,
            table = scores.Where(kv => kv.Value >= 0).OrderByDescending(kv => kv.Value)
                .Select(kv => new { seat = kv.Key, points = kv.Value, eggs = 0 }).ToArray(),
        };
        Ctx.Finish(winners, $"{Info.Title}: " + string.Join(" · ", scores.Where(kv => kv.Value >= 0)
            .OrderByDescending(kv => kv.Value).Select(kv => $"{Ctx.NickOf(kv.Key) ?? LiveBots.Name} {kv.Value} м")), scores);
    }

    /// <summary>Місця тим, хто вистояв (на стелі — за відставанням), очки раунду, таблиця.</summary>
    void EndRound()
    {
        var sim = Sim!;
        var run = sim.S - 1 - ReadySteps;
        // Після кінця раунду ввід не приймається й перемотувань більше не буде: те, що чекало на вікно
        // перемотування (влучання сніжкою, «Чисте небо»), можна вирішити вже зараз, а не губити.
        OnRoundEnd(run);
        for (var i = 0; i < Seats; i++)
        {
            var p = sim.P[i];
            if (!p.Plays || p.Out) continue;
            var better = 0;
            for (var j = 0; j < Seats; j++)
            {
                var q = sim.P[j];
                if (j != i && q.Plays && !q.Out && Rank(q) < Rank(p)) better++;
            }
            p.Place = better + 1;
            OnSurvive(i, p, run);
        }
        if (PartyM is not null)
        {
            PartyOver();
            return;
        }
        var row = new int[Seats];
        for (var i = 0; i < Seats; i++)
        {
            var p = sim.P[i];
            if (!p.Plays || N0 <= 1) continue;
            row[i] = (N0 - p.Place + 1) * 3 + Bonus(p);
            _points[i] += row[i];
            _eggsParty[i] += Bonus(p);
        }
        _roundPoints.Add(row);
        Phase = Over;
        _overLeft = OverSteps;
    }

    /// <summary>Раунд скінчився (ще до місць і очок): вирішити все, що чекало на вікно перемотування.</summary>
    protected virtual void OnRoundEnd(int run) { }

    /// <summary>Чим ранжувати тих, хто дожив до стелі раунду: менше — краще. Типово всі рівні.</summary>
    protected virtual int Rank(RunnerPlayer p) => 0;

    /// <summary>Очки понад місце (яйця у Стрибозаврів).</summary>
    protected virtual int Bonus(RunnerPlayer p) => 0;

    protected virtual void OnSurvive(int seat, RunnerPlayer p, int run) { }

    void FinishParty()
    {
        Phase = Done;
        var seated = new List<int>();
        for (var i = 0; i < Seats; i++) if (Ctx.Seated(i)) seated.Add(i);
        if (N0 <= 1)
        {
            _result = new { winners = seated.ToArray(), table = Array.Empty<object>() };
            Ctx.Finish([.. seated], "");
            return;
        }
        var order = seated.OrderByDescending(s => _points[s]).ThenBy(s => s).ToList();
        var best = order.Count > 0 ? _points[order[0]] : 0;
        var winners = order.Where(s => _points[s] == best).ToArray();
        // Усі за столом порівну (найчастіше — усі разом вибули, нічого не тиснувши): це нічия, а не перемога
        // кожного. Інакше двоє, що просто стоять, щопартії брали б черепки й ачівки за перемогу.
        var draw = winners.Length == order.Count && order.Count >= 2;
        if (draw) winners = [];
        _result = new
        {
            winners,
            draw,
            table = order.Select(s => new { seat = s, points = _points[s], eggs = _eggsParty[s] }).ToArray(),
        };
        Ctx.Finish(winners, $"{Info.Title}: {(draw ? "нічия — " : "")}" + string.Join(" · ", order.Select(s => $"{Ctx.NickOf(s)} {_points[s]}")));
    }

    // ---------- вихід ----------

    public override void OnLeave(int seat)
    {
        if (PartyM is not null) return;   // вечірка: місце просто стоїть, забіг догравається
        var nick = Ctx.NickOf(seat);
        if (Sim is not null && seat >= 0 && seat < Seats) Sim.P[seat].Plays = false;
        var left = new List<int>();
        for (var i = 0; i < Seats; i++) if (i != seat && Ctx.Seated(i)) left.Add(i);
        if (left.Count >= 2)
        {
            Ctx.Log($"{Info.Title}: {nick} встав з-за столу");
            return;
        }
        Phase = Done;
        if (left.Count == 1)
        {
            _result = new { winners = new[] { left[0] }, table = Array.Empty<object>() };
            Ctx.Finish([left[0]], N0 <= 1 ? "" : $"{Info.Title}: {nick} встав з-за столу, {Ctx.NickOf(left[0])} лишився сам");
            return;
        }
        Ctx.Finish([], "");
    }

    // ---------- вид і кадр ----------

    int[]? LobbyWire(int seat)
    {
        if (!Ctx.Seated(seat)) return null;
        return Mode == RunnerMode.Dino
            ? [0, 0, 0, 0, 0, 0, 0, 0, -1, -1, 0]
            : [RunnerStorks.StartY, 0, 1, Extra ? 1 : 0, 0, -1];
    }

    protected int[]?[] Players()
    {
        var a = new int[]?[Seats];
        for (var i = 0; i < Seats; i++) a[i] = Sim is null ? LobbyWire(i) : Sim.Wire(i);
        return a;
    }

    protected bool[] Plays()
    {
        var a = new bool[Seats];
        for (var i = 0; i < Seats; i++) a[i] = Sim is null ? Ctx.Seated(i) : Sim.P[i].Plays;
        return a;
    }

    protected bool[] Alive()
    {
        var a = new bool[Seats];
        for (var i = 0; i < Seats; i++) a[i] = Sim is null ? Ctx.Seated(i) : Sim.P[i].Plays && !Sim.P[i].Out;
        return a;
    }

    protected int[] Places()
    {
        var a = new int[Seats];
        if (Sim is not null)
            for (var i = 0; i < Seats; i++) a[i] = Sim.P[i].Plays ? Sim.P[i].Place : 0;
        return a;
    }

    protected int S => Sim?.S ?? 0;
    protected int Seed => Sim is null ? 0 : _seed;
    protected int Round => _round;
    protected int Rounds => Sim is null ? (N0Lobby() <= 1 ? 1 : _optRounds) : _rounds;
    protected int[] Points => (int[])_points.Clone();
    protected int[][] RoundPoints => [.. _roundPoints.Select(r => (int[])r.Clone())];
    protected object? Result => _result;

    int N0Lobby()
    {
        var n = 0;
        for (var i = 0; i < Seats; i++) if (Ctx.Seated(i)) n++;
        return n;
    }

    /// <summary>Пінги, що чекають відлуння; кадр забирає їх один раз.</summary>
    protected int?[]? TakePings()
    {
        if (!_pinged) return null;
        var a = (int?[])_ping.Clone();
        Array.Clear(_ping);
        _pinged = false;
        return a;
    }

    /// <summary>Події з минулого тика для спалахів; кадр забирає їх один раз.</summary>
    protected object[][]? TakeEvents()
    {
        var sim = Sim;
        if (sim is null || sim.EventCount == 0) return null;
        var list = new object[sim.EventCount][];
        for (var i = 0; i < sim.EventCount; i++) list[i] = EventWire(sim.Event(i));
        sim.ClearEvents();
        _evSeen = 0;
        return list;
    }

    public static object[] EventWire(in RunnerEvent e) => e.Kind switch
    {
        RunnerEvent.Hit => ["hit", e.Seat, e.A],
        RunnerEvent.Pit => ["pit", e.Seat, e.A],
        RunnerEvent.Egg => ["egg", e.Seat, e.A],
        RunnerEvent.Pepper => ["pepper", e.Seat, e.A],
        RunnerEvent.SnowTake => ["snow", e.Seat, e.A],
        RunnerEvent.Throw => ["throw", e.Seat, e.A, e.B],
        _ => ["out", e.Seat, e.A],
    };
}
