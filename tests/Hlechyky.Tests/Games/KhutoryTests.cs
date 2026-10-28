using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Хутори» (Катан по-нашому). Геометрію звіряємо з еталоном <c>web/games/khutory/dev/fixture.js</c>; правила — на
/// <c>KhutoryCore</c> напряму (з випадкових кидків не збудуєш потрібного столу); кімнату, види, приховане,
/// таймер і вихід — через <see cref="RoomHarness"/>; «самогра» чотирма <c>KhutoryAuto</c> — стрес-тест правил.
/// </summary>
public class KhutoryTests
{
    static readonly KhutoryGeo G = KhutoryGeo.I;

    // =========================================================================================
    // Геометрія й карта
    // =========================================================================================

    [Fact]
    public void Geometry_matches_the_fixture()
    {
        Assert.Equal(54, G.Vx.Length);
        Assert.Equal(72, G.Edges.Length);
        Assert.Equal(30, G.Coast.Length);
        Assert.Equal([-1.7321, -4], G.Vx[0]);
        Assert.Equal([1.7321, 4], G.Vx[53]);
        Assert.Equal([1.7321, -1], G.Vx[20]);
        Assert.Equal([0, 1], G.Edges[0]);
        Assert.Equal([31, 32], G.Edges[40]);
        Assert.Equal([50, 53], G.Edges[71]);
        Assert.Equal([0, 1, 2, 3, 4, 5], G.HexV[0]);
        Assert.Equal([18, 21, 31, 32, 29, 19], G.HexV[9]);
        Assert.Equal([43, 46, 52, 53, 50, 44], G.HexV[18]);
        Assert.Equal([6, 15, 11, 12, 27, 28, 45, 46, 47, 59, 60, 69, 70, 71, 67, 68, 63, 64, 65, 51, 52, 33, 34, 35, 19, 20, 4, 5, 0, 10], G.Coast);
        Assert.Equal([6, 12, 46, 60, 71, 64, 52, 35, 5], KhutoryBoard.Starter().PortEdge);
        Assert.All(G.Edges, e => Assert.True(e[0] < e[1]));
        // Кожна вершина — на 2 або 3 ребрах, 1–3 гексах; сусідство симетричне.
        for (var v = 0; v < 54; v++)
        {
            Assert.InRange(G.VE[v].Length, 2, 3);
            Assert.InRange(G.VH[v].Length, 1, 3);
            foreach (var u in G.VNb[v]) Assert.Contains(v, G.VNb[u]);
        }
        Assert.Equal(6, G.HNb[9].Length);   // центр
        Assert.Equal(3, G.HNb[0].Length);   // кут
    }

    [Fact]
    public void Starter_map_is_as_in_the_contract()
    {
        var b = KhutoryBoard.Starter();
        Assert.Equal("forest", KhutoryBoard.Terrains[b.T[0]]);
        Assert.Equal(10, b.Num[0]);
        Assert.Equal(9, b.SteppeHex);
        Assert.Equal(0, b.Num[9]);
        Assert.Equal(11, b.Num[18]);
        Assert.Equal("pasture", KhutoryBoard.Terrains[b.T[18]]);
        Assert.Equal([-1, 2, -1, 4, 3, -1, 1, 0, -1], b.PortKind);
    }

    [Fact]
    public void Random_map_is_fair_and_repeats_by_seed()
    {
        for (var seed = 0; seed < 60; seed++)
        {
            var a = KhutoryBoard.Random(new Random(seed));
            var b = KhutoryBoard.Random(new Random(seed));
            Assert.Equal(a.T, b.T);
            Assert.Equal(a.Num, b.Num);
            Assert.Equal(a.PortKind, b.PortKind);
            Assert.Equal(a.Id, b.Id);
            Assert.True(a.RedsApart());
            Assert.True(a.SameApart());
            Assert.Equal(1, a.T.Count(t => t == KhutoryBoard.Steppe));
            Assert.Equal(0, a.Num[a.SteppeHex]);
            Assert.Equal(4, a.PortKind.Count(k => k == KhutoryBoard.AnyPort));
            Assert.Equal([2, 3, 3, 4, 4, 5, 5, 6, 6, 8, 8, 9, 9, 10, 10, 11, 11, 12], a.Num.Where(n => n > 0).Order());
        }
        Assert.NotEqual(KhutoryBoard.Random(new Random(1)).Num, KhutoryBoard.Random(new Random(2)).Num);
    }
}
