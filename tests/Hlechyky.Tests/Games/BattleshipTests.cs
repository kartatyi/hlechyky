using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Морський бій: розстановка, таймер, стрільба і — окремо — приховування. Гра <c>Hidden</c>, тож половина
/// перевірок тут саме про те, чого у виді бути НЕ повинно (TESTING.md §4, spec «Тести»).
/// </summary>
public class BattleshipTests
{
    // Дві ручні розстановки: горизонтальна для синього, вертикальна для червоного. Обидві валідні,
    // обидві відомі напам'ять — тільки так можна написати «потопив увесь флот» без випадковості.
    static readonly int[][] Blue =
        [[0, 1, 2, 3], [5, 6, 7], [20, 21, 22], [24, 25], [27, 28], [40, 41], [43], [45], [47], [49]];
    static readonly int[][] Red =
        [[0, 10, 20, 30], [2, 12, 22], [4, 14, 24], [6, 16], [8, 18], [50, 60], [52], [54], [56], [58]];

    /// <summary>Розстановка так, як її шле web/games/battleship.js.</summary>
    static object Fleet(int[][] ships) => new { ships = ships.Select(s => new { cells = s }).ToArray() };

    static RoomHarness Table(int seed = 42)
    {
        var h = new RoomHarness("battleship", seed: seed);
        h.Join("Оля");
        h.Join("Петро");
        return h;
    }

    /// <summary>Обидва розставились руками й натиснули «Готово» — далі бій.</summary>
    static RoomHarness Battle(int seed = 42)
    {
        var h = Table(seed);
        h.Act(0, "place", Fleet(Blue));
        h.Act(1, "place", Fleet(Red));
        h.Act(0, "ready");
        h.Act(1, "ready");
        return h;
    }

    static JsonElement Prop(JsonElement e, params string[] path)
    {
        foreach (var name in path) e = e.GetProperty(name);
        return e;
    }

    static int[] Ints(JsonElement e) => [.. e.EnumerateArray().Select(x => x.GetInt32())];

    // ---------- валідатор розстановки ----------

    [Fact]
    public void A_proper_fleet_passes_the_validator()
    {
        Assert.Null(BattleshipRules.Invalid(Blue));
        Assert.Null(BattleshipRules.Invalid(Red));
        Assert.Null(BattleshipRules.Invalid(BattleshipRules.Canonical()));
    }

    [Fact]
    public void Ships_may_not_touch_even_by_a_corner()
    {
        // однопалубний із 43 переїхав у 33: тепер він упирається кутом у трипалубний з 20..22
        int[][] ships = [[0, 1, 2, 3], [5, 6, 7], [20, 21, 22], [24, 25], [27, 28], [40, 41], [33], [45], [47], [49]];
        Assert.Equal("Кораблі не можуть торкатись навіть кутами", BattleshipRules.Invalid(ships));
    }

    [Fact]
    public void Ships_may_not_touch_side_by_side()
    {
        int[][] ships = [[0, 1, 2, 3], [5, 6, 7], [20, 21, 22], [24, 25], [27, 28], [40, 41], [42], [45], [47], [49]];
        Assert.Equal("Кораблі не можуть торкатись навіть кутами", BattleshipRules.Invalid(ships));
    }

    [Fact]
    public void A_ship_may_not_hang_over_the_edge()
    {
        int[][] ships = [[0, 1, 2, 3], [5, 6, 7], [20, 21, 22], [24, 25], [27, 28], [40, 41], [43], [45], [47], [100]];
        Assert.Equal("Корабель виліз за межі поля", BattleshipRules.Invalid(ships));
    }

    [Fact]
    public void A_row_that_wraps_around_the_board_is_not_a_straight_ship()
    {
        // 9 і 10 сусідні за номером, але на полі це різні краї — прямим кораблем це не буде
        int[][] ships = [[9, 10], [0, 1, 2, 3], [5, 6, 7], [20, 21, 22], [27, 28], [40, 41], [43], [45], [47], [49]];
        Assert.Equal("Корабель має бути прямий", BattleshipRules.Invalid(ships));
    }

