using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Перестрілка — дуель «кожен проти кожного» на трьох-чотирьох (<see cref="Shootout"/>): цілі, порядок
/// пострілів, фальстарти, раунди до трьох, вихід посеред партії і рематч. Час — від <see cref="FakeClock"/>.
/// </summary>
public class ShootoutTests
{
    const int ResultTicks = Shootout.ResultMs / Duel.TickMs + 1;
    static readonly string[] Nicks = ["Оля", "Петро", "Ігор", "Марта"];

    static RoomHarness Street(int players = 3, int seed = 42)
    {
        var h = new RoomHarness("shootout", seed: seed);
        for (var i = 0; i < players; i++) h.Join(Nicks[i]);
        h.Start();
        return h;
    }

    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;

    static int TickUntil(RoomHarness h, string phase, int max = 500)
    {
        for (var i = 0; i < max; i++)
        {
            if (Phase(h) == phase) return i;
            h.Tick();
        }
        throw new InvalidOperationException($"фаза «{phase}» так і не настала за {max} тиків");
    }

    static int?[] Aims(RoomHarness h) =>
        [.. h.View(null).GetProperty("aim").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Null ? (int?)null : x.GetInt32())];

    static bool[] Alive(RoomHarness h) => [.. h.View(null).GetProperty("alive").EnumerateArray().Select(x => x.GetBoolean())];

    static int[] Wins(RoomHarness h) => [.. h.View(null).GetProperty("wins").EnumerateArray().Select(x => x.GetInt32())];

    static JsonElement Last(RoomHarness h) => h.View(null).GetProperty("last");

    // ---------- паспорт ----------

    [Fact]
    public void Shootout_is_a_live_game_for_three_or_four_that_lives_in_the_duel_module()
    {
        var game = Assert.Single(new Registry().Catalog, g => g.Id == "shootout");
        Assert.Equal(3, game.MinPlayers);
        Assert.Equal(4, game.MaxPlayers);
        Assert.Equal("duel", game.Module);
        Assert.Equal("byHost", game.Start);
        Assert.False(game.Rated);
    }

    [Fact]
    public void Two_can_not_start_a_shootout()
    {
        var h = new RoomHarness("shootout");
        h.Join("Оля");
        h.Join("Петро");
        Assert.False(h.Start().Ok);
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);
    }

    [Fact]
    public void Everyone_starts_aiming_at_the_next_one_round_the_table()
    {
        var h = Street(4);
        Assert.Equal([1, 2, 3, 0], Aims(h));
        Assert.Equal("ready", Phase(h));
        Assert.Equal("шулер", h.Room.SafeSeatName(2));
        Assert.Equal("гробар", h.Room.SafeSeatName(3));
    }

    // ---------- цілі ----------

    [Fact]
    public void Aiming_at_someone_is_seen_by_everyone()
    {
        var h = Street(3);
        h.Input(0, "aim", new { at = 2 });
        h.Tick();
        Assert.Equal(2, Aims(h)[0]);
        var f = Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);
        Assert.Equal(2, f.GetProperty("aim")[0].GetInt32());
    }

    [Fact]
    public void Aiming_at_yourself_or_at_an_empty_seat_is_refused()
    {
        var h = Street(3);
        Assert.False(h.Act(0, "aim", new { at = 0 }).Ok);
        Assert.False(h.Act(0, "aim", new { at = 3 }).Ok);
        Assert.Equal(1, Aims(h)[0]);
    }

    [Fact]
    public void Arrows_step_the_aim_round_the_table_and_skip_yourself()
    {
        var h = Street(3);
        Assert.True(h.Act(0, "aim", new { step = 1 }).Ok);     // 1 → 2
        Assert.Equal(2, Aims(h)[0]);
        Assert.True(h.Act(0, "aim", new { step = 1 }).Ok);     // 2 → (3 порожнє, 0 — я) → 1
        Assert.Equal(1, Aims(h)[0]);
        Assert.True(h.Act(0, "aim", new { step = -1 }).Ok);    // 1 → 2 (назад, через себе)
        Assert.Equal(2, Aims(h)[0]);
    }

    // ---------- постріли ----------

    [Fact]
    public void The_first_shot_after_fire_drops_the_target_and_a_dead_man_does_not_shoot()
    {
        var h = Street(3);
        h.Input(0, "aim", new { at = 1 });
        h.Input(1, "aim", new { at = 0 });
        h.Input(2, "aim", new { at = 0 });
        TickUntil(h, "fire");
        h.Clock.AdvanceMs(230);
        h.Input(0, "shoot");               // Оля перша — Петро падає
        h.Clock.AdvanceMs(20);
        h.Input(1, "shoot");               // Петро вже лежить: його постріл не рахується

        var alive = Alive(h);
        Assert.True(alive[0]);
        Assert.False(alive[1]);
        Assert.True(alive[2]);
        Assert.InRange(h.View(null).GetProperty("shot")[0].GetInt64(), 230, 290);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("shot")[1].ValueKind);
        Assert.Equal(1, h.View(null).GetProperty("hit")[0].GetInt32());
    }

    [Fact]
    public void The_second_shooter_takes_the_first_one_and_the_round()
    {
        var h = Street(3);
        h.Input(0, "aim", new { at = 1 });
        h.Input(2, "aim", new { at = 0 });
        TickUntil(h, "fire");
        h.Input(0, "shoot");               // Оля валить Петра
        h.Clock.AdvanceMs(40);
        h.Input(2, "shoot");               // Ігор валить Олю — і лишається сам
        h.Tick();

        Assert.Equal("result", Phase(h));
        Assert.Equal(2, Last(h).GetProperty("winner").GetInt32());
        Assert.Equal("last", Last(h).GetProperty("reason").GetString());
        Assert.Equal([0, 0, 1, 0], Wins(h));
    }

    [Fact]
    public void A_shot_into_someone_already_down_is_a_miss_and_nobody_takes_the_round()
    {
        var h = Street(3);
        h.Input(0, "aim", new { at = 1 });
        h.Input(2, "aim", new { at = 1 });
        TickUntil(h, "fire");
        h.Input(0, "shoot");               // Петро падає
        h.Input(2, "shoot");               // Ігор теж у Петра — той уже лежить
        h.Tick();

        Assert.Equal("result", Phase(h));
        Assert.Equal("many", Last(h).GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, Last(h).GetProperty("winner").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("hit")[2].ValueKind);
        Assert.Equal([0, 0, 0, 0], Wins(h));
    }

    [Fact]
    public void One_bullet_per_round()
    {
        var h = Street(4);
        h.Input(0, "aim", new { at = 1 });
        TickUntil(h, "fire");
        h.Input(0, "shoot");
        h.Input(0, "aim", new { at = 2 });
        h.Input(0, "shoot");
        Assert.True(Alive(h)[2], "другого патрона нема");
    }

    [Fact]
    public void A_false_start_leaves_you_without_a_bullet_but_still_a_target()
    {
        var h = Street(3);
        h.Input(1, "aim", new { at = 0 });
        h.Input(2, "aim", new { at = 1 });
        h.Input(1, "shoot");                // Петро поспішив
        Assert.True(h.View(null).GetProperty("fs")[1].GetBoolean());
        TickUntil(h, "fire");
        h.Input(1, "shoot");                // патрона нема — Оля живе
        Assert.True(Alive(h)[0]);
        h.Input(2, "shoot");                // а Петра таки підстрелили
        Assert.False(Alive(h)[1]);
    }

    [Fact]
    public void Everyone_rushing_before_fire_ends_the_round_with_nobody_on_top()
    {
        var h = Street(3);
        h.Tick(3);
        h.Input(0, "shoot");
        h.Input(1, "shoot");
        h.Input(2, "shoot");
        h.Tick();
        Assert.Equal("result", Phase(h));
        Assert.Equal("many", Last(h).GetProperty("reason").GetString());
    }

    [Fact]
    public void Nobody_shooting_is_a_sleepy_round_and_three_of_them_close_the_table()
    {
        var h = Street(3);
        for (var r = 0; r < Duel.IdleRounds && h.Room.Status == RoomStatus.Playing; r++)
        {
            TickUntil(h, "result");
            Assert.Equal("sleep", Last(h).GetProperty("reason").GetString());
            Assert.Equal(1, h.View(null).GetProperty("round").GetInt32());   // сонний раунд номера не рухає
            h.Tick(ResultTicks);
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Empty(h.Room.Result!.Winners);
        Assert.Contains("так і не вистрілили", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void The_fire_frame_carries_no_hint_of_when_it_comes()
    {
        var h = Street(3);
        TickUntil(h, "aim");
        var v = h.View(null);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("nextIn").ValueKind);
        Assert.False(Views.Has(v, "fireAt"));
    }

    // ---------- партія ----------

    [Fact]
    public void Three_rounds_win_the_shootout_and_the_journal_has_the_board()
    {
        var h = Street(3);
        for (var i = 0; i < 12 && h.Room.Status == RoomStatus.Playing; i++)
        {
            TickUntil(h, "fire");
            h.Input(0, "aim", new { at = 1 });
            h.Input(0, "shoot");
            h.Input(2, "aim", new { at = 0 });
            h.Clock.AdvanceMs(5);
            h.Input(2, "shoot");          // Ігор валить Олю й лишається сам
            TickUntil(h, "result");
            h.Tick(ResultTicks);
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([2], h.Room.Result!.Winners);
        var log = h.Outbox.OfType<Journal>().Last().Text;
        Assert.StartsWith("Перестрілка: Ігор 3", log);
        Assert.Equal("done", Phase(h));
    }

    [Fact]
    public void Reactions_go_to_the_table_of_quick_hands()
    {
        var h = Street(3);
        h.Input(0, "aim", new { at = 1 });
        TickUntil(h, "fire");
        h.Clock.AdvanceMs(210);
        h.Input(0, "shoot");
        TickUntil(h, "result");
        var s = Assert.Single(h.Scores);
        Assert.Equal("shootout", s.GameId);
        Assert.Equal("Оля", s.Nick);
        Assert.InRange(s.Score, 200, 270);
        Assert.Equal(s.Score, h.View(0).GetProperty("best")[0].GetInt64());
    }

    [Fact]
    public void Leaving_mid_game_keeps_the_rest_shooting_and_turns_their_guns()
    {
        var h = Street(4);
        h.Input(0, "aim", new { at = 3 });
        h.Leave("Марта");

        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.NotEqual(3, Aims(h)[0]);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("aim")[3].ValueKind);
        Assert.False(Alive(h)[3]);
        Assert.Contains("пішов з вулиці", h.Outbox.OfType<Journal>().Last().Text);

        h.Leave("Ігор");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);   // двоє ще достріляються
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
    }

    [Fact]
    public void A_dead_man_can_neither_aim_nor_shoot()
    {
        var h = Street(3);
        h.Input(0, "aim", new { at = 1 });
        TickUntil(h, "fire");
        h.Input(0, "shoot");
        var r = h.Act(1, "shoot");
        Assert.False(r.Ok);
        Assert.Contains("пилюці", r.Message);
    }

    [Fact]
    public void Rematch_rotates_the_seats_and_starts_clean()
    {
        var h = Street(3);
        h.Leave("Ігор");
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);

        var h2 = Street(3, seed: 5);
        for (var i = 0; i < 12 && h2.Room.Status == RoomStatus.Playing; i++)
        {
            TickUntil(h2, "fire");
            h2.Input(1, "aim", new { at = 2 });
            h2.Input(1, "shoot");
            h2.Input(0, "aim", new { at = 1 });
            h2.Clock.AdvanceMs(5);
            h2.Input(0, "shoot");
            TickUntil(h2, "result");
            h2.Tick(ResultTicks);
        }
        Assert.Equal(RoomStatus.Finished, h2.Room.Status);
        Assert.True(h2.Rematch("Оля").Ok);
        Assert.Equal("Петро", h2.Room.Seats[0]);
        Assert.Equal("Оля", h2.Room.Seats[2]);
        Assert.Equal([0, 0, 0, 0], Wins(h2));
        Assert.Equal("ready", Phase(h2));
        Assert.Equal(1, h2.View(null).GetProperty("round").GetInt32());
        Assert.Equal(JsonValueKind.Null, Last(h2).ValueKind);
    }

    [Fact]
    public void The_same_seed_gives_the_same_fire_moment()
    {
        static int Ticks(int seed) => TickUntil(Street(3, seed), "fire");
        Assert.Equal(Ticks(11), Ticks(11));
    }

    [Fact]
    public void Nothing_is_hidden_between_the_players()
    {
        var h = Street(4);
        h.Tick(40);
        var a = h.View(0).ToString();
        Assert.Equal(a, h.View(2).ToString());
        Assert.Equal(a, h.View(null).ToString());
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void A_thousand_ticks_on_four_are_quick()
    {
        var h = Street(4);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000 && h.Room.Status == RoomStatus.Playing; i++)
        {
            h.Tick();
            if (i % 37 == 0) h.Input(i % 4, "shoot");
        }
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"1000 тиків зайняли {sw.Elapsed}");
    }
}
