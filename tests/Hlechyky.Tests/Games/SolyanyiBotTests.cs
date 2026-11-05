using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;
using static Hlechyky.Games.Impl.SolyanyiMap;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Глек-гравець «Соляного шляху» (контракт §8.1, §14): 10 000 партій лише Глеками без жодного незаконного кроку й з
/// інваріантами після кожного ходу, медіани кіл, рівновага за чергою, блок і те, що Глек не підглядає (порядок колод і чужі
/// руки й замовлення не міняють його ходу).
/// </summary>
public class SolyanyiBotTests(ITestOutputHelper output)
{
    /// <summary>Підсумок однієї партії Глеків.</summary>
    internal sealed record Run(int Players, int Wagons, int Rounds, int Turns, string Reason, int[] WinPos, double Top, int Kept, int DoneN, int Failed, int Blocks, int Wipes, int Shuffles);

    /// <summary>Ядро на <paramref name="players"/> місць 0..n−1 з розкладом із сіду (до кінця setup).</summary>
    internal static SolyanyiCore Core(int players, int seed, int wagons = 45)
    {
        var c = new SolyanyiCore(Enumerable.Range(0, players)) { Wagons0 = wagons };
        c.Begin(new Random(seed));
        return c;
    }

    /// <summary>Ядро, де всі вже обрали за Глека (фаза play).</summary>
    internal static SolyanyiCore Playing(int players, int seed, int wagons = 45)
    {
        var c = Core(players, seed, wagons);
        foreach (var s in c.FromFirst().ToArray()) Must(SolyanyiBot.Apply(c, s, SolyanyiBot.Decide(c, s)), "keep");
        return c;
    }

    static void Must(string? err, string what)
    {
        if (err is not null) throw new Xunit.Sdk.XunitException($"незаконний крок {what}: {err}");
    }

    /// <summary>Інваріанти §14: 110 карт і 40 замовлень на місцях, вози, очки й дороги сходяться з шляхами.</summary>
    internal static void Invariants(SolyanyiCore c)
    {
        var cards = c.Deck.Count + c.Discard.Count + c.Market.Count(m => m >= 0) + c.Order.Sum(s => c.Hand[s].Sum());
        if (cards != DeckSize) throw new Xunit.Sdk.XunitException($"карт {cards} ≠ 110");
        var kinds = new int[9];
        foreach (var x in c.Deck.Concat(c.Discard).Concat(c.Market.Where(m => m >= 0))) kinds[x]++;
        foreach (var s in c.Order)
            for (var i = 0; i < 9; i++)
            {
                if (c.Hand[s][i] < 0) throw new Xunit.Sdk.XunitException("від'ємна рука");
                kinds[i] += c.Hand[s][i];
            }
        for (var i = 0; i < 9; i++)
            if (kinds[i] != (i == Wild ? DeckWild : DeckEach)) throw new Xunit.Sdk.XunitException($"вид {i}: {kinds[i]}");
        var tickets = new List<int>(c.TicketDeck);
        foreach (var s in c.Order)
        {
            tickets.AddRange(c.Tickets[s]);
            tickets.AddRange(c.Offer[s]);
        }
        if (tickets.Count != 40 || tickets.Distinct().Count() != 40) throw new Xunit.Sdk.XunitException($"замовлень {tickets.Count}");
        foreach (var s in c.Order)
        {
            var len = c.Taken[s].Sum(r => Routes[r].Len);
            var pts = c.Taken[s].Sum(r => Routes[r].Pts);
            if (c.Wagons[s] != c.Wagons0 - len || c.Wagons[s] < 0 || pts != c.RoutePts[s]) throw new Xunit.Sdk.XunitException($"місце {s}: вози/очки");
            if (c.Taken[s].Any(r => c.Owner[r] != s)) throw new Xunit.Sdk.XunitException($"місце {s}: власник шляху");
            foreach (var r in c.Taken[s])
                if (Routes[r].Twin >= 0 && c.Owner[Routes[r].Twin] == s) throw new Xunit.Sdk.XunitException($"місце {s}: обидва парні");
        }
        if (c.OnePairOnly)
            foreach (var r in Routes)
                if (r.Twin >= 0 && c.Owner[r.Id] >= 0 && c.Owner[r.Twin] >= 0) throw new Xunit.Sdk.XunitException("за столом на 2–3 обидва парні зайняті");
    }

