using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Прохід №3 по родині «Дуель»: обманки (п. 65), різні сигнали (п. 67), рекорди «найшвидшої руки» (п. 64),
/// середня реакція (п. 68), поправка на пінг (п. 70), Турнір стрільців (п. 66), очки в Перестрілці (п. 173).
/// </summary>
public class DuelSweep3Tests
{
    const int ResultTicks = Duel.ResultMs / Duel.TickMs + 1;

    static RoomHarness Duel2(object? options = null, int seed = 42, IServiceProvider? services = null)
    {
        var h = new RoomHarness("duel", options: options, seed: seed, services: services);
        h.Join("Оля");
        h.Join("Петро");
        return h;
    }

    static string Phase(RoomHarness h) => h.View(0).GetProperty("phase").GetString()!;

    static int TickUntil(RoomHarness h, string phase, int max = 600)
    {
        for (var i = 0; i < max; i++)
        {
            if (Phase(h) == phase) return i;
            h.Tick();
        }
        throw new InvalidOperationException($"фаза «{phase}» так і не настала");
    }

    static JsonElement Last(RoomHarness h) => h.View(0).GetProperty("last");

    // ---------- обманки ----------

    [Fact]
    public void Without_options_the_duel_has_no_decoys_signals_or_ping_fields()
    {
        var h = Duel2();
        for (var i = 0; i < 200; i++)
        {
            h.Tick();
            var v = h.View(0);
            Assert.False(Views.Has(v, "decoy"));
            Assert.False(Views.Has(v, "sig"));
            Assert.False(Views.Has(v, "ping"));
        }
    }

    /// <summary>Сід, на якому в першому раунді є обманка.</summary>
    static RoomHarness WithBait(out string word)
    {
        for (var seed = 1; seed < 200; seed++)
        {
            var h = Duel2(new { bait = "on" }, seed);
            TickUntil(h, "aim");
            for (var i = 0; i < 120 && Phase(h) == "aim"; i++)
            {
                h.Tick();
                var v = h.View(0);
                if (Views.Has(v, "decoy") && v.GetProperty("phase").GetString() == "aim")
                {
                    word = v.GetProperty("decoy").GetProperty("w").GetString()!;
                    return h;
                }
            }
        }
        throw new InvalidOperationException("жодної обманки за 200 сідів");
    }

    [Fact]
    public void A_decoy_is_shouted_during_aim_and_shooting_at_it_is_a_false_start_with_the_word()
    {
        var h = WithBait(out var word);
        Assert.Contains(word, DuelBout.Baits);
        h.Input(1, "shoot");
        h.Tick();
        var l = Last(h);
        Assert.Equal("false", l.GetProperty("reason").GetString());
        Assert.Equal(0, l.GetProperty("winner").GetInt32());
        Assert.Equal(word, l.GetProperty("bait").GetString());
    }

    [Fact]
    public void The_decoy_comes_at_least_seven_tenths_before_fire()
    {
        var h = WithBait(out _);
        var ticks = TickUntil(h, "fire");
        Assert.True(ticks * Duel.TickMs >= DuelBout.BaitBeforeFireMs - Duel.TickMs, $"{ticks} тиків");
    }

    // ---------- сигнали ----------

    [Fact]
    public void Mixed_signals_pick_all_four_kinds_over_rounds_and_the_view_says_which()
    {
        var seen = new HashSet<string>();
        for (var seed = 1; seed <= 40; seed++)
        {
            var h = Duel2(new { signal = "mix" }, seed);
            seen.Add(h.View(0).GetProperty("sig").GetString()!);
        }
        Assert.Equal(DuelBout.Signals.Order(), seen.Order());
    }

    // ---------- рекорди й середня реакція ----------

    static (RoomHarness h, DuelRecords rec) WithRecords(Func<string, DateTimeOffset, int, IReadOnlyList<(string, double)>>? seed = null)
    {
        var clock = new FakeClock();
        var rec = new DuelRecords(seed ?? ((_, _, _) => []), clock, inline: true);
        return (Duel2(services: RoomHarness.WithService(rec)), rec);
    }

