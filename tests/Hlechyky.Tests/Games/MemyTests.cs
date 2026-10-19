using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Фікстура «Мемології»: банк із десяти мемів (за контрактом specs/memy.md §4, через справжній <see cref="MemyBank.Parse"/>)
/// і тимчасовий кеш, де кожен мем уже «скачаний» трьома JPEG-кадрами. Без мережі й без ffmpeg; теку видаляє Dispose.
/// </summary>
public sealed class MemyCache : IDisposable
{
    public static JsonObject M(string id, string cat, string name, string[] names, string? phrase, string[] phrases, int year, int d, string[] tags) =>
        JsonSerializer.SerializeToNode(new
        {
            id, cat, name, names, phrase, phrases, year, difficulty = d, tags,
            about = $"Звідки «{name}»: вигаданий опис для тестів.",
            image = new { url = $"https://i.example.com/{id}.png", page = $"https://knowyourmeme.com/memes/{id}" },
            crop = (double[]?)null,
            sources = new[] { $"https://knowyourmeme.com/memes/{id}" },
        })!.AsObject();

    /// <summary>Десять мемів: шість світових, три українські, один старий; із фразою — чотири (w002, w004, w005, o001).</summary>
    public static JsonObject[] Ten() =>
    [
        M("w001", "world", "Хлопець озирається", ["Distracted Boyfriend", "невірний хлопець"], null, [], 2017, 1, ["люди", "стокове фото"]),
        M("w002", "world", "Doge", ["доге"], "Such wow", ["дуже вау"], 2013, 1, ["пес"]),
        M("w003", "world", "Кіт за столом", ["Woman Yelling at a Cat"], null, [], 2019, 1, ["кіт", "люди"]),
        M("w004", "world", "Grumpy Cat", ["сердитий кіт"], "No", ["ні"], 2012, 2, ["кіт"]),
        M("w005", "world", "Пес у палаючій кімнаті", ["This is fine dog"], "This is fine", ["усе гаразд"], 2013, 1, ["пес", "комікс"]),
        M("w006", "world", "Stonks", ["стонкс"], null, [], 2017, 2, ["люди", "техніка"]),
        M("u001", "ua", "Козак із люлькою", ["козак"], null, [], 2005, 2, ["кіно", "люди"]),
        M("u002", "ua", "Кіт Степан", ["Степан"], null, [], 2022, 1, ["кіт"]),
        M("u003", "ua", "Бавовна", ["хлопок"], null, [], 2022, 1, ["новини"]),
        M("o001", "old", "Trollface", ["тролфейс"], "Problem?", ["проблем"], 2008, 3, ["комікс"]),
    ];

    public static string Json(IEnumerable<JsonNode> memes) =>
        new JsonObject { ["version"] = 1, ["memes"] = new JsonArray([.. memes]) }.ToJsonString();

