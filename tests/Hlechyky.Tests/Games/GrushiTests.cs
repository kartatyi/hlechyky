using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Крадії груш: правила кроку (хода, ноша, комори, крадіжка, штовхан, хвилі) — на голому <see cref="GrushiCore"/>,
/// де тіло й грушу можна поставити куди треба; фази, кінці, вихід, ачівки, боти й вечірку — через кімнату й хост,
/// як гратимуть люди.
/// </summary>
[Collection(SerialPerf.Name)]
public class GrushiTests(ITestOutputHelper output)
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марко", "Соня", "Тарас", "Леся"];

    // ---------- підмостки ----------

    static RoomHarness Table(int players = 2, int seed = 42, string len = "2")
    {
        var h = new RoomHarness("grushi", new { len }, seed);
        foreach (var nick in Nicks.Take(players)) h.Join(nick);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static Grushi Game(RoomHarness h) => (Grushi)h.Room.Game;
    static GrushiCore Core(RoomHarness h) => Game(h).Core;

    static void ToGo(RoomHarness h)
    {
        for (var i = 0; i < 200 && Game(h).Phase != Grushi.PhGo; i++) h.Tick();
        Assert.Equal(Grushi.PhGo, Game(h).Phase);
    }

    /// <summary>Голий світ: гравці на перших <paramref name="n"/> місцях, хвиль нема (поки тест сам не дозволить).</summary>
    static GrushiCore World(int n = 2, int seed = 1)
    {
        var c = new GrushiCore(new Random(seed));
        c.Reset([.. Enumerable.Range(0, GrushiCore.Seats).Select(i => i < n)]);
        c.NextWave = int.MaxValue;
        return c;
    }

    static void Place(GrushiCore.Body b, double x, double y) { b.X = x; b.Y = y; }

    static GrushiCore.Pear Pear(GrushiCore c, double x, double y, bool gold = false)
    {
        var p = new GrushiCore.Pear { Id = 1000 + c.Ground.Count, X = x, Y = y, Gold = gold };
        c.Ground.Add(p);
        return p;
    }

    static string FrameJson(RoomHarness h) => JsonSerializer.Serialize(Game(h).Frame());

    // ---------- каталог і старт ----------

    [Fact]
    public void Info_is_live_party_minigame_with_len_and_bot_options()
    {
        var g = new Grushi();
        Assert.Equal("grushi", g.Info.Id);
        Assert.Equal(GameGroup.Live, g.Info.Group);
        Assert.Equal(1, g.Info.MinPlayers);
        Assert.Equal(8, g.Info.MaxPlayers);
        Assert.Equal(40, g.Info.TickMs);
        Assert.Equal(StartMode.ByHost, g.Info.Start);
        Assert.Contains(g.Info.Options!, o => o.Key == "len");
        Assert.Contains(g.Info.Options!, o => o.Key == "botlvl");
        Assert.DoesNotContain(g.Info.Options!, o => o.Key is "party" or "bots");
        Assert.True(PartyPool.Has("grushi"));
        Assert.True(g.PartyCapMs <= MinigameHost.MaxCapMs);
        Assert.Equal(2, g.PartyMin);
        Assert.Equal(8, g.PartyMax);
        Assert.False(string.IsNullOrWhiteSpace(g.Howto));
        foreach (var key in new[] { "grushi-robin", "grushi-guard", "grushi-porter" })
            Assert.NotNull(AchievementCatalog.Get(key));
    }

    [Fact]
    public void Alone_without_bot_cannot_start()
    {
        var h = new RoomHarness("grushi");
        h.Join("Оля");
        Assert.False(h.Start().Ok);
        Assert.Equal(LiveBots.AloneText, h.Reply.Message);
    }

    [Fact]
    public void Lobby_view_shows_larders_of_seated_and_empty_garden()
    {
        var h = new RoomHarness("grushi");
        h.Join("Оля");
        h.Join("Петро");
        var v = h.View(null);
        Assert.Equal("lobby", v.GetProperty("phase").GetString());
        var l = v.GetProperty("larders");
        Assert.Equal(JsonValueKind.Array, l[0].ValueKind);
        Assert.Equal(JsonValueKind.Array, l[1].ValueKind);
        Assert.Equal(JsonValueKind.Null, l[2].ValueKind);
        Assert.Equal(0, v.GetProperty("frame").GetProperty("g").GetArrayLength());
        Assert.Equal(Grushi.PhLobby, v.GetProperty("frame").GetProperty("ph").GetInt32());
    }

    [Fact]
    public void Start_counts_down_then_plays_and_view_has_no_hidden_parts()
    {
        var h = Table(3);
        var v = h.View(0);
        Assert.Equal("ready", v.GetProperty("phase").GetString());
        Assert.Equal(120, v.GetProperty("len").GetInt32());
        var f = v.GetProperty("frame");
        Assert.Equal(JsonValueKind.Array, f.GetProperty("p")[2].ValueKind);
        Assert.Equal(JsonValueKind.Null, f.GetProperty("p")[3].ValueKind);
        Assert.Equal(8, f.GetProperty("p")[0].GetArrayLength());
        Assert.Equal(Views.Json(Game(h).View(0)).GetRawText(), Views.Json(Game(h).View(null)).GetRawText());   // нічого свого
        h.Tick(Grushi.ReadyTicks - 1);
        Assert.Equal(Grushi.PhReady, Game(h).Phase);
        h.Tick();
        Assert.Equal(Grushi.PhGo, Game(h).Phase);
        Assert.Equal("go", h.View(1).GetProperty("phase").GetString());
    }

    [Fact]
    public void Larders_stand_on_edge_and_players_start_near_their_own()
    {
        for (var n = 2; n <= 8; n++)
        {
            var c = World(n);
            var pos = new HashSet<(double, double)>();
            for (var s = 0; s < n; s++)
            {
                var b = c.Bodies[s];
                Assert.True(Math.Max(Math.Abs(b.LarderX - GrushiCore.C), Math.Abs(b.LarderY - GrushiCore.C)) >= GrushiCore.LarderAt - 1);
                Assert.True(pos.Add((b.LarderX, b.LarderY)));
                var d = Math.Sqrt((b.X - b.LarderX) * (b.X - b.LarderX) + (b.Y - b.LarderY) * (b.Y - b.LarderY));
                Assert.InRange(d, GrushiCore.LarderR, GrushiCore.LarderR + 20);   // поза зоною — не скидає нічого на старті
            }
        }
    }

    // ---------- дії ----------

    [Fact]
    public void Illegal_acts_fail_and_do_not_change_anything()
    {
        var h = Table(2);
        var before = FrameJson(h);
        Assert.False(h.Act(0, "push").Ok);                       // на відліку штовхати не можна
        Assert.False(h.Act(0, "fly").Ok);
        Assert.False(h.Act(0, "move", new { a = 16 }).Ok);
        Assert.False(h.Act(0, "move", new { a = "схід" }).Ok);
        Assert.False(h.Act(0, "move", new { b = 1 }).Ok);
        Assert.False(Game(h).Act(5, "move", Views.Payload(new { a = 1 })).Ok);   // місце не грає
        Assert.Equal(before, FrameJson(h));
        Assert.True(h.Act(0, "move", 4).Ok);                     // намір на відліку — можна, голим числом теж
        Assert.Equal(4, Core(h).Bodies[0].Want);
        Assert.True(h.Act(0, "move", new { a = -1 }).Ok);
        Assert.Equal(-1, Core(h).Bodies[0].Want);
    }

    [Fact]
    public void Bot_toggle_only_in_lobby_and_after_game()
    {
        var h = new RoomHarness("grushi");
        h.Join("Оля");
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.True(h.Start().Ok);
        Assert.False(h.Act(0, LiveBots.Toggle, new { on = false }).Ok);
        Assert.Equal(Grushi.SoloBots, Game(h).Bots.Count);
    }

    [Fact]
    public void Move_speed_drops_ten_percent_per_pear()
    {
        var c = World();
        var b = c.Bodies[0];
        Place(b, 500, 200);
        c.Move(0, 0);
        c.Step();
        Assert.Equal(500 + GrushiCore.Speed, b.X, 6);
        b.Carry = 5;
        var x = b.X;
        c.Step();
        Assert.Equal(x + GrushiCore.Speed * 0.5, b.X, 6);
        Place(b, GrushiCore.Size - 5, 200);                   // край саду тримає
        c.Step();
        Assert.Equal(GrushiCore.Size - GrushiCore.BodyR, b.X, 6);
    }

    [Fact]
    public void Walking_over_pears_picks_up_to_five_and_gold_counts_three_in_larder()
    {
        var c = World();
        var b = c.Bodies[0];
        Place(b, 500, 300);
        Pear(c, 505, 300, gold: true);
        for (var i = 0; i < 5; i++) Pear(c, 500, 305);
        c.Step();
        Assert.Equal(5, b.Carry);
        Assert.Equal(1, b.Gold);
        Assert.Single(c.Ground);                              // шоста не влізла
        Place(b, b.LarderX, b.LarderY);
        c.Step();
        Assert.Equal(0, b.Carry);
        Assert.Equal(4 + GrushiCore.GoldValue, b.Larder);
        Assert.Equal(1, b.Loads5);
        Assert.Contains(c.Events(), e => e[0] == GrushiCore.EvDrop && e[1] == 0 && e[2] == 7);
    }

    [Fact]
    public void Locked_pear_cannot_be_picked_until_it_lands()
    {
        var c = World();
        var b = c.Bodies[0];
        Place(b, 500, 300);
        var p = Pear(c, 500, 300);
        p.Lock = 2;
        c.Step();
        Assert.Equal(0, b.Carry);
        c.Step();
        Assert.Equal(1, b.Carry);
    }

    [Fact]
    public void Standing_at_foreign_larder_steals_one_pear_per_second()
    {
        var c = World();
        var thief = c.Bodies[0];
        var owner = c.Bodies[1];
        owner.Larder = 2;
        Place(owner, 500, 500);
        Place(thief, owner.LarderX, owner.LarderY);
        for (var i = 0; i < GrushiCore.StealTicks - 1; i++) c.Step();
        Assert.Equal(0, thief.Carry);
        Assert.True(thief.StealProg > 0);
        c.Step();
        Assert.Equal(1, thief.Carry);
        Assert.Equal(1, owner.Larder);
        Assert.Contains(c.Events(), e => e[0] == GrushiCore.EvSteal && e[1] == 0 && e[2] == 1);
        for (var i = 0; i < 3 * GrushiCore.StealTicks; i++) c.Step();
        Assert.Equal(2, thief.Carry);                          // комора спорожніла — більше нема чого
        Assert.Equal(0, owner.Larder);
        Assert.Equal(2, thief.Stole);
        Assert.Equal(2, owner.Lost);
        Assert.Equal(0, thief.StealProg);
    }

    [Fact]
    public void Stealing_resets_when_leaving_zone_and_stops_with_full_load()
    {
        var c = World();
        var thief = c.Bodies[0];
        var owner = c.Bodies[1];
        owner.Larder = 10;
        Place(owner, 500, 500);
        Place(thief, owner.LarderX, owner.LarderY);
        for (var i = 0; i < 10; i++) c.Step();
        Place(thief, 500, 300);
        c.Step();
        Assert.Equal(0, thief.StealProg);
        Place(thief, owner.LarderX, owner.LarderY);
        thief.Carry = GrushiCore.CarryMax;
        for (var i = 0; i < 2 * GrushiCore.StealTicks; i++) c.Step();
        Assert.Equal(10, owner.Larder);
    }

    [Fact]
    public void Push_spills_load_stuns_and_cools_down()
    {
        var c = World(3);
        var a = c.Bodies[0];
        var t = c.Bodies[1];
        Place(a, 500, 300);
        Place(t, 560, 300);
        Place(c.Bodies[2], 800, 800);
        t.Carry = 4;
        t.Gold = 1;
        Assert.Null(c.Push(0));
        Assert.Equal(0, t.Carry);
        Assert.Equal(4, c.Ground.Count);
        Assert.Equal(1, c.Ground.Count(p => p.Gold));
        Assert.All(c.Ground, p => Assert.Equal(GrushiCore.SpillLock, p.Lock));
        Assert.Equal(GrushiCore.StunTicks, t.Stun);
        Assert.Equal(GrushiCore.PushCd, a.Cd);
        Assert.Equal(4, t.Spilled);
        Assert.NotNull(c.Push(0));                             // перезарядка
        var x = t.X;
        c.Move(1, 4);
        c.Step();
        Assert.True(t.X > x);                                  // відлітає від того, хто штовхнув
        Assert.Equal(x + GrushiCore.KnockStep, t.X, 6);        // і сам не ходить, поки приголомшений
    }

    [Fact]
    public void Pushed_thief_cannot_steal_while_immune()
    {
        var c = World();
        var owner = c.Bodies[0];
        var thief = c.Bodies[1];
        owner.Larder = 5;
        Place(thief, owner.LarderX, owner.LarderY);
        Place(owner, owner.LarderX, owner.LarderY + 50);
        for (var i = 0; i < GrushiCore.StealTicks / 2; i++) c.Step();
        Assert.Null(c.Push(0));
        // Огорожа лишає його в зоні (чи він одразу вертається) — однак, поки недоторканний, не краде.
        Place(thief, owner.LarderX, owner.LarderY);
        while (thief.Immune > 1) { c.Step(); Assert.Equal(0, thief.Carry); Assert.Equal(0, thief.StealProg); }
        for (var i = 0; i < GrushiCore.StealTicks; i++) c.Step();   // недоторканність скінчилась — за 1 с бере грушу
        Assert.Equal(1, thief.Carry);
        Assert.Equal(4, owner.Larder);
    }

    [Fact]
    public void Push_with_nobody_near_is_a_whiff_and_immune_body_is_skipped()
    {
        var c = World(2);
        Place(c.Bodies[0], 300, 300);
        Place(c.Bodies[1], 700, 700);
        Assert.Null(c.Push(0));
        Assert.Equal(GrushiCore.PushCd, c.Bodies[0].Cd);
        Assert.DoesNotContain(c.Events(), e => e[0] == GrushiCore.EvWhiff);   // подія з Act — у кадрі наступного тика
        c.Step();
        Assert.Contains(c.Events(), e => e[0] == GrushiCore.EvWhiff && e[1] == 0);
        Place(c.Bodies[1], 350, 300);
        c.Bodies[1].Immune = 5;
        c.Bodies[1].Carry = 3;
        c.Bodies[0].Cd = 0;
        Assert.Null(c.Push(0));
        Assert.Equal(3, c.Bodies[1].Carry);
    }

    [Fact]
    public void Push_through_act_only_in_play_and_cooldown_fails()
    {
        var h = Table(2);
        ToGo(h);
        Assert.True(h.Act(0, "push").Ok);
        Assert.False(h.Act(0, "push").Ok);
    }

    [Fact]
    public void Bodies_do_not_overlap()
    {
        var c = World(2);
        Place(c.Bodies[0], 500, 500);
        Place(c.Bodies[1], 510, 500);
        c.Step();
        var d = Math.Abs(c.Bodies[1].X - c.Bodies[0].X);
        Assert.True(d >= 2 * GrushiCore.BodyR - 1e-6);
    }

    [Fact]
    public void Trees_shake_first_then_pears_fall_and_ground_has_a_ceiling()
    {
        var c = World(4, seed: 3);
        c.NextWave = 1;
        c.Step();
        Assert.True(c.ShakeTree >= 0);
        Assert.Contains(c.Events(), e => e[0] == GrushiCore.EvShake);
        Assert.Empty(c.Ground);
        for (var i = 0; i < GrushiCore.ShakeTicks; i++) c.Step();
        Assert.NotEmpty(c.Ground);
        Assert.Equal(-1, c.ShakeTree);
        // Нікого нема, хто б збирав: земля наповнюється до стелі й не більше.
        for (var s = 0; s < 4; s++) Place(c.Bodies[s], 40 + s * 70, 40);
        for (var i = 0; i < 6000; i++) c.Step();
        Assert.InRange(c.Ground.Count, GrushiCore.GroundMax - 6, GrushiCore.GroundMax);
        Assert.All(c.Ground, p =>
        {
            Assert.InRange(p.X, 0, GrushiCore.Size);
            Assert.InRange(p.Y, 0, GrushiCore.Size);
        });
    }

    // ---------- кінці ----------

    [Fact]
    public void Time_out_finishes_with_fullest_larder_and_scores()
    {
        var h = Table(3);
        ToGo(h);
        Core(h).Bodies[0].Larder = 7;
        Core(h).Bodies[1].Larder = 12;
        Core(h).Bodies[2].Larder = 3;
        Core(h).NextWave = int.MaxValue;
        h.Tick(Game(h).GoTicks);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var r = h.Finished.Single().Result;
        Assert.Equal([1], r.Winners);
        Assert.Equal(12L, r.Scores![1]);
        Assert.Equal(7L, r.Scores[0]);
        Assert.Equal("over", h.View(null).GetProperty("phase").GetString());
        Assert.False(h.Act(0, "move", 1).Ok);
    }

    [Fact]
    public void Tie_shares_win_and_empty_larders_are_a_draw()
    {
        var h = Table(2);
        ToGo(h);
        Core(h).Bodies[0].Larder = 5;
        Core(h).Bodies[1].Larder = 5;
        Core(h).NextWave = int.MaxValue;
        h.Tick(Game(h).GoTicks);
        Assert.Equal([0, 1], h.Finished.Single().Result.Winners);

        var d = Table(2, seed: 5);
        ToGo(d);
        Core(d).NextWave = int.MaxValue;
        d.Tick(Game(d).GoTicks);
        Assert.True(d.Finished.Single().Result.Draw);
    }

    [Fact]
    public void Three_minute_option_lasts_longer()
    {
        var h = Table(2, len: "3");
        Assert.Equal(3 * 60 * GrushiCore.TicksPerSec, Game(h).GoTicks);
        Assert.Equal(180, h.View(null).GetProperty("len").GetInt32());
    }

    [Fact]
    public void Leaving_mid_game_spills_load_and_the_rest_play_on_until_one_is_left()
    {
        var h = Table(3);
        ToGo(h);
        Core(h).Bodies[2].Carry = 3;
        Core(h).Bodies[2].Larder = 4;
        h.Leave("Ганна");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.False(Core(h).Bodies[2].Plays);
        Assert.True(Core(h).Bodies[2].HasLarder);              // комора стоїть — можна обчистити
        Assert.Equal(3, Core(h).Ground.Count(p => p.Lock > 0));
        Core(h).Bodies[1].Larder = 2;
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var r = h.Finished.Single().Result;
        Assert.Equal([0], r.Winners.Length == 0 ? [0] : r.Winners);
    }

    [Fact]
    public void Rematch_starts_clean()
    {
        var h = Table(2);
        ToGo(h);
        Core(h).Bodies[0].Larder = 3;
        h.Tick(Game(h).GoTicks);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(h.Rematch().Ok, h.Reply.Message);
        Assert.Equal(Grushi.PhReady, Game(h).Phase);
        Assert.All(Core(h).Bodies.Where(b => b.Plays), b => Assert.Equal(0, b.Larder));
        Assert.Empty(Core(h).Ground);
        Assert.NotEqual(JsonValueKind.Null, h.View(null).GetProperty("series").ValueKind);
    }

    static string[] Said(RoomHarness h) => [.. h.Outbox.OfType<TableSaid>().Select(o => o.Line.Text)];

    [Fact]
    public void Glek_speaks_at_first_start_and_once_on_a_big_spill()
    {
        var h = Table(2);
        Assert.Single(Said(h), s => s.Contains("огризок"));
        ToGo(h);
        var c = Core(h);
        c.NextWave = int.MaxValue;
        for (var k = 0; k < 2; k++)
        {
            Place(c.Bodies[0], 500, 300);
            Place(c.Bodies[1], 560, 300);
            c.Bodies[1].Carry = 4;
            c.Bodies[1].Immune = 0;
            c.Bodies[0].Cd = 0;
            Assert.True(h.Act(0, "push").Ok);
            h.Tick();
        }
        Assert.Single(Said(h), s => s.Contains("на землі"));
        h.Tick(Game(h).GoTicks);
        Assert.True(h.Rematch().Ok);
        Assert.Single(Said(h), s => s.Contains("огризок"));     // лише на першій партії столу
    }

    // ---------- ачівки ----------

    [Fact]
    public void Achievements_go_to_those_who_earned_them_in_human_games()
    {
        var h = Table(3);
        ToGo(h);
        var c = Core(h);
        c.NextWave = int.MaxValue;
        c.Bodies[0].Stole = 10; c.Bodies[0].Larder = 14; c.Bodies[0].Lost = 2;     // Робін Гуд
        c.Bodies[1].Larder = 11; c.Bodies[1].Lost = 0; c.Bodies[1].Loads5 = 5;     // Сторож і Вантажник
        c.Bodies[2].Larder = 4; c.Bodies[2].Lost = 0;                              // мало груш — не сторож
        h.Tick(Game(h).GoTicks);
        Assert.Contains(h.Awards, a => a.Reason == "ach:grushi-robin" && a.Nick == "Оля");
        Assert.Contains(h.Awards, a => a.Reason == "ach:grushi-guard" && a.Nick == "Петро");
        Assert.Contains(h.Awards, a => a.Reason == "ach:grushi-porter" && a.Nick == "Петро");
        Assert.DoesNotContain(h.Awards, a => a.Reason.StartsWith("ach:grushi") && a.Nick == "Ганна");
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:grushi-guard" && a.Nick == "Оля");
    }

    [Fact]
    public void Bot_game_gives_no_achievements_and_bot_win_has_verdict()
    {
        var h = new RoomHarness("grushi", new { botlvl = "hard" }, 9);
        h.Join("Оля");
        h.Act(0, LiveBots.Toggle, new { on = true });
        Assert.True(h.Start().Ok);
        ToGo(h);
        Core(h).Bodies[0].Stole = 12;
        Core(h).Bodies[0].Larder = 1;
        Core(h).Bodies[1].Larder = 50;
        h.Tick(Game(h).GoTicks);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Empty(h.Awards.Where(a => a.Reason.StartsWith("ach:grushi")));
        var r = h.Finished.Single().Result;
        Assert.Empty(r.Winners);
        Assert.StartsWith("🤖", r.Verdict);
        Assert.Equal($"{LiveBots.Name} рудий", Game(h).SeatBot(1));
    }

    // ---------- боти ----------

    [Fact]
    public void Solo_with_bots_bots_actually_play()
    {
        var h = new RoomHarness("grushi", new { botlvl = "normal" }, 4);
        h.Join("Оля");
        h.Act(0, LiveBots.Toggle, new { on = true });
        Assert.True(h.Start().Ok);
        Assert.Equal([1, 2, 3], Game(h).Bots);
        var v = h.View(0);
        Assert.Equal(3, v.GetProperty("bot").GetArrayLength());
        h.Tick(Grushi.ReadyTicks + 60 * GrushiCore.TicksPerSec);
        var c = Core(h);
        Assert.All(new[] { 1, 2, 3 }, s => Assert.True(c.Bodies[s].Delivered > 0, $"бот {s} нічого не доніс"));
        Assert.Equal(0, c.Bodies[0].Larder);                   // людина стояла
    }

    /// <summary>Двоє ботів у голому світі, ввід — тим самим шляхом, що в <see cref="Grushi"/> (Move/Push щодумки).</summary>
    static (int A, int B) Duel(LiveBots.Level a, LiveBots.Level b, int seed, int players = 2, int seconds = 90)
    {
        var rng = new Random(seed);
        var c = new GrushiCore(rng);
        c.Reset([.. Enumerable.Range(0, GrushiCore.Seats).Select(i => i < players)]);
        var bots = Enumerable.Range(0, players).Select(i => new GrushiBot(i == 0 ? a : b, i)).ToArray();
        for (var t = 0; t < seconds * GrushiCore.TicksPerSec; t++)
        {
            for (var s = 0; s < players; s++)
            {
                if (!bots[s].Due(c.T)) continue;
                var m = bots[s].Think(c, s, rng);
                if (m.Sector is { } sec) c.Move(s, sec);
                if (m.Push) c.Push(s);
            }
            c.Step();
        }
        return (c.Bodies[0].Larder, Enumerable.Range(1, players - 1).Sum(s => c.Bodies[s].Larder) / (players - 1));
    }

    [Fact]
    public void Hard_bot_clearly_beats_easy_and_normal_clearly_beats_easy()
    {
        int hard = 0, easy = 0, normal = 0, easy2 = 0;
        for (var seed = 1; seed <= 6; seed++)
        {
            var (h, e) = Duel(LiveBots.Level.Hard, LiveBots.Level.Easy, seed, players: 4);
            hard += h; easy += e;
            var (n, e2) = Duel(LiveBots.Level.Normal, LiveBots.Level.Easy, seed, players: 4);
            normal += n; easy2 += e2;
        }
        output.WriteLine($"сильний {hard} проти легких {easy}; звичайний {normal} проти легких {easy2}");
        Assert.True(hard > easy * 1.3, $"сильний {hard}, легкі в середньому {easy}");
        Assert.True(normal > easy2 * 1.3, $"звичайний {normal}, легкі в середньому {easy2}");
        Assert.True(hard * easy2 > normal * easy * 1.1, "сильний мусить відривати від легких помітно більше, ніж звичайний");
    }

    [Fact]
    public void Easy_bot_never_steals_or_pushes_hard_bot_does_both()
    {
        var rng = new Random(2);
        var c = new GrushiCore(rng);
        c.Reset([.. Enumerable.Range(0, GrushiCore.Seats).Select(i => i < 4)]);
        var bots = new[] { LiveBots.Level.Easy, LiveBots.Level.Hard, LiveBots.Level.Hard, LiveBots.Level.Easy }
            .Select((l, i) => new GrushiBot(l, i)).ToArray();
        for (var t = 0; t < 120 * GrushiCore.TicksPerSec; t++)
        {
            for (var s = 0; s < 4; s++)
            {
                if (!bots[s].Due(c.T)) continue;
                var m = bots[s].Think(c, s, rng);
                if (m.Sector is { } sec) c.Move(s, sec);
                if (m.Push) c.Push(s);
            }
            c.Step();
        }
        output.WriteLine(string.Join(" | ", Enumerable.Range(0, 4).Select(s =>
            $"{s}: комора {c.Bodies[s].Larder}, вкрав {c.Bodies[s].Stole}, штовхнув {c.Bodies[s].Pushes}")));
        Assert.Equal(0, c.Bodies[0].Stole + c.Bodies[3].Stole);
        Assert.Equal(0, c.Bodies[0].Pushes + c.Bodies[3].Pushes);
        Assert.True(c.Bodies[1].Stole + c.Bodies[2].Stole > 0);
        Assert.True(c.Bodies[1].Pushes + c.Bodies[2].Pushes > 0);
    }

    // ---------- детермінізм і швидкість ----------

    [Fact]
    public void Same_seed_same_game()
    {
        static string Run(int seed)
        {
            var h = new RoomHarness("grushi", new { botlvl = "hard" }, seed);
            h.Join("Оля");
            h.Act(0, LiveBots.Toggle, new { on = true });
            h.Start();
            h.Tick(Grushi.ReadyTicks + 1500);
            return JsonSerializer.Serialize(((Grushi)h.Room.Game).Frame());
        }
        Assert.Equal(Run(7), Run(7));
        Assert.NotEqual(Run(7), Run(8));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Thousand_ticks_with_eight_bots_under_two_seconds()
    {
        var h = new PartyHarness("grushi", humans: 0, bots: 8, level: LiveBots.Level.Hard, seed: 5);
        h.Start();
        h.TickSub(10);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            h.TickSub();
            _ = h.Frame();
        }
        sw.Stop();
        output.WriteLine($"1000 тиків з кадром: {sw.ElapsedMilliseconds} мс");
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} мс");
    }

    // ---------- режим вечірки ----------

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(8)]
    public void Party_with_only_bots_finishes_before_cap_with_scores_for_all(int bots)
    {
        var h = new PartyHarness("grushi", humans: 0, bots: bots, seed: 7);
        h.Start();
        var r = h.RunToEnd();
        Assert.NotNull(r);
        Assert.Equal(MinigameEnd.Finished, r!.How);
        Assert.Equal(bots, r.Scores.Count);
        Assert.All(Enumerable.Range(0, bots), s => Assert.True(r.Scores.ContainsKey(s)));
        Assert.True(r.Scores.Values.Max() > 0);
        Assert.Equal(0, h.Ctx.Muted);
        Assert.True(h.Clock.UtcNow - h.StartedAt <= TimeSpan.FromMilliseconds(h.Host.CapMs));
        Assert.Empty(h.Parent.Says);                           // Глек у вечірці мовчить
    }

    [Fact]
    public void Party_idle_humans_do_not_stall_and_scores_cover_every_seat_midway()
    {
        var h = new PartyHarness("grushi", humans: 2, bots: 2, level: LiveBots.Level.Normal, seed: 3);
        h.Start();
        h.TickSub(Grushi.ReadyTicks + 30 * GrushiCore.TicksPerSec);
        var mid = ((Grushi)h.Game).PartyScores();
        Assert.Equal(4, mid.Count);
        Assert.All(mid.Values, v => Assert.True(v >= 0));
        var r = h.RunToEnd()!;
        Assert.Equal(MinigameEnd.Finished, r.How);
        Assert.Equal(4, r.Scores.Count);
        Assert.True(r.Scores[2] + r.Scores[3] > r.Scores[0] + r.Scores[1]);   // боти грали, люди стояли
        Assert.Equal(0, h.Ctx.Muted);
    }

    [Fact]
    public void Party_all_idle_finishes_with_equal_scores_and_everyone_winning()
    {
        var h = new PartyHarness("grushi", humans: 3, bots: 0, seed: 4);
        h.Start();
        var r = h.RunToEnd()!;
        Assert.Equal(MinigameEnd.Finished, r.How);
        Assert.Equal([0L, 0L, 0L], [.. r.Scores.OrderBy(kv => kv.Key).Select(kv => kv.Value)]);
        Assert.Equal([0, 1, 2], r.Winners);
    }

    [Fact]
    public void Party_good_human_ends_on_top_of_easy_bots()
    {
        var wins = 0;
        for (var seed = 1; seed <= 3; seed++)
        {
            var h = new PartyHarness("grushi", humans: 1, bots: 3, level: LiveBots.Level.Easy, seed: seed);
            h.Start();
            var brain = new GrushiBot(LiveBots.Level.Hard, 0);
            var rng = new Random(seed);
            var g = (Grushi)h.Game;
            var r = h.RunToEnd(p =>
            {
                if (g.Phase != Grushi.PhGo || !brain.Due(g.Core.T)) return;
                var m = brain.Think(g.Core, 0, rng);
                if (m.Sector is { } a) p.Act(0, "move", new { a });
                if (m.Push && g.Core.Bodies[0].Cd == 0) p.Act(0, "push");
            })!;
            output.WriteLine(string.Join(", ", r.Scores.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}")));
            if (r.Places[0] == 1) wins++;
        }
        Assert.True(wins >= 2, $"людина вгорі лише {wins} з 3");
    }

    [Fact]
    public void Party_is_deterministic_by_seed()
    {
        static string Run()
        {
            var h = new PartyHarness("grushi", humans: 0, bots: 6, seed: 11);
            h.Start();
            var r = h.RunToEnd()!;
            return string.Join(",", r.Scores.OrderBy(kv => kv.Key).Select(kv => kv.Value));
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Party_lasts_seventy_five_seconds_after_countdown()
    {
        var h = new PartyHarness("grushi", humans: 0, bots: 3, seed: 2);
        h.Start();
        Assert.Equal(Grushi.PartySeconds * GrushiCore.TicksPerSec, ((Grushi)h.Game).GoTicks);
        Assert.True(((Grushi)h.Game).Party);
        Assert.Equal(75, h.View(null).GetProperty("len").GetInt32());
        h.RunToEnd();
        var ms = (h.Clock.UtcNow - h.StartedAt).TotalMilliseconds;
        Assert.InRange(ms, 77_000, 80_000);
    }

    [Fact]
    public void Ordinary_table_ignores_party_keys()
    {
        var room = new RoomHarness("grushi", options: new { party = "1", bots = "1,2" });
        room.Join("Оля");
        room.Join("Петро");
        Assert.False(((Grushi)room.Room.Game).Party);
    }
}
