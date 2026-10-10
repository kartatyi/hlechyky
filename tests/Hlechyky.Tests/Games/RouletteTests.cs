using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Рулетка за столом і сам на сам (specs/roulette.md §9): дії, фази, гроші, перезапуск, вид, Глек, ачівки.</summary>
public class RouletteTests
{
    const string Spin = RouletteGame.Spinning, Bets = RouletteGame.Bets, Result = RouletteGame.Result, Idle = RouletteGame.Idle;

    static JsonElement PlayerOf(JsonElement view, string nick) =>
        view.GetProperty("players").EnumerateArray().First(p => p.GetProperty("nick").GetString() == nick);

    static bool HasPlayer(JsonElement view, string nick) =>
        view.GetProperty("players").EnumerateArray().Any(p => p.GetProperty("nick").GetString() == nick);

    // ---------- дії й перевірки ----------

    [Fact]
    public void A_bet_is_placed_and_shown_on_the_table()
    {
        var k = RouletteKit.Table(("Оля", 1000));
        Assert.True(k.Bet(0, "straight:17", 25).Ok);
        var v = k.View(0);
        var me = PlayerOf(v, "Оля");
        Assert.Equal(25, me.GetProperty("total").GetInt32());
        Assert.Equal("straight:17", me.GetProperty("bets")[0].GetProperty("spot").GetString());
        Assert.Equal(25, me.GetProperty("bets")[0].GetProperty("amount").GetInt32());
        Assert.True(me.GetProperty("mine").GetBoolean());
        Assert.Equal(25, v.GetProperty("onTable").GetInt32());
        Assert.Equal(1000, v.GetProperty("me").GetProperty("wallet").GetInt32());
        Assert.Equal(975, v.GetProperty("me").GetProperty("free").GetInt32());
        Assert.Empty(k.Spends);   // фішки на полі — лише намір

        // Act у реалтаймі видів не шле — вид летить із найближчого тика
        var before = k.H.Outbox.OfType<RoomViews>().Count();
        k.H.Tick();
        Assert.Equal(before + 1, k.H.Outbox.OfType<RoomViews>().Count());
        k.H.Tick();
        Assert.Equal(before + 1, k.H.Outbox.OfType<RoomViews>().Count());
    }

    [Fact]
    public void Bets_on_the_same_spot_add_up()
    {
        var k = RouletteKit.Table(("Оля", 1000));
        k.Bet(0, "red", 5);
        k.Bet(0, "straight:3", 1);
        k.Bet(0, "red", 5);
        var bets = PlayerOf(k.View(0), "Оля").GetProperty("bets");
        Assert.Equal(2, bets.GetArrayLength());
        Assert.Equal("red", bets[0].GetProperty("spot").GetString());
        Assert.Equal(10, bets[0].GetProperty("amount").GetInt32());
    }

    [Fact]
    public void Bet_errors_have_exact_texts()
    {
        var closed = new RoomHarness("roulette", seed: 7);
        closed.Join("Оля");
        Assert.Equal("Каса зачинена — спробуй трохи згодом", closed.Act(0, "bet", new { spot = "red", amount = 1 }).Message);
        Assert.True(closed.View(0).GetProperty("closed").GetBoolean());

        var k = RouletteKit.Table(("Оля", 1000));
        k.Bet(0, "red", 10);
        var before = Views.Text(k.View(0));
        void Refused(string text, string action, object? payload = null)
        {
            var r = k.H.Act(0, action, payload);
            Assert.False(r.Ok);
            Assert.Equal(text, r.Message);
            Assert.Equal(before, Views.Text(k.View(0)));
        }
        Refused("Тут так не ходять", "dance");
        Refused("Тут колесо крутить Глек — за розкладом", "spin");
        Refused("Не зрозумів ставки", "bet", 5);
        Refused("Не зрозумів ставки", "bet", new { amount = 5 });
        Refused("Не зрозумів ставки", "bet", new { type = "split", numbers = "17-20", amount = 5 });
        Refused("Такої ставки на полі нема", "bet", new { spot = "split:3-4", amount = 5 });
        Refused("Такої ставки на полі нема", "bet", new { type = "corner", numbers = new[] { 3, 4, 6, 7 }, amount = 5 });
        Refused("Ставка — ціле число черепків, від 1", "bet", new { spot = "red" });
        Refused("Ставка — ціле число черепків, від 1", "bet", new { spot = "red", amount = 0 });
        Refused("Ставка — ціле число черепків, від 1", "bet", new { spot = "red", amount = 2.5 });
        Refused("Ставка — ціле число черепків, від 1", "bet", new { spot = "red", amount = "5" });
        Refused("Бракує черепків: вільних 990", "bet", new { spot = "black", amount = 991 });
        Refused("Минулого кола ставок не було — нема чого повторювати", "repeat");

        var fresh = RouletteKit.Table(("Оля", 1000));
        Assert.Equal("Нема чого знімати", fresh.H.Act(0, "undo").Message);
        Assert.Equal("Нема чого знімати", fresh.H.Act(0, "clear").Message);
        Assert.Equal("Нема чого подвоювати", fresh.H.Act(0, "double").Message);

        // фази
        k.Rig(17);
        k.To(Spin);
        Assert.Equal("Ставки зроблено — чекай наступного кола", k.Bet(0, "red", 1).Message);
        Assert.Equal("Ставки зроблено — чекай наступного кола", k.H.Act(0, "undo").Message);
        k.To(Result);
        Assert.Equal("Глек рахує виграші — ставки за мить", k.Bet(0, "red", 1).Message);
        Assert.Equal("Глек рахує виграші — ставки за мить", k.H.Act(0, "repeat").Message);
    }

    [Fact]
    public void No_limit_you_can_bet_the_whole_wallet()
    {
        var k = RouletteKit.Table(("Оля", 1000));
        var r = k.Bet(0, "red", 1001);
        Assert.False(r.Ok);
        Assert.Equal("Бракує черепків: вільних 1000", r.Message);
        Assert.True(k.Bet(0, "red", 1000).Ok);
        Assert.Equal(0, k.Me(0).GetProperty("free").GetInt32());
        Assert.Equal("Бракує черепків: вільних 0", k.Bet(0, "black", 1).Message);
    }

    [Fact]
    public void Free_balance_counts_bets_already_on_the_table()
    {
        var k = RouletteKit.Table(("Оля", 1000));
        Assert.True(k.Bet(0, "red", 600).Ok);
        Assert.Equal("Бракує черепків: вільних 400", k.Bet(0, "straight:5", 500).Message);
        Assert.True(k.Bet(0, "straight:5", 400).Ok);
        Assert.Equal(0, k.Me(0).GetProperty("free").GetInt32());
        // гаманець змінився деінде — наступна дія читає його наново
        k.Stakes.Set("Оля", 1500);
        Assert.True(k.Bet(0, "black", 500).Ok);
        Assert.Equal(1500, k.Me(0).GetProperty("wallet").GetInt32());
    }

    [Fact]
    public void Undo_removes_the_last_step_and_repeat_is_one_step()
    {
        var k = RouletteKit.Table(("Оля", 1000));
        k.Bet(0, "red", 10);
        k.Bet(0, "straight:17", 5);
        k.Round(2);
        Assert.True(k.Bet(0, "black", 3).Ok);
        Assert.True(k.H.Act(0, "repeat").Ok);
        Assert.Equal(18, PlayerOf(k.View(0), "Оля").GetProperty("total").GetInt32());
        Assert.True(k.Me(0).GetProperty("canUndo").GetBoolean());
        Assert.True(k.H.Act(0, "undo").Ok);   // увесь «Повторити» одним кроком
        var bets = PlayerOf(k.View(0), "Оля").GetProperty("bets");
        Assert.Equal(1, bets.GetArrayLength());
        Assert.Equal("black", bets[0].GetProperty("spot").GetString());
        k.Bet(0, "black", 4);
        Assert.True(k.H.Act(0, "undo").Ok);   // знімає лише четвірку, трійка лишається
        Assert.Equal(3, PlayerOf(k.View(0), "Оля").GetProperty("total").GetInt32());
        Assert.True(k.H.Act(0, "undo").Ok);
        Assert.Equal(0, PlayerOf(k.View(0), "Оля").GetProperty("bets").GetArrayLength());
        Assert.Equal("Нема чого знімати", k.H.Act(0, "undo").Message);
        Assert.False(k.Me(0).GetProperty("canUndo").GetBoolean());
    }

