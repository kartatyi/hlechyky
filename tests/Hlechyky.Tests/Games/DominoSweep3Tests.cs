using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Прохід №3: «Глек підсідає» (№228) і «Козел» 2×2 до 101 (№208).</summary>
public class DominoSweep3Tests
{
    /// <summary>Хід людини: перша кістка, що лягає; нема — тягнути; базар порожній — пас.</summary>
    static void Human(RoomHarness h, int seat)
    {
        var v = h.View(seat);
        if (v.GetProperty("canPlay").GetBoolean())
            foreach (var t in v.GetProperty("hand").EnumerateArray())
                foreach (var end in new[] { "left", "right" })
                    if (h.Act(seat, "play", new { tile = new[] { t[0].GetInt32(), t[1].GetInt32() }, end }).Ok) return;
        if (v.GetProperty("mustDraw").GetBoolean() && h.Act(seat, "draw").Ok) return;
        Assert.True(h.Act(seat, "pass").Ok);
    }

    /// <summary>Грає партію до кінця: люди — <see cref="Human"/>, Глеки — на штовхан після «думки».</summary>
    static int PlayOut(RoomHarness h, int human = 0, int limit = 3000)
    {
        var steps = 0;
        while (h.Room.Status == RoomStatus.Playing && steps++ < limit)
        {
            var v = h.View(human);
            var turn = v.GetProperty("turn").GetInt32();
            if (v.GetProperty("bots") is { ValueKind: JsonValueKind.Array } bots && bots[turn].ValueKind == JsonValueKind.String)
            {
                h.Clock.AdvanceMs(BoardBots.ThinkMs);
                Assert.True(h.Act(human, BoardBots.Nudge).Ok);
            }
            else Human(h, turn);
        }
        return steps;
    }

    [Fact]
    public void Alone_needs_a_bot()
    {
        var h = new RoomHarness("domino");
        h.Join("Оля");
        Assert.False(h.Start().Ok);
        var b = new RoomHarness("domino", new { bots = "1", target = "1" });
        b.Join("Оля");
        Assert.True(b.Start().Ok);
        Assert.Equal(7, b.View(0).GetProperty("hand").GetArrayLength());   // удвох — по сім
    }

    [Fact]
    public void Bot_waits_then_plays_and_the_match_ends()
    {
        var h = new RoomHarness("domino", new { bots = "3", target = "50" }, seed: 5);
        h.Join("Оля");
        Assert.True(h.Start().Ok);
        var v = h.View(0);
        var turn = v.GetProperty("turn").GetInt32();
        if (turn != 0) Assert.False(h.Act(0, BoardBots.Nudge).Ok);   // ще не «подумав»
        PlayOut(h);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.All(h.Finished.Single().Seats.Skip(1), s => Assert.Null(s));
    }

    [Fact]
    public void Kozel_is_four_with_seven_each_and_no_boneyard()
    {
        var h = new RoomHarness("domino", new { mode = "kozel" });
        foreach (var n in new[] { "Оля", "Петро", "Марко" }) h.Join(n);
        Assert.False(h.Start().Ok);
        h.Join("Ганна");
        Assert.True(h.Start().Ok);
        var v = h.View(0);
        Assert.True(v.GetProperty("kozel").GetBoolean());
        Assert.Equal(0, v.GetProperty("boneyard").GetInt32());
        Assert.Equal(101, v.GetProperty("target").GetInt32());
        Assert.All(v.GetProperty("counts").EnumerateArray(), c => Assert.Equal(7, c.GetInt32()));
        // Першим ходить власник дубля 1-1.
        var turn = v.GetProperty("turn").GetInt32();
        var hand = h.View(turn).GetProperty("hand").EnumerateArray().Select(t => (t[0].GetInt32(), t[1].GetInt32()));
        Assert.Contains((1, 1), hand);
    }

    [Fact]
    public void Kozel_plays_to_101_and_the_goats_lose_as_a_pair()
    {
        var h = new RoomHarness("domino", new { mode = "kozel", bots = "3" }, seed: 11);
        h.Join("Оля");
        Assert.True(h.Start().Ok);
        PlayOut(h, limit: 20000);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var v = h.View(0);
        var scores = v.GetProperty("scores").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        // Штраф пари однаковий на обох її місцях, і хтось перейшов сотню.
        Assert.Equal(scores[0], scores[2]);
        Assert.Equal(scores[1], scores[3]);
        Assert.True(Math.Max(scores[0], scores[1]) >= Domino.KozelTarget);
        var won = h.Finished.Single().Result.Winners;
        var goats = scores[0] >= Domino.KozelTarget ? 0 : 1;
        Assert.Equal([1 - goats, 3 - goats], won);
        Assert.Equal(2, v.GetProperty("goats").GetArrayLength());
    }

    [Fact]
    public void Kozel_leaver_is_replaced_by_a_bot()
    {
        var h = new RoomHarness("domino", new { mode = "kozel" });
        foreach (var n in new[] { "Оля", "Петро", "Марко", "Ганна" }) h.Join(n);
        h.Start();
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        var v = h.View(0);
        Assert.Contains("🤖", v.GetProperty("bots")[1].GetString());
        Assert.Equal(7, v.GetProperty("counts")[1].GetInt32());
    }

    [Fact]
    public void Classic_by_default_is_unchanged()
    {
        var h = new RoomHarness("domino");
        foreach (var n in new[] { "Оля", "Петро", "Марко" }) h.Join(n);
        h.Start();
        var v = h.View(0);
        Assert.False(v.GetProperty("kozel").GetBoolean());
        Assert.Equal(50, v.GetProperty("target").GetInt32());
        Assert.Equal(5, v.GetProperty("hand").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("bots").ValueKind);
    }
}
