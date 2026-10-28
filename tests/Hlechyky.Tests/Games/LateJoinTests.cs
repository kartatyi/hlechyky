using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Tests.Support;
using Xunit;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Заглушка довгої гри (як вечірка): відпалий повертається посеред партії (LateJoin/OnJoin), а порожній стіл чекає
/// HoldEmpty. OnLeave партії не кінчає — інакше повертатись не було б куди.
/// </summary>
public sealed class TestLateJoin : Game
{
    public override GameInfo Info { get; } = new(
        "t-latejoin", "Тестове повернення", "тестове повернення", GameGroup.Party, 2, 4, Start: StartMode.ByHost);

    public readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Calls = [];

    public override void Start()
    {
        for (var s = 0; s < Info.MaxPlayers; s++) if (Ctx.NickOf(s) is { } n) Known.Add(n);
    }

    public override bool LateJoin(string nick) => Known.Contains(nick);
    public override void OnJoin(int seat) => Calls.Add($"join {seat} {Ctx.NickOf(seat)}");
    public override void OnLeave(int seat) => Calls.Add($"leave {seat} {Ctx.NickOf(seat)}");
    public override TimeSpan HoldEmpty => TimeSpan.FromMinutes(15);

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action == "end") Ctx.Finish([seat], "кінець");
        return ActResult.Done;
    }

    public override object View(int? seat) => new { seat };
}

/// <summary>
/// Заглушка гри, що тримає крісло за ніком (<see cref="Game.Rejoin"/>, як «Хутори»), а новеньких пускає лише Хому
/// (<see cref="Game.LateJoin"/> — на перше вільне).
/// </summary>
public sealed class TestRejoin : Game
{
    public override GameInfo Info { get; } = new(
        "t-rejoin", "Тестове своє місце", "тестове своє місце", GameGroup.Board, 2, 4, Start: StartMode.ByHost);

    readonly Dictionary<string, int> _own = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Calls = [];

    public override void Start()
    {
        for (var s = 0; s < Info.MaxPlayers; s++) if (Ctx.NickOf(s) is { } n) _own[n] = s;
    }

    public override int? Rejoin(string nick) => _own.TryGetValue(nick, out var s) ? s : null;
    public override bool LateJoin(string nick) => nick == "Хома";
    public override void OnJoin(int seat) => Calls.Add($"join {seat} {Ctx.NickOf(seat)}");
    public override void OnLeave(int seat) => Calls.Add($"leave {seat} {Ctx.NickOf(seat)}");
    public override object View(int? seat) => new { seat };
}

public sealed class LateJoinTests
{
    static RoomHarness Table(string game, params string[] nicks)
    {
        var h = new RoomHarness(game);
        foreach (var n in nicks) h.Join(n);
        Assert.True(h.Start().Ok);
        return h;
    }

    static void Gone(RoomHarness h, string nick)
    {
        h.Rooms.NoteOffline(nick, h.Clock.UtcNow);
        h.Clock.Advance(TimeSpan.FromSeconds(21));
        h.Rooms.DropIfGone(h.Clock.UtcNow);
    }

    [Fact]
    public void Without_override_joining_mid_game_is_refused_as_before()
    {
        var h = Table("t-party", "Оля", "Петро", "Іван");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        var r = h.Rooms.Join(h.RoomId, "Хома");
        Assert.False(r.Reply.Ok);
        Assert.Contains("місць", r.Reply.Message);
    }

    [Fact]
    public void Without_override_an_empty_playing_room_disappears_at_once()
    {
        var h = new RoomHarness("t-party");
        h.Join("Оля"); h.Join("Петро");
        h.Start();
        var id = h.RoomId;
        h.Leave("Оля"); h.Leave("Петро");
        Assert.Null(h.Rooms.Find(id));
    }

    [Fact]
    public void Gone_player_comes_back_to_another_seat_and_the_game_hears_it()
    {
        var h = Table("t-latejoin", "Оля", "Петро", "Іван");
        var g = (TestLateJoin)h.Room.Game;
        Gone(h, "Петро");
        Assert.Contains("leave 1 Петро", g.Calls);
        Assert.Null(h.Room.Seats[1]);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);

