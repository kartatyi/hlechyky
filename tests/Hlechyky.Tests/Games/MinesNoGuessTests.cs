using System.Diagnostics;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>Пакет 4 (20.10): поле дня без вгадування — розв'язувач, генератор від сіду дня, старі дні не змінились.</summary>
public class MinesNoGuessTests(ITestOutputHelper output)
{
    static MinesBoard Board(int w, int h, params int[] mines)
    {
        var b = new MinesBoard(w, h, mines.Length);
        b.Place(mines);
        return b;
    }

    [Fact]
    public void Solver_needs_the_pair_rule_for_one_two_one()
    {
        // 3×3: міни в кутах верхнього рядка, знизу нулі. Відкрито «1 2 1» — одиночні правила мовчать, пара каже: правий кут — міна.
        var b = Board(3, 3, 0, 2);
        Assert.True(MinesSolver.Solvable(b, 7));
        Assert.Equal(b.Safe, MinesSolver.Progress(b, 7));
    }

    [Fact]
    public void Solver_stops_at_a_fifty_fifty()
    {
        // 5×2: одна міна в правому стовпчику — «1» над «1», дві закриті клітинки: чисте 50/50.
        var b = Board(5, 2, 4);
        Assert.False(MinesSolver.Solvable(b, 0));
        Assert.Equal(b.Safe - 1, MinesSolver.Progress(b, 0));
    }

    [Fact]
    public void Solver_uses_the_global_mine_count_at_the_end()
    {
        // Те саме 50/50, але друга закрита клітинка відгороджена: міна одна, і її знайдено — решта чиста.
        var b = Board(5, 3, 4);   // (4,0) міна; (3,2) = 0 відкриває правий низ — далі логіка
        Assert.True(MinesSolver.Solvable(b, 0));
    }

    [Fact]
    public void Solver_never_opens_a_mine_on_random_fields()
    {
        var solved = 0;
        for (var s = 0; s < 200; s++)
        {
            var b = new MinesBoard(16, 16, 40);
            b.Generate(new Random(s), MinesDaily.Center);
            var p = MinesSolver.Progress(b, MinesDaily.Center);   // кидає, якщо вивів міну як чисту
            Assert.InRange(p, 9, b.Safe);
            if (p == b.Safe) solved++;
        }
        output.WriteLine($"розв'язних випадкових 16×16/40: {solved}/200");
        Assert.InRange(solved, 1, 199);
    }

    [Fact]
    public void Field_of_a_no_guess_day_is_deterministic_and_solvable()
    {
        const string day = "2026-10-25";
        var a = MinesFields.Compute("mines", day, 16, 16, 40, MinesDaily.Center);
        var b = MinesFields.Compute("mines", day, 16, 16, 40, MinesDaily.Center);
        Assert.True(a.NoGuess);
        Assert.Equal(a.Mines, b.Mines);
        Assert.Equal(a.Tries, b.Tries);
        Assert.Equal(40, a.Mines.Length);
        Assert.DoesNotContain(MinesDaily.Center, a.Mines);

        var field = MinesDaily.Field(day);
        Assert.Equal(a.Mines, field.MineCells());
        Assert.True(MinesSolver.Solvable(field, MinesDaily.Center));
        Assert.NotEqual(a.Mines, MinesFields.Compute("mines", "2026-10-26", 16, 16, 40, MinesDaily.Center).Mines);
    }

    [Fact]
    public void Days_before_the_switch_keep_the_old_field()
    {
        foreach (var day in new[] { "2026-09-10", "2026-10-20", "2026-10-21" })
        {
            Assert.False(MinesDaily.NoGuess(day));
            var old = new MinesBoard(16, 16, 40);
            old.Generate(new Random(Days.Seed("mines", day)), MinesDaily.Center);
            Assert.Equal(old.MineCells(), MinesDaily.Field(day).MineCells());
        }
        Assert.True(MinesDaily.NoGuess(MinesDaily.NoGuessFrom));
    }

    [Fact]
    public void Room_on_a_no_guess_day_deals_the_cached_field()
    {
        var h = new RoomHarness("mines-daily");
        h.Clock.UtcNow = new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
        Assert.True(h.Solo("Оля").Ok);
        Assert.True(MinesFields.Ready("mines", "2026-10-25", 16, 16, 40));
        var field = MinesDaily.Field("2026-10-25");
        field.Open(MinesDaily.Center);
        Assert.Equal(field.Text(false), h.View(0).GetProperty("cells").GetString());
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Measure_generator()
    {
        foreach (var (w, hh, m, center, puzzle) in new[] { (16, 16, 40, MinesDaily.Center, "mines"), (9, 9, 10, 4 * 9 + 4, "mines-easy") })
        {
            var tries = 0;
            var ms = 0.0;
            var fails = 0;
            const int days = 60;
            var d0 = new DateOnly(2026, 10, 22);
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < days; i++)
            {
                var l = MinesFields.Compute(puzzle, d0.AddDays(i).ToString("yyyy-MM-dd"), w, hh, m, center);
                tries += l.Tries;
                ms += l.Ms;
                if (!l.NoGuess) fails++;
            }
            output.WriteLine($"{w}×{hh}/{m}: кандидатів у середньому {tries / (double)days:0.0}, {ms / days:0.0} мс, без розв'язку {fails}/{days}");
            Assert.Equal(0, fails);
            Assert.True(sw.ElapsedMilliseconds < 60_000);
        }
    }
}
