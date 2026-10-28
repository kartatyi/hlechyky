using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using static Hlechyky.Games.Impl.SorochyntsiCards;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Сорочинський ярмарок»: час на хід (timeout — Глек доробляє хід, двічі — away, своя дія знімає), фази discard/guest з
/// пільгою 15 с, вихід (за людину — Глек), пауза без людей і повернення тим самим ніком на свій прилавок (контракт §8).
/// </summary>
public class SorochyntsiRejoinTests
{
    static readonly JsonElement NoPayload = JsonDocument.Parse("{}").RootElement;

    /// <summary>Телефон заснув: останнє з'єднання закрилось, Grace (20 с) минув — каркас звільняє місце.</summary>
    static void Gone(RoomHarness h, string nick)
    {
        h.Rooms.NoteOffline(nick, h.Clock.UtcNow);
        h.Clock.Advance(TimeSpan.FromSeconds(21));
        h.Rooms.DropIfGone(h.Clock.UtcNow);
    }

    static double Left(RoomHarness h, JsonElement v) => (v.GetProperty("deadline").GetDateTimeOffset() - h.Clock.UtcNow).TotalSeconds;

    static bool Away(RoomHarness h, int seat) =>
        h.View(null).GetProperty("players").EnumerateArray().Single(p => p.GetProperty("seat").GetInt32() == seat).GetProperty("away").GetBoolean();

    static IEnumerable<string> Log(RoomHarness h) => h.View(null).GetProperty("log").EnumerateArray().Select(l => l.GetString()!);

    /// <summary>Людина, чий хід, грає підказку Глека.</summary>
    static void Tip(RoomHarness h, int seat) => Assert.True(SoRig.PlayTip(h, seat, h.View(seat).GetProperty("me").GetProperty("tip")).Ok);

    [Fact]
    public void Timeout_waits_for_the_deadline_then_the_bot_finishes_the_turn_and_twice_means_away()
    {
        var h = SoRig.Table(2);
        var c = SoRig.CoreOf(h);
        var s = c.Turn;
        var o = c.Order.First(x => x != s);
        var nick = h.NickOf(s);
        Assert.Equal(60, Left(h, h.View(s)), 3);
        h.Clock.Advance(59);
        Assert.Equal("Ще є час", h.Act(o, "timeout").Message);
        h.Clock.Advance(2);
        Assert.True(h.Act(o, "timeout").Ok);
        Assert.Equal(o, c.Turn);
        Assert.True(c.NTok(s) > 0 || c.Reserved[s].Count > 0 || c.Cards[s].Count > 0);
        Assert.Contains($"{nick} задрімав — Глек торгує за нього", Log(h));
        Assert.False(Away(h, s));
        // Новий хід — новий строк.
        Assert.Equal(60, Left(h, h.View(o)), 3);
        Tip(h, o);
        h.Clock.Advance(61);
        var said = h.Outbox.OfType<TableSaid>().Count();
        Assert.True(h.Act(o, "timeout").Ok);
        Assert.True(Away(h, s));
        Assert.Contains(h.Outbox.OfType<TableSaid>().Skip(said), t => t.Line.Text.Contains(nick));
        // За задрімалого — Глек, але з пільгою 5 с: раптом повернеться.
        Tip(h, o);
        Assert.Equal(s, c.Turn);
        Assert.Equal(Sorochyntsi.AwayGraceMs, h.View(o).GetProperty("botIn").GetInt32());
        // Своя дія знімає away.
        Tip(h, s);
        Assert.False(Away(h, s));
        Assert.Contains($"{nick} знову торгує сам", Log(h));
    }

    [Fact]
    public void Timeout_also_gives_back_extra_tokens_and_picks_the_guest()
    {
        var h = SoRig.Table(2);
        var c = SoRig.CoreOf(h);
        var s = c.Turn;
        var o = c.Order.First(x => x != s);
        c.Table = [0, 5, 7];
        SoRig.Own(c, s, 0, 1, 2, 4, 8, 9, 10, 12, 16, 17, 18);
        SoRig.Hand(c, s, 2, 2, 2, 2, 2);
        Assert.True(h.Act(s, "take", new { goods = new[] { "linen", "fish", "herbs" } }).Ok);
        Assert.Equal(SoPhase.Discard, c.Phase);
        h.Clock.Advance(61);
        Assert.True(h.Act(o, "timeout").Ok);
        Assert.Equal(o, c.Turn);
        Assert.Equal(10, c.NTok(s));
        Assert.Single(c.GuestsOf[s]);
        Assert.Equal(SoPhase.Play, c.Phase);
    }

    [Fact]
    public void Discard_and_guest_phases_get_at_least_fifteen_seconds()
    {
        var h = SoRig.Table(2);
        var c = SoRig.CoreOf(h);
        var s = c.Turn;
        c.Table = [0, 5, 7];
        SoRig.Own(c, s, 0, 1, 2, 4, 8, 9, 10, 12, 16, 17, 18);
        SoRig.Hand(c, s, 2, 2, 2, 2, 2);
        h.Clock.Advance(55);
        Assert.True(h.Act(s, "take", new { goods = new[] { "linen", "fish", "herbs" } }).Ok);
        var v = h.View(s);
        Assert.Equal(15, Left(h, v), 3);
        Assert.Equal(Sorochyntsi.BackMs, v.GetProperty("timerMs").GetInt32());
        h.Clock.Advance(14);
        Assert.Equal("Ще є час", h.Act(c.Order.First(x => x != s), "timeout").Message);
        Tip(h, s);
        Assert.Equal(SoPhase.Guest, c.Phase);
        Assert.Equal(15, Left(h, h.View(s)), 3);
        Tip(h, s);
        Assert.NotEqual(s, c.Turn);
        Assert.Equal(60, Left(h, h.View(s)), 3);
    }

