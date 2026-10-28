using System.Text.RegularExpressions;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Попап «Поставити стіл» (web/games/core.js) мусить показувати типові значення опцій. З 05.10 до 28.10 він ішов
/// через <c>opts.map(optHtml)</c>: map другим аргументом дає індекс, а <c>optHtml(o, cur)</c> брав його за «поточне» —
/// і в ~45 іграх стояло не типове (Хутори без таймера, Глеки «легкі» замість «звичайних», Покер з 4 ботами…).
/// </summary>
public class TablePopupDefaultsTests
{
    [Fact]
    public void Option_html_is_never_mapped_point_free()
    {
        var js = File.ReadAllText(Hlechyky.Paths.Resolve("web/games/core.js"));
        Assert.Contains("function optHtml(o, cur)", js);
        Assert.DoesNotMatch(new Regex(@"\.map\(\s*optHtml\s*\)"), js);
    }
}
