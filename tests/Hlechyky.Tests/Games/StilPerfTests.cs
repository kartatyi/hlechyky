using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Прохід 28.09 (пакет «stil»): швидкодія настільних ігор на повному столі. Тик є лише в морського бою (і в «Під
/// глеком» — там свій перф-тест), решта покрокові — у них дорогою може бути тільки побудова виду, яку каркас робить
/// на кожне місце після кожного ходу. Окрема колекція: стінний годинник у паралельному прогоні бреше.
/// </summary>
[Collection(SerialPerf.Name)]
public class StilPerfTests(ITestOutputHelper output)
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марта", "Богдан"];

    static RoomHarness Seat(string game, int players, object? options = null, int seed = 7)
    {
        var h = new RoomHarness(game, options: options, seed: seed);
        for (var i = 0; i < players; i++) h.Join(Nicks[i]);
        if (h.Room.Status == RoomStatus.Lobby) h.Start();
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        return h;
    }

    /// <summary>Середній час побудови й серіалізації виду на одне місце (як це робить Broadcaster) і розмір виду.</summary>
    static (double Ms, int Bytes) Views_(RoomHarness h, int seats, int rounds = 400)
    {
        var game = h.Room.Game;
        var bytes = 0;
        for (var s = 0; s < seats; s++) bytes = Math.Max(bytes, Views.Text(game.View(s)).Length);   // прогрів
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < rounds; i++)
            for (var s = 0; s < seats; s++) Views.Text(game.View(s));
        sw.Stop();
        return (sw.Elapsed.TotalMilliseconds / (rounds * seats), bytes);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Battleship_on_four_ticks_quietly_and_builds_views_cheaply_mid_battle()
    {
        var h = Seat("battleship", 4, seed: 11);
        for (var s = 0; s < 4; s++) { Assert.True(h.Act(s, "random").Ok); Assert.True(h.Act(s, "ready").Ok); }
        h.Tick();
        // Пів партії: бот стріляє по першому живому чужому полю в першу вільну клітинку.
        var rng = new Random(3);
        var shots = 0;
        var actMs = 0.0;
        for (var i = 0; i < 150 && h.Room.Status == RoomStatus.Playing; i++)
        {
            var v = h.View(null);
            var turn = v.GetProperty("turn").GetInt32();
            var boards = v.GetProperty("boards");
            var foes = Enumerable.Range(0, 4).Where(s => s != turn && boards[s].ValueKind == JsonValueKind.Object
                                                        && !boards[s].GetProperty("out").GetBoolean()).ToArray();
            var at = foes[rng.Next(foes.Length)];
            var known = boards[at].GetProperty("hits").EnumerateArray().Concat(boards[at].GetProperty("misses").EnumerateArray())
                .Select(e => e.GetInt32()).ToHashSet();
            var free = Enumerable.Range(0, 100).Where(c => !known.Contains(c)).ToArray();
            var cell = free[rng.Next(free.Length)];
            var sw = Stopwatch.StartNew();
            lock (h.Room.Sync) Assert.True(h.Room.Game.Act(turn, "shoot", Views.Payload(new { cell, at })).Ok);
            actMs += sw.Elapsed.TotalMilliseconds;
            shots++;
            h.Tick();
        }
        Assert.Equal(RoomStatus.Playing, h.Room.Status);

        var (viewMs, viewBytes) = Views_(h, 4);
        var game = h.Room.Game;
        var frameBytes = Views.Text(game.Frame()).Length;

        // Тихий тик у бою (нічого не змінилось) — те, що відбувається 4 рази на секунду весь бій.
        const int N = 100_000;
        var quiet = Stopwatch.StartNew();
        lock (h.Room.Sync)
            for (var i = 0; i < N; i++)
                if (game.Tick().View) throw new InvalidOperationException("тихий тик щось розіслав");
        quiet.Stop();
        var tickMs = quiet.Elapsed.TotalMilliseconds / N;

        output.WriteLine($"Морський бій, 4 гравці, {shots} пострілів: Act {actMs / shots:F4} мс, тихий Tick {tickMs * 1_000_000:F0} нс, " +
                         $"вид {viewMs:F4} мс / {viewBytes} Б, кадр {frameBytes} Б");
        Assert.True(tickMs < 0.25, $"тихий тик {tickMs:F4} мс");
        Assert.True(actMs / shots < 0.2, $"постріл {actMs / shots:F4} мс");
        Assert.True(viewMs < 0.5, $"вид {viewMs:F4} мс");
        Assert.True(frameBytes < 1536, $"кадр {frameBytes} Б");
    }

    [Theory]
    [Trait("Category", "Perf")]
    [InlineData("checkers", 2)]
    [InlineData("chess", 2)]
    [InlineData("c4x", 4)]
    [InlineData("ttt3", 2)]
    [InlineData("domino", 4)]
    [InlineData("durak", 6)]
    [InlineData("scrabble", 4)]
    public void A_full_board_game_builds_its_views_cheaply(string game, int players)
    {
        var h = Seat(game, players);
        var (ms, bytes) = Views_(h, players);
        output.WriteLine($"{game}, {players} гравці: вид {ms:F4} мс, до {bytes} Б");
        Assert.True(ms < 0.5, $"{game}: вид {ms:F4} мс");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Mines_on_the_big_field_for_four_builds_views_cheaply()
    {
        var h = Seat("mines", 4, new { size = "16x16-40" });
        // Кілька ходів, щоб поле було вже розкрите, а не рівне.
        for (var i = 0; i < 12 && h.Room.Status == RoomStatus.Playing; i++)
        {
            var v = h.View(null);
            var turn = v.GetProperty("turn").GetInt32();
            var cells = v.GetProperty("cells").GetString()!;
            var closed = cells.IndexOf('#', (i * 37) % cells.Length);
            if (closed < 0) closed = cells.IndexOf('#');
            h.Act(turn, "open", new { cell = closed });
        }
        var (ms, bytes) = Views_(h, 4);
        output.WriteLine($"mines 16×16, 4 гравці: вид {ms:F4} мс, до {bytes} Б");
        Assert.True(ms < 0.5, $"mines: вид {ms:F4} мс");
    }
}
