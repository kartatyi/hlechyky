using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Змійки гуртом» (snake-party) на власному ядрі <see cref="SnakeArenaCore"/> — прохід №3: мертва змійка стає
/// яблуками (№150), вибулі кидають яблука й камінці (№151), серія до N перемог з автостартом (№152), «на час» із
/// відродженням (№153), бонуси (№154), тор (№155; і в дуелі «Змійка»).
/// </summary>
public class SnakeArenaTests(ITestOutputHelper output)
{
    static readonly string[] Nicks = ["Оля", "Петро", "Іра", "Сашко"];

    static RoomHarness Party(int players, object? options = null, bool start = true, int seed = 42)
    {
        var h = new RoomHarness("snake-party", options, seed: seed);
        for (var i = 0; i < players; i++) h.Join(Nicks[i]);
        if (start) h.Start();
        return h;
    }

    static void Ready(RoomHarness h) => h.Tick(SnakeCore.StartTicks);

    /// <summary>Розпакувати тіло з рядка (як клієнт): голова числом, далі r/d/l/u до наступної клітинки.</summary>
    static int[] Unpack(string s, int w, int h)
    {
        if (s.Length == 0) return [];
        var i = 0;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        var cells = new List<int> { int.Parse(s[..i]) };
        for (; i < s.Length; i++)
        {
            var c = cells[^1];
            int x = c % w, y = c / w;
            (x, y) = s[i] switch { 'r' => (x + 1, y), 'l' => (x - 1, y), 'd' => (x, y + 1), _ => (x, y - 1) };
            cells.Add((y + h) % h * w + (x + w) % w);
        }
        return [.. cells];
    }

    static int[][] Bodies(JsonElement v)
    {
        int w = v.GetProperty("width").GetInt32(), h = v.GetProperty("height").GetInt32();
        return [.. v.GetProperty("b").EnumerateArray().Select(b => Unpack(b.GetString()!, w, h))];
    }

    static int[] Ints(JsonElement v, string key) =>
        v.TryGetProperty(key, out var a) ? [.. a.EnumerateArray().Select(e => e.GetInt32())] : [];

    static string LastLog(RoomHarness h) => h.Outbox.OfType<Journal>().Last().Text;

    // =============================================================================================
    // Каталог і типові опції
    // =============================================================================================

    [Fact]
    public void The_party_table_seats_two_to_four_draws_with_its_own_module_and_defaults_to_the_old_rules()
    {
        var info = new Registry().Info("snake-party")!;

        Assert.Equal((1, 4), (info.MinPlayers, info.MaxPlayers));   // сама — з «🤖 + бот» (двоє ботів)
        Assert.Equal(StartMode.ByHost, info.Start);
        Assert.Equal(SnakeCore.TickMs, info.TickMs);
        Assert.False(info.Rated);
        Assert.Equal("snake-party", info.Module);
        var defaults = info.Options!.ToDictionary(o => o.Key, o => o.Default);
        Assert.Equal("auto", defaults["field"]);
        Assert.Equal("1", defaults["series"]);   // як було: один раунд і «Ще раз»
        Assert.Equal("last", defaults["mode"]);
        Assert.Equal("0", defaults["wrap"]);
        Assert.Equal("0", defaults["bonus"]);
    }

    [Fact]
    public void The_duel_keeps_walls_unless_the_table_asks_for_a_torus()
    {
        var info = new Registry().Info("snake")!;
        Assert.Equal("0", info.Options!.Single(o => o.Key == "wrap").Default);
        Assert.True(info.Rated);
    }

    [Fact]
    public void Snakes_get_one_apple_for_two_and_two_apples_for_a_crowd()
    {
        Assert.Single(Ints(Party(2).View(null), "ap"));
        Assert.Equal(2, Ints(Party(3).View(null), "ap").Length);
    }

    [Fact]
    public void The_default_frame_is_small_and_carries_packed_bodies()
    {
        var h = Party(2);
        h.Tick(1);
        var frame = Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);

