using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Хутори» не губить партію, коли людина відпала (телефон заснув): стіл стоїть на паузі <see cref="Khutory.HoldEmpty"/>,
/// людина сідає на свій хутір зі свіжим строком. І таймери не кривдять людину: розстановка — повний час, після козака
/// чи Толоки — щонайменше 20 с, а проспаний main Глек доробляє (будує), а не просто завершує.
/// </summary>
public class KhutoryPauseTests
{
    static readonly JsonElement NoPayload = JsonDocument.Parse("{}").RootElement;

    static RoomHarness Table(string[] people, string bots, string timer = "60", int seed = 5)
    {
        var h = new RoomHarness("khutory", new { map = "starter", timer, bots }, seed: seed);
        foreach (var p in people) h.Join(p);
        Assert.True(h.Start().Ok);
        return h;
    }

    static KhutoryCore CoreOf(RoomHarness h) => ((Khutory)h.Room.Game).Core;

    /// <summary>Телефон заснув: останнє з'єднання закрилось, Grace (20 с) минув — каркас звільняє місце.</summary>
    static void Gone(RoomHarness h, string nick)
    {
        h.Rooms.NoteOffline(nick, h.Clock.UtcNow);
        h.Clock.Advance(TimeSpan.FromSeconds(21));
        h.Rooms.DropIfGone(h.Clock.UtcNow);
    }

    static double DeadlineLeft(RoomHarness h, JsonElement v) =>
        (v.GetProperty("deadline").GetDateTimeOffset() - h.Clock.UtcNow).TotalSeconds;

    [Fact]
    public void Last_person_dropping_pauses_the_game_and_coming_back_gives_a_fresh_deadline()
    {
        var h = Table(["Оля"], bots: "3");
        var core = CoreOf(h);
        var seq = core.Seq;
        var said = h.Outbox.OfType<TableSaid>().Count();
        Gone(h, "Оля");

        // Партія не кінчається: стіл стоїть, місце Олі «away», Глек у балачку нічого не каже.
        Assert.NotNull(h.Rooms.Find(h.RoomId));
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Empty(h.Finished);
        Assert.False(core.Over);
        Assert.Null(h.Room.Seats[0]);
        Assert.True(h.View(null).GetProperty("players")[0].GetProperty("away").GetBoolean());
        Assert.Equal(said, h.Outbox.OfType<TableSaid>().Count());

        // Пауза: штовхнути Глеків чи строк нікому (Act — лише тим, хто сидить), минулі хвилини нічого не міняють.
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.False(h.Rooms.Act(h.RoomId, "Оля", BoardBots.Nudge, NoPayload).Reply.Ok);
        Assert.False(h.Rooms.Act(h.RoomId, "Оля", "timeout", NoPayload).Reply.Ok);
        h.Rooms.Housekeeping(h.Clock.UtcNow);
        Assert.NotNull(h.Rooms.Find(h.RoomId));
        Assert.Equal(seq, core.Seq);

        // Повернулась — на свій хутір, Глек віддає кермо, строк свіжий (не минулий за паузу).
        Assert.True(h.Join("Оля").Ok);
        Assert.Equal("Оля", h.Room.Seats[0]);
        var v = h.View(0);
        Assert.False(v.GetProperty("players")[0].GetProperty("away").GetBoolean());
        Assert.Equal(0, v.GetProperty("me").GetProperty("seat").GetInt32());
        Assert.True(DeadlineLeft(h, v) > 30, $"строк після повернення {DeadlineLeft(h, v)} с");
        Assert.False(h.Act(0, "timeout").Ok);
        Assert.Contains(v.GetProperty("log").EnumerateArray(), l => l.GetString()!.Contains("Оля повертається на свій хутір"));
    }

    [Fact]
    public void Paused_table_is_cleaned_up_after_ten_minutes()
    {
        var h = Table(["Оля"], bots: "2");
        Gone(h, "Оля");
        h.Clock.Advance(TimeSpan.FromMinutes(9));
        h.Rooms.Housekeeping(h.Clock.UtcNow);
        Assert.NotNull(h.Rooms.Find(h.RoomId));
        h.Clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        h.Rooms.Housekeeping(h.Clock.UtcNow);
        Assert.Null(h.Rooms.Find(h.RoomId));
    }

    [Fact]
    public void Last_person_standing_up_by_the_button_takes_the_table_away()
    {
        var h = Table(["Оля"], bots: "2");
        h.Leave("Оля");
        Assert.Null(h.Rooms.Find(h.RoomId));
        Assert.Empty(h.Finished);
    }

