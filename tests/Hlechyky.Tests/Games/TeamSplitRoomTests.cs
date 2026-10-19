using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Команди за силою за справжнім столом (пакет 1, 19.10): Кривуля й Бомбер, «Ще раз» міняє пари.</summary>
public class TeamSplitRoomTests
{
    sealed class FixedSkills(Dictionary<string, double> map) : ISkills
    {
        public double Strength(string gameId, string? nick) => map.TryGetValue(nick ?? "", out var s) ? s : 0;
    }

    // Оля й Ганна сильні, Петро й Іван слабкі: «через одного» поставило б сильних разом (2 : −2).
    static readonly Dictionary<string, double> Power = new() { ["Оля"] = 1, ["Петро"] = -1, ["Ганна"] = 1, ["Іван"] = -1 };
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван"];

    static RoomHarness Table(string game, string teams, int seed = 42)
    {
        var h = new RoomHarness(game, new { teams }, seed, RoomHarness.WithService<ISkills>(new FixedSkills(Power)));
        foreach (var n in Nicks) h.Join(n);
        h.Start();
        return h;
    }

    static int[] Teams(RoomHarness h) => h.Room.Game switch
    {
        CurveGame c => c.Teams!,
        _ => [.. h.View(null).GetProperty("teams").EnumerateArray().Select(x => x.GetInt32())],
    };

    /// <summary>Напарник Олі за ніком (місця на «Ще раз» зсуваються).</summary>
    static string MateOfOlya(RoomHarness h)
    {
        var t = Teams(h);
        var seats = Enumerable.Range(0, 4).ToDictionary(h.NickOf);
        return Nicks.Single(n => n != "Оля" && t[seats[n]] == t[seats["Оля"]]);
    }

    /// <summary>Уся команда суперників Олі встає — партія скінчилась; вони сідають назад, і «Ще раз».</summary>
    static void Again(RoomHarness h)
    {
        var mate = MateOfOlya(h);
        var foes = Nicks.Where(n => n != "Оля" && n != mate).ToArray();
        foreach (var f in foes) h.Leave(f);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        foreach (var f in foes) Assert.True(h.Join(f).Ok, h.Reply.Message);
        Assert.True((h.Room.Status == RoomStatus.Finished ? h.Rematch("Оля", true) : h.Start()).Ok, h.Room.Status + " " + h.Reply.Message);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Theory]
    [InlineData("curve")]
    [InlineData("bomber")]
    public void By_strength_pairs_strong_with_weak_and_changes_on_rematch(string game)
    {
        var h = Table(game, TeamSplit.ByStrength);
        var mates = new List<string> { MateOfOlya(h) };
        Assert.Contains(h.Outbox.OfType<Journal>(), l => l.Text.Contains(TeamSplit.Note));
        for (var i = 0; i < 3; i++)
        {
            Again(h);
            mates.Add(MateOfOlya(h));
        }
        // сильну Олю щоразу ставить зі слабким, і слабкі чергуються (обидва поділи рівні — «не той, що минулого разу»)
        Assert.All(mates, m => Assert.True(m is "Петро" or "Іван", string.Join(",", mates)));
        for (var i = 1; i < mates.Count; i++) Assert.NotEqual(mates[i - 1], mates[i]);
    }

    [Fact]
    public void Bomber_real_rematch_after_a_played_party_changes_mates()
    {
        var h = Table("bomber", TeamSplit.ByStrength);
        var mates = new List<string> { MateOfOlya(h) };
        for (var party = 0; party < 3; party++)
        {
            var mate = MateOfOlya(h);
            var foes = Enumerable.Range(0, 4).Where(s => h.NickOf(s) != "Оля" && h.NickOf(s) != mate).ToArray();
            for (var round = 0; round < 3 && h.Room.Status == RoomStatus.Playing; round++)
            {
                for (var i = 0; i < 200 && h.View(null).GetProperty("phase").GetString() != "go"; i++) h.Tick(1);
                foreach (var s in foes) h.Input(s, "bomb");   // команда суперників підриває себе сама
                h.Tick(BomberCore.FuseTicks + 1);
                for (var i = 0; i < 40 && h.View(null).GetProperty("phase").GetString() == "pause"; i++) h.Tick(1);
            }
            Assert.Equal(RoomStatus.Finished, h.Room.Status);
            Assert.True(h.Rematch("Оля", true).Ok, h.Reply.Message);   // справжнє «Ще раз»: місця зсуваються
            mates.Add(MateOfOlya(h));
        }
        Assert.All(mates, m => Assert.True(m is "Петро" or "Іван", string.Join(",", mates)));
        for (var i = 1; i < mates.Count; i++) Assert.NotEqual(mates[i - 1], mates[i]);
    }

    [Theory]
    [InlineData("curve")]
    [InlineData("bomber")]
    public void As_seated_keeps_the_old_alternation(string game)
    {
        var h = Table(game, TeamSplit.BySeat);
        Assert.Equal([0, 1, 0, 1], Teams(h).Take(4));
        Assert.Equal("Ганна", MateOfOlya(h));
        Assert.DoesNotContain(h.Outbox.OfType<Journal>(), l => l.Text.Contains(TeamSplit.Note));
    }
}
