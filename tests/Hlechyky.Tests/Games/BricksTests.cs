using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Цеглини (docs/games/specs/bricks.md §8). Правила стіни перевіряємо на голому <see cref="BricksCore"/> — там
/// фігурку можна поставити рівно туди, куди треба, — а суддю, сміття, раунди й кадри — через кімнату.
/// </summary>
public class BricksTests(ITestOutputHelper output)
{
    const int N = BricksCore.ModeNormal;
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван"];

    // =============================================================================================
    // підмостки рушія
    // =============================================================================================

    /// <summary>Порожня (або з рядами) стіна з фігуркою рівно там, де сказано; гравітація — етап 0 (48 тиків на клітинку).</summary>
    static BricksCore Put(int type, int bx, int by, int rot = 0, string[]? rows = null, int mode = N, int stage = 1800)
    {
        var c = new BricksCore(1, mode, stage);
        if (rows is not null) c.LoadRows(rows);
        c.Type = type;
        c.Bx = bx;
        c.By = by;
        c.Rot = rot;
        c.LowY = by;
        c.LockT = c.Resets = c.GravT = 0;
        c.LastRot = false;
        c.HoldUsed = false;
        return c;
    }

    /// <summary>Колодязь у стовпці 9 на <paramref name="n"/> рядів і вертикальна I над ним (плюс цеглинка вище — щоб не було чистої стіни).</summary>
    static BricksCore Well(int n, int mode = N)
    {
        var rows = new List<string>();
        for (var i = 0; i < 4; i++) rows.Add(i < n ? "8888888880" : "0000000000");
        rows.Add("8000000000");
        return Put(BricksCore.I, 7, 10, 1, [.. rows], mode);
    }

    static HashSet<(int X, int Y)> PieceCells(BricksCore c) => PieceCells(c.Type, c.Bx, c.By, c.Rot);

    static HashSet<(int X, int Y)> PieceCells(int type, int bx, int by, int rot)
    {
        var set = new HashSet<(int, int)>();
        var o = (type * 4 + rot) * 8;
        for (var i = 0; i < 8; i += 2) set.Add((bx + BricksCore.Shapes[o + i], by + BricksCore.Shapes[o + i + 1]));
        return set;
    }

    static string Row(BricksCore c, int y)
    {
        var s = new char[BricksCore.W];
        for (var x = 0; x < BricksCore.W; x++) s[x] = (char)('0' + c.Cells[y * BricksCore.W + x]);
        return new string(s);
    }

    static void Steps(BricksCore c, int n)
    {
        for (var i = 0; i < n; i++) c.Step();
    }

    /// <summary>Жорстке падіння й те, що з нього вийшло (одна фіксація з рядами).</summary>
    static BricksClear Drop(BricksCore c)
    {
        c.ClearCount = 0;
        c.Apply(BricksCore.KHard);
        Assert.Equal(1, c.ClearCount);
        return c.Clears[0];
    }

    // =============================================================================================
    // 8.1 Рушій
    // =============================================================================================

    [Fact]
    public void Spawn_puts_every_piece_in_rows_20_21_at_columns_3_to_6()
    {
        var want = new Dictionary<int, (int, int)[]>
        {
            [BricksCore.I] = [(3, 20), (4, 20), (5, 20), (6, 20)],
            [BricksCore.O] = [(4, 20), (5, 20), (4, 21), (5, 21)],
            [BricksCore.T] = [(3, 20), (4, 20), (5, 20), (4, 21)],
            [BricksCore.S] = [(3, 20), (4, 20), (4, 21), (5, 21)],
            [BricksCore.Z] = [(3, 21), (4, 21), (4, 20), (5, 20)],
            [BricksCore.J] = [(3, 21), (3, 20), (4, 20), (5, 20)],
            [BricksCore.L] = [(5, 21), (3, 20), (4, 20), (5, 20)],
        };
        foreach (var (type, cells) in want)
        {
            var c = new BricksCore(1, N, 1800);
            c.Spawn(type);
            Assert.True(c.Alive);
            Assert.Equal(0, c.Rot);
            Assert.True(PieceCells(c).SetEquals(cells), $"фігурка {type}");
            Assert.Equal(c.By, c.LowY);
            Assert.False(c.HoldUsed);
        }
    }

    [Fact]
    public void Rotation_tables_are_true_rotations_of_state_zero()
    {
        for (var type = 0; type < 7; type++)
        {
            var n = type == BricksCore.I ? 4 : 3;
            var cur = PieceCells(type, 0, 0, 0);
            for (var r = 1; r < 4; r++)
            {
                // за годинниковою (y угору): (x, y) → (y, n−1−x)
                cur = [.. cur.Select(p => (p.Y, n - 1 - p.X))];
                var want = type == BricksCore.O ? PieceCells(type, 0, 0, 0) : cur;
                Assert.True(PieceCells(type, 0, 0, r).SetEquals(want), $"фігурка {type}, оберт {r}");
            }
        }
    }

    [Fact]
    public void O_rotation_is_a_no_op_and_does_not_reset_lock_delay()
    {
        var c = Put(BricksCore.O, 4, -1);
        c.LockT = 10;
        var ver = c.Ver;
        c.Apply(BricksCore.KCw);
        c.Apply(BricksCore.KCcw);
        Assert.Equal(0, c.Rot);
        Assert.Equal(10, c.LockT);
        Assert.Equal(0, c.Resets);
        Assert.Equal(ver, c.Ver);
        Assert.False(c.LastRot);
    }

    [Fact]
    public void Wall_kick_tests_are_tried_in_order_and_first_free_wins()
    {
        // T стоїть вертикально впритул до лівої стіни (оберт R, рамка з x = −1). R→2: (0,0) вилазить за стіну,
        // (+1,0) — вільно, тож рамка стає на x = 0 і нижче не пробуємо.
        var c = Put(BricksCore.T, -1, 5, 1);
        c.Apply(BricksCore.KCw);
        Assert.Equal(2, c.Rot);
        Assert.Equal(0, c.Bx);
        Assert.Equal(5, c.By);
        Assert.True(c.LastRot);
        Assert.Equal(1, c.Resets);

        // Той самий перехід, але перший зсув вільний — рамка не рухається.
        var d = Put(BricksCore.T, 3, 5, 1);
        d.Apply(BricksCore.KCw);
        Assert.Equal((3, 5, 2), (d.Bx, d.By, d.Rot));
    }

    [Fact]
    public void I_piece_uses_its_own_kick_table()
    {
        // Вертикальна I у крайньому лівому стовпці (R, рамка x = −2). R→2 за таблицею I: (0,0) і (−1,0) — за стіною,
        // (+2,0) — вільно. Таблиця решти фігурок тут не допомогла б: її зсуви (+1,…) лишали б I за стіною.
        var c = Put(BricksCore.I, -2, 6, 1);
        c.Apply(BricksCore.KCw);
        Assert.Equal((0, 6, 2), (c.Bx, c.By, c.Rot));
        Assert.True(PieceCells(c).SetEquals([(0, 7), (1, 7), (2, 7), (3, 7)]));

        // І ще раз — зсув, якого в таблиці решти фігурок нема взагалі: 0→R пробує (−2,0) другим
        // (решта пробувала б (−1,0) і стала б у стовпець 4). (0,0) — цеглина на (5,7) заважає.
        var d = Put(BricksCore.I, 3, 6, 0, ["", "", "", "", "", "", "", "0000010000"]);
        d.Apply(BricksCore.KCw);
        Assert.Equal((1, 6, 1), (d.Bx, d.By, d.Rot));
    }

    [Fact]
    public void Failed_rotation_changes_nothing()
    {
        // T у тісній ямці, усі п'ять зсувів упираються в цеглу або в дно.
        var c = Put(BricksCore.T, 3, 0, 0, ["8888888888", "8880008888", "8888088888", "8888888888", "8888888888", "8888888888"]);
        c.LockT = 7;
        c.Resets = 3;
        var before = c.Hash();
        c.Apply(BricksCore.KCw);
        c.Apply(BricksCore.KCcw);
        Assert.Equal(before, c.Hash());
        Assert.Equal((3, 0, 0, 3, 7), (c.Bx, c.By, c.Rot, c.Resets, c.LockT));
        Assert.False(c.LastRot);
    }

    [Fact]
    public void Seven_bag_gives_every_piece_once_per_seven()
    {
        var c = new BricksCore(12345, N, 1800);
        for (var bag = 0; bag < 100; bag++)
        {
            var seen = new HashSet<int>();
            for (var i = 0; i < 7; i++) Assert.True(seen.Add(c.PieceAt(bag * 7 + i)));
            Assert.Equal(7, seen.Count);
        }
    }

    [Fact]
    public void Bag_depends_only_on_seed_and_index()
    {
        var a = new BricksCore(777, N, 1800);
        var b = new BricksCore(777, BricksCore.ModeHard, 600);
        var other = new BricksCore(778, N, 1800);
        var diff = 0;
        // Задом наперед і вперемішку — черга не має пам'яті, лише зерно й номер.
        for (var i = 69; i >= 0; i--)
        {
            Assert.Equal(a.PieceAt(i), b.PieceAt(69 - (69 - i)));
            if (a.PieceAt(i) != other.PieceAt(i)) diff++;
        }
        Assert.True(diff > 20, $"інше зерно — інша черга ({diff} розбіжностей із 70)");
        var bag = new int[7];
        BricksCore.Bag(777, 5, bag);
        for (var k = 0; k < 7; k++) Assert.Equal(bag[k], a.PieceAt(35 + k));
        // перша фігурка стіни — це нульова з черги
        Assert.Equal(a.PieceAt(0), new BricksCore(777, N, 1800).Type);
    }

    [Fact]
    public void Xorshift_matches_pinned_sequence()
    {
        var x = 1u;
        var got = new uint[8];
        for (var i = 0; i < 8; i++) got[i] = BricksCore.NextRand(ref x);
        Assert.Equal(BricksPins.Xorshift, got);
        Assert.Equal(270369u, got[0]);   // 1 ^ 1<<13 = 8193; 8193 ^ 8193<<5 = 270369 — перевірено руками
    }

