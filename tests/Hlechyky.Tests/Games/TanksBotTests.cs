using System.Diagnostics;
using System.Reflection;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Танчики соло з ботами (30.09): «🤖 + бот» саджає трьох ботів-гравців (кожен сам або 2×2 з глеками), у хвилях людина
/// рушає сама. Як бот грає, міряємо на голому <see cref="TanksCore"/>, а стіл, вердикт і нагороди — через кімнату.
/// </summary>
[Collection(SerialPerf.Name)]
public class TanksBotTests(ITestOutputHelper output)
{
    static readonly LiveBots.Level E = LiveBots.Level.Easy, N = LiveBots.Level.Normal, H = LiveBots.Level.Hard;

    static RoomHarness Solo(string lvl = "normal", int seed = 7, string mode = "ffa")
    {
        var h = new RoomHarness("tanks", new Dictionary<string, string> { ["botlvl"] = lvl, ["mode"] = mode }, seed);
        h.Join("Оля");
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static TanksCore CoreOf(RoomHarness h) =>
        (TanksCore)typeof(Tanks).GetProperty("Core", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(h.Room.Game)!;

    /// <summary>Боти проти ботів «кожен сам» на голому ядрі: фраги по місцях за <paramref name="ticks"/> тиків.</summary>
    static int[] Frags(LiveBots.Level[] lv, int ticks, int seed)
    {
        var rng = new Random(seed);
        var core = new TanksCore(rng);
        core.SetSides(null, false, false);
        core.Reset([true, true, true, true, false, false]);
        var bots = lv.Select((l, i) => new TanksBot(i, l, i)).ToArray();
        for (var t = 0; t < ticks; t++)
        {
            foreach (var b in bots) b.Think(core, rng);
            core.Step();
            core.Events.Clear();
        }
        return [.. Enumerable.Range(0, 4).Select(i => core.Tanks[i].Frags)];
    }

    // ---------- стіл ----------

    [Fact]
    public void Alone_the_table_asks_for_a_bot_and_the_level_is_an_option()
    {
        var h = new RoomHarness("tanks", new { botlvl = "easy" });
        h.Join("Оля");
        Assert.Equal(1, h.Room.Info.MinPlayers);
        Assert.Contains(h.Room.Info.Options!, o => o.Key == LiveBots.LevelOption.Key);
        Assert.Equal(LiveBots.AloneText, h.Start().Message);
        var v = h.View(0);
        Assert.True(v.GetProperty("botOffer").GetBoolean());
        Assert.Equal("easy", v.GetProperty("botLvl").GetString());
    }

    [Fact]
    public void Three_bot_tanks_sit_down_and_are_named_by_the_room()
    {
        var h = Solo();
        Assert.Null(h.Room.SafeSeatBot(0));
        for (var s = 1; s <= 3; s++) Assert.Equal(LiveBots.Name, h.Room.SafeSeatBot(s));
        Assert.Equal([1, 2, 3], h.View(0).GetProperty("bot").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        var core = CoreOf(h);
        for (var s = 0; s <= 3; s++) Assert.True(core.Tanks[s].Plays && !core.Tanks[s].Bot);   // звичайні танки, не 🤖 хвиль
        Assert.Equal(8, h.View(0).GetProperty("need").GetInt32());                               // «за столом» на чотирьох
    }

    [Fact]
    public void A_friend_sends_the_bots_away()
    {
        var h = new RoomHarness("tanks");
        h.Join("Оля");
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        h.Join("Петро");
        Assert.True(h.Start().Ok, h.Reply.Message);
        Assert.Null(h.Room.SafeSeatBot(2));
        Assert.False(CoreOf(h).Tanks[2].Plays);
    }

    [Fact]
    public void Waves_start_alone_without_a_bot()
    {
        var h = new RoomHarness("tanks", new { mode = "waves" });
        h.Join("Оля");
        Assert.False(h.View(0).GetProperty("botOffer").GetBoolean());
        Assert.False(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.True(h.Start().Ok, h.Reply.Message);
        Assert.True(h.View(0).GetProperty("coop").GetInt32() > 0);
        h.Tick(400);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);                 // один у коопі — не «лишився один живий»
        Assert.True(CoreOf(h).BotsAlive() > 0);
    }

    [Fact]
    public void Teams_are_two_on_two_with_bases_and_the_match_ends()
    {
        var h = Solo("normal", 5, "teams");
        var teams = h.View(0).GetProperty("teams").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal([0, 1, 0, 1, -1, -1], teams);
        for (var t = 0; t < Tanks.TeamMatchTicks + 200 && h.Room.Status == RoomStatus.Playing; t++) h.Tick();
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        output.WriteLine(h.Room.Result!.Verdict + " / " + h.View(0).GetProperty("end"));
        Assert.Empty(h.Awards);
    }

    [Fact]
    public void Bots_actually_play_they_drive_shoot_and_frag()
    {
        var f = Frags([N, N, N, N], 1500, 3);
        output.WriteLine("фраги звичайних за хвилину: " + string.Join(", ", f));
        Assert.True(f.Sum() >= 4, $"фрагів за хвилину: {f.Sum()}");
    }

    [Fact]
    public void A_passive_human_loses_to_the_bots_without_rewards_or_table()
    {
        var h = Solo("normal", seed: 3);
        for (var t = 0; t < TanksCore.MatchTicks + 200 && h.Room.Status == RoomStatus.Playing; t++) h.Tick();
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Empty(h.Finished.Single().Result.Winners);
        output.WriteLine(h.Room.Result!.Verdict);
        Assert.StartsWith("🤖 Бот переміг", h.Room.Result!.Verdict);
        Assert.Empty(h.Awards);
        Assert.Empty(h.Scores);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    public void A_decent_player_beats_three_easy_bots(int seed)
    {
        // «Людина» — сильний бот за місцем Олі: грає тим самим вводом через ядро.
        var h = Solo("easy", seed);
        var me = new TanksBot(0, H);
        var rng = new Random(seed);
        for (var t = 0; t < TanksCore.MatchTicks + 200 && h.Room.Status == RoomStatus.Playing; t++)
        {
            if (h.View(null).GetProperty("phase").GetString() == "go") me.Think(CoreOf(h), rng);
            h.Tick();
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        output.WriteLine(h.Room.Result!.Verdict);
        Assert.Equal([0], h.Finished.Single().Result.Winners);
        Assert.Equal("🏆 Оля — перемога над трьома легкими ботами", h.Room.Result!.Verdict);
        Assert.Empty(h.Awards);
    }

    [Fact]
    public void Human_leaving_ends_the_match()
    {
        var h = Solo();
        h.Tick(80);
        h.Leave("Оля");
        Assert.Empty(Assert.Single(h.Finished).Result.Winners);
    }

    [Fact]
    public void Calling_bots_is_refused_mid_match()
    {
        var h = Solo();
        Assert.False(h.Act(0, LiveBots.Toggle, new { on = false }).Ok);
    }

    // ---------- як грає ----------

    [Fact]
    public void Hard_bots_outfrag_easy_ones_and_normal_beats_easy()
    {
        var a = Frags([H, E, H, E], 3000, 1);
        var b = Frags([N, E, N, E], 3000, 2);
        output.WriteLine($"сильні/легкі {a[0] + a[2]}:{a[1] + a[3]}, звичайні/легкі {b[0] + b[2]}:{b[1] + b[3]}");
        Assert.True(a[0] + a[2] > 2 * (a[1] + a[3]));
        Assert.True(b[0] + b[2] > b[1] + b[3]);
    }

    [Fact]
    public void Hard_bot_dodges_or_shoots_down_shells_the_easy_one_eats_them()
    {
        // Відкрите поле; «гармата» (незнищенна) раз на секунду стріляє вздовж ряду; бот стоїть на тому ж ряду.
        int Hits(LiveBots.Level lvl)
        {
            var rng = new Random(4);
            var core = new TanksCore(rng);
            core.SetSides(null, false, false);
            core.Reset([true, true, false, false, false, false]);
            for (var y = 1; y < core.H - 1; y++)
                for (var x = 1; x < core.W - 1; x++) core.Tiles[core.Cell(x, y)] = TankTile.Free;
            var gun = core.Tanks[1];
            gun.Cell = core.Cell(3, 7);
            gun.Dir = 0;
            core.Tanks[0].Cell = core.Cell(12, 7);
            core.Tanks[0].Dir = 2;
            var bot = new TanksBot(0, lvl);
            for (var t = 0; t < 2500; t++)
            {
                gun.Shield = 1000;
                gun.Cell = core.Cell(3, core.Y(core.Tanks[0].Cell));      // гармата тримає ряд бота
                gun.Move = -1;
                gun.Dir = core.X(core.Tanks[0].Cell) > 3 ? 0 : 2;
                if (t % 25 == 0) core.Press(1);
                bot.Think(core, rng);
                core.Step();
                core.Events.Clear();
            }
            return core.Tanks[0].Deaths;
        }
        var easy = Hits(E);
        var hard = Hits(H);
        output.WriteLine($"підбили: легкого {easy}, сильного {hard} (100 пострілів)");
        Assert.True(hard * 2 < easy, $"легкий {easy}, сильний {hard}");
    }

    [Fact]
    public void Bot_thinking_is_cheap()
    {
        var rng = new Random(9);
        var core = new TanksCore(rng);
        core.SetSides(null, false, false);
        core.Reset([true, true, true, true, false, false]);
        var bots = new[] { new TanksBot(1, H), new TanksBot(2, H), new TanksBot(3, H) };
        var sw = new Stopwatch();
        for (var t = 0; t < 1000; t++)
        {
            sw.Start();
            foreach (var b in bots) b.Think(core, rng);
            sw.Stop();
            core.Step();
            core.Events.Clear();
        }
        var per = sw.Elapsed.TotalMilliseconds / 1000;
        output.WriteLine($"три боти: {per:F4} мс на тик");
        Assert.True(per < 0.6, $"{per:F4} мс на тик за трьох");
    }
}
