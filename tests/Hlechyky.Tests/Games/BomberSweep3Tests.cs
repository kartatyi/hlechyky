using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Бомбер, прохід №3 (29.09.2026): мапи й велике поле, стискання, бонуси-хаос, привиди з помстою,
/// команди, стрічка подій і підсумок партії. Правила — на голому ядрі, опції й кадр — через кімнату.
/// </summary>
public class BomberSweep3Tests
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марко", "Зоя"];

    static RoomHarness Table(int players, object? options = null, int seed = 42)
    {
        var h = new RoomHarness("bomber", options, seed: seed);
        foreach (var nick in Nicks.Take(players)) h.Join(nick);
        h.Start();
        return h;
    }

    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString() ?? "";

    static void Ready(RoomHarness h)
    {
        for (var i = 0; i < 200 && Phase(h) != "go"; i++) h.Tick(1);
        Assert.Equal("go", Phase(h));
    }

    /// <summary>Порожнє поле (без ящиків і живих) потрібного розміру й мапи.</summary>
    static BomberCore Empty(int w = BomberCore.W, int h = BomberCore.H, BomberMap map = BomberMap.Classic)
    {
        var core = new BomberCore(new Random(1), w, h, map);
        core.Layout();
        return core;
    }

    static BomberMan Put(BomberCore core, int seat, int x, int y)
    {
        var p = core.Players[seat];
        p.Cell = core.Cell(x, y);
        p.Move = -1;
        p.Step = 0;
        p.Want = -1;
        p.Plays = true;
        p.Alive = true;
        return p;
    }

    static void Run(BomberCore core, int ticks)
    {
        for (var i = 0; i < ticks; i++) core.Step();
    }

    static bool[] All(int n) => [.. Enumerable.Range(0, BomberCore.Seats).Select(i => i < n)];

    /// <summary>Усі не-стіни поля досяжні одна з одної (ящики — прохідні: їх можна розбити).</summary>
    static bool Connected(BomberCore core)
    {
        var free = Enumerable.Range(0, core.Tiles.Length).Where(c => core.Tiles[c] != BomberTile.Wall).ToList();
        var seen = new HashSet<int> { free[0] };
        var q = new Queue<int>(seen);
        while (q.Count > 0)
        {
            var c = q.Dequeue();
            for (var d = 0; d < 4; d++)
            {
                var n = core.Ahead(c, d);
                if (n >= 0 && core.Tiles[n] != BomberTile.Wall && seen.Add(n)) q.Enqueue(n);
            }
        }
        return seen.Count == free.Count;
    }

    // ---------- 36. мапи й велике поле ----------

    [Theory]
    [InlineData(BomberCore.W, BomberCore.H)]
    [InlineData(BomberCore.BigW, BomberCore.BigH)]
    public void Maze_has_long_walls_but_every_passage_is_reachable_and_starts_are_clear(int w, int h)
    {
        var classic = Empty(w, h);
        var maze = new BomberCore(new Random(5), w, h, BomberMap.Maze);
        maze.Reset(All(6));
        Assert.True(maze.BaseWalls.Length > classic.BaseWalls.Length + 5, "лабіринт мав би мати помітно більше стін");
        Assert.True(Connected(maze));
        foreach (var s in maze.Starts)
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    var c = maze.Cell(maze.X(s) + dx, maze.Y(s) + dy);
                    var pillar = maze.X(c) % 2 == 0 && maze.Y(c) % 2 == 0 || maze.X(c) is 0 || maze.Y(c) is 0
                        || maze.X(c) == w - 1 || maze.Y(c) == h - 1;
                    if (!pillar) Assert.Equal(BomberTile.Free, maze.Tiles[c]);
                }
    }

    [Fact]
    public void Open_map_has_far_fewer_boxes_than_classic_with_the_same_seed()
    {
        var classic = new BomberCore(new Random(3));
        classic.Reset(All(4));
        var open = new BomberCore(new Random(3), map: BomberMap.Open);
        open.Reset(All(4));
        Assert.True(open.BoxCells().Length < classic.BoxCells().Length * 0.7,
            $"відкрита {open.BoxCells().Length} проти класики {classic.BoxCells().Length}");
    }

    [Fact]
    public void Big_field_puts_six_starts_on_its_own_corners_and_middles()
    {
        var core = new BomberCore(new Random(1), BomberCore.BigW, BomberCore.BigH);
        core.Reset(All(6));
        Assert.Equal(core.Cell(17, 13), core.Players[1].Cell);
        Assert.Equal(core.Cell(9, 1), core.Players[4].Cell);
        Assert.Equal(core.Cell(9, 13), core.Players[5].Cell);
        Assert.All(core.Players, p => Assert.Equal(BomberTile.Free, core.Tiles[p.Cell]));
        Assert.True(Connected(core));
    }

    [Theory]
    [InlineData("15", 6, 15)]
    [InlineData("19", 2, 19)]
    [InlineData("auto", 4, 15)]
    [InlineData("auto", 5, 19)]
    public void Size_option_picks_the_field(string size, int players, int width)
    {
        var h = Table(players, new { size });
        var v = h.View(null);
        Assert.Equal(width, v.GetProperty("width").GetInt32());
        Assert.Equal(width == 19 ? 15 : 13, v.GetProperty("height").GetInt32());
        Assert.Equal(v.GetProperty("width").GetInt32() * v.GetProperty("height").GetInt32() > 200,
            v.GetProperty("walls").EnumerateArray().Max(e => e.GetInt32()) > 15 * 13);
    }

    [Fact]
    public void Default_table_is_the_old_bomber_without_new_view_fields()
    {
        var h = Table(2);
        var v = h.View(null);
        Assert.Equal(15, v.GetProperty("width").GetInt32());
        Assert.Equal("classic", v.GetProperty("map").GetString());
        foreach (var key in new[] { "shrinkAt", "ghosts", "chaos", "teams", "note", "sum", "sh", "ev" })
            Assert.False(v.TryGetProperty(key, out _), key);
        Ready(h);
        h.Tick(1600);
        // без стискання нових стін нема й на 96-й секунді
        Assert.False(h.View(null).TryGetProperty("sh", out _));
    }

    // ---------- 34. стискання ----------

    [Fact]
    public void Shrink_starts_at_ninety_seconds_and_walls_crush_whoever_stands_there()
    {
        var core = Empty();
        core.Shrink = true;
        Put(core, 0, 1, 1);    // перша клітинка спіралі
        Put(core, 1, 7, 5);    // середина — туди стіни дійдуть в останню чергу
        Run(core, BomberCore.ShrinkFrom - 1);
        Assert.Equal(0, core.Shrunk);
        Assert.True(core.Players[0].Alive);
        core.Step();
        Assert.Equal(1, core.Shrunk);
        Assert.Equal(BomberTile.Wall, core.Tiles[core.Cell(1, 1)]);
        Assert.False(core.Players[0].Alive);
        Assert.Contains(new BomberEvent(BomberHow.Wall, -1, 0), core.Events);
        Assert.True(core.RoundOver);   // лишився один — раунд його
    }

    [Fact]
    public void Shrink_order_is_a_spiral_from_the_edge_that_skips_pillars_and_fills_before_the_draw()
    {
        foreach (var (w, hgt) in new[] { (BomberCore.W, BomberCore.H), (BomberCore.BigW, BomberCore.BigH) })
        {
            var core = Empty(w, hgt);
            Assert.Equal(core.Cell(1, 1), core.ShrinkOrder[0]);
            Assert.Equal(core.Cell(2, 1), core.ShrinkOrder[1]);
            Assert.Equal(core.Cell(w - 2, 2), core.ShrinkOrder[w - 2]);   // верхній ряд скінчився — вниз правим краєм
            Assert.All(core.ShrinkOrder, c => Assert.NotEqual(BomberTile.Wall, core.Tiles[c]));
            Assert.Equal(core.ShrinkOrder.Length, core.ShrinkOrder.Distinct().Count());
            Assert.True(BomberCore.ShrinkFrom + core.ShrinkOrder.Length * core.ShrinkEvery <= BomberCore.RoundTicks,
                $"{w}×{hgt}: стискання не встигає до нічиєї");
        }
    }

    [Fact]
    public void Shrink_option_reaches_the_frame_and_the_view()
    {
        var h = Table(2, new { shrink = "1" });
        Assert.Equal(BomberCore.ShrinkFrom, h.View(null).GetProperty("shrinkAt").GetInt32());
        Ready(h);
        Assert.Equal(0, h.View(null).GetProperty("sh").GetInt32());
    }

    // ---------- 35. бонуси-хаос ----------

    [Fact]
    public void Glove_kicks_a_bomb_that_slides_until_the_wall()
    {
        var core = Empty();
        var p = Put(core, 0, 1, 1);
        p.Kick = true;
        core.Bombs.Add(new BomberBomb { Cell = core.Cell(2, 1), Owner = 1, Range = 2, Fuse = 100 });
        core.Turn(0, 0);
        Run(core, 30);
        // бомба доїхала до правого краю (13,1), а бомбер пішов слідом
        Assert.Equal(core.Cell(13, 1), core.Bombs[0].Cell);
        Assert.Equal(-1, core.Bombs[0].Slide);
        Assert.True(core.X(p.Cell) > 2);
    }

    [Fact]
    public void Without_a_glove_a_bomb_just_blocks_the_way()
    {
        var core = Empty();
        var p = Put(core, 0, 1, 1);
        core.Bombs.Add(new BomberBomb { Cell = core.Cell(2, 1), Owner = 1, Range = 2, Fuse = 100 });
        core.Turn(0, 0);
        Run(core, 20);
        Assert.Equal(core.Cell(2, 1), core.Bombs[0].Cell);
        Assert.Equal(core.Cell(1, 1), p.Cell);
    }

    [Fact]
    public void Reverse_curse_runs_the_other_way_and_passes_to_whoever_you_touch()
    {
        var core = Empty();
        var p = Put(core, 0, 3, 1);
        p.Curse = BomberCurse.Reverse;
        p.CurseLeft = BomberCore.CurseTicks;
        core.Turn(0, 0);                 // праворуч…
        Run(core, 4);
        Assert.Equal(core.Cell(2, 1), p.Cell);   // …а побіг ліворуч
        core.Turn(0, -1);
        var q = Put(core, 1, 1, 1);
        core.Turn(1, 0);
        Run(core, 3);                    // Петро забіг у ту саму клітинку
        Assert.Equal(BomberCurse.None, p.Curse);
        Assert.Equal(BomberCurse.Reverse, q.Curse);
    }

    [Fact]
    public void Bombs_curse_drops_bombs_by_itself_and_ends_after_ten_seconds()
    {
        var core = Empty();
        var p = Put(core, 0, 1, 1);
        p.Bombs = 2;
        p.Curse = BomberCurse.Bombs;
        p.CurseLeft = BomberCore.CurseTicks;
        core.Step();
        Assert.Single(core.Bombs);
        p.Alive = true;
        Run(core, BomberCore.CurseTicks);
        Assert.Equal(BomberCurse.None, p.Curse);
    }

    [Fact]
    public void Chaos_drops_all_five_kinds_and_plain_game_only_three()
    {
        static HashSet<BomberBonus> Kinds(bool chaos)
        {
            var seen = new HashSet<BomberBonus>();
            for (var seed = 0; seed < 60; seed++)
            {
                var core = new BomberCore(new Random(seed)) { Chaos = chaos };
                core.Reset(All(2));
                for (var c = 0; c < core.Tiles.Length; c++)
                    if (core.Tiles[c] == BomberTile.Free) core.Bombs.Add(new BomberBomb { Cell = c, Owner = 0, Range = 1, Fuse = 1 });
                core.Step();
                foreach (var d in core.Drops) seen.Add(d.Kind);
            }
            return seen;
        }
        Assert.Equal(5, Kinds(true).Count);
        Assert.Equal(3, Kinds(false).Count);
    }

    // ---------- 33. привиди ----------

    [Fact]
    public void Ghost_flies_through_walls_throws_one_slow_revenge_and_it_counts_as_revenge()
    {
        var core = Empty();
        core.Ghosts = true;
        var ghost = Put(core, 0, 1, 1);
        var victim = Put(core, 1, 5, 3);
        Put(core, 2, 13, 11);
        core.Bombs.Add(new BomberBomb { Cell = core.Cell(1, 1), Owner = 0, Range = 1, Fuse = 1 });
        core.Step();
        Assert.False(ghost.Alive);
        Assert.True(ghost.Ghost && ghost.Revenge);
        Assert.Equal(2, core.AliveCount);
        // крізь стовп (2,2) — вниз і праворуч
        core.Turn(0, 1);
        Run(core, 8);
        Assert.Equal(core.Cell(1, 3), ghost.Cell);
        core.Turn(0, 0);
        Run(core, 16);
        core.Turn(0, -1);
        Assert.Equal(core.Cell(5, 3), core.Center(ghost));
        Assert.True(core.Revenge(0));
        Assert.False(core.Revenge(0));   // одна на раунд
        Assert.Equal(BomberCore.RevengeFuse, core.Bombs.Single().Fuse);
        Run(core, BomberCore.RevengeFuse);
        Assert.False(victim.Alive);
        Assert.Contains(new BomberEvent(BomberHow.Revenge, 0, 1), core.Events);
        Assert.True(ghost.Ghost);        // привид нікуди не дівся і не вмирає
    }

    [Fact]
    public void Ghost_revenge_needs_a_free_cell()
    {
        var core = Empty();
        var p = Put(core, 0, 2, 2);      // стовп
        p.Alive = false;
        p.Ghost = true;
        p.Revenge = true;
        Assert.False(core.Revenge(0));
        Assert.True(p.Revenge);
    }

    [Fact]
    public void Ghosts_option_lets_the_dead_throw_revenge_through_the_room()
    {
        var h = Table(3, new { ghosts = "1" });
        Ready(h);
        h.Input(0, "bomb");
        h.Tick(BomberCore.FuseTicks);
        var me = h.View(null).GetProperty("p")[0];
        Assert.False(me.GetProperty("alive").GetBoolean());
        Assert.Equal(1, me.GetProperty("g").GetInt32());
        Assert.True(h.Act(0, "bomb").Ok);
        Assert.Equal(2, h.View(null).GetProperty("p")[0].GetProperty("g").GetInt32());
        Assert.False(h.Act(0, "bomb").Ok);
        Assert.Contains(h.View(null).GetProperty("b").EnumerateArray(), b => b.TryGetProperty("rv", out _));
    }

    // ---------- 37. команди ----------

    [Fact]
    public void Teams_of_two_on_four_and_nobody_on_three()
    {
        var h = Table(4, new { teams = "1" });
        Assert.Equal([0, 1, 0, 1, -1, -1], h.View(null).GetProperty("teams").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        var three = Table(3, new { teams = "1" });
        Assert.False(three.View(null).TryGetProperty("teams", out _));
        Assert.Contains("Команд не буде", three.View(null).GetProperty("note").GetString());
    }

    static BomberCore TeamCore(bool ff)
    {
        var core = new BomberCore(new Random(1)) { Teams = [0, 1, 0, 1, -1, -1], FriendlyFire = ff };
        core.Layout();
        Put(core, 0, 1, 1);
        Put(core, 2, 3, 1);
        Put(core, 1, 13, 11);
        Put(core, 3, 11, 11);
        return core;
    }

    [Fact]
    public void Teammates_bomb_does_not_hurt_without_friendly_fire()
    {
        var core = TeamCore(ff: false);
        core.Bombs.Add(new BomberBomb { Cell = core.Cell(1, 1), Owner = 0, Range = 2, Fuse = 1 });
        core.Step();
        Assert.False(core.Players[0].Alive);   // свою бомбу — завжди на собі
        Assert.True(core.Players[2].Alive);
        Assert.Contains(new BomberEvent(BomberHow.Self, 0, 0), core.Events);
    }

    [Fact]
    public void Friendly_fire_hurts_and_is_logged_as_a_team_kill()
    {
        var core = TeamCore(ff: true);
        core.Bombs.Add(new BomberBomb { Cell = core.Cell(2, 1), Owner = 0, Range = 1, Fuse = 1 });
        core.Step();
        Assert.False(core.Players[2].Alive);
        Assert.Contains(new BomberEvent(BomberHow.Team, 0, 2), core.Events);
    }

    [Fact]
    public void Round_goes_to_the_team_when_only_its_players_are_left()
    {
        var core = TeamCore(ff: false);
        Assert.False(core.RoundOver);
        core.Players[1].Alive = false;
        Assert.False(core.RoundOver);
        core.Players[3].Alive = false;
        Assert.True(core.RoundOver);
        Assert.Equal(0, core.LastTeam);
    }

    [Fact]
    public void Team_party_ends_with_the_whole_team_winning()
    {
        var h = Table(4, new { teams = "1" });
        for (var round = 0; round < 3; round++)
        {
            Ready(h);
            // «Помідори» (місця 1 і 3) підривають самі себе
            h.Input(1, "bomb");
            h.Input(3, "bomb");
            h.Tick(BomberCore.FuseTicks + 1);
            for (var i = 0; i < 40 && Phase(h) == "pause"; i++) h.Tick(1);
        }
        Assert.Single(h.Finished);
        Assert.Equal([0, 2], h.Finished[0].Result.Winners.OrderBy(s => s).ToArray());
        Assert.StartsWith("Бомбер: 🥒 Огірки (Оля, Ганна) 3 : 🍅 Помідори (Петро, Іван) 0", h.Finished[0].Result.Text);
    }

    // ---------- 38. стрічка подій і підсумок ----------

    [Fact]
    public void Feed_carries_a_fresh_event_for_a_couple_of_seconds_and_summary_names_the_self_bomber()
    {
        var h = Table(2);
        Ready(h);
        h.Input(0, "bomb");
        h.Tick(BomberCore.FuseTicks);
        var ev = h.View(null).GetProperty("ev");
        Assert.Equal(1, ev.GetArrayLength());
        Assert.Equal([1, (int)BomberHow.Self, 0, 0], ev[0].EnumerateArray().Select(e => e.GetInt32()).ToArray());
        h.Tick(Bomber.EventTicks);
        Assert.False(h.View(null).TryGetProperty("ev", out _));

        for (var round = 1; round < 3; round++)
        {
            Ready(h);
            h.Input(0, "bomb");
            h.Tick(BomberCore.FuseTicks);
        }
        var sum = h.View(null).GetProperty("sum").EnumerateArray().ToArray();
        var self = sum.Single(s => s.GetProperty("k").GetString() == "self");
        Assert.Equal(3, self.GetProperty("n").GetInt32());
        Assert.Equal([0], self.GetProperty("s").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        var lived = sum.Single(s => s.GetProperty("k").GetString() == "long");
        Assert.Equal([1], lived.GetProperty("s").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        Assert.DoesNotContain(sum, s => s.GetProperty("k").GetString() == "kills");   // нікого не підірвали — звання нема
    }

    [Fact]
    public void Kill_by_a_rival_goes_to_the_feed_with_both_names()
    {
        var core = Empty();
        Put(core, 0, 1, 1);
        Put(core, 1, 5, 1);
        core.Bombs.Add(new BomberBomb { Cell = core.Cell(1, 1), Owner = 1, Range = 2, Fuse = 1 });
        core.Step();
        Assert.Equal([new BomberEvent(BomberHow.Kill, 1, 0)], core.Events);
    }

    // ---------- перф і кадр ----------

    [Fact]
    [Trait("Category", "Perf")]
    public void Six_on_the_big_chaotic_field_with_ghosts_and_shrink_tick_cheaply_with_small_frames()
    {
        var h = Table(6, new { size = "19", chaos = "1", ghosts = "1", shrink = "1", map = "open" }, seed: 7);
        var ticks = 0L;
        var maxFrame = 0;
        var n = 0;
        for (var i = 0; i < 3000; i++)
        {
            if (h.Room.Status == RoomStatus.Finished) h.Rematch();
            for (var seat = 0; seat < 6; seat++)
            {
                if ((i + seat) % (5 + seat) == 0) h.Input(seat, "move", new { dir = (i / 5 + seat) % 4 });
                if ((i + seat * 3) % (19 + seat) == 0) h.Input(seat, "bomb");
            }
            h.Clock.AdvanceMs(BomberCore.TickMs);
            var t0 = Stopwatch.GetTimestamp();
            var outbox = h.Rooms.Tick(h.Room);
            ticks += Stopwatch.GetTimestamp() - t0;
            n++;
            foreach (var o in outbox)
                if (o is RoomFrame f) maxFrame = Math.Max(maxFrame, Encoding.UTF8.GetByteCount(Views.Text(f.Frame)));
        }
        var avgMs = ticks * 1000.0 / Stopwatch.Frequency / n;
        Assert.True(avgMs < 0.25, $"середній тик {avgMs:F3} мс");
        Assert.True(maxFrame <= 1500, $"найбільший кадр {maxFrame} Б");
    }
}