    [Fact]
    public void Hash_is_fnv1a_over_cells_then_fields_and_matches_pinned_value()
    {
        var c = new BricksCore(1, N, 1800);
        c.Spawn(BricksCore.T);
        Assert.Equal(BricksPins.SpawnT, c.Hash());
        // перша клітинка стіни міняє хеш, решта полів — теж
        var h = c.Hash();
        c.Cells[0] = 8;
        Assert.NotEqual(h, c.Hash());
        c.Cells[0] = 0;
        c.DasT = 1;
        Assert.NotEqual(h, c.Hash());
    }

    [Fact]
    public void Das_moves_once_immediately_then_after_10_ticks_every_2()
    {
        var c = Put(BricksCore.T, 3, 10);
        c.Apply(BricksCore.KLeft);
        Assert.Equal(2, c.Bx);
        var xs = new List<int>();
        for (var i = 1; i <= 16; i++)
        {
            c.Step();
            xs.Add(c.Bx);
        }
        // тики 1..9 — стоїть на 2; 10 — крок на 1; 12 — на 0 (далі стіна)
        Assert.Equal([2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 0, 0, 0, 0, 0], xs);
        Assert.Equal(10, c.By);   // гравітація 48 — за 16 тиків ще не впала
    }

    [Fact]
    public void Last_pressed_direction_wins_and_release_switches_back()
    {
        var c = Put(BricksCore.T, 3, 10);
        c.Apply(BricksCore.KLeft);
        Assert.Equal((2, -1), (c.Bx, c.Dir));
        c.Apply(BricksCore.KRight);                 // натиснув → при затиснутій ←
        Assert.Equal((3, 1, 0), (c.Bx, c.Dir, c.DasT));
        Steps(c, 5);
        c.Apply(BricksCore.KRightUp);               // відпустив →, ← ще тримає — назад ліворуч, одразу крок
        Assert.Equal((2, -1, 0), (c.Bx, c.Dir, c.DasT));
        c.Apply(BricksCore.KLeftUp);
        Assert.Equal((0, 0), (c.Dir, c.Keys));
        Steps(c, 20);
        Assert.Equal(2, c.Bx);                      // ніхто нічого не тримає — стоїть
    }

    [Fact]
    public void Soft_drop_falls_a_cell_every_2_ticks_and_hard_drop_locks_instantly()
    {
        var c = new BricksCore(1, N, 1800);
        c.Spawn(BricksCore.T);
        c.GravT = 40;                                // накопичене повільною гравітацією не кидає фігурку на кілька клітинок
        c.Apply(BricksCore.KSoft);
        c.Step();
        Assert.Equal(18, c.By);
        Steps(c, 10);
        Assert.Equal(13, c.By);
        c.Apply(BricksCore.KSoftUp);
        Steps(c, 10);
        Assert.Equal(13, c.By);

        var pi = c.Pi;
        c.Apply(BricksCore.KHard);
        Assert.Equal("0003330000", Row(c, 0));
        Assert.Equal("0000300000", Row(c, 1));
        Assert.Equal(pi + 1, c.Pi);                  // наступна вже на появі
        Assert.True(c.Type >= 0);
        Assert.Equal(c.Type == BricksCore.I ? 18 : 19, c.By);
    }

    [Fact]
    public void Gravity_uses_stage_table_and_never_skips_a_cell()
    {
        var c = new BricksCore(1, N, 1800);
        foreach (var (tick, g) in new[] { (0, 48), (1799, 48), (1800, 36), (3600, 26), (1800 * 9, 1), (1800 * 30, 1) })
        {
            c.Tick = tick;
            Assert.Equal(g, c.Gravity);
        }
        Assert.Equal(BricksCore.SprintG, new BricksCore(1, N, 0).Gravity);

        // G = 1: одна клітинка на тик, фігурка сідає рівно на цеглину й не проскакує крізь неї.
        var rows = new string[11];
        for (var i = 0; i < 10; i++) rows[i] = "";
        rows[10] = "0000100000";
        var d = Put(BricksCore.T, 3, 19, 0, rows, N, 1);
        d.Tick = 100;
        for (var i = 1; i <= 9; i++)
        {
            d.Step();
            Assert.Equal(19 - i, d.By);
        }
        Steps(d, 5);
        Assert.Equal(10, d.By);
        Assert.Equal(6, d.LockT);                    // лягла на 9-му тику — з того й лічить
    }

    [Fact]
    public void Lock_delay_is_30_ticks_and_moves_reset_it_at_most_15_times()
    {
        var c = Put(BricksCore.T, 3, -1);
        Steps(c, 29);
        Assert.Equal(BricksCore.T, c.Type);
        Assert.Equal(29, c.LockT);
        var pi = c.Pi;
        c.Step();
        Assert.Equal(pi + 1, c.Pi);                  // зафіксувалась на 30-му тику
        Assert.Equal("0003330000", Row(c, 0));

        var d = Put(BricksCore.T, 3, -1);
        for (var i = 0; i < 15; i++)
        {
            Steps(d, 20);
            d.Apply(i % 2 == 0 ? BricksCore.KLeft : BricksCore.KRight);
            d.Apply(i % 2 == 0 ? BricksCore.KLeftUp : BricksCore.KRightUp);
            Assert.Equal(0, d.LockT);
        }
        Assert.Equal(15, d.Resets);
        Steps(d, 20);
        d.Apply(BricksCore.KLeft);                  // шістнадцятий рух — затримку вже не скидає
        d.Apply(BricksCore.KLeftUp);
        Assert.Equal(20, d.LockT);
        Steps(d, 9);
        Assert.Equal(BricksCore.T, d.Type);
        d.Step();
        Assert.Equal(2, d.Pi);
    }

    [Fact]
    public void Falling_to_a_lower_row_restores_the_reset_budget()
    {
        // Полиця з п'яти цеглин: T їздить по ній, вичерпує скидання, з'їжджає з краю — і падає нижче.
        var c = Put(BricksCore.T, 0, 0, 0, ["8888800000"]);
        for (var i = 0; i < 15; i++)
        {
            c.Apply(i % 2 == 0 ? BricksCore.KRight : BricksCore.KLeft);
            c.Apply(i % 2 == 0 ? BricksCore.KRightUp : BricksCore.KLeftUp);
        }
        Assert.Equal(15, c.Resets);
        for (var i = 0; i < 4; i++)
        {
            c.Apply(BricksCore.KRight);
            c.Apply(BricksCore.KRightUp);
        }
        Assert.Equal(5, c.Bx);
        Steps(c, 60);                                 // у повітрі затримка не тікає; за 48 тиків упала на ряд нижче
        Assert.Equal(-1, c.By);
        Assert.Equal(-1, c.LowY);
        Assert.Equal(0, c.Resets);
        Assert.Equal(BricksCore.T, c.Type);
    }

    [Fact]
    public void Hold_swaps_once_per_piece_and_pulls_from_the_queue_when_empty()
    {
        var c = new BricksCore(4242, N, 1800);
        var p0 = c.Type;
        c.Apply(BricksCore.KHold);
        Assert.Equal(p0, c.Hold);
        Assert.Equal(c.PieceAt(1), c.Type);
        Assert.Equal(2, c.Pi);
        Assert.True(c.HoldUsed);

        var before = c.Hash();
        c.Apply(BricksCore.KHold);                  // удруге за ту саму фігурку — нічого
        Assert.Equal(before, c.Hash());

        c.Apply(BricksCore.KHard);
        Assert.Equal(c.PieceAt(2), c.Type);
        Assert.False(c.HoldUsed);
        var p2 = c.Type;
        c.Apply(BricksCore.KHold);                  // із кишені, черга не рухається
        Assert.Equal(p0, c.Type);
        Assert.Equal(p2, c.Hold);
        Assert.Equal(3, c.Pi);
        Assert.Equal(p0 == BricksCore.I ? 18 : 19, c.By);
    }

    [Fact]
    public void Clearing_pause_lasts_10_ticks_then_rows_collapse_and_next_piece_spawns()
    {
        var c = Put(BricksCore.I, 0, -2, 0, ["0000888888", "0000000001"]);
        c.Apply(BricksCore.KHard);
        Assert.Equal(-1, c.Type);
        Assert.Equal(10, c.Clearing);
        Assert.Equal(1, c.Lines);
        Steps(c, 9);
        Assert.Equal(-1, c.Type);
        Assert.Equal("1111888888", Row(c, 0));      // ряд ще стоїть (блимає)
        c.Step();
        Assert.Equal("0000000001", Row(c, 0));      // упав
        Assert.True(c.Type >= 0);
        Assert.Equal(0, c.Clearing);
    }

    [Fact]
    public void Attack_table_normal_is_0_1_2_4_and_tspin_is_2_4_6()
    {
        int[] normal = [0, 0, 1, 2, 4];
        for (var n = 1; n <= 4; n++)
        {
            var e = Drop(Well(n));
            Assert.Equal(n, e.Lines);
            Assert.Equal(normal[n], e.Attack);
            Assert.Equal(0, e.Kind);
        }

        var tss = Put(BricksCore.T, 3, 0, 2, ["8888088888", "8880000000", "0008000000"]);
        tss.LastRot = true;
        var s = Drop(tss);
        Assert.Equal((1, 1, 2), (s.Lines, s.Kind, s.Attack));

        var tsd = Put(BricksCore.T, 3, 0, 2, ["8888088888", "8880008888", "0008000000"]);
        tsd.LastRot = true;
        var d = Drop(tsd);
        Assert.Equal((2, 1, 4), (d.Lines, d.Kind, d.Attack));

        var tst = Put(BricksCore.T, 3, 0, 3, ["8888088888", "8880088888", "8888088888", "8000000000"]);
        tst.LastRot = true;
        var t = Drop(tst);
        Assert.Equal((3, 1, 6), (t.Lines, t.Kind, t.Attack));
    }

