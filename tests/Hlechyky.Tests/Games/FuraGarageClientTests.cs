using System.Text.RegularExpressions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Чумаки», гараж 0.0.6 — клієнт (specs/fura.md, «Гараж (0.0.6) — клієнт»): браузер малює вигляд фури за id речей Лавки
/// «🚚 Фура» (model.js — фарби з відтінками й наліпки, audio.js — ріжки). Нова річ у каталозі без малюнка в браузері
/// мовчки нічого б не показала — тут ловимо розбіжність.
/// </summary>
public partial class FuraGarageClientTests
{
    [GeneratedRegex(@"export const (\w+) = \{(.*?)\};", RegexOptions.Singleline)]
    private static partial Regex Block();
    [GeneratedRegex(@"(?:^|[\s,{])(\w+):\s*(\d+|\{)", RegexOptions.Multiline)]
    private static partial Regex Key();
    [GeneratedRegex(@"const HORNS = \{(.*?)\n\};", RegexOptions.Singleline)]
    private static partial Regex Horns();
    [GeneratedRegex(@"^\s{2}(\w+): \{", RegexOptions.Multiline)]
    private static partial Regex HornKey();

    static string Web(string rel)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "web", "games"))) dir = Path.GetDirectoryName(dir);
        return File.ReadAllText(Path.Combine(dir ?? throw new InvalidOperationException("нема кореня"), "web", "games", "fura", rel));
    }

    static Dictionary<string, string> Exported(string js, string name)
    {
        var m = Block().Matches(js).First(x => x.Groups[1].Value == name);
        return Key().Matches(m.Groups[2].Value).ToDictionary(x => x.Groups[1].Value, x => x.Groups[2].Value);
    }

    static IEnumerable<LavkaItem> Kind(string kind) => LavkaCatalog.All.Where(i => i.Kind == kind);

    [Fact]
    public void Every_paint_has_the_same_hue_in_the_browser()
    {
        var paint = Exported(Web("model.js"), "PAINT");
        foreach (var it in Kind(LavkaKind.FuraPaint).Where(i => i.Art is int))
        {
            Assert.True(paint.ContainsKey(it.Id), $"фарба {it.Id} без відтінку в model.js PAINT");
            Assert.Equal(it.Art.ToString(), paint[it.Id]);
        }
        Assert.Contains(Kind(LavkaKind.FuraPaint), i => i.Id == "paintrainbow");     // «Веселка» — окремо, смугами
    }

    [Fact]
    public void Every_sticker_has_a_picture()
    {
        var stickers = Exported(Web("model.js"), "STICKERS");
        foreach (var it in Kind(LavkaKind.FuraSticker)) Assert.True(stickers.ContainsKey(it.Id), $"наліпка {it.Id} без малюнка в model.js");
    }

    [Fact]
    public void Every_horn_sounds()
    {
        var horns = HornKey().Matches(Horns().Match(Web("audio.js")).Groups[1].Value).Select(m => m.Groups[1].Value).ToHashSet();
        foreach (var it in Kind(LavkaKind.FuraHorn)) Assert.True(horns.Contains(it.Id), $"ріжок {it.Id} без звуку в audio.js HORNS");
    }
}
