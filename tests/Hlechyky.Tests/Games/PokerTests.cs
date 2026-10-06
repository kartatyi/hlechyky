using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit;

namespace Hlechyky.Tests.Games;

/// <summary>Спільне для тестів покеру через RoomHarness.</summary>
internal static class PokerKit
{
    public static RoomHarness Table(object options, int seed, params string[] nicks)
    {
        var h = new RoomHarness("poker", options, seed);
        foreach (var n in nicks) h.Stakes.Set(n, 1000);
        foreach (var n in nicks) Assert.True(h.Join(n).Ok, h.Reply.Message);
        return h;
    }

    public static Poker Game(RoomHarness h) => (Poker)h.Room.Game;

    /// <summary>Людина, чий зараз хід (кімнатне місце), або null.</summary>
    public static int? HumanTurn(RoomHarness h)
    {
        var g = Game(h);
        var c = g.State.Core;
        if (!c.Live || c.Runout || c.Turn < 0) return null;
        var x = g.State.Seats[c.Turn];
        if (x.Nick is null || x.Away || x.Gone) return null;
        return h.Room.Seats[x.RoomSeat] == x.Nick ? x.RoomSeat : null;
    }

    /// <summary>Тикати, а людям ходити за політикою, поки stop() або не скінчились тики.</summary>
    public static int Drive(RoomHarness h, Func<int, PokerLegal, (string Action, object? Payload)> policy, int maxTicks, Func<bool>? stop = null)
    {
        var t = 0;
        for (var guard = 0; t < maxTicks && guard < maxTicks * 20; guard++)
        {
            if (h.Room.Status != RoomStatus.Playing || (stop?.Invoke() ?? false)) break;
            if (HumanTurn(h) is { } seat)
            {
                var g = Game(h);
                var legal = g.State.Core.Legal(g.State.Core.Turn)!;
                var (a, p) = policy(seat, legal);
                var r = h.Act(seat, a, p);
                Assert.True(r.Ok, $"{a}: {r.Message}");
                continue;
            }
            h.Tick();
            t++;
        }
        return t;
    }

    public static (string, object?) Calls(int seat, PokerLegal l) => (l.Check ? "check" : "call", null);
    public static (string, object?) Shove(int seat, PokerLegal l) => ("allin", null);

    public static (string, object?) Random(Random rng, PokerLegal l)
    {
        var r = rng.Next(10);
        if (r < 2 && !l.Check) return ("fold", null);
        if (r < 6) return (l.Check ? "check" : "call", null);
        if (r < 9 && l.RaiseMax > 0) return ("raise", new { to = l.RaiseMin + rng.Next(Math.Max(1, (l.RaiseMax - l.RaiseMin) / 4)) });
        return ("allin", null);
    }

    public static string Json(JsonElement e) => e.GetRawText();
}

public class PokerTests
{
    [Fact]
    public void CatalogAndOptions()
    {
        Assert.Single(RoomHarness.NewRegistry().Catalog, g => g.Id == "poker");
        var info = new Poker().Info;
        Assert.Equal(GameGroup.Board, info.Group);
        Assert.Equal(8, info.MaxPlayers);
        Assert.Equal(StartMode.ByHost, info.Start);
        Assert.True(info.RealTime);
        Assert.Contains(info.Options!, o => o.Key == "format" && o.Default == "fun");
    }

    [Fact]
    public void BotsOnlyForFun()
    {
        var h = new RoomHarness("poker", new { format = "cash", bots = "2" });
        h.Stakes.Set("Оля", 1000);
        Assert.False(h.Join("Оля").Ok);
        Assert.Contains("на інтерес", h.Reply.Message);
    }

    [Fact]
    public void FunWithTwoBotsPlaysToTheEnd()
    {
        var h = PokerKit.Table(new { format = "fun", bots = "2", pace = "3" }, 3, "Оля");
        Assert.True(h.Start().Ok, h.Reply.Message);
        var g = PokerKit.Game(h);
        Assert.Equal(3, g.State.Seats.Count(x => x.Name is not null));
        Assert.Equal(new[] { "Глек 🤖", "Макітра 🤖" }, new[] { g.SeatBot(1), g.SeatBot(2) });
        PokerKit.Drive(h, PokerKit.Shove, 200_000);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Single(h.Finished);
        var places = g.State.Seats.Where(x => x.Name is not null).Select(x => x.Place).Order().ToArray();
        Assert.Equal(new[] { 1, 2, 3 }, places);
        Assert.False(h.Finished[0].TableMoney);
    }

