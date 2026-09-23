using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Поле на компанію: «Мотоцикли гуртом», «Змійки гуртом» (2–4) і «Змійка на всіх» (1–4). Дуелі
/// «Мотоцикли» й «Змійка» не міняються — їхні тести живуть окремо й мають лишатись зеленими без правок.
/// </summary>
public class SnakePartyTests
{
    static readonly string[] Nicks = ["Оля", "Петро", "Іра", "Сашко"];

    static RoomHarness Party(string game, int players, object? options = null, bool start = true)
    {
        var h = new RoomHarness(game, options, seed: 42);
        for (var i = 0; i < players; i++) h.Join(Nicks[i]);
        if (start) h.Start();
        return h;
    }

    static RoomHarness Tron(int players, object? options = null) => Party("tron-party", players, options);

    static void Ready(RoomHarness h) => h.Tick(h.Room.Game is SnakePartyGame ? SnakeCore.StartTicks : TronGame.StartTicks);

    static int[][] Bodies(JsonElement v) =>
        [.. v.GetProperty("t").EnumerateArray().Select(b => b.EnumerateArray().Select(c => c.GetInt32()).ToArray())];

    static string LastLog(RoomHarness h) => h.Outbox.OfType<Journal>().Last().Text;

    static int Alive(JsonElement v) => v.GetProperty("al").GetInt32();

    // =============================================================================================
    // Каталог: дуелі лишились дуелями, компанія — окремими столами
    // =============================================================================================

    [Fact]
    public void The_duels_keep_two_seats_stakes_and_rating()
    {
        var registry = new Registry();
        foreach (var id in new[] { "tron", "snake" })
        {
            var info = registry.Info(id)!;
            Assert.Equal((2, 2), (info.MinPlayers, info.MaxPlayers));
            Assert.True(info.Rated);                        // Ело й ставки живуть лише на столі рівно на двох
            Assert.Equal(StartMode.WhenFull, info.Start);   // другий сів — поїхали, без «Почати»
        }
    }

    [Theory]
    [InlineData("tron-party", TronGame.TickMs)]
    [InlineData("snake-party", SnakeCore.TickMs)]
    public void The_party_tables_seat_two_to_four_and_start_on_the_hosts_word(string id, int tick)
    {
        var info = new Registry().Info(id)!;

        Assert.Equal((2, 4), (info.MinPlayers, info.MaxPlayers));
        Assert.Equal(StartMode.ByHost, info.Start);
        Assert.Equal(tick, info.TickMs);
        Assert.False(info.Rated);                   // Ело — для пар; тут таблиця перемог
        Assert.Equal("snake-modes", info.Module);   // малює той самий файл, що й дуель мотоциклів
    }

    [Fact]
    public void A_party_table_takes_no_stake()
    {
        var h = Party("tron-party", 2, new { stake = "10" }, start: false);

        Assert.Equal(0, h.Room.Stake);
    }

    // =============================================================================================
    // Мотоцикли гуртом: поле й старт
    // =============================================================================================

    [Fact]
    public void Two_riders_start_exactly_where_the_duel_puts_them()
    {
        var h = Tron(2);
        var duel = new SnakeCore(new Random(1), tailShrinks: false, apples: false);
        duel.Reset();
        var v = h.View(0);

        Assert.Equal(SnakeCore.W, v.GetProperty("width").GetInt32());
        Assert.Equal(SnakeCore.H, v.GetProperty("height").GetInt32());
        var t = Bodies(v);
        Assert.Equal(duel.A, t[0]);
        Assert.Equal(duel.B, t[1]);
        Assert.Empty(t[2]);
        Assert.Empty(t[3]);
    }

    [Fact]
    public void Three_or_four_riders_get_the_big_field_and_a_start_each()
    {
        var h = Tron(4);
        var v = h.View(null);

        Assert.Equal(ArenaGame.BigW, v.GetProperty("width").GetInt32());
        Assert.Equal(ArenaGame.BigH, v.GetProperty("height").GetInt32());
        var t = Bodies(v);
        Assert.All(t, b => Assert.Equal(ArenaCore.StartLen, b.Length));
        Assert.Equal(12, t.SelectMany(b => b).Distinct().Count());   // ніхто не стартує на чужій клітинці
        Assert.Equal(0b1111, Alive(v));
    }

