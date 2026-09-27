using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Швидкодія «Мотоциклів» і «Змійок» на повному столі (прохід по іграх 28.09): середній тик кімнати разом із
/// кадром, розмір кадру й виду на дроті. Вершників веде проста «рука» — з трьох ходів обирає той, за яким
/// найбільше вільного місця, — тож раунди тривають довго, а сліди виростають до сотень клітинок: саме тоді
/// тик найдорожчий. «Рука» думає поза заміром (їй потрібен вид), у замір іде лише те, що робить сервер.
/// </summary>
[Collection(SerialPerf.Name)]
public class MotoPerfTests(ITestOutputHelper output)
{
    static readonly string[] Nicks = ["Оля", "Петро", "Іра", "Сашко"];
    static readonly (int Dx, int Dy)[] Deltas = [(1, 0), (0, 1), (-1, 0), (0, -1)];

    sealed record Result(int Ticks, double MsPerTick, double FrameAvg, int FrameMax, int ViewMax, int Rounds);

    /// <summary>Стіл на players, «рука» веде всіх, поки не набереться ticks тиків у грі (відліки не рахуються).</summary>
    static Result Play(string game, int players, int ticks, object? options = null, int seed = 7)
    {
        var h = new RoomHarness(game, options, seed: seed);
        for (var i = 0; i < players; i++) h.Join(Nicks[i]);
        if (h.Room.Status == RoomStatus.Lobby) Assert.True(h.Start().Ok);
        var rng = new Random(seed);
        var sw = new Stopwatch();
        long frameBytes = 0;
        int frames = 0, frameMax = 0, viewMax = 0, played = 0, rounds = 0;
        while (played < ticks)
        {
            if (h.Room.Status != RoomStatus.Playing)
            {
                rounds++;
                Assert.True(h.Rematch().Ok);
                if (h.Room.Status == RoomStatus.Lobby) Assert.True(h.Start().Ok);
                continue;
            }
            var v = h.View(null);
            viewMax = Math.Max(viewMax, v.GetRawText().Length);
            var counting = v.GetProperty("startIn").GetInt32() > 0;
            var turns = counting ? [] : Steer(v, players, rng);
            sw.Start();
            foreach (var (seat, dir) in turns) h.Input(seat, "turn", new { dir });
            h.Tick();
            sw.Stop();
            if (!counting) played++;
            var f = Views.Text(h.Room.Game.Frame());
            frames++;
            frameBytes += f.Length;
            frameMax = Math.Max(frameMax, f.Length);
        }
        return new Result(played, sw.Elapsed.TotalMilliseconds / played, (double)frameBytes / frames, frameMax, viewMax, rounds);
    }

    /// <summary>Повороти «руки»: кожному живому — найпросторіший із трьох ходів (прямо, ліворуч, праворуч).</summary>
    static List<(int Seat, int Dir)> Steer(JsonElement v, int players, Random rng)
    {
        int w = v.GetProperty("width").GetInt32(), hgt = v.GetProperty("height").GetInt32();
        int[][] bodies;
        var alive = 15;
        if (v.TryGetProperty("t", out var t))
        {
            bodies = [.. t.EnumerateArray().Select(b => b.EnumerateArray().Select(c => c.GetInt32()).ToArray())];
            alive = v.GetProperty("al").GetInt32();
        }
        else if (v.TryGetProperty("a", out var a))
            bodies = [[.. a.EnumerateArray().Select(c => c.GetInt32())], [.. v.GetProperty("b").EnumerateArray().Select(c => c.GetInt32())]];
        else
            bodies = [[.. v.GetProperty("s").EnumerateArray().Select(c => c.GetInt32())]];
        // напрямок — з голови й шиї: так його бачить і клієнт
        var dirs = bodies.Select(b => b.Length < 2 ? 0 : Array.IndexOf(Deltas, (b[0] % w - b[1] % w, b[0] / w - b[1] / w))).ToArray();
        var busy = new HashSet<int>(bodies.SelectMany(b => b));
        var turns = new List<(int, int)>();
        for (var s = 0; s < bodies.Length && s < players; s++)
        {
            if (bodies[s].Length < 2 || dirs[s] < 0 || (alive & (1 << s)) == 0) continue;
            var cur = dirs[s];
            int best = cur, score = -1;
            foreach (var d in new[] { cur, (cur + 1) % 4, (cur + 3) % 4 })
            {
                var n = Ahead(bodies[s][0], d, w, hgt);
                if (n < 0 || busy.Contains(n)) continue;
                var sc = Space(n, busy, w, hgt) * 4 + rng.Next(3) + (d == cur ? 2 : 0);
                if (sc > score) (best, score) = (d, sc);
            }
            if (best == cur) continue;
            // «Змійка на всіх»: поворот шле той, чия це стрілка
            if (v.TryGetProperty("keys", out var keys))
            {
                var owner = keys.EnumerateArray().Select(k => k.GetInt32()).ToList().FindIndex(k => (k & (1 << best)) != 0);
                if (owner >= 0) turns.Add((owner, best));
            }
            else turns.Add((s, best));
        }
        return turns;
    }

    static int Ahead(int cell, int d, int w, int h)
    {
        var (x, y) = (cell % w + Deltas[d].Dx, cell / w + Deltas[d].Dy);
        return x < 0 || y < 0 || x >= w || y >= h ? -1 : y * w + x;
    }

    static int Space(int start, HashSet<int> busy, int w, int h)
    {
        var seen = new HashSet<int> { start };
        var stack = new Stack<int>();
        stack.Push(start);
        while (stack.Count > 0 && seen.Count < 120)
        {
            var c = stack.Pop();
            for (var d = 0; d < 4; d++)
            {
                var n = Ahead(c, d, w, h);
                if (n >= 0 && !busy.Contains(n) && seen.Add(n)) stack.Push(n);
            }
        }
        return seen.Count;
    }

    void Report(string what, Result r)
    {
        output.WriteLine($"{what}: {r.Ticks} тиків у грі ({r.Rounds} раундів) — {r.MsPerTick:F4} мс на тик (з вводом і кадром); " +
                         $"кадр {r.FrameAvg:F0} Б у середньому, найбільший {r.FrameMax} Б; вид — до {r.ViewMax} Б");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Four_riders_on_the_big_field_tick_well_under_budget()
    {
        var r = Play("tron-party", 4, 2000, new { field = "big" });
        Report("Мотоцикли гуртом, 4 на 34×24", r);
        Assert.True(r.MsPerTick <= 0.25, $"{r.MsPerTick:F4} мс на тик");
        Assert.True(r.FrameMax <= 1536, $"кадр {r.FrameMax} Б");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Four_snakes_on_the_big_field_tick_well_under_budget()
    {
        var r = Play("snake-party", 4, 2000, new { field = "big" });
        Report("Змійки гуртом, 4 на 34×24", r);
        Assert.True(r.MsPerTick <= 0.25, $"{r.MsPerTick:F4} мс на тик");
        Assert.True(r.FrameMax <= 1536, $"кадр {r.FrameMax} Б");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void The_duels_and_the_coop_snake_tick_well_under_budget()
    {
        foreach (var (game, n) in new[] { ("tron", 2), ("snake", 2), ("snake-coop", 4) })
        {
            var r = Play(game, n, 1500);
            Report($"{game} на {n}", r);
            Assert.True(r.MsPerTick <= 0.25, $"{game}: {r.MsPerTick:F4} мс на тик");
            Assert.True(r.FrameMax <= 1536, $"{game}: кадр {r.FrameMax} Б");
        }
    }
}
