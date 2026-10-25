using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Ручне тестування Ока майстра (ClickerEyeTest.cs, 24.10): з <c>Clicker:EyeTest</c> майстра кличуть кнопкою, без
/// нього — дії нема й панелі у виді теж.
/// </summary>
public class ClickerEyeTestTests
{
    static RoomHarness Wheel(bool on)
    {
        var services = new ServiceCollection()
            .Configure<ClickerOptions>(o => o.EyeTest = on)
            .BuildServiceProvider();
        var h = new RoomHarness("clicker", services: services);
        h.Solo("Оля");
        return h;
    }

    static JsonElement Guard(RoomHarness h) => h.View(0).GetProperty("guard");
    static ActResult Test(RoomHarness h, string kind) => h.Act(0, "eyetest", JsonSerializer.SerializeToElement(new { kind }));

    [Fact]
    public void Off_by_default_no_action_and_no_panel()
    {
        var h = Wheel(on: false);
        Assert.False(Test(h, "calm").Ok);
        Assert.Equal(JsonValueKind.Null, Guard(h).ValueKind);
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("eyeTest").ValueKind);
    }

    [Fact]
    public void Calm_asks_now_and_pays()
    {
        var h = Wheel(on: true);
        Assert.True(h.View(0).GetProperty("eyeTest").GetProperty("left").GetInt32() >= ClickerGuard.CalmMin);
        Assert.True(Test(h, "calm").Ok);
        var g = Guard(h);
        Assert.Equal("", g.GetProperty("why").GetString());
        Assert.True(g.GetProperty("pays").GetBoolean());
        Assert.True(g.GetProperty("gain").GetDouble() > 0);
    }

    [Theory]
    [InlineData("rhythm")]
    [InlineData("press")]
    public void Suspect_asks_now_without_pay(string kind)
    {
        var h = Wheel(on: true);
        Assert.True(Test(h, kind).Ok);
        var g = Guard(h);
        Assert.Equal(kind, g.GetProperty("why").GetString());
        Assert.False(g.GetProperty("pays").GetBoolean());
    }

    [Fact]
    public void Soon_lock_and_clear()
    {
        var h = Wheel(on: true);
        Assert.True(Test(h, "soon").Ok);
        Assert.Equal(Clicker.EyeTestSoon, h.View(0).GetProperty("eyeTest").GetProperty("left").GetInt32());

        Assert.True(Test(h, "lock").Ok);
        Assert.Equal("misses", Guard(h).GetProperty("why").GetString());
        Assert.NotEqual(JsonValueKind.Null, Guard(h).GetProperty("lockUntil").ValueKind);

        Assert.True(Test(h, "clear").Ok);
        Assert.Equal(JsonValueKind.Null, Guard(h).ValueKind);
        Assert.False(Test(h, "boom").Ok);
    }
}
