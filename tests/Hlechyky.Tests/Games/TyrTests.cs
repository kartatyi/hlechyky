using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Ярмарковий тир (docs/games/specs/tyr.md): правила копії, стіл, бот, вечірка, «Тир дня».</summary>
[Collection(SerialPerf.Name)]
public class TyrTests
{
    // ---------- помічники ----------

    /// <summary>Добрий стрілець: б'є в центр найдорожчої живої хорошої мішені; нема в що — доливає барабан.</summary>
    static (string Action, object Payload)? Perfect(TyrBase g, int seat)
    {
        if (!g.Shooter(seat).Plays || g.Over) return null;
        var now = g.NowMs;
        if (now < 0 || now >= TyrCore.StandMs) return null;
        var me = g.Shooter(seat);
        if (now < me.LastT + TyrCore.MinGap || me.Reloading(now) || me.Smoked(now)) return null;
        TyrTarget? best = null;
        foreach (var t in g.Targets)
        {
            if (t.T0 > now - 60) break;
            if (!t.AliveAt(now) || t.End - now < 60 || me.HasHit(t.Id) || TyrCore.Bad(t.Kind)) continue;
            var cx = t.XAt(now);
            if (cx < 0 || cx > TyrCore.W) continue;
            if (best is null || TyrCore.Points[t.Kind] > TyrCore.Points[best.Kind]) best = t;
        }
        if (best is null)
            return me.Ammo < TyrCore.Drum ? ("reload", new { s = g.StandIndex, t = now }) : null;
        return ("shot", new { s = g.StandIndex, t = now, x = best.XAt(now), y = best.Y });
    }

    static Tyr Game(RoomHarness h) => (Tyr)h.Room.Game;

    static RoomHarness Table(int people, int seed = 7, object? options = null)
    {
        var h = new RoomHarness("tyr", options, seed);
        for (var i = 0; i < people; i++) Assert.True(h.Join($"стрілець{i}").Ok);
        return h;
    }

    /// <summary>Тикати до кінця партії, щотику даючи стріляти «добрим» місцям.</summary>
    static void Play(RoomHarness h, params int[] good)
    {
        var g = Game(h);
        var done = h.Finished.Count;
        for (var i = 0; i < 4000 && h.Finished.Count == done; i++)
        {
            foreach (var s in good)
                if (Perfect(g, s) is { } m) h.Input(s, m.Action, m.Payload);
            h.Tick();
        }
    }

    static void ToGo(RoomHarness h) => h.Tick(TyrCore.ReadyMs / 50 + 1);

    // ---------- ядро ----------

    [Fact]
    public void Schedule_is_deterministic_and_sane()
    {
        for (var stand = 0; stand < TyrCore.Stands; stand++)
        {
            var a = TyrCore.Schedule(stand, new Random(5));
            var b = TyrCore.Schedule(stand, new Random(5));
            Assert.Equal(a, b);
            Assert.True(a.Count >= 20, $"стенд {stand}: замало мішеней ({a.Count})");
            for (var i = 0; i < a.Count; i++)
            {
                var t = a[i];
                Assert.Equal(i, t.Id);
                Assert.True(t.T0 >= 0 && t.End <= TyrCore.StandMs && t.Dur > 0);
                if (i > 0) Assert.True(a[i - 1].T0 <= t.T0);
            }
            // Гніздо полиці не тримає дві мішені водночас.
            var shelf = a.Where(t => t.Vx == 0).GroupBy(t => (t.X, t.Y));
            foreach (var slot in shelf)
            {
                var list = slot.OrderBy(t => t.T0).ToList();
                for (var i = 1; i < list.Count; i++) Assert.True(list[i].T0 >= list[i - 1].End);
            }
        }
        var water = TyrCore.Schedule(TyrCore.Water, new Random(1));
        Assert.All(water, t => Assert.True(t.Kind is TyrCore.Duck or TyrCore.Keg && t.Vx != 0));
        var mix = TyrCore.Schedule(TyrCore.Mix, new Random(1));
        Assert.Contains(mix, t => t.Kind == TyrCore.Gold);
        Assert.Contains(mix, t => t.Kind == TyrCore.Duck);
        Assert.Contains(mix, t => t.Kind == TyrCore.Pot);
        Assert.NotEqual(TyrCore.Schedule(TyrCore.Mix, new Random(2)), mix);
    }

