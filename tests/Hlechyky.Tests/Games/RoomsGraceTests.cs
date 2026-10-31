using Hlechyky.Games;
using Hlechyky.Tests.Support;
using Xunit;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Пакет B аудиту столів: довша Grace у покрокових посеред партії, «відпав до» в шапці столу і «Ще раз» / «Почати»,
/// що не стартують з тим, хто зараз не на зв'язку.
/// </summary>
public class RoomsGraceTests
{
    static RoomHarness Duel()
    {
        var h = new RoomHarness("t-duel");
        h.Join("Оля");
        h.Join("Петро");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        return h;
    }

    static void Drop(RoomHarness h, TimeSpan after)
    {
        h.Clock.Advance(after);
        h.Outbox.AddRange(h.Rooms.DropIfGone(h.Clock.UtcNow));
    }

    [Fact]
    public void Turn_based_game_waits_two_minutes_for_a_sleeping_phone()
    {
        var h = Duel();
        var since = h.Clock.UtcNow;
        var away = h.Rooms.NoteOffline("Петро", since);
        Assert.Single(away.OfType<RoomViews>());   // шапка в Олі дізнається одразу

        var seat = h.Room.Summary().Seats[1];
        Assert.Equal(since + Rooms.TurnGrace, seat.Away);
        Assert.Null(h.Room.Summary().Seats[0].Away);
        // на дроті: «away» лише в того, хто відпав
        var wire = Views.Json(h.Room.Summary()).GetProperty("seats");
        Assert.Equal(since + Rooms.TurnGrace, wire[1].GetProperty("away").GetDateTimeOffset());
        Assert.False(wire[0].TryGetProperty("away", out _));

        Drop(h, TimeSpan.FromSeconds(60));
        Assert.Equal("Петро", h.Room.Seats[1]);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);

        Drop(h, TimeSpan.FromSeconds(61));
        Assert.Null(h.Room.Seats[1]);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);   // типовий OnLeave — техпоразка, як і була
        Assert.Equal(new[] { 0 }, h.Room.Result!.Winners);
    }

    [Fact]
    public void Coming_back_clears_the_away_mark_and_keeps_the_seat()
    {
        var h = Duel();
        h.Rooms.NoteOffline("Петро", h.Clock.UtcNow);
        h.Clock.Advance(TimeSpan.FromSeconds(90));
        Assert.Single(h.Rooms.NoteOnline("петро").OfType<RoomViews>());
        Assert.Null(h.Room.Summary().Seats[1].Away);
        Assert.Empty(h.Rooms.NoteOnline("Петро"));   // уже онлайн — нічого не шле

        Drop(h, TimeSpan.FromSeconds(200));
        Assert.Equal("Петро", h.Room.Seats[1]);
    }

    [Fact]
    public void Realtime_and_lobby_keep_the_old_twenty_seconds()
    {
        var rt = new RoomHarness("t-tick");
        rt.Join("Оля");
        rt.Join("Петро");
        Assert.Equal(RoomStatus.Playing, rt.Room.Status);
        rt.Rooms.NoteOffline("Петро", rt.Clock.UtcNow);
        Assert.Equal(rt.Clock.UtcNow + Rooms.Grace, rt.Room.Summary().Seats[1].Away);
        Drop(rt, TimeSpan.FromSeconds(21));
        Assert.Null(rt.Room.Seats[1]);

        var lobby = new RoomHarness("t-party");
        lobby.Join("Оля");
        lobby.Join("Петро");
        lobby.Rooms.NoteOffline("Петро", lobby.Clock.UtcNow);
        Drop(lobby, TimeSpan.FromSeconds(21));
        Assert.Null(lobby.Room.Seats[1]);
        Assert.Equal(RoomStatus.Lobby, lobby.Room.Status);
    }

    [Fact]
    public void A_nick_is_kept_in_the_countdown_until_its_turn_game_lets_it_go()
    {
        var clock = new FakeClock();
        var rooms = new Rooms(RoomHarness.NewRegistry(), clock, new GameEvents(), new FakeStakes(), new FakeStore(), RoomHarness.Empty());
        var duel = rooms.Create("Оля", "t-duel", null).Reply.RoomId!;
        Assert.True(rooms.Join(duel, "Петро").Reply.Ok);
        rooms.NoteOffline("Петро", clock.UtcNow);

        clock.Advance(TimeSpan.FromSeconds(30));
        rooms.DropIfGone(clock.UtcNow);
        Assert.Equal("Петро", rooms.Find(duel)!.Seats[1]);   // ще чекаємо — нік лишається у відліку

        clock.Advance(TimeSpan.FromSeconds(91));
        rooms.DropIfGone(clock.UtcNow);
        Assert.Null(rooms.Find(duel)!.Seats[1]);
        Assert.True(rooms.Create("Петро", "t-party", null).Reply.Ok);   // ніде не сидить — і відлік забуто
    }

    [Fact]
    public void Rematch_with_someone_offline_asks_to_wait_and_anyway_frees_the_seat()
    {
        var h = new RoomHarness("t-party");
        foreach (var n in new[] { "Оля", "Петро", "Іван" }) h.Join(n);
        Assert.True(h.Start().Ok);
        Assert.True(h.Act(0, "win").Ok);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        h.Rooms.NoteOffline("Іван", h.Clock.UtcNow);
        h.Ready("Петро");

        var wait = h.Rooms.Rematch(h.RoomId, "Оля", force: false).Reply;
        Assert.False(wait.Ok);
        Assert.Equal(new[] { "Іван" }, wait.NotReady);
        Assert.StartsWith("Не на зв'язку: Іван", wait.Message);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);

        var go = h.Rooms.Rematch(h.RoomId, "Оля", force: true, dropAway: true);
        Assert.True(go.Reply.Ok, go.Reply.Message);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.DoesNotContain("Іван", h.Room.Seats);
        Assert.Equal(2, h.Room.Occupied);
    }

    [Fact]
    public void Start_anyway_without_enough_people_after_freeing_says_too_few()
    {
        var h = new RoomHarness("t-party");
        h.Join("Оля");
        h.Join("Петро");
        h.Rooms.NoteOffline("Петро", h.Clock.UtcNow);

        var wait = h.Rooms.StartByHost(h.RoomId, "Оля", force: false).Reply;
        Assert.Equal(new[] { "Петро" }, wait.NotReady);

        var go = h.Rooms.StartByHost(h.RoomId, "Оля", force: true, dropAway: true).Reply;
        Assert.False(go.Ok);
        Assert.StartsWith("Замало гравців", go.Message);
        Assert.Null(h.Room.Seats[1]);
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);
    }

    [Fact]
    public void Tournament_style_force_starts_as_before()
    {
        var h = new RoomHarness("t-party");
        h.Join("Оля");
        h.Join("Петро");
        h.Rooms.NoteOffline("Петро", h.Clock.UtcNow);
        Assert.True(h.Rooms.StartByHost(h.RoomId, "Оля", force: true).Reply.Ok);
        Assert.Equal("Петро", h.Room.Seats[1]);
    }
}
