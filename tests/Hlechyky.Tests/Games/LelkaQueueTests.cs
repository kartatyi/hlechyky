using System.Diagnostics;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>Лічильник звернень каси до економіки й сховища: тік їх робити не мусить (рецензія M1).</summary>
public sealed class CountingStakes(IStakes inner) : IStakes
{
    public int Hits;
    public int Balance(string nick) { Hits++; return inner.Balance(nick); }
    public bool TrySpend(string nick, int amount, string reason, string refKey) { Hits++; return inner.TrySpend(nick, amount, reason, refKey); }
    public void Grant(string nick, int amount, string reason, string refKey) { Hits++; inner.Grant(nick, amount, reason, refKey); }
    public IReadOnlyList<LedgerMove>? Moves(string refPrefix) { Hits++; return inner.Moves(refPrefix); }
}

public sealed class CountingStore(IGameStore inner) : IGameStore
{
    public int Hits;
    public void SaveState(string key, string json) { Hits++; inner.SaveState(key, json); }
    public string? LoadState(string key) { Hits++; return inner.LoadState(key); }
    public void DeleteState(string key) { Hits++; inner.DeleteState(key); }
}

/// <summary>Лелека й черга каси: тік у базу не ходить, гроші — рівно раз; вимкнена Лелека не крутить порожніх раундів.</summary>
public class LelkaQueueTests
{
    [Fact]
    public void Tick_never_touches_economy_or_store_and_money_still_lands_once()
    {
        CountingStakes? stakes = null;
        CountingStore? store = null;
        var queue = new List<Action>();
        var k = new LelkaKit(s => stakes = new CountingStakes(s), s => store = new CountingStore(s), queue.Add, ("Оля", 1000), ("Петро", 1000));
        int Hits() => stakes!.Hits + store!.Hits;
        void Run()
        {
            while (queue.Count > 0)
            {
                var all = queue.ToList();
                queue.Clear();
                foreach (var a in all) a();
            }
        }
        Run();
        k.Rig(300);
        Assert.True(k.Bet(0, 100, 1.5).Ok);
        Assert.True(k.Bet(1, 100).Ok);
        var before = Hits();
        k.To(Lelka.Flight);
        Assert.Equal(before, Hits());                           // зліт: запис і списання — у черзі, не в тіку
        Assert.All(k.S.Bets, b => Assert.False(b.Taken));
        Assert.Contains("ще мить", k.H.Act(1, "cash").Message); // поки каса не відповіла, забрати не можна
        k.FlyTo(160);
        Assert.Null(k.S.Bets.First(b => b.Nick == "Оля").Out); // автозабір чекає на списання
        before = Hits();
        Run();                                                  // черга: запис раунду + списання
        Assert.Equal(900, k.Stakes.Balance("Оля"));
        Assert.Single(k.Book.Pending());
        before = Hits();
        k.H.Tick();                                             // відповідь каси — на тіку, автозабір за своїм ×1,5
        Assert.Equal(150, k.S.Bets.First(b => b.Nick == "Оля").Out);
        Assert.Equal(before, Hits());
        Assert.True(k.H.Act(1, "cash").Ok, k.H.Reply.Message);
        var petro = k.S.Bets.First(b => b.Nick == "Петро").Win;
        before = Hits();
        k.To(Lelka.Crashed);
        k.To(Lelka.Bets);                                       // падіння, пауза, новий прийом — усе без бази
        Assert.Equal(before, Hits());
        Assert.NotEmpty(queue);
        Run();                                                  // забрані, розрахунок, гаманці
        Assert.Equal(1050, k.Stakes.Balance("Оля"));
        Assert.Equal(900 + petro, k.Stakes.Balance("Петро"));
        Assert.Equal(2, k.Grants.Count);                       // по виграшу кожному, розрахунок не доплатив удруге
        Assert.Empty(k.Book.Pending());
        k.H.Tick();
        Assert.Equal(1050, k.View(0).GetProperty("wallet").GetInt32());
    }

