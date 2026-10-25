using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Січ щоденних (25.10.2026): очки «1 + кого обійшов», тиждень пн…нд, підсумок із призами, номінаціями й 👑,
/// вічний Ело, дуелі, «що, якби» і Глек у Балачках.
/// </summary>
public class SichTests
{
    static readonly string[] Games = ["g1", "g2", "g3"];

    sealed class Rig : IDisposable
    {
        public EconomyRig E { get; } = new(people: true);
        public Daily Daily { get; }
        public Sich Sich { get; }

        public Rig(DateTimeOffset? now = null)
        {
            E.Clock.UtcNow = now ?? new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);   // середа 15:00 Київ
            var o = E.Options;
            o.Boost = 10;
            o.SichPrizes = [1000, 500, 250];
            o.SichNomPrize = 50;
            o.SichMinPlayers = 3;
            var names = new GameNames([], Games);
            Daily = new Daily(E.Store, names, E.Clock);
            Sich = new Sich(E.Store, Daily, E.Economy, names, E.GameStore, E.Clock, new FixedOptions<EconomyOptions>(o), E.Outbox,
                NullLogger<Sich>.Instance);
        }

        public string Today => Daily.Today();
        public string Day(int shift) => DateOnly.ParseExact(Today, "yyyy-MM-dd").AddDays(shift).ToString("yyyy-MM-dd");

        public void Solve(string game, string nick, int attempts, string? day = null, int ms = 1000) =>
            Daily.Record(game, nick, solved: true, attempts, ms, day);
        public void Miss(string game, string nick, string? day = null) => Daily.Record(game, nick, solved: false, 6, 0, day);

