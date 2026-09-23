using Hlechyky.Games;
using Hlechyky.Tests.Support;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace Hlechyky.Tests.Platform;

public class CurfewTests
{
    // 24 вересня 00:30 за Києвом (UTC+3 влітку)
    static readonly DateTimeOffset Night = new(2026, 9, 23, 21, 30, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Midnight = new(2026, 9, 23, 21, 0, 0, TimeSpan.Zero);

    static (Curfew Curfew, FakeClock Clock) Make(DateTimeOffset now, string text = "", string games = "clicker")
    {
        var clock = new FakeClock { UtcNow = now };
        var options = new FixedOptions<CurfewOptions>(new CurfewOptions { Nicks = ["владік", "микола ( справжній )"], Games = games, Text = text });
        return (new Curfew(options, new EphemeralDataProtectionProvider(), clock), clock);
    }

    [Fact]
    public void Night_is_from_kyiv_midnight_to_six()
    {
        Assert.Equal(Midnight, Curfew.NightStart(Night, 0, 6));
        Assert.Equal(Midnight, Curfew.NightStart(new DateTimeOffset(2026, 9, 24, 2, 59, 0, TimeSpan.Zero), 0, 6));   // 05:59
        Assert.Null(Curfew.NightStart(new DateTimeOffset(2026, 9, 24, 3, 0, 0, TimeSpan.Zero), 0, 6));               // 06:00
        Assert.Null(Curfew.NightStart(new DateTimeOffset(2026, 9, 23, 20, 59, 0, TimeSpan.Zero), 0, 6));             // 23:59
        // Узимку Київ — UTC+2, і північ зсувається разом із ним
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 22, 0, 0, TimeSpan.Zero),
            Curfew.NightStart(new DateTimeOffset(2026, 12, 1, 22, 30, 0, TimeSpan.Zero), 0, 6));
    }

    [Fact]
    public void Night_can_start_before_midnight()
    {
        var start = new DateTimeOffset(2026, 9, 23, 20, 0, 0, TimeSpan.Zero);   // 23:00 за Києвом
        Assert.Equal(start, Curfew.NightStart(new DateTimeOffset(2026, 9, 23, 20, 30, 0, TimeSpan.Zero), 23, 6));
        Assert.Equal(start, Curfew.NightStart(new DateTimeOffset(2026, 9, 24, 1, 0, 0, TimeSpan.Zero), 23, 6));
        Assert.Null(Curfew.NightStart(new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero), 23, 6));
        Assert.Null(Curfew.NightStart(Night, 6, 6));
    }

    [Fact]
    public void Only_listed_players_are_sent_to_sleep_and_only_at_night()
    {
        var (curfew, clock) = Make(Night);
        Assert.Contains("спати", curfew.Refusal("Владік", null, "clicker"));
        Assert.StartsWith("🌙", curfew.Refusal("микола ( справжній )", null, "clicker"));
        Assert.Null(curfew.Refusal("Назар", null, "clicker"));
        Assert.Null(curfew.Refusal("микола", null, "clicker"));
        Assert.Null(curfew.Refusal("гість владік", null, "clicker"));
        clock.UtcNow = new DateTimeOffset(2026, 9, 24, 3, 1, 0, TimeSpan.Zero);   // 06:01
        Assert.Null(curfew.Refusal("владік", null, "clicker"));
    }

    [Fact]
    public void Only_the_potters_wheel_is_closed()
    {
        var (curfew, _) = Make(Night);
        Assert.Contains("Гончарне коло", curfew.Text);
        Assert.NotNull(curfew.Refusal("владік", null, "clicker"));
        foreach (var other in new[] { "chess", "mafia", "wordle", "mines-daily", "", null })
            Assert.Null(curfew.Refusal("владік", null, other));

        var (more, _) = Make(Night, games: " clicker , mafia ");
        Assert.NotNull(more.Refusal("владік", null, "mafia"));
        Assert.Null(more.Refusal("владік", null, "chess"));
    }

    [Fact]
    public void Moves_are_refused_only_on_the_wheel()
    {
        var (curfew, _) = Make(Night);
        var wheel = new RoomHarness("clicker");
        Assert.True(wheel.Solo("владік").Ok);
        Assert.NotNull(curfew.MoveRefusal("владік", null, wheel.Room));
        Assert.Null(curfew.MoveRefusal("Назар", null, wheel.Room));

        var ttt = new RoomHarness("ttt");
        ttt.Join("владік");
        ttt.Join("микола ( справжній )");
        Assert.Null(curfew.MoveRefusal("владік", null, ttt.Room));
        Assert.Null(curfew.MoveRefusal("владік", null, null));
    }

    [Fact]
    public void Text_from_settings_is_what_they_see()
    {
        var (curfew, _) = Make(Night, "Пора спати! Лише для Владіка й Миколи.");
        Assert.Equal("🌙 Пора спати! Лише для Владіка й Миколи.", curfew.Refusal("владік", null, "clicker"));
    }

    [Fact]
    public void Game_started_before_night_with_someone_else_is_finished()
    {
        var (curfew, _) = Make(Night);
        var before = Midnight.AddMinutes(-10);
        Assert.True(curfew.Finishing(RoomStatus.Playing, false, before, ["владік", "Назар"], Midnight));
        Assert.False(curfew.Finishing(RoomStatus.Playing, false, before, ["владік", "Микола ( справжній )", null], Midnight));
        Assert.False(curfew.Finishing(RoomStatus.Playing, true, before, ["владік"], Midnight));
        Assert.False(curfew.Finishing(RoomStatus.Playing, false, Midnight.AddMinutes(5), ["владік", "Назар"], Midnight));
        Assert.False(curfew.Finishing(RoomStatus.Lobby, false, null, ["владік", "Назар"], Midnight));
    }

    [Fact]
    public void Browser_of_a_listed_player_keeps_the_curfew_for_guests_only()
    {
        var (curfew, _) = Make(Night);
        var mine = new DefaultHttpContext();
        mine.Items["account"] = new Account("владік", "", "salt", "member");
        mine.Items["nick"] = "владік";
        Assert.NotNull(curfew.ForMe(mine));
        var set = mine.Response.Headers.SetCookie.ToString();
        Assert.StartsWith(Curfew.Cookie + "=", set);
        var cookie = set.Split(';')[0];

        // той самий браузер, вийшов з акаунта — гість теж спить
        var guest = new DefaultHttpContext();
        guest.Request.Headers.Cookie = cookie;
        guest.Items["nick"] = "гість Влад";
        Assert.NotNull(curfew.Refusal("гість Влад", guest, "clicker"));
        Assert.NotNull(curfew.ForMe(guest));

        // інший акаунт у тому ж браузері — вже інша людина
        Assert.Null(curfew.Refusal("Назар", guest, "clicker"));

        // а без позначки гостя ніхто не чіпає, і про чужий відбій він нічого не знає
        var stranger = new DefaultHttpContext();
        stranger.Items["nick"] = "гість Влад";
        Assert.Null(curfew.Refusal("гість Влад", stranger, "clicker"));
        Assert.Null(curfew.ForMe(stranger));
        Assert.Equal("", stranger.Response.Headers.SetCookie.ToString());
    }
}
