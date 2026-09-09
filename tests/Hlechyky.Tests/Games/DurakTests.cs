using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Дурень підкидний на двох. Правила перевіряємо на <see cref="DurakCore"/> напряму — з випадкової роздачі
/// не збудуєш ні «козир козирем», ні шостої пари на столі; кімнату, види й приховування — через
/// <see cref="RoomHarness"/>, як і решта ігор (TESTING.md §4).
/// </summary>
public class DurakTests
{
    // ---------- дрібний інструмент ----------

    static int C(string card) => Cards.Parse(card) ?? throw new ArgumentException($"не карта: {card}");

    static DurakCore Core(string trump, string[] hand0, string[] hand1, string[]? deck = null, int attacker = 0)
    {
        var core = new DurakCore();
        core.Arrange(trump, hand0, hand1, deck, attacker);
        return core;
    }

    static string? Attack(DurakCore core, int seat, string card) => core.Attack(seat, C(card));
    static string? Defend(DurakCore core, int seat, string attack, string card) => core.Defend(seat, C(attack), C(card));

    static string[] Hand(DurakCore core, int seat) => [.. core.Sorted(seat).Select(Cards.Text)];

    /// <summary>Стіл у рядок: «7♠/K♠ 8♥/-» — так очима видно і атаки, і чим їх побили.</summary>
    static string Table(DurakCore core) =>
        string.Join(" ", core.Table.Select(p => Cards.Text(p.Attack) + "/" + (p.Defend is { } d ? Cards.Text(d) : "-")));

    static RoomHarness Sit(int seed = 7)
    {
        var h = new RoomHarness("durak", seed: seed);
        h.Join("Оля");
        h.Join("Петро");
        return h;
    }

    static string[] HandOf(RoomHarness h, int seat) =>
        [.. h.View(seat).GetProperty("hand").EnumerateArray().Select(e => e.GetString()!)];

    // =========================================================================================
    // Колода, роздача, перший хід
    // =========================================================================================

    [Fact]
    public void Every_card_text_survives_a_round_trip()
    {
        for (var card = 0; card < Cards.Count; card++) Assert.Equal(card, Cards.Parse(Cards.Text(card)));
        Assert.Equal("6♠", Cards.Text(0));
        Assert.Equal("A♣", Cards.Text(Cards.Count - 1));
    }

    [Fact]
    public void Nonsense_is_not_a_card()
    {
        foreach (var text in new[] { null, "", "7", "♥", "Z♥", "7x", "11♦", "  " })
            Assert.Null(Cards.Parse(text));
        Assert.Equal(C("10♦"), Cards.Parse(" 10♦ "));
        Assert.Equal(C("J♠"), Cards.Parse("j♠"));
    }

    [Fact]
    public void The_deck_holds_thirty_six_different_cards()
    {
        var core = new DurakCore();
        core.Deal(new Random(1));
        var all = core.Hands[0].Concat(core.Hands[1]).Concat(core.Deck).ToList();
        Assert.Equal(Cards.Count, all.Count);
        Assert.Equal(Cards.Count, all.Distinct().Count());
    }

    [Fact]
    public void Six_cards_each_and_twenty_four_left()
    {
        var core = new DurakCore();
        core.Deal(new Random(3));
        Assert.Equal(6, core.Hands[0].Count);
        Assert.Equal(6, core.Hands[1].Count);
        Assert.Equal(24, core.Deck.Count);
        Assert.Empty(core.Table);
        Assert.Equal(0, core.Discard);
        Assert.Null(core.Over);
        Assert.Equal(6, core.Limit);
    }

    [Fact]
    public void The_trump_is_the_suit_of_the_card_under_the_deck()
    {
        for (var seed = 1; seed <= 10; seed++)
        {
            var core = new DurakCore();
            core.Deal(new Random(seed));
            Assert.NotNull(core.TrumpCard);
            Assert.Equal(core.Trump, Cards.Suit(core.TrumpCard!.Value));
        }
    }

    [Fact]
    public void The_lowest_trump_opens_the_game()
    {
        for (var seed = 1; seed <= 40; seed++)
        {
            var core = new DurakCore();
            core.Deal(new Random(seed));
            var mine = Lowest(core, core.Attacker);
            var theirs = Lowest(core, core.Defender);
            // Рівність буває лише тоді, коли козирів нема ні в кого — тоді ходить перше місце.
            Assert.True(mine < theirs || (mine == theirs && core.Attacker == 0), $"сід {seed}");
        }

        static int Lowest(DurakCore core, int seat) => core.Hands[seat]
            .Where(c => Cards.Suit(c) == core.Trump).Select(Cards.Rank).DefaultIfEmpty(int.MaxValue).Min();
    }

