using Hlechyky.Games.Impl;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Суддя рейсу «Чумаків» напряму, без кімнати (specs/fura.md, «Суддя», «Плата»): кожне правило — окремий тест на межі.
/// </summary>
public sealed class FuraJudgeTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    static FuraPose At(double x, double z = 0, int k = 100, bool c = true) => new(x, z, 0, c, k, x, z, 0);

    /// <summary>Суддя в haul, точка відліку — (0, 0) у мить T0.</summary>
    static FuraJudge Hauling()
    {
        var j = FuraJudge.Begin(T0, At(0), T0);
        j.StartHaul(At(0));
        return j;
    }

    /// <summary>Скільки метрів за 0,25 с дає швидкість у км/год.</summary>
    static double Quarter(double kmh) => kmh / 3.6 * 0.25;

    [Fact]
    public void Speed_over_160_is_a_violation_and_the_fourth_voids_the_trip()
    {
        var j = Hauling();
        var x = 0.0;
        var t = T0;
        for (var i = 1; i <= 3; i++)
        {
            x += Quarter(170);
            t = t.AddSeconds(0.25);
            j.Feed(At(x), t);
            Assert.Equal(i, j.Violations);
            Assert.Null(j.Void);
        }
        x += Quarter(170);
        j.Feed(At(x), t.AddSeconds(0.25));
        Assert.Equal(4, j.Violations);
        Assert.NotNull(j.Void);
    }

    [Fact]
    public void Speed_just_under_160_is_fine()
    {
        var j = Hauling();
        j.Feed(At(Quarter(159)), T0.AddSeconds(0.25));
        Assert.Equal(0, j.Violations);
    }

    [Fact]
    public void One_jump_over_250_voids_the_trip_at_once()
    {
        var j = Hauling();
        j.Feed(At(Quarter(260)), T0.AddSeconds(0.25));
        Assert.Contains("недійсний", j.Void);
        Assert.Equal(0, j.Violations);
    }

    [Fact]
    public void A_jump_up_to_three_metres_right_after_rescue_is_not_counted()
    {
        // 150 км/год — чесно, +3 м стрибка «на колеса» — уже 193 км/год без поблажки
        var d = Quarter(150) + 3;
        var plain = Hauling();
        plain.Feed(At(d), T0.AddSeconds(0.25));
        Assert.Equal(1, plain.Violations);

        var rescued = Hauling();
        rescued.Rescue = true;
        rescued.Feed(At(d), T0.AddSeconds(0.25));
        Assert.Equal(0, rescued.Violations);
        Assert.False(rescued.Rescue);   // поблажка — на один стрибок

        rescued.Feed(At(d + d), T0.AddSeconds(0.5));
        Assert.Equal(1, rescued.Violations);
    }

    [Fact]
    public void Snapshots_closer_than_015_s_wait_for_a_longer_gap()
    {
        var j = Hauling();
        // за 0,1 с стрибок на 20 м (720 км/год) — не міряємо, точка відліку стоїть
        j.Feed(At(20), T0.AddSeconds(0.1));
        Assert.Null(j.Void);
        Assert.Equal(0, j.HaulM);
        // а на 0,25 с від відліку ті самі 20 м — 288 км/год: телепорт
        j.Feed(At(20), T0.AddSeconds(0.25));
        Assert.NotNull(j.Void);
    }

    [Fact]
    public void A_pause_does_not_run_the_clock_and_the_truck_must_stay_within_30_m()
    {
        var j = Hauling();
        j.Feed(At(5), T0.AddSeconds(1));
        Assert.Equal(1, j.TripSec, 3);
        j.Feed(At(30), T0.AddSeconds(21));    // 20 с паузи, 25 м — стоїть де стояв
        Assert.Equal(1, j.TripSec, 3);
        Assert.Equal(1, j.HaulSec, 3);
        Assert.Null(j.Void);
        j.Feed(At(31), T0.AddSeconds(22));
        Assert.Equal(2, j.TripSec, 3);

        var far = Hauling();
        far.Feed(At(31), T0.AddSeconds(16));  // 16 с і 31 м
        Assert.Contains("після паузи", far.Void);
    }

    [Fact]
    public void Exactly_15_s_is_still_driving_not_a_pause()
    {
        var j = Hauling();
        j.Feed(At(300), T0.AddSeconds(15));   // 72 км/год
        Assert.Equal(15, j.TripSec, 3);
        Assert.Equal(300, j.HaulM, 3);
    }

    [Fact]
    public void Time_before_take_is_not_counted()
    {
        var j = FuraJudge.Begin(T0.AddSeconds(5), At(0, c: false), T0);
        j.Feed(At(10, c: false), T0.AddSeconds(6));   // проміжок 6 с, з них після take — 1
        Assert.Equal(1, j.TripSec, 3);
        Assert.Equal(0, j.HaulM);   // до зчеплення пройдене не рахується
    }

    [Fact]
    public void A_stale_last_pose_is_not_a_reference_after_take()
    {
        var j = FuraJudge.Begin(T0.AddMinutes(5), At(0, c: false), T0);
        Assert.Null(j.Ref);
        j.Feed(At(5000, c: false), T0.AddMinutes(5).AddSeconds(1));   // перший знімок — новий відлік, не телепорт
        Assert.Null(j.Void);
    }

    [Fact]
    public void Cargo_integrity_only_goes_down()
    {
        var j = Hauling();
        j.Feed(At(5, k: 80), T0.AddSeconds(1));
        j.Feed(At(10, k: 95), T0.AddSeconds(2));
        Assert.Equal(80, j.MinK);
        j.Feed(At(12, k: 60), T0.AddSeconds(2.05));   // навіть із короткого знімка
        Assert.Equal(60, j.MinK);
    }

    [Fact]
    public void Integrity_before_hitching_does_not_count()
    {
        var j = FuraJudge.Begin(T0, At(0, c: false), T0);
        j.Feed(At(5, k: 10, c: false), T0.AddSeconds(1));
        Assert.Equal(100, j.MinK);
    }

    [Fact]
    public void Verdict_needs_minimum_time_and_distance()
    {
        // 2 км: не швидше за 72 с і не менше 1,8 км
        var j = new FuraJudge { Hauling = true, HaulSec = 71, HaulM = 2000 };
        Assert.Contains("швидко", j.Verdict(2));
        j.HaulSec = 72;
        Assert.Null(j.Verdict(2));
        j.HaulM = 1799;
        Assert.Contains("проїхав менше", j.Verdict(2));
        j.HaulM = 1800;
        Assert.Null(j.Verdict(2));
        j.Void = "Рейс недійсний: тест";
        Assert.Equal("Рейс недійсний: тест", j.Verdict(2));
    }

    [Fact]
    public void Pay_formula_with_bonuses_and_bounds()
    {
        // (8 + 9·2) × 1,5 = 39; × 0,9 цілого = 35,1
        Assert.Equal(39, FuraJudge.Pay(2, 1.5, 100, false, false, false));
        Assert.Equal(35, FuraJudge.Pay(2, 1.5, 90, false, false, false));
        Assert.Equal(43, FuraJudge.Pay(2, 1.5, 100, true, false, false));    // 42,9
        Assert.Equal(47, FuraJudge.Pay(2, 1.5, 100, true, true, false));     // 47,19
        Assert.Equal(42, FuraJudge.Pay(2, 1.5, 100, true, true, true));      // 42,471
        Assert.Equal(35, FuraJudge.Pay(2, 1.5, 100, false, false, true));    // 35,1
        // межі 15…50
        Assert.Equal(15, FuraJudge.Pay(0.3, 1, 30, false, false, true));
        Assert.Equal(50, FuraJudge.Pay(5, 1.7, 100, true, true, false));
        // k < 20 — усе розбито
        Assert.Equal(0, FuraJudge.Pay(2, 1.5, 19, true, true, false));
        Assert.Equal(15, FuraJudge.Pay(2, 1.5, 20, false, false, false));
    }

    [Fact]
    public void Minutes_follow_the_formula()
    {
        Assert.Equal(5, FuraJudge.Minutes(1.5));   // ⌈2,7⌉ + 2
        Assert.Equal(11, FuraJudge.Minutes(5));    // рівно 9 — без зайвої хвилини від похибки
        Assert.Equal(6, FuraJudge.Minutes(2.2));   // ⌈3,96⌉ + 2
    }

    static readonly FuraPlace Yard = new("p", "Місце", "Місце", "n", new FuraYard(0, 0, 40, 20),
        [new FuraDock("1", -10, 5, Math.PI), new FuraDock("2", 10, 5, Math.PI)], false);

    static FuraPose Trailer(double rx, double rz, double ry = 0, double v = 0, bool c = false) => new(rx, rz + 8, v, c, 90, rx, rz, ry);

    [Fact]
    public void Delivery_needs_a_fresh_still_unhitched_trailer_in_the_yard()
    {
        var p = Trailer(0, 0);
        Assert.Null(FuraJudge.NotYet(p, T0, T0.AddSeconds(2), Yard));
        Assert.Contains("телеметрії", FuraJudge.NotYet(p, T0, T0.AddSeconds(2.1), Yard));
        Assert.Contains("телеметрії", FuraJudge.NotYet(null, T0, T0, Yard));
        Assert.Contains("зупинись", FuraJudge.NotYet(Trailer(0, 0, v: -0.5), T0, T0, Yard));
        Assert.Null(FuraJudge.NotYet(Trailer(0, 0, v: 0.49), T0, T0, Yard));
        Assert.Contains("відчепи", FuraJudge.NotYet(Trailer(0, 0, c: true), T0, T0, Yard));
        // двір 40×20 + 15 м: по X до 35, по Z до 25
        Assert.Null(FuraJudge.NotYet(Trailer(35, 0), T0, T0, Yard));
        Assert.Contains("не в дворі", FuraJudge.NotYet(Trailer(36, -10), T0, T0, Yard));
        Assert.Null(FuraJudge.NotYet(Trailer(0, -25), T0, T0, Yard));
        Assert.Contains("не в дворі", FuraJudge.NotYet(Trailer(0, -26), T0, T0, Yard));
        // або ≤ 25 м від боксу: (10, 5) → (10, 30) поза розширеним двором, але рівно 25 м від боксу 2
        Assert.Null(FuraJudge.NotYet(Trailer(10, 30), T0, T0, Yard));
        Assert.Contains("не в дворі", FuraJudge.NotYet(Trailer(10, 30.5), T0, T0, Yard));
    }

    [Fact]
    public void Exact_dock_needs_two_metres_and_a_quarter_radian()
    {
        Assert.True(FuraJudge.Exact(Trailer(10, 7, Math.PI), Yard));
        Assert.False(FuraJudge.Exact(Trailer(10, 7.1, Math.PI), Yard));
        Assert.True(FuraJudge.Exact(Trailer(-10, 5, -Math.PI + 0.2), Yard));   // через ±π
        Assert.False(FuraJudge.Exact(Trailer(-10, 5, Math.PI - 0.3), Yard));
        Assert.False(FuraJudge.Exact(Trailer(-10, 5, 0), Yard));                // задом наперед
    }

    [Fact]
    public void Pickup_needs_hitched_trailer_within_12_m_of_the_dock()
    {
        var dock = new FuraDock("1", 0, 0, 0);
        Assert.True(FuraJudge.PickedUp(Trailer(0, 12, c: true), dock));
        Assert.False(FuraJudge.PickedUp(Trailer(0, 12.1, c: true), dock));
        Assert.False(FuraJudge.PickedUp(Trailer(0, 0, c: false), dock));
    }
}
