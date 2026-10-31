using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Tests.Support;
using Xunit;

namespace Hlechyky.Tests.Games;

/// <summary>Гра на 4, що рахує, для яких місць каркас кликав View (аудит столів №4).</summary>
public sealed class TestViewCounter : Game
{
    public override GameInfo Info { get; } = new(
        "t-viewcount", "Тестовий лічильник видів", "тестовий лічильник видів", GameGroup.Party, 2, 4, Start: StartMode.ByHost);

    public readonly Dictionary<int, int> Seen = [];
    public int WatcherViews;

    public override void Start() { }

    public override ActResult Act(int seat, string action, JsonElement payload) => ActResult.Done;

    public override object View(int? seat)
    {
        if (seat is { } s) Seen[s] = Seen.GetValueOrDefault(s) + 1;
        else WatcherViews++;
        return new { seat };
    }
}

/// <summary>
/// Пакет A аудиту столів: лобі не частіше ніж раз на 250 мс і без повторів, підписка без розсилки виду всій групі,
/// види лише для місць, на які дивляться, і знімок столів, що серіалізується один раз.
/// </summary>
public class BroadcastThrottleTests
{
    static List<Send> Plan(RoomHarness h, IReadOnlyList<Outgoing> messages, Dictionary<string, string>? conns = null,
        Func<List<RoomSummary>, bool>? fresh = null, bool wanted = false)
    {
        var byConn = conns ?? [];
        string? NickOf(string id) => byConn.TryGetValue(id, out var nick) ? nick : null;
        return Broadcaster.Plan(
            messages,
            h.Rooms.Snapshot,
            h.Rooms.SoloNow,
            wanted ? id => h.Rooms.ViewsFor(id, NickOf) : h.Rooms.ViewsFor,
            NickOf,
            nick => [.. byConn.Where(p => string.Equals(p.Value, nick, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key)],
            (text, roomId) => new { text, roomId },
            snapshotFor: (roomId, conn) => h.Rooms.SnapshotFor(roomId, conn, NickOf(conn)),
            lobbyFresh: fresh);
    }

    // ---------- A1: лобі ----------

    [Fact]
    public void Second_lobby_change_inside_the_gap_waits_and_the_tick_brings_it_once()
    {
        var gate = new LobbyThrottle();
        var first = new List<Outgoing> { new LobbyChanged(), new RoomViews("a") };
        gate.Gate(first, 1000);
        Assert.Single(first.OfType<LobbyChanged>());   // перша зміна — одразу

        var second = new List<Outgoing> { new LobbyChanged(), new RoomViews("a") };
        gate.Gate(second, 1100);
        Assert.Empty(second.OfType<LobbyChanged>());   // у вікні — відкладено
        Assert.Single(second.OfType<RoomViews>());     // решта пачки летить як була
        Assert.True(gate.Pending);

        var tick = new List<Outgoing>();
        gate.Gate(tick, 1200);
        Assert.Empty(tick);                            // вікно ще не минуло
        gate.Gate(tick, 1250);
        Assert.IsType<LobbyChanged>(Assert.Single(tick));
        Assert.False(gate.Pending);

        var idle = new List<Outgoing>();
        gate.Gate(idle, 2000);
        Assert.Empty(idle);                            // нічого не чекало — нічого й не дописано
    }

    [Fact]
    public void A_batch_with_a_journal_or_an_invite_sends_the_lobby_at_once_and_first()
    {
        var gate = new LobbyThrottle();
        gate.Gate([new LobbyChanged()], 1000);
        foreach (var urgent in new Outgoing[] { new Journal("рядок"), new Invite("a", "Оля", "кличе"), new InviteLine("a", "Оля", "кличе", default) })
        {
            var batch = new List<Outgoing> { urgent, new LobbyChanged() };
            gate.Gate(batch, 1010);
            Assert.Single(batch.OfType<LobbyChanged>());
        }

        var h = new RoomHarness("ttt");
        h.Join("Оля");
        var sends = Plan(h, [new Journal("Новий стіл"), new LobbyChanged()]);
        Assert.Equal("rooms", sends[0].Event);   // порядок «спершу лобі, потім Журнал» — як був
        Assert.Equal("chat", sends[1].Event);
    }

    [Fact]
    public void The_same_lobby_snapshot_is_not_sent_twice()
    {
        var gate = new LobbyThrottle();
        var h = new RoomHarness("ttt");
        h.Join("Оля");
        bool Fresh(List<RoomSummary> list) => gate.Fresh(JsonSerializer.SerializeToUtf8Bytes(list));

        Assert.Single(Plan(h, [new LobbyChanged()], fresh: Fresh), s => s.Event == "rooms");
        Assert.DoesNotContain(Plan(h, [new LobbyChanged()], fresh: Fresh), s => s.Event == "rooms");

        h.Join("Петро");
        Assert.Single(Plan(h, [new LobbyChanged()], fresh: Fresh), s => s.Event == "rooms");

        gate.Forget();   // хтось під'єднався й отримав свій знімок — наступний летить, навіть такий самий
        Assert.Single(Plan(h, [new LobbyChanged()], fresh: Fresh), s => s.Event == "rooms");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Twelve_tables_with_200_changes_a_second_give_at_most_five_lobbies()
    {
        var gate = new LobbyThrottle();
        var sent = 0;
        // 200 дій за секунду (по 5 мс) — склад міняється за 12 столами по черзі; між ними TickEngine кличе порожню розсилку.
        for (var t = 0; t <= 1000; t += 5)
        {
            var batch = new List<Outgoing>();
            if (t < 1000) { batch.Add(new LobbyChanged()); batch.Add(new RoomViews("r" + t % 12)); }
            gate.Gate(batch, t);
            sent += batch.OfType<LobbyChanged>().Count();
        }
        Assert.InRange(sent, 4, 5);
        Assert.False(gate.Pending);   // остання зміна теж долетіла
    }

    // ---------- A2: підписка ----------

    [Fact]
    public void A_new_watcher_costs_the_others_nothing_and_gets_its_own_snapshot()
    {
        var conns = new Dictionary<string, string> { ["c1"] = "Ганна", ["c2"] = "Тарас" };
        var h = new RoomHarness("ttt");
        h.Join("Оля");
        h.Join("Петро");
        h.Rooms.Watch(h.RoomId, "c1", "Ганна");

        var watched = h.Rooms.Watch(h.RoomId, "c2", "Тарас");
        Assert.Empty(watched.OfType<RoomViews>());
        Assert.Single(watched.OfType<RoomWatched>());   // хаб бачить, що підписка відбулась

        // хаб дописує новенькому повний знімок — і це все, що летить
        var sends = Plan(h, [.. watched, new RoomSnapshot(h.RoomId, "c2")], conns);
        var only = Assert.Single(sends);
        Assert.Equal(["c2"], Assert.IsType<ToConnections>(only.Target).Ids);
        Assert.Equal("room", only.Event);

        Assert.Empty(h.Rooms.Unwatch(h.RoomId, "c2"));   // і відписка мовчить, як і була
        Assert.Empty(h.Rooms.Watch(h.RoomId, "c1", "Ганна"));   // повтор — нічого
    }

    [Fact]
    public void A_peek_into_a_solo_game_still_refreshes_the_players_head()
    {
        var h = new RoomHarness("t-solo");
        h.Solo("Оля");
        h.Rooms.Focus("c-оля", "Оля", h.RoomId);
        Assert.Single(h.Rooms.Watch(h.RoomId, "c-петро", "Петро").OfType<RoomViews>());
    }

    // ---------- A3: види лише для тих, хто дивиться ----------

    [Fact]
    public void Views_are_built_only_for_watched_seats_and_the_watcher()
    {
        var conns = new Dictionary<string, string> { ["c-оля"] = "Оля", ["c-глядач"] = "Ганна" };
        var h = new RoomHarness("t-viewcount");
        foreach (var n in new[] { "Оля", "Петро", "Іван", "Марко" }) h.Join(n);
        Assert.True(h.Start().Ok);
        foreach (var (c, n) in conns) h.Rooms.Watch(h.RoomId, c, n);
        var game = (TestViewCounter)h.Room.Game;
        game.Seen.Clear();
        game.WatcherViews = 0;

        var sends = Plan(h, [new RoomViews(h.RoomId)], conns, wanted: true);

        Assert.Equal(new[] { 0 }, game.Seen.Keys.Order());
        Assert.Equal(1, game.Seen[0]);
        Assert.Equal(1, game.WatcherViews);
        var mine = Assert.Single(sends, s => s.Target is ToConnections);
        Assert.Equal(["c-оля"], Assert.IsType<ToConnections>(mine.Target).Ids);
        Assert.Equal(0, Views.Json(mine.Payload).GetProperty("seat").GetInt32());
        Assert.Single(sends, s => s.Target is ToGroupExcept);

        // старий виклик (агенти, тести) — як був: усі зайняті місця
        game.Seen.Clear();
        Assert.NotNull(h.Rooms.ViewsFor(h.RoomId));
        Assert.Equal(new[] { 0, 1, 2, 3 }, game.Seen.Keys.Order());
    }
}
