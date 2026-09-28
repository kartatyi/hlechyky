using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Прохід №3: погони (№211), «Глек підсідає» (№228), переводний (№210).</summary>
public class DurakSweep3Tests
{
    static int Card(string text) => DurakCards.Parse(text)!.Value;

    [Fact]
    public void Transfer_passes_the_table_to_the_next_player()
    {
        var core = new DurakCore(3) { AllowTransfer = true };
        core.Arrange("♠", [["6♥", "K♣"], ["6♦", "7♣"], ["8♥", "9♥", "10♥"]], deck: ["A♠"], attacker: 0);
        Assert.Null(core.Attack(0, Card("6♥")));
        Assert.True(core.CanTransfer(1));
        Assert.Equal("Переводять картою того самого номіналу", core.Transfer(1, Card("7♣")));
        Assert.Null(core.Transfer(1, Card("6♦")));
        Assert.Equal(2, core.Defender);
        Assert.Equal(1, core.Attacker);
        Assert.Equal(2, core.Table.Count);
        Assert.Equal(DurakPhase.Defend, core.Phase);
    }

    [Fact]
    public void No_transfer_in_podkydnoy_or_after_beating_or_onto_a_short_hand()
    {
        var plain = new DurakCore(3);
        plain.Arrange("♠", [["6♥"], ["6♦", "7♥"], ["8♥", "9♥"]], deck: ["A♠"]);
        plain.Attack(0, Card("6♥"));
        Assert.False(plain.CanTransfer(1));
        Assert.NotNull(plain.Transfer(1, Card("6♦")));

        var beat = new DurakCore(3) { AllowTransfer = true };
        beat.Arrange("♠", [["6♥", "6♣"], ["6♦", "7♥"], ["8♥", "9♥"]], deck: ["A♠"]);
        beat.Attack(0, Card("6♥"));
        beat.Defend(1, Card("6♥"), Card("7♥"));
        beat.Attack(0, Card("6♣"));
        Assert.Equal("Уже почав відбиватись — переводити пізно", beat.Transfer(1, Card("6♦")));

        var shortHand = new DurakCore(3) { AllowTransfer = true };
        shortHand.Arrange("♠", [["6♥"], ["6♦", "7♥"], ["8♥"]], deck: ["A♠"]);
        shortHand.Attack(0, Card("6♥"));
        Assert.False(shortHand.CanTransfer(1));   // у третього одна карта, а на столі буде дві
    }

    static void Human(RoomHarness h, int seat)
    {
        var v = h.View(seat);
        var hand = v.GetProperty("hand").EnumerateArray().Select(x => x.GetString()!).ToList();
        if (v.GetProperty("phase").GetString() == "defend" && v.GetProperty("defender").GetInt32() == seat)
        {
            foreach (var p in v.GetProperty("table").EnumerateArray())
            {
                if (p.GetProperty("defend").ValueKind == JsonValueKind.String) continue;
                foreach (var c in hand)
                    if (h.Act(seat, "defend", new { attack = p.GetProperty("attack").GetString(), card = c }).Ok) return;
                Assert.True(h.Act(seat, "take").Ok);
                return;
            }
        }
        if (!v.GetProperty("canAdd").GetBoolean()) return;
        foreach (var c in hand) if (h.Act(seat, "attack", new { card = c }).Ok) return;
        Assert.True(h.Act(seat, "done").Ok);
    }

    [Fact]
    public void Bots_play_a_whole_match_and_the_fool_gets_pogony()
    {
        foreach (var mode in new[] { "podkydnoy", "perevodnoy" })
        {
            var h = new RoomHarness("durak", new { bots = "3", mode }, seed: 9);
            h.Join("Оля");
            Assert.True(h.Start().Ok);
            Assert.Equal(mode == "perevodnoy", h.View(0).GetProperty("transfer").GetBoolean());
            for (var step = 0; step < 5000 && h.Room.Status == RoomStatus.Playing; step++)
            {
                var v = h.View(0);
                if (v.GetProperty("botIn").ValueKind == JsonValueKind.Number)
                {
                    h.Clock.AdvanceMs(BoardBots.ThinkMs);
                    if (h.Act(0, BoardBots.Nudge).Ok) continue;
                }
                Human(h, 0);
            }
            Assert.Equal(RoomStatus.Finished, h.Room.Status);
            var end = h.View(0);
            var result = end.GetProperty("result");
            if (result.GetProperty("fool").ValueKind == JsonValueKind.Number)
            {
                var fool = result.GetProperty("fool").GetInt32();
                Assert.Equal(end.GetProperty("names")[fool].GetString(), end.GetProperty("pogony").GetString());
            }
        }
    }

    [Fact]
    public void Pogony_stay_on_the_fool_through_the_next_match()
    {
        var h = new RoomHarness("durak");
        h.Join("Оля"); h.Join("Петро");
        h.Start();
        h.Leave("Петро");   // на двох хто встав — той і дурень
        Assert.Equal("Петро", h.View(0).GetProperty("pogony").GetString());
        h.Join("Петро");
        h.Rematch("Оля");
        h.Start();
        Assert.Equal("Петро", h.View(0).GetProperty("pogony").GetString());
    }

    [Fact]
    public void Alone_needs_a_bot()
    {
        var h = new RoomHarness("durak");
        h.Join("Оля");
        Assert.False(h.Start().Ok);
    }
}