    [Fact]
    public void With_no_trumps_at_all_the_first_seat_opens()
    {
        var core = Core("♣", ["6♠", "7♥", "K♦"], ["8♦", "9♠", "A♥"], attacker: 1);
        Assert.Equal(0, core.FirstAttacker());
    }

    [Fact]
    public void The_smaller_trump_wins_the_first_move()
    {
        Assert.Equal(1, Core("♣", ["6♠", "K♣"], ["8♦", "9♣"]).FirstAttacker());
        Assert.Equal(0, Core("♣", ["7♣", "K♣"], ["8♦", "9♣"]).FirstAttacker());
    }

    [Fact]
    public void The_same_seed_deals_the_same_cards()
    {
        static string Deal(int seed)
        {
            var core = new DurakCore();
            core.Deal(new Random(seed));
            return string.Join("|", Hand(core, 0)) + "//" + string.Join("|", Hand(core, 1)) + "//" + core.Trump + core.Attacker;
        }
        Assert.Equal(Deal(42), Deal(42));
        Assert.NotEqual(Deal(42), Deal(43));
    }

    [Fact]
    public void The_hand_is_sorted_by_suit_with_the_trump_last()
    {
        var core = Core("♦", ["K♠", "6♦", "7♥", "6♠", "A♦", "8♣"], ["6♥"]);
        Assert.Equal(["6♠", "K♠", "7♥", "8♣", "6♦", "A♦"], Hand(core, 0));
    }

    // =========================================================================================
    // Атака
    // =========================================================================================

    [Fact]
    public void The_attacker_opens_with_any_card()
    {
        var core = Core("♣", ["7♠", "9♥"], ["8♠", "6♥"]);
        Assert.Null(Attack(core, 0, "9♥"));
        Assert.Equal("9♥/-", Table(core));
        Assert.Equal(["7♠"], Hand(core, 0));
        Assert.Equal(DurakPhase.Defend, core.Phase);
        Assert.Equal(1, core.Turn);
    }

    [Fact]
    public void The_defender_cannot_attack()
    {
        var core = Core("♣", ["7♠"], ["8♠"]);
        Assert.Equal("Зараз ходить суперник", Attack(core, 1, "8♠"));
        Assert.Empty(core.Table);
    }

    [Fact]
    public void A_card_that_is_not_in_hand_is_refused()
    {
        var core = Core("♣", ["7♠"], ["8♠"]);
        Assert.Equal("Такої карти в тебе нема", Attack(core, 0, "8♠"));
        Assert.Empty(core.Table);
    }

    [Fact]
    public void While_something_is_unbeaten_the_attacker_waits()
    {
        var core = Core("♣", ["7♠", "7♥"], ["8♠", "8♥"]);
        Assert.Null(Attack(core, 0, "7♠"));
        Assert.Equal("Спершу дай суперникові відбитись", Attack(core, 0, "7♥"));
        Assert.Equal("7♠/-", Table(core));
    }

    // =========================================================================================
    // Захист
    // =========================================================================================

    [Fact]
    public void A_higher_card_of_the_same_suit_beats()
    {
        var core = Core("♣", ["7♠", "7♥"], ["8♠", "8♥"]);
        Attack(core, 0, "7♠");
        Assert.Null(Defend(core, 1, "7♠", "8♠"));
        Assert.Equal("7♠/8♠", Table(core));
        Assert.Equal(["8♥"], Hand(core, 1));
    }

    [Fact]
    public void A_lower_card_of_the_same_suit_does_not()
    {
        var core = Core("♣", ["9♠", "9♥"], ["8♠", "8♥"]);
        Attack(core, 0, "9♠");
        Assert.Equal("Такою не поб'єш", Defend(core, 1, "9♠", "8♠"));
        Assert.Equal("9♠/-", Table(core));
        Assert.Equal(2, core.Hands[1].Count);
    }

    [Fact]
    public void Another_suit_does_not_beat()
    {
        var core = Core("♣", ["7♠", "7♥"], ["K♦", "8♥"]);
        Attack(core, 0, "7♠");
        Assert.Equal("Такою не поб'єш", Defend(core, 1, "7♠", "K♦"));
        Assert.Equal("7♠/-", Table(core));
    }

    [Fact]
    public void A_trump_beats_a_plain_card()
    {
        var core = Core("♣", ["A♠", "A♥"], ["6♣", "8♥"]);
        Attack(core, 0, "A♠");
        Assert.Null(Defend(core, 1, "A♠", "6♣"));
        Assert.Equal("A♠/6♣", Table(core));
    }