    [Fact]
    public void Book_answer_after_the_crash_still_settles_fairly()
    {
        var queue = new List<Action>();
        var k = new LelkaKit(null, null, queue.Add, ("Оля", 1000), ("Петро", 1000), ("Іра", 50));
        void Run() { while (queue.Count > 0) { var all = queue.ToList(); queue.Clear(); foreach (var a in all) a(); } }
        Run();
        k.Rig(130);
        Assert.True(k.Bet(0, 100, 1.2).Ok);   // автозабір ≤ точки — каса заплатить ×1,2
        Assert.True(k.Bet(1, 100).Ok);        // без автозабору й без змоги забрати — ставку назад
        Assert.True(k.Bet(2, 50).Ok);
        k.Stakes.Set("Іра", 10);              // поки летіла, черепків не стало — списання не пройде
        k.To(Lelka.Crashed);                  // каса за весь політ так і не відповіла
        Run();
        Assert.Equal(1020, k.Stakes.Balance("Оля"));
        Assert.Equal(1000, k.Stakes.Balance("Петро"));
        Assert.Equal(10, k.Stakes.Balance("Іра"));
        Assert.Empty(k.Book.Pending());
    }

    [Fact]
    public void Disabled_lelka_stands_still_instead_of_empty_rounds()
    {
        var k = new LelkaKit(("Оля", 1000));
        k.Rig(150);
        k.To(Lelka.Pause);
        k.Options.Enabled = false;
        var round = k.S.Round;
        for (var i = 0; i < 300; i++) k.H.Tick();   // 30 с
        Assert.Equal(Lelka.Pause, k.S.Phase);
        Assert.Equal(round, k.S.Round);
        Assert.Null(k.S.Until);
        Assert.False(k.View(0).GetProperty("on").GetBoolean());
        k.Options.Enabled = true;
        k.H.Tick();
        Assert.Equal(Lelka.Bets, k.S.Phase);
        Assert.Equal(round + 1, k.S.Round);
    }
}

/// <summary>Замір M1: пауза тіку на зльоті й автозаборах із 12 ставками на справжній SQLite.</summary>
public class LelkaQueuePerfTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Perf")]
    public void Takeoff_and_auto_cash_do_not_stall_the_tick()
    {
        var inline = Measure(queued: false);
        var queued = Measure(queued: true);
        output.WriteLine($"12 ставок, SQLite: каса в тіку — зліт {inline.Takeoff:F2} мс, найдовший тік {inline.Max:F2} мс; " +
            $"каса в черзі — зліт {queued.Takeoff:F2} мс, найдовший тік {queued.Max:F2} мс");
        Assert.True(queued.Max < 5, $"найдовший тік із чергою — {queued.Max:F2} мс");
    }

    static (double Takeoff, double Max) Measure(bool queued)
    {
        using var rig = new EconomyRig();
        var people = Enumerable.Range(1, 12).Select(i => ($"гравець{i}", 0)).ToArray();
        foreach (var (nick, _) in people) rig.Economy.Grant(nick, 100_000, "test", "seed:" + nick);
        var queue = new List<Action>();
        var k = new LelkaKit(_ => rig.Economy, _ => rig.GameStore, queued ? queue.Add : null, people);
        void Run() { while (queue.Count > 0) { var all = queue.ToList(); queue.Clear(); foreach (var a in all) a(); } }
        double takeoff = 0, max = 0;
        k.Rig(300);
        for (var round = 0; round < 3; round++)
        {
            Run();
            k.To(Lelka.Bets);
            for (var s = 0; s < 12; s++) Assert.True(k.Bet(s, 100, 1.01 + s * 0.02).Ok, k.H.Reply.Message);
            for (var i = 0; i < 400 && k.S.Phase != Lelka.Crashed; i++)
            {
                var flying = k.S.Phase == Lelka.Flight;
                var sw = Stopwatch.StartNew();
                k.H.Tick();
                var ms = sw.Elapsed.TotalMilliseconds;
                if (round > 0)   // перший раунд — розігрів JIT і SQLite
                {
                    max = Math.Max(max, ms);
                    if (!flying && k.S.Phase == Lelka.Flight) takeoff = Math.Max(takeoff, ms);
                }
                Run();           // «пул потоків»: черга каси між тіками, поза заміром
            }
        }
        return (takeoff, max);
    }
}