    static List<TyrTarget> One(params TyrTarget[] t) => [.. t];

    [Fact]
    public void Shot_hits_misses_and_double_hit()
    {
        var jug = new TyrTarget(0, TyrCore.Jug, 100, 1200, 300, 150, 0, 36);
        var me = new TyrShooter { Plays = true };
        me.NewStand(1);
        var tg = One(jug);
        Assert.Equal(new TyrShot(-1, 0, false), TyrCore.Shoot(me, tg, 0, 50, 300, 150));   // ще не вискочила
        Assert.Equal(5, me.Ammo);
        Assert.Equal(new TyrShot(0, 1, false), TyrCore.Shoot(me, tg, 0, 400, 330, 150));
        Assert.Equal(1, me.Score);
        Assert.Null(TyrCore.Shoot(me, tg, 0, 450, 300, 150));                                // зачасто
        Assert.Equal(new TyrShot(-1, 0, false), TyrCore.Shoot(me, tg, 0, 600, 300, 150));   // уже збита
        Assert.Equal(1, me.Score);
        Assert.Equal((3, 2, 1), (me.Ammo, me.Misses, me.Hits));
        Assert.Equal(new TyrShot(-1, 0, false), TyrCore.Shoot(me, tg, 0, 800, 300, 260));   // повз
    }

    [Fact]
    public void Moving_duck_is_hit_where_it_is_at_shot_time()
    {
        var duck = new TyrTarget(0, TyrCore.Duck, 0, 4000, -40, 420, 250, 36);
        var me = new TyrShooter { Plays = true };
        me.NewStand(1);
        Assert.Equal(-1, TyrCore.Shoot(me, One(duck), 1, 2000, -40, 420)!.Value.Target);   // там, де була
        Assert.Equal(new TyrShot(0, 2, false), TyrCore.Shoot(me, One(duck), 1, 2200, 510, 420));
        Assert.Equal(2, me.StandScore[1]);
    }

    [Fact]
    public void Keg_smokes_pot_costs_and_empty_drum_reloads()
    {
        var keg = new TyrTarget(0, TyrCore.Keg, 0, 1500, 300, 150, 0, 38);
        var pot = new TyrTarget(1, TyrCore.Pot, 0, 5000, 500, 150, 0, 38);
        var me = new TyrShooter { Plays = true };
        me.NewStand(2);
        var tg = One(keg, pot);
        Assert.Equal(new TyrShot(0, -3, false), TyrCore.Shoot(me, tg, 0, 100, 300, 150));
        Assert.True(me.Smoked(1500));
        Assert.Null(TyrCore.Shoot(me, tg, 0, 1500, 500, 150));                               // дим
        Assert.Equal(new TyrShot(1, -2, false), TyrCore.Shoot(me, tg, 0, 1600, 500, 150));
        Assert.Equal(-5, me.Score);
        Assert.Equal((1, 1), (me.Kegs, me.Pots));
        for (var t = 2000; me.Ammo > 0; t += 100) TyrCore.Shoot(me, tg, 0, t, 900, 590);
        var last = me.LastT;
        Assert.Equal(new TyrShot(-1, 0, true), TyrCore.Shoot(me, tg, 0, last + 100, 900, 590));
        Assert.True(me.Reloading(last + 900));
        Assert.Null(TyrCore.Shoot(me, tg, 0, last + 900, 900, 590));
        Assert.NotNull(TyrCore.Shoot(me, tg, 0, last + 1100, 900, 590));
        Assert.Equal(TyrCore.Drum - 1, me.Ammo);
        Assert.True(TyrCore.Reload(me, last + 1300));
        Assert.False(TyrCore.Reload(me, last + 1500));                                       // уже заряджає
    }

