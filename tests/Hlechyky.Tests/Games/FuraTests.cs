using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>«Чумаки-далекобійники», етап 0: соло-стіл відкривається одразу, вид — лише номер етапу, дій нема.</summary>
public sealed class FuraTests
{
    [Fact]
    public void Solo_opens_and_shows_stage()
    {
        var h = new RoomHarness("fura");
        var reply = h.Solo("Оля");
        Assert.True(reply.Ok, reply.Message);
        Assert.Equal(0, h.View(0).GetProperty("stage").GetInt32());
    }

    [Fact]
    public void Act_is_refused_and_view_stays()
    {
        var h = new RoomHarness("fura");
        Assert.True(h.Solo("Оля").Ok);
        var before = h.View(0).GetRawText();
        Assert.False(h.Act(0, "go", new { }).Ok);
        Assert.Equal(before, h.View(0).GetRawText());
    }
}