    /// <summary>Дограти партію лише Глеками; кожен крок — законний (помилка — виняток), інваріанти після кожного ходу.</summary>
    internal static Run Play(SolyanyiCore c, bool check = true)
    {
        foreach (var s in c.FromFirst().ToArray())
            if (c.Phase == SlPhase.Setup && !c.Ready[s]) Must(SolyanyiBot.Apply(c, s, SolyanyiBot.Decide(c, s)), "keep");
        var guard = 0;
        var wipes = 0;
        var shuffles = 0;
        long seen = 0;
        while (!c.Over)
        {
            var s = c.Turn;
            var turns = c.Turns;
            while (!c.Over && c.Turn == s && c.Turns == turns)
            {
                var m = SolyanyiBot.Decide(c, s);
                Must(SolyanyiBot.Apply(c, s, m), m.Act);
                if (++guard > 1200) throw new Xunit.Sdk.XunitException("партія не кінчається");
            }
            if (check) Invariants(c);
            if (c.Turns > 400) throw new Xunit.Sdk.XunitException("понад 400 ходів");
            foreach (var e in c.Events)
            {
                if (e.Seq <= seen) continue;
                seen = e.Seq;
                if (e.K == "wipe") wipes++;
                else if (e.K == "shuffle") shuffles++;
            }
        }
        var pos = c.Winners.Select(w => (Array.IndexOf(c.Order, w) - Array.IndexOf(c.Order, c.First) + c.Seats) % c.Seats).ToArray();
        return new Run(c.Seats, c.Wagons0, c.Round, c.Turns, c.Reason, pos, c.Total(c.Winners[0]),
            c.Order.Sum(s => c.Tickets[s].Count), c.Order.Sum(c.DoneCount), c.Order.Sum(s => c.Tickets[s].Count(t => !c.Done[t])), c.Order.Sum(s => c.Blocks[s]), wipes, shuffles);
    }

