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
            // par: не нижче за записане, і час автора ≤ 60 % par
            var par = Math.Max(level.Par, (int)Math.Ceiling(ms / 0.6 / 5000.0) * 5000);
            VohnykRecord.Save(n, solution, new VohnykCheck(run.ClearedAt, run.Hash, VohnykRecord.Every, run.Hashes), par);
            output.WriteLine($"{n:00} «{level.Name}»: {run.ClearedAt} кроків = {ms / 1000.0:0.0} с, записів {solution.Length}, par {par / 1000} с");
        }
    }
}
