using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Обв'язка тестів рулетки (specs/roulette.md §9): свої ставки й сховище для каси (виплати — одразу, <c>defer: a =&gt; a()</c>),
/// кімната через справжній реєстр і каркас.
/// </summary>
public sealed class RouletteKit
{
    public FakeStakes Stakes { get; }
    public IGameStore Store { get; }
    public RouletteBook Book { get; }
    public RoomHarness H { get; }

    public RouletteKit(string game = "roulette", int seed = 7, IGameStore? store = null, Func<FakeStakes, IStakes>? wrap = null,
        params (string Nick, int Wallet)[] people)
    {
        Stakes = new FakeStakes();
        foreach (var (nick, wallet) in people) Stakes.Set(nick, wallet);
        Store = store ?? new FakeStore();
        Book = new RouletteBook(wrap?.Invoke(Stakes) ?? Stakes, Store, defer: a => a());
        H = new RoomHarness(game, services: RoomHarness.WithService(Book), seed: seed);
        foreach (var (nick, _) in people)
        {
            var reply = game == "roulette-solo" ? H.Solo(nick) : H.Join(nick);
            Assert.True(reply.Ok, reply.Message);
        }
    }

    public static RouletteKit Table(params (string Nick, int Wallet)[] people) => new("roulette", people: people);
    public static RouletteKit Solo(string nick = "Оля", int wallet = 1000) => new("roulette-solo", people: [(nick, wallet)]);

    public RouletteGame G => (RouletteGame)H.Room.Game;
    public RouletteState S => G.State;
    public FakeStore? Fake => Store as FakeStore;

    public ActResult Bet(int seat, string spot, int amount) => H.Act(seat, "bet", new { spot, amount });

    public void Rig(int n) => G.Rig = () => n;

    /// <summary>Тикати, поки фаза не стане <paramref name="phase"/>.</summary>
    public void To(string phase, int max = 2000)
    {
        for (var i = 0; i < max && S.Phase != phase; i++) H.Tick();
        Assert.Equal(phase, S.Phase);
    }

    /// <summary>Ціле коло спільного столу: до закриття, кулька лягла, нове вікно ставок.</summary>
    public void Round(int n)
    {
        Rig(n);
        To(RouletteGame.Spinning);
        To(RouletteGame.Result);
        To(RouletteGame.Bets);
    }

    /// <summary>Соло: крутити й дочекатись, поки кулька ляже.</summary>
    public void SoloSpin(int n)
    {
        Rig(n);
        var r = H.Act(0, "spin", new { });
        Assert.True(r.Ok, r.Message);
        To(RouletteGame.Bets);
    }

    public JsonElement View(int? seat) => H.View(seat);
    public JsonElement Me(int seat) => H.View(seat).GetProperty("me");

    public static string Key(string nick) => nick.Trim().ToLowerInvariant();

    public List<string> Spends => [.. Stakes.Calls.Where(c => c.StartsWith("spend:", StringComparison.Ordinal))];
    public List<string> Grants => [.. Stakes.Calls.Where(c => c.StartsWith("grant:", StringComparison.Ordinal))];

    /// <summary>Ключ леджера кола: roulette-bet:{кімната}:{epoch}:{коло}:{нік}.</summary>
    public string Ref(string kind, string nick, int? spin = null) =>
        $"{kind}:{H.RoomId}:{S.Epoch}:{spin ?? S.SpinNo}:{Key(nick)}";

    public List<string> Achievements(string nick) =>
        [.. H.Awards.Where(a => a.Nick == nick && a.Reason.StartsWith("ach:roulette-", StringComparison.Ordinal)).Select(a => a.Reason)];
}

/// <summary>Сховище, що не пише (каса заїла).</summary>
public sealed class BrokenStore : IGameStore
{
    public void SaveState(string key, string json) => throw new IOException("диск повний");
    public string? LoadState(string key) => null;
    public void DeleteState(string key) { }
}

/// <summary>Ставки-шпигун: перед кожним списанням питають, що зараз лежить у касі.</summary>
public sealed class SpyStakes(FakeStakes inner) : IStakes
{
    public Action<string>? BeforeSpend { get; set; }
    public int Balance(string nick) => inner.Balance(nick);
    public bool TrySpend(string nick, int amount, string reason, string refKey)
    {
        BeforeSpend?.Invoke(refKey);
        return inner.TrySpend(nick, amount, reason, refKey);
    }
    public void Grant(string nick, int amount, string reason, string refKey) => inner.Grant(nick, amount, reason, refKey);
    public IReadOnlyList<LedgerMove>? Moves(string refPrefix) => inner.Moves(refPrefix);
}
