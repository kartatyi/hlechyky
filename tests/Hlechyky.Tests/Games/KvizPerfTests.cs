using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Заміри «Проходу по іграх №2» (28.09.2026) для «Скільки?», «Де це?», наввипередки й Мафії на повному столі:
/// середній тик через справжній <see cref="Rooms"/>, хід (<c>Act</c>), побудова виду для кожного місця (так робить
/// розсилка для Hidden-ігор) і розмір виду й кадру таким, яким він іде на дріт: UTF-8, кирилиця без \u-escape
/// (SignalR серіалізує саме так; <see cref="Views.Text"/> тестів екранує кирилицю й завищує розмір утричі).
/// Бюджет спільних правил: тик ≤ 0,25 мс, хід і вид ≤ 0,2 мс, кадр ≤ 1,5 КБ. Числа — у вивід тесту.
/// </summary>
[Collection(SerialPerf.Name)]
public class KvizPerfTests(ITestOutputHelper output, WordleWords fx) : IClassFixture<WordleWords>
{
    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static int Bytes(object? o) => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(o, Wire));

    /// <summary>Лічильник заміру: скільки разів і скільки разом.</summary>
    sealed class Meter
    {
        readonly Stopwatch _sw = new();
        public int Count { get; private set; }
        public T Run<T>(Func<T> f) { _sw.Start(); var r = f(); _sw.Stop(); Count++; return r; }
        public void Run(Action f) { _sw.Start(); f(); _sw.Stop(); Count++; }
        public double AvgMs => Count == 0 ? 0 : _sw.Elapsed.TotalMilliseconds / Count;
        public override string ToString() => $"{AvgMs:F4} мс × {Count}";
    }

    sealed class Sizes
    {
        public int View, Frame, Views, Frames;
        public void Take(RoomHarness h, int seats)
        {
            var g = h.Room.Game;
            lock (h.Room.Sync)
            {
                for (var s = 0; s < seats; s++) View = Math.Max(View, Bytes(g.View(s)));
                View = Math.Max(View, Bytes(g.View(null)));
                if (g.Frame() is { } f) Frame = Math.Max(Frame, Bytes(f));
            }
        }
    }

    /// <summary>Вид кожного місця й глядача — рівно те, що будує розсилка на подію «room».</summary>
    static void BuildViews(RoomHarness h, Meter m, int seats)
    {
        var g = h.Room.Game;
        lock (h.Room.Sync)
            for (var s = -1; s < seats; s++) m.Run(() => g.View(s < 0 ? null : s));
    }

    static string Phase(RoomHarness h, string key = "phase") => h.View(null).GetProperty(key).GetString()!;

    void Report(string game, Meter tick, Meter act, Meter view, Sizes size, RoomHarness h)
    {
        var views = h.Outbox.OfType<RoomViews>().Count();
        var frames = h.Outbox.OfType<RoomFrame>().Count();
        output.WriteLine($"{game}: тик {tick}, хід {act}, вид {view}; вид ≤ {size.View} Б, кадр ≤ {size.Frame} Б; "
            + $"розсилок видів {views}, кадрів {frames} за {tick.Count} тиків");
        Assert.True(tick.AvgMs <= 0.25, $"{game}: тик {tick.AvgMs} мс");
        Assert.True(act.AvgMs <= 0.2, $"{game}: хід {act.AvgMs} мс");
        Assert.True(view.AvgMs <= 0.2, $"{game}: вид {view.AvgMs} мс");
        Assert.InRange(size.Frame, 0, 1500);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Skilky_on_a_table_of_twelve_ticks_answers_and_views_stay_within_budget()
    {
        var h = new RoomHarness("skilky", options: new { questions = "15", seconds = "15" }, seed: 3);
        for (var i = 0; i < Skilky.MaxSeats; i++) h.Join("Гравець" + i);
        Assert.True(h.Start().Ok);
        Meter tick = new(), act = new(), view = new();
        var size = new Sizes();
        var rng = new Random(1);
        for (var i = 0; i < 2000; i++)
        {
            if (h.Room.Status == RoomStatus.Finished) { size.Take(h, Skilky.MaxSeats); Assert.True(h.Rematch().Ok); }
            if (Phase(h) == Skilky.PhaseAsk)
                for (var s = 0; s < Skilky.MaxSeats; s++)
                    if (rng.Next(5) == 0) act.Run(() => h.Act(s, "answer", new { value = rng.Next(1, 100_000).ToString("N0") }));
            tick.Run(() => h.Tick(1));
            if (i % 7 == 0) { BuildViews(h, view, Skilky.MaxSeats); size.Take(h, Skilky.MaxSeats); }
        }
        Report("Скільки? ×12", tick, act, view, size, h);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Wordle_race_on_a_table_of_six_ticks_guesses_and_views_stay_within_budget()
    {
        var h = new RoomHarness("wordle-race", new { rounds = "5", seconds = "90" }, 5, RoomHarness.WithService(fx.Words));
        for (var i = 0; i < WordleRace.MaxSeats; i++) Assert.True(h.Join("Гравець" + i).Ok);
        Assert.True(h.Start().Ok);
        Meter tick = new(), act = new(), view = new();
        var size = new Sizes();
        var rng = new Random(2);
        for (var i = 0; i < 2000; i++)
        {
            if (h.Room.Status == RoomStatus.Finished) { size.Take(h, WordleRace.MaxSeats); Assert.True(h.Rematch().Ok); }
            if (Phase(h) == WordleRace.PhasePlay)
                for (var s = 0; s < WordleRace.MaxSeats; s++)
                    if (rng.Next(6) == 0)
                    {
                        var word = rng.Next(8) == 0 ? ((WordleRace)h.Room.Game).Answer : fx.Five[rng.Next(fx.Five.Length)];
                        act.Run(() => h.Act(s, "guess", new { word }));
                    }
            tick.Run(() => h.Tick(1));
            if (i % 7 == 0) { BuildViews(h, view, WordleRace.MaxSeats); size.Take(h, WordleRace.MaxSeats); }
        }
        Report("Наввипередки ×6", tick, act, view, size, h);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Mafia_on_a_table_of_twelve_ticks_night_deeds_votes_and_views_stay_within_budget()
    {
        string[] nicks = ["Оля", "Петро", "Ганна", "Микола", "Іван", "Марія", "Тарас", "Соломія", "Богдан", "Леся", "Остап", "Дарина"];
        var h = new RoomHarness("mafia", new { pace = "fast", extra = "don,maniac,kuma" }, 7);
        foreach (var n in nicks) h.Join(n);
        Assert.True(h.Start().Ok);
        Meter tick = new(), act = new(), view = new();
        var size = new Sizes();
        var rng = new Random(3);
        var done = new HashSet<string>();
        for (var i = 0; i < 2000; i++)
        {
            if (h.Room.Status == RoomStatus.Finished) { size.Take(h, nicks.Length); Assert.True(h.Rematch().Ok); done.Clear(); }
            var v = h.View(null);
            var phase = v.GetProperty("phase").GetString();
            var day = v.GetProperty("day").GetInt32();
            var alive = v.GetProperty("players").EnumerateArray().Where(p => p.GetProperty("alive").GetBoolean())
                .Select(p => p.GetProperty("seat").GetInt32()).ToArray();
            if ((phase == "night" || phase == "vote") && done.Add($"{h.Room.Round}:{phase}:{day}"))
                foreach (var s in alive)
                {
                    var me = h.View(s).GetProperty("me");
                    var role = me.ValueKind == JsonValueKind.Object ? me.GetProperty("role").GetString() : null;
                    var target = alive[rng.Next(alive.Length)];
                    var deed = phase == "vote" ? "vote" : role switch
                    {
                        "mafia" or "don" or "maniac" => "kill", "sheriff" => "check", "doctor" => "heal", "kuma" => "block", _ => null,
                    };
                    if (deed is null) continue;
                    act.Run(() => h.Act(s, deed, new { seat = target }));
                    if (role is "mafia" or "don" && phase == "night") act.Run(() => h.Act(s, "say", new { text = "ну що, кого сьогодні?" }));
                }
            tick.Run(() => h.Tick(1));
            if (i % 7 == 0) { BuildViews(h, view, nicks.Length); size.Take(h, nicks.Length); }
        }
        Report("Мафія ×12", tick, act, view, size, h);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Geo_on_a_table_of_ten_pins_and_views_stay_within_budget()
    {
        using var c = new GeoCache();
        var h = GeoTests.Table(c, 10, new { rounds = "10", seconds = "30" });
        Meter tick = new(), act = new(), view = new();
        var size = new Sizes();
        var rng = new Random(4);
        for (var i = 0; i < 2000; i++)
        {
            if (h.Room.Status == RoomStatus.Finished) { size.Take(h, 10); Assert.True(h.Rematch().Ok); }
            var phase = Phase(h);
            if (phase == "guess")
                for (var s = 0; s < 10; s++)
                    if (rng.Next(6) == 0)
                    {
                        act.Run(() => h.Act(s, "guess", new { x = rng.Next(GeoMap.W), y = rng.Next(GeoMap.H) }));
                        if (rng.Next(2) == 0) act.Run(() => h.Act(s, "ready"));
                    }
            if (phase == "reveal") for (var s = 0; s < 10; s++) if (rng.Next(8) == 0) act.Run(() => h.Act(s, "next"));
            tick.Run(() => h.Tick(1));
            if (i % 7 == 0) { BuildViews(h, view, 10); size.Take(h, 10); }
        }
        Report("Де це? ×10", tick, act, view, size, h);
    }
}