        Assert.Equal(["b", "ap", "al", "startIn", "winner"], frame.EnumerateObject().Select(p => p.Name).ToArray());
        var b = Bodies(h.View(null));
        Assert.Equal(SnakeArenaCore.StartLen, b[0].Length);
        Assert.Equal(SnakeArenaCore.StartLen, b[1].Length);
        Assert.Empty(b[2]);
    }

    [Fact]
    public void A_body_is_packed_through_the_edge_of_a_torus()
    {
        var core = new SnakeArenaCore(new Random(1), 26, 18, 4, 0, wrap: true);
        core.Reset([0, 1], 0);
        for (var i = 0; i < 23; i++) core.Step();   // жовта з (3, 6) праворуч — тепер голова на x = 0

        Assert.True(core.Alive[0]);
        Assert.Equal(core.Cell(0, 6), core.Bodies[0][0]);
        Assert.Equal("156ll", core.Pack(0, new StringBuilder()));
        Assert.Equal(core.Bodies[0], Unpack(core.Pack(0, new StringBuilder()), 26, 18));
    }

    [Fact]
    public void A_snake_grows_on_an_apple_and_a_new_one_appears()
    {
        var h = Party(2);
        Ready(h);
        var apple = Ints(h.View(null), "ap")[0];
        Assert.Equal(9 * 26 + 13, apple);   // посередині поля

        h.Tick(SnakeCore.W / 2 - SnakeArenaCore.StartLen);
        h.Input(0, "turn", new { dir = 1 });
        h.Tick(3);

        var v = h.View(null);
        Assert.Equal(SnakeArenaCore.StartLen + 1, Bodies(v)[0].Length);
        Assert.DoesNotContain(apple, Ints(v, "ap"));
        Assert.Single(Ints(v, "ap"));
    }

    [Fact]
    public void The_same_seed_plays_out_the_same_round_with_every_option_on()
    {
        static string Play()
        {
            var h = Party(4, new { mode = "time", bonus = "1", wrap = "1" });
            Ready(h);
            for (var i = 0; i < 200; i++)
            {
                if (i % 13 == 3) h.Input(i % 4, "turn", new { dir = (i / 13) % 4 });
                h.Tick(1);
            }
            return Views.Text(h.View(null));
        }

        Assert.Equal(Play(), Play());
    }

    // =============================================================================================
    // №150: мертва змійка стає яблуками
    // =============================================================================================

    [Fact]
    public void A_crashed_snake_crumbles_into_apples_and_the_round_goes_on()
    {
        var h = Party(3);
        Ready(h);
        h.Input(0, "turn", new { dir = 3 });
        h.Tick(9);   // жовта з ряду 8 угору — у стіну

        var v = h.View(null);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Empty(Bodies(v)[0]);
        Assert.Equal(0b110, v.GetProperty("al").GetInt32());
        Assert.Equal([3, 2 * 34 + 3], Ints(v, "lt"));   // кожна друга клітинка тіла (3, 0), (3, 1), (3, 2)
    }

    [Fact]
    public void Loot_apples_feed_whoever_gets_there_and_never_respawn()
    {
        var core = new SnakeArenaCore(new Random(1), 26, 18, 4, 0);
        core.Reset([0, 1], 0);
        core.Items[core.Cell(4, 6)] = SnakeArenaCore.Loot;   // прямо перед жовтою
        core.Step();

        Assert.Equal(4, core.Bodies[0].Count);
        Assert.Empty(core.CellsOf(SnakeArenaCore.Loot));
    }

    [Fact]
    public void Crumbs_stop_at_the_loot_ceiling()
    {
        var core = new SnakeArenaCore(new Random(1), 26, 18, 4, 0);
        core.Reset([0, 1], 0);
        core.Grow[0] = 120;
        for (var i = 0; i < 22; i++) core.Step();
        for (var i = 0; i < 3; i++) { core.Turn(0, 1); core.Step(); core.Turn(0, 2); for (var k = 0; k < 20; k++) core.Step(); core.Turn(0, 1); core.Step(); core.Turn(0, 0); for (var k = 0; k < 20; k++) core.Step(); }
        core.Kill(0, 0);

        Assert.True(core.CellsOf(SnakeArenaCore.Loot).Length <= SnakeArenaCore.LootCap);
    }

    // =============================================================================================
    // №151: вибулі кидають яблука й камінці
    // =============================================================================================

    [Fact]
    public void An_eliminated_player_throws_an_apple_once_per_five_seconds()
    {
        var h = Party(3);
        Ready(h);
        h.Input(0, "turn", new { dir = 3 });
        h.Tick(9);
        var far = 20 * 34 + 2;   // лівий нижній кут — далеко від усіх голів

        var first = h.Act(0, "drop", new { cell = far, k = "a" });
        Assert.True(first.Ok, first.Message);
        Assert.Contains(far, Ints(h.View(null), "lt"));
        var again = h.Act(0, "drop", new { cell = far + 2, k = "a" });
        Assert.False(again.Ok);
        Assert.Contains("5 секунд", again.Message);
        Assert.False(h.Act(1, "drop", new { cell = far + 2, k = "a" }).Ok);   // жива кидати не може

        h.Tick(1);
        Assert.Equal(SnakePartyGame.DropCooldown - 1, Ints(Views.Json(h.Room.Game.Frame()), "dc")[0]);
        var game = (SnakePartyGame)h.Room.Game;
        var rng = new Random(3);
        for (var i = 0; i < SnakePartyGame.DropCooldown; i++) { Steer(h, game.Arena, rng); h.Tick(); }
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        var core = game.Arena;
        var free = Enumerable.Range(0, core.W * core.H).First(c => core.Occ[c] == 0 && core.Items[c] == 0
            && Enumerable.Range(0, 4).All(s => !core.Alive[s] || core.Dist(c, core.Bodies[s][0]) > 3));
        var later = h.Act(0, "drop", new { cell = free, k = "a" });
        Assert.True(later.Ok, later.Message);
    }

    [Fact]
    public void Nothing_lands_within_three_cells_of_a_head()
    {
        var h = Party(3);
        Ready(h);
        h.Input(0, "turn", new { dir = 3 });
        h.Tick(9);
        var green = Bodies(h.View(null))[1][0];

        var r = h.Act(0, "drop", new { cell = green - 2, k = "r" });
        Assert.False(r.Ok);
        Assert.Contains("близько", r.Message);
    }

    [Fact]
    public void A_thrown_rock_stops_a_snake_and_the_round_goes_to_the_last_one()
    {
        var h = Party(3);
        Ready(h);
        h.Input(0, "turn", new { dir = 3 });
        h.Tick(9);
        var green = Bodies(h.View(null))[1][0];   // зелена їде ліворуч рядом 15
        Assert.Equal(15 * 34 + 21, green);

        Assert.True(h.Act(0, "drop", new { cell = green - 4, k = "r" }).Ok);
        h.Tick(4);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([2], h.Room.Result!.Winners);
        Assert.Equal(green - 4, h.View(null).GetProperty("crash")[1].GetInt32());
    }

    [Fact]
    public void A_rock_melts_after_three_seconds_and_the_pad_throws_it_before_the_leader()
    {
        var core = new SnakeArenaCore(new Random(1), 26, 18, 4, 0, wrap: true);
        core.Reset([0, 1], 0);
        core.Alive[1] = false;
        core.Bodies[1].Clear();

        Assert.Null(core.Drop(1, -1, 'r'));
        var rock = core.CellsOf(SnakeArenaCore.Rock).Single();
        Assert.Equal(core.Cell(7, 6), rock);   // чотири клітинки перед носом жовтої
        core.Turn(0, 1);   // жовта звертає і не влітає
        for (var i = 0; i < SnakeArenaCore.RockTicks; i++) core.Step();
        Assert.Empty(core.CellsOf(SnakeArenaCore.Rock));
    }

    [Fact]
    public void Throwing_is_off_in_the_timed_mode_where_nobody_stays_out()
    {
        var h = Party(3, new { mode = "time" });
        Ready(h);
        h.Input(0, "turn", new { dir = 3 });
        h.Tick(9);

        Assert.False(h.Act(0, "drop", new { cell = 20 * 34 + 2, k = "a" }).Ok);
    }

    // =============================================================================================
    // №152: серія до N перемог з автостартом
    // =============================================================================================

    [Fact]
    public void A_series_to_three_starts_the_next_round_by_itself_and_ends_on_the_third_win()
    {
        var h = Party(2, new { series = "3" });
        Ready(h);
        for (var round = 1; round <= 3; round++)
        {
            h.Input(0, "turn", new { dir = 3 });
            h.Tick(7);   // жовта з ряду 6 угору — у стіну
            if (round == 3) break;

            var v = h.View(null);
            Assert.Equal(RoomStatus.Playing, h.Room.Status);   // без «Ще раз»
            Assert.Equal("win", v.GetProperty("winner").GetString());
            Assert.Equal(round, Ints(v, "wins")[1]);
            Assert.Equal(round, v.GetProperty("round").GetInt32());
            h.Tick(SnakePartyGame.PauseTicks - 1);
            Assert.NotEqual(JsonValueKind.Null, h.View(null).GetProperty("winner").ValueKind);
            h.Tick(1);
            v = h.View(null);
            Assert.Equal(JsonValueKind.Null, v.GetProperty("winner").ValueKind);
            Assert.Equal(round + 1, v.GetProperty("round").GetInt32());
            Assert.Equal(SnakePartyGame.NextStartTicks, v.GetProperty("startIn").GetInt32());
            Assert.Equal(SnakeArenaCore.StartLen, Bodies(v)[0].Length);
            h.Tick(SnakePartyGame.NextStartTicks);
        }

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Room.Result!.Winners);
        Assert.Contains("серія до 3 — перемога — Петро", LastLog(h));
    }

    [Fact]
    public void Leaving_mid_series_hands_the_series_to_the_one_who_stayed()
    {
        var h = Party(2, new { series = "5" });
        Ready(h);
        h.Leave(Nicks[1]);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
    }

    // =============================================================================================
    // №153: «на час» із відродженням
    // =============================================================================================

    [Fact]
    public void In_the_timed_mode_a_crashed_snake_is_back_in_two_seconds_with_three_cells()
    {
        var h = Party(2, new { mode = "time" });
        Ready(h);
        h.Input(0, "turn", new { dir = 3 });
        h.Tick(7);

        var v = h.View(null);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(0b10, v.GetProperty("al").GetInt32());
        Assert.True(Ints(v, "rs")[0] > 0);
        h.Tick(SnakePartyGame.RespawnTicks);
        v = h.View(null);
        Assert.Equal(1, v.GetProperty("al").GetInt32() & 1);
        Assert.True(SnakeArenaCore.StartLen == Bodies(v)[0].Length, v.GetProperty("b")[0].GetString());
        Assert.Equal(-1, v.GetProperty("crash")[0].GetInt32());
    }

    [Fact]
    public void The_timed_round_ends_at_ninety_seconds_and_the_longest_takes_it()
    {
        var h = Party(2, new { mode = "time", wrap = "1" });
        Ready(h);
        h.Tick(SnakeCore.W / 2 - SnakeArenaCore.StartLen);
        h.Input(0, "turn", new { dir = 1 });
        h.Tick(3);                       // жовта з'їла яблуко
        h.Input(0, "turn", new { dir = 0 });
        var game = (SnakePartyGame)h.Room.Game;
        h.Tick(SnakePartyGame.TimedMoves - game.Moves - 1);
        var last = Bodies(h.View(null));
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        h.Tick(1);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Contains("час вийшов", LastLog(h));
        var len = Bodies(h.View(null)).Take(2).Select(b => b.Length).ToArray();
        var best = len.Max();
        var expected = len.All(l => l == best) ? [] : Enumerable.Range(0, 2).Where(s => len[s] == best).ToArray();
        Assert.Equal(expected, h.Room.Result!.Winners);
        Assert.NotNull(last);
    }

    // =============================================================================================
    // №154: бонуси
    // =============================================================================================

    static SnakeArenaCore Duo(bool bonuses = false, bool wrap = false)
    {
        var core = new SnakeArenaCore(new Random(1), 26, 18, 4, 0, wrap, bonuses);
        core.Reset([0, 1], 0);
        return core;
    }

    [Fact]
    public void A_golden_apple_grows_the_snake_by_three()
    {
        var core = Duo();
        core.Items[core.Cell(4, 6)] = SnakeArenaCore.Gold;
        core.Step();
        Assert.Equal(4, core.Bodies[0].Count);
        Assert.Contains(core.Events, e => e.Kind == 'g' && e.Seat == 0);
        core.Step();
        core.Step();
        core.Step();
        Assert.Equal(6, core.Bodies[0].Count);
    }

    [Fact]
    public void Scissors_cut_the_tail_in_half()
    {
        var core = Duo(wrap: true);
        core.Grow[0] = 9;
        for (var i = 0; i < 9; i++) core.Step();
        Assert.Equal(12, core.Bodies[0].Count);
        var ahead = core.Ahead(core.Bodies[0][0], core.Dirs[0]).Cell;
        core.Items[ahead] = SnakeArenaCore.Scissors;
        core.Step();

        Assert.Equal(6, core.Bodies[0].Count);
        Assert.Equal(6, core.Occ.Count(o => o == 1));   // відрізане звільнило клітинки
    }

    [Fact]
    public void A_snowflake_slows_everyone_else_for_three_seconds()
    {
        var core = Duo(wrap: true);
        core.Items[core.Cell(4, 6)] = SnakeArenaCore.Slow;
        core.Step();
        Assert.Equal(SnakeArenaCore.SlowTicks, core.SlowLeft[1]);
        Assert.Equal(0, core.SlowLeft[0]);

        var green = core.Bodies[1][0];
        for (var i = 0; i < 4; i++) core.Step();
        Assert.Equal(green - 2, core.Bodies[1][0]);   // за чотири тики — дві клітинки
        for (var i = 0; i < SnakeArenaCore.SlowTicks; i++) core.Step();
        Assert.Equal(0, core.SlowLeft[1]);
    }

    [Fact]
    public void Bonuses_appear_only_when_the_table_turns_them_on()
    {
        var on = Duo(bonuses: true, wrap: true);
        var off = Duo(bonuses: false, wrap: true);
        for (var i = 0; i <= SnakeArenaCore.BonusGapMin; i++) { on.Step(); off.Step(); }

        Assert.True(on.BonusCell >= 0);
        Assert.Contains(on.BonusKind, new[] { SnakeArenaCore.Gold, SnakeArenaCore.Scissors, SnakeArenaCore.Slow });
        Assert.Equal(-1, off.BonusCell);
        for (var i = 0; i < SnakeArenaCore.BonusTicks; i++) on.Step();
        Assert.True(on.BonusCell < 0 || on.Items[on.BonusCell] == on.BonusKind);   // старий бонус зник разом із клітинкою
    }

    // =============================================================================================
    // №155: тор
    // =============================================================================================

    [Fact]
    public void On_a_torus_the_party_snake_crawls_through_the_wall()
    {
        var h = Party(2, new { wrap = "1" });
        Ready(h);
        h.Tick(SnakeCore.W - SnakeArenaCore.StartLen);   // жовта праворуч — за край

        var v = h.View(null);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.True(v.GetProperty("wrap").GetBoolean());
        Assert.Equal(6 * 26, Bodies(v)[0][0]);
    }

    [Fact]
    public void On_a_torus_the_duel_snake_crawls_through_the_wall_and_with_walls_it_still_crashes()
    {
        foreach (var wrap in new[] { "1", "0" })
        {
            var h = new RoomHarness("snake", new { wrap }, seed: 42);
            h.Join(Nicks[0]);
            h.Join(Nicks[1]);
            h.Tick(SnakeCore.StartTicks);
            h.Tick(SnakeCore.W - 3);

            Assert.Equal(wrap == "1" ? RoomStatus.Playing : RoomStatus.Finished, h.Room.Status);
        }
    }

    // =============================================================================================
    // Швидкодія: 4 змійки на великому полі з усіма опціями
    // =============================================================================================

    [Fact]
    [Trait("Category", "Perf")]
    public void Four_snakes_with_every_option_tick_and_frame_within_budget()
    {
        foreach (var opts in new object[] { new { field = "big" }, new { field = "big", mode = "time", bonus = "1", wrap = "1" } })
        {
            var h = Party(4, opts, seed: 7);
            var game = (SnakePartyGame)h.Room.Game;
            var rng = new Random(7);
            var sw = new Stopwatch();
            int played = 0, frameMax = 0;
            while (played < 3000)
            {
                if (h.Room.Status != RoomStatus.Playing)
                {
                    Assert.True(h.Rematch().Ok);
                    if (h.Room.Status == RoomStatus.Lobby) Assert.True(h.Start().Ok);
                    continue;
                }
                var core = game.Arena;
                var counting = core.StartIn > 0;
                sw.Start();
                if (!counting) Steer(h, core, rng);
                h.Tick();
                sw.Stop();
                if (!counting) played++;
                frameMax = Math.Max(frameMax, Views.Text(game.Frame()).Length);
            }
            var ms = sw.Elapsed.TotalMilliseconds / played;
            output.WriteLine($"{Views.Text(opts)}: {ms:F4} мс на тик, кадр до {frameMax} Б");
            Assert.True(ms <= 0.25, $"{ms:F4} мс на тик");
            Assert.True(frameMax <= 1536, $"кадр {frameMax} Б");
        }
    }

    /// <summary>«Рука»: живі змійки звертають, коли попереду зайнято, інколи — просто так.</summary>
    static void Steer(RoomHarness h, SnakeArenaCore core, Random rng)
    {
        for (var s = 0; s < 4; s++)
        {
            if (!core.Alive[s]) continue;
            var dir = core.Dirs[s];
            var (c, ok) = core.Ahead(core.Bodies[s][0], dir);
            if (ok && core.Occ[c] == 0 && core.Items[c] != SnakeArenaCore.Rock && rng.Next(8) > 0) continue;
            foreach (var d in new[] { (dir + 1) % 4, (dir + 3) % 4 }.OrderBy(_ => rng.Next()))
            {
                var (n, ok2) = core.Ahead(core.Bodies[s][0], d);
                if (ok2 && core.Occ[n] == 0) { h.Input(s, "turn", new { dir = d }); break; }
            }
        }
    }
}
