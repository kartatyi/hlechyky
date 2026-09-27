using System.Diagnostics;
using System.Text;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Швидкодія аркад пакета arena (Прохід по іграх №2, 28.09.2026): Бомбер, Танчики, Кривуля, Земля, Дуель і
/// Перестрілка на максимумі гравців. Бюджет проходу: середній кімнатний тик ≤ 0,25 мс (разом із побудовою кадру),
/// ввід і вид — без помітних витрат, кадр ≤ 1,5 КБ. Міряємо саме <c>Rooms.Tick</c> — те, що крутиться під замком
/// кімнати в проді, — а кадр і вид серіалізуємо тим самим JSON, що піде на дріт. Окремою колекцією: у повному
/// паралельному прогоні стінний годинник бреше в рази.
/// </summary>
[Collection(SerialPerf.Name)]
public class ArenaPerfTests(ITestOutputHelper output)
{
    sealed class Stats
    {
        public int Ticks, Frames, Views, Inputs;
        public long TickTicks, MaxTickTicks, FrameBytes, MaxFrame, ViewBytes, MaxView, ViewTicks, MaxViewTicks, InputTicks;
        public double AvgTickMs => Ms(TickTicks) / Math.Max(1, Ticks);
        public double MaxTickMs => Ms(MaxTickTicks);
        public double AvgViewMs => Ms(ViewTicks) / Math.Max(1, Views);
        public double MaxViewMs => Ms(MaxViewTicks);
        public double AvgInputMs => Ms(InputTicks) / Math.Max(1, Inputs);
        public long AvgFrame => FrameBytes / Math.Max(1, Frames);
        public long AvgView => ViewBytes / Math.Max(1, Views);
        static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        public override string ToString() =>
            $"тиків {Ticks}: середній {AvgTickMs:0.0000} мс, найдовший {MaxTickMs:0.000} мс; " +
            $"кадрів {Frames}: у середньому {AvgFrame} Б, найбільший {MaxFrame} Б; " +
            $"видів {Views}: у середньому {AvgView} Б, найбільший {MaxView} Б, побудова+JSON {AvgViewMs:0.000} мс (найдовша {MaxViewMs:0.000}); " +
            $"вводів {Inputs}: {AvgInputMs:0.0000} мс";
    }

    /// <summary>
    /// Ганяє стіл <paramref name="ticks"/> тиків: перед кожним — ввід усіх місць (<paramref name="input"/> повертає
    /// скільки вводів послав), партія скінчилась — «Ще раз». Кадр і вид міряються так, як їх побачить браузер.
    /// </summary>
    static Stats Drive(RoomHarness h, int ticks, Func<RoomHarness, int, int> input)
    {
        var s = new Stats();
        var step = h.Room.Info.TickMs;
        for (var i = 0; i < ticks; i++)
        {
            if (h.Room.Status == RoomStatus.Finished) h.Rematch();
            var room = h.Room;
            var t0 = Stopwatch.GetTimestamp();
            s.Inputs += input(h, i);
            s.InputTicks += Stopwatch.GetTimestamp() - t0;

            h.Clock.AdvanceMs(step);
            t0 = Stopwatch.GetTimestamp();
            var outbox = h.Rooms.Tick(room);
            var dt = Stopwatch.GetTimestamp() - t0;
            s.Ticks++;
            s.TickTicks += dt;
            s.MaxTickTicks = Math.Max(s.MaxTickTicks, dt);

            foreach (var o in outbox)
            {
                switch (o)
                {
                    case RoomFrame f:
                        var fb = Encoding.UTF8.GetByteCount(Views.Text(f.Frame));
                        s.Frames++;
                        s.FrameBytes += fb;
                        s.MaxFrame = Math.Max(s.MaxFrame, fb);
                        break;
                    case RoomViews:
                        // Broadcaster будує вид під замком кімнати й серіалізує — рахуємо обидва кроки разом.
                        var v0 = Stopwatch.GetTimestamp();
                        string text;
                        lock (room.Sync) text = Views.Text(room.Game.View(null));
                        var vt = Stopwatch.GetTimestamp() - v0;
                        var vb = Encoding.UTF8.GetByteCount(text);
                        s.Views++;
                        s.ViewTicks += vt;
                        s.MaxViewTicks = Math.Max(s.MaxViewTicks, vt);
                        s.ViewBytes += vb;
                        s.MaxView = Math.Max(s.MaxView, vb);
                        break;
                }
            }
        }
        return s;
    }

    static RoomHarness Table(string game, int players, object? options = null, int seed = 42)
    {
        var h = new RoomHarness(game, options, seed: seed);
        foreach (var nick in Nicks.Take(players)) h.Join(nick);
        if (h.Room.Status == RoomStatus.Lobby) h.Start();
        return h;
    }

    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марко", "Зоя", "Тарас", "Леся"];

