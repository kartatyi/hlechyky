using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Прохід №3 (29.09): Глек-ведучий уголос (лише за столом, без ролей, гра на голос не чекає) і 🤖 селяни-боти, що
/// добирають стіл із лобі й самі доводять партію до кінця — без черепків і ачівок.
/// </summary>
public class MafiaSweep3Tests
{
    static readonly string[] Villagers = ["Оля", "Петро", "Ганна", "Микола", "Іван", "Марія"];

    static IServiceProvider Voiced(IDotepyVoice voice) => new ServiceCollection().AddSingleton(voice).BuildServiceProvider();

    static RoomHarness Table(int players, object? options = null, int bots = 0, IDotepyVoice? voice = null, int seed = 3)
    {
        var h = new RoomHarness("mafia", options, seed, voice is null ? null : Voiced(voice));
        for (var i = 0; i < players; i++) h.Join(Villagers[i]);
        if (bots > 0) Assert.True(h.Act(0, "bots", bots).Ok);
        return h;
    }

    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;

    static string? Said(RoomHarness h, int? seat)
    {
        var say = h.View(seat).GetProperty("say");
        return say.ValueKind == JsonValueKind.Object ? say.GetProperty("text").GetString() : null;
    }

    static void Until(RoomHarness h, Func<bool> done, int limit = 2000)
    {
        for (var i = 0; i < limit && !done(); i++) h.Tick();
        Assert.True(done(), $"не дочекались: фаза {Phase(h)}");
    }

    // =========================================================================================
    // Глек уголос
    // =========================================================================================

    [Fact]
    public void Host_speaks_to_the_table_but_not_to_a_spectator()
    {
        var h = Table(4, voice: new DotepyTests.FakeVoice());
        h.Start();
        Assert.Equal(MafiaVoiceLines.Intro, Said(h, 0));
        Assert.Null(Said(h, null));
        Until(h, () => Phase(h) == "night");
        Assert.Contains("Прокидається мафія", Said(h, 1));
        Assert.Equal("ostap", h.View(0).GetProperty("voice").GetString());
    }

    [Fact]
    public void Every_night_line_and_the_names_are_voiced_ahead()
    {
        var voice = new DotepyTests.FakeVoice();
        var h = Table(4, voice: voice);
        h.Start();
        var texts = voice.Prepared.Select(p => p.Text).ToHashSet();
        Assert.Contains(MafiaVoiceLines.Night, texts);
        Assert.Contains(MafiaVoiceLines.CivilWin, texts);
        foreach (var nick in Villagers.Take(4))
        {
            Assert.Contains(MafiaVoiceLines.Killed(nick), texts);
            Assert.Contains(MafiaVoiceLines.Exiled(nick), texts);
        }
    }

    [Fact]
    public void Without_voice_option_host_is_silent()
    {
        var voice = new DotepyTests.FakeVoice();
        var h = Table(4, new { voice = "none" }, voice: voice);
        h.Start();
        Assert.Null(Said(h, 0));
        Assert.Empty(voice.Prepared);
    }

    [Fact]
    public void Game_never_waits_for_the_voice()
    {
        // Той самий сід, той самий стіл: без голосу й з голосом, що не доспіває ніколи, — фази міняються в ті самі тики.
        static List<string> Run(IDotepyVoice? v)
        {
            var h = Table(5, bots: 0, voice: v, seed: 11);
            h.Start();
            var seen = new List<string>();
            for (var i = 0; i < 400 && Phase(h) != "done"; i++) { h.Tick(); seen.Add(Phase(h)); }
            return seen;
        }
        Assert.Equal(Run(null), Run(new DotepyTests.FakeVoice(readyAfter: -1)));
    }

