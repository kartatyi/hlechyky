namespace Hlechyky.Games.Impl;

/// <summary>
/// Лелеки: flappy-перегони над селом на рушії <see cref="RunnerSim"/>. Усі летять однією колонкою над тим
/// самим селом, тап — змах; перший зачеп прощає запасне пір'я, другий — на землю. Хто останній у небі —
/// бере раунд (docs/games/specs/storks.md).
/// </summary>
public sealed class Storks : RunnerParty
{
    /// <summary>«Чисте небо»: хвилина польоту без жодного зачепу.</summary>
    public const int CleanRun = 3000;
    static readonly string[] SeatNames = ["зелена", "жовта", "руда", "сіра", "синя", "рожева", "фіалкова", "червона"];

    public override GameInfo Info { get; } = new(
        "storks", "Лелеки", "лелек", GameGroup.Live, 1, RunnerSim.Seats,
        TickMs: RunnerSim.TickMs, Start: StartMode.ByHost,
        Options:
        [
            new GameOption("rounds", "Раундів", [("1", "1 раунд"), ("3", "3 раунди"), ("5", "5 раундів")], "3"),
            new GameOption("feather", "Запасне пір'я", [("on", "Є — перший зачеп прощається"), ("off", "Нема — один зачеп і вибув")], "on"),
        ],
        Hint: "Лелеки летять над селом: тап — змах крил. Комини, дроти, гнізда, повітряні змії — однакові для всіх. Хто останній у небі — бере раунд",
        Client: "runner");

    protected override RunnerMode Mode => RunnerMode.Storks;
    protected override string[] Names => SeatNames;
    protected override string ExtraKey => "feather";

    /// <summary>
    /// Ачівку даємо, коли хвилина без зачепу вже не може стати «з зачепом»: вікно перемотування (15 кроків)
    /// минуло, і запізнілий ввід цього не переграє.
    /// </summary>
    protected override void OnStepDone(int run)
    {
        if (run < CleanRun + RunnerSim.RewindMax) return;
        var sim = Sim!;
        for (var i = 0; i < RunnerSim.Seats; i++)
        {
            var p = sim.P[i];
            if (Awarded[i] || !p.Plays || p.Out || p.Down || p.Hits > 0) continue;
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
        ph = Phase, s = S, seed = Seed,
        plays = Plays(), alive = Alive(), place = Places(),
        points = Points, roundPoints = RoundPoints,
        p = Players(),
        result = Result,
    };

    public override object? Frame() => new RunnerFrame { S = S, Ph = Phase, P = Players(), Pg = TakePings(), Ev = TakeEvents() };
}
