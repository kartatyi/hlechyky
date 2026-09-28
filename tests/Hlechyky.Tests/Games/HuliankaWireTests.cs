using System.Diagnostics;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Прохід по іграх №2 (28.09.2026), пакет «гулянка»: скільки важить дріт реалтайм-ігор на повному столі (вісім
/// гравців, найгустіша юрма). Кадр летить щотика кожному глядачеві, тож його розмір — головна ціна гри для мережі;
/// вид — лише на подіях (і для Hidden-ігор — окремо на кожне місце). Середнє, найбільше й частка тиків із видом —
/// у вивід тесту, межі — з правил проходу (кадр ≤ 1,5 КБ, вид не щотика).
/// </summary>
[Collection(SerialPerf.Name)]
public class HuliankaWireTests(ITestOutputHelper output)
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марта", "Богдан", "Ліна", "Тарас"];

    sealed record Wire(double FrameAvg, int FrameMax, double ViewTicks, int ViewMax, int Ticks, double TickUs);

    /// <summary>Вісім людей за столом, повна юрма; ходять навмання й раз на ~2 с тиснуть дію гри.</summary>
    Wire Measure(string game, object options, Func<int, int, (string action, object payload)?> act, bool input = false)
    {
        var h = new RoomHarness(game, options, seed: 7);
        foreach (var n in Nicks) h.Join(n);
        Assert.True(h.Start().Ok);
        var rng = new Random(5);
        long bytes = 0;
        int frames = 0, max = 0, viewTicks = 0, viewMax = 0, ticks = 0;
        var sw = new Stopwatch();
        for (var t = 0; t < 4000 && h.Room.Status == RoomStatus.Playing; t++)
        {
            if (t % 7 == 0)
                for (var s = 0; s < 8; s++)
                    h.Input(s, "move", new { dir = rng.Next(-1, 4) });
            if (t % 50 == 13)
                for (var s = 0; s < 8; s++)
                    if (act(s, t) is { } a)
                    {
                        if (input) h.Input(s, a.action, a.payload);
                        else h.Act(s, a.action, a.payload);
                    }
            h.Outbox.Clear();
            sw.Start();
            h.Tick();
            sw.Stop();
            ticks++;
            var sawView = false;
            foreach (var o in h.Outbox)
            {
                if (o is RoomFrame f)
                {
                    var len = Views.Text(f.Frame).Length;
                    bytes += len;
                    frames++;
                    if (len > max) max = len;
                }
                else if (o is RoomViews) sawView = true;
            }
            if (sawView)
            {
                viewTicks++;
                if (viewTicks % 10 == 1) viewMax = Math.Max(viewMax, Views.Text(h.View(0)).Length);
            }
        }
        return new Wire(frames == 0 ? 0 : (double)bytes / frames, max, (double)viewTicks / ticks, viewMax, ticks,
            sw.Elapsed.TotalMilliseconds * 1000 / ticks);
    }

    void Report(string game, Wire w)
    {
        output.WriteLine($"{game}: кадр {w.FrameAvg:F0} Б у середньому, найбільший {w.FrameMax} Б; вид на {w.ViewTicks:P1} тиків, " +
                         $"вид місцю до {w.ViewMax} Б; {w.Ticks} тиків, {w.TickUs:F1} мкс на тик через кімнату");
        Assert.True(w.FrameMax <= 1536, $"{game}: кадр {w.FrameMax} Б");
        Assert.True(w.ViewTicks < 0.2, $"{game}: вид летить на {w.ViewTicks:P0} тиків");
    }

    [Theory]
    [Trait("Category", "Perf")]
    [InlineData("crowd")]
    [InlineData("dance")]
    [InlineData("freeze")]
    [InlineData("kupala")]
    [InlineData("potato")]
    [InlineData("skate")]
    [InlineData("tavern")]
    public void Full_table_frames_stay_small_and_views_fly_only_on_events(string game)
    {
        object options = game switch
        {
            "kupala" => new { rounds = "5", folk = "big" },
            "potato" => new { rounds = "5", crowd = "big", pots = "2" },
            "tavern" => new { rounds = "7", crowd = "big" },
            _ => new { rounds = "5", crowd = "big" },
        };
        Func<int, int, (string, object)?> act = game switch
        {
            "crowd" => (s, t) => (t / 50 % 2 == 0 ? "buy" : "shoot", new { }),
            "dance" => (s, t) => (t / 50 % 3 == 2 ? "slap" : "fig", t / 50 % 3 == 2 ? new { } : new { f = (t / 50 + s) % 3 }),
            "freeze" => (s, _) => ("push", new { }),
            "kupala" => (s, t) => ((t / 50 % 3) switch { 0 => "launch", 1 => "torch", _ => "slap" }, new { }),
            "potato" => (s, t) => (s % 2 == 0 ? "pass" : "slap", new { }),
            "tavern" => (s, t) => ((t / 50 % 3) switch { 0 => "drink", 1 => "sit", _ => "punch" }, new { }),
            _ => (_, _) => null,
        };
        Report(game, Measure(game, options, act));
    }

    [Theory]
    [Trait("Category", "Perf")]
    [InlineData("dino")]
    [InlineData("storks")]
    public void Eight_runners_frames_stay_small(string game)
    {
        var h = new RoomHarness(game, new { rounds = "5" }, seed: 11);
        foreach (var n in Nicks) h.Join(n);
        Assert.True(h.Start().Ok);
        var rng = new RunnerRng(3);
        var held = new int[8];
        long bytes = 0;
        int frames = 0, max = 0, viewTicks = 0, ticks = 0, viewMax = 0;
        for (var t = 0; t < 4000 && h.Room.Status == RoomStatus.Playing; t++)
        {
            var sim = game == "dino" ? ((Dino)h.Room.Game).World : ((RunnerParty)h.Room.Game).World;
            if (sim is not null)
                for (var s = 0; s < 8; s++)
                {
                    if (game == "dino" && sim.P[s].Lag > 3000) sim.P[s].Lag = 0;      // хай усі добіжать до кінця заміру
                    if ((t + s) % 3 != 0) continue;
                    held[s] ^= rng.Next(2) == 0 ? 1 : 2;
                    h.Input(s, "in", new { s = sim.S, k = held[s] | ((held[s] & 1) != 0 ? 4 : 0) });
                }
            h.Outbox.Clear();
            h.Tick();
            ticks++;
            var sawView = false;
            foreach (var o in h.Outbox)
            {
                if (o is RoomFrame f)
                {
                    var len = Views.Text(f.Frame).Length;
                    bytes += len;
                    frames++;
                    if (len > max) max = len;
                }
                else if (o is RoomViews) sawView = true;
            }
            if (sawView)
            {
                viewTicks++;
                if (viewTicks % 10 == 1) viewMax = Math.Max(viewMax, Views.Text(h.View(0)).Length);
            }
        }
        Report(game, new Wire(frames == 0 ? 0 : (double)bytes / frames, max, (double)viewTicks / ticks, viewMax, ticks, 0));
    }
}