    [Fact]
    public void A_bent_ship_is_refused()
    {
        int[][] ships = [[0, 1, 11], [5, 6, 7], [20, 21, 22], [24, 25], [27, 28], [40, 41], [43], [45], [47], [49]];
        Assert.Equal("Корабель має бути прямий", BattleshipRules.Invalid(ships));
    }

    [Fact]
    public void The_fleet_must_have_exactly_ten_ships()
    {
        Assert.Equal("Кораблів має бути рівно десять", BattleshipRules.Invalid(Blue.Take(9).ToArray()));
        Assert.Equal("Кораблів має бути рівно десять", BattleshipRules.Invalid([]));
        Assert.Equal("Кораблів має бути рівно десять", BattleshipRules.Invalid(null));
    }

    [Fact]
    public void The_fleet_composition_is_checked()
    {
        // десять кораблів, але два чотирипалубні замість одного
        int[][] ships = [[0, 1, 2, 3], [5, 6, 7, 8], [20, 21, 22], [24, 25], [27, 28], [40, 41], [43], [45], [47], [49]];
        Assert.Equal("Флот не той: один на чотири, два на три, три на два і чотири на одну клітинку",
            BattleshipRules.Invalid(ships));
    }

    [Fact]
    public void A_ship_longer_than_four_is_refused()
    {
        int[][] ships = [[0, 1, 2, 3, 4], [6, 7, 8], [20, 21, 22], [24, 25], [27, 28], [40, 41], [43], [45], [47], [49]];
        Assert.Equal("Корабель буває від однієї до чотирьох клітинок", BattleshipRules.Invalid(ships));
    }

    [Fact]
    public void Two_ships_on_the_same_cells_are_refused()
    {
        int[][] ships = [[0, 1, 2, 3], [5, 6, 7], [20, 21, 22], [24, 25], [27, 28], [40, 41], [40], [45], [47], [49]];
        Assert.Equal("Кораблі налазять один на одного", BattleshipRules.Invalid(ships));
    }

    [Fact]
    public void Two_hundred_random_fleets_are_all_legal()
    {
        for (var seed = 1; seed <= 200; seed++)
        {
            var fleet = BattleshipRules.RandomFleet(new Random(seed));
            Assert.Null(BattleshipRules.Invalid(fleet));
            Assert.Equal(BattleshipRules.Decks, fleet.Sum(s => s.Length));
        }
    }

    // ---------- фаза розстановки ----------

    [Fact]
    public void A_fresh_table_starts_in_the_placing_phase()
    {
        var h = Table();
        var v = h.View(0);

        Assert.Equal("placing", v.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("turn").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, v.GetProperty("placeUntil").ValueKind);
        Assert.Empty(Prop(v, "me", "ships").EnumerateArray());
        Assert.False(Prop(v, "me", "ready").GetBoolean());
        // у розстановці «на плаву» завжди весь флот: нуль тут був би і брехнею, і підказкою
        Assert.Equal(10, Prop(v, "enemy", "left").GetInt32());
    }

