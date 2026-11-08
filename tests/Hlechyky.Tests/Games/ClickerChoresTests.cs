using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «📜 Доручення Глека» (v13 Г3, ClickerChores.cs): три різні доручення на добу з доступних гончареві (вибір за ніком і днем),
/// лічильники справ руками, скриня (хвилини гри, Слава, черепки × Boost) і серія днів 1…7 з великою скринею на сьомий.
/// </summary>
public class ClickerChoresTests
{
    static readonly string[] Everything = ["catch", "gold", "fire", "ship", "village", "guest", "toloka", "gift", "fest", "riddle"];

    sealed class Tsekh
    {
        public FakeStore Store { get; } = new();
        public FakeClock Clock { get; } = new();
        public ClickerGuildService Svc { get; }
        public Tsekh() => Svc = new ClickerGuildService(Store, Clock);

        public RoomHarness Potter(string nick)
        {
            var h = new RoomHarness("clicker", services: RoomHarness.WithService(Svc));
            h.Solo(nick);
            Assert.True(h.Act(0, "look").Ok);
            lock (h.Room.Sync) Store.SaveState("clicker:" + ClickerGuildService.Key(nick), h.Room.Game.Save()!);
            return h;
        }
    }

    sealed class Via(Func<DateTimeOffset> now) : IClock
    {
        public DateTimeOffset UtcNow => now();
    }

    /// <summary>Гончар із сервісом дня (Economy:Boost = ×10) і фальшивим гаманцем — щоб бачити черепки скрині.</summary>
    static (RoomHarness H, FakeStakes Stakes) Wheel(string nick = "Оля")
    {
        RoomHarness h = null!;
        var stakes = new FakeStakes();
        var day = new ClickerDayService(null, stakes, () => new ClickerDayOptions(), new Via(() => h.Clock.UtcNow), person: _ => true) { Earn = n => n * 10 };
        var sp = new ServiceCollection().AddSingleton(day).AddSingleton<IStakes>(stakes).AddSingleton(new ClickerRiddleService()).BuildServiceProvider();
        h = new RoomHarness("clicker", services: sp);
        h.Solo(nick);
        return (h, stakes);
    }

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static string Today(RoomHarness h) => ClickerDayService.DayOf(h.Clock.UtcNow);
    static string Key(string code) => Clicker.ChoreCatalog[Clicker.ChoreCodes.IndexOf(code)].Key;
    static JsonElement? Chores(RoomHarness h) => h.View(0).TryGetProperty("chores", out var c) && c.ValueKind == JsonValueKind.Array ? c : null;
    static List<(string Key, int N)> Tasks(RoomHarness h) =>
        System.Text.RegularExpressions.Regex.Matches(Chores(h)!.Value[0].GetString()!, @"([a-z])(\d+)").Select(m => (Key(m.Groups[1].Value), int.Parse(m.Groups[2].Value))).ToList();
    static List<string> Picks(RoomHarness h) => Tasks(h).Select(t => t.Key).ToList();
    static int Prog(RoomHarness h, string key) => Tasks(h).First(t => t.Key == key).N;
    static int Streak(RoomHarness h) => Chores(h)!.Value[1].GetInt32();
    static bool Taken(RoomHarness h) => Chores(h)!.Value[2].GetInt32() == 1;
    static ActResult Claim(RoomHarness h) => h.Act(0, "chores", new { op = "claim" });
    static ActResult Spin(RoomHarness h) => h.Act(0, "spin", PotterHands.Human(3));
    static double Pots(RoomHarness h) => h.View(0).GetProperty("pots").GetDouble();
    static long Slava(RoomHarness h) =>
        h.View(0).TryGetProperty("trade", out var t) && t.ValueKind == JsonValueKind.Object ? t.GetProperty("slava").GetInt64() : 0;