    [Fact]
    public void A_trump_is_beaten_only_by_a_higher_trump()
    {
        var core = Core("♣", ["9♣", "9♥"], ["8♣", "K♣"]);
        Attack(core, 0, "9♣");
        Assert.Equal("Такою не поб'єш", Defend(core, 1, "9♣", "8♣"));
        Assert.Null(Defend(core, 1, "9♣", "K♣"));
        Assert.Equal("9♣/K♣", Table(core));
    }

    [Fact]
    public void A_plain_card_never_beats_a_trump()
    {
        var core = Core("♣", ["6♣", "7♥"], ["A♠", "A♥"]);
        Attack(core, 0, "6♣");
        Assert.Equal("Такою не поб'єш", Defend(core, 1, "6♣", "A♠"));
        Assert.Equal("Такою не поб'єш", Defend(core, 1, "6♣", "A♥"));
        Assert.Equal("6♣/-", Table(core));
    }

    [Fact]
    public void The_attacker_cannot_defend()
    {
        var core = Core("♣", ["7♠", "9♠"], ["8♠", "8♥"]);
        Attack(core, 0, "7♠");
        Assert.Equal("Відбивається суперник", Defend(core, 0, "7♠", "9♠"));
    }

    [Fact]
    public void A_card_that_is_already_beaten_is_left_alone()
    {
        var core = Core("♣", ["7♠", "7♥"], ["8♠", "9♠"]);
        Attack(core, 0, "7♠");
        Defend(core, 1, "7♠", "8♠");
        Attack(core, 0, "7♥");                       // стіл знову живий, є що бити
        Assert.Equal("Цю карту вже побито", Defend(core, 1, "7♠", "9♠"));
    }

    [Fact]
    public void An_attack_that_is_not_on_the_table_is_refused()
    {
        var core = Core("♣", ["7♠", "9♥"], ["8♠", "9♠"]);
        Attack(core, 0, "7♠");
        Assert.Equal("Такої карти на столі нема", Defend(core, 1, "9♥", "9♠"));
    }

    [Fact]
    public void Defending_with_nothing_on_the_table_is_refused()
    {
        var core = Core("♣", ["7♠"], ["8♠"]);
        Assert.Equal("Зараз нема чого бити", Defend(core, 1, "7♠", "8♠"));
    }

    [Fact]
    public void Defending_with_a_card_you_do_not_hold_is_refused()
    {
        var core = Core("♣", ["7♠", "9♥"], ["8♥"]);
        Attack(core, 0, "7♠");
        Assert.Equal("Такої карти в тебе нема", Defend(core, 1, "7♠", "8♠"));
    }

    // =========================================================================================
    // Підкидання і його ліміти
    // =========================================================================================

    [Fact]
    public void Only_ranks_already_on_the_table_can_be_added()
    {
        var core = Core("♣", ["7♠", "9♥", "7♦"], ["8♠", "6♥", "6♦"]);
        Attack(core, 0, "7♠");
        Defend(core, 1, "7♠", "8♠");
        Assert.Equal(DurakPhase.Attack, core.Phase);
        Assert.Equal("Підкидати можна лише те, що вже на столі", Attack(core, 0, "9♥"));
        Assert.Null(Attack(core, 0, "7♦"));
        Assert.Equal("7♠/8♠ 7♦/-", Table(core));
    }

    [Fact]
    public void The_rank_of_a_defending_card_counts_too()
    {
        var core = Core("♣", ["7♠", "8♥", "K♦"], ["8♠", "6♥", "6♦"]);
        Attack(core, 0, "7♠");
        Defend(core, 1, "7♠", "8♠");                 // на столі тепер і сімки, і вісімки
        Assert.Null(Attack(core, 0, "8♥"));
        Assert.Equal("7♠/8♠ 8♥/-", Table(core));
    }

    [Fact]
    public void The_bout_holds_at_most_six_pairs()
    {
        var core = Core("♣",
            ["7♠", "7♥", "7♦", "7♣", "9♠", "9♥", "9♦"],
            ["K♠", "K♥", "K♦", "9♣", "10♠", "10♥"]);
        Assert.Equal(6, core.Limit);

        foreach (var (attack, defend) in new[] { ("7♠", "K♠"), ("7♥", "K♥"), ("7♦", "K♦"), ("7♣", "9♣"), ("9♠", "10♠") })
        {
            Assert.Null(Attack(core, 0, attack));
            Assert.Null(Defend(core, 1, attack, defend));
            Assert.Equal(DurakPhase.Attack, core.Phase);
        }
        Assert.Equal(5, core.Table.Count);
        Assert.Null(Attack(core, 0, "9♥"));
        Assert.Equal(6, core.Table.Count);
        Assert.Null(Defend(core, 1, "9♥", "10♥"));

        // Шоста пара — стеля: відбій закривається сам, сьома карта на стіл уже не потрапляє.
        Assert.Empty(core.Table);
        Assert.Equal(12, core.Discard);
        Assert.Equal(["9♦"], Hand(core, 0));
    }

