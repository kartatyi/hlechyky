using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Xunit;

namespace Hlechyky.Tests.Games;

public class PokerCoreTests
{
    /// <summary>
    /// Колода під задані карти: роздача йде по одній, починаючи ліворуч від кнопки, двічі; далі дошка (без спалювання).
    /// </summary>
    internal static int[] Deck(int n, int button, bool[] dealt, Dictionary<int, string> holes, string board = "")
    {
        var order = new List<int>();
        for (var i = 1; i <= n; i++) { var p = (button + i) % n; if (dealt[p]) order.Add(p); }
        var deck = new List<int>();
        for (var r = 0; r < 2; r++)
            foreach (var p in order) deck.Add(holes.TryGetValue(p, out var h) ? PokerHand.ParseMany(h)[r] : -1);
        deck.AddRange(PokerHand.ParseMany(board));
        var rest = new Queue<int>(Enumerable.Range(0, 52).Where(c => !deck.Contains(c)));
        for (var i = 0; i < deck.Count; i++) if (deck[i] < 0) deck[i] = rest.Dequeue();
        while (rest.Count > 0) deck.Add(rest.Dequeue());
        return [.. deck];
    }

    static PokerCore Core(params int[] stacks)
    {
        var c = new PokerCore(stacks.Length);
        for (var i = 0; i < stacks.Length; i++) c.Stack[i] = stacks[i];
        return c;
    }

    static bool[] All(int n) => Enumerable.Repeat(true, n).ToArray();

    static void Deal(PokerCore c, int button, Dictionary<int, string>? holes = null, string board = "", int sb = 10, int bb = 20)
    {
        var dealt = Enumerable.Range(0, c.N).Select(p => c.Stack[p] > 0).ToArray();
        c.StartHand(dealt, button, sb, bb, Deck(c.N, button, dealt, holes ?? [], board));
    }

    [Fact]
    public void HeadsUpButtonIsSmallBlindAndActsFirstPreflopSecondAfter()
    {
        var c = Core(1000, 1000);
        Deal(c, 0);
        Assert.Equal(0, c.SbPos);
        Assert.Equal(1, c.BbPos);
        Assert.Equal(0, c.Turn);
        Assert.Equal(990, c.Stack[0]);
        Assert.Equal(980, c.Stack[1]);
        c.Act(0, "call");
        Assert.Equal(1, c.Turn);   // у великого — опція
        c.Act(1, "check");
        Assert.Equal(1, c.Street);
        Assert.Equal(3, c.Board.Count);
        Assert.Equal(1, c.Turn);   // постфлоп кнопка ходить другою
    }

    [Fact]
    public void ThreeWayOrder()
    {
        var c = Core(1000, 1000, 1000);
        Deal(c, 0);
        Assert.Equal((1, 2), (c.SbPos, c.BbPos));
        Assert.Equal(0, c.Turn);    // після великого — кнопка (UTG утрьох)
        c.Act(0, "call");
        c.Act(1, "call");
        Assert.Equal(2, c.Turn);
        c.Act(2, "check");
        Assert.Equal(1, c.Street);
        Assert.Equal(1, c.Turn);    // постфлоп — перший живий після кнопки
    }

    [Fact]
    public void MinRaiseIsLastRaiseSize()
    {
        var c = Core(1000, 1000, 1000);
        Deal(c, 0);
        Assert.Equal(40, c.Legal(0)!.RaiseMin);
        var ex = Assert.Throws<GameError>(() => c.Act(0, "raise", 30));
        Assert.Contains("40", ex.Message);
        c.Act(0, "raise", 100);           // рейз на 80
        Assert.Equal(180, c.Legal(1)!.RaiseMin);
        Assert.Equal(1000, c.Legal(1)!.RaiseMax);
        c.Act(1, "raise", 300);           // на 200
        Assert.Equal(500, c.Legal(2)!.RaiseMin);
        Assert.Throws<GameError>(() => c.Act(2, "raise", 1200));
        Assert.Throws<GameError>(() => c.Act(1, "fold"));   // не його хід
    }

