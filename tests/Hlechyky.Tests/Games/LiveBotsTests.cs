using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Xunit;

namespace Hlechyky.Tests.Games;

/// <summary>Спільне «🤖 + бот» живих ігор: хто кличе, коли бот грає, рівень з опцій.</summary>
public class LiveBotsTests
{
    sealed class Ctx(params string?[] nicks) : IRoomContext
    {
        public string?[] Nicks = nicks;
        public string RoomId => "t";
        public int Players => Nicks.Count(n => n is not null);
        public int Round => 1;
        public Random Rng { get; } = new(1);
        public IClock Clock => throw new NotSupportedException();
        public IReadOnlyDictionary<string, string> Options { get; } = new Dictionary<string, string>();
        public IServiceProvider Services => throw new NotSupportedException();
        public string? NickOf(int seat) => seat >= 0 && seat < Nicks.Length ? Nicks[seat] : null;
        public bool Seated(int seat) => NickOf(seat) is not null;
        public int? HostSeat => Array.FindIndex(Nicks, n => n is not null) is var i && i >= 0 ? i : null;
        public void Finish(int[] winners, string log, IReadOnlyDictionary<int, long>? scores = null, string? verdict = null) { }
        public void Log(string text) { }
        public void Say(string text) { }
        public void Score(int seat, double value, int? attempts = null) { }
        public void Award(int seat, int shards, string reason) { }
    }

    static JsonElement On(bool on) => JsonSerializer.SerializeToElement(new { on });

    [Fact]
    public void Alone_host_calls_bot_and_it_plays_until_a_friend_sits()
    {
        var ctx = new Ctx(null, "Оля", null, null);
        var b = new SoloBot();
        Assert.Equal(LiveBots.AloneText, b.CanStart(ctx, 4));
        Assert.True(b.Offer(ctx, 4));
        Assert.True(b.Switch(ctx, 1, On(true), 4).Ok);
        Assert.Null(b.CanStart(ctx, 4));
        Assert.True(b.Active(ctx, 4));
        ctx.Nicks[3] = "Петро";
        Assert.False(b.Active(ctx, 4));       // підсів друг — бот не грає, хоч і кликали
        Assert.True(b.Offer(ctx, 4));         // а прогнати його ще можна
        Assert.True(b.Switch(ctx, 1, On(false), 4).Ok);
        Assert.False(b.Offer(ctx, 4));
        Assert.False(b.Switch(ctx, 1, On(true), 4).Ok);   // удвох бота не кличуть
    }

    [Fact]
    public void Only_host_calls_the_bot_and_empty_payload_toggles()
    {
        var ctx = new Ctx("Оля", null);
        var b = new SoloBot();
        Assert.False(b.Switch(ctx, 1, default, 2).Ok);
        Assert.True(b.Switch(ctx, 0, default, 2).Ok);
        Assert.True(b.Wanted);
        Assert.True(b.Switch(ctx, 0, default, 2).Ok);
        Assert.False(b.Wanted);
    }

    [Fact]
    public void Level_comes_from_table_option_with_normal_by_default()
    {
        var b = new SoloBot();
        b.Configure(new Dictionary<string, string>());
        Assert.Equal(LiveBots.Level.Normal, b.Level);
        b.Configure(new Dictionary<string, string> { ["botlvl"] = "hard" });
        Assert.Equal(LiveBots.Level.Hard, b.Level);
        Assert.Equal("hard", b.LevelKey);
        b.Configure(new Dictionary<string, string> { ["botlvl"] = "дурня" });
        Assert.Equal(LiveBots.Level.Normal, b.Level);
        Assert.Equal("normal", LiveBots.LevelOption.Default);
    }
}
