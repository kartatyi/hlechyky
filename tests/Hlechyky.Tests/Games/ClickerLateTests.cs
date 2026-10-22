using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Глек не б'ється через порожню хату й поганий зв'язок (ClickerLate.cs, записки 12.10): smaug вийшла в «Ефір», не
/// дочекавшись глека, і за хвилину серії не стало; у бібочки інтернет на мить зникає — натиск на глек не доходить.
/// </summary>
public class ClickerLateTests
{
    const string Nick = "smaug";

    static RoomHarness Wheel()
    {
        var h = new RoomHarness("clicker");
        h.Solo(Nick);
        return h;
    }

    static JsonElement Fall(RoomHarness h) => h.View(0).GetProperty("fall");
    static DateTimeOffset FallAt(RoomHarness h) => Fall(h).GetProperty("at").GetDateTimeOffset();
    static int Streak(RoomHarness h) => Fall(h).GetProperty("streak").GetInt32();
    static string Miss(RoomHarness h) => Fall(h).GetProperty("miss").GetString()!;
    static long Pots(RoomHarness h) => h.View(0).GetProperty("pots").GetInt64();

    static ActResult Act(RoomHarness h, string action, object? payload = null) => h.Act(0, action, payload);
    static ActResult Spin(RoomHarness h, int n) => Act(h, "spin", PotterHands.Human(n));
    static ActResult Sight(RoomHarness h, bool on) => Act(h, "sight", new { on });
    static long Now(RoomHarness h) => h.Clock.UtcNow.ToUnixTimeMilliseconds();
    static ActResult Grab(RoomHarness h, long t) => Act(h, "grab", new { t });

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    /// <summary>Глек з полиці злетить за <paramref name="seconds"/> с від «зараз».</summary>
    static void FallIn(RoomHarness h, double seconds) => Patch(h, s =>
    {
        var at = h.Clock.UtcNow.AddSeconds(seconds);
        s["fall"] = new JsonObject { ["at"] = at.ToString("O"), ["until"] = (at + Clicker.FallShown).ToString("O"), ["x"] = 40 };
    });

    static void GoldenIn(RoomHarness h, double seconds) => Patch(h, s =>
    {
        var at = h.Clock.UtcNow.AddSeconds(seconds);
        s["golden"] = new JsonObject
        {
            ["at"] = at.ToString("O"), ["until"] = (at + Clicker.GoldenShown).ToString("O"),
            ["kind"] = (int)Clicker.GoldenKind.Merchant, ["x"] = 10, ["y"] = 10,
        };
    });

    /// <summary>Гончар із серією клацає коло — він точно тут; глек злетить за <paramref name="jugIn"/> с.</summary>
    static RoomHarness AtTheWheel(int streak, double jugIn)
    {
        var h = Wheel();
        Patch(h, s => s["fallStreak"] = streak);
        FallIn(h, jugIn);
        Assert.True(Spin(h, 5).Ok);
        return h;
    }

    // ---------- пішов від кола ----------

    [Fact]
    public void Leaving_the_wheel_before_the_jug_keeps_the_streak_even_back_within_a_minute()
    {
        // smaug, 12.10: вийшла в «Ефір», поки глека не було, повернулась за хвилину — серії нема.
        var h = AtTheWheel(1120, jugIn: 20);
        h.Clock.AdvanceMs(1000);
        Assert.True(Sight(h, false).Ok);                      // коло зникло з очей
        h.Clock.AdvanceMs(60 * 1000);                          // глек злетів і впав без неї

        Assert.Equal(1120, Streak(h));                         // вид на поверненні (WatchRoom) серії не рве
        var back = Sight(h, true);
        Assert.True(back.Ok);
        Assert.Contains("серія 1120 ціла", back.Message);
        Assert.Equal(1120, Streak(h));
        Assert.Equal("crack", Miss(h));                        // фартуха нема й не було — нічого не витрачено
        Assert.True(FallAt(h) > h.Clock.UtcNow);               // новий — від повернення
    }