    [Fact]
    public void A_seventh_attack_never_fits()
    {
        // Стеля в шість карт у звичайному перебігу спрацьовує сама (див. вище), тож відмову побачить лише
        // клієнт із протухлим видом. Стіл для цього складаємо руками.
        var core = Core("♣", ["9♦"], ["6♠", "6♥", "6♦", "6♣", "7♠", "7♥"]);
        foreach (var card in new[] { "8♠", "8♥", "8♦", "8♣", "9♠", "9♥" })
            core.Table.Add(new DurakPair(C(card), null));
        core.Phase = DurakPhase.Taking;

        Assert.Equal("На стіл більше не влізе", Attack(core, 0, "9♦"));
        Assert.Equal(6, core.Table.Count);
    }

    [Fact]
    public void More_cards_than_the_defender_holds_are_refused()
    {
        var core = Core("♣", ["7♠", "7♥"], ["6♦", "6♥"]);
        Assert.Null(Attack(core, 0, "7♠"));
        core.Phase = DurakPhase.Taking;   // ніби захисник уже сказав «Беру»...
        core.Limit = 1;                   // ...а карта в нього була одна
        Assert.Equal("У суперника стільки карт нема", Attack(core, 0, "7♥"));
        Assert.Single(core.Table);
    }

    [Fact]
    public void The_defenders_hand_size_caps_the_bout()
    {
        // У захисника дві карти — більше двох на стіл не покладеш, третя сімка лишається в руці.
        var core = Core("♣", ["7♠", "7♥", "7♦"], ["6♠", "6♥"]);
        Assert.Equal(2, core.Limit);
        Attack(core, 0, "7♠");
        Assert.Null(core.Take(1));
        Assert.Equal(DurakPhase.Taking, core.Phase);
        Assert.Null(Attack(core, 0, "7♥"));

        // Друга карта добила ліміт — відбій закрився сам, обидві сімки поїхали до захисника.
        Assert.Equal(DurakPhase.Attack, core.Phase);
        Assert.Equal(["7♦"], Hand(core, 0));
        Assert.Equal(4, core.Hands[1].Count);
        Assert.Equal(0, core.Discard);
    }

    [Fact]
    public void After_take_the_attacker_may_still_add()
    {
        var core = Core("♣", ["7♠", "7♥", "9♦"], ["6♠", "6♥", "6♦"]);
        Attack(core, 0, "7♠");
        Assert.Null(core.Take(1));
        Assert.Equal(DurakPhase.Taking, core.Phase);
        Assert.True(core.CanAdd);
        Assert.Null(Attack(core, 0, "7♥"));

        // Підкидати більше нічим (дев'ятка не пасує) — стіл їде до захисника, ходить той самий.
        Assert.Equal(0, core.Attacker);
        Assert.Equal(["9♦"], Hand(core, 0));
        Assert.Equal(5, core.Hands[1].Count);
        Assert.Contains("7♠", Hand(core, 1));
        Assert.Contains("7♥", Hand(core, 1));
    }

    // =========================================================================================
    // Кінець відбою
    // =========================================================================================

    [Fact]
    public void Bito_needs_every_attack_beaten()
    {
        var core = Core("♣", ["7♠", "7♥"], ["8♠", "8♥"]);
        Attack(core, 0, "7♠");
        Assert.Equal("Спершу дай суперникові відбитись", core.Done(0));
        Assert.Single(core.Table);
    }

    [Fact]
    public void Bito_on_an_empty_table_is_refused()
    {
        var core = Core("♣", ["7♠"], ["8♠"]);
        Assert.Equal("Спершу зайди картою", core.Done(0));
    }

    [Fact]
    public void Bito_is_only_for_the_attacker()
    {
        var core = Core("♣", ["7♠", "7♥"], ["8♠", "8♥"]);
        Attack(core, 0, "7♠");
        Defend(core, 1, "7♠", "8♠");
        Assert.Equal("«Біто» каже той, хто ходить", core.Done(1));
    }

