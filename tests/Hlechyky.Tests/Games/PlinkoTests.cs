using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Обв'язка: свої ставки, сховище й каса автоматів (Балачки — одразу), дошка через справжній каркас.</summary>
public sealed class PlinkoKit
{
    public FakeStakes Stakes { get; } = new();
    public FakeStore Store { get; } = new();
    public FakeSlotsWire Wire { get; } = new();
    public PlinkoOptions Options { get; } = new();
    public FakeClock Clock { get; } = new();
    public SlotsBank Bank { get; }
    public RoomHarness H { get; }

    public PlinkoKit(string nick = "Оля", int wallet = 10_000, int seed = 42, Action<PlinkoOptions>? opts = null)
    {
        opts?.Invoke(Options);
        Stakes.Set(nick, wallet);
        Bank = new SlotsBank(Stakes, Store, () => new SlotsOptions(), Clock, Wire, defer: a => a());
        H = new RoomHarness("plinko", services: RoomHarness.WithService(new PlinkoDesk(Bank, () => Options)), seed: seed);
        var r = H.Solo(nick);
        Assert.True(r.Ok, r.Message);
    }

    public Plinko G => (Plinko)H.Room.Game;
    public JsonElement View => H.View(0);
    public string Hash => View.GetProperty("hash").GetString()!;
    public JsonElement Last => View.GetProperty("recent")[0];

    public ActResult Drop(int bet = 10, int n = 1, int rows = 12, string risk = PlinkoCore.Low, string? hash = null) =>
        H.Act(0, "drop", new { bet, n, rows, risk, hash = hash ?? Hash });

    /// <summary>Підкласти seed наступного кидка: перший, у якого горошина №1 на <paramref name="rows"/> рядах іде шляхом <paramref name="path"/>.</summary>
    public string Rig(Func<string, bool> path, int rows)
    {
        for (var i = 0; ; i++)
        {
            var s = PlinkoCore.Hash("rig:" + i);
            if (!path(PlinkoCore.Path(s, 1, rows))) continue;
            G.State.NextSeed = s;
            G.State.NextHash = PlinkoCore.Hash(s);
            return s;
        }
    }
}

public class PlinkoTests
{
    static int Sum(FakeStakes s, string suffix) => s.Ledger.Where(m => m.Ref.EndsWith(suffix, StringComparison.Ordinal)).Sum(m => m.Delta);

    [Fact]
    public void A_drop_charges_the_batch_once_and_pays_every_ball()
    {
        var k = new PlinkoKit(wallet: 1_000_000);
        long won = 0;
        string[] risks = [PlinkoCore.Low, PlinkoCore.Mid, PlinkoCore.High];
        for (var i = 1; i <= 60; i++)
        {
            var rows = 8 + i % 9;
            var r = k.Drop(bet: 20, n: 10, rows: rows, risk: risks[i % 3]);
            Assert.True(r.Ok, r.Message);
            var last = k.Last;
            Assert.Equal(i, last.GetProperty("seq").GetInt32());
            Assert.Equal(i, k.View.GetProperty("seq").GetInt32());
            var balls = last.GetProperty("balls").EnumerateArray().ToList();
            Assert.Equal(10, balls.Count);
            Assert.All(balls, b => Assert.Equal(rows, b.GetProperty("p").GetString()!.Length));
            Assert.Equal(balls.Sum(b => b.GetProperty("win").GetInt32()), last.GetProperty("win").GetInt32());
            won += last.GetProperty("win").GetInt32();
        }
        Assert.Equal(60, k.Stakes.Ledger.Count(m => m.Ref.EndsWith(":bet", StringComparison.Ordinal)));
        Assert.Equal(-60 * 200, Sum(k.Stakes, ":bet"));
        Assert.Equal(won, Sum(k.Stakes, ":win"));
        Assert.Equal(1_000_000 - 12_000 + won, k.Stakes.Balance("Оля"));
        Assert.Equal(k.Stakes.Balance("Оля"), k.View.GetProperty("balance").GetInt32());
        Assert.All(k.Stakes.Reasons.Where(x => x.Key.EndsWith(":bet")), x => Assert.Equal("plinko-bet:plinko", x.Value));
        Assert.All(k.Stakes.Reasons.Where(x => x.Key.EndsWith(":win")), x => Assert.Equal("plinko-win:plinko", x.Value));
        Assert.Empty(k.Bank.Pending());
        Assert.Equal(Plinko.RecentLen, k.View.GetProperty("recent").GetArrayLength());
        Assert.Equal(600, k.View.GetProperty("drops").GetInt64());
        Assert.Equal(10_000, new SlotsBank(k.Stakes, k.Store, () => new SlotsOptions(), k.Clock).Pot);   // у Скарбничку — нічого
    }

