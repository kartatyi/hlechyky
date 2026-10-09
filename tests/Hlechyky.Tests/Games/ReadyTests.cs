using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>«✋ Готовий» і «не готові — почати все одно?» (bets-contract §5).</summary>
public class ReadyTests
{
    /// <summary>Компанія на трьох у лобі (t-party, починає господар Оля).</summary>
    static RoomHarness Party(params string[] nicks)
    {
        var h = new RoomHarness("t-party");
        foreach (var n in nicks.Length > 0 ? nicks : ["Оля", "Петро", "Іра"]) Assert.True(h.Join(n).Ok, h.Reply.Message);
        return h;
    }

    /// <summary>Дуель Оля — Петро, Оля виграла: стіл дограно, чекає «Ану ще раз».</summary>
    static RoomHarness Played(string game = "t-duel")
    {
        var h = new RoomHarness(game);
        h.Join("Оля");
        h.Join("Петро");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.True(h.Act(h.Room.SeatOf("Оля")!.Value, "win").Ok);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        return h;
    }

    static int[] ReadySeats(RoomHarness h) => h.Room.Summary().Ready?.ToArray() ?? [];

    [Fact]
    public void Ready_is_on_the_summary_and_off_again()
    {
        var h = Party();
        Assert.Null(h.Room.Summary().Ready);
        Assert.False(Views.Json(h.Room.Summary()).TryGetProperty("ready", out _));   // порожнє на дріт не йде

        Assert.True(h.Ready("Петро").Ok);
        Assert.True(h.Ready("Петро").Ok);   // подвійний клік — той самий стан
        Assert.Equal([1], ReadySeats(h));
        Assert.Equal([1], Views.Json(h.Room.Summary()).GetProperty("ready").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Contains(h.Outbox, o => o is RoomViews);
        Assert.Contains(h.Outbox, o => o is LobbyChanged);

        Assert.True(h.Ready("Петро", false).Ok);
        Assert.Empty(ReadySeats(h));
    }

    [Fact]
    public void Start_without_force_waits_for_the_rest_and_the_host_is_ready_himself()
    {
        var h = Party();
        h.Ready("Іра");
        var r = h.Start(force: false);
        Assert.False(r.Ok);
        Assert.Equal(["Петро"], r.NotReady);
        Assert.Equal("Ще не готові: Петро", r.Message);
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);
        Assert.Equal(0, ((TestParty)h.Room.Game).Starts);
        // Хто тиснув «Почати», той готовий — і решта це бачить.
        Assert.Equal([0, 2], ReadySeats(h));

        var wire = Views.Json(r);
        Assert.Equal("Петро", wire.GetProperty("notReady")[0].GetString());
        Assert.False(Views.Json(RoomReply.Fail("ні")).TryGetProperty("notReady", out _));   // старим відповідям поле не дописуємо
    }

    [Fact]
    public void Everyone_ready_starts_and_the_start_clears_readiness()
    {
        var h = Party();
        h.Ready("Петро");
        h.Ready("Іра");
        Assert.True(h.Start(force: false).Ok, h.Reply.Message);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Empty(h.Room.Ready);
        Assert.Null(h.Room.Summary().Ready);
        Assert.False(h.Ready("Петро").Ok);   // посеред партії готуватись нема до чого
        Assert.Equal("Партія вже йде", h.Reply.Message);
    }

    [Fact]
    public void Force_starts_anyway_and_the_old_call_does_not_ask()
    {
        var h = Party();
        Assert.True(h.Start(force: true).Ok, h.Reply.Message);
        Assert.Null(h.Reply.NotReady);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);

        var h2 = Party();
        Assert.True(h2.Start().Ok);   // внутрішні виклики (турнір, автостарт) готовність не питають
    }

    [Fact]
    public void Seat_changes_reset_everyone()
    {
        var h = Party("Оля", "Петро");
        h.Ready("Оля");
        h.Ready("Петро");
        Assert.Equal([0, 1], ReadySeats(h));

        h.Join("Іра");
        Assert.Empty(h.Room.Ready);

        h.Ready("Петро");
        h.Ready("Іра");
        h.Leave("Іра");
        Assert.Empty(h.Room.Ready);
    }

    [Fact]
    public void Settings_and_bot_reset_everyone()
    {
        var h = Played("t-tuned");
        h.Ready("Оля");
        h.Ready("Петро");
        var conf = h.Rooms.Reconfigure(h.RoomId, h.Room.Host, new Dictionary<string, string> { ["mode"] = "b" });
        Assert.True(conf.Reply.Ok, conf.Reply.Message);
        Assert.Empty(h.Room.Ready);

        h.Ready("Петро");
        Assert.True(h.Act(h.Room.SeatOf(h.Room.Host)!.Value, LiveBots.Toggle).Ok, h.Reply.Message);
        Assert.Empty(h.Room.Ready);
    }

    [Fact]
    public void Rematch_from_not_the_host_asks_and_shows_who_waits()
    {
        var h = Played();
        Assert.Equal("Оля", h.Room.Host);
        var r = h.Rematch("Петро", force: false);
        Assert.False(r.Ok);
        Assert.Equal(["Оля"], r.NotReady);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal(1, h.Room.Round);
        Assert.Contains("Петро", h.Room.Ready);   // хто тиснув — готовий

        // Оля тепер тисне сама: Петро вже готовий, тож питати нема кого.
        Assert.True(h.Rematch("Оля", force: false).Ok, h.Reply.Message);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(2, h.Room.Round);
        Assert.Empty(h.Room.Ready);
    }

    [Fact]
    public void Rematch_with_force_goes_without_asking()
    {
        var h = Played();
        Assert.True(h.Rematch("Петро", force: true).Ok, h.Reply.Message);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void Bot_is_always_ready()
    {
        var h = new RoomHarness("brid");
        h.Join("Оля");
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok, h.Reply.Message);
        Assert.True(h.Start(force: false).Ok, h.Reply.Message);
        Assert.Null(h.Reply.NotReady);
    }

    [Fact]
    public void Ready_refusals()
    {
        var h = Party("Оля", "Петро");
        Assert.Equal("Ти тут не граєш", h.Ready("Глядач").Message);
        Assert.False(h.Rooms.SetReady("нема", "Оля", true).Reply.Ok);
        Assert.False(h.Rooms.SetReady(h.RoomId, "", true).Reply.Ok);

        var solo = new RoomHarness("t-solo");
        solo.Solo("Оля");
        Assert.False(solo.Ready("Оля").Ok);
    }

    [Fact]
    public void Ready_survives_a_restart()
    {
        var h = Party();
        h.Ready("Петро");
        var tables = h.Rooms.Freeze();
        // Знімок лягає на диск JSON-ом (TablesKeeper): поле мусить пережити й дорогу туди-назад.
        var json = JsonSerializer.Serialize(tables, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var back = JsonSerializer.Deserialize<FrozenTables>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var rooms = new Rooms(h.Registry, h.Clock, new GameEvents(), new FakeStakes(), h.Store, RoomHarness.Empty());
        rooms.Restore(back);
        var room = rooms.Find(h.RoomId)!;
        Assert.Contains("Петро", room.Ready);
        Assert.DoesNotContain("Оля", room.Ready);

        // Старий знімок без поля — просто ніхто не готовий.
        var old = back with { Rooms = [.. back.Rooms.Select(f => f with { Ready = null })] };
        var rooms2 = new Rooms(h.Registry, h.Clock, new GameEvents(), new FakeStakes(), h.Store, RoomHarness.Empty());
        rooms2.Restore(old);
        Assert.Empty(rooms2.Find(h.RoomId)!.Ready);
    }
}