    /// <summary>Спершу прогрів (JIT, кеші), потім три заміри — беремо найкращий: справжнє сповільнення видно в усіх трьох.</summary>
    Stats Best(string name, Func<Stats> run)
    {
        run();
        Stats? best = null;
        for (var k = 0; k < 3; k++)
        {
            var s = run();
            if (best is null || s.AvgTickMs < best.AvgTickMs) best = s;
        }
        output.WriteLine($"{name}: {best}");
        return best!;
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Bomber_of_six_bombing_all_the_time_ticks_cheaply()
    {
        var s = Best("Бомбер ×6", () => Drive(Table("bomber", 6), 3000, (h, i) =>
        {
            var n = 0;
            for (var seat = 0; seat < 6; seat++)
            {
                if ((i + seat) % (5 + seat) == 0) { h.Input(seat, "move", new { dir = (i / 5 + seat) % 4 }); n++; }
                if ((i + seat * 3) % (19 + seat) == 0) { h.Input(seat, "bomb"); n++; }
            }
            return n;
        }));
        Assert.True(s.AvgTickMs < 0.25, $"середній тик {s.AvgTickMs} мс");
        Assert.True(s.MaxFrame <= 1500, $"найбільший кадр {s.MaxFrame} Б");
        Assert.True(s.AvgInputMs < 0.2, $"ввід {s.AvgInputMs} мс");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Tanks_of_six_on_the_big_map_shooting_all_the_time_tick_cheaply()
    {
        var s = Best("Танчики ×6", () => Drive(Table("tanks", 6), 3000, (h, i) =>
        {
            var n = 0;
            for (var seat = 0; seat < 6; seat++)
            {
                if ((i + seat) % (6 + seat) == 0) { h.Input(seat, "move", new { dir = (i / 6 + seat) % 4 }); n++; }
                h.Input(seat, "fire");
                n++;
            }
            return n;
        }));
        Assert.True(s.AvgTickMs < 0.25, $"середній тик {s.AvgTickMs} мс");
        Assert.True(s.MaxFrame <= 1500, $"найбільший кадр {s.MaxFrame} Б");
        Assert.True(s.AvgInputMs < 0.2, $"ввід {s.AvgInputMs} мс");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Curve_of_eight_ticks_cheaply_and_sends_few_heavy_views()
    {
        var s = Best("Кривуля ×8", () => Drive(Table("curve", 8), 3000, (h, i) =>
        {
            var n = 0;
            for (var seat = 0; seat < 8; seat++)
                if ((i + seat * 5) % (12 + seat) == 0) { h.Input(seat, "turn", new { d = (i / 12 + seat) % 3 - 1 }); n++; }
            return n;
        }));
        Assert.True(s.AvgTickMs < 0.25, $"середній тик {s.AvgTickMs} мс");
        Assert.True(s.MaxFrame <= 1500, $"найбільший кадр {s.MaxFrame} Б");
        Assert.True(s.AvgInputMs < 0.2, $"ввід {s.AvgInputMs} мс");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Territory_of_six_ticks_cheaply()
    {
        var s = Best("Земля ×6", () => Drive(Table("territory", 6), 2000, (h, i) =>
        {
            var n = 0;
            for (var seat = 0; seat < 6; seat++)
                if ((i + seat) % (4 + seat % 3) == 0) { h.Input(seat, "turn", new { dir = (i / 4 + seat) % 4 }); n++; }
            return n;
        }));
        Assert.True(s.AvgTickMs < 0.25, $"середній тик {s.AvgTickMs} мс");
        Assert.True(s.AvgFrame <= 1500, $"середній кадр {s.AvgFrame} Б");
        Assert.True(s.MaxFrame <= 4096, $"найбільший кадр {s.MaxFrame} Б");   // тик-пожежа їде повними рядками, ARCHITECTURE §12
        Assert.True(s.AvgInputMs < 0.2, $"ввід {s.AvgInputMs} мс");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Duel_and_shootout_tick_for_free()
    {
        var d = Best("Дуель ×2", () => Drive(Table("duel", 2), 3000, (h, i) =>
        {
            if (i % 37 != 0) return 0;
            h.Input(i % 2, "shoot");
            return 1;
        }));
        var s = Best("Перестрілка ×4", () => Drive(Table("shootout", 4), 3000, (h, i) =>
        {
            var n = 0;
            for (var seat = 0; seat < 4; seat++)
            {
                if ((i + seat) % 11 == 0) { h.Input(seat, "aim", new { step = 1 }); n++; }
                if ((i + seat * 7) % 43 == 0) { h.Input(seat, "shoot"); n++; }
            }
            return n;
        }));
        Assert.True(d.AvgTickMs < 0.25 && s.AvgTickMs < 0.25, $"{d.AvgTickMs} / {s.AvgTickMs} мс");
        Assert.True(d.MaxFrame <= 1500 && s.MaxFrame <= 1500, $"{d.MaxFrame} / {s.MaxFrame} Б");
    }
}
