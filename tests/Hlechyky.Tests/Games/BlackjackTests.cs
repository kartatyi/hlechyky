using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>«Двадцять одно в Глека» за столом і сам на сам (specs/blackjack.md §9): дії, фази, гроші, перезапуск, вид, Глек, ачівки.</summary>
public class BlackjackTests
{
    const string Bets = BlackjackGame.Bets, Play = BlackjackGame.Play, Glek = BlackjackGame.GlekTurn, Result = BlackjackGame.Result,
        Idle = BlackjackGame.Idle;

    static JsonElement BoxOf(JsonElement view, string nick) =>
        view.GetProperty("boxes").EnumerateArray().First(b => b.GetProperty("nick").GetString() == nick);

    static string[] CardsOf(JsonElement hand) => [.. hand.GetProperty("cards").EnumerateArray().Select(c => c.GetString()!)];

    /// <summary>Один гравець за столом поставив і сказав «Роздавай!» — роздано одразу.</summary>
    static BlackjackKit Dealt(int bet = 100, int wallet = 1000, params string[] cards)
    {
        var k = BlackjackKit.Table(("Оля", wallet));
        k.Rig(cards);
        Assert.True(k.Bet(0, bet).Ok, k.H.Reply.Message);
        k.DealNow(0);
        return k;
    }

    // ---------- ставки ----------

    [Fact]
    public void A_bet_is_an_intention_until_the_deal()
    {
        var k = BlackjackKit.Table(("Оля", 1000));
        Assert.True(k.Bet(0, 50).Ok);
        Assert.True(k.Bet(0, 25).Ok);
        var v = k.View(0);
        Assert.Equal(75, BoxOf(v, "Оля").GetProperty("bet").GetInt32());
        Assert.Equal(75, v.GetProperty("me").GetProperty("bet").GetInt32());
        Assert.Equal(925, v.GetProperty("me").GetProperty("free").GetInt32());
        Assert.Empty(k.Spends);
        Assert.True(k.Do(0, "clear").Ok);
        Assert.Equal(0, k.View(0).GetProperty("boxes").GetArrayLength());
    }

    [Fact]
    public void Bet_errors_have_exact_texts()
    {
        var closed = new RoomHarness("blackjack", seed: 7);
        closed.Join("Оля");
        Assert.Equal(BlackjackGame.ClosedText, closed.Act(0, "bet", new { amount = 10 }).Message);
        Assert.True(closed.View(0).GetProperty("closed").GetBoolean());

        var k = BlackjackKit.Table(("Оля", 1000));
        var before = Views.Text(k.View(0));
        void Refused(string text, string action, object? payload = null)
        {
            var r = k.H.Act(0, action, payload);
            Assert.False(r.Ok);
            Assert.Equal(text, r.Message);
            Assert.Equal(before, Views.Text(k.View(0)));
        }
        Refused("Тут так не ходять", "dance");
        Refused("Ставка — ціле число черепків, від 1", "bet", new { amount = 0 });
        Refused("Ставка — ціле число черепків, від 1", "bet", new { amount = "50" });
        Refused("Ставка — ціле число черепків, від 1", "bet", new { amount = 2.5 });
        Refused("Ставка — ціле число черепків, від 1", "bet", 50);
        Refused("Найменша ставка — 10 🏺", "bet", new { amount = 5 });
        Refused("Найбільша ставка — 2000 🏺", "bet", new { amount = 2001 });
        Refused("Бракує черепків: у гаманці 1000", "bet", new { amount = 1001 });
        Refused("Нема чого знімати", "clear");
        Refused("Минулої роздачі ставки не було", "rebet");
        Refused("Спершу постав ставку", "deal");
        Refused("Зараз не час ходити — спершу ставка", "hit");

        // фази
        var p = Dealt(100, 1000, "Th", "Tc", "9h", "7d");
        Assert.Equal(Play, p.S.Phase);
        Assert.Equal("Роздача йде — ставки на наступну", p.Bet(0, 10).Message);
        Assert.Equal("Подвоїти можна лише на перших двох картах, коли в тебе 9, 10 чи 11", p.Do(0, "double").Message);
        Assert.Equal("Розбити можна лише пару однакових карт", p.Do(0, "split").Message);
        p.Do(0, "stand");
        Assert.Equal(Glek, p.S.Phase);
        Assert.Equal("Глек уже грає свою руку", p.Do(0, "hit").Message);
        p.To(Result);
        Assert.Equal("Глек рахує — ставки за мить", p.Bet(0, 10).Message);
    }

    [Fact]
    public void Max_bet_zero_means_only_the_wallet_limits()
    {
        var k = new BlackjackKit(opts: new BlackjackOptions { MaxBet = 0 }, people: [("Оля", 50_000)]);
        Assert.True(k.Bet(0, 50_000).Ok);
        Assert.Equal("Бракує черепків: у гаманці 50000", k.Bet(0, 1).Message);
    }

    // ---------- роздача й гроші ----------

