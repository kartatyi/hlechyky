using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit;

namespace Hlechyky.Tests.Games;

/// <summary>Китайські шашки на зірці (прохід №3, пункт 207).</summary>
public class ZirkaTests
{
    static RoomHarness Table(int players = 2, int seed = 42)
    {
        var h = new RoomHarness("zirka", seed: seed);
        foreach (var nick in new[] { "Оля", "Петро", "Іра" }.Take(players)) h.Join(nick);
        Assert.True(h.Start().Ok);
        return h;
    }

    static Zirka Game(RoomHarness h) => (Zirka)h.Room.Game;
    static int Cell(int q, int r) => Array.IndexOf(Zirka.Cells, (q, r));
    static string Cells(RoomHarness h) => h.View(0).GetProperty("cells").GetString()!;

    static void Put(RoomHarness h, string board, int turn, int n = 2, int[]? players = null)
    {
        lock (h.Room.Sync) Game(h).Load(JsonSerializer.Serialize(new Zirka.State(board, n, turn, 0, null, null, null, new bool[3], players)));
    }

    static string Empty() => new('.', Zirka.Cells.Length);

    static string With(params (int Cell, char P)[] pegs)
    {
        var b = Empty().ToCharArray();
        foreach (var (c, p) in pegs) b[c] = p;
        return new string(b);
    }

    [Fact]
    public void Star_has_121_holes_and_ten_pegs_per_player()
    {
        Assert.Equal(121, Zirka.Cells.Length);
        var two = Cells(Table(2));
        Assert.Equal(10, two.Count(c => c == '0'));
        Assert.Equal(10, two.Count(c => c == '1'));
        var three = Cells(Table(3));
        foreach (var p in "012") Assert.Equal(10, three.Count(c => c == p));
    }

    [Fact]
    public void Step_and_chain_of_jumps_are_legal_but_a_long_slide_is_not()
    {
        var h = Table();
        // Фішка на (0,0); сусід на (1,0), ще один через лунку на (3,0) — стрибок (0,0)→(2,0)→(4,0).
        Put(h, With((Cell(0, 0), '0'), (Cell(1, 0), '1'), (Cell(3, 0), '0'), (Cell(-4, 8), '1')), 0);
        Assert.True(h.Act(0, "move", new { from = Cell(0, 0), to = Cell(4, 0) }).Ok);
        Assert.Equal(new[] { Cell(0, 0), Cell(2, 0), Cell(4, 0) }, h.View(0).GetProperty("last").EnumerateArray().Select(x => x.GetInt32()));
        Assert.False(h.Act(1, "move", new { from = Cell(1, 0), to = Cell(1, -3) }).Ok);   // так далеко не можна
        Assert.True(h.Act(1, "move", new { from = Cell(1, 0), to = Cell(1, -1) }).Ok);    // крок
    }

    [Fact]
    public void Illegal_moves_do_not_change_the_board()
    {
        var h = Table();
        var before = Cells(h);
        Assert.False(h.Act(1, "move", new { from = 0, to = 1 }).Ok);      // не твій хід
        Assert.False(h.Act(0, "move", new { from = 60, to = 61 }).Ok);    // не твоя фішка
        Assert.False(h.Act(0, "move", new { from = "x" }).Ok);
        Assert.Equal(before, Cells(h));
    }

    [Fact]
    public void Filling_the_target_wins()
    {
        var h = Table();
        // Ціль жовтих (місце 0) — верхній кут r ≤ −5. Дев'ять уже там, десята стрибає з (−1,−3)... крок на останню лунку.
        var target = Enumerable.Range(0, 121).Where(i => Zirka.Cells[i].R <= -5).ToList();
        Assert.Equal(10, target.Count);
        var last = target.OrderByDescending(i => Zirka.Cells[i].R).First();
        var (q, r) = Zirka.Cells[last];
        var pegs = target.Where(i => i != last).Select(i => (i, '0')).Append((Cell(q, r + 1), '0')).Append((Cell(-4, 8), '1')).ToArray();
        Put(h, With(pegs), 0);
        Assert.True(h.Act(0, "move", new { from = Cell(q, r + 1), to = last }).Ok);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
    }

    [Fact]
    public void Three_players_take_turns_in_order_and_leaver_pegs_vanish()
    {
        var h = Table(3);
        var v = h.View(0);
        Assert.Equal(3, v.GetProperty("n").GetInt32());
        Assert.Equal(0, v.GetProperty("turn").GetInt32());
        var m = v.GetProperty("moves").EnumerateObject().First();
        Assert.True(h.Act(0, "move", new { from = int.Parse(m.Name), to = m.Value[0].GetInt32() }).Ok);
        Assert.Equal(1, h.View(0).GetProperty("turn").GetInt32());
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.DoesNotContain('1', Cells(h));
        Assert.Equal(2, h.View(0).GetProperty("turn").GetInt32());
    }

    [Fact]
    public void Random_full_games_end_and_stay_fast()
    {
        var sw = Stopwatch.StartNew();
        foreach (var players in new[] { 2, 3 })
        {
            var h = Table(players, seed: players);
            var rng = new Random(players);
            for (var i = 0; i < 2000 && h.Room.Status == RoomStatus.Playing; i++)
            {
                var v = h.View(0);
                var seat = v.GetProperty("turn").GetInt32();
                var opts = v.GetProperty("moves").EnumerateObject().ToList();
                // Жадібно вперед до цілі: найбільший крок у бік свого кута, інколи випадково.
                var best = opts.SelectMany(o => o.Value.EnumerateArray().Select(t => (From: int.Parse(o.Name), To: t.GetInt32())))
                    .OrderByDescending(x => Progress(v, seat, x.From, x.To) + rng.NextDouble() * 0.5).First();
                Assert.True(h.Act(seat, "move", new { from = best.From, to = best.To }).Ok);
            }
            Assert.Equal(RoomStatus.Finished, h.Room.Status);
        }
        Assert.True(sw.ElapsedMilliseconds < 5000, $"{sw.ElapsedMilliseconds} мс");
    }

    /// <summary>Наскільки хід наближає фішку до свого цільового кута (за віссю домівки).</summary>
    static double Progress(JsonElement v, int seat, int from, int to)
    {
        var home = v.GetProperty("homes").EnumerateArray().First(x => x.GetProperty("seat").GetInt32() == seat);
        int axis = home.GetProperty("axis").GetInt32(), sign = home.GetProperty("sign").GetInt32();
        double Coord(int c) { var (q, r) = Zirka.Cells[c]; return axis switch { 0 => q, 1 => -q - r, _ => r }; }
        return (Coord(from) - Coord(to)) * sign;
    }

    [Fact]
    public void Save_load_roundtrip()
    {
        var h = Table(3);
        var copy = new Zirka();
        copy.Load(Game(h).Save()!);
        Assert.Equal(Game(h).Save(), copy.Save());
    }
}
