using Hlechyky.Games;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>«👁 Підглянути» в чужу соло-гру (RoomsPeek.cs): коли пускає, що бачить гравець і чого не чує сама гра.</summary>
public class PeekTests
{
    static TestSolo Game(RoomHarness h) => (TestSolo)h.Room.Game;

    static IReadOnlyList<string>? PeekOf(RoomHarness h)
    {
        lock (h.Room.Sync) return h.Room.Summary().Peek;
    }

    /// <summary>Щоденна для тестів — сапер дня; розгадали сьогодні лише ті, кого назвали.</summary>
    sealed class FakeDaily(params string[] solved) : IDailyPeek
    {
        public bool? Solved(string gameId, string nick) => gameId == "mines-daily" ? solved.Contains(nick) : null;
    }

    [Fact]
    public void You_can_peek_only_while_the_player_is_at_the_game()
    {
        var h = new RoomHarness("t-solo");
        h.Solo("Оля");
        Assert.Equal("Оля уже не грає", h.Rooms.PeekRefusal(h.RoomId, "Петро"));
        Assert.Empty(h.Rooms.Watch(h.RoomId, "c-петро", "Петро"));          // мовчки нічого, як і було з чужою приватною
        Assert.False(h.Room.Watchers.ContainsKey("c-петро"));

        h.Rooms.Focus("c-оля", "Оля", h.RoomId);
        Assert.Equal(h.RoomId, Assert.Single(h.Rooms.SoloNow()).Room);      // лобі знає, куди підглядати
        Assert.Null(h.Rooms.PeekRefusal(h.RoomId, "Петро"));
        Assert.Single(h.Rooms.Watch(h.RoomId, "c-петро", "Петро").OfType<RoomViews>());
        h.Rooms.Watch(h.RoomId, "c-петро-2", "петро");                      // друга вкладка — той самий підглядач
        Assert.Equal(["Петро"], PeekOf(h));

        // дивитись — не грати
        Assert.False(h.Rooms.Act(h.RoomId, "Петро", "add", default).Reply.Ok);
        Assert.Equal(0L, h.View(0).GetProperty("value").GetInt64());
    }

    [Fact]
    public void Only_a_solo_game_of_someone_else_is_for_peeking()
    {
        var h = new RoomHarness("t-solo");
        h.Solo("Оля");
        Assert.Null(h.Rooms.PeekRefusal(h.RoomId, "оля"));                  // своя — завжди можна
        Assert.Equal("Ця гра вже скінчилась", h.Rooms.PeekRefusal("00000000", "Петро"));

        var table = new RoomHarness("ttt");
        table.Join("Оля");
        Assert.NotNull(table.Rooms.PeekRefusal(table.RoomId, "Петро"));     // стіл видно й так — «👁» там свій
    }

    [Fact]
    public void A_peeker_does_not_keep_the_game_watched_for_its_player()
    {
        var h = new RoomHarness("t-solo");
        h.Solo("Оля");
        h.Rooms.Watch(h.RoomId, "c-оля", "Оля");
        h.Rooms.Focus("c-оля", "Оля", h.RoomId);
        h.Rooms.Watch(h.RoomId, "c-петро", "Петро");

        // підглядач пішов — гравцеві новий вид (у шапці його вже нема), а гра нічого не чує
        Assert.Single(h.Rooms.Unwatch(h.RoomId, "c-петро").OfType<RoomViews>());
        Assert.Null(PeekOf(h));
        Assert.Equal(0, Game(h).UnwatchedCount);

        // пішла вкладка гравця, хоч підглядач і лишився, — це вже «ніхто не дивиться»
        h.Rooms.Watch(h.RoomId, "c-петро", "Петро");
        h.Rooms.DropWatcher("c-оля");
        Assert.Equal(1, Game(h).UnwatchedCount);
        // а останній підглядач того самого вдруге не каже
        Assert.Single(h.Rooms.DropWatcher("c-петро").OfType<RoomViews>());
        Assert.Equal(1, Game(h).UnwatchedCount);
    }

    [Fact]
    public void Housekeeping_does_not_keep_a_solo_room_for_a_peeker()
    {
        var h = new RoomHarness("t-solo");
        h.Solo("Оля");
        h.Rooms.Focus("c-оля", "Оля", h.RoomId);
        h.Rooms.Watch(h.RoomId, "c-петро", "Петро");
        h.Rooms.Focus("c-оля", "Оля", null);                               // пішла в лобі, Петро так і дивиться

        h.Clock.Advance(TimeSpan.FromMinutes(31));
        h.Rooms.Housekeeping(h.Clock.UtcNow);
        Assert.Null(h.Rooms.Find(h.RoomId));
    }

    [Fact]
    public void A_daily_puzzle_can_be_peeked_only_after_you_solved_yours()
    {
        var h = new RoomHarness("mines-daily", services: RoomHarness.WithService<IDailyPeek>(new FakeDaily("Ганна")));
        h.Solo("Оля");
        h.Rooms.Focus("c-оля", "Оля", h.RoomId);

        Assert.Equal("Це щоденна — спершу розгадай свою, а тоді підглядай", h.Rooms.PeekRefusal(h.RoomId, "Петро"));
        Assert.Empty(h.Rooms.Watch(h.RoomId, "c-петро", "Петро"));
        Assert.Null(h.Rooms.PeekRefusal(h.RoomId, "Ганна"));
        Assert.Single(h.Rooms.Watch(h.RoomId, "c-ганна", "Ганна").OfType<RoomViews>());
    }
}
