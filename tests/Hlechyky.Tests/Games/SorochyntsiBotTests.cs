using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;
using static Hlechyky.Games.Impl.SorochyntsiCards;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Глек-гравець «Сорочинського ярмарку» (контракт §8.1, §14): 10 000 партій лише Глеками без жодного незаконного кроку
/// й з інваріантами після кожного ходу, булава й медіана кіл, детермінізм і те, що Глек не підглядає (колоди й чужа
/// бронь наосліп не міняють його ходу).
/// </summary>
public class SorochyntsiBotTests(ITestOutputHelper output)
{
    /// <summary>Підсумок однієї партії Глеків.</summary>
    internal sealed record Game(int Players, int Rounds, string Reason, int[] Winners, int[] TurnPos, int Turns);

    /// <summary>Ядро на <paramref name="players"/> місць 0..n−1 з розкладом із сіду.</summary>
    internal static SorochyntsiCore Core(int players, int seed, int target = 16)
    {
        var c = new SorochyntsiCore(Enumerable.Range(0, players)) { Target = target };
        c.Begin(new Random(seed));
        return c;
    }

    /// <summary>Інваріанти §14: жетони банк + руки = початок; 90 = колоди + ряди + куплені + броня; ⭐ і знижки з нуля.</summary>
    internal static void Invariants(SorochyntsiCore c)
    {
        for (var i = 0; i < 6; i++)
        {
            var sum = c.Bank[i] + c.Order.Sum(s => c.Tok[s][i]);
            if (sum != c.Bank0[i]) throw new Xunit.Sdk.XunitException($"жетон {Tokens[i]}: {sum} ≠ {c.Bank0[i]}");
            if (c.Bank[i] < 0 || c.Order.Any(s => c.Tok[s][i] < 0)) throw new Xunit.Sdk.XunitException("від'ємні жетони");
        }
        var all = new List<int>();
        foreach (var d in c.Decks) all.AddRange(d);
        foreach (var r in c.Rows) all.AddRange(r.Where(id => id >= 0));
        foreach (var s in c.Order)
        {
            all.AddRange(c.Cards[s]);
            all.AddRange(c.Reserved[s].Select(r => r.Id));
        }
        if (all.Count != 90 || all.Distinct().Count() != 90) throw new Xunit.Sdk.XunitException($"крамниць {all.Count} / {all.Distinct().Count()}");
        foreach (var s in c.Order)
        {
            if (c.Reserved[s].Count > ReserveMax) throw new Xunit.Sdk.XunitException("броні понад 3");
            var b = new int[6];
            var pts = 0;
            var icons = 0;
            foreach (var id in c.Cards[s])
            {
                b[Deck[id].Bonus]++;
                pts += Deck[id].Pts;
                icons += Deck[id].K;
            }
            pts += 3 * c.GuestsOf[s].Count + (c.Starshyna == s ? 3 : 0);
            if (!b.SequenceEqual(c.Bonus[s]) || pts != c.Points(s) || icons != c.Icons[s])
                throw new Xunit.Sdk.XunitException($"місце {s}: знижки/очки не сходяться");
            if (c.Phase == SoPhase.Play && c.NTok(s) > HandMax) throw new Xunit.Sdk.XunitException($"місце {s}: {c.NTok(s)} жетонів");
        }
    }

    /// <summary>Дограти партію лише Глеками; кожен крок — законний (помилка — виняток), інваріанти після кожного ходу.</summary>
    internal static Game Play(SorochyntsiCore c, bool check = true)
    {
        var guard = 0;
        while (!c.Over)
        {
            var s = c.Turn;
            var turns = c.Turns;
            while (!c.Over && c.Turn == s && c.Turns == turns)
            {
                var m = SorochyntsiBot.Decide(c, s);
                var err = SorochyntsiBot.Apply(c, s, m);
                if (err is not null) throw new Xunit.Sdk.XunitException($"незаконний крок {m.Act}: {err}");
                if (++guard > 5000) throw new Xunit.Sdk.XunitException("партія не кінчається");
            }
            if (check) Invariants(c);
        }
        var pos = c.Winners.Select(w => (Array.IndexOf(c.Order, w) - Array.IndexOf(c.Order, c.First) + c.Seats) % c.Seats).ToArray();
        return new Game(c.Seats, c.Timeline.Count, c.Reason, c.Winners, pos, c.Turns);
    }

    static double Median(List<int> a)
    {
        var s = a.Order().ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2.0;
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Ten_thousand_bot_games_are_legal_finish_by_bulava_and_take_at_most_thirty_rounds()
    {
        var sw = Stopwatch.StartNew();
        var games = new List<Game>();
        for (var seed = 1; seed <= 10_000; seed++) games.Add(Play(Core(2 + seed % 3, seed)));
        sw.Stop();
        foreach (var n in new[] { 2, 3, 4 })
        {
            var g = games.Where(x => x.Players == n).ToList();
            var bulava = g.Count(x => x.Reason == "bulava") / (double)g.Count;
            var med = Median([.. g.Select(x => x.Rounds)]);
            var win = new double[n];
            foreach (var x in g) foreach (var p in x.TurnPos) win[p] += 1.0 / x.TurnPos.Length;
            output.WriteLine($"{n} гравці: {g.Count} партій, булава {bulava:P1}, медіана кіл {med}, макс {g.Max(x => x.Rounds)}, "
                + $"stall {g.Count(x => x.Reason == "stall")}, rounds {g.Count(x => x.Reason == "rounds")}, "
                + $"перемоги за чергою [{string.Join(", ", win.Select(w => (w / g.Count).ToString("P1")))}]");
            Assert.True(bulava >= 0.95, $"{n}: булавою лише {bulava:P1}");
            Assert.True(med <= 30, $"{n}: медіана {med} кіл");
            Assert.All(g, x => Assert.True(x.Rounds <= MaxRounds));
        }
        output.WriteLine($"час: {sw.Elapsed.TotalSeconds:F1} с");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"10 000 партій — {sw.Elapsed.TotalSeconds:F1} с");
    }
}

