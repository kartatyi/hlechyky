using System.Diagnostics;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «🤖 + бот» у змійці, мотоциклах і змійках гуртом: сам без бота не почнеш, бот справді грає (їсть, не врізається,
/// сильний — помітно краще за легкого), переміг бот — переможців нема, людина одна — рейтинг і нагороди не рушать.
/// </summary>
public class SnakeBotTests(ITestOutputHelper output)
{
    static RoomHarness Alone(string game, string lvl = "normal", int seed = 1, object? extra = null)
    {
        var opts = new Dictionary<string, string> { ["botlvl"] = lvl };
        if (extra is not null)
            foreach (var p in extra.GetType().GetProperties()) opts[p.Name] = p.GetValue(extra)!.ToString()!;
        var h = new RoomHarness(game, opts, seed);
        h.Join("Оля");
        return h;
    }

    static RoomHarness WithBot(string game, string lvl = "normal", int seed = 1, object? extra = null)
    {
        var h = Alone(game, lvl, seed, extra);
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    /// <summary>
    /// Людина-манекен у змійці: кружляє квадратиком 2×2 біля свого старту (вниз, вліво, вгору, вправо) — довжина 3
    /// так ніколи не вріжеться в себе, тож партія кінчається, лише коли схибить бот (або з'їсть манекен яблуко).
    /// </summary>
    static readonly int[] Loop = [1, 2, 3, 0];

    /// <summary>Скільки тиків прожив бот проти манекена і чи програв він (false — вийшов час або розбився манекен).</summary>
    static (int Ticks, bool BotLost) Duel(string lvl, int seed, int cap = 4000)
    {
        var h = WithBot("snake", lvl, seed);
        h.Tick(SnakeCore.StartTicks);
        var t = 0;
        for (; t < cap && h.Room.Status == RoomStatus.Playing; t++)
        {
            h.Input(0, "turn", new { dir = Loop[t % 4] });
            h.Tick();
        }
        var lost = h.Room.Status == RoomStatus.Finished && h.Room.Result!.Winners.SequenceEqual([0]);
        return (t, lost);
    }

    // ---------------------------------------------------------------- змійка

    [Fact]
    public void Snake_alone_asks_for_a_bot_and_then_the_bot_takes_the_empty_seat()
    {
        var h = Alone("snake");
        var no = h.Start();
        Assert.False(no.Ok);
        Assert.Equal(LiveBots.AloneText, no.Message);
        var v = h.View(0);
        Assert.True(v.GetProperty("botOffer").GetBoolean());
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.Equal(1, h.View(0).GetProperty("bot").GetInt32());
        Assert.True(h.Start().Ok, h.Reply.Message);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(LiveBots.Name, h.Room.SafeSeatBot(1));
        Assert.Null(h.Room.SafeSeatBot(0));
        Assert.Equal("normal", h.View(0).GetProperty("botLvl").GetString());
    }

    [Fact]
    public void Snake_bot_beats_a_player_who_never_turns_and_gets_no_win()
    {
        var h = WithBot("snake");
        h.Tick(SnakeCore.StartTicks + 60);                 // жовта мовчки влітає в праву стіну
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var fin = Assert.Single(h.Finished);
        Assert.Empty(fin.Result.Winners);
        Assert.StartsWith("🤖 Бот переміг", fin.Result.Verdict);
        Assert.Contains("🤖 бот", fin.Result.Text);
        Assert.Contains("без нагород", fin.Result.Text);
    }

    [Fact]
    public void Snake_bot_eats_apples_and_does_not_crash_on_its_own()
    {
        var h = WithBot("snake", "normal", seed: 3);
        h.Tick(SnakeCore.StartTicks);
        var t = 0;
        for (; t < 600 && h.Room.Status == RoomStatus.Playing; t++)
        {
            h.Input(0, "turn", new { dir = Loop[t % 4] });
            h.Tick();
        }
        var len = h.View(0).GetProperty("b").GetArrayLength();
        output.WriteLine($"тиків {t}, довжина бота {len}");
        Assert.True(len >= 8, $"бот мав би наїсти яблук: довжина {len}");
    }

    [Fact]
    public void Snake_easy_bot_loses_to_a_dummy_and_hard_bot_lives_much_longer()
    {
        int easyTicks = 0, hardTicks = 0, easyLost = 0, hardLost = 0;
        for (var seed = 1; seed <= 6; seed++)
        {
            var e = Duel("easy", seed);
            var d = Duel("hard", seed);
            easyTicks += e.Ticks; hardTicks += d.Ticks;
            if (e.BotLost) easyLost++;
            if (d.BotLost) hardLost++;
        }
        var normal = Enumerable.Range(1, 6).Select(seed => Duel("normal", seed)).ToArray();
        output.WriteLine($"легкий: {easyTicks / 6} тиків, програв {easyLost}/6; звичайний: {normal.Sum(x => x.Ticks) / 6} тиків, програв {normal.Count(x => x.BotLost)}/6; сильний: {hardTicks / 6} тиків, програв {hardLost}/6");
        Assert.True(easyLost >= 4, $"легкий програв манекену лише {easyLost} з 6");
        Assert.True(hardTicks > easyTicks * 2, $"сильний {hardTicks} проти легкого {easyTicks}");
    }

    [Fact]
    public void Snake_win_over_the_bot_names_its_level()
    {
        for (var seed = 1; seed <= 6; seed++)
        {
            var h = WithBot("snake", "easy", seed);
            h.Tick(SnakeCore.StartTicks);
            for (var t = 0; t < 4000 && h.Room.Status == RoomStatus.Playing; t++)
            {
                h.Input(0, "turn", new { dir = Loop[t % 4] });
                h.Tick();
            }
            if (h.Room.Result is not { Winners: [0] } r) continue;
            Assert.Equal("🏆 Оля — перемога над легким ботом, 1:0", r.Verdict);
            Assert.Contains("без нагород", r.Text);
            AssertNoRewards("snake", Assert.Single(h.Finished));   // і перемога над ботом Ело не рушить
            return;
        }
        Assert.Fail("за шість партій манекен так і не переміг легкого бота");
    }

    [Fact]
    public void Snake_friend_sits_down_and_the_duel_is_human_again()
    {
        var h = Alone("snake");
        h.Act(0, LiveBots.Toggle, new { on = true });
        h.Join("Петро");                                   // стіл повний — стартує сам, як завжди
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Null(h.Room.SafeSeatBot(1));
        Assert.Equal(JsonValueKindNull, h.View(0).GetProperty("bot").ValueKind);
        h.Tick(SnakeCore.StartTicks);
        h.Input(1, "turn", new { dir = 1 });               // зелена — у нижню стіну
        h.Tick(20);
        var fin = Assert.Single(h.Finished);
        Assert.Equal([0], fin.Result.Winners);             // людська партія — людська перемога
        Assert.Null(fin.Result.Verdict);
    }

    static readonly System.Text.Json.JsonValueKind JsonValueKindNull = System.Text.Json.JsonValueKind.Null;

    [Fact]
    public void Snake_player_leaves_mid_game_and_the_game_ends()
    {
        var h = WithBot("snake");
        h.Tick(SnakeCore.StartTicks + 3);
        h.Leave("Оля");
        var fin = Assert.Single(h.Finished);
        Assert.Empty(fin.Result.Winners);
    }

    [Theory]
    [InlineData("snake")]
    [InlineData("tron")]
    [InlineData("snake-party")]
    public void A_game_with_the_bot_moves_no_rating_and_pays_nothing(string game)
    {
        var h = WithBot(game);
        h.Tick(400);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        AssertNoRewards(game, Assert.Single(h.Finished));
    }

    /// <summary>Подія кінця партії через справжні Rewards: ні Ело, ні черепків, ні ачівок.</summary>
    static void AssertNoRewards(string game, RoomFinishedEvent e)
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(e);
        var r = rig.Ratings.Of("Оля", game);
        Assert.Equal(1000, r.Elo);
        Assert.Equal(0, r.Games);
        Assert.Equal(0, rig.Paid("Оля", ""));
    }

    /// <summary>
    /// Найкращий із кількох замірів: перший заодно прогріває JIT, а шумний сусід на спільному сервері CI (GitHub) за
    /// один прохід міг подвоїти середнє — тести швидкості змійки падали в CI 02.10 і 08.10, хоча код не мінявся.
    /// Поріг той самий: міряємо, скільки думає бот, а не скільки заважають сусіди.
    /// </summary>
    static double BestOf(int tries, Func<double> measure)
    {
        var best = double.MaxValue;
        for (var i = 0; i < tries; i++) best = Math.Min(best, measure());
        return best;
    }

    [Fact]
    public void Snake_bot_thinks_fast()
    {
        var ticks = 0;
        var per = BestOf(3, () =>
        {
            var h = WithBot("snake", "hard", seed: 2);
            h.Tick(SnakeCore.StartTicks);
            var sw = new Stopwatch();
            ticks = 0;
            for (var t = 0; t < 1000 && h.Room.Status == RoomStatus.Playing; t++)
            {
                h.Input(0, "turn", new { dir = Loop[t % 4] });
                sw.Start();
                h.Tick();
                sw.Stop();
                ticks++;
            }
            return sw.Elapsed.TotalMilliseconds / ticks;
        });
        output.WriteLine($"змійка, сильний бот: {per:F4} мс на тик кімнати ({ticks} тиків)");
        Assert.True(per < 0.2, $"{per:F4} мс");
    }

    // ---------------------------------------------------------------- мотоцикли

    [Fact]
    public void Tron_alone_asks_for_a_bot_and_the_bot_rides_in_the_empty_seat()
    {
        var h = Alone("tron", "hard");
        Assert.Equal(LiveBots.AloneText, h.Start().Message);
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.Equal(1, h.View(0).GetProperty("bot").GetInt32());
        Assert.True(h.Start().Ok, h.Reply.Message);
        Assert.Equal(LiveBots.Name, h.Room.SafeSeatBot(1));
        Assert.Equal("hard", h.View(0).GetProperty("botLvl").GetString());
        Assert.Equal(LiveBots.Name, h.View(0).GetProperty("bots")[1].GetString());
    }

    [Fact]
    public void Tron_bot_outrides_a_player_who_never_turns()
    {
        var h = WithBot("tron");
        h.Tick(TronGame.StartTicks + 60);
        var fin = Assert.Single(h.Finished);
        Assert.Empty(fin.Result.Winners);
        Assert.Equal("🤖 Бот переміг", fin.Result.Verdict);
        Assert.Contains("🤖 бот", fin.Result.Text);
    }

    /// <summary>Мотоцикли: «людина» їздить мозком ботів гурту (той самий ввід) — рівний пересічний суперник.</summary>
    static int? TronRound(string lvl, int seed)
    {
        var h = WithBot("tron", lvl, seed);
        var game = (TronGame)h.Room.Game;
        var brain = new BotBrain();
        var me = new string?[] { "манекен", null };
        var rng = new Random(seed * 7919);
        h.Tick(TronGame.StartTicks);
        for (var t = 0; t < 700 && h.Room.Status == RoomStatus.Playing; t++)
        {
            brain.Think(game.Arena, me, rng);
            h.Tick();
        }
        return h.Room.Result is { } r ? (r.Winners.Length == 1 ? 0 : r.Verdict == "🤖 Бот переміг" ? 1 : -1) : null;
    }

    [Fact]
    public void Tron_hard_bot_wins_more_often_than_easy_against_the_same_rider()
    {
        int easy = 0, hard = 0, easyHuman = 0;
        const int n = 16;
        for (var seed = 1; seed <= n; seed++)
        {
            var e = TronRound("easy", seed);
            var d = TronRound("hard", seed);
            if (e == 1) easy++;
            if (e == 0) easyHuman++;
            if (d == 1) hard++;
        }
        output.WriteLine($"з {n}: легкий бот переміг {easy} (людина {easyHuman}), сильний — {hard}");
        Assert.True(hard > easy, $"сильний {hard}, легкий {easy}");
        Assert.True(easyHuman >= n / 2, $"пересічна людина перемогла легкого лише {easyHuman} з {n}");
    }

    [Fact]
    public void Tron_friend_sits_down_and_nobody_rides_for_the_bot()
    {
        var h = Alone("tron");
        h.Act(0, LiveBots.Toggle, new { on = true });
        h.Join("Петро");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Null(h.Room.SafeSeatBot(1));
        Assert.Equal(JsonValueKindNull, h.View(0).GetProperty("bot").ValueKind);
        Assert.False(h.View(0).TryGetProperty("bots", out _));
    }

    [Fact]
    public void Tron_player_leaves_mid_game_and_the_game_ends_without_winners()
    {
        var h = WithBot("tron");
        h.Tick(TronGame.StartTicks + 3);
        h.Leave("Оля");
        var fin = Assert.Single(h.Finished);
        Assert.Empty(fin.Result.Winners);
    }

    [Fact]
    public void Tron_bot_thinks_fast()
    {
        var ticks = 0;
        var per = BestOf(3, () =>
        {
            var h = WithBot("tron", "hard", seed: 4, extra: new { series = "5" });
            var game = (TronGame)h.Room.Game;
            var brain = new BotBrain();
            var me = new string?[] { "манекен", null };
            var rng = new Random(4);
            var sw = new Stopwatch();
            ticks = 0;
            for (var t = 0; t < 1000 && h.Room.Status == RoomStatus.Playing; t++)
            {
                brain.Think(game.Arena, me, rng);
                sw.Start();
                h.Tick();
                sw.Stop();
                ticks++;
            }
            return sw.Elapsed.TotalMilliseconds / ticks;
        });
        output.WriteLine($"мотоцикли, сильний бот: {per:F4} мс на тик кімнати ({ticks} тиків)");
        Assert.True(per < 0.2, $"{per:F4} мс");
    }

    // ---------------------------------------------------------------- змійки гуртом

    [Fact]
    public void Party_alone_gets_two_bots_on_the_big_field()
    {
        var h = Alone("snake-party");
        Assert.Equal(LiveBots.AloneText, h.Start().Message);
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.Equal([1, 2], h.View(0).GetProperty("bot").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        Assert.True(h.Start().Ok, h.Reply.Message);
        var v = h.View(0);
        Assert.Equal([true, true, true, false], v.GetProperty("present").EnumerateArray().Select(e => e.GetBoolean()).ToArray());
        Assert.Equal(SnakePartyGame.BigW, v.GetProperty("width").GetInt32());
        Assert.Equal(ArenaGame.BotNames[0], h.Room.SafeSeatBot(1));
        Assert.Equal(ArenaGame.BotNames[1], h.Room.SafeSeatBot(2));
        Assert.Null(h.Room.SafeSeatBot(3));
    }

    [Fact]
    public void Party_player_crashes_and_the_round_goes_to_the_bots_at_once()
    {
        var h = WithBot("snake-party");
        h.Tick(SnakeCore.StartTicks + 60);                 // людина мовчки влітає в стіну; боти мають її пережити
        var fin = Assert.Single(h.Finished);
        Assert.Empty(fin.Result.Winners);
        Assert.StartsWith("🤖", fin.Result.Verdict);
        Assert.Contains("🤖 Залізяка", fin.Result.Text);
        Assert.Contains("без нагород", fin.Result.Text);
    }

    /// <summary>Змійки гуртом проти манекена-квадратика: скільки тиків живуть боти і чи виграв манекен.</summary>
    static (int Ticks, bool Human) Party(string lvl, int seed)
    {
        var h = WithBot("snake-party", lvl, seed);
        h.Tick(SnakeCore.StartTicks);
        var t = 0;
        for (; t < 1500 && h.Room.Status == RoomStatus.Playing; t++)
        {
            h.Input(0, "turn", new { dir = Loop[t % 4] });
            h.Tick();
        }
        return (t, h.Room.Result is { Winners: [0] });
    }

    [Fact]
    public void Party_easy_bots_fall_to_a_dummy_and_hard_bots_last_longer()
    {
        int easyT = 0, hardT = 0, easyWins = 0;
        for (var seed = 1; seed <= 5; seed++)
        {
            var e = Party("easy", seed);
            var d = Party("hard", seed);
            easyT += e.Ticks; hardT += d.Ticks;
            if (e.Human) easyWins++;
        }
        output.WriteLine($"легкі: {easyT / 5} тиків, манекен виграв {easyWins}/5; сильні: {hardT / 5} тиків");
        Assert.True(easyWins >= 2, $"манекен переміг легких лише {easyWins} з 5");
        Assert.True(hardT > easyT, $"сильні {hardT} проти легких {easyT}");
    }

    [Fact]
    public void Party_timed_mode_with_bots_runs_to_the_bell()
    {
        var h = WithBot("snake-party", extra: new { mode = "time" });
        h.Tick(SnakeCore.StartTicks + SnakePartyGame.TimedMoves + 5);
        var fin = Assert.Single(h.Finished);
        Assert.DoesNotContain(1, fin.Result.Winners);
        Assert.DoesNotContain(2, fin.Result.Winners);
    }

    [Fact]
    public void Party_friend_sits_down_and_no_bots_come()
    {
        var h = Alone("snake-party");
        h.Act(0, LiveBots.Toggle, new { on = true });
        h.Join("Петро");
        Assert.Equal(0, h.View(0).GetProperty("bot").GetArrayLength());
        Assert.True(h.Start().Ok);
        Assert.Equal(2, h.View(0).GetProperty("present").EnumerateArray().Count(e => e.GetBoolean()));
        Assert.Null(h.Room.SafeSeatBot(2));
    }

    [Fact]
    public void Party_player_leaves_and_the_bots_do_not_play_on_alone()
    {
        var h = WithBot("snake-party");
        h.Tick(SnakeCore.StartTicks + 3);
        h.Leave("Оля");
        var fin = Assert.Single(h.Finished);
        Assert.Empty(fin.Result.Winners);
    }

    /// <summary>Тик кімнати «на час» з бонусами: мс на тик. Двоє людей стоять — у «на час» вони відроджуються й знову мчать у стіну.</summary>
    static double PartyTick(RoomHarness h)
    {
        h.Tick(SnakeCore.StartTicks);
        var sw = new Stopwatch();
        var ticks = 0;
        for (var t = 0; t < 740 && h.Room.Status == RoomStatus.Playing; t++)
        {
            h.Input(0, "turn", new { dir = Loop[t % 4] });
            sw.Start();
            h.Tick();
            sw.Stop();
            ticks++;
        }
        return sw.Elapsed.TotalMilliseconds / ticks;
    }

    [Fact]
    public void Party_bots_think_fast()
    {
        var opts = new Dictionary<string, string> { ["mode"] = "time", ["bonus"] = "1" };
        PartyTick(WithBot("snake-party", "hard", seed: 5, extra: new { mode = "time", bonus = "1" }));   // прогрів JIT
        var people = new RoomHarness("snake-party", opts, 5);
        foreach (var n in new[] { "Оля", "Петро", "Іра" }) people.Join(n);
        Assert.True(people.Start().Ok);
        var bare = PartyTick(people);
        var h = WithBot("snake-party", "hard", seed: 5, extra: new { mode = "time", bonus = "1" });
        var bots = PartyTick(h);

        // сама думка: той самий знімок поля, що складає гра, і рішення — на полі, яке боти наїли за партію
        var core = ((SnakePartyGame)h.Room.Game).Arena;
        var brain = new SnakeBrain();
        const int runs = 1000;
        var think = BestOf(3, () =>
        {
            var rng = new Random(1);
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < runs; i++)
            {
                var b = 1 + i % 2;
                if (!core.Alive[b]) continue;
                brain.Begin(core.W, core.H, core.Wrap);
                for (var s2 = 0; s2 < 4; s2++) if (core.Alive[s2]) brain.Body(core.Bodies[s2], core.Grow[s2], rival: s2 != b);
                for (var c = 0; c < core.Items.Length; c++)
                    if (core.Items[c] == SnakeArenaCore.Rock) brain.Block(c);
                    else if (core.Items[c] != SnakeArenaCore.None) brain.Food(c);
                brain.Decide(core.Bodies[b][0], core.Dirs[b], core.Bodies[b].Count, LiveBots.Level.Hard, rng);
            }
            return sw.Elapsed.TotalMilliseconds / runs;
        });
        output.WriteLine($"змійки гуртом: тик кімнати {bots:F4} мс з двома сильними ботами, {bare:F4} мс утрьох людьми; "
            + $"думка сильного бота {think:F4} мс (довжини {string.Join("/", core.Bodies.Select(x => x.Count))})");
        Assert.True(think < 0.2, $"{think:F4} мс на бота");
    }
}
