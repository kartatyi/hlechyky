using System.Reflection;
using Hlechyky.Games;
using Hlechyky.Tests.Support;
using Xunit;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Пакет B аудиту столів: засиджене лобі й покинута партія прибираються, дограні столи поступаються місцем новим,
/// мовчазного господаря можна обійти, заклики з ніка не частіші за паузу, посадка атомарна.
/// </summary>
public class RoomsHousekeepingTests
{
    static Rooms New(FakeClock clock, FakeStakes? stakes = null) =>
        new(RoomHarness.NewRegistry(), clock, new GameEvents(), stakes ?? new FakeStakes(), new FakeStore(), RoomHarness.Empty());

    static Outbox Sweep(RoomHarness h, TimeSpan after)
    {
        h.Clock.Advance(after);
        return h.Rooms.Housekeeping(h.Clock.UtcNow);
    }

    // ---------- засиджене лобі ----------

    [Fact]
    public void A_lobby_with_two_people_goes_away_after_half_an_hour_with_a_journal_line()
    {
        var h = new RoomHarness("t-party");
        h.Join("Оля");
        h.Join("Петро");
        var id = h.RoomId;

        Assert.Empty(Sweep(h, TimeSpan.FromMinutes(29)));
        Assert.NotNull(h.Rooms.Find(id));

        var outbox = Sweep(h, TimeSpan.FromMinutes(2));
        Assert.Null(h.Rooms.Find(id));
        Assert.Contains(outbox.OfType<Journal>(), j => j.Text.Contains("стіл розійшовся"));
        Assert.Single(outbox.OfType<LobbyChanged>());
    }

    [Fact]
    public void Talking_at_the_table_keeps_the_lobby_alive()
    {
        var h = new RoomHarness("t-party");
        h.Join("Оля");
        h.Join("Петро");
        h.Clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Null(h.Rooms.TableSay(h.RoomId, null, "Петро", "Олю, починай уже").Error);

        Sweep(h, TimeSpan.FromMinutes(15));
        Assert.NotNull(h.Rooms.Find(h.RoomId));
    }

    // ---------- покинута партія ----------

    [Fact]
    public void A_turn_based_game_without_moves_for_an_hour_ends_with_no_winner_and_stakes_back()
    {
        var h = new RoomHarness("t-duel", new { stake = "10" });
        h.Stakes.Set("Оля", 20).Set("Петро", 20);
        h.Join("Оля");
        h.Join("Петро");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(10, h.Stakes.Balance("Оля"));

        Assert.Empty(Sweep(h, TimeSpan.FromMinutes(59)));
        Assert.Equal(RoomStatus.Playing, h.Room.Status);

        var outbox = Sweep(h, TimeSpan.FromMinutes(2));
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(h.Room.Result!.Draw);
        Assert.Contains(outbox.OfType<Journal>(), j => j.Text.Contains("партію покинуто"));
        Assert.Equal(20, h.Stakes.Balance("Оля"));
        Assert.Equal(20, h.Stakes.Balance("Петро"));

        var id = h.RoomId;
        Sweep(h, TimeSpan.FromMinutes(16));
        Assert.Null(h.Rooms.Find(id));   // далі — як звичайний дограний стіл
    }

    [Fact]
    public void An_abandoned_rated_game_has_no_result_no_elo_no_rewards_and_stakes_go_back()
    {
        using var rig = new EconomyRig();
        var stakes = new FakeStakes().Set("Оля", 20).Set("Петро", 20).Set("Яся", 20).Set("Сем", 20);
        var rooms = new Rooms(RoomHarness.NewRegistry(), rig.Clock, rig.Events, stakes, new FakeStore(), RoomHarness.Empty());
        var stake = new Dictionary<string, string> { ["stake"] = "10" };

        // покинута: годину ніхто не ходив
        var gone = rooms.Create("Оля", "t-duel", stake).Reply.RoomId!;
        Assert.True(rooms.Join(gone, "Петро").Reply.Ok);
        // для порівняння — справжня нічия за сусіднім столом, щоб тест не проходив «сам собою»
        var draw = rooms.Create("Яся", "t-duel", stake).Reply.RoomId!;
        Assert.True(rooms.Join(draw, "Сем").Reply.Ok);
        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True(rooms.Act(draw, "Яся", "draw", default).Reply.Ok);
        rig.Clock.Advance(TimeSpan.FromMinutes(56));

        var outbox = rooms.Housekeeping(rig.Clock.UtcNow);

        var room = rooms.Find(gone)!;
        Assert.Equal(RoomStatus.Finished, room.Status);
        Assert.Equal("Партію покинуто — без результату", room.Result!.Verdict);
        Assert.Null(room.Summary().Evening);   // у «Рахунок вечора» не пішла
        Assert.Contains(outbox.OfType<Journal>(), j => j.Text.Contains("без результату"));
        foreach (var nick in new[] { "Оля", "Петро" })
        {
            Assert.Equal(0, rig.Ratings.Of(nick, "t-duel").Games);
            Assert.Equal(0, rig.Store.CountResults(Hlechyky.Games.Economy.Economy.Key(nick), "t-duel", null, DateTimeOffset.MinValue));
            Assert.Equal(0, rig.Paid(nick, "play:"));
            Assert.Equal(20, stakes.Balance(nick));   // ставку повернуто
        }
        Assert.Equal(1, rig.Ratings.Of("Яся", "t-duel").Games);   // а справжня нічия — партія, як і була
    }

