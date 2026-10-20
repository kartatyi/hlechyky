using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Tests.Support;
using Microsoft.AspNetCore.Http;

namespace Hlechyky.Tests.Platform;

/// <summary>«⭐ Мої ігри» (зірочки акаунта) і «🕘 Нещодавно» в лобі — GET /api/games/mine, POST /api/games/fav.</summary>
public class GameFavsTests
{
    static HttpContext User(string nick)
    {
        var c = Radio.As(nick);
        c.Items["account"] = new Account(nick, "", "salt", "member");
        return c;
    }

    [Fact]
    public void Stars_are_kept_in_order_per_nick_case_insensitive()
    {
        var favs = new GameFavs(new MemoryGameStore());
        Assert.Equal(["chess"], favs.Set("Влад", "chess", true));
        Assert.Equal(["chess", "crowd"], favs.Set("ВЛАД", "crowd", true));
        Assert.Equal(["chess", "crowd"], favs.Set("влад", "chess", true));   // вдруге — без змін
        Assert.Equal(["crowd"], favs.Set("Влад", "chess", false));
        Assert.Equal(["crowd"], favs.Of("влад"));
        Assert.Empty(favs.Of("Оля"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Chess!")]
    [InlineData("chess <script>")]
    public void Garbage_and_guests_are_refused(string? game)
    {
        var favs = new GameFavs(new MemoryGameStore());
        Assert.Null(favs.Set("Влад", game, true));
        Assert.Null(favs.Set("гість Вася", "chess", true));
        Assert.Empty(favs.Of("Влад"));
        Assert.Empty(favs.Of("гість Вася"));
    }

    [Fact]
    public void Too_many_stars_are_refused_but_removing_still_works()
    {
        var favs = new GameFavs(new MemoryGameStore());
        for (var i = 0; i < GameFavs.Max; i++) Assert.NotNull(favs.Set("Влад", "g" + i, true));
        Assert.Null(favs.Set("Влад", "one-more", true));
        Assert.Equal(GameFavs.Max - 1, favs.Set("Влад", "g0", false)!.Count);
    }

    [Fact]
    public void Only_accounts_can_star_and_mine_gives_stars_only_to_accounts()
    {
        using var rig = new EconomyRig();
        var favs = new GameFavs(rig.GameStore);

        var (status, _) = Radio.Reply(GameFavs.Fav(Radio.As("гість Вася"), new("chess", true), favs));
        Assert.Equal(403, status);

        var ok = Radio.Reply(GameFavs.Fav(User("Оля"), new("chess", true), favs)).Body;
        Assert.True(ok.GetProperty("ok").GetBoolean());
        Assert.Equal(["chess"], ok.GetProperty("favs").EnumerateArray().Select(x => x.GetString()));

        var mine = Radio.Reply(GameFavs.Mine(User("оля"), favs, rig.Store)).Body;
        Assert.True(mine.GetProperty("account").GetBoolean());
        Assert.Equal(["chess"], mine.GetProperty("favs").EnumerateArray().Select(x => x.GetString()));

        // Той самий нік без входу (гостьова вкладка до /api/me) зірочок не бачить.
        Assert.Empty(Radio.Reply(GameFavs.Mine(Radio.As("Оля"), favs, rig.Store)).Body.GetProperty("favs").EnumerateArray());
    }

    [Fact]
    public void Recent_goes_from_the_freshest_day_counting_time_at_the_table_too()
    {
        using var rig = new EconomyRig();
        var favs = new GameFavs(rig.GameStore);
        var at = rig.Clock.UtcNow;   // 2026-09-10 12:00 UTC = 15:00 у Києві
        rig.Store.AddResult(new ResultRow("r1", "chess", 1, "оля", "Оля", "win", null, "Петро", 0, at.AddDays(-3)));
        rig.Store.AddResult(new ResultRow("r2", "tron", 1, "оля", "Оля", "loss", null, "Петро", 0, at.AddHours(-2)));
        rig.Store.AddResult(new ResultRow("r3", "mafia", 1, "оля", "Оля", "win", null, "Петро", 0, at.AddHours(-1)));
        rig.Store.AddResult(new ResultRow("r4", "pong", 1, "петро", "Петро", "win", null, "Оля", 0, at));
        // Гончарне коло партій не пише — видно з часу за столом; хвилинка менше 60 с не рахується.
        rig.Store.AddTime([
            new TimeRow("оля", "game:clicker", "2026-09-10", 600),
            new TimeRow("оля", "game:chess", "2026-09-09", 300),
            new TimeRow("оля", "game:bomber", "2026-09-10", 30),
            new TimeRow("оля", "lobby", "2026-09-10", 900),
        ]);

        var list = rig.Store.RecentGames("оля", 10);
        Assert.Equal(["mafia", "tron", "clicker", "chess"], list.Select(x => x.Game));
        Assert.Equal(["2026-09-10", "2026-09-10", "2026-09-10", "2026-09-09"], list.Select(x => x.Day));
        Assert.Equal(["mafia", "tron"], rig.Store.RecentGames("оля", 2).Select(x => x.Game));

        var mine = Radio.Reply(GameFavs.Mine(Radio.As("Оля"), favs, rig.Store)).Body;
        Assert.False(mine.GetProperty("account").GetBoolean());
        Assert.Equal("mafia", mine.GetProperty("recent")[0].GetProperty("game").GetString());
        Assert.Empty(Radio.Reply(GameFavs.Mine(Radio.As(Auth.Guest), favs, rig.Store)).Body.GetProperty("recent").EnumerateArray());
    }
}
