using System.Text.Json.Nodes;
using Hlechyky.Games.Impl;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Дорога 2.0 (B2, specs/fura.md, «Дорога 2.0»): у <c>kinds</c> мапи краю — необов'язкові поля для браузера
/// (<c>lanes</c>, <c>shoulder</c>, <c>barrier</c>). Сервер їх не читає: мапа з ними й без них розбирається однаково —
/// ті самі дороги, місця й кілометри між усіма місцями.
/// </summary>
public sealed class FuraRoadMapTests
{
    static readonly string Json = File.ReadAllText(Paths.Resolve("data/fura/maps/kray.json"));

    [Fact]
    public void Kray_kinds_carry_the_optional_road_fields()
    {
        var kinds = JsonNode.Parse(Json)!["kinds"]!.AsObject();
        foreach (var (name, k) in kinds)
        {
            Assert.True(k!["lanes"]!.GetValue<int>() >= 1, name);
            Assert.InRange(k["shoulder"]!.GetValue<double>(), 0, 5);
            Assert.Contains(k["barrier"]!.GetValue<string>(), new[] { "auto", "none", "both" });
        }
        Assert.Equal("auto", kinds["highway"]!["barrier"]!.GetValue<string>());
    }

    [Fact]
    public void Server_reads_the_map_the_same_without_the_optional_fields()
    {
        var root = JsonNode.Parse(Json)!.AsObject();
        foreach (var (_, k) in root["kinds"]!.AsObject())
            foreach (var f in new[] { "lanes", "shoulder", "barrier" }) k!.AsObject().Remove(f);
        var bare = FuraMap.Parse("kray", root.ToJsonString());
        var full = FuraMap.Parse("kray", Json);

        Assert.Equal(bare.Roads, full.Roads);
        Assert.Equal(bare.Nodes.OrderBy(n => n.Key), full.Nodes.OrderBy(n => n.Key));
        Assert.Equal(bare.Places.Select(p => p.Id), full.Places.Select(p => p.Id));
        foreach (var a in full.Places)
            foreach (var b in full.Places)
                Assert.Equal(bare.Km(a.Id, b.Id), full.Km(a.Id, b.Id));
        Assert.Equal(9, full.Roads.Count);
    }
}