    // ---------- стіл ----------

    [Fact]
    public void Ready_then_go_and_illegal_shots_do_not_change_view()
    {
        var h = Table(2);
        Assert.True(h.Start().Ok);
        var g = Game(h);
        Assert.Equal("ready", h.View(0).GetProperty("phase").GetString());
        var before = h.View(0).GetRawText();
        Assert.False(h.Act(0, "shot", new { s = 0, t = 0, x = 100, y = 100 }).Ok);           // «Готуйсь»
        ToGo(h);
        Assert.Equal("go", h.View(0).GetProperty("phase").GetString());
        before = h.View(0).GetRawText();
        var now = g.NowMs;
        Assert.False(h.Act(0, "shot", new { s = 1, t = now, x = 100, y = 100 }).Ok);         // інший стенд
        Assert.False(h.Act(0, "shot", new { s = 0, t = now - 2000, x = 100, y = 100 }).Ok);  // запізнився
        Assert.False(h.Act(0, "shot", new { s = 0, t = now + 1000, x = 100, y = 100 }).Ok);  // з майбутнього
        Assert.False(h.Act(0, "shot", new { s = 0, x = 100, y = 100 }).Ok);                  // без часу
        Assert.False(h.Act(0, "shot", new { s = 0, t = now, x = 5000, y = 100 }).Ok);        // поза полем
        Assert.False(h.Act(0, "reload", new { s = 0, t = now }).Ok);                         // барабан повний
        Assert.False(h.Act(0, "dance", new { s = 0, t = now }).Ok);
        Assert.False(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);                       // партія йде
        Assert.Equal(before, h.View(0).GetRawText());
        Assert.True(h.Act(0, "shot", new { s = 0, t = now, x = 990, y = 590 }).Ok);          // промах — теж постріл
        Assert.Equal(TyrCore.Drum - 1, g.Shooter(0).Ammo);
        Assert.Equal(1, g.Shooter(0).Misses);
    }

    [Fact]
    public void Full_table_three_stands_good_shooter_wins_with_achievements()
    {
        var h = Table(2);
        h.Start();
        Play(h, 0);
        var f = Assert.Single(h.Finished);
        Assert.Equal([0], f.Result.Winners);
        var g = Game(h);
        Assert.True(g.Over);
        Assert.Equal(TyrCore.Stands - 1, g.StandIndex);
        Assert.Equal(g.Shooter(0).Score, f.Result.Scores![0]);
        Assert.Equal(0, f.Result.Scores[1]);
        Assert.True(g.Shooter(0).Score > 100, $"добрий стрілець набрав лише {g.Shooter(0).Score}");
        Assert.Equal(0, g.Shooter(0).Kegs + g.Shooter(0).Pots);
        Assert.Contains(h.Awards, a => a.Reason == "ach:tyr-sniper" && a.Nick == "стрілець0");
        Assert.Contains(h.Awards, a => a.Reason == "ach:tyr-dry" && a.Nick == "стрілець0");
        Assert.DoesNotContain(h.Awards, a => a.Nick == "стрілець1");
        var golds = g.Targets.Count(t => t.Kind == TyrCore.Gold);
        Assert.Equal(g.Shooter(0).StandGolds[TyrCore.Mix] == golds, h.Awards.Any(a => a.Reason == "ach:tyr-gold"));
        Assert.Equal("over", h.View(null).GetProperty("phase").GetString());
        Assert.False(h.Act(0, "shot", new { s = 2, t = 0, x = 1, y = 1 }).Ok);
    }