    [Fact]
    public void A_beaten_bout_goes_to_the_discard_and_swaps_the_roles()
    {
        var core = Core("♣", ["7♠", "7♥"], ["8♠", "8♦"]);
        Attack(core, 0, "7♠");
        Defend(core, 1, "7♠", "8♠");
        Assert.Equal(DurakPhase.Attack, core.Phase);   // ще є що підкинути — «Біто» тисне гравець

        Assert.Null(core.Done(0));
        Assert.Empty(core.Table);
        Assert.Equal(2, core.Discard);
        Assert.Equal(1, core.Attacker);
        Assert.Equal(0, core.Defender);
        Assert.Equal(DurakPhase.Attack, core.Phase);
        Assert.Equal(1, core.Limit);                   // у нового захисника лишилась одна карта
    }

    [Fact]
    public void Nothing_left_to_add_closes_the_bout_by_itself()
    {
        var core = Core("♣", ["7♠", "9♥"], ["8♠", "6♥"]);
        Attack(core, 0, "7♠");
        Assert.Null(Defend(core, 1, "7♠", "8♠"));

        // Дев'ятка до сімок і вісімок не пасує — тиснути «Біто» нема за чим.
        Assert.Empty(core.Table);
        Assert.Equal(2, core.Discard);
        Assert.Equal(1, core.Attacker);
    }

    [Fact]
    public void Taking_moves_the_whole_table_into_the_defenders_hand()
    {
        var core = Core("♣", ["7♠", "7♥", "9♦"], ["8♠", "6♥", "6♦"]);
        Attack(core, 0, "7♠");
        Defend(core, 1, "7♠", "8♠");
        Attack(core, 0, "7♥");
        Assert.Null(core.Take(1));

        Assert.Empty(core.Table);
        Assert.Equal(0, core.Discard);
        Assert.Equal(0, core.Attacker);                       // взяв — ходить той самий
        Assert.Equal(["7♠", "8♠", "6♥", "7♥", "6♦"], Hand(core, 1));
    }

    [Fact]
    public void Take_is_only_for_the_defender()
    {
        var core = Core("♣", ["7♠", "7♥"], ["8♠", "8♥"]);
        Attack(core, 0, "7♠");
        Assert.Equal("Бере той, хто відбивається", core.Take(0));
    }

    [Fact]
    public void Take_needs_something_to_beat()
    {
        var core = Core("♣", ["7♠"], ["8♠"]);
        Assert.Equal("Зараз нема чого брати", core.Take(1));
    }

    [Fact]
    public void Enough_finishes_the_take_when_the_attacker_says_so()
    {
        var core = Core("♣", ["7♠", "7♥", "7♦"], ["6♠", "6♥", "6♦"]);
        Attack(core, 0, "7♠");
        core.Take(1);
        Assert.Equal(DurakPhase.Taking, core.Phase);

        Assert.Null(core.Done(0));
        Assert.Empty(core.Table);
        Assert.Equal(4, core.Hands[1].Count);
        Assert.Equal(["7♥", "7♦"], Hand(core, 0));
        Assert.Equal(0, core.Attacker);
    }

    [Fact]
    public void The_attacker_refills_first_and_the_trump_card_comes_last()
    {
        var deck = new[] { "6♦", "7♦", "8♦", "9♦", "10♦", "J♦", "Q♦", "K♣" };
        var core = Core("♣", ["7♠"], ["8♠"], deck);
        Attack(core, 0, "7♠");
        Assert.Null(Defend(core, 1, "7♠", "8♠"));

        // Обидва лишились без карт: атакуючий добирає перших шість, решта — захиснику, козир останній.
        Assert.Equal(6, core.Hands[0].Count);
        Assert.Equal(2, core.Hands[1].Count);
        Assert.Empty(core.Deck);
        Assert.Null(core.TrumpCard);
        Assert.Equal(["Q♦", "K♣"], Hand(core, 1));
        Assert.DoesNotContain("K♣", Hand(core, 0));
    }

    [Fact]
    public void After_taking_only_the_attacker_refills()
    {
        var core = Core("♣", ["7♠", "9♥"], ["6♠", "6♥"], ["8♦", "10♦", "J♦", "Q♦", "K♦", "A♦", "6♣"]);
        Attack(core, 0, "7♠");
        Assert.Null(core.Take(1));

        Assert.Equal(6, core.Hands[0].Count);      // добрав до шести
        Assert.Equal(3, core.Hands[1].Count);      // свої дві плюс узята сімка, добору нема
        Assert.Equal(2, core.Deck.Count);
        Assert.Equal(0, core.Attacker);
    }

    // =========================================================================================
    // Кінець партії
    // =========================================================================================