    static void Shoot(RoomHarness h, int seat, int afterMs)
    {
        TickUntil(h, "fire");
        h.Clock.AdvanceMs(afterMs);
        h.Input(seat, "shoot");
        TickUntil(h, "result");
    }

    [Fact]
    public void Records_first_shot_is_quiet_then_a_better_one_is_a_personal_record_and_beating_the_week_is_announced()
    {
        var clock = new FakeClock();
        var rec = new DuelRecords((_, _, _) => [], clock, inline: true);
        var now = clock.UtcNow;
        Assert.Null(rec.Post("Оля", 300, now));                                        // тихий тиждень: перший — не рекорд
        Assert.Equal("week", rec.Post("Петро", 250, now));                            // побив тижневий
        Assert.Null(rec.Post("Оля", 320, now));                                        // гірше за своє
        Assert.Equal("pb", rec.Post("Оля", 280, now));                                 // своє побив, тижневий — ні
        Assert.Equal("Петро", rec.Week(now)!.Nick);
        // через тиждень рекорд тижня зникає, особистий — лишається
        Assert.Null(rec.Week(now.AddDays(8)));
        Assert.Equal(280, rec.Personal("оля"));
    }

    [Fact]
    public void Records_are_seeded_from_old_results()
    {
        var clock = new FakeClock();
        var rec = new DuelRecords((g, since, n) => g == "duel" ? [("Оля", 190.0)] : [], clock, inline: true);
        Assert.Equal(190, rec.Personal("Оля"));
        Assert.Equal(190, rec.Week(clock.UtcNow)!.Ms);
        Assert.Equal("week", rec.Post("Оля", 185, clock.UtcNow));                      // свій же тижневий теж рекорд тижня
    }

    [Fact]
    public void A_new_week_record_goes_to_the_journal_and_the_round_marks_it()
    {
        var (h, _) = WithRecords((_, _, _) => [("Марта", 400.0)]);
        Shoot(h, 0, 200);
        var l = Last(h);
        Assert.Equal("week", l.GetProperty("nr")[0].GetString());
        Assert.Contains(h.Outbox.OfType<Journal>(), j => j.Text.Contains("Найшвидша рука тижня — Оля: 200 мс"));
        var rec = h.View(0).GetProperty("rec");
        Assert.Equal("Оля", rec.GetProperty("week").GetProperty("n").GetString());
        Assert.Equal(200, rec.GetProperty("pb")[0].GetInt64());
    }

    [Fact]
    public void Inhuman_milliseconds_do_not_make_records()
    {
        var (h, rec) = WithRecords((_, _, _) => [("Марта", 400.0)]);
        Shoot(h, 0, 20);
        Assert.False(Views.Has(Last(h), "nr"));
        Assert.Null(rec.Personal("Оля"));
    }

    [Fact]
    public void The_average_reaction_of_each_is_in_the_view()
    {
        var h = Duel2();
        Shoot(h, 0, 200);
        h.Tick(ResultTicks);
        Shoot(h, 0, 300);
        var avg = h.View(0).GetProperty("avg");
        Assert.Equal(250, avg[0].GetInt64());
        Assert.Equal(JsonValueKind.Null, avg[1].ValueKind);
    }

    // ---------- пінг ----------

    /// <summary>Нагодувати пінг: кадри-заміри в «Готуйсь…», місце seat відлунює кожен через rtt мс.</summary>
    static void Pong(RoomHarness h, int seat, int rtt)
    {
        var fid = h.View(0).GetProperty("fid").GetInt64();
        h.Clock.AdvanceMs(rtt);
        h.Input(seat, "pong", new { f = fid });
        h.Clock.AdvanceMs(-rtt);
    }

    static RoomHarness Pinged(int rtt0, int rtt1)
    {
        var h = Duel2(new { ping = "on" });
        long seen = 0;
        for (int i = 0, got = 0; i < 200 && got < 4; i++)
        {
            h.Tick();
            var fid = h.View(0).GetProperty("fid").GetInt64();
            if (fid == seen) continue;
            // відлунюємо щойно надісланий кадр, як це зробив би браузер
            seen = fid;
            got++;
            Pong(h, 0, rtt0);
            Pong(h, 1, rtt1);
        }
        return h;
    }

