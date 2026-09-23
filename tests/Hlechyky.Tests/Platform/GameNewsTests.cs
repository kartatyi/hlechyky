using Hlechyky.Games;

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
}
