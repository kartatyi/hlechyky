using System.Text.RegularExpressions;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Крок прикрас (09.11, web/krok.js): вічні прикраси сайту — «🌈 Веселка» й рамка «Жива» з Лавки, пульси — крокують
/// 10 разів на секунду, а не на кожному кадрі монітора; смужки треку — без CSS-переходу. Плавно вони тримали
/// перемальовування на 180 кадрах/с на будь-якій сторінці, і YouTube на іншому моніторі губив кадри (GPU-процес у Chrome
/// один на всі вікна). Шви, які легко зламати непомітно, бо браузерних тестів нема: перейменована @keyframes мовчки
/// випаде з кроку, а повернений `transition: width` знову пустить шапку щокадру.
/// </summary>
public class DecorStepTests
{
    static string Web(string file) => File.ReadAllText(Paths.Resolve("web/" + file)).Replace("\r", "");

    /// <summary>Назви, які веде krok.js (рядки в лапках усередині <c>new Set([ … ])</c>).</summary>
    static string[] Stepped()
    {
        var js = Web("krok.js");
        var m = Regex.Match(js, @"const NAMES = new Set\(\[(?<b>[\s\S]*?)\]\);");
        Assert.True(m.Success, "у krok.js має бути const NAMES = new Set([...])");
        var body = Regex.Replace(m.Groups["b"].Value, @"//[^\n]*", "");
        return Regex.Matches(body, @"'([^']+)'").Select(x => x.Groups[1].Value).ToArray();
    }

    static string Rule(string css, string selector)
    {
        var m = Regex.Match(css, @"(?m)^" + Regex.Escape(selector) + @"\s*\{(?<b>[^}]*)\}");
        Assert.True(m.Success, $"у style.css нема правила «{selector}»");
        return m.Groups["b"].Value;
    }

    [Fact]
    public void Krok_is_loaded_on_every_page_before_app()
    {
        var html = Web("index.html");
        var krok = html.IndexOf("<script src=\"/krok.js\"></script>", StringComparison.Ordinal);
        var app = html.IndexOf("<script src=\"/app.js\"></script>", StringComparison.Ordinal);
        Assert.True(krok >= 0, "index.html має вантажити /krok.js");
        Assert.True(krok < app, "krok.js — до app.js: той малює ніки, а animationstart слухає вже krok");
    }

    [Fact]
    public void Lavka_looks_and_pulses_are_stepped()
    {
        var names = Stepped();
        foreach (var n in new[] { "lv-rainbow", "lv-slide", "lv-spin", "blink", "turnGlow", "gdot" })
            Assert.Contains(n, names);
    }

    [Fact]
    public void Every_stepped_name_is_a_real_keyframes()
    {
        var css = string.Join("\n", new[] { "static/style.css", "static/stats-news.css", "games/core.css", "games/dock.css" }.Select(Web));
        foreach (var n in Stepped())
            Assert.True(Regex.IsMatch(css, @"@keyframes\s+" + Regex.Escape(n) + @"\s*\{"),
                $"krok.js крокує «{n}», а такої @keyframes на сайті нема — перейменували? Тоді анімація знову йде щокадру");
    }

    [Fact]
    public void Track_bars_move_without_css_transition()
    {
        var css = Web("static/style.css");
        foreach (var sel in new[] { ".hdrprog > i", ".progress > div" })
            Assert.DoesNotContain("transition", Rule(css, sel));
        // ширину ставить app.js кроком «по пікселю», а не tick раз на секунду з переходом
        var app = Web("app.js");
        Assert.Contains("function paintBars()", app);
        Assert.Matches(@"barTimer = setTimeout\(paintBars, Math\.max\(100,", app);
    }

    /// <summary>Капсула музики (10.11, раунд hdr-music «Б»): смужка треку з часом — у самому блоці музики, а не через
    /// усю шапку; її малює paintNowInto, а paintBars переживає тишу, коли смужки нема.</summary>
    [Fact]
    public void Track_bar_lives_in_the_music_capsule()
    {
        var html = Web("index.html");
        var header = html[html.IndexOf("<header>", StringComparison.Ordinal)..html.IndexOf("</header>", StringComparison.Ordinal)];
        Assert.DoesNotContain("hdrProg", header);
        Assert.Contains("<div class=\"tools\">", header);
        var app = Web("app.js");
        Assert.Matches(@"class=""mini-pr""><span id=""hdrEl"">[^`]*id=""hdrProg""><i id=""hdrBar""", app);
        Assert.Contains("if (hb) hb.style.width = w;", app);
    }
}
