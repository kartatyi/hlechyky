using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Дуель-вестерн: фази за годинником, фальстарти, реакція в мілісекундах і серія до трьох перемог
/// (docs/games/specs/duel.md, TESTING.md §4). Час скрізь від <see cref="FakeClock"/> — жодного Sleep.
/// </summary>
public class DuelTests
{
    /// <summary>Скільки тиків триває кожна стала пауза.</summary>
    const int ReadyTicks = Duel.ReadyMs / Duel.TickMs;            // 30
    const int FireTicks = Duel.FireWindowMs / Duel.TickMs;        // 60
    const int ResultTicks = Duel.ResultMs / Duel.TickMs + 1;      // 51, з запасом на межу

    static RoomHarness Street(int seed = 42)
    {
        var h = new RoomHarness("duel", seed: seed);
        h.Join("Оля");
        h.Join("Петро");
        return h;
    }

    static string Phase(RoomHarness h) => h.View(0).GetProperty("phase").GetString()!;

    /// <summary>Тикати, доки не настане потрібна фаза. Повертає, скільки тиків на це пішло.</summary>
    static int TickUntil(RoomHarness h, string phase, int max = 400)
    {
        for (var i = 0; i < max; i++)
        {
            if (Phase(h) == phase) return i;
            h.Tick();
        }
        throw new InvalidOperationException($"фаза «{phase}» так і не настала за {max} тиків");
    }

    /// <summary>Дочекатись «ВОГОНЬ!», вистрілити з місця seat і дограти паузу до наступного раунду.</summary>
    static void WinRound(RoomHarness h, int seat)
    {
        TickUntil(h, "fire");
        h.Input(seat, "shoot");
        TickUntil(h, "result");
        h.Tick(ResultTicks);
    }

    static JsonElement Last(RoomHarness h) => h.View(0).GetProperty("last");

    static int[] Wins(RoomHarness h) => [.. h.View(0).GetProperty("wins").EnumerateArray().Select(x => x.GetInt32())];

    // ---------- фази ----------

    [Fact]
    public void Getting_ready_lasts_a_second_and_a_half_and_then_it_is_time_to_aim()
    {
        var h = Street();
        Assert.Equal("ready", Phase(h));

        h.Tick(ReadyTicks - 1);
        Assert.Equal("ready", Phase(h));
        h.Tick();
        Assert.Equal("aim", Phase(h));
    }

    [Fact]
    public void Aiming_never_takes_less_than_a_second_and_a_half_nor_more_than_five()
    {
        // Міряємо не хелпер, а справжню машину фаз: скільки тиків від початку партії до «ВОГОНЬ!».
        var lengths = new HashSet<int>();
        for (var seed = 1; seed <= 50; seed++)
        {
            var ticks = TickUntil(Street(seed), "fire");
            // 1.5 с «Готуйсь…» плюс 1.5–5 с «Цілься…», округлені вгору до сітки тиків
            Assert.InRange(ticks, (Duel.ReadyMs + Duel.AimMinMs) / Duel.TickMs, (Duel.ReadyMs + Duel.AimMaxMs) / Duel.TickMs);
            lengths.Add(ticks);
        }
        // сід кімнати справді щось міняє, інакше «випадковість» була б сталою
        Assert.True(lengths.Count > 10, $"на 50 сідах вийшло лише {lengths.Count} різних тривалостей");
    }