    [Fact]
    public void Deal_debits_once_with_the_round_ref_and_shows_two_cards_each()
    {
        var k = Dealt(100, 1000, "Th", "Tc", "9h", "7d");
        Assert.Equal(Play, k.S.Phase);
        var bet = k.BetRef("b", "Оля");
        Assert.Equal([$"spend:Оля:100:{bet}"], k.Spends);
        Assert.Equal("blackjack-bet:blackjack", k.Stakes.Reasons[bet]);
        var pending = Assert.Single(k.Book.Pending());
        Assert.False(pending.Final);
        Assert.Equal(k.TableKey, pending.Table);

        var v = k.View(0);
        Assert.Equal(0, v.GetProperty("turn").GetInt32());
        var dealer = v.GetProperty("dealer").GetProperty("cards");
        Assert.Equal("Tc", dealer[0].GetString());
        Assert.Equal(JsonValueKind.Null, dealer[1].ValueKind);
        var hand = BoxOf(v, "Оля").GetProperty("hands")[0];
        Assert.Equal(["Th", "9h"], CardsOf(hand));
        Assert.Equal(19, hand.GetProperty("total").GetInt32());
        Assert.True(hand.GetProperty("active").GetBoolean());
        var actions = v.GetProperty("me").GetProperty("actions");
        Assert.True(actions.GetProperty("hit").GetBoolean());
        Assert.False(actions.GetProperty("double").GetBoolean());
        Assert.Equal(900, v.GetProperty("me").GetProperty("wallet").GetInt32());
    }

    [Fact]
    public void Hole_card_is_hidden_from_everyone_until_glek_turns_it()
    {
        var k = BlackjackKit.Table(("Оля", 1000), ("Петро", 1000));
        k.Rig("Th", "9s", "Tc", "8h", "9h", "7d");
        k.Bet(0, 50);
        k.Bet(1, 50);
        k.DealNow(0, 1);
        foreach (int? seat in new int?[] { 0, 1, null })
            Assert.DoesNotContain("\"7d\"", Views.Text(k.View(seat)));
        Assert.Equal(JsonValueKind.Null, k.View(null).GetProperty("me").ValueKind);
        k.Do(0, "stand");
        k.Do(1, "stand");
        Assert.Equal(Glek, k.S.Phase);
        Assert.Contains("\"7d\"", Views.Text(k.View(null)));
    }

    [Fact]
    public void Win_pays_double_but_only_when_glek_has_shown_his_cards()
    {
        var k = Dealt(100, 1000, "Th", "Tc", "9h", "7d");
        Assert.True(k.Do(0, "stand").Ok);
        Assert.Equal(Glek, k.S.Phase);
        Assert.Empty(k.Grants);
        // у фазі glek підсумків на дроті ще нема — карти Глека відкриваються по одній
        Assert.Equal(JsonValueKind.Null, BoxOf(k.View(0), "Оля").GetProperty("hands")[0].GetProperty("outcome").ValueKind);
        Assert.True(Assert.Single(k.Book.Pending()).Final);
        k.To(Result);
        var pay = k.PayRef("Оля");
        Assert.Equal([$"grant:Оля:200:{pay}"], k.Grants);
        Assert.Equal("blackjack-pay:blackjack", k.Stakes.Reasons[pay]);
        Assert.Equal(1100, k.Stakes.Balance("Оля"));
        Assert.Empty(k.Book.Pending());
        var last = k.View(0).GetProperty("last");
        Assert.Equal(100, last.GetProperty("results")[0].GetProperty("net").GetInt32());
        Assert.Equal("win", BoxOf(k.View(0), "Оля").GetProperty("hands")[0].GetProperty("outcome").GetString());
        Assert.Equal(1100, k.Me(0).GetProperty("wallet").GetInt32());
    }

    [Fact]
    public void Double_is_a_second_debit_and_pays_four_times()
    {
        var k = Dealt(100, 1000, "6h", "6c", "5d", "Ts", "9h", "8c");
        Assert.True(k.Me(0).GetProperty("actions").GetProperty("double").GetBoolean());
        Assert.True(k.Do(0, "double").Ok);
        var d = k.BetRef("d", "Оля");
        Assert.Equal($"spend:Оля:100:{d}", k.Spends[1]);
        Assert.Equal("blackjack-double:blackjack", k.Stakes.Reasons[d]);
        k.To(Result);
        Assert.Equal([$"grant:Оля:400:{k.PayRef("Оля")}"], k.Grants);
        Assert.Equal(1200, k.Stakes.Balance("Оля"));
    }

    [Fact]
    public void Double_without_money_is_refused_and_nothing_moves()
    {
        var k = Dealt(100, 150, "6h", "6c", "5d", "Ts");
        var before = Views.Text(k.View(0));
        Assert.Equal("Бракує черепків: треба ще 100, у гаманці 50", k.Do(0, "double").Message);
        Assert.Single(k.Spends);
        Assert.Equal(before, Views.Text(k.View(0)));
    }

