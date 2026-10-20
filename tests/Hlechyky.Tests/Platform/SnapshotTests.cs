using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Окремий вид для новенького (прохід №3, п. 247): RoomSnapshot летить лише тому з'єднанню, що підійшло, повним видом
/// (Game.Snapshot), а легкої розсилки тієї ж пачки воно не отримує.
/// </summary>
public class SnapshotTests
{
    static List<Send> Plan(RoomHarness h, IReadOnlyList<Outgoing> messages, Dictionary<string, string> conns) =>
        Broadcaster.Plan(
            messages,
            h.Rooms.Snapshot,
            h.Rooms.SoloNow,
            h.Rooms.ViewsFor,
            id => conns.TryGetValue(id, out var nick) ? nick : null,
            nick => [.. conns.Where(p => string.Equals(p.Value, nick, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key)],
            (text, roomId) => new { text, roomId },
            snapshotFor: (roomId, conn) => h.Rooms.SnapshotFor(roomId, conn, conns.TryGetValue(conn, out var n) ? n : null));

    static RoomHarness Table(Dictionary<string, string> conns)
    {
        var h = new RoomHarness("ttt");
        h.Join("Оля");
        h.Join("Петро");
        foreach (var (conn, nick) in conns) h.Rooms.Watch(h.RoomId, conn, nick);
        return h;
    }

    [Fact]
    public void The_newcomer_gets_a_personal_full_view_and_is_left_out_of_the_group_one()
    {
        var conns = new Dictionary<string, string> { ["c-оля"] = "Оля", ["c-петро"] = "Петро", ["c-нова"] = "Ганна" };
        var h = Table(conns);
        var sends = Plan(h, [new RoomViews(h.RoomId), new RoomSnapshot(h.RoomId, "c-нова")], conns);

        var snap = Assert.Single(sends, s => s.Target is ToConnections { Ids: ["c-нова"] });
        Assert.Equal("room", snap.Event);
        var body = Views.Json(snap.Payload);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("seat").ValueKind);   // Ганна — глядач
        Assert.Equal(h.RoomId, body.GetProperty("room").GetProperty("id").GetString());

        var group = Assert.Single(sends, s => s.Target is ToGroupExcept);
        Assert.Contains("c-нова", ((ToGroupExcept)group.Target).Except);
        Assert.DoesNotContain(sends, s => s.Target is ToConnections t && t.Ids.Contains("c-нова") && s != snap);
    }

    [Fact]
    public void A_seated_newcomer_gets_the_view_of_their_seat()
    {
        var conns = new Dictionary<string, string> { ["c-петро"] = "Петро" };
        var h = Table(conns);
        var view = h.Rooms.SnapshotFor(h.RoomId, "c-петро", "Петро");
        Assert.NotNull(view);
        Assert.Equal(1, view.Seat);
    }

    [Fact]
    public void A_view_planned_before_a_second_tab_subscribed_is_older_than_that_tabs_snapshot()
    {
        // Петро відкрив той самий стіл у другій вкладці, поки летіла пачка, складена ще без неї: SignalR-група вже з нею,
        // а знімок пачки — ні, тож звідти їй іде вид глядача («на мить побачив розклад, а тоді ніби дивлюсь збоку»).
        // Номер розкладки каже браузеру, що той вид старший за її власний знімок.
        var conns = new Dictionary<string, string> { ["c-петро"] = "Петро" };
        var h = Table(conns);
        var stale = h.Rooms.ViewsFor(h.RoomId)!;
        h.Rooms.Watch(h.RoomId, "c-петро-2", "Петро");
        conns["c-петро-2"] = "Петро";
        var snap = h.Rooms.SnapshotFor(h.RoomId, "c-петро-2", "Петро")!;
        Assert.Equal(1, snap.Seat);
        Assert.True(snap.Seq > stale.Seq);

        var sends = Broadcaster.Plan([new RoomViews(h.RoomId)], h.Rooms.Snapshot, h.Rooms.SoloNow, _ => stale,
            id => conns.TryGetValue(id, out var nick) ? nick : null, _ => [], (text, roomId) => new { text, roomId });
        var group = Assert.Single(sends, s => s.Target is ToGroupExcept);
        Assert.DoesNotContain("c-петро-2", ((ToGroupExcept)group.Target).Except);
        Assert.Equal(JsonValueKind.Null, Views.Json(group.Payload).GetProperty("seat").ValueKind);
        Assert.All(sends.Where(s => s.Event == "room"), s => Assert.Equal(stale.Seq, Views.Json(s.Payload).GetProperty("seq").GetInt64()));
    }

    [Fact]
    public void View_numbers_grow_and_start_from_the_clock_so_a_restart_does_not_go_back()
    {
        var conns = new Dictionary<string, string> { ["c-оля"] = "Оля" };
        var h = Table(conns);
        var a = h.Rooms.ViewsFor(h.RoomId)!.Seq;
        var b = h.Rooms.SnapshotFor(h.RoomId, "c-оля", "Оля")!.Seq;
        var c = h.Rooms.ViewsFor(h.RoomId)!.Seq;
        Assert.True(a < b && b < c);
        Assert.True(a > DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds() * 1000);
    }

    [Fact]
    public void Only_a_connection_that_watches_the_table_can_ask_for_a_snapshot()
    {
        var conns = new Dictionary<string, string> { ["c-оля"] = "Оля" };
        var h = Table(conns);
        Assert.Null(h.Rooms.SnapshotFor(h.RoomId, "c-чужий", "Оля"));
        Assert.Empty(h.Rooms.Resync(h.RoomId, "c-чужий"));
        Assert.IsType<RoomSnapshot>(Assert.Single(h.Rooms.Resync(h.RoomId, "c-оля")));
        Assert.Empty(h.Rooms.Resync("нема-такого", "c-оля"));
    }

    [Fact]
    public void Without_a_snapshot_source_the_old_plan_still_sends_a_plain_view()
    {
        var conns = new Dictionary<string, string> { ["c-оля"] = "Оля" };
        var h = Table(conns);
        var sends = Broadcaster.Plan([new RoomSnapshot(h.RoomId, "c-оля")], h.Rooms.Snapshot, h.Rooms.SoloNow, h.Rooms.ViewsFor,
            id => conns.TryGetValue(id, out var nick) ? nick : null, _ => [], (text, roomId) => new { text, roomId });
        Assert.Contains(sends, s => s.Event == "room");
    }

    [Fact]
    public void By_default_a_snapshot_is_the_same_as_the_view()
    {
        var h = new RoomHarness("ttt");
        h.Join("Оля");
        h.Join("Петро");
        h.Start();
        Assert.Equal(h.View(0).GetRawText(), h.Snapshot(0).GetRawText());
    }
}
