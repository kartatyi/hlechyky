using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Забіг дня: одна криза на день для всіх, кожна спроба — окрема партія каркаса, у таблицю — метри. Лавину
/// тест підганяє сам (Lag), а щоб добігти до потрібних метрів — прибирає курс.
/// </summary>
[Collection(SerialPerf.Name)]
public class DinoDailyTests
{
    static RoomHarness Open(string nick = "Оля", RoomHarness? h = null)
    {
        h ??= new RoomHarness("dino-daily", seed: 3);
        Assert.True(h.Solo(nick).Ok);
        return h;
    }

    static DinoDaily Game(RoomHarness h) => (DinoDaily)h.Room.Game;

    static RunnerSim Sim(RoomHarness h) => Game(h).World!;

    static string Ph(RoomHarness h) => h.View(null).GetProperty("ph").GetString()!;

    static void Go(RoomHarness h)
    {
        h.Input(0, "in", new { s = 0, k = 5 });
        Assert.Equal("run", Ph(h));
    }

    /// <summary>Пробігти по чистій кризі до metres і віддатись лавині.</summary>
    static int RunTo(RoomHarness h, int metres, int eggs = 0)
    {
        Go(h);
        var sim = Sim(h);
        sim.ClearCourse();
        while (sim.PaceX(sim.S) / RunnerDino.SubPerMetre < metres) h.Tick();
        sim.P[0].Eggs = eggs;
        sim.P[0].Lag = 50000;
        h.Tick();
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        return (int)h.Scores.Last().Score;
    }

    // ---------- паспорт і ключ ----------

    [Fact]
    public void Passport_is_private_persistent_solo_with_higher_is_better_and_client_runner()
    {
        var g = new DinoDaily();
        var info = g.Info;
        Assert.Equal(("dino-daily", "Забіг дня", GameGroup.Solo), (info.Id, info.Title, info.Group));
        Assert.Equal((1, 1), (info.MinPlayers, info.MaxPlayers));
        Assert.True(info.Private && info.Persistent);
        Assert.Equal(StartMode.Immediate, info.Start);
        Assert.Equal(ScoreOrder.HigherIsBetter, info.Score);
        Assert.Equal(40, info.TickMs);
        Assert.Equal("runner", info.Module);
        Assert.False(typeof(IDailyGame).IsAssignableFrom(g.GetType()));
        Assert.Equal("стрибозавр", g.SeatName(0));
    }

    [Fact]
    public void Solo_key_has_the_kyiv_day_and_no_daily_prefix()
    {
        var clock = new FakeClock { UtcNow = new DateTimeOffset(2026, 9, 27, 21, 30, 0, TimeSpan.Zero) };   // 00:30 за Києвом
        Assert.Equal("dino-daily:2026-09-28:оля", new DinoDaily().SoloKey("оля", clock));
        clock.UtcNow = new DateTimeOffset(2026, 9, 27, 20, 30, 0, TimeSpan.Zero);
        Assert.Equal("dino-daily:2026-09-27:оля", new DinoDaily().SoloKey("оля", clock));
        var h = Open();
        Assert.Equal("dino-daily:2026-09-10:оля", h.Room.Key);
    }

    [Fact]
    public void Two_rooms_of_the_same_day_share_the_seed_and_different_days_do_not()
    {
        var a = Open("Оля");
        var b = Open("Петро");
        var seed = a.View(null).GetProperty("seed").GetInt32();
        Assert.Equal(Days.Seed("dino-daily", "2026-09-10"), seed);
        Assert.Equal(seed, b.View(null).GetProperty("seed").GetInt32());
        var c = new RoomHarness("dino-daily", seed: 3);
        c.Clock.Advance(TimeSpan.FromDays(1));
        Open("Оля", c);
        Assert.NotEqual(seed, c.View(null).GetProperty("seed").GetInt32());
        Assert.Equal(Days.Seed("dino-daily", "2026-09-11"), c.View(null).GetProperty("seed").GetInt32());
    }

    // ---------- старт ----------

