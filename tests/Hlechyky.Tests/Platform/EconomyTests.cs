using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>Черепки: ідемпотентність, атомарність, стелі, перерахунок, спільний гаманець на нік у будь-якому регістрі.</summary>
public class EconomyTests
{
    [Fact]
    public void Grant_with_same_ref_applies_once()
    {
        using var rig = new EconomyRig();
        Assert.Equal(GrantResult.Applied, rig.Economy.Grant("Оля", 5, "win:chess", "game:r1:1:оля"));
        Assert.Equal(GrantResult.Duplicate, rig.Economy.Grant("Оля", 5, "win:chess", "game:r1:1:оля"));
        Assert.Equal(5, rig.Economy.Balance("Оля"));
    }

    [Fact]
    public void Grant_without_ref_stacks()
    {
        using var rig = new EconomyRig();
        rig.Economy.Grant("Оля", 3, "listen");
        rig.Economy.Grant("Оля", 3, "listen");
        Assert.Equal(6, rig.Economy.Balance("Оля"));
    }

    [Fact]
    public void Nick_key_ignores_case_and_spaces()
    {
        using var rig = new EconomyRig();
        rig.Economy.Grant("Оля", 7, "listen");
        Assert.Equal(7, rig.Economy.Balance("  оля "));
        Assert.True(rig.Economy.TrySpend("ОЛЯ", 7, "stake"));
        Assert.Equal(0, rig.Economy.Balance("Оля"));
    }

    [Fact]
    public void Spend_fails_when_short_and_leaves_balance_alone()
    {
        using var rig = new EconomyRig();
        rig.Economy.Grant("Петро", 4, "listen");
        Assert.False(rig.Economy.TrySpend("Петро", 5, "stake", "stake:r1:1:петро"));
        Assert.Equal(4, rig.Economy.Balance("Петро"));
        // ref не «згорів»: після поповнення та сама спроба проходить
        rig.Economy.Grant("Петро", 1, "listen");
        Assert.True(rig.Economy.TrySpend("Петро", 5, "stake", "stake:r1:1:петро"));
        Assert.Equal(0, rig.Economy.Balance("Петро"));
    }

    [Fact]
    public void Spend_with_same_ref_takes_money_once()
    {
        using var rig = new EconomyRig();
        rig.Economy.Grant("Оля", 20, "listen");
        Assert.True(rig.Economy.TrySpend("Оля", 5, "stake", "stake:r1:1:оля"));
        Assert.True(rig.Economy.TrySpend("Оля", 5, "stake", "stake:r1:1:оля"));
        Assert.Equal(15, rig.Economy.Balance("Оля"));
    }

    [Fact]
    public void Twenty_parallel_spends_of_one_at_balance_ten_succeed_exactly_ten_times()
    {
        using var rig = new EconomyRig();
        rig.Economy.Grant("Оля", 10, "listen");

        var ok = 0;
        Parallel.For(0, 20, _ =>
        {
            if (rig.Economy.TrySpend("Оля", 1, "clicker")) Interlocked.Increment(ref ok);
        });

        Assert.Equal(10, ok);
        Assert.Equal(0, rig.Economy.Balance("Оля"));
    }

    [Fact]
    public void Cap_stops_grants_past_the_daily_limit()
    {
        using var rig = new EconomyRig();
        for (var i = 0; i < 12; i++)
            Assert.Equal(GrantResult.Applied, rig.Economy.GrantCapped("Оля", 1, "listen", $"listen:оля:d:{i}", "listen", 12));
        Assert.Equal(GrantResult.Capped, rig.Economy.GrantCapped("Оля", 1, "listen", "listen:оля:d:12", "listen", 12));
        Assert.Equal(12, rig.Economy.Balance("Оля"));
    }

    [Fact]
    public void Cap_counts_shards_when_units_are_shards()
    {
        using var rig = new EconomyRig();
        Assert.Equal(GrantResult.Applied, rig.Economy.GrantCapped("Оля", 25, "award:skilky", "a:1", "award", 30, 25));
        Assert.Equal(GrantResult.Capped, rig.Economy.GrantCapped("Оля", 10, "award:skilky", "a:2", "award", 30, 10));
        Assert.Equal(GrantResult.Applied, rig.Economy.GrantCapped("Оля", 5, "award:skilky", "a:3", "award", 30, 5));
        Assert.Equal(30, rig.Economy.Balance("Оля"));
    }

