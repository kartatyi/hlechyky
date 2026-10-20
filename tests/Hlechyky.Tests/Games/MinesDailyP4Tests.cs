using System.Reflection;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hlechyky.Tests.Games;

/// <summary>Пакет 4 (20.10): вибух пише промах дня, «🏳 Здаюсь» закриває день, легке поле 9×9 зі своїм табло.</summary>
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

    [Fact]
    public void Easy_field_is_nine_by_nine_with_ten_mines_and_no_guessing()
    {
        var (h, _) = Open("mines-daily-easy");
        var v = h.View(0);
        Assert.Equal(9, v.GetProperty("w").GetInt32());
        Assert.Equal(10, v.GetProperty("mines").GetInt32());
        Assert.Equal("easy", v.GetProperty("size").GetString());
        Assert.NotEqual('#', v.GetProperty("cells").GetString()![MinesDailyEasy.Center]);
        Assert.True(MinesSolver.Solvable(MinesDailyEasy.Field("2026-09-10"), MinesDailyEasy.Center));
        Assert.True(h.Registry.Info("mines-daily-easy")!.Hidden);
        Assert.IsNotAssignableFrom<IDailyGame>(h.Room.Game);   // без своєї плитки в «☀ Сьогодні»
        Assert.Equal(MinesDailyEasy.Field("2026-09-10").MineCells(), BoardOf(h).MineCells());
    }

    [Fact]
    public void Easy_field_has_its_own_board_row_and_the_same_daily_reward()
    {
        var card = new DailyCard(null, new FakeClock());
        var (easy, _) = Open("mines-daily-easy", "Оля", card);
        Solve(easy);
        var score = Assert.Single(easy.Scores);
        Assert.StartsWith("daily:mines-daily-easy:2026-09-10:", score.Key);
        Assert.Equal("daily:mines-daily", Assert.Single(easy.Awards).Reason);
        Assert.Single(card.Get("mines-daily-easy")!.Rows);
        Assert.Empty(card.Get("mines-daily")!.Rows);

        var (big, _) = Open("mines-daily", "Петро", card);
        var boards = big.View(0).GetProperty("boards");
        Assert.Equal("big", big.View(0).GetProperty("size").GetString());
        Assert.Equal("Оля", boards.GetProperty("easy").GetProperty("rows")[0].GetProperty("n").GetString());
        Assert.Equal(0, boards.GetProperty("big").GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public void Daily_reward_is_paid_once_for_both_fields()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(new AwardEvent("mines-daily-easy", "r1", "Оля", 0, "daily:mines-daily"));
        var once = rig.Economy.Balance("Оля");
        Assert.True(once > 0);
        rig.Events.Raise(new AwardEvent("mines-daily", "r2", "Оля", 0, "daily:mines-daily"));
        Assert.Equal(once, rig.Economy.Balance("Оля"));
    }
}
