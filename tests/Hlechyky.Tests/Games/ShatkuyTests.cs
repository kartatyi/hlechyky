using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Шаткуй!» (docs/games/specs/shatkuy.md): розклад кидків і ігровий час (ядро), розрізи з перевіркою «у повітрі», комбо,
/// промахи й глек Класики, мороз / ×2 / злива / великий кавун Ярмарку, Дзен без глеків і «Шаткуй дня».
/// </summary>
public class ShatkuyTests(Xunit.Abstractions.ITestOutputHelper output)
{
    // ---------- ядро ----------

    [Fact]
    public void Plan_is_the_same_for_the_same_seed_and_grows_lazily()
    {
        var a = new ShatkuyPlan(ShatkuyCore.Fair, 42);
        var b = new ShatkuyPlan(ShatkuyCore.Fair, 42);
        a.Until(20_000);
        b.Until(10_000);
        b.Until(20_000);
        Assert.Equal(a.Fruits, b.Fruits);
        Assert.All(a.Fruits, f => Assert.True(f.At <= 20_000 + 1_000));
        var c = new ShatkuyPlan(ShatkuyCore.Fair, 43);
        c.Until(20_000);
        Assert.NotEqual(a.Fruits.Select(f => f.K), c.Fruits.Select(f => f.K));
        Assert.Equal(ShatkuyCore.ShowerOf(42, 1, 5_000), ShatkuyCore.ShowerOf(42, 1, 5_000));
        Assert.All(ShatkuyCore.ShowerOf(42, 2, 5_000), f => Assert.True(f.Id > 20_000 && ShatkuyCore.IsPlain(f.K)));
    }

    [Fact]
    public void Timed_plan_ends_with_one_big_kavun_and_zen_has_no_gleks()
    {
        var fair = new ShatkuyPlan(ShatkuyCore.Fair, 7);
        fair.Until(200_000);
        Assert.Single(fair.Fruits, f => f.Big);
        Assert.All(fair.Fruits, f => Assert.True(f.At < ShatkuyCore.Fair.LenMs));
        Assert.Contains(fair.Fruits, f => f.Glek);
        Assert.Contains(fair.Fruits, f => ShatkuyCore.Specials.Contains(f.K));

        var zen = new ShatkuyPlan(ShatkuyCore.Zen, 7);
        zen.Until(200_000);
        Assert.DoesNotContain(zen.Fruits, f => f.Glek || f.Big || !ShatkuyCore.IsPlain(f.K));
        Assert.True(zen.Fruits.Count > 90);

        var classic = new ShatkuyPlan(ShatkuyCore.Classic, 7);
        classic.Until(120_000);
        Assert.DoesNotContain(classic.Fruits, f => f.Big || ShatkuyCore.Specials.Contains(f.K));
        Assert.Contains(classic.Fruits, f => f.Glek);
    }

    [Fact]
    public void Frost_slows_game_time_by_half_and_extends_when_cut_again()
    {
        var frost = new List<(int S, int E)>();
        Assert.Equal(3_000, ShatkuyCore.Game(3_000, frost));
        ShatkuyCore.AddFrost(frost, 1_000);
        Assert.Equal(1_000 + 1_000, ShatkuyCore.Game(3_000, frost));
        ShatkuyCore.AddFrost(frost, 4_000);   // ще йде — подовжити, а не новий відрізок
        Assert.Single(frost);
        Assert.Equal((1_000, 9_000), frost[0]);
        Assert.Equal(1_000 + 4_000 + 1_000, ShatkuyCore.Game(10_000, frost));
    }

    // ---------- партія ----------

    internal static (RoomHarness H, ShatkuyGame G) Open(string id, IServiceProvider? services = null)
    {
        var h = new RoomHarness(id, seed: 5, services: services);
        Assert.True(h.Solo("Мар'яна").Ok);
        return (h, (ShatkuyGame)h.Room.Game);
    }

