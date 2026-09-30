using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>Посиденьки (VoiceChat.cs): хто де говорить, хто кого чує, куди летять листи між браузерами.</summary>
public sealed class VoiceChatTests
{
    const string PeerA = "aaaaaaaaaaaa", PeerB = "bbbbbbbbbbbb", PeerC = "cccccccccccc";

    static VoiceChat NewVoice(RoomHarness h, VoiceChatOptions? o = null) => new(h.Rooms, new FixedOptions<VoiceChatOptions>(o ?? new VoiceChatOptions()));

    static JsonElement Json(object? payload) => JsonSerializer.SerializeToElement(payload);

    /// <summary>Останній voiceMe для з'єднання з цієї пачки (null — не було).</summary>
    static VoiceMeDto? MeOf(IEnumerable<VoiceSend> sends, string conn) =>
        sends.LastOrDefault(s => s.To == conn && s.Event == "voiceMe")?.Payload as VoiceMeDto;

    static VoiceRosterDto? RosterOf(IEnumerable<VoiceSend> sends) =>
        sends.LastOrDefault(s => s.To is null && s.Event == "voice")?.Payload as VoiceRosterDto;

    static VoiceLink LinkTo(VoiceMeDto me, string peer) => me.Links.Single(l => l.Peer == peer);

    [Fact]
    public void Guest_cannot_join()
    {
        var v = NewVoice(new RoomHarness("t-party"));
        var o = v.Join("c1", "гість Оля", account: false, PeerA, null, false, false);
        Assert.False(o.Reply.Ok);
        Assert.Equal(VoiceChat.NotAccount, o.Reply.Error);
        Assert.Empty(o.Sends);
        Assert.Empty(v.Roster.Rooms);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("UPPERCASEPEER1")]
    [InlineData("has space here")]
    public void Bad_peer_is_refused(string? peer)
    {
        var v = NewVoice(new RoomHarness("t-party"));
        Assert.Equal(VoiceChat.BadPeer, v.Join("c1", "Оля", true, peer, null, false, false).Reply.Error);
    }

    [Fact]
    public void Disabled_voice_refuses()
    {
        var v = NewVoice(new RoomHarness("t-party"), new VoiceChatOptions { Enabled = false });
        Assert.Equal(VoiceChat.Off, v.Join("c1", "Оля", true, PeerA, null, false, false).Reply.Error);
    }

    [Fact]
    public void Join_lands_in_home_and_everyone_sees_it()
    {
        var v = NewVoice(new RoomHarness("t-party"));
        var o = v.Join("c1", "Оля", true, PeerA, null, false, false);
        Assert.True(o.Reply.Ok);
        Assert.Equal(VoiceChat.Home, o.Reply.Room);
        Assert.NotEmpty(o.Reply.Ice!);
        var roster = RosterOf(o.Sends)!;
        var home = Assert.Single(roster.Rooms);
        Assert.Equal(VoiceChat.Home, home.Id);
        Assert.Equal(VoiceChat.HomeTitle, home.Title);
        Assert.Equal("Оля", Assert.Single(home.Members).Nick);
        Assert.Empty(MeOf(o.Sends, "c1")!.Links);
    }

    [Fact]
    public void Two_in_home_hear_each_other()
    {
        var v = NewVoice(new RoomHarness("t-party"));
        v.Join("c1", "Оля", true, PeerA, null, false, false);
        var o = v.Join("c2", "Петро", true, PeerB, null, false, false);
        var a = MeOf(o.Sends, "c1")!;
        var b = MeOf(o.Sends, "c2")!;
        Assert.Equal(new VoiceLink(PeerB, true, true, false), LinkTo(a, PeerB));
        Assert.Equal(new VoiceLink(PeerA, true, true, false), LinkTo(b, PeerA));
    }

