using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using static Hlechyky.Games.Impl.SolyanyiMap;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Соляний шлях»: час на хід (timeout — Глек доробляє хід, двічі — away, своя дія знімає), фази draw/keep із пільгою 15 с,
/// setup із 60 с, вихід (за людину — Глек), пауза без людей і повернення тим самим ніком на свою валку (контракт §8).
/// </summary>
public class SolyanyiRejoinTests
{
    static readonly JsonElement NoPayload = JsonDocument.Parse("{}").RootElement;

    /// <summary>Телефон заснув: останнє з'єднання закрилось, Grace минув — каркас звільняє місце.</summary>
    static void Gone(RoomHarness h, string nick)
    {
        h.Rooms.NoteOffline(nick, h.Clock.UtcNow);
        h.Clock.Advance(Rooms.TurnGrace + TimeSpan.FromSeconds(1));
        h.Rooms.DropIfGone(h.Clock.UtcNow);
    }

    static double Left(RoomHarness h, JsonElement v) => (v.GetProperty("deadline").GetDateTimeOffset() - h.Clock.UtcNow).TotalSeconds;

    static bool Away(RoomHarness h, int seat) =>
        h.View(null).GetProperty("players").EnumerateArray().Single(p => p.GetProperty("seat").GetInt32() == seat).GetProperty("away").GetBoolean();

    static IEnumerable<string> Log(RoomHarness h) => h.View(null).GetProperty("log").EnumerateArray().Select(l => l.GetString()!);

    /// <summary>Людина, чий хід, грає пораду Глека.</summary>
    static void Tip(RoomHarness h, int seat) => Assert.True(SlRig.PlayTip(h, seat, h.View(seat).GetProperty("me").GetProperty("tip")).Ok);

    /// <summary>
    /// Без таймера у setup: А встав (обрав за нього Глек), Б відпав, не обравши, і був останнім — за нього ніхто не обрав (пауза). А
    /// вертається: тепер Глек обирає за всіх, хто ще не обрав і не сидить, інакше стіл стояв би вічно (§8).
    /// </summary>
    [Fact]
    public void Setup_without_a_timer_does_not_deadlock_when_the_last_human_left_unready()
    {
        var h = SlRig.Table(3, timer: "none");
        var c = SlRig.CoreOf(h);
        var (a, b, d) = (h.NickOf(0)!, h.NickOf(1)!, h.NickOf(2)!);
        Assert.True(h.Act(2, "keep", new { ids = c.Offer[2].Take(2).ToArray() }).Ok);
        Gone(h, d);
        Gone(h, a);
        Assert.True(c.Ready[0]);    // поруч сидів Б — Глек обрав за А одразу
        Assert.False(c.Ready[1]);
        Gone(h, b);                 // останній, ще не обрав: пауза, обирати нема кому
        Assert.False(c.Ready[1]);
        Assert.Equal(SlPhase.Setup, c.Phase);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);

        Assert.True(h.Join(a).Ok);
        Assert.True(c.Ready[1]);
        Assert.Equal(SlPhase.Play, c.Phase);
        Assert.Contains(c.Events, e => e.K == "round");
        // Той, хто щойно сів, обирати не мусив: він обрав раніше; нікого не лишилось у setup.
        Assert.All(c.Order, s => Assert.True(c.Ready[s]));

