using System.Globalization;
using System.Text.RegularExpressions;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Оболонка сайту й лобі ігор — шви, які легко зламати непомітно, бо браузерних тестів нема:
/// <list type="bullet">
/// <item>записка #84: плашки над сторінкою (нічний відбій, 🚨 тривога, «Сайт оновився», «Назватись») зсувають main
///   униз, а висота колонки балачок рахувалась лише від вікна. Колонка — навіть згорнута — вилазила за низ, і сторінка
///   столу «в один екран» діставала зайву прокрутку на висоту плашок (104 px): стіл їхав під шапку. Тепер app.js міряє
///   плашки в --bars-h, балачки на стільки нижчі, а стіл кола стежить за плашками сам;</item>
/// <item>записка #80: ☆ «в мої» на плитці з картинкою на ПК було видно лише під мишкою — «пропала зірочка».</item>
/// </list>
/// </summary>
public class ShellBarsAndStarTests
{
    const string BarsSelector = "'body > .nightbar, body > .hellobar'";

    static string Web(string file) => File.ReadAllText(Paths.Resolve("web/" + file)).Replace("\r", "");

    /// <summary>Висоти правил «.chat { … }», що рахуються від вікна (100vh / 100dvh).</summary>
    static List<string> ChatHeights(string css) => Regex.Matches(css, @"\.chat\s*\{[^}]*\}")
        .SelectMany(r => Regex.Matches(r.Value, @"(?<![\w-])height:\s*(?<v>calc\([^;}]*)").Select(h => h.Groups["v"].Value))
        .Where(v => v.Contains("100vh") || v.Contains("100dvh"))
        .ToList();

    [Theory]
    [InlineData("static/style.css")]
    [InlineData("static/voice.css")]   // перебиває висоту зі style.css справжньою висотою шапки (--hdr-h)
    public void Chat_height_gives_way_to_the_bars_above_the_page(string file)
    {
        var heights = ChatHeights(Web(file));
        Assert.NotEmpty(heights);
        Assert.All(heights, v => Assert.Contains("var(--bars-h, 0px)", v));
    }

    [Fact]
    public void Bars_are_measured_and_the_wheel_table_watches_them()
    {
        var app = Web("app.js");
        Assert.Contains("setProperty('--bars-h'", app);
        Assert.Contains(BarsSelector, app);

        // Усе, що стоїть між шапкою й main, має клас, за яким плашки міряють: нова плашка без нього знову зсуне сторінку.
        var html = Web("index.html");
        int from = html.IndexOf("</header>", StringComparison.Ordinal), to = html.IndexOf("<main>", StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from, "у index.html між </header> і <main> стоять плашки");
        var bars = Regex.Matches(html[from..to], @"(?m)^<div\b[^>]*>");
        Assert.NotEmpty(bars);
        Assert.All(bars, (Match b) => Assert.Matches(@"class=""(nightbar|hellobar)\b", b.Value));

        // Стіл кола стоїть рівно в один екран: коли плашка зникає, висота body та сама (100dvh), тож за плашками
        // він стежить сам — інакше не доростав би назад.
        var clk = Web("games/clicker.js");
        var at = clk.IndexOf("function fitWatch(st)", StringComparison.Ordinal);
        Assert.True(at >= 0, "у clicker.js нема fitWatch");
        Assert.Contains(BarsSelector, clk.Substring(at, Math.Min(900, clk.Length - at)));
    }

    [Fact]
    public void Star_on_a_catalog_tile_is_visible_without_hover()
    {
        var css = Web("games/lobby.css");
        var opacities = Regex.Matches(css, @"(?m)^\s*\.gb \.gstar\s*\{[^}]*?opacity:\s*(?<o>[\d.]+)")
            .Select(m => double.Parse("0" + m.Groups["o"].Value, CultureInfo.InvariantCulture))
            .ToList();
        Assert.NotEmpty(opacities);
        Assert.All(opacities, o => Assert.True(o >= .5, $"☆ на плитці без мишки має бути видно (opacity {o})"));
    }
}
