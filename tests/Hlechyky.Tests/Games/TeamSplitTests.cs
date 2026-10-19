using Hlechyky.Games.Impl;

namespace Hlechyky.Tests.Games;

/// <summary>Поділ на команди за силою (пакет 1, 19.10): баланс, розміри, різноманітність на «Ще раз», детермінізм, боти.</summary>
public class TeamSplitTests
{
    static double Diff(IReadOnlyList<double> s, int[] t) =>
        Math.Abs(s.Where((_, i) => t[i] == 0).Sum() - s.Where((_, i) => t[i] == 1).Sum());

    /// <summary>Ключ поділу без назв команд: хто з першим гравцем.</summary>
    static string Shape(int[] t) => string.Concat(t.Select(x => x == t[0] ? 'a' : 'b'));

    [Fact]
    public void Strong_and_weak_play_against_two_average()
    {
        double[] s = [1.0, 0, -1.0, 0];   // сильний, середній, слабкий, середній
        for (var seed = 0; seed < 20; seed++)
        {
            var t = TeamSplit.Split(s, new Random(seed));
            Assert.Equal(t[0], t[2]);
            Assert.Equal(t[1], t[3]);
            Assert.NotEqual(t[0], t[1]);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    public void Sizes_differ_by_at_most_one_and_sums_are_close_to_best(int n)
    {
        var rng = new Random(n);
        var s = Enumerable.Range(0, n).Select(_ => rng.NextDouble() - 0.5).ToArray();
        // найкраща різниця — повним перебором тут же
        var best = double.MaxValue;
        for (var m = 0; m < 1 << n; m++)
        {
            var ones = System.Numerics.BitOperations.PopCount((uint)m);
            if (Math.Abs(n - 2 * ones) > 1) continue;
            best = Math.Min(best, Diff(s, [.. Enumerable.Range(0, n).Select(i => m >> i & 1)]));
        }
        for (var seed = 0; seed < 10; seed++)
        {
            var t = TeamSplit.Split(s, new Random(seed));
            Assert.Equal(n, t.Length);
            Assert.All(t, x => Assert.InRange(x, 0, 1));
            Assert.InRange(t.Count(x => x == 0) - t.Count(x => x == 1), -1, 1);
            Assert.True(Diff(s, t) <= best + TeamSplit.Slack + 1e-9);
        }
    }

    [Fact]
    public void Rematch_changes_split_when_there_are_equal_ones()
    {
        double[] s = [0, 0, 0, 0, 0, 0];   // усі новачки: усі поділи рівноцінні
        var rng = new Random(7);
        int[]? prev = null;
        var seen = new HashSet<string>();
        for (var i = 0; i < 10; i++)
        {
            var t = TeamSplit.Split(s, rng, prev);
            if (prev is not null) Assert.NotEqual(Shape(prev), Shape(t));
            seen.Add(Shape(t));
            prev = t;
        }
        Assert.True(seen.Count >= 4, $"лише {seen.Count} різних поділів за 10 разів");
    }

    [Fact]
    public void Single_best_split_repeats_even_after_previous()
    {
        double[] s = [3, 0, -3, 0];   // різниця між поділами далеко більша за Slack — вибору нема
        var a = TeamSplit.Split(s, new Random(1));
        var b = TeamSplit.Split(s, new Random(2), a);
        Assert.Equal(Shape(a), Shape(b));
    }

    [Fact]
    public void Same_rng_gives_same_split()
    {
        double[] s = [0.2, -0.1, 0, 0.05, 0.3, -0.2, 0, 0.1];
        Assert.Equal(TeamSplit.Split(s, new Random(42)), TeamSplit.Split(s, new Random(42)));
        Assert.Equal(TeamSplit.Split(s, new Random(42), [0, 1, 0, 1, 0, 1, 0, 1]),
            TeamSplit.Split(s, new Random(42), [0, 1, 0, 1, 0, 1, 0, 1]));
    }

    [Fact]
    public void Bots_count_by_level()
    {
        Assert.Equal(-0.4, TeamSplit.BotStrength(LiveBots.Level.Easy));
        Assert.Equal(0, TeamSplit.BotStrength(LiveBots.Level.Normal));
        Assert.Equal(0.3, TeamSplit.BotStrength(LiveBots.Level.Hard));
        // сильна людина (+0,4) і легкий бот (−0,4) проти двох звичайних ботів: сильного з легким — разом
        double[] s = [0.4, TeamSplit.BotStrength(LiveBots.Level.Easy), 0, 0];
        for (var seed = 0; seed < 10; seed++)
        {
            var t = TeamSplit.Split(s, new Random(seed));
            Assert.Equal(t[0], t[1]);
        }
    }

    [Fact]
    public void Alternate_is_the_old_way()
    {
        Assert.Equal([0, 1, 0, 1, 0], TeamSplit.Alternate(5));
        Assert.Empty(TeamSplit.Split([], new Random(1)));
        Assert.Equal([0], TeamSplit.Split([0.5], new Random(1)));
    }
}
