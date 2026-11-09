using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Мемологія», 08.11.2026: зарахування за головними словами (довгий мем не встигали дописати), типові 30 с і 45 с,
/// кліп відео на розкритті й «⏭ Далі».
/// </summary>
public class MemyAnswerTests(ITestOutputHelper output)
{
    static JsonObject[] Bank() =>
    [
        MemyCache.M("u801", "ua", "Ляшко застряг у ліфті", ["ляшко в ліфті", "ми в ліфті застрягли"], "Це Ляшко, добрий день, ми в ліфті застрягли", [], 2016, 2, ["політика"]),
        MemyCache.M("u223", "ua", "Ляшко з вилами", [], null, [], 2014, 2, ["політика"]),
        MemyCache.M("u006", "ua", "Тракторні війська", ["трактор"], null, [], 2022, 1, ["війна"]),
        MemyCache.M("u234", "ua", "Кіт Степан", ["Степан"], null, [], 2022, 1, ["кіт"]),
        MemyCache.M("w003", "world", "Кіт за столом", ["Woman Yelling at a Cat"], null, [], 2019, 1, ["кіт"]),
        MemyCache.M("w035", "world", "Кіт, що кивав у такт", [], null, [], 2020, 2, ["кіт"]),
        MemyCache.M("u502", "ua", "Зеленський: «Ми всі тут»", [], "Ми всі тут", [], 2022, 1, ["війна"]),
        MemyCache.M("w015", "world", "Берні знову просить", [], "I am once again asking for your financial support", [], 2020, 1, ["політика"]),
        MemyCache.M("u099", "ua", "Взнала про внука", [], "Взнала, що внук голосував за «Регіони» — переписала хату на кота", [], 2012, 2, ["люди"]),
    ];

    static MemyBank Make() => MemyBank.Parse(MemyCache.Json(Bank()), null);

    static bool Hits(MemyBank b, string id, string text, bool phrase = false)
    {
        var m = b.Find(id)!;
        return MemyAnswer.Hits(text, phrase ? m.PhraseAnswers : m.NameAnswers, b);
    }

    [Theory]
    [InlineData("u801", "ляшко ліфт", true, true)]              // з прикладу у віджеті
    [InlineData("u801", "ліфт ляшко", true, true)]              // порядок не важить
    [InlineData("u801", "застрягли в ліфті", true, true)]
    [InlineData("u801", "ляшко", true, false)]                  // одного слова мало
    [InlineData("u801", "ляшко ліфт", false, true)]             // і в режимі «Назву»
    [InlineData("u223", "вила ляшко", false, true)]             // дві головні — обидва, але в будь-якому порядку
    [InlineData("u223", "вила", false, false)]
    [InlineData("u223", "ляшко", false, false)]
    [InlineData("u006", "трактори", false, true)]               // інша назва «трактор» — ціла
    [InlineData("u006", "війська трактори", false, true)]
    [InlineData("u006", "війська", false, false)]
    [InlineData("u234", "степан", false, true)]
    [InlineData("u234", "кіт", false, false)]                   // «кіт» загальне — у трьох мемах
    [InlineData("w035", "кіт кивав", false, true)]
    [InlineData("w035", "кіт", false, false)]
    [InlineData("u502", "всі тут", true, false)]                // самі службові — лише ціла фраза
    [InlineData("u502", "ми всі тут", true, true)]
    [InlineData("w015", "asking support", true, true)]
    [InlineData("w015", "financial support again", true, true)]
    [InlineData("u099", "хату на кота", true, true)]
    [InlineData("u099", "переписала хату", true, true)]
    [InlineData("u099", "внук", true, false)]
    public void Main_words_are_enough(string id, string text, bool phrase, bool hit)
    {
        Assert.Equal(hit, Hits(Make(), id, text, phrase));
    }

    [Fact]
    public void A_list_of_words_is_not_an_answer()
    {
        var b = Make();
        Assert.True(Hits(b, "u801", "ляшко ліфт"));
        Assert.False(Hits(b, "u801", "ляшко трактор степан кивав вила ліфт"));      // перелік навздогад
        Assert.False(Hits(b, "u801", "трактор степан"));                            // чужі слова
        Assert.False(Hits(b, "u801", ""));
        Assert.False(Hits(b, "u801", "   "));
    }

    [Fact]
    public void Same_word_ignores_endings_and_one_typo_in_five()
    {
        Assert.True(MemyAnswer.Same("ліфт", "ліфті"));
        Assert.True(MemyAnswer.Same("застряг", "застрягли"));
        Assert.True(MemyAnswer.Same("ляжко", "ляшко"));           // одна помилка на п'ять
        Assert.False(MemyAnswer.Same("кот", "кіт"));
        Assert.True(MemyAnswer.Same("хата", "хату"));
        Assert.False(MemyAnswer.Same("скоро", "скорсезе"));       // хвіст довшого — понад три літери
        Assert.False(MemyAnswer.Same("просто", "просить"));
    }