    [Fact]
    public void Views_are_the_same_for_everyone_and_frame_has_the_schedule_window()
    {
        var h = Table(3);
        h.Start();
        ToGo(h);
        h.Tick(100);
        var v0 = h.View(0).GetRawText();
        Assert.Equal(v0, h.View(1).GetRawText());
        Assert.Equal(v0, h.View(null).GetRawText());
        var f = h.View(null).GetProperty("frame");
        Assert.Equal(TyrBase.PhGo, f.GetProperty("ph").GetInt32());
        var now = f.GetProperty("now").GetInt32();
        Assert.InRange(now, 5000, 5200);
        Assert.Equal(TyrBase.Seats, f.GetProperty("p").GetArrayLength());
        Assert.Equal(6, f.GetProperty("p")[0].GetArrayLength());
        Assert.Equal(JsonValueKind.Null, f.GetProperty("p")[5].ValueKind);
        Assert.All(f.GetProperty("tg").EnumerateArray(), t =>
        {
            Assert.Equal(8, t.GetArrayLength());
            Assert.True(t[2].GetInt32() <= now + TyrBase.LookMs);
            Assert.True(t[2].GetInt32() + t[3].GetInt32() >= now - TyrBase.TailMs);
        });
        Assert.True(f.GetProperty("tg").GetArrayLength() > 0);
        var rules = h.View(null).GetProperty("rules");
        Assert.Equal(TyrCore.W, rules.GetProperty("w").GetInt32());
        Assert.Equal(5, rules.GetProperty("points").GetArrayLength());
    }

