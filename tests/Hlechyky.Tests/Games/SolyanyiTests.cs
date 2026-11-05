using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;
using static Hlechyky.Games.Impl.SolyanyiMap;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Соляний шлях»: правила §2 і тексти помилок §6 — розклад, setup, ринок із замінами й перетасуванням, два взяття, оплата
/// шляхів, парні шляхи, замовлення, Найдовша валка з петлями, кінець партії й рівності (контракт §14).
/// </summary>
public class SolyanyiTests(ITestOutputHelper output)
{
    // =====================================================================================
    // Розклад
    // =====================================================================================

    [Fact]
    public void The_start_uses_the_rng_in_the_contract_order()
    {
        foreach (var n in new[] { 2, 5 })
        {
            const int seed = 12;
            var c = SolyanyiBotTests.Core(n, seed);
            var rng = new Random(seed);
            var cards = Enumerable.Range(0, 96).Select(i => i / 12).Concat(Enumerable.Repeat(8, 14)).ToArray();
            Fy(cards, rng);
            var tickets = Enumerable.Range(0, 40).ToArray();
            Fy(tickets, rng);
            var first = c.Order[rng.Next(n)];
            Assert.Equal(first, c.First);
            var at = 0;
            var order = Enumerable.Range(0, n).Select(i => (first + i) % n).ToArray();
            foreach (var s in order)
            {
                var hand = new int[9];
                foreach (var k in cards.Skip(at).Take(4)) hand[k]++;
                at += 4;
                Assert.Equal(hand, c.Hand[s]);
            }
            if (cards.Skip(at).Take(5).Count(k => k == 8) < 3)
                Assert.Equal(cards.Skip(at).Take(5), c.Market);
            var t = 0;
            foreach (var s in order)
            {
                Assert.Equal(tickets.Skip(t).Take(3), c.Offer[s]);
                t += 3;
            }
            Assert.Equal(tickets.Skip(t), c.TicketDeck);
            Assert.Equal(SlPhase.Setup, c.Phase);
            Assert.Equal(0, c.Round);
            Assert.All(c.Order, s => Assert.Equal(45, c.Wagons[s]));
            SolyanyiBotTests.Invariants(c);
        }
    }

    static void Fy(int[] a, Random rng)
    {
        for (var i = a.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (a[i], a[j]) = (a[j], a[i]);
        }
    }

    [Fact]
    public void Same_seed_same_deal_and_hand_market_and_offer_sizes()
    {
        var a = SolyanyiBotTests.Core(3, 9);
        var b = SolyanyiBotTests.Core(3, 9);
        Assert.Equal(a.Deck, b.Deck);
        Assert.Equal(a.Market, b.Market);
        Assert.Equal(a.TicketDeck, b.TicketDeck);
        Assert.Equal(a.First, b.First);
        Assert.All(a.Order, s =>
        {
            Assert.Equal(4, a.Hand[s].Sum());
            Assert.Equal(3, a.Offer[s].Count);
        });
        Assert.Equal(5, a.Market.Count(m => m >= 0));
        var seen = a.Deck.Concat(a.Discard).Concat(a.Market).Concat(a.Order.SelectMany(s => Enumerable.Range(0, 9).SelectMany(k => Enumerable.Repeat(k, a.Hand[s][k])))).ToList();
        Assert.Equal(110, seen.Count);
        Assert.Equal(14, seen.Count(k => k == Wild));
        Assert.All(Enumerable.Range(0, 8), k => Assert.Equal(12, seen.Count(x => x == k)));
    }

    [Fact]
    public void Events_at_the_start_go_deal_then_setup_then_round_and_turn()
    {
        var c = SolyanyiBotTests.Core(2, 9);
        Assert.Equal(["deal", "deal"], c.Events.Where(e => e.K is "deal" or "setup" or "round" or "turn").Select(e => e.K));
        var deal = c.Events.First(e => e.K == "deal");
        Assert.Equal(c.First, deal["seat"]);
        Assert.Equal(4, deal["cards"]);
        Assert.Equal(3, deal["tickets"]);
        var a = c.First;
        var b = c.Next(a);
        Assert.Null(c.Keep(a, [.. c.Offer[a].Take(2)]));
        Assert.Null(c.Keep(b, [.. c.Offer[b]]));
        var keys = c.Events.Select(e => e.K).Where(k => k is "deal" or "setup" or "round" or "turn").ToArray();
        Assert.Equal(["deal", "deal", "setup", "setup", "round", "turn"], keys);
        Assert.True(c.Events.Zip(c.Events.Skip(1)).All(p => p.First.Seq < p.Second.Seq));
    }

    /// <summary>
    /// Подвійний дотик по ринку: перший забрав карту, місце вже заповнила нова; другий несе ту карту, що людина бачила, і сервер
    /// відмовляє, а не бере незнайому карту, що закінчила б хід (§6). Без card (Глеки, старий клієнт) — як було.
    /// </summary>
    [Fact]
    public void Draw_with_an_expected_card_refuses_what_was_refilled_under_the_finger()
    {
        var h = SlRig.Play(2, seed: 6);
        var c = SlRig.CoreOf(h);
        var s = c.Turn;
        c.Market[2] = Array.IndexOf(Cards, "fish");
        c.Deck.Insert(0, Wild);
        Assert.True(h.Act(s, "draw", new { slot = 2, card = "fish" }).Ok);
        Assert.Equal(SlPhase.Draw, c.Phase);
        Assert.Equal(s, c.Turn);
        Assert.Equal(Wild, c.Market[2]);

        var state = SlRig.State(c);
        Assert.Equal("Цю карту вже замінили — глянь на ринок ще раз", h.Act(s, "draw", new { slot = 2, card = "fish" }).Message);
        Assert.Equal("Цю карту вже замінили — глянь на ринок ще раз", h.Act(s, "draw", new { slot = 2, card = "дурниця" }).Message);
        Assert.Equal(state, SlRig.State(c));
        // Порядок перевірок: місце порожнє — важливіше за очікування; вола другою й далі не беруть, навіть якщо вгадав.
        Assert.Equal("Вола з ринку другою картою не беруть", h.Act(s, "draw", new { slot = 2, card = "wild" }).Message);
        var m = c.Market[2];
        c.Market[2] = -1;
        Assert.Equal("На цьому місці порожньо", h.Act(s, "draw", new { slot = 2, card = "fish" }).Message);
        c.Market[2] = m;

        // Збіг і відсутність card — звичайне взяття; у deck card нічого не значить.
        var k = Enumerable.Range(0, 5).First(i => c.Market[i] >= 0 && c.Market[i] != Wild);
        Assert.True(h.Act(s, "draw", new { slot = k, card = Cards[c.Market[k]] }).Ok);
        var t = c.Turn;
        Assert.True(h.Act(t, "draw", new { deck = true, card = "fish" }).Ok);
        var free = Enumerable.Range(0, 5).First(i => c.Market[i] >= 0 && c.Market[i] != Wild);
        Assert.True(h.Act(t, "draw", new { slot = free }).Ok);
    }

    // =====================================================================================
    // Setup і замовлення
    // =====================================================================================

