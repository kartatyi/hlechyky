using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Шаховий годинник (шахи й шашки) і рахунок серії «Ще раз» — спільні шматки, які нічний прохід 24.09 додав
/// обом дошкам. Годинник пасивний: прапорець перевіряє кожна дія, а браузер шле flag, коли в когось скінчився час.
/// </summary>
public class BoardClockTests
{
    static RoomHarness Chess(string clock = "3", string variant = "classic")
    {
        var h = new RoomHarness("chess", options: new { variant, clock });
        h.Join("Оля");
        h.Join("Петро");
        return h;
    }

    static RoomHarness Checkers(string clock = "3")
    {
        var h = new RoomHarness("checkers", options: new { clock });
        h.Join("Оля");
        h.Join("Петро");
        return h;
    }

    static ActResult ChessMove(RoomHarness h, int seat, string from, string to) => h.Act(seat, "move", new { from, to });
    static ActResult CkMove(RoomHarness h, int seat, params string[] path) => h.Act(seat, "move", new { path });

    static long[] Ms(RoomHarness h) => [.. h.View(0).GetProperty("clock").GetProperty("ms").EnumerateArray().Select(e => e.GetInt64())];

    static string Reason(RoomHarness h) => h.View(0).GetProperty("result").GetProperty("reason").GetString()!;

    // ---------- годинник: шахи ----------

    [Fact]
    public void Without_the_option_there_is_no_clock_at_all()
    {
        var h = new RoomHarness("chess");
        h.Join("Оля");
        h.Join("Петро");
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("clock").ValueKind);
        h.Clock.Advance(TimeSpan.FromHours(3));
        Assert.True(ChessMove(h, 0, "e2", "e4").Ok);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void The_clock_waits_for_the_first_move_and_then_counts_with_the_increment()
    {
        var h = Chess("3");
        Assert.Equal([180_000L, 180_000L], Ms(h));
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("clock").GetProperty("running").ValueKind);

        h.Clock.Advance(TimeSpan.FromSeconds(50));          // білі думали над першим ходом — це безкоштовно
        ChessMove(h, 0, "e2", "e4");
        Assert.Equal([180_000L, 180_000L], Ms(h));
        Assert.Equal(1, h.View(0).GetProperty("clock").GetProperty("running").GetInt32());