        // Партія справді йде далі: Глек за відсутніх ходить за поштовхом А.
        var turn0 = c.Turns;
        for (var i = 0; i < 6; i++)
        {
            var seat = c.Turn;
            if (seat == 0) { Tip(h, 0); continue; }
            h.Clock.AdvanceMs(h.View(0).GetProperty("botIn").GetInt32());
            Assert.True(h.Act(0, BoardBots.Nudge).Ok);
        }
        Assert.True(c.Turns > turn0);
    }

    /// <summary>Той, хто вернувся сам ще не обравши, обирає сам: Глек не лізе за нього.</summary>
    [Fact]
    public void Setup_rejoin_leaves_the_returning_players_own_choice_alone()
    {
        var h = SlRig.Table(2, timer: "none");
        var c = SlRig.CoreOf(h);
        var (a, b) = (h.NickOf(0)!, h.NickOf(1)!);
        Gone(h, a);                 // за А обрав Глек, бо Б сидить
        Assert.True(c.Ready[0]);
        Gone(h, b);                 // Б останній, не обрав
        Assert.False(c.Ready[1]);
        Assert.True(h.Join(b).Ok);
        Assert.False(c.Ready[1]);
        Assert.Equal(SlPhase.Setup, c.Phase);
        Assert.Equal("setup", h.View(1).GetProperty("me").GetProperty("must").GetString());
        Assert.True(h.Act(1, "keep", new { ids = c.Offer[1].Take(2).ToArray() }).Ok);
        Assert.Equal(SlPhase.Play, c.Phase);
    }

    [Fact]
    public void Timeout_waits_for_the_deadline_then_the_bot_finishes_the_turn_and_twice_means_away()
    {
        var h = SlRig.Play(2);
        var c = SlRig.CoreOf(h);
        var s = c.Turn;
        var o = c.Next(s);
        var nick = h.NickOf(s);
        Assert.Equal(60, Left(h, h.View(s)), 3);
        h.Clock.Advance(59);
        Assert.Equal("Ще є час", h.Act(o, "timeout").Message);
        h.Clock.Advance(2);
        Assert.True(h.Act(o, "timeout").Ok);
        Assert.Equal(o, c.Turn);
        Assert.Equal(1, c.Turns);
        Assert.Contains($"{nick} задрімав — Глек їде за нього", Log(h));
        Assert.False(Away(h, s));
        // Новий хід — новий строк.
        Assert.Equal(60, Left(h, h.View(o)), 3);
        Tip(h, o);
        while (c.Turn != s) Tip(h, c.Turn);
        h.Clock.Advance(61);
        var said = h.Outbox.OfType<TableSaid>().Count();
        Assert.True(h.Act(o, "timeout").Ok);
        Assert.True(Away(h, s));
        Assert.Contains(h.Outbox.OfType<TableSaid>().Skip(said), t => t.Line.Text.Contains(nick));
        // За задрімалого — Глек, але з пільгою 5 с: раптом повернеться.
        while (c.Turn != s) Tip(h, c.Turn);
        Assert.Equal(Solyanyi.AwayGraceMs, h.View(o).GetProperty("botIn").GetInt32());
        Assert.Equal("Глек ще думає", h.Act(o, BoardBots.Nudge).Message);
        // Своя дія знімає away.
        Tip(h, s);
        Assert.False(Away(h, s));
        Assert.Contains($"{nick} знову веде валку сам", Log(h));
    }

    [Fact]
    public void Own_action_between_timeouts_resets_the_counter()
    {
        var h = SlRig.Play(2);
        var c = SlRig.CoreOf(h);
        var s = c.Turn;
        var o = c.Next(s);
        h.Clock.Advance(61);
        Assert.True(h.Act(o, "timeout").Ok);
        while (c.Turn != s) Tip(h, c.Turn);
        while (c.Turn == s) Tip(h, s);
        while (c.Turn != s) Tip(h, c.Turn);
        h.Clock.Advance(61);
        Assert.True(h.Act(o, "timeout").Ok);
        Assert.False(Away(h, s));
    }

    [Fact]
    public void Timeout_finishes_the_second_pick_and_the_ticket_choice()
    {
        var h = SlRig.Play(2);
        var c = SlRig.CoreOf(h);
        var s = c.Turn;
        var o = c.Next(s);
        SlRig.Market(c, Salt, Fish, Grain, Tar, Honey);
        var hand = c.Hand[s].Sum();
        Assert.True(h.Act(s, "draw", new { deck = true }).Ok);
        Assert.Equal(SlPhase.Draw, c.Phase);
        h.Clock.Advance(61);
        Assert.True(h.Act(o, "timeout").Ok);
        Assert.Equal(o, c.Turn);
        Assert.Equal(hand + 2, c.Hand[s].Sum());

        var kept = c.Tickets[o].Count;
        Assert.True(h.Act(o, "tickets").Ok);
        Assert.Equal(SlPhase.Keep, c.Phase);
        h.Clock.Advance(61);
        Assert.True(h.Act(s, "timeout").Ok);
        Assert.Equal(s, c.Turn);
        Assert.Equal(SlPhase.Play, c.Phase);
        Assert.True(c.Tickets[o].Count > kept);
        Assert.Empty(c.Offer[o]);
    }

    [Fact]
    public void Draw_and_keep_phases_get_at_least_fifteen_seconds_and_never_more_than_the_timer()
    {
        var h = SlRig.Play(2);
        var c = SlRig.CoreOf(h);
        var s = c.Turn;
        var o = c.Next(s);
        h.Clock.Advance(55);
        Assert.True(h.Act(s, "draw", new { deck = true }).Ok);
        var v = h.View(s);
        Assert.Equal(15, Left(h, v), 3);
        Assert.Equal(Solyanyi.BackMs, v.GetProperty("timerMs").GetInt32());
        h.Clock.Advance(14);
        Assert.Equal("Ще є час", h.Act(o, "timeout").Message);
        Tip(h, s);
        Assert.Equal(o, c.Turn);
        Assert.Equal(60, Left(h, h.View(s)), 3);
        h.Clock.Advance(50);
        Assert.True(h.Act(o, "tickets").Ok);
        Assert.Equal(15, Left(h, h.View(o)), 3);
        Tip(h, o);
        // У межах того самого строку, якщо там ще досить часу.
        Assert.True(h.Act(s, "draw", new { deck = true }).Ok);
        Assert.Equal(60, Left(h, h.View(s)), 3);
        Assert.Equal(60000, h.View(s).GetProperty("timerMs").GetInt32());
    }

    [Fact]
    public void Setup_gets_sixty_seconds_or_the_timer_and_timeout_chooses_for_everyone_late()
    {
        foreach (var (timer, want) in new[] { ("45", 60), ("60", 60), ("90", 90) })
            Assert.Equal(want, Left(SlRig.Table(2, timer: timer), SlRig.Table(2, timer: timer).View(0)), 3);
        var h = SlRig.Table(3);
        var c = SlRig.CoreOf(h);
        var s = c.First;
        var late1 = c.Next(s);
        var late2 = c.Next(late1);
        Assert.True(h.Act(s, "keep", new { ids = c.Offer[s].ToArray() }).Ok);
        h.Clock.Advance(59);
        Assert.Equal("Ще є час", h.Act(s, "timeout").Message);
        h.Clock.Advance(2);
        Assert.True(h.Act(s, "timeout").Ok);
        Assert.Equal(SlPhase.Play, c.Phase);
        Assert.True(c.Tickets[late1].Count >= 2);
        Assert.True(c.Tickets[late2].Count >= 2);
        Assert.Contains($"{h.NickOf(late1)} задрімав — Глек їде за нього", c.Log);
        Assert.Contains($"{h.NickOf(late2)} задрімав — Глек їде за нього", c.Log);
        Assert.DoesNotContain($"{h.NickOf(s)} задрімав — Глек їде за нього", c.Log);
        // Строк ходу першого гравця — новий.
        Assert.Equal(60, Left(h, h.View(c.Turn)), 3);
    }

    [Fact]
    public void Without_a_timer_nobody_times_out()
    {
        var h = SlRig.Play(2, timer: "none");
        var v = h.View(0);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("deadline").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("timerMs").ValueKind);
        h.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal("Тут без часу", h.Act(0, "timeout").Message);
        Assert.Equal(JsonValueKind.Null, SlRig.Table(2, timer: "none").View(0).GetProperty("deadline").ValueKind);
    }

    [Fact]
    public void Nudging_the_bot_waits_for_its_thinking_time_and_does_the_whole_turn()
    {
        var h = SlRig.Play(1, bots: "1");
        var c = SlRig.CoreOf(h);
        while (c.Turn != 1) Tip(h, 0);
        Assert.Equal("Глек ще думає", h.Act(0, BoardBots.Nudge).Message);
        Assert.Equal(BoardBots.ThinkMs, h.View(0).GetProperty("botIn").GetInt32());
        h.Clock.AdvanceMs(BoardBots.ThinkMs);
        var turns = c.Turns;
        Assert.True(h.Act(0, BoardBots.Nudge).Ok);
        Assert.Equal(turns + 1, c.Turns);
        Assert.Equal(0, c.Turn);
        Assert.Equal(SlPhase.Play, c.Phase);
        Assert.Equal("Глекам нема що робити", h.Act(0, BoardBots.Nudge).Message);
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("botIn").ValueKind);
        // У setup Глекам робити нічого.
        var s = SlRig.Table(1, bots: "1");
        Assert.Equal(JsonValueKind.Null, s.View(0).GetProperty("botIn").ValueKind);
        s.Clock.Advance(5);
        Assert.Equal("Глекам нема що робити", s.Act(0, BoardBots.Nudge).Message);
    }

    [Fact]
    public void Leaving_person_is_replaced_by_the_bot_and_comes_back_with_the_same_caravan()
    {
        var h = SlRig.Play(3);
        var c = SlRig.CoreOf(h);
        for (var i = 0; i < 6; i++) Tip(h, c.Turn);
        while (c.Turn != 1) Tip(h, c.Turn);
        Tip(h, 1);
        while (c.Phase != SlPhase.Play) Tip(h, c.Turn);
        Gone(h, "Петро");
        Assert.Null(h.Room.Seats[1]);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.True(Away(h, 1));
        Assert.Contains("Глек сідає замість Петро", Log(h));
        // Партія йде без нього: на його ході Глек ходить за поштовхом.
        for (var i = 0; i < 12; i++)
        {
            if (c.Turn == 1 && c.Phase == SlPhase.Play)
            {
                var v = h.View(0);
                h.Clock.AdvanceMs(v.GetProperty("botIn").GetInt32());
                Assert.True(h.Act(0, BoardBots.Nudge).Ok);
            }
            else Tip(h, c.Turn);
        }
        var hand = c.Hand[1].ToArray();
        var tickets = c.Tickets[1].ToArray();
        var taken = c.Taken[1].ToArray();
        var wagons = c.Wagons[1];
        Assert.True(h.Join("Петро").Ok);
        Assert.Contains("Твоя валка на місці", h.Reply.Message);
        Assert.Equal("Петро", h.Room.Seats[1]);
        Assert.False(Away(h, 1));
        Assert.Contains("Петро повертається до своєї валки", Log(h));
        var me = h.View(1).GetProperty("me");
        Assert.Equal(1, me.GetProperty("seat").GetInt32());
        Assert.Equal(hand, c.Hand[1]);
        Assert.Equal(tickets, c.Tickets[1]);
        Assert.Equal(taken, c.Taken[1]);
        Assert.Equal(wagons, c.Wagons[1]);
        Assert.Equal(tickets, me.GetProperty("tickets").EnumerateArray().Select(t => t.GetProperty("id").GetInt32()));
    }

    [Fact]
    public void Leaving_in_setup_the_bot_chooses_at_once_and_the_person_sees_the_choice_on_return()
    {
        var h = SlRig.Table(3);
        var c = SlRig.CoreOf(h);
        Assert.False(c.Ready[1]);
        Gone(h, "Петро");
        Assert.True(c.Ready[1]);
        Assert.True(c.Tickets[1].Count >= 2);
        Assert.Empty(c.Offer[1]);
        Assert.Contains("Глек сідає замість Петро", Log(h));
        Assert.Equal(SlPhase.Setup, c.Phase);
        Assert.True(h.Join("Петро").Ok);
        var me = h.View(1).GetProperty("me");
        Assert.Equal(c.Tickets[1], me.GetProperty("tickets").EnumerateArray().Select(t => t.GetProperty("id").GetInt32()));
        Assert.Equal(JsonValueKind.Null, me.GetProperty("must").ValueKind);
    }

    [Fact]
    public void Coming_back_during_keep_shows_the_offer_again()
    {
        var h = SlRig.Play(2, bots: "1");
        var c = SlRig.CoreOf(h);
        while (c.Turn != 1) SlRig.Step(h, 0);
        Assert.True(h.Act(1, "tickets").Ok);
        var offer = c.Offer[1].ToArray();
        h.Rooms.NoteOffline("Петро", h.Clock.UtcNow);
        h.Clock.Advance(Rooms.TurnGrace + TimeSpan.FromSeconds(1));
        // Каркас чекає Grace; місце ще за ним, поки DropIfGone не звільнив, — тож вид той самий.
        h.Rooms.DropIfGone(h.Clock.UtcNow);
        Assert.True(Away(h, 1));
        Assert.Equal(offer, c.Offer[1]);
        Assert.True(h.Join("Петро").Ok);
        var me = h.View(1).GetProperty("me");
        Assert.Equal("keep", me.GetProperty("must").GetString());
        Assert.Equal(offer, me.GetProperty("offer").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()));
    }

    [Fact]
    public void Stranger_cannot_take_someone_elses_caravan_and_a_real_bot_seat_is_nobodys()
    {
        var h = SlRig.Play(3);
        Gone(h, "Петро");
        Assert.False(h.Rooms.Join(h.RoomId, "Хома").Reply.Ok);
        Assert.Null(h.Room.Seats[1]);
        var game = SlRig.GameOf(h);
        lock (h.Room.Sync)
        {
            Assert.Null(game.Rejoin("Оля"));
            Assert.Equal(1, game.Rejoin("ПЕТРО"));
        }
        var b = SlRig.Play(1, bots: "2");
        lock (b.Room.Sync) Assert.Null(SlRig.GameOf(b).Rejoin("Глек 🤖"));
    }

    [Fact]
    public void Last_person_dropping_pauses_the_game_and_coming_back_gives_a_fresh_deadline()
    {
        var h = SlRig.Play(1, bots: "3");
        var c = SlRig.CoreOf(h);
        var seq = c.Seq;
        var said = h.Outbox.OfType<TableSaid>().Count();
        Gone(h, "Оля");
        Assert.NotNull(h.Rooms.Find(h.RoomId));
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Empty(h.Finished);
        Assert.True(Away(h, 0));
        Assert.Equal(said, h.Outbox.OfType<TableSaid>().Count());
        // Пауза: штовхнути Глеків чи строк нікому, хвилини минають — нічого не міняється.
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.False(h.Rooms.Act(h.RoomId, "Оля", BoardBots.Nudge, NoPayload).Reply.Ok);
        Assert.False(h.Rooms.Act(h.RoomId, "Оля", "timeout", NoPayload).Reply.Ok);
        h.Rooms.Housekeeping(h.Clock.UtcNow);
        Assert.NotNull(h.Rooms.Find(h.RoomId));
        Assert.Equal(seq, c.Seq);

        Assert.True(h.Join("Оля").Ok);
        Assert.Equal("Оля", h.Room.Seats[0]);
        var v = h.View(0);
        Assert.False(Away(h, 0));
        Assert.True(Left(h, v) > 30, $"строк після повернення {Left(h, v)} с");
        Assert.Equal("Ще є час", h.Act(0, "timeout").Message);
        // А стіл без людей довше за 10 хв — зникає.
        var g = SlRig.Play(1, bots: "1");
        Gone(g, "Оля");
        g.Clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        g.Rooms.Housekeeping(g.Clock.UtcNow);
        Assert.Null(g.Rooms.Find(g.RoomId));
    }

    [Fact]
    public void Last_person_dropping_in_setup_keeps_the_offer_for_the_return()
    {
        var h = SlRig.Table(1, bots: "1");
        var c = SlRig.CoreOf(h);
        var offer = c.Offer[0].ToArray();
        Gone(h, "Оля");
        Assert.False(c.Ready[0]);
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True(h.Join("Оля").Ok);
        var v = h.View(0);
        Assert.Equal("setup", v.GetProperty("me").GetProperty("must").GetString());
        Assert.Equal(offer, v.GetProperty("me").GetProperty("offer").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()));
        Assert.True(Left(h, v) > 30);
    }

    [Fact]
    public void Last_person_standing_up_by_the_button_takes_the_table_away()
    {
        var h = SlRig.Play(1, bots: "2");
        h.Leave("Оля");
        Assert.Null(h.Rooms.Find(h.RoomId));
        Assert.Empty(h.Finished);
    }

    [Fact]
    public void Leaving_by_the_button_mid_game_hands_the_caravan_to_the_bot()
    {
        var h = SlRig.Play(2, bots: "1");
        h.Leave("Петро");
        Assert.True(Away(h, 1));
        Assert.Contains(h.Outbox.OfType<Journal>(), j => j.Text.Contains("Петро встає — за нього їде Глек"));
        var c = SlRig.CoreOf(h);
        for (var i = 0; i < 1500 && h.Finished.Count == 0; i++) SlRig.Step(h, 0);
        Assert.Single(h.Finished);
        Assert.True(c.Taken[1].Count > 0);
        SolyanyiBotTests.Invariants(c);
        Assert.Equal(DeckSize, c.Deck.Count + c.Discard.Count + c.Market.Count(m => m >= 0) + c.Order.Sum(s => c.Hand[s].Sum()));
    }

    [Fact]
    public void Achievements_skip_away_people_and_bots()
    {
        var h = SlRig.Play(2, bots: "1");
        var c = SlRig.CoreOf(h);
        h.Leave("Петро");
        c.Biggest[1] = 6;
        c.Biggest[2] = 6;
        c.Biggest[0] = 6;
        for (var i = 0; i < 1500 && h.Finished.Count == 0; i++) SlRig.Step(h, 0);
        Assert.Single(h.Finished);
        var six = h.Awards.Where(a => a.Reason == "ach:sol-six").Select(a => a.Nick).ToArray();
        Assert.Equal(["Оля"], six);
    }
}
