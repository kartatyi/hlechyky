using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Поради Дядька Глека (аналітика кола, етап 4): книга фраз з репозиторію читається без зауважень і має кожен вид,
/// рід ставиться за списком She, «потім» ховає пораду (і переживає збереження), підказка в моменті приходить у виді
/// й не повторюється раніше за свій час, і жодна фраза не лишає незаповнених {дужок}.
/// </summary>
public class ClickerGlekTests
{
    static string BookPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "data", "clicker", "glek-tips.json");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("data/clicker/glek-tips.json");
    }

    static ClickerGlekBook Book() => ClickerGlekBook.Parse(File.ReadAllText(BookPath())).Book;

    [Fact]
    public void The_shipped_book_parses_cleanly_and_covers_every_kind()
    {
        var (book, problems) = ClickerGlekBook.Parse(File.ReadAllText(BookPath()));
        Assert.Empty(problems);
        foreach (var kind in ClickerGlekBook.Allowed.Keys) Assert.True(book.Count(kind) >= 3, kind);
    }

    [Fact]
    public void Unknown_placeholders_and_kinds_are_reported_not_shipped()
    {
        var (book, problems) = ClickerGlekBook.Parse("""{ "fire": ["Палити? {oops}"], "clay-play": ["Постав {clay} {від|від"], "dance": ["?"] }""");
        Assert.Contains(problems, p => p.StartsWith("fire:") && p.Contains("{oops}"));
        Assert.Contains(problems, p => p.StartsWith("clay-play:"));
        Assert.Contains(problems, p => p.StartsWith("dance:"));
        Assert.Equal(0, book.Count("fire"));
    }

    [Fact]
    public void Fill_puts_numbers_and_the_right_gender()
    {
        var vars = new Dictionary<string, string> { ["gain"] = "×2,3", ["clay"] = "чорну глину" };
        Assert.Equal("Ти {пішов|пішла}? Постав чорну глину — ×2,3".Replace("{пішов|пішла}", "пішла"),
            ClickerGlekBook.Fill("Ти {пішов|пішла}? Постав {clay} — {gain}", vars, she: true));
        Assert.Equal("Ти пішов? Постав чорну глину — ×2,3", ClickerGlekBook.Fill("Ти {пішов|пішла}? Постав {clay} — {gain}", vars, she: false));
        Assert.Equal("×2,6", Clicker.GlekGain(2.55));
        Assert.Equal("+13 %", Clicker.GlekGain(1.134));
    }

    // ---------- у колі ----------

    static RoomHarness Wheel(ClickerGlekOptions? o = null, Action<JsonObject>? edit = null)
    {
        var glek = new ClickerGlekService(() => o ?? new ClickerGlekOptions { She = ["Smaug"] }) { Fixed = Book() };
        var sp = new ServiceCollection().AddSingleton(glek).BuildServiceProvider();
        var h = new RoomHarness("clicker", services: sp);
        h.Clock.UtcNow = new DateTimeOffset(2026, 10, 25, 17, 0, 0, TimeSpan.Zero);
        h.Solo("Назар");
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            node["upgrades"] = new JsonObject { ["apprentice"] = 60, ["kiln"] = 60, ["workshop"] = 80, ["fair"] = 40, ["wheel"] = 60, ["flywheel"] = 4, ["basket"] = 5 };
            node["pots"] = 1e9;
            node["total"] = 1e10;
            node["fallStreak"] = 2000;
            node["house"] = new JsonObject { ["clays"] = new JsonArray("white", "black", "blue"), ["clay"] = "white" };
            edit?.Invoke(node);
            h.Room.Game.Load(node.ToJsonString());
        }
        return h;
    }

    static List<ClickerGlekTip> Card(RoomHarness h)
    {
        lock (h.Room.Sync)
        {
            var g = (Clicker)h.Room.Game;
            var m = typeof(Clicker).GetMethod("GlekCard", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            return (List<ClickerGlekTip>)m.Invoke(g, [g.Advise(), h.Clock.UtcNow])!;
        }
    }

    [Fact]
    public void The_card_speaks_in_words_with_numbers_and_later_hides_a_tip_for_a_day_even_after_a_reload()
    {
        var h = Wheel();
        var card = Card(h);
        Assert.InRange(card.Count, 1, Clicker.GlekCardSize);
        Assert.All(card, t => Assert.DoesNotContain("{", t.Text));
        var top = card[0].Kind;
        // У новачка альбом і села важать більше за глину — відкладаємо їх «потім», доки глина не вийде на картку.
        for (var i = 0; i < 8 && card.All(t => t.Kind != "clay-play"); i++)
        {
            Assert.True(h.Act(0, "glek", new { later = card[0].Kind }).Ok);
            card = Card(h);
        }
        var clay = card.Single(t => t.Kind == "clay-play");
        Assert.Contains("чорн", clay.Text);
        Assert.Equal("clay:black", clay.Where);

        Assert.True(h.Act(0, "glek", new { later = "clay-play" }).Ok);
        Assert.DoesNotContain(Card(h), t => t.Kind == "clay-play");
        lock (h.Room.Sync) h.Room.Game.Load(h.Room.Game.Save()!);                 // «потім» пережило збереження
        Assert.DoesNotContain(Card(h), t => t.Kind == "clay-play");
        h.Clock.Advance(TimeSpan.FromHours(25));
        Assert.Equal(top, Card(h)[0].Kind);                                  // через добу відкладене вертається
        Assert.False(h.Act(0, "glek", new { later = "dance" }).Ok);
    }

    static JsonElement? Moment(RoomHarness h) =>
        h.View(0).TryGetProperty("glek", out var g) && g.ValueKind == JsonValueKind.Object ? g : null;

    [Fact]
    public void At_the_wheel_on_white_the_moment_tip_offers_black_once_per_its_time()
    {
        var h = Wheel(new ClickerGlekOptions { MomentMinutes = 20 });
        Assert.True(h.Act(0, "spin", PotterHands.Human(5)).Ok);
        var g = Moment(h);
        Assert.NotNull(g);
        Assert.Equal("clay-play", g!.Value.GetProperty("kind").GetString());
        Assert.Equal("clay:black", g.Value.GetProperty("where").GetString());
        var id = g.Value.GetProperty("id").GetString();
        Assert.Equal(id, Moment(h)!.Value.GetProperty("id").GetString());   // той самий вид — та сама підказка
        h.Clock.Advance(20);
        Assert.True(h.Act(0, "spin", PotterHands.Human(5)).Ok);
        Assert.Null(Moment(h));                                              // уже казав — мовчить до свого часу
        h.Clock.Advance(TimeSpan.FromMinutes(21));
        Assert.True(h.Act(0, "spin", PotterHands.Human(5)).Ok);
        Assert.Equal("clay-play", Moment(h)!.Value.GetProperty("kind").GetString());
    }

    [Fact]
    public void Forming_on_blue_on_purpose_is_left_alone_and_a_quiet_potter_on_black_hears_about_white()
    {
        var blue = Wheel(edit: n => n["house"]!["clay"] = "blue");
        Assert.True(blue.Act(0, "spin", PotterHands.Human(5)).Ok);
        Assert.Null(Moment(blue));

        var black = Wheel(edit: n => n["house"]!["clay"] = "black");
        Assert.True(black.Act(0, "spin", PotterHands.Human(5)).Ok);
        black.Clock.Advance(TimeSpan.FromMinutes(3));
        var g = Moment(black);
        Assert.Equal("clay-leave", g!.Value.GetProperty("kind").GetString());
        Assert.Contains("біл", g.Value.GetProperty("text").GetString());
    }

    [Fact]
    public void Switching_the_clay_refreshes_the_card_in_the_diary_at_once()
    {
        var glek = new ClickerGlekService(() => new ClickerGlekOptions()) { Fixed = Book() };
        var diary = new ClickerDiaryService(null, () => new ClickerDiaryOptions(), new FakeClock(), person: _ => true);
        var h = new RoomHarness("clicker", services: new ServiceCollection().AddSingleton(glek).AddSingleton(diary).BuildServiceProvider());
        h.Clock.UtcNow = new DateTimeOffset(2026, 10, 25, 17, 0, 0, TimeSpan.Zero);
        h.Solo("Назар");
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            node["upgrades"] = new JsonObject { ["apprentice"] = 60, ["kiln"] = 60, ["workshop"] = 80, ["wheel"] = 60, ["flywheel"] = 4, ["basket"] = 5 };
            node["fallStreak"] = 2000;
            node["album"] = null;
            node["house"] = new JsonObject { ["clays"] = new JsonArray("white", "black"), ["clay"] = "white" };
            h.Room.Game.Load(node.ToJsonString());
        }
        // Усе, крім глини, — «потім», щоб картка була про глину.
        foreach (var k in ClickerGlekBook.Allowed.Keys.Where(k => k != "clay-play")) h.Act(0, "glek", new { later = k });
        Assert.True(h.Act(0, "spin", PotterHands.Human(5)).Ok);
        static string[] Kinds(ClickerDiaryService d) =>
            [.. JsonDocument.Parse(d.Diary("Назар")[^1].Json).RootElement.GetProperty("glek").EnumerateArray().Select(t => t.GetProperty("kind").GetString()!)];
        Assert.Contains("clay-play", Kinds(diary));
        h.Clock.Advance(30);
        Assert.True(h.Act(0, "knead", new { kind = "black" }).Ok);
        Assert.DoesNotContain("clay-play", Kinds(diary));
    }

    [Fact]
    public void Switched_off_glek_is_silent()
    {
        var h = Wheel(new ClickerGlekOptions { Enabled = false });
        Assert.True(h.Act(0, "spin", PotterHands.Human(5)).Ok);
        Assert.Null(Moment(h));
        Assert.Empty(Card(h));
    }

    [ClickerSavesFact]
    public void Every_tip_for_the_three_potters_reads_without_holes()
    {
        var dir = Environment.GetEnvironmentVariable("CLK_ADVISE_SAVES")!;
        foreach (var nick in new[] { "smaug", "владік", "назар" })
        {
            var json = File.ReadAllText(Path.Combine(dir, $"save-{nick}.json"));
            var glek = new ClickerGlekService(() => new ClickerGlekOptions { She = ["Smaug"] }) { Fixed = Book() };
            var h = new RoomHarness("clicker", services: new ServiceCollection().AddSingleton(glek).BuildServiceProvider());
            h.Clock.UtcNow = DateTimeOffset.Parse(JsonNode.Parse(json)!["lastSync"]!.GetValue<string>());
            h.Solo(nick == "smaug" ? "Smaug" : nick);
            lock (h.Room.Sync) h.Room.Game.Load(json);
            var g = (Clicker)h.Room.Game;
            ClickerAdvice a;
            lock (h.Room.Sync) a = g.Advise();
            var m = typeof(Clicker).GetMethod("GlekText", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            foreach (var t in a.Tips)
            {
                ClickerGlekTip? tip;
                lock (h.Room.Sync) tip = (ClickerGlekTip?)m.Invoke(g, [t, h.Clock.UtcNow]);
                Assert.NotNull(tip);
                Assert.DoesNotContain("{", tip!.Text);
                Assert.DoesNotContain("  ", tip.Text);
            }
            Assert.NotEmpty(Card(h));
        }
    }
}
