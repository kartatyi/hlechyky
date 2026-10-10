using Hlechyky.Games.Impl;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>Правила «Двадцять одно» без каркаса (specs/blackjack.md §2, §4, §9): очки, Глек, роздача, дії, виплати, стратегія.</summary>
public class BlackjackCoreTests(ITestOutputHelper output)
{
    static Func<string> Cards(params string[] cards)
    {
        var i = 0;
        return () => i < cards.Length ? cards[i++] : throw new InvalidOperationException("карти скінчились");
    }

    static BjDeal One(int bet, params string[] cards) => BjDeal.Start([("Оля", 0, bet)], Cards(cards));

    [Fact]
    public void Score_counts_aces_as_eleven_when_it_fits()
    {
        Assert.Equal((21, true), BlackjackCore.Score(["As", "Td"]));
        Assert.Equal((17, true), BlackjackCore.Score(["As", "6d"]));
        Assert.Equal((12, false), BlackjackCore.Score(["As", "6d", "5c"]));
        Assert.Equal((12, true), BlackjackCore.Score(["As", "Ad"]));
        Assert.Equal((21, false), BlackjackCore.Score(["Kh", "Qd", "Ac"]));
        Assert.Equal((22, false), BlackjackCore.Score(["Kh", "Qd", "2c"]));
        Assert.Equal(10, BlackjackCore.Value("Jc"));
        Assert.True(BlackjackCore.Natural(["Ah", "Kd"]));
        Assert.False(BlackjackCore.Natural(["Ah", "5d", "5c"]));
    }

    [Fact]
    public void Glek_hits_soft_17_and_stands_on_hard_17_and_soft_18()
    {
        Assert.True(BlackjackCore.DealerHits(["Ah", "6d"]));
        Assert.True(BlackjackCore.DealerHits(["Th", "6d"]));
        Assert.False(BlackjackCore.DealerHits(["Th", "7d"]));
        Assert.False(BlackjackCore.DealerHits(["Ah", "7d"]));
        Assert.False(BlackjackCore.DealerHits(["Ah", "6d", "Tc"]));   // жорсткі 17
    }

    [Fact]
    public void Shoe_from_a_seed_is_six_whole_decks_and_repeats()
    {
        var seed = BlackjackCore.NewSeed(new Random(1));
        Assert.Equal(64, seed.Length);
        var a = BlackjackCore.Shoe(seed);
        Assert.Equal(312, a.Length);
        Assert.All(a.GroupBy(c => c), g => Assert.Equal(6, g.Count()));
        Assert.Equal(52, a.Distinct().Count());
        Assert.Equal(a, BlackjackCore.Shoe(seed));
        Assert.NotEqual(a, BlackjackCore.Shoe(BlackjackCore.NewSeed(new Random(2))));
        Assert.Equal(BlackjackCore.Hash(seed), BlackjackCore.Hash(seed));
        Assert.Matches("^[0-9a-f]{64}$", BlackjackCore.Hash(seed));
    }

    [Fact]
    public void Shoe_order_matches_the_reference_the_browser_repeats()
    {
        // те саме рахують ⓘ у web/games/blackjack.js (crypto.subtle) і Python-еталон (hashlib) — spec §4
        Assert.Equal(["Qs", "4h", "5c", "3s", "3s", "7d", "Qs", "8d"], BlackjackCore.Shoe(new string('a', 64)).Take(8));
    }

    [Fact]
    public void First_card_of_the_shoe_is_uniform_over_ranks()
    {
        var rng = new Random(5);
        var counts = new int[13];
        for (var i = 0; i < 2600; i++) counts[BlackjackCore.Ranks.IndexOf(BlackjackCore.Shoe(BlackjackCore.NewSeed(rng))[0][0])]++;
        Assert.All(counts, n => Assert.InRange(n, 130, 270));   // ≈ 200 кожного
    }

    [Fact]
    public void Deal_order_is_player_glek_player_glek()
    {
        var d = BjDeal.Start([("Оля", 0, 10), ("Петро", 1, 20)], Cards("2h", "3h", "4h", "5h", "6h", "7h"));
        Assert.Equal(["2h", "5h"], d.Boxes[0].Hands[0].Cards);
        Assert.Equal(["3h", "6h"], d.Boxes[1].Hands[0].Cards);
        Assert.Equal(["4h", "7h"], d.Dealer);
        Assert.False(d.HoleOpen);
        Assert.Equal((0, 0), (d.Box, d.Hand));
    }

