using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Балачка столу: хто може говорити, що бачить той, хто щойно підійшов, і куди це летить. Правила конкретної гри
/// (мертві в мафії мовчать) перевіряє її власний тест; тут — каркас, однаковий для всіх столів.
/// </summary>
public class TableTalkTests
{
    static (Outbox Out, string? Error) Say(RoomHarness h, string nick, string text, string? conn = null) =>
        h.Rooms.TableSay(h.RoomId, conn, nick, text);

    static RoomHarness Duel()
    {
        var h = new RoomHarness("ttt");
        h.Join("Оля");
        h.Join("Петро");
        return h;
    }

    [Fact]
    public void A_seated_player_talks_and_the_line_goes_to_whoever_watches_the_table()
    {
        var h = Duel();

        var (outbox, error) = Say(h, "Оля", "  ходи вже  ");

        Assert.Null(error);
        var said = Assert.Single(outbox.OfType<TableSaid>());
        Assert.Equal(h.RoomId, said.RoomId);
        Assert.Equal(("Оля", "ходи вже", "chat"), (said.Line.Nick, said.Line.Text, said.Line.Kind));
        Assert.Equal(said.Line, Assert.Single(h.Room.Talk));
    }

    [Fact]
    public void A_passer_by_must_first_come_up_to_the_table()
    {
        var h = Duel();

        Assert.Equal("Спершу підійди до столу", Say(h, "Ганна", "привіт").Error);
        Assert.Equal("Спершу підійди до столу", Say(h, "Ганна", "привіт", "c-ганна").Error);   // з'єднання не підписане

        h.Rooms.Watch(h.RoomId, "c-ганна", "Ганна");
        Assert.Null(Say(h, "Ганна", "привіт", "c-ганна").Error);
        Assert.Null(h.Rooms.TalkRefusal(h.RoomId, "c-ганна", "Ганна"));

        h.Rooms.Unwatch(h.RoomId, "c-ганна");
        Assert.NotNull(h.Rooms.TalkRefusal(h.RoomId, "c-ганна", "Ганна"));
    }

    [Fact]
    public void An_empty_line_or_a_nameless_guest_says_nothing()
    {
        var h = Duel();

        Assert.NotNull(Say(h, "Оля", "   ").Error);
        Assert.NotNull(Say(h, "гість", "а я?").Error);
        Assert.Empty(h.Room.Talk);
    }

    [Fact]
    public void A_solo_room_has_no_table_talk()
    {
        var h = new RoomHarness("t-solo");
        h.Solo("Оля");

        Assert.NotNull(Say(h, "Оля", "сам із собою").Error);
        Assert.Empty(h.Rooms.TableLines(h.RoomId));
        h.Rooms.Watch(h.RoomId, "c1", "Оля");
        Assert.Null(h.Rooms.TalkHistory(h.RoomId, "c1"));
    }

    [Fact]
    public void Whoever_comes_up_gets_the_whole_conversation()
    {
        var h = Duel();
        Say(h, "Оля", "перша");
        Say(h, "Петро", "друга");

        h.Rooms.Watch(h.RoomId, "c-ганна", "Ганна");
        var history = h.Rooms.TalkHistory(h.RoomId, "c-ганна")!;

        Assert.Equal("c-ганна", history.ConnectionId);
        Assert.Equal(["перша", "друга"], history.Lines.Select(l => l.Text));
    }

    [Fact]
    public void Only_a_connection_that_watches_the_table_gets_its_conversation()
    {
        var h = Duel();
        Say(h, "Оля", "секрет столу");

        Assert.Null(h.Rooms.TalkHistory(h.RoomId, "c-чужий"));          // не підписаний — нічого
        Assert.Null(h.Rooms.TalkHistory("такого-нема", "c-чужий"));
        h.Rooms.Watch(h.RoomId, "c-чужий", "Ганна");
        Assert.Single(h.Rooms.TalkHistory(h.RoomId, "c-чужий")!.Lines);
    }

    [Fact]
    public void A_game_is_asked_who_keeps_quiet_only_while_its_round_is_on()
    {
        var h = new RoomHarness("t-quiet");
        h.Join("Оля");
        h.Join("Петро");
        Assert.Null(Say(h, "Оля", "почнемо?").Error);                    // лобі — балакають усі

        h.Start();
        Assert.Equal(TestQuiet.Why, Say(h, "Оля", "а тепер?").Error);

        h.Act(0, "win");                                                  // партія скінчилась — гра вже не питається
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Null(Say(h, "Оля", "добра гра").Error);
    }