    [Fact]
    public void ShortAllInDoesNotReopenBetting()
    {
        // A кнопка, B малий (140 фішок), C великий
        var c = Core(1000, 140, 1000);
        Deal(c, 0);
        c.Act(0, "raise", 100);          // рейз на 80
        c.Act(1, "allin");               // 140: доставка 40 < 80 — неповний
        Assert.Equal(140, c.CurrentBet);
        Assert.True(c.Legal(2)!.RaiseMax > 0);   // великий ще не ходив — може рейзити
        c.Act(2, "call");
        var a = c.Legal(0)!;
        Assert.Equal(0, a.RaiseMax);     // A вже ходив: лише колл або фолд
        Assert.Equal(40, a.Call);
        Assert.Throws<GameError>(() => c.Act(0, "raise", 300));
        c.Act(0, "call");
        Assert.Equal(1, c.Street);
    }

    [Fact]
    public void AllInWhenRaiseIsClosedIsJustACall()
    {
        // A кнопка, B малий, C великий зі стеком 15: A і B коллять, C — неповний олл-ін до 15 (торгів не відкриває)
        var c = Core(1000, 1000, 15);
        Deal(c, 0, sb: 5, bb: 10);
        c.Act(0, "call");
        c.Act(1, "call");
        c.Act(2, "allin");
        Assert.Equal(15, c.CurrentBet);
        Assert.Equal(0, c.Legal(0)!.RaiseMax);
        c.Act(0, "allin");               // рейзити не можна — «олл-ін» = колл 15, а не весь стек
        c.Act(1, "allin");
        Assert.Equal(985, c.Stack[0]);
        Assert.Equal(985, c.Stack[1]);
        Assert.False(c.Runout);
        Assert.Equal(1, c.Street);
    }

    [Fact]
    public void FullAllInRaiseReopens()
    {
        var c = Core(1000, 300, 1000);
        Deal(c, 0);
        c.Act(0, "raise", 100);
        c.Act(1, "allin");              // 300: доставка 200 ≥ 80 — повний
        c.Act(2, "call");
        Assert.True(c.Legal(0)!.RaiseMax > 0);
    }

    [Fact]
    public void SidePotsWithFourAllInsAndUncalledReturn()
    {
        // 0 — кнопка; стеки 100/200/300/1000; найкоротший має найкращу руку, далі за спаданням
        var c = Core(100, 200, 300, 1000);
        var holes = new Dictionary<int, string> { [0] = "Ac Ad", [1] = "Kc Kd", [2] = "Qc Qd", [3] = "7h 2s" };
        Deal(c, 0, holes, "3h 4d 8s 9c Jd");
        var chips = c.Chips;
        // UTG = 3 (після великого, 2)
        c.Act(3, "allin");
        c.Act(0, "allin");
        c.Act(1, "allin");
        c.Act(2, "allin");
        Assert.True(c.Runout);
        Assert.Equal(700, c.Stack[3]);           // невикликане повернуто: 1000 − 300
        var pots = c.Pots(false);
        Assert.Equal(new[] { 400, 300, 200 }, pots.Select(p => p.Amount));
        Assert.Equal(new[] { 0, 1, 2, 3 }, pots[0].Seats);
        Assert.Equal(new[] { 1, 2, 3 }, pots[1].Seats);
        c.RunoutAll();
        Assert.False(c.Live);
        Assert.Equal(new[] { 400, 300, 200, 700 }, c.Stack);
        Assert.Equal(chips, c.Chips);
        Assert.Equal(3, c.Wins.Count);
        Assert.All(c.Wins, w => Assert.True(w.Showdown));
    }