    [Fact]
    public void Setup_keeps_at_least_two_each_privately_then_first_goes()
    {
        var h = SlRig.Table(3);
        var c = SlRig.CoreOf(h);
        Assert.Equal(SlPhase.Setup, c.Phase);
        var a = c.First;
        var b = c.Next(a);
        var o = c.Next(b);
        var offerA = c.Offer[a].ToArray();
        var deck0 = c.TicketDeck.Count;
        Assert.Equal("Спершу всі обирають замовлення", h.Act(a, "draw", new { deck = true }).Message);
        Assert.Equal("Спершу всі обирають замовлення", h.Act(a, "claim", new { route = 2, c = "fish", w = 0 }).Message);
        Assert.Equal("Спершу всі обирають замовлення", h.Act(a, "tickets").Message);
        Assert.Equal("Спершу всі обирають замовлення", h.Act(a, "pass").Message);
        Assert.Equal("Не зрозумів, які замовлення", h.Act(a, "keep", new { }).Message);
        Assert.Equal("Не зрозумів, які замовлення", h.Act(a, "keep", new { ids = "x" }).Message);
        Assert.Equal("Не зрозумів, які замовлення", h.Act(a, "keep", new { ids = new object[] { "a" } }).Message);
        Assert.Equal("Лиши щонайменше 2", h.Act(a, "keep", new { ids = new[] { offerA[0] } }).Message);
        Assert.Equal("Цього замовлення тобі не пропонували", h.Act(a, "keep", new { ids = new[] { offerA[0], offerA[0] } }).Message);
        Assert.Equal("Цього замовлення тобі не пропонували", h.Act(a, "keep", new { ids = new[] { offerA[0], c.Offer[b][0] } }).Message);
        var back = offerA.Skip(1).Max();
        var keep = offerA.Where(x => x != back).ToArray();
        Assert.True(h.Act(a, "keep", new { ids = keep }).Ok);
        Assert.Equal("Ти вже обрав", h.Act(a, "keep", new { ids = keep }).Message);
        Assert.Equal(keep.OrderBy(x => Array.IndexOf(offerA, x)), c.Tickets[a]);
        Assert.Equal(back, c.TicketDeck[^1]);
        Assert.Equal(deck0 + 1, c.TicketDeck.Count);
        Assert.Equal(SlPhase.Setup, c.Phase);
        Assert.True(h.Act(b, "keep", new { ids = c.Offer[b].ToArray() }).Ok);
        // Чужі вибори не змішуються.
        Assert.Equal(3, c.Tickets[b].Count);
        Assert.Empty(c.Tickets[a].Intersect(c.Tickets[b]));
        Assert.True(h.Act(o, "keep", new { ids = c.Offer[o].Take(2).ToArray() }).Ok);
        Assert.Equal(SlPhase.Play, c.Phase);
        Assert.Equal(a, c.Turn);
        Assert.Equal(1, c.Round);
        Assert.Equal("Зараз нема чого обирати", h.Act(a, "keep", new { ids = new[] { 1 } }).Message);
    }

    [Fact]
    public void Bots_choose_at_the_start_in_turn_order()
    {
        var h = SlRig.Table(1, bots: "4", seed: 3);
        var c = SlRig.CoreOf(h);
        Assert.Equal(SlPhase.Setup, c.Phase);
        Assert.Equal(5, c.Seats);
        Assert.All(c.Order.Where(s => s != 0), s =>
        {
            Assert.True(c.Ready[s]);
            Assert.True(c.Tickets[s].Count >= 2);
            Assert.Empty(c.Offer[s]);
        });
        Assert.False(c.Ready[0]);
        SlRig.Ready(h);
        Assert.Equal(SlPhase.Play, c.Phase);
    }

    [Fact]
    public void Taking_tickets_returns_the_rest_to_the_bottom_sorted_and_two_left_means_an_offer_of_two()
    {
        var h = SlRig.Play(2);
        var c = SlRig.CoreOf(h);
        var s = c.Turn;
        var o = c.Next(s);
        var tail = c.TicketDeck.Skip(3).ToArray();
        var top = c.TicketDeck.Take(3).ToArray();
        Assert.True(h.Act(s, "tickets").Ok);
        Assert.Equal(SlPhase.Keep, c.Phase);
        Assert.Equal(top, c.Offer[s]);
        Assert.Equal(1, c.KeepMin);
        Assert.Equal("Спершу обери замовлення", h.Act(s, "draw", new { deck = true }).Message);
        Assert.Equal("Спершу обери замовлення", h.Act(s, "tickets").Message);
        Assert.Equal($"Зараз їде {h.NickOf(s)}", h.Act(o, "keep", new { ids = new[] { top[0] } }).Message);
        Assert.Equal("Лиши щонайменше 1", h.Act(s, "keep", new { ids = Array.Empty<int>() }).Message);
        Assert.True(h.Act(s, "keep", new { ids = new[] { top[1] } }).Ok);
        Assert.Contains(top[1], c.Tickets[s]);
        Assert.Equal(tail.Concat(top.Where(x => x != top[1]).Order()), c.TicketDeck);
        Assert.Equal(o, c.Turn);

        c.TicketDeck.RemoveRange(2, c.TicketDeck.Count - 2);
        var two = c.TicketDeck.ToArray();
        Assert.True(h.Act(o, "tickets").Ok);
        Assert.Equal(two, c.Offer[o]);
        Assert.Empty(c.TicketDeck);
        Assert.True(h.Act(o, "keep", new { ids = two }).Ok);
        Assert.Equal("Замовлень більше нема", h.Act(s, "tickets").Message);
    }

    [Fact]
    public void A_ticket_is_done_by_any_own_network_and_only_the_owner_hears_it()
    {
        var c = SolyanyiBotTests.Playing(3, 5);
        var s = c.Turn;
        foreach (var x in c.Order) c.Tickets[x].Clear();
        c.Tickets[s].Add(34);  // Біла Церква — Кропивницький
        SlRig.Give(c, s, (Wild, 12));
        // Кружний шлях: Біла Церква — Черкаси (35) — Кропивницький (36).
        Assert.Null(c.Claim(s, 35, "wild", 3));
        Assert.False(c.Done[34]);
        Assert.Null(c.Draw(c.Turn, null, true));
        Assert.Null(c.Draw(c.Turn, null, true));
        Assert.Null(c.Draw(c.Turn, null, true));
        Assert.Null(c.Draw(c.Turn, null, true));
        Assert.Equal(s, c.Turn);
        Assert.Null(c.Claim(s, 36, "wild", 2));
        Assert.True(c.Done[34]);
        var done = c.Events.Single(e => e.K == "done");
        Assert.Equal(s, done.Only);
        Assert.Equal(1, done.Own);
        Assert.Equal(c.Seq, c.Events.Where(e => e.Only < 0).Max(e => e.Seq));   // спільний лічильник вона не рухає
        Assert.Null(done.For(null));
        Assert.Null(done.For(c.Next(s)));
        Assert.Equal(34, done.For(s)!["ticket"]);
        Assert.Equal(5, c.DonePts(s));
        // Узяв посеред партії замовлення, яке вже з'єднане, — виконане одразу.
        var c2 = SolyanyiBotTests.Playing(2, 5);
        var a = c2.Turn;
        foreach (var r in new[] { 35, 36 })
        {
            c2.Owner[r] = a;
            c2.Taken[a].Add(r);
        }
        var deck = c2.TicketDeck;
        deck.Remove(34);
        foreach (var x in c2.Order) c2.Tickets[x].Remove(34);
        deck.Insert(0, 34);
        Assert.Null(c2.TakeTickets(a));
        Assert.Null(c2.Keep(a, [34]));
        Assert.True(c2.Done[34]);
    }

    // =====================================================================================
    // Ринок (§2.3)
    // =====================================================================================

    [Fact]
    public void Three_wilds_wipe_the_market_into_the_discard()
    {
        var c = SolyanyiBotTests.Playing(2, 5);
        var s = c.Turn;
        SlRig.Market(c, Wild, Wild, Salt, Fish, Grain);
        SlRig.Top(c, Wild, Salt, Fish, Grain, Tar, Honey);
        var discard = c.Discard.Count;
        var seq = c.Seq;
        Assert.Null(c.Draw(s, 2, false));
        Assert.Equal(discard + 5, c.Discard.Count);
        var wipe = c.Events.Single(e => e.Seq > seq && e.K == "wipe");
        Assert.Equal(new[] { "wild", "wild", "wild", "fish", "grain" }, wipe["cards"]);
        Assert.Equal(new[] { Salt, Fish, Grain, Tar, Honey }, c.Market);
        // Подій: draw, refill (віл), wipe, далі 5 refill.
        Assert.Equal(["draw", "refill", "wipe", "refill", "refill", "refill", "refill", "refill"],
            c.Events.Where(e => e.Seq > seq).Select(e => e.K));
        Assert.Contains("На ринку три воли — карти змінено", c.Log);
        SolyanyiBotTests.Invariants(c);
    }