    [Fact]
    public void Same_nick_from_another_tab_kicks_the_first()
    {
        var v = NewVoice(new RoomHarness("t-party"));
        v.Join("c1", "Оля", true, PeerA, null, false, false);
        var o = v.Join("c2", "оля", true, PeerB, null, false, false);
        Assert.True(o.Reply.Ok);
        Assert.Contains(o.Sends, s => s.To == "c1" && s.Event == "voiceKick");
        Assert.Equal(PeerB, Assert.Single(Assert.Single(v.Roster.Rooms).Members).Peer);
    }

    [Fact]
    public void Same_peer_on_new_connection_is_a_reconnect_not_a_kick()
    {
        var h = new RoomHarness("t-party");
        h.Join("Оля"); h.Join("Петро");
        var v = NewVoice(h);
        v.Join("c1", "Оля", true, PeerA, h.RoomId, false, false);
        // Сервер ще не помітив, що c1 зник, а вкладка вже зайшла з c2 — тим самим позивним.
        var o = v.Join("c2", "Оля", true, PeerA, null, true, false);
        Assert.True(o.Reply.Ok);
        Assert.DoesNotContain(o.Sends, s => s.Event == "voiceKick");
        Assert.Equal("t:" + h.RoomId, o.Reply.Room);   // лишився в голосі столу
        Assert.True(Assert.Single(Assert.Single(v.Roster.Rooms).Members).Muted);
    }

    [Fact]
    public void Peer_of_another_nick_is_refused()
    {
        var v = NewVoice(new RoomHarness("t-party"));
        v.Join("c1", "Оля", true, PeerA, null, false, false);
        Assert.Equal(VoiceChat.BadPeer, v.Join("c2", "Петро", true, PeerA, null, false, false).Reply.Error);
    }

    [Fact]
    public void Leave_and_drop_update_everyone()
    {
        var v = NewVoice(new RoomHarness("t-party"));
        v.Join("c1", "Оля", true, PeerA, null, false, false);
        v.Join("c2", "Петро", true, PeerB, null, false, false);
        var sends = v.Leave("c2");
        Assert.Equal("Оля", Assert.Single(RosterOf(sends)!.Rooms.Single().Members).Nick);
        Assert.Empty(MeOf(sends, "c1")!.Links);
        Assert.Empty(RosterOf(v.Drop("c1"))!.Rooms);
        Assert.Empty(v.Leave("c1"));
    }

    [Fact]
    public void Room_is_capped()
    {
        var v = NewVoice(new RoomHarness("t-party"), new VoiceChatOptions { MaxPerRoom = 2 });
        v.Join("c1", "Оля", true, PeerA, null, false, false);
        v.Join("c2", "Петро", true, PeerB, null, false, false);
        var o = v.Join("c3", "Іра", true, PeerC, null, false, false);
        Assert.False(o.Reply.Ok);
        Assert.Contains("2", o.Reply.Error);
    }

    [Fact]
    public void Mute_is_seen_and_repeats_are_quiet()
    {
        var v = NewVoice(new RoomHarness("t-party"));
        v.Join("c1", "Оля", true, PeerA, null, false, false);
        var sends = v.Set("c1", true, false);
        Assert.True(RosterOf(sends)!.Rooms.Single().Members.Single().Muted);
        Assert.Empty(v.Set("c1", true, false));
        Assert.Empty(v.Set("nobody", true, true));
    }