    [Fact]
    public void The_field_option_overrides_the_automatic_size()
    {
        var small = Tron(4, new { field = "small" });
        var big = Tron(2, new { field = "big" });

        Assert.Equal(SnakeCore.W, small.View(null).GetProperty("width").GetInt32());
        Assert.Equal(ArenaGame.BigW, big.View(null).GetProperty("width").GetInt32());
    }

    [Theory]
    [InlineData(26, 18)]
    [InlineData(34, 24)]
    public void Straight_lines_from_the_start_never_meet_head_on_on_the_same_tick(int w, int hgt)
    {
        // Хто задумався на старті, має врізатись у стіну чи чужий слід, а не лоб у лоб у сусіда:
        // для кожної пари прямих трас точка перетину досягається в різні тики.
        var core = new ArenaCore(new Random(1), w, hgt, 4, tailShrinks: false, apples: 0);
        var starts = core.Starts(4);
        (int, int)[] d = [(1, 0), (0, 1), (-1, 0), (0, -1)];
        for (var i = 0; i < 4; i++)
            for (var j = i + 1; j < 4; j++)
            {
                var (xi, yi, di) = starts[i];
                var (xj, yj, dj) = starts[j];
                for (var t = 1; t < 40; t++)
                    Assert.False((xi + d[di].Item1 * t, yi + d[di].Item2 * t) == (xj + d[dj].Item1 * t, yj + d[dj].Item2 * t),
                        $"траси {i} і {j} зустрічаються на тику {t}");
            }
    }

    [Fact]
    public void The_lobby_shows_the_riders_of_everyone_who_already_sat()
    {
        var h = Party("tron-party", 3, start: false);
        var v = h.View(null);

        Assert.Equal(RoomStatus.Lobby, h.Room.Status);
        Assert.Equal([true, true, true, false], v.GetProperty("present").EnumerateArray().Select(e => e.GetBoolean()).ToArray());
        Assert.Equal(ArenaGame.BigW, v.GetProperty("width").GetInt32());
    }

    [Fact]
    public void Seats_are_named_by_colour()
    {
        var h = Tron(4);

        Assert.Equal(["жовтий", "зелений", "синій", "рожевий"], Enumerable.Range(0, 4).Select(h.Room.Game.SeatName).ToArray());
    }

    // =============================================================================================
    // Мотоцикли гуртом: вибування, переможець, нічия
    // =============================================================================================

    [Fact]
    public void A_crash_knocks_one_rider_out_and_the_last_one_riding_takes_the_round()
    {
        var h = Tron(3);
        Ready(h);
        h.Input(0, "turn", new { dir = 3 });   // жовтий — вгору, у стелю за дев'ять кроків
        h.Tick(9);

        var v = h.View(null);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);   // один вибув — решта їде далі
        Assert.Equal(0b110, Alive(v));
        Assert.Equal(3, v.GetProperty("place")[0].GetInt32());
        Assert.NotEqual(-1, v.GetProperty("crash")[0].GetInt32());
        Assert.Equal(ArenaCore.StartLen + 8, Bodies(v)[0].Length);   // слід розбитого лишається стіною