    static double Median(IEnumerable<int> a)
    {
        var s = a.Order().ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2.0;
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Ten_thousand_bot_games_are_legal_finish_and_stay_in_the_expected_shape()
    {
        var games = new Run[10_000];
        var sw = Stopwatch.StartNew();
        // Партії незалежні: так 10 000 не з'їдають хвилину прогону всього набору.
        Parallel.For(1, 10_001, seed =>
        {
            var players = 2 + seed % 4;
            var wagons = (seed / 4) % 2 == 0 ? 45 : 35;
            games[seed - 1] = Play(Core(players, seed, wagons));
        });
        sw.Stop();
        output.WriteLine($"10 000 партій: {sw.Elapsed.TotalSeconds:F1} с");
        foreach (var w in new[] { 45, 35 })
            foreach (var n in new[] { 2, 3, 4, 5 })
            {
                var g = games.Where(x => x.Players == n && x.Wagons == w).ToList();
                var win = new double[n];
                foreach (var x in g) foreach (var p in x.WinPos) win[p] += 1.0 / x.WinPos.Length;
                output.WriteLine($"{w} возів, {n} гравці: {g.Count} партій, медіана кіл {Median(g.Select(x => x.Rounds))}, ходів {Median(g.Select(x => x.Turns))} (макс {g.Max(x => x.Turns)}), "
                    + $"переможець {g.Average(x => x.Top):F0} ⭐, узято {g.Average(x => (double)x.Kept / n):F1}, виконано {g.Average(x => (double)x.DoneN / n):F1} ({g.Sum(x => x.DoneN) / (double)g.Sum(x => x.Kept):P0}), "
                    + $"зірвано {g.Average(x => (double)x.Failed / n):F2} на гравця, блоків {g.Average(x => x.Blocks):F2}, замін ринку {g.Average(x => x.Wipes):F1}, тасувань {g.Average(x => x.Shuffles):F1}, "
                    + $"stall {g.Count(x => x.Reason == "stall")}, перемоги за чергою [{string.Join(", ", win.Select(v => (v / g.Count).ToString("P1")))}]");
                Assert.All(g, x => Assert.True(x.Turns <= 400));
                var med = Median(g.Select(x => x.Rounds));
                if (w == 45) Assert.InRange(med, 30, 50);
                else Assert.InRange(med, 25, 40);
                Assert.True(g.Average(x => x.Top) >= (w == 45 ? 80 : 60), $"переможець {g.Average(x => x.Top):F0}");
                Assert.True(g.Average(x => (double)x.Failed / n) <= 2, "зірвано забагато");
                var first = win[0] / g.Count;
                Assert.InRange(first, 1.0 / n - 0.10, 1.0 / n + 0.10);
            }
        Assert.True(games.Count(x => x.Reason == "wagons") >= 0.999 * games.Length, $"возами скінчилось {games.Count(x => x.Reason == "wagons")}");
        Assert.Contains(games, x => x.Blocks > 0);
        Assert.True(sw.Elapsed.TotalSeconds <= 120, $"{sw.Elapsed.TotalSeconds:F0} с");
    }

    [Fact]
    public void Same_seed_same_game()
    {
        var a = Core(4, 77);
        var b = Core(4, 77);
        Play(a, check: false);
        Play(b, check: false);
        Assert.Equal(JsonSerializer.Serialize(a.Snapshot()), JsonSerializer.Serialize(b.Snapshot()));
    }

    /// <summary>Та сама публічна картина для місця <paramref name="s"/>: інший порядок колод, чужі руки й замовлення — вперемішку.</summary>
    static SolyanyiCore Scramble(SolyanyiCore src, int s, int seed)
    {
        var c = src.Clone();
        var rng = new Random(seed);
        // Перемішати приховане: колоду разом із чужими руками (кількості лишаються), колоду замовлень разом із чужими замовленнями.
        var pool = new List<int>(c.Deck);
        foreach (var o in c.Order)
            if (o != s)
                for (var k = 0; k < 9; k++) pool.AddRange(Enumerable.Repeat(k, c.Hand[o][k]));
        var mixed = pool.OrderBy(_ => rng.Next()).ToList();
        var at = 0;
        foreach (var o in c.Order)
        {
            if (o == s) continue;
            var n = c.Hand[o].Sum();
            c.Hand[o] = new int[9];
            for (var i = 0; i < n; i++) c.Hand[o][mixed[at++]]++;
        }
        c.Deck = mixed.Skip(at).ToList();
        var tickets = new List<int>(c.TicketDeck);
        foreach (var o in c.Order)
            if (o != s)
            {
                tickets.AddRange(c.Tickets[o].Where(t => !c.Done[t]));
                tickets.AddRange(c.Offer[o]);
            }
        var shuffled = tickets.OrderBy(_ => rng.Next()).ToList();
        at = 0;
        foreach (var o in c.Order)
        {
            if (o == s) continue;
            c.Tickets[o] = [.. c.Tickets[o].Select(t => c.Done[t] ? t : shuffled[at++])];
            c.Offer[o] = [.. c.Offer[o].Select(_ => shuffled[at++])];
        }
        c.TicketDeck = shuffled.Skip(at).ToList();
        return c;
    }

    [Fact]
    public void Glek_does_not_peek_at_decks_foreign_hands_or_foreign_tickets()
    {
        foreach (var seed in new[] { 3, 8, 21 })
            foreach (var n in new[] { 2, 3, 5 })
            {
                var c = Playing(n, seed);
                for (var i = 0; i < 400 && !c.Over; i++)
                {
                    var s = c.Turn;
                    var m = SolyanyiBot.Decide(c, s);
                    var other = Scramble(c, s, seed * 1000 + i);
                    Invariants(other);
                    var m2 = SolyanyiBot.Decide(other, s);
                    Assert.Equal(Wire(m), Wire(m2));
                    Must(SolyanyiBot.Apply(c, s, m), m.Act);
                }
            }
    }

    static string Wire(SlMove m) => JsonSerializer.Serialize(m);

    [Fact]
    public void Plan_shares_routes_between_tickets_and_marks_impossible_ones_dead()
    {
        var c = Playing(4, 5);
        var s = c.Turn;
        foreach (var o in c.Order) c.Tickets[o].Clear();
        // Ужгород — Харків і Львів — Дніпро йдуть спільною дорогою: разом дешевше, ніж нарізно.
        var a = SolyanyiBot.Plan(c, s, [0]);
        var b = SolyanyiBot.Plan(c, s, [5]);
        var both = SolyanyiBot.Plan(c, s, [0, 5]);
        Assert.True(both.Need < a.Need + b.Need);
        Assert.Empty(both.Dead);
        Assert.Equal(both.Need, both.Zero.Sum(r => Routes[r].Len));
        // Вози скінчились — більше не везе.
        c.Wagons[s] = 5;
        var poor = SolyanyiBot.Plan(c, s, [0]);
        Assert.Equal([0], poor.Dead);
        Assert.Empty(poor.Zero);
        c.Wagons[s] = 45;
        // Усі виходи з Ізмаїла забрали — замовлення мертве, плана нема.
        foreach (var r in Adj[26]) c.Owner[r.Id] = c.Next(s);
        var cut = SolyanyiBot.Plan(c, s, [11]);
        Assert.Equal([11], cut.Dead);
        Assert.Empty(cut.Zero);
    }

    [Fact]
    public void Closed_twin_disappears_from_the_plan_at_two_or_three_and_stays_at_four()
    {
        foreach (var (n, closed) in new[] { (3, true), (4, false) })
        {
            var c = Playing(n, 5);
            var s = c.Turn;
            var o = c.Next(s);
            // Київ — Біла Церква: 38 зайнято суперником; 90 — парний.
            c.Owner[38] = o;
            c.Taken[o].Add(38);
            Assert.Equal(closed ? -1 : 2, c.Cost(s, 90));
            var plan = SolyanyiBot.Plan(c, s, [34]);   // Біла Церква — Кропивницький не торкається
            Assert.DoesNotContain(38, plan.Zero);
            if (closed) Assert.DoesNotContain(90, plan.Zero);
            Assert.Equal(closed ? [90] : Array.Empty<int>(), c.Closed());
        }
    }

    [Fact]
    public void Glek_replans_when_a_route_is_taken()
    {
        var c = Playing(3, 5);
        var s = c.Turn;
        var o = c.Next(s);
        c.Tickets[s].Clear();
        var plan = SolyanyiBot.Plan(c, s, [18]);   // Одеса — Київ
        var cut = plan.Zero.First();
        c.Owner[cut] = o;
        c.Taken[o].Add(cut);
        var again = SolyanyiBot.Plan(c, s, [18]);
        Assert.DoesNotContain(cut, again.Zero);
        Assert.True(again.Need >= plan.Need);
        Assert.Empty(again.Dead);
    }

    [Fact]
    public void Glek_blocks_two_networks_about_to_meet_but_only_twice_and_with_enough_wagons()
    {
        var c = Playing(3, 5);
        var s = c.Turn;
        var o = c.Next(s);
        foreach (var x in c.Order)
        {
            c.Tickets[x].Clear();
            c.Hand[x] = new int[9];
        }
        // Суперник: Київ — Ніжин — Чернігів — Конотоп і Полтава — Опішня — Харків, зв'язок між ними — Конотоп — Опішня; планів у Глека нема.
        foreach (var r in new[] { 1, 2, 3, 9, 10 })
        {
            c.Owner[r] = o;
            c.Taken[o].Add(r);
        }
        c.Hand[s][Wild] = 6;
        c.TicketDeck.Clear();
        var m = SolyanyiBot.Decide(c, s);
        Assert.Equal("claim", m.Act);
        Assert.True(m.Block);
        Assert.Equal(o, m.Victim);
        Assert.Equal(87, m.Route);   // Конотоп — Опішня: 3 вози зв'язку
        c.Blocks[s] = 2;
        Assert.False(SolyanyiBot.Decide(c, s).Block);
        c.Blocks[s] = 0;
        c.Wagons[s] = 9;
        Assert.False(SolyanyiBot.Decide(c, s).Block);
    }

    [Fact]
    public void Last_turn_prefers_the_route_that_joins_a_ticket()
    {
        var c = Playing(3, 5);
        var s = c.Turn;
        foreach (var x in c.Order) c.Tickets[x].Clear();
        c.Hand[s] = new int[9];
        c.Hand[s][Wild] = 6;
        c.Last = true;
        c.Left = 3;
        // Без замовлень — найдорожчий шлях, який можна оплатити (6 волів — 6 возів, Київ — Полтава, менший id).
        Assert.Equal(37, SolyanyiBot.Decide(c, s).Route);
        // Є замовлення Коломия — Хмельницький, якому бракує лише Кам'янець — Хмельницький: шлях із ×2 ⭐ важить більше за очки.
        foreach (var r in new[] { 77, 68 })
        {
            c.Owner[r] = s;
            c.Taken[s].Add(r);
        }
        c.Tickets[s].Add(38);
        Assert.Equal(37, SolyanyiBot.Decide(c, s).Route);   // 15 ⭐ за шлях проти 2 + 2 · 4
        c.Hand[s][Wild] = 2;
        Assert.Equal(67, SolyanyiBot.Decide(c, s).Route);   // на 2 воли: 2 + 8
    }

    [Fact]
    public void Keep_picks_at_least_the_minimum_and_breaks_ties_by_fewer_cards()
    {
        var c = Core(2, 5);
        var s = c.First;
        c.Offer[s] = [38, 39, 0];
        var ids = SolyanyiBot.Keep(c, s);
        Assert.True(ids.Length >= 2);
        Assert.All(ids, id => Assert.Contains(id, c.Offer[s]));
        // Посеред партії досить одного, і з малою кількістю возів Глек не бере довгих.
        var p = Playing(2, 5);
        var t = p.Turn;
        p.Tickets[t].Clear();
        p.Wagons[t] = 8;
        Must(p.TakeTickets(t), "tickets");
        p.Offer[t] = [0, 1, 38];
        var low = SolyanyiBot.Keep(p, t);
        Assert.Equal([38], low);
    }
}
