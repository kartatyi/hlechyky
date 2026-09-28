using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Рахунок вечора за столом» (прохід №3, п. 225): стіл пам'ятає перемоги за всі «Ану ще раз», хоч місця
/// й міняються; ігри для цього нічого не роблять — рахує каркас у Finish.
/// </summary>
public class EveningTests
{
    static void XWins(RoomHarness h)
    {
        // хрестик (місце 0) збирає верхній рядок
        foreach (var (seat, cell) in new[] { (0, 0), (1, 3), (0, 1), (1, 4), (0, 2) })
            Assert.True(h.Act(seat, "move", new { cell }).Ok);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
    }

    [Fact]
    public void Wins_follow_the_nick_across_rematches_not_the_seat()
    {
        var h = new RoomHarness("ttt");
        h.Join("Оля");
        h.Join("Петро");
        Assert.Null(h.Room.Summary().Evening);

        XWins(h);                       // Оля — хрестик
        h.Rematch();
        XWins(h);                       // місця помінялись: тепер хрестик — Петро
        h.Rematch();
        XWins(h);                       // знову Оля

        var ev = h.Room.Summary().Evening!;
        Assert.Equal(3, ev.Games);
        Assert.Equal("Оля", ev.Rows[0].Nick);
        Assert.Equal(2, ev.Rows[0].Wins);
        Assert.Equal(1, ev.Rows[1].Wins);
        Assert.Equal(3, ev.Rows[1].Games);
        Assert.Null(ev.Rows[0].Points);   // хрестики очок не рахують
    }

    [Fact]
    public void Evening_goes_over_the_wire_in_camel_case()
    {
        var h = new RoomHarness("ttt");
        h.Join("Оля");
        h.Join("Петро");
        XWins(h);
        var json = JsonSerializer.Serialize(h.Room.Summary(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        var ev = doc.RootElement.GetProperty("evening");
        Assert.Equal(1, ev.GetProperty("games").GetInt32());
        Assert.Equal("Оля", ev.GetProperty("rows")[0].GetProperty("nick").GetString());
        Assert.Equal(1, ev.GetProperty("rows")[0].GetProperty("wins").GetInt32());
    }

    [Fact]
    public void Points_add_up_when_the_game_gives_scores()
    {
        var h = new RoomHarness("ttt");
        h.Join("Оля");
        h.Join("Петро");
        h.Room.TallyEvening([1], new Dictionary<int, long> { [0] = 7, [1] = 12 });
        h.Room.TallyEvening([0], new Dictionary<int, long> { [0] = 20, [1] = 3 });

        var ev = h.Room.Summary().Evening!;
        Assert.Equal(2, ev.Games);
        // рівно по перемозі — вище той, у кого більше очок
        Assert.Equal("Оля", ev.Rows[0].Nick);
        Assert.Equal(27, ev.Rows[0].Points);
        Assert.Equal(15, ev.Rows[1].Points);
    }

    [Fact]
    public void Solo_tables_keep_no_evening()
    {
        var h = new RoomHarness("bricks-sprint");
        h.Solo("Оля");
        h.Room.TallyEvening([0], null);
        Assert.Null(h.Room.Summary().Evening);
    }
}

/// <summary>Реакції-емодзі за столом (прохід №3, п. 231): загальна дія каркаса, гра про них не знає.</summary>
public class TableReactTests
{
    [Fact]
    public void Seated_player_reacts_and_the_quota_stops_spam()
    {
        var h = new RoomHarness("chess");
        h.Join("Оля");
        h.Join("Петро");
        var before = Views.Text(h.Room.Game.View(null));

        var (out1, err1) = h.Rooms.TableReact(h.RoomId, null, "Оля", 2);
        Assert.Null(err1);
        var rx = Assert.IsType<TableReact>(Assert.Single(out1));
        Assert.Equal((0, 2, "Оля"), (rx.Seat!.Value, rx.E, rx.Nick));

        Assert.NotNull(h.Rooms.TableReact(h.RoomId, null, "Оля", 1).Error);      // одразу вдруге — зарано
        Assert.Null(h.Rooms.TableReact(h.RoomId, null, "Петро", 1).Error);       // квота своя в кожного
        h.Clock.UtcNow = h.Clock.UtcNow.AddMilliseconds(Rooms.ReactGapMs);
        Assert.Null(h.Rooms.TableReact(h.RoomId, null, "Оля", 0).Error);

        Assert.Equal(before, Views.Text(h.Room.Game.View(null)));                // у грі нічого не змінилось
    }

    [Fact]
    public void Strangers_and_unknown_emoji_are_refused()
    {
        var h = new RoomHarness("chess");
        h.Join("Оля");
        Assert.NotNull(h.Rooms.TableReact(h.RoomId, null, "Сусід", 0).Error);   // не сидить і не дивиться
        Assert.NotNull(h.Rooms.TableReact(h.RoomId, null, "Оля", Rooms.ReactKinds).Error);
        Assert.NotNull(h.Rooms.TableReact(h.RoomId, null, "Оля", -1).Error);
        Assert.NotNull(h.Rooms.TableReact("нема", null, "Оля", 0).Error);
    }
}

/// <summary>Rooms.SafeFrame (прохід №3, п. 246): null із перекритого Frame() — «нема чого слати», а не «шли весь вид».</summary>
public class SafeFrameTests
{
    [Fact]
    public void Null_frame_from_a_game_is_skipped_not_replaced_by_the_view()
    {
        var h = new RoomHarness("t-frameless");
        h.Join("Оля");
        h.Start();
        h.Outbox.Clear();
        h.Tick(3);
        Assert.DoesNotContain(h.Outbox, o => o is RoomFrame);

        ((TestFrameless)h.Room.Game).Empty = false;
        h.Tick();
        Assert.Single(h.Outbox.OfType<RoomFrame>());
    }
}