        h.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal([180_000L, 170_000L], Ms(h));           // час чорних іде просто у виді
        ChessMove(h, 1, "e7", "e5");
        Assert.Equal([180_000L, 172_000L], Ms(h));           // −10 с думання, +2 с надбавки
        Assert.Equal(0, h.View(0).GetProperty("clock").GetProperty("running").GetInt32());
    }

    [Fact]
    public void Flag_before_the_time_is_up_is_refused_and_changes_nothing()
    {
        var h = Chess("3");
        ChessMove(h, 0, "e2", "e4");
        h.Clock.Advance(TimeSpan.FromSeconds(179));
        Assert.Equal("Час ще є", h.Act(0, "flag").Message);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void A_fallen_flag_loses_the_game_when_the_opponent_claims_it()
    {
        var h = Chess("3");
        ChessMove(h, 0, "e2", "e4");
        h.Clock.Advance(TimeSpan.FromSeconds(181));

        var r = h.Act(0, "flag");
        Assert.True(r.Ok);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Equal("time", Reason(h));
        Assert.Contains("скінчився час", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Equal(0, h.View(0).GetProperty("clock").GetProperty("ms")[1].GetInt64());
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("clock").GetProperty("running").ValueKind);
    }

    [Fact]
    public void Moving_after_your_own_flag_fell_is_too_late()
    {
        var h = Chess("3");
        ChessMove(h, 0, "e2", "e4");
        h.Clock.Advance(TimeSpan.FromMinutes(4));

        Assert.Equal("Твій час вийшов", ChessMove(h, 1, "e7", "e5").Message);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Equal('p', h.View(0).GetProperty("board").GetString()![12]);   // e7 на місці: хід не пройшов
    }

    [Fact]
    public void Flag_against_a_bare_king_is_a_draw()
    {
        var h = Chess("3");
        // Білі: король і ферзь, чорні — голий король. Упаде прапорець білих — матувати чорним нічим.
        var json = JsonSerializer.Serialize(new
        {
            variant = "Classic", fen = "4k3/8/8/8/8/8/8/4K2Q w - - 0 1", moves = Array.Empty<string>(),
            lostWhite = "", lostBlack = "", seen = new Dictionary<string, int>(), lastFrom = -1, lastTo = -1,
            drawOffer = (int?)null, winner = (int?)null, reason = (string?)null,
        });
        lock (h.Room.Sync) h.Room.Game.Load(json);
        ChessMove(h, 0, "h1", "h2");
        ChessMove(h, 1, "e8", "d8");
        h.Clock.Advance(TimeSpan.FromMinutes(5));

        Assert.True(h.Act(1, "flag").Ok);
        Assert.True(h.Room.Result!.Draw);
        Assert.Equal("time-material", Reason(h));
    }

    [Fact]
    public void Rematch_resets_the_clock()
    {
        var h = Chess("5");
        ChessMove(h, 0, "e2", "e4");
        h.Clock.Advance(TimeSpan.FromMinutes(6));
        h.Act(0, "flag");
        Assert.True(h.Rematch().Ok);
        Assert.Equal([300_000L, 300_000L], Ms(h));
    }

    // ---------- годинник: шашки ----------

    [Fact]
    public void Checkers_clock_counts_and_flags_too()
    {
        var h = Checkers("3");
        Assert.Equal([180_000L, 180_000L], Ms(h));
        CkMove(h, 0, "c3", "d4");
        h.Clock.Advance(TimeSpan.FromSeconds(20));
        CkMove(h, 1, "f6", "e5");
        Assert.Equal([180_000L, 162_000L], Ms(h));

        h.Clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal("У суперника впав прапорець", h.Act(1, "flag").Message);
        Assert.Equal([1], h.Room.Result!.Winners);
        Assert.Equal("time", Reason(h));
    }

    [Fact]
    public void Checkers_passport_offers_the_clock()
    {
        var opt = Assert.Single(new Checkers().Info.Options!);
        Assert.Equal("clock", opt.Key);
        Assert.Equal("none", opt.Default);
    }

    // ---------- останній хід у шашках ----------

    [Fact]
    public void Checkers_view_names_the_men_taken_by_the_last_move()
    {
        var h = Checkers("none");
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("lastTaken").ValueKind);
        CkMove(h, 0, "c3", "d4");
        Assert.Equal(0, h.View(0).GetProperty("lastTaken").GetArrayLength());
        CkMove(h, 1, "f6", "e5");
        CkMove(h, 0, "d4", "f6");
        Assert.Equal(["e5"], h.View(0).GetProperty("lastTaken").EnumerateArray().Select(e => e.GetString()));
    }

    // ---------- серія ----------

    [Fact]
    public void Series_follows_the_people_not_the_seats()
    {
        var h = Chess("none");
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("series").ValueKind);
        h.Act(1, "resign");                                   // Петро (чорні) здався — Оля 1:0
        var s = h.View(0).GetProperty("series");
        Assert.Equal([1, 0], s.GetProperty("wins").EnumerateArray().Select(e => e.GetInt32()));

        h.Rematch();                                          // місця обернулись: Петро тепер білі
        Assert.Equal("Петро", h.Room.Seats[0]);
        Assert.Equal([0, 1], h.View(0).GetProperty("series").GetProperty("wins").EnumerateArray().Select(e => e.GetInt32()));
        h.Act(0, "draw");
        h.Act(1, "draw");
        s = h.View(0).GetProperty("series");
        Assert.Equal(1, s.GetProperty("draws").GetInt32());
        Assert.Equal(2, s.GetProperty("games").GetInt32());
    }

    [Fact]
    public void Series_starts_over_when_somebody_new_sits_down()
    {
        var h = Checkers("none");
        h.Act(0, "resign");
        Assert.Equal(1, h.View(0).GetProperty("series").GetProperty("games").GetInt32());
        h.Leave("Петро");
        h.Join("Марко");                                     // новий склад відкриває дограний стіл наново
        h.Start();
        if (h.Room.Status != RoomStatus.Playing) h.Rematch();
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("series").ValueKind);
    }
}
