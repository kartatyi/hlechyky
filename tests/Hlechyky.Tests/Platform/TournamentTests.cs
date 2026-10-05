using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Tests.Support;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Platform;

/// <summary>Турнір на вечір: збір, столи, очки за місця, корона.</summary>
public class TournamentTests
{
    sealed class Collect : IOutbox
    {
        public List<Outgoing> Sent { get; } = [];
        public void Post(Outgoing message) { lock (Sent) Sent.Add(message); }
    }

    sealed class NullHub : IHubContext<RadioHub>
    {
        public int Sends;
        public IHubClients Clients => new C(this);
        public IGroupManager Groups => throw new NotSupportedException();

        sealed class C(NullHub hub) : IHubClients
        {
            public IClientProxy All => new P(hub);
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => All;
            public IClientProxy Client(string connectionId) => All;
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => All;
            public IClientProxy Group(string groupName) => All;
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => All;
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => All;
            public IClientProxy User(string userId) => All;
            public IClientProxy Users(IReadOnlyList<string> userIds) => All;
        }

        sealed class P(NullHub hub) : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
            {
                Interlocked.Increment(ref hub.Sends);
                return Task.CompletedTask;
            }
        }
    }

    sealed class Setup
    {
        public RoomHarness H { get; } = new("ttt");
        public Presence Presence { get; } = new();
        public Collect Out { get; } = new();
        public Tournament T { get; }

        public Setup(params string[] online)
        {
            foreach (var nick in online) Presence.Set("c-" + nick, nick);
            T = new Tournament(H.Rooms, H.Registry, H.Events, Out, H.Store, Presence, new NullHub(), H.Clock, NullLogger<Tournament>.Instance) { OwnTimer = false };
            T.StartAsync(default).Wait();
        }

        public JsonElement Snap => Views.Json(T.Snapshot());
        public string Stage => Snap.GetProperty("stage").GetString()!;
        public string RoomId => Snap.GetProperty("room").GetProperty("id").GetString()!;
        public string[] Games => [.. Snap.GetProperty("games").EnumerateArray().Select(x => x.GetProperty("id").GetString()!)];
        public int? NextIn => Snap.GetProperty("nextIn") is { ValueKind: JsonValueKind.Number } n ? n.GetInt32() : null;

        /// <summary>Учасники зібрались, і перша гра почалась.</summary>
        public Setup Started(string[] games, params string[] joiners)
        {
            Assert.Null(T.Create(joiners[0], games));
            foreach (var p in joiners.Skip(1)) Assert.Null(T.Join(p));
            Assert.Null(T.Next(joiners[0]));
            return this;
        }

        /// <summary>Дограти поточну гру: хтось встає — техпоразка, стіл дограний.</summary>
        public string FinishGame(string loser)
        {
            var id = RoomId;
            H.Rooms.Leave(id, loser);
            return id;
        }

        public int Points(string nick) => Snap.GetProperty("standings").EnumerateArray().Single(x => x.GetProperty("nick").GetString() == nick).GetProperty("points").GetInt32();
    }

    // ---------------------------------------------------------------- місця

    [Fact]
    public void Places_follow_the_score_and_ties_share()
    {
        var places = Tournament.Places(["Оля", "Петро", "Ганна"], new Dictionary<int, long> { [0] = 50, [1] = 120, [2] = 50 }, [1]);
        Assert.Equal([("Петро", 1, 3), ("Ганна", 2, 2), ("Оля", 2, 2)], places.Select(p => (p.Nick, p.Place, p.Points)));
    }

    [Fact]
    public void Without_scores_winners_are_first_and_a_draw_is_first_for_all()
    {
        var win = Tournament.Places(["Оля", "Петро", null], null, [1]);
        Assert.Equal([("Петро", 1, 2), ("Оля", 2, 1)], win.Select(p => (p.Nick, p.Place, p.Points)));

        var draw = Tournament.Places(["Оля", "Петро"], null, []);
        Assert.All(draw, p => Assert.Equal(1, p.Place));
    }

    // ---------------------------------------------------------------- збір

    [Fact]
    public void Create_join_leave()
    {
        var s = new Setup("Оля", "Петро");
        Assert.Null(s.T.Create("Оля", ["ttt", "c4"]));
        Assert.Equal("gathering", s.Stage);
        Assert.NotNull(s.T.Create("Петро", ["ttt", "c4"]));   // другий турнір не збереш
        Assert.Null(s.T.Join("Петро"));
        Assert.NotNull(s.T.Join("Петро"));
        Assert.Equal(["Оля", "Петро"], s.Snap.GetProperty("players").EnumerateArray().Select(x => x.GetString()));
        Assert.Null(s.T.Leave("Оля"));
        Assert.Equal("Петро", s.Snap.GetProperty("host").GetString());   // господарем став той, хто лишився
        Assert.Contains(s.Out.Sent.OfType<Journal>(), j => j.Text.Contains("збирає турнір"));
    }

    [Fact]
    public void Games_must_be_two_to_six_and_multiplayer()
    {
        var s = new Setup("Оля");
        Assert.NotNull(s.T.Create("Оля", ["ttt"]));
        Assert.NotNull(s.T.Create("Оля", ["ttt", "nope"]));
        Assert.NotNull(s.T.Create("Оля", ["ttt", "wordle"]));   // соло не годиться
    }

    // ---------------------------------------------------------------- ігри

    [Fact]
    public void Next_sets_the_table_seats_everyone_and_starts()
    {
        var s = new Setup("Оля", "Петро");
        s.T.Create("Оля", ["ttt", "c4"]);
        s.T.Join("Петро");

        Assert.NotNull(s.T.Next("Петро"));   // не господар
        Assert.Null(s.T.Next("Оля"));

        Assert.Equal("playing", s.Stage);
        var room = s.H.Rooms.Find(s.RoomId)!;
        Assert.Equal(RoomStatus.Playing, room.Status);
        Assert.Equal(new string?[] { "Оля", "Петро" }, room.Seats);
        Assert.Contains(s.Out.Sent.OfType<Journal>(), j => j.Text.Contains("гра 1 з 2") && j.RoomId == room.Id);
        // Турнір садить усіх сам: кликати за такий стіл нікого, і рядка «кличе в …» у Балачках від нього нема.
        Assert.DoesNotContain(s.Out.Sent, m => m is Invite or InviteLine);
    }

    [Fact]
    public void A_finished_game_gives_points_and_the_last_one_crowns_the_champion()
    {
        var s = new Setup("Оля", "Петро");
        s.T.Create("Оля", ["ttt", "c4"]);
        s.T.Join("Петро");

        s.T.Next("Оля");
        s.H.Rooms.Leave(s.RoomId, "Петро");        // техпоразка: Оля перемогла
        Assert.Equal("between", s.Stage);
        Assert.Equal(2, s.Points("Оля"));
        Assert.Equal(1, s.Points("Петро"));

        Assert.Null(s.T.Next("Оля"));              // дограний стіл сам звільнив місця
        s.H.Rooms.Leave(s.RoomId, "Оля");
        Assert.Equal("done", s.Stage);
        Assert.Equal(3, s.Points("Оля"));
        Assert.Equal(3, s.Points("Петро"));
        Assert.Equal(["Оля", "Петро"], s.Snap.GetProperty("champions").EnumerateArray().Select(x => x.GetString()).Order());
        Assert.Contains("tournament:crown", s.H.Store.States.Keys);
        Assert.Contains(s.Out.Sent.OfType<Journal>(), j => j.Text.Contains("👑"));
    }

    [Fact]
    public void The_crown_survives_a_restart()
    {
        var s = new Setup("Оля", "Петро");
        s.T.Create("Оля", ["ttt", "c4"]);
        s.T.Join("Петро");
        s.T.Next("Оля");
        s.H.Rooms.Leave(s.RoomId, "Петро");
        s.T.Cancel("Оля");                          // достроково: чемпіон — хто попереду

        var again = new Tournament(s.H.Rooms, s.H.Registry, new GameEvents(), new Collect(), s.H.Store, s.Presence, new NullHub(), s.H.Clock, NullLogger<Tournament>.Instance);
        Assert.Equal(["Оля"], again.Crown());
    }

    [Fact]
    public void Too_few_online_players_cannot_start_a_game()
    {
        var s = new Setup("Оля");                    // Петро в турнірі, але не на сайті
        s.T.Create("Оля", ["ttt", "c4"]);
        s.T.Join("Петро");
        var error = s.T.Next("Оля");
        Assert.NotNull(error);
        Assert.Contains("щонайменше", error);
    }

    [Fact]
    public void A_vanished_table_counts_as_skipped()
    {
        var s = new Setup("Оля", "Петро");
        s.T.Create("Оля", ["ttt", "c4"]);
        s.T.Join("Петро");
        s.T.Next("Оля");
        Assert.Null(s.T.Skip("Оля"));
        var snap = s.Snap;
        Assert.NotEqual("playing", snap.GetProperty("stage").GetString());
        Assert.Equal(1, snap.GetProperty("results").GetArrayLength());
    }

    [Fact]
    public void A_winner_with_the_smaller_score_still_takes_first_place()
    {
        // сапер: Оля наступила на міну з рахунком 30:10 — партію виграв Петро, і в турнірі він перший
        var places = Tournament.Places(["Оля", "Петро"], new Dictionary<int, long> { [0] = 30, [1] = 10 }, [1]);
        Assert.Equal([("Петро", 1, 2), ("Оля", 2, 1)], places.Select(p => (p.Nick, p.Place, p.Points)));
    }

    [Fact]
    public void Among_losers_the_score_still_decides()
    {
        var places = Tournament.Places(["Оля", "Петро", "Ганна", "Тарас"],
            new Dictionary<int, long> { [0] = 5, [1] = 9, [2] = 7, [3] = 7 }, [0]);
        Assert.Equal([("Оля", 1, 4), ("Петро", 2, 3), ("Ганна", 3, 2), ("Тарас", 3, 2)], places.Select(p => (p.Nick, p.Place, p.Points)));
    }

    [Fact]
    public void Skipping_a_live_game_gives_nobody_points()
    {
        var s = new Setup("Оля", "Петро");
        s.T.Create("Оля", ["ttt", "c4"]);
        s.T.Join("Петро");
        s.T.Next("Оля");
        Assert.Null(s.T.Skip("Оля"));

        var snap = s.Snap;
        Assert.Equal("between", snap.GetProperty("stage").GetString());
        var r = Assert.Single(snap.GetProperty("results").EnumerateArray());
        Assert.True(r.GetProperty("skipped").GetBoolean());
        Assert.Equal(0, s.Points("Оля"));
        Assert.Equal(0, s.Points("Петро"));
    }

    [Fact]
    public void A_game_that_does_not_fit_the_company_can_be_skipped_between_games()
    {
        var s = new Setup("Оля", "Петро", "Ганна");
        s.T.Create("Оля", ["ttt", "c4", "ttt"]);
        s.T.Join("Петро");
        s.T.Join("Ганна");
        Assert.NotNull(s.T.Next("Оля"));             // утрьох у хрестики не сісти
        Assert.NotNull(s.T.Skip("Петро"));           // не господар
        Assert.Null(s.T.Skip("Оля"));                // ще на зборі — пропускаємо першу ж
        Assert.Equal("between", s.Stage);
        Assert.Equal(1, s.Snap.GetProperty("index").GetInt32());

        s.Presence.Remove("c-Ганна");
        Assert.Null(s.T.Next("Оля"));                // удвох у чотири в ряд
        s.H.Rooms.Leave(s.RoomId, "Петро");
        Assert.Equal("between", s.Stage);

        s.Presence.Set("c-Ганна", "Ганна");
        Assert.NotNull(s.T.Next("Оля"));             // знову троє на хрестики
        Assert.Null(s.T.Skip("Оля"));                // остання гра пропущена — турнір скінчився
        var snap = s.Snap;
        Assert.Equal("done", snap.GetProperty("stage").GetString());
        Assert.True(snap.GetProperty("results")[2].GetProperty("skipped").GetBoolean());
        Assert.Equal(["Оля"], snap.GetProperty("champions").EnumerateArray().Select(x => x.GetString()));
        Assert.NotNull(s.T.Skip("Оля"));             // після кінця — нема чого
    }

    // ---------------------------------------------------------------- правка ігор (записка #20)

    [Fact]
    public void The_host_can_change_the_games_while_gathering()
    {
        var s = new Setup("Оля", "Петро");
        s.T.Create("Оля", ["ttt", "c4"]);
        s.T.Join("Петро");

        Assert.NotNull(s.T.Edit("Петро", ["c4", "ttt"]));          // не господар
        Assert.Equal(["ttt", "c4"], s.Games);

        Assert.Null(s.T.Edit("Оля", ["c4", "ttt", "c4"]));        // переставити й додати
        Assert.Equal(["c4", "ttt", "c4"], s.Games);
        Assert.Contains(s.Out.Sent.OfType<Journal>(), j => j.Text.Contains("змінює ігри"));

        // ті самі межі, що й при створенні
        Assert.NotNull(s.T.Edit("Оля", ["ttt"]));
        Assert.NotNull(s.T.Edit("Оля", ["ttt", "c4", "ttt", "c4", "ttt", "c4", "ttt"]));
        Assert.NotNull(s.T.Edit("Оля", ["ttt", "wordle"]));
        Assert.NotNull(s.T.Edit("Оля", ["ttt", "nope"]));
        Assert.Equal(["c4", "ttt", "c4"], s.Games);
        Assert.Equal("gathering", s.Stage);

        // те саме ще раз — нічого не міняється і в Журнал не пишеться
        var lines = s.Out.Sent.Count;
        Assert.Null(s.T.Edit("Оля", ["c4", "ttt", "c4"]));
        Assert.Equal(lines, s.Out.Sent.Count);
    }

    [Fact]
    public void After_the_start_only_unplayed_games_change_and_never_mid_game()
    {
        var s = new Setup("Оля", "Петро").Started(["ttt", "c4", "ttt"], "Оля", "Петро");

        Assert.NotNull(s.T.Edit("Оля", ["ttt", "ttt", "c4"]));    // гра йде
        s.FinishGame("Петро");
        Assert.Equal("between", s.Stage);

        Assert.NotNull(s.T.Edit("Оля", ["c4", "c4", "ttt"]));     // зіграну не переписати
        Assert.NotNull(s.T.Edit("Оля", ["ttt"]));                 // хоч одна незіграна мусить лишитись
        Assert.NotNull(s.T.Edit("Петро", ["ttt", "ttt"]));        // не господар
        Assert.Null(s.T.Edit("Оля", ["ttt", "ttt", "c4", "ttt"]));
        Assert.Equal(["ttt", "ttt", "c4", "ttt"], s.Games);
        Assert.Equal(1, s.Snap.GetProperty("index").GetInt32());
        Assert.Equal(2, s.Points("Оля"));                         // очки за зігране на місці

        Assert.Null(s.T.Edit("Оля", ["ttt", "c4"]));              // прибрати — теж можна
        Assert.Null(s.T.Next("Оля"));
        Assert.Equal("c4", s.H.Rooms.Find(s.RoomId)!.Info.Id);
    }

    // ---------------------------------------------------------------- сам за наступний стіл (записка #23)

    [Fact]
    public void After_a_game_the_next_table_sets_itself_when_the_countdown_ends()
    {
        var s = new Setup("Оля", "Петро").Started(["ttt", "c4", "ttt"], "Оля", "Петро");
        var first = s.FinishGame("Петро");

        var snap = s.Snap;
        Assert.Equal("between", snap.GetProperty("stage").GetString());
        Assert.Equal(first, snap.GetProperty("prev").GetString());
        Assert.Equal((int)Tournament.AutoDelay.TotalMilliseconds, s.NextIn);

        s.H.Clock.Advance(Tournament.AutoDelay - TimeSpan.FromSeconds(1));
        s.T.Tick();
        Assert.Equal("between", s.Stage);                          // ще рано
        Assert.Equal(1000, s.NextIn);

        s.H.Clock.Advance(1);
        s.T.Tick();
        Assert.Equal("playing", s.Stage);
        var room = s.H.Rooms.Find(s.RoomId)!;
        Assert.Equal("c4", room.Info.Id);
        Assert.Equal(RoomStatus.Playing, room.Status);
        Assert.Equal(["Оля", "Петро"], room.Seats.OfType<string>().Order());
        Assert.Equal(["Оля", "Петро"], s.Snap.GetProperty("room").GetProperty("seats").EnumerateArray().Select(x => x.GetString()!).Order());
        Assert.Null(s.NextIn);
        Assert.Contains(s.Out.Sent.OfType<Journal>(), j => j.Text.Contains("гра 2 з 3") && j.RoomId == room.Id);
        // зі старого столу всіх звільнено — вставати руками не треба
        Assert.DoesNotContain(s.H.Rooms.Snapshot(), r => r.Id == first && r.Seats.Any(x => x.Nick is not null));
    }

    [Fact]
    public void Now_skips_the_countdown()
    {
        var s = new Setup("Оля", "Петро").Started(["ttt", "c4"], "Оля", "Петро");
        s.FinishGame("Петро");
        Assert.NotNull(s.NextIn);
        Assert.NotNull(s.T.Next("Петро"));                         // «Зараз» — теж за господарем
        Assert.Null(s.T.Next("Оля"));
        Assert.Equal("playing", s.Stage);
        Assert.Null(s.NextIn);

        s.H.Clock.Advance(Tournament.AutoDelay * 2);
        s.T.Tick();                                                // відлік не спрацює вдруге поверх живої гри
        Assert.Equal("playing", s.Stage);
        Assert.Equal(1, s.Snap.GetProperty("index").GetInt32());
    }

    [Fact]
    public void Pause_stops_the_countdown_until_the_host_says_next()
    {
        var s = new Setup("Оля", "Петро").Started(["ttt", "c4"], "Оля", "Петро");
        Assert.NotNull(s.T.Pause("Оля"));                          // гра йде — відліку нема
        s.FinishGame("Петро");

        Assert.NotNull(s.T.Pause("Петро"));                        // не господар
        Assert.Null(s.T.Pause("Оля"));
        Assert.Null(s.NextIn);
        Assert.True(s.Snap.GetProperty("held").GetBoolean());
        Assert.NotNull(s.T.Pause("Оля"));                          // уже на паузі

        s.H.Clock.Advance(Tournament.AutoDelay * 3);
        s.T.Tick();
        Assert.Equal("between", s.Stage);

        Assert.Null(s.T.Next("Оля"));                              // «Далі» вручну — як було
        Assert.Equal("playing", s.Stage);
        Assert.False(s.Snap.GetProperty("held").GetBoolean());
    }

    [Fact]
    public void The_last_game_has_no_countdown_and_crowns_at_once()
    {
        var s = new Setup("Оля", "Петро").Started(["ttt", "c4"], "Оля", "Петро");
        s.FinishGame("Петро");
        s.H.Clock.Advance(Tournament.AutoDelay);
        s.T.Tick();
        Assert.Equal("playing", s.Stage);

        var last = s.FinishGame("Оля");
        var snap = s.Snap;
        Assert.Equal("done", snap.GetProperty("stage").GetString());
        Assert.Equal(JsonValueKind.Null, snap.GetProperty("nextIn").ValueKind);
        Assert.Equal(last, snap.GetProperty("prev").GetString());
        Assert.Contains(s.Out.Sent.OfType<Journal>(), j => j.Text.Contains("👑"));

        var rooms = s.H.Rooms.Snapshot().Count;
        s.H.Clock.Advance(Tournament.AutoDelay * 2);
        s.T.Tick();
        Assert.Equal(rooms, s.H.Rooms.Snapshot().Count);           // нових столів після кінця нема
    }

    [Fact]
    public void Someone_at_another_table_is_not_pulled_and_everyone_sees_why()
    {
        var s = new Setup("Оля", "Петро").Started(["ttt", "c4"], "Оля", "Петро");
        s.FinishGame("Петро");
        var other = s.H.Rooms.Create("Петро", "c4", null).Reply.RoomId!;   // Петро сів грати щось своє

        s.H.Clock.Advance(Tournament.AutoDelay);
        s.T.Tick();
        var snap = s.Snap;
        Assert.Equal("between", snap.GetProperty("stage").GetString());
        Assert.Contains("Петро", snap.GetProperty("note").GetString());
        Assert.Equal(JsonValueKind.Null, snap.GetProperty("nextIn").ValueKind);
        Assert.Equal("Петро", s.H.Rooms.Find(other)!.Seats[0]);     // його стіл не чіпали

        s.H.Rooms.Leave(other, "Петро");
        Assert.Null(s.T.Next("Оля"));                               // встав — господар тисне «Далі»
        Assert.Equal(JsonValueKind.Null, s.Snap.GetProperty("note").ValueKind);
    }

    [Fact]
    public void Who_left_the_tournament_is_not_seated_by_the_countdown()
    {
        var s = new Setup("Оля", "Петро", "Ганна").Started(["c4", "ttt", "c4"], "Оля", "Петро");
        s.T.Join("Ганна");
        s.FinishGame("Петро");
        Assert.Null(s.T.Leave("Ганна"));                           // передумала

        s.H.Clock.Advance(Tournament.AutoDelay);
        s.T.Tick();
        Assert.Equal("playing", s.Stage);
        var seats = s.H.Rooms.Find(s.RoomId)!.Seats.OfType<string>().ToArray();
        Assert.DoesNotContain("Ганна", seats);
        Assert.Equal(2, seats.Length);
    }

    [Fact]
    public void A_skipped_game_also_counts_down_but_skipping_while_gathering_does_not()
    {
        var s = new Setup("Оля", "Петро");
        s.T.Create("Оля", ["ttt", "c4", "ttt", "c4"]);
        s.T.Join("Петро");
        Assert.Null(s.T.Skip("Оля"));                              // ще на зборі: люди, може, ще сходяться
        Assert.Equal("between", s.Stage);
        Assert.Null(s.NextIn);
        s.H.Clock.Advance(Tournament.AutoDelay * 2);
        s.T.Tick();
        Assert.Equal("between", s.Stage);

        Assert.Null(s.T.Next("Оля"));                              // c4
        Assert.Null(s.T.Skip("Оля"));                              // гра завила — пропуск, а далі відлік
        Assert.Equal("between", s.Stage);
        Assert.NotNull(s.NextIn);
        s.H.Clock.Advance(Tournament.AutoDelay);
        s.T.Tick();
        Assert.Equal("playing", s.Stage);                          // ttt, гра 3
        Assert.Equal(2, s.Snap.GetProperty("index").GetInt32());
    }

    [Fact]
    public void Without_a_tournament_the_snapshot_still_has_the_crown()
    {
        var s = new Setup();
        var snap = s.Snap;
        Assert.False(snap.GetProperty("active").GetBoolean());
        Assert.Equal(JsonValueKind.Array, snap.GetProperty("crown").ValueKind);
    }
}
