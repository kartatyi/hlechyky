using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>
/// 🏛 Зала скарбів (v13 §Д2, ClickerTreasure.cs): вісімнадцять речей у трьох вітринах, шанси джерел (глек з полиці 2 %,
/// розписний 10 %, корабель 25 %, посол напевно, річ за портом), дубль — уламок, п'ять уламків склеюються в річ тієї ж
/// вітрини, множник (+2 % за річ, зала ще +10 %), бонуси повних вітрин, «❓ Загадка Глека» і збереження.
/// </summary>
public class ClickerTreasureTests
{
    /// <summary>Випадок із наперед заданими відповідями: <c>NextDouble</c> — кидок, <c>Next(n)</c> — номер (обрізаний до n−1).</summary>
    sealed class Fixed(double roll, int pick = 0) : Random
    {
        public override double NextDouble() => roll;
        public override int Next(int max) => Math.Min(pick, max - 1);
    }

    static readonly Random Never = new Fixed(0.99);
    static Random Always(int pick = 0) => new Fixed(0.0, pick);

    static RoomHarness Wheel(bool open = true, string nick = "Оля")
    {
        var sp = new ServiceCollection().AddSingleton(new ClickerRiddleService()).BuildServiceProvider();
        var h = new RoomHarness("clicker", services: sp);
        h.Solo(nick);
        Patch(h, s =>
        {
            s["pots"] = 0;
            s["total"] = 1e40;
            if (open) AlbumNode(s)["cells"] = new JsonObject { ["pot"] = new JsonArray("") };
        });
        Game(h).TreasureRng = Never;
        return h;
    }

