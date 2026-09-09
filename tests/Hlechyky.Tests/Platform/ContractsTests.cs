using Hlechyky.Games;

namespace Hlechyky.Tests.Platform;

/// <summary>Димовий тест скелета: проєкт тестів бачить сервер, контракт компілюється і поводиться як задумано.</summary>
public class ContractsTests
{
    [Fact]
    public void GameInfo_derives_realtime_and_solo_flags()
    {
        var live = new GameInfo("pong", "Понг", "понг", GameGroup.Live, 2, 2, TickMs: 40);
        var solo = new GameInfo("clicker", "Гончарне коло", "гончарне коло", GameGroup.Solo, 1, 1, Start: StartMode.Immediate);
        Assert.True(live.RealTime);
        Assert.False(live.Solo);
        Assert.False(solo.RealTime);
        Assert.True(solo.Solo);
    }

    [Fact]
    public void The_client_module_defaults_to_the_game_id_and_can_be_shared()
    {
        Assert.Equal("ttt", new GameInfo("ttt", "Хрестики", "хрестики", GameGroup.Board, 2, 2).Module);
        // родина ігор в одному файлі: зникаючі хрестики малює ttt.js, окремого ttt3.js нема
        Assert.Equal("ttt", new GameInfo("ttt3", "Зникаючі", "зникаючі", GameGroup.Board, 2, 2, Client: "ttt").Module);

        var registry = Support.RoomHarness.NewRegistry();
        var ttt3 = registry.Catalog.Single(g => g.Id == "ttt3");
        Assert.Equal("ttt", ttt3.Module);
        // кожен модуль із каталогу справді лежить на диску — інакше лобі писало б «завантажую…» вічно
        foreach (var g in registry.Catalog.Where(g => !g.Id.StartsWith("t-", StringComparison.Ordinal)))
            Assert.True(File.Exists(Paths.Resolve($"web/games/{g.Module}.js")), $"нема web/games/{g.Module}.js для {g.Id}");
    }

    [Fact]
    public void ActResult_helpers_carry_message()
    {
        Assert.True(ActResult.Done.Ok);
        Assert.Equal("", ActResult.Done.Message);
        var fail = ActResult.Fail("Зараз не твій хід");
        Assert.False(fail.Ok);
        Assert.Equal("Зараз не твій хід", fail.Message);
    }

    [Fact]
    public void GameEvents_deliver_to_subscribers()
    {
        var events = new GameEvents();
        RoomFinishedEvent? got = null;
        events.RoomFinished += e => got = e;
        var info = new GameInfo("ttt", "хрестики-нолики", "хрестики-нолики", GameGroup.Board, 2, 2);
        var now = DateTimeOffset.UnixEpoch;
        events.Raise(new RoomFinishedEvent("r1", "ttt", info, 1, ["Оля", "Петро"], new RoomResult([0], false, "x", null), 0, now, now, 5));
        Assert.NotNull(got);
        Assert.Equal("Оля", got!.Seats[0]);
    }
}
