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

    // ---------- №232: пари 2×2 ----------

    static DurakCore Pairs() => new(4) { Team = [0, 1, 0, 1] };

    [Fact]
    public void Partner_of_the_defender_does_not_throw_in()
    {
        var core = Pairs();
        core.Arrange("♠", [["6♥", "7♠"], ["8♥", "9♥"], ["6♦"], ["6♣"]], deck: ["A♠"], attacker: 0);
        Assert.Null(core.Attack(0, Card("6♥")));
        Assert.Null(core.Defend(1, Card("6♥"), Card("8♥")));
        Assert.True(core.CanAddFor(2));
        Assert.False(core.CanAddFor(3));   // 3 — напарник захисника 1
        Assert.Equal("Своєму напарникові не підкидають", core.Attack(3, Card("6♣")));

        var solo = new DurakCore(4);
        solo.Arrange("♠", [["6♥", "7♠"], ["8♥", "9♥"], ["6♦"], ["6♣"]], deck: ["A♠"], attacker: 0);
        solo.Attack(0, Card("6♥"));
        solo.Defend(1, Card("6♥"), Card("8♥"));
        Assert.True(solo.CanAddFor(3));
    }

    [Fact]
    public void Match_ends_when_one_pair_is_out_and_the_fool_is_the_one_with_more_cards()
    {
        var core = Pairs();
        core.Arrange("♠", [["6♥"], ["7♥", "8♣", "K♦"], ["6♣"], ["9♦", "10♦"]], deck: [], attacker: 0);
        Assert.Null(core.Attack(0, Card("6♥")));
        Assert.Null(core.Defend(1, Card("6♥"), Card("7♥")));
        Assert.Null(core.Attack(2, Card("6♣")));
        Assert.Null(core.Defend(1, Card("6♣"), Card("8♣")));
        if (core.Over is null) Assert.Null(core.Done(0));

        Assert.NotNull(core.Over);
        Assert.Equal(3, core.Over!.Fool);
    }

    static RoomHarness PairsTable(params string[] nicks) => PairsTable("podkydnoy", 5, nicks);

    static RoomHarness PairsTable(string mode, int seed, params string[] nicks)
    {
        var h = new RoomHarness("durak", new { teams = "1", bots = "3", mode }, seed: seed);
        foreach (var n in nicks) h.Join(n);
        return h;
    }

    [Fact]
    public void Pairs_need_exactly_four_at_the_table()
    {
        var h = new RoomHarness("durak", new { teams = "1" });
        h.Join("Оля"); h.Join("Петро"); h.Join("Іра");
        Assert.False(h.Start().Ok);
        h.Join("Тарас");
        Assert.True(h.Start().Ok);
        Assert.Equal([0, 1, 0, 1, -1, -1], h.View(0).GetProperty("teams").EnumerateArray().Select(e => e.GetInt32()));

        var five = new RoomHarness("durak", new { teams = "1" });
        foreach (var n in new[] { "а", "б", "в", "г", "ґ" }) five.Join(n);
        Assert.False(five.Start().Ok);

        // Глеки добирають рівно до чотирьох, скільки б їх не просили.
        var bots = PairsTable("Оля", "Петро");
        Assert.True(bots.Start().Ok);
        Assert.Equal(2, bots.View(0).GetProperty("bots").EnumerateArray().Count(b => b.ValueKind == JsonValueKind.String));

        var plain = new RoomHarness("durak");
        plain.Join("Оля"); plain.Join("Петро");
        plain.Start();
        Assert.Equal(JsonValueKind.Null, plain.View(0).GetProperty("teams").ValueKind);
    }

    [Theory]
    [InlineData("podkydnoy")]
    [InlineData("perevodnoy")]
    public void Pairs_play_a_whole_match_with_bots_and_the_whole_pair_of_the_fool_loses(string mode)
    {
        for (var seed = 1; seed <= 15; seed++) PairsMatch(PairsTable(mode, seed, "Оля"));
    }

    static void PairsMatch(RoomHarness h)
    {
        Assert.True(h.Start().Ok);
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
        var result = h.View(0).GetProperty("result");
        var journal = h.Outbox.OfType<Journal>().Last().Text;
        if (result.GetProperty("fool").ValueKind == JsonValueKind.Number)
        {
            var fool = result.GetProperty("fool").GetInt32();
            // Оля (місце 0) грає в парі з Глеком на місці 2: або вони обоє дурні, або Оля серед переможців.
            Assert.Equal(fool % 2 == 1, h.Room.Result!.Winners.Contains(0));
            Assert.StartsWith("Дурень: дурні — пара ", journal);
        }
    }

    [Fact]
    public void Leaving_in_pairs_makes_the_leavers_pair_the_fools()
    {
        var h = new RoomHarness("durak", new { teams = "1" });
        foreach (var n in new[] { "Оля", "Петро", "Іра", "Тарас" }) h.Join(n);
        Assert.True(h.Start().Ok);
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0, 2], h.Room.Result!.Winners.Order());
        Assert.Equal("Дурень: Петро встає з-за столу — пара Петро і Тарас лишається дурнями", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Equal("Петро", h.View(0).GetProperty("pogony").GetString());
    }
}