    /// <summary>
    /// Справжній банк: назва чи фраза одного мему не зараховується іншому — головні слова не роблять сусідів «правильними».
    /// Нема банку в репозиторії — тест нічого не каже.
    /// </summary>
    [Fact]
    public void Real_bank_one_memes_answer_does_not_hit_another()
    {
        var path = Path.Combine(Root(), "data", "memes", "memes.json");
        if (!File.Exists(path)) return;
        var b = MemyBank.Load(path, null);
        var bad = new List<string>();
        foreach (var m in b.Memes)
        {
            Assert.True(MemyAnswer.Hits(m.Name, m.NameAnswers, b), m.Id);
            if (m.Phrase is { } p) Assert.True(MemyAnswer.Hits(p, m.PhraseAnswers, b), m.Id);
            foreach (var x in b.Memes)
            {
                if (x.Id == m.Id) continue;
                foreach (var said in (string[])[x.Name, .. x.Phrase is null ? [] : new[] { x.Phrase }])
                    if (!SvoyaAnswer.Hits(said, m.NameAnswers) && MemyAnswer.Hits(said, m.NameAnswers, b))
                        bad.Add($"«{said}» ({x.Id}) → {m.Id} «{m.NameAnswers.First(a => MemyAnswer.Hits(said, [a], b))}»");
            }
        }
        foreach (var s in bad) output.WriteLine(s);
        Assert.Empty(bad);
    }