    [Fact]
    public void Going_away_is_quiet_and_does_not_seat_the_potter()
    {
        var h = AtTheWheel(3, jugIn: 60);
        var seen = Fall(h).GetProperty("seen").GetDateTimeOffset();
        h.Clock.AdvanceMs(5000);
        Assert.True(Sight(h, false).Ok);
        Assert.Equal(seen, Fall(h).GetProperty("seen").GetDateTimeOffset());
    }

    [Fact]
    public void Nobody_watching_the_room_means_the_potter_left_too()
    {
        // Вкладка не встигла сказати сама (закрили, обрив): каркас бачить, що за кімнатою ніхто не стежить.
        var h = AtTheWheel(40, jugIn: 20);
        h.Rooms.Watch(h.RoomId, "c1", Nick);
        h.Clock.AdvanceMs(1000);
        h.Rooms.Unwatch(h.RoomId, "c1");
        h.Clock.AdvanceMs(60 * 1000);
        h.Rooms.Watch(h.RoomId, "c2", Nick);

        Assert.Equal(40, Streak(h));
        // Вид каже клієнтові, що сервер вважає його відсутнім: вкладка, що бачить коло, скаже «я тут» знову.
        Assert.True(Fall(h).GetProperty("gone").GetBoolean());
        Assert.True(Sight(h, true).Ok);
        Assert.False(Fall(h).GetProperty("gone").GetBoolean());
    }

    [Fact]
    public void A_dropped_connection_counts_as_leaving()
    {
        var h = AtTheWheel(40, jugIn: 20);
        h.Rooms.Watch(h.RoomId, "c1", Nick);
        h.Clock.AdvanceMs(1000);
        h.Rooms.DropWatcher("c1");
        h.Clock.AdvanceMs(60 * 1000);
        Assert.True(Spin(h, 5).Ok);

        Assert.Equal(40, Streak(h));
    }

    [Fact]
    public void Another_tab_still_watching_is_not_leaving()
    {
        var h = AtTheWheel(40, jugIn: 20);
        h.Rooms.Watch(h.RoomId, "c1", Nick);
        h.Rooms.Watch(h.RoomId, "c2", Nick);
        h.Clock.AdvanceMs(1000);
        h.Rooms.Unwatch(h.RoomId, "c1");                       // друга вкладка й далі дивиться на коло
        h.Clock.AdvanceMs(60 * 1000);
        Assert.True(Spin(h, 5).Ok);

        Assert.Equal(36, Streak(h));                           // тріщина: −10 %
    }

    [Fact]
    public void A_jug_already_flying_when_the_potter_leaves_still_breaks()
    {
        // Бачила, як летить, — і пішла: це промах, як і раніше.
        var h = AtTheWheel(40, jugIn: 0);
        h.Clock.AdvanceMs(1500);
        Assert.True(Sight(h, false).Ok);
        h.Clock.AdvanceMs(60 * 1000);
        Assert.True(Sight(h, true).Ok);

        Assert.Equal(36, Streak(h));                           // тріщина: −10 %
    }

    [Fact]
    public void Back_at_the_wheel_the_next_jug_counts_again()
    {
        var h = AtTheWheel(40, jugIn: 90);
        h.Clock.AdvanceMs(1000);
        Assert.True(Sight(h, false).Ok);
        h.Clock.AdvanceMs(10 * 1000);
        Assert.True(Spin(h, 5).Ok);                            // повернулась і клацає — знову тут
        FallIn(h, 10);
        h.Clock.AdvanceMs(20 * 1000);
        Assert.True(Spin(h, 5).Ok);

        Assert.Equal(36, Streak(h));                           // тріщина: −10 %
    }

    [Fact]
    public void Coming_back_mid_flight_spares_that_jug()
    {
        // Глек злетів без неї, а вона повернулась, поки він летить: встигне — спіймає, ні — серія ціла.
        var h = AtTheWheel(40, jugIn: 20);
        h.Clock.AdvanceMs(1000);
        Assert.True(Sight(h, false).Ok);
        h.Clock.AdvanceMs(20 * 1000);                          // летить
        Assert.True(Sight(h, true).Ok);
        Assert.Equal("away", Miss(h));                         // клієнт намалює просто «трісь», без «серія обірвалась»
        h.Clock.AdvanceMs(10 * 1000);
        Assert.True(Spin(h, 5).Ok);

        Assert.Equal(40, Streak(h));
        Assert.Equal("crack", Miss(h));                        // а наступний — знову на її очах
    }

