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
