using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Platform;

/// <summary>Хто де скільки: відрізки з <c>Here</c>/<c>SetListening</c>, кілька вкладок, хвіст бездіяльності, запис по днях.</summary>
public class PlayClockTests
{
    const string W = PlayClock.Where;

    sealed class Rig : IDisposable
    {
        public EconomyRig Eco { get; } = new();
        public PlayClock Clock { get; }
        public Rig() => Clock = new PlayClock(Eco.Store, Eco.Clock, NullLogger<PlayClock>.Instance);
        public void Wait(double seconds) => Eco.Clock.Advance(seconds);
        public int Seconds(string nickKey, string place) => Eco.Store.TimeTotals()
            .Where(r => r.NickKey == nickKey && r.Place == place).Sum(r => r.Seconds);
        public void Dispose() => Eco.Dispose();
    }

    [Fact]
    public void Counts_time_from_place_to_null()
    {
        using var rig = new Rig();
        rig.Clock.Set("c1", "Оля", W, "game:clicker");
        rig.Wait(90);
        rig.Clock.Set("c1", "Оля", W, null);
        rig.Wait(600);   // ніде — не набігає
        rig.Clock.Flush();

        Assert.Equal(90, rig.Seconds("оля", "game:clicker"));
    }

    [Fact]
    public void Minute_flush_writes_open_runs_and_keeps_counting()
    {
        using var rig = new Rig();
        rig.Clock.Set("c1", "Оля", W, "game:tron");
        rig.Wait(60);
        rig.Clock.Flush();
        Assert.Equal(60, rig.Seconds("оля", "game:tron"));

        rig.Wait(30);
        rig.Clock.Drop("c1");
        rig.Clock.Flush();
        Assert.Equal(90, rig.Seconds("оля", "game:tron"));
    }

    [Fact]
    public void Two_tabs_in_the_same_place_count_once()
    {
        using var rig = new Rig();
        rig.Clock.Set("c1", "Оля", W, "game:clicker");
        rig.Wait(10);
        rig.Clock.Set("c2", "оля", W, "game:clicker");
        rig.Wait(50);
        rig.Clock.Drop("c1");
        rig.Wait(40);
        rig.Clock.Set("c2", "Оля", W, null);
        rig.Clock.Flush();

        Assert.Equal(100, rig.Seconds("оля", "game:clicker"));
    }

    [Fact]
    public void Moving_between_places_splits_time_while_site_keeps_running()
    {
        using var rig = new Rig();
        rig.Clock.Set("c1", "Оля", PlayClock.Site, PlayClock.Site);
        rig.Clock.Set("c1", "Оля", W, "game:melody");
        rig.Wait(120);
        rig.Clock.Set("c1", "Оля", PlayClock.Site, PlayClock.Site);   // те саме місце — відрізок не рветься
        rig.Clock.Set("c1", "Оля", W, "lobby");
        rig.Wait(45);
        rig.Clock.Drop("c1");
        rig.Clock.Flush();

        Assert.Equal(120, rig.Seconds("оля", "game:melody"));
        Assert.Equal(45, rig.Seconds("оля", "lobby"));
        Assert.Equal(165, rig.Seconds("оля", "site"));
    }

    [Fact]
    public void Listening_is_its_own_channel_and_does_not_stop_the_game()
    {
        using var rig = new Rig();
        rig.Clock.Set("c1", "Оля", W, "game:clicker");
        rig.Clock.Set("c1", "Оля", PlayClock.Listen, PlayClock.Listen);
        rig.Wait(100);
        rig.Clock.Set("c1", "Оля", PlayClock.Listen, null);
        rig.Wait(20);
        rig.Clock.Drop("c1");
        rig.Clock.Flush();

        Assert.Equal(120, rig.Seconds("оля", "game:clicker"));
        Assert.Equal(100, rig.Seconds("оля", "listen"));
    }

    [Fact]
    public void Idle_tail_is_cut_even_after_it_was_already_written()
    {
        using var rig = new Rig();
        rig.Clock.Set("c1", "Оля", W, "game:clicker");
        rig.Wait(60);
        rig.Clock.Flush();                                   // 60 с уже в базі
        rig.Wait(300);
        rig.Clock.Flush();                                   // 360 с у базі, хоча останні 5 хв людини не було
        rig.Clock.Set("c1", "Оля", W, null, idleMs: 300_000);
        rig.Clock.Flush();

        Assert.Equal(60, rig.Seconds("оля", "game:clicker"));
    }

    [Fact]
    public void Idle_tail_never_goes_below_the_start_of_the_run()
    {
        using var rig = new Rig();
        rig.Clock.Set("c1", "Оля", W, "game:clicker");
        rig.Wait(20);
        rig.Clock.Set("c1", "Оля", W, null, idleMs: 3_600_000);
        rig.Clock.Flush();

        Assert.Empty(rig.Eco.Store.TimeTotals());
    }