    [Fact]
    public void Coming_back_mid_flight_the_jug_can_still_be_caught()
    {
        var h = AtTheWheel(40, jugIn: 20);
        h.Clock.AdvanceMs(1000);
        Assert.True(Sight(h, false).Ok);
        h.Clock.AdvanceMs(20 * 1000);
        Assert.True(Sight(h, true).Ok);
        Assert.True(Act(h, "grab").Ok);

        Assert.Equal(41, Streak(h));
    }

    [Fact]
    public void Leaving_survives_a_reload()
    {
        var h = AtTheWheel(40, jugIn: 20);
        h.Clock.AdvanceMs(1000);
        Assert.True(Sight(h, false).Ok);
        Patch(h, _ => { });                                    // кімнату вивантажили й підняли з бази
        h.Clock.AdvanceMs(60 * 1000);
        Assert.True(Spin(h, 5).Ok);

        Assert.Equal(40, Streak(h));
    }

    // ---------- натиск без зв'язку ----------

    [Fact]
    public void A_press_stuck_in_the_network_still_catches_the_jug()
    {
        // Бібочка: натиснула вчасно, а натиск доліз через 10 с — після пачки кліків, що вже записала глек розбитим.
        var h = AtTheWheel(5, jugIn: 0);
        h.Clock.AdvanceMs(1000);
        var pressed = Now(h);
        h.Clock.AdvanceMs(10 * 1000);
        Assert.True(Spin(h, 5).Ok);
        Assert.Equal(4, Streak(h));                            // тріщина: −10 %, щонайменше один
        var next = FallAt(h);
        var before = Pots(h);

        var r = Grab(h, pressed);
        Assert.True(r.Ok, r.Message);
        Assert.Contains("зв'язок наздогнав", r.Message);
        Assert.Equal(6, Streak(h));
        Assert.True(Pots(h) > before);
        Assert.Equal(next, FallAt(h));                         // наступний — за тим самим розкладом
    }

    [Fact]
    public void A_late_press_after_reconnect_beats_the_view_that_came_first()
    {
        // Зв'язок зник на хвилину: першим після реконекту прийшов вид (WatchRoom), натиск — за ним.
        var h = AtTheWheel(5, jugIn: 0);
        h.Clock.AdvanceMs(1000);
        var pressed = Now(h);
        h.Clock.AdvanceMs(60 * 1000);
        h.View(0);                                             // вид після реконекту записав глек розбитим
        Assert.Equal(4, Streak(h));                            // тріщина: −10 %, щонайменше один

        Assert.True(Grab(h, pressed).Ok);
        Assert.Equal(6, Streak(h));
    }

    [Fact]
    public void A_late_press_gives_back_the_apron()
    {
        var h = Wheel();
        Patch(h, s => { s["pots"] = 1e7; s["total"] = 1e7; });
        Assert.True(Act(h, "tool", new { key = "apron" }).Ok);
        Patch(h, s => s["fallStreak"] = 5);
        FallIn(h, 0);
        h.Clock.AdvanceMs(1000);
        var pressed = Now(h);
        h.Clock.AdvanceMs(10 * 1000);
        Assert.True(Spin(h, 5).Ok);
        Assert.Equal("crack", Miss(h));                        // фартух уберіг і розрядився

        Assert.True(Grab(h, pressed).Ok);
        Assert.Equal(6, Streak(h));
        Assert.Equal("apron", Miss(h));                        // залатаний — фартух знову цілий
    }

    [Fact]
    public void A_press_older_than_two_minutes_does_not_count()
    {
        var h = AtTheWheel(5, jugIn: 0);
        h.Clock.AdvanceMs(1000);
        var pressed = Now(h);
        h.Clock.Advance(Clicker.LateGrab + TimeSpan.FromSeconds(1));

        Assert.False(Grab(h, pressed).Ok);
        Assert.Equal(4, Streak(h));                            // тріщина: −10 %, щонайменше один
    }

