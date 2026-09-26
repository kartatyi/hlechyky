using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Стрибозаври: усі біжать одну кризу пліч-о-пліч від лавини. Спіткнувся — сповільнився, лавина ближче;
/// наздогнала — вибув. Останній на ногах бере раунд, партія з трьох (опція). Рух — у <see cref="RunnerSim"/>,
/// раунди й очки — у <see cref="RunnerParty"/>, тут — сніжки, яйця й ачівки (docs/games/specs/dino.md).
/// </summary>
public sealed class Dino : RunnerParty
{
    public const int FarMetres = 2000;
    static readonly string[] SeatNames = ["зелений", "жовтий", "рудий", "сірий", "синій", "рожевий", "фіалковий", "червоний"];

    public override GameInfo Info { get; } = new(
        "dino", "Стрибозаври", "стрибозаврів", GameGroup.Live, 1, RunnerSim.Seats,
        TickMs: RunnerSim.TickMs, Start: StartMode.ByHost,
        Options:
        [
            new GameOption("rounds", "Раундів", [("1", "1 раунд"), ("3", "3 раунди"), ("5", "5 раундів")], "3"),
            new GameOption("snow", "Сніжки", [("on", "Є — кидай у лідера"), ("off", "Без сніжок")], "on"),
        ],
        Hint: "Динозаври біжать від лавини по одній кризі: стрибай через брили, пригинайся під бурульками, кидай сніжки. Хто останній на ногах — бере раунд",
        Client: "runner");

    protected override RunnerMode Mode => RunnerMode.Dino;
    protected override string[] Names => SeatNames;
    protected override string ExtraKey => "snow";

    /// <summary>Влучання сніжкою чекає, поки вийде вікно перемотування: запізнілий стрибок ще може його скасувати.</summary>
    readonly List<(int Seat, int Id, int By, int At)> _pending = [];

    protected override void OnRoundStart() => _pending.Clear();

    protected override int Rank(RunnerPlayer p) => p.Lag;

    protected override int Bonus(RunnerPlayer p) => p.Eggs;

    /// <summary>Скільки метрів пройшла лінія темпу після кроку бігу run.</summary>
    public static int Metres(RunnerSim sim, int run) => sim.PaceX(Math.Max(0, run + 1)) / RunnerDino.SubPerMetre;

    protected override void OnOut(int seat, RunnerPlayer p, int run) => Far(seat, run);

    protected override void OnSurvive(int seat, RunnerPlayer p, int run) => Far(seat, run);

    void Far(int seat, int run)
    {
        if (Awarded[seat] || Metres(Sim!, run) < FarMetres) return;
        Awarded[seat] = true;
        Ctx.Award(seat, 0, "ach:dino-far");
    }

    protected override void OnEvent(in RunnerEvent e)
    {
        if (e.Kind != RunnerEvent.Hit || e.B < RunnerSim.SnowIdBase) return;
        var sim = Sim!;
        for (var i = 0; i < sim.SnowCount; i++)
        {
            ref readonly var o = ref sim.SnowBlock(i);
            if (o.Id != e.B) continue;
            if (o.By != e.Seat) _pending.Add((e.Seat, o.Id, o.By, sim.S));
            return;
        }
    }

