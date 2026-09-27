using System.Diagnostics;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Швидкодія Клавоперегонів (spec §5, бюджет хвилі: середній тик ≤ 0,25 мс на максимумі гравців). Окремою колекцією:
/// у паралельному прогоні стінний годинник бреше.
/// </summary>
[Collection(SerialPerf.Name)]
public class TyperacePerfTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Perf")]
    public void Perf_three_thousand_ticks_with_ten_typing_racers_take_under_a_second()
    {
        var h = TyperaceTests.Table(10, new { length = "long", source = "classic" });
        TyperaceTests.ToGo(h);
        var len = TyperaceTests.Len(h);
        var room = h.Room;
        var nicks = room.Seats.Select(s => s!).ToArray();
        var payloads = new System.Text.Json.JsonElement[len + 1, 2];
        for (var c = 0; c <= len; c++)
            for (var e = 0; e < 2; e++) payloads[c, e] = Views.Payload(new { c, e });

        const int ticks = 3000;
        long frameBytes = 0, frames = 0, maxFrame = 0;
        var tickTicks = 0L;
        var total = Stopwatch.StartNew();
        for (var i = 0; i < ticks; i++)
        {
            // кожен гонщик щотику шле новий pos (і інколи червоний) — гірше, ніж у житті (там ≤ 5/с)
            for (var s = 0; s < 10; s++)
                h.Rooms.Input(h.RoomId, nicks[s], "pos", payloads[(i + s * 7) % len, (i + s) % 9 == 0 ? 1 : 0]);
            h.Clock.AdvanceMs(1);           // годинник ледь іде — заїзд не впирається в стелю
            var t0 = Stopwatch.GetTimestamp();
            var outbox = h.Rooms.Tick(room);
            tickTicks += Stopwatch.GetTimestamp() - t0;
            foreach (var f in outbox.OfType<RoomFrame>())
            {
                var bytes = System.Text.Encoding.UTF8.GetByteCount(Views.Text(f.Frame));
                frameBytes += bytes;
                frames++;
                maxFrame = Math.Max(maxFrame, bytes);
            }
        }
        total.Stop();
        Assert.Equal(RoomStatus.Playing, room.Status);
        Assert.Equal(ticks, frames);
        var avgTickMs = tickTicks * 1000.0 / Stopwatch.Frequency / ticks;
        output.WriteLine($"Клавоперегони: {ticks} тиків × 10 гонщиків — усього {total.ElapsedMilliseconds} мс (разом із pos і серіалізацією), " +
                         $"середній Tick {avgTickMs:0.0000} мс, кадр у середньому {frameBytes / Math.Max(1, frames)} Б, найбільший {maxFrame} Б");
        Assert.True(total.ElapsedMilliseconds < 1000, $"{total.ElapsedMilliseconds} мс");
        Assert.True(avgTickMs < 0.25, $"середній тик {avgTickMs} мс");
        Assert.True(maxFrame < 120);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Perf_the_judge_replays_a_thousand_max_length_logs_under_200_ms()
    {
        // найдовший дозволений журнал: 1600 подій (текст 700 знаків, із помилками й Backspace)
        var log = TyperaceLogs.Sloppy(700, every: 3, seed: 7);
        Assert.InRange(log.K.Length, 1100, TyperaceJudge.MaxEvents);
        var len = 700;
        var serverMs = log.Ms + 200;
        Assert.Null(TyperaceJudge.Check(len, log.K, log.D, serverMs).Flag);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++) TyperaceJudge.Check(len, log.K, log.D, serverMs);
        sw.Stop();
        output.WriteLine($"Суддя: 1000 журналів по {log.K.Length} подій ({log.K.Length + log.D.Length} Б) — {sw.ElapsedMilliseconds} мс, " +
                         $"{sw.Elapsed.TotalMilliseconds / 1000:0.000} мс на журнал");
        Assert.True(sw.ElapsedMilliseconds < 200, $"{sw.ElapsedMilliseconds} мс");
        // і payload фінішу вміщається в квоту каркаса (8 КБ)
        var payload = Views.Text(new { k = log.K, d = log.D });
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(payload) < Rooms.MaxPayloadBytes);
    }
}
