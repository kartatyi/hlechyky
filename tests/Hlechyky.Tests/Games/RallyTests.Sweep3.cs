using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Прохід №3 (29.09): вибір траси між гонками, гараж, фотофініш, Дід Панас, живі перешкоди, нові траси, чемпіонат.</summary>
public partial class RallyTests
{
    // ---------- №85 вибір траси між гонками ----------

    [Fact]
    public void A_track_picked_after_the_race_restarts_the_countdown_there_and_sticks_for_the_next_rematch()
    {
        var h = Table(2);
        Assert.Equal("selo", Game(h).Track.Id);
        Green(h);
        Assert.False(h.Act(0, "track", new { track = "ozero" }).Ok);           // посеред гонки — ні
        FinishAll(h, 2);
        Assert.True(h.Rematch().Ok);
        h.Tick(3);
        Assert.False(h.Act(0, "track", new { track = "mars" }).Ok);
        Assert.True(h.Act(1, "track", new { track = "ozero" }).Ok);
        Assert.Equal("ozero", Game(h).Track.Id);
        Assert.Equal(0, Core(h).T);                                           // відлік — наново, машини на решітці озера
        Assert.Equal(RallyTracks.Get("ozero").SlotX[0], Core(h).Cars[0].X);
        Assert.Equal("ozero", h.View(null).GetProperty("track").GetProperty("id").GetString());
        Assert.False(h.Act(0, "track", new { track = "nich" }).Ok);            // вдруге за ту саму гонку — ні
        Green(h);
        FinishAll(h, 2);
        Assert.True(h.Rematch().Ok);
        Assert.Equal("ozero", Game(h).Track.Id);                              // звичайне «Ще раз» — туди ж
        h.Tick(RallyCore.CountTicks - 10);
        Assert.False(h.Act(0, "track", new { track = "nich" }).Ok);            // світлофор уже майже зелений — пізно
    }

    // ---------- №88 фотофініш ----------

