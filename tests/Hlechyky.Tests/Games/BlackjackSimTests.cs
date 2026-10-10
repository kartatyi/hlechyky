using Hlechyky.Games.Impl;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Математика «Двадцять одно в Глека» (specs/blackjack.md §2.6): мільйони роздач тим самим кодом, що грає за столом
/// (<see cref="BjDeal"/>), гравець ходить базовою стратегією (<see cref="BlackjackStrategy"/>), колода — 6 колод, нова на
/// кожну роздачу. Цифри — у таблицю spec; тест друкує їх (dotnet test … --logger "console;verbosity=detailed").
/// Більше роздач — змінна середовища BJ_SIM_ROUNDS (напр. 100000000).
/// </summary>
public class BlackjackSimTests(ITestOutputHelper output)
{
    public sealed record Sim(long Rounds, double Rtp, double RtpWagered, double Win, double Push, double Lose, double Naturals,
        double Doubles, double Splits, double Five21, int MaxReturn, double Sd);

    /// <summary>Роздачі з одним боксом (ставка 1): RTP на початкову ставку й на все поставлене (з подвоєннями й сплітами).</summary>
    public static Sim Simulate(long rounds, int seed)
    {
        var shoe = new BlackjackShoe(new Random(seed));
        Func<string> draw = shoe.Next;
        long wagered = 0, returned = 0, win = 0, push = 0, lose = 0, nat = 0, dbl = 0, split = 0, five = 0;
        double sq = 0;
        var max = 0;
        (string, int, int)[] box = [("гравець", 0, 1)];
        for (long i = 0; i < rounds; i++)
        {
            shoe.Reset();
            var d = BjDeal.Start(box, draw);
            while (!d.PlayersDone)
            {
                var move = BlackjackStrategy.Move(d) ?? BjDeal.Stand;
                if (move == BjDeal.Double) dbl++;
                if (move == BjDeal.Split) split++;
                var err = d.Act(move, draw);
                if (err is not null) throw new InvalidOperationException(err);
            }
            d.DealerPlays(draw);
            d.Settle();
            var b = d.Boxes[0];
            wagered += b.Staked;
            returned += b.Returned;
            var net = b.Returned - b.Staked;
            sq += (double)net * net;
            if (net > 0) win++;
            else if (net == 0) push++;
            else lose++;
            if (b.Hands[0].IsNatural) nat++;
            if (b.Hands.Any(h => h.Cards.Count >= 5 && BlackjackCore.Total(h.Cards) == 21)) five++;
            max = Math.Max(max, b.Returned);
        }
        double n = rounds;
        var mean = (returned - wagered) / n;
        return new Sim(rounds, returned / n - (wagered - n) / n, (double)returned / wagered, win / n, push / n, lose / n, nat / n,
            dbl / n, split / n, five / n, max, Math.Sqrt(sq / n - mean * mean));
    }

    void Print(string head, Sim s)
    {
        output.WriteLine($"{head}: {s.Rounds:N0} роздач");
        output.WriteLine($"  RTP на початкову ставку {s.Rtp:P3} (±{3 * s.Sd / Math.Sqrt(s.Rounds):P3} на 3σ), на все поставлене {s.RtpWagered:P3}");
        output.WriteLine($"  виграш {s.Win:P2}, нічия {s.Push:P2}, програш {s.Lose:P2}; блекджек {s.Naturals:P2}; подвоєння {s.Doubles:P2}, спліти {s.Splits:P2}");
        output.WriteLine($"  21 з п'яти карт і більше: {s.Five21:P3} (1 з {1 / Math.Max(1e-9, s.Five21):N0}); найбільше повернення {s.MaxReturn}× ставки; σ роздачі {s.Sd:F3}");
    }

    [Fact]
    public void Basic_strategy_rtp_is_between_96_5_and_97_percent()
    {
        var rounds = long.TryParse(Environment.GetEnvironmentVariable("BJ_SIM_ROUNDS"), out var r) && r > 0 ? r : 3_000_000;
        var s = Simulate(rounds, 20261010);
        Print("Двадцять одно, базова стратегія", s);
        Assert.InRange(s.Rtp, 0.965, 0.970);
        Assert.True(s.RtpWagered <= 0.975);
        // стеля: три руки, кожна виграла (подвоєння після спліту нема) — або одна подвоєна
        Assert.True(s.MaxReturn <= 6);
    }
}
