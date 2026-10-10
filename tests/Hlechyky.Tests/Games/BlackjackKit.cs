using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Обв'язка тестів «Двадцять одно» (specs/blackjack.md §9): свої ставки, сховище й налаштування для каси (виплати — одразу,
/// <c>defer: a =&gt; a()</c>), кімната через справжній реєстр і каркас. Карти — <see cref="Rig"/> (підуть першими).
/// Порядок роздачі на одного: моя 1-ша, Глекова відкрита, моя 2-га, Глекова закрита, далі — добори.
/// </summary>
public sealed class BlackjackKit
{
    public FakeStakes Stakes { get; }
    public IGameStore Store { get; }
    public BlackjackOptions Opts { get; }
    public BlackjackBook Book { get; }
    public RoomHarness H { get; }

    public BlackjackKit(string game = "blackjack", int seed = 7, IGameStore? store = null, BlackjackOptions? opts = null,
        Func<FakeStakes, IStakes>? wrap = null, params (string Nick, int Wallet)[] people)
    {
        Stakes = new FakeStakes();
        foreach (var (nick, wallet) in people) Stakes.Set(nick, wallet);
        Store = store ?? new FakeStore();
        Opts = opts ?? new BlackjackOptions();
        Book = new BlackjackBook(wrap?.Invoke(Stakes) ?? Stakes, Store, defer: a => a(), options: new FixedOptions<BlackjackOptions>(Opts));
        H = new RoomHarness(game, services: RoomHarness.WithService(Book), seed: seed);
        foreach (var (nick, _) in people)
        {
            var reply = game == "blackjack-solo" ? H.Solo(nick) : H.Join(nick);
            Assert.True(reply.Ok, reply.Message);
        }
    }

    public static BlackjackKit Table(params (string Nick, int Wallet)[] people) => new("blackjack", people: people);
    public static BlackjackKit Solo(string nick = "Оля", int wallet = 1000) => new("blackjack-solo", people: [(nick, wallet)]);

    public BlackjackGame G => (BlackjackGame)H.Room.Game;
    public BlackjackState S => G.State;
    public FakeStore? Fake => Store as FakeStore;

    public ActResult Bet(int seat, int amount) => H.Act(seat, "bet", new { amount });
    public ActResult Do(int seat, string action) => H.Act(seat, action, new { });

    public void Rig(params string[] cards) => G.Rig = [.. cards];

    /// <summary>Тикати, поки фаза не стане <paramref name="phase"/>.</summary>
    public void To(string phase, int max = 4000)
    {
        for (var i = 0; i < max && S.Phase != phase; i++) H.Tick();
        Assert.Equal(phase, S.Phase);
    }

    /// <summary>Стіл: «Роздавай!» від усіх, хто ставив (роздає одразу, коли готові всі).</summary>
    public void DealNow(params int[] seats)
    {
        foreach (var seat in seats) Assert.True(H.Act(seat, "deal", new { }).Ok, H.Reply.Message);
    }

    public JsonElement View(int? seat) => H.View(seat);
    public JsonElement Me(int seat) => H.View(seat).GetProperty("me");

    public static string Key(string nick) => nick.Trim().ToLowerInvariant();

    public List<string> Spends => [.. Stakes.Calls.Where(c => c.StartsWith("spend:", StringComparison.Ordinal))];
    public List<string> Grants => [.. Stakes.Calls.Where(c => c.StartsWith("grant:", StringComparison.Ordinal))];

    public string TableKey => $"{H.RoomId}:{S.Epoch}";

    /// <summary>Ключ списання: blackjack-bet:{кімната}:{epoch}:{роздача}:{крок}:{нік}.</summary>
    public string BetRef(string step, string nick, int? round = null) => $"blackjack-bet:{TableKey}:{round ?? S.RoundNo}:{step}:{Key(nick)}";
    public string PayRef(string nick, int? round = null) => $"blackjack-pay:{TableKey}:{round ?? S.RoundNo}:{Key(nick)}";
    public string BackRef(string nick, int? round = null) => $"blackjack-back:{TableKey}:{round ?? S.RoundNo}:{Key(nick)}";

    public List<string> Achievements(string nick) =>
        [.. H.Awards.Where(a => a.Nick == nick && a.Reason.StartsWith("ach:blackjack-", StringComparison.Ordinal)).Select(a => a.Reason)];
}