    [Fact]
    public void Clear_removes_all_my_bets_and_nobody_elses()
    {
        var k = RouletteKit.Table(("Оля", 1000), ("Петро", 1000));
        k.Bet(0, "red", 10);
        k.Bet(0, "dozen:2", 10);
        k.Bet(1, "black", 7);
        Assert.True(k.H.Act(0, "clear").Ok);
        var v = k.View(1);
        Assert.Equal(0, PlayerOf(v, "Оля").GetProperty("total").GetInt32());
        Assert.Equal(7, PlayerOf(v, "Петро").GetProperty("total").GetInt32());
        Assert.Equal(7, v.GetProperty("onTable").GetInt32());
        Assert.Equal("Нема чого знімати", k.H.Act(0, "clear").Message);
    }

    [Fact]
    public void Repeat_places_last_paid_round_bets_and_checks_balance()
    {
        var k = RouletteKit.Table(("Оля", 1000));
        k.Bet(0, "red", 300);
        k.Bet(0, "straight:17", 100);
        k.Round(2);   // чорне: −400
        Assert.Equal(600, k.Stakes.Balance("Оля"));
        Assert.Equal(400, k.Me(0).GetProperty("repeatCost").GetInt32());
        Assert.True(k.Me(0).GetProperty("canRepeat").GetBoolean());
        Assert.True(k.Bet(0, "black", 250).Ok);
        Assert.False(k.Me(0).GetProperty("canRepeat").GetBoolean());
        Assert.Equal("Бракує черепків на повтор: треба 400, вільних 350", k.H.Act(0, "repeat").Message);
        k.H.Act(0, "clear");
        Assert.True(k.H.Act(0, "repeat").Ok);
        var bets = PlayerOf(k.View(0), "Оля").GetProperty("bets");
        Assert.Equal(["red", "straight:17"], bets.EnumerateArray().Select(b => b.GetProperty("spot").GetString()!));
        Assert.Equal(400, PlayerOf(k.View(0), "Оля").GetProperty("total").GetInt32());
    }

    [Fact]
    public void Double_adds_the_same_again_and_checks_balance()
    {
        var k = RouletteKit.Table(("Оля", 1000));
        k.Bet(0, "red", 100);
        k.Bet(0, "split:17-20", 50);
        Assert.True(k.H.Act(0, "double").Ok);
        var bets = PlayerOf(k.View(0), "Оля").GetProperty("bets");
        Assert.Equal(200, bets[0].GetProperty("amount").GetInt32());
        Assert.Equal(100, bets[1].GetProperty("amount").GetInt32());
        Assert.True(k.Me(0).GetProperty("canDouble").GetBoolean());
        Assert.True(k.H.Act(0, "double").Ok);   // 600 на столі
        Assert.False(k.Me(0).GetProperty("canDouble").GetBoolean());
        Assert.Equal("Бракує черепків, щоб подвоїти: треба ще 600, вільних 400", k.H.Act(0, "double").Message);
        Assert.True(k.H.Act(0, "undo").Ok);
        Assert.Equal(300, PlayerOf(k.View(0), "Оля").GetProperty("total").GetInt32());
    }

    [Fact]
    public void Sixty_spots_per_player_is_the_cap()
    {
        var k = RouletteKit.Table(("Оля", 10_000));
        for (var n = 0; n <= 36; n++) Assert.True(k.Bet(0, $"straight:{n}", 1).Ok);
        foreach (var spot in RouletteCore.All.Where(s => s.StartsWith("split:", StringComparison.Ordinal)).Take(23))
            Assert.True(k.Bet(0, spot, 1).Ok);
        Assert.Equal(60, PlayerOf(k.View(0), "Оля").GetProperty("bets").GetArrayLength());
        Assert.Equal("Досить — у тебе вже 60 ставок на полі", k.Bet(0, "red", 1).Message);
        Assert.True(k.Bet(0, "straight:17", 5).Ok);   // на вже зайняте поле — можна
        Assert.True(k.H.Act(0, "double").Ok);
    }