    [Fact]
    public void SplitPotOddChipGoesLeftOfButton()
    {
        // дошка — роял: усі, хто дійшов, ділять. Кнопка 0, малий 1 (скидає), великий 2.
        var c = Core(1000, 1000, 1000);
        Deal(c, 0, new() { [0] = "2c 3d", [1] = "4c 5d", [2] = "6c 7d" }, "As Ks Qs Js Ts", sb: 5, bb: 10);
        c.Act(0, "call");
        c.Act(1, "fold");     // 5 у банку
        c.Act(2, "check");
        for (var s = 0; s < 3; s++) { c.Act(2, "check"); c.Act(0, "check"); }
        Assert.False(c.Live);
        var pot = Assert.Single(c.Wins);
        Assert.Equal(25, pot.Amount);
        Assert.Equal(new[] { 2, 0 }, pot.Winners);   // перший ліворуч від кнопки — 2 (1 скинув)
        Assert.Equal(new[] { 13, 12 }, pot.Shares);
        Assert.Equal(1003, c.Stack[2]);
        Assert.Equal(1002, c.Stack[0]);
    }

    [Fact]
    public void FoldWinsUncontestedWithUncalledBack()
    {
        var c = Core(1000, 1000);
        Deal(c, 0);
        c.Act(0, "raise", 600);
        c.Act(1, "fold");
        Assert.False(c.Live);
        Assert.Equal(1020, c.Stack[0]);
        Assert.Equal(980, c.Stack[1]);
        Assert.False(Assert.Single(c.Wins).Showdown);
    }

    [Fact]
    public void BiggerShoveGetsExcessBackAtOnce()
    {
        var c = Core(1000, 300);
        Deal(c, 0);
        c.Act(0, "allin");
        c.Act(1, "call");
        Assert.True(c.Runout);
        Assert.True(c.ShowAll);
        Assert.Equal(700, c.Stack[0]);   // 1000 − 300 повернуто ще до дошки
        Assert.Equal(600, c.Total.Sum());
        Assert.False(c.RunoutStep());
        Assert.Equal(3, c.Board.Count);
        Assert.False(c.RunoutStep());
        Assert.False(c.RunoutStep());
        Assert.Equal(5, c.Board.Count);
        Assert.True(c.RunoutStep());
        Assert.False(c.Live);
        Assert.Equal(1300, c.Stack.Sum());
    }

    [Fact]
    public void ShortBlindIsAllIn()
    {
        var c = Core(1000, 5);
        Deal(c, 1);   // 1 — кнопка й малий, у нього 5 < 10
        Assert.True(c.AllIn[1]);
        Assert.Equal(0, c.Turn);         // великий має опцію (рейзити нема кому)
        Assert.Equal(0, c.Legal(0)!.RaiseMax);
        c.Act(0, "check");
        Assert.True(c.Runout);
        c.RunoutAll();
        Assert.Equal(1005, c.Stack.Sum());
    }

    [Fact]
    public void RandomPlayKeepsChips()
    {
        var rng = new Random(7);
        var c = Core(500, 800, 300, 1200, 50, 900);
        var total = c.Chips;
        var button = 0;
        for (var hand = 0; hand < 300; hand++)
        {
            var dealt = Enumerable.Range(0, c.N).Select(p => c.Stack[p] > 0).ToArray();
            if (dealt.Count(d => d) < 2) break;
            button = c.Next(button, p => dealt[p]);
            var deck = Enumerable.Range(0, 52).OrderBy(_ => rng.Next()).ToArray();
            c.StartHand(dealt, button, 5, 10, deck);
            var guard = 0;
            while (c.Live && guard++ < 200)
            {
                if (c.Runout) { c.RunoutStep(); continue; }
                var p = c.Turn;
                var l = c.Legal(p)!;
                var r = rng.Next(10);
                if (r < 2) c.Act(p, "fold");
                else if (r < 6) c.Act(p, l.Check ? "check" : "call");
                else if (r < 9 && l.RaiseMax > 0) c.Act(p, "raise", l.RaiseMin + rng.Next(l.RaiseMax - l.RaiseMin + 1));
                else c.Act(p, "allin");
                Assert.Equal(total, c.Chips);
            }
            Assert.False(c.Live);
            Assert.Equal(total, c.Chips);
            Assert.All(c.Stack, s => Assert.True(s >= 0));
        }
    }
}
