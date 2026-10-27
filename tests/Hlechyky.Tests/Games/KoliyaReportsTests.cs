using System.Text;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Звіт партії «Колії» (27.10): з браузера лягає рядком у data/diag/koliya-*.jsonl з ніком, а завеликий, частий чи
/// сміттєвий — ні.
/// </summary>
public sealed class KoliyaReportsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hl-koliya-" + Guid.NewGuid().ToString("N"));
    readonly DiagLog _log;

    public KoliyaReportsTests() => _log = new DiagLog(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    static Stream Body(string json) => new MemoryStream(Encoding.UTF8.GetBytes(json));

    [Fact]
    public async Task Звіт_лягає_з_ніком_а_частий_завеликий_і_сміття_ні()
    {
        var inbox = new KoliyaReports(_log);
        const string ok = """{"v":1,"end":"lost","why":"derail","mode":"normal","sec":143,"prog":0.31,"pl":2}""";
        Assert.Equal(200, await inbox.Accept("Оля", "1.2.3.4", ok.Length, Body(ok), 10_000));
        Assert.Equal(429, await inbox.Accept("Оля", "1.2.3.4", ok.Length, Body(ok), 11_000));   // за секунду — ще рано
        Assert.Equal(200, await inbox.Accept("Петро", "1.2.3.4", ok.Length, Body(ok), 11_000));
        Assert.Equal(200, await inbox.Accept("Оля", "1.2.3.4", ok.Length, Body(ok), 12_500));
        Assert.Equal(413, await inbox.Accept("Оля", "1.2.3.4", KoliyaReports.MaxBytes + 1, Body(ok), 20_000));
        Assert.Equal(413, await inbox.Accept("Оля", "1.2.3.4", null, Body(ok), 30_000));
        Assert.Equal(400, await inbox.Accept("Оля", "1.2.3.4", 5, Body("[1,2]"), 40_000));
        Assert.Equal(400, await inbox.Accept("Оля", "1.2.3.4", 5, Body("{оля"), 50_000));

        await _log.Drain(TimeSpan.FromSeconds(3));
        var path = _log.PathOf("koliya", DateTime.Now);
        var lines = File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
        Assert.Equal(3, lines.Count);
        Assert.Equal("Оля", lines[0].GetProperty("nick").GetString());
        Assert.Equal("koliya", lines[0].GetProperty("k").GetString());
        Assert.Equal("derail", lines[0].GetProperty("m").GetProperty("why").GetString());
        Assert.Equal(143, lines[0].GetProperty("m").GetProperty("sec").GetInt32());
        Assert.Contains("Оля", File.ReadAllText(path));   // кирилиця як є: файл читають очима
    }
}
