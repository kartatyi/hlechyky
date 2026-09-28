using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Місця ботів у шапці кімнати (прохід №3, фініш): гра називає бота на місці без людини (<see cref="Game.SeatBot"/>),
/// і картка столу пише «Глек 🤖», а не «вільно»; у лобі (ботів ще нема) — нічого.
/// </summary>
public class SeatBotTests
{
    [Fact]
    public void Domino_bots_are_named_in_the_room_summary_while_playing()
    {
        var h = new RoomHarness("domino", new { bots = "1" });
        h.Join("Оля");
        Assert.All(h.Room.Summary().Seats, s => Assert.Null(s.Bot));   // у лобі ботів ще нема
        h.Start();
        var seats = h.Room.Summary().Seats;
        Assert.Null(seats[0].Bot);
        Assert.Equal("Оля", seats[0].Nick);
        Assert.Contains(seats, s => s.Nick is null && s.Bot is { } b && b.Contains("🤖"));
        Assert.Equal(1, seats.Count(s => s.Bot is not null));
    }

    [Fact]
    public void Bot_goes_on_the_wire_only_when_there_is_one()
    {
        var json = JsonSerializer.Serialize(new SeatSlot(0, "Оля"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("bot", json);
        json = JsonSerializer.Serialize(new SeatSlot(1, null, "Глек 🤖"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"bot\":", json);
    }

    [Fact]
    public void Games_without_bots_say_nothing()
    {
        var h = new RoomHarness("ttt");
        h.Join("Оля");
        h.Join("Петро");
        h.Start();
        Assert.All(h.Room.Summary().Seats, s => Assert.Null(s.Bot));
    }

    [Fact]
    public void Create_does_not_promise_a_start_the_game_would_refuse()
    {
        var rooms = new Rooms(RoomHarness.NewRegistry(), new FakeClock(), new GameEvents(), new FakeStakes(), new FakeStore(), RoomHarness.Empty());
        var lonely = rooms.Create("Оля", "durak", null).Reply;
        Assert.True(lonely.Ok);
        Assert.DoesNotContain("Можна почати вже", lonely.Message);
        Assert.Contains("Глек", lonely.Message);
        var withBot = rooms.Create("Петро", "durak", new Dictionary<string, string> { ["bots"] = "1" }).Reply;
        Assert.Contains("Можна почати вже", withBot.Message);
    }
}
