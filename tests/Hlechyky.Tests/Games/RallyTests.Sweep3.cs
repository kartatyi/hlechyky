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
