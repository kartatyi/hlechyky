using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>«⚖ Фора» в Кривулі (пакет 3, 20.10): множник на очки, які місце заробляє; ціль партії та сама.</summary>
public class CurveHandicapTests
{
    sealed class FixedSkills(Dictionary<string, double> map) : ISkills
    {
        public double Strength(string gameId, string? nick) => map.TryGetValue(nick ?? "", out var s) ? s : 0;
    }

    static readonly Dictionary<string, double> Power = new()
    {
        ["Микола"] = 0.40, ["владік"] = -0.24, ["Smaug"] = -0.09, ["Іван"] = -0.17,
    };

    static RoomHarness Table(int players, object? options = null)
    {
        var h = new RoomHarness("curve", options, 42, RoomHarness.WithService<ISkills>(new FixedSkills(Power)));
        foreach (var n in new[] { "Микола", "владік", "Smaug", "Іван" }.Take(players)) h.Join(n);
        h.Start();
        return h;
    }

    static CurveGame Game(RoomHarness h) => (CurveGame)h.Room.Game;

    static void Doom(RoomHarness h, int seat)
    {
        var head = Game(h).Field.Heads[seat];
        head.X = CurveCore.R + 1;
        head.Y = Game(h).Field.H / 2.0;
        head.A = Math.PI;
        head.Turn = 0;
        h.Tick(1);
    }

    static int[] Scores(RoomHarness h) => [.. h.View(0).GetProperty("scores").EnumerateArray().Select(x => x.GetInt32()).Take(4)];

    /// <summary>Раунди, де першим вибуває Smaug, потім владік: Миколі по 2 «сирих» очки, владіку по 1.</summary>
    static void Rounds(RoomHarness h, int n)
    {
        for (var r = 0; r < n; r++)
        {
            h.Tick(CurveCore.ReadyTicks);
            Doom(h, 2);
            Doom(h, 1);
            Assert.Equal("between", h.View(0).GetProperty("phase").GetString());
            h.Tick(CurveCore.BetweenTicks);
        }
    }

    [Fact]
    public void Points_are_multiplied_with_the_fraction_kept_and_the_target_stays()
    {
        var h = Table(3);
        var f = Game(h).HandicapFactors;
        Assert.InRange(f[0], 0.69, 0.71);   // Микола
        Assert.InRange(f[1], 1.2, 1.22);    // владік
        Rounds(h, 5);
        var s = Scores(h);
        Assert.Equal((int)Math.Floor(10 * f[0] + 1e-9), s[0]);   // 10 сирих → 6
        Assert.Equal((int)Math.Floor(5 * f[1] + 1e-9), s[1]);    // 5 сирих → 6
        Assert.Equal(0, s[2]);
        Assert.Equal(20, h.View(0).GetProperty("target").GetInt32());
        var hc = h.View(0).GetProperty("handicap").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        Assert.Equal(Math.Round(f[0], 2), hc[0]);
        Assert.Equal(1, hc[5]);
        Assert.Contains(h.Outbox.OfType<Journal>(), j => j.Text.StartsWith("⚖ Фора: Микола ×0,7"));
    }

    [Fact]
    public void Without_handicap_points_are_as_before()
    {
        var h = Table(3, new { handicap = Handicap.Off });
        Assert.Empty(Game(h).HandicapFactors);
        Rounds(h, 5);
        Assert.Equal([10, 5, 0, 0], Scores(h));
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("handicap").ValueKind);
        Assert.DoesNotContain(h.Outbox.OfType<Journal>(), j => j.Text.StartsWith("⚖"));
    }

    [Fact]
    public void Team_point_is_multiplied_by_the_mean_of_those_still_riding()
    {
        var h = Table(4, new { teams = TeamSplit.BySeat });   // як сіли: Микола й Smaug проти владіка й Івана
        var teams = Game(h).Teams!;
        Assert.Equal(teams[0], teams[2]);
        var f = Game(h).HandicapFactors;
        h.Tick(CurveCore.ReadyTicks);
        Doom(h, 1);
        Doom(h, 3);
        var mean = (f[0] + f[2]) / 2;                         // ≈ 0,86: два очки → одне
        Assert.Equal((int)Math.Floor(2 * mean + 1e-9), Scores(h)[0]);
        Assert.Equal(Scores(h)[0], Scores(h)[2]);
        Assert.Equal(1, Scores(h)[0]);
    }

    [Fact]
    public void Option_is_on_the_table_by_default()
    {
        var info = RoomHarness.NewRegistry().Info("curve")!;
        Assert.Contains(info.Options!, o => o.Key == Handicap.Key && o.Default == Handicap.Auto);
    }
}