    [Fact]
    public void Without_a_timer_nobody_times_out()
    {
        var h = SoRig.Table(2, timer: "none");
        var v = h.View(0);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("deadline").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("timerMs").ValueKind);
        h.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal("Тут без часу", h.Act(0, "timeout").Message);
        Assert.Equal(45, Left(SoRig.Table(2, timer: "45"), SoRig.Table(2, timer: "45").View(0)), 3);
    }

    [Fact]
    public void Leaving_person_is_replaced_by_the_bot_and_comes_back_with_the_same_stall()
    {
        var h = SoRig.Table(3);
        var c = SoRig.CoreOf(h);
        // Петро (місце 1) встигає забронювати наосліп.
        for (var i = 0; i < 6 && c.Turn != 1; i++) Tip(h, c.Turn);
        Assert.Equal(1, c.Turn);
        Assert.True(h.Act(1, "reserve", new { tier = 3 }).Ok);
        var blind = c.Reserved[1][0].Id;
        Gone(h, "Петро");
        Assert.Null(h.Room.Seats[1]);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.True(Away(h, 1));
        Assert.Contains("Глек сідає замість Петро", Log(h));
        // Партія йде без нього: на його ході Глек ходить за поштовхом.
        for (var i = 0; i < 12; i++)
        {
            if (c.Turn == 1)
            {
                var v = h.View(0);
                h.Clock.AdvanceMs(v.GetProperty("botIn").GetInt32());
                Assert.True(h.Act(0, BoardBots.Nudge).Ok);
            }
            else Tip(h, c.Turn);
        }
        var tokens = (int[])c.Tok[1].Clone();
        var cards = c.Cards[1].ToArray();
        var reserved = c.Reserved[1].ToArray();
        Assert.True(h.Join("Петро").Ok);
        Assert.Contains("прилавок", h.Reply.Message);
        Assert.Equal("Петро", h.Room.Seats[1]);
        Assert.False(Away(h, 1));
        Assert.Contains("Петро повертається до свого прилавка", Log(h));
        var me = h.View(1).GetProperty("me");
        Assert.Equal(1, me.GetProperty("seat").GetInt32());
        Assert.Equal(tokens, c.Tok[1]);
        Assert.Equal(cards, c.Cards[1]);
        Assert.Equal(reserved, c.Reserved[1]);
        Assert.Contains(me.GetProperty("reserved").EnumerateArray(), r => r.GetProperty("id").ValueKind == JsonValueKind.Number && r.GetProperty("id").GetInt32() == blind);
    }

    [Fact]
    public void Stranger_cannot_take_someone_elses_stall_and_a_real_bot_seat_is_nobodys()
    {
        var h = SoRig.Table(3);
        Gone(h, "Петро");
        Assert.False(h.Rooms.Join(h.RoomId, "Хома").Reply.Ok);
        Assert.Null(h.Room.Seats[1]);
        var game = SoRig.GameOf(h);
        lock (h.Room.Sync)
        {
            Assert.Null(game.Rejoin("Оля"));
            Assert.Equal(1, game.Rejoin("ПЕТРО"));
        }
        var b = SoRig.Table(1, bots: "2");
        lock (b.Room.Sync) Assert.Null(SoRig.GameOf(b).Rejoin("Глек 🤖"));
    }

    [Fact]
    public void Last_person_dropping_pauses_the_fair_and_coming_back_gives_a_fresh_deadline()
    {
        var h = SoRig.Table(1, bots: "3");
        var c = SoRig.CoreOf(h);
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
        var g = SoRig.Table(1, bots: "1");
        Gone(g, "Оля");
        g.Clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        g.Rooms.Housekeeping(g.Clock.UtcNow);
        Assert.Null(g.Rooms.Find(g.RoomId));
    }

    [Fact]
    public void Last_person_standing_up_by_the_button_takes_the_table_away()
    {
        var h = SoRig.Table(1, bots: "2");
        h.Leave("Оля");
        Assert.Null(h.Rooms.Find(h.RoomId));
        Assert.Empty(h.Finished);
    }

    [Fact]
    public void Leaving_by_the_button_mid_game_hands_the_stall_to_the_bot()
    {
        var h = SoRig.Table(2, bots: "1");
        h.Leave("Петро");
        Assert.True(Away(h, 1));
        Assert.Contains(h.Outbox.OfType<Journal>(), j => j.Text.Contains("Петро встає — за нього торгує Глек"));
        var c = SoRig.CoreOf(h);
        for (var i = 0; i < 400 && h.Finished.Count == 0; i++) SoRig.Step(h, 0);
        Assert.Single(h.Finished);
        Assert.True(c.Cards[1].Count > 0);
        Assert.Equal(GoldPile, c.Bank0[Gold]);
    }
}
