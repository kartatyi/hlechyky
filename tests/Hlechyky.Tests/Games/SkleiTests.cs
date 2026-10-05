using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Склей глек»: розбивка (SkleiCut) — напряму; партія, картинки, кінці, бот і вечірка — через кімнату й PartyHarness,
/// як гратимуть люди; колода (свої картинки, +1 автору, ачівки) — на SkleiDeck без бази, з тимчасовою текою.
/// </summary>
[Collection(SerialPerf.Name)]
public class SkleiTests(ITestOutputHelper output)
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марко", "Соня", "Тарас", "Леся"];

    static Sklei G(RoomHarness h) => (Sklei)h.Room.Game;

    static RoomHarness Table(int people, object? options = null, int seed = 7, IServiceProvider? services = null)
    {
        var h = new RoomHarness("sklei", options, seed, services);
        for (var i = 0; i < people; i++) Assert.True(h.Join(Nicks[i]).Ok);
        return h;
    }

    static RoomHarness Started(int people, object? options = null, int seed = 7, IServiceProvider? services = null)
    {
        var h = Table(people, options, seed, services);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static void ToGo(RoomHarness h)
    {
        for (var i = 0; i < 200 && G(h).Phase != Sklei.PhGo; i++) h.Tick();
        Assert.Equal(Sklei.PhGo, G(h).Phase);
    }

    static int N(Sklei g, int seat) => (int)Math.Round(Math.Sqrt(g.PiecesOf(seat)));

    static int Turns(Sklei g, int seat, int k) => g.CurrentLevel.Rotate ? g.Cut(N(g, seat)).Needed(k) : 0;

    /// <summary>Покласти черепок «рукою»: перед кожним — пауза <see cref="Sklei.PutGapMs"/>, як у живого гравця.</summary>
    static ActResult Put(RoomHarness h, int seat, int k, int? t = null)
    {
        h.Clock.AdvanceMs(Sklei.PutGapMs);
        return h.Act(seat, "put", new { k, c = k, r = 0, t = t ?? Turns(G(h), seat, k) });
    }

    static void Solve(RoomHarness h, int seat)
    {
        var g = G(h);
        for (var k = 0; k < g.PiecesOf(seat); k++) Assert.True(Put(h, seat, k).Ok);
    }

    // ---------- розбивка ----------

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    public void Cut_shape_and_determinism(int n)
    {
        var a = new SkleiCut(n, 123, rotate: true);
        var b = new SkleiCut(n, 123, rotate: true);
        var c = new SkleiCut(n, 124, rotate: true);
        Assert.Equal(a.V, b.V);
        Assert.Equal(a.Tray, b.Tray);
        Assert.NotEqual(a.V, c.V);
        Assert.Equal((n + 1) * (n + 1) * 2, a.V.Length);
        Assert.Equal((n - 1) * n * 2, a.Eh.Length);
        Assert.Equal((n - 1) * n * 2, a.Ev.Length);
        Assert.Equal(n * n * 3, a.Tray.Length);
        for (var j = 0; j <= n; j++)
            for (var i = 0; i <= n; i++)
            {
                var at = (j * (n + 1) + i) * 2;
                if (i == 0 || i == n) Assert.Equal(0, a.V[at]);          // зовнішній край — рівний
                if (j == 0 || j == n) Assert.Equal(0, a.V[at + 1]);
                Assert.InRange(a.V[at], -SkleiCut.VertexJitter, SkleiCut.VertexJitter);
            }
        Assert.All(a.Eh.Concat(a.Ev), o => Assert.InRange(o, -SkleiCut.CrackJitter, SkleiCut.CrackJitter));
        for (var k = 0; k < n * n; k++)
        {
            Assert.InRange(a.Tray[k * 3], 0, 1000);
            Assert.InRange(a.Tray[k * 3 + 1], 0, 1000);
            Assert.InRange(a.R0(k), 0, 3);
            Assert.Equal((4 - a.R0(k)) % 4, a.Needed(k));
        }
        Assert.Contains(Enumerable.Range(0, n * n), k => a.R0(k) != 0);
        var flat = new SkleiCut(n, 123, rotate: false);
        Assert.All(Enumerable.Range(0, n * n), k => Assert.Equal(0, flat.R0(k)));
        // Черепки не лягають один на одного: центри різні.
        Assert.Equal(n * n, Enumerable.Range(0, n * n).Select(k => (a.Tray[k * 3], a.Tray[k * 3 + 1])).Distinct().Count());
    }

    [Fact]
    public void Builtin_svgs_exist_for_every_picture()
    {
        var dir = Hlechyky.Paths.Resolve("web/games/sklei");
        Assert.True(SkleiBuiltin.All.Count >= 12);
        foreach (var (id, _) in SkleiBuiltin.All)
        {
            var path = Path.Combine(dir, id + ".svg");
            Assert.True(File.Exists(path), path);
            var text = File.ReadAllText(path);
            Assert.StartsWith("<svg", text);
            Assert.Contains("viewBox=\"0 0 600 600\"", text);
            Assert.DoesNotContain("href=\"http", text);                  // жодних зовнішніх ресурсів
        }
    }

    // ---------- лобі й старт ----------

    [Fact]
    public void Info_and_options()
    {
        var h = Table(1);
        var info = h.Room.Game.Info;
        Assert.Equal("sklei", info.Id);
        Assert.Equal(GameGroup.Live, info.Group);
        Assert.Equal(1, info.MinPlayers);
        Assert.Equal(8, info.MaxPlayers);
        Assert.Equal(100, info.TickMs);
        Assert.Equal(StartMode.ByHost, info.Start);
        Assert.Contains(info.Options!, o => o.Key == "level");
        Assert.Contains(info.Options!, o => o.Key == "pics");
        Assert.Contains(info.Options!, o => o.Key == "botlvl");
        Assert.DoesNotContain(info.Options!, o => o.Key is "party" or "bots");
        Assert.IsAssignableFrom<IPartyMinigame>(h.Room.Game);
        Assert.True(PartyPool.Has("sklei"));
    }

    [Fact]
    public void Alone_without_bot_does_not_start()
    {
        var h = Table(1);
        Assert.False(h.Start().Ok);
        Assert.Equal(LiveBots.AloneText, h.Reply.Message);
        var v = h.View(0);
        Assert.True(v.GetProperty("botOffer").GetBoolean());
        Assert.Equal("lobby", v.GetProperty("ph").GetString());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("cut").ValueKind);
    }

    [Fact]
    public void Ready_then_go_and_put_before_go_fails()
    {
        var h = Started(2);
        var g = G(h);
        Assert.Equal(Sklei.PhReady, g.Phase);
        Assert.Equal(1, g.PictureNo);
        Assert.False(h.Act(0, "put", new { k = 0, c = 0, r = 0, t = Turns(g, 0, 0) }).Ok);
        Assert.Equal(0, g.Placed(0));
        h.Tick(29);
        Assert.Equal(Sklei.PhReady, g.Phase);
        h.Tick(2);
        Assert.Equal(Sklei.PhGo, g.Phase);
        Assert.Equal(16, g.PiecesOf(0));          // «звично» за замовчуванням
    }

    [Fact]
    public void Wrong_puts_change_nothing()
    {
        var h = Started(2);
        ToGo(h);
        var g = G(h);
        var before = Views.Text(g.View(0));
        var k = Enumerable.Range(0, 16).First(i => Turns(g, 0, i) != 0);
        Assert.False(h.Act(0, "put", new { k, c = (k + 1) % 16, r = 0, t = Turns(g, 0, k) }).Ok);   // не в ту клітинку
        Assert.False(h.Act(0, "put", new { k, c = k, r = 0, t = 0 }).Ok);                         // не повернутий
        Assert.False(h.Act(0, "put", new { k, c = k, r = 1, t = Turns(g, 0, k) }).Ok);            // кут не той
        Assert.False(h.Act(0, "put", new { k = 99, c = 99, r = 0, t = 0 }).Ok);                   // нема такого
        Assert.False(h.Act(0, "put", new { k = -1, c = -1, r = 0, t = 0 }).Ok);
        Assert.False(h.Act(0, "put", new { k = "1", c = 1, r = 0, t = 0 }).Ok);                   // не число
        Assert.False(h.Act(0, "put", new { k = 1 }).Ok);
        Assert.False(h.Act(0, "put", new { k, c = k, r = 0, t = Turns(g, 0, k) + 400 }).Ok);      // абсурдні тапи
        Assert.False(h.Act(0, "dance").Ok);
        Assert.False(g.Act(5, "put", Views.Payload(new { k = 0, c = 0, r = 0, t = 0 })).Ok);      // не грає
        Assert.Equal(before, Views.Text(g.View(0)));
        Assert.True(Put(h, 0, k).Ok);
        Assert.Equal(1, g.Placed(0));
        Assert.False(Put(h, 0, k).Ok);                                                            // удруге — ні
        Assert.Equal(1, g.Placed(0));
        Assert.True(Put(h, 0, k == 0 ? 1 : 0, Turns(g, 0, k == 0 ? 1 : 0) + 4).Ok);               // зайве коло — можна, але «хибно»
    }

    [Fact]
    public void Three_pictures_sum_of_points_and_finish()
    {
        var h = Started(2);
        var g = G(h);
        var keys = new List<string>();
        for (var p = 1; p <= 3; p++)
        {
            ToGo(h);
            Assert.Equal(p, g.PictureNo);
            keys.Add(g.Picture.Key);
            Solve(h, 0);
            Assert.True(g.DoneMs(0) >= 0);
            h.Tick(5);
            Solve(h, 1);
            h.Tick();
            if (p < 3) Assert.Equal(Sklei.PhPause, g.Phase);
        }
        Assert.Equal(Sklei.PhOver, g.Phase);
        Assert.Equal(3, keys.Distinct().Count());                     // без повторів у партії
        Assert.Equal(30, g.Total(0));
        Assert.Equal(21, g.Total(1));
        var fin = Assert.Single(h.Finished);
        Assert.Equal([0], fin.Result.Winners);
        Assert.Equal(30, fin.Result.Scores![0]);
        Assert.Equal(21, fin.Result.Scores![1]);
        var v = h.View(1);
        Assert.Equal(3, v.GetProperty("history").GetArrayLength());
        Assert.Equal([0], v.GetProperty("winners").EnumerateArray().Select(e => e.GetInt32()));
        Assert.False(Put(h, 0, 0).Ok);
    }

    [Fact]
    public void Picture_ends_by_limit_and_nobody_wins_with_nothing()
    {
        var h = Started(2, new { level = "easy" });
        var g = G(h);
        for (var p = 0; p < 3; p++)
        {
            ToGo(h);
            h.Tick(Sklei.Levels[0].LimitMs / 100 + 1);
            Assert.NotEqual(Sklei.PhGo, g.Phase);
        }
        h.Tick(70);
        Assert.Equal(Sklei.PhOver, g.Phase);
        var fin = Assert.Single(h.Finished);
        Assert.Empty(fin.Result.Winners);
        Assert.Equal(0, g.Total(0));
    }

    [Fact]
    public void After_first_finisher_others_have_a_minute()
    {
        var h = Started(2);
        var g = G(h);
        ToGo(h);
        Put(h, 1, 0);                                  // Петро встиг один черепок
        Solve(h, 0);
        h.Tick(Sklei.AfterFirstMs / 100 - 5);
        Assert.Equal(Sklei.PhGo, g.Phase);
        h.Tick(10);
        Assert.Equal(Sklei.PhPause, g.Phase);
        Assert.Equal(10, g.Total(0));
        Assert.Equal(3, g.Total(1));                   // друге місце (7), але не склав — половина
    }

    [Fact]
    public void Phone_gets_at_most_sixteen_from_next_picture()
    {
        var h = Table(2, new { level = "hard" });
        Assert.True(h.Act(1, "dev", new { phone = true }).Ok);
        Assert.False(h.Act(1, "dev", new { phone = "так" }).Ok);
        Assert.True(h.Start().Ok);
        var g = G(h);
        Assert.Equal(25, g.PiecesOf(0));
        Assert.Equal(16, g.PiecesOf(1));
        var v1 = h.View(1);
        Assert.Equal(4, v1.GetProperty("n").GetInt32());
        Assert.Equal(4, v1.GetProperty("cut").GetProperty("n").GetInt32());
        Assert.False(v1.GetProperty("hint").GetBoolean());          // важко на телефоні — без контуру
        Assert.Equal(5, h.View(0).GetProperty("cut").GetProperty("n").GetInt32());
        ToGo(h);
        Assert.True(h.Act(0, "dev", new { phone = true }).Ok);      // посеред партії — мовчки «так», але нічого не міняє
        Assert.Equal(25, g.PiecesOf(0));
        Solve(h, 0);
        Solve(h, 1);
        h.Tick(70);
        ToGo(h);
        Assert.Equal(2, g.PictureNo);
        Assert.Equal(25, g.PiecesOf(0));                           // і з наступної картинки теж: ПК лишився ПК
    }

    [Fact]
    public void Puts_faster_than_glue_are_refused()
    {
        var h = Started(2);
        ToGo(h);
        var g = G(h);
        // скрипт із консолі: усі 16 правильних put за один тик — приросте лише перший
        var ok = Enumerable.Range(0, 16).Count(k => h.Act(0, "put", new { k, c = k, r = 0, t = Turns(g, 0, k) }).Ok);
        Assert.Equal(1, ok);
        Assert.Equal(1, g.Placed(0));
        var res = h.Act(0, "put", new { k = 1, c = 1, r = 0, t = Turns(g, 0, 1) });
        Assert.False(res.Ok);
        Assert.Contains("Повільніше", res.Message);
        h.Clock.AdvanceMs(Sklei.PutGapMs - 1);
        Assert.False(h.Act(0, "put", new { k = 1, c = 1, r = 0, t = Turns(g, 0, 1) }).Ok);
        h.Clock.AdvanceMs(1);
        Assert.True(h.Act(0, "put", new { k = 1, c = 1, r = 0, t = Turns(g, 0, 1) }).Ok);
        // хибний put темпу не «з'їдає»: інший гравець і відмова — не рахуються
        Assert.True(h.Act(1, "put", new { k = 0, c = 0, r = 0, t = Turns(g, 1, 0) }).Ok);
        Assert.Equal(2, g.Placed(0));
        Assert.Equal(1, g.Placed(1));
    }

    [Fact]
    public void Phone_on_hard_is_ranked_per_piece_against_pc()
    {
        var h = Table(2, new { level = "hard" });
        Assert.True(h.Act(1, "dev", new { phone = true }).Ok);
        Assert.True(h.Start().Ok);
        ToGo(h);
        var g = G(h);
        // телефон: 16 по 1,2 с — склав за 19,2 с; ПК потім 25 по 0,3 с — склав за 26,7 с, тобто пізніше,
        // але на черепок швидше (1,07 с проти 1,2 с) — він і перший
        for (var k = 0; k < 16; k++) { h.Clock.AdvanceMs(1200); Assert.True(h.Act(1, "put", new { k, c = k, r = 0, t = Turns(g, 1, k) }).Ok); }
        for (var k = 0; k < 25; k++) { h.Clock.AdvanceMs(Sklei.PutGapMs); Assert.True(h.Act(0, "put", new { k, c = k, r = 0, t = Turns(g, 0, k) }).Ok); }
        h.Tick(70);
        Assert.Equal(10, g.Total(0));
        Assert.Equal(7, g.Total(1));
    }

    [Fact]
    public void Phone_on_hard_no_restorer()
    {
        var h = Table(2, new { level = "hard" });
        Assert.True(h.Act(0, "dev", new { phone = true }).Ok);
        Assert.True(h.Start().Ok);
        var g = G(h);
        for (var p = 0; p < 3; p++) { ToGo(h); Solve(h, 0); Solve(h, 1); h.Tick(70); }
        Assert.Single(h.Finished);
        Assert.Equal(16, g.PiecesOf(0));
        Assert.DoesNotContain(h.Awards, a => a.Nick == Nicks[0] && a.Reason == "ach:sklei-restorer");
        Assert.Contains(h.Awards, a => a.Nick == Nicks[1] && a.Reason == "ach:sklei-restorer");
    }

    [Fact]
    public void Easy_has_no_rotations()
    {
        var h = Started(2, new { level = "easy" });
        ToGo(h);
        var g = G(h);
        Assert.Equal(9, g.PiecesOf(0));
        Assert.False(h.Act(0, "put", new { k = 0, c = 0, r = 0, t = 4 }).Ok);
        Assert.True(h.Act(0, "put", new { k = 0, c = 0, r = 0, t = 0 }).Ok);
        Assert.All(Enumerable.Range(0, 9), k => Assert.Equal(0, g.Cut(3).R0(k)));
    }

    // ---------- види ----------

    [Fact]
    public void Views_hide_others_sets_and_frame_is_public_progress()
    {
        var h = Started(3);
        ToGo(h);
        Put(h, 1, 3);
        Put(h, 1, 5);
        var v0 = h.View(0);
        Assert.Empty(v0.GetProperty("me").GetProperty("placed").EnumerateArray());
        var v1 = h.View(1);
        Assert.Equal([3, 5], v1.GetProperty("me").GetProperty("placed").EnumerateArray().Select(e => e.GetInt32()));
        var watcher = h.View(null);
        Assert.Equal(JsonValueKind.Null, watcher.GetProperty("me").ValueKind);
        Assert.DoesNotContain("\"placed\":[3,5]", Views.Text(G(h).View(0)));
        Assert.DoesNotContain("\"placed\":[3,5]", Views.Text(G(h).View(null)));
        var p1 = v0.GetProperty("players").EnumerateArray().Single(p => p.GetProperty("seat").GetInt32() == 1);
        Assert.Equal(2, p1.GetProperty("placed").GetInt32());
        Assert.Equal(16, p1.GetProperty("of").GetInt32());
        var pic = v0.GetProperty("pic");
        Assert.False(string.IsNullOrEmpty(pic.GetProperty("url").GetString()));
        Assert.Equal(16 * 3, v0.GetProperty("cut").GetProperty("tray").GetArrayLength());
        var f = Views.Json(G(h).Frame());
        Assert.Equal(3, f.GetProperty("p").GetArrayLength());
        Assert.Equal([1, 2, 16, -1], f.GetProperty("p")[1].EnumerateArray().Select(e => e.GetInt32()));
        Assert.False(f.TryGetProperty("tray", out _));
        Assert.True(Views.WireBytes(G(h).View(0)) < 4000, "вид малий");
    }

    [Fact]
    public void Same_seed_same_pictures_and_cuts()
    {
        string Sig(int seed)
        {
            var h = Started(2, seed: seed);
            var g = G(h);
            return g.Picture.Key + "|" + string.Join(",", g.Cut(4).V) + "|" + string.Join(",", g.Cut(4).Tray);
        }
        Assert.Equal(Sig(11), Sig(11));
        Assert.NotEqual(Sig(11), Sig(12));
    }

    [Fact]
    public void Tick_sends_view_only_when_something_changed()
    {
        var h = Started(2);
        ToGo(h);
        var g = G(h);
        Assert.Equal(TickResult.None, g.Tick());
        Put(h, 0, 0);
        Assert.Equal(TickResult.Both, g.Tick());
        Assert.Equal(TickResult.None, g.Tick());
    }

    // ---------- вихід, рематч ----------

    [Fact]
    public void Leave_mid_game_keeps_two_playing_then_ends()
    {
        var h = Started(3);
        ToGo(h);
        Solve(h, 2);
        h.Leave(Nicks[0]);
        Assert.Empty(h.Finished);
        Assert.NotEqual(Sklei.PhOver, G(h).Phase);
        h.Leave(Nicks[1]);
        var fin = Assert.Single(h.Finished);
        Assert.Equal([2], fin.Result.Winners);
    }

    [Fact]
    public void Rematch_starts_clean()
    {
        var h = Started(2);
        var g = G(h);
        for (var p = 0; p < 3; p++) { ToGo(h); Solve(h, 1); Solve(h, 0); h.Tick(70); }
        Assert.Equal(Sklei.PhOver, g.Phase);
        Assert.True(h.Rematch().Ok, h.Reply.Message);
        g = G(h);
        Assert.Equal(Sklei.PhReady, g.Phase);
        Assert.Equal(1, g.PictureNo);
        Assert.All(Enumerable.Range(0, 2), s => Assert.Equal(0, g.Total(s)));
        Assert.Equal(0, h.View(0).GetProperty("history").GetArrayLength());
    }

    // ---------- ачівки ----------

    [Fact]
    public void Restorer_for_clean_hard_match_only()
    {
        var h = Started(2, new { level = "hard" });
        var g = G(h);
        for (var p = 0; p < 3; p++)
        {
            ToGo(h);
            Solve(h, 0);
            for (var k = 0; k < g.PiecesOf(1); k++) Assert.True(Put(h, 1, k, Turns(g, 1, k) + (k == 0 && p == 1 ? 4 : 0)).Ok);
            h.Tick(70);
        }
        Assert.Single(h.Finished);
        Assert.Contains(h.Awards, a => a.Nick == Nicks[0] && a.Reason == "ach:sklei-restorer");
        Assert.DoesNotContain(h.Awards, a => a.Nick == Nicks[1]);
        Assert.NotNull(Hlechyky.Games.Economy.AchievementCatalog.Get("sklei-restorer"));
        Assert.NotNull(Hlechyky.Games.Economy.AchievementCatalog.Get("sklei-friends"));
        Assert.NotNull(Hlechyky.Games.Economy.AchievementCatalog.Get("sklei-author"));
    }

    [Fact]
    public void No_restorer_on_normal()
    {
        var h = Started(2);
        for (var p = 0; p < 3; p++) { ToGo(h); Solve(h, 0); Solve(h, 1); h.Tick(70); }
        Assert.Single(h.Finished);
        Assert.Empty(h.Awards);
    }

    // ---------- соло з ботом ----------

    static RoomHarness WithBot(string lvl, object? level = null, int seed = 7)
    {
        var h = new RoomHarness("sklei", level is null ? new { botlvl = lvl } : new { botlvl = lvl, level }, seed);
        Assert.True(h.Join(Nicks[0]).Ok);
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    [Fact]
    public void Bot_plays_and_wins_against_idle_human()
    {
        var h = WithBot("normal");
        var g = G(h);
        var bot = Assert.Single(g.Bots);
        Assert.Equal(LiveBots.Name, g.SeatBot(bot));
        ToGo(h);
        h.Tick(100);
        Assert.InRange(g.Placed(bot), 1, 6);                  // ~3,5 с на черепок
        for (var i = 0; i < 6000 && g.Phase != Sklei.PhOver; i++) h.Tick();
        var fin = Assert.Single(h.Finished);
        Assert.Empty(fin.Result.Winners);
        Assert.StartsWith("🤖", fin.Result.Verdict);
        Assert.Equal(30, g.Total(bot));
        Assert.Empty(h.Awards);
    }

    [Fact]
    public void Human_beats_easy_bot_and_gets_no_awards()
    {
        var h = WithBot("easy", "hard");
        var g = G(h);
        for (var p = 0; p < 3; p++)
        {
            ToGo(h);
            h.Tick(20);
            Solve(h, 0);
            for (var i = 0; i < 4000 && g.Phase == Sklei.PhGo; i++) h.Tick();
            for (var i = 0; i < 100 && g.Phase == Sklei.PhPause; i++) h.Tick();
        }
        var fin = Assert.Single(h.Finished);
        Assert.Equal([0], fin.Result.Winners);
        Assert.Contains("перемога над легким ботом", fin.Result.Verdict);
        Assert.Empty(h.Awards);                                // чистий «важко», але з ботом — без ачівок
    }

    [Fact]
    public void Strong_bot_is_faster_than_easy()
    {
        int Done(string lvl, int seed)
        {
            var h = WithBot(lvl, seed: seed);
            var g = G(h);
            ToGo(h);
            for (var i = 0; i < 3000 && g.DoneMs(g.Bots[0]) < 0; i++) h.Tick();
            return g.DoneMs(g.Bots[0]);
        }
        var easy = Enumerable.Range(1, 4).Select(s => Done("easy", s)).Average();
        var hard = Enumerable.Range(1, 4).Select(s => Done("hard", s)).Average();
        output.WriteLine($"16 черепків: легкий {easy / 1000:0.0} с, сильний {hard / 1000:0.0} с");
        Assert.True(hard < easy * 0.5, $"{hard} vs {easy}");
        Assert.InRange(hard, 20_000, 50_000);                  // сильний — важко, але не миттєво
    }

    [Fact]
    public void Friend_sits_down_bot_leaves()
    {
        var h = new RoomHarness("sklei", null, 3);
        h.Join(Nicks[0]);
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        h.Join(Nicks[1]);
        Assert.True(h.Start().Ok);
        Assert.Empty(G(h).Bots);
    }

    [Fact]
    public void Human_leaving_bot_game_ends_it()
    {
        var h = WithBot("normal");
        ToGo(h);
        h.Leave(Nicks[0]);
        var fin = Assert.Single(h.Finished);
        Assert.Empty(fin.Result.Winners);
    }

    // ---------- вечірка ----------

    [Fact]
    public void Party_bots_only_finish_before_cap_with_all_scores()
    {
        var h = new PartyHarness("sklei", humans: 0, bots: 4, level: LiveBots.Level.Hard, seed: 5);
        h.Start();
        var g = (Sklei)h.Game;
        Assert.Equal(16, g.PiecesOf(0));
        Assert.Equal(1, g.PicturesTotal);
        var r = h.RunToEnd();
        Assert.NotNull(r);
        Assert.Equal(MinigameEnd.Finished, r!.How);
        Assert.Equal(4, r.Scores.Count);
        Assert.All(r.Scores.Values, v => Assert.InRange(v, 0, 1000));
        Assert.True(h.Clock.UtcNow - h.StartedAt <= TimeSpan.FromMilliseconds(h.Host.CapMs));
        Assert.Equal(0, h.Ctx.Muted);
        Assert.Contains(g.Picture.Kind, new[] { SkleiKind.Builtin, SkleiKind.Art });
    }

    [Fact]
    public void Party_idle_humans_end_by_time_not_cap()
    {
        var h = new PartyHarness("sklei", humans: 3, bots: 0, seed: 9);
        h.Start();
        var r = h.RunToEnd();
        Assert.Equal(MinigameEnd.Finished, r!.How);
        Assert.All(r.Scores.Values, v => Assert.Equal(0, v));
        Assert.True(h.Clock.UtcNow - h.StartedAt <= TimeSpan.FromMilliseconds(Sklei.PartyReadyMs + Sklei.PartyPlayMs + 200));
    }

    [Fact]
    public void Party_good_human_is_on_top_and_scores_mid_way_cover_everyone()
    {
        var h = new PartyHarness("sklei", humans: 1, bots: 3, level: LiveBots.Level.Hard, seed: 6);
        h.Start();
        var g = (Sklei)h.Game;
        int k = 0, ticks = 0;
        var r = h.RunToEnd(p =>
        {
            // «людина» кладе черепок раз на секунду (50 тиків батька), щойно почали
            if (g.Phase == Sklei.PhGo && k < 16 && ++ticks % 50 == 0)
            {
                var res = p.Act(0, "put", new { k, c = k, r = 0, t = g.Cut(4).Needed(k) });
                Assert.True(res.Ok, res.Message);
                k++;
            }
            if (k == 8)
            {
                var mid = g.PartyScores();
                Assert.Equal(4, mid.Count);
                Assert.Equal(8, mid[0]);
            }
        });
        Assert.Equal(MinigameEnd.Finished, r!.How);
        Assert.Equal(1, r.Places[0]);
        Assert.InRange(r.Scores[0], 980, 990);              // ~16 с
        Assert.Equal([0], r.Winners);
        Assert.Equal(0, h.Ctx.Muted);
    }

    [Fact]
    public void Party_eight_seats_and_no_bot_toggle()
    {
        var h = new PartyHarness("sklei", humans: 2, bots: 6, level: LiveBots.Level.Easy, seed: 2);
        h.Start();
        Assert.False(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        var r = h.RunToEnd();
        Assert.Equal(8, r!.Scores.Count);
        Assert.Equal(8, r.Places.Length);
        var v = h.View(0);
        Assert.True(v.GetProperty("party").GetBoolean());
        Assert.False(v.GetProperty("botOffer").GetBoolean());
    }

    // ---------- колода ----------

    sealed class Wallet
    {
        public Dictionary<string, int> Balance { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Nick, int N, string Ref)> Grants { get; } = [];
        public List<(string Nick, string Key)> Unlocks { get; } = [];
    }

    static (SkleiDeck Deck, Wallet W, string Dir) NewDeck()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sklei-test-" + Guid.NewGuid().ToString("n")[..8]);
        var w = new Wallet();
        var deck = new SkleiDeck(null, dir, new FakeClock())
        {
            Spend = (nick, n, _, _) =>
            {
                if (w.Balance.GetValueOrDefault(nick) < n) return false;
                w.Balance[nick] -= n;
                return true;
            },
            Grant = (nick, n, _, r) => w.Grants.Add((nick, n, r)),
            Unlock = (nick, key) => w.Unlocks.Add((nick, key)),
        };
        return (deck, w, dir);
    }

    /// <summary>Найменший «PNG», який пропускає перевірка заголовка: підпис + IHDR із розмірами.</summary>
    static byte[] Png(int w, int h, int salt = 0)
    {
        var b = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), (uint)h);
        b[40] = (byte)salt;
        return b;
    }

    [Fact]
    public void Upload_checks_pays_hides_and_admin_removes()
    {
        var (deck, w, dir) = NewDeck();
        try
        {
            w.Balance["Оля"] = 1000;
            Assert.False(deck.Upload("гість", false, Png(500, 500)).Ok);
            Assert.False(deck.Upload("Оля", true, []).Ok);
            Assert.False(deck.Upload("Оля", true, "<svg/>"u8.ToArray()).Ok);
            Assert.False(deck.Upload("Оля", true, Png(1024, 1024)).Ok);          // завелика
            Assert.False(deck.Upload("Оля", true, Png(500, 300)).Ok);            // не квадрат
            Assert.False(deck.Upload("Оля", true, new byte[SkleiDeck.MaxBytes + 1]).Ok);
            Assert.Equal(1000, w.Balance["Оля"]);
            var ok = deck.Upload("Оля", true, Png(512, 512));
            Assert.True(ok.Ok, ok.Message);
            Assert.Equal(600, w.Balance["Оля"]);
            Assert.True(File.Exists(Path.Combine(dir, ok.Pic!.File)));
            Assert.NotNull(deck.Resolve(ok.Pic.File));
            Assert.Null(deck.Resolve("../secret.png"));
            Assert.False(deck.Upload("Оля", true, Png(512, 512)).Ok);            // та сама — ні
            w.Balance["Петро"] = 1000;
            Assert.False(deck.Upload("Петро", true, Png(512, 512)).Ok);          // і в іншого автора — не платить за наявну
            Assert.Equal(1000, w.Balance["Петро"]);
            Assert.Equal(600, w.Balance["Оля"]);
            Assert.True(deck.Upload("Оля", true, Png(510, 512, 1)).Ok);
            Assert.False(deck.Upload("Оля", true, Png(500, 500, 2)).Ok);         // бракує черепків
            Assert.Equal(200, w.Balance["Оля"]);
            Assert.Equal(2, deck.Own.Count);

            Assert.False(deck.Hide("Петро", true, ok.Pic.Id, true).Ok);          // чужу — ні
            Assert.True(deck.Hide("Оля", true, ok.Pic.Id, true).Ok);
            Assert.Single(deck.Own);
            Assert.Equal(2, deck.Mine("оля").Count);
            Assert.True(deck.Hide("Оля", true, ok.Pic.Id, false).Ok);
            Assert.Equal(2, deck.Own.Count);

            Assert.True(deck.Remove(ok.Pic.Id).Ok);
            Assert.False(File.Exists(Path.Combine(dir, ok.Pic.File)));
            Assert.Single(deck.Own);
            Assert.False(deck.Remove(ok.Pic.Id).Ok);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Solved_reports_grant_author_and_count_achievements()
    {
        var (deck, w, dir) = NewDeck();
        try
        {
            w.Balance["Smaug"] = 400;
            var pic = deck.Upload("Smaug", true, Png(300, 300)).Pic!;
            deck.Report(new SkleiSolved(pic.Picture.Key, SkleiKind.Own, "Smaug", "smaug", "r:1:1"));   // сам свою — нічого
            deck.Drain();
            Assert.Empty(w.Grants);
            for (var i = 0; i < SkleiDeck.AuthorForAch; i++)
                deck.Report(new SkleiSolved(pic.Picture.Key, SkleiKind.Own, "Smaug", i % 2 == 0 ? "Оля" : "Петро", $"r:{i}:1"));
            deck.Drain();
            Assert.Equal(SkleiDeck.AuthorForAch, w.Grants.Count);
            Assert.All(w.Grants, g => Assert.Equal(("Smaug", 1), (g.Nick, g.N)));
            Assert.Equal(SkleiDeck.AuthorForAch, w.Grants.Select(g => g.Ref).Distinct().Count());
            Assert.Contains(("Smaug", "sklei-author"), w.Unlocks);
            Assert.Equal(SkleiDeck.AuthorForAch, deck.Own[0].Solved);
            Assert.Contains(("Оля", "sklei-friends"), w.Unlocks);       // 13 чужих картинок
            Assert.Equal(13, deck.Friends("Оля"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Game_takes_mine_and_reports_friends_pictures()
    {
        var (deck, w, dir) = NewDeck();
        try
        {
            w.Balance["Ганна"] = 400;
            w.Balance["Оля"] = 400;
            var hers = deck.Upload("Ганна", true, Png(300, 300)).Pic!;
            var mine = deck.Upload("Оля", true, Png(300, 300, 9)).Pic!;
            var sp = RoomHarness.WithService(deck);
            var h = Started(2, new { pics = "mine" }, services: sp);
            Assert.Equal(mine.Picture.Key, G(h).Picture.Key);              // «мої» — лише тих, хто сидить

            var h2 = Started(2, new { pics = "all" }, seed: 1, services: sp);
            var g = G(h2);
            var seen = new List<string>();
            for (var p = 0; p < 3; p++) { ToGo(h2); seen.Add(g.Picture.Key); Solve(h2, 0); Solve(h2, 1); h2.Tick(70); }
            Assert.Single(h2.Finished);
            deck.Drain();
            // Скільки разів трапилась Ганнина — стільки по двоє гравців дали їй +1; Олину склав Петро — Олі +1.
            var hannaTimes = seen.Count(k => k == hers.Picture.Key);
            Assert.Equal(hannaTimes * 2, w.Grants.Count(x => x.Nick == "Ганна"));
            Assert.Equal(seen.Count(k => k == mine.Picture.Key), w.Grants.Count(x => x.Nick == "Оля"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Empty_kinds_fall_back_to_builtin()
    {
        foreach (var pics in new[] { "photo", "friends", "mine" })
        {
            var h = Started(2, new { pics });
            Assert.Equal(SkleiKind.Builtin, G(h).Picture.Kind);
        }
    }

    // ---------- швидкість ----------

    [Fact]
    [Trait("Category", "Perf")]
    public void Thousand_ticks_with_bots_fast()
    {
        var h = new PartyHarness("sklei", humans: 0, bots: 8, level: LiveBots.Level.Hard, seed: 4);
        h.Start();
        var g = (Sklei)h.Game;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++) { h.Clock.AdvanceMs(Sklei.TickMillis); g.Tick(); g.View(i % 8); }
        sw.Stop();
        output.WriteLine($"1000 тиків × 8 ботів + вид: {sw.ElapsedMilliseconds} мс");
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} мс");
    }
}