    [Fact]
    public void Wipes_stop_after_three_in_a_row_and_the_wilds_stay()
    {
        var c = SolyanyiBotTests.Playing(2, 5);
        var s = c.Turn;
        SlRig.Give(c, s);
        SlRig.Give(c, c.Next(s));
        SlRig.Market(c, Wild, Wild, Salt, Fish, Grain);
        SlRig.Top(c, Wild, Wild, Wild, Wild, Tar, Honey, Wild, Wild, Wild, Tar, Honey, Wild, Wild, Wild, Tar, Honey);
        var seq = c.Seq;
        Assert.Null(c.Draw(s, 2, false));
        Assert.Equal(WipeMax, c.Events.Count(e => e.Seq > seq && e.K == "wipe"));
        Assert.True(c.Market.Count(m => m == Wild) >= 3);
        SolyanyiBotTests.Invariants(c);
    }

    [Fact]
    public void Too_few_plain_cards_in_reserve_means_no_wipe()
    {
        var c = SolyanyiBotTests.Playing(2, 5);
        var s = c.Turn;
        c.Market = [Wild, Wild, Salt, Fish, Grain];
        c.Deck = [Wild, Salt, Fish];
        c.Discard = [];
        var seq = c.Seq;
        Assert.Null(c.Draw(s, 2, false));
        Assert.DoesNotContain(c.Events, e => e.Seq > seq && e.K == "wipe");
        Assert.Equal(3, c.Market.Count(m => m == Wild));
    }

    [Fact]
    public void Empty_deck_reshuffles_the_discard_and_when_nothing_is_left_the_slot_stays_empty()
    {
        var c = SolyanyiBotTests.Playing(2, 5);
        var s = c.Turn;
        c.Market = [Salt, Fish, Grain, Tar, Honey];
        c.Deck = [];
        c.Discard = [Wool, Hides];
        var seq = c.Seq;
        Assert.Null(c.Draw(s, 0, false));
        var shuffle = c.Events.Single(e => e.Seq > seq && e.K == "shuffle");
        Assert.Equal(2, shuffle["n"]);
        Assert.Empty(c.Discard);
        Assert.Single(c.Deck);
        Assert.True(c.Market[0] is Wool or Hides);
        Assert.Contains("Скид перетасовано", c.Log);
        // Друге взяття наосліп бере останню.
        Assert.Null(c.Draw(s, null, true));
        Assert.Empty(c.Deck);
        // Порожньо всюди — місце лишається null, і про це йде refill.
        var o = c.Turn;
        seq = c.Seq;
        Assert.Null(c.Draw(o, 1, false));
        Assert.Equal(-1, c.Market[1]);
        var refill = c.Events.Single(e => e.Seq > seq && e.K == "refill");
        Assert.Equal(1, refill["slot"]);
        Assert.Null(refill["card"]);
        Assert.Equal("Колода порожня", c.Draw(o, null, true));
    }

    [Fact]
    public void The_market_is_refilled_after_a_claim_too()
    {
        var c = SolyanyiBotTests.Playing(2, 5);
        var s = c.Turn;
        SlRig.Give(c, s, (Wild, 1));
        c.Market[3] = -1;
        c.Deck.Add(c.Deck[0]);
        var top = c.Deck[0];
        var seq = c.Seq;
        Assert.Null(c.Claim(s, 2, "wild", 1));
        Assert.Equal(top, c.Market[3]);
        var k = c.Events.Where(e => e.Seq > seq).Select(e => e.K).ToArray();
        Assert.True(Array.IndexOf(k, "claim") < Array.IndexOf(k, "refill"));
    }

    // =====================================================================================
    // Карти (§2.3, §6)
    // =====================================================================================

    [Fact]
    public void Each_draw_error_has_its_own_text()
    {
        var h = SlRig.Play(2);
        var c = SlRig.CoreOf(h);
        var s = c.Turn;
        var o = c.Next(s);
        var state = SlRig.State(c);
        Assert.Equal($"Зараз їде {h.NickOf(s)}", h.Act(o, "draw", new { deck = true }).Message);
        Assert.Equal("Не зрозумів, яку карту", h.Act(s, "draw", new { }).Message);
        Assert.Equal("Не зрозумів, яку карту", h.Act(s, "draw", new { slot = "x" }).Message);
        Assert.Equal("Не зрозумів, яку карту", h.Act(s, "draw", new { deck = false }).Message);
        Assert.Equal("Такого місця на ринку нема", h.Act(s, "draw", new { slot = 5 }).Message);
        Assert.Equal("Такого місця на ринку нема", h.Act(s, "draw", new { slot = -1 }).Message);
        Assert.Equal("Тут так не ходять", h.Act(s, "fly").Message);
        Assert.Equal(state, SlRig.State(c));
        var m = c.Market[2];
        c.Market[2] = -1;
        Assert.Equal("На цьому місці порожньо", h.Act(s, "draw", new { slot = 2 }).Message);
        c.Market[2] = m;
        var (deck, discard) = (c.Deck, c.Discard);
        c.Deck = [];
        c.Discard = [];
        Assert.Equal("Колода порожня", h.Act(s, "draw", new { deck = true }).Message);
        (c.Deck, c.Discard) = (deck, discard);
    }

    [Fact]
    public void Two_picks_a_turn_blind_wild_counts_as_a_card_and_draw_phase_forbids_other_actions()
    {
        var h = SlRig.Play(2);
        var c = SlRig.CoreOf(h);
        var s = c.Turn;
        SlRig.Market(c, Salt, Fish, Grain, Tar, Honey);
        SlRig.Top(c, Wild, Wild, Salt, Fish, Grain, Tar);
        var hand = c.Hand[s].Sum();
        Assert.True(h.Act(s, "draw", new { deck = true }).Ok);
        Assert.Equal(SlPhase.Draw, c.Phase);
        Assert.Equal(s, c.Turn);
        Assert.Equal("Спершу візьми другу карту", h.Act(s, "claim", new { route = 2, c = "wild", w = 1 }).Message);
        Assert.Equal("Спершу візьми другу карту", h.Act(s, "tickets").Message);
        Assert.Equal("Спершу візьми другу карту", h.Act(s, "pass").Message);
        Assert.True(h.Act(s, "draw", new { slot = 0 }).Ok);
        Assert.Equal(hand + 2, c.Hand[s].Sum());
        Assert.NotEqual(s, c.Turn);
        Assert.Equal(SlPhase.Play, c.Phase);
        Assert.Equal(1, c.Turns);
        Assert.Contains($"{h.NickOf(s)} бере сіль і карту наосліп", c.Log);
    }

    [Fact]
    public void Open_wild_first_ends_the_action_and_second_is_refused()
    {
        var h = SlRig.Play(2);
        var c = SlRig.CoreOf(h);
        var s = c.Turn;
        SlRig.Market(c, Wild, Fish, Grain, Tar, Honey);
        SlRig.Top(c, Salt, Fish, Grain);
        Assert.True(h.Act(s, "draw", new { slot = 0 }).Ok);
        var o = c.Turn;
        Assert.NotEqual(s, o);
        Assert.Contains($"{h.NickOf(s)} бере вола з ринку — і більше нічого", c.Log);
        Assert.Equal(1, c.FaceWild[s]);
        SlRig.Market(c, Wild, Fish, Grain, Tar, Honey);
        SlRig.Top(c, Salt, Fish, Grain);
        Assert.True(h.Act(o, "draw", new { slot = 1 }).Ok);
        Assert.Equal("Вола з ринку другою картою не беруть", h.Act(o, "draw", new { slot = 0 }).Message);
        Assert.True(h.Act(o, "draw", new { slot = 2 }).Ok);
        Assert.Contains($"{h.NickOf(o)} бере рибу і збіжжя", c.Log);
        Assert.Equal(s, c.Turn);
    }

    [Fact]
    public void No_possible_second_pick_ends_the_action_by_itself()
    {
        var c = SolyanyiBotTests.Playing(2, 5);
        var s = c.Turn;
        c.Market = [Salt, Wild, Wild, -1, -1];
        c.Deck = [];
        c.Discard = [];
        Assert.Null(c.Draw(s, 0, false));
        Assert.NotEqual(s, c.Turn);
        Assert.Equal(SlPhase.Play, c.Phase);
        Assert.Contains("бере сіль", c.Log[^1]);
    }