    [Fact]
    public void Signals_go_only_to_the_same_room()
    {
        var h = new RoomHarness("t-party");
        h.Join("Оля"); h.Join("Петро");
        var v = NewVoice(h);
        v.Join("c1", "Оля", true, PeerA, null, false, false);
        v.Join("c2", "Петро", true, PeerB, null, false, false);
        v.Join("c3", "Іра", true, PeerC, null, false, false);

        var s = Assert.Single(v.Signal("c1", PeerB, "{\"d\":1}", 100));
        Assert.Equal("c2", s.To);
        Assert.Equal("voiceSignal", s.Event);
        Assert.Equal(PeerA, Json(s.Payload).GetProperty("from").GetString());

        // Петро пішов у голос столу — Оля з Посиденьок до нього вже не пише.
        Assert.Null(v.Follow("c2", h.RoomId).Reply);
        Assert.Empty(v.Signal("c1", PeerB, "{}", 100));
        Assert.Empty(v.Signal("c1", PeerA, "{}", 100));         // сам собі
        Assert.Empty(v.Signal("c1", "zzzzzzzzzzzz", "{}", 100)); // нема такого
        Assert.Empty(v.Signal("c9", PeerC, "{}", 100));          // не в голосі
        Assert.Empty(v.Signal("c1", PeerC, new string('x', VoiceChat.MaxSignalChars + 1), 100));
    }

    [Fact]
    public void Signals_have_their_own_quota()
    {
        var v = NewVoice(new RoomHarness("t-party"));
        v.Join("c1", "Оля", true, PeerA, null, false, false);
        v.Join("c2", "Петро", true, PeerB, null, false, false);
        var sent = Enumerable.Range(0, VoiceChat.SignalsPerSecond + 5).Count(_ => v.Signal("c1", PeerB, "{}", 7).Count == 1);
        Assert.Equal(VoiceChat.SignalsPerSecond, sent);
        Assert.Single(v.Signal("c1", PeerB, "{}", 8));   // нова секунда — нова квота
    }

    [Fact]
    public void Table_voice_is_for_the_seated_and_watchers()
    {
        var h = new RoomHarness("t-party");
        h.Join("Оля"); h.Join("Петро");
        var v = NewVoice(h);
        Assert.Equal(VoiceChat.NotAtTable, v.Join("c3", "Іра", true, PeerC, h.RoomId, false, false).Reply.Error);
        h.Rooms.Watch(h.RoomId, "c3", "Іра");
        Assert.True(v.Join("c3", "Іра", true, PeerC, h.RoomId, false, false).Reply.Ok);
        var o = v.Join("c1", "Оля", true, PeerA, h.RoomId, false, false);
        Assert.Equal("t:" + h.RoomId, o.Reply.Room);
        var table = RosterOf(o.Sends)!.Rooms.Single(r => r.Table == h.RoomId);
        Assert.Equal("t-party", table.Game);
        Assert.Equal("Тестова компанія", table.Title);
        Assert.Equal(2, table.Members.Count);
        Assert.Equal(VoiceChat.NotAtTable, v.Follow("c1", "nope").Reply);
    }

    [Fact]
    public void Who_left_the_table_goes_back_home()
    {
        var h = new RoomHarness("t-party");
        h.Join("Оля"); h.Join("Петро");
        var v = NewVoice(h);
        v.Join("c1", "Оля", true, PeerA, h.RoomId, false, false);
        v.Join("c2", "Петро", true, PeerB, h.RoomId, false, false);
        h.Leave("Петро");
        var sends = v.Refresh();
        Assert.Equal(VoiceChat.Home, MeOf(sends, "c2")!.Room);
        var roster = RosterOf(sends)!;
        Assert.Equal("Петро", roster.Rooms.Single(r => r.Id == VoiceChat.Home).Members.Single().Nick);
        Assert.Equal("Оля", roster.Rooms.Single(r => r.Table == h.RoomId).Members.Single().Nick);
        Assert.Empty(v.Refresh());   // нічого не змінилось — нічого й не летить
    }

