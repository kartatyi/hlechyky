namespace Hlechyky.Games.Impl;

/// <summary>
/// «Козацький скарб» (slot-hold): 5×3, 20 ліній, дукати й «Утримуй і вигравай» (див. <see cref="SlotHoldMath"/>,
/// docs/games/specs/slots.md, розділ «Козацький скарб»). Гроші, Скарбничка, вид і таблиці — у базі <see cref="SlotGame"/>.
/// </summary>
public sealed class SlotHold : SlotGame
{
    static readonly SlotHoldMath MathImpl = new();

    public override GameInfo Info { get; } = new(
        "slot-hold", "Козацький скарб", "Козацького скарбу", GameGroup.Solo, 1, 1, Start: StartMode.Immediate,
        Private: true, Persistent: true, Score: ScoreOrder.HigherIsBetter,
        Hint: "5 барабанів, 20 ліній: шість дукатів — «Утримуй і вигравай», Булава, Пірнач і Гетьманський скарб", Client: "slot");

    protected override ISlotMath SlotMath => MathImpl;
}