        public void Dispose() => E.Dispose();
    }

    [Fact]
    public void Score_is_one_plus_everyone_beaten_and_a_miss_is_zero()
    {
        using var rig = new Rig();
        rig.Solve("g1", "Оля", 2);
        rig.Solve("g1", "Петро", 3);
        rig.Solve("g1", "Яся", 3);
        rig.Miss("g1", "Сем");
        var v = rig.Sich.View("Оля");
        int P(string n) => v.Rows.Single(r => r.Nick == n).Points;
        Assert.Equal(4, P("Оля"));     // обійшла трьох
        Assert.Equal(2, P("Петро"));   // з Ясею нічия — обходить лише Сема
        Assert.Equal(2, P("Яся"));
        Assert.Equal(0, P("Сем"));
        Assert.Equal(2, v.Rows.Single(r => r.Nick == "Петро").Place);
        Assert.Equal(2, v.Rows.Single(r => r.Nick == "Яся").Place);
    }

    [Fact]
    public void Week_runs_monday_to_sunday_and_sums_by_day()
    {
        using var rig = new Rig();   // середа 21.10
        rig.Solve("g1", "Оля", 1, rig.Day(-2));   // понеділок
        rig.Solve("g1", "Оля", 1, rig.Day(-3));   // неділя минулого тижня — не в заліку
        rig.Solve("g2", "Оля", 1);
        var v = rig.Sich.View("Оля");
        Assert.Equal("2026-10-19", v.Week);
        Assert.Equal(4, v.DaysLeft);
        var row = Assert.Single(v.Rows);
        Assert.Equal(2, row.Points);
        Assert.Equal([1, 0, 1, 0, 0, 0, 0], row.ByDay);
    }

    [Fact]
    public void Settling_pays_places_and_noms_once_and_crowns_the_winner()
    {
        using var rig = new Rig();
        rig.Solve("g1", "Оля", 1);
        rig.Solve("g1", "Петро", 2);
        rig.Solve("g1", "Яся", 3);
        rig.Solve("g2", "Петро", 1);
        rig.E.Clock.UtcNow = new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);   // понеділок
        Assert.True(rig.Sich.Settle("2026-10-19") > 0);
        // Петро 2 + 1 = 3, Оля 3 — порівну, але в Петра більше розв'язків
        Assert.Equal(10000, rig.E.Paid("Петро", "sich-week"));
        Assert.Equal(5000, rig.E.Paid("Оля", "sich-week"));
        Assert.Equal(2500, rig.E.Paid("Яся", "sich-week"));
        Assert.Equal(["Петро"], rig.Sich.Crown());
        Assert.Contains(rig.E.Outbox.Of<DjSays>(), s => s.Text.Contains("Гетьман тижня: Петро (3)"));
        // повтор не платить і не каже вдруге
        var said = rig.E.Outbox.Of<DjSays>().Count;
        Assert.Equal(0, rig.Sich.Settle("2026-10-19"));
        Assert.Equal(10000, rig.E.Paid("Петро", "sich-week"));
        Assert.Equal(said, rig.E.Outbox.Of<DjSays>().Count);
        // 🔗 Залізна серія — у кого найбільше днів, далі розв'язків: усі по дню, у Петра два розв'язки
        Assert.Equal(500, rig.E.Paid("Петро", "sich-nom:iron"));
        // минулий тиждень віддається знімком
        var past = rig.Sich.View("Оля", "2026-10-21");
        Assert.False(past.Live);
        Assert.Equal("Петро", past.Rows[0].Nick);
        Assert.Contains("2026-10-19", past.Weeks);
    }

    [Fact]
    public void Too_few_players_means_no_prizes_and_no_crown()
    {
        using var rig = new Rig();
        rig.Solve("g1", "Оля", 1);
        rig.Solve("g1", "Петро", 2);
        rig.E.Clock.UtcNow = new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
        rig.Sich.Settle("2026-10-19");
        Assert.Equal(0, rig.E.Paid("Оля", "sich-"));
        Assert.Empty(rig.Sich.Crown());
        Assert.Contains(rig.E.Outbox.Of<DjSays>(), s => s.Text.Contains("без господаря"));
    }

    [Fact]
    public async Task Tick_does_not_settle_weeks_before_sich_existed()
    {
        using var rig = new Rig(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
        rig.Solve("g1", "Оля", 1);
        rig.Solve("g1", "Петро", 2);
        rig.Solve("g1", "Яся", 3);
        rig.E.Clock.UtcNow = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
        await rig.Sich.Tick();
        Assert.Equal(0, rig.E.Paid("Оля", "sich-"));
        Assert.Empty(rig.Sich.View("Оля", "2026-10-14").Weeks);
    }

    [Fact]
    public async Task Elo_moves_winners_up_and_remembers_who_beat_whom()
    {
        using var rig = new Rig();
        rig.Solve("g1", "Оля", 1, rig.Day(-1));
        rig.Solve("g1", "Петро", 3, rig.Day(-1));
        rig.Solve("g2", "Оля", 1, rig.Day(-1));
        rig.Solve("g2", "Петро", 2, rig.Day(-1));
        await rig.Sich.CatchUp();
        var v = rig.Sich.View("Петро");
        var ola = v.Eternal.Single(e => e.Nick == "Оля");
        var petro = v.Eternal.Single(e => e.Nick == "Петро");
        Assert.True(ola.Elo > 1000 && petro.Elo < 1000);
        Assert.InRange(ola.Elo + petro.Elo, 1999, 2001);
        var duel = Assert.Single(v.Me!.Duels);
        Assert.Equal(("Оля", 0, 2, 0, 2), (duel.Nick, duel.W, duel.L, duel.AllW, duel.AllL));
        // підбите лежить у базі: новий сервіс бачить те саме
        var again = new Sich(rig.E.Store, rig.Daily, rig.E.Economy, new GameNames([], Games), rig.E.GameStore, rig.E.Clock,
            new FixedOptions<EconomyOptions>(rig.E.Options), rig.E.Outbox, NullLogger<Sich>.Instance);
        Assert.Equal(ola.Elo, again.View(null).Eternal.Single(e => e.Nick == "Оля").Elo);
    }

    [Fact]
    public void What_if_counts_everyone_already_in_and_the_place_it_would_give()
    {
        using var rig = new Rig();
        rig.Solve("g1", "Оля", 1);
        rig.Solve("g1", "Петро", 2);
        rig.Solve("g2", "Оля", 1);
        rig.Solve("g2", "Петро", 2);
        rig.Solve("g3", "Яся", 1);
        var me = rig.Sich.View("Яся").Me!;
        Assert.Equal(3, me.Place);
        var g1 = me.WhatIf.Single(x => x.Game == "g1");
        Assert.Equal(3, g1.Gain);    // обійти Олю й Петра
        Assert.Equal(1, g1.Place);   // 1 + 3 = 4 — порівну з Олею, вище нікого
        Assert.DoesNotContain(me.WhatIf, x => x.Game == "g3");
    }

    [Fact]
    public void Glek_announces_an_overtake_but_not_the_first_leader_or_at_night()
    {
        using var rig = new Rig();
        rig.Solve("g1", "Оля", 1);
        rig.Solve("g1", "Петро", 2);
        rig.Solve("g1", "Яся", 3);
        rig.Solve("g2", "Оля", 1);
        rig.Solve("g2", "Петро", 2);
        rig.Solve("g2", "Яся", 3);
        rig.Sich.Announce();   // перший лідер тижня — тихо
        Assert.Empty(rig.E.Outbox.Of<DjSays>());
        rig.Solve("g3", "Петро", 1, ms: 500);
        rig.Solve("g3", "Яся", 2);
        rig.Solve("g1", "Сем", 9, rig.Day(-1));
        rig.Solve("g1", "Петро", 1, rig.Day(-1));
        rig.Solve("g1", "Яся", 2, rig.Day(-1));
        rig.Solve("g1", "Оля", 3, rig.Day(-1));
        rig.E.Clock.Advance(TimeSpan.FromMinutes(1));   // кеш тижня
        rig.Sich.Announce();
        var said = Assert.Single(rig.E.Outbox.Of<DjSays>());
        Assert.Contains("новий лідер тижня — Петро", said.Text);
        Assert.Contains("Оля позаду", said.Text);
    }

    [Fact]
    public void Points_puzzles_rank_by_points()
    {
        var rows = new List<Sich.Entry>
        {
            new("d", "g", "a", "А", true, 1, 100, DateTimeOffset.MinValue, 30),
            new("d", "g", "b", "Б", true, 1, 100, DateTimeOffset.MinValue, 50),
            new("d", "g", "c", "В", true, 1, 100, DateTimeOffset.MinValue, null),
        };
        var placed = Sich.Rank(rows, points: true);
        Assert.Equal(["Б", "А", "В"], placed.Select(p => p.E.Nick));
        Assert.Equal([3, 2, 1], placed.Select(p => p.Score));
    }
}