    [Fact]
    public void An_empty_hand_with_an_empty_deck_wins()
    {
        var core = Core("♣", ["7♠", "7♥", "7♦"], ["6♠", "6♥", "6♦"]);
        Attack(core, 0, "7♠");
        core.Take(1);
        Attack(core, 0, "7♥");
        Assert.Null(Attack(core, 0, "7♦"));         // третя добила ліміт — відбій закрився сам

        Assert.NotNull(core.Over);
        Assert.Equal(0, core.Over!.Winner);
        Assert.Equal("out", core.Over.Reason);
        Assert.Equal(DurakPhase.Done, core.Phase);
        Assert.Equal(6, core.Hands[1].Count);
    }

    [Fact]
    public void Both_out_at_once_is_a_draw()
    {
        var core = Core("♣", ["7♠", "7♥"], ["8♠", "8♥"]);
        Attack(core, 0, "7♠");
        Defend(core, 1, "7♠", "8♠");
        Attack(core, 0, "7♥");
        Assert.Null(Defend(core, 1, "7♥", "8♥"));

        Assert.NotNull(core.Over);
        Assert.Null(core.Over!.Winner);
        Assert.Equal("both", core.Over.Reason);
        Assert.Equal(4, core.Discard);
    }

    [Fact]
    public void The_one_left_with_cards_is_the_fool()
    {
        var core = Core("♣", ["7♠"], ["8♠", "9♥"]);
        Attack(core, 0, "7♠");
        Assert.Null(Defend(core, 1, "7♠", "8♠"));
        Assert.Equal(0, core.Over!.Winner);          // вийшов перший, дурень — другий
        Assert.Single(core.Hands[1]);
    }

    [Fact]
    public void A_finished_game_refuses_every_move()
    {
        var core = Core("♣", ["7♠"], ["8♠", "9♥"]);
        Attack(core, 0, "7♠");
        Defend(core, 1, "7♠", "8♠");
        Assert.Equal("Партію зіграно", Attack(core, 1, "9♥"));
        Assert.Equal("Партію зіграно", core.Take(0));
        Assert.Equal("Партію зіграно", core.Done(1));
        Assert.Equal("Партію зіграно", Defend(core, 0, "7♠", "9♥"));
        Assert.False(core.CanAdd);
    }

    [Fact]
    public void Quitting_makes_the_other_one_the_winner()
    {
        var core = Core("♣", ["7♠"], ["8♠", "9♥"]);
        core.Quit(1);
        Assert.Equal(1, core.Over!.Winner);
        Assert.Equal("left", core.Over.Reason);
        Assert.Equal(DurakPhase.Done, core.Phase);
        core.Quit(0);                                 // другий раз нічого не міняє
        Assert.Equal(1, core.Over!.Winner);
    }

    // =========================================================================================
    // Кімната: види, приховування, кінець, рематч
    // =========================================================================================

    [Fact]
    public void The_game_is_in_the_catalog_as_a_hidden_pair_table()
    {
        var game = Assert.Single(new Registry().Catalog, g => g.Id == "durak");
        Assert.Equal("Дурень", game.Title);
        Assert.Equal("дурня", game.Accusative);
        Assert.Equal("board", game.Group);
        Assert.Equal("whenFull", game.Start);
        Assert.True(game.Hidden);
        Assert.True(game.Rated);
        Assert.False(game.Private);
        Assert.Equal(2, game.MinPlayers);
        Assert.Equal(2, game.MaxPlayers);
        Assert.Equal(0, game.TickMs);
        Assert.Equal("durak", game.Module);
        Assert.True(game.HasCss);
        Assert.NotEmpty(game.Hint);
    }

    [Fact]
    public void The_seats_are_named_for_the_lobby()
    {
        var h = Sit();
        Assert.Equal(["перший", "другий"], h.Room.Summary().SeatNames);
    }

    [Fact]
    public void The_view_has_the_shape_the_module_expects()
    {
        var h = Sit();
        var v = h.View(0);

        Assert.Equal(JsonValueKind.Number, v.GetProperty("turn").ValueKind);
        var attacker = v.GetProperty("attacker").GetInt32();
        var defender = v.GetProperty("defender").GetInt32();
        Assert.InRange(attacker, 0, 1);
        Assert.InRange(defender, 0, 1);
        Assert.NotEqual(attacker, defender);
        Assert.Equal(attacker, v.GetProperty("turn").GetInt32());
        Assert.Equal("attack", v.GetProperty("phase").GetString());
        Assert.Contains(v.GetProperty("trump").GetString()!, (IEnumerable<string>)["♠", "♥", "♦", "♣"]);
        Assert.EndsWith(v.GetProperty("trump").GetString()!, v.GetProperty("trumpCard").GetString()!);
        Assert.Equal(24, v.GetProperty("deck").GetInt32());
        Assert.Empty(v.GetProperty("table").EnumerateArray());
        Assert.Equal(6, v.GetProperty("hand").GetArrayLength());
        Assert.Equal([6, 6], v.GetProperty("counts").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(0, v.GetProperty("discard").GetInt32());
        Assert.True(v.GetProperty("canAdd").GetBoolean());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("result").ValueKind);
    }

