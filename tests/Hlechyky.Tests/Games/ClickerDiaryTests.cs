using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Щоденник гончаря (аналітика кола, етап 1): один знімок на київський день — новіший заміняє старий (і відновлений),
/// години додаються й діляться за київською годиною, гості й боти — не пишуться; коло пише години лише за відрізки між
/// діями до хвилини, а знімок — раз на кілька хвилин, на новий день і коли коло зникає з очей.
/// </summary>
public class ClickerDiaryTests
{
    static DateTimeOffset Local(int y, int m, int d, int h, int min, int s = 0) =>
        new(TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, m, d, h, min, s, DateTimeKind.Unspecified), Days.Kyiv), TimeSpan.Zero);

    static ClickerDiaryService Svc(Db? db = null, ClickerDiaryOptions? o = null) =>
        new(db, () => o ?? new ClickerDiaryOptions(), new FakeClock(), person: nick => !nick.StartsWith("гість", StringComparison.Ordinal));

    static void WithDb(Action<Db> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "clk-diary-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        try { body(new Db(Path.Combine(dir, "t.db"))); }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void One_snapshot_per_day_newest_wins_and_replaces_a_restored_row()
    {
        WithDb(db =>
        {
            db.Exec(ClickerDiaryService.Schema);
            db.Exec("INSERT INTO clicker_diary(day, nick_key, nick, at, json, restored) VALUES('2026-10-24', 'назар', 'Назар', '2026-10-24T20:00:00Z', '{\"old\":1}', 1)");
            var svc = Svc(db);
            Assert.True(Assert.Single(svc.Diary("Назар")).Restored);

            svc.Put("Назар", "2026-10-24", Local(2026, 10, 24, 22, 0), "{\"n\":1}");
            svc.Put("Назар", "2026-10-24", Local(2026, 10, 24, 23, 0), "{\"n\":2}");
            svc.Put("Назар", "2026-10-25", Local(2026, 10, 25, 9, 0), "{\"n\":3}");
            // Ще не записане теж видно.
            Assert.Equal(["{\"n\":2}", "{\"n\":3}"], svc.Diary("назар").Select(r => r.Json));
            svc.FlushNow();
            var rows = Svc(db).Diary("Назар");
            Assert.Equal(["2026-10-24", "2026-10-25"], rows.Select(r => r.Day));
            Assert.Equal("{\"n\":2}", rows[0].Json);
            Assert.All(rows, r => Assert.False(r.Restored));
        });
    }

    [Fact]
    public void Hours_add_up_across_flushes_split_by_kyiv_hour_and_keep_fractions()
    {
        WithDb(db =>
        {
            var svc = Svc(db);
            svc.AddHour("Smaug", Local(2026, 10, 25, 23, 59, 50), 30.6, formed: 2, grabbed: 1, clicks: 40);
            svc.AddHour("Smaug", Local(2026, 10, 26, 0, 0, 20), 30, formed: 1, grabbed: 0, clicks: 10);
            svc.FlushNow();
            svc.AddHour("Smaug", Local(2026, 10, 25, 23, 59, 59), 9.6, formed: 0, grabbed: 0, clicks: 3);
            svc.FlushNow();
            var hours = Svc(db).Hours("smaug");
            Assert.Equal([("2026-10-25", 23), ("2026-10-26", 0)], hours.Select(h => (h.Day, h.Hour)));
            // 30,6 + 9,6 = 40,2: ціла частина в базі, дріб чекає наступного запису.
            Assert.Equal((40, 2L, 1L, 43L), (hours[0].Seconds, hours[0].Formed, hours[0].Grabbed, hours[0].Clicks));
            Assert.Equal((30, 1L, 0L, 10L), (hours[1].Seconds, hours[1].Formed, hours[1].Grabbed, hours[1].Clicks));
        });
    }

    [Fact]
    public void Guests_and_switched_off_diary_write_nothing()
    {
        var svc = Svc();
        svc.Put("гість Оля", "2026-10-25", Local(2026, 10, 25, 12, 0), "{}");
        svc.AddHour("гість Оля", Local(2026, 10, 25, 12, 0), 10, 1, 1, 1);
        Assert.Empty(svc.Diary("гість Оля"));
        Assert.Empty(svc.Hours("гість Оля"));

        var off = Svc(o: new ClickerDiaryOptions { Enabled = false });
        off.Put("Назар", "2026-10-25", Local(2026, 10, 25, 12, 0), "{}");
        Assert.Empty(off.Diary("Назар"));
    }

    // ---------- коло з щоденником ----------

    static (RoomHarness H, ClickerDiaryService Svc) Wheel(ClickerDiaryOptions? o = null)
    {
        var svc = Svc(o: o);
        var h = new RoomHarness("clicker", services: RoomHarness.WithService(svc));
        h.Clock.UtcNow = Local(2026, 10, 25, 20, 0);
        h.Solo("Назар");
        return (h, svc);
    }

    static ActResult Spin(RoomHarness h, int n = 5) => h.Act(0, "spin", PotterHands.Human(n));

    [Fact]
    public void The_wheel_counts_only_the_seconds_between_actions_a_minute_apart_or_closer()
    {
        var (h, svc) = Wheel();
        Assert.True(Spin(h).Ok);                    // перша дія — лише опора
        h.Clock.Advance(20);
        Assert.True(Spin(h).Ok);
        h.Clock.Advance(30);
        Assert.True(Spin(h).Ok);
        h.Clock.Advance(300);                       // п'ять хвилин тиші — це вже не гра
        Assert.True(Spin(h).Ok);
        var hour = Assert.Single(svc.Hours("Назар"));
        Assert.Equal(("2026-10-25", 20, 50), (hour.Day, hour.Hour, hour.Seconds));
        Assert.Equal(10, hour.Clicks);              // кліки двох зарахованих відрізків, по 5
    }

    [Fact]
    public void A_snapshot_lands_on_the_first_action_then_every_few_minutes_and_when_the_wheel_hides()
    {
        var (h, svc) = Wheel(new ClickerDiaryOptions { SnapshotMinutes = 5 });
        Assert.True(Spin(h).Ok);
        var first = Assert.Single(svc.Diary("Назар"));
        Assert.Equal("2026-10-25", first.Day);
        h.Clock.Advance(60);
        Assert.True(Spin(h).Ok);
        Assert.Equal(first.At, Assert.Single(svc.Diary("Назар")).At);          // хвилина — ще той самий знімок
        h.Clock.Advance(10);
        Assert.True(h.Act(0, "sight", new { on = false }).Ok);                 // пішов з кола — знімок одразу
        var left = Assert.Single(svc.Diary("Назар"));
        Assert.Equal(h.Clock.UtcNow, left.At);

        using var doc = JsonDocument.Parse(left.Json);
        var j = doc.RootElement;
        Assert.Equal(Clicker.DiaryVersion, j.GetProperty("v").GetInt32());
        Assert.True(j.GetProperty("total").GetDouble() > 0);
        Assert.Equal(10, j.GetProperty("dayClicks").GetInt64());
        foreach (var k in new[] { "all", "stamps", "styles", "house", "album", "fair", "guests", "toloka", "passivePerk" })
            Assert.True(j.GetProperty("mult").GetProperty(k).GetDouble() >= 1, k);
        Assert.True(j.TryGetProperty("levels", out _));
        Assert.True(j.TryGetProperty("toloka", out _));
    }

    [Fact]
    public void A_new_kyiv_day_starts_a_new_row()
    {
        var (h, svc) = Wheel();
        h.Clock.UtcNow = Local(2026, 10, 25, 23, 59, 40);
        Assert.True(Spin(h).Ok);
        h.Clock.Advance(30);                         // 00:00:10 — уже 26.10
        Assert.True(Spin(h).Ok);
        Assert.Equal(["2026-10-25", "2026-10-26"], svc.Diary("Назар").Select(r => r.Day));
        Assert.Equal([("2026-10-26", 0)], svc.Hours("Назар").Select(x => (x.Day, x.Hour)));
    }
}