    [Fact]
    public void Blind_card_is_hidden_from_others_and_market_card_is_public()
    {
        var c = SolyanyiBotTests.Playing(2, 5);
        var s = c.Turn;
        var o = c.Next(s);
        SlRig.Market(c, Salt, Fish, Grain, Tar, Honey);
        SlRig.Top(c, Wool, Hides, Linen);
        Assert.Null(c.Draw(s, null, true));
        Assert.Null(c.Draw(s, 0, false));
        var draws = c.Events.Where(e => e.K == "draw").ToArray();
        Assert.Equal("wool", draws[0].For(s)!["card"]);
        Assert.Null(draws[0].For(o)!["card"]);
        Assert.Null(draws[0].For(null)!["card"]);
        Assert.Equal("deck", draws[0].For(o)!["from"]);
        Assert.Equal(1, draws[0].For(o)!["pick"]);
        Assert.Equal("salt", draws[1].For(o)!["card"]);
        Assert.Equal(2, draws[1]["pick"]);
        Assert.Equal(0, draws[1]["slot"]);
    }

    // =====================================================================================
    // Шлях (§2.4, §6)
    // =====================================================================================

    [Fact]
    public void Each_claim_error_has_its_own_text_and_changes_nothing()
    {
        var h = SlRig.Play(2);
        var c = SlRig.CoreOf(h);
        var s = c.Turn;
        var o = c.Next(s);
        SlRig.Give(c, s, (Salt, 3), (Fish, 1), (Wild, 1));
        var state = SlRig.State(c);
        ActResult C(object p) => h.Act(s, "claim", p);
        Assert.Equal($"Зараз їде {h.NickOf(s)}", h.Act(o, "claim", new { route = 0, c = "salt", w = 0 }).Message);
        Assert.Equal("Не зрозумів, який шлях", C(new { }).Message);
        Assert.Equal("Не зрозумів, який шлях", C(new { route = "x" }).Message);
        Assert.Equal("Такого шляху на мапі нема", C(new { route = 102, c = "salt", w = 0 }).Message);
        Assert.Equal("Не зрозумів, чим платиш", C(new { route = 0, w = 0 }).Message);
        Assert.Equal("Не зрозумів, чим платиш", C(new { route = 0, c = "salt" }).Message);
        Assert.Equal("Не зрозумів, чим платиш", C(new { route = 0, c = "salt", w = "x" }).Message);
        Assert.Equal("Не зрозумів, чим платиш", C(new { route = 0, c = "salt", w = -1 }).Message);
        Assert.Equal("Не зрозумів, чим платиш", C(new { route = 0, c = "salt", w = 4 }).Message);
        Assert.Equal("Такого вантажу нема", C(new { route = 0, c = "grey", w = 0 }).Message);
        Assert.Equal("На цей шлях треба сіль", C(new { route = 0, c = "fish", w = 0 }).Message);
        Assert.Equal("Не зрозумів, чим платиш", C(new { route = 0, c = "wild", w = 2 }).Message);
        Assert.Equal("Не зрозумів, чим платиш", C(new { route = 0, c = "salt", w = 3 }).Message);
        Assert.Equal("Стільки волів у тебе нема", C(new { route = 0, c = "wild", w = 3 }).Message);
        Assert.Equal("Стільки волів у тебе нема", C(new { route = 0, c = "salt", w = 2 }).Message);
        Assert.Equal("Стільки риби у тебе нема", C(new { route = 12, c = "fish", w = 0 }).Message);
        c.Wagons[s] = 2;
        Assert.Equal("Бракує возів: треба 3, є 2", C(new { route = 0, c = "salt", w = 0 }).Message);
        c.Wagons[s] = 45;
        Assert.Equal(state, SlRig.State(c));
        // Зайнятий — ім'я власника.
        c.Owner[0] = o;
        Assert.Equal($"Цей шлях уже зайняв {h.NickOf(o)}", C(new { route = 0, c = "salt", w = 0 }).Message);
    }

    [Fact]
    public void Colored_takes_its_cargo_and_wilds_grey_any_single_cargo_and_wilds_alone_work_everywhere()
    {
        var c = SolyanyiBotTests.Playing(2, 5);
        var s = c.Turn;
        var o = c.Next(s);
        void Pass()
        {
            Assert.Null(c.Draw(o, null, true));
            Assert.Null(c.Draw(o, null, true));
        }
        SlRig.Give(c, s, (Salt, 2), (Fish, 1), (Grain, 2), (Wild, 4));
        var discard = c.Discard.Count;
        Assert.Null(c.Claim(s, 0, "salt", 1));   // кольоровий 3: 2 солі + віл
        Assert.Equal(discard + 3, c.Discard.Count);
        Assert.Equal([Salt, Salt, Wild], c.Discard.Skip(discard));
        Assert.Equal(0, c.Hand[s][Salt]);
        Assert.Equal(3, c.Hand[s][Wild]);
        Assert.Equal(42, c.Wagons[s]);
        Assert.Equal(4, c.RoutePts[s]);
        Pass();
        Assert.Null(c.Claim(s, 4, "grain", 1));  // сірий 3: будь-який один вантаж і воли
        Pass();
        Assert.Equal("Не зрозумів, чим платиш", c.Claim(s, 5, "honey", 2));
        Assert.Null(c.Claim(s, 5, "wild", 2));   // кольоровий мед: самі воли
        Pass();
        Assert.Equal(new[] { 0, 4, 5 }, c.Taken[s]);
    }

    [Theory]
    [InlineData(2, 1, 1)]
    [InlineData(1, 2, 2)]
    [InlineData(0, 3, 4)]
    [InlineData(13, 4, 7)]
    [InlineData(63, 5, 10)]
    [InlineData(37, 6, 15)]
    public void Route_points_by_length(int route, int len, int pts)
    {
        var c = SolyanyiBotTests.Playing(3, 5);
        var s = c.Turn;
        Assert.Equal(len, Routes[route].Len);
        SlRig.Give(c, s, (Wild, len));
        Assert.Null(c.Claim(s, route, "wild", len));
        Assert.Equal(pts, c.RoutePts[s]);
        Assert.Equal(45 - len, c.Wagons[s]);
        var e = c.Events.Last(x => x.K == "claim");
        Assert.Equal(pts, e["pts"]);
        Assert.Equal(45 - len, e["wagons"]);
        Assert.Equal(len, e["road"]);
        Assert.Equal(0, e["n"]);
        Assert.Equal(len, e["w"]);
        Assert.Equal(c.Owner[route], s);
    }

    /// <summary>Перебір усіх шляхів × усіх можливих оплат: що обіцяє Quote, те й приймає Claim, і навпаки.</summary>
    [Fact]
    public void Quotes_match_what_claim_accepts_for_every_route_and_payment()
    {
        var hands = new (int Card, int N)[][]
        {
            [(Salt, 4), (Fish, 1), (Wild, 2)],
            [(Grain, 6)],
            [(Wild, 7)],
            [(Linen, 2), (Grain, 1), (Wool, 1), (Hides, 1), (Wild, 1)],
            [],
        };
        foreach (var hand in hands)
        {
            var src = SolyanyiBotTests.Playing(4, 5);
            var s = src.Turn;
            SlRig.Give(src, s, hand);
            src.Owner[3] = src.Next(s);                      // зайнятий
            src.Owner[38] = src.Next(s);                     // пара чужа — на 4 можна
            src.Owner[59] = s;                               // пара своя — не можна
            src.Taken[s].Add(59);
            var work = src.Clone();
            foreach (var r in Routes)
            {
                if (src.Owner[r.Id] >= 0) continue;
                var q = src.Quote(s, r.Id);
                Assert.Equal(q.Ok, src.ClaimOk(s, r.Id));
                var accepted = new List<SlPay>();
                for (var card = 0; card < 9; card++)
                    for (var w = 0; w <= r.Len; w++)
                    {
                        if (work.Claim(s, r.Id, Cards[card], w) is not null) continue;
                        accepted.Add(new SlPay(card, card == Wild ? 0 : r.Len - w, w));
                        work = src.Clone();
                    }
                var want = accepted.OrderBy(p => p.W).ThenBy(p => p.C).ToList();
                Assert.Equal(want, q.Combos);
                Assert.Equal(q.Ok, want.Count > 0);
                Assert.Equal(q.Ok ? src.Pays(s, r.Id) : [], q.Combos);
            }
        }
    }

