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

    // =========================================================================================
    // Дрібний інструмент для правил
    // =========================================================================================

    /// <summary>Ядро на стартовій карті; розстановку почав місце 0.</summary>
    static KhutoryCore Core(int n = 4, int seed = 1, bool friendly = false)
    {
        var c = new KhutoryCore(KhutoryBoard.Starter(), Enumerable.Range(0, n), new Random(seed)) { Friendly = friendly };
        c.Begin(0);
        return c;
    }

    /// <summary>Перескочити розстановку: хід <paramref name="turn"/>, кубики вже кинуто.</summary>
    static void MainOf(KhutoryCore c, int turn = 0)
    {
        c.Phase = KhPhase.Main;
        c.Turn = turn;
        c.TurnIdx = Array.IndexOf(c.Order, turn);
        c.Rolled = true;
        c.Dice = [2, 3];
    }

    /// <summary>Видати карти з банку (дерево, глина, вовна, зерно, залізо).</summary>
    static void Give(KhutoryCore c, int s, int wood = 0, int clay = 0, int wool = 0, int grain = 0, int ore = 0)
    {
        int[] a = [wood, clay, wool, grain, ore];
        for (var r = 0; r < 5; r++) { c.Bank[r] -= a[r]; c.Hand[s][r] += a[r]; }
    }

    static void Town(KhutoryCore c, int s, int v, bool city = false)
    {
        c.VOwner[v] = s;
        c.VCity[v] = city;
        c.RecountRoads();
    }

    static int E(int a, int b) => Array.FindIndex(G.Edges, e => e[0] == Math.Min(a, b) && e[1] == Math.Max(a, b));

    /// <summary>Шляхи гравця вздовж краю гекса: n ребер від кута 0 (6 — кільце).</summary>
    static void Chain(KhutoryCore c, int s, int hex, int n)
    {
        var hv = G.HexV[hex];
        for (var i = 0; i < n; i++) c.EOwner[E(hv[i], hv[(i + 1) % 6])] = s;
        c.RecountRoads();
    }

    static int Cards(KhutoryCore c, int s) => c.Hand[s].Sum();

    // =========================================================================================
    // Розстановка
    // =========================================================================================

    [Fact]
    public void Setup_goes_by_snake_and_the_second_town_pays()
    {
        var c = new KhutoryCore(KhutoryBoard.Starter(), [0, 1, 3], new Random(3));
        c.Begin(1);
        Assert.Equal([1, 3, 0], c.Order);
        var turns = new List<int>();
        // Хати на вершинах, що далеко одна від одної: верх гексів 0, 2, 12, 16, 18 і 7.
        int[] spots = [G.HexV[0][0], G.HexV[2][0], G.HexV[16][3], G.HexV[18][3], G.HexV[12][3], G.HexV[7][0]];
        for (var i = 0; i < 6; i++)
        {
            turns.Add(c.Turn);
            Assert.Equal("town", c.Must(c.Turn));
            Assert.NotNull(c.PlaceRoad(c.Turn, G.VE[spots[i]][0]));   // спершу хата
            Assert.Null(c.PlaceTown(c.Turn, spots[i]));
            Assert.Equal("road", c.Must(c.Turn));
            var far = G.VE[spots[(i + 1) % 6]][0];
            Assert.NotNull(c.PlaceRoad(c.Turn, far));                  // лише від щойно поставленої хати
            Assert.Null(c.PlaceRoad(c.Turn, c.RoadSpots(c.Turn)[0]));
        }
        Assert.Equal([1, 3, 0, 0, 3, 1], turns);
        Assert.Equal(KhPhase.Roll, c.Phase);
        Assert.Equal(1, c.Turn);
        // Перша хата — нічого; друга — по карті з кожного гекса, крім Дикого поля.
        Assert.Equal(G.VH[spots[3]].Count(h => c.B.ResOf(h) >= 0), Cards(c, 0));
        Assert.Equal(G.VH[spots[5]].Count(h => c.B.ResOf(h) >= 0), Cards(c, 1));
        Assert.All(Enumerable.Range(0, 5), r => Assert.Equal(19, c.Bank[r] + c.Hand[0][r] + c.Hand[1][r] + c.Hand[3][r]));
    }

    [Fact]
    public void Setup_keeps_the_distance_rule()
    {
        var c = Core();
        Assert.Null(c.PlaceTown(0, 0));
        Assert.Null(c.PlaceRoad(0, E(0, 1)));
        Assert.Equal(1, c.Turn);
        Assert.Contains("близько", c.PlaceTown(1, 1));
        Assert.Contains("уже", c.PlaceTown(1, 0));
        Assert.NotNull(c.PlaceTown(0, 30));   // не його черга
        Assert.Null(c.PlaceTown(1, 2));
    }

    // =========================================================================================
    // Кидок і роздача
    // =========================================================================================

    [Fact]
    public void Roll_pays_towns_and_cities_and_the_robber_blocks()
    {
        var c = Core();
        Town(c, 0, G.HexV[0][0]);                 // ліс 10
        Town(c, 1, G.HexV[0][3], city: true);     // той самий ліс, містечко
        c.Phase = KhPhase.Roll;
        c.Turn = 0;
        Assert.Null(c.Roll(0, 4, 6));
        Assert.Equal(1, c.Hand[0][0]);
        Assert.Equal(2, c.Hand[1][0]);
        Assert.Equal(KhPhase.Main, c.Phase);
        var ev = c.Events.Last(e => e.K == "roll").For(null);
        Assert.Equal(2, ((object[])ev["gains"]!).Length);
        Assert.NotNull(c.Roll(0, 4, 6));          // двічі не кидають

        c.Robber = 0;
        c.Phase = KhPhase.Roll;
        Assert.Null(c.Roll(0, 5, 5));
        Assert.Equal(1, c.Hand[0][0]);
    }

    [Fact]
    public void Short_bank_pays_nobody_or_the_only_one()
    {
        var c = Core();
        Town(c, 0, G.HexV[0][0]);
        Town(c, 1, G.HexV[0][3]);
        c.Bank[0] = 1;
        c.Phase = KhPhase.Roll;
        c.Turn = 0;
        Assert.Null(c.Roll(0, 4, 6));
        Assert.Equal(0, c.Hand[0][0] + c.Hand[1][0]);   // двоє на одне дерево — ніхто

        var d = Core();
        Town(d, 1, G.HexV[0][3], city: true);
        d.Bank[0] = 1;
        d.Phase = KhPhase.Roll;
        d.Turn = 0;
        Assert.Null(d.Roll(0, 4, 6));
        Assert.Equal(1, d.Hand[1][0]);                  // один — бере, що лишилось
        Assert.Equal(0, d.Bank[0]);
    }

    // =========================================================================================
    // Сімка, Кармелюк
    // =========================================================================================

    [Fact]
    public void Seven_discards_then_robber_then_steal()
    {
        var c = Core();
        Town(c, 1, G.HexV[4][0]);
        Town(c, 2, G.HexV[4][3]);
        Give(c, 1, wood: 5, ore: 4);   // 9 → скинути 4
        Give(c, 2, clay: 8);           // 8 → 4
        Give(c, 0, grain: 7);          // 7 — не скидає
        c.Phase = KhPhase.Roll;
        c.Turn = 0;
        Assert.Null(c.Roll(0, 3, 4));
        Assert.Equal(KhPhase.Discard, c.Phase);
        Assert.Equal([0, 4, 4, 0], c.DiscardNeed);
        Assert.Equal("discard", c.Must(1));
        Assert.Null(c.Must(0));
        Assert.Contains("рівно 4", c.Discard(1, [1, 0, 0, 0, 0]));
        Assert.NotNull(c.Discard(1, [0, 4, 0, 0, 0]));   // глини нема
        Assert.NotNull(c.Discard(0, [4, 0, 0, 0, 0]));   // йому не треба
        Assert.Null(c.Discard(1, [2, 0, 0, 0, 2]));
        Assert.Equal(KhPhase.Discard, c.Phase);
        Assert.Null(c.Discard(2, [0, 4, 0, 0, 0]));
        Assert.Equal(KhPhase.Robber, c.Phase);
        Assert.NotNull(c.MoveRobber(0, c.Robber));       // на той самий — ні
        Assert.Null(c.MoveRobber(0, 4));
        Assert.Equal(KhPhase.Steal, c.Phase);
        Assert.Equal([1, 2], c.StealFrom);
        Assert.NotNull(c.Steal(0, 3));
        Assert.Null(c.Steal(0, 2));
        Assert.Equal(8, Cards(c, 0));
        Assert.Equal(3, Cards(c, 2));
        Assert.Equal(KhPhase.Main, c.Phase);
        // Що вкрали, бачать лише злодій і пограбований.
        var ev = c.Events.Last(e => e.K == "steal");
        Assert.Equal("clay", ev.For(0)["res"]);
        Assert.Equal("clay", ev.For(2)["res"]);
        Assert.Null(ev.For(1)["res"]);
        Assert.Null(ev.For(null)["res"]);
    }

    [Fact]
    public void Lone_victim_is_robbed_at_once_and_empty_hands_are_skipped()
    {
        var c = Core();
        Town(c, 1, G.HexV[4][0]);
        Town(c, 2, G.HexV[4][3]);
        Give(c, 1, wool: 1);
        c.Phase = KhPhase.Robber;
        c.Turn = 0;
        c.Back = KhPhase.Main;
        Assert.Null(c.MoveRobber(0, 4));
        Assert.Equal(KhPhase.Main, c.Phase);
        Assert.Equal(1, c.Hand[0][2]);
    }

    [Fact]
    public void Friendly_robber_spares_the_poor()
    {
        var c = Core(friendly: true);
        Town(c, 1, G.HexV[4][0]);           // 1 очко — бідний
        Town(c, 0, G.HexV[6][0]);           // свій гекс можна
        c.Phase = KhPhase.Robber;
        c.Turn = 0;
        Assert.DoesNotContain(4, c.RobberSpots(0));
        Assert.Contains(6, c.RobberSpots(0));
        Assert.Contains("дружній", c.MoveRobber(0, 4));
        c.Friendly = false;
        Assert.Contains(4, c.RobberSpots(0));
        // Багатого (3+ очки) грабують і дружньо.
        c.Friendly = true;
        Town(c, 1, G.HexV[17][0], city: true);
        Assert.Contains(4, c.RobberSpots(0));
    }

    // =========================================================================================
    // Будівництво
    // =========================================================================================

    [Fact]
    public void Building_keeps_distance_connection_prices_and_stock()
    {
        var c = Core();
        MainOf(c);
        var hv = G.HexV[0];
        Town(c, 0, hv[0]);
        Assert.Contains("бракує 1 дерева й 1 глини", c.PlaceRoad(0, E(hv[0], hv[1])));
        Give(c, 0, wood: 5, clay: 5, wool: 2, grain: 6, ore: 6);
        Assert.NotNull(c.PlaceRoad(0, E(hv[2], hv[3])));        // не прилягає
        Assert.Null(c.PlaceRoad(0, E(hv[0], hv[1])));
        Assert.Null(c.PlaceRoad(0, E(hv[1], hv[2])));
        Assert.Contains("близько", c.PlaceTown(0, hv[1]));
        Assert.Contains("лише біля свого шляху", c.PlaceTown(0, hv[4]));
        Assert.Null(c.PlaceTown(0, hv[2]));
        Assert.Equal([2, 2, 1, 5, 6], c.Hand[0]);
        Assert.Null(c.PlaceCity(0, hv[2]));
        Assert.Contains("свою хату", c.PlaceCity(0, hv[2]));
        Assert.Equal(1, c.TownsOf(0));
        Assert.Equal(1, c.CitiesOf(0));
        Assert.Equal(3, c.Points(0));

        // Крізь чужу хату шлях не продовжують.
        Town(c, 1, hv[4]);
        Assert.Null(c.PlaceRoad(0, E(hv[2], hv[3])));
        Assert.Null(c.PlaceRoad(0, E(hv[3], hv[4])));
        Assert.False(c.RoadOk(0, E(hv[4], hv[5])));

        // Запас фігур.
        var d = Core();
        MainOf(d);
        Give(d, 0, wood: 2, clay: 2, wool: 2, grain: 2);
        Chain(d, 0, 9, 6);
        for (var v = 0; v < 54 && d.TownsOf(0) < 5; v++)
            if (d.TownFree(v) && !G.HexV[9].Contains(v)) Town(d, 0, v);
        Assert.Equal(5, d.TownsOf(0));
        Assert.Contains("скінчились", d.PlaceTown(0, G.HexV[9][0]));
        Assert.Equal("хати скінчились — перебудуй якусь на містечко", d.WhyNot(0, "town"));
    }

    [Fact]
    public void Can_and_why_speak_plainly()
    {
        var c = Core();
        MainOf(c);
        Town(c, 0, G.HexV[0][0]);
        Chain(c, 0, 0, 2);
        Give(c, 0, grain: 0, ore: 0, wool: 1);
        var (can, why) = c.CanWhy(0);
        Assert.False(can["city"]);
        Assert.Equal("бракує 2 зерна й 3 заліза", why["city"]);
        Assert.Equal("бракує 1 дерева, 1 глини й 1 зерна", why["town"]);
        Assert.Equal("не твій хід", c.CanWhy(1).why["road"]);
        c.Phase = KhPhase.Roll;
        Assert.Equal("спершу кинь кубики", c.CanWhy(0).why["dev"]);
    }

    // =========================================================================================
    // Грамоти
    // =========================================================================================

    [Fact]
    public void Deck_has_twenty_five_charters()
    {
        var c = Core();
        Assert.Equal(25, c.Deck.Count);
        Assert.Equal(14, c.Deck.Count(k => k == KhutoryCore.Knight));
        Assert.Equal(5, c.Deck.Count(k => k == KhutoryCore.Vp));
        Assert.Equal(2, c.Deck.Count(k => k == KhutoryCore.Mono));
    }

    [Fact]
    public void Fresh_charter_waits_and_one_per_turn()
    {
        var c = Core();
        MainOf(c);
        Give(c, 0, wool: 1, grain: 1, ore: 1);
        c.Deck.Add(KhutoryCore.Knight);
        Assert.Null(c.Buy(0));
        Assert.Equal("knight", c.Events.Last(e => e.K == "dev").For(0)["card"]);
        Assert.Null(c.Events.Last(e => e.K == "dev").For(1)["card"]);
        Assert.Equal("dev", c.Events.Last(e => e.K == "dev").For(1)["k"]);
        Assert.Contains("цього ходу", c.Play(0, KhutoryCore.Knight));
        Assert.Contains("бракує", c.Buy(0));
        Assert.Null(c.End(0));
        Assert.False(c.Devs[0][0].Fresh);

        // Козак — і до кидка; одна грамота за хід.
        c.Devs[0].Add(new KhDev { K = KhutoryCore.Plenty });
        MainOf(c);
        c.Phase = KhPhase.Roll;
        c.Rolled = false;
        Assert.Contains("лише козака", c.Play(0, KhutoryCore.Plenty, 0, 1));
        Assert.Null(c.Play(0, KhutoryCore.Knight));
        Assert.Equal(KhPhase.Robber, c.Phase);
        Assert.Null(c.MoveRobber(0, 0));
        Assert.Equal(KhPhase.Roll, c.Phase);      // повернулись до кидка
        Assert.Equal(1, c.Knights[0]);
        Assert.Null(c.Roll(0, 1, 1));
        Assert.Contains("Одну грамоту", c.Play(0, KhutoryCore.Plenty, 0, 1));
    }

    [Fact]
    public void Every_charter_does_its_thing()
    {
        var c = Core();
        MainOf(c);
        Town(c, 0, G.HexV[0][0]);
        c.Devs[0].AddRange([new KhDev { K = KhutoryCore.Toloka }, new KhDev { K = KhutoryCore.Plenty }, new KhDev { K = KhutoryCore.Mono }, new KhDev { K = KhutoryCore.Vp }]);
        Assert.Equal(2, c.Points(0));
        Assert.Equal(1, c.PublicPoints(0));
        Assert.Contains("Скарб не грають", c.Play(0, KhutoryCore.Vp));

        Assert.Null(c.Play(0, KhutoryCore.Toloka));
        Assert.Equal(KhPhase.Road2, c.Phase);
        Assert.Equal("road", c.Must(0));
        Assert.Null(c.PlaceRoad(0, E(G.HexV[0][0], G.HexV[0][1])));
        Assert.Equal(KhPhase.Road2, c.Phase);
        Assert.Null(c.PlaceRoad(0, E(G.HexV[0][1], G.HexV[0][2])));
        Assert.Equal(KhPhase.Main, c.Phase);
        Assert.Equal(2, c.RoadsOf(0));
        Assert.Equal(0, Cards(c, 0));             // задарма
        Assert.Null(c.End(0));

        MainOf(c);
        Assert.Null(c.Play(0, KhutoryCore.Plenty, 4, 4));
        Assert.Equal(2, c.Hand[0][4]);
        Assert.Null(c.End(0));

        MainOf(c);
        Give(c, 1, wool: 3);
        Give(c, 2, wool: 1, ore: 2);
        Assert.Null(c.Play(0, KhutoryCore.Mono, 2));
        Assert.Equal(4, c.Hand[0][2]);
        Assert.Equal(0, c.Hand[1][2] + c.Hand[2][2]);
        Assert.Equal(2, c.Hand[2][4]);
    }

    [Fact]
    public void Treasure_wins_at_once_on_own_turn()
    {
        var c = Core();
        MainOf(c);
        c.Target = 8;
        foreach (var v in new[] { G.HexV[0][0], G.HexV[2][0], G.HexV[16][3] }) Town(c, 0, v, city: true);
        Town(c, 0, G.HexV[18][3]);   // 7 очок
        c.Deck.Add(KhutoryCore.Vp);
        Give(c, 0, wool: 1, grain: 1, ore: 1);
        Assert.Null(c.Buy(0));
        Assert.Equal(KhPhase.Over, c.Phase);
        Assert.Equal(0, c.Winner);
        Assert.Equal(7, c.PublicPoints(0));
        Assert.Equal("win", c.Events.Last().K);
    }

    [Fact]
    public void Victory_comes_only_on_own_turn()
    {
        var c = Core();
        MainOf(c);
        foreach (var v in new[] { G.HexV[0][0], G.HexV[2][0], G.HexV[16][3], G.HexV[18][3] }) Town(c, 1, v, city: true);
        Assert.Equal(8, c.Points(1));
        Give(c, 0, wood: 1, clay: 1);
        Town(c, 0, G.HexV[9][0]);
        Assert.Null(c.PlaceRoad(0, G.VE[G.HexV[9][0]][0]));
        Assert.False(c.Over);                     // восьмеро очок у чужий хід — ще не перемога
        Assert.Null(c.End(0));
        Assert.True(c.Over);
        Assert.Equal(1, c.Winner);
    }

    // =========================================================================================
    // Чумацький шлях і сотня
    // =========================================================================================

    [Fact]
    public void Longest_road_counts_forks_and_rings()
    {
        var c = Core();
        Chain(c, 0, 9, 6);                         // кільце довкола центру
        Assert.Equal(6, c.LongestRoad(0));
        var hv = G.HexV[9];
        var tail = G.VE[hv[0]].First(e => c.EOwner[e] < 0);
        c.EOwner[tail] = 0;
        c.RecountRoads();
        Assert.Equal(7, c.LongestRoad(0));         // хвіст і все кільце
        Assert.Equal(0, c.Longest);

        // Вилка: 4 ребра вздовж гекса + гілка з другої вершини — довше за гілку не стане.
        var d = Core();
        Chain(d, 1, 0, 4);
        var branch = G.VE[G.HexV[0][1]].First(e => d.EOwner[e] < 0);
        d.EOwner[branch] = 1;
        d.RecountRoads();
        Assert.Equal(4, d.LongestRoad(1));
        Assert.Null(d.Longest);                    // < 5 — нагороди нема
    }

    [Fact]
    public void Broken_road_passes_the_award_or_leaves_it_nobodys()
    {
        var c = Core();
        Chain(c, 0, 0, 5);
        Assert.Equal(0, c.Longest);
        Chain(c, 1, 18, 5);
        Assert.Equal(0, c.Longest);                // нічия — власник зберігає
        Chain(c, 2, 16, 5);
        Town(c, 3, G.HexV[0][2]);                  // розірвали власникові
        Assert.Equal(3, c.LongestRoad(0));
        Assert.Null(c.Longest);                    // двоє рівних — нікому
        Assert.Equal("award", c.Events.Last().K);
        Assert.Null(c.Events.Last().For(null)["seat"]);
        c.EOwner[G.VE[G.HexV[18][5]].First(e => c.EOwner[e] < 0)] = 1;
        c.RecountRoads();
        Assert.Equal(1, c.Longest);                // єдиний найдовший
        Assert.Equal(2, c.PublicPoints(1));

        var d = Core();
        Chain(d, 0, 0, 5);
        Town(d, 1, G.HexV[0][3]);
        Assert.Null(d.Longest);                    // розірвали, а більше ніхто не має 5 — нікому
    }

    [Fact]
    public void Strongest_hundred_needs_three_and_more_than_the_holder()
    {
        var c = Core();
        for (var round = 0; round < 3; round++)
            foreach (var s in new[] { 0, 1 })
            {
                MainOf(c, s);
                c.PlayedDev = false;
                c.Devs[s].Add(new KhDev { K = KhutoryCore.Knight });
                Assert.Null(c.Play(s, KhutoryCore.Knight));
                Assert.Null(c.MoveRobber(s, c.Robber == 0 ? 1 : 0));
            }
        Assert.Equal(0, c.Army);                   // 3:3 — у першого
        MainOf(c, 1);
        c.PlayedDev = false;
        c.Devs[1].Add(new KhDev { K = KhutoryCore.Knight });
        Assert.Null(c.Play(1, KhutoryCore.Knight));
        Assert.Equal(1, c.Army);
        Assert.Equal(2, c.PublicPoints(1));
    }

    // =========================================================================================
    // Торгівля
    // =========================================================================================

    [Fact]
    public void Bank_trades_at_port_rates()
    {
        var c = Core();
        MainOf(c);
        var b = c.B;
        Assert.Equal([4, 4, 4, 4, 4], c.Rates(0));
        Town(c, 0, G.Edges[b.PortEdge[1]][0]);     // пристань вовни
        Assert.Equal([4, 4, 2, 4, 4], c.Rates(0));
        Town(c, 0, G.Edges[b.PortEdge[0]][1]);     // «3:1»
        Assert.Equal([3, 3, 2, 3, 3], c.Rates(0));
        Give(c, 0, wool: 3, wood: 2);
        Assert.Contains("треба віддати 3 дерева", c.BankTrade(0, 0, 4, 1));
        Assert.NotNull(c.BankTrade(0, 2, 2, 1));
        Assert.Null(c.BankTrade(0, 2, 4, 1));
        Assert.Equal([2, 0, 1, 0, 1], c.Hand[0]);
        Assert.Equal("bank", c.Events.Last().For(null)["with"]);
        Assert.NotNull(c.BankTrade(1, 2, 4, 1));   // не його хід
    }

    [Fact]
    public void Offers_check_cards_at_deal_and_vanish_at_end_of_turn()
    {
        var c = Core();
        MainOf(c);
        Give(c, 0, wood: 1);
        Give(c, 1, ore: 1);
        Give(c, 2, grain: 2);
        Assert.NotNull(c.Offer(0, [2, 0, 0, 0, 0], [0, 0, 0, 0, 1], null));    // стільки нема
        Assert.Null(c.Offer(0, [1, 0, 0, 0, 0], [0, 0, 0, 0, 1], null));
        var id = c.Offers[0].Id;
        Assert.NotNull(c.Answer(2, id, true));     // руда в нього нема
        Assert.Null(c.Answer(2, id, false));
        Assert.Null(c.Answer(1, id, true));
        Assert.Equal([1], c.Offers[0].Acc);
        Assert.NotNull(c.Deal(0, id, 2));          // не приймав

        // Зустрічна від Іри: 2 зерна за дерево.
        Assert.Null(c.Offer(2, [0, 0, 0, 2, 0], [1, 0, 0, 0, 0], 3));
        var counter = c.Offers[1];
        Assert.Equal(0, counter.To);
        Assert.NotNull(c.Answer(1, counter.Id, false));   // на зустрічну відповідає лише поточний
        Assert.Null(c.Answer(0, counter.Id, true));
        // Ще одна зустрічна — і поточний її відхиляє: вона зникає.
        Assert.Null(c.Offer(2, [0, 0, 0, 1, 0], [1, 0, 0, 0, 0], null));
        Assert.Null(c.Answer(0, c.Offers[2].Id, false));
        Assert.Equal(2, c.Offers.Count);

        // Петро встиг витратити руду — угода не відбудеться.
        c.Hand[1][4] = 0; c.Bank[4]++;
        var before = (int[])c.Hand[0].Clone();
        Assert.Contains("нема", c.Deal(0, id, 1));
        Assert.Equal(before, c.Hand[0]);

        Assert.Null(c.Deal(0, counter.Id, 2));
        Assert.Equal([0, 0, 0, 2, 0], c.Hand[0]);
        Assert.Equal([1, 0, 0, 0, 0], c.Hand[2]);
        Assert.Empty(c.Offers);                    // дерево пішло — і перша пропозиція зникла
        Assert.Equal(1, c.Trades[0]);

        Assert.Null(c.Offer(0, [0, 0, 0, 1, 0], [1, 0, 0, 0, 0], 2));
        Assert.Contains("автор", c.Cancel(2, c.Offers[0].Id));
        Assert.Null(c.End(0));
        Assert.Empty(c.Offers);
    }

    // =========================================================================================
    // Кімната: опції, види, таймер, вихід, рематч
    // =========================================================================================

    static readonly string[] Crew = ["Оля", "Петро", "Іра", "Влад"];

    static RoomHarness Table(int people = 2, string bots = "0", string map = "starter", string timer = "60", int seed = 5)
    {
        var h = new RoomHarness("khutory", new { map, timer, bots }, seed: seed);
        for (var i = 0; i < people; i++) h.Join(Crew[i]);
        Assert.True(h.Start().Ok);
        return h;
    }

    static KhutoryCore CoreOf(RoomHarness h) => ((Khutory)h.Room.Game).Core;

    [Fact]
    public void Info_and_options_are_as_in_the_contract()
    {
        var info = new Khutory().Info;
        Assert.Equal("khutory", info.Id);
        Assert.True(info.Hidden);
        Assert.Equal(GameGroup.Board, info.Group);
        Assert.Equal(StartMode.ByHost, info.Start);
        Assert.Equal((1, 4), (info.MinPlayers, info.MaxPlayers));
        Assert.Equal(["vp", "map", "friendly", "timer", "bots"], info.Options!.Select(o => o.Key));
        Assert.Equal("червоні", new Khutory().SeatName(0));
        Assert.Equal("фіолетові", new Khutory().SeatName(3));
    }

    [Fact]
    public void Alone_needs_a_bot()
    {
        var h = new RoomHarness("khutory", new { bots = "0" });
        h.Join("Оля");
        Assert.False(h.Start().Ok);
        var b = Table(1, bots: "2");
        var v = b.View(0);
        Assert.Equal(3, v.GetProperty("players").GetArrayLength());
        Assert.Equal(2, v.GetProperty("bots").EnumerateArray().Count(x => x.ValueKind == JsonValueKind.String));
    }

    [Fact]
    public void Views_show_own_hand_only()
    {
        var h = Table(2);
        var core = CoreOf(h);
        core.Hand[0][0] = 3; core.Bank[0] -= 3;
        var v0 = h.View(0);
        var v1 = h.View(1);
        var spectator = h.View(null);
        Assert.Equal(1, v0.GetProperty("v").GetInt32());
        var board = v0.GetProperty("board");
        Assert.Equal(19, board.GetProperty("hexes").GetArrayLength());
        Assert.Equal(54, board.GetProperty("vx").GetArrayLength());
        Assert.Equal(72, board.GetProperty("edges").GetArrayLength());
        Assert.Equal(9, board.GetProperty("ports").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, board.GetProperty("hexes")[9].GetProperty("num").ValueKind);
        Assert.Equal("setup", v0.GetProperty("phase").GetString());
        Assert.Equal(3, v0.GetProperty("me").GetProperty("hand").GetProperty("wood").GetInt32());
        Assert.Equal(1, v1.GetProperty("me").GetProperty("seat").GetInt32());
        Assert.Equal(0, v1.GetProperty("me").GetProperty("hand").GetProperty("wood").GetInt32());
        Assert.Equal(JsonValueKind.Null, spectator.GetProperty("me").ValueKind);
        foreach (var v in new[] { v0, v1, spectator })
            foreach (var p in v.GetProperty("players").EnumerateArray())
            {
                Assert.False(p.TryGetProperty("hand", out _));
                Assert.Equal(JsonValueKind.Number, p.GetProperty("cards").ValueKind);
            }
        Assert.Equal(3, v1.GetProperty("players")[0].GetProperty("cards").GetInt32());
        Assert.DoesNotContain("\"wood\":3", spectator.GetRawText());

        var turn = v0.GetProperty("turn").GetInt32();
        var mine = h.View(turn).GetProperty("me");
        Assert.Equal("town", mine.GetProperty("must").GetString());
        Assert.Equal(54, mine.GetProperty("spots").GetProperty("town").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, h.View(1 - turn).GetProperty("me").GetProperty("spots").ValueKind);
        Assert.Equal(40000, v0.GetProperty("timerMs").GetInt32());
        Assert.Equal(JsonValueKind.String, v0.GetProperty("deadline").ValueKind);
    }

    [Fact]
    public void Illegal_moves_do_not_change_the_view()
    {
        var h = Table(2);
        var turn = h.View(0).GetProperty("turn").GetInt32();
        var other = 1 - turn;
        var before = h.View(turn).GetRawText();
        Assert.False(h.Act(other, "town", new { v = 0 }).Ok);
        Assert.False(h.Act(turn, "road", new { e = 0 }).Ok);
        Assert.False(h.Act(turn, "town", new { v = 99 }).Ok);
        Assert.False(h.Act(turn, "town", new { x = 1 }).Ok);
        Assert.False(h.Act(turn, "roll").Ok);
        Assert.False(h.Act(turn, "fly").Ok);
        Assert.False(h.Act(turn, "timeout").Ok);   // час ще не минув
        Assert.False(h.Act(turn, "bot").Ok);       // ботів нема
        Assert.Equal(before, h.View(turn).GetRawText());

        Assert.True(h.Act(turn, "town", new { v = 0 }).Ok);
        var e = h.View(turn).GetProperty("me").GetProperty("spots").GetProperty("road")[0].GetInt32();
        Assert.True(h.Act(turn, "road", new { e }).Ok);
        Assert.Equal(other, h.View(0).GetProperty("turn").GetInt32());
        var ev = h.View(0).GetProperty("events");
        Assert.Contains(ev.EnumerateArray(), x => x.GetProperty("k").GetString() == "build");
    }

    [Fact]
    public void Sleeper_gets_auto_moves_and_then_a_bot()
    {
        var h = Table(2);
        var core = CoreOf(h);
        var a = core.Order[0];
        var b = core.Order[1];
        Assert.False(h.Act(a, "timeout").Ok);
        h.Clock.Advance(41);
        Assert.True(h.Act(b, "timeout").Ok);       // будь-хто штовхає
        Assert.Equal(1, core.TownsOf(a));
        Assert.Equal(1, core.RoadsOf(a));
        Assert.Equal(b, core.Turn);
        h.Clock.Advance(41);
        Assert.True(h.Act(a, "timeout").Ok);
        Assert.False(h.View(a).GetProperty("players")[b].GetProperty("away").GetBoolean());
        h.Clock.Advance(41);
        Assert.True(h.Act(a, "timeout").Ok);       // друга поспіль — далі за нього Глек
        Assert.Equal(2, core.TownsOf(b));
        Assert.True(h.View(a).GetProperty("players")[b].GetProperty("away").GetBoolean());

        // Без часу — timeout відмовляє.
        var calm = Table(2, timer: "none");
        Assert.Equal(JsonValueKind.Null, calm.View(0).GetProperty("deadline").ValueKind);
        calm.Clock.Advance(1000);
        Assert.False(calm.Act(0, "timeout").Ok);
    }

    [Fact]
    public void Leaver_is_replaced_by_a_bot_and_the_last_one_ends_it()
    {
        var h = Table(3);
        h.Leave("Іра");
        Assert.Empty(h.Finished);
        var p = h.View(0).GetProperty("players")[2];
        Assert.True(p.GetProperty("away").GetBoolean());
        Assert.Equal("Іра", p.GetProperty("nick").GetString());
        Assert.Contains(h.View(0).GetProperty("log").EnumerateArray(), l => l.GetString()!.Contains("Глек сідає замість Іра"));
        h.Leave("Петро");
        Assert.Empty(h.Finished);
        h.Leave("Оля");
        Assert.Single(h.Finished);
    }

    [Fact]
    public void Whole_game_with_bots_ends_and_rematch_deals_a_new_map()
    {
        var h = Table(1, bots: "3", map: "random", seed: 11);
        var firstMap = h.View(0).GetProperty("board").GetProperty("id").GetString();
        var steps = 0;
        while (h.Finished.Count == 0)
        {
            Assert.True(++steps < 6000, "партія не кінчається");
            var v = h.View(0);
            if (v.GetProperty("botIn").ValueKind == JsonValueKind.Number)
            {
                // Задрімала людина має 5 с на свій кидок — botIn тоді довший за ThinkMs.
                h.Clock.AdvanceMs(Math.Max(BoardBots.ThinkMs, v.GetProperty("botIn").GetInt32()));
                Assert.True(h.Act(0, "bot").Ok);
            }
            else
            {
                h.Clock.Advance(181);
                Assert.True(h.Act(0, "timeout").Ok);
            }
        }
        var end = h.View(0);
        Assert.Equal("over", end.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, end.GetProperty("turn").ValueKind);
        var over = end.GetProperty("over");
        Assert.Equal("points", over.GetProperty("reason").GetString());
        var winner = over.GetProperty("winner").GetInt32();
        Assert.True(over.GetProperty("points")[winner].GetInt32() >= 8);
        var stats = over.GetProperty("stats");
        Assert.Equal(11, stats.GetProperty("dice").GetArrayLength());
        Assert.Equal(4, stats.GetProperty("seats").GetArrayLength());
        Assert.True(stats.GetProperty("timeline").GetArrayLength() >= 2);

        Assert.True(h.Rematch().Ok);
        var again = h.View(0);
        Assert.Equal("setup", again.GetProperty("phase").GetString());
        Assert.NotEqual(firstMap, again.GetProperty("board").GetProperty("id").GetString());
        Assert.False(again.GetProperty("players")[0].GetProperty("away").GetBoolean());
    }
}

