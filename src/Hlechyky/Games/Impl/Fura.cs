using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Чумаки-далекобійники», етап 0 — полігон: тягач із причепом на Rapier і калібрування керма (план —
/// D:\or-wt\_fura\PLAN.md). Фізика й керування цілком у браузері (web/games/fura/), сервер лише відкриває соло-стіл:
/// рейсів, черепків і колони ще нема. Суддя рейсів і ретранслятор поз прийдуть на етапах 1–2.
/// </summary>
public sealed class Fura : Game
{
    public override GameInfo Info { get; } = new(
        "fura", "Чумаки-далекобійники", "«Чумаків-далекобійників»", GameGroup.Solo, 1, 1,
        Start: StartMode.Immediate, Private: true,
        Hint: "Полігон: тягач із причепом, кермо G29, пад або клавіатура. Покатайся й зачепи причіп");

    /// <summary>Номер етапу — клієнт показує його на полігоні, щоб було видно, яка збірка відкрита.</summary>
    public const int Stage = 0;

    public override string SeatName(int seat) => "1";

    public override void Start() { }

    /// <summary>На полігоні ходів нема: будь-яка дія — відмова, стан не міняється.</summary>
    public override ActResult Act(int seat, string action, JsonElement payload) =>
        ActResult.Fail("На полігоні поки нема що надсилати");

    public override object View(int? seat) => new { stage = Stage };
}
