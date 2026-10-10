namespace Hlechyky.Games.Impl;

/// <summary>
/// «Сліди на полиці»: полиця 6×6, кластери від 5, каскади, сліди від глини на полиці множать виграш (×2 … ×128, сума під
/// кластером), горно → 10 вільних обертів, де сліди не стираються. Можна купити бонус (вимикач <c>Slots:BuyBonus</c>).
/// Математика — <see cref="SlotSlidyMath"/>, гроші/Скарбничка/вид — у базі <see cref="SlotGame"/>. Spec — docs/games/specs/slot-slidy.md.
/// </summary>
public sealed class SlotSlidy : SlotGame
{
    static readonly SlotSlidyMath MathImpl = new();

    public override GameInfo Info { get; } = new(
        "slot-slidy", "Сліди на полиці", "Слідів на полиці", GameGroup.Solo, 1, 1, Start: StartMode.Immediate,
        Private: true, Persistent: true, Score: ScoreOrder.HigherIsBetter,
        Hint: "Полиця 6×6: п'ять однакових поруч б'ються, а на полиці лишається слід — удруге там же ×2, далі ×4 … ×128; горно дає вільні оберти", Client: "slot");

    protected override ISlotMath SlotMath => MathImpl;
}
