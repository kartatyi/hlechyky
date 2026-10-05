using System.Text.RegularExpressions;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Оболонка сайту (web/index.html, web/static/style.css, web/app.js, web/people.js) — шви, які легко зламати
/// непомітно, бо браузерних тестів нема:
/// <list type="bullet">
/// <item>записка #26: результати пошуку, підказка й рядки «закидаю…» висять під полем поверх сторінки. Повернеш їх
///   у потік — черга й поради Глека знову смикатимуться, а клік по результату губитиметься;</item>
/// <item>записка #21: 🎧 у шапці — кружечки людей з data-who (картка по кліку), а без вигляду з Лавки — порожні.</item>
/// </list>
/// </summary>
public class ShellSearchAndListenersTests
{
    static string Web(string file) => File.ReadAllText(Paths.Resolve("web/" + file)).Replace("\r", "");

    /// <summary>Тіло CSS-правила з точно таким селектором (перше входження).</summary>
    static string Rule(string css, string selector)
    {
        var m = Regex.Match(css, @"(?m)^" + Regex.Escape(selector) + @"\s*\{(?<b>[^}]*)\}");
        Assert.True(m.Success, $"у style.css нема правила «{selector}»");
        return m.Groups["b"].Value;
    }

    [Fact]
    public void Search_overlays_hang_under_the_input_row_not_in_flow()
    {
        var html = Web("index.html");
        var at = html.IndexOf("<div class=\"qbox\">", StringComparison.Ordinal);
        Assert.True(at >= 0, "поле пошуку має сидіти в .qbox");
        var rec = html.IndexOf("id=\"rec\"", at, StringComparison.Ordinal);
        Assert.True(rec > at);
        var box = html[at..rec];
        foreach (var part in new[] { "id=\"q\"", "id=\"results\"", "class=\"hintline\"", "id=\"adding\"" })
            Assert.Contains(part, box);

        var css = Web("static/style.css");
        Assert.Contains("position: relative", Rule(css, ".qbox"));
        Assert.Contains("position: absolute", Rule(css, ".results"));
        Assert.Contains("position: absolute", Rule(css, ".add .hintline"));
        Assert.Contains("position: absolute", Rule(css, ".adding"));
    }

    [Fact]
    public void Listeners_chip_draws_faces_with_card_on_click_and_blank_without_look()
    {
        var app = Web("app.js");
        var fn = app.IndexOf("function listenersHtml(", StringComparison.Ordinal);
        Assert.True(fn >= 0, "у app.js нема listenersHtml");
        var body = app[fn..app.IndexOf("\n  }", fn, StringComparison.Ordinal)];
        Assert.Contains("data-who=", body);
        Assert.Contains("'ava ls blank'", body);
        Assert.Contains("ls-more", body);
        // клік по кружечку відкриває картку, а не тост зі списком
        Assert.Matches(@"\$\('listeners'\)\.onclick = \(e\) => \{[^\n]*closest\('\[data-who\]'\)", app);

        // порожній кружечок — без літери; Лавка перемальовує з тими самими класами, тож blank переживає перевдягання
        var people = Web("people.js");
        Assert.Contains("const blank = /(^|\\s)blank(\\s|$)/.test(cls || '');", people);
        Assert.Contains(": blank ? '' :", people);
    }
}
