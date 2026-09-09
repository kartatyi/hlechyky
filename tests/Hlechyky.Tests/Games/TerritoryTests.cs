using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Земля: правила загарбання перевіряємо на голому ядрі <see cref="TerritoryCore"/> (поле розкладається
/// руками, крок за кроком), а кімнатні речі — через <see cref="RoomHarness"/>, тобто рівно так, як у це
/// гратимуть люди (TESTING.md §4).
/// </summary>
public class TerritoryTests
{
    static int Cell(int x, int y) => TerritoryCore.Cell(x, y);

    /// <summary>Поле з одним загарбником: так у полі нема нічого чужого і кожну клітинку видно.</summary>
    static TerritoryCore Solo()
    {
        var core = new TerritoryCore(new Random(1));
        core.Reset([true, false, false, false]);
        return core;
    }

    static TerritoryCore Pair()
    {
        var core = new TerritoryCore(new Random(1));
        core.Reset([true, true, false, false]);
        return core;
    }

    static void Walk(TerritoryCore core, int steps)
    {
        for (var i = 0; i < steps; i++) core.Step();
    }

    static RoomHarness Table(params string[] nicks)
    {
        var h = new RoomHarness("territory", seed: 42);
        foreach (var nick in nicks.Length > 0 ? nicks : ["Оля", "Петро"]) h.Join(nick);
        h.Start();
        return h;
    }

    static TerritoryCore Core(RoomHarness h) => ((Territory)h.Room.Game).Core;

    /// <summary>Скільки клітинок у рядку виду належить місцю seat.</summary>
    static int Count(JsonElement view, string field, int seat) =>
        view.GetProperty(field).GetString()!.Count(c => c == (char)('1' + seat));

    // ---------- ядро: замикання ----------

    [Fact]
    public void Everyone_starts_on_a_three_by_three_plot_and_nothing_more()
    {
        var core = Pair();

        Assert.Equal(9, core.Area(0));
        Assert.Equal(9, core.Area(1));
        Assert.Equal(0, core.Area(2));
        Assert.Equal(18, core.Owner.Count(o => o != 0));
        Assert.All(core.Trail, t => Assert.Equal(0, t));
        Assert.True(core.Riders[0].Alive);
        Assert.False(core.Riders[2].On);
    }

    [Fact]
    public void Four_riders_who_touch_nothing_do_not_burn_at_the_start()
    {
        var core = new TerritoryCore(new Random(1));
        core.Reset([true, true, true, true]);
        // Двадцять тиків — це дві секунди на подумати. Поки місця 2 і 3 дивились уздовж чужих рядів,
        // четверо, які нічого не натиснули, згорали лоб у лоб уже на першій секунді.
        Walk(core, 20);

        Assert.All(core.Riders, r => Assert.True(r.Alive));
    }

    [Fact]
    public void A_closed_rectangle_becomes_land_cell_by_cell()
    {
        var core = Solo();
        // Обхід 5×4: з наділу праворуч до (12,7), униз до (12,10), ліворуч до (8,10) і вгору додому.
        Walk(core, 4);
        core.Turn(0, 1); Walk(core, 3);
        core.Turn(0, 2); Walk(core, 4);
        core.Turn(0, 3); Walk(core, 2);

        var want = new HashSet<int>();
        for (var y = 7; y <= 10; y++) for (var x = 8; x <= 12; x++) want.Add(Cell(x, y));   // сам обхід і те, що всередині
        for (var y = 6; y <= 8; y++) for (var x = 7; x <= 9; x++) want.Add(Cell(x, y));     // стартовий наділ
        Assert.Equal(25, want.Count);

        for (var c = 0; c < TerritoryCore.Cells; c++)
        {
            Assert.Equal(want.Contains(c) ? 1 : 0, core.Owner[c]);
            Assert.Equal(0, core.Trail[c]);
        }
        Assert.Equal(25, core.Area(0));
        Assert.False(core.Riders[0].HasTrail);
    }

    [Fact]
    public void The_tick_that_closes_the_loop_reports_every_new_cell()
    {
        var core = Solo();
        Walk(core, 4);
        core.Turn(0, 1); Walk(core, 3);
        core.Turn(0, 2); Walk(core, 4);
        core.Turn(0, 3); Walk(core, 1);
        core.Step();                                  // саме цим кроком він повертається додому

        Assert.Equal(16, core.OwnerChanged.Count);    // 25 клітинок мінус дев'ять, що вже були своїми
        Assert.Equal(11, core.TrailChanged.Count);    // увесь слід згас, ставши землею
    }

    [Fact]
    public void The_fill_does_not_leak_through_a_diagonal_corner()
    {
        var core = new TerritoryCore(new Random(1));
        core.Reset([false, false, false, false]);
        // Ромб із чотирьох клітинок: вони торкаються лише кутами, і заливка по 4-зв'язності всередину не пролізе.
        core.PaintOwner(Cell(11, 7), 0);
        core.PaintOwner(Cell(10, 8), 0);
        core.PaintOwner(Cell(12, 8), 0);
        core.PaintOwner(Cell(11, 9), 0);
        core.Capture(0);

        Assert.Equal(1, core.Owner[Cell(11, 8)]);
        Assert.Equal(5, core.Area(0));
        Assert.Equal(0, core.Owner[Cell(10, 7)]);      // кут ромба — це ще не стіна для того, хто зовні
    }

    [Fact]
    public void Enemy_land_inside_the_loop_changes_hands()
    {
        var core = Solo();
        core.PaintOwner(Cell(10, 9), 1);               // чужий клаптик рівно посеред майбутньої петлі
        Walk(core, 4);
        core.Turn(0, 1); Walk(core, 3);
        core.Turn(0, 2); Walk(core, 4);
        core.Turn(0, 3); Walk(core, 2);

        Assert.Equal(1, core.Owner[Cell(10, 9)]);
        Assert.Equal(0, core.Area(1));
        Assert.Equal(25, core.Area(0));
    }

    [Fact]
    public void A_trail_is_left_only_outside_your_own_land()
    {
        var core = Solo();
        core.Step();
        Assert.Equal(0, core.Trail[Cell(9, 7)]);       // ще свій наділ
        Assert.False(core.Riders[0].HasTrail);

        core.Step();
        Assert.Equal(1, core.Trail[Cell(10, 7)]);
        Assert.True(core.Riders[0].HasTrail);
    }

    [Fact]
    public void A_pocket_inside_someone_elses_ring_is_not_stolen()
    {
        var core = Pair();
        core.Wipe();
        // Кільце Петра 5×5 із порожньою кишенею всередині: для заливки чужа земля — така сама «не моя»
        // клітинка, як і порожня, тож заливка крізь кільце проходить і кишеню Оля не отримує.
        for (var y = 20; y <= 24; y++)
            for (var x = 20; x <= 24; x++)
                if (x is 20 or 24 || y is 20 or 24) core.PaintOwner(Cell(x, y), 1);
        // Оля тим часом замикає своє кільце в іншому кутку — і забирає лише те, що всередині нього.
        for (var y = 5; y <= 9; y++)
            for (var x = 5; x <= 9; x++)
                if (x is 5 or 9 || y is 5 or 9) core.PaintOwner(Cell(x, y), 0);
        core.Capture(0);

        Assert.Equal(0, core.Owner[Cell(22, 22)]);
        Assert.Equal(16, core.Area(1));               // кільце Петра ціле, кишеня всередині нічия
        Assert.Equal(1, core.Owner[Cell(7, 7)]);      // а своя кишеня — своя
        Assert.Equal(25, core.Area(0));
    }

    [Fact]
    public void A_trail_over_enemy_land_does_not_take_it()
    {
        var core = Pair();
        core.PaintOwner(Cell(10, 7), 1);               // Петрів клаптик просто на дорозі
        Walk(core, 2);

        Assert.Equal(1, core.Trail[Cell(10, 7)]);
        Assert.Equal(2, core.Owner[Cell(10, 7)]);      // слід сам по собі землі не забирає
    }

    // ---------- ядро: смерть ----------

    [Fact]
    public void Riding_over_your_own_trail_burns_you()
    {
        var core = Solo();
        Walk(core, 6);
        core.Turn(0, 1); Walk(core, 1);
        core.Turn(0, 2); Walk(core, 3);
        core.Turn(0, 3); Walk(core, 1);                // і носом у власний слід на (11,7)

        Assert.False(core.Riders[0].Alive);
        Assert.Equal(TerritoryCore.RespawnTicks, core.Riders[0].RespawnIn);
        Assert.Equal(0, core.Area(0));
    }

    [Fact]
    public void Riding_over_a_foreign_trail_burns_its_owner()
    {
        var core = Pair();
        core.PaintTrail(Cell(20, 15), 0);
        var olya = core.Riders[0];
        olya.X = 19; olya.Y = 15; olya.Dir = 3; olya.HasTrail = true;
        var petro = core.Riders[1];
        petro.X = 20; petro.Y = 16; petro.Dir = 3;
        core.Step();

        Assert.False(olya.Alive);                      // класика splix: горить власник сліду
        Assert.True(petro.Alive);
        Assert.Equal(20, petro.X);
        Assert.Equal(15, petro.Y);
        Assert.Equal(2, core.Trail[Cell(20, 15)]);     // чужа мітка згасла, лягла своя
    }

    [Fact]
    public void Cutting_each_others_trail_in_one_tick_burns_both()
    {
        var core = Pair();
        core.PaintTrail(Cell(20, 15), 1);              // Петрів слід під носом в Олі
        core.PaintTrail(Cell(21, 15), 0);              // Олин — під носом у Петра
        var olya = core.Riders[0];
        olya.X = 19; olya.Y = 15; olya.Dir = 0; olya.HasTrail = true;
        var petro = core.Riders[1];
        petro.X = 22; petro.Y = 15; petro.Dir = 2; petro.HasTrail = true;
        core.Step();

        // Тик одночасний: той, кому щойно перерізали слід, устигає перерізати чужий, тож горять обидва.
        Assert.False(olya.Alive);
        Assert.False(petro.Alive);
        Assert.Equal(TerritoryCore.RespawnTicks, olya.RespawnIn);
        Assert.Equal(TerritoryCore.RespawnTicks, petro.RespawnIn);
    }

    [Fact]
    public void Two_heads_in_one_cell_burn_both()
    {
        var core = Pair();
        var a = core.Riders[0];
        a.X = 20; a.Y = 15; a.Dir = 0;
        var b = core.Riders[1];
        b.X = 22; b.Y = 15; b.Dir = 2;
        core.Step();

        Assert.False(a.Alive);
        Assert.False(b.Alive);
    }

    [Fact]
    public void Off_the_field_is_death()
    {
        var core = Solo();
        var a = core.Riders[0];
        a.X = 0; a.Y = 15; a.Dir = 2;
        core.Step();

        Assert.False(a.Alive);
        Assert.Equal(0, core.Area(0));
    }

    [Fact]
    public void Death_wipes_both_the_land_and_the_trail()
    {
        var core = Solo();
        Walk(core, 3);
        Assert.Equal(9, core.Area(0));
        Assert.Contains((byte)1, core.Trail);

        core.Burn(0);

        Assert.Equal(0, core.Area(0));
        Assert.All(core.Owner, o => Assert.Equal(0, o));
        Assert.DoesNotContain((byte)1, core.Trail);
    }

    [Fact]
    public void A_burnt_rider_comes_back_in_thirty_ticks_on_free_ground()
    {
        var core = Solo();
        core.Burn(0);
        Walk(core, TerritoryCore.RespawnTicks - 1);
        Assert.False(core.Riders[0].Alive);

        core.Step();

        var r = core.Riders[0];
        Assert.True(r.Alive);
        Assert.Equal(9, core.Area(0));
        Assert.InRange(r.X, 1, TerritoryCore.W - 2);
        Assert.InRange(r.Y, 1, TerritoryCore.H - 2);
    }

    [Fact]
    public void A_rider_left_without_land_burns_too()
    {
        var core = Pair();
        var petro = core.Riders[1];
        petro.X = 20; petro.Y = 15; petro.Dir = 0;
        // Наділ Петра забрали, поки він був у полі: вертатись нема куди, тож він теж починає спочатку.
        for (var c = 0; c < TerritoryCore.Cells; c++) if (core.Owner[c] == 2) core.PaintOwner(c, 0);
        core.Step();

        Assert.False(petro.Alive);
        Assert.Equal(TerritoryCore.RespawnTicks, petro.RespawnIn);

        // І повертається він як усі — за три секунди з новим наділом: інакше правило було б безглузде.
        Walk(core, TerritoryCore.RespawnTicks);
        Assert.True(petro.Alive);
        Assert.Equal(9, core.Area(1));
    }

    // ---------- ядро: керування, площі, детермінізм ----------

    [Fact]
    public void No_u_turn_and_at_most_two_queued_turns()
    {
        var core = Solo();
        core.Turn(0, 2);                               // розворот проти руху — ігноруємо
        core.Step();
        Assert.Equal(0, core.Riders[0].Dir);

        core.Turn(0, 1);
        core.Turn(0, 0);
        core.Turn(0, 1);                               // третій уже не влізе
        core.Step();
        Assert.Equal(1, core.Riders[0].Dir);
        core.Step();
        Assert.Equal(0, core.Riders[0].Dir);
        core.Step();
        Assert.Equal(0, core.Riders[0].Dir);
    }

    [Fact]
    public void Area_is_counted_in_cells_and_shown_in_percent()
    {
        var core = Solo();
        Assert.Equal(9, core.Area(0));
        Assert.Equal(0.8, core.Percent(0), 3);         // дев'ять клітинок із 1200

        core.Wipe();
        for (var c = 0; c < 120; c++) core.PaintOwner(c, 0);
        Assert.Equal(10.0, core.Percent(0), 3);

        core.Wipe();
        for (var c = 0; c < 15; c++) core.PaintOwner(c, 0);
        Assert.Equal(1.3, core.Percent(0), 3);         // рівно 1,25 % — половинку округлюємо вгору, не «до парного»
    }

    [Fact]
    public void The_same_seed_and_the_same_turns_give_the_same_field()
    {
        static string Play(int seed)
        {
            var core = new TerritoryCore(new Random(seed));
            core.Reset([true, true, true, true]);
            for (var i = 0; i < 400; i++)
            {
                if (i % 13 == 0) core.Turn(0, i / 13 % 4);
                if (i % 17 == 0) core.Turn(1, i / 17 % 4);
                if (i % 11 == 0) core.Turn(2, i / 11 % 4);
                core.Step();
            }
            return core.OwnerRow() + "|" + core.TrailRow();
        }

        Assert.Equal(Play(5), Play(5));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Four_riders_for_a_whole_round_are_fast()
    {
        const int ticks = 1000;                        // TESTING.md §4.4 просить саме тисячу — це довше за раунд
        var core = new TerritoryCore(new Random(11));
        core.Reset([true, true, true, true]);
        var dirs = new int[4];
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < ticks; i++)
        {
            // Поворот праворуч кожні три тики — це маленькі петлі, тобто заливка й пожежі раз по раз,
            // а не одна на весь раунд: саме такий тик і треба міряти.
            if (i % 3 == 0)
                for (var s = 0; s < 4; s++)
                {
                    dirs[s] = (dirs[s] + 1) % 4;
                    core.Turn(s, dirs[s]);
                }
            core.Step();
        }
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"{ticks} тиків на чотирьох зайняли {sw.Elapsed}");
    }

    // ---------- кімната ----------

    [Fact]
    public void Territory_is_in_the_catalog_as_a_live_game_for_two_to_four()
    {
        var game = Assert.Single(new Registry().Catalog, g => g.Id == "territory");

        Assert.Equal("live", game.Group);
        Assert.Equal("byHost", game.Start);
        Assert.Equal(TerritoryCore.TickMs, game.TickMs);
        Assert.Equal(2, game.MinPlayers);
        Assert.Equal(4, game.MaxPlayers);
        Assert.False(game.Rated);                      // ставок тут нема: вони лише в рейтингових іграх на двох
        Assert.True(game.HasCss);
        Assert.Equal("territory", game.Module);
    }

    [Fact]
    public void A_table_waiting_for_players_already_shows_the_plots()
    {
        var h = new RoomHarness("territory");
        h.Join("Оля");
        var one = h.View(null);
        Assert.Equal(TerritoryCore.Cells, one.GetProperty("owner").GetString()!.Length);
        Assert.Equal(9, Count(one, "owner", 0));
        Assert.Equal(0, Count(one, "owner", 1));

        h.Join("Петро");
        var two = h.View(null);
        Assert.Equal(9, Count(two, "owner", 1));       // сів другий — з'явився другий наділ
        Assert.Equal(RoomStatus.Lobby, h.Room.Status); // ByHost: сама вона не почнеться
    }

    [Fact]
    public void The_host_starts_the_round_and_every_tick_sends_a_frame()
    {
        var h = new RoomHarness("territory");
        h.Join("Оля");
        h.Join("Петро");
        Assert.False(h.Rooms.StartByHost(h.RoomId, "Петро").Reply.Ok);   // почати може лише господар

        h.Start();
        Assert.Equal(RoomStatus.Playing, h.Room.Status);

        h.Tick(3);
        Assert.Equal(3, h.Outbox.OfType<RoomFrame>().Count());
    }

    [Fact]
    public void View_carries_the_full_rows_for_a_reconnect()
    {
        var h = Table();
        var v = h.View(0);

        foreach (var name in new[] { "width", "height", "turn", "t", "owner", "trail", "heads", "area", "timeLeft" })
            Assert.True(Views.Has(v, name), name);
        Assert.Equal(TerritoryCore.W, v.GetProperty("width").GetInt32());
        Assert.Equal(TerritoryCore.Cells, v.GetProperty("owner").GetString()!.Length);
        Assert.Equal(TerritoryCore.Cells, v.GetProperty("trail").GetString()!.Length);
        Assert.Equal(4, v.GetProperty("heads").GetArrayLength());
        Assert.Equal(4, v.GetProperty("area").GetArrayLength());
        Assert.Equal(TerritoryCore.RoundTicks * TerritoryCore.TickMs, v.GetProperty("timeLeft").GetInt32());

        var head = v.GetProperty("heads")[0];
        foreach (var name in new[] { "on", "alive", "x", "y", "dir", "respawnIn" })
            Assert.True(Views.Has(head, name), name);
        Assert.True(head.GetProperty("on").GetBoolean());
        Assert.False(v.GetProperty("heads")[2].GetProperty("on").GetBoolean());
    }

    [Fact]
    public void The_frame_carries_only_what_changed_this_tick()
    {
        var h = Table();
        h.Tick(1);
        var first = Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);

        foreach (var name in new[] { "t", "heads", "area", "changes", "trails", "timeLeft" })
            Assert.True(Views.Has(first, name), name);
        Assert.Equal(1, first.GetProperty("t").GetInt32());
        // Перший крок обидва роблять усередині свого наділу: мінятись у полі нема чому.
        Assert.Equal(0, first.GetProperty("changes").GetArrayLength());
        Assert.Equal(0, first.GetProperty("trails").GetArrayLength());

        h.Tick(1);
        var second = Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);
        Assert.Equal(2, second.GetProperty("trails").GetArrayLength());   // обидва виїхали з наділу
        Assert.Equal(2, second.GetProperty("trails")[0].GetArrayLength());
        Assert.Equal(0, second.GetProperty("changes").GetArrayLength());
    }

    [Fact]
    public void A_tick_that_changes_almost_everything_travels_as_full_rows()
    {
        var h = Table();
        var core = Core(h);
        core.Wipe();
        for (var c = 0; c < 900; c++) core.PaintOwner(c, 0);   // Оля тримає три чверті поля…
        var olya = core.Riders[0];
        olya.X = 0; olya.Y = 15; olya.Dir = 2;                 // …і наступним кроком іде в стіну: усе це згорить
        h.Tick(1);
        var frame = h.Outbox.OfType<RoomFrame>().Last().Frame;
        var f = Views.Json(frame);

        // Тисяча пар [клітинка, хто] важила б утричі більше за саме поле, тож такий тик іде рядками.
        Assert.Equal(TerritoryCore.Cells, f.GetProperty("owner").GetString()!.Length);
        Assert.Equal(TerritoryCore.Cells, f.GetProperty("trail").GetString()!.Length);
        Assert.Equal(0, f.GetProperty("changes").GetArrayLength());
        Assert.Equal(0, f.GetProperty("trails").GetArrayLength());
        Assert.DoesNotContain('1', f.GetProperty("owner").GetString()!);   // земля Олі згоріла разом із нею
        var bytes = System.Text.Encoding.UTF8.GetByteCount(Views.Text(frame));
        Assert.True(bytes <= 4096, $"кадр на {bytes} Б, а ARCHITECTURE §12 просить ≤ 4 КБ");
    }

    [Fact]
    public void The_heaviest_frame_of_pairs_still_fits_the_budget()
    {
        var h = Table();
        var core = Core(h);
        core.Wipe();
        // Рівно стільки змін, скільки ще їде парами, і в найдорожчих номерах клітинок — це і є найгірший кадр.
        for (var c = TerritoryCore.Cells - Territory.BigFrame + 10; c < TerritoryCore.Cells; c++) core.PaintOwner(c, 0);
        var olya = core.Riders[0];
        olya.X = 0; olya.Y = 15; olya.Dir = 2;
        h.Tick(1);
        var frame = h.Outbox.OfType<RoomFrame>().Last().Frame;
        var f = Views.Json(frame);

        Assert.Equal(JsonValueKind.Null, f.GetProperty("owner").ValueKind);   // рядків нема — самі зміни
        Assert.Equal(Territory.BigFrame - 10, f.GetProperty("changes").GetArrayLength());
        var bytes = System.Text.Encoding.UTF8.GetByteCount(Views.Text(frame));
        Assert.True(bytes <= 4096, $"кадр на {bytes} Б, а ARCHITECTURE §12 просить ≤ 4 КБ");
    }

    [Fact]
    public void The_turn_payload_is_exactly_what_the_module_sends()
    {
        var h = Table();
        h.Input(0, "turn", new { dir = 1 });           // рівно те, що шле web/games/territory.js
        h.Tick(1);
        Assert.Equal(1, Core(h).Riders[0].Dir);

        Assert.False(h.Act(0, "move", new { dir = 2 }).Ok);   // іншого ходу в цій грі нема
        Assert.Equal("Тут так не ходять", h.Reply.Message);
    }

    [Fact]
    public void A_broken_turn_payload_changes_nothing()
    {
        var h = Table();
        var before = Views.Text(h.View(null));

        // З браузера може прилетіти що завгодно, і кривий поворот не має ні падати, ні щось міняти.
        h.Input(0, "turn", new { dir = 99 });
        h.Input(0, "turn", new { dir = -1 });
        h.Input(0, "turn", "вгору");
        h.Input(0, "turn", null);
        h.Input(0, "turn", new { });

        Assert.Equal(before, Views.Text(h.View(null)));
        h.Tick(1);
        Assert.Equal(0, Core(h).Riders[0].Dir);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void Every_seat_sees_the_same_field()
    {
        var h = Table("Оля", "Петро", "Ганна", "Іван");
        h.Tick(5);
        var mine = Views.Text(h.View(0));

        // Ховати в цій грі нема чого: і сусід, і глядач бачать те саме поле, що і я.
        foreach (var seat in new int?[] { 1, 2, 3, null }) Assert.Equal(mine, Views.Text(h.View(seat)));
    }

    [Fact]
    public void A_stranger_cannot_turn_anybody()
    {
        var h = Table();
        h.Rooms.Input(h.RoomId, "Чужий", "turn", Views.Payload(new { dir = 1 }));
        h.Tick(1);

        Assert.Equal(0, Core(h).Riders[0].Dir);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void Ninety_seconds_end_the_round_and_the_journal_shows_the_percents()
    {
        var h = Table();
        EndWith(h, 240, 120);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.False(h.Room.Result!.Draw);
        Assert.Equal("Земля: Оля жовта 20%, Петро зелена 10% — перемогла жовта", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Single(h.Finished);
    }

    [Fact]
    public void A_lead_of_one_cell_is_a_win_and_the_journal_says_whose()
    {
        var h = Table();
        EndWith(h, 244, 243);                          // 20,3 % проти 20,3 %: у табличці відсотки однакові

        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.False(h.Room.Result!.Draw);
        // Без імені переможця такий рядок читався б як нічия, якою він не є.
        Assert.Equal("Земля: Оля жовта 20.3%, Петро зелена 20.3% — перемогла жовта",
            h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void Two_equal_leaders_out_of_four_share_the_win()
    {
        var h = Table("Оля", "Петро", "Ганна", "Іван");
        EndWith(h, 300, 300, 100, 50);

        Assert.Equal([0, 1], h.Room.Result!.Winners);
        Assert.False(h.Room.Result!.Draw);             // нічия — це коли порівну в усіх, а не в двох
        Assert.EndsWith("— перемогли жовта і зелена", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void Equal_areas_end_the_round_in_a_draw()
    {
        var h = Table();
        EndWith(h, 120, 120);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(h.Room.Result!.Draw);
        Assert.Empty(h.Room.Result!.Winners);
        Assert.EndsWith("— нічия", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void Rematch_starts_a_clean_field()
    {
        var h = Table();
        EndWith(h, 240, 120);
        h.Rematch("Оля");

        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("Оля", h.Room.Seats[1]);          // місця обернулись, як і всюди на платформі
        var core = Core(h);
        Assert.Equal(0, core.Ticks);
        Assert.Equal(9, core.Area(0));
        Assert.Equal(9, core.Area(1));
        Assert.All(core.Trail, t => Assert.Equal(0, t));
    }

    [Fact]
    public void The_view_after_a_rematch_is_a_fresh_field()
    {
        var h = Table();
        EndWith(h, 240, 120);
        var last = Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);
        h.Rematch("Оля");

        var v = h.View(0);
        // Лічильник тиків нового раунду знову нульовий, хоч останній кадр минулого був дев'ятисотим:
        // саме тому web/games/territory.js не має права звіряти свіжість виду за t — інакше після
        // «Ще раз» на полі лишалась би минула партія, а стартові наділи їдуть тільки у виді.
        Assert.Equal(TerritoryCore.RoundTicks, last.GetProperty("t").GetInt32());
        Assert.Equal(0, v.GetProperty("t").GetInt32());
        Assert.Equal(9, Count(v, "owner", 0));
        Assert.Equal(9, Count(v, "owner", 1));
        Assert.Equal(18, v.GetProperty("owner").GetString()!.Count(c => c != '0'));
        Assert.All(v.GetProperty("trail").GetString()!, c => Assert.Equal('0', c));
    }

    [Fact]
    public void A_finished_table_reopened_by_a_newcomer_shows_the_new_plots()
    {
        var h = Table();
        EndWith(h, 240, 120);
        Assert.Equal(240, Count(h.View(null), "owner", 0));   // дограна партія лишається на столі

        h.Leave("Петро");
        h.Join("Ганна");                                      // стіл відкрився наново, раунд уже інший

        var v = h.View(null);
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);
        Assert.Equal(9, Count(v, "owner", 0));
        Assert.Equal(9, Count(v, "owner", 1));                // а не поле минулої партії
    }

    [Fact]
    public void Leaving_a_table_of_four_does_not_stop_the_round()
    {
        var h = Table("Оля", "Петро", "Ганна", "Іван");
        h.Tick(5);
        h.Leave("Петро");

        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.False(Core(h).Riders[1].On);
        Assert.Equal(0, Core(h).Area(1));              // його земля згоріла разом із ним
        Assert.Contains(h.Outbox.OfType<Journal>(), j => j.Text.Contains("Петро встав з-за столу, земля згоріла"));
    }

    [Fact]
    public void Leaving_a_table_of_two_hands_the_win_to_the_one_who_stayed()
    {
        var h = Table();
        h.Tick(5);
        h.Leave("Петро");

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Contains("не дограли", h.Room.Result!.Text);
    }

    [Fact]
    public void Territory_keeps_nothing_between_sessions()
    {
        var h = Table();
        Assert.False(h.Room.Info.Persistent);
        Assert.Null(h.Room.Game.Save());               // партія живе п'ять хвилин, зберігати нема чого
    }

    [Fact]
    public void Seat_names_are_the_colours_of_the_plots()
    {
        var h = Table("Оля", "Петро", "Ганна", "Іван");
        Assert.Equal(["жовта", "зелена", "глиняна", "блакитна"], h.Room.Summary().SeatNames);
    }

    /// <summary>
    /// Догнати раунд до останнього тика і розкласти землю руками: голови зупиняємо, щоб цей тик уже нічого
    /// не міняв, — інакше підсумок залежав би від того, у яку стіну хто врізався по дорозі.
    /// </summary>
    static void EndWith(RoomHarness h, params int[] areas)
    {
        h.Tick(TerritoryCore.RoundTicks - 1);
        var core = Core(h);
        foreach (var r in core.Riders) { r.Alive = false; r.RespawnIn = int.MaxValue; }
        core.Wipe();
        var cell = 0;
        for (var seat = 0; seat < areas.Length; seat++)
            for (var i = 0; i < areas[seat]; i++) core.PaintOwner(cell++, seat);
        h.Tick(1);
    }
}