    protected override void OnStepDone(int run)
    {
        if (_pending.Count == 0) return;
        var sim = Sim!;
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var (seat, id, by, at) = _pending[i];
            if (sim.S - at <= RunnerSim.RewindMax) continue;
            _pending.RemoveAt(i);
            if (sim.P[seat].HasPassed(id)) Ctx.Award(by, 0, "ach:dino-snow");
        }
    }

    /// <summary>Кинути сніжку: брила лягає за 320 px перед тим, хто найменше відстав (dino.md §2.6).</summary>
    protected override ActResult Other(int seat, string action, JsonElement payload)
    {
        if (action != "throw") return ActResult.Fail("Тут так не ходять");
        var sim = Sim!;
        if (Phase != Running) return ActResult.Fail("Зачекай старту");
        var me = sim.P[seat];
        if (!me.Plays || me.Out) return ActResult.Fail("Ти вже вибув — сніжки лишились у снігу");
        if (me.Snow == 0) return ActResult.Fail("Сніжки в тебе нема — підбери на трасі");
        var target = -1;
        for (var i = 0; i < RunnerSim.Seats; i++)
        {
            var q = sim.P[i];
            if (i == seat || !q.Plays || q.Out) continue;
            if (target < 0 || q.Lag < sim.P[target].Lag) target = i;
        }
        if (target < 0) return ActResult.Fail("Кидати нема в кого");
        var run = sim.Run;
        var x = sim.PaceX(run) - sim.P[target].Lag + RunnerDino.SnowAhead;
        sim.PlaceSnow(x, seat, run);
        sim.ConsumeSnow(seat);
        Ctx.Say($"❄ {Ctx.NickOf(seat)} кидає сніжку під ноги — {Ctx.NickOf(target)}, стрибай!");
        return ActResult.Done;
    }

    public override object View(int? seat)
    {
        var sim = Sim;
        var run = sim is null ? 0 : sim.Run;
        var eggs = new int[RunnerSim.Seats];
        if (sim is not null)
            for (var i = 0; i < RunnerSim.Seats; i++) eggs[i] = sim.P[i].Plays ? sim.P[i].Eggs : 0;
        return new
        {
            mode = "dino",
            turn = (int?)null,
            w = 800, h = 300, ground = 240, sub = RunnerSim.Sub, stepMs = RunnerSim.StepMs, stepsPerTick = RunnerSim.StepsPerTick,
            readySteps = ReadySteps, cap = RoundCap, pmCap = PmCap,
            rounds = Rounds, round = Round, n0 = sim is null ? 0 : N0,
            ph = Phase, s = S, seed = Seed,
            snowOpt = sim?.SnowOn ?? Extra,
            plays = Plays(), alive = Alive(), place = Places(), eggs,
            points = Points, roundPoints = RoundPoints,
            snow = sim?.WireSnow() ?? [],
            p = Players(),
            d = RunnerRules.AvD(run),
            m = sim is null ? 0 : sim.PaceX(run) / RunnerDino.SubPerMetre,
            result = Result,
        };
    }

    public override object? Frame()
    {
        var sim = Sim;
        var run = sim is null ? 0 : sim.Run;
        int[][]? sn = sim is not null && sim.SnowCount > 0 ? sim.WireSnow() : null;
        return new RunnerFrame
        {
            S = S, Ph = Phase, D = RunnerRules.AvD(run), P = Players(), Sn = sn, Pg = TakePings(), Ev = TakeEvents(),
        };
    }
}

/// <summary>
/// Забіг дня: соло на тій самій кризі, одна траса на день для всіх (зерно дня за Києвом), лавина лише тисне
/// ззаду, рахунок — метри. Кожна спроба — окрема партія каркаса (Finish → «Ще раз»), тож між спробами
/// кімната не тикає зовсім (docs/games/specs/dino-daily.md).
/// </summary>
public sealed class DinoDaily : Game
{
    public const int PmCapDaily = 1250, RewardFrom = 500, LogFrom = 1000;
    public const string Wait = "wait", Running = "run", Done = "done";
    static readonly TimeSpan LogEvery = TimeSpan.FromSeconds(60);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override GameInfo Info { get; } = new(
        "dino-daily", "Забіг дня", "забіг дня", GameGroup.Solo, 1, 1,
        TickMs: RunnerSim.TickMs, Start: StartMode.Immediate,
        Private: true, Persistent: true, Score: ScoreOrder.HigherIsBetter,
        Hint: "Та сама криза для всіх на цілий день. Скільки метрів пробіжиш від лавини? Спроб скільки завгодно, у таблицю йде найкраща",
        Client: "runner");

    string? _day;
    int _seed;
    RunnerSim? _sim;
    string _phase = Wait;
    bool _fresh;
    int _best, _runs, _eggs;
    bool _paid;
    DateTimeOffset? _loggedAt;
    object? _last;
    int? _ping;
    RunnerPacer _pacer;

    /// <summary>Світ поточної спроби (тести й діагностика).</summary>
    public RunnerSim? World => _sim;
    public string PhaseName => _phase;

    public override string SeatName(int seat) => "стрибозавр";

    /// <summary>З днем, але без «daily:» — інакше метри лягли б у щоденний глек як «спроби» (dino-daily.md §9.1).</summary>
    public override string SoloKey(string nickKey, IClock clock) => $"dino-daily:{Days.Today(clock)}:{nickKey}";