    internal static void Go(RoomHarness h, ShatkuyGame g)
    {
        Assert.Equal(ShatkuyGame.PhReady, g.Phase);
        Assert.True(h.Act(0, "go").Ok);
        Assert.Equal(ShatkuyGame.PhGo, g.Phase);
        h.Clock.AdvanceMs(ShatkuyCore.LeadMs);
    }

    /// <summary>Плоди, що зараз у повітрі за ігровим часом.</summary>
    static List<ShatkuyFruit> Air(ShatkuyGame g)
    {
        var now = g.GameNow;
        return [.. g.Fruits.Where(f => !g.IsCut(f.Id) && f.At <= now && now <= f.At + f.D).OrderBy(f => f.Id)];
    }

    static int _w;

    /// <summary>Бот: кожен тік ріже все, що летить, крім глеків (кожен плід — своїм змахом).</summary>
    internal static void Bot(RoomHarness h, ShatkuyGame g, int ticks, bool gleks = false, bool big = true)
    {
        for (var i = 0; i < ticks && g.Phase == ShatkuyGame.PhGo; i++)
        {
            var c = Air(g).Where(f => (gleks || !f.Glek) && (big || !f.Big)).Select(f => new[] { f.Id, ++_w }).ToArray();
            if (c.Length > 0) h.Input(0, "cut", new { c });
            h.Tick();
        }
    }

    [Fact]
    public void Classic_waits_for_go_and_ends_after_three_misses()
    {
        var (h, g) = Open("shatkuy");
        Assert.False(h.Act(0, "cut", new { c = new[] { new[] { 1, 1 } } }).Ok);
        Go(h, g);
        for (var i = 0; i < 2_000 && g.Phase == ShatkuyGame.PhGo; i++) h.Tick();
        Assert.Equal(ShatkuyGame.PhOver, g.Phase);
        Assert.Equal(ShatkuyGame.WhyMiss, g.Why);
        Assert.Equal(3, g.Misses);
        Assert.Single(h.Finished);
        Assert.Contains(h.Scores, s => s.GameId == "shatkuy" && s.Score == 0);
        var end = h.View(0).GetProperty("end");
        Assert.Equal("miss", end.GetProperty("why").GetString());
    }

    [Fact]
    public void Classic_bot_keeps_all_lives_and_a_glek_ends_it()
    {
        var (h, g) = Open("shatkuy");
        Go(h, g);
        Bot(h, g, 30_000 / ShatkuyGame.TickMs);
        Assert.Equal(ShatkuyGame.PhGo, g.Phase);
        Assert.Equal(0, g.Misses);
        Assert.True(g.Score > 15, $"очок {g.Score}");

        // дочекатись глека в повітрі й розрізати
        for (var i = 0; i < 4_000 && !Air(g).Any(f => f.Glek); i++) Bot(h, g, 1);
        var glek = Air(g).First(f => f.Glek);
        h.Input(0, "cut", new { c = new[] { new[] { glek.Id, ++_w } } });
        Assert.Equal(ShatkuyGame.PhOver, g.Phase);
        Assert.Equal(ShatkuyGame.WhyGlek, g.Why);
        Assert.True(g.Best == g.Score && g.Score > 0);
    }

