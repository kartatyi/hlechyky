using System.Text.Json;
using Hlechyky.Games.Impl;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Друга печера «Глибше» (прохід №3): тонкі платформи, дзеркала, промені й кришталі, портали — на синтетичних
/// майданчиках; привид найкращого проходження пари — у сховищі, виді й ендпоінті.
/// </summary>
public sealed partial class VohnykTests
{
    static void Steps(VohnykWorld w, int n, int kF = 0, int kW = 0)
    {
        for (var i = 0; i < n; i++) w.Step(kF, kW);
    }

    static int FeetRow(VohnykWorld w, int h) => (w.Y[h] + VohnykWorld.HeroH) / T;

    [Fact]
    public void A_thin_platform_is_passed_from_below_and_stood_on_from_above_and_holds_a_box()
    {
        // дошка на 2 плитки над підлогою (ряд 7), герой під нею: стрибок крізь неї — і стоїть зверху
        var w = W(Map(24, 12, (1, 10, 8, 8, '_')), fire: [3, 9], water: [15, 9], tweak: f => f.Boxes = [new VohnykAtFile { At = [6, 2] }]);
        Steps(w, 20, J);
        Steps(w, 60);
        Assert.Equal(8, FeetRow(w, 0));
        Assert.Equal(1, w.Grounded[0]);
        // скриня падає згори й лягає на дошку, а не крізь неї
        Assert.Equal(8 * T - VohnykWorld.BoxSize, w.BoxY[0]);
        // збоку дошка не заважає: Крапля на підлозі біжить під нею ліворуч без упину
        var x0 = w.X[1];
        Steps(w, 40, 0, L);
        Assert.True(w.X[1] < x0 - 100 * VohnykWorld.Px);
        Assert.Equal(10, FeetRow(w, 1));
    }

    [Fact]
    public void A_mirror_turns_where_the_hero_walks_out_and_turns_the_light_into_a_sensor_that_opens_a_door()
    {
        // світло з [20,3] ліворуч; дзеркало [10,9] на підлозі; кришталь [10,3] над ним
        var w = W(Map(24, 12), fire: [4, 9], water: [2, 9], tweak: f =>
        {
            f.Beams = [new VohnykBeamFile { Id = "e", At = [20, 9], Dir = "l", Who = "light" }];
            f.Mirrors = [new VohnykMirrorFile { Id = "m", At = [10, 9], Init = 1 }];
            f.Sensors = [new VohnykIdAtFile { Id = "s", At = [10, 3] }];
            f.Doors = [Door("d", 16, 1, 2, ["s"])];
        });
        Steps(w, 1);
        Assert.Equal(0, w.Sensor[0]);                          // «/»: промінь ліворуч → униз, у підлогу
        // Вогник проходить крізь дзеркало праворуч — лишається «/»; назад ліворуч — «\», промінь угору в кришталь
        Steps(w, 60, R);
        Steps(w, 80, L);
        Steps(w, 3);
        Assert.Equal(0, w.Lever[0]);
        Assert.Equal(1, w.Sensor[0]);
        Steps(w, 20);
        Assert.Equal(2 * T, w.DoorO[0]);
        // стали в світло між ліхтарем і дзеркалом — кришталь гасне
        Steps(w, 90, R);
        Assert.True(w.X[0] > 11 * T && w.X[0] < 19 * T);
        Assert.Equal(0, w.Sensor[0]);
    }

    [Fact]
    public void A_fixed_mirror_never_turns()
    {
        var w = W(Map(24, 12), fire: [4, 9], water: [2, 9], tweak: f => f.Mirrors = [new VohnykMirrorFile { Id = "m", At = [10, 9], Init = 1, Fixed = true }]);
        Steps(w, 60, R);
        Steps(w, 80, L);
        Assert.Equal(1, w.Lever[0]);
    }

    [Fact]
    public void A_fire_beam_evaporates_the_drop_but_the_flame_standing_in_it_shields_her()
    {
        // вогняний промінь зі стелі в колонку 10
        static VohnykWorld Make(int fireCol) => W(Map(24, 12), fire: [fireCol, 9], water: [7, 9], tweak: f =>
            f.Beams = [new VohnykBeamFile { Id = "e", At = [10, 1], Dir = "d", Who = "fire" }]);
        var bare = Make(2);
        for (var i = 0; i < 80 && bare.Died[1] == 0; i++) bare.Step(0, R);
        Assert.Equal(1, bare.Died[1]);
        Assert.True(bare.RayKills(1));
        Assert.Equal(0, bare.Died[0]);

        // Вогник стоїть у промені (нічия по висоті — прикриває той, кого промінь не чіпає) — Крапля проходить
        var shield = Make(10);
        Steps(shield, 1);
        Assert.Equal(1, shield.RayHit[0]);                     // у Вогника влучило, але йому байдуже
        for (var i = 0; i < 80; i++) shield.Step(0, R);
        Assert.Equal(0, shield.Died[1]);
        Assert.True(shield.X[1] > 11 * T);
    }

