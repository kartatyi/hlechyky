namespace Hlechyky.Tests.Platform;

/// <summary>
/// Відбиток фронту (/api/front): за ним відкрита сторінка після деплою бачить, який файл змінився, — і перевантажує
/// лише модуль гри або показує плашку, замість того щоб людина тиснула F5 і рвала собі музику.
/// </summary>
public sealed class FrontPrintTests : IDisposable
{
    readonly string _web = Path.Combine(Path.GetTempPath(), "hl-front-" + Guid.NewGuid().ToString("N"));

    public FrontPrintTests()
    {
        Directory.CreateDirectory(Path.Combine(_web, "games"));
        File.WriteAllText(Path.Combine(_web, "app.js"), "let a = 1;");
        File.WriteAllText(Path.Combine(_web, "index.html"), "<p>");
        File.WriteAllText(Path.Combine(_web, "games", "runner.js"), "run();");
        File.WriteAllText(Path.Combine(_web, "games", "runner.css"), ".r{}");
        File.WriteAllText(Path.Combine(_web, "icon.svg"), "<svg/>");
    }

    public void Dispose() { try { Directory.Delete(_web, true); } catch { /* тимчасова тека — не біда */ } }

    FrontPrint Print() => new(_web, TimeSpan.Zero);

    [Fact]
    public void Lists_scripts_styles_and_pages_by_web_path()
    {
        var files = Print().Files();
        Assert.Equal(["app.js", "games/runner.css", "games/runner.js", "index.html"], files.Keys.Order());
        Assert.All(files.Values, h => Assert.Matches("^[0-9a-f]{12}$", h));
    }

    [Fact]
    public void Only_the_rewritten_file_changes_its_print()
    {
        var print = Print();
        var before = print.Files();
        File.WriteAllText(Path.Combine(_web, "games", "runner.js"), "run(); jump();");
        var after = print.Files();
        Assert.NotEqual(before["games/runner.js"], after["games/runner.js"]);
        Assert.Equal(before["app.js"], after["app.js"]);
        Assert.Equal(before["games/runner.css"], after["games/runner.css"]);
    }

    [Fact]
    public void Same_content_gives_the_same_print_after_a_restart()
    {
        var first = Print().Files();
        File.SetLastWriteTimeUtc(Path.Combine(_web, "app.js"), DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal(first, Print().Files());
    }

    [Fact]
    public void Only_the_current_print_lets_a_module_be_cached_forever()
    {
        var print = Print();
        var v = print.Files()["games/runner.js"];
        Assert.True(print.Matches("/games/runner.js", v));     // так його питає Program.cs — шлях запиту
        Assert.True(print.Matches("games/runner.js", v));
        Assert.False(print.Matches("/games/runner.js", "20260927"));
        Assert.False(print.Matches("/games/runner.js", ""));
        Assert.False(print.Matches("/games/nope.js", v));
        Assert.False(print.Matches("/games/../app.js", print.Files()["app.js"]));
        // Деплой між каталогом і запитом: старий відбиток більше не «назавжди» — віддамо свіже з no-cache.
        File.WriteAllText(Path.Combine(_web, "games", "runner.js"), "run(); jump();");
        Assert.False(print.Matches("/games/runner.js", v));
    }

    [Fact]
    public void Catalog_gets_prints_of_game_modules_only() =>
        Assert.Equal(["games/runner.css", "games/runner.js"], Print().Games().Keys.Order());

    [Fact]
    public void Missing_web_folder_gives_an_empty_print() =>
        Assert.Empty(new FrontPrint(Path.Combine(_web, "nope"), TimeSpan.Zero).Files());
}