    [Fact]
    public void Cut_only_what_is_in_the_air_once_and_combo_gives_n_on_top()
    {
        var (h, g) = Open("shatkuy-zen");
        Go(h, g);
        // майбутній плід (уже в розкладі наперед) — не ріжеться
        h.Tick();
        var future = g.Fruits.Where(f => f.At > g.GameNow + 200).OrderBy(f => f.At).FirstOrDefault();
        if (future is not null) Assert.False(h.Act(0, "cut", new { c = new[] { new[] { future.Id, 1 } } }).Ok);

        // зібрати три в повітрі разом і різонути одним змахом
        for (var i = 0; i < 4_000 && Air(g).Count < 3; i++) h.Tick();
        var three = Air(g).Take(3).ToList();
        Assert.Equal(3, three.Count);
        var before = g.Score;
        Assert.True(h.Act(0, "cut", new { c = three.Select(f => new[] { f.Id, 9_000 }).ToArray() }).Ok);
        Assert.Equal(before + 3 + 3, g.Score);
        Assert.Equal(3, g.BestCombo);
        // той самий удруге — ні
        Assert.False(h.Act(0, "cut", new { c = new[] { new[] { three[0].Id, 9_001 } } }).Ok);
        Assert.Equal(before + 6, g.Score);

        // впалий давно — ні
        var fallen = g.Fruits.Where(f => !g.IsCut(f.Id) && f.Until < g.GameNow).FirstOrDefault();
        for (var i = 0; i < 400 && fallen is null; i++)
        {
            h.Tick();
            fallen = g.Fruits.Where(f => !g.IsCut(f.Id) && f.Until < g.GameNow).FirstOrDefault();
        }
        Assert.NotNull(fallen);
        Assert.False(h.Act(0, "cut", new { c = new[] { new[] { fallen!.Id, 9_002 } } }).Ok);
    }

    [Fact]
    public void Fair_runs_a_minute_specials_work_and_big_kavun_counts_once_per_swipe()
    {
        var (h, g) = Open("shatkuy-fair");
        Go(h, g);
        var sawFrost = false;
        var ticks = 0;
        while (g.Phase == ShatkuyGame.PhGo && ticks++ < 5_000)
        {
            var air = Air(g);
            var bigOne = air.FirstOrDefault(f => f.Big);
            if (bigOne is not null)
            {
                // один змах — один удар, хоч скільки разів його прислати
                var w = ++_w;
                h.Input(0, "cut", new { c = new[] { new[] { bigOne.Id, w }, new[] { bigOne.Id, w } } });
            }
            var c = air.Where(f => !f.Glek && !f.Big).Select(f => new[] { f.Id, ++_w }).ToArray();
            if (c.Length > 0) h.Input(0, "cut", new { c });
            if (g.Frost.Count > 0) sawFrost = true;
            h.Tick();
        }
        Assert.Equal(ShatkuyGame.PhOver, g.Phase);
        Assert.Equal(ShatkuyGame.WhyTime, g.Why);
        Assert.True(sawFrost || !g.Fruits.Any(f => f.K == ShatkuyCore.Frost && f.At < ShatkuyCore.Fair.LenMs - 3_000));
        // мороз подовжує справжню хвилину
        var realMs = ticks * ShatkuyGame.TickMs;
        if (sawFrost) Assert.True(realMs > ShatkuyCore.Fair.LenMs + 1_000, $"мс {realMs}");
        Assert.InRange(g.BigHits, 10, ShatkuyCore.BigMaxHits);
        Assert.True(g.Score > 60, $"очок {g.Score}");
        Assert.Contains(h.Awards, a => a.Reason.StartsWith("shatkuy:") && a.Shards > 0);
        Assert.Contains(h.Scores, s => s.GameId == "shatkuy-fair" && s.Score == g.Score);
    }

    [Fact]
    public void Glek_in_timed_mode_costs_points_not_the_game()
    {
        var (h, g) = Open("shatkuy-fair");
        Go(h, g);
        for (var i = 0; i < 4_000 && !Air(g).Any(f => f.Glek); i++) Bot(h, g, 1);
        var before = g.Score;
        h.Input(0, "cut", new { c = new[] { new[] { Air(g).First(f => f.Glek).Id, ++_w } } });
        Assert.Equal(ShatkuyGame.PhGo, g.Phase);
        Assert.Equal(Math.Max(0, before - ShatkuyCore.GlekPenalty), g.Score);
    }