    [Fact]
    public void A_water_beam_puts_out_the_flame_and_a_box_blocks_any_beam()
    {
        var w = W(Map(24, 12), fire: [4, 9], water: [2, 9], tweak: f =>
            f.Beams = [new VohnykBeamFile { Id = "e", At = [10, 1], Dir = "d", Who = "water" }]);
        for (var i = 0; i < 80 && w.Died[0] == 0; i++) w.Step(R, 0);
        Assert.Equal(1, w.Died[0]);
        Assert.Equal(0, w.Died[1]);

        var boxed = W(Map(24, 12), fire: [4, 9], water: [2, 9], tweak: f =>
        {
            f.Beams = [new VohnykBeamFile { Id = "e", At = [10, 1], Dir = "d", Who = "water" }];
            f.Boxes = [new VohnykAtFile { At = [10, 5] }];
        });
        Steps(boxed, 30);
        // скриня впала на підлогу під промінь — промінь упирається в її верх
        Assert.Equal(1, boxed.RayCount);
        Assert.Equal(boxed.BoxY[0], boxed.Ray[3]);
    }

    [Fact]
    public void A_beam_switched_by_a_button_shines_only_while_the_button_is_held()
    {
        var w = W(Map(24, 12), fire: [4, 9], water: [2, 9], tweak: f =>
        {
            f.Buttons = [Btn("b", 6, 9)];
            f.Beams = [new VohnykBeamFile { Id = "e", At = [15, 1], Dir = "d", Who = "light", By = ["b"] }];
        });
        Steps(w, 1);
        Assert.Equal(0, w.RayCount);
        Steps(w, 15, R);
        Assert.Equal(1, w.Button[0]);
        Assert.Equal(1, w.RayCount);
    }

    [Fact]
    public void A_sensor_cannot_switch_a_beam()
    {
        var f = new VohnykLevelFile
        {
            N = 99, Name = "х", Par = 60000, W = 24, H = 12, Rows = Map(24, 12),
            Spawn = new VohnykSpawnFile { Fire = [2, 9], Water = [4, 9] }, Exits = new VohnykSpawnFile { Fire = [20, 1], Water = [21, 1] },
            Sensors = [new VohnykIdAtFile { Id = "s", At = [10, 3] }],
            Beams = [new VohnykBeamFile { Id = "e", At = [15, 1], Dir = "d", By = ["s"] }],
        };
        Assert.Throws<InvalidDataException>(() => VohnykLevels.Build(f));
    }

    [Fact]
    public void A_portal_moves_the_hero_to_its_other_end_once_and_only_while_lit()
    {
        // портал a [8,8] → b [18,8] на тій самій підлозі; горить, поки натиснута кнопка [3,9]
        static VohnykWorld Make() => W(Map(24, 12), fire: [5, 9], water: [3, 9], tweak: f =>
        {
            f.Buttons = [Btn("b", 3, 9)];
            f.Portals = [new VohnykPortalFile { Id = "p", A = [8, 8], B = [18, 8], By = ["b"] }];
        });
        var w = Make();
        Steps(w, 30, R);
        Assert.InRange(w.X[0], 18 * T, 20 * T);                // вийшов із другого кінця й біжить далі
        var x = w.X[0];
        Steps(w, 10, R);
        Assert.True(w.X[0] > x);                               // назад не кинуло
        Assert.NotEqual(0, w.PortalIn[0] | (w.X[0] >= 19 * T ? 1 : 0));

        // Крапля зійшла з кнопки — портал погас, Вогник пробігає кінець наскрізь
        var off = Make();
        Steps(off, 20, R, R);
        Assert.Equal(0, off.Button[0]);
        Steps(off, 30, R, 0);
        Assert.True(off.X[0] > 8 * T && off.X[0] < 14 * T);
    }

