using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Глек «Скарбу Полуботка» (<see cref="SkarbBot"/>, контракт §8.1, §14): самогра 10 000 партій на ядрі, баланс ролей,
/// кріт ніколи не відкриває скриню, бот не підглядає (чужі ролі й руки не міняють його ходу), повна партія через кімнату.
/// </summary>
[Collection(SerialPerf.Name)]
public class SkarbBotTests(ITestOutputHelper output)
{
    sealed class Tally
    {
        public int Games, Rounds, Diggers, Moles, None, MaxTurns;
        public long Turns;
        public readonly int[] RoundsBy = new int[11], DiggersBy = new int[11];
    }

    /// <summary>Ціла партія лише Глеками на ядрі. Кожен крок — з Legal; інваріанти після кожного кроку.</summary>
    static void PlayOut(int seed, int n, bool allies, Tally t)
    {
        var rng = new Random(seed);
        var c = new SkarbCore(Enumerable.Range(0, n), rng) { Allies = allies };
        c.Begin();
        var bot = new SkarbBot();
        var steps = 0;
        while (!c.Over)
        {
            if (c.Phase == SkPhase.Round)
            {
                Assert.Null(c.NextRound());
                continue;
            }
            bot.Observe(c);
            var s = c.Turn;
            var mv = bot.Decide(c, s, rng);
            var err = c.Apply(s, mv);
            if (err is not null) Assert.Fail($"сід {seed}, n {n}, раунд {c.Round}, хід {steps}: {mv} → {err}");
            var inHands = c.Order.Sum(o => c.Hand[o].Count);
            if (inHands + c.Deck.Count + c.Placed.Count + c.Discard.Count != SkarbCards.DeckSize)
                Assert.Fail($"сід {seed}: карт не 67");
            if (++steps > 400) Assert.Fail($"сід {seed}: партія не кінчається");
        }
        Assert.Equal(SkarbCards.Rounds, c.Ends.Count);
        foreach (var e in c.Ends)
        {
            t.Rounds++;
            t.RoundsBy[n]++;
            if (e.Side == "diggers")
            {
                t.Diggers++;
                t.DiggersBy[n]++;
                if (e.Mole[e.Finisher!.Value]) Assert.Fail($"сід {seed}: кріт відкрив скриню сам");
            }
            else if (e.Side == "moles") t.Moles++;
            else t.None++;
        }
        Assert.All(c.Order, o => Assert.True(c.Gold[o] >= 0));
        Assert.NotEmpty(c.Winners);
        t.Games++;
        t.Turns += steps;
        t.MaxTurns = Math.Max(t.MaxTurns, steps);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Ten_thousand_bot_games_finish_legally_and_the_sides_are_balanced()
    {
        var sw = Stopwatch.StartNew();
        var t = new Tally();
        for (var seed = 1; seed <= 10_000; seed++)
        {
            var n = 3 + (seed - 1) % 8;
            PlayOut(seed, n, allies: (seed - 1) / 8 % 2 == 1, t);
        }
        sw.Stop();
        var share = 100.0 * t.Diggers / t.Rounds;
        output.WriteLine($"{t.Games} партій за {sw.Elapsed.TotalSeconds:0.0} с, ходів у середньому {t.Turns / (double)t.Games:0.#}, найбільше {t.MaxTurns}");
        output.WriteLine($"раундів {t.Rounds}: копачі {share:0.0} %, кроти {100.0 * t.Moles / t.Rounds:0.0} %, нікому {100.0 * t.None / t.Rounds:0.0} %");
        for (var n = 3; n <= 10; n++)
            output.WriteLine($"  {n} гравців: копачі {100.0 * t.DiggersBy[n] / t.RoundsBy[n]:0.0} %");
        Assert.Equal(10_000, t.Games);
        Assert.InRange(share, 35, 75);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"10 000 партій за {sw.Elapsed.TotalSeconds:0.0} с");
    }