    [Fact]
    public void Tspin_needs_three_corners_and_a_rotation_as_last_move()
    {
        // той самий T, покладений рухом, — звичайна двійка
        var moved = Put(BricksCore.T, 3, 0, 2, ["8888088888", "8880008888", "0008000000"]);
        var m = Drop(moved);
        Assert.Equal((2, 0, 1), (m.Lines, m.Kind, m.Attack));

        // оберт є, але кутів лише два
        var open = Put(BricksCore.T, 3, 0, 2, ["8888088888", "8880008888", "0000000001"]);
        open.LastRot = true;
        var e = Drop(open);
        Assert.Equal((0, 1), (e.Kind, e.Attack));

        // справжній оберт у ямку: T стоймя (R) опускається м'яко на дно ямки, оберт останнім — Т-оберт,
        // і жорстке падіння без руху цього не ламає
        var real = Put(BricksCore.T, 3, 6, 1, ["8888088888", "8880008888", "0008000000"]);
        real.Apply(BricksCore.KSoft);
        Steps(real, 12);
        Assert.Equal(0, real.By);
        real.Apply(BricksCore.KCw);
        Assert.Equal((3, 0, 2), (real.Bx, real.By, real.Rot));
        Assert.True(real.LastRot);
        real.Apply(BricksCore.KHard);
        Assert.Equal(1, real.ClearCount);
        Assert.Equal((2, 1, 4), (real.Clears[0].Lines, real.Clears[0].Kind, real.Clears[0].Attack));
    }

    [Fact]
    public void Back_to_back_adds_one_only_from_the_second_strong_clear_and_breaks_on_a_weak_one()
    {
        var first = Well(4);
        Assert.Equal(4, Drop(first).Attack);
        Assert.Equal(0, first.B2b);

        var second = Well(4);
        second.B2b = 0;
        var e = Drop(second);
        Assert.Equal((5, 1), (e.Attack, e.B2b));

        var tspin = Put(BricksCore.T, 3, 0, 2, ["8888088888", "8880008888", "0008000000"]);
        tspin.LastRot = true;
        tspin.B2b = 1;
        Assert.Equal(5, Drop(tspin).Attack);        // Т-оберт — теж «сильне»: 4 + 1
        Assert.Equal(2, tspin.B2b);

        var weak = Well(3);
        weak.B2b = 2;
        Assert.Equal(2, Drop(weak).Attack);
        Assert.Equal(-1, weak.B2b);

        var none = Put(BricksCore.O, 0, 5);
        none.B2b = 3;
        none.Apply(BricksCore.KHard);               // фіксація без рядів поспіль не чіпає
        Assert.Equal(3, none.B2b);
        Assert.Equal(0, none.ClearCount);
    }

    [Fact]
    public void Combo_bonus_grows_1_1_2_2_3_and_resets_on_a_lock_without_lines()
    {
        int[] bonus = [0, 1, 1, 2, 2, 3, 3, 3];
        for (var k = 0; k < bonus.Length; k++)
        {
            var c = Well(2);
            c.Combo = k - 1;
            var e = Drop(c);
            Assert.Equal(k, e.Combo);
            Assert.Equal(1 + bonus[k], e.Attack);
        }
        var o = Put(BricksCore.O, 0, 5);
        o.Combo = 4;
        o.Apply(BricksCore.KHard);
        Assert.Equal(-1, o.Combo);
    }

    [Fact]
    public void Perfect_clear_adds_four()
    {
        var two = Put(BricksCore.O, 7, 5, 0, ["8888888800", "8888888800"]);
        var e = Drop(two);
        Assert.Equal((2, 2, 5), (e.Lines, e.Kind, e.Attack));

        var four = Put(BricksCore.I, 7, 10, 1, ["8888888880", "8888888880", "8888888880", "8888888880"]);
        var f = Drop(four);
        Assert.Equal((4, 2, 8), (f.Lines, f.Kind, f.Attack));
    }

    [Fact]
    public void Hard_garbage_mode_adds_one_to_every_attack_and_none_mode_sends_nothing()
    {
        Assert.Equal(2, Drop(Well(1, BricksCore.ModeHard)).Attack);
        Assert.Equal(2, Drop(Well(2, BricksCore.ModeHard)).Attack);
        Assert.Equal(3, Drop(Well(3, BricksCore.ModeHard)).Attack);
        Assert.Equal(5, Drop(Well(4, BricksCore.ModeHard)).Attack);
        var none = Well(4, BricksCore.ModeNone);
        Assert.Equal(0, Drop(none).Attack);
        Assert.Equal(4, none.Lines);                  // ряди рахуються, сміття — ні
        var tsd = Put(BricksCore.T, 3, 0, 2, ["8888088888", "8880008888", "0008000000"], BricksCore.ModeNone);
        tsd.LastRot = true;
        Assert.Equal(0, Drop(tsd).Attack);
    }

    [Fact]
    public void Own_attack_cancels_pending_garbage_oldest_first_and_only_the_rest_is_sent()
    {
        var c = Well(4);
        c.AddCredit(1, 2, 0, 1000, 1);
        c.AddCredit(2, 3, 5, 1000, 2);
        var e = Drop(c);
        Assert.Equal((4, 0), (e.Attack, e.Sent));
        Assert.Equal(1, c.CreditCount);
        Assert.Equal((2, 1), (c.Credits[0].G, c.Credits[0].Rows));
        Assert.Equal(0, c.Sent);

        var d = Well(4);
        d.AddCredit(1, 1, 0, 0, 1);
        Assert.Equal(3, Drop(d).Sent);
        Assert.Equal(3, d.Sent);
        Assert.Equal(0, d.CreditCount);
    }

    [Fact]
    public void Garbage_enters_only_on_a_lock_without_lines_and_only_when_ripe()
    {
        var c = Put(BricksCore.O, 0, 10);
        c.Tick = 100;
        c.AddCredit(1, 2, 3, 130, 1);
        Assert.Equal((2, 0), (c.PendingRows, c.RipeRows));
        c.Apply(BricksCore.KHard);                   // ще не дозріло
        Assert.Equal(0, c.Recv);
        Assert.Equal(1, c.CreditCount);
        Assert.Equal("0220000000", Row(c, 0));

        c.Tick = 130;
        Assert.Equal(2, c.RipeRows);
        c.Bx = 6;
        c.Apply(BricksCore.KHard);                   // дозріло, фіксація без рядів — лізе
        Assert.Equal(2, c.Recv);
        Assert.Equal(0, c.CreditCount);
        Assert.Equal("8880888888", Row(c, 0));
        Assert.Equal("8880888888", Row(c, 1));

        var lines = Well(1);
        lines.AddCredit(1, 2, 4, 0, 1);              // дозріле, але фіксація з рядом — не лізе
        Drop(lines);
        Assert.Equal(0, lines.Recv);
        Assert.Equal(2, lines.PendingRows);
    }

    [Fact]
    public void At_most_eight_garbage_rows_enter_per_lock_the_rest_waits()
    {
        var c = Put(BricksCore.O, 0, 10);
        c.AddCredit(1, 5, 1, 0, 0);
        c.AddCredit(2, 6, 2, 0, 0);
        c.Apply(BricksCore.KHard);
        Assert.Equal(8, c.Recv);
        Assert.Equal(1, c.CreditCount);
        Assert.Equal((2, 3), (c.Credits[0].G, c.Credits[0].Rows));
        // найстаріша посилка — верхня частина нового шару
        for (var y = 3; y < 8; y++) Assert.Equal("8088888888", Row(c, y));
        for (var y = 0; y < 3; y++) Assert.Equal("8808888888", Row(c, y));
        Assert.Equal("0220000000", Row(c, 8));
    }

    [Fact]
    public void Garbage_rows_share_the_credit_hole_and_push_the_wall_up()
    {
        var c = Put(BricksCore.O, 5, 10, 0, ["1000000000"]);
        c.AddCredit(1, 2, 7, 0, 3);
        c.Apply(BricksCore.KHard);
        Assert.Equal("8888888088", Row(c, 0));
        Assert.Equal("8888888088", Row(c, 1));
        Assert.Equal("1000002200", Row(c, 2));
        Assert.Equal("0000002200", Row(c, 3));
        Assert.True(c.Alive);
    }

    [Fact]
    public void Garbage_reaching_row_22_tops_the_wall_out()
    {
        var rows = Enumerable.Repeat("8000000000", 16).ToArray();   // верх — ряд 15
        var ok = Put(BricksCore.O, 5, 17, 0, rows);
        ok.AddCredit(1, 6, 0, 0, 1);
        ok.Apply(BricksCore.KHard);
        Assert.True(ok.Alive);                        // 15 + 6 = 21

        var bad = Put(BricksCore.O, 5, 17, 0, rows);
        bad.AddCredit(1, 7, 0, 0, 1);
        bad.Apply(BricksCore.KHard);
        Assert.False(bad.Alive);
        Assert.Equal(BricksCore.OutGarbage, bad.OutWhy);
        Assert.Equal(-1, bad.Type);
    }

    [Fact]
    public void Spawn_into_filled_cells_tops_out()
    {
        var rows = new string[21];
        for (var i = 0; i < 20; i++) rows[i] = "";
        rows[20] = "0000100000";
        var c = new BricksCore(1, N, 1800);
        c.LoadRows(rows);
        c.Spawn(BricksCore.T);
        Assert.False(c.Alive);
        Assert.Equal(BricksCore.OutSpawn, c.OutWhy);
    }

    [Fact]
    public void Lock_entirely_above_row_20_tops_out()
    {
        var rows = new string[21];
        for (var i = 0; i < 20; i++) rows[i] = "";
        rows[20] = "0000100000";
        var c = Put(BricksCore.T, 3, 20, 0, rows);
        c.Apply(BricksCore.KHard);
        Assert.False(c.Alive);
        Assert.Equal(BricksCore.OutLock, c.OutWhy);

        // а якщо хоч одна клітинка нижче ряду 20 — живе (I стоймя в стовпці 9, верх — у ряду 20)
        var rows16 = Enumerable.Repeat("", 16).Append("0000000001").ToArray();
        var d = Put(BricksCore.I, 7, 17, 1, rows16);
        d.Apply(BricksCore.KHard);
        Assert.True(d.Alive);
        Assert.Equal("0000000001", Row(d, 20));
    }

