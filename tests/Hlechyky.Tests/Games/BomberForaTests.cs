using System.Text.Json;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>«⚖ Фора» в Бомбері (пакет 3, 20.10): без жодного раунду, коли хтось уже має, — +1 вогню або +1 бомба на старті.</summary>
public class BomberForaTests
{
    static RoomHarness Table(object? options = null, int seed = 42)
    {
        var h = new RoomHarness("bomber", options: options, seed: seed);
        h.Join("Оля");
        h.Join("Петро");
        h.Start();
        return h;
    }

    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString() ?? "";

    /// <summary>Оля підриває сама себе — раунд Петрів, далі пауза й старт наступного.</summary>
    static void OlyaGivesRoundAway(RoomHarness h)
    {
        for (var i = 0; i < 200 && Phase(h) != "go"; i++) h.Tick(1);
        Assert.Equal("go", Phase(h));
        h.Input(0, "bomb");
        h.Tick(BomberCore.FuseTicks);
        var round = h.View(null).GetProperty("round").GetInt32();
        for (var i = 0; i < 100 && h.View(null).GetProperty("round").GetInt32() == round; i++) h.Tick(1);
    }

    static (int Bombs, int Range) Man(JsonElement v, int seat)
    {
        var p = v.GetProperty("p")[seat];
        return (p.GetProperty("bombs").GetInt32(), p.GetProperty("range").GetInt32());
    }

    static int Extra(JsonElement v, int seat)
    {
        var (b, r) = Man(v, seat);
        return b - BomberCore.StartBombs + r - BomberCore.StartRange;
    }

    [Fact]
    public void The_one_without_a_round_starts_the_next_with_plus_one_and_the_leader_without()
    {
        foreach (var seed in new[] { 1, 2, 3, 42 })
        {
            var h = Table(seed: seed);
            Assert.Equal(0, Extra(h.View(null), 0));        // перший раунд — ні в кого нема перемог, фори нема
            OlyaGivesRoundAway(h);
            var v = h.View(null);
            Assert.Equal(2, v.GetProperty("round").GetInt32());
            Assert.Equal(1, Extra(v, 0));                   // рівно +1: або вогонь, або бомба
            Assert.Equal(0, Extra(v, 1));                   // Петро раунд уже має
            var ev = v.GetProperty("ev").EnumerateArray().Select(e => e.EnumerateArray().Select(x => x.GetInt32()).ToArray()).ToList();
            Assert.Contains(ev, e => e[1] == (int)BomberHow.Handicap && e[3] == 0);

            // ще раунд без перемог — знову +1, а не +2
            OlyaGivesRoundAway(h);
            Assert.Equal(1, Extra(h.View(null), 0));
        }
    }

    [Fact]
    public void Both_fire_and_bomb_come_up_across_seeds()
    {
        var kinds = new HashSet<string>();
        for (var seed = 1; seed <= 12; seed++)
        {
            var h = Table(seed: seed);
            OlyaGivesRoundAway(h);
            var (b, r) = Man(h.View(null), 0);
            kinds.Add(b > BomberCore.StartBombs ? "bomb" : r > BomberCore.StartRange ? "fire" : "none");
        }
        Assert.Equal(["bomb", "fire"], kinds.Order());
    }

    [Fact]
    public void Without_handicap_nobody_gets_a_bonus()
    {
        var h = Table(new { handicap = Handicap.Off });
        OlyaGivesRoundAway(h);
        var v = h.View(null);
        Assert.Equal(2, v.GetProperty("round").GetInt32());
        Assert.Equal(0, Extra(v, 0));
        Assert.Equal(0, Extra(v, 1));
    }
}