    [Fact]
    public void The_table_pairs_look_the_way_the_module_reads_them()
    {
        var h = Sit();
        var attacker = h.View(null).GetProperty("attacker").GetInt32();
        var card = HandOf(h, attacker)[0];
        Assert.True(h.Act(attacker, "attack", new { card }).Ok);

        var pair = h.View(null).GetProperty("table")[0];
        Assert.Equal(card, pair.GetProperty("attack").GetString());
        Assert.Equal(JsonValueKind.Null, pair.GetProperty("defend").ValueKind);
        Assert.Equal("defend", h.View(null).GetProperty("phase").GetString());
        Assert.Equal(attacker == 0 ? 1 : 0, h.View(null).GetProperty("turn").GetInt32());
    }

    [Fact]
    public void A_hand_belongs_to_its_own_seat_only()
    {
        var h = Sit();
        var mine = HandOf(h, 0);
        var theirs = HandOf(h, 1);

        Assert.Equal(6, mine.Length);
        Assert.Equal(6, theirs.Length);
        Assert.Empty(mine.Intersect(theirs));
        // У виді немає жодного сліду чужих карт — лише їх кількість.
        var seen = Views.Text(h.Room.Game.View(0));
        foreach (var card in theirs) Assert.DoesNotContain(card, seen);
    }

    [Fact]
    public void A_watcher_sees_no_hands_at_all()
    {
        var h = Sit();
        var v = h.View(null);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("hand").ValueKind);
        Assert.Equal([6, 6], v.GetProperty("counts").EnumerateArray().Select(e => e.GetInt32()));

