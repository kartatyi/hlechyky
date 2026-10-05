using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Тонкий лід: тріщини, падіння й стрибки — на голому <see cref="ThiniceCore"/> (там тіло й дірку можна поставити
/// куди треба), раунди, партію, вихід, ботів і режим вечірки — через кімнату й <see cref="PartyHarness"/>.
/// </summary>
[Collection(SerialPerf.Name)]
public class ThiniceTests(ITestOutputHelper output)
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марко", "Соня", "Тарас", "Леся"];
    const int Sub = ThiniceCore.Sub;

    // ---------- підмостки ----------

    static RoomHarness Table(int players = 2, int seed = 42)
    {
        var h = new RoomHarness("thinice", null, seed);
        foreach (var nick in Nicks.Take(players)) h.Join(nick);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static RoomHarness WithBots(string lvl = "normal", int seed = 42)
    {
        var h = new RoomHarness("thinice", new { botlvl = lvl }, seed);
        h.Join("Оля");
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static Thinice Game(RoomHarness h) => (Thinice)h.Room.Game;
    static ThiniceCore Core(RoomHarness h) => Game(h).Core;
    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString() ?? "";

    static void ToGo(RoomHarness h)
    {
        for (var i = 0; i < 300 && Game(h).Phase != Thinice.PhGo && h.Room.Status == RoomStatus.Playing; i++) h.Tick();
        Assert.Equal(Thinice.PhGo, Game(h).Phase);
    }

    /// <summary>Тіло — на нижній ярус над діркою: наступного тика шубовсне.</summary>
    static void Sink(ThiniceCore c, int seat)
    {
        var b = c.Bodies[seat];
        b.Tier = 1;
        b.Fall = b.Air = 0;
        c.Ice[1][c.CellOf(b)] = ThiniceCore.Gone;
    }

    /// <summary>Голе ядро: n тіл на цілому ставку, гра вже йде.</summary>
    static ThiniceCore Bare(int n = 2, int seed = 1)
    {
        var c = new ThiniceCore(new Random(seed));
        c.ResetParty([.. Enumerable.Range(0, ThiniceCore.Seats).Select(i => i < n)]);
        c.NewRound(n, 0);
        return c;
    }

    static ThiniceBody Put(ThiniceCore c, int seat, int cx, int cy, int tier = 0)
    {
        var b = c.Bodies[seat];
        b.X = cx * Sub + Sub / 2;
        b.Y = cy * Sub + Sub / 2;
        b.Tier = tier;
        b.Want = -1;
        b.Air = b.Fall = b.Cd = 0;
        return b;
    }

    static JsonElement Frame(RoomHarness h)
    {
        var room = h.Room;
        lock (room.Sync) return Views.Json(room.Game.Frame());
    }

    static int Cell(ThiniceCore c, int x, int y) => y * c.N + x;

    // ---------- паспорт і лобі ----------

    [Fact]
    public void Info_is_live_25hz_for_up_to_eight_and_in_party_pool()
    {
        var g = new Thinice();
        Assert.Equal("thinice", g.Info.Id);
        Assert.Equal(GameGroup.Live, g.Info.Group);
        Assert.Equal(40, g.Info.TickMs);
        Assert.Equal(8, g.Info.MaxPlayers);
        Assert.Equal(StartMode.ByHost, g.Info.Start);
        Assert.True(PartyPool.Has("thinice"));
        var p = (IPartyMinigame)g;
        Assert.InRange(p.PartyCapMs, 45_000, 120_000);
        Assert.Equal(2, p.PartyMin);
        Assert.Equal(8, p.PartyMax);
        Assert.False(string.IsNullOrWhiteSpace(p.Howto));
        Assert.DoesNotContain(g.Info.Options, o => o.Key is "party" or "bots");
    }

    [Fact]
    public void Lobby_shows_whole_pond_for_seated_and_refuses_moves()
    {
        var h = new RoomHarness("thinice");
        h.Join("Оля");
        Assert.Equal("lobby", Phase(h));
        Assert.True(h.View(0).GetProperty("botOffer").GetBoolean());
        Assert.False(h.Act(0, "move", 0).Ok);
        Assert.False(h.Start().Ok);                     // сам без бота — не стартує
        h.Join("Петро");
        h.Join("Ганна");
        h.Join("Іван");
        var v = h.View(null);
        Assert.Equal(12, v.GetProperty("n").GetInt32());
        var ice = v.GetProperty("frame").GetProperty("ice");
        Assert.Equal(new string('.', 144), ice[0].GetString());
        Assert.Equal(4, v.GetProperty("frame").GetProperty("p").EnumerateArray().Count(x => x.ValueKind != JsonValueKind.Null));
    }

    [Theory]
    [InlineData(2, 10)]
    [InlineData(3, 10)]
    [InlineData(5, 12)]
    [InlineData(8, 14)]
    public void Start_puts_everyone_on_distinct_upper_tiles_of_sized_pond(int players, int n)
    {
        var h = Table(players);
        var c = Core(h);
        Assert.Equal(n, c.N);
        Assert.Equal("ready", Phase(h));
        var cells = Enumerable.Range(0, players).Select(s => c.CellOf(c.Bodies[s])).ToArray();
        Assert.Equal(players, cells.Distinct().Count());
        Assert.All(Enumerable.Range(0, players), s => Assert.Equal(0, c.Bodies[s].Tier));
        h.Tick(Thinice.ReadyFirst);
        Assert.Equal("go", Phase(h));
        Assert.All(c.Ice[0], v => Assert.Equal(0, v));   // на відліку лід не тріщить
    }

    // ---------- лід ----------

    [Fact]
    public void Standing_still_cracks_then_drops_to_lower_tier_then_into_water()
    {
        var c = Bare();
        var b = Put(c, 0, 2, 2);
        Put(c, 1, 7, 7);
        c.Bodies[1].Want = 0;
        var cell = Cell(c, 2, 2);
        c.Step();
        Assert.Equal(ThiniceCore.CrackTicks, c.Ice[0][cell]);
        Assert.Equal('a' + ThiniceCore.CrackTicks, c.Row(0)[cell]);
        for (var i = 0; i < ThiniceCore.CrackTicks - 1; i++) { c.BeginTick(); c.Step(); }
        Assert.Equal(0, b.Tier);
        c.BeginTick();
        c.Step();                                          // плитка стала діркою — падаємо
        Assert.Equal(ThiniceCore.Gone, c.Ice[0][cell]);
        Assert.Equal('#', c.Row(0)[cell]);
        Assert.Equal(1, b.Tier);
        Assert.True(b.Dropped);
        Assert.Contains(c.Events(), e => e[0] == ThiniceCore.EvFall && e[1] == 0);
        for (var i = 0; i < ThiniceCore.FallTicks; i++) { c.BeginTick(); c.Step(); }
        Assert.Equal(0, b.Fall);
        Assert.Equal(ThiniceCore.CrackTicks, c.Ice[1][cell]);   // приземлився — нижня тріщить
        for (var i = 0; i < ThiniceCore.CrackTicks; i++) { c.BeginTick(); c.Step(); }
        Assert.False(b.In);
        Assert.Equal(0, b.OutRank);
        Assert.Equal([0], c.Out);
        Assert.Contains(c.Events(), e => e[0] == ThiniceCore.EvSplash && e[1] == 0);
    }

    [Fact]
    public void Falling_onto_a_lower_hole_goes_straight_into_water()
    {
        var c = Bare();
        var b = Put(c, 0, 3, 3);
        c.Ice[0][Cell(c, 3, 3)] = ThiniceCore.Gone;
        c.Ice[1][Cell(c, 3, 3)] = ThiniceCore.Gone;
        c.Step();
        Assert.Equal(1, b.Tier);
        for (var i = 0; i < ThiniceCore.FallTicks; i++) c.Step();
        Assert.False(b.In);
    }

    [Fact]
    public void Walking_cracks_the_trail_and_passes_through_others()
    {
        var c = Bare();
        var a = Put(c, 0, 1, 5);
        var o = Put(c, 1, 3, 5);
        o.Want = 8;   // назустріч
        a.Want = 0;
        for (var i = 0; i < 12; i++) c.Step();
        Assert.True(a.X > 3 * Sub);                         // пройшов крізь суперника, ніхто нікого не штовхнув
        Assert.True(o.X < 2 * Sub);
        Assert.True(c.Ice[0][Cell(c, 2, 5)] > 0);           // слід тріщить
        Assert.True(c.Ice[0][Cell(c, 3, 5)] > 0);
    }

    [Fact]
    public void Pond_has_a_fence_bodies_stop_at_the_edge()
    {
        var c = Bare();
        var a = Put(c, 0, 0, 0);
        a.Want = 10;   // вліво-вгору
        for (var i = 0; i < 5; i++) c.Step();
        Assert.Equal(ThiniceCore.Margin, a.X);
        Assert.True(a.In);
    }

    [Fact]
    public void Jump_clears_one_hole_without_cracking_and_has_cooldown()
    {
        var c = Bare();
        var a = Put(c, 0, 2, 5);
        Put(c, 1, 8, 8).Want = 4;
        c.Ice[0][Cell(c, 3, 5)] = ThiniceCore.Gone;
        a.Want = 0;
        a.X = 3 * Sub - 20;   // біля краю дірки
        c.Step();
        Assert.Null(c.Jump(0));
        Assert.Equal(1, a.Jumps);
        Assert.Equal("Ти вже в повітрі", c.Jump(0));
        for (var i = 0; i < ThiniceCore.AirTicks; i++) c.Step();
        Assert.Equal(0, a.Tier);                             // перелетів дірку
        Assert.Equal(0, a.Air);
        Assert.Equal(4, a.X / Sub);
        Assert.Equal(ThiniceCore.CrackTicks, c.Ice[0][Cell(c, 4, 5)]);   // приземлився — тріщить
        Assert.Equal("Стрибок ще не готовий", c.Jump(0));
        for (var i = 0; i < ThiniceCore.JumpCd; i++) c.Step();
        a.Tier = 0;
        a.In = true;
        Assert.Null(c.Jump(0));
    }

    [Fact]
    public void Standing_jump_lands_back_on_the_same_tile()
    {
        var c = Bare();
        var a = Put(c, 0, 4, 4);
        Put(c, 1, 8, 8);
        var x = a.X;
        Assert.Null(c.Jump(0));
        for (var i = 0; i < ThiniceCore.AirTicks - 1; i++) c.Step();
        Assert.Equal(0, c.Ice[0][Cell(c, 4, 4)]);            // у повітрі не тріщить
        c.Step();
        Assert.Equal(x, a.X);
        Assert.Equal(ThiniceCore.CrackTicks, c.Ice[0][Cell(c, 4, 4)]);
    }

    [Fact]
    public void Thaw_cracks_tiles_by_itself_and_clears_the_pond()
    {
        var c = Bare();
        Put(c, 0, 0, 0);
        Put(c, 1, 9, 9);
        c.ThawAt = 10;
        for (var i = 0; i < 9; i++) c.Step();
        Assert.Equal(2, c.Ice[0].Count(v => v != 0));         // лише під двома, що стоять
        c.BeginTick();
        c.Step();
        Assert.Contains(c.Events(), e => e[0] == ThiniceCore.EvThaw);
        for (var i = 0; i < 40 * 25; i++) c.Step();
        Assert.All(c.Ice[0], v => Assert.Equal(ThiniceCore.Gone, v));
        Assert.All(c.Ice[1], v => Assert.Equal(ThiniceCore.Gone, v));
        Assert.Equal(0, c.AliveCount);
    }

    // ---------- дії ----------

    [Fact]
    public void Illegal_acts_fail_and_do_not_change_the_view()
    {
        var h = Table(2);
        var before = h.View(0).GetRawText();
        Assert.False(h.Act(0, "jump").Ok);                      // відлік
        Assert.False(h.Act(0, "move", 16).Ok);
        Assert.False(h.Act(0, "move", "вліво").Ok);
        Assert.False(h.Act(0, "move", new { a = 2.5 }).Ok);
        Assert.False(h.Act(0, "fly").Ok);
        Assert.False(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.Equal(before, h.View(0).GetRawText());
        Assert.True(h.Act(0, "move", new { a = 4 }).Ok);        // намір приймаємо й на відліку
        Assert.True(h.Act(1, "move", 12).Ok);
    }

    [Fact]
    public void Move_intent_expires_without_confirmation()
    {
        var h = Table(2);
        ToGo(h);
        h.Input(0, "move", 0);
        var b = Core(h).Bodies[0];
        Assert.Equal(0, b.Want);
        h.Tick(Thinice.KeepTicks + 1);
        Assert.Equal(-1, b.Want);
    }

    [Fact]
    public void Jump_via_act_spends_cooldown_and_second_is_refused()
    {
        var h = Table(2);
        ToGo(h);
        Assert.True(h.Act(0, "jump").Ok);
        var r = h.Act(0, "jump");
        Assert.False(r.Ok);
        h.Tick(ThiniceCore.AirTicks + 1);
        Assert.Equal("Стрибок ще не готовий", h.Act(0, "jump").Message);
    }

    // ---------- раунди й партія ----------

    /// <summary>Закінчити раунд: усі, крім <paramref name="order"/>-останнього, падають у воду в заданому порядку.</summary>
    static void PlayRound(RoomHarness h, params int[] order)
    {
        ToGo(h);
        var c = Core(h);
        foreach (var s in order[..^1])
        {
            Sink(c, s);
            h.Tick();
        }
        Assert.Equal(Thinice.PhEnd, Game(h).Phase);
        for (var i = 0; i < 200 && h.Room.Status == RoomStatus.Playing && Game(h).Phase == Thinice.PhEnd; i++) h.Tick();
    }

    [Fact]
    public void Round_gives_points_by_elimination_order_and_a_round_win_to_the_last()
    {
        var h = Table(3);
        ToGo(h);
        var c = Core(h);
        Sink(c, 1);
        h.Tick();
        Sink(c, 2);
        h.Tick();
        Assert.Equal("end", Phase(h));
        Assert.Equal(0, c.Bodies[1].Points);
        Assert.Equal(1, c.Bodies[2].Points);
        Assert.Equal(2, c.Bodies[0].Points);
        Assert.Equal(1, c.Bodies[0].RoundWins);
        var lr = h.View(null).GetProperty("lastRound");
        Assert.Equal(0, lr.GetProperty("winner").GetInt32());
        Assert.Equal([2, 0, 1], lr.GetProperty("ranks").EnumerateArray().Take(3).Select(x => x.GetInt32()));
        h.Tick(Thinice.EndTicks);
        Assert.Equal("ready", Phase(h));
        Assert.Equal(2, Game(h).RoundNo);
    }

    [Fact]
    public void Simultaneous_splash_is_a_shared_place()
    {
        var h = Table(2);
        ToGo(h);
        var c = Core(h);
        Sink(c, 0);
        Sink(c, 1);
        h.Tick();
        Assert.Equal("end", Phase(h));
        Assert.Equal(0, c.Bodies[0].Points);
        Assert.Equal(0, c.Bodies[1].Points);
        Assert.Equal(-1, h.View(null).GetProperty("lastRound").GetProperty("winner").GetInt32());
    }

    [Fact]
    public void Three_rounds_then_finish_with_points_journal_and_achievements()
    {
        var h = Table(2);
        PlayRound(h, 1, 0);
        PlayRound(h, 1, 0);
        PlayRound(h, 1, 0);
        Assert.Equal("over", Phase(h));
        var f = Assert.Single(h.Finished);
        Assert.Equal([0], f.Result.Winners);
        Assert.Equal("Тонкий лід: Оля 3 : Петро 0", f.Result.Text);
        Assert.Equal(3, f.Result.Scores![0]);
        var keys = h.Awards.Select(a => a.Reason).ToArray();
        Assert.Contains("ach:thinice-figure", keys);
        Assert.Contains("ach:thinice-sweep", keys);
        Assert.DoesNotContain("ach:thinice-jumper", keys);
        Assert.Equal([0], h.View(null).GetProperty("winners").EnumerateArray().Select(x => x.GetInt32()));
    }

    [Fact]
    public void Dropping_to_the_lower_tier_loses_figure_skater()
    {
        var h = Table(2);
        ToGo(h);
        Core(h).Bodies[0].Dropped = true;
        PlayRound(h, 1, 0);
        PlayRound(h, 1, 0);
        PlayRound(h, 1, 0);
        var keys = h.Awards.Select(a => a.Reason).ToArray();
        Assert.DoesNotContain("ach:thinice-figure", keys);
        Assert.Contains("ach:thinice-sweep", keys);
    }

    [Fact]
    public void Five_jumps_in_a_won_round_give_jumper()
    {
        var h = Table(2);
        ToGo(h);
        Core(h).Bodies[0].Jumps = Thinice.JumperJumps;
        Sink(Core(h), 1);
        h.Tick();
        Assert.Contains(h.Awards, a => a.Reason == "ach:thinice-jumper");
    }

    [Fact]
    public void Tie_on_points_breaks_by_round_wins_and_full_tie_is_a_draw()
    {
        var h = Table(2);
        PlayRound(h, 1, 0);
        PlayRound(h, 0, 1);
        ToGo(h);
        var c = Core(h);
        Sink(c, 0);
        Sink(c, 1);
        h.Tick();
        h.Tick(Thinice.EndTicks);
        var f = Assert.Single(h.Finished);
        Assert.Empty(f.Result.Winners);
        Assert.Empty(h.Awards);
    }

    [Fact]
    public void Round_hits_the_time_cap_and_survivors_share_the_place()
    {
        var h = Table(3);
        ToGo(h);
        var c = Core(h);
        c.ThawAt = int.MaxValue;
        Sink(c, 2);
        h.Tick();
        // Двоє «бігають» вічно: щотику лікуємо лід.
        for (var i = 0; i < Thinice.CapTicks + 5 && Game(h).Phase == Thinice.PhGo; i++)
        {
            Array.Clear(c.Ice[0]);
            Array.Clear(c.Ice[1]);
            h.Tick();
        }
        Assert.Equal("end", Phase(h));
        var lr = h.View(null).GetProperty("lastRound");
        Assert.True(lr.GetProperty("byTime").GetBoolean());
        Assert.Equal(-1, lr.GetProperty("winner").GetInt32());
        Assert.Equal(1, c.Bodies[0].Points);
        Assert.Equal(1, c.Bodies[1].Points);
    }

    [Fact]
    public void Rematch_starts_a_fresh_match_and_keeps_series()
    {
        var h = Table(2);
        PlayRound(h, 1, 0);
        PlayRound(h, 1, 0);
        PlayRound(h, 1, 0);
        Assert.True(h.Rematch().Ok, h.Reply.Message);
        Assert.Equal("ready", Phase(h));
        Assert.Equal(1, Game(h).RoundNo);
        Assert.All(Enumerable.Range(0, 2), s => Assert.Equal(0, Core(h).Bodies[s].Points));
        var series = h.View(null).GetProperty("series");
        Assert.Equal(1, series.GetProperty("games").GetInt32());
    }

    [Fact]
    public void Leaving_mid_match_keeps_three_going_and_ends_a_duel()
    {
        var h = Table(3);
        ToGo(h);
        h.Leave("Ганна");
        Assert.False(Core(h).Bodies[2].Plays);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        h.Leave("Петро");
        var f = Assert.Single(h.Finished);
        Assert.Equal([0], f.Result.Winners);
    }

    // ---------- вид і кадр ----------

    [Fact]
    public void Views_are_the_same_for_everyone_and_frame_has_expected_shape()
    {
        var h = Table(3);
        ToGo(h);
        h.Input(0, "move", 0);
        h.Tick(5);
        var v0 = h.View(0).GetRawText();
        Assert.Equal(v0, h.View(1).GetRawText());
        Assert.Equal(v0, h.View(null).GetRawText());
        var f = Frame(h);
        Assert.Equal(Thinice.PhGo, f.GetProperty("ph").GetInt32());
        var ice = f.GetProperty("ice");
        Assert.Equal(2, ice.GetArrayLength());
        Assert.Equal(100, ice[0].GetString()!.Length);
        Assert.Matches("^[.#b-z]+$", ice[0].GetString()!);
        var p = f.GetProperty("p");
        Assert.Equal(8, p.GetArrayLength());
        Assert.Equal(8, p[0].GetArrayLength());
        Assert.Equal(JsonValueKind.Null, p[3].ValueKind);
        Assert.Equal(3, p[0][3].GetInt32());                    // на льоду й іде
        Assert.True(f.GetProperty("left").GetInt32() > 0);
    }

    [Fact]
    public void Same_seed_same_bot_match()
    {
        static string Run()
        {
            var h = WithBots("hard", 9);
            for (var i = 0; i < 6000 && h.Room.Status == RoomStatus.Playing; i++) h.Tick();
            var c = Core(h);
            return string.Join(",", Enumerable.Range(0, 4).Select(s => $"{c.Bodies[s].Points}/{c.Bodies[s].RoundWins}")) + "|" + h.Clock.UtcNow.ToUnixTimeMilliseconds();
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Thousand_ticks_with_eight_bots_are_fast()
    {
        var h = new PartyHarness("thinice", humans: 0, bots: 8, level: LiveBots.Level.Hard, seed: 3);
        h.Start();
        var sw = Stopwatch.StartNew();
        h.TickSub(1000);
        sw.Stop();
        output.WriteLine($"1000 тиків: {sw.ElapsedMilliseconds} мс");
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} мс");
    }

    // ---------- соло з ботами ----------

    [Fact]
    public void Solo_with_bots_plays_three_bots_without_awards()
    {
        var h = WithBots("normal");
        var g = Game(h);
        Assert.Equal(Thinice.SoloBots, g.Bots.Count);
        Assert.True(g.BotGame);
        ToGo(h);
        var c = Core(h);
        var start = g.Bots.Select(s => (c.Bodies[s].X, c.Bodies[s].Y)).ToArray();
        h.Tick(20);
        Assert.Contains(g.Bots, s => (c.Bodies[s].X, c.Bodies[s].Y) != start[g.Bots.ToList().IndexOf(s)]);
        for (var i = 0; i < 12000 && h.Room.Status == RoomStatus.Playing; i++) h.Tick();
        Assert.Single(h.Finished);
        Assert.Empty(h.Awards);
        Assert.StartsWith(LiveBots.Name, h.Room.Game.SeatBot(g.Bots[0]));
    }

    /// <summary>Голий раунд ботів заданих рівнів: місце кожного (більше — краще) і скільки тиків тривав.</summary>
    static (int[] Ranks, int Ticks) BotRound(LiveBots.Level[] levels, int seed, int thawAt = 1500)
    {
        var rng = new Random(seed);
        var c = new ThiniceCore(rng);
        var n = levels.Length;
        c.ResetParty([.. Enumerable.Range(0, ThiniceCore.Seats).Select(i => i < n)]);
        c.NewRound(Math.Max(2, n), rng.NextDouble());
        c.ThawAt = thawAt;
        var bots = levels.Select((l, i) => new ThiniceBot(l, i)).ToArray();
        while ((n == 1 ? c.AliveCount >= 1 : c.AliveCount > 1) && c.Rt < 3000)
        {
            c.BeginTick();
            for (var s = 0; s < n; s++)
            {
                if (!c.Bodies[s].In) continue;
                var (a, jump) = bots[s].Think(c, s, rng);
                c.Move(s, a);
                if (jump) c.Jump(s);
            }
            c.Step();
        }
        return ([.. Enumerable.Range(0, n).Select(s => c.Bodies[s].In ? c.Out.Count : c.Bodies[s].OutRank)], c.Rt);
    }

    [Theory]
    [InlineData(LiveBots.Level.Easy)]
    [InlineData(LiveBots.Level.Normal)]
    [InlineData(LiveBots.Level.Hard)]
    public void Lone_bot_keeps_running_on_the_ice(LiveBots.Level level)
    {
        var total = 0;
        for (var seed = 1; seed <= 6; seed++) total += BotRound([level], seed, int.MaxValue).Ticks;
        output.WriteLine($"{level}: у середньому {total / 6 * 40 / 1000.0:F1} с сам на ставку 10×10");
        // Стоячи — 2,4 с; бот, що справді бігає по цілому, тримається куди довше.
        Assert.True(total / 6 > 250, $"{total / 6} тиків");
    }

    [Fact]
    public void Hard_bots_outlast_easy_ones()
    {
        double hard = 0, easy = 0;
        const int Runs = 30;
        for (var seed = 1; seed <= Runs; seed++)
        {
            var (r, _) = BotRound([LiveBots.Level.Hard, LiveBots.Level.Easy, LiveBots.Level.Hard, LiveBots.Level.Easy], seed);
            hard += r[0] + r[2];
            easy += r[1] + r[3];
        }
        output.WriteLine($"середнє місце: сильний {hard / Runs / 2:F2}, легкий {easy / Runs / 2:F2}");
        Assert.True(hard > easy * 1.3, $"сильний {hard}, легкий {easy}");
    }

    [Fact]
    public void Hard_bots_outlast_normal_ones()
    {
        double hard = 0, normal = 0;
        for (var seed = 1; seed <= 40; seed++)
        {
            var (r, _) = BotRound([LiveBots.Level.Hard, LiveBots.Level.Normal, LiveBots.Level.Hard, LiveBots.Level.Normal], seed + 300);
            hard += r[0] + r[2];
            normal += r[1] + r[3];
        }
        output.WriteLine($"середнє місце: сильний {hard / 80:F2}, звичайний {normal / 80:F2}");
        Assert.True(hard > normal, $"сильний {hard}, звичайний {normal}");
    }

    [Fact]
    public void Normal_bots_outlast_easy_ones()
    {
        double normal = 0, easy = 0;
        const int Runs = 30;
        for (var seed = 1; seed <= Runs; seed++)
        {
            var (r, _) = BotRound([LiveBots.Level.Normal, LiveBots.Level.Easy, LiveBots.Level.Normal, LiveBots.Level.Easy], seed + 100);
            normal += r[0] + r[2];
            easy += r[1] + r[3];
        }
        output.WriteLine($"середнє місце: звичайний {normal / Runs / 2:F2}, легкий {easy / Runs / 2:F2}");
        Assert.True(normal > easy, $"звичайний {normal}, легкий {easy}");
    }

    // ---------- режим вечірки ----------

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(8)]
    public void Party_with_only_bots_finishes_before_cap_with_scores_for_all(int bots)
    {
        var h = new PartyHarness("thinice", humans: 0, bots: bots, seed: 7);
        h.Start();
        var g = (Thinice)h.Game;
        Assert.Equal(1, g.RoundsTotal);
        var r = h.RunToEnd();
        Assert.NotNull(r);
        Assert.Equal(MinigameEnd.Finished, r!.How);
        Assert.Equal(bots, r.Scores.Count);
        Assert.Equal(bots, r.Places.Length);
        Assert.NotEmpty(r.Winners);
        Assert.True(h.Clock.UtcNow - h.StartedAt <= TimeSpan.FromMilliseconds(h.Host.CapMs));
        Assert.Equal(0, h.Ctx.Muted);
        Assert.Equal(0, h.Parent.Leaked);
        Assert.Equal(0, h.Parent.Finishes);
        Assert.All(r.Scores.Values, v => Assert.InRange(v, 0, bots - 1));
    }

    [Fact]
    public void Party_idle_humans_still_end_before_cap_thanks_to_thaw()
    {
        var h = new PartyHarness("thinice", humans: 3, bots: 0, seed: 5);
        h.Start();
        var r = h.RunToEnd();
        Assert.NotNull(r);
        Assert.Equal(MinigameEnd.Finished, r!.How);
        Assert.Equal(3, r.Scores.Count);
        Assert.Equal(0, h.Ctx.Muted);
        Assert.False(h.View(0).GetProperty("botOffer").GetBoolean());
    }

    [Fact]
    public void Party_scores_midway_cover_every_seat_and_rank_survivors_above_fallen()
    {
        var h = new PartyHarness("thinice", humans: 3, bots: 1, seed: 2);
        h.Start();
        h.TickSub(Thinice.ReadyFirst + 5);
        var c = ((Thinice)h.Game).Core;
        Sink(c, 1);
        h.TickSub(1);
        var s = ((IPartyMinigame)h.Game).PartyScores();
        Assert.Equal(4, s.Count);
        Assert.Equal(0, s[1]);
        Assert.Equal(1, s[0]);
        Assert.Equal(1, s[3]);
    }

    [Fact]
    public void Party_human_who_plays_well_beats_easy_bots()
    {
        var top = 0;
        const int Runs = 6;
        for (var seed = 1; seed <= Runs; seed++)
        {
            var h = new PartyHarness("thinice", humans: 1, bots: 3, level: LiveBots.Level.Easy, seed: seed);
            h.Start();
            var brain = new ThiniceBot(LiveBots.Level.Hard, 0);
            var rng = new Random(seed);
            var lastT = -1;
            var r = h.RunToEnd(x =>
            {
                var g = (Thinice)x.Game;
                var c = g.Core;
                if (g.Phase != Thinice.PhGo || c.T == lastT || !c.Bodies[0].In) return;
                lastT = c.T;
                var (a, jump) = brain.Think(c, 0, rng);
                x.Act(0, "move", a);
                if (jump) x.Act(0, "jump");
            })!;
            Assert.Equal(4, r.Scores.Count);
            if (r.Scores[0] == r.Scores.Values.Max()) top++;
        }
        output.WriteLine($"людина-скрипт угорі {top} з {Runs}");
        Assert.True(top >= Runs / 2 + 1, $"{top} з {Runs}");
    }

    [Fact]
    public void Party_same_seed_same_places()
    {
        static string Run()
        {
            var h = new PartyHarness("thinice", humans: 0, bots: 6, seed: 11);
            h.Start();
            var r = h.RunToEnd()!;
            return string.Join(",", r.Places) + "|" + h.Clock.UtcNow.ToUnixTimeMilliseconds();
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Party_leaver_mid_game_is_not_notified_and_game_finishes()
    {
        var h = new PartyHarness("thinice", humans: 2, bots: 2, seed: 4);
        h.Start();
        h.TickSub(Thinice.ReadyFirst + 10);
        h.Parent.Away.Add(1);
        var r = h.RunToEnd();
        Assert.NotNull(r);
        Assert.Equal(MinigameEnd.Finished, r!.How);
        Assert.Equal(4, r.Scores.Count);
    }
}