    [Fact]
    public void Quote_reasons_follow_the_contract_order_and_texts()
    {
        var c = SolyanyiBotTests.Playing(3, 5);
        var s = c.Turn;
        var o = c.Next(s);
        SlRig.Give(c, s, (Linen, 1));
        c.Owner[39] = o;   // пара 91 закрита правилом 2–3
        var q = c.Quote(s, 91);
        Assert.Equal((false, 0, "За столом на 2–3 парний шлях один, і його вже зайнято"), (q.Ok, q.Short, q.Why));
        c.Wagons[s] = 2;
        Assert.Equal("Бракує возів: треба 3, є 2", c.Quote(s, 0).Why);
        Assert.Equal(0, c.Quote(s, 0).Short);
        c.Wagons[s] = 45;
        Assert.Equal((2 + 1, "бракує 3 солі"), (c.Quote(s, 0).Short, c.Quote(s, 0).Why));
        // Сірий: найбільший стос.
        Assert.Equal((2, "бракує 2 карт одного вантажу"), (c.Quote(s, 4).Short, c.Quote(s, 4).Why));
        Assert.Equal((1, "бракує 1 карти одного вантажу"), (c.Quote(s, 1).Short, c.Quote(s, 1).Why));
        var c4 = SolyanyiBotTests.Playing(4, 5);
        var a = c4.Turn;
        c4.Owner[39] = a;
        Assert.Equal("Обидва парні шляхи одному не можна", c4.Quote(a, 91).Why);
        Assert.Equal(c4.Quote(a, 91).Why, c4.Claim(a, 91, "wild", 2));
    }

    [Fact]
    public void Combos_are_ordered_by_wilds_then_cargo_and_best_saves_what_the_plan_needs()
    {
        var c = SolyanyiBotTests.Playing(2, 5);
        var s = c.Turn;
        SlRig.Give(c, s, (Grain, 1), (Wool, 1), (Hides, 1), (Linen, 2), (Wild, 1));
        // Приклад §5: шлях 24 (сірий 1), план тягне шляхи 10 (полотно), 17 (збіжжя), 18 (вовна) — шкіри зайві.
        var combos = c.Pays(s, 24);
        Assert.Equal([new SlPay(Grain, 1, 0), new SlPay(Wool, 1, 0), new SlPay(Hides, 1, 0), new SlPay(Linen, 1, 0), new SlPay(Wild, 0, 1)], combos);
        var plan = new SlPlan([10, 17, 18, 15, 88], 15, []);
        Assert.Equal(2, SolyanyiBot.BestPay(c, s, 24, combos, plan));
        // Шлях 10 — сам у плані: 2 полотна без волів.
        var ten = c.Pays(s, 10);
        Assert.Equal([new SlPay(Linen, 2, 0), new SlPay(Linen, 1, 1)], ten);
        Assert.Equal(0, SolyanyiBot.BestPay(c, s, 10, ten, plan));
        // Лише воли — найгірше, поки є що інше.
        SlRig.Give(c, s, (Salt, 1), (Wild, 3));
        var zero = c.Pays(s, 0);
        Assert.Equal(new SlPay(Salt, 1, 2), zero[0]);
        Assert.Equal(new SlPay(Wild, 0, 3), zero[1]);
        Assert.Equal(0, SolyanyiBot.BestPay(c, s, 0, zero, new SlPlan([], 0, [])));
    }