    [Fact]
    public void While_aiming_the_frame_gives_no_countdown_at_all()
    {
        var h = Street();
        h.Tick(ReadyTicks);
        Assert.Equal("aim", Phase(h));

        // будь-яке число тут — це той самий fireAt, лише з іншого боку
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("nextIn").ValueKind);
        // а в «Готуйсь…» відлік є: пауза там стала й нікого не видає
        Assert.True(h.View(0).GetProperty("round").GetInt32() > 0);
    }

    // ---------- фальстарти ----------

    [Fact]
    public void A_shot_while_aiming_is_a_bullet_in_the_sky_and_the_round_goes_to_the_rival()
    {
        var h = Street();
        h.Tick(ReadyTicks);
        Assert.Equal("aim", Phase(h));

        h.Input(1, "shoot");
        h.Tick();

        Assert.Equal("result", Phase(h));
        Assert.Equal("false", Last(h).GetProperty("reason").GetString());
        Assert.Equal(0, Last(h).GetProperty("winner").GetInt32());
        Assert.Equal([1, 0], Wins(h));
    }

    [Fact]
    public void Rushing_already_at_the_ready_counts_as_a_false_start_too()
    {
        var h = Street();
        h.Input(0, "shoot");
        h.Tick();

        Assert.Equal("result", Phase(h));
        Assert.Equal("false", Last(h).GetProperty("reason").GetString());
        Assert.Equal(1, Last(h).GetProperty("winner").GetInt32());
    }

    [Fact]
    public void When_both_rush_before_the_round_is_announced_nobody_takes_it()
    {
        var h = Street();
        h.Tick(ReadyTicks);
        h.Input(0, "shoot");
        h.Input(1, "shoot");          // обидва в одному тику — раунд нікому
        h.Tick();

        Assert.Equal("both-false", Last(h).GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, Last(h).GetProperty("winner").ValueKind);
        Assert.Equal([0, 0], Wins(h));
    }

    [Fact]
    public void A_replayed_round_keeps_its_number()
    {
        var h = Street();
        WinRound(h, 0);
        Assert.Equal(2, h.View(0).GetProperty("round").GetInt32());

        h.Input(0, "shoot");          // фальстарт обох у другому раунді
        h.Input(1, "shoot");
        h.Tick();
        h.Tick(ResultTicks);

        Assert.Equal("ready", Phase(h));
        Assert.Equal(2, h.View(0).GetProperty("round").GetInt32());
    }

    // ---------- постріл ----------

    [Fact]
    public void The_first_shot_after_fire_takes_the_round_and_the_reaction_is_measured()
    {
        var h = Street();
        TickUntil(h, "fire");

        h.Clock.AdvanceMs(120);
        h.Input(0, "shoot");
        h.Tick();

        Assert.Equal("result", Phase(h));
        Assert.Equal("shot", Last(h).GetProperty("reason").GetString());
        Assert.Equal(0, Last(h).GetProperty("winner").GetInt32());
        Assert.Equal(120, Last(h).GetProperty("ms")[0].GetInt64());
        Assert.Equal(JsonValueKind.Null, Last(h).GetProperty("ms")[1].ValueKind);
    }

    [Fact]
    public void The_second_shot_does_not_take_the_round_but_its_time_is_still_shown()
    {
        var h = Street();
        TickUntil(h, "fire");

        h.Clock.AdvanceMs(120);
        h.Input(0, "shoot");
        h.Clock.AdvanceMs(80);
        h.Input(1, "shoot");          // спізнився на 80 мс
        h.Input(1, "shoot");          // і вдруге тиснути марно
        h.Tick();

        Assert.Equal(0, Last(h).GetProperty("winner").GetInt32());
        Assert.Equal(120, Last(h).GetProperty("ms")[0].GetInt64());
        Assert.Equal(200, Last(h).GetProperty("ms")[1].GetInt64());
    }

    [Fact]
    public void Nobody_shooting_for_three_seconds_puts_both_to_sleep_and_replays_the_round()
    {
        var h = Street();
        TickUntil(h, "fire");
        h.Tick(FireTicks);

        Assert.Equal("result", Phase(h));
        Assert.Equal("sleep", Last(h).GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, Last(h).GetProperty("winner").ValueKind);
        Assert.Equal([0, 0], Wins(h));

        h.Tick(ResultTicks);
        Assert.Equal("ready", Phase(h));
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void A_shot_that_missed_the_window_does_not_take_the_round()
    {
        var h = Street();
        TickUntil(h, "fire");

        // вікно вже зачинилось, а тик, який оголосить «заснули», ще не настав
        h.Clock.AdvanceMs(Duel.FireWindowMs + 10);
        h.Input(0, "shoot");
        h.Tick();

        Assert.Equal("sleep", Last(h).GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, Last(h).GetProperty("ms")[0].ValueKind);
        Assert.Equal([0, 0], Wins(h));
        Assert.Empty(h.Scores);
    }

    [Fact]
    public void Three_sleepy_rounds_in_a_row_close_the_empty_street()
    {
        var h = Street();
        for (var i = 0; i < Duel.IdleRounds; i++)
        {
            TickUntil(h, "fire");
            h.Tick(FireTicks);            // за столом нікого
            Assert.Equal("sleep", Last(h).GetProperty("reason").GetString());
            h.Tick(ResultTicks);
        }

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("done", Phase(h));
        Assert.Empty(h.Room.Result!.Winners);      // нічия: ставки повертаються
        Assert.Equal("Дуель: Оля і Петро так і не вистрілили — дуель не відбулась",
            h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void One_shot_wakes_the_street_up_and_the_sleepy_count_starts_over()
    {
        var h = Street();
        for (var i = 0; i < Duel.IdleRounds - 1; i++)
        {
            TickUntil(h, "fire");
            h.Tick(FireTicks);
            h.Tick(ResultTicks);
        }

        WinRound(h, 0);                   // хтось таки прокинувся

        for (var i = 0; i < Duel.IdleRounds - 1; i++)
        {
            TickUntil(h, "fire");
            h.Tick(FireTicks);
            h.Tick(ResultTicks);
        }
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void A_shot_between_the_rounds_is_politely_refused()
    {
        var h = Street();
        TickUntil(h, "fire");
        h.Input(0, "shoot");
        TickUntil(h, "result");

        var refused = h.Act(1, "shoot");
        Assert.False(refused.Ok);
        Assert.Equal("Раунд уже скінчився, чекай наступного", refused.Message);
        Assert.Equal(0, Last(h).GetProperty("winner").GetInt32());   // стан не зрушив
    }

    [Fact]
    public void An_unknown_action_is_refused_and_changes_nothing()
    {
        var h = Street();
        var before = h.View(0).ToString();

        var refused = h.Act(0, "dance");
        Assert.False(refused.Ok);
        Assert.Equal("Тут так не ходять", refused.Message);
        Assert.Equal(before, h.View(0).ToString());
    }

    // ---------- партія ----------

    [Fact]
    public void Three_won_rounds_finish_the_duel_and_the_journal_carries_the_score()
    {
        var h = Street();
        WinRound(h, 0);
        WinRound(h, 1);
        WinRound(h, 0);
        WinRound(h, 0);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("done", Phase(h));
        Assert.Equal("Дуель: Оля шериф 3:1 Петро бандит", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Single(h.Finished);
    }

    [Fact]
    public void The_score_table_gets_the_reaction_of_everyone_who_pulled_the_trigger()
    {
        var h = Street();
        TickUntil(h, "fire");
        h.Clock.AdvanceMs(150);
        h.Input(0, "shoot");
        h.Clock.AdvanceMs(90);
        h.Input(1, "shoot");
        h.Tick();

        Assert.Equal(2, h.Scores.Count);
        Assert.Equal(150, h.Scores.Single(s => s.Nick == "Оля").Score);
        Assert.Equal(240, h.Scores.Single(s => s.Nick == "Петро").Score);
        Assert.All(h.Scores, s => Assert.Equal(ScoreOrder.LowerIsBetter, s.Order));
        Assert.All(h.Scores, s => Assert.Equal("duel", s.GameId));
    }

    [Fact]
    public void A_false_start_and_a_sleepy_round_leave_the_table_of_reactions_alone()
    {
        var h = Street();
        h.Tick(ReadyTicks);
        h.Input(1, "shoot");              // фальстарт: стріляли, але не в ту мить
        h.Tick();
        Assert.Equal("false", Last(h).GetProperty("reason").GetString());
        Assert.Empty(h.Scores);

        h.Tick(ResultTicks);
        TickUntil(h, "fire");
        h.Tick(FireTicks);                // і заснули обидва
        Assert.Equal("sleep", Last(h).GetProperty("reason").GetString());
        Assert.Empty(h.Scores);
    }

    [Fact]
    public void A_reaction_no_human_has_takes_the_round_but_not_a_place_in_the_table()
    {
        var h = Street();
        TickUntil(h, "fire");

        h.Clock.AdvanceMs(Duel.HumanFloorMs - 1);
        h.Input(0, "shoot");
        h.Tick();

        Assert.Equal(0, Last(h).GetProperty("winner").GetInt32());
        Assert.Equal([1, 0], Wins(h));                                  // раунд усе одно його
        Assert.Equal(Duel.HumanFloorMs - 1, Last(h).GetProperty("ms")[0].GetInt64());
        Assert.Empty(h.Scores);                                         // а таблиця й ачівка — ні
    }

    [Fact]
    public void The_view_remembers_the_best_reaction_of_each_hand()
    {
        var h = Street();
        TickUntil(h, "fire");
        h.Clock.AdvanceMs(300);
        h.Input(0, "shoot");
        h.Tick();
        h.Tick(ResultTicks);

        TickUntil(h, "fire");
        h.Clock.AdvanceMs(180);
        h.Input(0, "shoot");
        h.Tick();

        var best = h.View(0).GetProperty("best");
        Assert.Equal(180, best[0].GetInt64());               // лишився кращий, а не останній
        Assert.Equal(JsonValueKind.Null, best[1].ValueKind);
    }

    [Fact]
    public void Rematch_starts_a_brand_new_duel_from_zero()
    {
        var h = Street();
        WinRound(h, 0);
        WinRound(h, 0);
        WinRound(h, 0);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);

        h.Rematch("Оля");
        Assert.Equal("Оля", h.Room.Seats[1]);                // місця обернулись
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("ready", Phase(h));
        Assert.Equal(1, h.View(0).GetProperty("round").GetInt32());
        Assert.Equal([0, 0], Wins(h));
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("last").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("best")[0].ValueKind);
    }

    [Fact]
    public void Walking_away_in_the_middle_is_a_technical_loss()
    {
        var h = Street();
        WinRound(h, 1);
        h.Leave("Оля");

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Room.Result!.Winners);
        Assert.Contains("Оля", h.Outbox.OfType<Journal>().Last().Text);
    }

    // ---------- дріт ----------

    [Fact]
    public void The_frame_carries_exactly_what_the_scene_draws_and_never_the_moment_of_fire()
    {
        var h = Street();
        h.Tick(ReadyTicks);
        var frame = Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);

        foreach (var name in new[] { "phase", "round", "wins", "last", "nextIn" })
            Assert.True(Views.Has(frame, name), name);
        Assert.False(Views.Has(frame, "fireAt"));
        Assert.False(Views.Has(frame, "best"));               // рекорди — справа виду, не кадру
        Assert.Equal("aim", frame.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, frame.GetProperty("nextIn").ValueKind);
        Assert.DoesNotContain("fireAt", Views.Text(h.View(0)));
    }

    [Fact]
    public void The_view_is_the_frame_plus_the_records()
    {
        var h = Street();
        var v = h.View(0);

        foreach (var name in new[] { "phase", "round", "wins", "last", "nextIn", "best" })
            Assert.True(Views.Has(v, name), name);
        Assert.False(Views.Has(v, "turn"));                  // дуель не покрокова, ходити нікому
        Assert.Equal(2, v.GetProperty("wins").GetArrayLength());
        Assert.Equal(2, v.GetProperty("best").GetArrayLength());
        // обидва місця і глядач бачать те саме: ховати в дуелі нема чого
        Assert.Equal(Views.Text(h.View(0)), Views.Text(h.View(1)));
        Assert.Equal(Views.Text(h.View(0)), Views.Text(h.View(null)));
    }

    [Fact]
    public void Frames_fly_only_when_the_phase_changes()
    {
        var h = Street();
        h.Tick(ReadyTicks - 1);
        Assert.Empty(h.Outbox.OfType<RoomFrame>());   // «Готуйсь…» триває — розповідати нема чого

        h.Tick();
        Assert.Single(h.Outbox.OfType<RoomFrame>());
        h.Tick(5);
        Assert.Single(h.Outbox.OfType<RoomFrame>());
    }

    [Fact]
    public void The_duel_is_in_the_catalog_as_a_live_game_with_a_lower_is_better_table()
    {
        var registry = new Registry();
        var game = Assert.Single(registry.Catalog, g => g.Id == "duel");

        Assert.Equal("live", game.Group);
        Assert.Equal(Duel.TickMs, game.TickMs);
        Assert.True(game.Rated);
        Assert.Equal(2, game.MaxPlayers);
        Assert.Equal("duel", game.Module);
        Assert.Equal(ScoreOrder.LowerIsBetter, registry.Info("duel")!.Score);
        Assert.Equal("шериф", registry.Create("duel")!.SeatName(0));
        Assert.Equal("бандит", registry.Create("duel")!.SeatName(1));
    }

    [Fact]
    public void The_same_seed_plays_the_very_same_duel()
    {
        static string Play(int seed)
        {
            var h = Street(seed);
            for (var i = 0; i < 200; i++)
            {
                if (Phase(h) == "fire") h.Input(i % 2, "shoot");
                h.Tick();
            }
            return Views.Text(h.View(0));
        }
        Assert.Equal(Play(7), Play(7));
        Assert.NotEqual("", Play(7));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void A_thousand_ticks_of_one_street_are_instant()
    {
        var h = Street();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            // обидва щоразу поспішають: раунд переграється, партія не кінчається — тик має справжню роботу
            if (Phase(h) is "ready" or "aim") { h.Input(0, "shoot"); h.Input(1, "shoot"); }
            h.Tick();
        }
        sw.Stop();

        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"1000 тиків зайняли {sw.Elapsed}");
    }
}