    public static MemyBank TenBank() => MemyBank.Parse(Json(Ten()), null);

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "memy-test-" + Guid.NewGuid().ToString("N")[..10]);
    public MemyBank Bank { get; }
    public FakeClock Clock { get; } = new();
    public MemyImages Images { get; }

    /// <param name="ready">Які меми вже «скачані»; null — усі.</param>
    public MemyCache(MemyBank? bank = null, Func<MemyMeme, bool>? ready = null)
    {
        Bank = bank ?? TenBank();
        Directory.CreateDirectory(Dir);
        foreach (var m in Bank.Memes) if (ready?.Invoke(m) ?? true) Put(m);
        Images = MemyImages.Offline(Bank, Dir, Clock);
    }

    /// <summary>Три кадри мему на диск: крихітний справжній JPEG (той самий, що в тестах «Де це?»).</summary>
    void Put(MemyMeme m)
    {
        for (var f = 0; f < MemyImages.Frames; f++) File.WriteAllBytes(Path.Combine(Dir, MemyImages.FileNameFor(m, f)), GeoCache.Jpeg);
    }

    /// <summary>Докласти мем на диск і перечитати теку (імітація завантажувача).</summary>
    public void Add(string id)
    {
        Put(Bank.Find(id)!);
        Images.Rescan();
    }

    public IServiceProvider Services(TempDb? db = null)
    {
        var s = new ServiceCollection().AddSingleton(Images);
        if (db is not null) s.AddSingleton(db.Db);
        return s.BuildServiceProvider();
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// «Мемологія» і тренування (docs/games/specs/memy.md): паспорт, банк, проявлення й токени, варіанти, очки, блок,
/// «Підпис», приховане до розкриття, кінці, детермінізм, рематч, соло, пам'ять баченого. Партія живе від тика
/// (500 мс): «прокрути годинник і подивись».
/// </summary>
public class MemyTests
{
    const int MaxTicks = 2000;

    internal static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марта", "Богдан", "Леся", "Остап", "Ніна", "Юрко", "Дарка", "Тарас"];

    internal static RoomHarness Table(MemyCache c, int players = 2, object? options = null, int seed = 42, bool start = true, TempDb? db = null)
    {
        var h = new RoomHarness("memy", options, seed, c.Services(db));
        for (var i = 0; i < players; i++) h.Join(Nicks[i]);
        if (start) Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    internal static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;

    internal static bool Playing(RoomHarness h) => h.Rooms.Find(h.RoomId) is { Status: RoomStatus.Playing };

    internal static void Until(RoomHarness h, string phase)
    {
        for (var i = 0; i < MaxTicks && Playing(h) && Phase(h) != phase; i++) h.Tick(1);
    }

    /// <summary>Прокрутити годинник на стільки секунд (тик — 500 мс).</summary>
    internal static void Sec(RoomHarness h, double seconds) => h.Tick((int)Math.Round(seconds * 2));

    internal static string Token(string url) => url["/api/games/memy/".Length..^4];

    /// <summary>Мем поточного раунду — через токен картинки, як його бачить сервер (клієнт цього не може).</summary>
    internal static MemyMeme Current(RoomHarness h, MemyCache c)
    {
        var path = c.Images.Resolve(Token(h.View(null).GetProperty("image").GetString()!))!;
        return c.Bank.Memes.First(m => Enumerable.Range(0, MemyImages.Frames).Any(f => c.Images.PathFor(m, f) == path));
    }

    static string Mode(RoomHarness h) => h.View(null).GetProperty("mode").GetString()!;

    /// <summary>Правильна відповідь раунду: фраза в режимі «Підпис», інакше назва.</summary>
    internal static string Right(RoomHarness h, MemyCache c) => Mode(h) == Memy.WhatPhrase ? Current(h, c).Phrase! : Current(h, c).Name;

    static int Score(RoomHarness h, int seat) => h.View(null).GetProperty("scores")[seat].GetInt32();

    static string[]? Choices(RoomHarness h) =>
        h.View(null).GetProperty("choices") is { ValueKind: JsonValueKind.Array } a ? [.. a.EnumerateArray().Select(x => x.GetString()!)] : null;

    static int RightIndex(RoomHarness h, MemyCache c) => Array.IndexOf(Choices(h)!, Right(h, c));

    static int WrongIndex(RoomHarness h, MemyCache c) => RightIndex(h, c) == 0 ? 1 : 0;

    static ActResult Write(RoomHarness h, int seat, string text) => h.Act(seat, "guess", new { text });

    // ================================================================== паспорт

    [Fact]
    public void Passport_and_options_are_as_in_the_spec()
    {
        var info = new Memy().Info;
        Assert.Equal(("memy", GameGroup.Party, 1, 12, 500), (info.Id, info.Group, info.MinPlayers, info.MaxPlayers, info.TickMs));
        Assert.Equal(StartMode.ByHost, info.Start);
        Assert.True(info.Hidden);
        Assert.Equal(ScoreOrder.HigherIsBetter, info.Score);
        Assert.Equal(["rounds", "seconds", "what", "answer", "choicesAt", "show", "cat", "level"], info.Options!.Select(o => o.Key));
        Assert.Equal(["10", "20", "mix", "both", "8", "pixels", "all", "all"], info.Options!.Select(o => o.Default));
        Assert.True(info.Options!.Single(o => o.Key == "cat").Multi);
        Assert.Equal(["5", "10", "15"], info.Options!.Single(o => o.Key == "rounds").Values.Select(v => v.Value));
        Assert.Equal(["15", "20", "30"], info.Options!.Single(o => o.Key == "seconds").Values.Select(v => v.Value));
        Assert.Equal("1", new Memy().SeatName(0));
        Assert.Equal("12", new Memy().SeatName(11));

        var solo = new MemySolo().Info;
        Assert.Equal(("memy-solo", GameGroup.Solo, 1, 1, "memy"), (solo.Id, solo.Group, solo.MinPlayers, solo.MaxPlayers, solo.Module));
        Assert.True(solo.Private);
        Assert.Equal(ScoreOrder.HigherIsBetter, solo.Score);
        Assert.Contains(RoomHarness.NewRegistry().Catalog, g => g.Id == "memy-solo");
    }

    [Fact]
    public void Achievements_are_in_the_catalog()
    {
        Assert.Contains(Hlechyky.Games.Economy.AchievementCatalog.All, a => a.Key == Memy.AchWin && a.Title == "Мемолог");
        Assert.Contains(Hlechyky.Games.Economy.AchievementCatalog.All, a => a.Key == Memy.AchPixels && a.Title == "З пікселів");
    }

    // ================================================================== банк

    [Fact]
    public void Bank_reads_the_contract_format()
    {
        var bank = MemyCache.TenBank();
        Assert.Empty(bank.Problems);
        Assert.Equal(10, bank.Memes.Count);
        var doge = bank.Find("w002")!;
        Assert.Equal(("world", "Doge", "Such wow", 2013, 1), (doge.Cat, doge.Name, doge.Phrase, doge.Year, doge.Difficulty));
        Assert.Equal(["дуже вау"], doge.Phrases);
        Assert.Equal(["пес"], doge.Tags);
        Assert.Equal("https://i.example.com/w002.png", doge.ImageUrl);
        Assert.Null(doge.Crop);
        Assert.False(bank.Find("w001")!.HasPhrase);
    }

    [Fact]
    public void Bank_drops_bad_records_one_by_one_and_keeps_the_rest()
    {
        JsonObject Spoil(string id, Action<JsonObject> how)
        {
            var o = MemyCache.M(id, id[0] == 'u' ? "ua" : "world", "Мем " + id, [], null, [], 2015, 1, ["люди"]);
            how(o);
            return o;
        }
        var good2 = MemyCache.M("u100", "ua", "Ой, усе", ["ой"], "Ой", ["ой-ой"], 2020, 2, ["люди"]);
        good2["crop"] = new JsonArray(0, 0.1, 1, 0.8);
        JsonNode[] records =
        [
            MemyCache.M("w100", "world", "Добрий мем", [], null, [], 2015, 1, ["люди"]),
            good2,
            Spoil("w101", o => o["id"] = "x101"),
            Spoil("w102", o => o["id"] = "u102"),
            Spoil("w103", o => o["cat"] = "ru"),
            Spoil("w104", o => o["name"] = "Я"),
            Spoil("w105", o => o["name"] = new string('м', 61)),
            Spoil("w106", o => o["about"] = new string('а', 301)),
            Spoil("w107", o => o["about"] = ""),
            Spoil("w108", o => o["year"] = "2017"),
            Spoil("w109", o => o["difficulty"] = 4),
            Spoil("w110", o => o["tags"] = new JsonArray()),
            Spoil("w111", o => o["tags"] = new JsonArray("а", "б", "в", "г")),
            Spoil("w112", o => o["image"]!["url"] = "http://i.example.com/x.png"),
            Spoil("w113", o => o.Remove("image")),
            Spoil("w114", o => o["crop"] = new JsonArray(0.6, 0, 0.5, 1)),
            Spoil("w115", o => o["crop"] = new JsonArray(0, 0, 1)),
            Spoil("w116", o => o["sources"] = new JsonArray()),
            Spoil("w117", o => o["sources"] = new JsonArray("http://x.example.com")),
            Spoil("w118", o => o["phrases"] = new JsonArray("щось")),
            Spoil("w119", o => o["names"] = "Doge"),
            MemyCache.M("w100", "world", "Повтор", [], null, [], 2015, 1, ["люди"]),
            JsonValue.Create("просто рядок")!,
        ];
        var bank = MemyBank.Parse(MemyCache.Json(records), null);
        Assert.Equal(["w100", "u100"], bank.Memes.Select(m => m.Id));
        Assert.Equal([0, 0.1, 1, 0.8], bank.Find("u100")!.Crop!);
        Assert.Equal(records.Length - 2, bank.Problems.Count);
        void Has(string start, string why) =>
            Assert.Contains(bank.Problems, p => p.StartsWith(start + ":", StringComparison.Ordinal) && p.Contains(why, StringComparison.Ordinal));
        Has("x101", "за зразком");
        Has("u102", "префікс");
        Has("w103", "категорія");
        Has("w104", "назва");
        Has("w105", "назва");
        Has("w106", "about довше");
        Has("w107", "нема about");
        Has("w108", "рік");
        Has("w109", "складність");
        Has("w110", "tags");
        Has("w111", "tags");
        Has("w112", "image.url");
        Has("w113", "нема image");
        Has("w114", "crop");
        Has("w115", "crop");
        Has("w116", "sources");
        Has("w117", "не https");
        Has("w118", "phrases без phrase");
        Has("w119", "names");
        Has("w100", "повтор id");
        Has("#23", "не об'єкт");
    }

    [Fact]
    public void Missing_or_broken_bank_is_empty_and_never_throws()
    {
        Assert.Empty(MemyBank.Load(Path.Combine(Path.GetTempPath(), "нема-" + Guid.NewGuid().ToString("N"), "memes.json"), null).Memes);
        Assert.Empty(MemyBank.Parse("{ битий", null).Memes);
        Assert.Empty(MemyBank.Parse("{}", null).Memes);
        Assert.Empty(MemyBank.Parse("[]", null).Memes);
    }

    [Fact]
    public void Empty_bank_table_refuses_to_start_and_solo_says_so()
    {
        using var c = new MemyCache(MemyBank.Empty);
        var h = Table(c, 2, start: false);
        Assert.False(h.Start().Ok);
        Assert.Contains("Мемів ще нема", h.Reply.Message);

        // без сервісу картинок зовсім — так само чесна відмова, а не виняток
        var bare = new RoomHarness("memy", null, 1, RoomHarness.Empty());
        bare.Join("Оля");
        Assert.False(bare.Start().Ok);
        Assert.Contains("Мемів ще нема", bare.Reply.Message);

        var solo = new RoomHarness("memy-solo", null, 1, c.Services());
        Assert.True(solo.Solo("Оля").Ok, solo.Reply.Message);
        Assert.Contains("Мемів ще нема", Assert.Single(solo.Finished).Result.Text);
    }

    [Fact]
    public void Phrase_mode_without_phrases_in_the_categories_is_refused()
    {
        using var c = new MemyCache();
        var h = Table(c, 1, new { what = "phrase", cat = "ua" }, start: false);
        Assert.False(h.Start().Ok);
        Assert.Contains("підписом", h.Reply.Message);
    }

    // ================================================================== картинки, проявлення, токени

    [Fact]
    public void Party_waits_for_the_first_meme_then_plays_it()
    {
        using var c = new MemyCache(ready: _ => false);
        var h = Table(c, 2);
        Assert.Equal(Memy.PhaseLoading, Phase(h));
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("image").ValueKind);
        Sec(h, 10);
        Assert.Equal(Memy.PhaseLoading, Phase(h));
        c.Add("u003");
        h.Tick(1);
        Assert.Equal(Memy.PhasePlay, Phase(h));
        Assert.Equal("u003", Current(h, c).Id);
    }

    [Fact]
    public void Nothing_downloads_in_a_minute_the_party_ends_honestly()
    {
        using var c = new MemyCache(ready: _ => false);
        var h = Table(c, 2);
        Sec(h, 61);
        Assert.False(Playing(h));
        var fin = Assert.Single(h.Finished);
        Assert.Empty(fin.Result.Winners);
        Assert.Contains("не докачались", fin.Result.Text);
    }

    [Fact]
    public void Meme_without_an_image_is_skipped_and_the_party_is_shorter()
    {
        using var c = new MemyCache();
        c.Images.GiveUp("w003", "відповідь 404");
        var h = Table(c, 1, new { rounds = "10" });
        Assert.Equal(9, h.View(null).GetProperty("rounds").GetInt32());
        var seen = new List<string>();
        for (var guard = 0; guard < 20 && Playing(h); guard++)
        {
            Until(h, Memy.PhasePlay);
            if (!Playing(h)) break;
            seen.Add(Current(h, c).Id);
            Until(h, Memy.PhaseReveal);
        }
        Assert.Equal(9, seen.Count);
        Assert.DoesNotContain("w003", seen);
        Assert.Single(h.Finished);
        Assert.Contains("лише 9 з 10", h.View(null).GetProperty("error").GetString());
    }

    [Fact]
    public void Pixel_frames_come_at_0_5_and_10_seconds_each_with_its_own_token()
    {
        using var c = new MemyCache();
        var h = Table(c, 2);
        var m = Current(h, c);
        var tokens = new List<string>();
        foreach (var (at, frame) in new[] { (0.0, 0), (5.0, 1), (5.0, 2) })
        {
            Sec(h, at);
            var v = h.View(1);
            Assert.Equal(frame, v.GetProperty("frame").GetInt32());
            var url = v.GetProperty("image").GetString()!;
            Assert.Matches("^/api/games/memy/[0-9a-f]{24}\\.jpg$", url);
            Assert.DoesNotContain(m.Id, url);
            Assert.Equal(c.Images.PathFor(m, frame), c.Images.Resolve(Token(url)));
            tokens.Add(Token(url));
            // до розкриття у виді — рівно одна картинка: поточного кадру
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(v.GetRawText(), "/api/games/memy/"));
            Assert.Equal(frame < 2, v.GetProperty("nextFrameAt").ValueKind == JsonValueKind.String);
        }
        Assert.Equal(3, tokens.Distinct().Count());
        // розкриття — чіткий кадр, той самий токен і в answer
        Until(h, Memy.PhaseReveal);
        var r = h.View(null);
        Assert.Equal(c.Images.PathFor(m, MemyImages.Clear), c.Images.Resolve(Token(r.GetProperty("image").GetString()!)));
        Assert.Equal(r.GetProperty("image").GetString(), r.GetProperty("answer").GetProperty("image").GetString());
    }

    [Fact]
    public void Whole_picture_mode_shows_the_clear_frame_at_once()
    {
        using var c = new MemyCache();
        var h = Table(c, 1, new { show = "whole" });
        var v = h.View(0);
        Assert.Equal(2, v.GetProperty("frame").GetInt32());
        Assert.Equal(c.Images.PathFor(Current(h, c), MemyImages.Clear), c.Images.Resolve(Token(v.GetProperty("image").GetString()!)));
        Assert.Equal(JsonValueKind.Null, v.GetProperty("nextFrameAt").ValueKind);
    }

    // ================================================================== варіанти

    [Theory]
    [InlineData("20", "8", 12)]
    [InlineData("20", "5", 15)]
    [InlineData("20", "half", 10)]
    [InlineData("30", "8", 22)]
    [InlineData("15", "half", 7.5)]
    public void Choices_appear_when_the_option_says(string seconds, string at, double openSec)
    {
        using var c = new MemyCache();
        var h = Table(c, 2, new { seconds, choicesAt = at });
        var start = h.Clock.UtcNow;
        Assert.Equal(start.AddSeconds(openSec), h.View(0).GetProperty("choicesAt").GetDateTimeOffset());
        Sec(h, openSec - 0.5);
        Assert.Null(Choices(h));
        Assert.False(h.View(0).GetProperty("choicesOpen").GetBoolean());
        Sec(h, 0.5);
        var ch = Choices(h)!;
        Assert.Equal(4, ch.Length);
        Assert.Single(ch, x => x == Right(h, c));
        Assert.True(h.View(0).GetProperty("choicesOpen").GetBoolean());
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("choicesAt").ValueKind);
    }

    [Fact]
    public void Pick_only_mode_opens_choices_at_once_and_writing_is_refused()
    {
        using var c = new MemyCache();
        var h = Table(c, 2, new { answer = "pick" });
        Assert.Equal(4, Choices(h)!.Length);
        Assert.False(h.View(0).GetProperty("canWrite").GetBoolean());
        var r = Write(h, 0, Right(h, c));
        Assert.False(r.Ok);
        Assert.Contains("лише варіанти", r.Message);
        Assert.True(h.Act(0, "pick", new { i = RightIndex(h, c) }).Ok);
        // одразу з варіантами — пів ціни, але кадр 0 і першість зверху
        Assert.Equal(Memy.Half + 50 + Memy.FirstBonus, Score(h, 0));
    }

    [Fact]
    public void Write_only_mode_never_shows_choices()
    {
        using var c = new MemyCache();
        var h = Table(c, 2, new { answer = "write" });
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("choicesAt").ValueKind);
        Sec(h, 19.5);
        Assert.Null(Choices(h));
        Assert.Contains("Варіантів ще нема", h.Act(0, "pick", new { i = 0 }).Message);
        Assert.True(Write(h, 0, Right(h, c)).Ok);
    }

    [Fact]
    public void Wrong_choices_come_from_the_same_category_with_a_shared_tag_first()
    {
        var bank = MemyCache.TenBank();
        // «Кіт за столом» (світовий; кіт, люди): світові зі спільним тегом — рівно троє
        var (names, right) = Memy.Choices(bank.Find("w003")!, phrase: false, bank, new Random(1));
        Assert.Equal("Кіт за столом", names[right]);
        Assert.Equal(["Grumpy Cat", "Stonks", "Кіт за столом", "Хлопець озирається"], names.Order(StringComparer.Ordinal));

        // «Підпис» для Grumpy Cat: лише фрази; світові фрази (Doge, This is fine) — першими, четверта — з решти
        var (phrases, pr) = Memy.Choices(bank.Find("w004")!, phrase: true, bank, new Random(1));
        Assert.Equal("No", phrases[pr]);
        Assert.Equal(4, phrases.Length);
        Assert.Contains("Such wow", phrases);
        Assert.Contains("This is fine", phrases);
        Assert.Contains("Problem?", phrases);
    }

    // ================================================================== очки

    [Fact]
    public void Points_full_price_frame_bonus_first_and_half_after_choices()
    {
        using var c = new MemyCache();
        var h = Table(c, 4);
        var r0 = Write(h, 0, Right(h, c));
        Assert.True(r0.Ok);
        Assert.Contains("🥇", r0.Message);
        Assert.Equal(Memy.Full + 50 + Memy.FirstBonus, Score(h, 0));          // кадр 0, перший
        Sec(h, 5);
        Assert.True(Write(h, 1, Right(h, c)).Ok);
        Assert.Equal(Memy.Full + 25, Score(h, 1));                             // кадр 1
        Sec(h, 5.5);
        Assert.True(Write(h, 2, Right(h, c)).Ok);
        Assert.Equal(Memy.Full, Score(h, 2));                                  // чітка, ще до варіантів
        Sec(h, 1.5);                                                           // 12 с — варіанти
        Assert.True(h.Act(3, "pick", new { i = RightIndex(h, c) }).Ok);
        Assert.Equal(Memy.Half, Score(h, 3));
        var found = h.View(null).GetProperty("found");
        Assert.Equal(4, found.GetArrayLength());
        Assert.Equal(0, found[0].GetProperty("frame").GetInt32());
    }

    [Fact]
    public void Writing_after_the_choices_opened_is_also_half_price()
    {
        using var c = new MemyCache();
        var h = Table(c, 2);
        Sec(h, 12);
        Assert.True(Write(h, 1, Right(h, c)).Ok);
        Assert.Equal(Memy.Half + Memy.FirstBonus, Score(h, 1));
    }

    [Fact]
    public void Without_pixels_speed_bonus_is_linear_in_time()
    {
        using var c = new MemyCache();
        var h = Table(c, 2, new { show = "whole", answer = "write" });
        Assert.True(Write(h, 0, Right(h, c)).Ok);
        Assert.Equal(Memy.Full + Memy.FastMax + Memy.FirstBonus, Score(h, 0));
        Sec(h, 10);
        Assert.True(Write(h, 1, Right(h, c)).Ok);
        Assert.Equal(Memy.Full + 25, Score(h, 1));
    }

    [Fact]
    public void Names_aliases_and_the_phrase_count_in_name_mode()
    {
        using var c = new MemyCache();
        var h = Table(c, 3, new { what = "name", cat = "world" });
        var m = Current(h, c);
        Assert.Equal(Memy.WhatName, Mode(h));
        Assert.True(Write(h, 0, m.Names[0].ToUpperInvariant()).Ok);
        Assert.True(Write(h, 1, "це ж " + m.Name).Ok);
        Assert.Contains("Мимо", Write(h, 2, "щось зовсім інше").Message);
    }

    [Fact]
    public void Miss_and_writing_no_more_than_once_a_second()
    {
        using var c = new MemyCache();
        var h = Table(c, 2);
        Assert.Equal("Мимо", Write(h, 0, "геть не те").Message);
        Assert.Equal("Не так швидко", Write(h, 0, Right(h, c)).Message);
        h.Tick(1);
        Assert.Equal("Не так швидко", Write(h, 0, Right(h, c)).Message);
        h.Tick(1);
        Assert.True(Write(h, 0, Right(h, c)).Ok);
        Assert.Contains("Уже вгадано", Write(h, 0, Right(h, c)).Message);
    }

    [Fact]
    public void Wrong_choice_blocks_until_the_end_of_the_round()
    {
        using var c = new MemyCache();
        var h = Table(c, 2);
        Sec(h, 12);
        var bad = h.Act(0, "pick", new { i = WrongIndex(h, c) });
        Assert.True(bad.Ok);
        Assert.Contains("без тебе", bad.Message);
        Assert.True(h.View(0).GetProperty("me").GetProperty("blocked").GetBoolean());
        Assert.Contains("профукав", Write(h, 0, Right(h, c)).Message);
        Assert.Contains("профукав", h.Act(0, "pick", new { i = RightIndex(h, c) }).Message);
        Assert.Contains("Ти вже своє зробив", h.Act(0, "skip").Message);
        Assert.Equal(0, Score(h, 0));
        // наступний раунд — знову в грі
        Until(h, Memy.PhaseReveal);
        Until(h, Memy.PhasePlay);
        Assert.True(Write(h, 0, Right(h, c)).Ok);
    }

    [Fact]
    public void Round_ends_early_when_everyone_is_done_or_skips()
    {
        using var c = new MemyCache();
        var h = Table(c, 3);
        Assert.True(Write(h, 0, Right(h, c)).Ok);
        Sec(h, 12);
        Assert.True(h.Act(1, "pick", new { i = WrongIndex(h, c) }).Ok);
        Assert.True(h.Act(2, "skip").Ok);
        Assert.True(h.Act(2, "skip").Ok);                                     // передумав
        h.Tick(1);
        Assert.Equal(Memy.PhasePlay, Phase(h));
        Assert.True(h.Act(2, "skip").Ok);
        h.Tick(1);
        Assert.Equal(Memy.PhaseReveal, Phase(h));
        Assert.Equal(Memy.RevealMs, h.View(null).GetProperty("totalMs").GetInt32());
    }

    // ================================================================== «Підпис» і «Упереміш»

    [Fact]
    public void Phrase_mode_plays_only_memes_with_a_phrase_and_takes_phrases()
    {
        using var c = new MemyCache();
        var h = Table(c, 2, new { what = "phrase", rounds = "5" });
        Assert.Equal(4, h.View(null).GetProperty("rounds").GetInt32());     // фраз у банку лише чотири
        Assert.Contains("лише 4", h.View(null).GetProperty("note").GetString());
        for (var guard = 0; guard < 10 && Playing(h); guard++)
        {
            Until(h, Memy.PhasePlay);
            if (!Playing(h)) break;
            var m = Current(h, c);
            Assert.True(m.HasPhrase);
            Assert.Equal(Memy.WhatPhrase, Mode(h));
            Assert.Contains("Мимо", Write(h, 0, m.Name).Message);               // назва — не підпис
            h.Tick(2);
            Assert.True(Write(h, 0, m.Phrases.Length > 0 ? m.Phrases[0] : m.Phrase!).Ok);
            Sec(h, 12);
            Assert.All(Choices(h)!, x => Assert.Contains(c.Bank.Memes, mm => mm.Phrase == x));
            Until(h, Memy.PhaseReveal);
            Assert.Equal(m.Phrase, h.View(null).GetProperty("answer").GetProperty("phrase").GetString());
        }
        Assert.Single(h.Finished);
    }

    [Fact]
    public void Mix_mode_asks_a_phrase_only_where_there_is_one()
    {
        using var c = new MemyCache();
        var modes = new List<(string Id, string Mode)>();
        for (var seed = 1; seed <= 4; seed++)
        {
            var h = Table(c, 1, new { rounds = "10" }, seed);
            for (var guard = 0; guard < 20 && Playing(h); guard++)
            {
                Until(h, Memy.PhasePlay);
                if (!Playing(h)) break;
                modes.Add((Current(h, c).Id, Mode(h)));
                Until(h, Memy.PhaseReveal);
            }
        }
        Assert.All(modes.Where(x => x.Mode == Memy.WhatPhrase), x => Assert.True(c.Bank.Find(x.Id)!.HasPhrase));
        Assert.Contains(modes, x => x.Mode == Memy.WhatPhrase);
        Assert.Contains(modes, x => x.Mode == Memy.WhatName && c.Bank.Find(x.Id)!.HasPhrase);
    }

    // ================================================================== категорії й складність

    [Fact]
    public void Category_and_level_choose_the_memes()
    {
        using var c = new MemyCache();
        List<MemyMeme> Play(object options)
        {
            var h = Table(c, 1, options);
            var got = new List<MemyMeme>();
            for (var guard = 0; guard < 20 && Playing(h); guard++)
            {
                Until(h, Memy.PhasePlay);
                if (!Playing(h)) break;
                got.Add(Current(h, c));
                Until(h, Memy.PhaseReveal);
            }
            return got;
        }
        Assert.All(Play(new { cat = "ua", rounds = "5" }), m => Assert.Equal("ua", m.Cat));
        Assert.Equal(3, Play(new { cat = "ua", rounds = "5" }).Count);
        Assert.Equal(7, Play(new { cat = "world,old", rounds = "10" }).Count);
        Assert.All(Play(new { level = "easy", rounds = "5" }), m => Assert.Equal(1, m.Difficulty));
        // «Для знавців» — чотири складні, п'ятий добирається з найближчої складності
        var hard = Play(new { level = "hard", rounds = "5" });
        Assert.All(hard.Take(4), m => Assert.True(m.Difficulty >= 2));
        Assert.Equal(1, hard[4].Difficulty);
    }

    // ================================================================== приховане

    [Fact]
    public void Others_answers_and_the_meme_are_hidden_until_the_reveal()
    {
        using var c = new MemyCache();
        var h = Table(c, 3);
        var m = Current(h, c);
        Assert.Equal("Мимо", Write(h, 0, "секретна здогадка Олі").Message);
        Assert.True(Write(h, 1, Right(h, c)).Ok);
        h.Tick(1);
        foreach (int? seat in new int?[] { 2, null })
        {
            var raw = h.View(seat).GetRawText();
            Assert.DoesNotContain(JsonEncoded("секретна"), raw);
            Assert.DoesNotContain(JsonEncoded(m.Name), raw);
            Assert.DoesNotContain(JsonEncoded(m.About), raw);
            Assert.DoesNotContain(m.Id, raw);
        }
        Assert.Contains("секретна", h.View(0).GetProperty("me").GetProperty("text").GetString());
        // хто влучив — видно, що саме писав — ні
        var found = h.View(2).GetProperty("found");
        Assert.Equal(1, found[0].GetProperty("seat").GetInt32());
        Assert.False(found[0].TryGetProperty("text", out _));
        Assert.Equal(JsonValueKind.Null, h.View(2).GetProperty("answers").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.View(2).GetProperty("answer").ValueKind);

        Until(h, Memy.PhaseReveal);
        var v = h.View(2);
        Assert.Equal(m.Name, v.GetProperty("answer").GetProperty("name").GetString());
        Assert.Equal(m.About, v.GetProperty("answer").GetProperty("about").GetString());
        Assert.Equal(m.Year, v.GetProperty("answer").GetProperty("year").GetInt32());
        Assert.Equal(m.ImagePage, v.GetProperty("answer").GetProperty("page").GetString());
        var answers = v.GetProperty("answers").EnumerateArray().ToList();
        Assert.Equal("секретна здогадка Олі", answers.Single(a => a.GetProperty("seat").GetInt32() == 0).GetProperty("text").GetString());
        Assert.True(answers.Single(a => a.GetProperty("seat").GetInt32() == 1).GetProperty("hit").GetBoolean());
    }

    /// <summary>Як рядок лежить усередині JSON на дроті (кирилиця там — \uXXXX).</summary>
    static string JsonEncoded(string s) => JsonSerializer.Serialize(s)[1..^1];

    [Fact]
    public void Illegal_acts_do_not_change_any_view()
    {
        using var c = new MemyCache();
        var h = Table(c, 2);
        string Snap() => h.View(0).GetRawText() + h.View(1).GetRawText() + h.View(null).GetRawText();
        void Refused(int seat, string action, object? payload)
        {
            var before = Snap();
            Assert.False(h.Act(seat, action, payload).Ok);
            Assert.Equal(before, Snap());
        }
        Refused(0, "dance", null);
        Refused(0, "pick", new { i = 0 });                        // варіантів ще нема
        Refused(0, "guess", new { text = "   " });
        Refused(0, "guess", null);
        Assert.Equal("Мимо", Write(h, 0, "не те").Message);
        Refused(0, "guess", new { text = "знову не те" });         // частіше, ніж раз на секунду
        Sec(h, 12);
        Refused(1, "pick", new { i = 9 });
        Refused(1, "pick", new { i = -1 });
        Refused(1, "pick", new { x = 1 });
        Assert.True(h.Act(1, "pick", new { i = WrongIndex(h, c) }).Ok);
        Refused(1, "pick", new { i = RightIndex(h, c) });         // профукав
        Refused(1, "skip", null);
        Until(h, Memy.PhaseReveal);
        Refused(0, "guess", new { text = Current(h, c).Name });
        Refused(0, "skip", null);
    }

    // ================================================================== кінці

    [Fact]
    public void Full_party_ends_with_winner_scores_shards_and_achievements()
    {
        using var c = new MemyCache();
        var h = Table(c, 2, new { rounds = "5" });
        for (var guard = 0; guard < 20 && Playing(h); guard++)
        {
            Until(h, Memy.PhasePlay);
            if (!Playing(h)) break;
            Assert.True(Write(h, 0, Right(h, c)).Ok);
            Until(h, Memy.PhaseReveal);
        }
        var fin = Assert.Single(h.Finished);
        Assert.Equal([0], fin.Result.Winners);
        var total = 5 * (Memy.Full + 50 + Memy.FirstBonus);
        Assert.Equal(total, fin.Result.Scores![0]);
        Assert.Equal(0, fin.Result.Scores![1]);
        Assert.Contains("головний мемолог — Оля", fin.Result.Text);
        Assert.Contains(h.Awards, a => a.Nick == "Оля" && a.Reason == "points:1" && a.Shards == total / Memy.PointsPerShard);
        Assert.Contains(h.Awards, a => a.Nick == "Оля" && a.Reason == "ach:" + Memy.AchWin);
        Assert.Single(h.Awards, a => a.Nick == "Оля" && a.Reason == "ach:" + Memy.AchPixels);
        Assert.DoesNotContain(h.Awards, a => a.Nick == "Петро");
        var v = h.View(null);
        Assert.Equal(Memy.PhaseDone, v.GetProperty("phase").GetString());
        Assert.Equal(5, v.GetProperty("played").GetArrayLength());
        Assert.Equal([0], v.GetProperty("result").GetProperty("winners").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Contains("Партію зіграно", Write(h, 0, "щось").Message);
    }

    [Fact]
    public void Nobody_guessed_is_a_draw()
    {
        using var c = new MemyCache();
        var h = Table(c, 2, new { rounds = "5" });
        Until(h, Memy.PhaseDone);
        var fin = Assert.Single(h.Finished);
        Assert.Empty(fin.Result.Winners);
        Assert.Contains("жодного мему не впізнали", fin.Result.Text);
        Assert.DoesNotContain(h.Awards, a => a.Reason.StartsWith("ach:", StringComparison.Ordinal));
    }

    [Fact]
    public void One_leaving_does_not_stop_the_rest_and_all_leaving_ends_it()
    {
        using var c = new MemyCache();
        var h = Table(c, 3);
        h.Leave("Петро");
        Assert.True(Playing(h));
        Assert.Contains(1, h.View(null).GetProperty("left").EnumerateArray().Select(x => x.GetInt32()));
        // ті, хто лишився, своє зробили — раунд не чекає того, хто пішов
        Assert.True(Write(h, 0, Right(h, c)).Ok);
        Assert.True(h.Act(2, "skip").Ok);
        h.Tick(1);
        Assert.Equal(Memy.PhaseReveal, Phase(h));
        h.Leave("Оля");
        h.Leave("Ганна");
        Assert.False(Playing(h));
        Assert.Single(h.Finished);
    }

    // ================================================================== детермінізм, рематч

    static List<string> Script(MemyCache c, int seed)
    {
        var h = Table(c, 2, new { rounds = "10" }, seed);
        var log = new List<string>();
        for (var guard = 0; guard < 30 && Playing(h); guard++)
        {
            Until(h, Memy.PhasePlay);
            if (!Playing(h)) break;
            Sec(h, 12);
            log.Add($"{Current(h, c).Id}|{Mode(h)}|{string.Join("/", Choices(h)!)}");
            Until(h, Memy.PhaseReveal);
        }
        return log;
    }

    [Fact]
    public void Same_seed_same_party()
    {
        using var c = new MemyCache();
        var a = Script(c, 7);
        Assert.Equal(10, a.Count);
        Assert.Equal(a, Script(c, 7));
        Assert.NotEqual(a, Script(c, 8));
    }

    [Fact]
    public void Rematch_starts_a_fresh_party()
    {
        using var c = new MemyCache();
        var h = Table(c, 2, new { rounds = "5" });
        Assert.True(Write(h, 0, Right(h, c)).Ok);
        Until(h, Memy.PhaseDone);
        Assert.True(h.Rematch().Ok, h.Reply.Message);
        var v = h.View(null);
        Assert.Equal(Memy.PhasePlay, v.GetProperty("phase").GetString());
        Assert.Equal(1, v.GetProperty("round").GetInt32());
        Assert.All(v.GetProperty("scores").EnumerateArray(), x => Assert.Equal(0, x.GetInt32()));
        Assert.Equal(JsonValueKind.Null, v.GetProperty("result").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("played").ValueKind);
        // друга партія платить заново: номер партії в причині
        Assert.True(Write(h, 0, Right(h, c)).Ok);
        Until(h, Memy.PhaseDone);
        Assert.Contains(h.Awards, a => a.Reason == "points:2");
    }

    // ================================================================== соло

    [Fact]
    public void Solo_plays_ten_memes_with_default_rules_and_records_the_score()
    {
        using var c = new MemyCache();
        var h = new RoomHarness("memy-solo", new { rounds = "5", what = "phrase" }, 3, c.Services());
        Assert.True(h.Solo("Оля").Ok, h.Reply.Message);
        var rounds = 0;
        for (var guard = 0; guard < 30 && Playing(h); guard++)
        {
            Until(h, Memy.PhasePlay);
            if (!Playing(h)) break;
            rounds++;
            Assert.True(Write(h, 0, Right(h, c)).Ok);
            h.Tick(1);
            Until(h, Memy.PhaseReveal);
        }
        Assert.Equal(10, rounds);                          // опції столу на тренування не діють
        var score = Assert.Single(h.Scores);
        Assert.Equal(("memy-solo", "Оля"), (score.GameId, score.Nick));
        Assert.Equal(10 * (Memy.Full + 50 + Memy.FirstBonus), score.Score);
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:" + Memy.AchWin);
        Assert.Contains(h.Awards, a => a.Reason == "ach:" + Memy.AchPixels);
    }

    // ================================================================== пам'ять баченого

    [Fact]
    public void Unseen_memes_go_first_and_shown_ones_are_remembered()
    {
        using var c = new MemyCache();
        using var temp = new TempDb();
        var seen = new[] { "w001", "w002", "w003", "w004", "w005", "w006", "u001" };
        new GameSeen(temp.Db).Seed(Memy.SeenGame, [GameSeen.NickKey("Оля")], seen, new DateTimeOffset(2026, 10, 18, 20, 0, 0, TimeSpan.Zero));
        var h = Table(c, 2, new { rounds = "5" }, db: temp);
        var got = new List<string>();
        for (var guard = 0; guard < 20 && Playing(h); guard++)
        {
            Until(h, Memy.PhasePlay);
            if (!Playing(h)) break;
            got.Add(Current(h, c).Id);
            Until(h, Memy.PhaseReveal);
        }
        Assert.Equal(["o001", "u002", "u003"], got.Take(3).Order(StringComparer.Ordinal));
        var peek = new GameSeen(temp.Db).Peek(Memy.SeenGame, [GameSeen.NickKey("Петро")]);
        Assert.Equal(got.Order(StringComparer.Ordinal), peek.Keys.Order(StringComparer.Ordinal));
    }

    // ================================================================== кеш і токени

    [Fact]
    public void Tokens_are_opaque_per_frame_and_die_with_time()
    {
        using var c = new MemyCache();
        var t0 = c.Images.Issue("w002", 0);
        var t0b = c.Images.Issue("w002", 0);
        var t2 = c.Images.Issue("w002", 2);
        Assert.True(GeoPhotos.IsToken(t0));
        Assert.NotEqual(t0, t0b);
        Assert.Equal(c.Images.PathFor(c.Bank.Find("w002")!, 0), c.Images.Resolve(t0));
        Assert.Equal(c.Images.PathFor(c.Bank.Find("w002")!, 2), c.Images.Resolve(t2));
        Assert.DoesNotContain("w002", Path.GetFileName(c.Images.Resolve(t2)!));
        Assert.Null(c.Images.Resolve("../memes.json"));
        Assert.Null(c.Images.Resolve(new string('a', 24)));
        Assert.Throws<ArgumentException>(() => c.Images.Issue("нема", 0));
        c.Clock.Advance(MemyImages.TokenLife + TimeSpan.FromSeconds(1));
        Assert.Null(c.Images.Resolve(t0));
    }

    [Fact]
    public void Sweep_removes_strangers_and_stale_temps_but_keeps_the_bank()
    {
        using var c = new MemyCache();
        File.WriteAllBytes(Path.Combine(c.Dir, "чуже.jpg"), [1]);
        var temp = Path.Combine(c.Dir, "abc-0.tmp.jpg");
        File.WriteAllBytes(temp, [1]);
        File.SetLastWriteTimeUtc(temp, DateTime.UtcNow.AddHours(-2));
        var fresh = Path.Combine(c.Dir, "abc.src.tmp");
        File.WriteAllBytes(fresh, [1]);
        Assert.Equal(2, c.Images.Sweep());
        Assert.True(File.Exists(fresh));
        Assert.Equal(10, c.Images.ReadyCount);
    }

    sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(answer(request));
        }
    }

    static HttpResponseMessage Bytes(byte[] body, string type) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) { Headers = { ContentType = new(type) } } };

    [Fact]
    public async Task Page_instead_of_a_picture_or_404_gives_up_at_once_and_500_after_three_tries()
    {
        using var c = new MemyCache(ready: _ => false);
        var ff = Path.Combine(c.Dir, "нема-ffmpeg.exe");   // до ffmpeg справа не дійде: завантаження відпаде раніше
        var html = new MemyImages(c.Bank, c.Dir, c.Clock, ff, null, new Stub(_ => Bytes("<html/>"u8.ToArray(), "text/html")), TimeSpan.Zero, online: false);
        Assert.False(await html.FetchAsync(c.Bank.Find("w001")!, default));
        Assert.True(html.Gone("w001"));
        Assert.Contains("не картинка", html.Failed["w001"]);

        var gone = new MemyImages(c.Bank, c.Dir, c.Clock, ff, null, new Stub(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), TimeSpan.Zero, online: false);
        Assert.False(await gone.FetchAsync(c.Bank.Find("w001")!, default));
        Assert.True(gone.Gone("w001"));

        var stub = new Stub(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var flaky = new MemyImages(c.Bank, c.Dir, c.Clock, ff, null, stub, TimeSpan.Zero, online: false);
        var m = c.Bank.Find("w002")!;
        Assert.False(await flaky.FetchAsync(m, default));
        Assert.False(await flaky.FetchAsync(m, default));
        Assert.False(flaky.Gone("w002"));
        Assert.False(await flaky.FetchAsync(m, default));
        Assert.True(flaky.Gone("w002"));
        Assert.Contains("500", flaky.Failed["w002"]);
        Assert.Empty(Directory.EnumerateFiles(c.Dir));   // жодних недописаних сирих файлів
    }

    // ---------------------------------------------------------------- справжній ffmpeg

    static string? Ffmpeg()
    {
        foreach (var p in new[] { Paths.Resolve("tools/yt-dlp/ffmpeg.exe"), "D:/or-dev/tools/yt-dlp/ffmpeg.exe" })
            if (File.Exists(p)) return p;
        return null;
    }

    /// <summary>Згенерувати картинку ffmpeg-ом (testsrc): локально, без мережі.</summary>
    static byte[] Generate(string ff, string dir, string name, string lavfi)
    {
        var path = Path.Combine(dir, name);
        var psi = new ProcessStartInfo(ff) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "lavfi", "-i", lavfi, "-t", "1",
                     "-frames:v", name.EndsWith(".gif", StringComparison.Ordinal) ? "5" : "1", path })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        return bytes;
    }

    /// <summary>Ширина й висота JPEG із сегмента SOFn.</summary>
    static (int W, int H) Size(byte[] jpg)
    {
        var i = 2;
        while (i + 9 < jpg.Length && jpg[i] == 0xFF)
        {
            var m = jpg[i + 1];
            if (m is >= 0xC0 and <= 0xC3) return ((jpg[i + 7] << 8) | jpg[i + 8], (jpg[i + 5] << 8) | jpg[i + 6]);
            i += 2 + ((jpg[i + 2] << 8) | jpg[i + 3]);
        }
        throw new InvalidDataException("нема SOF");
    }

    [Fact]
    public async Task Real_ffmpeg_crops_shrinks_strips_and_makes_pixel_frames()
    {
        if (Ffmpeg() is not { } ff) return;   // на машині без ffmpeg — нема що перевіряти
        var cropped = MemyCache.M("w001", "world", "Рамка", [], null, [], 2017, 1, ["люди"]);
        cropped["crop"] = new JsonArray(0.25, 0, 0.5, 1);
        var gif = MemyCache.M("o001", "old", "Гіфка", [], null, [], 2008, 1, ["комікс"]);
        gif["image"]!["url"] = "https://i.example.com/o001.gif";
        var bank = MemyBank.Parse(MemyCache.Json([cropped, MemyCache.M("w002", "world", "Ціла", [], null, [], 2017, 1, ["люди"]), gif]), null);
        using var c = new MemyCache(bank, _ => false);
        var src = Path.Combine(c.Dir, "src");
        Directory.CreateDirectory(src);
        var png = Generate(ff, src, "a.png", "testsrc=size=1600x800:rate=1");
        var anim = Generate(ff, src, "b.gif", "testsrc=size=320x200:rate=5");
        Directory.Delete(src);
        var stub = new Stub(r => r.RequestUri!.AbsolutePath.EndsWith(".gif", StringComparison.Ordinal) ? Bytes(anim, "image/gif") : Bytes(png, "image/png"));
        var images = new MemyImages(bank, c.Dir, c.Clock, ff, null, stub, TimeSpan.Zero, online: false);

        foreach (var m in bank.Memes) Assert.True(await images.FetchAsync(m, default), images.Failed.GetValueOrDefault(m.Id));
        Assert.Equal(3, images.ReadyCount);
        var expect = new Dictionary<string, (int, int)>
        {
            ["w001"] = (800, 800),      // рамка — середина 800×800
            ["w002"] = (960, 480),      // довша сторона ≤ 960
            ["o001"] = (320, 200),      // gif — перший кадр, менших не розтягуємо
        };
        foreach (var m in bank.Memes)
            for (var f = 0; f < MemyImages.Frames; f++)
            {
                var bytes = File.ReadAllBytes(images.PathFor(m, f));
                Assert.Equal(expect[m.Id], Size(bytes));            // піксельні — рівно як чіткий: картинка не стрибає
                Assert.False(GeoImage.HasSegment(bytes, 0xE1));   // ні EXIF
                Assert.False(GeoImage.HasSegment(bytes, 0xFE));   // ні коментаря ffmpeg
            }
        Assert.Equal(3 * MemyImages.Frames, Directory.EnumerateFiles(c.Dir).Count());   // жодних тимчасових
        Assert.Equal(3, stub.Calls);

        // запасний граф (для ffmpeg без «scale» зі зразком) теж справний: піксельні — довша сторона PixelOut
        var raw = Path.Combine(c.Dir, "raw.png");
        File.WriteAllBytes(raw, png);
        string[] outs = [.. Enumerable.Range(0, MemyImages.Frames).Select(f => Path.Combine(c.Dir, $"old-{f}.jpg"))];
        var psi = new ProcessStartInfo(ff) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in MemyImages.Args(raw, null, outs, exact: false)) psi.ArgumentList.Add(a);
        using (var p = Process.Start(psi)!)
        {
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
        }
        Assert.Equal((MemyImages.PixelOut, MemyImages.PixelOut / 2), Size(File.ReadAllBytes(outs[0])));
        Assert.Equal((960, 480), Size(File.ReadAllBytes(outs[MemyImages.Clear])));

        // битий файл під виглядом картинки: ffmpeg не розкодує — засинає одразу
        var junkDir = c.Dir + "-junk";
        var junk = new MemyImages(bank, junkDir, c.Clock, ff, null, new Stub(_ => Bytes([1, 2, 3, 4, 5], "image/png")), TimeSpan.Zero, online: false);
        try
        {
            Assert.False(await junk.FetchAsync(bank.Find("w002")!, default));
            Assert.True(junk.Gone("w002"));
            Assert.StartsWith("ffmpeg", junk.Failed["w002"]);
        }
        finally { try { Directory.Delete(junkDir, true); } catch (IOException) { } }
    }
}