    [Fact]
    public void Cap_resets_with_the_kyiv_day()
    {
        using var rig = new EconomyRig();
        rig.Economy.GrantSequenced("Оля", 1, "listen", n => $"listen:оля:{Days.Today(rig.Clock)}:{n}", "listen", 1);
        Assert.Equal(GrantResult.Capped,
            rig.Economy.GrantSequenced("Оля", 1, "listen", n => $"listen:оля:{Days.Today(rig.Clock)}:{n}", "listen", 1));

        rig.Clock.Advance(TimeSpan.FromHours(9));   // 21:00 UTC — уже завтра за Києвом
        Assert.Equal(GrantResult.Applied,
            rig.Economy.GrantSequenced("Оля", 1, "listen", n => $"listen:оля:{Days.Today(rig.Clock)}:{n}", "listen", 1));
        Assert.Equal(2, rig.Economy.Balance("Оля"));
    }

    [Fact]
    public void Rebuild_restores_wallets_from_the_ledger()
    {
        using var rig = new EconomyRig();
        rig.Economy.Grant("Оля", 10, "listen");
        rig.Economy.TrySpend("Оля", 4, "stake");
        rig.Db.Exec("UPDATE wallets SET balance = 999, earned = 0, spent = 0 WHERE nick_key = 'оля'");
        Assert.Equal(999, rig.Economy.Balance("Оля"));

        rig.Economy.Rebuild();

        var w = rig.Economy.Wallet("Оля");
        Assert.Equal(6, w.Balance);
        Assert.Equal(10, w.Earned);
        Assert.Equal(4, w.Spent);
    }

    [Fact]
    public void Every_move_of_money_reaches_the_outbox_with_human_text()
    {
        using var rig = new EconomyRig();
        rig.Names.Learn(EconomyRig.Info("chess", "Шахи", "шахи"));

        rig.Economy.Grant("Оля", 5, "win:chess");
        rig.Economy.Grant("Оля", 10, "listen");
        rig.Economy.TrySpend("Оля", 10, "stake");

        var wallet = rig.Outbox.Of<WalletChanged>();
        Assert.Equal(3, wallet.Count);
        Assert.Equal("+5 черепків: перемога — Шахи", wallet[0].Text);
        Assert.Equal(5, wallet[0].Balance);
        Assert.Equal("−10 черепків: ставка", wallet[2].Text);
        Assert.Equal(5, wallet[2].Balance);
        Assert.Equal(-10, wallet[2].Delta);
    }

    [Fact]
    public void Duplicate_and_capped_grants_stay_silent()
    {
        using var rig = new EconomyRig();
        rig.Economy.Grant("Оля", 5, "listen", "r1");
        rig.Outbox.Clear();
        rig.Economy.Grant("Оля", 5, "listen", "r1");
        rig.Economy.GrantCapped("Оля", 5, "listen", "r2", "listen", 0);
        Assert.Empty(rig.Outbox.Of<WalletChanged>());
    }

    [Fact]
    public void Shard_plural_matches_ukrainian()
    {
        Assert.Equal("черепок", Economy.Shards(1));
        Assert.Equal("черепки", Economy.Shards(3));
        Assert.Equal("черепків", Economy.Shards(5));
        Assert.Equal("черепків", Economy.Shards(-12));
        Assert.Equal("черепок", Economy.Shards(21));
    }

    [Fact]
    public void Top_lists_the_richest_first()
    {
        using var rig = new EconomyRig();
        rig.Economy.Grant("Оля", 30, "listen");
        rig.Economy.Grant("Петро", 10, "listen");
        var top = rig.Economy.Top(5);
        Assert.Equal("Оля", top[0].Nick);
        Assert.Equal(30, top[0].Balance);
        Assert.Equal("Петро", top[1].Nick);
    }

    [Fact]
    public void Stakes_interface_is_the_same_economy()
    {
        using var rig = new EconomyRig();
        IStakes stakes = rig.Economy;
        stakes.Grant("Оля", 10, "stake-refund", "stake-refund:r1:1:оля");
        stakes.Grant("Оля", 10, "stake-refund", "stake-refund:r1:1:оля");
        Assert.Equal(10, stakes.Balance("Оля"));
        Assert.True(stakes.TrySpend("Оля", 10, "stake", "stake:r1:2:оля"));
        Assert.False(stakes.TrySpend("Оля", 1, "stake", "stake:r1:3:оля"));
    }
}
