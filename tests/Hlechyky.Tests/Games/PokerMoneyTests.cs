using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit;

namespace Hlechyky.Tests.Games;

/// <summary>Гроші за покерним столом: банк столу каркаса, кеш-стіл, турнір на черепки, перезапуск.</summary>
public class PokerMoneyTests
{
    static long Wallets(RoomHarness h, params string[] nicks) => nicks.Sum(n => (long)h.Stakes.Balance(n));

    /// <summary>Гроші не народжуються й не зникають: гаманці + те, що лежить за столом, — як на початку.</summary>
    static void Conserved(RoomHarness h, long total, params string[] nicks) =>
        Assert.Equal(total, Wallets(h, nicks) + h.Room.Bank.Held);

    [Fact]
    public void CashBuyInAndCanStartShortfall()
    {
        var h = PokerKit.Table(new { format = "cash", stakes = "1" }, 1, "Оля", "Петро", "Іра");
        h.Stakes.Set("Іра", 60);
        Assert.False(h.Start().Ok);
        Assert.Equal("У Іра бракує черепків на викуп (100)", h.Reply.Message);
        h.Stakes.Set("Іра", 1000);
        Assert.True(h.Start().Ok, h.Reply.Message);
        Assert.Equal(900, h.Stakes.Balance("Оля"));
        Assert.Equal(300, h.Room.Bank.Held);
        Assert.Contains(h.Stakes.Calls, c => c.StartsWith("spend:Оля:100:table-in:", StringComparison.Ordinal));
        var v = h.View(0);
        Assert.Equal(100, v.GetProperty("cash").GetProperty("bought").GetInt32());
        Assert.True(PokerKit.Game(h).CheckInvariant());
    }

    [Fact]
    public void LateJoinPaysAtOnceOrSaysWhy()
    {
        var h = PokerKit.Table(new { format = "cash", stakes = "2" }, 2, "Оля", "Петро");
        h.Start();
        h.Stakes.Set("Бідний", 100);
        h.Stakes.Set("Іра", 1000);
        var total = Wallets(h, "Оля", "Петро", "Іра", "Бідний") + h.Room.Bank.Held;
        Assert.False(h.Join("Бідний").Ok);
        Assert.Equal("Щоб сісти, треба 250 черепків — у тебе 100", h.Reply.Message);
        Assert.True(h.Join("Іра").Ok, h.Reply.Message);
        Assert.Equal("Є! Ти за столом: викуп 250 черепків — граєш з наступної роздачі", h.Reply.Message);
        Assert.Equal(750, h.Stakes.Balance("Іра"));
        var g = PokerKit.Game(h);
        var p = g.State.Seats.FindIndex(x => x.Nick == "Іра");
        Assert.Equal("wait", g.State.Seats[p].State);
        Assert.False(g.State.Core.Dealt[p] && g.State.Core.Live);
        Conserved(h, total, "Оля", "Петро", "Іра", "Бідний");
        PokerKit.Drive(h, PokerKit.Calls, 2000, () => g.State.Core.Live && g.State.Core.Dealt[p]);
        Assert.True(g.State.Core.Dealt[p]);
        Assert.True(g.CheckInvariant());
    }

    [Fact]
    public void StandUpMidHandFoldsAndPaysTheRest()
    {
        var h = PokerKit.Table(new { format = "cash" }, 3, "Оля", "Петро", "Іра");
        h.Start();
        var nicks = new[] { "Оля", "Петро", "Іра" };
        var total = Wallets(h, nicks) + h.Room.Bank.Held;
        var g = PokerKit.Game(h);
        var room = h.Room;
        var turn = PokerKit.HumanTurn(h)!.Value;
        h.Act(turn, "raise", new { to = 10 });
        var who = h.NickOf(turn);
        var p = g.State.Seats.FindIndex(x => x.Nick == who);
        Assert.True(h.Leave(who).Ok);
        Assert.Equal(900 + 90, h.Stakes.Balance(who));         // 100 − 10 у банку роздачі
        Assert.Null(g.State.Seats[p].Nick);
        Assert.Equal(10, g.State.Core.Total[p]);               // поставлене лишається в банку
        Assert.Contains(h.Stakes.Calls, c => c.StartsWith($"grant:{who}:90:table-out:", StringComparison.Ordinal));
        Conserved(h, total, nicks);
        Assert.True(g.CheckInvariant());
        // решта догравають, гроші не губляться
        PokerKit.Drive(h, PokerKit.Calls, 3000, () => g.State.Core.HandNo >= 3);
        Conserved(h, total, nicks);
        // встають усі — стіл розходиться, банк порожній
        foreach (var n in nicks.Where(n => n != who)) h.Leave(n);
        Assert.Equal(RoomStatus.Finished, room.Status);
        Assert.Single(h.Finished);
        Assert.Equal(0, room.Bank.Held);
        Assert.Equal(total, Wallets(h, nicks));
        Assert.True(h.Finished[0].TableMoney);
    }

