using Hlechyky.Games;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>«🆕» з каталогу сервера (прохід №3, п. 249): сервер сам пам'ятає, коли вперше побачив гру.</summary>
public class GameAddedTests
{
    static CatalogGame G(string id) =>
        new(id, id, id, "board", 2, 2, 0, "whenFull", false, false, false, [], "", false, false, id);

    [Fact]
    public void First_run_marks_everything_old_and_later_games_get_the_day_they_appeared()
    {
        var store = new FakeStore();
        var clock = new FakeClock();
        var map = new GameAdded(store, clock).Map([G("ttt"), G("c4")]);
        Assert.Empty(map);                                // наявні ігри не «нові» — їм додали модулі, коли треба

        clock.UtcNow = new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);
        // новий процес (перезапуск після деплою) — з новою грою
        var next = new GameAdded(store, clock).Map([G("ttt"), G("c4"), G("frog")]);
        Assert.Equal("2026-10-03", Assert.Single(next).Value);
        Assert.Equal("frog", next.Keys.Single());

        // ще за тиждень дата не зсувається
        clock.UtcNow = clock.UtcNow.AddDays(7);
        Assert.Equal("2026-10-03", new GameAdded(store, clock).Map([G("ttt"), G("c4"), G("frog")])["frog"]);
    }

    [Fact]
    public void Catalog_carries_added_dates()
    {
        var c = new Catalog([G("frog")], [0], null, new Dictionary<string, string> { ["frog"] = "2026-10-03" });
        var json = System.Text.Json.JsonSerializer.Serialize(c, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"added\":{\"frog\":\"2026-10-03\"}", json);
    }
}
