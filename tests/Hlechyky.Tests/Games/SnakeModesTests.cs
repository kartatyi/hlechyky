using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Два режими змійки: «Мотоцикли» (слід не зникає, дуель до зіткнення) і «Змійка на двох» (одна змійка,
/// двоє за кермом). Поле й геометрія спільні з дуеллю, тож тут перевіряємо саме те, чим ці режими від неї
/// відрізняються: слід, дельта-кадр, вісь місця й спільний результат пари (TESTING.md §4).
/// </summary>
public class SnakeModesTests
{
    // Стартові координати з SnakeCore.Reset(): жовтий — голова на (3, 6) і дивиться праворуч,
    // зелений — голова на (22, 12) і дивиться ліворуч. Половина сценаріїв нижче рахується від них.
    const int RowA = SnakeCore.H / 2 - 3;
    const int RowB = SnakeCore.H / 2 + 3;

    static RoomHarness Tron()
    {
        var h = new RoomHarness("tron", seed: 42);
        h.Join("Оля");
        h.Join("Петро");
        return h;
    }

    static RoomHarness Coop()
    {
        var h = new RoomHarness("snake-coop", seed: 42);
        h.Join("Оля");
        h.Join("Петро");
        return h;
    }

    /// <summary>Пропустити «готуйсь», щоб дійти до руху.</summary>
    static void ReadyTron(RoomHarness h) => h.Tick(TronGame.StartTicks);

    static void ReadyCoop(RoomHarness h) => h.Tick(CoopSnakeCore.StartTicks);

    static int[] Cells(JsonElement view, string name) =>
        [.. view.GetProperty(name).EnumerateArray().Select(e => e.GetInt32())];

    static string LastLog(RoomHarness h) => h.Outbox.OfType<Journal>().Last().Text;

    // =============================================================================================
    // Мотоцикли: каталог і поле
    // =============================================================================================

    [Fact]
    public void Tron_is_in_the_catalog_as_a_live_game()
    {
        var game = Assert.Single(new Registry().Catalog, g => g.Id == "tron");

        Assert.Equal("live", game.Group);
        Assert.Equal(TronGame.TickMs, game.TickMs);
        Assert.Equal(2, game.MinPlayers);
        Assert.Equal(2, game.MaxPlayers);
        Assert.True(game.Rated);          // рівно двоє + рейтинг = ставка можлива
    }

    [Fact]
    public void Both_modes_are_drawn_by_one_client_module()
    {
        var registry = new Registry();

        // Дві гри в одному файлі законні, але це треба оголосити — інакше браузер піде по 404.
        Assert.Equal("snake-modes", registry.Info("tron")!.Module);
        Assert.Equal("snake-modes", registry.Info("snake-coop")!.Module);
    }

    [Fact]
    public void The_countdown_runs_before_anyone_moves()
    {
        var h = Tron();
        Assert.Equal(TronGame.StartTicks, h.View(0).GetProperty("startIn").GetInt32());
        Assert.Equal(3_000, TronGame.StartTicks * TronGame.TickMs);   // рівно три секунди «готуйсь»

        var before = h.View(0).GetProperty("a").ToString();
        h.Tick(5);

        Assert.Equal(TronGame.StartTicks - 5, h.View(0).GetProperty("startIn").GetInt32());
        Assert.Equal(before, h.View(0).GetProperty("a").ToString());
    }

    [Fact]
    public void The_trail_never_shrinks()
    {
        var h = Tron();
        ReadyTron(h);
        h.Tick(10);

        var v = h.View(null);
        Assert.Equal(13, Cells(v, "a").Length);   // три на старті плюс десять кроків
        Assert.Equal(13, Cells(v, "b").Length);
    }

    [Fact]
    public void There_are_no_apples_on_the_bike_track()
    {
        var h = Tron();

        Assert.False(Views.Has(h.View(null), "apple"));
    }

    [Fact]
    public void The_view_carries_the_whole_state_because_the_frame_does_not()
    {
        var h = Tron();
        var v = h.View(0);

        foreach (var name in new[] { "width", "height", "turn", "a", "b", "dirA", "dirB", "winsA", "winsB", "startIn", "winner" })
            Assert.True(Views.Has(v, name), name);
        Assert.Equal(SnakeCore.W, v.GetProperty("width").GetInt32());
        Assert.Equal(0, v.GetProperty("dirA").GetInt32());
        Assert.Equal(2, v.GetProperty("dirB").GetInt32());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("turn").ValueKind);
    }

    // =============================================================================================
    // Мотоцикли: смерть і рахунок
    // =============================================================================================

    [Fact]
    public void The_wall_ends_the_round_and_the_journal_carries_the_series()
    {
        var h = Tron();
        ReadyTron(h);
        h.Input(1, "turn", new { dir = 1 });   // зелений вниз, до нижньої стіни — п'ять клітинок і все
        h.Tick(6);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("Мотоцикли: Оля жовтий 1:0 Петро зелений", LastLog(h));
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Equal("x", h.View(0).GetProperty("winner").GetString());
        Assert.Equal(1, h.View(0).GetProperty("winsA").GetInt32());
    }

    [Fact]
    public void Riding_into_your_own_trail_loses_the_round()
    {
        var h = Tron();
        ReadyTron(h);
        // зелений робить петлю: вгору, праворуч, вниз — і впирається у власну стартову клітинку
        h.Tick(1);
        h.Input(1, "turn", new { dir = 3 });
        h.Tick(1);
        h.Input(1, "turn", new { dir = 0 });
        h.Tick(1);
        h.Input(1, "turn", new { dir = 1 });
        h.Tick(1);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("x", h.View(0).GetProperty("winner").GetString());
        Assert.Equal([0], h.Room.Result!.Winners);
    }

    [Fact]
    public void Riding_into_the_rivals_trail_loses_the_round()
    {
        var h = Tron();
        ReadyTron(h);
        h.Tick(7);                             // жовтий проїхав сім клітинок праворуч
        h.Input(0, "turn", new { dir = 1 });   // і пірнув униз, назустріч сліду зеленого
        h.Tick(6);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("o", h.View(0).GetProperty("winner").GetString());
        Assert.Equal([1], h.Room.Result!.Winners);
        Assert.Equal(1, h.View(0).GetProperty("winsB").GetInt32());
    }

    [Fact]
    public void Two_bikes_left_alone_hit_the_walls_on_the_same_tick_and_that_is_a_draw()
    {
        var h = Tron();
        ReadyTron(h);
        h.Tick(23);        // обидва їдуть прямо через усе поле і впираються у свої стіни одночасно

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(h.Room.Result!.Draw);
        Assert.Equal("draw", h.View(null).GetProperty("winner").GetString());
        Assert.Contains("врізались одночасно", LastLog(h));
        Assert.Equal(0, h.View(0).GetProperty("winsA").GetInt32());
        Assert.Equal(0, h.View(0).GetProperty("winsB").GetInt32());
    }

    [Fact]
    public void A_head_on_crash_kills_both_riders()
    {
        // Лоб у лоб на повному полі не трапляється (парність відстані між головами не міняється), тому
        // саме правило перевіряємо на ядрі — так само, як це робить дуель.
        var core = new SnakeCore(new Random(1), tailShrinks: false, apples: false);
        core.Reset();
        core.A.Clear();
        core.A.AddRange([SnakeCore.Cell(5, 5), SnakeCore.Cell(4, 5)]);
        core.B.Clear();
        core.B.AddRange([SnakeCore.Cell(7, 5), SnakeCore.Cell(8, 5)]);
        core.DirA = 0;
        core.DirB = 2;

        var (deadA, deadB) = core.Step();

        Assert.True(deadA);
        Assert.True(deadB);
    }

    [Fact]
    public void The_round_ends_long_before_the_minute_long_safety_net()
    {
        var h = Tron();
        ReadyTron(h);
        h.Tick(23);

        var game = (TronGame)h.Room.Game;
        Assert.Equal(23, game.Moves);              // відлік «готуйсь» кроками не рахується
        Assert.True(game.Moves < game.MaxMoves);   // страховка на хвилину так і лишається страховкою
    }

    [Fact]
    public void The_safety_net_calls_a_draw_when_the_round_overstays_its_welcome()
    {
        var h = Tron();
        // Двом слідам на полі в 468 клітинок хвилини не протриматись, тож справжню межу звичайною грою не
        // дістати. Опускаємо її — і перевіряємо саму гілку, а не те, що вона десь там є.
        var game = (TronGame)h.Room.Game;
        game.MaxMoves = 3;
        ReadyTron(h);
        h.Tick(3);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(h.Room.Result!.Draw);
        Assert.Empty(h.Room.Result.Winners);
        Assert.Equal("draw", h.View(null).GetProperty("winner").GetString());
        Assert.Contains("хвилина минула", LastLog(h));
        Assert.Equal(3, game.Moves);
    }

    [Fact]
    public void The_series_survives_a_rematch_and_a_new_pair_resets_it()
    {
        var h = Tron();
        ReadyTron(h);
        h.Input(1, "turn", new { dir = 1 });
        h.Tick(6);
        Assert.Equal(1, h.View(0).GetProperty("winsA").GetInt32());

        h.Rematch("Оля");
        // Місця обернулись — разом з ними поїхав і рахунок: Оля тепер сидить другою.
        Assert.Equal("Оля", h.Room.Seats[1]);
        Assert.Equal(0, h.View(0).GetProperty("winsA").GetInt32());
        Assert.Equal(1, h.View(0).GetProperty("winsB").GetInt32());
        Assert.Equal(TronGame.StartTicks, h.View(0).GetProperty("startIn").GetInt32());
        Assert.Equal(3, Cells(h.View(0), "a").Length);

        h.Leave("Оля");
        Assert.True(h.Rooms.Join(h.RoomId, "Ганна").Reply.Ok);   // дограний стіл відкривається наново
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(0, h.View(0).GetProperty("winsA").GetInt32());
        Assert.Equal(0, h.View(0).GetProperty("winsB").GetInt32());
    }

    [Fact]
    public void Leaving_mid_round_is_a_technical_loss()
    {
        var h = Tron();
        ReadyTron(h);
        h.Tick(3);
        h.Leave("Петро");

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Contains("встав з-за столу", LastLog(h));
        // Раунд скінчився і в самій грі: без цього поле лишилось би яскравим, ніби партія триває.
        Assert.Equal("x", h.View(null).GetProperty("winner").GetString());
    }

    [Fact]
    public void Both_riders_and_a_watcher_see_the_same_track()
    {
        var h = Tron();
        ReadyTron(h);
        h.Tick(4);

        // Ховати в мотоциклах нема чого: обидва сліди й так у всіх на очах (TESTING.md §4.2).
        Assert.Equal(Views.Text(h.View(0)), Views.Text(h.View(1)));
        Assert.Equal(Views.Text(h.View(0)), Views.Text(h.View(null)));
    }

    // =============================================================================================
    // Мотоцикли: дельта-кадр
    // =============================================================================================

    [Fact]
    public void The_frame_is_two_heads_and_nothing_more()
    {
        var h = Tron();
        h.Tick(1);
        var frame = Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);

        Assert.Equal(new[] { "ha", "hb", "startIn", "winner" }, frame.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(SnakeCore.Cell(3, RowA), frame.GetProperty("ha").GetInt32());
        Assert.Equal(SnakeCore.Cell(SnakeCore.W - 4, RowB), frame.GetProperty("hb").GetInt32());
        Assert.Equal(JsonValueKind.Null, frame.GetProperty("winner").ValueKind);
    }

    [Fact]
    public void Heads_from_the_frames_rebuild_exactly_the_trail_of_the_view()
    {
        var h = Tron();
        var start = Cells(h.View(null), "a");     // те, що клієнт отримав подією 'room'
        ReadyTron(h);
        h.Tick(12);

        var client = new TronTrail();
        client.ApplyView(start);
        foreach (var frame in h.Outbox.OfType<RoomFrame>())
            client.AddHead(Views.Json(frame.Frame).GetProperty("ha").GetInt32());

        Assert.Equal(Cells(h.View(null), "a"), client.Cells);
        Assert.Equal(15, client.Cells.Count);
    }

    [Fact]
    public void The_same_view_handed_over_twice_does_not_eat_the_trail()
    {
        var h = Tron();
        var start = Cells(h.View(null), "a");
        ReadyTron(h);
        h.Tick(12);
        var whole = Cells(h.View(null), "a");

        var client = new TronTrail();
        client.ApplyView(start);
        foreach (var frame in h.Outbox.OfType<RoomFrame>())
            client.AddHead(Views.Json(frame.Frame).GetProperty("ha").GetInt32());
        // Посеред раунду вид не приходить (Tick віддає самі кадри), але update() смикається на кожну подію
        // 'rooms' — і приносить ТОЙ САМИЙ, стартовий вид із кешу. Застосувати його вдруге означало б
        // відкотити слід до трьох клітинок, а середину вже ніхто не домалює: кадр несе лише голову.
        client.ApplyView(start);

        Assert.Equal(whole, client.Cells);
    }

    // =============================================================================================
    // Мотоцикли: ввід
    // =============================================================================================

    [Fact]
    public void A_turn_arrives_both_as_an_object_and_as_a_bare_number()
    {
        var down = SnakeCore.Cell(3, RowA + 1);   // поворот застосовується тим самим тиком, не наступним

        var wrapped = Tron();
        ReadyTron(wrapped);
        wrapped.Input(0, "turn", new { dir = 1 });
        wrapped.Tick(1);

        var bare = Tron();
        ReadyTron(bare);
        bare.Input(0, "turn", 1);
        bare.Tick(1);

        Assert.Equal(down, Cells(wrapped.View(0), "a")[0]);
        Assert.Equal(down, Cells(bare.View(0), "a")[0]);
    }

    [Fact]
    public void The_bikes_know_only_the_turn_action()
    {
        var h = Tron();
        ReadyTron(h);
        var before = Views.Text(h.View(0));

        var reply = h.Act(0, "move", new { cell = 1 });

        Assert.False(reply.Ok);
        Assert.Equal("Тут так не ходять", reply.Message);
        Assert.Equal(before, Views.Text(h.View(0)));
    }

    // =============================================================================================
    // Змійка на двох: каталог і осі
    // =============================================================================================

    [Fact]
    public void Coop_is_in_the_catalog_as_a_live_game_without_a_rating()
    {
        var registry = new Registry();
        var game = Assert.Single(registry.Catalog, g => g.Id == "snake-coop");

        Assert.Equal("live", game.Group);
        Assert.Equal(SnakeCore.TickMs, game.TickMs);
        Assert.False(game.Rated);                                             // кооп: змагатись нема з ким
        Assert.Equal(ScoreOrder.HigherIsBetter, registry.Info("snake-coop")!.Score);
    }

    [Fact]
    public void Seat_zero_turns_the_snake_up_and_down()
    {
        var h = Coop();
        ReadyCoop(h);
        h.Input(0, "turn", new { dir = 1 });
        h.Tick(1);

        Assert.Equal(SnakeCore.Cell(SnakeCore.W / 2, SnakeCore.H / 2 + 1), Cells(h.View(0), "s")[0]);
        Assert.Equal(1, h.View(0).GetProperty("dir").GetInt32());
    }

    [Fact]
    public void A_turn_along_the_other_axis_is_refused_with_a_short_human_message()
    {
        var h = Coop();
        ReadyCoop(h);
        var before = Views.Text(h.View(null));

        var mine = h.Act(0, "turn", new { dir = 0 });
        var theirs = h.Act(1, "turn", new { dir = 3 });

        Assert.False(mine.Ok);
        Assert.Equal("Ти крутиш вгору-вниз", mine.Message);
        Assert.False(theirs.Ok);
        Assert.Equal("Ти крутиш вліво-вправо", theirs.Message);
        Assert.Equal(before, Views.Text(h.View(null)));   // відмова стану не міняє
    }

    [Fact]
    public void Seat_one_turns_the_snake_sideways_once_it_goes_vertical()
    {
        var h = Coop();
        ReadyCoop(h);
        h.Input(0, "turn", new { dir = 1 });
        h.Tick(1);
        h.Input(1, "turn", new { dir = 2 });
        h.Tick(1);

        Assert.Equal(SnakeCore.Cell(SnakeCore.W / 2 - 1, SnakeCore.H / 2 + 1), Cells(h.View(0), "s")[0]);
        Assert.Equal(2, h.View(0).GetProperty("dir").GetInt32());
    }

    [Fact]
    public void Two_turns_from_two_seats_apply_one_per_tick_and_do_not_stick_together()
    {
        var h = Coop();
        ReadyCoop(h);
        h.Input(0, "turn", new { dir = 1 });   // обидва натиснули в один тик
        h.Input(1, "turn", new { dir = 2 });

        h.Tick(1);
        Assert.Equal(1, h.View(0).GetProperty("dir").GetInt32());
        h.Tick(1);
        Assert.Equal(2, h.View(0).GetProperty("dir").GetInt32());
    }

    // =============================================================================================
    // Змійка на двох: кінець раунду і результат пари
    // =============================================================================================

    [Fact]
    public void Hitting_the_wall_finishes_the_round_for_both_and_scores_the_length()
    {
        var h = Coop();
        ReadyCoop(h);
        h.Tick(12);                            // до правої стіни рівно дванадцять клітинок
        var len = h.View(null).GetProperty("len").GetInt32();
        h.Tick(1);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        // Переможців у кооперативі нема — і це не формальність: за «перемогу» каркас видає ачівки
        // («Перша перемога», «Серія», «Десять перемог») повз стелю черепків, і пара набивала б їх у грі,
        // де програти неможливо. Довжина від цього не губиться, вона йде окремо, у Scores.
        Assert.Empty(h.Room.Result!.Winners);
        Assert.True(h.Room.Result.Draw);
        Assert.Equal($"Змійка на двох: Оля і Петро виростили змійку до {len}", LastLog(h));
        Assert.Equal("end", h.View(null).GetProperty("winner").GetString());
    }

    [Fact]
    public void Leaving_the_pair_mid_round_ends_it_as_a_technical_loss()
    {
        var h = Coop();
        ReadyCoop(h);
        h.Tick(2);
        h.Leave("Петро");

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Contains("встав з-за столу", LastLog(h));
        Assert.Equal("end", h.View(null).GetProperty("winner").GetString());   // поле треба притемнити
    }

    [Fact]
    public void The_coop_knows_only_the_turn_action()
    {
        var h = Coop();
        ReadyCoop(h);
        var before = Views.Text(h.View(null));

        var reply = h.Act(0, "move", new { cell = 1 });

        Assert.False(reply.Ok);
        Assert.Equal("Тут так не ходять", reply.Message);
        Assert.Equal(before, Views.Text(h.View(null)));
    }

    [Fact]
    public void A_stale_view_does_not_roll_the_coop_snake_back()
    {
        var h = Coop();
        var start = Views.Text(h.View(null));      // вид зі старту раунду, як його закешував каркас
        ReadyCoop(h);
        h.Tick(5);
        var newest = Views.Text(h.Outbox.OfType<RoomFrame>().Last().Frame);

        var client = new CoopField();
        client.ApplyView(start);
        foreach (var frame in h.Outbox.OfType<RoomFrame>()) client.ApplyFrame(Views.Text(frame.Frame));
        client.ApplyView(start);                   // подія 'rooms' принесла той самий вид із кешу

        Assert.NotEqual(start, newest);            // за п'ять тиків змійка справді від'їхала
        Assert.Equal(newest, client.Shown);        // і назад її ніхто не смикнув
    }

    [Fact]
    public void The_length_goes_to_the_pair_table_and_into_the_result_row()
    {
        var h = Coop();
        ReadyCoop(h);
        h.Tick(12);
        var len = h.View(null).GetProperty("len").GetInt32();
        h.Tick(1);

        Assert.Equal(2, h.Scores.Count);
        Assert.All(h.Scores, s =>
        {
            Assert.Equal("snake-coop", s.GameId);
            Assert.Equal(len, (int)s.Score);
            Assert.Equal(ScoreOrder.HigherIsBetter, s.Order);
        });
        Assert.Equal(new[] { "Оля", "Петро" }, h.Scores.Select(s => s.Nick).Order().ToArray());

        var finished = Assert.Single(h.Finished);
        Assert.Equal(len, (int)finished.Result.Scores![0]);
        Assert.Equal(len, (int)finished.Result.Scores![1]);
    }

    [Fact]
    public void A_rematch_starts_a_clean_snake()
    {
        var h = Coop();
        ReadyCoop(h);
        h.Tick(13);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);

        h.Rematch("Оля");
        var v = h.View(0);

        Assert.Equal(3, Cells(v, "s").Length);
        Assert.Equal(3, v.GetProperty("len").GetInt32());
        Assert.Equal(CoopSnakeCore.StartTicks, v.GetProperty("startIn").GetInt32());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("winner").ValueKind);
    }

    [Fact]
    public void The_coop_frame_carries_exactly_what_the_client_draws()
    {
        var h = Coop();
        h.Tick(1);
        var frame = Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);

        Assert.Equal(new[] { "s", "apple", "dir", "startIn", "len", "winner" }, frame.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(3, frame.GetProperty("s").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, frame.GetProperty("winner").ValueKind);
    }

    [Fact]
    public void A_watcher_sees_exactly_what_the_players_see()
    {
        var h = Coop();
        ReadyCoop(h);
        h.Tick(4);

        // Ховати тут нема чого: змійка одна, і глядач має бачити те саме поле, що й обидва гравці.
        Assert.Equal(Views.Text(h.View(0)), Views.Text(h.View(null)));
        Assert.Equal(Views.Text(h.View(0)), Views.Text(h.View(1)));
    }

    [Fact]
    public void The_same_seed_plays_out_the_same_round()
    {
        static string Play(int seed)
        {
            var h = new RoomHarness("snake-coop", seed: seed);
            h.Join("Оля");
            h.Join("Петро");
            ReadyCoop(h);
            for (var i = 0; i < 10; i++)
            {
                if (i == 3) h.Input(0, "turn", new { dir = 1 });
                if (i == 6) h.Input(1, "turn", new { dir = 2 });
                h.Tick(1);
            }
            return Views.Text(h.View(null));
        }

        Assert.Equal(Play(5), Play(5));
    }

    // =============================================================================================
    // Ядро кооперативної змійки
    // =============================================================================================

    [Fact]
    public void Coop_core_starts_with_one_snake_in_the_middle()
    {
        var core = new CoopSnakeCore(new Random(1));
        core.Reset();

        Assert.Equal(3, core.Len);
        Assert.Equal(SnakeCore.Cell(SnakeCore.W / 2, SnakeCore.H / 2), core.S[0]);
        Assert.Equal(0, core.Dir);
        Assert.DoesNotContain(core.Apple, core.S);
    }

    [Fact]
    public void Coop_core_takes_only_its_own_axis_and_queues_at_most_two_turns()
    {
        var core = new CoopSnakeCore(new Random(1));
        core.Reset();

        Assert.False(core.Turn(0, 0));   // горизонталь — не вісь місця 0
        Assert.False(core.Turn(1, 2));   // розворот проти руху
        Assert.False(core.Turn(1, 0));   // туди й так їдемо
        Assert.True(core.Turn(0, 1));
        Assert.True(core.Turn(1, 2));    // після «вниз» лівий поворот уже законний
        Assert.False(core.Turn(0, 3));   // третій у чергу не влізе

        core.Step();
        Assert.Equal(1, core.Dir);
        core.Step();
        Assert.Equal(2, core.Dir);
    }

    [Fact]
    public void Coop_core_grows_on_an_apple_and_puts_a_new_one_on_a_free_cell()
    {
        var core = new CoopSnakeCore(new Random(7));
        core.Reset();
        core.S.Clear();
        core.S.AddRange([SnakeCore.Cell(5, 5), SnakeCore.Cell(4, 5), SnakeCore.Cell(3, 5)]);
        core.Dir = 0;
        core.Apple = SnakeCore.Cell(6, 5);

        Assert.False(core.Step());
        Assert.Equal(4, core.Len);
        Assert.NotEqual(SnakeCore.Cell(6, 5), core.Apple);
        Assert.DoesNotContain(core.Apple, core.S);
    }

    [Fact]
    public void Coop_core_kills_the_snake_on_the_wall_and_on_its_own_body()
    {
        var wall = new CoopSnakeCore(new Random(1));
        wall.Reset();
        var dead = false;
        for (var i = 0; i < 30 && !dead; i++) dead = wall.Step();   // прямо в праву стіну
        Assert.True(dead);

        var loop = new CoopSnakeCore(new Random(1));
        loop.Reset();
        loop.S.Clear();
        loop.S.AddRange([SnakeCore.Cell(5, 5), SnakeCore.Cell(5, 4), SnakeCore.Cell(4, 4), SnakeCore.Cell(4, 5), SnakeCore.Cell(4, 6)]);
        loop.Dir = 2;
        loop.Apple = -1;                 // щоб яблуко випадково не опинилось під головою і не врятувало хвіст
        Assert.True(loop.Step());
    }

    [Fact]
    public void Coop_core_apples_are_deterministic_for_the_same_seed()
    {
        static string Apples(int seed)
        {
            var core = new CoopSnakeCore(new Random(seed));
            core.Reset();
            var list = new List<int> { core.Apple };
            for (var i = 0; i < 5; i++)
            {
                core.PlaceApple();
                list.Add(core.Apple);
            }
            return string.Join(",", list);
        }

        Assert.Equal(Apples(9), Apples(9));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void A_thousand_ticks_of_both_modes_are_instant()
    {
        // Тик кімнати — це не лише крок ядра: до нього додаються кадр і вид, а вид мотоциклів копіює два
        // сліди на сотні клітинок. Тому міряємо через кімнату на двох гравцях (TESTING.md §4.4).
        var tron = Tron();
        var coop = Coop();

        var ticked = 0;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            ticked += Pump(tron, i);
            ticked += Pump(coop, i);
        }
        sw.Stop();

        // Раунди тут коротші за тисячу тиків, тож більшість ітерацій — це справжні тики, а не рематчі.
        // Без цієї перевірки тест міг би тихо виродитись у тисячу невдалих «Ще раз» і нічого не міряти.
        Assert.True(ticked > 1500, $"тиків насправді було {ticked}");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"{ticked} тиків зайняли {sw.Elapsed}");

        // Раунд мотоциклів фізично коротший за тисячу тиків, тож дограний стіл щоразу переставляємо наново.
        static int Pump(RoomHarness h, int i)
        {
            if (h.Room.Status == RoomStatus.Finished) { h.Rematch(); return 0; }
            if (i % 7 == 0) h.Input(i % 2, "turn", new { dir = i / 7 % 4 });
            h.Tick(1);
            lock (h.Room.Sync)
            {
                Views.Json(h.Room.Game.Frame());
                Views.Json(h.Room.Game.View(null));
            }
            return 1;
        }
    }

    /// <summary>
    /// Модель того, що робить із дельта-кадром <c>web/games/snake-modes.js</c>: тримає слід сама, дописує голови
    /// з кадрів і перекладає поле з виду лише тоді, коли вид справді новий. У браузері «новий» — це порівняння
    /// посилань (<c>ctx.view !== st.view</c>): каркас віддає в <c>ctx.view</c> кешований об'єкт останньої події
    /// <c>room</c>, а <c>update()</c> кличеться ще й на кожну <c>rooms</c>.
    /// </summary>
    sealed class TronTrail
    {
        int[]? _view;
        readonly HashSet<int> _seen = [];

        public List<int> Cells { get; } = [];

        public void ApplyView(int[] view)
        {
            if (ReferenceEquals(view, _view)) return;
            _view = view;
            Cells.Clear();
            Cells.AddRange(view);
            _seen.Clear();
            foreach (var cell in view) _seen.Add(cell);
        }

        /// <summary>Голову, яку вже бачили, не дописуємо: слід не зникає, тож двічі в одну клітинку не заїдеш.</summary>
        public void AddHead(int cell)
        {
            if (_seen.Add(cell)) Cells.Insert(0, cell);
        }
    }

    /// <summary>Те саме для коопа: кадр там повний, і застарілий вид не має його перебивати.</summary>
    sealed class CoopField
    {
        string? _view;

        public string? Shown { get; private set; }

        public void ApplyView(string view)
        {
            if (ReferenceEquals(view, _view)) return;
            _view = view;
            Shown = view;
        }

        public void ApplyFrame(string frame) => Shown = frame;
    }
}