    [Fact]
    public void IllegalActsDoNotChangeTheView()
    {
        var h = PokerKit.Table(new { format = "fun" }, 5, "Оля", "Петро", "Іра");
        h.Start();
        var turn = PokerKit.HumanTurn(h)!.Value;
        var other = Enumerable.Range(0, 3).First(s => s != turn);
        var before = PokerKit.Json(h.View(turn));
        Assert.False(h.Act(other, "call").Ok);                      // не його хід
        Assert.False(h.Act(turn, "raise", new { to = 25 }).Ok);     // менше за мінрейз
        Assert.False(h.Act(turn, "raise", new { col = 100 }).Ok);   // не та форма
        Assert.False(h.Act(turn, "raise", new { to = 999_999 }).Ok);
        Assert.False(h.Act(turn, "check").Ok);                      // треба доставити
        Assert.False(h.Act(turn, "dance").Ok);
        Assert.False(h.Act(turn, "rebuy").Ok);                      // не кеш
        Assert.False(h.Act(turn, "show").Ok);
        Assert.False(h.Act(turn, "back").Ok);
        Assert.Equal(before, PokerKit.Json(h.View(turn)));
        Assert.True(h.Act(turn, "call").Ok);
        Assert.True(h.Act(other, "away").Ok);
        Assert.False(h.Act(other, "away").Ok);
        Assert.True(h.Act(other, "back").Ok);
    }

    [Fact]
    public void RaiseAndViewsReachEveryoneFromTheTick()
    {
        var h = PokerKit.Table(new { format = "fun" }, 5, "Оля", "Петро");
        h.Start();
        var turn = PokerKit.HumanTurn(h)!.Value;
        h.Outbox.Clear();
        Assert.True(h.Act(turn, "raise", new { to = 60 }).Ok);
        Assert.DoesNotContain(h.Outbox, o => o is RoomViews);   // реалтайм: Act видів не шле
        h.Tick();                                                // а перший же тик (100 мс) — шле
        Assert.Contains(h.Outbox, o => o is RoomViews);
        var v = h.View(1 - turn);
        Assert.Equal(1 - turn, v.GetProperty("turn").GetInt32());
        var a = v.GetProperty("actions");
        Assert.Equal(40, a.GetProperty("call").GetInt32());
        Assert.Equal(100, a.GetProperty("raiseMin").GetInt32());
    }

    [Fact]
    public void NoForeignCardsOrDeckInViews()
    {
        var h = PokerKit.Table(new { format = "fun" }, 9, "Оля", "Петро", "Іра");
        h.Start();
        var c = PokerKit.Game(h).State.Core;
        foreach (int? seat in new int?[] { 0, 1, 2, null })
        {
            var v = h.View(seat);
            var json = PokerKit.Json(v);
            Assert.DoesNotContain("deck", json, StringComparison.OrdinalIgnoreCase);
            for (var p = 0; p < 3; p++)
            {
                var mine = seat == p;
                var cards = v.GetProperty("seats")[p].GetProperty("cards");
                Assert.Equal(mine, cards.ValueKind == JsonValueKind.Array);
                foreach (var card in c.Hole[p])
                    Assert.Equal(mine, json.Contains($"\"{PokerHand.Code(card)}\""));
            }
            // непороздані карти колоди теж ніде
            foreach (var card in c.Deck.Skip(6).Take(5)) Assert.DoesNotContain($"\"{PokerHand.Code(card)}\"", json);
        }
        Assert.True(h.View(0).GetProperty("seats")[1].GetProperty("hasCards").GetBoolean());
    }

    [Fact]
    public void TimeoutChecksOrFoldsThenAway()
    {
        var h = PokerKit.Table(new { format = "fun" }, 11, "Оля", "Петро");
        h.Start();
        var g = PokerKit.Game(h);
        var first = PokerKit.HumanTurn(h)!.Value;
        var hand = g.State.Core.HandNo;
        h.Tick(299);
        Assert.Equal(first, PokerKit.HumanTurn(h));
        h.Tick(2);                                   // 30 с минуло — автофолд (треба доставити)
        Assert.True(g.State.Core.HandNo == hand && !g.State.Core.Live);
        Assert.Equal(1, g.State.Seats.Single(x => x.RoomSeat == first).Strikes);
        // ще одне прострочення того самого — ☕
        for (var i = 0; i < 2000 && !g.State.Seats.Single(x => x.RoomSeat == first).Away; i++)
        {
            if (PokerKit.HumanTurn(h) is { } s && s != first) h.Act(s, g.State.Core.Legal(g.State.Core.Turn)!.Check ? "check" : "call");
            else h.Tick();
        }
        Assert.True(h.View(first).GetProperty("seats")[g.State.Seats.FindIndex(x => x.RoomSeat == first)].GetProperty("away").GetBoolean());
    }

