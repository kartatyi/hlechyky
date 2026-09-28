using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Танчики, прохід №3: автовогонь і буфер пострілу, серії й помста, підсумок, 🪃 рикошет, кущі й лід,
/// команди з глеками, хвилі 🤖.
/// </summary>
[Collection(Hlechyky.Tests.Support.SerialPerf.Name)]
public class TanksSweep3Tests
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марко", "Зоя"];

    static RoomHarness Table(int players = 2, int seed = 42, object? options = null)
    {
        var h = new RoomHarness("tanks", options, seed: seed);
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

    static TanksCore Empty(int seed = 1)
    {
        var core = new TanksCore(new Random(seed));
        core.Layout();
        return core;
    }

    static Tank Put(TanksCore core, int seat, int x, int y, int dir = 0)
    {
        var t = core.Tanks[seat];
        t.Cell = core.Cell(x, y);
        t.Move = -1;
        t.Step = 0;
        t.Want = -1;
        t.Dir = dir;
        t.Plays = true;
        t.Alive = true;
        return t;
    }

    static void Steps(TanksCore core, int n)
    {
        for (var i = 0; i < n; i++) core.Step();
    }

    /// <summary>Розчистити смугу мапи (у кімнаті мапа випадкова, а тест стріляє по прямій).</summary>
    static void Lane(TanksCore core, int y, int x0, int x1)
    {
        for (var x = x0; x <= x1; x++) core.Tiles[core.Cell(x, y)] = TankTile.Free;
    }

    static TanksCore Core(RoomHarness h)
    {
        var field = typeof(Tanks).GetField("_core", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (TanksCore)field.GetValue((Tanks)h.Room.Game)!;
    }

    // ---------- 79. автовогонь і буфер ----------

    [Fact]
    public void A_press_just_before_the_reload_ends_is_remembered_and_fires_by_itself()
    {
        var core = Empty();
        Put(core, 0, 3, 5, dir: 2);                             // дулом у рамку: снаряд згасне швидко
        Assert.True(core.Press(0));
        Steps(core, TanksCore.ReloadTicks - 3);                 // до кінця перезарядки — 3 тики
        Assert.Empty(core.Shells);
        var before = core.Tanks[0].Shots;
        Assert.False(core.Press(0));                            // ще не можна — але натиск не губиться
        Steps(core, TanksCore.BufferTicks);
        Assert.Equal(before + 1, core.Tanks[0].Shots);
    }

    [Fact]
    public void A_press_too_early_is_forgotten_like_before()
    {
        var core = Empty();
        Put(core, 0, 3, 5, dir: 0);
        Assert.True(core.Press(0));
        Steps(core, 1);
        Assert.False(core.Press(0));                            // до кінця перезарядки — 9 тиків
        Steps(core, 30);
        Assert.Equal(1, core.Tanks[0].Shots);
    }

    [Fact]
    public void Holding_fire_shoots_as_soon_as_it_can_and_releasing_stops()
    {
        var core = Empty();
        Put(core, 0, 2, 5, dir: 2);
        core.Hold(0, true);
        Assert.Equal(1, core.Tanks[0].Shots);
        Steps(core, 60);
        Assert.True(core.Tanks[0].Shots >= 4, $"пострілів {core.Tanks[0].Shots}");
        core.Hold(0, false);
        var n = core.Tanks[0].Shots;
        Steps(core, 60);
        Assert.Equal(n, core.Tanks[0].Shots);
    }

    [Fact]
    public void Fire_with_on_true_holds_through_the_room_and_on_false_lets_go()
    {
        var h = Table();
        Ready(h);
        var core = Core(h);
        Assert.True(h.Act(0, "fire", new { on = true }).Ok);
        h.Tick(80);
        var n = core.Tanks[0].Shots;
        Assert.True(n >= 3, $"пострілів {n}");
        Assert.True(h.Act(0, "fire", new { on = false }).Ok);
        h.Tick(40);
        Assert.Equal(n, core.Tanks[0].Shots);
        Assert.True(h.Act(0, "fire").Ok);                       // натиск на перезарядці — не «Перезарядка», а буфер
    }

    [Fact]
    public void Holding_fire_during_the_countdown_starts_shooting_on_go()
    {
        var h = Table();
        h.Act(0, "fire", new { on = 1 });
        Ready(h);
        h.Tick(2);
        Assert.True(Core(h).Tanks[0].Shots >= 1);
    }

    // ---------- 83. серії й помста ----------

    [Fact]
    public void Revenge_is_announced_and_with_the_option_pays_an_extra_frag()
    {
        var core = Empty();
        core.RevengeFrag = true;
        var a = Put(core, 0, 3, 5, dir: 0);
        var b = Put(core, 1, 6, 5, dir: 2);
        core.Fire(1);                                           // зелений підбиває жовтого
        Steps(core, 3);
        Assert.False(a.Alive);
        Assert.Equal(TankHow.Kill, core.Events[^1].How);
        Assert.Equal(0, a.Nemesis == 1 ? 0 : 1);
        core.Events.Clear();
        Steps(core, TanksCore.RespawnTicks + TanksCore.ShieldTicks);
        Put(core, 0, 3, 5, dir: 0);
        core.Tanks[0].Reload = 0;
        core.Fire(0);                                           // жовтий відплачує
        Steps(core, 3);
        Assert.False(b.Alive);
        var ev = Assert.Single(core.Events);
        Assert.Equal(TankHow.Revenge, ev.How);
        Assert.Equal(2, a.Frags);                               // фраг і ще один за помсту
        Assert.Equal(1, a.Revenges);
        Assert.Equal(-1, a.Nemesis);
    }

    [Fact]
    public void Without_the_option_revenge_is_only_glory()
    {
        var core = Empty();
        var a = Put(core, 0, 3, 5, dir: 0);
        Put(core, 1, 6, 5, dir: 2);
        a.Nemesis = 1;
        core.Fire(0);
        Steps(core, 3);
        Assert.Equal(TankHow.Revenge, core.Events[^1].How);
        Assert.Equal(1, a.Frags);
    }

    [Fact]
    public void A_streak_counts_kills_without_dying_and_death_resets_it()
    {
        var core = Empty();
        var a = Put(core, 0, 3, 5, dir: 0);
        for (var k = 1; k <= 3; k++)
        {
            Put(core, 1, 6, 5, dir: 2);
            core.Tanks[1].Shield = 0;
            a.Reload = 0;
            core.Fire(0);
            Steps(core, 3);
            Assert.Equal(k, core.Events[^1].N);
        }
        Assert.Equal(3, a.BestStreak);
        Put(core, 1, 6, 5, dir: 2);
        core.Tanks[1].Reload = 0;
        core.Fire(1);
        Steps(core, 3);
        Assert.Equal(0, a.Streak);
        Assert.Equal(3, a.BestStreak);
    }

    [Fact]
    public void A_bot_is_nobodys_nemesis()
    {
        var core = Empty();
        var man = Put(core, 0, 3, 5, dir: 0);
        var bot = Put(core, TanksCore.Seats, 6, 5, dir: 2);
        bot.Bot = true;
        core.Fire(TanksCore.Seats);
        Steps(core, 3);
        Assert.False(man.Alive);
        Assert.Equal(-1, man.Nemesis);
    }

    // ---------- 84. підсумок ----------

    [Fact]
    public void The_final_view_carries_a_summary_row_for_everyone_with_accuracy_and_nemesis()
    {
        var h = Table(options: new { frags = "5" });
        Ready(h);
        var core = Core(h);
        Lane(core, 5, 3, 6);
        for (var i = 0; i < 5; i++)
        {
            Put(core, 0, 3, 5, dir: 0);
            Put(core, 1, 6, 5, dir: 2);
            core.Tanks[1].Shield = 0;
            core.Tanks[0].Reload = 0;
            h.Act(0, "fire");
            h.Tick(3);
        }
        Assert.Equal("over", Phase(h));
        var sum = h.View(null).GetProperty("sum");
        Assert.Equal(2, sum.GetArrayLength());
        var olya = sum[0];
        Assert.Equal(0, olya[0].GetInt32());
        Assert.Equal(5, olya[1].GetInt32());                   // фраги
        Assert.Equal(5, olya[3].GetInt32());                   // пострілів
        Assert.Equal(5, olya[4].GetInt32());                   // влучань
        var petro = sum[1];
        Assert.Equal(5, petro[2].GetInt32());                  // смертей
        Assert.Equal(0, petro[6].GetInt32());                  // найчастіше підбивала Оля
        Assert.Equal(5, petro[7].GetInt32());
    }

    [Fact]
    public void Events_ride_in_frames_for_three_seconds()
    {
        var h = Table();
        Ready(h);
        var core = Core(h);
        Lane(core, 5, 3, 6);
        Put(core, 0, 3, 5, dir: 0);
        Put(core, 1, 6, 5, dir: 2);
        core.Tanks[1].Shield = 0;
        h.Act(0, "fire");
        h.Tick(3);
        var ev = h.View(null).GetProperty("ev");
        var e = ev[ev.GetArrayLength() - 1];
        Assert.Equal((int)TankHow.Kill, e[1].GetInt32());
        Assert.Equal(0, e[2].GetInt32());
        Assert.Equal(1, e[3].GetInt32());
        h.Tick(Tanks.EventTicks + 2);
        Assert.False(h.View(null).TryGetProperty("ev", out _));
    }

    // ---------- 82. рикошет ----------

    [Fact]
    public void A_bouncing_shell_turns_right_off_steel_once()
    {
        var core = Empty();
        var gun = Put(core, 0, 3, 5, dir: 0);
        TanksCore.Apply(gun, TankBonus.Bounce);
        core.Tiles[core.Cell(8, 5)] = TankTile.Steel;
        var target = Put(core, 1, 7, 9, dir: 0);              // під кутом: праворуч за ходом — це вниз
        core.Fire(0);
        Steps(core, 20);
        Assert.False(target.Alive);
        Assert.Equal(1, gun.Frags);
    }

    [Fact]
    public void A_bouncing_shell_turns_left_when_right_is_steel_and_bounces_only_once()
    {
        var core = Empty();
        var gun = Put(core, 0, 3, 5, dir: 0);
        TanksCore.Apply(gun, TankBonus.Bounce);
        core.Tiles[core.Cell(8, 5)] = TankTile.Steel;
        core.Tiles[core.Cell(7, 6)] = TankTile.Steel;          // праворуч за ходом (униз) — сталь
        core.Fire(0);
        Steps(core, 7);
        var s = Assert.Single(core.Shells);
        Assert.Equal(3, s.Dir);                                 // пішов угору
        Assert.False(s.Bounce);
        Steps(core, 30);                                        // об рамку вгорі — вже без відскоку
        Assert.Empty(core.Shells);
    }

    [Fact]
    public void Bounce_burns_with_the_tank_and_shows_in_perks()
    {
        var h = Table();
        Ready(h);
        var core = Core(h);
        TanksCore.Apply(core.Tanks[0], TankBonus.Bounce);
        h.Tick(1);
        Assert.Contains("b", h.View(null).GetProperty("p")[0].GetProperty("perks").GetString());
        Lane(core, 5, 3, 6);
        var t = Put(core, 0, 3, 5, dir: 0);
        Put(core, 1, 6, 5, dir: 2);
        core.Tanks[0].Shield = 0;
        core.Tanks[1].Reload = 0;
        core.Fire(1);
        Steps(core, 3);
        Assert.False(t.Bounce);
    }

    // ---------- 81. кущі й лід ----------

    [Fact]
    public void The_wild_map_has_mirrored_bushes_and_ice_and_the_classic_one_has_none()
    {
        var wild = Table(options: new { map = "wild" });
        var core = Core(wild);
        var bushes = core.BushCells();
        var ice = core.IceCells();
        Assert.True(bushes.Length > 0, $"wild={core.Wild} tiles={string.Join(",", core.Tiles.GroupBy(t => t).Select(g => g.Key + ":" + g.Count()))} W={core.W}");
        Assert.NotEmpty(ice);
        foreach (var c in bushes.Concat(ice))
            Assert.Equal(core.Tiles[c], core.Tiles[core.Cell(core.W - 1 - core.X(c), core.H - 1 - core.Y(c))]);
        var v = wild.View(null);
        Assert.Equal(bushes.Length, v.GetProperty("bush").GetArrayLength());
        foreach (var s in core.Starts) Assert.Equal(TankTile.Free, core.Tiles[s]);

        var classic = Table();
        Assert.Empty(Core(classic).BushCells());
        Assert.False(classic.View(null).TryGetProperty("bush", out _));
    }

    [Fact]
    public void Shells_fly_through_bushes_and_tanks_drive_into_them()
    {
        var core = Empty();
        Put(core, 0, 3, 5, dir: 0);
        core.Tiles[core.Cell(5, 5)] = TankTile.Bush;
        var hidden = Put(core, 1, 7, 5, dir: 2);
        core.Tiles[core.Cell(7, 5)] = TankTile.Bush;
        core.Fire(0);
        Steps(core, 6);
        Assert.False(hidden.Alive);
        var t = Put(core, 2, 3, 8, dir: 0);
        core.Tiles[core.Cell(4, 8)] = TankTile.Bush;
        core.Turn(2, 0);
        Steps(core, 4);
        Assert.Equal(core.Cell(4, 8), t.Cell);
    }

    [Fact]
    public void Ice_slides_a_released_tank_one_cell_further_but_not_a_held_one_more()
    {
        var core = Empty();
        var t = Put(core, 0, 3, 5, dir: 0);
        core.Tiles[core.Cell(4, 5)] = TankTile.Ice;
        core.Tiles[core.Cell(5, 5)] = TankTile.Ice;
        core.Turn(0, 0);
        Steps(core, 1);
        core.Turn(0, -1);                                       // відпустив одразу — на звичній підлозі став би в (4,5)
        Steps(core, 20);
        Assert.Equal(core.Cell(5, 5), t.Cell);                  // ковзнув ще на одну
        Assert.Equal(-1, t.Move);

        var u = Put(core, 1, 3, 8, dir: 0);
        core.Tiles[core.Cell(4, 8)] = TankTile.Ice;
        core.Turn(1, 0);
        Steps(core, 1);
        core.Turn(1, 1);                                        // хоче вниз — але лід несе ще клітинку праворуч
        Steps(core, 7);
        Assert.Equal(core.Cell(5, 8), u.Cell);
    }

    // ---------- 78. команди й глек ----------

    [Fact]
    public void Teams_of_four_split_left_and_right_with_a_jug_each()
    {
        var h = Table(4, options: new { mode = "teams" });
        var v = h.View(null);
        var teams = v.GetProperty("teams").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal([0, 1, 0, 1, -1, -1], teams);
        Assert.Equal(2, v.GetProperty("bases").GetArrayLength());
        var core = Core(h);
        for (var s = 0; s < 4; s++)
            Assert.Equal(teams[s] == 0 ? 1 : core.W - 2, core.X(core.Tanks[s].Home));
        Assert.Equal(TankTile.Base, core.Tiles[core.BaseCell[0]]);
        Assert.Equal(TankTile.Brick, core.Tiles[core.BaseCell[0] + 1]);
    }

    [Fact]
    public void Teammates_do_not_hurt_each_other()
    {
        var h = Table(4, options: new { mode = "teams" });
        Ready(h);
        var core = Core(h);
        Lane(core, 3, 5, 8);
        Put(core, 0, 5, 3, dir: 0);
        var mate = Put(core, 2, 8, 3, dir: 2);
        mate.Shield = 0;
        h.Act(0, "fire");
        h.Tick(4);
        Assert.True(mate.Alive);
        Assert.Equal(0, core.Tanks[0].Frags);
    }

    [Fact]
    public void Breaking_the_enemy_jug_wins_for_the_whole_team()
    {
        var h = Table(4, options: new { mode = "teams" });
        Ready(h);
        var core = Core(h);
        var jug = core.BaseCell[1];
        Lane(core, core.Y(jug), core.X(jug) - 3, core.X(jug) - 1);
        Put(core, 0, core.X(jug) - 3, core.Y(jug), dir: 0);
        h.Act(0, "fire");
        h.Tick(6);
        Assert.Equal("over", Phase(h));
        var done = Assert.Single(h.Finished);
        Assert.Equal([0, 2], done.Result.Winners.Order().ToArray());
        Assert.Contains("глек розбито", done.Result.Text);
        Assert.Equal("base", h.View(null).GetProperty("end").GetString());
    }

    [Fact]
    public void A_shell_of_my_own_team_does_not_break_my_jug()
    {
        var h = Table(2, options: new { mode = "teams" });
        Ready(h);
        var core = Core(h);
        var jug = core.BaseCell[0];
        Lane(core, core.Y(jug), core.X(jug) + 1, core.X(jug) + 3);
        Put(core, 0, core.X(jug) + 3, core.Y(jug), dir: 2);
        h.Act(0, "fire");
        h.Tick(6);
        Assert.True(core.BaseUp[0]);
        Assert.Equal("go", Phase(h));
    }

    [Fact]
    public void Teams_need_an_even_table_otherwise_everyone_plays_alone()
    {
        var h = Table(3, options: new { mode = "teams" });
        var v = h.View(null);
        Assert.False(v.TryGetProperty("teams", out _));
        Assert.Contains("Команд не буде", v.GetProperty("note").GetString());
    }

    // ---------- 80. хвилі 🤖 ----------

    [Fact]
    public void Waves_send_bots_from_the_right_and_they_ride_in_the_frame()
    {
        var h = Table(2, options: new { mode = "waves" });
        Ready(h);
        h.Tick(TanksCore.FirstWaveTicks - 2);
        Assert.Equal(0, h.View(null).GetProperty("wv")[0].GetInt32());     // три секунди роз'їхатись
        h.Tick(60);
        var v = h.View(null);
        Assert.Equal(1, v.GetProperty("wv")[0].GetInt32());
        Assert.True(v.GetProperty("e").GetArrayLength() >= 1);
        Assert.Single(v.GetProperty("bases").EnumerateArray());
        Assert.Equal(0, v.GetProperty("need").GetInt32());
        var core = Core(h);
        Assert.All(core.Tanks.Skip(TanksCore.Seats).Where(t => t.Alive), t => Assert.True(core.X(t.Home) == core.W - 2));
    }

    [Fact]
    public void Bots_break_the_jug_and_the_table_loses_without_rewards()
    {
        var h = Table(2, options: new { mode = "waves" });
        Ready(h);
        h.Tick(TanksCore.FirstWaveTicks + 3);
        var core = Core(h);
        var jug = core.BaseCell[0];
        Lane(core, core.Y(jug), core.X(jug) + 1, core.X(jug) + 3);
        var bot = core.Tanks.Skip(TanksCore.Seats).First(t => t.Alive);
        bot.Cell = jug + 3;
        bot.Move = -1;
        bot.Step = 0;
        bot.Dir = 2;
        bot.Want = 2;
        bot.Reload = 0;
        h.Tick(30);
        Assert.Equal("over", Phase(h));
        var done = Assert.Single(h.Finished);
        Assert.Empty(done.Result.Winners);
        Assert.Contains("глек розбили", done.Result.Text);
        Assert.StartsWith("💔 Глек розбили", done.Result.Verdict);   // статус столу — не «Нічия»
        Assert.Empty(h.Scores);                                 // проти 🤖 — не в таблицю
    }

    [Fact]
    public void Clearing_all_waves_wins_the_coop()
    {
        var core = new TanksCore(new Random(3));
        core.SetSides([0, 0, -1, -1, -1, -1], bases: false, waves: true);
        core.Reset([true, true, false, false, false, false]);
        for (var i = 0; i < 20000 && !core.Won; i++)
        {
            core.Step();
            foreach (var t in core.Tanks.Skip(TanksCore.Seats)) if (t.Alive && t.Shield == 0) { t.Alive = false; }
            core.BaseUp[0] = true;
        }
        Assert.True(core.Won);
        Assert.Equal(TanksCore.WaveCount, core.Wave);
    }

    [Fact]
    public void Waves_are_for_two_to_four()
    {
        var h = Table(5, options: new { mode = "waves" });
        var v = h.View(null);
        Assert.False(v.TryGetProperty("coop", out _));
        Assert.Contains("Хвилі", v.GetProperty("note").GetString());
    }

    [Fact]
    public void Bot_fights_play_out_on_their_own_and_stay_cheap()
    {
        // Троє людей стріляють і їздять, 🤖 лізуть хвилями: тик ≤ 0,25 мс, кадр ≤ 1,5 КБ.
        var h = new RoomHarness("tanks", new { mode = "waves", map = "wild" }, seed: 7);
        foreach (var nick in Nicks.Take(3)) h.Join(nick);
        h.Start();
        long ticks = 0, maxFrame = 0;
        var n = 0;
        for (var i = 0; i < 3000; i++)
        {
            if (h.Room.Status == RoomStatus.Finished) h.Rematch();
            for (var s = 0; s < 3; s++)
            {
                if ((i + s) % (7 + s) == 0) h.Input(s, "move", new { dir = (i / 7 + s) % 4 });
                if (i % 5 == s) h.Input(s, "fire");
            }
            h.Clock.AdvanceMs(TanksCore.TickMs);
            var t0 = Stopwatch.GetTimestamp();
            var outbox = h.Rooms.Tick(h.Room);
            ticks += Stopwatch.GetTimestamp() - t0;
            n++;
            foreach (var o in outbox)
                if (o is RoomFrame f) maxFrame = Math.Max(maxFrame, Encoding.UTF8.GetByteCount(Views.Text(f.Frame)));
        }
        var avg = ticks * 1000.0 / Stopwatch.Frequency / n;
        Assert.True(avg < 0.25, $"середній тик {avg} мс");
        Assert.True(maxFrame <= 1500, $"найбільший кадр {maxFrame} Б");
    }
}
