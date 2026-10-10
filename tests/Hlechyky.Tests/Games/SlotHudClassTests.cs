using System.Text.RegularExpressions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Записки #42 (Smaug, «Розбиті глеки») і #43 (владік, «Однорукий Глек»), 10.10: після виграшу ≥ «Гарно!» чи Скарбнички
/// баланс унизу ліворуч зникав до перезавантаження. <c>ctx.payout(n, {glow:true})</c> чіпляв на <c>.sk-stat</c> балансу
/// клас <c>sk-glow</c>, а в kit.css <c>.sk-glow</c> — окремий шар підсвітки барабана (absolute, inset 0, <b>opacity 0</b>).
/// Клас стану, що кіт додає готовому елементу, не сміє мати власного правила — інакше елемент перебирає чужий вигляд.
/// </summary>
public partial class SlotHudClassTests
{
    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments();
    [GeneratedRegex(@"@keyframes[^{]*\{(?:[^{}]*\{[^}]*\})*[^}]*\}")]
    private static partial Regex Keyframes();
    [GeneratedRegex(@"([^{}@;]+)\{")]
    private static partial Regex Rules();
    // селектор — один клас (з псевдо): «.sk-glow», «.sk-glow:hover», «.sk-glow::after»
    [GeneratedRegex(@"^\.([A-Za-z_][\w-]*)(?::{1,2}[\w-]+(?:\([^)]*\))?)*$")]
    private static partial Regex Lone();
    // клас-модифікатор у складеному селекторі: «.sk-stat.sk-glow» → sk-glow
    [GeneratedRegex(@"(?<=[\w)\]-])\.([A-Za-z_][\w-]*)")]
    private static partial Regex Modifier();
    [GeneratedRegex(@"\brestart\(([^;]*?)\);")]
    private static partial Regex RestartCall();
    [GeneratedRegex(@"'([\w-]+)'")]
    private static partial Regex Quoted();

    static string SlotsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "web", "games"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? throw new InvalidOperationException("не знайшов корінь репозиторію"), "web", "games", "slots");
    }

    static IEnumerable<string> Selectors(string css)
    {
        var text = Keyframes().Replace(Comments().Replace(css, ""), "");
        foreach (Match m in Rules().Matches(text))
            foreach (var sel in m.Groups[1].Value.Split(','))
                if (sel.Trim() is { Length: > 0 } s) yield return s;
    }

    /// <summary>Класи, що мають своє правило «.клас { … }» — окремий елемент зі своїм виглядом.</summary>
    internal static HashSet<string> LoneClasses(IEnumerable<string> cssFiles) =>
        [.. cssFiles.SelectMany(Selectors).Select(s => Lone().Match(s)).Where(m => m.Success).Select(m => m.Groups[1].Value)];

    /// <summary>Класи, що в CSS ідуть станом іншого елемента: «.sk-stat.sk-glow …» → sk-glow.</summary>
    internal static HashSet<string> ModifierClasses(IEnumerable<string> cssFiles) =>
        [.. cssFiles.SelectMany(Selectors).SelectMany(s => Modifier().Matches(s)).Select(m => m.Groups[1].Value)];

    static List<string> SlotCss() => [.. Directory.GetFiles(SlotsDir(), "*.css").Select(File.ReadAllText)];

    [Fact]
    public void Payout_classes_on_the_balance_have_no_rule_of_their_own()
    {
        var js = File.ReadAllText(Path.Combine(SlotsDir(), "kit.js"));
        var at = js.IndexOf("ctx.payout = function", StringComparison.Ordinal);
        Assert.True(at >= 0, "у kit.js нема ctx.payout");
        var body = js[at..js.IndexOf("ctx.landFx", at, StringComparison.Ordinal)];
        var used = RestartCall().Matches(body).SelectMany(m => Quoted().Matches(m.Groups[1].Value)).Select(m => m.Groups[1].Value).ToList();
        Assert.Contains("sk-hit", used);
        Assert.True(used.Count >= 2, "payout мусить мати і звичайний, і яскравий клас: " + string.Join(", ", used));
        var lone = LoneClasses(SlotCss());
        var bad = used.Where(lone.Contains).ToList();
        Assert.True(bad.Count == 0, "клас балансу має власне правило в CSS слотів (баланс перебере його вигляд): " + string.Join(", ", bad));
    }

    [Fact]
    public void Every_restarted_class_in_the_kit_has_no_rule_of_their_own()
    {
        // restart(el, 'клас') — ефект на готовому елементі (кнопка, бульбашка, лічильник): такий клас — лише стан
        var js = File.ReadAllText(Path.Combine(SlotsDir(), "kit.js"));
        var used = RestartCall().Matches(js).SelectMany(m => Quoted().Matches(m.Groups[1].Value)).Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(used);
        var lone = LoneClasses(SlotCss());
        var bad = used.Where(lone.Contains).ToList();
        Assert.True(bad.Count == 0, "restart() чіпляє клас, що має власне правило в CSS слотів: " + string.Join(", ", bad));
    }

    [Fact]
    public void No_slot_class_is_both_a_layer_and_a_state()
    {
        var css = SlotCss();
        var both = LoneClasses(css).Intersect(ModifierClasses(css)).Order().ToList();
        Assert.True(both.Count == 0,
            "клас у CSS слотів — і окремий елемент («.x { … }»), і стан іншого («.y.x»): перейменуй стан\n" + string.Join("\n", both));
    }

    [Fact]
    public void The_check_catches_the_old_balance_glow()
    {
        // як було до виправлення (13d4c8b): шар підсвітки барабана й «яскравий» баланс — той самий клас
        string[] old =
        [
            ".sk-glow { position: absolute; inset: 0; opacity: 0; } .sk-reel.sk-tease > .sk-glow { opacity: 1; }"
            + " @keyframes sk-glow { to { opacity: .55; } }"
            + " .sk-stat.sk-glow b { animation: sk-hit .42s; } .sk-stat.sk-glow::after { animation: sk-glowa 1.3s; }"
            + " .sk-stat.sk-hit b { animation: sk-hit .3s; } .sk-cell.win .x { } .win .sg-cherry { }",
        ];
        Assert.Equal(["sk-glow"], LoneClasses(old).Intersect(ModifierClasses(old)).Order());
        Assert.DoesNotContain("sk-hit", LoneClasses(old));
        Assert.DoesNotContain("win", LoneClasses(old));
    }
}
