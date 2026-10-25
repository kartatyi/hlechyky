using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games.Impl;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Мапа Житомира з OpenStreetMap (<c>data/fura/maps/zhytomyr.json</c>, зібрана <c>tools/osm</c>; specs/fura.md,
/// «Мапи з OSM (A4)») у теперішньому форматі kray.json: сервер її читає, граф зв'язний, між усіма місцями є шлях
/// адекватної довжини, двори й бокси на місці. Розбираємо з файлу (не з кешу <see cref="FuraMap.Get"/>), щоб міряти час розбору.
/// </summary>
public sealed class FuraOsmMapTests
{
    const string File_ = "data/fura/maps/zhytomyr.json";

    static readonly Lazy<(FuraMap Map, double Ms)> Loaded = new(() =>
    {
        var json = File.ReadAllText(Paths.Resolve(File_));
        var sw = Stopwatch.StartNew();
        var map = FuraMap.Parse("zhytomyr", json);
        return (map, sw.Elapsed.TotalMilliseconds);
    });

    static FuraMap Map => Loaded.Value.Map;

    static double Straight(FuraPlace a, FuraPlace b)
    {
        var (ax, az) = Map.Nodes[a.Node];
        var (bx, bz) = Map.Nodes[b.Node];
        return Math.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));
    }

    [Fact]
    public void Reads_As_A_Kray_Map()
    {
        Assert.Equal("Житомир", Map.Title);
        Assert.InRange(Map.Places.Count, 6, 20);
        Assert.InRange(Map.Nodes.Count, 100, 3000);
        Assert.Single(Map.Places, p => p.Depot);
        Assert.Equal("dvir", Map.Places.Single(p => p.Depot).Id);   // kray.js шукає вузол dvir
        var total = Map.Roads.Sum(r => r.Length) / 1000;
        Assert.InRange(total, 150, 500);                               // головні вулиці й траси міста з околицями
        Assert.All(Map.Roads, r => Assert.Contains(r.Kind, new[] { "highway", "village", "dirt" }));
        Assert.All(Map.Roads, r => Assert.True(r.Length > 0.5, $"дорога {r.Id} нульової довжини"));
    }

    [Fact]
    public void Carries_Osm_Attribution()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Paths.Resolve(File_)));
        var attr = doc.RootElement.GetProperty("attribution");
        Assert.Contains("OpenStreetMap contributors", attr.GetProperty("osm").GetString());
        Assert.Contains("COPERNICUS", attr.GetProperty("dem").GetString());
    }

    [Fact]
    public void Graph_Is_Connected()
    {
        var adj = Map.Nodes.Keys.ToDictionary(k => k, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var r in Map.Roads)
        {
            adj[r.A].Add(r.B);
            adj[r.B].Add(r.A);
        }
        var seen = new HashSet<string>(StringComparer.Ordinal) { "dvir" };
        var stack = new Stack<string>(["dvir"]);
        while (stack.TryPop(out var v))
            foreach (var u in adj[v])
                if (seen.Add(u)) stack.Push(u);
        Assert.Equal(Map.Nodes.Count, seen.Count);
    }

    [Fact]
    public void Every_Place_Reaches_Every_Other_With_Sane_Length()
    {
        foreach (var a in Map.Places)
        foreach (var b in Map.Places)
        {
            var m = Map.Meters(a.Id, b.Id);
            if (a == b)
            {
                Assert.Equal(0, m);
                continue;
            }
            Assert.True(double.IsFinite(m), $"{a.Id} → {b.Id}: шляху нема");
            var line = Straight(a, b);
            // не коротше за пряму і не довше за 5 прямих + 1 км (залізниця й річка дають об'їзди, але не по пів міста)
            Assert.InRange(m, line - 1, 5 * line + 1000);
            Assert.InRange(Map.Km(a.Id, b.Id), 0.3, 25);
            Assert.Equal(Map.Meters(b.Id, a.Id), m, 6);                 // дороги двобічні
        }
        // знайомі відстані містом: вокзал — Житній ринок ≈ 2,7 км дорогою, «Рудь» — картонний ≈ 2 км
        Assert.InRange(Map.Km("vokzal", "rynok"), 2.0, 4.0);
        Assert.InRange(Map.Km("rud", "karton"), 1.5, 3.5);
    }

    [Fact]
    public void Yards_And_Docks_Are_Usable()
    {
        foreach (var p in Map.Places)
        {
            Assert.InRange(p.Docks.Length, 2, 6);
            Assert.Equal(p.Docks.Length, p.Docks.Select(d => d.Id).Distinct().Count());
            foreach (var d in p.Docks)
            {
                Assert.True(p.Yard.Contains(d.X, d.Z), $"{p.Id}/{d.Id}: бокс поза двором");
                Assert.InRange(d.Yaw, -3.1416, 3.1416);                   // π у файлі — з 4 знаками, як у kray.json
                // бокс (причіп ≈ 16 м) влазить у двір уздовж свого курсу
                var (fx, fz) = (Math.Sin(d.Yaw) * 8, Math.Cos(d.Yaw) * 8);
                Assert.True(p.Yard.Contains(d.X + fx, d.Z + fz, 1) && p.Yard.Contains(d.X - fx, d.Z - fz, 1),
                    $"{p.Id}/{d.Id}: причіп не влазить у двір");
            }
            var (nx, nz) = Map.Nodes[p.Node];
            var gate = Math.Sqrt((nx - p.Yard.X) * (nx - p.Yard.X) + (nz - p.Yard.Z) * (nz - p.Yard.Z));
            Assert.InRange(gate, 20, 160);                                // двір біля вузла під'їзду, але не на дорозі
        }
    }

    [Fact]
    public void Every_Cargo_Has_A_Trip()
    {
        Assert.NotEmpty(Map.Cargo);
        foreach (var c in Map.Cargo)
        {
            Assert.NotEmpty(c.From);
            Assert.True(c.From.All(f => c.To.Any(t => t != f)), $"{c.Id}: нема куди везти");
        }
        // кожне місце щось відправляє або приймає
        var used = Map.Cargo.SelectMany(c => c.From.Concat(c.To)).ToHashSet(StringComparer.Ordinal);
        Assert.All(Map.Places, p => Assert.Contains(p.Id, used));
    }

    [Fact]
    public void Parses_Fast_Enough()
    {
        // Дейкстра у FuraMap — O(вузли × дороги) на місце: на цій мапі ще терпимо, на всьому місті — ні
        _ = Map;
        Assert.True(Loaded.Value.Ms < 3000, $"розбір {Loaded.Value.Ms:0} мс");
    }
}
