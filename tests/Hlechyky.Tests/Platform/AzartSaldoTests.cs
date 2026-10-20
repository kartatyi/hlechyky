using Hlechyky.Games.Economy;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Сальдо азарту (пакет 5): поставив / повернув у слотах і Лелеці — сьогодні (київська доба) і за весь час. На старті
/// підсумовує леджер, далі дописує рухи з <see cref="Economy.Moved"/>; призи й подарунки не рахує.
/// </summary>
public class AzartSaldoTests
{
    /// <summary>Вівторок 20.10, 15:00 за Києвом.</summary>
    static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    static EconomyRig Rig()
    {
        var rig = new EconomyRig();
        rig.Clock.UtcNow = Now;
        return rig;
    }

    [Fact]
    public void Kind_comes_from_the_reason_head_and_only_bets_and_returns_count()
    {
        Assert.Equal("slot", AzartSaldo.KindOf("slot-bet:slot-hold"));
        Assert.Equal("slot", AzartSaldo.KindOf("slot-jackpot:slot-glek"));
        Assert.Equal("slot", AzartSaldo.KindOf("slot-buy:slot-slidy"));
        Assert.Equal("lelka", AzartSaldo.KindOf("lelka-back:lelka"));
        Assert.Null(AzartSaldo.KindOf("slot-prize:week"));
        Assert.Null(AzartSaldo.KindOf("win:chess"));
        Assert.Null(AzartSaldo.KindOf(""));
    }

    [Fact]
    public void Live_moves_add_up_for_today_and_all_time_and_the_day_rolls_over()
    {
        using var rig = Rig();
        var saldo = new AzartSaldo(rig.Db, rig.Economy, rig.Clock);
        saldo.StartAsync(default).Wait();
        rig.Economy.Grant("Оля", 1000, "award", "seed");

        Assert.True(rig.Economy.TrySpend("Оля", 100, "slot-bet:slot-hold", "s1:bet"));
        rig.Economy.Grant("Оля", 250, "slot-win:slot-hold", "s1:win");
        Assert.True(rig.Economy.TrySpend("Оля", 50, "slot-bet:slot-glek", "s2:bet"));
        Assert.True(rig.Economy.TrySpend("Оля", 20, "lelka-bet:lelka", "l1:bet"));

        Assert.Equal(new Saldo(150, 250, 150, 250), saldo.Get("slot", "Оля"));
        Assert.Equal(100, saldo.Get("slot", "оля").DayNet);   // нік — без різниці регістру
        Assert.Equal(new Saldo(20, 0, 20, 0), saldo.Get("lelka", "Оля"));
        Assert.Equal(default, saldo.Get("slot", "Петро"));

        rig.Clock.UtcNow = Now.AddDays(1);
        Assert.Equal(new Saldo(0, 0, 150, 250), saldo.Get("slot", "Оля"));
        Assert.True(rig.Economy.TrySpend("Оля", 10, "slot-bet:slot-hold", "s3:bet"));
        Assert.Equal(new Saldo(10, 0, 160, 250), saldo.Get("slot", "Оля"));
    }

    [Fact]
    public void Start_sums_the_ledger_with_the_kyiv_day_boundary()
    {
        using var rig = Rig();
        rig.Economy.Grant("Оля", 5000, "award", "seed");
        // 19.10 23:30 за Києвом — учора
        rig.Clock.UtcNow = new DateTimeOffset(2026, 10, 19, 20, 30, 0, TimeSpan.Zero);
        Assert.True(rig.Economy.TrySpend("Оля", 300, "slot-bet:slot-cluster", "a:bet"));
        rig.Economy.Grant("Оля", 100, "slot-jackpot:slot-cluster", "a:jp");
        // 20.10 00:30 за Києвом — сьогодні
        rig.Clock.UtcNow = new DateTimeOffset(2026, 10, 19, 21, 30, 0, TimeSpan.Zero);
        Assert.True(rig.Economy.TrySpend("Оля", 40, "slot-bet:slot-cluster", "b:bet"));
        rig.Economy.Grant("Оля", 70, "lelka-win:lelka", "c:win");
        rig.Economy.Grant("Оля", 999, "slot-prize:week", "prize");   // приз — не повернута ставка

        rig.Clock.UtcNow = Now;
        var saldo = new AzartSaldo(rig.Db, rig.Economy, rig.Clock);
        saldo.StartAsync(default).Wait();

        Assert.Equal(new Saldo(40, 0, 340, 100), saldo.Get("slot", "Оля"));
        Assert.Equal(new Saldo(0, 70, 0, 70), saldo.Get("lelka", "Оля"));
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 21, 0, 0, TimeSpan.Zero), AzartSaldo.DayStartUtc(Now));
    }
}
