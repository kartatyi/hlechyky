using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>«Намисто» за столом каркаса: траси, журнал вводу проти годинника сервера, пауза, кінець, нагороди, «Намисто дня».</summary>
[Collection(SerialPerf.Name)]
public class NamystoTests
{
    static IServiceProvider Services(NamystoStore? store = null, NamystoDailyBoard? board = null) => new ServiceCollection()
        .AddSingleton(store ?? new NamystoStore(null))
        .AddSingleton(board ?? new NamystoDailyBoard(null))
        .BuildServiceProvider();

    static RoomHarness Open(string game = "namysto", IServiceProvider? services = null, string nick = "Оля")
    {
        var h = new RoomHarness(game, seed: 5, services: services ?? Services());
        Assert.True(h.Solo(nick).Ok);
        return h;
    }

    static Namysto G(RoomHarness h) => (Namysto)h.Room.Game;
    static string Ph(RoomHarness h) => h.View(0).GetProperty("phase").GetString()!;

    static void Go(RoomHarness h, int level = 1)
    {
        if (G(h).Info.Id == "namysto") Assert.True(h.Act(0, "level", new { n = level }).Ok);
        Assert.True(h.Act(0, "go").Ok);
        Assert.Equal(Namysto.PhGo, G(h).Phase);
    }

    /// <summary>Пачка журналу так, як її шле браузер: годинник іде рівно, пачка — раз на 15 кроків.</summary>
    static void Feed(RoomHarness h, IReadOnlyList<int[]> journal, int until)
    {
        var g = G(h);
        var k = 0;
        for (var s = 15; ; s += 15)
        {
            h.Clock.AdvanceMs(250);
            var batch = new List<int[]>();
            while (k < journal.Count && journal[k][0] < s) batch.Add(journal[k++]);
            h.Input(0, "j", new { s, ep = g.Epoch, e = batch });
            if (h.Room.Status == RoomStatus.Finished || s >= until) break;
        }
    }

    // ---------- паспорт ----------

    [Fact]
    public void Passports_are_private_solo_realtime_with_one_client_and_daily_is_a_daily()
    {
        var a = new Namysto().Info;
        var d = new NamystoDaily().Info;
        Assert.Equal(("namysto", "Намисто", GameGroup.Solo, "namysto"), (a.Id, a.Title, a.Group, a.Module));
        Assert.Equal(("namysto-daily", "Намисто дня", GameGroup.Solo, "namysto"), (d.Id, d.Title, d.Group, d.Module));
        Assert.True(a.Private && d.Private && d.Persistent && !a.Persistent);
        Assert.Equal((1, 1, 100), (a.MinPlayers, a.MaxPlayers, a.TickMs));
        Assert.Equal(ScoreOrder.HigherIsBetter, a.Score);
        Assert.True(typeof(IDailyGame).IsAssignableFrom(typeof(NamystoDaily)));
        Assert.False(typeof(IDailyGame).IsAssignableFrom(typeof(Namysto)));
        var clock = new FakeClock { UtcNow = new DateTimeOffset(2026, 10, 20, 21, 30, 0, TimeSpan.Zero) };   // 00:30 за Києвом
        Assert.Equal("daily:namysto-daily:2026-10-21:оля", new NamystoDaily().SoloKey("оля", clock));
        Assert.Equal("namysto:оля", new Namysto().SoloKey("оля", clock));
    }

    [Fact]
    public void Tracks_endpoint_json_has_all_twelve_with_delta_points_that_rebuild_the_server_track()
    {
        using var doc = JsonDocument.Parse(NamystoSetup.TracksJson.Value);
        var root = doc.RootElement;
        Assert.Equal(NamystoSetup.TracksVersion.Value, root.GetProperty("v").GetUInt32());
        var tracks = root.GetProperty("tracks");
        Assert.Equal(12, tracks.GetArrayLength());
        foreach (var t in tracks.EnumerateArray())
        {
            var lv = NamystoTracks.Level(t.GetProperty("n").GetInt32());
            var d = t.GetProperty("pts").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            for (var i = 2; i < d.Length; i++) d[i] += d[i - 2];
            Assert.Equal(lv.Wire(), d);
            Assert.Equal((lv.Track.Sx, lv.Track.Sy), (t.GetProperty("sx").GetInt32(), t.GetProperty("sy").GetInt32()));
        }
        Assert.InRange(NamystoSetup.TracksJson.Value.Length, 10_000, 120_000);
    }