    [Fact]
    public void A_move_resets_the_hour()
    {
        var h = new RoomHarness("t-party");
        h.Join("Оля");
        h.Join("Петро");
        Assert.True(h.Start().Ok);
        h.Clock.Advance(TimeSpan.FromMinutes(50));
        Assert.True(h.Act(0, "move").Ok);

        Sweep(h, TimeSpan.FromMinutes(50));
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Theory]
    [InlineData("t-tick")]        // реалтайм: свої таймери й кадри
    [InlineData("t-latejoin")]    // HoldEmpty: гра вміє чекати сама
    [InlineData("khutory")]       // Persistent + Resumable + HoldEmpty
    public void Games_that_wait_on_their_own_are_not_touched(string game)
    {
        var h = new RoomHarness(game);
        h.Join("Оля");
        h.Join("Петро");
        if (h.Room.Status == RoomStatus.Lobby) Assert.True(h.Start().Ok);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);

        Sweep(h, TimeSpan.FromMinutes(61));
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    // ---------- місце для нового столу ----------

    [Fact]
    public void When_tables_run_out_the_oldest_finished_table_nobody_watches_makes_room()
    {
        var clock = new FakeClock();
        var rooms = New(clock);
        var ids = new List<string>();
        for (var i = 0; i < Rooms.MaxRooms; i++)
        {
            ids.Add(rooms.Create($"А{i}", "t-duel", null).Reply.RoomId!);
            Assert.True(rooms.Join(ids[^1], $"Б{i}").Reply.Ok);
        }
        // три дограні: 2-й найстаріший, 5-й — під очима глядача, 7-й свіжий
        Assert.True(rooms.Act(ids[5], "А5", "win", default).Reply.Ok);
        rooms.Watch(ids[5], "c-глядач", "Ганна");
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(rooms.Act(ids[2], "А2", "win", default).Reply.Ok);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(rooms.Act(ids[7], "А7", "win", default).Reply.Ok);

        var made = rooms.Create("Новенька", "t-duel", null);
        Assert.True(made.Reply.Ok, made.Reply.Message);
        Assert.Null(rooms.Find(ids[2]));
        Assert.NotNull(rooms.Find(ids[5]));
        Assert.NotNull(rooms.Find(ids[7]));
        Assert.Contains(made.Out, m => m is LobbyChanged);

        // далі прибирати нема що: 5-й дивляться, 7-й щойно дограли
        Assert.Equal("Столів уже задосить, дограйте ті, що є", rooms.Create("Ще одна", "t-duel", null).Reply.Message);
    }

    // ---------- мовчазний господар ----------

    [Fact]
    public void A_guest_can_start_when_the_host_has_been_silent_for_ten_minutes_and_becomes_host()
    {
        var h = new RoomHarness("t-party");
        h.Join("Оля");
        h.Join("Петро");

        Assert.Equal("Почати може лише господар", h.Rooms.StartByHost(h.RoomId, "Петро").Reply.Message);
        h.Clock.Advance(TimeSpan.FromMinutes(9));
        Assert.True(h.Ready("Оля").Ok);   // господар озвався — відлік наново
        h.Clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal("Почати може лише господар", h.Rooms.StartByHost(h.RoomId, "Петро").Reply.Message);

        h.Clock.Advance(TimeSpan.FromMinutes(2));
        var started = h.Rooms.StartByHost(h.RoomId, "Петро");
        Assert.True(started.Reply.Ok, started.Reply.Message);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("Петро", h.Room.Host);
        Assert.Equal("Почати може лише господар", h.Rooms.StartByHost(h.RoomId, "Ганна").Reply.Message);   // не за столом
    }

    // ---------- пауза закликів з ніка ----------

    [Fact]
    public void Two_new_tables_within_two_minutes_call_everyone_once_but_both_go_to_the_journal()
    {
        var clock = new FakeClock();
        var rooms = New(clock);
        var first = rooms.Create("Оля", "t-party", null);
        Assert.True(rooms.Leave(first.Reply.RoomId!, "Оля").Reply.Ok);
        clock.Advance(TimeSpan.FromSeconds(60));
        var second = rooms.Create("Оля", "t-party", null);

        Assert.Single(first.Out.OfType<Invite>());
        Assert.Single(first.Out.OfType<InviteLine>());
        Assert.Empty(second.Out.OfType<Invite>());
        Assert.Empty(second.Out.OfType<InviteLine>());
        Assert.Contains(second.Out.OfType<Journal>(), j => j.Text.StartsWith("Новий стіл"));
        Assert.Null(rooms.Find(second.Reply.RoomId)!.CalledAt);   // тихий стіл: «Покликати ще раз» — одразу

        // інший нік — своя пауза; і через дві хвилини Оля кличе знову
        Assert.Single(rooms.Create("Петро", "t-party", null).Out.OfType<Invite>());
        Assert.True(rooms.Leave(second.Reply.RoomId!, "Оля").Reply.Ok);
        clock.Advance(Calls.AgainGap);
        Assert.Single(rooms.Create("Оля", "t-party", null).Out.OfType<Invite>());
    }

    // ---------- атомарна посадка ----------

    [Fact]
    public void While_a_nick_is_being_seated_it_can_neither_create_nor_join_elsewhere()
    {
        var clock = new FakeClock();
        var rooms = New(clock);
        var a = rooms.Create("Оля", "t-party", null).Reply.RoomId!;
        var seating = (HashSet<string>)typeof(Rooms).GetField("_seating", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(rooms)!;

        seating.Add("Петро");   // друга вкладка Петра саме сідає за інший стіл
        Assert.Equal("Ти вже за столом. Встань, якщо хочеш новий", rooms.Create("петро", "t-party", null).Reply.Message);
        Assert.Equal("Не так швидко", rooms.Join(a, "Петро").Reply.Message);
        seating.Remove("Петро");

        Assert.True(rooms.Join(a, "Петро").Reply.Ok);
        Assert.Empty(seating);   // після посадки нік відпущено
    }
}