    [Fact]
    public void Idle_of_one_tab_does_not_cut_while_another_is_still_there()
    {
        using var rig = new Rig();
        rig.Clock.Set("c1", "Оля", W, "game:clicker");
        rig.Clock.Set("c2", "Оля", W, "game:clicker");
        rig.Wait(400);
        rig.Clock.Set("c1", "Оля", W, null, idleMs: 300_000);
        rig.Wait(100);
        rig.Clock.Drop("c2");
        rig.Clock.Flush();

        Assert.Equal(500, rig.Seconds("оля", "game:clicker"));
    }

    [Fact]
    public void Drop_ends_every_channel_of_the_connection()
    {
        using var rig = new Rig();
        rig.Clock.Set("c1", "Оля", W, "page");
        rig.Clock.Set("c1", "Оля", PlayClock.Site, PlayClock.Site);
        rig.Clock.Set("c1", "Оля", PlayClock.Listen, PlayClock.Listen);
        rig.Wait(30);
        rig.Clock.Drop("c1");
        rig.Wait(300);
        rig.Clock.Flush();

        Assert.Equal(30, rig.Seconds("оля", "page"));
        Assert.Equal(30, rig.Seconds("оля", "site"));
        Assert.Equal(30, rig.Seconds("оля", "listen"));
    }

    [Fact]
    public void Time_lands_on_the_kyiv_day_of_the_flush()
    {
        using var rig = new Rig();
        rig.Eco.Clock.UtcNow = new DateTimeOffset(2026, 9, 10, 20, 59, 0, TimeSpan.Zero);   // 23:59 за Києвом
        rig.Clock.Set("c1", "Оля", W, "game:clicker");
        rig.Wait(30);
        rig.Clock.Flush();
        rig.Wait(60);                                                                         // уже 00:00:30
        rig.Clock.Drop("c1");
        rig.Clock.Flush();

        Assert.Equal(30, rig.Eco.Store.TimeTotals("2026-09-10").Sum(r => r.Seconds) - rig.Eco.Store.TimeTotals("2026-09-11").Sum(r => r.Seconds));
        Assert.Equal(60, rig.Eco.Store.TimeTotals("2026-09-11").Sum(r => r.Seconds));
        Assert.Equal("2026-09-10", rig.Eco.Store.TimeSince());
    }

    [Fact]
    public void Nobody_and_nowhere_count_nothing()
    {
        using var rig = new Rig();
        rig.Clock.Set("c1", "  ", W, "game:clicker");
        rig.Clock.Set("c2", "Оля", W, "");
        rig.Wait(60);
        rig.Clock.Flush();

        Assert.Empty(rig.Eco.Store.TimeTotals());
    }

    [Fact]
    public void Time_page_groups_places_by_person_and_game()
    {
        using var rig = new Rig();
        rig.Eco.Economy.Grant("Оля", 1, "test", "t1");   // гаманець дає нікові справжнє написання
        rig.Clock.Set("c1", "Оля", PlayClock.Site, PlayClock.Site);
        rig.Clock.Set("c1", "Оля", W, "game:tron");
        rig.Wait(600);
        rig.Clock.Set("c1", "Оля", W, "watch:tron");
        rig.Wait(60);
        rig.Clock.Set("c1", "Оля", W, "lobby");
        rig.Wait(30);
        rig.Clock.Set("c2", "Петро", PlayClock.Site, PlayClock.Site);
        rig.Clock.Set("c2", "Петро", W, "game:tron");
        rig.Wait(120);
        rig.Clock.Flush();

        var json = JsonSerializer.SerializeToElement(rig.Eco.Boards.Time("all"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var people = json.GetProperty("people").EnumerateArray().ToList();
        Assert.Equal("Оля", people[0].GetProperty("nick").GetString());
        Assert.Equal(810, people[0].GetProperty("site").GetInt32());
        Assert.Equal(600, people[0].GetProperty("play").GetInt32());
        Assert.Equal(60, people[0].GetProperty("watch").GetInt32());
        Assert.Equal(150, people[0].GetProperty("lobby").GetInt32());
        Assert.Equal("петро", people[1].GetProperty("nick").GetString());   // гаманця нема — ключ

        var tron = json.GetProperty("games").EnumerateArray().Single();
        Assert.Equal(720, tron.GetProperty("play").GetInt32());
        Assert.Equal(60, tron.GetProperty("watch").GetInt32());
        Assert.Equal(["Оля", "петро"], tron.GetProperty("people").EnumerateArray().Select(p => p.GetProperty("nick").GetString()).ToArray());
    }
}
