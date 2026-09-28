using System.Text.RegularExpressions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Прохід №3, п. 253: CSS-клас верхнього рівня (з нього починається селектор), визначений у стилях двох різних ігор, —
/// червоний тест. Так уже було з <c>.bclock</c> і <c>.dround</c>: стилі однієї гри ламали іншу, бо всі *.css
/// лежать на одній сторінці. Класи каркаса (core.css — <c>.gseat</c>, <c>.board</c>…) ігри доточують свідомо, їх не рахуємо;
/// файли однієї гри (<c>clicker.css</c> і <c>clicker-scene.css</c>) — одна гра.
/// </summary>
public partial class CssClashTests
{
    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments();
    [GeneratedRegex(@"@keyframes[^{]*\{(?:[^{}]*\{[^}]*\})*[^}]*\}")]
    private static partial Regex Keyframes();
    [GeneratedRegex(@"([^{}@;]+)\{")]
    private static partial Regex Rules();
    [GeneratedRegex(@"^\.([A-Za-z_][\w-]*)")]
    private static partial Regex Leading();

    static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "web", "games"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("не знайшов корінь репозиторію");
    }

    /// <summary>Клас → файли, де з нього починається хоч один селектор.</summary>
    internal static Dictionary<string, SortedSet<string>> LeadingClasses(IEnumerable<(string File, string Css)> files)
    {
        var map = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var (file, css) in files)
        {
            var text = Keyframes().Replace(Comments().Replace(css, ""), "");
            foreach (Match m in Rules().Matches(text))
                foreach (var sel in m.Groups[1].Value.Split(','))
                    if (Leading().Match(sel.Trim()) is { Success: true } lead)
                    {
                        if (!map.TryGetValue(lead.Groups[1].Value, out var set)) map[lead.Groups[1].Value] = set = [];
                        set.Add(file);
                    }
        }
        return map;
    }

    /// <summary>Гра, якій належить файл: «clicker-scene.css» → «clicker».</summary>
    static string GameOf(string file) => Path.GetFileNameWithoutExtension(file).Split('-')[0];

    internal static List<string> Clashes(IEnumerable<(string File, string Css)> files)
    {
        var all = files.ToList();
        var map = LeadingClasses(all);
        var core = map.Where(p => p.Value.Contains("core.css")).Select(p => p.Key).ToHashSet();
        return [.. map
            .Where(p => !core.Contains(p.Key) && p.Value.Select(GameOf).Distinct().Count() > 1)
            .Select(p => $".{p.Key}: {string.Join(", ", p.Value)}")
            .Order()];
    }

    [Fact]
    public void No_top_level_class_is_defined_by_two_games()
    {
        var dir = Path.Combine(FindRoot(), "web", "games");
        var files = Directory.GetFiles(dir, "*.css").Select(f => (Path.GetFileName(f), File.ReadAllText(f)));
        var clash = Clashes(files);
        Assert.True(clash.Count == 0,
            "Той самий клас верхнього рівня в стилях різних ігор — перейменуй із префіксом гри:\n" + string.Join("\n", clash));
    }

    [Fact]
    public void The_check_catches_a_shared_clock_but_not_core_or_same_game_files()
    {
        var clash = Clashes([
            ("core.css", ".gseat { color: red }"),
            ("chess.css", ".bclock { top: 0 } .gseat.x { color: blue } /* .dround { } */"),
            ("checkers.css", ".bclock:hover, .chk-a { top: 1px } @keyframes spin { from { opacity: 0 } to { opacity: 1 } }"),
            ("clicker.css", ".clk { }"),
            ("clicker-scene.css", ".clk .clk-x { }"),
        ]);
        Assert.Equal([".bclock: checkers.css, chess.css"], clash);
    }
}
