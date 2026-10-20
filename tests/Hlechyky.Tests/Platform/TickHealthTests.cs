using System.Text;
using System.Text.Json;
using Hlechyky.Games;
using Microsoft.Extensions.Configuration;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Замір «дьоргання» (20.10): підсумки й затики циклу тика лягають у data/diag/tick-*.jsonl, метр браузера —
/// у jerk-*.jsonl, і порожніх вікон чи сміття від клієнта там нема.
/// </summary>
public sealed class TickHealthTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hl-diag-" + Guid.NewGuid().ToString("N"));
    readonly DiagLog _log;

    public TickHealthTests() => _log = new DiagLog(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    List<JsonElement> Lines(string kind)
    {
        _log.Drain(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        var path = _log.PathOf(kind, DateTime.Now);
        if (!File.Exists(path)) return [];
        return [.. File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement.Clone())];
    }

    static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Рівне_коло_не_пише_затиків_а_спізніле_пише()
    {
        var h = new TickHealth(_log);
        h.Loop(10.4, 0.3, 0, 0.2, Now);
        h.Loop(9.8, 0.3, 0, 0.2, Now);
        Assert.Empty(Lines("tick"));

        h.Loop(62, 0.3, 0, 0.2, Now);   // цикл прокинувся на 52 мс пізніше
        var spike = Assert.Single(Lines("tick"));
        Assert.Equal("spike", spike.GetProperty("k").GetString());
        Assert.Equal(52, spike.GetProperty("late").GetDouble());
    }

    [Fact]
    public void Повільна_розсилка_теж_затик_навіть_якщо_цикл_встиг()
    {
        var h = new TickHealth(_log);
        h.Loop(10, 0.3, 0, 40, Now);
        var spike = Assert.Single(Lines("tick"));
        Assert.Equal(40, spike.GetProperty("flush").GetDouble());
    }

    [Fact]
    public void Вікно_рахує_запізнення_кожної_кімнати_і_зсуви_розкладу()
    {
        var h = new TickHealth(_log);
        h.RoomTick("r1", "bomber", 60, 4, 1, 2, 0.5);
        h.Loop(10, 0.5, 0, 0.1, Now);
        h.RoomTick("r1", "bomber", 60, 4, 1, 5, 0.4);
        h.Loop(10, 0.4, 0, 0.1, Now);
        h.RoomTick("r1", "bomber", 60, 4, 1, 70, 0.6);   // тик на цілий тик пізніше — розклад зсунувся
        h.Loop(10, 0.6, 0, 0.1, Now);
        h.CloseWindow(Now);

        var lines = Lines("tick");
        var spike = Assert.Single(lines, l => l.GetProperty("k").GetString() == "spike");
        Assert.Equal("r1", spike.GetProperty("room").GetString());
        Assert.Equal(70, spike.GetProperty("roomLate").GetDouble());

        var win = Assert.Single(lines, l => l.GetProperty("k").GetString() == "win");
        Assert.Equal(3, win.GetProperty("loops").GetInt32());
        var room = Assert.Single(win.GetProperty("rooms").EnumerateArray());
        Assert.Equal("bomber", room.GetProperty("game").GetString());
        Assert.Equal(3, room.GetProperty("ticks").GetInt32());
        Assert.Equal(1, room.GetProperty("rebased").GetInt32());
        Assert.Equal(1, room.GetProperty("over25").GetInt32());
        Assert.Equal(70, room.GetProperty("late").GetProperty("max").GetDouble());
        Assert.Single(h.Recent());
    }

    [Fact]
    public void Порожнє_вікно_без_кімнат_не_пишеться()
    {
        var h = new TickHealth(_log);
        h.Loop(10, 0, 0, 0, Now);
        h.CloseWindow(Now);
        Assert.Empty(Lines("tick"));
        Assert.Empty(h.Recent());
    }

    [Fact]
    public void Вимкнений_замір_мовчить()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Diag:Tick"] = "false" }).Build();
        var h = new TickHealth(_log, cfg);
        h.RoomTick("r1", "bomber", 60, 4, 1, 90, 0.5);
        h.Loop(80, 0.5, 0, 0.1, Now);
        h.CloseWindow(Now);
        Assert.Empty(Lines("tick"));
    }

    static Stream Body(string json) => new MemoryStream(Encoding.UTF8.GetBytes(json));

    [Fact]
    public async Task Метр_браузера_лягає_з_ніком_а_сміття_ні()
    {
        var inbox = new JerkInbox(_log);
        const string ok = """{"v":1,"game":"bomber","frames":80}""";
        Assert.Equal(200, await inbox.Accept("Оля", "1.2.3.4", ok.Length, Body(ok), 10_000));
        Assert.Equal(429, await inbox.Accept("Оля", "1.2.3.4", ok.Length, Body(ok), 10_500));   // за пів секунди — ще рано
        Assert.Equal(200, await inbox.Accept("Петро", "1.2.3.4", ok.Length, Body(ok), 10_500));
        Assert.Equal(413, await inbox.Accept("Оля", "1.2.3.4", JerkInbox.MaxBytes + 1, Body(ok), 20_000));
        Assert.Equal(413, await inbox.Accept("Оля", "1.2.3.4", null, Body(ok), 30_000));
        Assert.Equal(400, await inbox.Accept("Оля", "1.2.3.4", 5, Body("[1,2]"), 40_000));
        Assert.Equal(400, await inbox.Accept("Оля", "1.2.3.4", 5, Body("{оля"), 50_000));

        var lines = Lines("jerk");
        Assert.Equal(2, lines.Count);
        Assert.Equal("Оля", lines[0].GetProperty("nick").GetString());
        Assert.Equal(80, lines[0].GetProperty("m").GetProperty("frames").GetInt32());
        // кирилиця — як є, а не Р…: файл читають очима
        Assert.Contains("Оля", File.ReadAllText(_log.PathOf("jerk", DateTime.Now)));
    }
}
