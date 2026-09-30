using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Голос столу в іграх зі своїми правилами (Посиденьки, <see cref="Game.Voice"/>): мафія вночі шепочеться лише між
/// собою, мертві говорять лише на лаву; капітан у Позивних мовчить, поки ходить його команда. І те саме — через
/// VoiceChat, як це побачать браузери.
/// </summary>
public sealed class VoiceRulesTests
{
    static readonly string[] Villagers = ["Оля", "Петро", "Ганна", "Микола", "Іван"];

    // ---------- мафія ----------

    static RoomHarness Mafia(int players = 5)
    {
        var h = new RoomHarness("mafia", seed: 3);
        for (var i = 0; i < players; i++) h.Join(Villagers[i]);
        h.Start();
        return h;
    }

    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;

    static void To(RoomHarness h, string phase)
    {
        for (var i = 0; i < 2000 && Phase(h) != phase; i++) h.Tick();
        Assert.Equal(phase, Phase(h));
    }

    static Dictionary<int, string> Roles(RoomHarness h)
    {
        var map = new Dictionary<int, string>();
        for (var seat = 0; seat < h.Room.Seats.Length; seat++)
        {
            if (h.Room.Seats[seat] is null) continue;
            var me = h.View(seat).GetProperty("me");
            if (me.ValueKind == JsonValueKind.Object) map[seat] = me.GetProperty("role").GetString()!;
        }
        return map;
    }

    static VoiceRule? Rule(RoomHarness h, int? seat)
    {
        lock (h.Room.Sync) return h.Room.Game.Voice(seat);
    }

    static bool Hears(VoiceRule? listener, VoiceRule? speaker) => (listener ?? VoiceRule.All).Hears(speaker ?? VoiceRule.All);

    [Fact]
    public void Mafia_lobby_and_day_everyone_talks()
    {
        var h = new RoomHarness("mafia", seed: 3);
        foreach (var n in Villagers) h.Join(n);
        Assert.Null(Rule(h, 0));   // лобі — як скаже каркас (усі всіх)
        h.Start();
        To(h, "day");
        var roles = Roles(h);
        foreach (var a in roles.Keys)
            foreach (var b in roles.Keys)
                if (a != b) Assert.True(Hears(Rule(h, a), Rule(h, b)), $"{a} не чує {b} удень");
    }

    [Fact]
    public void Mafia_night_only_mafia_whispers()
    {
        var h = Mafia();
        To(h, "night");
        var roles = Roles(h);
        var mafia = roles.Where(r => r.Value == "mafia").Select(r => r.Key).ToList();
        var civil = roles.Where(r => r.Value != "mafia").Select(r => r.Key).ToList();
        Assert.NotEmpty(mafia);
        Assert.NotEmpty(civil);
        foreach (var m in mafia)
        {
            foreach (var c in civil)
            {
                Assert.False(Hears(Rule(h, c), Rule(h, m)), "мирний чує мафію вночі");
                Assert.False(Hears(Rule(h, m), Rule(h, c)), "мафія чує мирного вночі");
            }
            foreach (var m2 in mafia.Where(x => x != m)) Assert.True(Hears(Rule(h, m2), Rule(h, m)));
            Assert.False(Hears(Rule(h, null), Rule(h, m)), "глядач чує мафію вночі");
        }
        foreach (var c in civil) foreach (var c2 in civil.Where(x => x != c)) Assert.False(Hears(Rule(h, c2), Rule(h, c)), "мирні шепочуться вночі");
    }