    [Fact]
    public void NewcomerOnLeaversChairSeesNoCardsOfHis()
    {
        var h = PokerKit.Table(new { format = "cash" }, 15, "Оля", "Петро", "Іра");
        h.Stakes.Set("Новий", 1000);
        h.Start();
        var g = PokerKit.Game(h);
        var turn = PokerKit.HumanTurn(h)!.Value;
        h.Act(turn, "call");
        var leaverSeat = PokerKit.HumanTurn(h)!.Value;
        var p = g.State.Seats.FindIndex(x => x.RoomSeat == leaverSeat);
        var hole = g.State.Core.Hole[p].Select(PokerHand.Code).ToArray();
        h.Leave(h.NickOf(leaverSeat));
        Assert.True(h.Join("Новий").Ok, h.Reply.Message);
        Assert.Equal("Новий", h.Room.Seats[leaverSeat]);
        Assert.True(g.State.Core.Live);
        foreach (int? s in new int?[] { leaverSeat, null })
        {
            var json = PokerKit.Json(h.View(s));
            Assert.All(hole, c => Assert.DoesNotContain($"\"{c}\"", json));
        }
        Assert.Equal("wait", h.View(leaverSeat).GetProperty("seats")[p].GetProperty("state").GetString());
        Assert.False(h.View(null).GetProperty("cash").GetProperty("canRebuy").GetBoolean());
        Assert.True(g.CheckInvariant());
    }

    [Fact]
    public void RebuyTopsUpBetweenHands()
    {
        var h = PokerKit.Table(new { format = "cash" }, 4, "Оля", "Петро");
        h.Start();
        var g = PokerKit.Game(h);
        var total = Wallets(h, "Оля", "Петро") + h.Room.Bank.Held;
        var turn = PokerKit.HumanTurn(h)!.Value;
        h.Act(turn, "fold");                               // мінус малий сліпий
        var p = g.State.Seats.FindIndex(x => x.RoomSeat == turn);
        Assert.Equal(99, g.State.Core.Stack[p]);
        var v = h.View(turn).GetProperty("cash");
        Assert.True(v.GetProperty("canRebuy").GetBoolean());
        Assert.Equal(1, v.GetProperty("rebuyCost").GetInt32());
        Assert.True(h.Act(turn, "rebuy").Ok);
        Assert.Equal(100, g.State.Core.Stack[p]);
        Assert.False(h.Act(turn, "rebuy").Ok);             // повний
        Assert.Equal(101, g.State.Seats[p].Bought);
        Conserved(h, total, "Оля", "Петро");
        Assert.True(g.CheckInvariant());
    }

    [Fact]
    public void AwayTenMinutesStandsUpWithPayout()
    {
        var h = PokerKit.Table(new { format = "cash" }, 5, "Оля", "Петро", "Іра");
        h.Start();
        var g = PokerKit.Game(h);
        var total = Wallets(h, "Оля", "Петро", "Іра") + h.Room.Bank.Held;
        Assert.True(h.Act(2, "away").Ok);
        PokerKit.Drive(h, PokerKit.Calls, 6100);
        var p = g.State.Seats.FindIndex(x => x.Nick == "Іра");
        Assert.Equal("out", g.State.Seats[p].State);
        Assert.Equal(0, g.State.Core.Stack[p]);
        Assert.Equal("Іра", h.Room.Seats[2]);              // місце за нею
        Conserved(h, total, "Оля", "Петро", "Іра");
        Assert.True(h.Act(2, "rebuy").Ok);                 // повертається за повний викуп
        Assert.Equal(100, g.State.Core.Stack[p]);
        Conserved(h, total, "Оля", "Петро", "Іра");
    }