        // Іван теж відпав і повертається першим — на перше вільне крісло, колишнє Петрове
        Gone(h, "Іван");
        var r = h.Rooms.Join(h.RoomId, "Іван");
        Assert.True(r.Reply.Ok, r.Reply.Message);
        Assert.Equal("Ти знову за столом", r.Reply.Message);
        Assert.Equal("Іван", h.Room.Seats[1]);   // перше вільне — колишнє Петрове
        Assert.Contains("join 1 Іван", g.Calls);

        Assert.True(h.Rooms.Join(h.RoomId, "Петро").Reply.Ok);
        Assert.Equal("Петро", h.Room.Seats[2]);
        Assert.Contains("join 2 Петро", g.Calls);
    }

    [Fact]
    public void Stranger_is_refused_when_the_game_says_no()
    {
        var h = Table("t-latejoin", "Оля", "Петро", "Іван");
        var r = h.Rooms.Join(h.RoomId, "Чужий");
        Assert.False(r.Reply.Ok);
        Assert.Contains("місць", r.Reply.Message);
        Assert.Empty(((TestLateJoin)h.Room.Game).Calls);
    }

    [Fact]
    public void Already_seated_is_not_a_late_join()
    {
        var h = Table("t-latejoin", "Оля", "Петро");
        Assert.False(h.Rooms.Join(h.RoomId, "Оля").Reply.Ok);
    }

    [Fact]
    public void Empty_room_waits_hold_empty_then_is_swept()
    {
        var h = Table("t-latejoin", "Оля", "Петро");
        var id = h.RoomId;
        h.Leave("Оля");
        h.Leave("Петро");
        Assert.NotNull(h.Rooms.Find(id));
        Assert.Equal(0, h.Room.Occupied);

        h.Clock.Advance(TimeSpan.FromMinutes(14));
        h.Tick(5);   // тик не продовжує життя
        h.Rooms.Housekeeping(h.Clock.UtcNow);
        Assert.NotNull(h.Rooms.Find(id));

        h.Clock.Advance(TimeSpan.FromMinutes(2));
        h.Rooms.Housekeeping(h.Clock.UtcNow);
        Assert.Null(h.Rooms.Find(id));
    }

    [Fact]
    public void Coming_back_to_an_empty_room_keeps_it_alive()
    {
        var h = Table("t-latejoin", "Оля", "Петро");
        var id = h.RoomId;
        Gone(h, "Оля");
        Gone(h, "Петро");
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(h.Rooms.Join(id, "Оля").Reply.Ok);
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        h.Rooms.Housekeeping(h.Clock.UtcNow);
        Assert.NotNull(h.Rooms.Find(id));
    }

    [Fact]
    public void Finished_empty_room_is_swept_as_usual()
    {
        var h = Table("t-latejoin", "Оля", "Петро");
        var id = h.RoomId;
        h.Act(0, "end");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        h.Leave("Оля"); h.Leave("Петро");
        Assert.Null(h.Rooms.Find(id));
    }

    [Fact]
    public void Rejoin_puts_the_nick_back_on_its_own_seat_not_the_first_free_one()
    {
        var h = Table("t-rejoin", "Оля", "Петро", "Іван");
        var g = (TestRejoin)h.Room.Game;
        Gone(h, "Петро");
        Gone(h, "Іван");
        var r = h.Rooms.Join(h.RoomId, "Іван");
        Assert.True(r.Reply.Ok, r.Reply.Message);
        Assert.Equal("Іван", h.Room.Seats[2]);   // перше вільне — Петрове (1), але Іванове — 2
        Assert.Null(h.Room.Seats[1]);
        Assert.Contains("join 2 Іван", g.Calls);
    }

    [Fact]
    public void Rejoin_never_takes_an_occupied_seat()
    {
        var h = Table("t-rejoin", "Оля", "Петро", "Іван");
        Gone(h, "Петро");
        Assert.True(h.Rooms.Join(h.RoomId, "Хома").Reply.Ok);   // новенький — на перше вільне, Петрове
        Assert.Equal("Хома", h.Room.Seats[1]);
        // Своє крісло зайняте, а на інше гра Петра не пускає — звичайне «місць нема», Хома сидить.
        var r = h.Rooms.Join(h.RoomId, "Петро");
        Assert.False(r.Reply.Ok);
        Assert.Contains("місць", r.Reply.Message);
        Assert.Equal("Хома", h.Room.Seats[1]);
        Assert.Null(h.Room.Seats[3]);
    }
}
