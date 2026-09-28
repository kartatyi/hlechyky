using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Стрибозаври, прохід №3: «👻 дух лавини» — вибулий раз на 9 с кидає брилу перед бігуном.</summary>
[Collection(SerialPerf.Name)]
public class DinoSpiritTests
{
    static readonly string[] Nicks = ["Оля", "Петро", "Микола"];

    static RoomHarness Table(bool spirit, int players = 3)
    {
        var h = new RoomHarness("dino", spirit ? new { spirit = "on" } : null, 42);
        foreach (var n in Nicks.Take(players)) h.Join(n);
        Assert.True(h.Start().Ok);
        for (var i = 0; i < 200 && Ph(h) == "ready"; i++) h.Tick();
        Assert.Equal("run", Ph(h));
        return h;
    }

    static RunnerSim Sim(RoomHarness h) => ((Dino)h.Room.Game).World!;
    static string Ph(RoomHarness h) => h.View(null).GetProperty("ph").GetString()!;

    static void Out(RoomHarness h, int seat)
    {
        Sim(h).P[seat].Lag = 20000;
        h.Tick();
        Assert.True(Sim(h).P[seat].Out);
    }

    [Fact]
    public void Default_table_has_no_spirit_and_drop_is_refused()
    {
        var h = Table(false);
        Out(h, 2);
        Assert.False(h.View(0).GetProperty("spirit").GetBoolean());
        Assert.Equal("Духів лавини за цим столом не кличуть", h.Act(2, "drop").Message);
    }

    [Fact]
    public void Only_the_fallen_drop_after_a_short_breath_then_once_per_nine_seconds()
    {
        var h = Table(true);
        Assert.Equal("Духом стають, лише коли лавина наздожене", h.Act(0, "drop").Message);
        Out(h, 2);
        Assert.StartsWith("Дух набирає снігу", h.Act(2, "drop").Message);
        var sim = Sim(h);
        sim.ClearCourse();
        for (var i = 0; i < Dino.SpiritFirst / RunnerSim.StepsPerTick + 1; i++) { sim.P[0].Lag = sim.P[1].Lag = 0; h.Tick(); }
        Assert.True(h.Act(2, "drop").Ok);
        Assert.Equal(1, sim.SnowCount);
        Assert.StartsWith("Дух набирає снігу — ще 9 с", h.Act(2, "drop").Message);
    }

    [Fact]
    public void Block_lands_before_the_chosen_runner_or_the_leader_and_counts_for_the_avenger_not_the_sniper()
    {
        var h = Table(true);
        Out(h, 2);
        var sim = Sim(h);
        sim.ClearCourse();
        for (var i = 0; i < Dino.SpiritFirst / RunnerSim.StepsPerTick + 1; i++) { sim.P[0].Lag = 0; sim.P[1].Lag = 0; h.Tick(); }
        sim.P[1].Lag = 300;   // Оля — лідерка
        Assert.True(h.Act(2, "drop", new { t = 1 }).Ok);
        Assert.Equal(1, sim.SnowBlock(0).For);
        for (var i = 0; i < Dino.SpiritSteps / RunnerSim.StepsPerTick + 1; i++) { sim.P[0].Lag = 0; sim.P[1].Lag = 300; h.Tick(); }
        Assert.True(h.Act(2, "drop").Ok);
        Assert.Equal(0, sim.SnowBlock(sim.SnowCount - 1).For);
        // Оля біжить у свою брилу
        for (var i = 0; i < 80; i++) { sim.P[0].Lag = 0; sim.P[1].Lag = 300; h.Tick(); }
        Assert.True(sim.P[0].Hits >= 1);
        sim.P[1].Lag = 20000;
        h.Tick();
        var v = h.View(0);
        var av = v.GetProperty("avenger");
        Assert.Equal(2, av[0].GetInt32());
        Assert.True(av[1].GetInt32() >= 1);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, v.GetProperty("sniper").ValueKind);
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:dino-snow");
    }
}
