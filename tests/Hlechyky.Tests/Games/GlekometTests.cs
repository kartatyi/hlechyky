using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Глекомети: фізику, вирви й шкоду перевіряємо на голому <see cref="GlekometCore"/> (там хату можна поставити
/// рівно туди, куди треба), а черги, пропуски, кінець партії й вид — через кімнату (spec glekomet.md §8).
/// </summary>
[Collection(SerialPerf.Name)]
public class GlekometTests(Xunit.Abstractions.ITestOutputHelper output)
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марта", "Тарас"];

    // ---------- підмостки ----------

    static RoomHarness Table(int players = 2, int seed = 42, object? options = null)
    {
        var h = new RoomHarness("glekomet", options, seed: seed);
        foreach (var nick in Nicks.Take(players)) h.Join(nick);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static Glekomet Game(RoomHarness h) => (Glekomet)h.Room.Game;

    static GlekometCore Core(RoomHarness h) => Game(h).Core!;

    static string Phase(RoomHarness h) => Game(h).Phase;

    static JsonElement V(RoomHarness h) => h.View(null);

    /// <summary>Тикати до прицілу (або кінця партії).</summary>
    static void Ready(RoomHarness h)
    {
        for (var i = 0; i < 3000 && Phase(h) != Glekomet.PhaseAim && h.Room.Status == RoomStatus.Playing; i++) h.Tick(1);
    }

    /// <summary>Після пострілу чи пропуску — до наступного прицілу або кінця.</summary>
    static void Settle(RoomHarness h)
    {
        for (var i = 0; i < 400 && Phase(h) != Glekomet.PhaseAim && h.Room.Status == RoomStatus.Playing; i++) h.Tick(1);
    }

    static ActResult Fire(RoomHarness h, int seat, int a, int p, int w = 0) => h.Act(seat, "fire", new { a, p, w });

    /// <summary>Кут, під яким глек на силі 100 зі стартової хати гарантовано вилітає за найближчий край села.</summary>
    static int Away(RoomHarness h, int seat) => Core(h).Huts[seat].X < 500 ? 135 : 45;

    /// <summary>Хто ходить стріляє геть за край — партія йде, а нікому нічого.</summary>
    static void ShootAway(RoomHarness h)
    {
        var s = Game(h).Turn;
        Assert.True(Fire(h, s, Away(h, s), 100).Ok, h.Reply.Message);
        Settle(h);
    }

    static void SkipTurn(RoomHarness h)
    {
        Assert.True(h.Act(Game(h).Turn, "skip").Ok, h.Reply.Message);
        Settle(h);
    }

    /// <summary>Рівне село заданої висоти і хати там, де треба тесту (решта місць — порожні).</summary>
    static GlekometCore Flatten(RoomHarness h, int height, params (int seat, int x)[] huts)
    {
        var core = Core(h);
        core.Flat(height);
        foreach (var (seat, x) in huts) core.Place(seat, x);
        return core;
    }

    static GlekometCore Bare(int height = 100, int seed = 1)
    {
        var core = new GlekometCore(new Random(seed));
        core.Flat(height);
        return core;
    }

    /// <summary>Політ до кінця; повертає останню подію (x, y, kind) або null.</summary>
    static (int X, int Y, string Kind)? Fly(GlekometCore core, int maxTicks = 220)
    {
        (int, int, string)? last = null;
        for (var i = 0; i < maxTicks && core.LiveShells > 0; i++)
        {
            core.ClearMarks();
            core.Step();
            for (var k = 0; k < core.ExCount; k++) last = (core.ExX[k], core.ExY[k], GlekometCore.ExNames[core.ExK[k]]);
        }
        return last;
    }

    /// <summary>Сила пострілу під кутом a, що кладе глек найближче до x на рівному селі з поточним вітром.</summary>
    static int PowerFor(GlekometCore core, int seat, int a, int x, int w = 0)
    {
        int best = 50, miss = int.MaxValue;
        for (var p = 5; p <= 100; p++)
        {
            var probe = Bare(core.H[0]);
            probe.Place(seat, core.Huts[seat].X);
            probe.Wind = core.Wind;
            probe.BeginShot(seat, w);
            var hut = probe.Huts[seat];
            var v = GlekometCore.VPerPower * p;
            probe.Spawn(w, hut.X, hut.Y + GlekometCore.Launch, v * Math.Cos(a * Math.PI / 180), v * Math.Sin(a * Math.PI / 180), false);
            if (Fly(probe) is not { } end) continue;
            var d = Math.Abs(end.X - x);
            if (d < miss) { miss = d; best = p; }
        }
        return best;
    }

    /// <summary>Постріл із кімнати точно в хату target (рівне село): кут 45° або 135°.</summary>
    static void HitHut(RoomHarness h, int seat, int target, int w = 0)
    {
        var core = Core(h);
        var a = core.Huts[target].X > core.Huts[seat].X ? 45 : 135;
        var p = PowerFor(core, seat, a, core.Huts[target].X, w);
        Assert.True(Fire(h, seat, a, p, w).Ok, h.Reply.Message);
        Settle(h);
    }

    static List<JsonElement> Frames(RoomHarness h, int from) =>
        [.. h.Outbox.Skip(from).OfType<RoomFrame>().Select(f => Views.Json(f.Frame))];

    // =============================================================================================
    // Паспорт і старт
    // =============================================================================================

    [Fact]
    public void Catalog_lists_glekomet_as_live_by_host_2_to_6_with_three_options()
    {
        var g = RoomHarness.NewRegistry().Catalog.Single(c => c.Id == "glekomet");
        Assert.Equal("Глекомети", g.Title);
        Assert.Equal("live", g.Group);
        Assert.Equal("byHost", g.Start);
        Assert.Equal((2, 6, 40), (g.MinPlayers, g.MaxPlayers, g.TickMs));
        Assert.False(g.Hidden);
        Assert.Equal(["teams", "turn", "water"], g.Options.Select(o => o.Key));
        Assert.Equal(["solo", "30", "6"], g.Options.Select(o => o.Default));
        Assert.Equal(["20", "30", "45"], g.Options[1].Values.Select(v => v[0]));
        Assert.Equal(["6", "10", "0"], g.Options[2].Values.Select(v => v[0]));
        Assert.Equal("glekomet", g.Module);
    }

    [Fact]
    public void Two_players_cannot_start_alone_and_host_starts_when_two_are_seated()
    {
        var h = new RoomHarness("glekomet", seed: 5);
        h.Join("Оля");
        Assert.False(h.Start().Ok);
        Assert.Contains("Замало гравців", h.Reply.Message);
        Assert.Equal("lobby", V(h).GetProperty("phase").GetString());
        h.Join("Петро");
        Assert.True(h.Start().Ok);
        Assert.Equal(Glekomet.PhaseStart, Phase(h));
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void The_map_is_deterministic_for_a_seed_and_differs_between_seeds()
    {
        var a = Table(4, seed: 42);
        var b = Table(4, seed: 42);
        var c = Table(4, seed: 43);
        Assert.Equal(Views.Text(Game(a).View(null)), Views.Text(Game(b).View(null)));
        Assert.NotEqual(V(a).GetProperty("h").GetRawText(), V(c).GetProperty("h").GetRawText());
    }

    [Fact]
    public void The_lobby_view_does_not_touch_the_room_random()
    {
        var a = new RoomHarness("glekomet", seed: 9);
        a.Join("Оля"); a.Join("Петро");
        for (var i = 0; i < 5; i++) _ = a.View(null);       // лобі дивляться скільки завгодно
        a.Start();
        var b = Table(2, seed: 9);
        Assert.Equal(V(a).GetProperty("h").GetRawText(), V(b).GetProperty("h").GetRawText());
    }

    [Fact]
    public void Huts_stand_on_flat_pads_above_water_and_apart()
    {
        foreach (var seed in new[] { 1, 2, 3, 7, 42, 99, 1234 })
        {
            var h = Table(6, seed);
            var core = Core(h);
            var huts = core.Huts.Where(x => x.Plays).ToArray();
            Assert.Equal(6, huts.Length);
            foreach (var hut in huts)
            {
                Assert.InRange(hut.X, GlekometCore.MinX, GlekometCore.MaxX);
                Assert.Equal(core.Ground(hut.X), hut.Y);
                for (var c = GlekometCore.Col(hut.X - 24); c <= GlekometCore.Col(hut.X + 24); c++) Assert.Equal(hut.Y, core.H[c]);
                Assert.True(hut.Y >= 60 && hut.Y > core.Water, $"сід {seed}: хата на {hut.Y}");
            }
            for (var i = 0; i < huts.Length; i++)
                for (var j = i + 1; j < huts.Length; j++)
                    Assert.True(Math.Abs(huts[i].X - huts[j].X) >= 60, $"сід {seed}: {huts[i].X} і {huts[j].X}");
        }
    }

    [Fact]
    public void Heights_are_within_60_and_340_and_there_are_250_columns()
    {
        foreach (var seed in Enumerable.Range(1, 30))
        {
            var h = Table(2, seed);
            var hs = V(h).GetProperty("h").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            Assert.Equal(250, hs.Length);
            Assert.All(hs, x => Assert.InRange(x, 60, 340));
        }
    }

    // =============================================================================================
    // Фази, хід, таймер
    // =============================================================================================

    [Fact]
    public void Ready_lasts_fifty_ticks_then_the_first_alive_seat_aims()
    {
        var h = Table(3);
        Assert.Equal(50, V(h).GetProperty("startIn").GetInt32());
        h.Tick(49);
        Assert.Equal(Glekomet.PhaseStart, Phase(h));
        Assert.Equal(JsonValueKind.Null, V(h).GetProperty("turn").ValueKind);
        h.Tick(1);
        Assert.Equal(Glekomet.PhaseAim, Phase(h));
        Assert.Equal(0, V(h).GetProperty("turn").GetInt32());
        Assert.Equal(1, V(h).GetProperty("round").GetInt32());
        Assert.Equal(1, V(h).GetProperty("turnNo").GetInt32());
    }

    [Theory]
    [InlineData("20", 500)]
    [InlineData("30", 750)]
    [InlineData("45", 1125)]
    public void Turn_time_option_sets_the_aim_length_in_ticks(string turn, int ticks)
    {
        var h = Table(2, options: new { turn });
        Ready(h);
        Assert.Equal(int.Parse(turn) * 1000, V(h).GetProperty("turnMs").GetInt32());
        h.Tick(ticks - 1);
        Assert.Equal(Glekomet.PhaseAim, Phase(h));
        h.Tick(1);
        Assert.Equal(Glekomet.PhaseSettle, Phase(h));
    }

    [Fact]
    public void A_turn_that_runs_out_is_skipped_and_counted()
    {
        var h = Table(2);
        Ready(h);
        h.Tick(750);
        var v = V(h);
        Assert.Equal(1, v.GetProperty("huts")[0].GetProperty("skips").GetInt32());
        Assert.Contains("Оля: хід прогавлено", v.GetProperty("log").EnumerateArray().Select(e => e.GetString()));
        h.Tick(14);
        Assert.Equal(Glekomet.PhaseSettle, Phase(h));
        h.Tick(1);
        Assert.Equal(Glekomet.PhaseAim, Phase(h));
        Assert.Equal(1, Game(h).Turn);
    }

    [Fact]
    public void Three_skips_in_a_row_retire_the_hut()
    {
        var h = Table(2);
        Ready(h);
        for (var round = 0; round < 3; round++)
        {
            Assert.Equal(0, Game(h).Turn);
            h.Tick(750);                      // Оля спить
            Settle(h);
            if (h.Room.Status != RoomStatus.Playing) break;
            SkipTurn(h);                      // Петро пропускає сам
        }
        var hut = V(h).GetProperty("huts")[0];
        Assert.False(hut.GetProperty("alive").GetBoolean());
        Assert.Equal("afk", hut.GetProperty("reason").GetString());
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Room.Result!.Winners);
    }

    [Fact]
    public void Three_idle_rounds_end_the_match_in_a_draw()
    {
        var h = Table(2);
        Ready(h);
        for (var i = 0; i < 6 && h.Room.Status == RoomStatus.Playing; i++) SkipTurn(h);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(h.Room.Result!.Draw);
        Assert.Equal("Глекомети: так ніхто й не стрельнув — розійшлись", h.Room.Result.Text);
        Assert.Equal("idle", V(h).GetProperty("result").GetProperty("reason").GetString());
    }

    [Fact]
    public void A_shot_in_the_round_resets_the_idle_count()
    {
        var h = Table(2);
        Ready(h);
        for (var i = 0; i < 4; i++) SkipTurn(h);   // два порожні кола
        ShootAway(h);                               // Оля стрельнула
        for (var i = 0; i < 3; i++) SkipTurn(h);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void Turn_order_cycles_over_alive_seats_only_and_round_grows_on_wrap()
    {
        var h = Table(3);
        Ready(h);
        Core(h).Kill(1, "hit");
        Assert.Equal(0, Game(h).Turn);
        SkipTurn(h);
        Assert.Equal(2, Game(h).Turn);
        Assert.Equal(1, Game(h).Round);
        ShootAway(h);
        Assert.Equal(0, Game(h).Turn);
        Assert.Equal(2, Game(h).Round);
    }

    [Fact]
    public void EndsAt_matches_clock_plus_remaining_ticks()
    {
        var h = Table(2);
        Ready(h);
        h.Tick(100);
        var ends = DateTimeOffset.Parse(V(h).GetProperty("endsAt").GetString()!);
        var left = ends - h.Clock.UtcNow;
        Assert.InRange(left.TotalMilliseconds, 650 * 40 - 1, 650 * 40 + 1);
        Assert.Equal(650, Game(h).LeftTicks);
    }

    [Fact]
    public void Wind_changes_every_turn_within_five_either_way()
    {
        var h = Table(2);
        Ready(h);
        var winds = new HashSet<int>();
        for (var i = 0; i < 12 && h.Room.Status == RoomStatus.Playing; i++)
        {
            var wind = V(h).GetProperty("wind").GetInt32();
            Assert.InRange(wind, -5, 5);
            winds.Add(wind);
            ShootAway(h);
        }
        Assert.True(winds.Count > 2);
    }

    // =============================================================================================
    // Дії й відмови
    // =============================================================================================

    static void Refused(RoomHarness h, int seat, string action, object? payload, string text)
    {
        var before = Views.Text(Game(h).View(null));
        var r = h.Act(seat, action, payload);
        Assert.False(r.Ok);
        Assert.Equal(text, r.Message);
        Assert.Equal(before, Views.Text(Game(h).View(null)));
    }

    [Fact]
    public void Fire_is_refused_out_of_turn_with_the_right_text()
    {
        var h = Table(3);
        Refused(h, 0, "fire", new { a = 45, p = 60, w = 0 }, "Зачекай, зараз почнемо");
        Ready(h);
        Refused(h, 1, "fire", new { a = 45, p = 60, w = 0 }, "Зараз не твій хід");
        Refused(h, 2, "move", new { dir = 1 }, "Зараз не твій хід");
        Refused(h, 1, "skip", null, "Зараз не твій хід");
        Refused(h, 0, "dance", null, "Тут так не ходять");
    }

    [Fact]
    public void Fire_is_refused_while_a_shell_flies()
    {
        var h = Table(2);
        Ready(h);
        Assert.True(Fire(h, 0, Away(h, 0), 100).Ok);
        Refused(h, 0, "fire", new { a = 45, p = 60, w = 0 }, "Зачекай, глек ще летить");
        for (var i = 0; i < 300 && Phase(h) == Glekomet.PhaseFly; i++) h.Tick(1);
        Assert.Equal(Glekomet.PhaseSettle, Phase(h));
        Refused(h, 0, "fire", new { a = 45, p = 60, w = 0 }, "Зачекай, глек ще летить");
        Refused(h, 0, "move", 1, "Зачекай, глек ще летить");
    }

    [Fact]
    public void Fire_validates_angle_power_and_weapon()
    {
        var h = Table(2);
        Ready(h);
        Refused(h, 0, "fire", new { a = 181, p = 60, w = 0 }, "Кут — від 0 до 180");
        Refused(h, 0, "fire", new { a = -1, p = 60, w = 0 }, "Кут — від 0 до 180");
        Refused(h, 0, "fire", new { a = 45.5, p = 60, w = 0 }, "Кут — від 0 до 180");
        Refused(h, 0, "fire", new { a = 45, p = 4, w = 0 }, "Сила — від 5 до 100");
        Refused(h, 0, "fire", new { a = 45, p = 101, w = 0 }, "Сила — від 5 до 100");
        Refused(h, 0, "fire", new { a = 45, w = 0 }, "Сила — від 5 до 100");
        Refused(h, 0, "fire", new { a = 45, p = 60, w = 7 }, "Такої зброї в коморі нема");
        Refused(h, 0, "fire", new { a = 45, p = 60, w = "pot" }, "Такої зброї в коморі нема");
        Refused(h, 0, "fire", new { a = 45, p = 60 }, "Такої зброї в коморі нема");
        Refused(h, 0, "fire", 45, "Такої зброї в коморі нема");
    }

    [Fact]
    public void An_empty_weapon_cannot_be_fired_but_the_pot_is_infinite()
    {
        var h = Table(2);
        Ready(h);
        for (var i = 0; i < 2; i++)
        {
            Assert.True(Fire(h, 0, Away(h, 0), 100, GlekometCore.Shards).Ok, h.Reply.Message);
            Settle(h);
            ShootAway(h);
        }
        Assert.Equal(0, V(h).GetProperty("inv")[0][1].GetInt32());
        Refused(h, 0, "fire", new { a = Away(h, 0), p = 100, w = 1 }, "Цього вже не лишилось");
        for (var i = 0; i < 10; i++)
        {
            Assert.True(Fire(h, 0, Away(h, 0), 100, 0).Ok, h.Reply.Message);
            Settle(h);
            ShootAway(h);
        }
        Assert.Equal(-1, V(h).GetProperty("inv")[0][0].GetInt32());
        Assert.Equal(12, V(h).GetProperty("stats")[0].GetProperty("shots").GetInt32());
    }

    [Fact]
    public void Move_costs_eight_fuel_and_stops_at_the_village_edge()
    {
        var h = Table(2);
        Ready(h);
        var core = Flatten(h, 150, (0, 300), (1, 700));
        Assert.True(h.Act(0, "move", new { dir = 1 }).Ok, h.Reply.Message);
        Assert.Equal(308, core.Huts[0].X);
        Assert.Equal(112, core.Huts[0].Fuel);
        Assert.Equal(112, V(h).GetProperty("huts")[0].GetProperty("fuel").GetInt32());
        core.Huts[0].X = 970;
        Refused(h, 0, "move", new { dir = 1 }, "Далі — край села");
        core.Huts[0].X = 30;
        Refused(h, 0, "move", -1, "Далі — край села");
        Refused(h, 0, "move", new { dir = 2 }, "Такого напрямку нема");
        core.Huts[0].X = 300;
        for (var i = 0; i < 14; i++) Assert.True(h.Act(0, "move", i % 2 == 0 ? 1 : -1).Ok, h.Reply.Message);
        Assert.Equal(0, core.Huts[0].Fuel);
        Refused(h, 0, "move", 1, "Пальне скінчилось");
        Assert.Equal(Glekomet.PhaseAim, Phase(h));
    }

    [Fact]
    public void Move_refuses_steep_climbs_but_allows_any_descent()
    {
        var h = Table(2);
        Ready(h);
        var core = Flatten(h, 150, (0, 300), (1, 800));
        for (var c = GlekometCore.Col(308); c < 150; c++) core.H[c] = 163;
        Refused(h, 0, "move", 1, "Туди не заїхати — крутий схил");
        for (var c = GlekometCore.Col(308); c < 150; c++) core.H[c] = 162;
        Assert.True(h.Act(0, "move", 1).Ok, h.Reply.Message);
        Assert.Equal(162, core.Huts[0].Y);
        Assert.Equal(100, core.Huts[0].Hp);
        for (var c = GlekometCore.Col(316); c < 150; c++) core.H[c] = 102;
        Assert.True(h.Act(0, "move", 1).Ok, h.Reply.Message);
        Assert.Equal(102, core.Huts[0].Y);
        Assert.Equal(100 - (60 - 24) / 2, core.Huts[0].Hp);
        Assert.Contains("Оля: хата впала у вирву −18", V(h).GetProperty("log").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Move_refuses_a_step_into_another_hut()
    {
        var h = Table(2);
        Ready(h);
        Flatten(h, 150, (0, 300), (1, 345));
        Refused(h, 0, "move", 1, "Там уже стоїть хата");
        Assert.True(h.Act(0, "move", -1).Ok);
    }

    [Fact]
    public void Moving_into_the_pond_drowns_the_hut_and_passes_the_turn()
    {
        var h = Table(3);
        Ready(h);
        var core = Flatten(h, 150, (0, 300), (1, 500), (2, 800));
        for (var c = GlekometCore.Col(308); c < GlekometCore.Col(400); c++) core.H[c] = 10;
        Assert.True(h.Act(0, "move", 1).Ok, h.Reply.Message);
        Assert.False(core.Huts[0].Alive);
        Assert.Equal("drown", core.Huts[0].Reason);
        Assert.Equal(Glekomet.PhaseSettle, Phase(h));
        // у паузі на полі — саме ця біда, а не минулий постріл
        Assert.Equal("Оля: хату затопило", V(h).GetProperty("last").GetProperty("text").GetString());
        Assert.Equal("drown", V(h).GetProperty("last").GetProperty("hits")[0].GetProperty("kind").GetString());
        Settle(h);
        Assert.Equal(1, Game(h).Turn);
    }

    [Fact]
    public void Skip_ends_the_turn_without_a_skip_mark()
    {
        var h = Table(2);
        Ready(h);
        var r = h.Act(0, "skip");
        Assert.True(r.Ok);
        Assert.Equal("Хід пропущено", r.Message);
        Assert.Equal(Glekomet.PhaseSettle, Phase(h));
        h.Tick(15);
        Assert.Equal(Glekomet.PhaseAim, Phase(h));
        Assert.Equal(1, Game(h).Turn);
        Assert.Equal(0, V(h).GetProperty("huts")[0].GetProperty("skips").GetInt32());
    }

    [Fact]
    public void Server_accepts_exactly_what_the_module_sends()
    {
        // glekomet.js: ctx.act('fire', {a, p, w}), ctx.act('move', {dir}), ctx.act('skip'), ctx.input('aim', {a, p, w})
        var h = Table(2);
        Ready(h);
        Flatten(h, 150, (0, 300), (1, 700));
        Assert.True(h.Act(0, "move", new { dir = 1 }).Ok, h.Reply.Message);
        Assert.True(h.Act(0, "move", -1).Ok, h.Reply.Message);
        var mark = h.Outbox.Count;
        h.Input(0, "aim", new { a = 60, p = 70, w = 2 });
        h.Tick(1);
        var frame = Frames(h, mark).Last();
        Assert.Equal([60, 70, 2], frame.GetProperty("aim").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal([60, 70, 2], V(h).GetProperty("aim").EnumerateArray().Select(e => e.GetInt32()));
        h.Input(1, "aim", new { a = 10, p = 10, w = 0 });                   // не його хід — мовчки ні
        h.Input(0, "aim", new { a = 200, p = 70, w = 2 });                  // поза межами — мовчки ні
        Assert.Equal([60, 70, 2], V(h).GetProperty("aim").EnumerateArray().Select(e => e.GetInt32()));
        Assert.True(h.Act(0, "fire", new { a = 45, p = 60, w = 0 }).Ok, h.Reply.Message);
        Assert.Equal(Glekomet.PhaseFly, Phase(h));
        Settle(h);
        Assert.True(h.Act(1, "skip", null).Ok);
    }

    [Fact]
    public void Fire_move_and_skip_also_work_through_input()
    {
        var h = Table(2);
        Ready(h);
        Flatten(h, 150, (0, 300), (1, 700));
        h.Input(0, "move", 1);
        Assert.Equal(308, Core(h).Huts[0].X);
        h.Input(0, "fire", new { a = 135, p = 100, w = 0 });
        Assert.Equal(Glekomet.PhaseFly, Phase(h));
        Settle(h);
        h.Input(1, "skip");
        Assert.Equal(Glekomet.PhaseSettle, Phase(h));
    }

    // =============================================================================================
    // Політ і фізика
    // =============================================================================================

    [Fact]
    public void A_full_power_shot_at_45_degrees_crosses_the_whole_village_without_wind()
    {
        var core = Bare(100);
        core.Place(0, 30);
        core.Fire(0, 45, 100, GlekometCore.Pot, 0);
        var end = Fly(core);
        Assert.NotNull(end);
        Assert.True(end.Value.Kind == "out" || end.Value.X >= 950, $"{end}");
    }

    static int Landing(int wind, int w = GlekometCore.Pot, int a = 45, int p = 60)
    {
        var core = Bare(100);
        core.Place(0, 100);
        core.Fire(0, a, p, w, wind);
        var end = Fly(core);
        Assert.NotNull(end);
        return end.Value.X;
    }

    [Fact]
    public void Wind_bends_the_flight_by_the_specified_acceleration()
    {
        var diff = Landing(5) - Landing(-5);
        Assert.InRange(diff, 155, 185);
        Assert.True(Landing(0) > Landing(-5) && Landing(0) < Landing(5));
    }

    [Fact]
    public void A_shell_explodes_on_the_surface_and_carves_a_crater_of_its_radius()
    {
        foreach (var (w, depth, cols) in new[] { (GlekometCore.Pot, 21, 10), (GlekometCore.Varenyk, 39, 20) })
        {
            var core = Bare(200);
            core.BeginShot(0, w);
            core.Explode(w, 500, 200, -1);
            Assert.InRange(200 - core.H.Min(), depth - 1, depth + 1);
            Assert.Equal(cols, core.H.Count(x => x < 200));
            Assert.Equal(cols, Enumerable.Range(0, 250).Count(c => core.Dirty[c]));
        }
    }

    [Fact]
    public void A_crater_in_the_air_bites_only_what_it_reaches()
    {
        var core = Bare(200);
        core.Crater(500, 230, 22);                  // вибух за 30 u над землею: радіус 22 землі не дістає
        Assert.All(core.H, x => Assert.Equal(200, x));
        core.Crater(500, 210, 22);                  // за 10 u — неглибока ямка
        Assert.InRange(200 - core.H.Min(), 11, 12);
    }

    [Fact]
    public void Isqrt_is_exact_on_squares_and_neighbours()
    {
        for (var n = 0; n < 5000; n++)
        {
            var s = GlekometCore.Isqrt(n);
            Assert.True(s * s <= n && (s + 1) * (s + 1) > n, $"{n} → {s}");
        }
    }

    [Fact]
    public void A_direct_hit_deals_full_damage_and_a_near_miss_scales_with_distance()
    {
        var core = Bare(100);
        core.Place(0, 200);
        var target = core.Place(1, 600);
        core.BeginShot(0, GlekometCore.Pot);
        core.Spawn(GlekometCore.Pot, 612, 200, 0, -100, false);          // у край даху — усе одно пряме
        Fly(core);
        Assert.Equal(65, target.Hp);
        Assert.Equal(1 << 1, core.ShotDirect);

        target.Hp = 100;
        core.BeginShot(0, GlekometCore.Pot);
        core.Explode(GlekometCore.Pot, 630, 100, -1);                      // 10 u від стіни
        Assert.Equal(75, target.Hp);
        Assert.Equal(25, core.ShotDmg[1]);
        Assert.Equal(0, core.ShotDmg[0]);
        Assert.Equal(25, GlekometCore.Falloff(35, 36, 10));
        Assert.Equal(0, GlekometCore.Falloff(35, 36, 36));
    }

    [Fact]
    public void Shards_split_at_the_apex_into_four_and_each_can_hit()
    {
        var core = Bare(100);
        core.Place(0, 100);
        core.Place(1, 520);
        core.Place(2, 600);
        core.Fire(0, 60, 70, GlekometCore.Shards, 0);
        var split = false;
        for (var i = 0; i < 200 && core.LiveShells > 0; i++)
        {
            core.ClearMarks();
            core.Step();
            if (!split && core.LiveShells == 4)
            {
                split = true;
                Assert.All(core.Shells.Where(s => s.Alive), s => Assert.True(s.Split && s.Kind == GlekometCore.Shards));
            }
        }
        Assert.True(split);
        Assert.True(core.ShotDmg.Sum() <= 64);
    }

    [Fact]
    public void Shards_hit_several_huts_with_one_shot()
    {
        // Скалки летять віялом над двома хатами поруч — дістається обом.
        var core = Bare(100);
        core.Place(0, 100);
        core.Place(1, 480);
        core.Place(2, 560);
        var hit2 = 0;
        for (var p = 50; p <= 90 && hit2 < 2; p++)
        {
            core.Flat(100);
            core.Place(0, 100); core.Place(1, 480); core.Place(2, 560);
            core.Fire(0, 60, p, GlekometCore.Shards, 0);
            Fly(core);
            hit2 = (core.ShotDmg[1] > 0 ? 1 : 0) + (core.ShotDmg[2] > 0 ? 1 : 0);
        }
        Assert.Equal(2, hit2);
    }

    [Fact]
    public void The_varenyk_feels_only_forty_percent_of_the_wind()
    {
        var pot = Landing(5) - Landing(0);
        var varenyk = Landing(5, GlekometCore.Varenyk) - Landing(0, GlekometCore.Varenyk);
        Assert.InRange(varenyk / (double)pot, 0.36, 0.44);
    }

    [Fact]
    public void A_shell_over_water_splashes_without_damage_and_out_of_bounds_is_lost()
    {
        var core = Bare(10);                          // ставок: земля нижче води
        for (var c = 0; c < 50; c++) core.H[c] = 200; // хата на березі
        var hut = core.Place(0, 100);
        core.Fire(0, 45, 60, GlekometCore.Pot, 0);
        var end = Fly(core);
        Assert.Equal("splash", end!.Value.Kind);
        Assert.Equal(core.Water, end.Value.Y);
        Assert.Equal(100, hut.Hp);
        Assert.Equal(1, core.ShotSplash);

        core.Fire(0, 135, 100, GlekometCore.Pot, 0);
        end = Fly(core);
        Assert.Equal("out", end!.Value.Kind);
        Assert.Equal(0, end.Value.X);
        Assert.Equal(1, core.ShotOut);
    }

    [Fact]
    public void A_flight_never_exceeds_eight_seconds()
    {
        var core = Bare(100);
        core.Place(0, 500);
        core.Fire(0, 90, 100, GlekometCore.Pot, 0);
        var ticks = 0;
        while (core.LiveShells > 0 && ticks < 300) { core.ClearMarks(); core.Step(); ticks++; }
        Assert.True(ticks <= Glekomet.FlyTicks, $"{ticks}");

        core.BeginShot(0, GlekometCore.Pot);
        core.Spawn(GlekometCore.Pot, 500, 100000, 0, 0, false);            // «завис» високо в небі
        ticks = 0;
        (int, int, string)? end = null;
        while (core.LiveShells > 0 && ticks < 300)
        {
            core.ClearMarks();
            core.Step();
            ticks++;
            if (core.ExCount > 0) end = (core.ExX[0], core.ExY[0], GlekometCore.ExNames[core.ExK[0]]);
        }
        Assert.Equal(200, ticks);
        Assert.Equal("cloud", end!.Value.Item3);
    }

    // =============================================================================================
    // Земля, падіння, вода, спецснаряди
    // =============================================================================================

    [Fact]
    public void Ground_blown_from_under_a_hut_drops_it_and_hurts_beyond_24_units()
    {
        var core = Bare(200);
        var hut = core.Place(0, 500);
        core.Crater(500, 200, 40);
        core.Crater(500, core.Ground(500), 40);
        var drop = 200 - core.Ground(500);
        core.Settle();
        Assert.Equal(core.Ground(500), hut.Y);
        Assert.Equal(100 - (drop - 24) / 2, hut.Hp);
        Assert.Equal((drop - 24) / 2, core.ShotFall[0]);

        var small = Bare(200);
        var calm = small.Place(0, 500);
        small.Crater(500, 200, 22);
        small.Settle();
        Assert.Equal(179, calm.Y);
        Assert.Equal(100, calm.Hp);                    // 21 u — ще не падіння
    }

    [Fact]
    public void Hay_raises_a_mound_and_lifts_a_buried_hut_without_damage()
    {
        var core = Bare(100);
        var hut = core.Place(0, 500);
        core.Mound(500);
        Assert.InRange(core.Ground(500) - 100, 23, 27);
        Assert.Equal(100, core.H[GlekometCore.Col(460)]);          // за 40 u — земля та сама
        core.Settle();
        Assert.Equal(core.Ground(500), hut.Y);
        Assert.Equal(100, hut.Hp);

        var shot = Bare(100);
        shot.Place(0, 100);
        shot.Fire(0, 45, 60, GlekometCore.Hay, 0);
        var end = Fly(shot);
        Assert.Equal("hay", end!.Value.Kind);
        Assert.Equal(1, shot.ShotHay);
        Assert.True(shot.H.Max() > 120);
    }

    [Fact]
    public void The_stork_moves_the_shooter_to_the_landing_spot()
    {
        var core = Bare(100);
        var hut = core.Place(0, 100);
        core.Place(1, 900);
        core.Fire(0, 45, 60, GlekometCore.Stork, 0);
        var end = Fly(core);
        Assert.Equal("stork", end!.Value.Kind);
        Assert.Equal(1, core.ShotStork);
        Assert.Equal(end.Value.X, hut.X);
        Assert.Equal(core.Ground(hut.X), hut.Y);
        Assert.Equal(100, hut.Hp);
        Assert.NotEqual(0, core.MovedMask & 1);
    }

    [Fact]
    public void The_stork_is_wasted_over_water_or_when_no_dry_spot_is_near()
    {
        var core = Bare(10);
        for (var c = 0; c < 50; c++) core.H[c] = 200;
        var hut = core.Place(0, 100);
        core.Fire(0, 45, 60, GlekometCore.Stork, 0);
        Assert.Equal("splash", Fly(core)!.Value.Kind);
        Assert.Equal(100, hut.X);

        // суша є, але вся в радіусі 60 — під водою, крім острівця під самою хатою сусіда
        var dry = Bare(10);
        dry.Place(0, 100);
        Assert.Equal(-1, dry.StorkSpot(0, 500));
        for (var c = GlekometCore.Col(470); c <= GlekometCore.Col(530); c++) dry.H[c] = 100;
        dry.Place(1, 500);
        Assert.Equal(-1, dry.StorkSpot(0, 500));

        // і через кімнату: лелеку списано, у журналі — чому
        var h = Table(2);
        Ready(h);
        var room = Flatten(h, 10);
        for (var c = 0; c < 50; c++) room.H[c] = 200;
        for (var c = GlekometCore.Col(860); c <= GlekometCore.Col(940); c++) room.H[c] = 200;
        room.Place(0, 100);
        room.Place(1, 900);
        room.Wind = 0;
        Assert.True(Fire(h, 0, 45, 60, GlekometCore.Stork).Ok, h.Reply.Message);
        Settle(h);
        Assert.Equal(0, V(h).GetProperty("inv")[0][4].GetInt32());
        Assert.Contains("лелека на воду не сідає", V(h).GetProperty("last").GetProperty("text").GetString());
    }

    [Fact]
    public void Khrin_poisons_everyone_in_reach_for_three_turns_of_eight()
    {
        var core = Bare(100);
        core.Place(0, 100);
        var a = core.Place(1, 500);
        var b = core.Place(2, 560);
        var far = core.Place(3, 800);
        core.BeginShot(0, GlekometCore.Khrin);
        core.Explode(GlekometCore.Khrin, 530, 100, -1);
        Assert.Equal(3, a.Poison);
        Assert.Equal(3, b.Poison);
        Assert.Equal(0, far.Poison);
        Assert.Equal(100 - GlekometCore.Falloff(12, 40, 10), a.Hp);
        a.Poison = 1;
        core.Explode(GlekometCore.Khrin, 530, 100, -1);
        Assert.Equal(3, a.Poison);                                 // перезапис, а не сума

        var h = Table(2);
        Ready(h);
        var hut = Core(h).Huts[1];
        hut.Poison = 3;
        hut.PoisonBy = 0;
        var hp = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            SkipTurn(h);                  // Оля
            hp.Add(hut.Hp);
            if (i < 3) Assert.Contains("Хрін дошкуляє: Петро −8", V(h).GetProperty("log").EnumerateArray().Select(e => e.GetString()));
            ShootAway(h);                 // Петро
        }
        Assert.Equal([92, 84, 76, 76], hp);
        Assert.Equal(0, hut.Poison);
        Assert.Equal(24, V(h).GetProperty("stats")[0].GetProperty("dmg").GetInt32());
    }

    [Fact]
    public void Khrin_can_finish_a_hut_on_its_turn()
    {
        var h = Table(3);
        Ready(h);
        var hut = Core(h).Huts[1];
        hut.Hp = 5;
        hut.Poison = 2;
        hut.PoisonBy = 2;
        SkipTurn(h);
        Assert.False(hut.Alive);
        Assert.Equal("poison", hut.Reason);
        Assert.Equal(2, Game(h).Turn);
        Assert.Contains("Петро: хрін добив хату", V(h).GetProperty("log").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(1, V(h).GetProperty("stats")[2].GetProperty("kills").GetInt32());
    }

    [Fact]
    public void Water_rises_from_the_chosen_round_by_fifteen_and_drowns_low_huts()
    {
        var h = Table(3, options: new { water = "6" });
        Ready(h);
        while (Game(h).Round < 5) ShootAway(h);
        Assert.Equal(20, Core(h).Water);
        var low = Core(h).Huts[2];
        for (var c = GlekometCore.Col(low.X - 24); c <= GlekometCore.Col(low.X + 24); c++) Core(h).H[c] = 30;
        Core(h).Settle();
        Assert.True(low.Alive);
        var mark = h.Outbox.Count;
        while (Game(h).Round < 6) ShootAway(h);
        Assert.Equal(35, Core(h).Water);
        Assert.Equal(35, V(h).GetProperty("water").GetInt32());
        Assert.False(low.Alive);
        Assert.Equal("drown", low.Reason);
        Assert.Contains(Frames(h, mark), f => Views.Has(f, "wl") && f.GetProperty("wl").GetInt32() == 35);
        Assert.Contains("Ганна: хату затопило", V(h).GetProperty("log").EnumerateArray().Select(e => e.GetString()));
        while (Game(h).Round < 7 && h.Room.Status == RoomStatus.Playing) ShootAway(h);
        Assert.Equal(50, Core(h).Water);

        var dry = Table(2, options: new { water = "0" });
        Ready(dry);
        while (Game(dry).Round < 9) ShootAway(dry);
        Assert.Equal(20, Core(dry).Water);
    }

    [Fact]
    public void A_hut_hit_to_zero_becomes_ruins_and_the_killer_gets_the_kill()
    {
        var h = Table(3);
        Ready(h);
        Flatten(h, 100, (0, 200), (1, 600), (2, 900));
        Core(h).Huts[1].Hp = 10;
        HitHut(h, 0, 1);
        var hut = V(h).GetProperty("huts")[1];
        Assert.False(hut.GetProperty("alive").GetBoolean());
        Assert.Equal("hit", hut.GetProperty("reason").GetString());
        Assert.Equal(0, hut.GetProperty("hp").GetInt32());
        var st = V(h).GetProperty("stats")[0];
        Assert.Equal(1, st.GetProperty("kills").GetInt32());
        Assert.Equal(10, st.GetProperty("dmg").GetInt32());
        Assert.Equal(1, st.GetProperty("hits").GetInt32());
        Assert.Contains("Петро: хата в руїнах (Оля)", V(h).GetProperty("log").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void Self_hits_count_as_self_damage_not_as_hits()
    {
        var h = Table(2);
        Ready(h);
        Flatten(h, 100, (0, 300), (1, 800));
        Core(h).Wind = 0;
        Assert.True(Fire(h, 0, 0, 5).Ok);
        Settle(h);
        var st = V(h).GetProperty("stats")[0];
        Assert.Equal(35, st.GetProperty("self").GetInt32());
        Assert.Equal(0, st.GetProperty("hits").GetInt32());
        Assert.Equal(0, st.GetProperty("dmg").GetInt32());
        Assert.Equal(65, Core(h).Huts[0].Hp);
        Assert.Contains("(у свою хату!)", V(h).GetProperty("last").GetProperty("text").GetString());
    }

    // =============================================================================================
    // Кінець партії, вихід, рематч, команди
    // =============================================================================================

    [Fact]
    public void Last_hut_standing_wins_and_the_journal_names_the_winner_first()
    {
        var h = Table(3);
        Ready(h);
        Flatten(h, 100, (0, 200), (1, 500), (2, 850));
        Core(h).Kill(2, "hit");           // Ганна вибула першою
        Core(h).Huts[1].Hp = 20;
        HitHut(h, 0, 1);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Equal("Глекомети: Оля — остання хата в селі · Петро, Ганна — руїни", h.Room.Result.Text);
        Assert.Equal(20, h.Room.Result.Scores![0]);
        Assert.Equal(0, h.Room.Result.Scores[1]);
        var result = V(h).GetProperty("result");
        Assert.Equal("last", result.GetProperty("reason").GetString());
        Assert.Equal(0, result.GetProperty("best").GetProperty("seat").GetInt32());
        Assert.Equal(1, result.GetProperty("best").GetProperty("to").GetInt32());
        Assert.Equal("over", V(h).GetProperty("phase").GetString());
        Assert.Single(h.Finished);
    }

    [Fact]
    public void Everyone_drowning_at_once_is_a_draw()
    {
        var h = Table(2, options: new { water = "6" });
        Ready(h);
        while (Game(h).Round < 5) ShootAway(h);
        var core = Core(h);
        foreach (var hut in core.Huts.Where(x => x.Alive))
            for (var c = GlekometCore.Col(hut.X - 24); c <= GlekometCore.Col(hut.X + 24); c++) core.H[c] = 25;
        core.Settle();
        while (h.Room.Status == RoomStatus.Playing) ShootAway(h);
        Assert.True(h.Room.Result!.Draw);
        Assert.Equal("Глекомети: усі хати в руїнах — нічия", h.Room.Result.Text);
    }

    [Fact]
    public void Teams_option_pairs_even_and_odd_seats_and_the_team_wins_together()
    {
        var h = Table(4, options: new { teams = "teams" });
        Assert.True(V(h).GetProperty("teams").GetBoolean());
        Assert.Equal([0, 1, 0, 1], V(h).GetProperty("huts").EnumerateArray().Take(4).Select(x => x.GetProperty("team").GetInt32()));
        Ready(h);
        Core(h).Kill(1, "hit");
        Core(h).Kill(2, "hit");           // з парних вибула Ганна, але Оля ще стоїть
        SkipTurn(h);                      // Оля
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(3, Game(h).Turn);
        ShootAway(h);                     // Іван
        Core(h).Kill(3, "hit");
        SkipTurn(h);                      // Оля — і перевірка кінця
        Assert.Equal([0, 2], h.Room.Result!.Winners);
        Assert.StartsWith("Глекомети: Оля + Ганна — останні хати в селі · ", h.Room.Result.Text);
        Assert.Equal("team", V(h).GetProperty("result").GetProperty("reason").GetString());
    }

    [Fact]
    public void In_teams_hitting_an_ally_is_counted_as_own_damage()
    {
        var h = Table(4, options: new { teams = "teams" });
        Ready(h);
        Flatten(h, 100, (0, 150), (1, 950), (2, 600), (3, 380));
        HitHut(h, 0, 2);
        var st = V(h).GetProperty("stats")[0];
        Assert.Equal(0, st.GetProperty("dmg").GetInt32());
        Assert.True(st.GetProperty("self").GetInt32() > 0);
    }

    [Fact]
    public void Teams_option_falls_back_to_free_for_all_on_odd_tables()
    {
        var h = Table(3, options: new { teams = "teams" });
        Assert.False(V(h).GetProperty("teams").GetBoolean());
        Assert.Contains("Команди — лише парним складом: граємо кожен за себе", V(h).GetProperty("log").EnumerateArray().Select(e => e.GetString()));
        Assert.False(Game(h).Teams);
    }

    [Fact]
    public void Leaving_mid_match_ruins_the_hut_and_the_game_goes_on_for_three()
    {
        var h = Table(3);
        Ready(h);
        Assert.Equal(0, Game(h).Turn);
        h.Leave("Оля");                                  // пішла у свій хід
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("left", Core(h).Huts[0].Reason);
        Assert.Equal(Glekomet.PhaseSettle, Phase(h));
        Assert.Equal("Оля: за столом нема, хата порожня", V(h).GetProperty("last").GetProperty("text").GetString());
        Settle(h);
        Assert.Equal(1, Game(h).Turn);
        Assert.Contains("Оля: за столом нема, хата порожня", V(h).GetProperty("log").EnumerateArray().Select(e => e.GetString()));
        // каркас ніка того, хто встав, уже не знає — руїну підписує вид
        Assert.Equal("Оля", V(h).GetProperty("huts")[0].GetProperty("nick").GetString());

        h.Leave("Ганна");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Room.Result!.Winners);
        Assert.Equal("Глекомети: Ганна — за столом нема, партію не дограли", h.Room.Result.Text);
        Assert.Equal("left", V(h).GetProperty("result").GetProperty("reason").GetString());
    }

    [Fact]
    public void Leaving_on_two_is_a_technical_defeat()
    {
        var h = Table(2);
        Ready(h);
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
    }

    [Fact]
    public void Rematch_gives_a_fresh_map_and_stock_but_keeps_the_win_streak_by_nick()
    {
        var h = Table(2);
        Ready(h);
        var map = V(h).GetProperty("h").GetRawText();
        Flatten(h, 100, (0, 200), (1, 700));
        Core(h).Huts[1].Hp = 10;
        Assert.True(Fire(h, 0, 135, 100, GlekometCore.Varenyk).Ok);   // вареник у небо — запас списано
        Settle(h);
        HitHut(h, 1, 0);                                                 // Петро чіпляє Олю
        HitHut(h, 0, 1);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal(1, V(h).GetProperty("wins")[0].GetInt32());

        Assert.True(h.Rematch().Ok, h.Reply.Message);
        Assert.Equal("Оля", h.Room.Seats[1]);                            // місця обернулись
        var v = V(h);
        Assert.Equal("start", v.GetProperty("phase").GetString());
        Assert.NotEqual(map, v.GetProperty("h").GetRawText());
        Assert.Equal(1, v.GetProperty("wins")[1].GetInt32());
        Assert.Equal(0, v.GetProperty("wins")[0].GetInt32());
        Assert.All(v.GetProperty("huts").EnumerateArray().Take(2), x =>
        {
            Assert.Equal(100, x.GetProperty("hp").GetInt32());
            Assert.Equal(120, x.GetProperty("fuel").GetInt32());
            Assert.True(x.GetProperty("alive").GetBoolean());
        });
        Assert.Equal([-1, 2, 1, 2, 1, 2], v.GetProperty("inv")[1].EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(20, v.GetProperty("water").GetInt32());
        Assert.Equal(0, v.GetProperty("stats")[1].GetProperty("shots").GetInt32());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("result").ValueKind);
    }

    [Fact]
    public void A_new_crew_starts_the_streak_from_zero()
    {
        var h = Table(2);
        Ready(h);
        h.Leave("Петро");
        Assert.Equal(1, V(h).GetProperty("wins")[0].GetInt32());
        h.Join("Марта");                  // стіл відкрився наново — новий склад
        Assert.True(h.Start().Ok, h.Reply.Message);
        Assert.All(V(h).GetProperty("wins").EnumerateArray(), x => Assert.Equal(0, x.GetInt32()));
    }

    [Fact]
    public void Sniper_and_clean_achievements_are_requested_through_awards()
    {
        var h = Table(2);
        Ready(h);
        Flatten(h, 100, (0, 100), (1, 700));
        Core(h).Huts[1].Hp = 30;
        HitHut(h, 0, 1);
        Assert.Contains(h.Awards, a => a.Reason == "ach:glekomet-sniper" && a.Nick == "Оля" && a.Shards == 0);
        Assert.Contains(h.Awards, a => a.Reason == "ach:glekomet-clean" && a.Nick == "Оля");
        Assert.DoesNotContain(h.Awards, a => a.Nick == "Петро");
    }

    [Fact]
    public void A_table_everyone_left_gives_the_win_but_not_the_clean_achievement()
    {
        var h = Table(3);
        Ready(h);
        h.Leave("Петро");
        h.Leave("Ганна");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Equal(100, Core(h).Huts[0].Hp);
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:glekomet-clean");
    }

    [Fact]
    public void A_close_hit_is_no_sniper_shot()
    {
        var h = Table(3);
        Ready(h);
        Flatten(h, 100, (0, 300), (1, 600), (2, 950));
        HitHut(h, 0, 1);
        Assert.True(Core(h).ShotDmg[1] > 0);
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:glekomet-sniper");
    }

    [Fact]
    public void Round_forty_is_a_safety_net_draw()
    {
        var h = Table(2);
        Game(h).MaxRounds = 3;
        Ready(h);
        while (h.Room.Status == RoomStatus.Playing) ShootAway(h);
        Assert.True(h.Room.Result!.Draw);
        Assert.Equal("Глекомети: село стоїть, порох скінчився — нічия", h.Room.Result.Text);
        Assert.Equal(40, new Glekomet().MaxRounds);
    }

    // =============================================================================================
    // Вид, кадр, детермінізм, перф
    // =============================================================================================

    [Fact]
    public void The_view_has_the_documented_shape_and_is_identical_for_seats_and_watchers()
    {
        var h = Table(3);
        Ready(h);
        var v = V(h);
        foreach (var key in new[] { "phase", "turn", "round", "endsAt", "turnMs", "startIn", "wind", "water", "waterFrom", "w", "hgt", "step",
                     "h", "teams", "huts", "inv", "aim", "shells", "last", "log", "stats", "wins", "result", "turnNo" })
            Assert.True(Views.Has(v, key), key);
        Assert.Equal((1000, 500, 4), (v.GetProperty("w").GetInt32(), v.GetProperty("hgt").GetInt32(), v.GetProperty("step").GetInt32()));
        Assert.Equal(6, v.GetProperty("huts").GetArrayLength());
        Assert.Equal(6, v.GetProperty("inv").GetArrayLength());
        Assert.Equal(6, v.GetProperty("stats").GetArrayLength());
        var hut = v.GetProperty("huts")[0];
        foreach (var key in new[] { "seat", "nick", "x", "y", "hp", "alive", "team", "poison", "fuel", "skips", "reason" })
            Assert.True(Views.Has(hut, key), key);
        var empty = v.GetProperty("huts")[5];
        Assert.False(empty.GetProperty("alive").GetBoolean());
        Assert.Equal(0, empty.GetProperty("hp").GetInt32());
        Assert.Equal("", empty.GetProperty("reason").GetString());
        Assert.Equal([-1, 2, 1, 2, 1, 2], v.GetProperty("inv")[0].EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(3, v.GetProperty("aim").GetArrayLength());
        foreach (var key in new[] { "shots", "hits", "dmg", "kills", "self", "best" })
            Assert.True(Views.Has(v.GetProperty("stats")[0], key), key);

        var spectator = Views.Text(Game(h).View(null));
        Assert.Equal(spectator, Views.Text(Game(h).View(0)));
        Assert.Equal(spectator, Views.Text(Game(h).View(2)));
    }

    [Fact]
    public void An_idle_aim_tick_sends_nothing_and_a_shot_sends_frames_then_views()
    {
        var h = Table(2);
        Ready(h);
        h.Tick(1);
        var mark = h.Outbox.Count;
        h.Tick(20);
        Assert.DoesNotContain(h.Outbox.Skip(mark), o => o is RoomFrame or RoomViews);

        Assert.True(Fire(h, 0, Away(h, 0), 100).Ok);
        Assert.DoesNotContain(h.Outbox.Skip(mark), o => o is RoomViews);   // Act у реалтаймі видів не шле
        mark = h.Outbox.Count;
        h.Tick(1);
        var first = h.Outbox.Skip(mark).ToList();
        Assert.Contains(first, o => o is RoomFrame);
        Assert.Contains(first, o => o is RoomViews);
        mark = h.Outbox.Count;
        h.Tick(1);
        var second = h.Outbox.Skip(mark).ToList();
        Assert.Contains(second, o => o is RoomFrame);
        Assert.DoesNotContain(second, o => o is RoomViews);
        mark = h.Outbox.Count;
        for (var i = 0; i < 300 && Phase(h) == Glekomet.PhaseFly; i++) h.Tick(1);
        Assert.Contains(h.Outbox.Skip(mark), o => o is RoomViews);
        Assert.Equal(Glekomet.PhaseSettle, Phase(h));
        mark = h.Outbox.Count;
        h.Tick(5);
        Assert.DoesNotContain(h.Outbox.Skip(mark), o => o is RoomFrame or RoomViews);
    }

    [Fact]
    public void Frames_carry_only_what_changed_and_stay_small()
    {
        var h = Table(6);
        Ready(h);
        Flatten(h, 150, (0, 100), (1, 380), (2, 460), (3, 540), (4, 620), (5, 900));
        Core(h).Wind = 0;
        var mark = h.Outbox.Count;
        var p = PowerFor(Core(h), 0, 60, 500, GlekometCore.Shards);
        Assert.True(Fire(h, 0, 60, p, GlekometCore.Shards).Ok);
        for (var i = 0; i < 300 && Phase(h) == Glekomet.PhaseFly; i++) h.Tick(1);
        var frames = Frames(h, mark);
        var texts = h.Outbox.Skip(mark).OfType<RoomFrame>().Select(f => Views.Text(f.Frame)).ToList();
        var flying = frames.Where(f => Views.Has(f, "sh") && !Views.Has(f, "ex")).ToList();
        Assert.NotEmpty(flying);
        Assert.All(flying, f => Assert.False(Views.Has(f, "dh") || Views.Has(f, "hp") || Views.Has(f, "aim") || Views.Has(f, "si")));
        var booms = frames.Where(f => Views.Has(f, "ex")).ToList();
        Assert.NotEmpty(booms);
        // Скалка, що влучила в стіну хати, рве повітря над землею (вирва 14 до ґрунту не дістає) — тоді dh нема.
        Assert.Contains(booms, f => Views.Has(f, "dh"));
        Assert.Contains(frames, f => Views.Has(f, "hp"));
        // Клієнт малює вибух із кадру, тож здоров'я не може змінитись без вибуху в тому самому кадрі.
        Assert.All(frames.Where(f => Views.Has(f, "hp")), f => Assert.True(Views.Has(f, "ex")));
        Assert.Contains(frames, f => f.GetProperty("sh").GetArrayLength() == 4);
        Assert.All(texts, t => Assert.True(t.Length < 1500, $"{t.Length}: {t}"));
        Assert.True(texts.Where(t => !t.Contains("\"ex\"")).Max(t => t.Length) < 300);
        Assert.True(Views.Text(Game(h).View(null)).Length < 4096);
    }

    [Fact]
    public void The_largest_frame_with_four_blasts_stays_under_budget()
    {
        var h = Table(6);
        Ready(h);
        var core = Flatten(h, 150, (0, 100), (1, 300), (2, 500), (3, 700), (4, 850), (5, 950));
        core.ClearMarks();
        core.BeginShot(0, GlekometCore.Shards);
        for (var i = 0; i < 4; i++) core.Explode(GlekometCore.Shards, 260 + i * 140, 150, -1);
        for (var i = 0; i < 4; i++) core.Spawn(GlekometCore.Shards, 100 + i * 100, 480, 10, 10, true);
        core.MovedMask = 0b111111;
        core.HpChanged = true;
        var text = Views.Text(Game(h).Frame());
        output.WriteLine($"Глекомети: найбільший кадр (4 скалки + 4 вибухи + 6 хат + здоров'я) — {text.Length} Б");
        Assert.True(text.Length < 1500, $"{text.Length}: {text}");
    }

    [Fact]
    public void Height_deltas_rebuild_the_full_height_map_exactly()
    {
        var h = Table(4, seed: 11);
        Ready(h);
        var map = V(h).GetProperty("h").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        var rng = new Random(5);
        for (var shot = 0; shot < 8 && h.Room.Status == RoomStatus.Playing; shot++)
        {
            var mark = h.Outbox.Count;
            var s = Game(h).Turn;
            Assert.True(Fire(h, s, rng.Next(20, 160), rng.Next(30, 80), rng.Next(0, 3) == 0 ? GlekometCore.Varenyk : 0).Ok || Fire(h, s, 60, 50, 0).Ok);
            for (var i = 0; i < 300 && Phase(h) == Glekomet.PhaseFly; i++) h.Tick(1);
            foreach (var f in Frames(h, mark).Where(f => Views.Has(f, "dh")))
                foreach (var run in f.GetProperty("dh").EnumerateArray())
                {
                    var c0 = run[0].GetInt32();
                    for (var k = 1; k < run.GetArrayLength(); k++) map[c0 + k - 1] = run[k].GetInt32();
                }
            Assert.Equal(V(h).GetProperty("h").EnumerateArray().Select(e => e.GetInt32()), map);
            Settle(h);
        }
    }

    [Fact]
    public void The_same_seed_and_inputs_replay_to_the_same_view()
    {
        foreach (var seed in new[] { 3, 77 })
        {
            string Play()
            {
                var h = Table(4, seed);
                var rng = new Random(seed * 13);
                for (var t = 0; t < 400 && h.Room.Status == RoomStatus.Playing; t++)
                {
                    if (Phase(h) == Glekomet.PhaseAim && rng.Next(8) == 0)
                    {
                        var s = Game(h).Turn;
                        switch (rng.Next(4))
                        {
                            case 0: h.Act(s, "move", rng.Next(2) == 0 ? -1 : 1); break;
                            case 1: h.Input(s, "aim", new { a = rng.Next(181), p = rng.Next(5, 101), w = rng.Next(6) }); break;
                            default: Fire(h, s, rng.Next(10, 171), rng.Next(20, 101), rng.Next(6)); break;
                        }
                    }
                    h.Tick(1);
                }
                return Views.Text(Game(h).View(null));
            }
            Assert.Equal(Play(), Play());
        }
    }

    [Fact]
    public void The_flight_core_allocates_nothing()
    {
        var core = Bare(150);
        core.Place(0, 100);
        core.Place(1, 500);
        core.Place(2, 900);
        void Burst(int n)
        {
            for (var i = 0; i < n; i++)
            {
                if (core.LiveShells == 0)
                {
                    core.Flat(150);
                    core.Place(0, 100); core.Place(1, 500); core.Place(2, 900);
                    core.Fire(0, 30 + i % 60, 40 + i % 50, i % 3 == 0 ? GlekometCore.Shards : i % 3 == 1 ? GlekometCore.Varenyk : GlekometCore.Hay, i % 11 - 5);
                }
                core.ClearMarks();
                core.Step();
            }
        }
        Burst(400);                                     // прогрів JIT
        var before = GC.GetAllocatedBytesForCurrentThread();
        Burst(1000);
        var spent = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(spent < 1024, $"{spent} байт");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Perf_three_thousand_ticks_of_six_huts_firing_nonstop_stay_under_a_second()
    {
        var best = double.MaxValue;
        long frames = 0, bytes = 0, biggest = 0, view = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var h = Table(6, seed: 17 + attempt);
            var game = Game(h);
            var rng = new Random(attempt);
            Ready(h);
            var sw = new Stopwatch();
            for (var t = 0; t < 3000; t++)
            {
                if (h.Room.Status != RoomStatus.Playing) { h.Rematch(); continue; }
                if (game.Phase == Glekomet.PhaseAim)
                {
                    var s = game.Turn;
                    var w = h.View(null).GetProperty("inv")[s][1].GetInt32() > 0 && rng.Next(2) == 0 ? 1 : 0;
                    h.Act(s, "fire", new { a = rng.Next(20, 161), p = rng.Next(40, 101), w });
                }
                var mark = h.Outbox.Count;
                sw.Start();
                h.Tick(1);
                sw.Stop();
                if (attempt == 0)
                    foreach (var f in h.Outbox.Skip(mark).OfType<RoomFrame>())
                    {
                        var len = Views.Text(f.Frame).Length;
                        frames++;
                        bytes += len;
                        biggest = Math.Max(biggest, len);
                    }
            }
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            view = Math.Max(view, Views.Text(game.View(null)).Length);
        }
        output.WriteLine($"Глекомети: 3000 тиків на шістьох — {best:F1} мс ({best / 3000:F4} мс/тик); кадрів {frames}, у середньому {bytes / Math.Max(1, frames)} Б, найбільший {biggest} Б; вид до {view} Б");
        Assert.True(best < 1000, $"{best} мс");
        Assert.True(best / 3000 < 0.25, $"{best / 3000} мс/тик");
    }
}
