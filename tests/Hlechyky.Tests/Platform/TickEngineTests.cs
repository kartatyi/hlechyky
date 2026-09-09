using Hlechyky.Games;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Логіка тика — це <c>Rooms.TickDue</c> + <c>Rooms.Tick</c> із фейковим годинником; сам BackgroundService
/// тонкий і лише крутить це коло, тому тут його не піднімаємо (TESTING.md §3).
/// </summary>
public class TickEngineTests
{
    static Rooms New(FakeClock clock) =>
        new(RoomHarness.NewRegistry(), clock, new GameEvents(), new FakeStakes(), new FakeStore(), RoomHarness.Empty())
        { SeedOverride = 3 };

    static string Playing(Rooms rooms, string game = "t-tick", IReadOnlyDictionary<string, string>? options = null)
    {
        var id = rooms.Create("Оля", game, options).Reply.RoomId!;
        rooms.Join(id, "Петро");
        return id;
    }

    static int Run(Rooms rooms, FakeClock clock, int steps, int stepMs = TickEngine.StepMs)
    {
        var ticks = 0;
        for (var i = 0; i < steps; i++)
        {
            clock.AdvanceMs(stepMs);
            foreach (var room in rooms.TickDue(clock.UtcNow))
            {
                rooms.Tick(room);
                ticks++;
            }
        }
        return ticks;
    }

    [Fact]
    public void Hundred_millisecond_room_ticks_exactly_ten_times_a_second()
    {
        var clock = new FakeClock();
        var rooms = New(clock);
        var id = Playing(rooms);

        var ticks = Run(rooms, clock, 50);   // 50 × 20 мс = рівно секунда
        Assert.Equal(10, ticks);
        Assert.Equal(10, ((TestTicker)rooms.Find(id)!.Game).Ticks);
    }

    [Fact]
    public void Missed_time_is_not_caught_up()
    {
        var clock = new FakeClock();
        var rooms = New(clock);
        var id = Playing(rooms);

        clock.Advance(TimeSpan.FromMinutes(5));            // сервер спав
        foreach (var room in rooms.TickDue(clock.UtcNow)) rooms.Tick(room);

        Assert.Equal(1, ((TestTicker)rooms.Find(id)!.Game).Ticks);
        Assert.Equal(clock.UtcNow.AddMilliseconds(100), rooms.Find(id)!.NextTickAt);
    }

    [Fact]
    public void Lobby_and_finished_rooms_do_not_tick()
    {
        var clock = new FakeClock();
        var rooms = New(clock);
        var lonely = rooms.Create("Оля", "t-tick", null).Reply.RoomId!;
        Assert.Empty(rooms.TickDue(clock.UtcNow.AddSeconds(10)));

        rooms.Join(lonely, "Петро");
        Run(rooms, clock, 10);
        rooms.Leave(lonely, "Петро");                      // техпоразка → Finished
        Assert.Equal(RoomStatus.Finished, rooms.Find(lonely)!.Status);

        var before = ((TestTicker)rooms.Find(lonely)!.Game).Ticks;
        Run(rooms, clock, 50);
        Assert.Equal(before, ((TestTicker)rooms.Find(lonely)!.Game).Ticks);
    }

    [Fact]
    public void Room_that_explodes_in_tick_finishes_in_a_draw_and_the_neighbours_keep_ticking()
    {
        var clock = new FakeClock();
        var rooms = New(clock);
        var bad = Playing(rooms, options: new Dictionary<string, string> { ["boom"] = "3" });
        var good = rooms.Create("Ганна", "t-tick", null).Reply.RoomId!;
        rooms.Join(good, "Богдан");

        Run(rooms, clock, 50);

        var broken = rooms.Find(bad)!;
        Assert.Equal(RoomStatus.Finished, broken.Status);
        Assert.True(broken.Result!.Draw);
        Assert.Contains("партія зламалась, вибачте", broken.Result.Text);
        Assert.Equal(10, ((TestTicker)rooms.Find(good)!.Game).Ticks);
    }

    [Fact]
    public void Deferred_work_of_a_broken_tick_runs_outside_the_room_lock()
    {
        var clock = new FakeClock();
        var stakes = new FakeStakes().Set("Оля", 10).Set("Петро", 10);
        var rooms = new Rooms(RoomHarness.NewRegistry(), clock, new GameEvents(), stakes, new FakeStore(),
            RoomHarness.Empty()) { SeedOverride = 3 };
        var id = Playing(rooms, options: new Dictionary<string, string> { ["boom"] = "2", ["stake"] = "5" });
        var room = rooms.Find(id)!;
        Assert.Equal(5, room.Stake);

        var underLock = false;
        stakes.OnGrant = () => underLock |= Monitor.IsEntered(room.Sync);
        Run(rooms, clock, 20);

        Assert.Equal(RoomStatus.Finished, room.Status);
        Assert.True(room.Result!.Draw);
        Assert.Equal(10, stakes.Balance("Оля"));            // ставку повернули
        Assert.False(underLock, "виплату зробили, не відпустивши замка кімнати");
    }

    [Fact]
    public void Tick_result_decides_what_goes_on_the_wire()
    {
        var clock = new FakeClock();
        var rooms = New(clock);
        var id = Playing(rooms);

        clock.AdvanceMs(100);
        var outbox = rooms.Tick(rooms.Find(id)!);
        var frame = Assert.Single(outbox.OfType<RoomFrame>());
        Assert.Equal(id, frame.RoomId);
        Assert.Empty(outbox.OfType<RoomViews>());          // TickResult.FrameOnly
    }

    [Fact]
    public void Housekeeping_and_grace_run_on_the_same_loop()
    {
        var clock = new FakeClock();
        var rooms = New(clock);
        var id = Playing(rooms);
        rooms.NoteOffline("Петро", clock.UtcNow);

        clock.Advance(TimeSpan.FromSeconds(21));
        rooms.DropIfGone(clock.UtcNow);
        Assert.Equal(RoomStatus.Finished, rooms.Find(id)!.Status);   // техпоразка того, хто не повернувся

        clock.Advance(TimeSpan.FromMinutes(16));
        rooms.Housekeeping(clock.UtcNow);
        Assert.Null(rooms.Find(id));
    }
}
