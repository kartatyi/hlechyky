using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Кіт спить, серія тріскає (ClickerCatNap.cs, 21.10): «🥛 Молока котові» — глеки з полиці не падають і серія чекає;
/// розбитий глек відколює від серії частку, що росте з кожним розбитим поспіль, а не обнуляє її.
/// </summary>
public class ClickerCatNapTests
{
    static RoomHarness Wheel(int seed = 1)
    {
        var h = new RoomHarness("clicker", seed: seed);
        h.Solo("владік");
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static JsonElement Fall(RoomHarness h) => View(h).GetProperty("fall");
    static DateTimeOffset FallAt(RoomHarness h) => Fall(h).GetProperty("at").GetDateTimeOffset();
    static int Streak(RoomHarness h) => Fall(h).GetProperty("streak").GetInt32();
    static string Miss(RoomHarness h) => Fall(h).GetProperty("miss").GetString()!;
    static string? NapOf(RoomHarness h) => Fall(h).GetProperty("nap").GetString();
    static double Pots(RoomHarness h) => View(h).GetProperty("pots").GetDouble();

    static ActResult Act(RoomHarness h, string action, object? payload = null) => h.Act(0, action, payload);
    static ActResult Spin(RoomHarness h) => Act(h, "spin", PotterHands.Human(5));
    static ActResult Nap(RoomHarness h, bool on) => Act(h, "nap", new { on });

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static void FallIn(RoomHarness h, double seconds) => Patch(h, s =>
    {
        var at = h.Clock.UtcNow.AddSeconds(seconds);
        s["fall"] = new JsonObject { ["at"] = at.ToString("O"), ["until"] = (at + Clicker.FallShown).ToString("O"), ["x"] = 40 };
    });

    static readonly TimeSpan Past = Clicker.FallShown + Clicker.CatchGrace + TimeSpan.FromSeconds(0.5);

    /// <summary>Гончар біля кола з серією, глек на полиці злетить за <paramref name="jugIn"/> с.</summary>
    static RoomHarness AtTheWheel(int streak, double jugIn)
    {
        var h = Wheel();
        Patch(h, s => s["fallStreak"] = streak);
        FallIn(h, jugIn);
        Assert.True(Spin(h).Ok);
        return h;
    }

    /// <summary>Глек летить на очах і розбивається: гончар клацає коло весь його політ.</summary>
    static void MissOne(RoomHarness h)
    {
        FallIn(h, 0);
        Assert.True(Spin(h).Ok);
        h.Clock.Advance(Past);
        Assert.True(Spin(h).Ok);
    }

    // ---------- кіт спить ----------

    [Fact]
    public void A_sleeping_cat_drops_no_jugs_and_the_streak_waits()
    {
        var h = AtTheWheel(1956, jugIn: 30);
        var r = Nap(h, true);
        Assert.True(r.Ok, r.Message);
        Assert.Contains("серія 1956 чекає", r.Message);
        Assert.Equal("on", NapOf(h));
        Assert.Equal("nap", Miss(h));
        Assert.Equal(Clicker.NapParked, FallAt(h));

        // Година біля кола: клацає щохвилини — і жоден глек не впав, серія не росла й не тріснула.
        for (var i = 0; i < 60; i++)
        {
            h.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.True(Spin(h).Ok);
        }
        Assert.Equal(1956, Streak(h));
        var grab = Act(h, "grab");
        Assert.False(grab.Ok);
        Assert.Contains("Кіт спить", grab.Message);
    }

    [Fact]
    public void Waking_the_cat_brings_the_next_jug_in_a_minute_or_two_and_the_streak_goes_on()
    {
        var h = AtTheWheel(100, jugIn: 30);
        Assert.True(Nap(h, true).Ok);
        h.Clock.Advance(TimeSpan.FromHours(3));
        var r = Nap(h, false);
        Assert.True(r.Ok, r.Message);
        Assert.Null(NapOf(h));
        var wait = (FallAt(h) - h.Clock.UtcNow).TotalSeconds;
        Assert.InRange(wait, Clicker.FallMinSeconds * 0.5, Clicker.FallMaxSeconds * 1.5);

        h.Clock.Advance(FallAt(h) - h.Clock.UtcNow + TimeSpan.FromMilliseconds(300));
        Assert.True(Act(h, "grab").Ok);
        Assert.Equal(101, Streak(h));
    }

    [Fact]
    public void A_jug_already_flying_still_has_to_be_caught_and_then_the_cat_sleeps()
    {
        var h = AtTheWheel(10, jugIn: 0);
        var r = Nap(h, true);
        Assert.True(r.Ok);
        Assert.Contains("доскидає", r.Message);
        Assert.Equal("soon", NapOf(h));
        Assert.True(Act(h, "grab").Ok);                       // цей ще ловиться
        Assert.Equal(11, Streak(h));
        Assert.Equal("on", NapOf(h));                         // а далі — сон, глека нема
        Assert.Equal(Clicker.NapParked, FallAt(h));

        // Не спіймав — тріщина, як і без кота, і далі кіт спить.
        var g = AtTheWheel(10, jugIn: 0);
        Assert.True(Nap(g, true).Ok);
        g.Clock.Advance(Past);
        Assert.True(Spin(g).Ok);
        Assert.Equal(9, Streak(g));
        Assert.Equal("on", NapOf(g));
    }

    [Fact]
    public void Napping_twice_or_waking_an_awake_cat_changes_nothing()
    {
        var h = AtTheWheel(5, jugIn: 40);
        var at = FallAt(h);
        Assert.True(Nap(h, false).Ok);
        Assert.Equal(at, FallAt(h));
        Assert.True(Nap(h, true).Ok);
        Assert.True(Nap(h, true).Ok);
        Assert.Equal("on", NapOf(h));
    }

    [Fact]
    public void The_cat_keeps_sleeping_after_a_reload()
    {
        var h = AtTheWheel(42, jugIn: 30);
        Assert.True(Nap(h, true).Ok);
        Patch(h, _ => { });
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(Spin(h).Ok);
        Assert.Equal("on", NapOf(h));
        Assert.Equal(42, Streak(h));
        Assert.True(Nap(h, false).Ok);
        Assert.True(FallAt(h) < h.Clock.UtcNow + TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void A_sleeping_cat_does_not_come_visiting()
    {
        var h = AtTheWheel(5, jugIn: 30);
        Patch(h, s =>
        {
            var at = h.Clock.UtcNow;
            s["cat"] = new JsonObject { ["at"] = at.ToString("O"), ["until"] = (at + Clicker.CatShown).ToString("O"), ["a"] = 2, ["b"] = 0 };
        });
        Assert.True(Nap(h, true).Ok);
        var cat = View(h).GetProperty("events").GetProperty("cat");
        Assert.True(cat.GetProperty("until").GetDateTimeOffset() < h.Clock.UtcNow);
        var pet = Act(h, "pet");
        Assert.False(pet.Ok);
        Assert.Contains("спить", pet.Message);
        Assert.Equal(5, Streak(h));
    }

    [Fact]
    public void A_sleeping_cat_does_not_touch_the_income_while_the_potter_is_away()
    {
        // Пасив, комора під полицею (віха Ночі) і «хвилина гри» рахуються за часом, а не за глеками, що впали.
        RoomHarness Rich()
        {
            var h = Wheel();
            Patch(h, s =>
            {
                foreach (var (key, level) in new[] { ("wheel", 100), ("apprentice", 100), ("kiln", 100), ("workshop", 300), ("basket", 10) })
                    s["upgrades"]![key] = level;
                s["marks"] = new JsonArray("kiln:100");            // «Піч на всю ніч»: комора ловить частку глеків
                s["fallStreak"] = 300;
            });
            return h;
        }
        var awake = Rich();
        var asleep = Rich();
        Assert.True(Act(awake, "look").Ok);
        Assert.True(Nap(asleep, true).Ok);
        Assert.Equal(((Clicker)awake.Room.Game).PlayMinute, ((Clicker)asleep.Room.Game).PlayMinute);

        var before = (Pots(awake), Pots(asleep));
        awake.Clock.Advance(TimeSpan.FromHours(8));
        asleep.Clock.Advance(TimeSpan.FromHours(8));
        Assert.True(Act(awake, "look").Ok);
        Assert.True(Act(asleep, "look").Ok);
        Assert.Equal(Pots(awake) - before.Item1, Pots(asleep) - before.Item2);
        Assert.Contains(View(asleep).GetProperty("away").GetProperty("notes").EnumerateArray(),
            n => n.GetString()!.StartsWith("🫙 Комора під полицею"));
        Assert.Equal(300, Streak(asleep));
    }

    // ---------- серія тріскає ----------

    [Fact]
    public void Each_broken_jug_in_a_row_chips_off_more_of_the_streak()
    {
        var h = AtTheWheel(1956, jugIn: 60);
        Assert.Equal("crack", Miss(h));
        Assert.Equal(196, Fall(h).GetProperty("lose").GetInt32());
        MissOne(h);
        Assert.Equal(1760, Streak(h));                        // −10 %
        MissOne(h);
        Assert.Equal(1408, Streak(h));                        // −20 %
        MissOne(h);
        Assert.Equal(986, Streak(h));                         // −30 %

        // Спіймав — лік «поспіль» з нуля: наступний розбитий знову −10 %.
        FallIn(h, 0);
        Assert.True(Act(h, "grab").Ok);
        Assert.Equal(987, Streak(h));
        MissOne(h);
        Assert.Equal(888, Streak(h));
    }

    [Fact]
    public void Ten_broken_in_a_row_take_the_whole_streak()
    {
        var h = AtTheWheel(100_000, jugIn: 60);
        for (var i = 0; i < 9; i++) MissOne(h);
        Assert.Equal(36, Streak(h));                          // 90 000, 72 000, 50 400 … 363, 36
        Assert.Equal("streak", Miss(h));                      // десятий забере решту — і вид це каже наперед
        MissOne(h);
        Assert.Equal(0, Streak(h));
    }

    [Fact]
    public void The_cracks_in_a_row_survive_a_reload()
    {
        var h = AtTheWheel(100, jugIn: 60);
        MissOne(h);
        Assert.Equal(90, Streak(h));
        Patch(h, _ => { });
        MissOne(h);
        Assert.Equal(72, Streak(h));                          // другий поспіль — −20 %, хоч між ними й перезапуск
    }

    [Fact]
    public void A_late_press_patches_the_crack()
    {
        var h = AtTheWheel(1956, jugIn: 60);
        MissOne(h);
        Assert.Equal(1760, Streak(h));
        FallIn(h, 0);
        h.Clock.AdvanceMs(1000);
        var pressed = h.Clock.UtcNow.ToUnixTimeMilliseconds();
        h.Clock.Advance(Past);
        Assert.True(Spin(h).Ok);
        Assert.Equal(1408, Streak(h));                        // записано розбитим — другий поспіль
        var r = Act(h, "grab", new { t = pressed });
        Assert.True(r.Ok, r.Message);
        Assert.Equal(1761, Streak(h));                        // серія як до нього, і +1 за спійманий
        MissOne(h);
        Assert.Equal(1761 - 176, Streak(h));                  // спійманий скинув лік: знову −10 %
    }
}