    [Fact]
    public void All_people_dropped_is_the_same_pause_and_each_comes_back_to_own_seat()
    {
        var h = Table(["Оля", "Петро"], bots: "0");
        var core = CoreOf(h);
        Gone(h, "Петро");
        Assert.True(h.View(0).GetProperty("players")[1].GetProperty("away").GetBoolean());
        Gone(h, "Оля");
        Assert.NotNull(h.Rooms.Find(h.RoomId));
        Assert.False(core.Over);
        Assert.Empty(h.Finished);

        Assert.True(h.Join("Петро").Ok);
        Assert.Equal("Петро", h.Room.Seats[1]);
        var v = h.View(1);
        Assert.False(v.GetProperty("players")[1].GetProperty("away").GetBoolean());
        Assert.True(v.GetProperty("players")[0].GetProperty("away").GetBoolean());   // за Олю поки Глек
        Assert.True(h.Join("Оля").Ok);
        Assert.Equal("Оля", h.Room.Seats[0]);
    }

    [Fact]
    public void Setup_gets_the_full_turn_timer()
    {
        var h = Table(["Оля", "Петро"], bots: "0", timer: "90");
        var v = h.View(0);
        Assert.Equal("setup", v.GetProperty("phase").GetString());
        Assert.Equal(90_000, v.GetProperty("timerMs").GetInt32());
        Assert.InRange(DeadlineLeft(h, v), 89, 90);
        var turn = v.GetProperty("turn").GetInt32();
        h.Clock.Advance(60);
        Assert.False(h.Act(turn, "timeout").Ok);   // на 60-й секунді ще ставить сам
        Assert.True(h.Act(turn, "town", new { v = h.View(turn).GetProperty("me").GetProperty("spots").GetProperty("town")[0].GetInt32() }).Ok);
        // Шлях після хати — теж повний час.
        Assert.InRange(DeadlineLeft(h, h.View(turn)), 89, 90);
    }

    [Fact]
    public void Back_in_main_after_toloka_there_are_at_least_twenty_seconds()
    {
        var h = KhutoryBotTests.Table(bots: "1");
        var c = KhutoryBotTests.Main(h, 0);
        KhutoryBotTests.Rearm(h);
        h.Clock.Advance(175);
        c.Devs[0].Add(new KhDev { K = KhutoryCore.Toloka });
        Assert.True(h.Act(0, "play", new { k = "road" }).Ok);
        for (var i = 0; i < 2 && h.View(0).GetProperty("phase").GetString() == "road2"; i++)
            Assert.True(h.Act(0, "road", new { e = h.View(0).GetProperty("me").GetProperty("spots").GetProperty("road")[0].GetInt32() }).Ok);
        var v = h.View(0);
        Assert.Equal("main", v.GetProperty("phase").GetString());
        Assert.InRange(DeadlineLeft(h, v), 19, 21);
    }

    [Fact]
    public void Slept_main_turn_is_finished_by_glek_who_builds_but_does_not_touch_the_cards()
    {
        var h = KhutoryBotTests.Table(bots: "1");
        var c = KhutoryBotTests.Main(h, 0);
        // Дорога до місця під хату (після розстановки його ще нема).
        for (var i = 0; c.TownSpots(0).Count == 0; i++)
        {
            Assert.True(i < 4, "не прокласти шлях до місця під хату");
            KhutoryBotTests.Give(c, 0, wood: 1, clay: 1);
            Assert.Null(c.PlaceRoad(0, c.RoadSpots(0)[0]));
        }
        var towns = c.TownsOf(0);
        // На хату + рівно на грамоту: Глек будує хату, але грамоту за людину не купує й козака не грає.
        KhutoryBotTests.Give(c, 0, wood: 1, clay: 1, wool: 2, grain: 2, ore: 1);
        c.Devs[0].Add(new KhDev { K = KhutoryCore.Knight });
        var deck = c.Deck.Count;
        KhutoryBotTests.Rearm(h);
        h.Clock.Advance(181);
        Assert.True(h.Act(0, "timeout").Ok);
        Assert.Equal(towns + 1, c.TownsOf(0));
        Assert.NotEqual(0, c.Turn);
        Assert.Equal(deck, c.Deck.Count);
        Assert.Single(c.Devs[0]);
        Assert.Equal(0, c.Knights[0]);
        Assert.False(h.View(0).GetProperty("players")[0].GetProperty("away").GetBoolean());   // один раз — ще не away
    }

    [Fact]
    public void Slept_toloka_is_finished_with_free_roads()
    {
        var h = KhutoryBotTests.Table(bots: "1");
        var c = KhutoryBotTests.Main(h, 0);
        KhutoryBotTests.Rearm(h);
        var roads = c.RoadsOf(0);
        c.Devs[0].Add(new KhDev { K = KhutoryCore.Toloka });
        Assert.True(h.Act(0, "play", new { k = "road" }).Ok);
        Assert.Equal("road2", h.View(0).GetProperty("phase").GetString());
        h.Clock.Advance(41);
        Assert.True(h.Act(0, "timeout").Ok);
        Assert.Equal(roads + 2, c.RoadsOf(0));
        Assert.NotEqual(0, c.Turn);
    }
}
