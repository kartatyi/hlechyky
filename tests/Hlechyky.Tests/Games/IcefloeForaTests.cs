using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>«⚖ Фора» в Крижині (пакет 3, 20.10): переможений раунду стартує наступний із предметом.</summary>
public class IcefloeForaTests
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван"];

    static RoomHarness Table(int players, object options, int seed = 42)
    {
        var h = new RoomHarness("icefloe", options, seed);
        foreach (var nick in Nicks.Take(players)) h.Join(nick);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static Icefloe Game(RoomHarness h) => (Icefloe)h.Room.Game;
    static IcefloeCore Core(RoomHarness h) => Game(h).Core;

    static void ToGo(RoomHarness h)
    {
        for (var i = 0; i < 300 && Game(h).Phase != Icefloe.PhGo; i++) h.Tick();
        Assert.Equal(Icefloe.PhGo, Game(h).Phase);
    }

    static void Sink(RoomHarness h, int seat)
    {
        var b = Core(h).Bodies[seat];
        b.B.X = 40;
        b.B.Y = 40;
        b.B.Vx = b.B.Vy = 0;
        h.Tick();
    }

    /// <summary>Топити по черзі <paramref name="order"/> і дочекатись відліку наступного раунду.</summary>
    static void Round(RoomHarness h, params int[] order)
    {
        ToGo(h);
        foreach (var s in order) Sink(h, s);
        Assert.Equal(Icefloe.PhEnd, Game(h).Phase);
        for (var i = 0; i < 200 && Game(h).Phase != Icefloe.PhReady; i++) h.Tick();
        Assert.Equal(Icefloe.PhReady, Game(h).Phase);
    }

    static bool Holds(IcefloeBody b) => b.Spikes > 0 || b.Jug > 0 || b.Skates > 0 || b.Fist > 0 || b.Ammo > 0;

    static int[] ForaSeats(RoomHarness h) =>
        h.View(null).TryGetProperty("fora", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.Array
            ? [.. f.EnumerateArray().Select(x => x[0].GetInt32()).Order()] : [];

    [Fact]
    public void The_first_to_fall_starts_the_next_round_with_an_item_and_the_winner_without()
    {
        var kinds = new HashSet<int>();
        for (var seed = 1; seed <= 10; seed++)
        {
            var h = Table(3, new { wins = "3" }, seed);
            Assert.Empty(ForaSeats(h));                    // перший раунд — нікому
            Round(h, 1, 2);                                // Петро шубовснув першим, Ганна другою, раунд Олі
            var c = Core(h);
            Assert.True(Holds(c.Bodies[1]));
            Assert.False(Holds(c.Bodies[0]));
            Assert.False(Holds(c.Bodies[2]));
            Assert.Equal([1], ForaSeats(h));
            kinds.Add(h.View(null).GetProperty("fora")[0][1].GetInt32());

            // ефект тане лише в грі: на «Го» ще повний
            ToGo(h);
            var b = Core(h).Bodies[1];
            Assert.True(b.Ammo > 0 || Math.Max(Math.Max(b.Spikes, b.Jug), Math.Max(b.Skates, b.Fist)) >= IcefloeCore.EffectTicks - 1);
        }
        Assert.True(kinds.Count >= 3);
        Assert.DoesNotContain(IcefloeCore.KindGust, kinds);
        Assert.DoesNotContain(IcefloeCore.KindFrost, kinds);
    }

    [Fact]
    public void In_teams_the_whole_losing_team_gets_an_item()
    {
        var h = Table(4, new { wins = "3", teams = "seat" });
        Round(h, 1, 3);                                    // руді (1 і 3) у воді — раунд синіх
        var c = Core(h);
        Assert.True(Holds(c.Bodies[1]) && Holds(c.Bodies[3]));
        Assert.False(Holds(c.Bodies[0]) || Holds(c.Bodies[2]));
        Assert.Equal([1, 3], ForaSeats(h));
    }

    [Fact]
    public void Without_handicap_nobody_gets_an_item()
    {
        var h = Table(3, new { wins = "3", handicap = Handicap.Off });
        Round(h, 1, 2);
        for (var s = 0; s < 3; s++) Assert.False(Holds(Core(h).Bodies[s]));
        Assert.Empty(ForaSeats(h));
    }
}