    [Fact]
    public void Mafia_dead_and_spectators_sit_on_the_bench()
    {
        var h = Mafia();
        To(h, "night");
        var roles = Roles(h);
        var killer = roles.First(r => r.Value == "mafia").Key;
        var victim = roles.First(r => r.Value != "mafia").Key;
        foreach (var (seat, role) in roles) if (role == "mafia") h.Act(seat, "kill", new { seat = victim });
        To(h, "day");
        Assert.False(h.View(null).GetProperty("players").EnumerateArray().First(p => p.GetProperty("seat").GetInt32() == victim).GetProperty("alive").GetBoolean());

        var alive = roles.Keys.Where(s => s != victim).ToList();
        foreach (var s in alive)
        {
            Assert.False(Hears(Rule(h, s), Rule(h, victim)), "живий чує мертвого");
            Assert.True(Hears(Rule(h, victim), Rule(h, s)), "мертвий не чує живих");
            Assert.False(Hears(Rule(h, s), Rule(h, null)), "живий чує глядача");
        }
        Assert.True(Hears(Rule(h, null), Rule(h, victim)), "глядач і мертвий не говорять між собою");
        Assert.True(Hears(Rule(h, victim), Rule(h, null)));

        // Наступної ночі мертвий чує мафію (ролі й так знає), а глядач — ні.
        To(h, "night");
        Assert.True(Hears(Rule(h, victim), Rule(h, killer)));
        Assert.False(Hears(Rule(h, null), Rule(h, killer)));
    }

    [Fact]
    public void Mafia_night_through_voice_chat()
    {
        var h = Mafia();
        var voice = new VoiceChat(h.Rooms, new FixedOptions<VoiceChatOptions>(new VoiceChatOptions()));
        var peers = new[] { "aaaaaaaaaaaa", "bbbbbbbbbbbb", "cccccccccccc", "dddddddddddd", "eeeeeeeeeeee" };
        for (var i = 0; i < 5; i++) Assert.True(voice.Join("c" + i, Villagers[i], true, peers[i], h.RoomId, false, false).Reply.Ok);
        To(h, "night");
        voice.Refresh();
        var roles = Roles(h);
        var mafia = roles.First(r => r.Value == "mafia").Key;
        var civil = roles.First(r => r.Value != "mafia").Key;
        var link = voice.MeOf("c" + mafia)!.Links.Single(l => l.Peer == peers[civil]);
        Assert.False(link.Send);
        Assert.False(link.Recv);
        To(h, "day");
        voice.Refresh();
        link = voice.MeOf("c" + mafia)!.Links.Single(l => l.Peer == peers[civil]);
        Assert.True(link.Send && link.Recv);
    }

    // ---------- Позивні ----------

    static RoomHarness Pozyvni()
    {
        var words = new PictionaryWords(new[] { "кіт", "пес", "море", "ліс", "хата", "вода", "небо", "сонце", "ніч", "стіл", "вікно", "книга", "мед", "сир", "гора",
            "річка", "пісок", "вогонь", "сніг", "дощ", "кінь", "вовк", "риба", "птах", "дерево", "камінь", "зірка", "дорога", "хліб", "сіль" }
            .Select(w => ("animals", w)));
        var h = new RoomHarness("pozyvni", seed: 7, services: RoomHarness.WithService(words));
        foreach (var nick in new[] { "Оля", "Петро", "Ганна", "Влад" }) h.Join(nick);
        h.Start();
        Assert.True(h.Act(0, "go").Ok);
        return h;
    }

    static string Side(RoomHarness h) => h.View(null).GetProperty("side").GetString()!;

    static int Boss(RoomHarness h, string side) =>
        h.View(null).GetProperty("teams").GetProperty(side).GetProperty("boss").GetInt32();

    [Fact]
    public void Pozyvni_captain_listens_while_his_team_moves()
    {
        var h = Pozyvni();
        var side = Side(h);
        var foe = side == "red" ? "blue" : "red";
        var boss = Boss(h, side);
        var foeBoss = Boss(h, foe);
        Assert.Same(VoiceRule.Listen, Rule(h, boss));
        Assert.Null(Rule(h, foeBoss));   // капітан суперника говорить
        Assert.True(h.Act(boss, "clue", new { word = "натяк", count = 1 }).Ok);
        Assert.Same(VoiceRule.Listen, Rule(h, boss));   // команда шукає — капітан так само мовчить
        var field = Enumerable.Range(0, 4).First(s => s != boss && h.View(null).GetProperty("teams").GetProperty(side)
            .GetProperty("seats").EnumerateArray().Any(e => e.GetInt32() == s));
        Assert.Null(Rule(h, field));
        Assert.True(h.Act(field, "pass").Ok);
        Assert.Null(Rule(h, boss));                     // хід перейшов — говорить знову
        Assert.Same(VoiceRule.Listen, Rule(h, foeBoss));
    }
}