    /// <summary>
    /// Бот не підглядає: та сама публічна картина, але чужі ролі й руки переставлені — той самий хід (і та сама «найгірша»
    /// карта для таймауту). Скриня лежить там само: єдине, що кріт-Глек знає понад гравця, — «не відкривати її самому».
    /// </summary>
    [Fact]
    public void Same_public_picture_with_others_roles_and_hands_shuffled_gives_the_same_move()
    {
        var checks = 0;
        for (var seed = 1; seed <= 400; seed++)
        {
            var n = 3 + seed % 8;
            var rng = new Random(seed);
            var c = new SkarbCore(Enumerable.Range(0, n), rng);
            c.Begin();
            var bot = new SkarbBot();
            var stop = rng.Next(3, 60);
            for (var i = 0; i < stop && !c.Over; i++)
            {
                if (c.Phase == SkPhase.Round) { c.NextRound(); continue; }
                bot.Observe(c);
                Assert.Null(c.Apply(c.Turn, bot.Decide(c, c.Turn, rng)));
            }
            if (c.Phase != SkPhase.Play) continue;
            var s = c.Turn;
            var twin = c.Clone();
            var others = twin.Order.Where(o => o != s).ToArray();
            var shift = 1 + seed % (others.Length - 1 > 0 ? others.Length - 1 : 1);
            var roles = others.Select(o => c.Mole[o]).ToArray();
            var hands = others.Select(o => c.Hand[o].ToList()).ToArray();
            for (var i = 0; i < others.Length; i++)
            {
                twin.Mole[others[i]] = roles[(i + shift) % others.Length];
                twin.Hand[others[i]] = hands[(i + shift) % others.Length];
            }
            var a = new SkarbBot();
            var b = new SkarbBot();
            a.Observe(c);
            b.Observe(twin);
            Assert.Equal(a.Decide(c, s, new Random(seed)), b.Decide(twin, s, new Random(seed)));
            Assert.Equal(a.Decide(c, s, new Random(seed), oracle: false), b.Decide(twin, s, new Random(seed), oracle: false));
            Assert.Equal(SkarbBot.Worst(c, s), SkarbBot.Worst(twin, s));
            checks++;
        }
        Assert.True(checks > 200, $"перевірили лише {checks}");
    }

    [Fact]
    public void Tip_for_a_human_mole_never_reaches_a_goal_that_may_hide_the_chest()
    {
        var checks = 0;
        for (var seed = 1; seed <= 600; seed++)
        {
            var rng = new Random(seed);
            var c = new SkarbCore(Enumerable.Range(0, 3 + seed % 8), rng);
            c.Begin();
            var bot = new SkarbBot();
            while (c.Phase == SkPhase.Play)
            {
                bot.Observe(c);
                var s = c.Turn;
                var tip = bot.Decide(c, s, new Random(seed), oracle: false);
                if (c.Mole[s] && !tip.Pass && SkarbCore.CardOf(tip.Card).K == SkKind.Path)
                {
                    var sh = SkarbCore.CardOf(tip.Card).Shape!;
                    var p = SkarbBot.Belief(c, s);
                    var hit = c.WouldReach(tip.X, tip.Y, SkarbCards.Mask(sh, tip.R), sh.C);
                    for (var g = 0; g < 3; g++) Assert.False((hit >> g & 1) != 0 && p[g] > 0, $"сід {seed}: підказка кротові веде до мітки {g}");
                    checks++;
                }
                Assert.Null(c.Apply(s, bot.Decide(c, s, rng)));
            }
        }
        Assert.True(checks > 100);
    }

    [Fact]
    public void Room_games_from_three_to_ten_with_gleks_finish()
    {
        for (var n = 3; n <= 10; n++)
        {
            var h = new RoomHarness("skarb", new { timer = "30", bots = (n - 1).ToString(), allies = (n % 2).ToString() }, seed: 100 + n);
            h.Join("Оля");
            Assert.True(h.Start().Ok);
            Assert.Equal(n, SkarbRoom.CoreOf(h).Seats);
            for (var i = 0; i < 3000 && h.Finished.Count == 0; i++) SkarbRoom.Step(h, 0);
            Assert.Single(h.Finished);
            var v = h.View(0);
            Assert.Equal("over", v.GetProperty("phase").GetString());
            Assert.Equal(n, v.GetProperty("players").GetArrayLength());
        }
    }
}
