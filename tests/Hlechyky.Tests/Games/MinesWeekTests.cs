using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hlechyky.Tests.Games;

/// <summary>Пакет 4 (20.10): залік тижня Сапера дня — сума місць за 7 днів, пропуск = «розв'язків того дня + 1».</summary>
public class MinesWeekTests
{
    static string[] Names(IReadOnlyList<MinesWeekRow> rows) => [.. rows.Select(r => $"{r.Nick} {r.Sum}")];

    [Fact]
    public void Week_sums_places_with_penalty_for_missed_days()
    {
        using var rig = new EconomyRig();   // сьогодні 2026-09-10
        rig.Daily.Record("mines-daily", "Зоя", solved: true, attempts: 1, ms: 10_000, day: "2026-09-03");    // 8 днів тому — поза вікном
        rig.Daily.Record("mines-daily", "Марко", solved: true, attempts: 1, ms: 90_000, day: "2026-09-04");  // край вікна
        rig.Daily.Record("mines-daily", "Оля", solved: true, attempts: 1, ms: 60_000, day: "2026-09-09");
        rig.Daily.Record("mines-daily", "Петро", solved: true, attempts: 2, ms: 30_000, day: "2026-09-09");
        rig.Daily.Record("mines-daily", "Іван", solved: false, attempts: 3, ms: 0, day: "2026-09-09");
        rig.Daily.Record("mines-daily", "Петро", solved: true, attempts: 1, ms: 50_000, day: "2026-09-10");
        rig.Daily.Record("mines-daily-easy", "Оля", solved: true, attempts: 1, ms: 5_000, day: "2026-09-10");   // легке поле — не в залік

        var week = new MinesWeek(rig.Db, rig.Clock);
        week.LoadNow();
        // 04.09: Марко 1, решта 2 · 05–08.09: усім по 1 · 09.09: Оля 1, Петро 2, Іван і Марко 3 · 10.09: Петро 1, решта 2
        Assert.Equal(["Оля 9", "Петро 9", "Марко 10", "Іван 11"], Names(week.Get()!));

        // свіжий розв'язок кімнати видно одразу, ще до бази
        week.Note("Оля", solved: true, attempts: 1, ms: 40_000);
        Assert.Equal(["Оля 8", "Петро 10", "Марко 11", "Іван 12"], Names(week.Get()!));
    }

    [Fact]
    public void Same_attempts_and_time_share_a_place_and_the_list_is_capped()
    {
        var rows = new List<MinesWeek.Result>
        {
            new("а", "А", "2026-09-10", true, 1, 50_000),
            new("б", "Б", "2026-09-10", true, 1, 50_000),
            new("в", "В", "2026-09-10", true, 2, 10_000),
        };
        for (var i = 0; i < 6; i++) rows.Add(new($"x{i}", $"Я{i}", "2026-09-10", false, 1, 0));
        var week = MinesWeek.Rank(rows, "2026-09-10");
        Assert.Equal(MinesWeek.MaxRows, week.Count);
        Assert.Equal(["А 7", "Б 7", "В 9"], Names(week).Take(3));   // 6 порожніх днів по 1 + місце дня
        Assert.Equal("Я0 10", Names(week)[3]);
    }

    [Fact]
    public void Solving_in_the_room_puts_me_into_the_week_and_the_easy_field_does_not()
    {
        var clock = new FakeClock();
        var week = new MinesWeek(null, clock);
        var card = new DailyCard(null, clock);
        var sp = new ServiceCollection().AddSingleton(card).AddSingleton(week).AddSingleton(new MinesGhosts(null, clock)).BuildServiceProvider();

        var easy = new RoomHarness("mines-daily-easy", services: sp);
        Assert.True(easy.Solo("Оля").Ok);
        MinesDailyP4Tests.Solve(easy);
        Assert.Empty(week.Get()!);

        var big = new RoomHarness("mines-daily", services: sp);
        Assert.True(big.Solo("Петро").Ok);
        MinesDailyP4Tests.Solve(big);
        var w = big.View(0).GetProperty("week");
        Assert.Equal("Петро", w[0].GetProperty("n").GetString());
        Assert.Equal(7, w[0].GetProperty("s").GetInt32());
    }
}
