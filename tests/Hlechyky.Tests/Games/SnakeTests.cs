using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Змійка-дуель: те саме поле й ті самі правила, що були до переїзду на кімнати, плюс перевірки ядра
/// <see cref="SnakeCore"/>, на якому далі виростуть мотоцикли й кооп (TESTING.md §4).
/// </summary>
public class SnakeTests
{
    static RoomHarness Table()
    {
        var h = new RoomHarness("snake", seed: 42);
        h.Join("Оля");
        h.Join("Петро");
        return h;
    }

    /// <summary>Пропустити зворотний відлік «готуйсь», щоб дійти до руху.</summary>
    static void Ready(RoomHarness h) => h.Tick(SnakeCore.StartTicks);

    /// <summary>Зелена йде в нижню стіну — так партія закінчується швидко і з переможцем.</summary>
    static void SendGreenIntoTheWall(RoomHarness h) => h.Input(1, "turn", new { dir = 1 });

    // ---------- кімната ----------

    [Fact]
    public void Countdown_runs_before_anyone_moves()
    {
        var h = Table();
        Assert.Equal(SnakeCore.StartTicks, h.View(0).GetProperty("startIn").GetInt32());

        var before = h.View(0).GetProperty("a").ToString();
        h.Tick(5);
        Assert.Equal(SnakeCore.StartTicks - 5, h.View(0).GetProperty("startIn").GetInt32());
        Assert.Equal(before, h.View(0).GetProperty("a").ToString());
    }

    [Fact]
    public void Every_tick_sends_a_frame_and_the_end_sends_the_views_too()
    {
        var h = Table();
        h.Tick(1);
        Assert.Single(h.Outbox.OfType<RoomFrame>());
        Assert.Equal(2, h.Outbox.OfType<RoomViews>().Count());   // по одному на кожен вхід; тик шле лише кадр

        Ready(h);
        SendGreenIntoTheWall(h);
        h.Tick(10);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Contains(h.Outbox.OfType<Journal>(), j => j.Text.StartsWith("Змійка:"));
        Assert.NotEmpty(h.Outbox.OfType<RoomViews>().Skip(2));
    }

