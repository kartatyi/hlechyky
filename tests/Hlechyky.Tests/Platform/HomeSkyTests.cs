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

    /// <summary>
    /// У чотирьох розділах угорі той самий пейзаж, що на головній (і тієї самої висоти), і назва розділу в небі. Зшито
    /// трьома файлами: розмітка назв (index.html), клас sky-sec на body (app.js) і правила під нього (polyva.css). За
    /// столом гри сцени бути не може — там потрібне місце.
    /// </summary>
    [Theory]
    [InlineData("viewEfir", "Ефір")]
    [InlineData("viewLib", "Бібліотека")]
    [InlineData("viewGames", "Ігри")]
    [InlineData("viewStats", "Хто скільки")]
    public void Each_section_opens_with_its_name_in_the_sky(string view, string title)
    {
        var html = Web("index.html");
        int at = html.IndexOf($"id=\"{view}\"", StringComparison.Ordinal);
        Assert.True(at >= 0, $"нема #{view}");
        var head = html.Substring(at, Math.Min(900, html.Length - at));
        Assert.Matches(@"class=""hm-skybox sec-top"">.*<h1>" + Regex.Escape(title) + "</h1>", head);
        Assert.Contains("data-sky-text", head);   // поруч із рядком розділу — погода, як на головній
    }

    [Fact]
    public void The_section_scene_is_switched_by_the_route_and_gives_way_to_a_game_table()
    {
        var css = Web("static/polyva.css");
        var js = Web("app.js");
        Assert.Matches(@"classList\.toggle\('sky-sec',[^;]*'efir'[^;]*'lib'[^;]*'games'[^;]*'stats'", js);
        Assert.Contains("body.sky-sec:not(.g-room) .sky-scene { display: block; }", css);
        Assert.Contains("body.sky-sec:not(.g-room) .sec-top { display: block; }", css);
        Assert.Matches(@"(?m)^\.sec-top \{ display: none; \}", css);   // поза сценою (за столом гри) назви розділу нема
        // сцена в розділах тієї самої висоти, що на головній
        var home = Regex.Match(css, @"(?m)^body\.route-home \{ (--sky-h: [^}]*)\}").Groups[1].Value.Trim();
        Assert.NotEqual("", home);
        Assert.Contains("body.sky-sec:not(.g-room) { " + home + " }", css);
        // жодне правило сцени розділів не діє за столом гри
        var loose = Regex.Matches(css, @"(?m)^\s*body\.sky-sec(?!:not\(\.g-room\))[^{]*\{").Select(m => m.Value).Where(r => !r.Contains(".hellobar")).ToList();
        Assert.True(loose.Count == 0, "правила сцени розділів без :not(.g-room): " + string.Join(" ", loose));
    }
}