        // синій їде вниз по колонці, яку зелений уже перетнув на дев'ятому тику, — і в'їжджає в його слід
        h.Tick(3);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Room.Result!.Winners);
        Assert.StartsWith("Мотоцикли гуртом: раунд бере Петро (зелений). Рахунок: Оля 0 · Петро 1 · Іра 0", LastLog(h));
        var end = h.View(null);
        Assert.Equal("win", end.GetProperty("winner").GetString());
        Assert.Equal([1], end.GetProperty("winners").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        Assert.Equal(1, end.GetProperty("place")[1].GetInt32());
        Assert.Equal(2, end.GetProperty("place")[2].GetInt32());
    }

    [Fact]
    public void Everybody_crashing_on_the_same_tick_is_a_draw()
    {
        var h = Tron(2);
        Ready(h);
        h.Tick(SnakeCore.W - ArenaCore.StartLen);   // обидва прямо — у протилежні стіни одночасно

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(h.Room.Result!.Draw);
        Assert.Equal("draw", h.View(null).GetProperty("winner").GetString());
        Assert.Contains("нічия", LastLog(h));
    }

    [Fact]
    public void The_last_two_crashing_together_share_the_round_when_someone_went_out_before_them()
    {
        var h = Tron(3);
        Ready(h);
        h.Input(0, "turn", new { dir = 3 });   // жовтий — у стелю на 9-му тику
        h.Input(2, "turn", new { dir = 2 });   // синій — ліворуч уздовж третього ряду, у слід жовтого на 18-му
        h.Tick(2);
        h.Input(1, "turn", new { dir = 3 });   // зелений — угору по 28-й колонці, у стелю теж на 18-му
        h.Tick(16);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1, 2], h.Room.Result!.Winners);
        Assert.False(h.Room.Result.Draw);
        Assert.Contains("Петро і Іра врізались останніми в один тик", LastLog(h));
        Assert.Equal([0, 1, 1, 0], Wins(h));
    }

    static int[] Wins(RoomHarness h) => [.. h.View(null).GetProperty("wins").EnumerateArray().Select(e => e.GetInt32())];

    [Fact]
    public void Leaving_mid_round_knocks_you_out_and_the_rest_ride_on()
    {
        var h = Tron(3);
        Ready(h);
        h.Tick(2);
        h.Leave("Іра");

        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(0b011, Alive(h.View(null)));
        Assert.Contains("Іра встав з-за столу, решта їде далі", LastLog(h));

        h.Leave("Петро");                        // лишилась одна — раунд її
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        var finished = Assert.Single(h.Finished);
        Assert.Equal("Петро", finished.Seats[1]);   // той, хто встав, у результаті — серед тих, хто програв
    }

    [Fact]
    public void Turns_of_a_rider_who_is_out_are_ignored()
    {
        var h = Tron(3);
        Ready(h);
        h.Input(0, "turn", new { dir = 3 });
        h.Tick(9);
        var before = Views.Text(h.View(null));

        h.Input(0, "turn", new { dir = 0 });
        Assert.Equal(before, Views.Text(h.View(null)));
    }

    [Fact]
    public void The_series_follows_the_nick_through_rematches_and_resets_for_a_new_crew()
    {
        var h = Tron(3);
        Ready(h);
        h.Input(0, "turn", new { dir = 3 });
        h.Tick(12);                               // Петро бере раунд (див. вище)
        Assert.Equal(RoomStatus.Finished, h.Room.Status);

        h.Rematch("Оля");                         // місця обернулись, рахунок — за людиною
        var seatOfPetro = Array.IndexOf(h.Room.Seats, "Петро");
        Assert.NotEqual(1, seatOfPetro);
        Assert.Equal(1, Wins(h)[seatOfPetro]);
        Assert.Equal(1, Wins(h).Sum());

        // новий склад — нова серія
        h.Leave("Іра");
        h.Tick(400);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        h.Join("Сашко");
        h.Start();
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(0, Wins(h).Sum());
    }

    [Fact]
    public void The_bike_frame_is_heads_and_a_mask_and_nothing_more()
    {
        var h = Tron(3);
        h.Tick(1);
        var frame = Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);

        Assert.Equal(["h", "al", "startIn", "winner"], frame.EnumerateObject().Select(p => p.Name).ToArray());
        var heads = frame.GetProperty("h").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(4, heads.Length);
        Assert.Equal(-1, heads[3]);               // порожнє місце — ні голови, ні сліду
    }

    [Fact]
    public void Heads_from_the_frames_rebuild_the_trails_of_the_view()
    {
        var h = Tron(4);
        var start = Bodies(h.View(null));
        var trails = start.Select(b => b.ToList()).ToArray();
        var seen = start.Select(b => b.ToHashSet()).ToArray();
        Ready(h);
        h.Input(0, "turn", new { dir = 1 });
        h.Tick(4);
        h.Input(3, "turn", new { dir = 0 });
        h.Tick(4);
        foreach (var f in h.Outbox.OfType<RoomFrame>())
        {
            var heads = Views.Json(f.Frame).GetProperty("h").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            for (var s = 0; s < 4; s++)
                if (heads[s] >= 0 && seen[s].Add(heads[s])) trails[s].Insert(0, heads[s]);
        }

        var truth = Bodies(h.View(null));
        for (var s = 0; s < 4; s++) Assert.Equal(truth[s], trails[s].ToArray());
    }

    [Fact]
    public void A_crash_sends_the_whole_view_so_everyone_sees_the_cross()
    {
        var h = Tron(3);
        Ready(h);
        h.Input(0, "turn", new { dir = 3 });
        h.Tick(8);
        var views = h.Outbox.OfType<RoomViews>().Count();
        h.Tick(1);                                  // тут жовтий вилітає

        Assert.True(h.Outbox.OfType<RoomViews>().Count() > views);
    }

    [Fact]
    public void The_safety_net_ends_an_endless_bike_round_in_a_draw()
    {
        var h = Tron(3);
        ((ArenaGame)h.Room.Game).MaxMoves = 3;
        Ready(h);
        h.Tick(3);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(h.Room.Result!.Draw);
        Assert.Contains("час вийшов", LastLog(h));
    }

    [Fact]
    public void A_watcher_sees_the_same_field_as_the_riders()
    {
        var h = Tron(4);
        Ready(h);
        h.Tick(5);

        Assert.Equal(Views.Text(h.View(0)), Views.Text(h.View(null)));
        Assert.Equal(Views.Text(h.View(3)), Views.Text(h.View(null)));
    }

    // =============================================================================================
    // Змійки гуртом
    // =============================================================================================

    [Fact]
    public void Snakes_get_one_apple_for_two_and_two_apples_for_a_crowd()
    {
        Assert.Single(Party("snake-party", 2).View(null).GetProperty("ap").EnumerateArray());
        Assert.Equal(2, Party("snake-party", 3).View(null).GetProperty("ap").GetArrayLength());
    }

    [Fact]
    public void A_crashed_snake_leaves_the_field_and_the_round_goes_on()
    {
        var h = Party("snake-party", 3);
        Ready(h);
        h.Input(0, "turn", new { dir = 3 });
        h.Tick(9);

        var v = h.View(null);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Empty(Bodies(v)[0]);                  // змійка не лишає по собі стіни
        Assert.NotEqual(-1, v.GetProperty("crash")[0].GetInt32());
        Assert.Equal(0b110, Alive(v));
    }

    [Fact]
    public void A_snake_grows_on_an_apple_and_a_new_one_appears()
    {
        var h = Party("snake-party", 2);
        Ready(h);
        var apple = h.View(null).GetProperty("ap")[0].GetInt32();   // посередині поля, на ряд нижче жовтої
        var core = new ArenaCore(new Random(1), SnakeCore.W, SnakeCore.H, 4, true, 0);
        Assert.Equal(core.Cell(SnakeCore.W / 2, SnakeCore.H / 2), apple);

        // жовта: праворуч до колонки яблука, тоді вниз на три ряди
        h.Tick(SnakeCore.W / 2 - ArenaCore.StartLen);
        h.Input(0, "turn", new { dir = 1 });
        h.Tick(3);

        var v = h.View(null);
        Assert.Equal(ArenaCore.StartLen + 1, Bodies(v)[0].Length);
        Assert.DoesNotContain(apple, v.GetProperty("ap").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Single(v.GetProperty("ap").EnumerateArray());
    }

    [Fact]
    public void The_snake_frame_carries_whole_bodies_and_apples()
    {
        var h = Party("snake-party", 2);
        h.Tick(1);
        var frame = Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);

        Assert.Equal(["t", "ap", "al", "startIn", "winner"], frame.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void When_time_runs_out_the_longest_snake_takes_the_round()
    {
        var h = Party("snake-party", 2);
        var game = (ArenaGame)h.Room.Game;
        Ready(h);
        h.Tick(SnakeCore.W / 2 - ArenaCore.StartLen);
        h.Input(0, "turn", new { dir = 1 });
        h.Tick(3);                                   // жовта з'їла яблуко — на клітинку довша
        game.MaxMoves = game.Moves + 1;
        h.Tick(1);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Contains("час вийшов — найдовша в Оля", LastLog(h));
    }

    [Fact]
    public void The_same_seed_plays_out_the_same_snake_round()
    {
        static string Play()
        {
            var h = Party("snake-party", 4);
            Ready(h);
            for (var i = 0; i < 15; i++)
            {
                if (i == 3) h.Input(0, "turn", new { dir = 1 });
                if (i == 7) h.Input(2, "turn", new { dir = 0 });
                h.Tick(1);
            }
            return Views.Text(h.View(null));
        }

        Assert.Equal(Play(), Play());
    }

    // =============================================================================================
    // Змійка на всіх (1–4)
    // =============================================================================================

    [Fact]
    public void The_coop_seats_one_to_four_and_starts_on_the_hosts_word()
    {
        var info = new Registry().Info("snake-coop")!;

        Assert.Equal((1, 4), (info.MinPlayers, info.MaxPlayers));
        Assert.Equal(StartMode.ByHost, info.Start);
        Assert.Equal("Змійка на всіх", info.Title);
    }

    [Theory]
    [InlineData(1, new[] { 15 })]
    [InlineData(2, new[] { 0b1010, 0b0101 })]
    [InlineData(3, new[] { 0b1010, 0b0100, 0b0001 })]
    [InlineData(4, new[] { 0b1000, 0b0001, 0b0010, 0b0100 })]
    public void The_arrows_are_shared_out_among_whoever_is_at_the_table(int players, int[] masks)
    {
        var h = Party("snake-coop", players);
        var keys = h.View(0).GetProperty("keys").EnumerateArray().Select(e => e.GetInt32()).ToArray();

        Assert.Equal(masks, keys.Take(players).ToArray());
        Assert.All(keys.Skip(players), k => Assert.Equal(0, k));
        // кожна стрілка — рівно в одного
        Assert.Equal(15, keys.Aggregate(0, (a, k) => a | k));
        Assert.Equal(4, keys.Sum(k => System.Numerics.BitOperations.PopCount((uint)k)));
    }

    [Fact]
    public void Seat_chips_say_which_arrows_are_whose_even_before_the_start()
    {
        var h = Party("snake-coop", 3, start: false);
        var names = Enumerable.Range(0, 4).Select(h.Room.Game.SeatName).ToArray();

        Assert.Equal(["вгору-вниз", "вліво", "вправо", "вліво"], names);   // вільне — що дістанеться новенькому (учотирьох по одній)
    }

    [Fact]
    public void Four_drivers_each_turn_only_their_own_arrow()
    {
        var h = Party("snake-coop", 4);
        Ready(h);

        var foreign = h.Act(0, "turn", new { dir = 1 });
        Assert.False(foreign.Ok);
        Assert.Equal("Ти крутиш вгору", foreign.Message);

        Assert.True(h.Act(2, "turn", new { dir = 1 }).Ok);   // третій — вниз
        h.Tick(1);
        Assert.Equal(1, h.View(null).GetProperty("dir").GetInt32());
    }

    [Fact]
    public void Alone_you_steer_every_way_and_the_result_is_yours()
    {
        var h = Party("snake-coop", 1);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Ready(h);

        Assert.True(h.Act(0, "turn", new { dir = 3 }).Ok);
        h.Tick(1);
        Assert.True(h.Act(0, "turn", new { dir = 2 }).Ok);
        h.Tick(30);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Contains("Оля сам на сам — змійка доросла до", LastLog(h));
        Assert.Single(h.Scores);
    }

    [Fact]
    public void Three_drivers_share_the_length_and_nobody_wins_against_anybody()
    {
        var h = Party("snake-coop", 3);
        Ready(h);
        h.Tick(13);                                  // прямо — у праву стіну

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Empty(h.Room.Result!.Winners);
        Assert.Equal(3, h.Scores.Count);
        Assert.StartsWith("Змійка на всіх: Оля, Петро і Іра виростили змійку до", LastLog(h));
        Assert.Equal(3, h.Room.Result.Scores!.Count);
    }

    [Fact]
    public void When_a_driver_of_four_leaves_the_arrows_are_shared_out_again()
    {
        var h = Party("snake-coop", 4);
        Ready(h);
        h.Leave("Петро");                            // той, хто крутив «вправо»

        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        var keys = h.View(0).GetProperty("keys").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(15, keys.Aggregate(0, (a, k) => a | k));   // жодна стрілка не лишилась нічиєю
        Assert.Equal(0, keys[1]);
    }
}