    // ---------- кампанія: вибір траси ----------

    [Fact]
    public void Campaign_starts_at_the_track_list_with_only_the_first_track_open()
    {
        var h = Open();
        var v = h.View(0);
        Assert.Equal("pick", v.GetProperty("phase").GetString());
        Assert.Equal("campaign", v.GetProperty("mode").GetString());
        var levels = v.GetProperty("levels");
        Assert.Equal(12, levels.GetArrayLength());
        Assert.True(levels[0].GetProperty("open").GetBoolean());
        Assert.False(levels[1].GetProperty("open").GetBoolean());
        Assert.False(h.Act(0, "level", new { n = 2 }).Ok);
        Assert.False(h.Act(0, "level", new { n = 0 }).Ok);
        Assert.False(h.Act(0, "go").Ok);
        Assert.True(h.Act(0, "level", new { n = 1 }).Ok);
        v = h.View(0);
        Assert.Equal("ready", v.GetProperty("phase").GetString());
        Assert.Equal(1, v.GetProperty("lv").GetProperty("n").GetInt32());
        Assert.True(v.GetProperty("seed").GetUInt32() > 0);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("jr").ValueKind);
        Assert.True(h.Act(0, "pick").Ok);
        Assert.Equal("pick", Ph(h));
    }

    [Fact]
    public void Store_opens_the_next_track_after_a_clear_and_remembers_the_best()
    {
        var s = new NamystoStore(null);
        var at = DateTimeOffset.UnixEpoch;
        Assert.Equal(1, s.Open("Оля"));
        Assert.Equal((false, true), s.Record("Оля", 1, 500, false, at));
        Assert.Equal(1, s.Open("Оля"));
        Assert.Equal((true, false), s.Record("оля", 1, 400, true, at));
        Assert.Equal(2, s.Open("Оля"));
        Assert.Equal(500, s.Best("Оля", 1)!.Best);
        Assert.Equal((false, false), s.Record("Оля", 1, 450, true, at));
        Assert.Equal((false, true), s.Record("Оля", 1, 900, true, at));
        s.Record("Петро", 1, 700, false, at);
        Assert.Equal(new[] { "Оля", "Петро" }, s.Top(1, 3).Select(b => b.Nick));
        Assert.False(s.AllCleared("Оля"));
        for (var n = 2; n <= 12; n++) s.Record("Оля", n, 1, true, at);
        Assert.True(s.AllCleared("Оля"));
        Assert.Equal(12, s.Open("Оля"));
    }

    // ---------- журнал проти годинника ----------

    [Fact]
    public void Journal_cannot_run_ahead_of_the_server_clock()
    {
        var h = Open();
        Go(h);
        h.Input(0, "j", new { s = 5000, ep = 0, e = Array.Empty<int[]>() });
        Assert.Equal(Namysto.Ahead, G(h).Sim!.S);
        h.Clock.AdvanceMs(1000);
        h.Input(0, "j", new { s = 5000, ep = 0, e = Array.Empty<int[]>() });
        Assert.Equal(60 + Namysto.Ahead, G(h).Sim!.S);
    }

    [Fact]
    public void Silent_client_does_not_freeze_the_string_the_server_rolls_on_and_asks_to_resync()
    {
        var h = Open();
        Go(h);
        h.Tick(30);   // 3 с: ще в межах
        Assert.Equal(0, G(h).Sim!.S);
        Assert.Equal(0, G(h).Epoch);
        h.Tick(20);   // 5 с: сервер котить сам, а браузер просить звіритись — один раз, поки той не перезбереться
        Assert.Equal(300 - Namysto.Behind, G(h).Sim!.S);
        Assert.Equal(1, G(h).Epoch);
        var v = h.View(0);
        Assert.Equal(1, v.GetProperty("ep").GetInt32());
        Assert.Equal(G(h).Sim!.S, v.GetProperty("s").GetInt32());
        Assert.Equal(300, v.GetProperty("real").GetInt32());
        Assert.Equal(JsonValueKind.Array, v.GetProperty("jr").ValueKind);
        // пачка старої епохи — мимо
        var s = G(h).Sim!.S;
        h.Input(0, "j", new { s = s + 10, ep = 0, e = new[] { new[] { s + 1, 2 } } });
        Assert.Equal(s, G(h).Sim!.S);
        // натиск на кроці, який сервер уже прокрутив, — знову звіряємось
        h.Input(0, "j", new { s = s + 10, ep = 1, e = new[] { new[] { s - 5, 2 } } });
        Assert.Equal(2, G(h).Epoch);
        Assert.Equal(s, G(h).Sim!.S);
    }

    [Fact]
    public void Bad_batches_change_nothing()
    {
        var h = Open();
        Go(h);
        h.Clock.AdvanceMs(1000);
        var g = G(h);
        foreach (var bad in new object[]
        {
            new { s = 20, ep = 0, e = new[] { new[] { 5, 1, 10 } } },          // постріл без y
            new { s = 20, ep = 0, e = new[] { new[] { 5, 3 } } },              // невідома дія
            new { s = 20, ep = 0, e = new[] { new[] { 5, 1, 99999, 10 } } },   // ціль за тридев'ять земель
            new { s = 20, ep = 0, e = new[] { new[] { 9, 2 }, new[] { 5, 2 } } },   // кроки назад
            new { s = 20, ep = 0, e = new[] { new[] { 25, 2 } } },             // подія після s
            new { s = 20, ep = 0, e = Enumerable.Range(0, 49).Select(i => new[] { 1, 2 }).ToArray() },
            new { ep = 0 },
        })
        {
            h.Input(0, "j", bad);
            Assert.Equal(0, g.Sim!.S);
            Assert.Empty(g.Journal);
        }
    }

    [Fact]
    public void Server_replays_the_browser_journal_to_the_same_score_and_pays_the_first_clear()
    {
        var store = new NamystoStore(null);
        var h = Open(services: Services(store));
        Assert.True(h.Act(0, "level", new { n = 1 }).Ok);
        var g = G(h);
        var lv = g.Level!;
        var (local, journal) = NamystoBot.Play(lv, g.Seed, every: 12);
        Assert.Equal(NamystoSim.Won, local.State);
        Assert.True(h.Act(0, "go").Ok);
        Feed(h, journal, local.S + 30);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal(local.Hash(), g.Sim!.Hash());
        Assert.Equal(journal.Count, g.Journal.Count);
        Assert.Equal(local.Score, (long)h.Scores.Single().Score);
        Assert.Contains(h.Awards, a => a.Reason == "namysto:1" && a.Shards == Namysto.FirstClearShards);
        Assert.Equal(2, store.Open("Оля"));
        var v = h.View(0);
        Assert.Equal("over", v.GetProperty("phase").GetString());
        var r = v.GetProperty("result");
        Assert.True(r.GetProperty("won").GetBoolean());
        Assert.True(r.GetProperty("first").GetBoolean());
        Assert.Equal(local.Score, r.GetProperty("score").GetInt64());
        Assert.Contains("пройдено вперше", h.Room.Result!.Text);

        // «Далі» → «Ще раз»: на другу трасу; третя ще закрита
        Assert.False(h.Act(0, "next", new { n = 3 }).Ok);
        Assert.True(h.Act(0, "next", new { n = 2 }).Ok);
        Assert.True(h.Rematch().Ok);
        Assert.Equal("ready", Ph(h));
        Assert.Equal(2, G(h).Level!.N);

        // та сама траса вдруге — без черепків
        var awards = h.Awards.Count;
        Assert.True(h.Act(0, "level", new { n = 1 }).Ok);
        g = G(h);
        (local, journal) = NamystoBot.Play(g.Level!, g.Seed, every: 12);
        Assert.True(h.Act(0, "go").Ok);
        Feed(h, journal, local.S + 30);
        Assert.DoesNotContain(h.Awards.Skip(awards), a => a.Reason.StartsWith("namysto:", StringComparison.Ordinal));
    }

    [Fact]
    public void Losing_ends_the_party_with_the_points_on_the_table_but_opens_nothing()
    {
        var store = new NamystoStore(null);
        var h = Open(services: Services(store));
        Go(h);
        // ніхто не стріляє: разок докочується до криниці
        for (var i = 0; i < 400 && h.Room.Status != RoomStatus.Finished; i++)
        {
            h.Clock.AdvanceMs(1000);
            h.Input(0, "j", new { s = G(h).Sim!.S + 60, ep = G(h).Epoch, e = Array.Empty<int[]>() });
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal(NamystoSim.Lost, G(h).Sim!.State);
        Assert.Equal(0, h.Scores.Single().Score);
        Assert.Equal(1, store.Open("Оля"));
        Assert.Equal("💧 Намисто впало в криницю", h.Room.Result!.Verdict);
        Assert.DoesNotContain(h.Awards, a => a.Reason.StartsWith("namysto:", StringComparison.Ordinal));
    }

    [Fact]
    public void Pause_stops_the_server_clock_and_resume_lets_it_run()
    {
        var h = Open();
        Go(h);
        h.Clock.AdvanceMs(500);
        Assert.True(h.Act(0, "pause", new { s = 30, ep = 0, e = new[] { new[] { 10, 2 } } }).Ok);
        Assert.True(G(h).Paused);
        Assert.Equal(30, G(h).Sim!.S);
        Assert.Single(G(h).Journal);
        h.Tick(100);   // 10 с паузи — сервер стоїть
        Assert.Equal(30, G(h).Sim!.S);
        Assert.Equal(0, G(h).Epoch);
        Assert.True(G(h).View(0) is not null);
        h.Input(0, "j", new { s = 40, ep = 0, e = Array.Empty<int[]>() });
        Assert.Equal(30, G(h).Sim!.S);
        Assert.True(h.Act(0, "resume").Ok);
        Assert.False(G(h).Paused);
        Assert.Equal(30, G(h).Real());
        h.Clock.AdvanceMs(1000);
        h.Input(0, "j", new { s = 90, ep = 0, e = Array.Empty<int[]>() });
        Assert.Equal(90, G(h).Sim!.S);
    }

    [Fact]
    public void Achievements_come_from_the_finished_string()
    {
        var h = Open();
        Assert.True(h.Act(0, "level", new { n = 1 }).Ok);
        var g = G(h);
        var (local, journal) = NamystoBot.Play(g.Level!, g.Seed, every: 12);
        Assert.True(h.Act(0, "go").Ok);
        Feed(h, journal, local.S + 30);
        var asked = h.Awards.Where(a => a.Reason.StartsWith("ach:", StringComparison.Ordinal)).Select(a => a.Reason).ToList();
        Assert.Equal(local.Misses == 0, asked.Contains("ach:namysto-clean"));
        Assert.Equal(local.MaxCombo >= Namysto.ComboAch, asked.Contains("ach:namysto-combo"));
        Assert.Equal(local.MaxHead >= local.End - Namysto.EdgeBeads * NamystoSim.Dm, asked.Contains("ach:namysto-edge"));
        foreach (var key in new[] { "namysto-all", "namysto-combo", "namysto-edge", "namysto-clean" })
            Assert.Contains(Hlechyky.Games.Economy.AchievementCatalog.All, a => a.Key == key);
    }

    // ---------- «Намисто дня» ----------

    [Fact]
    public void Daily_has_one_track_for_everybody_and_one_attempt()
    {
        var board = new NamystoDailyBoard(null);
        var services = Services(board: board);
        var h = Open("namysto-daily", services);
        var g = G(h);
        Assert.Equal("ready", g.Phase);
        Assert.False(h.Act(0, "level", new { n = 3 }).Ok);
        var day = Days.Today(h.Clock);
        Assert.Equal(NamystoTracks.Daily(day).N, g.Level!.N);
        Assert.Equal((uint)Days.Seed("namysto", day), g.Seed);
        var v = h.View(0);
        Assert.Equal("daily", v.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("daily").GetProperty("board").ValueKind);

        var (local, journal) = NamystoBot.Play(g.Level, g.Seed, every: 12);
        Assert.True(h.Act(0, "go").Ok);
        Feed(h, journal, local.S + 30);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var row = board.Of(day, "Оля")!;
        Assert.Equal((int)local.Score, row.Points);
        Assert.Contains(h.Awards, a => a.Reason == "daily:namysto-daily");
        var sc = h.Scores.Single();
        Assert.Equal((0.0, 1), (sc.Score, sc.Attempts!.Value));
        Assert.StartsWith("daily:namysto-daily:", sc.Key);
        v = h.View(0);
        Assert.Equal(1, v.GetProperty("daily").GetProperty("place").GetInt32());
        Assert.Contains("Намисто дня №", v.GetProperty("daily").GetProperty("share").GetString());

        // друга спроба — ні
        Assert.False(h.Rematch().Ok);
        Assert.Contains("вже зібрано", h.Reply.Message);

        // інша людина — той самий сід і та сама траса
        var h2 = new RoomHarness("namysto-daily", seed: 99, services: services);
        Assert.True(h2.Solo("Петро").Ok);
        Assert.Equal(g.Seed, G(h2).Seed);
        Assert.Equal(g.Level.N, G(h2).Level!.N);
    }

    [Fact]
    public void Closed_tab_pauses_the_string_and_the_same_room_resumes_it()
    {
        var h = Open();
        Go(h);
        h.Clock.AdvanceMs(1000);
        h.Input(0, "j", new { s = 60, ep = 0, e = new[] { new[] { 20, 2 } } });
        lock (h.Room.Sync) G(h).Unwatched();
        Assert.True(G(h).Paused);
        Assert.Equal(1, G(h).Epoch);
        h.Tick(600);   // хвилина — разок стоїть
        Assert.Equal(60, G(h).Sim!.S);
        Assert.True(h.Act(0, "resume").Ok);
        h.Clock.AdvanceMs(500);
        h.Input(0, "j", new { s = 90, ep = 1, e = Array.Empty<int[]>() });
        Assert.Equal(90, G(h).Sim!.S);
    }

    [Fact]
    public void Daily_attempt_survives_a_restart_paused_on_the_saved_journal()
    {
        var board = new NamystoDailyBoard(null);
        var h = Open("namysto-daily", Services(board: board));
        var g = G(h);
        var (_, journal) = NamystoBot.Play(g.Level!, g.Seed, every: 12, maxSteps: 900);
        Assert.True(h.Act(0, "go").Ok);
        Feed(h, journal, 900);
        var json = g.Save()!;
        var hash = g.Sim!.Hash();
        var s = g.Sim.S;

        // новий процес: та сама кімната з чистого — Start, потім Load
        var h2 = new RoomHarness("namysto-daily", seed: 3, services: Services(board: board));
        h2.Clock.UtcNow = h.Clock.UtcNow;
        Assert.True(h2.Solo("Оля").Ok);
        var g2 = G(h2);
        lock (h2.Room.Sync) g2.Load(json);
        Assert.Equal(Namysto.PhGo, g2.Phase);
        Assert.True(g2.Paused);
        Assert.Equal(s, g2.Sim!.S);
        Assert.Equal(hash, g2.Sim.Hash());
        Assert.Equal(g.Journal.Count, g2.Journal.Count);
        Assert.Equal(g.Epoch + 1, g2.Epoch);
        Assert.Equal(s, g2.Real());
        h2.Tick(50);
        Assert.Equal(s, g2.Sim.S);
        Assert.Equal(RoomStatus.Playing, h2.Room.Status);
        Assert.True(h2.Act(0, "resume").Ok);
        Assert.Null(board.Of(Days.Today(h2.Clock), "Оля"));

        // доведена до кінця спроба після ще одного перезапуску — лише підсумок
        var (local, full) = NamystoBot.Play(g.Level!, g.Seed, every: 12);
        var h3 = new RoomHarness("namysto-daily", seed: 3, services: Services(board: board));
        h3.Clock.UtcNow = h.Clock.UtcNow;
        Assert.True(h3.Solo("Оля").Ok);
        Assert.True(h3.Act(0, "go").Ok);
        Feed(h3, full, local.S + 30);
        Assert.Equal(RoomStatus.Finished, h3.Room.Status);
        var h4 = new RoomHarness("namysto-daily", seed: 4, services: Services(board: board));
        h4.Clock.UtcNow = h3.Clock.UtcNow;
        Assert.True(h4.Solo("Оля").Ok);
        lock (h4.Room.Sync) G(h4).Load(G(h3).Save()!);
        h4.Tick();
        Assert.Equal(RoomStatus.Finished, h4.Room.Status);
        Assert.Equal("over", Ph(h4));
        Assert.False(h4.Act(0, "go").Ok);
        Assert.Single(board.Top(Days.Today(h4.Clock)));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void A_whole_party_through_the_room_is_cheap()
    {
        var h = Open();
        Assert.True(h.Act(0, "level", new { n = 12 }).Ok == false);
        Assert.True(h.Act(0, "level", new { n = 1 }).Ok);
        var g = G(h);
        var (local, journal) = NamystoBot.Play(g.Level!, g.Seed, every: 12);
        Assert.True(h.Act(0, "go").Ok);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Feed(h, journal, local.S + 30);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} мс");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
    }
}