    [Fact]
    public void FiveHundredHandsKeepTheSum()
    {
        var nicks = new[] { "Оля", "Петро", "Іра", "Марко", "Стефа" };
        var h = PokerKit.Table(new { format = "cash" }, 6, nicks[..4]);
        foreach (var n in nicks) h.Stakes.Set(n, 3000);
        h.Start();
        var g = PokerKit.Game(h);
        var room = h.Room;
        var total = Wallets(h, nicks) + h.Room.Bank.Held;
        var rng = new Random(6);
        var lastHand = 0L;
        var idle = 0;
        var guard = 0;
        while (g.State.Core.HandNo < 500 && guard++ < 400_000 && h.Room.Status == RoomStatus.Playing)
        {
            if (PokerKit.HumanTurn(h) is { } seat)
            {
                var (a, p) = PokerKit.Random(rng, g.State.Core.Legal(g.State.Core.Turn)!);
                Assert.True(h.Act(seat, a, p).Ok);
            }
            else h.Tick();
            if (!g.State.Core.Live && (g.State.Core.HandNo != lastHand || ++idle > 60))
            {
                lastHand = g.State.Core.HandNo;
                idle = 0;
                Assert.True(g.CheckInvariant());
                Conserved(h, total, nicks);
                // між роздачами: хтось підсідає, хтось докуповується чи встає, хтось забирає фішки
                var free = nicks.Where(n => !room.Seats.Contains(n) && h.Stakes.Balance(n) >= 100).ToList();
                if (free.Count > 0 && (rng.Next(4) == 0 || room.Seats.Count(s => s is not null) < 3)) h.Join(free[rng.Next(free.Count)]);
                foreach (var x in g.State.Seats.Where(x => x.Nick is not null && x.State == "out").ToList())
                    if (rng.Next(3) > 0 && h.Stakes.Balance(x.Nick!) >= 100) h.Act(x.RoomSeat, "rebuy");
                    else if (room.Seats.Count(s => s is not null) > 2) h.Leave(x.Nick!);
                if (room.Seats.Count(s => s is not null) > 2 && rng.Next(8) == 0
                    && g.State.Seats.Where(x => x.Nick is not null).OrderBy(_ => rng.Next()).FirstOrDefault() is { } any) h.Leave(any.Nick!);
            }
        }
        Assert.True(g.State.Core.HandNo >= 500, $"зіграно {g.State.Core.HandNo}: {room.Status} {room.Result?.Text}");
        Conserved(h, total, nicks);
        foreach (var n in nicks) if (room.Seats.Contains(n)) h.Leave(n);
        Assert.Equal(0, room.Bank.Held);
        Assert.Equal(total, Wallets(h, nicks));
    }