    [Fact]
    public void Glek_peeks_under_ace_and_ten_and_blackjack_ends_the_round()
    {
        var d = One(10, "Th", "As", "9h", "Kd");
        Assert.True(d.DealerBj);
        Assert.True(d.PlayersDone);
        d.Settle();
        Assert.Equal("lose", d.Boxes[0].Hands[0].Outcome);
        // десятка зверху й туз під нею — теж
        Assert.True(One(10, "Th", "Ks", "9h", "Ad").DealerBj);
        // під дев'ятку Глек не заглядає (і блекджеку там не буває)
        Assert.False(One(10, "Th", "9s", "9h", "Ad").DealerBj);
    }

    [Fact]
    public void Blackjack_pays_one_to_one_and_loses_to_glek_blackjack()
    {
        var d = One(10, "Ah", "9c", "Kd", "7s");
        Assert.True(d.PlayersDone);
        Assert.False(d.AnyLive());
        d.DealerPlays(Cards());   // проти блекджеку Глек не добирає
        Assert.Equal(["9c", "7s"], d.Dealer);
        d.Settle();
        Assert.Equal(("bj", 20), (d.Boxes[0].Hands[0].Outcome, d.Boxes[0].Hands[0].Return));

        var both = One(10, "Ah", "As", "Kd", "Kc");
        both.Settle();
        Assert.Equal(("lose", 0), (both.Boxes[0].Hands[0].Outcome, both.Boxes[0].Hands[0].Return));
    }

    [Fact]
    public void Outcomes_pay_win_double_push_and_nothing()
    {
        Assert.Equal(20, BlackjackCore.Pays("win", 10));
        Assert.Equal(20, BlackjackCore.Pays("bj", 10));
        Assert.Equal(10, BlackjackCore.Pays("push", 10));
        Assert.Equal(0, BlackjackCore.Pays("lose", 10));
        Assert.Equal(0, BlackjackCore.Pays("bust", 10));

        var d = One(10, "6h", "6c", "5d", "Ts");
        Assert.Null(d.Act(BjDeal.Double, Cards("9h")));
        Assert.True(d.PlayersDone);
        d.DealerPlays(Cards("8c"));
        d.Settle();
        Assert.Equal(("win", 40, 20), (d.Boxes[0].Hands[0].Outcome, d.Boxes[0].Hands[0].Return, d.Boxes[0].Staked));
    }

    [Fact]
    public void Double_only_on_hard_9_10_11_with_two_cards()
    {
        Assert.True(One(10, "5h", "6c", "4d", "Ts").Allowed().Double);    // 9
        Assert.True(One(10, "5h", "6c", "6d", "Ts").Allowed().Double);    // 11
        Assert.False(One(10, "5h", "6c", "3d", "Ts").Allowed().Double);   // 8
        Assert.False(One(10, "5h", "6c", "7d", "Ts").Allowed().Double);   // 12
        Assert.False(One(10, "Ah", "6c", "9d", "Ts").Allowed().Double);   // м'які 20 — ні
        Assert.False(One(10, "Ah", "6c", "Ad", "Ts").Allowed().Double);   // м'які 12 — ні
        var three = One(10, "2h", "6c", "3d", "Ts");
        three.Act(BjDeal.Hit, Cards("4c"));                                // 9 з трьох карт — ні
        Assert.False(three.Allowed().Double);
        Assert.Equal("Подвоїти можна лише на перших двох картах, коли в тебе 9, 10 чи 11", three.Act(BjDeal.Double, Cards("Tc")));
    }

    [Fact]
    public void Split_up_to_three_hands_without_double_after_split()
    {
        var d = One(10, "8h", "9c", "8d", "9d");
        Assert.True(d.Allowed().Split);
        Assert.Null(d.Act(BjDeal.Split, Cards("8s", "3h")));
        Assert.Equal(2, d.Boxes[0].Hands.Count);
        Assert.Equal(["8h", "8s"], d.Boxes[0].Hands[0].Cards);
        Assert.Equal(["8d", "3h"], d.Boxes[0].Hands[1].Cards);
        Assert.True(d.Allowed().Split);
        Assert.Null(d.Act(BjDeal.Split, Cards("2c", "8c")));
        Assert.Equal(3, d.Boxes[0].Hands.Count);
        Assert.Equal(["8h", "2c"], d.Boxes[0].Hands[0].Cards);
        Assert.False(d.Allowed().Double);   // 10 після спліту — подвоїти не можна
        d.Act(BjDeal.Stand, Cards());
        Assert.Equal(["8s", "8c"], d.Current!.Cards);
        Assert.False(d.Allowed().Split);
        Assert.Equal("Більше трьох рук не буває", d.Act(BjDeal.Split, Cards("2d", "2s")));
        Assert.Equal("Розбити можна лише пару однакових карт", One(10, "Kh", "9c", "Qd", "9d").Act(BjDeal.Split, Cards("2d", "2s")));
    }

