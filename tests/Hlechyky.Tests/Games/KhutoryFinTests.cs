using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Хутори», фінальні правки після плейтесту: вид до старту, журнал обміну між людьми, пільга задрімалому.
/// </summary>
public class KhutoryFinTests
{
    [Fact]
    public void Before_start_the_view_has_no_phase_turn_or_me()
    {
        var h = new RoomHarness("khutory", new { bots = "1" });
        h.Join("Оля");
        h.Join("Петро");
        var v = h.View(0);
        Assert.Equal(0, v.GetProperty("players").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("phase").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("turn").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("me").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("setup").ValueKind);

        Assert.True(h.Start().Ok);
        var go = h.View(0);
        Assert.Equal("setup", go.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Number, go.GetProperty("turn").ValueKind);
        Assert.Equal(JsonValueKind.Object, go.GetProperty("me").ValueKind);
    }

    [Theory]
    [InlineData(1, 0, "1 дерево")]
    [InlineData(2, 0, "2 дерева")]
    [InlineData(5, 0, "5 дерев")]
    [InlineData(11, 0, "11 дерев")]
    [InlineData(21, 0, "21 дерево")]
    [InlineData(22, 1, "22 глини")]
    [InlineData(12, 3, "12 зерен")]
    [InlineData(1, 4, "1 залізо")]
    [InlineData(3, 2, "3 вовни")]
    public void Resource_counts_are_declined(int n, int r, string want) =>
        Assert.Equal(want, KhutoryCore.ResCount(n, r));

    [Fact]
    public void People_trade_is_logged_with_what_for_what()
    {
        string[] names = ["владік", "Оля", "Петро"];
        var c = new KhutoryCore(KhutoryBoard.Starter(), Enumerable.Range(0, 3), new Random(1)) { NameOf = s => names[s] };
        c.Begin(0);
        c.Phase = KhPhase.Main;
        c.Turn = 0;
        c.TurnIdx = Array.IndexOf(c.Order, 0);
        c.Rolled = true;
        c.Dice = [2, 3];
        c.Hand[0][0] = 3; c.Hand[0][1] = 2;
        c.Hand[1][4] = 1;
        Assert.Null(c.Offer(0, [1, 2, 0, 0, 0], [0, 0, 0, 0, 1], null));
        var id = c.Offers[^1].Id;
        Assert.Null(c.Answer(1, id, true));
        Assert.Null(c.Deal(0, id, 1));
        Assert.Equal("владік і Оля міняються: 1 дерево + 2 глини ⇄ 1 залізо", c.Log[^1]);

        // Банк — так само з відмінюванням.
        c.Hand[0][0] = 4;
        Assert.Null(c.BankTrade(0, 0, 3, 1));
        Assert.Contains("4 дерева ⇄ 1 зерно", c.Log[^1]);
    }

    [Fact]
    public void Away_human_gets_five_seconds_to_roll_before_the_bot()
    {
        var h = new RoomHarness("khutory", new { map = "starter", timer = "60", bots = "0" }, seed: 5);
        h.Join("Оля");
        h.Join("Петро");
        Assert.True(h.Start().Ok);
        var checkedGrace = false;
        for (var step = 0; step < 400 && !checkedGrace; step++)
        {
            var v = h.View(0);
            var turn = v.GetProperty("turn").GetInt32();
            var away = v.GetProperty("players").EnumerateArray().First(p => p.GetProperty("seat").GetInt32() == turn).GetProperty("away").GetBoolean();
            if (v.GetProperty("botIn").ValueKind == JsonValueKind.Number)
            {
                var botIn = v.GetProperty("botIn").GetInt32();
                if (away && v.GetProperty("phase").GetString() == "roll")
                {
                    Assert.True(botIn >= Khutory.AwayGraceMs, $"botIn {botIn}");
                    h.Clock.AdvanceMs(Khutory.AwayGraceMs - 100);
                    Assert.False(h.Act(0, "bot").Ok);          // ще чекаємо людину
                    h.Clock.AdvanceMs(200);
                    Assert.True(h.Act(0, "bot").Ok);           // а тепер кидає Глек
                    Assert.NotEqual("roll", h.View(0).GetProperty("phase").GetString());
                    checkedGrace = true;
                    break;
                }
                h.Clock.AdvanceMs(botIn);
                Assert.True(h.Act(0, "bot").Ok);
            }
            else
            {
                h.Clock.Advance(61);
                Assert.True(h.Act(0, "timeout").Ok);
            }
        }
        Assert.True(checkedGrace, "задрімала людина так і не дійшла до кидка");
    }
}