    [Fact]
    public void View_shows_only_the_next_second_and_rematch_keeps_the_record()
    {
        var (h, g) = Open("shatkuy");
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("t0").ValueKind);
        Go(h, g);
        Bot(h, g, 200);
        var v = h.View(0);
        var now = g.GameNow;
        foreach (var f in v.GetProperty("fruits").EnumerateArray())
            Assert.True(f.GetProperty("at").GetInt32() <= now + ShatkuyCore.LookMs);
        Assert.Equal("go", v.GetProperty("phase").GetString());
        for (var i = 0; i < 4_000 && g.Phase == ShatkuyGame.PhGo; i++) h.Tick();
        var best = g.Best;
        Assert.True(best > 0);
        Assert.True(h.Rematch().Ok);
        var g2 = (ShatkuyGame)h.Room.Game;
        Assert.Equal(ShatkuyGame.PhReady, g2.Phase);
        Assert.Equal(best, g2.Best);
        Assert.Equal(0, g2.Score);

        // рекорд переживає новий стіл
        var saved = h.Store.States[h.Room.Key!];
        var h2 = new RoomHarness("shatkuy");
        h2.Store.States[h.Room.Key!] = saved;
        Assert.True(h2.Solo("Мар'яна").Ok);
        Assert.Equal(best, ((ShatkuyGame)h2.Room.Game).Best);
    }

    // ---------- Шаткуй дня ----------

    static (RoomHarness H, ShatkuyDaily G) Daily(string nick, ShatkuyDailyBoard table)
    {
        var h = new RoomHarness("shatkuy-daily", services: RoomHarness.WithService(table));
        Assert.True(h.Solo(nick).Ok);
        return (h, (ShatkuyDaily)h.Room.Game);
    }

    [Fact]
    public void Daily_same_cart_for_everyone_one_attempt_and_a_table()
    {
        var table = new ShatkuyDailyBoard(null);
        var (a, ga) = Daily("Оля", table);
        var (b, gb) = Daily("Микола", table);
        Assert.StartsWith("daily:shatkuy-daily:", a.Room.Key);
        Go(a, ga);
        Go(b, gb);
        Bot(a, ga, 400);
        Bot(b, gb, 400);
        Assert.Equal(ga.Fruits.Where(f => f.Id < 10_000).OrderBy(f => f.Id).Take(20), gb.Fruits.Where(f => f.Id < 10_000).OrderBy(f => f.Id).Take(20));
        Assert.Equal(JsonValueKind.Null, a.View(0).GetProperty("daily").GetProperty("board").ValueKind);
        for (var i = 0; i < 4_000 && ga.Phase == ShatkuyGame.PhGo; i++) Bot(a, ga, 1);
        Assert.True(ga.Reported);
        Assert.Contains(a.Scores, s => s.GameId == "shatkuy-daily" && s.Attempts == 1);
        Assert.Contains(a.Awards, x => x.Reason == "daily:shatkuy-daily");
        Assert.DoesNotContain(a.Awards, x => x.Reason.StartsWith("shatkuy:"));
        Assert.Equal(ga.Score, ((IDailyPoints)table).Points(ga.Day, "оля"));
        var v = a.View(0);
        Assert.Equal("over", v.GetProperty("phase").GetString());
        Assert.Contains("Шаткуй дня №", v.GetProperty("daily").GetProperty("share").GetString());
        Assert.False(a.Rematch().Ok);

        // Микола пішов посеред партії — спроба зарахована з тим, що встиг
        var score = gb.Score;
        b.Leave("Микола");
        Assert.True(gb.Reported);
        Assert.Equal(score, ((IDailyPoints)table).Points(gb.Day, "микола"));
        Assert.Equal(2, table.Top(ga.Day).Count);

        // повернувся — щоденний глек видає перший тік нової кімнати, і вдруге не грає
        var key = "daily:shatkuy-daily:" + gb.Day + ":микола";
        var b2 = new RoomHarness("shatkuy-daily", services: RoomHarness.WithService(table));
        b2.Store.States[key] = b.Store.States[key];
        Assert.True(b2.Solo("Микола").Ok);
        b2.Tick();
        Assert.Contains(b2.Awards, x => x.Reason == "daily:shatkuy-daily");
        Assert.False(b2.Act(0, "go").Ok);
        Assert.True(((ShatkuyDaily)b2.Room.Game).Reported);
    }

    [Fact]
    public void Calibrate_scores_with_bots()
    {
        if (Environment.GetEnvironmentVariable("SHATKUY_CALIB") != "1") return;
        foreach (var id in new[] { "shatkuy", "shatkuy-fair", "shatkuy-zen", "shatkuy-daily" })
            for (var skill = 0; skill < 2; skill++)
            {
                var scores = new List<string>();
                for (var seed = 1; seed <= 6; seed++)
                {
                    var h = new RoomHarness(id, seed: seed, services: RoomHarness.WithService(new ShatkuyDailyBoard(null)));
                    Assert.True(h.Solo("бот" + seed).Ok);
                    var g = (ShatkuyGame)h.Room.Game;
                    Go(h, g);
                    var rng = new Random(seed);
                    for (var i = 0; i < 40_000 && g.Phase == ShatkuyGame.PhGo; i++)
                    {
                        // skill 0 — «людина»: бачить плід за 300 мс польоту, 8 % пропускає, ріже пачками по 1–4 одним змахом
                        var air = Air(g).Where(f => !f.Glek && (skill == 1 || g.GameNow - f.At > 300)).ToList();
                        if (skill == 0) air = [.. air.Where(_ => rng.NextDouble() > 0.04)];
                        var w = ++_w;
                        var c = air.Select((f, j) => new[] { f.Id, skill == 1 ? ++_w : (j % 3 == 0 ? (w = ++_w) : w) }).ToArray();
                        if (c.Length > 0 && (skill == 1 || rng.NextDouble() < 0.5)) h.Input(0, "cut", new { c });
                        if (skill == 0 && rng.NextDouble() < 0.0008)
                        {
                            var glek = Air(g).FirstOrDefault(f => f.Glek);
                            if (glek is not null) h.Input(0, "cut", new { c = new[] { new[] { glek.Id, ++_w } } });
                        }
                        h.Tick();
                    }
                    scores.Add($"{g.Score}({g.Why},c{g.BestCombo},b{g.BigHits},{g.GameNow / 1000}s)");
                }
                var line = $"{id} {(skill == 0 ? "людина" : "ідеал")}: {string.Join(" ", scores)}";
                output.WriteLine(line);
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "shatkuy-calib.txt"), line + Environment.NewLine);
            }
    }

    [Fact]
    public void Daily_played_day_stays_played_after_reload()
    {
        var table = new ShatkuyDailyBoard(null);
        var (h, g) = Daily("Оля", table);
        Go(h, g);
        for (var i = 0; i < 4_000 && g.Phase == ShatkuyGame.PhGo; i++) Bot(h, g, 1);
        var key = h.Room.Key!;
        var h2 = new RoomHarness("shatkuy-daily", services: RoomHarness.WithService(table));
        h2.Store.States[key] = h.Store.States[key];
        Assert.True(h2.Solo("Оля").Ok);
        var g2 = (ShatkuyDaily)h2.Room.Game;
        Assert.True(g2.Reported);
        Assert.Equal(g.Score, g2.Score);
        Assert.Equal(ShatkuyDaily.DoneToday, h2.Act(0, "go").Message);
        Assert.Equal("over", h2.View(0).GetProperty("phase").GetString());
    }
}

/// <summary>Швидкодія «Шаткуй!» — окремою колекцією (SerialPerf).</summary>
[Collection(SerialPerf.Name)]
public class ShatkuyPerfTests
{
    [Fact, Trait("Category", "Perf")]
    public void Thousand_ticks_of_a_shower_are_fast()
    {
        var (h, g) = ShatkuyTests.Open("shatkuy-fair");
        ShatkuyTests.Go(h, g);
        var sw = Stopwatch.StartNew();
        ShatkuyTests.Bot(h, g, 1_000);
        for (var i = 0; i < 50; i++) h.View(0);
        Assert.True(sw.ElapsedMilliseconds < 2_000, $"{sw.ElapsedMilliseconds} мс");
    }
}