    [Fact]
    public void The_rival_cannot_count_from_my_board_whether_i_have_placed_anything()
    {
        var h = Table();
        var blind = h.View(1).GetProperty("enemy").ToString();

        h.Act(0, "place", Fleet(Blue));
        Assert.Equal(blind, h.View(1).GetProperty("enemy").ToString());

        // і в кадрі, який летить усім одразу, теж
        h.Tick(1);
        var frame = Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);
        Assert.Equal([10, 10], Ints(frame.GetProperty("left")));
    }

    [Fact]
    public void A_valid_placement_is_accepted_and_lands_in_my_own_view()
    {
        var h = Table();
        Assert.True(h.Act(0, "place", Fleet(Blue)).Ok);

        var ships = Prop(h.View(0), "me", "ships");
        Assert.Equal(10, ships.GetArrayLength());
        Assert.Equal(Blue[0], Ints(ships[0]));
    }

    [Fact]
    public void A_bad_placement_changes_nothing()
    {
        var h = Table();
        h.Act(0, "place", Fleet(Blue));
        var before = h.View(0).ToString();

        int[][] touching = [[0, 1, 2, 3], [5, 6, 7], [20, 21, 22], [24, 25], [27, 28], [40, 41], [42], [45], [47], [49]];
        var r = h.Act(0, "place", Fleet(touching));

        Assert.False(r.Ok);
        Assert.Equal("Кораблі не можуть торкатись навіть кутами", r.Message);
        Assert.Equal(before, h.View(0).ToString());
    }

    [Fact]
    public void Nonsense_in_the_payload_is_refused_politely()
    {
        var h = Table();
        Assert.Equal("Не зрозумів розстановку", h.Act(0, "place", new { boats = 1 }).Message);
        Assert.Equal("Не зрозумів розстановку", h.Act(0, "place", new { ships = "усі" }).Message);
        Assert.Equal("Тут так не ходять", h.Act(0, "торпеда").Message);
    }

    [Fact]
    public void Ready_needs_a_fleet_first()
    {
        var h = Table();
        var r = h.Act(0, "ready");

        Assert.False(r.Ok);
        Assert.Equal("Кораблів має бути рівно десять", r.Message);
        Assert.False(Prop(h.View(0), "me", "ready").GetBoolean());
    }

    [Fact]
    public void Random_scatters_a_legal_fleet_and_ready_locks_it()
    {
        var h = Table();
        Assert.True(h.Act(0, "random").Ok);

        var ships = Prop(h.View(0), "me", "ships").EnumerateArray().Select(Ints).ToArray();
        Assert.Null(BattleshipRules.Invalid(ships));

        Assert.True(h.Act(0, "ready").Ok);
        Assert.True(Prop(h.View(0), "me", "ready").GetBoolean());
        Assert.Equal("Ти вже сказав «Готово»", h.Act(0, "random").Message);
        Assert.Equal("Ти вже сказав «Готово»", h.Act(0, "place", Fleet(Blue)).Message);
    }

    [Fact]
    public void Clear_wipes_my_fleet_so_there_is_nothing_left_to_lock()
    {
        // «Скинути» в браузері стирає розстановку — сервер мусить забути її разом із людиною
        var h = Table();
        h.Act(0, "place", Fleet(Blue));

        Assert.True(h.Act(0, "clear").Ok);
        Assert.Empty(Prop(h.View(0), "me", "ships").EnumerateArray());
        Assert.Equal("Кораблів має бути рівно десять", h.Act(0, "ready").Message);

        Assert.True(h.Act(0, "clear").Ok);   // скинути порожнє поле — не помилка, просто нічого не стається
        Assert.True(h.Act(0, "place", Fleet(Blue)).Ok);
        Assert.True(h.Act(0, "ready").Ok);
        Assert.Equal("Ти вже сказав «Готово»", h.Act(0, "clear").Message);
    }

    [Fact]
    public void The_timer_does_not_drag_a_cleared_fleet_into_the_battle()
    {
        var h = Table();
        h.Act(0, "place", Fleet(Blue));
        h.Act(0, "clear");
        h.Act(1, "place", Fleet(Red));
        h.Tick(BattleshipRules.PlaceSeconds);

        var ships = Prop(h.View(0), "me", "ships").EnumerateArray().Select(Ints).ToArray();
        Assert.Null(BattleshipRules.Invalid(ships));
        Assert.NotEqual(Blue.Select(s => string.Join(",", s)), ships.Select(s => string.Join(",", s)));
    }

    [Fact]
    public void After_the_bell_placing_is_over_even_without_a_tick()
    {
        // spec просить перевіряти час і в Act: тик іде щосекунди, і за цю секунду ніхто не має встигнути
        var h = Table();
        h.Act(0, "place", Fleet(Blue));
        h.Clock.Advance(BattleshipRules.PlaceSeconds + 1);

        Assert.Equal("Бій уже почався, кораблі не рухаються", h.Act(1, "random").Message);
        Assert.Equal("Бій уже почався", h.Act(1, "ready").Message);

        var v = h.View(1);
        Assert.Equal("battle", v.GetProperty("phase").GetString());
        Assert.True(Prop(v, "me", "ready").GetBoolean());
        Assert.Null(BattleshipRules.Invalid(Prop(v, "me", "ships").EnumerateArray().Select(Ints).ToArray()));
        // синій, що встиг розставитись, лишився зі своїм флотом і стріляє першим
        Assert.Equal(Blue[0], Ints(Prop(h.View(0), "me", "ships")[0]));
        Assert.True(h.Act(0, "shoot", new { cell = 0 }).Ok);
    }

    [Fact]
    public void The_rival_sees_that_i_am_ready_but_not_what_i_placed()
    {
        var h = Table();
        h.Act(0, "place", Fleet(Blue));
        h.Act(0, "ready");

        var v = h.View(1);
        Assert.True(Prop(v, "enemy", "ready").GetBoolean());
        Assert.Equal(10, Prop(v, "enemy", "left").GetInt32());
        Assert.False(Views.Has(v.GetProperty("enemy"), "ships"));
    }

    [Fact]
    public void Both_ready_starts_the_battle_and_the_blue_shoots_first()
    {
        var h = Battle();
        var v = h.View(0);

        Assert.Equal("battle", v.GetProperty("phase").GetString());
        Assert.Equal(0, v.GetProperty("turn").GetInt32());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("placeUntil").ValueKind);
    }

    [Fact]
    public void The_placing_timer_scatters_a_fleet_for_whoever_did_not_place()
    {
        var h = Table();
        h.Act(0, "place", Fleet(Blue));
        h.Act(0, "ready");

        h.Tick(BattleshipRules.PlaceSeconds - 1);
        Assert.Equal("placing", h.View(1).GetProperty("phase").GetString());

        h.Tick(1);
        var v = h.View(1);
        Assert.Equal("battle", v.GetProperty("phase").GetString());
        Assert.True(Prop(v, "me", "ready").GetBoolean());
        Assert.Null(BattleshipRules.Invalid(Prop(v, "me", "ships").EnumerateArray().Select(Ints).ToArray()));
        Assert.Contains(h.Outbox.OfType<Journal>(), j => j.Text.Contains("час на розстановку вийшов"));
    }

    [Fact]
    public void The_timer_keeps_the_fleet_of_someone_who_placed_but_forgot_to_press_ready()
    {
        var h = Table();
        h.Act(0, "place", Fleet(Blue));
        h.Act(1, "place", Fleet(Red));
        h.Tick(BattleshipRules.PlaceSeconds);

        Assert.Equal("battle", h.View(0).GetProperty("phase").GetString());
        Assert.Equal(Blue[0], Ints(Prop(h.View(0), "me", "ships")[0]));
        Assert.Equal(Red[0], Ints(Prop(h.View(1), "me", "ships")[0]));
    }

    // ---------- бій ----------

    [Fact]
    public void You_cannot_shoot_before_the_fleets_are_placed()
    {
        var h = Table();
        Assert.Equal("Спершу розстав кораблі", h.Act(0, "shoot", new { cell = 0 }).Message);
    }

    [Fact]
    public void You_cannot_shoot_out_of_turn()
    {
        var h = Battle();
        var r = h.Act(1, "shoot", new { cell = 0 });

        Assert.False(r.Ok);
        Assert.Equal("Зараз не твій хід", r.Message);
        Assert.Equal(0, h.View(0).GetProperty("shots").GetInt32());
    }

    [Fact]
    public void A_shot_off_the_board_is_refused()
    {
        var h = Battle();
        Assert.Equal("Не зрозумів, куди стріляти", h.Act(0, "shoot", new { cell = 100 }).Message);
        Assert.Equal("Не зрозумів, куди стріляти", h.Act(0, "shoot", new { cell = -1 }).Message);
        Assert.Equal("Не зрозумів, куди стріляти", h.Act(0, "shoot", new { where = 5 }).Message);
        Assert.Equal(0, h.View(0).GetProperty("shots").GetInt32());
    }

    [Fact]
    public void A_hit_leaves_the_turn_with_the_shooter()
    {
        var h = Battle();
        var r = h.Act(0, "shoot", new { cell = 0 });   // 0 — ніс червоного чотирипалубного

        Assert.True(r.Ok);
        Assert.Equal("Влучив! Стріляй ще", r.Message);
        Assert.Equal(0, h.View(0).GetProperty("turn").GetInt32());
        Assert.Equal([0], Ints(Prop(h.View(0), "enemy", "hits")));
        Assert.Equal(1, h.View(0).GetProperty("shots").GetInt32());
    }

    [Fact]
    public void A_miss_hands_the_turn_over()
    {
        var h = Battle();
        var r = h.Act(0, "shoot", new { cell = 99 });

        Assert.Equal("Мимо", r.Message);
        Assert.Equal(1, h.View(0).GetProperty("turn").GetInt32());
        Assert.Equal([99], Ints(Prop(h.View(0), "enemy", "misses")));
        Assert.True(h.Act(1, "shoot", new { cell = 99 }).Ok);   // по своєму полю стріляти можна: воно чуже для нього
    }

    [Fact]
    public void You_cannot_shoot_the_same_cell_twice()
    {
        var h = Battle();
        h.Act(0, "shoot", new { cell = 0 });
        var before = h.View(0).ToString();

        var r = h.Act(0, "shoot", new { cell = 0 });
        Assert.False(r.Ok);
        Assert.Equal("Сюди вже стріляв", r.Message);
        Assert.Equal(before, h.View(0).ToString());
    }

    [Fact]
    public void A_sunk_ship_paints_its_surroundings_as_misses()
    {
        var h = Battle();
        var r = h.Act(0, "shoot", new { cell = 52 });   // однопалубний червоного

        Assert.Equal("Потопив! Стріляй ще", r.Message);
        var v = h.View(0);
        Assert.Equal([52], Ints(Prop(v, "enemy", "hits")));
        Assert.Equal([41, 42, 43, 51, 53, 61, 62, 63], Ints(Prop(v, "enemy", "misses")));
        Assert.Equal([52], Ints(Prop(v, "enemy", "sunk")[0]));
        Assert.Equal(9, Prop(v, "enemy", "left").GetInt32());
        Assert.Equal("Сюди вже стріляв", h.Act(0, "shoot", new { cell = 51 }).Message);
    }

    [Fact]
    public void Twenty_hits_sink_the_whole_fleet_and_end_the_game()
    {
        var h = Battle();
        foreach (var cell in Red.SelectMany(s => s))
            Assert.True(h.Act(0, "shoot", new { cell }).Ok);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Equal("Морський бій: Оля синій 10:0 Петро червоний, пострілів 20", h.Outbox.OfType<Journal>().Last().Text);

        var v = h.View(0);
        Assert.Equal("done", v.GetProperty("phase").GetString());
        Assert.Equal(0, Prop(v, "result", "winner").GetInt32());
        Assert.Equal([20, 0], Ints(Prop(v, "result", "shots")));
        Assert.Equal(0, Prop(v, "enemy", "left").GetInt32());
    }

    [Fact]
    public void Leaving_in_the_middle_of_the_battle_is_a_forfeit()
    {
        var h = Battle();
        h.Act(0, "shoot", new { cell = 0 });
        h.Leave("Петро");

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
    }

    // ---------- приховування (Hidden) ----------

    [Fact]
    public void My_rival_never_sees_my_ships()
    {
        var h = Battle();
        h.Act(0, "shoot", new { cell = 0 });

        var v = h.View(1);
        // мій власний флот у моєму ж виді бути мусить — інакше не намалюєш своє поле
        Assert.Equal(10, Prop(v, "me", "ships").GetArrayLength());
        Assert.False(Views.Has(v.GetProperty("enemy"), "ships"));
        // обидва поля разом дістаються лише глядачеві
        Assert.Equal(JsonValueKind.Null, v.GetProperty("boards").ValueKind);
        // жодна клітинка синього флоту не витекла: у чужій частині виду взагалі нема слова ships
        Assert.DoesNotContain("ships", v.GetProperty("enemy").ToString());
    }

    [Fact]
    public void Two_different_enemy_placements_look_exactly_the_same_from_my_seat()
    {
        var one = Table();
        one.Act(0, "place", Fleet(Blue));
        one.Act(1, "place", Fleet(Red));
        one.Act(0, "ready");
        one.Act(1, "ready");

        var two = Table();
        two.Act(0, "place", Fleet(Red));      // синій стоїть інакше…
        two.Act(1, "place", Fleet(Red));
        two.Act(0, "ready");
        two.Act(1, "ready");

        // …а червоний цієї різниці не бачить узагалі
        Assert.Equal(one.View(1).ToString(), two.View(1).ToString());
    }

    [Fact]
    public void A_watcher_sees_both_boards_and_no_ships_at_all()
    {
        var h = Battle();
        h.Act(0, "shoot", new { cell = 0 });
        h.Act(0, "shoot", new { cell = 99 });

        var v = h.View(null);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("me").ValueKind);
        Assert.Equal(2, v.GetProperty("boards").GetArrayLength());
        Assert.DoesNotContain("ships", v.ToString());
        Assert.Equal([0], Ints(Prop(v.GetProperty("boards")[1], "hits")));
        Assert.Equal([99], Ints(Prop(v.GetProperty("boards")[1], "misses")));
        Assert.Empty(Ints(Prop(v.GetProperty("boards")[0], "hits")));
    }

    [Fact]
    public void The_frame_carries_the_countdown_and_no_secrets()
    {
        var h = Table();
        h.Act(0, "place", Fleet(Blue));
        h.Act(0, "ready");
        h.Tick(3);

        var frame = Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);
        foreach (var name in new[] { "phase", "turn", "placeLeft", "ready", "left", "shots", "winner" })
            Assert.True(Views.Has(frame, name), name);
        Assert.Equal("placing", frame.GetProperty("phase").GetString());
        Assert.Equal(BattleshipRules.PlaceSeconds - 3, frame.GetProperty("placeLeft").GetInt32());
        Assert.True(frame.GetProperty("ready")[0].GetBoolean());
        Assert.False(frame.GetProperty("ready")[1].GetBoolean());
        Assert.DoesNotContain("ships", frame.ToString());
    }

    // ---------- каркас: тики, види, рематч, збереження ----------

    [Fact]
    public void Views_after_a_shot_come_with_the_next_tick()
    {
        // Rooms.Act не шле RoomViews іграм із TickMs > 0 — роздати їх може тільки тик, і Tick це вміє
        var h = Battle();
        var before = h.Outbox.OfType<RoomViews>().Count();
        h.Act(0, "shoot", new { cell = 0 });
        Assert.Equal(before, h.Outbox.OfType<RoomViews>().Count());

        h.Tick(1);
        Assert.Equal(before + 1, h.Outbox.OfType<RoomViews>().Count());

        // у бою без змін кімната мовчить: ні кадрів, ні видів
        var quiet = h.Outbox.Count;
        h.Tick(5);
        Assert.Equal(quiet, h.Outbox.Count);
    }

    [Fact]
    public void The_same_seed_scatters_the_same_fleet()
    {
        static string Scatter(int seed)
        {
            var h = Table(seed);
            h.Act(0, "random");
            h.Act(1, "random");
            return Prop(h.View(0), "me", "ships") + "|" + Prop(h.View(1), "me", "ships");
        }

        Assert.Equal(Scatter(7), Scatter(7));
        Assert.NotEqual(Scatter(7), Scatter(8));
    }

    [Fact]
    public void A_rematch_starts_from_an_empty_sea()
    {
        var h = Battle();
        foreach (var cell in Red.SelectMany(s => s)) h.Act(0, "shoot", new { cell });
        h.Rematch("Оля");

        Assert.Equal("Оля", h.Room.Seats[1]);          // місця обернув каркас
        var v = h.View(0);
        Assert.Equal("placing", v.GetProperty("phase").GetString());
        Assert.Empty(Prop(v, "me", "ships").EnumerateArray());
        Assert.False(Prop(v, "me", "ready").GetBoolean());
        Assert.Equal(0, v.GetProperty("shots").GetInt32());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("result").ValueKind);
    }

    [Fact]
    public void Load_of_a_saved_state_gives_back_the_same_view()
    {
        var h = Battle();
        h.Act(0, "shoot", new { cell = 0 });
        h.Act(0, "shoot", new { cell = 52 });
        h.Act(0, "shoot", new { cell = 99 });

        var saved = h.Room.Game.Save();
        Assert.NotNull(saved);

        var copy = new Battleship();
        copy.Load(saved!);
        Assert.Equal(Views.Text(h.Room.Game.View(0)), Views.Text(copy.View(0)));
        Assert.Equal(Views.Text(h.Room.Game.View(1)), Views.Text(copy.View(1)));
        Assert.Equal(Views.Text(h.Room.Game.View(null)), Views.Text(copy.View(null)));
    }

    [Fact]
    public void Battleship_is_in_the_catalog_as_a_hidden_board_game()
    {
        var game = Assert.Single(new Registry().Catalog, g => g.Id == "battleship");

        Assert.Equal("board", game.Group);
        Assert.True(game.Hidden);
        Assert.True(game.Rated);
        Assert.Equal(1000, game.TickMs);
        Assert.Equal(2, game.MinPlayers);
        Assert.Equal(2, game.MaxPlayers);
        Assert.Equal("battleship", game.Module);
    }

    [Fact]
    public void A_played_out_game_is_long_enough_to_pay_for_itself()
    {
        // TickMs > 0 → каркас не рахує ходів (Moves == 0), тож нагороду відмикає час: MinRewardSeconds = 20
        var h = Battle();
        h.Tick(25);
        foreach (var cell in Red.SelectMany(s => s)) h.Act(0, "shoot", new { cell });

        var e = Assert.Single(h.Finished);
        Assert.Equal(0, e.Moves);
        Assert.True((e.FinishedAt - e.StartedAt).TotalSeconds >= 20, $"партія тривала {(e.FinishedAt - e.StartedAt).TotalSeconds} с");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void A_thousand_ticks_of_one_room_are_instant()
    {
        // Партію навмисне не дограємо до кінця: Rooms.TickDue обходить кімнати не в статусі Playing, і
        // «тисяча тиків» після перемоги перетворилась би на тисячу порожніх обертів циклу.
        var h = Battle();
        var cells = Red.SelectMany(s => s).Take(BattleshipRules.Decks - 1).ToArray();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            // перші тики збирають і кадр, і види (влучання лишає хід), решта — найдешевший шлях Tick()
            if (i < cells.Length) Assert.True(h.Act(0, "shoot", new { cell = cells[i] }).Ok);
            h.Tick(1);
        }
        sw.Stop();

        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(cells.Length, h.View(0).GetProperty("shots").GetInt32());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"1000 тиків зайняли {sw.Elapsed}");
    }
}
