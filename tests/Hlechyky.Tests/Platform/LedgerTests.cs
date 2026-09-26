using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Tests.Support;
using Microsoft.AspNetCore.Http;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// «Мій гаманець» (GET /api/games/ledger): баланс, останні рухи людською мовою і місяць за групами — звідки черепки
/// прийшли й куди пішли. Лише свій: нік береться з того, під ким зайшли, а не з запиту.
/// </summary>
public class LedgerTests
{
    /// <summary>Субота, 12:00 за Києвом; місяць почався 28.08 (27.08 о 21:00 UTC).</summary>
    static readonly DateTimeOffset Now = new(2026, 9, 26, 9, 0, 0, TimeSpan.Zero);

    static readonly GameInfo Chess = EconomyRig.Info("chess", "Шахи", "шахи", rated: true);

    static JsonElement Mine(EconomyRig rig, string nick, int? limit = null, string? query = null)
    {
        var c = Radio.As(nick);
        if (query is not null) c.Request.QueryString = new QueryString(query);
        return Views.Json(PeopleEndpoints.Ledger(c, limit, rig.Store, rig.Economy, rig.Clock));
    }

    static EconomyRig Rig()
    {
        var rig = new EconomyRig();
        rig.Clock.UtcNow = Now;
        rig.Names.Learn(Chess);
        return rig;
    }

    [Fact]
    public void Ledger_shows_the_wallet_and_latest_moves_in_human_words()
    {
        using var rig = Rig();
        rig.Economy.Grant("Оля", 5, "win:chess", "w1");
        rig.Clock.Advance(60);
        rig.Economy.Grant("Оля", 1, "listen", "l1");
        rig.Clock.Advance(60);
        Assert.True(rig.Economy.TrySpend("Оля", 4, "stake", "s1"));

        var e = Mine(rig, "оля");

        Assert.Equal(2, e.GetProperty("balance").GetInt32());
        Assert.Equal(6, e.GetProperty("earned").GetInt32());
        Assert.Equal(4, e.GetProperty("spent").GetInt32());
        var items = e.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([-4, 1, 5], items.Select(i => i.GetProperty("delta").GetInt32()));
        Assert.Equal(["stake", "listen", "win:chess"], items.Select(i => i.GetProperty("reason").GetString()));
        Assert.Equal(["ставка", "за те, що слухаєш", "перемога — Шахи"], items.Select(i => i.GetProperty("text").GetString()));
        Assert.Equal(Now.AddSeconds(120), items[0].GetProperty("at").GetDateTimeOffset());
        Assert.Equal(Now, items[2].GetProperty("at").GetDateTimeOffset());
    }

    [Fact]
    public void Someone_elses_wallet_is_out_of_reach()
    {
        using var rig = Rig();
        rig.Economy.Grant("Петро", 100, "listen", "p1");
        rig.Economy.Grant("Оля", 1, "listen", "o1");

        // хоч би що дописали в запит — видно лише свій гаманець
        var e = Mine(rig, "Оля", query: "?nick=%D0%9F%D0%B5%D1%82%D1%80%D0%BE");
        Assert.Equal(1, e.GetProperty("balance").GetInt32());
        Assert.Equal([1], e.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("delta").GetInt32()));

        // гість «Петро» — не Петро з акаунтом: у нього свій, порожній
        var guest = Mine(rig, "гість Петро");
        Assert.Equal(0, guest.GetProperty("balance").GetInt32());
        Assert.Empty(guest.GetProperty("items").EnumerateArray());
        Assert.Empty(guest.GetProperty("month").EnumerateArray());
    }

    [Fact]
    public void Limit_is_thirty_by_default_and_never_more_than_a_hundred()
    {
        using var rig = Rig();
        for (var i = 0; i < 120; i++)
        {
            rig.Economy.Grant("Оля", 1, "listen", "l" + i);
            rig.Clock.Advance(60);
        }

        int Count(int? limit) => Mine(rig, "Оля", limit).GetProperty("items").GetArrayLength();
        Assert.Equal(30, Count(null));
        Assert.Equal(5, Count(5));
        Assert.Equal(PeopleEndpoints.LedgerMax, Count(500));
        Assert.Equal(1, Count(0));
        // найсвіжіше згори
        Assert.Equal(Now.AddMinutes(119), Mine(rig, "Оля", 1).GetProperty("items")[0].GetProperty("at").GetDateTimeOffset());
    }

    [Fact]
    public void Month_groups_where_shards_came_from_and_where_they_went()
    {
        using var rig = Rig();
        rig.Clock.UtcNow = new DateTimeOffset(2026, 8, 27, 20, 30, 0, TimeSpan.Zero);   // 27.08, 23:30 за Києвом — ще не місяць
        rig.Economy.Grant("Оля", 50, "listen", "old");
        rig.Clock.UtcNow = Now.AddDays(-5);
        rig.Economy.Grant("Оля", 10, "listen", "l1");
        rig.Economy.Grant("Оля", 10, "listen", "l2");
        rig.Economy.Grant("Оля", 5, "win:chess", "w1");
        Assert.True(rig.Economy.TrySpend("Оля", 10, "stake", "s1"));
        rig.Economy.Grant("Оля", 20, "stake-win", "s1-win");
        Assert.True(rig.Economy.TrySpend("Оля", 5, "stake", "s2"));
        rig.Economy.Grant("Оля", 5, "stake-refund", "s2-back");
        Assert.True(rig.Economy.TrySpend("Оля", 7, "ban:trk1"));
        rig.Economy.Grant("Оля", 7, "ban-refund:trk1");
        Assert.True(rig.Economy.TrySpend("Оля", 9, "unban:trk2"));
        rig.Economy.Grant("Оля", 2, "ad:listen", "ad1");
        rig.Clock.UtcNow = Now;

        var month = Mine(rig, "Оля").GetProperty("month").EnumerateArray().ToList();

        Assert.Equal(["stake", "ban", "listen", "win", "ad"], month.Select(g => g.GetProperty("cat").GetString()));
        Assert.Equal(["ставки", "бан-лист", "слухання радіо", "перемоги", "реклама"], month.Select(g => g.GetProperty("title").GetString()));
        Assert.Equal([25, 7, 20, 5, 2], month.Select(g => g.GetProperty("earned").GetInt32()));
        Assert.Equal([15, 16, 0, 0, 0], month.Select(g => g.GetProperty("spent").GetInt32()));
    }

    [Theory]
    [InlineData("listen", "listen")]
    [InlineData("win:chess", "win")]
    [InlineData("daily:wordle", "daily")]
    [InlineData("ach:first-win", "ach")]
    [InlineData("stake", "stake")]
    [InlineData("stake-win", "stake")]
    [InlineData("stake-refund", "stake")]
    [InlineData("ban:abc", "ban")]
    [InlineData("unban:abc", "ban")]
    [InlineData("ban-refund:abc", "ban")]
    [InlineData("award:shootout:best", "award")]
    [InlineData("щось-нове", "щось-нове")]
    public void Reason_falls_into_its_group(string reason, string cat)
    {
        Assert.Equal(cat, PeopleEndpoints.Cat(reason));
        Assert.False(string.IsNullOrWhiteSpace(PeopleEndpoints.CatTitle(cat)));
    }
}