/// <summary>Глек через кімнату, детермінізм і «не підглядає».</summary>
public class SorochyntsiBotRoomTests
{
    [Fact]
    public void Same_seed_plays_the_same_game()
    {
        var a = SorochyntsiBotTests.Core(3, 77);
        var b = SorochyntsiBotTests.Core(3, 77);
        SorochyntsiBotTests.Play(a);
        SorochyntsiBotTests.Play(b);
        Assert.Equal(SoRig.State(a), SoRig.State(b));
    }

    [Fact]
    public void Bot_does_not_peek_at_deck_order_or_someone_elses_blind_reserve()
    {
        var checkedMoves = 0;
        foreach (var seed in new[] { 3, 4, 5, 6 })
        {
            var c = SorochyntsiBotTests.Core(2 + seed % 3, seed);
            var rng = new Random(seed);
            for (var turn = 0; turn < 400 && !c.Over; turn++)
            {
                var s = c.Turn;
                // Та сама публічна картина: колоди переставлено, чужа бронь наосліп — інші карти з колод.
                var alt = c.Clone();
                foreach (var d in alt.Decks)
                    for (var i = d.Count - 1; i > 0; i--)
                    {
                        var j = rng.Next(i + 1);
                        (d[i], d[j]) = (d[j], d[i]);
                    }
                foreach (var o in alt.Order.Where(o => o != s))
                    for (var k = 0; k < alt.Reserved[o].Count; k++)
                    {
                        var r = alt.Reserved[o][k];
                        var deck = alt.Decks[Deck[r.Id].Tier - 1];
                        if (!r.Blind || deck.Count == 0) continue;
                        (alt.Reserved[o][k], deck[0]) = (new SoRes(deck[0], true), r.Id);
                    }
                Assert.Equal(Show(SorochyntsiBot.Decide(c, s)), Show(SorochyntsiBot.Decide(alt, s)));
                checkedMoves++;
                Assert.Null(SorochyntsiBot.Apply(c, s, SorochyntsiBot.Decide(c, s)));
            }
        }
        Assert.True(checkedMoves > 200, $"перевірено лише {checkedMoves} ходів");
    }

    static string Show(SoMove m) =>
        $"{m.Act}|{string.Join(",", m.Got ?? [])}|{m.Card}|{m.Tier}|{string.Join(",", m.Give ?? [])}|{m.Guest}";

    [Fact]
    public void Blind_reserves_happen_and_stay_hidden_from_the_bots_eyes()
    {
        // За 300 партій Глеки бронюють і наосліп (з колоди III, коли цвіту не видно), і з ряду.
        var blind = 0;
        var row = 0;
        for (var seed = 1; seed <= 300; seed++)
        {
            var c = SorochyntsiBotTests.Core(2 + seed % 3, seed);
            SorochyntsiBotTests.Play(c, check: false);
            blind += c.Blinds.Sum();
            row += c.Reserves.Sum() - c.Blinds.Sum();
        }
        Assert.True(blind > 0 && row > 0, $"наосліп {blind}, з ряду {row}");
    }

    [Fact]
    public void A_person_following_the_tips_plays_legal_moves_to_the_end_with_three_bots()
    {
        foreach (var seed in new[] { 31, 32 })
        {
            var h = SoRig.Table(1, bots: "3", seed: seed);
            for (var i = 0; i < 3000 && h.Finished.Count == 0; i++) SoRig.Step(h, 0);
            Assert.Single(h.Finished);
            SorochyntsiBotTests.Invariants(SoRig.CoreOf(h));
        }
    }

    [Fact]
    public void Bot_waits_its_think_time_and_plays_the_whole_turn_at_once()
    {
        var h = SoRig.Table(1, bots: "1", seed: 2);
        var c = SoRig.CoreOf(h);
        if (c.Turn == 0) SoRig.Step(h, 0);
        Assert.Equal(1, c.Turn);
        var v = h.View(0);
        Assert.Equal(BoardBots.ThinkMs, v.GetProperty("botIn").GetInt32());
        Assert.Equal("Глек ще думає", h.Act(0, BoardBots.Nudge).Message);
        h.Clock.AdvanceMs(BoardBots.ThinkMs);
        // Глек на межі десяти: дія, повернення й гість — одним поштовхом.
        SoRig.Hand(c, 1, 2, 2, 2, 2, 2);
        Assert.True(h.Act(0, BoardBots.Nudge).Ok);
        Assert.Equal(0, c.Turn);
        Assert.Equal(SoPhase.Play, c.Phase);
        Assert.True(c.NTok(1) <= HandMax);
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("botIn").ValueKind);
        Assert.Equal("Глекам нема що робити", h.Act(0, BoardBots.Nudge).Message);
    }
}