    [Fact]
    public void Split_twice_makes_three_hands_each_with_its_own_debit()
    {
        var k = Dealt(100, 1000, "8h", "9c", "8d", "9d", "8s", "Th", "3c", "8c", "9s");
        Assert.True(k.Do(0, "split").Ok);   // 8h 8s · 8d Th
        Assert.True(k.Do(0, "split").Ok);   // 8h 3c · 8s 8c · 8d Th
        Assert.Equal("Подвоїти можна лише на перших двох картах, коли в тебе 9, 10 чи 11", k.Do(0, "double").Message);   // 11 після спліту
        Assert.True(k.Do(0, "hit").Ok);     // 8h 3c 9s = 20
        Assert.True(k.Do(0, "stand").Ok);
        Assert.Equal("Більше трьох рук не буває", k.Do(0, "split").Message);
        Assert.True(k.Do(0, "stand").Ok);   // 16
        Assert.True(k.Do(0, "stand").Ok);   // 18
        Assert.Equal(
            [$"spend:Оля:100:{k.BetRef("b", "Оля")}", $"spend:Оля:100:{k.BetRef("s1", "Оля")}", $"spend:Оля:100:{k.BetRef("s2", "Оля")}"],
            k.Spends);
        Assert.Equal("blackjack-split:blackjack", k.Stakes.Reasons[k.BetRef("s2", "Оля")]);
        k.To(Result);   // Глек 18: 20 — виграш, 16 — програш, 18 — нічия
        var hands = BoxOf(k.View(0), "Оля").GetProperty("hands");
        Assert.Equal(["win", "lose", "push"], hands.EnumerateArray().Select(h => h.GetProperty("outcome").GetString()));
        Assert.Equal([$"grant:Оля:300:{k.PayRef("Оля")}"], k.Grants);
        Assert.Equal(1000, k.Stakes.Balance("Оля"));
    }

