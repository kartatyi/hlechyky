using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Бомбер: правила поля, руху й вибухів перевіряємо на голому <see cref="BomberCore"/> (там можна
/// поставити ящик рівно туди, куди треба), а раунди, рахунок і кінець партії — уже через кімнату
/// (TESTING.md §4).
/// </summary>
public class BomberTests
{
    // ---------- підмостки ----------

    /// <summary>Стіл на потрібну кількість гравців; бомбер стартує з кнопки господаря.</summary>
    static RoomHarness Table(int players = 2, int seed = 42)
    {
        var h = new RoomHarness("bomber", seed: seed);
        foreach (var nick in new[] { "Оля", "Петро", "Ганна", "Іван" }.Take(players)) h.Join(nick);
        h.Start();
        return h;
    }

    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString() ?? "";

    /// <summary>Перечекати «готуйсь»: до самої гри доходимо не рахуючи тики руками.</summary>
    static void Ready(RoomHarness h)
    {
        for (var i = 0; i < 200 && Phase(h) != "go"; i++) h.Tick(1);
        Assert.Equal("go", Phase(h));
    }

    /// <summary>Жовтий підриває сам себе — найкоротший спосіб віддати раунд суперникові.</summary>
    static void GiveRoundAway(RoomHarness h)
    {
        Ready(h);
        h.Input(0, "bomb");
        h.Tick(BomberCore.FuseTicks);
    }

    /// <summary>Поле без ящиків і без живих: далі тест ставить туди рівно те, що перевіряє.</summary>
    static BomberCore Empty(int seed = 1)
    {
        var core = new BomberCore(new Random(seed));
        core.Layout();
        return core;
    }

    static BomberMan Put(BomberCore core, int seat, int x, int y)
    {
        var p = core.Players[seat];
        p.Cell = BomberCore.Cell(x, y);
        p.Move = -1;
        p.Step = 0;
        p.Want = -1;
        p.Plays = true;
        p.Alive = true;
        return p;
    }

    static void Steps(BomberCore core, int times)
    {
        for (var i = 0; i < times; i++) core.Step();
    }

    static bool Burning(BomberCore core, int x, int y) => core.Flame[BomberCore.Cell(x, y)] > 0;

    /// <summary>Та сама геометрія, що й у грі, але записана в тесті окремо — щоб перевіряти, а не повторювати.</summary>
    static bool IsWall(int x, int y) =>
        x == 0 || y == 0 || x == BomberCore.W - 1 || y == BomberCore.H - 1 || (x % 2 == 0 && y % 2 == 0);

    static bool NearCorner(int x, int y) => BomberCore.Corners.Any(c =>
        Math.Abs(BomberCore.X(c) - x) <= 1 && Math.Abs(BomberCore.Y(c) - y) <= 1);

    static bool[] All(int n) => [.. Enumerable.Range(0, BomberCore.Seats).Select(i => i < n)];

    // ---------- поле ----------

    [Fact]
    public void The_field_is_walled_all_around()
    {
        var core = Empty();
        for (var x = 0; x < BomberCore.W; x++)
        {
            Assert.Equal(BomberTile.Wall, core.Tiles[BomberCore.Cell(x, 0)]);
            Assert.Equal(BomberTile.Wall, core.Tiles[BomberCore.Cell(x, BomberCore.H - 1)]);
        }
        for (var y = 0; y < BomberCore.H; y++)
        {
            Assert.Equal(BomberTile.Wall, core.Tiles[BomberCore.Cell(0, y)]);
            Assert.Equal(BomberTile.Wall, core.Tiles[BomberCore.Cell(BomberCore.W - 1, y)]);
        }
    }

    [Fact]
    public void Pillars_stand_on_every_even_crossing_and_nowhere_else()
    {
        var core = Empty();
        for (var y = 1; y < BomberCore.H - 1; y++)
            for (var x = 1; x < BomberCore.W - 1; x++)
            {
                var wall = core.Tiles[BomberCore.Cell(x, y)] == BomberTile.Wall;
                Assert.Equal(x % 2 == 0 && y % 2 == 0, wall);
            }
    }

    [Fact]
    public void The_corners_stay_clear_for_three_by_three()
    {
        var core = new BomberCore(new Random(5));
        core.Reset(All(4));
        foreach (var corner in BomberCore.Corners)
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    var cell = BomberCore.Cell(BomberCore.X(corner) + dx, BomberCore.Y(corner) + dy);
                    Assert.NotEqual(BomberTile.Box, core.Tiles[cell]);
                }
    }

    [Fact]
    public void About_fifty_five_percent_of_the_free_cells_get_a_box()
    {
        var candidates = 0;
        for (var y = 1; y < BomberCore.H - 1; y++)
            for (var x = 1; x < BomberCore.W - 1; x++)
                if (!IsWall(x, y) && !NearCorner(x, y)) candidates++;

        var total = 0.0;
        for (var seed = 1; seed <= 40; seed++)
        {
            var core = new BomberCore(new Random(seed));
            core.Reset(All(4));
            total += core.BoxCells().Length / (double)candidates;
        }
        var share = total / 40;
        Assert.InRange(share, 0.50, 0.60);
    }

    [Fact]
    public void Four_corners_are_the_starts_and_two_players_take_the_diagonal()
    {
        var core = new BomberCore(new Random(3));
        core.Reset(All(4));
        for (var i = 0; i < BomberCore.Seats; i++)
        {
            Assert.Equal(BomberCore.Corners[i], core.Players[i].Cell);
            Assert.True(core.Players[i].Alive);
        }

        core.Reset(All(2));
        Assert.True(core.Players[0].Alive);
        Assert.True(core.Players[1].Alive);
        Assert.False(core.Players[2].Alive);
        Assert.False(core.Players[3].Alive);
        var (a, b) = (core.Players[0].Cell, core.Players[1].Cell);
        Assert.Equal(BomberCore.W - 3, Math.Abs(BomberCore.X(a) - BomberCore.X(b)));
        Assert.Equal(BomberCore.H - 3, Math.Abs(BomberCore.Y(a) - BomberCore.Y(b)));
    }

    // ---------- рух ----------

    [Fact]
    public void A_wall_stops_the_walker()
    {
        var core = Empty();
        var p = Put(core, 0, 1, 1);
        core.Turn(0, 3);                 // вгору, а там рамка
        Steps(core, 10);

        Assert.Equal(BomberCore.Cell(1, 1), p.Cell);
        Assert.Equal(-1, p.Move);
    }

    [Fact]
    public void A_box_stops_the_walker()
    {
        var core = Empty();
        core.Tiles[BomberCore.Cell(2, 1)] = BomberTile.Box;
        var p = Put(core, 0, 1, 1);
        core.Turn(0, 0);
        Steps(core, 10);

        Assert.Equal(BomberCore.Cell(1, 1), p.Cell);
    }

    [Fact]
    public void A_bomb_stops_the_walker()
    {
        var core = Empty();
        Put(core, 1, 2, 1);
        Assert.True(core.Bomb(1));       // чужа бомба лягла просто на дорозі
        var p = Put(core, 0, 1, 1);
        core.Turn(0, 0);
        Steps(core, 10);

        Assert.Equal(BomberCore.Cell(1, 1), p.Cell);
    }

    [Fact]
    public void You_can_step_off_your_own_fresh_bomb_but_not_back_onto_it()
    {
        var core = Empty();
        var p = Put(core, 0, 1, 1);
        Assert.True(core.Bomb(0));
        core.Turn(0, 0);
        Steps(core, 4);
        Assert.Equal(BomberCore.Cell(2, 1), p.Cell);

        core.Turn(0, 2);                 // назад, на свою ж бомбу — уже не можна
        Steps(core, 8);
        Assert.Equal(BomberCore.Cell(2, 1), p.Cell);
    }

    [Fact]
    public void A_cell_takes_four_ticks_and_three_in_boots()
    {
        var core = Empty();
        var p = Put(core, 0, 1, 1);
        core.Turn(0, 0);
        Steps(core, 3);
        Assert.Equal(BomberCore.Cell(1, 1), p.Cell);
        core.Step();
        Assert.Equal(BomberCore.Cell(2, 1), p.Cell);

        p.Boots = true;
        Steps(core, 3);
        Assert.Equal(BomberCore.Cell(3, 1), p.Cell);
    }

    [Fact]
    public void Letting_the_key_go_stops_at_the_next_cell()
    {
        var core = Empty();
        var p = Put(core, 0, 1, 1);
        core.Turn(0, 0);
        Steps(core, 2);                  // півклітинки позаду
        core.Turn(0, -1);                // відпустив
        Steps(core, 2);
        Assert.Equal(BomberCore.Cell(2, 1), p.Cell);
        Assert.Equal(-1, p.Move);

        Steps(core, 8);
        Assert.Equal(BomberCore.Cell(2, 1), p.Cell);
    }

    [Fact]
    public void The_centre_cell_switches_halfway_through_the_step()
    {
        var core = Empty();
        var p = Put(core, 0, 1, 1);
        core.Turn(0, 0);
        core.Step();
        Assert.Equal(BomberCore.Cell(1, 1), BomberCore.Center(p));
        core.Step();
        Assert.Equal(BomberCore.Cell(2, 1), BomberCore.Center(p));
    }

    // ---------- бомби й вибухи ----------

    [Fact]
    public void The_bomb_limit_is_one_until_a_bonus_says_otherwise()
    {
        var core = Empty();
        var p = Put(core, 0, 1, 1);
        Assert.True(core.Bomb(0));
        core.Turn(0, 0);
        Steps(core, 4);
        Assert.False(core.Bomb(0));      // одна вже цокає

        p.Bombs = 2;
        Assert.True(core.Bomb(0));
        Assert.Equal(2, core.Bombs.Count);
    }

    [Fact]
    public void Two_bombs_never_share_a_cell()
    {
        var core = Empty();
        Put(core, 0, 1, 1);
        Put(core, 1, 1, 1);
        Assert.True(core.Bomb(0));
        Assert.False(core.Bomb(1));      // сусід стоїть на тій самій клітинці
        Assert.Single(core.Bombs);
    }

    [Fact]
    public void The_fuse_burns_exactly_thirty_three_ticks()
    {
        var core = Empty();
        Put(core, 0, 1, 1);
        core.Bomb(0);
        Steps(core, BomberCore.FuseTicks - 1);
        Assert.Single(core.Bombs);
        Assert.False(Burning(core, 1, 1));

        core.Step();
        Assert.Empty(core.Bombs);
        Assert.True(Burning(core, 1, 1));
    }

    [Fact]
    public void The_flame_lives_four_tenths_of_a_second()
    {
        var core = Empty();
        Put(core, 0, 1, 1);
        core.Bomb(0);
        Steps(core, BomberCore.FuseTicks);
        Assert.Equal(BomberCore.FlameTicks, core.Flame[BomberCore.Cell(1, 1)]);

        Steps(core, BomberCore.FlameTicks - 1);
        Assert.True(Burning(core, 1, 1));
        core.Step();
        Assert.False(Burning(core, 1, 1));
    }

    [Fact]
    public void The_blast_is_a_cross_that_stops_at_the_walls()
    {
        var core = Empty();
        Put(core, 0, 1, 1);
        core.Bomb(0);
        Steps(core, BomberCore.FuseTicks);

        Assert.Equal(new[] { (1, 1), (2, 1), (3, 1), (1, 2), (1, 3) }.Length, core.FlameCells().Length);
        foreach (var (x, y) in new[] { (1, 1), (2, 1), (3, 1), (1, 2), (1, 3) }) Assert.True(Burning(core, x, y));
    }

    [Fact]
    public void The_blast_breaks_the_first_box_and_goes_no_further()
    {
        var core = Empty();
        core.Tiles[BomberCore.Cell(3, 1)] = BomberTile.Box;
        core.Tiles[BomberCore.Cell(4, 1)] = BomberTile.Box;
        var p = Put(core, 0, 1, 1);
        p.Range = 4;
        core.Bomb(0);
        Steps(core, BomberCore.FuseTicks);

        Assert.Equal(BomberTile.Free, core.Tiles[BomberCore.Cell(3, 1)]);
        Assert.Equal(BomberTile.Box, core.Tiles[BomberCore.Cell(4, 1)]);
        Assert.True(Burning(core, 3, 1));
        Assert.False(Burning(core, 4, 1));
    }

    [Fact]
    public void A_bomb_keeps_the_range_it_had_when_it_was_put_down()
    {
        var core = Empty();
        var p = Put(core, 0, 1, 1);
        core.Bomb(0);
        p.Range = BomberCore.MaxRange;   // вогонь підібрали вже після того, як запал пішов
        Steps(core, BomberCore.FuseTicks);

        Assert.True(Burning(core, 3, 1));
        Assert.False(Burning(core, 4, 1));
    }

    [Fact]
    public void Two_bombs_go_off_in_one_chain()
    {
        var core = Empty();
        Put(core, 0, 1, 1);
        core.Bomb(0);
        Steps(core, 5);                  // друга ляже пізніше, тож сама б ще не встигла

        Put(core, 1, 3, 1);
        core.Bomb(1);
        Steps(core, BomberCore.FuseTicks - 5);

        Assert.Empty(core.Bombs);
        Assert.True(Burning(core, 5, 1));   // це вже промінь другої бомби, перша так далеко не дістає
    }

    [Fact]
    public void Two_fuses_burning_out_together_each_go_off_once()
    {
        // Обидві бомби потрапляють у вибух одним тиком, і кожна має зніматися рівно раз: якби ланцюг
        // діставав ту саму бомбу двічі, лічильник «моїх бомб на полі» пішов би в мінус.
        var core = Empty();
        Put(core, 0, 1, 1);
        Put(core, 1, 3, 1);
        Assert.True(core.Bomb(0));
        Assert.True(core.Bomb(1));
        Steps(core, BomberCore.FuseTicks);

        Assert.Empty(core.Bombs);
        Assert.True(Burning(core, 1, 1));
        Assert.True(Burning(core, 5, 1));
        Assert.Equal(0, core.Players[0].Fused);
        Assert.Equal(0, core.Players[1].Fused);

        core.Players[0].Alive = true;      // згорів у власному вибуху — воскрешаємо, щоб перевірити ліміт
        Assert.True(core.Bomb(0));
    }

    [Fact]
    public void A_chain_blast_does_not_shoot_through_a_box_the_first_bomb_just_broke()
    {
        // Увесь ланцюг рахуємо проти поля, яким воно було до вибуху: ящик зупиняє і той промінь,
        // що прийшов у ту саму клітинку вже після того, як ящик розлетівся.
        var core = Empty();
        core.Tiles[BomberCore.Cell(5, 1)] = BomberTile.Box;
        var first = Put(core, 0, 1, 1);
        first.Range = 4;
        Assert.True(core.Bomb(0));
        Steps(core, 5);

        var second = Put(core, 1, 3, 1);
        second.Range = 4;
        Assert.True(core.Bomb(1));
        Steps(core, BomberCore.FuseTicks - 5);

        Assert.Empty(core.Bombs);
        Assert.Equal(BomberTile.Free, core.Tiles[BomberCore.Cell(5, 1)]);
        Assert.True(Burning(core, 5, 1));
        Assert.False(Burning(core, 6, 1));
    }

    [Fact]
    public void A_player_caught_in_the_flame_dies()
    {
        var core = Empty();
        Put(core, 0, 1, 1);
        var victim = Put(core, 1, 3, 1);
        core.Bomb(0);
        Steps(core, BomberCore.FuseTicks - 1);
        Assert.True(victim.Alive);

        core.Step();
        Assert.False(victim.Alive);
    }

    [Fact]
    public void Everyone_dying_at_once_leaves_the_round_without_a_winner()
    {
        var core = Empty();
        Put(core, 0, 1, 1);
        Put(core, 1, 3, 1);
        core.Bomb(0);
        Steps(core, BomberCore.FuseTicks);

        Assert.Equal(0, core.AliveCount);
        Assert.True(core.RoundOver);
        Assert.Equal(-1, core.LastStanding);
    }

    // ---------- бонуси ----------

    [Fact]
    public void A_broken_box_leaves_a_bonus_about_a_third_of_the_time()
    {
        var dropped = 0;
        var kinds = new HashSet<BomberBonus>();
        for (var seed = 0; seed < 400; seed++)
        {
            var core = Empty(seed);
            core.Tiles[BomberCore.Cell(2, 1)] = BomberTile.Box;
            Put(core, 0, 1, 1);
            core.Bomb(0);
            Steps(core, BomberCore.FuseTicks);
            if (core.Drops.Count == 0) continue;
            dropped++;
            kinds.Add(core.Drops[0].Kind);
        }
        Assert.InRange(dropped / 400.0, 0.24, 0.36);
        Assert.Equal(3, kinds.Count);     // трапляються всі три бонуси, а не один
    }

    [Fact]
    public void A_bonus_is_applied_the_moment_it_is_stepped_on()
    {
        var core = Empty();
        var p = Put(core, 0, 1, 1);
        core.Drops.Add(new BomberDrop { Cell = BomberCore.Cell(2, 1), Kind = BomberBonus.Range });
        core.Drops.Add(new BomberDrop { Cell = BomberCore.Cell(3, 1), Kind = BomberBonus.Bomb });
        core.Drops.Add(new BomberDrop { Cell = BomberCore.Cell(4, 1), Kind = BomberBonus.Boots });
        core.Turn(0, 0);
        Steps(core, 12);

        Assert.Equal(BomberCore.StartRange + 1, p.Range);
        Assert.Equal(BomberCore.StartBombs + 1, p.Bombs);
        Assert.True(p.Boots);
        Assert.Empty(core.Drops);
    }

    [Fact]
    public void Upgrades_stop_at_the_ceiling()
    {
        var core = Empty();
        var p = Put(core, 0, 1, 1);
        p.Range = BomberCore.MaxRange;
        p.Bombs = BomberCore.MaxBombs;
        core.Drops.Add(new BomberDrop { Cell = BomberCore.Cell(2, 1), Kind = BomberBonus.Range });
        core.Drops.Add(new BomberDrop { Cell = BomberCore.Cell(3, 1), Kind = BomberBonus.Bomb });
        core.Turn(0, 0);
        Steps(core, 8);

        Assert.Equal(BomberCore.MaxRange, p.Range);
        Assert.Equal(BomberCore.MaxBombs, p.Bombs);
    }

    [Fact]
    public void A_bonus_lying_in_the_open_burns_in_the_flame()
    {
        var core = Empty();
        Put(core, 0, 1, 1);
        core.Drops.Add(new BomberDrop { Cell = BomberCore.Cell(2, 1), Kind = BomberBonus.Range });
        core.Bomb(0);
        Steps(core, BomberCore.FuseTicks);

        Assert.Empty(core.Drops);
    }

    // ---------- раунди й партія ----------

    [Fact]
    public void A_table_waiting_for_players_already_looks_like_a_field()
    {
        var h = new RoomHarness("bomber");
        h.Join("Оля");

        var v = h.View(null);
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);
        Assert.Equal(BomberCore.W, v.GetProperty("width").GetInt32());
        Assert.NotEmpty(v.GetProperty("walls").EnumerateArray());
        Assert.Empty(v.GetProperty("boxes").EnumerateArray());   // ящики — вже з Ctx.Rng, у лобі їх не малюємо
        Assert.True(v.GetProperty("p")[0].GetProperty("alive").GetBoolean());
        Assert.False(v.GetProperty("p")[1].GetProperty("alive").GetBoolean());
    }

    [Fact]
    public void Nobody_moves_while_the_countdown_runs()
    {
        var h = Table();
        Assert.Equal("start", Phase(h));
        var before = h.View(0).GetProperty("p")[0].ToString();

        h.Input(0, "move", new { dir = 0 });
        h.Tick(5);
        Assert.Equal("start", Phase(h));
        Assert.Equal(before, h.View(0).GetProperty("p")[0].ToString());
    }

    [Fact]
    public void The_countdown_ends_and_the_walkers_start_walking()
    {
        var h = Table();
        Ready(h);
        var before = h.View(0).GetProperty("p")[0].GetProperty("x").GetInt32();

        h.Input(0, "move", new { dir = 0 });
        h.Tick(4);
        Assert.Equal(before + BomberCore.Sub, h.View(0).GetProperty("p")[0].GetProperty("x").GetInt32());
    }

    [Fact]
    public void A_direction_held_through_the_countdown_works_from_the_first_tick()
    {
        // Найприродніша річ за столом: затиснути стрілку ще на «Готуйсь». Намір має дочекатись раунду,
        // а не пропасти разом із відмовою.
        var h = Table();
        Assert.Equal("start", Phase(h));
        h.Input(0, "move", new { dir = 1 });
        Ready(h);
        var before = h.View(0).GetProperty("p")[0].GetProperty("y").GetInt32();

        h.Tick(4);
        Assert.Equal(before + BomberCore.Sub, h.View(0).GetProperty("p")[0].GetProperty("y").GetInt32());
    }

    [Fact]
    public void A_direction_held_from_the_last_round_survives_the_fresh_field()
    {
        var h = Table();
        Ready(h);
        h.Input(0, "move", new { dir = 1 });
        h.Input(1, "bomb");                       // Петро підриває сам себе — раунд бере Оля
        h.Tick(BomberCore.FuseTicks);
        Assert.Equal("pause", Phase(h));

        Ready(h);                                 // новий раунд, нове поле, клавішу так і не відпускали
        var before = h.View(0).GetProperty("p")[0].GetProperty("y").GetInt32();
        h.Tick(4);
        Assert.Equal(before + BomberCore.Sub, h.View(0).GetProperty("p")[0].GetProperty("y").GetInt32());
    }

    [Fact]
    public void A_key_pressed_while_you_lie_dead_waits_for_the_new_round()
    {
        // Пауза між раундами — теж час: хтось саме тоді бере в руки клавіші. Намір мусить дочекатись
        // свіжого поля, а не пропасти разом із відмовою «тебе вже підірвали».
        var h = Table();
        GiveRoundAway(h);
        Assert.Equal("pause", Phase(h));
        Assert.True(h.Act(0, "move", new { dir = 1 }).Ok);

        Ready(h);
        var before = h.View(0).GetProperty("p")[0].GetProperty("y").GetInt32();
        h.Tick(4);
        Assert.Equal(before + BomberCore.Sub, h.View(0).GetProperty("p")[0].GetProperty("y").GetInt32());
    }

    [Fact]
    public void Three_rounds_take_the_match()
    {
        var h = Table();
        for (var i = 0; i < 3; i++) GiveRoundAway(h);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Room.Result!.Winners);
        Assert.Equal([0, 3, 0, 0], h.View(null).GetProperty("wins").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        Assert.StartsWith("Бомбер: Петро 3 : Оля 0", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void A_round_where_both_blow_up_gives_nobody_a_win()
    {
        var h = Table();
        Ready(h);
        h.Input(0, "bomb");
        h.Input(1, "bomb");
        h.Tick(BomberCore.FuseTicks);

        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("pause", Phase(h));
        Assert.All(h.View(null).GetProperty("wins").EnumerateArray(), w => Assert.Equal(0, w.GetInt32()));
    }

    [Fact]
    public void The_pause_gives_way_to_a_fresh_field()
    {
        var h = Table();
        GiveRoundAway(h);
        Assert.Equal("pause", Phase(h));
        Assert.Equal(1, h.View(null).GetProperty("round").GetInt32());

        for (var i = 0; i < 200 && Phase(h) == "pause"; i++) h.Tick(1);
        Assert.Equal("start", Phase(h));
        Assert.Equal(2, h.View(null).GetProperty("round").GetInt32());
        Assert.Empty(h.View(null).GetProperty("f").EnumerateArray());   // догоріло
        Assert.True(h.View(null).GetProperty("p")[0].GetProperty("alive").GetBoolean());
    }

    [Fact]
    public void Two_minutes_end_the_round_in_a_draw()
    {
        var h = Table();
        Ready(h);
        h.Tick(BomberCore.RoundTicks);

        Assert.Equal("pause", Phase(h));
        Assert.All(h.View(null).GetProperty("wins").EnumerateArray(), w => Assert.Equal(0, w.GetInt32()));
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void Nine_drawn_rounds_end_the_match_with_nobody_ahead()
    {
        // Запобіжник проти вічної партії: дев'ять нічиїх — і стіл розходиться внічию, а не грає далі.
        var h = Table();
        for (var i = 0; i < Bomber.MaxRounds && h.Room.Status == RoomStatus.Playing; i++)
        {
            Ready(h);
            h.Tick(BomberCore.RoundTicks);
        }

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Empty(h.Room.Result!.Winners);
        Assert.Equal("Бомбер: Оля 0 : Петро 0 — нічия", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void After_nine_rounds_the_match_goes_to_whoever_is_ahead()
    {
        var h = Table();
        GiveRoundAway(h);                       // перший раунд — Петрів
        for (var i = 0; i < Bomber.MaxRounds - 1 && h.Room.Status == RoomStatus.Playing; i++)
        {
            Ready(h);
            h.Tick(BomberCore.RoundTicks);      // решта — нічиї, до трьох перемог ніхто не дійде
        }

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Room.Result!.Winners);
        Assert.StartsWith("Бомбер: Петро 1 : Оля 0", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void Rematch_starts_from_a_clean_field_and_a_zero_score()
    {
        var h = Table();
        for (var i = 0; i < 3; i++) GiveRoundAway(h);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);

        h.Rematch("Оля");
        Assert.Equal("Оля", h.Room.Seats[1]);           // місця обернулись, як усюди на платформі
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        var v = h.View(null);
        Assert.Equal("start", v.GetProperty("phase").GetString());
        Assert.Equal(1, v.GetProperty("round").GetInt32());
        Assert.All(v.GetProperty("wins").EnumerateArray(), w => Assert.Equal(0, w.GetInt32()));
        Assert.NotEmpty(v.GetProperty("boxes").EnumerateArray());
    }

    [Fact]
    public void A_leaver_out_of_four_does_not_break_the_game_for_the_rest()
    {
        var h = Table(4);
        Ready(h);
        h.Leave("Ганна");

        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.False(h.View(null).GetProperty("p")[2].GetProperty("alive").GetBoolean());
        h.Tick(5);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void The_last_one_left_at_the_table_takes_the_match()
    {
        var h = Table(4);
        Ready(h);
        h.Leave("Ганна");
        h.Leave("Іван");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);

        h.Leave("Петро");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
    }

    // ---------- ввід ----------

    [Fact]
    public void An_unknown_action_and_a_too_early_one_are_refused_politely()
    {
        var h = Table();
        Assert.Equal("Зачекай, зараз почнемо", h.Act(0, "bomb").Message);

        Ready(h);
        var before = h.View(null).ToString();
        Assert.Equal("Тут так не ходять", h.Act(0, "jump").Message);
        Assert.Equal(before, h.View(null).ToString());    // відмова нічого не змінила на полі
        Assert.True(h.Act(0, "move", new { dir = 1 }).Ok);
        Assert.True(h.Act(0, "move", 1).Ok);              // голе число теж приймаємо
    }

    [Fact]
    public void A_direction_out_of_the_four_is_refused_and_changes_nothing()
    {
        var h = Table();
        Ready(h);
        var before = h.View(null).ToString();

        Assert.Equal("Такого напрямку нема", h.Act(0, "move", new { dir = 7 }).Message);
        Assert.Equal("Такого напрямку нема", h.Act(0, "move", new { dir = -2 }).Message);
        Assert.Equal("Такого напрямку нема", h.Act(0, "move", "вгору").Message);
        Assert.Equal("Такого напрямку нема", h.Act(0, "move").Message);
        Assert.Equal(before, h.View(null).ToString());
        Assert.True(h.Act(0, "move", new { dir = -1 }).Ok);   // «стоп» — це нормальний намір, а не сміття
    }

    [Fact]
    public void The_dead_are_told_to_wait_for_the_next_round()
    {
        // На чотирьох раунд після однієї смерті триває далі — саме там і чути відмову небіжчикові.
        var h = Table(4);
        Ready(h);
        h.Input(1, "bomb");
        h.Tick(BomberCore.FuseTicks);

        Assert.Equal("go", Phase(h));
        Assert.False(h.View(null).GetProperty("p")[1].GetProperty("alive").GetBoolean());
        Assert.Equal("Тебе вже підірвали, чекай наступного раунду", h.Act(1, "bomb").Message);
        Assert.True(h.Act(0, "bomb").Ok);
    }

    [Fact]
    public void The_bomb_limit_is_reported_in_human_words()
    {
        var h = Table();
        Ready(h);
        Assert.True(h.Act(0, "bomb").Ok);
        h.Input(0, "move", new { dir = 1 });
        h.Tick(4);
        Assert.Equal("Бомби скінчились", h.Act(0, "bomb").Message);
    }

    // ---------- вид і кадр ----------

    [Fact]
    public void The_frame_carries_exactly_what_the_client_draws()
    {
        var h = Table();
        h.Tick(1);
        var frame = Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);

        foreach (var name in new[] { "t", "p", "b", "f", "boxes", "pw", "wins", "phase", "startIn" })
            Assert.True(Views.Has(frame, name), name);
        Assert.Equal(BomberCore.Seats, frame.GetProperty("p").GetArrayLength());
        foreach (var name in new[] { "x", "y", "alive", "bombs", "range", "boots" })
            Assert.True(Views.Has(frame.GetProperty("p")[0], name), name);
        Assert.False(Views.Has(frame, "walls"));   // стіни не міняються — у кадрі їм не місце
    }

    [Fact]
    public void The_view_carries_the_board_the_client_needs_before_the_first_frame()
    {
        var h = Table();
        var v = h.View(0);

        Assert.Equal(BomberCore.W, v.GetProperty("width").GetInt32());
        Assert.Equal(BomberCore.H, v.GetProperty("height").GetInt32());
        Assert.Equal(BomberCore.Sub, v.GetProperty("sub").GetInt32());
        Assert.Equal(Bomber.WinsToTake, v.GetProperty("need").GetInt32());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("turn").ValueKind);
        Assert.NotEmpty(v.GetProperty("walls").EnumerateArray());
        Assert.NotEmpty(v.GetProperty("boxes").EnumerateArray());
        Assert.Equal(v.ToString(), h.View(null).ToString());   // ховати тут нема чого
    }

    [Fact]
    public void Bomber_is_in_the_catalog_as_a_live_game_for_four()
    {
        var game = Assert.Single(new Registry().Catalog, g => g.Id == "bomber");

        Assert.Equal("live", game.Group);
        Assert.Equal(BomberCore.TickMs, game.TickMs);
        Assert.Equal("byHost", game.Start);
        Assert.Equal(2, game.MinPlayers);
        Assert.Equal(BomberCore.Seats, game.MaxPlayers);
        Assert.False(game.Rated);            // на чотирьох ставок і Ело не буває
        Assert.Equal("bomber", game.Module);
    }

    // ---------- детермінізм і швидкість ----------

    [Fact]
    public void The_same_seed_gives_the_same_round()
    {
        static string Play(int seed)
        {
            var h = Table(2, seed);
            Ready(h);
            h.Input(0, "move", new { dir = 1 });
            h.Input(1, "move", new { dir = 3 });
            h.Tick(20);
            h.Input(0, "bomb");
            h.Tick(40);
            return h.View(null).ToString();
        }

        Assert.Equal(Play(7), Play(7));
        Assert.NotEqual(Play(7), Play(8));   // сід таки щось вирішує
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Four_players_and_two_thousand_ticks_are_instant()
    {
        var core = new BomberCore(new Random(11));
        core.Reset(All(4));
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < BomberCore.RoundTicks; i++)
        {
            for (var seat = 0; seat < BomberCore.Seats; seat++)
            {
                if (i % (7 + seat) == 0) core.Turn(seat, (i / 3 + seat) % 4);
                if (i % (23 + seat * 5) == 0) core.Bomb(seat);
                if (!core.Players[seat].Alive) { core.Players[seat].Alive = true; }   // хай бігають до кінця
            }
            core.Step();
        }
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"2000 тиків зайняли {sw.Elapsed}");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void A_whole_round_in_a_room_of_four_costs_next_to_nothing()
    {
        // Голе ядро — це пів справи: найдорожче в бомбері не крок світу, а кадр, який кімната будує
        // 16 разів на секунду. Тому міряємо саме кімнатний тик разом із розсилкою (TESTING.md §4.4).
        var h = Table(4);
        Ready(h);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < BomberCore.RoundTicks; i++)
        {
            if (i % 9 == 0) h.Input(i % BomberCore.Seats, "move", new { dir = (i / 9) % 4 });
            h.Tick(1);
        }
        sw.Stop();

        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"2000 тиків кімнати зайняли {sw.Elapsed}");
    }
}