    [Fact]
    public void Game_decides_who_hears_whom_mid_match()
    {
        var h = new RoomHarness("t-voice");
        h.Join("Оля"); h.Join("Петро");
        h.Rooms.Watch(h.RoomId, "c3", "Іра");
        h.Rooms.Watch(h.RoomId, "c4", "Сашко");
        var v = NewVoice(h);
        v.Join("c1", "Оля", true, PeerA, h.RoomId, false, false);
        v.Join("c2", "Петро", true, PeerB, h.RoomId, false, false);
        v.Join("c3", "Іра", true, PeerC, h.RoomId, false, false);
        v.Join("c4", "Сашко", true, "dddddddddddd", h.RoomId, false, false);
        // У лобі чують усі всіх.
        Assert.All(v.MeOf("c3")!.Links, l => Assert.True(l.Send && l.Recv));

        h.Start();
        Assert.Contains(v.Refresh(), s => s.Event == "voiceMe");
        var olya = v.MeOf("c1")!;
        var petro = v.MeOf("c2")!;
        var ira = v.MeOf("c3")!;
        // Петро (місце 1) лише слухає: Олю чує, його — ніхто.
        Assert.Equal(new VoiceLink(PeerA, false, true, false), LinkTo(petro, PeerA));
        Assert.Equal(new VoiceLink(PeerB, true, false, false), LinkTo(olya, PeerB));
        // Глядачка на лаві: стіл чує її — ні, вона стіл — так; з іншим глядачем говорять обоє.
        Assert.Equal(new VoiceLink(PeerA, false, true, false), LinkTo(ira, PeerA));
        Assert.Equal(new VoiceLink("dddddddddddd", true, true, false), LinkTo(ira, "dddddddddddd"));

        // Партія скінчилась — знову всі всіх.
        h.Leave("Петро");   // типовий OnLeave закінчує партію
        v.Refresh();
        Assert.All(v.MeOf("c3")!.Links, l => Assert.True(l.Send && l.Recv));
    }

    [Fact]
    public void Share_and_watch()
    {
        var v = NewVoice(new RoomHarness("t-party"));
        v.Join("c1", "Оля", true, PeerA, null, false, false);
        v.Join("c2", "Петро", true, PeerB, null, false, false);
        Assert.NotNull(v.Watch("c2", PeerA, true).Reply);   // Оля ще нічого не показує
        Assert.True(RosterOf(v.Share("c1", true))!.Rooms.Single().Members.Single(m => m.Peer == PeerA).Share);
        var o = v.Watch("c2", PeerA, true);
        Assert.Null(o.Reply);
        Assert.True(LinkTo(MeOf(o.Sends, "c1")!, PeerB).Watch);
        var off = v.Share("c1", false);
        Assert.False(LinkTo(MeOf(off, "c1")!, PeerB).Watch);
    }

    [Fact]
    public void Public_roster_hides_peers()
    {
        var v = NewVoice(new RoomHarness("t-party"));
        v.Join("c1", "Оля", true, PeerA, null, true, false);
        Assert.Equal(PeerA, v.Roster.Rooms.Single().Members.Single().Peer);
        var pub = v.PublicRoster.Rooms.Single().Members.Single();
        Assert.Equal("", pub.Peer);
        Assert.Equal("Оля", pub.Nick);
        Assert.True(pub.Muted);
    }

    [Fact]
    public void Turn_gets_time_limited_credentials()
    {
        var o = new VoiceChatOptions { TurnSecret = "s3cret", TurnUrls = ["turn:example.test:3478"], TurnTtlHours = 2 };
        var v = NewVoice(new RoomHarness("t-party"), o);
        var ice = v.Join("c1", "Оля", true, PeerA, null, false, false).Reply.Ice!;
        var turn = Assert.Single(ice, s => s.Urls.Contains("turn:example.test:3478"));
        var parts = turn.Username!.Split(':');
        Assert.Equal(PeerA, parts[1]);
        Assert.InRange(long.Parse(parts[0]) - DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 2 * 3600 - 60, 2 * 3600 + 60);
        var expect = Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes("s3cret"), Encoding.UTF8.GetBytes(turn.Username)));
        Assert.Equal(expect, turn.Credential);
        Assert.Contains(ice, s => s.Urls.Any(u => u.StartsWith("stun:")));
    }
}
