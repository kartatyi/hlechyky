using System.Text.RegularExpressions;
using Hlechyky.Games.Impl;
using static Hlechyky.Games.Impl.SolyanyiMap;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Граф «Соляного шляху» (контракт §1, §4.2, §14): числа таблиць, зв'язність без мостів, замовлення за Дейкстрою, відбиток
/// мапи — і дослівна звірка з <c>web/games/solyanyi/consts.js</c>, бо клієнт тримає ту саму мапу окремо.
/// </summary>
public class SolyanyiMapTests
{
    static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "web", "games", "solyanyi", "consts.js"))) return dir.FullName;
        throw new InvalidOperationException("не знайшов web/games/solyanyi/consts.js");
    }

    static string Consts() => File.ReadAllText(Path.Combine(FindRoot(), "web", "games", "solyanyi", "consts.js"));

    [Fact]
    public void Map_signature_is_the_contract_one()
    {
        Assert.Equal("01c5d1de", MapSig());
        Assert.Equal(Sig, MapSig());
        Assert.StartsWith("r0:0:1:salt:3:;r1:0:2:grey:2:;", SigText());
    }

    [Fact]
    public void Server_map_is_identical_to_the_client_constants()
    {
        var js = Consts();
        Assert.Contains($"export const MAP_SIG = '{MapSig()}'", js);

        var cities = Regex.Matches(js, @"^\s*\[(\d+), '(\w+)', (?:'([^']+)'|""([^""]+)""), \d+, \d+, \d+, \d\],", RegexOptions.Multiline);
        Assert.Equal(Cities.Length, cities.Count);
        foreach (Match m in cities)
        {
            var c = Cities[int.Parse(m.Groups[1].Value)];
            Assert.Equal(c.Slug, m.Groups[2].Value);
            Assert.Equal(c.Name, m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value);
        }

        var routes = Regex.Matches(js, @"^\s*\[(\d+), (\d+), (\d+), '(\w+)', (\d), (\d+|null), -?\d+\],", RegexOptions.Multiline);
        Assert.Equal(Routes.Length, routes.Count);
        foreach (Match m in routes)
        {
            var r = Routes[int.Parse(m.Groups[1].Value)];
            Assert.Equal(r.A, int.Parse(m.Groups[2].Value));
            Assert.Equal(r.B, int.Parse(m.Groups[3].Value));
            Assert.Equal(r.Cargo == Grey ? "grey" : Cargo[r.Cargo], m.Groups[4].Value);
            Assert.Equal(r.Len, int.Parse(m.Groups[5].Value));
            Assert.Equal(r.Twin < 0 ? "null" : r.Twin.ToString(), m.Groups[6].Value);
        }

        var tickets = Regex.Matches(js, @"^\s*\[(\d+), (\d+), (\d+), (\d+), (\d+)\],\s*//", RegexOptions.Multiline);
        Assert.Equal(Tickets.Length, tickets.Count);
        foreach (Match m in tickets)
        {
            var t = Tickets[int.Parse(m.Groups[1].Value)];
            Assert.Equal((t.A, t.B, t.D, t.Pts), (int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value)));
        }
    }

    [Fact]
    public void Cards_and_constants_match_the_client()
    {
        var js = Consts();
        Assert.Contains("export const CARGO = ['salt', 'fish', 'grain', 'tar', 'honey', 'wool', 'hides', 'linen'];", js);
        Assert.Contains($"DECK_EACH = {DeckEach}, DECK_WILD = {DeckWild}, DECK_SIZE = {DeckSize}, HAND_START = {HandStart}, MARKET_SIZE = {MarketSize}, DRAW_PICKS = {DrawPicks}", js);
        Assert.Contains($"WIPE_AT = {WipeAt}, WIPE_MAX = {WipeMax}", js);
        Assert.Contains($"OFFER = {Offer}, KEEP_START = {KeepStart}, KEEP_LATER = {KeepLater}", js);
        Assert.Contains($"LAST_AT = {LastAt}", js);
        Assert.Contains($"LONGEST_BONUS = {LongestBonus}", js);
        Assert.Contains("ROUTE_POINTS = { 1: 1, 2: 2, 3: 4, 4: 7, 5: 10, 6: 15 }", js);
        Assert.Equal(DeckSize, DeckEach * 8 + DeckWild);
    }

    [Fact]
    public void Graph_has_the_contract_counts()
    {
        Assert.Equal(45, Cities.Length);
        Assert.Equal(102, Routes.Length);
        for (var i = 0; i < Routes.Length; i++)
        {
            Assert.Equal(i, Routes[i].Id);
            Assert.NotEqual(Routes[i].A, Routes[i].B);
        }
        for (var i = 0; i < Cities.Length; i++) Assert.Equal(i, Cities[i].I);
        Assert.Equal(270, Routes.Sum(r => r.Len));
        Assert.Equal(375, Routes.Sum(r => r.Pts));
        Assert.Equal([11, 38, 36, 12, 3, 2], Enumerable.Range(1, 6).Select(l => Routes.Count(r => r.Len == l)));
        Assert.Equal(23, Routes.Count(r => r.Cargo == Grey));
        // За вантажем (возів / шляхів), §1.3.
        var byCargo = new (int C, int Wagons, int N)[]
        {
            (Salt, 29, 9), (Fish, 27, 12), (Grain, 29, 8), (Tar, 27, 9), (Honey, 26, 9), (Wool, 25, 10), (Hides, 27, 11), (Linen, 28, 11), (Grey, 52, 23),
        };
        foreach (var (c, wagons, n) in byCargo)
        {
            Assert.Equal(wagons, Routes.Where(r => r.Cargo == c).Sum(r => r.Len));
            Assert.Equal(n, Routes.Count(r => r.Cargo == c));
        }
        // Вибірково рядки §1.3 повністю.
        Assert.Equal(new SlRoute(0, 0, 1, Salt, 3, -1), Routes[0]);
        Assert.Equal(new SlRoute(6, 4, 6, Fish, 2, -1), Routes[6]);
        Assert.Equal(new SlRoute(11, 17, 22, Grey, 3, -1), Routes[11]);
        Assert.Equal(new SlRoute(37, 0, 5, Grain, 6, -1), Routes[37]);
        Assert.Equal(new SlRoute(63, 33, 43, Salt, 5, -1), Routes[63]);
        Assert.Equal(new SlRoute(71, 26, 23, Tar, 6, -1), Routes[71]);
        Assert.Equal(new SlRoute(72, 31, 23, Grain, 5, -1), Routes[72]);
        Assert.Equal(new SlRoute(88, 14, 29, Grey, 5, -1), Routes[88]);
        Assert.Equal(new SlRoute(89, 0, 32, Linen, 3, 59), Routes[89]);
        Assert.Equal(new SlRoute(97, 28, 29, Salt, 3, 56), Routes[97]);
    }

    [Fact]
    public void Twins_are_symmetric_with_the_same_ends_and_length_and_different_cargo()
    {
        var pairs = Routes.Where(r => r.Twin >= 0).ToList();
        Assert.Equal(26, pairs.Count);
        foreach (var r in pairs)
        {
            var t = Routes[r.Twin];
            Assert.Equal(r.Id, t.Twin);
            Assert.Equal((r.A, r.B, r.Len), (t.A, t.B, t.Len));
            Assert.NotEqual(r.Cargo, t.Cargo);
            Assert.True(r.Len >= 2);
        }
        // Інших паралельних шляхів нема: 89 коридорів.
        Assert.Equal(89, Routes.Select(r => (Math.Min(r.A, r.B), Math.Max(r.A, r.B))).Distinct().Count());
    }

    static bool Connected(int skipA = -1, int skipB = -1)
    {
        var seen = new bool[Cities.Length];
        var stack = new Stack<int>();
        stack.Push(0);
        seen[0] = true;
        while (stack.TryPop(out var u))
            foreach (var r in Adj[u])
            {
                if ((r.A == skipA && r.B == skipB) || (r.A == skipB && r.B == skipA)) continue;
                var v = Other(r, u);
                if (seen[v]) continue;
                seen[v] = true;
                stack.Push(v);
            }
        return seen.All(x => x);
    }

    [Fact]
    public void Graph_is_connected_without_bridges_and_every_city_has_two_neighbours()
    {
        Assert.True(Connected());
        foreach (var r in Routes) Assert.True(Connected(r.A, r.B), $"міст: {RouteName(r.Id)}");
        foreach (var c in Cities) Assert.True(Adj[c.I].Select(r => Other(r, c.I)).Distinct().Count() >= 2, c.Name);
    }

    [Fact]
    public void Road_counts_per_city_match_the_table()
    {
        // Колонка «Шляхів» §1.2: пара рахується за двох.
        var want = new[] { 9, 3, 3, 4, 3, 6, 4, 7, 3, 3, 6, 5, 2, 2, 6, 5, 8, 7, 4, 6, 5, 5, 6, 6, 5, 4, 2, 5, 6, 4, 2, 7, 5, 4, 5, 5, 3, 3, 2, 4, 6, 6, 3, 3, 2 };
        Assert.Equal(want, Cities.Select(c => Adj[c.I].Length));
    }

    [Fact]
    public void Tickets_have_recomputed_lengths_and_points_and_no_pair_repeats()
    {
        Assert.Equal(40, Tickets.Length);
        for (var i = 0; i < Tickets.Length; i++)
        {
            var t = Tickets[i];
            Assert.Equal(i, t.Id);
            var d = Dijkstra(t.A, t.B, LenCost);
            Assert.Equal(d, t.D);
            Assert.Equal(d + Math.Max(0, d / 5 - 1), t.Pts);
            Assert.Equal(TicketPoints(d), t.Pts);
        }
        Assert.Equal(40, Tickets.Select(t => (Math.Min(t.A, t.B), Math.Max(t.A, t.B))).Distinct().Count());
        foreach (var c in Cities) Assert.Contains(Tickets, t => t.A == c.I || t.B == c.I);
        Assert.Equal(4, Tickets.Min(t => t.Pts));
        Assert.Equal(26, Tickets.Max(t => t.Pts));
        Assert.Equal(11, Tickets.Count(t => t.Pts >= 15));
        Assert.Equal(12, Tickets.Count(IsCrimea));
        Assert.Equal(5, Tickets.Count(t => t.Pts >= 15 && IsCrimea(t)));
    }

    [Fact]
    public void Dijkstra_breaks_ties_by_fewer_routes_then_lower_id()
    {
        var path = new List<int>();
        // Київ — Біла Церква: 38 і 90 однакові, менший id.
        Assert.Equal(2, Dijkstra(0, 19, LenCost, path));
        Assert.Equal([38], path);
        var cost = LenCost.ToArray();
        cost[38] = -1;
        Assert.Equal(2, Dijkstra(0, 19, cost, path));
        Assert.Equal([90], path);
        // Чернігів — Конотоп: напряму 3, через Ніжин 1 + 2 = 3 — менше шляхів.
        Assert.Equal(3, Dijkstra(1, 3, LenCost, path));
        Assert.Equal([4], path);
        // Сам до себе — 0 і порожній шлях; відрізане місто — -1.
        Assert.Equal(0, Dijkstra(5, 5, LenCost, path));
        Assert.Empty(path);
        foreach (var r in Adj[44]) cost[r.Id] = -1;
        Assert.Equal(-1, Dijkstra(0, 44, cost, path));
    }
}