        var seen = Views.Text(h.Room.Game.View(null));
        foreach (var card in HandOf(h, 0).Concat(HandOf(h, 1))) Assert.DoesNotContain(card, seen);
        Assert.False(Views.Has(v, "hands"));
        Assert.False(Views.Has(v, "cards"));
    }

    [Fact]
    public void An_illegal_move_changes_nothing()
    {
        var h = Sit();
        var attacker = h.View(null).GetProperty("attacker").GetInt32();
        var defender = attacker == 0 ? 1 : 0;
        var before = Views.Text(h.Room.Game.View(null));

        Assert.Equal("Зараз ходить суперник", h.Act(defender, "attack", new { card = HandOf(h, defender)[0] }).Message);
        Assert.Equal("Зараз нема чого бити", h.Act(defender, "defend", new { attack = "6♠", card = HandOf(h, defender)[0] }).Message);
        Assert.Equal("Спершу зайди картою", h.Act(attacker, "done").Message);
        Assert.Equal("Зараз нема чого брати", h.Act(defender, "take").Message);
        Assert.Equal(before, Views.Text(h.Room.Game.View(null)));
        Assert.Equal(0, h.Room.Moves);
    }

    [Fact]
    public void An_unknown_action_and_a_broken_payload_are_refused()
    {
        var h = Sit();
        var attacker = h.View(null).GetProperty("attacker").GetInt32();
        Assert.Equal("Тут так не ходять", h.Act(attacker, "fold", new { card = "7♥" }).Message);
        Assert.Equal("Не зрозумів, яка карта", h.Act(attacker, "attack", new { nope = 1 }).Message);
        Assert.Equal("Не зрозумів, яка карта", h.Act(attacker, "attack", new { card = "Z♥" }).Message);
        Assert.Equal("Не зрозумів, чим і що бити", h.Act(attacker, "defend", new { card = "7♥" }).Message);
        Assert.Equal(0, h.Room.Moves);
    }

    [Fact]
    public void Leaving_mid_game_makes_the_leaver_the_fool()
    {
        var h = Sit();
        Assert.True(h.Leave("Петро").Ok);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        var result = h.View(0).GetProperty("result");
        Assert.Equal(0, result.GetProperty("winner").GetInt32());
        Assert.Equal("left", result.GetProperty("reason").GetString());
        Assert.Contains("встав з-за столу", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("turn").ValueKind);
        Assert.Single(h.Finished);
    }

    [Fact]
    public void Rematch_deals_again_and_swaps_the_seats()
    {
        var h = Sit(5);
        var was = string.Join("|", HandOf(h, 0));
        PlayOut(h);
        Assert.True(h.Rematch("Оля").Ok);

        Assert.Equal("Петро", h.Room.Seats[0]);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(24, h.View(0).GetProperty("deck").GetInt32());
        Assert.Equal(0, h.View(0).GetProperty("discard").GetInt32());
        Assert.Empty(h.View(0).GetProperty("table").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("result").ValueKind);
        Assert.Equal(0, h.Room.Moves);
        Assert.NotEqual(was, string.Join("|", HandOf(h, 0)));
    }

    [Fact]
    public void A_played_out_game_names_the_fool_in_the_journal()
    {
        var h = Sit(11);
        PlayOut(h);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var text = h.Outbox.OfType<Journal>().Last().Text;
        Assert.StartsWith("Дурень: ", text);
        var result = h.Room.Result!;
        if (result.Draw) Assert.Contains("вийшли разом", text);
        else
        {
            Assert.Contains("дурень — ", text);
            Assert.Single(result.Winners);
        }
        var finished = Assert.Single(h.Finished);
        Assert.Equal("durak", finished.GameId);
        Assert.True(h.Room.Moves > 5, "справжня партія має бути довшою за п'ять ходів");
        Assert.Equal("Партію зіграно, тисни «Ще раз»", h.Act(0, "done").Message);
    }

    [Fact]
    public void Every_seed_plays_out_to_a_verdict()
    {
        for (var seed = 1; seed <= 25; seed++)
        {
            var h = Sit(seed);
            PlayOut(h);
            Assert.Equal(RoomStatus.Finished, h.Room.Status);

            var v = h.View(null);
            var result = v.GetProperty("result");
            var reason = result.GetProperty("reason").GetString()!;
            Assert.Contains(reason, (IEnumerable<string>)["out", "both"]);
            Assert.Equal("done", v.GetProperty("phase").GetString());
            Assert.Equal(0, v.GetProperty("deck").GetInt32());
            Assert.Empty(v.GetProperty("table").EnumerateArray());

            var counts = v.GetProperty("counts").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            var discard = v.GetProperty("discard").GetInt32();
            Assert.Equal(Cards.Count, counts[0] + counts[1] + discard);   // карти нікуди не діваються
            if (reason == "both") Assert.Equal(JsonValueKind.Null, result.GetProperty("winner").ValueKind);
            else Assert.Equal(0, counts[result.GetProperty("winner").GetInt32()]);
        }
    }

    // ---------- простий гравець для наскрізних партій ----------

    /// <summary>
    /// Грає партію до кінця найпростішою стратегією: заходить першою картою, б'є найдешевшим, чим може,
    /// не підкидає й не тягне час. Стратегія свідомо тупа — тут перевіряються правила, а не гра.
    /// </summary>
    static void PlayOut(RoomHarness h, int cap = 2000)
    {
        for (var i = 0; i < cap && h.Room.Status == RoomStatus.Playing; i++)
        {
            var pub = h.View(null);
            var phase = pub.GetProperty("phase").GetString()!;
            var turn = pub.GetProperty("turn").GetInt32();
            var trump = pub.GetProperty("trump").GetString()!;
            var hand = HandOf(h, turn);
            var open = pub.GetProperty("table").EnumerateArray()
                .Where(t => t.GetProperty("defend").ValueKind == JsonValueKind.Null)
                .Select(t => t.GetProperty("attack").GetString()!)
                .ToArray();

            ActResult step;
            if (phase == "defend")
            {
                var pick = hand.Where(c => BeatsText(c, open[0], trump))
                    .OrderBy(c => Cards.Suit(C(c)) == Cards.SuitOf(trump) ? 1 : 0)
                    .ThenBy(c => Cards.Rank(C(c)))
                    .FirstOrDefault();
                step = pick is null ? h.Act(turn, "take") : h.Act(turn, "defend", new { attack = open[0], card = pick });
            }
            else if (phase == "attack" && pub.GetProperty("table").GetArrayLength() == 0)
            {
                step = h.Act(turn, "attack", new { card = hand[0] });
            }
            else
            {
                step = h.Act(turn, "done");
            }
            Assert.True(step.Ok, $"хід у фазі «{phase}» відбито: {step.Message}");
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
    }

    static bool BeatsText(string card, string against, string trump)
    {
        var (c, a) = (C(card), C(against));
        return Cards.Suit(c) == Cards.Suit(a) ? Cards.Rank(c) > Cards.Rank(a) : Cards.Suit(c) == Cards.SuitOf(trump);
    }
}