/// <summary>Самогра «Хуторів» — заміри часу, тож без паралелі з іншими тестами.</summary>
[Collection(SerialPerf.Name)]
public class KhutorySelfPlayTests
{
    // =========================================================================================
    // Самогра: чотири Глеки до перемоги
    // =========================================================================================

    [Fact]
    [Trait("Category", "Perf")]
    public void Two_hundred_bot_games_end_with_a_winner_and_keep_invariants()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var turnsTotal = 0;
        for (var seed = 1; seed <= 200; seed++)
        {
            var rng = new Random(seed);
            var n = 2 + seed % 3;
            var board = seed % 5 == 0 ? KhutoryBoard.Starter() : KhutoryBoard.Random(rng);
            var c = new KhutoryCore(board, Enumerable.Range(0, n), rng) { Target = seed % 2 == 0 ? 10 : 8, Friendly = seed % 3 != 0 };
            c.Begin(rng.Next(n));
            var steps = 0;
            while (!c.Over)
            {
                var s = Enumerable.Range(0, 4).Select(i => (c.Turn + i) % 4).First(c.MayAct);
                var err = KhutoryAuto.Step(c, s, rng);
                Assert.True(err is null, $"сід {seed}, крок {steps}, фаза {c.Phase}: {err}");
                Invariants(c);
                Assert.True(++steps < 20_000, $"сід {seed}: партія не кінчається");
            }
            var turns = c.Rounds * c.Seats + c.TurnIdx;
            Assert.True(turns < 400, $"сід {seed}: {turns} ходів");
            Assert.True(c.Points(c.Winner!.Value) >= c.Target);
            turnsTotal += turns;
        }
        Assert.True(sw.ElapsedMilliseconds < 5000, $"{sw.ElapsedMilliseconds} мс");
        Assert.InRange(turnsTotal / 200, 10, 300);
    }

    /// <summary>Банк + руки = 19 кожного ресурсу, фігури в межах запасу й збігаються з полем, очки = сума, грамоти всі на місці.</summary>
    static void Invariants(KhutoryCore c)
    {
        for (var r = 0; r < 5; r++)
        {
            var sum = c.Bank[r];
            for (var s = 0; s < 4; s++)
            {
                if (c.Hand[s][r] < 0) Assert.Fail($"від'ємна рука {s}");
                sum += c.Hand[s][r];
            }
            if (sum != 19) Assert.Fail($"ресурс {r}: {sum}");
        }
        var towns = new int[4];
        var cities = new int[4];
        var roads = new int[4];
        for (var v = 0; v < 54; v++) if (c.VOwner[v] >= 0) (c.VCity[v] ? cities : towns)[c.VOwner[v]]++;
        foreach (var o in c.EOwner) if (o >= 0) roads[o]++;
        for (var s = 0; s < 4; s++)
        {
            if (roads[s] > 15 || towns[s] > 5 || cities[s] > 4) Assert.Fail($"фігур більше за запас у {s}");
            if (roads[s] != c.RoadsOf(s) || towns[s] != c.TownsOf(s) || cities[s] != c.CitiesOf(s)) Assert.Fail($"лічильники фігур {s}");
            var pts = towns[s] + 2 * cities[s] + (c.Longest == s ? 2 : 0) + (c.Army == s ? 2 : 0);
            if (pts != c.PublicPoints(s)) Assert.Fail($"очки {s}");
        }
        var devs = c.Deck.Count + c.PlayedOther;
        for (var s = 0; s < 4; s++) devs += c.Devs[s].Count + c.Knights[s];
        if (devs != 25) Assert.Fail($"грамот {devs}");
    }
}