    static FrozenTables RoundTrip(FrozenTables t)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        return JsonSerializer.Deserialize<FrozenTables>(JsonSerializer.Serialize(t, json), json)!;
    }

    static Rooms Fresh(RoomHarness h) =>
        new(h.Registry, h.Clock, new GameEvents(), h.Stakes, new FakeStore(), RoomHarness.Empty()) { SeedOverride = 99 };

    [Fact]
    public void CrashMidHandRefundsStackPlusCommittedOnce()
    {
        var nicks = new[] { "Оля", "Петро", "Іра" };
        var h = PokerKit.Table(new { format = "cash" }, 7, nicks);
        h.Start();
        var g = PokerKit.Game(h);
        var turn = PokerKit.HumanTurn(h)!.Value;
        h.Act(turn, "raise", new { to = 30 });
        h.Leave(h.NickOf((turn + 1) % 3));    // встав посеред роздачі: його внесене лишилось у банку роздачі
        var total = Wallets(h, nicks) + h.Room.Bank.Held;
        var owed = g.SettleTable()!;
        Assert.Equal(h.Room.Bank.Held, owed.Values.Sum());
        var snap = RoundTrip(h.Rooms.Capture(clean: false));   // знімок про всяк випадок — партія буде перервана

        var a = Fresh(h);
        var report = a.Restore(snap);
        Assert.Equal(1, report.Interrupted);
        Assert.Equal(total, Wallets(h, nicks));
        foreach (var (nick, amount) in owed) Assert.Contains(h.Stakes.Calls, c => c.StartsWith($"grant:{nick}:{amount}:table-out:", StringComparison.Ordinal));
        Assert.Contains("черепки зі столу повернуто", a.Find(h.RoomId)!.Result!.Text);
        // той самий знімок удруге — нічого не подвоюється
        var calls = h.Stakes.Calls.Count;
        Fresh(h).Restore(snap);
        Assert.Equal(total, Wallets(h, nicks));
        Assert.Equal(calls, h.Stakes.Calls.Count);
    }

    [Fact]
    public void CleanFreezeContinuesWithoutMovingMoney()
    {
        var nicks = new[] { "Оля", "Петро" };
        var h = PokerKit.Table(new { format = "cash" }, 8, nicks);
        h.Start();
        PokerKit.Drive(h, PokerKit.Calls, 30);
        var before = PokerKit.Game(h).Save();
        var wallets = Wallets(h, nicks);
        var snap = RoundTrip(h.Rooms.Freeze());
        var a = Fresh(h);
        Assert.Equal(1, a.Restore(snap).Continued);
        Assert.Equal(wallets, Wallets(h, nicks));
        var room = a.Find(h.RoomId)!;
        var g = (Poker)room.Game;
        Assert.Equal(before, g.Save());
        Assert.Equal(200, room.Bank.Held);
        Assert.True(g.CheckInvariant());
        // і гра йде далі: встали — гроші повернулись повністю
        foreach (var n in nicks) a.Leave(h.RoomId, n);
        Assert.Equal(wallets + 200, Wallets(h, nicks));
        Assert.Empty(a.Busy());
    }

    [Fact]
    public void TableMoneyGamesGetNoStandardRewardsButCountResults()
    {
        using var rig = new EconomyRig();
        var info = new Poker().Info;
        var plain = rig.Finished("p1", info, ["Оля", "Петро"], [0]);
        rig.Events.Raise(plain);
        var after = rig.Economy.Balance("Оля");
        Assert.True(after > 0);
        rig.Events.Raise(rig.Finished("p2", info, ["Оля", "Петро"], [0]) with { TableMoney = true });
        Assert.Equal(after, rig.Economy.Balance("Оля"));                 // за стіл на черепки — без нагороди
        Assert.True(rig.Achievements.Has("Оля", "first-win"));
        // реальні тексти леджера
        Assert.Equal("викуп — Покер", rig.Economy.Reason("table-buyin:poker"));
        Assert.Equal("забрав зі столу — Покер", rig.Economy.Reason("table-cashout:poker"));
        Assert.Equal("приз турніру — Покер", rig.Economy.Reason("table-prize:poker"));
    }

    [Fact]
    public void NoLateBuyInWhileFrozen()
    {
        var h = PokerKit.Table(new { format = "cash" }, 16, "Оля", "Петро");
        h.Stakes.Set("Іра", 1000);
        h.Start();
        h.Rooms.Freeze();
        Assert.False(h.Join("Іра").Ok);
        Assert.Equal(1000, h.Stakes.Balance("Іра"));
        Assert.Equal(200, h.Room.Bank.Held);
    }

    [Fact]
    public void CashTableDoesNotBlockDeploy()
    {
        var h = PokerKit.Table(new { format = "cash" }, 9, "Оля", "Петро");
        h.Start();
        Assert.Empty(h.Rooms.Busy());
    }

    [Fact]
    public void TourFeesShortfallAndPrizes()
    {
        var nicks = new[] { "Оля", "Петро", "Іра", "Марко", "Стефа" };
        var h = PokerKit.Table(new { format = "tour", buyin = "100", pace = "3" }, 10, nicks);
        h.Stakes.Set("Марко", 40);
        Assert.False(h.Start().Ok);
        Assert.Equal("У Марко бракує черепків на внесок (100)", h.Reply.Message);
        h.Stakes.Set("Марко", 1000);
        Assert.True(h.Start().Ok);
        var total = Wallets(h, nicks) + h.Room.Bank.Held;
        Assert.Equal(500, h.Room.Bank.Held);
        var v = h.View(0).GetProperty("tour");
        Assert.Equal(500, v.GetProperty("pool").GetInt32());
        PokerKit.Drive(h, PokerKit.Shove, 400_000);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var g = PokerKit.Game(h);
        var first = g.State.Seats.Single(x => x.Place == 1).Nick!;
        var second = g.State.Seats.Single(x => x.Place == 2).Nick!;
        Assert.Contains($"grant:{first}:350:table-out:", string.Join("\n", h.Stakes.Calls));
        Assert.Contains($"grant:{second}:150:table-out:", string.Join("\n", h.Stakes.Calls));
        Assert.Equal(0, h.Room.Bank.Held);
        Assert.Equal(total, Wallets(h, nicks));
        Assert.Equal((350, 150), Poker.Prizes(500, 5));
        Assert.Equal((200, 0), Poker.Prizes(200, 4));
        Assert.Equal((71, 30), Poker.Prizes(101, 5));
        Assert.Contains(h.Awards, a => a.Reason == "ach:poker-champ" && a.Nick == first);
        Assert.True(h.Finished[0].TableMoney);
    }

    [Fact]
    public void TourInterruptReturnsFees()
    {
        var nicks = new[] { "Оля", "Петро", "Іра" };
        var h = PokerKit.Table(new { format = "tour", buyin = "50" }, 11, nicks);
        h.Start();
        PokerKit.Drive(h, PokerKit.Calls, 50);
        var snap = RoundTrip(h.Rooms.Capture(clean: false));
        Fresh(h).Restore(snap);
        Assert.All(nicks, n => Assert.Equal(1000, h.Stakes.Balance(n)));
    }

    [Fact]
    public void TourEveryoneGoneSplitsByChips()
    {
        var nicks = new[] { "Оля", "Петро" };
        var h = PokerKit.Table(new { format = "tour", buyin = "20" }, 12, nicks);
        h.Start();
        var g = PokerKit.Game(h);
        foreach (var n in nicks) h.Leave(n);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);       // чекає повернення
        h.Tick(6100);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal(0, h.Room.Bank.Held);
        Assert.Equal(2000, Wallets(h, nicks));
        Assert.Equal("🏁 Турнір зупинено: усі пішли", h.Room.Result!.Verdict);
    }

    [Fact]
    public void AbandonedMoneyTableIsSettledBeforeDrop()
    {
        // гра зламалась і не спорожнила банк: каркас повертає «вніс мінус забрав» перед прибиранням
        var h = PokerKit.Table(new { format = "cash" }, 13, "Оля", "Петро");
        h.Start();
        var g = PokerKit.Game(h);
        lock (h.Room.Sync) g.State.Seats.ForEach(x => x.Nick = null);   // гра «забула» людей
        h.Leave("Оля");
        h.Leave("Петро");
        Assert.Equal(2000, Wallets(h, "Оля", "Петро"));
    }

    [Fact]
    public void FrameworkRefusesToPayMoreThanHeld()
    {
        var h = PokerKit.Table(new { format = "cash" }, 14, "Оля", "Петро");
        h.Start();
        var ctx = h.Room.Game.Ctx;
        lock (h.Room.Sync)
        {
            Assert.False(ctx.CashOut("Оля", 201, TableMoney.CashOut));
            Assert.True(ctx.CashOut("Оля", 0, TableMoney.CashOut));
        }
        Assert.Equal(200, h.Room.Bank.Held);
        Assert.Equal(900, h.Stakes.Balance("Оля"));
    }
}