    [Fact]
    public void The_room_waits_with_no_frames_until_the_first_jump_edge()
    {
        var h = Open();
        var frames = h.Outbox.OfType<RoomFrame>().Count();
        h.Tick(25);
        Assert.Equal(frames, h.Outbox.OfType<RoomFrame>().Count());
        var v = h.View(null);
        Assert.Equal("wait", v.GetProperty("ph").GetString());
        Assert.Equal(0, v.GetProperty("s").GetInt32());
        Assert.Equal(0, v.GetProperty("m").GetInt32());
        Assert.Equal(0, Sim(h).S);
        Assert.True(Sim(h).ObstacleCount > 0);   // курс дня вже видно
    }

    [Fact]
    public void A_held_key_without_an_edge_does_not_start_the_run()
    {
        var h = Open();
        h.Input(0, "in", new { s = 0, k = 1 });
        h.Input(0, "in", new { s = 0, k = 2 });
        h.Tick(5);
        Assert.Equal("wait", Ph(h));
        Assert.Equal("Стрибни, щоб побігти", h.Act(0, "in", new { s = 0, k = 3 }).Message);
    }

    [Fact]
    public void The_first_input_starts_the_run_at_step_zero_with_a_jump()
    {
        var h = Open();
        h.Input(0, "in", new { s = 77, k = 5 });     // хоч би що клієнт написав у s — старт на кроці 0
        var before = h.Outbox.Count;
        h.Tick();
        Assert.Contains(h.Outbox.Skip(before), o => o is RoomViews);
        var p = Sim(h).P[0];
        Assert.True(p.Air);
        Assert.True(p.Y > 0);
        Assert.Equal(2, Sim(h).S);
        Assert.Equal("run", Ph(h));
    }

    [Fact]
    public void Progress_keeps_growing_to_1250_and_there_is_no_snow_or_countdown()
    {
        var h = Open();
        var sim = Sim(h);
        Assert.Equal(1250, sim.PmCap);
        Assert.Equal(0, sim.ReadySteps);
        Assert.False(sim.SnowOn);
        var v = h.View(null);
        Assert.Equal(1250, v.GetProperty("pmCap").GetInt32());
        Assert.Equal(0, v.GetProperty("readySteps").GetInt32());
        Assert.True(v.GetProperty("daily").GetBoolean());
    }

    [Fact]
    public void There_is_no_round_cap_the_run_lasts_until_the_avalanche()
    {
        var h = Open();
        Go(h);
        Sim(h).ClearCourse();
        h.Tick(3600);                  // 7200 кроків — далі за стелю партії
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("run", Ph(h));
        Assert.True(Sim(h).S >= 7200);
    }

    // ---------- рахунок ----------

    [Fact]
    public void Being_caught_finishes_the_room_with_the_distance_as_solo_score()
    {
        var h = Open();
        var m = RunTo(h, 300);
        Assert.InRange(m, 300, 305);
        var score = h.Scores.Single();
        Assert.Equal("dino-daily:2026-09-10:оля", score.Key);
        Assert.Equal(ScoreOrder.HigherIsBetter, score.Order);
        Assert.Equal([0], h.Finished.Single().Result.Winners);
        var v = h.View(null);
        Assert.Equal("done", v.GetProperty("ph").GetString());
        Assert.Equal(m, v.GetProperty("last").GetProperty("m").GetInt32());
        Assert.True(v.GetProperty("last").GetProperty("record").GetBoolean());
        Assert.Equal(1, v.GetProperty("runs").GetInt32());
        Assert.Equal(m, v.GetProperty("best").GetInt32());
    }

    [Fact]
    public void Snowballs_never_spawn_and_throw_is_refused()
    {
        var h = Open();
        Go(h);
        var sim = Sim(h);
        for (var i = 0; i < 3000; i++)
        {
            sim.P[0].Lag = 0;
            h.Tick();
            for (var k = 0; k < sim.PickupCount; k++) Assert.NotEqual(RunnerKind.SnowBall, sim.Pickup(k).Kind);
        }
        Assert.Equal("Тут так не ходять", h.Act(0, "throw").Message);
    }

    [Fact]
    public void Eggs_count_but_do_not_change_metres()
    {
        var a = Open();
        var plain = RunTo(a, 250);
        var b = Open();
        var eggy = RunTo(b, 250, eggs: 5);
        Assert.Equal(plain, eggy);
        Assert.Equal(5, b.View(null).GetProperty("last").GetProperty("eggs").GetInt32());
        Assert.Equal(5, b.View(null).GetProperty("eggsTotal").GetInt32());
    }