    [Fact]
    public void Ping_is_measured_by_the_server_and_capped()
    {
        var h = Pinged(20, 300);
        var p = h.View(0).GetProperty("ping");
        Assert.Equal(20, p[0].GetInt32());
        Assert.Equal(PingMeter.CapMs, p[1].GetInt32());
    }

    [Fact]
    public void With_ping_on_a_later_shot_with_the_worse_ping_can_still_win()
    {
        var h = Pinged(10, 70);
        TickUntil(h, "fire");
        h.Clock.AdvanceMs(200);
        h.Input(0, "shoot");                // 200 − 10 = 190
        h.Clock.AdvanceMs(40);
        h.Input(1, "shoot");                // 240 − 70 = 170
        TickUntil(h, "result");
        var l = Last(h);
        Assert.Equal(1, l.GetProperty("winner").GetInt32());
        Assert.Equal(190, l.GetProperty("ms")[0].GetInt64());
        Assert.Equal(170, l.GetProperty("ms")[1].GetInt64());
        Assert.Equal(70, l.GetProperty("pc")[1].GetInt64());
    }

    [Fact]
    public void With_ping_on_the_first_shot_wins_once_the_other_cannot_catch_up()
    {
        var h = Pinged(10, 70);
        TickUntil(h, "fire");
        h.Clock.AdvanceMs(200);
        h.Input(0, "shoot");                // 190: суперник мав би прилетіти раніше за 190 + 70 = 260 мс
        h.Tick();
        Assert.Equal("fire", Phase(h));     // 250 мс — ще чекаємо
        h.Tick();
        Assert.Equal("result", Phase(h));
        Assert.Equal(0, Last(h).GetProperty("winner").GetInt32());
    }

    [Fact]
    public void Without_enough_samples_or_with_a_made_up_frame_there_is_no_compensation()
    {
        var h = Duel2(new { ping = "on" });
        h.Input(0, "pong", new { f = 999 });     // такого кадру не було
        Pong(h, 1, 50);                           // один замір — замало
        var p = h.View(0).GetProperty("ping");
        Assert.Equal(0, p[0].GetInt32());
        Assert.Equal(0, p[1].GetInt32());
    }

    [Fact]
    public void Ping_frames_never_fly_during_aim()
    {
        var h = Duel2(new { ping = "on" });
        TickUntil(h, "aim");
        h.Outbox.Clear();
        var n = 0;
        while (Phase(h) == "aim") { h.Tick(); n++; }
        // у «Цілься…» кадрів нема взагалі; перший — уже «ВОГОНЬ!»
        Assert.Single(h.Outbox.OfType<RoomFrame>());
        Assert.True(n > 1);
    }

    // ---------- турнір ----------

    static readonly string[] Nicks = ["Оля", "Петро", "Ігор", "Марта", "Тарас", "Леся", "Богдан", "Ніна"];

    static RoomHarness Cup(int players, int seed = 42, object? options = null)
    {
        var h = new RoomHarness("duelcup", options: options, seed: seed);
        for (var i = 0; i < players; i++) h.Join(Nicks[i]);
        h.Start();
        return h;
    }

    static int[] Pair(RoomHarness h)
    {
        var p = h.View(null).GetProperty("pair");
        return p.ValueKind == JsonValueKind.Null ? [] : [p[0].GetInt32(), p[1].GetInt32()];
    }

    /// <summary>Дограти турнір: у кожному раунді стріляє ліва сторона пари. Повертає, скільки матчів зіграно.</summary>
    static int PlayCup(RoomHarness h)
    {
        var matches = 0;
        var pair = Pair(h);
        for (var guard = 0; guard < 100 && h.Finished.Count == 0; guard++)
        {
            TickUntil(h, "fire");
            var now = Pair(h);
            if (!now.SequenceEqual(pair)) { matches++; pair = now; }
            h.Clock.AdvanceMs(210);
            h.Input(now[0], "shoot");
            TickUntil(h, "result");
            h.Tick(ResultTicks);
        }
        return matches + 1;
    }