    [Fact]
    public void First_cave_levels_keep_their_snapshot_length_so_old_records_and_hashes_stay_valid()
    {
        foreach (var l in VohnykLevels.All.Where(l => l.N <= VohnykLevels.Cave1))
        {
            var w = new VohnykWorld(l);
            Assert.Equal(2 * VohnykWorld.HeroInts + 3 * l.Boxes.Length + l.Doors.Length + 2 * l.Lifts.Length + l.Levers.Length + l.Buttons.Length + 3, w.StateLength);
        }
        Assert.True(VohnykLevels.Count > VohnykLevels.Cave1, "друга печера має бути");
    }

    [Fact]
    public void Deep_levels_unlock_after_the_fifteenth_and_do_not_count_for_the_crystal_cave_achievement()
    {
        var store = Unlocked(VohnykLevels.Cave1 + 1, "Оля");
        Assert.True(store.Unlocked(["оля"], 16));
        Assert.False(store.Unlocked(["петро"], 16));
        for (var n = 1; n <= VohnykLevels.Cave1; n++) store.Record(["Оля"], n, 1000, 0, 3, DateTimeOffset.UnixEpoch);
        Assert.True(store.AllThreeStars("оля"));               // шістнадцятий ще не пройдено — а ачівка першої печери є
    }

    [Fact]
    public void The_ghost_is_kept_for_the_pair_only_when_faster_and_built_lazily()
    {
        var store = new VohnykStore(null);
        var calls = 0;
        Func<string> G(string s) => () => { calls++; return s; };
        store.Record(["Оля", "Петро"], 16, 50000, 0, 2, DateTimeOffset.UnixEpoch, ghost: G("A"));
        store.Record(["Петро", "Оля"], 16, 60000, 0, 2, DateTimeOffset.UnixEpoch, ghost: G("B"));
        Assert.Equal("A", store.Ghost("оля+петро", 16)!.Data);
        Assert.Equal(1, calls);                                // повільніший привид навіть не будувався
        store.Record(["Оля", "Петро"], 16, 40000, 0, 2, DateTimeOffset.UnixEpoch, ghost: G("C"));
        Assert.Equal("C", store.Ghost("оля+петро", 16)!.Data);
        Assert.Equal(40000, store.Ghost("оля+петро", 16)!.Ms);
    }

    [Fact]
    public void A_cleared_deep_level_leaves_a_ghost_of_both_heroes_and_the_view_points_to_it()
    {
        var store = Unlocked(16, "Оля", "Петро");
        var h = Table(store);
        Assert.True(h.Act(0, "pick", new { level = 16 }).Ok);
        h.Start();
        var level = VohnykLevels.Get(16);
        Play(h, level.Solution);
        var v = h.View(0);
        Assert.True(v.GetProperty("result").GetProperty("cleared").GetBoolean());
        var ghost = store.Ghost("оля+петро", 16);
        Assert.NotNull(ghost);
        var shorts = Convert.FromBase64String(ghost!.Data).Length / 2;
        // знімок кожні 4 кроки годинника, по 4 числа (x·2+погляд, y — для обох)
        Assert.Equal(level.Check!.Steps / Vohnyk.GhostEvery * 4, shorts);
        Assert.Equal(ghost.Ms, v.GetProperty("ghost").GetProperty("ms").GetInt32());
        Assert.Equal("оля+петро", v.GetProperty("ghost").GetProperty("pair").GetString());
        // перший знімок — Вогник біля старту на підлозі (y у px — верх героя)
        var bytes = Convert.FromBase64String(ghost.Data);
        var y0 = BitConverter.ToInt16(bytes, 2);
        Assert.Equal((level.Spawn[0].Row + 1) * 40 - 36, y0);
    }

    [Fact]
    public void The_level_view_carries_the_new_things_of_the_deep_cave()
    {
        var h = Table(Unlocked(17, "Оля", "Петро"));
        Assert.True(h.Act(0, "pick", new { level = 17 }).Ok);
        var lv = h.View(0).GetProperty("level");
        Assert.Equal(1, lv.GetProperty("mirrors").GetArrayLength());
        Assert.Equal(3, lv.GetProperty("beams").GetArrayLength());
        Assert.Equal("d", lv.GetProperty("beams")[0].GetProperty("dir").GetString());
        Assert.Equal("fire", lv.GetProperty("beams")[0].GetProperty("who").GetString());
        Assert.Equal(1, lv.GetProperty("sensors").GetArrayLength());
        Assert.Equal(0, lv.GetProperty("levers").GetArrayLength());
        Assert.False(lv.TryGetProperty("solution", out _));
    }
}