    // =====================================================================================
    // Парні (§2.4)
    // =====================================================================================

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void At_two_or_three_the_second_twin_is_closed_for_everyone(int n)
    {
        var c = SolyanyiBotTests.Playing(n, 5);
        var s = c.Turn;
        SlRig.Give(c, s, (Wild, 5));
        Assert.Null(c.Claim(s, 39, "wild", 2));
        Assert.Equal([91], c.Closed());
        var o = c.Turn;
        SlRig.Give(c, o, (Wild, 5));
        const string closed = "За столом на 2–3 парний шлях один, і його вже зайнято";
        Assert.Equal(closed, c.Claim(o, 91, "wild", 2));
        Assert.Equal(-1, c.Cost(o, 91));
        Assert.Equal(-1, c.Cost(s, 91));
        Assert.False(c.ClaimOk(o, 91));
        Assert.DoesNotContain(91, SolyanyiBot.Plan(c, o, [27]).Zero);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void At_four_or_five_both_twins_go_but_not_to_one_player(int n)
    {
        var c = SolyanyiBotTests.Playing(n, 5);
        var s = c.Turn;
        SlRig.Give(c, s, (Wild, 5));
        Assert.Null(c.Claim(s, 39, "wild", 2));
        Assert.Empty(c.Closed());
        var o = c.Turn;
        SlRig.Give(c, o, (Wild, 5));
        Assert.Equal(2, c.Cost(o, 91));
        Assert.Null(c.Claim(o, 91, "wild", 2));
        for (var i = 0; i < n * 2; i++)
        {
            if (c.Turn == s) break;
            var t = c.Turn;
            Assert.Null(c.Draw(t, null, true));
            Assert.Null(c.Draw(t, null, true));
        }
        var c2 = SolyanyiBotTests.Playing(n, 5);
        var a = c2.Turn;
        c2.Owner[39] = a;
        c2.Taken[a].Add(39);
        SlRig.Give(c2, a, (Wild, 2));
        Assert.Equal("Обидва парні шляхи одному не можна", c2.Claim(a, 91, "wild", 2));
        Assert.Equal(-1, c2.Cost(a, 91));
    }

    // =====================================================================================
    // Найдовша валка (§2.7)
    // =====================================================================================

    static SolyanyiCore With(int s, params int[] routes)
    {
        var c = SolyanyiBotTests.Playing(4, 5);
        foreach (var r in routes)
        {
            c.Owner[r] = s;
            c.Taken[s].Add(r);
        }
        return c;
    }

    [Theory]
    [InlineData(8, 0, 2, 1, 3)]                 // петля Київ — Чернігів — Ніжин — Київ + Ніжин — Конотоп
    [InlineData(6, 59, 38, 34)]                 // зірка: лише два промені
    [InlineData(14, 0, 2, 1, 59, 61, 60)]       // вісімка: дві петлі через Київ
    [InlineData(3, 0)]                          // один шлях
    [InlineData(9, 0, 37)]                      // дві дуги через Київ
    public void Longest_road_by_hand(int want, params int[] routes)
    {
        var c = With(0, routes);
        Assert.Equal(want, c.LongestRoad(0));
        var path = c.LongestPath(0);
        Assert.Equal(want, path.Sum(r => Routes[r].Len));
        Assert.Equal(path.Length, path.Distinct().Count());
        AssertTrail(path);
    }

    /// <summary>Шляхи справді йдуть один за одним.</summary>
    static void AssertTrail(int[] path)
    {
        if (path.Length < 2) return;
        var a = Routes[path[0]];
        var b = Routes[path[1]];
        var at = a.A == b.A || a.A == b.B ? a.A : a.B;
        foreach (var id in path.Skip(1))
        {
            var r = Routes[id];
            Assert.True(r.A == at || r.B == at, string.Join(",", path));
            at = Other(r, at);
        }
    }

    /// <summary>Повний перебір із кожного міста, як написано в контракті, — наш прискорений обхід мусить дати те саме.</summary>
    static int Brute(IReadOnlyList<int> routes)
    {
        var best = 0;
        var used = new bool[Routes.Length];
        void Go(int at, int len)
        {
            best = Math.Max(best, len);
            foreach (var id in routes)
            {
                if (used[id]) continue;
                var r = Routes[id];
                if (r.A != at && r.B != at) continue;
                used[id] = true;
                Go(Other(r, at), len + r.Len);
                used[id] = false;
            }
        }
        foreach (var city in routes.SelectMany(id => new[] { Routes[id].A, Routes[id].B }).Distinct()) Go(city, 0);
        return best;
    }

    [Fact]
    public void Fast_road_equals_the_full_search_on_random_networks()
    {
        var rng = new Random(4);
        for (var i = 0; i < 400; i++)
        {
            var from = rng.Next(60);
            var picks = Enumerable.Range(from, 26).Where(_ => rng.Next(100) < 55).Where(r => Routes[r].Twin < 0 || r < Routes[r].Twin).Take(14).ToArray();
            var c = With(0, picks);
            var want = Brute(picks);
            Assert.Equal(want, c.LongestRoad(0));
            var path = c.LongestPath(0);
            Assert.Equal(want, path.Sum(r => Routes[r].Len));
            AssertTrail(path);
        }
    }

    [Fact]
    public void Separate_networks_take_the_larger_and_ties_share_the_bonus_and_no_routes_no_bonus()
    {
        var c = With(0, 0, 2, 72);     // 3 + 1 = 4 і окремо 5
        Assert.Equal(5, c.LongestRoad(0));
        var e = SolyanyiBotTests.Playing(3, 5);
        var (a, b, o) = (e.Order[0], e.Order[1], e.Order[2]);
        e.Road[a] = 7;
        e.Road[b] = 7;
        e.Taken[a].AddRange([60, 61, 62]);
        e.Taken[b].AddRange([0, 2, 3]);
        SlRig.Stall(e);
        Assert.Equal("stall", e.Reason);
        Assert.Equal([a, b], e.LongestSeats);
        Assert.Equal(10, e.Bonus(a));
        Assert.Equal(10, e.Bonus(b));
        Assert.Equal(0, e.Bonus(o));
        var empty = SolyanyiBotTests.Playing(2, 5);
        SlRig.Stall(empty);
        Assert.Empty(empty.LongestSeats);
        Assert.Equal(0, empty.LongestLen);
        Assert.All(empty.Order, s => Assert.Equal(0, empty.Bonus(s)));
    }

    /// <summary>Зв'язна мережа на 45 возів: щоразу шлях, що прилягає (випадковий серед найкоротших) — найгустіше, що може мати гравець.</summary>
    static List<int> Dense(int city, Random rng, int wagons = 45)
    {
        var mine = new HashSet<int> { city };
        var taken = new List<int>();
        while (true)
        {
            var next = Routes.Where(r => !taken.Contains(r.Id) && (r.Twin < 0 || !taken.Contains(r.Twin)) && r.Len <= wagons && (mine.Contains(r.A) || mine.Contains(r.B)))
                .OrderBy(r => mine.Contains(r.A) && mine.Contains(r.B) ? 0 : 1).ThenBy(r => r.Len).ThenBy(_ => rng.Next()).FirstOrDefault();
            if (next is null) return taken;
            taken.Add(next.Id);
            wagons -= next.Len;
            mine.Add(next.A);
            mine.Add(next.B);
        }
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Longest_road_on_the_densest_networks_of_45_wagons_takes_at_most_five_milliseconds()
    {
        var rng = new Random(11);
        for (var i = 0; i < 100; i++) With(0, [.. Dense(i % Cities.Length, rng)]).LongestRoad(0);   // прогрів JIT
        var times = new List<double>();
        var most = 0;
        for (var i = 0; i < 450; i++)
        {
            var routes = Dense(i % Cities.Length, rng);
            var c = With(0, [.. routes]);
            most = Math.Max(most, routes.Count);
            // Найкраще з трьох: міряємо обхід, а не чужі процеси на машині.
            var ms = double.MaxValue;
            for (var k = 0; k < 3; k++)
            {
                var sw = CpuWatch.StartNew();
                Assert.True(c.LongestRoad(0) > 0);
                sw.Stop();
                ms = Math.Min(ms, sw.Elapsed.TotalMilliseconds);
            }
            times.Add(ms);
        }
        times.Sort();
        var p95 = times[(int)(times.Count * 0.95)];
        output.WriteLine($"найгустіша мережа {most} шляхів, 95 % ≤ {p95:F2} мс, найгірше {times[^1]:F2} мс");
        // 45 шляхів гравець мати не може (§14 про це — помилка): 45 возів — це ≤ 28 шляхів, а зв'язна густа мережа — близько 21.
        Assert.True(p95 <= 5, $"95 % ≤ {p95:F1} мс");
        Assert.True(times[^1] <= 25, $"найгірше {times[^1]:F1} мс");   // запас на зайняту машину
    }

    // =====================================================================================
    // Кінець партії (§2.6, §2.7)
    // =====================================================================================

    [Fact]
    public void Two_wagons_at_the_end_of_a_turn_give_everyone_exactly_one_more_turn()
    {
        var h = SlRig.Play(3);
        var c = SlRig.CoreOf(h);
        var s = c.Turn;
        var second = c.Next(s);
        var third = c.Next(second);
        c.Wagons[s] = 3;
        SlRig.Give(c, s, (Wild, 1));
        Assert.True(h.Act(s, "claim", new { route = 2, c = "wild", w = 1 }).Ok);
        Assert.True(c.Last);
        Assert.Equal(3, c.Left);
        Assert.Equal(s, c.LastBy);
        var v = h.View(second);
        Assert.True(v.GetProperty("last").GetBoolean());
        Assert.Equal(3, v.GetProperty("left").GetInt32());
        Assert.Contains($"У {h.NickOf(s)} лишилось 2 вози — кожен має ще один хід", c.Log);
        var last = c.Events.Single(e => e.K == "last");
        Assert.Equal(s, last["seat"]);
        Assert.Equal(2, last["wagons"]);
        // Другий гравець теж на ≤ 2 — нічого нового не запускає.
        c.Wagons[second] = 3;
        SlRig.Give(c, second, (Wild, 1));
        Assert.True(h.Act(second, "claim", new { route = 9, c = "wild", w = 1 }).Ok);
        Assert.Equal(2, c.Left);
        Assert.Single(c.Events, e => e.K == "last");
        Assert.True(h.Act(third, "draw", new { deck = true }).Ok);
        Assert.True(h.Act(third, "draw", new { deck = true }).Ok);
        Assert.Equal(1, c.Left);
        Assert.Equal(s, c.Turn);
        Assert.False(c.Over);
        Assert.True(h.Act(s, "draw", new { deck = true }).Ok);
        Assert.True(h.Act(s, "draw", new { deck = true }).Ok);
        Assert.True(c.Over);
        Assert.Equal("wagons", c.Reason);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var over = h.View(0).GetProperty("over");
        Assert.Equal(s, over.GetProperty("lastBy").GetInt32());
        Assert.Equal("wagons", over.GetProperty("reason").GetString());
        // Подія кінця: longest, потім over.
        var keys = c.Events.Select(e => e.K).ToArray();
        Assert.Equal("over", keys[^1]);
        Assert.Equal("longest", keys[^2]);
    }

    [Fact]
    public void Pass_is_legal_only_with_nothing_to_do_and_all_passing_is_a_stall()
    {
        var h = SlRig.Play(2);
        var c = SlRig.CoreOf(h);
        var s = c.Turn;
        Assert.Equal("Пропускати не можна — є що робити", h.Act(s, "pass").Message);
        c.Market = [-1, -1, -1, -1, -1];
        c.Deck = [];
        c.Discard = [];
        foreach (var x in c.Order) c.Hand[x] = new int[9];
        c.TicketDeck.Clear();
        c.Tickets[s].Clear();
        var legal = c.Legal(s);
        Assert.True(legal.Pass);
        Assert.False(legal.Draw);
        Assert.Equal("Карт ніде нема", legal.Why["draw"]);
        Assert.Equal("Замовлень більше нема", legal.Why["tickets"]);
        Assert.Equal("Жодного шляху зараз не потягнеш", legal.Why["claim"]);
        Assert.True(h.Act(s, "pass").Ok);
        Assert.Contains($"{h.NickOf(s)} пропускає хід — нема що робити", c.Log);
        Assert.False(c.Over);
        Assert.True(h.Act(c.Turn, "pass").Ok);
        Assert.True(c.Over);
        Assert.Equal("stall", c.Reason);
        Assert.Equal(-1, c.LastBy);
        Assert.Single(h.Finished);
    }

    [Fact]
    public void A_non_pass_action_resets_the_stall_counter()
    {
        var c = SolyanyiBotTests.Playing(3, 5);
        c.Market = [-1, -1, -1, -1, -1];
        c.Deck = [];
        c.Discard = [];
        foreach (var x in c.Order) c.Hand[x] = new int[9];
        c.TicketDeck.Clear();
        var s = c.Turn;
        Assert.Null(c.Pass(s));
        Assert.Equal(1, c.Passes);
        var o = c.Turn;
        c.Hand[o][Wild] = 1;
        Assert.Null(c.Claim(o, 2, "wild", 1));
        Assert.Equal(0, c.Passes);
    }

    [Fact]
    public void Scoring_counts_routes_tickets_bonus_and_can_be_negative()
    {
        var c = SolyanyiBotTests.Playing(2, 5);
        var (a, b) = (c.Order[0], c.Order[1]);
        foreach (var x in c.Order) c.Tickets[x].Clear();
        c.Tickets[a].AddRange([0, 38]);   // 26 і 4
        c.Done[38] = true;
        c.RoutePts[a] = 5;
        c.Tickets[b].Add(39);
        c.Done[39] = true;
        c.Road[b] = 3;
        c.Taken[b].Add(0);
        SlRig.Stall(c);
        Assert.Equal(5 + 4 - 26, c.Total(a));
        Assert.Equal(-17, c.Total(a));
        Assert.Equal(0 + 4 + 10, c.Total(b));
        Assert.Equal([b], c.Winners);
    }

    [Fact]
    public void Ties_go_to_more_done_tickets_then_the_longest_road_then_shared()
    {
        // Більше виконаних.
        var c = SolyanyiBotTests.Playing(2, 5);
        var (a, b) = (c.Order[0], c.Order[1]);
        foreach (var x in c.Order) c.Tickets[x].Clear();
        c.Tickets[a].AddRange([38, 39]);
        c.Done[38] = c.Done[39] = true;    // 4 + 4
        c.Tickets[b].Add(28);
        c.Done[28] = true;                 // 7
        c.RoutePts[b] = 1;
        SlRig.Stall(c);
        Assert.Equal(c.Total(a), c.Total(b));
        Assert.Equal([a], c.Winners);

        // Далі — Валка (бонус входить у total, тож рівні total із різними Валками).
        c = SolyanyiBotTests.Playing(2, 5);
        (a, b) = (c.Order[0], c.Order[1]);
        foreach (var x in c.Order) c.Tickets[x].Clear();
        c.Road[a] = 5;
        c.Taken[a].Add(72);
        c.RoutePts[a] = 3;
        c.RoutePts[b] = 13;
        SlRig.Stall(c);
        Assert.Equal(13, c.Total(a));
        Assert.Equal(13, c.Total(b));
        Assert.Equal([a], c.Winners);

        // Усе рівно — спільна.
        c = SolyanyiBotTests.Playing(3, 5);
        foreach (var x in c.Order) c.Tickets[x].Clear();
        SlRig.Stall(c);
        Assert.Equal(c.Order, c.Winners);
    }

    [Fact]
    public void Shared_win_and_negative_scores_reach_the_framework()
    {
        var h = SlRig.Play(2);
        var c = SlRig.CoreOf(h);
        foreach (var x in c.Order) c.Tickets[x].Clear();
        c.Tickets[0].Add(38);
        c.Tickets[1].Add(39);
        c.Market = [-1, -1, -1, -1, -1];
        c.Deck = [];
        c.Discard = [];
        foreach (var x in c.Order) c.Hand[x] = new int[9];
        c.TicketDeck.Clear();
        Assert.True(h.Act(c.Turn, "pass").Ok);
        Assert.True(h.Act(c.Turn, "pass").Ok);
        var f = Assert.Single(h.Finished);
        Assert.Equal(-4, c.Total(0));
        var line = h.Outbox.OfType<Journal>().Last().Text;
        Assert.Contains("Соляний шлях:", line);
        Assert.Contains("— -4 ⭐", line);
        Assert.Equal(new long[] { -4, -4 }, new[] { f.Result.Scores![0], f.Result.Scores![1] });
    }

    [Fact]
    public void Wagons_option_gives_thirty_five()
    {
        var h = SlRig.Play(2, wagons: "35");
        var c = SlRig.CoreOf(h);
        Assert.All(c.Order, s => Assert.Equal(35, c.Wagons[s]));
        Assert.Equal(35, h.View(0).GetProperty("wagons").GetInt32());
        Assert.Equal(35, h.View(0).GetProperty("players")[0].GetProperty("wagons").GetInt32());
    }

    [Fact]
    public void Info_and_can_start_follow_the_contract()
    {
        var g = new Solyanyi();
        Assert.Equal("solyanyi", g.Info.Id);
        Assert.Equal((GameGroup.Board, 1, 5, StartMode.ByHost, true, false, ScoreOrder.None, 0),
            (g.Info.Group, g.Info.MinPlayers, g.Info.MaxPlayers, g.Info.Start, g.Info.Hidden, g.Info.Rated, g.Info.Score, g.Info.TickMs));
        Assert.Equal(["bots", "wagons", "timer"], g.Info.Options!.Select(o => o.Key));
        Assert.Equal(["0", "1", "2", "3", "4"], g.Info.Options![0].Values.Select(v => v.Item1));
        Assert.Equal("45", g.Info.Options![1].Default);
        Assert.Equal("60", g.Info.Options![2].Default);
        Assert.Equal(["червоні", "сині", "зелені", "помаранчеві", "фіолетові"], Enumerable.Range(0, 5).Select(g.SeatName));

        var h = new RoomHarness("solyanyi", new { timer = "60", bots = "0", wagons = "45" }, seed: 1);
        h.Join("Оля");
        Assert.False(h.Start().Ok);
        Assert.Equal("Самому не доїхати: хай хтось сяде — або додай 🤖 Глека", h.Reply.Message);
        h.Join("Петро");
        Assert.True(h.Start().Ok);
    }

    [Fact]
    public void Bot_seat_cannot_be_played_by_people_and_outsiders_cannot_act()
    {
        var h = SlRig.Play(1, bots: "1");
        var g = SlRig.GameOf(h);
        var payload = JsonDocument.Parse("""{"deck":true}""").RootElement;
        lock (h.Room.Sync)
        {
            Assert.Equal("Тут грає Глек", g.Act(1, "draw", payload).Message);
            Assert.Equal("Ти не граєш за цим столом", g.Act(4, "draw", payload).Message);
            Assert.Equal("Ти не граєш за цим столом", g.Act(9, "draw", payload).Message);
        }
    }

    [Fact]
    public void Finished_game_refuses_everything_at_the_core()
    {
        var c = SolyanyiBotTests.Playing(2, 5);
        SlRig.Stall(c);
        Assert.Equal("Партія вже скінчилась", c.Draw(c.Order[0], null, true));
        Assert.Equal("Партія вже скінчилась", c.Claim(c.Order[0], 0, "salt", 0));
        Assert.Equal("Партія вже скінчилась", c.TakeTickets(c.Order[0]));
        Assert.Equal("Партія вже скінчилась", c.Keep(c.Order[0], [0]));
        Assert.Equal("Партія вже скінчилась", c.Pass(c.Order[0]));
    }

    [Fact]
    public void Achievements_go_to_people_at_the_table_when_earned()
    {
        var h = SlRig.Play(3, seed: 5);
        var c = SlRig.CoreOf(h);
        foreach (var x in c.Order)
        {
            c.Tickets[x].Clear();
            c.Hand[x] = new int[9];
        }
        var s = c.Order[0];
        // Два замовлення з Криму, 5 і більше — усі виконані, шлях на 6, дорога найдовша, 0 возів — перемога за столом на 3.
        c.Tickets[s].AddRange([8, 14, 19, 26, 27]);
        foreach (var t in c.Tickets[s]) c.Done[t] = true;
        c.Crimea[s] = 5;
        c.Biggest[s] = 6;
        c.Wagons[s] = 0;
        c.Road[s] = 6;
        c.Owner[37] = s;
        c.Taken[s].Add(37);
        c.RoutePts[s] = 15;
        c.Market = [-1, -1, -1, -1, -1];
        c.Deck = [];
        c.Discard = [];
        c.TicketDeck.Clear();
        foreach (var _ in c.Order) Assert.True(h.Act(c.Turn, "pass").Ok);
        Assert.True(c.Over);
        var got = h.Awards.Where(a => a.Reason.StartsWith("ach:sol-")).Select(a => a.Reason).Order().ToArray();
        Assert.Equal(["ach:sol-clean", "ach:sol-dry", "ach:sol-perekop", "ach:sol-six", "ach:sol-valka"], got);
        Assert.All(h.Awards.Where(a => a.Reason.StartsWith("ach:sol-")), a => Assert.Equal(h.NickOf(s), a.Nick));
    }

    [Fact]
    public void A_whole_game_with_people_following_tips_and_bots_finishes_with_rematch()
    {
        var h = SlRig.Play(2, bots: "1", seed: 8);
        var c = SlRig.CoreOf(h);
        for (var i = 0; i < 1500 && h.Finished.Count == 0; i++) SlRig.Step(h, 0);
        Assert.Single(h.Finished);
        Assert.True(c.Over);
        var seq = c.Seq;
        Assert.True(h.Rematch().Ok, h.Reply.Message);
        var d = SlRig.CoreOf(h);
        Assert.NotSame(c, d);
        Assert.Equal(SlPhase.Setup, d.Phase);
        Assert.True(d.Seq > seq);   // seq наскрізь
        Assert.Equal(0, d.Order.Sum(s => d.RoutePts[s]));
    }
}

/// <summary>Спільні для тестів «Соляного шляху» заготовки столів і підкручування стану.</summary>
internal static class SlRig
{
    public static readonly string[] Crew = ["Оля", "Петро", "Іра", "Влад", "Марко"];

    /// <summary>Стіл із людьми й Глеками, фаза setup.</summary>
    public static RoomHarness Table(int people, string bots = "0", string timer = "60", string wagons = "45", int seed = 5)
    {
        var h = new RoomHarness("solyanyi", new { timer, bots, wagons }, seed: seed);
        for (var i = 0; i < people; i++) h.Join(Crew[i]);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    /// <summary>Стіл, де всі люди вже обрали замовлення: фаза play.</summary>
    public static RoomHarness Play(int people, string bots = "0", string timer = "60", string wagons = "45", int seed = 5)
    {
        var h = Table(people, bots, timer, wagons, seed);
        Ready(h);
        return h;
    }

    public static void Ready(RoomHarness h)
    {
        var c = CoreOf(h);
        foreach (var s in c.Order)
            if (!c.Ready[s]) Assert.True(h.Act(s, "keep", new { ids = c.Offer[s].Take(2).ToArray() }).Ok);
    }

    public static Solyanyi GameOf(RoomHarness h) => (Solyanyi)h.Room.Game;
    public static SolyanyiCore CoreOf(RoomHarness h) => GameOf(h).Core;

    /// <summary>Знімок стану ядра для «помилка нічого не міняє».</summary>
    public static string State(SolyanyiCore c) => JsonSerializer.Serialize(c.Snapshot());

    /// <summary>Рука рівно така: стара — назад у колоду, нова — із запасу. 110 карт лишаються на місцях.</summary>
    public static void Give(SolyanyiCore c, int s, params (int Card, int N)[] cards)
    {
        for (var k = 0; k < 9; k++)
        {
            c.Deck.AddRange(Enumerable.Repeat(k, c.Hand[s][k]));
            c.Hand[s][k] = 0;
        }
        foreach (var (card, n) in cards)
            for (var i = 0; i < n; i++)
            {
                Take(c, card);
                c.Hand[s][card]++;
            }
    }

    /// <summary>Вийняти карту виду з колоди, скиду, ринку або чужої руки (щоб лічба сходилась).</summary>
    static void Take(SolyanyiCore c, int card)
    {
        if (c.Deck.Remove(card) || c.Discard.Remove(card)) return;
        for (var k = 0; k < 5; k++)
            if (c.Market[k] == card)
            {
                c.Market[k] = c.Deck[0];
                c.Deck.RemoveAt(0);
                return;
            }
        foreach (var o in c.Order)
            if (c.Hand[o][card] > 0)
            {
                c.Hand[o][card]--;
                c.Hand[o][c.Deck[0]]++;
                c.Deck.RemoveAt(0);
                return;
            }
        throw new InvalidOperationException($"нема карти {card}");
    }

    /// <summary>Ринок рівно такий; колишні карти — у колоду.</summary>
    public static void Market(SolyanyiCore c, params int[] kinds)
    {
        for (var k = 0; k < 5; k++)
        {
            if (c.Market[k] >= 0) c.Deck.Add(c.Market[k]);
            c.Market[k] = -1;
        }
        for (var k = 0; k < 5; k++)
        {
            if (kinds[k] < 0) continue;
            Take(c, kinds[k]);
            c.Market[k] = kinds[k];
        }
    }

    /// <summary>Такі карти — згори колоди, у цьому порядку.</summary>
    public static void Top(SolyanyiCore c, params int[] kinds)
    {
        foreach (var k in kinds) Take(c, k);
        c.Deck.InsertRange(0, kinds);
    }

    /// <summary>Довести партію до кінця <c>stall</c>: забрати всі карти й замовлення-колоду, усі пропускають.</summary>
    public static void Stall(SolyanyiCore c)
    {
        c.Market = [-1, -1, -1, -1, -1];
        c.Deck = [];
        c.Discard = [];
        c.TicketDeck.Clear();
        foreach (var x in c.Order) c.Hand[x] = new int[9];
        foreach (var _ in c.Order) Assert.Null(c.Pass(c.Turn));
        Assert.True(c.Over);
    }

    /// <summary>Один крок столу від місця <paramref name="by"/>: штовхнути Глека; нема на кого — людина, чий хід, грає пораду.</summary>
    public static void Step(RoomHarness h, int by)
    {
        var v = h.View(by);
        if (v.GetProperty("phase").GetString() == "setup")
        {
            Ready(h);
            return;
        }
        if (v.GetProperty("botIn").ValueKind == JsonValueKind.Number)
        {
            h.Clock.AdvanceMs(Math.Max(BoardBots.ThinkMs, v.GetProperty("botIn").GetInt32()));
            var r = h.Act(by, BoardBots.Nudge);
            Assert.True(r.Ok, r.Message);
            return;
        }
        var turn = v.GetProperty("turn").GetInt32();
        var res = PlayTip(h, turn, h.View(turn).GetProperty("me").GetProperty("tip"));
        Assert.True(res.Ok, res.Message);
    }

    /// <summary>Зіграти «💡 Глек радить» як дію людини (тим самим payload, що шле клієнт).</summary>
    public static ActResult PlayTip(RoomHarness h, int seat, JsonElement tip)
    {
        var act = tip.GetProperty("act").GetString()!;
        return act switch
        {
            "claim" => h.Act(seat, "claim", new { route = tip.GetProperty("route").GetInt32(), c = tip.GetProperty("c").GetString(), w = tip.GetProperty("w").GetInt32() }),
            "draw" when tip.TryGetProperty("slot", out var slot) => h.Act(seat, "draw", new { slot = slot.GetInt32() }),
            "draw" => h.Act(seat, "draw", new { deck = true }),
            "tickets" => h.Act(seat, "tickets", new { }),
            "keep" => h.Act(seat, "keep", new { ids = tip.GetProperty("ids").EnumerateArray().Select(x => x.GetInt32()).ToArray() }),
            _ => h.Act(seat, "pass", new { }),
        };
    }
}
