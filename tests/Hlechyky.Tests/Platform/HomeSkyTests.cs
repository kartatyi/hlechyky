using System.Text.RegularExpressions;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Пейзаж головної («Обрій»): малюнок, стилі й скрипт лежать у трьох файлах і зшиті лише назвами — класами в SVG та
/// змінними на &lt;html&gt;. Розійдуться — шар мовчки стане чорним або зникне, а браузерних тестів нема.
/// </summary>
public class HomeSkyTests
{
    static string Web(string file) => File.ReadAllText(Paths.Resolve("web/" + file)).Replace("\r", "");

    [Theory]
    [InlineData("static/obrii.svg")]
    public void Every_class_in_the_drawing_has_a_rule(string file)
    {
        var css = Web("static/polyva.css");
        var classes = Regex.Matches(Web(file), @"class=""(?<c>[^""]+)""")
            .SelectMany(m => m.Groups["c"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Distinct()
            .ToList();
        Assert.NotEmpty(classes);
        Assert.All(classes, c => Assert.True(Regex.IsMatch(css, @"\." + Regex.Escape(c) + @"(?![\w-])"), $"у polyva.css нема правила для .{c} з {file}"));
    }

    [Fact]
    public void Every_colour_the_layers_use_is_set_by_the_script_and_has_a_default()
    {
        var css = Web("static/polyva.css");
        var js = Web("sky.js");
        // лише правила шарів пейзажу: .ld-… і зупинки градієнтів .ob-s…
        var used = Regex.Matches(css, @"(?m)^\.(?:ld|ob)-[\w-]+[^{]*\{(?<body>[^}]*)\}")
            .SelectMany(r => Regex.Matches(r.Groups["body"].Value, @"var\((?<v>--[\w-]+)").Select(v => v.Groups["v"].Value))
            .Where(v => v != "--k" && v != "--rx" && v != "--bg")
            .Distinct()
            .ToList();
        Assert.NotEmpty(used);
        var root = Regex.Match(css, @"(?s):root\s*\{(?<b>[^}]*--sk0[^}]*)\}").Groups["b"].Value;
        Assert.All(used, v =>
        {
            Assert.True(js.Contains("'" + v + "'"), $"sky.js не ставить {v}");
            Assert.True(Regex.IsMatch(root, Regex.Escape(v) + @"\s*:"), $"у :root нема типового значення {v} (поки скрипт не відпрацював, шар чорний)");
        });
    }

    [Fact]
    public void The_scene_starts_at_the_top_of_the_page()
    {
        var html = Web("index.html");
        int sky = html.IndexOf("id=\"sky\"", StringComparison.Ordinal), header = html.IndexOf("<header>", StringComparison.Ordinal);
        Assert.True(sky >= 0 && sky < header, "сцена стоїть перед шапкою: шапка лежить у небі, а не над ним");
        // міст через Тетерів — те, за чим пейзаж упізнають
        Assert.Contains("class=\"ld-br\"", Web("static/obrii.svg"));
    }
}
