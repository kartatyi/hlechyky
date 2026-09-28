using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Лелеки, прохід №3: «🪽 ключ лелек» — поруч з іншою падаєш повільніше; розклад ключа — у кадрі наперед.</summary>
[Collection(SerialPerf.Name)]
public class StorksKeyTests
{
    static RoomHarness Table(bool key, int players = 2)
    {
        var h = new RoomHarness("storks", key ? new { key = "on" } : null, 42);
        foreach (var n in new[] { "Оля", "Петро", "Микола" }.Take(players)) h.Join(n);
        Assert.True(h.Start().Ok);
        for (var i = 0; i < 200 && h.View(null).GetProperty("ph").GetString() == "ready"; i++) h.Tick();
        return h;
    }

    static RunnerSim Sim(RoomHarness h) => ((Storks)h.Room.Game).World!;

    [Fact]
    public void Default_table_has_no_key_and_no_key_in_frames()
    {
        var h = Table(false);
        Assert.False(Sim(h).KeyOn);
        h.Tick(5);
        Assert.DoesNotContain("\"ky\"", Views.Text(h.Room.Game.Frame()));
        Assert.False(h.View(0).GetProperty("keyOpt").GetBoolean());
        var info = new Storks().Info;
        Assert.Equal("off", info.Options!.Single(o => o.Key == "key").Default);
    }

    [Fact]
    public void Close_storks_fly_as_a_key_and_fall_slower_far_ones_do_not()
    {
        var sim = new RunnerSim(RunnerMode.Storks, 5, [true, true, true], 0, 1000, snowOn: false) { KeyOn = true };
        sim.ClearCourse();
        sim.P[0].Y = sim.P[1].Y = 200 * 16;
        sim.P[2].Y = 100 * 16;   // далеко внизу — сама
        for (var i = 0; i < RunnerStorks.KeyDelay; i++) { sim.P[0].Y = sim.P[1].Y = 200 * 16; sim.P[2].Y = 100 * 16; sim.P[0].Vy = sim.P[1].Vy = sim.P[2].Vy = 0; sim.Step(); }
        var t = sim.S;
        Assert.True(sim.InKey(0, t) && sim.InKey(1, t));
        Assert.False(sim.InKey(2, t));
        var y0 = sim.P[0].Y; var y2 = sim.P[2].Y;
        sim.P[0].Vy = sim.P[2].Vy = 0;
        sim.Step();
        Assert.Equal(-RunnerStorks.KeyG, sim.P[0].Y - y0);
        Assert.Equal(-RunnerStorks.G, sim.P[2].Y - y2);
    }

    [Fact]
    public void Frame_carries_key_schedule_ahead_and_client_mirror_gets_the_same_flight()
    {
        var h = Table(true, 2);
        var sim = Sim(h);
        Assert.True(sim.KeyOn);
        h.Tick(10);
        var f = Views.Json(h.Room.Game.Frame());
        var ky = f.GetProperty("ky").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(8, ky.Length);
        Assert.True(ky[6] >= sim.S, "останній блок ключа — попереду сервера");
        // «клієнт»: власний Sim, який не вирішує ключ сам, а лише знає розклад із кадрів, — летить так само
        var mirror = new RunnerSim(RunnerMode.Storks, 1, [true, true], 0, 1000, snowOn: false) { KeyOn = true };
        for (var k = 0; k < ky.Length; k += 2) mirror.KnowKey(ky[k], ky[k + 1]);
        Assert.Equal(sim.InKey(0, ky[6]), mirror.InKey(0, ky[6]));
        Assert.True(Views.WireBytes(h.Room.Game.Frame()) < 1500);
    }

    [Fact]
    public void Rewind_uses_the_recorded_schedule_so_the_key_is_deterministic()
    {
        static int Fly(bool late)
        {
            var sim = new RunnerSim(RunnerMode.Storks, 9, [true, true], 0, 1000, snowOn: false) { KeyOn = true };
            sim.ClearCourse();
            for (var i = 0; i < 120; i++)
            {
                if (i % 20 == 0) { sim.Input(0, sim.S, 5); sim.Input(1, sim.S, 5); }
                if (late && i % 20 == 10) sim.Input(0, sim.S - 5, 5);   // запізнілий змах — перемотування
                if (!late && i % 20 == 15) sim.Input(0, sim.S - 10, 5);   // той самий змах на тому самому кроці, інакше
                sim.Step();
                sim.ClearEvents();
            }
            return sim.P[0].Y;
        }
        Assert.Equal(Fly(true), Fly(false));
    }
}
