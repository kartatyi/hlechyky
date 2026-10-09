namespace Hlechyky.Games.Impl;

/// <summary>
/// «Розбиті глеки»: 6×5, платить 8+ однакових будь-де, каскади, писанки-множники, горно → вільні оберти з накопиченням
/// множника. Математика — <see cref="SlotCascadeMath"/>, гроші/Скарбничка/вид — у базі <see cref="SlotGame"/>.
/// </summary>
public sealed class SlotCascade : SlotGame
{
    static readonly SlotCascadeMath MathImpl = new();

    public override GameInfo Info { get; } = new(
        "slot-cascade", "Розбиті глеки", "Розбитих глеків", GameGroup.Solo, 1, 1, Start: StartMode.Immediate,
        Private: true, Persistent: true, Score: ScoreOrder.HigherIsBetter,
        Hint: "Полиця 6×5: вісім однакових будь-де б'ються, решта падає; писанки множать, горно дає вільні оберти", Client: "slot");

    protected override ISlotMath SlotMath => MathImpl;
}