    [Fact]
    public void A_late_press_outside_the_flight_does_not_count()
    {
        var h = AtTheWheel(5, jugIn: 0);
        h.Clock.AdvanceMs(10 * 1000);
        var tooLate = Now(h);                                  // глек давно на долівці
        Assert.True(Spin(h, 5).Ok);

        Assert.False(Grab(h, tooLate).Ok);
        Assert.Equal(4, Streak(h));                            // тріщина: −10 %, щонайменше один
    }

    [Fact]
    public void A_press_from_the_future_is_not_believed()
    {
        var h = AtTheWheel(5, jugIn: 0);
        h.Clock.AdvanceMs(1000);
        var pressed = Now(h);
        h.Clock.AdvanceMs(10 * 1000);
        Assert.True(Spin(h, 5).Ok);

        // Натиск «через п'ять секунд» — такого сервер ще не бачив, і серії він не латає.
        Assert.False(Grab(h, Now(h) + 5000).Ok);
        Assert.Equal(4, Streak(h));                            // тріщина: −10 %, щонайменше один
        Assert.True(Grab(h, pressed).Ok);
    }

    [Fact]
    public void The_same_press_sent_twice_pays_once()
    {
        // Відповідь загубилась у дорозі — клієнт повторив той самий натиск.
        var h = AtTheWheel(5, jugIn: 0);
        h.Clock.AdvanceMs(1000);
        var pressed = Now(h);
        Assert.True(Grab(h, pressed).Ok);
        h.Clock.AdvanceMs(5000);
        var pots = Pots(h);

        var again = Grab(h, pressed);
        Assert.True(again.Ok);
        Assert.Equal("", again.Message ?? "");
        Assert.Equal(6, Streak(h));
        Assert.Equal(pots, Pots(h));
    }

    [Fact]
    public void An_old_retry_does_not_catch_the_next_jug()
    {
        var h = AtTheWheel(5, jugIn: 0);
        h.Clock.AdvanceMs(1000);
        var pressed = Now(h);
        Assert.True(Grab(h, pressed).Ok);
        var next = FallAt(h);
        h.Clock.Advance(next - h.Clock.UtcNow + TimeSpan.FromMilliseconds(500));   // наступний уже летить

        Assert.True(Grab(h, pressed).Ok);                      // повтор старого — тихо, нічого
        Assert.Equal(6, Streak(h));
        Assert.Equal(next, FallAt(h));                         // наступний досі на сцені
        Assert.True(Grab(h, Now(h)).Ok);                       // а цей ловиться своїм натиском
        Assert.Equal(7, Streak(h));
    }

    [Fact]
    public void Without_the_press_time_a_late_grab_breaks_as_before()
    {
        var h = AtTheWheel(5, jugIn: 0);
        h.Clock.AdvanceMs(10 * 1000);
        Assert.True(Spin(h, 5).Ok);

        Assert.False(Act(h, "grab").Ok);
        Assert.Equal(4, Streak(h));                            // тріщина: −10 %, щонайменше один
    }

    [Fact]
    public void A_late_press_catches_the_golden_jug_too()
    {
        var h = Wheel();
        GoldenIn(h, 0);
        Assert.True(Spin(h, 5).Ok);
        h.Clock.AdvanceMs(2000);
        var pressed = Now(h);
        h.Clock.Advance(Clicker.GoldenShown + TimeSpan.FromSeconds(10));
        Assert.True(Spin(h, 5).Ok);                            // утік — розклад уже новий
        var before = Pots(h);

        var r = Act(h, "catch", new { t = pressed });
        Assert.True(r.Ok, r.Message);
        Assert.Contains("Щедрий купець", r.Message);
        Assert.Contains("зв'язок наздогнав", r.Message);
        Assert.True(Pots(h) > before);
        var paid = Pots(h);
        Assert.True(Act(h, "catch", new { t = pressed }).Ok);  // повтор — без другого купця
        Assert.Equal(paid, Pots(h));
    }
}
