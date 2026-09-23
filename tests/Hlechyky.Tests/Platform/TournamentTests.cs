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
            T = new Tournament(H.Rooms, H.Registry, H.Events, Out, H.Store, Presence, new NullHub(), H.Clock, NullLogger<Tournament>.Instance);
            T.StartAsync(default).Wait();
        }

        public JsonElement Snap => Views.Json(T.Snapshot());
        public string Stage => Snap.GetProperty("stage").GetString()!;
        public string RoomId => Snap.GetProperty("room").GetProperty("id").GetString()!;
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

    [Fact]
    public void Without_a_tournament_the_snapshot_still_has_the_crown()
    {
        var s = new Setup();
        var snap = s.Snap;
        Assert.False(snap.GetProperty("active").GetBoolean());
        Assert.Equal(JsonValueKind.Array, snap.GetProperty("crown").ValueKind);
    }
}
