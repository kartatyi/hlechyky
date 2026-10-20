using System.Reflection;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hlechyky.Tests.Games;

/// <summary>Пакет 4 (20.10): вибух пише промах дня, «🏳 Здаюсь» закриває день.</summary>
public class MinesDailyP4Tests
{
    static (RoomHarness H, DailyCard Card) Open(string game = "mines-daily", string nick = "Оля", DailyCard? card = null, FakeClock? clock = null)
    {
        card ??= new DailyCard(null, clock ?? new FakeClock());
        var sp = new ServiceCollection().AddSingleton(card).AddSingleton(new MinesGhosts(null, new FakeClock())).BuildServiceProvider();
        var h = new RoomHarness(game, seed: 1, services: sp);
        if (clock is not null) h.Clock.UtcNow = clock.UtcNow;
        Assert.True(h.Solo(nick).Ok);
        return (h, card);
    }

    internal static MinesBoard BoardOf(RoomHarness h)
    {
        for (var t = h.Room.Game.GetType(); t is not null; t = t.BaseType)
            if (t.GetField("_board", BindingFlags.NonPublic | BindingFlags.Instance) is { } f) return (MinesBoard)f.GetValue(h.Room.Game)!;
        throw new InvalidOperationException("нема _board");
    }

    static int AMine(RoomHarness h)
    {
        var b = BoardOf(h);
        return Enumerable.Range(0, b.Cells).First(b.IsMine);
    }

    internal static void Solve(RoomHarness h, int stepMs = 700)
    {
        var board = BoardOf(h);
        for (var c = 0; c < board.Cells && board.Left > 0; c++)
        {
            if (board.IsMine(c) || board.IsOpen(c)) continue;
            h.Clock.AdvanceMs(stepMs);
            Assert.True(h.Act(0, "open", new { cell = c }).Ok);
        }
    }

    [Fact]
    public void Boom_writes_a_miss_and_shows_it_on_the_card()
    {
        var (h, card) = Open();
        Assert.True(h.Act(0, "open", new { cell = AMine(h) }).Ok);
        var miss = Assert.Single(h.Scores);
        Assert.True(miss.Missed);
        Assert.Equal(1, miss.Attempts);
        Assert.StartsWith("daily:mines-daily:", miss.Key);
        var m = h.View(0).GetProperty("board").GetProperty("miss")[0];
        Assert.Equal(("Оля", 1), (m.GetProperty("n").GetString(), m.GetProperty("a").GetInt32()));
        Assert.Equal(RoomStatus.Playing, h.Room.Status);

        // ще спроба — і розв'язок перекриває промах на табло
        Assert.True(h.Act(0, "restart").Ok);
        Solve(h);
        Assert.Empty(card.Get("mines-daily")!.Missed!);
        Assert.Equal(2, card.Get("mines-daily")!.Rows[0].Attempts);
    }

    [Fact]
    public void Resign_ends_the_day_reveals_the_field_and_writes_a_miss()
    {
        var (h, card) = Open();
        Assert.True(h.Act(0, "open", new { cell = AMine(h) }).Ok);   // спершу бабах — здатись можна й після нього
        h.Scores.Clear();
        var r = h.Act(0, "resign");
        Assert.True(r.Ok);
        var miss = Assert.Single(h.Scores);
        Assert.True(miss.Missed);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Empty(h.Outbox.OfType<Journal>());
        Assert.Empty(h.Awards);

        var v = h.View(0);
        Assert.True(v.GetProperty("resigned").GetBoolean());
        Assert.Equal("resign", v.GetProperty("result").GetProperty("reason").GetString());
        Assert.Equal(40, v.GetProperty("cells").GetString()!.Count(ch => ch == '*'));
        Assert.Equal("Оля", card.Get("mines-daily")!.Missed![0].Nick);

        // стан пережив перезапуск: день так і лишився закінченим
        var saved = h.Room.Game.Save()!;
        var (again, _) = Open();
        again.Room.Game.Load(saved);
        Assert.True(again.View(0).GetProperty("resigned").GetBoolean());
        Assert.False(again.Act(0, "restart").Ok);
        Assert.False(again.Act(0, "open", new { cell = 0 }).Ok);
    }

    [Fact]
    public void Resign_after_solving_is_refused()
    {
        var (h, _) = Open();
        Solve(h);
        Assert.False(h.Act(0, "resign").Ok);
    }
}