    [Fact]
    public void Glek_peeks_blackjack_and_the_round_ends_at_once()
    {
        var k = Dealt(100, 1000, "Th", "As", "9h", "Kd");
        Assert.Equal(Glek, k.S.Phase);
        var v = k.View(0);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("turn").ValueKind);
        Assert.True(v.GetProperty("dealer").GetProperty("bj").GetBoolean());
        k.To(Result);
        Assert.Empty(k.Grants);
        Assert.Equal("bj", k.S.Glek.Mood);
        Assert.Equal(900, k.Stakes.Balance("Оля"));
    }

    [Fact]
    public void Blackjack_on_blackjack_goes_to_glek()
    {
        var k = Dealt(100, 1000, "Ah", "As", "Kd", "Kc");
        k.To(Result);
        Assert.Empty(k.Grants);
        Assert.Equal(BlackjackLines.BjOnBj, k.S.Glek.Say);
    }

    [Fact]
    public void Player_blackjack_pays_one_to_one_and_glek_draws_nothing()
    {
        var k = Dealt(100, 1000, "Ah", "9c", "Kd", "7s");
        Assert.Equal(Glek, k.S.Phase);
        k.To(Result);
        Assert.Equal(["9c", "7s"], k.S.Deal!.Dealer);
        Assert.Equal([$"grant:Оля:200:{k.PayRef("Оля")}"], k.Grants);
        Assert.Contains("ach:blackjack-natural", k.Achievements("Оля"));
    }

    [Fact]
    public void Push_returns_the_stake_and_bust_returns_nothing()
    {
        var push = Dealt(100, 1000, "Th", "Tc", "8h", "8d");
        push.Do(0, "stand");
        push.To(Result);
        Assert.Equal([$"grant:Оля:100:{push.PayRef("Оля")}"], push.Grants);

        var bust = Dealt(100, 1000, "Th", "Tc", "6h", "7d", "9s");
        bust.Do(0, "hit");
        bust.To(Result);
        Assert.Empty(bust.Grants);
        Assert.Equal(["Tc", "7d"], bust.S.Deal!.Dealer);   // усі перебрали — Глек не добирає
    }

    // ---------- черга й таймер ----------

    [Fact]
    public void Each_decision_has_15_seconds_then_the_hand_stands()
    {
        var k = Dealt(100, 1000, "Th", "Tc", "6h", "7d");
        Assert.Equal(15_000, k.View(0).GetProperty("phaseMs").GetInt32());
        k.H.Tick(59);
        Assert.Equal(Play, k.S.Phase);
        k.H.Tick();
        Assert.Equal(Glek, k.S.Phase);
        Assert.Contains("задумується", k.S.Glek.Say);
        Assert.Equal(["Th", "6h"], k.S.Deal!.Boxes[0].Hands[0].Cards);
    }

    [Fact]
    public void Every_action_restarts_the_decision_clock()
    {
        var k = Dealt(100, 1000, "2h", "Tc", "3h", "7d", "2c", "2d");
        k.H.Tick(50);
        Assert.True(k.Do(0, "hit").Ok);
        k.H.Tick(50);
        Assert.Equal(Play, k.S.Phase);
        Assert.True(k.Do(0, "hit").Ok);
        Assert.Equal(Play, k.S.Phase);
    }

    [Fact]
    public void Turns_go_in_seat_order_and_others_wait()
    {
        var k = BlackjackKit.Table(("Оля", 1000), ("Петро", 1000));
        k.Rig("Th", "9s", "Tc", "8h", "9h", "7d");
        k.Bet(0, 50);
        k.Bet(1, 50);
        k.DealNow(0, 1);
        Assert.Equal(0, k.View(1).GetProperty("turn").GetInt32());
        Assert.Equal(JsonValueKind.Null, k.Me(1).GetProperty("actions").ValueKind);
        Assert.Equal("Чекай свого ходу — зараз ходить Оля", k.Do(1, "hit").Message);
        Assert.True(k.Do(0, "stand").Ok);
        Assert.Equal(1, k.View(0).GetProperty("turn").GetInt32());
        Assert.True(k.Do(1, "stand").Ok);
        k.To(Result);
        Assert.Equal(2, k.Grants.Count);
    }

    [Fact]
    public void Deal_comes_early_when_everyone_seated_is_ready()
    {
        var k = BlackjackKit.Table(("Оля", 1000), ("Петро", 1000));
        k.Bet(0, 50);
        k.DealNow(0);
        k.H.Tick();
        Assert.Equal(Bets, k.S.Phase);   // Петро ще не ставив
        k.Bet(1, 50);
        k.DealNow(1);
        Assert.NotEqual(Bets, k.S.Phase);
        Assert.Equal(2, k.Spends.Count);
    }

    [Fact]
    public void Window_closes_after_15_seconds_with_whoever_bet()
    {
        var k = BlackjackKit.Table(("Оля", 1000), ("Петро", 1000));
        k.Rig("Th", "Tc", "9h", "7d");
        k.Bet(0, 50);
        Assert.Equal(15_000, k.View(0).GetProperty("phaseMs").GetInt32());
        k.H.Tick(59);
        Assert.Equal(Bets, k.S.Phase);
        Assert.Equal("hurry", k.S.Glek.Mood);
        k.H.Tick();
        Assert.Equal(Play, k.S.Phase);
        Assert.Single(k.S.Deal!.Boxes);
        Assert.Equal("Карти вже роздаються — чекай наступної роздачі", Assert.IsType<string>(BetLate()));

        string? BetLate()
        {
            var late = BlackjackKit.Table(("Оля", 1000));
            late.H.Clock.AdvanceMs(15_000);   // дедлайн минув, а тика ще не було
            return late.Bet(0, 50).Message;
        }
    }

    [Fact]
    public void Empty_windows_sigh_then_doze_and_a_bet_wakes_the_table()
    {
        var k = BlackjackKit.Table(("Оля", 1000));
        k.H.Tick(61);
        Assert.Equal("sigh", k.S.Glek.Mood);
        Assert.Equal(Bets, k.S.Phase);
        k.H.Tick(120);
        Assert.Equal(Idle, k.S.Phase);
        Assert.Equal("doze", k.S.Glek.Mood);
        Assert.True(k.Bet(0, 10).Ok);
        Assert.Equal(Bets, k.S.Phase);
        Assert.Equal(15_000, k.View(0).GetProperty("leftMs").GetInt32());
    }

    [Fact]
    public void Leaving_mid_hand_stands_and_still_pays()
    {
        var k = BlackjackKit.Table(("Оля", 1000), ("Петро", 1000));
        k.Rig("Th", "9s", "Tc", "9h", "8h", "7d");
        k.Bet(0, 50);
        k.Bet(1, 50);
        k.DealNow(0, 1);
        k.H.Leave("Оля");
        k.H.Tick();
        Assert.Equal(1, k.View(1).GetProperty("turn").GetInt32());
        var box = BoxOf(k.View(1), "Оля");
        Assert.False(box.GetProperty("here").GetBoolean());
        Assert.True(k.H.Act(1, "stand").Ok);   // місця не зсуваються: Петро — на першому
        k.To(Result);
        Assert.Contains($"grant:Оля:100:{k.PayRef("Оля")}", k.Grants);
        Assert.DoesNotContain("ach:blackjack", string.Join(",", k.H.Awards.Where(a => a.Nick == "Оля").Select(a => a.Reason)));
    }

    [Fact]
    public void Leaving_during_bets_drops_the_bet()
    {
        var k = BlackjackKit.Table(("Оля", 1000), ("Петро", 1000));
        k.Bet(0, 50);
        k.Bet(1, 50);
        k.H.Leave("Оля");
        Assert.Single(k.S.Bets);
        k.Rig("Th", "Tc", "9h", "7d");
        k.To(Play);
        Assert.Single(k.Spends);
    }

    [Fact]
    public void Late_join_can_bet_next_round()
    {
        var k = Dealt(100, 1000, "Th", "Tc", "9h", "7d");
        k.Stakes.Set("Петро", 500);
        Assert.True(k.H.Join("Петро").Ok);
        Assert.Equal("Роздача йде — ставки на наступну", k.H.Act(1, "bet", new { amount = 10 }).Message);
        k.Do(0, "stand");
        k.To(Bets);
        Assert.True(k.H.Act(1, "bet", new { amount = 10 }).Ok);
        Assert.Equal(500, k.Me(1).GetProperty("wallet").GetInt32());
    }

    // ---------- каса й перезапуск ----------

    [Fact]
    public void Settle_and_recover_pay_once()
    {
        var k = Dealt(100, 1000, "Th", "Tc", "9h", "7d");
        k.Do(0, "stand");
        var fin = Assert.Single(k.Book.Pending());
        Assert.Equal(1, k.Book.Recover(DateTimeOffset.MaxValue));   // падіння після підсумків: сирота платить підсумок
        Assert.Equal([$"grant:Оля:200:{k.PayRef("Оля")}"], k.Grants);
        k.To(Result);   // гра розраховує ту саму роздачу — ключ не дає заплатити вдруге
        k.Book.Settle(fin);
        Assert.Single(k.Grants);
        Assert.Equal(1100, k.Stakes.Balance("Оля"));
    }

    [Fact]
    public void Crash_mid_hand_refunds_every_debit_once()
    {
        var k = Dealt(100, 1000, "8h", "9c", "8d", "9d", "2s", "3s");
        k.Do(0, "split");
        Assert.Equal(2, k.Spends.Count);
        Assert.Equal(800, k.Stakes.Balance("Оля"));
        Assert.Equal(1, k.Book.Recover(DateTimeOffset.MaxValue));
        var back = k.BackRef("Оля");
        Assert.Equal([$"grant:Оля:200:{back}"], k.Grants);
        Assert.Equal("blackjack-back:blackjack", k.Stakes.Reasons[back]);
        Assert.Equal(0, k.Book.Recover(DateTimeOffset.MaxValue));
        Assert.Equal(1000, k.Stakes.Balance("Оля"));
    }

    [Fact]
    public void Recover_skips_players_who_never_paid_and_refunds_a_wrong_sum()
    {
        var stakes = new FakeStakes().Set("Оля", 1000).Set("Петро", 1000);
        var book = new BlackjackBook(stakes, new FakeStore(), defer: a => a());
        // запис є, а списання нема (впало між записом і списанням) — нікому нічого
        book.Open(new BjRound("r:e", 1, "blackjack", DateTimeOffset.UnixEpoch, false, [new BjPay("Оля", 100, null)]));
        // підсумок каже 100, а списано 50 — запис зіпсовано: повертаємо списане
        book.Open(new BjRound("r:e", 2, "blackjack", DateTimeOffset.UnixEpoch, true, [new BjPay("Петро", 100, 200)]));
        stakes.TrySpend("Петро", 50, "blackjack-bet:blackjack", BlackjackBook.BetRef("r:e", 2, "b", "Петро"));
        Assert.Equal(2, book.Recover(DateTimeOffset.MaxValue));
        Assert.Equal(["grant:Петро:50:blackjack-back:r:e:2:петро"], stakes.Calls.Where(c => c.StartsWith("grant:")));
        Assert.Empty(book.Pending());
    }

    [Fact]
    public void Nick_with_a_colon_is_not_confused_with_a_step()
    {
        var stakes = new FakeStakes().Set("a", 1000).Set("b:a", 1000);
        var book = new BlackjackBook(stakes, new FakeStore(), defer: a => a());
        book.Open(new BjRound("r:e", 1, "blackjack", DateTimeOffset.UnixEpoch, false, [new BjPay("a", 10, null), new BjPay("b:a", 10, null)]));
        stakes.TrySpend("a", 10, "x", BlackjackBook.BetRef("r:e", 1, "b", "a"));
        stakes.TrySpend("b:a", 10, "x", BlackjackBook.BetRef("r:e", 1, "b", "b:a"));
        book.Recover(DateTimeOffset.MaxValue);
        Assert.Equal(1000, stakes.Balance("a"));
        Assert.Equal(1000, stakes.Balance("b:a"));
    }

    [Fact]
    public void Store_failure_skips_the_deal_without_debits()
    {
        var k = new BlackjackKit(store: new BrokenStore(), people: [("Оля", 1000)]);
        k.Bet(0, 50);
        k.DealNow(0);
        Assert.Equal(Bets, k.S.Phase);
        Assert.Empty(k.Spends);
        Assert.Equal(BlackjackLines.BookStuck, k.S.Glek.Say);
        Assert.Equal(50, k.Me(0).GetProperty("bet").GetInt32());   // ставка лишилась на сукні
    }

    [Fact]
    public void Debit_failure_at_deal_drops_that_box_with_a_note()
    {
        var k = BlackjackKit.Table(("Оля", 1000), ("Петро", 1000));
        k.Rig("Th", "Tc", "9h", "7d");
        k.Bet(0, 50);
        k.Bet(1, 50);
        k.DealNow(1);
        k.Stakes.Set("Петро", 10);   // гаманець спорожнів деінде, поки Оля думала
        k.DealNow(0);
        Assert.Equal(Play, k.S.Phase);
        Assert.Single(k.S.Deal!.Boxes);
        Assert.Equal("Черепків не стало — твою ставку (50) знято", k.Me(1).GetProperty("note").GetString());
    }

    [Fact]
    public void Resume_mid_hand_continues_with_the_old_round_keys_and_pays_once()
    {
        var k = Dealt(100, 1000, "6h", "6c", "5d", "Ts", "9h", "8c");
        var table = k.TableKey;
        var saved = k.G.Save()!;
        lock (k.H.Room.Sync)
        {
            k.G.Start();
            k.G.Load(saved);
            k.G.Resumed(TimeSpan.FromSeconds(30));
        }
        Assert.NotEqual(table, k.TableKey);   // новий epoch
        Assert.Equal(Play, k.S.Phase);
        Assert.True(k.G.Holds(table, k.S.RoundNo));
        Assert.True(k.Do(0, "double").Ok);
        Assert.Contains($"spend:Оля:100:blackjack-bet:{table}:{k.S.RoundNo}:d:оля", k.Spends);
        k.To(Result);
        Assert.Equal([$"grant:Оля:400:blackjack-pay:{table}:{k.S.RoundNo}:оля"], k.Grants);
    }

    [Fact]
    public void Live_table_round_is_left_alone_by_the_sweep()
    {
        var stakes = new FakeStakes().Set("Оля", 1000);
        BlackjackGame? game = null;
        var book = new BlackjackBook(stakes, new FakeStore(), defer: a => a(), held: (t, r) => game?.Holds(t, r) == true);
        var h = new RoomHarness("blackjack", services: RoomHarness.WithService(book), seed: 3);
        h.Join("Оля");
        game = (BlackjackGame)h.Room.Game;
        game.Rig = ["Th", "Tc", "9h", "7d"];
        h.Act(0, "bet", new { amount = 100 });
        h.Act(0, "deal", new { });
        Assert.Equal(0, book.Recover(DateTimeOffset.MaxValue));
        Assert.Single(book.Pending());
        h.Act(0, "stand");
        for (var i = 0; i < 40 && game.State.Phase != Result; i++) h.Tick();
        Assert.Single(stakes.Calls, c => c.StartsWith("grant:"));
    }

    [Fact]
    public void Resume_of_a_round_the_book_already_refunded_reopens_bets_with_a_note()
    {
        var k = Dealt(100, 1000, "Th", "Tc", "9h", "7d");
        var saved = k.G.Save()!;
        k.Book.Recover(DateTimeOffset.MaxValue);   // старий процес упав, каса повернула ставку
        lock (k.H.Room.Sync)
        {
            k.G.Start();
            k.G.Load(saved);
            k.G.Resumed(TimeSpan.FromSeconds(30));
        }
        Assert.Equal(Bets, k.S.Phase);
        Assert.Null(k.S.Deal);
        Assert.Equal("Роздачу перервав перезапуск — ставку (100) повернуто", k.Me(0).GetProperty("note").GetString());
        Assert.Equal(1000, k.Stakes.Balance("Оля"));
        Assert.Single(k.Grants);
    }

    [Fact]
    public void Shared_table_is_resumable_and_not_busy()
    {
        var k = Dealt(100, 1000, "Th", "Tc", "9h", "7d");
        Assert.True(k.G.Resumable);
        Assert.Empty(k.H.Rooms.Busy());
    }

    // ---------- соло ----------

    [Fact]
    public void Solo_deal_plays_and_pays_after_glek_shows()
    {
        var k = BlackjackKit.Solo();
        Assert.Equal(Bets, k.S.Phase);
        Assert.Equal(JsonValueKind.Null, k.View(0).GetProperty("leftMs").ValueKind);
        k.Rig("Th", "Tc", "9h", "7d");
        Assert.True(k.H.Act(0, "deal", new { amount = 100 }).Ok);
        Assert.Equal(Play, k.S.Phase);
        Assert.Equal(0, k.View(0).GetProperty("turn").GetInt32());
        k.H.Tick(400);
        Assert.Equal(Play, k.S.Phase);   // у соло таймера ходу нема
        Assert.True(k.Do(0, "stand").Ok);
        k.To(Bets);
        Assert.Equal([$"grant:Оля:200:{k.PayRef("Оля")}"], k.Grants);
        var v = k.View(0);
        Assert.Equal(100, v.GetProperty("last").GetProperty("results")[0].GetProperty("net").GetInt32());
        Assert.Equal("win", BoxOf(v, "Оля").GetProperty("hands")[0].GetProperty("outcome").GetString());   // остання рука лишається на сукні
        Assert.Equal(100, v.GetProperty("me").GetProperty("rebet").GetInt32());
        // «Роздати» без суми — ставка минулої роздачі
        k.Rig("Th", "Tc", "9h", "7d");
        Assert.True(k.H.Act(0, "deal", new { }).Ok);
        Assert.Equal(Play, k.S.Phase);
        Assert.Equal(100, k.S.Deal!.Boxes[0].Bet);
    }

    [Fact]
    public void Solo_refusals_say_why()
    {
        var k = BlackjackKit.Solo(wallet: 50);
        Assert.Equal("Спершу постав ставку", k.H.Act(0, "deal", new { }).Message);
        Assert.Equal("Бракує черепків: у гаманці 50", k.H.Act(0, "deal", new { amount = 100 }).Message);
        Assert.Equal("Ставка — ціле число черепків, від 1", k.H.Act(0, "deal", new { amount = "x" }).Message);
        Assert.Empty(k.Spends);
        Assert.True(k.H.Act(0, "deal", new { amount = 50 }).Ok);
        Assert.Equal("Роздача йде — спершу дограй руку", k.Bet(0, 10).Message);
    }

    [Fact]
    public void Solo_reopen_of_a_settled_round_is_quiet()
    {
        var k = BlackjackKit.Solo(wallet: 10_000);
        k.Rig("Th", "Tc", "9h", "7d");
        k.H.Act(0, "deal", new { amount = 1000 });
        k.Do(0, "stand");
        var saved = k.H.Store.States["blackjack-solo:оля"];   // каркас зберіг після дії — у фазі glek
        Assert.Contains("\"phase\":\"glek\"", saved);
        k.To(Bets);
        var scores = k.H.Scores.Count;
        var awards = k.H.Awards.Count;
        for (var i = 0; i < 2; i++)
        {
            lock (k.H.Room.Sync)
            {
                k.G.Start();
                k.G.Load(saved);
            }
            Assert.Equal(Bets, k.S.Phase);
            Assert.Equal(1000, k.View(0).GetProperty("last").GetProperty("results")[0].GetProperty("net").GetInt32());
        }
        Assert.Single(k.Grants);
        Assert.Equal(scores, k.H.Scores.Count);
        Assert.Equal(awards, k.H.Awards.Count);
    }

    [Fact]
    public void Solo_reopen_after_a_crash_mid_hand_tells_the_stake_came_back()
    {
        var k = BlackjackKit.Solo();
        k.Rig("Th", "Tc", "9h", "7d");
        k.H.Act(0, "deal", new { amount = 100 });
        var saved = k.H.Store.States["blackjack-solo:оля"];
        k.Book.Recover(DateTimeOffset.MaxValue);
        lock (k.H.Room.Sync)
        {
            k.G.Start();
            k.G.Load(saved);
        }
        Assert.Equal(Bets, k.S.Phase);
        Assert.Equal("Роздачу перервав перезапуск — ставку (100) повернуто", k.Me(0).GetProperty("note").GetString());
        Assert.Equal(1000, k.Stakes.Balance("Оля"));
    }

    [Fact]
    public void Solo_reopen_mid_hand_while_the_book_holds_plays_on()
    {
        var k = BlackjackKit.Solo();
        k.Rig("Th", "Tc", "9h", "7d");
        k.H.Act(0, "deal", new { amount = 100 });
        var saved = k.H.Store.States["blackjack-solo:оля"];
        lock (k.H.Room.Sync)
        {
            k.G.Start();
            k.G.Load(saved);
        }
        Assert.Equal(Play, k.S.Phase);
        Assert.True(k.Do(0, "stand").Ok);
        k.To(Bets);
        Assert.Single(k.Grants);
    }

    // ---------- вид, Глек, ачівки, таблиця ----------

    [Fact]
    public void View_shape_on_the_wire()
    {
        var k = Dealt(100, 1000, "Th", "Tc", "6h", "7d");
        var v = k.View(0);
        foreach (var f in new[] { "mode", "phase", "until", "leftMs", "phaseMs", "turn", "round", "hash", "limits", "on", "closed",
                     "dealer", "boxes", "last", "records", "glek", "me" })
            Assert.True(Views.Has(v, f), f);
        foreach (var f in new[] { "wallet", "bet", "ready", "free", "staked", "canBet", "canDeal", "canRebet", "rebet", "actions", "hint",
                     "streak", "note" })
            Assert.True(Views.Has(v.GetProperty("me"), f), f);
        Assert.Equal("table", v.GetProperty("mode").GetString());
        Assert.Matches("^[0-9a-f]{64}$", v.GetProperty("hash").GetString()!);
        Assert.Equal(10, v.GetProperty("limits").GetProperty("min").GetInt32());
        Assert.True(v.GetProperty("on").GetBoolean());
        Assert.True(v.GetProperty("boxes")[0].GetProperty("mine").GetBoolean());
        Assert.True(k.View(null).GetProperty("boxes")[0].TryGetProperty("mine", out var mine) && !mine.GetBoolean());
    }

    [Fact]
    public void Hint_shows_the_basic_strategy_move_on_my_turn()
    {
        var k = Dealt(100, 1000, "Th", "Tc", "6h", "7d");
        var hint = k.Me(0).GetProperty("hint");
        Assert.Equal("hit", hint.GetProperty("move").GetString());
        Assert.Equal("Бери карту", hint.GetProperty("text").GetString());
        var dbl = Dealt(100, 1000, "6h", "6c", "5d", "Ts");
        Assert.Equal("double", dbl.Me(0).GetProperty("hint").GetProperty("move").GetString());
    }

    [Fact]
    public void Seed_hash_comes_before_the_bets_and_the_seed_after_the_round()
    {
        var k = BlackjackKit.Table(("Оля", 1000));
        var hash = k.View(0).GetProperty("hash").GetString()!;
        k.Bet(0, 50);
        k.DealNow(0);
        if (k.S.Phase == Play) k.Do(0, "stand");
        k.To(Result);
        var last = k.View(0).GetProperty("last");
        var seed = last.GetProperty("seed").GetString()!;
        Assert.Equal(hash, last.GetProperty("hash").GetString());
        Assert.Equal(hash, BlackjackCore.Hash(seed));
        var drawn = last.GetProperty("drawn").EnumerateArray().Select(c => c.GetString()!).ToArray();
        Assert.True(drawn.Length >= 4);
        Assert.Equal(BlackjackCore.Shoe(seed).Take(drawn.Length), drawn);
        k.To(Bets);
        Assert.NotEqual(hash, k.View(0).GetProperty("hash").GetString());
    }

    [Fact]
    public void Five_card_21_streaks_score_and_split_wins_are_rewarded()
    {
        var k = BlackjackKit.Table(("Оля", 10_000));
        // 2+3+4+5+7 = 21 з п'яти карт; у Глека 17
        k.Rig("2h", "Tc", "3d", "7s", "4c", "5d", "7h");
        k.Bet(0, 100);
        k.DealNow(0);
        k.Do(0, "hit");
        k.Do(0, "hit");
        k.Do(0, "hit");
        k.To(Result);
        Assert.Contains("ach:blackjack-five", k.Achievements("Оля"));
        Assert.Equal(100, k.H.Scores.Last().Score);
        Assert.Equal(1, k.View(0).GetProperty("records").GetProperty("fives").GetInt32());
        Assert.Equal("dance", k.S.Glek.Mood);
        for (var i = 0; i < 4; i++)
        {
            k.To(Bets);
            k.Rig("Th", "Tc", "9h", "7d");
            k.H.Act(0, "rebet");
            k.DealNow(0);
            k.Do(0, "stand");
            k.To(Result);
        }
        Assert.Contains("ach:blackjack-streak5", k.Achievements("Оля"));
        Assert.Equal(5, k.View(0).GetProperty("records").GetProperty("streak").GetProperty("n").GetInt32());

        k.To(Bets);
        k.Rig("8h", "6c", "8d", "Td", "8s", "Th", "3c", "9c", "Tc", "9d", "9s");
        k.H.Act(0, "rebet");
        k.DealNow(0);
        k.Do(0, "split");   // 8h 8s · 8d Th
        k.Do(0, "split");   // 8h 3c · 8s 9c · 8d Th
        k.Do(0, "hit");     // 8h 3c Tc = 21
        k.Do(0, "stand");   // 17
        k.Do(0, "stand");   // 18; Глек 16 + 9 = 25
        k.To(Result);
        Assert.Contains("ach:blackjack-split3", k.Achievements("Оля"));
    }

    [Fact]
    public void Disabled_game_refuses_new_bets_but_finishes_the_round()
    {
        var opts = new BlackjackOptions();
        var k = new BlackjackKit(opts: opts, people: [("Оля", 1000)]);
        k.Rig("Th", "Tc", "9h", "7d");
        k.Bet(0, 100);
        k.DealNow(0);
        opts.Enabled = false;
        Assert.True(k.Do(0, "stand").Ok);
        k.To(Result);
        Assert.Single(k.Grants);
        k.To(Bets);
        Assert.Equal(BlackjackGame.OffText, k.Bet(0, 10).Message);
        Assert.False(k.View(0).GetProperty("on").GetBoolean());
        Assert.False(k.Me(0).GetProperty("canBet").GetBoolean());
        var fresh = new RoomHarness("blackjack", services: RoomHarness.WithService(k.Book), seed: 2);
        var r = fresh.Join("Петро");
        Assert.False(r.Ok);
        Assert.Equal(BlackjackGame.OffText, r.Message);
    }

    [Fact]
    public void Deterministic_with_the_same_seed()
    {
        string Play()
        {
            var k = BlackjackKit.Table(("Оля", 1000));
            k.Bet(0, 50);
            k.DealNow(0);
            while (k.S.Phase == BlackjackGame.Play) k.Do(0, "stand");
            k.To(Result);
            return Views.Text(k.View(0));
        }
        Assert.Equal(Play(), Play());
    }

    [Fact, Trait("Category", "Perf")]
    public void Thousand_ticks_with_five_players_under_a_second()
    {
        var people = Enumerable.Range(0, 5).Select(i => ($"Гравець{i}", 1_000_000)).ToArray();
        var k = BlackjackKit.Table(people);
        var sw = Stopwatch.StartNew();
        for (var t = 0; t < 1000; t++)
        {
            if (k.S.Phase == Bets && k.S.Bets.Count == 0)
                for (var s = 0; s < 5; s++) k.Bet(s, 100);
            k.H.Tick();
        }
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1000, $"{sw.ElapsedMilliseconds} мс");
        Assert.True(k.S.RoundNo >= 2);
    }
}
