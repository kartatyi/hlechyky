using System.Diagnostics;
using System.Reflection;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Бомбер соло з ботами (30.09): «🤖 + бот» саджає трьох ботів, рівень — опцією столу. Як бот грає, міряємо на голому
/// <see cref="BomberCore"/> (боти проти ботів — скільки підриваються самі, хто виживає), а стіл, вердикт і нагороди —
/// через кімнату.
/// </summary>
[Collection(SerialPerf.Name)]
public class BomberBotTests(ITestOutputHelper output)
{
    static readonly LiveBots.Level E = LiveBots.Level.Easy, N = LiveBots.Level.Normal, H = LiveBots.Level.Hard;

    static RoomHarness Solo(string lvl = "normal", int seed = 7, object? extra = null)
    {
        var opts = new Dictionary<string, string> { ["botlvl"] = lvl };
        if (extra is not null)
            foreach (var p in extra.GetType().GetProperties()) opts[p.Name] = p.GetValue(extra)!.ToString()!;
        var h = new RoomHarness("bomber", opts, seed);
        h.Join("Оля");
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static BomberCore CoreOf(RoomHarness h) =>
        (BomberCore)typeof(Bomber).GetProperty("Core", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(h.Room.Game)!;

    /// <summary>Раунди «боти проти ботів» на голому ядрі: (перемоги по місцях, скільки разів підірвались самі, нічиї, тиків).</summary>
    static (int[] Wins, int[] Selfs, int Draws, long Ticks) Rounds(LiveBots.Level[] lv, int rounds, int seed)
    {
        var rng = new Random(seed);
        var wins = new int[4];
        var selfs = new int[4];
        var draws = 0;
        long ticks = 0;
        for (var r = 0; r < rounds; r++)
        {
            var core = new BomberCore(rng);
            core.Reset([true, true, true, true, false, false]);
            var bots = lv.Select((l, i) => new BomberBot(i, l)).ToArray();
            while (!core.RoundOver)
            {
                foreach (var b in bots) b.Think(core, rng);
                core.Step();
                foreach (var ev in core.Events) if (ev.How == BomberHow.Self) selfs[ev.Victim]++;
                core.Events.Clear();
            }
            ticks += core.Ticks;
            var w = core.LastStanding;
            if (w >= 0) wins[w]++;
            else draws++;
        }
        return (wins, selfs, draws, ticks);
    }

    // ---------- стіл ----------

    [Fact]
    public void Alone_the_table_asks_for_a_bot_and_the_level_is_an_option()
    {
        var h = new RoomHarness("bomber", new { botlvl = "hard" });
        h.Join("Оля");
        Assert.Equal(1, h.Room.Info.MinPlayers);
        Assert.Contains(h.Room.Info.Options!, o => o.Key == LiveBots.LevelOption.Key);
        Assert.Equal(LiveBots.AloneText, h.Start().Message);
        var v = h.View(0);
        Assert.True(v.GetProperty("botOffer").GetBoolean());
        Assert.False(v.GetProperty("botWanted").GetBoolean());
        Assert.Equal("hard", v.GetProperty("botLvl").GetString());
    }

    [Fact]
    public void Three_bots_sit_down_and_are_named_by_the_room()
    {
        var h = Solo();
        Assert.Null(h.Room.SafeSeatBot(0));
        for (var s = 1; s <= 3; s++) Assert.Equal(LiveBots.Name, h.Room.SafeSeatBot(s));
        Assert.Null(h.Room.SafeSeatBot(4));
        Assert.Equal([1, 2, 3], h.View(0).GetProperty("bot").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        var men = h.View(0).GetProperty("p");
        for (var s = 0; s <= 3; s++) Assert.True(men[s].GetProperty("alive").GetBoolean());
        Assert.False(men[4].GetProperty("alive").GetBoolean());
    }

    [Fact]
    public void Only_the_host_calls_bots_and_a_friend_sends_them_away()
    {
        var h = new RoomHarness("bomber");
        h.Join("Оля");
        h.Join("Петро");
        Assert.False(h.Act(1, LiveBots.Toggle, new { on = true }).Ok);           // не господар
        Assert.False(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);           // удвох бот не потрібен
        h.Leave("Петро");
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        h.Join("Петро");                                                          // підсів друг — партія людська
        Assert.True(h.Start().Ok, h.Reply.Message);
        Assert.Null(h.Room.SafeSeatBot(2));
        Assert.Empty(h.View(0).GetProperty("bot").EnumerateArray());
        Assert.False(h.View(0).GetProperty("p")[2].GetProperty("alive").GetBoolean());
    }

    [Fact]
    public void Bots_actually_play_they_walk_bomb_and_break_boxes()
    {
        var h = Solo();
        var core = CoreOf(h);
        var start = Enumerable.Range(1, 3).Select(s => core.Players[s].Cell).ToArray();
        var boxes = core.BoxCells().Length;
        var bombs = 0;
        for (var t = 0; t < 400 && h.Room.Status == RoomStatus.Playing; t++)
        {
            h.Tick();
            bombs += CoreOf(h).Bombs.Count(b => b.Owner > 0 && b.Fuse == BomberCore.FuseTicks - 1);
        }
        core = CoreOf(h);
        Assert.True(bombs >= 3, $"бомб від ботів: {bombs}");
        Assert.True(core.BoxCells().Length < boxes || h.Room.Status != RoomStatus.Playing);
        Assert.Contains(Enumerable.Range(1, 3), s => core.Players[s].Cell != start[s - 1] || !core.Players[s].Alive);
    }

    [Fact]
    public void A_passive_human_loses_the_match_to_the_bots_without_rewards()
    {
        var h = Solo("normal", seed: 3);
        for (var t = 0; t < 40000 && h.Room.Status == RoomStatus.Playing; t++) h.Tick();
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var fin = h.Finished.Single();
        Assert.Empty(fin.Result.Winners);
        Assert.StartsWith("🤖 Бот переміг", h.Room.Result!.Verdict);
        Assert.Empty(h.Awards);
        Assert.Empty(h.Scores);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    public void A_decent_player_beats_three_easy_bots(int seed)
    {
        // «Людина» тут — сильний бот за місцем Олі: грає тим самим вводом через ядро, як живий гравець.
        var h = Solo("easy", seed);
        var me = new BomberBot(0, H);
        var rng = new Random(seed);
        for (var t = 0; t < 40000 && h.Room.Status == RoomStatus.Playing; t++)
        {
            me.Think(CoreOf(h), rng);
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
        h.Tick(60);
        h.Leave("Оля");
        Assert.Empty(Assert.Single(h.Finished).Result.Winners);
    }

    [Fact]
    public void With_teams_it_is_two_on_two_the_human_with_a_bot()
    {
        var h = Solo("normal", 5, new { teams = "1" });
        var teams = h.View(0).GetProperty("teams").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal([0, 1, 0, 1, -1, -1], teams);
    }

    [Fact]
    public void Calling_bots_is_refused_mid_match_but_fine_after_it()
    {
        var h = Solo();
        Assert.False(h.Act(0, LiveBots.Toggle, new { on = false }).Ok);
    }

    // ---------- як грає ----------

    [Fact]
    public void Alone_on_the_field_bots_break_boxes_and_rarely_blow_themselves_up()
    {
        // Сам на полі (суперників нема — ніхто не заганяє в кут): скільки разів бот підірве сам себе за хвилину ламання ящиків.
        (int Deaths, int Boxes) Lone(LiveBots.Level lvl)
        {
            var (deaths, boxes) = (0, 0);
            for (var seed = 1; seed <= 6; seed++)
            {
                var rng = new Random(seed);
                var core = new BomberCore(rng);
                core.Reset([true, false, false, false, false, false]);
                var bot = new BomberBot(0, lvl);
                for (var t = 0; t < 1000; t++)
                {
                    bot.Think(core, rng);
                    core.Step();
                    if (core.Players[0].Alive) continue;
                    deaths++;
                    boxes += core.Broke[0];
                    core.Reset([true, false, false, false, false, false]);
                }
                boxes += core.Broke[0];
            }
            return (deaths, boxes);
        }
        var easy = Lone(E);
        var normal = Lone(N);
        var hard = Lone(H);
        output.WriteLine($"сам на полі, 6 хв: легкий {easy}, звичайний {normal}, сильний {hard} (смертей, ящиків)");
        Assert.True(hard.Deaths <= 1, $"сильний підірвався сам {hard.Deaths} разів");
        Assert.True(normal.Deaths <= 3, $"звичайний підірвався сам {normal.Deaths} разів");
        Assert.True(easy.Deaths > hard.Deaths, "легкий мав би інколи помилятися з утечею");
        Assert.True(hard.Boxes >= 60 && normal.Boxes >= 60, $"ящиків: звичайний {normal.Boxes}, сильний {hard.Boxes}");
    }

    [Fact]
    public void Hard_bots_outlive_easy_ones()
    {
        var r = Rounds([H, E, H, E], 40, 2);
        output.WriteLine($"сильні {r.Wins[0] + r.Wins[2]}, легкі {r.Wins[1] + r.Wins[3]}, нічиїх {r.Draws}");
        Assert.True(r.Wins[0] + r.Wins[2] > 2 * (r.Wins[1] + r.Wins[3]));
    }

    [Fact]
    public void Normal_beats_easy_and_hard_beats_normal()
    {
        var a = Rounds([N, E, N, E], 30, 3);
        var b = Rounds([H, N, H, N], 30, 4);
        output.WriteLine($"звич/легкі {a.Wins[0] + a.Wins[2]}:{a.Wins[1] + a.Wins[3]}, сильні/звич {b.Wins[0] + b.Wins[2]}:{b.Wins[1] + b.Wins[3]}");
        Assert.True(a.Wins[0] + a.Wins[2] > a.Wins[1] + a.Wins[3]);
        Assert.True(b.Wins[0] + b.Wins[2] > b.Wins[1] + b.Wins[3]);
    }

    [Fact]
    public void Bot_thinking_is_cheap()
    {
        var rng = new Random(9);
        var core = new BomberCore(rng);
        core.Reset([true, true, true, true, false, false]);
        var bots = new[] { new BomberBot(1, H), new BomberBot(2, H), new BomberBot(3, H) };
        var sw = new Stopwatch();
        var thinks = 0;
        for (var t = 0; t < 1000; t++)
        {
            if (core.RoundOver) { core.Reset([true, true, true, true, false, false]); }
            sw.Start();
            foreach (var b in bots) b.Think(core, rng);
            sw.Stop();
            thinks++;
            core.Step();
        }
        var per = sw.Elapsed.TotalMilliseconds / thinks;
        output.WriteLine($"три боти: {per:F4} мс на тик");
        Assert.True(per < 0.6, $"{per:F4} мс на тик за трьох");
    }
}