    public override void Start()
    {
        _day ??= Days.Today(Ctx.Clock);
        _seed = Days.Seed("dino-daily", _day);
        _sim = new RunnerSim(RunnerMode.Dino, _seed, [true], 0, PmCapDaily, snowOn: false);
        _phase = Wait;
        _fresh = false;
        _ping = null;
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (_sim is null || seat != 0) return ActResult.Fail("Тут так не ходять");
        switch (action)
        {
            case "in":
                if (!RunnerParty.TryIn(payload, out var s, out var k)) return ActResult.Fail("Тут так не ходять");
                if (_phase == Wait)
                {
                    // Перший стрибок і є старт — як у хромівського динозаврика.
                    if ((k & 4) == 0) return ActResult.Fail("Стрибни, щоб побігти");
                    _phase = Running;
                    _fresh = true;
                    _pacer.Reset();
                    _sim.Input(0, 0, k);
                    return ActResult.Done;
                }
                if (_phase != Running) return ActResult.Fail("Забіг скінчився — тисни «Ще раз»");
                return _sim.Input(0, s, k) ? ActResult.Done : ActResult.Fail("Запізно");
            case "ping":
                if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("t", out var t)
                    || t.ValueKind != JsonValueKind.Number || !t.TryGetInt32(out var tv)) return ActResult.Fail("Тут так не ходять");
                _ping = tv & 0xFFFFF;
                return ActResult.Done;
            default:
                return ActResult.Fail("Тут так не ходять");
        }
    }

    public override TickResult Tick()
    {
        if (_sim is null || _phase != Running) return TickResult.None;
        var view = _fresh;
        _fresh = false;
        var steps = _pacer.Due(Ctx.Clock.UtcNow);
        for (var i = 0; i < steps; i++)
        {
            _sim.Step();
            if (!_sim.P[0].Out) continue;
            Caught();
            return TickResult.Both;
        }
        return view ? TickResult.Both : TickResult.FrameOnly;
    }

    /// <summary>Лавина наздогнала: метри в таблицю, нагорода дня від 500 м, рядок у Журнал за новий рекорд від 1000 м.</summary>
    void Caught()
    {
        var sim = _sim!;
        var p = sim.P[0];
        var metres = Dino.Metres(sim, sim.S - 1);
        var before = _best;
        _runs++;
        _eggs += p.Eggs;
        if (metres > _best) _best = metres;
        Ctx.Score(0, metres);
        if (metres >= RewardFrom && !_paid)
        {
            _paid = true;
            Ctx.Award(0, 0, "daily:dino-daily");
        }
        if (metres >= Dino.FarMetres) Ctx.Award(0, 0, "ach:dino-far");
        var log = "";
        var now = Ctx.Clock.UtcNow;
        if (metres > before && metres >= LogFrom && (_loggedAt is null || now - _loggedAt.Value >= LogEvery))
        {
            log = $"Забіг дня: {Ctx.NickOf(0)} — {metres} м, найкращий сьогодні";
            _loggedAt = now;
        }
        _last = new { m = metres, eggs = p.Eggs, record = metres > before };
        _phase = Done;
        Ctx.Finish([0], log);
    }

    public override object View(int? seat)
    {
        var sim = _sim;
        var run = sim is null ? 0 : sim.S;
        var day = _day ?? Days.Today(Ctx.Clock);
        return new
        {
            mode = "dino",
            daily = true,
            turn = (int?)null,
            w = 800, h = 300, ground = 240, sub = RunnerSim.Sub, stepMs = RunnerSim.StepMs, stepsPerTick = RunnerSim.StepsPerTick,
            readySteps = 0, cap = 0, pmCap = PmCapDaily, rounds = 1, round = 1, n0 = 1, snowOpt = false,
            day, no = Days.Number(day), seed = _seed,
            ph = _phase, s = run,
            plays = new[] { true, false, false, false, false, false, false, false },
            alive = new[] { sim is null || !sim.P[0].Out, false, false, false, false, false, false, false },
            p = sim?.WirePlayers() ?? new int[]?[RunnerSim.Seats],
            d = RunnerRules.AvD(run),
            m = Metres(run),
            best = _best, runs = _runs, eggsTotal = _eggs,
            last = _last,
        };
    }

    int Metres(int run) => _sim is null || run <= 0 ? 0 : _sim.PaceX(run) / RunnerDino.SubPerMetre;

    public override object? Frame()
    {
        var sim = _sim;
        var run = sim?.S ?? 0;
        object[][]? ev = null;
        if (sim is not null && sim.EventCount > 0)
        {
            ev = new object[sim.EventCount][];
            for (var i = 0; i < sim.EventCount; i++) ev[i] = RunnerParty.EventWire(sim.Event(i));
            sim.ClearEvents();
        }
        int?[]? pg = null;
        if (_ping is { } t)
        {
            pg = new int?[RunnerSim.Seats];
            pg[0] = t;
            _ping = null;
        }
        return new RunnerFrame
        {
            S = run, Ph = _phase, D = RunnerRules.AvD(run), M = Metres(run),
            P = sim?.WirePlayers() ?? new int[]?[RunnerSim.Seats], Pg = pg, Ev = ev,
        };
    }

    // ---------- збереження ----------

    sealed record SaveState(string Day, int Best, int Runs, int Eggs, bool Paid, DateTimeOffset? LoggedAt);

    public override string? Save() =>
        JsonSerializer.Serialize(new SaveState(_day ?? Days.Today(Ctx.Clock), _best, _runs, _eggs, _paid, _loggedAt), Json);

    public override void Load(string json)
    {
        var s = JsonSerializer.Deserialize<SaveState>(json, Json);
        if (s is null || s.Day != (_day ?? Days.Today(Ctx.Clock))) return;
        _best = s.Best;
        _runs = s.Runs;
        _eggs = s.Eggs;
        _paid = s.Paid;
        _loggedAt = s.LoggedAt;
    }
}
