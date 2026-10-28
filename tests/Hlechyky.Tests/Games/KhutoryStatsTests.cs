using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Хутори»: статистика для HUD — останні кидки (<c>rolls</c>), лічильник чисел під час партії (<c>diceCount</c>)
/// і «скільки разів у тебе крали» (<c>over.stats.seats[].robbed</c>, звання «Улюбленець Кармелюка»).
/// </summary>
public class KhutoryStatsTests
{
    static readonly KhutoryGeo G = KhutoryGeo.I;

    static KhutoryCore Core(int n = 4, int seed = 1)
    {
        var c = new KhutoryCore(KhutoryBoard.Starter(), Enumerable.Range(0, n), new Random(seed)) { Friendly = false };
        c.Begin(0);
        return c;
    }

    static void RollPhase(KhutoryCore c, int turn = 0)
    {
        c.Phase = KhPhase.Roll;
        c.Turn = turn;
        c.TurnIdx = Array.IndexOf(c.Order, turn);
        c.Rolled = false;
        c.Dice = null;
    }

    [Fact]
    public void Rolls_keep_the_last_eight_sums_newest_last()
    {
        var c = Core();
        int[][] dice = [[1, 1], [2, 1], [2, 2], [3, 2], [3, 3], [4, 4], [5, 4], [5, 5], [6, 5], [6, 6], [1, 2]];
        foreach (var d in dice)
        {
            RollPhase(c);
            Assert.Null(c.Roll(0, d[0], d[1]));
        }
        Assert.Equal(KhutoryCore.RollsKeep, c.Rolls.Count);
        Assert.Equal([5, 6, 8, 9, 10, 11, 12, 3], c.Rolls);
        // Лічильник чисел рахує всі кидки, не лише останні 8.
        Assert.Equal(11, c.DiceCount.Sum());
        Assert.Equal(2, c.DiceCount[3 - 2]);
    }

    [Fact]
    public void Robbed_counts_the_victim_and_stolen_the_thief()
    {
        var c = Core();
        c.VOwner[G.HexV[4][0]] = 1;
        c.VOwner[G.HexV[4][3]] = 2;
        c.RecountRoads();
        int[] give = [2, 2, 0, 0, 0];
        for (var r = 0; r < 5; r++) { c.Bank[r] -= give[r]; c.Hand[1][r] += give[r]; c.Bank[r] -= give[r]; c.Hand[2][r] += give[r]; }
        RollPhase(c);
        Assert.Null(c.Roll(0, 3, 4));
        Assert.Equal(KhPhase.Robber, c.Phase);
        Assert.Null(c.MoveRobber(0, 4));
        Assert.Equal(KhPhase.Steal, c.Phase);
        Assert.Null(c.Steal(0, 2));
        Assert.Equal(1, c.Stolen[0]);
        Assert.Equal(0, c.Robbed[0]);
        Assert.Equal(1, c.Robbed[2]);
        Assert.Equal(0, c.Robbed[1]);
        Assert.Equal(0, c.Stolen[2]);
    }

    [Fact]
    public void View_shows_rolls_and_dice_count_while_playing_and_robbed_at_the_end()
    {
        var h = new RoomHarness("khutory", new { map = "starter", timer = "60", bots = "3" }, seed: 11);
        h.Join("Оля");
        Assert.True(h.Start().Ok);
        var start = h.View(0);
        Assert.Equal(0, start.GetProperty("rolls").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, start.GetProperty("diceCount").ValueKind);

        var sawRolls = false;
        for (var steps = 0; h.Finished.Count == 0; steps++)
        {
            Assert.True(steps < 6000, "партія не кінчається");
            var v = h.View(0);
            if (v.GetProperty("phase").GetString() == "main")
            {
                var rolls = v.GetProperty("rolls");
                Assert.InRange(rolls.GetArrayLength(), 1, KhutoryCore.RollsKeep);
                var d = v.GetProperty("dice");
                Assert.Equal(d[0].GetInt32() + d[1].GetInt32(), rolls[rolls.GetArrayLength() - 1].GetInt32());
                var count = v.GetProperty("diceCount");
                Assert.Equal(11, count.GetArrayLength());
                Assert.True(count.EnumerateArray().Sum(x => x.GetInt32()) >= rolls.GetArrayLength());
                sawRolls = true;
            }
            if (v.GetProperty("botIn").ValueKind == JsonValueKind.Number)
            {
                h.Clock.AdvanceMs(Math.Max(BoardBots.ThinkMs, v.GetProperty("botIn").GetInt32()));
                Assert.True(h.Act(0, "bot").Ok);
            }
            else
            {
                h.Clock.Advance(181);
                Assert.True(h.Act(0, "timeout").Ok);
            }
        }
        Assert.True(sawRolls);
        var c = ((Khutory)h.Room.Game).Core;
        var seats = h.View(0).GetProperty("over").GetProperty("stats").GetProperty("seats");
        var robbedSum = 0;
        foreach (var x in seats.EnumerateArray())
        {
            var s = x.GetProperty("seat").GetInt32();
            Assert.Equal(c.Robbed[s], x.GetProperty("robbed").GetInt32());
            robbedSum += c.Robbed[s];
        }
        // Кожна крадіжка — один злодій і одна жертва.
        Assert.Equal(seats.EnumerateArray().Sum(x => x.GetProperty("stolen").GetInt32()), robbedSum);
    }
}