    [Fact]
    public void Clone_and_restore_give_identical_hash()
    {
        var c = new BricksCore(55, BricksCore.ModeHard, 300);
        var ev = BricksFixtures.Gen(55, 60);
        for (var i = 0; i < ev.Length; i += 2) { c.AdvanceTo(ev[i]); c.Apply(ev[i + 1]); }
        c.AddCredit(1, 3, 4, c.Tick + 30, 2);
        var snap = c.Clone();
        var h = c.Hash();
        c.Apply(BricksCore.KLeft);
        Steps(c, 77);
        c.Apply(BricksCore.KHard);
        if (snap.Alive) Assert.NotEqual(h, c.Hash());
        c.CopyFrom(snap);
        Assert.Equal(h, c.Hash());
        Assert.Equal(snap.Seq, c.Seq);
        Assert.Equal(snap.CellsVer, c.CellsVer);
    }

    [Fact]
    public void Wire_rows_are_trimmed_strings_of_ten_digits_bottom_up()
    {
        var c = new BricksCore(1, N, 1800);
        Assert.Empty(c.WireRows());
        c.LoadRows(["1234567000", "", "0000000008"]);
        Assert.Equal(["1234567000", "0000000000", "0000000008"], c.WireRows());
    }

    [Fact]
    public void Wire_round_trip_restores_the_same_hash()
    {
        var seat = new BricksSeat { Nick = "Оля" };
        var c = seat.Core;
        c.Reset(9, BricksCore.ModeHard, 600);
        var ev = BricksFixtures.Gen(9, 120);
        for (var i = 0; i < ev.Length && c.Alive; i += 2) { c.AdvanceTo(ev[i]); c.Apply(ev[i + 1]); c.Seq++; }
        c.Apply(BricksCore.KRight);                   // посеред DAS
        c.Step();
        c.AddCredit(c.Gseq + 1, 2, 6, c.Tick + 30, 1);
        c.AddCredit(c.Gseq + 1, 1, 3, c.Tick, -1);

        var wire = Views.Json(BricksWire.Board(2, seat));
        Assert.Equal(2, wire.GetProperty("s").GetInt32());
        Assert.Equal("Оля", wire.GetProperty("nk").GetString());
        foreach (var r in wire.GetProperty("r").EnumerateArray()) Assert.Matches("^[0-8]{10}$", r.GetString()!);
        Assert.Equal(c.Hash(), wire.GetProperty("h").GetUInt32());

        var back = new BricksCore();
        back.LoadWire(wire, 9, BricksCore.ModeHard, 600);
        Assert.Equal(c.Hash(), back.Hash());
        // і далі живе однаково
        c.Apply(BricksCore.KCw); back.Apply(BricksCore.KCw);
        Steps(c, 200); Steps(back, 200);
        Assert.Equal(c.Hash(), back.Hash());
    }

    [Fact]
    public void Step_and_lock_allocate_nothing()
    {
        var c = new BricksCore(3, N, 60);
        int[] keys = [1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 0];
        void Run(int n)
        {
            for (var i = 0; i < n; i++)
            {
                if (!c.Alive) c.Reset((uint)(i + 3), N, 60);
                if (i % 7 == 0) c.Apply(keys[i / 7 % keys.Length]);
                if (i % 97 == 0) c.AddCredit(c.Gseq + 1, 1 + i % 3, i % 10, c.Tick + 30, 1);
                c.Step();
                c.ClearCount = 0;
                if (i % 50 == 0) c.Hash();
            }
        }
        Run(20_000);                                   // прогрів: JIT, кеш мішка
        var before = GC.GetAllocatedBytesForCurrentThread();
        Run(10_000);
        var after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }

    // =============================================================================================
    // 8.4 Детермінізм C# ↔ JS: зафіксовані журнали
    // =============================================================================================

    public static IEnumerable<object[]> FixtureNames() => BricksFixtures.All.Select(f => new object[] { f.Name });

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Fixture_journal_gives_the_pinned_hash(string name)
    {
        var fx = BricksFixtures.All.Single(f => f.Name == name);
        var c = BricksFixtures.Run(fx);
        Assert.True(fx.Hash == c.Hash(), $"{name}: хеш {c.Hash()}, рядів {c.Lines}, живий {c.Alive}, тик {c.Tick}");
        Assert.Equal(fx.Lines, c.Lines);
        // і вдруге — те саме (жодного стану поза стіною)
        Assert.Equal(c.Hash(), BricksFixtures.Run(fx).Hash());
    }

    [Fact]
    public void Fixture_journals_really_exercise_the_rules()
    {
        // Щоб зафіксовані хеші щось перевіряли, журнали мають зачепити те, заради чого їх писали.
        var tspins = BricksFixtures.Run(BricksFixtures.All.Single(f => f.Name == "srs_kicks_and_tspins"));
        Assert.True(tspins.Lines > 0);
        var garbage = BricksFixtures.Run(BricksFixtures.All.Single(f => f.Name == "garbage_and_top_out"));
        Assert.True(garbage.Recv > 0);
        Assert.False(garbage.Alive);
        var hard = BricksFixtures.Run(BricksFixtures.All.Single(f => f.Name == "hard_mode_with_credits"));
        Assert.True(hard.Recv > 0);
        var bot = BricksFixtures.Run(BricksFixtures.All.Single(f => f.Name == "bot_game"));
        Assert.True(bot.Lines >= 20, $"бот закрив {bot.Lines}");
        Assert.True(bot.Recv > 0);
        var fast = BricksFixtures.Run(BricksFixtures.All.Single(f => f.Name == "bot_hard_fast"));
        Assert.True(fast.Stage >= 5, $"етап {fast.Stage}");
    }

    [Fact]
    public void Suite_of_random_journals_with_credits_gives_the_pinned_hash()
    {
        Assert.Equal(BricksFixtures.SuiteHash, BricksFixtures.Suite());
    }

    [Fact]
    public void Selftest_stand_carries_the_same_fixtures_and_pins_as_the_tests()
    {
        var html = File.ReadAllText(Paths.Resolve("docs/games/dev/bricks-selftest.html"));
        var m = Regex.Match(html, "<script id=\"bricks-fx\" type=\"application/json\">(.*?)</script>", RegexOptions.Singleline);
        Assert.True(m.Success, "у стенді нема блоку bricks-fx");
        var want = BricksFixtures.StandJson();
        using var doc = JsonDocument.Parse(m.Groups[1].Value);
        var got = JsonSerializer.Serialize(doc.RootElement);
        if (got != want) File.WriteAllText(Path.Combine(Path.GetTempPath(), "bricks-fx.json"), want);
        Assert.True(got == want, "стенд розійшовся з тестами: справжній блок — у %TEMP%/bricks-fx.json");
    }

    // =============================================================================================
    // 8.2 Кімната
    // =============================================================================================

    static RoomHarness Table(int players = 2, object? options = null, int seed = 42)
    {
        var h = new RoomHarness("bricks", options, seed: seed);
        foreach (var nick in Nicks.Take(players)) h.Join(nick);
        h.Start();
        return h;
    }

    static Bricks Game(RoomHarness h) => (Bricks)h.Room.Game;

    static BricksCore Wall(RoomHarness h, int seat) => Game(h).SeatState(seat).Core;

    static void Go(RoomHarness h)
    {
        for (var i = 0; i < 200 && Game(h).Phase != Bricks.PhaseGo; i++) h.Tick();
        Assert.Equal(Bricks.PhaseGo, Game(h).Phase);
    }

    /// <summary>Годинник уперед на стільки тиків стіни (60 Гц) — без тиків кімнати, тож сервер нічого сам не веде.</summary>
    static void Later(RoomHarness h, int wallTicks) => h.Clock.AdvanceMs((int)Math.Ceiling(wallTicks * 1000.0 / 60));

    /// <summary>Пачка журналу з наступним номером.</summary>
    static ActResult J(RoomHarness h, int seat, params int[] e) => h.Act(seat, "j", new { q = Wall(h, seat).Seq + 1, e });

    static ActResult Jq(RoomHarness h, int seat, int q, int[] e, uint? hash = null, int? g = null, int? f = null) =>
        h.Act(seat, "j", new { q, e, h = hash, g, f });

    /// <summary>Стіна по вінця (стовпець 9 порожній, щоб без рядів) і фігурка на появі — жорстке падіння її валить.</summary>
    static void Kill(RoomHarness h, int seat)
    {
        var c = Wall(h, seat);
        c.LoadRows(Enumerable.Repeat("8888888880", 20).ToArray());
        c.Clearing = 0;
        c.Spawn(c.Type < 0 ? BricksCore.T : c.Type);
        Assert.True(J(h, seat, c.Tick, BricksCore.KHard).Ok);
        Assert.False(c.Alive);
    }

    /// <summary>Четвірка: колодязь у стовпці 9 і вертикальна I над ним.</summary>
    static void Four(RoomHarness h, int seat)
    {
        var c = Wall(h, seat);
        c.LoadRows(["8888888880", "8888888880", "8888888880", "8888888880", "8000000000"]);
        c.Clearing = 0;
        c.Spawn(BricksCore.I);
        (c.Rot, c.Bx, c.By, c.LowY) = (1, 7, 10, 10);
        Assert.True(J(h, seat, c.Tick, BricksCore.KHard).Ok);
    }

    /// <summary>Двійка (напад 1): дві смуги з діркою 8–9 і O туди.</summary>
    static void Double(RoomHarness h, int seat)
    {
        var c = Wall(h, seat);
        c.LoadRows(["8888888800", "8888888800", "8000000000"]);
        c.Clearing = 0;
        c.Spawn(BricksCore.O);
        (c.Bx, c.By, c.LowY) = (7, 10, 10);
        Assert.True(J(h, seat, c.Tick, BricksCore.KHard).Ok);
    }

