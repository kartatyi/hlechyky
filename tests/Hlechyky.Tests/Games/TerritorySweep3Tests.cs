using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Земля, прохід №3 (29.09.2026): довжина раунду й мета «до 40 % поля» — опцією столу.</summary>
public class TerritorySweep3Tests
{
    static RoomHarness Table(object? options)
    {
        var h = new RoomHarness("territory", options, seed: 42);
        h.Join("Оля");
        h.Join("Петро");
        h.Start();
        h.Tick(Territory.ReadyTicks);
        return h;
    }

    static TerritoryCore Core(RoomHarness h) => ((Territory)h.Room.Game).Core;

    /// <summary>Приспати обох, щоб ніхто не згорів сам, поки ми тикаємо годинник.</summary>
    static void Freeze(RoomHarness h)
    {
        foreach (var r in Core(h).Riders) { r.Alive = false; r.RespawnIn = int.MaxValue; }
    }

    [Fact]
    public void By_default_the_round_is_the_usual_ninety_seconds()
    {
        var h = Table(null);
        Assert.Equal(TerritoryCore.RoundTicks * TerritoryCore.TickMs, h.View(null).GetProperty("timeLeft").GetInt32());
        Assert.Equal(0, h.View(null).GetProperty("goal").GetInt32());
    }

    [Theory]
    [InlineData("60", 600)]
    [InlineData("150", 1500)]
    public void A_short_or_long_round_ends_on_its_own_clock(string round, int ticks)
    {
        var h = Table(new { round });
        Assert.Equal(ticks * TerritoryCore.TickMs, h.View(null).GetProperty("timeLeft").GetInt32());
        Freeze(h);
        h.Tick(ticks - 1);
        Assert.Empty(h.Finished);
        h.Tick();
        Assert.Single(h.Finished);
    }

    [Fact]
    public void Up_to_forty_percent_ends_the_round_the_moment_someone_gets_there()
    {
        var h = Table(new { round = "40" });
        Assert.Equal(Territory.GoalPercent, h.View(null).GetProperty("goal").GetInt32());
        Freeze(h);
        var core = Core(h);
        core.Wipe();
        var cells = core.W * core.H;
        for (var i = 0; i < cells * 39 / 100; i++) core.PaintOwner(i, 1);
        h.Tick(5);
        Assert.Empty(h.Finished);

        for (var i = cells * 39 / 100; i < cells * 41 / 100; i++) core.PaintOwner(i, 1);
        h.Tick();
        var fin = Assert.Single(h.Finished);
        Assert.Equal([1], fin.Result.Winners);
    }

    [Fact]
    public void Up_to_forty_percent_still_has_a_three_minute_ceiling()
    {
        var h = Table(new { round = "40" });
        Freeze(h);
        h.Tick(Territory.GoalMaxTicks - 1);
        Assert.Empty(h.Finished);
        h.Tick();
        Assert.Single(h.Finished);
    }
}
