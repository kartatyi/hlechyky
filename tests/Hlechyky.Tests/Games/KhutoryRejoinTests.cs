using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Хутори»: хто випав посеред партії (Grace минув) чи встав, сідає назад тим самим ніком на СВОЄ місце — з тією ж рукою
/// (<see cref="Game.Rejoin"/> → <c>Rooms.LateSeat</c>). Чужий нік чужого хутора не займе.
/// </summary>
public class KhutoryRejoinTests
{
    static readonly string[] Crew = ["Оля", "Петро", "Іра", "Влад"];

    static RoomHarness Table(int people, int seed = 5)
    {
        var h = new RoomHarness("khutory", new { map = "starter", timer = "60", bots = "0" }, seed: seed);
        for (var i = 0; i < people; i++) h.Join(Crew[i]);
        Assert.True(h.Start().Ok);
        return h;
    }

    static KhutoryCore CoreOf(RoomHarness h) => ((Khutory)h.Room.Game).Core;

    /// <summary>Телефон заснув: останнє з'єднання закрилось, Grace (20 с) минув — каркас звільняє місце.</summary>
    static void Gone(RoomHarness h, string nick)
    {
        h.Rooms.NoteOffline(nick, h.Clock.UtcNow);
        h.Clock.Advance(TimeSpan.FromSeconds(21));
        h.Rooms.DropIfGone(h.Clock.UtcNow);
    }

    /// <summary>Один крок столу від місця <paramref name="by"/>: штовхнути Глека, а нема на кого — минув строк.</summary>
    static void Step(RoomHarness h, int by)
    {
        var v = h.View(by);
        if (v.GetProperty("botIn").ValueKind == JsonValueKind.Number)
        {
            h.Clock.AdvanceMs(Math.Max(BoardBots.ThinkMs, v.GetProperty("botIn").GetInt32()));
            Assert.True(h.Act(by, BoardBots.Nudge).Ok);
        }
        else
        {
            h.Clock.Advance(61);
            Assert.True(h.Act(by, "timeout").Ok);
        }
    }

    static int[] HandOf(JsonElement me) => [.. KhutoryBoard.Res.Select(r => me.GetProperty("hand").GetProperty(r).GetInt32())];

    static int[] TownsOf(KhutoryCore c, int seat) => [.. Enumerable.Range(0, KhutoryGeo.Verts).Where(v => c.VOwner[v] == seat)];

    [Fact]
    public void Dropped_player_comes_back_to_own_seat_with_the_same_hand_after_the_bot_played()
    {
        var h = Table(3);
        var core = CoreOf(h);
        Gone(h, "Петро");
        Assert.Null(h.Room.Seats[1]);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        var away = h.View(0).GetProperty("players")[1];
        Assert.True(away.GetProperty("away").GetBoolean());
        Assert.Equal("Петро", away.GetProperty("nick").GetString());

        // Партія йде без нього: Глек розставляє Петрові обидві хати, друга дає перші карти.
        for (var i = 0; core.Phase == KhPhase.Setup; i++)
        {
            Assert.True(i < 200, "розстановка не кінчається");
            Step(h, 0);
        }
        Assert.Equal(2, core.TownsOf(1));
        var hand = (int[])core.Hand[1].Clone();
        Assert.True(hand.Sum() > 0);
        var towns = TownsOf(core, 1);

        var said = h.Outbox.OfType<TableSaid>().Count();
        Assert.True(h.Join("Петро").Ok);
        Assert.Contains("хутір", h.Reply.Message);
        Assert.Equal("Петро", h.Room.Seats[1]);
        var me = h.View(1).GetProperty("me");
        Assert.Equal(1, me.GetProperty("seat").GetInt32());
        Assert.Equal(hand, HandOf(me));
        Assert.Equal(towns, TownsOf(core, 1));
        var back = h.View(0);
        Assert.False(back.GetProperty("players")[1].GetProperty("away").GetBoolean());
        Assert.Contains(back.GetProperty("log").EnumerateArray(), l => l.GetString()!.Contains("Петро повертається на свій хутір"));
        // Глек віддає кермо в балачці столу (Back).
        Assert.Contains(h.Outbox.OfType<TableSaid>().Skip(said), s => s.Line.Text.Contains("Петро"));

        // Далі Петро ходить сам: на його кидок Глек уже не чекає.
        for (var i = 0; !(core.Turn == 1 && core.Phase == KhPhase.Roll); i++)
        {
            Assert.True(i < 400, "черга Петра не настає");
            Step(h, 0);
        }
        Assert.False(h.View(1).GetProperty("players")[1].GetProperty("away").GetBoolean());
        Assert.Equal(JsonValueKind.Null, h.View(1).GetProperty("botIn").ValueKind);
        Assert.True(h.Act(1, "roll").Ok);
    }

    [Fact]
    public void Stranger_cannot_take_someone_elses_farm()
    {
        var h = Table(3);
        Gone(h, "Петро");
        var stranger = h.Rooms.Join(h.RoomId, "Хома");
        Assert.False(stranger.Reply.Ok);
        Assert.Equal("От халепа — місць уже нема", stranger.Reply.Message);
        Assert.Null(h.Room.Seats[1]);
        // Хто вже сидить, на чуже крісло не пересяде.
        Assert.False(h.Rooms.Join(h.RoomId, "Оля").Reply.Ok);
        Assert.Equal("Оля", h.Room.Seats[0]);
        Assert.Null(h.Room.Seats[1]);
        Assert.True(h.View(0).GetProperty("players")[1].GetProperty("away").GetBoolean());
        // А господар — сідає.
        Assert.True(h.Join("Петро").Ok);
        Assert.Equal("Петро", h.Room.Seats[1]);
    }

    [Fact]
    public void Each_returns_to_own_seat_not_the_first_free_one()
    {
        var h = Table(4);
        h.Leave("Петро");
        h.Leave("Іра");
        Assert.Empty(h.Finished);
        // Вільне перше — місце 1 (Петрове), але Іра сідає на своє 2-ге.
        Assert.True(h.Join("Іра").Ok);
        Assert.Equal("Іра", h.Room.Seats[2]);
        Assert.Null(h.Room.Seats[1]);
        Assert.Equal(2, h.View(2).GetProperty("me").GetProperty("seat").GetInt32());
        Assert.True(h.Join("Петро").Ok);
        Assert.Equal("Петро", h.Room.Seats[1]);
        Assert.All(h.View(0).GetProperty("players").EnumerateArray(), p => Assert.False(p.GetProperty("away").GetBoolean()));
        // Удруге не сяде: уже за столом.
        Assert.False(h.Rooms.Join(h.RoomId, "Петро").Reply.Ok);
    }

    [Fact]
    public void Seat_held_by_a_real_bot_is_not_anyones_to_take()
    {
        var h = new RoomHarness("khutory", new { map = "starter", timer = "60", bots = "2" }, seed: 5);
        h.Join("Оля");
        h.Join("Петро");
        Assert.True(h.Start().Ok);
        var game = (Khutory)h.Room.Game;
        lock (h.Room.Sync)
        {
            Assert.Null(game.Rejoin("Оля"));   // сидить — повертатись нема куди
            Assert.Null(game.Rejoin("🤖 Глек"));
        }
        Gone(h, "Петро");
        lock (h.Room.Sync) Assert.Equal(1, game.Rejoin("ПЕТРО"));
    }
}
