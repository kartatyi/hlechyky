using Hlechyky.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Живий процес edge-tts (<see cref="EdgeTtsEngine"/>, <see cref="EdgeWorker"/>): відмови поспіль прибирають процес, і
/// наступна репліка піднімає свіжий. Замість python — батник: відповідає як edge-worker.py, а «зламаний» він чи
/// «полагоджений», вирішує раз на старті (як модуль, імпортований у пам'ять), і кожен запуск дописує рядок у starts.txt.
/// </summary>
public class EdgeTtsWorkerTests
{
    public sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "фальшивий python — батник cmd"; }
    }

    const string FakePython = """
        @echo off
        echo started>>"%~dp0starts.txt"
        set fixed=0
        if exist "%~dp0fixed.flag" set fixed=1
        echo {"ready": true}
        :loop
        set "line="
        set /p "line="
        if not defined line exit /b 0
        if %fixed%==1 goto ok
        echo {"ok": false, "err": "WSServerHandshakeError: 403"}
        goto loop
        :ok
        echo {"ok": true}
        goto loop

        """;

    [WindowsFact]
    public async Task Worker_that_keeps_failing_is_replaced_and_picks_up_the_upgraded_edge_tts()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hlechyky-edge-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var python = Path.Combine(dir, "python.cmd");
            File.WriteAllText(python, FakePython.Replace("\n", "\r\n"));
            using var tts = new EdgeTtsEngine(new FixedOptions<TtsOptions>(new TtsOptions { Python = python, TimeoutSeconds = 10 }),
                new FixedOptions<YtDlpOptions>(new YtDlpOptions()), NullLogger<EdgeTtsEngine>.Instance);
            var n = 0;
            int Starts() => File.Exists(Path.Combine(dir, "starts.txt")) ? File.ReadAllLines(Path.Combine(dir, "starts.txt")).Length : 0;
            Task<bool> Say()
            {
                // «mp3» уже лежить: живий процес лише відповідає ok, файл за нього пише тест
                var path = Path.Combine(dir, $"line-{n++}.mp3");
                File.WriteAllText(path, "mp3");
                return tts.SynthesizeAsync("uk-UA-OstapNeural", "Привіт", "+0%", 0, path, default);
            }

            Assert.False(await Say());                                         // 403 — одна відмова ще не привід
            Assert.Equal(1, Starts());
            File.WriteAllText(Path.Combine(dir, "fixed.flag"), "");            // pip install -U edge-tts
            Assert.False(await Say());                                         // старий процес зі старим модулем — друга відмова поспіль
            Assert.Equal(1, Starts());
            Assert.True(await Say());                                          // прибраний — наступна репліка підняла свіжий
            Assert.Equal(2, Starts());
            Assert.True(await Say());
            Assert.Equal(2, Starts());                                         // а справний живе далі
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
