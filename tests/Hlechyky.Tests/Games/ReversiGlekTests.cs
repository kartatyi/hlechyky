using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Реверсі з Глеком: соло-кімната, налаштування, пауза, паси, кінець партії, сила рівнів.</summary>
public class ReversiGlekTests
{
    static RoomHarness Open(int seed = 42)
    {
        var h = new RoomHarness("reversi-glek", seed: seed);
        Assert.True(h.Solo("Оля").Ok);
        return h;
    }

    static int Cell(string sq) => ReversiCore.Parse(sq)!.Value;
    static int[] Ints(JsonElement e) => [.. e.EnumerateArray().Select(x => x.GetInt32())];

    static string Board(params (char P, string Sq)[] discs)
    {
        var b = Enumerable.Repeat('.', 64).ToArray();
        foreach (var (p, sq) in discs) b[Cell(sq)] = p;
        return new string(b);
    }

    static void Put(RoomHarness h, int side, string board)
    {
        lock (h.Room.Sync) h.Room.Game.Load(JsonSerializer.Serialize(new Reversi.Position(board, side)));
    }

    [Fact]
    public void Solo_table_is_not_rated_and_not_in_the_lobby()
    {
        var info = new ReversiGlek().Info;
        Assert.True(info.Solo);
        Assert.True(info.Private);
        Assert.False(info.Rated);
        Assert.Equal(0, info.TickMs % 10);
        Assert.Equal("reversi", info.Client);
    }

    [Fact]
    public void First_game_human_is_black_and_glek_answers_after_a_pause()
    {
        var h = Open();
        var v = h.View(0);
        Assert.Equal("b", v.GetProperty("me").GetString());
        Assert.Equal(0, v.GetProperty("turn").GetInt32());
        Assert.Equal(4, v.GetProperty("legal").GetArrayLength());
        Assert.Equal("easy", v.GetProperty("glek").GetProperty("level").GetString());
        Assert.Equal("turn", v.GetProperty("glek").GetProperty("color").GetString());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("clock").ValueKind);