    static List<JsonElement> Frames(RoomHarness h) => [.. h.Outbox.OfType<RoomFrame>().Select(f => Views.Json(f.Frame))];

    static List<JsonElement> Events(IEnumerable<JsonElement> frames, string kind) =>
        [.. frames.SelectMany(f => f.GetProperty("ev").EnumerateArray()).Where(e => e[0].GetString() == kind)];

    [Fact]
    public void Catalog_lists_bricks_as_live_2_to_4_byhost_tick_40_with_three_options_and_css()
    {
        var game = Assert.Single(new Registry().Catalog, g => g.Id == "bricks");
        Assert.Equal("live", game.Group);
        Assert.Equal((2, 4, 40), (game.MinPlayers, game.MaxPlayers, game.TickMs));
        Assert.Equal("byHost", game.Start);
        Assert.False(game.Hidden);
        Assert.False(game.Rated);
        Assert.Equal(["wins", "speed", "garbage"], game.Options.Select(o => o.Key));
        Assert.Equal(["1", "normal", "normal"], game.Options.Select(o => o.Default));
        Assert.Equal("bricks", game.Module);
        Assert.True(game.HasCss);
        Assert.True(File.Exists(Paths.Resolve("web/games/bricks.js")));
        Assert.DoesNotContain("Тетріс", game.Hint, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["теракота", "бірюза", "олива", "кобальт"], Enumerable.Range(0, 4).Select(i => new Bricks().SeatName(i)));
    }

    [Fact]
    public void Round_starts_with_a_75_tick_countdown_and_the_same_seed_for_every_seat()
    {
        var h = Table(3);
        var v = h.View(0);
        Assert.Equal("start", v.GetProperty("phase").GetString());
        Assert.Equal(75, v.GetProperty("startIn").GetInt32());
        Assert.Equal(3, v.GetProperty("boards").GetArrayLength());
        var seed = v.GetProperty("seed").GetUInt32();
        Assert.NotEqual(0u, seed);
        for (var s = 0; s < 3; s++)
        {
            Assert.Equal(seed, Wall(h, s).Seed);
            Assert.Equal(Wall(h, 0).Type, Wall(h, s).Type);
        }
        h.Tick(74);
        Assert.Equal(Bricks.PhaseStart, Game(h).Phase);
        Assert.Equal(0, Wall(h, 0).Tick);              // на відліку стіна стоїть
        h.Tick();
        Assert.Equal(Bricks.PhaseGo, Game(h).Phase);
        Assert.Equal([1, 2, 0, -1], h.View(null).GetProperty("target").EnumerateArray().Select(x => x.GetInt32()));
    }

    [Fact]
    public void Journal_is_rejected_before_go_with_a_human_message()
    {
        var h = new RoomHarness("bricks", seed: 3);
        h.Join("Оля");
        h.Join("Петро");
        Assert.False(h.Act(0, "j", new { q = 1, e = new[] { 0, 1 } }).Ok);   // лобі: каркас ще й не питає гру
        h.Start();
        var r = J(h, 0, 0, BricksCore.KLeft);
        Assert.Equal((false, "Зачекай, зараз почнемо"), (r.Ok, r.Message));
        Assert.Equal(0, Wall(h, 0).Seq);
        Assert.Equal("Зараз нема чого звіряти", h.Act(0, "sync").Message);
        Assert.Equal("Тут так не ходять", h.Act(0, "ready").Message);
        Assert.Equal("Тут так не ходять", h.Act(0, "move", new { dir = 1 }).Message);
    }

    [Fact]
    public void A_valid_journal_moves_the_server_wall_exactly_like_the_core()
    {
        var h = Table(2);
        Go(h);
        int[] e = [3, 1, 5, 2, 12, 7, 20, 5, 40, 6, 41, 10, 60, 3, 70, 4, 90, 0];
        Later(h, 100);
        var local = new BricksCore(Game(h).Seed, N, 1800);
        for (var i = 0; i < e.Length; i += 2) { local.AdvanceTo(e[i]); local.Apply(e[i + 1]); }
        var r = Jq(h, 0, 1, e, local.Hash());
        Assert.True(r.Ok, r.Message);
        var w = Wall(h, 0);
        Assert.Equal(local.Hash(), w.Hash());
        Assert.Equal(9, w.Seq);
        Assert.Equal(90, w.Tick);
        Assert.False(Game(h).SeatState(0).NeedFix);
        Assert.Equal(0, Wall(h, 1).Tick);              // чужу стіну не чіпає
    }

    [Fact]
    public void Stale_batch_is_ignored_silently_and_a_gap_triggers_a_fix()
    {
        var h = Table(2);
        Go(h);
        Assert.True(Jq(h, 0, 1, [5, 1, 6, 2]).Ok);
        var hash = Wall(h, 0).Hash();
        Assert.True(Jq(h, 0, 1, [5, 1, 6, 2]).Ok);     // дублікат — мовчки
        Assert.Equal(hash, Wall(h, 0).Hash());
        Assert.False(Game(h).SeatState(0).NeedFix);
        Assert.True(Jq(h, 0, 2, [6, 2, 7, 3]).Ok);     // перекриття: голову пропускаємо
        Assert.Equal(3, Wall(h, 0).Seq);

        var r = Jq(h, 0, 9, [8, 0]);
        Assert.Equal((false, "Журнал із дірою"), (r.Ok, r.Message));
        Assert.True(Game(h).SeatState(0).NeedFix);
        var was = Frames(h).Count;
        h.Tick();
        var fix = Assert.Single(Events(Frames(h).Skip(was), "f"));
        Assert.Equal(0, fix[1].GetInt32());
        Assert.Equal(3, fix[2].GetProperty("q").GetInt32());
        Assert.Equal(1, fix[2].GetProperty("fx").GetInt32());
    }

    [Fact]
    public void Journal_from_the_past_and_from_the_future_are_rejected_with_a_fix()
    {
        var h = Table(2);
        Go(h);
        Later(h, 30);
        Assert.True(J(h, 0, 20, 1).Ok);
        var r = J(h, 0, 19, 2);
        Assert.Equal((false, "Журнал із минулого"), (r.Ok, r.Message));
        var wall = Game(h).WallTick;
        r = J(h, 0, wall + BricksCore.Ahead + 1, 2);
        Assert.Equal((false, "Журнал із майбутнього"), (r.Ok, r.Message));
        Assert.True(J(h, 0, wall + BricksCore.Ahead, 2).Ok);   // рівно на межі — можна
        Assert.True(Game(h).SeatState(0).NeedFix);

        // спадний час усередині пачки — теж минуле
        var h2 = Table(2);
        Go(h2);
        r = J(h2, 1, 10, 1, 9, 2);
        Assert.Equal("Журнал із минулого", r.Message);
        Assert.Equal(0, Wall(h2, 1).Seq);
    }

    [Fact]
    public void More_than_four_events_per_tick_are_rejected()
    {
        var h = Table(2);
        Go(h);
        Assert.True(J(h, 0, 5, 1, 5, 2, 5, 3, 5, 4).Ok);
        var r = J(h, 1, 5, 1, 5, 2, 5, 3, 5, 4, 5, 7);
        Assert.Equal((false, "Забагато натисків за раз"), (r.Ok, r.Message));
        Assert.Equal(0, Wall(h, 1).Seq);
    }

    [Fact]
    public void Malformed_journal_is_rejected_and_the_wall_is_untouched()
    {
        var h = Table(2);
        Go(h);
        var hash = Wall(h, 0).Hash();
        object[] bad =
        [
            "рядок",
            new { q = 1 },
            new { q = 1, e = "1,2" },
            new { q = 1, e = new[] { 1, 2, 3 } },
            new { q = 1, e = new[] { 1, 9 } },
            new { q = 1, e = new[] { -1, 1 } },
            new { q = 1, e = new[] { 1.5, 1 } },
            new { q = 0, e = new[] { 1, 1 } },
            new { q = 1, e = Enumerable.Repeat(0, 130).ToArray() },
            new { q = 1, e = Array.Empty<int>() },
            new { q = 1, e = new[] { 1, 1 }, h = "x" },
            new { seq = 1, events = new[] { 1, 1 } },
        ];
        foreach (var p in bad)
        {
            var r = h.Act(0, "j", p);
            Assert.Equal((false, "Журнал не читається"), (r.Ok, r.Message));
        }
        Assert.Equal(hash, Wall(h, 0).Hash());
        Assert.Equal(0, Wall(h, 0).Seq);
    }

    [Fact]
    public void Hash_mismatch_yields_a_fix_event_at_most_once_per_second()
    {
        var h = Table(2);
        Go(h);
        Assert.True(Jq(h, 0, 1, [2, 1], hash: 12345).Ok);   // прийнято, але розійшлось
        Assert.True(Game(h).SeatState(0).NeedFix);
        var was = Frames(h).Count;
        h.Tick();
        Assert.Single(Events(Frames(h).Skip(was), "f"));

        Assert.True(Jq(h, 0, 2, [3, 2], hash: 999).Ok);
        was = Frames(h).Count;
        h.Tick(BricksJournal.FixEvery - 2);
        Assert.Empty(Events(Frames(h).Skip(was), "f"));
        h.Tick(2);
        Assert.Single(Events(Frames(h).Skip(was), "f"));

        // «sync» — те саме й з тією самою стелею
        Assert.True(h.Act(0, "sync").Ok);
        Assert.True(h.Act(0, "sync").Ok);
        was = Frames(h).Count;
        h.Tick(BricksJournal.FixEvery);
        Assert.Single(Events(Frames(h).Skip(was), "f"));
    }

