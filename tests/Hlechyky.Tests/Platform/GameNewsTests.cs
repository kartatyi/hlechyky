using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>«Що нового» в іграх: сервер пам'ятає лише «нік бачив версію v гри g» — один раз на гравця.</summary>
public class GameNewsTests
{
    [Fact]
    public void Seen_version_is_remembered_per_nick_and_case_insensitive()
    {
        var news = new GameNews(new MemoryGameStore());
        Assert.Empty(news.Seen("Влад"));

        Assert.True(news.Mark("Влад", "tron", "2026-09-24"));
        Assert.Equal("2026-09-24", news.Seen("влад")["tron"]);
        Assert.Empty(news.Seen("Оля"));

        // Нова версія заміняє стару, інші ігри не чіпає.
        Assert.True(news.Mark("Влад", "snake", "2026-09-24"));
        Assert.True(news.Mark("ВЛАД", "tron", "2026-10-01"));
        var seen = news.Seen("влад");
        Assert.Equal("2026-10-01", seen["tron"]);
        Assert.Equal("2026-09-24", seen["snake"]);
    }

    [Theory]
    [InlineData("tron", "")]
    [InlineData("", "v1")]
    [InlineData("Tron!", "v1")]
    [InlineData("tron", "v1 <script>")]
    [InlineData(null, "v1")]
    [InlineData("tron", null)]
    public void Garbage_is_ignored(string? game, string? v)
    {
        var news = new GameNews(new MemoryGameStore());
        Assert.False(news.Mark("Влад", game, v));
        Assert.Empty(news.Seen("Влад"));
    }

    [Fact]
    public void Guest_is_not_stored_on_server()
    {
        var news = new GameNews(new MemoryGameStore());
        Assert.False(news.Mark(Auth.Guest, "tron", "v1"));
        Assert.Empty(news.Seen(Auth.Guest));
    }

    [Fact]
    public void News_also_says_which_games_the_nick_has_played_guests_too()
    {
        using var rig = new EconomyRig();
        var news = new GameNews(rig.GameStore);
        var at = rig.Clock.UtcNow;
        rig.Store.AddResult(new ResultRow("r1", "tron", 1, "гість вася", "гість Вася", "win", null, "Оля", 0, at));
        rig.Store.AddResult(new ResultRow("r1", "tron", 1, "оля", "Оля", "loss", null, "гість Вася", 0, at));
        rig.Store.AddResult(new ResultRow("r2", "chess", 1, "оля", "Оля", "win", null, "Петро", 0, at));
        rig.Store.AddResult(new ResultRow("r2", "chess", 2, "оля", "Оля", "draw", null, "Петро", 0, at));
        news.Mark("Оля", "tron", "v2");

        var (status, mine) = Radio.Reply(GameNews.Get(Radio.As("ОЛЯ"), news, rig.Store));
        Assert.Equal(200, status);
        Assert.Equal("v2", mine.GetProperty("seen").GetProperty("tron").GetString());
        Assert.Equal(["chess", "tron"], mine.GetProperty("played").EnumerateArray().Select(g => g.GetString()));

        var guest = Radio.Reply(GameNews.Get(Radio.As("гість Вася"), news, rig.Store)).Body;
        Assert.Empty(guest.GetProperty("seen").EnumerateObject());
        Assert.Equal(["tron"], guest.GetProperty("played").EnumerateArray().Select(g => g.GetString()));

        Assert.Empty(Radio.Reply(GameNews.Get(Radio.As("Новенький"), news, rig.Store)).Body.GetProperty("played").EnumerateArray());
    }
}
