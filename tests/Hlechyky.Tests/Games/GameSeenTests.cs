using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>«🧠 Пам'ять баченого між партіями» (пакет 1): таблиця game_seen, стеля, правило «найменше бачили сидячі».</summary>
public class GameSeenTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 19, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RememberWritesToDbAndSurvivesNewProcessMemory()
    {
        using var temp = new TempDb();
        var seen = new GameSeen(temp.Db);
        seen.Remember("pozyvni", [GameSeen.NickKey(" Оля ")], ["кіт", "пес"], T0);
        seen.Flush();
        Assert.Equal(2, seen.CountInDb("pozyvni", "оля"));
        Assert.Equal(0, seen.CountInDb("pictionary", "оля"));   // інша гра — своя пам'ять

        // «Новий процес»: інша Db на тому самому файлі — пам'ять порожня, поки не дочитали з бази.
        var again = new GameSeen(new Db(temp.Path));
        again.Warm("pozyvni");
        var peek = again.Peek("pozyvni", ["оля"]);
        Assert.Equal(new[] { "кіт", "пес" }, peek.Keys.Order().ToArray());
        Assert.Equal(1, peek["кіт"].By);
        Assert.Equal(T0, peek["кіт"].Last);
        Assert.Empty(again.Peek("pozyvni", ["петро"]));
    }

    [Fact]
    public void PeekCountsSeatedWhoSawAndLatestTime()
    {
        using var temp = new TempDb();
        var seen = new GameSeen(temp.Db);
        seen.Seed("g", ["оля", "петро"], ["a"], T0);
        seen.Seed("g", ["оля"], ["a"], T0.AddHours(1));
        seen.Seed("g", ["марко"], ["b"], T0);

        var peek = seen.Peek("g", ["оля", "петро"]);
        Assert.Equal(new GameSeen.Seen(2, T0.AddHours(1)), peek["a"]);
        Assert.False(peek.ContainsKey("b"));   // марко не сидить — не рахується
    }

    [Fact]
    public void CapKeepsNewestPerGameAndNick()
    {
        using var temp = new TempDb();
        var seen = new GameSeen(temp.Db);
        var items = Enumerable.Range(0, GameSeen.Cap + 50).Select(i => "w" + i).ToArray();
        for (var i = 0; i < items.Length; i += 50)
            seen.Remember("g", ["оля"], items.Skip(i).Take(50), T0.AddMinutes(i));
        seen.Remember("g", ["петро"], ["w0"], T0);
        seen.Flush();

        Assert.Equal(GameSeen.Cap, seen.CountInDb("g", "оля"));
        Assert.Equal(1, seen.CountInDb("g", "петро"));
        var peek = seen.Peek("g", ["оля"]);
        Assert.Equal(GameSeen.Cap, peek.Count);
        Assert.False(peek.ContainsKey("w0") || peek.ContainsKey("w49"));   // найстаріша пачка пішла
        Assert.True(peek.ContainsKey("w" + (items.Length - 1)));
    }

    [Fact]
    public void OrderPutsLeastSeenBySeatedFirstThenOlder()
    {
        var seen = new Dictionary<string, GameSeen.Seen>
        {
            ["a"] = new(2, T0),
            ["b"] = new(1, T0.AddDays(1)),
            ["c"] = new(1, T0),
        };
        Assert.Equal(new[] { "d", "e", "c", "b", "a" }, GameSeen.Order(new[] { "a", "d", "b", "e", "c" }, x => x, seen));
    }

    [Fact]
    public void EmptyHistoryKeepsOldBehaviour()
    {
        var none = new Dictionary<string, GameSeen.Seen>();
        Assert.Equal(new[] { "c", "a", "b" }, GameSeen.Order(new[] { "c", "a", "b" }, x => x, none));
        // Pick без історії — рівно rng.Next з усіх, як було.
        var items = new[] { "a", "b", "c", "d" };
        Assert.Equal(items[new Random(7).Next(4)], GameSeen.Pick(items, x => x, none, new Random(7)));

        var silent = new GameSeen(null);   // без бази — мовчить і не кидає
        silent.Remember("g", ["оля"], ["a"], T0);
        silent.Prefetch("g");
        Assert.Empty(silent.Peek("g", ["оля"]));
    }

    [Fact]
    public void PickTakesUnseenWhileAny()
    {
        var seen = new Dictionary<string, GameSeen.Seen> { ["a"] = new(1, T0), ["b"] = new(1, T0), ["c"] = new(1, T0) };
        for (var i = 0; i < 20; i++)
            Assert.Equal("d", GameSeen.Pick(new[] { "a", "b", "c", "d" }, x => x, seen, new Random(i)));
    }
}