    [Fact]
    public void Same_seed_same_game()
    {
        string Run()
        {
            var h = Table(1, seed: 11);
            h.Act(0, LiveBots.Toggle, new { on = true });
            h.Start();
            h.Tick(700);
            return h.View(null).GetRawText();
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Leaving_mid_game_others_keep_shooting_last_one_ends_it()
    {
        var h = Table(3);
        h.Start();
        ToGo(h);
        h.Leave("стрілець2");
        Assert.Empty(h.Finished);
        h.Tick(5);
        h.Leave("стрілець1");
        Assert.Empty(h.Finished);
        h.Leave("стрілець0");
        Assert.Single(h.Finished);
    }

    [Fact]
    public void Rematch_brings_new_targets_and_clean_scores()
    {
        var h = Table(2);
        h.Start();
        Play(h, 0, 1);
        var first = Game(h).Targets.Select(t => (t.Kind, t.T0, t.X)).ToList();
        Assert.True(h.Rematch("стрілець0").Ok);
        h.Rematch("стрілець1");
        if (Game(h).Over) h.Start();
        var g = Game(h);
        Assert.False(g.Over);
        Assert.Equal(0, g.StandIndex);
        Assert.Equal(0, g.Shooter(0).Score + g.Shooter(1).Score);
        Play(h, 1);
        Assert.Equal(2, h.Finished.Count);
        Assert.NotEqual(first, g.Targets.Select(t => (t.Kind, t.T0, t.X)).ToList());
    }

    // ---------- соло з ботом ----------

    [Fact]
    public void Alone_needs_a_bot_and_bot_really_shoots_without_awards()
    {
        var h = Table(1, options: new { botlvl = "hard" });
        Assert.False(h.Start().Ok);
        Assert.True(h.View(0).GetProperty("botOffer").GetBoolean());
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.True(h.Start().Ok);
        var g = Game(h);
        Assert.Equal([1], g.Bots);
        Assert.Equal(LiveBots.Name, g.SeatBot(1));
        Play(h);   // людина стоїть
        var f = Assert.Single(h.Finished);
        Assert.True(g.Shooter(1).Score > 40, $"сильний бот набрав лише {g.Shooter(1).Score}");
        Assert.Empty(f.Result.Winners);
        Assert.StartsWith("🤖", f.Result.Verdict);
        Assert.Empty(h.Awards);
    }

    [Fact]
    public void Human_beats_bot_verdict()
    {
        var h = Table(1, options: new { botlvl = "easy" });
        h.Act(0, LiveBots.Toggle, new { on = true });
        h.Start();
        Play(h, 0);
        var f = Assert.Single(h.Finished);
        Assert.Equal([0], f.Result.Winners);
        Assert.Contains("легким ботом", f.Result.Verdict);
    }

    static double PartyAverage(LiveBots.Level level, int seeds = 4)
    {
        long sum = 0;
        var n = 0;
        for (var seed = 1; seed <= seeds; seed++)
        {
            var h = new PartyHarness("tyr", humans: 0, bots: 4, level: level, seed: seed);
            h.Start();
            var r = h.RunToEnd()!;
            foreach (var v in r.Scores.Values) { sum += v; n++; }
        }
        return (double)sum / n;
    }

    [Fact]
    public void Strong_bot_is_clearly_better_than_easy()
    {
        var easy = PartyAverage(LiveBots.Level.Easy);
        var normal = PartyAverage(LiveBots.Level.Normal);
        var hard = PartyAverage(LiveBots.Level.Hard);
        Assert.True(normal > easy * 1.2, $"легкий {easy:0.0}, звичайний {normal:0.0}");
        Assert.True(hard > normal * 1.15, $"звичайний {normal:0.0}, сильний {hard:0.0}");
        Assert.True(easy > 3, $"легкий бот майже не влучає: {easy:0.0}");
    }

    // ---------- вечірка ----------

    [Fact]
    public void Party_bots_only_finish_before_cap_with_all_scores()
    {
        var h = new PartyHarness("tyr", humans: 0, bots: 8, level: LiveBots.Level.Normal, seed: 3);
        h.Start();
        var g = (Tyr)h.Game;
        Assert.True(g.Party);
        Assert.Equal(1, g.StandCount);
        Assert.Equal(TyrCore.Mix, g.StandKind);
        h.Tick(1500);   // 30 с
        var mid = g.PartyScores();
        Assert.Equal(Enumerable.Range(0, 8), mid.Keys.Order());
        var r = h.RunToEnd()!;
        Assert.Equal(MinigameEnd.Finished, r.How);
        Assert.Equal(8, r.Scores.Count);
        Assert.True(r.Scores.Values.Max() > 0);
        Assert.Equal(0, h.Ctx.Muted);
        Assert.True(h.Clock.UtcNow - h.StartedAt <= TimeSpan.FromMilliseconds(h.Host.CapMs));
        Assert.True(h.Clock.UtcNow - h.StartedAt >= TimeSpan.FromMilliseconds(TyrCore.ReadyMs + TyrCore.StandMs));
    }

    [Fact]
    public void Party_good_human_is_on_top_and_afk_human_still_finishes()
    {
        var h = new PartyHarness("tyr", humans: 2, bots: 3, level: LiveBots.Level.Hard, seed: 9);
        h.Start();
        var g = (Tyr)h.Game;
        var r = h.RunToEnd(x =>
        {
            if (Perfect(g, 0) is { } m) x.Act(0, m.Action, m.Payload);
        })!;
        Assert.Equal(MinigameEnd.Finished, r.How);
        Assert.Equal([0], r.Winners);
        Assert.Equal(1, r.Places[0]);
        Assert.Equal(0, r.Scores[1]);                       // людина, що простояла
        Assert.All(new[] { 2, 3, 4 }, s => Assert.True(r.Scores[s] > 0));
        Assert.Equal(0, h.Ctx.Muted);
        Assert.False(string.IsNullOrWhiteSpace(g.Howto));
        Assert.Equal("party", h.View(0).GetProperty("mode").GetString());
        Assert.False(h.View(0).GetProperty("botOffer").GetBoolean());
    }

    [Fact]
    public void Tyr_is_in_party_pool_daily_is_not()
    {
        Assert.True(PartyPool.Has("tyr"));
        Assert.False(PartyPool.Has("tyr-daily"));
        var g = (IPartyMinigame)PartyPool.Create("tyr")!;
        Assert.InRange(g.PartyCapMs, TyrCore.ReadyMs + TyrCore.StandMs + TyrBase.GraceMs + 1000, 120_000);
        Assert.Equal((2, 8), (g.PartyMin, g.PartyMax));
    }

    [Fact]
    public void Human_leaving_bot_game_ends_it()
    {
        var h = Table(1);
        h.Act(0, LiveBots.Toggle, new { on = true });
        h.Start();
        ToGo(h);
        h.Tick(40);
        h.Leave("стрілець0");
        Assert.Single(h.Finished);
        Assert.Empty(h.Awards);
    }

    [Fact]
    public void Normal_table_is_not_party()
    {
        var h = Table(2);
        h.Start();
        Assert.False(Game(h).Party);
        Assert.Equal(3, Game(h).StandCount);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Thousand_ticks_with_eight_bots_are_fast()
    {
        var h = new PartyHarness("tyr", humans: 0, bots: 8, level: LiveBots.Level.Hard, seed: 4);
        h.Start();
        var sw = Stopwatch.StartNew();
        h.TickSub(1000);
        for (var i = 0; i < 50; i++) _ = Views.Json(h.Game.Frame()!);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} мс");
    }

    // ---------- Тир дня ----------

    [Fact]
    public void Daily_same_targets_for_everyone_one_attempt()
    {
        var a = new RoomHarness("tyr-daily", seed: 1);
        Assert.True(a.Solo("оля").Ok);
        var b = new RoomHarness("tyr-daily", seed: 999);
        Assert.True(b.Solo("петро").Ok);
        var ga = (TyrDaily)a.Room.Game;
        var gb = (TyrDaily)b.Room.Game;
        Assert.Equal(ga.Targets, gb.Targets);
        var g = ga;
        for (var i = 0; i < 4000 && a.Finished.Count == 0; i++)
        {
            if (Perfect(g, 0) is { } m) a.Input(0, m.Action, m.Payload);
            a.Tick();
        }
        Assert.Single(a.Finished);
        Assert.True(g.Reported);
        var score = Assert.Single(a.Scores);
        Assert.Equal(1, score.Attempts);
        Assert.StartsWith("daily:tyr-daily:", score.Key);
        Assert.Contains(a.Awards, x => x.Reason == "daily:tyr-daily");
        var v = a.View(0);
        Assert.Equal("daily", v.GetProperty("mode").GetString());
        Assert.Contains("Тир дня", v.GetProperty("daily").GetProperty("share").GetString());
        // «Ще раз» того ж дня — підсумок, а не нова спроба.
        a.Rematch("оля");
        a.Tick(5);
        Assert.Single(a.Scores);
        Assert.Equal(g.Shooter(0).Score, ((TyrDaily)a.Room.Game).Shooter(0).Score);
    }

    [Fact]
    public void Daily_save_load_keeps_the_result()
    {
        var a = new RoomHarness("tyr-daily", seed: 1);
        a.Solo("оля");
        var g = (TyrDaily)a.Room.Game;
        Assert.Null(new TyrDaily().Save());
        for (var i = 0; i < 4000 && a.Finished.Count == 0; i++)
        {
            if (Perfect(g, 0) is { } m) a.Input(0, m.Action, m.Payload);
            a.Tick();
        }
        string json;
        lock (a.Room.Sync) json = g.Save()!;
        var back = new TyrDaily();
        back.Load(json);
        Assert.True(back.Reported);
        Assert.True(back.Over);
        Assert.Equal(g.Day, back.Day);
        Assert.Equal(g.Shooter(0).Score, back.Shooter(0).Score);
        Assert.Equal(g.Shooter(0).Hits, back.Shooter(0).Hits);
        Assert.Equal(json, back.Save());
    }

    [Fact]
    public void Daily_board_orders_by_points_first_result_stays()
    {
        var b = new TyrDailyBoard(null);
        b.Record("2026-10-06", "Оля", 50, 40, 50, DateTimeOffset.UnixEpoch);
        b.Record("2026-10-06", "Петро", 70, 50, 70, DateTimeOffset.UnixEpoch);
        b.Record("2026-10-06", "оля", 99, 99, 99, DateTimeOffset.UnixEpoch);
        Assert.Equal(["Петро", "Оля"], b.Top("2026-10-06").Select(r => r.Nick));
        Assert.Equal(50, ((IDailyPoints)b).Points("2026-10-06", "ОЛЯ"));
        Assert.Equal("tyr-daily", ((IDailyPoints)b).Game);
    }
}