    [Fact]
    public void The_daily_reward_is_asked_once_and_only_from_500_metres()
    {
        var h = Open();
        RunTo(h, 300);
        Assert.DoesNotContain(h.Awards, a => a.Reason == "daily:dino-daily");
        h.Rematch();
        RunTo(h, 520);
        Assert.Single(h.Awards, a => a.Reason == "daily:dino-daily");
        h.Rematch();
        RunTo(h, 600);
        Assert.Single(h.Awards, a => a.Reason == "daily:dino-daily");
    }

    [Fact]
    public void Two_kilometres_ask_dino_far()
    {
        var h = Open();
        RunTo(h, 1990);
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:dino-far");
        h.Rematch();
        RunTo(h, 2001);
        Assert.Single(h.Awards, a => a.Reason == "ach:dino-far");
    }

    [Fact]
    public void A_new_personal_best_over_1000_metres_writes_one_journal_line_per_minute()
    {
        var h = Open();
        var m = RunTo(h, 1100);
        Assert.Equal($"Забіг дня: Оля — {m} м, особистий рекорд дня", h.Outbox.OfType<Journal>().Last().Text);
        h.Rematch();
        RunTo(h, 1200);                  // краще, але хвилини ще не минуло
        Assert.Single(h.Outbox.OfType<Journal>(), j => j.Text.StartsWith("Забіг дня"));
        h.Clock.Advance(TimeSpan.FromSeconds(61));
        h.Rematch();
        var m3 = RunTo(h, 1300);
        Assert.Equal(2, h.Outbox.OfType<Journal>().Count(j => j.Text.StartsWith("Забіг дня")));
        Assert.Contains($"{m3} м", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void A_worse_run_or_a_short_one_writes_nothing_to_the_journal()
    {
        var h = Open();
        RunTo(h, 900);                   // менше 1000 — мовчимо
        Assert.DoesNotContain(h.Outbox.OfType<Journal>(), j => j.Text.StartsWith("Забіг дня"));
        h.Rematch();
        RunTo(h, 1500);
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        h.Rematch();
        RunTo(h, 1200);                  // гірше за рекорд
        Assert.Single(h.Outbox.OfType<Journal>(), j => j.Text.StartsWith("Забіг дня"));
        Assert.False(h.View(null).GetProperty("last").GetProperty("record").GetBoolean());
    }

    // ---------- спроби, збереження, повернення ----------

    [Fact]
    public void Rematch_returns_to_wait_and_keeps_best_and_runs()
    {
        var h = Open();
        var m = RunTo(h, 400);
        Assert.True(h.Rematch().Ok);
        var v = h.View(null);
        Assert.Equal("wait", v.GetProperty("ph").GetString());
        Assert.Equal(0, v.GetProperty("s").GetInt32());
        Assert.Equal(m, v.GetProperty("best").GetInt32());
        Assert.Equal(1, v.GetProperty("runs").GetInt32());
        Assert.Equal(Days.Seed("dino-daily", "2026-09-10"), v.GetProperty("seed").GetInt32());
        RunTo(h, 200);
        Assert.Equal(m, h.View(null).GetProperty("best").GetInt32());
        Assert.Equal(2, h.View(null).GetProperty("runs").GetInt32());
        Assert.Equal(2, h.Scores.Count);
    }

    [Fact]
    public void Rematch_after_kyiv_midnight_runs_the_new_day_with_a_fresh_record_and_reward()
    {
        // Кімнату відкрито о 23:58 за Києвом; «Ще раз» — уже після півночі. Рядок у таблицю каркас однаково пише
        // в сьогодні (за часом запису), тож і траса мусить бути сьогоднішня, і рекорд, і нагорода дня.
        var h = new RoomHarness("dino-daily", seed: 3);
        h.Clock.UtcNow = new DateTimeOffset(2026, 9, 10, 20, 58, 0, TimeSpan.Zero);
        Open("Оля", h);
        Assert.Equal("2026-09-10", h.View(null).GetProperty("day").GetString());
        var m1 = RunTo(h, 600);
        Assert.Single(h.Awards, a => a.Reason == "daily:dino-daily");
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True(h.Rematch().Ok);
        var v = h.View(null);
        Assert.Equal("2026-09-11", v.GetProperty("day").GetString());
        Assert.Equal(Days.Seed("dino-daily", "2026-09-11"), v.GetProperty("seed").GetInt32());
        var fresh = new RunnerSim(RunnerMode.Dino, Days.Seed("dino-daily", "2026-09-11"), [true], 0, DinoDaily.PmCapDaily, snowOn: false);
        Assert.Equal(fresh.Obstacle(0).X, Sim(h).Obstacle(0).X);             // і курс у світі — сьогоднішній
        Assert.Equal(fresh.Obstacle(0).Kind, Sim(h).Obstacle(0).Kind);
        Assert.Equal(0, v.GetProperty("best").GetInt32());
        Assert.Equal(0, v.GetProperty("runs").GetInt32());
        var m2 = RunTo(h, 550);
        Assert.True(m2 < m1);
        Assert.Equal(m2, h.View(null).GetProperty("best").GetInt32());      // рекорд нового дня — з нуля
        Assert.True(h.View(null).GetProperty("last").GetProperty("record").GetBoolean());
        Assert.Equal(2, h.Awards.Count(a => a.Reason == "daily:dino-daily"));   // нагорода нового дня проситься знову
        Assert.Contains("\"day\":\"2026-09-11\"", h.Room.Game.Save());
    }

    [Fact]
    public void Save_then_load_restores_best_runs_eggs_and_paid_for_the_same_day()
    {
        var h = Open();
        var m = RunTo(h, 600, eggs: 3);
        var json = h.Store.LoadState("dino-daily:2026-09-10:оля");
        Assert.NotNull(json);
        var saved = JsonDocument.Parse(json!).RootElement;
        Assert.Equal("2026-09-10", saved.GetProperty("day").GetString());
        Assert.Equal(m, saved.GetProperty("best").GetInt32());
        Assert.True(saved.GetProperty("paid").GetBoolean());

        var again = new DinoDaily { Ctx = h.Room.Game.Ctx };
        again.Start();
        again.Load(json!);
        var v = Views.Json(again.View(null));
        Assert.Equal(m, v.GetProperty("best").GetInt32());
        Assert.Equal(1, v.GetProperty("runs").GetInt32());
        Assert.Equal(3, v.GetProperty("eggsTotal").GetInt32());
        Assert.Equal(json, again.Save());
    }

    [Fact]
    public void A_saved_state_from_another_day_is_ignored()
    {
        var h = Open();
        var game = Game(h);
        game.Load("""{"day":"2026-09-09","best":9999,"runs":40,"eggs":3,"paid":true,"loggedAt":null}""");
        Assert.Equal(0, h.View(null).GetProperty("best").GetInt32());
        game.Load("""{"day":"2026-09-10","best":777,"runs":4,"eggs":3,"paid":true,"loggedAt":null}""");
        Assert.Equal(777, h.View(null).GetProperty("best").GetInt32());
    }

    [Fact]
    public void Reopening_the_solo_room_the_same_day_returns_the_same_room_and_its_record()
    {
        var h = Open();
        var id = h.RoomId;
        var m = RunTo(h, 350);
        Assert.True(h.Solo("Оля").Ok);
        Assert.Equal(id, h.RoomId);
        // кімнату прибрали (минуло 15 хвилин) — нова підніме збереження
        h.Clock.Advance(TimeSpan.FromMinutes(16));
        h.Rooms.Housekeeping(h.Clock.UtcNow);
        Assert.Null(h.Rooms.Find(id));
        Assert.True(h.Solo("Оля").Ok);
        Assert.NotEqual(id, h.RoomId);
        Assert.Equal(m, h.View(null).GetProperty("best").GetInt32());
        Assert.Equal("wait", Ph(h));
    }

    [Fact]
    public void Leaving_mid_run_records_nothing()
    {
        var h = Open();
        Go(h);
        h.Tick(50);
        var id = h.RoomId;
        h.Leave("Оля");
        Assert.Null(h.Rooms.Find(id));
        Assert.Empty(h.Scores);
        Assert.Empty(h.Finished);
    }

    // ---------- дріт ----------

    [Fact]
    public void View_of_the_owner_and_of_null_seat_are_identical_and_hold_day_seed_best_and_runs()
    {
        var h = Open();
        RunTo(h, 150);
        h.Rematch();
        Go(h);
        h.Tick(30);
        Assert.Equal(h.View(null).GetRawText(), h.View(0).GetRawText());
        var v = h.View(null);
        foreach (var key in new[] { "day", "no", "seed", "best", "runs", "eggsTotal", "p", "d", "m", "s", "ph" })
            Assert.True(Views.Has(v, key), key);
        Assert.Equal(Days.Number("2026-09-10"), v.GetProperty("no").GetInt32());
        Assert.Equal(11, v.GetProperty("p")[0].GetArrayLength());
    }

    [Fact]
    public void Frame_has_metres_and_stays_under_two_hundred_bytes()
    {
        var h = Open();
        Go(h);
        var sim = Sim(h);
        var max = 0;
        for (var i = 0; i < 2000; i++)
        {
            sim.P[0].Lag = Math.Min(sim.P[0].Lag, 3000);
            h.Tick();
            var f = h.Outbox.OfType<RoomFrame>().Last().Frame;
            max = Math.Max(max, System.Text.Encoding.UTF8.GetByteCount(Views.Text(f)));
        }
        var last = Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);
        Assert.Equal(sim.PaceX(sim.S) / RunnerDino.SubPerMetre, last.GetProperty("m").GetInt32());
        Assert.True(max < 200, $"кадр {max} Б");
    }

    [Fact]
    public void Input_payload_is_exactly_what_the_module_sends()
    {
        var h = Open();
        foreach (var junk in new object?[] { null, 4, new { k = 5 }, new { s = 0 }, new { s = 0, k = 13 }, new { s = "0", k = 5 } })
            h.Input(0, "in", junk);
        Assert.Equal("wait", Ph(h));
        h.Input(0, "in", new { s = 0, k = 5 });
        Assert.Equal("run", Ph(h));
        h.Tick(3);
        h.Input(0, "ping", new { t = 4321 });
        h.Tick();
        Assert.Equal(4321, Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame).GetProperty("pg")[0].GetInt32());
    }