    [Theory]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(5, 4)]
    [InlineData(8, 7)]
    public void The_cup_plays_every_real_match_one_by_one_and_names_the_sheriff_of_the_evening(int players, int matches)
    {
        var h = Cup(players);
        Assert.Equal(matches, PlayCup(h));
        Assert.Single(h.Finished);
        Assert.Single(h.Finished[0].Result.Winners);
        Assert.Contains("шериф вечора", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void Only_the_pair_shoots_the_rest_watch()
    {
        var h = Cup(4);
        var pair = Pair(h);
        var watcher = Enumerable.Range(0, 4).First(s => !pair.Contains(s));
        Assert.False(h.Act(watcher, "shoot").Ok);
        Assert.True(h.Act(pair[0], "shoot").Ok);
    }

    [Fact]
    public void Leaving_mid_match_hands_it_to_the_opponent()
    {
        var h = Cup(4);
        var pair = Pair(h);
        h.Leave(h.NickOf(pair[0]));
        h.Tick();
        var cup = h.View(null).GetProperty("cup")[0];
        var done = cup.EnumerateArray().First(m => m[2].ValueKind != JsonValueKind.Null);
        Assert.Equal(pair[1], done[2].GetInt32());
        Assert.Equal(1, done[5].GetInt32());
        Assert.False(Pair(h).Contains(pair[0]));
    }

    [Fact]
    public void The_cup_draw_is_the_same_for_the_same_seed()
    {
        static string Draw(int seed) => h(seed).View(null).GetProperty("cup").GetRawText();
        static RoomHarness h(int seed) => Cup(6, seed);
        Assert.Equal(Draw(7), Draw(7));
        Assert.NotEqual(Draw(7), Draw(8));
    }

    // ---------- Перестрілка: очки ----------

    static RoomHarness Shoot3(int seed = 42)
    {
        var h = new RoomHarness("shootout", seed: seed);
        h.Join("Оля");
        h.Join("Петро");
        h.Join("Ігор");
        h.Start();
        return h;
    }

    [Fact]
    public void Shootout_by_default_counts_points_a_hit_and_the_fastest_hit()
    {
        var h = Shoot3();
        Assert.Equal(Shootout.PointsNeeded, h.View(null).GetProperty("target").GetInt32());
        // 0 цілиться в 1, 2 — в 0 (стартові цілі: наступний по колу, 2 → 0)
        TickUntil(h, "fire");
        h.Clock.AdvanceMs(150);
        h.Input(2, "shoot");        // 2 влучає в 0, найшвидший
        h.Clock.AdvanceMs(50);
        h.Input(1, "shoot");        // 1 цілиться в 2 — влучає
        TickUntil(h, "result");
        var v = h.View(null);
        Assert.Equal("points", v.GetProperty("last").GetProperty("reason").GetString());
        Assert.Equal([0, 1, 2, 0], [.. v.GetProperty("wins").EnumerateArray().Select(x => x.GetInt32())]);
        Assert.Equal(2, v.GetProperty("last").GetProperty("fast").GetInt32());
    }

    [Fact]
    public void Shootout_points_game_ends_at_seven_with_a_single_leader()
    {
        var h = Shoot3();
        for (var r = 0; r < 20 && h.Finished.Count == 0; r++)
        {
            TickUntil(h, "fire");
            h.Clock.AdvanceMs(150);
            h.Input(2, "shoot");     // Ігор щоразу найшвидший і влучає: +2 за раунд
            TickUntil(h, "result");
            h.Tick(Shootout.ResultMs / Duel.TickMs + 1);
        }
        Assert.Single(h.Finished);
        Assert.Equal([2], h.Finished[0].Result.Winners);
        Assert.Contains("Ігор 8", h.Outbox.OfType<Journal>().Last().Text);
    }
}