    /// <summary>Усе, що може знадобитись: гроші, вироби, розпис, Пристань, фестиваль, зала скарбів.</summary>
    static void Rich(RoomHarness h, bool pier = true) => Patch(h, s =>
    {
        s["pots"] = 0;                                              // з нуля: різницю видно (при 1e30 вона тоне)
        s["total"] = 1e40;
        s["styles"] = new JsonArray("kosiv");
        s["craft"] = new JsonObject { ["firedBy"] = new JsonObject { ["pot"] = 50 }, ["items"] = new JsonObject { ["pot||1"] = 15, ["bowl||1"] = 15 } };
        var built = pier
            ? Clicker.Buildings.Select(b => b.Key).TakeWhile(k => k != "pier").Append("pier").Append("festival").Distinct()
            : [];
        s["toloka"] = new JsonObject { ["built"] = new JsonArray(built.Select(x => (JsonNode)x).ToArray()) };
        s["album"] = new JsonObject { ["cells"] = new JsonObject { ["pot"] = new JsonArray("") } };
    });

    /// <summary>Підміняє доручення на сьогодні на задані (і прогрес); серія й остання скриня лишаються.</summary>
    static void Force(RoomHarness h, string[] pick, int[]? prog = null, bool taken = false) => Patch(h, s =>
    {
        var old = s["chores"] as JsonObject;
        s["chores"] = new JsonObject
        {
            ["day"] = Today(h), ["pick"] = new JsonArray(pick.Select(x => (JsonNode)x).ToArray()),
            ["prog"] = new JsonArray((prog ?? new int[pick.Length]).Select(x => (JsonNode)x).ToArray()), ["taken"] = taken,
            ["streak"] = old?["streak"]?.GetValue<int>() ?? 0, ["last"] = old?["last"]?.GetValue<string>() ?? "",
        };
    });

    /// <summary>Усі три виконано.</summary>
    static void AllDone(RoomHarness h) => Force(h, ["catch", "gold", "fire"], [30, 3, 1]);

    static void FallNow(RoomHarness h) => Patch(h, s =>
        s["fall"] = new JsonObject { ["at"] = h.Clock.UtcNow.ToString("O"), ["until"] = (h.Clock.UtcNow + Clicker.FallShown).ToString("O"), ["x"] = 40 });

    static void RackDry(RoomHarness h, int n) => Patch(h, s =>
    {
        var rack = new JsonArray();
        for (var i = 0; i < n; i++) rack.Add(new JsonObject { ["ware"] = "pot", ["clay"] = "", ["dryAt"] = h.Clock.UtcNow.AddMinutes(-1).ToString("O") });
        s["craft"]!["rack"] = rack;
    });

    // ---------- вибір ----------

    [Fact]
    public void A_fresh_potter_gets_only_what_he_can_do_today()
    {
        var (h, _) = Wheel();
        Assert.Equal(["catch", "gold"], Picks(h).Order().ToArray());
        Assert.Equal(Today(h), JsonNode.Parse(h.Room.Game.Save()!)!["chores"]!["day"]!.GetValue<string>());
    }

    [Fact]
    public void Three_different_available_chores_the_same_all_day_and_for_the_same_nick()
    {
        var (h, _) = Wheel("Оля");
        Rich(h);
        Patch(h, s => s["chores"] = null);                              // пул щойно розширився — вибір заново
        var first = Picks(h);
        Assert.Equal(3, first.Count);
        Assert.Equal(3, first.Distinct().Count());
        Assert.All(first, k => Assert.Contains(k, Everything));
        Assert.DoesNotContain(first, k => k is "guest" or "gift");      // ні гостей, ні цеху — цього гончареві не дають
        Assert.Equal(first, Picks(h));
        Spin(h);
        Assert.Equal(first, Picks(h));
        // Той самий нік і день в іншій кімнаті — ті самі доручення (вибір детермінований).
        var (h2, _) = Wheel("Оля");
        Rich(h2);
        Patch(h2, s => s["chores"] = null);
        Assert.Equal(first, Picks(h2));
    }