        Assert.True(h.Act(0, "move", new { cell = Cell("d3") }).Ok);
        var r = h.Act(0, "move", new { cell = Cell("c3") });
        Assert.Equal("Глек думає — зачекай хвильку", r.Message);
        v = h.View(0);
        Assert.Equal(1, v.GetProperty("turn").GetInt32());
        Assert.Equal("w", v.GetProperty("toMove").GetString());
        Assert.Empty(Ints(v.GetProperty("legal")));   // не твій хід — ходів не підсвічуємо
        Assert.True(v.GetProperty("glek").GetProperty("thinking").GetBoolean());
        h.Tick(2);
        Assert.Equal(1, h.View(0).GetProperty("moves").GetInt32());   // ще «думає»
        h.Tick(15);
        v = h.View(0);
        Assert.Equal(2, v.GetProperty("moves").GetInt32());
        Assert.Equal(0, v.GetProperty("turn").GetInt32());
        Assert.NotEmpty(Ints(v.GetProperty("flipped")));
        Assert.True(v.GetProperty("legal").GetArrayLength() > 0);
    }

    [Fact]
    public void Choosing_white_restarts_and_glek_opens()
    {
        var h = Open();
        Assert.True(h.Act(0, "move", new { cell = Cell("d3") }).Ok);
        var r = h.Act(0, "set", new { color = "white", level = "hard" });
        Assert.True(r.Ok);
        Assert.Equal("Нова партія — ти білими, Глек починає", r.Message);
        var v = h.View(0);
        Assert.Equal("w", v.GetProperty("me").GetString());
        Assert.Equal(1, v.GetProperty("turn").GetInt32());
        Assert.Equal(0, v.GetProperty("moves").GetInt32());
        Assert.Equal("hard", v.GetProperty("glek").GetProperty("level").GetString());
        h.Tick(15);
        v = h.View(0);
        Assert.Equal(1, v.GetProperty("moves").GetInt32());
        Assert.Equal(0, v.GetProperty("turn").GetInt32());
        Assert.Equal('b', v.GetProperty("board").GetString()![v.GetProperty("last").GetInt32()]);
    }

    [Fact]
    public void Bad_settings_are_refused_and_change_nothing()
    {
        var h = Open();
        Assert.True(h.Act(0, "move", new { cell = Cell("d3") }).Ok);
        var before = h.View(0).GetRawText();
        Assert.Equal("Не зрозумів налаштування", h.Act(0, "set", new { level = "insane" }).Message);
        Assert.False(h.Act(0, "set", new { color = "red" }).Ok);
        Assert.False(h.Act(0, "set", null).Ok);
        Assert.False(h.Act(0, "dance").Ok);
        Assert.Equal(before, h.View(0).GetRawText());
        Assert.True(h.Act(0, "set", new { level = "medium" }).Ok);
        Assert.Equal("medium", h.View(0).GetProperty("glek").GetProperty("level").GetString());
        Assert.Equal(0, h.View(0).GetProperty("moves").GetInt32());   // нова партія
    }

    [Fact]
    public void Illegal_moves_are_refused()
    {
        var h = Open();
        var before = h.View(0).GetRawText();
        Assert.Equal("Поле вже зайняте", h.Act(0, "move", new { cell = Cell("d4") }).Message);
        Assert.Equal("Так не можна — нічого не перевертається", h.Act(0, "move", new { cell = 0 }).Message);
        Assert.Equal("Не зрозумів, куди ставити", h.Act(0, "move", new { cell = 99 }).Message);
        Assert.Equal(before, h.View(0).GetRawText());
    }

    [Fact]
    public void Rematch_swaps_colours_when_by_turn()
    {
        var h = Open();
        Assert.True(h.Act(0, "resign").Ok);
        Assert.Equal(1, h.View(0).GetProperty("result").GetProperty("winner").GetInt32());
        Assert.Equal("resign", h.View(0).GetProperty("result").GetProperty("reason").GetString());
        Assert.True(h.Rematch().Ok);
        Assert.Equal("w", h.View(0).GetProperty("me").GetString());
        Assert.True(h.Act(0, "resign").Ok);
        Assert.True(h.Rematch().Ok);
        Assert.Equal("b", h.View(0).GetProperty("me").GetString());
    }

    [Fact]
    public void Glek_passes_and_the_human_finishes_with_a_wipe()
    {
        var h = Open();
        Put(h, 0, Board(('b', "a1"), ('w', "b1"), ('b', "a8"), ('w', "b8")));
        Assert.True(h.Act(0, "move", new { cell = Cell("c1") }).Ok);
        h.Tick();
        var v = h.View(0);
        Assert.Equal(0, v.GetProperty("turn").GetInt32());
        Assert.Equal(1, v.GetProperty("pass").GetInt32());
        Assert.Contains(h.Outbox.OfType<Journal>(), j => j.Text == "🤖 Глек пасує");
        Assert.True(h.Act(0, "move", new { cell = Cell("c8") }).Ok);
        h.Tick();
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        var r = h.View(0).GetProperty("result");
        Assert.Equal("wipe", r.GetProperty("reason").GetString());
        Assert.Equal(6, r.GetProperty("b").GetInt32());
        Assert.Empty(h.Awards);   // соло нагород не дає
    }

    [Fact]
    public void The_human_passes_and_glek_finishes_the_game()
    {
        var h = Open();
        Assert.True(h.Act(0, "set", new { color = "black", level = "medium" }).Ok);
        // Черга білих (Глека): у нього c1 і c8, а в людини після першого ж його ходу — нічого.
        Put(h, 1, Board(('w', "a1"), ('b', "b1"), ('w', "a8"), ('b', "b8")));
        h.Tick(40);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal(1, h.View(0).GetProperty("result").GetProperty("winner").GetInt32());   // Глека за столом нема — у Result його теж нема
        Assert.Contains(h.Outbox.OfType<Journal>(), j => j.Text == "⚫ ти пасуєш — ходити нікуди");
        Assert.StartsWith("Реверсі з Глеком: Оля ⚫ 0 : 6 ⚪ Глек (звичайний)", h.Outbox.OfType<Journal>().Last(j => j.Text.StartsWith("Реверсі")).Text);
    }

    /// <summary>Людина ходить першим законним ходом, Глек — сам; уся партія до кінця.</summary>
    static (string Board, string[] Log) PlayOut(int seed, string level)
    {
        var h = Open(seed);
        Assert.True(h.Act(0, "set", new { level }).Ok);
        for (var guard = 0; guard < 2000 && h.Room.Status == RoomStatus.Playing; guard++)
        {
            var v = h.View(0);
            var legal = Ints(v.GetProperty("legal"));
            if (legal.Length > 0) Assert.True(h.Act(0, "move", new { cell = legal[^1] }).Ok);
            else h.Tick();
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        return (h.View(0).GetProperty("board").GetString()!, [.. h.Outbox.OfType<Journal>().Select(j => j.Text)]);
    }

    [Theory]
    [InlineData("easy")]
    [InlineData("medium")]
    public void Same_seed_same_game(string level)
    {
        var a = PlayOut(11, level);
        var b = PlayOut(11, level);
        Assert.Equal(a.Board, b.Board);
        Assert.Equal(a.Log, b.Log);
    }

    // ---------- двигун ----------

    [Fact]
    public void Glek_takes_corners_when_it_can()
    {
        // 20 випадкових позицій серединки, де в того, чия черга, є кут: звичайний бере його майже завжди. Сильного
        // тут не міряємо — у випадкових (перекошених) позиціях він часто бачить прямий виграш повз кут; його сила —
        // у дуелях нижче.
        var spots = new List<ReversiCore>();
        for (var seed = 1; spots.Count < 20 && seed < 500; seed++)
        {
            var rng = new Random(seed);
            var c = ReversiCore.Start();
            while (!c.Over && c.Empties > 16)
            {
                if (c.Legal(c.Side).Any(ReversiEngine.IsCorner)) { spots.Add(c.Clone()); break; }
                var legal = c.Legal(c.Side);
                c.Play(legal[rng.Next(legal.Length)]);
                c.PassIfStuck();
            }
        }
        Assert.Equal(20, spots.Count);
        int Took(ReversiEngine.Level lv) => spots.Count(c => ReversiEngine.IsCorner(ReversiEngine.Pick(c, lv, new Random(1))));
        var medium = Took(ReversiEngine.Medium);
        Assert.True(medium >= 15, $"звичайний узяв кут {medium} з 20");
    }

    [Fact]
    public void Pick_returns_minus_one_when_there_is_no_move()
    {
        var c = ReversiCore.FromString(Board(('b', "a1"), ('w', "h8")), 0)!;
        Assert.Equal(-1, ReversiEngine.Pick(c, ReversiEngine.Hard, new Random(1)));
    }

    [Fact]
    public void Hard_glek_plays_the_endgame_perfectly()
    {
        // Випадкова партія до 10 порожніх: сильний хід не гірший за найкращий точний результат.
        var rng = new Random(3);
        var c = ReversiCore.Start();
        while (c.Empties > 10 && !c.Over)
        {
            var legal = c.Legal(c.Side);
            if (legal.Length == 0) { c.PassIfStuck(); continue; }
            c.Play(legal[rng.Next(legal.Length)]);
            c.PassIfStuck();
        }
        Assert.False(c.Over);
        var best = c.Legal(c.Side).Max(m => { var x = c.Clone(); x.Play(m); return -Solve(x); });
        var pick = ReversiEngine.Pick(c, ReversiEngine.Hard, new Random(1));
        var y = c.Clone();
        y.Play(pick);
        Assert.Equal(best, -Solve(y));
    }

    /// <summary>Точна різниця фішок для того, чия черга (повний перебір, без відсікань).</summary>
    static int Solve(ReversiCore c)
    {
        var legal = c.Legal(c.Side);
        if (legal.Length == 0)
        {
            if (!c.HasMove(1 - c.Side)) return c.Count(c.Side) - c.Count(1 - c.Side);
            var p = c.Clone();
            p.Side = 1 - p.Side;
            return -Solve(p);
        }
        var best = int.MinValue;
        foreach (var m in legal)
        {
            var x = c.Clone();
            x.Play(m);
            best = Math.Max(best, -Solve(x));
        }
        return best;
    }

    /// <summary>Партія двигун проти двигуна; повертає різницю фішок з погляду <paramref name="a"/>.</summary>
    static int Duel(ReversiEngine.Level a, ReversiEngine.Level b, bool aBlack, int seed)
    {
        var rng = new Random(seed);
        var c = ReversiCore.Start();
        var aSide = aBlack ? 0 : 1;
        while (!c.Over)
        {
            var m = ReversiEngine.Pick(c, c.Side == aSide ? a : b, rng);
            Assert.True(m >= 0);
            Assert.NotEqual(0UL, c.Play(m));
            c.PassIfStuck();
        }
        return c.Count(aSide) - c.Count(1 - aSide);
    }

    [Fact]
    public void Medium_beats_easy_most_of_the_time()
    {
        var wins = Enumerable.Range(0, 10).Count(i => Duel(ReversiEngine.Medium, ReversiEngine.Easy, i % 2 == 0, 100 + i) > 0);
        Assert.True(wins >= 7, $"звичайний виграв у легкого {wins} з 10");
    }

    [Fact]
    public void Hard_beats_medium_more_often_than_not()
    {
        var diffs = Enumerable.Range(0, 10).Select(i => Duel(ReversiEngine.Hard, ReversiEngine.Medium, i % 2 == 0, 300 + i)).ToList();
        Assert.True(diffs.Count(d => d > 0) >= 6, $"сильний проти звичайного: {string.Join(", ", diffs)}");
    }

    [Fact]
    public void Hard_beats_easy_most_of_the_time()
    {
        var wins = Enumerable.Range(0, 10).Count(i => Duel(ReversiEngine.Hard, ReversiEngine.Easy, i % 2 == 0, 200 + i) > 0);
        Assert.True(wins >= 8, $"сильний виграв у легкого {wins} з 10");
    }
}