    [Fact]
    public void Spectator_cannot_bet()
    {
        var k = RouletteKit.Table(("Оля", 1000));
        k.Stakes.Set("Глядач", 1000);
        var r = k.H.Rooms.Act(k.H.RoomId, "Глядач", "bet", Views.Payload(new { spot = "red", amount = 5 }));
        Assert.False(r.Reply.Ok);
        Assert.Equal("Ти тут не граєш", r.Reply.Message);
        var v = k.View(null);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("me").ValueKind);
    }

    [Fact]
    public void Spin_is_refused_at_the_shared_table()
    {
        var k = RouletteKit.Table(("Оля", 1000));
        k.Bet(0, "red", 5);
        Assert.Equal("Тут колесо крутить Глек — за розкладом", k.H.Act(0, "spin", new { }).Message);
        Assert.Equal(Bets, k.S.Phase);
    }

    [Fact]
    public void Payload_shapes_spot_and_type_are_the_same_bet()
    {
        var a = RouletteKit.Table(("Оля", 1000));
        var b = RouletteKit.Table(("Оля", 1000));
        Assert.True(a.H.Act(0, "bet", new { spot = "split:17-20", amount = 25 }).Ok);
        Assert.True(b.H.Act(0, "bet", new { type = "split", numbers = new[] { 20, 17 }, amount = 25 }).Ok);
        Assert.True(a.H.Act(0, "bet", new { spot = "dozen:2", amount = 5 }).Ok);
        Assert.True(b.H.Act(0, "bet", new { type = "dozen", target = 2, amount = 5 }).Ok);
        Assert.True(a.H.Act(0, "bet", new { spot = "red", amount = 5 }).Ok);
        Assert.True(b.H.Act(0, "bet", new { type = "red", amount = 5 }).Ok);
        Assert.Equal(Views.Text(a.View(0)), Views.Text(b.View(0)));
    }

    // ---------- фази спільного столу ----------

    [Fact]
    public void Table_starts_in_bets_with_a_25_second_window()
    {
        var k = RouletteKit.Table(("Оля", 1000));
        Assert.Equal(RoomStatus.Playing, k.H.Room.Status);
        var v = k.View(0);
        Assert.Equal("table", v.GetProperty("mode").GetString());
        Assert.Equal("bets", v.GetProperty("phase").GetString());
        Assert.Equal(25_000, v.GetProperty("leftMs").GetInt32());
        Assert.Equal(25_000, v.GetProperty("phaseMs").GetInt32());
        Assert.Equal(k.H.Clock.UtcNow.AddSeconds(25), v.GetProperty("until").GetDateTimeOffset());
        Assert.Equal("idle", v.GetProperty("glek").GetProperty("mood").GetString());
    }

    [Fact]
    public void Bets_close_at_the_deadline_spin_lasts_6s_result_5s_then_new_bets()
    {
        var k = RouletteKit.Table(("Оля", 1000));
        k.Rig(17);
        k.Bet(0, "red", 10);
        k.H.Tick(99);
        Assert.Equal(Bets, k.S.Phase);
        k.H.Tick();
        Assert.Equal(Spin, k.S.Phase);
        var v = k.View(0);
        Assert.Equal(6000, v.GetProperty("spin").GetProperty("leftMs").GetInt32());
        Assert.Equal(6000, v.GetProperty("spin").GetProperty("ms").GetInt32());
        Assert.Equal(6000, v.GetProperty("phaseMs").GetInt32());
        Assert.Equal("call", v.GetProperty("glek").GetProperty("mood").GetString());
        k.H.Tick(23);
        Assert.Equal(Spin, k.S.Phase);
        k.H.Tick();
        Assert.Equal(Result, k.S.Phase);
        v = k.View(0);
        Assert.Equal(0, v.GetProperty("spin").GetProperty("leftMs").GetInt32());
        Assert.Equal(5000, v.GetProperty("phaseMs").GetInt32());
        k.H.Tick(19);
        Assert.Equal(Result, k.S.Phase);
        k.H.Tick();
        Assert.Equal(Bets, k.S.Phase);
        v = k.View(0);
        Assert.Equal(25_000, v.GetProperty("leftMs").GetInt32());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("spin").ValueKind);
        Assert.Equal(0, v.GetProperty("onTable").GetInt32());   // поле чисте
        Assert.Equal(17, v.GetProperty("history")[0].GetProperty("n").GetInt32());
    }

    [Fact]
    public void A_bet_after_the_deadline_before_the_tick_is_refused()
    {
        var k = RouletteKit.Table(("Оля", 1000));
        k.H.Clock.AdvanceMs(25_000);
        Assert.Equal("Ставки зроблено — чекай наступного кола", k.Bet(0, "red", 5).Message);
        k.H.Clock.AdvanceMs(-1);
        Assert.True(k.Bet(0, "red", 5).Ok);
    }

    [Fact]
    public void Empty_window_sighs_and_three_empty_windows_doze()
    {
        var k = RouletteKit.Table(("Оля", 1000));
        int Said() => k.H.Outbox.OfType<TableSaid>().Count();
        var said = Said();
        k.H.Tick(100);
        Assert.Equal(Bets, k.S.Phase);
        Assert.Equal("sigh", k.S.Glek.Mood);
        Assert.Contains(k.S.Glek.Say, RouletteLines.Sigh);
        Assert.Equal(said + 1, Said());
        Assert.Equal(25_000, k.View(0).GetProperty("leftMs").GetInt32());
        Assert.Empty(k.S.History);   // колесо без ставок не крутиться
        k.H.Tick(100);
        Assert.Equal(Bets, k.S.Phase);
        Assert.Equal(said + 1, Said());   // балачка — лише перше порожнє коло
        k.H.Tick(100);
        Assert.Equal(Idle, k.S.Phase);
        Assert.Equal("doze", k.S.Glek.Mood);
        var v = k.View(0);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("until").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("leftMs").ValueKind);
        k.H.Tick(400);
        Assert.Equal(Idle, k.S.Phase);
        Assert.Empty(k.Spends);
    }

    [Fact]
    public void First_bet_wakes_an_idle_table_with_a_full_window()
    {
        var k = RouletteKit.Table(("Оля", 1000));
        k.To(Idle);
        k.H.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(k.Bet(0, "red", 5).Ok);
        Assert.Equal(Bets, k.S.Phase);
        Assert.Equal(25_000, k.View(0).GetProperty("leftMs").GetInt32());
        Assert.Equal("idle", k.S.Glek.Mood);
        Assert.Equal(0, k.S.EmptyRounds);
        k.Rig(1);
        k.To(Spin);
        Assert.Single(k.Spends);
    }

    [Fact]
    public void Hurry_cue_comes_once_in_the_last_5_seconds()
    {
        var k = RouletteKit.Table(("Оля", 1000));
        k.Bet(0, "red", 5);
        k.H.Tick(79);
        Assert.NotEqual("hurry", k.S.Glek.Mood);
        var seq = k.S.Glek.Seq;
        k.H.Tick();
        Assert.Equal("hurry", k.S.Glek.Mood);
        Assert.Contains(k.S.Glek.Say, RouletteLines.Hurry);
        Assert.Equal(seq + 1, k.S.Glek.Seq);
        k.H.Tick(19);
        Assert.Equal(seq + 1, k.S.Glek.Seq);
        Assert.Equal(Bets, k.S.Phase);

        // без ставок підказки нема
        var quiet = RouletteKit.Table(("Оля", 1000));
        quiet.H.Tick(99);
        Assert.NotEqual("hurry", quiet.S.Glek.Mood);
    }

    [Fact]
    public void Number_is_absent_from_the_view_before_close()
    {
        var k = RouletteKit.Table(("Оля", 1000));
        k.Rig(17);
        k.Bet(0, "red", 5);
        var v = k.View(0);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("spin").ValueKind);
        Assert.DoesNotContain("17", Views.Text(v.GetProperty("history")));
        Assert.Equal(0, k.S.SpinNo);
        k.To(Spin);
        v = k.View(null);
        Assert.Equal(17, v.GetProperty("spin").GetProperty("n").GetInt32());
        Assert.Equal("b", v.GetProperty("spin").GetProperty("c").GetString());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("last").ValueKind);
        Assert.Equal(0, v.GetProperty("history").GetArrayLength());
        k.To(Result);
        v = k.View(null);
        Assert.Equal(17, v.GetProperty("last").GetProperty("n").GetInt32());
        Assert.Equal(17, v.GetProperty("history")[0].GetProperty("n").GetInt32());
    }

    [Fact]
    public void Leaving_mid_bets_keeps_bets_and_they_settle()
    {
        var k = RouletteKit.Table(("Оля", 1000), ("Петро", 1000));
        k.Bet(0, "red", 5);
        k.Bet(1, "dozen:1", 10);
        Assert.True(k.H.Leave("Петро").Ok);
        Assert.Equal(RoomStatus.Playing, k.H.Room.Status);   // не техпоразка
        var p = PlayerOf(k.View(0), "Петро");
        Assert.False(p.GetProperty("here").GetBoolean());
        Assert.Equal(JsonValueKind.Null, p.GetProperty("seat").ValueKind);
        Assert.Equal(1, p.GetProperty("color").GetInt32());
        Assert.Equal(10, p.GetProperty("total").GetInt32());

        k.Rig(5);
        k.To(Spin);
        var refBet = k.Ref("roulette-bet", "Петро");
        Assert.Contains($"spend:Петро:10:{refBet}", k.Stakes.Calls);
        k.To(Result);
        Assert.Contains($"grant:Петро:30:{k.Ref("roulette-win", "Петро")}", k.Stakes.Calls);
        Assert.Equal(1020, k.Stakes.Balance("Петро"));
        Assert.True(HasPlayer(k.View(0), "Петро"));
        k.To(Bets);
        Assert.False(HasPlayer(k.View(0), "Петро"));
        Assert.Empty(k.H.Finished);
    }

    [Fact]
    public void Late_join_can_bet_next_window()
    {
        var k = RouletteKit.Table(("Оля", 1000));
        k.Stakes.Set("Петро", 500);
        k.Bet(0, "red", 5);
        k.Rig(3);
        k.To(Spin);
        var r = k.H.Join("Петро");
        Assert.True(r.Ok, r.Message);
        Assert.Equal("Сідай ближче! Ставки — до «Ставки зроблено!», черепки — з гаманця", r.Message);
        Assert.Equal(500, k.Me(1).GetProperty("wallet").GetInt32());
        Assert.Equal("Ставки зроблено — чекай наступного кола", k.Bet(1, "red", 5).Message);
        k.To(Bets);
        Assert.True(k.Bet(1, "red", 5).Ok);
        Assert.Equal(1, PlayerOf(k.View(1), "Петро").GetProperty("seat").GetInt32());
    }

    [Fact]
    public void Empty_table_holds_60s_then_goes()
    {
        var k = RouletteKit.Table(("Оля", 1000));
        k.Rig(1);
        k.Bet(0, "red", 100);
        var id = k.H.RoomId;
        var room = k.H.Room;
        k.H.Leave("Оля");
        Assert.NotNull(k.H.Rooms.Find(id));
        var leftAt = k.H.Clock.UtcNow;
        k.To(Spin);
        k.To(Result);
        Assert.Equal(1100, k.Stakes.Balance("Оля"));   // покинуті ставки дограли
        k.To(Idle);   // нікого — нового кола нема
        k.H.Rooms.Housekeeping(leftAt.AddSeconds(59));
        Assert.NotNull(k.H.Rooms.Find(id));
        k.H.Rooms.Housekeeping(leftAt.AddSeconds(61));
        Assert.Null(k.H.Rooms.Find(id));
        Assert.Empty(k.H.Finished);
        Assert.Equal(RoomStatus.Playing, room.Status);
    }

    // ---------- гроші й ідемпотентність ----------

    [Fact]
    public void Close_debits_once_per_player_with_the_round_ref()
    {
        var k = RouletteKit.Table(("Оля", 1000), ("Петро", 1000));
        k.Bet(0, "red", 10);
        k.Bet(0, "straight:17", 15);
        k.Bet(0, "red", 5);
        k.Bet(1, "black", 40);
        k.Rig(17);
        k.To(Spin);
        Assert.Equal(2, k.Spends.Count);
        Assert.Equal(1, k.S.SpinNo);
        Assert.Matches("^[0-9a-f]{8}$", k.S.Epoch);
        var olya = $"roulette-bet:{k.H.RoomId}:{k.S.Epoch}:1:оля";
        Assert.Contains($"spend:Оля:30:{olya}", k.Stakes.Calls);
        Assert.Contains($"spend:Петро:40:roulette-bet:{k.H.RoomId}:{k.S.Epoch}:1:петро", k.Stakes.Calls);
        Assert.Equal("roulette-bet:roulette", k.Stakes.Reasons[olya]);
        Assert.Equal(970, k.Me(0).GetProperty("wallet").GetInt32());
        Assert.Equal(970, k.Me(0).GetProperty("free").GetInt32());   // уже списано — у «вільних» не двоїться
    }

    [Fact]
    public void Payout_comes_at_landing_not_at_close()
    {
        var k = RouletteKit.Table(("Оля", 1000));
        k.Bet(0, "straight:17", 10);
        k.Rig(17);
        k.To(Spin);
        Assert.Empty(k.Grants);
        k.H.Tick(23);
        Assert.Empty(k.Grants);
        k.H.Tick();
        var win = k.Ref("roulette-win", "Оля");
        Assert.Equal([$"grant:Оля:360:{win}"], k.Grants);
        Assert.Equal("roulette-win:roulette", k.Stakes.Reasons[win]);
        Assert.Equal(1350, k.Stakes.Balance("Оля"));
        Assert.Equal(1350, k.Me(0).GetProperty("wallet").GetInt32());
    }

    [Fact]
    public void Losers_get_no_grant_and_winners_get_return()
    {
        var k = RouletteKit.Table(("Оля", 1000), ("Петро", 1000), ("Іра", 1000));
        k.Bet(0, "straight:17", 10);
        k.Bet(0, "black", 10);   // 17 — чорне
        k.Bet(1, "red", 20);
        k.Bet(2, "dozen:1", 20);
        k.Bet(2, "column:2", 5);
        k.Rig(17);
        k.To(Result);
        Assert.Equal(2, k.Grants.Count);
        Assert.Contains($"grant:Оля:380:{k.Ref("roulette-win", "Оля")}", k.Grants);
        Assert.Contains($"grant:Іра:15:{k.Ref("roulette-win", "Іра")}", k.Grants);
        Assert.Equal(1360, k.Stakes.Balance("Оля"));
        Assert.Equal(980, k.Stakes.Balance("Петро"));
        Assert.Equal(990, k.Stakes.Balance("Іра"));
        var last = k.View(null).GetProperty("last");
        Assert.Equal(65, last.GetProperty("staked").GetInt32());
        Assert.Equal(395, last.GetProperty("paid").GetInt32());
        var results = last.GetProperty("results");
        Assert.Equal(["Оля", "Іра", "Петро"], results.EnumerateArray().Select(r => r.GetProperty("nick").GetString()!));
        Assert.Equal(360, results[0].GetProperty("net").GetInt32());
        Assert.Equal(["straight:17", "black"], results[0].GetProperty("hits").EnumerateArray().Select(h => h.GetString()!));
        Assert.Equal(["column:2"], results[1].GetProperty("hits").EnumerateArray().Select(h => h.GetString()!));
    }

    [Fact]
    public void Settle_twice_pays_once()
    {
        var k = RouletteKit.Table(("Оля", 1000));
        k.Bet(0, "red", 50);
        k.Rig(1);
        k.To(Spin);
        var pending = k.S.Pending!;
        k.To(Result);
        k.Book.Settle(pending);
        Assert.Equal(0, k.Book.Recover(DateTimeOffset.MaxValue));   // запис уже прибрано
        k.Book.Open(pending);                                        // а навіть якби лишився —
        Assert.Equal(1, k.Book.Recover(DateTimeOffset.MaxValue));
        Assert.Single(k.Grants);
        Assert.Equal(1050, k.Stakes.Balance("Оля"));
    }

    [Fact]
    public void Debit_failure_at_close_drops_that_players_bets_with_a_note()
    {
        var k = RouletteKit.Table(("Оля", 1000), ("Петро", 1000));
        k.Bet(0, "red", 10);
        k.Bet(1, "black", 50);
        k.Stakes.Set("Петро", 20);   // спорожнив гаманець у Лавці
        k.Rig(2);
        k.To(Spin);
        Assert.Single(k.Spends);
        Assert.Equal("Черепків не стало — твої ставки (50) знято", k.Me(1).GetProperty("note").GetString());
        Assert.Equal(JsonValueKind.Null, k.Me(0).GetProperty("note").ValueKind);
        Assert.Equal(0, PlayerOf(k.View(1), "Петро").GetProperty("total").GetInt32());
        k.To(Result);
        Assert.Empty(k.Grants);   // чорне, але Петро не платив
        Assert.Equal(20, k.Stakes.Balance("Петро"));
        Assert.DoesNotContain(k.View(null).GetProperty("last").GetProperty("results").EnumerateArray(),
            r => r.GetProperty("nick").GetString() == "Петро");

        // не заплатив ніхто — коло не крутиться
        k.To(Bets);
        k.Bet(0, "red", 10);
        k.Stakes.Set("Оля", 0);
        var spins = k.S.SpinNo;
        k.H.Tick(100);
        Assert.Equal(Bets, k.S.Phase);
        Assert.Equal(spins + 1, k.S.SpinNo);
        Assert.Equal("Черепків ні в кого не стало — не кручу", k.S.Glek.Say);
        Assert.Empty(k.Book.Pending());
        Assert.Equal(25_000, k.View(0).GetProperty("leftMs").GetInt32());
    }

    [Fact]
    public void Pending_spin_is_written_before_debits_and_removed_after_settle()
    {
        SpyStakes? spy = null;
        var seen = new List<bool>();
        var k = new RouletteKit(wrap: f => spy = new SpyStakes(f), people: [("Оля", 1000), ("Петро", 1000)]);
        spy!.BeforeSpend = refKey => seen.Add(k.Fake!.States.TryGetValue(RouletteBook.StoreKey, out var json)
            && json.Contains($"\"{k.H.RoomId}:{k.S.Epoch}\"", StringComparison.Ordinal));
        k.Bet(0, "red", 10);
        k.Bet(1, "black", 10);
        k.Rig(1);
        k.To(Spin);
        Assert.Equal([true, true], seen);
        var pending = Assert.Single(k.Book.Pending());
        Assert.Equal(1, pending.Number);
        Assert.Equal(2, pending.Pays.Count);
        Assert.Equal(new SpinPay("Оля", 10, 20), pending.Pays[0]);
        Assert.Equal(new SpinPay("Петро", 10, 0), pending.Pays[1]);
        k.To(Result);
        Assert.Empty(k.Book.Pending());
        Assert.DoesNotContain(k.H.RoomId, k.Fake!.States[RouletteBook.StoreKey]);
    }

    [Fact]
    public void Store_failure_skips_the_round_without_debits()
    {
        var k = new RouletteKit(store: new BrokenStore(), people: [("Оля", 1000)]);
        k.Bet(0, "red", 10);
        k.H.Tick(100);
        Assert.Equal(Bets, k.S.Phase);
        Assert.Empty(k.Spends);
        Assert.Equal("Каса заїла — це коло не крутимо", k.S.Glek.Say);
        Assert.Equal(10, PlayerOf(k.View(0), "Оля").GetProperty("total").GetInt32());   // ставки лишились на полі
        Assert.Equal(25_000, k.View(0).GetProperty("leftMs").GetInt32());
        Assert.Equal(1, k.S.SpinNo);
    }

    [Fact]
    public void New_epoch_after_load_means_refs_never_repeat()
    {
        var k = RouletteKit.Table(("Оля", 1000));
        k.Bet(0, "red", 10);
        k.Round(2);
        var first = k.Spends.Single();
        var epoch = k.S.Epoch;
        lock (k.H.Room.Sync)
        {
            var json = k.G.Save()!;
            k.G.Start();
            k.G.Load(json);
            k.G.Resumed(TimeSpan.Zero);
        }
        Assert.NotEqual(epoch, k.S.Epoch);
        Assert.Equal(1, k.S.SpinNo);
        k.Bet(0, "red", 10);
        k.Round(2);
        Assert.Equal(2, k.Spends.Count);
        Assert.NotEqual(first.Split(':', 4)[3], k.Spends[1].Split(':', 4)[3]);
        Assert.Equal(980, k.Stakes.Balance("Оля"));
    }

    // ---------- перезапуск ----------

    [Fact]
    public void Resume_mid_bets_keeps_bets_and_shifts_the_deadline()
    {
        var k = RouletteKit.Table(("Оля", 1000));
        k.Bet(0, "red", 10);
        k.Bet(0, "straight:17", 5);
        k.H.Tick(40);   // 10 с минуло, лишилось 15
        var json = k.G.Save()!;
        var pause = TimeSpan.FromSeconds(30);
        k.H.Clock.Advance(pause);
        lock (k.H.Room.Sync)
        {
            k.G.Start();
            k.G.Load(json);
            k.G.Resumed(pause);
        }
        var v = k.View(0);
        Assert.Equal(Bets, k.S.Phase);
        Assert.Equal(15_000, v.GetProperty("leftMs").GetInt32());
        Assert.Equal(15, PlayerOf(v, "Оля").GetProperty("total").GetInt32());
        Assert.True(k.Me(0).GetProperty("canUndo").GetBoolean());
        k.Rig(17);
        k.To(Result);
        Assert.Single(k.Grants);
    }

    [Fact]
    public void Resume_mid_spin_settles_once()
    {
        var k = RouletteKit.Table(("Оля", 1000), ("Петро", 1000));
        k.Bet(0, "straight:17", 10);
        k.Bet(1, "red", 10);
        k.Rig(17);
        k.To(Spin);
        var json = k.G.Save()!;
        // новий процес: спершу TablesKeeper повертає стіл (Program.cs), тоді каса на старті — і коло, на яке стіл чекає, не чіпає
        var pause = TimeSpan.FromSeconds(20);
        k.H.Clock.Advance(pause);
        lock (k.H.Room.Sync)
        {
            k.G.Start();
            k.G.Load(json);
            k.G.Resumed(pause);
        }
        Assert.Equal(Spin, k.S.Phase);
        var book2 = new RouletteBook(k.Stakes, k.Store, k.H.Clock, defer: a => a(),
            held: (table, spin) => RouletteBook.HeldBy(k.H.Rooms, table, spin));
        Assert.Equal(0, book2.Recover(DateTimeOffset.MaxValue));
        Assert.Empty(k.Grants);   // гроші — коли ляже кулька, не раніше
        Assert.Single(book2.Pending());
        k.To(Result);
        Assert.Single(k.Grants);
        Assert.Equal(1350, k.Stakes.Balance("Оля"));
        Assert.Equal(990, k.Stakes.Balance("Петро"));
        Assert.Equal(17, k.View(0).GetProperty("last").GetProperty("n").GetInt32());
        Assert.Equal(["ach:roulette-straight"], k.Achievements("Оля"));
        Assert.Empty(book2.Pending());
    }

    [Fact]
    public void Resume_of_a_spin_the_book_already_paid_lands_quietly()
    {
        var k = RouletteKit.Table(("Оля", 1000), ("Петро", 1000));
        k.Bet(0, "straight:17", 100);
        k.Bet(1, "red", 10);
        k.Rig(17);
        k.To(Spin);
        var json = k.G.Save()!;
        // заморозка тяглась довше за OrphanAge — підмітання старого процесу вже виплатило коло
        Assert.Equal(1, new RouletteBook(k.Stakes, k.Store, defer: a => a()).Recover(DateTimeOffset.MaxValue));
        var says = k.H.Outbox.Count;
        var pause = TimeSpan.FromMinutes(10);
        k.H.Clock.Advance(pause);
        lock (k.H.Room.Sync)
        {
            k.G.Start();
            k.G.Load(json);
            k.G.Resumed(pause);
        }
        Assert.Equal(Result, k.S.Phase);
        Assert.Equal(5000, k.View(0).GetProperty("leftMs").GetInt32());   // від «зараз», пауза не додалась
        Assert.Equal(17, k.View(0).GetProperty("last").GetProperty("n").GetInt32());
        Assert.Equal(JsonValueKind.Null, k.View(0).GetProperty("glek").GetProperty("say").ValueKind);
        k.To(Bets);
        Assert.Single(k.Grants);
        Assert.Empty(k.H.Awards);
        Assert.Empty(k.H.Scores);
        Assert.DoesNotContain(k.H.Outbox.Skip(says).OfType<Journal>(), j => j.Text.StartsWith("🎡", StringComparison.Ordinal));
        Assert.Equal(4500, k.Stakes.Balance("Оля"));
    }

    [Fact]
    public void Sweep_leaves_a_spin_the_live_table_still_holds()
    {
        var k = RouletteKit.Table(("Оля", 1000));
        k.Bet(0, "red", 100);
        k.Rig(1);
        k.To(Spin);
        var book2 = new RouletteBook(k.Stakes, k.Store, k.H.Clock, defer: a => a(),
            held: (table, spin) => RouletteBook.HeldBy(k.H.Rooms, table, spin));
        // довга пауза (сервер спав): запис постарів, а кулька ще не лягла — платить гра, коли ляже
        k.H.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, book2.Sweep());
        Assert.Empty(k.Grants);
        // сирота від столу, якого вже нема, — платиться як завжди
        Assert.True(book2.Open(new PendingSpin("gone0000:cafe0000", 1, "roulette", 1, k.H.Clock.UtcNow.AddMinutes(-3), [new SpinPay("Оля", 5, 10)])));
        Assert.True(book2.Take("Оля", 5, "roulette-bet:roulette", "roulette-bet:gone0000:cafe0000:1:оля"));
        Assert.Equal(1, book2.Sweep());
        Assert.Equal(["grant:Оля:10:roulette-win:gone0000:cafe0000:1:оля"], k.Grants);
        k.To(Result);
        Assert.Equal(2, k.Grants.Count);
        Assert.Equal(1105, k.Stakes.Balance("Оля"));
        Assert.Empty(book2.Pending());
    }

    [Fact]
    public void Crash_mid_spin_is_settled_by_the_drawn_number_from_the_book()
    {
        var k = RouletteKit.Table(("Оля", 1000), ("Петро", 1000));
        k.Bet(0, "red", 100);
        k.Bet(1, "black", 100);
        k.Rig(1);
        k.To(Spin);
        // процес упав: гри нема, лише запис каси
        var book2 = new RouletteBook(k.Stakes, k.Store, defer: a => a());
        Assert.Equal(1, book2.Recover(DateTimeOffset.MaxValue));
        Assert.Equal(1100, k.Stakes.Balance("Оля"));
        Assert.Equal(900, k.Stakes.Balance("Петро"));   // програш — не повернення
        Assert.Single(k.Grants);
        Assert.Empty(book2.Pending());
    }

    [Fact]
    public void Shared_table_is_resumable_and_not_busy()
    {
        var k = RouletteKit.Table(("Оля", 1000), ("Петро", 1000));
        k.Bet(0, "red", 10);
        Assert.True(k.G.Resumable);
        Assert.Empty(k.H.Rooms.Busy());
        Assert.False(new RouletteSolo().Resumable);
    }

    // ---------- соло ----------

    [Fact]
    public void Solo_spin_debits_draws_and_settles_after_5s()
    {
        var k = RouletteKit.Solo();
        var v = k.View(0);
        Assert.Equal("solo", v.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("until").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("leftMs").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("phaseMs").ValueKind);
        k.H.Tick(200);
        Assert.Equal(Bets, k.S.Phase);   // таймера ставок нема
        k.Bet(0, "straight:17", 10);
        k.Bet(0, "red", 20);
        k.Rig(17);
        Assert.True(k.H.Act(0, "spin", new { }).Ok);
        Assert.Equal(Spin, k.S.Phase);
        Assert.Equal([$"spend:Оля:30:{k.Ref("roulette-bet", "Оля")}"], k.Spends);
        Assert.Equal("roulette-bet:roulette-solo", k.Stakes.Reasons[k.Ref("roulette-bet", "Оля")]);
        v = k.View(0);
        Assert.Equal(5000, v.GetProperty("spin").GetProperty("leftMs").GetInt32());
        Assert.Equal(5000, v.GetProperty("phaseMs").GetInt32());
        Assert.Equal("Колесо крутиться — дочекайся", k.Bet(0, "red", 1).Message);
        k.H.Tick(19);
        Assert.Equal(Spin, k.S.Phase);
        Assert.Empty(k.Grants);
        k.H.Tick();
        Assert.Equal(Bets, k.S.Phase);
        Assert.Equal([$"grant:Оля:360:{k.Ref("roulette-win", "Оля")}"], k.Grants);
        Assert.Equal("roulette-win:roulette-solo", k.Stakes.Reasons[k.Ref("roulette-win", "Оля")]);
        v = k.View(0);
        Assert.Equal(0, v.GetProperty("onTable").GetInt32());
        Assert.Equal(0, v.GetProperty("spin").GetProperty("leftMs").GetInt32());
        Assert.Equal(17, v.GetProperty("last").GetProperty("n").GetInt32());
        Assert.Equal(330, v.GetProperty("last").GetProperty("results")[0].GetProperty("net").GetInt32());
        Assert.Equal(1330, v.GetProperty("me").GetProperty("wallet").GetInt32());
        Assert.Single(v.GetProperty("players").EnumerateArray());
    }

    [Fact]
    public void Solo_spin_without_bets_is_refused_and_again_repeats_then_spins()
    {
        var k = RouletteKit.Solo();
        Assert.Equal("Спершу постав хоч один черепок", k.H.Act(0, "spin", new { }).Message);
        Assert.Equal("Спершу постав хоч один черепок", k.H.Act(0, "spin", new { again = true }).Message);
        Assert.Equal(0, k.S.SpinNo);
        k.Bet(0, "red", 50);
        k.SoloSpin(2);
        Assert.Equal(50, k.Me(0).GetProperty("repeatCost").GetInt32());
        Assert.Equal("Спершу постав хоч один черепок", k.H.Act(0, "spin", new { }).Message);
        k.Rig(1);
        Assert.True(k.H.Act(0, "spin", new { again = true }).Ok);
        Assert.Equal(Spin, k.S.Phase);
        Assert.Equal(2, k.Spends.Count);
        Assert.StartsWith("spend:Оля:50:", k.Spends[1]);
        k.To(Bets);
        Assert.Equal(1000, k.Stakes.Balance("Оля"));
    }

    [Fact]
    public void Solo_debit_failure_keeps_bets_and_says_why()
    {
        var k = RouletteKit.Solo();
        k.Bet(0, "red", 500);
        k.Stakes.Set("Оля", 100);
        var r = k.H.Act(0, "spin", new { });
        Assert.False(r.Ok);
        Assert.Equal("Бракує черепків: у гаманці 100, а на столі 500", r.Message);
        Assert.Equal(Bets, k.S.Phase);
        Assert.Equal(500, k.View(0).GetProperty("onTable").GetInt32());
        Assert.Empty(k.Book.Pending());
        Assert.Empty(k.Spends);

        // «Ще раз і крутити» не вдалось — повтор не лишається на полі
        SpyStakes? spy = null;
        var again = new RouletteKit("roulette-solo", wrap: f => spy = new SpyStakes(f), people: [("Оля", 1000)]);
        again.Bet(0, "red", 100);
        again.SoloSpin(2);
        spy!.BeforeSpend = _ => again.Stakes.Set("Оля", 10);   // гаманець спорожнів між читанням і списанням
        r = again.H.Act(0, "spin", new { again = true });
        Assert.Equal("Бракує черепків: у гаманці 900, а на столі 100", r.Message);
        Assert.Equal(0, again.View(0).GetProperty("onTable").GetInt32());
        Assert.Equal(Bets, again.S.Phase);
    }

    [Fact]
    public void Solo_state_survives_save_load_with_history_and_last_bets()
    {
        var k = RouletteKit.Solo();
        k.Bet(0, "red", 50);
        k.Bet(0, "straight:7", 5);
        k.SoloSpin(7);
        k.Bet(0, "dozen:3", 10);
        var saved = k.H.Store.States["roulette-solo:оля"];   // каркас зберіг після останньої дії
        var before = Views.Text(k.View(0));
        lock (k.H.Room.Sync)
        {
            k.G.Start();
            k.G.Load(saved);
        }
        Assert.Equal(before, Views.Text(k.View(0)));
        Assert.Equal(7, k.View(0).GetProperty("history")[0].GetProperty("n").GetInt32());
        k.H.Act(0, "clear");
        Assert.True(k.H.Act(0, "repeat").Ok);
        Assert.Equal(55, k.View(0).GetProperty("onTable").GetInt32());
    }

    [Fact]
    public void Solo_reopen_of_a_spin_already_settled_is_quiet()
    {
        var k = RouletteKit.Solo(wallet: 10_000);
        k.Bet(0, "straight:17", 50);
        k.Rig(17);
        Assert.True(k.H.Act(0, "spin", new { }).Ok);
        // каркас зберігає соло лише після дії — тик, у якому лягла кулька, у сховище не потрапляє
        var saved = k.H.Store.States["roulette-solo:оля"];
        Assert.Contains("\"phase\":\"spin\"", saved);
        k.To(Bets);
        int Lines() => k.H.Outbox.OfType<Journal>().Count(j => j.Text.StartsWith("🎡", StringComparison.Ordinal));
        Assert.Equal(1, Lines());
        Assert.Single(k.H.Scores);
        var awards = k.H.Awards.Count;
        Assert.Equal(["ach:roulette-straight", "ach:roulette-hopak"], k.Achievements("Оля"));
        Assert.Empty(k.Book.Pending());
        Assert.Equal(0, k.Book.Recover(DateTimeOffset.MaxValue));
        k.H.Clock.Advance(TimeSpan.FromMinutes(40));
        for (var reopen = 0; reopen < 2; reopen++)
        {
            lock (k.H.Room.Sync)
            {
                k.G.Start();
                k.G.Load(saved);
            }
            Assert.Equal(Bets, k.S.Phase);   // одразу: другої кульки нема
            Assert.Null(k.S.Glek.Say);        // і Глек не повторює свого «Сімнадцять!»
            k.H.Tick(40);
            Assert.Equal(1, Lines());
            Assert.Single(k.H.Scores);
            Assert.Equal(awards, k.H.Awards.Count);
            Assert.Single(k.Grants);
            var v = k.View(0);
            Assert.Equal(17, v.GetProperty("last").GetProperty("n").GetInt32());
            Assert.Equal(1750, v.GetProperty("last").GetProperty("results")[0].GetProperty("net").GetInt32());
            Assert.Equal(1, v.GetProperty("history").GetArrayLength());
            Assert.Equal(11_750, v.GetProperty("me").GetProperty("wallet").GetInt32());
            Assert.True(v.GetProperty("me").GetProperty("canRepeat").GetBoolean());
        }
        Assert.Equal(11_750, k.Stakes.Balance("Оля"));
    }

    [Fact]
    public void Solo_reopen_mid_spin_still_lands_the_ball_and_tells_once()
    {
        var k = RouletteKit.Solo(wallet: 10_000);
        k.Bet(0, "straight:17", 50);
        k.Rig(17);
        Assert.True(k.H.Act(0, "spin", new { }).Ok);
        var saved = k.H.Store.States["roulette-solo:оля"];
        // відкрили наново, поки кулька котилась: каса ще тримає коло — грає гра
        lock (k.H.Room.Sync)
        {
            k.G.Start();
            k.G.Load(saved);
        }
        Assert.Equal(Spin, k.S.Phase);
        Assert.Single(k.Book.Pending());
        k.To(Bets);
        Assert.Single(k.Grants);
        Assert.Single(k.H.Outbox.OfType<Journal>(), j => j.Text.StartsWith("🎡", StringComparison.Ordinal));
        Assert.Single(k.H.Scores);
        Assert.Equal(11_750, k.Stakes.Balance("Оля"));
    }

    [Fact]
    public void Solo_reopen_reads_the_wallet_afresh()
    {
        var k = RouletteKit.Solo();
        k.Bet(0, "red", 10);
        var saved = k.H.Store.States["roulette-solo:оля"];
        Assert.Equal(990, k.Me(0).GetProperty("free").GetInt32());
        k.Stakes.Set("Оля", 5000);   // між відкриттями гаманець жив своїм життям
        lock (k.H.Room.Sync)
        {
            k.G.Start();
            k.G.Load(saved);
        }
        Assert.Equal(5000, k.Me(0).GetProperty("wallet").GetInt32());
        Assert.Equal(4990, k.Me(0).GetProperty("free").GetInt32());
    }

    // ---------- межа арифметики ----------

    [Fact]
    public void Huge_bets_are_refused_before_the_payout_could_overflow()
    {
        var k = RouletteKit.Table(("Оля", 200_000_000));
        Assert.Equal(59_652_323, RouletteCore.MaxPerSpot);
        Assert.Equal(RouletteCore.Say.SpotTooBig, k.Bet(0, "straight:17", RouletteCore.MaxPerSpot + 1).Message);
        Assert.Equal("Завелика ставка на одне поле", RouletteCore.Say.SpotTooBig);
        Assert.True(k.Bet(0, "straight:17", RouletteCore.MaxPerSpot).Ok);
        Assert.Equal(RouletteCore.Say.SpotTooBig, k.Bet(0, "straight:17", 1).Message);   // докласти на те саме поле — теж понад
        // 17 уже на межі: спліт 17·20 додав би понад int за того самого числа
        Assert.Equal(RouletteCore.Say.WinTooBig, k.Bet(0, "split:17-20", 2).Message);
        Assert.True(k.Bet(0, "split:17-20", 1).Ok);   // ×18 = 18 — ще влазить (межа ділиться з остачею 19)
        // червоне грає на інших числах, ніж 17, — рахується найгірше число, а не сума всього поля
        Assert.True(k.Bet(0, "red", 1_000_000).Ok);
        Assert.Equal(RouletteCore.Say.SpotTooBig, k.H.Act(0, "double").Message);
        var v = k.Me(0);
        Assert.Equal(RouletteCore.MaxPerSpot + 1 + 1_000_000, v.GetProperty("onTable").GetInt32());
        Assert.Equal(int.MaxValue - 1, RouletteCore.MaxReturn([("straight:17", RouletteCore.MaxPerSpot), ("split:17-20", 1)]));
    }

    // ---------- вид, Глек, ачівки, таблиця, Журнал, RNG ----------

    [Fact]
    public void View_shape_on_the_wire()
    {
        var k = RouletteKit.Table(("Оля", 1000), ("Петро", 1000));
        k.Bet(0, "straight:17", 25);
        k.Bet(1, "dozen:1", 10);
        k.Round(17);
        k.Bet(0, "red", 5);
        var v = k.View(0);
        string[] fields = ["mode", "wheel", "phase", "until", "leftMs", "phaseMs", "spin", "history", "players", "onTable", "last", "glek", "me", "closed"];
        Assert.Equal(fields.Order(), v.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(["nick", "seat", "color", "here", "mine", "total", "bets"], v.GetProperty("players")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(["wallet", "onTable", "free", "canUndo", "canRepeat", "repeatCost", "canDouble", "canWheel", "note"],
            v.GetProperty("me").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["no", "n", "c", "staked", "paid", "results", "big"], v.GetProperty("last").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["nick", "color", "staked", "paid", "net", "hits"],
            v.GetProperty("last").GetProperty("results")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(["mood", "say", "seq"], v.GetProperty("glek").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["n", "c"], v.GetProperty("history")[0].EnumerateObject().Select(p => p.Name));
        Assert.False(v.GetProperty("closed").GetBoolean());
        Assert.Equal("eu", v.GetProperty("wheel").GetString());
        Assert.False(v.GetProperty("me").GetProperty("canWheel").GetBoolean());

        k.To(Spin);
        Assert.Equal(["no", "n", "c", "until", "leftMs", "ms"], k.View(0).GetProperty("spin").EnumerateObject().Select(p => p.Name));

        var watcher = k.View(null);
        Assert.Equal(JsonValueKind.Null, watcher.GetProperty("me").ValueKind);
        Assert.All(watcher.GetProperty("players").EnumerateArray(), p => Assert.False(p.GetProperty("mine").GetBoolean()));
        Assert.Equal(2, watcher.GetProperty("players").GetArrayLength());
        Assert.True(PlayerOf(k.View(1), "Петро").GetProperty("mine").GetBoolean());
        Assert.False(PlayerOf(k.View(1), "Оля").GetProperty("mine").GetBoolean());
        Assert.True(Views.WireBytes(k.G.View(0)) < 16_000);
    }

    [Fact]
    public void View_of_eight_players_with_sixty_spots_stays_small()
    {
        var people = Enumerable.Range(1, 8).Select(i => ($"Гравець{i}", 100_000)).ToArray();
        var k = RouletteKit.Table(people);
        var spots = RouletteCore.All.Take(60).ToList();
        for (var seat = 0; seat < 8; seat++)
            foreach (var spot in spots) Assert.True(k.Bet(seat, spot, 1000).Ok);
        var bytes = Views.WireBytes(k.G.View(0));
        Assert.True(bytes <= 20 * 1024, $"вид {bytes} байт");   // ≈ 19 КБ: 8 × 60 полів по 1000
    }

    [Fact]
    public void Glek_moods_follow_the_priority()
    {
        var k = RouletteKit.Table(("Оля", 100_000));
        var seq = k.S.Glek.Seq;
        string Check(string mood, int n, params (string Spot, int Amount)[] bets)
        {
            foreach (var (spot, amount) in bets) Assert.True(k.Bet(0, spot, amount).Ok);
            k.Rig(n);
            k.To(Result);
            Assert.Equal(mood, k.S.Glek.Mood);
            Assert.True(k.S.Glek.Seq > seq);
            seq = k.S.Glek.Seq;
            var say = k.S.Glek.Say!;
            Assert.Equal(mood, k.View(0).GetProperty("glek").GetProperty("mood").GetString());
            k.To(Bets);
            return say;
        }
        Assert.StartsWith("17, чорне! ", Check("dance", 17, ("straight:17", 10)));
        Assert.Equal("Оля", k.View(0).GetProperty("last").GetProperty("big").GetString());
        Check("dance", 0, ("straight:0", 10), ("red", 500));   // гопак сильніший за зеро
        Assert.Contains(Check("laugh", 0, ("red", 100)), RouletteLines.Zero);
        Assert.Equal(JsonValueKind.Null, k.View(0).GetProperty("last").GetProperty("big").ValueKind);
        Assert.StartsWith("1, червоне! ", Check("clap", 1, ("red", 100)));
        Assert.StartsWith("2, чорне! ", Check("rake", 2, ("red", 100)));
        Check("dance", 2, ("black", 1000));                   // net ≥ 1000
        Assert.StartsWith("Зеро! ", Check("clap", 0, ("straight:0", 5)));   // число, але ставка менша за 10
        Assert.Equal(JsonValueKind.Null, k.View(0).GetProperty("last").GetProperty("big").ValueKind);
    }

    [Fact]
    public void Achievements_are_requested_only_for_seated_winners()
    {
        var k = RouletteKit.Table(("Оля", 100_000), ("Петро", 100_000), ("Іра", 100));
        k.Bet(0, "straight:17", 1);
        k.Bet(1, "straight:17", 30);
        k.Bet(2, "black", 100);   // увесь гаманець
        k.H.Leave("Петро");
        k.Round(17);
        Assert.Equal(["ach:roulette-straight"], k.Achievements("Оля"));
        Assert.Empty(k.Achievements("Петро"));   // виграв гопак, але вже не сидить
        Assert.Equal(101_050, k.Stakes.Balance("Петро"));
        Assert.Equal(["ach:roulette-allin"], k.Achievements("Іра"));

        k.Bet(0, "straight:0", 1);
        k.Bet(0, "black", 30);
        k.Round(0);
        Assert.Contains("ach:roulette-zero", k.Achievements("Оля"));

        // ва-банк, що програв, і ва-банк менше 50 — без ачівки
        var poor = RouletteKit.Table(("Оля", 40), ("Петро", 100));
        poor.Bet(0, "red", 40);
        poor.Bet(1, "black", 100);
        poor.Round(1);
        Assert.Empty(poor.Achievements("Оля"));
        Assert.Empty(poor.Achievements("Петро"));
        Assert.Equal("Петро ставить усе… а кулька каже «ні». Тримайся",
            poor.H.Outbox.OfType<TableSaid>().Last().Line.Text);

        // гопак
        var big = RouletteKit.Table(("Оля", 10_000));
        big.Bet(0, "straight:17", 30);
        big.Round(17);
        Assert.Contains("ach:roulette-hopak", big.Achievements("Оля"));
        Assert.Contains("ach:roulette-straight", big.Achievements("Оля"));
    }

    [Fact]
    public void Red_five_needs_five_own_red_rounds_in_a_row()
    {
        var k = RouletteKit.Table(("Оля", 10_000), ("Петро", 10_000));
        void RedRound(int n)
        {
            k.Bet(0, "red", 10);
            k.Round(n);
        }
        for (var i = 0; i < 4; i++) RedRound(1);
        k.Bet(1, "black", 10);
        k.Round(2);                    // коло без Олиної ставки серію не рве
        Assert.Equal(4, k.S.Red["оля"]);
        RedRound(2);                   // чорне — серія з нуля
        Assert.Equal(0, k.S.Red["оля"]);
        for (var i = 0; i < 4; i++) RedRound(3);
        k.Bet(0, "black", 10);
        k.Round(3);                    // коло без red — теж з нуля
        Assert.DoesNotContain("ach:roulette-red5", k.Achievements("Оля"));
        for (var i = 0; i < 5; i++) RedRound(5);
        Assert.Contains("ach:roulette-red5", k.Achievements("Оля"));
    }

    [Fact]
    public void Score_is_the_net_win_of_seated_winners()
    {
        var k = RouletteKit.Table(("Оля", 1000), ("Петро", 1000), ("Іра", 1000));
        k.Bet(0, "red", 100);
        k.Bet(1, "black", 100);
        k.Bet(2, "dozen:1", 100);
        k.H.Leave("Іра");
        k.Round(1);
        var score = Assert.Single(k.H.Scores);
        Assert.Equal("Оля", score.Nick);
        Assert.Equal(100, score.Score);
        Assert.Equal("roulette", score.GameId);

        var solo = RouletteKit.Solo();
        solo.Bet(0, "straight:5", 10);
        solo.SoloSpin(5);
        Assert.Equal(350, Assert.Single(solo.H.Scores).Score);
    }

    [Fact]
    public void Journal_line_for_a_big_win_at_most_every_10_minutes()
    {
        var k = RouletteKit.Table(("Оля", 100_000));
        List<string> Lines() => [.. k.H.Outbox.OfType<Journal>().Select(j => j.Text).Where(t => t.StartsWith("🎡", StringComparison.Ordinal))];
        k.Bet(0, "straight:17", 100);
        k.Round(17);
        Assert.Equal(["🎡 Рулетка: Оля виграє 3500 черепків — випало 17"], Lines());
        k.Bet(0, "black", 5000);
        k.Round(2);
        Assert.Single(Lines());   // не частіше раз на 10 хв
        k.S.JournalAt = k.H.Clock.UtcNow.AddMinutes(-10);
        k.Bet(0, "black", 3000);
        k.Round(2);
        Assert.Equal(2, Lines().Count);
        Assert.Equal("🎡 Рулетка: Оля виграє 3000 черепків — випало 2", Lines()[1]);
        k.S.JournalAt = k.H.Clock.UtcNow.AddMinutes(-10);
        k.Bet(0, "red", 1999);
        k.Round(1);
        Assert.Equal(2, Lines().Count);   // дрібниця в Журнал не йде

        var solo = RouletteKit.Solo(wallet: 10_000);
        solo.Bet(0, "straight:4", 50);
        solo.SoloSpin(4);
        Assert.Equal("🎡 Рулетка сам на сам: Оля виграє 1750 черепків — випало 4",
            solo.H.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void Draw_is_uniform_over_37_pockets()
    {
        var k = RouletteKit.Table(("Оля", 1000));
        var counts = new int[37];
        lock (k.H.Room.Sync)
            for (var i = 0; i < 37_000; i++) counts[k.G.Draw()]++;
        Assert.All(counts, c => Assert.InRange(c, 800, 1200));
        k.G.Rig = () => 37;
        Assert.Throws<GameError>(() => k.G.Draw());
        k.G.Rig = () => -1;
        Assert.Throws<GameError>(() => k.G.Draw());
    }

    [Fact]
    public void Deterministic_with_the_same_seed()
    {
        string Play()
        {
            var k = RouletteKit.Table(("Оля", 5000), ("Петро", 5000));
            var texts = new List<string>();
            for (var round = 0; round < 4; round++)
            {
                k.Bet(0, "red", 10 + round);
                k.Bet(1, $"straight:{round * 7}", 5);
                k.To(Spin);
                texts.Add(Views.Text(k.View(0)));
                k.To(Result);
                texts.Add(Views.Text(k.View(1)));
                k.To(Bets);
            }
            return string.Join("\n", texts);
        }
        Assert.Equal(Play(), Play());
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Thousand_ticks_with_eight_players_under_a_second()
    {
        var people = Enumerable.Range(1, 8).Select(i => ($"Гравець{i}", 1_000_000)).ToArray();
        var k = RouletteKit.Table(people);
        var sw = Stopwatch.StartNew();
        for (var t = 0; t < 1000; t++)
        {
            if (k.S.Phase == Bets && t % 4 == 0)
                for (var seat = 0; seat < 8; seat++) k.Bet(seat, RouletteCore.All[(t + seat) % RouletteCore.All.Count], 1);
            k.H.Tick();
            if (t % 10 == 0) _ = k.G.View(t % 8);
        }
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1000, $"{sw.ElapsedMilliseconds} мс");
        Assert.True(k.S.History.Count >= 5);
    }
}