    [Fact]
    public void Wall_ends_the_round_and_the_journal_carries_the_series_score()
    {
        var h = Table();
        Ready(h);
        SendGreenIntoTheWall(h);
        h.Tick(10);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("Змійка: Оля жовта 1:0 Петро зелена", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Equal("x", h.View(0).GetProperty("winner").GetString());
    }

    [Fact]
    public void Turns_arrive_through_input_and_only_from_the_right_seat()
    {
        var h = Table();
        Ready(h);
        h.Input(0, "turn", new { dir = 1 });                   // жовта вниз
        h.Tick(1);

        var a = h.View(0).GetProperty("a")[0].GetInt32();
        Assert.Equal(SnakeCore.W, a - SnakeCore.Cell(3, SnakeCore.H / 2 - 3));   // рівно на рядок нижче

        h.Rooms.Input(h.RoomId, "Чужий", "turn", Views.Payload(new { dir = 3 }));
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void Rematch_keeps_the_series_and_a_new_pair_resets_it()
    {
        var h = Table();
        Ready(h);
        SendGreenIntoTheWall(h);
        h.Tick(10);
        Assert.Equal(1, h.View(0).GetProperty("winsA").GetInt32());

        h.Rematch("Оля");
        // Місця обернулись — разом з ними поїхав і рахунок: Оля тепер сидить другою.
        Assert.Equal("Оля", h.Room.Seats[1]);
        Assert.Equal(0, h.View(0).GetProperty("winsA").GetInt32());
        Assert.Equal(1, h.View(0).GetProperty("winsB").GetInt32());
        Assert.Equal(SnakeCore.StartTicks, h.View(0).GetProperty("startIn").GetInt32());

        h.Leave("Оля");
        Assert.True(h.Rooms.Join(h.RoomId, "Ганна").Reply.Ok);   // дограний стіл відкривається наново
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(0, h.View(0).GetProperty("winsA").GetInt32());
        Assert.Equal(0, h.View(0).GetProperty("winsB").GetInt32());
    }

    [Fact]
    public void Frame_carries_exactly_what_the_client_draws()
    {
        var h = Table();
        h.Tick(1);
        var frame = Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);

        foreach (var name in new[] { "a", "b", "apple", "winsA", "winsB", "startIn", "winner" })
            Assert.True(Views.Has(frame, name), name);
        Assert.Equal(JsonValueKind.Null, frame.GetProperty("winner").ValueKind);
        Assert.Equal(3, frame.GetProperty("a").GetArrayLength());
    }

    [Fact]
    public void Snake_is_in_the_catalog_as_a_live_game()
    {
        var game = Assert.Single(new Registry().Catalog, g => g.Id == "snake");
        Assert.Equal("live", game.Group);
        Assert.Equal(SnakeCore.TickMs, game.TickMs);
        Assert.True(game.Rated);
    }

    // ---------- ядро ----------

    [Fact]
    public void Core_starts_with_two_snakes_facing_each_other()
    {
        var core = new SnakeCore(new Random(1));
        core.Reset();

        Assert.Equal(3, core.A.Count);
        Assert.Equal(3, core.B.Count);
        Assert.Equal(0, core.DirA);
        Assert.Equal(2, core.DirB);
        Assert.Equal(SnakeCore.Cell(SnakeCore.W / 2, SnakeCore.H / 2), core.Apple);
    }

    [Fact]
    public void Core_refuses_a_u_turn_and_queues_at_most_two_turns()
    {
        var core = new SnakeCore(new Random(1));
        core.Reset();
        core.Turn(0, 2);                 // розворот проти руху — ігноруємо
        core.Step();
        Assert.Equal(0, core.DirA);

        core.Turn(0, 1);
        core.Turn(0, 0);
        core.Turn(0, 1);                 // третій уже не влізе
        core.Step();
        Assert.Equal(1, core.DirA);
        core.Step();
        Assert.Equal(0, core.DirA);
        core.Step();
        Assert.Equal(0, core.DirA);
    }

    [Fact]
    public void Core_kills_the_one_who_hits_the_wall()
    {
        var core = new SnakeCore(new Random(1));
        core.Reset();
        core.Turn(1, 1);                 // зелена вниз, до нижньої стіни
        var dead = false;
        for (var i = 0; i < 30 && !dead; i++) dead = core.Step().DeadB;
        Assert.True(dead);
    }

    [Fact]
    public void Core_grows_on_an_apple_and_puts_a_new_one_on_a_free_cell()
    {
        var core = new SnakeCore(new Random(7));
        core.Reset();
        core.A.Clear();
        core.A.AddRange([SnakeCore.Cell(5, 5), SnakeCore.Cell(4, 5), SnakeCore.Cell(3, 5)]);
        core.DirA = 0;
        core.Apple = SnakeCore.Cell(6, 5);
        core.Step();

        Assert.Equal(4, core.A.Count);
        Assert.NotEqual(SnakeCore.Cell(6, 5), core.Apple);
        Assert.DoesNotContain(core.Apple, core.A);
    }

    [Fact]
    public void Core_head_on_collision_kills_both()
    {
        var core = new SnakeCore(new Random(1));
        core.Reset();
        core.A.Clear();
        core.A.AddRange([SnakeCore.Cell(5, 5), SnakeCore.Cell(4, 5)]);
        core.B.Clear();
        core.B.AddRange([SnakeCore.Cell(7, 5), SnakeCore.Cell(8, 5)]);
        core.DirA = 0;
        core.DirB = 2;
        var (deadA, deadB) = core.Step();

        Assert.True(deadA);
        Assert.True(deadB);
    }

    [Fact]
    public void Core_without_a_shrinking_tail_leaves_a_wall_behind_it()
    {
        var core = new SnakeCore(new Random(1), tailShrinks: false, apples: false);
        core.Reset();
        Assert.Equal(-1, core.Apple);

        core.Step();
        core.Step();
        Assert.Equal(5, core.A.Count);   // три на старті плюс два кроки
        Assert.Equal(-1, core.Apple);
    }

    [Fact]
    public void Core_is_deterministic_for_the_same_seed()
    {
        static string Play(int seed)
        {
            var core = new SnakeCore(new Random(seed));
            core.Reset();
            for (var i = 0; i < 20; i++)
            {
                if (i == 5) core.Turn(0, 1);
                if (i == 9) core.Turn(1, 3);
                core.Step();
            }
            return string.Join(",", core.A) + "|" + string.Join(",", core.B) + "|" + core.Apple;
        }
        Assert.Equal(Play(3), Play(3));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void A_thousand_ticks_of_one_room_are_instant()
    {
        var core = new SnakeCore(new Random(11));
        core.Reset();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            if (i % 17 == 0) core.Turn(0, i / 17 % 4);
            if (i % 19 == 0) core.Turn(1, i / 19 % 4);
            core.Step();
        }
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"1000 кроків зайняли {sw.Elapsed}");
    }
}