    [Fact]
    public void The_same_day_and_inputs_give_the_same_metres()
    {
        static (int, int) Play(string nick)
        {
            var h = new RoomHarness("dino-daily", seed: nick.Length);
            h.Solo(nick);
            h.Input(0, "in", new { s = 0, k = 5 });
            var sim = ((DinoDaily)h.Room.Game).World!;
            var rng = new RunnerRng(21);
            for (var i = 0; i < 20000 && h.Room.Status == RoomStatus.Playing; i++)
            {
                if (i % 4 == 0) h.Input(0, "in", new { s = sim.S, k = rng.Next(8) });
                h.Tick();
            }
            return ((int)h.Scores.Single().Score, sim.P[0].Hits);
        }
        var a = Play("Оля");
        Assert.Equal(a, Play("Марта"));      // інший нік, інше зерно кімнати — та сама траса дня
        Assert.True(a.Item2 > 0);
    }

    [Fact]
    public void Simulation_hash_matches_the_javascript_reference_for_the_daily_fixture()
    {
        var (name, make, js) = RunnerSimTests.Fixtures.Single(f => f.Name.StartsWith("dino-daily"));
        var sim = make();
        Assert.True(js == sim.Hash, $"{name}: C# {sim.Hash}, JS {js}");
        Assert.Equal(1250, sim.PmCap);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Ten_daily_rooms_ticking_together_cost_under_a_millisecond_per_tick()
    {
        var hs = new List<RoomHarness>();
        for (var i = 0; i < 10; i++)
        {
            var h = new RoomHarness("dino-daily", seed: i + 1);
            h.Solo("Гравець" + i);
            h.Input(0, "in", new { s = 0, k = 5 });
            ((DinoDaily)h.Room.Game).World!.ClearCourse();
            hs.Add(h);
        }
        for (var i = 0; i < 20; i++) foreach (var h in hs) h.Rooms.Tick(h.Room);
        var sw = Stopwatch.StartNew();
        for (var t = 0; t < 1000; t++)
            foreach (var h in hs) h.Rooms.Tick(h.Room);
        sw.Stop();
        var perTick = sw.Elapsed.TotalMilliseconds / 1000;
        Console.WriteLine($"[perf] dino-daily: 10 кімнат, {perTick:F4} мс на спільний тик");
        Assert.True(perTick < 1, $"{perTick} мс");
    }
}
