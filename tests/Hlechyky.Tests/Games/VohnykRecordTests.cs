using Hlechyky.Games.Impl;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>Факт, що біжить лише з VOHNYK_RECORD=1 (переписує файли рівнів), інакше — Skip.</summary>
public sealed class VohnykRecordFactAttribute : FactAttribute
{
    public VohnykRecordFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VOHNYK_RECORD") != "1")
            Skip = "запис проходжень — лише з VOHNYK_RECORD=1";
    }
}

/// <summary>
/// Запис проходжень (specs/vohnyk.md §5.5, §7.2): робот грає план кожного рівня, журнал і хеші лягають у файл.
/// Звичайний прогін тестів це пропускає; автор рівнів запускає
/// <c>VOHNYK_RECORD=1 dotnet test --filter Record_level_checks</c>. Змінна VOHNYK_ONLY=3 — лише рівень 3.
/// </summary>
public sealed class VohnykRecordTests(ITestOutputHelper output)
{
    /// <summary>
    /// par — час на третю зірку. Робот грає ідеально й обома руками водночас, тож люди вдвох упораються щонайшвидше
    /// за вдвічі-втричі довше: par = 4 × час робота, округлено вгору до 5 с, не менше 25 с. Навчальні рівні 1–2
    /// лишають щедрий par зі spec (25 і 60 с) — там третя зірка має діставатись майже всім.
    /// </summary>
    public static int Par(int n, int botMs, int filePar)
    {
        if (n <= 2) return Math.Max(filePar, (int)Math.Ceiling(botMs / 0.6 / 5000.0) * 5000);
        return Math.Max(25000, (int)Math.Ceiling(botMs * 4 / 5000.0) * 5000);
    }

    [VohnykRecordFact]
    public void Record_level_checks_when_asked()
    {
        var only = int.TryParse(Environment.GetEnvironmentVariable("VOHNYK_ONLY"), out var o) ? o : 0;
        for (var n = 1; n <= VohnykLevels.Count; n++)
        {
            if (only > 0 && n != only) continue;
            var path = VohnykRecord.LevelPath(n);
            if (!File.Exists(path)) { output.WriteLine($"{n:00}: файла нема"); continue; }
            var level = VohnykLevels.LoadFile(path);
            var bot = VohnykPlans.Play(level);
            var solution = bot.Log.ToArray();
            var run = VohnykRecord.Replay(level, solution);
            Assert.True(run.ClearedAt > 0, $"рівень {n}: журнал робота наосліп не проходить (крок {run.Steps}, смерть {run.DiedAt})");
            Assert.Equal(level.AllGemsMask, run.Gems);
            var ms = run.ClearedAt * 20;
            var par = Par(n, ms, level.Par);
            VohnykRecord.Save(n, solution, new VohnykCheck(run.ClearedAt, run.Hash, VohnykRecord.Every, run.Hashes), par);
            output.WriteLine($"{n:00} «{level.Name}»: {run.ClearedAt} кроків = {ms / 1000.0:0.0} с, записів {solution.Length}, par {par / 1000} с");
        }
    }
}