    [Fact]
    public void The_hash_shown_before_a_drop_opens_to_its_seed_and_paths()
    {
        var k = new PlinkoKit();
        var before = k.Hash;
        Assert.Matches("^[0-9a-f]{64}$", before);
        Assert.True(k.Drop(bet: 10, n: 5, rows: 16, risk: PlinkoCore.High).Ok);
        var d = k.Last;
        var seed = d.GetProperty("seed").GetString()!;
        Assert.Equal(before, d.GetProperty("hash").GetString());
        Assert.Equal(before, PlinkoCore.Hash(seed));
        var t = PlinkoCore.Table(16, PlinkoCore.High);
        var i = 0;
        foreach (var b in d.GetProperty("balls").EnumerateArray())
        {
            var p = PlinkoCore.Path(seed, ++i, 16);
            Assert.Equal(p, b.GetProperty("p").GetString());
            Assert.Equal(PlinkoCore.Slot(p), b.GetProperty("s").GetInt32());
            Assert.Equal(t[PlinkoCore.Slot(p)], b.GetProperty("x").GetInt32());
        }
        Assert.NotEqual(before, k.Hash);   // наступний кидок — нова жменя
    }

    [Fact]
    public void A_stale_hash_is_refused_and_nothing_is_charged()
    {
        var k = new PlinkoKit();
        var r = k.Drop(hash: new string('a', 64));
        Assert.False(r.Ok);
        Assert.Equal(Plinko.StaleText, r.Message);
        Assert.Empty(k.Stakes.Ledger);
        // без відбитка — теж можна (скажімо, бот чи старий клієнт): кидок іде під той, що показано
        var shown = k.Hash;
        Assert.True(k.H.Act(0, "drop", new { bet = 10 }).Ok);
        Assert.Equal(shown, k.Last.GetProperty("hash").GetString());
    }

    [Fact]
    public void Illegal_drops_change_nothing()
    {
        var k = new PlinkoKit(wallet: 30);
        var before = k.View.ToString();
        Assert.False(k.Drop(bet: 15).Ok);                    // не кратна 10 і нема в наборі
        Assert.False(k.Drop(bet: 5).Ok);
        Assert.False(k.H.Act(0, "drop", new { n = 1 }).Ok);  // без ставки
        Assert.False(k.Drop(rows: 7).Ok);
        Assert.False(k.Drop(rows: 17).Ok);
        Assert.False(k.Drop(risk: "wild").Ok);
        Assert.False(k.Drop(n: 0).Ok);
        Assert.False(k.Drop(n: 11).Ok);
        var poor = k.Drop(bet: 10, n: 5);                     // 50 > 30
        Assert.False(poor.Ok);
        Assert.Contains("Бракує черепків", poor.Message);
        Assert.False(k.H.Act(0, "dance").Ok);
        Assert.Empty(k.Stakes.Ledger);
        Assert.Equal(before, k.View.ToString());
    }

    [Fact]
    public void Config_switches_the_board_off_and_limits_bets_and_balls()
    {
        var off = new PlinkoKit(opts: o => o.Enabled = false);
        Assert.False(off.View.GetProperty("on").GetBoolean());
        var r = off.Drop();
        Assert.False(r.Ok);
        Assert.Equal(Plinko.OffText, r.Message);

        var k = new PlinkoKit(opts: o => { o.Bets = [15, 25, 30, 1000]; o.MaxBet = 500; o.MaxBalls = 3; });
        Assert.Equal([30], k.View.GetProperty("bets").EnumerateArray().Select(x => x.GetInt32()));   // 15, 25 — не кратні 10; 1000 > стелі
        Assert.Equal(3, k.View.GetProperty("maxBalls").GetInt32());
        Assert.False(k.Drop(bet: 30, n: 5).Ok);
        Assert.True(k.Drop(bet: 30, n: 3).Ok);
        Assert.Equal(3, k.View.GetProperty("balls").GetInt32());
    }

    [Fact]
    public void An_edge_ball_on_16_rows_pays_a_thousand_and_is_bragged_about()
    {
        var k = new PlinkoKit(wallet: 100);
        k.Rig(p => p == new string('1', 16), 16);
        Assert.True(k.Drop(bet: 10, n: 1, rows: 16, risk: PlinkoCore.High).Ok);
        var ball = k.Last.GetProperty("balls")[0];
        Assert.Equal(16, ball.GetProperty("s").GetInt32());
        Assert.Equal(10_000, ball.GetProperty("x").GetInt32());
        Assert.Equal(10_000, ball.GetProperty("win").GetInt32());
        Assert.Equal(100 - 10 + 10_000, k.Stakes.Balance("Оля"));
        Assert.Contains(k.H.Awards, a => a.Reason == "ach:plinko-100");
        Assert.Contains(k.H.Awards, a => a.Reason == "ach:plinko-edge");
        var chat = Assert.Single(k.Wire.Chats);
        Assert.Contains("Гороховій дошці", chat);
        Assert.Contains("×1000", chat);
        var w = Assert.Single(k.Bank.Week());
        Assert.Equal(("plinko", 1000.0, 10_000), (w.Game, w.Mult, w.Win));
        Assert.Equal(1000.0, Assert.Single(k.H.Scores).Score);
        Assert.Equal(1000.0, k.View.GetProperty("best").GetDouble());

        // друга така сама — ачівки вже є, просити вдруге не треба
        k.Rig(p => p == new string('0', 16), 16);
        Assert.True(k.Drop(bet: 10, n: 1, rows: 16, risk: PlinkoCore.High).Ok);
        Assert.Equal(2, k.H.Awards.Count);
    }

