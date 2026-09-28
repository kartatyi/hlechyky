namespace Hlechyky.Games.Impl;

/// <summary>
/// Лелеки: flappy-перегони над селом на рушії <see cref="RunnerSim"/>. Усі летять однією колонкою над тим
/// самим селом, тап — змах; перший зачеп прощає запасне пір'я, другий — на землю. Хто останній у небі —
/// бере раунд (docs/games/specs/storks.md).
/// </summary>
public sealed class Storks : RunnerParty
{
    /// <summary>«Чисте небо»: хвилина польоту без жодного зачепу (від старту раунду чи від останнього зачепу).</summary>
    public const int CleanRun = 3000;
    static readonly string[] SeatNames = ["зелена", "жовта", "руда", "сіра", "синя", "рожева", "фіалкова", "червона"];

    public override GameInfo Info { get; } = new(
        "storks", "Лелеки", "лелек", GameGroup.Live, 1, RunnerSim.Seats,
        TickMs: RunnerSim.TickMs, Start: StartMode.ByHost,
        Options:
        [
            new GameOption("rounds", "Раундів", [("1", "1 раунд"), ("3", "3 раунди"), ("5", "5 раундів")], "3"),
            new GameOption("feather", "Запасне пір'я", [("on", "Є — перший зачеп прощається"), ("off", "Нема — один зачеп і вибув")], "on"),
            new GameOption("key", "Ключ лелек", [("off", "Кожна сама по собі"), ("on", "🪽 Поруч з іншою — падаєш повільніше")], "off"),
        ],
        Hint: "Лелеки летять над селом: тап — змах крил. Комини, дроти, гнізда, повітряні змії — однакові для всіх. Хто останній у небі — бере раунд",
        Client: "runner");

    protected override RunnerMode Mode => RunnerMode.Storks;
    protected override string[] Names => SeatNames;
    protected override string ExtraKey => "feather";

    /// <summary>Крок бігу останнього зачепу кожного місця в цьому раунді (−1 — зачепів не було).</summary>
    readonly int[] _lastHit = new int[RunnerSim.Seats];

    bool _key;
    /// <summary>Скільки кроків кожна летіла ключем цього раунду — «🪽 Найвірніша пара» в таблиці.</summary>
    readonly int[] _keySteps = new int[RunnerSim.Seats];

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        base.Configure(options);
        _key = options.TryGetValue("key", out var v) && v == "on";
    }

    protected override void OnRoundStart()
    {
        Array.Fill(_lastHit, -1);
        Array.Clear(_keySteps);
        Sim!.KeyOn = _key && N0 > 1;
    }

    /// <summary>Хто найдовше летів ключем: [місце, секунд]; null — ніхто.</summary>
    int[]? KeyBest()
    {
        var best = -1;
        for (var i = 0; i < RunnerSim.Seats; i++) if (_keySteps[i] > 0 && (best < 0 || _keySteps[i] > _keySteps[best])) best = i;
        return best < 0 ? null : [best, _keySteps[best] * RunnerSim.StepMs / 1000];
    }

    /// <summary>Зачеп, прощений пір'ям: з нього «хвилина без зачепу» починається знову.</summary>
    protected override void OnEvent(in RunnerEvent e)
    {
        // Перемотування могло скасувати пізніший зачеп і дати раніший — беремо пізніший: ачівка хіба трохи
        // забариться, але не прийде раніше, ніж треба.
        if (e.Kind == RunnerEvent.Hit && e.Seat is >= 0 and < RunnerSim.Seats && e.A > _lastHit[e.Seat]) _lastHit[e.Seat] = e.A;
    }

    /// <summary>
    /// «Чисте небо» — хвилина польоту без жодного зачепу: від старту раунду або від останнього зачепу, прощеного
    /// пір'ям. Даємо, коли вікно перемотування (15 кроків) минуло і запізнілий ввід цього вже не переграє.
    /// </summary>
    protected override void OnStepDone(int run)
    {
        Clean(run, RunnerSim.RewindMax);
        var sim = Sim!;
        if (!sim.KeyOn) return;
        var t = sim.S - 1;
        for (var i = 0; i < RunnerSim.Seats; i++) if (sim.InKey(i, t) && !sim.P[i].Out && !sim.P[i].Down) _keySteps[i]++;
    }

    /// <summary>Кінець раунду: перемотувань більше не буде — запас на вікно не потрібен.</summary>
    protected override void OnRoundEnd(int run) => Clean(run, 0);

    void Clean(int run, int margin)
    {
        if (run < CleanRun + margin) return;
        var sim = Sim!;
        for (var i = 0; i < RunnerSim.Seats; i++)
        {
            var p = sim.P[i];
            if (Awarded[i] || !p.Plays || p.Out || p.Down) continue;
            if (p.Hits > 0 && (_lastHit[i] < 0 || run - _lastHit[i] < CleanRun + margin)) continue;
            Awarded[i] = true;
            Ctx.Award(i, 0, "ach:storks-clean");
        }
    }

    public override object View(int? seat) => new
    {
        mode = "storks",
        turn = (int?)null,
        w = 800, h = 300, sub = RunnerSim.Sub, stepMs = RunnerSim.StepMs, stepsPerTick = RunnerSim.StepsPerTick,
        readySteps = ReadySteps, cap = RoundCap, pmCap = PmCap,
        rounds = Rounds, round = Round, n0 = Sim is null ? 0 : N0,
        featherOpt = Extra,
        keyOpt = _key,
        keyBest = _key && Phase is Over or Done ? KeyBest() : null,
        ph = Phase, s = S, seed = Seed,
        plays = Plays(), alive = Alive(), place = Places(),
        points = Points, roundPoints = RoundPoints,
        p = Players(),
        result = Result,
    };

    public override object? Frame() => new RunnerFrame
    {
        S = S, Ph = Phase, P = Players(), Pg = TakePings(), Ev = TakeEvents(), Ky = Sim?.WireKey(4),
    };
}