    [Fact]
    public void Batch_sent_before_the_fix_arrived_is_dropped_by_its_epoch()
    {
        var h = Table(2);
        Go(h);
        Assert.True(h.Act(0, "sync").Ok);
        h.Tick();
        var st = Game(h).SeatState(0);
        Assert.Equal(1, st.Epoch);
        Assert.True(Jq(h, 0, 1, [3, 1], f: 0).Ok);            // летіла ще до виправлення — мимо
        Assert.Equal(0, Wall(h, 0).Seq);
        Assert.True(Jq(h, 0, 1, [3, 1], f: 1).Ok);
        Assert.Equal(1, Wall(h, 0).Seq);
        Assert.Equal("Журнал не читається", Jq(h, 0, 2, [4, 2], f: 7).Message);
    }

    [Fact]
    public void Hash_is_not_checked_while_the_client_has_not_seen_the_latest_credit()
    {
        var h = Table(2);
        Go(h);
        Assert.True(J(h, 0, 2, 0).Ok);
        Four(h, 1);                                             // сміття летить місцю 0 — у його стіні посилка g = 1
        Assert.Equal(1, Wall(h, 0).Gseq);
        var st = Game(h).SeatState(0);
        Assert.True(Jq(h, 0, 2, [3, 1], hash: 1, g: 0).Ok);     // ще не знав про посилку — хеш іншим і має бути
        Assert.False(st.NeedFix);
        Assert.True(Jq(h, 0, 3, [4, 2], hash: 1, g: 1).Ok);     // знав — тоді вже розбіжність
        Assert.True(st.NeedFix);
        st.NeedFix = false;
        Assert.True(Jq(h, 0, 4, [5, 0], hash: Wall(h, 0).Hash() + 1, g: 5).Ok);   // знає більше за сервер — вигадка
        Assert.True(st.NeedFix);
    }

    [Fact]
    public void Attack_credits_the_next_alive_seat_clockwise_with_a_server_chosen_hole()
    {
        var h = Table(4);
        Go(h);
        var was = Frames(h).Count;
        Four(h, 3);                                             // 3 → 0 по колу
        var c0 = Wall(h, 0);
        Assert.Equal(1, c0.CreditCount);
        Assert.Equal((1, 4, 3), (c0.Credits[0].G, c0.Credits[0].Rows, c0.Credits[0].From));
        Assert.InRange(c0.Credits[0].Hole, 0, 9);
        h.Tick();
        var g = Assert.Single(Events(Frames(h).Skip(was), "g"));
        Assert.Equal(0, g[1].GetInt32());
        Assert.Equal(c0.Credits[0].Hole, g[4].GetInt32());
        var c = Assert.Single(Events(Frames(h).Skip(was), "c"));
        Assert.Equal([3, 4, 0, 0, 0, 4, 0], c.EnumerateArray().Skip(1).Select(x => x.GetInt32()));

        Kill(h, 1);                                             // 1 вибув — 0 тепер цілить у 2
        Assert.Equal([2, -1, 3, 0], h.View(null).GetProperty("target").EnumerateArray().Select(x => x.GetInt32()));
        Wall(h, 0).CreditCount = 0;                             // інакше свій напад загасив би ті чотири
        Double(h, 0);
        Assert.Equal(1, Wall(h, 2).PendingRows);
        Assert.Equal(0, Wall(h, 1).CreditCount);

        // дірки обирає сервер, і вони бувають різні
        var holes = new HashSet<int>();
        for (var i = 0; i < 12; i++) { Double(h, 3); holes.Add(Wall(h, 0).Credits[Wall(h, 0).CreditCount - 1].Hole); }
        Assert.True(holes.Count > 1);
    }

    [Fact]
    public void Credit_is_positioned_at_the_victims_current_tick_and_seq_and_ripens_30_ticks_later()
    {
        var h = Table(2);
        Go(h);
        Later(h, 60);
        Assert.True(J(h, 1, 30, 0, 31, 1).Ok);                   // жертва на тику 31, подія №2
        var was = Frames(h).Count;
        Four(h, 0);
        var cr = Wall(h, 1).Credits[0];
        Assert.Equal(31 + BricksCore.RipeTicks, cr.RipeAt);
        Assert.Equal(0, cr.From);
        h.Tick();
        var g = Assert.Single(Events(Frames(h).Skip(was), "g"));
        // ["g", seat, g, rows, hole, at, after, from]
        Assert.Equal(8, g.GetArrayLength());
        Assert.Equal((1, 1, 4, 31, 2, 0), (g[1].GetInt32(), g[2].GetInt32(), g[3].GetInt32(), g[5].GetInt32(), g[6].GetInt32(), g[7].GetInt32()));
        Assert.Equal(4, Wall(h, 1).PendingRows);
        Assert.Equal(0, Wall(h, 1).RipeRows);
    }

    [Fact]
    public void Lone_survivor_has_no_target_and_attacks_evaporate()
    {
        var h = Table(3);
        Go(h);
        Kill(h, 1);
        Kill(h, 2);
        Assert.Equal(-1, Game(h).Target(0));
        Assert.Equal([-1, -1, -1, -1], h.View(null).GetProperty("target").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(0, Wall(h, 1).CreditCount + Wall(h, 2).CreditCount);
    }

    [Fact]
    public void Silent_client_is_driven_by_the_server_after_90_ticks_and_falls_by_gravity()
    {
        var h = Table(2);
        Go(h);
        h.Tick(37);                                            // 1,48 с — ще чекаємо
        Assert.Equal(0, Wall(h, 0).Tick);
        h.Tick(3);                                             // понад 90 тиків мовчання — ведемо самі
        var w = Wall(h, 0);
        Assert.Equal(Game(h).WallTick - BricksCore.Behind, w.Tick);
        h.Tick(60);
        Assert.Equal(Game(h).WallTick - BricksCore.Behind, w.Tick);
        Assert.True(w.By < 19, "гравітація тягне фігурку вниз і без клієнта");
        // клієнт озвався зі старим часом — минуле й виправлення
        Assert.Equal("Журнал із минулого", J(h, 0, 5, 1).Message);
        Assert.True(Game(h).SeatState(0).NeedFix);
    }

    [Fact]
    public void Frame_is_sent_only_when_something_changed()
    {
        var h = Table(2);
        Go(h);
        h.Tick(2);
        var was = Frames(h).Count;
        h.Tick(3);
        Assert.Equal(was, Frames(h).Count);
        Assert.True(J(h, 0, 5, 0).Ok);                          // пульс: лише час
        h.Tick();
        Assert.Equal(was + 1, Frames(h).Count);
        h.Tick(18);                                            // до кінця першої секунди — тиша
        Assert.Equal(was + 1, Frames(h).Count);
        h.Tick();                                              // нова секунда годинника — один кадр
        Assert.Equal(was + 2, Frames(h).Count);
    }

    [Fact]
    public void Frame_carries_rows_only_for_changed_walls_plus_a_keyframe_every_25_ticks()
    {
        var h = Table(3);
        Go(h);
        var go = Frames(h).Last();
        Assert.Equal("go", go.GetProperty("ph").GetString());
        h.Tick(2);
        Assert.True(J(h, 0, 5, 1).Ok);
        h.Tick();
        var f = Frames(h).Last();
        var b = Assert.Single(f.GetProperty("b").EnumerateArray());
        Assert.Equal(0, b.GetProperty("s").GetInt32());
        Assert.False(b.TryGetProperty("r", out _));            // клітинки не змінились — рядів нема
        Assert.Equal(Wall(h, 0).Hash(), b.GetProperty("x").GetUInt32());

        Assert.True(J(h, 1, 6, BricksCore.KHard).Ok);
        h.Tick();
        b = Assert.Single(Frames(h).Last().GetProperty("b").EnumerateArray());
        Assert.Equal(1, b.GetProperty("s").GetInt32());
        Assert.InRange(b.GetProperty("r").GetArrayLength(), 1, 2);

        // ключовий: не рідше разу на 25 тиків кімнати, якщо кадр і так летить, — усі стіни з рядами
        var was = Frames(h).Count;
        h.Tick(Bricks.KeyEvery);
        var key = Frames(h).Skip(was).First(x => x.GetProperty("b").GetArrayLength() == 3);
        Assert.All(key.GetProperty("b").EnumerateArray(), x => Assert.True(x.TryGetProperty("r", out _)));
    }

    [Fact]
    public void Frame_of_four_walls_with_14_rows_and_six_events_fits_in_1500_bytes()
    {
        var h = Table(4);
        Go(h);
        h.Tick(2);
        string[] rows = ["8888888808", "8888888088", "6677700100", "0666770000", "3330222000", "0300222000", "1111000000",
            "0000440000", "0004400000", "0000055000", "0000550000", "0070000000", "0770000000", "0700000000"];
        for (var s = 0; s < 4; s++) { Wall(h, s).LoadRows(rows); Game(h).SeatState(s).Dirty = true; }
        // задом наперед: кожен б'є сусіда, який уже закрив свої ряди, тож гасити нападникові нічого — летять усі три
        foreach (var s in new[] { 2, 1, 0 })
        {
            var c = Wall(h, s);
            var r = rows.ToArray();
            r[0] = "8888888800";
            r[1] = "8888888800";
            c.LoadRows(r);
            c.Clearing = 0;
            c.Spawn(BricksCore.O);
            (c.Bx, c.By, c.LowY) = (7, 16, 16);
            Assert.True(J(h, s, c.Tick, BricksCore.KHard).Ok);
            Assert.Equal(2, c.Lines);
        }
        h.Tick();
        var frame = h.Outbox.OfType<RoomFrame>().Last().Frame;
        var json = Views.Text(frame);
        var el = Views.Json(frame);
        Assert.Equal(4, el.GetProperty("b").GetArrayLength());
        Assert.Equal(6, el.GetProperty("ev").GetArrayLength());
        Assert.All(el.GetProperty("b").EnumerateArray(), x => Assert.Equal(14, x.GetProperty("r").GetArrayLength()));
        var bytes = System.Text.Encoding.UTF8.GetByteCount(json);
        // вид на чотирьох із тими самими стінами — spec §8.5: ≤ 4 КБ (летить лише на подіях)
        var view = System.Text.Encoding.UTF8.GetByteCount(Views.Text(h.Room.Game.View(null)));
        output.WriteLine($"ключовий кадр 4 стіни × 14 рядів + 6 подій: {bytes} Б; вид: {view} Б");
        Assert.True(bytes <= 1500, $"кадр {bytes} Б");
        Assert.True(view <= 4096, $"вид {view} Б");
    }

    [Fact]
    public void View_is_identical_for_every_seat_and_the_watcher()
    {
        var h = Table(4);
        Go(h);
        Later(h, 40);
        Assert.True(J(h, 2, 10, 3, 20, 7).Ok);
        Four(h, 1);
        var watcher = Views.Text(h.Room.Game.View(null));
        for (var s = 0; s < 4; s++) Assert.Equal(watcher, Views.Text(h.Room.Game.View(s)));
        var v = h.View(null);
        foreach (var name in new[] { "turn", "phase", "startIn", "t", "round", "need", "wins", "seed", "speed", "garbage", "stage", "target", "lvl", "sd", "rules", "boards", "result" })
            Assert.True(Views.Has(v, name), name);
        var b = v.GetProperty("boards")[0];
        foreach (var name in new[] { "s", "nk", "a", "rk", "l", "sn", "rc", "k", "q", "g", "fx", "r", "p", "hd", "hu", "pi", "n", "cb", "bb", "ky", "dr", "ds", "gt", "lt", "rs", "ly", "cl", "lr", "cr", "h" })
            Assert.True(Views.Has(b, name), name);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(watcher) < 4096, $"вид {watcher.Length} Б");
    }