    static Clicker Game(RoomHarness h) => (Clicker)h.Room.Game;

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var rng = h.Room.Game is Clicker c ? c.TreasureRng : null;
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
            if (rng is not null) ((Clicker)h.Room.Game).TreasureRng = rng;
        }
    }

    static JsonObject AlbumNode(JsonObject s)
    {
        if (s["album"] is JsonObject a) return a;
        var fresh = new JsonObject();
        s["album"] = fresh;
        return fresh;
    }

    static JsonElement Hall(RoomHarness h) => h.View(0).GetProperty("album").GetProperty("hall");
    static int Mask(RoomHarness h) => Hall(h).GetProperty("mask").GetInt32();
    static int Shards(RoomHarness h, int c) => Hall(h).GetProperty("shards")[c].GetInt32();
    static int Bit(string key) => 1 << Array.FindIndex(Clicker.Treasures, t => t.Key == key);
    static int Count(int mask) => System.Numerics.BitOperations.PopCount((uint)mask);
    static ActResult Act(RoomHarness h, string action, object? payload = null) => h.Act(0, action, payload);
    static ActResult Riddle(RoomHarness h, string pick) => Act(h, "album", new { op = "riddle", pick });

    static void SetHall(RoomHarness h, string[] items, int[]? shards = null) => Patch(h, s =>
    {
        var a = AlbumNode(s);
        a["hall"] = new JsonArray(items.Select(x => (JsonNode)x).ToArray());
        a["hallShards"] = new JsonArray((shards ?? [0, 0, 0]).Select(x => (JsonNode)x).ToArray());
    });

    static string[] Keys(int c, int n = 6) => Clicker.Treasures.Where(t => t.Case == c).Take(n).Select(t => t.Key).ToArray();

    // ---------- каталог і книга ----------

    [Fact]
    public void Eighteen_things_three_cases_of_six_with_unique_keys_and_no_cigarette()
    {
        Assert.Equal(18, Clicker.Treasures.Length);
        Assert.Equal(18, Clicker.Treasures.Select(t => t.Key).Distinct().Count());
        for (var c = 0; c < 3; c++) Assert.Equal(6, Clicker.Treasures.Count(t => t.Case == c));
        Assert.DoesNotContain(Clicker.Treasures, t => t.Emoji.Contains("🚬"));
        Assert.Equal([0.02, 0.10, 0.25], Clicker.TreasureChance);
        Assert.Equal(0.46, Clicker.TreasureBonus((1 << 18) - 1), 9);
        Assert.Equal(0.02, Clicker.TreasureBonus(1), 9);
        Assert.Equal(0.0, Clicker.TreasureBonus(0), 9);
    }

    [Fact]
    public void Riddles_json_refers_only_to_these_things_and_parses_without_problems()
    {
        var path = Paths.Resolve(ClickerRiddleService.DefaultFile);
        var json = File.ReadAllText(path);
        var (book, problems) = ClickerRiddleBook.Parse(json);
        Assert.Empty(problems);
        Assert.True(book.Riddles.Count >= 40);
        var keys = Clicker.Treasures.Select(t => t.Key).ToHashSet();
        Assert.All(book.Riddles, r =>
        {
            Assert.Contains(r.Item, keys);
            Assert.All(r.Options, o => Assert.Contains(o, keys));
        });
        using var doc = JsonDocument.Parse(json);
        foreach (var it in doc.RootElement.GetProperty("items").EnumerateObject())
        {
            Assert.Contains(it.Name, keys);
            var t = Clicker.Treasures.First(x => x.Key == it.Name);
            Assert.Equal(it.Value.GetProperty("name").GetString(), t.Name);
            Assert.Equal(Clicker.TreasureCaseKeys[t.Case], it.Value.GetProperty("case").GetString());
        }
        Assert.Equal(18, doc.RootElement.GetProperty("items").EnumerateObject().Count());
    }

    [Fact]
    public void A_bad_riddle_is_skipped_with_a_problem()
    {
        var (book, problems) = ClickerRiddleBook.Parse("""
            { "riddles": [
              { "item": "pysanka", "text": "добра", "options": ["pysanka", "lozhka", "sopilka"] },
              { "item": "pysanka", "text": "нема правильної", "options": ["lozhka", "sopilka", "namysto"] },
              { "item": "xxx", "text": "невідома", "options": ["xxx", "lozhka", "sopilka"] } ] }
            """);
        Assert.Single(book.Riddles);
        Assert.Equal(2, problems.Count);
    }

    // ---------- шанси й дубль ----------

    [Fact]
    public void The_chance_of_each_source_decides_whether_a_thing_falls()
    {
        var h = Wheel();
        var g = Game(h);
        lock (h.Room.Sync)
        {
            Assert.NotNull(g.TreasureFind(0, 0.0199, 0));
            Assert.NotNull(g.TreasureFind(1, 0.0999, 0));
            Assert.NotNull(g.TreasureFind(2, 0.2499, 0));
            Assert.Null(g.TreasureFind(0, 0.02, 1));
            Assert.Null(g.TreasureFind(1, 0.10, 1));
            Assert.Null(g.TreasureFind(2, 0.25, 1));
        }
        Assert.Equal(Bit("pysanka") | Bit("lyulka") | Bit("chashka"), Mask(h));
    }

    [Fact]
    public void The_hall_is_closed_until_the_album_has_a_cell()
    {
        var h = Wheel(open: false);
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("album").GetProperty("hall").ValueKind);
        lock (h.Room.Sync) Assert.Null(Game(h).TreasureFind(0, 0.0, 0));
        Game(h).TreasureRng = Always();
        Patch(h, s => { });
        Assert.NotNull(h.View(0).GetProperty("album"));
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("album").GetProperty("hall").ValueKind);
    }

    [Fact]
    public void A_duplicate_is_a_shard_of_its_own_case_and_five_shards_glue_a_missing_thing_of_that_case()
    {
        var h = Wheel();
        var g = Game(h);
        string? first, dup;
        lock (h.Room.Sync) { first = g.TreasureFind(1, 0, 2); dup = g.TreasureFind(1, 0, 2); }
        Assert.Contains("Пернач", first);
        Assert.Contains("+2 % до всього", first);
        Assert.Contains("уламок 1/5", dup);
        Assert.Equal(Bit("pernach"), Mask(h));
        Assert.Equal(1, Shards(h, 1));
        Assert.Equal(0, Shards(h, 0));

        for (var i = 0; i < 3; i++) lock (h.Room.Sync) g.TreasureFind(1, 0, 2);
        Assert.Equal(4, Shards(h, 1));
        string? glued;
        lock (h.Room.Sync) glued = g.TreasureFind(1, 0, 2);
        Assert.Contains("склеєно", glued);
        Assert.Equal(0, Shards(h, 1));
        var mask = Mask(h);
        Assert.Equal(2, Count(mask));
        Assert.Equal(0, mask & ~(((1 << 6) - 1) << 6));               // обидві речі — козацькі
    }

    [Fact]
    public void A_full_case_drops_no_more_things_or_shards()
    {
        var h = Wheel();
        SetHall(h, Keys(0));
        lock (h.Room.Sync) Assert.Null(Game(h).TreasureFind(0, 0.0, 3));
        Assert.Equal(0, Shards(h, 0));
        Assert.Equal(Count(Mask(h)), 6);
    }

    // ---------- справжні джерела ----------

    [Fact]
    public void A_caught_shelf_jug_can_bring_a_village_thing_and_says_so()
    {
        var h = Wheel();
        Game(h).TreasureRng = Always(2);
        Patch(h, s => s["fall"] = new JsonObject
        {
            ["at"] = h.Clock.UtcNow.ToString("O"), ["until"] = (h.Clock.UtcNow + Clicker.FallShown).ToString("O"), ["x"] = 40,
        });
        var r = Act(h, "grab");
        Assert.True(r.Ok, r.Message);
        Assert.Contains("🏛 Зала скарбів: Дерев'яна ложка! +2 % до всього", r.Message);
        Assert.Equal(Bit("lozhka"), Mask(h));
    }

    [Fact]
    public void A_caught_golden_jug_can_bring_a_cossack_thing()
    {
        var h = Wheel();
        Game(h).TreasureRng = Always(1);
        Patch(h, s => s["golden"] = new JsonObject
        {
            ["at"] = h.Clock.UtcNow.ToString("O"), ["until"] = (h.Clock.UtcNow + Clicker.GoldenShown).ToString("O"),
            ["kind"] = (int)Clicker.GoldenKind.Merchant, ["x"] = 10, ["y"] = 10,
        });
        var r = Act(h, "catch");
        Assert.True(r.Ok, r.Message);
        Assert.Contains("Зала скарбів: Порохівниця!", r.Message);
        Assert.Equal(Bit("porokhivnytsia"), Mask(h));
    }

    [Fact]
    public void A_missed_roll_or_a_closed_hall_changes_nothing()
    {
        var h = Wheel();
        Patch(h, s => s["fall"] = new JsonObject
        {
            ["at"] = h.Clock.UtcNow.ToString("O"), ["until"] = (h.Clock.UtcNow + Clicker.FallShown).ToString("O"), ["x"] = 40,
        });
        var r = Act(h, "grab");
        Assert.True(r.Ok);
        Assert.DoesNotContain("Зала скарбів", r.Message);
        Assert.Equal(0, Mask(h));

        var closed = Wheel(open: false);
        Game(closed).TreasureRng = Always();
        Patch(closed, s => s["fall"] = new JsonObject
        {
            ["at"] = closed.Clock.UtcNow.ToString("O"), ["until"] = (closed.Clock.UtcNow + Clicker.FallShown).ToString("O"), ["x"] = 40,
        });
        var rc = Act(closed, "grab");
        Assert.True(rc.Ok, rc.Message);
        Assert.DoesNotContain("Зала скарбів", rc.Message);
    }

    // ---------- кораблі ----------

    static RoomHarness Pier(string[]? hall = null)
    {
        var h = Wheel();
        Patch(h, s =>
        {
            var up = Clicker.Buildings.Select(b => b.Key).TakeWhile(k => k != "pier").Append("pier");
            s["toloka"] = new JsonObject { ["built"] = new JsonArray(up.Select(x => (JsonNode)x).ToArray()) };
            s["styles"] = new JsonArray(Clicker.Ports.Select(p => (JsonNode)p.Style).Distinct().ToArray());
        });
        if (hall is not null) SetHall(h, hall);
        return h;
    }

    static void Dock(RoomHarness h, string port, bool envoy, int count = 2, int id = 1)
    {
        var style = Clicker.Ports.First(p => p.Key == port).Style;
        var now = h.Clock.UtcNow;
        var ship = new JsonObject
        {
            ["id"] = id, ["port"] = port, ["ware"] = "", ["style"] = style, ["quality"] = 3, ["count"] = count, ["lux"] = 0,
            ["at"] = now.ToString("O"), ["until"] = now.AddHours(envoy ? 12 : 8).ToString("O"), ["envoy"] = envoy,
        };
        Patch(h, s =>
        {
            s["trade"] = new JsonObject
            {
                ["slava"] = 0, ["firstDone"] = true, ["next"] = now.AddHours(5).ToString("O"), ["shipId"] = 5,
                ["ships"] = envoy ? new JsonArray() : new JsonArray(ship),
                ["envoy"] = envoy ? ship : null,
                ["envoyNext"] = now.AddHours(12).ToString("O"),
            };
            s["craft"]!["items"] = new JsonObject { [$"jug|{style}|3"] = 20 };
        });
    }

    static ActResult Load(RoomHarness h, int id = 1) => Act(h, "trade", new { @do = "loadAll", id });

    [Fact]
    public void The_port_decides_the_overseas_thing_two_ports_per_thing_and_the_envoy_always_brings_it()
    {
        Assert.Equal(Clicker.Ports.Select(p => p.Key).Order(), Clicker.TreasurePortItem.Keys.Order());
        Assert.Equal(6, Clicker.TreasurePortItem.Values.Distinct().Count());
        var h = Pier();
        foreach (var p in Clicker.Ports)
        {
            SetHall(h, []);
            Dock(h, p.Key, envoy: true);
            var r = Load(h);
            Assert.True(r.Ok, r.Message);
            Assert.Contains("Зала скарбів", r.Message);
            Assert.Equal(Bit(Clicker.TreasurePortItem[p.Key]), Mask(h));         // посол: річ напевно, саме за портом
        }
    }

    [Fact]
    public void An_ordinary_full_ship_brings_a_thing_with_a_quarter_chance()
    {
        var h = Pier();
        Dock(h, "delft", envoy: false);
        Assert.True(Load(h).Ok);
        Assert.Equal(0, Mask(h));                                                // кидок 0,99 — нічого

        Game(h).TreasureRng = Always();
        Dock(h, "kyoto", envoy: false, id: 2);
        var r = Load(h, 2);
        Assert.True(r.Ok, r.Message);
        Assert.Contains("Порцелянова чашка!", r.Message);
        Assert.Equal(Bit("chashka"), Mask(h));

        Dock(h, "kyoto", envoy: false, id: 3);
        Assert.Contains("уламок 1/5", Load(h, 3).Message);                       // та сама річ удруге — уламок вітрини
        Assert.Equal(1, Shards(h, 2));
    }

    [Fact]
    public void A_half_loaded_ship_brings_nothing_yet()
    {
        var h = Pier();
        Game(h).TreasureRng = Always();
        Dock(h, "athens", envoy: false, count: 50);
        var r = Act(h, "trade", new { @do = "load", id = 1, n = 1 });
        Assert.True(r.Ok, r.Message);
        Assert.DoesNotContain("Зала скарбів", r.Message);
        Assert.Equal(0, Mask(h));
    }

    // ---------- множник і бонуси вітрин ----------

    [Fact]
    public void Each_thing_is_two_percent_to_everything_and_the_whole_hall_ten_more()
    {
        var h = Wheel();
        Patch(h, s => s["upgrades"] = new JsonObject { ["apprentice"] = 40 });
        double Rate() => h.View(0).GetProperty("perSecond").GetDouble();
        var none = Rate();
        Assert.True(none > 0);
        SetHall(h, ["pysanka"]);
        Assert.Equal(1.02, Rate() / none, 6);
        SetHall(h, Keys(0, 5).Concat(Keys(1, 5)).ToArray());
        Assert.Equal(1.20, Rate() / none, 6);
        SetHall(h, Clicker.Treasures.Select(t => t.Key).ToArray());
        Assert.Equal(1.46, Rate() / none, 6);
    }

    [Fact]
    public void A_full_village_case_gives_a_quarter_more_from_shelf_jugs()
    {
        var h = Wheel();
        double Gain() => h.View(0).GetProperty("fall").GetProperty("gain").GetDouble();
        Patch(h, s => s["upgrades"] = new JsonObject { ["apprentice"] = 60, ["wheel"] = 30 });
        SetHall(h, Keys(0, 5));
        var five = Gain();
        SetHall(h, Keys(0));
        Assert.InRange(Gain() / five, 1.25, 1.30);                                // +25 % й +2 % до всього за шосту річ
    }

    [Fact]
    public void A_full_cossack_case_brings_golden_jugs_a_fifth_more_often()
    {
        var h = Wheel();
        SetHall(h, Keys(1));
        for (var i = 0; i < 4; i++)
        {
            Patch(h, s => s["golden"] = new JsonObject
            {
                ["at"] = h.Clock.UtcNow.ToString("O"), ["until"] = (h.Clock.UtcNow + Clicker.GoldenShown).ToString("O"),
                ["kind"] = (int)Clicker.GoldenKind.Merchant, ["x"] = 10, ["y"] = 10,
            });
            Assert.True(Act(h, "catch").Ok);
            var wait = (h.View(0).GetProperty("golden").GetProperty("at").GetDateTimeOffset() - h.Clock.UtcNow).TotalSeconds;
            Assert.InRange(wait, Clicker.GoldenMinSeconds / 1.2 - 0.01, Clicker.GoldenMaxSeconds / 1.2 + 0.01);
        }
    }

    [Fact]
    public void A_full_overseas_case_makes_ships_pay_a_quarter_more()
    {
        double Pay(string[] hall)
        {
            var h = Pier(hall);
            Dock(h, "delft", envoy: false);
            var before = h.View(0).GetProperty("pots").GetDouble();
            Assert.True(Load(h).Ok);
            return h.View(0).GetProperty("pots").GetDouble() - before;
        }
        var five = Pay(Keys(2, 5));
        var six = Pay(Keys(2));
        Assert.True(five > 0);
        Assert.Equal(1.25, six / five, 2);
    }

    // ---------- уся зала ----------

    [Fact]
    public void The_last_thing_completes_the_hall_gives_the_achievement_and_ten_percent()
    {
        var all = Clicker.Treasures.Select(t => t.Key).ToArray();
        var h = Pier(all.Where(k => k != "chashka").ToArray());
        Dock(h, "kyoto", envoy: true);
        var r = Load(h);
        Assert.True(r.Ok, r.Message);
        Assert.Contains("вітрина «Заморська вітрина» повна", r.Message);
        Assert.Contains("уся зала зібрана: ще +10 %", r.Message);
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-hall");
        Assert.Equal((1 << 18) - 1, Mask(h));
    }

    // ---------- загадка ----------

    static JsonElement Riddle(RoomHarness h) => Hall(h).GetProperty("riddle");
    static string RiddleText(RoomHarness h) => Riddle(h).GetProperty("text").GetString()!;
    static string[] Options(RoomHarness h) => Riddle(h).GetProperty("options").EnumerateArray().Select(x => x.GetString()!).ToArray();

    /// <summary>Правильна відповідь сьогоднішньої загадки (з книги за текстом).</summary>
    static string Answer(RoomHarness h)
    {
        var (book, _) = ClickerRiddleBook.Parse(File.ReadAllText(Paths.Resolve(ClickerRiddleService.DefaultFile)));
        return book.Riddles.First(r => r.Text == RiddleText(h)).Item;
    }

    [Fact]
    public void The_riddle_is_one_per_day_with_three_options_and_hides_the_answer_until_asked()
    {
        var h = Wheel();
        var r = Riddle(h);
        Assert.Equal(3, Options(h).Length);
        Assert.False(r.GetProperty("done").GetBoolean());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("item").ValueKind);
        Assert.Equal(RiddleText(h), RiddleText(h));
        Assert.Contains(Answer(h), Options(h));
        var h2 = Wheel();                                                        // той самий нік і день — та сама загадка
        Assert.Equal(RiddleText(h), RiddleText(h2));
    }

    [Fact]
    public void A_right_answer_gives_a_shard_of_that_things_case_and_only_one_try_a_day()
    {
        var h = Wheel();
        var item = Answer(h);
        var c = Clicker.Treasures.First(t => t.Key == item).Case;
        var res = Riddle(h, item);
        Assert.True(res.Ok, res.Message);
        Assert.Contains("Вгадав", res.Message);
        Assert.Equal(1, Shards(h, c));
        Assert.Equal(1, Shards(h, 0) + Shards(h, 1) + Shards(h, 2));
        Assert.Equal(0, Mask(h));                                                // уламок, а не сама річ
        Assert.True(Riddle(h).GetProperty("right").GetBoolean());
        Assert.Equal(item, Riddle(h).GetProperty("item").GetString());
        var again = Riddle(h, item);
        Assert.False(again.Ok);
        Assert.Equal(1, Shards(h, c));
    }

    [Fact]
    public void A_wrong_answer_gives_nothing_shows_the_right_one_and_waits_for_tomorrow()
    {
        var h = Wheel();
        var item = Answer(h);
        var wrong = Options(h).First(o => o != item);
        var res = Riddle(h, wrong);
        Assert.True(res.Ok, res.Message);
        Assert.Contains("Не вгадав", res.Message);
        Assert.Contains(Clicker.Treasures.First(t => t.Key == item).Name.ToLowerInvariant(), res.Message);
        Assert.Equal(0, Shards(h, 0) + Shards(h, 1) + Shards(h, 2));
        Assert.True(Riddle(h).GetProperty("done").GetBoolean());
        Assert.False(Riddle(h).GetProperty("right").GetBoolean());
        Assert.Equal(item, Riddle(h).GetProperty("item").GetString());
        Assert.False(Riddle(h, item).Ok);

        var yesterday = RiddleText(h);
        h.Clock.Advance(TimeSpan.FromDays(1));
        Assert.False(Riddle(h).GetProperty("done").GetBoolean());                // нова доба — нова загадка
        Assert.NotEqual(yesterday, RiddleText(h));
    }

    [Fact]
    public void An_option_outside_the_riddle_is_refused()
    {
        var h = Wheel();
        var outside = Clicker.Treasures.First(t => !Options(h).Contains(t.Key)).Key;
        Assert.False(Riddle(h, outside).Ok);
        Assert.False(Riddle(h, "nonsense").Ok);
        Assert.False(Riddle(h).GetProperty("done").GetBoolean());
    }

    [Fact]
    public void Riddles_do_not_repeat_until_all_of_them_have_been_given()
    {
        var h = Wheel();
        var (book, _) = ClickerRiddleBook.Parse(File.ReadAllText(Paths.Resolve(ClickerRiddleService.DefaultFile)));
        var seen = new HashSet<string>();
        for (var day = 0; day < book.Riddles.Count; day++)
        {
            Assert.True(seen.Add(RiddleText(h)), $"доба {day}: загадка повторилась");
            Assert.True(Riddle(h, Options(h)[0]).Ok);
            h.Clock.Advance(TimeSpan.FromDays(1));
        }
        Assert.Equal(book.Riddles.Count, seen.Count);
        Assert.Contains(RiddleText(h), seen);                                    // усі пройдено — коло почалось знову
    }

    [Fact]
    public void The_fifth_riddle_shard_glues_a_thing_and_a_full_case_takes_no_shards()
    {
        var h = Wheel();
        var item = Answer(h);
        var c = Clicker.Treasures.First(t => t.Key == item).Case;
        var shards = new[] { 0, 0, 0 };
        shards[c] = 4;
        SetHall(h, [], shards);
        var res = Riddle(h, item);
        Assert.True(res.Ok, res.Message);
        Assert.Contains("склеєно", res.Message);
        Assert.Equal(0, Shards(h, c));
        Assert.Equal(1, Count(Mask(h)));
        Assert.Equal(c, Clicker.Treasures[System.Numerics.BitOperations.TrailingZeroCount(Mask(h))].Case);

        var h2 = Wheel();
        var item2 = Answer(h2);
        var c2 = Clicker.Treasures.First(t => t.Key == item2).Case;
        SetHall(h2, Keys(c2));
        var res2 = Riddle(h2, item2);
        Assert.True(res2.Ok, res2.Message);
        Assert.Contains("уже повна", res2.Message);
        Assert.Equal(0, Shards(h2, c2));
    }

    [Fact]
    public void The_riddle_needs_an_open_hall()
    {
        var h = Wheel(open: false);
        Assert.False(Riddle(h, "pysanka").Ok);
    }

    // ---------- збереження ----------

    [Fact]
    public void Things_shards_and_todays_riddle_survive_save_and_load()
    {
        var h = Wheel();
        var item = Answer(h);
        var c = Clicker.Treasures.First(t => t.Key == item).Case;
        SetHall(h, ["pysanka", "pernach", "kresalo"], [1, 2, 3]);
        var other = Options(h).First(o => o != item);
        Assert.True(Riddle(h, other).Ok);
        var text = RiddleText(h);
        var mask = Mask(h);

        Patch(h, _ => { });
        Assert.Equal(mask, Mask(h));
        Assert.Equal(Bit("pysanka") | Bit("pernach") | Bit("kresalo"), mask);
        Assert.Equal([1, 2, 3], new[] { Shards(h, 0), Shards(h, 1), Shards(h, 2) });
        Assert.Equal(text, RiddleText(h));
        Assert.True(Riddle(h).GetProperty("done").GetBoolean());
        Assert.False(Riddle(h, item).Ok);                                        // спроба на добу не повертається з перезавантаженням

        var saved = JsonNode.Parse(h.Room.Game.Save()!)!["album"]!.AsObject();
        Assert.Equal(3, saved["hall"]!.AsArray().Count);
        Assert.Contains("pysanka", saved["hall"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.NotNull(saved["riddle"]);
        Assert.True(c >= 0);
    }

    [Fact]
    public void Garbage_in_the_save_is_dropped()
    {
        var h = Wheel();
        Patch(h, s =>
        {
            var a = AlbumNode(s);
            a["hall"] = new JsonArray("pysanka", "nonsense", "pysanka");
            a["hallShards"] = new JsonArray(99, -4, 3, 7);
        });
        Assert.Equal(Bit("pysanka"), Mask(h));
        Assert.Equal(4, Shards(h, 0));
        Assert.Equal(0, Shards(h, 1));
        Assert.Equal(3, Shards(h, 2));
    }
}