    [Fact]
    public void Split_aces_get_one_card_and_twenty_one_is_not_blackjack()
    {
        var d = One(10, "Ah", "7c", "Ad", "9d");
        Assert.Null(d.Act(BjDeal.Split, Cards("Kh", "5s")));
        Assert.True(d.PlayersDone);
        Assert.All(d.Boxes[0].Hands, h => Assert.True(h.Aces && h.Done));
        Assert.False(d.Boxes[0].Hands[0].IsNatural);
        d.DealerPlays(Cards("Tc"));
        d.Settle();
        Assert.Equal(["win", "win"], d.Boxes[0].Hands.Select(h => h.Outcome));
        Assert.Equal(40, d.Boxes[0].Returned);
    }

    [Fact]
    public void Hitting_to_21_stands_by_itself_and_bust_ends_the_hand()
    {
        var d = One(10, "5h", "9c", "6d", "8d");
        d.Act(BjDeal.Hit, Cards("Tc"));
        Assert.True(d.PlayersDone);
        var bust = One(10, "Th", "9c", "6d", "8d");
        bust.Act(BjDeal.Hit, Cards("Kc"));
        Assert.True(bust.PlayersDone);
        Assert.False(bust.AnyLive());
        bust.DealerPlays(Cards());
        bust.Settle();
        Assert.Equal("bust", bust.Boxes[0].Hands[0].Outcome);
    }

    [Fact]
    public void Basic_strategy_says_the_textbook_moves()
    {
        string Move(string[] mine, string up)
        {
            var d = new BjDeal
            {
                Boxes = [new BjBox { Bet = 1, Hands = [new BjHand { Bet = 1, Cards = [.. mine] }] }],
                Dealer = [up, "2c"], Box = 0, Hand = 0,
            };
            return BlackjackStrategy.Move(d)!;
        }
        Assert.Equal(BjDeal.Hit, Move(["Th", "6c"], "Td"));      // 16 проти десятки — беру
        Assert.Equal(BjDeal.Stand, Move(["Th", "2c"], "4d"));    // 12 проти четвірки — стою
        Assert.Equal(BjDeal.Hit, Move(["Th", "2c"], "2d"));      // 12 проти двійки — беру
        Assert.Equal(BjDeal.Double, Move(["6h", "5c"], "6d"));   // 11 — подвоюю
        Assert.Equal(BjDeal.Double, Move(["6h", "4c"], "9d"));   // 10 проти дев'ятки — подвоюю
        Assert.Equal(BjDeal.Hit, Move(["6h", "4c"], "Td"));      // 10 проти десятки — беру
        Assert.Equal(BjDeal.Split, Move(["8h", "8c"], "Td"));    // вісімки — завжди б'ю
        Assert.Equal(BjDeal.Split, Move(["Ah", "Ac"], "6d"));    // тузи — б'ю
        Assert.Equal(BjDeal.Stand, Move(["Th", "Tc"], "6d"));    // десятки — ніколи
        Assert.Equal(BjDeal.Hit, Move(["5h", "5c"], "Td"));      // п'ятірки — як 10
        Assert.Equal(BjDeal.Hit, Move(["Ah", "7c"], "9d"));      // м'які 18 проти дев'ятки — беру
        Assert.Equal(BjDeal.Stand, Move(["Ah", "7c"], "7d"));    // м'які 18 проти сімки — стою
        Assert.Equal(BjDeal.Stand, Move(["Th", "7c"], "Ad"));    // 17 — стою
    }

    [Fact]
    public void Strategy_chart_for_the_spec()
    {
        var chart = BlackjackStrategy.Chart();
        output.WriteLine(chart);
        Assert.Contains("P8", chart);
        Assert.DoesNotContain("?", chart);
    }
}