    [Fact]
    public void A_finish_closer_than_three_tenths_puts_a_photo_finish_into_the_view()
    {
        var h = Table(3);
        Green(h);
        var core = Core(h);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("photo").ValueKind);
        // перший і другий — на 0,28 с, третій далеко: фотофініш — пара 1–2
        ReadyToFinish(core, 0, 2);
        ReadyToFinish(core, 1, 2, x: 18.6);
        for (var i = 0; i < 60 && core.Cars[1].Fin == 0; i++)
        {
            Ctl(h, 0, 4);
            Ctl(h, 1, 4);
            h.Tick();
        }
        Assert.Equal(1, core.Cars[0].Fin);
        Assert.Equal(2, core.Cars[1].Fin);
        var gap = core.Cars[1].FinishMs - core.Cars[0].FinishMs;
        var photo = h.View(null).GetProperty("photo");
        if (gap < Rally.PhotoMs)
        {
            Assert.Equal(0, photo[0].GetInt32());
            Assert.Equal(1, photo[1].GetInt32());
            Assert.Equal(gap, photo[2].GetInt32());
        }
        else Assert.Equal(JsonValueKind.Null, photo.ValueKind);
        Assert.True(gap < Rally.PhotoMs, $"розрив {gap} мс — підсунь другого ближче");
    }

    [Fact]
    public void A_clear_win_has_no_photo_finish()
    {
        var h = Table(2);
        Green(h);
        var core = Core(h);
        ReadyToFinish(core, 0, 2);
        ReadyToFinish(core, 1, 2, x: 12);
        for (var i = 0; i < 200 && h.Room.Status == RoomStatus.Playing; i++)
        {
            Ctl(h, 0, 4);
            Ctl(h, 1, 4);
            h.Tick();
        }
        Assert.True(core.Cars[1].FinishMs - core.Cars[0].FinishMs >= Rally.PhotoMs);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("photo").ValueKind);
    }

    // ---------- №87 Дід Панас ----------

    [Fact]
    public void Grandpa_bots_fill_free_seats_up_to_four_drive_a_race_and_take_no_rewards_or_records()
    {
        var laps = new RallyLaps(new MemoryGameStore(), a => a());
        var h = Table(1, new { track = "selo", laps = "3", bots = "3" }, services: RoomHarness.WithService(laps));
        var game = Game(h);
        var core = Core(h);
        Assert.Equal(3, Enumerable.Range(0, 6).Count(game.IsBot));
        Assert.Equal(4, core.Cars.Count(c => c.Present));
        var v = h.View(null);
        Assert.Equal("🤖 Дід Панас", v.GetProperty("bots")[1].GetString());
        Assert.Equal("traktor", v.GetProperty("cars")[1].GetString());
        // людину теж веде автопілот (тихіший за аса) — гонка доїжджає до кінця
        Green(h);
        Pilot(h, new RallyPilot(game.Track) { Cap = 700 }, [0], () => h.Room.Status != RoomStatus.Playing);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(core.Cars[1].Fin > 0 || core.Cars[2].Fin > 0 || core.Cars[3].Fin > 0, "хтось із ботів мав би доїхати");
        Assert.True(core.Cars[0].Fin > 0, "людина мала б доїхати");
        // рекордів боти не пишуть, людина одна — нагород за «перемогу» нема
        Assert.DoesNotContain(laps.Top("selo", 20), r => r.Nick.Contains("🤖"));
        Assert.Empty(h.Room.Result!.Winners);
        Assert.Contains("🤖", LastJournal(h));
    }

    [Fact]
    public void Four_people_leave_no_room_for_bots_and_without_the_option_nobody_joins()
    {
        var h = Table(4, new { track = "selo", laps = "3", bots = "1" });
        Assert.Equal(4, Core(h).Cars.Count(c => c.Present));
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("bots").ValueKind);
        var plain = Table(1);
        Assert.Equal(1, Core(plain).Cars.Count(c => c.Present));
    }

    [Fact]
    public void Bots_driving_do_not_keep_an_idle_race_alive_and_leaving_leaves_no_bot_only_race()
    {
        var h = Table(2, new { track = "selo", laps = "7", bots = "2" });
        Green(h);
        h.Leave(h.NickOf(1));
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        h.Leave(h.NickOf(0));
        Assert.Contains("роз'їхались", LastJournal(h));
    }

    [Fact]
    public void Pilot_levels_differ_in_pace()
    {
        int Lapped(string level)
        {
            var h = Table(1, new { track = "yarmarok", laps = "7", bots = level });
            Green(h);
            Ctl(h, 0, 0);
            h.Tick(1400);
            var core = Core(h);
            return core.Passed(core.Cars[1]);
        }
        var slow = Lapped("1");
        var fast = Lapped("3");
        output.WriteLine($"за 56 с: тихо — {slow} воріт, ас — {fast}");
        Assert.True(fast > slow, $"ас {fast} ≤ тихо {slow}");
    }

    // ---------- №89 живі перешкоди ----------

    [Fact]
    public void A_critter_walks_there_waits_walks_back_and_waits()
    {
        var d = RallyTrack.Critter(0, 10, 1, 10, 6, 50, 150);
        // зерно 9973 → зсув 0: на тику 0 курка в A
        RallyCore.CritterAt(d, 9973, 0, out var x, out var y);
        Assert.Equal((d[1], d[2]), (x, y));
        RallyCore.CritterAt(d, 9973, 25, out x, out y);
        Assert.Equal(d[2] + (d[4] - d[2]) / 2, y);
        RallyCore.CritterAt(d, 9973, 100, out x, out y);
        Assert.Equal(d[4], y);
        RallyCore.CritterAt(d, 9973, 225, out x, out y);
        Assert.Equal(d[4] + (d[2] - d[4]) / 2, y);
        RallyCore.CritterAt(d, 9973, 300, out x, out y);
        Assert.Equal(d[2], y);
        RallyCore.CritterAt(d, 9973, 400, out x, out y);                          // період — 400 тиків
        Assert.Equal(d[2], y);
    }

    [Fact]
    public void A_car_hits_a_chicken_slows_down_and_gets_the_event_but_flies_over_it_from_a_ramp()
    {
        var track = new RallyTrack("arena-live", "Арена", [.. Arena().Map], Arena().Gates, [.. Arena().Slots],
            Critters: [RallyTrack.Critter(0, 20, 10, 20, 10, 50, 150)]);   // курка стоїть на місці
        var core = Bare(track);
        core.Live = 9973;
        var c = core.Put(0, At(16.5), At(10.5), 0, 800);
        c.Mask = 4;
        var ev = 0;
        for (var i = 0; i < 20; i++) { core.Tick(); ev |= c.Ev; }
        Assert.True((ev & RallyCore.EvCritter) != 0);
        Assert.True(c.VF < 700, $"швидкість {c.VF}");
        // без живності та сама машина їде крізь те місце
        var bare = Bare(track);
        var b = bare.Put(0, At(16.5), At(10.5), 0, 800);
        b.Mask = 4;
        for (var i = 0; i < 20; i++) bare.Tick();
        Assert.True(b.X > c.X);
        // у повітрі — над куркою
        var fly = Bare(track);
        fly.Live = 9973;
        var f = fly.Put(0, At(18.5), At(10.5), 0, 800);
        f.Air = 14;
        fly.Tick();
        fly.Tick();
        Assert.Equal(0, f.Ev & RallyCore.EvCritter);
    }

    [Fact]
    public void Live_option_seeds_critters_on_tracks_that_have_them_and_the_journal_really_meets_them()
    {
        Assert.Equal(0, Core(Table(1)).Live);                                   // типово — як було
        Assert.NotEqual(0, Core(Table(1, new { track = "selo", laps = "3", live = "1" })).Live);
        Assert.Equal(0, Core(Table(1, new { track = "ozero", laps = "3", live = "1" })).Live);   // на озері живності нема
        var h = Table(1, new { track = "yarmarok", laps = "3", live = "1" });
        Assert.Equal(2, h.View(null).GetProperty("track").GetProperty("critters").GetArrayLength());
        var j = RallyReplays.Load(Path.Combine(RallyReplays.Dir(), "4-selo-live.json"));
        var withLive = RallyReplays.Play(j);
        j.Live = 0;
        Assert.NotEqual(withLive, RallyReplays.Play(j));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Tick_with_bots_and_critters_stays_cheap_and_the_frame_small()
    {
        var h = Table(1, new { track = "selo", laps = "7", bots = "3", live = "1" });
        Green(h);
        var game = Game(h);
        var core = Core(h);
        var pilot = new RallyPilot(game.Track);
        long maxBytes = 0;
        var sw = new Stopwatch();
        var n = 0;
        for (var i = 0; i < 3000 && h.Room.Status == RoomStatus.Playing; i++)
        {
            Ctl(h, 0, pilot.Mask(core, 0));
            h.Clock.Advance(TimeSpan.FromMilliseconds(RallyCore.TickMs));
            sw.Start();
            game.Tick();
            sw.Stop();
            n++;
            if (i % 50 == 0) maxBytes = Math.Max(maxBytes, Views.WireBytes(game.Frame()!));
        }
        var us = sw.Elapsed.TotalMilliseconds * 1000 / n;
        output.WriteLine($"Tick з трьома ботами й живністю: {us:F1} мкс, кадр до {maxBytes} Б, {n} тиків");
        Assert.True(us < 250, $"{us} мкс");
        Assert.True(maxBytes <= 1536, $"{maxBytes} Б");
    }

    // ---------- №91 гараж ----------

    [Fact]
    public void Garage_paint_and_plate_ride_with_the_nick_and_are_checked()
    {
        var h = Table(2, start: false);
        Assert.True(h.Act(0, "garage", new { paint = 3, plate = " влад7 " }).Ok);
        Assert.False(h.Act(1, "garage", new { paint = 12, plate = "" }).Ok);
        Assert.False(h.Act(1, "garage", new { paint = 1, plate = "ДУЖЕДОВГИЙ" }).Ok);
        Assert.False(h.Act(1, "garage", new { paint = 1, plate = "<b>" }).Ok);
        var v = h.View(null);
        Assert.Equal(3, v.GetProperty("paint")[0].GetInt32());
        Assert.Equal("ВЛАД7", v.GetProperty("plates")[0].GetString());
        Assert.Equal(-1, v.GetProperty("paint")[1].GetInt32());
        h.Start();
        Green(h);
        Assert.False(h.Act(0, "garage", new { paint = 1, plate = "" }).Ok);   // посеред гонки — ні
        FinishAll(h, 2);
        Assert.False(h.Act(1, "garage", new { paint = 0, plate = "ПЕТРО" }).Ok);  // підсумок каркас до гри не пускає
        Assert.True(h.Rematch().Ok);                                           // місця обернулись — фарба за ніком
        Assert.True(h.Act(h.NickOf(0) == "Петро" ? 0 : 1, "garage", new { paint = 0, plate = "ПЕТРО" }).Ok);   // на відліку — можна
        var seatOla = h.NickOf(0) == "Оля" ? 0 : 1;
        v = h.View(null);
        Assert.Equal(3, v.GetProperty("paint")[seatOla].GetInt32());
        Assert.Equal("ПЕТРО", v.GetProperty("plates")[1 - seatOla].GetString());
    }
}