    [Fact]
    public void Small_wins_stay_out_of_the_week_and_the_chat()
    {
        var k = new PlinkoKit(wallet: 100_000);
        for (var i = 0; i < 30; i++) Assert.True(k.Drop(bet: 10, n: 10, rows: 8, risk: PlinkoCore.Low).Ok);   // край — ×5
        Assert.Empty(k.Bank.Week());
        Assert.Empty(k.Wire.Chats);
        Assert.DoesNotContain(k.H.Awards, a => a.Reason.StartsWith("ach:plinko", StringComparison.Ordinal));
    }

    [Fact]
    public void Save_and_load_keep_the_board_but_not_the_seed()
    {
        var k = new PlinkoKit(wallet: 100_000);
        for (var i = 0; i < 4; i++) Assert.True(k.Drop(bet: 50, n: 3, rows: 14, risk: PlinkoCore.Mid).Ok);
        var json = k.G.Save()!;
        Assert.DoesNotContain(k.G.State.NextSeed, json);   // таємниця наступного кидка не лягає в базу
        var other = new Plinko();
        other.Load(json);
        Assert.Equal(4, other.State.Seq);
        Assert.Equal((50, 14, PlinkoCore.Mid, 3), (other.State.Bet, other.State.Rows, other.State.Risk, other.State.Balls));
        Assert.Equal(4, other.State.Recent.Count);
        Assert.Equal(12, other.State.Drops);
        Assert.Equal(k.G.State.Recent[0].Balls, other.State.Recent[0].Balls);
        Assert.NotEqual(k.G.State.NextHash, other.State.NextHash);
        Assert.NotEqual(k.G.State.Epoch, other.State.Epoch);
        Assert.Equal(json, other.Save());

        var odd = new Plinko();
        odd.Load("""{"seq":3,"rows":40,"risk":"wild","balls":99}""");
        Assert.Equal((12, PlinkoCore.Low, PlinkoOptions.BallsCap), (odd.State.Rows, odd.State.Risk, odd.State.Balls));
        Assert.Empty(odd.State.Recent);
    }

    [Fact]
    public void A_drop_cut_between_bet_and_win_is_paid_once_by_the_bank()
    {
        var k = new PlinkoKit(wallet: 1_000);
        var fail = true;
        k.Stakes.OnGrant = () =>
        {
            if (!fail) return;
            fail = false;
            throw new TimeoutException("база не відповіла");
        };
        Assert.True(k.Drop(bet: 10, n: 4, rows: 12, risk: PlinkoCore.Low).Ok);   // на низькому кожна горошина хоч щось платить
        var win = k.Last.GetProperty("win").GetInt32();
        Assert.True(win > 0);
        Assert.Equal(1_000 - 40, k.Stakes.Balance("Оля"));                        // списано, виграш завис
        var pending = Assert.Single(k.Bank.Pending());
        Assert.Equal("plinko", pending.Kind);

        k.Clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(1, k.Bank.Recover(k.Clock.UtcNow));
        Assert.Equal(1_000 - 40 + win, k.Stakes.Balance("Оля"));
        Assert.Equal("plinko-win:plinko", k.Stakes.Reasons[pending.Ref + ":win"]);
        Assert.Empty(k.Bank.Pending());
        k.Bank.Recover(k.Clock.UtcNow.AddMinutes(5));
        Assert.Equal(1_000 - 40 + win, k.Stakes.Balance("Оля"));
    }

    [Fact]
    public void Old_bank_records_without_a_kind_still_pay_as_slots()
    {
        var old = JsonSerializer.Deserialize<SlotPending>(
            """{"ref":"slot:r-e:1","nick":"Оля","gameId":"slot-glek","bet":10,"win":40,"jackpot":0,"at":"2026-10-20T10:00:00+00:00"}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("slot-win:slot-glek", old.WinReason);
        Assert.Equal("slot-win:slot-glek", new SlotPending("x", "Оля", "slot-glek", 10, 40, 0, DateTimeOffset.UnixEpoch).WinReason);
        Assert.Equal("plinko-win:plinko", new SlotPending("x", "Оля", "plinko", 10, 40, 0, DateTimeOffset.UnixEpoch, Plinko.Kind).WinReason);
    }
}