    [Fact]
    public void Voice_never_names_a_role_even_when_the_table_reveals_them()
    {
        var h = Table(3, new { reveal = "on" }, bots: 4, voice: new DotepyTests.FakeVoice());
        h.Start();
        var heard = new List<string>();
        for (var i = 0; i < 2000 && Phase(h) != "done"; i++)
        {
            h.Tick();
            if (Said(h, 0) is { } t && (heard.Count == 0 || heard[^1] != t)) heard.Add(t);
        }
        Assert.Equal("done", Phase(h));
        Assert.True(heard.Count >= 4);
        foreach (var t in heard)
            foreach (var role in new[] { "комісар", "лікар", "мирн", "дон", "кума", "маньяк" })
                if (!t.StartsWith("Гру закінчено", StringComparison.Ordinal)) Assert.DoesNotContain(role, t, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================================
    // 🤖 Боти
    // =========================================================================================

    [Fact]
    public void Two_friends_need_a_bot_to_start()
    {
        var h = Table(2);
        Assert.False(h.Start().Ok);
        Assert.True(h.Act(1, "bots", 1).Ok);
        Assert.True(h.Start().Ok);
        var players = h.View(0).GetProperty("players").EnumerateArray().ToList();
        Assert.Equal(3, players.Count);
        Assert.Single(players, p => p.GetProperty("bot").GetBoolean());
    }

    [Fact]
    public void Lobby_shows_where_bots_will_sit_and_caps_them_by_free_benches()
    {
        var h = Table(3, bots: 2);
        var bots = h.View(null).GetProperty("players").EnumerateArray().Where(p => p.GetProperty("bot").GetBoolean()).ToList();
        Assert.Equal(2, bots.Count);
        Assert.All(bots, b => Assert.StartsWith("🤖", b.GetProperty("nick").GetString()));
        Assert.Equal(2, h.View(0).GetProperty("bots").GetInt32());
        Assert.False(h.Act(0, "bots", 10).Ok);   // 3 людини + 10 > 12
        Assert.True(h.Act(0, "bots", 0).Ok);
        Assert.Equal(0, h.View(0).GetProperty("bots").GetInt32());
    }

    [Fact]
    public void Bots_play_a_whole_game_and_get_nothing_for_it()
    {
        for (var seed = 1; seed <= 12; seed++)
        {
            var h = Table(2, bots: 5, seed: seed);
            Assert.True(h.Start().Ok);
            // Люди лише ріжуть (якщо вони мафія) — решту партії доводять боти й годинник.
            for (var i = 0; i < 3000 && Phase(h) != "done"; i++)
            {
                h.Tick();
                if (Phase(h) != "night") continue;
                for (var s = 0; s < 2; s++)
                {
                    var v = h.View(s);
                    if (v.GetProperty("me").GetProperty("role").GetString() != "mafia") continue;
                    var prey = v.GetProperty("players").EnumerateArray().FirstOrDefault(p =>
                        p.GetProperty("alive").GetBoolean() && p.GetProperty("role").ValueKind == JsonValueKind.Null);
                    if (prey.ValueKind == JsonValueKind.Object) h.Act(s, "kill", prey.GetProperty("seat").GetInt32());
                }
            }
            Assert.Equal("done", Phase(h));
            var fin = Assert.Single(h.Finished);
            Assert.All(fin.Result.Winners, w => Assert.NotNull(fin.Seats[w]));
            Assert.All(h.Awards, a => Assert.DoesNotContain("🤖", a.Nick));
        }
    }

    [Fact]
    public void Bot_mafia_backs_its_human_partner_at_night()
    {
        // Шукаємо роздачу, де людина — мафія, а поруч мафіозі-бот: бот показує туди ж, куди людина.
        for (var seed = 1; seed <= 60; seed++)
        {
            var h = Table(3, new { mafia = "2" }, bots: 4, seed: seed);
            h.Start();
            var me = h.View(0).GetProperty("me").GetProperty("role").GetString();
            if (me != "mafia") continue;
            var mate = h.View(0).GetProperty("players").EnumerateArray()
                .FirstOrDefault(p => p.GetProperty("bot").GetBoolean() && p.GetProperty("role").GetString() == "mafia");
            if (mate.ValueKind != JsonValueKind.Object) continue;
            Until(h, () => Phase(h) == "night");
            var victim = h.View(0).GetProperty("players").EnumerateArray()
                .First(p => p.GetProperty("role").ValueKind == JsonValueKind.Null).GetProperty("seat").GetInt32();
            Assert.True(h.Act(0, "kill", victim).Ok);
            var bot = mate.GetProperty("seat").GetInt32();
            Until(h, () => h.View(0).GetProperty("night").GetProperty("votes").TryGetProperty(bot.ToString(), out _) || Phase(h) != "night");
            Assert.Equal(victim, h.View(0).GetProperty("night").GetProperty("votes").GetProperty(bot.ToString()).GetInt32());
            return;
        }
        Assert.Fail("не знайшлось роздачі з людиною й ботом у мафії");
    }

    [Fact]
    public void Bots_survive_save_and_load()
    {
        var h = Table(2, bots: 2);
        h.Start();
        var game = (Mafia)h.Room.Game;
        var json = game.Save()!;
        var copy = new Mafia();
        copy.Load(json);
        Assert.Contains("\"bots\":[", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, JsonDocument.Parse(json).RootElement.GetProperty("bots").GetArrayLength());
    }
}
