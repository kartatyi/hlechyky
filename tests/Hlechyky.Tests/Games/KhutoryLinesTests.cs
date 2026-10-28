using System.Text.RegularExpressions;
using Hlechyky.Games.Impl;
using Xunit;

namespace Hlechyky.Tests.Games;

/// <summary>Книга реплік Глека-ведучого «Хуторів» і запобіжник від спаму.</summary>
public class KhutoryLinesTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    // Найдовші можливі значення: нік на 20 символів, найдовші форми ресурсів, двоцифрові числа.
    static Dictionary<string, string> LongVars() => new()
    {
        ["who"] = "ДуженьДовгийНікГравц", ["other"] = "ІншийДуженьДовгийНік", ["victim"] = "ПограбованийГравець1",
        ["res"] = "залізо", ["resGen"] = "заліза", ["resAcc"] = "залізо", ["n"] = "12", ["points"] = "12",
    };

    static Dictionary<string, string> Vars(string who) => new(LongVars()) { ["who"] = who };

    public static IEnumerable<object[]> Moments() => Enum.GetValues<KhMoment>().Select(m => new object[] { m });

    [Theory, MemberData(nameof(Moments))]
    public void Each_moment_has_at_least_six_distinct_lines(KhMoment m)
    {
        var lines = KhutoryLines.Lines(m);
        Assert.InRange(lines.Count, 6, 12);
        Assert.Equal(lines.Count, lines.Distinct().Count());
    }

    [Theory, MemberData(nameof(Moments))]
    public void Lines_fit_140_with_long_nick_and_have_only_known_slots(KhMoment m)
    {
        foreach (var t in KhutoryLines.Lines(m))
        {
            foreach (Match x in Regex.Matches(t, @"\{(\w+)\}"))
                Assert.Contains(x.Groups[1].Value, KhutoryLines.Known);
            var s = KhutoryLines.Fill(t, LongVars());
            Assert.True(s.Length <= 140, $"{s.Length}: {s}");
            Assert.DoesNotContain("{", s);
            Assert.DoesNotContain("}", s);
            // Без підстановок — м'які заглушки, теж без дужок.
            Assert.DoesNotContain("{", KhutoryLines.Fill(t, null));
        }
    }

    [Fact]
    public void Lines_with_names_actually_use_the_name()
    {
        foreach (var m in new[] { KhMoment.Win, KhMoment.Longest, KhMoment.Army, KhMoment.Away, KhMoment.Back, KhMoment.FirstCity })
            Assert.All(KhutoryLines.Lines(m), t => Assert.Contains("{who}", t));
        Assert.All(KhutoryLines.Lines(KhMoment.Steal), t => Assert.Contains("{victim}", t));
        Assert.All(KhutoryLines.Lines(KhMoment.PlayerTrade), t => Assert.Contains("{other}", t));
        Assert.All(KhutoryLines.Lines(KhMoment.BankShort), t => Assert.Contains("{resGen}", t));
        Assert.All(KhutoryLines.Lines(KhMoment.BankTrade), t => Assert.DoesNotContain("{", t));
    }

    [Fact]
    public void ResVars_gives_ready_cases()
    {
        var c = KhutoryLines.ResVars("clay");
        Assert.Equal(("глина", "глини", "глину"), (c["res"], c["resGen"], c["resAcc"]));
        Assert.Equal("заліза", KhutoryLines.ResVars("ore")["resGen"]);
        Assert.Equal("добра", KhutoryLines.ResVars("???")["resGen"]);
    }

    [Fact]
    public void Line_is_deterministic_by_seed()
    {
        var a = Enumerable.Range(0, 50).Select(i => KhutoryLines.Line((KhMoment)(i % 23), new Random(7 + i), LongVars()));
        var b = Enumerable.Range(0, 50).Select(i => KhutoryLines.Line((KhMoment)(i % 23), new Random(7 + i), LongVars()));
        Assert.Equal(a, b);

        string Run(int seed)
        {
            var g = new KhutoryGlek();
            var rng = new Random(seed);
            var outp = new List<string?>();
            for (var i = 0; i < 200; i++)
            {
                if (i % 3 == 0) g.NewTurn();
                outp.Add(g.Say((KhMoment)(i % 23), T0.AddSeconds(i * 7), rng, LongVars()));
            }
            return string.Join("|", outp);
        }
        Assert.Equal(Run(5), Run(5));
    }

    [Fact]
    public void Always_moments_always_speak()
    {
        var g = new KhutoryGlek();
        var rng = new Random(1);
        Assert.NotNull(g.Say(KhMoment.Start, T0, rng, LongVars()));
        foreach (var m in new[] { KhMoment.Win, KhMoment.Longest, KhMoment.LongestLost, KhMoment.Army,
                     KhMoment.Away, KhMoment.Back, KhMoment.SevenAgain })
            for (var i = 0; i < 5; i++)
                Assert.NotNull(g.Say(m, T0, rng, LongVars())); // той самий хід, та сама секунда
    }

    [Fact]
    public void First_seven_always_then_general_rule()
    {
        var g = new KhutoryGlek();
        var rng = new Random(2);
        g.Say(KhMoment.Start, T0, rng, LongVars());
        Assert.NotNull(g.Say(KhMoment.Seven, T0.AddSeconds(1), rng)); // перша — хоч і за секунду після Start
        g.NewTurn();
        Assert.Null(g.Say(KhMoment.Seven, T0.AddSeconds(10), rng)); // друга — під кулдаун
        g.NewTurn();
        Assert.NotNull(g.Say(KhMoment.Seven, T0.AddSeconds(40), rng));
        // Нова партія — перша сімка знову гарантована.
        g.Say(KhMoment.Start, T0.AddSeconds(41), rng, LongVars());
        Assert.NotNull(g.Say(KhMoment.Seven, T0.AddSeconds(42), rng));
    }

    [Fact]
    public void Common_moments_respect_cooldown_and_one_per_turn()
    {
        var g = new KhutoryGlek();
        var rng = new Random(3);
        Assert.NotNull(g.Say(KhMoment.FirstCity, T0, rng, LongVars()));
        g.NewTurn();
        Assert.Null(g.Say(KhMoment.Drought, T0.AddSeconds(24), rng));          // < 25 с
        Assert.NotNull(g.Say(KhMoment.Drought, T0.AddSeconds(25), rng));       // рівно 25 с — можна
        Assert.Null(g.Say(KhMoment.Toloka, T0.AddSeconds(100), rng, LongVars())); // той самий хід
        g.NewTurn();
        Assert.NotNull(g.Say(KhMoment.Toloka, T0.AddSeconds(100), rng, LongVars()));

        // «Завжди»-рядок теж займає хід і скидає кулдаун.
        g.NewTurn();
        Assert.NotNull(g.Say(KhMoment.Army, T0.AddSeconds(200), rng, LongVars()));
        Assert.Null(g.Say(KhMoment.Plenty, T0.AddSeconds(300), rng, LongVars()));
    }

    [Fact]
    public void Common_moments_never_closer_than_25s_in_a_long_run()
    {
        var g = new KhutoryGlek();
        var rng = new Random(4);
        var common = Enum.GetValues<KhMoment>().Where(m => m is not (KhMoment.Win or KhMoment.Start or KhMoment.Longest
            or KhMoment.LongestLost or KhMoment.Army or KhMoment.Away or KhMoment.Back or KhMoment.SevenAgain or KhMoment.Seven)).ToArray();
        DateTimeOffset? last = null;
        var said = 0;
        for (var i = 0; i < 2000; i++)
        {
            if (i % 4 == 0) g.NewTurn();
            var now = T0.AddSeconds(i * 3);
            var s = g.Say(common[rng.Next(common.Length)], now, rng, Vars("Гравець" + (i % 4)));
            if (s == null) continue;
            if (last != null) Assert.True(now - last >= KhutoryGlek.Cooldown);
            last = now;
            said++;
        }
        Assert.InRange(said, 50, 2000 * 3 / 25 + 1);
    }

    [Fact]
    public void Same_line_never_twice_in_a_row_for_a_moment()
    {
        var g = new KhutoryGlek();
        var rng = new Random(5);
        foreach (var m in Enum.GetValues<KhMoment>())
        {
            string? prev = null;
            var n = 0;
            for (var i = 0; n < 40 && i < 1000; i++)
            {
                g.NewTurn();
                var s = g.Say(m, T0.AddMinutes(i + 1000 * (int)m), rng, LongVars());
                if (s == null) continue;
                Assert.NotEqual(prev, s);
                prev = s;
                n++;
            }
        }
    }

    [Fact]
    public void Chance_moments_are_sometimes_silent_and_nearwin_once_per_player()
    {
        var g = new KhutoryGlek();
        var rng = new Random(6);
        var hits = 0;
        for (var i = 0; i < 500; i++)
        {
            g.NewTurn();
            if (g.Say(KhMoment.Harvest, T0.AddMinutes(i), rng, LongVars()) != null) hits++;
        }
        Assert.InRange(hits, 140, 260); // ~40 %

        g.NewTurn();
        Assert.NotNull(g.Say(KhMoment.NearWin, T0.AddHours(20), rng, Vars("Оля")));
        g.NewTurn();
        Assert.Null(g.Say(KhMoment.NearWin, T0.AddHours(21), rng, Vars("Оля")));
        g.NewTurn();
        Assert.NotNull(g.Say(KhMoment.NearWin, T0.AddHours(22), rng, Vars("Петро")));
    }
}
