using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

public sealed class FlakyMovesStakes(IStakes inner) : IStakes
{
    public int Fails = 1;
    public int Balance(string nick) => inner.Balance(nick);
    public bool TrySpend(string nick, int amount, string reason, string refKey) => inner.TrySpend(nick, amount, reason, refKey);
    public void Grant(string nick, int amount, string reason, string refKey) => inner.Grant(nick, amount, reason, refKey);
    public IReadOnlyList<LedgerMove>? Moves(string refPrefix)
    {
        if (Fails-- > 0) throw new InvalidOperationException("db busy");
        return inner.Moves(refPrefix);
    }
}

/// Рецензія 09.10: розрахунок упав (леджер зайнятий) — сирота платить за підсумком раунду, а не «ставку назад» тому, хто пролетів.
public class LelkaSettleRecordTests
{
    [Fact]
    public void Failed_settle_then_sweep_pays_by_the_recorded_outcome()
    {
        FlakyMovesStakes? flaky = null;
        var k = new LelkaKit(s => flaky = new FlakyMovesStakes(s), null, null, ("Оля", 1000), ("Петро", 1000));
        k.Rig(300);
        Assert.True(k.Bet(0, 100).Ok);
        Assert.True(k.Bet(1, 100).Ok);
        k.To(Lelka.Flight);
        k.FlyTo(200);
        Assert.True(k.H.Act(0, "cash", new { }).Ok);
        k.To(Lelka.Crashed);
        Assert.Single(k.Book.Pending());   // розрахунок упав — запис лишився
        var before = k.Stakes.Balance("Петро");
        k.Book.Recover(DateTimeOffset.MaxValue);
        Assert.Empty(k.Book.Pending());
        Assert.Equal(before, k.Stakes.Balance("Петро"));   // Петро програв на ×3 — нічого не має отримати
    }
}