    [Fact]
    public void A_chore_that_the_potter_cannot_do_is_never_given()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 40; i++)
        {
            var (h, _) = Wheel("гончар" + i);
            Rich(h, pier: false);
            Patch(h, s => { s["album"] = new JsonObject(); s["chores"] = null; });
            foreach (var k in Picks(h)) seen.Add(k);
        }
        Assert.DoesNotContain("ship", seen);
        Assert.DoesNotContain("fest", seen);
        Assert.DoesNotContain("guest", seen);
        Assert.DoesNotContain("gift", seen);
        Assert.DoesNotContain("riddle", seen);                          // зала скарбів зачинена: у альбомі нема клітинок
        Assert.Contains("fire", seen);
        Assert.Contains("village", seen);
        Assert.Contains("toloka", seen);

        seen.Clear();
        for (var i = 0; i < 40; i++)
        {
            var (h, _) = Wheel("майстер" + i);
            Rich(h);
            Patch(h, s => s["chores"] = null);
            foreach (var k in Picks(h)) seen.Add(k);
        }
        Assert.Contains("ship", seen);
        Assert.Contains("fest", seen);
        Assert.Contains("riddle", seen);
    }

    [Fact]
    public void A_new_day_brings_new_chores_and_empties_the_progress()
    {
        var (h, _) = Wheel();
        Force(h, ["catch", "gold"], [7, 1]);
        Assert.Equal(7, Prog(h, "catch"));
        h.Clock.Advance(TimeSpan.FromHours(24));
        Assert.Equal(0, Prog(h, Picks(h)[0]));
        Assert.Equal(Today(h), JsonNode.Parse(h.Room.Game.Save()!)!["chores"]!["day"]!.GetValue<string>());
    }

    // ---------- лічильники ----------

    [Fact]
    public void Catching_shelf_jugs_by_hand_counts_and_stops_at_the_goal()
    {
        var (h, _) = Wheel();
        Force(h, ["catch", "gold"], [28, 0]);
        FallNow(h);
        Assert.True(h.Act(0, "grab").Ok);
        Assert.Equal(29, Prog(h, "catch"));
        FallNow(h);
        Assert.True(h.Act(0, "grab").Ok);
        Assert.Equal(30, Prog(h, "catch"));
        FallNow(h);
        Assert.True(h.Act(0, "grab").Ok);
        Assert.Equal(30, Prog(h, "catch"));                            // стеля — ціль
    }

    [Fact]
    public void Golden_jugs_count_for_the_gold_chore()
    {
        var (h, _) = Wheel();
        Force(h, ["catch", "gold"]);
        Patch(h, s => s["golden"] = new JsonObject
        {
            ["at"] = h.Clock.UtcNow.ToString("O"), ["until"] = (h.Clock.UtcNow + Clicker.GoldenShown).ToString("O"),
            ["kind"] = (int)Clicker.GoldenKind.Merchant, ["x"] = 10, ["y"] = 10,
        });
        Assert.True(h.Act(0, "catch").Ok);
        Assert.Equal(1, Prog(h, "gold"));
        Assert.Equal(0, Prog(h, "catch"));
    }

    [Fact]
    public void A_village_order_and_a_guest_order_count_when_delivered_by_hand()
    {
        var (h, _) = Wheel();
        Rich(h, pier: false);
        Force(h, ["village", "guest", "catch"]);
        var now = h.Clock.UtcNow;
        Patch(h, s =>
        {
            s["fair"] = new JsonObject
            {
                ["orders"] = new JsonArray(new JsonObject
                {
                    ["id"] = 100, ["village"] = "opishnia", ["who"] = 0, ["ware"] = "pot", ["style"] = "", ["quality"] = 1, ["count"] = 2, ["mult"] = 2,
                    ["at"] = now.ToString("O"), ["until"] = now.AddMinutes(30).ToString("O"), ["lord"] = false, ["sour"] = false,
                }),
                ["orderNext"] = now.AddHours(5).ToString("O"),
            };
        });
        Assert.True(h.Act(0, "fair", new { op = "deliver", id = 100 }).Ok);
        Assert.Equal(1, Prog(h, "village"));
        Assert.Equal(0, Prog(h, "guest"));

        var guest = Clicker.Guests[0].Key;
        Patch(h, s =>
        {
            s["guests"] = new JsonObject
            {
                ["met"] = new JsonObject { [guest] = now.AddHours(-1).ToString("O") }, ["rep"] = new JsonObject(),
                ["orders"] = new JsonArray(new JsonObject
                {
                    ["id"] = 7, ["guest"] = guest, ["who"] = 0, ["ware"] = "pot", ["style"] = "", ["quality"] = 1, ["count"] = 2,
                    ["at"] = now.ToString("O"), ["until"] = now.AddMinutes(200).ToString("O"),
                }),
                ["orderId"] = 100, ["next"] = now.AddMinutes(40).ToString("O"), ["delivered"] = 0,
            };
            s["craft"]!["items"] = new JsonObject { ["pot||1"] = 10 };
        });
        var g = h.Act(0, "guests", new { @do = "give", id = 7 });
        Assert.True(g.Ok, g.Message);
        Assert.Equal(1, Prog(h, "guest"));
    }

    [Fact]
    public void Laying_a_toloka_stage_counts_items_and_the_riddle_and_the_festival_count_too()
    {
        var (h, _) = Wheel();
        Rich(h);
        Patch(h, s => { s["toloka"] = new JsonObject(); s["pots"] = 1e30; });   // перший етап — криниця
        Force(h, ["toloka", "riddle", "catch"]);
        var lay = h.Act(0, "toloka", new { @do = "lay" });
        Assert.True(lay.Ok, lay.Message);
        Assert.Equal(20, Prog(h, "toloka"));                           // 30 виробів пішло, але ціль — 20

        var riddle = h.View(0).GetProperty("album").GetProperty("hall").GetProperty("riddle");
        var r = h.Act(0, "album", new { op = "riddle", pick = riddle.GetProperty("options")[0].GetString() });
        Assert.True(r.Ok, r.Message);
        Assert.Equal(1, Prog(h, "riddle"));                            // будь-яка відповідь

        Rich(h);
        Force(h, ["fest", "catch", "gold"]);
        Assert.True(h.Act(0, "toloka", new { @do = "festival" }).Ok);
        Assert.Equal(1, Prog(h, "fest"));
    }

    [Fact]
    public void Loading_a_ship_to_full_counts_but_a_partial_load_does_not()
    {
        var (h, _) = Wheel();
        Rich(h);
        var now = h.Clock.UtcNow;
        Patch(h, s =>
        {
            s["craft"]!["items"] = new JsonObject { ["pot||3"] = 20 };
            s["trade"] = new JsonObject
            {
                ["slava"] = 0, ["firstDone"] = true, ["next"] = now.AddMinutes(40).ToString("O"), ["shipId"] = 5, ["envoyNext"] = now.AddHours(12).ToString("O"),
                ["ships"] = new JsonArray(new JsonObject
                {
                    ["id"] = 5, ["port"] = "delft", ["ware"] = "pot", ["style"] = "", ["quality"] = 3, ["count"] = 12, ["lux"] = 0,
                    ["at"] = now.ToString("O"), ["until"] = now.AddHours(8).ToString("O"), ["envoy"] = false,
                }),
            };
        });
        Force(h, ["ship", "catch", "gold"]);
        var part = h.Act(0, "trade", new { @do = "load", id = 5, n = 5, keep = false });
        Assert.True(part.Ok, part.Message);
        Assert.Equal(0, Prog(h, "ship"));
        var full = h.Act(0, "trade", new { @do = "loadAll", id = 5, n = 20, keep = false });
        Assert.True(full.Ok, full.Message);
        Assert.Equal(1, Prog(h, "ship"));
    }

    [Fact]
    public void A_gift_to_a_friend_counts()
    {
        var g = new Tsekh();
        var ola = g.Potter("Оля");
        g.Potter("Петро");
        Patch(ola, s => s["craft"] = new JsonObject { ["firedBy"] = new JsonObject { ["pot"] = 5 }, ["items"] = new JsonObject { ["pot||1"] = 2 } });
        Force(ola, ["gift", "catch", "gold"]);
        var r = ola.Act(0, "guild", new { op = "gift", nick = "Петро", key = "pot||1", keep = false });
        Assert.True(r.Ok, r.Message);
        Assert.Equal(1, Prog(ola, "gift"));
    }

    [Fact]
    public void A_hand_fired_painted_batch_counts_but_a_helper_batch_does_not()
    {
        var (h, _) = Wheel();
        Patch(h, s => { s["styles"] = new JsonArray("kosiv"); s["craft"] = new JsonObject { ["firedBy"] = new JsonObject { ["pot"] = 50 } }; });
        Force(h, ["fire", "catch", "gold"]);
        RackDry(h, 6);
        Assert.True(h.Act(0, "kiln", new { op = "paint", style = "kosiv", tech = "" }).Ok);
        Assert.True(h.Act(0, "kiln", new { op = "light" }).Ok);
        h.Clock.Advance(KilnHeat.Steps / 10.0 + 0.2 + Clicker.KilnOwnWait.TotalSeconds);
        var open = h.Act(0, "kiln", new { op = "open", t = Array.Empty<int[]>(), own = true });
        Assert.True(open.Ok, open.Message);
        Assert.Equal(1, Prog(h, "fire"));

        Force(h, ["fire", "catch", "gold"]);
        h.Clock.Advance(Clicker.KilnCool + TimeSpan.FromSeconds(1));
        RackDry(h, 6);
        Assert.True(h.Act(0, "kiln", new { op = "light", helper = true }).Ok);
        h.Clock.Advance(40);
        Spin(h);
        Assert.Equal(0, Prog(h, "fire"));                               // палій — не руки
    }

    // ---------- скриня ----------

    [Fact]
    public void The_chest_opens_only_when_all_three_are_done_and_pays_minutes_slava_and_boosted_shards()
    {
        var (h, stakes) = Wheel("Оля");
        Rich(h);
        Force(h, ["catch", "gold", "fire"], [30, 3, 0]);
        var early = Claim(h);
        Assert.False(early.Ok);
        Assert.Contains("всі доручення", early.Message);

        AllDone(h);
        var pots = Pots(h);
        var slava = Slava(h);
        var r = Claim(h);
        Assert.True(r.Ok, r.Message);
        Assert.True(Pots(h) > pots);
        Assert.Equal(slava + 200, Slava(h));
        Assert.Equal(300, stakes.Balance("Оля"));                       // 30 × Boost(10)
        Assert.Equal(1, Streak(h));
        Assert.True(Taken(h));

        var again = Claim(h);                                           // без подвійного забирання
        Assert.False(again.Ok);
        Assert.Equal(300, stakes.Balance("Оля"));
        Assert.Equal(slava + 200, Slava(h));
    }

    [Fact]
    public void Without_trade_the_chest_pays_more_play_minutes_instead_of_glory()
    {
        var (h, _) = Wheel();
        Rich(h, pier: false);
        AllDone(h);
        var before = Pots(h);
        Assert.True(Claim(h).Ok);
        var plain = Pots(h) - before;

        var (h2, _) = Wheel("Оля2");
        Rich(h2);
        AllDone(h2);
        var before2 = Pots(h2);
        Assert.True(Claim(h2).Ok);
        var withTrade = Pots(h2) - before2;
        Assert.True(plain > withTrade * 1.3, $"без торгівлі {plain} має бути помітно більше, ніж {withTrade}");
    }

    [Fact]
    public void The_streak_grows_each_day_the_seventh_chest_is_big_then_it_starts_over()
    {
        var (h, stakes) = Wheel("Оля");
        Rich(h);
        for (var day = 1; day <= 7; day++)
        {
            AllDone(h);
            var r = Claim(h);
            Assert.True(r.Ok, r.Message);
            Assert.Equal(day, Streak(h));
            if (day < 7) h.Clock.Advance(TimeSpan.FromHours(24));
        }
        // Сьомий: «Свято» 60 хв у запасі і ще 100 × 10 черепків (разом із 300 за звичайну скриню).
        Assert.Equal(60, h.View(0).GetProperty("buffs").GetProperty("feast").GetProperty("bank").GetInt32());
        Assert.Equal(7 * 300 + 1000, stakes.Balance("Оля"));
        h.Clock.Advance(TimeSpan.FromHours(24));
        Assert.Equal(0, Streak(h));                                     // після сьомої нова серія ще не почалась
        AllDone(h);
        Assert.True(Claim(h).Ok);
        Assert.Equal(1, Streak(h));
        Assert.Equal(7 * 300 + 1000 + 300, stakes.Balance("Оля"));
    }

    [Fact]
    public void A_missed_day_sends_the_streak_back_to_one()
    {
        var (h, _) = Wheel();
        Rich(h);
        AllDone(h);
        Assert.True(Claim(h).Ok);
        h.Clock.Advance(TimeSpan.FromHours(24));
        AllDone(h);
        Assert.True(Claim(h).Ok);
        Assert.Equal(2, Streak(h));
        h.Clock.Advance(TimeSpan.FromHours(48));                        // скриню за позавчора й учора не забрано
        Assert.Equal(0, Streak(h));
        AllDone(h);
        Assert.True(Claim(h).Ok);
        Assert.Equal(1, Streak(h));
    }

    [Fact]
    public void Chores_and_the_streak_survive_a_save_and_a_load()
    {
        var (h, _) = Wheel();
        Rich(h);
        AllDone(h);
        Assert.True(Claim(h).Ok);
        var picks = Picks(h);
        Patch(h, _ => { });                                             // Save → Load
        Assert.Equal(picks, Picks(h));
        Assert.Equal(1, Streak(h));
        Assert.False(Claim(h).Ok);                                      // забрана скриня лишилась забраною
    }

    // ---------- рецензія v13 ----------

    [Fact]
    public void The_festival_chore_is_not_given_when_the_next_festival_is_later_than_the_end_of_the_day()
    {
        var seenLate = new HashSet<string>();
        var seenSoon = new HashSet<string>();
        for (var i = 0; i < 40; i++)
        {
            var (h, _) = Wheel("пізній" + i);
            Rich(h);
            Patch(h, s => { s["toloka"]!["festivalNext"] = h.Clock.UtcNow.AddHours(30).ToString("O"); s["chores"] = null; });
            foreach (var k in Picks(h)) seenLate.Add(k);
            var (g, _) = Wheel("ранній" + i);
            Rich(g);
            Patch(g, s => { s["toloka"]!["festivalNext"] = g.Clock.UtcNow.AddMinutes(10).ToString("O"); s["chores"] = null; });
            foreach (var k in Picks(g)) seenSoon.Add(k);
        }
        Assert.DoesNotContain("fest", seenLate);
        Assert.Contains("fest", seenSoon);
    }

    [Fact]
    public void The_guest_chore_needs_a_guest_order_not_only_a_met_guest()
    {
        var seen = new HashSet<string>();
        var seenWith = new HashSet<string>();
        var guest = Clicker.Guests[0].Key;
        for (var i = 0; i < 40; i++)
        {
            var (h, _) = Wheel("без" + i);
            Rich(h);
            var now = h.Clock.UtcNow;
            Patch(h, s =>
            {
                s["guests"] = new JsonObject { ["met"] = new JsonObject { [guest] = now.AddHours(-1).ToString("O") }, ["rep"] = new JsonObject(), ["orders"] = new JsonArray() };
                s["chores"] = null;
            });
            foreach (var k in Picks(h)) seen.Add(k);
            var (g, _) = Wheel("з" + i);
            Rich(g);
            Patch(g, s =>
            {
                s["guests"] = new JsonObject
                {
                    ["met"] = new JsonObject { [guest] = now.AddHours(-1).ToString("O") }, ["rep"] = new JsonObject(),
                    ["orders"] = new JsonArray(new JsonObject
                    {
                        ["id"] = 7, ["guest"] = guest, ["who"] = 0, ["ware"] = "pot", ["style"] = "", ["quality"] = 1, ["count"] = 2,
                        ["at"] = now.ToString("O"), ["until"] = now.AddMinutes(200).ToString("O"),
                    }),
                    ["orderId"] = 100, ["next"] = now.AddMinutes(40).ToString("O"), ["delivered"] = 0,
                };
                s["chores"] = null;
            });
            foreach (var k in Picks(g)) seenWith.Add(k);
        }
        Assert.DoesNotContain("guest", seen);
        Assert.Contains("guest", seenWith);
    }

    [Fact]
    public void The_streak_survives_the_clock_change_day_because_yesterday_is_found_by_the_calendar()
    {
        var (h, _) = Wheel();
        Rich(h);
        // Київ переводить годинник 25.10.2026: доба на 25 годин. Остання година 25.10 за 24 год тому вже «25.10» — не «вчора».
        h.Clock.UtcNow = new DateTimeOffset(2026, 10, 25, 21, 30, 0, TimeSpan.Zero);   // 23:30 за київським 25.10
        Assert.Equal("2026-10-25", Days.Of(h.Clock.UtcNow));
        Patch(h, s => s["chores"] = new JsonObject { ["day"] = "2026-10-25", ["pick"] = new JsonArray("catch", "gold", "fire"), ["prog"] = new JsonArray(30, 3, 1), ["taken"] = false, ["streak"] = 3, ["last"] = "2026-10-24" });
        var r = Claim(h);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(4, Streak(h));
    }
}