    [Fact]
    public void The_table_keeps_only_the_last_hundred_lines()
    {
        var h = Duel();
        for (var i = 0; i < Rooms.TalkLines + 20; i++) Say(h, i % 2 == 0 ? "Оля" : "Петро", $"рядок {i}");

        var lines = h.Rooms.TableLines(h.RoomId);
        Assert.Equal(Rooms.TalkLines, lines.Count);
        Assert.Equal("рядок 20", lines[0].Text);
        Assert.Equal(lines[^1].Id, h.Rooms.TableLastId(h.RoomId));
    }

    [Fact]
    public void A_long_line_is_cut_like_in_the_chat_and_not_in_the_middle_of_a_smile()
    {
        var h = Duel();
        var text = new string('а', Rooms.TalkChars - 1) + "😀" + "хвіст";

        var line = Assert.Single(Say(h, "Оля", text).Out.OfType<TableSaid>()).Line;

        Assert.Equal(Rooms.TalkChars - 1, line.Text.Length);   // смайл не розрізаємо навпіл
        Assert.DoesNotContain("хвіст", line.Text);
    }

    [Fact]
    public void Line_numbers_run_through_all_the_tables()
    {
        var clock = new FakeClock();
        var rooms = new Rooms(RoomHarness.NewRegistry(), clock, new GameEvents(), new FakeStakes(), new FakeStore(), RoomHarness.Empty());
        var a = rooms.Create("Оля", "ttt", null).Reply.RoomId!;
        var b = rooms.Create("Петро", "c4", null).Reply.RoomId!;

        var first = rooms.TableSay(a, null, "Оля", "тут").Out.OfType<TableSaid>().Single().Line.Id;
        var second = rooms.TableSay(b, null, "Петро", "там").Out.OfType<TableSaid>().Single().Line.Id;

        Assert.True(second > first);
        Assert.Equal(0, rooms.TableLastId("такого-нема"));
    }

    [Fact]
    public void The_broadcaster_sends_a_line_to_the_room_group_and_the_history_to_one_connection()
    {
        var h = Duel();
        var said = Say(h, "Оля", "ну що, ще раз?").Out;
        h.Rooms.Watch(h.RoomId, "c-ганна", "Ганна");
        List<Outgoing> history = [h.Rooms.TalkHistory(h.RoomId, "c-ганна")!];

        var sends = Broadcaster.Plan([.. said, .. history], h.Rooms.Snapshot, h.Rooms.SoloNow, h.Rooms.ViewsFor, _ => null, _ => [],
            (text, roomId) => new { text, roomId });

        var line = Assert.Single(sends, s => s.Event == "tableChat");
        Assert.Equal(new ToGroup("room:" + h.RoomId), line.Target);
        var body = Views.Json(line.Payload);
        Assert.Equal(h.RoomId, body.GetProperty("id").GetString());
        Assert.Equal("ну що, ще раз?", body.GetProperty("line").GetProperty("text").GetString());

        var all = Assert.Single(sends, s => s.Event == "tableHistory");
        Assert.Equal(["c-ганна"], ((ToConnections)all.Target).Ids);
        Assert.Equal(1, Views.Json(all.Payload).GetProperty("lines").GetArrayLength());
        Assert.DoesNotContain(sends, s => s.Event == "chat");   // у загальні Балачки — нічого
    }

    [Fact]
    public void The_host_line_of_a_game_lands_at_its_table_under_the_djs_name()
    {
        var h = new RoomHarness("mafia");
        foreach (var nick in new[] { "Оля", "Петро", "Ганна", "Іван" }) h.Join(nick);
        h.Start();

        var line = h.Outbox.OfType<TableSaid>().First().Line;
        Assert.Equal(("Дядько Глек", "dj"), (line.Nick, line.Kind));
        Assert.Equal(JsonValueKind.String, Views.Json(line).GetProperty("text").ValueKind);
        Assert.Contains(h.Room.Talk, l => l.Id == line.Id);
    }
}