[Collection(SerialPerf.Name)]
public class ReversiGlekPerfTests
{
    /// <summary>Позиція після n випадкових ходів від початку (сід фіксований).</summary>
    static ReversiCore After(int n, int seed)
    {
        var rng = new Random(seed);
        var c = ReversiCore.Start();
        for (var i = 0; i < n && !c.Over; i++)
        {
            var legal = c.Legal(c.Side);
            c.Play(legal[rng.Next(legal.Length)]);
            c.PassIfStuck();
        }
        return c;
    }

    [Theory]
    [Trait("Category", "Perf")]
    [InlineData(20)]   // серединка: найбільше ходів
    [InlineData(30)]
    [InlineData(47)]   // 13 порожніх — ще не точна кінцівка
    [InlineData(48)]   // 12 порожніх — точна кінцівка
    public void Hard_glek_thinks_about_100_ms_at_most(int plies)
    {
        var worst = 0L;
        for (var seed = 1; seed <= 4; seed++)
        {
            var c = After(plies, seed);
            if (c.Over) continue;
            ReversiEngine.Pick(c, ReversiEngine.Hard, new Random(1));   // прогрів JIT
            var sw = Stopwatch.StartNew();
            ReversiEngine.Pick(c, ReversiEngine.Hard, new Random(2));
            worst = Math.Max(worst, sw.ElapsedMilliseconds);
        }
        Assert.True(worst <= 120, $"сильний Глек думав {worst} мс");   // Debug-збірка, запас на шум
    }
}