    [Fact]
    public void Top_out_ranks_players_in_order_of_falling_and_last_alive_takes_the_round()
    {
        var h = Table(3);
        Go(h);
        Kill(h, 1);
        Assert.Equal(3, Game(h).SeatState(1).Rank);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Kill(h, 0);
        Assert.Equal(2, Game(h).SeatState(0).Rank);
        Assert.Equal(1, Game(h).SeatState(2).Rank);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);         // партія до одного раунду
        Assert.Equal([2], h.Room.Result!.Winners);
        var v = h.View(null);
        Assert.Equal("over", v.GetProperty("phase").GetString());
        Assert.Equal([2, 3, 1, 0], v.GetProperty("result").GetProperty("ranks").EnumerateArray().Select(x => x.GetInt32()));
    }

    [Fact]
    public void Fallen_wall_cannot_journal_and_gets_its_final_state()
    {
        var h = Table(3);
        Go(h);
        Kill(h, 1);
        var r = J(h, 1, Wall(h, 1).Tick, 1);
        Assert.Equal((false, "Ти вже вибув — дивись, як мучаться інші"), (r.Ok, r.Message));
        var was = Frames(h).Count;
        h.Tick();
        var o = Assert.Single(Events(Frames(h).Skip(was), "o"));
        Assert.Equal([1, 3], o.EnumerateArray().Skip(1).Take(2).Select(x => x.GetInt32()));
        var f = Assert.Single(Events(Frames(h).Skip(was), "f"));
        Assert.Equal(0, f[2].GetProperty("a").GetInt32());
    }

    [Fact]
    public void Match_to_two_wins_pauses_five_seconds_between_rounds_with_a_fresh_seed()
    {
        var h = Table(2, new { wins = "2" });
        Go(h);
        var seed = Game(h).Seed;
        Kill(h, 1);
        Assert.Equal(Bricks.PhasePause, Game(h).Phase);
        var v = h.View(null);
        Assert.Equal([1, 0, 0, 0], v.GetProperty("wins").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(2, v.GetProperty("need").GetInt32());
        Assert.Equal("Зачекай, зараз почнемо", J(h, 0, Wall(h, 0).Tick, 0).Message);
        h.Tick(Bricks.PauseTicks - 1);
        Assert.Equal(Bricks.PhasePause, Game(h).Phase);
        h.Tick();
        Assert.Equal(Bricks.PhaseStart, Game(h).Phase);
        Assert.Equal(2, h.View(null).GetProperty("round").GetInt32());
        Assert.NotEqual(seed, Game(h).Seed);
        Assert.True(Wall(h, 1).Alive);
        Assert.Equal(0, Wall(h, 1).Seq);
        Go(h);
        Kill(h, 1);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
    }

    [Fact]
    public void Sudden_death_adds_a_ripe_system_row_to_everyone_every_600_ticks_after_18000()
    {
        var h = Table(3);
        Go(h);
        for (var s = 0; s < 3; s++) { Wall(h, s).Type = -1; Wall(h, s).Clearing = 1_000_000; }   // стіни просто стоять
        Later(h, BricksCore.Sudden - 10);
        h.Tick();
        Assert.Equal(0, Wall(h, 0).CreditCount);
        Assert.Equal(-1, Frames(h).Last().GetProperty("sd").GetInt32());
        Later(h, 20);
        var was = Frames(h).Count;
        h.Tick();
        for (var s = 0; s < 3; s++)
        {
            var c = Wall(h, s);
            Assert.Equal(1, c.CreditCount);
            Assert.Equal((1, -1), (c.Credits[0].Rows, c.Credits[0].From));
            Assert.Equal(c.Tick, c.Credits[0].RipeAt);                   // дозріла одразу
        }
        Assert.Equal(3, Events(Frames(h).Skip(was), "g").Count);
        Assert.InRange(Frames(h).Last().GetProperty("sd").GetInt32(), 1, BricksCore.SuddenEvery);
        Later(h, BricksCore.SuddenEvery);
        h.Tick();
        Assert.Equal(2, Wall(h, 1).PendingRows);
    }

    [Fact]
    public void At_28800_ticks_the_round_goes_to_the_most_lines_and_ties_share_it()
    {
        var h = Table(3);
        Go(h);
        for (var s = 0; s < 3; s++) { Wall(h, s).Type = -1; Wall(h, s).Clearing = 1_000_000; }
        Wall(h, 0).Lines = 12;
        Wall(h, 1).Lines = 30;
        Wall(h, 2).Lines = 30;
        Later(h, BricksCore.Cap + 5);
        h.Tick();
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1, 2], h.Room.Result!.Winners.Order());
        Assert.Equal(3, Game(h).SeatState(0).Rank);
        Assert.Equal(1, Game(h).SeatState(1).Rank);
        Assert.Equal(1, Game(h).SeatState(2).Rank);
    }

    [Fact]
    public void Leaving_mid_round_tops_the_leaver_out_and_the_match_goes_on_with_three()
    {
        var h = Table(4);
        Go(h);
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        var st = Game(h).SeatState(1);
        Assert.False(st.Core.Alive);
        Assert.Equal(4, st.Rank);
        Assert.Equal(BricksCore.OutLeft, st.Core.OutWhy);
        Assert.Equal([2, -1, 3, 0], h.View(null).GetProperty("target").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(4, h.View(null).GetProperty("boards").GetArrayLength());   // стіна втікача лишається сірою до кінця раунду
        Kill(h, 0);
        Kill(h, 2);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([3], h.Room.Result!.Winners);
        // підсумок партії: утікач — зі своїм місцем (упав першим — останній), а не «0-й», і позначений як той, хто встав
        var res = h.View(null).GetProperty("result");
        Assert.Equal([3, 4, 2, 1], res.GetProperty("ranks").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal([false, true, false, false], res.GetProperty("left").EnumerateArray().Select(x => x.GetBoolean()));
        Assert.EndsWith("Петро 0", h.Room.Result.Text);
    }

    [Fact]
    public void Leaving_mid_round_ends_the_match_on_two()
    {
        var h = Table(2);
        Go(h);
        h.Leave("Оля");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Room.Result!.Winners);
        Assert.Contains("Оля встав з-за столу", h.Room.Result.Text);
        // підсумок на картці: той, хто лишився, — перший, а не «Раунд нікому»
        var v = h.View(null);
        Assert.Equal([2, 1, 0, 0], v.GetProperty("result").GetProperty("ranks").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal([true, false, false, false], v.GetProperty("result").GetProperty("left").EnumerateArray().Select(x => x.GetBoolean()));
        Assert.Equal(1, v.GetProperty("boards")[1].GetProperty("rk").GetInt32());
        Assert.Equal(0L, h.Room.Result.Scores![1]);
    }

    [Fact]
    public void Rematch_rotates_seats_resets_wins_and_retargets_the_ring()
    {
        var h = Table(3, new { wins = "1" });
        Go(h);
        Kill(h, 0);
        Kill(h, 1);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var seed = Game(h).Seed;
        Assert.True(h.Rematch().Ok);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(new string?[] { "Петро", "Ганна", "Оля", null }, h.Room.Seats);
        var v = h.View(null);
        Assert.Equal("start", v.GetProperty("phase").GetString());
        Assert.Equal([0, 0, 0, 0], v.GetProperty("wins").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(JsonValueKind.Null, v.GetProperty("result").ValueKind);
        Assert.NotEqual(seed, Game(h).Seed);
        Assert.Equal([1, 2, 0, -1], v.GetProperty("target").EnumerateArray().Select(x => x.GetInt32()));
        Assert.All(Enumerable.Range(0, 3), s => Assert.True(Wall(h, s).Alive));
        Assert.Equal("Петро", v.GetProperty("boards")[0].GetProperty("nk").GetString());
    }

    [Fact]
    public void Speed_option_changes_stage_length_and_garbage_option_reaches_the_core()
    {
        var calm = Table(2, new { speed = "calm", garbage = "hard" });
        Assert.Equal((2700, BricksCore.ModeHard), (Wall(calm, 0).StageTicks, Wall(calm, 0).Garbage));
        var fast = Table(2, new { speed = "fast", garbage = "none" });
        Assert.Equal((1200, BricksCore.ModeNone), (Wall(fast, 1).StageTicks, Wall(fast, 1).Garbage));
        var v = fast.View(null);
        Assert.Equal(("fast", "none", 1200), (v.GetProperty("speed").GetString(), v.GetProperty("garbage").GetString(), v.GetProperty("stage").GetInt32()));
        var odd = Table(2, new { speed = "warp", garbage = "rocks", wins = "9" });
        Assert.Equal((1800, N), (Wall(odd, 0).StageTicks, Wall(odd, 0).Garbage));
        Assert.Equal(1, odd.View(null).GetProperty("need").GetInt32());

        // етап темпу — у кадрі
        Go(fast);
        Later(fast, 1300);
        Assert.True(J(fast, 0, 1290, 0).Ok);
        fast.Tick();
        Assert.Equal(1, Frames(fast).Last().GetProperty("lvl").GetInt32());
        Assert.Equal(36, Wall(fast, 0).Gravity);
    }

    [Fact]
    public void Finish_log_lists_wins_winner_first_and_scores_carry_lines()
    {
        var h = Table(3, new { wins = "2" });
        Go(h);
        Four(h, 2);
        h.Tick(12);
        Kill(h, 0);
        Kill(h, 1);                                     // раунд 1 — Ганна
        h.Tick(Bricks.PauseTicks);
        Go(h);
        Kill(h, 2);
        Kill(h, 1);                                     // раунд 2 — Оля
        h.Tick(Bricks.PauseTicks);
        Go(h);
        Kill(h, 0);
        Kill(h, 1);                                     // раунд 3 — Ганна, 2 : 1
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var fin = Assert.Single(h.Finished);
        Assert.Equal([2], fin.Result.Winners);
        Assert.Equal("Цеглини: Ганна 2 : Оля 1 : Петро 0", fin.Result.Text);
        Assert.Equal(4, fin.Result.Scores![2]);
        Assert.Equal(0, fin.Result.Scores[0]);
        var res = h.View(null).GetProperty("result");
        Assert.Equal([2, 3, 1, 0], res.GetProperty("ranks").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(4, res.GetProperty("lines")[2].GetInt32());
    }

    [Fact]
    public void Four_rows_at_once_asks_for_the_bricks_four_achievement_once_per_seat()
    {
        var h = Table(2, new { wins = "3" });
        Go(h);
        Four(h, 0);
        h.Tick(12);
        Four(h, 0);
        Four(h, 1);
        var asked = h.Awards.Where(a => a.Reason == "ach:bricks-four").ToList();
        Assert.Equal(2, asked.Count);
        Assert.Equal(["Оля", "Петро"], asked.Select(a => a.Nick).Order());
        Assert.All(asked, a => Assert.Equal(0, a.Shards));
    }

    [Fact]
    public void Journal_payload_is_exactly_what_the_module_sends()
    {
        var js = File.ReadAllText(Paths.Resolve("web/games/bricks.js"));
        // модуль шле рівно { q, e, h, g, f } — і ні під якими іншими іменами (send — це ctx.input картки або хаб бота)
        Assert.Matches(new Regex(@"send\('j', \{ q: \w+, e: \w+, h: \w+, g: \w+, f: [\w.]+ \}\)"), js);
        Assert.Contains("send('sync')", js);
        Assert.Contains("input('ready')", js);

        var h = Table(2);
        Go(h);
        var local = new BricksCore(Game(h).Seed, N, 1800);
        local.AdvanceTo(4); local.Apply(3); local.AdvanceTo(6); local.Apply(4);
        // саме так, як його серіалізує браузер: числа, h — беззнакове 32-бітне
        var payload = JsonDocument.Parse($"{{\"q\":1,\"e\":[4,3,6,4],\"h\":{local.Hash()},\"g\":0,\"f\":0}}").RootElement;
        Assert.True(h.Act(0, "j", payload).Ok);
        Assert.Equal(local.Hash(), Wall(h, 0).Hash());
        Assert.False(Game(h).SeatState(0).NeedFix);
        Assert.Equal("Журнал не читається", h.Act(0, "j", new { seq = 2, events = new[] { 7, 7 } }).Message);
    }

    [Fact]
    public void Determinism_same_seed_same_journals_same_hashes()
    {
        string Play()
        {
            var h = Table(3, seed: 77);
            Go(h);
            var ev = BricksFixtures.Gen(5, 100);
            var views = new List<string>();
            for (var i = 0; i < 90; i += 3)
            {
                Later(h, 20);
                for (var s = 0; s < 3; s++)
                {
                    var c = Wall(h, s);
                    if (!c.Alive || h.Room.Status != RoomStatus.Playing) continue;
                    var t = Math.Max(c.Tick, Game(h).WallTick - 5);
                    J(h, s, t, ev[(i + s) * 2 + 1], t + 1, ev[(i + s + 1) * 2 + 1]);
                }
                h.Tick();
                views.Add(Views.Text(h.Room.Game.View(null)));
            }
            return string.Join("\n", views) + string.Join(",", Frames(h).Select(f => f.GetRawText()));
        }
        Assert.Equal(Play(), Play());
    }

    [Fact]
    public void Reconnecting_client_finds_its_whole_wall_in_the_view()
    {
        var h = Table(2);
        Go(h);
        Later(h, 30);
        Assert.True(J(h, 0, 10, BricksCore.KLeft, 20, BricksCore.KSoft).Ok);
        Four(h, 1);
        var b = h.View(0).GetProperty("boards").EnumerateArray().Single(x => x.GetProperty("s").GetInt32() == 0);
        var back = new BricksCore();
        back.LoadWire(b, h.View(0).GetProperty("seed").GetUInt32(), N, 1800);
        Assert.Equal(Wall(h, 0).Hash(), back.Hash());
        Assert.Equal(5, b.GetProperty("ky").GetInt32());         // ← і ↓ досі затиснуті — клієнт відпустить сам
        Assert.Equal(1, b.GetProperty("cr").GetArrayLength());
    }
}

/// <summary>Числа, спільні з JS-стендом (docs/games/dev/bricks-selftest.html).</summary>
public static class BricksPins
{
    public static readonly uint[] Xorshift = [270369, 67634689, 2647435461, 307599695, 2398689233, 745495504, 632435482, 435756210];
    public const uint SpawnT = 3627329965;
}

/// <summary>Швидкодія кімнати — окремою колекцією, бо стінний годинник у паралельному прогоні бреше.</summary>
[Collection(SerialPerf.Name)]
public class BricksPerfTests(ITestOutputHelper output)
{
    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait("Category", "Perf")]
    public void Perf_3000_ticks_with_four_journaling_bots_under_one_second()
    {
        var h = new RoomHarness("bricks", new { wins = "3" }, seed: 5);
        foreach (var nick in new[] { "Оля", "Петро", "Ганна", "Іван" }) h.Join(nick);
        h.Start();
        var game = (Bricks)h.Room.Game;
        int[] keys = [1, 2, 3, 4, 7, 0, 8, 5, 6, 3, 4, 11, 0, 10];
        var step = 0;
        long bytes = 0, frames = 0, maxFrame = 0;
        var seen = 0;
        var tickTime = TimeSpan.Zero;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 3000; i++)
        {
            if (h.Room.Status == RoomStatus.Finished) h.Rematch();
            if (game.Phase == Bricks.PhaseGo && i % 3 == 0)
            {
                for (var s = 0; s < 4; s++)
                {
                    var c = game.SeatState(s).Core;
                    if (!c.Alive) continue;
                    var t = Math.Max(c.Tick, game.WallTick - 4);
                    var k1 = keys[step++ % keys.Length];
                    var k2 = keys[step++ % keys.Length];
                    h.Input(s, "j", new { q = c.Seq + 1, e = new[] { t, k1, t + 1, k2, t + 2, 0 }, g = c.Gseq });
                }
            }
            // тик кімнати разом із серіалізацією кадра — так, як це платить сервер
            var t0 = Stopwatch.GetTimestamp();
            h.Tick();
            var all = h.Outbox;
            for (; seen < all.Count; seen++)
            {
                if (all[seen] is not RoomFrame f) continue;
                var n = JsonSerializer.SerializeToUtf8Bytes(f.Frame, Wire).Length;
                bytes += n;
                frames++;
                maxFrame = Math.Max(maxFrame, n);
            }
            tickTime += Stopwatch.GetElapsedTime(t0);
        }
        sw.Stop();
        output.WriteLine($"3000 тиків із ботами: {sw.Elapsed.TotalMilliseconds:F0} мс; чистий Tick() у середньому {tickTime.TotalMilliseconds / 3000:F4} мс; " +
                         $"кадрів {frames}, середній {(frames == 0 ? 0 : bytes / frames)} Б, найбільший {maxFrame} Б");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"3000 тиків зайняли {sw.Elapsed}");
        Assert.True(tickTime.TotalMilliseconds / 3000 <= 0.25, $"середній тик {tickTime.TotalMilliseconds / 3000:F4} мс");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Journal_batch_costs_microseconds()
    {
        var h = new RoomHarness("bricks", null, seed: 9);
        h.Join("Оля");
        h.Join("Петро");
        h.Start();
        var game = (Bricks)h.Room.Game;
        for (var i = 0; i < 100 && game.Phase != Bricks.PhaseGo; i++) h.Tick();
        var c = game.SeatState(0).Core;
        var payloads = new List<JsonElement>();
        var t = 0;
        for (var i = 0; i < 2000; i++)
        {
            payloads.Add(Views.Payload(new { q = i + 1, e = new[] { t, 1 + i % 4 }, h = 0 }));
            t += 1;
        }
        h.Clock.AdvanceMs(40_000);
        var sw = Stopwatch.StartNew();
        foreach (var p in payloads) h.Rooms.Input(h.RoomId, "Оля", "j", p);
        sw.Stop();
        var us = sw.Elapsed.TotalMilliseconds * 1000 / payloads.Count;
        output.WriteLine($"пачка журналу: {us:F2} мкс разом із замком кімнати й Input каркаса");
        Assert.True(us < 50, $"{us:F2} мкс на пачку");
        Assert.True(c.Seq > 1000);
    }
}