    static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "web", "games"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("не знайшов корінь репозиторію");
    }

    // ================================================================== кліп у банку

    static JsonObject WithClip(JsonObject m, object? clip)
    {
        m["clip"] = clip is null ? null : JsonSerializer.SerializeToNode(clip);
        return m;
    }

    [Fact]
    public void Bank_reads_clip_and_drops_only_a_bad_clip()
    {
        var ten = MemyCache.Ten();
        WithClip(ten[0], new { video = "https://www.youtube.com/watch?v=EUAIulcmNYI&t=30s", from = 34, to = 41 });
        WithClip(ten[1], new { video = "https://youtu.be/EUAIulcmNYI", from = 1, to = 5 });       // не канонічне
        WithClip(ten[2], new { video = "https://www.youtube.com/watch?v=EUAIulcmNYI", from = 10, to = 45 }); // задовгий
        WithClip(ten[3], new { video = "https://www.youtube.com/watch?v=EUAIulcmNYI", from = 3.5, to = 9 }); // не цілі
        var b = MemyBank.Parse(MemyCache.Json(ten), null);
        Assert.Equal(10, b.Memes.Count);                                        // мем лишається, лише без відео
        Assert.Equal(new MemyClip("EUAIulcmNYI", 34, 41), b.Find("w001")!.Clip);
        Assert.Null(b.Find("w002")!.Clip);
        Assert.Null(b.Find("w003")!.Clip);
        Assert.Null(b.Find("w004")!.Clip);
        Assert.Null(b.Find("w005")!.Clip);
        Assert.Equal(3, b.Problems.Count(p => p.Contains("clip", StringComparison.Ordinal)));
    }

    [Fact]
    public void Video_frame_meme_without_clip_plays_around_the_frame()
    {
        var m = MemyCache.M("u801", "ua", "Ляшко застряг у ліфті", [], null, [], 2016, 2, ["політика"]);
        m["image"] = JsonSerializer.SerializeToNode(new { video = "https://www.youtube.com/watch?v=EUAIulcmNYI", at = 36.6, page = "https://www.youtube.com/watch?v=EUAIulcmNYI" });
        var b = MemyBank.Parse(MemyCache.Json([m]), null);
        Assert.Equal(new MemyClip("EUAIulcmNYI", 34, 42), b.Find("u801")!.Clip);
    }

    /// <summary>09.11: «clip»: false — свідомо без відео, хоч кадр і з ролика (ТСН із запіканими титрами); не помилка банку.</summary>
    [Fact]
    public void Clip_false_means_no_video_even_for_a_frame_from_video()
    {
        var m = MemyCache.M("u040", "ua", "Поворознюк до Зеленського", [], "Вова, їбаш їх", [], 2019, 2, ["політика"]);
        m["image"] = JsonSerializer.SerializeToNode(new { video = "https://www.youtube.com/watch?v=EUAIulcmNYI", at = 6, page = "https://www.youtube.com/watch?v=EUAIulcmNYI" });
        m["clip"] = false;
        var b = MemyBank.Parse(MemyCache.Json([m]), null);
        Assert.Null(b.Find("u040")!.Clip);
        Assert.DoesNotContain(b.Problems, p => p.Contains("clip", StringComparison.Ordinal));
    }

    /// <summary>09.11: «можна і більше показувати» — короткий ролик 404 (25 с) і кліп на всю стелю (30 с) грають до кінця.</summary>
    [Fact]
    public void Long_clip_plays_to_the_end()
    {
        var ten = MemyCache.Ten();
        WithClip(ten[0], new { file = "https://404.com.ua/rails/active_storage/blobs/proxy/abc/short.mp4", from = 0, to = 25 });
        WithClip(ten[1], new { video = "https://www.youtube.com/watch?v=EUAIulcmNYI", from = 0, to = 30 });
        var b = MemyBank.Parse(MemyCache.Json(ten), null);
        Assert.Empty(b.Problems);
        Assert.Equal(28_000, Memy.RevealFor(b.Find("w001")));
        Assert.Equal(33_000, Memy.RevealFor(b.Find("w002")));
    }

    // ================================================================== за столом

    static MemyBank ClipBank()
    {
        var ten = MemyCache.Ten();
        foreach (var m in ten) WithClip(m, new { video = "https://www.youtube.com/watch?v=EUAIulcmNYI", from = 34, to = 42 });
        return MemyBank.Parse(MemyCache.Json(ten), null);
    }

    [Fact]
    public void Default_round_is_30_seconds_and_45_can_be_picked()
    {
        var info = new Memy().Info;
        Assert.Equal(["15", "20", "30", "45"], info.Options!.Single(o => o.Key == "seconds").Values.Select(v => v.Value));
        Assert.Equal("30", info.Options!.Single(o => o.Key == "seconds").Default);
        using var c = new MemyCache();
        var h = MemyTests.Table(c, 2);
        Assert.Equal(30, h.View(null).GetProperty("seconds").GetInt32());
        Assert.Equal(30_000, h.View(null).GetProperty("totalMs").GetInt32());
        var h45 = MemyTests.Table(c, 2, new { seconds = "45" });
        Assert.Equal(45_000, h45.View(null).GetProperty("totalMs").GetInt32());
    }

    [Fact]
    public void Reveal_with_clip_lasts_for_the_clip_and_shows_it_only_after_the_round()
    {
        using var c = new MemyCache(ClipBank());
        var h = MemyTests.Table(c, 2);
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("answer").ValueKind);   // кліпу до розкриття нема
        Assert.DoesNotContain("EUAIulcmNYI", h.View(0).GetRawText(), StringComparison.Ordinal);
        MemyTests.Until(h, Memy.PhaseReveal);
        var clip = h.View(0).GetProperty("answer").GetProperty("clip");
        Assert.Equal(("EUAIulcmNYI", 34, 42), (clip.GetProperty("id").GetString(), clip.GetProperty("from").GetInt32(), clip.GetProperty("to").GetInt32()));
        Assert.Equal(8_000 + Memy.RevealClipLead, h.View(null).GetProperty("totalMs").GetInt32());
        Assert.Equal(11_000, Memy.RevealFor(c.Bank.Find("w001")));
        Assert.Equal(Memy.RevealMs, Memy.RevealFor(MemyCache.TenBank().Find("w001")));
    }

    [Fact]
    public void Next_from_everyone_cuts_the_reveal_short_and_is_a_toggle()
    {
        using var c = new MemyCache(ClipBank());
        var h = MemyTests.Table(c, 2);
        MemyTests.Until(h, Memy.PhaseReveal);
        var round = h.View(null).GetProperty("round").GetInt32();
        Assert.True(h.Act(0, "skip").Ok);
        Assert.Equal([0], h.View(null).GetProperty("next").EnumerateArray().Select(x => x.GetInt32()));
        Assert.True(h.Act(0, "skip").Ok);                                         // передумав
        Assert.Empty(h.View(null).GetProperty("next").EnumerateArray());
        Assert.True(h.Act(0, "skip").Ok);
        h.Tick(2);
        Assert.Equal(Memy.PhaseReveal, MemyTests.Phase(h));                        // другий ще дивиться
        Assert.True(h.Act(1, "skip").Ok);
        h.Tick(1);
        Assert.Equal(Memy.PhasePlay, MemyTests.Phase(h));
        Assert.Equal(round + 1, h.View(null).GetProperty("round").GetInt32());
        Assert.Empty(h.View(null).GetProperty("next").EnumerateArray());
    }

    [Fact]
    public void Played_list_at_the_end_carries_clips()
    {
        using var c = new MemyCache(ClipBank());
        var h = MemyTests.Table(c, 1, new { rounds = "5" });
        for (var i = 0; i < 5; i++)
        {
            MemyTests.Until(h, Memy.PhaseReveal);
            if (i < 4) { h.Act(0, "skip"); h.Tick(1); }
        }
        MemyTests.Until(h, Memy.PhaseDone);
        var played = h.View(0).GetProperty("played").EnumerateArray().ToList();
        Assert.Equal(5, played.Count);
        Assert.All(played, p => Assert.Equal("EUAIulcmNYI", p.GetProperty("clip").GetProperty("id").GetString()));
    }
}
