using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Стрибозаври як партія: раунди, місця, очки, сніжки, ачівки, вихід, рематч, вид і кадр — через справжню
/// кімнату. Щоб раунд скінчився, коли треба, тест просто штовхає бігуна до лавини (Lag), а щоб усі дожили
/// до стелі — прибирає курс.
/// </summary>
[Collection(SerialPerf.Name)]
public class DinoTests
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марта", "Богдан", "Ліна", "Тарас"];
    const int U = RunnerSim.Sub;

    static RoomHarness Table(int players = 2, int seed = 42, object? options = null)
    {
        var h = new RoomHarness("dino", options, seed);
        foreach (var nick in Nicks.Take(players)) h.Join(nick);
        Assert.True(h.Start().Ok);
        return h;
    }

    static Dino Game(RoomHarness h) => (Dino)h.Room.Game;

    static RunnerSim Sim(RoomHarness h) => Game(h).World!;

    static string Ph(RoomHarness h) => h.View(null).GetProperty("ph").GetString()!;

    static void ToRun(RoomHarness h)
    {
        for (var i = 0; i < 200 && Ph(h) == "ready"; i++) h.Tick();
        Assert.Equal("run", Ph(h));
    }

    /// <summary>Штовхнути місця до лавини: на наступному кроці вони вибувають.</summary>
    static void Catch(RoomHarness h, params int[] seats)
    {
        foreach (var s in seats) Sim(h).P[s].Lag = 20000;
        h.Tick();
    }

    /// <summary>Дограти раунд: усі, крім winner, вибувають (по одному, щоб місця йшли підряд).</summary>
    static void Win(RoomHarness h, int winner)
    {
        ToRun(h);
        var order = Enumerable.Range(0, 8).Where(s => s != winner && Sim(h).P[s].Plays && !Sim(h).P[s].Out).ToList();
        foreach (var s in order) Catch(h, s);
        Assert.Equal("over", Ph(h));
    }

    static void ThroughOver(RoomHarness h)
    {
        for (var i = 0; i < 200 && Ph(h) == "over"; i++) h.Tick();
    }

    static int[] Ints(JsonElement e, string name) => e.GetProperty(name).EnumerateArray().Select(x => x.GetInt32()).ToArray();

    static bool[] Bools(JsonElement e, string name) => e.GetProperty(name).EnumerateArray().Select(x => x.GetBoolean()).ToArray();

    // ---------- паспорт ----------

    [Fact]
    public void Passport_is_live_one_to_eight_by_host_with_client_runner_and_two_options()
    {
        var info = new Dino().Info;
        Assert.Equal("dino", info.Id);
        Assert.Equal("Стрибозаври", info.Title);
        Assert.Equal(GameGroup.Live, info.Group);
        Assert.Equal((1, 8), (info.MinPlayers, info.MaxPlayers));
        Assert.Equal(40, info.TickMs);
        Assert.Equal(StartMode.ByHost, info.Start);
        Assert.Equal("runner", info.Module);
        Assert.False(info.Hidden || info.Private || info.Persistent || info.Rated);
        Assert.Equal(ScoreOrder.None, info.Score);
        Assert.Equal(["rounds", "snow"], info.Options!.Select(o => o.Key));
        Assert.Equal("3", info.Options![0].Default);
        Assert.True(File.Exists(Path.Combine(FindRoot(), "web", "games", "runner.js")));
    }

    internal static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "web", "games"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("корінь не знайдено");
    }

    [Fact]
    public void Seat_names_cover_eight_seats_and_differ()
    {
        var g = new Dino();
        var names = Enumerable.Range(0, 8).Select(g.SeatName).ToList();
        Assert.Equal(8, names.Distinct().Count());
        Assert.Equal("зелений", names[0]);
        Assert.Equal("червоний", names[7]);
    }

    // ---------- старт і відлік ----------

    [Fact]
    public void The_lobby_view_has_no_seed_and_the_first_start_draws_one_from_the_room_rng()
    {
        var h = new RoomHarness("dino", seed: 42);
        h.Join("Оля");
        h.Join("Петро");
        var lobby = h.View(null);
        Assert.Equal("lobby", lobby.GetProperty("ph").GetString());
        Assert.Equal(0, lobby.GetProperty("seed").GetInt32());
        Assert.Equal([true, true, false, false, false, false, false, false], Bools(lobby, "plays"));
        Assert.Equal(JsonValueKind.Array, lobby.GetProperty("p")[1].ValueKind);
        Assert.Equal(JsonValueKind.Null, lobby.GetProperty("p")[2].ValueKind);
        h.Start();
        Assert.Equal(new Random(42).Next(1, int.MaxValue), h.View(null).GetProperty("seed").GetInt32());
    }

    [Fact]
    public void Ready_lasts_150_steps_and_the_world_does_not_move()
    {
        var h = Table(2);
        h.Tick(74);
        var v = h.View(null);
        Assert.Equal("ready", v.GetProperty("ph").GetString());
        Assert.Equal(148, v.GetProperty("s").GetInt32());
        Assert.Equal(0, Sim(h).P[0].Lag);
        Assert.Equal(0, Sim(h).Run);
        h.Tick();
        Assert.Equal("run", Ph(h));
        Assert.Equal(150, Sim(h).S);
        h.Tick();
        Assert.True(Sim(h).PaceX(Sim(h).Run) > 0);
    }

    [Fact]
    public void Slow_ticks_do_not_slow_the_race_down()
    {
        // Кімната тикає «не раніше ніж за 40 мс», а насправді — за ~50: кроків на тик тоді більше, ніж два.
        var h = Table(2);
        for (var i = 0; i < 60; i++)
        {
            h.Clock.AdvanceMs(10);
            h.Tick();
        }
        Assert.Equal(149, Sim(h).S);          // перший тик — два кроки, далі 59 × 50 мс = 147 кроків (і 10 мс у запасі)
        Assert.Equal("ready", Ph(h));
        h.Clock.AdvanceMs(10);
        h.Tick();
        Assert.Equal(152, Sim(h).S);          // 50 мс + 10 мс запасу = три кроки: відлік (3 с) скінчився вчасно
        Assert.Equal("run", Ph(h));
    }

    [Fact]
    public void An_edge_before_the_run_does_not_jump_at_the_start()
    {
        var h = Table(2);
        h.Tick(10);
        h.Input(0, "in", new { s = Sim(h).S, k = 5 });
        ToRun(h);
        for (var i = 0; i < 10; i++)
        {
            h.Tick();
            Assert.False(Sim(h).P[0].Air);
        }
    }

    // ---------- вибування, місця, очки ----------

    [Fact]
    public void A_runner_caught_by_the_avalanche_is_out_and_the_view_says_so()
    {
        var h = Table(3);
        ToRun(h);
        var before = h.Outbox.Count;
        Catch(h, 2);
        Assert.Contains(h.Outbox.Skip(before), o => o is RoomViews);
        var v = h.View(null);
        Assert.Equal([true, true, false, false, false, false, false, false], Bools(v, "alive"));
        Assert.Equal(3, Ints(v, "place")[2]);
        Assert.Equal(4, v.GetProperty("p")[2][3].GetInt32());
        Assert.Equal("run", Ph(h));
    }

    [Fact]
    public void Two_runners_caught_on_the_same_step_share_the_place()
    {
        var h = Table(4);
        ToRun(h);
        Catch(h, 1, 2);
        var place = Ints(h.View(null), "place");
        Assert.Equal(3, place[1]);
        Assert.Equal(3, place[2]);
        Assert.Equal("run", Ph(h));
    }

    [Fact]
    public void With_two_players_the_round_ends_when_one_is_out()
    {
        var h = Table(2);
        ToRun(h);
        Catch(h, 1);
        var v = h.View(null);
        Assert.Equal("over", v.GetProperty("ph").GetString());
        Assert.Equal([1, 2], Ints(v, "place").Take(2));
        Assert.Equal([6, 3], Ints(v, "points").Take(2));
    }

    [Fact]
    public void The_round_cap_ranks_survivors_by_lag()
    {
        var h = Table(3, options: new { rounds = "1" });
        ToRun(h);
        var sim = Sim(h);
        sim.ClearCourse();
        sim.P[1].Lag = -500;       // попереду темпу — і таким лишиться
        h.Tick(3010);
        var v = h.View(null);
        Assert.Equal("over", v.GetProperty("ph").GetString());
        Assert.True(sim.S - RunnerParty.ReadySteps >= RunnerParty.RoundCap);
        Assert.Equal([2, 1, 2], Ints(v, "place").Take(3));
        Assert.Equal([6, 9, 6], Ints(v, "points").Take(3));
    }

    [Fact]
    public void Points_are_three_per_place_rank_plus_one_per_egg()
    {
        var h = Table(3);
        ToRun(h);
        Sim(h).P[0].Eggs = 4;
        Catch(h, 0);
        Catch(h, 1);
        var v = h.View(null);
        Assert.Equal("over", v.GetProperty("ph").GetString());
        Assert.Equal([3, 2, 1], Ints(v, "place").Take(3));
        Assert.Equal([7, 6, 9], Ints(v, "points").Take(3));
        Assert.Equal([7, 6, 9], v.GetProperty("roundPoints")[0].EnumerateArray().Take(3).Select(x => x.GetInt32()));
    }

    [Fact]
    public void Three_rounds_then_finish_with_the_winner_first_in_the_journal()
    {
        var h = Table(3);
        for (var round = 1; round <= 3; round++)
        {
            Assert.Equal(round, h.View(null).GetProperty("round").GetInt32());
            // Оля перемагає, Петро другий: спершу вибуває Ганна
            ToRun(h);
            Catch(h, 2);
            Catch(h, 1);
            ThroughOver(h);
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("Стрибозаври: Оля 27 · Петро 18 · Ганна 9", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Equal([0], h.Finished.Single().Result.Winners);
        var result = h.View(null).GetProperty("result");
        Assert.Equal([0], Ints(result, "winners"));
        Assert.Equal(27, result.GetProperty("table")[0].GetProperty("points").GetInt32());
        Assert.Equal("done", Ph(h));
    }

    [Fact]
    public void Tied_leaders_are_all_winners()
    {
        var h = Table(2, options: new { rounds = "1" });
        ToRun(h);
        Catch(h, 0, 1);                          // обох одним кроком — обидва перші
        ThroughOver(h);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0, 1], h.Finished.Single().Result.Winners);
        Assert.False(h.Finished.Single().Result.Draw);
    }

    [Fact]
    public void One_round_option_and_five_round_option_are_honoured()
    {
        var one = Table(2, options: new { rounds = "1" });
        Win(one, 0);
        ThroughOver(one);
        Assert.Equal(RoomStatus.Finished, one.Room.Status);

        var five = Table(2, options: new { rounds = "5" });
        Assert.Equal(5, five.View(null).GetProperty("rounds").GetInt32());
        for (var r = 0; r < 4; r++) { Win(five, r % 2); ThroughOver(five); }
        Assert.Equal(RoomStatus.Playing, five.Room.Status);
        Win(five, 0);
        ThroughOver(five);
        Assert.Equal(RoomStatus.Finished, five.Room.Status);

        var junk = Table(2, options: new { rounds = "7" });
        Assert.Equal(3, junk.View(null).GetProperty("rounds").GetInt32());
    }

    // ---------- сніжки ----------

    static bool SnowballsAppear(RoomHarness h)
    {
        ToRun(h);
        var sim = Sim(h);
        for (var i = 0; i < 3000; i++)
        {
            sim.P[0].Lag = 0;
            sim.P[1].Lag = 0;
            h.Tick();
            for (var k = 0; k < sim.PickupCount; k++)
                if (sim.Pickup(k).Kind == RunnerKind.SnowBall) return true;
        }
        return false;
    }

    [Fact]
    public void Snow_off_option_turns_snowballs_into_eggs()
    {
        Assert.True(SnowballsAppear(Table(2, seed: 3)));
        var off = Table(2, seed: 3, options: new { snow = "off" });
        Assert.False(off.View(null).GetProperty("snowOpt").GetBoolean());
        Assert.False(SnowballsAppear(off));
    }

    [Fact]
    public void Throw_without_a_snowball_is_refused_with_the_right_text()
    {
        var h = Table(2);
        ToRun(h);
        var r = h.Act(0, "throw");
        Assert.False(r.Ok);
        Assert.Equal("Сніжки в тебе нема — підбери на трасі", r.Message);
        Assert.Equal(0, Sim(h).SnowCount);
    }

    [Fact]
    public void Throw_before_the_run_and_after_being_out_is_refused()
    {
        var h = Table(3);
        Sim(h).P[0].Snow = 1;
        Assert.Equal("Зачекай старту", h.Act(0, "throw").Message);
        ToRun(h);
        Sim(h).P[2].Snow = 1;
        Catch(h, 2);
        Assert.Equal("Ти вже вибув — сніжки лишились у снігу", h.Act(2, "throw").Message);
        Assert.Equal("Тут так не ходять", h.Act(0, "dance").Message);
    }

    [Fact]
    public void Throw_with_no_other_alive_runner_is_refused()
    {
        var h = Table(1);
        ToRun(h);
        Sim(h).P[0].Snow = 1;
        Assert.Equal("Кидати нема в кого", h.Act(0, "throw").Message);
        Assert.Equal(1, Sim(h).P[0].Snow);
    }

    [Fact]
    public void A_throw_targets_the_least_lagging_rival_and_says_so_in_the_table_talk()
    {
        var h = Table(3);
        ToRun(h);
        h.Tick(20);
        var sim = Sim(h);
        sim.ClearCourse();
        sim.P[1].Lag = 500;
        sim.P[2].Lag = -300;
        sim.P[0].Snow = 1;
        var r = h.Act(0, "throw");
        Assert.True(r.Ok, r.Message);
        Assert.Equal(1, sim.SnowCount);
        var block = sim.SnowBlock(0);
        Assert.Equal(sim.PaceX(sim.Run) + 300 + RunnerDino.SnowAhead, block.X);
        Assert.Equal(0, block.By);
        Assert.Equal(0, sim.P[0].Snow);
        var said = h.Outbox.OfType<TableSaid>().Last().Line.Text;
        Assert.Equal("❄ Оля кидає сніжку під ноги — Ганна, стрибай!", said);
        // у кадрі — брила з тим, хто кинув, і з кроком, з якого б'є
        h.Tick();
        var frame = Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);
        var sn = frame.GetProperty("sn")[0].EnumerateArray().Select(x => x.GetInt32()).ToArray();
        Assert.Equal([block.X, 0, block.Id, block.Since], sn);
        Assert.Contains(frame.GetProperty("ev").EnumerateArray(), e => e[0].GetString() == "throw");
    }

    [Fact]
    public void Hitting_a_thrown_block_awards_dino_snow_to_the_thrower()
    {
        var h = Table(2);
        ToRun(h);
        var sim = Sim(h);
        sim.ClearCourse();
        sim.P[0].Snow = 1;
        Assert.True(h.Act(0, "throw").Ok);
        for (var i = 0; i < 60; i++) { sim.P[0].Lag = 0; h.Tick(); }
        Assert.Equal(1, sim.P[1].Hits);
        var award = Assert.Single(h.Awards, a => a.Reason == "ach:dino-snow");
        Assert.Equal("Оля", award.Nick);
    }

    [Fact]
    public void Tripping_over_your_own_snow_block_awards_nothing()
    {
        var h = Table(2);
        ToRun(h);
        var sim = Sim(h);
        sim.ClearCourse();
        sim.P[1].Snow = 1;
        Assert.True(h.Act(1, "throw").Ok);       // ціль — Оля, але Олю прибираємо з траси: об брилу б'ється лише Петро
        sim.P[0].Plays = false;
        h.Tick(60);
        sim.P[0].Plays = true;
        Assert.True(sim.P[1].Hits >= 1);
        h.Tick(20);
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:dino-snow");
    }

    [Fact]
    public void A_late_jump_that_dodges_a_snow_block_cancels_the_award()
    {
        var h = Table(2);
        ToRun(h);
        var sim = Sim(h);
        sim.ClearCourse();
        sim.P[0].Snow = 1;
        Assert.True(h.Act(0, "throw").Ok);
        var block = sim.SnowBlock(0);
        // ідемо до брили; Петро «на сервері» спотикається, а потім приходить його стрибок з минулого
        var s0 = -1;
        for (var i = 0; i < 80 && sim.P[1].Hits == 0; i++)
        {
            sim.P[0].Lag = 0;
            var wx = sim.PaceX(sim.Run) - sim.P[1].Lag;
            if (s0 < 0 && block.X - wx < 60 * U) s0 = sim.S;
            h.Tick();
        }
        Assert.Equal(1, sim.P[1].Hits);
        h.Input(1, "in", new { s = s0, k = 5 });
        h.Input(1, "in", new { s = s0 + 1, k = 0 });
        Assert.Equal(0, sim.P[1].Hits);
        h.Tick(30);
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:dino-snow");
    }

    // ---------- ачівки й тренування ----------

    [Fact]
    public void Two_kilometres_award_dino_far_once()
    {
        var h = Table(2, options: new { rounds = "1" });
        ToRun(h);
        Sim(h).ClearCourse();
        h.Tick(3010);                      // до стелі: ≈ 3,5 км
        Assert.Equal("over", Ph(h));
        Assert.Equal(2, h.Awards.Count(a => a.Reason == "ach:dino-far"));
        Assert.Equal(["Оля", "Петро"], h.Awards.Where(a => a.Reason == "ach:dino-far").Select(a => a.Nick).Order());

        var short_ = Table(2, options: new { rounds = "1" });
        ToRun(short_);
        Catch(short_, 1);
        Assert.DoesNotContain(short_.Awards, a => a.Reason == "ach:dino-far");
    }

    [Fact]
    public void A_single_player_plays_one_round_of_training_without_journal_or_points()
    {
        var h = Table(1, options: new { rounds = "5" });
        Assert.Equal(1, h.View(null).GetProperty("rounds").GetInt32());
        ToRun(h);
        h.Tick(50);
        Catch(h, 0);
        Assert.Equal("over", Ph(h));
        ThroughOver(h);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Finished.Single().Result.Winners);
        Assert.DoesNotContain(h.Outbox.OfType<Journal>(), j => j.Text.StartsWith("Стрибозаври:"));
        Assert.All(Ints(h.View(null), "points"), p => Assert.Equal(0, p));
        Assert.True(h.View(null).GetProperty("m").GetInt32() >= 0);
    }

    // ---------- вихід і рематч ----------

    [Fact]
    public void Leaving_with_three_at_the_table_keeps_the_party_going()
    {
        var h = Table(3);
        ToRun(h);
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Contains(h.Outbox.OfType<Journal>(), j => j.Text == "Стрибозаври: Петро встав з-за столу");
        var v = h.View(null);
        Assert.False(Bools(v, "plays")[1]);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("p")[1].ValueKind);
        h.Tick(5);
        Assert.Equal("run", Ph(h));
        Catch(h, 2);                             // лишились двоє, одна вибула — раунд Олі
        Assert.Equal("over", Ph(h));
        Assert.Equal(1, Ints(h.View(null), "place")[0]);
    }

    [Fact]
    public void Leaving_with_two_at_the_table_ends_the_party_for_the_one_left()
    {
        var h = Table(2);
        ToRun(h);
        h.Leave("Оля");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Finished.Single().Result.Winners);
        Assert.Equal("Стрибозаври: Оля встав з-за столу, Петро лишився сам", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void Leaving_alone_finishes_quietly()
    {
        var h = Table(1);
        ToRun(h);
        h.Leave("Оля");
        var fin = h.Finished.Single();
        Assert.Empty(fin.Result.Winners);
        Assert.DoesNotContain(h.Outbox.OfType<Journal>(), j => j.Text.StartsWith("Стрибозаври:"));
    }

    [Fact]
    public void Rematch_rotates_seats_gives_a_new_seed_and_zero_points()
    {
        var h = Table(2, options: new { rounds = "1" });
        var seed1 = h.View(null).GetProperty("seed").GetInt32();
        Win(h, 0);
        ThroughOver(h);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(h.Rematch().Ok);
        Assert.Equal("Петро", h.NickOf(0));
        Assert.Equal("Оля", h.NickOf(1));
        var v = h.View(null);
        Assert.Equal("ready", v.GetProperty("ph").GetString());
        Assert.NotEqual(seed1, v.GetProperty("seed").GetInt32());
        Assert.All(Ints(v, "points"), p => Assert.Equal(0, p));
        Assert.Equal(1, v.GetProperty("round").GetInt32());
        Assert.Equal(0, v.GetProperty("s").GetInt32());
    }

    // ---------- дріт ----------

    [Fact]
    public void Input_payload_is_exactly_what_the_module_sends()
    {
        // рівно те, що шле runner.js: Input('in', { s, k })
        var h = Table(2);
        ToRun(h);
        h.Tick(5);
        var sim = Sim(h);
        sim.ClearCourse();
        foreach (var junk in new object?[] { null, 5, "стрибок", new { k = 5 }, new { s = "10", k = 5 }, new { s = sim.S, k = 12 }, new { s = sim.S, k = -1 }, new[] { 1, 5 } })
        {
            h.Input(0, "in", junk);
            h.Tick();
            Assert.False(sim.P[0].Air);
        }
        h.Input(0, "in", new { s = sim.S, k = 5, extra = "не заважає" });
        h.Tick();
        Assert.True(sim.P[0].Air);
        Assert.Equal("Тут так не ходять", h.Act(0, "in", new { s = "x" }).Message);
        Assert.True(h.Act(0, "in", new { s = sim.S, k = 0 }).Ok);   // і через Act теж приймається
    }

    [Fact]
    public void A_late_input_is_rewound_to_its_step()
    {
        var h = Table(2);
        ToRun(h);
        var sim = Sim(h);
        sim.ClearCourse();
        var s = sim.S;
        h.Tick(3);
        h.Input(0, "in", new { s, k = 5 });          // стрибок шість кроків тому
        Assert.True(sim.P[0].Air);
        Assert.True(sim.P[0].Y > 5 * 150);           // уже пролетів шість кроків дуги
    }

    [Fact]
    public void Ping_is_echoed_to_the_asking_seat_in_the_next_frame_only()
    {
        var h = Table(2);
        h.Tick();
        h.Input(1, "ping", new { t = 812345 });
        var before = h.Outbox.Count;
        h.Tick();
        var f = Views.Json(h.Outbox.Skip(before).OfType<RoomFrame>().Single().Frame);
        var pg = f.GetProperty("pg");
        Assert.Equal(JsonValueKind.Null, pg[0].ValueKind);
        Assert.Equal(812345, pg[1].GetInt32());
        before = h.Outbox.Count;
        h.Tick();
        Assert.False(Views.Has(Views.Json(h.Outbox.Skip(before).OfType<RoomFrame>().Single().Frame), "pg"));
        h.Input(0, "ping", new { t = 0x1234567 });
        h.Tick();
        Assert.Equal(0x1234567 & 0xFFFFF, Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame).GetProperty("pg")[0].GetInt32());
    }

    [Fact]
    public void Views_of_every_seat_and_of_the_watcher_are_identical()
    {
        var h = Table(3);
        ToRun(h);
        h.Tick(40);
        var watcher = h.View(null).GetRawText();
        for (var s = 0; s < 3; s++) Assert.Equal(watcher, h.View(s).GetRawText());
    }

    [Fact]
    public void View_stays_under_four_kilobytes_on_eight_players_after_five_rounds()
    {
        var h = Table(8, options: new { rounds = "5" });
        for (var r = 0; r < 5; r++) { Win(h, r); if (r < 4) ThroughOver(h); }
        var text = Views.Text(h.Room.Game.View(null));
        Console.WriteLine($"[size] dino 8 гравців, 5 раундів: вид {Encoding(text)} Б");
        Assert.True(Encoding(text) < 4096, $"вид {Encoding(text)} Б");
    }

    static int Encoding(string s) => System.Text.Encoding.UTF8.GetByteCount(s);

    [Fact]
    public void Frame_of_eight_runners_stays_under_one_kilobyte()
    {
        var h = Table(8);
        ToRun(h);
        int max = 0, sum = 0;
        var sim = Sim(h);
        for (var i = 0; i < 400; i++)
        {
            for (var s = 0; s < 8; s++) { sim.P[s].Lag = Math.Min(sim.P[s].Lag, 3000); sim.P[s].Eggs = 12; }
            h.Tick();
            var n = Encoding(Views.Text(h.Outbox.OfType<RoomFrame>().Last().Frame));
            max = Math.Max(max, n);
            sum += n;
        }
        Console.WriteLine($"[size] dino 8 гравців: кадр у середньому {sum / 400} Б, найбільший {max} Б");
        Assert.True(max < 1024, $"кадр {max} Б");
    }

    [Fact]
    public void Frame_allocates_fresh_arrays_each_time()
    {
        var h = Table(2);
        ToRun(h);
        var g = Game(h);
        var a = (RunnerFrame)g.Frame()!;
        var b = (RunnerFrame)g.Frame()!;
        Assert.NotSame(a.P, b.P);
        Assert.NotSame(a.P[0], b.P[0]);
    }

    [Fact]
    public void Frames_are_sent_every_tick_in_run_and_never_in_over_or_done()
    {
        var h = Table(2, options: new { rounds = "1" });
        var frames = h.Outbox.OfType<RoomFrame>().Count();
        h.Tick(10);
        Assert.Equal(frames + 10, h.Outbox.OfType<RoomFrame>().Count());
        ToRun(h);
        Catch(h, 1);
        frames = h.Outbox.OfType<RoomFrame>().Count();
        h.Tick(20);
        Assert.Equal("over", Ph(h));
        Assert.Equal(frames, h.Outbox.OfType<RoomFrame>().Count());
        ThroughOver(h);
        frames = h.Outbox.OfType<RoomFrame>().Count();
        h.Tick(20);
        Assert.Equal(frames, h.Outbox.OfType<RoomFrame>().Count());
    }

    [Fact]
    public void The_same_seed_and_inputs_give_the_same_frames()
    {
        static List<string> Play(int seed)
        {
            var h = Table(3, seed);
            var rng = new RunnerRng(9);
            var list = new List<string>();
            for (var i = 0; i < 700 && h.Room.Status == RoomStatus.Playing; i++)
            {
                if (i % 3 == 0) h.Input(i % 3, "in", new { s = Sim(h).S, k = rng.Next(8) });
                var before = h.Outbox.Count;
                h.Tick();
                list.AddRange(h.Outbox.Skip(before).OfType<RoomFrame>().Select(f => Views.Text(f.Frame)));
            }
            return list;
        }
        var a = Play(5);
        Assert.Equal(a, Play(5));
        Assert.NotEqual(a, Play(6));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Three_thousand_ticks_of_eight_runners_take_under_a_second()
    {
        var h = Table(8, seed: 11, options: new { rounds = "5" });
        ToRun(h);
        var room = h.Room;
        var rng = new RunnerRng(3);
        var held = new int[8];
        // прогрів JIT
        for (var i = 0; i < 50; i++) h.Rooms.Tick(room);
        var sw = new Stopwatch();
        var measured = 0;
        // міряємо лише тики бігу: раунд упирається в стелю (6000 кроків = 3000 тиків), тоді — наступний
        for (var guard = 0; measured < 3000 && guard < 20000; guard++)
        {
            var game = Game(h);
            var sim = game.World!;
            if (game.PhaseName != "run") { h.Rooms.Tick(room); continue; }
            for (var s = 0; s < 8; s++)
            {
                if (sim.P[s].Lag > 3000) sim.P[s].Lag = 0;      // не даємо лавині закінчити раунд посеред заміру
                if ((guard + s) % 3 == 0)
                {
                    held[s] ^= rng.Next(2) == 0 ? 1 : 2;
                    h.Rooms.Input(room.Id, room.Seats[s]!, "in", Views.Payload(new { s = sim.S, k = held[s] | ((held[s] & 1) != 0 ? 4 : 0) }));
                }
            }
            sw.Start();
            h.Rooms.Tick(room);
            sw.Stop();
            measured++;
        }
        Assert.Equal(3000, measured);
        var ms = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"[perf] dino 8 гравців: 3000 тиків за {ms:F1} мс, {ms / 3000:F4} мс на тик");
        Assert.True(ms < 1000, $"3000 тиків за {ms} мс");
    }
}