    [Fact]
    public void BlindsRiseWithTime()
    {
        var h = PokerKit.Table(new { format = "fun", pace = "3" }, 13, "Оля", "Петро");
        h.Start();
        Assert.Equal(1, h.View(0).GetProperty("level").GetInt32());
        PokerKit.Drive(h, PokerKit.Calls, 1801);
        var v = h.View(0);
        Assert.True(v.GetProperty("level").GetInt32() >= 2);
        PokerKit.Drive(h, PokerKit.Calls, 200, () => PokerKit.Game(h).State.Core.BigBlind >= 30);
        Assert.True(PokerKit.Game(h).State.Core.BigBlind >= 30);
    }

    [Fact]
    public void SameSeedSameCards()
    {
        string Deal(int seed)
        {
            var h = PokerKit.Table(new { format = "fun", bots = "1" }, seed, "Оля", "Петро");
            h.Start();
            PokerKit.Drive(h, PokerKit.Calls, 300);
            return PokerKit.Json(h.View(0));
        }
        Assert.Equal(Deal(21), Deal(21));
        Assert.NotEqual(Deal(21), Deal(22));
    }

    [Fact]
    public void SaveLoadMidHand()
    {
        var h = PokerKit.Table(new { format = "fun", bots = "2" }, 17, "Оля", "Петро");
        h.Start();
        PokerKit.Drive(h, PokerKit.Calls, 40);
        var g = PokerKit.Game(h);
        var save = g.Save()!;
        var views = new[] { PokerKit.Json(h.View(0)), PokerKit.Json(h.View(1)), PokerKit.Json(h.View(null)) };
        lock (h.Room.Sync) g.Load(save);
        Assert.Equal(save, g.Save());
        Assert.Equal(views, new[] { PokerKit.Json(h.View(0)), PokerKit.Json(h.View(1)), PokerKit.Json(h.View(null)) });
        PokerKit.Drive(h, PokerKit.Calls, 500);   // і грається далі
        Assert.True(g.State.Core.HandNo > 1);
    }

    [Fact]
    public void LeaverKeepsChipsAndCanComeBack()
    {
        var h = PokerKit.Table(new { format = "fun", bots = "1" }, 19, "Оля", "Петро");
        h.Start();
        var g = PokerKit.Game(h);
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        var p = g.State.Seats.FindIndex(x => x.Nick == "Петро");
        Assert.True(g.State.Seats[p].Gone);
        PokerKit.Drive(h, PokerKit.Calls, 300);
        Assert.Equal("Турнір уже йде: підсісти не можна — повернутись може лише той, хто грав", h.Join("Іра").Message);
        Assert.True(h.Join("Петро").Ok, h.Reply.Message);
        Assert.False(g.State.Seats[p].Gone);
        Assert.Equal("Петро", h.Room.Seats[g.State.Seats[p].RoomSeat]);
        Assert.Equal(p, h.View(g.State.Seats[p].RoomSeat).GetProperty("me").GetInt32());
    }

    [Fact]
    public void ShowCardsAfterUncontestedWin()
    {
        var h = PokerKit.Table(new { format = "fun" }, 23, "Оля", "Петро");
        h.Start();
        var g = PokerKit.Game(h);
        var turn = PokerKit.HumanTurn(h)!.Value;
        h.Act(turn, "raise", new { to = 200 });
        var other = 1 - turn;
        h.Act(other, "fold");
        Assert.True(h.View(turn).GetProperty("canShow").GetBoolean());
        var hidden = PokerKit.Json(h.View(other));
        var mine = g.State.Core.Hole[g.State.Seats.FindIndex(x => x.RoomSeat == turn)].Select(PokerHand.Code).ToArray();
        Assert.DoesNotContain($"\"{mine[0]}\"", hidden);
        Assert.True(h.Act(turn, "show").Ok);
        Assert.Contains($"\"{mine[0]}\"", PokerKit.Json(h.View(other)));
        Assert.False(h.Act(turn, "show").Ok);
    }

}

[Collection(SerialPerf.Name)]
public class PokerPerfTests
{
    [Fact]
    [Trait("Category", "Perf")]
    public void ThousandTicksFast()
    {
        var h = PokerKit.Table(new { format = "fun", bots = "5" }, 29, "Оля", "Петро");
        h.Start();
        var sw = Stopwatch.StartNew();
        h.Tick(1000);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000, $"1000 тиків за {sw.ElapsedMilliseconds} мс");
    }
}
