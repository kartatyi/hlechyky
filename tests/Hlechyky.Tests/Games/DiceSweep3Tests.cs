using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Під глеком», прохід №3 (29.09): вболівальники ставлять «правда/брехня» (таблиця «нюх вболівальника»), Глек читає
/// вердикт розкриття вголос (гра не чекає), а звання партії йдуть у сезон.
/// </summary>
public class DiceSweep3Tests
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна"];

    static RoomHarness Table(int players, IServiceProvider? services = null, object? options = null, int seed = 42)
    {
        var h = new RoomHarness("dice", options, seed, services);
        for (var i = 0; i < players; i++) h.Join(Nicks[i]);
        h.Start();
        for (var i = 0; i < 40 && DiceTests.Game(h).Phase != DicePhase.Bid; i++) h.Tick();
        return h;
    }

    static JsonElement V(RoomHarness h) => h.View(null);

    /// <summary>Оля ставить «дві трійки», на столі їх три (з глечиком) — ставка правдива.</summary>
    static RoomHarness TwoThrees(IServiceProvider? services = null)
    {
        var h = Table(2, services);
        DiceTests.Core(h).Arrange([[3, 3, 1, 5, 6], [2, 2, 2, 2, 2]]);
        DiceTests.Core(h).SetTurn(0);
        Assert.True(h.Act(0, "bid", new { q = 2, f = 3 }).Ok);
        return h;
    }

    [Fact]
    public void Spectator_bets_on_the_bid_and_lands_in_the_fans_table()
    {
        var h = TwoThrees();
        var g = DiceTests.Game(h);
        Assert.True(g.Bet("Глядач", null, truth: true).Ok);
        Assert.True(g.Bet("Скептик", null, truth: false).Ok);
        Assert.Equal(1, V(h).GetProperty("fan").GetProperty("bets").GetProperty("truth").GetInt32());
        Assert.True(h.Act(1, "liar", new { q = 2, f = 3 }).Ok);
        var fans = V(h).GetProperty("fan").GetProperty("settled").EnumerateArray().ToList();
        Assert.Equal(2, fans.Count);
        Assert.True(fans.Single(f => f.GetProperty("nick").GetString() == "Глядач").GetProperty("hit").GetBoolean());
        Assert.False(fans.Single(f => f.GetProperty("nick").GetString() == "Скептик").GetProperty("hit").GetBoolean());
        var table = V(h).GetProperty("fan").GetProperty("table").EnumerateArray().First();
        Assert.Equal("Глядач", table.GetProperty("nick").GetString());
        Assert.Equal(1, table.GetProperty("hits").GetInt32());
    }

    [Fact]
    public void A_bet_on_an_older_bid_burns_and_a_player_with_dice_cannot_bet()
    {
        var h = TwoThrees();
        var g = DiceTests.Game(h);
        Assert.True(g.Bet("Глядач", null, truth: true).Ok);
        Assert.False(g.Bet("Петро", 1, truth: false).Ok);   // Петро ще з кісточками
        Assert.True(h.Act(1, "bid", new { q = 3, f = 3 }).Ok);
        Assert.Equal(JsonValueKind.Null, V(h).GetProperty("fan").ValueKind);
        Assert.True(h.Act(0, "liar", new { q = 3, f = 3 }).Ok);
        Assert.Equal(JsonValueKind.Null, V(h).GetProperty("fan").ValueKind);
    }

    [Fact]
    public void Voice_reads_the_verdict_and_the_game_never_waits()
    {
        var voice = new DotepyTests.FakeVoice();
        var h = TwoThrees(new ServiceCollection().AddSingleton<IDotepyVoice>(voice).BuildServiceProvider());
        Assert.Contains(voice.Prepared, p => p.Text == DiceVoiceLines.Win("Петро"));
        Assert.True(h.Act(1, "liar", new { q = 2, f = 3 }).Ok);
        Assert.Equal(DiceVoiceLines.Truth("Петро"), V(h).GetProperty("fan").GetProperty("say").GetProperty("text").GetString());

        // Голос, що не доспіває ніколи: та сама партія йде тими самими тиками, просто мовчки.
        static string Run(IDotepyVoice? v)
        {
            var sp = v is null ? null : new ServiceCollection().AddSingleton(v).BuildServiceProvider();
            var t = Table(3, sp, seed: 5);
            var moves = 0;
            var trace = new System.Text.StringBuilder();
            for (var i = 0; i < 3000 && t.Room.Status == RoomStatus.Playing; i++)
            {
                DiceTests.BotStep(t, ref moves);
                t.Tick();
                trace.Append(DiceTests.Game(t).Phase).Append(',');
            }
            Assert.Null(DiceTests.Game(t).Speech);
            return trace.ToString();
        }
        Assert.Equal(Run(null), Run(new DotepyTests.FakeVoice(readyAfter: -1)));
    }

    [Fact]
    public void Titles_of_a_played_game_go_to_the_season()
    {
        var season = new DiceSeason(null);
        var sp = new ServiceCollection().AddSingleton(season).BuildServiceProvider();
        var h = Table(3, sp, seed: 7);
        var moves = 0;
        for (var i = 0; i < 5000 && h.Room.Status == RoomStatus.Playing; i++)
        {
            DiceTests.BotStep(h, ref moves);
            if (DiceTests.Game(h).Phase == DicePhase.Bid) DiceTests.Game(h).Bet("Глядач", null, truth: i % 3 != 0);
            h.Tick();
        }
        var fun = V(h).GetProperty("result").GetProperty("fun").GetArrayLength();
        Assert.True(fun > 0);
        var table = JsonSerializer.SerializeToElement(season.Table(DateTimeOffset.MinValue));
        Assert.Equal(fun, table.EnumerateArray().Sum(t => t.GetProperty("rows").EnumerateArray().Sum(r => r.GetProperty("n").GetInt32())));
    }

    [Fact]
    public void Season_counts_titles_per_nick_within_the_period()
    {
        var season = new DiceSeason(null);
        var now = DateTimeOffset.UtcNow;
        season.Record([(DiceTitles.Bluff, "Петро"), (DiceTitles.Nose, "Оля")], now.AddDays(-10));
        season.Record([(DiceTitles.Bluff, "Петро")], now.AddDays(-1));
        season.Record([(DiceTitles.Bluff, "петро"), (DiceTitles.Bluff, "Оля")], now);
        var week = JsonSerializer.SerializeToElement(season.Table(now.AddDays(-7)));
        var bluff = week.EnumerateArray().Single(t => t.GetProperty("kind").GetString() == DiceTitles.Bluff);
        var top = bluff.GetProperty("rows")[0];
        Assert.Equal(2, top.GetProperty("n").GetInt32());
        Assert.DoesNotContain(week.EnumerateArray(), t => t.GetProperty("kind").GetString() == DiceTitles.Nose);
    }
}
