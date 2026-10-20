using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>«⚖ Фора» в Глекометах (пакет 3, 20.10): HP хати = 100 × множник за силою — слабшим більше, сильнішим менше.</summary>
public class GlekometForaTests
{
    sealed class FixedSkills(Dictionary<string, double> map) : ISkills
    {
        public double Strength(string gameId, string? nick) => map.TryGetValue(nick ?? "", out var s) ? s : 0;
    }

    // Оля й Ганна сильні, Петро й Іван слабкі
    static readonly Dictionary<string, double> Power = new() { ["Оля"] = 0.3, ["Петро"] = -0.2, ["Ганна"] = 0.3, ["Іван"] = -0.2 };
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван"];

    static RoomHarness Table(int players, object? options = null, int seed = 42)
    {
        var h = new RoomHarness("glekomet", options, seed, RoomHarness.WithService<ISkills>(new FixedSkills(Power)));
        foreach (var n in Nicks.Take(players)) h.Join(n);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static GlekometCore Core(RoomHarness h) => ((Glekomet)h.Room.Game).Core!;

    static JsonElement Hut(RoomHarness h, int seat) =>
        h.View(null).GetProperty("huts").EnumerateArray().First(x => x.GetProperty("seat").GetInt32() == seat);

    [Fact]
    public void The_weaker_hut_gets_more_hp_and_the_stronger_less()
    {
        var h = Table(2);
        var c = Core(h);
        // середня 0,05: Оля 1 − 0,8·0,25 = 0,8 → 80; Петро 1 + 0,8·0,25 = 1,2 → 120
        Assert.Equal(80, c.Huts[0].Hp);
        Assert.Equal(120, c.Huts[1].Hp);
        Assert.Equal(80, c.Huts[0].MaxHp);
        Assert.Equal(120, Hut(h, 1).GetProperty("hp").GetInt32());
        Assert.Equal(120, Hut(h, 1).GetProperty("maxHp").GetInt32());
        Assert.Contains(h.Outbox.OfType<Journal>(), l => l.Text.StartsWith("⚖ Фора:") && l.Text.Contains("Петро ×1,2"));
    }

    [Fact]
    public void In_teams_every_hut_gets_its_own_factor()
    {
        var h = Table(4, new { teams = "seats" });
        var c = Core(h);
        Assert.Equal([80, 120, 80, 120], Enumerable.Range(0, 4).Select(s => c.Huts[s].Hp).ToArray());
    }

    [Fact]
    public void Without_handicap_every_hut_has_a_hundred()
    {
        var h = Table(2, new { handicap = Handicap.Off });
        var c = Core(h);
        Assert.Equal(100, c.Huts[0].Hp);
        Assert.Equal(100, c.Huts[1].Hp);
        Assert.Equal(100, Hut(h, 0).GetProperty("maxHp").GetInt32());
        Assert.DoesNotContain(h.Outbox.OfType<Journal>(), l => l.Text.StartsWith("⚖ Фора:"));
    }

    [Fact]
    public void Equal_players_keep_a_hundred_and_no_note()
    {
        var h = new RoomHarness("glekomet", null, 42, RoomHarness.WithService<ISkills>(new FixedSkills([])));
        h.Join("Оля");
        h.Join("Петро");
        h.Start();
        Assert.Equal(100, Core(h).Huts[0].Hp);
        Assert.DoesNotContain(h.Outbox.OfType<Journal>(), l => l.Text.StartsWith("⚖ Фора:"));
    }
}
