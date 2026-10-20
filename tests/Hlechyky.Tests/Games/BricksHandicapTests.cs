using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «⚖ Фора» в Цеглинах (пакет 3, 20.10): множник на сміття, яке місце ВІДПРАВЛЯЄ. Рушій (гасіння, хеші) той самий —
/// міняється лише посилка жертві, яку шле сервер.
/// </summary>
public class BricksHandicapTests
{
    sealed class FixedSkills(Dictionary<string, double> map) : ISkills
    {
        public double Strength(string gameId, string? nick) => map.TryGetValue(nick ?? "", out var s) ? s : 0;
    }

    // Микола +0,40, владік −0,24: середня 0,08 → Микола ×0,744, владік ×1,256
    static readonly Dictionary<string, double> Power = new() { ["Микола"] = 0.40, ["владік"] = -0.24 };

    static RoomHarness Table(object? options = null, string game = "bricks")
    {
        var h = new RoomHarness(game, options, 42, RoomHarness.WithService<ISkills>(new FixedSkills(Power)));
        h.Join("Микола");
        h.Join("владік");
        h.Start();
        return h;
    }

    static Bricks Game(RoomHarness h) => (Bricks)h.Room.Game;
    static BricksCore Wall(RoomHarness h, int seat) => Game(h).SeatState(seat).Core;
    static int SeatOf(RoomHarness h, string nick) => Enumerable.Range(0, 2).Single(s => h.NickOf(s) == nick);

    static void Go(RoomHarness h)
    {
        for (var i = 0; i < 200 && Game(h).Phase != Bricks.PhaseGo; i++) h.Tick();
        Assert.Equal(Bricks.PhaseGo, Game(h).Phase);
    }

    /// <summary>Четвірка: колодязь у стовпці 9 і вертикальна I над ним (своє сміття перед тим прибране — без гасіння).</summary>
    static void Four(RoomHarness h, int seat)
    {
        var c = Wall(h, seat);
        c.AdvanceTo(c.Tick + 1);
        c.CreditCount = 0;
        c.LoadRows(["8888888880", "8888888880", "8888888880", "8888888880", "8000000000"]);
        c.Clearing = 0;
        c.Spawn(BricksCore.I);
        (c.Rot, c.Bx, c.By, c.LowY) = (1, 7, 10, 10);
        Assert.True(h.Act(seat, "j", new { q = c.Seq + 1, e = new[] { c.Tick, BricksCore.KHard } }).Ok);
    }

    /// <summary>Кілька четвірок поспіль; повертає (скільки нарахував рушій, скільки рядів дісталось жертві).</summary>
    static (int Engine, int Got) Fours(RoomHarness h, int seat, int n)
    {
        var victim = 1 - seat;
        var got = 0;
        for (var i = 0; i < n; i++)
        {
            var before = Wall(h, victim);
            var was = Enumerable.Range(0, before.CreditCount).Sum(k => before.Credits[k].Rows);
            Four(h, seat);
            var v = Wall(h, victim);
            got += Enumerable.Range(0, v.CreditCount).Sum(k => v.Credits[k].Rows) - was;
        }
        return (Wall(h, seat).Sent, got);
    }

    [Fact]
    public void The_strong_sends_fewer_rows_than_cleared_and_the_weak_sends_more()
    {
        var h = Table();
        Go(h);
        var f = Game(h).HandicapFactors;
        int kolya = SeatOf(h, "Микола"), vlad = SeatOf(h, "владік");
        Assert.InRange(f[kolya], 0.74, 0.75);
        Assert.InRange(f[vlad], 1.25, 1.26);

        var (eng, got) = Fours(h, kolya, 6);
        Assert.True(eng >= 24, $"рушій нарахував {eng}");
        Assert.InRange(got, (int)Math.Floor(eng * f[kolya]) - 1, (int)Math.Floor(eng * f[kolya]));
        Assert.Equal(got, Game(h).SeatState(kolya).MatchSent);

        var (eng2, got2) = Fours(h, vlad, 6);
        Assert.InRange(got2, (int)Math.Floor(eng2 * f[vlad]) - 1, (int)Math.Floor(eng2 * f[vlad]));
        Assert.True(got2 > eng2);

        var v = h.View(null);
        var hc = v.GetProperty("handicap").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        Assert.Equal(4, hc.Length);
        Assert.Equal(Math.Round(f[kolya], 2), hc[kolya]);
        Assert.Equal(1, hc[2]);
        Assert.Equal(got, v.GetProperty("rsent")[kolya].GetInt32());
        Assert.Contains(h.Outbox.OfType<Journal>(), j => j.Text.StartsWith("⚖ Фора: Микола ×0,74"));
    }

    [Fact]
    public void Without_handicap_the_victim_gets_exactly_what_the_engine_sent()
    {
        var h = Table(new { handicap = Handicap.Off });
        Go(h);
        Assert.Empty(Game(h).HandicapFactors);
        var (eng, got) = Fours(h, SeatOf(h, "Микола"), 4);
        Assert.Equal(eng, got);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("handicap").ValueKind);
        Assert.DoesNotContain(h.Outbox.OfType<Journal>(), j => j.Text.StartsWith("⚖"));
    }

    [Fact]
    public void Rated_duel_has_no_handicap()
    {
        var h = Table(game: "bricks-duel");
        Assert.Empty(Game(h).HandicapFactors);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("handicap").ValueKind);
    }

    [Fact]
    public void Table_options_offer_the_handicap_and_the_duel_does_not()
    {
        var reg = RoomHarness.NewRegistry();
        Assert.Contains(reg.Info("bricks")!.Options!, o => o.Key == Handicap.Key && o.Default == Handicap.Auto);
        Assert.DoesNotContain(reg.Info("bricks-duel")!.Options!, o => o.Key == Handicap.Key);
    }
}