[Collection(SerialPerf.Name)]
public class MemyPerfTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Perf")]
    public void Perf_thousand_ticks_with_twelve_players_take_under_two_seconds()
    {
        using var c = new MemyCache();
        var h = MemyTests.Table(c, 12, new { rounds = "15", seconds = "15" });
        var rng = new Random(5);
        var sw = new Stopwatch();
        var matches = 1;
        for (var t = 0; t < 1000; t++)
        {
            if (h.Room.Status == RoomStatus.Finished)
            {
                Assert.True(h.Rematch().Ok, h.Reply.Message);
                matches++;
            }
            if (MemyTests.Phase(h) == Memy.PhasePlay && t % 3 == 0)
            {
                var s = rng.Next(12);
                if (rng.Next(3) == 0) h.Act(s, "guess", new { text = MemyTests.Right(h, c) });
                else if (rng.Next(2) == 0) h.Act(s, "pick", new { i = rng.Next(4) });
                else h.Act(s, "guess", new { text = "щось не те" });
            }
            sw.Start();
            h.Tick(1);
            sw.Stop();
        }
        output.WriteLine($"1000 тиків на дванадцятьох ({matches} партій): {sw.Elapsed.TotalMilliseconds:F1} мс");
        output.WriteLine($"вид на дванадцятьох: {h.View(0).GetRawText().Length} символів");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"{sw.Elapsed.TotalMilliseconds} мс");
    }
}
